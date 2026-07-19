#!/usr/bin/env python3
"""Provider 身份、路由广告和模型过滤的完全离线黑盒门禁。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
import json
import os
import queue
import secrets
import signal
import subprocess
import sys
import tempfile
import threading
import time
from collections import Counter, defaultdict
from collections.abc import Sequence
from pathlib import Path
from typing import Any

JSONObject = dict[str, Any]
RouteKey = tuple[str, str]
ModelKey = tuple[str, str, str]
AuthKey = tuple[str, str]

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_FIXTURE = ROOT / "CONFORMANCE/fixtures/provider-identity/advertisement-cases.json"
DEFAULT_MANIFEST = ROOT / "SPEC/providers.v1.json"
DEFAULT_CATALOG = ROOT / "SPEC/models.v1.json"
CONFIG_ENV = "AGENT_PROVIDER_IDENTITY_CONFORMANCE_CONFIG"
CANARY_ENV = "AGENT_PROVIDER_IDENTITY_CREDENTIAL_CANARY"
DEAD_PROXY_URL = "http://127.0.0.1:9"
LOOPBACK_NO_PROXY = "127.0.0.1,::1"
MAX_ARGV_ITEMS = 128
MAX_ARG_CHARS = 4096
MAX_COMMAND_JSON_CHARS = 8 * 1024 * 1024
MAX_PROCESS_OUTPUT_BYTES = 16 * 1024 * 1024
MAX_PROCESS_STDERR_BYTES = 1024 * 1024
PROCESS_READ_CHUNK_BYTES = 64 * 1024
PROCESS_TERMINATION_GRACE_SECONDS = 0.2
PROCESS_REAP_TIMEOUT_SECONDS = 2.0
ALL_CAPABILITY_FEATURES = [
    "authentication",
    "text",
    "streaming",
    "tools",
    "reasoning",
    "image_input",
    "image_output",
]
PRIVATE_FIELD_PARTS = (
    "adapterid",
    "authprofile",
    "credential",
    "endpoint",
    "environment",
    "secret",
)
SENSITIVE_ENV_MARKERS = (
    "API_KEY",
    "AUTH_SOCK",
    "AUTHORIZATION",
    "BASE_URL",
    "COOKIE",
    "CREDENTIAL",
    "ENDPOINT",
    "PASSWORD",
    "PASSWD",
    "PRIVATE_KEY",
    "SECRET",
    "TOKEN",
)


class ProviderIdentityRunnerError(Exception):
    """Provider 身份门禁的稳定、脱敏失败。"""


def reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> JSONObject:
    """功能：构造严格 JSON object，并在任何层级拒绝重复键。

    输入：标准库 JSON decoder 提供的有序键值对。
    输出：无重复键的普通字典。
    不变量：诊断不回显攻击者控制的键名或对应值。
    失败：同一 object 内重复键时抛出 ProviderIdentityRunnerError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    result: JSONObject = {}
    for key, value in pairs:
        if key in result:
            raise ProviderIdentityRunnerError("duplicate JSON key rejected")
        result[key] = value
    return result


def parse_json_bytes(raw: bytes, label: str) -> JSONObject:
    """功能：按严格 UTF-8、单 JSON value 和重复键拒绝规则解析对象。

    输入：有界 JSON bytes 与不含敏感值的诊断标签。
    输出：顶层 JSON object。
    不变量：不在异常中回显原始 bytes。
    失败：UTF-8、JSON、重复键或顶层类型无效时抛脱敏门禁错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        text = raw.decode("utf-8", errors="strict")
        value = json.loads(text, object_pairs_hook=reject_duplicate_keys)
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ProviderIdentityRunnerError(f"{label} is not strict JSON") from exc
    if not isinstance(value, dict):
        raise ProviderIdentityRunnerError(f"{label} must be a JSON object")
    return value


def load_json(path: Path, *, max_bytes: int = 64 * 1024 * 1024) -> JSONObject:
    """功能：有界读取并严格解析共享 manifest、catalog 或 fixture。

    输入：普通文件路径和最大允许 bytes。
    输出：严格 JSON object。
    不变量：不跟随目录、不容忍重复键或尾随 JSON。
    失败：文件、大小、UTF-8 或 JSON 无效时抛脱敏门禁错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        if not path.is_file():
            raise ProviderIdentityRunnerError(f"required JSON file is missing: {path.name}")
        size = path.stat().st_size
        if size <= 0 or size > max_bytes:
            raise ProviderIdentityRunnerError(f"JSON file size is invalid: {path.name}")
        raw = path.read_bytes()
    except OSError as exc:
        raise ProviderIdentityRunnerError(f"cannot read JSON file: {path.name}") from exc
    return parse_json_bytes(raw, path.name)


def validate_schema_document(value: JSONObject, schema_path: Path) -> None:
    """功能：用指定 Draft 2020-12 Schema 严格验证一个独立文档。

    输入：已严格解析的文档与不依赖外部引用的 Schema 路径。
    输出：验证成功时无返回值。
    不变量：仅在调用方显式选择 Schema gate 时导入开发依赖。
    失败：依赖缺失、Schema 无效或实例不匹配时抛脱敏门禁错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        import jsonschema
    except ImportError as exc:
        raise ProviderIdentityRunnerError(
            "--schema-root requires the optional jsonschema package"
        ) from exc
    schema = load_json(schema_path, max_bytes=4 * 1024 * 1024)
    try:
        jsonschema.Draft202012Validator.check_schema(schema)
        jsonschema.Draft202012Validator(schema).validate(value)
    except jsonschema.SchemaError as exc:
        raise ProviderIdentityRunnerError(f"invalid schema: {schema_path.name}") from exc
    except jsonschema.ValidationError as exc:
        location = "/".join(str(item) for item in exc.absolute_path) or "<root>"
        raise ProviderIdentityRunnerError(
            f"{schema_path.name} rejected instance at {location}"
        ) from exc


def require_list(value: Any, label: str) -> list[Any]:
    """功能：把动态 JSON 值收窄为 list。

    输入：任意值和安全标签。
    输出：原 list。
    失败：类型错误时抛脱敏门禁错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, list):
        raise ProviderIdentityRunnerError(f"{label} must be an array")
    return value


