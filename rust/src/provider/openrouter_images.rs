use async_trait::async_trait;
use base64::Engine as _;
use base64::engine::general_purpose::STANDARD as BASE64_STANDARD;
use futures_util::stream;
use reqwest::header::{AUTHORIZATION, CONTENT_LENGTH, CONTENT_TYPE, HeaderValue};
use serde_json::{Value, json};
use tokio_util::sync::CancellationToken;

use super::sse::{
    build_http_client, native_endpoint, next_response_chunk, provider_status_error,
    send_provider_request,
};
use super::{Provider, ProviderRequest, ProviderStream};
use crate::domain::{
    ContentBlock, FinishReason, ProviderEvent, ProviderImage, Role, Usage, validate_image_signature,
};
use crate::error::{AgentError, ErrorCode};
use crate::protocol::parse_strict_value;
use crate::session::SessionStore;

const API_FAMILY: &str = "openrouter-images";
const PROVIDER_ID: &str = "openrouter";
const MAX_IMAGE_COUNT: usize = 8;
const MAX_INPUT_IMAGE_BYTES: usize = 16_777_216;
const MAX_OUTPUT_IMAGE_BYTES: usize = 33_554_432;
const MAX_RESPONSE_BYTES: usize = 50_331_648;
const MAX_TEXT_BYTES: usize = 262_144;
const MAX_SAFE_INTEGER: u64 = 9_007_199_254_740_991;

const IMAGE_MODELS: &[&str] = &[
    "black-forest-labs/flux.2-flex",
    "black-forest-labs/flux.2-klein-4b",
    "black-forest-labs/flux.2-max",
    "black-forest-labs/flux.2-pro",
    "bytedance-seed/seedream-4.5",
    "google/gemini-2.5-flash-image",
    "google/gemini-3-pro-image",
    "google/gemini-3-pro-image-preview",
    "google/gemini-3.1-flash-image",
    "google/gemini-3.1-flash-image-preview",
    "google/gemini-3.1-flash-lite-image",
    "microsoft/mai-image-2.5",
    "openai/gpt-5-image",
    "openai/gpt-5-image-mini",
    "openai/gpt-5.4-image-2",
    "openai/gpt-image-1",
    "openai/gpt-image-1-mini",
    "openai/gpt-image-2",
    "openrouter/auto",
    "recraft/recraft-v3",
    "recraft/recraft-v4",
    "recraft/recraft-v4-pro",
    "recraft/recraft-v4-pro-vector",
    "recraft/recraft-v4-vector",
    "recraft/recraft-v4.1",
    "recraft/recraft-v4.1-pro",
    "recraft/recraft-v4.1-pro-vector",
    "recraft/recraft-v4.1-utility",
    "recraft/recraft-v4.1-utility-pro",
    "recraft/recraft-v4.1-vector",
    "sourceful/riverflow-v2-fast",
    "sourceful/riverflow-v2-pro",
    "sourceful/riverflow-v2.5-fast",
    "sourceful/riverflow-v2.5-pro",
    "x-ai/grok-imagine-image-quality",
];

const TEXT_OUTPUT_MODELS: &[&str] = &[
    "google/gemini-2.5-flash-image",
    "google/gemini-3-pro-image",
    "google/gemini-3-pro-image-preview",
    "google/gemini-3.1-flash-image",
    "google/gemini-3.1-flash-image-preview",
    "google/gemini-3.1-flash-lite-image",
    "openai/gpt-5-image",
    "openai/gpt-5-image-mini",
    "openai/gpt-5.4-image-2",
    "openrouter/auto",
];

/// OpenRouter Images 非流式原生 HTTP Provider。
///
/// 不变量：实例不保存 credential；endpoint 在构造边界完成同源校验；Session store 只用于
/// 复核当前 Session 的 input `image_ref`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Clone)]
pub struct OpenRouterImagesProvider {
    client: Result<reqwest::Client, AgentError>,
    endpoint: Result<reqwest::Url, AgentError>,
    sessions: SessionStore,
}

struct ParsedCompletion {
    response_id: String,
    text: Option<String>,
    images: Vec<ProviderImage>,
    usage: Option<Usage>,
}

