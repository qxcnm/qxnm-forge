from __future__ import annotations

import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


CONFORMANCE = Path(__file__).resolve().parents[1]
ROOT = CONFORMANCE.parent
SCHEMAS = ROOT / "SPEC/schemas"
sys.path.insert(0, str(CONFORMANCE))

import provider_runner  # noqa: E402
import tool_runner  # noqa: E402
import tool_validation  # noqa: E402


class ToolRunnerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        """功能：加载共享 16-case 工具/审批夹具供静态与动态测试使用。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        cls.fixture = tool_validation.load_fixture()

    @unittest.skipIf(
        importlib.util.find_spec("jsonschema") is None,
        "optional jsonschema package is unavailable",
    )
    def test_static_gate_covers_daemon_failure_and_recovery_cases(self) -> None:
        """功能：确认公共静态门禁覆盖十一正常、四故障和一个恢复案例。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.assertEqual(
            (11, 4, 1),
            tool_runner.run_static_gate(tool_validation.DEFAULT_FIXTURE, SCHEMAS),
        )

    @unittest.skipIf(
        importlib.util.find_spec("jsonschema") is None,
        "optional jsonschema package is unavailable",
    )
    def test_fixture_matches_bundled_tool_approval_schema(self) -> None:
        """功能：确认工具/审批机器夹具通过 bundled Draft 2020-12 Schema。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        tool_runner.validate_fixture_schema(tool_validation.DEFAULT_FIXTURE, SCHEMAS)

    def test_dynamic_fixture_rejects_process_or_shell_commands(self) -> None:
        """功能：确认动态 fixture 即使结构可改也不能携带任意执行型工具。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        fixture = copy.deepcopy(self.fixture)
        case = fixture["cases"][0]
        case["scenario"]["steps"][0] = {
            "type": "tool_call",
            "toolCallId": "forbidden-process-1",
            "name": "process.exec",
            "arguments": {"executable": "forbidden", "args": []},
        }
        with self.assertRaises(tool_validation.ToolValidationError):
            tool_validation.validate_fixture(fixture, None)

    @unittest.skipIf(
        importlib.util.find_spec("jsonschema") is None,
        "optional jsonschema package is unavailable",
    )
    def test_live_journal_snapshot_ignores_only_in_progress_tail(self) -> None:
        """功能：确认事件时检查读取完整 LF 前缀，不把并发半写下一行误报损坏。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        header = {
            "kind": "session",
            "schemaVersion": "0.1",
            "sessionId": tool_runner.SESSION_ID,
            "createdAt": "2026-07-11T04:00:00Z",
            "workspace": "/workspace",
        }
        record = {
            "schemaVersion": "0.1",
            "kind": "run.cancellation_requested",
            "recordId": "record-live-prefix-1",
            "sessionId": tool_runner.SESSION_ID,
            "seq": 1,
            "parentId": None,
            "time": "2026-07-11T04:00:00Z",
            "data": {"runId": "run-live-prefix-1"},
        }
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            path = root / "sessions/live/journal.jsonl"
            path.parent.mkdir(parents=True)
            complete = "\n".join(
                json.dumps(value, separators=(",", ":")) for value in (header, record)
            )
            path.write_bytes((complete + '\n{"schemaVersion"').encode("utf-8"))
            records = tool_runner._load_session_records(root, SCHEMAS, live_prefix=True)
            self.assertEqual([record], records)
            with self.assertRaises(provider_runner.ProviderProbeError):
                tool_runner._load_session_records(root, SCHEMAS)

    @unittest.skipIf(
        importlib.util.find_spec("jsonschema") is None,
        "optional jsonschema package is unavailable",
    )
    def test_control_success_without_durable_mutation_is_rejected(self) -> None:
        """功能：确认成功响应不能依赖后续事件掩盖尚未落盘的控制 mutation。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        header = {
            "kind": "session",
            "schemaVersion": "0.1",
            "sessionId": tool_runner.SESSION_ID,
            "createdAt": "2026-07-11T04:00:00Z",
            "workspace": "/workspace",
        }
        request = tool_runner._cancel_request("run-control-negative-1")
        response = {
            "jsonrpc": "2.0",
            "id": request["id"],
            "result": {"cancellationState": "requested"},
        }
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            path = root / "journal.jsonl"
            path.write_text(
                json.dumps(header, separators=(",", ":")) + "\n",
                encoding="utf-8",
            )
            with self.assertRaises(tool_runner.ToolProbeError):
                tool_runner.assert_control_response_durable(
                    root, SCHEMAS, request, response
                )

    @unittest.skipIf(
        importlib.util.find_spec("jsonschema") is None,
        "optional jsonschema package is unavailable",
    )
    def test_dynamic_approval_allow_once_probe_passes(self) -> None:
        """功能：确认 runner 可驱动真实 approval/respond ack、写入和多轮 continuation。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        completed = self._run_dynamic_case("interactive_allow_once")
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertIn("PASS: interactive_allow_once", completed.stdout)

    @unittest.skipIf(
        importlib.util.find_spec("jsonschema") is None,
        "optional jsonschema package is unavailable",
    )
    def test_repeated_cancel_is_idempotent_in_dynamic_probe(self) -> None:
        """功能：确认取消审批案例发送两次 cancel 且最终只有一个 durable intent。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        completed = self._run_dynamic_case("cancel_while_waiting_approval")
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertIn("PASS: cancel_while_waiting_approval", completed.stdout)

    @unittest.skipIf(
        importlib.util.find_spec("jsonschema") is None,
        "optional jsonschema package is unavailable",
    )
    def test_cancel_finalizes_every_complete_source_call(self) -> None:
        """功能：确认取消首个审批后，同批其余完整工具调用仍获得 cancelled 结果。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        completed = self._run_dynamic_case("cancel_finishes_remaining_source_calls")
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertIn(
            "PASS: cancel_finishes_remaining_source_calls", completed.stdout
        )

    @unittest.skipIf(
        importlib.util.find_spec("jsonschema") is None,
        "optional jsonschema package is unavailable",
    )
    def test_dynamic_probe_rejects_tool_completed_before_result(self) -> None:
        """功能：确认 tool.completed 早于 result/message durable append 的假实现失败。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        completed = self._run_dynamic_case(
            "safe_file_read_without_approval", extra_daemon_args=["--late-tool-result"]
        )
        self.assertEqual(1, completed.returncode)
        self.assertIn("tool.result was not durably appended", completed.stderr)

    def _run_dynamic_case(
        self, case_name: str, *, extra_daemon_args: list[str] | None = None
    ) -> subprocess.CompletedProcess[str]:
        """功能：以 JSON argv 和 fake daemon 运行一个隔离的动态工具案例。

        输入：稳定案例名及可选仅用于负例的 fake daemon 参数。
        输出：捕获 stdout/stderr 的 subprocess 完成对象。
        不变量：命令作为 JSON argv 传入 tool runner，不经过 shell。
        失败：子进程启动错误由 subprocess 传播；案例失败保留在返回码中。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        daemon_command = [
            sys.executable,
            str(CONFORMANCE / "tests/fake_tool_daemon.py"),
            *(extra_daemon_args or []),
        ]
        return subprocess.run(
            [
                sys.executable,
                str(CONFORMANCE / "tool_runner.py"),
                "--case",
                case_name,
                "--timeout",
                "8",
                "--daemon-command-json",
                json.dumps(daemon_command),
            ],
            cwd=ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )


if __name__ == "__main__":
    unittest.main()
