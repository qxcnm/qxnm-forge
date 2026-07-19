#!/usr/bin/env python3
"""六条 manifest 驱动 canonical Provider route 的离线静态与动态门禁。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
import copy
import json
import os
from pathlib import Path
import queue
import secrets
import socketserver
import tempfile
import threading
import time
from typing import Any, Sequence
from urllib.parse import urlsplit

import provider_identity_runner as identity_runner
import provider_image_runner
import provider_mock
import provider_runner
import runner
import schema_validation


JSONObject = dict[str, Any]
RouteKey = tuple[str, str]

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_FIXTURE = ROOT / "CONFORMANCE/fixtures/provider-route/route-spine-cases.json"
DEFAULT_MANIFEST = ROOT / "SPEC/providers.v1.json"
DEFAULT_CATALOG = ROOT / "SPEC/models.v1.json"
DEFAULT_GOLDEN_REQUESTS = (
    ROOT / "CONFORMANCE/fixtures/golden/provider-route-groq.requests.ndjson"
)
DEFAULT_GOLDEN_TRACE = (
    ROOT / "CONFORMANCE/fixtures/golden/provider-route-groq.trace.ndjson"
)
MOCK_ROOT = ROOT / "CONFORMANCE/fixtures/provider"
CONFIG_ENV = "AGENT_PROVIDER_ROUTE_CONFORMANCE_CONFIG"
DEAD_PROXY = "http://127.0.0.1:9"
MAX_SCAN_FILE_BYTES = 4 * 1024 * 1024
MAX_SCAN_TOTAL_BYTES = 16 * 1024 * 1024
SPINE_KEYS: tuple[RouteKey, ...] = (
    ("google", "google-generative-ai"),
    ("groq", "openai-completions"),
    ("minimax", "anthropic-messages"),
    ("mistral", "mistral-conversations"),
    ("openai", "openai-responses"),
    ("openrouter", "openrouter-images"),
)
PRODUCTION_ENDPOINTS: dict[RouteKey, str] = {
    (
        "google",
        "google-generative-ai",
    ): "https://generativelanguage.googleapis.com/v1beta",
    ("groq", "openai-completions"): "https://api.groq.com/openai/v1",
    ("minimax", "anthropic-messages"): "https://api.minimax.io/anthropic",
    ("mistral", "mistral-conversations"): "https://api.mistral.ai",
    ("openai", "openai-responses"): "https://api.openai.com/v1",
    ("openrouter", "openrouter-images"): "https://openrouter.ai/api/v1",
}
REQUEST_TARGETS: dict[RouteKey, JSONObject] = {
    ("google", "google-generative-ai"): {
        "pathSuffix": "/models/gemini-2.5-flash:streamGenerateContent",
        "requiredQuery": {"alt": "sse"},
    },
    ("groq", "openai-completions"): {
        "pathSuffix": "/chat/completions",
        "requiredQuery": {},
    },
    ("minimax", "anthropic-messages"): {
        "pathSuffix": "/v1/messages",
        "requiredQuery": {},
    },
    ("mistral", "mistral-conversations"): {
        "pathSuffix": "/v1/chat/completions",
        "requiredQuery": {},
    },
    ("openai", "openai-responses"): {
        "pathSuffix": "/responses",
        "requiredQuery": {},
    },
    ("openrouter", "openrouter-images"): {
        "pathSuffix": "/chat/completions",
        "requiredQuery": {},
    },
}
AUTH_HEADERS: dict[str, list[JSONObject]] = {
    "anthropic-messages": [{"name": "x-api-key", "prefix": ""}],
    "google-generative-ai": [{"name": "x-goog-api-key", "prefix": ""}],
    "mistral-conversations": [{"name": "Authorization", "prefix": "Bearer "}],
    "openai-completions": [{"name": "Authorization", "prefix": "Bearer "}],
    "openai-responses": [{"name": "Authorization", "prefix": "Bearer "}],
}
TEXT_FEATURES = ["authentication", "text", "streaming", "tools"]
IMAGE_FEATURES = ["authentication", "text", "image_input", "image_output"]
NEGATIVE_KINDS = (
    "missing_credential",
    "wrong_family",
    "unknown_model",
    "unlisted_route_config",
    "production_presence",
    "endpoint_non_loopback",
    "config_unknown_field",
    "config_duplicate_key",
    "wire_duplicate_key",
)


class ProviderRouteRunnerError(Exception):
    """表示 route spine 的静态、动态或安全门禁失败。"""


def require_object(value: Any, label: str) -> JSONObject:
    """功能：把动态 JSON 值收窄为对象。

    输入：任意值与安全标签。输出：原对象。失败：类型不符时抛门禁错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, dict):
        raise ProviderRouteRunnerError(f"{label} must be an object")
    return value


def require_list(value: Any, label: str) -> list[Any]:
    """功能：把动态 JSON 值收窄为数组。

    输入：任意值与安全标签。输出：原数组。失败：类型不符时抛门禁错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, list):
        raise ProviderRouteRunnerError(f"{label} must be an array")
    return value


def load_json(path: Path) -> JSONObject:
    """功能：复用 identity runner 的有界严格 JSON loader。

    输入：仓库 JSON 路径。输出：无重复键的顶层对象。失败：I/O/JSON 异常时脱敏失败。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        return identity_runner.load_json(path)
    except identity_runner.ProviderIdentityRunnerError as exc:
        raise ProviderRouteRunnerError(str(exc)) from exc


def validate_local_schema(
    value: JSONObject, schema_name: str, schema_root: Path
) -> None:
    """功能：用只注册 bundled 资源的 Draft 2020-12 Schema 验证文档。

    输入：实例、Schema 文件名与本地 Schema 根。输出：成功时无返回值。
    不变量：不解析网络引用。失败：依赖、Schema 或实例无效时抛脱敏门禁错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        import jsonschema
        from referencing import Registry, Resource
    except ImportError as exc:
        raise ProviderRouteRunnerError("jsonschema/referencing is required") from exc
    resources: list[tuple[str, Any]] = []
    selected: JSONObject | None = None
    try:
        for path in sorted(schema_root.rglob("*.schema.json")):
            schema = load_json(path)
            resources.append((str(schema["$id"]), Resource.from_contents(schema)))
            if path.name == schema_name:
                selected = schema
        if selected is None:
            raise ProviderRouteRunnerError("required route Schema is missing")
        validator = jsonschema.Draft202012Validator(
            selected, registry=Registry().with_resources(resources)
        )
        errors = list(validator.iter_errors(value))
    except (OSError, KeyError, jsonschema.SchemaError) as exc:
        raise ProviderRouteRunnerError("route Schema cannot be prepared") from exc
    if errors:
        raise ProviderRouteRunnerError(f"{schema_name} rejected the route document")


def route_fixture_index(fixture: JSONObject) -> dict[RouteKey, JSONObject]:
    """功能：建立六条 fixture route 的唯一索引并冻结顺序与负例集合。

    输入：严格 route fixture。输出：按 `(providerId, apiFamily)` 索引的 route。
    失败：计数、顺序、名称或身份漂移时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    counts = require_object(fixture.get("counts"), "fixture.counts")
    if counts != {"routes": 6, "textRoutes": 5, "imageRoutes": 1, "negativeCases": 9}:
        raise ProviderRouteRunnerError("route fixture counts drifted")
    routes = [
        require_object(item, "fixture route")
        for item in require_list(fixture.get("routes"), "fixture.routes")
    ]
    result: dict[RouteKey, JSONObject] = {}
    names: set[str] = set()
    for route in routes:
        key = (route.get("providerId"), route.get("apiFamily"))
        name = route.get("name")
        if not all(isinstance(item, str) for item in key) or not isinstance(name, str):
            raise ProviderRouteRunnerError("route fixture identity is invalid")
        typed_key = (str(key[0]), str(key[1]))
        if typed_key in result or name in names:
            raise ProviderRouteRunnerError("route fixture identity is duplicated")
        result[typed_key] = route
        names.add(name)
    if tuple(sorted(result)) != SPINE_KEYS:
        raise ProviderRouteRunnerError("route fixture allowlist drifted")
    negative = [
        str(require_object(item, "negative case").get("kind"))
        for item in require_list(fixture.get("negativeCases"), "negativeCases")
    ]
    if tuple(negative) != NEGATIVE_KINDS:
        raise ProviderRouteRunnerError("route negative coverage drifted")
    return result


