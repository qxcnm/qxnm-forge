#!/usr/bin/env python3
"""工具与审批 v0.1 的离线静态门禁和可选 daemon 黑盒 runner。

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
from typing import Any, Sequence

import provider_runner
import runner
import schema_validation
import session_runner
import session_validation
import tool_validation


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SCHEMA_ROOT = ROOT / "SPEC/schemas"
SESSION_ID = "tool-conformance-session-1"
INPUT_TEXT = "Execute the configured local conformance tool calls."
MAX_JOURNAL_BYTES = 16_777_216

JSONObject = dict[str, Any]


class ToolProbeError(Exception):
    """表示工具/审批静态门禁或 daemon 黑盒探针未满足公共契约。"""


def validate_fixture_schema(fixture_path: Path, schema_root: Path) -> None:
    """功能：用 bundled Draft 2020-12 Schema 验证工具/审批机器夹具。

    输入：夹具路径及 SPEC/schemas 根目录。
    输出：Schema 与实例有效时无返回值。
    不变量：只注册本地 Schema，不解析远程引用或访问网络。
    失败：依赖、Schema、实例或文件无效时抛出脱敏 ToolProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        import jsonschema
        from referencing import Registry, Resource
    except ImportError as exc:
        raise ToolProbeError(
            "tool/approval Schema validation requires jsonschema and referencing"
        ) from exc
    resources: list[tuple[str, Any]] = []
    schemas: dict[str, JSONObject] = {}
    try:
        for path in sorted(schema_root.rglob("*.schema.json")):
            schema = tool_validation.require_object(
                runner.strict_json_loads(path.read_text(encoding="utf-8")),
                "bundled Schema",
            )
            jsonschema.Draft202012Validator.check_schema(schema)
            schema_id = tool_validation.require_string(schema.get("$id"), "Schema $id")
            resources.append((schema_id, Resource.from_contents(schema)))
            schemas[str(path.relative_to(schema_root))] = schema
        schema_name = "tool-approval-cases.schema.json"
        if schema_name not in schemas:
            raise ToolProbeError("bundled tool/approval case Schema is missing")
        fixture = tool_validation.load_fixture(fixture_path)
        errors = sorted(
            jsonschema.Draft202012Validator(
                schemas[schema_name], registry=Registry().with_resources(resources)
            ).iter_errors(fixture),
            key=lambda item: (
                tuple(str(part) for part in item.absolute_path),
                tuple(str(part) for part in item.absolute_schema_path),
            ),
        )
    except ToolProbeError:
        raise
    except Exception as exc:
        raise ToolProbeError("cannot prepare bundled tool/approval Schema") from exc
    if errors:
        raise ToolProbeError("tool/approval fixture violates bundled Schema")


def run_static_gate(fixture_path: Path, schema_root: Path) -> tuple[int, int, int]:
    """功能：执行不启动 daemon、不执行工具的工具/审批静态门禁。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        validate_fixture_schema(fixture_path, schema_root)
        return tool_validation.static_self_test(fixture_path, schema_root)
    except tool_validation.ToolValidationError as exc:
        raise ToolProbeError(str(exc)) from exc


def _initialize_request(interactive: bool) -> JSONObject:
    """功能：构造声明真实交互审批能力的 initialize 请求。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": "tool-initialize-1",
        "method": "initialize",
        "params": {
            "protocolVersions": ["0.1"],
            "client": {"name": "tool-conformance-runner", "version": "0.1.0"},
            "capabilities": {"interactiveApprovals": interactive},
        },
    }


def _configure_request(case: JSONObject) -> JSONObject:
    """功能：把选中案例的受治理 faux 多轮场景包装为配置请求。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    scenario = tool_validation.require_object(case.get("scenario"), "case.scenario")
    return {
        "jsonrpc": "2.0",
        "id": "tool-configure-1",
        "method": "faux/configure",
        "params": {"sessionId": SESSION_ID, "scenario": scenario},
    }


def _run_start_request() -> JSONObject:
    """功能：构造固定输入和 sequential 工具批次的 run/start 请求。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": "tool-run-start-1",
        "method": "run/start",
        "params": {
            "sessionId": SESSION_ID,
            "input": {
                "role": "user",
                "content": [{"type": "text", "text": INPUT_TEXT}],
            },
            "provider": {"id": "faux", "modelId": "faux-v1"},
            "options": {"toolExecution": "sequential"},
        },
    }


def _approval_response_request(
    run_id: str, approval_id: str, decision: str, sequence: int = 1
) -> JSONObject:
    """功能：用运行时 opaque ID 构造一次性审批响应请求。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": f"tool-approval-response-{sequence}",
        "method": "approval/respond",
        "params": {
            "sessionId": SESSION_ID,
            "runId": run_id,
            "approvalId": approval_id,
            "decision": {"choice": decision},
        },
    }


def _session_get_request() -> JSONObject:
    """功能：构造触发静止 Session fail-closed 恢复的完整快照请求。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": "tool-recovery-get-1",
        "method": "session/get",
        "params": {"sessionId": SESSION_ID, "afterSeq": 0},
    }


def _cancel_request(run_id: str, sequence: int = 1) -> JSONObject:
    """功能：构造等待审批期间使用的可重复幂等 run/cancel 请求。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": f"tool-run-cancel-{sequence}",
        "method": "run/cancel",
        "params": {"sessionId": SESSION_ID, "runId": run_id},
    }


def _await_success(
    process: runner.DaemonProcess,
    request_id: str,
    frames: list[JSONObject],
    *,
    timeout: float,
) -> JSONObject:
    """功能：等待指定成功响应并拒绝该控制响应之前出现任何 run 事件。

    输入：daemon、opaque 请求 ID、累计帧与有界超时。
    输出：匹配 ID 且 result 为对象的成功响应。
    不变量：控制响应前事件会使探针失败，确保批准/取消 ack 先于恢复执行。
    失败：超时、错误响应、未知 ID 或提前事件时抛出 ToolProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    deadline = time.monotonic() + timeout
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ToolProbeError("daemon response timed out")
        try:
            frame = process.next_frame(remaining)
        except queue.Empty as exc:
            raise ToolProbeError("daemon response timed out") from exc
        frames.append(frame)
        if frame.get("method") == "event":
            raise ToolProbeError("daemon emitted an event before required response")
        if frame.get("id") != request_id:
            raise ToolProbeError("daemon returned an unexpected response id")
        if isinstance(frame.get("error"), dict):
            error = frame["error"]
            raise ToolProbeError(
                "daemon returned structured error "
                f"code={error.get('code')!r} retryable={error.get('retryable')!r}"
            )
        if not isinstance(frame.get("result"), dict):
            raise ToolProbeError("daemon success result must be an object")
        return frame


