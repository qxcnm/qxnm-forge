#!/usr/bin/env python3
"""ADR 0021 Linux file.read/file.write 句柄一致性离线 conformance runner。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import queue
import secrets
import stat
import sys
import tempfile
import time
from typing import Any, Sequence

import provider_runner
import runner
import schema_validation
import session_runner
import session_validation
import tool_runner
import tool_validation


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SCHEMA_ROOT = ROOT / "SPEC/schemas"
DEFAULT_CASES = ROOT / "CONFORMANCE/fixtures/security/path-boundary-race-cases.json"
CONFIGURATION_ENVIRONMENT = "AGENT_PATH_BOUNDARY_CONFORMANCE_CONFIG"
SESSION_ID = tool_runner.SESSION_ID
MAX_SCAN_BYTES = 16_777_216

JSONObject = dict[str, Any]


class PathBoundaryConformanceError(Exception):
    """表示 ADR 0021 静态、协议、持久化或文件系统断言失败。"""


@dataclass(frozen=True)
class MutationOutcome:
    """保存并发路径重绑定后仍代表原对象的检查路径。"""

    pinned_root: Path
    pinned_target: Path
    moved_old_leaf: Path | None


def load_fixture(path: Path, schema_root: Path) -> JSONObject:
    """功能：严格加载并以 bundled Draft 2020-12 Schema 验证 ADR 0021 fixture。

    输入：机器 fixture 和本地 Schema 根。
    输出：字段已由 Schema 收窄的 JSON object。
    不变量：只注册本地 Schema，不解析网络引用；重复键和非标准数值先被拒绝。
    失败：文件、UTF-8、JSON、Schema 引擎或实例违规抛脱敏共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        import jsonschema
        from referencing import Registry, Resource
    except ImportError as exc:
        raise PathBoundaryConformanceError(
            "path boundary validation requires jsonschema and referencing"
        ) from exc
    try:
        value = runner.strict_json_loads(path.read_text(encoding="utf-8"))
        if not isinstance(value, dict):
            raise PathBoundaryConformanceError(
                "path boundary fixture must be an object"
            )
        schema_path = schema_root / "path-boundary-race-cases.schema.json"
        schema = runner.strict_json_loads(schema_path.read_text(encoding="utf-8"))
        if not isinstance(schema, dict):
            raise PathBoundaryConformanceError("path boundary Schema must be an object")
        jsonschema.Draft202012Validator.check_schema(schema)
        schema_id = schema.get("$id")
        if not isinstance(schema_id, str):
            raise PathBoundaryConformanceError("path boundary Schema omitted $id")
        registry = Registry().with_resource(schema_id, Resource.from_contents(schema))
        errors = sorted(
            jsonschema.Draft202012Validator(schema, registry=registry).iter_errors(
                value
            ),
            key=lambda item: (
                tuple(str(part) for part in item.absolute_path),
                tuple(str(part) for part in item.absolute_schema_path),
            ),
        )
    except PathBoundaryConformanceError:
        raise
    except Exception as exc:
        raise PathBoundaryConformanceError(
            "cannot validate path boundary fixture"
        ) from exc
    if errors:
        raise PathBoundaryConformanceError(
            "path boundary fixture violates bundled Schema"
        )
    return value


def validate_fixture_semantics(
    fixture: JSONObject,
) -> tuple[list[JSONObject], list[JSONObject]]:
    """功能：冻结六案例顺序、四 checkpoint 和两项生产缺门组合。

    输入：已通过 Schema 的 fixture。
    输出：六个动态案例和两个生产负例。
    不变量：read/write 各覆盖 root、nested parent、leaf，toolCallId 全局唯一。
    失败：任何顺序、关联或闭集漂移抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    cases = fixture.get("cases")
    negative = fixture.get("productionNegativeCases")
    if not isinstance(cases, list) or not all(isinstance(case, dict) for case in cases):
        raise PathBoundaryConformanceError("path boundary cases are invalid")
    if not isinstance(negative, list) or not all(
        isinstance(case, dict) for case in negative
    ):
        raise PathBoundaryConformanceError("path boundary production gates are invalid")
    expected_names = [
        "read_workspace_root_rebind",
        "write_workspace_root_rebind",
        "read_nested_parent_rebind",
        "write_nested_parent_rebind",
        "read_leaf_rebind",
        "write_leaf_rebind",
    ]
    if [case.get("name") for case in cases] != expected_names:
        raise PathBoundaryConformanceError("path boundary case order changed")
    tool_call_ids = [case.get("toolCallId") for case in cases]
    if len(set(tool_call_ids)) != len(tool_call_ids):
        raise PathBoundaryConformanceError("path boundary toolCallId must be unique")
    expected_negative = [
        ("configuration_with_cli_gate_only", True, False),
        ("configuration_with_environment_gate_only", False, True),
    ]
    observed_negative = [
        (case.get("name"), case.get("cliGate"), case.get("environmentGate"))
        for case in negative
    ]
    if observed_negative != expected_negative:
        raise PathBoundaryConformanceError("path boundary production gates changed")
    return list(cases), list(negative)


def render_command(
    template: Sequence[str], workspace: Path, state_root: Path
) -> list[str]:
    """功能：只展开 runner 自建 workspace/state 和仓库根占位符。

    输入：严格 JSON argv 模板及本案例临时路径。
    输出：保持 argv 边界、不经过 shell 的命令。
    不变量：实现命令必须显式含独立 `--conformance`，供正例双门使用。
    失败：模板缺少 conformance 门时抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    command = session_runner.render_command(
        list(template),
        {
            "workspace": str(workspace),
            "stateRoot": str(state_root),
            "sessionsRoot": str(state_root / "sessions"),
            "sessionId": SESSION_ID,
            "repoRoot": str(ROOT),
        },
    )
    if "--conformance" not in command:
        raise PathBoundaryConformanceError(
            "path boundary daemon command requires exact --conformance"
        )
    return command


