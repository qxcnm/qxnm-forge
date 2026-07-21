#!/usr/bin/env python3
"""以 faux stdio daemon 验证 Rust/.NET Session 生命周期双向互操作。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
from pathlib import Path
import re
import secrets
import tempfile
from typing import Any, Sequence

import runner
from schema_validation import validate_protocol_trace
import session_validation


JSONObject = dict[str, Any]
ROOT = Path(__file__).resolve().parent.parent
DEFAULT_SCHEMAS = ROOT / "SPEC/schemas"
REQUIRED_METHODS = frozenset(
    {
        "initialize",
        "faux/configure",
        "run/start",
        "session/list",
        "session/archive",
        "session/restore",
        "session/delete",
    }
)
SUMMARY_REQUIRED_FIELDS = frozenset(
    {"sessionId", "title", "project", "updatedAt", "archived"}
)
SUMMARY_ALLOWED_FIELDS = SUMMARY_REQUIRED_FIELDS | {"status"}
OPAQUE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$")
CURSOR = re.compile(r"^[A-Za-z0-9._:-]{1,64}$")
SYNTHETIC_SECRET_ENV = "QXNM_FORGE_SESSION_LIFECYCLE_SYNTHETIC_SECRET"
MAX_PAGES = 256
MAX_SESSIONS = 4096


class SessionLifecycleConformanceError(Exception):
    """表示双运行时生命周期、隐私或 durable 状态违反 ADR 0030。"""


def parse_command(raw: str, label: str) -> list[str]:
    """功能：严格解析不经 shell 的有界 JSON argv 前缀。

    输入：命令 JSON 文本与公开运行时标签。
    输出：最多 128 项、每项最多 4,096 字符的 argv。
    不变量：不展开 shell，也不把命令内容写入错误。
    失败：JSON、数组、字符串、长度或 NUL 边界无效时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if len(raw) > 512 * 1024:
        raise SessionLifecycleConformanceError(f"{label} command is invalid")
    try:
        value = runner.strict_json_loads(raw)
    except runner.ProtocolViolation as exc:
        raise SessionLifecycleConformanceError(f"{label} command is invalid") from exc
    if (
        not isinstance(value, list)
        or not 1 <= len(value) <= 128
        or any(
            not isinstance(item, str) or not item or len(item) > 4096 or "\x00" in item
            for item in value
        )
    ):
        raise SessionLifecycleConformanceError(f"{label} command is invalid")
    return list(value)


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    """功能：解析双运行时命令、Schema、临时根与总等待上限。

    输入：可选 CLI argv；省略时读取进程参数。
    输出：已验证基础类型的 argparse Namespace。
    不变量：只有同时提供 Rust 与 .NET 命令时才会启动 daemon。
    失败：缺失参数或基础类型错误时由 argparse 终止。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rust-command-json", required=True)
    parser.add_argument("--dotnet-command-json", required=True)
    parser.add_argument("--schema-root", type=Path, default=DEFAULT_SCHEMAS)
    parser.add_argument(
        "--work-root",
        type=Path,
        help="保留隔离证据的空目录；省略时自动创建并清理临时目录",
    )
    parser.add_argument("--timeout", type=float, default=15.0)
    return parser.parse_args(argv)


def daemon_command(
    prefix: Sequence[str], runtime: str, workspace: Path, state_root: Path
) -> list[str]:
    """功能：为 Rust 或 .NET 前缀追加各自真实 stdio daemon 参数。

    输入：可执行前缀、`rust`/`dotnet`、隔离 workspace 与 state root。
    输出：保持 argv 边界的完整 daemon 命令。
    不变量：Rust 不伪造 `--stdio`；.NET 显式要求 `--stdio`。
    失败：未知运行时立即拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    common = [
        "daemon",
        "--conformance",
        "--workspace",
        str(workspace),
        "--state-dir",
        str(state_root),
    ]
    if runtime == "rust":
        return [*prefix, *common]
    if runtime == "dotnet":
        return [*prefix, "daemon", "--stdio", *common[1:]]
    raise SessionLifecycleConformanceError("runtime label is invalid")


