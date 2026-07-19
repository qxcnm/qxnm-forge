from __future__ import annotations

from copy import deepcopy
from pathlib import Path
import sys
import unittest


CONFORMANCE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(CONFORMANCE))

import writer_lock_runner  # noqa: E402


class WriterLockRunnerTests(unittest.TestCase):
    def test_fixed_fixture_and_owner_are_accepted(self) -> None:
        """功能：验证 runner 接受规范九案例和有效 owner。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        fixture = writer_lock_runner.load_fixed_cases(
            CONFORMANCE / "fixtures/session/writer-lock-cases.json"
        )
        owner = fixture["cases"][0]["owner"]
        self.assertIs(
            writer_lock_runner.validate_owner(owner, "session-lock-fixture"), owner
        )

    def test_owner_external_host_and_unknown_field_are_rejected(self) -> None:
        """功能：验证 runner 在任何连接前拒绝 host 注入和扩展 core 字段。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        fixture = writer_lock_runner.load_fixed_cases(
            CONFORMANCE / "fixtures/session/writer-lock-cases.json"
        )
        owner = deepcopy(fixture["cases"][0]["owner"])
        owner["endpoint"]["host"] = "203.0.113.10"
        with self.assertRaises(writer_lock_runner.WriterLockConformanceError):
            writer_lock_runner.validate_owner(owner, "session-lock-fixture")
        owner = deepcopy(fixture["cases"][0]["owner"])
        owner["workspace"] = "/must/not/appear"
        with self.assertRaises(writer_lock_runner.WriterLockConformanceError):
            writer_lock_runner.validate_owner(owner, "session-lock-fixture")

    def test_static_main_does_not_require_daemon(self) -> None:
        """功能：验证默认入口只执行 fixture 门禁且不启动任何进程。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.assertEqual(writer_lock_runner.main([]), 0)

    def test_locked_response_requires_dedicated_error_code(self) -> None:
        """功能：验证共同 runner 拒绝把 writer lock 误报为 run busy。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        valid = {
            "error": {
                "code": -32002,
                "message": "locked",
                "retryable": True,
                "details": {"kind": "session_locked"},
            }
        }
        writer_lock_runner.assert_locked_response(valid)
        invalid = deepcopy(valid)
        invalid["error"]["code"] = -32004
        with self.assertRaises(writer_lock_runner.WriterLockConformanceError):
            writer_lock_runner.assert_locked_response(invalid)


if __name__ == "__main__":
    unittest.main()
