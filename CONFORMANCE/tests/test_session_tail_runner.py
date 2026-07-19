from __future__ import annotations

from copy import deepcopy
from pathlib import Path
import sys
import unittest


CONFORMANCE = Path(__file__).resolve().parents[1]
ROOT = CONFORMANCE.parent
SCHEMAS = ROOT / "SPEC/schemas"
CASES = CONFORMANCE / "fixtures/session/journal-tail-recovery-cases.json"
BASE = (
    CONFORMANCE
    / "fixtures/session/portable-v0.1/journal.before-recovery.jsonl"
)
sys.path.insert(0, str(CONFORMANCE))

import runner  # noqa: E402
import session_tail_runner  # noqa: E402


class SessionTailRunnerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        """功能：加载案例与 journal Schema，供所有尾部合同专项测试复用。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        try:
            cls.case_validator = session_tail_runner.build_schema_validator(
                SCHEMAS, "session/journal-tail-recovery-cases.schema.json"
            )
            cls.journal_validator = session_tail_runner.build_schema_validator(
                SCHEMAS, "session/journal.schema.json"
            )
        except session_tail_runner.SessionTailConformanceError as exc:
            if "requires jsonschema" in str(exc):
                raise unittest.SkipTest(str(exc)) from exc
            raise
        cls.fixture = session_tail_runner.load_contract(CASES, cls.case_validator)

    def test_fixed_fixture_and_generated_tail_classes(self) -> None:
        """功能：确认固定六案例及生成的 no-LF/LF 分类完整通过静态门禁。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        session_tail_runner.validate_generated_cases(
            self.fixture, BASE, self.journal_validator
        )
        self.assertEqual(6, len(self.fixture["cases"]))
        self.assertEqual(
            ["repair", "repair", *(["journal_corrupt"] * 4)],
            [case["expected"] for case in self.fixture["cases"]],
        )

    def test_committed_prefix_has_gap_and_unknown_extensions(self) -> None:
        """功能：确认动态基础前缀冻结 event gap 与多层未知扩展值。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        raw, values = session_tail_runner.build_committed_prefix(
            BASE, self.journal_validator, "session-tail-unit"
        )
        self.assertTrue(raw.endswith(b"\n"))
        self.assertEqual(
            {"org.example.future-header"}, set(values[0]["extensions"])
        )
        self.assertEqual(
            session_tail_runner.UNKNOWN_EXTENSION_RECORD_ID,
            values[-1]["recordId"],
        )
        events = [
            value["data"]["event"]["seq"]
            for value in values
            if value.get("kind") == "event.emitted"
        ]
        self.assertEqual([1, 2, 3, 4, 5, 6, 7, 10], events)

    def test_valid_json_no_lf_is_schema_valid_but_uncommitted(self) -> None:
        """功能：确认 valid no-LF 负例本身是完整且 Schema 合法的 journal record。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        _, values = session_tail_runner.build_committed_prefix(
            BASE, self.journal_validator, "session-tail-valid-unit"
        )
        last = values[-1]
        tail = session_tail_runner.build_tail(
            "valid_record_no_lf",
            "session-tail-valid-unit",
            last["seq"] + 1,
            last["recordId"],
            self.journal_validator,
        )
        self.assertNotIn(b"\n", tail)
        parsed = runner.strict_json_loads(tail.decode("utf-8"))
        self.assertEqual([], list(self.journal_validator.iter_errors(parsed)))
        self.assertEqual(
            session_tail_runner.UNCOMMITTED_VALID_RECORD_ID, parsed["recordId"]
        )

    def test_recursive_duplicate_key_is_complete_and_strictly_rejected(self) -> None:
        """功能：确认 duplicate-key 负例以 LF 完成且重复发生在递归对象中。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        tail = session_tail_runner.build_recursive_duplicate_tail(
            "session-tail-duplicate-unit", 23, "record-parent"
        )
        self.assertTrue(tail.endswith(b"\n"))
        self.assertIn(b'"outer":{"same":1,"same":2}', tail)
        with self.assertRaises(runner.ProtocolViolation):
            runner.strict_json_loads(tail[:-1].decode("utf-8"))

    def test_recovery_namespace_value_is_schema_closed(self) -> None:
        """功能：确认专用 recovery namespace 的 value 拒绝未知字段和旧 action。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        _, values = session_tail_runner.build_committed_prefix(
            BASE, self.journal_validator, "session-tail-schema-unit"
        )
        record = {
            "schemaVersion": "0.1",
            "kind": "extension",
            "recordId": "record-tail-recovery-schema-unit",
            "sessionId": "session-tail-schema-unit",
            "seq": values[-1]["seq"] + 1,
            "parentId": values[-1]["recordId"],
            "time": "2026-07-10T08:00:23Z",
            "data": {
                "namespace": session_tail_runner.RECOVERY_NAMESPACE,
                "value": {
                    "action": session_tail_runner.RECOVERY_ACTION,
                    "discardedBytes": 1,
                    "backupFile": "journal.recovery-" + ("0" * 64) + ".bak",
                    "originalSha256": "0" * 64,
                },
            },
        }
        self.assertEqual([], list(self.journal_validator.iter_errors(record)))
        invalid = deepcopy(record)
        invalid["data"]["value"]["implementation"] = "must-not-persist"
        self.assertTrue(list(self.journal_validator.iter_errors(invalid)))
        invalid = deepcopy(record)
        invalid["data"]["value"]["action"] = "truncated_torn_tail"
        self.assertTrue(list(self.journal_validator.iter_errors(invalid)))

    def test_corrupt_response_requires_exact_non_retryable_triplet(self) -> None:
        """功能：确认 runner 拒绝错误 code、retryable 或 details.kind 的宽松映射。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        valid = {
            "error": {
                "code": -32008,
                "message": "corrupt",
                "retryable": False,
                "details": {"kind": "journal_corrupt"},
            }
        }
        session_tail_runner.assert_corrupt_response(valid, "unit")
        for field, replacement in (
            ("code", -32603),
            ("retryable", True),
        ):
            invalid = deepcopy(valid)
            invalid["error"][field] = replacement
            with self.subTest(field=field), self.assertRaises(
                session_tail_runner.SessionTailConformanceError
            ):
                session_tail_runner.assert_corrupt_response(invalid, "unit")
        invalid = deepcopy(valid)
        invalid["error"]["details"]["kind"] = "internal_error"
        with self.assertRaises(session_tail_runner.SessionTailConformanceError):
            session_tail_runner.assert_corrupt_response(invalid, "unit")

    def test_static_main_does_not_start_daemon(self) -> None:
        """功能：确认默认入口只运行共享 fixture/Schema 静态门禁。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.assertEqual(0, session_tail_runner.main([]))


if __name__ == "__main__":
    unittest.main()
