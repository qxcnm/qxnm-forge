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

/// OpenAI Responses API family 的独立原生 Rust Provider。
///
/// 不变量：认证密钥不会通过 `Debug` 暴露；实例不会自动跟随 HTTP 重定向。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Clone)]
pub struct OpenAiResponsesProvider {
    id: String,
    client: Result<reqwest::Client, AgentError>,
    endpoint: String,
    credential_source: ProviderCredentialSource,
}

impl OpenAiResponsesProvider {
    /// 功能：创建 OpenAI Responses API family 的原生 HTTP Provider。
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

    /// 功能：为本地已确认推广 route 创建按请求读取 stored credential 的 Responses adapter。
    ///
    /// 输入：固定 Provider ID、完整同源 endpoint 和不持有 secret 的来源对象。
    /// 输出：复用既有 Responses request/SSE parser 的 Provider。
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
impl Provider for OpenAiResponsesProvider {
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

    /// 功能：调用 Responses 流式接口并规范化文本、推理、工具和用量事件。
    ///
    /// 输入：通用 Provider 请求和运行级取消令牌。
    /// 输出：解析 SSE 后按服务器顺序产生的统一事件流。
    /// 不变量：认证信息仅进入最终请求头；禁止自动重定向；单 SSE 事件限制为 4 MiB。
    /// 失败：网络、状态、idle timeout、SSE/JSON、工具身份或取消返回脱敏稳定错误。
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
            .json(&request_body(&request));
        drop(api_key);
        let response = send_provider_request(builder, &cancellation).await?;
        if !response.status().is_success() {
            return Err(provider_status_error(&response));
        }
        let output = try_stream! {
            let mut response = response;
            let mut decoder = SseDecoder::new(4 * 1024 * 1024);
            let mut calls = BTreeMap::<String, ResponseCallState>::new();
            while let Some(chunk) = next_response_chunk(&mut response, &cancellation).await? {
                for event in decoder.push(&chunk)? {
                    for normalized in parse_event(&event, &mut calls)? { yield normalized; }
                }
            }
            for event in decoder.finish()? {
                for normalized in parse_event(&event, &mut calls)? { yield normalized; }
            }
        };
        Ok(Box::pin(output))
    }
}

/// 功能：保存 Responses item ID 对应的稳定 call ID 及是否已经规范化结束。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(super) struct ResponseCallState {
    call_id: String,
    ended: bool,
}

/// 功能：把公共消息、工具和输出上限转换为 Responses API 请求 JSON。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(super) fn request_body(request: &ProviderRequest) -> Value {
    let mut input = Vec::new();
    for message in &request.messages {
        let role = match message.role {
            Role::System => "system",
            Role::User | Role::Tool => "user",
            Role::Assistant => "assistant",
        };
        for block in &message.content {
            match block {
                ContentBlock::Text { text } => input.push(json!({
                    "type": "message",
                    "role": role,
                    "content": [{
                        "type": if message.role == Role::Assistant { "output_text" } else { "input_text" },
                        "text": text
                    }]
                })),
                ContentBlock::ToolCall {
                    call_id,
                    name,
                    arguments,
                } => input.push(json!({
                    "type": "function_call",
                    "call_id": call_id,
                    "name": name,
                    "arguments": arguments.to_string()
                })),
                ContentBlock::ToolResult {
                    call_id, output, ..
                } => input.push(json!({
                    "type": "function_call_output",
                    "call_id": call_id,
                    "output": output.text.as_deref().unwrap_or("tool result stored as artifact")
                })),
                ContentBlock::Reasoning { .. }
                | ContentBlock::ImageRef { .. }
                | ContentBlock::ArtifactRef { .. } => {}
            }
        }
    }
    let tools = request
        .tools
        .iter()
        .map(|tool| {
            json!({
                "type":"function",
                "name":tool.name,
                "description":tool.description,
                "parameters":tool.input_schema,
                "strict":true
            })
        })
        .collect::<Vec<_>>();
    let mut body = json!({"model":request.model,"input":input,"stream":true});
    if let Some(instructions) = &request.system_instructions {
        body["instructions"] = Value::String(instructions.clone());
    }
    if !tools.is_empty() {
        body["tools"] = Value::Array(tools);
    }
    if let Some(limit) = request.max_output_tokens {
        body["max_output_tokens"] = json!(limit);
    }
    body
}

