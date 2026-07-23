use async_stream::try_stream;
use async_trait::async_trait;
use reqwest::header::{CONTENT_TYPE, HeaderValue};
use serde_json::{Value, json};
use tokio_util::sync::CancellationToken;

use super::sse::{
    build_http_client, native_endpoint, next_response_chunk, provider_status_error,
    send_provider_request,
};
use super::{
    Provider, ProviderRequest, ProviderStream, SseDecoder, SseEvent, credential_from_environment,
};
use crate::domain::{ContentBlock, FinishReason, ProviderEvent, Role, Usage};
use crate::error::{AgentError, ErrorCode};

/// Google Generative AI API family 的独立原生 Rust Provider。
///
/// 不变量：使用 REST `streamGenerateContent?alt=sse`，不把 Google 流伪装为裸 chunked JSON。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Clone)]
pub struct GoogleGenerativeAiProvider {
    id: String,
    client: Result<reqwest::Client, AgentError>,
    base_url: String,
    credential_env: Option<String>,
}

impl GoogleGenerativeAiProvider {
    /// 功能：创建 Google Generative AI 原生 Provider。
    ///
    /// 输入：注册 ID、包含 `/v1beta` 的 base URL 及可选 Gemini credential 环境名称。
    /// 输出：持有独立 reqwest 客户端且尚未发起请求的 Provider。
    /// 不变量：模型 ID 在每次请求边界验证后才进入 path；密钥不参与 Debug 或持久化。
    /// 失败：客户端、base、模型或凭据错误在 `stream` 返回稳定脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn new(
        id: impl Into<String>,
        base_url: impl Into<String>,
        credential_env: Option<String>,
    ) -> Self {
        Self {
            id: id.into(),
            client: build_http_client(),
            base_url: base_url.into(),
            credential_env,
        }
    }
}

#[async_trait]
impl Provider for GoogleGenerativeAiProvider {
    /// 功能：返回 Google Provider 的稳定注册 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn id(&self) -> &str {
        &self.id
    }

    /// 功能：调用 Google streamGenerateContent SSE 并规范化文本、推理、工具和 usage。
    ///
    /// 输入：公共 Provider 请求和运行取消令牌。
    /// 输出：按 GenerateContentResponse 顺序产生统一 ProviderEvent 流。
    /// 不变量：`functionCall.args` 作为一个完整对象一次规范化；字节分片仅属于传输层。
    /// 失败：凭据、模型、endpoint、HTTP、超时、取消、断流或 JSON/SSE 违规返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn stream(
        &self,
        request: ProviderRequest,
        cancellation: CancellationToken,
    ) -> Result<ProviderStream, AgentError> {
        let client = self.client.as_ref().map_err(Clone::clone)?;
        let api_key = credential_from_environment(self.credential_env.as_deref())?;
        let credential = HeaderValue::from_str(&api_key).map_err(|_| {
            AgentError::new(
                ErrorCode::ProviderUnavailable,
                "provider credential is invalid",
            )
        })?;
        let path = google_stream_path(&request.model)?;
        let endpoint = native_endpoint(&self.base_url, &path, &[("alt", "sse")])?;
        let builder = client
            .post(endpoint)
            .header(CONTENT_TYPE, "application/json")
            .header("x-goog-api-key", credential)
            .json(&request_body(&request));
        drop(api_key);
        let response = send_provider_request(builder, &cancellation).await?;
        if !response.status().is_success() {
            return Err(provider_status_error(&response));
        }

        let output = try_stream! {
            let mut response = response;
            let mut decoder = SseDecoder::new(4 * 1024 * 1024);
            let mut state = GoogleStreamState::default();
            while let Some(chunk) = next_response_chunk(&mut response, &cancellation).await? {
                for event in decoder.push(&chunk)? {
                    for normalized in parse_event(&event, &mut state)? {
                        yield normalized;
                    }
                }
            }
            for event in decoder.finish()? {
                for normalized in parse_event(&event, &mut state)? {
                    yield normalized;
                }
            }
        };
        Ok(Box::pin(output))
    }
}