def scrubbed_environment(root: Path, canary: str) -> tuple[list[str], JSONObject]:
    """功能：构造清除真实 secret/endpoint 且强制离线 faux 的子进程环境。

    输入：隔离临时根与运行期随机 canary。
    输出：要删除的继承变量名和要注入的安全环境映射。
    不变量：home/config/cache 都位于临时根；代理固定为 dead loopback；live 开关关闭。
    失败：本函数不访问网络、CredentialStore 或 Provider。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    sensitive_markers = (
        "API_KEY",
        "TOKEN",
        "SECRET",
        "PASSWORD",
        "CREDENTIAL",
        "ENDPOINT",
        "BASE_URL",
        "CATALOG_URL",
    )
    removed = [
        name
        for name in os.environ
        if any(marker in name.upper() for marker in sensitive_markers)
        or name.lower()
        in {
            "http_proxy",
            "https_proxy",
            "all_proxy",
            "no_proxy",
        }
    ]
    home = root / "home"
    for directory in (home, root / "xdg-config", root / "xdg-cache", root / "azure"):
        directory.mkdir(parents=True, mode=0o700, exist_ok=True)
    extra: JSONObject = {
        "HOME": str(home),
        "USERPROFILE": str(home),
        "XDG_CONFIG_HOME": str(root / "xdg-config"),
        "XDG_CACHE_HOME": str(root / "xdg-cache"),
        "AZURE_CONFIG_DIR": str(root / "azure"),
        "AWS_EC2_METADATA_DISABLED": "true",
        "QXNM_FORGE_CONFORMANCE": "1",
        "QXNM_FORGE_LIVE_TESTS": "0",
        "QXNM_FORGE_ALLOW_LIVE_PROVIDER_TESTS": "0",
        "QXNM_FORGE_PROVIDER_CONFORMANCE": "0",
        "HTTP_PROXY": "http://127.0.0.1:9",
        "HTTPS_PROXY": "http://127.0.0.1:9",
        "ALL_PROXY": "http://127.0.0.1:9",
        "http_proxy": "http://127.0.0.1:9",
        "https_proxy": "http://127.0.0.1:9",
        "all_proxy": "http://127.0.0.1:9",
        "NO_PROXY": "localhost,127.0.0.1,::1",
        "no_proxy": "localhost,127.0.0.1,::1",
        SYNTHETIC_SECRET_ENV: canary,
    }
    return removed, extra


def initialize_request(request_id: str) -> JSONObject:
    """功能：构造只协商公共 0.1 与 stdio event replay 的 initialize 请求。

    输入：本次连接唯一 request ID。
    输出：不含产品品牌或凭据的 JSON-RPC 对象。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": request_id,
        "method": "initialize",
        "params": {
            "protocolVersions": ["0.1"],
            "client": {
                "name": "session-lifecycle-conformance",
                "version": "0.1.0",
            },
            "capabilities": {"eventReplay": True},
        },
    }


def create_requests(
    prefix: str, session_id: str, prompt: str, response_text: str
) -> list[JSONObject]:
    """功能：构造 initialize、faux 配置与新 Session run 的完整请求序列。

    输入：request 前缀、目标 ID、首条用户文本和确定性回复。
    输出：只使用 faux/faux-v1 的三个 JSON-RPC 请求。
    不变量：expectedContext 精确等于将要 durable 的首条 user input。
    失败：输入边界由公共 Schema 和 daemon 共同验证。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    input_message = {
        "role": "user",
        "content": [{"type": "text", "text": prompt}],
    }
    scenario_name = prefix + "-scenario"
    return [
        initialize_request(prefix + "-initialize"),
        {
            "jsonrpc": "2.0",
            "id": prefix + "-configure",
            "method": "faux/configure",
            "params": {
                "sessionId": session_id,
                "scenario": {
                    "schemaVersion": "0.1",
                    "name": scenario_name,
                    "seed": 20260721,
                    "expectedContext": [input_message],
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


def rpc_request(request_id: str, method: str, params: JSONObject) -> JSONObject:
    """功能：构造一个有 ID 的生命周期 JSON-RPC 请求。

    输入：唯一 ID、冻结 method 与封闭 params。
    输出：JSON-RPC 2.0 request object。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": request_id,
        "method": method,
        "params": params,
    }


def response_for(frames: Sequence[JSONObject], request_id: str) -> JSONObject:
    """功能：取得指定 request 的唯一响应对象。

    输入：已严格解析的 server frames 与 request ID。
    输出：包含 result 或 error 的唯一响应。
    失败：缺失或重复响应时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    matches = [frame for frame in frames if frame.get("id") == request_id]
    if len(matches) != 1:
        raise SessionLifecycleConformanceError("daemon response cardinality is invalid")
    return matches[0]


def result_for(frames: Sequence[JSONObject], request_id: str) -> JSONObject:
    """功能：取得指定 request 的唯一成功 result 对象。

    输入：server frames 与 request ID。
    输出：严格为 JSON object 的 result。
    失败：响应为 error、缺失或 result 非对象时拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    response = response_for(frames, request_id)
    result = response.get("result")
    if not isinstance(result, dict) or "error" in response:
        raise SessionLifecycleConformanceError("daemon operation did not succeed")
    return result


def validate_capabilities(result: JSONObject, runtime: str) -> int:
    """功能：验证实现身份及 lifecycle/faux/stdio 能力广告并返回帧上限。

    输入：initialize result 与预期运行时语言。
    输出：协商后的正整数 maxFrameBytes。
    不变量：四个生命周期方法、faux run 和 stdio 都必须真实广告。
    失败：语言、能力、Provider 或限制缺失时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    implementation = result.get("implementation")
    capabilities = result.get("capabilities")
    limits = result.get("limits")
    if (
        not isinstance(implementation, dict)
        or implementation.get("language") != runtime
    ):
        raise SessionLifecycleConformanceError(
            "daemon implementation language is invalid"
        )
    if not isinstance(capabilities, dict) or not isinstance(limits, dict):
        raise SessionLifecycleConformanceError("daemon initialize result is incomplete")
    methods = capabilities.get("methods")
    if (
        not isinstance(methods, list)
        or any(not isinstance(item, str) for item in methods)
        or not REQUIRED_METHODS.issubset(methods)
    ):
        raise SessionLifecycleConformanceError(
            "daemon lifecycle methods are not advertised"
        )
    transports = capabilities.get("transports")
    if not isinstance(transports, list) or "stdio" not in transports:
        raise SessionLifecycleConformanceError(
            "daemon stdio transport is not advertised"
        )
    providers = capabilities.get("providers")
    if not isinstance(providers, list) or not any(
        isinstance(item, dict)
        and item.get("id") == "faux"
        and isinstance(item.get("models"), list)
        and "faux-v1" in item["models"]
        for item in providers
    ):
        raise SessionLifecycleConformanceError("daemon faux provider is not advertised")
    max_frame = limits.get("maxFrameBytes")
    if (
        not isinstance(max_frame, int)
        or isinstance(max_frame, bool)
        or max_frame < 1024
    ):
        raise SessionLifecycleConformanceError("daemon maxFrameBytes is invalid")
    return max_frame


def assert_canary_absent(
    canary: str, frames: Sequence[JSONObject], stderr: str
) -> None:
    """功能：确认 synthetic secret 没有进入协议 stdout 或诊断 stderr。

    输入：运行期 canary、解析帧与有界 stderr 文本。
    输出：零泄漏时无返回值。
    失败：任一输出包含 canary 时使用固定错误拒绝，不复述 secret。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    encoded = json.dumps(frames, ensure_ascii=False, separators=(",", ":"))
    if canary in encoded or canary in stderr:
        raise SessionLifecycleConformanceError(
            "synthetic secret leaked to daemon output"
        )


