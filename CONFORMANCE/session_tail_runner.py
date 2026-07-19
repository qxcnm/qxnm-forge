#!/usr/bin/env python3
"""以真实 daemon open 验证 Session journal 尾部修复与严格损坏边界。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
from copy import deepcopy
from dataclasses import dataclass
from hashlib import sha256
import json
import os
from pathlib import Path
import queue
import re
import stat
import sys
import tempfile
from typing import Any, Sequence

import runner
from schema_validation import SchemaValidationError, validate_protocol_trace
import session_runner
import session_validation


JSONObject = dict[str, Any]
RECOVERY_NAMESPACE = "org.agent-session.recovery"
RECOVERY_ACTION = "truncate_uncommitted_tail"
UNKNOWN_TOOL_CALL_ID = "tool-call-side-effect-1"
UNKNOWN_EXTENSION_RECORD_ID = "record-tail-contract-unknown-extension"
UNCOMMITTED_VALID_RECORD_ID = "record-uncommitted-valid-tail"
BACKUP_NAME = re.compile(r"^journal\.recovery-([a-f0-9]{64})\.bak$")
REMOVED_ENV = (
    "OPENAI_API_KEY",
    "ANTHROPIC_API_KEY",
    "AZURE_OPENAI_API_KEY",
    "AWS_ACCESS_KEY_ID",
    "AWS_SECRET_ACCESS_KEY",
    "AWS_SESSION_TOKEN",
    "GOOGLE_API_KEY",
    "GEMINI_API_KEY",
    "MISTRAL_API_KEY",
    "OPENROUTER_API_KEY",
    "GITHUB_TOKEN",
    "GH_TOKEN",
)


class SessionTailConformanceError(Exception):
    """表示尾部恢复 fixture、协议响应或持久化结果违反共同合同。"""


@dataclass(frozen=True)
class PreparedCase:
    """保存一个隔离黑盒案例的原始字节、路径与确定性期望。"""

    name: str
    mutation: str
    expected: str
    session_id: str
    directory: Path
    journal_path: Path
    committed_prefix: bytes
    original_bytes: bytes
    tail_bytes: bytes
    original_sha256: str
    base_values: list[JSONObject]


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    """功能：解析尾部案例、基础 journal、Schema、daemon 命令和资源上限。

    输入：可选 argv；daemon 命令是一个不经 shell 的 JSON 字符串数组模板。
    输出：包含已解析 Path、timeout 和可选命令模板的 argparse Namespace。
    不变量：默认入口只验证静态合同，不启动实现或访问网络。
    失败：参数类型或必需值错误时由 argparse 以非零状态终止。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(
        description="验证 portable Session journal 尾部恢复与完整坏行失败关闭"
    )
    parser.add_argument(
        "--cases",
        type=Path,
        default=root
        / "CONFORMANCE/fixtures/session/journal-tail-recovery-cases.json",
    )
    parser.add_argument(
        "--base-journal",
        type=Path,
        default=root
        / "CONFORMANCE/fixtures/session/portable-v0.1/journal.before-recovery.jsonl",
    )
    parser.add_argument("--schema-root", type=Path, default=root / "SPEC/schemas")
    parser.add_argument("--daemon-command-json")
    parser.add_argument(
        "--work-root",
        type=Path,
        help="保留黑盒状态的空目录；省略时使用并清理临时目录",
    )
    parser.add_argument("--timeout", type=float, default=10.0)
    return parser.parse_args(argv)


