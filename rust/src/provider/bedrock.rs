use std::collections::BTreeMap;

use async_stream::try_stream;
use async_trait::async_trait;
use chrono::{DateTime, Utc};
use reqwest::Url;
use reqwest::header::{ACCEPT, CONTENT_TYPE};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use tokio_util::sync::CancellationToken;

use super::sse::{
    build_http_client, native_endpoint, next_response_chunk, provider_status_error,
    send_provider_request,
};
use super::{Provider, ProviderRequest, ProviderStream};
use crate::domain::{ContentBlock, FinishReason, ProviderEvent, Role, Usage};
use crate::error::{AgentError, ErrorCode};
use crate::protocol::parse_strict_value;

const MAX_EVENT_STREAM_MESSAGE_BYTES: usize = 1_048_576;
const MAX_TOOL_ARGUMENT_BYTES: usize = 1_048_576;

/// Amazon Bedrock ConverseStream SigV4 与 AWS EventStream 的独立原生 Rust Provider。
///
/// 不变量：请求正文使用 Converse 原生结构；响应必须通过长度、typed headers、双 CRC、UTF-8 与严格 JSON 校验。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Clone)]
pub struct BedrockConverseStreamProvider {
    id: String,
    client: Result<reqwest::Client, AgentError>,
    base_url: String,
    region: String,
}

impl BedrockConverseStreamProvider {
    /// 功能：创建不保存 AWS credential 的 Bedrock ConverseStream Provider。
    ///
    /// 输入：注册 ID、可信 Bedrock base URL 与 AWS region。
    /// 输出：每轮发送时才读取并派生 SigV4 key 的原生 Provider。
    /// 不变量：实例仅保存非敏感 region；不访问 metadata、profile、SDK 或其他语言运行时。
    /// 失败：客户端、endpoint、region 或凭据错误在 `stream` 调用边界返回脱敏结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn new(
        id: impl Into<String>,
        base_url: impl Into<String>,
        region: impl Into<String>,
    ) -> Self {
        Self {
            id: id.into(),
            client: build_http_client(),
            base_url: base_url.into(),
            region: region.into(),
        }
    }
}

#[async_trait]
impl Provider for BedrockConverseStreamProvider {
    /// 功能：返回 Bedrock Provider 的稳定注册 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn id(&self) -> &str {
        &self.id
    }

    /// 功能：SigV4 签名并调用 Bedrock ConverseStream，再规范化可信 EventStream 事件。
    ///
    /// 输入：公共消息、工具、模型选择与运行取消令牌。
    /// 输出：文本、推理、完整工具调用、usage 与消息终态的统一异步事件流。
    /// 不变量：二进制正文绝不经过 SSE；credential 仅存在于当前签名调用栈且不进入可观察输出。
    /// 失败：配置、认证、HTTP、超时、取消、长度、双 CRC、header、UTF-8、JSON 或事件顺序违规均返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn stream(
        &self,
        request: ProviderRequest,
        cancellation: CancellationToken,
    ) -> Result<ProviderStream, AgentError> {
        let client = self.client.as_ref().map_err(Clone::clone)?;
        let path = bedrock_stream_path(&request.model)?;
        let endpoint = native_endpoint(&self.base_url, &path, &[])?;
        let body = serde_json::to_vec(&bedrock_request_body(&request)).map_err(|_| {
            AgentError::new(
                ErrorCode::InvalidParams,
                "Bedrock request JSON serialization failed",
            )
        })?;
        let headers = bedrock_signed_headers(&endpoint, &body, &self.region, Utc::now())?;
        let mut builder = client
            .post(endpoint)
            .header(ACCEPT, "application/vnd.amazon.eventstream")
            .header(CONTENT_TYPE, "application/json")
            .header("x-amz-content-sha256", headers.payload_hash)
            .header("x-amz-date", headers.amz_date)
            .header("Authorization", headers.authorization)
            .body(body);
        if let Some(session_token) = headers.session_token {
            builder = builder.header("x-amz-security-token", session_token);
        }
        let response = send_provider_request(builder, &cancellation).await?;
        if !response.status().is_success() {
            return Err(provider_status_error(&response));
        }
        if !response_content_type_is_eventstream(&response) {
            return Err(protocol_error("Bedrock response content type is invalid"));
        }

        let output = try_stream! {
            let mut response = response;
            let mut decoder = AwsEventStreamDecoder::new(MAX_EVENT_STREAM_MESSAGE_BYTES)?;
            let mut state = BedrockStreamState::default();
            while let Some(chunk) = next_response_chunk(&mut response, &cancellation).await? {
                for event in decoder.push(&chunk)? {
                    for normalized in consume_event(event, &mut state)? {
                        yield normalized;
                    }
                }
            }
            decoder.finish()?;
            finalize_stream(&state)?;
        };
        Ok(Box::pin(output))
    }
}

/// Bedrock SigV4 请求发送边界需要的非持久化 header 值。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
struct SignedHeaders {
    payload_hash: String,
    amz_date: String,
    session_token: Option<String>,
    authorization: String,
}

/// 已通过 AWS EventStream 全部传输层校验的单个 JSON 事件。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
struct AwsEventStreamEvent {
    message_type: String,
    event_type: String,
    payload: Value,
}

/// 增量 AWS EventStream decoder，只缓冲一个有界的未完成 frame。
///
/// 不变量：任何未通过双 CRC 的 payload 都不会交给 Bedrock family 状态机。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
struct AwsEventStreamDecoder {
    buffer: Vec<u8>,
    max_message_bytes: usize,
}