def validate_endpoint_base(value: Any, expected: str) -> None:
    """功能：验证 catalog endpoint 是精确固定 HTTPS origin/base。

    输入：endpoint 对象与冻结 base。输出：精确匹配时无返回值。
    不变量：禁止 userinfo、query、fragment 与非 HTTPS。失败：任一差异时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    endpoint = require_object(value, "catalog endpoint")
    if endpoint != {"strategy": "fixed", "baseUrl": expected}:
        raise ProviderRouteRunnerError(
            "catalog endpoint differs from frozen route base"
        )
    parsed = urlsplit(expected)
    if (
        parsed.scheme != "https"
        or not parsed.hostname
        or parsed.username is not None
        or parsed.password is not None
        or parsed.query
        or parsed.fragment
        or "\\" in expected
    ):
        raise ProviderRouteRunnerError(
            "production endpoint is not a safe fixed HTTPS base"
        )


def validate_manifest_route(
    route_case: JSONObject,
    context: JSONObject,
    adapter_index: dict[str, JSONObject],
) -> None:
    """功能：逐字段核对 route、adapter、API-key auth、header 与空 quirk。

    输入：fixture route、manifest route/provider 上下文及 adapter 索引。输出：一致时无返回值。
    不变量：六条 spine 仅接受一个 secret 环境 API key 和默认 header policy。
    失败：引用、字段、认证、header 或 quirk 漂移时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    provider = require_object(context.get("provider"), "manifest Provider")
    route = require_object(context.get("route"), "manifest route")
    expected_media = (
        "image" if route_case["apiFamily"] == "openrouter-images" else "text"
    )
    if (
        route.get("media") != expected_media
        or route.get("adapterId") != route_case.get("adapterId")
        or route.get("modelCatalogId") != "pi-v1-frozen"
        or route.get("headerPolicyId") != "api-family-default"
        or route.get("quirks") != []
        or route.get("authProfileIds") != ["api-key"]
        or route.get("endpoint")
        != {
            "source": "model-catalog",
            "templateBindings": [],
            "runtimeEndpointEnv": [],
            "runtimeOverride": "explicit-configuration-only",
        }
    ):
        raise ProviderRouteRunnerError("manifest executable route contract drifted")
    adapter = adapter_index.get(str(route_case.get("adapterId")))
    if adapter is None or adapter.get("apiFamily") != route_case.get("apiFamily"):
        raise ProviderRouteRunnerError("manifest adapter binding drifted")
    credential = route_case.get("credentialEnv")
    expected_profile = {"id": "api-key", "kind": "api-key", "environment": [credential]}
    expected_environment = [{"name": credential, "role": "secret"}]
    if (
        provider.get("authProfiles") != [expected_profile]
        or provider.get("environment") != expected_environment
    ):
        raise ProviderRouteRunnerError("manifest route authentication binding drifted")


def validate_mock_contract(route_case: JSONObject) -> None:
    """功能：把 route fixture 与既有 family-native text/image mock 契约交叉核对。

    输入：一条 route fixture。输出：family、case、request target 与 header 契约一致时无返回值。
    不变量：复用既有 mock loader/语义，不复制 SSE 或图像 family parser。
    失败：fixture、family、case、认证 header 或请求目标漂移时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    target = require_object(route_case.get("requestTarget"), "route requestTarget")
    if route_case.get("apiFamily") == "openrouter-images":
        image_fixture = provider_image_runner.strict_load(
            MOCK_ROOT / str(route_case["mockFixture"])
        )
        family, cases = provider_image_runner.validate_semantics(image_fixture)
        if (
            family.get("apiFamily") != route_case.get("apiFamily")
            or family.get("providerId") != route_case.get("providerId")
            or family.get("modelId") != route_case.get("modelId")
            or family.get("credentialEnv") != route_case.get("credentialEnv")
            or family.get("request", {}).get("pathSuffix") != target.get("pathSuffix")
            or target.get("requiredQuery") != {}
            or family.get("request", {}).get("requiredHeaders")
            != ["authorization", "content-type"]
            or not any(item.get("name") == route_case.get("mockCase") for item in cases)
        ):
            raise ProviderRouteRunnerError("image mock route contract drifted")
        return
    fixture = provider_mock.load_fixture(MOCK_ROOT / str(route_case["mockFixture"]))
    family = provider_mock.family_by_id(fixture, str(route_case["mockFamily"]))
    case = provider_mock.case_by_name(family, str(route_case["mockCase"]))
    if family.get("apiFamily") != route_case.get("apiFamily"):
        raise ProviderRouteRunnerError("text mock API family drifted")
    if family.get("authHeaders") != AUTH_HEADERS.get(str(route_case.get("apiFamily"))):
        raise ProviderRouteRunnerError("text mock authentication header drifted")
    if family.get("fixedHeaders") not in (None, []):
        raise ProviderRouteRunnerError("default header policy gained fixed headers")
    if case.get("expected", {}).get("terminal") != "completed":
        raise ProviderRouteRunnerError("text mock happy path is not completed")
    mock_target = family.get("requestTarget")
    if mock_target is not None and mock_target != target:
        if route_case.get("apiFamily") != "google-generative-ai" or mock_target != {
            "pathSuffix": "/models/mock-google-v1:streamGenerateContent",
            "requiredQuery": {"alt": "sse"},
        }:
            raise ProviderRouteRunnerError("text mock native request target drifted")


def validate_static_suite(
    fixture: JSONObject,
    manifest: JSONObject,
    catalog: JSONObject,
    *,
    schema_root: Path | None,
) -> tuple[
    dict[RouteKey, JSONObject],
    dict[RouteKey, JSONObject],
    dict[RouteKey, list[JSONObject]],
]:
    """功能：执行 ADR 0018 六 route 的完整静态交叉门禁。

    输入：route fixture、manifest、catalog 与可选 Schema 根。输出：fixture/manifest/catalog 索引。
    不变量：严格核对 allowlist、auth/header/quirk/endpoint、descriptor 和 133 个模型计数。
    失败：Schema 或任一交叉事实漂移时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if schema_root is not None:
        validate_local_schema(fixture, "provider-route-cases.schema.json", schema_root)
        validate_local_schema(manifest, "provider-manifest.schema.json", schema_root)
        validate_local_schema(catalog, "model-catalog.schema.json", schema_root)
    fixture_index = route_fixture_index(fixture)
    validate_groq_golden(
        fixture_index[("groq", "openai-completions")], schema_root=schema_root
    )
    try:
        manifest_index, _ = identity_runner.index_manifest(manifest)
        catalog_index = identity_runner.index_catalog(catalog, manifest_index)
    except identity_runner.ProviderIdentityRunnerError as exc:
        raise ProviderRouteRunnerError(str(exc)) from exc
    adapter_index = {
        str(item["id"]): require_object(item, "manifest adapter")
        for item in require_list(manifest.get("adapters"), "manifest.adapters")
    }
    total_models = 0
    for key in SPINE_KEYS:
        route_case = fixture_index[key]
        if route_case.get("requestTarget") != REQUEST_TARGETS[key]:
            raise ProviderRouteRunnerError("spine native request target drifted")
        context = manifest_index.get(key)
        if context is None:
            raise ProviderRouteRunnerError("spine route is absent from manifest")
        validate_manifest_route(route_case, context, adapter_index)
        models = catalog_index.get(key, [])
        if len(models) != route_case.get("productionModelCount"):
            raise ProviderRouteRunnerError("spine catalog model count drifted")
        total_models += len(models)
        expected_endpoint = PRODUCTION_ENDPOINTS[key]
        for model in models:
            validate_endpoint_base(model.get("endpoint"), expected_endpoint)
        selected = [
            item for item in models if item.get("modelId") == route_case.get("modelId")
        ]
        if len(selected) != 1:
            raise ProviderRouteRunnerError("spine representative model is not unique")
        expected_features = (
            IMAGE_FEATURES if key[1] == "openrouter-images" else TEXT_FEATURES
        )
        if route_case.get("providerFeatures") != expected_features:
            raise ProviderRouteRunnerError("spine capability allowance drifted")
        descriptor = identity_runner.normalized_model(
            selected[0], set(expected_features)
        )
        if descriptor != route_case.get("expectedDescriptor"):
            raise ProviderRouteRunnerError("spine model descriptor projection drifted")
        validate_mock_contract(route_case)
    if total_models != 133:
        raise ProviderRouteRunnerError("spine total catalog census drifted")
    return fixture_index, manifest_index, catalog_index


def route_config(route_case: JSONObject, endpoint_base: str) -> JSONObject:
    """功能：构造不含 credential 的单 route、单模型 strict conformance 配置。

    输入：fixture route 与 literal-loopback endpoint base。输出：五字段配置对象。
    不变量：不携带 adapter/header/quirk/credential 名或值。失败：调用方负责 Schema 验证。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "schemaVersion": "0.1",
        "providerId": route_case["providerId"],
        "apiFamily": route_case["apiFamily"],
        "modelId": route_case["modelId"],
        "endpointBase": endpoint_base,
    }


def initialize_request() -> JSONObject:
    """功能：构造 route spine 动态门禁 initialize 请求。

    输出：只协商协议 0.1 的 JSON-RPC 请求。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": "route-initialize",
        "method": "initialize",
        "params": {
            "protocolVersions": ["0.1"],
            "client": {"name": "provider-route-conformance", "version": "0.1.0"},
            "capabilities": {},
        },
    }


