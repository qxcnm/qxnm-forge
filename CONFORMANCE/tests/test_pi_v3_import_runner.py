from __future__ import annotations

import json
from pathlib import Path
import sys
import unittest


CONFORMANCE = Path(__file__).resolve().parents[1]
ROOT = CONFORMANCE.parent
sys.path.insert(0, str(CONFORMANCE))

import pi_v3_import_runner  # noqa: E402


class PiV3ImportRunnerTests(unittest.TestCase):
    def test_static_main_succeeds_without_import_command(self) -> None:
        """功能：验证默认入口只执行静态 fixture 且不创建原生进程。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.assertEqual(pi_v3_import_runner.main([]), 0)

    def test_success_output_rejects_banner_or_second_line(self) -> None:
        """功能：验证 import 成功 stdout 必须是唯一严格 LF JSON 对象。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        valid = b'{"status":"completed","sessionId":"s","reportArtifactId":"a"}\n'
        self.assertEqual(
            pi_v3_import_runner.parse_success_output(valid)["status"], "completed"
        )
        for invalid in (b"banner\n" + valid, valid + b"{}\n", valid.rstrip(b"\n")):
            with self.assertRaises(pi_v3_import_runner.PiV3ImportRunnerError):
                pi_v3_import_runner.parse_success_output(invalid)

    def test_fake_native_import_passes_complete_black_box_gate(self) -> None:
        """功能：用确定性 fake CLI 验证进程、源不变、journal 和 artifact 门禁。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        command = json.dumps(
            [
                sys.executable,
                str(CONFORMANCE / "tests/fake_pi_v3_import_cli.py"),
                "session",
                "import-pi-v3",
                "--source",
                "{source}",
                "--workspace",
                "{workspace}",
                "--state-dir",
                "{stateRoot}",
                "--session",
                "{sessionId}",
                "--format",
                "json",
                "--conformance",
            ]
        )
        self.assertEqual(
            pi_v3_import_runner.main(["--import-command-json", command]), 0
        )


if __name__ == "__main__":
    unittest.main()