def require_object(value: Any, label: str) -> JSONObject:
    """功能：把动态 JSON 值收窄为 object。

    输入：任意值和安全标签。
    输出：原 object。
    失败：类型错误时抛脱敏门禁错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, dict):
        raise ProviderIdentityRunnerError(f"{label} must be an object")
    return value


def index_manifest(manifest: JSONObject) -> tuple[dict[RouteKey, JSONObject], JSONObject]:
    """功能：建立 35 Provider、45 route 的唯一索引并验证引用闭包。

    输入：已通过严格 JSON 的 Provider manifest。
    输出：按 `(providerId, apiFamily)` 索引的路由上下文和 Provider ID 索引。
    不变量：Provider、route、adapter、auth profile 引用均唯一且闭合。
    失败：计数、重复身份或悬空引用时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    providers = require_list(manifest.get("providers"), "manifest.providers")
    adapters = require_list(manifest.get("adapters"), "manifest.adapters")
    if len(providers) != 35:
        raise ProviderIdentityRunnerError("manifest must contain 35 Providers")
    adapter_index: dict[str, JSONObject] = {}
    for item in adapters:
        adapter = require_object(item, "manifest adapter")
        adapter_id = adapter.get("id")
        if not isinstance(adapter_id, str) or adapter_id in adapter_index:
            raise ProviderIdentityRunnerError("manifest adapter identity is invalid")
        adapter_index[adapter_id] = adapter

    provider_index: JSONObject = {}
    route_index: dict[RouteKey, JSONObject] = {}
    for item in providers:
        provider = require_object(item, "manifest Provider")
        provider_id = provider.get("id")
        if not isinstance(provider_id, str) or provider_id in provider_index:
            raise ProviderIdentityRunnerError("manifest Provider identity is invalid")
        provider_index[provider_id] = provider
        auth_profiles = require_list(provider.get("authProfiles"), "Provider authProfiles")
        auth_ids: set[str] = set()
        for raw_profile in auth_profiles:
            profile = require_object(raw_profile, "auth profile")
            profile_id = profile.get("id")
            if not isinstance(profile_id, str) or profile_id in auth_ids:
                raise ProviderIdentityRunnerError("auth profile identity is invalid")
            auth_ids.add(profile_id)
        for raw_route in require_list(provider.get("routes"), "Provider routes"):
            route = require_object(raw_route, "Provider route")
            family = route.get("apiFamily")
            adapter_id = route.get("adapterId")
            if not isinstance(family, str) or not isinstance(adapter_id, str):
                raise ProviderIdentityRunnerError("route identity is invalid")
            key = (provider_id, family)
            if key in route_index:
                raise ProviderIdentityRunnerError("manifest route identity is duplicated")
            adapter = adapter_index.get(adapter_id)
            if adapter is None or adapter.get("apiFamily") != family:
                raise ProviderIdentityRunnerError("route adapter reference is invalid")
            route_auth = require_list(route.get("authProfileIds"), "route authProfileIds")
            if not route_auth or any(item not in auth_ids for item in route_auth):
                raise ProviderIdentityRunnerError("route auth profile reference is invalid")
            route_index[key] = {"provider": provider, "route": route}
    if len(route_index) != 45:
        raise ProviderIdentityRunnerError("manifest must contain 45 unique routes")
    return route_index, provider_index


def index_catalog(
    catalog: JSONObject, route_index: dict[RouteKey, JSONObject]
) -> dict[RouteKey, list[JSONObject]]:
    """功能：按 manifest route 索引 1,076 个冻结模型并保留跨 family 重名。

    输入：严格 catalog 和已验证 manifest route 索引。
    输出：每条 route 的稳定 catalog 行列表。
    不变量：唯一键是 `(providerId, modelId, apiFamily)`，不是二元模型键。
    失败：悬空 route、重复三元组、空 route 或总数漂移时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    models = require_list(catalog.get("models"), "catalog.models")
    if len(models) != 1076:
        raise ProviderIdentityRunnerError("catalog must contain 1076 models")
    grouped: dict[RouteKey, list[JSONObject]] = defaultdict(list)
    seen: set[ModelKey] = set()
    for item in models:
        model = require_object(item, "catalog model")
        provider_id = model.get("providerId")
        model_id = model.get("modelId")
        family = model.get("apiFamily")
        if not all(isinstance(value, str) for value in (provider_id, model_id, family)):
            raise ProviderIdentityRunnerError("catalog model identity is invalid")
        route_key = (provider_id, family)
        model_key = (provider_id, model_id, family)
        if route_key not in route_index or model_key in seen:
            raise ProviderIdentityRunnerError("catalog route identity is invalid or duplicated")
        seen.add(model_key)
        grouped[route_key].append(model)
    if set(grouped) != set(route_index):
        raise ProviderIdentityRunnerError("every manifest route must have catalog models")
    for route_models in grouped.values():
        route_models.sort(key=lambda model: str(model["modelId"]))
    return dict(grouped)


def route_ref(key: RouteKey) -> JSONObject:
    """功能：把内部 route tuple 转为品牌中立 presence DTO。

    输入：`(providerId, apiFamily)`。
    输出：只含两个公共身份字段的新 object。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {"providerId": key[0], "apiFamily": key[1]}


def selected_routes(case: JSONObject, route_index: dict[RouteKey, JSONObject]) -> list[RouteKey]:
    """功能：将 fixture 的 all 或显式 route 选择解析为稳定唯一列表。

    输入：一个案例和 canonical route 索引。
    输出：按 Provider/family 排序的 route keys。
    失败：未知或重复 route 时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    raw_routes = case.get("routes")
    if raw_routes == "all":
        return sorted(route_index)
    result: list[RouteKey] = []
    seen: set[RouteKey] = set()
    for raw_ref in require_list(raw_routes, "case.routes"):
        item = require_object(raw_ref, "case route reference")
        key = (item.get("providerId"), item.get("apiFamily"))
        if not all(isinstance(value, str) for value in key) or key not in route_index:
            raise ProviderIdentityRunnerError("case references an unknown route")
        typed_key = (str(key[0]), str(key[1]))
        if typed_key in seen:
            raise ProviderIdentityRunnerError("case route reference is duplicated")
        seen.add(typed_key)
        result.append(typed_key)
    return sorted(result)


def endpoint_environment_names(route: JSONObject) -> set[str]:
    """功能：收集一条 route 广告判定所需的 endpoint/template 配置名称。

    输入：manifest route。
    输出：只含环境变量名称的集合，不读取任何值。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    endpoint = require_object(route.get("endpoint"), "route endpoint")
    names = {
        str(item) for item in require_list(endpoint.get("runtimeEndpointEnv"), "runtimeEndpointEnv")
    }
    for raw_binding in require_list(endpoint.get("templateBindings"), "templateBindings"):
        binding = require_object(raw_binding, "template binding")
        names.update(
            str(item) for item in require_list(binding.get("environment"), "binding environment")
        )
    return names


