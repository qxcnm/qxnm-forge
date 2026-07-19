from __future__ import annotations

import json
from pathlib import Path
import subprocess
import sys
import unittest


CONFORMANCE = Path(__file__).resolve().parents[1]
ROOT = CONFORMANCE.parent
sys.path.insert(0, str(CONFORMANCE))

import runner  # noqa: E402


def load_fixture(relative: str) -> dict[str, object]:
    """功能：读取一个 UTF-8 JSON 测试夹具。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return json.loads((CONFORMANCE / "fixtures" / relative).read_text(encoding="utf-8"))


class TransportFixtureTests(unittest.TestCase):
    def test_ndjson_cases(self) -> None:
        """功能：逐项验证 NDJSON 拆包、错误和末帧夹具。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        fixture = load_fixture("transport/ndjson-cases.json")
        for case in fixture["cases"]:  # type: ignore[index]
            with self.subTest(case=case["name"]):
                decoder = runner.NDJSONDecoder()
                actual: list[object] = []
                if "error" in case:
                    with self.assertRaisesRegex(
                        runner.ProtocolViolation, case["error"]
                    ):
                        for chunk in case["chunksHex"]:
                            actual.extend(decoder.feed(bytes.fromhex(chunk)))
                        actual.extend(decoder.finish())
                else:
                    for chunk in case["chunksHex"]:
                        actual.extend(decoder.feed(bytes.fromhex(chunk)))
                    actual.extend(decoder.finish())
                    self.assertEqual(case["expected"], actual)

    def test_ndjson_reports_actual_wire_payload_bytes(self) -> None:
        """功能：确认 decoder 记录 JSON whitespace 与 CR，而不是重序列化后的大小。

        输入：一条带尾随 whitespace/CRLF 的合法对象帧和一条无 LF 尾帧。
        输出：callback 收到与返回对象同一身份及各自实际 payload 字节数。
        不变量：LF 是分隔符不计入；LF 前的可选 CR 与尾随 JSON whitespace 均计入。
        失败：字节数或对象身份漂移时测试失败。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        observed: list[tuple[dict[str, object], int]] = []

        def record(frame: dict[str, object], wire_bytes: int) -> None:
            """功能：收集 decoder callback 的对象身份与实际 payload 字节数。

            输入：刚解析的对象帧与 LF 前字节数。
            输出：追加到测试私有列表。
            不变量：不复制或重序列化 frame。
            失败：仅由 list.append 的内存错误传播。
            作者：高宏顺
            邮箱：18272669457@163.com
            """

            observed.append((frame, wire_bytes))

        first_raw = b'{"a":1}   \r\n'
        final_raw = b'{"b":2} '
        decoder = runner.NDJSONDecoder(frame_bytes_callback=record)
        frames = decoder.feed(first_raw)
        frames.extend(decoder.feed(final_raw))
        frames.extend(decoder.finish())

        self.assertEqual([{"a": 1}, {"b": 2}], frames)
        self.assertIs(observed[0][0], frames[0])
        self.assertIs(observed[1][0], frames[1])
        self.assertEqual(len(first_raw) - 1, observed[0][1])
        self.assertEqual(len(final_raw), observed[1][1])

    def test_partial_json_cases(self) -> None:
        """功能：逐项验证 partial JSON 参数流夹具。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        fixture = load_fixture("transport/partial-json-cases.json")
        for case in fixture["cases"]:  # type: ignore[index]
            with self.subTest(case=case["name"]):
                decoder = runner.PartialJSONDecoder()
                actual: list[object] = []
                if "error" in case:
                    with self.assertRaisesRegex(
                        runner.ProtocolViolation, case["error"]
                    ):
                        for chunk in case["chunksHex"]:
                            actual.extend(decoder.feed(bytes.fromhex(chunk)))
                        actual.extend(decoder.finish())
                else:
                    for chunk in case["chunksHex"]:
                        actual.extend(decoder.feed(bytes.fromhex(chunk)))
                    actual.extend(decoder.finish())
                    self.assertEqual(case["expected"], actual)

    def test_sse_cases(self) -> None:
        """功能：逐项验证 SSE 换行、UTF-8 和多行数据夹具。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        fixture = load_fixture("transport/sse-cases.json")
        for case in fixture["cases"]:  # type: ignore[index]
            with self.subTest(case=case["name"]):
                decoder = runner.SSEDecoder()
                actual: list[object] = []
                if "error" in case:
                    with self.assertRaisesRegex(
                        runner.ProtocolViolation, case["error"]
                    ):
                        for chunk in case["chunksHex"]:
                            actual.extend(decoder.feed(bytes.fromhex(chunk)))
                        actual.extend(decoder.finish())
                else:
                    for chunk in case["chunksHex"]:
                        actual.extend(decoder.feed(bytes.fromhex(chunk)))
                    actual.extend(decoder.finish())
                    self.assertEqual(case["expected"], actual)


class TraceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        """功能：准备 golden 请求、期望轨迹和假 daemon 路径。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        cls.requests = CONFORMANCE / "fixtures/golden/hello.requests.ndjson"
        cls.golden = CONFORMANCE / "fixtures/golden/hello.trace.ndjson"
        cls.daemon = CONFORMANCE / "tests/fake_daemon.py"

    def test_end_to_end_golden(self) -> None:
        """功能：验证 9 帧成功轨迹的完整黑盒流程。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        normalized = runner.run_conformance(
            [sys.executable, str(self.daemon)],
            self.requests,
            self.golden,
            timeout=2.0,
            settle_seconds=0.02,
        )
        self.assertEqual(9, len(normalized))

    def test_duplicate_sequence_fails(self) -> None:
        """功能：确认重复事件序号被判为协议违规。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with self.assertRaisesRegex(runner.ProtocolViolation, "strictly increasing"):
            runner.run_conformance(
                [sys.executable, str(self.daemon), "--bad-seq"],
                self.requests,
                self.golden,
                timeout=2.0,
                settle_seconds=0.02,
            )

    def test_stdout_diagnostic_fails_transport(self) -> None:
        """功能：确认 stdout 污染诊断含安全上下文且不泄露后续 argv。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        secret_argument = "argv-canary-must-not-leak"
        with self.assertRaises(runner.ProtocolViolation) as raised:
            runner.run_conformance(
                [
                    sys.executable,
                    str(self.daemon),
                    "--stdout-log",
                    "--diagnostic-token",
                    secret_argument,
                ],
                self.requests,
                self.golden,
                timeout=2.0,
                settle_seconds=0.02,
            )
        diagnostic = str(raised.exception)
        self.assertIn("invalid JSON", diagnostic)
        self.assertIn(f"executable='{Path(sys.executable).name}'", diagnostic)
        self.assertIn("safe stderr marker for invalid stdout", diagnostic)
        self.assertNotIn(secret_argument, diagnostic)

    def test_missing_executable_diagnostic_omits_later_argv(self) -> None:
        """功能：确认启动失败只报告 executable 标签而不回显敏感 argv。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        executable = "qxnm-forge-daemon-that-does-not-exist"
        secret_argument = "missing-command-canary-must-not-leak"
        with self.assertRaises(runner.ConformanceError) as raised:
            runner.run_conformance(
                [executable, "--token", secret_argument],
                self.requests,
                self.golden,
                timeout=2.0,
                settle_seconds=0.02,
            )
        diagnostic = str(raised.exception)
        self.assertIn(f"executable='{executable}'", diagnostic)
        self.assertNotIn(secret_argument, diagnostic)

    def test_event_after_terminal_fails(self) -> None:
        """功能：确认终态后的任何 run 事件都被拒绝。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with self.assertRaisesRegex(runner.ProtocolViolation, "after terminal"):
            runner.run_conformance(
                [sys.executable, str(self.daemon), "--event-after-terminal"],
                self.requests,
                self.golden,
                timeout=2.0,
                settle_seconds=0.05,
            )

    def test_cli(self) -> None:
        """功能：验证 runner CLI 的成功退出码和摘要输出。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        completed = subprocess.run(
            [
                sys.executable,
                str(CONFORMANCE / "runner.py"),
                "--requests",
                str(self.requests),
                "--golden",
                str(self.golden),
                "--timeout",
                "2",
                "--",
                sys.executable,
                str(self.daemon),
            ],
            cwd=ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertIn("PASS: 9 frames conform", completed.stdout)

    def test_normalizer_preserves_references(self) -> None:
        """功能：确认同一 opaque ID 的多次引用保持相同规范化值。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        frames = [
            {"runId": "opaque-a", "data": {"messageId": "opaque-m"}},
            {"runId": "opaque-a", "data": {"messageId": "opaque-m"}},
        ]
        self.assertEqual(
            [
                {"runId": "run-1", "data": {"messageId": "message-1"}},
                {"runId": "run-1", "data": {"messageId": "message-1"}},
            ],
            runner.TraceNormalizer().normalize(frames),
        )

    def test_normalizer_preserves_deterministic_scenario_id(self) -> None:
        """功能：确认 faux:<name> 场景 ID 不被 opaque ID 规范化覆盖。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        frames = [{"result": {"scenarioId": "faux:hello-v1"}}]
        self.assertEqual(frames, runner.TraceNormalizer().normalize(frames))

    def test_normalizer_uses_honest_minimum_capability_baseline(self) -> None:
        """功能：确认实现能力差异先验证后归一，不强迫广告计划中 Provider 或工具。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        trace = runner.load_ndjson(self.golden)
        trace[0]["result"]["capabilities"] = {
            "methods": ["initialize", "faux/configure", "run/start", "run/cancel"],
            "eventTypes": [
                "run.started",
                "turn.started",
                "message.started",
                "message.delta",
                "message.completed",
                "run.completed",
                "run.failed",
                "run.cancelled",
                "run.interrupted",
            ],
            "providers": [
                {"id": "faux", "models": ["faux-v1"]},
                {"id": "anthropic", "models": []},
            ],
            "tools": ["file.read"],
            "transports": ["stdio"],
        }
        normalized = runner.TraceNormalizer().normalize(trace)
        self.assertEqual(
            runner.NORMALIZED_SERVER_CAPABILITIES,
            normalized[0]["result"]["capabilities"],
        )

    def test_initialize_rejects_missing_exercised_faux_capability(self) -> None:
        """功能：确认 runner 拒绝未声明本轨迹实际调用的 faux Provider。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        requests = runner.load_ndjson(self.requests)
        trace = runner.load_ndjson(self.golden)
        trace[0]["result"]["capabilities"]["providers"] = [
            {"id": "anthropic", "models": []}
        ]
        for frame in trace[3:]:
            frame["params"]["time"] = "2026-07-10T08:00:00Z"
        with self.assertRaisesRegex(runner.ProtocolViolation, "faux/faux-v1"):
            runner.TraceValidator(requests).validate(trace)

    def test_initialize_rejects_undeclared_emitted_event(self) -> None:
        """功能：确认 daemon 不能发送 initialize 未声明的事件类型。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        requests = runner.load_ndjson(self.requests)
        trace = runner.load_ndjson(self.golden)
        trace[0]["result"]["capabilities"]["eventTypes"].remove("message.delta")
        for frame in trace[3:]:
            frame["params"]["time"] = "2026-07-10T08:00:00Z"
        with self.assertRaisesRegex(runner.ProtocolViolation, "was not declared"):
            runner.TraceValidator(requests).validate(trace)

    def test_event_time_requires_rfc3339_utc(self) -> None:
        """功能：确认事件时间必须是带尾随 Z 的 RFC 3339 UTC。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        for invalid in (
            "2026-07-10 08:00:00+00:00",
            "2026-07-10T16:00:00+08:00",
            "2026-07-10T08:00:00",
        ):
            with (
                self.subTest(invalid=invalid),
                self.assertRaisesRegex(runner.ProtocolViolation, "RFC 3339 UTC"),
            ):
                runner._parse_utc_time(invalid)

    def test_sequence_requires_positive_safe_integer(self) -> None:
        """功能：确认事件序号 0 和超出安全整数范围的值均被拒绝。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        requests = runner.load_ndjson(self.requests)
        for invalid in (0, 9_007_199_254_740_992):
            trace = runner.load_ndjson(self.golden)
            trace[1]["result"]["scenarioId"] = "faux:hello-v1"
            trace[3]["params"]["seq"] = invalid
            for frame in trace[3:]:
                frame["params"]["time"] = "2026-07-10T08:00:00Z"
            with (
                self.subTest(invalid=invalid),
                self.assertRaisesRegex(
                    runner.ProtocolViolation, "positive safe integer"
                ),
            ):
                runner.TraceValidator(requests).validate(trace)


class FixtureSanityTests(unittest.TestCase):
    def test_all_json_fixtures_are_strict_json(self) -> None:
        """功能：确认所有 JSON 夹具均通过严格解析。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        for path in sorted((CONFORMANCE / "fixtures").rglob("*.json")):
            with self.subTest(path=path.relative_to(CONFORMANCE)):
                value = runner.strict_json_loads(path.read_text(encoding="utf-8"))
                self.assertIsInstance(value, dict)

    def test_golden_fixture_validates(self) -> None:
        """功能：确认 golden 自身满足公共协议生命周期不变量。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        requests = runner.load_ndjson(
            CONFORMANCE / "fixtures/golden/hello.requests.ndjson"
        )
        trace = runner.load_ndjson(CONFORMANCE / "fixtures/golden/hello.trace.ndjson")
        for frame in trace:
            if frame.get("method") == "event":
                frame["params"]["time"] = "2026-07-10T08:00:00Z"
        runner.TraceValidator(requests).validate(trace)


if __name__ == "__main__":
    unittest.main()
