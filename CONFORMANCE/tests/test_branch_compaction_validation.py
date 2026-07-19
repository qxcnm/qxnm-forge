from __future__ import annotations

from copy import deepcopy
from pathlib import Path
import sys
import unittest


CONFORMANCE = Path(__file__).resolve().parents[1]
ROOT = CONFORMANCE.parent
sys.path.insert(0, str(CONFORMANCE))

import branch_compaction_validation  # noqa: E402
import session_validation  # noqa: E402


class BranchCompactionValidationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        """功能：加载一次规范 fixture 供语义篡改单测复用。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        cls.case_dir = CONFORMANCE / "fixtures/session/branch-compaction-v0.1"
        validator = session_validation.build_line_validator(ROOT / "SPEC/schemas")
        _, cls.values = session_validation.load_journal(
            cls.case_dir / "journal.jsonl", validator
        )

    def test_normative_projection_matches_expected_ids(self) -> None:
        """功能：验证 summary、retained、Branch A 的精确五消息顺序。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        state = branch_compaction_validation.validate_case(
            self.case_dir, ROOT / "SPEC/schemas"
        )
        self.assertEqual(
            [message["messageId"] for message in state.messages],
            [
                "message-summary",
                "message-recent-user",
                "message-recent-assistant",
                "message-branch-a-user",
                "message-branch-a-assistant",
            ],
        )

    def test_selection_target_must_equal_parent(self) -> None:
        """功能：验证 branch.selected 不能声明与 tree edge 不同的目标。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        values = deepcopy(self.values)
        values[-1]["data"]["leafRecordId"] = "record-compaction"
        with self.assertRaises(branch_compaction_validation.BranchCompactionError):
            branch_compaction_validation.validate_values(values)

    def test_ordinary_append_must_extend_selected_head(self) -> None:
        """功能：验证只有 branch.selected 可以跳到 earlier parent。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        values = deepcopy(self.values)
        values[10]["parentId"] = "record-compaction"
        with self.assertRaises(branch_compaction_validation.BranchCompactionError):
            branch_compaction_validation.validate_values(values)

    def test_retained_boundary_must_be_user_message(self) -> None:
        """功能：验证 compaction 不能从 assistant/tool 边界孤立上下文。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        values = deepcopy(self.values)
        values[6]["data"]["firstRetainedRecordId"] = "record-recent-assistant"
        with self.assertRaises(branch_compaction_validation.BranchCompactionError):
            branch_compaction_validation.validate_values(values)

    def test_tokens_after_cannot_grow(self) -> None:
        """功能：验证 compaction token 估算不得大于压缩前值。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        values = deepcopy(self.values)
        values[6]["data"]["tokensAfter"] = 101
        with self.assertRaises(branch_compaction_validation.BranchCompactionError):
            branch_compaction_validation.validate_values(values)


if __name__ == "__main__":
    unittest.main()
