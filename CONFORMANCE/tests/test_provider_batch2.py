from __future__ import annotations

import http.client
import importlib.util
import json
from pathlib import Path
import socket
import subprocess
import sys
import time
import unittest
from urllib.parse import urlencode, urlsplit


CONFORMANCE = Path(__file__).resolve().parents[1]
ROOT = CONFORMANCE.parent
SCHEMAS = ROOT / "SPEC/schemas"
sys.path.insert(0, str(CONFORMANCE))

import provider_mock  # noqa: E402
import provider_runner  # noqa: E402
import runner  # noqa: E402


def tool_schema() -> dict[str, object]:
    """功能：返回第二批请求自测使用的受限 file.read 参数 Schema。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "type": "object",
        "properties": {"path": {"type": "string"}},
        "required": ["path"],
        "additionalProperties": False,
    }


def request_document(family_id: str) -> dict[str, object]:
    """功能：按第二批 family 原生 wire 构造带非空工具定义的测试请求体。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    schema = tool_schema()
    if family_id == "mistral-conversations":
        return {
            "model": "mock-mistral-v1",
            "messages": [{"role": "user", "content": "fixture input"}],
            "stream": True,
            "tools": [
                {
                    "type": "function",
                    "function": {
                        "name": "file.read",
                        "description": "Read one file.",
                        "parameters": schema,
                        "strict": False,
                    },
                }
            ],
        }
    if family_id == "azure-openai-responses":
        return {
            "model": "mock-azure-responses-v1",
            "input": [{"role": "user", "content": "fixture input"}],
            "stream": True,
            "store": False,
            "tools": [
                {
                    "type": "function",
                    "name": "file.read",
                    "description": "Read one file.",
                    "parameters": schema,
                    "strict": False,
                }
            ],
        }
    if family_id == "google-generative-ai":
        return {
            "contents": [{"role": "user", "parts": [{"text": "fixture input"}]}],
            "tools": [
                {
                    "functionDeclarations": [
                        {
                            "name": "file.read",
                            "description": "Read one file.",
                            "parametersJsonSchema": schema,
                        }
                    ]
                }
            ],
        }
    raise AssertionError("unknown batch2 family")


def request_headers(family_id: str, credential: str) -> dict[str, str]:
    """功能：按第二批 family 生成测试认证 header，凭据仅保留在内存。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    headers = {"Content-Type": "application/json"}
    if family_id == "mistral-conversations":
        headers["Authorization"] = f"Bearer {credential}"
    elif family_id == "azure-openai-responses":
        headers["api-key"] = credential
    elif family_id == "google-generative-ai":
        headers["x-goog-api-key"] = credential
    else:
        raise AssertionError("unknown batch2 family")
    return headers


def post_case(
    server: provider_mock.ProviderMockServer,
    family: dict[str, object],
    case_name: str,
    *,
    credential: str = "synthetic-batch2-credential",
    target_override: str | None = None,
    timeout: float = 2.0,
) -> tuple[int, dict[str, str], bytes]:
    """功能：按原生 path/query 向第二批回环 mock POST 并读取完整响应。

    输入：mock、family、案例名、内存凭据、可选错误目标和超时。
    输出：HTTP 状态、大小写归一化 header 与响应字节。
    不变量：仅连接 server 暴露的字面 127.0.0.1 ephemeral 端口。
    失败：连接或读取失败由标准库异常显式传播。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    family_id = family.get("id")
    target_contract = family.get("requestTarget")
    if not isinstance(family_id, str) or not isinstance(target_contract, dict):
        raise AssertionError("invalid batch2 family")
    parsed = urlsplit(server.endpoint(family_id, case_name))
    if target_override is None:
        suffix = target_contract["pathSuffix"]
        query = urlencode(target_contract["requiredQuery"])
        target = parsed.path + suffix
        if query:
            target += "?" + query
    else:
        target = parsed.path + target_override
    connection = http.client.HTTPConnection(
        parsed.hostname,
        parsed.port,
        timeout=timeout,
    )
    try:
        connection.request(
            "POST",
            target,
            body=json.dumps(
                request_document(family_id),
                ensure_ascii=False,
                separators=(",", ":"),
            ),
            headers=request_headers(family_id, credential),
        )
        response = connection.getresponse()
        body = response.read()
        headers = {name.casefold(): value for name, value in response.getheaders()}
        return response.status, headers, body
    finally:
        connection.close()