def _await_success_with_events(
    process: runner.DaemonProcess,
    request_id: str,
    frames: list[JSONObject],
    state_root: Path,
    schema_root: Path,
    *,
    timeout: float,
) -> tuple[JSONObject, bool]:
    """功能：等待允许并发事件的控制响应并实时执行 durable 前缀检查。

    输入：daemon、请求 ID、累计帧、状态/Schema 根与有界超时。
    输出：成功响应及等待期间是否已经观察到 run 终态。
    不变量：不对 run/cancel 新增“响应必须早于事件”的非规范顺序；每个并发事件仍先验证持久化。
    失败：超时、未知响应、结构化错误或事件 durability 违规时抛出 ToolProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    terminal_seen = False
    deadline = time.monotonic() + timeout
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ToolProbeError("daemon response timed out")
        try:
            frame = process.next_frame(remaining)
        except queue.Empty as exc:
            raise ToolProbeError("daemon response timed out") from exc
        frames.append(frame)
        event_type = _event_type(frame)
        if event_type is not None:
            assert_event_durable(state_root, schema_root, frame)
            terminal_seen = terminal_seen or event_type in runner.TERMINAL_EVENTS
            continue
        if frame.get("id") != request_id:
            raise ToolProbeError("daemon returned an unexpected response id")
        if isinstance(frame.get("error"), dict):
            error = frame["error"]
            raise ToolProbeError(
                "daemon returned structured error "
                f"code={error.get('code')!r} retryable={error.get('retryable')!r}"
            )
        if not isinstance(frame.get("result"), dict):
            raise ToolProbeError("daemon success result must be an object")
        return frame, terminal_seen


def _event_type(frame: JSONObject) -> str | None:
    """功能：从 event notification 安全提取类型字符串。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    params = frame.get("params")
    value = params.get("type") if isinstance(params, dict) else None
    return value if frame.get("method") == "event" and isinstance(value, str) else None


def _event_data(frame: JSONObject) -> JSONObject:
    """功能：从 event notification 提取必须为对象的 data。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    params = frame.get("params")
    data = params.get("data") if isinstance(params, dict) else None
    if frame.get("method") != "event" or not isinstance(data, dict):
        raise ToolProbeError("event data must be an object")
    return data


def _assert_capabilities(initialize: JSONObject, case: JSONObject) -> None:
    """功能：确认 daemon 诚实声明本案例实际需要的方法、事件、faux 和工具。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    result = initialize.get("result")
    capabilities = result.get("capabilities") if isinstance(result, dict) else None
    if not isinstance(capabilities, dict):
        raise ToolProbeError("initialize omitted capabilities")
    methods = capabilities.get("methods")
    event_types = capabilities.get("eventTypes")
    providers = capabilities.get("providers")
    tools = capabilities.get("tools")
    if not all(
        isinstance(value, list) for value in (methods, event_types, providers, tools)
    ):
        raise ToolProbeError("initialize capability arrays are invalid")
    required_methods = {"initialize", "faux/configure", "run/start", "run/cancel"}
    if case.get("mode") == "interactive":
        required_methods.add("approval/respond")
    if not required_methods.issubset(set(methods)):
        raise ToolProbeError("daemon omitted an exercised method capability")
    if not any(
        isinstance(value, dict)
        and value.get("id") == "faux"
        and "faux-v1" in value.get("models", [])
        for value in providers
    ):
        raise ToolProbeError("daemon omitted faux/faux-v1 capability")
    expected = tool_validation.require_object(case.get("expected"), "case.expected")
    if case.get("kind") == "approval_failure":
        required_events = {
            "run.started",
            "turn.started",
            "tool.requested",
            "approval.requested",
            "approval.resolved",
            "tool.completed",
            "run.completed",
            "run.interrupted",
        }
        if not required_events.issubset(set(event_types)):
            raise ToolProbeError("daemon omitted an approval-failure event capability")
        if "file.write" not in set(tools):
            raise ToolProbeError("daemon omitted file.write approval capability")
        return
    expected_order = tool_validation.require_array(
        expected.get("semanticEventOrder"), "expected.semanticEventOrder"
    )
    if not set(expected_order).issubset(set(event_types)):
        raise ToolProbeError("daemon omitted an exercised event capability")
    required_tools = {
        value.get("name")
        for value in expected.get("toolCalls", [])
        if isinstance(value, dict) and value.get("name") in ("file.read", "file.write")
    }
    if not required_tools.issubset(set(tools)):
        raise ToolProbeError("daemon omitted an executable tool capability")


def _render_command(
    template: Sequence[str], workspace: Path, state_root: Path
) -> list[str]:
    """功能：只展开 runner 创建路径的已知占位符并保持 argv 边界。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return session_runner.render_command(
        list(template),
        {
            "workspace": str(workspace),
            "stateRoot": str(state_root),
            "sessionsRoot": str(state_root / "sessions"),
            "sessionId": SESSION_ID,
            "repoRoot": str(ROOT),
        },
    )


def _seed_workspace(workspace: Path, case: JSONObject) -> None:
    """功能：只按受治理相对路径在新临时工作区创建确定性文本夹具。

    输入：runner 新建工作区与已静态验证的 daemon 案例。
    输出：种子文件创建并刷新后无返回值。
    不变量：路径由 validate_relative_path 约束且父目录不含符号链接。
    失败：文件系统错误或路径边界不满足时抛出 ToolProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    for raw_seed in tool_validation.require_array(case.get("seedFiles"), "seedFiles"):
        seed = tool_validation.require_object(raw_seed, "seed file")
        relative = tool_validation.validate_relative_path(seed.get("path"), "seed.path")
        content = seed.get("content")
        if not isinstance(content, str):
            raise ToolProbeError("seed content must be text")
        path = workspace / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        try:
            path.write_text(content, encoding="utf-8", newline="")
        except OSError as exc:
            raise ToolProbeError("cannot create conformance workspace seed") from exc


def _journal_candidates(state_root: Path) -> list[Path]:
    """功能：列出状态根下所有不经符号链接的 regular journal.jsonl。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return [
        path
        for path in sorted(state_root.rglob("journal.jsonl"))
        if not path.is_symlink() and path.is_file()
    ]


def _load_session_records(
    state_root: Path, schema_root: Path, *, live_prefix: bool = False
) -> list[JSONObject]:
    """功能：定位本案例 Session journal 并加载 strict final 或 durable live 前缀。

    输入：runner 独占状态根、bundled Schema 根及是否处于并发追加阶段。
    输出：不含 header 的有序 journal record 对象。
    不变量：live 模式只解析最后一个完整 LF 前缀，不把并发中的下一条半写尾行误判为损坏；最终模式严格校验全文件。
    失败：没有/重复目标 journal、边界或 JSON 违规时抛出 ToolProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not live_prefix:
        provider_runner.validate_probe_journals(state_root, schema_root)
    matches: list[list[JSONObject]] = []
    total = 0
    for path in _journal_candidates(state_root):
        try:
            raw = path.read_bytes()
        except OSError as exc:
            raise ToolProbeError("cannot read conformance Session journal") from exc
        total += len(raw)
        if total > MAX_JOURNAL_BYTES:
            raise ToolProbeError("conformance Session journal limit exceeded")
        if live_prefix:
            last_lf = raw.rfind(b"\n")
            if last_lf < 0:
                continue
            raw = raw[: last_lf + 1]
        try:
            values = [
                runner.strict_json_loads(line)
                for line in raw.decode("utf-8", errors="strict").splitlines()
            ]
        except Exception as exc:
            raise ToolProbeError("cannot parse conformance Session journal") from exc
        if (
            values
            and isinstance(values[0], dict)
            and values[0].get("kind") == "session"
            and values[0].get("sessionId") == SESSION_ID
            and all(isinstance(value, dict) for value in values[1:])
        ):
            matches.append(values[1:])
    if len(matches) != 1:
        raise ToolProbeError("expected exactly one journal for the exercised session")
    return matches[0]


