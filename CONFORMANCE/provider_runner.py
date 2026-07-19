#!/usr/bin/env python3
"""九类 Provider 分批静态 mock 门禁与可选 daemon 黑盒探针。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import queue
import secrets
import sys
import tempfile
import time
from typing import Sequence

import provider_mock
import runner
import schema_validation
import session_validation


JSONValue = (
    type(None) | bool | int | float | str | list["JSONValue"] | dict[str, "JSONValue"]
)
JSONObject = dict[str, JSONValue]

CREDENTIAL_ENV = (
    "OPENAI_API_KEY",
    "ANTHROPIC_API_KEY",
    "AZURE_OPENAI_API_KEY",
    "AZURE_CLIENT_SECRET",
    "AWS_ACCESS_KEY_ID",
    "AWS_BEARER_TOKEN_BEDROCK",
    "AWS_CONTAINER_CREDENTIALS_FULL_URI",
    "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI",
    "AWS_DEFAULT_REGION",
    "AWS_EC2_METADATA_DISABLED",
    "AWS_PROFILE",
    "AWS_REGION",
    "AWS_SECRET_ACCESS_KEY",
    "AWS_SESSION_TOKEN",
    "AWS_WEB_IDENTITY_TOKEN_FILE",
    "GOOGLE_API_KEY",
    "GEMINI_API_KEY",
    "GOOGLE_APPLICATION_CREDENTIALS",
    "GOOGLE_CLOUD_API_KEY",
    "GOOGLE_CLOUD_LOCATION",
    "GOOGLE_CLOUD_PROJECT",
    "GCLOUD_PROJECT",
    "MISTRAL_API_KEY",
    "OPENROUTER_API_KEY",
    "GITHUB_TOKEN",
    "GH_TOKEN",
    "QXNM_FORGE_GOOGLE_VERTEX_OAUTH_TOKEN",
    "QXNM_FORGE_OPENAI_CODEX_OAUTH_TOKEN",
)

ENDPOINT_ENV = (
    "QXNM_FORGE_OPENAI_COMPATIBLE_ENDPOINT",
    "QXNM_FORGE_OPENAI_RESPONSES_ENDPOINT",
    "QXNM_FORGE_ANTHROPIC_ENDPOINT",
    "QXNM_FORGE_MISTRAL_CONVERSATIONS_ENDPOINT",
    "QXNM_FORGE_AZURE_OPENAI_RESPONSES_ENDPOINT",
    "QXNM_FORGE_GOOGLE_GENERATIVE_AI_ENDPOINT",
    "QXNM_FORGE_GOOGLE_VERTEX_ENDPOINT",
    "QXNM_FORGE_BEDROCK_CONVERSE_STREAM_ENDPOINT",
    "QXNM_FORGE_OPENAI_CODEX_RESPONSES_ENDPOINT",
)

PROXY_ENV = (
    "HTTP_PROXY",
    "HTTPS_PROXY",
    "ALL_PROXY",
    "NO_PROXY",
    "http_proxy",
    "https_proxy",
    "all_proxy",
    "no_proxy",
)


class ProviderProbeError(Exception):
    """表示静态 mock 或实现黑盒探针没有满足公共契约。"""


def parse_command_json(value: str) -> list[str]:
    """功能：把 JSON 字符串安全解析为不经过 shell 的非空 argv。

    输入：命令行参数中的 JSON 数组文本。
    输出：最多 128 项、每项最多 4096 字符且不含 NUL 的字符串 argv。
    不变量：不执行 shell 展开，也不在错误中回显输入文本或任一参数。
    失败：类型、数量或字符限制不合法时抛出 argparse.ArgumentTypeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        parsed = json.loads(value)
    except json.JSONDecodeError as exc:
        raise argparse.ArgumentTypeError(
            "daemon command must be a JSON argv array"
        ) from exc
    if (
        not isinstance(parsed, list)
        or not parsed
        or len(parsed) > 128
        or not all(
            isinstance(item, str) and item and len(item) <= 4096 and "\x00" not in item
            for item in parsed
        )
    ):
        raise argparse.ArgumentTypeError(
            "daemon command must be a bounded JSON argv array"
        )
    return parsed


def _expand_probe_command(
    command: Sequence[str], workspace: Path, state_root: Path
) -> list[str]:
    """功能：在安全 argv 项内替换 runner 分配的 workspace/stateRoot 占位符。

    输入：未经 shell 的 daemon argv、每案例独占工作区和状态根绝对路径。
    输出：逐项字面替换 `{workspace}`/`{stateRoot}` 后的新 argv。
    不变量：不执行 shell、环境或格式字符串展开；每个占位符最多展开为 runner 路径。
    失败：命令为空、路径非绝对或替换后参数越界时抛出 ProviderProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not command or not workspace.is_absolute() or not state_root.is_absolute():
        raise ProviderProbeError(
            "probe command template or allocated paths are invalid"
        )
    expanded = [
        item.replace("{workspace}", str(workspace)).replace(
            "{stateRoot}", str(state_root)
        )
        for item in command
    ]
    if any(not item or len(item) > 4096 or "\x00" in item for item in expanded):
        raise ProviderProbeError("expanded daemon command exceeds argv limits")
    return expanded


def _prepare_probe_directories(root: Path) -> tuple[Path, Path]:
    """功能：创建每案例独占工作区/状态根并写入固定可读 README 工具夹具。

    输入：`TemporaryDirectory` 创建的绝对探针根。
    输出：绝对 workspace 与 stateRoot 路径。
    不变量：只在探针根内创建目录；README 内容固定、不含环境、凭据或外部正文。
    失败：路径边界或本地文件系统操作失败时抛出脱敏 ProviderProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not root.is_absolute():
        raise ProviderProbeError("probe temporary root must be absolute")
    workspace = root / "workspace"
    state_root = root / "state"
    try:
        workspace.mkdir(mode=0o700)
        state_root.mkdir(mode=0o700)
        (workspace / "README.md").write_text(
            "provider tool continuation fixture\n",
            encoding="utf-8",
            newline="\n",
        )
    except OSError as exc:
        raise ProviderProbeError(
            "cannot prepare isolated provider probe paths"
        ) from exc
    if workspace.parent != root or state_root.parent != root:
        raise ProviderProbeError("probe paths escaped the temporary root")
    return workspace, state_root


