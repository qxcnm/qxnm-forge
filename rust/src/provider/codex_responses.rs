use std::collections::BTreeMap;

use async_stream::try_stream;
use async_trait::async_trait;
use reqwest::header::{AUTHORIZATION, CONTENT_TYPE};
use serde_json::{Value, json};
use tokio_util::sync::CancellationToken;

use super::openai_responses::{self, ResponseCallState};
use super::sse::{
    build_http_client, native_endpoint, next_response_chunk, provider_status_error,
    send_provider_request,
};
use super::{Provider, ProviderRequest, ProviderStream, SseDecoder, SseEvent};
use crate::domain::ProviderEvent;
use crate::error::{AgentError, ErrorCode};

/// OpenAI Codex OAuth Responses API family 的独立原生 Rust Provider。
///
/// 不变量：固定 route/header 由 adapter 持有，OAuth token 仅进入最终 bearer 请求头。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Clone)]
pub struct OpenAiCodexResponsesProvider {
    id: String,
    client: Result<reqwest::Client, AgentError>,
    endpoint: Result<reqwest::Url, AgentError>,
    oauth_token: Option<String>,
}

impl OpenAiCodexResponsesProvider {
    /// 功能：创建固定 `/codex/responses` route 的 Codex OAuth Provider。
    ///
    /// 输入：注册 ID、可信 base URL 与可选 OAuth bearer。
    /// 输出：尚未发起网络请求的原生 SSE Provider。
    /// 不变量：构造时只校验 endpoint；token 不进入 Debug、日志、事件或持久化。
    /// 失败：HTTP 客户端或 endpoint 错误延迟到 `stream` 返回脱敏结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn new(
        id: impl Into<String>,
        base_url: impl AsRef<str>,
        oauth_token: Option<String>,
    ) -> Self {
        Self {
            id: id.into(),
            client: build_http_client(),
            endpoint: native_endpoint(base_url.as_ref(), "/codex/responses", &[]),
            oauth_token,
        }
    }
}

#[async_trait]
impl Provider for OpenAiCodexResponsesProvider {
    /// 功能：返回 Codex Provider 的稳定注册 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn id(&self) -> &str {
        &self.id
    }

    /// 功能：发送 Codex Responses SSE 请求并规范化文本、推理、工具及 usage。
    ///
    /// 输入：公共 Provider 请求与运行取消令牌。
    /// 输出：严格按命名 Responses 事件顺序产生统一 ProviderEvent 流。
    /// 不变量：请求强制 `stream:true/store:false` 并发送固定非敏感 adapter headers。
    /// 失败：凭据、endpoint、HTTP、取消、超时、SSE、命名事件或 JSON 违规返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn stream(
        &self,
        request: ProviderRequest,
        cancellation: CancellationToken,
    ) -> Result<ProviderStream, AgentError> {
        let client = self.client.as_ref().map_err(Clone::clone)?;
        let endpoint = self.endpoint.as_ref().map_err(Clone::clone)?;
        let token = self
            .oauth_token
            .as_deref()
            .ok_or_else(missing_credential_error)?;
        let response = send_provider_request(
            client
                .post(endpoint.clone())
                .header(CONTENT_TYPE, "application/json")
                .header(AUTHORIZATION, format!("Bearer {token}"))
                .header("OpenAI-Beta", "responses=experimental")
                .header("originator", "qxnm-forge")
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
            let mut calls = BTreeMap::<String, ResponseCallState>::new();
            while let Some(chunk) = next_response_chunk(&mut response, &cancellation).await? {
                for event in decoder.push(&chunk)? {
                    for normalized in parse_named_event(&event, &mut calls)? {
                        yield normalized;
                    }
                }
            }
            for event in decoder.finish()? {
                for normalized in parse_named_event(&event, &mut calls)? {
                    yield normalized;
                }
            }
        };
        Ok(Box::pin(output))
    }
}

/// 功能：构造 Codex Responses 请求体并强制关闭服务端存储。
///
/// 输入：公共消息、工具、模型及可选输出限制。
/// 输出：包含 `model/input/stream/store:false` 的 Responses JSON。
/// 不变量：工具定义顺序和 Schema 保持不变；模型只来自显式 run/start 选择。
/// 失败：纯内存映射不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn request_body(request: &ProviderRequest) -> Value {
    let mut body = openai_responses::request_body(request);
    body["store"] = Value::Bool(false);
    body
}

/// 功能：验证 Codex SSE 的命名事件与 JSON `type` 一致后复用 Responses 归一化器。
///
/// 输入：完整 SSE 事件及 item ID 到工具调用状态的映射。
/// 输出：对应的统一 ProviderEvent 列表。
/// 不变量：`[DONE]` 可忽略；其余事件必须显式命名且与 body type 完全一致。
/// 失败：缺少名称、类型不一致或 Responses 语义非法时返回结构化流错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_named_event(
    event: &SseEvent,
    calls: &mut BTreeMap<String, ResponseCallState>,
) -> Result<Vec<ProviderEvent>, AgentError> {
    let data = event.data.trim();
    if data.is_empty() || data == "[DONE]" {
        return Ok(Vec::new());
    }
    let value: Value = serde_json::from_str(data).map_err(|_| {
        AgentError::new(
            ErrorCode::StreamInterrupted,
            "invalid Codex Responses stream JSON",
        )
    })?;
    let kind = value.get("type").and_then(Value::as_str).ok_or_else(|| {
        AgentError::new(
            ErrorCode::StreamInterrupted,
            "Codex Responses event is missing type",
        )
    })?;
    if event.event.as_deref() != Some(kind) {
        return Err(AgentError::new(
            ErrorCode::StreamInterrupted,
            "Codex Responses event name does not match its type",
        ));
    }
    openai_responses::parse_event(event, calls)
}

/// 功能：创建缺少 Codex OAuth bearer 的稳定脱敏配置错误。
///
/// 输入：无。
/// 输出：不可重试的 ProviderUnavailable 错误。
/// 不变量：不披露 token、endpoint、模型、账户或环境变量。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn missing_credential_error() -> AgentError {
    AgentError::new(
        ErrorCode::ProviderUnavailable,
        "Codex OAuth credential is not configured",
    )
    .with_details(json!({"kind":"provider_unavailable"}))
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeMap;

    use serde_json::json;

    use super::{parse_named_event, request_body};
    use crate::domain::{Message, Role};
    use crate::provider::{ProviderRequest, SseEvent};

    /// 功能：验证 Codex 请求冻结 store:false 且拒绝 SSE 名称与 JSON type 不一致。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn freezes_codex_body_and_named_events() {
        let body = request_body(&ProviderRequest {
            model: "mock-codex-v1".to_owned(),
            messages: vec![Message::text("user", Role::User, "hello")],
            tools: Vec::new(),
            max_output_tokens: None,
            session_id: None,
            run_id: None,
        });
        assert_eq!(body["stream"], true);
        assert_eq!(body["store"], false);

        let event = SseEvent {
            event: Some("response.output_text.done".to_owned()),
            data: json!({"type":"response.output_text.delta","delta":"x"}).to_string(),
            id: None,
        };
        assert!(parse_named_event(&event, &mut BTreeMap::new()).is_err());
    }
}