def build_schema_validator(schema_root: Path, schema_name: str) -> Any:
    """功能：加载全部公共 Schema 并为指定相对名称创建 Draft 2020-12 验证器。

    输入：Schema 根目录及相对根目录的 Schema 文件名。
    输出：带完整跨文件引用 Registry 的 Draft202012Validator。
    不变量：严格读取所有 Schema，不从网络解析远程引用。
    失败：依赖缺失、Schema 无效、重复/缺失 ID 或目标不存在时抛共同验证错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        import jsonschema
        from referencing import Registry, Resource
    except ImportError as exc:
        raise SessionTailConformanceError(
            "session tail Schema validation requires jsonschema and referencing"
        ) from exc

    resources: list[tuple[str, Any]] = []
    schemas: dict[str, JSONObject] = {}
    for path in sorted(schema_root.rglob("*.schema.json")):
        try:
            schema = session_validation.require_object(
                runner.strict_json_loads(path.read_text(encoding="utf-8")), str(path)
            )
            jsonschema.Draft202012Validator.check_schema(schema)
            schema_id = session_validation.require_string(schema.get("$id"), str(path))
            resource = Resource.from_contents(schema)
        except (OSError, ValueError, jsonschema.SchemaError) as exc:
            raise SessionTailConformanceError(f"invalid Schema {path}: {exc}") from exc
        relative = str(path.relative_to(schema_root))
        schemas[relative] = schema
        resources.append((schema_id, resource))
    if schema_name not in schemas:
        raise SessionTailConformanceError(f"missing Schema {schema_root / schema_name}")
    return jsonschema.Draft202012Validator(
        schemas[schema_name], registry=Registry().with_resources(resources)
    )


def load_contract(path: Path, validator: Any) -> JSONObject:
    """功能：严格加载并验证固定六案例尾部恢复机器合同。

    输入：案例 JSON 路径和对应 Draft 2020-12 验证器。
    输出：通过 Schema 与精确语义检查的顶层对象。
    不变量：namespace、action、backup 模板、错误三元组和案例顺序不可漂移。
    失败：I/O、重复键、Schema 或固定值不一致时抛出明确错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    value = session_validation.require_object(
        runner.strict_json_loads(path.read_text(encoding="utf-8")), str(path)
    )
    errors = sorted(
        validator.iter_errors(value),
        key=lambda error: (
            tuple(str(part) for part in error.absolute_path),
            error.message,
        ),
    )
    if errors:
        location = "/".join(str(part) for part in errors[0].absolute_path) or "<root>"
        raise SessionTailConformanceError(
            f"tail cases Schema violation at {location}: {errors[0].message}"
        )
    contract = session_validation.require_object(value.get("contract"), "contract")
    expected_contract = {
        "namespace": RECOVERY_NAMESPACE,
        "action": RECOVERY_ACTION,
        "backupFileTemplate": "journal.recovery-{originalSha256}.bak",
        "unterminatedPolicy": (
            "uncommitted_without_independent_durability_witness"
        ),
        "corruptError": {
            "code": -32008,
            "retryable": False,
            "kind": "journal_corrupt",
        },
    }
    if contract != expected_contract:
        raise SessionTailConformanceError("tail recovery contract constants drifted")
    expected_cases = [
        ("invalid_torn_tail", "invalid_json_no_lf", "repair"),
        ("valid_json_no_lf", "valid_record_no_lf", "repair"),
        ("complete_invalid_utf8", "invalid_utf8_lf", "journal_corrupt"),
        ("complete_invalid_json", "invalid_json_lf", "journal_corrupt"),
        ("complete_invalid_schema", "invalid_schema_lf", "journal_corrupt"),
        (
            "recursive_duplicate_key",
            "recursive_duplicate_key_lf",
            "journal_corrupt",
        ),
    ]
    cases = session_validation.require_array(value.get("cases"), "cases")
    actual_cases = [
        (
            session_validation.require_object(item, "case").get("name"),
            item.get("mutation"),
            item.get("expected"),
        )
        for item in cases
    ]
    if actual_cases != expected_cases:
        raise SessionTailConformanceError("tail recovery case order or semantics drifted")
    return value


def canonical_line(value: JSONObject) -> bytes:
    """功能：把一个 portable journal 对象编码为紧凑 UTF-8 并附加唯一 LF。

    输入：只含 JSON 值的对象。
    输出：无 BOM、无 CR、以一个 LF 结尾的确定性字节。
    不变量：不排序键，保留构造时字段顺序以便字节前缀断言。
    失败：对象含不可 JSON 序列化值时传播 TypeError 或 ValueError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return (
        json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n"
    ).encode("utf-8")


def validate_values(values: list[JSONObject], validator: Any, context: str) -> None:
    """功能：逐值执行 journal Schema 并重建跨记录状态不变量。

    输入：header 加 records、journal Schema 验证器和安全上下文标签。
    输出：全部合法时无返回值。
    不变量：同时检查字段 Schema、连续 journal seq、父引用、事件和恢复身份。
    失败：任一行或状态不合法时抛 SessionTailConformanceError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    for index, value in enumerate(values, start=1):
        errors = sorted(
            validator.iter_errors(value),
            key=lambda error: (
                tuple(str(part) for part in error.absolute_path),
                error.message,
            ),
        )
        if errors:
            location = "/".join(str(part) for part in errors[0].absolute_path)
            raise SessionTailConformanceError(
                f"{context}:{index} Schema violation at "
                f"{location or '<root>'}: {errors[0].message}"
            )
    try:
        session_validation.reconstruct_state(values)
    except session_validation.SessionValidationError as exc:
        raise SessionTailConformanceError(f"{context}: {exc}") from exc


