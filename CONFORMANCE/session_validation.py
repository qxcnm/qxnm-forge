#!/usr/bin/env python3
"""验证 portable Session journal fixture 的 Schema 与恢复语义。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from pathlib import Path
import sys
from typing import Any

from runner import ProtocolViolation, strict_json_loads


JSONObject = dict[str, Any]


class SessionValidationError(Exception):
    """表示 journal 结构、生命周期、恢复或 fixture 期望不一致。"""


@dataclass(frozen=True)
class SessionState:
    """保存从完整 journal 推导出的语言中立 Session 状态。"""

    header: JSONObject
    records: list[JSONObject]
    messages: list[JSONObject]
    events: list[JSONObject]
    unfinished_run_ids: list[str]
    interrupted_run_ids: list[str]
    unresolved_non_idempotent_tool_call_ids: list[str]
    unknown_outcome_tool_call_ids: list[str]


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    """功能：解析 Session fixture 目录与公共 Schema 根目录。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(
        description="逐行验证 portable Session fixture 及跨语言恢复语义"
    )
    parser.add_argument(
        "--case-dir",
        type=Path,
        default=root / "CONFORMANCE/fixtures/session/portable-v0.1",
    )
    parser.add_argument(
        "--schema-root",
        type=Path,
        default=root / "SPEC/schemas",
    )
    return parser.parse_args(argv)


def require_object(value: Any, context: str) -> JSONObject:
    """功能：要求值为 JSON 对象并返回带类型信息的对象。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, dict):
        raise SessionValidationError(f"{context} must be an object")
    return value


def require_array(value: Any, context: str) -> list[Any]:
    """功能：要求值为 JSON 数组并返回带类型信息的数组。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, list):
        raise SessionValidationError(f"{context} must be an array")
    return value


def require_string(value: Any, context: str) -> str:
    """功能：要求值为非空字符串并返回该字符串。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, str) or not value:
        raise SessionValidationError(f"{context} must be a non-empty string")
    return value


def build_line_validator(schema_root: Path) -> Any:
    """功能：加载全部 Draft 2020-12 Schema 并创建 journal 行验证器。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        import jsonschema
        from referencing import Registry, Resource
    except ImportError as exc:
        raise SessionValidationError(
            "session Schema validation requires jsonschema and referencing"
        ) from exc

    resources: list[tuple[str, Any]] = []
    schemas: dict[str, JSONObject] = {}
    for path in sorted(schema_root.rglob("*.schema.json")):
        schema = require_object(
            strict_json_loads(path.read_text(encoding="utf-8")), str(path)
        )
        try:
            jsonschema.Draft202012Validator.check_schema(schema)
            schema_id = require_string(schema.get("$id"), f"{path}.$id")
            resource = Resource.from_contents(schema)
        except (jsonschema.SchemaError, ValueError) as exc:
            raise SessionValidationError(f"invalid Schema {path}: {exc}") from exc
        schemas[str(path.relative_to(schema_root))] = schema
        resources.append((schema_id, resource))
    schema_name = "session/journal.schema.json"
    if schema_name not in schemas:
        raise SessionValidationError(f"missing {schema_root / schema_name}")
    return jsonschema.Draft202012Validator(
        schemas[schema_name], registry=Registry().with_resources(resources)
    )


