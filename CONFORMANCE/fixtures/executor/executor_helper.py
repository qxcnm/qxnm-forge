#!/usr/bin/env python3
"""qxnm-forge executor conformance helper.

本文件只包含固定、离线、无网络动作；runner 会把它复制到临时 workspace。
作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time


def safe_marker(value: str) -> Path:
    """功能：把夹具提供的 marker 名限制为临时 workspace 内单一文件名。

    输入：不可信的相对 marker 字符串。
    输出：当前工作区下的安全 Path。
    不变量：拒绝绝对路径、路径分隔符、`.`/`..` 和 NUL，绝不访问 workspace 外。
    失败：违反边界时抛出 ValueError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if (
        not value
        or "\x00" in value
        or value in {".", ".."}
        or Path(value).is_absolute()
        or Path(value).name != value
        or value.startswith("~")
    ):
        raise ValueError("marker must be one workspace-relative filename")
    return Path.cwd() / value


def action_emit(arguments: list[str]) -> int:
    """功能：按固定参数写入 stdout/stderr 并返回指定非零或零退出码。

    输入：`exitCode`, stdout 文本和 stderr 文本三个参数。
    输出：原样写入两条独立流并返回受限退出码。
    不变量：不执行参数中的命令，不访问文件或网络。
    失败：参数数量、NUL 或退出码越界时返回参数错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if len(arguments) != 3:
        return 2
    try:
        exit_code = int(arguments[0], 10)
    except ValueError:
        return 2
    if not -128 <= exit_code <= 255 or any("\x00" in item for item in arguments[1:]):
        return 2
    sys.stdout.write(arguments[1])
    sys.stdout.flush()
    sys.stderr.write(arguments[2])
    sys.stderr.flush()
    return exit_code


def action_argv(arguments: list[str]) -> int:
    """功能：以 JSON 记录收到的 argv，验证空格、通配符和 Unicode 边界未被重解析。

    输入：任意由 runner 固定生成的字符串 argv。
    输出：stdout 中一个 JSON 数组，不拼接或执行任何元素。
    不变量：输出不包含环境变量展开结果，不访问网络。
    失败：参数包含 NUL 时返回参数错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if any("\x00" in item for item in arguments):
        return 2
    sys.stdout.write(json.dumps(arguments, ensure_ascii=False, separators=(",", ":")))
    sys.stdout.flush()
    return 0


def action_read_stdin(arguments: list[str]) -> int:
    """功能：读取 bounded stdin 并以 JSON 字符串回显，验证 stdin 不混入 argv。

    输入：无额外参数和由 executor 提供的 UTF-8 stdin。
    输出：stdout 中一个 JSON 字符串。
    不变量：最多读取 1 MiB，不执行输入内容。
    失败：额外参数或超大输入返回参数错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if arguments:
        return 2
    value = sys.stdin.buffer.read(1_048_577)
    if len(value) > 1_048_576:
        return 2
    sys.stdout.write(json.dumps(value.decode("utf-8"), ensure_ascii=False))
    sys.stdout.flush()
    return 0


def action_sleep(arguments: list[str]) -> int:
    """功能：在不创建子进程的情况下等待有限毫秒，供 timeout/cancel 夹具使用。

    输入：一个 1..60000 的毫秒数。
    输出：等待结束后写入固定 `awake` 文本并返回零。
    不变量：不执行 shell、不访问网络，等待上限有限。
    失败：格式或范围无效时返回参数错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if len(arguments) != 1:
        return 2
    try:
        delay_ms = int(arguments[0], 10)
    except ValueError:
        return 2
    if not 1 <= delay_ms <= 60_000:
        return 2
    time.sleep(delay_ms / 1000)
    print("awake", flush=True)
    return 0


def action_overflow(arguments: list[str]) -> int:
    """功能：分块产生有界测试输出，驱动 executor 持续排空与 output-limit 终止。

    输入：字节数和 `stdout`/`stderr` 流名。
    输出：指定流中固定 `x` 字节，不创建文件。
    不变量：字节数最多 2 MiB，单次写块有限且不联网。
    失败：格式、流名或范围无效时返回参数错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if len(arguments) != 2:
        return 2
    try:
        count = int(arguments[0], 10)
    except ValueError:
        return 2
    if not 1 <= count <= 2 * 1024 * 1024 or arguments[1] not in {"stdout", "stderr"}:
        return 2
    stream = sys.stdout.buffer if arguments[1] == "stdout" else sys.stderr.buffer
    block = b"x" * min(8192, count)
    remaining = count
    while remaining:
        size = min(len(block), remaining)
        stream.write(block[:size])
        stream.flush()
        remaining -= size
    return 0


def action_env_probe(arguments: list[str]) -> int:
    """功能：只报告指定环境变量是否存在，不输出变量名对应的 secret 值。

    输入：一个安全环境变量名。
    输出：固定 `present` 或 `absent` 文本。
    不变量：永远不打印环境变量内容，尤其不打印 conformance canary。
    失败：名称格式无效时返回参数错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if len(arguments) != 1 or not arguments[0].replace("_", "").isalnum():
        return 2
    print("present" if arguments[0] in os.environ else "absent", flush=True)
    return 0