def models_request(provider_id: str) -> JSONObject:
    """功能：构造按 canonical Provider 过滤的 models/list 请求。

    输入：fixture providerId。输出：JSON-RPC 请求。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": "route-models",
        "method": "models/list",
        "params": {"providerId": provider_id},
    }


def run_start_request(
    route_case: JSONObject,
    *,
    session_id: str = "provider-route-session",
    family: str | None = None,
    model_id: str | None = None,
) -> JSONObject:
    """功能：构造显式携带 apiFamily 的 canonical run/start 请求。

    输入：route、Session ID 及可选 family/model 负例覆盖。输出：完整 JSON-RPC 请求。
    不变量：provider selection 始终含 id/modelId/apiFamily；图像 prompt 固定为既有 mock 文本。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    text = (
        "生成一张确定性本地图像。"
        if route_case.get("apiFamily") == "openrouter-images"
        else "Return the deterministic local mock response."
    )
    return {
        "jsonrpc": "2.0",
        "id": "route-run-start",
        "method": "run/start",
        "params": {
            "sessionId": session_id,
            "input": {"role": "user", "content": [{"type": "text", "text": text}]},
            "provider": {
                "id": route_case["providerId"],
                "modelId": model_id or route_case["modelId"],
                "apiFamily": family or route_case["apiFamily"],
            },
        },
    }


def validate_groq_golden(route_case: JSONObject, *, schema_root: Path | None) -> None:
    """功能：验证 canonical Groq route 的请求与规范化终态 golden 契约。

    输入：冻结 Groq route 案例与可选本地 Schema 根。输出：语义、生命周期和 Schema
    全部一致时无返回值。不变量：golden 只表达合成回环 credential 场景，不携带
    credential、endpoint、presence 路径或生产 HTTP 内部信息；两个文本 delta 的拼接值
    必须等于既有 family-native mock 正文。失败：文件、严格 NDJSON、身份、descriptor、
    生命周期、隐私或 Schema 任一漂移时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        requests = runner.load_ndjson(DEFAULT_GOLDEN_REQUESTS)
        trace = runner.load_ndjson(DEFAULT_GOLDEN_TRACE)
    except runner.ConformanceError as exc:
        raise ProviderRouteRunnerError(
            "provider route golden cannot be loaded"
        ) from exc

    expected_requests = [
        initialize_request(),
        models_request("groq"),
        run_start_request(route_case),
    ]
    if requests != expected_requests:
        raise ProviderRouteRunnerError("provider route golden requests drifted")
    if len(trace) != 10:
        raise ProviderRouteRunnerError("provider route golden frame count drifted")

    expected_initialize = {
        "protocolVersion": "0.1",
        "implementation": {
            "name": "$IMPLEMENTATION",
            "version": "$VERSION",
            "language": "$LANGUAGE",
        },
        "capabilities": {
            "methods": ["initialize", "models/list", "run/start"],
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
                {"id": "groq", "models": [route_case["modelId"]]},
            ],
            "tools": [],
            "transports": ["stdio"],
        },
        "limits": {
            "maxFrameBytes": 1_048_576,
            "maxEventBytes": 262_144,
            "maxArtifactBytes": 1_073_741_824,
            "maxConcurrentRuns": 1,
        },
    }
    if trace[0] != {
        "jsonrpc": "2.0",
        "id": "route-initialize",
        "result": expected_initialize,
    }:
        raise ProviderRouteRunnerError("provider route golden initialize drifted")
    if trace[1] != {
        "jsonrpc": "2.0",
        "id": "route-models",
        "result": {"models": [route_case["expectedDescriptor"]]},
    }:
        raise ProviderRouteRunnerError("provider route golden descriptor drifted")
    if trace[2] != {
        "jsonrpc": "2.0",
        "id": "route-run-start",
        "result": {"runId": "route-run-1"},
    }:
        raise ProviderRouteRunnerError("provider route golden acceptance drifted")

    events = trace[3:]
    expected_types = [
        "run.started",
        "turn.started",
        "message.started",
        "message.delta",
        "message.delta",
        "message.completed",
        "run.completed",
    ]
    expected_data = [
        {},
        {},
        {"messageId": "route-message-1", "role": "assistant"},
        {
            "messageId": "route-message-1",
            "delta": {"type": "text", "text": "你好 "},
        },
        {
            "messageId": "route-message-1",
            "delta": {"type": "text", "text": "👋 from mock."},
        },
        {"messageId": "route-message-1", "finishReason": "stop"},
        {
            "status": "completed",
            "usage": {"inputTokens": 7, "outputTokens": 5, "totalTokens": 12},
        },
    ]
    for index, frame in enumerate(events):
        params = require_object(frame.get("params"), "golden event params")
        expected_turn = None if index in (0, 6) else "route-turn-1"
        expected_keys = {
            "sessionId",
            "runId",
            "seq",
            "time",
            "type",
            "data",
        }
        if expected_turn is not None:
            expected_keys.add("turnId")
        if (
            frame.get("jsonrpc") != "2.0"
            or frame.get("method") != "event"
            or params.get("sessionId") != "provider-route-session"
            or params.get("runId") != "route-run-1"
            or params.get("seq") != index + 1
            or params.get("time") != "$TIME"
            or params.get("type") != expected_types[index]
            or params.get("data") != expected_data[index]
            or params.get("turnId") != expected_turn
            or set(params) != expected_keys
        ):
            raise ProviderRouteRunnerError("provider route golden event drifted")

    golden_wire = json.dumps(
        [requests, trace], ensure_ascii=False, separators=(",", ":")
    )
    forbidden = (
        str(route_case["credentialEnv"]),
        CONFIG_ENV,
        PRODUCTION_ENDPOINTS[("groq", "openai-completions")],
        "endpointBase",
        "Bearer ",
    )
    if any(token in golden_wire for token in forbidden):
        raise ProviderRouteRunnerError(
            "provider route golden contains private runtime material"
        )

    materialized = copy.deepcopy(trace)
    implementation = require_object(
        require_object(materialized[0].get("result"), "golden initialize result").get(
            "implementation"
        ),
        "golden implementation",
    )
    implementation.update(
        {"name": "qxnm-forge-route-golden", "version": "0.1.0", "language": "python"}
    )
    for frame in materialized[3:]:
        require_object(frame.get("params"), "golden event params")["time"] = (
            "2026-07-15T00:00:00Z"
        )
    try:
        runner.TraceValidator(requests).validate(materialized)
        if schema_root is not None:
            schema_validation.validate_protocol_trace(
                requests, materialized, schema_root
            )
    except (runner.ConformanceError, schema_validation.SchemaValidationError) as exc:
        raise ProviderRouteRunnerError(
            "provider route golden violates protocol semantics"
        ) from exc


def sanitized_environment(
    manifest: JSONObject,
    temporary_root: Path,
    config_path: Path | None,
    *,
    credential_name: str | None,
    credential_value: str,
    conformance: bool,
    proxy_url: str = DEAD_PROXY,
) -> dict[str, str]:
    """功能：构造清除真实 Provider/cloud 状态的动态门禁环境。

    输入：manifest、临时根、可选配置路径、可选合成 credential、模式和代理。输出：子进程环境。
    不变量：真实 secret/endpoint/proxy 被移除；仅测试 canary 可进入最终 credential 环境变量。
    失败：不读取外部 credential store；非法配置由候选 daemon 拒绝。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    environment = dict(os.environ)
    manifest_names = {
        str(entry["name"])
        for provider in manifest["providers"]
        for entry in provider["environment"]
    }
    for name in list(environment):
        upper = name.upper()
        if name in manifest_names or any(
            marker in upper for marker in identity_runner.SENSITIVE_ENV_MARKERS
        ):
            environment.pop(name, None)
    for name in (
        *provider_runner.CREDENTIAL_ENV,
        *provider_runner.ENDPOINT_ENV,
        *provider_runner.PROXY_ENV,
        CONFIG_ENV,
        identity_runner.CONFIG_ENV,
        identity_runner.CANARY_ENV,
        "QXNM_FORGE_PROVIDER_CONFORMANCE",
        "QXNM_FORGE_CONFORMANCE",
    ):
        environment.pop(name, None)
    environment.update(
        {
            "HOME": str(temporary_root / "home"),
            "USERPROFILE": str(temporary_root / "home"),
            "XDG_CONFIG_HOME": str(temporary_root / "xdg-config"),
            "XDG_CACHE_HOME": str(temporary_root / "xdg-cache"),
            "AZURE_CONFIG_DIR": str(temporary_root / "azure"),
            "AWS_EC2_METADATA_DISABLED": "true",
            "HTTP_PROXY": proxy_url,
            "HTTPS_PROXY": proxy_url,
            "ALL_PROXY": proxy_url,
            "http_proxy": proxy_url,
            "https_proxy": proxy_url,
            "all_proxy": proxy_url,
            "NO_PROXY": "127.0.0.1,::1",
            "no_proxy": "127.0.0.1,::1",
            "QXNM_FORGE_SESSION_ROOT": str(temporary_root / "state"),
        }
    )
    if config_path is not None:
        environment[CONFIG_ENV] = str(config_path)
    if conformance:
        environment["QXNM_FORGE_CONFORMANCE"] = "1"
        environment["QXNM_FORGE_PROVIDER_CONFORMANCE"] = "1"
    if credential_name is not None:
        environment[credential_name] = credential_value
    return environment


