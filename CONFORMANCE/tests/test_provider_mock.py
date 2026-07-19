from __future__ import annotations

import http.client
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import time
import unittest
from urllib.parse import urlsplit


CONFORMANCE = Path(__file__).resolve().parents[1]
ROOT = CONFORMANCE.parent
sys.path.insert(0, str(CONFORMANCE))

import provider_mock  # noqa: E402
import runner  # noqa: E402


EXPECTED_TOOL_TEXT = "provider tool continuation fixture\n"


def request_document(family: dict[str, object]) -> dict[str, object]:
    """功能：按 family 生成不含敏感值且满足形状契约的 Provider 请求。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    family_id = family["id"]
    document: dict[str, object] = {
        "model": family["modelId"],
        "stream": True,
    }
    input_schema: dict[str, object] = {
        "type": "object",
        "properties": {"path": {"type": "string"}},
        "required": ["path"],
        "additionalProperties": False,
    }
    if family_id == "openai-responses":
        document["input"] = [{"role": "user", "content": "fixture input"}]
        document["tools"] = [
            {
                "type": "function",
                "name": "file.read",
                "parameters": input_schema,
            }
        ]
    else:
        document["messages"] = [{"role": "user", "content": "fixture input"}]
    if family_id == "anthropic-messages":
        document["max_tokens"] = 32
        document["tools"] = [{"name": "file.read", "input_schema": input_schema}]
    elif family_id == "openai-compatible":
        document["tools"] = [
            {
                "type": "function",
                "function": {
                    "name": "file.read",
                    "parameters": input_schema,
                },
            }
        ]
    return document


def request_headers(family: dict[str, object], credential: str) -> dict[str, str]:
    """功能：按 family 生成测试用认证和必需版本 header。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    headers = {"Content-Type": "application/json"}
    if family["id"] == "anthropic-messages":
        headers["x-api-key"] = credential
        headers["anthropic-version"] = "2023-06-01"
    else:
        headers["Authorization"] = f"Bearer {credential}"
    return headers


def continuation_document(family: dict[str, object]) -> dict[str, object]:
    """功能：构造含固定原生 tool call 与非空成功 tool result 的第二轮请求。

    输入：首批 family 机器对象。
    输出：在普通工具声明请求上追加对应 Chat/Responses/Anthropic 历史的请求对象。
    不变量：调用 ID、工具名和参数与 mock 首轮响应精确一致，结果正文仅为合成测试值。
    失败：未知 family ID 时抛出 AssertionError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    document = request_document(family)
    family_id = family["id"]
    if family_id == "openai-compatible":
        document["messages"] = [
            {"role": "user", "content": "fixture input"},
            {
                "role": "assistant",
                "content": "",
                "tool_calls": [
                    {
                        "id": "call_mock_1",
                        "type": "function",
                        "function": {
                            "name": "file.read",
                            "arguments": '{"path":"README.md"}',
                        },
                    }
                ],
            },
            {
                "role": "tool",
                "tool_call_id": "call_mock_1",
                "content": EXPECTED_TOOL_TEXT,
            },
        ]
    elif family_id == "openai-responses":
        document["input"] = [
            {"role": "user", "content": "fixture input"},
            {
                "type": "function_call",
                "call_id": "call_mock_1",
                "name": "file.read",
                "arguments": '{"path":"README.md"}',
            },
            {
                "type": "function_call_output",
                "call_id": "call_mock_1",
                "output": EXPECTED_TOOL_TEXT,
            },
        ]
    elif family_id == "anthropic-messages":
        document["messages"] = [
            {"role": "user", "content": [{"type": "text", "text": "fixture input"}]},
            {
                "role": "assistant",
                "content": [
                    {
                        "type": "tool_use",
                        "id": "call_mock_1",
                        "name": "file.read",
                        "input": {"path": "README.md"},
                    }
                ],
            },
            {
                "role": "user",
                "content": [
                    {
                        "type": "tool_result",
                        "tool_use_id": "call_mock_1",
                        "content": EXPECTED_TOOL_TEXT,
                        "is_error": False,
                    }
                ],
            },
        ]
    else:
        raise AssertionError("unknown baseline provider family")
    return document


def post_case(
    server: provider_mock.ProviderMockServer,
    family: dict[str, object],
    case_name: str,
    *,
    credential: str = "synthetic-test-credential",
    suffix: str = "",
    timeout: float = 2.0,
    document: dict[str, object] | None = None,
) -> tuple[int, dict[str, str], bytes]:
    """功能：直接向回环 mock POST 一个案例并完整读取状态、header 与 body。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    family_id = family["id"]
    if not isinstance(family_id, str):
        raise AssertionError("family id must be a string")
    parsed = urlsplit(server.endpoint(family_id, case_name) + suffix)
    connection = http.client.HTTPConnection(
        parsed.hostname, parsed.port, timeout=timeout
    )
    try:
        connection.request(
            "POST",
            parsed.path,
            body=json.dumps(
                document if document is not None else request_document(family)
            ),
            headers=request_headers(family, credential),
        )
        response = connection.getresponse()
        body = response.read()
        headers = {name.casefold(): value for name, value in response.getheaders()}
        return response.status, headers, body
    finally:
        connection.close()


class ProviderMockFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        """功能：加载一次共享 Provider mock 夹具供纯静态测试使用。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        cls.fixture = provider_mock.load_fixture()

    def test_static_self_test_covers_three_families_and_twenty_one_cases(self) -> None:
        """功能：确认静态门禁完整覆盖三 family 各七个案例。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.assertEqual((3, 21), provider_mock.static_self_test())

    def test_text_wire_has_crlf_multiline_sse_and_unicode(self) -> None:
        """功能：确认三 family 文本流覆盖 CRLF、多行 data、Unicode 和 EOF 尾事件。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        for family_value in self.fixture["families"]:
            family = family_value
            self.assertIsInstance(family, dict)
            case = provider_mock.case_by_name(family, "text_success")
            attempt = case["attempts"][0]
            self.assertIsInstance(attempt, dict)
            wire = provider_mock.render_stream(family["id"], attempt)
            with self.subTest(family=family["id"]):
                self.assertIn(b"\r\n", wire)
                self.assertIn(b"\r\ndata:", wire)
                self.assertIn("你好".encode(), wire)
                self.assertIn("👋".encode(), wire)
                self.assertTrue(wire.endswith(b"\r\n"))
                self.assertFalse(wire.endswith(b"\r\n\r\n"))
                self.assertIs(attempt.get("finalEventWithoutBlankLine"), True)

    def test_fragmentation_crosses_utf8_continuation_bytes(self) -> None:
        """功能：确认机器 chunkSizes 确实在 UTF-8 continuation byte 前形成边界。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        family = provider_mock.family_by_id(self.fixture, "openai-compatible")
        case = provider_mock.case_by_name(family, "text_success")
        attempt = case["attempts"][0]
        wire = provider_mock.render_stream("openai-compatible", attempt)
        chunks = provider_mock.split_bytes(wire, attempt["chunkSizes"])
        self.assertEqual(wire, b"".join(chunks))
        self.assertTrue(
            any(chunk and 0x80 <= chunk[0] <= 0xBF for chunk in chunks),
            "fixture must split before a UTF-8 continuation byte",
        )

    def test_partial_tool_arguments_use_native_wire_events(self) -> None:
        """功能：确认三 family 均使用原生事件流分三段发送工具参数。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        expected_markers = {
            "openai-compatible": b"tool_calls",
            "openai-responses": b"response.function_call_arguments.delta",
            "anthropic-messages": b"input_json_delta",
        }
        for family_id, marker in expected_markers.items():
            family = provider_mock.family_by_id(self.fixture, family_id)
            case = provider_mock.case_by_name(family, "partial_tool_arguments")
            attempt = case["attempts"][0]
            wire = provider_mock.render_stream(family_id, attempt)
            with self.subTest(family=family_id):
                self.assertIn(marker, wire)
                self.assertIn(b"README", wire)
                self.assertIn(b".md", wire)

    def test_partial_tool_case_freezes_two_turns_events_and_cumulative_usage(
        self,
    ) -> None:
        """功能：确认首批工具案例固定两次请求、成功终态、生命周期和累计 usage。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        for family_value in self.fixture["families"]:
            self.assertIsInstance(family_value, dict)
            case = provider_mock.case_by_name(family_value, "partial_tool_arguments")
            with self.subTest(family=family_value["id"]):
                self.assertEqual(
                    ["tool_stream", "text_stream"],
                    [attempt["kind"] for attempt in case["attempts"]],
                )
                self.assertEqual("completed", case["expected"]["terminal"])
                self.assertEqual(2, case["expected"]["exactRequests"])
                self.assertEqual(
                    {"inputTokens": 14, "outputTokens": 10, "totalTokens": 24},
                    case["expected"]["usage"],
                )
                self.assertEqual(
                    {
                        "content": [{"type": "text", "text": EXPECTED_TOOL_TEXT}],
                        "isError": False,
                    },
                    case["expected"]["toolResult"],
                )
                self.assertEqual(
                    [
                        "tool.requested",
                        "tool.completed",
                        "message.delta",
                        "run.completed",
                    ],
                    case["expected"]["requiredEventTypes"],
                )

    def test_rendered_sse_decodes_after_machine_fragmentation(self) -> None:
        """功能：确认公共增量 SSE 解码器可消费三 family 的任意字节分片。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        for family_value in self.fixture["families"]:
            family = family_value
            self.assertIsInstance(family, dict)
            case = provider_mock.case_by_name(family, "text_success")
            attempt = case["attempts"][0]
            wire = provider_mock.render_stream(family["id"], attempt)
            decoder = runner.SSEDecoder()
            events: list[object] = []
            for chunk in provider_mock.split_bytes(wire, attempt["chunkSizes"]):
                events.extend(decoder.feed(chunk))
            events.extend(decoder.finish())
            with self.subTest(family=family["id"]):
                self.assertGreaterEqual(len(events), 4)


class ProviderMockHTTPTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        """功能：加载共享夹具供回环 HTTP 测试选择 family。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        cls.fixture = provider_mock.load_fixture()

    def test_success_requests_validate_auth_shape_and_api_suffix(self) -> None:
        """功能：确认三 family 的请求、认证与追加 API path 均被 mock 接受。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        canary = "http-canary-must-not-be-observed"
        with provider_mock.ProviderMockServer() as server:
            for family_value in self.fixture["families"]:
                family = family_value
                self.assertIsInstance(family, dict)
                status, headers, body = post_case(
                    server,
                    family,
                    "text_success",
                    credential=canary,
                    suffix="/v1/native-endpoint",
                )
                with self.subTest(family=family["id"]):
                    self.assertEqual(200, status)
                    self.assertIn("text/event-stream", headers["content-type"])
                    self.assertIn("你好".encode(), body)
            observations = server.state.observations()

        serialized = json.dumps(observations, ensure_ascii=False)
        self.assertNotIn(canary, serialized)
        for observation in observations:
            self.assertTrue(observation["requestValid"])
            self.assertTrue(all(item["present"] for item in observation["auth"]))
            self.assertTrue(all(item["prefixValid"] for item in observation["auth"]))

    def test_wrong_model_is_rejected_without_recording_value(self) -> None:
        """功能：确认固定字段不匹配返回 400 且观测不复制实际 model 值。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        family = provider_mock.family_by_id(self.fixture, "openai-compatible")
        bad_model = "model-value-must-not-be-observed"
        with provider_mock.ProviderMockServer() as server:
            parsed = urlsplit(server.endpoint("openai-compatible", "text_success"))
            document = request_document(family)
            document["model"] = bad_model
            connection = http.client.HTTPConnection(
                parsed.hostname, parsed.port, timeout=2
            )
            try:
                connection.request(
                    "POST",
                    parsed.path,
                    body=json.dumps(document),
                    headers=request_headers(family, "synthetic"),
                )
                response = connection.getresponse()
                response.read()
                self.assertEqual(400, response.status)
            finally:
                connection.close()
            observations = server.state.observations()
        self.assertNotIn(bad_model, json.dumps(observations))
        self.assertFalse(observations[0]["requestValid"])
        self.assertIn("mismatch:/model", observations[0]["validationErrors"])

    def test_rate_limit_retry_after_then_success(self) -> None:
        """功能：确认每个 family 的首次 429 携带 Retry-After: 0，随后成功。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with provider_mock.ProviderMockServer() as server:
            for family_value in self.fixture["families"]:
                family = family_value
                self.assertIsInstance(family, dict)
                first = post_case(server, family, "rate_limit_retry")
                second = post_case(server, family, "rate_limit_retry")
                with self.subTest(family=family["id"]):
                    self.assertEqual(429, first[0])
                    self.assertEqual("0", first[1]["retry-after"])
                    self.assertEqual(200, second[0])
                    self.assertIn(
                        b"text/event-stream", second[1]["content-type"].encode()
                    )

    def test_tool_continuation_accepts_only_correlated_family_native_second_request(
        self,
    ) -> None:
        """功能：确认三 family 第二请求仅接受唯一、关联、非空且成功的原生工具结果。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        canary = "native-tool-result-body-must-not-be-observed"
        with provider_mock.ProviderMockServer() as server:
            for family_value in self.fixture["families"]:
                self.assertIsInstance(family_value, dict)
                first = post_case(
                    server,
                    family_value,
                    "partial_tool_arguments",
                    credential=canary,
                )
                second = post_case(
                    server,
                    family_value,
                    "partial_tool_arguments",
                    credential=canary,
                    document=continuation_document(family_value),
                )
                with self.subTest(family=family_value["id"]):
                    self.assertEqual(200, first[0])
                    self.assertEqual(200, second[0])
                    self.assertIn(b"README", first[2])
                    self.assertIn("你好".encode(), second[2])
            observations = server.state.observations()

        self.assertEqual(6, len(observations))
        self.assertNotIn(canary, json.dumps(observations, ensure_ascii=False))
        for offset in range(0, len(observations), 2):
            first_shape = observations[offset]["continuationShape"]
            second_shape = observations[offset + 1]["continuationShape"]
            self.assertIs(first_shape["required"], False)
            self.assertIs(second_shape["required"], True)
            self.assertTrue(
                all(
                    second_shape[field]
                    for field in (
                        "nativeToolCallExact",
                        "nativeToolResultCorrelated",
                        "nativeToolResultNonEmpty",
                        "nativeToolResultSuccessful",
                        "nativeToolResultExact",
                        "toolCallPrecedesResult",
                    )
                )
            )
            self.assertNotIn(
                "provider tool continuation fixture",
                json.dumps(observations[offset + 1]),
            )

    def test_tool_continuation_rejects_wrong_call_id_without_recording_body(
        self,
    ) -> None:
        """功能：确认错误关联 ID 返回 400，观测仅含固定标签且不保存工具正文。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        family = provider_mock.family_by_id(self.fixture, "openai-responses")
        document = continuation_document(family)
        input_items = document["input"]
        self.assertIsInstance(input_items, list)
        output = input_items[-1]
        self.assertIsInstance(output, dict)
        output["call_id"] = "wrong-call-id"
        output["output"] = "body-canary-must-not-be-recorded"
        with provider_mock.ProviderMockServer() as server:
            self.assertEqual(
                200,
                post_case(server, family, "partial_tool_arguments")[0],
            )
            status, _, _ = post_case(
                server,
                family,
                "partial_tool_arguments",
                document=document,
            )
            observations = server.state.observations()
        self.assertEqual(400, status)
        serialized = json.dumps(observations, ensure_ascii=False)
        self.assertNotIn("body-canary-must-not-be-recorded", serialized)
        self.assertIn(
            "continuation:tool-result-correlation",
            observations[-1]["validationErrors"],
        )

    def test_tool_continuation_rejects_forged_result_body_without_recording_it(
        self,
    ) -> None:
        """功能：确认关联正确但正文伪造的工具结果失败，观测只暴露精确匹配布尔值。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        family = provider_mock.family_by_id(self.fixture, "openai-compatible")
        document = continuation_document(family)
        messages = document["messages"]
        self.assertIsInstance(messages, list)
        tool_message = messages[-1]
        self.assertIsInstance(tool_message, dict)
        forged = "forged-tool-result-must-not-be-observed"
        tool_message["content"] = forged
        with provider_mock.ProviderMockServer() as server:
            self.assertEqual(
                200,
                post_case(server, family, "partial_tool_arguments")[0],
            )
            status, _, _ = post_case(
                server,
                family,
                "partial_tool_arguments",
                document=document,
            )
            observations = server.state.observations()
        self.assertEqual(400, status)
        serialized = json.dumps(observations, ensure_ascii=False)
        self.assertNotIn(forged, serialized)
        self.assertIs(
            observations[-1]["continuationShape"]["nativeToolResultExact"],
            False,
        )
        self.assertIn(
            "continuation:tool-result-content",
            observations[-1]["validationErrors"],
        )

    def test_server_error_then_success(self) -> None:
        """功能：确认每个 family 的 503 HTTP-date 重试提示受理后成功。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with provider_mock.ProviderMockServer() as server:
            for family_value in self.fixture["families"]:
                family = family_value
                self.assertIsInstance(family, dict)
                first = post_case(server, family, "server_error_retry")
                second = post_case(server, family, "server_error_retry")
                with self.subTest(family=family["id"]):
                    self.assertEqual(503, first[0])
                    self.assertEqual(
                        "Wed, 21 Oct 2037 07:28:00 GMT",
                        first[1]["retry-after"],
                    )
                    self.assertEqual(200, second[0])

    def test_disconnect_closes_with_incomplete_event(self) -> None:
        """功能：确认断流案例发送完整前缀后以残缺 JSON 结束连接。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with provider_mock.ProviderMockServer() as server:
            for family_value in self.fixture["families"]:
                family = family_value
                self.assertIsInstance(family, dict)
                status, _, body = post_case(server, family, "disconnect")
                with self.subTest(family=family["id"]):
                    self.assertEqual(200, status)
                    self.assertIn(b"synthetic_incomplete", body)
                    self.assertFalse(body.endswith(b"\n\n"))

    def test_idle_timeout_and_client_cancellation_do_not_hang_shutdown(self) -> None:
        """功能：确认 idle 会触发客户端超时，主动断开后服务可立即安全关闭。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        family = provider_mock.family_by_id(self.fixture, "openai-compatible")
        started = time.monotonic()
        with provider_mock.ProviderMockServer() as server:
            for case_name in ("idle_timeout", "cancellation"):
                parsed = urlsplit(server.endpoint("openai-compatible", case_name))
                connection = http.client.HTTPConnection(
                    parsed.hostname, parsed.port, timeout=0.1
                )
                connection.request(
                    "POST",
                    parsed.path,
                    body=json.dumps(request_document(family)),
                    headers=request_headers(family, "synthetic"),
                )
                response = connection.getresponse()
                self.assertEqual(200, response.status)
                self.assertTrue(response.read(1))
                with self.assertRaises((TimeoutError, socket.timeout, OSError)):
                    response.read()
                connection.close()
        self.assertLess(time.monotonic() - started, 1.5)

    def test_control_health_and_observations_are_json(self) -> None:
        """功能：确认控制端点只返回健康状态和脱敏观测 JSON。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with provider_mock.ProviderMockServer() as server:
            parsed = urlsplit(server.origin)
            connection = http.client.HTTPConnection(
                parsed.hostname, parsed.port, timeout=2
            )
            try:
                connection.request("GET", "/_control/health")
                health = connection.getresponse()
                self.assertEqual({"ok": True}, json.loads(health.read()))
            finally:
                connection.close()

            connection = http.client.HTTPConnection(
                parsed.hostname, parsed.port, timeout=2
            )
            try:
                connection.request("GET", "/_control/observations")
                observations = connection.getresponse()
                self.assertEqual({"observations": []}, json.loads(observations.read()))
            finally:
                connection.close()

    def test_standalone_cli_exposes_loopback_health(self) -> None:
        """功能：确认独立 CLI 仅报告回环 origin 且 health 可访问。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        environment = os.environ.copy()
        environment["PYTHONDONTWRITEBYTECODE"] = "1"
        process = subprocess.Popen(
            [sys.executable, str(CONFORMANCE / "provider_mock.py")],
            cwd=ROOT,
            env=environment,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        try:
            assert process.stdout is not None
            ready = process.stdout.readline().strip()
            self.assertTrue(ready.startswith("READY http://127.0.0.1:"), ready)
            parsed = urlsplit(ready.removeprefix("READY "))
            connection = http.client.HTTPConnection(
                parsed.hostname, parsed.port, timeout=2
            )
            try:
                connection.request("GET", "/_control/health")
                response = connection.getresponse()
                self.assertEqual(200, response.status)
                self.assertEqual({"ok": True}, json.loads(response.read()))
            finally:
                connection.close()
        finally:
            process.terminate()
            try:
                process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=2)
            if process.stdout is not None:
                process.stdout.close()
            if process.stderr is not None:
                process.stderr.close()


if __name__ == "__main__":
    unittest.main()
