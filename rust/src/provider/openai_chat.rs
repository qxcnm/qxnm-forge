use std::collections::BTreeMap;

use async_stream::try_stream;
use async_trait::async_trait;
use reqwest::header::{AUTHORIZATION, CONTENT_TYPE, HeaderValue};
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

/// OpenAI Chat Completions API family 的独立原生 Rust Provider。
///
/// 不变量：认证密钥不会通过 `Debug` 暴露；实例不会自动跟随 HTTP 重定向。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Clone)]
pub struct OpenAiChatProvider {
    id: String,
    client: Result<reqwest::Client, AgentError>,
    endpoint: String,
    credential_source: ProviderCredentialSource,
}

impl OpenAiChatProvider {
    /// 功能：创建 OpenAI Chat Completions API family 的原生 HTTP Provider。
    ///
    /// 输入：注册标识、完整 API endpoint 和可选 Bearer credential 环境名称。
    /// 输出：持有独立 Rust HTTP 客户端或脱敏初始化错误的 Provider 实例。
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
        }
    }

    /// 功能：为本地已确认推广 route 创建按请求读取 stored credential 的 Chat adapter。
    ///
    /// 输入：固定 Provider ID、完整同源 endpoint 和不持有 secret 的来源对象。
    /// 输出：复用既有 Chat request/SSE parser 的 Provider。
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
        }
    }
}

#[async_trait]
impl Provider for OpenAiChatProvider {
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

    /// 功能：调用 Chat Completions 流式接口并规范化文本、推理、工具和用量事件。
    ///
    /// 输入：通用 Provider 请求和运行级取消令牌。
    /// 输出：解析 SSE 后按服务器顺序产生的统一事件流。
    /// 不变量：认证信息仅进入最终请求头；禁止自动重定向；单 SSE 事件限制为 4 MiB。
    /// 失败：网络、状态、idle timeout、SSE/JSON、工具顺序或取消返回脱敏稳定错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn stream(
        &self,
        request: ProviderRequest,
        cancellation: CancellationToken,
    ) -> Result<ProviderStream, AgentError> {
        let body = request_body(&request)?;
        let client = match &self.client {
            Ok(client) => client,
            Err(error) => return Err(error.clone()),
        };
        let api_key = self.credential_source.read_for_request()?;
        let authorization = HeaderValue::from_str(&format!("Bearer {api_key}")).map_err(|_| {
            AgentError::new(
                ErrorCode::ProviderUnavailable,
                "provider credential is invalid",
            )
        })?;
        let builder = client
            .post(&self.endpoint)
            .header(CONTENT_TYPE, "application/json")
            .header(AUTHORIZATION, authorization)
            .json(&body);
        drop(api_key);
        let response = send_provider_request(builder, &cancellation).await?;
        if !response.status().is_success() {
            return Err(provider_status_error(&response));
        }

        let output = try_stream! {
            let mut response = response;
            let mut decoder = SseDecoder::new(4 * 1024 * 1024);
            let mut calls = BTreeMap::<u64, (String, String)>::new();
            while let Some(chunk) = next_response_chunk(&mut response, &cancellation).await? {
                for event in decoder.push(&chunk)? {
                    for normalized in parse_event(&event, &mut calls)? {
                        yield normalized;
                    }
                }
            }
            for event in decoder.finish()? {
                for normalized in parse_event(&event, &mut calls)? {
                    yield normalized;
                }
            }
        };
        Ok(Box::pin(output))
    }
}

