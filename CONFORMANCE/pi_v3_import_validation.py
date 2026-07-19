#!/usr/bin/env python3
"""验证 PI Session v3 clean-room 导入夹具、报告与 portable journal。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from datetime import datetime
import hashlib
from pathlib import Path, PurePosixPath
import re
import stat
import sys
from typing import Any, Sequence

import branch_compaction_validation
import runner
import session_validation


JSONObject = dict[str, Any]
REFERENCE_COMMIT = "3f9aa5d10b35223abf6146f960ff5cb5c68053ee"
IMPORT_NAMESPACE = "org.agentprotocol.pi-v3"
REPORT_MEDIA_TYPE = "application/vnd.qxnm-forge.pi-v3-import-report+json"
MAX_SOURCE_BYTES = 16_777_216
MAX_LINE_BYTES = 1_048_576
MAX_SOURCE_ENTRIES = 100_000
MAX_JSON_DEPTH = 64
KNOWN_ENTRY_TYPES = {
    "message",
    "thinking_level_change",
    "model_change",
    "compaction",
    "branch_summary",
    "custom",
    "custom_message",
    "label",
    "session_info",
}
KNOWN_ENTRY_FIELDS = {
    "message": {"type", "id", "parentId", "timestamp", "message"},
    "thinking_level_change": {
        "type",
        "id",
        "parentId",
        "timestamp",
        "thinkingLevel",
    },
    "model_change": {
        "type",
        "id",
        "parentId",
        "timestamp",
        "provider",
        "modelId",
    },
    "compaction": {
        "type",
        "id",
        "parentId",
        "timestamp",
        "summary",
        "firstKeptEntryId",
        "tokensBefore",
        "details",
        "fromHook",
    },
    "branch_summary": {
        "type",
        "id",
        "parentId",
        "timestamp",
        "fromId",
        "summary",
        "details",
        "fromHook",
    },
    "custom": {
        "type",
        "id",
        "parentId",
        "timestamp",
        "customType",
        "data",
    },
    "custom_message": {
        "type",
        "id",
        "parentId",
        "timestamp",
        "customType",
        "content",
        "details",
        "display",
    },
    "label": {
        "type",
        "id",
        "parentId",
        "timestamp",
        "targetId",
        "label",
    },
    "session_info": {"type", "id", "parentId", "timestamp", "name"},
}
FORBIDDEN_IMPORTED_KINDS = {
    "event.emitted",
    "run.accepted",
    "run.started",
    "run.cancellation_requested",
    "run.terminal",
    "turn.started",
    "turn.completed",
    "provider.attempt",
    "tool.intent",
    "tool.result",
    "approval.requested",
    "approval.resolved",
    "queue.appended",
    "queue.consumed",
    "faux.configured",
}
_SENSITIVE_KEY = re.compile(
    r"^(?:authorization|cookie|credential|password|passwd|secret|api[_-]?key|"
    r"access[_-]?token|refresh[_-]?token)$",
    re.IGNORECASE,
)
_SENSITIVE_TEXT = re.compile(
    r"(?i)(?:bearer\s+[A-Za-z0-9._~+/=-]{8,}|sk-[A-Za-z0-9_-]{8,}|"
    r"(?:api[_-]?key|password|secret|access[_-]?token)\s*[:=]\s*[^\s,;]+)"
)
_WINDOWS_PATH = re.compile(r"^[A-Za-z]:[\\/]")


class PiV3ImportValidationError(Exception):
    """表示 PI v3 source、报告、映射或目标 journal 不符合公共契约。"""


@dataclass(frozen=True)
class PiSourceEntry:
    """保存一条严格 PI v3 source entry 及其逐行证据。"""

    line_number: int
    raw_line: bytes
    value: JSONObject


@dataclass(frozen=True)
class PiSourceState:
    """保存完整 PI v3 source 的结构化只读验证结果。"""

    raw: bytes
    sha256: str
    header: JSONObject
    entries: tuple[PiSourceEntry, ...]


@dataclass(frozen=True)
class PiV3ImportState:
    """保存通过验证的来源、报告、journal 与 selected context 摘要。"""

    source: PiSourceState
    report: JSONObject
    journal_values: tuple[JSONObject, ...]
    selected_head_record_id: str
    selected_message_ids: tuple[str, ...]


def require_object(value: Any, context: str) -> JSONObject:
    """功能：要求动态值为字符串键 JSON 对象并返回。

    输入：任意严格 JSON 值和安全诊断上下文。
    输出：类型收窄后的对象。
    不变量：不回显对象字段值。
    失败：非对象或含非字符串键时抛 PiV3ImportValidationError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, dict) or not all(isinstance(key, str) for key in value):
        raise PiV3ImportValidationError(f"{context} must be a JSON object")
    return value


def require_array(value: Any, context: str) -> list[Any]:
    """功能：要求动态值为 JSON 数组并返回。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, list):
        raise PiV3ImportValidationError(f"{context} must be an array")
    return value


def require_string(value: Any, context: str) -> str:
    """功能：要求动态值为非空字符串并返回。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, str) or not value:
        raise PiV3ImportValidationError(f"{context} must be a non-empty string")
    return value


def require_integer(value: Any, context: str, *, minimum: int = 0) -> int:
    """功能：要求动态值为给定下界以上的非 bool safe integer。

    输入：任意 JSON 值、安全诊断上下文和闭区间下界。
    输出：安全范围内的 Python int。
    失败：bool、非整数、越界时抛 PiV3ImportValidationError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if (
        not isinstance(value, int)
        or isinstance(value, bool)
        or value < minimum
        or value > 9_007_199_254_740_991
    ):
        raise PiV3ImportValidationError(f"{context} must be a safe integer")
    return value


def _validate_utc(value: Any, context: str) -> str:
    """功能：验证 PI entry timestamp 为带 Z 的 UTC RFC 3339 字符串。

    输入：动态 timestamp 与安全上下文。
    输出：原始已验证字符串。
    失败：非字符串、非 Z 结尾或日期无效时抛 PiV3ImportValidationError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    text = require_string(value, context)
    if not text.endswith("Z"):
        raise PiV3ImportValidationError(f"{context} must be UTC")
    try:
        datetime.fromisoformat(text[:-1] + "+00:00")
    except ValueError as exc:
        raise PiV3ImportValidationError(f"{context} is invalid") from exc
    return text