def action_delayed_write(arguments: list[str]) -> int:
    """功能：等待后写入安全 marker，作为被父进程树清理的后代动作。

    输入：workspace 内单一 marker 文件名和 1..60000 毫秒延迟。
    输出：marker 内容 `descendant-was-alive`；只在延迟后写入。
    不变量：路径严格限制在当前 workspace，不创建网络连接。
    失败：边界或延迟无效时返回参数错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if len(arguments) != 2:
        return 2
    try:
        marker = safe_marker(arguments[0])
        delay_ms = int(arguments[1], 10)
    except (ValueError, TypeError):
        return 2
    if not 1 <= delay_ms <= 60_000:
        return 2
    time.sleep(delay_ms / 1000)
    marker.write_text("descendant-was-alive", encoding="utf-8")
    return 0


def action_descendant(arguments: list[str]) -> int:
    """功能：启动固定 delayed-write 后代并保持父进程，覆盖进程组逃逸攻击夹具。

    输入：marker 文件名、延迟毫秒和 `escape`/`group` 模式。
    输出：固定 `child-started` 文本后持续等待，直到 executor 终止父树。
    不变量：只启动本文件的 delayed-write 动作；`escape` 在 POSIX 使用 setsid，不能执行任意 argv。
    失败：参数、平台或 marker 边界无效时返回参数错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if len(arguments) != 3 or arguments[2] not in {"escape", "group"}:
        return 2
    try:
        _ = safe_marker(arguments[0])
        int(arguments[1], 10)
    except (ValueError, TypeError):
        return 2
    child = [
        sys.executable,
        str(Path(__file__).resolve()),
        "delayed-write",
        *arguments[:2],
    ]
    kwargs: dict[str, object] = {}
    if arguments[2] == "escape" and os.name == "posix":
        kwargs["start_new_session"] = True
    elif arguments[2] == "escape" and os.name == "nt":
        kwargs["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    try:
        subprocess.Popen(
            child,
            cwd=Path.cwd(),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            **kwargs,
        )
    except (OSError, ValueError):
        return 2
    print("child-started", flush=True)
    time.sleep(60)
    return 0


def action_terminal(arguments: list[str]) -> int:
    """功能：在真实终端设备中执行固定 echo/size/exit 命令，拒绝 pipe 冒充 PTY。

    输入：无额外参数；stdin/stdout 必须由 PTY/ConPTY 提供。
    输出：JSON ready/size/echo/interrupt 行和真实 tty 布尔值。
    不变量：不访问网络、不启动子进程；收到 interrupt 后安全退出。
    失败：任一标准流不是 tty 时返回参数错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not sys.stdin.isatty() or not sys.stdout.isatty():
        return 3
    interrupted = False

    def on_interrupt(_signum: int, _frame: object) -> None:
        """功能：记录 PTY interrupt 并打断可能被 PEP 475 重启的阻塞读取。

        输入：宿主 signal handler 参数。
        输出：更新本地布尔状态后抛出由主循环明确捕获的 KeyboardInterrupt。
        不变量：只处理 SIGINT，不执行 I/O、子进程或任意命令。
        失败：KeyboardInterrupt 是固定控制流，不携带动态文本并且不会越过 action 边界。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        nonlocal interrupted
        interrupted = True
        raise KeyboardInterrupt

    if hasattr(signal, "SIGINT"):
        signal.signal(signal.SIGINT, on_interrupt)
    print(json.dumps({"ready": True, "stdinTty": True, "stdoutTty": True}), flush=True)
    while not interrupted:
        try:
            line = sys.stdin.readline()
        except KeyboardInterrupt:
            break
        if not line:
            break
        command = line.rstrip("\r\n")
        if command == "size":
            try:
                size = os.get_terminal_size(sys.stdout.fileno())
                print(
                    json.dumps({"columns": size.columns, "rows": size.lines}),
                    flush=True,
                )
            except OSError:
                return 4
        elif command == "exit":
            return 0
        elif command.startswith("echo:"):
            print(command[5:], flush=True)
        else:
            print("unknown", flush=True)
    print(json.dumps({"interrupted": interrupted}), flush=True)
    return 130 if interrupted else 0


def build_parser() -> argparse.ArgumentParser:
    """功能：构造仅允许固定 helper action 的命令行解析器。

    输入：无。
    输出：拒绝未知动作和缺失参数的 ArgumentParser。
    不变量：解析器不接受任意 shell 或 executable 字段。
    失败：调用方传入非法 action 时由 argparse 返回错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser(description="offline qxnm-forge executor helper")
    parser.add_argument(
        "action",
        choices=[
            "emit",
            "argv",
            "read-stdin",
            "sleep",
            "overflow",
            "env-probe",
            "delayed-write",
            "descendant",
            "terminal",
        ],
    )
    parser.add_argument("arguments", nargs="*")
    return parser


def main(argv: list[str] | None = None) -> int:
    """功能：分派固定 helper 动作并把异常转换为无敏感信息的退出码。

    输入：命令行 argv；省略时读取进程 argv。
    输出：动作退出码，不输出 stack trace 或 secret。
    不变量：只调用本文件中的固定函数，不联网、不执行用户提供的命令。
    失败：动作异常返回 2，便于 runner 区分 helper 故障与被终止。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = build_parser().parse_args(argv)
    handlers = {
        "emit": action_emit,
        "argv": action_argv,
        "read-stdin": action_read_stdin,
        "sleep": action_sleep,
        "overflow": action_overflow,
        "env-probe": action_env_probe,
        "delayed-write": action_delayed_write,
        "descendant": action_descendant,
        "terminal": action_terminal,
    }
    try:
        return handlers[args.action](args.arguments)
    except (OSError, ValueError, UnicodeError):
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