impl AwsEventStreamDecoder {
    /// 功能：创建固定单 frame 字节上限的空 AWS EventStream decoder。
    ///
    /// 输入：允许的单消息最大总字节数。
    /// 输出：尚未消费任何响应字节的 decoder。
    /// 不变量：上限必须覆盖 16 字节最小 frame 且不超过 64 MiB 防御边界。
    /// 失败：上限非法时返回 OutputLimitExceeded。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn new(max_message_bytes: usize) -> Result<Self, AgentError> {
        if !(16..=64 * 1024 * 1024).contains(&max_message_bytes) {
            return Err(AgentError::new(
                ErrorCode::OutputLimitExceeded,
                "AWS EventStream message limit is invalid",
            ));
        }
        Ok(Self {
            buffer: Vec::new(),
            max_message_bytes,
        })
    }

    /// 功能：接收任意网络分片并返回其中所有完整且可信的 EventStream 事件。
    ///
    /// 输入：可能切在 prelude、任一 CRC、typed header、UTF-8 或 JSON token 中间的字节。
    /// 输出：按 wire 顺序通过长度、双 CRC、header 与 payload 校验的事件。
    /// 不变量：残片总量有界；prelude CRC 在等待完整 frame 前验证；消费顺序不改变。
    /// 失败：长度、CRC、header、UTF-8、严格 JSON 或资源上限违规时 fail closed。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn push(&mut self, chunk: &[u8]) -> Result<Vec<AwsEventStreamEvent>, AgentError> {
        self.buffer.extend_from_slice(chunk);
        let mut events = Vec::new();
        while self.buffer.len() >= 12 {
            let total_length = read_u32(&self.buffer, 0)? as usize;
            let headers_length = read_u32(&self.buffer, 4)? as usize;
            if total_length < 16 || total_length > self.max_message_bytes {
                return Err(protocol_error("AWS EventStream message length is invalid"));
            }
            if headers_length > total_length - 16 {
                return Err(protocol_error("AWS EventStream header length is invalid"));
            }
            let expected_prelude_crc = read_u32(&self.buffer, 8)?;
            if crc32(&self.buffer[..8]) != expected_prelude_crc {
                return Err(protocol_error("AWS EventStream prelude CRC is invalid"));
            }
            if self.buffer.len() < total_length {
                break;
            }
            let frame = self.buffer[..total_length].to_vec();
            self.buffer.drain(..total_length);
            events.push(decode_eventstream_frame(&frame, headers_length)?);
        }
        if self.buffer.len() > self.max_message_bytes {
            return Err(AgentError::new(
                ErrorCode::OutputLimitExceeded,
                "AWS EventStream buffered message exceeded its limit",
            ));
        }
        Ok(events)
    }

    /// 功能：在 HTTP EOF 处确认没有残缺的二进制 EventStream frame。
    ///
    /// 输入：decoder 当前残片状态。
    /// 输出：缓冲区为空时成功。
    /// 不变量：EOF 不会补齐、忽略或猜测任何 frame 字节。
    /// 失败：存在任意残片时返回可重试 StreamInterrupted。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn finish(self) -> Result<(), AgentError> {
        if self.buffer.is_empty() {
            Ok(())
        } else {
            Err(AgentError::new(
                ErrorCode::StreamInterrupted,
                "AWS EventStream ended during a message",
            )
            .retryable(true))
        }
    }
}

/// Bedrock 工具 block 的身份、partial JSON 与结束状态累积器。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
struct BedrockToolAccumulator {
    call_id: String,
    name: String,
    arguments: String,
    stopped: bool,
}

/// 单次 Bedrock 消息流的顺序、工具、usage 与完成状态。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Default)]
struct BedrockStreamState {
    started: bool,
    stopped: bool,
    metadata: bool,
    stop_reason: Option<String>,
    tools: BTreeMap<u64, BedrockToolAccumulator>,
}

/// 功能：把公共历史消息映射为 Bedrock Converse `messages/system` 原生结构。
///
/// 输入：按 durable 顺序排列的消息与内容块。
/// 输出：包含 messages、可选 system/toolConfig/inferenceConfig 的 JSON 对象。
/// 不变量：模型 ID 不进入正文；工具结果只引用自身 call ID；工具 Schema 深复制到 inputSchema.json。
/// 失败：纯内存映射不返回错误；图像和 artifact 在文本 family 中显式忽略。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn bedrock_request_body(request: &ProviderRequest) -> Value {
    let mut messages = Vec::<Value>::new();
    let mut system = Vec::<Value>::new();
    for message in &request.messages {
        if message.role == Role::System {
            for block in &message.content {
                if let ContentBlock::Text { text } = block {
                    system.push(json!({"text":text}));
                }
            }
            continue;
        }
        let mut content = Vec::<Value>::new();
        for block in &message.content {
            match block {
                ContentBlock::Text { text } => content.push(json!({"text":text})),
                ContentBlock::Reasoning { text, .. } if message.role == Role::Assistant => {
                    content.push(json!({"text":text}));
                }
                ContentBlock::ToolCall {
                    call_id,
                    name,
                    arguments,
                } if message.role == Role::Assistant => content.push(json!({
                    "toolUse":{"toolUseId":call_id,"name":name,"input":arguments}
                })),
                ContentBlock::ToolResult {
                    call_id,
                    output,
                    is_error,
                    ..
                } => content.push(json!({
                    "toolResult":{
                        "toolUseId":call_id,
                        "content":[{"text":output.text.as_deref().unwrap_or("tool result stored as artifact")}],
                        "status":if *is_error {"error"} else {"success"}
                    }
                })),
                ContentBlock::Reasoning { .. }
                | ContentBlock::ToolCall { .. }
                | ContentBlock::ImageRef { .. }
                | ContentBlock::ArtifactRef { .. } => {}
            }
        }
        if !content.is_empty() {
            messages.push(json!({
                "role":if message.role == Role::Assistant {"assistant"} else {"user"},
                "content":content
            }));
        }
    }
    let mut body = json!({"messages":messages});
    if !system.is_empty() {
        body["system"] = Value::Array(system);
    }
    if !request.tools.is_empty() {
        body["toolConfig"] = json!({
            "tools":request.tools.iter().map(|tool| json!({
                "toolSpec":{
                    "name":tool.name,
                    "description":tool.description,
                    "inputSchema":{"json":tool.input_schema}
                }
            })).collect::<Vec<_>>()
        });
    }
    if let Some(limit) = request.max_output_tokens {
        body["inferenceConfig"] = json!({"maxTokens":limit});
    }
    body
}