def _matching_indices(
    records: Sequence[JSONObject], kind: str, predicate: Any
) -> list[int]:
    """功能：返回满足 kind 和无副作用谓词的 journal 记录下标。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return [
        index
        for index, record in enumerate(records)
        if record.get("kind") == kind and predicate(record)
    ]


def _require_prior_record(
    records: Sequence[JSONObject], kind: str, event_index: int, predicate: Any
) -> int:
    """功能：要求目标 kind 的唯一匹配记录在 event.emitted 之前已持久化。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    indices = _matching_indices(records, kind, predicate)
    if len(indices) != 1 or indices[0] >= event_index:
        raise ToolProbeError(f"{kind} was not durably appended before its event")
    return indices[0]


def _record_data(record: JSONObject) -> JSONObject:
    """功能：取得 journal record 必须为对象的 data 节点。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    data = record.get("data")
    if not isinstance(data, dict):
        raise ToolProbeError("journal record data must be an object")
    return data


def assert_event_durable(
    state_root: Path, schema_root: Path, frame: JSONObject
) -> None:
    """功能：在事件被观察时验证 event 及其工具/审批前置记录已 durable。

    输入：状态根、Schema 根和刚从 stdout 读取的 event notification。
    输出：精确 event.emitted 及类型特定前置记录均已存在时无返回值。
    不变量：tool.completed 前必须同时有 tool.result 与 canonical tool message。
    失败：journal 缺失、Schema 违规、重复或顺序倒置时抛出 ToolProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    params = frame.get("params")
    if frame.get("method") != "event" or not isinstance(params, dict):
        raise ToolProbeError("durability check requires an event notification")
    records = _load_session_records(state_root, schema_root, live_prefix=True)
    event_indices = _matching_indices(
        records,
        "event.emitted",
        lambda record: _record_data(record).get("event") == params,
    )
    if len(event_indices) != 1:
        raise ToolProbeError("observable event lacks one exact durable event record")
    event_index = event_indices[0]
    event_type = params.get("type")
    data = _event_data(frame)
    if event_type == "tool.requested":
        tool_call_id = data.get("toolCallId")
        _require_prior_record(
            records,
            "tool.intent",
            event_index,
            lambda record: _record_data(record).get("runId") == params.get("runId")
            and _record_data(record).get("turnId") == params.get("turnId")
            and _record_data(record).get("toolCallId") == tool_call_id
            and _record_data(record).get("name") == data.get("name")
            and _record_data(record).get("arguments") == data.get("arguments"),
        )
    elif event_type == "approval.requested":
        approval = data.get("approval")
        approval_id = approval.get("approvalId") if isinstance(approval, dict) else None
        tool_call_id = (
            approval.get("toolCallId") if isinstance(approval, dict) else None
        )
        operation_hash = (
            approval.get("operationHash") if isinstance(approval, dict) else None
        )
        _require_prior_record(
            records,
            "tool.intent",
            event_index,
            lambda record: _record_data(record).get("runId") == params.get("runId")
            and _record_data(record).get("turnId") == params.get("turnId")
            and _record_data(record).get("toolCallId") == tool_call_id
            and _record_data(record).get("operationHash") == operation_hash,
        )
        _require_prior_record(
            records,
            "approval.requested",
            event_index,
            lambda record: _record_data(record).get("approval") == approval,
        )
    elif event_type == "approval.resolved":
        approval_id = data.get("approvalId")
        _require_prior_record(
            records,
            "approval.resolved",
            event_index,
            lambda record: _record_data(record).get("approvalId") == approval_id
            and _record_data(record).get("decision") == data.get("decision")
            and _record_data(record).get("resolutionSource")
            == data.get("resolutionSource"),
        )
    elif event_type == "tool.completed":
        tool_call_id = data.get("toolCallId")
        intent_names = [
            _record_data(record).get("name")
            for record in records
            if record.get("kind") == "tool.intent"
            and _record_data(record).get("toolCallId") == tool_call_id
        ]
        if len(intent_names) != 1:
            raise ToolProbeError("tool.completed lacks one canonical tool intent")
        tool_name = intent_names[0]
        result_index = _require_prior_record(
            records,
            "tool.result",
            event_index,
            lambda record: _record_data(record).get("runId") == params.get("runId")
            and _record_data(record).get("turnId") == params.get("turnId")
            and _record_data(record).get("toolCallId") == tool_call_id
            and _record_data(record).get("outcomeKnown") is True
            and _record_data(record).get("result") == data.get("result"),
        )
        message_index = _require_prior_record(
            records,
            "message.appended",
            event_index,
            lambda record: isinstance(_record_data(record).get("message"), dict)
            and _record_data(record)["message"].get("role") == "tool"
            and _record_data(record)["message"].get("toolCallId") == tool_call_id
            and _record_data(record)["message"].get("toolName") == tool_name
            and _record_data(record)["message"].get("content")
            == data.get("result", {}).get("content")
            and _record_data(record)["message"].get("isError")
            == data.get("result", {}).get("isError"),
        )
        if message_index <= result_index:
            raise ToolProbeError("canonical tool message must follow tool.result")
    elif event_type == "run.cancelled":
        _require_prior_record(
            records,
            "run.cancellation_requested",
            event_index,
            lambda record: _record_data(record).get("runId") == params.get("runId"),
        )