/// 功能：解析单个 Responses SSE 事件并维护 item ID 到工具调用 ID 的映射。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(super) fn parse_event(
    event: &SseEvent,
    calls: &mut BTreeMap<String, ResponseCallState>,
) -> Result<Vec<ProviderEvent>, AgentError> {
    if event.data.trim().is_empty() || event.data.trim() == "[DONE]" {
        return Ok(Vec::new());
    }
    let value: Value = serde_json::from_str(&event.data).map_err(|error| {
        AgentError::new(
            ErrorCode::StreamInterrupted,
            format!("invalid OpenAI Responses stream JSON: {error}"),
        )
    })?;
    let kind = value.get("type").and_then(Value::as_str).unwrap_or("");
    let mut output = Vec::new();
    match kind {
        "response.created" => {
            if let Some(id) = value.pointer("/response/id").and_then(Value::as_str) {
                output.push(ProviderEvent::MessageStart {
                    provider_message_id: id.to_owned(),
                });
            }
        }
        "response.output_text.delta" => {
            if let Some(text) = value.get("delta").and_then(Value::as_str) {
                output.push(ProviderEvent::TextDelta {
                    text: text.to_owned(),
                });
            }
        }
        "response.reasoning_summary_text.delta" | "response.reasoning_text.delta" => {
            if let Some(text) = value.get("delta").and_then(Value::as_str) {
                output.push(ProviderEvent::ReasoningDelta {
                    text: text.to_owned(),
                });
            }
        }
        "response.output_item.added" => {
            if value.pointer("/item/type").and_then(Value::as_str) == Some("function_call") {
                let call_id = value
                    .pointer("/item/call_id")
                    .or_else(|| value.pointer("/item/id"))
                    .and_then(Value::as_str)
                    .ok_or_else(|| {
                        AgentError::new(ErrorCode::StreamInterrupted, "function call missing id")
                    })?;
                let name = value
                    .pointer("/item/name")
                    .and_then(Value::as_str)
                    .ok_or_else(|| {
                        AgentError::new(ErrorCode::StreamInterrupted, "function call missing name")
                    })?;
                let item_id = value
                    .pointer("/item/id")
                    .and_then(Value::as_str)
                    .unwrap_or(call_id);
                calls.insert(
                    item_id.to_owned(),
                    ResponseCallState {
                        call_id: call_id.to_owned(),
                        ended: false,
                    },
                );
                output.push(ProviderEvent::ToolCallStart {
                    call_id: call_id.to_owned(),
                    name: name.to_owned(),
                });
            }
        }
        "response.function_call_arguments.delta" => {
            let item_id = value
                .get("item_id")
                .and_then(Value::as_str)
                .or_else(|| value.get("call_id").and_then(Value::as_str))
                .ok_or_else(|| {
                    AgentError::new(ErrorCode::StreamInterrupted, "tool delta missing item id")
                })?;
            let call_id = calls
                .get(item_id)
                .map_or(item_id, |state| state.call_id.as_str());
            let delta = value.get("delta").and_then(Value::as_str).unwrap_or("");
            output.push(ProviderEvent::ToolCallArgumentsDelta {
                call_id: call_id.to_owned(),
                delta: delta.to_owned(),
            });
        }
        "response.function_call_arguments.done" => {
            let item_id = value
                .get("item_id")
                .and_then(Value::as_str)
                .or_else(|| value.get("call_id").and_then(Value::as_str))
                .ok_or_else(|| {
                    AgentError::new(ErrorCode::StreamInterrupted, "tool end missing item id")
                })?;
            if let Some(state) = calls.get_mut(item_id) {
                if !state.ended {
                    state.ended = true;
                    output.push(ProviderEvent::ToolCallEnd {
                        call_id: state.call_id.clone(),
                    });
                }
            } else {
                output.push(ProviderEvent::ToolCallEnd {
                    call_id: item_id.to_owned(),
                });
            }
        }
        "response.output_item.done" => {
            if value.pointer("/item/type").and_then(Value::as_str) == Some("function_call") {
                let item_id = value.pointer("/item/id").and_then(Value::as_str);
                if let Some(state) = item_id.and_then(|id| calls.get_mut(id)) {
                    if !state.ended {
                        state.ended = true;
                        output.push(ProviderEvent::ToolCallEnd {
                            call_id: state.call_id.clone(),
                        });
                    }
                } else if let Some(call_id) = value
                    .pointer("/item/call_id")
                    .and_then(Value::as_str)
                    .or(item_id)
                {
                    output.push(ProviderEvent::ToolCallEnd {
                        call_id: call_id.to_owned(),
                    });
                }
            }
        }
        "response.completed" | "response.incomplete" => {
            if let Some(usage) = value.pointer("/response/usage") {
                output.push(ProviderEvent::Usage(Usage {
                    input_tokens: usage
                        .get("input_tokens")
                        .and_then(Value::as_u64)
                        .unwrap_or(0),
                    output_tokens: usage
                        .get("output_tokens")
                        .and_then(Value::as_u64)
                        .unwrap_or(0),
                    total_tokens: usage
                        .get("total_tokens")
                        .and_then(Value::as_u64)
                        .unwrap_or_else(|| {
                            usage
                                .get("input_tokens")
                                .and_then(Value::as_u64)
                                .unwrap_or(0)
                                .saturating_add(
                                    usage
                                        .get("output_tokens")
                                        .and_then(Value::as_u64)
                                        .unwrap_or(0),
                                )
                        }),
                    cached_input_tokens: usage
                        .pointer("/input_tokens_details/cached_tokens")
                        .and_then(Value::as_u64)
                        .unwrap_or(0),
                    cost_micros: None,
                }));
            }
            let has_calls = !calls.is_empty();
            output.push(ProviderEvent::MessageEnd {
                finish_reason: if kind == "response.incomplete" {
                    FinishReason::Length
                } else if has_calls {
                    FinishReason::ToolCalls
                } else {
                    FinishReason::Stop
                },
            });
        }
        "error" | "response.failed" => {
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
    use std::error::Error;
    use std::io::{self, Read, Write};
    use std::net::{TcpListener, TcpStream};
    use std::thread::{self, JoinHandle};
    use std::time::Duration;

    use chrono::Utc;
    use futures_util::TryStreamExt;
    use serde_json::json;
    use tokio_util::sync::CancellationToken;

    use super::{OpenAiResponsesProvider, parse_event, request_body};
    use crate::domain::{ContentBlock, Message, ProviderEvent, Role, ToolOutput};
    use crate::error::ErrorCode;
    use crate::provider::{Provider, ProviderRequest, SseEvent};

    /// 功能：从本地 mock 连接读取一条有界且带 Content-Length 的完整 HTTP 请求。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn read_mock_request(stream: &mut TcpStream) -> io::Result<Vec<u8>> {
        stream.set_read_timeout(Some(Duration::from_secs(2)))?;
        let mut request = Vec::new();
        let mut buffer = [0_u8; 4_096];
        loop {
            let read = stream.read(&mut buffer)?;
            if read == 0 {
                break;
            }
            request.extend_from_slice(&buffer[..read]);
            if request.len() > 64 * 1024 {
                return Err(io::Error::other("mock HTTP request exceeded test limit"));
            }
            if expected_request_bytes(&request).is_some_and(|expected| request.len() >= expected) {
                break;
            }
        }
        Ok(request)
    }

    /// 功能：从已收到的 HTTP 头计算包含请求正文在内的预期总字节数。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn expected_request_bytes(request: &[u8]) -> Option<usize> {
        let header_end = request.windows(4).position(|bytes| bytes == b"\r\n\r\n")?;
        let headers = std::str::from_utf8(&request[..header_end]).ok()?;
        let content_length = headers.lines().find_map(|line| {
            let (name, value) = line.split_once(':')?;
            name.eq_ignore_ascii_case("content-length")
                .then(|| value.trim().parse::<usize>().ok())
                .flatten()
        });
        Some(header_end + 4 + content_length.unwrap_or(0))
    }

    /// 功能：启动一次性本地 HTTP/SSE mock，并按指定字节分片发送响应正文。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn spawn_sse_server(
        chunks: Vec<Vec<u8>>,
    ) -> io::Result<(String, JoinHandle<io::Result<Vec<u8>>>)> {
        let listener = TcpListener::bind(("127.0.0.1", 0))?;
        let address = listener.local_addr()?;
        let handle = thread::spawn(move || {
            let (mut stream, _) = listener.accept()?;
            let request = read_mock_request(&mut stream)?;
            let content_length = chunks.iter().map(Vec::len).sum::<usize>();
            write!(
                stream,
                "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {content_length}\r\nConnection: close\r\n\r\n"
            )?;
            for chunk in chunks {
                stream.write_all(&chunk)?;
                stream.flush()?;
                thread::sleep(Duration::from_millis(1));
            }
            Ok(request)
        });
        Ok((format!("http://{address}/responses"), handle))
    }

    /// 功能：验证 Responses 历史中的助手工具调用和工具结果使用专用 input item。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn serializes_function_call_and_function_call_output() {
        let request = ProviderRequest {
            model: "mock-model".to_owned(),
            system_instructions: Some("profile guidance".to_owned()),
            messages: vec![
                Message {
                    id: "assistant-1".to_owned(),
                    role: Role::Assistant,
                    content: vec![ContentBlock::ToolCall {
                        call_id: "call-1".to_owned(),
                        name: "read".to_owned(),
                        arguments: json!({"path":"资料.txt"}),
                    }],
                    created_at: Utc::now(),
                },
                Message {
                    id: "tool-1".to_owned(),
                    role: Role::Tool,
                    content: vec![ContentBlock::ToolResult {
                        call_id: "call-1".to_owned(),
                        name: "read".to_owned(),
                        output: ToolOutput {
                            text: Some("内容".to_owned()),
                            artifact: None,
                            termination_reason: None,
                            execution: None,
                            metadata: BTreeMap::new(),
                        },
                        is_error: false,
                    }],
                    created_at: Utc::now(),
                },
            ],
            tools: Vec::new(),
            max_output_tokens: None,
            session_id: None,
            run_id: None,
        };

        let body = request_body(&request);
        assert_eq!(body["instructions"], "profile guidance");
        assert_eq!(body["input"][0]["type"], "function_call");
        assert_eq!(body["input"][0]["call_id"], "call-1");
        assert_eq!(body["input"][0]["name"], "read");
        assert_eq!(body["input"][0]["arguments"], r#"{"path":"资料.txt"}"#);
        assert_eq!(body["input"][1]["type"], "function_call_output");
        assert_eq!(body["input"][1]["call_id"], "call-1");
        assert_eq!(body["input"][1]["output"], "内容");
    }

    /// 功能：验证 Responses 跨事件工具参数片段通过 item ID 归一到稳定 call ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn normalizes_partial_function_arguments() -> Result<(), crate::error::AgentError> {
        let mut calls = BTreeMap::new();
        let started = parse_event(
            &SseEvent {
                event: Some("response.output_item.added".to_owned()),
                data: json!({
                    "type":"response.output_item.added",
                    "item":{"type":"function_call","id":"fc-1","call_id":"call-1","name":"read"}
                })
                .to_string(),
                id: None,
            },
            &mut calls,
        )?;
        let delta = parse_event(
            &SseEvent {
                event: Some("response.function_call_arguments.delta".to_owned()),
                data: json!({
                    "type":"response.function_call_arguments.delta",
                    "item_id":"fc-1",
                    "delta":"{\"path\":\"x\"}"
                })
                .to_string(),
                id: None,
            },
            &mut calls,
        )?;
        let ended = parse_event(
            &SseEvent {
                event: Some("response.function_call_arguments.done".to_owned()),
                data: json!({
                    "type":"response.function_call_arguments.done",
                    "item_id":"fc-1",
                    "arguments":"{\"path\":\"x\"}"
                })
                .to_string(),
                id: None,
            },
            &mut calls,
        )?;
        let repeated_end = parse_event(
            &SseEvent {
                event: Some("response.output_item.done".to_owned()),
                data: json!({
                    "type":"response.output_item.done",
                    "item":{"type":"function_call","id":"fc-1"}
                })
                .to_string(),
                id: None,
            },
            &mut calls,
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
            ended.as_slice(),
            [ProviderEvent::ToolCallEnd { call_id }] if call_id == "call-1"
        ));
        assert!(repeated_end.is_empty());
        Ok(())
    }

    /// 功能：验证 Responses 流错误不会把 Provider 返回的敏感消息带入公共错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn redacts_stream_error_message() {
        let result = parse_event(
            &SseEvent {
                event: Some("error".to_owned()),
                data: json!({
                    "type":"error",
                    "error":{"message":"qxnm-forge-secret-canary-responses"}
                })
                .to_string(),
                id: None,
            },
            &mut BTreeMap::new(),
        );
        let Err(error) = result else {
            panic!("provider stream error was not rejected");
        };
        assert_eq!(error.code, ErrorCode::ProviderError);
        assert!(!error.message.contains("qxnm-forge-secret-canary-responses"));
    }

    /// 功能：通过本地 HTTP/SSE mock 验证 CRLF、多行 data、UTF-8 分片和完整 Provider 流。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn streams_fragmented_local_sse_without_real_provider() -> Result<(), Box<dyn Error>> {
        let source = concat!(
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp-local\"}}\r\n\r\n",
            "event: response.output_text.delta\r\n",
            "data: {\"type\":\"response.output_text.delta\",\r\n",
            "data: \"delta\":\"你🙂\"}\r\n\r\n",
            "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":2,\"output_tokens\":1,\"total_tokens\":3}}}\r\n\r\n"
        )
        .as_bytes();
        let emoji_start = source
            .windows("🙂".len())
            .position(|window| window == "🙂".as_bytes())
            .ok_or_else(|| io::Error::other("emoji missing from mock SSE"))?;
        let chunks = vec![
            source[..emoji_start + 1].to_vec(),
            source[emoji_start + 1..emoji_start + 3].to_vec(),
            source[emoji_start + 3..].to_vec(),
        ];
        let (endpoint, server) = spawn_sse_server(chunks)?;
        let canary = "qxnm-forge-secret-canary-local-sse";
        let provider = OpenAiResponsesProvider::new(
            "responses",
            endpoint,
            Some("QXNM_FORGE_TEST_FIXED_CREDENTIAL".to_owned()),
        );
        let request = ProviderRequest {
            model: "mock-model".to_owned(),
            system_instructions: None,
            messages: vec![Message::text("user-1", Role::User, "hello")],
            tools: Vec::new(),
            max_output_tokens: Some(32),
            session_id: None,
            run_id: None,
        };
        let events = provider
            .stream(request, CancellationToken::new())
            .await?
            .try_collect::<Vec<_>>()
            .await?;
        assert!(matches!(
            events.first(),
            Some(ProviderEvent::MessageStart { provider_message_id })
                if provider_message_id == "resp-local"
        ));
        assert!(events.iter().any(|event| matches!(
            event,
            ProviderEvent::TextDelta { text } if text == "你🙂"
        )));
        assert!(
            events
                .iter()
                .any(|event| matches!(event, ProviderEvent::MessageEnd { .. }))
        );

        let raw_request = server
            .join()
            .map_err(|_| io::Error::other("local SSE server panicked"))??;
        let request_text = String::from_utf8_lossy(&raw_request);
        assert!(request_text.contains(&format!("Bearer {canary}")));
        Ok(())
    }
}