def _strict_json(data: str, context: str) -> JSONObject:
    """功能：严格解析顶层 JSON 对象并统一脱敏错误。

    输入：已严格 UTF-8 解码的文本及不含实例数据的上下文。
    输出：拒绝重复键、非有限数与尾随内容的 JSON 对象。
    失败：语法或顶层类型违规时抛 PiV3ImportValidationError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        return require_object(runner.strict_json_loads(data), context)
    except PiV3ImportValidationError:
        raise
    except Exception as exc:
        raise PiV3ImportValidationError(f"{context} is not strict JSON") from exc


def _validate_json_depth(data: bytes, context: str) -> None:
    """功能：在 JSON 解码前以字符串感知扫描限制对象/数组嵌套深度。

    输入：单个 JSON 文档的原始 UTF-8 字节和安全上下文。
    输出：最大嵌套不超过 64 且括号未提前闭合时无返回值。
    不变量：字符串内部括号与转义引号不计入深度；完整语法仍由严格解析器验证。
    失败：超深或明显负深度时抛 PiV3ImportValidationError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    depth = 0
    in_string = False
    escaped = False
    for byte in data:
        if in_string:
            if escaped:
                escaped = False
            elif byte == 0x5C:
                escaped = True
            elif byte == 0x22:
                in_string = False
            continue
        if byte == 0x22:
            in_string = True
        elif byte in (0x7B, 0x5B):
            depth += 1
            if depth > MAX_JSON_DEPTH:
                raise PiV3ImportValidationError(f"{context} exceeds JSON nesting limit")
        elif byte in (0x7D, 0x5D):
            depth -= 1
            if depth < 0:
                raise PiV3ImportValidationError(f"{context} has invalid nesting")


def _read_regular_file(path: Path, *, maximum: int, context: str) -> bytes:
    """功能：不跟随 fixture 叶符号链接地读取有界普通文件。

    输入：由 case 根约束的文件、最大字节数与安全上下文。
    输出：非空原始字节。
    不变量：拒绝符号链接、目录、设备及大小读取竞态；错误不含文件内容。
    失败：类型、大小、读取或 stat 前后变化时抛 PiV3ImportValidationError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        before = path.lstat()
        if stat.S_ISLNK(before.st_mode) or not stat.S_ISREG(before.st_mode):
            raise PiV3ImportValidationError(f"{context} must be a regular file")
        if before.st_size <= 0 or before.st_size > maximum:
            raise PiV3ImportValidationError(f"{context} size is out of bounds")
        raw = path.read_bytes()
        after = path.lstat()
    except PiV3ImportValidationError:
        raise
    except OSError as exc:
        raise PiV3ImportValidationError(f"cannot read {context}") from exc
    if (
        before.st_dev != after.st_dev
        or before.st_ino != after.st_ino
        or before.st_size != after.st_size
        or len(raw) != before.st_size
    ):
        raise PiV3ImportValidationError(f"{context} changed while being read")
    return raw


def resolve_case_file(case_dir: Path, relative_value: Any, context: str) -> Path:
    """功能：把 fixture 相对文件名约束到真实 case 根内。

    输入：case 目录、expectations 中的相对 POSIX 路径和值上下文。
    输出：不存在父级跳转、绝对路径或符号链接祖先的目标 Path。
    不变量：解析结果不能逃出 case 根；不接受调用方任意主机路径。
    失败：路径语法、根逃逸、缺失或任一现有组件为符号链接时拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    relative = PurePosixPath(require_string(relative_value, context))
    if relative.is_absolute() or ".." in relative.parts or "." in relative.parts:
        raise PiV3ImportValidationError(f"{context} must be a safe relative path")
    try:
        root = case_dir.resolve(strict=True)
        candidate = root.joinpath(*relative.parts)
        resolved = candidate.resolve(strict=True)
        resolved.relative_to(root)
    except (OSError, ValueError) as exc:
        raise PiV3ImportValidationError(f"{context} escapes or is missing") from exc
    current = candidate
    while current != root:
        if current.is_symlink():
            raise PiV3ImportValidationError(f"{context} must not traverse a symlink")
        current = current.parent
    return resolved


