#!/usr/bin/env python3
"""以公共 RPC 和 faux context 断言黑盒验证 Session 打开、恢复与续接。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
from pathlib import Path
import shutil
import sys
import tempfile
from typing import Any, Sequence

import runner
from schema_validation import validate_protocol_trace
import session_validation


JSONObject = dict[str, Any]


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    """功能：解析 fixture、Schema、工作根和一个或多个 daemon 命令模板。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(
        description="验证 portable Session fixture，或驱动一个/多个实现依次续接"
    )
    parser.add_argument(
        "--case-dir",
        type=Path,
        default=root / "CONFORMANCE/fixtures/session/portable-v0.1",
    )
    parser.add_argument("--schema-root", type=Path, default=root / "SPEC/schemas")
    parser.add_argument(
        "--work-root",
        type=Path,
        help="保留黑盒状态的空目录；省略时使用并清理临时目录",
    )
    parser.add_argument(
        "--daemon-command-json",
        action="append",
        default=[],
        help=(
            "JSON argv 数组，可重复以测试跨实现 handoff；支持 {workspace}、"
            "{stateRoot}、{sessionsRoot}、{sessionId}、{repoRoot} 占位符"
        ),
    )
    parser.add_argument("--timeout", type=float, default=10.0)
    return parser.parse_args(argv)


def parse_command_template(raw: str) -> list[str]:
    """功能：严格解析不经过 shell 的 daemon JSON argv 模板。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        value = runner.strict_json_loads(raw)
    except runner.ProtocolViolation as exc:
        raise session_validation.SessionValidationError(
            f"invalid --daemon-command-json: {exc}"
        ) from exc
    if (
        not isinstance(value, list)
        or not value
        or not all(isinstance(item, str) and item for item in value)
    ):
        raise session_validation.SessionValidationError(
            "--daemon-command-json must be a non-empty JSON string array"
        )
    return value


def render_command(template: list[str], replacements: dict[str, str]) -> list[str]:
    """功能：只替换已知路径和 Session 占位符，保持 argv 边界不变。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    rendered: list[str] = []
    for item in template:
        value = item
        for name, replacement in replacements.items():
            value = value.replace("{" + name + "}", replacement)
        rendered.append(value)
    return rendered


def load_expectations(case_dir: Path) -> JSONObject:
    """功能：严格读取 Session case 的机器可读期望对象。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return session_validation.require_object(
        runner.strict_json_loads(
            (case_dir / "expectations.json").read_text(encoding="utf-8")
        ),
        "expectations",
    )


def build_requests(
    session_id: str,
    stage: int,
    after_seq: int,
    expected_context: list[JSONObject],
    input_message: JSONObject,
    response_text: str,
) -> list[JSONObject]:
    """功能：构造 session/get、context assertion 和 continuation 的有序 RPC 请求。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    prefix = f"session-stage-{stage + 1}"
    scenario_name = f"session-handoff-{stage + 1}"
    return [
        {
            "jsonrpc": "2.0",
            "id": prefix + "-initialize",
            "method": "initialize",
            "params": {
                "protocolVersions": ["0.1"],
                "client": {"name": "session-conformance", "version": "0.1.0"},
                "capabilities": {"eventReplay": True},
            },
        },
        {
            "jsonrpc": "2.0",
            "id": prefix + "-get",
            "method": "session/get",
            "params": {"sessionId": session_id, "afterSeq": after_seq},
        },
        {
            "jsonrpc": "2.0",
            "id": prefix + "-configure",
            "method": "faux/configure",
            "params": {
                "sessionId": session_id,
                "scenario": {
                    "schemaVersion": "0.1",
                    "name": scenario_name,
                    "seed": 20260710 + stage,
                    "expectedContext": expected_context,
                    "steps": [{"type": "text", "text": response_text}],
                    "usage": {
                        "inputTokens": 0,
                        "outputTokens": 0,
                        "totalTokens": 0,
                    },
                },
            },
        },
        {
            "jsonrpc": "2.0",
            "id": prefix + "-start",
            "method": "run/start",
            "params": {
                "sessionId": session_id,
                "input": input_message,
                "provider": {"id": "faux", "modelId": "faux-v1"},
            },
        },
    ]