def build_committed_prefix(
    base_path: Path, validator: Any, session_id: str
) -> tuple[bytes, list[JSONObject]]:
    """功能：从公共 crash fixture 构造带 event gap 和未知扩展的有效已提交前缀。

    输入：公共恢复前 journal、行验证器和本案例 Session ID。
    输出：LF 结尾的 committed bytes 及其严格对象列表。
    不变量：只把最后 durable event 从 8 改为 10，并追加一个未知 extension 记录。
    失败：基础 fixture 结构漂移、Schema 或状态不变量失败时拒绝生成案例。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    _, loaded = session_validation.load_journal(base_path, validator)
    values = deepcopy(loaded)
    header = values[0]
    header["sessionId"] = session_id
    header["extensions"] = {
        "org.example.future-header": {
            "preserve": ["literal", {"nested": True}],
        }
    }
    event_records: list[JSONObject] = []
    for record in values[1:]:
        record["sessionId"] = session_id
        if record.get("kind") == "event.emitted":
            event = session_validation.require_object(
                session_validation.require_object(record.get("data"), "event data").get(
                    "event"
                ),
                "event",
            )
            event["sessionId"] = session_id
            event_records.append(record)
    event_seqs = [record["data"]["event"]["seq"] for record in event_records]
    if event_seqs != list(range(1, 9)):
        raise SessionTailConformanceError("portable base event sequence drifted")
    event_records[-1]["data"]["event"]["seq"] = 10
    records = values[1:]
    last_record = records[-1]
    unknown_extension: JSONObject = {
        "schemaVersion": "0.1",
        "kind": "extension",
        "recordId": UNKNOWN_EXTENSION_RECORD_ID,
        "sessionId": session_id,
        "seq": len(records) + 1,
        "parentId": last_record["recordId"],
        "time": "2026-07-10T08:00:22Z",
        "data": {
            "namespace": "org.example.future-session",
            "value": {
                "opaque": True,
                "nested": {"items": [1, "two", {"three": 3}]},
            },
        },
        "extensions": {
            "org.example.future-record": {
                "preserve": {"enabled": True, "version": 7}
            }
        },
    }
    values.append(unknown_extension)
    validate_values(values, validator, "generated committed prefix")
    state = session_validation.reconstruct_state(values)
    if [event["seq"] for event in state.events] != [1, 2, 3, 4, 5, 6, 7, 10]:
        raise SessionTailConformanceError("generated committed prefix lacks event gap")
    return b"".join(canonical_line(value) for value in values), values


def build_recursive_duplicate_tail(
    session_id: str, next_seq: int, parent_id: str
) -> bytes:
    """功能：构造仅在递归 value 对象内含重复键的完整 LF journal 行。

    输入：受控 Session ID、下一 journal seq 和已存在 parent record ID。
    输出：外层字段和 Schema 形状均正常、内层 `same` 重复且以 LF 结尾的 UTF-8。
    不变量：手写 JSON 只用于制造结构化 API 无法表示的重复键负例。
    失败：输入不来自 runner 生成的受控 opaque ID 时调用方不得使用本函数。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    encoded_session = json.dumps(session_id, ensure_ascii=True)
    encoded_parent = json.dumps(parent_id, ensure_ascii=True)
    return (
        "{"
        '"schemaVersion":"0.1",'
        '"kind":"extension",'
        '"recordId":"record-recursive-duplicate-tail",'
        f'"sessionId":{encoded_session},'
        f'"seq":{next_seq},'
        f'"parentId":{encoded_parent},'
        '"time":"2026-07-10T08:00:23Z",'
        '"data":{"namespace":"org.example.duplicate",'
        '"value":{"outer":{"same":1,"same":2}}}'
        "}\n"
    ).encode("utf-8")


