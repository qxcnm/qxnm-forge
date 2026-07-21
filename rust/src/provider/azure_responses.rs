use std::collections::BTreeMap;

use async_stream::try_stream;
use async_trait::async_trait;
use reqwest::header::CONTENT_TYPE;
use serde_json::{Value, json};
use tokio_util::sync::CancellationToken;

use super::openai_responses;
use super::sse::{
    build_http_client, native_endpoint, next_response_chunk, provider_status_error,
    send_provider_request,
};
use super::{Provider, ProviderRequest, ProviderStream, SseDecoder};
use crate::error::{AgentError, ErrorCode};

/// Azure OpenAI Responses API family 的独立原生 Rust Provider。
///
/// 不变量：Azure base 必须由运行时显式配置；请求固定追加 `/responses` 与 `api-version`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Clone)]
pub struct AzureOpenAiResponsesProvider {
    id: String,
    client: Result<reqwest::Client, AgentError>,
    endpoint: Result<reqwest::Url, AgentError>,
    api_key: Option<String>,
}

impl AzureOpenAiResponsesProvider {
    /// 功能：创建 Azure Responses Provider 并预校验显式 base 与 API 版本。
    ///
    /// 输入：注册 ID、运行时 base URL、API version 和可选 `api-key` 密钥。
    /// 输出：持有固定 Responses request-target 的原生 Provider。
    /// 不变量：不猜测 Azure resource；生产只接受 HTTPS，回环 HTTP 仅限显式 conformance。
    /// 失败：客户端、base 或版本无效时在 `stream` 返回脱敏结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn new(
        id: impl Into<String>,
        base_url: impl AsRef<str>,
        api_version: impl AsRef<str>,
        api_key: Option<String>,
    ) -> Self {
        let api_version = api_version.as_ref();
        let endpoint = if valid_api_version(api_version) {
            native_endpoint(
                base_url.as_ref(),
                "/responses",
                &[("api-version", api_version)],
            )
        } else {
            Err(invalid_api_version_error())
        };
        Self {
            id: id.into(),
            client: build_http_client(),
            endpoint,
            api_key,
        }
    }
}

#[async_trait]
impl Provider for AzureOpenAiResponsesProvider {
    /// 功能：返回 Azure Responses Provider 的稳定注册 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn id(&self) -> &str {
        &self.id
    }

    /// 功能：发送 Azure Responses SSE 请求并规范化文本、推理、工具和 usage。
    ///
    /// 输入：公共 Provider 请求和运行取消令牌。
    /// 输出：共享 Responses 语义的统一 ProviderEvent 流。
    /// 不变量：认证只进入最终 `api-key`；body 固定 `store:false`；禁止自动重定向。
    /// 失败：缺少凭据、endpoint、HTTP 状态、超时、断流、取消或 SSE/JSON 违规返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn stream(
        &self,
        request: ProviderRequest,
        cancellation: CancellationToken,
    ) -> Result<ProviderStream, AgentError> {
        let client = self.client.as_ref().map_err(Clone::clone)?;
        let endpoint = self.endpoint.as_ref().map_err(Clone::clone)?;
        let api_key = self
            .api_key
            .as_deref()
            .ok_or_else(missing_credential_error)?;
        let response = send_provider_request(
            client
                .post(endpoint.clone())
                .header(CONTENT_TYPE, "application/json")
                .header("api-key", api_key)
                .json(&request_body(&request)),
            &cancellation,
        )
        .await?;
        if !response.status().is_success() {
            return Err(provider_status_error(&response));
        }

        let output = try_stream! {
            let mut response = response;
            let mut decoder = SseDecoder::new(4 * 1024 * 1024);
            let mut calls = BTreeMap::<String, openai_responses::ResponseCallState>::new();
            while let Some(chunk) = next_response_chunk(&mut response, &cancellation).await? {
                for event in decoder.push(&chunk)? {
                    for normalized in openai_responses::parse_event(&event, &mut calls)? {
                        yield normalized;
                    }
                }
            }
            for event in decoder.finish()? {
                for normalized in openai_responses::parse_event(&event, &mut calls)? {
                    yield normalized;
                }
            }
        };
        Ok(Box::pin(output))
    }
}