def build_presence_config(case: JSONObject, route_index: dict[RouteKey, JSONObject]) -> JSONObject:
    """功能：从高层案例自行生成无 credential/endpoint 值的 presence 配置。

    输入：fixture 案例和 canonical route 索引。
    输出：严格、稳定排序的 conformance-only presence object。
    不变量：配置只含 manifest 身份和存在性，不含路径、URL、token 或 secret 值。
    失败：案例引用不属于所选 route 的 auth profile 时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    routes = selected_routes(case, route_index)
    gates = require_object(case.get("gates"), "case.gates")
    adapter_ids: set[str] = set()
    capability_allowances: list[JSONObject] = []
    auth_refs: set[AuthKey] = set()
    configured_names: set[str] = set()

    if gates.get("adapter") == "available":
        adapter_ids.update(str(route_index[key]["route"]["adapterId"]) for key in routes)
    if gates.get("capability") == "allowed":
        raw_features = case.get("capabilityFeatures", ALL_CAPABILITY_FEATURES)
        features = [str(item) for item in require_list(raw_features, "capabilityFeatures")]
        for key in routes:
            capability_allowances.append({**route_ref(key), "features": sorted(features)})
    if gates.get("authentication") == "available":
        explicit_profiles = case.get("usableAuthProfiles")
        if explicit_profiles is None:
            for key in routes:
                route = route_index[key]["route"]
                for profile_id in require_list(route.get("authProfileIds"), "route authProfileIds"):
                    auth_refs.add((key[0], str(profile_id)))
        else:
            for raw_ref in require_list(explicit_profiles, "usableAuthProfiles"):
                item = require_object(raw_ref, "usable auth reference")
                auth_key = (str(item.get("providerId")), str(item.get("authProfileId")))
                matching_routes = [key for key in routes if key[0] == auth_key[0]]
                if not matching_routes or not any(
                    auth_key[1] in route_index[key]["route"]["authProfileIds"]
                    for key in matching_routes
                ):
                    raise ProviderIdentityRunnerError(
                        "case auth profile is outside selected routes"
                    )
                auth_refs.add(auth_key)
    if gates.get("endpoint") == "configured":
        for key in routes:
            configured_names.update(endpoint_environment_names(route_index[key]["route"]))

    return {
        "schemaVersion": "0.1",
        "implementedAdapterIds": sorted(adapter_ids),
        "capabilityAllowances": sorted(
            capability_allowances,
            key=lambda item: (str(item["providerId"]), str(item["apiFamily"])),
        ),
        "usableAuthProfiles": [
            {"providerId": provider_id, "authProfileId": profile_id}
            for provider_id, profile_id in sorted(auth_refs)
        ],
        "configuredEnvironmentNames": sorted(configured_names),
    }


def allowance_index(config: JSONObject) -> dict[RouteKey, set[str]]:
    """功能：把 capability allowance DTO 转为 route 到 feature 集合的索引。

    输入：已生成或严格加载的 presence config。
    输出：route-keyed feature sets。
    失败：重复 route allowance 时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    result: dict[RouteKey, set[str]] = {}
    for raw_item in require_list(config.get("capabilityAllowances"), "capabilityAllowances"):
        item = require_object(raw_item, "capability allowance")
        key = (str(item.get("providerId")), str(item.get("apiFamily")))
        if key in result:
            raise ProviderIdentityRunnerError("capability allowance route is duplicated")
        result[key] = {str(value) for value in require_list(item.get("features"), "features")}
    return result


def route_is_usable(
    key: RouteKey,
    context: JSONObject,
    route_models: Sequence[JSONObject],
    config: JSONObject,
) -> bool:
    """功能：计算 manifest/auth/endpoint/adapter/capability 五项精确交集。

    输入：route 身份、manifest 上下文、该 route 的 catalog 行和 presence 配置。
    输出：整条 route 是否可进入广告。
    不变量：不读取 credential/endpoint 值；任一 predicate 缺失即 false。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    route = context["route"]
    if route.get("adapterId") not in set(config["implementedAdapterIds"]):
        return False
    allowances = allowance_index(config)
    features = allowances.get(key, set())
    if "authentication" not in features:
        return False
    if route.get("media") == "text" and "text" not in features:
        return False
    if route.get("media") == "image" and (
        "image_output" not in features or not ({"text", "image_input"} & features)
    ):
        return False
    usable_auth = {
        (str(item["providerId"]), str(item["authProfileId"]))
        for item in config["usableAuthProfiles"]
    }
    if not any((key[0], str(profile_id)) in usable_auth for profile_id in route["authProfileIds"]):
        return False

    configured = set(config["configuredEnvironmentNames"])
    endpoint = route["endpoint"]
    for binding in endpoint["templateBindings"]:
        if not configured.intersection(binding["environment"]):
            return False
    runtime_names = endpoint["runtimeEndpointEnv"]
    if runtime_names and not configured.intersection(runtime_names):
        return False
    if any(model["endpoint"]["strategy"] == "runtime-required" for model in route_models):
        if not runtime_names or not configured.intersection(runtime_names):
            return False
    return True


def usable_route_keys(
    route_index: dict[RouteKey, JSONObject],
    catalog_index: dict[RouteKey, list[JSONObject]],
    config: JSONObject,
) -> list[RouteKey]:
    """功能：稳定列出 presence 配置允许广告的全部 canonical routes。

    输入：manifest、catalog 索引和 presence 配置。
    输出：按 Provider/family 排序的可用 route keys。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return [
        key
        for key in sorted(route_index)
        if route_is_usable(key, route_index[key], catalog_index[key], config)
    ]


def normalized_model(model: JSONObject, features: set[str]) -> JSONObject:
    """功能：把冻结 catalog 行投影为协议 model descriptor 的真实能力交集。

    输入：一个 catalog row 和该 route 获准公开的 capability features。
    输出：不含 endpoint、credential、adapter 或兼容内部字段的 model DTO。
    不变量：输入/输出/推理只会缩小 catalog 证据，绝不会凭空增加。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    catalog_capabilities = model["capabilities"]
    input_media = [
        media
        for media in catalog_capabilities["input"]
        if (media == "text" and "text" in features)
        or (media == "image" and "image_input" in features)
    ]
    if model["media"] == "text":
        output_media = ["text"]
        streaming = "streaming" in features
        tools = "tools" in features
        reasoning = bool(catalog_capabilities["reasoning"] and "reasoning" in features)
    else:
        output_media = [
            media
            for media in catalog_capabilities["output"]
            if (media == "text" and "text" in features)
            or (media == "image" and "image_output" in features)
        ]
        streaming = False
        tools = False
        reasoning = False
    capabilities: JSONObject = {
        "input": input_media,
        "output": output_media,
        "streaming": streaming,
        "tools": tools,
        "reasoning": reasoning,
    }
    limits = model.get("limits")
    if isinstance(limits, dict):
        capabilities["contextTokens"] = limits["contextWindow"]
        capabilities["maxOutputTokens"] = limits["maxOutputTokens"]
    return {
        "providerId": model["providerId"],
        "modelId": model["modelId"],
        "displayName": model["name"],
        "apiFamily": model["apiFamily"],
        "capabilities": capabilities,
    }


def expected_models(
    usable_routes: Sequence[RouteKey],
    catalog_index: dict[RouteKey, list[JSONObject]],
    config: JSONObject,
) -> list[JSONObject]:
    """功能：从可用 route 快照生成稳定的预期 models/list 描述。

    输入：可用 route、catalog 索引和 capability presence。
    输出：按三元模型身份排序的 descriptor 列表。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    allowances = allowance_index(config)
    result: list[JSONObject] = []
    for key in usable_routes:
        for model in catalog_index[key]:
            descriptor = normalized_model(model, allowances[key])
            capabilities = descriptor["capabilities"]
            if capabilities["input"] and capabilities["output"]:
                result.append(descriptor)
    result.sort(key=model_key)
    return result


def model_key(model: JSONObject) -> ModelKey:
    """功能：提取协议 model descriptor 的稳定三元身份排序键。

    输入：model DTO。
    输出：`(providerId, modelId, apiFamily)` 字符串 tuple。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return (str(model.get("providerId")), str(model.get("modelId")), str(model.get("apiFamily")))


def faux_model() -> JSONObject:
    """功能：构造不属于 live manifest 的固定 faux 模型描述。

    输出：符合公共 model Schema 的新对象。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "providerId": "faux",
        "modelId": "faux-v1",
        "displayName": "Deterministic Faux v1",
        "apiFamily": "faux",
        "capabilities": {
            "input": ["text"],
            "output": ["text"],
            "streaming": True,
            "tools": True,
            "reasoning": False,
        },
    }


