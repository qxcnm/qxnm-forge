#!/usr/bin/env python3
"""工具、审批与非幂等恢复机器夹具的标准库语义验证器。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
from pathlib import Path, PurePosixPath
import sys
from typing import Any, Sequence

import runner
import session_validation


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_FIXTURE = ROOT / "CONFORMANCE/fixtures/tools/tool-approval-cases.json"
EXPECTED_CASES = (
    "safe_file_read_without_approval",
    "headless_file_write_default_deny",
    "interactive_allow_once",
    "interactive_deny",
    "unknown_tool",
    "invalid_tool_arguments",
    "sequential_source_order",
    "tool_result_in_next_provider_context",
    "cancel_while_waiting_approval",
    "cancel_finishes_remaining_source_calls",
    "append_before_event",
    "approval_timeout_late_duplicate_conflict",
    "approval_stdin_eof_disconnect",
    "approval_response_delivery_failure",
    "approval_sigkill_recovery",
    "non_idempotent_not_replayed",
)
SAFE_DYNAMIC_TOOLS = frozenset(("file.read", "file.write", "fixture.unknown"))
SEMANTIC_EVENTS = frozenset(
    (
        "run.started",
        "turn.started",
        "turn.completed",
        "tool.requested",
        "approval.requested",
        "approval.resolved",
        "tool.started",
        "tool.completed",
        "run.completed",
        "run.failed",
        "run.cancelled",
        "run.interrupted",
    )
)

JSONObject = dict[str, Any]


class ToolValidationError(Exception):
    """表示工具/审批夹具结构、语义或恢复断言不一致。"""


def require_object(value: Any, context: str) -> JSONObject:
    """功能：要求一个夹具节点为 JSON 对象并返回窄化值。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, dict):
        raise ToolValidationError(f"{context} must be an object")
    return value


def require_array(value: Any, context: str) -> list[Any]:
    """功能：要求一个夹具节点为 JSON 数组并返回窄化值。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, list):
        raise ToolValidationError(f"{context} must be an array")
    return value


def require_string(value: Any, context: str) -> str:
    """功能：要求一个夹具节点为非空字符串并返回窄化值。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, str) or not value:
        raise ToolValidationError(f"{context} must be a non-empty string")
    return value


def load_fixture(path: Path = DEFAULT_FIXTURE) -> JSONObject:
    """功能：严格读取工具/审批机器夹具并拒绝重复键和非标准数值。

    输入：受版本控制的 JSON 夹具路径。
    输出：顶层 JSON 对象。
    不变量：不执行夹具中的任何工具或命令。
    失败：读取、UTF-8、JSON 或顶层类型无效时抛出 ToolValidationError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        text = path.read_text(encoding="utf-8", errors="strict")
        value = runner.strict_json_loads(text)
    except (OSError, UnicodeError, runner.ProtocolViolation) as exc:
        raise ToolValidationError("cannot load tool/approval fixture") from exc
    return require_object(value, "tool/approval fixture")


def case_by_name(fixture: JSONObject, case_name: str) -> JSONObject:
    """功能：按稳定名称从已验证夹具选择唯一案例。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    matches = [
        value
        for value in require_array(fixture.get("cases"), "fixture.cases")
        if isinstance(value, dict) and value.get("name") == case_name
    ]
    if len(matches) != 1:
        raise ToolValidationError("tool/approval case name is missing or duplicated")
    return matches[0]