def _validate_known_entry(
    entry: JSONObject, prior: dict[str, JSONObject], context: str
) -> None:
    """功能：验证 PI v3 已知 entry 的必要字段与关键交叉引用。

    输入：已验证 base/tree 的 entry、此前 entry 索引和安全上下文。
    输出：类型专属字段满足 clean-room 最小契约时无返回值。
    不变量：不会执行 message/tool/custom 内容，也不会宽容跳过已知畸形类型。
    失败：已知类型缺字段、字段类型错误或引用无效时拒绝整个 source。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    entry_type = require_string(entry.get("type"), f"{context}.type")
    if set(entry) - KNOWN_ENTRY_FIELDS[entry_type]:
        raise PiV3ImportValidationError(
            f"{context} contains unknown known-entry fields"
        )
    if entry_type == "message":
        message = require_object(entry.get("message"), f"{context}.message")
        role = require_string(message.get("role"), f"{context}.message.role")
        if role not in {"user", "assistant", "toolResult", "bashExecution", "custom"}:
            raise PiV3ImportValidationError(f"{context}.message.role is unsupported")
        return
    if entry_type == "model_change":
        require_string(entry.get("provider"), f"{context}.provider")
        require_string(entry.get("modelId"), f"{context}.modelId")
        return
    if entry_type == "thinking_level_change":
        if entry.get("thinkingLevel") not in {
            "off",
            "minimal",
            "low",
            "medium",
            "high",
            "xhigh",
        }:
            raise PiV3ImportValidationError(f"{context}.thinkingLevel is invalid")
        return
    if entry_type == "compaction":
        require_string(entry.get("summary"), f"{context}.summary")
        kept = require_string(
            entry.get("firstKeptEntryId"), f"{context}.firstKeptEntryId"
        )
        if kept not in prior:
            raise PiV3ImportValidationError(
                f"{context}.firstKeptEntryId is not earlier"
            )
        ancestor_id = entry.get("parentId")
        ancestors: set[str] = set()
        while isinstance(ancestor_id, str):
            if ancestor_id in ancestors or ancestor_id not in prior:
                raise PiV3ImportValidationError(f"{context}.parent ancestry is invalid")
            ancestors.add(ancestor_id)
            ancestor_id = prior[ancestor_id].get("parentId")
        if kept not in ancestors:
            raise PiV3ImportValidationError(
                f"{context}.firstKeptEntryId is not on source ancestry"
            )
        require_integer(entry.get("tokensBefore"), f"{context}.tokensBefore")
        return
    if entry_type == "branch_summary":
        from_id = require_string(entry.get("fromId"), f"{context}.fromId")
        if from_id != "root" and from_id not in prior:
            raise PiV3ImportValidationError(f"{context}.fromId is not earlier")
        require_string(entry.get("summary"), f"{context}.summary")
        return
    if entry_type in {"custom", "custom_message"}:
        require_string(entry.get("customType"), f"{context}.customType")
        if entry_type == "custom_message" and not isinstance(
            entry.get("display"), bool
        ):
            raise PiV3ImportValidationError(f"{context}.display must be boolean")
        return
    if entry_type == "label":
        target_id = require_string(entry.get("targetId"), f"{context}.targetId")
        if target_id not in prior:
            raise PiV3ImportValidationError(f"{context}.targetId is not earlier")
        label = entry.get("label")
        if label is not None and not isinstance(label, str):
            raise PiV3ImportValidationError(f"{context}.label must be string or null")
        return
    if entry_type == "session_info":
        name = entry.get("name")
        if name is not None and not isinstance(name, str):
            raise PiV3ImportValidationError(f"{context}.name must be string or null")


def load_pi_v3_source(path: Path) -> PiSourceState:
    """功能：严格读取并验证一个 PI Session v3 source。

    输入：调用方已约束到 fixture 根的只读普通文件。
    输出：完整字节 SHA-256、header 和含原始逐行证据的 entries。
    不变量：只接受 UTF-8/LF、单一 v3 header、唯一 ID、单根 earlier-only tree；
    未知 type 仅在 base/tree 合法时保留，不跳过畸形行。
    失败：BOM、CR、空行、无终止 LF、超限、重复键、非有限数或结构错误时拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    raw = _read_regular_file(path, maximum=MAX_SOURCE_BYTES, context="PI v3 source")
    if raw.startswith(b"\xef\xbb\xbf") or not raw.endswith(b"\n") or b"\r" in raw:
        raise PiV3ImportValidationError("PI v3 source must be BOM-free LF JSONL")
    lines = raw[:-1].split(b"\n")
    if (
        not lines
        or len(lines) > MAX_SOURCE_ENTRIES + 1
        or any(not line or len(line) > MAX_LINE_BYTES for line in lines)
    ):
        raise PiV3ImportValidationError("PI v3 source line count or size is invalid")
    values: list[JSONObject] = []
    for line_number, line in enumerate(lines, start=1):
        _validate_json_depth(line, f"PI v3 source line {line_number}")
        try:
            text = line.decode("utf-8", errors="strict")
        except UnicodeDecodeError as exc:
            raise PiV3ImportValidationError(
                f"PI v3 source line {line_number} is not strict UTF-8"
            ) from exc
        values.append(_strict_json(text, f"PI v3 source line {line_number}"))
    header = values[0]
    allowed_header = {"type", "version", "id", "timestamp", "cwd", "parentSession"}
    if set(header) - allowed_header:
        raise PiV3ImportValidationError("PI v3 header contains unknown fields")
    if header.get("type") != "session" or header.get("version") != 3:
        raise PiV3ImportValidationError("PI v3 source header/version is invalid")
    require_string(header.get("id"), "PI v3 header.id")
    _validate_utc(header.get("timestamp"), "PI v3 header.timestamp")
    if not isinstance(header.get("cwd"), str):
        raise PiV3ImportValidationError("PI v3 header.cwd must be a string")
    if "parentSession" in header and not isinstance(header.get("parentSession"), str):
        raise PiV3ImportValidationError("PI v3 header.parentSession must be a string")

    prior: dict[str, JSONObject] = {}
    source_entries: list[PiSourceEntry] = []
    for index, value in enumerate(values[1:], start=2):
        if value.get("type") == "session":
            raise PiV3ImportValidationError("PI v3 source contains a second header")
        entry_id = require_string(value.get("id"), f"PI v3 source line {index}.id")
        if entry_id in prior:
            raise PiV3ImportValidationError("PI v3 source contains duplicate entry IDs")
        parent_id = value.get("parentId")
        if index == 2:
            if parent_id is not None:
                raise PiV3ImportValidationError(
                    "PI v3 first entry must be the sole root"
                )
        elif not isinstance(parent_id, str) or parent_id not in prior:
            raise PiV3ImportValidationError(
                "PI v3 parent must reference an earlier entry"
            )
        _validate_utc(value.get("timestamp"), f"PI v3 source line {index}.timestamp")
        entry_type = require_string(
            value.get("type"), f"PI v3 source line {index}.type"
        )
        if entry_type in KNOWN_ENTRY_TYPES:
            _validate_known_entry(value, prior, f"PI v3 source line {index}")
        prior[entry_id] = value
        source_entries.append(PiSourceEntry(index, lines[index - 1], value))
    return PiSourceState(
        raw=raw,
        sha256=hashlib.sha256(raw).hexdigest(),
        header=header,
        entries=tuple(source_entries),
    )