/// 功能：验证 Bedrock 模型 ID 并构造 ConverseStream 固定请求路径。
///
/// 输入：run/start 的模型 ID。
/// 输出：`/model/{id}/converse-stream` 路径。
/// 不变量：模型只允许 1..256 个受限 ASCII 字符，不能注入路径、查询或 fragment。
/// 失败：模型非法时返回不回显其值的 InvalidParams。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn bedrock_stream_path(model: &str) -> Result<String, AgentError> {
    if model.is_empty()
        || model.len() > 256
        || !model
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-' | b':'))
    {
        return Err(AgentError::new(
            ErrorCode::InvalidParams,
            "Bedrock model ID is invalid",
        ));
    }
    Ok(format!("/model/{model}/converse-stream"))
}

/// 功能：从最终 endpoint、正文、region、时间和当前 AWS 环境凭据生成完整 SigV4 headers。
///
/// 输入：最终 URL、精确请求字节、受限 region 与 UTC 时间。
/// 输出：payload hash、日期、session token 与 Authorization 字符串。
/// 不变量：secret 仅存在于本调用栈；canonical request 与真实请求 target/header 一致。
/// 失败：region、credential、host 或 URI 无效时返回不含原值的 ProviderUnavailable。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn bedrock_signed_headers(
    endpoint: &Url,
    body: &[u8],
    region: &str,
    timestamp: DateTime<Utc>,
) -> Result<SignedHeaders, AgentError> {
    if !valid_region(region) {
        return Err(configuration_error("AWS region configuration is invalid"));
    }
    let access_key = credential("AWS_ACCESS_KEY_ID", 256, true)?;
    let secret_key = credential("AWS_SECRET_ACCESS_KEY", 8_192, false)?;
    let session_token = optional_credential("AWS_SESSION_TOKEN", 8_192)?;
    let host = canonical_host(endpoint)?;
    let payload_hash = hex_lower(&Sha256::digest(body));
    let amz_date = timestamp.format("%Y%m%dT%H%M%SZ").to_string();
    let date_stamp = timestamp.format("%Y%m%d").to_string();
    let (canonical_headers, signed_headers) = if let Some(token) = session_token.as_deref() {
        (
            format!(
                "content-type:application/json\nhost:{host}\nx-amz-content-sha256:{payload_hash}\nx-amz-date:{amz_date}\nx-amz-security-token:{token}\n"
            ),
            "content-type;host;x-amz-content-sha256;x-amz-date;x-amz-security-token",
        )
    } else {
        (
            format!(
                "content-type:application/json\nhost:{host}\nx-amz-content-sha256:{payload_hash}\nx-amz-date:{amz_date}\n"
            ),
            "content-type;host;x-amz-content-sha256;x-amz-date",
        )
    };
    let canonical_request = format!(
        "POST\n{}\n\n{canonical_headers}\n{signed_headers}\n{payload_hash}",
        canonical_uri(endpoint.path())?
    );
    let scope = format!("{date_stamp}/{region}/bedrock/aws4_request");
    let string_to_sign = format!(
        "AWS4-HMAC-SHA256\n{amz_date}\n{scope}\n{}",
        hex_lower(&Sha256::digest(canonical_request.as_bytes()))
    );
    let date_key = hmac_sha256(
        format!("AWS4{secret_key}").as_bytes(),
        date_stamp.as_bytes(),
    );
    let region_key = hmac_sha256(&date_key, region.as_bytes());
    let service_key = hmac_sha256(&region_key, b"bedrock");
    let signing_key = hmac_sha256(&service_key, b"aws4_request");
    let signature = hex_lower(&hmac_sha256(&signing_key, string_to_sign.as_bytes()));
    Ok(SignedHeaders {
        payload_hash,
        amz_date,
        session_token,
        authorization: format!(
            "AWS4-HMAC-SHA256 Credential={access_key}/{scope}, SignedHeaders={signed_headers}, Signature={signature}"
        ),
    })
}

/// 功能：读取一个必需 AWS 环境 credential 并验证长度、ASCII 与控制字符边界。
///
/// 输入：固定环境变量名、最大长度及是否使用 access-key 字符集。
/// 输出：仅供当前请求签名使用的 credential 字符串。
/// 不变量：错误不回显变量名或值；值不会被写入对象、日志、事件或 Session。
/// 失败：缺失、空值、超限或字符非法时返回脱敏 ProviderUnavailable。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn credential(name: &'static str, max_len: usize, access_key: bool) -> Result<String, AgentError> {
    let value = std::env::var(name).unwrap_or_default();
    let valid = !value.is_empty()
        && value.len() <= max_len
        && if access_key {
            value
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'_' | b'-'))
        } else {
            value.bytes().all(|byte| (0x21..=0x7e).contains(&byte))
        };
    if valid {
        Ok(value)
    } else {
        Err(configuration_error(
            "AWS credential is unavailable or invalid",
        ))
    }
}

/// 功能：读取可选 AWS session token，并在存在时验证长度、ASCII 与控制字符边界。
///
/// 输入：固定环境变量名与最大允许字节数。
/// 输出：未配置时为 None，合法时为仅供当前 SigV4 请求使用的 token。
/// 不变量：错误不回显变量名或值；空值等同于未配置且不会加入签名 headers。
/// 失败：已配置但超限或含非可打印 ASCII 时返回脱敏 ProviderUnavailable。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn optional_credential(name: &'static str, max_len: usize) -> Result<Option<String>, AgentError> {
    let value = std::env::var(name).unwrap_or_default();
    if value.is_empty() {
        return Ok(None);
    }
    if value.len() <= max_len && value.bytes().all(|byte| (0x21..=0x7e).contains(&byte)) {
        Ok(Some(value))
    } else {
        Err(configuration_error(
            "AWS credential is unavailable or invalid",
        ))
    }
}