def write_config(path: Path, value: JSONObject) -> None:
    """功能：以紧凑严格 JSON 写入 runner 独占的临时 route 配置。

    输入：临时普通文件路径与配置对象。输出：落盘并收紧为 owner-only 权限。
    不变量：配置不含 credential；仅写 TemporaryDirectory 内路径。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    path.write_text(
        json.dumps(value, ensure_ascii=False, separators=(",", ":")), encoding="utf-8"
    )
    try:
        path.chmod(0o600)
    except OSError as exc:
        raise ProviderRouteRunnerError("cannot secure route config file") from exc


def expand_command(
    command: Sequence[str], workspace: Path, state_root: Path
) -> list[str]:
    """功能：复用公共 runner 的无 shell workspace/stateRoot 字面占位符展开。

    输入：安全 argv 与 runner 临时路径。输出：有界展开后的 argv。
    失败：空命令、非绝对路径或展开越界时抛 route 门禁错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        return provider_runner._expand_probe_command(command, workspace, state_root)
    except provider_runner.ProviderProbeError as exc:
        raise ProviderRouteRunnerError(str(exc)) from exc


def prepare_directories(root: Path) -> tuple[Path, Path]:
    """功能：创建每案独占 workspace/state/home 目录。

    输入：绝对临时根。输出：workspace 与 stateRoot。失败：路径或文件系统异常时脱敏失败。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        workspace, state_root = provider_runner._prepare_probe_directories(root)
        (root / "home").mkdir()
        return workspace, state_root
    except (OSError, provider_runner.ProviderProbeError) as exc:
        raise ProviderRouteRunnerError(
            "cannot prepare route probe directories"
        ) from exc


class NetworkTripwireHandler(socketserver.BaseRequestHandler):
    """记录任何意外 TCP 连接并立即关闭，不解析或保存请求内容。"""

    def handle(self) -> None:
        """功能：把一次代理/endpoint TCP 连接登记为生产门禁失败证据。

        输入：socketserver 提供的本机连接。输出：登记后立即返回并关闭连接。
        不变量：不读取、解析、保存或记录连接字节及远端实例值。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        server = self.server
        if not isinstance(server, NetworkTripwireServer):
            raise ProviderRouteRunnerError("network tripwire server type is invalid")
        server.record_connection()


class NetworkTripwireServer(socketserver.ThreadingTCPServer):
    """只绑定 literal IPv4 loopback 的零内容网络连接计数器。"""

    allow_reuse_address = False
    daemon_threads = True

    def __init__(self) -> None:
        """功能：在内核分配端口创建尚未启动的 loopback TCP tripwire。

        输出：连接计数为零的 server。失败：无法绑定 loopback 时传播本地 I/O 错误。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self._connections = 0
        self._lock = threading.Lock()
        super().__init__(("127.0.0.1", 0), NetworkTripwireHandler)

    def record_connection(self) -> None:
        """功能：线程安全增加一次意外网络连接计数。

        输出：计数原子增加一。不变量：不保存 socket、地址或传输内容。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with self._lock:
            self._connections += 1

    def connection_count(self) -> int:
        """功能：线程安全读取当前意外网络连接总数。

        输出：从零开始的连接数。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with self._lock:
            return self._connections


class NetworkTripwire:
    """管理 production advertisement 案例的 loopback TCP tripwire。"""

    def __init__(self) -> None:
        """功能：创建尚未启动的 tripwire server 与空线程槽。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.server = NetworkTripwireServer()
        self.thread: threading.Thread | None = None

    @property
    def origin(self) -> str:
        """功能：返回可同时作为 dead proxy 与合成 endpoint 的 loopback origin。

        输出：`http://127.0.0.1:<ephemeral-port>`。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        host, port = self.server.server_address[:2]
        if host != "127.0.0.1":
            raise ProviderRouteRunnerError("network tripwire escaped literal loopback")
        return f"http://127.0.0.1:{port}"

    def start(self) -> None:
        """功能：在守护线程启动 tripwire 接受循环。

        失败：重复启动时 fail closed。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if self.thread is not None:
            raise ProviderRouteRunnerError("network tripwire was already started")
        self.thread = threading.Thread(
            target=self.server.serve_forever,
            kwargs={"poll_interval": 0.02},
            name="provider-route-network-tripwire",
            daemon=True,
        )
        self.thread.start()

    def stop(self) -> None:
        """功能：停止监听、关闭 socket 并有界回收 tripwire 线程。

        输出：server 不再接受连接。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.server.shutdown()
        self.server.server_close()
        if self.thread is not None:
            self.thread.join(timeout=2.0)
            self.thread = None

    def __enter__(self) -> NetworkTripwire:
        """功能：启动 tripwire 并返回当前实例。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.start()
        return self

    def __exit__(self, exc_type: Any, exc: Any, traceback: Any) -> None:
        """功能：无论 production gate 是否失败都关闭 tripwire。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        del exc_type, exc, traceback
        self.stop()


def projected_text_fixture(
    route_case: JSONObject,
) -> tuple[JSONObject, JSONObject, JSONObject]:
    """功能：把既有 text mock 夹具仅收窄到 canonical model/target 身份。

    输入：route fixture。输出：深拷贝 suite、选中 family 与 text_success case。
    不变量：stream renderer/parser/auth header 契约均继续由 provider_mock 提供。
    失败：family/case 缺失时传播脱敏 mock 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    fixture = copy.deepcopy(
        provider_mock.load_fixture(MOCK_ROOT / str(route_case["mockFixture"]))
    )
    family = provider_mock.family_by_id(fixture, str(route_case["mockFamily"]))
    family["providerId"] = route_case["providerId"]
    family["modelId"] = route_case["modelId"]
    family["credentialEnv"] = route_case["credentialEnv"]
    request = require_object(family.get("request"), "mock request")
    equals = require_object(request.get("equals"), "mock request.equals")
    if "/model" in equals:
        equals["/model"] = route_case["modelId"]
    family["requestTarget"] = copy.deepcopy(route_case["requestTarget"])
    case = provider_mock.case_by_name(family, str(route_case["mockCase"]))
    return fixture, family, case


class RouteMock:
    """管理一条 route 的既有 text 或 image loopback mock。"""

    def __init__(self, route_case: JSONObject, canary: str) -> None:
        """功能：创建尚未启动的 canonical route loopback mock。

        输入：route fixture 与运行时 canary。输出：初始化 server/state。
        不变量：只绑定 literal IPv4 loopback；观测不保存 header/body/credential 值。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.route_case = route_case
        self.is_image = route_case.get("apiFamily") == "openrouter-images"
        if self.is_image:
            fixture = provider_image_runner.strict_load(
                MOCK_ROOT / str(route_case["mockFixture"])
            )
            _, cases = provider_image_runner.validate_semantics(fixture)
            self.image_state = provider_image_runner.MockState(fixture, canary)
            self.server: Any = provider_image_runner.ImageServer(self.image_state)
            image_cases = [
                item for item in cases if item.get("name") == route_case["mockCase"]
            ]
            if len(image_cases) != 1:
                raise ProviderRouteRunnerError("image mock case is not unique")
            self.image_case: JSONObject | None = image_cases[0]
            self.text_state = None
            self.text_family = None
            self.text_case = None
        else:
            fixture, family, case = projected_text_fixture(route_case)
            self.text_state = provider_mock.MockState(fixture)
            self.server = provider_mock.ProviderHTTPServer(self.text_state)
            self.text_family = family
            self.text_case = case
            self.image_state = None
            self.image_case = None
        self.thread: threading.Thread | None = None

    @property
    def endpoint_base(self) -> str:
        """功能：返回供 strict config 使用的 literal-loopback base。

        输出：不含原生 request target 的 family/case base。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        port = int(self.server.server_address[1])
        if self.is_image:
            return f"http://127.0.0.1:{port}/case/{self.route_case['mockCase']}"
        return f"http://127.0.0.1:{port}/{self.route_case['mockFamily']}/{self.route_case['mockCase']}"

    def start(self) -> None:
        """功能：在守护线程启动 loopback mock。

        失败：重复启动时抛门禁错误。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if self.thread is not None:
            raise ProviderRouteRunnerError("route mock was already started")
        self.thread = threading.Thread(
            target=self.server.serve_forever,
            kwargs={"poll_interval": 0.05},
            daemon=True,
        )
        self.thread.start()

    def stop(self) -> None:
        """功能：关闭监听、唤醒等待并回收 mock 线程。

        输出：server socket 与线程均被回收。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if self.text_state is not None:
            self.text_state.stop_event.set()
        self.server.shutdown()
        self.server.server_close()
        if self.thread is not None:
            self.thread.join(timeout=2.0)
            self.thread = None

    def __enter__(self) -> RouteMock:
        """功能：启动 mock 并返回当前实例。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.start()
        return self

    def __exit__(self, exc_type: Any, exc: Any, traceback: Any) -> None:
        """功能：无论案例成败均停止 mock。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        del exc_type, exc, traceback
        self.stop()

    def observations(self) -> list[JSONObject]:
        """功能：返回当前 route 的脱敏请求观测。

        输出：text 为 provider_mock 观测；image 为 case/request/valid 三字段观测。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if self.text_state is not None:
            return self.text_state.observations()
        assert self.image_state is not None
        with self.image_state.lock:
            return copy.deepcopy(self.image_state.observed)


def await_frame(
    process: runner.DaemonProcess,
    request_id: str,
    frames: list[JSONObject],
    timeout: float,
) -> JSONObject:
    """功能：等待指定响应并拒绝响应前事件或未知 ID。

    输入：daemon、request ID、累计帧与 timeout。输出：成功或错误 response。
    失败：deadline、提前事件或错误关联时抛脱敏门禁错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            frame = process.next_frame(max(0.01, deadline - time.monotonic()))
        except queue.Empty as exc:
            raise ProviderRouteRunnerError("route daemon response timed out") from exc
        frames.append(frame)
        if frame.get("id") == request_id:
            return frame
        raise ProviderRouteRunnerError("route daemon emitted an unexpected frame")
    raise ProviderRouteRunnerError("route daemon response timed out")


