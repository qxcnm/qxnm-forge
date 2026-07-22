//! 冻结 Provider/model 目录与经审计官方兼容入口派生的自定义连接模板。
//!
//! 作者：高宏顺 <18272669457@163.com>

use std::collections::BTreeSet;

use reqwest::Url;
use serde::{Deserialize, Serialize};
use serde_json::json;

use crate::error::{AgentError, ErrorCode};
use crate::provider::native_endpoint;
use crate::provider_identity::load_frozen_indexes;

const API_FAMILY: &str = "openai-completions";
const ADAPTER_ID: &str = "openai-completions-v1";
const API_KEY_PROFILE: &str = "api-key";
const MODEL_DISCOVERY: &str = "openai-models";
const EXPECTED_DERIVED_TEMPLATE_COUNT: usize = 20;
const EXPECTED_TEMPLATE_COUNT: usize = 23;
const AUDITED_OFFICIAL_COMPATIBILITY_TEMPLATES: [(&str, &str, &str); 3] = [
    ("openai", "OpenAI", "https://api.openai.com/v1"),
    (
        "anthropic",
        "Anthropic Claude",
        "https://api.anthropic.com/v1",
    ),
    (
        "google",
        "Google Gemini",
        "https://generativelanguage.googleapis.com/v1beta/openai",
    ),
];

/// `providerCatalog/list` 的严格空参数。
#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ProviderCatalogListParams {}

/// 一个只提供非敏感默认值、不会声明 Provider 已配置或可执行的连接模板。
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProviderConnectionTemplate {
    pub template_id: String,
    pub display_name: String,
    pub suggested_provider_id: String,
    pub api_family: String,
    pub default_base_url: String,
    pub model_discovery: String,
    pub logo_asset_id: Option<String>,
}

/// daemon 启动期从冻结目录和受控官方入口派生、后续仅只读投影的模板快照。
#[derive(Debug, Clone)]
pub struct ProviderCatalog {
    templates: Vec<ProviderConnectionTemplate>,
}

impl ProviderCatalog {
    /// 功能：加载冻结目录并追加经审计官方 OpenAI-compatible 连接模板。
    ///
    /// 输入：无；目录路径、摘要和 census 均由编译期冻结加载器决定。
    /// 输出：按 `templateId` ordinal 排序的不可变、无 secret 模板快照。
    /// 不变量：模板不读取 credential/environment presence，不声明远端已验证、已配置或可执行；
    /// 冻结派生 base 与受控官方兼容 base 都必须可安全追加 `/chat/completions` 与 `/models`。
    /// 失败：冻结目录、引用、兼容筛选、官方入口或受控展示映射漂移时返回脱敏内部错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn from_frozen() -> Result<Self, AgentError> {
        let (manifest, catalog) =
            load_frozen_indexes().map_err(|_| provider_catalog_unavailable())?;
        let mut templates = Vec::new();
        for (route_key, route) in &manifest.routes {
            let (provider_id, api_family) = route_key;
            if api_family != API_FAMILY
                || route.media != "text"
                || route.adapter_id != ADAPTER_ID
                || route.auth_profile_ids != [API_KEY_PROFILE]
                || !route.template_bindings.is_empty()
            {
                continue;
            }
            let auth = manifest
                .auth_profiles
                .get(&(provider_id.clone(), API_KEY_PROFILE.to_owned()))
                .ok_or_else(provider_catalog_unavailable)?;
            if auth.kind != API_KEY_PROFILE || auth.environment.len() != 1 {
                continue;
            }
            let models = catalog
                .get(route_key)
                .ok_or_else(provider_catalog_unavailable)?;
            let mut bases = BTreeSet::new();
            let compatible = models.iter().all(|model| {
                if model.media != "text"
                    || model.provider_id != *provider_id
                    || model.api_family != API_FAMILY
                    || model.endpoint_strategy != "fixed"
                {
                    return false;
                }
                model.endpoint_base.as_deref().is_some_and(|base| {
                    if !fixed_https_base_is_compatible(base) {
                        return false;
                    }
                    bases.insert(base.to_owned());
                    true
                })
            });
            if !compatible || bases.len() != 1 {
                continue;
            }
            let default_base_url = bases.pop_first().ok_or_else(provider_catalog_unavailable)?;
            let display_name = provider_display_name(provider_id)
                .ok_or_else(provider_catalog_unavailable)?
                .to_owned();
            templates.push(ProviderConnectionTemplate {
                template_id: provider_id.clone(),
                display_name,
                suggested_provider_id: format!("custom-{provider_id}"),
                api_family: API_FAMILY.to_owned(),
                default_base_url,
                model_discovery: MODEL_DISCOVERY.to_owned(),
                logo_asset_id: None,
            });
        }
        if templates.len() != EXPECTED_DERIVED_TEMPLATE_COUNT {
            return Err(provider_catalog_unavailable());
        }
        for (template_id, display_name, default_base_url) in
            AUDITED_OFFICIAL_COMPATIBILITY_TEMPLATES
        {
            if !fixed_https_base_is_compatible(default_base_url)
                || templates
                    .iter()
                    .any(|template| template.template_id == template_id)
            {
                return Err(provider_catalog_unavailable());
            }
            templates.push(ProviderConnectionTemplate {
                template_id: template_id.to_owned(),
                display_name: display_name.to_owned(),
                suggested_provider_id: format!("custom-{template_id}"),
                api_family: API_FAMILY.to_owned(),
                default_base_url: default_base_url.to_owned(),
                model_discovery: MODEL_DISCOVERY.to_owned(),
                logo_asset_id: None,
            });
        }
        templates.sort_by(|left, right| left.template_id.cmp(&right.template_id));
        if templates.len() != EXPECTED_TEMPLATE_COUNT {
            return Err(provider_catalog_unavailable());
        }
        Ok(Self { templates })
    }

    /// 功能：返回本次 daemon 生命周期固定的配置模板只读切片。
    ///
    /// 输入：启动期已完成冻结验证的 catalog 快照。
    /// 输出：按 `templateId` ordinal 排序且不含 secret/credential 状态的模板。
    /// 不变量：不重新读取文件、环境、credential 或网络，也不扩大可执行 Provider registry。
    /// 失败：本方法不执行 I/O 且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn templates(&self) -> &[ProviderConnectionTemplate] {
        &self.templates
    }
}