/// 功能：验证 AWS region 只含长度有界的小写字母、数字与连字符。
///
/// 输入：运行时 region。
/// 输出：满足 1..64 ASCII 约束时为 true。
/// 不变量：region 不能注入 SigV4 scope 分隔符或控制字符。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn valid_region(region: &str) -> bool {
    !region.is_empty()
        && region.len() <= 64
        && region
            .bytes()
            .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'-')
}

/// 功能：构造与实际 HTTP Host header 一致的 SigV4 canonical host。
///
/// 输入：已验证的最终请求 URL。
/// 输出：必要时带 IPv6 方括号和显式非默认端口的 host。
/// 不变量：不接受缺失 host；输出不包含 userinfo、path、query 或 fragment。
/// 失败：URL 没有 host 时返回脱敏配置错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn canonical_host(endpoint: &Url) -> Result<String, AgentError> {
    let host = endpoint
        .host_str()
        .ok_or_else(|| configuration_error("Bedrock endpoint host is invalid"))?;
    let literal = if host.contains(':') {
        format!("[{host}]")
    } else {
        host.to_owned()
    };
    Ok(endpoint
        .port()
        .map_or(literal.clone(), |port| format!("{literal}:{port}")))
}

/// 功能：把 URL path 转换为 SigV4 RFC 3986 canonical URI。
///
/// 输入：reqwest URL 已解析 path。
/// 输出：保留 `/` 与 unreserved 字符、规范化已有转义并编码其余字节的 URI。
/// 不变量：不解码路径分隔符；百分号必须属于完整十六进制转义。
/// 失败：存在残缺百分号转义时返回脱敏配置错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn canonical_uri(path: &str) -> Result<String, AgentError> {
    let bytes = path.as_bytes();
    let mut output = String::with_capacity(bytes.len());
    let mut index = 0;
    while index < bytes.len() {
        let byte = bytes[index];
        if byte.is_ascii_alphanumeric() || matches!(byte, b'/' | b'-' | b'_' | b'.' | b'~') {
            output.push(char::from(byte));
            index += 1;
        } else if byte == b'%' {
            if index + 2 >= bytes.len()
                || !bytes[index + 1].is_ascii_hexdigit()
                || !bytes[index + 2].is_ascii_hexdigit()
            {
                return Err(configuration_error("Bedrock canonical URI is invalid"));
            }
            output.push('%');
            output.push(char::from(bytes[index + 1].to_ascii_uppercase()));
            output.push(char::from(bytes[index + 2].to_ascii_uppercase()));
            index += 3;
        } else {
            output.push('%');
            output.push_str(&format!("{byte:02X}"));
            index += 1;
        }
    }
    Ok(if output.is_empty() {
        "/".to_owned()
    } else {
        output
    })
}

/// 功能：计算 SigV4 key derivation 和最终签名使用的 HMAC-SHA256。
///
/// 输入：任意 key 字节与消息字节。
/// 输出：固定 32 字节 MAC。
/// 不变量：遵循 64 字节 SHA-256 block 规则；不使用外部命令或语言运行时。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn hmac_sha256(key: &[u8], message: &[u8]) -> [u8; 32] {
    let mut normalized = [0_u8; 64];
    if key.len() > normalized.len() {
        normalized[..32].copy_from_slice(&Sha256::digest(key));
    } else {
        normalized[..key.len()].copy_from_slice(key);
    }
    let mut inner_pad = [0x36_u8; 64];
    let mut outer_pad = [0x5c_u8; 64];
    for ((inner, outer), key_byte) in inner_pad
        .iter_mut()
        .zip(outer_pad.iter_mut())
        .zip(normalized)
    {
        *inner ^= key_byte;
        *outer ^= key_byte;
    }
    let mut inner = Sha256::new();
    inner.update(inner_pad);
    inner.update(message);
    let inner_digest = inner.finalize();
    let mut outer = Sha256::new();
    outer.update(outer_pad);
    outer.update(inner_digest);
    outer.finalize().into()
}

/// 功能：把摘要字节编码为 SigV4 使用的小写十六进制。
///
/// 输入：任意摘要字节。
/// 输出：长度为输入两倍的小写 ASCII 字符串。
/// 不变量：编码过程不省略前导零。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn hex_lower(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut output = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        output.push(char::from(HEX[usize::from(byte >> 4)]));
        output.push(char::from(HEX[usize::from(byte & 0x0f)]));
    }
    output
}

/// 功能：判断成功响应是否声明 AWS EventStream 原生媒体类型。
///
/// 输入：尚未消费正文的 HTTP 响应。
/// 输出：Content-Type 忽略大小写后以 `application/vnd.amazon.eventstream` 开头时为 true。
/// 不变量：不会读取或记录其他响应头和正文。
/// 失败：缺失或非法 UTF-8 header 按 false 处理。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn response_content_type_is_eventstream(response: &reqwest::Response) -> bool {
    response
        .headers()
        .get(CONTENT_TYPE)
        .and_then(|value| value.to_str().ok())
        .is_some_and(|value| {
            value
                .to_ascii_lowercase()
                .starts_with("application/vnd.amazon.eventstream")
        })
}

/// 功能：读取 slice 中指定偏移的大端无符号 32 位整数。
///
/// 输入：原始 frame 字节与偏移。
/// 输出：与平台字节序无关的 u32。
/// 不变量：只读取连续四字节，不发生未对齐或 unsafe 访问。
/// 失败：越界时返回结构化协议错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn read_u32(bytes: &[u8], offset: usize) -> Result<u32, AgentError> {
    let raw: [u8; 4] = bytes
        .get(offset..offset.saturating_add(4))
        .and_then(|slice| slice.try_into().ok())
        .ok_or_else(|| protocol_error("AWS EventStream integer is truncated"))?;
    Ok(u32::from_be_bytes(raw))
}

