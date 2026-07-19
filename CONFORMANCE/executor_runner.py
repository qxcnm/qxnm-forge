#!/usr/bin/env python3
"""qxnm-forge process/shell/terminal 离线 conformance runner。

默认只复制并执行本机 Python 解释器与临时 workspace 内的固定 helper；
提供 daemon argv 后才进行黑盒 RPC 探针。不会联网，也不会读取真实 Provider 凭据。
作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
import json
import os
import queue
import re
import secrets
import shutil
import signal
import subprocess
import sys
import tempfile
import time
from collections.abc import Sequence
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
FIXTURE = ROOT / "CONFORMANCE/fixtures/executor/executor-cases.json"
HELPER = ROOT / "CONFORMANCE/fixtures/executor/executor_helper.py"
JSONObject = dict[str, Any]


class ExecutorRunnerError(Exception):
    """executor fixture、helper、协议或安全断言失败。"""


def strict_load(path: Path) -> JSONObject:
    """功能：严格读取 JSON fixture 并拒绝重复键、尾随数据和非对象根。

    输入：仓库内受控 JSON 文件路径。
    输出：字符串键 JSON 对象。
    不变量：不执行文件内容，不允许 NaN/Infinity 或重复属性覆盖。
    失败：文件、UTF-8、JSON 或根类型不合法时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    def reject_pairs(pairs: list[tuple[str, Any]]) -> JSONObject:
        """功能：构造 fixture 对象并拒绝重复属性。

        输入：JSON decoder 收集的属性对。
        输出：无重复字符串键对象。
        不变量：不采用 last-value-wins。
        失败：发现重复键时抛出 ExecutorRunnerError。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        value: JSONObject = {}
        for key, item in pairs:
            if key in value:
                raise ExecutorRunnerError(f"duplicate fixture key: {key}")
            value[key] = item
        return value

    def reject_constant(value: str) -> Any:
        """功能：拒绝 JSON 标准之外的浮点常量。

        输入：decoder 识别的常量名称。
        输出：无；始终抛出安全 fixture 错误。
        不变量：fixture 不携带 NaN 或 Infinity。
        失败：任一非标准常量都会失败关闭。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        raise ExecutorRunnerError(f"non-finite fixture number: {value}")

    try:
        text = path.read_text(encoding="utf-8")
        value = json.loads(
            text,
            object_pairs_hook=reject_pairs,
            parse_constant=reject_constant,
        )
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ExecutorRunnerError(f"cannot read executor fixture: {type(exc).__name__}") from exc
    if not isinstance(value, dict):
        raise ExecutorRunnerError("executor fixture root must be an object")
    return value


def current_platform() -> str:
    """功能：把宿主系统归一化为 fixture 使用的 linux/macos/windows 标签。

    输入：当前 Python 平台信息。
    输出：一个稳定平台标签。
    不变量：未知平台不被误判为已支持的平台。
    失败：未知系统返回 `other`，由 runner 标记 skip 而非 PASS。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if os.name == "nt":
        return "windows"
    if sys.platform == "darwin":
        return "macos"
    if sys.platform.startswith("linux"):
        return "linux"
    return "other"


def validate_schema(fixture: JSONObject, schema_root: Path) -> None:
    """功能：使用 Draft 2020-12 validator 校验 executor 机器夹具。

    输入：已严格解码的 fixture 和 Schema 根目录。
    输出：Schema 通过时无返回值。
    不变量：不自动修补 fixture，不因缺少可选依赖而伪造通过。
    失败：jsonschema 不可用或任一 Schema violation 时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        import jsonschema
        from referencing import Registry, Resource
    except ImportError as exc:  # pragma: no cover - optional developer dependency
        raise ExecutorRunnerError(
            "jsonschema and referencing are required for --schema-root"
        ) from exc
    schema_path = schema_root / "executor-cases.schema.json"
    schema = json.loads(schema_path.read_text(encoding="utf-8"))
    resources: list[tuple[str, Resource[Any]]] = []
    for path in sorted(schema_root.rglob("*.schema.json")):
        document = json.loads(path.read_text(encoding="utf-8"))
        resources.append((document["$id"], Resource.from_contents(document)))
    registry = Registry().with_resources(resources)
    validator = jsonschema.Draft202012Validator(schema, registry=registry)
    errors = sorted(validator.iter_errors(fixture), key=lambda item: tuple(item.absolute_path))
    if errors:
        raise ExecutorRunnerError(
            f"executor fixture schema violation at {list(errors[0].absolute_path)}"
        )


def terminal_notification_validator(schema_root: Path) -> Any:
    """功能：构造离线 terminal notification 的跨文件 Draft 2020-12 validator。

    输入：包含公共协议 Schema 的本地根目录。
    输出：只引用 `terminal.schema.json#/$defs/notification` 的 validator。
    不变量：所有 `$ref` 都从仓库本地 registry 解析，不访问网络或修改 Schema。
    失败：依赖缺失、Schema 不可读或 registry 无法建立时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        import jsonschema
        from referencing import Registry, Resource
    except ImportError as exc:  # pragma: no cover - required by the executor gate
        raise ExecutorRunnerError(
            "jsonschema and referencing are required for terminal validation"
        ) from exc
    try:
        resources: list[tuple[str, Resource[Any]]] = []
        for path in sorted(schema_root.rglob("*.schema.json")):
            document = json.loads(path.read_text(encoding="utf-8"))
            resources.append((document["$id"], Resource.from_contents(document)))
        registry = Registry().with_resources(resources)
        terminal_schema = json.loads(
            (schema_root / "protocol/terminal.schema.json").read_text(encoding="utf-8")
        )
    except (OSError, UnicodeError, json.JSONDecodeError, KeyError) as exc:
        raise ExecutorRunnerError("cannot build terminal notification validator") from exc
    return jsonschema.Draft202012Validator(
        {
            "$schema": terminal_schema["$schema"],
            "$ref": terminal_schema["$id"] + "#/$defs/notification",
        },
        registry=registry,
    )


def require_list(value: Any, context: str) -> list[Any]:
    """功能：取得 fixture 中的 JSON 数组并提供安全上下文。

    输入：任意动态 JSON 值与字段标签。
    输出：原数组对象。
    不变量：调用者不会把标量当作案例列表迭代。
    失败：类型不符时抛出 ExecutorRunnerError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, list):
        raise ExecutorRunnerError(f"{context} must be an array")
    return value


def validate_semantics(fixture: JSONObject, helper: Path) -> None:
    """功能：执行 Schema 之外的安全语义门禁，拒绝网络、任意命令和危险路径。

    输入：executor fixture 与固定 helper 路径。
    输出：所有案例均为受控离线动作时无返回值。
    不变量：helper 文件名固定；shell 文本只允许夹具中的两个静态脚本；案例名全局唯一。
    失败：发现未知 action、危险 token、重复名、超预算或 helper 缺失时抛出错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if helper.name != fixture.get("helper") or not helper.is_file():
        raise ExecutorRunnerError("executor helper is missing or has an unexpected name")
    safety = fixture.get("safety")
    if not isinstance(safety, dict) or safety.get("network") != "forbidden":
        raise ExecutorRunnerError("executor fixture must forbid network access")
    names: set[str] = set()
    for group_name in ("processCases", "shellCases", "treeCases", "terminalCases"):
        for case in require_list(fixture.get(group_name), group_name):
            if not isinstance(case, dict) or not isinstance(case.get("name"), str):
                raise ExecutorRunnerError(f"{group_name} contains an invalid case")
            if case["name"] in names:
                raise ExecutorRunnerError(f"duplicate executor case: {case['name']}")
            names.add(case["name"])
    shell_scripts = {
        "printf shell-stdout; printf shell-stderr >&2; exit 7",
        "echo shell-stdout & echo shell-stderr 1>&2 & exit /b 7",
    }
    dangerous = re.compile(
        r"(?:curl|wget|nc\b|/dev/tcp|powershell\s+-enc|`|\$\(|;\s*rm\b)", re.IGNORECASE
    )
    for case in require_list(fixture.get("shellCases"), "shellCases"):
        if not isinstance(case, dict) or case.get("script") not in shell_scripts:
            raise ExecutorRunnerError("shell case is not one of the fixed offline scripts")
        script = str(case["script"])
        if dangerous.search(script):
            raise ExecutorRunnerError("shell fixture contains a forbidden network or code token")
    helper_text = helper.read_text(encoding="utf-8")
    if any(
        token in helper_text
        for token in ("socket", "urllib", "requests", "os.system(", "shell=True")
    ):
        raise ExecutorRunnerError("executor helper contains a forbidden network/shell primitive")
    for case in require_list(fixture.get("processCases"), "processCases"):
        if not isinstance(case, dict) or case.get("action") not in {
            "emit",
            "argv",
            "read-stdin",
            "sleep",
            "overflow",
            "env-probe",
        }:
            raise ExecutorRunnerError("process fixture action is not allowlisted")


def platform_cases(fixture: JSONObject, group: str) -> tuple[list[JSONObject], list[str]]:
    """功能：筛选当前平台案例并返回明确 skip 原因列表。

    输入：fixture 和 process/shell/tree/terminal 分组名。
    输出：适用案例及 `skip(platform_unavailable)` 的案例名列表。
    不变量：不把未声明平台的案例当作执行成功。
    失败：分组不是数组或案例结构无效时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    tag = current_platform()
    selected: list[JSONObject] = []
    skipped: list[str] = []
    for raw in require_list(fixture.get(group), group):
        if not isinstance(raw, dict):
            raise ExecutorRunnerError(f"{group} contains a non-object case")
        platforms = raw.get("platforms")
        if platforms is not None and tag not in platforms:
            skipped.append(f"{raw.get('name', '<unknown>')}:skip(platform_unavailable)")
        else:
            selected.append(raw)
    return selected, skipped