def build_tail(
    mutation: str,
    session_id: str,
    next_seq: int,
    parent_id: str,
    validator: Any,
) -> bytes:
    """功能：按固定 mutation 名生成精确无 LF 或完整坏行字节。

    输入：六种冻结 mutation 之一、Session/seq/parent 和 journal 验证器。
    输出：待追加到有效 committed prefix 的原始尾部字节。
    不变量：两个 repair 尾部不含 LF；四个 corruption 尾部均以一个 LF 完成。
    失败：未知 mutation 或“valid”记录未通过公共 Schema 时拒绝生成。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if mutation == "invalid_json_no_lf":
        return b'{"schemaVersion":"0.1","kind":"extension"'
    if mutation == "valid_record_no_lf":
        record: JSONObject = {
            "schemaVersion": "0.1",
            "kind": "extension",
            "recordId": UNCOMMITTED_VALID_RECORD_ID,
            "sessionId": session_id,
            "seq": next_seq,
            "parentId": parent_id,
            "time": "2026-07-10T08:00:23Z",
            "data": {
                "namespace": "org.example.uncommitted",
                "value": {"validJsonAndSchema": True},
            },
        }
        errors = list(validator.iter_errors(record))
        if errors:
            raise SessionTailConformanceError(
                "valid no-LF case no longer matches journal Schema"
            )
        return canonical_line(record)[:-1]
    if mutation == "invalid_utf8_lf":
        return b"\xff\n"
    if mutation == "invalid_json_lf":
        return b'{"schemaVersion":\n'
    if mutation == "invalid_schema_lf":
        return b'{"kind":"extension"}\n'
    if mutation == "recursive_duplicate_key_lf":
        return build_recursive_duplicate_tail(session_id, next_seq, parent_id)
    raise SessionTailConformanceError(f"unknown tail mutation {mutation!r}")


def validate_generated_cases(
    fixture: JSONObject, base_path: Path, journal_validator: Any
) -> None:
    """功能：静态证明六案例生成的 LF 边界与合法无 LF 记录没有漂移。

    输入：已验证案例合同、公共基础 journal 和 journal Schema 验证器。
    输出：六个 tail 都符合其 repair/corrupt 分类时无返回值。
    不变量：valid no-LF 是 Schema-valid 完整记录；递归重复键由 strict parser 拒绝。
    失败：生成器、基础 fixture、分类或 strict parser 行为漂移时抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    cases = session_validation.require_array(fixture.get("cases"), "cases")
    for raw_case in cases:
        case = session_validation.require_object(raw_case, "case")
        name = session_validation.require_string(case.get("name"), "case.name")
        session_id = "session-tail-static-" + name.replace("_", "-")
        _, values = build_committed_prefix(base_path, journal_validator, session_id)
        records = values[1:]
        tail = build_tail(
            session_validation.require_string(case.get("mutation"), "case.mutation"),
            session_id,
            int(records[-1]["seq"]) + 1,
            session_validation.require_string(records[-1].get("recordId"), "recordId"),
            journal_validator,
        )
        expected = session_validation.require_string(case.get("expected"), "expected")
        if expected == "repair" and (not tail or b"\n" in tail):
            raise SessionTailConformanceError(f"{name} is not an unterminated tail")
        if expected == "journal_corrupt" and not tail.endswith(b"\n"):
            raise SessionTailConformanceError(f"{name} is not an LF-complete bad line")
        if case.get("mutation") == "valid_record_no_lf":
            value = session_validation.require_object(
                runner.strict_json_loads(tail.decode("utf-8")), name
            )
            if list(journal_validator.iter_errors(value)):
                raise SessionTailConformanceError("valid no-LF tail failed Schema")
        if case.get("mutation") == "recursive_duplicate_key_lf":
            try:
                runner.strict_json_loads(tail.decode("utf-8").removesuffix("\n"))
            except runner.ProtocolViolation:
                pass
            else:
                raise SessionTailConformanceError(
                    "strict parser accepted recursively duplicated key"
                )


