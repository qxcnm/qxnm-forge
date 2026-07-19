#!/usr/bin/env python3
"""黑盒验证原生 CLI 安全导入 PI Session v3 合成夹具。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import os
from pathlib import Path
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import threading
from typing import Any, BinaryIO, Sequence

import pi_v3_import_validation
import provider_runner
import runner
import session_runner


JSONObject = dict[str, Any]
MAX_STDOUT_BYTES = 65_536
MAX_STDERR_BYTES = 131_072
MAX_DISCOVERED_FILE_BYTES = 32 * 1024 * 1024


class PiV3ImportRunnerError(Exception):
    """表示原生 PI v3 import CLI 或其文件副作用不符合共同契约。"""


@dataclass(frozen=True)
class CommandOutcome:
    """保存有界子进程退出状态与双流字节。"""

    return_code: int
    stdout: bytes
    stderr: bytes


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    """功能：解析 fixture、Schema、隔离根和一个或多个 import CLI 模板。

    输入：可选 argv；省略时读取当前进程命令行。
    输出：路径已保留为 Path 的 argparse Namespace。
    不变量：命令模板只接受严格 JSON argv，不经过 shell。
    失败：非法 CLI 由 argparse 以 usage error 终止。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(
        description="验证原生 CLI 的 PI Session v3 clean-room 一次性导入"
    )
    parser.add_argument(
        "--case-dir",
        type=Path,
        default=root / "CONFORMANCE/fixtures/session/pi-v3-import-v0.1",
    )
    parser.add_argument("--schema-root", type=Path, default=root / "SPEC/schemas")
    parser.add_argument(
        "--work-root", type=Path, help="保留输出的空目录；省略时使用临时目录"
    )
    parser.add_argument("--import-command-json", action="append", default=[])
    parser.add_argument("--timeout", type=float, default=15.0)
    return parser.parse_args(argv)


def require_object(value: Any, context: str) -> JSONObject:
    """功能：要求动态 JSON 值是字符串键对象并返回。

    输入：动态值与不含实例内容的诊断标签。
    输出：类型收窄后的 JSON 对象。
    失败：值不是对象或含非字符串键时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, dict) or not all(isinstance(key, str) for key in value):
        raise PiV3ImportRunnerError(f"{context} must be a JSON object")
    return value


def render_command(
    raw_template: str,
    source: Path,
    workspace: Path,
    state_root: Path,
    session_id: str,
) -> list[str]:
    """功能：严格解析 argv 模板并替换 runner 管理的有限占位符。

    输入：JSON argv 模板、隔离 source/workspace/state 与目标 Session ID。
    输出：可直接交给 Popen 且未经过 shell 的 argv。
    不变量：只替换完整参数中的已知占位符，不展开环境或命令文本。
    失败：模板不是安全字符串数组或含未知占位符时传播 Session runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    template = session_runner.parse_command_template(raw_template)
    return session_runner.render_command(
        template,
        {
            "source": str(source),
            "workspace": str(workspace),
            "stateRoot": str(state_root),
            "sessionId": session_id,
            "repoRoot": str(Path(__file__).resolve().parents[1]),
        },
    )


def _read_bounded_stream(
    stream: BinaryIO,
    limit: int,
    output: bytearray,
    overflow: list[bool],
) -> None:
    """功能：持续排空子进程流，只保留固定字节前缀并记录越界。

    输入：二进制 pipe、正字节上限、调用方缓冲区和单元素越界标记。
    输出：EOF 前持续排空，最多向 output 写入 limit 字节。
    不变量：即使越界仍读取后续字节，避免子进程因 pipe 背压假死。
    失败：底层读取错误仅设置 overflow，由主线程统一拒绝结果。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        while True:
            chunk = stream.read(4096)
            if not chunk:
                return
            remaining = max(limit - len(output), 0)
            output.extend(chunk[:remaining])
            if len(chunk) > remaining:
                overflow[0] = True
    except OSError:
        overflow[0] = True


def _terminate_process_tree(process: subprocess.Popen[bytes]) -> None:
    """功能：超时时强制终止 runner 自己启动的完整 import CLI 进程范围。

    输入：仍可能运行且由本 runner 创建的 Popen。
    输出：尽力终止并在短超时内回收直接子进程。
    不变量：POSIX 使用新 session 的进程组；Windows 使用进程组根进程。
    失败：进程竞态消失视为成功，其余系统错误被安全收敛。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        if os.name == "posix":
            os.killpg(process.pid, signal.SIGKILL)
        else:
            process.kill()
    except (OSError, ProcessLookupError):
        pass
    try:
        process.wait(timeout=2.0)
    except subprocess.TimeoutExpired:
        pass


