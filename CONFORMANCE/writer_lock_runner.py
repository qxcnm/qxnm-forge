#!/usr/bin/env python3
"""以真实 holder/contender 进程验证跨语言 Session writer lease。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
from datetime import datetime
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
from typing import Any, Sequence

import provider_runner
import runner
import schema_validation
import session_runner
import session_validation


JSONObject = dict[str, Any]
MAX_OWNER_BYTES = 16 * 1024
OWNER_KEYS = {
    "schemaVersion",
    "protocol",
    "sessionId",
    "token",
    "endpoint",
    "pid",
    "createdAt",
    "implementation",
}
IMPLEMENTATION_LANGUAGES = {"dotnet", "rust", "go", "typescript", "python"}


class WriterLockConformanceError(Exception):
    """writer lease 黑盒 runner 的安全、协议或持久化断言失败。"""


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    """功能：解析 Schema、隔离工作根、超时与一个或多个 daemon argv 模板。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(
        description="验证 writer.lock.d loopback lease，多个命令执行有序 N×N 竞争"
    )
    parser.add_argument("--schema-root", type=Path, default=root / "SPEC/schemas")
    parser.add_argument(
        "--fixture",
        type=Path,
        default=root / "CONFORMANCE/fixtures/session/writer-lock-cases.json",
    )
    parser.add_argument(
        "--work-root",
        type=Path,
        help="保留每个 pair 状态的空目录；省略时使用并清理临时目录",
    )
    parser.add_argument(
        "--daemon-command-json",
        action="append",
        default=[],
        help=(
            "可重复的 JSON argv 数组；支持 {workspace}、{stateRoot}、"
            "{sessionsRoot}、{sessionId}、{repoRoot} 占位符"
        ),
    )
    parser.add_argument("--timeout", type=float, default=10.0)
    return parser.parse_args(argv)


def load_fixed_cases(path: Path) -> JSONObject:
    """功能：严格读取并固定验证九个规范 writer-lock 决策案例。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        fixture = require_object(
            runner.strict_json_loads(path.read_text(encoding="utf-8")), "fixture"
        )
    except (OSError, runner.ProtocolViolation) as exc:
        raise WriterLockConformanceError(
            f"cannot load writer-lock fixture: {exc}"
        ) from exc
    cases = fixture.get("cases")
    if not isinstance(cases, list):
        raise WriterLockConformanceError("writer-lock fixture cases must be an array")
    observed = [
        (case.get("name"), case.get("probe"), case.get("expected"))
        for case in cases
        if isinstance(case, dict)
    ]
    expected = [
        ("live_owner", "connected", "locked"),
        ("live_unresponsive_owner", "connected_without_response", "locked"),
        ("ambiguous_probe_timeout", "timeout", "locked"),
        ("dead_owner", "connection_refused", "stale_candidate"),
        ("metadata_initializing", "metadata_missing_within_grace", "wait"),
        ("metadata_abandoned", "metadata_missing_after_grace", "stale_candidate"),
        ("external_host_injection", "invalid_external_host", "invalid"),
        ("stale_takeover_race_lost", "atomic_rename_lost", "retry"),
        ("release_token_changed", "release_token_mismatch", "preserve"),
    ]
    if observed != expected:
        raise WriterLockConformanceError("writer-lock fixture decision order drifted")
    return fixture


def require_object(value: Any, context: str) -> JSONObject:
    """功能：要求动态 JSON 值是字符串键对象并返回该对象。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, dict) or not all(isinstance(key, str) for key in value):
        raise WriterLockConformanceError(f"{context} must be a JSON object")
    return value


