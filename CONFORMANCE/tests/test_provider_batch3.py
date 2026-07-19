from __future__ import annotations

import http.client
import importlib.util
import json
from pathlib import Path
import socket
import struct
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
    """功能：返回第三批请求自测使用的受限 file.read 参数 Schema。

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
    """功能：按第三批 family 原生 wire 构造带非空工具定义的测试请求体。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    schema = tool_schema()
    if family_id == "google-vertex":
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
    if family_id == "bedrock-converse-stream":
        return {
            "messages": [{"role": "user", "content": [{"text": "fixture input"}]}],
            "toolConfig": {
                "tools": [
                    {
                        "toolSpec": {
                            "name": "file.read",
                            "description": "Read one file.",
                            "inputSchema": {"json": schema},
                        }
                    }
                ]
            },
        }
    if family_id == "openai-codex-responses":
        return {
            "model": "mock-codex-v1",
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
    raise AssertionError("unknown batch3 family")


def request_headers(family_id: str, credential: str) -> dict[str, str]:
    """功能：按第三批 family 生成测试认证/签名形状及固定 header。

    输入：family ID 与仅驻留内存的合成 credential。
    输出：回环请求所需 header；调用者不得记录其中的值。
    不变量：只测试 scheme/header presence，不声称验证真实 OAuth 或 SigV4。
    失败：未知 family 时抛出 AssertionError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    headers = {"Content-Type": "application/json"}
    if family_id == "google-vertex":
        headers["Authorization"] = f"Bearer {credential}"
    elif family_id == "bedrock-converse-stream":
        headers.update(
            {
                "Accept": "application/vnd.amazon.eventstream",
                "Authorization": (
                    "AWS4-HMAC-SHA256 Credential="
                    f"{credential}/20260714/us-east-1/bedrock/aws4_request, "
                    "SignedHeaders=content-type;host;x-amz-content-sha256;"
                    "x-amz-date;x-amz-security-token, Signature=synthetic"
                ),
                "x-amz-content-sha256": "synthetic-payload-digest",
                "x-amz-date": "20260714T010000Z",
                "x-amz-security-token": credential,
            }
        )
    elif family_id == "openai-codex-responses":
        headers.update(
            {
                "Authorization": f"Bearer {credential}",
                "OpenAI-Beta": "responses=experimental",
                "originator": "qxnm-forge",
            }
        )
    else:
        raise AssertionError("unknown batch3 family")
    return headers


def post_case(
    server: provider_mock.ProviderMockServer,
    family: dict[str, object],
    case_name: str,
    *,
    credential: str = "synthetic-batch3-credential",
    target_override: str | None = None,
    headers_override: dict[str, str] | None = None,
    timeout: float = 2.0,
) -> tuple[int, dict[str, str], bytes]:
    """功能：按原生 path/query 向第三批回环 mock POST 并读取完整响应。

    输入：mock、family、案例名、内存凭据及可选目标/header 覆盖和超时。
    输出：HTTP 状态、大小写归一化响应 header 与响应字节。
    不变量：只连接 server 暴露的字面 127.0.0.1 ephemeral 端口。
    失败：连接或读取失败由标准库异常显式传播。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    family_id = family.get("id")
    target_contract = family.get("requestTarget")
    if not isinstance(family_id, str) or not isinstance(target_contract, dict):
        raise AssertionError("invalid batch3 family")
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
        parsed.hostname, parsed.port, timeout=timeout
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
            headers=(
                request_headers(family_id, credential)
                if headers_override is None
                else headers_override
            ),
        )
        response = connection.getresponse()
        body = response.read()
        headers = {name.casefold(): value for name, value in response.getheaders()}
        return response.status, headers, body
    finally:
        connection.close()