/// Google 流解析期间保存 message-start 与工具调用结束原因所需的最小状态。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Default)]
pub(super) struct GoogleStreamState {
    started: bool,
    response_id: Option<String>,
    tool_call_count: u64,
}

/// 功能：把公共历史消息、工具结果和定义映射为 Google GenerateContent 请求体。
///
/// 输入：ProviderRequest 中按 durable 顺序排列的消息、工具定义和输出上限。
/// 输出：包含 `contents`、可选 `systemInstruction/tools/generationConfig` 的 JSON 对象。
/// 不变量：模型与 stream 选择仅进入 URL；工具结果以 `functionResponse` 回注下一轮。
/// 失败：纯内存映射不返回错误；不可表达的 artifact/image 块在文本 v1 中显式忽略。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(super) fn request_body(request: &ProviderRequest) -> Value {
    let mut system_parts = request
        .system_instructions
        .iter()
        .map(String::as_str)
        .collect::<Vec<_>>();
    system_parts.extend(
        request
            .messages
            .iter()
            .filter(|message| message.role == Role::System)
            .flat_map(|message| &message.content)
            .filter_map(|block| match block {
                ContentBlock::Text { text } => Some(text.as_str()),
                _ => None,
            })
            .collect::<Vec<_>>(),
    );
    let system = system_parts.join("\n");
    let mut contents = Vec::<Value>::new();
    for message in request
        .messages
        .iter()
        .filter(|message| message.role != Role::System)
    {
        let role = if message.role == Role::Assistant {
            "model"
        } else {
            "user"
        };
        let mut parts = Vec::<Value>::new();
        for block in &message.content {
            match block {
                ContentBlock::Text { text } => parts.push(json!({"text":text})),
                ContentBlock::Reasoning { text, .. } => {
                    parts.push(json!({"text":text,"thought":true}));
                }
                ContentBlock::ToolCall {
                    call_id,
                    name,
                    arguments,
                } => parts.push(json!({
                    "functionCall":{"id":call_id,"name":name,"args":arguments}
                })),
                ContentBlock::ToolResult {
                    call_id,
                    name,
                    output,
                    is_error,
                } => {
                    let text = output
                        .text
                        .as_deref()
                        .unwrap_or("tool result stored as artifact");
                    let response = if *is_error {
                        json!({"error":text})
                    } else {
                        json!({"output":text})
                    };
                    parts.push(json!({
                        "functionResponse":{
                            "id":call_id,
                            "name":name,
                            "response":response
                        }
                    }));
                }
                ContentBlock::ImageRef { .. } | ContentBlock::ArtifactRef { .. } => {}
            }
        }
        if !parts.is_empty() {
            if message.role == Role::Tool
                && contents.last_mut().is_some_and(|last| {
                    last.get("role").and_then(Value::as_str) == Some("user")
                        && last
                            .get("parts")
                            .and_then(Value::as_array)
                            .is_some_and(|existing| {
                                existing
                                    .iter()
                                    .any(|part| part.get("functionResponse").is_some())
                            })
                })
            {
                if let Some(existing) = contents
                    .last_mut()
                    .and_then(|last| last.get_mut("parts"))
                    .and_then(Value::as_array_mut)
                {
                    existing.extend(parts);
                }
            } else {
                contents.push(json!({"role":role,"parts":parts}));
            }
        }
    }
    let tools = request
        .tools
        .iter()
        .map(|tool| {
            json!({
                "name":tool.name,
                "description":tool.description,
                "parametersJsonSchema":tool.input_schema
            })
        })
        .collect::<Vec<_>>();
    let mut body = json!({"contents":contents});
    if !system.is_empty() {
        body["systemInstruction"] = json!({"parts":[{"text":system}]});
    }
    if !tools.is_empty() {
        body["tools"] = json!([{"functionDeclarations":tools}]);
    }
    if let Some(limit) = request.max_output_tokens {
        body["generationConfig"] = json!({"maxOutputTokens":limit});
    }
    body
}