def prepare_cases(
    root: Path,
    fixture: JSONObject,
    base_path: Path,
    journal_validator: Any,
) -> tuple[Path, Path, list[PreparedCase]]:
    """功能：在空临时根中创建六个互相隔离的真实 journal 故障案例。

    输入：测试根、固定合同、公共基础 journal 和 journal Schema 验证器。
    输出：state root、workspace 和每案不可变元数据。
    不变量：不覆盖已有目录；每个原始 journal 等于有效 committed prefix 加精确 tail。
    失败：路径已存在、I/O 或生成验证失败时停止且不启动 daemon。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    state_root = root / "state"
    sessions_root = state_root / "sessions"
    workspace = root / "workspace"
    if state_root.exists() or workspace.exists():
        raise SessionTailConformanceError("tail probe root must be empty")
    sessions_root.mkdir(parents=True, exist_ok=False)
    workspace.mkdir(parents=True, exist_ok=False)
    prepared: list[PreparedCase] = []
    cases = session_validation.require_array(fixture.get("cases"), "cases")
    for raw_case in cases:
        case = session_validation.require_object(raw_case, "case")
        name = session_validation.require_string(case.get("name"), "case.name")
        mutation = session_validation.require_string(case.get("mutation"), "mutation")
        expected = session_validation.require_string(case.get("expected"), "expected")
        session_id = "session-tail-" + name.replace("_", "-")
        committed, values = build_committed_prefix(
            base_path, journal_validator, session_id
        )
        records = values[1:]
        tail = build_tail(
            mutation,
            session_id,
            int(records[-1]["seq"]) + 1,
            session_validation.require_string(records[-1].get("recordId"), "recordId"),
            journal_validator,
        )
        directory = sessions_root / session_id
        (directory / "artifacts").mkdir(parents=True, exist_ok=False)
        journal_path = directory / "journal.jsonl"
        original = committed + tail
        journal_path.write_bytes(original)
        prepared.append(
            PreparedCase(
                name=name,
                mutation=mutation,
                expected=expected,
                session_id=session_id,
                directory=directory,
                journal_path=journal_path,
                committed_prefix=committed,
                original_bytes=original,
                tail_bytes=tail,
                original_sha256=sha256(original).hexdigest(),
                base_values=values,
            )
        )
    return state_root, workspace, prepared


def build_requests(cases: Sequence[PreparedCase]) -> list[JSONObject]:
    """功能：构造一次 initialize 与每案例一个 session/get 的有序 RPC 请求。

    输入：六个 PreparedCase。
    输出：可由共同 DaemonProcess 顺序发送的 JSON-RPC 对象列表。
    不变量：afterSeq=7 同时观察原 gap 后 seq=10 与新恢复事件。
    失败：调用方传入空案例时仍只生成 initialize，随后语义验证会拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    requests: list[JSONObject] = [
        {
            "jsonrpc": "2.0",
            "id": "session-tail-initialize",
            "method": "initialize",
            "params": {
                "protocolVersions": ["0.1"],
                "client": {"name": "session-tail-conformance", "version": "0.1.0"},
                "capabilities": {"eventReplay": True},
            },
        }
    ]
    requests.extend(
        {
            "jsonrpc": "2.0",
            "id": "session-tail-get-" + case.name,
            "method": "session/get",
            "params": {"sessionId": case.session_id, "afterSeq": 7},
        }
        for case in cases
    )
    return requests


def run_open(
    command: list[str],
    sessions_root: Path,
    requests: list[JSONObject],
    schema_root: Path,
    timeout: float,
) -> list[JSONObject]:
    """功能：启动一个隔离 daemon 并完成六个 Session open 请求。

    输入：无 shell argv、Session 根、请求、Schema 根和总响应 timeout。
    输出：通过协议生命周期与 Schema 验证的全部响应帧。
    不变量：禁用 live Provider 并删除常见凭据；只访问 runner 创建的临时 Session。
    失败：启动、timeout、协议、Schema 或输出违规时抛出共同错误并清理进程树。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    process = runner.DaemonProcess(
        command,
        timeout=timeout,
        max_frame_bytes=1_048_576,
        removed_env=REMOVED_ENV,
        extra_env={
            "QXNM_FORGE_CONFORMANCE": "1",
            "QXNM_FORGE_SESSION_ROOT": str(sessions_root),
            "QXNM_FORGE_LIVE_TESTS": "0",
            "QXNM_FORGE_ALLOW_LIVE_PROVIDER_TESTS": "0",
            "LIVE_PROVIDER_TESTS": "0",
        },
    )
    try:
        frames = process.run(requests, settle_seconds=0.03)
    except queue.Empty as exc:
        raise SessionTailConformanceError(
            f"daemon did not answer Session open within {timeout:.3f}s"
        ) from exc
    runner.TraceValidator(requests).validate(frames)
    validate_protocol_trace(requests, frames, schema_root)
    return frames


def response_for(frames: Sequence[JSONObject], request_id: str) -> JSONObject:
    """功能：取得指定 request ID 的唯一 JSON-RPC 响应。

    输入：daemon 帧和已发送的 request ID。
    输出：包含 result 或 error 的唯一响应对象。
    不变量：通知和其他响应不会被误关联。
    失败：缺失、重复或非对象响应时抛 SessionTailConformanceError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    matches = [frame for frame in frames if frame.get("id") == request_id]
    if len(matches) != 1:
        raise SessionTailConformanceError(
            f"request {request_id!r} did not receive exactly one response"
        )
    return session_validation.require_object(matches[0], request_id)


