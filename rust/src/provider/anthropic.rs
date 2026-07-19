use std::collections::BTreeMap;

use async_stream::try_stream;
use async_trait::async_trait;
use reqwest::header::{CONTENT_TYPE, HeaderValue};
use serde_json::{Value, json};
use tokio_util::sync::CancellationToken;

use super::sse::{
    build_http_client, next_response_chunk, provider_status_error, send_provider_request,
};
use super::{
    Provider, ProviderCredentialSource, ProviderRequest, ProviderStream, SseDecoder, SseEvent,
};
use crate::domain::{ContentBlock, FinishReason, ProviderEvent, Role, Usage};
use crate::error::{AgentError, ErrorCode};

/// Anthropic Messages API family 的独立原生 Rust Provider。
///
/// 不变量：认证密钥不会通过 `Debug` 暴露；实例不会自动跟随 HTTP 重定向。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Clone)]
pub struct AnthropicMessagesProvider {
    id: String,
    client: Result<reqwest::Client, AgentError>,
    endpoint: String,
    credential_source: ProviderCredentialSource,
    api_version: String,
}

impl AnthropicMessagesProvider {
    /// 功能：创建 Anthropic Messages API family 的原生 HTTP Provider。
    ///
    /// 输入：注册标识、完整 API endpoint 和可选 `x-api-key` credential 环境名称。
    /// 输出：持有固定 API 版本、独立客户端或脱敏初始化错误的 Provider 实例。
    /// 不变量：实例只保存环境名称，不保存 credential 值；值仅在 `stream` 请求边界读取。
    /// 失败：本方法不发起网络请求；客户端、endpoint 或认证错误在调用 `stream` 时报告。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn new(
        id: impl Into<String>,
        endpoint: impl Into<String>,
        credential_env: Option<String>,
    ) -> Self {
        Self {
            id: id.into(),
            client: build_http_client(),
            endpoint: endpoint.into(),
            credential_source: ProviderCredentialSource::from_environment(credential_env),
            api_version: "2023-06-01".to_owned(),
        }
    }

    /// 功能：为本地已确认推广 route 创建按请求读取 stored credential 的 Messages adapter。
    ///
    /// 输入：固定 Provider ID、完整同源 endpoint 和不持有 secret 的来源对象。
    /// 输出：复用既有 Anthropic request/SSE parser 的 Provider。
    /// 不变量：adapter 字段不保存 API key；stored source 失败不回退环境变量。
    /// 失败：本方法不联网；endpoint、credential 或 transport 错误在请求边界脱敏返回。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn with_credential_source(
        id: impl Into<String>,
        endpoint: impl Into<String>,
        credential_source: ProviderCredentialSource,
    ) -> Self {
        Self {
            id: id.into(),
            client: build_http_client(),
            endpoint: endpoint.into(),
            credential_source,
            api_version: "2023-06-01".to_owned(),
        }
    }
}

#[async_trait]
impl Provider for AnthropicMessagesProvider {
    /// 功能：返回构造时配置的 Provider 注册标识。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn id(&self) -> &str {
        &self.id
    }

    /// 功能：在 Session durable 副作用前判断环境或 stored credential 当前可用。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn is_available(&self) -> bool {
        self.credential_source.is_available()
    }

    /// 功能：调用 Anthropic Messages 流式接口并规范化文本、推理、工具和用量事件。
    ///
    /// 输入：通用 Provider 请求和运行级取消令牌。
    /// 输出：解析 SSE 后按服务器顺序产生的统一事件流。
    /// 不变量：认证信息仅进入最终 `x-api-key` 头；禁止重定向；单 SSE 事件限制为 4 MiB。
    /// 失败：网络、状态、idle timeout、SSE/JSON、工具顺序或取消返回脱敏稳定错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn stream(
        &self,
        request: ProviderRequest,
        cancellation: CancellationToken,
    ) -> Result<ProviderStream, AgentError> {
        let client = match &self.client {
            Ok(client) => client,
            Err(error) => return Err(error.clone()),
        };
        let api_key = self.credential_source.read_for_request()?;
        let credential = HeaderValue::from_str(&api_key).map_err(|_| {
            AgentError::new(
                ErrorCode::ProviderUnavailable,
                "provider credential is invalid",
            )
        })?;
        let builder = client
            .post(&self.endpoint)
            .header(CONTENT_TYPE, "application/json")
            .header("anthropic-version", &self.api_version)
            .header("x-api-key", credential)
            .json(&request_body(&request));
        drop(api_key);
        let response = send_provider_request(builder, &cancellation).await?;
        if !response.status().is_success() {
            return Err(provider_status_error(&response));
        }
        let output = try_stream! {
            let mut response = response;
            let mut decoder = SseDecoder::new(4 * 1024 * 1024);
            let mut calls = BTreeMap::<u64, String>::new();
            let mut ended = false;
            while let Some(chunk) = next_response_chunk(&mut response, &cancellation).await? {
                for event in decoder.push(&chunk)? {
                    for normalized in parse_event(&event, &mut calls, &mut ended)? { yield normalized; }
                }
            }
            for event in decoder.finish()? {
                for normalized in parse_event(&event, &mut calls, &mut ended)? { yield normalized; }
            }
        };
        Ok(Box::pin(output))
    }
}