impl OpenRouterImagesProvider {
    /// 功能：创建不读取 credential、也不发起网络请求的 OpenRouter Images adapter。
    ///
    /// 输入：生产清单或 conformance runner 提供的 base URL，以及当前 Session store。
    /// 输出：固定绑定 `openrouter-images` family 的 Rust 原生 Provider。
    /// 不变量：生产只接受 HTTPS；conformance 只额外接受字面 `127.0.0.1` HTTP；禁止代理和重定向。
    /// 失败：非法 endpoint 或 HTTP 客户端初始化错误延迟到 `stream` 以脱敏结构化错误返回。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn new(base_url: impl AsRef<str>, sessions: SessionStore) -> Self {
        Self {
            client: build_http_client(),
            endpoint: native_endpoint(base_url.as_ref(), "/chat/completions", &[]),
            sessions,
        }
    }

    /// 功能：把最新 user 的 portable text/image_ref 顺序转换为原生非流式 JSON body。
    ///
    /// 输入：含当前 Session ID、冻结 modelId 与 selected-context 消息的 Provider 请求。
    /// 输出：紧凑 UTF-8 JSON；image bytes 仅以请求局部 canonical data URL 存在。
    /// 不变量：最多八图、总输入图像 16 MiB、文字 256 KiB；每个引用均重新执行 no-follow/hash/length/media/魔数复核。
    /// 失败：模型、内容、Session 引用、大小或 JSON 编码违规返回不含路径和图像数据的结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn request_body(&self, request: &ProviderRequest) -> Result<Vec<u8>, AgentError> {
        if !self.supports_model(&request.model) {
            return Err(request_error(
                "OpenRouter image model is not in the frozen catalog",
            ));
        }
        let session_id = request
            .session_id
            .as_deref()
            .ok_or_else(|| request_error("OpenRouter image request has no Session identity"))?;
        let message = request
            .messages
            .iter()
            .rev()
            .find(|message| message.role == Role::User)
            .ok_or_else(|| request_error("OpenRouter image request has no user message"))?;
        if message.content.is_empty() || message.content.len() > 128 {
            return Err(request_error("OpenRouter image input content is invalid"));
        }

        let mut content = Vec::with_capacity(message.content.len());
        let mut image_count = 0_usize;
        let mut image_bytes = 0_usize;
        let mut text_bytes = 0_usize;
        for block in &message.content {
            match block {
                ContentBlock::Text { text } => {
                    text_bytes = text_bytes.checked_add(text.len()).ok_or_else(|| {
                        request_limit_error("OpenRouter image input text is too large")
                    })?;
                    if text_bytes > MAX_TEXT_BYTES {
                        return Err(request_limit_error(
                            "OpenRouter image input text is too large",
                        ));
                    }
                    content.push(json!({"type":"text","text":text}));
                }
                ContentBlock::ImageRef { artifact, .. } => {
                    image_count += 1;
                    if image_count > MAX_IMAGE_COUNT {
                        return Err(request_limit_error(
                            "OpenRouter image input count is too large",
                        ));
                    }
                    let bytes = self
                        .sessions
                        .read_verified_image_artifact(session_id, artifact, MAX_INPUT_IMAGE_BYTES)
                        .await?;
                    image_bytes = image_bytes.checked_add(bytes.len()).ok_or_else(|| {
                        request_limit_error("OpenRouter image input is too large")
                    })?;
                    if image_bytes > MAX_INPUT_IMAGE_BYTES {
                        return Err(request_limit_error("OpenRouter image input is too large"));
                    }
                    let encoded = BASE64_STANDARD.encode(bytes);
                    content.push(json!({
                        "type":"image_url",
                        "image_url":{"url":format!(
                            "data:{};base64,{encoded}", artifact.media_type
                        )}
                    }));
                }
                _ => {
                    return Err(request_error(
                        "OpenRouter image input contains an unsupported content block",
                    ));
                }
            }
        }
        let body = json!({
            "model":request.model,
            "messages":[{"role":"user","content":content}],
            "stream":false,
            "modalities":modalities(&request.model)
        });
        serde_json::to_vec(&body)
            .map_err(|_| request_error("OpenRouter image request JSON serialization failed"))
    }
}