def run_bounded_command(
    command: list[str], timeout: float, environment: dict[str, str]
) -> CommandOutcome:
    """功能：在无 shell、清洁环境中运行 import CLI 并有界收集双流。

    输入：非空 argv、正超时与已清理环境副本。
    输出：退出码及有界 stdout/stderr 字节。
    不变量：POSIX 子进程进入独立 session；任一流越界或超时都失败关闭。
    失败：启动、超时、流越界或读取错误转换为不含完整 argv/环境的 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not command or timeout <= 0:
        raise PiV3ImportRunnerError("import command and timeout must be valid")
    options: dict[str, Any] = {}
    if os.name == "posix":
        options["start_new_session"] = True
    elif os.name == "nt":
        options["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    try:
        process = subprocess.Popen(
            command,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=environment,
            **options,
        )
    except OSError as exc:
        raise PiV3ImportRunnerError(
            f"cannot launch import executable {Path(command[0]).name!r}"
        ) from exc
    assert process.stdout is not None
    assert process.stderr is not None
    stdout = bytearray()
    stderr = bytearray()
    stdout_overflow = [False]
    stderr_overflow = [False]
    stdout_thread = threading.Thread(
        target=_read_bounded_stream,
        args=(process.stdout, MAX_STDOUT_BYTES, stdout, stdout_overflow),
        daemon=True,
    )
    stderr_thread = threading.Thread(
        target=_read_bounded_stream,
        args=(process.stderr, MAX_STDERR_BYTES, stderr, stderr_overflow),
        daemon=True,
    )
    stdout_thread.start()
    stderr_thread.start()
    try:
        return_code = process.wait(timeout=timeout)
    except subprocess.TimeoutExpired as exc:
        _terminate_process_tree(process)
        stdout_thread.join(timeout=2.0)
        stderr_thread.join(timeout=2.0)
        process.stdout.close()
        process.stderr.close()
        raise PiV3ImportRunnerError("import CLI timed out") from exc
    stdout_thread.join(timeout=2.0)
    stderr_thread.join(timeout=2.0)
    process.stdout.close()
    process.stderr.close()
    if (
        stdout_thread.is_alive()
        or stderr_thread.is_alive()
        or stdout_overflow[0]
        or stderr_overflow[0]
    ):
        _terminate_process_tree(process)
        raise PiV3ImportRunnerError("import CLI output exceeded its bounded channel")
    return CommandOutcome(return_code, bytes(stdout), bytes(stderr))


def clean_environment(workspace: Path, state_root: Path) -> dict[str, str]:
    """功能：构造禁止 live Provider、凭据、代理继承和字节码缓存的导入环境。

    输入：当前隔离 workspace 与 state root。
    输出：仅加入 conformance 路径/关闭开关的进程环境副本。
    不变量：删除共同 Provider runner 列出的全部凭据与 endpoint 名称。
    失败：本函数不执行 I/O，也不返回凭据值。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    environment = os.environ.copy()
    for name in (
        *provider_runner.CREDENTIAL_ENV,
        *provider_runner.ENDPOINT_ENV,
        *provider_runner.PROXY_ENV,
    ):
        environment.pop(name, None)
    environment.update(
        {
            "PYTHONDONTWRITEBYTECODE": "1",
            "QXNM_FORGE_CONFORMANCE": "1",
            "QXNM_FORGE_IMPORT_CONFORMANCE": "1",
            "QXNM_FORGE_LIVE_TESTS": "0",
            "QXNM_FORGE_ALLOW_LIVE_PROVIDER_TESTS": "0",
            "QXNM_FORGE_WORKSPACE": str(workspace),
            "QXNM_FORGE_SESSION_ROOT": str(state_root),
            "HTTP_PROXY": "http://127.0.0.1:9",
            "HTTPS_PROXY": "http://127.0.0.1:9",
            "ALL_PROXY": "http://127.0.0.1:9",
            "NO_PROXY": "localhost,127.0.0.1,::1",
        }
    )
    return environment


