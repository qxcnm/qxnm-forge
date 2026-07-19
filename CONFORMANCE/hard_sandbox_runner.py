#!/usr/bin/env python3
"""Linux Bubblewrap hard-sandbox 静态和本机 OS 隔离门禁。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import json
import os
from pathlib import Path
import queue
import secrets
import selectors
import shlex
import shutil
import signal
import socket
import stat
import subprocess
import sys
import tempfile
import time
from typing import Any, Sequence

import runner

ROOT = Path(__file__).resolve().parents[1]
FIXTURE = ROOT / "CONFORMANCE/fixtures/security/hard-sandbox-cases.json"
DAEMON_HELPER = ROOT / "CONFORMANCE/fixtures/security/hard_sandbox_helper.py"
JSONObject = dict[str, Any]

LOCAL_OVERALL_TIMEOUT_SECONDS = 20.0
CASE_TIMEOUT_SECONDS = 3.0
TREE_PROBE_TIMEOUT_SECONDS = 0.75
TREE_READY_TIMEOUT_SECONDS = 2.0
MAX_OUTPUT_BYTES = 65_536
TERMINATION_GRACE_SECONDS = 0.25
CLEANUP_TIMEOUT_SECONDS = 2.0
READ_CHUNK_BYTES = 8_192
SOCKET_CONNECT_TIMEOUT_SECONDS = 0.5

HARD_SANDBOX_CAPABILITY: JSONObject = {
    "profile": "linux-bwrap-v1",
    "platform": "linux",
    "filesystem": "mount_namespace_allowlist",
    "network": "network_namespace_isolated",
    "process": "pid_namespace_die_with_parent",
    "credentials": "empty_environment",
    "execution": {
        "timeout": "bounded",
        "output": "bounded",
        "descendants": "fork_setsid_terminated_on_cancel_or_parent_exit",
    },
    "failureMode": "sandbox_unavailable_no_host_fallback",
    "selfTested": True,
}


class HardSandboxError(Exception):
    """表示 hard-sandbox fixture 或真实 OS 隔离证据失败。"""


@dataclass(frozen=True)
class ProcessIdentity:
    """表示带启动时钟、session 与 PID namespace 身份的宿主进程。"""

    pid: int
    start_ticks: int
    session_id: int
    namespace_pid: int


@dataclass(frozen=True)
class BoundedResult:
    """表示有界执行结果及监督期间观察到的完整进程树证据。"""

    returncode: int
    stdout: bytes
    stderr: bytes
    timed_out: bool
    output_limited: bool
    observed: tuple[ProcessIdentity, ...]


def strict_load(path: Path) -> JSONObject:
    """功能：严格读取 hard-sandbox JSON fixture 并拒绝重复键。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    def pairs(values: list[tuple[str, Any]]) -> JSONObject:
        """功能：构造无重复键 JSON 对象。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        result: JSONObject = {}
        for key, value in values:
            if key in result:
                raise HardSandboxError("hard-sandbox fixture contains duplicate keys")
            result[key] = value
        return result

    try:
        value = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=pairs)
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise HardSandboxError("hard-sandbox fixture cannot be loaded") from exc
    if not isinstance(value, dict):
        raise HardSandboxError("hard-sandbox fixture root must be an object")
    return value


def validate_schema(fixture: JSONObject, schema_root: Path) -> None:
    """功能：使用 bundled Draft 2020-12 Schema 验证 hard-sandbox fixture。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        import jsonschema
        from referencing import Registry, Resource
    except ImportError as exc:
        raise HardSandboxError("jsonschema/referencing is required") from exc
    resources: list[tuple[str, Resource[Any]]] = []
    schemas: dict[str, JSONObject] = {}
    for path in sorted(schema_root.rglob("*.schema.json")):
        document = json.loads(path.read_text(encoding="utf-8"))
        schemas[path.name] = document
        resources.append((document["$id"], Resource.from_contents(document)))
    validator = jsonschema.Draft202012Validator(
        schemas["hard-sandbox-cases.schema.json"],
        registry=Registry().with_resources(resources),
    )
    if list(validator.iter_errors(fixture)):
        raise HardSandboxError("hard-sandbox fixture violates its Schema")


def validate_semantics(fixture: JSONObject) -> list[JSONObject]:
    """功能：冻结四个 profile 案例及 network、执行监督和失败关闭边界。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    cases = fixture.get("cases")
    names = [case.get("name") for case in cases] if isinstance(cases, list) else []
    if fixture.get("profile") != "linux-bwrap-v1" or names != [
        "read_only_isolation",
        "read_write_isolation",
        "missing_backend_fails_closed",
        "writable_backend_fails_closed",
    ]:
        raise HardSandboxError("hard-sandbox fixture semantic contract drifted")
    expected_safety = {
        "network": "loopback_listener_only",
        "networkProbe": "python_socket_connect",
        "workspace": "temporary_only",
        "outsideCanary": "random_per_run",
        "credentialCanary": "random_per_run",
        "descendantProbe": "fork_setsid",
        "failureMode": "fail_closed_no_host_fallback",
        "execution": {
            "overallTimeoutMs": int(LOCAL_OVERALL_TIMEOUT_SECONDS * 1000),
            "caseTimeoutMs": int(CASE_TIMEOUT_SECONDS * 1000),
            "treeProbeTimeoutMs": int(TREE_PROBE_TIMEOUT_SECONDS * 1000),
            "maxOutputBytes": MAX_OUTPUT_BYTES,
            "terminationGraceMs": int(TERMINATION_GRACE_SECONDS * 1000),
            "cleanupTimeoutMs": int(CLEANUP_TIMEOUT_SECONDS * 1000),
            "socketConnectTimeoutMs": int(SOCKET_CONNECT_TIMEOUT_SECONDS * 1000),
        },
    }
    if fixture.get("safety") != expected_safety:
        raise HardSandboxError("hard-sandbox safety contract drifted")
    return cases


def validate_daemon_semantics(fixture: JSONObject) -> JSONObject:
    """功能：冻结 daemon 动态证据的配置门控、工具入口与案例顺序。

    输入：Schema 已验证的 hard-sandbox fixture。
    输出：可供动态 runner 使用的 daemon 契约对象。
    不变量：不允许新增 RPC、PATH backend 发现或仅凭 conformance 自动授权。
    失败：环境名、profile、backend、案例顺序或有界执行参数漂移时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    daemon = fixture.get("daemon")
    if not isinstance(daemon, dict):
        raise HardSandboxError("hard-sandbox daemon contract is missing")
    positive_names = [
        case.get("name")
        for case in daemon.get("positiveCases", [])
        if isinstance(case, dict)
    ]
    negative_names = [
        case.get("name")
        for case in daemon.get("negativeCases", [])
        if isinstance(case, dict)
    ]
    production_names = [
        case.get("name")
        for case in daemon.get("productionGates", [])
        if isinstance(case, dict)
    ]
    if (
        daemon.get("tool") != "process.exec"
        or daemon.get("profileEnvironment") != "AGENT_HARD_SANDBOX_PROFILE"
        or daemon.get("profile") != "linux-bwrap-v1"
        or daemon.get("productionBackend") != "/usr/bin/bwrap"
        or daemon.get("conformanceBackendEnvironment")
        != "AGENT_HARD_SANDBOX_CONFORMANCE_BACKEND"
        or daemon.get("conformanceGate")
        != {
            "cliArgument": "--conformance",
            "environment": {"name": "QXNM_FORGE_CONFORMANCE", "value": "1"},
        }
        or daemon.get("sandboxRequest")
        != {
            "profile": "linux-bwrap-v1",
            "workspaceAccess": "read_only",
            "network": "isolated",
        }
        or daemon.get("execution")
        != {
            "caseTimeoutMs": 12000,
            "readyTimeoutMs": 2000,
            "descendantDelayMs": 1200,
            "descendantSettleMs": 1600,
        }
        or positive_names
        != [
            "daemon_read_only_isolation",
            "daemon_read_write_isolation",
            "daemon_timeout_descendant_cleanup",
            "daemon_output_descendant_cleanup",
            "daemon_cancel_descendant_cleanup",
            "daemon_exit_descendant_cleanup",
        ]
        or negative_names
        != [
            "unconfigured_request_fails_closed",
            "missing_backend_startup_fails_closed",
            "writable_backend_startup_fails_closed",
            "setup_self_test_startup_fails_closed",
        ]
        or production_names
        != [
            "fixed_backend_advertises",
            "conformance_backend_presence_fails_closed",
        ]
    ):
        raise HardSandboxError("hard-sandbox daemon semantic contract drifted")
    return daemon


def read_process_identity(pid: int) -> ProcessIdentity | None:
    """功能：从 Linux procfs 读取可抗 PID 复用的进程身份与 session 证据。

    输入：宿主 PID。
    输出：进程仍存在时返回身份，否则返回 None。
    不变量：仅把相同 PID 与 starttime 组合视为同一进程。
    失败：procfs 竞争消失或无权限时按不存在处理，不泄露异常细节。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        raw_stat = Path(f"/proc/{pid}/stat").read_text(encoding="ascii")
        tail = raw_stat[raw_stat.rfind(")") + 2 :].split()
        start_ticks = int(tail[19])
        session_id = int(tail[3])
        namespace_pid = pid
        for line in (
            Path(f"/proc/{pid}/status").read_text(encoding="ascii").splitlines()
        ):
            if line.startswith("NSpid:"):
                namespace_pid = int(line.split()[-1])
                break
        return ProcessIdentity(pid, start_ticks, session_id, namespace_pid)
    except (IndexError, OSError, UnicodeError, ValueError):
        return None


def collect_process_tree(root_pid: int) -> dict[tuple[int, int], ProcessIdentity]:
    """功能：递归收集 root 及跨 process-group/session 的当前宿主后代。

    输入：待观察根 PID。
    输出：以 PID/starttime 为键的进程身份快照。
    不变量：遍历 `/proc/*/task/*/children`，不把路径前缀当成进程 containment。
    失败：并发退出造成的缺项被安全忽略，调用方必须继续做消失确认。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    found: dict[tuple[int, int], ProcessIdentity] = {}
    pending = [root_pid]
    visited: set[int] = set()
    while pending:
        pid = pending.pop()
        if pid in visited:
            continue
        visited.add(pid)
        identity = read_process_identity(pid)
        if identity is None:
            continue
        found[(identity.pid, identity.start_ticks)] = identity
        task_root = Path(f"/proc/{pid}/task")
        try:
            task_paths = list(task_root.iterdir())
        except OSError:
            continue
        for task_path in task_paths:
            try:
                children = (task_path / "children").read_text(encoding="ascii").split()
            except (OSError, UnicodeError):
                continue
            for child in children:
                try:
                    pending.append(int(child))
                except ValueError:
                    continue
    return found