def response_result(
    frames: list[JSONObject], request_id: str, context: str
) -> JSONObject:
    """功能：从 daemon 帧中取得指定成功响应对象并拒绝缺失或错误响应。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    matches = [frame for frame in frames if frame.get("id") == request_id]
    if len(matches) != 1 or "result" not in matches[0]:
        raise session_validation.SessionValidationError(
            f"{context} did not return exactly one success response"
        )
    return session_validation.require_object(matches[0]["result"], context)


def validate_snapshot(
    result: JSONObject,
    session_id: str,
    expected_messages: list[JSONObject],
    expected_latest_seq: int,
    after_seq: int,
) -> None:
    """功能：验证 session/get 的完整消息快照和 event-sequence replay 语义。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if result.get("sessionId") != session_id:
        raise session_validation.SessionValidationError(
            "session/get returned another sessionId"
        )
    if result.get("activeRunId") is not None:
        raise session_validation.SessionValidationError(
            "session/get returned an active run after recovery"
        )
    if result.get("latestSeq") != expected_latest_seq:
        raise session_validation.SessionValidationError(
            "session/get latestSeq is not the durable event sequence"
        )
    raw_messages = session_validation.require_array(
        result.get("messages"), "session/get.messages"
    )
    projected = [
        session_validation.project_message(
            session_validation.require_object(item, "session/get message")
        )
        for item in raw_messages
    ]
    if projected != expected_messages:
        raise session_validation.SessionValidationError(
            "session/get did not recover the expected message context"
        )
    raw_events = session_validation.require_array(
        result.get("events"), "session/get.events"
    )
    event_seqs = [
        session_validation.require_object(item, "session/get event").get("seq")
        for item in raw_events
    ]
    expected_seqs = list(range(after_seq + 1, expected_latest_seq + 1))
    if event_seqs != expected_seqs:
        raise session_validation.SessionValidationError(
            "session/get afterSeq did not return the expected event seq range"
        )


def validate_actual_journal(
    journal_path: Path,
    validator: Any,
    byte_prefix: bytes,
    expected_messages_before: list[JSONObject],
    input_message: JSONObject,
    response_text: str,
    *,
    require_recovery_prefix: bool,
) -> tuple[bytes, session_validation.SessionState]:
    """功能：验证实现只追加合法记录、未重放工具且保存本阶段输入输出消息。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    raw, values = session_validation.load_journal(journal_path, validator)
    if not raw.startswith(byte_prefix):
        raise session_validation.SessionValidationError(
            "implementation rewrote the imported journal prefix"
        )
    state = session_validation.reconstruct_state(values)
    if state.unfinished_run_ids:
        raise session_validation.SessionValidationError(
            "implementation left an unfinished run after continuation"
        )
    if state.unknown_outcome_tool_call_ids != ["tool-call-side-effect-1"]:
        raise session_validation.SessionValidationError(
            "implementation lost or duplicated the unknown tool outcome"
        )
    intents = [
        record
        for record in state.records
        if record.get("kind") == "tool.intent"
        and record["data"].get("toolCallId") == "tool-call-side-effect-1"
    ]
    if len(intents) != 1:
        raise session_validation.SessionValidationError(
            "implementation replayed the non-idempotent tool intent"
        )
    if require_recovery_prefix:
        base_record_count = 21
        recovered_kinds = [
            record["kind"]
            for record in state.records[base_record_count : base_record_count + 4]
        ]
        if recovered_kinds != [
            "tool.result",
            "message.appended",
            "run.terminal",
            "event.emitted",
        ]:
            raise session_validation.SessionValidationError(
                "implementation did not append canonical recovery records before continuation"
            )
    expected_tail = [
        session_validation.project_message(input_message),
        {"role": "assistant", "content": [{"type": "text", "text": response_text}]},
    ]
    if state.messages[: len(expected_messages_before)] != expected_messages_before:
        raise session_validation.SessionValidationError(
            "implementation changed the pre-stage message context"
        )
    if state.messages[-2:] != expected_tail:
        raise session_validation.SessionValidationError(
            "implementation did not persist the continuation input/output messages"
        )
    return raw, state


def run_stage(
    command: list[str],
    stage: int,
    session_id: str,
    sessions_root: Path,
    journal_path: Path,
    schema_root: Path,
    validator: Any,
    byte_prefix: bytes,
    state_before: session_validation.SessionState,
    input_message: JSONObject,
    response_text: str,
    timeout: float,
) -> tuple[bytes, session_validation.SessionState]:
    """功能：驱动一个 daemon 完成恢复快照、context 断言和一次续接。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    after_seq = 4 if stage == 0 else session_validation.latest_event_seq(state_before)
    expected_context = [
        *state_before.messages,
        session_validation.project_message(input_message),
    ]
    requests = build_requests(
        session_id,
        stage,
        after_seq,
        expected_context,
        input_message,
        response_text,
    )
    process = runner.DaemonProcess(
        command,
        timeout=timeout,
        max_frame_bytes=1_048_576,
        extra_env={
            "QXNM_FORGE_SESSION_ROOT": str(sessions_root),
            "QXNM_FORGE_LIVE_TESTS": "0",
            "QXNM_FORGE_ALLOW_LIVE_PROVIDER_TESTS": "0",
        },
    )
    frames = process.run(requests, settle_seconds=0.05)
    runner.TraceValidator(requests).validate(frames)
    validate_protocol_trace(requests, frames, schema_root)
    get_id = f"session-stage-{stage + 1}-get"
    snapshot = response_result(frames, get_id, "session/get result")
    validate_snapshot(
        snapshot,
        session_id,
        state_before.messages,
        session_validation.latest_event_seq(state_before),
        after_seq,
    )
    return validate_actual_journal(
        journal_path,
        validator,
        byte_prefix,
        state_before.messages,
        input_message,
        response_text,
        require_recovery_prefix=stage == 0,
    )