/// 功能：解析一个 Google GenerateContentResponse SSE 数据对象。
///
/// 输入：已完成的 SSE event 与本次流的最小状态。
/// 输出：message start、文本/推理 delta、完整工具调用、usage 与 message end 事件。
/// 不变量：完整 `args` 先序列化为单个严格 JSON delta 再结束工具；错误 message 不外泄。
/// 失败：JSON、工具名/参数、响应结构或 Provider error 违规时返回结构化错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(super) fn parse_event(
    event: &SseEvent,
    state: &mut GoogleStreamState,
) -> Result<Vec<ProviderEvent>, AgentError> {
    if event.data.trim().is_empty() {
        return Ok(Vec::new());
    }
    let value: Value = serde_json::from_str(&event.data).map_err(|_| {
        AgentError::new(
            ErrorCode::StreamInterrupted,
            "invalid Google Generative AI stream JSON",
        )
    })?;
    if value.get("error").is_some() {
        return Err(AgentError::new(
            ErrorCode::ProviderError,
            "provider stream reported an error",
        ));
    }
    let mut output = Vec::new();
    if let Some(response_id) = value.get("responseId").and_then(Value::as_str) {
        if state.response_id.is_none() {
            state.response_id = Some(response_id.to_owned());
        }
        if !state.started {
            state.started = true;
            output.push(ProviderEvent::MessageStart {
                provider_message_id: response_id.to_owned(),
            });
        }
    }
    if let Some(candidates) = value.get("candidates").and_then(Value::as_array) {
        for candidate in candidates {
            if let Some(parts) = candidate
                .pointer("/content/parts")
                .and_then(Value::as_array)
            {
                for part in parts {
                    if let Some(text) = part.get("text").and_then(Value::as_str)
                        && !text.is_empty()
                    {
                        output.push(
                            if part.get("thought").and_then(Value::as_bool) == Some(true) {
                                ProviderEvent::ReasoningDelta {
                                    text: text.to_owned(),
                                }
                            } else {
                                ProviderEvent::TextDelta {
                                    text: text.to_owned(),
                                }
                            },
                        );
                    }
                    if let Some(call) = part.get("functionCall") {
                        let name = call.get("name").and_then(Value::as_str).ok_or_else(|| {
                            AgentError::new(
                                ErrorCode::StreamInterrupted,
                                "Google function call missing name",
                            )
                        })?;
                        let args = call.get("args").cloned().unwrap_or_else(|| json!({}));
                        if !args.is_object() {
                            return Err(AgentError::new(
                                ErrorCode::ToolArgumentsInvalid,
                                "Google function arguments must be an object",
                            ));
                        }
                        state.tool_call_count = state.tool_call_count.saturating_add(1);
                        let call_id = call
                            .get("id")
                            .and_then(Value::as_str)
                            .filter(|id| !id.is_empty())
                            .map(str::to_owned)
                            .unwrap_or_else(|| fallback_call_id(state, name));
                        output.push(ProviderEvent::ToolCallStart {
                            call_id: call_id.clone(),
                            name: name.to_owned(),
                        });
                        output.push(ProviderEvent::ToolCallArgumentsDelta {
                            call_id: call_id.clone(),
                            delta: args.to_string(),
                        });
                        output.push(ProviderEvent::ToolCallEnd { call_id });
                    }
                }
            }
            if let Some(reason) = candidate.get("finishReason").and_then(Value::as_str) {
                output.extend(usage_event(&value));
                output.push(ProviderEvent::MessageEnd {
                    finish_reason: google_finish_reason(reason, state.tool_call_count > 0),
                });
            }
        }
    }
    if !output
        .iter()
        .any(|event| matches!(event, ProviderEvent::Usage(_)))
    {
        output.extend(usage_event(&value));
    }
    Ok(output)
}