def assert_fixture_counts(
    fixture: JSONObject,
    route_index: dict[RouteKey, JSONObject],
    catalog_index: dict[RouteKey, list[JSONObject]],
) -> None:
    """功能：验证 fixture census 与完整 manifest/catalog 及歧义对完全一致。

    输入：fixture、route 和 catalog 索引。
    输出：全部固定计数及 OpenRouter 双 family 歧义成立时无返回值。
    失败：任何 census 漂移时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    counts = require_object(fixture.get("counts"), "fixture.counts")
    all_models = [model for models in catalog_index.values() for model in models]
    provider_model_counts = Counter(
        (str(model["providerId"]), str(model["modelId"])) for model in all_models
    )
    ambiguous = {key for key, count in provider_model_counts.items() if count > 1}
    initialize_ids = sum(
        len({str(model["modelId"]) for model in all_models if model["providerId"] == provider_id})
        for provider_id in {key[0] for key in route_index}
    )
    observed = {
        "providers": len({key[0] for key in route_index}),
        "routes": len(route_index),
        "models": len(all_models),
        "initializeModelIds": initialize_ids,
        "ambiguousProviderModelPairs": len(ambiguous),
    }
    if observed != counts:
        raise ProviderIdentityRunnerError("fixture census does not match manifest/catalog")
    expected_ambiguous = {
        ("openrouter", "google/gemini-3-pro-image"),
        ("openrouter", "openrouter/auto"),
    }
    if ambiguous != expected_ambiguous:
        raise ProviderIdentityRunnerError("OpenRouter text/image ambiguity census drifted")
    for provider_id, model_id in ambiguous:
        families = {
            str(model["apiFamily"])
            for model in all_models
            if model["providerId"] == provider_id and model["modelId"] == model_id
        }
        if families != {"openai-completions", "openrouter-images"}:
            raise ProviderIdentityRunnerError("ambiguous model pair did not retain both families")


def assert_case_expectation(
    case: JSONObject,
    routes: Sequence[RouteKey],
    models: Sequence[JSONObject],
) -> None:
    """功能：比较一个案例的独立预期计数与交集算法结果。

    输入：案例、实际可用 routes 和预期模型 DTO。
    输出：四项计数一致时无返回值。
    失败：fixture 与算法分歧时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    expected = require_object(case.get("expected"), "case.expected")
    provider_ids = {key[0] for key in routes}
    initialize_count = sum(
        len({str(model["modelId"]) for model in models if model["providerId"] == provider_id})
        for provider_id in provider_ids
    )
    observed = {
        "providers": len(provider_ids),
        "routes": len(routes),
        "models": len(models),
        "initializeModelIds": initialize_count,
    }
    if observed != expected:
        raise ProviderIdentityRunnerError(f"case count mismatch: {case.get('name')}")


def validate_static_suite(
    fixture: JSONObject,
    manifest: JSONObject,
    catalog: JSONObject,
    *,
    schema_root: Path | None,
) -> tuple[dict[RouteKey, JSONObject], JSONObject, dict[RouteKey, list[JSONObject]]]:
    """功能：执行 Schema、census、引用闭包和全部案例的静态门禁。

    输入：fixture/manifest/catalog 及可选 Schema 根目录。
    输出：供动态门禁复用的三个索引。
    不变量：不启动进程、不访问网络、不读取 credential 值。
    失败：任一静态不变量漂移时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if schema_root is not None:
        validate_schema_document(fixture, schema_root / "provider-identity-cases.schema.json")
        validate_schema_document(manifest, schema_root / "provider-manifest.schema.json")
        validate_schema_document(catalog, schema_root / "model-catalog.schema.json")
    route_index, provider_index = index_manifest(manifest)
    catalog_index = index_catalog(catalog, route_index)
    assert_fixture_counts(fixture, route_index, catalog_index)
    names: set[str] = set()
    for raw_case in require_list(fixture.get("cases"), "fixture.cases"):
        case = require_object(raw_case, "fixture case")
        name = case.get("name")
        if not isinstance(name, str) or name in names:
            raise ProviderIdentityRunnerError("fixture case name is invalid or duplicated")
        names.add(name)
        config = build_presence_config(case, route_index)
        if schema_root is not None:
            validate_schema_document(config, schema_root / "provider-identity-config.schema.json")
        routes = usable_route_keys(route_index, catalog_index, config)
        models = expected_models(routes, catalog_index, config)
        assert_case_expectation(case, routes, models)
    negative_kinds = [
        require_object(item, "negative case").get("kind")
        for item in require_list(fixture.get("negativeCases"), "negativeCases")
    ]
    if set(negative_kinds) != {
        "wire_unknown_field",
        "wire_duplicate_key",
        "config_unknown_field",
        "config_duplicate_key",
    }:
        raise ProviderIdentityRunnerError("negative case coverage is incomplete")
    return route_index, provider_index, catalog_index


def encode_request(value: JSONObject) -> bytes:
    """功能：把一个测试请求编码为紧凑 UTF-8 JSON，不附加 LF。

    输入：JSON-RPC request object。
    输出：确定性 bytes。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def initialize_request(request_id: str = "identity-initialize") -> JSONObject:
    """功能：构造 Provider 身份门禁的公共 initialize 请求。

    输入：opaque 测试 request ID。
    输出：只协商协议 0.1 的 JSON-RPC object。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": request_id,
        "method": "initialize",
        "params": {
            "protocolVersions": ["0.1"],
            "client": {"name": "provider-identity-conformance", "version": "0.1.0"},
            "capabilities": {},
        },
    }


def positive_requests(provider_ids: Sequence[str]) -> list[JSONObject]:
    """功能：生成 initialize、全量、逐 Provider 和未知 Provider 的查询序列。

    输入：35 个 canonical Provider IDs。
    输出：ID 唯一且稳定排序的 JSON-RPC requests。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    requests = [
        initialize_request(),
        {"jsonrpc": "2.0", "id": "models-all", "method": "models/list", "params": {}},
    ]
    for index, provider_id in enumerate(sorted(provider_ids), start=1):
        requests.append(
            {
                "jsonrpc": "2.0",
                "id": f"models-provider-{index}",
                "method": "models/list",
                "params": {"providerId": provider_id},
            }
        )
    requests.append(
        {
            "jsonrpc": "2.0",
            "id": "models-unknown",
            "method": "models/list",
            "params": {"providerId": "manifest-unknown"},
        }
    )
    return requests