def assert_success(response: JSONObject, label: str) -> JSONObject:
    """功能：提取 JSON-RPC 成功 result 对象。

    输入：response 与安全标签。输出：result。失败：error/缺失/非对象时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if "error" in response:
        raise ProviderRouteRunnerError(f"{label} returned an error")
    return require_object(response.get("result"), f"{label} result")


def assert_route_advertisement(
    initialize: JSONObject,
    models: JSONObject,
    route_case: JSONObject,
) -> None:
    """功能：证明 initialize/models/list 来自同一条单 route snapshot。

    输入：两个成功 response 与 route fixture。输出：精确 descriptor/并集一致时无返回值。
    不变量：Provider 级模型并集去重，models/list 保留 apiFamily 三元身份。
    失败：缺失、额外模型或 descriptor 漂移时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    initialize_result = assert_success(initialize, "initialize")
    capabilities = require_object(
        initialize_result.get("capabilities"), "initialize capabilities"
    )
    providers = require_list(capabilities.get("providers"), "initialize providers")
    selected = [
        item
        for item in providers
        if isinstance(item, dict) and item.get("id") == route_case["providerId"]
    ]
    if selected != [
        {"id": route_case["providerId"], "models": [route_case["modelId"]]}
    ]:
        raise ProviderRouteRunnerError("initialize route advertisement drifted")
    model_result = assert_success(models, "models/list")
    if model_result.get("models") != [route_case["expectedDescriptor"]]:
        raise ProviderRouteRunnerError("models/list route descriptor drifted")


def assert_error(response: JSONObject, expected: JSONObject) -> None:
    """功能：验证结构化错误 code/kind 且禁止成功降级。

    输入：response 与 fixture expected。输出：精确匹配时无返回值。
    失败：error 缺失、code/kind/retryable 漂移时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    error = require_object(response.get("error"), "negative response error")
    details = require_object(error.get("details"), "negative error details")
    if (
        error.get("code") != expected.get("errorCode")
        or details.get("kind") != expected.get("errorKind")
        or error.get("retryable") is not False
    ):
        raise ProviderRouteRunnerError("route rejection error classification drifted")


def scan_absent(root: Path, tokens: Sequence[bytes]) -> None:
    """功能：有界扫描 runner workspace/state，拒绝敏感 token 持久化。

    输入：runner 独占根和非空 token。输出：未发现时无返回值。
    不变量：不跟随 symlink；单文件 4 MiB、总计 16 MiB。失败：泄漏/读取/上限时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    total = 0
    for path in sorted(root.rglob("*")):
        if path.is_symlink() or not path.is_file():
            continue
        size = path.stat().st_size
        if size > MAX_SCAN_FILE_BYTES or total + size > MAX_SCAN_TOTAL_BYTES:
            raise ProviderRouteRunnerError("route artifact scan limit exceeded")
        data = path.read_bytes()
        total += len(data)
        if any(token and token in data for token in tokens):
            raise ProviderRouteRunnerError("route probe persisted forbidden material")


def assert_no_session(state_root: Path, session_id: str) -> None:
    """功能：证明拒绝发生在 Session journal 或目标 Session ID 持久化之前。

    输入：runner stateRoot 与负例 sessionId。输出：无 journal/ID 时无返回值。
    失败：发现任何 journal 或目标 ID bytes 时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if any(path.is_file() for path in state_root.rglob("journal.jsonl")):
        raise ProviderRouteRunnerError("rejected route created a Session journal")
    scan_absent(state_root, [session_id.encode("utf-8")])


def assert_tokens_absent(
    frames: Sequence[JSONObject],
    stderr: str,
    root: Path,
    tokens: Sequence[bytes],
) -> None:
    """功能：扫描协议、stderr、workspace 与 state 中不存在任一私有 token。

    输入：协议帧、stderr、runner 临时根和 token bytes。输出：全部缺席时无返回值。
    不变量：诊断不回显 token；文件扫描保持单文件/总量边界且不跟随 symlink。
    失败：任一输出或持久化命中时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    bounded_tokens = [token for token in tokens if token]
    wire = json.dumps(list(frames), ensure_ascii=False, separators=(",", ":")).encode()
    diagnostic = stderr.encode("utf-8", errors="replace")
    if any(token in wire or token in diagnostic for token in bounded_tokens):
        raise ProviderRouteRunnerError("route probe leaked private runtime material")
    scan_absent(root / "workspace", bounded_tokens)
    scan_absent(root / "state", bounded_tokens)


def assert_no_leak(
    frames: Sequence[JSONObject],
    stderr: str,
    root: Path,
    canary: str,
    config_path: Path,
    endpoint_base: str,
) -> None:
    """功能：扫描协议、stderr、workspace/state 与 mock 外的敏感 route 材料。

    输入：帧、stderr、临时根、canary、配置路径和原始 endpoint。输出：无泄漏时无返回值。
    不变量：不把 token 内容写入诊断。失败：任一输出/持久化命中时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    assert_tokens_absent(
        frames,
        stderr,
        root,
        [canary.encode(), str(config_path).encode(), endpoint_base.encode()],
    )


def run_positive_case(
    command: Sequence[str],
    route_case: JSONObject,
    manifest: JSONObject,
    schema_root: Path | None,
    timeout: float,
) -> None:
    """功能：运行一条 initialize→models/list→run/start→原生 HTTP→终态正例。

    输入：安全 argv、route、manifest、可选 Schema 根和 timeout。输出：全部事实通过时无返回值。
    不变量：fresh daemon/workspace/state/config/mock/canary；不接触真实 Provider。
    失败：广告、协议、原生请求、终态、Session 或隐私检查任一失败时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    canary = "provider-route-canary-" + secrets.token_urlsafe(32)
    with (
        RouteMock(route_case, canary) as mock,
        tempfile.TemporaryDirectory(prefix="provider-route-") as temporary,
    ):
        root = Path(temporary)
        workspace, state_root = prepare_directories(root)
        config_path = root / "route.json"
        write_config(config_path, route_config(route_case, mock.endpoint_base))
        environment = sanitized_environment(
            manifest,
            root,
            config_path,
            credential_name=str(route_case["credentialEnv"]),
            credential_value=canary,
            conformance=True,
        )
        process = runner.DaemonProcess(
            expand_command(command, workspace, state_root),
            timeout=timeout,
            max_frame_bytes=1_048_576,
            extra_env=environment,
            removed_env=tuple(
                set(
                    (
                        *provider_runner.CREDENTIAL_ENV,
                        *provider_runner.ENDPOINT_ENV,
                        *provider_runner.PROXY_ENV,
                        CONFIG_ENV,
                    )
                )
            ),
        )
        frames: list[JSONObject] = []
        requests = [
            initialize_request(),
            models_request(str(route_case["providerId"])),
            run_start_request(route_case),
        ]
        try:
            process.send_request(requests[0])
            initialized = await_frame(process, "route-initialize", frames, timeout)
            process.send_request(requests[1])
            models = await_frame(process, "route-models", frames, timeout)
            assert_route_advertisement(initialized, models, route_case)
            process.send_request(requests[2])
            started = await_frame(process, "route-run-start", frames, timeout)
            run_id = assert_success(started, "run/start").get("runId")
            if not isinstance(run_id, str) or not run_id:
                raise ProviderRouteRunnerError("run/start omitted runId")
            provider_runner._await_terminal(process, run_id, frames, timeout)
        finally:
            process.close()
        if schema_root is not None:
            try:
                schema_validation.validate_protocol_trace(requests, frames, schema_root)
            except schema_validation.SchemaValidationError as exc:
                raise ProviderRouteRunnerError(
                    "route protocol trace violates Schema"
                ) from exc
        observations = mock.observations()
        if len(observations) != 1:
            raise ProviderRouteRunnerError("route mock request count drifted")
        if route_case.get("apiFamily") == "openrouter-images":
            if observations[0].get("valid") is not True:
                raise ProviderRouteRunnerError("image-native request contract failed")
            assert mock.image_state is not None and mock.image_case is not None
            provider_image_runner.verify_case(
                mock.image_case,
                frames,
                state_root,
                canary,
                len(observations),
            )
        else:
            if (
                observations[0].get("requestValid") is not True
                or observations[0].get("requestTargetValid") is not True
            ):
                raise ProviderRouteRunnerError(
                    "family-native text request contract failed"
                )
            assert mock.text_family is not None and mock.text_case is not None
            provider_runner.verify_trace(
                requests,
                frames,
                mock.text_family,
                mock.text_case,
                schema_root=schema_root,
            )
        assert_no_leak(
            frames,
            process.stderr_text(limit=131_072),
            root,
            canary,
            config_path,
            mock.endpoint_base,
        )