def _load_schema_validator(schema_root: Path, relative_name: str) -> Any:
    """功能：加载全部本地 Draft 2020-12 Schema 并选择指定根验证器。

    输入：bundled Schema 根和固定相对 Schema 名。
    输出：支持全部本地跨文件引用的 jsonschema validator。
    不变量：不解析远程引用或网络资源；每个 Schema 先执行元 Schema 检查。
    失败：依赖、读取、严格 JSON、ID 或 Schema 无效时抛脱敏验证错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        import jsonschema
        from referencing import Registry, Resource
    except ImportError as exc:
        raise PiV3ImportValidationError(
            "PI import Schema validation requires jsonschema and referencing"
        ) from exc
    resources: list[tuple[str, Any]] = []
    schemas: dict[str, JSONObject] = {}
    for path in sorted(schema_root.rglob("*.schema.json")):
        try:
            schema = _strict_json(path.read_text(encoding="utf-8"), "bundled Schema")
            jsonschema.Draft202012Validator.check_schema(schema)
            schema_id = require_string(schema.get("$id"), "Schema $id")
            resource = Resource.from_contents(schema)
        except (OSError, jsonschema.SchemaError, ValueError) as exc:
            raise PiV3ImportValidationError("bundled Schema is invalid") from exc
        schemas[str(path.relative_to(schema_root))] = schema
        resources.append((schema_id, resource))
    if relative_name not in schemas:
        raise PiV3ImportValidationError("required PI import Schema is missing")
    return jsonschema.Draft202012Validator(
        schemas[relative_name],
        registry=Registry().with_resources(resources),
    )


def _validate_schema(value: JSONObject, validator: Any, context: str) -> None:
    """功能：验证一个 JSON 对象并返回稳定、不回显实例值的首个错误。

    输入：实例、已构建 validator 和安全上下文。
    输出：无 Schema 错误时无返回值。
    失败：按实例路径排序后抛 PiV3ImportValidationError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    errors = sorted(
        validator.iter_errors(value),
        key=lambda error: tuple(str(part) for part in error.absolute_path),
    )
    if errors:
        location = "/".join(str(part) for part in errors[0].absolute_path)
        raise PiV3ImportValidationError(
            f"{context} Schema violation at {location or '<root>'}"
        )


def load_json_object(
    path: Path, *, maximum: int, context: str
) -> tuple[bytes, JSONObject]:
    """功能：读取有界普通文件并严格解析为一个 JSON 对象。

    输入：安全 fixture 文件、上限和诊断上下文。
    输出：用于哈希的原始字节与严格对象。
    失败：BOM、UTF-8、JSON、空文件、符号链接或大小边界违规时拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    raw = _read_regular_file(path, maximum=maximum, context=context)
    if raw.startswith(b"\xef\xbb\xbf"):
        raise PiV3ImportValidationError(f"{context} must not contain a BOM")
    try:
        text = raw.decode("utf-8", errors="strict")
    except UnicodeDecodeError as exc:
        raise PiV3ImportValidationError(f"{context} is not strict UTF-8") from exc
    _validate_json_depth(raw, context)
    return raw, _strict_json(text, context)


def _walk_json(value: Any) -> Sequence[Any]:
    """功能：返回当前严格 JSON 值的直接子值供有界迭代。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if isinstance(value, dict):
        return list(value.values())
    if isinstance(value, list):
        return value
    return ()


def assert_no_sensitive_values(value: Any, context: str) -> None:
    """功能：递归拒绝 fixture、target 或报告中的 secret 形状。

    输入：严格 JSON 值和安全上下文。
    输出：未发现敏感键、Bearer/sk token 或 key=value 形状时无返回值。
    不变量：诊断不回显命中的键名或值；最多遍历 200000 个节点。
    失败：发现敏感形状或递归节点超限时抛 PiV3ImportValidationError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    stack = [value]
    visited = 0
    while stack:
        current = stack.pop()
        visited += 1
        if visited > 200_000:
            raise PiV3ImportValidationError(f"{context} JSON node limit exceeded")
        if isinstance(current, dict):
            if any(_SENSITIVE_KEY.fullmatch(key) for key in current):
                raise PiV3ImportValidationError(f"{context} contains a sensitive key")
        elif isinstance(current, str) and _SENSITIVE_TEXT.search(current):
            raise PiV3ImportValidationError(f"{context} contains a sensitive value")
        stack.extend(_walk_json(current))


def assert_no_host_paths(value: Any, context: str) -> None:
    """功能：递归拒绝 fixture target/report 中持久化的明显主机路径。

    输入：严格 JSON 值和安全上下文。
    输出：没有 POSIX absolute、home、UNC 或 drive path 字符串时无返回值。
    不变量：媒体类型等内部斜杠不误判；诊断不回显路径。
    失败：路径形状或遍历节点超限时抛 PiV3ImportValidationError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    stack = [value]
    visited = 0
    while stack:
        current = stack.pop()
        visited += 1
        if visited > 200_000:
            raise PiV3ImportValidationError(f"{context} JSON node limit exceeded")
        if isinstance(current, str) and (
            current.startswith("/")
            or current.startswith("~/")
            or current.startswith("\\\\")
            or _WINDOWS_PATH.match(current)
        ):
            raise PiV3ImportValidationError(f"{context} contains a host path")
        stack.extend(_walk_json(current))


