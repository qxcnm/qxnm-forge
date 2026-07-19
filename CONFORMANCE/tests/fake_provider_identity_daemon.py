#!/usr/bin/env python3
"""仅用于验证 Provider 身份广告 runner 的确定性、无网络假 daemon。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from collections import defaultdict
from pathlib import Path
from typing import Any

JSONObject = dict[str, Any]
RouteKey = tuple[str, str]
ROOT = Path(__file__).resolve().parents[2]
CONFIG_ENV = "AGENT_PROVIDER_IDENTITY_CONFORMANCE_CONFIG"
CANARY_ENV = "AGENT_PROVIDER_IDENTITY_CREDENTIAL_CANARY"
CONFIG_KEYS = {
    "schemaVersion",
    "implementedAdapterIds",
    "capabilityAllowances",
    "usableAuthProfiles",
    "configuredEnvironmentNames",
}
CAPABILITY_FEATURES = {
    "authentication",
    "text",
    "streaming",
    "tools",
    "reasoning",
    "image_input",
    "image_output",
}


class StrictInputError(Exception):
    """严格 JSON、presence 或请求形状无效。"""


def reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> JSONObject:
    """功能：在假 daemon 的所有 JSON 输入层级拒绝重复键。

    输入：decoder 提供的有序键值对。
    输出：无重复键字典。
    失败：发现重复键时抛 StrictInputError，且不回显值。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    result: JSONObject = {}
    for key, value in pairs:
        if key in result:
            raise StrictInputError("duplicate key")
        result[key] = value
    return result


def strict_json(raw: bytes) -> JSONObject:
    """功能：严格解析一个 UTF-8 JSON object。

    输入：单个完整 JSON value bytes。
    输出：顶层 object。
    失败：编码、语法、重复键或顶层类型无效时抛 StrictInputError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        value = json.loads(
            raw.decode("utf-8", errors="strict"),
            object_pairs_hook=reject_duplicate_keys,
        )
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise StrictInputError("invalid JSON") from exc
    if not isinstance(value, dict):
        raise StrictInputError("top level must be object")
    return value


def load_object(path: Path, max_bytes: int) -> JSONObject:
    """功能：有界读取 manifest、catalog 或临时 presence object。

    输入：普通文件路径与最大 bytes。
    输出：严格 JSON object。
    失败：缺失、过大、读取或 JSON 无效时抛 StrictInputError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        if not path.is_file() or path.stat().st_size <= 0 or path.stat().st_size > max_bytes:
            raise StrictInputError("configuration file is invalid")
        return strict_json(path.read_bytes())
    except OSError as exc:
        raise StrictInputError("configuration file is unreadable") from exc


def require_list(value: Any) -> list[Any]:
    """功能：把 presence/manifest 动态值收窄为 list。

    输入：任意 JSON 值。
    输出：原 list。
    失败：类型不符时抛 StrictInputError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, list):
        raise StrictInputError("array required")
    return value


def require_object(value: Any) -> JSONObject:
    """功能：把 presence/manifest 动态值收窄为 object。

    输入：任意 JSON 值。
    输出：原 object。
    失败：类型不符时抛 StrictInputError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, dict):
        raise StrictInputError("object required")
    return value


def unique_strings(value: Any) -> list[str]:
    """功能：验证一个数组只含唯一非空字符串。

    输入：presence 配置数组。
    输出：保持输入顺序的 string list。
    失败：类型、空值或重复时抛 StrictInputError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    values = require_list(value)
    if any(not isinstance(item, str) or not item for item in values):
        raise StrictInputError("string array is invalid")
    if len(set(values)) != len(values):
        raise StrictInputError("string array is duplicated")
    return values


def manifest_indexes(
    manifest: JSONObject,
) -> tuple[dict[RouteKey, JSONObject], dict[str, JSONObject]]:
    """功能：独立建立假 daemon 使用的 Provider/route 索引。

    输入：canonical manifest。
    输出：route 和 Provider 索引。
    不变量：不导入 runner 代码，保持黑盒期望与实现相互独立。
    失败：重复或非法身份时抛 StrictInputError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    route_index: dict[RouteKey, JSONObject] = {}
    provider_index: dict[str, JSONObject] = {}
    for raw_provider in require_list(manifest.get("providers")):
        provider = require_object(raw_provider)
        provider_id = provider.get("id")
        if not isinstance(provider_id, str) or provider_id in provider_index:
            raise StrictInputError("Provider identity is invalid")
        provider_index[provider_id] = provider
        for raw_route in require_list(provider.get("routes")):
            route = require_object(raw_route)
            family = route.get("apiFamily")
            if not isinstance(family, str) or (provider_id, family) in route_index:
                raise StrictInputError("route identity is invalid")
            route_index[(provider_id, family)] = route
    return route_index, provider_index


