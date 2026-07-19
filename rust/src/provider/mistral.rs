use std::collections::BTreeMap;

use async_stream::try_stream;
use async_trait::async_trait;
use reqwest::header::{AUTHORIZATION, CONTENT_TYPE, HeaderValue};
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use super::openai_chat;
use super::sse::{
    build_http_client, native_endpoint, next_response_chunk, provider_status_error,
    send_provider_request,
};
use super::{Provider, ProviderRequest, ProviderStream, SseDecoder, credential_from_environment};
use crate::error::{AgentError, ErrorCode};

/// Mistral Conversations API family 的独立原生 Rust Provider。
///
/// 不变量：base URL 只追加一次 `/v1/chat/completions`；密钥不参与 Debug；HTTP 不自动重定向。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Clone)]
pub struct MistralConversationsProvider {
    id: String,
    client: Result<reqwest::Client, AgentError>,
    endpoint: Result<reqwest::Url, AgentError>,
    credential_env: Option<String>,
}

impl MistralConversationsProvider {
    /// 功能：创建 Mistral Conversations 原生 Provider 并预校验安全请求目标。
    ///
    /// 输入：注册 ID、Mistral base URL 和可选 Bearer credential 环境名称。
    /// 输出：持有原生 reqwest 客户端及固定 Chat endpoint 的 Provider。
    /// 不变量：构造不发起网络；生产只接受 HTTPS，测试 HTTP 仅限显式 conformance 的 127.0.0.1。
    /// 失败：客户端或 endpoint 配置错误延迟到 `stream` 以相同结构化错误返回。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn new(
        id: impl Into<String>,
        base_url: impl AsRef<str>,
        credential_env: Option<String>,
    ) -> Self {
        Self {
            id: id.into(),
            client: build_http_client(),
            endpoint: native_endpoint(base_url.as_ref(), "/v1/chat/completions", &[]),
            credential_env,
        }
    }
}

#[async_trait]
impl Provider for MistralConversationsProvider {
    /// 功能：返回 Mistral Provider 的稳定注册 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn id(&self) -> &str {
        &self.id
    }

    /// 功能：发送 Mistral Chat SSE 请求并规范化文本、工具参数和 usage。
    ///
    /// 输入：公共消息/工具请求及运行取消令牌。
    /// 输出：按原生 SSE 顺序产生统一 ProviderEvent 流。
    /// 不变量：工具定义使用 Mistral function 包装且 `strict:false`；凭据仅进入最终 Bearer header。
    /// 失败：缺少凭据、目标、HTTP 状态、超时、取消、断流或 JSON/SSE 违规返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn stream(
        &self,
        request: ProviderRequest,
        cancellation: CancellationToken,
    ) -> Result<ProviderStream, AgentError> {
        let client = self.client.as_ref().map_err(Clone::clone)?;
        let endpoint = self.endpoint.as_ref().map_err(Clone::clone)?;
        let api_key = credential_from_environment(self.credential_env.as_deref())?;
        let authorization = HeaderValue::from_str(&format!("Bearer {api_key}")).map_err(|_| {
            AgentError::new(
                ErrorCode::ProviderUnavailable,
                "provider credential is invalid",
            )
        })?;
        let builder = client
            .post(endpoint.clone())
            .header(CONTENT_TYPE, "application/json")
            .header(AUTHORIZATION, authorization)
            .json(&request_body(&request)?);
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
                    for normalized in openai_chat::parse_event(&event, &mut calls)? {
                        yield normalized;
                    }
                }
            }
            for event in decoder.finish()? {
                for normalized in openai_chat::parse_event(&event, &mut calls)? {
                    yield normalized;
                }
            }
        };
        Ok(Box::pin(output))
    }
}

/// 功能：把公共请求映射为 Mistral Chat wire JSON，并移除 OpenAI 专属字段。
///
/// 输入：包含历史消息、完整工具清单和可选输出上限的 ProviderRequest。
/// 输出：`model/messages/stream/tools` 使用 Mistral 原生形状的 JSON 对象。
/// 不变量：工具顺序保持不变；所有 function tool 显式 `strict:false`；不发送 `stream_options`。
/// 失败：基础 Chat 映射失败时返回结构化错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn request_body(request: &ProviderRequest) -> Result<Value, AgentError> {
    let mut body = openai_chat::request_body(request)?;
    let Some(object) = body.as_object_mut() else {
        return Err(AgentError::new(
            ErrorCode::InternalError,
            "Mistral request mapping did not produce an object",
        ));
    };
    object.remove("stream_options");
    if let Some(limit) = object.remove("max_completion_tokens") {
        object.insert("max_tokens".to_owned(), limit);
    }
    if let Some(tools) = object.get_mut("tools").and_then(Value::as_array_mut) {
        for tool in tools {
            if let Some(function) = tool.get_mut("function").and_then(Value::as_object_mut) {
                function.insert("strict".to_owned(), Value::Bool(false));
            }
        }
    }
    Ok(body)
}

#[cfg(test)]
mod tests {
    use chrono::Utc;
    use serde_json::{Value, json};

    use super::request_body;
    use crate::domain::{ContentBlock, Message, Role, ToolDefinition, ToolEffect, ToolOutput};
    use crate::provider::ProviderRequest;

    /// 功能：验证 Mistral 请求包含非空工具、工具结果续轮且没有 OpenAI stream_options。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn serializes_native_nonempty_tool_definition() -> Result<(), crate::error::AgentError> {
        let body = request_body(&ProviderRequest {
            model: "mock-mistral-v1".to_owned(),
            messages: vec![
                Message {
                    id: "m1".to_owned(),
                    role: Role::User,
                    content: vec![ContentBlock::Text {
                        text: "hello".to_owned(),
                    }],
                    created_at: Utc::now(),
                },
                Message {
                    id: "m2".to_owned(),
                    role: Role::Assistant,
                    content: vec![ContentBlock::ToolCall {
                        call_id: "callmock1".to_owned(),
                        name: "file.read".to_owned(),
                        arguments: json!({"path":"README.md"}),
                    }],
                    created_at: Utc::now(),
                },
                Message {
                    id: "m3".to_owned(),
                    role: Role::Tool,
                    content: vec![ContentBlock::ToolResult {
                        call_id: "callmock1".to_owned(),
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
        })?;
        assert_eq!(body["tools"][0]["function"]["name"], "file.read");
        assert_eq!(body["tools"][0]["function"]["strict"], false);
        assert!(body.get("stream_options").is_none());
        assert!(body["messages"].as_array().is_some_and(|messages| {
            messages.iter().any(|message| {
                message.get("role").and_then(Value::as_str) == Some("tool")
                    && message.get("tool_call_id").and_then(Value::as_str) == Some("callmock1")
            })
        }));
        Ok(())
    }
}
