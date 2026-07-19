#!/usr/bin/env python3
"""九类 Provider 传输的分批一致性本机 mock，仅使用 Python 标准库。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
import copy
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import math
from pathlib import Path
import socket
import struct
import sys
import threading
from typing import Mapping, Sequence
from urllib.parse import parse_qsl, urlsplit
import zlib


JSONValue = (
    type(None) | bool | int | float | str | list["JSONValue"] | dict[str, "JSONValue"]
)
JSONObject = dict[str, JSONValue]

DEFAULT_FIXTURE = Path(__file__).resolve().parent / "fixtures/provider/mock-cases.json"
BATCH2_FIXTURE = (
    Path(__file__).resolve().parent / "fixtures/provider/mock-cases-batch2.json"
)
BATCH3_FIXTURE = (
    Path(__file__).resolve().parent / "fixtures/provider/mock-cases-batch3.json"
)
BASELINE_FAMILIES = (
    "openai-compatible",
    "openai-responses",
    "anthropic-messages",
)
BATCH2_FAMILIES = (
    "mistral-conversations",
    "azure-openai-responses",
    "google-generative-ai",
)
BATCH3_FAMILIES = (
    "google-vertex",
    "bedrock-converse-stream",
    "openai-codex-responses",
)
ALL_FAMILIES = (*BASELINE_FAMILIES, *BATCH2_FAMILIES, *BATCH3_FAMILIES)
# 保留旧名称，避免首批 21 案例的外部 runner 或实现脚本失效。
EXPECTED_FAMILIES = BASELINE_FAMILIES
EXPECTED_CASES = (
    "text_success",
    "partial_tool_arguments",
    "rate_limit_retry",
    "server_error_retry",
    "disconnect",
    "idle_timeout",
    "cancellation",
)
MAX_REQUEST_BYTES = 1_048_576

FAMILY_CONTRACTS: dict[str, JSONObject] = {
    "openai-compatible": {
        "apiFamily": "openai-completions",
        "providerId": "openai-compatible",
        "modelId": "mock-chat-v1",
        "endpointEnv": "QXNM_FORGE_OPENAI_COMPATIBLE_ENDPOINT",
        "credentialEnv": "OPENAI_API_KEY",
        "authHeaders": [{"name": "Authorization", "prefix": "Bearer "}],
        "requiredHeaders": ["content-type"],
        "requiredJsonPointers": [
            "/model",
            "/messages",
            "/stream",
            "/tools/0/type",
            "/tools/0/function/name",
            "/tools/0/function/parameters",
        ],
        "equals": {
            "/model": "mock-chat-v1",
            "/stream": True,
            "/tools/0/type": "function",
            "/tools/0/function/name": "file.read",
        },
    },
    "openai-responses": {
        "apiFamily": "openai-responses",
        "providerId": "openai-responses",
        "modelId": "mock-responses-v1",
        "endpointEnv": "QXNM_FORGE_OPENAI_RESPONSES_ENDPOINT",
        "credentialEnv": "OPENAI_API_KEY",
        "authHeaders": [{"name": "Authorization", "prefix": "Bearer "}],
        "requiredHeaders": ["content-type"],
        "requiredJsonPointers": [
            "/model",
            "/input",
            "/stream",
            "/tools/0/type",
            "/tools/0/name",
            "/tools/0/parameters",
        ],
        "equals": {
            "/model": "mock-responses-v1",
            "/stream": True,
            "/tools/0/type": "function",
            "/tools/0/name": "file.read",
        },
    },
    "anthropic-messages": {
        "apiFamily": "anthropic-messages",
        "providerId": "anthropic",
        "modelId": "mock-anthropic-v1",
        "endpointEnv": "QXNM_FORGE_ANTHROPIC_ENDPOINT",
        "credentialEnv": "ANTHROPIC_API_KEY",
        "authHeaders": [{"name": "x-api-key", "prefix": ""}],
        "requiredHeaders": ["content-type", "anthropic-version"],
        "requiredJsonPointers": [
            "/model",
            "/messages",
            "/stream",
            "/max_tokens",
            "/tools/0/name",
            "/tools/0/input_schema",
        ],
        "equals": {
            "/model": "mock-anthropic-v1",
            "/stream": True,
            "/tools/0/name": "file.read",
        },
    },
    "mistral-conversations": {
        "apiFamily": "mistral-conversations",
        "providerId": "mistral",
        "modelId": "mock-mistral-v1",
        "endpointEnv": "QXNM_FORGE_MISTRAL_CONVERSATIONS_ENDPOINT",
        "credentialEnv": "MISTRAL_API_KEY",
        "authHeaders": [{"name": "Authorization", "prefix": "Bearer "}],
        "requiredHeaders": ["content-type"],
        "requiredJsonPointers": [
            "/model",
            "/messages",
            "/stream",
            "/tools/0/type",
            "/tools/0/function/name",
            "/tools/0/function/parameters",
        ],
        "equals": {
            "/model": "mock-mistral-v1",
            "/stream": True,
            "/tools/0/type": "function",
            "/tools/0/function/name": "file.read",
        },
        "requestTarget": {
            "pathSuffix": "/v1/chat/completions",
            "requiredQuery": {},
        },
        "toolArgumentDelivery": "partial-json-string",
    },
    "azure-openai-responses": {
        "apiFamily": "azure-openai-responses",
        "providerId": "azure-openai-responses",
        "modelId": "mock-azure-responses-v1",
        "endpointEnv": "QXNM_FORGE_AZURE_OPENAI_RESPONSES_ENDPOINT",
        "credentialEnv": "AZURE_OPENAI_API_KEY",
        "authHeaders": [{"name": "api-key", "prefix": ""}],
        "requiredHeaders": ["content-type"],
        "requiredJsonPointers": [
            "/model",
            "/input",
            "/stream",
            "/store",
            "/tools/0/type",
            "/tools/0/name",
            "/tools/0/parameters",
        ],
        "equals": {
            "/model": "mock-azure-responses-v1",
            "/stream": True,
            "/store": False,
            "/tools/0/type": "function",
            "/tools/0/name": "file.read",
        },
        "requestTarget": {
            "pathSuffix": "/responses",
            "requiredQuery": {"api-version": "v1"},
        },
        "toolArgumentDelivery": "partial-json-string",
    },
    "google-generative-ai": {
        "apiFamily": "google-generative-ai",
        "providerId": "google",
        "modelId": "mock-google-v1",
        "endpointEnv": "QXNM_FORGE_GOOGLE_GENERATIVE_AI_ENDPOINT",
        "credentialEnv": "GEMINI_API_KEY",
        "authHeaders": [{"name": "x-goog-api-key", "prefix": ""}],
        "requiredHeaders": ["content-type"],
        "requiredJsonPointers": [
            "/contents",
            "/tools/0/functionDeclarations/0/name",
            "/tools/0/functionDeclarations/0/parametersJsonSchema",
        ],
        "equals": {
            "/contents/0/role": "user",
            "/tools/0/functionDeclarations/0/name": "file.read",
        },
        "requestTarget": {
            "pathSuffix": "/models/mock-google-v1:streamGenerateContent",
            "requiredQuery": {"alt": "sse"},
        },
        "toolArgumentDelivery": "complete-json-object",
    },
    "google-vertex": {
        "apiFamily": "google-vertex",
        "providerId": "google-vertex",
        "modelId": "mock-vertex-v1",
        "endpointEnv": "QXNM_FORGE_GOOGLE_VERTEX_ENDPOINT",
        "credentialEnv": "QXNM_FORGE_GOOGLE_VERTEX_OAUTH_TOKEN",
        "authHeaders": [{"name": "Authorization", "prefix": "Bearer "}],
        "fixedHeaders": [],
        "requiredHeaders": ["content-type"],
        "requiredJsonPointers": [
            "/contents",
            "/tools/0/functionDeclarations/0/name",
            "/tools/0/functionDeclarations/0/parametersJsonSchema",
        ],
        "equals": {
            "/contents/0/role": "user",
            "/tools/0/functionDeclarations/0/name": "file.read",
        },
        "requestTarget": {
            "pathSuffix": (
                "/v1/projects/mock-project/locations/us-central1/publishers/google/"
                "models/mock-vertex-v1:streamGenerateContent"
            ),
            "requiredQuery": {"alt": "sse"},
        },
        "toolArgumentDelivery": "complete-json-object",
        "streamProtocol": "sse",
    },
    "bedrock-converse-stream": {
        "apiFamily": "bedrock-converse-stream",
        "providerId": "amazon-bedrock",
        "modelId": "mock-bedrock-v1",
        "endpointEnv": "QXNM_FORGE_BEDROCK_CONVERSE_STREAM_ENDPOINT",
        "credentialEnv": "AWS_ACCESS_KEY_ID",
        "authHeaders": [{"name": "Authorization", "prefix": "AWS4-HMAC-SHA256 "}],
        "fixedHeaders": [],
        "requiredHeaders": [
            "content-type",
            "accept",
            "x-amz-content-sha256",
            "x-amz-date",
            "x-amz-security-token",
        ],
        "requiredJsonPointers": [
            "/messages",
            "/messages/0/content/0/text",
            "/toolConfig/tools/0/toolSpec/name",
            "/toolConfig/tools/0/toolSpec/inputSchema/json",
        ],
        "equals": {
            "/messages/0/role": "user",
            "/toolConfig/tools/0/toolSpec/name": "file.read",
        },
        "requestTarget": {
            "pathSuffix": "/model/mock-bedrock-v1/converse-stream",
            "requiredQuery": {},
        },
        "toolArgumentDelivery": "partial-json-string",
        "streamProtocol": "aws-event-stream",
    },
    "openai-codex-responses": {
        "apiFamily": "openai-codex-responses",
        "providerId": "openai-codex",
        "modelId": "mock-codex-v1",
        "endpointEnv": "QXNM_FORGE_OPENAI_CODEX_RESPONSES_ENDPOINT",
        "credentialEnv": "QXNM_FORGE_OPENAI_CODEX_OAUTH_TOKEN",
        "authHeaders": [{"name": "Authorization", "prefix": "Bearer "}],
        "fixedHeaders": [
            {"name": "OpenAI-Beta", "prefix": "responses=experimental"},
            {"name": "originator", "prefix": "qxnm-forge"},
        ],
        "requiredHeaders": ["content-type", "openai-beta", "originator"],
        "requiredJsonPointers": [
            "/model",
            "/input",
            "/stream",
            "/store",
            "/tools/0/type",
            "/tools/0/name",
            "/tools/0/parameters",
        ],
        "equals": {
            "/model": "mock-codex-v1",
            "/stream": True,
            "/store": False,
            "/tools/0/type": "function",
            "/tools/0/name": "file.read",
        },
        "requestTarget": {
            "pathSuffix": "/codex/responses",
            "requiredQuery": {},
        },
        "toolArgumentDelivery": "partial-json-string",
        "streamProtocol": "sse",
    },
}


class ProviderMockError(Exception):
    """表示 mock 夹具、请求或生命周期不满足确定性安全约束。"""


def _reject_duplicate_keys(pairs: list[tuple[str, JSONValue]]) -> JSONObject:
    """功能：构造 JSON 对象并拒绝会造成跨语言歧义的重复键。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    result: JSONObject = {}
    for key, value in pairs:
        if key in result:
            raise ProviderMockError(f"重复 JSON 键：{key!r}")
        result[key] = value
    return result