def scenario_for(case: JSONObject) -> JSONObject:
    """功能：为一个路径案例构造单工具调用和确定性 continuation 的 faux 场景。

    输入：fixture case。
    输出：只含 file.read 或 file.write 的两轮 faux scenario。
    不变量：toolCallId、相对路径和写内容逐字来自受 Schema 约束的 fixture。
    失败：缺失固定字段时抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    tool_name = case.get("toolName")
    tool_call_id = case.get("toolCallId")
    relative_path = case.get("relativePath")
    if not all(
        isinstance(value, str) for value in (tool_name, tool_call_id, relative_path)
    ):
        raise PathBoundaryConformanceError("path boundary case identity is invalid")
    arguments: JSONObject = {"path": relative_path}
    if tool_name == "file.write":
        content = case.get("writeContent")
        if not isinstance(content, str):
            raise PathBoundaryConformanceError("write case omitted content")
        arguments["content"] = content
    return {
        "schemaVersion": "0.1",
        "name": "path-boundary-" + str(case.get("name")),
        "seed": 2100,
        "steps": [
            {
                "type": "tool_call",
                "toolCallId": tool_call_id,
                "name": tool_name,
                "arguments": arguments,
            }
        ],
        "usage": {"inputTokens": 1, "outputTokens": 1, "totalTokens": 2},
        "continuations": [
            {
                "steps": [{"type": "text", "text": "path boundary complete"}],
                "usage": {"inputTokens": 1, "outputTokens": 1, "totalTokens": 2},
            }
        ],
    }


def write_strict_json(path: Path, value: JSONObject, mode: int = 0o600) -> None:
    """功能：以 create-exclusive、完整写入和 fsync 发布 runner 控制 JSON。

    输入：runner 独占路径、JSON object 和固定权限。
    输出：完整 regular file 与父目录项已刷新后返回。
    不变量：不覆盖、不跟随 symlink；序列化使用紧凑 UTF-8 JSON。
    失败：创建、写入或同步错误向上抛出。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(path, flags, mode)
    try:
        data = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode(
            "utf-8"
        )
        offset = 0
        while offset < len(data):
            offset += os.write(descriptor, data[offset:])
        os.fsync(descriptor)
    finally:
        os.close(descriptor)
    directory = os.open(path.parent, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def wait_ready(control: Path, case: JSONObject, timeout: float) -> None:
    """功能：有界等待并严格验证 daemon 发布的 ready.json。

    输入：0700 control 目录、当前 case 和正超时。
    输出：exact ready record durable 后返回。
    不变量：拒绝 symlink、非 regular、超限、重复键、未知字段和关联漂移。
    失败：期限内未出现或内容非法抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    path = control / "ready.json"
    if (control / "release.json").exists() or (control / "release.json").is_symlink():
        raise PathBoundaryConformanceError("release control file appeared before ready")
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            descriptor = os.open(
                path,
                os.O_RDONLY
                | getattr(os, "O_CLOEXEC", 0)
                | getattr(os, "O_NOFOLLOW", 0),
            )
        except FileNotFoundError:
            time.sleep(0.005)
            continue
        except OSError as exc:
            raise PathBoundaryConformanceError("ready control file is unsafe") from exc
        try:
            metadata = os.fstat(descriptor)
            if not stat.S_ISREG(metadata.st_mode) or metadata.st_size > 4096:
                raise PathBoundaryConformanceError("ready control file is unsafe")
            raw = bytearray()
            while True:
                chunk = os.read(descriptor, 4097 - len(raw))
                if not chunk:
                    break
                raw.extend(chunk)
                if len(raw) > 4096:
                    raise PathBoundaryConformanceError("ready control file is unsafe")
            value = runner.strict_json_loads(
                bytes(raw).decode("utf-8", errors="strict")
            )
        except Exception as exc:
            if isinstance(exc, PathBoundaryConformanceError):
                raise
            raise PathBoundaryConformanceError("ready control JSON is invalid") from exc
        finally:
            os.close(descriptor)
        expected = {
            "schemaVersion": "0.1",
            "caseId": case.get("name"),
            "toolCallId": case.get("toolCallId"),
            "checkpoint": case.get("checkpoint"),
            "state": "ready",
        }
        if value != expected:
            raise PathBoundaryConformanceError("ready control correlation changed")
        return
    raise PathBoundaryConformanceError("ready control file timed out")


def seed_case(workspace: Path, outside: Path, case: JSONObject, canary: str) -> Path:
    """功能：创建案例的 pinned inside 文件和带随机 canary 的 outside 目标。

    输入：fresh workspace/outside、fixture case 和本轮随机 canary。
    输出：workspace 内原始目标路径。
    不变量：父目录均为 runner 新建 regular directory，不创建预置 symlink。
    失败：fixture 路径或内容非法时抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    relative_path = case.get("relativePath")
    inside_content = case.get("insideContent")
    if not isinstance(relative_path, str) or not isinstance(inside_content, str):
        raise PathBoundaryConformanceError("path boundary seed is invalid")
    target = workspace / Path(relative_path)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(inside_content, encoding="utf-8", newline="")
    outside.mkdir()
    (outside / "target.txt").write_text(canary + "\n", encoding="utf-8", newline="")
    (outside / "unchanged.txt").write_text(
        "outside unchanged\n", encoding="utf-8", newline=""
    )
    return target


def file_kind(mode: int) -> str:
    """功能：把 lstat mode 映射为不跟随链接的稳定对象类型标签。

    输入：POSIX st_mode。
    输出：file、directory、symlink 或 other。
    不变量：类型判断不读取对象内容。
    失败：本方法不返回错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if stat.S_ISREG(mode):
        return "file"
    if stat.S_ISDIR(mode):
        return "directory"
    if stat.S_ISLNK(mode):
        return "symlink"
    return "other"


def read_regular_nofollow(
    path: Path, max_bytes: int, context: str
) -> tuple[bytes, os.stat_result]:
    """功能：以 no-follow fd 有界读取一个 regular file。

    输入：待读路径、正字节上限和不含内容的诊断标签。
    输出：完整字节及同一打开对象的 fstat 元数据。
    不变量：不跟随最终 symlink；读取对象必须始终为 bounded regular file。
    失败：symlink、特殊对象、超限或 I/O 错误抛共同错误，不回显正文。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if max_bytes <= 0:
        raise ValueError("regular file read limit must be positive")
    try:
        descriptor = os.open(
            path,
            os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0),
        )
    except OSError as exc:
        raise PathBoundaryConformanceError(f"cannot open {context} safely") from exc
    try:
        metadata = os.fstat(descriptor)
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_size > max_bytes:
            raise PathBoundaryConformanceError(f"{context} is not bounded regular")
        data = bytearray()
        while True:
            chunk = os.read(descriptor, min(65_536, max_bytes + 1 - len(data)))
            if not chunk:
                break
            data.extend(chunk)
            if len(data) > max_bytes:
                raise PathBoundaryConformanceError(f"{context} exceeds limit")
        return bytes(data), metadata
    finally:
        os.close(descriptor)


