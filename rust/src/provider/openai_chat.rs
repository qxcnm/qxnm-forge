use std::collections::BTreeMap;

use async_stream::try_stream;
use async_trait::async_trait;
use futures_util::stream;
use reqwest::header::{AUTHORIZATION, CONTENT_TYPE, HeaderValue};
use serde_json::{Value, json};
use tokio_util::sync::CancellationToken;

use super::sse::{
    build_http_client, next_response_chunk, provider_status_error, send_provider_request,
};
use super::{
    CUSTOM_MAX_IMAGE_COUNT, CUSTOM_MAX_JSON_RESPONSE_BYTES, CUSTOM_MAX_TOTAL_IMAGE_BYTES, Provider,
    ProviderCredentialSource, ProviderRequest, ProviderStream, SseDecoder, SseEvent,
    decode_custom_image_data_url, read_bounded_json_response, resolved_image_data_url,
};
use crate::domain::{ContentBlock, FinishReason, ProviderEvent, Role, Usage};
use crate::error::{AgentError, ErrorCode};
use crate::protocol::parse_strict_value;

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
    image_output: bool,
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
            image_output: false,
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
            image_output: false,
        }
    }

    /// 功能：为显式声明图片输出的自定义连接启用有界非流式响应解析。
    ///
    /// 输入：连接启动快照的 `supportsImageOutput`。
    /// 输出：保持其它 transport 配置不变的 adapter。
    /// 不变量：仅调用方显式开启；关闭时完整保持现有 SSE 行为。
    /// 失败：本方法不执行 I/O 且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn with_image_output(mut self, enabled: bool) -> Self {
        self.image_output = enabled;
        self
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
        let mut body = request_body(&request)?;
        if self.image_output {
            configure_image_output_request(&mut body);
        }
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
        let mut response = send_provider_request(builder, &cancellation).await?;
        if !response.status().is_success() {
            return Err(provider_status_error(&response));
        }
        if self.image_output {
            let raw = read_bounded_json_response(
                &mut response,
                &cancellation,
                CUSTOM_MAX_JSON_RESPONSE_BYTES,
            )
            .await?;
            let text = std::str::from_utf8(&raw).map_err(|_| image_protocol_error())?;
            let value = parse_strict_value(text).map_err(|_| image_protocol_error())?;
            return Ok(Box::pin(stream::iter(
                parse_image_completion(&value)?.into_iter().map(Ok),
            )));
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

/// 功能：严格解析 Chat 非流式文字或 `message.images` 图片完成。
///
/// 输入：已通过 strict JSON 解码的响应对象。
/// 输出：满足 Agent 序列约束的 MessageStart、单一文字或图片完成、可选 usage 与 MessageEnd。
/// 不变量：图片先整批验证；最多八图、总解码字节 4 MiB，不产生图片字节 wire event。
/// 失败：ID、choices、content、data URL、Base64、MIME、魔数、数量或大小无效时返回脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_image_completion(value: &Value) -> Result<Vec<ProviderEvent>, AgentError> {
    let response_id = value
        .get("id")
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
        .ok_or_else(image_protocol_error)?;
    let choices = value
        .get("choices")
        .and_then(Value::as_array)
        .ok_or_else(image_protocol_error)?;
    if choices.len() != 1 {
        return Err(image_protocol_error());
    }
    let choice = &choices[0];
    let message = choice
        .get("message")
        .and_then(Value::as_object)
        .ok_or_else(image_protocol_error)?;
    let final_text = message
        .get("content")
        .and_then(Value::as_str)
        .filter(|text| !text.is_empty())
        .map(str::to_owned);
    let mut images = Vec::new();
    let mut total_bytes = 0_usize;
    if let Some(values) = message.get("images") {
        let values = values.as_array().ok_or_else(image_protocol_error)?;
        if values.len() > CUSTOM_MAX_IMAGE_COUNT {
            return Err(image_output_limit_error());
        }
        for value in values {
            let url = value
                .pointer("/image_url/url")
                .and_then(Value::as_str)
                .ok_or_else(image_protocol_error)?;
            let image = decode_custom_image_data_url(url)?;
            total_bytes = total_bytes
                .checked_add(image.bytes.len())
                .ok_or_else(image_output_limit_error)?;
            if total_bytes > CUSTOM_MAX_TOTAL_IMAGE_BYTES {
                return Err(image_output_limit_error());
            }
            images.push(image);
        }
    }
    if images.is_empty() && final_text.is_none() {
        return Err(image_protocol_error());
    }
    if !images.is_empty() && choice.get("finish_reason").and_then(Value::as_str) != Some("stop") {
        return Err(image_protocol_error());
    }
    let mut events = vec![ProviderEvent::MessageStart {
        provider_message_id: response_id.to_owned(),
    }];
    if images.is_empty() {
        events.push(ProviderEvent::TextDelta {
            text: final_text.expect("checked text"),
        });
    } else {
        events.push(ProviderEvent::ImageCompletion {
            text: final_text,
            images,
        });
    }
    if let Some(usage) = value.get("usage").filter(|usage| usage.is_object()) {
        events.push(ProviderEvent::Usage(parse_non_streaming_usage(usage)));
    }
    events.push(ProviderEvent::MessageEnd {
        finish_reason: finish_reason(
            choice
                .get("finish_reason")
                .and_then(Value::as_str)
                .unwrap_or("stop"),
        ),
    });
    Ok(events)
}