def _reject_constant(value: str) -> JSONValue:
    """功能：拒绝 JSON 标准之外的 NaN 和 Infinity 常量。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    raise ProviderMockError(f"非标准 JSON 常量：{value}")


def _parse_finite_float(value: str) -> float:
    """功能：解析 JSON 浮点数并拒绝指数溢出形成的非有限值。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    number = float(value)
    if not math.isfinite(number):
        raise ProviderMockError("JSON 数值超出有限范围")
    return number


def _parse_json_object(raw: bytes, context: str) -> JSONObject:
    """功能：把有界字节严格解析为 UTF-8 顶层 JSON 对象。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        text = raw.decode("utf-8", errors="strict")
        value = json.loads(
            text,
            object_pairs_hook=_reject_duplicate_keys,
            parse_constant=_reject_constant,
            parse_float=_parse_finite_float,
        )
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ProviderMockError(f"{context} 不是严格 UTF-8 JSON 对象") from exc
    if not isinstance(value, dict):
        raise ProviderMockError(f"{context} 顶层必须是 JSON 对象")
    return value


def load_fixture(path: Path = DEFAULT_FIXTURE) -> JSONObject:
    """功能：从磁盘加载 Provider mock 机器夹具并执行标准库语义校验。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        raw = path.read_bytes()
    except OSError as exc:
        raise ProviderMockError(f"无法读取 Provider mock 夹具：{path}") from exc
    fixture = _parse_json_object(raw, str(path))
    validate_fixture(fixture)
    return fixture


def _object(value: JSONValue, context: str) -> JSONObject:
    """功能：要求夹具节点为 JSON 对象并返回窄化后的对象。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, dict):
        raise ProviderMockError(f"{context} 必须是对象")
    return value


def _array(value: JSONValue, context: str) -> list[JSONValue]:
    """功能：要求夹具节点为 JSON 数组并返回窄化后的数组。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, list):
        raise ProviderMockError(f"{context} 必须是数组")
    return value


def _nonempty_string(value: JSONValue, context: str) -> str:
    """功能：要求夹具节点为非空字符串并返回窄化后的值。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, str) or not value:
        raise ProviderMockError(f"{context} 必须是非空字符串")
    return value


def fixture_family_ids(fixture: JSONObject) -> tuple[str, ...]:
    """功能：按 suiteId 返回该独立夹具允许的固定 family 顺序。

    输入：严格解析但尚未完成语义验证的夹具对象。
    输出：首批、第二批或第三批各三个不可变 family ID。
    不变量：缺省 suiteId 仅表示历史首批夹具；未知 suite 永不降级接受。
    失败：schemaVersion 或 suiteId 未知时抛出 ProviderMockError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if fixture.get("schemaVersion") != "0.1":
        raise ProviderMockError("Provider mock schemaVersion 必须为 0.1")
    suite_id = fixture.get("suiteId")
    if suite_id is None:
        return BASELINE_FAMILIES
    if suite_id == "batch2-native-v1":
        return BATCH2_FAMILIES
    if suite_id == "batch3-native-v1":
        return BATCH3_FAMILIES
    raise ProviderMockError("Provider mock suiteId 未知")


def validate_fixture(fixture: JSONObject) -> None:
    """功能：验证一个三 family 独立套件及七类重试/流式案例不变量。

    输入：顶层 Provider mock JSON 对象。
    输出：验证成功时无返回值。
    不变量：suite、family 与案例集合固定，usage 总数一致，故障注入顺序确定。
    失败：结构、名称或案例语义不符合 v0.1 时抛出 ProviderMockError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    expected_families = fixture_family_ids(fixture)
    families = _array(fixture.get("families"), "families")
    family_ids = [
        _nonempty_string(_object(item, "family").get("id"), "family.id")
        for item in families
    ]
    if tuple(family_ids) != expected_families:
        raise ProviderMockError("Provider mock family 顺序或集合不符合 v0.1")

    for family_value in families:
        family = _object(family_value, "family")
        family_id = _nonempty_string(family.get("id"), "family.id")
        contract = FAMILY_CONTRACTS[family_id]
        for field in (
            "apiFamily",
            "providerId",
            "modelId",
            "endpointEnv",
            "credentialEnv",
        ):
            if family.get(field) != contract[field]:
                raise ProviderMockError(f"{family_id}.{field} 不符合固定 v0.1 契约")
        auth_headers = _array(family.get("authHeaders"), f"{family_id}.authHeaders")
        if auth_headers != contract["authHeaders"]:
            raise ProviderMockError(f"{family_id}.authHeaders 不符合固定认证契约")
        request = _object(family.get("request"), f"{family_id}.request")
        for field in ("requiredHeaders", "requiredJsonPointers", "equals"):
            if request.get(field) != contract[field]:
                raise ProviderMockError(f"{family_id}.request.{field} 不符合固定契约")
        if family_id in (*BATCH2_FAMILIES, *BATCH3_FAMILIES):
            if family.get("requestTarget") != contract["requestTarget"]:
                raise ProviderMockError(
                    f"{family_id}.requestTarget 不符合固定原生请求目标"
                )
            if family.get("toolArgumentDelivery") != contract["toolArgumentDelivery"]:
                raise ProviderMockError(
                    f"{family_id}.toolArgumentDelivery 不符合固定原生语义"
                )
        if family_id in BATCH3_FAMILIES:
            if family.get("streamProtocol") != contract["streamProtocol"]:
                raise ProviderMockError(
                    f"{family_id}.streamProtocol 不符合固定第三批传输语义"
                )
            fixed_headers = _array(
                family.get("fixedHeaders"), f"{family_id}.fixedHeaders"
            )
            if fixed_headers != contract["fixedHeaders"]:
                raise ProviderMockError(
                    f"{family_id}.fixedHeaders 不符合固定第三批 header 契约"
                )
        elif "streamProtocol" in family or "fixedHeaders" in family:
            raise ProviderMockError(f"{family_id} 非第三批夹具不得注入第三批传输语义")
        if family_id in BASELINE_FAMILIES and (
            "requestTarget" in family or "toolArgumentDelivery" in family
        ):
            raise ProviderMockError(f"{family_id} 首批夹具不得注入后续批次请求目标语义")

        cases = _array(family.get("cases"), f"{family_id}.cases")
        case_names = [
            _nonempty_string(
                _object(item, f"{family_id}.case").get("name"),
                f"{family_id}.case.name",
            )
            for item in cases
        ]
        if tuple(case_names) != EXPECTED_CASES:
            raise ProviderMockError(f"{family_id} 的案例顺序或集合不符合 v0.1")
        for case_value in cases:
            _validate_case(family_id, _object(case_value, f"{family_id}.case"))


def _validate_case(family_id: str, case: JSONObject) -> None:
    """功能：验证单个案例的 attempt 顺序、终态和预期 usage。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    name = _nonempty_string(case.get("name"), f"{family_id}.case.name")
    attempts = _array(case.get("attempts"), f"{family_id}.{name}.attempts")
    if not attempts:
        raise ProviderMockError(f"{family_id}.{name} 至少需要一个 attempt")
    kinds = [
        _nonempty_string(
            _object(attempt, f"{family_id}.{name}.attempt").get("kind"),
            f"{family_id}.{name}.attempt.kind",
        )
        for attempt in attempts
    ]
    for attempt_value in attempts:
        _validate_attempt(
            family_id,
            name,
            _object(attempt_value, f"{family_id}.{name}.attempt"),
        )
    expected_kinds = {
        "text_success": ["text_stream"],
        "partial_tool_arguments": (
            ["tool_stream", "text_stream"]
            if family_id in BASELINE_FAMILIES
            else ["tool_stream"]
        ),
        "rate_limit_retry": ["http_error", "text_stream"],
        "server_error_retry": ["http_error", "text_stream"],
        "disconnect": ["disconnect_stream"],
        "idle_timeout": ["idle_stream"],
        "cancellation": ["cancel_stream"],
    }
    if kinds != expected_kinds[name]:
        raise ProviderMockError(f"{family_id}.{name} 的 attempt 顺序不符合规范")
    if name == "text_success":
        first_attempt = _object(attempts[0], f"{family_id}.{name}.attempt")
        if family_id == "bedrock-converse-stream":
            if (
                first_attempt.get("protocol") != "aws-event-stream"
                or first_attempt.get("crcBoundaryCoverage") is not True
            ):
                raise ProviderMockError(
                    f"{family_id}.{name} 必须覆盖 AWS EventStream CRC 边界"
                )
        elif first_attempt.get("finalEventWithoutBlankLine") is not True:
            raise ProviderMockError(
                f"{family_id}.{name} 必须覆盖 EOF 前无额外空行的完整 SSE 事件"
            )
    if name == "rate_limit_retry" and attempts[0].get("status") != 429:  # type: ignore[union-attr]
        raise ProviderMockError(f"{family_id}.{name} 首次响应必须为 429")
    if name == "server_error_retry" and attempts[0].get("status") != 503:  # type: ignore[union-attr]
        raise ProviderMockError(f"{family_id}.{name} 首次响应必须为 503")
    if (
        name == "server_error_retry"
        and attempts[0].get("retryAfter")  # type: ignore[union-attr]
        != "Wed, 21 Oct 2037 07:28:00 GMT"
    ):
        raise ProviderMockError(f"{family_id}.{name} 必须覆盖 HTTP-date Retry-After")

    expected = _object(case.get("expected"), f"{family_id}.{name}.expected")
    expected_terminals = {
        "text_success": "completed",
        "partial_tool_arguments": (
            "completed" if family_id in BASELINE_FAMILIES else "failed"
        ),
        "rate_limit_retry": "completed",
        "server_error_retry": "completed",
        "disconnect": "interrupted",
        "idle_timeout": "failed",
        "cancellation": "cancelled",
    }
    terminal = expected.get("terminal")
    if terminal != expected_terminals[name]:
        raise ProviderMockError(f"{family_id}.{name}.terminal 不符合固定故障语义")
    required_event_types = _array(
        expected.get("requiredEventTypes"),
        f"{family_id}.{name}.requiredEventTypes",
    )
    if f"run.{terminal}" not in required_event_types:
        raise ProviderMockError(f"{family_id}.{name} 未要求对应终态事件")
    min_requests = expected.get("minRequests")
    if not isinstance(min_requests, int) or isinstance(min_requests, bool):
        raise ProviderMockError(f"{family_id}.{name}.minRequests 必须是整数")
    if min_requests != len(attempts):
        raise ProviderMockError(f"{family_id}.{name}.minRequests 必须等于 attempt 数")
    exact_requests = expected.get("exactRequests")
    if family_id in BASELINE_FAMILIES and name == "partial_tool_arguments":
        if exact_requests != 2:
            raise ProviderMockError(
                f"{family_id}.{name}.exactRequests 必须精确固定为 2"
            )
        if expected.get("text") != "你好 👋 from mock.":
            raise ProviderMockError(f"{family_id}.{name}.text 未固定第二轮文本")
        if expected.get("toolCall") != {
            "id": "call_mock_1",
            "name": "file.read",
            "arguments": {"path": "README.md"},
        }:
            raise ProviderMockError(f"{family_id}.{name}.toolCall 未固定原生调用")
        if expected.get("toolResult") != {
            "content": [
                {"type": "text", "text": "provider tool continuation fixture\n"}
            ],
            "isError": False,
        }:
            raise ProviderMockError(f"{family_id}.{name}.toolResult 未固定真实读取结果")
        if expected.get("usage") != {
            "inputTokens": 14,
            "outputTokens": 10,
            "totalTokens": 24,
        }:
            raise ProviderMockError(f"{family_id}.{name}.usage 未固定两轮累计值")
        if required_event_types != [
            "tool.requested",
            "tool.completed",
            "message.delta",
            "run.completed",
        ]:
            raise ProviderMockError(
                f"{family_id}.{name}.requiredEventTypes 未固定完整工具续轮"
            )
    elif exact_requests is not None:
        raise ProviderMockError(
            f"{family_id}.{name} 不得声明首批工具续轮专用 exactRequests"
        )
    usage_value = expected.get("usage")
    if usage_value is not None:
        usage = _object(usage_value, f"{family_id}.{name}.usage")
        if usage.get("totalTokens") != usage.get("inputTokens", 0) + usage.get(
            "outputTokens", 0
        ):
            raise ProviderMockError(f"{family_id}.{name}.usage token 总数不一致")