def assert_control_response_durable(
    state_root: Path,
    schema_root: Path,
    request: JSONObject,
    response: JSONObject,
) -> None:
    """功能：在控制成功帧刚被观察时验证其 accepted mutation 已唯一落盘。

    输入：状态/Schema 根、刚发送的 approval/respond 或 run/cancel 及成功响应。
    输出：对应 approval resolution 或 cancellation intent 已存在且唯一时无返回值。
    不变量：使用并发安全的完整 LF 前缀；不等待后续事件来掩盖先响应后写盘。
    失败：响应形状、关联 ID、decision/source 或 durable 记录数量不符时抛出 ToolProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(response.get("result"), dict):
        raise ToolProbeError("control response result must be an object")
    records = _load_session_records(state_root, schema_root, live_prefix=True)
    params = tool_validation.require_object(request.get("params"), "control.params")
    if request.get("method") == "approval/respond":
        matches = _matching_indices(
            records,
            "approval.resolved",
            lambda record: _record_data(record).get("runId") == params.get("runId")
            and _record_data(record).get("approvalId") == params.get("approvalId")
            and _record_data(record).get("decision") == params.get("decision")
            and _record_data(record).get("resolutionSource") == "client",
        )
        if len(matches) != 1 or response["result"].get("accepted") is not True:
            raise ToolProbeError("approval success preceded its durable resolution")
        return
    if request.get("method") == "run/cancel":
        matches = _matching_indices(
            records,
            "run.cancellation_requested",
            lambda record: _record_data(record).get("runId") == params.get("runId"),
        )
        state = response["result"].get("cancellationState")
        if len(matches) != 1 or state not in (
            "requested",
            "alreadyRequested",
            "terminal",
        ):
            raise ToolProbeError(
                "cancel success preceded or duplicated its durable intent"
            )
        return
    raise ToolProbeError("unsupported control response durability check")


def _approval_from_event(frame: JSONObject, expected_call: JSONObject) -> JSONObject:
    """功能：验证审批请求绑定到精确工具调用、参数和非空 operation hash。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    data = _event_data(frame)
    approval = data.get("approval")
    if not isinstance(approval, dict):
        raise ToolProbeError("approval.requested omitted approval object")
    operation_hash = approval.get("operationHash")
    if (
        approval.get("toolCallId") != expected_call.get("toolCallId")
        or approval.get("operation") != expected_call.get("name")
        or approval.get("arguments") != expected_call.get("arguments")
        or not isinstance(operation_hash, str)
        or len(operation_hash) != 64
        or any(character not in "0123456789abcdef" for character in operation_hash)
    ):
        raise ToolProbeError("approval request is not bound to normalized tool intent")
    choices = approval.get("choices")
    if not isinstance(choices, list) or not {"allow_once", "deny"}.issubset(choices):
        raise ToolProbeError("approval request omitted required choices")
    approval_id = approval.get("approvalId")
    if not isinstance(approval_id, str) or not approval_id:
        raise ToolProbeError("approval request omitted opaque approvalId")
    resources = approval.get("resources")
    expected_path = expected_call.get("arguments", {}).get("path")
    if (
        approval.get("risk") not in ("low", "medium", "high", "critical")
        or not isinstance(approval.get("expiresAt"), str)
        or not isinstance(resources, list)
        or not any(
            isinstance(resource, dict)
            and resource.get("kind") == "path"
            and resource.get("value") == expected_path
            for resource in resources
        )
    ):
        raise ToolProbeError("approval request omitted risk, expiry, or affected path")
    return approval


def _tool_calls_from_case(case: JSONObject) -> dict[str, JSONObject]:
    """功能：把场景首轮工具调用按 toolCallId 建立只读查找表。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    scenario = tool_validation.require_object(case.get("scenario"), "case.scenario")
    result: dict[str, JSONObject] = {}
    for raw_step in tool_validation.require_array(
        scenario.get("steps"), "scenario.steps"
    ):
        step = tool_validation.require_object(raw_step, "scenario step")
        if step.get("type") == "tool_call":
            tool_call_id = tool_validation.require_string(
                step.get("toolCallId"), "toolCallId"
            )
            result[tool_call_id] = step
    return result


def _verify_trace(
    requests: Sequence[JSONObject],
    frames: Sequence[JSONObject],
    case: JSONObject,
    schema_root: Path,
) -> None:
    """功能：验证实际 RPC Schema、生命周期、语义事件顺序与工具结果结构。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        schema_validation.validate_protocol_trace(requests, frames, schema_root)
    except schema_validation.SchemaValidationError as exc:
        raise ToolProbeError(
            "actual tool protocol trace violates bundled Schema"
        ) from exc
    runner.TraceValidator(requests).validate(frames)
    expected = tool_validation.require_object(case.get("expected"), "case.expected")
    observed_order = [
        event_type
        for event_type in (_event_type(frame) for frame in frames)
        if event_type in tool_validation.SEMANTIC_EVENTS
    ]
    expected_order = tool_validation.require_array(
        expected.get("semanticEventOrder"), "expected.semanticEventOrder"
    )
    if observed_order != expected_order:
        raise ToolProbeError("semantic tool event order differs from fixture")
    if observed_order.count("turn.started") != expected.get("providerTurnCount"):
        raise ToolProbeError("Provider turn count differs from fixture")

    expected_calls = tool_validation.require_array(
        expected.get("toolCalls"), "expected.toolCalls"
    )
    expected_by_id = {
        value.get("toolCallId"): value
        for value in expected_calls
        if isinstance(value, dict)
    }
    requested: list[JSONObject] = []
    started_ids: list[Any] = []
    completed: list[JSONObject] = []
    approvals: list[JSONObject] = []
    resolutions: list[JSONObject] = []
    for frame in frames:
        event_type = _event_type(frame)
        if event_type == "tool.requested":
            requested.append(_event_data(frame))
        elif event_type == "tool.started":
            started_ids.append(_event_data(frame).get("toolCallId"))
        elif event_type == "tool.completed":
            completed.append(_event_data(frame))
        elif event_type == "approval.requested":
            approvals.append(_event_data(frame))
        elif event_type == "approval.resolved":
            resolutions.append(_event_data(frame))
    requested_pairs = [
        (value.get("toolCallId"), value.get("name")) for value in requested
    ]
    expected_pairs = [
        (value.get("toolCallId"), value.get("name")) for value in expected_calls
    ]
    if requested_pairs != expected_pairs or len(completed) != len(expected_calls):
        raise ToolProbeError("tool request/completion correlation differs from fixture")
    scenario_calls = _tool_calls_from_case(case)
    if any(
        not isinstance(scenario_calls.get(value.get("toolCallId")), dict)
        or value.get("arguments")
        != scenario_calls[value.get("toolCallId")].get("arguments")
        for value in requested
    ):
        raise ToolProbeError("tool.requested arguments differ from Provider source call")
    if [value.get("toolCallId") for value in completed] != [
        value.get("toolCallId") for value in expected_calls
    ]:
        raise ToolProbeError("sequential tool completion order differs from source order")
    expected_started = [
        value.get("toolCallId")
        for value in expected_calls
        if isinstance(value, dict) and value.get("executed") is True
    ]
    if started_ids != expected_started:
        raise ToolProbeError("tool execution set or source order differs from fixture")
    for completion in completed:
        tool_call_id = completion.get("toolCallId")
        expected_call = expected_by_id.get(tool_call_id)
        result = completion.get("result")
        if not isinstance(expected_call, dict) or not isinstance(result, dict):
            raise ToolProbeError("tool.completed result correlation is invalid")
        if result.get("isError") is not expected_call.get("isError"):
            raise ToolProbeError("tool result error flag differs from fixture")
        if expected_call.get("isError") is True:
            error = result.get("error")
            details = error.get("details") if isinstance(error, dict) else None
            if (
                result.get("terminationReason")
                != expected_call.get("terminationReason")
                or not isinstance(details, dict)
                or details.get("kind") != expected_call.get("errorKind")
            ):
                raise ToolProbeError("structured tool error differs from fixture")
    if len(approvals) != expected.get("approvalCount") or len(resolutions) != len(
        approvals
    ):
        raise ToolProbeError("approval event count differs from fixture")
    control = tool_validation.require_object(case.get("control"), "case.control")
    if resolutions:
        expected_source = (
            "cancellation"
            if control.get("action") == "cancel_on_approval"
            else "client"
        )
        expected_choice = (
            "deny"
            if control.get("action") == "cancel_on_approval"
            else control.get("decision")
        )
        requested_ids = []
        for value in approvals:
            approval = value.get("approval")
            requested_ids.append(
                approval.get("approvalId") if isinstance(approval, dict) else None
            )
        resolved_ids = [value.get("approvalId") for value in resolutions]
        if requested_ids != resolved_ids or any(
            value.get("resolutionSource") != expected_source
            or not isinstance(value.get("decision"), dict)
            or value["decision"].get("choice") != expected_choice
            for value in resolutions
        ):
            raise ToolProbeError(
                "approval resolution ID, decision, or source differs from fixture"
            )