def validate_relative_path(value: Any, context: str) -> str:
    """功能：验证夹具路径是无穿越、无绝对前缀的 portable 相对路径。

    输入：夹具路径值及稳定诊断上下文。
    输出：原始 POSIX 相对路径字符串。
    不变量：拒绝空段、`.`、`..`、反斜杠、NUL 和绝对路径。
    失败：边界不满足时抛出 ToolValidationError，且不访问文件系统。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    path_text = require_string(value, context)
    path = PurePosixPath(path_text)
    segments = path_text.split("/")
    if (
        path.is_absolute()
        or "\\" in path_text
        or "\x00" in path_text
        or any(part in ("", ".", "..") for part in segments)
    ):
        raise ToolValidationError(f"{context} must be a safe portable relative path")
    return path_text


def _scenario_tool_calls(case: JSONObject, context: str) -> list[JSONObject]:
    """功能：提取首个 faux turn 的完整工具调用并拒绝可执行命令型夹具。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    scenario = require_object(case.get("scenario"), f"{context}.scenario")
    calls: list[JSONObject] = []
    for index, raw_step in enumerate(
        require_array(scenario.get("steps"), f"{context}.scenario.steps")
    ):
        step = require_object(raw_step, f"{context}.scenario.steps[{index}]")
        step_type = require_string(step.get("type"), f"{context}.step.type")
        if step_type == "tool_call":
            name = require_string(step.get("name"), f"{context}.tool.name")
            if name not in SAFE_DYNAMIC_TOOLS:
                raise ToolValidationError(
                    f"{context} dynamic fixture contains a forbidden tool"
                )
            arguments = require_object(
                step.get("arguments"), f"{context}.tool.arguments"
            )
            path_value = arguments.get("path")
            if path_value is not None:
                validate_relative_path(path_value, f"{context}.tool.arguments.path")
            calls.append(step)
        elif step_type != "text":
            raise ToolValidationError(
                f"{context} dynamic fixture permits only text and tool_call steps"
            )
    if not calls:
        raise ToolValidationError(f"{context} must contain at least one tool call")
    return calls


