//! Manifest 驱动的 canonical Provider 可执行 Route Spine。
//!
//! 作者：高宏顺 <18272669457@163.com>

use std::collections::{BTreeMap, BTreeSet};
use std::path::Path;
use std::sync::Arc;

use async_trait::async_trait;
use serde::Deserialize;
use serde_json::{Value, json};
use tokio_util::sync::CancellationToken;

use crate::error::{AgentError, ErrorCode};
use crate::provider::{
    AnthropicMessagesProvider, GoogleGenerativeAiProvider, MistralConversationsProvider,
    OpenAiChatProvider, OpenAiResponsesProvider, OpenRouterImagesProvider, Provider,
    ProviderRequest, ProviderStream, credential_is_present, native_endpoint,
};
use crate::provider_identity::{
    AdvertisedModel, PROVIDER_IDENTITY_CONFIG_ENV, StrictInputError, default_models,
    load_frozen_indexes, load_json_object, model_key, normalized_model,
};
use crate::session::SessionStore;

pub const PROVIDER_ROUTE_CONFIG_ENV: &str = "AGENT_PROVIDER_ROUTE_CONFORMANCE_CONFIG";

const ROUTE_CONFIGURATION_ERROR: &str = "Provider route configuration is invalid.";
const MAX_ROUTE_CONFIG_BYTES: u64 = 1024 * 1024;

#[derive(Debug, Clone, Copy)]
struct RouteSpec {
    provider_id: &'static str,
    api_family: &'static str,
    adapter_id: &'static str,
    credential_env: &'static str,
    endpoint_base: &'static str,
    media: &'static str,
    model_count: usize,
}

const ROUTE_SPECS: [RouteSpec; 6] = [
    RouteSpec {
        provider_id: "groq",
        api_family: "openai-completions",
        adapter_id: "openai-completions-v1",
        credential_env: "GROQ_API_KEY",
        endpoint_base: "https://api.groq.com/openai/v1",
        media: "text",
        model_count: 7,
    },
    RouteSpec {
        provider_id: "minimax",
        api_family: "anthropic-messages",
        adapter_id: "anthropic-messages-v1",
        credential_env: "MINIMAX_API_KEY",
        endpoint_base: "https://api.minimax.io/anthropic",
        media: "text",
        model_count: 3,
    },
    RouteSpec {
        provider_id: "mistral",
        api_family: "mistral-conversations",
        adapter_id: "mistral-conversations-v1",
        credential_env: "MISTRAL_API_KEY",
        endpoint_base: "https://api.mistral.ai",
        media: "text",
        model_count: 30,
    },
    RouteSpec {
        provider_id: "openai",
        api_family: "openai-responses",
        adapter_id: "openai-responses-v1",
        credential_env: "OPENAI_API_KEY",
        endpoint_base: "https://api.openai.com/v1",
        media: "text",
        model_count: 42,
    },
    RouteSpec {
        provider_id: "google",
        api_family: "google-generative-ai",
        adapter_id: "google-generative-ai-v1",
        credential_env: "GEMINI_API_KEY",
        endpoint_base: "https://generativelanguage.googleapis.com/v1beta",
        media: "text",
        model_count: 16,
    },
    RouteSpec {
        provider_id: "openrouter",
        api_family: "openrouter-images",
        adapter_id: "openrouter-images-v1",
        credential_env: "OPENROUTER_API_KEY",
        endpoint_base: "https://openrouter.ai/api/v1",
        media: "image",
        model_count: 35,
    },
];

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RouteConfig {
    schema_version: String,
    provider_id: String,
    api_family: String,
    model_id: String,
    endpoint_base: String,
}

#[derive(Debug, Clone)]
struct ExecutableRoute {
    provider_id: String,
    api_family: String,
    adapter_id: String,
    credential_env: String,
    endpoint_base: String,
    model_ids: BTreeSet<String>,
}

/// daemon 启动期生成、同时约束广告与执行的不可变 canonical route 快照。
#[derive(Debug, Clone)]
pub struct ProviderRouteSnapshot {
    routes: Vec<ExecutableRoute>,
    models: Vec<AdvertisedModel>,
    allow_legacy_fallback: bool,
}