#[async_trait]
impl Provider for OpenRouterImagesProvider {
    /// 功能：返回冻结的 OpenRouter Provider ID。
    ///
    /// 输入：当前 adapter。
    /// 输出：品牌清单 ID `openrouter`。
    /// 不变量：不会返回项目品牌、endpoint 或 credential。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn id(&self) -> &str {
        PROVIDER_ID
    }

    /// 功能：返回强制显式路由的品牌中立图像 API family。
    ///
    /// 输入：当前 adapter。
    /// 输出：始终为 `Some("openrouter-images")`。
    /// 不变量：不从 modelId 推断 family。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn api_family(&self) -> Option<&str> {
        Some(API_FAMILY)
    }

    /// 功能：只允许冻结 v1 清单中的三十五个图像 modelId。
    ///
    /// 输入：客户端 Provider selection 的 modelId。
    /// 输出：精确命中冻结清单时为 true。
    /// 不变量：不接受动态模型，也不根据一次成功请求提升支持状态。
    /// 失败：本方法不执行 I/O 且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn supports_model(&self, model_id: &str) -> bool {
        IMAGE_MODELS.contains(&model_id)
    }

    /// 功能：执行一次非流式 OpenRouter Images HTTP 请求并返回内存内 artifact 候选事件。
    ///
    /// 输入：selected Session 消息、冻结模型和运行级取消令牌。
    /// 输出：`MessageStart`、单个无序列化能力的 `ImageCompletion`、可选 usage 与 `MessageEnd`。
    /// 不变量：credential 在最终 header 边界读取并立即释放；完整响应和全批图像先验证，绝不产生 text/image delta。
    /// 失败：取消、timeout、HTTP、断流、UTF-8、strict JSON、base64、MIME、魔数、usage 或限制错误均脱敏返回。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn stream(
        &self,
        request: ProviderRequest,
        cancellation: CancellationToken,
    ) -> Result<ProviderStream, AgentError> {
        let body = tokio::select! {
            biased;
            () = cancellation.cancelled() => return Err(cancelled_error()),
            result = self.request_body(&request) => result?,
        };
        let client = self.client.as_ref().map_err(Clone::clone)?;
        let endpoint = self.endpoint.as_ref().map_err(Clone::clone)?;
        let api_key = std::env::var("OPENROUTER_API_KEY")
            .ok()
            .filter(|value| !value.is_empty())
            .ok_or_else(|| {
                AgentError::new(
                    ErrorCode::ProviderUnavailable,
                    "OpenRouter image credential is unavailable",
                )
            })?;
        let authorization = HeaderValue::from_str(&format!("Bearer {api_key}")).map_err(|_| {
            AgentError::new(
                ErrorCode::ProviderUnavailable,
                "OpenRouter image credential is invalid",
            )
        })?;
        let builder = client
            .post(endpoint.clone())
            .header(CONTENT_TYPE, "application/json")
            .header(AUTHORIZATION, authorization.clone())
            .body(body);
        let response = send_provider_request(builder, &cancellation).await;
        drop(authorization);
        drop(api_key);
        let mut response = response?;
        if !response.status().is_success() {
            return Err(provider_status_error(&response));
        }
        let raw = read_bounded_response(&mut response, &cancellation).await?;
        let text = std::str::from_utf8(&raw)
            .map_err(|_| protocol_error("OpenRouter image response is not valid UTF-8"))?;
        let value = parse_strict_value(text)
            .map_err(|_| protocol_error("OpenRouter image response is not strict JSON"))?;
        let completion = parse_completion(&value)?;
        let mut events = vec![Ok(ProviderEvent::MessageStart {
            provider_message_id: completion.response_id,
        })];
        events.push(Ok(ProviderEvent::ImageCompletion {
            text: completion.text,
            images: completion.images,
        }));
        if let Some(usage) = completion.usage {
            events.push(Ok(ProviderEvent::Usage(usage)));
        }
        events.push(Ok(ProviderEvent::MessageEnd {
            finish_reason: FinishReason::Stop,
        }));
        Ok(Box::pin(stream::iter(events)))
    }
}