def synthetic_environment_value(name: str) -> str:
    """功能：为 endpoint/configuration 名称生成不指向真实服务的安全测试值。

    输入：manifest 中的配置环境名称。
    输出：满足已知模板形状的非敏感字符串。
    不变量：URL 使用保留 `.invalid`，runner 从不发起 Provider 请求。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    values = {
        "AZURE_OPENAI_BASE_URL": "https://provider-identity.invalid",
        "AZURE_OPENAI_RESOURCE_NAME": "provider-identity-conformance",
        "AWS_DEFAULT_REGION": "us-east-1",
        "AWS_REGION": "us-east-1",
        "CLOUDFLARE_ACCOUNT_ID": "conformance-account",
        "CLOUDFLARE_GATEWAY_ID": "conformance-gateway",
        "GOOGLE_CLOUD_LOCATION": "us-central1",
    }
    return values.get(name, "provider-identity-conformance")


def child_environment(
    manifest: JSONObject,
    config: JSONObject,
    config_path: Path | None,
    temp_root: Path,
    canary: str,
) -> dict[str, str]:
    """功能：构造清除真实 Provider/cloud 状态的最小化子进程环境。

    输入：manifest、presence 配置、可选配置路径、临时根和随机 canary。
    输出：只为合成 presence 设置值的环境副本。
    不变量：真实继承 credential/endpoint 被删除；proxy 固定为 dead loopback，home 临时化。
    失败：本函数不访问 credential store 或网络。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    environment = dict(os.environ)
    for inherited_name in list(environment):
        normalized_name = inherited_name.upper()
        if any(marker in normalized_name for marker in SENSITIVE_ENV_MARKERS):
            environment.pop(inherited_name, None)
    manifest_names = {
        str(entry["name"])
        for provider in manifest["providers"]
        for entry in provider["environment"]
    }
    for name in manifest_names | {
        CONFIG_ENV,
        CANARY_ENV,
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "http_proxy",
        "https_proxy",
        "all_proxy",
        "NO_PROXY",
        "no_proxy",
    }:
        environment.pop(name, None)
    environment.update(
        {
            "HOME": str(temp_root / "home"),
            "USERPROFILE": str(temp_root / "home"),
            "XDG_CONFIG_HOME": str(temp_root / "xdg-config"),
            "XDG_CACHE_HOME": str(temp_root / "xdg-cache"),
            "AWS_EC2_METADATA_DISABLED": "true",
            "AZURE_CONFIG_DIR": str(temp_root / "azure"),
            "HTTP_PROXY": DEAD_PROXY_URL,
            "HTTPS_PROXY": DEAD_PROXY_URL,
            "ALL_PROXY": DEAD_PROXY_URL,
            "http_proxy": DEAD_PROXY_URL,
            "https_proxy": DEAD_PROXY_URL,
            "all_proxy": DEAD_PROXY_URL,
            "NO_PROXY": LOOPBACK_NO_PROXY,
            "no_proxy": LOOPBACK_NO_PROXY,
            CANARY_ENV: canary,
        }
    )
    if config_path is not None:
        environment[CONFIG_ENV] = str(config_path)
    configured_names = {str(item) for item in config["configuredEnvironmentNames"]}
    for name in configured_names:
        environment[name] = synthetic_environment_value(name)

    provider_index = {str(provider["id"]): provider for provider in manifest["providers"]}
    for auth_ref in config["usableAuthProfiles"]:
        provider = provider_index[str(auth_ref["providerId"])]
        roles = {str(entry["name"]): str(entry["role"]) for entry in provider["environment"]}
        profile = next(
            item for item in provider["authProfiles"] if item["id"] == auth_ref["authProfileId"]
        )
        for name in profile["environment"]:
            if roles.get(name) == "secret":
                environment[str(name)] = canary
    return environment


def owned_process_group_alive(process: subprocess.Popen[bytes]) -> bool:
    """功能：判断 runner 创建的 POSIX 进程组或跨平台根进程是否仍存活。

    输入：以独立 session/进程组启动的 daemon 根进程。
    输出：POSIX 上组内仍有成员，或非 POSIX 根进程仍运行时为 true。
    不变量：只探测 runner 所有的 PID/PGID，不扫描或信号其它进程。
    失败：权限错误保守视为仍存活；进程不存在返回 false。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if os.name != "posix":
        return process.poll() is None
    try:
        os.killpg(process.pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


def terminate_process_tree(process: subprocess.Popen[bytes]) -> None:
    """功能：两阶段终止 runner 所有的 daemon 根进程和普通后代进程树。

    输入：POSIX 上以 setsid 启动、其它平台上直接启动的根进程。
    输出：根进程已 wait；POSIX 独立进程组已收到 TERM/KILL。
    不变量：POSIX 只信号 `PGID == root PID` 的自有组；其它平台只 terminate/kill 根进程。
    失败：根进程在有界清理期限内无法回收时抛脱敏门禁错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if os.name == "posix":
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except OSError:
            pass
    elif process.poll() is None:
        try:
            process.terminate()
        except OSError:
            pass
    grace_deadline = time.monotonic() + PROCESS_TERMINATION_GRACE_SECONDS
    while owned_process_group_alive(process) and time.monotonic() < grace_deadline:
        time.sleep(0.01)
    if os.name == "posix":
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except OSError:
            pass
    elif process.poll() is None:
        try:
            process.kill()
        except OSError:
            pass
    try:
        process.wait(timeout=PROCESS_REAP_TIMEOUT_SECONDS)
    except subprocess.TimeoutExpired as exc:
        try:
            process.kill()
        except OSError:
            pass
        raise ProviderIdentityRunnerError("daemon process tree could not be reaped") from exc


def drain_process_pipe(
    stream: Any,
    stream_name: str,
    events: queue.Queue[tuple[str, bytes | None, bool]],
    stop: threading.Event,
) -> None:
    """功能：在专用线程持续读取一个 daemon pipe，并通过小型有界队列回传。

    输入：二进制 pipe、固定标签、容量受限事件队列和监督停止信号。
    输出：按读取顺序发送 chunks，最终发送一个 EOF/错误终止事件。
    不变量：单次最多 64 KiB；队列背压限制 reader 内存，绝不解析或记录内容。
    失败：pipe 读取异常只发送脱敏错误标志，由监督主循环统一清理进程树。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    failed = False
    try:
        while not stop.is_set():
            chunk = stream.read(PROCESS_READ_CHUNK_BYTES)
            if not chunk:
                break
            while not stop.is_set():
                try:
                    events.put((stream_name, chunk, False), timeout=0.05)
                    break
                except queue.Full:
                    continue
    except (OSError, ValueError):
        failed = True
    finally:
        while not stop.is_set():
            try:
                events.put((stream_name, None, failed), timeout=0.05)
                break
            except queue.Full:
                continue


def write_process_input(
    stream: Any,
    payload: bytes,
    failed: threading.Event,
) -> None:
    """功能：在专用线程有界写入完整 NDJSON 请求并关闭 daemon stdin。

    输入：二进制 stdin pipe、最多 2 MiB payload 和错误信号。
    输出：写入/flush 后关闭 pipe；对端提前退出的 BrokenPipe 视为正常竞态。
    不变量：主监督线程不因候选 daemon 拒绝读取 stdin 而越过总 deadline。
    失败：非 BrokenPipe 的写入异常设置脱敏标志，不传播或回显 payload。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        stream.write(payload)
        stream.flush()
    except BrokenPipeError:
        pass
    except (OSError, ValueError):
        failed.set()
    finally:
        try:
            stream.close()
        except (OSError, ValueError):
            pass


