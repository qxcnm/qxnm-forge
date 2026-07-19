"""冻结 Provider manifest 与模型目录的共享语义门禁。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

from collections import Counter
import hashlib
import json
import re
from typing import Any
from urllib.parse import urlsplit


EXPECTED_PROVIDER_IDS = {
    "amazon-bedrock",
    "ant-ling",
    "anthropic",
    "azure-openai-responses",
    "cerebras",
    "cloudflare-ai-gateway",
    "cloudflare-workers-ai",
    "deepseek",
    "fireworks",
    "github-copilot",
    "google",
    "google-vertex",
    "groq",
    "huggingface",
    "kimi-coding",
    "minimax",
    "minimax-cn",
    "mistral",
    "moonshotai",
    "moonshotai-cn",
    "nvidia",
    "openai",
    "openai-codex",
    "opencode",
    "opencode-go",
    "openrouter",
    "together",
    "vercel-ai-gateway",
    "xai",
    "xiaomi",
    "xiaomi-token-plan-ams",
    "xiaomi-token-plan-cn",
    "xiaomi-token-plan-sgp",
    "zai",
    "zai-coding-cn",
}
ALLOWED_TEMPLATE_VARIABLES = {
    "CLOUDFLARE_ACCOUNT_ID",
    "CLOUDFLARE_GATEWAY_ID",
    "location",
}
ENVIRONMENT_NAME = re.compile(r"^[A-Z][A-Z0-9_]{0,127}$")
TEMPLATE_VARIABLE = re.compile(r"\{([A-Za-z][A-Za-z0-9_]*)\}")


class DataValidationError(Exception):
    """冻结共享数据出现身份、引用、来源或摘要漂移。"""


def _canonical_sha256(value: Any) -> str:
    """功能：计算 UTF-8、排序 key、无空白 canonical JSON 的 SHA-256。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    encoded = json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def _require_unique(values: list[str], label: str) -> set[str]:
    """功能：要求字符串列表无重复并返回集合。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    result = set(values)
    if len(result) != len(values):
        raise DataValidationError(f"{label} contains a duplicate identity")
    return result


def _validate_https_url(value: str, label: str) -> None:
    """功能：要求冻结 endpoint 使用无 userinfo 的绝对 HTTPS URL。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parsed = urlsplit(value)
    if (
        parsed.scheme != "https"
        or not parsed.hostname
        or parsed.username is not None
        or parsed.password is not None
        or parsed.fragment
    ):
        raise DataValidationError(f"{label} must be a safe HTTPS URL")


