//! 原生 Provider 传输层：每个 API family 只实现一次，而不是按品牌复制。

mod anthropic;
mod azure_responses;
mod bedrock;
mod codex_responses;
mod faux;
mod google;
mod mistral;
mod openai_chat;
mod openai_responses;
mod openrouter_images;
mod sse;
mod vertex;

use std::pin::Pin;

use async_trait::async_trait;
use futures_util::Stream;
use serde::{Deserialize, Serialize};
use tokio_util::sync::CancellationToken;

pub use anthropic::AnthropicMessagesProvider;
pub use azure_responses::AzureOpenAiResponsesProvider;
pub use bedrock::BedrockConverseStreamProvider;
pub use codex_responses::OpenAiCodexResponsesProvider;
pub use faux::{
    FauxContinuation, FauxError, FauxErrorDetails, FauxMoney, FauxProvider, FauxScenario,
    FauxScript, FauxStep, FauxUsage,
};
pub use google::GoogleGenerativeAiProvider;
pub use mistral::MistralConversationsProvider;
pub use openai_chat::OpenAiChatProvider;
pub use openai_responses::OpenAiResponsesProvider;
pub use openrouter_images::OpenRouterImagesProvider;
pub(crate) use sse::native_endpoint;
pub use sse::{SseDecoder, SseEvent};
pub use vertex::GoogleVertexProvider;

use crate::commercial_state::ProviderCredentialStore;
use crate::domain::{Message, ProviderEvent, ToolDefinition};
use crate::error::AgentError;

/// Provider adapter 只保存来源身份、并在最终请求边界解析 credential 的统一来源。
#[derive(Clone)]
pub(crate) enum ProviderCredentialSource {
    Environment(Option<String>),
    Stored {
        store: ProviderCredentialStore,
        provider_id: String,
    },
}

impl ProviderCredentialSource {
    /// 功能：创建兼容既有 adapter 的请求期环境 credential 来源。
    ///
    /// 输入：固定环境变量名称；None 表示必需 credential 不可用。
    /// 输出：不持有环境值的来源对象。
    /// 不变量：值只在 `read_for_request` 调用期间读取。
    /// 失败：本方法不读取环境且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn from_environment(name: Option<String>) -> Self {
        Self::Environment(name)
    }

    /// 功能：创建工作区外文件型 stored credential 来源。
    ///
    /// 输入：已验证 store 句柄和固定本地 Provider ID。
    /// 输出：只持有路径与 ID、不持有 key 的来源对象。
    /// 不变量：stored source 失败时不会回退环境变量。
    /// 失败：本方法不读取文件且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn from_store(store: ProviderCredentialStore, provider_id: String) -> Self {
        Self::Stored { store, provider_id }
    }

    /// 功能：在 Session durable 副作用前判断来源当前可用。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn is_available(&self) -> bool {
        match self {
            Self::Environment(name) => name.as_deref().is_some_and(credential_is_present),
            Self::Stored { store, provider_id } => store.contains(provider_id),
        }
    }

    /// 功能：仅在最终 HTTP header 构造边界读取最新 credential。
    ///
    /// 输入：构造时固定的环境名称或 stored Provider ID。
    /// 输出：请求局部持有的非空 credential。
    /// 不变量：不记录、持久化或回显值；stored 失败不回退环境。
    /// 失败：来源缺失、损坏或不可读时返回脱敏 ProviderUnavailable。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn read_for_request(&self) -> Result<String, AgentError> {
        match self {
            Self::Environment(name) => credential_from_environment(name.as_deref()),
            Self::Stored { store, provider_id } => store.read_for_request(provider_id),
        }
    }
}

pub type ProviderStream =
    Pin<Box<dyn Stream<Item = Result<ProviderEvent, AgentError>> + Send + 'static>>;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProviderRequest {
    pub model: String,
    pub messages: Vec<Message>,
    #[serde(default)]
    pub tools: Vec<ToolDefinition>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_output_tokens: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub session_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub run_id: Option<String>,
}

#[async_trait]
pub trait Provider: Send + Sync {
    /// 功能：返回当前 Provider 实例在运行时注册表中的稳定标识。
    ///
    /// 输入：当前 Provider 实例。
    /// 输出：生命周期受实例约束的标识字符串。
    /// 不变量：同一实例生命周期内标识不变，且不得包含凭据。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn id(&self) -> &str;