def _validate_attempt(family_id: str, case_name: str, attempt: JSONObject) -> None:
    """功能：验证单个 attempt 的分片、HTTP 状态或等待参数均有界且完整。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    kind = _nonempty_string(attempt.get("kind"), f"{family_id}.{case_name}.kind")
    if kind in {"text_stream", "tool_stream"}:
        if attempt.get("protocol") == "aws-event-stream":
            if family_id != "bedrock-converse-stream":
                raise ProviderMockError(
                    f"{family_id}.{case_name} 不得使用 AWS EventStream attempt"
                )
            if attempt.get("crcBoundaryCoverage") is not True:
                raise ProviderMockError(
                    f"{family_id}.{case_name} 必须显式覆盖 CRC 边界"
                )
            sizes = _array(
                attempt.get("chunkSizes"), f"{family_id}.{case_name}.chunkSizes"
            )
            if not sizes or not all(
                isinstance(value, int)
                and not isinstance(value, bool)
                and 1 <= value <= 65536
                for value in sizes
            ):
                raise ProviderMockError(f"{family_id}.{case_name}.chunkSizes 无效")
            return
        if attempt.get("lineEnding") not in {"lf", "crlf"}:
            raise ProviderMockError(f"{family_id}.{case_name} 换行类型无效")
        if not isinstance(attempt.get("multiline"), bool):
            raise ProviderMockError(f"{family_id}.{case_name}.multiline 必须为布尔值")
        final_without_blank = attempt.get("finalEventWithoutBlankLine", False)
        if not isinstance(final_without_blank, bool):
            raise ProviderMockError(
                f"{family_id}.{case_name}.finalEventWithoutBlankLine 必须为布尔值"
            )
        sizes = _array(attempt.get("chunkSizes"), f"{family_id}.{case_name}.chunkSizes")
        if not sizes or not all(
            isinstance(value, int)
            and not isinstance(value, bool)
            and 1 <= value <= 65536
            for value in sizes
        ):
            raise ProviderMockError(f"{family_id}.{case_name}.chunkSizes 无效")
        return
    if kind == "disconnect_stream":
        if attempt.get("protocol") == "aws-event-stream":
            if (
                family_id != "bedrock-converse-stream"
                or attempt.get("crcBoundaryCoverage") is not True
            ):
                raise ProviderMockError(
                    f"{family_id}.{case_name} EventStream 断流契约无效"
                )
            sizes = _array(
                attempt.get("chunkSizes"), f"{family_id}.{case_name}.chunkSizes"
            )
            if not sizes or not all(
                isinstance(value, int)
                and not isinstance(value, bool)
                and 1 <= value <= 65536
                for value in sizes
            ):
                raise ProviderMockError(f"{family_id}.{case_name}.chunkSizes 无效")
            return
        if attempt.get("lineEnding") not in {"lf", "crlf"}:
            raise ProviderMockError(f"{family_id}.{case_name} 断流换行类型无效")
        sizes = _array(attempt.get("chunkSizes"), f"{family_id}.{case_name}.chunkSizes")
        if not sizes or not all(
            isinstance(value, int)
            and not isinstance(value, bool)
            and 1 <= value <= 65536
            for value in sizes
        ):
            raise ProviderMockError(f"{family_id}.{case_name}.chunkSizes 无效")
        return
    if kind == "http_error":
        if attempt.get("status") not in {429, 500, 502, 503, 504}:
            raise ProviderMockError(f"{family_id}.{case_name}.status 无效")
        if attempt.get("status") == 429 and attempt.get("retryAfter") != "0":
            raise ProviderMockError(
                f"{family_id}.{case_name} 429 必须使用 Retry-After: 0"
            )
        return
    if kind in {"idle_stream", "cancel_stream"}:
        idle_ms = attempt.get("idleMs")
        if (
            not isinstance(idle_ms, int)
            or isinstance(idle_ms, bool)
            or not 50 <= idle_ms <= 10000
        ):
            raise ProviderMockError(f"{family_id}.{case_name}.idleMs 无效")
        return
    raise ProviderMockError(f"{family_id}.{case_name} attempt kind 无效")


def family_by_id(fixture: JSONObject, family_id: str) -> JSONObject:
    """功能：按固定 family ID 返回夹具对象，未知 ID 明确失败。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    for value in _array(fixture.get("families"), "families"):
        family = _object(value, "family")
        if family.get("id") == family_id:
            return family
    raise ProviderMockError(f"未知 Provider family：{family_id}")


def case_by_name(family: JSONObject, case_name: str) -> JSONObject:
    """功能：按案例名称返回指定 family 的机器案例，未知名称明确失败。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    for value in _array(family.get("cases"), "family.cases"):
        case = _object(value, "case")
        if case.get("name") == case_name:
            return case
    raise ProviderMockError(f"未知 Provider mock case：{case_name}")


def _json_pointer(document: JSONValue, pointer: str) -> JSONValue:
    """功能：解析 RFC 6901 JSON 指针并返回目标值。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not pointer.startswith("/"):
        raise KeyError(pointer)
    current = document
    for raw_token in pointer[1:].split("/"):
        token = raw_token.replace("~1", "/").replace("~0", "~")
        if isinstance(current, dict) and token in current:
            current = current[token]
        elif isinstance(current, list) and token.isdecimal():
            index = int(token)
            if index >= len(current):
                raise KeyError(pointer)
            current = current[index]
        else:
            raise KeyError(pointer)
    return current


def summarize_request(document: JSONObject) -> JSONObject:
    """功能：生成不含字段值、prompt 或凭据的请求形状摘要。

    输入：已严格解析的 Provider 请求对象。
    输出：仅含顶层键、布尔形状和数组计数的脱敏对象。
    不变量：不复制 model、消息内容、工具参数或任意请求字符串值。
    失败：本函数不因未知字段失败，验证结果由调用方单独记录。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    summary: JSONObject = {"topLevelKeys": sorted(document)}
    for field in ("messages", "input", "contents", "tools"):
        value = document.get(field)
        summary[f"{field}Count"] = len(value) if isinstance(value, list) else None
    summary["modelPresent"] = isinstance(document.get("model"), str)
    summary["streamIsTrue"] = document.get("stream") is True
    summary["maxTokensPresent"] = "max_tokens" in document
    summary["toolConfigPresent"] = isinstance(document.get("toolConfig"), dict)
    return summary


def _validate_request_shape(family: JSONObject, document: JSONObject) -> list[str]:
    """功能：按夹具 JSON 指针检查请求必需字段与固定标量。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    request = _object(family.get("request"), "family.request")
    errors: list[str] = []
    for pointer_value in _array(
        request.get("requiredJsonPointers"), "request.requiredJsonPointers"
    ):
        pointer = _nonempty_string(pointer_value, "required JSON pointer")
        try:
            _json_pointer(document, pointer)
        except KeyError:
            errors.append(f"missing:{pointer}")
    equals = _object(request.get("equals"), "request.equals")
    for pointer, expected in equals.items():
        try:
            actual = _json_pointer(document, pointer)
        except KeyError:
            errors.append(f"missing:{pointer}")
            continue
        if actual != expected or type(actual) is not type(expected):
            errors.append(f"mismatch:{pointer}")
    return sorted(set(errors))


def _json_value_exact(actual: JSONValue, expected: JSONValue) -> bool:
    """功能：按 JSON 类型与递归结构精确比较两个值，避免 bool/number 混同。

    输入：两个已经严格解析的 JSON 值。
    输出：类型、对象键、数组顺序及叶子值均相同时返回 true。
    不变量：不序列化或记录实例正文，不接受 Python 的 `True == 1` 宽松比较。
    失败：未知运行时类型直接返回 false，不抛出含实例值的异常。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if type(actual) is not type(expected):
        return False
    if isinstance(actual, dict) and isinstance(expected, dict):
        return actual.keys() == expected.keys() and all(
            _json_value_exact(actual[key], expected[key]) for key in actual
        )
    if isinstance(actual, list) and isinstance(expected, list):
        return len(actual) == len(expected) and all(
            _json_value_exact(left, right) for left, right in zip(actual, expected)
        )
    return actual == expected


def _arguments_string_matches(value: JSONValue, expected: JSONObject) -> bool:
    """功能：严格解析有界原生参数字符串并与固定公共参数对象精确比较。

    输入：Chat/Responses 原生 arguments 值及夹具期望对象。
    输出：值为不超过 64 KiB 的严格 UTF-8 JSON 对象且结构精确匹配时返回 true。
    不变量：解析失败不回显 arguments，也不把参数保存到脱敏观测。
    失败：非法类型、过长、编码/JSON 错误均安全返回 false。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, str):
        return False
    raw = value.encode("utf-8")
    if not raw or len(raw) > 65_536:
        return False
    try:
        parsed = _parse_json_object(raw, "tool arguments")
    except ProviderMockError:
        return False
    return _json_value_exact(parsed, expected)