def helper_command(helper: Path, action: str, arguments: Sequence[str]) -> list[str]:
    """功能：构造不经过 shell 的固定本机 Python helper argv。

    输入：helper 路径、allowlist action 与字符串参数。
    输出：可直接传给 subprocess 的 argv 列表。
    不变量：argv 边界保留；不会把参数拼成命令字符串。
    失败：action 或参数边界已由语义门禁验证，异常由 subprocess 传播为 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return [sys.executable, str(helper), action, *arguments]


def terminate_owned_process(process: subprocess.Popen[bytes]) -> None:
    """功能：在本机 helper 超时后终止其独立进程组并回收句柄。

    输入：runner 以 start_new_session 创建的 helper 进程。
    输出：进程及普通后代尽力退出，句柄已 wait。
    不变量：只向本 runner 创建的 PID/PGID 发信号，不执行用户字符串。
    失败：进程竞态退出被忽略；无法回收时抛出 Runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if process.poll() is not None:
        return
    try:
        if os.name == "posix":
            os.killpg(process.pid, signal.SIGKILL)
        else:
            process.kill()
    except ProcessLookupError:
        pass
    try:
        process.wait(timeout=2)
    except subprocess.TimeoutExpired as exc:
        raise ExecutorRunnerError("local helper could not be reaped") from exc


def run_local_process_case(case: JSONObject, helper: Path, workspace: Path) -> None:
    """功能：在临时 workspace 执行一个固定 process helper 案例并验证退出/双流/argv。

    输入：Schema 已验证的 process case、helper 和临时 workspace。
    输出：案例通过时无返回值。
    不变量：shell=False、独立会话、stdin/输出均有界；超时不会留下 runner 进程。
    失败：结果与 expected 不符、超预算或子进程无法回收时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    arguments = [str(item) for item in case.get("arguments", [])]
    stdin = case.get("stdin")
    process = subprocess.Popen(
        helper_command(helper, str(case["action"]), arguments),
        cwd=workspace,
        stdin=subprocess.PIPE if stdin is not None else subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        start_new_session=os.name == "posix",
        env={
            key: value
            for key, value in os.environ.items()
            if key in {"PATH", "SystemRoot", "TEMP", "TMP"}
        },
    )
    timeout_ms = int(case["limits"]["timeoutMs"])
    try:
        stdout, stderr = process.communicate(
            None if stdin is None else str(stdin).encode("utf-8"),
            timeout=max(timeout_ms / 1000, 0.05) + 0.2,
        )
        timed_out = False
    except subprocess.TimeoutExpired:
        timed_out = True
        terminate_owned_process(process)
        stdout, stderr = process.communicate()
    expected = case["expected"]
    output_limited = (
        len(stdout) > int(case["limits"]["stdoutBytes"])
        or len(stderr) > int(case["limits"]["stderrBytes"])
        or len(stdout) + len(stderr) > int(case["limits"]["totalOutputBytes"])
    )
    reason = "timeout" if timed_out else "output_limit" if output_limited else "exit"
    if expected.get("terminationReason") != reason:
        raise ExecutorRunnerError(f"local case {case['name']} reason {reason!r} differs")
    if reason == "exit" and process.returncode != expected.get("exitCode"):
        raise ExecutorRunnerError(f"local case {case['name']} exit code differs")
    if expected.get("stdout") is not None and stdout.decode("utf-8") != expected["stdout"]:
        raise ExecutorRunnerError(f"local case {case['name']} stdout differs")
    if expected.get("stderr") is not None and stderr.decode("utf-8") != expected["stderr"]:
        raise ExecutorRunnerError(f"local case {case['name']} stderr differs")
    if expected.get("stdoutJson") is not None:
        try:
            observed = json.loads(stdout.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise ExecutorRunnerError(f"local case {case['name']} argv output is not JSON") from exc
        if observed != expected["stdoutJson"]:
            raise ExecutorRunnerError(f"local case {case['name']} argv boundaries changed")
    if expected.get("stdinEcho") is not None:
        if json.loads(stdout.decode("utf-8")) != expected["stdinEcho"]:
            raise ExecutorRunnerError(f"local case {case['name']} stdin changed")
    if len(stdout) + len(stderr) > 4 * 1024 * 1024:
        raise ExecutorRunnerError(f"local case {case['name']} exceeded runner output budget")


def run_local_shell_case(case: JSONObject, workspace: Path) -> None:
    """功能：用显式固定 POSIX shell 执行 shell fixture，不接受 shell=True 或猜测解释器。

    输入：适用平台的静态 shell case 与临时 workspace。
    输出：stdout/stderr、非零退出和 timeout 断言通过时无返回值。
    不变量：脚本文本来自语义 allowlist，argv 使用 `sh -c <one argument>`。
    失败：shell 不存在、输出/退出结果漂移或命令未结束时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if current_platform() not in {"linux", "macos"}:
        return
    limits = case["limits"]
    process = subprocess.Popen(
        ["sh", "-c", str(case["script"])],
        cwd=workspace,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        start_new_session=True,
        env={
            key: value
            for key, value in os.environ.items()
            if key in {"PATH", "HOME", "TMPDIR", "TMP", "TEMP"}
        },
    )
    try:
        stdout, stderr = process.communicate(timeout=int(limits["timeoutMs"]) / 1000 + 0.2)
    except subprocess.TimeoutExpired as exc:
        terminate_owned_process(process)
        raise ExecutorRunnerError(f"shell case {case['name']} timed out") from exc
    expected = case["expected"]
    if (
        process.returncode != expected.get("exitCode")
        or stdout.decode() != expected.get("stdout")
        or stderr.decode() != expected.get("stderr")
    ):
        raise ExecutorRunnerError(f"shell case {case['name']} result differs")


def run_local_terminal_case(case: JSONObject, helper: Path, workspace: Path) -> str:
    """功能：在 POSIX 真 PTY 上运行 terminal helper，验证 tty 身份、输入和退出清理。

    输入：terminal fixture、helper 和临时 workspace。
    输出：`unix_pty` 能力标签；非 POSIX 平台返回明确 skip 标签。
    不变量：使用 `pty.fork`，绝不把普通 pipe 当作终端；子进程退出后句柄关闭。
    失败：ready/echo/size/SIGINT 轨迹不符或子进程泄漏时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if os.name != "posix":
        return "skip(platform_unavailable)"
    import pty

    pid, master = pty.fork()
    if pid == 0:
        os.chdir(workspace)
        os.execve(sys.executable, helper_command(helper, "terminal", []), os.environ.copy())
    output = bytearray()
    waited = False
    try:
        deadline = time.monotonic() + 3
        while b'"ready": true' not in output and time.monotonic() < deadline:
            try:
                output.extend(os.read(master, 4096))
            except OSError:
                break
        if b'"stdinTty": true' not in output or b'"stdoutTty": true' not in output:
            raise ExecutorRunnerError("terminal helper did not observe a real PTY")
        os.write(master, b"echo:terminal-ok\n")
        os.write(master, b"size\n")
        time.sleep(0.1)
        output.extend(os.read(master, 4096))
        if b"terminal-ok" not in output:
            raise ExecutorRunnerError("terminal write/echo failed")
        os.kill(pid, signal.SIGINT)
        status: int | None = None
        exit_deadline = time.monotonic() + 2
        while time.monotonic() < exit_deadline:
            reaped_pid, candidate_status = os.waitpid(pid, os.WNOHANG)
            if reaped_pid == pid:
                status = candidate_status
                waited = True
                break
            time.sleep(0.01)
        if status is None:
            raise ExecutorRunnerError("terminal helper did not exit after SIGINT")
        while True:
            try:
                chunk = os.read(master, 4096)
            except OSError:
                break
            if not chunk:
                break
            output.extend(chunk)
        if (
            os.waitstatus_to_exitcode(status) != 130
            or b'"interrupted": true' not in output
        ):
            raise ExecutorRunnerError("terminal helper did not exit through SIGINT handling")
        return "unix_pty"
    finally:
        if not waited:
            try:
                os.kill(pid, signal.SIGKILL)
            except OSError:
                pass
            try:
                os.waitpid(pid, 0)
            except OSError:
                pass
        os.close(master)


def run_local_suite(fixture: JSONObject, helper: Path) -> list[str]:
    """功能：在隔离临时根目录运行本机可执行 helper 与真实 PTY 自测。

    输入：已通过 Schema/语义门禁的 fixture 和固定 helper。
    输出：PASS/skip 标签列表。
    不变量：所有路径临时化；结束后递归清理，不接触用户 workspace 或真实凭据。
    失败：任一适用 helper 案例失败时抛出 ExecutorRunnerError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    reports: list[str] = []
    with tempfile.TemporaryDirectory(prefix="qxnm-forge-executor-") as temporary:
        root = Path(temporary)
        workspace = root / "workspace"
        workspace.mkdir()
        local_helper = workspace / helper.name
        shutil.copyfile(helper, local_helper)
        process_cases, skipped = platform_cases(fixture, "processCases")
        for item in skipped:
            reports.append(item)
        for case in process_cases:
            run_local_process_case(case, local_helper, workspace)
            reports.append(f"{case['name']}:PASS")
        shell_cases, skipped = platform_cases(fixture, "shellCases")
        for item in skipped:
            reports.append(item)
        for case in shell_cases:
            run_local_shell_case(case, workspace)
            reports.append(f"{case['name']}:PASS")
        tree_cases, skipped = platform_cases(fixture, "treeCases")
        for item in skipped:
            reports.append(item)
        for case in tree_cases:
            if current_platform() in {"linux", "macos"}:
                # group 模式验证 runner 的回收路径；escape 模式留给 daemon conformance，避免
                # 本地 runner 故意留下逃逸后代。其不可伪造要求在动态探针中检查。
                arguments = list(case["arguments"])
                arguments[2] = "group"
                local_case = dict(case)
                local_case["arguments"] = arguments
                run_local_process_case(local_case, local_helper, workspace)
                marker = workspace / arguments[0]
                if marker.exists():
                    raise ExecutorRunnerError(f"tree case {case['name']} left a marker")
                reports.append(
                    f"{case['name']}:skip(daemon_required_after_cooperative_helper_selftest)"
                )
            else:
                reports.append(f"{case['name']}:skip(platform_unavailable)")
        terminal_cases, skipped = platform_cases(fixture, "terminalCases")
        for item in skipped:
            reports.append(item)
        for case in terminal_cases:
            if case["name"] == "pty_identity_io_resize_signal":
                result = run_local_terminal_case(case, local_helper, workspace)
                reports.append(
                    f"{case['name']}:{result if result.startswith('skip') else 'PASS(helper_pty)'}"
                )
            else:
                reports.append(f"{case['name']}:skip(daemon_required)")
    return reports