def _verify_workspace(workspace: Path, case: JSONObject) -> None:
    """功能：验证所有声明的工作区副作用且拒绝符号链接伪装结果。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    expected = tool_validation.require_object(case.get("expected"), "case.expected")
    for raw_item in tool_validation.require_array(
        expected.get("workspace"), "workspace"
    ):
        item = tool_validation.require_object(raw_item, "workspace expectation")
        relative = tool_validation.validate_relative_path(
            item.get("path"), "workspace.path"
        )
        path = workspace / relative
        if item.get("state") == "absent":
            if path.exists() or path.is_symlink():
                raise ToolProbeError("denied tool unexpectedly changed the workspace")
            continue
        if path.is_symlink() or not path.is_file():
            raise ToolProbeError("expected workspace result is not a regular file")
        try:
            content = path.read_text(encoding="utf-8", errors="strict")
        except (OSError, UnicodeError) as exc:
            raise ToolProbeError("cannot inspect expected workspace result") from exc
        if content != item.get("content"):
            raise ToolProbeError("workspace file content differs from fixture")


def _verify_final_journal(
    state_root: Path, schema_root: Path, case: JSONObject
) -> None:
    """功能：验证最终 journal 中每类工具、审批与取消记录的精确数量。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    records = _load_session_records(state_root, schema_root)
    try:
        state = session_validation.reconstruct_state(
            [{"kind": "session", "sessionId": SESSION_ID}, *records]
        )
    except session_validation.SessionValidationError as exc:
        raise ToolProbeError("final journal cross-record invariants failed") from exc
    expected = tool_validation.require_object(case.get("expected"), "case.expected")
    journal = tool_validation.require_object(
        expected.get("journal"), "expected.journal"
    )
    mappings = (
        ("tool.intent", "toolIntentCount"),
        ("tool.result", "toolResultCount"),
        ("approval.requested", "approvalRequestedCount"),
        ("approval.resolved", "approvalResolvedCount"),
        ("run.cancellation_requested", "cancellationRequestedCount"),
    )
    for kind, field in mappings:
        actual = sum(1 for record in records if record.get("kind") == kind)
        if actual != journal.get(field, 0):
            raise ToolProbeError(f"final journal {kind} count differs from fixture")
    terminal = expected.get("terminal")
    if terminal == "cancelled":
        terminal_records = [
            record
            for record in records
            if record.get("kind") == "run.terminal"
            and isinstance(record.get("data"), dict)
            and record["data"].get("status") == "cancelled"
        ]
        if len(terminal_records) != 1 or state.unfinished_run_ids:
            raise ToolProbeError("final cancelled journal state differs from fixture")
    elif state.unfinished_run_ids:
        raise ToolProbeError("final completed journal retained an unfinished run")


def _probe_environment(
    workspace: Path, state_root: Path, canary: str
) -> dict[str, str]:
    """功能：构造仅指向 runner 临时根的工具 conformance 子环境增量。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "QXNM_FORGE_CONFORMANCE": "1",
        "QXNM_FORGE_TOOL_CONFORMANCE": "1",
        "QXNM_FORGE_WORKSPACE": str(workspace),
        "QXNM_FORGE_SESSION_ROOT": str(state_root),
        "QXNM_FORGE_TOOL_CONFORMANCE_CANARY": canary,
    }


def _await_conflict_with_events(
    process: runner.DaemonProcess,
    request_id: str,
    frames: list[JSONObject],
    state_root: Path,
    schema_root: Path,
    *,
    timeout: float,
) -> bool:
    """功能：允许终态事件并发时等待固定 `-32010` 不可重试审批 conflict。

    输入：活动 daemon、请求 ID、累计帧、Session/Schema 根与正超时。
    输出：等待期间是否观察到 run 终态。
    不变量：每个并发事件先做 durable 前缀验证；成功响应或其他错误码一律失败。
    失败：超时、未知响应、错误形状或 durability 漂移时抛出 ToolProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    terminal_seen = False
    deadline = time.monotonic() + timeout
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ToolProbeError("approval conflict response timed out")
        try:
            frame = process.next_frame(remaining)
        except queue.Empty as exc:
            raise ToolProbeError("approval conflict response timed out") from exc
        frames.append(frame)
        event_type = _event_type(frame)
        if event_type is not None:
            assert_event_durable(state_root, schema_root, frame)
            terminal_seen = terminal_seen or event_type in runner.TERMINAL_EVENTS
            continue
        if frame.get("id") != request_id:
            raise ToolProbeError("daemon returned an unexpected conflict response id")
        error = frame.get("error")
        if not isinstance(error, dict):
            raise ToolProbeError("late approval response unexpectedly succeeded")
        if error.get("code") != -32010 or error.get("retryable") is not False:
            raise ToolProbeError("late approval response did not return fixed conflict")
        return terminal_seen