/// 功能：把公共消息、工具和输出上限转换为 Anthropic Messages 请求 JSON。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn request_body(request: &ProviderRequest) -> Value {
    let system = request
        .messages
        .iter()
        .filter(|message| message.role == Role::System)
        .flat_map(|message| &message.content)
        .filter_map(|block| match block {
            ContentBlock::Text { text } => Some(text.as_str()),
            _ => None,
        })
        .collect::<Vec<_>>()
        .join("\n");
    let messages = request
        .messages
        .iter()
        .filter(|message| message.role != Role::System)
        .map(|message| {
            let role = if message.role == Role::Assistant { "assistant" } else { "user" };
            let content = message
                .content
                .iter()
                .filter_map(|block| match block {
                    ContentBlock::Text { text } => Some(json!({"type":"text","text":text})),
                    ContentBlock::ToolCall { call_id, name, arguments } => Some(json!({
                        "type":"tool_use","id":call_id,"name":name,"input":arguments
                    })),
                    ContentBlock::ToolResult { call_id, output, is_error, .. } => Some(json!({
                        "type":"tool_result",
                        "tool_use_id":call_id,
                        "content":output.text.as_deref().unwrap_or("tool result stored as artifact"),
                        "is_error":is_error
                    })),
                    _ => None,
                })
                .collect::<Vec<_>>();
            json!({"role":role,"content":content})
        })
        .collect::<Vec<_>>();
    let tools = request
        .tools
        .iter()
        .map(|tool| {
            json!({
                "name":tool.name,"description":tool.description,"input_schema":tool.input_schema
            })
        })
        .collect::<Vec<_>>();
    let mut body = json!({
        "model":request.model,
        "messages":messages,
        "max_tokens":request.max_output_tokens.unwrap_or(4096),
        "stream":true
    });
    if !system.is_empty() {
        body["system"] = Value::String(system);
    }
    if !tools.is_empty() {
        body["tools"] = Value::Array(tools);
    }
    body
}