def parse_success_output(raw: bytes) -> JSONObject:
    """功能：严格解析 import CLI 唯一 LF 结尾的成功 JSON 对象。

    输入：有界 stdout 原始字节。
    输出：无重复键、无尾随数据的对象。
    不变量：stdout 只允许一个非空 UTF-8 JSON 行，禁止 banner 或 ANSI。
    失败：编码、换行、重复键、尾随帧或非对象时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not raw.endswith(b"\n") or raw.count(b"\n") != 1 or b"\x1b" in raw:
        raise PiV3ImportRunnerError("import stdout must contain one LF JSON frame")
    try:
        text = raw[:-1].decode("utf-8", errors="strict")
        value = runner.strict_json_loads(text)
    except (UnicodeDecodeError, runner.ProtocolViolation) as exc:
        raise PiV3ImportRunnerError("import stdout is not strict UTF-8 JSON") from exc
    return require_object(value, "import success output")


def discover_regular_files(root: Path) -> list[Path]:
    """功能：不跟随符号链接地枚举隔离 state root 内的全部普通文件。

    输入：runner 独占的新 state root。
    输出：按相对路径排序的普通文件列表。
    不变量：目录、文件或链接离开 root 都被拒绝；单文件大小受固定上限。
    失败：symlink、特殊文件、超大文件或遍历 I/O 失败时抛出 runner 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    files: list[Path] = []
    for directory, directory_names, file_names in os.walk(root, followlinks=False):
        directory_path = Path(directory)
        for name in list(directory_names):
            candidate = directory_path / name
            if candidate.is_symlink():
                raise PiV3ImportRunnerError(
                    "import target contains a symlink directory"
                )
        for name in file_names:
            candidate = directory_path / name
            metadata = candidate.lstat()
            if stat.S_ISLNK(metadata.st_mode) or not stat.S_ISREG(metadata.st_mode):
                raise PiV3ImportRunnerError("import target contains a non-regular file")
            if metadata.st_size > MAX_DISCOVERED_FILE_BYTES:
                raise PiV3ImportRunnerError("import target file exceeds runner limit")
            files.append(candidate)
    return sorted(files, key=lambda path: str(path.relative_to(root)))