def prepare_fixture_root(
    root: Path, case_dir: Path, session_id: str
) -> tuple[Path, Path, Path]:
    """功能：在空测试根中按 canonical layout 复制恢复前 journal。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    state_root = root / "state"
    sessions_root = state_root / "sessions"
    session_dir = sessions_root / session_id
    if session_dir.exists():
        raise session_validation.SessionValidationError(
            f"refusing to overwrite existing probe session: {session_dir}"
        )
    workspace = root / "workspace"
    workspace.mkdir(parents=True, exist_ok=True)
    (session_dir / "artifacts").mkdir(parents=True, exist_ok=False)
    shutil.copyfile(
        case_dir / "journal.before-recovery.jsonl", session_dir / "journal.jsonl"
    )
    return state_root, sessions_root, workspace


def run_probe(args: argparse.Namespace, work_root: Path) -> int:
    """功能：让一个或多个 daemon 顺序打开同一 journal 并验证跨实现 handoff。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    case_dir = args.case_dir.resolve()
    schema_root = args.schema_root.resolve()
    _, recovered = session_validation.validate_case(case_dir, schema_root)
    expectations = load_expectations(case_dir)
    session_id = session_validation.require_string(
        expectations.get("sessionId"), "expectations.sessionId"
    )
    state_root, sessions_root, workspace = prepare_fixture_root(
        work_root, case_dir, session_id
    )
    journal_path = sessions_root / session_id / "journal.jsonl"
    validator = session_validation.build_line_validator(schema_root)
    byte_prefix = journal_path.read_bytes()
    state_before = recovered
    continuation = session_validation.require_object(
        expectations.get("continuation"), "expectations.continuation"
    )

    for stage, raw_template in enumerate(args.daemon_command_json):
        template = parse_command_template(raw_template)
        command = render_command(
            template,
            {
                "workspace": str(workspace),
                "stateRoot": str(state_root),
                "sessionsRoot": str(sessions_root),
                "sessionId": session_id,
                "repoRoot": str(Path(__file__).resolve().parents[1]),
            },
        )
        if stage == 0:
            input_message = session_validation.require_object(
                continuation.get("input"), "continuation.input"
            )
            response_text = session_validation.require_string(
                continuation.get("responseText"), "continuation.responseText"
            )
        else:
            input_message = {
                "role": "user",
                "content": [
                    {"type": "text", "text": f"Confirm handoff stage {stage + 1}."}
                ],
            }
            response_text = f"Handoff stage {stage + 1} retained the recovered history."
        byte_prefix, state_before = run_stage(
            command,
            stage,
            session_id,
            sessions_root,
            journal_path,
            schema_root,
            validator,
            byte_prefix,
            state_before,
            input_message,
            response_text,
            args.timeout,
        )
        print(
            f"SESSION STAGE {stage + 1} PASS: command={command[0]!r}, "
            f"messages={len(state_before.messages)}, "
            f"latestEventSeq={session_validation.latest_event_seq(state_before)}"
        )
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    """功能：验证静态 fixture，或执行可选的多实现黑盒 Session handoff。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = parse_args(argv)
    try:
        before, after = session_validation.validate_case(
            args.case_dir.resolve(), args.schema_root.resolve()
        )
        print(
            "SESSION FIXTURE PASS: "
            f"before={len(before.records)}, after={len(after.records)}, "
            f"latestEventSeq={session_validation.latest_event_seq(after)}"
        )
        if not args.daemon_command_json:
            return 0
        if args.work_root is not None:
            args.work_root.mkdir(parents=True, exist_ok=True)
            return run_probe(args, args.work_root.resolve())
        with tempfile.TemporaryDirectory(prefix="qxnm-forge-session-conformance-") as temp:
            return run_probe(args, Path(temp))
    except (
        OSError,
        ValueError,
        runner.ConformanceError,
        session_validation.SessionValidationError,
    ) as exc:
        print(f"SESSION BLACK-BOX FAIL: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