def load_journal(path: Path, validator: Any) -> tuple[bytes, list[JSONObject]]:
    """功能：严格读取 UTF-8 LF journal，并逐行执行公共 Schema 验证。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        raw = path.read_bytes()
    except OSError as exc:
        raise SessionValidationError(f"cannot read {path}: {exc}") from exc
    if raw.startswith(b"\xef\xbb\xbf"):
        raise SessionValidationError(f"{path} must not contain a UTF-8 BOM")
    if not raw.endswith(b"\n"):
        raise SessionValidationError(f"{path} must end every committed line with LF")
    try:
        text = raw.decode("utf-8", errors="strict")
    except UnicodeDecodeError as exc:
        raise SessionValidationError(f"{path} is not strict UTF-8") from exc
    lines = text.splitlines()
    if not lines or any(not line for line in lines):
        raise SessionValidationError(f"{path} contains no lines or a blank line")
    values: list[JSONObject] = []
    for line_number, line in enumerate(lines, start=1):
        try:
            value = require_object(strict_json_loads(line), f"{path}:{line_number}")
        except Exception as exc:
            if isinstance(exc, SessionValidationError):
                raise
            raise SessionValidationError(f"{path}:{line_number}: {exc}") from exc
        errors = sorted(
            validator.iter_errors(value),
            key=lambda item: (
                tuple(str(part) for part in item.absolute_path),
                item.message,
            ),
        )
        if errors:
            location = "/".join(str(part) for part in errors[0].absolute_path)
            raise SessionValidationError(
                f"{path}:{line_number} Schema violation at "
                f"{location or '<root>'}: {errors[0].message}"
            )
        values.append(value)
    return raw, values


def project_message(message: JSONObject) -> JSONObject:
    """功能：把 durable message 投影为 faux expectedContext 的比较形状。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    role = require_string(message.get("role"), "message.role")
    projected: JSONObject = {
        "role": role,
        "content": require_array(message.get("content"), "message.content"),
    }
    if role == "tool":
        projected["toolCallId"] = require_string(
            message.get("toolCallId"), "tool message.toolCallId"
        )
        projected["toolName"] = require_string(
            message.get("toolName"), "tool message.toolName"
        )
        if not isinstance(message.get("isError"), bool):
            raise SessionValidationError("tool message.isError must be boolean")
        projected["isError"] = message["isError"]
    return projected


def reconstruct_state(values: list[JSONObject]) -> SessionState:
    """功能：验证跨记录不变量并重建消息、事件、run 与工具恢复状态。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not values:
        raise SessionValidationError("journal is empty")
    header = values[0]
    if header.get("kind") != "session":
        raise SessionValidationError("journal first line must be the session header")
    session_id = require_string(header.get("sessionId"), "header.sessionId")
    records = values[1:]
    prior_ids: set[str] = set()
    message_ids: set[str] = set()
    messages: list[JSONObject] = []
    events: list[JSONObject] = []
    accepted_order: list[str] = []
    terminal_status: dict[str, str] = {}
    tool_intents: dict[str, JSONObject] = {}
    tool_results: dict[str, list[JSONObject]] = {}
    last_event_seq = 0

    for expected_seq, record in enumerate(records, start=1):
        if record.get("seq") != expected_seq:
            raise SessionValidationError(
                f"journal seq must be contiguous: expected {expected_seq}"
            )
        if record.get("sessionId") != session_id:
            raise SessionValidationError("record sessionId differs from header")
        record_id = require_string(record.get("recordId"), "record.recordId")
        if record_id in prior_ids:
            raise SessionValidationError(f"duplicate recordId {record_id!r}")
        parent_id = record.get("parentId")
        if parent_id is not None and parent_id not in prior_ids:
            raise SessionValidationError(
                f"parentId {parent_id!r} does not reference an earlier record"
            )
        prior_ids.add(record_id)
        kind = require_string(record.get("kind"), "record.kind")
        data = require_object(record.get("data"), f"{kind}.data")

        if kind == "message.appended":
            message = require_object(data.get("message"), "message.appended.message")
            message_id = require_string(message.get("messageId"), "message.messageId")
            if message_id in message_ids:
                raise SessionValidationError(f"duplicate messageId {message_id!r}")
            message_ids.add(message_id)
            messages.append(project_message(message))
        elif kind == "run.accepted":
            run_id = require_string(data.get("runId"), "run.accepted.runId")
            if run_id in accepted_order:
                raise SessionValidationError(f"duplicate accepted run {run_id!r}")
            input_message_id = require_string(
                data.get("inputMessageId"), "run.accepted.inputMessageId"
            )
            if input_message_id not in message_ids:
                raise SessionValidationError(
                    f"run {run_id!r} references a non-durable input message"
                )
            accepted_order.append(run_id)
        elif kind == "run.terminal":
            run_id = require_string(data.get("runId"), "run.terminal.runId")
            if run_id not in accepted_order:
                raise SessionValidationError(f"terminal for unaccepted run {run_id!r}")
            if run_id in terminal_status:
                raise SessionValidationError(f"duplicate terminal for run {run_id!r}")
            terminal_status[run_id] = require_string(
                data.get("status"), "run.terminal.status"
            )
        elif kind == "tool.intent":
            tool_call_id = require_string(
                data.get("toolCallId"), "tool.intent.toolCallId"
            )
            if tool_call_id in tool_intents:
                raise SessionValidationError(
                    f"fixture contains duplicate tool intent {tool_call_id!r}"
                )
            tool_intents[tool_call_id] = data
        elif kind == "tool.result":
            tool_call_id = require_string(
                data.get("toolCallId"), "tool.result.toolCallId"
            )
            if tool_call_id not in tool_intents:
                raise SessionValidationError(
                    f"tool result references missing intent {tool_call_id!r}"
                )
            tool_results.setdefault(tool_call_id, []).append(data)
        elif kind == "event.emitted":
            event = require_object(data.get("event"), "event.emitted.event")
            event_seq = event.get("seq")
            if not isinstance(event_seq, int) or isinstance(event_seq, bool):
                raise SessionValidationError("event seq must be an integer")
            if event_seq <= last_event_seq:
                raise SessionValidationError("event seq must be strictly increasing")
            if event.get("sessionId") != session_id:
                raise SessionValidationError("event sessionId differs from header")
            last_event_seq = event_seq
            events.append(event)

    unfinished = [item for item in accepted_order if item not in terminal_status]
    interrupted = [
        item for item in accepted_order if terminal_status.get(item) == "interrupted"
    ]
    unresolved_non_idempotent = sorted(
        tool_call_id
        for tool_call_id, intent in tool_intents.items()
        if intent.get("idempotent") is False and tool_call_id not in tool_results
    )
    unknown_outcomes = sorted(
        tool_call_id
        for tool_call_id, results in tool_results.items()
        if any(result.get("outcomeKnown") is False for result in results)
    )
    return SessionState(
        header=header,
        records=records,
        messages=messages,
        events=events,
        unfinished_run_ids=unfinished,
        interrupted_run_ids=interrupted,
        unresolved_non_idempotent_tool_call_ids=unresolved_non_idempotent,
        unknown_outcome_tool_call_ids=unknown_outcomes,
    )


def event_seqs_after(state: SessionState, after_seq: int) -> list[int]:
    """功能：按 session/get 语义返回严格大于 afterSeq 的事件序号。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return [int(event["seq"]) for event in state.events if event["seq"] > after_seq]