def _validate_daemon_case(case: JSONObject, context: str) -> None:
    """功能：验证动态 daemon 案例的安全输入、控制动作和可观察预期一致。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    mode = require_string(case.get("mode"), f"{context}.mode")
    control = require_object(case.get("control"), f"{context}.control")
    action = require_string(control.get("action"), f"{context}.control.action")
    if mode == "headless" and action != "none":
        raise ToolValidationError(f"{context} headless case cannot send approvals")
    if mode == "interactive" and action == "none":
        raise ToolValidationError(f"{context} interactive case needs a control action")

    seed_paths: set[str] = set()
    for index, raw_seed in enumerate(
        require_array(case.get("seedFiles"), f"{context}.seedFiles")
    ):
        seed = require_object(raw_seed, f"{context}.seedFiles[{index}]")
        path = validate_relative_path(seed.get("path"), f"{context}.seed.path")
        if path in seed_paths:
            raise ToolValidationError(f"{context} has duplicate seed paths")
        seed_paths.add(path)
        if not isinstance(seed.get("content"), str):
            raise ToolValidationError(f"{context} seed content must be text")

    calls = _scenario_tool_calls(case, context)
    expected = require_object(case.get("expected"), f"{context}.expected")
    expected_calls = [
        require_object(value, f"{context}.expected.toolCalls")
        for value in require_array(
            expected.get("toolCalls"), f"{context}.expected.toolCalls"
        )
    ]
    source_pairs = [(value.get("toolCallId"), value.get("name")) for value in calls]
    expected_pairs = [
        (value.get("toolCallId"), value.get("name")) for value in expected_calls
    ]
    if source_pairs != expected_pairs:
        raise ToolValidationError(
            f"{context} tool expectations must preserve Provider source order"
        )

    event_order = require_array(
        expected.get("semanticEventOrder"), f"{context}.expected.semanticEventOrder"
    )
    if any(value not in SEMANTIC_EVENTS for value in event_order):
        raise ToolValidationError(f"{context} contains an unknown semantic event")
    terminal = require_string(expected.get("terminal"), f"{context}.terminal")
    if not event_order or event_order[-1] != f"run.{terminal}":
        raise ToolValidationError(f"{context} semantic order must end at its terminal")

    approval_count = expected.get("approvalCount")
    expected_approval_count = 0 if action == "none" else 1
    if approval_count != expected_approval_count:
        raise ToolValidationError(f"{context} approval count conflicts with control")
    if event_order.count("approval.requested") != expected_approval_count:
        raise ToolValidationError(f"{context} approval request event count is invalid")
    if event_order.count("approval.resolved") != expected_approval_count:
        raise ToolValidationError(
            f"{context} approval resolution event count is invalid"
        )

    scenario = require_object(case.get("scenario"), f"{context}.scenario")
    continuations = require_array(
        scenario.get("continuations", []), f"{context}.scenario.continuations"
    )
    provider_turn_count = expected.get("providerTurnCount")
    if provider_turn_count != 1 + len(continuations):
        raise ToolValidationError(f"{context} provider turn count is inconsistent")

    journal = require_object(expected.get("journal"), f"{context}.expected.journal")
    if journal.get("toolIntentCount") != len(calls):
        raise ToolValidationError(f"{context} journal intent count is inconsistent")
    if journal.get("toolResultCount") != len(calls):
        raise ToolValidationError(f"{context} journal result count is inconsistent")
    if journal.get("approvalRequestedCount", 0) != expected_approval_count:
        raise ToolValidationError(
            f"{context} journal approval request count is invalid"
        )
    if journal.get("approvalResolvedCount", 0) != expected_approval_count:
        raise ToolValidationError(f"{context} journal approval result count is invalid")
    cancellation_count = 1 if action == "cancel_on_approval" else 0
    if journal.get("cancellationRequestedCount", 0) != cancellation_count:
        raise ToolValidationError(f"{context} cancellation intent count is invalid")

    expected_paths: set[str] = set()
    for raw_expected in require_array(
        expected.get("workspace"), f"{context}.expected.workspace"
    ):
        item = require_object(raw_expected, f"{context}.workspace item")
        path = validate_relative_path(item.get("path"), f"{context}.workspace.path")
        if path in expected_paths:
            raise ToolValidationError(f"{context} has duplicate workspace expectations")
        expected_paths.add(path)


def _validate_approval_failure_case(case: JSONObject, context: str) -> None:
    """功能：验证审批 timeout、断连、响应交付失败和崩溃恢复夹具的封闭语义。

    输入：一个 approval_failure case 和稳定诊断上下文。
    输出：固定 file.write、fault 与零执行预期完全一致时无返回值。
    不变量：timeout/EOF 只有一轮 text continuation；交付失败/崩溃不含 continuation、任意命令或已有目标。
    失败：结构虽过 Schema 但跨字段语义漂移时抛出 ToolValidationError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if case.get("mode") != "interactive":
        raise ToolValidationError(f"{context} must negotiate interactive approvals")
    seed_files = require_array(case.get("seedFiles"), f"{context}.seedFiles")
    if seed_files:
        raise ToolValidationError(f"{context} failure workspace must start empty")
    calls = _scenario_tool_calls(case, context)
    if len(calls) != 1 or calls[0].get("name") != "file.write":
        raise ToolValidationError(f"{context} must contain one file.write call")
    scenario = require_object(case.get("scenario"), f"{context}.scenario")
    continuations = require_array(
        scenario.get("continuations", []), f"{context}.scenario.continuations"
    )

    fault = require_object(case.get("fault"), f"{context}.fault")
    action = require_string(fault.get("action"), f"{context}.fault.action")
    expected_by_action: dict[str, tuple[str, str, int, list[str]]] = {
        "timeout": ("deny", "timeout", 2, ["completed"]),
        "stdin_eof": ("deny", "disconnect", 0, ["completed", "interrupted"]),
        "response_delivery_failure": (
            "allow_once",
            "client",
            0,
            ["completed", "interrupted"],
        ),
        "force_kill_recovery": ("deny", "disconnect", 0, ["interrupted"]),
    }
    if action not in expected_by_action:
        raise ToolValidationError(f"{context} has an unknown approval fault")
    if action in ("timeout", "stdin_eof"):
        if len(continuations) != 1:
            raise ToolValidationError(f"{context} requires one denial continuation")
        continuation = require_object(
            continuations[0], f"{context}.scenario.continuations[0]"
        )
        steps = require_array(
            continuation.get("steps"), f"{context}.scenario.continuations[0].steps"
        )
        if len(steps) != 1 or require_object(
            steps[0], f"{context}.scenario.continuations[0].steps[0]"
        ).get("type") != "text":
            raise ToolValidationError(f"{context} denial continuation must be text only")
    elif continuations:
        raise ToolValidationError(f"{context} cannot run a Provider continuation")
    decision, source, conflict_count, terminals = expected_by_action[action]
    if action == "timeout" and (
        fault.get("timeoutMs") != 100 or fault.get("lateResponseCount") != 2
    ):
        raise ToolValidationError(f"{context} timeout override must be 100 ms")
    if action == "response_delivery_failure" and fault.get("decision") != "allow_once":
        raise ToolValidationError(f"{context} delivery fault must exercise allow_once")

    expected = require_object(case.get("expected"), f"{context}.expected")
    resolution = require_object(
        expected.get("resolution"), f"{context}.expected.resolution"
    )
    if resolution != {"count": 1, "decision": decision, "source": source}:
        raise ToolValidationError(f"{context} resolution expectation differs")
    conflicts = require_object(
        expected.get("conflictResponses"), f"{context}.expected.conflicts"
    )
    if conflicts != {"count": conflict_count, "code": -32010, "retryable": False}:
        raise ToolValidationError(f"{context} conflict expectation differs")
    if (
        expected.get("toolIntentCount") != 1
        or expected.get("toolResultCount") != 1
        or expected.get("toolStartedCount") != 0
        or expected.get("outcomeKnown") is not True
        or expected.get("terminalStatuses") != terminals
    ):
        raise ToolValidationError(f"{context} fail-closed counts differ")
    workspace = require_array(
        expected.get("workspace"), f"{context}.expected.workspace"
    )
    if len(workspace) != 1:
        raise ToolValidationError(f"{context} must assert one absent target")
    target = require_object(workspace[0], f"{context}.workspace[0]")
    call_path = require_object(calls[0].get("arguments"), f"{context}.arguments").get(
        "path"
    )
    if target != {"path": call_path, "state": "absent"}:
        raise ToolValidationError(f"{context} workspace target differs")