def parse_command(value: str) -> list[str]:
    """功能：严格解析可选 daemon JSON argv，不经过 shell 或字符串模板执行。

    输入：命令行 JSON 数组。
    输出：非空 argv 字符串列表。
    不变量：最多 128 项、每项有限长度且不含 NUL；占位符只由 runner 后续替换。
    失败：JSON 类型或边界不合法时抛出 argparse.ArgumentTypeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        parsed = json.loads(value)
    except json.JSONDecodeError as exc:
        raise argparse.ArgumentTypeError("daemon command must be JSON argv") from exc
    if (
        not isinstance(parsed, list)
        or not parsed
        or len(parsed) > 128
        or not all(
            isinstance(item, str) and item and len(item) <= 4096 and "\x00" not in item
            for item in parsed
        )
    ):
        raise argparse.ArgumentTypeError("daemon command argv is invalid")
    return parsed


def render_command(
    template: Sequence[str], workspace: Path, state_root: Path, session_id: str
) -> list[str]:
    """功能：只替换 runner 管理的路径/ID 占位符并保持 argv 边界。

    输入：已严格解析的 argv 模板、临时 workspace/state root 与 Session ID。
    输出：不经过 shell 的具体 argv。
    不变量：只支持五个固定占位符，替换值不重新解析。
    失败：替换后仍含花括号时抛出 runner 错误，避免隐式模板语义。
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
            raise ExecutorRunnerError("daemon command contains an unknown placeholder")
        rendered.append(value)
    return rendered


def initialize_request(request_id: str, *, terminal: bool) -> JSONObject:
    """功能：构造 process 或 terminal 黑盒探针的 initialize 请求。

    输入：唯一 request ID 和是否协商 terminal notifications。
    输出：协议 0.1、event replay 与交互审批能力对象。
    不变量：terminalEvents 只在 terminal 探针出现，旧 process 实现不会因未知字段失败。
    失败：纯内存构造，无 I/O。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    capabilities: JSONObject = {"eventReplay": True, "interactiveApprovals": True}
    if terminal:
        capabilities["terminalEvents"] = True
    return {
        "jsonrpc": "2.0",
        "id": request_id,
        "method": "initialize",
        "params": {
            "protocolVersions": ["0.1"],
            "client": {"name": "executor-conformance", "version": "0.1.0"},
            "capabilities": capabilities,
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
    """功能：等待一个 request ID 的唯一响应，并按策略保留并发事件。

    输入：DaemonProcess、request ID、trace 容器、正超时和事件许可。
    输出：包含 result 的成功响应。
    不变量：不接受重复/错误 ID；不解析 error.message。
    失败：超时、错误响应或意外 notification 时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    deadline = time.monotonic() + timeout
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ExecutorRunnerError(f"response {request_id!r} timed out")
        try:
            frame = process.next_frame(remaining)
        except queue.Empty as exc:
            raise ExecutorRunnerError(f"response {request_id!r} timed out") from exc
        frames.append(frame)
        if frame.get("id") == request_id:
            if "result" not in frame or "error" in frame:
                error = frame.get("error")
                kind = error.get("details", {}).get("kind") if isinstance(error, dict) else None
                raise ExecutorRunnerError(f"request {request_id!r} failed with kind {kind!r}")
            return frame
        if frame.get("method") in {"event", "terminal/event"} and allow_events:
            continue
        raise ExecutorRunnerError(f"request {request_id!r} received an unexpected frame")


def faux_configure_request(case: JSONObject, tool_arguments: JSONObject) -> JSONObject:
    """功能：构造只调用一个 process/shell helper 的 deterministic faux scenario。

    输入：机器案例和已渲染安全 tool arguments。
    输出：faux/configure JSON-RPC 请求。
    不变量：场景只含一个固定工具调用和一个文本 continuation，不含网络或真实模型。
    失败：纯内存构造，无 I/O。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    tool_name = "shell.exec" if "shell" in case else "process.exec"
    name = "executor-" + str(case["name"])
    return {
        "jsonrpc": "2.0",
        "id": "executor-configure-1",
        "method": "faux/configure",
        "params": {
            "sessionId": "executor-conformance-session",
            "scenario": {
                "schemaVersion": "0.1",
                "name": name,
                "seed": 20260714,
                "steps": [
                    {
                        "type": "tool_call",
                        "toolCallId": "executor-call-1",
                        "name": tool_name,
                        "arguments": tool_arguments,
                    }
                ],
                "continuations": [
                    {"steps": [{"type": "text", "text": "executor probe completed"}]}
                ],
            },
        },
    }


def run_start_request() -> JSONObject:
    """功能：构造 executor fixture 共用的 faux run/start 请求。

    输入：无。
    输出：固定 Session、用户输入和 faux Provider 的 JSON-RPC 请求。
    不变量：不携带 executable、secret 或主机路径。
    失败：纯内存构造，无 I/O。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": "executor-start-1",
        "method": "run/start",
        "params": {
            "sessionId": "executor-conformance-session",
            "input": {
                "role": "user",
                "content": [{"type": "text", "text": "Run the offline executor helper."}],
            },
            "provider": {"id": "faux", "modelId": "faux-v1"},
        },
    }


def approval_request(run_id: str, approval_id: str) -> JSONObject:
    """功能：构造只允许当前 executor operation 一次的 approval/respond。

    输入：已响应 run ID 和 approval.requested 中的 opaque approval ID。
    输出：allow_once JSON-RPC 请求。
    不变量：不扩展到 allow_always，不修改 tool arguments。
    失败：纯内存构造，无 I/O。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": "executor-approval-1",
        "method": "approval/respond",
        "params": {
            "sessionId": "executor-conformance-session",
            "runId": run_id,
            "approvalId": approval_id,
            "decision": {"choice": "allow_once"},
        },
    }


def tool_arguments(case: JSONObject, helper: Path) -> JSONObject:
    """功能：把抽象 fixture action 渲染成 process.exec 或 shell.exec 的安全参数。

    输入：Schema 已验证案例和临时 workspace helper。
    输出：不经过 shell 的 process args，或显式固定 shell script。
    不变量：process executable 固定为当前 Python；helper 位于 workspace；限制取自 fixture。
    失败：案例类型无效时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    limits = case["limits"]
    common: JSONObject = {
        "cwd": ".",
        "timeoutMs": limits["timeoutMs"],
        "outputLimitBytes": limits["totalOutputBytes"],
    }
    if "shell" in case:
        return {**common, "shell": case["shell"], "script": case["script"]}
    result: JSONObject = {
        **common,
        "executable": sys.executable,
        "args": [helper.name, case["action"], *case.get("arguments", [])],
    }
    if "stdin" in case:
        result["stdin"] = {"type": "text", "text": case["stdin"]}
    return result