/// 功能：解析单个 Anthropic SSE 事件并维护内容块索引到工具调用 ID 的映射。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_event(
    event: &SseEvent,
    calls: &mut BTreeMap<u64, String>,
    ended: &mut bool,
) -> Result<Vec<ProviderEvent>, AgentError> {
    if event.data.trim().is_empty() {
        return Ok(Vec::new());
    }
    let value: Value = serde_json::from_str(&event.data).map_err(|error| {
        AgentError::new(
            ErrorCode::StreamInterrupted,
            format!("invalid Anthropic stream JSON: {error}"),
        )
    })?;
    let kind = value
        .get("type")
        .and_then(Value::as_str)
        .or(event.event.as_deref())
        .unwrap_or("");
    let mut output = Vec::new();
    match kind {
        "message_start" => {
            if let Some(id) = value.pointer("/message/id").and_then(Value::as_str) {
                output.push(ProviderEvent::MessageStart {
                    provider_message_id: id.to_owned(),
                });
            }
            if let Some(input) = value
                .pointer("/message/usage/input_tokens")
                .and_then(Value::as_u64)
            {
                output.push(ProviderEvent::Usage(Usage {
                    input_tokens: input,
                    total_tokens: input,
                    ..Usage::default()
                }));
            }
        }
        "content_block_start" => {
            let index = value.get("index").and_then(Value::as_u64).unwrap_or(0);
            if value.pointer("/content_block/type").and_then(Value::as_str) == Some("tool_use") {
                let id = value
                    .pointer("/content_block/id")
                    .and_then(Value::as_str)
                    .ok_or_else(|| {
                        AgentError::new(ErrorCode::StreamInterrupted, "tool use missing id")
                    })?;
                let name = value
                    .pointer("/content_block/name")
                    .and_then(Value::as_str)
                    .ok_or_else(|| {
                        AgentError::new(ErrorCode::StreamInterrupted, "tool use missing name")
                    })?;
                calls.insert(index, id.to_owned());
                output.push(ProviderEvent::ToolCallStart {
                    call_id: id.to_owned(),
                    name: name.to_owned(),
                });
            }
        }
        "content_block_delta" => match value.pointer("/delta/type").and_then(Value::as_str) {
            Some("text_delta") => {
                if let Some(text) = value.pointer("/delta/text").and_then(Value::as_str) {
                    output.push(ProviderEvent::TextDelta {
                        text: text.to_owned(),
                    });
                }
            }
            Some("thinking_delta") => {
                if let Some(text) = value.pointer("/delta/thinking").and_then(Value::as_str) {
                    output.push(ProviderEvent::ReasoningDelta {
                        text: text.to_owned(),
                    });
                }
            }
            Some("input_json_delta") => {
                let index = value.get("index").and_then(Value::as_u64).unwrap_or(0);
                let call_id = calls.get(&index).ok_or_else(|| {
                    AgentError::new(
                        ErrorCode::StreamInterrupted,
                        "tool arguments arrived before tool start",
                    )
                })?;
                let delta = value
                    .pointer("/delta/partial_json")
                    .and_then(Value::as_str)
                    .unwrap_or("");
                output.push(ProviderEvent::ToolCallArgumentsDelta {
                    call_id: call_id.clone(),
                    delta: delta.to_owned(),
                });
            }
            _ => {}
        },
        "content_block_stop" => {
            let index = value.get("index").and_then(Value::as_u64).unwrap_or(0);
            if let Some(call_id) = calls.get(&index) {
                output.push(ProviderEvent::ToolCallEnd {
                    call_id: call_id.clone(),
                });
            }
        }
        "message_delta" => {
            if let Some(count) = value
                .pointer("/usage/output_tokens")
                .and_then(Value::as_u64)
            {
                output.push(ProviderEvent::Usage(Usage {
                    output_tokens: count,
                    total_tokens: count,
                    ..Usage::default()
                }));
            }
            if let Some(reason) = value.pointer("/delta/stop_reason").and_then(Value::as_str) {
                output.push(ProviderEvent::MessageEnd {
                    finish_reason: match reason {
                        "end_turn" | "stop_sequence" => FinishReason::Stop,
                        "tool_use" => FinishReason::ToolCalls,
                        "max_tokens" => FinishReason::Length,
                        "refusal" => FinishReason::Error,
                        _ => FinishReason::Error,
                    },
                });
                *ended = true;
            }
        }
        "message_stop" if !*ended => {
            output.push(ProviderEvent::MessageEnd {
                finish_reason: FinishReason::Stop,
            });
            *ended = true;
        }
        "error" => {
            return Err(AgentError::new(
                ErrorCode::ProviderError,
                "provider stream reported an error",
            ));
        }
        _ => {}
    }
    Ok(output)
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeMap;

    use serde_json::json;

    use super::parse_event;
    use crate::domain::ProviderEvent;
    use crate::error::ErrorCode;
    use crate::provider::SseEvent;

    /// 功能：验证 Anthropic 的 partial_json 工具参数按内容块索引归属并正确结束。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn normalizes_partial_tool_arguments() -> Result<(), crate::error::AgentError> {
        let mut calls = BTreeMap::new();
        let mut ended = false;
        let started = parse_event(
            &SseEvent {
                event: Some("content_block_start".to_owned()),
                data: json!({
                    "type":"content_block_start",
                    "index":1,
                    "content_block":{"type":"tool_use","id":"call-1","name":"read"}
                })
                .to_string(),
                id: None,
            },
            &mut calls,
            &mut ended,
        )?;
        let delta = parse_event(
            &SseEvent {
                event: Some("content_block_delta".to_owned()),
                data: json!({
                    "type":"content_block_delta",
                    "index":1,
                    "delta":{"type":"input_json_delta","partial_json":"{\"path\":\"x\"}"}
                })
                .to_string(),
                id: None,
            },
            &mut calls,
            &mut ended,
        )?;
        let stopped = parse_event(
            &SseEvent {
                event: Some("content_block_stop".to_owned()),
                data: json!({"type":"content_block_stop","index":1}).to_string(),
                id: None,
            },
            &mut calls,
            &mut ended,
        )?;
        assert!(matches!(
            started.as_slice(),
            [ProviderEvent::ToolCallStart { call_id, name }]
                if call_id == "call-1" && name == "read"
        ));
        assert!(matches!(
            delta.as_slice(),
            [ProviderEvent::ToolCallArgumentsDelta { call_id, delta }]
                if call_id == "call-1" && delta == "{\"path\":\"x\"}"
        ));
        assert!(matches!(
            stopped.as_slice(),
            [ProviderEvent::ToolCallEnd { call_id }] if call_id == "call-1"
        ));
        Ok(())
    }

    /// 功能：验证 Anthropic 流错误不会把 Provider 返回的敏感消息带入公共错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn redacts_stream_error_message() {
        let result = parse_event(
            &SseEvent {
                event: Some("error".to_owned()),
                data: json!({
                    "type":"error",
                    "error":{"message":"qxnm-forge-secret-canary-anthropic"}
                })
                .to_string(),
                id: None,
            },
            &mut BTreeMap::new(),
            &mut false,
        );
        let Err(error) = result else {
            panic!("provider stream error was not rejected");
        };
        assert_eq!(error.code, ErrorCode::ProviderError);
        assert!(!error.message.contains("qxnm-forge-secret-canary-anthropic"));
    }
}
