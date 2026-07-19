use std::collections::BTreeMap;
use std::fmt;

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::error::AgentError;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Role {
    System,
    User,
    Assistant,
    Tool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum ContentBlock {
    Text {
        text: String,
    },
    Reasoning {
        text: String,
        #[serde(default)]
        redacted: bool,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        signature: Option<String>,
    },
    ImageRef {
        artifact: ArtifactRef,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        alt: Option<String>,
    },
    ArtifactRef {
        artifact: ArtifactRef,
    },
    ToolCall {
        call_id: String,
        name: String,
        arguments: Value,
    },
    ToolResult {
        call_id: String,
        name: String,
        output: ToolOutput,
        is_error: bool,
    },
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Message {
    pub id: String,
    pub role: Role,
    pub content: Vec<ContentBlock>,
    pub created_at: DateTime<Utc>,
}

impl Message {
    /// 功能：创建仅包含一个文本内容块并使用当前 UTC 时间的领域消息。
    ///
    /// 输入：不透明消息标识、消息角色和文本内容。
    /// 输出：字段完整且可直接序列化的 `Message`。
    /// 不变量：消息内容恰好包含一个 `Text` 块，创建时间由本机 UTC 时钟生成。
    /// 失败：本方法不返回错误；调用方负责确保消息标识符合上层协议约束。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn text(id: impl Into<String>, role: Role, text: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            role,
            content: vec![ContentBlock::Text { text: text.into() }],
            created_at: Utc::now(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ArtifactRef {
    pub artifact_id: String,
    pub media_type: String,
    pub byte_length: u64,
    pub sha256: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub display_name: Option<String>,
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub extensions: BTreeMap<String, Value>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ToolOutput {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub text: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub artifact: Option<ArtifactRef>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub termination_reason: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub execution: Option<Value>,
    #[serde(default)]
    pub metadata: BTreeMap<String, Value>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ToolDefinition {
    pub name: String,
    pub description: String,
    pub input_schema: Value,
    pub effect: ToolEffect,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ToolEffect {
    Read,
    Write,
    Process,
    Shell,
    Terminal,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Usage {
    #[serde(default)]
    pub input_tokens: u64,
    #[serde(default)]
    pub output_tokens: u64,
    #[serde(default)]
    pub total_tokens: u64,
    #[serde(default, skip_serializing_if = "is_zero")]
    pub cached_input_tokens: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cost_micros: Option<u64>,
}

/// 功能：为序列化器判断无符号整数是否为零，以省略无意义的可选统计字段。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn is_zero(value: &u64) -> bool {
    *value == 0
}

/// Provider 图像完成事件在 Provider 与 Agent 之间短暂传递的已验证图像。
///
/// 不变量：原始字节不实现序列化，不能直接进入协议、journal、日志或错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Clone, PartialEq, Eq)]
pub struct ProviderImage {
    pub media_type: String,
    pub bytes: Vec<u8>,
}

impl fmt::Debug for ProviderImage {
    /// 功能：只输出图像媒体类型与长度，避免 `Debug` 泄漏原始 artifact bytes。
    ///
    /// 输入：当前内存图像和标准 formatter。
    /// 输出：不含 bytes 内容、base64、URL 或 path 的调试结构。
    /// 不变量：无论图像内容为何，formatter 都只观察 `media_type` 与 `bytes.len()`。
    /// 失败：底层 formatter 写入失败时原样返回 `fmt::Error`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("ProviderImage")
            .field("media_type", &self.media_type)
            .field("byte_length", &self.bytes.len())
            .finish()
    }
}

/// 功能：按 MIME allowlist 执行 PNG/JPEG/WebP/GIF 基础魔数核验。
///
/// 输入：声明媒体类型和原始图像字节。
/// 输出：声明与基础签名一致时成功。
/// 不变量：本检查只阻止明显 MIME 欺骗，不宣称安全解码或 hard sandbox。
/// 失败：未知 MIME 或签名不匹配返回不含字节的结构化协议错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) fn validate_image_signature(media_type: &str, bytes: &[u8]) -> Result<(), AgentError> {
    let valid = match media_type {
        "image/png" => bytes.starts_with(b"\x89PNG\r\n\x1a\n"),
        "image/jpeg" => bytes.starts_with(b"\xff\xd8\xff"),
        "image/webp" => bytes.len() >= 12 && bytes.starts_with(b"RIFF") && &bytes[8..12] == b"WEBP",
        "image/gif" => bytes.starts_with(b"GIF87a") || bytes.starts_with(b"GIF89a"),
        _ => false,
    };
    if valid {
        Ok(())
    } else {
        Err(AgentError::new(
            crate::error::ErrorCode::ProviderError,
            "image bytes do not match the declared media type",
        )
        .with_details(serde_json::json!({"kind":"provider_protocol_error"})))
    }
}

/// Provider 到 Agent 的进程内规范化事件。
///
/// 不变量：`ImageCompletion` 的字节只允许交给 Session artifact 发布边界，本类型不实现
/// `Serialize`/`Deserialize`，避免图像意外进入任何 wire DTO。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Clone, PartialEq)]
pub enum ProviderEvent {
    MessageStart {
        provider_message_id: String,
    },
    TextDelta {
        text: String,
    },
    ReasoningDelta {
        text: String,
    },
    ToolCallStart {
        call_id: String,
        name: String,
    },
    ToolCallArgumentsDelta {
        call_id: String,
        delta: String,
    },
    ToolCallEnd {
        call_id: String,
    },
    ImageCompletion {
        text: Option<String>,
        images: Vec<ProviderImage>,
    },
    Usage(Usage),
    MessageEnd {
        finish_reason: FinishReason,
    },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum FinishReason {
    Stop,
    #[serde(rename = "tool_use")]
    ToolCalls,
    Length,
    Cancelled,
    Error,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModelCapabilities {
    pub text: bool,
    pub tools: bool,
    pub reasoning: bool,
    pub image_input: bool,
    pub streaming: bool,
    pub max_context_tokens: u64,
    pub max_output_tokens: u64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EventEnvelope {
    pub session_id: String,
    pub run_id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub turn_id: Option<String>,
    pub seq: u64,
    pub time: DateTime<Utc>,
    #[serde(rename = "type")]
    pub event_type: String,
    pub data: Value,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ApprovalRequest {
    pub approval_id: String,
    pub tool_call_id: String,
    pub effect: ToolEffect,
    pub summary: String,
    pub arguments_digest: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RunFailure {
    pub error: AgentError,
}

#[cfg(test)]
mod tests {
    use super::{ContentBlock, Message, ProviderImage, Role};

    /// 功能：验证内容块在 JSON 线上格式中使用带 `type` 字段的标记联合结构。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn content_uses_tagged_wire_shape() -> Result<(), serde_json::Error> {
        let message = Message::text("m1", Role::User, "hello");
        let value = serde_json::to_value(message)?;
        assert_eq!(value["content"][0]["type"], "text");
        assert!(matches!(
            serde_json::from_value::<ContentBlock>(value["content"][0].clone())?,
            ContentBlock::Text { .. }
        ));
        Ok(())
    }

    /// 功能：验证 ProviderImage 的 Debug 表示不会泄漏原始图像字节。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn provider_image_debug_is_redacted() {
        let image = ProviderImage {
            media_type: "image/png".to_owned(),
            bytes: b"image-secret-canary".to_vec(),
        };
        let debug = format!("{image:?}");
        assert!(debug.contains("byte_length"));
        assert!(!debug.contains("image-secret-canary"));
    }
}