/// 功能：在 content-length、逐 chunk、idle timeout 与取消边界下读取完整 JSON 响应。
///
/// 输入：成功 HTTP response 和运行级取消令牌。
/// 输出：最多 48 MiB 的原始响应字节。
/// 不变量：不把正文、URL 或底层网络错误写入错误；断流在首个 ProviderEvent 前完成分类。
/// 失败：超限、取消、timeout 或断流返回可移植结构化错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn read_bounded_response(
    response: &mut reqwest::Response,
    cancellation: &CancellationToken,
) -> Result<Vec<u8>, AgentError> {
    if response
        .headers()
        .get(CONTENT_LENGTH)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.parse::<u64>().ok())
        .is_some_and(|length| length > MAX_RESPONSE_BYTES as u64)
    {
        return Err(output_limit_error(
            "OpenRouter image response exceeded the byte limit",
        ));
    }
    let mut bytes = Vec::new();
    loop {
        let chunk = next_response_chunk(response, cancellation).await?;
        let Some(chunk) = chunk else {
            break;
        };
        let next_length = bytes.len().checked_add(chunk.len()).ok_or_else(|| {
            output_limit_error("OpenRouter image response exceeded the byte limit")
        })?;
        if next_length > MAX_RESPONSE_BYTES {
            return Err(output_limit_error(
                "OpenRouter image response exceeded the byte limit",
            ));
        }
        bytes.extend_from_slice(&chunk);
    }
    Ok(bytes)
}

/// 功能：严格归一化 choices[0] 的可选文字、全部 image data URL 和 usage。
///
/// 输入：已通过无重复键、无尾随内容 strict JSON 解析的值。
/// 输出：完整验证且仍只在内存中的图像完成对象。
/// 不变量：最多八图且总解码字节不超过 32 MiB；首张图返回前整批均已验证。
/// 失败：Provider error、结构、文字、图像、usage 或上限违规返回不含原始值的错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_completion(value: &Value) -> Result<ParsedCompletion, AgentError> {
    let object = value
        .as_object()
        .ok_or_else(|| protocol_error("OpenRouter image response must be an object"))?;
    if object.get("error").is_some_and(|value| !value.is_null()) {
        return Err(AgentError::new(
            ErrorCode::ProviderUnavailable,
            "OpenRouter image Provider returned an error",
        ));
    }
    let response_id = match object.get("id") {
        None => "openrouter-image-response".to_owned(),
        Some(Value::String(value)) if !value.is_empty() && value.len() <= 256 => value.clone(),
        _ => return Err(protocol_error("OpenRouter image response ID is invalid")),
    };
    let choices = object
        .get("choices")
        .and_then(Value::as_array)
        .filter(|choices| choices.len() == 1)
        .ok_or_else(|| protocol_error("OpenRouter image choices are invalid"))?;
    let message = choices[0]
        .get("message")
        .and_then(Value::as_object)
        .ok_or_else(|| protocol_error("OpenRouter image message is invalid"))?;
    let text = match message.get("content") {
        None | Some(Value::Null) => None,
        Some(Value::String(text)) if text.len() <= MAX_TEXT_BYTES => {
            (!text.is_empty()).then(|| text.clone())
        }
        _ => return Err(protocol_error("OpenRouter image text output is invalid")),
    };
    let image_values = message
        .get("images")
        .and_then(Value::as_array)
        .filter(|images| !images.is_empty() && images.len() <= MAX_IMAGE_COUNT)
        .ok_or_else(|| protocol_error("OpenRouter image output count is invalid"))?;
    let mut images = Vec::with_capacity(image_values.len());
    let mut total_bytes = 0_usize;
    for value in image_values {
        let url = value
            .get("image_url")
            .and_then(Value::as_object)
            .and_then(|image_url| image_url.get("url"))
            .and_then(Value::as_str)
            .ok_or_else(|| protocol_error("OpenRouter image URL is invalid"))?;
        let image = decode_data_url(url)?;
        total_bytes = total_bytes
            .checked_add(image.bytes.len())
            .ok_or_else(|| output_limit_error("OpenRouter image output is too large"))?;
        if total_bytes > MAX_OUTPUT_IMAGE_BYTES {
            return Err(output_limit_error("OpenRouter image output is too large"));
        }
        images.push(image);
    }
    let usage = match object.get("usage") {
        None | Some(Value::Null) => None,
        Some(value) => Some(parse_usage(value)?),
    };
    Ok(ParsedCompletion {
        response_id,
        text,
        images,
        usage,
    })
}