def identity_is_alive(identity: ProcessIdentity) -> bool:
    """功能：按 PID 与 starttime 判断同一宿主进程是否仍存活。

    输入：先前从 procfs 记录的进程身份。
    输出：同一进程仍存在时返回 True，否则返回 False。
    不变量：PID 相同但 starttime 不同视为已消失，避免 PID 复用误杀。
    失败：procfs 读取竞争或无权限时保守返回 False，最终清理门禁仍检查其余身份。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    current = read_process_identity(identity.pid)
    return current is not None and current.start_ticks == identity.start_ticks


def wait_identities_gone(
    identities: Sequence[ProcessIdentity], timeout_seconds: float
) -> list[ProcessIdentity]:
    """功能：在有界期限内等待已记录进程身份全部消失。

    输入：抗 PID 复用身份集合与正数超时。
    输出：期限结束后仍存活的原进程身份。
    不变量：从不把同 PID 的新进程误判为旧后代。
    失败：procfs 不可读时由 identity_is_alive 按不存在处理。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    deadline = time.monotonic() + timeout_seconds
    survivors = [identity for identity in identities if identity_is_alive(identity)]
    while survivors and time.monotonic() < deadline:
        time.sleep(0.02)
        survivors = [identity for identity in survivors if identity_is_alive(identity)]
    return survivors


def signal_identities(identities: Sequence[ProcessIdentity], signum: int) -> None:
    """功能：只向身份仍匹配的进程发送终止信号，避免 PID 复用误杀。

    输入：已观察身份与 POSIX signal 编号。
    输出：无返回值。
    不变量：信号前重新核对 starttime；拒绝信号 PID 1 和当前 runner。
    失败：进程并发退出或无权限时忽略，由后续存活门禁决定结果。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    for identity in identities:
        if identity.pid in (1, os.getpid()) or not identity_is_alive(identity):
            continue
        try:
            os.kill(identity.pid, signum)
        except OSError:
            continue


def terminate_process_tree(
    process: subprocess.Popen[bytes],
    observed: dict[tuple[int, int], ProcessIdentity],
) -> None:
    """功能：有界终止根进程及 fork/setsid 后代并确认身份全部消失。

    输入：以新 session 启动的根进程和监督期间累计的身份快照。
    输出：清理完成时无返回值。
    不变量：TERM/KILL 两阶段同时覆盖进程组和逐身份后代，最终等待根进程回收。
    失败：清理期限后仍有原身份存活时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    observed.update(collect_process_tree(process.pid))
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except OSError:
        pass
    signal_identities(tuple(observed.values()), signal.SIGTERM)
    survivors = wait_identities_gone(
        tuple(observed.values()), TERMINATION_GRACE_SECONDS
    )
    if survivors:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except OSError:
            pass
        signal_identities(survivors, signal.SIGKILL)
    try:
        process.wait(timeout=CLEANUP_TIMEOUT_SECONDS)
    except subprocess.TimeoutExpired:
        try:
            process.kill()
        except OSError:
            pass
        try:
            process.wait(timeout=CLEANUP_TIMEOUT_SECONDS)
        except subprocess.TimeoutExpired as exc:
            raise HardSandboxError("hard-sandbox root process was not reaped") from exc
    survivors = wait_identities_gone(tuple(observed.values()), CLEANUP_TIMEOUT_SECONDS)
    if survivors:
        signal_identities(survivors, signal.SIGKILL)
        raise HardSandboxError("hard-sandbox descendant cleanup failed")