def require_nonempty_string(value: Any, context: str) -> str:
    """功能：要求动态 JSON 值是非空字符串并返回。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, str) or not value:
        raise WriterLockConformanceError(f"{context} must be a non-empty string")
    return value


def validate_owner(owner: JSONObject, session_id: str) -> JSONObject:
    """功能：按公共 writer-lock Schema 值域验证有界 strict owner 对象。

    输入：已由 duplicate-key parser 读取的对象及其 canonical Session ID。
    输出：验证成功的原对象。
    不变量：外部 host、未知字段、非 256-bit token 和非五语言身份全部拒绝。
    失败：任一不变量抛出 WriterLockConformanceError，绝不连接未验证 endpoint。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if set(owner) != OWNER_KEYS:
        raise WriterLockConformanceError("owner.json fields do not match closed Schema")
    if (
        owner.get("schemaVersion") != "0.1"
        or owner.get("protocol") != "tcp-loopback-writer-lease-v1"
        or owner.get("sessionId") != session_id
    ):
        raise WriterLockConformanceError(
            "owner.json protocol or Session identity is invalid"
        )
    token = owner.get("token")
    if (
        not isinstance(token, str)
        or len(token) != 64
        or any(character not in "0123456789abcdef" for character in token)
    ):
        raise WriterLockConformanceError(
            "owner.json token is not 256-bit lowercase hex"
        )
    endpoint = require_object(owner.get("endpoint"), "owner.endpoint")
    port = endpoint.get("port")
    if (
        set(endpoint) != {"host", "port"}
        or endpoint.get("host") != "127.0.0.1"
        or not isinstance(port, int)
        or isinstance(port, bool)
        or not 1 <= port <= 65535
    ):
        raise WriterLockConformanceError("owner endpoint is not literal IPv4 loopback")
    pid = owner.get("pid")
    if (
        not isinstance(pid, int)
        or isinstance(pid, bool)
        or not 1 <= pid <= 9_007_199_254_740_991
    ):
        raise WriterLockConformanceError("owner pid is not a positive safe integer")
    created_at = require_nonempty_string(owner.get("createdAt"), "owner.createdAt")
    try:
        if not created_at.endswith("Z"):
            raise ValueError("UTC suffix is absent")
        datetime.fromisoformat(created_at[:-1] + "+00:00")
    except ValueError as exc:
        raise WriterLockConformanceError("owner createdAt is not UTC RFC 3339") from exc
    implementation = require_object(owner.get("implementation"), "owner.implementation")
    if set(implementation) != {"name", "version", "language"}:
        raise WriterLockConformanceError("owner implementation fields are not closed")
    name = require_nonempty_string(
        implementation.get("name"), "owner implementation.name"
    )
    version = require_nonempty_string(
        implementation.get("version"), "owner implementation.version"
    )
    language = implementation.get("language")
    if len(name) > 128 or len(version) > 64 or language not in IMPLEMENTATION_LANGUAGES:
        raise WriterLockConformanceError("owner implementation identity is invalid")
    return owner


def load_live_owner(owner_path: Path, session_id: str) -> tuple[bytes, JSONObject]:
    """功能：拒绝 symlink 并有界、strict 读取正在持有的 owner.json。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if owner_path.is_symlink() or not owner_path.is_file():
        raise WriterLockConformanceError(
            "owner.json is missing, non-regular, or symlinked"
        )
    try:
        raw = owner_path.read_bytes()
    except OSError as exc:
        raise WriterLockConformanceError("cannot read live owner.json") from exc
    if not raw or len(raw) > MAX_OWNER_BYTES:
        raise WriterLockConformanceError("live owner.json exceeds bounded size")
    try:
        text = raw.decode("utf-8", errors="strict")
        owner = require_object(runner.strict_json_loads(text), "owner.json")
    except (UnicodeDecodeError, runner.ProtocolViolation) as exc:
        raise WriterLockConformanceError(
            "live owner.json is not strict UTF-8 JSON"
        ) from exc
    return raw, validate_owner(owner, session_id)


def rpc(
    process: runner.DaemonProcess,
    request: JSONObject,
    frames: list[JSONObject],
    timeout: float,
) -> JSONObject:
    """功能：发送一个 RPC 并容许事件交错地等待唯一同 ID 响应。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    request_id = request.get("id")
    process.send_request(request)
    deadline = time.monotonic() + timeout
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise WriterLockConformanceError(f"timed out awaiting RPC {request_id!r}")
        try:
            frame = process.next_frame(remaining)
        except Exception as exc:
            raise WriterLockConformanceError(
                f"daemon failed while awaiting RPC {request_id!r}"
            ) from exc
        frames.append(frame)
        if "id" not in frame:
            if frame.get("method") != "event":
                raise WriterLockConformanceError(
                    "daemon emitted a non-event notification"
                )
            continue
        if frame.get("id") != request_id:
            raise WriterLockConformanceError(
                "daemon returned an unexpected response ID"
            )
        return frame