def validate_trace(
    requests: Sequence[JSONObject],
    frames: Sequence[JSONObject],
    schema_root: Path,
    runtime: str,
) -> int:
    """功能：执行公共跨消息、Draft 2020-12 与能力广告三重验证。

    输入：完整请求/响应轨迹、Schema 根和运行时语言。
    输出：initialize 协商的 maxFrameBytes。
    失败：协议关联、事件顺序、Schema 或能力广告不一致时传播固定门禁失败。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    runner.TraceValidator(requests).validate(frames)
    validate_protocol_trace(list(requests), list(frames), schema_root)
    initialize_id = str(requests[0]["id"])
    return validate_capabilities(result_for(frames, initialize_id), runtime)


def execute_requests(
    prefix: Sequence[str],
    runtime: str,
    workspace: Path,
    state_root: Path,
    requests: Sequence[JSONObject],
    schema_root: Path,
    environment_root: Path,
    canary: str,
    timeout: float,
) -> list[JSONObject]:
    """功能：在单个隔离 daemon 中执行已知请求序列并严格验证完整轨迹。

    输入：运行时命令、隔离路径、请求、Schema、canary 与 deadline。
    输出：验证后的 server frames。
    不变量：无 shell、faux-only、继承 secret 清除、stdout 严格 NDJSON。
    失败：启动、超时、协议、Schema、泄漏或非零行为均包装为固定阶段错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    removed, extra = scrubbed_environment(environment_root, canary)
    process = runner.DaemonProcess(
        daemon_command(prefix, runtime, workspace, state_root),
        timeout=timeout,
        max_frame_bytes=1_048_576,
        stderr_limit=131_072,
        extra_env=extra,
        removed_env=removed,
    )
    try:
        frames = process.run(requests, settle_seconds=0.05)
        stderr = process.stderr_text(limit=131_072)
        if process.process.returncode != 0:
            raise SessionLifecycleConformanceError(
                f"{runtime} daemon did not exit cleanly"
            )
        validate_trace(requests, frames, schema_root, runtime)
        assert_canary_absent(canary, frames, stderr)
        return frames
    except SessionLifecycleConformanceError:
        raise
    except (
        OSError,
        ValueError,
        runner.ConformanceError,
        session_validation.SessionValidationError,
    ) as exc:
        raise SessionLifecycleConformanceError(
            f"{runtime} daemon interaction failed"
        ) from exc


def parse_utc(value: Any) -> dt.datetime:
    """功能：解析摘要的规范 UTC 时间并拒绝本地时间或非字符串。

    输入：`updatedAt` 值。
    输出：带 UTC tzinfo 的 datetime。
    失败：格式、时区或类型无效时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, str) or not value.endswith("Z"):
        raise SessionLifecycleConformanceError("session summary updatedAt is invalid")
    try:
        parsed = dt.datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError as exc:
        raise SessionLifecycleConformanceError(
            "session summary updatedAt is invalid"
        ) from exc
    if parsed.tzinfo != dt.timezone.utc:
        raise SessionLifecycleConformanceError("session summary updatedAt is invalid")
    return parsed


def validate_summary(summary: Any) -> JSONObject:
    """功能：验证 Session 摘要是封闭、脱敏、类型有界的 ADR 0030 投影。

    输入：一个未知 JSON 值。
    输出：验证后的摘要对象。
    不变量：只允许五个必需字段和可选 status，不含 transcript/path/secret/provider。
    失败：字段集、ID、文本、时间、归档或状态无效时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(summary, dict):
        raise SessionLifecycleConformanceError("session summary must be an object")
    keys = frozenset(summary)
    if not SUMMARY_REQUIRED_FIELDS.issubset(keys) or not keys.issubset(
        SUMMARY_ALLOWED_FIELDS
    ):
        raise SessionLifecycleConformanceError("session summary fields are not closed")
    session_id = summary.get("sessionId")
    title = summary.get("title")
    project = summary.get("project")
    if not isinstance(session_id, str) or OPAQUE_ID.fullmatch(session_id) is None:
        raise SessionLifecycleConformanceError("session summary ID is invalid")
    if (
        not isinstance(title, str)
        or not 1 <= len(title) <= 96
        or "\n" in title
        or "\r" in title
    ):
        raise SessionLifecycleConformanceError("session summary title is invalid")
    if not isinstance(project, str) or not 1 <= len(project) <= 128:
        raise SessionLifecycleConformanceError("session summary project is invalid")
    if not isinstance(summary.get("archived"), bool):
        raise SessionLifecycleConformanceError("session summary archived is invalid")
    status = summary.get("status")
    if status is not None and status not in {"active", "approval"}:
        raise SessionLifecycleConformanceError("session summary status is invalid")
    parse_utc(summary.get("updatedAt"))
    return summary


