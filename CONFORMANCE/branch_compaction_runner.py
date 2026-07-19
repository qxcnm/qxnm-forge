#!/usr/bin/env python3
"""黑盒验证实现可打开、投影并续接 portable branch/compaction Session。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import shutil
import sys
import tempfile
from typing import Any, Callable, Sequence

import branch_compaction_validation
import provider_runner
import runner
import schema_validation
import session_runner
import session_validation


JSONObject = dict[str, Any]


class BranchCompactionRunnerError(Exception):
    """branch/compaction 黑盒 continuation 断言失败。"""


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    """功能：解析 fixture、Schema、隔离根与一个或多个 daemon 命令模板。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(
        description="验证 portable branch/compaction projection 与跨实现 continuation"
    )
    parser.add_argument(
        "--case-dir",
        type=Path,
        default=root / "CONFORMANCE/fixtures/session/branch-compaction-v0.1",
    )
    parser.add_argument("--schema-root", type=Path, default=root / "SPEC/schemas")
    parser.add_argument(
        "--work-root", type=Path, help="保留状态的空目录；省略时使用并清理临时目录"
    )
    parser.add_argument("--daemon-command-json", action="append", default=[])
    parser.add_argument("--timeout", type=float, default=10.0)
    return parser.parse_args(argv)


def require_object(value: Any, context: str) -> JSONObject:
    """功能：要求动态 JSON 值是字符串键对象并返回。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, dict) or not all(isinstance(key, str) for key in value):
        raise BranchCompactionRunnerError(f"{context} must be a JSON object")
    return value


def prepare_root(
    work_root: Path, case_dir: Path, session_id: str
) -> tuple[Path, Path, Path, Path]:
    """功能：把只读规范 journal 复制到全新 canonical state/sessions 布局。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    workspace = work_root / "workspace"
    state_root = work_root / "state"
    sessions_root = state_root / "sessions"
    session_directory = sessions_root / session_id
    workspace.mkdir(parents=True, exist_ok=False)
    (session_directory / "artifacts").mkdir(parents=True, exist_ok=False)
    journal_path = session_directory / "journal.jsonl"
    shutil.copyfile(case_dir / "journal.jsonl", journal_path)
    return workspace, state_root, sessions_root, journal_path