def catalog_index(
    catalog: JSONObject, route_index: dict[RouteKey, JSONObject]
) -> dict[RouteKey, list[JSONObject]]:
    """功能：独立按 route 分组冻结模型并拒绝悬空/重复三元身份。

    输入：catalog 和 manifest route 索引。
    输出：route-keyed、按 modelId 排序的模型行。
    失败：route 不存在或三元身份重复时抛 StrictInputError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    grouped: dict[RouteKey, list[JSONObject]] = defaultdict(list)
    seen: set[tuple[str, str, str]] = set()
    for raw_model in require_list(catalog.get("models")):
        model = require_object(raw_model)
        provider_id = model.get("providerId")
        model_id = model.get("modelId")
        family = model.get("apiFamily")
        if not all(isinstance(item, str) for item in (provider_id, model_id, family)):
            raise StrictInputError("model identity is invalid")
        route_key = (str(provider_id), str(family))
        model_key = (str(provider_id), str(model_id), str(family))
        if route_key not in route_index or model_key in seen:
            raise StrictInputError("model route is invalid")
        seen.add(model_key)
        grouped[route_key].append(model)
    for models in grouped.values():
        models.sort(key=lambda model: str(model["modelId"]))
    return dict(grouped)


def empty_config() -> JSONObject:
    """功能：构造默认拒绝全部 live route 的 presence 配置。

    输出：所有 presence 集合为空的新 object。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "schemaVersion": "0.1",
        "implementedAdapterIds": [],
        "capabilityAllowances": [],
        "usableAuthProfiles": [],
        "configuredEnvironmentNames": [],
    }


def validate_config(
    config: JSONObject,
    route_index: dict[RouteKey, JSONObject],
    provider_index: dict[str, JSONObject],
) -> JSONObject:
    """功能：严格验证无值 presence 配置的字段、唯一性和 manifest 引用。

    输入：临时 config 及 canonical 索引。
    输出：原 config。
    不变量：只接受固定五个字段，绝不接受 credential/endpoint value。
    失败：未知字段、重复项或悬空引用时抛 StrictInputError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if set(config) != CONFIG_KEYS or config.get("schemaVersion") != "0.1":
        raise StrictInputError("presence fields are invalid")
    adapter_ids = unique_strings(config.get("implementedAdapterIds"))
    manifest_adapter_ids = {str(context["adapterId"]) for context in route_index.values()}
    if not set(adapter_ids).issubset(manifest_adapter_ids):
        raise StrictInputError("adapter presence is unknown")
    unique_strings(config.get("configuredEnvironmentNames"))
    manifest_environment = {
        str(entry["name"])
        for provider in provider_index.values()
        for entry in provider["environment"]
    }
    if not set(config["configuredEnvironmentNames"]).issubset(manifest_environment):
        raise StrictInputError("configuration presence is unknown")

    allowance_keys: set[RouteKey] = set()
    for raw_allowance in require_list(config.get("capabilityAllowances")):
        allowance = require_object(raw_allowance)
        if set(allowance) != {"providerId", "apiFamily", "features"}:
            raise StrictInputError("capability allowance fields are invalid")
        key = (allowance.get("providerId"), allowance.get("apiFamily"))
        if not all(isinstance(item, str) for item in key):
            raise StrictInputError("capability allowance identity is invalid")
        typed_key = (str(key[0]), str(key[1]))
        features = unique_strings(allowance.get("features"))
        if typed_key not in route_index or typed_key in allowance_keys:
            raise StrictInputError("capability allowance route is invalid")
        if not set(features).issubset(CAPABILITY_FEATURES):
            raise StrictInputError("capability feature is unknown")
        allowance_keys.add(typed_key)

    auth_keys: set[tuple[str, str]] = set()
    for raw_auth in require_list(config.get("usableAuthProfiles")):
        auth = require_object(raw_auth)
        if set(auth) != {"providerId", "authProfileId"}:
            raise StrictInputError("auth presence fields are invalid")
        provider_id = auth.get("providerId")
        profile_id = auth.get("authProfileId")
        if not isinstance(provider_id, str) or not isinstance(profile_id, str):
            raise StrictInputError("auth presence identity is invalid")
        provider = provider_index.get(provider_id)
        available_ids = (
            {str(item["id"]) for item in provider["authProfiles"]}
            if provider is not None
            else set()
        )
        auth_key = (provider_id, profile_id)
        if profile_id not in available_ids or auth_key in auth_keys:
            raise StrictInputError("auth presence reference is invalid")
        auth_keys.add(auth_key)
    return config


def load_config(
    route_index: dict[RouteKey, JSONObject], provider_index: dict[str, JSONObject]
) -> JSONObject:
    """功能：加载可选临时 presence 文件，缺省时安全拒绝全部 live route。

    输入：canonical route/Provider 索引。
    输出：严格验证的 config。
    不变量：环境变量只承载临时文件路径，不承载配置或 credential 值。
    失败：文件或字段无效时抛 StrictInputError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    raw_path = os.environ.get(CONFIG_ENV)
    if raw_path is None:
        return empty_config()
    return validate_config(load_object(Path(raw_path), 1024 * 1024), route_index, provider_index)