def validate_list_result(result: Any, requested_limit: int) -> list[JSONObject]:
    """功能：验证分页结果严格三字段及 cursor/hasMore 一致性。

    输入：未知 result 与本页请求 limit。
    输出：逐项通过脱敏摘要验证的 sessions。
    不变量：终页显式 `nextCursor:null`；非终页必须有进度和合法 opaque cursor。
    失败：字段、数组、页大小、cursor 或布尔语义无效时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(result, dict) or set(result) != {
        "sessions",
        "nextCursor",
        "hasMore",
    }:
        raise SessionLifecycleConformanceError(
            "session/list result is not three-field closed"
        )
    raw_sessions = result.get("sessions")
    has_more = result.get("hasMore")
    next_cursor = result.get("nextCursor")
    if (
        not isinstance(raw_sessions, list)
        or len(raw_sessions) > requested_limit
        or not isinstance(has_more, bool)
    ):
        raise SessionLifecycleConformanceError("session/list page bounds are invalid")
    if has_more:
        if (
            not raw_sessions
            or not isinstance(next_cursor, str)
            or CURSOR.fullmatch(next_cursor) is None
        ):
            raise SessionLifecycleConformanceError(
                "session/list non-terminal cursor is invalid"
            )
    elif next_cursor is not None:
        raise SessionLifecycleConformanceError(
            "session/list terminal cursor must be null"
        )
    return [validate_summary(item) for item in raw_sessions]


def validate_summary_result(result: Any, session_id: str, archived: bool) -> JSONObject:
    """功能：验证 archive/restore 返回唯一目标摘要及预期状态。

    输入：mutation result、目标 ID 与预期 archived 值。
    输出：验证后的摘要。
    失败：外层字段、ID 或 durable 状态不匹配时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(result, dict) or set(result) != {"session"}:
        raise SessionLifecycleConformanceError("session mutation result is invalid")
    summary = validate_summary(result["session"])
    if summary["sessionId"] != session_id or summary["archived"] is not archived:
        raise SessionLifecycleConformanceError("session mutation state is inconsistent")
    return summary


def validate_delete_result(result: Any) -> None:
    """功能：验证永久删除只在完整成功时返回固定 `{deleted:true}`。

    输入：delete success result。
    输出：严格匹配时无返回值。
    失败：字段扩展或值不为 true 时拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if result != {"deleted": True}:
        raise SessionLifecycleConformanceError("session/delete result is invalid")


def validate_redacted_error(
    response: JSONObject,
    request_id: str,
    public_session_id: str,
    private_values: Sequence[str],
) -> None:
    """功能：验证缺失 Session 错误只含公共 kind 与可选公开 resourceId。

    输入：error response、请求 ID、公开 Session ID 和不得泄露的私有文本。
    输出：固定 session_not_found 且完成脱敏时无返回值。
    不变量：错误不携带 path、journal、transcript、Provider 或 secret 字段。
    失败：envelope、错误字段、kind、resourceId 或文本泄漏无效时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if set(response) != {"jsonrpc", "id", "error"} or response.get("id") != request_id:
        raise SessionLifecycleConformanceError("session error envelope is invalid")
    error = response.get("error")
    if not isinstance(error, dict) or set(error) != {
        "code",
        "message",
        "retryable",
        "details",
    }:
        raise SessionLifecycleConformanceError("session error fields are invalid")
    if (
        not isinstance(error.get("code"), int)
        or isinstance(error.get("code"), bool)
        or not isinstance(error.get("message"), str)
        or error.get("retryable") is not False
    ):
        raise SessionLifecycleConformanceError("session error shape is invalid")
    details = error.get("details")
    if (
        not isinstance(details, dict)
        or not set(details).issubset({"kind", "resourceId"})
        or details.get("kind") != "session_not_found"
        or ("resourceId" in details and details.get("resourceId") != public_session_id)
    ):
        raise SessionLifecycleConformanceError("session error details are not redacted")
    serialized = json.dumps(response, ensure_ascii=False, separators=(",", ":"))
    protected_values = [*private_values, "journal.jsonl", "archive-state.json"]
    if any(value and value in serialized for value in protected_values):
        raise SessionLifecycleConformanceError("session error leaked private context")