/// 功能：严格解码 allowlist MIME 的 canonical base64 image data URL。
///
/// 输入：未经信任的 `image_url.url` 字符串。
/// 输出：MIME 与基础魔数一致的非空图像字节。
/// 不变量：拒绝 remote URL、percent encoding、空白/宽松 base64、SVG、大小写 MIME 和非 canonical padding。
/// 失败：格式/MIME/base64/魔数违规返回脱敏 protocol error，字节上限违规返回 output limit。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn decode_data_url(value: &str) -> Result<ProviderImage, AgentError> {
    if value.len() > MAX_OUTPUT_IMAGE_BYTES.saturating_mul(4) / 3 + 1_024 {
        return Err(output_limit_error("OpenRouter image data URL is too large"));
    }
    let (media_type, encoded) = ["image/png", "image/jpeg", "image/webp", "image/gif"]
        .iter()
        .find_map(|media_type| {
            value
                .strip_prefix(&format!("data:{media_type};base64,"))
                .map(|encoded| ((*media_type).to_owned(), encoded))
        })
        .ok_or_else(|| {
            protocol_error("OpenRouter image URL must be a supported base64 data URL")
        })?;
    if encoded.is_empty() || encoded.len() % 4 != 0 || !encoded.is_ascii() {
        return Err(protocol_error("OpenRouter image base64 is invalid"));
    }
    let bytes = BASE64_STANDARD
        .decode(encoded.as_bytes())
        .map_err(|_| protocol_error("OpenRouter image base64 is invalid"))?;
    if bytes.is_empty() || bytes.len() > MAX_OUTPUT_IMAGE_BYTES {
        return Err(output_limit_error("OpenRouter image output is too large"));
    }
    if BASE64_STANDARD.encode(&bytes) != encoded {
        return Err(protocol_error("OpenRouter image base64 is not canonical"));
    }
    validate_image_signature(&media_type, &bytes)?;
    Ok(ProviderImage { media_type, bytes })
}

/// 功能：严格映射 OpenAI-style 三项 token usage。
///
/// 输入：strict JSON 中存在的 usage 值。
/// 输出：非负 JavaScript safe integer 且 total 自洽的公共 Usage。
/// 不变量：不从缺失字段推断数值，且 `total = prompt + completion`。
/// 失败：对象、字段、范围或总数违规返回脱敏 protocol error。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_usage(value: &Value) -> Result<Usage, AgentError> {
    let object = value
        .as_object()
        .ok_or_else(|| protocol_error("OpenRouter image usage is invalid"))?;
    let prompt = safe_usage_integer(object.get("prompt_tokens"))?;
    let completion = safe_usage_integer(object.get("completion_tokens"))?;
    let total = safe_usage_integer(object.get("total_tokens"))?;
    if prompt.checked_add(completion) != Some(total) {
        return Err(protocol_error("OpenRouter image usage total is invalid"));
    }
    Ok(Usage {
        input_tokens: prompt,
        output_tokens: completion,
        total_tokens: total,
        cached_input_tokens: 0,
        cost_micros: None,
    })
}

/// 功能：读取一个必需的非负 JavaScript safe-integer usage 字段。
///
/// 输入：可选 strict JSON number 引用。
/// 输出：`0..=2^53-1` 的 u64。
/// 不变量：JSON bool、浮点数、负数和超 safe-integer 均不接受。
/// 失败：缺失或越界返回不含原始数值的 protocol error。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn safe_usage_integer(value: Option<&Value>) -> Result<u64, AgentError> {
    value
        .and_then(Value::as_u64)
        .filter(|value| *value <= MAX_SAFE_INTEGER)
        .ok_or_else(|| protocol_error("OpenRouter image usage token value is invalid"))
}

/// 功能：按冻结模型能力返回精确 OpenRouter output modalities。
///
/// 输入：已通过冻结目录验证的 modelId。
/// 输出：文本输出模型为 `["image","text"]`，其余为 `["image"]`。
/// 不变量：未知模型不通过 `supports_model`，本函数不动态推断能力。
/// 失败：本函数不执行 I/O 且不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn modalities(model_id: &str) -> Vec<&'static str> {
    if TEXT_OUTPUT_MODELS.contains(&model_id) {
        vec!["image", "text"]
    } else {
        vec!["image"]
    }
}