def _records_for_tool(
    records: Sequence[JSONObject], kind: str, tool_call_id: str
) -> list[JSONObject]:
    """功能：从 portable journal 记录中选择指定工具调用和 kind 的记录。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return [
        record
        for record in records
        if record.get("kind") == kind
        and isinstance(record.get("data"), dict)
        and record["data"].get("toolCallId") == tool_call_id
    ]


def validate_portable_recovery_case(case: JSONObject, schema_root: Path) -> None:
    """功能：复用 portable Session 夹具证明非幂等未知结果未被再次执行。

    输入：恢复案例和 bundled Schema 根目录。
    输出：before/after 数量、未知结果和 interrupted 终态一致时无返回值。
    不变量：只允许固定 portable-v0.1 夹具；不启动 daemon、Provider 或工具。
    失败：路径、Schema、恢复语义或预期不一致时抛出 ToolValidationError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    fixture_name = validate_relative_path(
        case.get("fixtureDirectory"), "recovery.fixtureDirectory"
    )
    if fixture_name != "CONFORMANCE/fixtures/session/portable-v0.1":
        raise ToolValidationError(
            "recovery case must use the governed portable fixture"
        )
    fixture_path = ROOT / fixture_name
    try:
        before, after = session_validation.validate_case(fixture_path, schema_root)
    except session_validation.SessionValidationError as exc:
        raise ToolValidationError(
            "portable recovery fixture validation failed"
        ) from exc
    expected = require_object(case.get("expected"), "recovery.expected")
    tool_call_id = require_string(expected.get("toolCallId"), "recovery.toolCallId")
    before_intents = _records_for_tool(before.records, "tool.intent", tool_call_id)
    after_intents = _records_for_tool(after.records, "tool.intent", tool_call_id)
    after_results = _records_for_tool(after.records, "tool.result", tool_call_id)
    if len(before_intents) != expected.get("intentCountBefore"):
        raise ToolValidationError("portable recovery before intent count differs")
    if len(after_intents) != expected.get("intentCountAfter"):
        raise ToolValidationError("portable recovery replayed a tool intent")
    if len(after_results) != expected.get("resultCountAfter"):
        raise ToolValidationError("portable recovery result count differs")
    if not after_results or after_results[0]["data"].get(
        "outcomeKnown"
    ) is not expected.get("outcomeKnown"):
        raise ToolValidationError("portable recovery outcomeKnown differs")
    terminal = expected.get("terminal")
    terminal_records = [
        record
        for record in after.records
        if record.get("kind") == "run.terminal"
        and isinstance(record.get("data"), dict)
        and record["data"].get("status") == terminal
    ]
    if not terminal_records:
        raise ToolValidationError("portable recovery terminal differs")