def initialize_daemon(
    process: runner.DaemonProcess,
    prefix: str,
    requests: list[JSONObject],
    frames: list[JSONObject],
    timeout: float,
) -> JSONObject:
    """功能：作为首帧协商协议并返回封闭实现身份。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    request: JSONObject = {
        "jsonrpc": "2.0",
        "id": prefix + "-initialize",
        "method": "initialize",
        "params": {
            "protocolVersions": ["0.1"],
            "client": {"name": "writer-lock-conformance", "version": "0.1.0"},
            "capabilities": {"eventReplay": True},
        },
    }
    requests.append(request)
    response = rpc(process, request, frames, timeout)
    result = require_object(response.get("result"), "initialize result")
    if result.get("protocolVersion") != "0.1":
        raise WriterLockConformanceError("daemon did not negotiate protocol 0.1")
    implementation = require_object(result.get("implementation"), "implementation")
    if implementation.get("language") not in IMPLEMENTATION_LANGUAGES:
        raise WriterLockConformanceError(
            "daemon returned an unknown implementation language"
        )
    return implementation


def build_holder_requests(
    session_id: str, pair_id: str
) -> tuple[JSONObject, JSONObject]:
    """功能：构造持久化长延时 faux 场景与异步 run/start 请求。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    configure: JSONObject = {
        "jsonrpc": "2.0",
        "id": pair_id + "-configure",
        "method": "faux/configure",
        "params": {
            "sessionId": session_id,
            "scenario": {
                "schemaVersion": "0.1",
                "name": pair_id + "-hold",
                "seed": 20260714,
                "steps": [
                    {"type": "delay", "durationMs": 30_000},
                    {"type": "text", "text": "holder should be crashed first"},
                ],
                "usage": {"inputTokens": 0, "outputTokens": 0, "totalTokens": 0},
            },
        },
    }
    start: JSONObject = {
        "jsonrpc": "2.0",
        "id": pair_id + "-start",
        "method": "run/start",
        "params": {
            "sessionId": session_id,
            "input": {
                "role": "user",
                "content": [{"type": "text", "text": "hold writer lease"}],
            },
            "provider": {"id": "faux", "modelId": "faux-v1"},
        },
    }
    return configure, start


def wait_for_run_event(
    process: runner.DaemonProcess,
    frames: list[JSONObject],
    run_id: str,
    event_type: str,
    timeout: float,
) -> None:
    """功能：等待指定 run 的 durable 生命周期可观察事件。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    deadline = time.monotonic() + timeout
    while True:
        for frame in frames:
            params = frame.get("params")
            if (
                frame.get("method") == "event"
                and isinstance(params, dict)
                and params.get("runId") == run_id
                and params.get("type") == event_type
            ):
                return
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise WriterLockConformanceError(
                f"holder did not emit required {event_type} event"
            )
        frames.append(process.next_frame(remaining))


def wait_for_journal_stable(path: Path, timeout: float) -> bytes:
    """功能：等待 holder 进入 faux delay 后 journal 在短窗口内不再变化。

    输入：canonical journal 路径和总超时。
    输出：连续 200ms 未变化的 durable 字节快照。
    不变量：只用于排除 holder 自身紧随 turn.started 的 provider.attempt append。
    失败：文件缺失、读取错误或持续变化超过超时则终止 pair。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    deadline = time.monotonic() + timeout
    stable_since = time.monotonic()
    previous: bytes | None = None
    while time.monotonic() < deadline:
        try:
            current = path.read_bytes()
        except OSError as exc:
            raise WriterLockConformanceError("cannot read holder journal") from exc
        if previous == current and time.monotonic() - stable_since >= 0.2:
            return current
        if previous != current:
            previous = current
            stable_since = time.monotonic()
        time.sleep(0.025)
    raise WriterLockConformanceError("holder journal did not become stable")


