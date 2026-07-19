#!/usr/bin/env python3
"""验证 portable Session branch selection 与 context compaction 语义。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from pathlib import Path
import sys
from typing import Any, Sequence

import runner
import session_validation


JSONObject = dict[str, Any]

EXPECTED_MUTATION_ERRORS: JSONObject = {
    "staleExpectedHead": {
        "code": -32010,
        "retryable": True,
        "kind": "stale_session_head",
    },
    "sessionBusy": {"code": -32004, "retryable": True, "kind": "session_busy"},
    "recordNotFound": {
        "code": -32602,
        "retryable": False,
        "kind": "record_not_found",
    },
    "branchNotQuiescent": {
        "code": -32010,
        "retryable": False,
        "kind": "branch_not_quiescent",
    },
    "invalidCompactionBoundary": {
        "code": -32602,
        "retryable": False,
        "kind": "invalid_compaction_boundary",
    },
    "invalidCompactionTokens": {
        "code": -32602,
        "retryable": False,
        "kind": "invalid_compaction_tokens",
    },
    "journalCorrupt": {
        "code": -32008,
        "retryable": False,
        "kind": "journal_corrupt",
    },
}


class BranchCompactionError(Exception):
    """branch/compaction fixture 的结构或跨记录不变量失败。"""


@dataclass(frozen=True)
class BranchCompactionState:
    """静态验证后 selected branch 的可移植投影。"""

    session_id: str
    selected_head_record_id: str
    compaction_record_id: str | None
    messages: tuple[JSONObject, ...]


def require_object(value: Any, context: str) -> JSONObject:
    """功能：要求动态值是字符串键 JSON 对象并返回。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, dict) or not all(isinstance(key, str) for key in value):
        raise BranchCompactionError(f"{context} must be a JSON object")
    return value


def require_string(value: Any, context: str) -> str:
    """功能：要求动态值是非空字符串并返回。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, str) or not value:
        raise BranchCompactionError(f"{context} must be a non-empty string")
    return value


def validate_mutation_errors(expectations: JSONObject) -> None:
    """功能：核对 branch/compaction 机器夹具中的完整 portable 错误映射。

    输入：已通过严格 JSON 解码的 expectations 对象。
    输出：映射与 ADR 0011 固定值逐项相同时正常返回。
    不变量：错误码、retryable 与 kind 必须整体精确匹配，不接受隐式默认值。
    失败：缺失、多余或任一字段漂移时抛出 BranchCompactionError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    actual = require_object(expectations.get("mutationErrors"), "mutationErrors")
    if actual != EXPECTED_MUTATION_ERRORS:
        raise BranchCompactionError(
            "branch/compaction mutation error mapping differs from ADR 0011"
        )


def parent_chain(
    records: dict[str, JSONObject], head_record_id: str
) -> list[JSONObject]:
    """功能：沿 earlier-only parent 边重建从根到指定 head 的唯一链。

    输入：按 ID 索引的已验证记录和目标 head。
    输出：根到 head 的有序记录列表。
    不变量：每个 ID 最多访问一次，循环或未知 parent 立即拒绝。
    失败：目标/父记录缺失或形成循环时抛出 BranchCompactionError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    chain: list[JSONObject] = []
    seen: set[str] = set()
    current_id: str | None = head_record_id
    while current_id is not None:
        if current_id in seen or current_id not in records:
            raise BranchCompactionError(
                "parent chain is cyclic or references unknown ID"
            )
        seen.add(current_id)
        current = records[current_id]
        chain.append(current)
        parent = current.get("parentId")
        if parent is not None and not isinstance(parent, str):
            raise BranchCompactionError("parentId must be null or an opaque ID")
        current_id = parent
    chain.reverse()
    return chain


def require_quiescent_chain(
    records: dict[str, JSONObject], head_record_id: str
) -> None:
    """功能：确认 branch target 链没有未终止 run、工具或审批。

    输入：全记录索引和待选择 earlier target。
    输出：该 parent 链处于可安全继续的边界时成功。
    不变量：只扫描 target 祖先，不让未选 sibling 污染判断。
    失败：发现任何 unresolved 生命周期时抛出 BranchCompactionError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    accepted: set[str] = set()
    terminal: set[str] = set()
    intents: set[str] = set()
    results: set[str] = set()
    approvals: set[str] = set()
    resolutions: set[str] = set()
    for record in parent_chain(records, head_record_id):
        data = require_object(record.get("data"), "record.data")
        kind = record.get("kind")
        if kind == "run.accepted":
            accepted.add(require_string(data.get("runId"), "run.accepted.runId"))
        elif kind == "run.terminal":
            terminal.add(require_string(data.get("runId"), "run.terminal.runId"))
        elif kind == "tool.intent":
            intents.add(
                require_string(data.get("toolCallId"), "tool.intent.toolCallId")
            )
        elif kind == "tool.result":
            results.add(
                require_string(data.get("toolCallId"), "tool.result.toolCallId")
            )
        elif kind == "approval.requested":
            approval = require_object(
                data.get("approval"), "approval.requested.approval"
            )
            approvals.add(require_string(approval.get("approvalId"), "approvalId"))
        elif kind == "approval.resolved":
            resolutions.add(
                require_string(data.get("approvalId"), "approval.resolved.approvalId")
            )
    if accepted - terminal or intents - results or approvals - resolutions:
        raise BranchCompactionError("branch target is not quiescent")