def assert_synthetic_source(source: PiSourceState) -> None:
    """功能：确认公共 source fixture 仅含脱敏路径和 synthetic 对话文本。

    输入：已结构验证的 PI v3 source。
    输出：fixture 不含真实路径、secret、用户或 Provider 内容时无返回值。
    不变量：生产 importer 不使用本 synthetic 限制；它只属于公共夹具隐私门禁。
    失败：cwd/parent path、非 fixture Provider 或非 synthetic 内容时拒绝夹具。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if source.header.get("cwd") != "[REDACTED]" or "parentSession" in source.header:
        raise PiV3ImportValidationError("PI source fixture path is not redacted")
    assert_no_sensitive_values(
        [source.header, *[entry.value for entry in source.entries]],
        "PI source fixture",
    )
    assert_no_host_paths(
        [entry.value for entry in source.entries],
        "PI source fixture",
    )
    for entry in source.entries:
        value = entry.value
        if value.get("type") == "message":
            message = require_object(value.get("message"), "fixture message")
            if message.get("role") == "assistant" and (
                message.get("provider") != "fixture-provider"
                or message.get("model") != "fixture-model"
            ):
                raise PiV3ImportValidationError(
                    "PI source fixture contains a non-fixture Provider"
                )
            content = message.get("content")
            blocks = (
                [{"type": "text", "text": content}]
                if isinstance(content, str)
                else content
            )
            for block_value in require_array(blocks, "fixture message content"):
                block = require_object(block_value, "fixture content block")
                text = block.get("text", block.get("thinking"))
                if isinstance(text, str) and not text.startswith("[SYNTHETIC_"):
                    raise PiV3ImportValidationError(
                        "PI source fixture contains non-synthetic message content"
                    )
        if value.get("type") in {"compaction", "branch_summary"}:
            summary = require_string(value.get("summary"), "fixture summary")
            if not summary.startswith("[SYNTHETIC_"):
                raise PiV3ImportValidationError(
                    "PI source fixture contains non-synthetic summary"
                )
        if value.get("type") == "custom_message":
            content = value.get("content")
            if not isinstance(content, str) or not content.startswith("[SYNTHETIC_"):
                raise PiV3ImportValidationError(
                    "PI source fixture contains non-synthetic custom context"
                )


def _source_mapping_index(
    source: PiSourceState, expectations: JSONObject
) -> tuple[dict[str, JSONObject], set[str]]:
    """功能：验证 expectations 对每个 source entry 恰好给出一项映射。

    输入：source 状态和已通过 Schema 的 expectations。
    输出：按 source ID 索引的 mapping 与全部 target record ID 集合。
    不变量：source line/type/ID 精确匹配，target ID 不跨 mapping 复用。
    失败：遗漏、重复、漂移或 target ID 冲突时拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    source_by_id = {
        require_string(entry.value.get("id"), "source entry.id"): entry
        for entry in source.entries
    }
    mappings: dict[str, JSONObject] = {}
    target_ids: set[str] = set()
    for raw_mapping in require_array(
        expectations.get("sourceMappings"), "expectations.sourceMappings"
    ):
        mapping = require_object(raw_mapping, "source mapping")
        source_id = require_string(
            mapping.get("sourceEntryId"), "mapping.sourceEntryId"
        )
        if source_id in mappings or source_id not in source_by_id:
            raise PiV3ImportValidationError("source mapping ID is duplicate or unknown")
        source_entry = source_by_id[source_id]
        if mapping.get("sourceLine") != source_entry.line_number or mapping.get(
            "sourceType"
        ) != source_entry.value.get("type"):
            raise PiV3ImportValidationError("source mapping line/type differs")
        for raw_target in require_array(
            mapping.get("targetRecordIds"), "targetRecordIds"
        ):
            target_id = require_string(raw_target, "target record ID")
            if target_id in target_ids:
                raise PiV3ImportValidationError(
                    "target record appears in multiple mappings"
                )
            target_ids.add(target_id)
        mappings[source_id] = mapping
    if set(mappings) != set(source_by_id):
        raise PiV3ImportValidationError("source mappings do not cover every entry")
    return mappings, target_ids


def _origin_for_record(record: JSONObject) -> JSONObject | None:
    """功能：提取 target record 的 PI v3 top-level provenance extension。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    extensions = record.get("extensions")
    if not isinstance(extensions, dict):
        return None
    value = extensions.get(IMPORT_NAMESPACE)
    return value if isinstance(value, dict) else None


def _validate_mapping_kinds(
    source: PiSourceState,
    mappings: dict[str, JSONObject],
    records: dict[str, JSONObject],
) -> None:
    """功能：核对每类 PI entry 的 target kind、来源扩展与隔离语义。

    输入：source、机器 mapping 和 target record 索引。
    输出：全部映射符合 ADR 0012 时无返回值。
    不变量：custom、custom-message、label、unknown 只能成为 context-free extension；
    compaction 必须按 summary message 加 context.compacted 的顺序展开。
    失败：记录缺失、kind 漂移、来源扩展不符或扩展被提升为 message 时拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    expected_kinds = {
        "message": ["message.appended"],
        "model_change": ["model.selected"],
        "thinking_level_change": ["model.selected"],
        "compaction": ["message.appended", "context.compacted"],
        "branch_summary": ["message.appended"],
        "custom": ["extension"],
        "custom_message": ["extension"],
        "label": ["extension"],
        "session_info": ["session.metadata"],
    }
    for source_entry in source.entries:
        source_id = require_string(source_entry.value.get("id"), "source entry ID")
        source_type = require_string(
            source_entry.value.get("type"), "source entry type"
        )
        mapping = mappings[source_id]
        target_ids = require_array(mapping.get("targetRecordIds"), "mapping targets")
        actual: list[str] = []
        for raw_target in target_ids:
            target_id = require_string(raw_target, "mapping target ID")
            if target_id not in records:
                raise PiV3ImportValidationError(
                    "mapping references an unknown target record"
                )
            record = records[target_id]
            actual.append(require_string(record.get("kind"), "target kind"))
            origin = _origin_for_record(record)
            if (
                origin is None
                or origin.get("sourceEntryId") != source_id
                or origin.get("sourceLine") != source_entry.line_number
            ):
                raise PiV3ImportValidationError(
                    "target record source provenance differs"
                )
        wanted = expected_kinds.get(source_type, ["extension"])
        if actual != wanted:
            raise PiV3ImportValidationError(
                "source entry mapped to an invalid target kind"
            )
        if wanted == ["extension"]:
            record = records[require_string(target_ids[0], "extension target")]
            data = require_object(record.get("data"), "extension data")
            if data.get("namespace") != IMPORT_NAMESPACE:
                raise PiV3ImportValidationError("PI extension namespace differs")
            extension_value = require_object(data.get("value"), "extension value")
            if extension_value.get("sourceEntryId") != source_id:
                raise PiV3ImportValidationError("PI extension identity differs")