    /// 功能：返回本实例绑定的显式 API family；旧的唯一文本路由返回 `None`。
    ///
    /// 输入：当前 Provider 实例。
    /// 输出：需要参与路由的品牌中立 family 标识，或兼容旧唯一文本路由的空值。
    /// 不变量：同一实例生命周期内返回值稳定，且不得由 modelId 或请求内容猜测。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn api_family(&self) -> Option<&str> {
        None
    }

    /// 功能：判断 modelId 是否属于当前实例公开支持的固定目录。
    ///
    /// 输入：未经信任的语言中立 modelId。
    /// 输出：允许进入原生 Provider 请求时为 true。
    /// 不变量：默认实现保持既有文本 Provider 行为；有冻结目录的 family 必须覆盖本方法。
    /// 失败：本方法不执行 I/O 且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn supports_model(&self, _model_id: &str) -> bool {
        true
    }

    /// 功能：在创建 Session 前判断当前 route 的运行时依赖是否仍可用。
    ///
    /// 输入：当前 Provider 实例；不得传入或返回 credential 值。
    /// 输出：route 可进入 durable 接受流程时为 true。
    /// 不变量：默认兼容旧 adapter；manifest route 必须覆盖并只做无副作用 availability 检查。
    /// 失败：本方法不执行网络或持久化且不返回错误；false 由 Agent 映射为 `provider_unavailable`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn is_available(&self) -> bool {
        true
    }

    /// 功能：以当前 API family 原生传输发起模型调用并返回规范化异步事件流。
    ///
    /// 输入：语言中立 Provider 请求及运行级取消令牌。
    /// 输出：按到达顺序产生统一 `ProviderEvent` 的异步流。
    /// 不变量：实现不得借用其他语言运行时；认证信息不得进入事件、日志或错误详情。
    /// 失败：请求、认证、限流、流中断、协议解析或取消失败均返回稳定结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn stream(
        &self,
        request: ProviderRequest,
        cancellation: CancellationToken,
    ) -> Result<ProviderStream, AgentError>;
}

/// 功能：在最终 HTTP request header 构造边界读取一个非空环境 credential。
///
/// 输入：manifest 固定且不含秘密值的环境变量名称。
/// 输出：只由调用方请求局部持有的 credential 字符串。
/// 不变量：函数不记录、持久化或回显名称和值；调用方必须在 header 构造后尽快释放副本。
/// 失败：名称缺失、非 Unicode 或值为空时返回固定脱敏 `provider_unavailable`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) fn credential_from_environment(name: Option<&str>) -> Result<String, AgentError> {
    #[cfg(test)]
    if name == Some("QXNM_FORGE_TEST_FIXED_CREDENTIAL") {
        return Ok("qxnm-forge-secret-canary-local-sse".to_owned());
    }
    name.and_then(|name| std::env::var(name).ok())
        .filter(|value| !value.is_empty())
        .ok_or_else(|| {
            AgentError::new(
                crate::error::ErrorCode::ProviderUnavailable,
                "provider credential is unavailable",
            )
            .with_details(serde_json::json!({"kind":"provider_unavailable"}))
        })
}

/// 功能：只以 opaque OS 环境值判断一个固定 credential source 当前是否非空。
///
/// 输入：manifest 固定环境变量名称。
/// 输出：变量存在、可读取且至少含一个字节时为 true。
/// 不变量：不把值转换为日志、协议、错误或持久对象；结果只用于 route availability。
/// 失败：缺失或空值安全返回 false；本函数不返回诊断。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) fn credential_is_present(name: &str) -> bool {
    std::env::var_os(name).is_some_and(|value| !value.is_empty())
}

/// 功能：从请求消息倒序提取最近用户消息中的首个文本块。
///
/// 输入：只读公共 Provider 请求。
/// 输出：找到的文本副本，未找到时为空字符串。
/// 不变量：不修改消息顺序或请求内容，也不读取系统、助手或工具消息作为提示。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) fn last_user_text(request: &ProviderRequest) -> String {
    request
        .messages
        .iter()
        .rev()
        .find(|message| message.role == crate::domain::Role::User)
        .and_then(|message| {
            message.content.iter().find_map(|block| match block {
                crate::domain::ContentBlock::Text { text } => Some(text.clone()),
                _ => None,
            })
        })
        .unwrap_or_default()
}