def tree_manifest(root: Path) -> dict[str, tuple[Any, ...]]:
    """功能：构造不跟随 symlink 且忽略 atime 的完整树 manifest。

    输入：runner 自建 outside 根。
    输出：相对路径到类型、身份、权限、时间、内容 hash/链接目标的映射。
    不变量：总读取量受 16 MiB 限制；mtime/ctime 纳入，atime 明确排除。
    失败：特殊对象、超限或读取错误抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    result: dict[str, tuple[Any, ...]] = {}
    total = 0
    pending = [root]
    while pending:
        path = pending.pop()
        metadata = path.lstat()
        kind = file_kind(metadata.st_mode)
        relative = "." if path == root else path.relative_to(root).as_posix()
        extra: str | None = None
        if kind == "file":
            data, metadata = read_regular_nofollow(
                path, max(1, MAX_SCAN_BYTES - total), "outside manifest file"
            )
            total += len(data)
            if total > MAX_SCAN_BYTES:
                raise PathBoundaryConformanceError("outside manifest exceeds limit")
            extra = hashlib.sha256(data).hexdigest()
        elif kind == "symlink":
            extra = os.readlink(path)
        elif kind == "directory":
            pending.extend(sorted(path.iterdir(), reverse=True))
        else:
            raise PathBoundaryConformanceError("outside tree contains special object")
        result[relative] = (
            kind,
            stat.S_IMODE(metadata.st_mode),
            metadata.st_uid,
            metadata.st_gid,
            metadata.st_dev,
            metadata.st_ino,
            metadata.st_nlink,
            metadata.st_size,
            metadata.st_mtime_ns,
            metadata.st_ctime_ns,
            extra,
        )
    return result


def mutate_case(
    workspace: Path, target: Path, outside: Path, case: JSONObject
) -> MutationOutcome:
    """功能：在 ready 后实施 fixture 指定的 root、parent 或 leaf rename/rebind。

    输入：原 workspace/target、outside 和当前 case。
    输出：可继续检查的 pinned 根、精确目标，以及 leaf-write 的 moved old leaf（若有）。
    不变量：symlink 只创建在原 workspace 路径空间，outside 自身零写入。
    失败：未知 mutationTarget 抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    mutation = case.get("mutationTarget")
    moved_old_leaf: Path | None = None
    if mutation == "workspace_root":
        pinned = workspace.with_name(workspace.name + ".pinned")
        workspace.rename(pinned)
        os.symlink(outside, workspace, target_is_directory=True)
        sync_parent(workspace.parent)
        return MutationOutcome(
            pinned,
            pinned / str(case.get("relativePath")),
            None,
        )
    if mutation == "nested_parent":
        parent = target.parent
        moved = parent.with_name(parent.name + ".pinned")
        parent.rename(moved)
        os.symlink(outside, parent, target_is_directory=True)
        sync_parent(parent.parent)
        return MutationOutcome(workspace, moved / target.name, None)
    if mutation == "leaf":
        moved_old_leaf = target.with_name(target.name + ".pinned-old")
        target.rename(moved_old_leaf)
        os.symlink(outside / "target.txt", target)
        sync_parent(target.parent)
        pinned_target = (
            target if case.get("toolName") == "file.write" else moved_old_leaf
        )
        return MutationOutcome(workspace, pinned_target, moved_old_leaf)
    raise PathBoundaryConformanceError("unknown path boundary mutation")


