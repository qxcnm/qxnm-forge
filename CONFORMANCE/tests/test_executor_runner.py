from __future__ import annotations

from copy import deepcopy
import json
from pathlib import Path
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
CONFORMANCE = ROOT / "CONFORMANCE"
SCHEMAS = ROOT / "SPEC/schemas"
sys.path.insert(0, str(CONFORMANCE))

import executor_runner  # noqa: E402

try:
    import jsonschema
    from referencing import Registry, Resource
except ImportError:  # pragma: no cover - optional developer gate
    jsonschema = None


class ExecutorRunnerTests(unittest.TestCase):
    """executor fixture、helper、安全门禁和 terminal Schema 单元测试。"""

    @classmethod
    def setUpClass(cls) -> None:
        """功能：加载规范 fixture 并建立跨文件 Draft 2020-12 registry。

        输入：仓库内固定 Schema 与 executor fixture。
        输出：供所有测试复用的 fixture 和 registry。
        不变量：缺少可选 jsonschema 时只跳过 Schema 测试，不伪造通过。
        失败：fixture 自身不可读时让测试类初始化失败。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        cls.fixture = executor_runner.strict_load(executor_runner.FIXTURE)
        if jsonschema is None:
            cls.registry = None
            return
        resources: list[tuple[str, Resource[object]]] = []
        for path in sorted(SCHEMAS.rglob("*.schema.json")):
            schema = json.loads(path.read_text(encoding="utf-8"))
            resources.append((schema["$id"], Resource.from_contents(schema)))
        cls.registry = Registry().with_resources(resources)

    def validator(self, schema_name: str, reference: str) -> object:
        """功能：创建指向一个规范 `$defs` 的跨文件 Schema validator。

        输入：Schema 相对名和 fragment reference。
        输出：Draft202012Validator。
        不变量：只读取仓库 Schema，不访问网络 `$ref`。
        失败：jsonschema 不可用时跳过当前测试。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if jsonschema is None or self.registry is None:
            self.skipTest("optional jsonschema package is unavailable")
        schema = json.loads((SCHEMAS / schema_name).read_text(encoding="utf-8"))
        return jsonschema.Draft202012Validator(
            {
                "$schema": schema["$schema"],
                "$ref": schema["$id"] + reference,
            },
            registry=self.registry,
        )

    def terminal_event(
        self, seq: int, event_type: str, data: dict[str, object]
    ) -> dict[str, object]:
        """功能：构造 trace validator 单元测试使用的最小 terminal notification。

        输入：正序列号、公共 terminal event type 与对应 data 对象。
        输出：固定 Session/terminal 身份的 JSON-RPC notification。
        不变量：时间与 envelope 符合公共 Schema，不隐藏额外字段。
        失败：纯内存 fixture 构造，无 I/O；无效组合由被测 validator 拒绝。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        return {
            "jsonrpc": "2.0",
            "method": "terminal/event",
            "params": {
                "sessionId": "session-one",
                "terminalId": "terminal-one",
                "seq": seq,
                "time": "2026-07-14T02:03:04Z",
                "type": event_type,
                "data": data,
            },
        }

    @staticmethod
    def canonical_wire_size(frame: dict[str, object]) -> int:
        """功能：为纯内存 notification 提供与紧凑 UTF-8 wire 一致的字节数。

        输入：单测自行构造、未经过 DaemonProcess 的 JSON 对象帧。
        输出：不含 NDJSON 换行的紧凑 UTF-8 payload 字节数。
        不变量：只供 validator 单测注入；真实 daemon 必须使用实际 stdout 字节账本。
        失败：对象不可 JSON 序列化时传播 TypeError/ValueError。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        return len(
            json.dumps(frame, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        )

    def test_static_fixture_schema_and_security_semantics(self) -> None:
        """功能：确认规范 executor fixture 同时通过 Schema 和离线安全语义门禁。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        executor_runner.validate_schema(self.fixture, SCHEMAS)
        executor_runner.validate_semantics(self.fixture, executor_runner.HELPER)

    def test_security_gate_rejects_arbitrary_shell_script(self) -> None:
        """功能：确认 fixture 不能把固定 shell 案例替换成任意网络命令。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        changed = deepcopy(self.fixture)
        changed["shellCases"][0]["script"] = "curl https://example.invalid"
        with self.assertRaises(executor_runner.ExecutorRunnerError):
            executor_runner.validate_semantics(changed, executor_runner.HELPER)

    def test_helper_rejects_workspace_escape_marker(self) -> None:
        """功能：确认 descendant marker helper 拒绝 traversal 与绝对路径。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        for value in ("../escape", "/tmp/escape", "..", "nested/escape"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                module_path = CONFORMANCE / "fixtures/executor"
                sys.path.insert(0, str(module_path))
                try:
                    import executor_helper

                    executor_helper.safe_marker(value)
                finally:
                    sys.path.pop(0)

    def test_local_helper_preserves_argv_and_separated_streams(self) -> None:
        """功能：在临时 workspace 执行两个固定 helper 案例并核对 argv/双流。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with tempfile.TemporaryDirectory(prefix="executor-runner-test-") as temporary:
            workspace = Path(temporary)
            helper = workspace / "executor_helper.py"
            helper.write_bytes(executor_runner.HELPER.read_bytes())
            by_name = {case["name"]: case for case in self.fixture["processCases"]}
            executor_runner.run_local_process_case(
                by_name["argv_boundaries_and_nonzero"], helper, workspace
            )
            executor_runner.run_local_process_case(
                by_name["separated_streams_and_nonzero"], helper, workspace
            )

    def test_execution_schema_accepts_structured_result(self) -> None:
        """功能：确认完整 structured execution result 可嵌入 ToolResult。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        stream = {
            "encoding": "utf-8-replacement",
            "text": "ok",
            "capturedBytes": 2,
            "totalBytes": 2,
            "omittedBytes": 0,
            "truncated": False,
        }
        result = {
            "exitCode": 0,
            "signal": None,
            "terminationReason": "exit",
            "durationMs": 3,
            "containment": "unix_process_group_descendant_guard",
            "stdout": stream,
            "stderr": {**stream, "text": "", "capturedBytes": 0, "totalBytes": 0},
        }
        self.validator("executor.schema.json", "#/$defs/executionResult").validate(
            result
        )
        self.validator("domain/tool.schema.json", "#/$defs/toolResult").validate(
            {
                "content": [{"type": "text", "text": "ok"}],
                "isError": False,
                "terminationReason": "exit",
                "execution": result,
            }
        )

    def test_terminal_schema_rejects_pipe_masquerading_as_pty(self) -> None:
        """功能：确认 terminal/open result 只允许 Unix PTY 或 Windows ConPTY。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        result = {
            "terminalId": "terminal-one",
            "attachmentId": "attachment-one",
            "state": "running",
            "ptyKind": "unix_pty",
            "size": {"columns": 80, "rows": 24},
            "latestOutputSeq": 0,
        }
        validator = self.validator(
            "protocol/terminal.schema.json", "#/$defs/openResult"
        )
        validator.validate(result)
        result["ptyKind"] = "pipe"
        with self.assertRaises(jsonschema.ValidationError):
            validator.validate(result)

    def test_terminal_rpc_and_notification_match_jsonrpc_schema(self) -> None:
        """功能：确认 terminal/open 请求与 terminal/event notification 可由公共协议验证。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        request = {
            "jsonrpc": "2.0",
            "id": "terminal-open-1",
            "method": "terminal/open",
            "params": {
                "sessionId": "session-one",
                "executable": "python3",
                "args": ["executor_helper.py", "terminal"],
                "cwd": ".",
                "environment": [],
                "size": {"columns": 80, "rows": 24},
                "limits": {
                    "lifetimeMs": 10000,
                    "idleTimeoutMs": 5000,
                    "retentionBytes": 65536,
                    "inputBufferBytes": 65536,
                    "eventBytes": 16384,
                },
                "disconnectPolicy": "terminate",
            },
        }
        notification = {
            "jsonrpc": "2.0",
            "method": "terminal/event",
            "params": {
                "sessionId": "session-one",
                "terminalId": "terminal-one",
                "seq": 1,
                "time": "2026-07-14T02:03:04Z",
                "type": "terminal.output",
                "data": {"stream": "pty", "data": "hello\r\n", "byteCount": 7},
            },
        }
        self.validator(
            "protocol/jsonrpc.schema.json", "#/$defs/terminalOpenRequest"
        ).validate(request)
        self.validator(
            "protocol/terminal.schema.json", "#/$defs/notification"
        ).validate(notification)

    def test_terminal_probe_negotiates_terminal_events(self) -> None:
        """功能：确认 terminal 动态探针显式协商独立 terminal notification 通道。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        request = executor_runner.initialize_request("terminal-init", terminal=True)
        capabilities = request["params"]["capabilities"]
        self.assertIs(capabilities["terminalEvents"], True)

    def test_terminal_fixture_requires_replay_evidence_and_backpressure(self) -> None:
        """功能：确认动态案例先观察 live 文本再 attach，并包含独立 backpressure 动作。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        by_name = {case["name"]: case for case in self.fixture["terminalCases"]}
        replay_actions = by_name["attachment_replay_and_cleanup"]["actions"]
        self.assertEqual(["write", "await", "write", "await", "attach", "close"], [
            action["type"] for action in replay_actions
        ])
        self.assertEqual("replay-me", replay_actions[1]["contains"])
        self.assertEqual('"rows":', replay_actions[3]["contains"])
        pressure_actions = by_name["input_backpressure_and_atomic_rejection"][
            "actions"
        ]
        self.assertIn("backpressure", [action["type"] for action in pressure_actions])

    def test_terminal_trace_accepts_only_response_delimited_replay(self) -> None:
        """功能：确认旧 seq 仅在登记的 attach response 后 replay window 内合法。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        validator = executor_runner.terminal_notification_validator(SCHEMAS)
        output = self.terminal_event(
            1,
            "terminal.output",
            {"stream": "pty", "data": "ready\r\n", "byteCount": 7},
        )
        frames = [
            output,
            {"jsonrpc": "2.0", "id": "attach-1", "result": {}},
            deepcopy(output),
            self.terminal_event(
                2,
                "terminal.exited",
                {"exitCode": 0, "signal": None, "terminationReason": "exit"},
            ),
            self.terminal_event(3, "terminal.closed", {"reason": "client"}),
        ]
        executor_runner.validate_terminal_trace(
            frames,
            validator,
            session_id="session-one",
            terminal_id="terminal-one",
            replay_windows=[(2, 3, 0, 1)],
            max_event_bytes=262_144,
            output_chunk_bytes=16_384,
            require_closed=True,
            wire_size_for=self.canonical_wire_size,
        )
        with self.assertRaises(executor_runner.ExecutorRunnerError):
            executor_runner.validate_terminal_trace(
                frames,
                validator,
                session_id="session-one",
                terminal_id="terminal-one",
                replay_windows=[],
                max_event_bytes=262_144,
                output_chunk_bytes=16_384,
                require_closed=True,
                wire_size_for=self.canonical_wire_size,
            )
        forged = deepcopy(frames)
        forged[2]["params"]["data"]["data"] = "forged\r\n"
        forged[2]["params"]["data"]["byteCount"] = 8
        with self.assertRaises(executor_runner.ExecutorRunnerError):
            executor_runner.validate_terminal_trace(
                forged,
                validator,
                session_id="session-one",
                terminal_id="terminal-one",
                replay_windows=[(2, 3, 0, 1)],
                max_event_bytes=262_144,
                output_chunk_bytes=16_384,
                require_closed=True,
                wire_size_for=self.canonical_wire_size,
            )

    def test_terminal_trace_rejects_late_output_and_event_overflow(self) -> None:
        """功能：确认 exited 后 output 与超过广告 event budget 的 notification 均失败。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        validator = executor_runner.terminal_notification_validator(SCHEMAS)
        frames = [
            self.terminal_event(
                1,
                "terminal.exited",
                {"exitCode": 0, "signal": None, "terminationReason": "exit"},
            ),
            self.terminal_event(
                2,
                "terminal.output",
                {"stream": "pty", "data": "late", "byteCount": 4},
            ),
            self.terminal_event(3, "terminal.closed", {"reason": "client"}),
        ]
        with self.assertRaises(executor_runner.ExecutorRunnerError):
            executor_runner.validate_terminal_trace(
                frames,
                validator,
                session_id="session-one",
                terminal_id="terminal-one",
                replay_windows=[],
                max_event_bytes=262_144,
                output_chunk_bytes=16_384,
                require_closed=True,
                wire_size_for=self.canonical_wire_size,
            )
        valid = [
            self.terminal_event(
                1,
                "terminal.exited",
                {"exitCode": 0, "signal": None, "terminationReason": "exit"},
            ),
            self.terminal_event(2, "terminal.closed", {"reason": "client"}),
        ]
        with self.assertRaises(executor_runner.ExecutorRunnerError):
            executor_runner.validate_terminal_trace(
                valid,
                validator,
                session_id="session-one",
                terminal_id="terminal-one",
                replay_windows=[],
                max_event_bytes=32,
                output_chunk_bytes=16_384,
                require_closed=True,
                wire_size_for=self.canonical_wire_size,
            )

    def test_main_static_only_succeeds_without_daemon(self) -> None:
        """功能：确认静态 runner 可在不启动语言 daemon 时完成确定性门禁。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.assertEqual(0, executor_runner.main(["--skip-local"]))


if __name__ == "__main__":
    unittest.main()
