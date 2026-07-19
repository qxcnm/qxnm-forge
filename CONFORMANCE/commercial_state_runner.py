#!/usr/bin/env python3
"""推广 Provider 本地安装与 CredentialStore 双实现黑盒门禁。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import tempfile
from typing import Any


ROOT = Path(__file__).resolve().parent.parent
FIXTURE = (
    ROOT
    / "CONFORMANCE/fixtures/sponsored-provider-commercial/installed-routes.json"
)


class CommercialStateGateError(Exception):
    """黑盒命令、输出、跨语言格式或零泄露不变量失败。"""


def parse_command(value: str) -> list[str]:
    """功能：把 JSON argv 解码为不经过 shell 的有界命令。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        command = json.loads(value)
    except json.JSONDecodeError as exc:
        raise argparse.ArgumentTypeError("command must be JSON argv") from exc
    if (
        not isinstance(command, list)
        or not 1 <= len(command) <= 64
        or any(
            not isinstance(item, str)
            or not item
            or len(item) > 4096
            or "\0" in item
            for item in command
        )
    ):
        raise argparse.ArgumentTypeError("command JSON argv is invalid")
    return [
        str(Path(item).resolve())
        if not Path(item).is_absolute() and Path(item).is_file()
        else item
        for item in command
    ]


def run_command(
    command: list[str],
    arguments: list[str],
    *,
    cwd: Path,
    stdin: str = "",
    expected: int = 0,
) -> subprocess.CompletedProcess[str]:
    """功能：在隔离环境直接运行 CLI，并验证退出码和有界输出。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    environment = os.environ.copy()
    for name in tuple(environment):
        upper = name.upper()
        if upper.endswith(("_API_KEY", "_TOKEN", "_SECRET", "_PASSWORD")):
            environment.pop(name, None)
    environment["NO_PROXY"] = "*"
    environment["no_proxy"] = "*"
    completed = subprocess.run(
        [*command, *arguments],
        cwd=cwd,
        env=environment,
        input=stdin,
        text=True,
        capture_output=True,
        timeout=20,
        check=False,
    )
    if completed.returncode != expected:
        raise CommercialStateGateError(
            f"unexpected exit {completed.returncode}, expected {expected}: "
            f"{completed.stderr[:400]}"
        )
    if len(completed.stdout) > 1024 * 1024 or len(completed.stderr) > 1024 * 1024:
        raise CommercialStateGateError("CLI output exceeded 1 MiB")
    return completed


def parse_json_output(completed: subprocess.CompletedProcess[str]) -> Any:
    """功能：严格解析 CLI 单个 JSON 输出且拒绝 trailing data。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        return json.loads(completed.stdout)
    except json.JSONDecodeError as exc:
        raise CommercialStateGateError("CLI output is not one JSON value") from exc


def assert_canary_absent(
    canary: str,
    completed: subprocess.CompletedProcess[str],
) -> None:
    """功能：确认运行期随机 credential 没有进入 stdout 或 stderr。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if canary in completed.stdout or canary in completed.stderr:
        raise CommercialStateGateError("credential canary leaked to CLI output")


def install_fixture(state: Path) -> None:
    """功能：把不含 secret 的共同安装 fixture 放到固定本地状态路径。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    commercial = state / "commercial"
    commercial.mkdir(parents=True, mode=0o700, exist_ok=True)
    shutil.copyfile(FIXTURE, commercial / "installed-sponsored-routes.json")
    os.chmod(commercial / "installed-sponsored-routes.json", 0o600)


