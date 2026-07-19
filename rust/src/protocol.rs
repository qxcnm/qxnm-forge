use std::collections::BTreeMap;
use std::fmt;

use serde::de::{self, MapAccess, SeqAccess, Visitor};
use serde::{Deserialize, Deserializer, Serialize};
use serde_json::Value;

use crate::domain::{ArtifactRef, ContentBlock, EventEnvelope};
use crate::error::AgentError;

pub const PROTOCOL_VERSION: &str = "0.1";

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct JsonRpcRequest {
    pub jsonrpc: String,
    pub id: Value,
    pub method: String,
    #[serde(default)]
    pub params: Value,
}

struct StrictValue(Value);

struct StrictValueVisitor;

impl<'de> Visitor<'de> for StrictValueVisitor {
    type Value = StrictValue;

    /// 功能：描述严格 JSON 值解析器的期望输入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn expecting(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str("a JSON value without duplicate object keys")
    }

    /// 功能：把 JSON null 转换为严格 Value。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn visit_unit<E>(self) -> Result<Self::Value, E> {
        Ok(StrictValue(Value::Null))
    }

    /// 功能：把 JSON null/none 转换为严格 Value。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn visit_none<E>(self) -> Result<Self::Value, E>
    where
        E: de::Error,
    {
        self.visit_unit()
    }

    /// 功能：递归解析 Option 中存在的严格 JSON 值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn visit_some<D>(self, deserializer: D) -> Result<Self::Value, D::Error>
    where
        D: Deserializer<'de>,
    {
        StrictValue::deserialize(deserializer)
    }

    /// 功能：把 JSON 布尔值转换为严格 Value。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn visit_bool<E>(self, value: bool) -> Result<Self::Value, E> {
        Ok(StrictValue(Value::Bool(value)))
    }

    /// 功能：把有符号 JSON 整数转换为严格 Value。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn visit_i64<E>(self, value: i64) -> Result<Self::Value, E> {
        Ok(StrictValue(Value::Number(value.into())))
    }

    /// 功能：把无符号 JSON 整数转换为严格 Value。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn visit_u64<E>(self, value: u64) -> Result<Self::Value, E> {
        Ok(StrictValue(Value::Number(value.into())))
    }

    /// 功能：把有限 JSON 浮点数转换为严格 Value，并拒绝非有限值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn visit_f64<E>(self, value: f64) -> Result<Self::Value, E>
    where
        E: de::Error,
    {
        serde_json::Number::from_f64(value)
            .map(Value::Number)
            .map(StrictValue)
            .ok_or_else(|| E::custom("non-finite JSON number is invalid"))
    }

    /// 功能：复制借用 JSON 字符串为严格 Value。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn visit_str<E>(self, value: &str) -> Result<Self::Value, E> {
        Ok(StrictValue(Value::String(value.to_owned())))
    }

    /// 功能：接收已拥有 JSON 字符串为严格 Value。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn visit_string<E>(self, value: String) -> Result<Self::Value, E> {
        Ok(StrictValue(Value::String(value)))
    }

    /// 功能：递归解析 JSON 数组并保持元素原始顺序。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn visit_seq<A>(self, mut sequence: A) -> Result<Self::Value, A::Error>
    where
        A: SeqAccess<'de>,
    {
        let mut values = Vec::new();
        while let Some(StrictValue(value)) = sequence.next_element()? {
            values.push(value);
        }
        Ok(StrictValue(Value::Array(values)))
    }

    /// 功能：递归解析 JSON 对象并在同一对象层级拒绝重复字段名。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn visit_map<A>(self, mut map: A) -> Result<Self::Value, A::Error>
    where
        A: MapAccess<'de>,
    {
        let mut values = serde_json::Map::new();
        while let Some(key) = map.next_key::<String>()? {
            if values.contains_key(&key) {
                return Err(de::Error::custom(format!(
                    "duplicate JSON object key: {key}"
                )));
            }
            let StrictValue(value) = map.next_value()?;
            values.insert(key, value);
        }
        Ok(StrictValue(Value::Object(values)))
    }
}

impl<'de> Deserialize<'de> for StrictValue {
    /// 功能：入口式递归反序列化严格 JSON，确保嵌套对象也拒绝重复键。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        deserializer.deserialize_any(StrictValueVisitor)
    }
}