def validate_fixture_schema(fixture_path: Path, schema_root: Path) -> None:
    """功能：使用可选 Draft 2020-12 引擎验证 Provider mock 机器夹具。

    输入：夹具路径和本地 SPEC/schemas 根目录。
    输出：Schema 与实例均有效时无返回值。
    不变量：只读取 bundled Schema，绝不解析远程引用或访问网络。
    失败：缺少引擎、文件、无效 Schema 或实例违规时抛出 ProviderProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        import jsonschema
    except ImportError as exc:
        raise ProviderProbeError(
            "--schema-root requires the optional jsonschema package"
        ) from exc
    schema_path = schema_root / "provider-mock-cases.schema.json"
    try:
        schema = json.loads(schema_path.read_text(encoding="utf-8"))
        fixture = json.loads(fixture_path.read_text(encoding="utf-8"))
        jsonschema.Draft202012Validator.check_schema(schema)
        jsonschema.Draft202012Validator(schema).validate(fixture)
    except (OSError, json.JSONDecodeError, jsonschema.SchemaError) as exc:
        raise ProviderProbeError("Provider mock Schema 无法加载或无效") from exc
    except jsonschema.ValidationError as exc:
        location = "/".join(str(part) for part in exc.absolute_path) or "<root>"
        raise ProviderProbeError(
            f"Provider mock fixture Schema 违规于 {location}"
        ) from exc


def run_static_gate(fixture_path: Path, schema_root: Path | None) -> tuple[int, int]:
    """功能：执行无网络、无 daemon、无凭据的 Provider mock 静态门禁。

    输入：机器夹具路径及可选 bundled Schema 根目录。
    输出：通过校验的 family 数和案例总数。
    不变量：不启动监听、不生成凭据、不修改 capability 状态。
    失败：夹具语义、Schema 或 wire 渲染失败时抛出 ProviderProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        counts = provider_mock.static_self_test(fixture_path)
        if schema_root is not None:
            validate_fixture_schema(fixture_path, schema_root)
        return counts
    except provider_mock.ProviderMockError as exc:
        raise ProviderProbeError(str(exc)) from exc


def _initialize_request() -> JSONObject:
    """功能：构造 Provider 探针使用的语言中立 initialize 请求。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": "provider-initialize-1",
        "method": "initialize",
        "params": {
            "protocolVersions": ["0.1"],
            "client": {"name": "provider-conformance-runner", "version": "0.1.0"},
            "capabilities": {},
        },
    }


def _run_start_request(family: JSONObject) -> JSONObject:
    """功能：按 family 构造固定 prompt、model 和 provider 的 run/start 请求。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    provider_id = family.get("providerId")
    model_id = family.get("modelId")
    if not isinstance(provider_id, str) or not isinstance(model_id, str):
        raise ProviderProbeError("Provider family 缺少 providerId/modelId")
    return {
        "jsonrpc": "2.0",
        "id": "provider-run-start-1",
        "method": "run/start",
        "params": {
            "sessionId": "provider-conformance-session-1",
            "input": {
                "role": "user",
                "content": [
                    {
                        "type": "text",
                        "text": "Return the deterministic local mock response.",
                    }
                ],
            },
            "provider": {"id": provider_id, "modelId": model_id},
        },
    }


def _cancel_request(run_id: str) -> JSONObject:
    """功能：用已接受的 opaque run ID 构造幂等 run/cancel 请求。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": "provider-run-cancel-1",
        "method": "run/cancel",
        "params": {
            "sessionId": "provider-conformance-session-1",
            "runId": run_id,
        },
    }


def _await_response(
    process: runner.DaemonProcess,
    request_id: str,
    frames: list[JSONObject],
    *,
    timeout: float,
    allow_events: bool,
) -> JSONObject:
    """功能：在超时内收集帧直到目标响应，并拒绝提前事件或错误响应。

    输入：daemon、目标 ID、累计帧、超时和是否允许并发事件。
    输出：目标成功响应对象。
    不变量：不解析错误 message；只按结构化响应位置、code 和 retryable 判断。
    失败：超时、未知响应、提前事件或 JSON-RPC error 时抛出 ProviderProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    deadline = time.monotonic() + timeout
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ProviderProbeError("daemon response timed out")
        try:
            frame = process.next_frame(remaining)
        except queue.Empty as exc:
            raise ProviderProbeError("daemon response timed out") from exc
        frames.append(frame)
        if frame.get("method") == "event":
            if not allow_events:
                raise ProviderProbeError(
                    "daemon emitted an event before required response"
                )
            continue
        if frame.get("id") != request_id:
            raise ProviderProbeError("daemon returned an unexpected response id")
        error = frame.get("error")
        if isinstance(error, dict):
            code = error.get("code")
            retryable = error.get("retryable")
            raise ProviderProbeError(
                f"daemon returned JSON-RPC error code={code!r}, retryable={retryable!r}"
            )
        if not isinstance(frame.get("result"), dict):
            raise ProviderProbeError("daemon success response result must be an object")
        return frame


