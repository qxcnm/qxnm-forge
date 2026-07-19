#!/usr/bin/env python3
"""签名推广 Provider 目录的 Rust/.NET 双向黑盒一致性 runner。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
import base64
import binascii
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from typing import Any


ROOT = Path(__file__).resolve().parent.parent
FIXTURE = ROOT / "CONFORMANCE/fixtures/sponsored-provider-catalog/empty.catalog.json"
GOLDEN = ROOT / "CONFORMANCE/fixtures/golden/sponsored-catalog.verify.json"
MAX_OUTPUT_BYTES = 256 * 1024


class RunnerError(Exception):
    """双向签名、验签、安全负例或子进程边界失败。"""


def _reject_duplicate_pairs(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    """功能：把 JSON object pairs 转为字典并拒绝重复键。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise RunnerError("fixture contains a duplicate JSON key")
        result[key] = value
    return result


def load_json(path: Path) -> dict[str, Any]:
    """功能：有界、严格 UTF-8 读取单个 JSON object。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    data = path.read_bytes()
    if not data or len(data) > MAX_OUTPUT_BYTES:
        raise RunnerError("JSON fixture size is invalid")
    try:
        value = json.loads(
            data.decode("utf-8"), object_pairs_hook=_reject_duplicate_pairs
        )
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise RunnerError("JSON fixture is invalid") from exc
    if not isinstance(value, dict):
        raise RunnerError("JSON fixture root must be an object")
    return value


def parse_command(value: str, label: str) -> list[str]:
    """功能：严格解析 runner 显式接收的无 shell JSON argv。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        command = json.loads(value)
    except json.JSONDecodeError as exc:
        raise RunnerError(f"{label} command JSON is invalid") from exc
    if (
        not isinstance(command, list)
        or not command
        or any(not isinstance(item, str) or not item for item in command)
    ):
        raise RunnerError(f"{label} command must be a non-empty string array")
    return command


def scrubbed_environment() -> dict[str, str]:
    """功能：创建禁止 Provider 凭据、live 开关和外网代理的子进程环境。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    environment = os.environ.copy()
    for name in (
        "OPENAI_API_KEY",
        "ANTHROPIC_API_KEY",
        "GEMINI_API_KEY",
        "MISTRAL_API_KEY",
        "OPENROUTER_API_KEY",
        "QXNM_FORGE_SESSION_ROOT",
        "QXNM_FORGE_SPONSOR_CATALOG_URL",
    ):
        environment.pop(name, None)
    environment.update(
        {
            "QXNM_FORGE_LIVE_TESTS": "0",
            "QXNM_FORGE_ALLOW_LIVE_PROVIDER_TESTS": "0",
            "HTTP_PROXY": "http://127.0.0.1:9",
            "HTTPS_PROXY": "http://127.0.0.1:9",
            "ALL_PROXY": "http://127.0.0.1:9",
            "NO_PROXY": "localhost,127.0.0.1,::1",
        }
    )
    return environment


def run_command(
    prefix: list[str],
    arguments: list[str],
    *,
    should_succeed: bool,
) -> bytes:
    """功能：无 shell 运行有界 CLI，并校验成功或安全失败预期。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        completed = subprocess.run(
            [*prefix, *arguments],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=scrubbed_environment(),
            timeout=15,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise RunnerError("catalog CLI failed to start or timed out") from exc
    if (
        len(completed.stdout) > MAX_OUTPUT_BYTES
        or len(completed.stderr) > MAX_OUTPUT_BYTES
    ):
        raise RunnerError("catalog CLI output exceeded the runner limit")
    if should_succeed and completed.returncode != 0:
        raise RunnerError("catalog CLI unexpectedly failed")
    if not should_succeed and completed.returncode == 0:
        raise RunnerError("catalog CLI accepted a tampered envelope")
    return completed.stdout


def create_tampered_envelope(source: Path, target: Path) -> None:
    """功能：保持 envelope JSON 合法但翻转 signed payload 一个字节。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    envelope = load_json(source)
    payload_value = envelope.get("payload")
    if not isinstance(payload_value, str):
        raise RunnerError("signed envelope has no payload")
    try:
        payload = bytearray(base64.b64decode(payload_value, validate=True))
    except (ValueError, binascii.Error) as exc:
        raise RunnerError("signed envelope payload is invalid") from exc
    if not payload:
        raise RunnerError("signed envelope payload is empty")
    payload[0] ^= 1
    envelope["payload"] = base64.b64encode(payload).decode("ascii")
    target.write_text(
        json.dumps(envelope, ensure_ascii=False, separators=(",", ":")) + "\n",
        encoding="utf-8",
    )


def verify_cross_direction(
    signer: list[str],
    verifier: list[str],
    directory: Path,
    label: str,
) -> None:
    """功能：由一端临时生成/签名，再由另一端精确 golden 验证并拒绝篡改。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    private_key = directory / f"{label}.private.der"
    public_key = directory / f"{label}.public.json"
    envelope = directory / f"{label}.envelope.json"
    tampered = directory / f"{label}.tampered.json"
    run_command(
        signer,
        [
            "sponsors",
            "keygen",
            "--key-id",
            f"{label}-owner-1",
            "--private-key",
            str(private_key),
            "--public-key",
            str(public_key),
        ],
        should_succeed=True,
    )
    run_command(
        signer,
        [
            "sponsors",
            "sign",
            "--catalog",
            str(FIXTURE),
            "--private-key",
            str(private_key),
            "--public-key",
            str(public_key),
            "--output",
            str(envelope),
        ],
        should_succeed=True,
    )
    output = run_command(
        verifier,
        [
            "sponsors",
            "verify",
            "--envelope",
            str(envelope),
            "--public-key",
            str(public_key),
            *(["--json"] if label == "rust" else ["--format", "json"]),
        ],
        should_succeed=True,
    )
    if output != GOLDEN.read_bytes():
        raise RunnerError(f"{label} cross-verification output does not match golden")
    create_tampered_envelope(envelope, tampered)
    leaked = run_command(
        verifier,
        [
            "sponsors",
            "verify",
            "--envelope",
            str(tampered),
            "--public-key",
            str(public_key),
        ],
        should_succeed=False,
    )
    if leaked:
        raise RunnerError("tampered envelope failure wrote stdout")


def main() -> int:
    """功能：运行 Rust→.NET 与 .NET→Rust 两个签名方向及两个篡改负例。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser()
    parser.add_argument("--rust-command-json", required=True)
    parser.add_argument("--dotnet-command-json", required=True)
    arguments = parser.parse_args()
    rust = parse_command(arguments.rust_command_json, "Rust")
    dotnet = parse_command(arguments.dotnet_command_json, ".NET")
    os.umask(0o077)
    temporary = Path(tempfile.mkdtemp(prefix="qxnm-forge-sponsored-catalog-"))
    try:
        verify_cross_direction(rust, dotnet, temporary, "rust")
        verify_cross_direction(dotnet, rust, temporary, "dotnet")
    finally:
        shutil.rmtree(temporary, ignore_errors=True)
    print("sponsored catalog cross-signature: 2 directions / 2 tamper negatives PASS")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except RunnerError as error:
        raise SystemExit(f"sponsored catalog conformance failed: {error}") from error