def assert_corrupt_response(response: JSONObject, context: str) -> None:
    """功能：验证完整坏行只返回固定非重试 journal_corrupt 错误。

    输入：session/get 响应及不含原始数据的案例标签。
    输出：错误 code/retryable/details.kind 精确匹配时无返回值。
    不变量：不解析或约束实现可安全本地化的 message 文本。
    失败：成功响应、错误缺失或三元组漂移时抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    error = session_validation.require_object(response.get("error"), f"{context}.error")
    details = session_validation.require_object(
        error.get("details"), f"{context}.error.details"
    )
    if (
        error.get("code") != -32008
        or error.get("retryable") is not False
        or details.get("kind") != "journal_corrupt"
    ):
        raise SessionTailConformanceError(
            f"{context} did not return -32008/non-retryable/journal_corrupt"
        )


def recovery_backups(directory: Path) -> list[Path]:
    """功能：列出 Session 目录内所有规范 recovery backup 候选。

    输入：runner 创建的单 Session 目录。
    输出：按 basename 排序且匹配完整内容寻址名称的直接子路径。
    不变量：不递归、不跟随符号链接、不把 lock/artifact 文件当作备份。
    失败：目录无法读取时传播 OSError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return sorted(
        path
        for path in directory.iterdir()
        if BACKUP_NAME.fullmatch(path.name) is not None
    )