def _collect_fault_terminal(
    process: runner.DaemonProcess,
    frames: list[JSONObject],
    state_root: Path,
    schema_root: Path,
    *,
    timeout: float,
) -> str:
    """功能：收集审批故障后的 durable 事件直到唯一 run 终态。

    输入：活动 daemon、累计帧、Session/Schema 根与正超时。
    输出：观察到的 terminal event type。
    不变量：故障阶段不接受 unsolicited response；所有事件在线验证 append-before-observe。
    失败：超时、EOF、响应插入或 durability 违规时抛出 ToolProbeError/ConformanceError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    deadline = time.monotonic() + timeout
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ToolProbeError("approval fault terminal event timed out")
        try:
            frame = process.next_frame(remaining)
        except queue.Empty as exc:
            raise ToolProbeError("approval fault terminal event timed out") from exc
        frames.append(frame)
        event_type = _event_type(frame)
        if event_type is None:
            raise ToolProbeError("daemon emitted an unsolicited fault response")
        assert_event_durable(state_root, schema_root, frame)
        if event_type in runner.TERMINAL_EVENTS:
            return event_type


def _verify_approval_failure_journal(
    state_root: Path,
    schema_root: Path,
    workspace: Path,
    case: JSONObject,
) -> None:
    """功能：验证审批故障最终只有一个决定、零执行、一个 known 结果和一个 run 终态。

    输入：隔离 state/Schema/workspace 根和已验证 approval_failure case。
    输出：完整 fail-closed 持久化与工作区断言成立时无返回值。
    不变量：不以展示文本推断安全结果；按 record kind、ID、decision/source 与 outcomeKnown 校验。
    失败：journal、跨记录状态、计数、终态或 workspace 漂移时抛出 ToolProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    records = _load_session_records(state_root, schema_root)
    expected = tool_validation.require_object(case.get("expected"), "case.expected")
    expected_resolution = tool_validation.require_object(
        expected.get("resolution"), "expected.resolution"
    )
    scenario_calls = list(_tool_calls_from_case(case).values())
    if len(scenario_calls) != 1:
        raise ToolProbeError("approval failure case must contain one tool call")
    tool_call_id = tool_validation.require_string(
        scenario_calls[0].get("toolCallId"), "scenario.toolCallId"
    )

    requested = [record for record in records if record.get("kind") == "approval.requested"]
    resolutions = [record for record in records if record.get("kind") == "approval.resolved"]
    intents = [record for record in records if record.get("kind") == "tool.intent"]
    results = [record for record in records if record.get("kind") == "tool.result"]
    terminals = [record for record in records if record.get("kind") == "run.terminal"]
    if len(requested) != 1 or len(resolutions) != expected_resolution.get("count"):
        raise ToolProbeError("approval fault request/resolution count differs")
    if len(intents) != expected.get("toolIntentCount"):
        raise ToolProbeError("approval fault tool.intent count differs")
    if len(results) != expected.get("toolResultCount"):
        raise ToolProbeError("approval fault tool.result count differs")
    if len(terminals) != 1:
        raise ToolProbeError("approval fault must have one run terminal")
    resolution_data = _record_data(resolutions[0])
    decision = resolution_data.get("decision")
    if (
        not isinstance(decision, dict)
        or decision.get("choice") != expected_resolution.get("decision")
        or resolution_data.get("resolutionSource") != expected_resolution.get("source")
    ):
        raise ToolProbeError("approval fault resolution decision/source differs")
    result_data = _record_data(results[0])
    if (
        result_data.get("toolCallId") != tool_call_id
        or result_data.get("outcomeKnown") is not expected.get("outcomeKnown")
    ):
        raise ToolProbeError("approval fault tool result identity/outcome differs")
    tool_messages = [
        record
        for record in records
        if record.get("kind") == "message.appended"
        and _record_data(record).get("message", {}).get("toolCallId") == tool_call_id
    ]
    if len(tool_messages) != 1:
        raise ToolProbeError("approval fault omitted canonical tool message")
    tool_started_count = sum(
        1
        for record in records
        if record.get("kind") == "event.emitted"
        and _record_data(record).get("event", {}).get("type") == "tool.started"
    )
    if tool_started_count != expected.get("toolStartedCount"):
        raise ToolProbeError("approval fault unexpectedly started the tool")
    if any(record.get("kind") == "run.cancellation_requested" for record in records):
        raise ToolProbeError("approval transport failure was rewritten as run cancellation")
    terminal_status = _record_data(terminals[0]).get("status")
    if terminal_status not in expected.get("terminalStatuses", []):
        raise ToolProbeError("approval fault terminal status differs")
    try:
        state = session_validation.reconstruct_state(
            [{"kind": "session", "sessionId": SESSION_ID}, *records]
        )
    except session_validation.SessionValidationError as exc:
        raise ToolProbeError("approval fault journal invariants failed") from exc
    if state.unfinished_run_ids:
        raise ToolProbeError("approval fault retained an unfinished run")
    _verify_workspace(workspace, case)