def _continuation_contract(
    family: JSONObject,
    case: JSONObject,
    request_number: int,
    document: JSONObject,
) -> tuple[list[str], JSONObject]:
    """功能：验证首批第二次请求中的 family-native 工具调用历史与工具结果。

    输入：family/case、从一开始的 HTTP 请求序号和严格请求对象。
    输出：不含正文的固定错误标签与仅含布尔值的续轮形状摘要。
    不变量：只对首批 `partial_tool_arguments` 的第二个请求启用；要求唯一调用、唯一
    结果、精确 ID/name/arguments、固定 README 结果、成功标记及调用先于结果。
    失败：结构差异转换为固定标签，不复制 prompt、工具输出或任意请求字符串。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    family_id = _nonempty_string(family.get("id"), "family.id")
    case_name = _nonempty_string(case.get("name"), "case.name")
    required = (
        family_id in BASELINE_FAMILIES
        and case_name == "partial_tool_arguments"
        and request_number == 2
    )
    summary: JSONObject = {
        "required": required,
        "nativeToolCallExact": False,
        "nativeToolResultCorrelated": False,
        "nativeToolResultNonEmpty": False,
        "nativeToolResultSuccessful": False,
        "nativeToolResultExact": False,
        "toolCallPrecedesResult": False,
    }
    if not required:
        return [], summary

    expected_value = _object(case.get("expected"), "case.expected")
    call = _object(expected_value.get("toolCall"), "expected.toolCall")
    call_id = _nonempty_string(call.get("id"), "expected.toolCall.id")
    call_name = _nonempty_string(call.get("name"), "expected.toolCall.name")
    arguments = _object(call.get("arguments"), "expected.toolCall.arguments")
    expected_result = _object(expected_value.get("toolResult"), "expected.toolResult")
    expected_content = _array(
        expected_result.get("content"), "expected.toolResult.content"
    )
    if len(expected_content) != 1:
        raise ProviderMockError("expected.toolResult.content 必须固定一个文本块")
    expected_block = _object(expected_content[0], "expected.toolResult.content[0]")
    expected_text = _nonempty_string(
        expected_block.get("text"), "expected.toolResult.content[0].text"
    )

    call_exact = False
    result_correlated = False
    result_nonempty = False
    result_successful = False
    result_exact = False
    call_position = -1
    result_position = -1

    if family_id == "openai-compatible":
        messages = document.get("messages")
        if isinstance(messages, list):
            native_calls: list[tuple[int, JSONObject]] = []
            native_results: list[tuple[int, JSONObject]] = []
            for index, message_value in enumerate(messages):
                if not isinstance(message_value, dict):
                    continue
                if message_value.get("role") == "assistant":
                    tool_calls = message_value.get("tool_calls")
                    if isinstance(tool_calls, list):
                        native_calls.extend(
                            (index, item)
                            for item in tool_calls
                            if isinstance(item, dict)
                        )
                if message_value.get("role") == "tool":
                    native_results.append((index, message_value))
            if len(native_calls) == 1:
                call_position, native_call = native_calls[0]
                function = native_call.get("function")
                call_exact = (
                    native_call.get("id") == call_id
                    and native_call.get("type") == "function"
                    and isinstance(function, dict)
                    and function.get("name") == call_name
                    and _arguments_string_matches(function.get("arguments"), arguments)
                )
            if len(native_results) == 1:
                result_position, native_result = native_results[0]
                result_correlated = native_result.get("tool_call_id") == call_id
                content = native_result.get("content")
                result_nonempty = isinstance(content, str) and bool(content.strip())
                result_successful = result_correlated and result_nonempty
                result_exact = result_correlated and content == expected_text
    elif family_id == "openai-responses":
        input_items = document.get("input")
        if isinstance(input_items, list):
            native_calls = [
                (index, item)
                for index, item in enumerate(input_items)
                if isinstance(item, dict) and item.get("type") == "function_call"
            ]
            native_results = [
                (index, item)
                for index, item in enumerate(input_items)
                if isinstance(item, dict) and item.get("type") == "function_call_output"
            ]
            if len(native_calls) == 1:
                call_position, native_call = native_calls[0]
                call_exact = (
                    native_call.get("call_id") == call_id
                    and native_call.get("name") == call_name
                    and _arguments_string_matches(
                        native_call.get("arguments"), arguments
                    )
                )
            if len(native_results) == 1:
                result_position, native_result = native_results[0]
                result_correlated = native_result.get("call_id") == call_id
                output = native_result.get("output")
                result_nonempty = isinstance(output, str) and bool(output.strip())
                result_successful = result_correlated and result_nonempty
                result_exact = result_correlated and output == expected_text
    elif family_id == "anthropic-messages":
        messages = document.get("messages")
        if isinstance(messages, list):
            native_calls = []
            native_results = []
            for index, message_value in enumerate(messages):
                if not isinstance(message_value, dict):
                    continue
                content = message_value.get("content")
                if not isinstance(content, list):
                    continue
                for block in content:
                    if not isinstance(block, dict):
                        continue
                    if (
                        message_value.get("role") == "assistant"
                        and block.get("type") == "tool_use"
                    ):
                        native_calls.append((index, block))
                    elif (
                        message_value.get("role") == "user"
                        and block.get("type") == "tool_result"
                    ):
                        native_results.append((index, block))
            if len(native_calls) == 1:
                call_position, native_call = native_calls[0]
                call_exact = (
                    native_call.get("id") == call_id
                    and native_call.get("name") == call_name
                    and _json_value_exact(native_call.get("input"), arguments)
                )
            if len(native_results) == 1:
                result_position, native_result = native_results[0]
                result_correlated = native_result.get("tool_use_id") == call_id
                content = native_result.get("content")
                result_nonempty = isinstance(content, str) and bool(content.strip())
                result_successful = (
                    result_correlated
                    and result_nonempty
                    and native_result.get("is_error") is False
                )
                result_exact = (
                    result_correlated
                    and content == expected_text
                    and native_result.get("is_error") is False
                )

    call_precedes_result = (
        call_position >= 0 and result_position >= 0 and call_position < result_position
    )
    summary.update(
        {
            "nativeToolCallExact": call_exact,
            "nativeToolResultCorrelated": result_correlated,
            "nativeToolResultNonEmpty": result_nonempty,
            "nativeToolResultSuccessful": result_successful,
            "nativeToolResultExact": result_exact,
            "toolCallPrecedesResult": call_precedes_result,
        }
    )
    errors: list[str] = []
    for field, label in (
        ("nativeToolCallExact", "continuation:tool-call"),
        ("nativeToolResultCorrelated", "continuation:tool-result-correlation"),
        ("nativeToolResultNonEmpty", "continuation:tool-result-empty"),
        ("nativeToolResultSuccessful", "continuation:tool-result-error"),
        ("nativeToolResultExact", "continuation:tool-result-content"),
        ("toolCallPrecedesResult", "continuation:order"),
    ):
        if summary.get(field) is not True:
            errors.append(label)
    return errors, summary


def _validate_request_target(
    family: JSONObject,
    suffix: str,
    raw_query: str,
) -> list[str]:
    """功能：验证第二批原生 API path/query，首批维持无查询参数兼容规则。

    输入：family 夹具、family/case 之后的原始 path 后缀和原始 query。
    输出：仅含固定错误标签的排序列表，不包含请求目标或查询值。
    不变量：第二批必须精确匹配快照提取的后缀和唯一查询键值；首批仍允许任意后缀。
    失败：本函数不抛出实例值；解析失败返回脱敏错误标签。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    contract_value = family.get("requestTarget")
    if contract_value is None:
        return ["target:unexpected-query"] if raw_query else []
    contract = _object(contract_value, "family.requestTarget")
    expected_suffix = _nonempty_string(
        contract.get("pathSuffix"), "requestTarget.pathSuffix"
    )
    errors: list[str] = []
    if suffix != expected_suffix:
        errors.append("target:path-suffix")
    expected_query = _object(
        contract.get("requiredQuery"), "requestTarget.requiredQuery"
    )
    try:
        pairs = parse_qsl(
            raw_query,
            keep_blank_values=True,
            strict_parsing=True,
            max_num_fields=16,
            encoding="utf-8",
            errors="strict",
        )
    except (UnicodeDecodeError, ValueError):
        return sorted({*errors, "target:query-syntax"})
    actual_query: dict[str, str] = {}
    for name, value in pairs:
        if name in actual_query:
            errors.append("target:duplicate-query")
            continue
        actual_query[name] = value
    if actual_query != expected_query:
        errors.append("target:query")
    return sorted(set(errors))


def _sse_event(
    event_type: str | None,
    payload: JSONObject,
    *,
    line_ending: str,
    multiline: bool,
) -> bytes:
    """功能：把 Provider 事件编码为可选择 CRLF 和多行 data 的 SSE 字节。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    newline = "\r\n" if line_ending == "crlf" else "\n"
    if multiline:
        serialized = json.dumps(payload, ensure_ascii=False, indent=2)
        data_lines = serialized.splitlines()
    else:
        serialized = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
        data_lines = [serialized]
    fields = [] if event_type is None else [f"event: {event_type}"]
    fields.extend(f"data: {line}" for line in data_lines)
    return (newline.join(fields) + newline + newline).encode("utf-8")


def _sse_literal(value: str, *, line_ending: str) -> bytes:
    """功能：把 `[DONE]` 等非 JSON 数据编码为一个完整 SSE 事件。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    newline = "\r\n" if line_ending == "crlf" else "\n"
    return f"data: {value}{newline}{newline}".encode("utf-8")