def _terminal_frame(frames: Sequence[JSONObject], run_id: str) -> JSONObject | None:
    """功能：从累计帧查找指定 run 的唯一终态事件。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    terminals: list[JSONObject] = []
    for frame in frames:
        params = frame.get("params")
        if (
            frame.get("method") == "event"
            and isinstance(params, dict)
            and params.get("runId") == run_id
            and params.get("type") in runner.TERMINAL_EVENTS
        ):
            terminals.append(frame)
    if len(terminals) > 1:
        raise ProviderProbeError("daemon emitted multiple terminal events")
    return terminals[0] if terminals else None


def _await_terminal(
    process: runner.DaemonProcess,
    run_id: str,
    frames: list[JSONObject],
    timeout: float,
) -> JSONObject:
    """功能：在总超时内收集指定 run 的唯一终态事件。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    terminal = _terminal_frame(frames, run_id)
    if terminal is not None:
        return terminal
    deadline = time.monotonic() + timeout
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ProviderProbeError("daemon terminal event timed out")
        try:
            frames.append(process.next_frame(remaining))
        except queue.Empty as exc:
            raise ProviderProbeError("daemon terminal event timed out") from exc
        terminal = _terminal_frame(frames, run_id)
        if terminal is not None:
            return terminal


def _await_first_mock_request(
    process: runner.DaemonProcess,
    server: provider_mock.ProviderMockServer,
    frames: list[JSONObject],
    *,
    start_observation: int,
    family_id: str,
    case_name: str,
    run_id: str,
    timeout: float,
) -> None:
    """功能：取消前有界等待 mock 确认首个目标 HTTP 请求已经到达。

    输入：daemon/mock、累计帧、观测起点、目标 family/case/run 与超时。
    输出：发现目标请求观测时无返回值。
    不变量：等待期间继续排空协议事件；不读取或记录认证值、请求体或环境。
    失败：请求前出现终态、daemon 退出或到期仍无目标观测时抛出安全错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    deadline = time.monotonic() + timeout
    while True:
        observations = server.state.observations()[start_observation:]
        if any(
            observation.get("family") == family_id
            and observation.get("case") == case_name
            for observation in observations
        ):
            return
        terminal = _terminal_frame(frames, run_id)
        if terminal is not None:
            raise ProviderProbeError(
                "run became terminal before provider request arrived"
            )
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ProviderProbeError(
                "provider request did not reach the local mock in time"
            )
        try:
            frames.append(process.next_frame(min(0.02, remaining)))
        except queue.Empty:
            continue


def _assert_provider_advertised(initialize: JSONObject, provider_id: str) -> None:
    """功能：确认 initialize 真实声明了本探针即将调用的 Provider ID。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    result = initialize.get("result")
    capabilities = result.get("capabilities") if isinstance(result, dict) else None
    providers = (
        capabilities.get("providers") if isinstance(capabilities, dict) else None
    )
    if not isinstance(providers, list) or not any(
        isinstance(value, dict) and value.get("id") == provider_id
        for value in providers
    ):
        raise ProviderProbeError("daemon did not advertise the exercised provider")


def _event_data(frame: JSONObject) -> JSONObject | None:
    """功能：从 event notification 安全提取 JSON 对象 data。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    params = frame.get("params")
    if frame.get("method") != "event" or not isinstance(params, dict):
        return None
    data = params.get("data")
    return data if isinstance(data, dict) else None


def _event_type(frame: JSONObject) -> str | None:
    """功能：从 event notification 安全提取公共事件类型。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    params = frame.get("params")
    value = params.get("type") if isinstance(params, dict) else None
    return value if frame.get("method") == "event" and isinstance(value, str) else None


def _verify_text(frames: Sequence[JSONObject], expected: str) -> None:
    """功能：按事件顺序重组所有 text delta 并与机器夹具精确比较。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    pieces: list[str] = []
    for frame in frames:
        if _event_type(frame) != "message.delta":
            continue
        data = _event_data(frame)
        delta = data.get("delta") if isinstance(data, dict) else None
        text = delta.get("text") if isinstance(delta, dict) else None
        if isinstance(text, str):
            pieces.append(text)
    if "".join(pieces) != expected:
        raise ProviderProbeError("normalized text deltas differ from fixture")


def _verify_tool_call(frames: Sequence[JSONObject], expected: JSONObject) -> None:
    """功能：确认 partial arguments 被重组为一个完整且精确的 tool.requested。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    requested = [
        _event_data(frame) for frame in frames if _event_type(frame) == "tool.requested"
    ]
    requested = [value for value in requested if value is not None]
    if len(requested) != 1:
        raise ProviderProbeError("expected exactly one tool.requested event")
    actual = requested[0]
    if (
        actual.get("toolCallId") != expected.get("id")
        or actual.get("name") != expected.get("name")
        or actual.get("arguments") != expected.get("arguments")
    ):
        raise ProviderProbeError("reassembled tool call differs from fixture")