def initialize_request(request_id: str) -> JSONObject:
    """功能：构造 branch/compaction 黑盒案例共用的 initialize 请求。

    输入：当前隔离案例内唯一的 opaque request ID。
    输出：声明 event replay 能力的 JSON-RPC initialize 对象。
    不变量：协议版本、客户端身份与能力字段保持语言中立。
    失败：本函数只构造内存对象，不执行 I/O。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": request_id,
        "method": "initialize",
        "params": {
            "protocolVersions": ["0.1"],
            "client": {"name": "branch-conformance", "version": "0.1.0"},
            "capabilities": {"eventReplay": True},
        },
    }


def build_requests(
    stage: int,
    session_id: str,
    state: branch_compaction_validation.BranchCompactionState,
    input_message: JSONObject,
    response_text: str,
) -> list[JSONObject]:
    """功能：构造 snapshot、faux context 断言与 continuation 的四个请求。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    prefix = f"branch-stage-{stage + 1}"
    expected_context = [
        *[session_validation.project_message(message) for message in state.messages],
        session_validation.project_message(input_message),
    ]
    return [
        initialize_request(prefix + "-initialize"),
        {
            "jsonrpc": "2.0",
            "id": prefix + "-get",
            "method": "session/get",
            "params": {"sessionId": session_id, "afterSeq": 0},
        },
        {
            "jsonrpc": "2.0",
            "id": prefix + "-configure",
            "method": "faux/configure",
            "params": {
                "sessionId": session_id,
                "scenario": {
                    "schemaVersion": "0.1",
                    "name": prefix,
                    "seed": 20260714 + stage,
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


def response_result(frames: list[JSONObject], request_id: str) -> JSONObject:
    """功能：取得唯一同 ID 成功响应对象并拒绝 error/重复/缺失。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    matches = [frame for frame in frames if frame.get("id") == request_id]
    if len(matches) != 1 or "result" not in matches[0]:
        raise BranchCompactionRunnerError(
            f"request {request_id!r} did not succeed once"
        )
    return require_object(matches[0]["result"], f"response {request_id!r}")


def assert_response_error(
    frames: list[JSONObject], request_id: str, expected_name: str
) -> None:
    """功能：断言 mutation 请求返回机器夹具固定的 portable error 三元组。

    输入：完整响应帧、目标 request ID 与 mutationErrors 中的案例名。
    输出：唯一 error 的 code、retryable、details.kind 精确匹配时正常返回。
    不变量：不解析 message，也不接受用相邻通用错误替代固定映射。
    失败：响应缺失/重复、错误案例未知或任一字段漂移时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    expected = branch_compaction_validation.EXPECTED_MUTATION_ERRORS.get(expected_name)
    if not isinstance(expected, dict):
        raise BranchCompactionRunnerError(
            f"unknown mutation error expectation {expected_name!r}"
        )
    matches = [frame for frame in frames if frame.get("id") == request_id]
    if len(matches) != 1 or "error" not in matches[0]:
        raise BranchCompactionRunnerError(
            f"request {request_id!r} did not fail exactly once"
        )
    error = require_object(matches[0]["error"], f"error {request_id!r}")
    details = require_object(error.get("details"), f"error details {request_id!r}")
    actual = {
        "code": error.get("code"),
        "retryable": error.get("retryable"),
        "kind": details.get("kind"),
    }
    if actual != expected:
        raise BranchCompactionRunnerError(
            f"request {request_id!r} returned {actual!r}, expected {expected!r}"
        )


def validate_snapshot(
    result: JSONObject,
    state: branch_compaction_validation.BranchCompactionState,
) -> None:
    """功能：验证 session/get 返回 exact selected projection 与 head/compaction 身份。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if (
        result.get("sessionId") != state.session_id
        or result.get("activeRunId") is not None
    ):
        raise BranchCompactionRunnerError(
            "session/get identity or activeRunId is invalid"
        )
    if result.get("messages") != list(state.messages):
        raise BranchCompactionRunnerError(
            "session/get did not return exact selected projection"
        )
    if result.get("selectedHeadRecordId") != state.selected_head_record_id:
        raise BranchCompactionRunnerError(
            "session/get selected head is absent or incorrect"
        )
    if result.get("compactionRecordId") != state.compaction_record_id:
        raise BranchCompactionRunnerError(
            "session/get compaction identity is absent or incorrect"
        )


def validate_declared_methods(initialize_result: JSONObject) -> None:
    """功能：要求实现诚实声明 branch select 与 compact 公共 mutation API。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    capabilities = require_object(initialize_result.get("capabilities"), "capabilities")
    methods = capabilities.get("methods")
    if not isinstance(methods, list) or not {
        "session/branch/select",
        "session/compact",
    }.issubset(methods):
        raise BranchCompactionRunnerError(
            "implementation omitted session/branch/select or session/compact"
        )


def render_command(
    raw_template: str,
    workspace: Path,
    state_root: Path,
    sessions_root: Path,
    session_id: str,
) -> list[str]:
    """功能：严格解析 JSON argv 并替换 runner 管理的有限占位符。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    template = session_runner.parse_command_template(raw_template)
    return session_runner.render_command(
        template,
        {
            "workspace": str(workspace),
            "stateRoot": str(state_root),
            "sessionsRoot": str(sessions_root),
            "sessionId": session_id,
            "repoRoot": str(Path(__file__).resolve().parents[1]),
        },
    )


def start_daemon(
    raw_template: str,
    workspace: Path,
    state_root: Path,
    sessions_root: Path,
    session_id: str,
    timeout: float,
) -> runner.DaemonProcess:
    """功能：用隔离路径和清洁凭据环境启动一个 branch conformance daemon。

    输入：安全 argv 模板、隔离目录、Session ID 与正数超时。
    输出：尚未发送请求的严格 NDJSON daemon 包装器。
    不变量：删除 Provider 凭据、endpoint 与 proxy，且显式关闭 live tests。
    失败：模板、环境或进程启动错误由共同 runner 以脱敏诊断抛出。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    command = render_command(
        raw_template, workspace, state_root, sessions_root, session_id
    )
    return runner.DaemonProcess(
        command,
        timeout=timeout,
        max_frame_bytes=1_048_576,
        extra_env={
            "QXNM_FORGE_WORKSPACE": str(workspace),
            "QXNM_FORGE_SESSION_ROOT": str(sessions_root),
            "QXNM_FORGE_CONFORMANCE": "1",
            "QXNM_FORGE_LIVE_TESTS": "0",
            "QXNM_FORGE_ALLOW_LIVE_PROVIDER_TESTS": "0",
        },
        removed_env=(
            *provider_runner.CREDENTIAL_ENV,
            *provider_runner.ENDPOINT_ENV,
            *provider_runner.PROXY_ENV,
        ),
    )


def run_rpc_case(
    raw_template: str,
    case_root: Path,
    case_dir: Path,
    session_id: str,
    schema_root: Path,
    requests: list[JSONObject],
    timeout: float,
    journal_mutator: Callable[[Path], None] | None = None,
) -> tuple[list[JSONObject], Path, bytes]:
    """功能：在独立 fixture 副本上运行一组 mutation RPC 并验证 wire Schema。

    输入：daemon 模板、隔离根、规范 fixture、请求、超时及可选测试变换器。
    输出：协议帧、journal 路径与发送请求前的精确字节快照。
    不变量：每个案例使用新进程和新 Session 副本，绝不修改规范 fixture。
    失败：协议、Schema、进程或临时 journal 变换失败时终止当前语言套件。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    workspace, state_root, sessions_root, journal_path = prepare_root(
        case_root, case_dir, session_id
    )
    if journal_mutator is not None:
        journal_mutator(journal_path)
    byte_prefix = journal_path.read_bytes()
    process = start_daemon(
        raw_template,
        workspace,
        state_root,
        sessions_root,
        session_id,
        timeout,
    )
    frames = process.run(requests, settle_seconds=0.05)
    runner.TraceValidator(requests).validate(frames)
    schema_validation.validate_protocol_trace(requests, frames, schema_root)
    validate_declared_methods(response_result(frames, requests[0]["id"]))
    return frames, journal_path, byte_prefix


def require_unchanged_journal(
    journal_path: Path, byte_prefix: bytes, context: str
) -> None:
    """功能：要求失败 mutation 没有改写或追加 Session journal。

    输入：canonical journal、请求前字节和安全案例标签。
    输出：文件逐字节相同时正常返回。
    不变量：失败路径连诊断或补偿记录也不得写入 journal。
    失败：文件变化或不可读时抛出 branch runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if journal_path.read_bytes() != byte_prefix:
        raise BranchCompactionRunnerError(
            f"{context} changed the journal despite mutation failure"
        )


def append_non_quiescent_history(journal_path: Path) -> None:
    """功能：向临时 fixture 追加已终止 run，并保留历史 non-quiescent target。

    输入：仅属于当前 runner 隔离案例的 journal 路径。
    输出：追加 run.accepted、其后代 run.terminal 与对应 terminal event 三条合法记录。
    不变量：target 指向 accepted 本身时其祖先仍缺 terminal；当前 head 已完整恢复且静止。
    失败：临时文件读取、严格 JSON 解码或追加写入失败时传播异常。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    lines = journal_path.read_text(encoding="utf-8").splitlines()
    last = require_object(runner.strict_json_loads(lines[-1]), "last fixture record")
    session_id = last["sessionId"]
    accepted = {
        "schemaVersion": "0.1",
        "kind": "run.accepted",
        "recordId": "record-non-quiescent-run",
        "sessionId": session_id,
        "seq": int(last["seq"]) + 1,
        "parentId": last["recordId"],
        "time": "2026-07-14T02:00:13Z",
        "data": {
            "runId": "run-non-quiescent-history",
            "inputMessageId": "message-branch-a-user",
            "provider": {"id": "faux", "modelId": "faux-v1"},
        },
    }
    terminal = {
        "schemaVersion": "0.1",
        "kind": "run.terminal",
        "recordId": "record-non-quiescent-terminal",
        "sessionId": session_id,
        "seq": accepted["seq"] + 1,
        "parentId": accepted["recordId"],
        "time": "2026-07-14T02:00:14Z",
        "data": {
            "runId": "run-non-quiescent-history",
            "status": "completed",
            "usage": {"inputTokens": 0, "outputTokens": 0, "totalTokens": 0},
        },
    }
    terminal_event = {
        "schemaVersion": "0.1",
        "kind": "event.emitted",
        "recordId": "record-non-quiescent-event",
        "sessionId": session_id,
        "seq": terminal["seq"] + 1,
        "parentId": terminal["recordId"],
        "time": "2026-07-14T02:00:15Z",
        "data": {
            "event": {
                "sessionId": session_id,
                "runId": "run-non-quiescent-history",
                "seq": 1,
                "time": "2026-07-14T02:00:15Z",
                "type": "run.completed",
                "data": {
                    "status": "completed",
                    "usage": {
                        "inputTokens": 0,
                        "outputTokens": 0,
                        "totalTokens": 0,
                    },
                },
            }
        },
    }
    with journal_path.open("a", encoding="utf-8", newline="\n") as stream:
        for record in (accepted, terminal, terminal_event):
            stream.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")))
            stream.write("\n")


def append_summary_without_compaction(journal_path: Path) -> None:
    """功能：模拟进程在 durable summary 后、context.compacted 前崩溃的 journal。

    输入：仅属于当前隔离案例的规范 fixture 副本。
    输出：追加一条 canonical assistant summary-like message，不追加控制记录。
    不变量：旧 compaction 继续生效，新消息只能作为普通 post-compaction 消息投影。
    失败：临时文件读取、严格 JSON 解码或追加失败时传播异常。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    lines = journal_path.read_text(encoding="utf-8").splitlines()
    last = require_object(runner.strict_json_loads(lines[-1]), "last fixture record")
    record = {
        "schemaVersion": "0.1",
        "kind": "message.appended",
        "recordId": "record-summary-only-crash",
        "sessionId": last["sessionId"],
        "seq": int(last["seq"]) + 1,
        "parentId": last["recordId"],
        "time": "2026-07-14T02:00:13Z",
        "data": {
            "message": {
                "messageId": "message-summary-only-crash",
                "role": "assistant",
                "content": [
                    {
                        "type": "text",
                        "text": "Summary durable before an injected compaction crash.",
                    }
                ],
                "provider": {"id": "faux", "modelId": "faux-v1"},
                "finishReason": "stop",
                "usage": {
                    "inputTokens": 20,
                    "outputTokens": 7,
                    "totalTokens": 27,
                },
                "time": "2026-07-14T02:00:13Z",
            }
        },
    }
    with journal_path.open("a", encoding="utf-8", newline="\n") as stream:
        stream.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")))
        stream.write("\n")


def corrupt_parent_reference(journal_path: Path) -> None:
    """功能：在临时案例中制造 Schema 合法但 tree 语义损坏的 parent 引用。

    输入：隔离 fixture journal 路径。
    输出：只替换最终 selection 的 parentId，形成 unknown earlier reference。
    不变量：不触碰仓库内规范 fixture，且替换必须恰好发生一次。
    失败：目标片段缺失/重复或文件 I/O 失败时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    original = journal_path.read_text(encoding="utf-8")
    needle = '"parentId":"record-branch-a-assistant"'
    if original.count(needle) != 1:
        raise BranchCompactionRunnerError(
            "corrupt-journal fixture parent marker is not unique"
        )
    journal_path.write_text(
        original.replace(needle, '"parentId":"record-unknown-parent"', 1),
        encoding="utf-8",
        newline="\n",
    )


def branch_select_request(
    request_id: str,
    session_id: str,
    expected_head_record_id: str,
    target_leaf_record_id: str,
) -> JSONObject:
    """功能：构造具有显式 CAS head 的 branch selection 请求。

    输入：请求/Session ID、预期 selected head 与 earlier target ID。
    输出：符合公共 JSON-RPC Schema 的 session/branch/select 请求。
    不变量：ID 原样作为 opaque 字符串传输，不推断其结构或顺序。
    失败：本函数只构造内存对象，不访问 Session。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": request_id,
        "method": "session/branch/select",
        "params": {
            "sessionId": session_id,
            "expectedHeadRecordId": expected_head_record_id,
            "targetLeafRecordId": target_leaf_record_id,
        },
    }


def compact_request(
    request_id: str,
    session_id: str,
    expected_head_record_id: str,
    first_retained_record_id: str,
    *,
    tokens_before: int = 90,
    tokens_after: int = 40,
) -> JSONObject:
    """功能：构造确定性 summary pair 的 session/compact 请求。

    输入：请求/Session/head/retained ID 以及压缩前后 token 估算。
    输出：使用 faux summarizer、固定 usage 和 versioned strategy 的 RPC 请求。
    不变量：不在 runner 中生成实现专属 ID，summary/result ID 由 daemon 返回。
    失败：非法 token 组合仍原样构造，供服务端负向 conformance 验证。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": request_id,
        "method": "session/compact",
        "params": {
            "sessionId": session_id,
            "expectedHeadRecordId": expected_head_record_id,
            "firstRetainedRecordId": first_retained_record_id,
            "summaryText": "Conformance summary for the selected branch.",
            "provider": {"id": "faux", "modelId": "faux-v1"},
            "usage": {"inputTokens": 30, "outputTokens": 8, "totalTokens": 38},
            "tokensBefore": tokens_before,
            "tokensAfter": tokens_after,
            "strategy": "conformance-summary-v1",
        },
    }


def load_branch_state(
    journal_path: Path, schema_root: Path
) -> tuple[branch_compaction_validation.BranchCompactionState, list[JSONObject]]:
    """功能：从 mutation 临时 journal 加载 Schema 合法记录并计算 selected 投影。

    输入：隔离 journal 路径与公共 Schema 根。
    输出：branch/compaction 状态及完整 header/record JSON 列表。
    不变量：先逐行严格验证，再执行 tree、quiescence 与 compaction 跨记录验证。
    失败：字节、Schema 或语义损坏时传播共同 validator 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    validator = session_validation.build_line_validator(schema_root)
    _, values = session_validation.load_journal(journal_path, validator)
    return branch_compaction_validation.validate_values(values), values


def run_summary_only_crash_case(
    raw_template: str,
    case_root: Path,
    case_dir: Path,
    schema_root: Path,
    initial_state: branch_compaction_validation.BranchCompactionState,
    timeout: float,
) -> None:
    """功能：验证 summary-only 崩溃态不被误认为已完成新压缩。

    输入：daemon、隔离路径、规范 fixture、初态与超时。
    输出：旧 compaction ID 保持，summary-like message 仅作为普通尾消息返回。
    不变量：只读 session/get 不得修补、删除或补写 context.compacted。
    失败：journal 被改写、active compaction 漂移或投影遗漏/重复时终止套件。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    get_id = "mutation-summary-only-get"
    requests = [
        initialize_request("mutation-summary-only-init"),
        {
            "jsonrpc": "2.0",
            "id": get_id,
            "method": "session/get",
            "params": {"sessionId": initial_state.session_id, "afterSeq": 0},
        },
    ]
    frames, journal_path, byte_prefix = run_rpc_case(
        raw_template,
        case_root,
        case_dir,
        initial_state.session_id,
        schema_root,
        requests,
        timeout,
        append_summary_without_compaction,
    )
    require_unchanged_journal(journal_path, byte_prefix, "summary-only crash read")
    state, values = load_branch_state(journal_path, schema_root)
    if (
        len(values) != 14
        or state.compaction_record_id != initial_state.compaction_record_id
        or len(state.messages) != len(initial_state.messages) + 1
        or state.messages[-1].get("messageId") != "message-summary-only-crash"
    ):
        raise BranchCompactionRunnerError(
            "summary-only crash state was treated as a completed compaction"
        )
    validate_snapshot(response_result(frames, get_id), state)


def run_successful_branch_case(
    raw_template: str,
    case_root: Path,
    case_dir: Path,
    schema_root: Path,
    initial_state: branch_compaction_validation.BranchCompactionState,
    timeout: float,
) -> None:
    """功能：验证真实 branch mutation、重复选择、结果身份、投影与 append-only。

    输入：daemon、隔离路径、fixture、初态及超时。
    输出：连续两次选择 Branch B 并由 session/get 返回同一投影时正常返回。
    不变量：每次只追加一个 branch.selected，返回 head 必须等于本次 selection ID。
    失败：wire、结果 ID、字节前缀、记录数量或精确投影不符时终止套件。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    request_id = "mutation-branch-success"
    get_id = "mutation-branch-success-get"
    requests = [
        initialize_request("mutation-branch-success-init"),
        branch_select_request(
            request_id,
            initial_state.session_id,
            initial_state.selected_head_record_id,
            "record-branch-b-assistant",
        ),
        {
            "jsonrpc": "2.0",
            "id": get_id,
            "method": "session/get",
            "params": {"sessionId": initial_state.session_id, "afterSeq": 0},
        },
    ]
    frames, journal_path, byte_prefix = run_rpc_case(
        raw_template,
        case_root,
        case_dir,
        initial_state.session_id,
        schema_root,
        requests,
        timeout,
    )
    final_raw = journal_path.read_bytes()
    if not final_raw.startswith(byte_prefix) or final_raw == byte_prefix:
        raise BranchCompactionRunnerError(
            "successful branch selection did not append to the byte prefix"
        )
    result = response_result(frames, request_id)
    selection_id = result.get("selectionRecordId")
    if (
        result.get("targetLeafRecordId") != "record-branch-b-assistant"
        or not isinstance(selection_id, str)
        or not selection_id
        or result.get("selectedHeadRecordId") != selection_id
    ):
        raise BranchCompactionRunnerError(
            "branch selection result IDs are inconsistent"
        )
    final_state, values = load_branch_state(journal_path, schema_root)
    if (
        final_state.selected_head_record_id != selection_id
        or len(values) != 14
        or [message.get("messageId") for message in final_state.messages]
        != [
            "message-summary",
            "message-recent-user",
            "message-recent-assistant",
            "message-branch-b-user",
            "message-branch-b-assistant",
        ]
    ):
        raise BranchCompactionRunnerError(
            "successful branch selection produced an invalid tree or projection"
        )
    validate_snapshot(response_result(frames, get_id), final_state)
    repeat_id = "mutation-branch-repeat"
    repeat_get_id = "mutation-branch-repeat-get"
    repeat_requests = [
        initialize_request("mutation-branch-repeat-init"),
        branch_select_request(
            repeat_id,
            initial_state.session_id,
            selection_id,
            "record-branch-b-assistant",
        ),
        {
            "jsonrpc": "2.0",
            "id": repeat_get_id,
            "method": "session/get",
            "params": {"sessionId": initial_state.session_id, "afterSeq": 0},
        },
    ]
    process = start_daemon(
        raw_template,
        case_root / "workspace",
        case_root / "state",
        case_root / "state" / "sessions",
        initial_state.session_id,
        timeout,
    )
    repeat_frames = process.run(repeat_requests, settle_seconds=0.05)
    runner.TraceValidator(repeat_requests).validate(repeat_frames)
    schema_validation.validate_protocol_trace(
        repeat_requests, repeat_frames, schema_root
    )
    validate_declared_methods(
        response_result(repeat_frames, "mutation-branch-repeat-init")
    )
    repeat_result = response_result(repeat_frames, repeat_id)
    repeat_selection_id = repeat_result.get("selectionRecordId")
    if (
        not isinstance(repeat_selection_id, str)
        or not repeat_selection_id
        or repeat_selection_id == selection_id
        or repeat_result.get("targetLeafRecordId") != "record-branch-b-assistant"
        or repeat_result.get("selectedHeadRecordId") != repeat_selection_id
    ):
        raise BranchCompactionRunnerError(
            "repeated branch selection result IDs are inconsistent"
        )
    repeated_raw = journal_path.read_bytes()
    if not repeated_raw.startswith(final_raw) or repeated_raw == final_raw:
        raise BranchCompactionRunnerError(
            "repeated branch selection rewrote the prior byte prefix"
        )
    repeated_state, repeated_values = load_branch_state(journal_path, schema_root)
    if (
        len(repeated_values) != 15
        or repeated_state.selected_head_record_id != repeat_selection_id
        or repeated_state.messages != final_state.messages
    ):
        raise BranchCompactionRunnerError(
            "repeated branch selection changed the selected message projection"
        )
    validate_snapshot(response_result(repeat_frames, repeat_get_id), repeated_state)


def run_successful_compaction_case(
    raw_template: str,
    case_root: Path,
    case_dir: Path,
    schema_root: Path,
    initial_state: branch_compaction_validation.BranchCompactionState,
    timeout: float,
) -> None:
    """功能：验证真实 compact mutation 原子追加 summary pair 并激活新投影。

    输入：daemon、隔离路径、fixture、初态及超时。
    输出：两个 durable 记录、四个一致 result ID 和精确新 context 均通过。
    不变量：summary 紧接 source，compaction 紧接 summary，旧字节完全保留。
    失败：只追加一条、ID 引用漂移、投影重复 summary 或 Schema 失败时终止。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    request_id = "mutation-compact-success"
    get_id = "mutation-compact-success-get"
    requests = [
        initialize_request("mutation-compact-success-init"),
        compact_request(
            request_id,
            initial_state.session_id,
            initial_state.selected_head_record_id,
            "record-branch-a-user",
        ),
        {
            "jsonrpc": "2.0",
            "id": get_id,
            "method": "session/get",
            "params": {"sessionId": initial_state.session_id, "afterSeq": 0},
        },
    ]
    frames, journal_path, byte_prefix = run_rpc_case(
        raw_template,
        case_root,
        case_dir,
        initial_state.session_id,
        schema_root,
        requests,
        timeout,
    )
    final_raw = journal_path.read_bytes()
    if not final_raw.startswith(byte_prefix) or final_raw == byte_prefix:
        raise BranchCompactionRunnerError(
            "successful compaction did not preserve and extend the byte prefix"
        )
    result = response_result(frames, request_id)
    result_ids = [
        result.get("summaryMessageId"),
        result.get("summaryRecordId"),
        result.get("compactionRecordId"),
        result.get("selectedHeadRecordId"),
    ]
    if (
        not all(isinstance(value, str) and value for value in result_ids)
        or len(set(result_ids)) != 3
        or result.get("selectedHeadRecordId") != result.get("compactionRecordId")
    ):
        raise BranchCompactionRunnerError("compaction result IDs are invalid")
    final_state, values = load_branch_state(journal_path, schema_root)
    if (
        len(values) != 15
        or final_state.selected_head_record_id != result.get("compactionRecordId")
        or final_state.compaction_record_id != result.get("compactionRecordId")
        or [message.get("messageId") for message in final_state.messages]
        != [
            result.get("summaryMessageId"),
            "message-branch-a-user",
            "message-branch-a-assistant",
        ]
        or final_state.messages[0].get("content")
        != [{"type": "text", "text": "Conformance summary for the selected branch."}]
    ):
        raise BranchCompactionRunnerError(
            "successful compaction produced an invalid pair or projection"
        )
    validate_snapshot(response_result(frames, get_id), final_state)


def run_unchanged_error_case(
    raw_template: str,
    case_root: Path,
    case_dir: Path,
    schema_root: Path,
    session_id: str,
    request: JSONObject,
    expected_error: str,
    timeout: float,
    journal_mutator: Callable[[Path], None] | None = None,
) -> None:
    """功能：运行一个预期失败的 mutation 并证明 journal 零字节变化。

    输入：隔离案例、一个 mutation 请求、固定错误名及可选 fixture 变换器。
    输出：portable error 精确且失败前后 journal 相同时正常返回。
    不变量：initialize 成功后只发送这一项状态变更请求。
    失败：请求意外成功、错误三元组漂移或任何 journal 写入时终止套件。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    init_id = str(request["id"]) + "-init"
    frames, journal_path, byte_prefix = run_rpc_case(
        raw_template,
        case_root,
        case_dir,
        session_id,
        schema_root,
        [initialize_request(init_id), request],
        timeout,
        journal_mutator,
    )
    assert_response_error(frames, str(request["id"]), expected_error)
    require_unchanged_journal(journal_path, byte_prefix, expected_error)


def run_busy_error_case(
    raw_template: str,
    case_root: Path,
    case_dir: Path,
    schema_root: Path,
    initial_state: branch_compaction_validation.BranchCompactionState,
    timeout: float,
) -> None:
    """功能：在 faux delay 的 active run 中验证 mutation 优先返回 session_busy。

    输入：daemon、隔离路径、fixture、初态及超时。
    输出：run 最终完成且并发 branch mutation 返回 -32004/session_busy。
    不变量：busy 判定先于 stale expected-head，失败 mutation 不追加 selection。
    失败：mutation 排队到 run 后、错误映射漂移或写入 branch 记录时终止。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    mutation_id = "mutation-session-busy"
    requests: list[JSONObject] = [
        initialize_request("mutation-session-busy-init"),
        {
            "jsonrpc": "2.0",
            "id": "mutation-session-busy-configure",
            "method": "faux/configure",
            "params": {
                "sessionId": initial_state.session_id,
                "scenario": {
                    "schemaVersion": "0.1",
                    "name": "branch-session-busy",
                    "seed": 20260714,
                    "steps": [
                        {"type": "delay", "durationMs": 750},
                        {"type": "text", "text": "busy probe completed"},
                    ],
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
            "id": "mutation-session-busy-run",
            "method": "run/start",
            "params": {
                "sessionId": initial_state.session_id,
                "input": {
                    "role": "user",
                    "content": [{"type": "text", "text": "hold the Session"}],
                },
                "provider": {"id": "faux", "modelId": "faux-v1"},
            },
        },
        branch_select_request(
            mutation_id,
            initial_state.session_id,
            initial_state.selected_head_record_id,
            "record-branch-b-assistant",
        ),
    ]
    frames, journal_path, _ = run_rpc_case(
        raw_template,
        case_root,
        case_dir,
        initial_state.session_id,
        schema_root,
        requests,
        timeout,
    )
    assert_response_error(frames, mutation_id, "sessionBusy")
    _, values = load_branch_state(journal_path, schema_root)
    if sum(record.get("kind") == "branch.selected" for record in values) != 2:
        raise BranchCompactionRunnerError(
            "session_busy mutation appended an unexpected branch.selected record"
        )


def run_mutation_suite(
    raw_template: str,
    stage: int,
    work_root: Path,
    case_dir: Path,
    schema_root: Path,
    initial_state: branch_compaction_validation.BranchCompactionState,
    timeout: float,
) -> None:
    """功能：运行 mutation 成功/重复/崩溃读取和七类固定错误黑盒套件。

    输入：一个语言 daemon 模板、stage 隔离根、公共 fixture/Schema 与初态。
    输出：durable mutation、summary-only 恢复和全部 portable failure 通过时返回。
    不变量：每个案例使用独立副本，错误案例必须保持自身 byte prefix 不变。
    失败：任一协议、持久化、投影、CAS、quiescence 或错误映射断言失败即停止。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    stage_root = work_root / f"implementation-{stage + 1}"
    run_successful_branch_case(
        raw_template,
        stage_root / "branch-success",
        case_dir,
        schema_root,
        initial_state,
        timeout,
    )
    run_successful_compaction_case(
        raw_template,
        stage_root / "compact-success",
        case_dir,
        schema_root,
        initial_state,
        timeout,
    )
    run_summary_only_crash_case(
        raw_template,
        stage_root / "summary-only-crash",
        case_dir,
        schema_root,
        initial_state,
        timeout,
    )
    simple_cases = [
        (
            "stale-head",
            branch_select_request(
                "mutation-stale-head",
                initial_state.session_id,
                "record-stale-head",
                "record-branch-b-assistant",
            ),
            "staleExpectedHead",
        ),
        (
            "record-not-found",
            branch_select_request(
                "mutation-record-not-found",
                initial_state.session_id,
                initial_state.selected_head_record_id,
                "record-does-not-exist",
            ),
            "recordNotFound",
        ),
        (
            "invalid-boundary",
            compact_request(
                "mutation-invalid-boundary",
                initial_state.session_id,
                initial_state.selected_head_record_id,
                "record-recent-assistant",
            ),
            "invalidCompactionBoundary",
        ),
        (
            "invalid-tokens",
            compact_request(
                "mutation-invalid-tokens",
                initial_state.session_id,
                initial_state.selected_head_record_id,
                "record-branch-a-user",
                tokens_before=40,
                tokens_after=41,
            ),
            "invalidCompactionTokens",
        ),
    ]
    for name, request, expected in simple_cases:
        run_unchanged_error_case(
            raw_template,
            stage_root / name,
            case_dir,
            schema_root,
            initial_state.session_id,
            request,
            expected,
            timeout,
        )
    run_unchanged_error_case(
        raw_template,
        stage_root / "non-quiescent",
        case_dir,
        schema_root,
        initial_state.session_id,
        branch_select_request(
            "mutation-non-quiescent",
            initial_state.session_id,
            "record-non-quiescent-event",
            "record-non-quiescent-run",
        ),
        "branchNotQuiescent",
        timeout,
        append_non_quiescent_history,
    )
    run_unchanged_error_case(
        raw_template,
        stage_root / "journal-corrupt",
        case_dir,
        schema_root,
        initial_state.session_id,
        branch_select_request(
            "mutation-journal-corrupt",
            initial_state.session_id,
            initial_state.selected_head_record_id,
            "record-branch-b-assistant",
        ),
        "journalCorrupt",
        timeout,
        corrupt_parent_reference,
    )
    run_busy_error_case(
        raw_template,
        stage_root / "session-busy",
        case_dir,
        schema_root,
        initial_state,
        timeout,
    )


def run_stage(
    raw_template: str,
    stage: int,
    workspace: Path,
    state_root: Path,
    sessions_root: Path,
    journal_path: Path,
    schema_root: Path,
    state: branch_compaction_validation.BranchCompactionState,
    byte_prefix: bytes,
    timeout: float,
) -> tuple[branch_compaction_validation.BranchCompactionState, bytes]:
    """功能：驱动一个实现 snapshot + faux continuation 并验证 append-only 新状态。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    input_message: JSONObject = {
        "role": "user",
        "content": [
            {
                "type": "text",
                "text": f"Continue selected compacted branch at stage {stage + 1}.",
            }
        ],
    }
    response_text = f"Selected branch continuation stage {stage + 1}."
    requests = build_requests(
        stage, state.session_id, state, input_message, response_text
    )
    process = start_daemon(
        raw_template,
        workspace,
        state_root,
        sessions_root,
        state.session_id,
        timeout,
    )
    frames = process.run(requests, settle_seconds=0.05)
    runner.TraceValidator(requests).validate(frames)
    schema_validation.validate_protocol_trace(requests, frames, schema_root)
    prefix = f"branch-stage-{stage + 1}"
    initialize_result = response_result(frames, prefix + "-initialize")
    validate_declared_methods(initialize_result)
    validate_snapshot(response_result(frames, prefix + "-get"), state)
    validator = session_validation.build_line_validator(schema_root)
    final_raw, values = session_validation.load_journal(journal_path, validator)
    if not final_raw.startswith(byte_prefix) or len(final_raw) <= len(byte_prefix):
        raise BranchCompactionRunnerError(
            "implementation rewrote prefix or appended nothing"
        )
    next_state = branch_compaction_validation.validate_values(values)
    if len(next_state.messages) != len(state.messages) + 2:
        raise BranchCompactionRunnerError(
            "continuation did not append exactly two messages"
        )
    if next_state.messages[-2].get("content") != input_message["content"]:
        raise BranchCompactionRunnerError("continued user message differs from request")
    assistant_content = next_state.messages[-1].get("content")
    if assistant_content != [{"type": "text", "text": response_text}]:
        raise BranchCompactionRunnerError(
            "continued assistant message differs from faux output"
        )
    return next_state, final_raw


def run_probe(args: argparse.Namespace, work_root: Path) -> int:
    """功能：静态验证 fixture，并让多个实现按顺序续接同一 selected branch。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    case_dir = args.case_dir.resolve()
    schema_root = args.schema_root.resolve()
    state = branch_compaction_validation.validate_case(case_dir, schema_root)
    if not args.daemon_command_json:
        print(
            "PASS: branch/compaction static projection "
            f"head={state.selected_head_record_id}, messages={len(state.messages)}"
        )
        return 0
    mutation_root = work_root / "mutations"
    for stage, template in enumerate(args.daemon_command_json):
        run_mutation_suite(
            template,
            stage,
            mutation_root,
            case_dir,
            schema_root,
            state,
            args.timeout,
        )
        print(f"PASS: branch/compaction mutation suite stage {stage + 1}")
    workspace, state_root, sessions_root, journal_path = prepare_root(
        work_root / "continuation", case_dir, state.session_id
    )
    byte_prefix = journal_path.read_bytes()
    for stage, template in enumerate(args.daemon_command_json):
        state, byte_prefix = run_stage(
            template,
            stage,
            workspace,
            state_root,
            sessions_root,
            journal_path,
            schema_root,
            state,
            byte_prefix,
            args.timeout,
        )
        print(
            f"PASS: branch/compaction stage {stage + 1}, "
            f"head={state.selected_head_record_id}, messages={len(state.messages)}"
        )
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    """功能：运行静态或黑盒 branch/compaction continuation gate。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = parse_args(argv)
    try:
        if args.timeout <= 0:
            raise BranchCompactionRunnerError("--timeout must be positive")
        if args.work_root is not None:
            root = args.work_root.resolve()
            root.mkdir(parents=True, exist_ok=True)
            if any(root.iterdir()):
                raise BranchCompactionRunnerError("--work-root must be empty")
            return run_probe(args, root)
        with tempfile.TemporaryDirectory(prefix="qxnm-forge-branch-") as temporary:
            return run_probe(args, Path(temporary))
    except (
        OSError,
        runner.ConformanceError,
        runner.ProtocolViolation,
        schema_validation.SchemaValidationError,
        session_validation.SessionValidationError,
        branch_compaction_validation.BranchCompactionError,
        BranchCompactionRunnerError,
    ) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