def execution_result_from_frames(
    frames: Sequence[JSONObject],
) -> tuple[JSONObject, JSONObject]:
    """功能：从完整 Agent trace 提取唯一 tool.completed ToolResult 与 execution。

    输入：daemon 响应和 notification 帧。
    输出：ToolResult 对象和其结构化 execution 对象。
    不变量：文本渲染不能替代 execution；重复 tool.completed 被拒绝。
    失败：结果缺失、重复或未提供 execution 时抛出 runner 错误。
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
        raise ExecutorRunnerError("executor trace must contain one tool.completed result")
    execution = results[0].get("execution")
    if not isinstance(execution, dict):
        raise ExecutorRunnerError("tool result omitted structured execution")
    return results[0], execution


def validate_execution(case: JSONObject, tool_result: JSONObject, execution: JSONObject) -> None:
    """功能：核对 normalized execution 的终止、双流、计数、错误和 containment 不变量。

    输入：机器案例、ToolResult 和 execution 对象。
    输出：与 fixture 完全一致时无返回值。
    不变量：captured+omitted=total，truncated 与 omitted 一致，ToolResult 原因与 execution 相同。
    失败：任一字段漂移、secret 可见或 tree 等级不足时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    expected = case["expected"]
    if execution.get("terminationReason") != expected.get("terminationReason"):
        raise ExecutorRunnerError(f"case {case['name']} termination reason differs")
    if "exitCode" in expected and execution.get("exitCode") != expected["exitCode"]:
        raise ExecutorRunnerError(f"case {case['name']} exit code differs")
    if tool_result.get("terminationReason") != execution.get("terminationReason"):
        raise ExecutorRunnerError(f"case {case['name']} ToolResult reason differs")
    if "isError" in expected and tool_result.get("isError") is not expected["isError"]:
        raise ExecutorRunnerError(f"case {case['name']} isError differs")
    for stream_name in ("stdout", "stderr"):
        stream = execution.get(stream_name)
        if not isinstance(stream, dict):
            raise ExecutorRunnerError(f"case {case['name']} omitted {stream_name}")
        captured = stream.get("capturedBytes")
        total = stream.get("totalBytes")
        omitted = stream.get("omittedBytes")
        if not all(isinstance(value, int) and value >= 0 for value in (captured, total, omitted)):
            raise ExecutorRunnerError(f"case {case['name']} stream counts are invalid")
        if captured + omitted != total or bool(stream.get("truncated")) != (omitted > 0):
            raise ExecutorRunnerError(f"case {case['name']} stream count invariant failed")
    if expected.get("stdout") is not None and execution["stdout"].get("text") != expected["stdout"]:
        raise ExecutorRunnerError(f"case {case['name']} stdout identity differs")
    if expected.get("stderr") is not None and execution["stderr"].get("text") != expected["stderr"]:
        raise ExecutorRunnerError(f"case {case['name']} stderr identity differs")
    if expected.get("stdoutJson") is not None:
        try:
            observed = json.loads(execution["stdout"].get("text", ""))
        except json.JSONDecodeError as exc:
            raise ExecutorRunnerError(f"case {case['name']} stdout is not JSON") from exc
        if observed != expected["stdoutJson"]:
            raise ExecutorRunnerError(f"case {case['name']} argv boundaries changed")
    if expected.get("stdinEcho") is not None:
        if json.loads(execution["stdout"].get("text", "")) != expected["stdinEcho"]:
            raise ExecutorRunnerError(f"case {case['name']} stdin differs")
    if expected.get("secretAbsent") and execution["stdout"].get("text") != "absent\n":
        raise ExecutorRunnerError("executor child inherited the secret canary")
    if expected.get("truncated") and not (
        execution["stdout"].get("truncated") or execution["stderr"].get("truncated")
    ):
        raise ExecutorRunnerError(f"case {case['name']} did not report truncation")
    required = expected.get("requiredContainment")
    if required is not None and execution.get("containment") not in {
        required,
        "os_isolation",
    }:
        raise ExecutorRunnerError(f"case {case['name']} containment is insufficient")


def scan_canary(root: Path, canary: str) -> None:
    """功能：扫描临时 workspace/state regular files，拒绝 secret canary 持久化。

    输入：runner 独占临时根和随机 canary。
    输出：不存在泄漏时无返回值。
    不变量：不跟随符号链接，单文件和总读取量有界。
    失败：发现 canary、符号链接或超出安全扫描预算时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    total = 0
    needle = canary.encode("utf-8")
    for path in root.rglob("*"):
        if path.is_symlink():
            raise ExecutorRunnerError("executor probe created a symbolic link")
        if not path.is_file():
            continue
        size = path.stat().st_size
        total += size
        if size > 8 * 1024 * 1024 or total > 32 * 1024 * 1024:
            raise ExecutorRunnerError("executor probe tree exceeds scan budget")
        if needle in path.read_bytes():
            raise ExecutorRunnerError("secret canary leaked into executor probe files")


def run_daemon_tool_case(
    template: Sequence[str],
    case: JSONObject,
    helper_source: Path,
    *,
    timeout: float,
) -> str:
    """功能：通过 faux tool/approval 流驱动一个原生 daemon process/shell/tree 案例。

    输入：安全 daemon argv 模板、机器案例、helper 与总超时。
    输出：通过标签；平台不适用返回明确 skip 标签。
    不变量：每案独立临时根、随机 canary、一次批准；不接触真实项目或 Provider。
    失败：协议、执行结果、tree cleanup 或泄漏不符时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    platforms = case.get("platforms")
    if isinstance(platforms, list) and current_platform() not in platforms:
        return "skip(platform_unavailable)"
    import provider_runner
    import runner

    canary = "executor-canary-" + secrets.token_urlsafe(32)
    with tempfile.TemporaryDirectory(prefix="executor-daemon-") as temporary:
        root = Path(temporary)
        workspace = root / "workspace"
        state_root = root / "state"
        workspace.mkdir()
        state_root.mkdir()
        helper = workspace / helper_source.name
        shutil.copyfile(helper_source, helper)
        command = render_command(template, workspace, state_root, "executor-conformance-session")
        process = runner.DaemonProcess(
            command,
            timeout=timeout,
            max_frame_bytes=1_048_576,
            extra_env={
                "QXNM_FORGE_WORKSPACE": str(workspace),
                "QXNM_FORGE_SESSION_ROOT": str(state_root / "sessions"),
                "QXNM_FORGE_CONFORMANCE": "1",
                "QXNM_FORGE_LIVE_TESTS": "0",
                "QXNM_FORGE_EXECUTOR_SECRET_CANARY": canary,
                "HTTP_PROXY": "http://127.0.0.1:9",
                "HTTPS_PROXY": "http://127.0.0.1:9",
                "ALL_PROXY": "http://127.0.0.1:9",
                "NO_PROXY": "",
            },
            removed_env=(
                *provider_runner.CREDENTIAL_ENV,
                *provider_runner.ENDPOINT_ENV,
                *provider_runner.PROXY_ENV,
            ),
        )
        frames: list[JSONObject] = []
        try:
            init = initialize_request("executor-init-1", terminal=False)
            process.send_request(init)
            initialized = await_response(
                process, "executor-init-1", frames, timeout=timeout, allow_events=False
            )
            capabilities = initialized["result"].get("capabilities")
            tools = capabilities.get("tools") if isinstance(capabilities, dict) else None
            tool_name = "shell.exec" if "shell" in case else "process.exec"
            if not isinstance(tools, list) or tool_name not in tools:
                return "skip(capability_unavailable)"
            configure = faux_configure_request(case, tool_arguments(case, helper))
            process.send_request(configure)
            await_response(
                process,
                "executor-configure-1",
                frames,
                timeout=timeout,
                allow_events=False,
            )
            start = run_start_request()
            process.send_request(start)
            start_response = await_response(
                process, "executor-start-1", frames, timeout=timeout, allow_events=False
            )
            run_id = start_response["result"].get("runId")
            if not isinstance(run_id, str) or not run_id:
                raise ExecutorRunnerError("executor run/start omitted runId")
            approved = False
            cancelled = False
            terminal = False
            deadline = time.monotonic() + timeout
            while not terminal:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise ExecutorRunnerError(f"case {case['name']} daemon trace timed out")
                try:
                    frame = process.next_frame(remaining)
                except queue.Empty as exc:
                    raise ExecutorRunnerError(
                        f"case {case['name']} daemon trace timed out"
                    ) from exc
                frames.append(frame)
                if frame.get("method") != "event":
                    raise ExecutorRunnerError("executor daemon emitted unsolicited response")
                params = frame.get("params")
                event_type = params.get("type") if isinstance(params, dict) else None
                if event_type == "approval.requested":
                    if approved:
                        raise ExecutorRunnerError("executor case requested approval twice")
                    data = params.get("data")
                    approval = data.get("approval") if isinstance(data, dict) else None
                    approval_id = approval.get("approvalId") if isinstance(approval, dict) else None
                    if not isinstance(approval_id, str):
                        raise ExecutorRunnerError("approval.requested omitted approvalId")
                    request = approval_request(run_id, approval_id)
                    process.send_request(request)
                    await_response(
                        process,
                        "executor-approval-1",
                        frames,
                        timeout=timeout,
                        allow_events=True,
                    )
                    approved = True
                if (
                    event_type == "tool.started"
                    and case.get("control") == "cancel_on_started"
                    and not cancelled
                ):
                    cancel = {
                        "jsonrpc": "2.0",
                        "id": "executor-cancel-1",
                        "method": "run/cancel",
                        "params": {
                            "sessionId": "executor-conformance-session",
                            "runId": run_id,
                        },
                    }
                    process.send_request(cancel)
                    await_response(
                        process,
                        "executor-cancel-1",
                        frames,
                        timeout=timeout,
                        allow_events=True,
                    )
                    cancelled = True
                    terminal = any(
                        isinstance(item.get("params"), dict)
                        and item["params"].get("type") in runner.TERMINAL_EVENTS
                        for item in frames
                    )
                if event_type in runner.TERMINAL_EVENTS:
                    terminal = True
            if not approved:
                raise ExecutorRunnerError("dangerous executor tool ran without explicit approval")
            tool_result, execution = execution_result_from_frames(frames)
            validate_execution(case, tool_result, execution)
            settle_ms = int(case.get("settleMs", 0))
            if settle_ms:
                time.sleep(settle_ms / 1000)
            expected = case["expected"]
            if expected.get("markerMustNotExist"):
                marker = workspace / str(case["arguments"][0])
                if marker.exists():
                    raise ExecutorRunnerError(f"case {case['name']} left an escaped descendant")
        finally:
            process.close()
        serialized = json.dumps(frames, ensure_ascii=False)
        if canary in serialized or canary in process.stderr_text(limit=131_072):
            raise ExecutorRunnerError("secret canary leaked into daemon protocol or stderr")
        scan_canary(workspace, canary)
        scan_canary(state_root, canary)
    return "PASS"