def _tool_result_matches(result: JSONObject, expected: JSONObject) -> bool:
    """功能：确认公共 ToolResult 精确等于固定 README 规范化成功结果。

    输入：已经通过协议 Schema 或待验证的 ToolResult 对象及机器期望。
    输出：content 与 isError 均逐结构精确相同时返回 true。
    不变量：只返回布尔值，不复制或记录工具输出正文；额外结果字段不影响比较。
    失败：结构不完整时返回 false，不抛出含正文的异常。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return (
        result.get("content") == expected.get("content")
        and result.get("isError") is False
        and expected.get("isError") is False
    )


def _verify_tool_lifecycle(
    frames: Sequence[JSONObject],
    expected: JSONObject,
    expected_result: JSONObject,
) -> None:
    """功能：精确验证工具请求、真实执行、成功完成与第二轮文本终态顺序。

    输入：完整协议帧、固定 toolCall 与规范化 ToolResult 期望。
    输出：唯一调用按 requested→started→completed→text→run.completed 完成时无返回值。
    不变量：ID/name/arguments 必须与原生首轮一致；ToolResult 必须精确等于固定成功结果。
    失败：数量、关联、顺序或结果状态不一致时抛出脱敏 ProviderProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    typed = [
        (index, _event_type(frame), _event_data(frame))
        for index, frame in enumerate(frames)
        if _event_type(frame) is not None
    ]

    def matches(event_type: str) -> list[tuple[int, JSONObject]]:
        """功能：返回指定事件类型及其对象 data 的有序位置。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        return [
            (index, data)
            for index, actual_type, data in typed
            if actual_type == event_type and isinstance(data, dict)
        ]

    requested = matches("tool.requested")
    started = matches("tool.started")
    completed = matches("tool.completed")
    deltas = matches("message.delta")
    terminals = matches("run.completed")
    if not all(
        len(values) == 1 for values in (requested, started, completed, terminals)
    ):
        raise ProviderProbeError("tool continuation lifecycle event counts differ")
    if not deltas:
        raise ProviderProbeError("tool continuation final text delta is missing")

    requested_index, requested_data = requested[0]
    started_index, started_data = started[0]
    completed_index, completed_data = completed[0]
    terminal_index, _ = terminals[0]
    first_delta_index = deltas[0][0]
    expected_id = expected.get("id")
    expected_name = expected.get("name")
    if (
        requested_data.get("toolCallId") != expected_id
        or requested_data.get("name") != expected_name
        or requested_data.get("arguments") != expected.get("arguments")
        or started_data.get("toolCallId") != expected_id
        or started_data.get("name") != expected_name
        or completed_data.get("toolCallId") != expected_id
    ):
        raise ProviderProbeError("tool continuation lifecycle correlation differs")
    result = completed_data.get("result")
    if not isinstance(result, dict) or not _tool_result_matches(
        result, expected_result
    ):
        raise ProviderProbeError(
            "tool continuation did not produce a successful text result"
        )
    if not (
        requested_index
        < started_index
        < completed_index
        < first_delta_index
        < terminal_index
    ):
        raise ProviderProbeError("tool continuation lifecycle order differs")


def _verify_usage(frames: Sequence[JSONObject], expected: JSONObject) -> None:
    """功能：确认唯一 run 终态携带与夹具一致的累计 normalized usage。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    usages: list[JSONObject] = []
    for frame in frames:
        if _event_type(frame) not in runner.TERMINAL_EVENTS:
            continue
        data = _event_data(frame)
        usage = data.get("usage") if isinstance(data, dict) else None
        if isinstance(usage, dict):
            usages.append(usage)
    if len(usages) != 1 or usages[0] != expected:
        raise ProviderProbeError("terminal cumulative usage differs from fixture")


def verify_trace(
    requests: Sequence[JSONObject],
    frames: Sequence[JSONObject],
    family: JSONObject,
    case: JSONObject,
    *,
    schema_root: Path | None = None,
) -> None:
    """功能：验证公共协议、终态、事件、文本/工具参数和 usage 预期。

    输入：实际请求/帧、选中的 family/case 机器夹具及可选 Schema 根目录。
    输出：全部黑盒语义一致时无返回值。
    不变量：不按错误 message 分支，不改变或提升 capability 状态。
    失败：协议或任一期望不匹配时抛出 ProviderProbeError/ProtocolViolation。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if schema_root is not None:
        try:
            schema_validation.validate_protocol_trace(requests, frames, schema_root)
        except schema_validation.SchemaValidationError as exc:
            raise ProviderProbeError(
                "actual provider protocol trace violates bundled Schema"
            ) from exc
    runner.TraceValidator(requests).validate(frames)
    expected_value = case.get("expected")
    if not isinstance(expected_value, dict):
        raise ProviderProbeError("case.expected must be an object")
    expected = expected_value
    event_types = [value for value in (_event_type(frame) for frame in frames) if value]
    required_types = expected.get("requiredEventTypes")
    if not isinstance(required_types, list) or not all(
        isinstance(value, str) for value in required_types
    ):
        raise ProviderProbeError("case expected event types are invalid")
    missing = sorted(set(required_types) - set(event_types))
    if missing:
        raise ProviderProbeError(f"required normalized events missing: {missing!r}")
    terminal = expected.get("terminal")
    if f"run.{terminal}" not in event_types:
        raise ProviderProbeError("terminal event differs from fixture")
    text = expected.get("text")
    if isinstance(text, str):
        _verify_text(frames, text)
    tool_call = expected.get("toolCall")
    if isinstance(tool_call, dict):
        _verify_tool_call(frames, tool_call)
        if (
            family.get("id") in provider_mock.BASELINE_FAMILIES
            and case.get("name") == "partial_tool_arguments"
        ):
            tool_result = expected.get("toolResult")
            if not isinstance(tool_result, dict):
                raise ProviderProbeError("tool continuation result fixture is invalid")
            _verify_tool_lifecycle(frames, tool_call, tool_result)
    usage = expected.get("usage")
    if isinstance(usage, dict):
        _verify_usage(frames, usage)

    provider_id = family.get("providerId")
    if not isinstance(provider_id, str):
        raise ProviderProbeError("family.providerId must be a string")
    _assert_provider_advertised(frames[0], provider_id)


def _assert_canary_absent(label: str, data: bytes, canary: str) -> None:
    """功能：断言一个有界输出中不存在运行时 canary 且错误不回显 canary。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if canary.encode("utf-8") in data:
        raise ProviderProbeError(f"credential canary leaked into {label}")


def _scan_tree_for_canary(root: Path, canary: str) -> None:
    """功能：有界扫描 runner 分配的 session 根目录以阻止凭据持久化。

    输入：仅由本探针创建的临时目录与内存 canary。
    输出：未发现 canary 时无返回值。
    不变量：不跟随符号链接，单文件上限 4 MiB、总读取上限 16 MiB。
    失败：发现 canary、无法读取普通文件或超过上限时抛出 ProviderProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    total = 0
    for path in sorted(root.rglob("*")):
        if path.is_symlink() or not path.is_file():
            continue
        try:
            size = path.stat().st_size
        except OSError as exc:
            raise ProviderProbeError(
                "cannot inspect conformance session artifact"
            ) from exc
        if size > 4_194_304 or total + size > 16_777_216:
            raise ProviderProbeError("conformance session artifact scan limit exceeded")
        try:
            data = path.read_bytes()
        except OSError as exc:
            raise ProviderProbeError(
                "cannot read conformance session artifact"
            ) from exc
        total += len(data)
        _assert_canary_absent("session artifact", data, canary)


def _safe_journal_location(root: Path, path: Path, line_number: int) -> str:
    """功能：生成只含 runner 内相对路径和行号的 journal 安全定位文本。

    输入：runner 临时根、其下的 journal 路径和一基行号。
    输出：不包含 journal 实例内容的 POSIX 相对路径与行号。
    不变量：绝不返回临时根外的绝对路径或任意 journal 字段值。
    失败：路径不位于临时根内时抛出 ProviderProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        relative = path.relative_to(root)
    except ValueError as exc:
        raise ProviderProbeError("journal path escaped the probe session root") from exc
    return f"{relative.as_posix()}:{line_number}"