def close_process_pipes(process: subprocess.Popen[bytes]) -> None:
    """功能：尽力关闭 daemon 的 stdin/stdout/stderr 本地 pipe 端以解除 reader 阻塞。

    输入：runner 创建的 Popen。
    输出：可用 pipe 均被关闭。
    不变量：不读取、记录或回显残余内容；并发关闭错误被安全忽略。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    for stream in (process.stdin, process.stdout, process.stderr):
        if stream is None:
            continue
        try:
            stream.close()
        except (OSError, ValueError):
            pass


def run_process(
    argv: Sequence[str],
    raw_requests: Sequence[bytes],
    environment: dict[str, str],
    timeout: float,
    *,
    max_output_bytes: int = MAX_PROCESS_OUTPUT_BYTES,
    max_stderr_bytes: int = MAX_PROCESS_STDERR_BYTES,
) -> tuple[subprocess.CompletedProcess[bytes], list[JSONObject]]:
    """功能：持续有界排空双流，在总 deadline 下无 shell 执行 daemon。

    输入：argv、无 LF 请求帧、净化环境、timeout 和可选输出硬上限。
    输出：有界 CompletedProcess 与严格 server frame 列表。
    不变量：reader 队列有界；超限/timeout 立即清理自有进程树；不回显 argv/env/raw output。
    失败：启动、pipe、deadline、输出、后代存活或非法 NDJSON 时抛脱敏门禁错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if (
        not argv
        or timeout <= 0
        or max_output_bytes <= 0
        or max_stderr_bytes <= 0
        or max_stderr_bytes > max_output_bytes
    ):
        raise ProviderIdentityRunnerError("daemon execution limits are invalid")
    input_size = sum(len(frame) + 1 for frame in raw_requests)
    if input_size > 2 * 1024 * 1024:
        raise ProviderIdentityRunnerError("daemon request input exceeded limit")
    payload = b"".join(frame + b"\n" for frame in raw_requests)
    creation_flags = 0
    if os.name == "nt":
        creation_flags = int(getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0))
    deadline = time.monotonic() + timeout
    try:
        process = subprocess.Popen(
            list(argv),
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            cwd=ROOT,
            env=environment,
            start_new_session=os.name == "posix",
            creationflags=creation_flags,
            close_fds=True,
            bufsize=0,
        )
    except OSError as exc:
        raise ProviderIdentityRunnerError("provider identity daemon failed to start") from exc
    if process.stdin is None or process.stdout is None or process.stderr is None:
        terminate_process_tree(process)
        close_process_pipes(process)
        raise ProviderIdentityRunnerError("daemon pipes are unavailable")

    events: queue.Queue[tuple[str, bytes | None, bool]] = queue.Queue(maxsize=8)
    stop_readers = threading.Event()
    readers = [
        threading.Thread(
            target=drain_process_pipe,
            args=(process.stdout, "stdout", events, stop_readers),
            daemon=True,
            name="provider-identity-stdout",
        ),
        threading.Thread(
            target=drain_process_pipe,
            args=(process.stderr, "stderr", events, stop_readers),
            daemon=True,
            name="provider-identity-stderr",
        ),
    ]
    input_failed = threading.Event()
    writer = threading.Thread(
        target=write_process_input,
        args=(process.stdin, payload, input_failed),
        daemon=True,
        name="provider-identity-stdin",
    )
    stdout = bytearray()
    stderr = bytearray()
    total_seen = 0
    stderr_seen = 0
    active_readers = len(readers)
    failure: str | None = None
    try:
        for reader in readers:
            reader.start()
        writer.start()

        while active_readers:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                failure = "timeout"
                break
            try:
                stream_name, chunk, read_failed = events.get(timeout=min(0.05, remaining))
            except queue.Empty:
                continue
            if chunk is None:
                active_readers -= 1
                if read_failed:
                    failure = "pipe"
                    break
                continue
            total_seen += len(chunk)
            if stream_name == "stderr":
                stderr_seen += len(chunk)
            total_available = max(0, max_output_bytes - len(stdout) - len(stderr))
            if stream_name == "stdout":
                stdout.extend(chunk[:total_available])
            else:
                stderr_available = max(0, min(total_available, max_stderr_bytes - len(stderr)))
                stderr.extend(chunk[:stderr_available])
            if total_seen > max_output_bytes or stderr_seen > max_stderr_bytes:
                failure = "output"
                break

        if failure is None:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                failure = "timeout"
            else:
                try:
                    process.wait(timeout=remaining)
                except subprocess.TimeoutExpired:
                    failure = "timeout"
        if failure is not None:
            stop_readers.set()
            terminate_process_tree(process)
            close_process_pipes(process)
        elif owned_process_group_alive(process):
            stop_readers.set()
            terminate_process_tree(process)
            close_process_pipes(process)
            failure = "descendant"

        for reader in readers:
            reader.join(timeout=PROCESS_REAP_TIMEOUT_SECONDS)
        writer.join(timeout=PROCESS_REAP_TIMEOUT_SECONDS)
        if any(reader.is_alive() for reader in readers) or writer.is_alive():
            raise ProviderIdentityRunnerError("daemon pipe workers could not be stopped")
        if failure is None and input_failed.is_set() and process.returncode == 0:
            failure = "pipe"
        if failure == "timeout":
            raise ProviderIdentityRunnerError("provider identity daemon exceeded deadline")
        if failure == "output":
            raise ProviderIdentityRunnerError("provider identity daemon output exceeded limit")
        if failure == "pipe":
            raise ProviderIdentityRunnerError("provider identity daemon pipe failed")
        if failure == "descendant":
            raise ProviderIdentityRunnerError("provider identity daemon left a descendant")
        completed = subprocess.CompletedProcess(
            ["<provider-identity-daemon>"],
            int(process.returncode),
            bytes(stdout),
            bytes(stderr),
        )
    except BaseException:
        stop_readers.set()
        if process.poll() is None or owned_process_group_alive(process):
            terminate_process_tree(process)
        raise
    finally:
        stop_readers.set()
        close_process_pipes(process)

    frames: list[JSONObject] = []
    for index, line in enumerate(completed.stdout.splitlines(), start=1):
        if not line:
            raise ProviderIdentityRunnerError("daemon emitted a blank stdout frame")
        frames.append(parse_json_bytes(line, f"server frame {index}"))
    return completed, frames


def assert_no_private_fields(value: Any) -> None:
    """功能：递归拒绝协议广告中的内部 credential/endpoint/adapter 字段名。

    输入：一个已解析 server frame 或其子值。
    输出：只含公共 DTO 字段时无返回值。
    失败：发现内部字段名时 fail closed，不回显字段值。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if isinstance(value, dict):
        for key, child in value.items():
            compact = "".join(character for character in key.lower() if character.isalnum())
            if any(part in compact for part in PRIVATE_FIELD_PARTS):
                raise ProviderIdentityRunnerError("protocol exposed a private provider field")
            assert_no_private_fields(child)
    elif isinstance(value, list):
        for child in value:
            assert_no_private_fields(child)


def response_map(frames: Sequence[JSONObject]) -> dict[str, JSONObject]:
    """功能：按 opaque string ID 建立一请求一响应索引。

    输入：daemon server frames。
    输出：无重复 ID 的 response map。
    失败：notification、非字符串 ID 或重复响应时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    result: dict[str, JSONObject] = {}
    for frame in frames:
        response_id = frame.get("id")
        if not isinstance(response_id, str) or response_id in result:
            raise ProviderIdentityRunnerError("daemon response correlation is invalid")
        result[response_id] = frame
    return result