def next_terminal_event(
    process: Any,
    frames: list[JSONObject],
    terminal_id: str,
    *,
    timeout: float,
) -> JSONObject:
    """功能：等待并验证一个指定 terminal 的 `terminal/event` notification。

    输入：DaemonProcess、trace、terminal ID 和正超时。
    输出：terminal event params 对象。
    不变量：不把 Agent event、响应或另一 terminal 的输出混入当前轨迹。
    失败：超时、frame 形状或 terminal identity 漂移时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        frame = process.next_frame(timeout)
    except queue.Empty as exc:
        raise ExecutorRunnerError("terminal event timed out") from exc
    frames.append(frame)
    params = frame.get("params")
    if (
        frame.get("method") != "terminal/event"
        or not isinstance(params, dict)
        or params.get("terminalId") != terminal_id
    ):
        raise ExecutorRunnerError("terminal emitted an invalid or unrelated frame")
    return params


def drain_terminal_events_until_quiet(
    process: Any,
    frames: list[JSONObject],
    terminal_id: str,
    latest_seq: int,
    *,
    total_timeout: float,
    quiet_timeout: float = 0.1,
) -> int:
    """功能：在 attach 前排空已经产生的 live output，隔离随后 response-delimited replay。

    输入：daemon、trace、terminal ID、最后 live seq，以及正总/静默超时。
    输出：排空后最后一个严格递增 live seq；无待处理事件时保持原值。
    不变量：只接受当前 terminal 的 output/truncated；连续输出超过总预算时失败，不把它误认成 replay。
    失败：超时参数非法、非 terminal 帧、身份/seq 漂移、提前 exited/closed 或无法静默时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if total_timeout <= 0 or quiet_timeout <= 0:
        raise ExecutorRunnerError("terminal quiet-drain timeout is invalid")
    quiet_timeout = min(quiet_timeout, total_timeout)
    deadline = time.monotonic() + total_timeout
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ExecutorRunnerError("terminal live output did not quiesce before attach")
        try:
            event = next_terminal_event(
                process,
                frames,
                terminal_id,
                timeout=min(quiet_timeout, remaining),
            )
        except ExecutorRunnerError as exc:
            if isinstance(exc.__cause__, queue.Empty):
                return latest_seq
            raise
        seq = event.get("seq")
        if (
            not isinstance(seq, int)
            or isinstance(seq, bool)
            or seq <= latest_seq
            or event.get("type") not in {"terminal.output", "terminal.truncated"}
        ):
            raise ExecutorRunnerError(
                "terminal emitted an invalid live transition before attach"
            )
        latest_seq = seq


def terminal_output_text(frames: Sequence[JSONObject], terminal_id: str) -> str:
    """功能：从已收集 trace 汇总指定 terminal 的全部 PTY 输出文本。

    输入：响应/notification trace 与目标 terminal ID。
    输出：按 trace 顺序连接的 `terminal.output.data.data`。
    不变量：忽略其他 terminal、Agent event、响应和非输出事件。
    失败：本函数只读取已验证帧，不抛出动态内容错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    output: list[str] = []
    for frame in frames:
        params = frame.get("params")
        if (
            frame.get("method") != "terminal/event"
            or not isinstance(params, dict)
            or params.get("terminalId") != terminal_id
            or params.get("type") != "terminal.output"
        ):
            continue
        data = params.get("data")
        text = data.get("data") if isinstance(data, dict) else None
        if isinstance(text, str):
            output.append(text)
    return "".join(output)


def terminal_rpc_frame(
    process: Any,
    frames: list[JSONObject],
    request_id: str,
    method: str,
    params: JSONObject,
    *,
    timeout: float,
    allow_events: bool,
) -> JSONObject:
    """功能：发送 terminal RPC 并返回未经结果/错误分支折叠的唯一响应帧。

    输入：daemon、trace、唯一 ID、terminal method/params、超时及并发事件策略。
    输出：恰含 `result` 或 `error` 之一的匹配响应。
    不变量：等待期间只可放行同一连接的 `terminal/event`；Agent event 和异 ID 响应失败关闭。
    失败：超时、重复分支、意外帧或 daemon 退出时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    request = {"jsonrpc": "2.0", "id": request_id, "method": method, "params": params}
    process.send_request(request)
    deadline = time.monotonic() + timeout
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ExecutorRunnerError(f"terminal response {request_id!r} timed out")
        try:
            frame = process.next_frame(remaining)
        except queue.Empty as exc:
            raise ExecutorRunnerError(f"terminal response {request_id!r} timed out") from exc
        frames.append(frame)
        if frame.get("id") == request_id:
            has_result = "result" in frame
            has_error = "error" in frame
            if has_result == has_error:
                raise ExecutorRunnerError(
                    f"terminal response {request_id!r} must contain exactly one outcome"
                )
            return frame
        if allow_events and frame.get("method") == "terminal/event":
            continue
        raise ExecutorRunnerError(
            f"terminal response {request_id!r} received an unexpected frame"
        )


def terminal_rpc_error(
    process: Any,
    frames: list[JSONObject],
    request_id: str,
    method: str,
    params: JSONObject,
    *,
    timeout: float,
    allow_events: bool,
    code: int,
    kind: str,
    retryable: bool,
) -> JSONObject:
    """功能：发送预期失败的 terminal RPC 并核对稳定结构化错误身份。

    输入：terminal RPC、事件策略以及期望 code/kind/retryable 三元组。
    输出：匹配的 error 对象，供调用者追加无副作用断言。
    不变量：不解析 display-only message，不把任意失败当作安全拒绝。
    失败：请求成功或错误身份漂移时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    frame = terminal_rpc_frame(
        process,
        frames,
        request_id,
        method,
        params,
        timeout=timeout,
        allow_events=allow_events,
    )
    error = frame.get("error")
    details = error.get("details") if isinstance(error, dict) else None
    if (
        not isinstance(error, dict)
        or error.get("code") != code
        or error.get("retryable") is not retryable
        or not isinstance(details, dict)
        or details.get("kind") != kind
    ):
        raise ExecutorRunnerError(
            f"terminal request {request_id!r} returned another structured error"
        )
    return error


def drive_terminal_backpressure(
    process: Any,
    frames: list[JSONObject],
    terminal_id: str,
    attachment_id: str,
    input_seq: int,
    *,
    action_index: int,
    timeout: float,
) -> tuple[int, str]:
    """功能：用无换行 bounded burst 填满 PTY 输入队列并证明原子 backpressure。

    输入：当前 terminal ownership、最后已接受 inputSeq、action 序号与超时。
    输出：最后已接受 inputSeq，以及被拒绝块独有的可见 sentinel。
    不变量：每块恰为 65536 个 ASCII 字节、最多八块；错误必须为 retryable -32011，失败块不推进 seq。
    失败：部分/错误成功响应、错误身份漂移或八块内无 backpressure 时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    for attempt in range(1, 9):
        next_seq = input_seq + 1
        sentinel = f"qxnm-forge-backpressure-{action_index}-{attempt}-{secrets.token_hex(8)}"
        data = sentinel + ("x" * (65_536 - len(sentinel)))
        frame = terminal_rpc_frame(
            process,
            frames,
            f"terminal-backpressure-{action_index}-{attempt}",
            "terminal/write",
            {
                "sessionId": "terminal-conformance-session",
                "terminalId": terminal_id,
                "attachmentId": attachment_id,
                "inputSeq": next_seq,
                "data": data,
            },
            timeout=timeout,
            allow_events=True,
        )
        result = frame.get("result")
        if isinstance(result, dict):
            if (
                result.get("accepted") is not True
                or result.get("inputSeq") != next_seq
                or result.get("acceptedBytes") != len(data)
            ):
                raise ExecutorRunnerError(
                    "terminal backpressure probe observed a partial success"
                )
            input_seq = next_seq
            continue
        error = frame.get("error")
        details = error.get("details") if isinstance(error, dict) else None
        if (
            not isinstance(error, dict)
            or error.get("code") != -32011
            or error.get("retryable") is not True
            or not isinstance(details, dict)
            or details.get("kind") != "terminal_backpressure"
        ):
            raise ExecutorRunnerError(
                "terminal input pressure returned another structured error"
            )
        return input_seq, sentinel
    raise ExecutorRunnerError("terminal input queue did not apply bounded backpressure")