/// 功能：把公共消息、工具和输出上限转换为 Chat Completions 请求 JSON。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(super) fn request_body(request: &ProviderRequest) -> Result<Value, AgentError> {
    let messages = request
        .messages
        .iter()
        .map(|message| {
            let role = match message.role {
                Role::System => "system",
                Role::User => "user",
                Role::Assistant => "assistant",
                Role::Tool => "tool",
            };
            let text = message
                .content
                .iter()
                .filter_map(|block| match block {
                    ContentBlock::Text { text } => Some(text.as_str()),
                    _ => None,
                })
                .collect::<Vec<_>>()
                .join("\n");
            let mut value = json!({"role": role, "content": text});
            let tool_calls = message
                .content
                .iter()
                .filter_map(|block| match block {
                    ContentBlock::ToolCall {
                        call_id,
                        name,
                        arguments,
                    } => Some(json!({
                        "id": call_id,
                        "type": "function",
                        "function": {"name": name, "arguments": arguments.to_string()}
                    })),
                    _ => None,
                })
                .collect::<Vec<_>>();
            if !tool_calls.is_empty() {
                value["tool_calls"] = Value::Array(tool_calls);
            }
            if let Some(ContentBlock::ToolResult {
                call_id, output, ..
            }) = message
                .content
                .iter()
                .find(|block| matches!(block, ContentBlock::ToolResult { .. }))
            {
                value["tool_call_id"] = Value::String(call_id.clone());
                value["content"] = Value::String(
                    output
                        .text
                        .clone()
                        .unwrap_or_else(|| "tool result stored as artifact".to_owned()),
                );
            }
            value
        })
        .collect::<Vec<_>>();
    let tools = request
        .tools
        .iter()
        .map(|tool| {
            json!({
                "type":"function",
                "function":{
                    "name":tool.name,
                    "description":tool.description,
                    "parameters":tool.input_schema
                }
            })
        })
        .collect::<Vec<_>>();
    let mut body = json!({
        "model": request.model,
        "messages": messages,
        "stream": true,
        "stream_options": {"include_usage": true}
    });
    if !tools.is_empty() {
        body["tools"] = Value::Array(tools);
    }
    if let Some(limit) = request.max_output_tokens {
        body["max_completion_tokens"] = json!(limit);
    }
    Ok(body)
}

/// 功能：解析单个 Chat Completions SSE 事件并维护分片工具调用身份映射。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(super) fn parse_event(
    event: &SseEvent,
    calls: &mut BTreeMap<u64, (String, String)>,
) -> Result<Vec<ProviderEvent>, AgentError> {
    if event.data.trim() == "[DONE]" || event.data.trim().is_empty() {
        return Ok(Vec::new());
    }
    let value: Value = serde_json::from_str(&event.data).map_err(|error| {
        AgentError::new(
            ErrorCode::StreamInterrupted,
            format!("invalid OpenAI chat stream JSON: {error}"),
        )
    })?;
    if value.get("error").is_some() {
        return Err(AgentError::new(
            ErrorCode::ProviderError,
            "provider stream reported an error",
        ));
    }
    let mut output = Vec::new();
    if let Some(id) = value.get("id").and_then(Value::as_str)
        && value
            .get("choices")
            .and_then(Value::as_array)
            .is_some_and(|choices| {
                choices.iter().any(|choice| {
                    choice.pointer("/delta/role").and_then(Value::as_str) == Some("assistant")
                })
            })
    {
        output.push(ProviderEvent::MessageStart {
            provider_message_id: id.to_owned(),
        });
    }
    if let Some(choices) = value.get("choices").and_then(Value::as_array) {
        for choice in choices {
            if let Some(text) = choice.pointer("/delta/content").and_then(Value::as_str)
                && !text.is_empty()
            {
                output.push(ProviderEvent::TextDelta {
                    text: text.to_owned(),
                });
            }
            if let Some(text) = choice
                .pointer("/delta/reasoning_content")
                .and_then(Value::as_str)
                && !text.is_empty()
            {
                output.push(ProviderEvent::ReasoningDelta {
                    text: text.to_owned(),
                });
            }
            if let Some(tool_calls) = choice
                .pointer("/delta/tool_calls")
                .and_then(Value::as_array)
            {
                for call in tool_calls {
                    let index = call.get("index").and_then(Value::as_u64).ok_or_else(|| {
                        AgentError::new(
                            ErrorCode::StreamInterrupted,
                            "tool call delta missing index",
                        )
                    })?;
                    let id = call.get("id").and_then(Value::as_str);
                    let name = call.pointer("/function/name").and_then(Value::as_str);
                    if let (Some(id), Some(name)) = (id, name) {
                        calls.insert(index, (id.to_owned(), name.to_owned()));
                        output.push(ProviderEvent::ToolCallStart {
                            call_id: id.to_owned(),
                            name: name.to_owned(),
                        });
                    }
                    if let Some(delta) = call.pointer("/function/arguments").and_then(Value::as_str)
                        && !delta.is_empty()
                    {
                        let call_id =
                            calls.get(&index).map(|(id, _)| id.clone()).ok_or_else(|| {
                                AgentError::new(
                                    ErrorCode::StreamInterrupted,
                                    "tool arguments arrived before tool identity",
                                )
                            })?;
                        output.push(ProviderEvent::ToolCallArgumentsDelta {
                            call_id,
                            delta: delta.to_owned(),
                        });
                    }
                }
            }
            if let Some(reason) = choice.get("finish_reason").and_then(Value::as_str) {
                if reason == "tool_calls" {
                    for (call_id, _) in calls.values() {
                        output.push(ProviderEvent::ToolCallEnd {
                            call_id: call_id.clone(),
                        });
                    }
                }
                output.push(ProviderEvent::MessageEnd {
                    finish_reason: finish_reason(reason),
                });
            }
        }
    }
    if let Some(usage) = value.get("usage") {
        output.push(ProviderEvent::Usage(Usage {
            input_tokens: usage
                .get("prompt_tokens")
                .and_then(Value::as_u64)
                .unwrap_or(0),
            output_tokens: usage
                .get("completion_tokens")
                .and_then(Value::as_u64)
                .unwrap_or(0),
            total_tokens: usage
                .get("total_tokens")
                .and_then(Value::as_u64)
                .unwrap_or(0),
            cached_input_tokens: usage
                .pointer("/prompt_tokens_details/cached_tokens")
                .and_then(Value::as_u64)
                .unwrap_or(0),
            cost_micros: None,
        }));
    }
    Ok(output)
}