def _validate_report_coverage(
    source: PiSourceState,
    mappings: dict[str, JSONObject],
    report: JSONObject,
    expectations: JSONObject,
) -> None:
    """功能：验证 mandatory report 精确覆盖所有非无损 core 映射。

    输入：source、映射、报告和 expectations。
    输出：逐项 line digest、target IDs、disposition 与计数一致时无返回值。
    不变量：任何 extension、quarantine 或 loss 都必须报告；无损 mapped entry 不伪报。
    失败：遗漏、多报、重复、hash、line、type、target 或 count 漂移时拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    expected_reported = [
        require_string(value, "reported source ID")
        for value in require_array(
            expectations.get("reportedSourceEntryIds"), "reportedSourceEntryIds"
        )
    ]
    derived_reported = [
        source_id
        for source_id, mapping in mappings.items()
        if mapping.get("disposition") != "mapped"
    ]
    if set(expected_reported) != set(derived_reported):
        raise PiV3ImportValidationError(
            "reported source ID expectations are incomplete"
        )
    source_by_id = {
        require_string(entry.value.get("id"), "source ID"): entry
        for entry in source.entries
    }
    report_entries: dict[str, JSONObject] = {}
    for value in require_array(report.get("entries"), "report.entries"):
        item = require_object(value, "report entry")
        source_id = require_string(item.get("sourceEntryId"), "report sourceEntryId")
        if source_id in report_entries or source_id not in source_by_id:
            raise PiV3ImportValidationError("report source ID is duplicate or unknown")
        source_entry = source_by_id[source_id]
        mapping = mappings[source_id]
        if (
            item.get("sourceLine") != source_entry.line_number
            or item.get("sourceType") != source_entry.value.get("type")
            or item.get("disposition") != mapping.get("disposition")
            or item.get("targetRecordIds") != mapping.get("targetRecordIds")
            or item.get("sourceLineSha256")
            != hashlib.sha256(source_entry.raw_line).hexdigest()
        ):
            raise PiV3ImportValidationError("report entry evidence differs")
        report_entries[source_id] = item
    if list(report_entries) != expected_reported:
        raise PiV3ImportValidationError(
            "report entries do not match required order or coverage"
        )
    if report.get("counts") != expectations.get("counts"):
        raise PiV3ImportValidationError("report counts differ from expectations")
    if "source_path_not_persisted" not in require_array(
        report.get("warnings"), "report.warnings"
    ):
        raise PiV3ImportValidationError("report omits source path disposition warning")


def _mapped_content(blocks: Any) -> list[JSONObject]:
    """功能：把 fixture PI text/thinking blocks 投影为 portable 内容供精确比较。

    输入：PI message string 或内容数组。
    输出：仅含 text/reasoning 的新 portable block 数组。
    失败：fixture 中出现其他 block 类型时拒绝，避免测试静默扩大契约。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    values = (
        [{"type": "text", "text": blocks}]
        if isinstance(blocks, str)
        else require_array(blocks, "PI message content")
    )
    result: list[JSONObject] = []
    for value in values:
        block = require_object(value, "PI content block")
        if block.get("type") == "text":
            result.append(
                {"type": "text", "text": require_string(block.get("text"), "text")}
            )
        elif block.get("type") == "thinking":
            result.append(
                {
                    "type": "reasoning",
                    "text": require_string(block.get("thinking"), "thinking"),
                }
            )
        else:
            raise PiV3ImportValidationError("fixture content block is unsupported")
    return result


def _mapped_usage(value: Any) -> JSONObject:
    """功能：按 ADR 0012 把 PI usage 映射为 portable usage 比较对象。

    输入：PI assistant usage 对象。
    输出：包含 cache/reasoning 分项与 aggregate USD cost 的 portable 对象。
    不变量：PI total 必须等于 input+cacheRead+cacheWrite+output。
    失败：字段类型、范围、total 或 cost total 非数值时拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    usage = require_object(value, "PI usage")
    input_tokens = require_integer(usage.get("input"), "PI usage.input")
    output_tokens = require_integer(usage.get("output"), "PI usage.output")
    cache_read = require_integer(usage.get("cacheRead"), "PI usage.cacheRead")
    cache_write = require_integer(usage.get("cacheWrite"), "PI usage.cacheWrite")
    total = require_integer(usage.get("totalTokens"), "PI usage.totalTokens")
    normalized_input = input_tokens + cache_read + cache_write
    if total != normalized_input + output_tokens:
        raise PiV3ImportValidationError("PI usage total is inconsistent")
    cost = require_object(usage.get("cost"), "PI usage.cost")
    cost_total = cost.get("total")
    if (
        not isinstance(cost_total, (int, float))
        or isinstance(cost_total, bool)
        or cost_total < 0
    ):
        raise PiV3ImportValidationError("PI usage cost is invalid")
    result: JSONObject = {
        "inputTokens": normalized_input,
        "outputTokens": output_tokens,
        "totalTokens": total,
        "cachedInputTokens": cache_read,
        "cacheWriteTokens": cache_write,
        "cost": {"currency": "USD", "amount": format(cost_total, ".12g")},
    }
    if "reasoning" in usage:
        result["reasoningTokens"] = require_integer(
            usage.get("reasoning"), "PI usage.reasoning"
        )
    return result


def _validate_message_semantics(
    source: PiSourceState,
    mappings: dict[str, JSONObject],
    records: dict[str, JSONObject],
) -> None:
    """功能：精确核对 fixture 消息、usage、分支 summary 和 metadata 映射。

    输入：source、source mapping 与 target record 索引。
    输出：所有可无损字段符合 ADR 映射时无返回值。
    不变量：比较 canonical 完成消息而非事件 delta；custom message 不参与此路径。
    失败：内容、角色、Provider、usage、时间、summary wrapper 或 metadata 漂移时拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    branch_prefix = (
        "The following is a summary of a branch that this conversation came back from:"
        "\n\n<summary>\n"
    )
    for source_entry in source.entries:
        source_value = source_entry.value
        source_id = require_string(source_value.get("id"), "source ID")
        source_type = source_value.get("type")
        target_ids = require_array(
            mappings[source_id].get("targetRecordIds"), "target IDs"
        )
        if source_type == "message":
            source_message = require_object(
                source_value.get("message"), "source message"
            )
            if source_message.get("role") not in {"user", "assistant"}:
                continue
            record = records[require_string(target_ids[0], "message target")]
            target_message = require_object(
                require_object(record.get("data"), "message data").get("message"),
                "target message",
            )
            if (
                target_message.get("role") != source_message.get("role")
                or target_message.get("content")
                != _mapped_content(source_message.get("content"))
                or target_message.get("time") != source_value.get("timestamp")
            ):
                raise PiV3ImportValidationError("portable message mapping differs")
            if source_message.get("role") == "assistant":
                wanted_finish = {
                    "toolUse": "tool_use",
                    "aborted": "cancelled",
                }.get(
                    source_message.get("stopReason"),
                    source_message.get("stopReason"),
                )
                if (
                    target_message.get("provider")
                    != {
                        "id": source_message.get("provider"),
                        "modelId": source_message.get("model"),
                    }
                    or target_message.get("finishReason") != wanted_finish
                    or target_message.get("usage")
                    != _mapped_usage(source_message.get("usage"))
                ):
                    raise PiV3ImportValidationError(
                        "assistant metadata mapping differs"
                    )
        elif source_type == "branch_summary":
            record = records[require_string(target_ids[0], "branch summary target")]
            message = require_object(
                require_object(record.get("data"), "branch data").get("message"),
                "branch summary message",
            )
            wanted = (
                branch_prefix
                + require_string(source_value.get("summary"), "branch summary")
                + "\n</summary>"
            )
            if message.get("role") != "user" or message.get("content") != [
                {"type": "text", "text": wanted}
            ]:
                raise PiV3ImportValidationError(
                    "branch summary context mapping differs"
                )
        elif source_type == "session_info":
            record = records[require_string(target_ids[0], "metadata target")]
            data = require_object(record.get("data"), "session metadata")
            if data.get("name") != source_value.get("name"):
                raise PiV3ImportValidationError("session metadata mapping differs")


