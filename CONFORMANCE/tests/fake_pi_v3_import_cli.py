#!/usr/bin/env python3
"""供共同 runner 自测使用的确定性 PI v3 import CLI，不属于任何语言实现。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sys
from typing import Sequence


EXPECTED_SESSION_ID = "session-import-pi-v3-fixture"
EXPECTED_SOURCE_SHA256 = (
    "f31b9b7a784a3af526fec462e5b16c5bffaaedf45b7a8fa101d946bfd3d02b39"
)


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    """功能：解析共同 import CLI 固定子命令和安全路径参数。

    输入：可选 argv。
    输出：包含 source/workspace/state/session 的 Namespace。
    不变量：只接受 conformance JSON 输出形态，不接受额外未知参数。
    失败：非法参数由 argparse 终止。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser()
    parser.add_argument("scope", choices=["session"])
    parser.add_argument("operation", choices=["import-pi-v3"])
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--workspace", type=Path, required=True)
    parser.add_argument("--state-dir", type=Path, required=True)
    parser.add_argument("--session", required=True)
    parser.add_argument("--format", choices=["json"], required=True)
    parser.add_argument("--conformance", action="store_true", required=True)
    return parser.parse_args(argv)


def run(args: argparse.Namespace) -> int:
    """功能：核对合成 source 并复制规范 journal/report 作为 runner 自测产物。

    输入：严格解析的 conformance import 参数。
    输出：成功写入一个隔离 Session 后返回 0。
    不变量：不修改 source，不读取凭据，不执行 PI/工具/Provider。
    失败：source、Session、workspace 或目标状态非法时返回非零且不输出路径。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        source = args.source.read_bytes()
    except OSError:
        return 7
    if (
        hashlib.sha256(source).hexdigest() != EXPECTED_SOURCE_SHA256
        or args.session != EXPECTED_SESSION_ID
        or not args.workspace.is_dir()
        or not args.conformance
    ):
        return 2
    fixture = Path(__file__).resolve().parents[1] / "fixtures/session/pi-v3-import-v0.1"
    session_root = args.state_dir / "sessions" / args.session
    artifact_root = session_root / "artifacts"
    try:
        artifact_root.mkdir(parents=True, exist_ok=False)
        shutil.copyfile(
            fixture / "expected.journal.jsonl", session_root / "journal.jsonl"
        )
        shutil.copyfile(
            fixture / "artifacts/import-report.json",
            artifact_root / "artifact-pi-v3-import-report.json",
        )
    except OSError:
        return 8
    output = {
        "status": "completed_with_warnings",
        "sessionId": EXPECTED_SESSION_ID,
        "reportArtifactId": "artifact-pi-v3-import-report",
    }
    sys.stdout.write(json.dumps(output, ensure_ascii=False, separators=(",", ":")))
    sys.stdout.write("\n")
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    """功能：运行 fake import CLI 并返回 portable 退出码。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return run(parse_args(argv))


if __name__ == "__main__":
    raise SystemExit(main())