def run_bounded(
    command: Sequence[str],
    *,
    env: dict[str, str],
    timeout_seconds: float,
    max_output_bytes: int,
) -> BoundedResult:
    """功能：在统一 deadline、输出上限和进程树清理下直接执行 argv。

    输入：非 shell argv、最小环境、正数超时和合并 stdout/stderr 字节上限。
    输出：显式标记 timeout/output-limit 并携带有界输出与进程树证据的结果。
    不变量：stdout/stderr 持续排空但最多保留 max_output_bytes；所有异常路径清理后代。
    失败：启动失败沿用 OSError；无法回收 fork/setsid 后代时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if timeout_seconds <= 0 or max_output_bytes <= 0:
        raise HardSandboxError("hard-sandbox execution limits are invalid")
    process = subprocess.Popen(
        list(command),
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=env,
        start_new_session=True,
        close_fds=True,
    )
    if process.stdout is None or process.stderr is None:
        terminate_process_tree(process, {})
        raise HardSandboxError("hard-sandbox output pipes unavailable")
    selector = selectors.DefaultSelector()
    stdout = bytearray()
    stderr = bytearray()
    observed = collect_process_tree(process.pid)
    total_output = 0
    timed_out = False
    output_limited = False
    deadline = time.monotonic() + timeout_seconds
    streams = ((process.stdout, stdout), (process.stderr, stderr))
    try:
        for stream, target in streams:
            os.set_blocking(stream.fileno(), False)
            selector.register(stream, selectors.EVENT_READ, target)
        while selector.get_map():
            observed.update(collect_process_tree(process.pid))
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                timed_out = True
                break
            events = selector.select(min(0.05, remaining))
            for key, _ in events:
                try:
                    chunk = os.read(key.fileobj.fileno(), READ_CHUNK_BYTES)
                except BlockingIOError:
                    continue
                if not chunk:
                    selector.unregister(key.fileobj)
                    continue
                available = max_output_bytes - total_output
                if available > 0:
                    key.data.extend(chunk[:available])
                total_output += len(chunk)
                if total_output > max_output_bytes:
                    output_limited = True
                    break
            if output_limited:
                break
        if timed_out or output_limited:
            terminate_process_tree(process, observed)
        else:
            try:
                process.wait(timeout=max(0.01, deadline - time.monotonic()))
            except subprocess.TimeoutExpired:
                timed_out = True
                terminate_process_tree(process, observed)
            else:
                survivors = wait_identities_gone(
                    tuple(observed.values()), TERMINATION_GRACE_SECONDS
                )
                if survivors:
                    terminate_process_tree(process, observed)
                    raise HardSandboxError(
                        "hard-sandbox descendants survived parent exit"
                    )
        observed.update(collect_process_tree(process.pid))
        return BoundedResult(
            process.returncode,
            bytes(stdout),
            bytes(stderr),
            timed_out,
            output_limited,
            tuple(observed.values()),
        )
    except BaseException:
        if process.poll() is None or any(
            identity_is_alive(identity) for identity in observed.values()
        ):
            terminate_process_tree(process, observed)
        raise
    finally:
        selector.close()
        process.stdout.close()
        process.stderr.close()


def validate_backend(path: Path) -> None:
    """功能：验证 Bubblewrap 为 root-owned、普通且当前用户不可写的绝对系统二进制。

    输入：候选绝对路径。
    输出：符合 backend 身份策略时无返回值。
    不变量：不执行 symlink、用户可写或非 root-owned 文件。
    失败：身份不可信时抛 sandbox_unavailable 语义的 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not path.is_absolute():
        raise HardSandboxError("sandbox_unavailable")
    try:
        metadata = path.lstat()
    except OSError as exc:
        raise HardSandboxError("sandbox_unavailable") from exc
    if (
        not stat.S_ISREG(metadata.st_mode)
        or metadata.st_uid != 0
        or not metadata.st_mode & (stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
        or metadata.st_mode & (stat.S_IWGRP | stat.S_IWOTH)
        or os.access(path, os.W_OK)
        or not os.access(path, os.X_OK)
    ):
        raise HardSandboxError("sandbox_unavailable")


def bwrap_command(
    backend: Path, workspace: Path, access: str, script: str
) -> list[str]:
    """功能：渲染不经过 shell拼接的固定 linux-bwrap-v1 argv。

    输入：可信 backend、canonical 临时 workspace、读写模式和固定测试脚本。
    输出：可直接交给 subprocess 的 argv。
    不变量：不 bind 宿主 `/` 或 workspace 外数据；network/PID/mount/user 均隔离。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not workspace.is_absolute() or access not in ("read_only", "read_write"):
        raise HardSandboxError("invalid hard-sandbox command boundary")
    command = [
        str(backend),
        "--unshare-user",
        "--unshare-pid",
        "--unshare-net",
        "--unshare-ipc",
        "--unshare-uts",
        "--unshare-cgroup-try",
        "--die-with-parent",
        "--new-session",
        "--proc",
        "/proc",
        "--dev",
        "/dev",
        "--tmpfs",
        "/tmp",
    ]
    for system_path in ("/usr", "/bin", "/lib", "/lib64"):
        if Path(system_path).exists():
            command.extend(("--ro-bind", system_path, system_path))
    command.extend(
        (
            "--bind" if access == "read_write" else "--ro-bind",
            str(workspace),
            "/workspace",
            "--chdir",
            "/workspace",
            "--setenv",
            "PATH",
            "/usr/bin:/bin",
            "/usr/bin/env",
            "-i",
            "PATH=/usr/bin:/bin",
            "/bin/sh",
            "-c",
            script,
        )
    )
    return command


def sandbox_environment(secret: str | None = None) -> dict[str, str]:
    """功能：构造只含固定 PATH 与可选测试 canary 的最小宿主启动环境。

    输入：仅用于隔离自测的随机 credential canary，可省略。
    输出：不继承宿主变量的新环境映射。
    不变量：除固定 PATH/canary 外不传播任何变量，sandbox 内再由 env -i 清空 canary。
    失败：本函数不访问 credential source；无外部失败条件。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    environment = {"PATH": "/usr/bin:/bin"}
    if secret is not None:
        environment["QXNM_FORGE_SANDBOX_SECRET"] = secret
    return environment


def run_profile_command(
    backend: Path,
    workspace: Path,
    access: str,
    script: str,
    *,
    timeout_seconds: float = CASE_TIMEOUT_SECONDS,
    max_output_bytes: int = MAX_OUTPUT_BYTES,
    secret: str | None = None,
) -> BoundedResult:
    """功能：验证 backend 后仅沿 linux-bwrap-v1 路径执行有界命令。

    输入：backend、临时工作区、访问模式、固定测试脚本与可选执行界限/canary。
    输出：sandbox 子进程的 BoundedResult。
    不变量：身份或 setup 失败不调用 host shell 兜底，危险路径统一失败关闭。
    失败：backend 不可信时抛 sandbox_unavailable；执行监督失败时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    validate_backend(backend)
    return run_bounded(
        bwrap_command(backend, workspace, access, script),
        env=sandbox_environment(secret),
        timeout_seconds=timeout_seconds,
        max_output_bytes=max_output_bytes,
    )


def network_probe_command(port: int) -> str:
    """功能：渲染明确执行 Python socket.connect_ex 的本地 TCP 探针命令。

    输入：runner 在宿主 loopback 上监听的端口。
    输出：可嵌入固定 self-test 脚本且参数经 shell quote 的命令片段。
    不变量：连接成功以 44 退出；隔离导致的连接错误才以 0 退出，不依赖 `/dev/tcp`。
    失败：解释器、socket 模块或探针自身不可执行时返回非零并使 self-test 失败。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if port < 1 or port > 65_535:
        raise HardSandboxError("invalid hard-sandbox listener port")
    code = (
        "import socket,sys;"
        "client=socket.socket(socket.AF_INET,socket.SOCK_STREAM);"
        "client.settimeout(float(sys.argv[2]));"
        "result=client.connect_ex(('127.0.0.1',int(sys.argv[1])));"
        "client.close();"
        "sys.exit(44 if result == 0 else 0)"
    )
    argv = (
        "/usr/bin/python3",
        "-c",
        code,
        str(port),
        str(SOCKET_CONNECT_TIMEOUT_SECONDS),
    )
    return " ".join(shlex.quote(argument) for argument in argv)


def run_isolation_case(backend: Path, case: JSONObject) -> None:
    """功能：真实验证 workspace、宿主外、系统写、socket network 与 credential 隔离。

    输入：可信 Bubblewrap 路径和一个读写隔离案例。
    输出：所有隔离断言成立时无返回值。
    不变量：宿主先证明 listener 可达，sandbox 再实际执行 socket.connect_ex 且必须隔离；
    命令受 timeout/output/tree 统一监督。
    失败：setup、脚本、边界或 canary 任一异常均抛 HardSandboxError，不转 host 执行。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    outside_token = "outside-" + secrets.token_urlsafe(24)
    secret = "credential-" + secrets.token_urlsafe(24)
    with tempfile.TemporaryDirectory(prefix="qxnm-forge-bwrap-") as temporary:
        root = Path(temporary)
        workspace = root / "workspace"
        workspace.mkdir()
        (workspace / "inside.txt").write_text("inside", encoding="utf-8")
        outside = root / (outside_token + ".txt")
        outside.write_text(outside_token, encoding="utf-8")
        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        listener.bind(("127.0.0.1", 0))
        listener.listen(4)
        port = listener.getsockname()[1]
        access = str(case["workspaceAccess"])
        expected_write = "yes" if access == "read_write" else "no"
        script = (
            "set -eu; "
            'test "$(cat /workspace/inside.txt)" = inside; '
            "if test -e " + shlex.quote(str(outside)) + "; then exit 41; fi; "
            "if touch /usr/qxnm-forge-sandbox-write 2>/dev/null; then exit 42; fi; "
            "if env | grep -q QXNM_FORGE_SANDBOX_SECRET; then exit 43; fi; "
            + network_probe_command(port)
            + "; "
            "write=no; if touch /workspace/write.txt 2>/dev/null; then write=yes; fi; "
            'test "$write" = ' + expected_write + "; "
            "touch /tmp/works; test -f /tmp/works; printf isolated"
        )
        try:
            positive_control = socket.create_connection(
                ("127.0.0.1", port), timeout=SOCKET_CONNECT_TIMEOUT_SECONDS
            )
            positive_control.close()
            completed = run_profile_command(
                backend,
                workspace,
                access,
                script,
                secret=secret,
            )
        finally:
            listener.close()
        if (
            completed.returncode != 0
            or completed.stdout != b"isolated"
            or completed.timed_out
            or completed.output_limited
        ):
            raise HardSandboxError("linux-bwrap-v1 isolation self-test failed")
        if access == "read_only" and (workspace / "write.txt").exists():
            raise HardSandboxError("read-only workspace was modified")
        if access == "read_write" and not (workspace / "write.txt").is_file():
            raise HardSandboxError("read-write workspace was not writable")
        if secret.encode() in completed.stdout + completed.stderr:
            raise HardSandboxError("sandbox credential canary leaked")


def tree_probe_code() -> str:
    """功能：生成 fork 后调用 setsid 并按 timeout/output 模式存活的 Python 探针。

    输入：无；运行时从 argv 接收 sandbox marker 和固定模式。
    输出：不定义额外函数的 Python 源码文本。
    不变量：child 必须先 setsid 并写 namespace PID，再持续存活等待监督清理。
    失败：marker 未出现时 parent 以 71 退出，使门禁失败而非误判通过。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return (
        "import os,sys,time\n"
        "marker=sys.argv[1]\n"
        "mode=sys.argv[2]\n"
        "child=os.fork()\n"
        "if child == 0:\n"
        "    os.setsid()\n"
        "    with open(marker,'w',encoding='ascii') as stream:\n"
        "        stream.write(str(os.getpid()))\n"
        "    while True:\n"
        "        time.sleep(0.05)\n"
        "deadline=time.monotonic()+1.0\n"
        "while not os.path.exists(marker) and time.monotonic() < deadline:\n"
        "    time.sleep(0.01)\n"
        "if not os.path.exists(marker):\n"
        "    sys.exit(71)\n"
        "if mode == 'output':\n"
        "    chunk=b'x'*8192\n"
        "    while True:\n"
        "        os.write(1,chunk)\n"
        "while True:\n"
        "    time.sleep(0.05)\n"
    )


def tree_probe_script(marker: str, mode: str) -> str:
    """功能：把固定 fork/setsid 探针代码与 sandbox 内 marker/mode 渲染为命令。

    输入：sandbox 内 marker 绝对路径和固定执行模式。
    输出：所有 argv 元素均经过 shell quote 的命令文本。
    不变量：解释器固定为经过本机门禁验证的 `/usr/bin/python3`。
    失败：未知 mode 由调用方拒绝；解释器执行失败导致 self-test 非零。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    argv = ("/usr/bin/python3", "-c", tree_probe_code(), marker, mode)
    return " ".join(shlex.quote(argument) for argument in argv)


def verify_setsid_identity(
    marker: Path, identities: Sequence[ProcessIdentity]
) -> ProcessIdentity:
    """功能：用 namespace PID marker 绑定并验证真实 setsid 后代身份。

    输入：宿主可见 marker 与监督期身份快照。
    输出：匹配 namespace PID 且 session leader 为自身的宿主身份。
    不变量：仅凭观察到的 `/proc` 证据判定，不接受未执行 marker。
    失败：marker 缺失/非法或后代未被观察为 session leader 时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        namespace_pid = int(marker.read_text(encoding="ascii"))
    except (OSError, UnicodeError, ValueError) as exc:
        raise HardSandboxError("fork/setsid probe marker unavailable") from exc
    matches = [
        identity
        for identity in identities
        if identity.namespace_pid == namespace_pid
        and identity.session_id == identity.pid
    ]
    if not matches:
        raise HardSandboxError("fork/setsid descendant was not observed")
    return matches[-1]


def run_tree_termination_probe(backend: Path, mode: str) -> None:
    """功能：真实触发 timeout 或 output-limit 并验证 fork/setsid 后代被清理。

    输入：可信 backend 与 `timeout`/`output` 模式。
    输出：对应边界触发、输出有界且全部身份消失时无返回值。
    不变量：用户探针只在临时 read-write workspace 的 OS sandbox 中执行。
    失败：边界未触发、setsid 未证实或任一后代残留时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if mode not in ("timeout", "output"):
        raise HardSandboxError("unknown hard-sandbox tree probe mode")
    with tempfile.TemporaryDirectory(prefix="qxnm-forge-bwrap-tree-") as temporary:
        workspace = Path(temporary)
        marker = workspace / f"{mode}.ready"
        result = run_profile_command(
            backend,
            workspace,
            "read_write",
            tree_probe_script(f"/workspace/{marker.name}", mode),
            timeout_seconds=(
                TREE_PROBE_TIMEOUT_SECONDS
                if mode == "timeout"
                else CASE_TIMEOUT_SECONDS
            ),
            max_output_bytes=MAX_OUTPUT_BYTES,
        )
        descendant = verify_setsid_identity(marker, result.observed)
        if mode == "timeout" and (not result.timed_out or result.output_limited):
            raise HardSandboxError("hard-sandbox timeout bound was not enforced")
        if mode == "output" and (
            not result.output_limited
            or result.timed_out
            or len(result.stdout) + len(result.stderr) != MAX_OUTPUT_BYTES
        ):
            raise HardSandboxError("hard-sandbox output bound was not enforced")
        if identity_is_alive(descendant):
            raise HardSandboxError("cancelled setsid descendant survived")


def wait_for_path(path: Path, timeout_seconds: float) -> None:
    """功能：在单调时钟期限内等待本机进程探针 marker 完整落盘。

    输入：临时 marker 路径和正数超时。
    输出：期限内出现非空普通文件时无返回值。
    不变量：只接受 runner 自建临时根中的 non-symlink regular file，避免创建后写入竞态。
    失败：期限结束仍不存在、为空或不是普通文件时抛 HardSandboxError。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    deadline = time.monotonic() + timeout_seconds
    ready = False
    while time.monotonic() < deadline:
        try:
            metadata = path.lstat()
            ready = stat.S_ISREG(metadata.st_mode) and metadata.st_size > 0
        except OSError:
            ready = False
        if ready:
            break
        time.sleep(0.01)
    if not ready:
        raise HardSandboxError("hard-sandbox process marker timed out")


def run_parent_exit_probe(backend: Path) -> None:
    """功能：让 Bubblewrap 的直接父进程退出并验证 fork/setsid 后代随之消失。

    输入：可信 backend。
    输出：`--die-with-parent` 生效且所有已记录身份消失时无返回值。
    不变量：helper 只负责启动/退出控制，用户探针始终在 linux-bwrap-v1 内执行。
    失败：marker、setsid、父结束或进程树消失任一步超时即抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    helper_code = (
        "import json,subprocess,sys\n"
        "command=json.loads(sys.argv[1])\n"
        "control=sys.argv[2]\n"
        "process=subprocess.Popen(command,stdin=subprocess.DEVNULL,"
        "stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,close_fds=True)\n"
        "with open(control,'w',encoding='ascii') as stream:\n"
        "    stream.write(str(process.pid))\n"
        "sys.stdin.buffer.read(1)\n"
    )
    with tempfile.TemporaryDirectory(prefix="qxnm-forge-bwrap-parent-") as temporary:
        workspace = Path(temporary)
        marker = workspace / "parent.ready"
        control = workspace / "parent.control"
        command = bwrap_command(
            backend,
            workspace,
            "read_write",
            tree_probe_script("/workspace/parent.ready", "parent"),
        )
        helper = subprocess.Popen(
            [sys.executable, "-c", helper_code, json.dumps(command), str(control)],
            stdin=subprocess.PIPE,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            env=sandbox_environment(),
            start_new_session=True,
            close_fds=True,
        )
        identities: tuple[ProcessIdentity, ...] = ()
        try:
            wait_for_path(control, TREE_READY_TIMEOUT_SECONDS)
            try:
                root_pid = int(control.read_text(encoding="ascii"))
            except (OSError, UnicodeError, ValueError) as exc:
                raise HardSandboxError("parent-exit root marker invalid") from exc
            wait_for_path(marker, TREE_READY_TIMEOUT_SECONDS)
            snapshot = collect_process_tree(root_pid)
            identities = tuple(snapshot.values())
            verify_setsid_identity(marker, identities)
            if helper.stdin is None:
                raise HardSandboxError("parent-exit helper control unavailable")
            helper.stdin.close()
            helper.wait(timeout=TREE_READY_TIMEOUT_SECONDS)
            survivors = wait_identities_gone(identities, CLEANUP_TIMEOUT_SECONDS)
            if survivors:
                signal_identities(survivors, signal.SIGTERM)
                survivors = wait_identities_gone(survivors, TERMINATION_GRACE_SECONDS)
                signal_identities(survivors, signal.SIGKILL)
                raise HardSandboxError("parent-exit setsid descendant survived")
        finally:
            if helper.poll() is None:
                try:
                    helper.terminate()
                    helper.wait(timeout=TERMINATION_GRACE_SECONDS)
                except subprocess.TimeoutExpired:
                    helper.kill()
                    helper.wait(timeout=CLEANUP_TIMEOUT_SECONDS)
            if helper.stdin is not None and not helper.stdin.closed:
                helper.stdin.close()
            survivors = wait_identities_gone(identities, TERMINATION_GRACE_SECONDS)
            if survivors:
                signal_identities(survivors, signal.SIGKILL)


def run_setup_failure_probe(backend: Path, root: Path, expected_kind: str) -> None:
    """功能：制造 Bubblewrap bind setup 失败并证明不会在 host 执行 marker。

    输入：可信 backend、临时根与 fixture 规定的失败类型。
    输出：setup 非零退出且 marker 未出现时无返回值。
    不变量：失败命令只经过 bwrap argv；代码中没有 host process fallback 分支。
    失败：setup 意外成功、越界、执行 marker 或契约类型漂移时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if expected_kind != "sandbox_unavailable":
        raise HardSandboxError("setup failure kind drifted")
    missing_workspace = root / "missing-workspace"
    marker = root / "host-fallback-started"
    script = "touch " + shlex.quote(str(marker))
    result = run_bounded(
        bwrap_command(backend, missing_workspace, "read_write", script),
        env=sandbox_environment(),
        timeout_seconds=CASE_TIMEOUT_SECONDS,
        max_output_bytes=MAX_OUTPUT_BYTES,
    )
    if (
        result.returncode == 0
        or result.timed_out
        or result.output_limited
        or marker.exists()
    ):
        raise HardSandboxError("sandbox setup failure did not fail closed")


def run_failure_cases(backend: Path, cases: list[JSONObject]) -> None:
    """功能：验证 backend/setup 失败关闭且任何路径均不会运行 host fallback。

    输入：可信实际 backend 与两个固定失败案例。
    输出：缺失、用户可写和 setup 失败全部拒绝时无返回值。
    不变量：通过与正常执行相同的 run_profile_command 入口验证 backend 身份顺序。
    失败：失败类型不符、子代码启动或 host fallback marker 出现时抛 HardSandboxError。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    with tempfile.TemporaryDirectory(prefix="qxnm-forge-bwrap-fail-") as temporary:
        root = Path(temporary)
        missing = root / "missing-bwrap"
        writable = root / "writable-bwrap"
        writable.write_bytes(backend.read_bytes())
        writable.chmod(0o700)
        for case, candidate in zip(cases, (missing, writable), strict=True):
            marker = root / f"{case['name']}.started"
            try:
                run_profile_command(
                    candidate,
                    root,
                    "read_write",
                    "touch /workspace/" + marker.name,
                )
            except HardSandboxError as exc:
                if str(exc) != case["expected"]["failureKind"]:
                    raise
            else:
                raise HardSandboxError("untrusted backend did not fail closed")
            if marker.exists() or case["expected"].get("hostFallback") is not False:
                raise HardSandboxError("failure backend started child code")
        run_setup_failure_probe(
            backend,
            root,
            str(cases[1]["expected"].get("setupFailureKind")),
        )


def ensure_overall_deadline(started: float) -> None:
    """功能：确认完整本机 hard-sandbox 门禁未超过固定整体 deadline。

    输入：本机 suite 启动时的单调时钟值。
    输出：整体期限仍有效时无返回值。
    不变量：不使用可回拨的 wall clock；各阻塞操作另有更短独立 deadline。
    失败：总耗时超过 fixture 冻结上限时抛 HardSandboxError。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if time.monotonic() - started > LOCAL_OVERALL_TIMEOUT_SECONDS:
        raise HardSandboxError("hard-sandbox overall execution timed out")


def run_local_suite(backend: Path, cases: list[JSONObject]) -> None:
    """功能：在整体 deadline 内执行 backend、隔离、边界、后代与失败关闭门禁。

    输入：配置 backend 与 Schema/语义已验证的四个案例。
    输出：完整真实本机 profile 门禁通过时无返回值。
    不变量：全部 subprocess 使用有界监督或受控 parent-exit helper，不访问公网/Provider。
    失败：任一 profile 维度不成立均抛 sandbox_unavailable 或 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    started = time.monotonic()
    validate_backend(backend)
    probe = Path("/usr/bin/python3")
    try:
        resolved_probe = probe.resolve(strict=True)
    except OSError as exc:
        raise HardSandboxError("sandbox_unavailable") from exc
    validate_backend(resolved_probe)
    if not os.access(resolved_probe, os.X_OK):
        raise HardSandboxError("sandbox_unavailable")
    version = run_bounded(
        [str(backend), "--version"],
        env=sandbox_environment(),
        timeout_seconds=2.0,
        max_output_bytes=4_096,
    )
    if version.returncode != 0 or version.timed_out or version.output_limited:
        raise HardSandboxError("sandbox_unavailable")
    ensure_overall_deadline(started)
    run_isolation_case(backend, cases[0])
    run_isolation_case(backend, cases[1])
    ensure_overall_deadline(started)
    run_tree_termination_probe(backend, "timeout")
    run_tree_termination_probe(backend, "output")
    run_parent_exit_probe(backend)
    ensure_overall_deadline(started)
    run_failure_cases(backend, cases[2:])
    ensure_overall_deadline(started)


def parse_daemon_command(value: str) -> list[str]:
    """功能：严格解析不经过 shell 的 daemon JSON argv 模板。

    输入：CLI 中的 JSON 数组文本。
    输出：1..128 个无 NUL、有界非空字符串参数。
    不变量：不执行变量、shell、glob 或 command substitution 展开。
    失败：JSON、类型或边界非法时抛 argparse.ArgumentTypeError，且不回显 argv。
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
        or not 1 <= len(parsed) <= 128
        or not all(
            isinstance(item, str) and item and len(item) <= 4096 and "\x00" not in item
            for item in parsed
        )
    ):
        raise argparse.ArgumentTypeError(
            "daemon command must be a bounded JSON argv array"
        )
    return parsed