def message_from_record(record: JSONObject, context: str) -> JSONObject:
    """功能：从 message.appended 记录提取 canonical message 对象。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if record.get("kind") != "message.appended":
        raise BranchCompactionError(f"{context} must reference message.appended")
    data = require_object(record.get("data"), f"{context}.data")
    return require_object(data.get("message"), f"{context}.message")


def validate_compaction(
    record: JSONObject, records: dict[str, JSONObject]
) -> tuple[JSONObject, list[JSONObject]]:
    """功能：验证 compaction 引用并返回 summary 与 retained source 链。

    输入：一个 context.compacted 记录和全记录索引。
    输出：canonical summary message 及 retained boundary 到 source 的记录链。
    不变量：summary 紧接 source，retained 边界是 source 祖先上的 user message。
    失败：引用、角色、token 或顺序不变量失败时拒绝整个 journal。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if record.get("kind") != "context.compacted":
        raise BranchCompactionError("validate_compaction received another record kind")
    data = require_object(record.get("data"), "context.compacted.data")
    source_id = require_string(data.get("sourceLeafRecordId"), "sourceLeafRecordId")
    retained_id = require_string(
        data.get("firstRetainedRecordId"), "firstRetainedRecordId"
    )
    summary_message_id = require_string(
        data.get("summaryMessageId"), "summaryMessageId"
    )
    summary_record_id = require_string(record.get("parentId"), "compaction.parentId")
    if summary_record_id not in records:
        raise BranchCompactionError("compaction summary record is unknown")
    summary_record = records[summary_record_id]
    if summary_record.get("parentId") != source_id:
        raise BranchCompactionError(
            "summary record is not an immediate source descendant"
        )
    summary = message_from_record(summary_record, "compaction summary")
    if (
        summary.get("messageId") != summary_message_id
        or summary.get("role") != "assistant"
        or summary.get("finishReason") != "stop"
    ):
        raise BranchCompactionError("compaction summary identity or role is invalid")
    content = summary.get("content")
    if (
        not isinstance(content, list)
        or len(content) != 1
        or not isinstance(content[0], dict)
        or content[0].get("type") != "text"
        or not isinstance(content[0].get("text"), str)
        or not content[0]["text"]
    ):
        raise BranchCompactionError("compaction summary must contain one nonempty text")
    source_chain = parent_chain(records, source_id)
    source_ids = [
        require_string(item.get("recordId"), "recordId") for item in source_chain
    ]
    if retained_id not in source_ids:
        raise BranchCompactionError("retained boundary is not on source ancestry")
    retained_index = source_ids.index(retained_id)
    retained_message = message_from_record(
        source_chain[retained_index], "first retained record"
    )
    if retained_message.get("role") != "user":
        raise BranchCompactionError("first retained record must be a user message")
    tokens_before = data.get("tokensBefore")
    tokens_after = data.get("tokensAfter")
    if (
        not isinstance(tokens_before, int)
        or isinstance(tokens_before, bool)
        or (
            tokens_after is not None
            and (
                not isinstance(tokens_after, int)
                or isinstance(tokens_after, bool)
                or tokens_after > tokens_before
            )
        )
    ):
        raise BranchCompactionError("compaction token estimates are invalid")
    return summary, source_chain[retained_index:]