def _openai_chat_stream(
    kind: str,
    line_ending: str,
    multiline: bool,
    *,
    response_id: str = "chatcmpl_mock",
    tool_call_id: str = "call_mock_1",
) -> bytes:
    """功能：生成 OpenAI/Mistral Chat 形状的文本或 partial tool SSE 流。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    common = {"id": response_id, "object": "chat.completion.chunk"}
    payloads: list[JSONObject]
    if kind == "text_stream":
        payloads = [
            {
                **common,
                "choices": [
                    {"index": 0, "delta": {"role": "assistant"}, "finish_reason": None}
                ],
            },
            {
                **common,
                "choices": [
                    {"index": 0, "delta": {"content": "你好 "}, "finish_reason": None}
                ],
            },
            {
                **common,
                "choices": [
                    {
                        "index": 0,
                        "delta": {"content": "👋 from mock."},
                        "finish_reason": None,
                    }
                ],
            },
            {
                **common,
                "choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}],
            },
        ]
    else:
        fragments = ('{"path":"', "README", '.md"}')
        payloads = [
            {
                **common,
                "choices": [
                    {
                        "index": 0,
                        "delta": {
                            "role": "assistant",
                            "tool_calls": [
                                {
                                    "index": 0,
                                    "id": tool_call_id,
                                    "type": "function",
                                    "function": {
                                        "name": "file.read",
                                        "arguments": fragments[0],
                                    },
                                }
                            ],
                        },
                        "finish_reason": None,
                    }
                ],
            },
            *[
                {
                    **common,
                    "choices": [
                        {
                            "index": 0,
                            "delta": {
                                "tool_calls": [
                                    {
                                        "index": 0,
                                        "function": {"arguments": fragment},
                                    }
                                ]
                            },
                            "finish_reason": None,
                        }
                    ],
                }
                for fragment in fragments[1:]
            ],
            {
                **common,
                "choices": [{"index": 0, "delta": {}, "finish_reason": "tool_calls"}],
            },
        ]
    usage: JSONObject = {
        **common,
        "choices": [],
        "usage": {"prompt_tokens": 7, "completion_tokens": 5, "total_tokens": 12},
    }
    return b"".join(
        _sse_event(None, payload, line_ending=line_ending, multiline=multiline)
        for payload in [*payloads, usage]
    ) + _sse_literal("[DONE]", line_ending=line_ending)


def _openai_responses_stream(
    kind: str,
    line_ending: str,
    multiline: bool,
    *,
    response_id: str = "resp_mock",
    tool_call_id: str = "call_mock_1",
) -> bytes:
    """功能：生成 OpenAI/Azure Responses 文本或 partial arguments SSE 流。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    events: list[tuple[str, JSONObject]] = [
        (
            "response.created",
            {
                "type": "response.created",
                "response": {"id": response_id, "status": "in_progress", "output": []},
            },
        )
    ]
    if kind == "text_stream":
        events.extend(
            [
                (
                    "response.output_text.delta",
                    {
                        "type": "response.output_text.delta",
                        "item_id": "msg_mock",
                        "output_index": 0,
                        "content_index": 0,
                        "delta": "你好 ",
                    },
                ),
                (
                    "response.output_text.delta",
                    {
                        "type": "response.output_text.delta",
                        "item_id": "msg_mock",
                        "output_index": 0,
                        "content_index": 0,
                        "delta": "👋 from mock.",
                    },
                ),
                (
                    "response.output_text.done",
                    {
                        "type": "response.output_text.done",
                        "item_id": "msg_mock",
                        "output_index": 0,
                        "content_index": 0,
                        "text": "你好 👋 from mock.",
                    },
                ),
            ]
        )
    else:
        arguments = '{"path":"README.md"}'
        events.append(
            (
                "response.output_item.added",
                {
                    "type": "response.output_item.added",
                    "output_index": 0,
                    "item": {
                        "id": "fc_mock",
                        "type": "function_call",
                        "call_id": tool_call_id,
                        "name": "file.read",
                        "arguments": "",
                    },
                },
            )
        )
        for fragment in ('{"path":"', "README", '.md"}'):
            events.append(
                (
                    "response.function_call_arguments.delta",
                    {
                        "type": "response.function_call_arguments.delta",
                        "item_id": "fc_mock",
                        "output_index": 0,
                        "delta": fragment,
                    },
                )
            )
        events.append(
            (
                "response.function_call_arguments.done",
                {
                    "type": "response.function_call_arguments.done",
                    "item_id": "fc_mock",
                    "output_index": 0,
                    "arguments": arguments,
                },
            )
        )
    events.append(
        (
            "response.completed",
            {
                "type": "response.completed",
                "response": {
                    "id": response_id,
                    "status": "completed",
                    "output": [],
                    "usage": {
                        "input_tokens": 7,
                        "output_tokens": 5,
                        "total_tokens": 12,
                    },
                },
            },
        )
    )
    return b"".join(
        _sse_event(name, payload, line_ending=line_ending, multiline=multiline)
        for name, payload in events
    )


def _google_generative_ai_stream(
    kind: str,
    line_ending: str,
    multiline: bool,
    *,
    model_id: str = "mock-google-v1",
    response_id: str = "google_resp_mock",
    tool_call_id: str = "call_mock_google_1",
) -> bytes:
    """功能：生成 Google streamGenerateContent 原生 SSE 文本或完整函数参数响应。

    输入：文本/工具 attempt 类型、LF/CRLF 选择和多行 SSE 开关。
    输出：`data` 内为 GenerateContentResponse 的确定性 UTF-8 SSE 字节。
    不变量：Google functionCall.args 是一个完整 JSON 对象，不伪造字符串增量事件。
    失败：调用者已限制 kind；未知 kind 会按工具响应处理。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    payloads: list[JSONObject]
    if kind == "text_stream":
        payloads = [
            {
                "candidates": [
                    {
                        "content": {
                            "role": "model",
                            "parts": [{"text": "你好 "}],
                        },
                        "index": 0,
                    }
                ],
                "modelVersion": model_id,
                "responseId": response_id,
            },
            {
                "candidates": [
                    {
                        "content": {
                            "role": "model",
                            "parts": [{"text": "👋 from mock."}],
                        },
                        "finishReason": "STOP",
                        "index": 0,
                    }
                ],
                "usageMetadata": {
                    "promptTokenCount": 7,
                    "candidatesTokenCount": 5,
                    "totalTokenCount": 12,
                },
                "modelVersion": model_id,
                "responseId": response_id,
            },
        ]
    else:
        payloads = [
            {
                "candidates": [
                    {
                        "content": {
                            "role": "model",
                            "parts": [
                                {
                                    "functionCall": {
                                        "id": tool_call_id,
                                        "name": "file.read",
                                        "args": {"path": "README.md"},
                                    }
                                }
                            ],
                        },
                        "finishReason": "STOP",
                        "index": 0,
                    }
                ],
                "usageMetadata": {
                    "promptTokenCount": 7,
                    "candidatesTokenCount": 5,
                    "totalTokenCount": 12,
                },
                "modelVersion": model_id,
                "responseId": response_id,
            }
        ]
    return b"".join(
        _sse_event(None, payload, line_ending=line_ending, multiline=multiline)
        for payload in payloads
    )


def _aws_event_stream_headers(event_type: str) -> bytes:
    """功能：编码 Bedrock 事件所需的三个确定性 AWS EventStream 字符串 header。

    输入：合成 ConverseStream 事件类型。
    输出：按固定顺序编码的 EventStream typed-header 字节。
    不变量：仅接受短 ASCII 名称和值，类型固定为 string（7）。
    失败：事件类型越界或非 ASCII 时抛出 ProviderMockError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    headers = (
        (":message-type", "event"),
        (":event-type", event_type),
        (":content-type", "application/json"),
    )
    encoded = bytearray()
    for name, value in headers:
        try:
            name_bytes = name.encode("ascii")
            value_bytes = value.encode("ascii")
        except UnicodeEncodeError as exc:
            raise ProviderMockError("AWS EventStream header 必须是 ASCII") from exc
        if not 1 <= len(name_bytes) <= 255 or len(value_bytes) > 65535:
            raise ProviderMockError("AWS EventStream header 长度越界")
        encoded.extend(bytes((len(name_bytes),)))
        encoded.extend(name_bytes)
        encoded.extend(b"\x07")
        encoded.extend(struct.pack(">H", len(value_bytes)))
        encoded.extend(value_bytes)
    return bytes(encoded)


def _aws_event_stream_message(event_type: str, payload: JSONObject) -> bytes:
    """功能：生成含 prelude CRC 与 message CRC 的一个 AWS EventStream 消息。

    输入：ConverseStream 事件类型及 UTF-8 JSON 对象 payload。
    输出：确定性的完整二进制 EventStream message。
    不变量：长度为大端编码；两个 CRC 均按 IEEE CRC32 计算并限制到无符号 32 位。
    失败：header 或总消息超出 1 MiB 安全上限时抛出 ProviderMockError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    headers = _aws_event_stream_headers(event_type)
    payload_bytes = json.dumps(
        payload, ensure_ascii=False, separators=(",", ":")
    ).encode("utf-8")
    total_length = 16 + len(headers) + len(payload_bytes)
    if total_length > MAX_REQUEST_BYTES:
        raise ProviderMockError("AWS EventStream message 超出安全上限")
    prelude = struct.pack(">II", total_length, len(headers))
    prelude_crc = struct.pack(">I", zlib.crc32(prelude) & 0xFFFFFFFF)
    without_message_crc = prelude + prelude_crc + headers + payload_bytes
    message_crc = struct.pack(">I", zlib.crc32(without_message_crc) & 0xFFFFFFFF)
    return without_message_crc + message_crc


def decode_aws_event_stream(wire: bytes) -> list[tuple[dict[str, str], JSONObject]]:
    """功能：严格解码完整 AWS EventStream 字节并验证长度、typed header 与双 CRC。

    输入：一串由零个或多个完整 EventStream message 组成的有界字节。
    输出：按 wire 顺序排列的 header 映射与严格 UTF-8 JSON 对象。
    不变量：不接受残帧、重复 header、非 string header、CRC 差异或超限长度。
    失败：任一二进制/UTF-8/JSON 不变量不成立时抛出 ProviderMockError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if len(wire) > MAX_REQUEST_BYTES:
        raise ProviderMockError("AWS EventStream wire 超出安全上限")
    decoded: list[tuple[dict[str, str], JSONObject]] = []
    offset = 0
    while offset < len(wire):
        if len(wire) - offset < 12:
            raise ProviderMockError("AWS EventStream prelude 不完整")
        total_length, headers_length = struct.unpack_from(">II", wire, offset)
        if total_length < 16 or total_length > MAX_REQUEST_BYTES:
            raise ProviderMockError("AWS EventStream total length 无效")
        if headers_length > total_length - 16:
            raise ProviderMockError("AWS EventStream headers length 无效")
        end = offset + total_length
        if end > len(wire):
            raise ProviderMockError("AWS EventStream message 提前结束")
        prelude = wire[offset : offset + 8]
        (actual_prelude_crc,) = struct.unpack_from(">I", wire, offset + 8)
        if (zlib.crc32(prelude) & 0xFFFFFFFF) != actual_prelude_crc:
            raise ProviderMockError("AWS EventStream prelude CRC 无效")
        (actual_message_crc,) = struct.unpack_from(">I", wire, end - 4)
        if (zlib.crc32(wire[offset : end - 4]) & 0xFFFFFFFF) != actual_message_crc:
            raise ProviderMockError("AWS EventStream message CRC 无效")

        headers_start = offset + 12
        headers_end = headers_start + headers_length
        cursor = headers_start
        headers: dict[str, str] = {}
        while cursor < headers_end:
            name_length = wire[cursor]
            cursor += 1
            if name_length == 0 or cursor + name_length + 3 > headers_end:
                raise ProviderMockError("AWS EventStream header name 长度无效")
            try:
                name = wire[cursor : cursor + name_length].decode("ascii")
            except UnicodeDecodeError as exc:
                raise ProviderMockError("AWS EventStream header name 非 ASCII") from exc
            cursor += name_length
            value_type = wire[cursor]
            cursor += 1
            if value_type != 7:
                raise ProviderMockError("AWS EventStream 仅允许 string header")
            (value_length,) = struct.unpack_from(">H", wire, cursor)
            cursor += 2
            if cursor + value_length > headers_end:
                raise ProviderMockError("AWS EventStream header value 长度无效")
            try:
                value = wire[cursor : cursor + value_length].decode("utf-8")
            except UnicodeDecodeError as exc:
                raise ProviderMockError(
                    "AWS EventStream header value 非 UTF-8"
                ) from exc
            cursor += value_length
            if name in headers:
                raise ProviderMockError("AWS EventStream header 重复")
            headers[name] = value
        if cursor != headers_end:
            raise ProviderMockError("AWS EventStream header 边界无效")
        payload = _parse_json_object(
            wire[headers_end : end - 4], "AWS EventStream payload"
        )
        decoded.append((headers, payload))
        offset = end
    return decoded