def sync_parent(path: Path) -> None:
    """功能：刷新 runner rename/symlink 所在目录项以固定 barrier 时点。

    输入：runner 拥有的现存目录。
    输出：fsync 完成后无返回值。
    不变量：不创建、不删除、不遍历链接。
    失败：open/fsync 错误向上抛出。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def event_type(frame: JSONObject) -> str | None:
    """功能：从 event notification 安全提取事件类型。

    输入：严格 NDJSON frame。
    输出：事件类型字符串；非事件为 None。
    不变量：不从展示文本猜测语义。
    失败：本方法不返回错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    params = frame.get("params")
    value = params.get("type") if isinstance(params, dict) else None
    return value if frame.get("method") == "event" and isinstance(value, str) else None


def event_data(frame: JSONObject) -> JSONObject:
    """功能：取得 event notification 的对象 data。

    输入：已确认事件 frame。
    输出：data object。
    不变量：缺失或非对象永不静默转换。
    失败：形状非法抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    params = frame.get("params")
    data = params.get("data") if isinstance(params, dict) else None
    if not isinstance(data, dict):
        raise PathBoundaryConformanceError("event data must be an object")
    return data


def await_success(
    process: runner.DaemonProcess,
    request_id: str,
    frames: list[JSONObject],
    state_root: Path,
    schema_root: Path,
    timeout: float,
    *,
    allow_events: bool = True,
) -> JSONObject:
    """功能：等待指定成功响应并在线验证期间出现的 durable events。

    输入：daemon、请求 ID、帧集合、state/Schema 和正超时。
    输出：匹配 ID 的成功 response。
    不变量：任何允许的 event 先验证 append-before-observe；未知 response ID 立即失败。
    失败：超时、错误响应或协议漂移抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    deadline = time.monotonic() + timeout
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise PathBoundaryConformanceError("daemon response timed out")
        try:
            frame = process.next_frame(remaining)
        except queue.Empty as exc:
            raise PathBoundaryConformanceError("daemon response timed out") from exc
        frames.append(frame)
        if event_type(frame) is not None:
            if not allow_events:
                raise PathBoundaryConformanceError(
                    "daemon emitted an event before required control response"
                )
            tool_runner.assert_event_durable(state_root, schema_root, frame)
            continue
        if frame.get("id") != request_id:
            raise PathBoundaryConformanceError("daemon returned unexpected response id")
        if isinstance(frame.get("error"), dict) or not isinstance(
            frame.get("result"), dict
        ):
            raise PathBoundaryConformanceError(
                "daemon returned unexpected request failure"
            )
        return frame


def assert_waiting_for_release(process: runner.DaemonProcess) -> None:
    """功能：确认 ready 发布后 daemon 未在 release 前继续发送协议帧。

    输入：已命中 barrier 的 daemon。
    输出：短静默窗口内无下一帧时无返回值。
    不变量：checkpoint 的工具完成与终态必须发生在 runner release 之后。
    失败：提前事件、响应或进程退出抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        process.next_frame(0.05)
    except queue.Empty:
        return
    raise PathBoundaryConformanceError("daemon advanced past checkpoint before release")


def verify_protocol(
    requests: Sequence[JSONObject],
    frames: Sequence[JSONObject],
    case: JSONObject,
    schema_root: Path,
) -> None:
    """功能：验证路径案例的公共协议 Schema、生命周期与精确工具事件序列。

    输入：实际发送的请求、观察帧、fixture case 和 Schema 根。
    输出：Schema、请求关联、唯一终态和语义事件顺序全部通过时无返回值。
    不变量：write 的 approval response 先于 resolved/started；read 不产生 approval。
    失败：Schema 或生命周期漂移抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        schema_validation.validate_protocol_trace(requests, frames, schema_root)
        runner.TraceValidator(requests).validate(frames)
    except (schema_validation.SchemaValidationError, runner.ProtocolViolation) as exc:
        raise PathBoundaryConformanceError(
            "path boundary protocol trace violates the common contract"
        ) from exc
    observed = [
        value
        for value in (event_type(frame) for frame in frames)
        if value in tool_validation.SEMANTIC_EVENTS
    ]
    expected = [
        "run.started",
        "turn.started",
        "tool.requested",
    ]
    if case.get("toolName") == "file.write":
        expected.extend(["approval.requested", "approval.resolved"])
    expected.extend(
        [
            "tool.started",
            "tool.completed",
            "turn.completed",
            "turn.started",
            "run.completed",
        ]
    )
    if observed != expected:
        raise PathBoundaryConformanceError(
            "path boundary semantic event order differs from ADR 0021"
        )


