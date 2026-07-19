from __future__ import annotations

from copy import deepcopy
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


CONFORMANCE = Path(__file__).resolve().parents[1]
ROOT = CONFORMANCE.parent
CASE = CONFORMANCE / "fixtures/session/pi-v3-import-v0.1"
SCHEMAS = ROOT / "SPEC/schemas"
sys.path.insert(0, str(CONFORMANCE))

import pi_v3_import_validation  # noqa: E402


class PiV3ImportValidationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        """功能：加载并验证一次 PI v3 clean-room fixture 供负例测试复用。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        cls.state = pi_v3_import_validation.validate_case(CASE, SCHEMAS)
        _, cls.expectations = pi_v3_import_validation.load_json_object(
            CASE / "expectations.json",
            maximum=2_097_152,
            context="test expectations",
        )

    def test_normative_fixture_preserves_tree_and_excludes_custom_context(self) -> None:
        """功能：确认分支、compaction、未知项和 custom 隔离后的 selected context。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.assertEqual(17, len(self.state.source.entries))
        self.assertEqual(20, len(self.state.journal_values) - 1)
        self.assertEqual(
            (
                "message-pi-user-1",
                "message-pi-assistant-1",
                "message-pi-user-2",
                "message-pi-branch-summary",
                "message-pi-branch-user",
                "message-pi-branch-assistant",
            ),
            self.state.selected_message_ids,
        )
        self.assertNotIn("message-pi-custom-message-1", self.state.selected_message_ids)
        self.assertEqual("completed_with_warnings", self.state.report["status"])

    def test_source_rejects_duplicate_json_keys(self) -> None:
        """功能：确认 PI permissive skip 行为不会让重复 JSON 键进入导入。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        source = (CASE / "source.pi-v3.jsonl").read_bytes()
        changed = source.replace(b'"version":3', b'"version":3,"version":3', 1)
        with tempfile.TemporaryDirectory(prefix="pi-import-source-") as temporary:
            path = Path(temporary) / "source.jsonl"
            path.write_bytes(changed)
            with self.assertRaises(pi_v3_import_validation.PiV3ImportValidationError):
                pi_v3_import_validation.load_pi_v3_source(path)

    def test_source_rejects_future_parent_and_second_root(self) -> None:
        """功能：确认 source tree 只接受一个 root 和 earlier-only parent。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        source_lines = [
            json.loads(line)
            for line in (CASE / "source.pi-v3.jsonl")
            .read_text(encoding="utf-8")
            .splitlines()
        ]
        for parent in (None, "pi-unknown-1"):
            changed = deepcopy(source_lines)
            changed[2]["parentId"] = parent
            raw = (
                "\n".join(
                    json.dumps(
                        value,
                        ensure_ascii=False,
                        separators=(",", ":"),
                        allow_nan=False,
                    )
                    for value in changed
                )
                + "\n"
            ).encode()
            with tempfile.TemporaryDirectory(prefix="pi-import-parent-") as temporary:
                path = Path(temporary) / "source.jsonl"
                path.write_bytes(raw)
                with self.assertRaises(
                    pi_v3_import_validation.PiV3ImportValidationError
                ):
                    pi_v3_import_validation.load_pi_v3_source(path)

    def test_source_rejects_unknown_fields_on_known_entry_and_excess_depth(
        self,
    ) -> None:
        """功能：确认已知 entry 不接受偷塞字段且 JSON 嵌套深度有硬上限。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        source = (CASE / "source.pi-v3.jsonl").read_text(encoding="utf-8").splitlines()
        changed = json.loads(source[1])
        changed["unexpected"] = True
        source[1] = json.dumps(changed, separators=(",", ":"))
        with tempfile.TemporaryDirectory(prefix="pi-import-fields-") as temporary:
            path = Path(temporary) / "source.jsonl"
            path.write_text("\n".join(source) + "\n", encoding="utf-8")
            with self.assertRaises(pi_v3_import_validation.PiV3ImportValidationError):
                pi_v3_import_validation.load_pi_v3_source(path)
        nested = ("[" * 65 + "0" + "]" * 65).encode()
        with self.assertRaises(pi_v3_import_validation.PiV3ImportValidationError):
            pi_v3_import_validation._validate_json_depth(nested, "test")

    def test_report_coverage_rejects_omitted_loss(self) -> None:
        """功能：确认报告遗漏任一 custom、未知或有损 entry 时失败。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        report = deepcopy(self.state.report)
        report["entries"] = report["entries"][:-1]
        mappings, _ = pi_v3_import_validation._source_mapping_index(
            self.state.source, self.expectations
        )
        with self.assertRaises(pi_v3_import_validation.PiV3ImportValidationError):
            pi_v3_import_validation._validate_report_coverage(
                self.state.source, mappings, report, self.expectations
            )

    def test_custom_message_cannot_be_promoted_to_context(self) -> None:
        """功能：确认 custom_message 的目标必须是 context-free extension。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        values = deepcopy(list(self.state.journal_values))
        records = {record["recordId"]: record for record in values[1:]}
        records["record-pi-custom-message-1"]["kind"] = "message.appended"
        mappings, _ = pi_v3_import_validation._source_mapping_index(
            self.state.source, self.expectations
        )
        with self.assertRaises(pi_v3_import_validation.PiV3ImportValidationError):
            pi_v3_import_validation._validate_mapping_kinds(
                self.state.source, mappings, records
            )

    def test_imported_journal_rejects_lifecycle_record(self) -> None:
        """功能：确认导入目标不能伪造 run、Provider 或工具恢复生命周期。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        values = deepcopy(list(self.state.journal_values))
        values[9]["kind"] = "run.started"
        with self.assertRaises(pi_v3_import_validation.PiV3ImportValidationError):
            pi_v3_import_validation.validate_import_journal(
                self.state.source,
                values,
                self.expectations,
                self.state.report,
            )

    def test_sensitive_and_host_path_scanners_fail_closed(self) -> None:
        """功能：确认报告 quarantine 不能携带 secret 或主机绝对路径。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with self.assertRaises(pi_v3_import_validation.PiV3ImportValidationError):
            pi_v3_import_validation.assert_no_sensitive_values(
                {"apiKey": "synthetic-value"}, "test"
            )
        with self.assertRaises(pi_v3_import_validation.PiV3ImportValidationError):
            pi_v3_import_validation.assert_no_host_paths(
                {"value": "/host/private/source.jsonl"}, "test"
            )

    def test_report_byte_change_breaks_artifact_binding(self) -> None:
        """功能：确认报告即使仍是合法 JSON，字节变化也会触发 SHA-256 失败。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with tempfile.TemporaryDirectory(prefix="pi-import-report-") as temporary:
            copied = Path(temporary) / "case"
            shutil.copytree(CASE, copied)
            report_path = copied / "artifacts/import-report.json"
            report_path.write_bytes(report_path.read_bytes() + b" ")
            with self.assertRaises(pi_v3_import_validation.PiV3ImportValidationError):
                pi_v3_import_validation.validate_case(copied, SCHEMAS)

    def test_cli_static_gate_reports_only_counts(self) -> None:
        """功能：确认静态入口成功且 stdout 不复制 source 内容或路径。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        completed = subprocess.run(
            [
                sys.executable,
                str(CONFORMANCE / "pi_v3_import_validation.py"),
                "--case-dir",
                str(CASE),
                "--schema-root",
                str(SCHEMAS),
            ],
            cwd=ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertIn("PI V3 IMPORT FIXTURE PASS", completed.stdout)
        self.assertNotIn("SYNTHETIC_USER", completed.stdout)
        self.assertNotIn(str(CASE), completed.stdout)


if __name__ == "__main__":
    unittest.main()