def _bedrock_converse_stream(kind: str) -> bytes:
    """功能：生成 Bedrock ConverseStream 文本或 partial tool input EventStream。

    输入：`text_stream` 或 `tool_stream`。
    输出：由完整 AWS EventStream message 拼接而成的确定性二进制 wire。
    不变量：工具参数分三段字符串交付；usage 仅在 metadata 中交付。
    失败：未知 kind 抛出 ProviderMockError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if kind not in {"text_stream", "tool_stream"}:
        raise ProviderMockError("Bedrock stream kind 无效")
    events: list[tuple[str, JSONObject]] = [("messageStart", {"role": "assistant"})]
    stop_reason = "end_turn"
    if kind == "text_stream":
        for text_value in ("你好 ", "👋 from mock."):
            events.append(
                (
                    "contentBlockDelta",
                    {"delta": {"text": text_value}, "contentBlockIndex": 0},
                )
            )
    else:
        stop_reason = "tool_use"
        events.append(
            (
                "contentBlockStart",
                {
                    "start": {
                        "toolUse": {
                            "toolUseId": "call_mock_bedrock_1",
                            "name": "file.read",
                        }
                    },
                    "contentBlockIndex": 0,
                },
            )
        )
        for fragment in ('{"path":"', "README", '.md"}'):
            events.append(
                (
                    "contentBlockDelta",
                    {
                        "delta": {"toolUse": {"input": fragment}},
                        "contentBlockIndex": 0,
                    },
                )
            )
    events.extend(
        [
            ("contentBlockStop", {"contentBlockIndex": 0}),
            ("messageStop", {"stopReason": stop_reason}),
            (
                "metadata",
                {
                    "usage": {
                        "inputTokens": 7,
                        "outputTokens": 5,
                        "totalTokens": 12,
                    },
                    "metrics": {"latencyMs": 1},
                },
            ),
        ]
    )
    return b"".join(
        _aws_event_stream_message(event_type, payload) for event_type, payload in events
    )


def _anthropic_stream(kind: str, line_ending: str, multiline: bool) -> bytes:
    """功能：生成 Anthropic Messages 文本或 partial input_json SSE 流。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    events: list[tuple[str, JSONObject]] = [
        (
            "message_start",
            {
                "type": "message_start",
                "message": {
                    "id": "msg_mock",
                    "type": "message",
                    "role": "assistant",
                    "content": [],
                    "model": "mock-anthropic-v1",
                    "stop_reason": None,
                    "stop_sequence": None,
                    "usage": {"input_tokens": 7, "output_tokens": 0},
                },
            },
        )
    ]
    stop_reason = "end_turn"
    if kind == "text_stream":
        events.append(
            (
                "content_block_start",
                {
                    "type": "content_block_start",
                    "index": 0,
                    "content_block": {"type": "text", "text": ""},
                },
            )
        )
        for text in ("你好 ", "👋 from mock."):
            events.append(
                (
                    "content_block_delta",
                    {
                        "type": "content_block_delta",
                        "index": 0,
                        "delta": {"type": "text_delta", "text": text},
                    },
                )
            )
    else:
        stop_reason = "tool_use"
        events.append(
            (
                "content_block_start",
                {
                    "type": "content_block_start",
                    "index": 0,
                    "content_block": {
                        "type": "tool_use",
                        "id": "call_mock_1",
                        "name": "file.read",
                        "input": {},
                    },
                },
            )
        )
        for fragment in ('{"path":"', "README", '.md"}'):
            events.append(
                (
                    "content_block_delta",
                    {
                        "type": "content_block_delta",
                        "index": 0,
                        "delta": {"type": "input_json_delta", "partial_json": fragment},
                    },
                )
            )
    events.extend(
        [
            ("content_block_stop", {"type": "content_block_stop", "index": 0}),
            (
                "message_delta",
                {
                    "type": "message_delta",
                    "delta": {"stop_reason": stop_reason, "stop_sequence": None},
                    "usage": {"output_tokens": 5},
                },
            ),
            ("message_stop", {"type": "message_stop"}),
        ]
    )
    return b"".join(
        _sse_event(name, payload, line_ending=line_ending, multiline=multiline)
        for name, payload in events
    )


def render_stream(family_id: str, attempt: JSONObject) -> bytes:
    """功能：按 family 和 attempt 生成确定性的真实 Provider SSE wire 字节。

    输入：固定 family ID 及 kind 为 text_stream/tool_stream 的 attempt。
    输出：含原生 Provider 事件名、usage 和结束语义的 UTF-8 字节。
    不变量：相同输入得到逐字节相同输出，且工具参数分为三个片段。
    失败：未知 family 或非流式 kind 时抛出 ProviderMockError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    kind = _nonempty_string(attempt.get("kind"), "attempt.kind")
    if kind not in {"text_stream", "tool_stream"}:
        raise ProviderMockError(f"attempt kind 不是可渲染成功流：{kind}")
    if family_id == "bedrock-converse-stream":
        if attempt.get("protocol") != "aws-event-stream":
            raise ProviderMockError("Bedrock attempt 缺少 AWS EventStream protocol")
        return _bedrock_converse_stream(kind)

    line_ending = _nonempty_string(attempt.get("lineEnding"), "attempt.lineEnding")
    multiline = attempt.get("multiline") is True
    if family_id == "openai-compatible":
        wire = _openai_chat_stream(kind, line_ending, multiline)
    elif family_id == "openai-responses":
        wire = _openai_responses_stream(kind, line_ending, multiline)
    elif family_id == "anthropic-messages":
        wire = _anthropic_stream(kind, line_ending, multiline)
    elif family_id == "mistral-conversations":
        wire = _openai_chat_stream(
            kind,
            line_ending,
            multiline,
            response_id="mistral_resp_mock",
            tool_call_id="callmock1",
        )
    elif family_id == "azure-openai-responses":
        wire = _openai_responses_stream(
            kind,
            line_ending,
            multiline,
            response_id="azure_resp_mock",
            tool_call_id="call_mock_azure_1",
        )
    elif family_id == "google-generative-ai":
        wire = _google_generative_ai_stream(kind, line_ending, multiline)
    elif family_id == "google-vertex":
        wire = _google_generative_ai_stream(
            kind,
            line_ending,
            multiline,
            model_id="mock-vertex-v1",
            response_id="vertex_resp_mock",
            tool_call_id="call_mock_vertex_1",
        )
    elif family_id == "openai-codex-responses":
        wire = _openai_responses_stream(
            kind,
            line_ending,
            multiline,
            response_id="codex_resp_mock",
            tool_call_id="call_mock_codex_1",
        )
    else:
        raise ProviderMockError(f"未知 Provider family：{family_id}")
    if attempt.get("finalEventWithoutBlankLine") is True:
        return _remove_final_sse_blank_line(wire, line_ending)
    return wire


def _remove_final_sse_blank_line(wire: bytes, line_ending: str) -> bytes:
    """功能：移除最后一个 SSE 事件的空行分隔符并保留完整字段行终止符。

    输入：至少以两个规范换行结尾的完整 SSE wire 和换行类型。
    输出：最后字段行仍完整、但 EOF 前不再含事件空行的字节串。
    不变量：只移除一个 LF 或 CRLF，不改变 JSON、UTF-8 或此前事件边界。
    失败：wire 尾部不满足生成器不变量时抛出 ProviderMockError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    newline = b"\r\n" if line_ending == "crlf" else b"\n"
    if not wire.endswith(newline + newline):
        raise ProviderMockError("SSE wire 缺少可移除的最终空行")
    return wire[: -len(newline)]


def render_disconnect_prefix(family_id: str, line_ending: str | None) -> bytes:
    """功能：生成一个完整起始事件和一个残缺原生事件后供服务端断流。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if family_id == "bedrock-converse-stream":
        first = _aws_event_stream_message("messageStart", {"role": "assistant"})
        incomplete = _aws_event_stream_message(
            "contentBlockDelta",
            {"delta": {"text": "synthetic_incomplete"}, "contentBlockIndex": 0},
        )
        return first + incomplete[:10]

    line_ending = _nonempty_string(line_ending, "disconnect.lineEnding")
    if family_id in {"openai-compatible", "mistral-conversations"}:
        first = _sse_event(
            None,
            {
                "id": (
                    "mistral_resp_mock"
                    if family_id == "mistral-conversations"
                    else "chatcmpl_mock"
                ),
                "object": "chat.completion.chunk",
                "choices": [{"index": 0, "delta": {"content": "partial"}}],
            },
            line_ending=line_ending,
            multiline=False,
        )
    elif family_id in {
        "openai-responses",
        "azure-openai-responses",
        "openai-codex-responses",
    }:
        first = _sse_event(
            "response.created",
            {
                "type": "response.created",
                "response": {
                    "id": (
                        "azure_resp_mock"
                        if family_id == "azure-openai-responses"
                        else (
                            "codex_resp_mock"
                            if family_id == "openai-codex-responses"
                            else "resp_mock"
                        )
                    ),
                    "status": "in_progress",
                    "output": [],
                },
            },
            line_ending=line_ending,
            multiline=False,
        )
    elif family_id == "anthropic-messages":
        first = _sse_event(
            "message_start",
            {
                "type": "message_start",
                "message": {
                    "id": "msg_mock",
                    "type": "message",
                    "role": "assistant",
                    "content": [],
                    "model": "mock-anthropic-v1",
                    "stop_reason": None,
                    "stop_sequence": None,
                    "usage": {"input_tokens": 7, "output_tokens": 0},
                },
            },
            line_ending=line_ending,
            multiline=False,
        )
    elif family_id in {"google-generative-ai", "google-vertex"}:
        first = _sse_event(
            None,
            {
                "candidates": [
                    {
                        "content": {
                            "role": "model",
                            "parts": [{"text": "partial"}],
                        },
                        "index": 0,
                    }
                ],
                "responseId": (
                    "vertex_resp_mock"
                    if family_id == "google-vertex"
                    else "google_resp_mock"
                ),
            },
            line_ending=line_ending,
            multiline=False,
        )
    else:
        raise ProviderMockError(f"未知 Provider family：{family_id}")
    return first + b'data: {"type":"synthetic_incomplete"'


def render_wait_prefix(family_id: str) -> bytes:
    """功能：生成 idle/cancel 案例在等待前发送的一个完整 SSE 起始事件。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if family_id in {"openai-compatible", "mistral-conversations"}:
        return _sse_event(
            None,
            {
                "id": (
                    "mistral_resp_mock"
                    if family_id == "mistral-conversations"
                    else "chatcmpl_mock"
                ),
                "object": "chat.completion.chunk",
                "choices": [{"index": 0, "delta": {"role": "assistant"}}],
            },
            line_ending="lf",
            multiline=False,
        )
    if family_id in {
        "openai-responses",
        "azure-openai-responses",
        "openai-codex-responses",
    }:
        return _sse_event(
            "response.created",
            {
                "type": "response.created",
                "response": {
                    "id": (
                        "azure_resp_mock"
                        if family_id == "azure-openai-responses"
                        else (
                            "codex_resp_mock"
                            if family_id == "openai-codex-responses"
                            else "resp_mock"
                        )
                    ),
                    "status": "in_progress",
                    "output": [],
                },
            },
            line_ending="lf",
            multiline=False,
        )
    if family_id == "anthropic-messages":
        return _sse_event(
            "ping",
            {"type": "ping"},
            line_ending="lf",
            multiline=False,
        )
    if family_id in {"google-generative-ai", "google-vertex"}:
        return _sse_event(
            None,
            {
                "candidates": [],
                "responseId": (
                    "vertex_resp_mock"
                    if family_id == "google-vertex"
                    else "google_resp_mock"
                ),
            },
            line_ending="lf",
            multiline=False,
        )
    if family_id == "bedrock-converse-stream":
        return _aws_event_stream_message("messageStart", {"role": "assistant"})
    raise ProviderMockError(f"未知 Provider family：{family_id}")


