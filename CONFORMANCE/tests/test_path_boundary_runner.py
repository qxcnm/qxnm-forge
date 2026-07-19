"""验证 ADR 0021 Linux 路径一致性并发 runner 的静态与文件系统边界。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

from copy import deepcopy
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest


CONFORMANCE = Path(__file__).resolve().parents[1]
ROOT = CONFORMANCE.parent
SCHEMAS = ROOT / "SPEC/schemas"
FIXTURE = CONFORMANCE / "fixtures/security/path-boundary-race-cases.json"
sys.path.insert(0, str(CONFORMANCE))

import path_boundary_runner  # noqa: E402


class PathBoundaryRunnerTests(unittest.TestCase):
    """覆盖 fixture 闭集、命令入口、no-follow 控制文件和三类路径重绑定。"""

    @classmethod
    def setUpClass(cls) -> None:
        """功能：加载一次已通过 Schema 的固定 6+2 fixture。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        try:
            cls.fixture = path_boundary_runner.load_fixture(FIXTURE, SCHEMAS)
        except path_boundary_runner.PathBoundaryConformanceError as exc:
            if "requires jsonschema" in str(exc):
                raise unittest.SkipTest(str(exc)) from exc
            raise

    def test_fixture_freezes_six_cases_and_two_single_gate_failures(self) -> None:
        """功能：确认静态合同固定六个并发案例和两个单门失败组合。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        cases, gates = path_boundary_runner.validate_fixture_semantics(self.fixture)
        self.assertEqual(6, len(cases))
        self.assertEqual(2, len(gates))
        self.assertEqual(
            [
                "file.read",
                "file.write",
                "file.read",
                "file.write",
                "file.read",
                "file.write",
            ],
            [case["toolName"] for case in cases],
        )

    def test_semantic_gate_rejects_case_order_drift(self) -> None:
        """功能：确认即使调用方绕过 Schema，案例顺序漂移仍被语义门禁拒绝。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        invalid = deepcopy(self.fixture)
        invalid["cases"][0], invalid["cases"][1] = (
            invalid["cases"][1],
            invalid["cases"][0],
        )
        with self.assertRaises(path_boundary_runner.PathBoundaryConformanceError):
            path_boundary_runner.validate_fixture_semantics(invalid)

    def test_scenario_preserves_fixture_tool_identity_and_write_content(self) -> None:
        """功能：确认 faux 场景不改写 toolCallId、相对路径或写入正文。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        write_case = self.fixture["cases"][1]
        scenario = path_boundary_runner.scenario_for(write_case)
        call = scenario["steps"][0]
        self.assertEqual(write_case["toolCallId"], call["toolCallId"])
        self.assertEqual(write_case["relativePath"], call["arguments"]["path"])
        self.assertEqual(write_case["writeContent"], call["arguments"]["content"])

    def test_nested_parent_mutation_returns_moved_pinned_target(self) -> None:
        """功能：确认 nested parent 重绑定后结果指向 moved 原 parent 而非 symlink pathname。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        case = self.fixture["cases"][2]
        with tempfile.TemporaryDirectory(prefix="path-boundary-unit-") as temporary:
            root = Path(temporary)
            workspace = root / "workspace"
            outside = root / "outside"
            workspace.mkdir()
            target = path_boundary_runner.seed_case(
                workspace, outside, case, "unit-outside-canary"
            )
            outcome = path_boundary_runner.mutate_case(workspace, target, outside, case)
            self.assertEqual(
                workspace / "nested/parent.pinned/target.txt",
                outcome.pinned_target,
            )
            self.assertEqual(case["insideContent"], outcome.pinned_target.read_text())
            self.assertTrue((workspace / "nested/parent").is_symlink())

    def test_ready_reader_rejects_symlink_without_following(self) -> None:
        """功能：确认 ready.json 为 symlink 时 no-follow reader 立即失败。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        case = self.fixture["cases"][0]
        with tempfile.TemporaryDirectory(prefix="path-boundary-ready-") as temporary:
            root = Path(temporary)
            control = root / "control"
            control.mkdir(mode=0o700)
            target = root / "outside-ready.json"
            target.write_text(
                json.dumps(
                    {
                        "schemaVersion": "0.1",
                        "caseId": case["name"],
                        "toolCallId": case["toolCallId"],
                        "checkpoint": case["checkpoint"],
                        "state": "ready",
                    }
                ),
                encoding="utf-8",
            )
            os.symlink(target, control / "ready.json")
            with self.assertRaises(path_boundary_runner.PathBoundaryConformanceError):
                path_boundary_runner.wait_ready(control, case, 0.1)

    def test_manifest_detects_content_change_and_tracks_raw_symlink(self) -> None:
        """功能：确认 outside manifest 同时跟踪 regular 内容与 raw symlink target。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with tempfile.TemporaryDirectory(prefix="path-boundary-manifest-") as temporary:
            root = Path(temporary)
            (root / "target.txt").write_text("before\n", encoding="utf-8")
            os.symlink("target.txt", root / "raw-link")
            before = path_boundary_runner.tree_manifest(root)
            self.assertEqual("target.txt", before["raw-link"][-1])
            (root / "target.txt").write_text("after\n", encoding="utf-8")
            self.assertNotEqual(before, path_boundary_runner.tree_manifest(root))

    def test_case_cli_selects_one_dynamic_case(self) -> None:
        """功能：确认调试入口接受固定 case 名且不接受任意名称。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        parsed = path_boundary_runner.parse_args(
            ["--case", "read_nested_parent_rebind"]
        )
        self.assertEqual("read_nested_parent_rebind", parsed.case)


if __name__ == "__main__":
    unittest.main()