/// 功能：读取 slice 中指定偏移的大端无符号 16 位整数。
///
/// 输入：原始 header 字节与偏移。
/// 输出：与平台字节序无关的 u16。
/// 不变量：只读取连续两字节，不发生未对齐或 unsafe 访问。
/// 失败：越界时返回结构化协议错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn read_u16(bytes: &[u8], offset: usize) -> Result<u16, AgentError> {
    let raw: [u8; 2] = bytes
        .get(offset..offset.saturating_add(2))
        .and_then(|slice| slice.try_into().ok())
        .ok_or_else(|| protocol_error("AWS EventStream integer is truncated"))?;
    Ok(u16::from_be_bytes(raw))
}

/// 功能：计算 AWS EventStream 使用的 IEEE CRC32。
///
/// 输入：prelude 八字节或不含末尾 CRC 的完整消息字节。
/// 输出：与 AWS wire 一致的 32 位校验值。
/// 不变量：多项式为反射形式 0xEDB88320，初值与终值均按 IEEE 规则处理。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn crc32(bytes: &[u8]) -> u32 {
    let mut crc = u32::MAX;
    for byte in bytes {
        crc ^= u32::from(*byte);
        for _ in 0..8 {
            crc = if crc & 1 == 1 {
                (crc >> 1) ^ 0xedb8_8320
            } else {
                crc >> 1
            };
        }
    }
    !crc
}

/// 功能：验证完整 EventStream frame 的 message CRC、typed headers、UTF-8 与严格 JSON。
///
/// 输入：已通过 prelude 长度/CRC 的完整 frame 与 header 区长度。
/// 输出：只含稳定 message/event type 与 JSON 对象的可信事件。
/// 不变量：只接受 string typed header（类型 7）、唯一 header 名和 application/json。
/// 失败：CRC、header、路由、UTF-8、重复 JSON key 或非对象 payload 违规时 fail closed。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn decode_eventstream_frame(
    frame: &[u8],
    headers_length: usize,
) -> Result<AwsEventStreamEvent, AgentError> {
    let expected_crc = read_u32(frame, frame.len().saturating_sub(4))?;
    if crc32(&frame[..frame.len().saturating_sub(4)]) != expected_crc {
        return Err(protocol_error("AWS EventStream message CRC is invalid"));
    }
    let headers_end = 12_usize
        .checked_add(headers_length)
        .ok_or_else(|| protocol_error("AWS EventStream header length overflowed"))?;
    if headers_end > frame.len().saturating_sub(4) {
        return Err(protocol_error("AWS EventStream header boundary is invalid"));
    }
    let headers = decode_eventstream_headers(&frame[12..headers_end])?;
    let message_type = headers
        .get(":message-type")
        .cloned()
        .ok_or_else(|| protocol_error("AWS EventStream message type header is missing"))?;
    if message_type != "event" && message_type != "exception" {
        return Err(protocol_error("AWS EventStream message type is invalid"));
    }
    if headers.get(":content-type").map(String::as_str) != Some("application/json") {
        return Err(protocol_error("AWS EventStream content type is invalid"));
    }
    let event_type = headers
        .get(if message_type == "exception" {
            ":exception-type"
        } else {
            ":event-type"
        })
        .filter(|value| !value.is_empty() && value.len() <= 128)
        .cloned()
        .ok_or_else(|| protocol_error("AWS EventStream event type is invalid"))?;
    let payload_text = std::str::from_utf8(&frame[headers_end..frame.len() - 4])
        .map_err(|_| protocol_error("AWS EventStream payload is invalid UTF-8"))?;
    let payload = parse_strict_value(payload_text)
        .map_err(|_| protocol_error("AWS EventStream payload is invalid JSON"))?;
    if !payload.is_object() {
        return Err(protocol_error(
            "AWS EventStream payload must be a JSON object",
        ));
    }
    Ok(AwsEventStreamEvent {
        message_type,
        event_type,
        payload,
    })
}

/// 功能：严格解析 AWS EventStream string typed-header 区域。
///
/// 输入：由 headersByteLength 精确限定的原始字节。
/// 输出：无重复名称的 UTF-8 header 映射。
/// 不变量：名称必须非空 ASCII；类型必须为 string（7）；游标精确结束于区域边界。
/// 失败：长度、类型、UTF-8、ASCII 或重复键违规时返回结构化协议错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn decode_eventstream_headers(raw: &[u8]) -> Result<BTreeMap<String, String>, AgentError> {
    let mut headers = BTreeMap::new();
    let mut cursor = 0_usize;
    while cursor < raw.len() {
        let name_length = usize::from(
            *raw.get(cursor)
                .ok_or_else(|| protocol_error("AWS EventStream header is truncated"))?,
        );
        cursor += 1;
        if name_length == 0 || cursor.saturating_add(name_length).saturating_add(3) > raw.len() {
            return Err(protocol_error("AWS EventStream header length is invalid"));
        }
        let name_bytes = &raw[cursor..cursor + name_length];
        cursor += name_length;
        if !name_bytes.is_ascii() {
            return Err(protocol_error("AWS EventStream header name is invalid"));
        }
        let name = std::str::from_utf8(name_bytes)
            .map_err(|_| protocol_error("AWS EventStream header name is invalid"))?
            .to_owned();
        if raw.get(cursor) != Some(&7) {
            return Err(protocol_error("AWS EventStream header type is unsupported"));
        }
        cursor += 1;
        let value_length = usize::from(read_u16(raw, cursor)?);
        cursor += 2;
        let value_end = cursor
            .checked_add(value_length)
            .filter(|end| *end <= raw.len())
            .ok_or_else(|| protocol_error("AWS EventStream header value is truncated"))?;
        let value = std::str::from_utf8(&raw[cursor..value_end])
            .map_err(|_| protocol_error("AWS EventStream header value is invalid UTF-8"))?
            .to_owned();
        cursor = value_end;
        if headers.insert(name, value).is_some() {
            return Err(protocol_error("AWS EventStream header is duplicated"));
        }
    }
    Ok(headers)
}