def validate_session_journal(
    state_root: Path,
    session_id: str,
    prompt: str,
    workspace: Path,
    validator: Any,
    canary: str,
) -> None:
    """功能：验证新 Session 位于固定 sessions 根且 journal portable、静止、零 secret。

    输入：共享 state root、ID、首条文本、workspace、Schema validator 与 canary。
    输出：布局和 durable journal 合法时无返回值。
    不变量：只接受 `stateRoot/sessions/<id>/journal.jsonl`，不从 stateRoot 其它目录猜 Session。
    失败：路径、Schema、重建状态、首条输入、unfinished run 或 canary 泄漏时拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    session_directory = state_root / "sessions" / session_id
    journal_path = session_directory / "journal.jsonl"
    if not session_directory.is_dir() or not journal_path.is_file():
        raise SessionLifecycleConformanceError("portable session layout is invalid")
    try:
        raw, values = session_validation.load_journal(journal_path, validator)
        state = session_validation.reconstruct_state(values)
    except session_validation.SessionValidationError as exc:
        raise SessionLifecycleConformanceError(
            "portable session journal is invalid"
        ) from exc
    if state.header.get("sessionId") != session_id:
        raise SessionLifecycleConformanceError(
            "portable journal session ID is inconsistent"
        )
    header_workspace = state.header.get("workspace")
    if not isinstance(header_workspace, str):
        raise SessionLifecycleConformanceError("portable journal workspace is invalid")
    try:
        if Path(header_workspace).resolve() != workspace.resolve():
            raise SessionLifecycleConformanceError(
                "portable journal workspace is inconsistent"
            )
    except OSError as exc:
        raise SessionLifecycleConformanceError(
            "portable journal workspace is invalid"
        ) from exc
    expected_input = {
        "role": "user",
        "content": [{"type": "text", "text": prompt}],
    }
    if not state.messages or state.messages[0] != expected_input:
        raise SessionLifecycleConformanceError(
            "portable journal lost its first user input"
        )
    if state.unfinished_run_ids:
        raise SessionLifecycleConformanceError(
            "portable journal retained an unfinished run"
        )
    if canary.encode("utf-8") in raw:
        raise SessionLifecycleConformanceError(
            "synthetic secret leaked to portable journal"
        )


def archive_ids(state_root: Path, *, required: bool) -> list[str]:
    """功能：严格读取共享 archive-state 文档并返回排序 ID 集合。

    输入：state root 与是否要求文档已 durable 存在。
    输出：缺失且非 required 时为空，否则为严格排序唯一 ID 列表。
    不变量：只接受 ADR 0030 两字段、Schema 0.1、有界普通文件。
    失败：缺失、链接、大小、JSON、字段或排序无效时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    path = state_root / "session-lifecycle" / "archive-state.json"
    if not path.exists():
        if required:
            raise SessionLifecycleConformanceError("archive state was not persisted")
        return []
    if not path.is_file() or path.is_symlink():
        raise SessionLifecycleConformanceError("archive state path is unsafe")
    try:
        raw = path.read_bytes()
    except OSError as exc:
        raise SessionLifecycleConformanceError("archive state is unreadable") from exc
    if not raw or len(raw) > 1_048_576:
        raise SessionLifecycleConformanceError("archive state size is invalid")
    try:
        document = runner.strict_json_loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, runner.ProtocolViolation) as exc:
        raise SessionLifecycleConformanceError("archive state JSON is invalid") from exc
    if (
        not isinstance(document, dict)
        or set(document) != {"schemaVersion", "archivedSessionIds"}
        or document.get("schemaVersion") != "0.1"
        or not isinstance(document.get("archivedSessionIds"), list)
    ):
        raise SessionLifecycleConformanceError("archive state fields are invalid")
    values = document["archivedSessionIds"]
    if (
        len(values) > MAX_SESSIONS
        or any(
            not isinstance(item, str) or OPAQUE_ID.fullmatch(item) is None
            for item in values
        )
        or values != sorted(set(values))
    ):
        raise SessionLifecycleConformanceError("archive state IDs are invalid")
    return list(values)


def assert_archive_membership(
    state_root: Path, session_id: str, archived: bool
) -> None:
    """功能：确认 archive 文档与 mutation 返回状态 durable 一致。

    输入：state root、目标 ID 与预期 archived 状态。
    输出：文档 membership 一致时无返回值。
    失败：归档未落盘或恢复/删除未清理 ID 时拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    ids = archive_ids(state_root, required=archived)
    if (session_id in ids) is not archived:
        raise SessionLifecycleConformanceError(
            "archive state membership is inconsistent"
        )


def assert_expected_summary(
    summaries: Sequence[JSONObject],
    session_id: str,
    prompt: str,
    project: str,
    archived: bool,
) -> JSONObject:
    """功能：在去重列表中取得目标摘要并验证 title/project/archive 投影。

    输入：完整摘要集合、目标 ID、首条文本、项目 basename 与归档状态。
    输出：唯一匹配摘要。
    失败：缺失、重复或摘要投影不一致时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    matches = [item for item in summaries if item["sessionId"] == session_id]
    if len(matches) != 1:
        raise SessionLifecycleConformanceError(
            "session list membership is inconsistent"
        )
    summary = matches[0]
    if (
        summary["title"] != prompt
        or summary["project"] != project
        or summary["archived"] is not archived
    ):
        raise SessionLifecycleConformanceError(
            "session summary projection is inconsistent"
        )
    return summary