def validate_fixture(fixture: JSONObject, schema_root: Path | None = None) -> None:
    """功能：验证 16 个工具/审批案例的名称、静态安全和恢复语义。

    输入：已严格解析的夹具及可选 bundled Schema 根目录。
    输出：所有案例满足公共约束时无返回值。
    不变量：动态案例不含 process/shell/terminal；恢复案例不执行工具。
    失败：任一结构或交叉字段不一致时抛出 ToolValidationError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if fixture.get("schemaVersion") != "0.1":
        raise ToolValidationError("tool/approval fixture schemaVersion must be 0.1")
    raw_cases = require_array(fixture.get("cases"), "fixture.cases")
    cases = [require_object(value, "fixture case") for value in raw_cases]
    names = [require_string(case.get("name"), "case.name") for case in cases]
    if tuple(names) != EXPECTED_CASES:
        raise ToolValidationError("tool/approval case names or order differ")
    if len(set(names)) != len(names):
        raise ToolValidationError("tool/approval case names must be unique")
    for index, case in enumerate(cases):
        context = f"case[{index}] {names[index]}"
        kind = require_string(case.get("kind"), f"{context}.kind")
        if kind == "daemon":
            _validate_daemon_case(case, context)
        elif kind == "approval_failure":
            _validate_approval_failure_case(case, context)
        elif kind == "portable_recovery":
            if schema_root is not None:
                validate_portable_recovery_case(case, schema_root)
        else:
            raise ToolValidationError(f"{context} has an unknown kind")


def static_self_test(
    fixture_path: Path = DEFAULT_FIXTURE, schema_root: Path | None = None
) -> tuple[int, int, int]:
    """功能：运行工具/审批离线静态门禁并返回 daemon/故障/恢复案例数。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    fixture = load_fixture(fixture_path)
    validate_fixture(fixture, schema_root)
    cases = require_array(fixture.get("cases"), "fixture.cases")
    daemon_count = sum(
        1 for value in cases if isinstance(value, dict) and value.get("kind") == "daemon"
    )
    failure_count = sum(
        1
        for value in cases
        if isinstance(value, dict) and value.get("kind") == "approval_failure"
    )
    return daemon_count, failure_count, len(cases) - daemon_count - failure_count


def build_argument_parser() -> argparse.ArgumentParser:
    """功能：构建工具/审批静态验证器参数解析器。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser(description="验证工具与审批公共机器夹具")
    parser.add_argument("--fixture", type=Path, default=DEFAULT_FIXTURE)
    parser.add_argument("--schema-root", type=Path, default=ROOT / "SPEC/schemas")
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    """功能：执行工具/审批静态门禁并输出稳定计数。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = build_argument_parser().parse_args(argv)
    try:
        daemon_count, failure_count, recovery_count = static_self_test(
            args.fixture, args.schema_root
        )
        print(
            "PASS: tool/approval static "
            f"{daemon_count} daemon cases / {failure_count} failure cases / "
            f"{recovery_count} recovery case"
        )
        return 0
    except ToolValidationError as exc:
        print(f"tool/approval validation failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