def validate_terminal_trace(
    frames: Sequence[JSONObject],
    validator: Any,
    *,
    session_id: str,
    terminal_id: str,
    replay_windows: Sequence[tuple[int, int, int, int]],
    max_event_bytes: int,
    output_chunk_bytes: int,
    require_closed: bool,
    wire_size_for: Any,
) -> None:
    """功能：验证 terminal 全轨迹的 Schema、大小、replay 分段和最终生命周期顺序。

    输入：完整响应/事件 trace、公共 validator、terminal 身份、replay frame 窗口、协商上限及实际 wire-size 查询器。
    输出：所有 live 事件严格递增且满足 output/truncated→exited→closed 时无返回值。
    不变量：replay 仅在登记的 response 后窗口允许 seq 回退；closed 是 live 终态且后面无事件。
    失败：Schema、身份、byteCount、event 大小、replay 边界或生命周期不符时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    replay_by_index: dict[int, tuple[int, int]] = {}
    for start, end, after_seq, through_seq in replay_windows:
        if not 0 <= start <= end <= len(frames) or through_seq <= after_seq:
            raise ExecutorRunnerError("terminal replay window bounds are invalid")
        previous = after_seq
        observed = False
        for frame_index in range(start, end):
            frame = frames[frame_index]
            if frame.get("method") != "terminal/event":
                raise ExecutorRunnerError("terminal replay window contains a non-event frame")
            params = frame.get("params")
            seq = params.get("seq") if isinstance(params, dict) else None
            if (
                not isinstance(seq, int)
                or isinstance(seq, bool)
                or seq <= previous
                or seq > through_seq
            ):
                raise ExecutorRunnerError("terminal replay sequence is not a strict bounded suffix")
            if frame_index in replay_by_index:
                raise ExecutorRunnerError("terminal replay windows overlap")
            replay_by_index[frame_index] = (after_seq, through_seq)
            previous = seq
            observed = True
        if not observed or previous != through_seq:
            raise ExecutorRunnerError("terminal replay did not reach replayThroughSeq")

    latest_live_seq = 0
    original_output_by_seq: dict[int, JSONObject] = {}
    exited = False
    closed = False
    terminal_events = 0
    for frame_index, frame in enumerate(frames):
        if frame.get("method") != "terminal/event":
            continue
        terminal_events += 1
        try:
            errors = sorted(
                validator.iter_errors(frame), key=lambda item: tuple(item.absolute_path)
            )
        except Exception as exc:  # pragma: no cover - malformed validator is a gate failure
            raise ExecutorRunnerError("terminal notification validator failed") from exc
        if errors:
            raise ExecutorRunnerError(
                "terminal notification schema violation at "
                f"{list(errors[0].absolute_path)}"
            )
        params = frame["params"]
        if params.get("sessionId") != session_id or params.get("terminalId") != terminal_id:
            raise ExecutorRunnerError("terminal event identity crossed session or terminal bounds")
        canonical_size = len(
            json.dumps(frame, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        )
        try:
            wire_size = wire_size_for(frame)
        except Exception as exc:
            raise ExecutorRunnerError("terminal notification wire size is unavailable") from exc
        if (
            not isinstance(wire_size, int)
            or isinstance(wire_size, bool)
            or wire_size < canonical_size
            or wire_size > max_event_bytes
        ):
            raise ExecutorRunnerError("terminal notification exceeds advertised maxEventBytes")
        event_type = params["type"]
        data = params["data"]
        if event_type == "terminal.output":
            encoded = data["data"].encode("utf-8")
            if data["byteCount"] != len(encoded):
                raise ExecutorRunnerError("fixture PTY output byteCount differs from UTF-8 bytes")
            if data["byteCount"] > output_chunk_bytes:
                raise ExecutorRunnerError("terminal output exceeds requested eventBytes chunk")
        if frame_index in replay_by_index:
            if event_type != "terminal.output":
                raise ExecutorRunnerError("fixture replay contains a non-output transition")
            original = original_output_by_seq.get(params["seq"])
            if original is None or original.get("params") != params:
                raise ExecutorRunnerError(
                    "terminal replay differs from its previously delivered retained output"
                )
            continue
        seq = params["seq"]
        if seq <= latest_live_seq:
            raise ExecutorRunnerError("live terminal event sequence regressed or repeated")
        latest_live_seq = seq
        if closed:
            raise ExecutorRunnerError("terminal emitted an event after terminal.closed")
        if event_type in {"terminal.output", "terminal.truncated"}:
            if exited:
                raise ExecutorRunnerError("terminal emitted output after terminal.exited")
            if event_type == "terminal.output":
                original_output_by_seq[seq] = frame
        elif event_type == "terminal.exited":
            if exited:
                raise ExecutorRunnerError("terminal emitted terminal.exited more than once")
            exited = True
        elif event_type == "terminal.closed":
            if not exited:
                raise ExecutorRunnerError("terminal.closed preceded terminal.exited")
            closed = True
    if terminal_events == 0:
        raise ExecutorRunnerError("terminal trace contains no notifications")
    if require_closed and (not exited or not closed):
        raise ExecutorRunnerError("terminal trace omitted exited or closed cleanup")


def terminal_rpc(
    process: Any,
    frames: list[JSONObject],
    request_id: str,
    method: str,
    params: JSONObject,
    *,
    timeout: float,
    allow_events: bool = True,
) -> JSONObject:
    """功能：发送一个 terminal state mutation 并取得 success result。

    输入：daemon、trace、request ID、terminal method/params 和超时策略。
    输出：方法专属 result 对象。
    不变量：request ID 唯一；open/attach 的 response-before-event 可用 allow_events=false 强制。
    失败：错误响应、超时或 result 非对象时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    response = terminal_rpc_frame(
        process,
        frames,
        request_id,
        method,
        params,
        timeout=timeout,
        allow_events=allow_events,
    )
    result = response.get("result")
    if not isinstance(result, dict):
        raise ExecutorRunnerError(f"terminal method {method} returned a non-object result")
    return result