/// 功能：将 Chat Completions 完成原因归一化为公共结束原因枚举。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn finish_reason(value: &str) -> FinishReason {
    match value {
        "stop" => FinishReason::Stop,
        "tool_calls" | "function_call" => FinishReason::ToolCalls,
        "length" => FinishReason::Length,
        "content_filter" => FinishReason::Error,
        _ => FinishReason::Error,
    }
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeMap;

    use serde_json::json;

    use super::parse_event;
    use crate::domain::ProviderEvent;
    use crate::error::ErrorCode;
    use crate::provider::SseEvent;

    /// 功能：验证跨 SSE 事件到达的工具参数片段被归属到同一调用并正确结束。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn normalizes_partial_tool_arguments() -> Result<(), crate::error::AgentError> {
        let mut calls = BTreeMap::new();
        let first = parse_event(
            &SseEvent {
                event: None,
                id: None,
                data: r#"{"id":"m1","choices":[{"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"c1","function":{"name":"read","arguments":"{\"pa"}}]},"finish_reason":null}]}"#.to_owned(),
            },
            &mut calls,
        )?;
        let second = parse_event(
            &SseEvent {
                event: None,
                id: None,
                data: r#"{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"x\"}"}}]},"finish_reason":"tool_calls"}]}"#.to_owned(),
            },
            &mut calls,
        )?;
        assert!(
            first
                .iter()
                .any(|event| matches!(event, ProviderEvent::ToolCallStart { .. }))
        );
        assert!(
            second
                .iter()
                .any(|event| matches!(event, ProviderEvent::ToolCallEnd { .. }))
        );
        Ok(())
    }

    /// 功能：验证 Chat Completions 流错误不会把 Provider 敏感消息带入公共错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn redacts_stream_error_message() {
        let result = parse_event(
            &SseEvent {
                event: Some("message".to_owned()),
                id: None,
                data: json!({"error":{"message":"qxnm-forge-secret-canary-chat"}}).to_string(),
            },
            &mut BTreeMap::new(),
        );
        let Err(error) = result else {
            panic!("provider stream error was not rejected");
        };
        assert_eq!(error.code, ErrorCode::ProviderError);
        assert!(!error.message.contains("qxnm-forge-secret-canary-chat"));
    }
}