def success_result(responses: dict[str, JSONObject], request_id: str) -> JSONObject:
    """功能：取得指定请求的成功 result 并拒绝缺失或 error response。

    输入：response map 和 request ID。
    输出：result object。
    失败：响应缺失、失败或 result 非 object 时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    response = responses.get(request_id)
    if response is None or "error" in response:
        raise ProviderIdentityRunnerError(f"request did not succeed: {request_id}")
    return require_object(response.get("result"), f"{request_id} result")


def expected_provider_capabilities(models: Sequence[JSONObject]) -> list[JSONObject]:
    """功能：从 models/list 快照派生 initialize Provider 级模型去重并集。

    输入：canonical live model descriptors。
    输出：含固定 faux、按 ID 排序的 Provider capability 数组。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    grouped: dict[str, set[str]] = defaultdict(set)
    grouped["faux"].add("faux-v1")
    for model in models:
        grouped[str(model["providerId"])].add(str(model["modelId"]))
    return [
        {"id": provider_id, "models": sorted(model_ids)}
        for provider_id, model_ids in sorted(grouped.items())
    ]


def assert_advertisement(
    frames: Sequence[JSONObject],
    requests: Sequence[JSONObject],
    provider_ids: Sequence[str],
    models: Sequence[JSONObject],
) -> None:
    """功能：比较 initialize、全量及逐 Provider models/list 的完整黑盒事实。

    输入：server frames、请求、canonical Provider IDs 和预期 live models。
    输出：所有广告均为同一交集快照时无返回值。
    不变量：三元模型身份保留、Provider 摘要为去重并集、未知过滤返回空。
    失败：额外/缺失/乱序/私有字段/能力投影漂移时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if len(frames) != len(requests):
        raise ProviderIdentityRunnerError("daemon did not return exactly one response per request")
    responses = response_map(frames)
    initialized = success_result(responses, "identity-initialize")
    capabilities = require_object(initialized.get("capabilities"), "initialize capabilities")
    methods = require_list(capabilities.get("methods"), "initialize methods")
    if "models/list" not in methods:
        raise ProviderIdentityRunnerError("initialize omitted exercised models/list method")
    advertised_providers = require_list(capabilities.get("providers"), "initialize providers")
    if advertised_providers != expected_provider_capabilities(models):
        raise ProviderIdentityRunnerError("initialize Provider/model union is not manifest-derived")

    expected_all = sorted([faux_model(), *models], key=model_key)
    all_result = success_result(responses, "models-all")
    actual_all = require_list(all_result.get("models"), "models-all models")
    if actual_all != expected_all:
        raise ProviderIdentityRunnerError(
            "unfiltered models/list differs from usable route snapshot"
        )
    if len({model_key(require_object(item, "model")) for item in actual_all}) != len(actual_all):
        raise ProviderIdentityRunnerError("models/list duplicated a route-qualified model identity")

    for index, provider_id in enumerate(sorted(provider_ids), start=1):
        result = success_result(responses, f"models-provider-{index}")
        actual = require_list(result.get("models"), "filtered models")
        expected = [model for model in models if model["providerId"] == provider_id]
        if actual != expected:
            raise ProviderIdentityRunnerError("providerId filter changed the usable route snapshot")
    unknown = success_result(responses, "models-unknown")
    if unknown != {"models": []}:
        raise ProviderIdentityRunnerError(
            "unknown well-formed providerId must return an empty list"
        )
    assert_no_private_fields(frames)


def validate_protocol_frames(
    requests: Sequence[JSONObject], frames: Sequence[JSONObject], schema_root: Path | None
) -> None:
    """功能：可选调用共同跨文件 Schema bridge 验证全部协议帧。

    输入：请求、server frames 和可选 Schema 根。
    输出：未选择或验证通过时无返回值。
    失败：共同 Schema bridge 拒绝时转换为门禁错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if schema_root is None:
        return
    try:
        from schema_validation import SchemaValidationError, validate_protocol_trace

        validate_protocol_trace(requests, frames, schema_root)
    except (ImportError, SchemaValidationError) as exc:
        raise ProviderIdentityRunnerError("protocol Schema validation failed") from exc


def run_positive_case(
    argv: Sequence[str],
    case: JSONObject,
    manifest: JSONObject,
    route_index: dict[RouteKey, JSONObject],
    provider_index: JSONObject,
    catalog_index: dict[RouteKey, list[JSONObject]],
    schema_root: Path | None,
    timeout: float,
) -> None:
    """功能：运行一个 presence 案例并验证完整 initialize/models/list 广告。

    输入：daemon argv、案例、共享索引、可选 Schema gate 和 timeout。
    输出：黑盒事实完全匹配时无返回值。
    不变量：default_closed 不传配置路径；每次使用全新 home/canary。
    失败：进程、Schema、泄漏或广告漂移时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    config = build_presence_config(case, route_index)
    usable_routes = usable_route_keys(route_index, catalog_index, config)
    models = expected_models(usable_routes, catalog_index, config)
    assert_case_expectation(case, usable_routes, models)
    requests = positive_requests(sorted(str(item) for item in provider_index))
    raw_requests = [encode_request(request) for request in requests]
    canary = "provider-identity-canary-" + secrets.token_hex(24)
    with tempfile.TemporaryDirectory(prefix="provider-identity-") as temporary:
        temp_root = Path(temporary)
        (temp_root / "home").mkdir()
        config_path: Path | None = None
        if case.get("name") != "default_closed":
            config_path = temp_root / "presence.json"
            config_path.write_text(
                json.dumps(config, ensure_ascii=False, separators=(",", ":")),
                encoding="utf-8",
            )
        environment = child_environment(manifest, config, config_path, temp_root, canary)
        completed, frames = run_process(argv, raw_requests, environment, timeout)
    if completed.returncode != 0:
        raise ProviderIdentityRunnerError("daemon rejected a valid presence case")
    canary_bytes = canary.encode("utf-8")
    if canary_bytes in completed.stdout or canary_bytes in completed.stderr:
        raise ProviderIdentityRunnerError("daemon leaked the synthetic credential canary")
    for provider in manifest["providers"]:
        for entry in provider["environment"]:
            if str(entry["name"]).encode("ascii") in completed.stdout:
                raise ProviderIdentityRunnerError("daemon leaked a credential/config source name")
    validate_protocol_frames(requests, frames, schema_root)
    assert_advertisement(frames, requests, sorted(provider_index), models)
    if case.get("name") == "openrouter_dual_family":
        pairs = Counter((model["providerId"], model["modelId"]) for model in models)
        if sorted(count for count in pairs.values() if count > 1) != [2, 2]:
            raise ProviderIdentityRunnerError(
                "OpenRouter ambiguity was not retained in models/list"
            )


def write_malformed_config(kind: str, path: Path) -> None:
    """功能：只为 strict-config 负例写入未知字段或重复键配置。

    输入：两个固定负例 kind 之一和临时文件路径。
    输出：写入无 credential 值的畸形 JSON。
    不变量：内容只存在临时目录，不进入 fixture 或诊断。
    失败：未知 kind 时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    base = (
        '{"schemaVersion":"0.1","implementedAdapterIds":[],'
        '"capabilityAllowances":[],"usableAuthProfiles":[],'
        '"configuredEnvironmentNames":[]'
    )
    if kind == "config_unknown_field":
        raw = base + ',"unknown":true}'
    elif kind == "config_duplicate_key":
        raw = base + ',"schemaVersion":"0.1"}'
    else:
        raise ProviderIdentityRunnerError("unknown malformed config case")
    path.write_text(raw, encoding="utf-8")