def assert_success_snapshot(
    response: JSONObject,
    case: PreparedCase,
    state: session_validation.SessionState,
) -> None:
    """功能：验证修复后 session/get 使用 event seq gap 和 durable 最大值投影。

    输入：成功响应、案例和从实际 journal 重建的状态。
    输出：Session 身份、quiescence、latestSeq 与 afterSeq events 一致时无返回值。
    不变量：messages 不用于替代 durable record 验证；event 序号按原值返回不重编号。
    失败：响应错误、活动 run 或事件投影不一致时抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    result = session_validation.require_object(
        response.get("result"), f"{case.name}.result"
    )
    if result.get("sessionId") != case.session_id:
        raise SessionTailConformanceError(f"{case.name} returned another sessionId")
    if result.get("activeRunId") is not None:
        raise SessionTailConformanceError(f"{case.name} remained active after recovery")
    latest = session_validation.latest_event_seq(state)
    if result.get("latestSeq") != latest:
        raise SessionTailConformanceError(f"{case.name} latestSeq is not event max")
    events = session_validation.require_array(result.get("events"), "session/get.events")
    returned = [
        session_validation.require_object(event, "session/get event").get("seq")
        for event in events
    ]
    expected = session_validation.event_seqs_after(state, 7)
    if returned != expected:
        raise SessionTailConformanceError(
            f"{case.name} renumbered or lost event sequence gaps"
        )


def inspect_repaired_case(
    case: PreparedCase,
    response: JSONObject,
    journal_validator: Any,
) -> tuple[bytes, dict[str, bytes]]:
    """功能：验证一个修复案例的完整 backup、extension、恢复和响应合同。

    输入：准备案例、当前 session/get 响应和 journal Schema 验证器。
    输出：用于第二次 reopen 比较的 journal bytes 与 backup bytes 映射。
    不变量：committed prefix 逐字节保留；只追加一条诊断和四条语义恢复记录。
    失败：路径、hash、Schema、顺序、event、unknown extension 或精确计数不符时失败。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    raw, values = session_validation.load_journal(case.journal_path, journal_validator)
    if not raw.startswith(case.committed_prefix):
        raise SessionTailConformanceError(
            f"{case.name} rewrote committed bytes while repairing tail"
        )
    if values[: len(case.base_values)] != case.base_values:
        raise SessionTailConformanceError(
            f"{case.name} changed unknown extensions or committed values"
        )
    appended = values[len(case.base_values) :]
    if [record.get("kind") for record in appended] != [
        "extension",
        "tool.result",
        "message.appended",
        "run.terminal",
        "event.emitted",
    ]:
        raise SessionTailConformanceError(
            f"{case.name} recovery append order or exact count is not canonical"
        )
    recovery = appended[0]
    expected_backup = f"journal.recovery-{case.original_sha256}.bak"
    expected_value = {
        "action": RECOVERY_ACTION,
        "discardedBytes": len(case.tail_bytes),
        "backupFile": expected_backup,
        "originalSha256": case.original_sha256,
    }
    if recovery.get("seq") != len(case.base_values):
        raise SessionTailConformanceError(f"{case.name} recovery seq is not next")
    if recovery.get("parentId") != UNKNOWN_EXTENSION_RECORD_ID:
        raise SessionTailConformanceError(
            f"{case.name} recovery parent is not the selected committed head"
        )
    data = session_validation.require_object(recovery.get("data"), "recovery.data")
    if data != {"namespace": RECOVERY_NAMESPACE, "value": expected_value}:
        raise SessionTailConformanceError(
            f"{case.name} recovery extension shape is not frozen value"
        )
    if any(value.get("recordId") == UNCOMMITTED_VALID_RECORD_ID for value in values):
        raise SessionTailConformanceError(
            f"{case.name} accepted a valid JSON value without LF as committed"
        )

    backups = recovery_backups(case.directory)
    if [path.name for path in backups] != [expected_backup]:
        raise SessionTailConformanceError(
            f"{case.name} did not create exactly the content-addressed backup"
        )
    backup = backups[0]
    info = os.lstat(backup)
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISREG(info.st_mode):
        raise SessionTailConformanceError(f"{case.name} backup is not a regular file")
    backup_bytes = backup.read_bytes()
    if backup_bytes != case.original_bytes:
        raise SessionTailConformanceError(
            f"{case.name} backup is not the complete original journal bytes"
        )
    if sha256(backup_bytes).hexdigest() != case.original_sha256:
        raise SessionTailConformanceError(f"{case.name} backup digest mismatch")

    state = session_validation.reconstruct_state(values)
    if state.unfinished_run_ids or state.interrupted_run_ids != ["run-crash-1"]:
        raise SessionTailConformanceError(f"{case.name} did not recover one run")
    if state.unknown_outcome_tool_call_ids != [UNKNOWN_TOOL_CALL_ID]:
        raise SessionTailConformanceError(
            f"{case.name} lost or duplicated the unknown tool outcome"
        )
    intents = [
        record
        for record in state.records
        if record.get("kind") == "tool.intent"
        and record["data"].get("toolCallId") == UNKNOWN_TOOL_CALL_ID
    ]
    results = [
        record
        for record in state.records
        if record.get("kind") == "tool.result"
        and record["data"].get("toolCallId") == UNKNOWN_TOOL_CALL_ID
    ]
    tool_messages = [
        record
        for record in state.records
        if record.get("kind") == "message.appended"
        and record["data"].get("message", {}).get("toolCallId")
        == UNKNOWN_TOOL_CALL_ID
    ]
    terminals = [
        record
        for record in state.records
        if record.get("kind") == "run.terminal"
        and record["data"].get("runId") == "run-crash-1"
    ]
    if not (
        len(intents) == len(results) == len(tool_messages) == len(terminals) == 1
    ):
        raise SessionTailConformanceError(
            f"{case.name} recovery result/message/terminal is not exact-one"
        )
    if results[0]["data"].get("outcomeKnown") is not False:
        raise SessionTailConformanceError(
            f"{case.name} ambiguous tool recovery is not outcomeKnown=false"
        )
    event_seqs = [int(event["seq"]) for event in state.events]
    if event_seqs[:8] != [1, 2, 3, 4, 5, 6, 7, 10]:
        raise SessionTailConformanceError(f"{case.name} changed the durable event gap")
    if len(event_seqs) != 9 or event_seqs[-1] <= 10:
        raise SessionTailConformanceError(
            f"{case.name} recovery did not allocate above greatest durable event seq"
        )
    assert_success_snapshot(response, case, state)
    return raw, {backup.name: backup_bytes}


def inspect_corrupt_case(case: PreparedCase, response: JSONObject) -> None:
    """功能：验证完整 LF 坏行返回错误且 journal 与 recovery backup 零变化。

    输入：准备案例及本次 session/get 响应。
    输出：错误与持久化副作用均符合合同时无返回值。
    不变量：允许独立 lock 辅助文件，但不允许 journal rewrite 或 recovery backup。
    失败：响应漂移、journal 字节变化或出现规范 backup 时抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    assert_corrupt_response(response, case.name)
    if case.journal_path.read_bytes() != case.original_bytes:
        raise SessionTailConformanceError(
            f"{case.name} modified an LF-complete corrupt journal"
        )
    if recovery_backups(case.directory):
        raise SessionTailConformanceError(
            f"{case.name} created a recovery backup for complete corruption"
        )


def render_daemon_command(
    raw_template: str, state_root: Path, workspace: Path, session_id: str
) -> list[str]:
    """功能：严格解析并渲染尾部 runner 的已知 daemon argv 占位符。

    输入：JSON argv 模板、临时 state/workspace 及一个诊断用 Session ID。
    输出：保持原 argv 边界且不经 shell 的命令列表。
    不变量：只替换 session_runner 已公开的五个路径/身份占位符。
    失败：模板不是非空字符串数组时由共同 parser 拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    template = session_runner.parse_command_template(raw_template)
    return session_runner.render_command(
        template,
        {
            "workspace": str(workspace),
            "stateRoot": str(state_root),
            "sessionsRoot": str(state_root / "sessions"),
            "sessionId": session_id,
            "repoRoot": str(Path(__file__).resolve().parents[1]),
        },
    )


