use async_stream::try_stream;
use async_trait::async_trait;
use reqwest::header::{AUTHORIZATION, CONTENT_TYPE};
use serde_json::json;
use tokio_util::sync::CancellationToken;

use super::google::{self, GoogleStreamState};
use super::sse::{
    build_http_client, native_endpoint, next_response_chunk, provider_status_error,
    send_provider_request,
};
use super::{Provider, ProviderRequest, ProviderStream, SseDecoder};
use crate::error::{AgentError, ErrorCode};

/// Google Vertex AI streamGenerateContent API family 的独立原生 Rust Provider。
///
/// 不变量：OAuth token 仅进入最终请求头；project、location 与 model 均经单段字符约束后才进入路径。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Clone)]
pub struct GoogleVertexProvider {
    id: String,
    client: Result<reqwest::Client, AgentError>,
    base_url: String,
    oauth_token: Option<String>,
    project: String,
    location: String,
}

impl GoogleVertexProvider {
    /// 功能：创建具有固定 project/location 资源边界的 Vertex OAuth/SSE Provider。
    ///
    /// 输入：注册 ID、可信 base URL、可选 OAuth token、Google project 与 location。
    /// 输出：尚未发起网络请求的原生 Provider。
    /// 不变量：构造阶段不输出或持久化 token；动态路径值在 `stream` 调用边界重新验证。
    /// 失败：HTTP 客户端、endpoint、凭据或资源段错误在 `stream` 中返回脱敏结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn new(
        id: impl Into<String>,
        base_url: impl Into<String>,
        oauth_token: Option<String>,
        project: impl Into<String>,
        location: impl Into<String>,
    ) -> Self {
        Self {
            id: id.into(),
            client: build_http_client(),
            base_url: base_url.into(),
            oauth_token,
            project: project.into(),
            location: location.into(),
        }
    }
}

#[async_trait]
impl Provider for GoogleVertexProvider {
    /// 功能：返回 Vertex Provider 的稳定注册 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn id(&self) -> &str {
        &self.id
    }

    /// 功能：通过 Vertex 资源路径与 OAuth bearer 调用 streamGenerateContent SSE。
    ///
    /// 输入：公共消息、工具、模型请求及运行取消令牌。
    /// 输出：复用 Google GenerateContent 语义规范化后的文本、推理、工具及 usage 事件流。
    /// 不变量：authority 不受资源 ID 影响；OAuth token 不进入错误、事件、日志或 Session。
    /// 失败：配置、认证、HTTP、超时、取消、SSE、UTF-8 或 JSON 违规返回脱敏结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn stream(
        &self,
        request: ProviderRequest,
        cancellation: CancellationToken,
    ) -> Result<ProviderStream, AgentError> {
        let client = self.client.as_ref().map_err(Clone::clone)?;
        let token = self
            .oauth_token
            .as_deref()
            .ok_or_else(missing_credential_error)?;
        let path = vertex_stream_path(&self.project, &self.location, &request.model)?;
        let endpoint = native_endpoint(&self.base_url, &path, &[("alt", "sse")])?;
        let response = send_provider_request(
            client
                .post(endpoint)
                .header(CONTENT_TYPE, "application/json")
                .header(AUTHORIZATION, format!("Bearer {token}"))
                .json(&google::request_body(&request)),
            &cancellation,
        )
        .await?;
        if !response.status().is_success() {
            return Err(provider_status_error(&response));
        }

        let output = try_stream! {
            let mut response = response;
            let mut decoder = SseDecoder::new(4 * 1024 * 1024);
            let mut state = GoogleStreamState::default();
            while let Some(chunk) = next_response_chunk(&mut response, &cancellation).await? {
                for event in decoder.push(&chunk)? {
                    for normalized in google::parse_event(&event, &mut state)? {
                        yield normalized;
                    }
                }
            }
            for event in decoder.finish()? {
                for normalized in google::parse_event(&event, &mut state)? {
                    yield normalized;
                }
            }
        };
        Ok(Box::pin(output))
    }
}

/// 功能：验证 Vertex project/location/model 并构造固定发布者下的流式资源路径。
///
/// 输入：运行时 project、location 与 run/start 模型 ID。
/// 输出：`/v1/projects/...:streamGenerateContent` 路径。
/// 不变量：每个动态值只占一个受限 ASCII path segment，无法注入斜杠、查询或 authority。
/// 失败：任一资源段非法时返回不回显原值的 InvalidParams 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn vertex_stream_path(project: &str, location: &str, model: &str) -> Result<String, AgentError> {
    if !valid_resource_segment(project, true)
        || !valid_resource_segment(location, false)
        || !valid_model_segment(model)
    {
        return Err(AgentError::new(
            ErrorCode::InvalidParams,
            "Vertex resource route is invalid",
        ));
    }
    Ok(format!(
        "/v1/projects/{project}/locations/{location}/publishers/google/models/{model}:streamGenerateContent"
    ))
}

/// 功能：判断 Vertex project 或 location 是否满足单一有界 ASCII 资源段约束。
///
/// 输入：待验证文本及是否允许 project 使用冒号。
/// 输出：长度 1..128 且字符集安全时为 true。
/// 不变量：始终拒绝斜杠、百分号、反斜杠、控制符、查询及 fragment 元字符。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn valid_resource_segment(value: &str, allow_colon: bool) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value.bytes().all(|byte| {
            byte.is_ascii_alphanumeric()
                || matches!(byte, b'.' | b'_' | b'-')
                || (allow_colon && byte == b':')
        })
}

/// 功能：判断 Vertex 模型 ID 是否可安全进入一个路径段。
///
/// 输入：run/start 的模型 ID。
/// 输出：长度 1..256 且只含字母、数字、点、下划线或连字符时为 true。
/// 不变量：模型无法注入路径分隔符、查询、fragment 或控制字符。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn valid_model_segment(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 256
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
}

/// 功能：创建缺少 Vertex OAuth bearer 的稳定脱敏配置错误。
///
/// 输入：无。
/// 输出：不可重试的 ProviderUnavailable 错误。
/// 不变量：不披露 token、环境变量、project、location、模型或 endpoint。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn missing_credential_error() -> AgentError {
    AgentError::new(
        ErrorCode::ProviderUnavailable,
        "Vertex OAuth credential is not configured",
    )
    .with_details(json!({"kind":"provider_unavailable"}))
}

#[cfg(test)]
mod tests {
    use super::{valid_model_segment, valid_resource_segment, vertex_stream_path};

    /// 功能：验证 Vertex 冻结资源路径与注入字符边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn validates_vertex_route_segments() {
        assert_eq!(
            vertex_stream_path("mock-project", "us-central1", "mock-vertex-v1").as_deref(),
            Ok(
                "/v1/projects/mock-project/locations/us-central1/publishers/google/models/mock-vertex-v1:streamGenerateContent"
            )
        );
        assert!(valid_resource_segment("domain:project", true));
        assert!(!valid_resource_segment("../project", true));
        assert!(valid_model_segment("gemini-2.5-pro"));
        assert!(!valid_model_segment("model/../../escape"));
    }
}