def negative_expected(fixture: JSONObject, kind: str) -> JSONObject:
    """功能：按 kind 取得唯一负例 expected 对象。

    输入：route fixture 与 kind。输出：expected。失败：缺失/重复时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    matches = [
        require_object(item, "negative case")
        for item in require_list(fixture.get("negativeCases"), "negativeCases")
        if isinstance(item, dict) and item.get("kind") == kind
    ]
    if len(matches) != 1:
        raise ProviderRouteRunnerError("negative route case is not unique")
    return require_object(matches[0].get("expected"), "negative expected")


def run_selection_negative(
    command: Sequence[str],
    route_case: JSONObject,
    manifest: JSONObject,
    fixture: JSONObject,
    kind: str,
    timeout: float,
) -> None:
    """功能：执行 missing credential、wrong family 或 unknown model 的前置拒绝。

    输入：argv、route、manifest、fixture、kind 与 timeout。输出：错误及零 Session 副作用时无返回值。
    不变量：不启动 mock、不发网络；每案 fresh state/config/canary。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    canary = "provider-route-negative-" + secrets.token_urlsafe(32)
    session_id = f"provider-route-negative-{kind}"
    endpoint = "http://127.0.0.1:9/route-negative"
    with tempfile.TemporaryDirectory(prefix="provider-route-negative-") as temporary:
        root = Path(temporary)
        workspace, state_root = prepare_directories(root)
        config_path = root / "route.json"
        write_config(config_path, route_config(route_case, endpoint))
        credential_name = (
            None if kind == "missing_credential" else str(route_case["credentialEnv"])
        )
        environment = sanitized_environment(
            manifest,
            root,
            config_path,
            credential_name=credential_name,
            credential_value=canary,
            conformance=True,
        )
        process = runner.DaemonProcess(
            expand_command(command, workspace, state_root),
            timeout=timeout,
            max_frame_bytes=1_048_576,
            extra_env=environment,
            removed_env=tuple(
                set(
                    (
                        *provider_runner.CREDENTIAL_ENV,
                        *provider_runner.ENDPOINT_ENV,
                        *provider_runner.PROXY_ENV,
                        CONFIG_ENV,
                    )
                )
            ),
        )
        frames: list[JSONObject] = []
        try:
            process.send_request(initialize_request())
            initialized = await_frame(process, "route-initialize", frames, timeout)
            assert_success(initialized, "initialize")
            process.send_request(models_request(str(route_case["providerId"])))
            listed = await_frame(process, "route-models", frames, timeout)
            models = assert_success(listed, "models/list").get("models")
            if kind == "missing_credential" and models != []:
                raise ProviderRouteRunnerError(
                    "missing credential route remained advertised"
                )
            wrong_family = (
                "openai-responses"
                if route_case["apiFamily"] != "openai-responses"
                else "anthropic-messages"
            )
            request = run_start_request(
                route_case,
                session_id=session_id,
                family=wrong_family if kind == "wrong_family" else None,
                model_id="manifest-unknown-model" if kind == "unknown_model" else None,
            )
            process.send_request(request)
            rejected = await_frame(process, "route-run-start", frames, timeout)
            assert_error(rejected, negative_expected(fixture, kind))
        finally:
            process.close()
        assert_no_session(state_root, session_id)
        assert_no_leak(
            frames,
            process.stderr_text(limit=131_072),
            root,
            canary,
            config_path,
            endpoint,
        )


def malformed_config(kind: str, base: JSONObject) -> bytes:
    """功能：生成 endpoint/config/unlisted 三类启动负例的严格或畸形 JSON bytes。

    输入：固定 kind 与基准配置。输出：不含 credential 的有界 bytes。
    失败：未知 kind 时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if kind == "endpoint_non_loopback":
        value = {**base, "endpointBase": "http://localhost:12345/provider"}
        return json.dumps(value, separators=(",", ":")).encode()
    if kind == "config_unknown_field":
        value = {**base, "unknown": True}
        return json.dumps(value, separators=(",", ":")).encode()
    if kind == "config_duplicate_key":
        return (
            json.dumps(base, separators=(",", ":"))[:-1] + ',"schemaVersion":"0.1"}'
        ).encode()
    if kind == "unlisted_route_config":
        value = {
            **base,
            "providerId": "anthropic",
            "apiFamily": "anthropic-messages",
            "modelId": "claude-fable-5",
        }
        return json.dumps(value, separators=(",", ":")).encode()
    raise ProviderRouteRunnerError("unknown malformed route config kind")


def run_startup_negative(
    command: Sequence[str],
    route_case: JSONObject,
    manifest: JSONObject,
    kind: str,
    timeout: float,
) -> None:
    """功能：执行 unlisted/production/endpoint/strict-config 的启动拒绝案例。

    输入：argv、route、manifest、kind 与 timeout。输出：非零且零 stdout/Session 时无返回值。
    不变量：production 案例移除字面 `--conformance` 并清除两项授权环境。
    失败：候选容忍 presence、泄漏或产生 Session 时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    canary = "provider-route-startup-" + secrets.token_urlsafe(32)
    endpoint = "http://127.0.0.1:9/route-negative"
    with tempfile.TemporaryDirectory(prefix="provider-route-startup-") as temporary:
        root = Path(temporary)
        workspace, state_root = prepare_directories(root)
        config_path = root / "route.json"
        base = route_config(route_case, endpoint)
        raw = (
            json.dumps(base, separators=(",", ":")).encode()
            if kind == "production_presence"
            else malformed_config(kind, base)
        )
        config_path.write_bytes(raw)
        config_path.chmod(0o600)
        production = kind == "production_presence"
        environment = sanitized_environment(
            manifest,
            root,
            config_path,
            credential_name=str(route_case["credentialEnv"]),
            credential_value=canary,
            conformance=not production,
        )
        expanded = expand_command(command, workspace, state_root)
        if production:
            expanded = [item for item in expanded if item != "--conformance"]
        completed, frames = identity_runner.run_process(
            expanded,
            [identity_runner.encode_request(initialize_request())],
            environment,
            timeout,
        )
        if completed.returncode == 0 or completed.stdout or frames:
            raise ProviderRouteRunnerError(
                "daemon did not reject invalid route presence at startup"
            )
        assert_no_session(state_root, "provider-route-startup-negative")
        assert_no_leak(
            [],
            completed.stderr.decode("utf-8", errors="replace"),
            root,
            canary,
            config_path,
            endpoint,
        )


def run_wire_duplicate_negative(
    command: Sequence[str],
    route_case: JSONObject,
    manifest: JSONObject,
    fixture: JSONObject,
    timeout: float,
) -> None:
    """功能：验证合法 initialize 前缀后 duplicate-key wire 得到 null-ID parse error。

    输入：argv、route、manifest、fixture 与 timeout。输出：两帧、-32700、零 Session 时无返回值。
    不变量：同一 stdin chunk 发送合法前缀和畸形帧；不访问 Provider 网络。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    canary = "provider-route-wire-" + secrets.token_urlsafe(32)
    endpoint = "http://127.0.0.1:9/route-wire"
    session_id = "provider-route-wire-duplicate"
    with tempfile.TemporaryDirectory(prefix="provider-route-wire-") as temporary:
        root = Path(temporary)
        workspace, state_root = prepare_directories(root)
        config_path = root / "route.json"
        write_config(config_path, route_config(route_case, endpoint))
        environment = sanitized_environment(
            manifest,
            root,
            config_path,
            credential_name=str(route_case["credentialEnv"]),
            credential_value=canary,
            conformance=True,
        )
        duplicate = (
            b'{"jsonrpc":"2.0","id":"route-wire-duplicate","method":"run/start",'
            b'"params":{"sessionId":"provider-route-wire-duplicate",'
            b'"sessionId":"provider-route-wire-duplicate","input":{"role":"user",'
            b'"content":[{"type":"text","text":"x"}]},"provider":{"id":"groq",'
            b'"modelId":"llama-3.1-8b-instant","apiFamily":"openai-completions"}}}'
        )
        completed, frames = identity_runner.run_process(
            expand_command(command, workspace, state_root),
            [identity_runner.encode_request(initialize_request()), duplicate],
            environment,
            timeout,
        )
        expected = negative_expected(fixture, "wire_duplicate_key")
        if completed.returncode != 0 or len(frames) != 2 or "error" in frames[0]:
            raise ProviderRouteRunnerError(
                "duplicate-key wire did not preserve initialize prefix"
            )
        if frames[1].get("id") is not None:
            raise ProviderRouteRunnerError("duplicate-key parse error must use null ID")
        error = require_object(frames[1].get("error"), "wire duplicate error")
        if error.get("code") != expected.get("errorCode"):
            raise ProviderRouteRunnerError("duplicate-key parse error code drifted")
        assert_no_session(state_root, session_id)
        assert_no_leak(
            frames,
            completed.stderr.decode("utf-8", errors="replace"),
            root,
            canary,
            config_path,
            endpoint,
        )


def expected_production_models(
    fixture_index: dict[RouteKey, JSONObject],
    catalog_index: dict[RouteKey, list[JSONObject]],
) -> list[JSONObject]:
    """功能：从六条 spine catalog 与能力下界生成生产模式 133 个 descriptor。

    输入：已通过静态门禁的 route fixture/catalog 索引。输出：按三元身份排序的 descriptors。
    不变量：只缩小 catalog 能力；不包含 faux、endpoint、credential、adapter 或 quirk。
    失败：投影为空或总数不是 133 时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    models: list[JSONObject] = []
    for key in SPINE_KEYS:
        features = set(require_list(fixture_index[key]["providerFeatures"], "features"))
        for model in catalog_index[key]:
            descriptor = identity_runner.normalized_model(model, features)
            capabilities = require_object(
                descriptor.get("capabilities"), "production model capabilities"
            )
            if not capabilities.get("input") or not capabilities.get("output"):
                raise ProviderRouteRunnerError(
                    "production spine projected an empty model descriptor"
                )
            models.append(descriptor)
    models.sort(key=identity_runner.model_key)
    if len(models) != 133:
        raise ProviderRouteRunnerError("production spine descriptor census drifted")
    return models