def allowance_index(config: JSONObject) -> dict[RouteKey, set[str]]:
    """功能：将严格 capability presence 转换为 route feature 索引。

    输入：已验证 config。
    输出：route-keyed feature sets。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        (str(item["providerId"]), str(item["apiFamily"])): set(item["features"])
        for item in config["capabilityAllowances"]
    }


def route_is_usable(
    key: RouteKey,
    route: JSONObject,
    models: list[JSONObject],
    config: JSONObject,
) -> bool:
    """功能：独立执行 route 的五项 presence 交集判定。

    输入：route key、manifest route、catalog 行和 config。
    输出：该 route 是否应广告。
    不变量：不读取任何 credential 值，不向外输出 presence 内部字段。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if route["adapterId"] not in config["implementedAdapterIds"]:
        return False
    features = allowance_index(config).get(key, set())
    if "authentication" not in features:
        return False
    if route["media"] == "text" and "text" not in features:
        return False
    if route["media"] == "image" and (
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
    if any(model["endpoint"]["strategy"] == "runtime-required" for model in models):
        if not runtime_names or not configured.intersection(runtime_names):
            return False
    return True


def normalized_model(model: JSONObject, features: set[str]) -> JSONObject:
    """功能：独立把 catalog row 归一化为 capability-filtered 公共 model DTO。

    输入：catalog row 和获准 features。
    输出：无 Provider 内部字段的协议描述。
    不变量：能力仅取 catalog 证据与 allowance 的交集。
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
    if isinstance(model.get("limits"), dict):
        capabilities["contextTokens"] = model["limits"]["contextWindow"]
        capabilities["maxOutputTokens"] = model["limits"]["maxOutputTokens"]
    return {
        "providerId": model["providerId"],
        "modelId": model["modelId"],
        "displayName": model["name"],
        "apiFamily": model["apiFamily"],
        "capabilities": capabilities,
    }


def model_key(model: JSONObject) -> tuple[str, str, str]:
    """功能：提取公共 model DTO 的三元稳定排序键。

    输入：model DTO。
    输出：Provider/model/family tuple。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return (str(model["providerId"]), str(model["modelId"]), str(model["apiFamily"]))


def faux_model() -> JSONObject:
    """功能：构造固定 faux 模型描述供 conformance handshake 使用。

    输出：新公共 model DTO。
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


def advertised_models(
    route_index: dict[RouteKey, JSONObject],
    models_by_route: dict[RouteKey, list[JSONObject]],
    config: JSONObject,
    *,
    drop_first_route: bool,
) -> list[JSONObject]:
    """功能：计算并归一化当前假 daemon 的 live 模型快照。

    输入：manifest/catalog/config 及测试专用丢路由开关。
    输出：按三元身份排序的 live descriptors。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    routes = [
        key
        for key in sorted(route_index)
        if route_is_usable(key, route_index[key], models_by_route[key], config)
    ]
    if drop_first_route and routes:
        routes = routes[1:]
    allowances = allowance_index(config)
    models: list[JSONObject] = []
    for key in routes:
        for model in models_by_route[key]:
            descriptor = normalized_model(model, allowances[key])
            capabilities = descriptor["capabilities"]
            if capabilities["input"] and capabilities["output"]:
                models.append(descriptor)
    models.sort(key=model_key)
    return models


def provider_capabilities(models: list[JSONObject], *, inject_unknown: bool) -> list[JSONObject]:
    """功能：从同一模型快照派生 initialize Provider/model 并集。

    输入：live models 和测试专用未知 Provider 开关。
    输出：含 faux、按 Provider ID 排序的 capability 数组。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    grouped: dict[str, set[str]] = defaultdict(set)
    grouped["faux"].add("faux-v1")
    for model in models:
        grouped[str(model["providerId"])].add(str(model["modelId"]))
    if inject_unknown:
        grouped["manifest-unknown"] = set()
    return [
        {"id": provider_id, "models": sorted(model_ids)}
        for provider_id, model_ids in sorted(grouped.items())
    ]


def emit(value: JSONObject) -> None:
    """功能：把一个完整 JSON-RPC object 写为紧凑 UTF-8 NDJSON。

    输入：server frame。
    输出：写 stdout 并立即 flush。
    不变量：stdout 不写日志或 presence 值。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    sys.stdout.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def error_response(
    response_id: str | int | None,
    code: int,
    message: str,
    kind: str,
) -> JSONObject:
    """功能：构造不含原始输入的 portable JSON-RPC error response。

    输入：可空 request ID、错误码、安全消息和结构化 kind。
    输出：完整 error frame。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "jsonrpc": "2.0",
        "id": response_id,
        "error": {
            "code": code,
            "message": message,
            "retryable": False,
            "details": {"kind": kind},
        },
    }


def initialize_result(models: list[JSONObject], *, inject_unknown: bool) -> JSONObject:
    """功能：构造只广告 initialize/models-list 和实际 Provider 并集的结果。

    输入：当前 live 模型快照与测试专用未知路由开关。
    输出：符合 protocol 0.1 Schema 的 initialize result。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "protocolVersion": "0.1",
        "implementation": {
            "name": "provider-identity-conformance",
            "version": "0.1.0+test",
            "language": "python",
        },
        "capabilities": {
            "methods": ["initialize", "models/list"],
            "eventTypes": [
                "run.completed",
                "run.failed",
                "run.cancelled",
                "run.interrupted",
            ],
            "providers": provider_capabilities(models, inject_unknown=inject_unknown),
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


def handle_request(
    request: JSONObject,
    initialized: bool,
    models: list[JSONObject],
    *,
    inject_unknown: bool,
) -> tuple[JSONObject, bool]:
    """功能：严格处理 initialize 与既有 models/list 两个 RPC。

    输入：request、连接状态、live 模型快照和测试开关。
    输出：response 及更新后的 initialized 状态。
    失败：生命周期、方法或参数错误以 portable response 返回。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    response_id = request.get("id")
    if request.get("jsonrpc") != "2.0" or not isinstance(response_id, str | int):
        return error_response(
            response_id, -32600, "Invalid request.", "invalid_request"
        ), initialized
    method = request.get("method")
    params = request.get("params")
    if not isinstance(params, dict):
        return error_response(
            response_id, -32602, "Invalid parameters.", "invalid_params"
        ), initialized
    if not initialized and method != "initialize":
        return error_response(
            response_id, -32600, "Initialize first.", "lifecycle_violation"
        ), initialized
    if method == "initialize":
        if initialized or set(params) != {"protocolVersions", "client", "capabilities"}:
            return error_response(
                response_id, -32600, "Invalid initialize.", "invalid_request"
            ), initialized
        if "0.1" not in params.get("protocolVersions", []):
            return error_response(
                response_id, -32001, "No compatible protocol.", "incompatible_protocol"
            ), initialized
        return {
            "jsonrpc": "2.0",
            "id": response_id,
            "result": initialize_result(models, inject_unknown=inject_unknown),
        }, True
    if method == "models/list":
        if not set(params).issubset({"providerId"}):
            return error_response(
                response_id, -32602, "Invalid parameters.", "invalid_params"
            ), initialized
        provider_id = params.get("providerId")
        if provider_id is not None and (
            not isinstance(provider_id, str) or not provider_id or len(provider_id) > 128
        ):
            return error_response(
                response_id, -32602, "Invalid parameters.", "invalid_params"
            ), initialized
        available = [faux_model(), *models]
        if provider_id is not None:
            available = [model for model in available if model["providerId"] == provider_id]
        available.sort(key=model_key)
        return {"jsonrpc": "2.0", "id": response_id, "result": {"models": available}}, initialized
    return error_response(response_id, -32601, "Method not found.", "method_not_found"), initialized


def parse_args() -> argparse.Namespace:
    """功能：解析 runner 自测使用的广告、输出、timeout 和后代开关。

    输出：仅供专项负测使用的严格 argparse namespace。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser()
    parser.add_argument("--leak-canary", action="store_true")
    parser.add_argument("--advertise-unknown-route", action="store_true")
    parser.add_argument("--drop-first-route", action="store_true")
    parser.add_argument("--flood-output-bytes", type=int, default=0)
    parser.add_argument("--hang", action="store_true")
    parser.add_argument("--spawn-descendant-marker", type=Path)
    parser.add_argument("--descendant-write", type=Path)
    return parser.parse_args()


def flood_output(byte_count: int) -> None:
    """功能：为 runner 输出上限负测持续写 stdout，直到给定字节数或 pipe 关闭。

    输入：非负目标 bytes。
    输出：不含 credential 的固定 `x` bytes；无 JSON 语义。
    不变量：每次最多 64 KiB，pipe 被监督器关闭后立即停止。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    remaining = max(0, byte_count)
    chunk = b"x" * (64 * 1024)
    while remaining:
        try:
            written = os.write(sys.stdout.fileno(), chunk[:remaining])
        except (BrokenPipeError, OSError):
            return
        remaining -= written


def spawn_marker_descendant(marker: Path) -> None:
    """功能：生成继承当前 POSIX 进程组的固定 Python 后代供清理负测。

    输入：runner 临时目录内的 marker 路径。
    输出：启动后立即返回，不等待后代。
    不变量：使用 argv、DEVNULL 和 close_fds，不经过 shell 或继承协议 pipe。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    subprocess.Popen(
        [sys.executable, str(Path(__file__).resolve()), "--descendant-write", str(marker)],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        close_fds=True,
    )


def write_delayed_marker(marker: Path) -> int:
    """功能：延迟写入固定 marker，用于证明 timeout 是否遗留后代。

    输入：测试临时目录内路径。
    输出：成功写入返回 0；父进程组被正确清理时本函数不会执行到写入。
    不变量：只写固定文本，不读取环境、网络或仓库文件。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    time.sleep(0.8)
    marker.write_text("descendant-survived", encoding="utf-8")
    return 0


def hang_forever() -> None:
    """功能：保持 fake daemon 存活以触发 runner 总 deadline 清理路径。

    输出：正常情况下不返回，只能由监督器信号终止。
    不变量：不产生输出、不访问网络且每次 sleep 有界。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    while True:
        time.sleep(1.0)


def main() -> int:
    """功能：加载严格 presence 后逐帧服务 initialize/models-list，永不联网。

    输出：正常 EOF 为 0；启动配置无效为 2。
    不变量：除显式负测开关外，credential canary 不进入 stdout/stderr。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = parse_args()
    if args.descendant_write is not None:
        return write_delayed_marker(args.descendant_write)
    if args.flood_output_bytes > 0:
        flood_output(args.flood_output_bytes)
    if args.spawn_descendant_marker is not None:
        spawn_marker_descendant(args.spawn_descendant_marker)
    if args.hang:
        hang_forever()
    try:
        manifest = load_object(ROOT / "SPEC/providers.v1.json", 8 * 1024 * 1024)
        catalog = load_object(ROOT / "SPEC/models.v1.json", 64 * 1024 * 1024)
        routes, providers = manifest_indexes(manifest)
        models_by_route = catalog_index(catalog, routes)
        config = load_config(routes, providers)
        models = advertised_models(
            routes,
            models_by_route,
            config,
            drop_first_route=args.drop_first_route,
        )
    except StrictInputError:
        print("provider identity configuration rejected", file=sys.stderr, flush=True)
        return 2
    if args.leak_canary:
        print(os.environ.get(CANARY_ENV, "missing-canary"), file=sys.stderr, flush=True)
    initialized = False
    for raw_line in sys.stdin.buffer:
        try:
            request = strict_json(raw_line)
        except StrictInputError:
            emit(error_response(None, -32700, "Parse error.", "parse_error"))
            continue
        response, initialized = handle_request(
            request,
            initialized,
            models,
            inject_unknown=args.advertise_unknown_route,
        )
        emit(response)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