/// 功能：把 Google usageMetadata 转换为至多一个公共 Usage 事件。
///
/// 输入：GenerateContentResponse JSON。
/// 输出：存在 usageMetadata 时返回一个事件，否则为空。
/// 不变量：缓存 token 从 input 中分离，总量优先采用 Provider 值且使用饱和运算。
/// 失败：缺失或类型错误字段按零处理，本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn usage_event(value: &Value) -> Vec<ProviderEvent> {
    let Some(usage) = value.get("usageMetadata") else {
        return Vec::new();
    };
    let prompt = usage
        .get("promptTokenCount")
        .and_then(Value::as_u64)
        .unwrap_or(0);
    let cached = usage
        .get("cachedContentTokenCount")
        .and_then(Value::as_u64)
        .unwrap_or(0)
        .min(prompt);
    let candidates = usage
        .get("candidatesTokenCount")
        .and_then(Value::as_u64)
        .unwrap_or(0);
    let thoughts = usage
        .get("thoughtsTokenCount")
        .and_then(Value::as_u64)
        .unwrap_or(0);
    vec![ProviderEvent::Usage(Usage {
        input_tokens: prompt.saturating_sub(cached),
        output_tokens: candidates.saturating_add(thoughts),
        total_tokens: usage
            .get("totalTokenCount")
            .and_then(Value::as_u64)
            .unwrap_or_else(|| prompt.saturating_add(candidates).saturating_add(thoughts)),
        cached_input_tokens: cached,
        cost_micros: None,
    })]
}

/// 功能：将 Google 完成原因及本流工具状态归一化为公共结束原因。
///
/// 输入：Google finishReason 字符串及是否已出现完整工具调用。
/// 输出：工具调用优先，否则 STOP/MAX_TOKENS/安全拒绝映射到公共枚举。
/// 不变量：未知原因保守映射 Error，不从错误文本推断状态。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn google_finish_reason(reason: &str, has_tool_calls: bool) -> FinishReason {
    if has_tool_calls {
        return FinishReason::ToolCalls;
    }
    match reason {
        "STOP" => FinishReason::Stop,
        "MAX_TOKENS" => FinishReason::Length,
        "SAFETY" | "RECITATION" | "PROHIBITED_CONTENT" | "BLOCKLIST" => FinishReason::Error,
        _ => FinishReason::Error,
    }
}

/// 功能：验证 Google 模型 ID 并构造不会改变 authority 的原生流路径。
///
/// 输入：run/start 选择的模型 ID。
/// 输出：`/models/{id}:streamGenerateContent` 固定路径。
/// 不变量：模型只允许 1..128 个 ASCII 字母、数字、点、下划线或连字符。
/// 失败：非法模型返回不回显模型值的 InvalidParams 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn google_stream_path(model: &str) -> Result<String, AgentError> {
    if model.is_empty()
        || model.len() > 128
        || !model
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
    {
        return Err(AgentError::new(
            ErrorCode::InvalidParams,
            "Google model ID is invalid",
        ));
    }
    Ok(format!("/models/{model}:streamGenerateContent"))
}

/// 功能：为缺少 Google functionCall.id 的响应生成有界稳定 opaque ID。
///
/// 输入：当前响应 ID、工具名称及流内单调调用序号。
/// 输出：只含允许 ASCII 字符且不超过 128 字节的调用 ID。
/// 不变量：相同流顺序得到相同 ID；不使用时间、随机数或凭据。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn fallback_call_id(state: &GoogleStreamState, name: &str) -> String {
    let response = state.response_id.as_deref().unwrap_or("response");
    let mut value = format!("{name}_{response}_{}", state.tool_call_count)
        .chars()
        .map(|character| {
            if character.is_ascii_alphanumeric() || matches!(character, '.' | '_' | ':' | '-') {
                character
            } else {
                '_'
            }
        })
        .collect::<String>();
    value.truncate(128);
    if value
        .chars()
        .next()
        .is_none_or(|character| !character.is_ascii_alphanumeric())
    {
        value.insert_str(0, "call_");
        value.truncate(128);
    }
    value
}

