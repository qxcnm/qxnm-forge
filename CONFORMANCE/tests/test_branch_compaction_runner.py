from __future__ import annotations

from pathlib import Path
import sys
import unittest


CONFORMANCE = Path(__file__).resolve().parents[1]
ROOT = CONFORMANCE.parent
sys.path.insert(0, str(CONFORMANCE))

import branch_compaction_runner  # noqa: E402
import branch_compaction_validation  # noqa: E402


class BranchCompactionRunnerTests(unittest.TestCase):
    def test_build_requests_carries_exact_selected_context(self) -> None:
        """功能：验证 faux expectedContext 是 selected 投影加本轮用户输入。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        state = branch_compaction_validation.validate_case(
            CONFORMANCE / "fixtures/session/branch-compaction-v0.1",
            ROOT / "SPEC/schemas",
        )
        input_message = {
            "role": "user",
            "content": [{"type": "text", "text": "continue"}],
        }
        requests = branch_compaction_runner.build_requests(
            0, state.session_id, state, input_message, "done"
        )
        scenario = requests[2]["params"]["scenario"]
        self.assertEqual(len(scenario["expectedContext"]), len(state.messages) + 1)
        self.assertEqual(scenario["expectedContext"][-1], input_message)

    def test_static_main_succeeds_without_daemon(self) -> None:
        """功能：验证默认入口只运行静态 fixture 且不启动进程。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.assertEqual(branch_compaction_runner.main([]), 0)

    def test_mutation_error_requires_exact_portable_mapping(self) -> None:
        """功能：验证 mutation runner 同时固定 code、retryable 与 kind。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        frames = [
            {
                "jsonrpc": "2.0",
                "id": "stale",
                "error": {
                    "code": -32010,
                    "message": "stale",
                    "retryable": True,
                    "details": {"kind": "stale_session_head"},
                },
            }
        ]
        branch_compaction_runner.assert_response_error(
            frames, "stale", "staleExpectedHead"
        )
        frames[0]["error"]["details"]["kind"] = "stale_state"
        with self.assertRaises(branch_compaction_runner.BranchCompactionRunnerError):
            branch_compaction_runner.assert_response_error(
                frames, "stale", "staleExpectedHead"
            )

    def test_request_builders_keep_explicit_cas_and_token_values(self) -> None:
        """功能：验证 mutation 请求构造器不改写 opaque head 或负向 token 值。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        branch = branch_compaction_runner.branch_select_request(
            "branch", "session", "expected-head", "target"
        )
        self.assertEqual(branch["params"]["expectedHeadRecordId"], "expected-head")
        compact = branch_compaction_runner.compact_request(
            "compact",
            "session",
            "expected-head",
            "retained",
            tokens_before=40,
            tokens_after=41,
        )
        self.assertEqual(compact["params"]["tokensBefore"], 40)
        self.assertEqual(compact["params"]["tokensAfter"], 41)


if __name__ == "__main__":
    unittest.main()