def project_selected_messages(
    selected_chain: list[JSONObject], records: dict[str, JSONObject]
) -> tuple[list[JSONObject], str | None]:
    """功能：按最新 compaction 投影 selected parent chain 的 Provider messages。

    输入：根到 selected head 的链和全记录索引。
    输出：精确 message 顺序与生效 compaction record ID。
    不变量：summary 只出现一次，早期前缀和 sibling 永不进入投影。
    失败：最新 compaction 无效时拒绝而不回退全历史。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    compaction_indexes = [
        index
        for index, record in enumerate(selected_chain)
        if record.get("kind") == "context.compacted"
    ]
    if not compaction_indexes:
        return (
            [
                message_from_record(record, "selected message")
                for record in selected_chain
                if record.get("kind") == "message.appended"
            ],
            None,
        )
    compaction_index = compaction_indexes[-1]
    compaction = selected_chain[compaction_index]
    summary, retained_records = validate_compaction(compaction, records)
    messages = [summary]
    messages.extend(
        message_from_record(record, "retained message")
        for record in retained_records
        if record.get("kind") == "message.appended"
    )
    messages.extend(
        message_from_record(record, "post-compaction message")
        for record in selected_chain[compaction_index + 1 :]
        if record.get("kind") == "message.appended"
    )
    return messages, require_string(compaction.get("recordId"), "compaction recordId")


def validate_values(values: list[JSONObject]) -> BranchCompactionState:
    """功能：验证完整 journal tree、selected-head 转换和 compaction 投影。

    输入：已逐行通过公共 JSON Schema 的 header/records。
    输出：selected head、active compaction 与精确消息投影。
    不变量：普通 append 只能延伸 selected head，selection 目标等于自身 parent。
    失败：append/tree/branch/compaction 任一歧义均拒绝整个 Session。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not values or values[0].get("kind") != "session":
        raise BranchCompactionError("journal must begin with a Session header")
    session_id = require_string(values[0].get("sessionId"), "header.sessionId")
    records: dict[str, JSONObject] = {}
    selected_head: str | None = None
    message_ids: set[str] = set()
    for expected_seq, record in enumerate(values[1:], start=1):
        if record.get("seq") != expected_seq or record.get("sessionId") != session_id:
            raise BranchCompactionError("journal seq or Session identity is invalid")
        record_id = require_string(record.get("recordId"), "record.recordId")
        if record_id in records:
            raise BranchCompactionError("journal contains a duplicate recordId")
        parent_id = record.get("parentId")
        if parent_id is not None and parent_id not in records:
            raise BranchCompactionError("parentId must reference an earlier record")
        if record.get("kind") == "branch.selected":
            data = require_object(record.get("data"), "branch.selected.data")
            target = require_string(data.get("leafRecordId"), "leafRecordId")
            if parent_id != target:
                raise BranchCompactionError(
                    "branch target must equal selection parentId"
                )
            require_quiescent_chain(records, target)
        elif parent_id != selected_head:
            raise BranchCompactionError("ordinary append did not extend selected head")
        records[record_id] = record
        selected_head = record_id
        if record.get("kind") == "message.appended":
            message = message_from_record(record, "message record")
            message_id = require_string(message.get("messageId"), "message.messageId")
            if message_id in message_ids:
                raise BranchCompactionError("journal contains a duplicate messageId")
            message_ids.add(message_id)
        if record.get("kind") == "context.compacted":
            validate_compaction(record, records)
    if selected_head is None:
        raise BranchCompactionError("branch fixture contains no records")
    selected_chain = parent_chain(records, selected_head)
    messages, compaction_id = project_selected_messages(selected_chain, records)
    return BranchCompactionState(
        session_id=session_id,
        selected_head_record_id=selected_head,
        compaction_record_id=compaction_id,
        messages=tuple(messages),
    )


def validate_case(case_dir: Path, schema_root: Path) -> BranchCompactionState:
    """功能：加载 branch fixture、执行 Schema/语义验证并核对机器期望。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    validator = session_validation.build_line_validator(schema_root)
    _, values = session_validation.load_journal(case_dir / "journal.jsonl", validator)
    state = validate_values(values)
    expectations = require_object(
        runner.strict_json_loads(
            (case_dir / "expectations.json").read_text(encoding="utf-8")
        ),
        "expectations",
    )
    if expectations.get("sessionId") != state.session_id:
        raise BranchCompactionError("expected Session ID differs from journal")
    if expectations.get("selectedHeadRecordId") != state.selected_head_record_id:
        raise BranchCompactionError("selected head differs from expectation")
    if expectations.get("compactionRecordId") != state.compaction_record_id:
        raise BranchCompactionError("active compaction differs from expectation")
    selected_ids = [message.get("messageId") for message in state.messages]
    if selected_ids != expectations.get("selectedMessageIds"):
        raise BranchCompactionError(
            "selected message projection differs from expectation"
        )
    excluded = expectations.get("excludedMessageIds")
    if not isinstance(excluded, list) or set(selected_ids).intersection(excluded):
        raise BranchCompactionError(
            "excluded sibling/prefix message entered projection"
        )
    validate_mutation_errors(expectations)
    return state


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    """功能：解析 branch fixture 与公共 Schema 根路径。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(
        description="验证 portable branch/compaction fixture"
    )
    parser.add_argument(
        "--case-dir",
        type=Path,
        default=root / "CONFORMANCE/fixtures/session/branch-compaction-v0.1",
    )
    parser.add_argument("--schema-root", type=Path, default=root / "SPEC/schemas")
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    """功能：运行 branch/compaction 静态 conformance 并输出精简摘要。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = parse_args(argv)
    try:
        state = validate_case(args.case_dir.resolve(), args.schema_root.resolve())
    except (
        OSError,
        runner.ProtocolViolation,
        session_validation.SessionValidationError,
        BranchCompactionError,
    ) as exc:
        print(f"BRANCH/COMPACTION FAIL: {exc}", file=sys.stderr)
        return 1
    print(
        "BRANCH/COMPACTION PASS: "
        f"head={state.selected_head_record_id}, "
        f"compaction={state.compaction_record_id}, messages={len(state.messages)}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