def assert_locked_response(response: JSONObject) -> None:
    """功能：要求 contender 返回固定 -32002 retryable session_locked 错误。

    输入：竞争 daemon 对状态变更请求返回的 JSON-RPC response。
    输出：code、retryable 与 details.kind 全部符合公共协议时正常返回。
    不变量：writer lease 冲突不得复用 run/session busy 的 -32004。
    失败：错误对象缺失或任一 portable 字段漂移时终止共同矩阵。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    error = require_object(response.get("error"), "locked response error")
    details = require_object(error.get("details"), "locked response details")
    if (
        error.get("code") != -32002
        or error.get("retryable") is not True
        or details.get("kind") != "session_locked"
    ):
        raise WriterLockConformanceError(
            "contender did not return -32002 retryable details.kind=session_locked"
        )


def crash_daemon(process: runner.DaemonProcess) -> None:
    """功能：无清理机会地终止 holder 进程以验证 OS witness crash 语义。

    输入：已经确认持有 lease 的 daemon 包装器。
    输出：进程树已强制退出且 reader 资源已回收。
    不变量：不向 daemon 发送取消、EOF 或普通终止信号来触发 clean release。
    失败：平台终止错误转换为 WriterLockConformanceError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        if os.name == "posix":
            os.killpg(process.process.pid, signal.SIGKILL)
        else:
            process.process.kill()
        process.process.wait(timeout=2.0)
    except (OSError, ProcessLookupError, subprocess.TimeoutExpired) as exc:
        raise WriterLockConformanceError("cannot crash holder daemon") from exc
    finally:
        process.close()


def wait_for_claim_absent(
    claim_path: Path,
    timeout: float,
    *,
    holder_pid: int | None = None,
    contender_pid: int | None = None,
) -> None:
    """功能：有界等待 clean-close 后 canonical writer claim 消失。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    deadline = time.monotonic() + timeout
    while claim_path.exists() and time.monotonic() < deadline:
        time.sleep(0.025)
    if claim_path.exists():
        role = "unknown process"
        owner_path = claim_path / "owner.json"
        try:
            raw_owner = require_object(
                runner.strict_json_loads(owner_path.read_text(encoding="utf-8")),
                "surviving owner",
            )
            owner_pid = raw_owner.get("pid")
            if owner_pid == holder_pid:
                role = "crashed holder"
            elif owner_pid == contender_pid:
                role = "cleanly closed contender"
        except (OSError, UnicodeError, runner.ProtocolViolation):
            role = "unreadable owner"
        raise WriterLockConformanceError(
            f"canonical writer claim survived clean close ({role})"
        )


def render_pair_command(
    raw_template: str,
    workspace: Path,
    state_root: Path,
    sessions_root: Path,
    session_id: str,
) -> list[str]:
    """功能：严格解析命令模板并只替换 runner 管理的已知占位符。

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