def paginate_sessions(
    prefix: Sequence[str],
    runtime: str,
    workspace: Path,
    state_root: Path,
    schema_root: Path,
    environment_root: Path,
    canary: str,
    timeout: float,
    *,
    page_limit: int = 1,
) -> list[JSONObject]:
    """功能：在同一 daemon 中跟随 opaque cursor 直到显式 null 终页。

    输入：运行时、隔离路径、Schema、canary、deadline 与 1..128 页大小。
    输出：按服务顺序去重拼接的完整 Session 摘要。
    不变量：拒绝重复 cursor、空进度页、重复 ID、超限循环和超 maxFrame 响应。
    失败：任一分页、协议、Schema、输出或排序不变量无效时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if page_limit < 1 or page_limit > 128:
        raise SessionLifecycleConformanceError("pagination limit is invalid")
    removed, extra = scrubbed_environment(environment_root, canary)
    process = runner.DaemonProcess(
        daemon_command(prefix, runtime, workspace, state_root),
        timeout=timeout,
        max_frame_bytes=1_048_576,
        stderr_limit=131_072,
        extra_env=extra,
        removed_env=removed,
    )
    requests: list[JSONObject] = []
    frames: list[JSONObject] = []
    summaries: list[JSONObject] = []
    seen_ids: set[str] = set()
    seen_cursors: set[str] = set()
    try:
        initialize = initialize_request(f"{runtime}-page-initialize")
        requests.append(initialize)
        process.send_request(initialize)
        initialize_response = process.next_frame(timeout)
        frames.append(initialize_response)
        max_frame = validate_capabilities(
            result_for(frames, str(initialize["id"])), runtime
        )
        cursor: str | None = None
        for page_number in range(1, MAX_PAGES + 1):
            params: JSONObject = {"limit": page_limit}
            if cursor is not None:
                params["cursor"] = cursor
            request = rpc_request(
                f"{runtime}-page-{page_number}", "session/list", params
            )
            requests.append(request)
            process.send_request(request)
            response = process.next_frame(timeout)
            frames.append(response)
            if process.frame_wire_bytes(response) > max_frame:
                raise SessionLifecycleConformanceError(
                    "session/list response exceeded maxFrameBytes"
                )
            result = result_for(frames, str(request["id"]))
            page = validate_list_result(result, page_limit)
            for summary in page:
                session_id = str(summary["sessionId"])
                if session_id in seen_ids:
                    raise SessionLifecycleConformanceError(
                        "session/list repeated a session across pages"
                    )
                seen_ids.add(session_id)
                summaries.append(summary)
                if len(summaries) > MAX_SESSIONS:
                    raise SessionLifecycleConformanceError(
                        "session/list exceeded runner session budget"
                    )
            if result["hasMore"] is False:
                break
            cursor = str(result["nextCursor"])
            if cursor in seen_cursors:
                raise SessionLifecycleConformanceError(
                    "session/list repeated an opaque cursor"
                )
            seen_cursors.add(cursor)
        else:
            raise SessionLifecycleConformanceError(
                "session/list exceeded runner page budget"
            )
        process.close_stdin()
        process.close()
        stderr = process.stderr_text(limit=131_072)
        if process.process.returncode != 0:
            raise SessionLifecycleConformanceError(
                f"{runtime} pagination daemon did not exit cleanly"
            )
        validate_trace(requests, frames, schema_root, runtime)
        assert_canary_absent(canary, frames, stderr)
    except SessionLifecycleConformanceError:
        process.close()
        raise
    except (
        OSError,
        ValueError,
        runner.ConformanceError,
        session_validation.SessionValidationError,
    ) as exc:
        process.close()
        raise SessionLifecycleConformanceError(
            f"{runtime} pagination interaction failed"
        ) from exc
    expected_order = sorted(
        summaries,
        key=lambda item: (-parse_utc(item["updatedAt"]).timestamp(), item["sessionId"]),
    )
    if summaries != expected_order:
        raise SessionLifecycleConformanceError("session/list ordering is invalid")
    return summaries


def create_session(
    prefix: Sequence[str],
    runtime: str,
    workspace: Path,
    state_root: Path,
    schema_root: Path,
    environment_root: Path,
    canary: str,
    timeout: float,
    session_id: str,
    prompt: str,
    response_text: str,
    journal_validator: Any,
) -> None:
    """功能：通过 faux stdio run 创建并验证一个 quiescent portable Session。

    输入：运行时依赖、目标 ID、用户文本、确定性回复和 journal validator。
    输出：run 唯一终态且 durable journal 合法时无返回值。
    不变量：不调用任何 live Provider 或网络 endpoint。
    失败：协议、run、journal 布局、Schema 或零泄漏不变量失败时拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    requests = create_requests(
        runtime + "-" + session_id, session_id, prompt, response_text
    )
    execute_requests(
        prefix,
        runtime,
        workspace,
        state_root,
        requests,
        schema_root,
        environment_root,
        canary,
        timeout,
    )
    validate_session_journal(
        state_root,
        session_id,
        prompt,
        workspace,
        journal_validator,
        canary,
    )


def mutate_session(
    prefix: Sequence[str],
    runtime: str,
    workspace: Path,
    state_root: Path,
    schema_root: Path,
    environment_root: Path,
    canary: str,
    timeout: float,
    method: str,
    session_id: str,
) -> JSONObject:
    """功能：在 fresh daemon 中执行单个 archive/restore/delete mutation。

    输入：运行时依赖、冻结 method 与目标 Session ID。
    输出：验证轨迹后的 mutation result。
    失败：未知方法或 daemon 失败时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if method not in {"session/archive", "session/restore", "session/delete"}:
        raise SessionLifecycleConformanceError("session mutation method is invalid")
    request_id = runtime + "-" + method.removeprefix("session/")
    requests = [
        initialize_request(runtime + "-mutation-initialize"),
        rpc_request(request_id, method, {"sessionId": session_id}),
    ]
    frames = execute_requests(
        prefix,
        runtime,
        workspace,
        state_root,
        requests,
        schema_root,
        environment_root,
        canary,
        timeout,
    )
    return result_for(frames, request_id)


def assert_missing_error(
    prefix: Sequence[str],
    runtime: str,
    workspace: Path,
    state_root: Path,
    schema_root: Path,
    environment_root: Path,
    canary: str,
    timeout: float,
    session_id: str,
    private_values: Sequence[str] = (),
) -> None:
    """功能：对已删除 ID 执行 archive 并验证两端相同的脱敏不存在错误。

    输入：运行时依赖、已删除的公开 Session ID 与不得回显的用户文本。
    输出：error shape 和隐私扫描通过时无返回值。
    失败：意外成功、错误 kind、路径、journal、transcript 或 secret 泄漏时拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    request_id = runtime + "-missing-archive"
    requests = [
        initialize_request(runtime + "-missing-initialize"),
        rpc_request(request_id, "session/archive", {"sessionId": session_id}),
    ]
    frames = execute_requests(
        prefix,
        runtime,
        workspace,
        state_root,
        requests,
        schema_root,
        environment_root,
        canary,
        timeout,
    )
    response = response_for(frames, request_id)
    if "result" in response:
        raise SessionLifecycleConformanceError("missing Session mutation succeeded")
    validate_redacted_error(
        response,
        request_id,
        session_id,
        [str(workspace), str(state_root), canary, *private_values],
    )