impl ProviderRouteSnapshot {
    /// 功能：最早处理 Route Spine conformance presence，并在双门控下构造收窄快照。
    ///
    /// 输入：一般 conformance 与 Provider conformance 两个由宿主显式判定的布尔门。
    /// 输出：presence 不存在时为 `None`；存在且严格有效时为单 route、单模型快照。
    /// 不变量：生产 presence、与 identity presence 冲突或任一门缺失时不读取配置文件、
    /// credential、transport 或 CLI；文件仅 no-follow、有界、strict JSON 读取。
    /// 失败：门控、路径、文件、JSON、loopback endpoint 或 manifest/catalog 引用无效时返回固定脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn from_environment(
        conformance: bool,
        provider_conformance: bool,
    ) -> Result<Option<Self>, AgentError> {
        let Some(path) = std::env::var_os(PROVIDER_ROUTE_CONFIG_ENV) else {
            return Ok(None);
        };
        if !conformance
            || !provider_conformance
            || std::env::var_os(PROVIDER_IDENTITY_CONFIG_ENV).is_some()
        {
            return Err(route_configuration_error());
        }
        let value = load_json_object(Path::new(&path), MAX_ROUTE_CONFIG_BYTES)
            .map_err(|_| route_configuration_error())?;
        let config: RouteConfig =
            serde_json::from_value(value).map_err(|_| route_configuration_error())?;
        validate_route_config(&config).map_err(|_| route_configuration_error())?;
        build_snapshot(Some(config), credential_is_present)
            .map(Some)
            .map_err(|_| route_configuration_error())
    }

    /// 功能：从冻结 manifest/catalog 与当前 credential presence 构造普通生产快照。
    ///
    /// 输出：只含六条 allowlist 中启动时 credential 非空 route 的 descriptor 与执行计划。
    /// 不变量：endpoint、adapter、auth、header、quirk 与 model allowlist 全部由冻结数据验证；
    /// 不读取任何 endpoint override，也不把 credential 值保存到快照。
    /// 失败：shared snapshot 或任一 allowlist 不变量漂移时返回固定脱敏启动错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn production() -> Result<Self, AgentError> {
        build_snapshot(None, production_credential_available)
            .map_err(|_| route_configuration_error())
    }

    /// 功能：按可选 Provider ID返回与执行计划同源的排序模型快照。
    ///
    /// 输入：缺省表示全量，字符串只与 canonical Provider ID 精确比较。
    /// 输出：全新 descriptor 列表；全量固定包含 faux，未知 Provider 返回空数组。
    /// 不变量：排序键固定为 `(providerId, modelId, apiFamily)`，不得出现无 adapter 的模型。
    /// 失败：本方法不执行 I/O 且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn models(&self, provider_id: Option<&str>) -> Vec<AdvertisedModel> {
        let mut models = default_models(provider_id);
        models.extend(
            self.models
                .iter()
                .filter(|model| provider_id.is_none_or(|value| model.provider_id == value))
                .cloned(),
        );
        models.sort_by_key(model_key);
        models
    }

    /// 功能：把 Route Spine 模型与旧 family conformance Provider 合并为 initialize 能力并集。
    ///
    /// 输入：Agent 从实际 legacy adapter 生成的可信 capabilities DTO。
    /// 输出：按 Provider ID 排序、modelId 去重的 Provider 列表。
    /// 不变量：canonical modelId 只来自本快照；legacy 项只能贡献其原有 ID/模型字符串。
    /// 失败：畸形 legacy DTO 被安全忽略，本方法不执行外部 I/O。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn merged_provider_capabilities(&self, legacy: Vec<Value>) -> Vec<Value> {
        let mut grouped = BTreeMap::<String, BTreeSet<String>>::new();
        for provider in legacy {
            let Some(provider_id) = provider.get("id").and_then(Value::as_str) else {
                continue;
            };
            let models = grouped.entry(provider_id.to_owned()).or_default();
            if let Some(values) = provider.get("models").and_then(Value::as_array) {
                models.extend(values.iter().filter_map(Value::as_str).map(str::to_owned));
            }
        }
        grouped
            .entry("faux".to_owned())
            .or_default()
            .insert("faux-v1".to_owned());
        for model in &self.models {
            grouped
                .entry(model.provider_id.clone())
                .or_default()
                .insert(model.model_id.clone());
        }
        grouped
            .into_iter()
            .map(|(provider_id, models)| {
                json!({"id":provider_id,"models":models.into_iter().collect::<Vec<_>>()})
            })
            .collect()
    }

    /// 功能：在 Session 创建前把 canonical Provider/model/family 解析到快照中的唯一 route。
    ///
    /// 输入：客户端 Provider ID、modelId 与可选 API family。
    /// 输出：命中时返回实际 family；非 Route Spine legacy ID 返回 `None` 交由既有 registry。
    /// 不变量：只接受快照 model allowlist；省略 family 仅在唯一命中时解析；每次重新检查
    /// credential presence，但绝不读取或保存 credential 值。
    /// 失败：canonical wrong family、unknown/unadvertised model、缺 credential 或零命中返回
    /// `provider_unavailable`；多命中且省略 family 返回既有 `invalid_params` 歧义。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn resolve(
        &self,
        provider_id: &str,
        model_id: &str,
        api_family: Option<&str>,
    ) -> Result<Option<String>, AgentError> {
        let mut matches = self.routes.iter().filter(|route| {
            route.provider_id == provider_id
                && route.model_ids.contains(model_id)
                && api_family.is_none_or(|family| route.api_family == family)
        });
        let first = matches.next();
        if first.is_some() && matches.next().is_some() && api_family.is_none() {
            return Err(AgentError::new(
                ErrorCode::InvalidParams,
                "provider selection requires an explicit API family",
            )
            .with_details(json!({"kind":"invalid_params","field":"provider.apiFamily"})));
        }
        if let Some(route) = first {
            if !credential_is_present(&route.credential_env) {
                return Err(provider_unavailable());
            }
            return Ok(Some(route.api_family.clone()));
        }
        if self.allow_legacy_fallback && is_legacy_family_fallback(provider_id, api_family) {
            return Ok(None);
        }
        if ROUTE_SPECS
            .iter()
            .any(|spec| spec.provider_id == provider_id)
        {
            return Err(provider_unavailable());
        }
        Ok(None)
    }

    /// 功能：从快照 route 计划构造 canonical family adapter registry。
    ///
    /// 输入：当前 daemon 的 Session store，仅图像 adapter 用于重新复核 input artifact。
    /// 输出：以内部 route key 索引、每项带精确 family/model/credential 门的原生 Rust Provider。
    /// 不变量：adapter 只保存 endpoint 与 credential 环境名称；固定原生 path 通过 URL
    /// 解析器追加并保持 scheme/host/effective port；不发起网络或读取 credential 值。
    /// 失败：冻结或 conformance endpoint 无法安全追加时返回脱敏结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn build_providers(
        &self,
        sessions: &SessionStore,
    ) -> Result<BTreeMap<String, Arc<dyn Provider>>, AgentError> {
        let mut providers = BTreeMap::<String, Arc<dyn Provider>>::new();
        for route in &self.routes {
            let credential_env = Some(route.credential_env.clone());
            let inner: Arc<dyn Provider> = match route.adapter_id.as_str() {
                "openai-completions-v1" => Arc::new(OpenAiChatProvider::new(
                    &route.provider_id,
                    native_endpoint(&route.endpoint_base, "/chat/completions", &[])?.to_string(),
                    credential_env,
                )),
                "anthropic-messages-v1" => Arc::new(AnthropicMessagesProvider::new(
                    &route.provider_id,
                    native_endpoint(&route.endpoint_base, "/v1/messages", &[])?.to_string(),
                    credential_env,
                )),
                "mistral-conversations-v1" => Arc::new(MistralConversationsProvider::new(
                    &route.provider_id,
                    &route.endpoint_base,
                    credential_env,
                )),
                "openai-responses-v1" => Arc::new(OpenAiResponsesProvider::new(
                    &route.provider_id,
                    native_endpoint(&route.endpoint_base, "/responses", &[])?.to_string(),
                    credential_env,
                )),
                "google-generative-ai-v1" => Arc::new(GoogleGenerativeAiProvider::new(
                    &route.provider_id,
                    &route.endpoint_base,
                    credential_env,
                )),
                "openrouter-images-v1" => Arc::new(OpenRouterImagesProvider::new(
                    &route.endpoint_base,
                    sessions.clone(),
                )),
                _ => return Err(route_configuration_error()),
            };
            let provider = ExecutableRouteProvider {
                provider_id: route.provider_id.clone(),
                api_family: route.api_family.clone(),
                credential_env: route.credential_env.clone(),
                model_ids: route.model_ids.clone(),
                inner,
            };
            providers.insert(
                format!("{}|{}", route.provider_id, route.api_family),
                Arc::new(provider),
            );
        }
        Ok(providers)
    }
}