def render_daemon_command(
    template: Sequence[str], workspace: Path, state_root: Path, session_id: str
) -> list[str]:
    """功能：只在 argv 元素内部替换共同 runner 的五个固定路径占位符。

    输入：安全命令模板、临时 workspace/state root 和 opaque Session ID。
    输出：不经过 shell 的具体 daemon argv。
    不变量：替换结果不重新解析；未知花括号使门禁失败关闭。
    失败：空命令或未知占位符抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    replacements = {
        "{workspace}": str(workspace),
        "{stateRoot}": str(state_root),
        "{sessionsRoot}": str(state_root / "sessions"),
        "{sessionId}": session_id,
        "{repoRoot}": str(ROOT),
    }
    rendered: list[str] = []
    for item in template:
        value = item
        for marker, replacement in replacements.items():
            value = value.replace(marker, replacement)
        if "{" in value or "}" in value:
            raise HardSandboxError("daemon command contains an unknown placeholder")
        rendered.append(value)
    if not rendered:
        raise HardSandboxError("daemon command is empty")
    return rendered


def production_daemon_command(command: Sequence[str]) -> list[str]:
    """功能：移除标准 CLI 的精确 conformance 开关以构造生产模式 argv。

    输入：已展开的 daemon argv。
    输出：删除所有字面 `--conformance` 后的新 argv。
    不变量：不做子串、前缀或参数值改写，不能把其他参数误判为门控。
    失败：删除后为空时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    result = [item for item in command if item != "--conformance"]
    if not result:
        raise HardSandboxError("production daemon command is empty")
    return result