def assert_session_id_absent(state: Path, session_id: str) -> None:
    """功能：确认缺 credential 拒绝没有产生指定 Session durable 记录。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    for path in state.rglob("*"):
        if path.is_file() and path.stat().st_size <= 2 * 1024 * 1024:
            try:
                text = path.read_text(encoding="utf-8")
            except UnicodeDecodeError:
                continue
            if session_id in text:
                raise CommercialStateGateError(
                    "missing credential created a durable Session record"
                )


def exercise(
    rust: list[str],
    dotnet: list[str],
    temporary: Path,
) -> dict[str, int]:
    """功能：执行双向 credential、共享 route、确认门和 Session 前拒绝案例。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    workspace = temporary / "workspace"
    state = temporary / "state"
    workspace.mkdir()
    state.mkdir()
    canary = "commercial-" + secrets.token_hex(24)
    rust_set = run_command(
        rust,
        [
            "auth",
            "set",
            "--provider",
            "relay-example-relay",
            "--workspace",
            str(workspace),
            "--state-dir",
            str(state),
        ],
        cwd=workspace,
        stdin=canary,
    )
    assert_canary_absent(canary, rust_set)
    dotnet_list = run_command(
        dotnet,
        [
            "auth",
            "list",
            "--workspace",
            str(workspace),
            "--state-dir",
            str(state),
            "--json",
        ],
        cwd=workspace,
    )
    if parse_json_output(dotnet_list) != {"providers": ["relay-example-relay"]}:
        raise CommercialStateGateError(".NET did not read the Rust credential status")

    rotated = "commercial-" + secrets.token_hex(24)
    dotnet_set = run_command(
        dotnet,
        [
            "auth",
            "set",
            "--provider",
            "relay-example-relay",
            "--workspace",
            str(workspace),
            "--state-dir",
            str(state),
        ],
        cwd=workspace,
        stdin=rotated,
    )
    assert_canary_absent(rotated, dotnet_set)
    rust_list = run_command(
        rust,
        [
            "auth",
            "list",
            "--workspace",
            str(workspace),
            "--state-dir",
            str(state),
            "--format",
            "json",
        ],
        cwd=workspace,
    )
    if parse_json_output(rust_list) != {"providers": ["relay-example-relay"]}:
        raise CommercialStateGateError("Rust did not read the .NET credential status")

    install_fixture(state)
    rust_routes = parse_json_output(
        run_command(
            rust,
            ["sponsors", "installed", "--state-dir", str(state), "--format", "json"],
            cwd=workspace,
        )
    )
    dotnet_routes = parse_json_output(
        run_command(
            dotnet,
            ["sponsors", "installed", "--state-dir", str(state), "--json"],
            cwd=workspace,
        )
    )
    if rust_routes != dotnet_routes or rust_routes != json.loads(FIXTURE.read_text()):
        raise CommercialStateGateError("installed route format is not cross-language stable")

    for command in (rust, dotnet):
        refused = run_command(
            command,
            [
                "sponsors",
                "use",
                "example-relay",
                "--model",
                "model-v1",
                "--state-dir",
                str(state),
            ],
            cwd=workspace,
            expected=2,
        )
        if "accept-disclosure" not in refused.stderr:
            raise CommercialStateGateError("sponsors use did not require explicit acceptance")

    run_command(
        dotnet,
        [
            "auth",
            "remove",
            "--provider",
            "relay-example-relay",
            "--workspace",
            str(workspace),
            "--state-dir",
            str(state),
        ],
        cwd=workspace,
    )
    for name, command in (("rust", rust), ("dotnet", dotnet)):
        session_id = f"missing-credential-{name}"
        missing = run_command(
            command,
            [
                "run",
                "hello",
                "--workspace",
                str(workspace),
                "--state-dir",
                str(state),
                "--session",
                session_id,
                "--provider",
                "relay-example-relay",
                "--model",
                "model-v1",
                "--api-family",
                "openai-completions",
            ],
            cwd=workspace,
            expected=4,
        )
        assert_canary_absent(canary, missing)
        assert_canary_absent(rotated, missing)
        assert_session_id_absent(state, session_id)

    return {"cases": 10, "crossRuntimeDirections": 2, "networkRequests": 0}


def main() -> int:
    """功能：解析双端命令、运行隔离门禁并输出稳定 JSON 汇总。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser()
    parser.add_argument("--rust-command-json", required=True, type=parse_command)
    parser.add_argument("--dotnet-command-json", required=True, type=parse_command)
    arguments = parser.parse_args()
    try:
        with tempfile.TemporaryDirectory(prefix="qxnm-forge-commercial-") as directory:
            result = exercise(
                arguments.rust_command_json,
                arguments.dotnet_command_json,
                Path(directory),
            )
    except (CommercialStateGateError, OSError, subprocess.SubprocessError) as exc:
        print(json.dumps({"status": "failed", "error": str(exc)}, ensure_ascii=False))
        return 1
    print(json.dumps({"status": "passed", **result}, ensure_ascii=False, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