/// 功能：严格解析任意 JSON 值，并递归拒绝重复对象键。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
///
/// 输入必须是且只能是一个 RFC 8259 JSON 值；非有限数字、重复键或尾随数据均失败。
pub(crate) fn parse_strict_value(input: &str) -> Result<Value, serde_json::Error> {
    let StrictValue(value) = serde_json::from_str(input)?;
    Ok(value)
}

/// 功能：严格解析一个 JSON-RPC 请求，拒绝重复键、未知字段和非法 DTO。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub fn parse_strict_request(input: &str) -> Result<JsonRpcRequest, serde_json::Error> {
    serde_json::from_value(parse_strict_value(input)?)
}

/// 功能：验证 JSON-RPC ID 是非空短字符串或 JavaScript 安全范围内的非负整数。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[must_use]
pub fn is_valid_request_id(id: &Value) -> bool {
    match id {
        Value::String(value) => !value.is_empty() && value.len() <= 128,
        Value::Number(value) => value
            .as_u64()
            .is_some_and(|number| number <= 9_007_199_254_740_991),
        _ => false,
    }
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct JsonRpcResponse {
    pub jsonrpc: &'static str,
    pub id: Value,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<RpcError>,
}

impl JsonRpcResponse {
    /// 功能：为 opaque 请求 ID 创建符合 JSON-RPC 2.0 的成功响应。
    ///
    /// 输入：原样回传的 JSON 请求 ID 和语言中立的结果值。
    /// 输出：仅设置 `result`、不设置 `error` 的响应。
    /// 不变量：请求 ID 不经数值或字符串转换，协议版本固定为 `2.0`。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn success(id: Value, result: Value) -> Self {
        Self {
            jsonrpc: "2.0",
            id,
            result: Some(result),
            error: None,
        }
    }

    /// 功能：为 opaque 请求 ID 创建符合 JSON-RPC 2.0 的结构化失败响应。
    ///
    /// 输入：原样回传的 JSON 请求 ID 和统一领域错误。
    /// 输出：仅设置 `error`、不设置 `result` 的响应。
    /// 不变量：稳定错误码经集中映射生成，客户端无需且不得解析消息文本。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn failure(id: Value, error: AgentError) -> Self {
        Self {
            jsonrpc: "2.0",
            id,
            result: None,
            error: Some(error.into()),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RpcError {
    pub code: i64,
    pub message: String,
    pub retryable: bool,
    pub details: Value,
}

impl From<AgentError> for RpcError {
    /// 功能：将领域结构化错误映射为公共 JSON-RPC 错误数据。
    ///
    /// 输入：语言中立的领域错误。
    /// 输出：包含数值码、稳定领域码、消息、重试标志和详情的 RPC 错误。
    /// 不变量：所有字段语义保持不变，数值码只通过集中映射产生。
    /// 失败：转换本身不失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn from(value: AgentError) -> Self {
        Self {
            code: value.code.rpc_code(),
            message: value.message,
            retryable: value.retryable,
            details: value.details,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct JsonRpcNotification<'a> {
    pub jsonrpc: &'static str,
    pub method: &'static str,
    pub params: &'a EventEnvelope,
}

impl<'a> From<&'a EventEnvelope> for JsonRpcNotification<'a> {
    /// 功能：把事件信封包装为借用数据的 JSON-RPC `event` 通知。
    ///
    /// 输入：已分配单调序号的事件信封引用。
    /// 输出：固定方法名为 `event` 且借用原信封的通知。
    /// 不变量：事件字段不复制、不改写，协议版本固定为 `2.0`。
    /// 失败：转换本身不失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn from(value: &'a EventEnvelope) -> Self {
        Self {
            jsonrpc: "2.0",
            method: "event",
            params: value,
        }
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct InitializeParams {
    pub protocol_versions: Vec<String>,
    #[serde(default)]
    pub client: Option<ClientInfo>,
    pub capabilities: ClientCapabilities,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ClientCapabilities {
    #[serde(default)]
    pub event_replay: bool,
    #[serde(default)]
    pub interactive_approvals: bool,
    #[serde(default)]
    pub terminal_events: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PeerInfo {
    pub name: String,
    pub version: String,
    pub language: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ClientInfo {
    pub name: String,
    pub version: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct InitializeResult {
    pub protocol_version: &'static str,
    pub implementation: PeerInfo,
    pub capabilities: Value,
    pub limits: Value,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RunStartParams {
    pub session_id: String,
    pub input: InputMessage,
    pub provider: ProviderSelection,
    #[serde(default)]
    pub options: Option<RunOptions>,
    #[serde(default)]
    pub extensions: BTreeMap<String, Value>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RunOptions {
    #[serde(default)]
    pub thinking: Option<String>,
    #[serde(default)]
    pub tool_execution: Option<String>,
    #[serde(default)]
    pub steering_mode: Option<String>,
    #[serde(default)]
    pub follow_up_mode: Option<String>,
    #[serde(default)]
    pub extensions: BTreeMap<String, Value>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct InputMessage {
    pub role: String,
    pub content: Vec<InputContent>,
    #[serde(default)]
    pub extensions: BTreeMap<String, Value>,
}

impl InputMessage {
    /// 功能：校验运行输入为非空用户文本消息并合并多个文本内容块。
    ///
    /// 输入：反序列化后的输入消息。
    /// 输出：按原顺序以换行连接的文本内容。
    /// 不变量：只有角色严格为 `user` 且至少包含一个非空总体文本的消息可进入 Agent。
    /// 失败：角色错误、内容块为空或合并文本为空时返回 `InvalidParams`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn text(&self) -> Result<String, crate::error::AgentError> {
        if self.role != "user" || self.content.is_empty() {
            return Err(crate::error::AgentError::new(
                crate::error::ErrorCode::InvalidParams,
                "run input must be a non-empty user message",
            ));
        }
        if self
            .content
            .iter()
            .any(|content| !matches!(content, InputContent::Text { .. }))
        {
            return Err(crate::error::AgentError::new(
                crate::error::ErrorCode::InvalidParams,
                "protocol v0.1 run input currently accepts text content only",
            ));
        }
        let text = self
            .content
            .iter()
            .filter_map(|content| match content {
                InputContent::Text { text } => Some(text.as_str()),
                InputContent::ImageRef { .. } => None,
            })
            .collect::<Vec<_>>()
            .join("\n");
        if text.is_empty() {
            return Err(crate::error::AgentError::new(
                crate::error::ErrorCode::InvalidParams,
                "run input text must not be empty",
            ));
        }
        Ok(text)
    }

    /// 功能：把 run/start 的 portable user text/image_ref 输入转换为领域内容块。
    ///
    /// 输入：strict DTO 解码后的输入消息。
    /// 输出：保持客户端源顺序的 `ContentBlock::Text`/`ImageRef` 列表。
    /// 不变量：角色必须为 user、内容 1..=128，且至少有一段非空文字或一个图像引用；不读取 artifact 文件。
    /// 失败：角色、数量或全空文字违规返回 `InvalidParams`；引用的 durable/no-follow 校验由 Session 接受边界完成。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn content_blocks(&self) -> Result<Vec<ContentBlock>, crate::error::AgentError> {
        if self.role != "user" || self.content.is_empty() || self.content.len() > 128 {
            return Err(crate::error::AgentError::new(
                crate::error::ErrorCode::InvalidParams,
                "run input must be a non-empty user message",
            ));
        }
        let mut has_effective_content = false;
        let blocks = self
            .content
            .iter()
            .map(|content| match content {
                InputContent::Text { text } => {
                    has_effective_content |= !text.is_empty();
                    ContentBlock::Text { text: text.clone() }
                }
                InputContent::ImageRef { artifact, alt } => {
                    has_effective_content = true;
                    ContentBlock::ImageRef {
                        artifact: artifact.clone(),
                        alt: alt.clone(),
                    }
                }
            })
            .collect::<Vec<_>>();
        if !has_effective_content {
            return Err(crate::error::AgentError::new(
                crate::error::ErrorCode::InvalidParams,
                "run input content must not be empty",
            ));
        }
        Ok(blocks)
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case", deny_unknown_fields)]
pub enum InputContent {
    Text {
        text: String,
    },
    ImageRef {
        artifact: ArtifactRef,
        #[serde(default)]
        alt: Option<String>,
    },
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ProviderSelection {
    pub id: String,
    pub model_id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub api_family: Option<String>,
    #[serde(default)]
    pub extensions: BTreeMap<String, Value>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RunCancelParams {
    pub session_id: String,
    pub run_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ApprovalDecision {
    pub choice: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub extensions: BTreeMap<String, Value>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ApprovalRespondParams {
    pub session_id: String,
    pub run_id: String,
    pub approval_id: String,
    pub decision: ApprovalDecision,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct QueueParams {
    pub session_id: String,
    pub run_id: String,
    pub input: InputMessage,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SessionGetParams {
    pub session_id: String,
    #[serde(default)]
    pub after_seq: u64,
}

/// `session/branch/select` 的严格 expected-head CAS 参数。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SessionBranchSelectParams {
    pub session_id: String,
    pub expected_head_record_id: String,
    pub target_leaf_record_id: String,
}

/// `session/compact` usage 中可选的语言中立金额对象。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SessionCompactCost {
    pub currency: String,
    pub amount: String,
}

/// `session/compact` summary 消息使用的完整 portable usage。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SessionCompactUsage {
    pub input_tokens: u64,
    pub output_tokens: u64,
    pub total_tokens: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cached_input_tokens: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cache_write_tokens: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reasoning_tokens: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cost: Option<SessionCompactCost>,
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub extensions: BTreeMap<String, Value>,
}

/// `session/compact` 两记录 durable mutation 的严格参数。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SessionCompactParams {
    pub session_id: String,
    pub expected_head_record_id: String,
    pub first_retained_record_id: String,
    pub summary_text: String,
    pub provider: ProviderSelection,
    pub usage: SessionCompactUsage,
    pub tokens_before: u64,
    pub tokens_after: u64,
    pub strategy: String,
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::{
        JsonRpcResponse, PROTOCOL_VERSION, RunStartParams, parse_strict_request, parse_strict_value,
    };
    use crate::domain::ContentBlock;

    /// 功能：验证超出 JavaScript 安全整数范围的数值请求 ID 仍按 opaque JSON 值保留。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn preserves_opaque_numeric_id() {
        let response = JsonRpcResponse::success(
            json!(9_007_199_254_740_993_u64),
            json!({
                "protocolVersion": PROTOCOL_VERSION
            }),
        );
        assert_eq!(response.id, json!(9_007_199_254_740_993_u64));
    }

    /// 功能：验证嵌套重复 JSON 键被严格请求解析器拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn rejects_duplicate_object_keys() {
        let error = parse_strict_request(
            r#"{"jsonrpc":"2.0","id":"x","method":"initialize","params":{"x":1,"x":2}}"#,
        )
        .expect_err("duplicate keys must fail");
        assert!(error.to_string().contains("duplicate JSON object key"));
    }

    /// 功能：验证通用严格解析入口拒绝嵌套重复键与 JSON 后的尾随数据。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn strict_value_rejects_duplicates_and_trailing_data() {
        assert!(parse_strict_value(r#"{"outer":{"x":1,"x":2}}"#).is_err());
        assert!(parse_strict_value(r#"{"ok":true} {"extra":true}"#).is_err());
    }

    /// 功能：验证 run/start 保留显式 apiFamily 并按源顺序解码 text/image_ref。
    ///
    /// 输入：符合公共 DTO 的 OpenRouter Images 参数。
    /// 输出：family 精确保留且第二个领域块为 image_ref。
    /// 不变量：协议层只解码 reference，不读取 host path 或 artifact bytes。
    /// 失败：DTO 形状或内容联合漂移使测试失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn decodes_explicit_image_route_and_reference() -> Result<(), Box<dyn std::error::Error>> {
        let params: RunStartParams = serde_json::from_value(json!({
            "sessionId":"s-image",
            "input":{
                "role":"user",
                "content":[
                    {"type":"text","text":"edit"},
                    {"type":"image_ref","artifact":{
                        "artifactId":"artifact-existing",
                        "mediaType":"image/png",
                        "byteLength":15,
                        "sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                    }}
                ]
            },
            "provider":{
                "id":"openrouter",
                "modelId":"google/gemini-2.5-flash-image",
                "apiFamily":"openrouter-images"
            }
        }))?;
        assert_eq!(
            params.provider.api_family.as_deref(),
            Some("openrouter-images")
        );
        let blocks = params.input.content_blocks()?;
        assert!(matches!(blocks.get(1), Some(ContentBlock::ImageRef { .. })));
        Ok(())
    }
}