def initialize_request(request_id: str = "hard-sandbox-init-1") -> JSONObject:
    """功能：构造协商 event replay 与交互审批的协议 0.1 initialize 请求。

    输入：唯一 JSON-RPC request ID。
    输出：不含 sandbox 配置或 backend 路径的 initialize 请求。
    不变量：hard-sandbox 配置只来自受治理环境，客户端 capability 不能启用它。
    失败：纯内存构造，无外部失败。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": request_id,
        "method": "initialize",
        "params": {
            "protocolVersions": ["0.1"],
            "client": {"name": "hard-sandbox-conformance", "version": "0.1.0"},
            "capabilities": {"eventReplay": True, "interactiveApprovals": True},
        },
    }


def await_response(
    process: Any,
    request_id: str,
    frames: list[JSONObject],
    *,
    timeout: float,
    allow_events: bool,
) -> JSONObject:
    """功能：在 deadline 内取得指定 request ID 的唯一响应并保留允许的事件。

    输入：DaemonProcess、request ID、trace 容器、正超时与事件许可。
    输出：完整 success 或 error response frame。
    不变量：拒绝其他 response ID、非 Agent notification 和重复生命周期乱序。
    失败：超时、EOF 或意外 frame 时抛 HardSandboxError/底层 conformance 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    deadline = time.monotonic() + timeout
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise HardSandboxError("hard-sandbox daemon response timed out")
        try:
            frame = process.next_frame(remaining)
        except queue.Empty as exc:
            raise HardSandboxError("hard-sandbox daemon response timed out") from exc
        frames.append(frame)
        if frame.get("id") == request_id:
            return frame
        if allow_events and frame.get("method") == "event":
            continue
        raise HardSandboxError("hard-sandbox daemon emitted an unexpected frame")


def require_success(response: JSONObject, request_id: str) -> JSONObject:
    """功能：提取指定响应的对象 result 并拒绝错误或非对象成功值。

    输入：已按 ID 收到的 response 与期望 request ID。
    输出：成功 result 对象。
    不变量：不解析 display-only error message。
    失败：ID、error 或 result 形状异常时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    result = response.get("result")
    if (
        response.get("id") != request_id
        or "error" in response
        or not isinstance(result, dict)
    ):
        raise HardSandboxError("hard-sandbox daemon request failed unexpectedly")
    return result


def require_sandbox_unavailable(response: JSONObject, request_id: str) -> None:
    """功能：验证初始化或工具路径使用精确 `-32603/sandbox_unavailable` 失败。

    输入：response frame 与对应 request ID。
    输出：错误 code/retryable/details 完全满足契约时无返回值。
    不变量：不依赖 message 文本，也不接受 provider/process 通用失败冒充 sandbox 失败。
    失败：响应成功或 portable error 字段漂移时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    error = response.get("error")
    details = error.get("details") if isinstance(error, dict) else None
    if (
        response.get("id") != request_id
        or not isinstance(error, dict)
        or error.get("code") != -32603
        or error.get("retryable") is not False
        or not isinstance(details, dict)
        or details.get("kind") != "sandbox_unavailable"
    ):
        raise HardSandboxError("daemon did not return sandbox_unavailable")


def assert_hard_sandbox_capability(result: JSONObject, *, expected: bool) -> None:
    """功能：精确核对 initialize 的完整 named capability 或其缺失。

    输入：initialize result 与是否应广告。
    输出：capability、方法和工具广告符合当前配置时无返回值。
    不变量：不接受 Boolean、部分对象或未 self-test 的乐观 claim。
    失败：server capabilities 形状或 hardSandbox 值漂移时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    capabilities = result.get("capabilities")
    if not isinstance(capabilities, dict):
        raise HardSandboxError("initialize omitted server capabilities")
    observed = capabilities.get("hardSandbox")
    if expected:
        if observed != HARD_SANDBOX_CAPABILITY:
            raise HardSandboxError("daemon hardSandbox capability differs")
        tools = capabilities.get("tools")
        methods = capabilities.get("methods")
        if (
            not isinstance(tools, list)
            or "process.exec" not in tools
            or not isinstance(methods, list)
            or "approval/respond" not in methods
            or "faux/configure" not in methods
            or "run/start" not in methods
        ):
            raise HardSandboxError("hard-sandbox daemon omitted tool/approval methods")
    elif "hardSandbox" in capabilities:
        raise HardSandboxError("unconfigured daemon advertised hardSandbox")


def start_daemon(
    template: Sequence[str],
    workspace: Path,
    state_root: Path,
    *,
    timeout: float,
    profile: bool,
    backend_override: Path | None,
    conformance_argv: bool,
    conformance_environment: bool,
    secret: str,
) -> Any:
    """功能：用清理后的环境启动一个 hard-sandbox 黑盒 daemon。

    输入：argv 模板、临时根、门控组合、可选 backend override、超时与 canary。
    输出：可交互驱动的 runner.DaemonProcess。
    不变量：清除 Provider/代理/既有 sandbox 配置；override 仅按案例显式注入。
    失败：命令门控缺失或 daemon 无法启动时抛 HardSandboxError/ConformanceError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    import provider_runner
    import runner

    command = render_daemon_command(
        template, workspace, state_root, "hard-sandbox-conformance-session"
    )
    if conformance_argv:
        if "--conformance" not in command:
            raise HardSandboxError("daemon command omitted the conformance CLI gate")
    else:
        command = production_daemon_command(command)
    extra_env = {
        "QXNM_FORGE_WORKSPACE": str(workspace),
        "QXNM_FORGE_SESSION_ROOT": str(state_root / "sessions"),
        "QXNM_FORGE_CONFORMANCE": "1" if conformance_environment else "0",
        "QXNM_FORGE_LIVE_TESTS": "0",
        "QXNM_FORGE_HARD_SANDBOX_SECRET": secret,
        "HTTP_PROXY": "http://127.0.0.1:9",
        "HTTPS_PROXY": "http://127.0.0.1:9",
        "ALL_PROXY": "http://127.0.0.1:9",
        "NO_PROXY": "",
    }
    if profile:
        extra_env["AGENT_HARD_SANDBOX_PROFILE"] = "linux-bwrap-v1"
    if backend_override is not None:
        extra_env["AGENT_HARD_SANDBOX_CONFORMANCE_BACKEND"] = str(backend_override)
    removed_env = (
        *provider_runner.CREDENTIAL_ENV,
        *provider_runner.ENDPOINT_ENV,
        *provider_runner.PROXY_ENV,
        "AGENT_HARD_SANDBOX_PROFILE",
        "AGENT_HARD_SANDBOX_CONFORMANCE_BACKEND",
        "QXNM_FORGE_HARD_SANDBOX_SECRET",
    )
    return runner.DaemonProcess(
        command,
        timeout=timeout,
        max_frame_bytes=1_048_576,
        extra_env=extra_env,
        removed_env=removed_env,
    )


def sandbox_request(workspace_access: str) -> JSONObject:
    """功能：构造闭合的 `linux-bwrap-v1` process.exec sandbox 参数。

    输入：`read_only` 或 `read_write` workspace access。
    输出：profile、workspaceAccess、isolated network 三字段对象。
    不变量：network 永远为 isolated；不提供 host-network 降级开关。
    失败：未知 access 时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if workspace_access not in {"read_only", "read_write"}:
        raise HardSandboxError("unknown sandbox workspace access")
    return {
        "profile": "linux-bwrap-v1",
        "workspaceAccess": workspace_access,
        "network": "isolated",
    }


def tool_arguments(
    case: JSONObject, helper_name: str, *, listener_port: int | None = None
) -> JSONObject:
    """功能：把抽象 daemon 案例渲染为固定 `/usr/bin/python3` process.exec 参数。

    输入：Schema 案例、workspace helper 文件名与可选 loopback listener 端口。
    输出：含精确 sandbox 对象、cwd、argv、timeout 和输出上限的工具参数。
    不变量：不传宿主 workspace 路径、credential 或任意脚本；helper action 为封闭枚举。
    失败：案例 kind/port 非法时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    kind = case.get("kind")
    access = str(case.get("workspaceAccess", "read_write"))
    common: JSONObject = {
        "executable": "/usr/bin/python3",
        "cwd": ".",
        "timeoutMs": 5000,
        "outputLimitBytes": 65536,
        "sandbox": sandbox_request(access),
    }
    if kind == "isolation":
        if listener_port is None or not 1 <= listener_port <= 65_535:
            raise HardSandboxError("isolation case omitted listener port")
        common["cwd"] = "probe"
        common["timeoutMs"] = 3000
        common["args"] = [
            "../" + helper_name,
            "isolation",
            access,
            str(listener_port),
            "argv 空格 * 雪",
        ]
        return common
    if kind in {"timeout", "output", "cancel", "daemon_exit"}:
        common["args"] = [
            helper_name,
            "descendant",
            f"{kind}.ready",
            f"{kind}.escaped",
            "1200",
            "output" if kind == "output" else "sleep",
        ]
        if kind == "timeout":
            common["timeoutMs"] = 500
        elif kind == "output":
            common["timeoutMs"] = 3000
            common["outputLimitBytes"] = 32768
        else:
            common["timeoutMs"] = 5000
        return common
    if kind == "unconfigured_request":
        common["args"] = [helper_name, "marker", "host-fallback.started"]
        return common
    raise HardSandboxError("unknown daemon hard-sandbox case")