/// 功能：把公共请求映射为 Azure Responses JSON 并强制关闭服务端存储。
///
/// 输入：包含历史消息、工具结果、工具定义及模型部署名的 ProviderRequest。
/// 输出：兼容 Responses wire 且包含 `store:false` 的 JSON 对象。
/// 不变量：工具顺序保持，工具 schema 使用非严格模式以兼容冻结 PI 行为。
/// 失败：映射为纯内存操作，不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn request_body(request: &ProviderRequest) -> Value {
    let mut body = openai_responses::request_body(request);
    body["store"] = Value::Bool(false);
    if let Some(tools) = body.get_mut("tools").and_then(Value::as_array_mut) {
        for tool in tools {
            tool["strict"] = Value::Bool(false);
        }
    }
    body
}

/// 功能：验证 Azure API version 只含有限 ASCII 标识字符且长度有界。
///
/// 输入：运行时显式 API version。
/// 输出：长度 1..64 且仅含字母、数字、点或连字符时为 true。
/// 不变量：值随后只通过 URL query encoder 注入，不能改变 authority 或 path。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn valid_api_version(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 64
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-'))
}

/// 功能：创建缺少 Azure API key 的稳定脱敏配置错误。
///
/// 输入：无。
/// 输出：不可重试的 ProviderUnavailable 错误。
/// 不变量：不披露环境、base、resource 或凭据。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn missing_credential_error() -> AgentError {
    AgentError::new(
        ErrorCode::ProviderUnavailable,
        "Azure OpenAI credential is not configured",
    )
    .with_details(json!({"kind":"provider_unavailable"}))
}

/// 功能：创建非法 Azure API version 的固定错误而不回显配置值。
///
/// 输入：无。
/// 输出：不可重试的 ProviderUnavailable 错误。
/// 不变量：错误中不出现版本、URL 或凭据原值。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn invalid_api_version_error() -> AgentError {
    AgentError::new(
        ErrorCode::ProviderUnavailable,
        "Azure OpenAI API version is invalid",
    )
    .with_details(json!({"kind":"invalid_provider_endpoint"}))
}

#[cfg(test)]
mod tests {
    use chrono::Utc;
    use serde_json::{Value, json};

    use super::{request_body, valid_api_version};
    use crate::domain::{ContentBlock, Message, Role, ToolDefinition, ToolEffect, ToolOutput};
    use crate::provider::ProviderRequest;

    /// 功能：验证 Azure 请求包含 store:false、非空工具及上一轮工具结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn serializes_tool_result_continuation() {
        let body = request_body(&ProviderRequest {
            model: "mock-azure-responses-v1".to_owned(),
            system_instructions: None,
            messages: vec![
                Message {
                    id: "assistant".to_owned(),
                    role: Role::Assistant,
                    content: vec![ContentBlock::ToolCall {
                        call_id: "call_1".to_owned(),
                        name: "file.read".to_owned(),
                        arguments: json!({"path":"README.md"}),
                    }],
                    created_at: Utc::now(),
                },
                Message {
                    id: "tool".to_owned(),
                    role: Role::Tool,
                    content: vec![ContentBlock::ToolResult {
                        call_id: "call_1".to_owned(),
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
        });
        assert_eq!(body["store"], false);
        assert_eq!(body["tools"][0]["name"], "file.read");
        assert_eq!(body["tools"][0]["strict"], false);
        assert!(
            body["input"].as_array().is_some_and(|values| values
                .iter()
                .any(|value| value.get("type").and_then(Value::as_str)
                    == Some("function_call_output")))
        );
    }

    /// 功能：验证 Azure API version 字符边界拒绝 query 注入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn validates_api_version_safely() {
        assert!(valid_api_version("v1"));
        assert!(valid_api_version("2025-04-01-preview"));
        assert!(!valid_api_version("v1&redirect=https://example.test"));
    }
}