def latest_event_seq(state: SessionState) -> int:
    """功能：返回 durable event 最大序号而非 journal record 序号。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return max((int(event["seq"]) for event in state.events), default=0)


def require_expected_list(section: JSONObject, field: str) -> list[Any]:
    """功能：读取 expectations 小节中的必需数组字段。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return require_array(section.get(field), f"expectations.{field}")


def validate_state_expectations(
    state: SessionState,
    section: JSONObject,
    *,
    recovered: bool,
) -> None:
    """功能：比较重建状态与 fixture 中恢复前或恢复后的确定性期望。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    expected_latest = section.get("latestEventSeq")
    if latest_event_seq(state) != expected_latest:
        raise SessionValidationError(
            f"latest event seq mismatch: {latest_event_seq(state)} != {expected_latest}"
        )
    after_seq = section.get("afterSeq")
    if not isinstance(after_seq, int) or isinstance(after_seq, bool):
        raise SessionValidationError("expectations.afterSeq must be an integer")
    expected_event_seqs = require_expected_list(section, "returnedEventSeqs")
    if event_seqs_after(state, after_seq) != expected_event_seqs:
        raise SessionValidationError("session/get afterSeq event projection mismatch")
    expected_context = require_expected_list(section, "messageContext")
    if state.messages != expected_context:
        raise SessionValidationError("reconstructed message context mismatch")

    if recovered:
        if state.unfinished_run_ids:
            raise SessionValidationError("recovered journal still has unfinished runs")
        if state.interrupted_run_ids != require_expected_list(
            section, "interruptedRunIds"
        ):
            raise SessionValidationError("interrupted run set mismatch")
        if state.unknown_outcome_tool_call_ids != require_expected_list(
            section, "unknownOutcomeToolCallIds"
        ):
            raise SessionValidationError("unknown-outcome tool set mismatch")
    else:
        if state.unfinished_run_ids != require_expected_list(
            section, "unfinishedRunIds"
        ):
            raise SessionValidationError("unfinished run set mismatch")
        if state.unresolved_non_idempotent_tool_call_ids != require_expected_list(
            section, "ambiguousNonIdempotentToolCallIds"
        ):
            raise SessionValidationError("ambiguous non-idempotent tool set mismatch")


def validate_case(
    case_dir: Path, schema_root: Path
) -> tuple[SessionState, SessionState]:
    """功能：验证公共恢复前后 fixture、append-only 前缀与 continuation 上下文。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    validator = build_line_validator(schema_root)
    before_raw, before_values = load_journal(
        case_dir / "journal.before-recovery.jsonl", validator
    )
    after_raw, after_values = load_journal(
        case_dir / "journal.after-recovery.jsonl", validator
    )
    if not after_raw.startswith(before_raw):
        raise SessionValidationError("recovery rewrote bytes instead of appending")
    before_state = reconstruct_state(before_values)
    after_state = reconstruct_state(after_values)

    expectations = require_object(
        strict_json_loads((case_dir / "expectations.json").read_text(encoding="utf-8")),
        "expectations",
    )
    if expectations.get("schemaVersion") != "0.1":
        raise SessionValidationError("unsupported expectations schemaVersion")
    if expectations.get("sessionId") != before_state.header.get("sessionId"):
        raise SessionValidationError("expectations sessionId differs from journal")
    before_section = require_object(
        expectations.get("beforeRecovery"), "expectations.beforeRecovery"
    )
    after_section = require_object(
        expectations.get("afterRecovery"), "expectations.afterRecovery"
    )
    validate_state_expectations(before_state, before_section, recovered=False)
    validate_state_expectations(after_state, after_section, recovered=True)

    appended = after_state.records[len(before_state.records) :]
    if [record["kind"] for record in appended] != [
        "tool.result",
        "message.appended",
        "run.terminal",
        "event.emitted",
    ]:
        raise SessionValidationError("recovery append order is not canonical")
    ambiguous_ids = require_expected_list(
        before_section, "ambiguousNonIdempotentToolCallIds"
    )
    replayed_ids = [
        record["data"].get("toolCallId")
        for record in appended
        if record.get("kind") == "tool.intent"
        and record["data"].get("toolCallId") in ambiguous_ids
    ]
    if replayed_ids:
        raise SessionValidationError("recovery replayed a non-idempotent tool intent")
    if require_expected_list(after_section, "automaticReplayToolCallIds"):
        raise SessionValidationError("fixture must require no automatic tool replay")

    scenario = require_object(
        strict_json_loads(
            (case_dir / "continuation.scenario.json").read_text(encoding="utf-8")
        ),
        "continuation scenario",
    )
    continuation = require_object(
        expectations.get("continuation"), "expectations.continuation"
    )
    expected_context = require_expected_list(continuation, "expectedContext")
    if scenario.get("expectedContext") != expected_context:
        raise SessionValidationError(
            "scenario expectedContext differs from expectations"
        )
    input_message = require_object(continuation.get("input"), "continuation.input")
    if expected_context != after_state.messages + [project_message(input_message)]:
        raise SessionValidationError(
            "continuation context must equal recovered messages plus new input"
        )
    return before_state, after_state


def main(argv: list[str] | None = None) -> int:
    """功能：运行 Session fixture 验证并输出可复现摘要与退出码。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = parse_args(argv)
    try:
        before, after = validate_case(
            args.case_dir.resolve(), args.schema_root.resolve()
        )
    except (OSError, ProtocolViolation, SessionValidationError, ValueError) as exc:
        print(f"SESSION FAIL: {exc}", file=sys.stderr)
        return 1
    print(
        "SESSION PASS: "
        f"before={len(before.records)} records/after={len(after.records)} records, "
        f"messages={len(after.messages)}, latestEventSeq={latest_event_seq(after)}, "
        "non-idempotent replay=0"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