def production_models_requests(provider_ids: Sequence[str]) -> list[JSONObject]:
    """功能：构造生产 initialize 与六个逐 Provider models/list 请求。

    输入：六个 canonical Provider ID。输出：七个 ID 唯一的稳定请求。
    失败：Provider 集合不是六条 spine 精确集合时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    expected_ids = sorted({key[0] for key in SPINE_KEYS})
    if sorted(provider_ids) != expected_ids:
        raise ProviderRouteRunnerError("production Provider request set drifted")
    requests = [initialize_request()]
    for index, provider_id in enumerate(expected_ids, start=1):
        request = models_request(provider_id)
        request["id"] = f"production-models-{index}"
        requests.append(request)
    return requests


def production_command(
    command: Sequence[str], workspace: Path, state_root: Path
) -> list[str]:
    """功能：展开 daemon argv 并移除标准 CLI 的字面 conformance 开关。

    输入：安全 argv template 与 fresh workspace/state。输出：普通生产模式 argv。
    不变量：只删除精确 `--conformance` 项，不执行 shell、前缀或子串重写。
    失败：展开后的命令为空时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    expanded = [
        item
        for item in expand_command(command, workspace, state_root)
        if item != "--conformance"
    ]
    if not expanded:
        raise ProviderRouteRunnerError("production daemon command became empty")
    return expanded


def assert_production_advertisement(
    requests: Sequence[JSONObject],
    frames: Sequence[JSONObject],
    models: Sequence[JSONObject],
) -> None:
    """功能：精确验证七 Provider、134 模型并集与六次 route-qualified 列表。

    输入：七个请求、实际响应帧和 133 个 catalog descriptors。输出：快照完全一致时无返回值。
    不变量：initialize 含 faux；models/list 只返回对应 canonical Provider descriptors。
    失败：额外/缺失/乱序/私有字段或模型并集漂移时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if len(requests) != 7 or len(frames) != 7:
        raise ProviderRouteRunnerError(
            "production advertisement did not return seven responses"
        )
    responses = identity_runner.response_map(frames)
    initialized = identity_runner.success_result(responses, "route-initialize")
    capabilities = require_object(
        initialized.get("capabilities"), "production initialize capabilities"
    )
    providers = require_list(
        capabilities.get("providers"), "production initialize providers"
    )
    expected_providers = identity_runner.expected_provider_capabilities(models)
    if providers != expected_providers or len(providers) != 7:
        raise ProviderRouteRunnerError("production Provider advertisement drifted")
    if (
        sum(
            len(require_list(item.get("models"), "Provider models"))
            for item in providers
        )
        != 134
    ):
        raise ProviderRouteRunnerError("production Provider/model union census drifted")
    provider_ids = sorted({str(model["providerId"]) for model in models})
    for index, provider_id in enumerate(provider_ids, start=1):
        result = identity_runner.success_result(responses, f"production-models-{index}")
        expected = [model for model in models if model["providerId"] == provider_id]
        if result != {"models": expected}:
            raise ProviderRouteRunnerError(
                "production models/list differs from executable snapshot"
            )
    identity_runner.assert_no_private_fields(frames)


def run_production_spine_gate(
    command: Sequence[str],
    manifest: JSONObject,
    fixture_index: dict[RouteKey, JSONObject],
    catalog_index: dict[RouteKey, list[JSONObject]],
    schema_root: Path | None,
    timeout: float,
) -> None:
    """功能：验证无 presence/conformance 时六个 credential 激活完整生产 spine 广告。

    输入：daemon argv、共享索引、Schema 根与 timeout。输出：7 Provider/134 模型精确匹配。
    不变量：fresh daemon/workspace/state；只设置六个合成 credential；零 HTTP/Session/泄漏。
    失败：启动、广告、Schema、网络、持久化或 canary 隐私任一漂移时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    models = expected_production_models(fixture_index, catalog_index)
    requests = production_models_requests(sorted({key[0] for key in SPINE_KEYS}))
    credential_canaries = {
        str(fixture_index[key]["credentialEnv"]): "production-route-canary-"
        + secrets.token_urlsafe(24)
        for key in SPINE_KEYS
    }
    with (
        NetworkTripwire() as tripwire,
        tempfile.TemporaryDirectory(prefix="provider-route-production-") as temporary,
    ):
        root = Path(temporary)
        workspace, state_root = prepare_directories(root)
        environment = sanitized_environment(
            manifest,
            root,
            None,
            credential_name=None,
            credential_value="unused",
            conformance=False,
            proxy_url=tripwire.origin,
        )
        environment.update(credential_canaries)
        completed, frames = identity_runner.run_process(
            production_command(command, workspace, state_root),
            [identity_runner.encode_request(request) for request in requests],
            environment,
            timeout,
        )
        if completed.returncode != 0:
            raise ProviderRouteRunnerError(
                "daemon rejected valid production spine credentials"
            )
        if schema_root is not None:
            try:
                schema_validation.validate_protocol_trace(requests, frames, schema_root)
            except schema_validation.SchemaValidationError as exc:
                raise ProviderRouteRunnerError(
                    "production advertisement violates protocol Schema"
                ) from exc
        assert_production_advertisement(requests, frames, models)
        time.sleep(0.05)
        if tripwire.server.connection_count() != 0:
            raise ProviderRouteRunnerError(
                "production advertisement attempted an HTTP connection"
            )
        assert_no_session(state_root, "provider-route-production-no-session")
        assert_tokens_absent(
            frames,
            completed.stderr.decode("utf-8", errors="replace"),
            root,
            [
                *(value.encode() for value in credential_canaries.values()),
                tripwire.origin.encode(),
            ],
        )