def faux_configure_request(
    index: int, session_id: str, case: JSONObject, arguments: JSONObject
) -> JSONObject:
    """功能：构造只调用一次 process.exec 的 deterministic faux scenario。

    输入：案例序号、Session ID、案例和完整工具参数。
    输出：包含一条 tool_call 与文本 continuation 的 faux/configure 请求。
    不变量：不访问真实 Provider，参数对象与随后 approval 必须逐字段一致。
    失败：纯内存构造，无 I/O。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": f"hard-sandbox-configure-{index}",
        "method": "faux/configure",
        "params": {
            "sessionId": session_id,
            "scenario": {
                "schemaVersion": "0.1",
                "name": "hard-sandbox-" + str(case["name"]),
                "seed": 20260715,
                "steps": [
                    {
                        "type": "tool_call",
                        "toolCallId": f"hard-sandbox-call-{index}",
                        "name": "process.exec",
                        "arguments": arguments,
                    }
                ],
                "continuations": [
                    {"steps": [{"type": "text", "text": "sandbox probe completed"}]}
                ],
            },
        },
    }


def run_start_request(index: int, session_id: str) -> JSONObject:
    """功能：构造 hard-sandbox faux Session 的 run/start 请求。

    输入：案例序号与唯一 Session ID。
    输出：固定用户输入和 faux Provider selection 的 JSON-RPC 请求。
    不变量：不携带 executable、backend、host path 或 secret。
    失败：纯内存构造，无 I/O。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": f"hard-sandbox-start-{index}",
        "method": "run/start",
        "params": {
            "sessionId": session_id,
            "input": {
                "role": "user",
                "content": [{"type": "text", "text": "Run the offline sandbox probe."}],
            },
            "provider": {"id": "faux", "modelId": "faux-v1"},
        },
    }


def approval_response_request(
    index: int, session_id: str, run_id: str, approval_id: str
) -> JSONObject:
    """功能：构造仅绑定当前 hard-sandbox operation 的 allow_once 审批响应。

    输入：案例序号、Session/run/approval opaque ID。
    输出：不含 allow-always 或客户端 resolutionSource 的 approval/respond 请求。
    不变量：每个 approval ID 只使用一次，sandbox 参数不在响应中重写。
    失败：纯内存构造，无 I/O。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": f"hard-sandbox-approval-{index}",
        "method": "approval/respond",
        "params": {
            "sessionId": session_id,
            "runId": run_id,
            "approvalId": approval_id,
            "decision": {"choice": "allow_once"},
        },
    }


def cancel_request(index: int, session_id: str, run_id: str) -> JSONObject:
    """功能：构造驱动统一整树清理路径的 run/cancel 请求。

    输入：案例序号、Session ID 与已接受 run ID。
    输出：精确 run 引用的 JSON-RPC 请求。
    不变量：不使用 OS signal 或直接 PID 控制绕过 daemon。
    失败：纯内存构造，无 I/O。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": f"hard-sandbox-cancel-{index}",
        "method": "run/cancel",
        "params": {"sessionId": session_id, "runId": run_id},
    }


def wait_for_regular_file(path: Path, timeout: float) -> None:
    """功能：在 monotonic deadline 内等待临时 workspace 的 non-symlink marker。

    输入：runner 独占路径和正超时。
    输出：出现非空普通文件时无返回值。
    不变量：不跟随 symlink，不接受目录、空文件或 workspace 外替代证据。
    失败：deadline 到期或对象类型异常时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            metadata = path.lstat()
            if stat.S_ISREG(metadata.st_mode) and metadata.st_size > 0:
                return
        except OSError:
            pass
        time.sleep(0.01)
    raise HardSandboxError("sandbox descendant ready marker timed out")


def validate_approval(approval: JSONObject, arguments: JSONObject) -> str:
    """功能：验证人工审批精确绑定 process.exec 及完整 sandbox 参数。

    输入：approval.requested 对象与原始工具 arguments。
    输出：合法 opaque approval ID。
    不变量：arguments 必须逐字段相等，operation hash 为 64 位小写 SHA-256 形状。
    失败：脱落/改写 sandbox、工具名、choices 或 hash 时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    approval_id = approval.get("approvalId")
    operation_hash = approval.get("operationHash")
    choices = approval.get("choices")
    if (
        not isinstance(approval_id, str)
        or not approval_id
        or approval.get("operation") != "process.exec"
        or approval.get("arguments") != arguments
        or not isinstance(operation_hash, str)
        or len(operation_hash) != 64
        or any(character not in "0123456789abcdef" for character in operation_hash)
        or not isinstance(choices, list)
        or "allow_once" not in choices
        or "deny" not in choices
    ):
        raise HardSandboxError("hard-sandbox approval binding differs")
    return approval_id


def tool_result_from_frames(frames: Sequence[JSONObject]) -> JSONObject:
    """功能：从完整 Agent trace 提取唯一 process.exec tool.completed result。

    输入：当前案例全部响应与事件帧。
    输出：唯一 portable ToolResult 对象。
    不变量：不以文本摘要代替 structured error/execution；重复结果被拒绝。
    失败：缺失、重复或形状非法时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    results: list[JSONObject] = []
    for frame in frames:
        if frame.get("method") != "event":
            continue
        params = frame.get("params")
        if not isinstance(params, dict) or params.get("type") != "tool.completed":
            continue
        data = params.get("data")
        result = data.get("result") if isinstance(data, dict) else None
        if isinstance(result, dict):
            results.append(result)
    if len(results) != 1:
        raise HardSandboxError("sandbox trace must contain one tool.completed result")
    return results[0]


def validate_execution(case: JSONObject, result: JSONObject) -> None:
    """功能：核对 hard-sandbox execution 终止原因、containment 与双流字节账。

    输入：daemon 案例及其 portable ToolResult。
    输出：成功/timeout/output/cancel 语义和 capture 不变量成立时无返回值。
    不变量：已启动 sandbox 子进程必须报告 `os_isolation`，不能降级为进程组。
    失败：execution 缺失、终止漂移或 capture 账不平时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    expected = case.get("expected")
    execution = result.get("execution")
    if not isinstance(expected, dict) or not isinstance(execution, dict):
        raise HardSandboxError("sandbox tool result omitted execution")
    if (
        execution.get("terminationReason") != expected.get("terminationReason")
        or result.get("terminationReason") != expected.get("terminationReason")
        or execution.get("containment") != "os_isolation"
    ):
        raise HardSandboxError("sandbox execution termination or containment differs")
    for stream_name in ("stdout", "stderr"):
        stream = execution.get(stream_name)
        if not isinstance(stream, dict):
            raise HardSandboxError("sandbox execution omitted stream capture")
        captured = stream.get("capturedBytes")
        total = stream.get("totalBytes")
        omitted = stream.get("omittedBytes")
        if (
            not all(
                isinstance(value, int) and not isinstance(value, bool) and value >= 0
                for value in (captured, total, omitted)
            )
            or captured + omitted != total
            or bool(stream.get("truncated")) != (omitted > 0)
        ):
            raise HardSandboxError("sandbox stream byte accounting differs")
    is_success = expected.get("terminationReason") == "exit"
    if result.get("isError") is is_success:
        raise HardSandboxError("sandbox ToolResult isError differs")