def run_negative_case(
    argv: Sequence[str],
    negative: JSONObject,
    manifest: JSONObject,
    schema_root: Path | None,
    timeout: float,
) -> None:
    """功能：执行 wire/config 未知字段与重复键四个 fail-closed 负例。

    输入：daemon argv、负例、manifest、可选 Schema 根和 timeout。
    输出：得到规范错误码或启动拒绝时无返回值。
    不变量：每个负例使用新进程、新 home、新 canary，不接触网络。
    失败：容忍畸形输入、泄漏 canary 或错误分类漂移时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    kind = str(negative.get("kind"))
    canary = "provider-identity-canary-" + secrets.token_hex(24)
    empty_config: JSONObject = {
        "schemaVersion": "0.1",
        "implementedAdapterIds": [],
        "capabilityAllowances": [],
        "usableAuthProfiles": [],
        "configuredEnvironmentNames": [],
    }
    with tempfile.TemporaryDirectory(prefix="provider-identity-negative-") as temporary:
        temp_root = Path(temporary)
        (temp_root / "home").mkdir()
        config_path: Path | None = None
        if kind.startswith("config_"):
            config_path = temp_root / "malformed-presence.json"
            write_malformed_config(kind, config_path)
        environment = child_environment(manifest, empty_config, config_path, temp_root, canary)
        if kind == "wire_unknown_field":
            requests = [
                encode_request(initialize_request("negative-initialize")),
                encode_request(
                    {
                        "jsonrpc": "2.0",
                        "id": "negative-request",
                        "method": "models/list",
                        "params": {"unknown": True},
                    }
                ),
            ]
        elif kind == "wire_duplicate_key":
            requests = [
                encode_request(initialize_request("negative-initialize")),
                (
                    b'{"jsonrpc":"2.0","id":"negative-request",'
                    b'"method":"models/list","params":{"providerId":"faux",'
                    b'"providerId":"faux"}}'
                ),
            ]
        else:
            requests = [encode_request(initialize_request("negative-initialize"))]
        completed, frames = run_process(argv, requests, environment, timeout)
    canary_bytes = canary.encode("utf-8")
    if canary_bytes in completed.stdout or canary_bytes in completed.stderr:
        raise ProviderIdentityRunnerError("negative case leaked the synthetic canary")
    expected = require_object(negative.get("expected"), "negative expected")
    if kind.startswith("config_"):
        if completed.returncode == 0 or completed.stdout:
            raise ProviderIdentityRunnerError("daemon did not reject malformed presence config")
        return
    if completed.returncode != 0 or len(frames) != 2:
        raise ProviderIdentityRunnerError("daemon did not return the required wire error")
    validate_protocol_frames([], frames, schema_root)
    error = require_object(frames[1].get("error"), "negative error")
    if error.get("code") != expected.get("errorCode"):
        raise ProviderIdentityRunnerError("wire strictness error code drifted")
    if kind == "wire_duplicate_key" and frames[1].get("id") is not None:
        raise ProviderIdentityRunnerError("duplicate-key parse error must use null response ID")


def parse_daemon_command(raw: str) -> list[str]:
    """功能：严格解析无 shell 的 daemon argv JSON。

    输入：CLI 提供的 JSON array 字符串。
    输出：最多 128 项、每项最多 4,096 字符的非空字符串 argv。
    不变量：总 JSON 文本有界，不执行 shell，也不回显输入或参数。
    失败：文本、数组、项数、项长度、空项或 NUL 越界时 fail closed。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if len(raw) > MAX_COMMAND_JSON_CHARS:
        raise ProviderIdentityRunnerError("daemon command must be bounded JSON argv")
    try:
        value = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ProviderIdentityRunnerError("daemon command must be valid JSON argv") from exc
    if (
        not isinstance(value, list)
        or not value
        or len(value) > MAX_ARGV_ITEMS
        or any(
            not isinstance(item, str) or not item or len(item) > MAX_ARG_CHARS or "\x00" in item
            for item in value
        )
    ):
        raise ProviderIdentityRunnerError("daemon command must be bounded JSON argv")
    return value


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    """功能：解析静态与动态 Provider 身份门禁参数。

    输入：可选 argv；None 时读取当前进程 argv。
    输出：含 fixture/manifest/catalog/Schema/daemon/timeout 的命名空间。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, default=DEFAULT_FIXTURE)
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--catalog", type=Path, default=DEFAULT_CATALOG)
    parser.add_argument("--schema-root", type=Path)
    parser.add_argument("--daemon-command-json")
    parser.add_argument("--timeout", type=float, default=15.0)
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    """功能：运行严格静态 gate，并可选择执行全部黑盒 daemon 案例。

    输入：CLI argv。
    输出：成功为 0，任何脱敏门禁失败为 1。
    不变量：不修改 capability matrix、manifest、catalog 或 golden fixture。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        args = parse_args(argv)
        if args.timeout <= 0 or args.timeout > 120:
            raise ProviderIdentityRunnerError("timeout must be within (0, 120] seconds")
        fixture = load_json(args.fixture)
        manifest = load_json(args.manifest)
        catalog = load_json(args.catalog)
        route_index, provider_index, catalog_index = validate_static_suite(
            fixture,
            manifest,
            catalog,
            schema_root=args.schema_root,
        )
        cases = [require_object(item, "fixture case") for item in fixture["cases"]]
        if args.daemon_command_json is not None:
            daemon_argv = parse_daemon_command(args.daemon_command_json)
            for case in cases:
                run_positive_case(
                    daemon_argv,
                    case,
                    manifest,
                    route_index,
                    provider_index,
                    catalog_index,
                    args.schema_root,
                    args.timeout,
                )
            for negative in fixture["negativeCases"]:
                run_negative_case(
                    daemon_argv,
                    require_object(negative, "negative case"),
                    manifest,
                    args.schema_root,
                    args.timeout,
                )
        mode = "static+black-box" if args.daemon_command_json is not None else "static"
        print(
            f"provider identity {mode} gate passed: "
            f"{len(provider_index)} providers / {len(route_index)} routes / "
            f"{sum(len(items) for items in catalog_index.values())} models / "
            f"{len(cases)} cases"
        )
        return 0
    except ProviderIdentityRunnerError as exc:
        print(f"provider identity gate failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