def validate_model_catalog_semantics(catalog: dict[str, Any]) -> None:
    """功能：验证冻结模型身份、endpoint、来源计数与 canonical records 摘要。

    输入：已通过 JSON Schema 的 `models.v1.json` 对象。
    输出：全部语义闭合时返回 None。
    不变量：1,041 个文本模型、35 个图像模型及来源摘要不可静默漂移。
    失败：重复身份、HTTP、未知模板、计数或摘要变化抛 DataValidationError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        models = catalog["models"]
        source = catalog["source"]
    except (KeyError, TypeError) as exc:
        raise DataValidationError("model catalog shape is invalid") from exc
    identities = [
        f"{model['media']}\0{model['providerId']}\0{model['apiFamily']}\0{model['modelId']}"
        for model in models
    ]
    _require_unique(identities, "model catalog")

    text_counts: Counter[str] = Counter()
    image_counts: Counter[str] = Counter()
    for model in models:
        media = model["media"]
        provider_id = model["providerId"]
        if media == "text":
            text_counts[provider_id] += 1
        elif media == "image":
            image_counts[provider_id] += 1
        else:
            raise DataValidationError("model media is invalid")

        endpoint = model["endpoint"]
        strategy = endpoint["strategy"]
        base_url = endpoint.get("baseUrl")
        variables = endpoint.get("templateVariables", [])
        if strategy in {"fixed", "template"}:
            if not isinstance(base_url, str):
                raise DataValidationError("model endpoint is missing")
            _validate_https_url(base_url, "model endpoint")
        if strategy == "template":
            declared = set(variables)
            observed = set(TEMPLATE_VARIABLE.findall(base_url))
            if (
                len(declared) != len(variables)
                or not declared
                or not declared <= ALLOWED_TEMPLATE_VARIABLES
                or declared != observed
            ):
                raise DataValidationError(
                    "model endpoint template variables are invalid"
                )
        elif variables:
            raise DataValidationError("non-template endpoint declares variables")

    observed = source["observedCounts"]
    if (
        observed
        != {
            "providers": 35,
            "textModels": 1041,
            "imageProviders": 1,
            "imageModels": 35,
        }
        or len(text_counts) != 35
        or sum(text_counts.values()) != 1041
        or len(image_counts) != 1
        or sum(image_counts.values()) != 35
        or dict(text_counts) != source["textModelsByProvider"]
        or dict(image_counts) != source["imageModelsByProvider"]
    ):
        raise DataValidationError("model catalog observed counts drifted")
    if source.get(
        "recordsDigestAlgorithm"
    ) != "sha256-canonical-json-v1" or _canonical_sha256(models) != source.get(
        "recordsSha256"
    ):
        raise DataValidationError("model catalog records digest drifted")


def validate_provider_manifest_semantics(
    manifest: dict[str, Any],
    *,
    catalog: dict[str, Any] | None = None,
) -> None:
    """功能：验证 Provider 身份、环境、route 引用、模板绑定与 manifest 摘要。

    输入：已通过 Schema 的 manifest；可选冻结模型 catalog 用于跨文件引用。
    输出：35 个 Provider 和全部 route 引用闭合时返回 None。
    不变量：身份集合、adapter/family、auth/header/catalog 引用和摘要不可静默改变。
    失败：重复/替换身份、非法环境、悬空引用、缺模板绑定或摘要漂移时抛错。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        providers = manifest["providers"]
        adapters = manifest["adapters"]
        header_policies = manifest["headerPolicies"]
        model_catalogs = manifest["modelCatalogs"]
    except (KeyError, TypeError) as exc:
        raise DataValidationError("provider manifest shape is invalid") from exc

    provider_ids = _require_unique(
        [provider["id"] for provider in providers], "provider manifest"
    )
    if provider_ids != EXPECTED_PROVIDER_IDS:
        raise DataValidationError("frozen provider identity set drifted")
    adapter_ids = _require_unique(
        [adapter["id"] for adapter in adapters], "provider adapters"
    )
    adapter_families = {adapter["id"]: adapter["apiFamily"] for adapter in adapters}
    header_ids = _require_unique(
        [policy["id"] for policy in header_policies], "header policies"
    )
    catalog_ids = _require_unique(
        [item["id"] for item in model_catalogs], "model catalogs"
    )
    if catalog is not None and catalog.get("catalogId") not in catalog_ids:
        raise DataValidationError("model catalog identity is not referenced")

    catalog_variables: dict[tuple[str, str], set[str]] = {}
    if catalog is not None:
        for model in catalog["models"]:
            key = (model["providerId"], model["apiFamily"])
            catalog_variables.setdefault(key, set()).update(
                model["endpoint"].get("templateVariables", [])
            )

    route_ids: list[str] = []
    for provider in providers:
        environment = provider["environment"]
        environment_names = _require_unique(
            [entry["name"] for entry in environment],
            f"provider {provider['id']} environment",
        )
        if any(not ENVIRONMENT_NAME.fullmatch(name) for name in environment_names):
            raise DataValidationError("provider environment name is invalid")
        auth_profiles = provider["authProfiles"]
        auth_ids = _require_unique(
            [profile["id"] for profile in auth_profiles],
            f"provider {provider['id']} auth profiles",
        )
        for profile in auth_profiles:
            if not set(profile["environment"]) <= environment_names:
                raise DataValidationError("auth profile references unknown environment")

        for route in provider["routes"]:
            api_family = route["apiFamily"]
            route_ids.append(f"{provider['id']}\0{api_family}")
            if (
                route["adapterId"] not in adapter_ids
                or adapter_families[route["adapterId"]] != api_family
                or route["modelCatalogId"] not in catalog_ids
                or route["headerPolicyId"] not in header_ids
                or not set(route["authProfileIds"]) <= auth_ids
                or not set(route["endpoint"]["runtimeEndpointEnv"]) <= environment_names
            ):
                raise DataValidationError(
                    "provider route contains a dangling reference"
                )
            bindings = route["endpoint"]["templateBindings"]
            binding_variables = _require_unique(
                [binding["variable"] for binding in bindings],
                "route template bindings",
            )
            if any(
                not set(binding["environment"]) <= environment_names
                for binding in bindings
            ):
                raise DataValidationError(
                    "template binding references unknown environment"
                )
            expected_variables = catalog_variables.get(
                (provider["id"], api_family), set()
            )
            if catalog is not None and binding_variables != expected_variables:
                raise DataValidationError(
                    "route template bindings do not match catalog"
                )
    _require_unique(route_ids, "provider routes")

    digest_input = {
        key: value
        for key, value in manifest.items()
        if key not in {"manifestDigestAlgorithm", "manifestSha256"}
    }
    if manifest.get(
        "manifestDigestAlgorithm"
    ) != "sha256-canonical-json-excluding-manifest-digest-v1" or _canonical_sha256(
        digest_input
    ) != manifest.get("manifestSha256"):
        raise DataValidationError("provider manifest digest drifted")