def drive_tool_case(
    process: Any,
    index: int,
    case: JSONObject,
    arguments: JSONObject,
    workspace: Path,
    *,
    timeout: float,
    expect_approval: bool,
) -> tuple[JSONObject | None, list[JSONObject]]:
    """功能：通过完整 faux/Agent/approval 链驱动一个 daemon sandbox 工具案例。

    输入：活跃 daemon、案例身份、参数、workspace、deadline 与审批预期。
    输出：普通案例的唯一 ToolResult（daemon_exit 为 None）及完整 trace。
    不变量：危险正例恰好一次 allow_once；取消只经 run/cancel；无配置负例不得启动工具。
    失败：协议顺序、审批绑定、终态或 marker readiness 漂移时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    session_id = f"hard-sandbox-session-{index}"
    frames: list[JSONObject] = []
    configure = faux_configure_request(index, session_id, case, arguments)
    process.send_request(configure)
    require_success(
        await_response(
            process,
            str(configure["id"]),
            frames,
            timeout=timeout,
            allow_events=False,
        ),
        str(configure["id"]),
    )
    start = run_start_request(index, session_id)
    process.send_request(start)
    start_result = require_success(
        await_response(
            process,
            str(start["id"]),
            frames,
            timeout=timeout,
            allow_events=False,
        ),
        str(start["id"]),
    )
    run_id = start_result.get("runId")
    if not isinstance(run_id, str) or not run_id:
        raise HardSandboxError("hard-sandbox run/start omitted runId")
    approved = False
    started = False
    terminal = False
    deadline = time.monotonic() + timeout
    while not terminal:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise HardSandboxError("hard-sandbox daemon trace timed out")
        try:
            frame = process.next_frame(remaining)
        except queue.Empty as exc:
            raise HardSandboxError("hard-sandbox daemon trace timed out") from exc
        frames.append(frame)
        if frame.get("method") != "event":
            raise HardSandboxError("hard-sandbox daemon emitted unsolicited response")
        params = frame.get("params")
        event_type = params.get("type") if isinstance(params, dict) else None
        if event_type == "approval.requested":
            if approved or not expect_approval:
                raise HardSandboxError("hard-sandbox approval presence differs")
            data = params.get("data")
            approval = data.get("approval") if isinstance(data, dict) else None
            if not isinstance(approval, dict):
                raise HardSandboxError("approval.requested omitted approval")
            approval_id = validate_approval(approval, arguments)
            response = approval_response_request(index, session_id, run_id, approval_id)
            process.send_request(response)
            require_success(
                await_response(
                    process,
                    str(response["id"]),
                    frames,
                    timeout=timeout,
                    allow_events=True,
                ),
                str(response["id"]),
            )
            approved = True
        if event_type == "tool.started":
            if started or not expect_approval:
                raise HardSandboxError("hard-sandbox tool start presence differs")
            started = True
            kind = case.get("kind")
            if kind in {"cancel", "daemon_exit"}:
                wait_for_regular_file(workspace / f"{kind}.ready", 2.0)
            if kind == "cancel":
                cancel = cancel_request(index, session_id, run_id)
                process.send_request(cancel)
                require_success(
                    await_response(
                        process,
                        str(cancel["id"]),
                        frames,
                        timeout=timeout,
                        allow_events=True,
                    ),
                    str(cancel["id"]),
                )
            elif kind == "daemon_exit":
                return None, frames
        if event_type in runner.TERMINAL_EVENTS:
            terminal = True
        if any(
            isinstance(item.get("params"), dict)
            and item["params"].get("type") in runner.TERMINAL_EVENTS
            for item in frames
        ):
            terminal = True
    if approved is not expect_approval:
        raise HardSandboxError("hard-sandbox approval count differs")
    if expect_approval and not started:
        raise HardSandboxError("approved hard-sandbox tool never started")
    return tool_result_from_frames(frames), frames


def scan_canary(root: Path, canary: str) -> None:
    """功能：有界扫描 runner 临时树并拒绝 credential canary 持久化。

    输入：runner 独占临时根与随机 canary。
    输出：未发现泄漏时无返回值。
    不变量：不跟随 symlink；单文件 8 MiB、总计 32 MiB 后失败关闭。
    失败：symlink、预算超限或匹配 canary 时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    needle = canary.encode("utf-8")
    total = 0
    for path in root.rglob("*"):
        if path.is_symlink():
            raise HardSandboxError("hard-sandbox probe tree contains a symlink")
        if not path.is_file():
            continue
        size = path.stat().st_size
        total += size
        if size > 8 * 1024 * 1024 or total > 32 * 1024 * 1024:
            raise HardSandboxError("hard-sandbox probe tree exceeds scan budget")
        if needle in path.read_bytes():
            raise HardSandboxError("hard-sandbox credential canary leaked")


def validate_isolation_result(result: JSONObject, access: str) -> None:
    """功能：解析 helper 的固定 JSON 并核对全部 daemon OS 隔离维度。

    输入：成功 ToolResult 与 workspace access。
    输出：workspace/cwd/argv/tmp/system/network/credential 证据完全成立时无返回值。
    不变量：不从任意文本推断网络结果，只接受 helper 的封闭布尔对象。
    失败：capture 缺失、JSON 非法或布尔集合不精确时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    execution = result.get("execution")
    stdout = execution.get("stdout") if isinstance(execution, dict) else None
    text = stdout.get("text") if isinstance(stdout, dict) else None
    try:
        evidence = json.loads(text) if isinstance(text, str) else None
    except json.JSONDecodeError as exc:
        raise HardSandboxError("sandbox isolation helper output is not JSON") from exc
    expected = {
        "argvPreserved": True,
        "credentialAbsent": True,
        "cwdMapped": True,
        "networkIsolated": True,
        "outsideInvisible": True,
        "systemReadOnly": True,
        "tmpWritable": True,
        "workspaceReadable": True,
        "workspaceWritable": access == "read_write",
    }
    if evidence != expected:
        raise HardSandboxError("sandbox isolation evidence differs")


def run_enabled_daemon_cases(
    template: Sequence[str], daemon_contract: JSONObject, backend: Path, timeout: float
) -> tuple[int, str]:
    """功能：在一个已 self-test daemon 中运行六个真实 sandbox 正例。

    输入：daemon argv、冻结契约、可信 backend 与总案例超时。
    输出：通过案例数和 stderr 尾部，供统一泄漏扫描。
    不变量：只用临时 workspace、loopback listener、synthetic canary 与 faux Provider。
    失败：capability、审批、隔离、终止或后代 marker 任一漂移即抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    with tempfile.TemporaryDirectory(prefix="hard-sandbox-daemon-") as temporary:
        root = Path(temporary)
        workspace = root / "workspace"
        state_root = root / "state"
        workspace.mkdir()
        state_root.mkdir()
        (workspace / "probe").mkdir()
        (workspace / "inside.txt").write_text("inside", encoding="utf-8")
        (root / "outside.txt").write_text("outside", encoding="utf-8")
        helper = workspace / DAEMON_HELPER.name
        shutil.copyfile(DAEMON_HELPER, helper)
        secret = "hard-sandbox-canary-" + secrets.token_urlsafe(32)
        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        listener.bind(("127.0.0.1", 0))
        listener.listen(4)
        port = listener.getsockname()[1]
        positive = socket.create_connection(
            ("127.0.0.1", port), timeout=SOCKET_CONNECT_TIMEOUT_SECONDS
        )
        positive.close()
        process = start_daemon(
            template,
            workspace,
            state_root,
            timeout=timeout,
            profile=True,
            backend_override=backend,
            conformance_argv=True,
            conformance_environment=True,
            secret=secret,
        )
        frames: list[JSONObject] = []
        closed = False
        try:
            init = initialize_request()
            process.send_request(init)
            initialized = require_success(
                await_response(
                    process,
                    str(init["id"]),
                    frames,
                    timeout=timeout,
                    allow_events=False,
                ),
                str(init["id"]),
            )
            assert_hard_sandbox_capability(initialized, expected=True)
            cases = daemon_contract.get("positiveCases")
            if not isinstance(cases, list):
                raise HardSandboxError("daemon positive cases are missing")
            for index, case in enumerate(cases, start=1):
                if not isinstance(case, dict):
                    raise HardSandboxError("daemon positive case is invalid")
                arguments = tool_arguments(case, helper.name, listener_port=port)
                try:
                    result, trace = drive_tool_case(
                        process,
                        index,
                        case,
                        arguments,
                        workspace,
                        timeout=timeout,
                        expect_approval=True,
                    )
                except (HardSandboxError, runner.ConformanceError) as exc:
                    raise HardSandboxError(
                        f"daemon case {case['name']} failed: {exc}"
                    ) from exc
                frames.extend(trace)
                kind = case.get("kind")
                if kind == "daemon_exit":
                    process.close()
                    closed = True
                    time.sleep(1.6)
                    if (workspace / "daemon_exit.escaped").exists():
                        raise HardSandboxError("daemon exit left a setsid descendant")
                    continue
                if result is None:
                    raise HardSandboxError("sandbox positive case omitted ToolResult")
                validate_execution(case, result)
                if kind == "isolation":
                    access = str(case["workspaceAccess"])
                    validate_isolation_result(result, access)
                    if (workspace / "write.txt").exists() is (access == "read_only"):
                        raise HardSandboxError(
                            "sandbox workspace access differs on host"
                        )
                    if access == "read_write":
                        (workspace / "write.txt").unlink()
                elif kind in {"timeout", "output", "cancel"}:
                    wait_for_regular_file(workspace / f"{kind}.ready", 2.0)
                    time.sleep(1.6)
                    if (workspace / f"{kind}.escaped").exists():
                        raise HardSandboxError(
                            "sandbox setsid descendant escaped cleanup"
                        )
            stderr = process.stderr_text(limit=131_072)
        finally:
            listener.close()
            if not closed:
                process.close()
        serialized = json.dumps(frames, ensure_ascii=False)
        if secret in serialized or secret in stderr:
            raise HardSandboxError(
                "hard-sandbox credential canary leaked to protocol or stderr"
            )
        scan_canary(workspace, secret)
        scan_canary(state_root, secret)
        return len(cases), stderr