#[cfg(test)]
mod tests {
    use chrono::Utc;
    use serde_json::json;

    use super::{GoogleStreamState, google_stream_path, parse_event, request_body};
    use crate::domain::{
        ContentBlock, Message, ProviderEvent, Role, ToolDefinition, ToolEffect, ToolOutput,
    };
    use crate::provider::{ProviderRequest, SseEvent};

    /// 功能：验证 Google 请求用 functionResponse 回注工具结果且保留非空工具定义。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn serializes_tool_result_continuation() {
        let body = request_body(&ProviderRequest {
            model: "mock-google-v1".to_owned(),
            system_instructions: Some("profile guidance".to_owned()),
            messages: vec![
                Message {
                    id: "assistant".to_owned(),
                    role: Role::Assistant,
                    content: vec![ContentBlock::ToolCall {
                        call_id: "call_google_1".to_owned(),
                        name: "file.read".to_owned(),
                        arguments: json!({"path":"README.md"}),
                    }],
                    created_at: Utc::now(),
                },
                Message {
                    id: "tool".to_owned(),
                    role: Role::Tool,
                    content: vec![ContentBlock::ToolResult {
                        call_id: "call_google_1".to_owned(),
                        name: "file.read".to_owned(),
                        output: ToolOutput {
                            text: Some("ok".to_owned()),
                            artifact: None,
                            termination_reason: None,
                            execution: None,
                            metadata: Default::default(),
                        },
                        is_error: false,
                    }],
                    created_at: Utc::now(),
                },
            ],
            tools: vec![ToolDefinition {
                name: "file.read".to_owned(),
                description: "read".to_owned(),
                input_schema: json!({"type":"object"}),
                effect: ToolEffect::Read,
            }],
            max_output_tokens: None,
            session_id: None,
            run_id: None,
            resolved_images: Vec::new(),
        });
        assert_eq!(
            body["tools"][0]["functionDeclarations"][0]["name"],
            "file.read"
        );
        assert_eq!(
            body["systemInstruction"]["parts"][0]["text"],
            "profile guidance"
        );
        assert!(body["contents"].as_array().is_some_and(|contents| {
            contents.iter().any(|content| {
                content
                    .pointer("/parts/0/functionResponse/response/output")
                    .and_then(serde_json::Value::as_str)
                    == Some("ok")
            })
        }));
    }

    /// 功能：验证 Google 完整 functionCall.args 被规范化为单个工具参数 delta。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn normalizes_complete_function_arguments() -> Result<(), crate::error::AgentError> {
        let events = parse_event(
            &SseEvent {
                event: Some("message".to_owned()),
                data: json!({
                    "responseId":"google_response_1",
                    "candidates":[{"content":{"parts":[{"functionCall":{
                        "id":"call_google_1","name":"file.read","args":{"path":"README.md"}
                    }}]},"finishReason":"STOP"}],
                    "usageMetadata":{"promptTokenCount":7,"candidatesTokenCount":5,"totalTokenCount":12}
                })
                .to_string(),
                id: None,
            },
            &mut GoogleStreamState::default(),
        )?;
        assert!(events.iter().any(|event| matches!(event,
            ProviderEvent::ToolCallArgumentsDelta { call_id, delta }
            if call_id == "call_google_1" && delta == r#"{"path":"README.md"}"#
        )));
        assert!(events.iter().any(|event| matches!(
            event,
            ProviderEvent::MessageEnd {
                finish_reason: crate::domain::FinishReason::ToolCalls
            }
        )));
        Ok(())
    }

    /// 功能：验证 Google 模型 ID 路径构造拒绝 slash、query 与 fragment 注入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn validates_model_path_segment() {
        assert_eq!(
            google_stream_path("gemini-2.5-flash").as_deref(),
            Ok("/models/gemini-2.5-flash:streamGenerateContent")
        );
        assert!(google_stream_path("models/x?alt=json").is_err());
    }
}