def run_daemon_terminal_case(
    template: Sequence[str],
    case: JSONObject,
    helper_source: Path,
    *,
    timeout: float,
) -> str:
    """功能：用真实 terminal RPC 驱动 PTY identity、I/O、resize、signal、attach 与 close。

    输入：daemon argv 模板、terminal 机器案例、helper 和总超时。
    输出：PASS，或平台/能力不可用的明确 skip 标签。
    不变量：ptyKind 不接受 pipe；open/attach response 领先输出；attachment mutation 使用当前 token。
    失败：ownership、replay、backpressure、cleanup 或终端身份不符时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    platforms = case.get("platforms")
    if isinstance(platforms, list) and current_platform() not in platforms:
        return "skip(platform_unavailable)"
    import provider_runner
    import runner

    canary = "terminal-canary-" + secrets.token_urlsafe(32)
    with tempfile.TemporaryDirectory(prefix="terminal-daemon-") as temporary:
        root = Path(temporary)
        workspace = root / "workspace"
        state_root = root / "state"
        workspace.mkdir()
        state_root.mkdir()
        helper = workspace / helper_source.name
        shutil.copyfile(helper_source, helper)
        command = render_command(template, workspace, state_root, "terminal-conformance-session")
        process = runner.DaemonProcess(
            command,
            timeout=timeout,
            max_frame_bytes=1_048_576,
            extra_env={
                "QXNM_FORGE_WORKSPACE": str(workspace),
                "QXNM_FORGE_SESSION_ROOT": str(state_root / "sessions"),
                "QXNM_FORGE_CONFORMANCE": "1",
                "QXNM_FORGE_TERMINAL_CONFORMANCE_POLICY": "fixture-only",
                "QXNM_FORGE_LIVE_TESTS": "0",
                "QXNM_FORGE_EXECUTOR_SECRET_CANARY": canary,
                "HTTP_PROXY": "http://127.0.0.1:9",
                "HTTPS_PROXY": "http://127.0.0.1:9",
                "ALL_PROXY": "http://127.0.0.1:9",
                "NO_PROXY": "",
            },
            removed_env=(
                *provider_runner.CREDENTIAL_ENV,
                *provider_runner.ENDPOINT_ENV,
                *provider_runner.PROXY_ENV,
            ),
        )
        frames: list[JSONObject] = []
        try:
            init = initialize_request("terminal-init-1", terminal=True)
            process.send_request(init)
            initialized = await_response(
                process, "terminal-init-1", frames, timeout=timeout, allow_events=False
            )
            capabilities = initialized["result"].get("capabilities")
            methods = capabilities.get("methods") if isinstance(capabilities, dict) else None
            required_methods = {
                "terminal/open",
                "terminal/write",
                "terminal/resize",
                "terminal/signal",
                "terminal/attach",
                "terminal/close",
            }
            if not isinstance(methods, list):
                raise ExecutorRunnerError("initialize omitted the methods capability array")
            event_types = (
                capabilities.get("terminalEventTypes") if isinstance(capabilities, dict) else None
            )
            advertised_methods = required_methods.intersection(methods)
            advertised_events = set(event_types) if isinstance(event_types, list) else set()
            if not advertised_methods and not advertised_events:
                return "skip(capability_unavailable)"
            required_events = {
                "terminal.output",
                "terminal.truncated",
                "terminal.exited",
                "terminal.closed",
            }
            if advertised_methods != required_methods or advertised_events != required_events:
                raise ExecutorRunnerError(
                    "terminal capability is partially or inconsistently advertised"
                )
            initialize_limits = initialized["result"].get("limits")
            max_event_bytes = (
                initialize_limits.get("maxEventBytes")
                if isinstance(initialize_limits, dict)
                else None
            )
            max_frame_bytes = (
                initialize_limits.get("maxFrameBytes")
                if isinstance(initialize_limits, dict)
                else None
            )
            if (
                not isinstance(max_event_bytes, int)
                or isinstance(max_event_bytes, bool)
                or max_event_bytes <= 0
                or not isinstance(max_frame_bytes, int)
                or isinstance(max_frame_bytes, bool)
                or max_frame_bytes <= 0
                or max_event_bytes > max_frame_bytes
            ):
                raise ExecutorRunnerError("initialize advertised invalid frame/event limits")
            notification_validator = terminal_notification_validator(ROOT / "SPEC/schemas")
            initial_size = case["initialSize"]
            opened = terminal_rpc(
                process,
                frames,
                "terminal-open-1",
                "terminal/open",
                {
                    "sessionId": "terminal-conformance-session",
                    "executable": sys.executable,
                    "args": [helper.name, "terminal"],
                    "cwd": ".",
                    "environment": [],
                    "size": initial_size,
                    "limits": {
                        "lifetimeMs": 10000,
                        "idleTimeoutMs": 5000,
                        "retentionBytes": 65536,
                        "inputBufferBytes": 65536,
                        "eventBytes": 16384,
                    },
                    "disconnectPolicy": "terminate",
                },
                timeout=timeout,
                allow_events=False,
            )
            terminal_id = opened.get("terminalId")
            attachment_id = opened.get("attachmentId")
            pty_kind = opened.get("ptyKind")
            if (
                not isinstance(terminal_id, str)
                or not terminal_id
                or not isinstance(attachment_id, str)
                or not attachment_id
            ):
                raise ExecutorRunnerError("terminal/open omitted ownership IDs")
            expected_pty_kind = (
                "windows_conpty" if current_platform() == "windows" else "unix_pty"
            )
            if pty_kind != expected_pty_kind:
                raise ExecutorRunnerError("terminal/open reported the wrong native ptyKind")
            opened_latest = opened.get("latestOutputSeq")
            if (
                opened.get("state") != "running"
                or opened.get("size") != initial_size
                or not isinstance(opened_latest, int)
                or isinstance(opened_latest, bool)
                or opened_latest < 0
            ):
                raise ExecutorRunnerError("terminal/open returned an inconsistent snapshot")
            input_seq = 0
            latest_seq = 0
            current_size = initial_size
            replay_windows: list[tuple[int, int, int, int]] = []
            replay_verified = False
            rejected_sentinels: list[str] = []
            for index, action in enumerate(case["actions"], start=1):
                action_type = action["type"]
                if action_type == "await":
                    expected_type = action.get("event")
                    contains = action.get("contains")
                    already_observed = any(
                        frame.get("method") == "terminal/event"
                        and isinstance(frame.get("params"), dict)
                        and frame["params"].get("terminalId") == terminal_id
                        and frame["params"].get("type") == expected_type
                        for frame in frames
                    )
                    if already_observed and (
                        contains is None
                        or contains in terminal_output_text(frames, terminal_id)
                    ):
                        continue
                    deadline = time.monotonic() + timeout
                    while True:
                        remaining = deadline - time.monotonic()
                        if remaining <= 0:
                            raise ExecutorRunnerError("terminal expected event timed out")
                        event = next_terminal_event(process, frames, terminal_id, timeout=remaining)
                        seq = event.get("seq")
                        if not isinstance(seq, int) or seq <= latest_seq:
                            raise ExecutorRunnerError("live terminal output sequence regressed")
                        latest_seq = seq
                        if event.get("type") == "terminal.output":
                            data = event.get("data")
                            text = data.get("data") if isinstance(data, dict) else None
                            if text is not None and not isinstance(text, str):
                                raise ExecutorRunnerError(
                                    "terminal output event carried non-text data"
                                )
                        if event.get("type") == expected_type:
                            if contains is None or contains in terminal_output_text(
                                frames, terminal_id
                            ):
                                break
                elif action_type == "write":
                    input_seq += 1
                    result = terminal_rpc(
                        process,
                        frames,
                        f"terminal-write-{index}",
                        "terminal/write",
                        {
                            "sessionId": "terminal-conformance-session",
                            "terminalId": terminal_id,
                            "attachmentId": attachment_id,
                            "inputSeq": input_seq,
                            "data": action["data"],
                        },
                        timeout=timeout,
                    )
                    if (
                        result.get("accepted") is not True
                        or result.get("inputSeq") != input_seq
                        or result.get("acceptedBytes")
                        != len(action["data"].encode("utf-8"))
                    ):
                        raise ExecutorRunnerError(
                            "terminal/write did not atomically accept the input"
                        )
                elif action_type == "resize":
                    result = terminal_rpc(
                        process,
                        frames,
                        f"terminal-resize-{index}",
                        "terminal/resize",
                        {
                            "sessionId": "terminal-conformance-session",
                            "terminalId": terminal_id,
                            "attachmentId": attachment_id,
                            "size": action["size"],
                        },
                        timeout=timeout,
                    )
                    if result.get("accepted") is not True or result.get("size") != action["size"]:
                        raise ExecutorRunnerError("terminal/resize returned another size")
                    current_size = action["size"]
                elif action_type == "signal":
                    result = terminal_rpc(
                        process,
                        frames,
                        f"terminal-signal-{index}",
                        "terminal/signal",
                        {
                            "sessionId": "terminal-conformance-session",
                            "terminalId": terminal_id,
                            "attachmentId": attachment_id,
                            "signal": action["signal"],
                        },
                        timeout=timeout,
                    )
                    if result.get("accepted") is not True:
                        raise ExecutorRunnerError("terminal/signal was not accepted")
                elif action_type == "attach":
                    after_seq = int(action["afterSeq"])
                    latest_seq = drain_terminal_events_until_quiet(
                        process,
                        frames,
                        terminal_id,
                        latest_seq,
                        total_timeout=min(timeout, 1.0),
                    )
                    if action["takeover"]:
                        terminal_rpc_error(
                            process,
                            frames,
                            f"terminal-attach-busy-{index}",
                            "terminal/attach",
                            {
                                "sessionId": "terminal-conformance-session",
                                "terminalId": terminal_id,
                                "afterSeq": after_seq,
                                "takeover": False,
                            },
                            timeout=timeout,
                            allow_events=False,
                            code=-32004,
                            kind="terminal_busy",
                            retryable=True,
                        )
                    previous_attachment = attachment_id
                    result = terminal_rpc(
                        process,
                        frames,
                        f"terminal-attach-{index}",
                        "terminal/attach",
                        {
                            "sessionId": "terminal-conformance-session",
                            "terminalId": terminal_id,
                            "afterSeq": after_seq,
                            "takeover": action["takeover"],
                        },
                        timeout=timeout,
                        allow_events=False,
                    )
                    replacement = result.get("attachmentId")
                    replay_through = result.get("replayThroughSeq")
                    attach_latest = result.get("latestOutputSeq")
                    if (
                        not isinstance(replacement, str)
                        or not replacement
                        or replacement == previous_attachment
                        or result.get("terminalId") != terminal_id
                        or result.get("state") != "running"
                        or result.get("ptyKind") != pty_kind
                        or result.get("size") != current_size
                        or not isinstance(replay_through, int)
                        or isinstance(replay_through, bool)
                        or not isinstance(attach_latest, int)
                        or isinstance(attach_latest, bool)
                        or replay_through <= after_seq
                        or replay_through != attach_latest
                    ):
                        raise ExecutorRunnerError(
                            "terminal/attach returned an inconsistent replay snapshot"
                        )
                    attachment_id = replacement
                    replay_start = len(frames)
                    replay_last = after_seq
                    replay_output: list[str] = []
                    while replay_last < replay_through:
                        event = next_terminal_event(
                            process,
                            frames,
                            terminal_id,
                            timeout=timeout,
                        )
                        seq = event.get("seq")
                        if (
                            not isinstance(seq, int)
                            or isinstance(seq, bool)
                            or seq <= replay_last
                            or seq > replay_through
                        ):
                            raise ExecutorRunnerError(
                                "terminal attach replay sequence escaped its response boundary"
                            )
                        replay_last = seq
                        if event.get("type") != "terminal.output":
                            raise ExecutorRunnerError(
                                "fixture replay included a non-output transition"
                            )
                        data = event.get("data")
                        text = data.get("data") if isinstance(data, dict) else None
                        if isinstance(text, str):
                            replay_output.append(text)
                    replay_end = len(frames)
                    replay_windows.append(
                        (replay_start, replay_end, after_seq, replay_through)
                    )
                    replay_expected = case["expected"].get("replayContains")
                    if replay_expected is not None:
                        if replay_expected not in "".join(replay_output):
                            raise ExecutorRunnerError(
                                "terminal attach replay omitted the expected retained output"
                            )
                        replay_verified = True
                    terminal_rpc_error(
                        process,
                        frames,
                        f"terminal-stale-attachment-{index}",
                        "terminal/resize",
                        {
                            "sessionId": "terminal-conformance-session",
                            "terminalId": terminal_id,
                            "attachmentId": previous_attachment,
                            "size": current_size,
                        },
                        timeout=timeout,
                        allow_events=False,
                        code=-32010,
                        kind="terminal_attachment_stale",
                        retryable=False,
                    )
                    latest_seq = replay_through
                elif action_type == "backpressure":
                    input_seq, rejected = drive_terminal_backpressure(
                        process,
                        frames,
                        terminal_id,
                        attachment_id,
                        input_seq,
                        action_index=index,
                        timeout=timeout,
                    )
                    rejected_sentinels.append(rejected)
                elif action_type == "close":
                    result = terminal_rpc(
                        process,
                        frames,
                        f"terminal-close-{index}",
                        "terminal/close",
                        {
                            "sessionId": "terminal-conformance-session",
                            "terminalId": terminal_id,
                            "attachmentId": attachment_id,
                            "mode": action["mode"],
                        },
                        timeout=timeout,
                    )
                    if result.get("accepted") is not True:
                        raise ExecutorRunnerError("terminal/close was not accepted")
                else:
                    raise ExecutorRunnerError("terminal fixture contains an unknown action")
            replay_expected = case["expected"].get("replayContains")
            if replay_expected is not None and not replay_verified:
                raise ExecutorRunnerError(
                    "terminal replay expectation was not proven by a post-response replay window"
                )
            resize = case["expected"].get("resize")
            if resize is not None:
                rendered = terminal_output_text(frames, terminal_id)
                if str(resize["columns"]) not in rendered or str(resize["rows"]) not in rendered:
                    raise ExecutorRunnerError(
                        "terminal helper did not observe the resized dimensions"
                    )
            if case["expected"].get("closed"):
                deadline = time.monotonic() + timeout
                while not any(
                    frame.get("method") == "terminal/event"
                    and isinstance(frame.get("params"), dict)
                    and frame["params"].get("terminalId") == terminal_id
                    and frame["params"].get("type") == "terminal.closed"
                    for frame in frames
                ):
                    next_terminal_event(
                        process,
                        frames,
                        terminal_id,
                        timeout=max(deadline - time.monotonic(), 0.01),
                    )
            quiet_deadline = time.monotonic() + min(timeout, 0.25)
            while True:
                remaining = quiet_deadline - time.monotonic()
                if remaining <= 0:
                    break
                try:
                    frame = process.next_frame(remaining)
                except queue.Empty:
                    break
                frames.append(frame)
                if frame.get("method") != "terminal/event":
                    raise ExecutorRunnerError(
                        "terminal emitted an unsolicited frame after cleanup"
                    )
            rendered = terminal_output_text(frames, terminal_id)
            if any(sentinel in rendered for sentinel in rejected_sentinels):
                raise ExecutorRunnerError(
                    "terminal backpressure partially wrote the rejected input block"
                )
            validate_terminal_trace(
                frames,
                notification_validator,
                session_id="terminal-conformance-session",
                terminal_id=terminal_id,
                replay_windows=replay_windows,
                max_event_bytes=min(max_event_bytes, max_frame_bytes),
                output_chunk_bytes=16_384,
                require_closed=bool(case["expected"].get("closed")),
                wire_size_for=process.frame_wire_bytes,
            )
        finally:
            process.close()
        serialized = json.dumps(frames, ensure_ascii=False)
        if canary in serialized or canary in process.stderr_text(limit=131_072):
            raise ExecutorRunnerError("terminal canary leaked into protocol or stderr")
        scan_canary(workspace, canary)
        scan_canary(state_root, canary)
    return "PASS"


def run_daemon_suite(
    templates: Sequence[Sequence[str]],
    fixture: JSONObject,
    helper: Path,
    *,
    timeout: float,
) -> list[str]:
    """功能：对每个显式 daemon 依次运行 process/shell/tree 与 terminal 黑盒案例。

    输入：一个或多个 argv 模板、fixture、helper 和总超时。
    输出：带实现序号的 PASS/skip 报告列表。
    不变量：每个案例独立进程/临时根；平台/能力 skip 不计为 PASS。
    失败：已广告能力的任一行为不符时立即抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    reports: list[str] = []
    tool_cases = [
        *require_list(fixture.get("processCases"), "processCases"),
        *require_list(fixture.get("shellCases"), "shellCases"),
        *require_list(fixture.get("treeCases"), "treeCases"),
    ]
    terminal_cases = require_list(fixture.get("terminalCases"), "terminalCases")
    for implementation, template in enumerate(templates, start=1):
        for case in tool_cases:
            if not isinstance(case, dict):
                raise ExecutorRunnerError("executor daemon case is not an object")
            outcome = run_daemon_tool_case(template, case, helper, timeout=timeout)
            reports.append(f"implementation-{implementation}/{case['name']}:{outcome}")
        for case in terminal_cases:
            if not isinstance(case, dict):
                raise ExecutorRunnerError("terminal daemon case is not an object")
            outcome = run_daemon_terminal_case(template, case, helper, timeout=timeout)
            reports.append(f"implementation-{implementation}/{case['name']}:{outcome}")
    return reports