def _safe_schema_instance_path(error: object) -> str:
    """功能：把 Schema 实例路径脱敏为层级形状而不暴露字段名或值。

    输入：jsonschema ValidationError 兼容对象。
    输出：仅保留数组下标并把所有对象键替换为 `<field>` 的路径。
    不变量：不复制 error.message、实例值或可能由实例控制的属性名。
    失败：路径不可迭代时返回 `<root>`，不传播第三方异常。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        parts = list(getattr(error, "absolute_path"))
    except (AttributeError, TypeError):
        return "<root>"
    if not parts:
        return "<root>"
    return "/".join(str(part) if isinstance(part, int) else "<field>" for part in parts)


def validate_probe_journals(root: Path, schema_root: Path) -> int:
    """功能：递归验证探针临时根内所有原生 Session journal 行。

    输入：runner 独占的临时 Session 根和 bundled Schema 根目录。
    输出：通过严格 UTF-8、LF、JSON 与 Session Schema 校验的 journal 文件数。
    不变量：不跟随符号链接；单文件最多 4 MiB、总计最多 16 MiB；诊断不回显行内容或字段值。
    失败：Schema 无法准备、journal 读取/编码/边界/JSON/Schema 违规时抛出脱敏 ProviderProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        validator = session_validation.build_line_validator(schema_root)
    except Exception as exc:
        raise ProviderProbeError(
            "cannot prepare bundled Session journal Schema"
        ) from exc

    total_size = 0
    journal_count = 0
    for path in sorted(root.rglob("journal.jsonl")):
        if path.is_symlink():
            continue
        try:
            if not path.is_file():
                continue
            size = path.stat().st_size
        except OSError as exc:
            raise ProviderProbeError(
                "cannot inspect conformance Session journal"
            ) from exc
        if size > 4_194_304 or total_size + size > 16_777_216:
            raise ProviderProbeError(
                "conformance Session journal validation limit exceeded"
            )
        try:
            raw = path.read_bytes()
        except OSError as exc:
            raise ProviderProbeError("cannot read conformance Session journal") from exc
        total_size += len(raw)
        journal_count += 1
        location = _safe_journal_location(root, path, 1)
        if raw.startswith(b"\xef\xbb\xbf"):
            raise ProviderProbeError(f"journal encoding violation at {location}")
        if not raw or not raw.endswith(b"\n") or b"\r" in raw:
            raise ProviderProbeError(f"journal LF framing violation at {location}")
        try:
            text = raw.decode("utf-8", errors="strict")
        except UnicodeDecodeError as exc:
            raise ProviderProbeError(f"journal UTF-8 violation at {location}") from exc
        lines = text[:-1].split("\n")
        if not lines or any(not line for line in lines):
            raise ProviderProbeError(f"journal blank-line violation at {location}")
        for line_number, line in enumerate(lines, start=1):
            line_location = _safe_journal_location(root, path, line_number)
            try:
                value = runner.strict_json_loads(line)
            except Exception as exc:
                raise ProviderProbeError(
                    f"journal JSON violation at {line_location}"
                ) from exc
            if not isinstance(value, dict):
                raise ProviderProbeError(f"journal object violation at {line_location}")
            try:
                errors = sorted(
                    validator.iter_errors(value),
                    key=lambda item: (
                        tuple(str(part) for part in item.absolute_path),
                        tuple(str(part) for part in item.absolute_schema_path),
                    ),
                )
            except Exception as exc:
                raise ProviderProbeError(
                    f"journal Schema evaluation failed at {line_location}"
                ) from exc
            if errors:
                instance_path = _safe_schema_instance_path(errors[0])
                raise ProviderProbeError(
                    f"journal Schema violation at {line_location} instance {instance_path}"
                )
    return journal_count


def _load_probe_session_records(root: Path) -> list[JSONObject]:
    """功能：从探针临时根定位唯一目标 Session 并严格加载有序 journal records。

    输入：runner 独占且已通过 Schema 校验的临时 Session 根。
    输出：不含 session header 的目标 Session record 数组。
    不变量：只接受固定探针 sessionId，不跟随符号链接，不在错误中回显记录正文。
    失败：journal 缺失/重复、UTF-8/JSON/对象形状异常时抛出脱敏 ProviderProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    matches: list[list[JSONObject]] = []
    for path in sorted(root.rglob("journal.jsonl")):
        if path.is_symlink() or not path.is_file():
            continue
        try:
            raw = path.read_bytes()
            lines = raw.decode("utf-8", errors="strict").splitlines()
            values = [runner.strict_json_loads(line) for line in lines]
        except Exception as exc:
            raise ProviderProbeError(
                "cannot parse conformance Session journal for persistence proof"
            ) from exc
        if (
            values
            and isinstance(values[0], dict)
            and values[0].get("kind") == "session"
            and values[0].get("sessionId") == "provider-conformance-session-1"
            and all(isinstance(value, dict) for value in values[1:])
        ):
            matches.append(values[1:])
    if len(matches) != 1:
        raise ProviderProbeError(
            "expected one target Session journal for tool persistence proof"
        )
    return matches[0]


def _journal_data(record: JSONObject) -> JSONObject | None:
    """功能：安全取得 journal record 的对象 data，非法形状返回空值。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    data = record.get("data")
    return data if isinstance(data, dict) else None