/// 功能：消费一个可信 Bedrock 原生事件并推进严格消息状态机。
///
/// 输入：通过传输层校验的事件与本次流状态。
/// 输出：本事件产生的文本、推理、工具、usage 或结束 ProviderEvent。
/// 不变量：工具 ID/name 由 block start 冻结；partial input 只按 index 追加；终态仅在 metadata 后发布。
/// 失败：exception、字段类型、事件顺序、重复终态或工具上限违规时返回脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn consume_event(
    event: AwsEventStreamEvent,
    state: &mut BedrockStreamState,
) -> Result<Vec<ProviderEvent>, AgentError> {
    if event.message_type == "exception" {
        return Err(AgentError::new(
            ErrorCode::ProviderUnavailable,
            "Bedrock stream returned an exception",
        ));
    }
    if state.metadata {
        return Err(protocol_error("Bedrock stream continued after metadata"));
    }
    match event.event_type.as_str() {
        "messageStart" => consume_message_start(&event.payload, state),
        "contentBlockStart" => consume_block_start(&event.payload, state),
        "contentBlockDelta" => consume_block_delta(&event.payload, state),
        "contentBlockStop" => consume_block_stop(&event.payload, state),
        "messageStop" => consume_message_stop(&event.payload, state),
        "metadata" => consume_metadata(&event.payload, state),
        _ => Ok(Vec::new()),
    }
}

/// 功能：验证唯一 messageStart 并产生公共消息起始事件。
///
/// 输入：Bedrock messageStart payload 与当前状态。
/// 输出：一个稳定 opaque provider message ID 的 MessageStart。
/// 不变量：role 必须为 assistant，且 start 只能出现一次并早于 stop。
/// 失败：字段或顺序违规时返回结构化协议错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn consume_message_start(
    payload: &Value,
    state: &mut BedrockStreamState,
) -> Result<Vec<ProviderEvent>, AgentError> {
    if state.started
        || state.stopped
        || payload.get("role").and_then(Value::as_str) != Some("assistant")
    {
        return Err(protocol_error("Bedrock messageStart is invalid"));
    }
    state.started = true;
    Ok(vec![ProviderEvent::MessageStart {
        provider_message_id: "bedrock:message".to_owned(),
    }])
}

/// 功能：验证 contentBlockStart 并冻结可选 toolUse 的 block index、ID 与 name。
///
/// 输入：Bedrock block start payload 与当前状态。
/// 输出：start 本身不发布公共事件。
/// 不变量：工具 identity 每个 index 只能设置一次；事件必须处于 start 与 stop 之间。
/// 失败：index、identity、重复或顺序违规时返回结构化协议错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn consume_block_start(
    payload: &Value,
    state: &mut BedrockStreamState,
) -> Result<Vec<ProviderEvent>, AgentError> {
    require_content_phase(state)?;
    let index = payload
        .get("contentBlockIndex")
        .and_then(Value::as_u64)
        .ok_or_else(|| protocol_error("Bedrock content block index is invalid"))?;
    let Some(tool) = payload.pointer("/start/toolUse") else {
        return Ok(Vec::new());
    };
    let call_id = tool
        .get("toolUseId")
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| protocol_error("Bedrock tool call ID is invalid"))?;
    let name = tool
        .get("name")
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| protocol_error("Bedrock tool name is invalid"))?;
    if state.tools.contains_key(&index) || state.tools.len() >= 128 {
        return Err(protocol_error(
            "Bedrock tool block is duplicated or excessive",
        ));
    }
    state.tools.insert(
        index,
        BedrockToolAccumulator {
            call_id: call_id.to_owned(),
            name: name.to_owned(),
            arguments: String::new(),
            stopped: false,
        },
    );
    Ok(Vec::new())
}

/// 功能：解析 contentBlockDelta 的文本、推理或 partial tool input。
///
/// 输入：Bedrock delta payload 与当前工具映射。
/// 输出：非空文本/推理立即产生 delta；工具参数仅有界累计。
/// 不变量：工具 input 只能进入已 start 且未 stop 的相同 index；累计 UTF-8 字节有上限。
/// 失败：字段类型、顺序、index 或资源上限违规时返回结构化错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn consume_block_delta(
    payload: &Value,
    state: &mut BedrockStreamState,
) -> Result<Vec<ProviderEvent>, AgentError> {
    require_content_phase(state)?;
    let index = payload
        .get("contentBlockIndex")
        .and_then(Value::as_u64)
        .ok_or_else(|| protocol_error("Bedrock content block index is invalid"))?;
    let delta = payload
        .get("delta")
        .and_then(Value::as_object)
        .ok_or_else(|| protocol_error("Bedrock content block delta is invalid"))?;
    if let Some(value) = delta.get("text") {
        let text = value
            .as_str()
            .ok_or_else(|| protocol_error("Bedrock text delta is invalid"))?;
        return Ok(if text.is_empty() {
            Vec::new()
        } else {
            vec![ProviderEvent::TextDelta {
                text: text.to_owned(),
            }]
        });
    }
    if let Some(value) = delta
        .get("reasoningContent")
        .and_then(Value::as_object)
        .and_then(|reasoning| reasoning.get("text"))
    {
        let text = value
            .as_str()
            .ok_or_else(|| protocol_error("Bedrock reasoning delta is invalid"))?;
        return Ok(if text.is_empty() {
            Vec::new()
        } else {
            vec![ProviderEvent::ReasoningDelta {
                text: text.to_owned(),
            }]
        });
    }
    let fragment = delta
        .get("toolUse")
        .and_then(Value::as_object)
        .and_then(|tool| tool.get("input"))
        .and_then(Value::as_str)
        .ok_or_else(|| protocol_error("Bedrock tool input delta is invalid"))?;
    let tool = state
        .tools
        .get_mut(&index)
        .filter(|tool| !tool.stopped)
        .ok_or_else(|| protocol_error("Bedrock tool input arrived outside its block"))?;
    if tool.arguments.len().saturating_add(fragment.len()) > MAX_TOOL_ARGUMENT_BYTES {
        return Err(AgentError::new(
            ErrorCode::OutputLimitExceeded,
            "Bedrock tool arguments exceeded their byte limit",
        ));
    }
    tool.arguments.push_str(fragment);
    Ok(Vec::new())
}