/// 功能：验证候选 base URL 满足当前自定义 Chat 与模型发现的共同安全路径合同。
///
/// 输入：冻结 catalog 推导或受控官方入口表中的一个 fixed base URL。
/// 输出：无敏感 URL 组成且可安全追加两个固定 path 时为 true。
/// 不变量：仅接受 HTTPS、无 userinfo/query/fragment 且具有 host 的 URL；不访问网络。
/// 失败：解析或任一路径拼接失败时安全返回 false。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn fixed_https_base_is_compatible(base: &str) -> bool {
    let Ok(url) = Url::parse(base) else {
        return false;
    };
    url.scheme() == "https"
        && url.host().is_some()
        && url.username().is_empty()
        && url.password().is_none()
        && url.query().is_none()
        && url.fragment().is_none()
        && native_endpoint(base, "/chat/completions", &[]).is_ok()
        && native_endpoint(base, "/models", &[]).is_ok()
}

/// 功能：把冻结 Provider ID 映射为稳定且受控的公开显示名。
///
/// 输入：已通过结构兼容筛选的 canonical Provider ID。
/// 输出：存在显式审核映射时返回显示名，否则返回 None 令整个快照失败关闭。
/// 不变量：不从 URL、环境或模型名推断品牌；新增候选必须经过代码评审后显式加入。
/// 失败：未知 ID 返回 None。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn provider_display_name(provider_id: &str) -> Option<&'static str> {
    match provider_id {
        "ant-ling" => Some("Ant Ling"),
        "cerebras" => Some("Cerebras"),
        "deepseek" => Some("DeepSeek"),
        "fireworks" => Some("Fireworks AI"),
        "groq" => Some("Groq"),
        "huggingface" => Some("Hugging Face"),
        "moonshotai" => Some("Moonshot AI"),
        "moonshotai-cn" => Some("Moonshot AI (China)"),
        "nvidia" => Some("NVIDIA NIM"),
        "opencode" => Some("OpenCode"),
        "opencode-go" => Some("OpenCode Go"),
        "openrouter" => Some("OpenRouter"),
        "together" => Some("Together AI"),
        "xai" => Some("xAI"),
        "xiaomi" => Some("Xiaomi MiMo"),
        "xiaomi-token-plan-ams" => Some("Xiaomi MiMo Token Plan (Amsterdam)"),
        "xiaomi-token-plan-cn" => Some("Xiaomi MiMo Token Plan (China)"),
        "xiaomi-token-plan-sgp" => Some("Xiaomi MiMo Token Plan (Singapore)"),
        "zai" => Some("Z.AI"),
        "zai-coding-cn" => Some("Z.AI Coding (China)"),
        _ => None,
    }
}