def _verify_tool_result_persistence(
    root: Path,
    frames: Sequence[JSONObject],
    expected: JSONObject,
) -> None:
    """功能：证明工具 intent/result/canonical message 在对应事件前已按序持久化。

    输入：探针 Session 根、完整协议帧及固定 toolCall 期望。
    输出：唯一 durable 链与 stdout requested/completed/run.completed 精确对应时无返回值。
    不变量：顺序必须为 intent→requested event、result→tool message→completed event，且
    completed event 先于最终 run.completed event；比较过程不记录工具输出正文。
    失败：任一记录缺失、重复、关联或顺序不同均抛出脱敏 ProviderProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    records = _load_probe_session_records(root)
    requested_frames = [
        frame for frame in frames if _event_type(frame) == "tool.requested"
    ]
    completed_frames = [
        frame for frame in frames if _event_type(frame) == "tool.completed"
    ]
    terminal_frames = [
        frame for frame in frames if _event_type(frame) == "run.completed"
    ]
    if not (
        len(requested_frames) == len(completed_frames) == len(terminal_frames) == 1
    ):
        raise ProviderProbeError(
            "tool persistence proof requires unique lifecycle events"
        )
    requested_params = requested_frames[0].get("params")
    completed_params = completed_frames[0].get("params")
    terminal_params = terminal_frames[0].get("params")
    completed_data = _event_data(completed_frames[0])
    if not all(
        isinstance(value, dict)
        for value in (
            requested_params,
            completed_params,
            terminal_params,
            completed_data,
        )
    ):
        raise ProviderProbeError("tool persistence lifecycle event shape is invalid")
    expected_id = expected.get("id")
    expected_name = expected.get("name")
    expected_arguments = expected.get("arguments")
    completed_result = completed_data.get("result")  # type: ignore[union-attr]

    intent_indices = [
        index
        for index, record in enumerate(records)
        if record.get("kind") == "tool.intent"
        and isinstance(_journal_data(record), dict)
        and _journal_data(record).get("toolCallId") == expected_id  # type: ignore[union-attr]
        and _journal_data(record).get("name") == expected_name  # type: ignore[union-attr]
        and _journal_data(record).get("arguments") == expected_arguments  # type: ignore[union-attr]
    ]
    requested_event_indices = [
        index
        for index, record in enumerate(records)
        if record.get("kind") == "event.emitted"
        and isinstance(_journal_data(record), dict)
        and _journal_data(record).get("event") == requested_params  # type: ignore[union-attr]
    ]
    result_indices = [
        index
        for index, record in enumerate(records)
        if record.get("kind") == "tool.result"
        and isinstance(_journal_data(record), dict)
        and _journal_data(record).get("toolCallId") == expected_id  # type: ignore[union-attr]
        and _journal_data(record).get("outcomeKnown") is True  # type: ignore[union-attr]
        and _journal_data(record).get("result") == completed_result  # type: ignore[union-attr]
    ]
    message_indices = [
        index
        for index, record in enumerate(records)
        if record.get("kind") == "message.appended"
        and isinstance(_journal_data(record), dict)
        and isinstance(_journal_data(record).get("message"), dict)  # type: ignore[union-attr]
        and _journal_data(record)["message"].get("role") == "tool"  # type: ignore[index,union-attr]
        and _journal_data(record)["message"].get("toolCallId") == expected_id  # type: ignore[index,union-attr]
        and _journal_data(record)["message"].get("toolName") == expected_name  # type: ignore[index,union-attr]
        and isinstance(completed_result, dict)
        and _journal_data(record)["message"].get("content")  # type: ignore[index,union-attr]
        == completed_result.get("content")
        and _journal_data(record)["message"].get("isError")  # type: ignore[index,union-attr]
        == completed_result.get("isError")
    ]
    completed_event_indices = [
        index
        for index, record in enumerate(records)
        if record.get("kind") == "event.emitted"
        and isinstance(_journal_data(record), dict)
        and _journal_data(record).get("event") == completed_params  # type: ignore[union-attr]
    ]
    terminal_event_indices = [
        index
        for index, record in enumerate(records)
        if record.get("kind") == "event.emitted"
        and isinstance(_journal_data(record), dict)
        and _journal_data(record).get("event") == terminal_params  # type: ignore[union-attr]
    ]
    groups = (
        intent_indices,
        requested_event_indices,
        result_indices,
        message_indices,
        completed_event_indices,
        terminal_event_indices,
    )
    if not all(len(group) == 1 for group in groups):
        raise ProviderProbeError("tool persistence records are missing or duplicated")
    if not (
        intent_indices[0]
        < requested_event_indices[0]
        < result_indices[0]
        < message_indices[0]
        < completed_event_indices[0]
        < terminal_event_indices[0]
    ):
        raise ProviderProbeError("tool persistence record order differs")


def _probe_environment(
    family: JSONObject,
    endpoint: str,
    canary: str,
    session_root: Path,
) -> dict[str, str]:
    """功能：构造仅含精确 loopback endpoint、合成凭据和有界策略的子环境增量。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    endpoint_env = family.get("endpointEnv")
    credential_env = family.get("credentialEnv")
    if not isinstance(endpoint_env, str) or not isinstance(credential_env, str):
        raise ProviderProbeError("family endpoint/credential env names are invalid")
    dead_proxy = "http://127.0.0.1:9"
    environment = {
        "QXNM_FORGE_PROVIDER_CONFORMANCE": "1",
        "QXNM_FORGE_CONFORMANCE": "1",
        endpoint_env: endpoint,
        credential_env: canary,
        "QXNM_FORGE_PROVIDER_CONNECT_TIMEOUT_MS": "150",
        "QXNM_FORGE_PROVIDER_IDLE_TIMEOUT_MS": "150",
        "QXNM_FORGE_PROVIDER_TOTAL_TIMEOUT_MS": "1500",
        "QXNM_FORGE_PROVIDER_MAX_ATTEMPTS": "2",
        "QXNM_FORGE_PROVIDER_RETRY_MAX_DELAY_MS": "50",
        "QXNM_FORGE_SESSION_ROOT": str(session_root),
        "HTTP_PROXY": dead_proxy,
        "HTTPS_PROXY": dead_proxy,
        "ALL_PROXY": dead_proxy,
        "NO_PROXY": "127.0.0.1",
        "http_proxy": dead_proxy,
        "https_proxy": dead_proxy,
        "all_proxy": dead_proxy,
        "no_proxy": "127.0.0.1",
    }
    family_id = family.get("id")
    if family_id == "google-vertex":
        environment.update(
            {
                "GOOGLE_CLOUD_PROJECT": "mock-project",
                "GCLOUD_PROJECT": "mock-project",
                "GOOGLE_CLOUD_LOCATION": "us-central1",
            }
        )
    elif family_id == "bedrock-converse-stream":
        environment.update(
            {
                "AWS_SECRET_ACCESS_KEY": canary,
                "AWS_SESSION_TOKEN": canary,
                "AWS_REGION": "us-east-1",
                "AWS_DEFAULT_REGION": "us-east-1",
                "AWS_EC2_METADATA_DISABLED": "true",
            }
        )
    return environment