def validate_import_journal(
    source: PiSourceState,
    values: list[JSONObject],
    expectations: JSONObject,
    report: JSONObject,
) -> tuple[str, tuple[str, ...]]:
    """功能：验证 imported journal 的 provenance、映射、隔离和 selected context。

    输入：严格 source、已过公共 Schema 的 journal、expectations 和报告。
    输出：selected head 与有序 canonical message ID。
    不变量：新 Session ID、固定 commit/source hash/report artifact 强绑定；不含任何
    run、tool execution 或 recovery 记录；branch/compaction 使用公共状态机验证器。
    失败：任一 provenance、映射、artifact、计数、context 或安全不变量漂移时拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not values:
        raise PiV3ImportValidationError("imported journal is empty")
    header = values[0]
    new_session_id = require_string(header.get("sessionId"), "target session ID")
    source_session_id = require_string(source.header.get("id"), "source session ID")
    if new_session_id == source_session_id or new_session_id != expectations.get(
        "newSessionId"
    ):
        raise PiV3ImportValidationError(
            "import must create a different expected Session ID"
        )
    provenance = require_object(header.get("provenance"), "target provenance")
    expected_provenance = {
        "source": "pi-session-v3",
        "sourceSessionId": source_session_id,
        "sourceSha256": source.sha256,
        "referenceCommit": REFERENCE_COMMIT,
        "reportArtifactId": require_object(
            expectations.get("reportArtifact"), "expected report artifact"
        ).get("artifactId"),
    }
    if any(provenance.get(key) != value for key, value in expected_provenance.items()):
        raise PiV3ImportValidationError("target PI provenance differs")
    if any(key in header for key in ("cwd", "parentSession", "sourcePath")):
        raise PiV3ImportValidationError("target header persisted a PI or source path")

    records = {
        require_string(record.get("recordId"), "target record ID"): record
        for record in values[1:]
    }
    if len(records) != len(values) - 1:
        raise PiV3ImportValidationError("target journal contains duplicate record IDs")
    forbidden = {
        require_string(value, "forbidden kind")
        for value in require_array(
            expectations.get("forbiddenJournalKinds"), "forbiddenJournalKinds"
        )
    }
    if forbidden != FORBIDDEN_IMPORTED_KINDS or any(
        record.get("kind") in forbidden for record in records.values()
    ):
        raise PiV3ImportValidationError("imported journal contains lifecycle records")
    mappings, mapped_target_ids = _source_mapping_index(source, expectations)
    synthetic_ids = {
        require_string(value, "synthetic record ID")
        for value in require_array(
            expectations.get("syntheticRecordIds"), "syntheticRecordIds"
        )
    }
    if set(records) != mapped_target_ids | synthetic_ids:
        raise PiV3ImportValidationError(
            "target records differ from mapped plus synthetic IDs"
        )
    _validate_mapping_kinds(source, mappings, records)
    _validate_message_semantics(source, mappings, records)
    _validate_report_coverage(source, mappings, report, expectations)

    expected_counts = require_object(expectations.get("counts"), "expected counts")
    dispositions = [mapping.get("disposition") for mapping in mappings.values()]
    report_counts = require_object(report.get("counts"), "report counts")
    actual_counts = {
        "sourceEntries": len(source.entries),
        "targetRecords": len(records),
        "mappedSourceEntries": sum(
            value in {"mapped", "mapped_with_loss"} for value in dispositions
        ),
        "extensionSourceEntries": sum(
            value in {"preserved_extension", "quarantined"} for value in dispositions
        ),
        "reportedSourceEntries": len(
            require_array(report.get("entries"), "report entries")
        ),
        "redactedValues": report_counts.get("redactedValues"),
        "skippedSourceEntries": report_counts.get("skippedSourceEntries"),
    }
    if actual_counts != expected_counts:
        raise PiV3ImportValidationError("derived import counts differ")

    branch_state = branch_compaction_validation.validate_values(values)
    selected_ids = tuple(
        require_string(message.get("messageId"), "selected message ID")
        for message in branch_state.messages
    )
    if branch_state.selected_head_record_id != expectations.get(
        "selectedHeadRecordId"
    ) or list(selected_ids) != expectations.get("selectedMessageIds"):
        raise PiV3ImportValidationError("selected imported context differs")
    final_source_id = require_string(
        source.entries[-1].value.get("id"), "last source ID"
    )
    final_mapping = mappings[final_source_id]
    final_target = require_string(
        require_array(final_mapping.get("targetRecordIds"), "last target IDs")[-1],
        "last target ID",
    )
    selected_chain = branch_compaction_validation.parent_chain(
        records, branch_state.selected_head_record_id
    )
    if final_target not in {
        require_string(record.get("recordId"), "selected chain record ID")
        for record in selected_chain
    }:
        raise PiV3ImportValidationError(
            "last PI entry is not represented on selected chain"
        )
    return branch_state.selected_head_record_id, selected_ids


def validate_case(case_dir: Path, schema_root: Path) -> PiV3ImportState:
    """功能：端到端验证 PI v3 clean-room source、报告 artifact 和目标 journal。

    输入：bundled case 目录与 Schema 根。
    输出：可供测试检查的不可变来源、报告和 selected context 状态。
    不变量：只读本地普通文件，不执行 PI、工具、Provider，不写源或 target。
    失败：路径、JSON、Schema、hash、provenance、映射、隐私或状态机违规时拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    expectations_path = resolve_case_file(case_dir, "expectations.json", "expectations")
    _, expectations = load_json_object(
        expectations_path,
        maximum=2_097_152,
        context="PI import expectations",
    )
    case_validator = _load_schema_validator(
        schema_root, "session/pi-v3-import-case.schema.json"
    )
    _validate_schema(expectations, case_validator, "PI import expectations")

    source_path = resolve_case_file(
        case_dir, expectations.get("sourceFile"), "expectations.sourceFile"
    )
    source = load_pi_v3_source(source_path)
    assert_synthetic_source(source)
    if (
        source.sha256 != expectations.get("sourceSha256")
        or source.header.get("id") != expectations.get("sourceSessionId")
        or expectations.get("referenceCommit") != REFERENCE_COMMIT
    ):
        raise PiV3ImportValidationError("source identity or hash expectations differ")

    report_path = resolve_case_file(
        case_dir,
        expectations.get("reportArtifactFile"),
        "expectations.reportArtifactFile",
    )
    report_raw, report = load_json_object(
        report_path,
        maximum=4_194_304,
        context="PI import report artifact",
    )
    report_validator = _load_schema_validator(
        schema_root, "session/pi-v3-import-report.schema.json"
    )
    _validate_schema(report, report_validator, "PI import report")
    expected_artifact = require_object(
        expectations.get("reportArtifact"), "expected report artifact"
    )
    if (
        len(report_raw) != expected_artifact.get("byteLength")
        or hashlib.sha256(report_raw).hexdigest() != expected_artifact.get("sha256")
        or expected_artifact.get("mediaType") != REPORT_MEDIA_TYPE
    ):
        raise PiV3ImportValidationError(
            "report artifact bytes, hash or media type differ"
        )
    report_source = require_object(report.get("source"), "report.source")
    report_target = require_object(report.get("target"), "report.target")
    if (
        report_source.get("sessionId") != source.header.get("id")
        or report_source.get("sha256") != source.sha256
        or report_source.get("byteLength") != len(source.raw)
        or report_source.get("referenceCommit") != REFERENCE_COMMIT
        or report_source.get("sourcePathDisposition") != "not_persisted"
        or report_target.get("sessionId") != expectations.get("newSessionId")
        or report_target.get("reportArtifactId") != expected_artifact.get("artifactId")
    ):
        raise PiV3ImportValidationError("report source or target provenance differs")

    journal_path = resolve_case_file(
        case_dir,
        expectations.get("expectedJournalFile"),
        "expectations.expectedJournalFile",
    )
    line_validator = session_validation.build_line_validator(schema_root)
    _, journal_values = session_validation.load_journal(journal_path, line_validator)
    session_validation.reconstruct_state(journal_values)
    artifact_records = [
        record
        for record in journal_values[1:]
        if record.get("kind") == "artifact.created"
    ]
    if len(artifact_records) != 1:
        raise PiV3ImportValidationError(
            "journal must contain one report artifact record"
        )
    artifact = require_object(
        require_object(artifact_records[0].get("data"), "artifact data").get(
            "artifact"
        ),
        "artifact reference",
    )
    if artifact != expected_artifact:
        raise PiV3ImportValidationError("journal report artifact reference differs")

    assert_no_sensitive_values([report, *journal_values], "PI import target")
    assert_no_host_paths([report, *journal_values], "PI import target")
    selected_head, selected_ids = validate_import_journal(
        source, journal_values, expectations, report
    )
    return PiV3ImportState(
        source=source,
        report=report,
        journal_values=tuple(journal_values),
        selected_head_record_id=selected_head,
        selected_message_ids=selected_ids,
    )