def run_outside_allowlist_gate(
    command: Sequence[str],
    manifest: JSONObject,
    schema_root: Path | None,
    timeout: float,
) -> None:
    """功能：验证 Azure/Anthropic credential 与 endpoint 不能绕过六 route allowlist。

    输入：daemon argv、manifest、Schema 根与 timeout。输出：只广告 faux 且无 HTTP/泄漏。
    不变量：普通生产、fresh state；outside 配置包含 loopback tripwire 但不得被读取执行。
    失败：新增广告、网络连接、Session、副作用或 canary 泄漏时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    canaries = [
        "outside-azure-canary-" + secrets.token_urlsafe(24),
        "outside-anthropic-canary-" + secrets.token_urlsafe(24),
        "outside-anthropic-oauth-canary-" + secrets.token_urlsafe(24),
    ]
    requests = [
        initialize_request(),
        {
            "jsonrpc": "2.0",
            "id": "outside-models-all",
            "method": "models/list",
            "params": {},
        },
    ]
    with (
        NetworkTripwire() as tripwire,
        tempfile.TemporaryDirectory(prefix="provider-route-outside-") as temporary,
    ):
        root = Path(temporary)
        workspace, state_root = prepare_directories(root)
        environment = sanitized_environment(
            manifest,
            root,
            None,
            credential_name=None,
            credential_value="unused",
            conformance=False,
            proxy_url=tripwire.origin,
        )
        environment.update(
            {
                "AZURE_OPENAI_API_KEY": canaries[0],
                "AZURE_OPENAI_BASE_URL": tripwire.origin + "/azure",
                "AZURE_OPENAI_API_VERSION": "v1",
                "QXNM_FORGE_AZURE_OPENAI_RESPONSES_ENDPOINT": tripwire.origin
                + "/azure-legacy",
                "ANTHROPIC_API_KEY": canaries[1],
                "ANTHROPIC_OAUTH_TOKEN": canaries[2],
                "QXNM_FORGE_ANTHROPIC_ENDPOINT": tripwire.origin + "/anthropic",
            }
        )
        completed, frames = identity_runner.run_process(
            production_command(command, workspace, state_root),
            [identity_runner.encode_request(request) for request in requests],
            environment,
            timeout,
        )
        if completed.returncode != 0 or len(frames) != 2:
            raise ProviderRouteRunnerError(
                "daemon rejected the outside-allowlist production gate"
            )
        responses = identity_runner.response_map(frames)
        initialized = identity_runner.success_result(responses, "route-initialize")
        capabilities = require_object(
            initialized.get("capabilities"), "outside initialize capabilities"
        )
        if capabilities.get("providers") != [{"id": "faux", "models": ["faux-v1"]}]:
            raise ProviderRouteRunnerError(
                "outside-allowlist configuration gained a Provider advertisement"
            )
        listed = identity_runner.success_result(responses, "outside-models-all")
        if listed != {"models": [identity_runner.faux_model()]}:
            raise ProviderRouteRunnerError(
                "outside-allowlist configuration gained a model descriptor"
            )
        if schema_root is not None:
            try:
                schema_validation.validate_protocol_trace(requests, frames, schema_root)
            except schema_validation.SchemaValidationError as exc:
                raise ProviderRouteRunnerError(
                    "outside-allowlist trace violates protocol Schema"
                ) from exc
        time.sleep(0.05)
        if tripwire.server.connection_count() != 0:
            raise ProviderRouteRunnerError(
                "outside-allowlist advertisement attempted an HTTP connection"
            )
        assert_no_session(state_root, "provider-route-outside-no-session")
        assert_tokens_absent(
            frames,
            completed.stderr.decode("utf-8", errors="replace"),
            root,
            [*(value.encode() for value in canaries), tripwire.origin.encode()],
        )


def assert_startup_presence_rejected(
    command: Sequence[str],
    environment: dict[str, str],
    workspace: Path,
    state_root: Path,
    root: Path,
    tokens: Sequence[bytes],
    timeout: float,
) -> None:
    """功能：共同验证非法 presence 在读取 initialize 前固定、脱敏、零 Session 拒绝。

    输入：argv、环境、fresh 路径、私有 token 与 timeout。输出：非零且零 stdout 时无返回值。
    不变量：只发送一个 initialize；拒绝不解析路径实例、不创建 Session、不访问网络。
    失败：成功启动、协议输出、泄漏或持久化时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    completed, frames = identity_runner.run_process(
        expand_command(command, workspace, state_root),
        [identity_runner.encode_request(initialize_request())],
        environment,
        timeout,
    )
    if completed.returncode == 0 or completed.stdout or frames:
        raise ProviderRouteRunnerError(
            "daemon did not reject invalid Provider presence before startup"
        )
    assert_no_session(state_root, "provider-route-presence-no-session")
    assert_tokens_absent(
        [],
        completed.stderr.decode("utf-8", errors="replace"),
        root,
        tokens,
    )


def run_dual_presence_gate(
    command: Sequence[str], manifest: JSONObject, timeout: float
) -> None:
    """功能：验证 route 与 identity 双 presence 在读取任一路径前固定拒绝。

    输入：daemon argv、manifest 与 timeout。输出：不存在的 canary 路径未读取/泄漏时无返回值。
    不变量：一般/provider conformance 均显式开启，使失败唯一归因于 presence 互斥。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    with tempfile.TemporaryDirectory(prefix="provider-route-dual-") as temporary:
        root = Path(temporary)
        workspace, state_root = prepare_directories(root)
        route_path = root / ("missing-route-" + secrets.token_hex(24))
        identity_path = root / ("missing-identity-" + secrets.token_hex(24))
        environment = sanitized_environment(
            manifest,
            root,
            None,
            credential_name=None,
            credential_value="unused",
            conformance=True,
        )
        environment[CONFIG_ENV] = str(route_path)
        environment[identity_runner.CONFIG_ENV] = str(identity_path)
        assert_startup_presence_rejected(
            command,
            environment,
            workspace,
            state_root,
            root,
            [str(route_path).encode(), str(identity_path).encode()],
            timeout,
        )


def run_empty_route_presence_gate(
    command: Sequence[str], manifest: JSONObject, timeout: float
) -> None:
    """功能：验证 route presence 环境项存在但值为空时仍在启动前拒绝。

    输入：daemon argv、manifest 与 timeout。输出：空 presence 未降级为 absent 时无返回值。
    不变量：显式 conformance 下只设置空 route presence；identity presence 保持 absent。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    with tempfile.TemporaryDirectory(prefix="provider-route-empty-") as temporary:
        root = Path(temporary)
        workspace, state_root = prepare_directories(root)
        environment = sanitized_environment(
            manifest,
            root,
            None,
            credential_name=None,
            credential_value="unused",
            conformance=True,
        )
        environment[CONFIG_ENV] = ""
        assert_startup_presence_rejected(
            command,
            environment,
            workspace,
            state_root,
            root,
            [],
            timeout,
        )


def run_production_gates(
    command: Sequence[str],
    manifest: JSONObject,
    fixture_index: dict[RouteKey, JSONObject],
    catalog_index: dict[RouteKey, list[JSONObject]],
    schema_root: Path | None,
    timeout: float,
) -> int:
    """功能：依次运行完整生产广告、allowlist 隔离与两项 presence precedence 门禁。

    输入：daemon argv、共享索引、Schema 根和 timeout。输出：固定通过数 4。
    不变量：四案均 fresh、无真实 Provider、无 Session、脱敏且网络 tripwire 为零。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    run_production_spine_gate(
        command,
        manifest,
        fixture_index,
        catalog_index,
        schema_root,
        timeout,
    )
    run_outside_allowlist_gate(command, manifest, schema_root, timeout)
    run_dual_presence_gate(command, manifest, timeout)
    run_empty_route_presence_gate(command, manifest, timeout)
    return 4


def run_dynamic_suite(
    command: Sequence[str],
    fixture: JSONObject,
    fixture_index: dict[RouteKey, JSONObject],
    manifest: JSONObject,
    catalog_index: dict[RouteKey, list[JSONObject]],
    schema_root: Path | None,
    timeout: float,
) -> tuple[int, int, int]:
    """功能：运行六条 happy path、九个 fail-closed 负例与四个生产门禁。

    输入：daemon argv、共享文档/索引、Schema 根和 timeout。输出：正例、负例和生产门禁通过数。
    不变量：每案独立 daemon/config/workspace/state/canary；默认只连接 runner loopback mock。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    for key in SPINE_KEYS:
        run_positive_case(command, fixture_index[key], manifest, schema_root, timeout)
    base_route = fixture_index[("groq", "openai-completions")]
    for kind in ("missing_credential", "wrong_family", "unknown_model"):
        run_selection_negative(command, base_route, manifest, fixture, kind, timeout)
    for kind in (
        "unlisted_route_config",
        "production_presence",
        "endpoint_non_loopback",
        "config_unknown_field",
        "config_duplicate_key",
    ):
        run_startup_negative(command, base_route, manifest, kind, timeout)
    run_wire_duplicate_negative(command, base_route, manifest, fixture, timeout)
    production_gates = run_production_gates(
        command,
        manifest,
        fixture_index,
        catalog_index,
        schema_root,
        timeout,
    )
    return 6, 9, production_gates


def build_parser() -> argparse.ArgumentParser:
    """功能：构建 ADR 0018 静态与可选 daemon 动态门禁 CLI。

    输出：只接受安全 JSON argv 与有界 timeout 的 ArgumentParser。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, default=DEFAULT_FIXTURE)
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--catalog", type=Path, default=DEFAULT_CATALOG)
    parser.add_argument("--schema-root", type=Path)
    parser.add_argument(
        "--daemon-command-json", type=provider_runner.parse_command_json
    )
    parser.add_argument("--timeout", type=float, default=10.0)
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    """功能：运行 route spine 静态 gate，并可选择运行六正九负黑盒套件。

    输入：CLI argv。输出：成功 0、门禁失败 1、参数范围失败 2。
    不变量：不修改 capabilities、语言实现、manifest/catalog 或真实 Provider 状态。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = build_parser().parse_args(argv)
    if args.timeout <= 0 or args.timeout > 60:
        print("provider route gate: timeout must be in (0, 60]", file=os.sys.stderr)
        return 2
    try:
        fixture = load_json(args.fixture)
        manifest = load_json(args.manifest)
        catalog = load_json(args.catalog)
        fixture_index, _, catalog_index = validate_static_suite(
            fixture, manifest, catalog, schema_root=args.schema_root
        )
        if args.daemon_command_json is None:
            print("PASS: provider route static 6 routes / 133 models / 9 negatives")
            return 0
        positives, negatives, production_gates = run_dynamic_suite(
            args.daemon_command_json,
            fixture,
            fixture_index,
            manifest,
            catalog_index,
            args.schema_root,
            args.timeout,
        )
        print(
            f"PASS: provider route daemon {positives} positives / {negatives} negatives / "
            f"{production_gates} production gates"
        )
        return 0
    except (
        ProviderRouteRunnerError,
        identity_runner.ProviderIdentityRunnerError,
        provider_mock.ProviderMockError,
        provider_image_runner.ImageRunnerError,
        provider_runner.ProviderProbeError,
        runner.ConformanceError,
    ) as exc:
        print(f"provider route gate failed: {exc}", file=os.sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