struct ExecutableRouteProvider {
    provider_id: String,
    api_family: String,
    credential_env: String,
    model_ids: BTreeSet<String>,
    inner: Arc<dyn Provider>,
}

#[async_trait]
impl Provider for ExecutableRouteProvider {
    /// 功能：返回 route snapshot 固定的 canonical Provider ID。
    ///
    /// 输入：当前 route wrapper。
    /// 输出：manifest Provider ID。
    /// 不变量：不由 endpoint、credential 或请求推断。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn id(&self) -> &str {
        &self.provider_id
    }

    /// 功能：返回 route snapshot 固定的 API family。
    ///
    /// 输入：当前 route wrapper。
    /// 输出：始终为一个显式 family。
    /// 不变量：不从 modelId 或注册顺序猜测。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn api_family(&self) -> Option<&str> {
        Some(&self.api_family)
    }

    /// 功能：按 snapshot catalog allowlist 判断 modelId。
    ///
    /// 输入：未经信任的 modelId。
    /// 输出：精确三元目录命中时为 true。
    /// 不变量：不保留动态 modelId 后门。
    /// 失败：本方法不执行 I/O 且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn supports_model(&self, model_id: &str) -> bool {
        self.model_ids.contains(model_id)
    }

    /// 功能：在 durable 接受前重新判断 route credential 是否存在且非空。
    ///
    /// 输入：manifest 固定的 credential source 名称。
    /// 输出：当前环境 presence 状态。
    /// 不变量：不把 credential 值保存、记录或返回。
    /// 失败：缺失或空值安全返回 false。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn is_available(&self) -> bool {
        credential_is_present(&self.credential_env)
    }

    /// 功能：通过已绑定的唯一 family adapter 执行 canonical route 请求。
    ///
    /// 输入：已通过三元 allowlist 的 Provider 请求与取消令牌。
    /// 输出：family adapter 规范化后的事件流。
    /// 不变量：wrapper 不接触 credential 值；inner adapter 在最终 header 边界读取最新值。
    /// 失败：credential 竞态失效或 inner transport/protocol 失败时返回脱敏结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn stream(
        &self,
        request: ProviderRequest,
        cancellation: CancellationToken,
    ) -> Result<ProviderStream, AgentError> {
        if !self.is_available() {
            return Err(provider_unavailable());
        }
        self.inner.stream(request, cancellation).await
    }
}