def verify_journal(state_root: Path, schema_root: Path, case: JSONObject) -> None:
    """功能：验证每例最终只有一个工具事实链和唯一 run terminal。

    输入：隔离 state、Schema 和当前 case。
    输出：journal Schema、ID 关联和 exact-one 计数均通过时无返回值。
    不变量：write 额外要求唯一 approval request/resolution；read 要求二者为零。
    失败：记录缺失、重复、关联或终态漂移抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    provider_runner.validate_probe_journals(state_root, schema_root)
    records = tool_runner._load_session_records(state_root, schema_root)
    tool_call_id = case.get("toolCallId")

    def matching(kind: str) -> list[JSONObject]:
        """功能：筛选 kind 且 data.toolCallId 命中当前案例的记录。

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

    if len(matching("tool.intent")) != 1 or len(matching("tool.result")) != 1:
        raise PathBoundaryConformanceError("journal tool facts are not exact-one")
    messages = [
        record
        for record in records
        if record.get("kind") == "message.appended"
        and isinstance(record.get("data"), dict)
        and isinstance(record["data"].get("message"), dict)
        and record["data"]["message"].get("toolCallId") == tool_call_id
    ]
    if len(messages) != 1:
        raise PathBoundaryConformanceError(
            "journal canonical tool message is not exact-one"
        )
    completed = [
        record
        for record in records
        if record.get("kind") == "event.emitted"
        and isinstance(record.get("data"), dict)
        and isinstance(record["data"].get("event"), dict)
        and record["data"]["event"].get("type") == "tool.completed"
        and isinstance(record["data"]["event"].get("data"), dict)
        and record["data"]["event"]["data"].get("toolCallId") == tool_call_id
    ]
    terminals = [record for record in records if record.get("kind") == "run.terminal"]
    if len(completed) != 1 or len(terminals) != 1:
        raise PathBoundaryConformanceError(
            "journal completion or terminal is not exact-one"
        )
    requested = [
        record for record in records if record.get("kind") == "approval.requested"
    ]
    resolved = [
        record for record in records if record.get("kind") == "approval.resolved"
    ]
    if case.get("toolName") == "file.write":
        if len(requested) != 1 or len(resolved) != 1:
            raise PathBoundaryConformanceError(
                "journal approval facts differ from operation"
            )
        requested_data = requested[0].get("data")
        approval = (
            requested_data.get("approval") if isinstance(requested_data, dict) else None
        )
        resolved_data = resolved[0].get("data")
        if (
            not isinstance(approval, dict)
            or approval.get("toolCallId") != tool_call_id
            or not isinstance(resolved_data, dict)
            or resolved_data.get("approvalId") != approval.get("approvalId")
            or resolved_data.get("decision") != {"choice": "allow_once"}
            or resolved_data.get("resolutionSource") != "client"
        ):
            raise PathBoundaryConformanceError(
                "journal approval correlation differs from operation"
            )
    elif requested or resolved:
        raise PathBoundaryConformanceError(
            "read operation unexpectedly persisted approval"
        )
    terminal_index = records.index(terminals[0])
    terminal_events = [
        index
        for index, record in enumerate(records)
        if record.get("kind") == "event.emitted"
        and isinstance(record.get("data"), dict)
        and isinstance(record["data"].get("event"), dict)
        and record["data"]["event"].get("type") == "run.completed"
    ]
    if len(terminal_events) != 1 or terminal_index >= terminal_events[0]:
        raise PathBoundaryConformanceError(
            "run terminal was not durably appended before observation"
        )
    try:
        state = session_validation.reconstruct_state(
            [{"kind": "session", "sessionId": SESSION_ID}, *records]
        )
    except session_validation.SessionValidationError as exc:
        raise PathBoundaryConformanceError(
            "journal cross-record invariants failed"
        ) from exc
    if state.unfinished_run_ids:
        raise PathBoundaryConformanceError("journal retained an unfinished run")


def assert_canary_absent(label: str, data: bytes, canary: str) -> None:
    """功能：在有界字节中拒绝随机 outside canary 泄漏。

    输入：安全标签、待查字节和随机 canary。
    输出：未命中时无返回值。
    不变量：错误不回显 canary 或原始内容。
    失败：命中时抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if canary.encode("utf-8") in data:
        raise PathBoundaryConformanceError(f"{label} leaked outside canary")


def scan_tree(root: Path, canary: str) -> None:
    """功能：有界扫描不跟随 symlink 的 regular files 并拒绝 canary。

    输入：runner 自建 state/control/pinned 根和随机 canary。
    输出：所有 regular file 均未命中时无返回值。
    不变量：总读取量最多 16 MiB；symlink 与特殊对象不打开。
    失败：超限、I/O 或 canary 命中抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not root.exists() and not root.is_symlink():
        return
    total = 0
    pending = [root]
    while pending:
        path = pending.pop()
        metadata = path.lstat()
        if stat.S_ISDIR(metadata.st_mode):
            pending.extend(path.iterdir())
        elif stat.S_ISREG(metadata.st_mode):
            data, _ = read_regular_nofollow(
                path, max(1, MAX_SCAN_BYTES - total), "canary scan file"
            )
            total += len(data)
            if total > MAX_SCAN_BYTES:
                raise PathBoundaryConformanceError("canary scan exceeds limit")
            assert_canary_absent("filesystem tree", data, canary)