def run_unconfigured_request_case(
    template: Sequence[str], case: JSONObject, timeout: float
) -> None:
    """功能：证明未配置 profile 时显式 sandbox 请求失败且绝不 host fallback。

    输入：daemon argv、冻结负例与超时。
    输出：无 capability、无 approval/start/execution/marker 时无返回值。
    不变量：仍通过真实 faux tool pipeline 注入 optional sandbox 字段。
    失败：未知字段、host 执行、错误 kind 或 capability 乐观广告时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    with tempfile.TemporaryDirectory(prefix="hard-sandbox-unconfigured-") as temporary:
        root = Path(temporary)
        workspace = root / "workspace"
        state_root = root / "state"
        workspace.mkdir()
        state_root.mkdir()
        helper = workspace / DAEMON_HELPER.name
        shutil.copyfile(DAEMON_HELPER, helper)
        secret = "hard-sandbox-canary-" + secrets.token_urlsafe(32)
        process = start_daemon(
            template,
            workspace,
            state_root,
            timeout=timeout,
            profile=False,
            backend_override=None,
            conformance_argv=True,
            conformance_environment=True,
            secret=secret,
        )
        frames: list[JSONObject] = []
        try:
            init = initialize_request("hard-sandbox-unconfigured-init")
            process.send_request(init)
            initialized = require_success(
                await_response(
                    process,
                    str(init["id"]),
                    frames,
                    timeout=timeout,
                    allow_events=False,
                ),
                str(init["id"]),
            )
            assert_hard_sandbox_capability(initialized, expected=False)
            arguments = tool_arguments(case, helper.name)
            try:
                result, trace = drive_tool_case(
                    process,
                    90,
                    case,
                    arguments,
                    workspace,
                    timeout=timeout,
                    expect_approval=False,
                )
            except (HardSandboxError, runner.ConformanceError) as exc:
                raise HardSandboxError(
                    f"daemon case {case['name']} failed: {exc}"
                ) from exc
            frames.extend(trace)
            if result is None or result.get("execution") is not None:
                raise HardSandboxError("unavailable sandbox fabricated execution")
            error = result.get("error")
            details = error.get("details") if isinstance(error, dict) else None
            if (
                not isinstance(error, dict)
                or error.get("code") != -32603
                or error.get("retryable") is not False
                or not isinstance(details, dict)
                or details.get("kind") != "sandbox_unavailable"
            ):
                raise HardSandboxError(
                    "unconfigured request did not return sandbox_unavailable"
                )
            if (workspace / "host-fallback.started").exists():
                raise HardSandboxError(
                    "unconfigured sandbox request used host fallback"
                )
            stderr = process.stderr_text(limit=131_072)
        finally:
            process.close()
        if secret in json.dumps(frames, ensure_ascii=False) or secret in stderr:
            raise HardSandboxError("unconfigured sandbox leaked credential canary")
        scan_canary(workspace, secret)
        scan_canary(state_root, secret)


def run_startup_failure_case(
    template: Sequence[str], kind: str, backend: Path, timeout: float
) -> None:
    """功能：驱动 missing、用户可写或非 sandbox backend 的 initialize 失败关闭。

    输入：daemon argv、失败 kind、真实 backend 与超时。
    输出：`-32603/sandbox_unavailable` 且零 Session/marker 时无返回值。
    不变量：override 只在双 conformance 门控下读取；不调用 host executor 兜底。
    失败：backend 准备、响应形状、泄漏或副作用漂移时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    with tempfile.TemporaryDirectory(prefix="hard-sandbox-startup-") as temporary:
        root = Path(temporary)
        workspace = root / "workspace"
        state_root = root / "state"
        workspace.mkdir()
        state_root.mkdir()
        if kind == "missing_backend":
            candidate = root / "missing-bwrap"
        elif kind == "user_writable_backend":
            candidate = root / "writable-bwrap"
            shutil.copyfile(backend, candidate)
            candidate.chmod(0o700)
        elif kind == "setup_non_sandbox_backend":
            candidate = setup_failure_backend()
        else:
            raise HardSandboxError("unknown startup hard-sandbox failure kind")
        secret = "hard-sandbox-canary-" + secrets.token_urlsafe(32)
        process = start_daemon(
            template,
            workspace,
            state_root,
            timeout=timeout,
            profile=True,
            backend_override=candidate,
            conformance_argv=True,
            conformance_environment=True,
            secret=secret,
        )
        frames: list[JSONObject] = []
        try:
            init = initialize_request("hard-sandbox-startup-failure")
            process.send_request(init)
            response = await_response(
                process,
                str(init["id"]),
                frames,
                timeout=timeout,
                allow_events=False,
            )
            require_sandbox_unavailable(response, str(init["id"]))
            stderr = process.stderr_text(limit=131_072)
        finally:
            process.close()
        sessions = state_root / "sessions"
        if sessions.exists() and any(sessions.rglob("*")):
            raise HardSandboxError("startup sandbox failure created Session state")
        if secret in json.dumps(frames, ensure_ascii=False) or secret in stderr:
            raise HardSandboxError("startup sandbox failure leaked credential canary")


def setup_failure_backend() -> Path:
    """功能：选择身份可信且 version 成功、但不会建立 namespace 的本机负例程序。

    输入：无；只检查固定 coreutils 候选及其 canonical regular-file 目标。
    输出：root-owned、不可写、可执行且 `--version` 成功的非 bwrap 绝对路径。
    不变量：拒绝 symlink 本身、`/usr/bin/bwrap` 与 PATH 搜索；探针受 2 秒/4 KiB 限制。
    失败：没有安全候选时抛 HardSandboxError，而不是弱化 setup self-test 负例。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    for nominal in (Path("/usr/bin/true"), Path("/usr/bin/env"), Path("/bin/echo")):
        try:
            candidate = nominal.resolve(strict=True)
            if candidate == Path("/usr/bin/bwrap"):
                continue
            validate_backend(candidate)
            version = run_bounded(
                [str(candidate), "--version"],
                env=sandbox_environment(),
                timeout_seconds=2.0,
                max_output_bytes=4096,
            )
        except (HardSandboxError, OSError, subprocess.SubprocessError):
            continue
        if (
            version.returncode == 0
            and not version.timed_out
            and not version.output_limited
        ):
            return candidate
    raise HardSandboxError("sandbox_unavailable")


def run_production_gates(template: Sequence[str], backend: Path, timeout: float) -> int:
    """功能：验证生产固定 backend 广告及 conformance override presence 失败关闭。

    输入：daemon argv、已验证 `/usr/bin/bwrap` 与超时。
    输出：通过的生产 gate 数量 2。
    不变量：生产命令移除精确 `--conformance`；固定正例不设置 override。
    失败：生产搜索替代 backend、忽略 override presence 或 capability 漂移时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if backend != Path("/usr/bin/bwrap"):
        raise HardSandboxError("production gate requires fixed /usr/bin/bwrap")
    for index, override in enumerate((None, backend), start=1):
        with tempfile.TemporaryDirectory(
            prefix="hard-sandbox-production-"
        ) as temporary:
            root = Path(temporary)
            workspace = root / "workspace"
            state_root = root / "state"
            workspace.mkdir()
            state_root.mkdir()
            secret = "hard-sandbox-canary-" + secrets.token_urlsafe(32)
            process = start_daemon(
                template,
                workspace,
                state_root,
                timeout=timeout,
                profile=True,
                backend_override=override,
                conformance_argv=False,
                conformance_environment=True,
                secret=secret,
            )
            frames: list[JSONObject] = []
            try:
                init = initialize_request(f"hard-sandbox-production-{index}")
                process.send_request(init)
                response = await_response(
                    process,
                    str(init["id"]),
                    frames,
                    timeout=timeout,
                    allow_events=False,
                )
                if override is None:
                    initialized = require_success(response, str(init["id"]))
                    assert_hard_sandbox_capability(initialized, expected=True)
                else:
                    require_sandbox_unavailable(response, str(init["id"]))
                stderr = process.stderr_text(limit=131_072)
            finally:
                process.close()
            if secret in json.dumps(frames, ensure_ascii=False) or secret in stderr:
                raise HardSandboxError(
                    "production sandbox gate leaked credential canary"
                )
    return 2


def run_daemon_suite(
    template: Sequence[str], daemon_contract: JSONObject, backend: Path, timeout: float
) -> tuple[int, int, int]:
    """功能：执行一个原生 daemon 的正例、负例与生产门控完整矩阵。

    输入：JSON argv 模板、冻结 daemon 契约、固定 backend 与超时。
    输出：`(6 positives, 4 negatives, 2 production gates)`。
    不变量：Linux-only、faux/offline、无真实 Provider；所有危险执行显式 allow_once。
    失败：平台、backend 或任一动态证据不足时抛 HardSandboxError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not sys.platform.startswith("linux"):
        raise HardSandboxError("linux-bwrap-v1 daemon suite requires Linux")
    validate_backend(backend)
    positives, _ = run_enabled_daemon_cases(template, daemon_contract, backend, timeout)
    negative_cases = daemon_contract.get("negativeCases")
    if not isinstance(negative_cases, list) or len(negative_cases) != 4:
        raise HardSandboxError("daemon negative cases are missing")
    unconfigured = negative_cases[0]
    if not isinstance(unconfigured, dict):
        raise HardSandboxError("unconfigured sandbox case is invalid")
    run_unconfigured_request_case(template, unconfigured, timeout)
    for case in negative_cases[1:]:
        if not isinstance(case, dict):
            raise HardSandboxError("startup sandbox case is invalid")
        run_startup_failure_case(template, str(case["kind"]), backend, timeout)
    production = run_production_gates(template, backend, timeout)
    return positives, len(negative_cases), production


def build_parser() -> argparse.ArgumentParser:
    """功能：构建静态、本机 Bubblewrap 与可选原生 daemon 门禁参数。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser(description="qxnm-forge linux-bwrap-v1 conformance")
    parser.add_argument("--fixture", type=Path, default=FIXTURE)
    parser.add_argument("--schema-root", type=Path, default=ROOT / "SPEC/schemas")
    parser.add_argument("--backend", type=Path, default=Path("/usr/bin/bwrap"))
    parser.add_argument("--run-local", action="store_true")
    parser.add_argument(
        "--daemon-command-json", action="append", type=parse_daemon_command
    )
    parser.add_argument("--timeout", type=float, default=30.0)
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    """功能：执行 hard-sandbox 静态、本机及原生 daemon 动态证据门禁。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = build_parser().parse_args(argv)
    try:
        if args.timeout <= 0 or args.timeout > 60:
            raise HardSandboxError("hard-sandbox daemon timeout must be in (0, 60]")
        fixture = strict_load(args.fixture)
        validate_schema(fixture, args.schema_root)
        cases = validate_semantics(fixture)
        daemon_contract = validate_daemon_semantics(fixture)
        print("PASS: hard-sandbox static 4 local / 12 daemon gates")
        if args.run_local:
            if not sys.platform.startswith("linux"):
                print("SKIP: linux-bwrap-v1 local platform_unavailable")
            else:
                run_local_suite(args.backend, cases)
                print("PASS: linux-bwrap-v1 local 4/4")
        commands = args.daemon_command_json or []
        for index, command in enumerate(commands, start=1):
            positives, negatives, production = run_daemon_suite(
                command, daemon_contract, args.backend, args.timeout
            )
            print(
                f"PASS: linux-bwrap-v1 daemon-{index} "
                f"{positives} positives / {negatives} negatives / "
                f"{production} production gates"
            )
        return 0
    except (
        HardSandboxError,
        OSError,
        subprocess.SubprocessError,
        runner.ConformanceError,
    ) as exc:
        print(f"hard-sandbox conformance failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