def run_pair(
    holder_template: str,
    contender_template: str,
    pair_index: int,
    pair_root: Path,
    schema_root: Path,
    timeout: float,
) -> tuple[str, str]:
    """功能：验证一个 holder→contender live 拒绝、crash 接管和 clean release。

    输入：两个安全 argv 模板、pair 序号、隔离根、Schema 根与超时。
    输出：initialize 揭示的 holder/contender 语言。
    不变量：locked 尝试不得改变 journal；holder 用 SIGKILL 留下 stale claim。
    失败：任一 wire、owner、append-only、恢复或清理断言失败即终止该 pair。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    pair_id = f"writer-pair-{pair_index + 1}"
    session_id = pair_id + "-session"
    workspace = pair_root / "workspace"
    state_root = pair_root / "state"
    sessions_root = state_root / "sessions"
    workspace.mkdir(parents=True, exist_ok=False)
    sessions_root.mkdir(parents=True, exist_ok=False)
    holder_command = render_pair_command(
        holder_template, workspace, state_root, sessions_root, session_id
    )
    contender_command = render_pair_command(
        contender_template, workspace, state_root, sessions_root, session_id
    )
    environment = {
        "QXNM_FORGE_WORKSPACE": str(workspace),
        "QXNM_FORGE_SESSION_ROOT": str(sessions_root),
        "QXNM_FORGE_CONFORMANCE": "1",
        "QXNM_FORGE_LIVE_TESTS": "0",
        "QXNM_FORGE_ALLOW_LIVE_PROVIDER_TESTS": "0",
    }
    removed_env = (
        *provider_runner.CREDENTIAL_ENV,
        *provider_runner.ENDPOINT_ENV,
        *provider_runner.PROXY_ENV,
    )
    holder = runner.DaemonProcess(
        holder_command,
        timeout=timeout,
        max_frame_bytes=1_048_576,
        extra_env=environment,
        removed_env=removed_env,
    )
    contender: runner.DaemonProcess | None = None
    holder_pid = holder.process.pid
    contender_pid: int | None = None
    holder_requests: list[JSONObject] = []
    holder_frames: list[JSONObject] = []
    contender_requests: list[JSONObject] = []
    contender_frames: list[JSONObject] = []
    holder_crashed = False
    try:
        holder_identity = initialize_daemon(
            holder, pair_id + "-holder", holder_requests, holder_frames, timeout
        )
        configure, start = build_holder_requests(session_id, pair_id)
        for request in (configure, start):
            holder_requests.append(request)
            response = rpc(holder, request, holder_frames, timeout)
            if "result" not in response:
                raise WriterLockConformanceError(
                    f"holder {request['method']} did not succeed"
                )
        start_response = next(
            frame for frame in holder_frames if frame.get("id") == start["id"]
        )
        run_id = require_nonempty_string(
            require_object(start_response.get("result"), "run/start result").get(
                "runId"
            ),
            "run/start runId",
        )
        wait_for_run_event(holder, holder_frames, run_id, "turn.started", timeout)

        session_directory = sessions_root / session_id
        journal_path = session_directory / "journal.jsonl"
        claim_path = session_directory / "writer.lock.d"
        owner_raw, owner = load_live_owner(claim_path / "owner.json", session_id)
        holder_language = require_nonempty_string(
            holder_identity.get("language"), "holder language"
        )
        if owner["implementation"]["language"] != holder_language:
            raise WriterLockConformanceError(
                "owner implementation language differs from initialize"
            )
        baseline = wait_for_journal_stable(journal_path, timeout)

        contender = runner.DaemonProcess(
            contender_command,
            timeout=timeout,
            max_frame_bytes=1_048_576,
            extra_env=environment,
            removed_env=removed_env,
        )
        contender_pid = contender.process.pid
        contender_identity = initialize_daemon(
            contender,
            pair_id + "-contender",
            contender_requests,
            contender_frames,
            timeout,
        )
        locked_request: JSONObject = {
            "jsonrpc": "2.0",
            "id": pair_id + "-locked-get",
            "method": "session/get",
            "params": {"sessionId": session_id, "afterSeq": 0},
        }
        contender_requests.append(locked_request)
        locked_response = rpc(contender, locked_request, contender_frames, timeout)
        assert_locked_response(locked_response)
        if journal_path.read_bytes() != baseline:
            raise WriterLockConformanceError(
                "locked contender modified the holder journal"
            )
        current_raw, current_owner = load_live_owner(
            claim_path / "owner.json", session_id
        )
        if current_raw != owner_raw or current_owner.get("token") != owner.get("token"):
            raise WriterLockConformanceError(
                "locked contender replaced live holder metadata"
            )

        crash_daemon(holder)
        holder_crashed = True
        success_response: JSONObject | None = None
        retry_deadline = time.monotonic() + timeout
        attempt = 0
        while time.monotonic() < retry_deadline:
            attempt += 1
            takeover_request: JSONObject = {
                "jsonrpc": "2.0",
                "id": f"{pair_id}-takeover-{attempt}",
                "method": "session/get",
                "params": {"sessionId": session_id, "afterSeq": 0},
            }
            contender_requests.append(takeover_request)
            response = rpc(contender, takeover_request, contender_frames, timeout)
            if "result" in response:
                success_response = response
                break
            assert_locked_response(response)
            time.sleep(0.025)
        if success_response is None:
            raise WriterLockConformanceError(
                "contender did not take over crashed holder within timeout"
            )
        result = require_object(success_response.get("result"), "session/get result")
        if result.get("sessionId") != session_id:
            raise WriterLockConformanceError("takeover returned another Session")

        schema_validation.validate_protocol_trace(
            holder_requests, holder_frames, schema_root
        )
        schema_validation.validate_protocol_trace(
            contender_requests, contender_frames, schema_root
        )
        validator = session_validation.build_line_validator(schema_root)
        final_raw, values = session_validation.load_journal(journal_path, validator)
        if not final_raw.startswith(baseline):
            raise WriterLockConformanceError(
                "crash recovery did not preserve the journal byte prefix"
            )
        session_validation.reconstruct_state(values)
        contender_language = require_nonempty_string(
            contender_identity.get("language"), "contender language"
        )
        return holder_language, contender_language
    finally:
        if not holder_crashed:
            holder.close()
        if contender is not None:
            contender.close()
        claim_path = sessions_root / session_id / "writer.lock.d"
        if claim_path.parent.exists():
            wait_for_claim_absent(
                claim_path,
                min(timeout, 3.0),
                holder_pid=holder_pid,
                contender_pid=contender_pid,
            )


def run_probe(args: argparse.Namespace, work_root: Path) -> int:
    """功能：对全部 daemon 模板执行有序 N×N holder/contender 差分矩阵。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if args.timeout <= 0:
        raise WriterLockConformanceError("--timeout must be positive")
    load_fixed_cases(args.fixture.resolve())
    templates = list(args.daemon_command_json)
    if not templates:
        print("PASS: writer-lock fixture 9 decisions (static only)")
        return 0
    pair_index = 0
    for holder_index, holder in enumerate(templates):
        for contender_index, contender in enumerate(templates):
            pair_root = work_root / f"pair-{pair_index + 1}"
            pair_root.mkdir(parents=True, exist_ok=False)
            holder_language, contender_language = run_pair(
                holder,
                contender,
                pair_index,
                pair_root,
                args.schema_root.resolve(),
                args.timeout,
            )
            print(
                "PASS: writer lease "
                f"{holder_index + 1}:{holder_language} -> "
                f"{contender_index + 1}:{contender_language}"
            )
            pair_index += 1
    print(f"PASS: writer lease ordered matrix {pair_index}/{len(templates) ** 2}")
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    """功能：运行静态决策门禁或隔离的跨实现 writer lease 矩阵。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = parse_args(argv)
    try:
        if args.work_root is not None:
            work_root = args.work_root.resolve()
            work_root.mkdir(parents=True, exist_ok=True)
            if any(work_root.iterdir()):
                raise WriterLockConformanceError("--work-root must be empty")
            return run_probe(args, work_root)
        with tempfile.TemporaryDirectory(prefix="qxnm-forge-writer-lock-") as temporary:
            return run_probe(args, Path(temporary))
    except (
        WriterLockConformanceError,
        runner.ConformanceError,
        runner.ProtocolViolation,
        schema_validation.SchemaValidationError,
        session_validation.SessionValidationError,
    ) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