def _verify_request_observations(
    observations: Sequence[JSONObject],
    family: JSONObject,
    case: JSONObject,
) -> None:
    """功能：验证 mock 请求次数、目标元数据及首批工具续轮脱敏形状。

    输入：本次 run 新增观测、family 与 case 夹具。
    输出：请求数及每次认证/形状均满足机器期望时无返回值。
    不变量：工具续轮必须恰好两次请求，第二次六项原生关联/结果布尔值全部为 true；
    本函数不读取或记录认证值、prompt、工具输出或完整 HTTP body。
    失败：数量、序号、family/case 或任一固定布尔摘要不同则抛 ProviderProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    family_id = family.get("id")
    case_name = case.get("name")
    expected = case.get("expected")
    if (
        not isinstance(family_id, str)
        or not isinstance(case_name, str)
        or not isinstance(expected, dict)
    ):
        raise ProviderProbeError("fixture request observation metadata is invalid")
    min_requests = expected.get("minRequests")
    exact_requests = expected.get("exactRequests")
    if not isinstance(min_requests, int) or len(observations) < min_requests:
        raise ProviderProbeError("mock request count is below fixture expectation")
    if isinstance(exact_requests, int) and len(observations) != exact_requests:
        raise ProviderProbeError(
            "mock request count differs from exact fixture expectation"
        )
    if not observations or any(
        observation.get("family") != family_id
        or observation.get("case") != case_name
        or observation.get("requestValid") is not True
        for observation in observations
    ):
        raise ProviderProbeError("mock observations contain invalid request metadata")

    if (
        family_id in provider_mock.BASELINE_FAMILIES
        and case_name == "partial_tool_arguments"
    ):
        if [observation.get("attempt") for observation in observations] != [1, 2]:
            raise ProviderProbeError("tool continuation request sequence differs")
        first_shape = observations[0].get("continuationShape")
        second_shape = observations[1].get("continuationShape")
        if (
            not isinstance(first_shape, dict)
            or first_shape.get("required") is not False
            or not isinstance(second_shape, dict)
            or second_shape.get("required") is not True
            or not all(
                second_shape.get(field) is True
                for field in (
                    "nativeToolCallExact",
                    "nativeToolResultCorrelated",
                    "nativeToolResultNonEmpty",
                    "nativeToolResultSuccessful",
                    "nativeToolResultExact",
                    "toolCallPrecedesResult",
                )
            )
        ):
            raise ProviderProbeError("family-native tool continuation shape differs")


def run_probe(
    command: Sequence[str],
    server: provider_mock.ProviderMockServer,
    family: JSONObject,
    case: JSONObject,
    *,
    timeout: float,
    schema_root: Path | None = None,
) -> None:
    """功能：以清洁环境执行一个 family/case 的动态 daemon 黑盒探针。

    输入：安全 argv、已启动回环 mock、机器 family/case、总超时和可选 Schema 根目录。
    输出：协议、观测和泄漏检查全部通过时无返回值。
    不变量：凭据运行时随机生成；不经过 shell；真实 Provider/云凭据先清除。
    失败：任何实现、协议、安全或期望不符时抛出已脱敏 ProviderProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    family_id = family.get("id")
    case_name = case.get("name")
    if not isinstance(family_id, str) or not isinstance(case_name, str):
        raise ProviderProbeError("family/case identifiers are invalid")
    canary = "provider-canary-" + secrets.token_urlsafe(32)
    start_observation = len(server.state.observations())
    frames: list[JSONObject] = []
    requests: list[JSONObject] = []
    process: runner.DaemonProcess | None = None

    try:
        with tempfile.TemporaryDirectory(prefix="provider-conformance-") as temporary:
            probe_root = Path(temporary)
            workspace, session_root = _prepare_probe_directories(probe_root)
            expanded_command = _expand_probe_command(command, workspace, session_root)
            environment = _probe_environment(
                family,
                server.endpoint(family_id, case_name),
                canary,
                session_root,
            )
            process = runner.DaemonProcess(
                expanded_command,
                timeout=timeout,
                max_frame_bytes=1_048_576,
                extra_env=environment,
                removed_env=(*CREDENTIAL_ENV, *ENDPOINT_ENV, *PROXY_ENV),
            )
            try:
                initialize_request = _initialize_request()
                requests.append(initialize_request)
                process.send_request(initialize_request)
                initialize = _await_response(
                    process,
                    "provider-initialize-1",
                    frames,
                    timeout=timeout,
                    allow_events=False,
                )
                provider_id = family.get("providerId")
                if not isinstance(provider_id, str):
                    raise ProviderProbeError("family.providerId must be a string")
                _assert_provider_advertised(initialize, provider_id)

                start_request = _run_start_request(family)
                requests.append(start_request)
                process.send_request(start_request)
                start_response = _await_response(
                    process,
                    "provider-run-start-1",
                    frames,
                    timeout=timeout,
                    allow_events=False,
                )
                result = start_response.get("result")
                run_id = result.get("runId") if isinstance(result, dict) else None
                if not isinstance(run_id, str) or not run_id:
                    raise ProviderProbeError(
                        "run/start did not return a non-empty runId"
                    )

                if case_name == "cancellation":
                    _await_first_mock_request(
                        process,
                        server,
                        frames,
                        start_observation=start_observation,
                        family_id=family_id,
                        case_name=case_name,
                        run_id=run_id,
                        timeout=timeout,
                    )
                    cancel_request = _cancel_request(run_id)
                    requests.append(cancel_request)
                    process.send_request(cancel_request)
                    _await_response(
                        process,
                        "provider-run-cancel-1",
                        frames,
                        timeout=timeout,
                        allow_events=True,
                    )
                _await_terminal(process, run_id, frames, timeout)
            finally:
                process.close()

            verify_trace(
                requests,
                frames,
                family,
                case,
                schema_root=schema_root,
            )
            observations = server.state.observations()[start_observation:]
            _verify_request_observations(observations, family, case)
            for observation in observations:
                auth = observation.get("auth")
                if not isinstance(auth, list) or not all(
                    isinstance(item, dict)
                    and item.get("present") is True
                    and item.get("prefixValid") is True
                    for item in auth
                ):
                    raise ProviderProbeError(
                        "mock did not observe valid authentication shape"
                    )
                fixed_headers = observation.get("fixedHeaders")
                expected_fixed_headers = family.get("fixedHeaders", [])
                if (
                    not isinstance(expected_fixed_headers, list)
                    or not isinstance(fixed_headers, list)
                    or len(fixed_headers) != len(expected_fixed_headers)
                    or not all(
                        isinstance(item, dict)
                        and item.get("present") is True
                        and item.get("prefixValid") is True
                        for item in fixed_headers
                    )
                ):
                    raise ProviderProbeError(
                        "mock did not observe valid fixed-header shape"
                    )

            frame_bytes = json.dumps(frames, ensure_ascii=False).encode("utf-8")
            observation_bytes = json.dumps(observations, ensure_ascii=False).encode(
                "utf-8"
            )
            stderr_bytes = process.stderr_text(limit=131_072).encode("utf-8")
            _assert_canary_absent("protocol stdout", frame_bytes, canary)
            _assert_canary_absent("daemon stderr", stderr_bytes, canary)
            _assert_canary_absent("mock observations", observation_bytes, canary)
            _scan_tree_for_canary(probe_root, canary)
            if schema_root is not None:
                validate_probe_journals(probe_root, schema_root)
                expected = case.get("expected")
                tool_call = (
                    expected.get("toolCall") if isinstance(expected, dict) else None
                )
                if (
                    family_id in provider_mock.BASELINE_FAMILIES
                    and case_name == "partial_tool_arguments"
                    and isinstance(tool_call, dict)
                ):
                    _verify_tool_result_persistence(probe_root, frames, tool_call)
    except Exception as exc:
        safe_message = str(exc).replace(canary, "[REDACTED]")
        if isinstance(exc, ProviderProbeError) and safe_message == str(exc):
            raise
        raise ProviderProbeError(safe_message or type(exc).__name__) from exc