def run_case(
    template: Sequence[str], case: JSONObject, schema_root: Path, timeout: float
) -> None:
    """功能：以 fresh daemon 驱动一个确定性 rename/rebind 句柄案例。

    输入：daemon argv 模板、fixture case、Schema 根和正超时。
    输出：协议、barrier、pinned 结果、outside manifest、journal 和零泄漏全部通过。
    不变量：仅使用 faux Provider和 runner 临时目录；write 必须真实 allow_once。
    失败：任一动态断言不符抛脱敏共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    canary = "path-boundary-canary-" + secrets.token_urlsafe(32)
    process: runner.DaemonProcess | None = None
    try:
        with tempfile.TemporaryDirectory(prefix="path-boundary-") as temporary:
            root = Path(temporary)
            workspace = root / "workspace"
            state_root = root / "state"
            outside = root / "outside"
            control = root / "control"
            workspace.mkdir()
            state_root.mkdir()
            control.mkdir(mode=0o700)
            os.chmod(control, 0o700)
            target = seed_case(workspace, outside, case, canary)
            outside_before = tree_manifest(outside)
            config = root / "barrier-config.json"
            write_strict_json(
                config,
                {
                    "schemaVersion": "0.1",
                    "caseId": case.get("name"),
                    "toolCallId": case.get("toolCallId"),
                    "checkpoint": case.get("checkpoint"),
                    "controlDirectory": str(control),
                    "timeoutMs": 5000,
                },
            )
            command = render_command(template, workspace, state_root)
            environment = {
                "QXNM_FORGE_CONFORMANCE": "1",
                "QXNM_FORGE_SESSION_ROOT": str(state_root),
                "QXNM_FORGE_WORKSPACE": str(workspace),
                CONFIGURATION_ENVIRONMENT: str(config),
            }
            process = runner.DaemonProcess(
                command,
                timeout=timeout,
                max_frame_bytes=1_048_576,
                extra_env=environment,
                removed_env=(
                    *provider_runner.CREDENTIAL_ENV,
                    *provider_runner.ENDPOINT_ENV,
                    *provider_runner.PROXY_ENV,
                    CONFIGURATION_ENVIRONMENT,
                ),
            )
            frames: list[JSONObject] = []
            requests: list[JSONObject] = []
            initialize_request = tool_runner._initialize_request(
                case.get("toolName") == "file.write"
            )
            requests.append(initialize_request)
            process.send_request(initialize_request)
            await_success(
                process,
                "tool-initialize-1",
                frames,
                state_root,
                schema_root,
                timeout,
            )
            scenario_case: JSONObject = {"scenario": scenario_for(case)}
            configure_request = tool_runner._configure_request(scenario_case)
            requests.append(configure_request)
            process.send_request(configure_request)
            await_success(
                process,
                "tool-configure-1",
                frames,
                state_root,
                schema_root,
                timeout,
            )
            start_request = tool_runner._run_start_request()
            requests.append(start_request)
            process.send_request(start_request)
            start = await_success(
                process,
                "tool-run-start-1",
                frames,
                state_root,
                schema_root,
                timeout,
            )
            result = start.get("result")
            run_id = result.get("runId") if isinstance(result, dict) else None
            if not isinstance(run_id, str) or not run_id:
                raise PathBoundaryConformanceError("run/start omitted runId")
            released = False
            terminal_seen = False
            completion: JSONObject | None = None
            mutation_outcome: MutationOutcome | None = None
            approval_count = 0
            started_count = 0
            deadline = time.monotonic() + timeout
            while not terminal_seen:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise PathBoundaryConformanceError("path boundary run timed out")
                frame = process.next_frame(remaining)
                frames.append(frame)
                current_type = event_type(frame)
                if current_type is None:
                    raise PathBoundaryConformanceError(
                        "unexpected asynchronous response"
                    )
                tool_runner.assert_event_durable(state_root, schema_root, frame)
                data = event_data(frame)
                if current_type == "approval.requested":
                    if case.get("toolName") != "file.write" or approval_count != 0:
                        raise PathBoundaryConformanceError(
                            "path boundary case produced an unexpected approval"
                        )
                    approval = data.get("approval")
                    approval_id = (
                        approval.get("approvalId")
                        if isinstance(approval, dict)
                        else None
                    )
                    if not isinstance(approval_id, str):
                        raise PathBoundaryConformanceError(
                            "approval omitted approvalId"
                        )
                    scenario_step = scenario_case["scenario"]["steps"][0]
                    tool_runner._approval_from_event(frame, scenario_step)
                    request = tool_runner._approval_response_request(
                        run_id, approval_id, "allow_once"
                    )
                    requests.append(request)
                    process.send_request(request)
                    response = await_success(
                        process,
                        str(request["id"]),
                        frames,
                        state_root,
                        schema_root,
                        timeout,
                        allow_events=False,
                    )
                    tool_runner.assert_control_response_durable(
                        state_root, schema_root, request, response
                    )
                    if response["result"].get("accepted") is not True:
                        raise PathBoundaryConformanceError(
                            "approval/respond was not accepted"
                        )
                    approval_count += 1
                elif current_type == "tool.started" and data.get(
                    "toolCallId"
                ) == case.get("toolCallId"):
                    started_count += 1
                    if started_count != 1 or released:
                        raise PathBoundaryConformanceError(
                            "tool.started was missing or duplicated"
                        )
                    if case.get("toolName") == "file.write" and approval_count != 1:
                        raise PathBoundaryConformanceError(
                            "write started before one delivered approval response"
                        )
                    wait_ready(control, case, timeout)
                    assert_waiting_for_release(process)
                    mutation_outcome = mutate_case(workspace, target, outside, case)
                    write_strict_json(
                        control / "release.json",
                        {
                            "schemaVersion": "0.1",
                            "caseId": case.get("name"),
                            "toolCallId": case.get("toolCallId"),
                            "checkpoint": case.get("checkpoint"),
                            "state": "release",
                        },
                    )
                    released = True
                elif current_type == "tool.completed" and data.get(
                    "toolCallId"
                ) == case.get("toolCallId"):
                    completion = data
                if current_type in runner.TERMINAL_EVENTS:
                    terminal_seen = True
            process.close_stdin()
            process.wait_for_exit(timeout)
            stderr = process.stderr_text(limit=131_072).encode("utf-8")
            process.close()
            process = None
            if not released or completion is None:
                raise PathBoundaryConformanceError(
                    "barrier or completion was not observed"
                )
            if mutation_outcome is None or started_count != 1:
                raise PathBoundaryConformanceError("pinned mutation outcome is missing")
            expected_approvals = 1 if case.get("toolName") == "file.write" else 0
            if approval_count != expected_approvals:
                raise PathBoundaryConformanceError(
                    "approval count differs from operation"
                )
            tool_result = completion.get("result")
            if (
                not isinstance(tool_result, dict)
                or tool_result.get("isError") is not False
            ):
                raise PathBoundaryConformanceError("path boundary tool did not succeed")
            pinned_root = mutation_outcome.pinned_root
            pinned_target = mutation_outcome.pinned_target
            moved_old_leaf = mutation_outcome.moved_old_leaf
            if case.get("toolName") == "file.read":
                content = tool_result.get("content")
                text = (
                    content[0].get("text")
                    if isinstance(content, list)
                    and content
                    and isinstance(content[0], dict)
                    else None
                )
                if text != case.get("insideContent"):
                    raise PathBoundaryConformanceError(
                        "read did not use pinned content"
                    )
                if (
                    pinned_target.is_symlink()
                    or not pinned_target.is_file()
                    or pinned_target.read_text(encoding="utf-8")
                    != case.get("insideContent")
                ):
                    raise PathBoundaryConformanceError(
                        "read mutation lost the pinned original file"
                    )
            else:
                if pinned_target.is_symlink() or pinned_target.read_text(
                    encoding="utf-8"
                ) != case.get("writeContent"):
                    raise PathBoundaryConformanceError(
                        "write did not land in pinned tree"
                    )
                if case.get("name") == "write_leaf_rebind":
                    if moved_old_leaf is None or moved_old_leaf.read_text(
                        encoding="utf-8"
                    ) != case.get("insideContent"):
                        raise PathBoundaryConformanceError(
                            "leaf replace changed moved old file"
                        )
            if tree_manifest(outside) != outside_before:
                raise PathBoundaryConformanceError("outside tree changed")
            verify_protocol(requests, frames, case, schema_root)
            verify_journal(state_root, schema_root, case)
            frame_bytes = json.dumps(frames, ensure_ascii=False).encode("utf-8")
            assert_canary_absent("protocol frames", frame_bytes, canary)
            assert_canary_absent("daemon stderr", stderr, canary)
            scan_tree(state_root, canary)
            scan_tree(control, canary)
            scan_tree(pinned_root, canary)
    except Exception as exc:
        safe_message = str(exc).replace(canary, "[REDACTED]")
        if isinstance(exc, PathBoundaryConformanceError) and safe_message == str(exc):
            raise
        raise PathBoundaryConformanceError(safe_message or type(exc).__name__) from exc
    finally:
        if process is not None:
            process.close()


def run_production_gate(
    template: Sequence[str], gate: JSONObject, schema_root: Path, timeout: float
) -> None:
    """功能：用无人写 FIFO 证明单门配置在读取前由 initialize 固定拒绝。

    输入：daemon argv、生产负例、Schema 根和正超时。
    输出：固定错误、零 ready、零 journal 且进程 clean exit 时无返回值。
    不变量：CLI-only 把环境门设为 0；environment-only 删除 argv 的精确 conformance 元素。
    失败：FIFO 被阻塞读取、错误形状或持久化副作用漂移抛共同错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    process: runner.DaemonProcess | None = None
    try:
        with tempfile.TemporaryDirectory(prefix="path-boundary-gate-") as temporary:
            root = Path(temporary)
            workspace = root / "workspace"
            state_root = root / "state"
            control = root / "control"
            workspace.mkdir()
            state_root.mkdir()
            control.mkdir(mode=0o700)
            fifo = root / "configuration.fifo"
            os.mkfifo(fifo, 0o600)
            command = render_command(template, workspace, state_root)
            if gate.get("cliGate") is False:
                command = [value for value in command if value != "--conformance"]
            environment = {
                "QXNM_FORGE_CONFORMANCE": "1" if gate.get("environmentGate") else "0",
                "QXNM_FORGE_SESSION_ROOT": str(state_root),
                CONFIGURATION_ENVIRONMENT: str(fifo),
            }
            process = runner.DaemonProcess(
                command,
                timeout=timeout,
                max_frame_bytes=1_048_576,
                extra_env=environment,
                removed_env=(
                    *provider_runner.CREDENTIAL_ENV,
                    *provider_runner.ENDPOINT_ENV,
                    *provider_runner.PROXY_ENV,
                    CONFIGURATION_ENVIRONMENT,
                ),
            )
            request = tool_runner._initialize_request(False)
            process.send_request(request)
            try:
                frame = process.next_frame(timeout)
            except queue.Empty as exc:
                raise PathBoundaryConformanceError(
                    "single-gate daemon likely read the configuration FIFO"
                ) from exc
            error = frame.get("error")
            details = error.get("details") if isinstance(error, dict) else None
            if (
                frame.get("id") != request.get("id")
                or not isinstance(error, dict)
                or error.get("code") != -32603
                or error.get("retryable") is not False
                or not isinstance(details, dict)
                or details.get("kind") != "conformance_configuration_invalid"
            ):
                raise PathBoundaryConformanceError(
                    "single-gate initialize error differs from ADR 0021"
                )
            try:
                schema_validation.validate_protocol_trace(
                    [request], [frame], schema_root
                )
            except schema_validation.SchemaValidationError as exc:
                raise PathBoundaryConformanceError(
                    "single-gate initialize error violates protocol Schema"
                ) from exc
            process.close_stdin()
            process.wait_for_exit(timeout)
            stderr = process.stderr_text(limit=131_072)
            process.close()
            process = None
            if (control / "ready.json").exists() or list(
                state_root.rglob("journal.jsonl")
            ):
                raise PathBoundaryConformanceError(
                    "single-gate configuration created durable side effects"
                )
            if not stat.S_ISFIFO(fifo.lstat().st_mode):
                raise PathBoundaryConformanceError(
                    "single-gate configuration source was modified"
                )
            sensitive = str(fifo)
            if (
                sensitive in json.dumps(frame, ensure_ascii=False)
                or sensitive in stderr
            ):
                raise PathBoundaryConformanceError(
                    "single-gate error leaked config path"
                )
    finally:
        if process is not None:
            process.close()


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    """功能：解析静态或真实 daemon path-boundary runner 参数。

    输入：可选 argv；省略时读取 sys.argv。
    输出：路径、命令模板和正超时均已由 argparse 收窄的 Namespace。
    不变量：daemon command 只接受 JSON argv string，可重复用于多实现手工验证。
    失败：非法参数由 argparse 以退出码 2 拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--schema-root", type=Path, default=DEFAULT_SCHEMA_ROOT)
    parser.add_argument("--cases", type=Path, default=DEFAULT_CASES)
    parser.add_argument("--daemon-command-json", action="append")
    parser.add_argument(
        "--case",
        choices=(
            "read_workspace_root_rebind",
            "write_workspace_root_rebind",
            "read_nested_parent_rebind",
            "write_nested_parent_rebind",
            "read_leaf_rebind",
            "write_leaf_rebind",
        ),
        help="仅运行一个动态 race case；生产缺门门禁仍完整运行",
    )
    parser.add_argument("--timeout", type=float, default=30.0)
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    """功能：运行 ADR 0021 静态门禁及可选 Linux daemon 6+2 动态门禁。

    输入：CLI argv。
    输出：全部通过为 0；合同或实现失败为 1；参数错误由 argparse 返回 2。
    不变量：不访问网络或真实 Provider；非 Linux 动态请求明确跳过而不外推证据。
    失败：只输出脱敏短错误，不回显临时路径、canary 或完整 daemon argv。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = parse_args(argv)
    if args.timeout <= 0:
        print("PATH BOUNDARY FAIL: timeout must be positive", file=sys.stderr)
        return 1
    try:
        fixture = load_fixture(args.cases.resolve(), args.schema_root.resolve())
        cases, negative = validate_fixture_semantics(fixture)
        print("PATH BOUNDARY FIXTURE PASS: races=6, productionGates=2")
        if not args.daemon_command_json:
            return 0
        if not sys.platform.startswith("linux"):
            print("PATH BOUNDARY DYNAMIC SKIP: Linux required")
            return 0
        templates = [
            session_runner.parse_command_template(raw)
            for raw in args.daemon_command_json
        ]
        selected_cases = [
            case for case in cases if args.case is None or case.get("name") == args.case
        ]
        for template in templates:
            for case in selected_cases:
                run_case(template, case, args.schema_root.resolve(), args.timeout)
            for gate in negative:
                run_production_gate(
                    template, gate, args.schema_root.resolve(), args.timeout
                )
        print(
            "PATH BOUNDARY BLACK-BOX PASS: "
            f"implementations={len(templates)}, "
            f"races={len(selected_cases) * len(templates)}, "
            f"productionGates={2 * len(templates)}"
        )
        return 0
    except Exception as exc:
        message = str(exc).splitlines()[0] if str(exc) else type(exc).__name__
        print(f"PATH BOUNDARY FAIL: {message}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