class ProviderBatch2FixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        """功能：加载独立第二批夹具供纯静态与 HTTP 测试使用。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        cls.fixture = provider_mock.load_fixture(provider_mock.BATCH2_FIXTURE)

    def test_static_suite_has_three_families_and_twenty_one_cases(self) -> None:
        """功能：确认第二批独立套件固定为三 family 各七个案例。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.assertEqual(
            (3, 21),
            provider_mock.static_self_test(provider_mock.BATCH2_FIXTURE),
        )
        self.assertEqual(
            provider_mock.BATCH2_FAMILIES,
            provider_mock.fixture_family_ids(self.fixture),
        )

    @unittest.skipIf(
        importlib.util.find_spec("jsonschema") is None,
        "optional jsonschema package is unavailable",
    )
    def test_batch2_fixture_matches_draft_2020_12_schema(self) -> None:
        """功能：确认第二批夹具通过 bundled Draft 2020-12 Schema。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        provider_runner.validate_fixture_schema(provider_mock.BATCH2_FIXTURE, SCHEMAS)

    def test_native_targets_auth_and_argument_delivery_are_frozen(self) -> None:
        """功能：确认三类真实请求目标、认证位置和工具参数交付模式未漂移。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        expected = {
            "mistral-conversations": (
                "/v1/chat/completions",
                {},
                "Authorization",
                "partial-json-string",
            ),
            "azure-openai-responses": (
                "/responses",
                {"api-version": "v1"},
                "api-key",
                "partial-json-string",
            ),
            "google-generative-ai": (
                "/models/mock-google-v1:streamGenerateContent",
                {"alt": "sse"},
                "x-goog-api-key",
                "complete-json-object",
            ),
        }
        for family_value in self.fixture["families"]:
            self.assertIsInstance(family_value, dict)
            family = family_value
            target = family["requestTarget"]
            actual = (
                target["pathSuffix"],
                target["requiredQuery"],
                family["authHeaders"][0]["name"],
                family["toolArgumentDelivery"],
            )
            with self.subTest(family=family["id"]):
                self.assertEqual(expected[family["id"]], actual)

    def test_native_text_sse_handles_utf8_fragmentation_crlf_and_eof(self) -> None:
        """功能：确认三类文本 SSE 覆盖 Unicode 字节分片、CRLF 与 EOF 尾事件。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        for family_value in self.fixture["families"]:
            self.assertIsInstance(family_value, dict)
            family = family_value
            case = provider_mock.case_by_name(family, "text_success")
            attempt = case["attempts"][0]
            wire = provider_mock.render_stream(family["id"], attempt)
            chunks = provider_mock.split_bytes(wire, attempt["chunkSizes"])
            decoder = runner.SSEDecoder()
            events: list[object] = []
            for chunk in chunks:
                events.extend(decoder.feed(chunk))
            events.extend(decoder.finish())
            with self.subTest(family=family["id"]):
                self.assertEqual(wire, b"".join(chunks))
                self.assertIn("你好".encode(), wire)
                self.assertIn("👋".encode(), wire)
                self.assertIn(b"\r\n", wire)
                self.assertTrue(wire.endswith(b"\r\n"))
                self.assertFalse(wire.endswith(b"\r\n\r\n"))
                self.assertGreaterEqual(len(events), 2)

    def test_tool_wire_uses_partial_strings_or_complete_google_object(self) -> None:
        """功能：确认 Mistral/Azure 为参数增量，Google 为完整 functionCall.args 对象。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        expected_markers = {
            "mistral-conversations": b"tool_calls",
            "azure-openai-responses": b"response.function_call_arguments.delta",
            "google-generative-ai": b'"functionCall"',
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
                if family_id == "google-generative-ai":
                    self.assertIn(b'"args":{"path":"README.md"}', wire)
                    self.assertNotIn(b"function_call_arguments.delta", wire)


class ProviderBatch2HTTPTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        """功能：加载第二批夹具供回环 HTTP 行为测试使用。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        cls.fixture = provider_mock.load_fixture(provider_mock.BATCH2_FIXTURE)

    def test_success_requests_validate_native_target_auth_and_body_shape(self) -> None:
        """功能：确认三类原生 path/query、认证与带工具请求体均被接受且脱敏。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        canary = "batch2-http-canary-must-not-be-observed"
        with provider_mock.ProviderMockServer(provider_mock.BATCH2_FIXTURE) as server:
            for family_value in self.fixture["families"]:
                self.assertIsInstance(family_value, dict)
                status, headers, body = post_case(
                    server,
                    family_value,
                    "text_success",
                    credential=canary,
                )
                with self.subTest(family=family_value["id"]):
                    self.assertEqual(200, status)
                    self.assertIn("text/event-stream", headers["content-type"])
                    self.assertIn("你好".encode(), body)
            observations = server.state.observations()
        serialized = json.dumps(observations, ensure_ascii=False)
        self.assertNotIn(canary, serialized)
        self.assertEqual(3, len(observations))
        self.assertTrue(all(item["requestValid"] for item in observations))
        self.assertTrue(all(item["requestTargetValid"] for item in observations))

    def test_wrong_path_or_query_is_rejected_without_echoing_target(self) -> None:
        """功能：确认错误原生目标返回 400，观测只含固定标签而不复制实例值。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        canary = "target-value-must-not-be-observed"
        google = provider_mock.family_by_id(self.fixture, "google-generative-ai")
        with provider_mock.ProviderMockServer(provider_mock.BATCH2_FIXTURE) as server:
            status, _, _ = post_case(
                server,
                google,
                "text_success",
                target_override=f"/{canary}?alt=wrong",
            )
            observations = server.state.observations()
        self.assertEqual(400, status)
        serialized = json.dumps(observations, ensure_ascii=False)
        self.assertNotIn(canary, serialized)
        self.assertFalse(observations[0]["requestTargetValid"])
        self.assertIn("target:path-suffix", observations[0]["validationErrors"])
        self.assertIn("target:query", observations[0]["validationErrors"])

    def test_429_and_503_retry_after_then_success_for_every_family(self) -> None:
        """功能：确认三类均覆盖 429 数字和 503 HTTP-date Retry-After 后成功。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with provider_mock.ProviderMockServer(provider_mock.BATCH2_FIXTURE) as server:
            for family_value in self.fixture["families"]:
                self.assertIsInstance(family_value, dict)
                rate_first = post_case(server, family_value, "rate_limit_retry")
                rate_second = post_case(server, family_value, "rate_limit_retry")
                server_first = post_case(server, family_value, "server_error_retry")
                server_second = post_case(server, family_value, "server_error_retry")
                with self.subTest(family=family_value["id"]):
                    self.assertEqual(429, rate_first[0])
                    self.assertEqual("0", rate_first[1]["retry-after"])
                    self.assertEqual(200, rate_second[0])
                    self.assertEqual(503, server_first[0])
                    self.assertEqual(
                        "Wed, 21 Oct 2037 07:28:00 GMT",
                        server_first[1]["retry-after"],
                    )
                    self.assertEqual(200, server_second[0])

    def test_disconnect_idle_and_client_close_are_bounded(self) -> None:
        """功能：确认三类断流、idle timeout 与客户端取消均不会阻塞 mock 回收。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        started = time.monotonic()
        with provider_mock.ProviderMockServer(provider_mock.BATCH2_FIXTURE) as server:
            for family_value in self.fixture["families"]:
                self.assertIsInstance(family_value, dict)
                status, _, body = post_case(server, family_value, "disconnect")
                self.assertEqual(200, status)
                self.assertIn(b"synthetic_incomplete", body)
                for case_name in ("idle_timeout", "cancellation"):
                    with self.subTest(family=family_value["id"], case=case_name):
                        with self.assertRaises((TimeoutError, socket.timeout, OSError)):
                            post_case(
                                server,
                                family_value,
                                case_name,
                                timeout=0.1,
                            )
        self.assertLess(time.monotonic() - started, 2.5)


class ProviderBatch2RunnerTests(unittest.TestCase):
    def test_opt_in_runner_drives_all_three_native_text_families(self) -> None:
        """功能：确认共同 runner 用独立夹具端到端驱动三个 fake native adapter。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        command_json = json.dumps(
            [
                sys.executable,
                str(CONFORMANCE / "tests/fake_provider_batch2_daemon.py"),
            ]
        )
        for family_id in provider_mock.BATCH2_FAMILIES:
            completed = subprocess.run(
                [
                    sys.executable,
                    str(CONFORMANCE / "provider_runner.py"),
                    "--fixture",
                    str(provider_mock.BATCH2_FIXTURE),
                    "--family",
                    family_id,
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
            with self.subTest(family=family_id):
                self.assertEqual(0, completed.returncode, completed.stderr)
                self.assertIn(f"PASS: {family_id}/text_success", completed.stdout)
                self.assertIn("PASS: provider daemon probes 1", completed.stdout)

    def test_opt_in_runner_cancels_all_three_native_families_after_request(
        self,
    ) -> None:
        """功能：确认 runner 等每类首个原生 HTTP 观测后再发出幂等取消请求。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        command_json = json.dumps(
            [
                sys.executable,
                str(CONFORMANCE / "tests/fake_provider_batch2_daemon.py"),
            ]
        )
        for family_id in provider_mock.BATCH2_FAMILIES:
            completed = subprocess.run(
                [
                    sys.executable,
                    str(CONFORMANCE / "provider_runner.py"),
                    "--fixture",
                    str(provider_mock.BATCH2_FIXTURE),
                    "--family",
                    family_id,
                    "--case",
                    "cancellation",
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
            with self.subTest(family=family_id):
                self.assertEqual(0, completed.returncode, completed.stderr)
                self.assertIn(f"PASS: {family_id}/cancellation", completed.stdout)
                self.assertIn("PASS: provider daemon probes 1", completed.stdout)


if __name__ == "__main__":
    unittest.main()