/// 功能：从冻结索引构造六条 allowlist route，并按可选 config 与 credential presence 收窄。
///
/// 输入：可选已验证 config，以及只返回布尔 presence、不得泄密的 credential predicate。
/// 输出：广告与执行共享的不可变 route/model 快照。
/// 不变量：config 只能收窄和替换 loopback base；不能创建 route/model 或改变 adapter/auth/capability。
/// 失败：冻结索引、route 条件、endpoint 一致性、config 引用或模型 census 漂移时返回内部错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn build_snapshot(
    config: Option<RouteConfig>,
    credential_present: impl Fn(&str) -> bool,
) -> Result<ProviderRouteSnapshot, StrictInputError> {
    let allow_legacy_fallback = config.is_none();
    let (manifest, catalog) = load_frozen_indexes()?;
    let mut routes = Vec::new();
    let mut models = Vec::new();
    let mut config_matched = config.is_none();
    for spec in ROUTE_SPECS {
        let key = (spec.provider_id.to_owned(), spec.api_family.to_owned());
        let route = manifest.routes.get(&key).ok_or(StrictInputError)?;
        let auth = manifest
            .auth_profiles
            .get(&(spec.provider_id.to_owned(), "api-key".to_owned()))
            .ok_or(StrictInputError)?;
        if route.media != spec.media
            || route.adapter_id != spec.adapter_id
            || route.auth_profile_ids != ["api-key"]
            || route.header_policy_id != "api-family-default"
            || !route.quirks.is_empty()
            || !route.template_bindings.is_empty()
            || !route.runtime_endpoint_env.is_empty()
            || auth.kind != "api-key"
            || auth.environment != [spec.credential_env]
        {
            return Err(StrictInputError);
        }
        let route_models = catalog.get(&key).ok_or(StrictInputError)?;
        if route_models.len() != spec.model_count
            || route_models.iter().any(|model| {
                model.endpoint_strategy != "fixed"
                    || model.endpoint_base.as_deref() != Some(spec.endpoint_base)
            })
        {
            return Err(StrictInputError);
        }

        let selected = config.as_ref().is_none_or(|config| {
            config.provider_id == spec.provider_id && config.api_family == spec.api_family
        });
        if !selected {
            continue;
        }
        let selected_models = route_models
            .iter()
            .filter(|model| {
                config
                    .as_ref()
                    .is_none_or(|config| config.model_id == model.model_id)
            })
            .collect::<Vec<_>>();
        if selected_models.is_empty() {
            return Err(StrictInputError);
        }
        config_matched = true;
        let endpoint_base = config.as_ref().map_or_else(
            || spec.endpoint_base.to_owned(),
            |config| config.endpoint_base.clone(),
        );
        let features = if spec.media == "text" {
            BTreeSet::from([
                "authentication".to_owned(),
                "text".to_owned(),
                "streaming".to_owned(),
                "tools".to_owned(),
            ])
        } else {
            BTreeSet::from([
                "authentication".to_owned(),
                "text".to_owned(),
                "image_input".to_owned(),
                "image_output".to_owned(),
            ])
        };
        let descriptors = selected_models
            .iter()
            .map(|model| normalized_model(model, &features))
            .collect::<Vec<_>>();
        if !credential_present(spec.credential_env) {
            continue;
        }
        let model_ids = selected_models
            .iter()
            .map(|model| model.model_id.clone())
            .collect::<BTreeSet<_>>();
        models.extend(descriptors.iter().cloned());
        routes.push(ExecutableRoute {
            provider_id: spec.provider_id.to_owned(),
            api_family: spec.api_family.to_owned(),
            adapter_id: spec.adapter_id.to_owned(),
            credential_env: spec.credential_env.to_owned(),
            endpoint_base,
            model_ids,
        });
    }
    if !config_matched {
        return Err(StrictInputError);
    }
    models.sort_by_key(model_key);
    Ok(ProviderRouteSnapshot {
        routes,
        models,
        allow_legacy_fallback,
    })
}