/// 功能：归一化 Chat 非流式 usage，缺失计数按零处理。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_non_streaming_usage(value: &Value) -> Usage {
    let input_tokens = value
        .get("prompt_tokens")
        .and_then(Value::as_u64)
        .unwrap_or(0);
    let output_tokens = value
        .get("completion_tokens")
        .and_then(Value::as_u64)
        .unwrap_or(0);
    Usage {
        input_tokens,
        output_tokens,
        total_tokens: value
            .get("total_tokens")
            .and_then(Value::as_u64)
            .unwrap_or(0),
        cached_input_tokens: value
            .pointer("/prompt_tokens_details/cached_tokens")
            .and_then(Value::as_u64)
            .unwrap_or(0),
        cost_micros: None,
    }
}

/// 功能：把 Chat 请求收窄为独占图片输出的非流式协议形状。
///
/// 输入：已经由公共消息映射器构造的 Chat 请求对象。
/// 输出：原对象就地设为 `stream:false`、`modalities:["text","image"]`，并移除 function tools/stream options。
/// 不变量：图片输出 route 不会携带 Agent function tools；模型、消息和输出上限保持不变。
/// 失败：调用方始终传入对象，本方法不执行 I/O 且不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn configure_image_output_request(body: &mut Value) {
    body["stream"] = Value::Bool(false);
    if let Some(object) = body.as_object_mut() {
        object.remove("stream_options");
        object.remove("tools");
    }
    body["modalities"] = json!(["text", "image"]);
}

/// 功能：构造不含 Provider 正文的 Chat 图片协议错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn image_protocol_error() -> AgentError {
    AgentError::new(
        ErrorCode::ProviderError,
        "provider image response is invalid",
    )
    .with_details(json!({"kind":"provider_protocol_error"}))
}

/// 功能：构造 Chat 图片批次超过数量或总字节上限的错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn image_output_limit_error() -> AgentError {
    AgentError::new(
        ErrorCode::OutputLimitExceeded,
        "provider image response exceeded the byte limit",
    )
    .with_details(json!({"kind":"provider_output_limit"}))
}