def run_probe(args: argparse.Namespace, root: Path) -> None:
    """功能：用两个真实 daemon 生命周期验证六案例首次 open 与 clean reopen。

    输入：已解析参数和空测试根。
    输出：全部动态断言通过时无返回值并由 main 输出摘要。
    不变量：首轮六案例共享一个隔离 state root；第二轮不改动已完成修复结果。
    失败：任一协议、持久化、幂等或安全断言失败时抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    schema_root = args.schema_root.resolve()
    case_validator = build_schema_validator(
        schema_root, "session/journal-tail-recovery-cases.schema.json"
    )
    journal_validator = build_schema_validator(
        schema_root, "session/journal.schema.json"
    )
    fixture = load_contract(args.cases.resolve(), case_validator)
    state_root, workspace, cases = prepare_cases(
        root, fixture, args.base_journal.resolve(), journal_validator
    )
    command = render_daemon_command(
        session_validation.require_string(
            args.daemon_command_json, "--daemon-command-json"
        ),
        state_root,
        workspace,
        cases[0].session_id,
    )
    requests = build_requests(cases)
    first_frames = run_open(
        command, state_root / "sessions", requests, schema_root, args.timeout
    )
    first_snapshots: dict[str, tuple[bytes, dict[str, bytes]]] = {}
    for case in cases:
        response = response_for(first_frames, "session-tail-get-" + case.name)
        if case.expected == "repair":
            first_snapshots[case.name] = inspect_repaired_case(
                case, response, journal_validator
            )
        else:
            inspect_corrupt_case(case, response)

    second_frames = run_open(
        command, state_root / "sessions", requests, schema_root, args.timeout
    )
    for case in cases:
        response = response_for(second_frames, "session-tail-get-" + case.name)
        if case.expected == "repair":
            second = inspect_repaired_case(case, response, journal_validator)
            if second != first_snapshots[case.name]:
                raise SessionTailConformanceError(
                    f"{case.name} clean reopen was not byte-for-byte idempotent"
                )
        else:
            inspect_corrupt_case(case, response)


def main(argv: Sequence[str] | None = None) -> int:
    """功能：执行静态六案例门禁，并可选运行原生 daemon 双开黑盒探针。

    输入：命令行参数；无 daemon 参数时绝不启动子进程。
    输出：成功返回 0 并打印稳定案例计数，失败返回 1 并给出有界诊断。
    不变量：不改变 capability matrix，不接触真实 Provider 或用户默认状态。
    失败：Schema、fixture、daemon、协议或持久化不一致均返回非零。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = parse_args(argv)
    try:
        if args.timeout <= 0:
            raise SessionTailConformanceError("--timeout must be positive")
        schema_root = args.schema_root.resolve()
        case_validator = build_schema_validator(
            schema_root, "session/journal-tail-recovery-cases.schema.json"
        )
        journal_validator = build_schema_validator(
            schema_root, "session/journal.schema.json"
        )
        fixture = load_contract(args.cases.resolve(), case_validator)
        validate_generated_cases(
            fixture, args.base_journal.resolve(), journal_validator
        )
        print("SESSION TAIL FIXTURE PASS: repair=2, journal_corrupt=4")
        if args.daemon_command_json is None:
            return 0
        if args.work_root is not None:
            args.work_root.mkdir(parents=True, exist_ok=True)
            run_probe(args, args.work_root.resolve())
        else:
            with tempfile.TemporaryDirectory(prefix="agent-session-tail-") as temporary:
                run_probe(args, Path(temporary))
        print("SESSION TAIL BLACK-BOX PASS: cases=6, opens=12, reopen=idempotent")
        return 0
    except (
        OSError,
        ValueError,
        runner.ConformanceError,
        runner.ProtocolViolation,
        SchemaValidationError,
        session_validation.SessionValidationError,
        SessionTailConformanceError,
    ) as exc:
        print(f"SESSION TAIL FAIL: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