def _run_approval_failure_probe(
    command_template: Sequence[str],
    case: JSONObject,
    schema_root: Path,
    *,
    timeout: float,
) -> None:
    """功能：驱动 timeout、stdin EOF、response delivery failure 或 SIGKILL 恢复审批故障。

    输入：安全 daemon argv 模板、approval_failure case、Schema 根与总超时。
    输出：协议故障注入、最终 journal、workspace 与 canary 断言全部通过时无返回值。
    不变量：只执行 runner 临时根中的 faux/file.write；timeout override 仅在 argv/env 双门下注入。
    失败：任一协议、进程、恢复、持久化或泄漏断言失败时抛出脱敏 ToolProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    canary = "approval-fault-canary-" + secrets.token_urlsafe(32)
    process: runner.DaemonProcess | None = None
    stderr_parts: list[str] = []
    frames: list[JSONObject] = []
    try:
        with tempfile.TemporaryDirectory(prefix="approval-failure-") as temporary:
            temporary_root = Path(temporary)
            workspace = temporary_root / "workspace"
            state_root = temporary_root / "state"
            workspace.mkdir()
            state_root.mkdir()
            _seed_workspace(workspace, case)
            command = _render_command(command_template, workspace, state_root)
            fault = tool_validation.require_object(case.get("fault"), "case.fault")
            action = tool_validation.require_string(fault.get("action"), "fault.action")
            environment = _probe_environment(workspace, state_root, canary)
            if action == "timeout":
                if "--conformance" not in command:
                    raise ToolProbeError(
                        "approval timeout requires an exact --conformance argv element"
                    )
                environment["QXNM_FORGE_APPROVAL_TIMEOUT_MS"] = "100"
            pause_predicate = (
                (lambda frame: _event_type(frame) == "approval.requested")
                if action == "response_delivery_failure"
                else None
            )
            process = runner.DaemonProcess(
                command,
                timeout=timeout,
                max_frame_bytes=1_048_576,
                extra_env=environment,
                removed_env=(
                    *provider_runner.CREDENTIAL_ENV,
                    *provider_runner.ENDPOINT_ENV,
                    *provider_runner.PROXY_ENV,
                    "QXNM_FORGE_APPROVAL_TIMEOUT_MS",
                ),
                pause_stdout_after=pause_predicate,
            )

            initialize_request = _initialize_request(True)
            process.send_request(initialize_request)
            initialize = _await_success(
                process, "tool-initialize-1", frames, timeout=timeout
            )
            _assert_capabilities(initialize, case)
            configure_request = _configure_request(case)
            process.send_request(configure_request)
            _await_success(process, "tool-configure-1", frames, timeout=timeout)
            process.send_request(_run_start_request())
            start_response = _await_success(
                process, "tool-run-start-1", frames, timeout=timeout
            )
            result = start_response.get("result")
            run_id = result.get("runId") if isinstance(result, dict) else None
            if not isinstance(run_id, str) or not run_id:
                raise ToolProbeError("run/start omitted opaque runId")

            scenario_calls = _tool_calls_from_case(case)
            approval: JSONObject | None = None
            deadline = time.monotonic() + timeout
            while approval is None:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise ToolProbeError("approval.requested event timed out")
                frame = process.next_frame(remaining)
                frames.append(frame)
                if _event_type(frame) is None:
                    raise ToolProbeError("daemon emitted unsolicited pre-fault response")
                assert_event_durable(state_root, schema_root, frame)
                if _event_type(frame) == "approval.requested":
                    approval_data = _event_data(frame).get("approval")
                    tool_call_id = (
                        approval_data.get("toolCallId")
                        if isinstance(approval_data, dict)
                        else None
                    )
                    expected_call = scenario_calls.get(tool_call_id)
                    if not isinstance(expected_call, dict):
                        raise ToolProbeError("approval references an unknown tool call")
                    approval = _approval_from_event(frame, expected_call)

            terminal_seen = False
            recovery_prefix: bytes | None = None
            if action == "timeout":
                while True:
                    frame = process.next_frame(timeout)
                    frames.append(frame)
                    event_type = _event_type(frame)
                    if event_type is None:
                        raise ToolProbeError("daemon emitted unsolicited timeout response")
                    assert_event_durable(state_root, schema_root, frame)
                    terminal_seen = terminal_seen or event_type in runner.TERMINAL_EVENTS
                    if event_type == "approval.resolved":
                        break
                late_count = fault.get("lateResponseCount")
                if not isinstance(late_count, int):
                    raise ToolProbeError("timeout fault omitted lateResponseCount")
                for sequence in range(1, late_count + 1):
                    request = _approval_response_request(
                        run_id, approval["approvalId"], "allow_once", sequence
                    )
                    process.send_request(request)
                    terminal_seen = _await_conflict_with_events(
                        process,
                        request["id"],
                        frames,
                        state_root,
                        schema_root,
                        timeout=timeout,
                    ) or terminal_seen
                if not terminal_seen:
                    _collect_fault_terminal(
                        process, frames, state_root, schema_root, timeout=timeout
                    )
                process.close_stdin()
                process.wait_for_exit(timeout)
            elif action == "stdin_eof":
                process.close_stdin()
                _collect_fault_terminal(
                    process, frames, state_root, schema_root, timeout=timeout
                )
                process.wait_for_exit(timeout)
            elif action == "response_delivery_failure":
                process.break_stdout_delivery(timeout)
                process.send_request(
                    _approval_response_request(
                        run_id,
                        approval["approvalId"],
                        tool_validation.require_string(
                            fault.get("decision"), "fault.decision"
                        ),
                    )
                )
                process.wait_for_exit(timeout)
            elif action == "force_kill_recovery":
                process.force_kill(timeout)
                journals = _journal_candidates(state_root)
                if len(journals) != 1:
                    raise ToolProbeError("crash point did not leave one session journal")
                recovery_prefix = journals[0].read_bytes()
                stderr_parts.append(process.stderr_text(limit=131_072))
                process.close()
                process = runner.DaemonProcess(
                    command,
                    timeout=timeout,
                    max_frame_bytes=1_048_576,
                    extra_env=environment,
                    removed_env=(
                        *provider_runner.CREDENTIAL_ENV,
                        *provider_runner.ENDPOINT_ENV,
                        *provider_runner.PROXY_ENV,
                        "QXNM_FORGE_APPROVAL_TIMEOUT_MS",
                    ),
                )
                recovery_frames: list[JSONObject] = []
                process.send_request(_initialize_request(False))
                _await_success(
                    process,
                    "tool-initialize-1",
                    recovery_frames,
                    timeout=timeout,
                )
                get_request = _session_get_request()
                process.send_request(get_request)
                _await_success(
                    process,
                    "tool-recovery-get-1",
                    recovery_frames,
                    timeout=timeout,
                )
                frames.extend(recovery_frames)
                process.close_stdin()
                process.wait_for_exit(timeout)
            else:
                raise ToolProbeError("unknown approval failure action")

            stderr_parts.append(process.stderr_text(limit=131_072))
            process.close()
            process = None
            if recovery_prefix is not None:
                journals = _journal_candidates(state_root)
                if len(journals) != 1 or not journals[0].read_bytes().startswith(
                    recovery_prefix
                ):
                    raise ToolProbeError("approval recovery changed the journal prefix")
            _verify_approval_failure_journal(
                state_root, schema_root, workspace, case
            )
            frame_bytes = json.dumps(frames, ensure_ascii=False).encode("utf-8")
            stderr_bytes = "\n".join(stderr_parts).encode("utf-8")
            provider_runner._assert_canary_absent(
                "approval protocol stdout", frame_bytes, canary
            )
            provider_runner._assert_canary_absent(
                "approval daemon stderr", stderr_bytes, canary
            )
            provider_runner._scan_tree_for_canary(state_root, canary)
            provider_runner._scan_tree_for_canary(workspace, canary)
    except Exception as exc:
        safe_message = str(exc).replace(canary, "[REDACTED]")
        if isinstance(exc, ToolProbeError) and safe_message == str(exc):
            raise
        raise ToolProbeError(safe_message or type(exc).__name__) from exc
    finally:
        if process is not None:
            process.close()


def run_probe(
    command_template: Sequence[str],
    case: JSONObject,
    schema_root: Path,
    *,
    timeout: float,
) -> None:
    """功能：在全新工作区和状态根中驱动一个工具/审批 daemon 案例。

    输入：安全 argv 模板、已验证案例、Schema 根与总超时。
    输出：协议、审批、持久化、工作区和泄漏断言全部通过时无返回值。
    不变量：不经过 shell；只执行案例允许的 file.read/file.write；真实凭据先移除。
    失败：任一实现或安全契约不符时抛出脱敏 ToolProbeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if case.get("kind") == "approval_failure":
        _run_approval_failure_probe(
            command_template,
            case,
            schema_root,
            timeout=timeout,
        )
        return

    canary = "tool-canary-" + secrets.token_urlsafe(32)
    process: runner.DaemonProcess | None = None
    frames: list[JSONObject] = []
    requests: list[JSONObject] = []
    try:
        with tempfile.TemporaryDirectory(prefix="tool-conformance-") as temporary:
            temporary_root = Path(temporary)
            workspace = temporary_root / "workspace"
            state_root = temporary_root / "state"
            workspace.mkdir()
            state_root.mkdir()
            _seed_workspace(workspace, case)
            command = _render_command(command_template, workspace, state_root)
            process = runner.DaemonProcess(
                command,
                timeout=timeout,
                max_frame_bytes=1_048_576,
                extra_env=_probe_environment(workspace, state_root, canary),
                removed_env=(
                    *provider_runner.CREDENTIAL_ENV,
                    *provider_runner.ENDPOINT_ENV,
                    *provider_runner.PROXY_ENV,
                ),
            )
            try:
                initialize_request = _initialize_request(
                    case.get("mode") == "interactive"
                )
                requests.append(initialize_request)
                process.send_request(initialize_request)
                initialize = _await_success(
                    process, "tool-initialize-1", frames, timeout=timeout
                )
                _assert_capabilities(initialize, case)

                configure_request = _configure_request(case)
                requests.append(configure_request)
                process.send_request(configure_request)
                _await_success(process, "tool-configure-1", frames, timeout=timeout)

                start_request = _run_start_request()
                requests.append(start_request)
                process.send_request(start_request)
                start_response = _await_success(
                    process, "tool-run-start-1", frames, timeout=timeout
                )
                result = start_response.get("result")
                run_id = result.get("runId") if isinstance(result, dict) else None
                if not isinstance(run_id, str) or not run_id:
                    raise ToolProbeError("run/start omitted opaque runId")

                control = tool_validation.require_object(
                    case.get("control"), "case.control"
                )
                scenario_calls = _tool_calls_from_case(case)
                control_sent = False
                terminal_seen = False
                deadline = time.monotonic() + timeout
                while not terminal_seen:
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        raise ToolProbeError("tool run terminal event timed out")
                    try:
                        frame = process.next_frame(remaining)
                    except queue.Empty as exc:
                        raise ToolProbeError(
                            "tool run terminal event timed out"
                        ) from exc
                    frames.append(frame)
                    event_type = _event_type(frame)
                    if event_type is None:
                        raise ToolProbeError("daemon emitted an unsolicited response")
                    assert_event_durable(state_root, schema_root, frame)
                    if event_type == "approval.requested":
                        approval_data = _event_data(frame).get("approval")
                        tool_call_id = (
                            approval_data.get("toolCallId")
                            if isinstance(approval_data, dict)
                            else None
                        )
                        expected_call = scenario_calls.get(tool_call_id)
                        if not isinstance(expected_call, dict):
                            raise ToolProbeError(
                                "approval references an unknown tool call"
                            )
                        approval = _approval_from_event(frame, expected_call)
                        if control_sent:
                            raise ToolProbeError("case produced more than one approval")
                        action = control.get("action")
                        if action == "respond":
                            request = _approval_response_request(
                                run_id,
                                approval["approvalId"],
                                tool_validation.require_string(
                                    control.get("decision"), "control.decision"
                                ),
                            )
                            requests.append(request)
                            process.send_request(request)
                            response = _await_success(
                                process,
                                "tool-approval-response-1",
                                frames,
                                timeout=timeout,
                            )
                            assert_control_response_durable(
                                state_root,
                                schema_root,
                                request,
                                response,
                            )
                            if response["result"].get("accepted") is not True:
                                raise ToolProbeError(
                                    "approval/respond was not accepted"
                                )
                        elif action == "cancel_on_approval":
                            request = _cancel_request(run_id, 1)
                            requests.append(request)
                            process.send_request(request)
                            response, concurrent_terminal = _await_success_with_events(
                                process,
                                "tool-run-cancel-1",
                                frames,
                                state_root,
                                schema_root,
                                timeout=timeout,
                            )
                            terminal_seen = terminal_seen or concurrent_terminal
                            assert_control_response_durable(
                                state_root,
                                schema_root,
                                request,
                                response,
                            )
                            if (
                                response["result"].get("cancellationState")
                                != "requested"
                            ):
                                raise ToolProbeError(
                                    "run/cancel did not accept cancellation"
                                )
                            duplicate = _cancel_request(run_id, 2)
                            requests.append(duplicate)
                            process.send_request(duplicate)
                            duplicate_response, concurrent_terminal = (
                                _await_success_with_events(
                                    process,
                                    "tool-run-cancel-2",
                                    frames,
                                    state_root,
                                    schema_root,
                                    timeout=timeout,
                                )
                            )
                            terminal_seen = terminal_seen or concurrent_terminal
                            assert_control_response_durable(
                                state_root,
                                schema_root,
                                duplicate,
                                duplicate_response,
                            )
                            if duplicate_response["result"].get(
                                "cancellationState"
                            ) not in ("alreadyRequested", "terminal"):
                                raise ToolProbeError(
                                    "repeated run/cancel was not idempotent"
                                )
                        else:
                            raise ToolProbeError(
                                "unexpected approval in a no-control case"
                            )
                        control_sent = True
                    if event_type in runner.TERMINAL_EVENTS:
                        terminal_seen = True
            finally:
                process.close()

            _verify_trace(requests, frames, case, schema_root)
            _verify_workspace(workspace, case)
            _verify_final_journal(state_root, schema_root, case)
            frame_bytes = json.dumps(frames, ensure_ascii=False).encode("utf-8")
            stderr_bytes = process.stderr_text(limit=131_072).encode("utf-8")
            provider_runner._assert_canary_absent(
                "protocol stdout", frame_bytes, canary
            )
            provider_runner._assert_canary_absent("daemon stderr", stderr_bytes, canary)
            provider_runner._scan_tree_for_canary(state_root, canary)
            provider_runner._scan_tree_for_canary(workspace, canary)
    except Exception as exc:
        safe_message = str(exc).replace(canary, "[REDACTED]")
        if isinstance(exc, ToolProbeError) and safe_message == str(exc):
            raise
        raise ToolProbeError(safe_message or type(exc).__name__) from exc