/// 功能：严格验证 conformance route config 的 Schema 闭集与 literal-loopback endpoint。
///
/// 输入：已通过 duplicate-key 严格 JSON 与 deny-unknown-fields 解码的配置。
/// 输出：所有标识、长度和 endpoint 字面规则有效时成功。
/// 不变量：配置不含 credential、adapter、header 或 quirk；endpoint 只能是无 query/fragment 的 127.0.0.1 HTTP base。
/// 失败：版本、pattern、长度、控制字符、port 或 path 违规时返回内部错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_route_config(config: &RouteConfig) -> Result<(), StrictInputError> {
    if config.schema_version != "0.1"
        || !is_provider_id(&config.provider_id)
        || !is_slug(&config.api_family)
        || config.model_id.is_empty()
        || config.model_id.chars().count() > 256
        || config.model_id.chars().any(char::is_control)
        || !is_literal_loopback_base(&config.endpoint_base)
    {
        return Err(StrictInputError);
    }
    Ok(())
}

/// 功能：判断字符串是否满足 canonical Provider ID 的 ASCII pattern。
///
/// 输入：未经信任字符串。
/// 输出：长度与字符闭集符合 Schema 时为 true。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn is_provider_id(value: &str) -> bool {
    let bytes = value.as_bytes();
    (1..=128).contains(&bytes.len())
        && bytes
            .first()
            .is_some_and(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit())
        && bytes.iter().all(|byte| {
            byte.is_ascii_lowercase() || byte.is_ascii_digit() || matches!(byte, b'-' | b'.')
        })
}