/// 功能：构造不泄漏冻结目录路径、URL 或内容的统一 catalog 启动错误。
///
/// 输出：固定 `InternalError/provider_catalog_unavailable`。
/// 不变量：错误不含 provider、endpoint、credential 或底层解析诊断。
/// 失败：本函数只构造错误，不会再次失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn provider_catalog_unavailable() -> AgentError {
    AgentError::new(ErrorCode::InternalError, "provider catalog is unavailable")
        .with_details(json!({"kind":"provider_catalog_unavailable"}))
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeSet;

    use serde_json::{Value, json};

    use super::{ProviderCatalog, fixed_https_base_is_compatible};

    /// 功能：锁定 20 个冻结派生模板与 3 个官方兼容模板的 ordinal 顺序。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn derives_exact_openai_compatible_template_catalog() -> Result<(), Box<dyn std::error::Error>>
    {
        let catalog = ProviderCatalog::from_frozen()?;
        let templates = catalog.templates();
        let ids = templates
            .iter()
            .map(|template| template.template_id.as_str())
            .collect::<Vec<_>>();
        assert_eq!(
            ids,
            vec![
                "ant-ling",
                "anthropic",
                "cerebras",
                "deepseek",
                "fireworks",
                "google",
                "groq",
                "huggingface",
                "moonshotai",
                "moonshotai-cn",
                "nvidia",
                "openai",
                "opencode",
                "opencode-go",
                "openrouter",
                "together",
                "xai",
                "xiaomi",
                "xiaomi-token-plan-ams",
                "xiaomi-token-plan-cn",
                "xiaomi-token-plan-sgp",
                "zai",
                "zai-coding-cn",
            ]
        );
        assert!(templates.iter().all(|template| {
            template.suggested_provider_id == format!("custom-{}", template.template_id)
                && template.api_family == "openai-completions"
                && template.model_discovery == "openai-models"
                && template.logo_asset_id.is_none()
                && fixed_https_base_is_compatible(&template.default_base_url)
        }));
        let nvidia = templates
            .iter()
            .find(|template| template.template_id == "nvidia")
            .ok_or("missing NVIDIA template")?;
        assert_eq!(nvidia.display_name, "NVIDIA NIM");
        assert_eq!(
            nvidia.default_base_url,
            "https://integrate.api.nvidia.com/v1"
        );
        let deepseek = templates
            .iter()
            .find(|template| template.template_id == "deepseek")
            .ok_or("missing DeepSeek template")?;
        assert_eq!(deepseek.default_base_url, "https://api.deepseek.com");
        let official = templates
            .iter()
            .filter(|template| {
                matches!(
                    template.template_id.as_str(),
                    "openai" | "anthropic" | "google"
                )
            })
            .map(|template| {
                (
                    template.template_id.as_str(),
                    template.display_name.as_str(),
                    template.default_base_url.as_str(),
                )
            })
            .collect::<Vec<_>>();
        assert_eq!(
            official,
            vec![
                (
                    "anthropic",
                    "Anthropic Claude",
                    "https://api.anthropic.com/v1",
                ),
                (
                    "google",
                    "Google Gemini",
                    "https://generativelanguage.googleapis.com/v1beta/openai",
                ),
                ("openai", "OpenAI", "https://api.openai.com/v1"),
            ]
        );
        Ok(())
    }

    /// 功能：验证模板 JSON 只包含七个公开字段且显式输出空 logo。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn serializes_strict_public_template_shape() -> Result<(), Box<dyn std::error::Error>> {
        let catalog = ProviderCatalog::from_frozen()?;
        let value = serde_json::to_value(&catalog.templates()[0])?;
        let object = value.as_object().ok_or("template must be object")?;
        assert_eq!(
            object.keys().map(String::as_str).collect::<BTreeSet<_>>(),
            BTreeSet::from([
                "apiFamily",
                "defaultBaseUrl",
                "displayName",
                "logoAssetId",
                "modelDiscovery",
                "suggestedProviderId",
                "templateId",
            ])
        );
        assert_eq!(value["logoAssetId"], Value::Null);
        assert_eq!(value["modelDiscovery"], json!("openai-models"));
        Ok(())
    }

    /// 功能：验证 Rust 完整模板目录与跨语言 golden fixture 逐字段一致。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn matches_cross_language_golden_fixture() -> Result<(), Box<dyn std::error::Error>> {
        let catalog = ProviderCatalog::from_frozen()?;
        let actual = json!({"templates":catalog.templates()});
        let expected: Value = serde_json::from_str(include_str!(
            "../../CONFORMANCE/fixtures/provider-catalog/configuration-templates.json"
        ))?;
        assert_eq!(actual, expected);
        Ok(())
    }

    /// 功能：验证模板 endpoint 安全边界拒绝非 HTTPS 与敏感 URL 组成。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn rejects_unsafe_template_bases() {
        assert!(fixed_https_base_is_compatible("https://example.test/v1"));
        for invalid in [
            "http://example.test/v1",
            "https://user@example.test/v1",
            "https://example.test/v1?token=x",
            "https://example.test/v1#fragment",
        ] {
            assert!(!fixed_https_base_is_compatible(invalid));
        }
    }
}