def split_bytes(data: bytes, sizes: Sequence[int]) -> list[bytes]:
    """功能：循环给定大小把 wire 字节拆分到包括 UTF-8 continuation 的边界。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not sizes or any(size <= 0 for size in sizes):
        raise ProviderMockError("chunkSizes 必须是正整数非空数组")
    chunks: list[bytes] = []
    offset = 0
    index = 0
    while offset < len(data):
        size = sizes[index % len(sizes)]
        chunks.append(data[offset : offset + size])
        offset += size
        index += 1
    return chunks


class MockState:
    """线程安全保存请求次数和脱敏观测，不保存请求体或 header 值。"""

    def __init__(self, fixture: JSONObject) -> None:
        """功能：初始化只含机器夹具、计数器和脱敏观测的共享状态。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.fixture = fixture
        self._counts: dict[tuple[str, str], int] = {}
        self._observations: list[JSONObject] = []
        self._lock = threading.Lock()
        self.stop_event = threading.Event()

    def observe(
        self,
        family: JSONObject,
        case: JSONObject,
        headers: Mapping[str, str],
        document: JSONObject,
        target_errors: Sequence[str] = (),
    ) -> tuple[int, JSONObject, bool]:
        """功能：原子分配 attempt 并保存只含布尔值和形状的请求观测。

        输入：family/case 夹具、只读请求头、已解析请求对象和脱敏请求目标错误。
        输出：从 1 开始的 attempt 序号、选中 attempt 和请求是否合法。
        不变量：观测中不写入 header 值、完整请求、prompt、model 值或凭据。
        失败：夹具内部缺少 attempt 时抛出 ProviderMockError。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        family_id = _nonempty_string(family.get("id"), "family.id")
        case_name = _nonempty_string(case.get("name"), "case.name")
        key = (family_id, case_name)
        with self._lock:
            number = self._counts.get(key, 0) + 1
            self._counts[key] = number

        auth_observations: list[JSONValue] = []
        continuation_errors, continuation_shape = _continuation_contract(
            family,
            case,
            number,
            document,
        )
        validation_errors = [
            *_validate_request_shape(family, document),
            *target_errors,
            *continuation_errors,
        ]
        for value in _array(family.get("authHeaders"), "family.authHeaders"):
            contract = _object(value, "authHeader")
            name = _nonempty_string(contract.get("name"), "authHeader.name")
            prefix_value = contract.get("prefix")
            prefix = prefix_value if isinstance(prefix_value, str) else ""
            actual = headers.get(name, "")
            present = bool(actual)
            prefix_valid = (
                present and actual.startswith(prefix) and len(actual) > len(prefix)
            )
            auth_observations.append(
                {"name": name, "present": present, "prefixValid": prefix_valid}
            )
            if not prefix_valid:
                validation_errors.append(f"auth:{name.casefold()}")

        fixed_header_observations: list[JSONValue] = []
        fixed_headers_value = family.get("fixedHeaders", [])
        for value in _array(fixed_headers_value, "family.fixedHeaders"):
            contract = _object(value, "fixedHeader")
            name = _nonempty_string(contract.get("name"), "fixedHeader.name")
            prefix_value = contract.get("prefix")
            prefix = prefix_value if isinstance(prefix_value, str) else ""
            actual = headers.get(name, "")
            present = bool(actual)
            prefix_valid = present and actual.startswith(prefix)
            fixed_header_observations.append(
                {"name": name, "present": present, "prefixValid": prefix_valid}
            )
            if not prefix_valid:
                validation_errors.append(f"fixed-header:{name.casefold()}")

        required_header_presence: JSONObject = {}
        request = _object(family.get("request"), "family.request")
        for value in _array(request.get("requiredHeaders"), "request.requiredHeaders"):
            name = _nonempty_string(value, "required header")
            present = bool(headers.get(name))
            required_header_presence[name] = present
            if not present:
                validation_errors.append(f"header:{name}")

        valid = not validation_errors
        observation: JSONObject = {
            "family": family_id,
            "case": case_name,
            "attempt": number,
            "auth": auth_observations,
            "fixedHeaders": fixed_header_observations,
            "requiredHeadersPresent": required_header_presence,
            "requestShape": summarize_request(document),
            "continuationShape": continuation_shape,
            "requestTargetValid": not target_errors,
            "requestValid": valid,
            "validationErrors": sorted(set(validation_errors)),
        }
        with self._lock:
            self._observations.append(observation)

        attempts = _array(case.get("attempts"), "case.attempts")
        if not attempts:
            raise ProviderMockError("case.attempts 不得为空")
        selected = _object(attempts[min(number - 1, len(attempts) - 1)], "attempt")
        return number, selected, valid

    def observations(self) -> list[JSONObject]:
        """功能：返回脱敏观测的深拷贝，防止控制端修改服务状态。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with self._lock:
            return copy.deepcopy(self._observations)