/// 功能：判断字符串是否满足 API family slug 的 ASCII pattern。
///
/// 输入：未经信任字符串。
/// 输出：长度与小写字母/数字/连字符闭集符合 Schema 时为 true。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn is_slug(value: &str) -> bool {
    let bytes = value.as_bytes();
    (1..=128).contains(&bytes.len())
        && bytes
            .first()
            .is_some_and(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit())
        && bytes
            .iter()
            .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || *byte == b'-')
}

/// 功能：按配置 Schema 的精确字面语法验证 loopback endpoint base。
///
/// 输入：未经信任 endpoint 字符串。
/// 输出：仅 `http://127.0.0.1:<1..65535>` 加零个或多个安全非空 path segment 时为 true。
/// 不变量：拒绝 userinfo、query、fragment、反斜杠、percent encoding、authority 编码和非 loopback host。
/// 失败：本方法不解析 DNS、不访问网络且不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn is_literal_loopback_base(value: &str) -> bool {
    if value.len() > 2048 {
        return false;
    }
    let Some(remainder) = value.strip_prefix("http://127.0.0.1:") else {
        return false;
    };
    let (port, path) = remainder
        .split_once('/')
        .map_or((remainder, None), |(port, path)| (port, Some(path)));
    if port.is_empty()
        || port.len() > 5
        || port.starts_with('0')
        || !port.bytes().all(|byte| byte.is_ascii_digit())
        || port.parse::<u16>().ok().is_none_or(|value| value == 0)
    {
        return false;
    }
    path.is_none_or(|path| {
        !path.is_empty()
            && path.split('/').all(|segment| {
                !segment.is_empty()
                    && segment.bytes().all(|byte| {
                        byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'~' | b'-')
                    })
            })
    })
}

/// 功能：判断 unmatched canonical 请求是否属于旧 family conformance 的显式兼容入口。
///
/// 输入：Provider ID 与客户端可选 family。
/// 输出：只在旧 runner 省略 family 或给出其固定 family、启用 Provider conformance 且对应 loopback endpoint env 非空时为 true。
/// 不变量：不读取 credential，不为普通生产或显式 wrong-family 请求开放动态 modelId。
/// 失败：环境缺失安全返回 false。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn is_legacy_family_fallback(provider_id: &str, api_family: Option<&str>) -> bool {
    if std::env::var("QXNM_FORGE_PROVIDER_CONFORMANCE").as_deref() != Ok("1") {
        return false;
    }
    let route = match provider_id {
        "mistral" => Some((
            "mistral-conversations",
            "QXNM_FORGE_MISTRAL_CONVERSATIONS_ENDPOINT",
        )),
        "google" => Some((
            "google-generative-ai",
            "QXNM_FORGE_GOOGLE_GENERATIVE_AI_ENDPOINT",
        )),
        "openrouter" => Some(("openrouter-images", "QXNM_FORGE_OPENROUTER_IMAGES_ENDPOINT")),
        _ => None,
    };
    route.is_some_and(|(family, endpoint_env)| {
        api_family.is_none_or(|value| value == family) && credential_is_present(endpoint_env)
    })
}