/// 功能：验证 contentBlockStop 并标记对应工具 block 已完成。
///
/// 输入：Bedrock block stop payload 与当前状态。
/// 输出：无公共事件。
/// 不变量：文本 block 可没有 start 累积器；工具 block 只能 stop 一次。
/// 失败：index、顺序或重复工具 stop 违规时返回结构化协议错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn consume_block_stop(
    payload: &Value,
    state: &mut BedrockStreamState,
) -> Result<Vec<ProviderEvent>, AgentError> {
    require_content_phase(state)?;
    let index = payload
        .get("contentBlockIndex")
        .and_then(Value::as_u64)
        .ok_or_else(|| protocol_error("Bedrock content block stop is invalid"))?;
    if let Some(tool) = state.tools.get_mut(&index) {
        if tool.stopped {
            return Err(protocol_error("Bedrock tool block stopped twice"));
        }
        tool.stopped = true;
    }
    Ok(Vec::new())
}

/// 功能：验证唯一 messageStop 并保存原生结束原因供 metadata 后统一提交。
///
/// 输入：Bedrock messageStop payload 与当前状态。
/// 输出：无公共事件，避免在 usage 前过早终结消息。
/// 不变量：stop 必须晚于 start，原因必须是非空字符串，metadata 尚未出现。
/// 失败：字段或顺序违规时返回结构化协议错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn consume_message_stop(
    payload: &Value,
    state: &mut BedrockStreamState,
) -> Result<Vec<ProviderEvent>, AgentError> {
    let reason = payload
        .get("stopReason")
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| protocol_error("Bedrock messageStop reason is invalid"))?;
    if !state.started || state.stopped || state.metadata {
        return Err(protocol_error("Bedrock messageStop order is invalid"));
    }
    state.stopped = true;
    state.stop_reason = Some(reason.to_owned());
    Ok(Vec::new())
}

/// 功能：验证终端 metadata，按 block 顺序提交完整工具、usage 与 MessageEnd。
///
/// 输入：Bedrock metadata payload 与已停止的消息状态。
/// 输出：工具 start/arguments/end、一个 Usage 和最终 MessageEnd。
/// 不变量：所有工具必须已 block stop；usage 三字段必须为非负整数；终态只发布一次。
/// 失败：metadata 顺序、usage、工具完整性或 stop reason 无效时返回结构化协议错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn consume_metadata(
    payload: &Value,
    state: &mut BedrockStreamState,
) -> Result<Vec<ProviderEvent>, AgentError> {
    if !state.started || !state.stopped || state.metadata {
        return Err(protocol_error("Bedrock metadata order is invalid"));
    }
    let usage = payload
        .get("usage")
        .and_then(Value::as_object)
        .ok_or_else(|| protocol_error("Bedrock usage is invalid"))?;
    let input_tokens = usage
        .get("inputTokens")
        .and_then(Value::as_u64)
        .ok_or_else(|| protocol_error("Bedrock input token usage is invalid"))?;
    let output_tokens = usage
        .get("outputTokens")
        .and_then(Value::as_u64)
        .ok_or_else(|| protocol_error("Bedrock output token usage is invalid"))?;
    let total_tokens = usage
        .get("totalTokens")
        .and_then(Value::as_u64)
        .ok_or_else(|| protocol_error("Bedrock total token usage is invalid"))?;
    let mut output = Vec::new();
    for tool in state.tools.values() {
        if !tool.stopped {
            return Err(protocol_error("Bedrock tool block is incomplete"));
        }
        output.push(ProviderEvent::ToolCallStart {
            call_id: tool.call_id.clone(),
            name: tool.name.clone(),
        });
        output.push(ProviderEvent::ToolCallArgumentsDelta {
            call_id: tool.call_id.clone(),
            delta: tool.arguments.clone(),
        });
        output.push(ProviderEvent::ToolCallEnd {
            call_id: tool.call_id.clone(),
        });
    }
    output.push(ProviderEvent::Usage(Usage {
        input_tokens,
        output_tokens,
        total_tokens,
        cached_input_tokens: 0,
        cost_micros: None,
    }));
    output.push(ProviderEvent::MessageEnd {
        finish_reason: bedrock_finish_reason(
            state
                .stop_reason
                .as_deref()
                .ok_or_else(|| protocol_error("Bedrock stop reason is missing"))?,
            !state.tools.is_empty(),
        ),
    });
    state.metadata = true;
    Ok(output)
}

/// 功能：要求 content block 事件严格处于 messageStart 与 messageStop 之间。
///
/// 输入：当前 Bedrock 状态。
/// 输出：处于内容阶段时成功。
/// 不变量：metadata 前后均不能重新进入内容阶段。
/// 失败：事件顺序违规时返回结构化协议错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn require_content_phase(state: &BedrockStreamState) -> Result<(), AgentError> {
    if state.started && !state.stopped && !state.metadata {
        Ok(())
    } else {
        Err(protocol_error(
            "Bedrock content block event order is invalid",
        ))
    }
}

