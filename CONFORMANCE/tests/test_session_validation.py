from __future__ import annotations

import copy
from pathlib import Path
import subprocess
import sys
import unittest


CONFORMANCE = Path(__file__).resolve().parents[1]
ROOT = CONFORMANCE.parent
CASE = CONFORMANCE / "fixtures/session/portable-v0.1"
SCHEMAS = ROOT / "SPEC/schemas"
sys.path.insert(0, str(CONFORMANCE))

import session_validation  # noqa: E402


class PortableSessionFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        """功能：验证公共 fixture 一次并保存恢复前后的重建状态。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        try:
            cls.before, cls.after = session_validation.validate_case(CASE, SCHEMAS)
        except session_validation.SessionValidationError as exc:
            if "requires jsonschema" in str(exc):
                raise unittest.SkipTest(str(exc)) from exc
            raise

    def test_every_line_and_recovery_contract_validate(self) -> None:
        """功能：确认恢复后只追加四条规范记录且保留完整消息上下文。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.assertEqual(21, len(self.before.records))
        self.assertEqual(25, len(self.after.records))
        self.assertEqual(5, len(self.after.messages))
        self.assertEqual([], self.after.unfinished_run_ids)
        self.assertEqual(["run-crash-1"], self.after.interrupted_run_ids)
        self.assertEqual(
            ["tool-call-side-effect-1"],
            self.after.unknown_outcome_tool_call_ids,
        )

    def test_latest_and_after_seq_use_event_sequence(self) -> None:
        """功能：确认 session/get 序号只读取 event.seq 而非 journal seq。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.assertEqual(9, session_validation.latest_event_seq(self.after))
        self.assertEqual(25, self.after.records[-1]["seq"])
        self.assertEqual(
            [5, 6, 7, 8, 9], session_validation.event_seqs_after(self.after, 4)
        )

    def test_reconstruct_rejects_duplicate_non_idempotent_intent(self) -> None:
        """功能：确认恢复不能用第二条同 ID tool.intent 冒充状态重建。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        values = [self.before.header, *copy.deepcopy(self.before.records)]
        duplicate = copy.deepcopy(values[-2])
        duplicate["seq"] = len(values)
        duplicate["recordId"] = "record-duplicate-intent"
        duplicate["parentId"] = values[-1]["recordId"]
        values.append(duplicate)
        with self.assertRaisesRegex(
            session_validation.SessionValidationError, "duplicate tool intent"
        ):
            session_validation.reconstruct_state(values)

    def test_session_runner_static_entry(self) -> None:
        """功能：确认黑盒 runner 无 daemon 参数时完成静态 fixture 门禁。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        completed = subprocess.run(
            [sys.executable, str(CONFORMANCE / "session_runner.py")],
            cwd=ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertIn("SESSION FIXTURE PASS", completed.stdout)


if __name__ == "__main__":
    unittest.main()