def build_parser() -> argparse.ArgumentParser:
    """功能：构造 executor 静态、本机 helper 和可选 daemon 探针的 CLI。

    输入：无。
    输出：带安全默认值的 ArgumentParser。
    不变量：默认不启动外部 daemon，只运行临时 workspace helper。
    失败：argparse 负责报告无效命令行。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser(description="Validate qxnm-forge executor and terminal contracts")
    parser.add_argument("--fixture", type=Path, default=FIXTURE)
    parser.add_argument("--schema-root", type=Path, default=ROOT / "SPEC/schemas")
    parser.add_argument("--helper", type=Path, default=HELPER)
    parser.add_argument("--daemon-command-json", action="append", type=parse_command)
    parser.add_argument("--timeout", type=float, default=12.0)
    parser.add_argument("--skip-local", action="store_true")
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    """功能：运行 executor Schema/安全门禁、本机 helper 自测并报告平台 skip。

    输入：CLI argv；可选 daemon JSON argv 仅在显式提供时使用。
    输出：标准输出中的脱敏 PASS/skip 摘要和进程退出码。
    不变量：默认临时 workspace、无网络、无真实凭据；平台不可用只报告 skip。
    失败：fixture、helper 或适用案例失败时返回 1，不把未实现能力伪造成 PASS。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = build_parser().parse_args(argv)
    try:
        if args.timeout <= 0 or args.timeout > 60:
            raise ExecutorRunnerError("timeout must be in (0, 60]")
        fixture = strict_load(args.fixture)
        validate_schema(fixture, args.schema_root)
        validate_semantics(fixture, args.helper)
        print("PASS: executor fixture schema and safety semantics")
        if not args.skip_local:
            for report in run_local_suite(fixture, args.helper):
                print(
                    f"PASS: executor local {report}"
                    if ":PASS" in report
                    else f"SKIP: executor local {report}"
                )
        if args.daemon_command_json:
            for report in run_daemon_suite(
                args.daemon_command_json,
                fixture,
                args.helper,
                timeout=args.timeout,
            ):
                print(
                    f"PASS: executor daemon {report}"
                    if report.endswith(":PASS")
                    else f"SKIP: executor daemon {report}"
                )
        return 0
    except (ExecutorRunnerError, OSError, subprocess.SubprocessError) as exc:
        print(f"executor conformance failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
