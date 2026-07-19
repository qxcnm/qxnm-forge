from __future__ import annotations

import argparse
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

import provider_mock  # noqa: E402
import provider_runner  # noqa: E402


class ProviderRunnerStaticTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        """功能：加载共享夹具供 provider runner 静态与安全测试使用。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        cls.fixture = provider_mock.load_fixture()

    def test_static_gate_without_optional_schema_engine(self) -> None:
        """功能：确认默认静态入口无需 daemon、凭据或第三方包即可运行。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.assertEqual(
            (3, 21),
            provider_runner.run_static_gate(provider_mock.DEFAULT_FIXTURE, None),
        )

    @unittest.skipIf(
        importlib.util.find_spec("jsonschema") is None,
        "optional jsonschema package is unavailable",
    )
    def test_fixture_matches_provider_mock_schema(self) -> None:
        """功能：确认机器夹具通过 bundled Draft 2020-12 Provider mock Schema。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        provider_runner.validate_fixture_schema(provider_mock.DEFAULT_FIXTURE, SCHEMAS)

    @unittest.skipIf(
        importlib.util.find_spec("jsonschema") is None,
        "optional jsonschema package is unavailable",
    )
    def test_schema_rejects_legacy_single_request_tool_case(self) -> None:
        """功能：确认 Schema 拒绝缺少第二请求与 exactRequests 的旧首批工具案例。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        fixture = json.loads(provider_mock.DEFAULT_FIXTURE.read_text(encoding="utf-8"))
        family = fixture["families"][0]
        case = next(
            value
            for value in family["cases"]
            if value["name"] == "partial_tool_arguments"
        )
        case["attempts"] = case["attempts"][:1]
        case["expected"].pop("exactRequests")
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "legacy-provider-cases.json"
            path.write_text(json.dumps(fixture), encoding="utf-8")
            with self.assertRaises(provider_runner.ProviderProbeError):
                provider_runner.validate_fixture_schema(path, SCHEMAS)

    @unittest.skipIf(
        importlib.util.find_spec("jsonschema") is None,
        "optional jsonschema package is unavailable",
    )
    def test_dynamic_trace_schema_failure_never_echoes_instance_value(self) -> None:
        """功能：确认动态 trace 的 Schema 负例失败且不回显实例中的敏感值。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        canary = "protocol-schema-canary-must-not-be-echoed"
        family = provider_mock.family_by_id(self.fixture, "openai-compatible")
        case = provider_mock.case_by_name(family, "text_success")
        request = provider_runner._initialize_request()
        request["unexpected"] = canary
        with self.assertRaises(provider_runner.ProviderProbeError) as raised:
            provider_runner.verify_trace(
                [request],
                [],
                family,
                case,
                schema_root=SCHEMAS,
            )
        self.assertIn("protocol trace", str(raised.exception))
        self.assertNotIn(canary, str(raised.exception))

    @unittest.skipIf(
        importlib.util.find_spec("jsonschema") is None,
        "optional jsonschema package is unavailable",
    )
    def test_probe_journals_support_direct_and_rust_nested_layouts(self) -> None:
        """功能：确认动态门禁递归验证 .NET 式直接布局与 Rust sessions 布局。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        source = (
            CONFORMANCE / "fixtures/session/portable-v0.1/journal.before-recovery.jsonl"
        ).read_bytes()
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            direct = root / "dotnet/session-a/journal.jsonl"
            nested = root / "sessions/session-b/branches/main/journal.jsonl"
            direct.parent.mkdir(parents=True)
            nested.parent.mkdir(parents=True)
            direct.write_bytes(source)
            nested.write_bytes(source)
            count = provider_runner.validate_probe_journals(root, SCHEMAS)
        self.assertEqual(2, count)

    @unittest.skipIf(
        importlib.util.find_spec("jsonschema") is None,
        "optional jsonschema package is unavailable",
    )
    def test_probe_journal_schema_failure_is_located_and_redacted(self) -> None:
        """功能：确认 journal Schema 负例报告相对路径和行号但不回显内容。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        canary = "journal-schema-canary-must-not-be-echoed"
        value = {
            "kind": "session",
            "schemaVersion": "0.1",
            "sessionId": "session-negative-1",
            "createdAt": "2026-07-11T03:00:00Z",
            "workspace": "/workspace",
            "unexpected": canary,
        }
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            path = root / "sessions/session-negative/journal.jsonl"
            path.parent.mkdir(parents=True)
            path.write_text(
                json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n",
                encoding="utf-8",
            )
            with self.assertRaises(provider_runner.ProviderProbeError) as raised:
                provider_runner.validate_probe_journals(root, SCHEMAS)
        message = str(raised.exception)
        self.assertIn("sessions/session-negative/journal.jsonl:1", message)
        self.assertIn("Schema violation", message)
        self.assertNotIn(canary, message)

    def test_command_json_is_argv_only_and_never_shell_text(self) -> None:
        """功能：确认 daemon 命令只接受安全 JSON argv 数组而非 shell 字符串。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.assertEqual(
            ["daemon", "--flag", "value with spaces"],
            provider_runner.parse_command_json(
                '["daemon","--flag","value with spaces"]'
            ),
        )
        with self.assertRaises(argparse.ArgumentTypeError):
            provider_runner.parse_command_json("daemon --flag $(unsafe)")
        with self.assertRaises(argparse.ArgumentTypeError):
            provider_runner.parse_command_json('["daemon", "bad\\u0000arg"]')

    def test_probe_templates_use_isolated_seeded_workspace_without_shell_expansion(
        self,
    ) -> None:
        """功能：确认每案例工作区/stateRoot 独占、README 固定且占位符仅字面替换。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            workspace, state_root = provider_runner._prepare_probe_directories(root)
            expanded = provider_runner._expand_probe_command(
                [
                    "daemon",
                    "--workspace={workspace}",
                    "--state-dir",
                    "{stateRoot}",
                    "$(must-not-run)",
                ],
                workspace,
                state_root,
            )
            self.assertEqual(
                "provider tool continuation fixture\n",
                (workspace / "README.md").read_text(encoding="utf-8"),
            )
            self.assertEqual(root, workspace.parent)
            self.assertEqual(root, state_root.parent)
            self.assertEqual(f"--workspace={workspace}", expanded[1])
            self.assertEqual(str(state_root), expanded[3])
            self.assertEqual("$(must-not-run)", expanded[4])

    def test_runtime_environment_targets_exact_loopback_and_dead_proxy(self) -> None:
        """功能：确认动态探针只注入精确回环 endpoint、合成凭据和死代理。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        family = provider_mock.family_by_id(self.fixture, "openai-compatible")
        environment = provider_runner._probe_environment(
            family,
            "http://127.0.0.1:43210/openai-compatible/text_success",
            "in-memory-canary",
            Path("/temporary/session-root"),
        )
        self.assertEqual("1", environment["QXNM_FORGE_PROVIDER_CONFORMANCE"])
        self.assertEqual("127.0.0.1", environment["NO_PROXY"])
        self.assertEqual("http://127.0.0.1:9", environment["HTTPS_PROXY"])
        self.assertTrue(
            environment["QXNM_FORGE_OPENAI_COMPATIBLE_ENDPOINT"].startswith(
                "http://127.0.0.1:43210/"
            )
        )

    def test_tool_lifecycle_requires_successful_execution_before_final_text(
        self,
    ) -> None:
        """功能：确认工具续轮只接受唯一成功结果及 requested→started→completed→文本终态顺序。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        expected = {
            "id": "call_mock_1",
            "name": "file.read",
            "arguments": {"path": "README.md"},
        }
        expected_result = {
            "content": [
                {
                    "type": "text",
                    "text": "provider tool continuation fixture\n",
                }
            ],
            "isError": False,
        }
        frames = [
            {
                "method": "event",
                "params": {
                    "type": "tool.requested",
                    "data": {
                        "toolCallId": "call_mock_1",
                        "name": "file.read",
                        "arguments": {"path": "README.md"},
                    },
                },
            },
            {
                "method": "event",
                "params": {
                    "type": "tool.started",
                    "data": {"toolCallId": "call_mock_1", "name": "file.read"},
                },
            },
            {
                "method": "event",
                "params": {
                    "type": "tool.completed",
                    "data": {
                        "toolCallId": "call_mock_1",
                        "result": {
                            "content": expected_result["content"],
                            "isError": False,
                        },
                    },
                },
            },
            {
                "method": "event",
                "params": {
                    "type": "message.delta",
                    "data": {
                        "messageId": "message-2",
                        "delta": {"type": "text", "text": "done"},
                    },
                },
            },
            {
                "method": "event",
                "params": {
                    "type": "run.completed",
                    "data": {
                        "status": "completed",
                        "usage": {
                            "inputTokens": 14,
                            "outputTokens": 10,
                            "totalTokens": 24,
                        },
                    },
                },
            },
        ]
        provider_runner._verify_tool_lifecycle(frames, expected, expected_result)
        provider_runner._verify_usage(
            frames,
            {"inputTokens": 14, "outputTokens": 10, "totalTokens": 24},
        )
        invalid = json.loads(json.dumps(frames))
        invalid[2]["params"]["data"]["result"]["isError"] = True
        with self.assertRaises(provider_runner.ProviderProbeError):
            provider_runner._verify_tool_lifecycle(invalid, expected, expected_result)

    def test_tool_observations_reject_extra_request_and_false_native_shape(
        self,
    ) -> None:
        """功能：确认工具案例精确限制两次请求且第二次所有原生形状布尔值为真。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        family = provider_mock.family_by_id(self.fixture, "openai-compatible")
        case = provider_mock.case_by_name(family, "partial_tool_arguments")
        first = {
            "family": "openai-compatible",
            "case": "partial_tool_arguments",
            "attempt": 1,
            "requestValid": True,
            "continuationShape": {"required": False},
        }
        second = {
            "family": "openai-compatible",
            "case": "partial_tool_arguments",
            "attempt": 2,
            "requestValid": True,
            "continuationShape": {
                "required": True,
                "nativeToolCallExact": True,
                "nativeToolResultCorrelated": True,
                "nativeToolResultNonEmpty": True,
                "nativeToolResultSuccessful": True,
                "nativeToolResultExact": True,
                "toolCallPrecedesResult": True,
            },
        }
        provider_runner._verify_request_observations([first, second], family, case)
        with self.assertRaises(provider_runner.ProviderProbeError):
            provider_runner._verify_request_observations(
                [first, second, second], family, case
            )
        invalid_second = json.loads(json.dumps(second))
        invalid_second["continuationShape"]["nativeToolResultCorrelated"] = False
        with self.assertRaises(provider_runner.ProviderProbeError):
            provider_runner._verify_request_observations(
                [first, invalid_second], family, case
            )

    def test_session_scan_detects_canary_without_echoing_it(self) -> None:
        """功能：确认临时 journal/artifact 出现 canary 时失败但诊断不回显值。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        canary = "journal-canary-must-never-be-echoed"
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "journal.jsonl"
            path.write_text(f'{{"unexpected":"{canary}"}}\n', encoding="utf-8")
            with self.assertRaises(provider_runner.ProviderProbeError) as raised:
                provider_runner._scan_tree_for_canary(Path(temporary), canary)
        self.assertNotIn(canary, str(raised.exception))
        self.assertIn("session artifact", str(raised.exception))

    def test_static_cli_reports_counts(self) -> None:
        """功能：确认无 daemon 的 CLI 只运行静态门禁并输出明确计数。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        completed = subprocess.run(
            [
                sys.executable,
                str(CONFORMANCE / "provider_runner.py"),
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
        self.assertIn("3 families / 21 cases", completed.stdout)

    def test_missing_daemon_executable_does_not_echo_sensitive_argv(self) -> None:
        """功能：确认 opt-in 探针启动失败不回显 JSON argv 中的敏感后续参数。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        executable = "provider-probe-daemon-that-does-not-exist"
        secret_argument = "provider-probe-argv-canary-must-not-leak"
        command_json = json.dumps([executable, "--token", secret_argument])
        completed = subprocess.run(
            [
                sys.executable,
                str(CONFORMANCE / "provider_runner.py"),
                "--family",
                "openai-compatible",
                "--case",
                "text_success",
                "--timeout",
                "1",
                "--daemon-command-json",
                command_json,
            ],
            cwd=ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        output = completed.stdout + completed.stderr
        self.assertEqual(1, completed.returncode, output)
        self.assertIn(f"executable='{executable}'", output)
        self.assertNotIn(secret_argument, output)

    def test_opt_in_probe_drives_mock_and_normalized_trace(self) -> None:
        """功能：确认 opt-in runner 可端到端驱动 daemon、mock 和 normalized 断言。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        command_json = json.dumps(
            [
                sys.executable,
                str(CONFORMANCE / "tests/fake_provider_daemon.py"),
            ]
        )
        completed = subprocess.run(
            [
                sys.executable,
                str(CONFORMANCE / "provider_runner.py"),
                "--family",
                "openai-compatible",
                "--case",
                "text_success",
                "--timeout",
                "3",
                "--schema-root",
                str(SCHEMAS),
                "--daemon-command-json",
                command_json,
            ],
            cwd=ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertIn("PASS: openai-compatible/text_success", completed.stdout)
        self.assertIn("PASS: provider daemon probes 1", completed.stdout)

    def test_cancellation_waits_until_mock_received_first_request(self) -> None:
        """功能：确认 runner 等首个 HTTP 观测后才取消，消除零请求竞态。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        command_json = json.dumps(
            [
                sys.executable,
                str(CONFORMANCE / "tests/fake_provider_daemon.py"),
            ]
        )
        completed = subprocess.run(
            [
                sys.executable,
                str(CONFORMANCE / "provider_runner.py"),
                "--family",
                "openai-compatible",
                "--case",
                "cancellation",
                "--timeout",
                "3",
                "--daemon-command-json",
                command_json,
            ],
            cwd=ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertIn("PASS: openai-compatible/cancellation", completed.stdout)


if __name__ == "__main__":
    unittest.main()