def run_stage(
    raw_template: str,
    stage: int,
    work_root: Path,
    case_dir: Path,
    static_state: pi_v3_import_validation.PiV3ImportState,
    expectations: JSONObject,
    timeout: float,
) -> None:
    """功能：驱动一个原生 import CLI 并验证输出、源不变、journal 与报告 artifact。

    输入：命令模板、stage 隔离根、规范 case/静态状态/期望和超时。
    输出：实现产物与确定性 clean-room fixture 逐字节一致时正常返回。
    不变量：源副本只读且前后 identity/bytes/mode 不变；state 内只允许普通文件。
    失败：进程、输出、泄漏、覆盖、journal、artifact 或多余 Session 失败时终止。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    stage_root = work_root / f"implementation-{stage + 1}"
    workspace = stage_root / "workspace"
    state_root = stage_root / "state"
    source_root = stage_root / "source-canary-do-not-persist"
    workspace.mkdir(parents=True, exist_ok=False)
    state_root.mkdir(parents=True, exist_ok=False)
    source_root.mkdir(parents=True, exist_ok=False)
    source = source_root / "pi-v3-source.jsonl"
    shutil.copyfile(case_dir / "source.pi-v3.jsonl", source)
    source.chmod(0o400)
    source_before = source.read_bytes()
    source_stat_before = source.stat()
    session_id = str(expectations["newSessionId"])
    command = render_command(raw_template, source, workspace, state_root, session_id)
    outcome = run_bounded_command(
        command, timeout, clean_environment(workspace, state_root)
    )
    if outcome.return_code != 0:
        raise PiV3ImportRunnerError(
            f"import CLI exited nonzero ({outcome.return_code})"
        )
    leaked_path = str(source).encode("utf-8")
    if leaked_path in outcome.stdout or leaked_path in outcome.stderr:
        raise PiV3ImportRunnerError("import CLI leaked the caller source path")
    output = parse_success_output(outcome.stdout)
    expected_output = {
        "status": static_state.report["status"],
        "sessionId": expectations["newSessionId"],
        "reportArtifactId": require_object(
            expectations["reportArtifact"], "expected report artifact"
        )["artifactId"],
    }
    if output != expected_output:
        raise PiV3ImportRunnerError("import CLI success output differs")
    source_stat_after = source.stat()
    if (
        source.read_bytes() != source_before
        or source_stat_after.st_mode != source_stat_before.st_mode
        or source_stat_after.st_size != source_stat_before.st_size
        or source_stat_after.st_ino != source_stat_before.st_ino
    ):
        raise PiV3ImportRunnerError("import CLI modified or replaced the source")
    files = discover_regular_files(state_root)
    journals = [path for path in files if path.name == "journal.jsonl"]
    if len(journals) != 1:
        raise PiV3ImportRunnerError("import must publish exactly one journal")
    expected_journal = (case_dir / "expected.journal.jsonl").read_bytes()
    if journals[0].read_bytes() != expected_journal:
        raise PiV3ImportRunnerError(
            "imported conformance journal differs byte-for-byte"
        )
    expected_report = (case_dir / "artifacts/import-report.json").read_bytes()
    expected_report_hash = hashlib.sha256(expected_report).hexdigest()
    report_matches = [
        path
        for path in files
        if path != journals[0]
        and path.stat().st_size == len(expected_report)
        and hashlib.sha256(path.read_bytes()).hexdigest() == expected_report_hash
    ]
    if len(report_matches) != 1 or report_matches[0].read_bytes() != expected_report:
        raise PiV3ImportRunnerError("import report artifact is missing or ambiguous")


def run_probe(args: argparse.Namespace, work_root: Path) -> int:
    """功能：先验证规范 fixture，再顺序运行每个原生 import CLI 模板。

    输入：解析参数与空隔离根。
    输出：全部 stage 通过时返回 0；无命令时只运行静态门禁。
    不变量：每个实现使用不同 workspace/state/source 副本，互不读取前一 stage。
    失败：静态或任一实现失败时抛出并由 main 转为退出 1。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    case_dir = args.case_dir.resolve()
    schema_root = args.schema_root.resolve()
    static_state = pi_v3_import_validation.validate_case(case_dir, schema_root)
    _, expectations = pi_v3_import_validation.load_json_object(
        case_dir / "expectations.json",
        maximum=2_097_152,
        context="PI import expectations",
    )
    for stage, template in enumerate(args.import_command_json):
        run_stage(
            template,
            stage,
            work_root,
            case_dir,
            static_state,
            expectations,
            args.timeout,
        )
        print(f"PASS: PI v3 native import stage {stage + 1}")
    if not args.import_command_json:
        print(
            "PASS: PI v3 import static fixture "
            f"sourceEntries={len(static_state.source.entries)}, "
            f"targetRecords={len(static_state.journal_values) - 1}"
        )
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    """功能：运行静态或原生 PI v3 import 黑盒门禁并输出脱敏摘要。

    输入：可选 CLI argv。
    输出：成功为 0，参数、契约、实现或 I/O 失败为 1。
    不变量：失败文本不含完整 argv、source 内容、路径、报告值或环境。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = parse_args(argv)
    try:
        if args.timeout <= 0:
            raise PiV3ImportRunnerError("--timeout must be positive")
        if args.work_root is not None:
            root = args.work_root.resolve()
            root.mkdir(parents=True, exist_ok=True)
            if any(root.iterdir()):
                raise PiV3ImportRunnerError("--work-root must be empty")
            return run_probe(args, root)
        with tempfile.TemporaryDirectory(prefix="qxnm-forge-pi-import-") as temporary:
            return run_probe(args, Path(temporary))
    except (
        OSError,
        ValueError,
        runner.ConformanceError,
        pi_v3_import_validation.PiV3ImportValidationError,
        PiV3ImportRunnerError,
    ) as exc:
        print(f"PI V3 IMPORT FAIL: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