def build_argument_parser() -> argparse.ArgumentParser:
    """功能：构建 PI v3 import 静态 conformance 参数解析器。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(
        description="Validate the PI Session v3 clean-room import fixture."
    )
    parser.add_argument(
        "--case-dir",
        type=Path,
        default=root / "CONFORMANCE/fixtures/session/pi-v3-import-v0.1",
    )
    parser.add_argument(
        "--schema-root",
        type=Path,
        default=root / "SPEC/schemas",
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    """功能：运行 PI v3 import 静态门禁并打印不含实例内容的摘要。

    输入：可选 CLI 参数序列。
    输出：成功为 0，契约、依赖或文件失败为 1。
    不变量：不执行 PI、工具、Provider 或写入任何 Session。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = build_argument_parser().parse_args(argv)
    try:
        state = validate_case(args.case_dir, args.schema_root)
    except (
        PiV3ImportValidationError,
        session_validation.SessionValidationError,
        branch_compaction_validation.BranchCompactionError,
    ) as exc:
        print(f"PI V3 IMPORT FIXTURE FAILED: {exc}", file=sys.stderr)
        return 1
    print(
        "PI V3 IMPORT FIXTURE PASS "
        f"sourceEntries={len(state.source.entries)} "
        f"targetRecords={len(state.journal_values) - 1} "
        f"selectedMessages={len(state.selected_message_ids)}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