def exercise(
    rust: Sequence[str],
    dotnet: Sequence[str],
    root: Path,
    schema_root: Path,
    timeout: float,
) -> JSONObject:
    """功能：执行 Rust→.NET 与 .NET→Rust 两个完整生命周期方向。

    输入：两个 CLI 前缀、隔离根、公共 Schema 与 deadline。
    输出：稳定的案例、方向、分页和 Provider HTTP 配置状态。
    不变量：两端共享同一 `stateRoot/sessions` 与 archive-state 文档，只使用 faux，且不配置 Provider HTTP。
    失败：任一创建、摘要、归档、恢复、删除、分页或隐私断言失败即终止。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    workspace = root / "workspace"
    state_root = root / "state"
    environment_root = root / "environment"
    workspace.mkdir(parents=True, mode=0o700, exist_ok=False)
    state_root.mkdir(parents=True, mode=0o700, exist_ok=False)
    environment_root.mkdir(parents=True, mode=0o700, exist_ok=False)
    canary = "session-lifecycle-" + secrets.token_hex(24)
    journal_validator = session_validation.build_line_validator(schema_root)
    rust_id = "lifecycle-rust-portable-1"
    rust_prompt = "Rust portable lifecycle handoff"
    dotnet_id = "lifecycle-dotnet-portable-1"
    dotnet_prompt = "Dotnet portable lifecycle handoff"
    companion_id = "lifecycle-dotnet-pagination-2"
    companion_prompt = "Dotnet pagination companion"
    project = workspace.name

    create_session(
        rust,
        "rust",
        workspace,
        state_root,
        schema_root,
        environment_root,
        canary,
        timeout,
        rust_id,
        rust_prompt,
        "Rust-created Session is ready for .NET.",
        journal_validator,
    )
    dotnet_summaries = paginate_sessions(
        dotnet,
        "dotnet",
        workspace,
        state_root,
        schema_root,
        environment_root,
        canary,
        timeout,
    )
    assert_expected_summary(
        dotnet_summaries, rust_id, rust_prompt, project, archived=False
    )
    archived = mutate_session(
        dotnet,
        "dotnet",
        workspace,
        state_root,
        schema_root,
        environment_root,
        canary,
        timeout,
        "session/archive",
        rust_id,
    )
    validate_summary_result(archived, rust_id, archived=True)
    assert_archive_membership(state_root, rust_id, archived=True)

    rust_summaries = paginate_sessions(
        rust,
        "rust",
        workspace,
        state_root,
        schema_root,
        environment_root,
        canary,
        timeout,
    )
    assert_expected_summary(
        rust_summaries, rust_id, rust_prompt, project, archived=True
    )
    restored = mutate_session(
        rust,
        "rust",
        workspace,
        state_root,
        schema_root,
        environment_root,
        canary,
        timeout,
        "session/restore",
        rust_id,
    )
    validate_summary_result(restored, rust_id, archived=False)
    assert_archive_membership(state_root, rust_id, archived=False)

    dotnet_summaries = paginate_sessions(
        dotnet,
        "dotnet",
        workspace,
        state_root,
        schema_root,
        environment_root,
        canary,
        timeout,
    )
    assert_expected_summary(
        dotnet_summaries, rust_id, rust_prompt, project, archived=False
    )
    deleted = mutate_session(
        dotnet,
        "dotnet",
        workspace,
        state_root,
        schema_root,
        environment_root,
        canary,
        timeout,
        "session/delete",
        rust_id,
    )
    validate_delete_result(deleted)
    assert_archive_membership(state_root, rust_id, archived=False)
    if (state_root / "sessions" / rust_id).exists():
        raise SessionLifecycleConformanceError("deleted Rust Session still exists")
    rust_summaries = paginate_sessions(
        rust,
        "rust",
        workspace,
        state_root,
        schema_root,
        environment_root,
        canary,
        timeout,
    )
    if any(item["sessionId"] == rust_id for item in rust_summaries):
        raise SessionLifecycleConformanceError("Rust listed a Session deleted by .NET")
    assert_missing_error(
        rust,
        "rust",
        workspace,
        state_root,
        schema_root,
        environment_root,
        canary,
        timeout,
        rust_id,
        [rust_prompt, "Rust-created Session is ready for .NET."],
    )

    create_session(
        dotnet,
        "dotnet",
        workspace,
        state_root,
        schema_root,
        environment_root,
        canary,
        timeout,
        dotnet_id,
        dotnet_prompt,
        ".NET-created Session is ready for Rust.",
        journal_validator,
    )
    create_session(
        dotnet,
        "dotnet",
        workspace,
        state_root,
        schema_root,
        environment_root,
        canary,
        timeout,
        companion_id,
        companion_prompt,
        "Pagination companion completed.",
        journal_validator,
    )
    rust_summaries = paginate_sessions(
        rust,
        "rust",
        workspace,
        state_root,
        schema_root,
        environment_root,
        canary,
        timeout,
    )
    if len(rust_summaries) != 2:
        raise SessionLifecycleConformanceError(
            "Rust pagination did not traverse two Sessions"
        )
    assert_expected_summary(
        rust_summaries, dotnet_id, dotnet_prompt, project, archived=False
    )
    assert_expected_summary(
        rust_summaries, companion_id, companion_prompt, project, archived=False
    )
    archived = mutate_session(
        rust,
        "rust",
        workspace,
        state_root,
        schema_root,
        environment_root,
        canary,
        timeout,
        "session/archive",
        dotnet_id,
    )
    validate_summary_result(archived, dotnet_id, archived=True)
    assert_archive_membership(state_root, dotnet_id, archived=True)

    dotnet_summaries = paginate_sessions(
        dotnet,
        "dotnet",
        workspace,
        state_root,
        schema_root,
        environment_root,
        canary,
        timeout,
    )
    if len(dotnet_summaries) != 2:
        raise SessionLifecycleConformanceError(
            ".NET pagination did not traverse two Sessions"
        )
    assert_expected_summary(
        dotnet_summaries, dotnet_id, dotnet_prompt, project, archived=True
    )
    restored = mutate_session(
        dotnet,
        "dotnet",
        workspace,
        state_root,
        schema_root,
        environment_root,
        canary,
        timeout,
        "session/restore",
        dotnet_id,
    )
    validate_summary_result(restored, dotnet_id, archived=False)
    assert_archive_membership(state_root, dotnet_id, archived=False)

    rust_summaries = paginate_sessions(
        rust,
        "rust",
        workspace,
        state_root,
        schema_root,
        environment_root,
        canary,
        timeout,
    )
    assert_expected_summary(
        rust_summaries, dotnet_id, dotnet_prompt, project, archived=False
    )
    deleted = mutate_session(
        rust,
        "rust",
        workspace,
        state_root,
        schema_root,
        environment_root,
        canary,
        timeout,
        "session/delete",
        dotnet_id,
    )
    validate_delete_result(deleted)
    assert_archive_membership(state_root, dotnet_id, archived=False)
    if (state_root / "sessions" / dotnet_id).exists():
        raise SessionLifecycleConformanceError("deleted .NET Session still exists")
    dotnet_summaries = paginate_sessions(
        dotnet,
        "dotnet",
        workspace,
        state_root,
        schema_root,
        environment_root,
        canary,
        timeout,
    )
    if any(item["sessionId"] == dotnet_id for item in dotnet_summaries) or {
        item["sessionId"] for item in dotnet_summaries
    } != {companion_id}:
        raise SessionLifecycleConformanceError(".NET listed a Session deleted by Rust")
    assert_missing_error(
        dotnet,
        "dotnet",
        workspace,
        state_root,
        schema_root,
        environment_root,
        canary,
        timeout,
        dotnet_id,
        [dotnet_prompt, ".NET-created Session is ready for Rust."],
    )
    return {
        "cases": 16,
        "crossRuntimeDirections": 2,
        "paginatedRuntimes": 2,
        "providerHttpConfigured": False,
    }


def run_with_root(args: argparse.Namespace, root: Path) -> JSONObject:
    """功能：解析命令并在调用方指定的空证据根执行完整门禁。

    输入：CLI 参数与必须为空的 root。
    输出：`exercise` 的稳定统计对象。
    不变量：不覆盖已有文件，防止测试状态与用户状态混合。
    失败：root 非目录、非空或命令无效时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    root.mkdir(parents=True, mode=0o700, exist_ok=True)
    if any(root.iterdir()):
        raise SessionLifecycleConformanceError("work root must be empty")
    rust = parse_command(args.rust_command_json, "Rust")
    dotnet = parse_command(args.dotnet_command_json, ".NET")
    return exercise(rust, dotnet, root, args.schema_root.resolve(), args.timeout)


def main(argv: Sequence[str] | None = None) -> int:
    """功能：运行隔离双向门禁并只输出不含路径或 secret 的稳定 JSON。

    输入：可选 CLI argv。
    输出：通过为 0，任一失败为 1。
    不变量：失败输出只含固定类别，不复述 argv、路径、stderr、journal 或 canary。
    失败：所有受控门禁错误都转换为脱敏 JSON 结果。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = parse_args(argv)
    if args.timeout <= 0 or args.timeout > 120:
        print(json.dumps({"status": "failed", "error": "invalid runner limits"}))
        return 1
    os.umask(0o077)
    try:
        if args.work_root is not None:
            result = run_with_root(args, args.work_root.resolve())
        else:
            with tempfile.TemporaryDirectory(
                prefix="qxnm-forge-session-lifecycle-"
            ) as temporary:
                result = run_with_root(args, Path(temporary))
    except (
        OSError,
        ValueError,
        runner.ConformanceError,
        session_validation.SessionValidationError,
        SessionLifecycleConformanceError,
    ):
        print(
            json.dumps(
                {"status": "failed", "error": "session lifecycle conformance failed"},
                sort_keys=True,
            )
        )
        return 1
    print(json.dumps({"status": "passed", **result}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