/// 功能：创建客户端输入或冻结目录违规的 Provider 请求错误。
///
/// 输入：由实现常量提供且不含用户数据的消息。
/// 输出：不可重试 `InvalidParams` 错误。
/// 不变量：不得传入 credential、URL、path、data URL 或原始正文。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn request_error(message: &'static str) -> AgentError {
    AgentError::new(ErrorCode::InvalidParams, message)
        .with_details(json!({"kind":"provider_request_invalid"}))
}

/// 功能：创建输入资源上限错误。
///
/// 输入：由实现常量提供且不含用户数据的消息。
/// 输出：不可重试 `OutputLimitExceeded` 错误。
/// 不变量：错误不含图像、文字、path、URL 或 credential 内容。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn request_limit_error(message: &'static str) -> AgentError {
    AgentError::new(ErrorCode::OutputLimitExceeded, message)
        .with_details(json!({"kind":"provider_request_limit"}))
}

/// 功能：创建响应协议错误。
///
/// 输入：由实现常量提供且不含 Provider 原始值的消息。
/// 输出：不可重试 `ProviderError` 错误。
/// 不变量：错误不含 response body、base64、data/remote URL、path 或 credential。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn protocol_error(message: &'static str) -> AgentError {
    AgentError::new(ErrorCode::ProviderError, message)
        .with_details(json!({"kind":"provider_protocol_error"}))
}

/// 功能：创建响应资源上限错误。
///
/// 输入：由实现常量提供且不含 Provider 原始值的消息。
/// 输出：不可重试 `OutputLimitExceeded` 错误。
/// 不变量：错误仅描述上限分类，不回显正文或实际图像数据。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn output_limit_error(message: &'static str) -> AgentError {
    AgentError::new(ErrorCode::OutputLimitExceeded, message)
        .with_details(json!({"kind":"provider_output_limit"}))
}

/// 功能：创建 OpenRouter Images 本地取消的稳定错误。
///
/// 输入：无。
/// 输出：不可重试、无 Provider 数据的 `Cancelled` 错误。
/// 不变量：错误不含请求 body、artifact bytes、URL、path 或 credential。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn cancelled_error() -> AgentError {
    AgentError::new(
        ErrorCode::Cancelled,
        "OpenRouter image request was cancelled",
    )
}

#[cfg(test)]
mod tests {
    use base64::Engine as _;
    use base64::engine::general_purpose::STANDARD as BASE64_STANDARD;
    use serde_json::json;

    use super::{IMAGE_MODELS, decode_data_url, modalities, parse_usage};

    /// 功能：验证 canonical PNG data URL 被解码且 remote、宽松 base64 与 MIME 欺骗被拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn strictly_decodes_image_data_urls() -> Result<(), crate::error::AgentError> {
        let png = b"\x89PNG\r\n\x1a\nfixture";
        let url = format!("data:image/png;base64,{}", BASE64_STANDARD.encode(png));
        let image = decode_data_url(&url)?;
        assert_eq!(image.media_type, "image/png");
        assert_eq!(image.bytes, png);
        assert!(decode_data_url("https://invalid.example/image.png").is_err());
        assert!(decode_data_url("data:image/png;base64,!!!!").is_err());
        let mismatch = format!("data:image/jpeg;base64,{}", BASE64_STANDARD.encode(png));
        assert!(decode_data_url(&mismatch).is_err());
        Ok(())
    }

    /// 功能：验证 usage 必须为安全整数且 total 与输入输出之和严格一致。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn validates_usage_invariants() -> Result<(), crate::error::AgentError> {
        let usage = parse_usage(&json!({
            "prompt_tokens":7,
            "completion_tokens":5,
            "total_tokens":12
        }))?;
        assert_eq!(usage.total_tokens, 12);
        assert!(
            parse_usage(&json!({
                "prompt_tokens":7,
                "completion_tokens":5,
                "total_tokens":13
            }))
            .is_err()
        );
        Ok(())
    }

    /// 功能：验证 fixture 模型使用 image/text 顺序且纯图模型不臆测文本能力。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn chooses_frozen_modalities() {
        assert_eq!(IMAGE_MODELS.len(), 35);
        assert!(IMAGE_MODELS.windows(2).all(|pair| pair[0] < pair[1]));
        assert_eq!(
            modalities("google/gemini-2.5-flash-image"),
            vec!["image", "text"]
        );
        assert_eq!(modalities("black-forest-labs/flux.2-max"), vec!["image"]);
    }
}