def build_argument_parser() -> argparse.ArgumentParser:
    """功能：构建静态门禁及 opt-in daemon 探针参数解析器。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser(
        description="Validate local provider mock cases and optionally probe a daemon."
    )
    parser.add_argument("--fixture", type=Path, default=provider_mock.DEFAULT_FIXTURE)
    parser.add_argument("--schema-root", type=Path)
    parser.add_argument("--daemon-command-json", type=parse_command_json)
    parser.add_argument("--family", choices=provider_mock.ALL_FAMILIES)
    parser.add_argument("--case", choices=provider_mock.EXPECTED_CASES)
    parser.add_argument("--timeout", type=float, default=6.0)
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    """功能：运行默认静态门禁，并在明确提供安全 JSON argv 时运行黑盒探针。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = build_argument_parser().parse_args(argv)
    if args.timeout <= 0 or args.timeout > 60:
        print("provider conformance: timeout must be in (0, 60]", file=sys.stderr)
        return 2
    try:
        family_count, case_count = run_static_gate(args.fixture, args.schema_root)
        if args.daemon_command_json is None:
            print(
                f"PASS: provider runner static {family_count} families / {case_count} cases"
            )
            return 0

        fixture = provider_mock.load_fixture(args.fixture)
        families = [
            value
            for value in fixture["families"]
            if isinstance(value, dict)
            and (args.family is None or value.get("id") == args.family)
        ]
        if args.family is not None and not families:
            raise ProviderProbeError(
                "selected family is not present in the selected fixture suite"
            )
        with provider_mock.ProviderMockServer(args.fixture) as server:
            probe_count = 0
            for family in families:
                cases = family.get("cases")
                if not isinstance(cases, list):
                    raise ProviderProbeError("family.cases must be an array")
                for case in cases:
                    if not isinstance(case, dict):
                        raise ProviderProbeError("case must be an object")
                    if args.case is not None and case.get("name") != args.case:
                        continue
                    run_probe(
                        args.daemon_command_json,
                        server,
                        family,
                        case,
                        timeout=args.timeout,
                        schema_root=args.schema_root,
                    )
                    probe_count += 1
                    print(f"PASS: {family['id']}/{case['name']}")
        print(f"PASS: provider daemon probes {probe_count}")
        return 0
    except (
        ProviderProbeError,
        provider_mock.ProviderMockError,
        runner.ConformanceError,
    ) as exc:
        print(f"provider conformance failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