/// 功能：在 HTTP EOF 后确认 Bedrock 原生消息已经完成 stop 与 metadata。
///
/// 输入：最终流状态。
/// 输出：完整消息成功。
/// 不变量：仅收到 messageStart、stop 但无 usage 或空响应都不能伪装为成功。
/// 失败：缺少原生终态时返回可重试 StreamInterrupted。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn finalize_stream(state: &BedrockStreamState) -> Result<(), AgentError> {
    if state.started && state.stopped && state.metadata {
        Ok(())
    } else {
        Err(AgentError::new(
            ErrorCode::StreamInterrupted,
            "Bedrock stream ended before terminal metadata",
        )
        .retryable(true))
    }
}

/// 功能：把 Bedrock stopReason 与工具状态映射为公共结束原因。
///
/// 输入：原生 stop reason 及是否存在工具调用。
/// 输出：工具优先，否则 end_turn/max_tokens/其他映射到 ToolCalls/Stop/Length/Error。
/// 不变量：未知字符串不会被文本推断为成功。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn bedrock_finish_reason(reason: &str, has_tools: bool) -> FinishReason {
    if has_tools || reason == "tool_use" {
        FinishReason::ToolCalls
    } else {
        match reason {
            "end_turn" | "stop_sequence" => FinishReason::Stop,
            "max_tokens" => FinishReason::Length,
            _ => FinishReason::Error,
        }
    }
}

/// 功能：创建不包含 payload、header、URL 或 credential 的 EventStream 协议错误。
///
/// 输入：由实现常量提供的稳定诊断文本。
/// 输出：StreamInterrupted 结构化错误。
/// 不变量：调用方不得传入 Provider 原始数据；详情只含公共错误分类。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn protocol_error(message: &'static str) -> AgentError {
    AgentError::new(ErrorCode::StreamInterrupted, message)
}

/// 功能：创建不回显云配置或 credential 的 Bedrock 配置错误。
///
/// 输入：由实现常量提供的稳定诊断文本。
/// 输出：ProviderUnavailable 结构化错误。
/// 不变量：消息和详情不得包含环境变量值、URL、模型或签名材料。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn configuration_error(message: &'static str) -> AgentError {
    AgentError::new(ErrorCode::ProviderUnavailable, message)
}

#[cfg(test)]
mod tests {
    use super::{
        AwsEventStreamDecoder, bedrock_request_body, bedrock_stream_path, canonical_uri, crc32,
        hmac_sha256,
    };
    use crate::domain::{Message, Role, ToolDefinition, ToolEffect};
    use crate::provider::ProviderRequest;
    use serde_json::json;

    /// 功能：构造测试专用 AWS string typed header 字节。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn test_header(name: &str, value: &str) -> Vec<u8> {
        let mut output = vec![u8::try_from(name.len()).unwrap_or(0)];
        output.extend_from_slice(name.as_bytes());
        output.push(7);
        output.extend_from_slice(&u16::try_from(value.len()).unwrap_or(0).to_be_bytes());
        output.extend_from_slice(value.as_bytes());
        output
    }

    /// 功能：构造带双 CRC 的测试 EventStream frame。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn test_frame(event_type: &str, payload: &[u8]) -> Vec<u8> {
        let mut headers = Vec::new();
        headers.extend(test_header(":message-type", "event"));
        headers.extend(test_header(":event-type", event_type));
        headers.extend(test_header(":content-type", "application/json"));
        let total = 16 + headers.len() + payload.len();
        let mut output = Vec::new();
        output.extend_from_slice(&u32::try_from(total).unwrap_or(0).to_be_bytes());
        output.extend_from_slice(&u32::try_from(headers.len()).unwrap_or(0).to_be_bytes());
        output.extend_from_slice(&crc32(&output[..8]).to_be_bytes());
        output.extend(headers);
        output.extend_from_slice(payload);
        let message_crc = crc32(&output);
        output.extend_from_slice(&message_crc.to_be_bytes());
        output
    }

    /// 功能：验证 EventStream decoder 可跨每个字节边界并校验双 CRC。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn decodes_fragmented_eventstream_with_both_crcs() -> Result<(), crate::error::AgentError> {
        let frame = test_frame("messageStart", br#"{"role":"assistant"}"#);
        let mut decoder = AwsEventStreamDecoder::new(1_048_576)?;
        let mut events = Vec::new();
        for byte in frame {
            events.extend(decoder.push(&[byte])?);
        }
        decoder.finish()?;
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].event_type, "messageStart");
        assert_eq!(events[0].payload["role"], "assistant");
        Ok(())
    }

    /// 功能：验证 HMAC-SHA256、CRC32 与 canonical URI 使用已知向量。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn matches_cryptographic_known_vectors() {
        assert_eq!(crc32(b"123456789"), 0xcbf4_3926);
        assert_eq!(
            super::hex_lower(&hmac_sha256(
                b"key",
                b"The quick brown fox jumps over the lazy dog"
            )),
            "f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8"
        );
        assert_eq!(
            canonical_uri("/model/vendor:model/converse-stream").as_deref(),
            Ok("/model/vendor%3Amodel/converse-stream")
        );
    }

    /// 功能：验证 Bedrock 请求将用户文本和工具 Schema 放入冻结原生位置。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn serializes_bedrock_converse_request() {
        let body = bedrock_request_body(&ProviderRequest {
            model: "mock-bedrock-v1".to_owned(),
            messages: vec![Message::text("user", Role::User, "hello")],
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
        assert_eq!(body["messages"][0]["content"][0]["text"], "hello");
        assert_eq!(
            body["toolConfig"]["tools"][0]["toolSpec"]["name"],
            "file.read"
        );
        assert_eq!(
            bedrock_stream_path("mock-bedrock-v1").as_deref(),
            Ok("/model/mock-bedrock-v1/converse-stream")
        );
    }
}