/// 功能：把公共消息、工具和输出上限转换为 Chat Completions 请求 JSON。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(super) fn request_body(request: &ProviderRequest) -> Result<Value, AgentError> {
    let mut messages = Vec::new();
    if let Some(instructions) = &request.system_instructions {
        messages.push(json!({"role":"system","content":instructions}));
    }
    messages.extend(
        request
            .messages
            .iter()
            .map(|message| -> Result<Option<Value>, AgentError> {
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
                let has_text = message
                    .content
                    .iter()
                    .any(|block| matches!(block, ContentBlock::Text { .. }));
                let mut native_content = message
                    .content
                    .iter()
                    .filter_map(|block| match block {
                        ContentBlock::Text { text } => Some(Ok(json!({"type":"text","text":text}))),
                        ContentBlock::ImageRef { artifact, .. }
                            if message.role == Role::User
                                && request
                                    .resolved_images
                                    .iter()
                                    .any(|image| image.reference == *artifact) =>
                        {
                            Some(
                                resolved_image_data_url(request, artifact)
                                    .map(|url| json!({"type":"image_url","image_url":{"url":url}})),
                            )
                        }
                        _ => None,
                    })
                    .collect::<Result<Vec<_>, AgentError>>()?;
                let has_image = message.role == Role::User
                    && message.content.iter().any(|block| match block {
                        ContentBlock::ImageRef { artifact, .. } => request
                            .resolved_images
                            .iter()
                            .any(|image| image.reference == *artifact),
                        _ => false,
                    });
                let content = if has_image {
                    Value::Array(std::mem::take(&mut native_content))
                } else {
                    Value::String(text)
                };
                let mut value = json!({"role": role, "content": content});
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
                let has_tool_calls = !tool_calls.is_empty();
                if has_tool_calls {
                    value["tool_calls"] = Value::Array(tool_calls);
                }
                let tool_result = message
                    .content
                    .iter()
                    .find(|block| matches!(block, ContentBlock::ToolResult { .. }));
                if let Some(ContentBlock::ToolResult {
                    call_id, output, ..
                }) = tool_result
                {
                    value["tool_call_id"] = Value::String(call_id.clone());
                    value["content"] = Value::String(
                        output
                            .text
                            .clone()
                            .unwrap_or_else(|| "tool result stored as artifact".to_owned()),
                    );
                }
                if !has_text && !has_image && !has_tool_calls && tool_result.is_none() {
                    return Ok(None);
                }
                Ok(Some(value))
            })
            .collect::<Result<Vec<_>, AgentError>>()?
            .into_iter()
            .flatten(),
    );
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

    use base64::Engine as _;
    use base64::engine::general_purpose::STANDARD as BASE64_STANDARD;
    use chrono::Utc;
    use serde_json::json;

    use super::{
        configure_image_output_request, parse_event, parse_image_completion, request_body,
    };
    use crate::domain::{ArtifactRef, ContentBlock, FinishReason, Message, ProviderEvent, Role};
    use crate::error::ErrorCode;
    use crate::provider::{ProviderRequest, SseEvent};

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

    /// 功能：验证 Chat 图片输出请求关闭流式与 function tools，并只声明 text/image modalities。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn image_output_request_is_non_streaming_and_tool_narrowed() {
        let mut body = json!({
            "model":"fixture-model",
            "messages":[],
            "stream":true,
            "stream_options":{"include_usage":true},
            "tools":[{"type":"function","function":{"name":"file.read"}}]
        });
        configure_image_output_request(&mut body);
        assert_eq!(body["stream"], false);
        assert_eq!(body["modalities"], json!(["text", "image"]));
        assert!(body.get("stream_options").is_none());
        assert!(body.get("tools").is_none());
    }

    /// 功能：验证 Chat 非流式纯文字保留 usage，并拒绝空完成、非 stop 图片和非单 choice。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn validates_non_streaming_text_image_finish_and_usage() {
        let text_events = parse_image_completion(&json!({
            "id":"chat-text-1",
            "choices":[{
                "message":{"content":"完成"},
                "finish_reason":"stop"
            }],
            "usage":{"prompt_tokens":2,"completion_tokens":3}
        }))
        .expect("valid pure text completion");
        assert!(matches!(
            text_events.as_slice(),
            [
                ProviderEvent::MessageStart { .. },
                ProviderEvent::TextDelta { text },
                ProviderEvent::Usage(usage),
                ProviderEvent::MessageEnd { finish_reason: FinishReason::Stop }
            ] if text == "完成"
                && usage.input_tokens == 2
                && usage.output_tokens == 3
                && usage.total_tokens == 0
        ));

        let encoded = BASE64_STANDARD.encode(b"\x89PNG\r\n\x1a\nfixture");
        let invalid_finish = json!({
            "id":"chat-image-1",
            "choices":[{
                "message":{"content":null,"images":[{
                    "image_url":{"url":format!("data:image/png;base64,{encoded}")}
                }]},
                "finish_reason":"length"
            }]
        });
        assert!(parse_image_completion(&invalid_finish).is_err());
        for invalid in [
            json!({"id":"chat-empty","choices":[{"message":{"content":""},"finish_reason":"stop"}]}),
            json!({"id":"chat-zero","choices":[]}),
            json!({"id":"chat-two","choices":[{},{}]}),
        ] {
            assert!(parse_image_completion(&invalid).is_err());
        }
    }

    /// 功能：验证 output-only/text-only 后续轮次会保留历史文字并省略所有未解析图片引用。
    ///
    /// 不变量：assistant 输出图片永不回送为 user image_url；切换到不支持图片输入的 route 时，历史 user 图片也不会使请求序列化失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn omits_historical_images_when_route_cannot_accept_input() {
        let artifact = ArtifactRef {
            artifact_id: "artifact-history-chat".to_owned(),
            media_type: "image/png".to_owned(),
            byte_length: 16,
            sha256: "0".repeat(64),
            display_name: None,
            extensions: BTreeMap::new(),
        };
        let request = ProviderRequest {
            model: "fixture-model".to_owned(),
            system_instructions: None,
            messages: vec![
                Message {
                    id: "message-user-image".to_owned(),
                    role: Role::User,
                    content: vec![
                        ContentBlock::Text {
                            text: "生成一张图".to_owned(),
                        },
                        ContentBlock::ImageRef {
                            artifact: artifact.clone(),
                            alt: None,
                        },
                    ],
                    created_at: Utc::now(),
                },
                Message {
                    id: "message-assistant-image".to_owned(),
                    role: Role::Assistant,
                    content: vec![
                        ContentBlock::Text {
                            text: "图片已生成".to_owned(),
                        },
                        ContentBlock::ImageRef {
                            artifact: artifact.clone(),
                            alt: None,
                        },
                    ],
                    created_at: Utc::now(),
                },
                Message {
                    id: "message-assistant-image-only".to_owned(),
                    role: Role::Assistant,
                    content: vec![ContentBlock::ImageRef {
                        artifact: artifact.clone(),
                        alt: None,
                    }],
                    created_at: Utc::now(),
                },
                Message {
                    id: "message-user-image-only".to_owned(),
                    role: Role::User,
                    content: vec![ContentBlock::ImageRef {
                        artifact,
                        alt: None,
                    }],
                    created_at: Utc::now(),
                },
                Message::text("message-next", Role::User, "继续说明"),
            ],
            tools: Vec::new(),
            max_output_tokens: None,
            session_id: None,
            run_id: None,
            resolved_images: Vec::new(),
        };

        let body = request_body(&request).expect("historical images must be safely omitted");
        assert!(!body.to_string().contains("image_url"));
        assert_eq!(body["messages"][0]["content"], "生成一张图");
        assert_eq!(body["messages"][1]["content"], "图片已生成");
        assert_eq!(body["messages"][2]["content"], "继续说明");
        assert_eq!(body["messages"].as_array().map(Vec::len), Some(3));
    }
}