def build_argument_parser() -> argparse.ArgumentParser:
    """功能：构建工具/审批静态与 opt-in daemon runner 参数解析器。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser(
        description="Validate tool/approval fixtures and optionally probe a daemon."
    )
    parser.add_argument("--fixture", type=Path, default=tool_validation.DEFAULT_FIXTURE)
    parser.add_argument("--schema-root", type=Path, default=DEFAULT_SCHEMA_ROOT)
    parser.add_argument(
        "--daemon-command-json", type=provider_runner.parse_command_json
    )
    parser.add_argument("--case", choices=tool_validation.EXPECTED_CASES)
    parser.add_argument(
        "--kind", choices=("daemon", "approval_failure", "portable_recovery")
    )
    parser.add_argument("--timeout", type=float, default=8.0)
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    """功能：运行静态工具门禁，并在显式提供 argv 时逐案例运行动态探针。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = build_argument_parser().parse_args(argv)
    if args.timeout <= 0 or args.timeout > 60:
        print("tool conformance: timeout must be in (0, 60]", file=sys.stderr)
        return 2
    try:
        daemon_count, failure_count, recovery_count = run_static_gate(
            args.fixture, args.schema_root
        )
        if args.daemon_command_json is None:
            print(
                "PASS: tool/approval static "
                f"{daemon_count} daemon cases / {failure_count} failure cases / "
                f"{recovery_count} recovery case"
            )
            return 0
        fixture = tool_validation.load_fixture(args.fixture)
        selected = [
            tool_validation.require_object(value, "fixture case")
            for value in tool_validation.require_array(
                fixture.get("cases"), "fixture.cases"
            )
            if isinstance(value, dict)
            and (args.case is None or value.get("name") == args.case)
            and (args.kind is None or value.get("kind") == args.kind)
        ]
        probe_count = 0
        for case in selected:
            if case.get("kind") == "portable_recovery":
                tool_validation.validate_portable_recovery_case(case, args.schema_root)
                print(f"PASS: {case['name']} (static recovery)")
                continue
            run_probe(
                args.daemon_command_json,
                case,
                args.schema_root,
                timeout=args.timeout,
            )
            probe_count += 1
            print(f"PASS: {case['name']}")
        print(f"PASS: tool/approval daemon probes {probe_count}")
        return 0
    except (
        ToolProbeError,
        tool_validation.ToolValidationError,
        runner.ConformanceError,
    ) as exc:
        print(f"tool conformance failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