class ProviderHTTPServer(ThreadingHTTPServer):
    """仅绑定 IPv4 loopback 的线程化 Provider mock HTTP 服务。"""

    daemon_threads = True
    allow_reuse_address = False

    def __init__(self, state: MockState) -> None:
        """功能：在 127.0.0.1 的内核分配端口上创建 HTTP 服务。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.mock_state = state
        super().__init__(("127.0.0.1", 0), ProviderMockRequestHandler)


class ProviderMockRequestHandler(BaseHTTPRequestHandler):
    """处理 Provider POST 和只读控制 GET，禁用默认访问日志。"""

    protocol_version = "HTTP/1.1"
    server_version = "provider-conformance-mock/0.1"
    sys_version = ""

    def log_message(self, format: str, *args: object) -> None:
        """功能：禁用可能记录路径、请求或 header 的默认 HTTP 日志。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        del format, args

    @property
    def state(self) -> MockState:
        """功能：返回类型明确的线程安全 mock 共享状态。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        server = self.server
        if not isinstance(server, ProviderHTTPServer):
            raise ProviderMockError("HTTP server 类型不正确")
        return server.mock_state

    def do_GET(self) -> None:
        """功能：提供不含敏感值的 health 与 observations 控制端点。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        parsed = urlsplit(self.path)
        if parsed.query or parsed.fragment:
            self._send_json(HTTPStatus.NOT_FOUND, {"error": "not_found"})
            return
        if parsed.path == "/_control/health":
            self._send_json(HTTPStatus.OK, {"ok": True})
            return
        if parsed.path == "/_control/observations":
            self._send_json(
                HTTPStatus.OK,
                {"observations": self.state.observations()},
            )
            return
        self._send_json(HTTPStatus.NOT_FOUND, {"error": "not_found"})

    def do_POST(self) -> None:
        """功能：验证请求形状/认证并执行确定性流、重试或故障 attempt。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        try:
            family, case, target_errors = self._route()
            document = self._read_request_object()
            _, attempt, valid = self.state.observe(
                family,
                case,
                self.headers,
                document,
                target_errors,
            )
            if not valid:
                self._send_json(
                    HTTPStatus.BAD_REQUEST,
                    {"error": "request_contract_violation"},
                )
                return
            self._serve_attempt(family, attempt)
        except ProviderMockError:
            self._send_json(HTTPStatus.BAD_REQUEST, {"error": "invalid_mock_request"})
        except (BrokenPipeError, ConnectionResetError, socket.timeout, OSError):
            self.close_connection = True

    def _route(self) -> tuple[JSONObject, JSONObject, list[str]]:
        """功能：解析固定 family/case，并脱敏验证第二批原生 API 后缀与查询。

        输入：HTTP handler 的原始 request-target。
        输出：family、case 与不含实例值的目标错误标签。
        不变量：family/case 必须属于当前夹具；fragment 永不接受。
        失败：路由身份无效时抛出 ProviderMockError，请求目标差异交给观测返回 400。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        parsed = urlsplit(self.path)
        if parsed.fragment:
            raise ProviderMockError("Provider mock 路径不得含 fragment")
        parts = [part for part in parsed.path.split("/") if part]
        allowed_families = fixture_family_ids(self.state.fixture)
        if len(parts) < 2 or parts[0] not in allowed_families:
            raise ProviderMockError("Provider mock 路径必须包含 family/case")
        family = family_by_id(self.state.fixture, parts[0])
        case = case_by_name(family, parts[1])
        prefix = f"/{parts[0]}/{parts[1]}"
        if not parsed.path.startswith(prefix):
            raise ProviderMockError("Provider mock 路径前缀无效")
        suffix = parsed.path[len(prefix) :]
        return family, case, _validate_request_target(family, suffix, parsed.query)

    def _read_request_object(self) -> JSONObject:
        """功能：按 Content-Length 和 1 MiB 上限读取严格 JSON 请求对象。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        raw_length = self.headers.get("Content-Length")
        if raw_length is None or not raw_length.isdecimal():
            raise ProviderMockError("Provider 请求必须提供十进制 Content-Length")
        length = int(raw_length)
        if length <= 0 or length > MAX_REQUEST_BYTES:
            raise ProviderMockError("Provider 请求体大小超出范围")
        raw = self.rfile.read(length)
        if len(raw) != length:
            raise ProviderMockError("Provider 请求体提前结束")
        return _parse_json_object(raw, "Provider 请求")

    def _serve_attempt(self, family: JSONObject, attempt: JSONObject) -> None:
        """功能：把选中的 HTTP error、SSE、断流、idle 或 cancel 行为写回客户端。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        family_id = _nonempty_string(family.get("id"), "family.id")
        kind = _nonempty_string(attempt.get("kind"), "attempt.kind")
        if kind == "http_error":
            status = attempt.get("status")
            if not isinstance(status, int) or isinstance(status, bool):
                raise ProviderMockError("HTTP error attempt 缺少整数 status")
            extra_headers = {}
            retry_after = attempt.get("retryAfter")
            if isinstance(retry_after, str):
                extra_headers["Retry-After"] = retry_after
            self._send_json(
                status,
                self._provider_error(family_id, status),
                extra_headers=extra_headers,
            )
            return

        self.send_response(HTTPStatus.OK)
        content_type = (
            "application/vnd.amazon.eventstream"
            if family_id == "bedrock-converse-stream"
            else "text/event-stream; charset=utf-8"
        )
        self.send_header("Content-Type", content_type)
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection", "close")
        self.end_headers()
        self.close_connection = True

        if kind in {"text_stream", "tool_stream"}:
            data = render_stream(family_id, attempt)
            sizes_value = _array(attempt.get("chunkSizes"), "attempt.chunkSizes")
            sizes = [int(value) for value in sizes_value if isinstance(value, int)]
            self._write_chunks(split_bytes(data, sizes))
            return
        if kind == "disconnect_stream":
            line_ending_value = attempt.get("lineEnding")
            line_ending = (
                _nonempty_string(line_ending_value, "attempt.lineEnding")
                if family_id != "bedrock-converse-stream"
                else None
            )
            data = render_disconnect_prefix(family_id, line_ending)
            sizes_value = _array(attempt.get("chunkSizes"), "attempt.chunkSizes")
            sizes = [int(value) for value in sizes_value if isinstance(value, int)]
            self._write_chunks(split_bytes(data, sizes))
            try:
                self.connection.shutdown(socket.SHUT_WR)
            except OSError:
                pass
            return
        if kind in {"idle_stream", "cancel_stream"}:
            self._write_chunks([render_wait_prefix(family_id)])
            idle_ms = attempt.get("idleMs")
            if not isinstance(idle_ms, int) or isinstance(idle_ms, bool):
                raise ProviderMockError("wait attempt 缺少整数 idleMs")
            self.state.stop_event.wait(idle_ms / 1000)
            return
        raise ProviderMockError(f"未知 attempt kind：{kind}")

    def _write_chunks(self, chunks: Sequence[bytes]) -> None:
        """功能：逐块刷新 SSE 字节并把客户端取消视为安全终止。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        try:
            for chunk in chunks:
                self.wfile.write(chunk)
                self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError, OSError):
            self.close_connection = True

    @staticmethod
    def _provider_error(family_id: str, status: int) -> JSONObject:
        """功能：按 family 构造无原始 body、prompt 或凭据的合成错误对象。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if family_id == "anthropic-messages":
            error_type = "rate_limit_error" if status == 429 else "overloaded_error"
            return {
                "type": "error",
                "error": {"type": error_type, "message": "synthetic provider error"},
            }
        if family_id == "mistral-conversations":
            return {
                "object": "error",
                "message": "synthetic provider error",
                "type": "rate_limit_error" if status == 429 else "server_error",
                "code": "mock_error",
            }
        if family_id in {"google-generative-ai", "google-vertex"}:
            return {
                "error": {
                    "code": status,
                    "message": "synthetic provider error",
                    "status": (
                        "RESOURCE_EXHAUSTED" if status == 429 else "UNAVAILABLE"
                    ),
                }
            }
        if family_id == "bedrock-converse-stream":
            return {
                "message": "synthetic provider error",
                "__type": (
                    "ThrottlingException"
                    if status == 429
                    else "ServiceUnavailableException"
                ),
            }
        error_type = "rate_limit_error" if status == 429 else "server_error"
        return {
            "error": {
                "message": "synthetic provider error",
                "type": error_type,
                "code": "mock_error",
            }
        }

    def _send_json(
        self,
        status: int,
        value: JSONObject,
        *,
        extra_headers: Mapping[str, str] | None = None,
    ) -> None:
        """功能：以有界紧凑 JSON 响应控制请求或合成 Provider 错误。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        data = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode(
            "utf-8"
        )
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Connection", "close")
        if extra_headers is not None:
            for name, header_value in extra_headers.items():
                self.send_header(name, header_value)
        self.end_headers()
        self.close_connection = True
        try:
            self.wfile.write(data)
            self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError, OSError):
            self.close_connection = True


class ProviderMockServer:
    """管理回环 mock 服务线程并提供精确 family/case endpoint。"""

    def __init__(self, fixture_path: Path = DEFAULT_FIXTURE) -> None:
        """功能：加载夹具并创建尚未启动的 ephemeral loopback 服务。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.state = MockState(load_fixture(fixture_path))
        self.httpd = ProviderHTTPServer(self.state)
        self._thread: threading.Thread | None = None

    @property
    def origin(self) -> str:
        """功能：返回只含 127.0.0.1 和内核分配端口的 HTTP origin。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        host, port = self.httpd.server_address[:2]
        if host != "127.0.0.1":
            raise ProviderMockError("Provider mock 未绑定到 IPv4 loopback")
        return f"http://127.0.0.1:{port}"

    def endpoint(self, family_id: str, case_name: str) -> str:
        """功能：返回指定固定 family/case 的精确 loopback endpoint。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        family = family_by_id(self.state.fixture, family_id)
        case_by_name(family, case_name)
        return f"{self.origin}/{family_id}/{case_name}"

    def start(self) -> None:
        """功能：在单独守护线程启动 HTTP serve_forever 循环。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if self._thread is not None:
            raise ProviderMockError("Provider mock 已启动")
        self._thread = threading.Thread(
            target=self.httpd.serve_forever,
            kwargs={"poll_interval": 0.05},
            name="provider-conformance-mock",
            daemon=True,
        )
        self._thread.start()

    def stop(self) -> None:
        """功能：唤醒等待中的 handler，关闭监听并回收服务线程。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.state.stop_event.set()
        if self._thread is not None:
            self.httpd.shutdown()
            self._thread.join(timeout=2.0)
            self._thread = None
        self.httpd.server_close()

    def __enter__(self) -> ProviderMockServer:
        """功能：启动 mock 并把实例返回给 with 语句。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.start()
        return self

    def __exit__(
        self,
        exc_type: type[BaseException] | None,
        exc: BaseException | None,
        traceback: object,
    ) -> None:
        """功能：无论 with 块是否异常都可靠停止 mock 服务。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        del exc_type, exc, traceback
        self.stop()


def build_argument_parser() -> argparse.ArgumentParser:
    """功能：构建独立 mock 服务和静态自检的命令行参数解析器。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser(
        description="Run the standard-library qxnm-forge provider conformance mock."
    )
    parser.add_argument("--fixture", type=Path, default=DEFAULT_FIXTURE)
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="validate and render every machine case, then exit",
    )
    return parser


def static_self_test(fixture_path: Path = DEFAULT_FIXTURE) -> tuple[int, int]:
    """功能：离线校验全部案例并渲染所有成功/断流/wait wire 数据。

    输入：Provider mock fixture 路径。
    输出：family 数与案例总数。
    不变量：不启动网络监听，不读取环境凭据，不调用真实 Provider。
    失败：夹具或任一 wire 渲染无效时抛出 ProviderMockError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    fixture = load_fixture(fixture_path)
    family_count = 0
    case_count = 0
    for family_value in _array(fixture.get("families"), "families"):
        family = _object(family_value, "family")
        family_id = _nonempty_string(family.get("id"), "family.id")
        family_count += 1
        for case_value in _array(family.get("cases"), "family.cases"):
            case = _object(case_value, "case")
            case_count += 1
            for attempt_value in _array(case.get("attempts"), "case.attempts"):
                attempt = _object(attempt_value, "attempt")
                kind = _nonempty_string(attempt.get("kind"), "attempt.kind")
                if kind in {"text_stream", "tool_stream"}:
                    wire = render_stream(family_id, attempt)
                    if family_id == "bedrock-converse-stream":
                        decoded = decode_aws_event_stream(wire)
                        if not decoded or not all(
                            headers.get(":message-type") == "event"
                            and headers.get(":content-type") == "application/json"
                            for headers, _ in decoded
                        ):
                            raise ProviderMockError(
                                "Bedrock 成功流未生成有效 AWS EventStream"
                            )
                    elif not wire or b"data:" not in wire:
                        raise ProviderMockError("成功流未生成 SSE data")
                    sizes = [
                        int(value)
                        for value in _array(attempt.get("chunkSizes"), "chunkSizes")
                        if isinstance(value, int)
                    ]
                    if b"".join(split_bytes(wire, sizes)) != wire:
                        raise ProviderMockError("chunkSizes 未无损重组 wire")
                elif kind == "disconnect_stream":
                    line_ending_value = attempt.get("lineEnding")
                    line_ending = (
                        _nonempty_string(line_ending_value, "attempt.lineEnding")
                        if family_id != "bedrock-converse-stream"
                        else None
                    )
                    disconnect_wire = render_disconnect_prefix(family_id, line_ending)
                    if family_id == "bedrock-converse-stream":
                        try:
                            decode_aws_event_stream(disconnect_wire)
                        except ProviderMockError:
                            pass
                        else:
                            raise ProviderMockError("Bedrock 断流案例未包含残帧")
                    elif b"synthetic_incomplete" not in disconnect_wire:
                        raise ProviderMockError("断流案例缺少残缺事件")
                elif kind in {"idle_stream", "cancel_stream"}:
                    wait_wire = render_wait_prefix(family_id)
                    if family_id == "bedrock-converse-stream":
                        if len(decode_aws_event_stream(wait_wire)) != 1:
                            raise ProviderMockError("Bedrock 等待案例缺少起始事件")
                    elif b"data:" not in wait_wire:
                        raise ProviderMockError("等待案例缺少起始 SSE 事件")
                elif kind != "http_error":
                    raise ProviderMockError(f"未知 attempt kind：{kind}")
    return family_count, case_count


def main(argv: Sequence[str] | None = None) -> int:
    """功能：运行静态自检，或启动只监听 ephemeral IPv4 loopback 的 mock。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = build_argument_parser().parse_args(argv)
    try:
        if args.self_test:
            family_count, case_count = static_self_test(args.fixture)
            print(f"PASS: provider mock {family_count} families / {case_count} cases")
            return 0
        with ProviderMockServer(args.fixture) as server:
            print(f"READY {server.origin}", flush=True)
            while not server.state.stop_event.wait(0.25):
                pass
    except ProviderMockError as exc:
        print(f"provider mock failed: {exc}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        return 0
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
