#!/usr/bin/env python3
"""qxnm-forge hard-sandbox daemon conformance 的固定离线 helper。

本文件只在 runner 创建的临时 workspace 内运行，不访问公网或真实凭据。
作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import socket
import sys
import time


def safe_name(value: str) -> str:
    """功能：把 runner 参数限制为 workspace 根中的单一安全文件名。

    输入：待验证的 marker 文件名。
    输出：不含路径语义的原字符串。
    不变量：拒绝绝对路径、分隔符、`.`、`..`、NUL 与 home 展开前缀。
    失败：边界非法时抛出 ValueError，且不访问文件系统。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    candidate = Path(value)
    if (
        not value
        or "\x00" in value
        or value in {".", ".."}
        or value.startswith("~")
        or candidate.is_absolute()
        or candidate.name != value
    ):
        raise ValueError("marker must be one workspace-relative filename")
    return value


def isolation_probe(arguments: list[str]) -> int:
    """功能：验证 daemon 建立的文件、网络、cwd、argv、临时目录与凭据隔离。

    输入：workspace access、宿主 loopback 端口和固定 Unicode argv token。
    输出：stdout 中一行固定键布尔 JSON；全部断言成立时返回 0。
    不变量：只尝试连接 runner 的 `127.0.0.1` listener；不输出环境值或宿主路径。
    失败：任一隔离维度不成立或参数非法时返回非零，写测试文件仅限 `/workspace` 与 `/tmp`。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if len(arguments) != 3 or arguments[0] not in {"read_only", "read_write"}:
        return 2
    try:
        port = int(arguments[1], 10)
    except ValueError:
        return 2
    if not 1 <= port <= 65_535 or arguments[2] != "argv 空格 * 雪":
        return 2
    workspace_readable = Path("../inside.txt").read_text(encoding="utf-8") == "inside"
    cwd_mapped = Path.cwd().as_posix() == "/workspace/probe"
    outside_invisible = not Path("/workspace/../outside.txt").exists()
    credential_absent = "QXNM_FORGE_HARD_SANDBOX_SECRET" not in os.environ
    temporary = Path("/tmp/qxnm-forge-hard-sandbox-probe")
    temporary.write_text("tmp", encoding="utf-8")
    tmp_writable = temporary.read_text(encoding="utf-8") == "tmp"
    system_target = Path("/usr/qxnm-forge-hard-sandbox-probe")
    system_read_only = False
    try:
        system_target.write_text("forbidden", encoding="utf-8")
    except OSError:
        system_read_only = True
    else:
        try:
            system_target.unlink()
        except OSError:
            pass
    client = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    client.settimeout(0.5)
    try:
        network_isolated = client.connect_ex(("127.0.0.1", port)) != 0
    finally:
        client.close()
    write_target = Path("../write.txt")
    workspace_writable = False
    try:
        write_target.write_text("written", encoding="utf-8")
        workspace_writable = write_target.read_text(encoding="utf-8") == "written"
    except OSError:
        workspace_writable = False
    expected_writable = arguments[0] == "read_write"
    evidence = {
        "argvPreserved": True,
        "credentialAbsent": credential_absent,
        "cwdMapped": cwd_mapped,
        "networkIsolated": network_isolated,
        "outsideInvisible": outside_invisible,
        "systemReadOnly": system_read_only,
        "tmpWritable": tmp_writable,
        "workspaceReadable": workspace_readable,
        "workspaceWritable": workspace_writable,
    }
    print(json.dumps(evidence, sort_keys=True, separators=(",", ":")), flush=True)
    return (
        0
        if all(
            (
                workspace_readable,
                cwd_mapped,
                outside_invisible,
                credential_absent,
                tmp_writable,
                system_read_only,
                network_isolated,
                workspace_writable == expected_writable,
            )
        )
        else 3
    )


def descendant_probe(arguments: list[str]) -> int:
    """功能：创建真实 fork+setsid 后代并以 sleep 或输出洪泛驱动清理路径。

    输入：ready/逃逸 marker 文件名、后代延迟毫秒及 `sleep`/`output` 模式。
    输出：父进程持续运行或持续输出，正常情况下由 daemon supervisor 终止。
    不变量：子进程只在 `/workspace` 写两个固定 marker；调用 `setsid` 后不执行任意程序。
    失败：非 POSIX、参数非法、fork/setsid 或文件操作失败时返回非零。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if (
        len(arguments) != 4
        or arguments[3] not in {"sleep", "output"}
        or os.name != "posix"
    ):
        return 2
    try:
        ready_name = safe_name(arguments[0])
        escaped_name = safe_name(arguments[1])
        delay_ms = int(arguments[2], 10)
    except (TypeError, ValueError):
        return 2
    if not 100 <= delay_ms <= 10_000:
        return 2
    child = os.fork()
    if child == 0:
        try:
            os.setsid()
            Path("/workspace", ready_name).write_text("ready", encoding="ascii")
            time.sleep(delay_ms / 1000)
            Path("/workspace", escaped_name).write_text("escaped", encoding="ascii")
            os._exit(0)
        except BaseException:
            os._exit(74)
    if arguments[3] == "output":
        block = b"x" * 8192
        while True:
            os.write(sys.stdout.fileno(), block)
    while True:
        time.sleep(0.05)


def marker_probe(arguments: list[str]) -> int:
    """功能：写入单一 workspace marker，用于证明 unavailable 路径没有 host fallback。

    输入：一个安全 marker 文件名。
    输出：写入固定文本并返回 0。
    不变量：目标始终位于当前 cwd；不解析命令、不访问网络或环境值。
    失败：名称非法或写入失败时返回非零。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if len(arguments) != 1:
        return 2
    try:
        name = safe_name(arguments[0])
        Path.cwd().joinpath(name).write_text("started", encoding="ascii")
    except (OSError, ValueError):
        return 2
    return 0


def build_parser() -> argparse.ArgumentParser:
    """功能：构造仅接受三个固定 hard-sandbox 探针动作的参数解析器。

    输入：无。
    输出：拒绝未知 action 的 ArgumentParser。
    不变量：任何参数都不会被解释为 shell、模块名或任意 executable。
    失败：非法 argv 由 argparse 以固定非零状态拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser(description="offline hard-sandbox daemon helper")
    parser.add_argument("action", choices=["isolation", "descendant", "marker"])
    parser.add_argument("arguments", nargs="*")
    return parser


def main(argv: list[str] | None = None) -> int:
    """功能：分派固定探针并把预期 I/O/参数失败收敛为非敏感退出码。

    输入：可选 argv；省略时使用进程 argv。
    输出：动作返回码。
    不变量：只调用本文件静态映射的函数，不打印 stack trace 或 credential。
    失败：预期的 OSError/Unicode/ValueError 返回 2，未知异常仍由进程非零暴露。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parsed = build_parser().parse_args(argv)
    actions = {
        "isolation": isolation_probe,
        "descendant": descendant_probe,
        "marker": marker_probe,
    }
    try:
        return actions[parsed.action](parsed.arguments)
    except (OSError, UnicodeError, ValueError):
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