/// 功能：为普通快照判断 credential presence，同时让旧 family runner 的 endpoint override 优先。
///
/// 输入：六条 allowlist 之一的固定 credential 环境名称。
/// 输出：credential 非空且当前进程没有对应 legacy conformance endpoint 时为 true。
/// 不变量：避免旧 OpenRouter image runner 的 catalog model 被 production endpoint route 抢占；
/// 普通生产不读取或接受任何 endpoint override。
/// 失败：环境缺失安全返回 false，本函数不返回诊断。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn production_credential_available(credential_env: &str) -> bool {
    let legacy_endpoint = match credential_env {
        "MISTRAL_API_KEY" => Some("QXNM_FORGE_MISTRAL_CONVERSATIONS_ENDPOINT"),
        "GEMINI_API_KEY" => Some("QXNM_FORGE_GOOGLE_GENERATIVE_AI_ENDPOINT"),
        "OPENROUTER_API_KEY" => Some("QXNM_FORGE_OPENROUTER_IMAGES_ENDPOINT"),
        _ => None,
    };
    let legacy_active = std::env::var("QXNM_FORGE_PROVIDER_CONFORMANCE").as_deref() == Ok("1")
        && legacy_endpoint.is_some_and(credential_is_present);
    !legacy_active && credential_is_present(credential_env)
}

/// 功能：构造统一、不泄漏配置路径、endpoint、model 或 credential 的启动错误。
///
/// 输出：稳定 `InvalidParams/invalid_params` 错误。
/// 不变量：消息不含攻击者控制值。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn route_configuration_error() -> AgentError {
    AgentError::new(ErrorCode::InvalidParams, ROUTE_CONFIGURATION_ERROR)
}

/// 功能：构造 Session 创建前 canonical route 不可用的稳定协议错误。
///
/// 输出：`ProviderUnavailable/provider_unavailable` 且不带 route、model 或 credential 值。
/// 不变量：错误可安全写入协议、stderr 与失败记录。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn provider_unavailable() -> AgentError {
    AgentError::new(
        ErrorCode::ProviderUnavailable,
        "provider route is unavailable",
    )
    .with_details(json!({"kind":"provider_unavailable"}))
}

#[cfg(test)]
mod tests {
    use super::{RouteConfig, build_snapshot, is_literal_loopback_base};

    /// 功能：验证六条冻结 allowlist 恰好生成 133 个 catalog descriptor。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn validates_frozen_route_spine_counts() -> Result<(), Box<dyn std::error::Error>> {
        let snapshot = build_snapshot(None, |_| true).map_err(|_| "route snapshot rejected")?;
        assert_eq!(snapshot.routes.len(), 6);
        assert_eq!(snapshot.models.len(), 133);
        assert_eq!(
            snapshot
                .routes
                .iter()
                .map(|route| route.model_ids.len())
                .sum::<usize>(),
            133
        );
        Ok(())
    }

    /// 功能：验证 conformance config 只能把快照收窄为一条既有 route 的一个模型。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn conformance_config_narrows_existing_route_and_model()
    -> Result<(), Box<dyn std::error::Error>> {
        let snapshot = build_snapshot(
            Some(RouteConfig {
                schema_version: "0.1".to_owned(),
                provider_id: "google".to_owned(),
                api_family: "google-generative-ai".to_owned(),
                model_id: "gemini-2.5-flash".to_owned(),
                endpoint_base: "http://127.0.0.1:12345/mock".to_owned(),
            }),
            |_| true,
        )
        .map_err(|_| "route snapshot rejected")?;
        assert_eq!(snapshot.routes.len(), 1);
        assert_eq!(snapshot.models.len(), 1);
        assert_eq!(
            snapshot.routes[0].endpoint_base,
            "http://127.0.0.1:12345/mock"
        );
        Ok(())
    }

    /// 功能：验证 literal-loopback parser 拒绝 authority、编码、query、fragment 与空 path segment 注入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn validates_literal_loopback_endpoint_grammar() {
        assert!(is_literal_loopback_base("http://127.0.0.1:12345/mock-v1"));
        for invalid in [
            "https://127.0.0.1:12345",
            "http://localhost:12345",
            "http://127.0.0.1:0",
            "http://127.0.0.1:65536",
            "http://user@127.0.0.1:12345",
            "http://127.0.0.1:12345/%2fhost",
            "http://127.0.0.1:12345/mock?x=1",
            "http://127.0.0.1:12345/mock#x",
            "http://127.0.0.1:12345/mock//nested",
        ] {
            assert!(!is_literal_loopback_base(invalid), "accepted {invalid}");
        }
    }
}