class ProviderBatch3FixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        """功能：加载独立第三批夹具供静态和 wire 测试使用。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        cls.fixture = provider_mock.load_fixture(provider_mock.BATCH3_FIXTURE)

    def test_static_suite_has_three_families_and_twenty_one_cases(self) -> None:
        """功能：确认第三批独立套件固定为三 family 各七个案例。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.assertEqual(
            (3, 21),
            provider_mock.static_self_test(provider_mock.BATCH3_FIXTURE),
        )
        self.assertEqual(
            provider_mock.BATCH3_FAMILIES,
            provider_mock.fixture_family_ids(self.fixture),
        )

    @unittest.skipIf(
        importlib.util.find_spec("jsonschema") is None,
        "optional jsonschema package is unavailable",
    )
    def test_batch3_fixture_matches_draft_2020_12_schema(self) -> None:
        """功能：确认第三批夹具通过 bundled Draft 2020-12 Schema。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        provider_runner.validate_fixture_schema(provider_mock.BATCH3_FIXTURE, SCHEMAS)

    def test_native_targets_auth_protocol_and_fixed_headers_are_frozen(self) -> None:
        """功能：确认第三批目标、认证 scheme、传输和固定 header 未漂移。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        expected = {
            "google-vertex": ("Bearer ", "sse", 0, "complete-json-object"),
            "bedrock-converse-stream": (
                "AWS4-HMAC-SHA256 ",
                "aws-event-stream",
                0,
                "partial-json-string",
            ),
            "openai-codex-responses": (
                "Bearer ",
                "sse",
                2,
                "partial-json-string",
            ),
        }
        for family_value in self.fixture["families"]:
            self.assertIsInstance(family_value, dict)
            family = family_value
            actual = (
                family["authHeaders"][0]["prefix"],
                family["streamProtocol"],
                len(family["fixedHeaders"]),
                family["toolArgumentDelivery"],
            )
            with self.subTest(family=family["id"]):
                self.assertEqual(expected[family["id"]], actual)
        vertex_target = self.fixture["families"][0]["requestTarget"]["pathSuffix"]
        self.assertIn("/projects/mock-project/locations/us-central1/", vertex_target)
        self.assertIn("/publishers/google/models/", vertex_target)

    def test_sse_and_eventstream_survive_arbitrary_utf8_protocol_chunks(self) -> None:
        """功能：确认 SSE 与 AWS EventStream 在 UTF-8/JSON/CRC 任意分片后无损。

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
            with self.subTest(family=family["id"]):
                self.assertEqual(wire, b"".join(chunks))
                self.assertIn("你好".encode(), wire)
                self.assertIn("👋".encode(), wire)
                if family["id"] == "bedrock-converse-stream":
                    events = provider_mock.decode_aws_event_stream(wire)
                    boundaries: list[int] = []
                    offset = 0
                    for chunk in chunks[:-1]:
                        offset += len(chunk)
                        boundaries.append(offset)
                    (first_length,) = struct.unpack_from(">I", wire, 0)
                    self.assertGreaterEqual(len(events), 6)
                    self.assertNotIn(b"data:", wire)
                    self.assertTrue(any(8 < value < 12 for value in boundaries))
                    self.assertTrue(
                        any(
                            first_length - 4 < value < first_length
                            for value in boundaries
                        )
                    )
                    self.assertTrue(
                        all(
                            headers[":content-type"] == "application/json"
                            for headers, _ in events
                        )
                    )
                else:
                    decoder = runner.SSEDecoder()
                    events: list[object] = []
                    for chunk in chunks:
                        events.extend(decoder.feed(chunk))
                    events.extend(decoder.finish())
                    self.assertIn(b"\r\n", wire)
                    self.assertTrue(wire.endswith(b"\r\n"))
                    self.assertFalse(wire.endswith(b"\r\n\r\n"))
                    self.assertGreaterEqual(len(events), 2)

    def test_tool_wire_preserves_each_native_argument_delivery_model(self) -> None:
        """功能：确认 Vertex 完整对象、Bedrock/Codex partial string 的差异。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        vertex = provider_mock.family_by_id(self.fixture, "google-vertex")
        vertex_attempt = provider_mock.case_by_name(vertex, "partial_tool_arguments")[
            "attempts"
        ][0]
        vertex_wire = provider_mock.render_stream("google-vertex", vertex_attempt)
        self.assertIn(b'"args":{"path":"README.md"}', vertex_wire)

        codex = provider_mock.family_by_id(self.fixture, "openai-codex-responses")
        codex_attempt = provider_mock.case_by_name(codex, "partial_tool_arguments")[
            "attempts"
        ][0]
        codex_wire = provider_mock.render_stream(
            "openai-codex-responses", codex_attempt
        )
        self.assertIn(b"response.function_call_arguments.delta", codex_wire)

        bedrock = provider_mock.family_by_id(self.fixture, "bedrock-converse-stream")
        bedrock_attempt = provider_mock.case_by_name(bedrock, "partial_tool_arguments")[
            "attempts"
        ][0]
        bedrock_wire = provider_mock.render_stream(
            "bedrock-converse-stream", bedrock_attempt
        )
        fragments: list[str] = []
        for headers, payload in provider_mock.decode_aws_event_stream(bedrock_wire):
            if headers.get(":event-type") != "contentBlockDelta":
                continue
            delta = payload.get("delta")
            tool_use = delta.get("toolUse") if isinstance(delta, dict) else None
            input_value = tool_use.get("input") if isinstance(tool_use, dict) else None
            if isinstance(input_value, str):
                fragments.append(input_value)
        self.assertEqual('{"path":"README.md"}', "".join(fragments))

    def test_bedrock_decoder_rejects_prelude_and_message_crc_corruption(self) -> None:
        """功能：确认任一 Bedrock prelude/message CRC 被篡改都会拒绝整条 wire。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        family = provider_mock.family_by_id(self.fixture, "bedrock-converse-stream")
        attempt = provider_mock.case_by_name(family, "text_success")["attempts"][0]
        wire = provider_mock.render_stream("bedrock-converse-stream", attempt)

        bad_prelude = bytearray(wire)
        bad_prelude[8] ^= 0x01
        with self.assertRaises(provider_mock.ProviderMockError):
            provider_mock.decode_aws_event_stream(bytes(bad_prelude))

        (first_length,) = struct.unpack_from(">I", wire, 0)
        bad_message = bytearray(wire)
        bad_message[first_length - 1] ^= 0x01
        with self.assertRaises(provider_mock.ProviderMockError):
            provider_mock.decode_aws_event_stream(bytes(bad_message))


class ProviderBatch3HTTPTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        """功能：加载第三批夹具供回环 HTTP 行为测试使用。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        cls.fixture = provider_mock.load_fixture(provider_mock.BATCH3_FIXTURE)

    def test_success_requests_validate_target_auth_body_and_content_type(self) -> None:
        """功能：确认三类原生目标、认证、请求体及流 content type 均被接受。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        canary = "batch3-http-canary-must-not-be-observed"
        with provider_mock.ProviderMockServer(provider_mock.BATCH3_FIXTURE) as server:
            for family_value in self.fixture["families"]:
                self.assertIsInstance(family_value, dict)
                status, headers, body = post_case(
                    server, family_value, "text_success", credential=canary
                )
                with self.subTest(family=family_value["id"]):
                    self.assertEqual(200, status)
                    expected_type = (
                        "application/vnd.amazon.eventstream"
                        if family_value["id"] == "bedrock-converse-stream"
                        else "text/event-stream"
                    )
                    self.assertIn(expected_type, headers["content-type"])
                    self.assertIn("你好".encode(), body)
            observations = server.state.observations()
        serialized = json.dumps(observations, ensure_ascii=False)
        self.assertNotIn(canary, serialized)
        self.assertEqual(3, len(observations))
        self.assertTrue(all(item["requestValid"] for item in observations))
        self.assertTrue(all(item["requestTargetValid"] for item in observations))
        self.assertEqual(2, len(observations[-1]["fixedHeaders"]))

    def test_wrong_vertex_resource_path_is_rejected_without_value_echo(self) -> None:
        """功能：确认错误 Vertex project/location 路径返回 400 且不回显实例值。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        canary = "vertex-target-must-not-be-observed"
        vertex = provider_mock.family_by_id(self.fixture, "google-vertex")
        with provider_mock.ProviderMockServer(provider_mock.BATCH3_FIXTURE) as server:
            status, _, _ = post_case(
                server,
                vertex,
                "text_success",
                target_override=f"/v1/projects/{canary}?alt=wrong",
            )
            observations = server.state.observations()
        self.assertEqual(400, status)
        serialized = json.dumps(observations, ensure_ascii=False)
        self.assertNotIn(canary, serialized)
        self.assertIn("target:path-suffix", observations[0]["validationErrors"])
        self.assertIn("target:query", observations[0]["validationErrors"])

    def test_missing_sigv4_or_codex_fixed_header_is_rejected_and_redacted(self) -> None:
        """功能：确认缺少 SigV4 signing 或 Codex 固定 header 时脱敏拒绝。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        canary = "missing-header-canary-must-not-be-observed"
        bedrock = provider_mock.family_by_id(self.fixture, "bedrock-converse-stream")
        codex = provider_mock.family_by_id(self.fixture, "openai-codex-responses")
        with provider_mock.ProviderMockServer(provider_mock.BATCH3_FIXTURE) as server:
            bedrock_headers = request_headers("bedrock-converse-stream", canary)
            del bedrock_headers["x-amz-date"]
            bedrock_result = post_case(
                server,
                bedrock,
                "text_success",
                headers_override=bedrock_headers,
            )
            codex_headers = request_headers("openai-codex-responses", canary)
            del codex_headers["OpenAI-Beta"]
            codex_result = post_case(
                server,
                codex,
                "text_success",
                headers_override=codex_headers,
            )
            observations = server.state.observations()
        self.assertEqual(400, bedrock_result[0])
        self.assertEqual(400, codex_result[0])
        serialized = json.dumps(observations, ensure_ascii=False)
        self.assertNotIn(canary, serialized)
        self.assertIn("header:x-amz-date", observations[0]["validationErrors"])
        self.assertIn("fixed-header:openai-beta", observations[1]["validationErrors"])

    def test_429_and_503_retry_after_then_success_for_every_family(self) -> None:
        """功能：确认第三批均覆盖 429 数字和 503 HTTP-date 重试后成功。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with provider_mock.ProviderMockServer(provider_mock.BATCH3_FIXTURE) as server:
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
        """功能：确认第三批断流、idle 与客户端取消都不会阻塞 mock 回收。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        started = time.monotonic()
        with provider_mock.ProviderMockServer(provider_mock.BATCH3_FIXTURE) as server:
            for family_value in self.fixture["families"]:
                self.assertIsInstance(family_value, dict)
                status, _, body = post_case(server, family_value, "disconnect")
                self.assertEqual(200, status)
                if family_value["id"] == "bedrock-converse-stream":
                    with self.assertRaises(provider_mock.ProviderMockError):
                        provider_mock.decode_aws_event_stream(body)
                else:
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


class ProviderBatch3RunnerTests(unittest.TestCase):
    def test_opt_in_runner_drives_all_three_native_text_families(self) -> None:
        """功能：确认共同 runner 用第三批夹具驱动三个 fake native adapter。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        command_json = json.dumps(
            [sys.executable, str(CONFORMANCE / "tests/fake_provider_batch3_daemon.py")]
        )
        for family_id in provider_mock.BATCH3_FAMILIES:
            completed = subprocess.run(
                [
                    sys.executable,
                    str(CONFORMANCE / "provider_runner.py"),
                    "--fixture",
                    str(provider_mock.BATCH3_FIXTURE),
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
        """功能：确认 runner 等第三批首个 HTTP 观测后再发出幂等取消请求。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        command_json = json.dumps(
            [sys.executable, str(CONFORMANCE / "tests/fake_provider_batch3_daemon.py")]
        )
        for family_id in provider_mock.BATCH3_FAMILIES:
            completed = subprocess.run(
                [
                    sys.executable,
                    str(CONFORMANCE / "provider_runner.py"),
                    "--fixture",
                    str(provider_mock.BATCH3_FIXTURE),
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
