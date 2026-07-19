use std::time::Duration;

use chrono::{DateTime, Utc};
use reqwest::Url;
use reqwest::header::{HeaderValue, RETRY_AFTER};
use reqwest::redirect::Policy;
use serde_json::json;
use tokio_util::sync::CancellationToken;

use crate::error::{AgentError, ErrorCode};

const DEFAULT_CONNECT_TIMEOUT: Duration = Duration::from_secs(10);
const DEFAULT_RESPONSE_HEADERS_TIMEOUT: Duration = Duration::from_secs(60);
const DEFAULT_STREAM_IDLE_TIMEOUT: Duration = Duration::from_secs(60);
const DEFAULT_TOTAL_TIMEOUT: Duration = Duration::from_secs(300);
const MAX_RETRY_AFTER_MS: u64 = 24 * 60 * 60 * 1_000;

/// 已解码的 SSE 事件；多个 `data:` 字段按规范用换行符连接。
///
/// 不变量：事件在空行或 EOF 前完整字段行结束时提交，缺省事件类型为 `message`，ID 遵循流级延续语义。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SseEvent {
    /// 服务器声明的事件类型；规范缺省值以 `Some("message")` 表示。
    pub event: Option<String>,
    /// 按接收顺序用换行连接后的所有 `data:` 字段。
    pub data: String,
    /// 当前事件生效的 Last-Event-ID，可能来自较早事件块。
    pub id: Option<String>,
}

/// 增量 SSE 解码器，可处理任意字节边界和 UTF-8 多字节字符分片。
///
/// 不变量：只缓冲一个尚未提交的有界事件，不暴露原始 Provider 字节。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Default)]
pub struct SseDecoder {
    buffer: Vec<u8>,
    max_event_bytes: usize,
    last_event_id: Option<String>,
}

impl SseDecoder {
    /// 功能：创建具有单事件缓冲字节上限的增量 SSE 解码器。
    ///
    /// 输入：一个尚未完成事件允许占用的最大字节数。
    /// 输出：空缓冲区解码器。
    /// 不变量：后续单个事件超过该上限时立即失败，避免无界内存增长。
    /// 失败：本方法不返回错误；零上限会使任何非空事件失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn new(max_event_bytes: usize) -> Self {
        Self {
            buffer: Vec::new(),
            max_event_bytes,
            last_event_id: None,
        }
    }

    /// 功能：接收任意边界字节分片并解码其中所有完整 SSE 事件。
    ///
    /// 输入：下一段原始响应字节，可切在 UTF-8 字符、CRLF 或 JSON token 中间。
    /// 输出：本次由空行提交的事件列表，未完整数据继续留在缓冲区。
    /// 不变量：事件顺序和 Last-Event-ID 语义保持不变；多行 `data` 使用换行连接。
    /// 失败：单事件超限、完整事件 UTF-8 非法或解析失败时返回结构化流错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn push(&mut self, bytes: &[u8]) -> Result<Vec<SseEvent>, AgentError> {
        self.buffer.extend_from_slice(bytes);

        let mut events = Vec::new();
        while let Some((event_end, consumed)) = find_event_end(&self.buffer) {
            if event_end > self.max_event_bytes {
                return Err(event_limit_error(self.max_event_bytes, event_end));
            }
            let event_bytes = self.buffer[..event_end].to_vec();
            self.buffer.drain(..consumed);
            let (event, id_update) = parse_event(&event_bytes)?;
            if let Some(id) = id_update {
                self.last_event_id = Some(id);
            }
            if let Some(mut event) = event {
                event.id.clone_from(&self.last_event_id);
                events.push(event);
            }
        }

        if self.buffer.len() > self.max_event_bytes {
            return Err(event_limit_error(self.max_event_bytes, self.buffer.len()));
        }
        Ok(events)
    }

    /// 功能：在底层流结束时提交完整尾事件，并丢弃未结束字段行的残片。
    ///
    /// 输入：当前解码器中尚未遇到空行分隔符的尾部数据。
    /// 输出：尾部以 LF/CR 完成字段行时至多一个事件，否则为空列表。
    /// 不变量：调用后缓冲区为空；缺少字段行终止符的断流残片不会伪装成完整事件。
    /// 失败：尾部超过限制、UTF-8 非法或完整尾事件解析失败时返回结构化流错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn finish(&mut self) -> Result<Vec<SseEvent>, AgentError> {
        let remaining = std::mem::take(&mut self.buffer);
        if remaining.len() > self.max_event_bytes {
            return Err(event_limit_error(self.max_event_bytes, remaining.len()));
        }
        std::str::from_utf8(&remaining).map_err(|_| {
            AgentError::new(
                ErrorCode::StreamInterrupted,
                "provider sent invalid UTF-8 SSE data",
            )
        })?;
        if remaining.is_empty() || !(remaining.ends_with(b"\n") || remaining.ends_with(b"\r")) {
            return Ok(Vec::new());
        }
        let (event, id_update) = parse_event(&remaining)?;
        if let Some(id) = id_update {
            self.last_event_id = Some(id);
        }
        let Some(mut event) = event else {
            return Ok(Vec::new());
        };
        event.id.clone_from(&self.last_event_id);
        Ok(vec![event])
    }
}

/// 功能：构造禁用环境代理与自动重定向并启用连接超时的 Provider HTTP 客户端。
///
/// 输入：无，使用规范固定的安全传输默认值。
/// 输出：不会把认证头自动转发到重定向目标的原生 reqwest 客户端。
/// 不变量：TLS 校验保持启用；认证请求不会隐式经过环境代理；任何 3xx 均返回 family adapter 处理。
/// 失败：本机无法初始化 HTTP/TLS 客户端时返回不含底层敏感文本的错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(super) fn build_http_client() -> Result<reqwest::Client, AgentError> {
    reqwest::Client::builder()
        .redirect(Policy::none())
        .no_proxy()
        .connect_timeout(configured_duration(
            "QXNM_FORGE_PROVIDER_CONNECT_TIMEOUT_MS",
            DEFAULT_CONNECT_TIMEOUT,
        ))
        .timeout(configured_duration(
            "QXNM_FORGE_PROVIDER_TOTAL_TIMEOUT_MS",
            DEFAULT_TOTAL_TIMEOUT,
        ))
        .build()
        .map_err(|_| {
            AgentError::new(
                ErrorCode::ProviderUnavailable,
                "provider HTTP client initialization failed",
            )
        })
}

/// 功能：校验 Provider base URL 并安全追加 family 固定的原生路径与查询参数。
///
/// 输入：显式或清单提供的 base URL、以 `/` 开头的固定路径后缀及固定查询键值。
/// 输出：生产仅 HTTPS、conformance 仅字面 127.0.0.1 HTTP 的完整请求 URL。
/// 不变量：拒绝 userinfo、原始 query/fragment、非固定 HTTP origin 和会改变 authority 的拼接；只做一次字面追加。
/// 失败：URL、scheme、origin 或后缀不符合边界时返回不含原始 URL 的结构化 Provider 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) fn native_endpoint(
    base: &str,
    path_suffix: &str,
    query: &[(&str, &str)],
) -> Result<Url, AgentError> {
    if !path_suffix.starts_with('/')
        || path_suffix.contains(['?', '#', '\\'])
        || path_suffix.chars().any(char::is_control)
    {
        return Err(invalid_endpoint_error());
    }
    let mut url = Url::parse(base).map_err(|_| invalid_endpoint_error())?;
    if !url.username().is_empty()
        || url.password().is_some()
        || url.query().is_some()
        || url.fragment().is_some()
    {
        return Err(invalid_endpoint_error());
    }
    let conformance_loopback = std::env::var("QXNM_FORGE_PROVIDER_CONFORMANCE")
        .ok()
        .is_some_and(|value| value == "1")
        && url.scheme() == "http"
        && url.host_str() == Some("127.0.0.1");
    if url.scheme() != "https" && !conformance_loopback {
        return Err(invalid_endpoint_error());
    }
    let origin = (
        url.scheme().to_owned(),
        url.host_str().map(str::to_owned),
        url.port_or_known_default(),
    );
    let base_path = url.path().trim_end_matches('/');
    let joined = format!("{base_path}{path_suffix}");
    url.set_path(&joined);
    if !query.is_empty() {
        let mut pairs = url.query_pairs_mut();
        pairs.clear();
        pairs.extend_pairs(query.iter().copied());
    }
    if origin
        != (
            url.scheme().to_owned(),
            url.host_str().map(str::to_owned),
            url.port_or_known_default(),
        )
    {
        return Err(invalid_endpoint_error());
    }
    Ok(url)
}

/// 功能：创建不会回显 URL、主机或配置值的非法 Provider endpoint 错误。
///
/// 输入：无。
/// 输出：不可重试且带稳定 `invalid_provider_endpoint` 分类的错误。
/// 不变量：消息与详情不含 base URL、查询、凭据或解析器底层错误。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn invalid_endpoint_error() -> AgentError {
    AgentError::new(
        ErrorCode::ProviderUnavailable,
        "provider endpoint configuration is invalid",
    )
    .with_details(json!({"kind":"invalid_provider_endpoint"}))
}

/// 功能：可取消且有响应头等待上限地发送一次 Provider HTTP 请求。
///
/// 输入：已在最后边界注入认证信息的请求构造器，以及运行级取消令牌。
/// 输出：未读取正文的 HTTP 响应，供调用方检查状态并增量消费。
/// 不变量：错误消息不包含 URL、认证头或 reqwest 原始错误文本。
/// 失败：本地取消、响应头超时或网络建立失败返回稳定结构化错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(super) async fn send_provider_request(
    request: reqwest::RequestBuilder,
    cancellation: &CancellationToken,
) -> Result<reqwest::Response, AgentError> {
    tokio::select! {
        biased;
        () = cancellation.cancelled() => Err(cancelled_error()),
        result = tokio::time::timeout(configured_duration(
            "QXNM_FORGE_PROVIDER_TOTAL_TIMEOUT_MS",
            DEFAULT_RESPONSE_HEADERS_TIMEOUT,
        ), request.send()) => {
            match result {
                Ok(Ok(response)) => Ok(response),
                Ok(Err(error)) => Err(request_transport_error(&error)),
                Err(_) => Err(timeout_error("provider_response_headers")),
            }
        }
    }
}

/// 功能：在取消或流空闲上限约束下读取下一段 Provider 响应正文。
///
/// 输入：成功 HTTP 响应的可变引用和运行级取消令牌。
/// 输出：下一段已解压正文的字节副本，正常 EOF 返回 `None`。
/// 不变量：每次成功收到字节后重新开始 idle timeout；原始网络错误文本不外泄。
/// 失败：取消、idle timeout 或响应体传输中断返回稳定结构化错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(super) async fn next_response_chunk(
    response: &mut reqwest::Response,
    cancellation: &CancellationToken,
) -> Result<Option<Vec<u8>>, AgentError> {
    next_response_chunk_with_timeout(
        response,
        cancellation,
        configured_duration(
            "QXNM_FORGE_PROVIDER_IDLE_TIMEOUT_MS",
            DEFAULT_STREAM_IDLE_TIMEOUT,
        ),
    )
    .await
}

/// 功能：读取 Provider conformance 毫秒环境变量并安全退回生产默认时限。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
///
/// 仅接受正十进制毫秒，零、非法值和溢出值不会关闭安全时限。
fn configured_duration(name: &str, default: Duration) -> Duration {
    std::env::var(name)
        .ok()
        .and_then(|value| value.parse::<u64>().ok())
        .filter(|value| *value > 0)
        .map(Duration::from_millis)
        .unwrap_or(default)
}

/// 功能：将非成功 Provider HTTP 状态转换为有界且脱敏的结构化错误。
///
/// 输入：尚未读取原始正文的 HTTP 响应。
/// 输出：含 `httpStatus` 及可选有界 `retryAfterMs` 的公共错误。
/// 不变量：响应正文、响应头原文和凭据不进入消息或详情；详情符合公共错误 Schema。
/// 失败：本函数自身不失败；429、408 与可重试 5xx 按规范设置重试标志。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(super) fn provider_status_error(response: &reqwest::Response) -> AgentError {
    let status = response.status();
    let code = if status.as_u16() == 429 {
        ErrorCode::ProviderRateLimited
    } else if status.is_server_error() || status.as_u16() == 408 {
        ErrorCode::ProviderUnavailable
    } else {
        ErrorCode::ProviderError
    };
    let retryable = matches!(status.as_u16(), 408 | 429 | 500 | 502 | 503 | 504);
    let mut details = json!({
        "kind": code.detail_kind(),
        "httpStatus": status.as_u16()
    });
    if let Some(retry_after_ms) = response
        .headers()
        .get(RETRY_AFTER)
        .and_then(|value| retry_after_ms(value, Utc::now()))
    {
        details["retryAfterMs"] = json!(retry_after_ms);
    }
    AgentError::new(
        code,
        format!(
            "provider HTTP request failed with status {}",
            status.as_u16()
        ),
    )
    .retryable(retryable)
    .with_details(details)
}

/// 功能：识别原始字节中由 LF、CRLF、CR 或其组合表示的首个 SSE 空行边界。
///
/// 输入：可能包含多个或半个事件的字节缓冲区。
/// 输出：事件内容结束偏移与应从缓冲区消费的总字节数。
/// 不变量：位于分片末尾的非空行 CR 会等待下一分片，以免把 CRLF 误判为两个换行。
/// 失败：本函数不返回错误；尚无完整空行时返回 `None`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn find_event_end(bytes: &[u8]) -> Option<(usize, usize)> {
    let mut line_start = 0;
    let mut index = 0;
    while index < bytes.len() {
        match bytes[index] {
            b'\n' => {
                if index == line_start {
                    return Some((line_start, index + 1));
                }
                line_start = index + 1;
                index += 1;
            }
            b'\r' => {
                if index == line_start {
                    let newline_len = usize::from(bytes.get(index + 1) == Some(&b'\n')) + 1;
                    return Some((line_start, index + newline_len));
                }
                if index + 1 == bytes.len() {
                    return None;
                }
                let newline_len = usize::from(bytes[index + 1] == b'\n') + 1;
                line_start = index + newline_len;
                index += newline_len;
            }
            _ => index += 1,
        }
    }
    None
}

/// 功能：把一个已由空行提交的 SSE 字节块解析为事件及 Last-Event-ID 更新。
///
/// 输入：不包含提交空行、但可包含普通行终止符的单事件字节。
/// 输出：仅在至少有一个 `data` 字段时返回事件，并单独返回合法 `id` 更新。
/// 不变量：缺省事件类型为 `message`；注释忽略；含 NUL 的 ID 不改变既有 ID。
/// 失败：事件不是合法 UTF-8 时返回不包含原始字节的流中断错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_event(bytes: &[u8]) -> Result<(Option<SseEvent>, Option<String>), AgentError> {
    let text = std::str::from_utf8(bytes).map_err(|_| {
        AgentError::new(
            ErrorCode::StreamInterrupted,
            "provider sent invalid UTF-8 SSE data",
        )
    })?;
    let normalized = text.replace("\r\n", "\n").replace('\r', "\n");
    let mut event = None;
    let mut id_update = None;
    let mut data = Vec::new();
    for line in normalized.lines() {
        if line.starts_with(':') || line.is_empty() {
            continue;
        }
        let (field, value) = line.split_once(':').unwrap_or((line, ""));
        let value = value.strip_prefix(' ').unwrap_or(value);
        match field {
            "event" => event = Some(value.to_owned()),
            "data" => data.push(value.to_owned()),
            "id" if !value.contains('\0') => id_update = Some(value.to_owned()),
            _ => {}
        }
    }
    if data.is_empty() {
        return Ok((None, id_update));
    }
    Ok((
        Some(SseEvent {
            event: Some(
                event
                    .filter(|value| !value.is_empty())
                    .unwrap_or_else(|| "message".to_owned()),
            ),
            data: data.join("\n"),
            id: None,
        }),
        id_update,
    ))
}

/// 功能：使用指定测试或生产 idle 上限读取下一段响应正文。
///
/// 输入：响应、取消令牌及本次等待允许的最长空闲时间。
/// 输出：下一段正文副本或正常 EOF。
/// 不变量：取消分支具有优先级；超时不会继续后台读取同一响应。
/// 失败：取消、超时或响应体中断返回不含 URL、正文和底层错误文本的错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn next_response_chunk_with_timeout(
    response: &mut reqwest::Response,
    cancellation: &CancellationToken,
    idle_timeout: Duration,
) -> Result<Option<Vec<u8>>, AgentError> {
    tokio::select! {
        biased;
        () = cancellation.cancelled() => Err(cancelled_error()),
        result = tokio::time::timeout(idle_timeout, response.chunk()) => {
            match result {
                Ok(Ok(chunk)) => Ok(chunk.map(|bytes| bytes.to_vec())),
                Ok(Err(error)) => Err(stream_transport_error(&error)),
                Err(_) => Err(timeout_error("provider_stream_idle")),
            }
        }
    }
}

/// 功能：将 Retry-After 秒数或 HTTP-date 转换为有界毫秒数。
///
/// 输入：HTTP 头值和用于解析绝对日期的 UTC 当前时间。
/// 输出：有效提示对应的 `0..=24h` 毫秒数，无效头返回 `None`。
/// 不变量：任何超大秒数或远期日期均截断，不产生整数溢出或无界等待。
/// 失败：非 UTF-8、非法整数和非法 HTTP-date 均作为无提示处理。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn retry_after_ms(value: &HeaderValue, now: DateTime<Utc>) -> Option<u64> {
    let text = value.to_str().ok()?.trim();
    if let Ok(seconds) = text.parse::<u64>() {
        return Some(seconds.saturating_mul(1_000).min(MAX_RETRY_AFTER_MS));
    }
    let retry_at = DateTime::parse_from_rfc2822(text).ok()?.with_timezone(&Utc);
    let millis = retry_at
        .signed_duration_since(now)
        .num_milliseconds()
        .max(0);
    Some(u64::try_from(millis).ok()?.min(MAX_RETRY_AFTER_MS))
}

/// 功能：创建 SSE 单事件超限的结构化资源错误。
///
/// 输入：配置上限和当前观察到的字节数。
/// 输出：符合公共错误 Schema 且不含原始事件内容的错误。
/// 不变量：详情仅使用 `limit` 与 `observed` 标准字段。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn event_limit_error(limit: usize, observed: usize) -> AgentError {
    AgentError::new(
        ErrorCode::OutputLimitExceeded,
        "SSE event exceeded configured byte limit",
    )
    .with_details(json!({
        "kind": ErrorCode::OutputLimitExceeded.detail_kind(),
        "limit": limit,
        "observed": observed
    }))
}

/// 功能：把请求建立阶段的 reqwest 错误安全映射为公共错误。
///
/// 输入：仅用于分类、绝不直接格式化输出的 reqwest 错误引用。
/// 输出：超时或 Provider 不可用错误。
/// 不变量：消息和详情不含 endpoint、查询参数、认证头或底层错误链。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn request_transport_error(error: &reqwest::Error) -> AgentError {
    if error.is_timeout() {
        timeout_error("provider_request")
    } else {
        AgentError::new(
            ErrorCode::ProviderUnavailable,
            "provider network request failed",
        )
        .retryable(true)
    }
}

/// 功能：把响应体读取阶段的 reqwest 错误安全映射为流中断错误。
///
/// 输入：仅用于分类、绝不直接格式化输出的 reqwest 错误引用。
/// 输出：超时或可重试流中断错误。
/// 不变量：消息和详情不含 endpoint、响应正文或底层错误链。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn stream_transport_error(error: &reqwest::Error) -> AgentError {
    if error.is_timeout() {
        timeout_error("provider_stream")
    } else {
        AgentError::new(
            ErrorCode::StreamInterrupted,
            "provider response stream was interrupted",
        )
        .retryable(true)
    }
}

/// 功能：创建 Provider 本地取消使用的统一结构化错误。
///
/// 输入：无。
/// 输出：不可重试的 `Cancelled` 错误。
/// 不变量：消息和详情不含 Provider 数据或凭据。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn cancelled_error() -> AgentError {
    AgentError::new(ErrorCode::Cancelled, "provider request cancelled")
}

/// 功能：创建 Provider 请求或流等待超时的统一结构化错误。
///
/// 输入：公共错误 Schema 允许的稳定操作名。
/// 输出：可重试的 `Timeout` 错误及 `operation` 详情。
/// 不变量：操作名由实现常量提供，不接收 Provider 或用户原始文本。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn timeout_error(operation: &'static str) -> AgentError {
    AgentError::new(ErrorCode::Timeout, "provider request timed out")
        .retryable(true)
        .with_details(json!({
            "kind": ErrorCode::Timeout.detail_kind(),
            "operation": operation
        }))
}

#[cfg(test)]
mod tests {
    use std::error::Error;
    use std::io::{self, Read, Write};
    use std::net::TcpListener;
    use std::thread::{self, JoinHandle};

    use chrono::{TimeZone, Utc};
    use reqwest::header::HeaderValue;
    use tokio_util::sync::CancellationToken;

    use super::{
        SseDecoder, build_http_client, native_endpoint, next_response_chunk_with_timeout,
        provider_status_error, retry_after_ms, send_provider_request,
    };
    use crate::error::ErrorCode;

    /// 功能：启动只服务一次的本地 HTTP mock，并在写完响应后按需保持连接。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn spawn_mock_server(
        response: Vec<u8>,
        hold_open: std::time::Duration,
    ) -> io::Result<(String, JoinHandle<io::Result<()>>)> {
        let listener = TcpListener::bind(("127.0.0.1", 0))?;
        let address = listener.local_addr()?;
        let handle = thread::spawn(move || {
            let (mut stream, _) = listener.accept()?;
            let mut request = [0_u8; 2_048];
            let _ = stream.read(&mut request)?;
            stream.write_all(&response)?;
            stream.flush()?;
            thread::sleep(hold_open);
            Ok(())
        });
        Ok((format!("http://{address}/mock"), handle))
    }

    /// 功能：等待本地 HTTP mock 线程并把 panic 转换为测试错误。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn join_mock_server(handle: JoinHandle<io::Result<()>>) -> io::Result<()> {
        handle
            .join()
            .map_err(|_| io::Error::other("mock HTTP server panicked"))?
    }

    /// 功能：验证 CRLF、多行 data 和 UTF-8 多字节字符任意分片均可正确解码。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn handles_crlf_multiline_and_utf8_splits() -> Result<(), crate::error::AgentError> {
        let source = "event: delta\r\ndata: 你\r\ndata: 好\r\n\r\n".as_bytes();
        let mut decoder = SseDecoder::new(1024);
        let mut events = Vec::new();
        for byte in source {
            events.extend(decoder.push(std::slice::from_ref(byte))?);
        }
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].event.as_deref(), Some("delta"));
        assert_eq!(events[0].data, "你\n好");
        Ok(())
    }

    /// 功能：验证 LF 空行边界、注释忽略以及缺省 `message` 事件类型。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn handles_lf_boundaries_comments_and_default_event() -> Result<(), crate::error::AgentError> {
        let mut decoder = SseDecoder::new(1024);
        let events = decoder.push(b": ping\ndata: one\n\ndata: two\n\n")?;
        assert_eq!(events.len(), 2);
        assert_eq!(events[0].event.as_deref(), Some("message"));
        assert_eq!(events[0].data, "one");
        assert_eq!(events[1].data, "two");
        Ok(())
    }

    /// 功能：验证裸 CR 分隔及 Last-Event-ID 会延续到后续未显式带 ID 的事件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn handles_bare_cr_and_persistent_event_id() -> Result<(), crate::error::AgentError> {
        let mut decoder = SseDecoder::new(1024);
        let events = decoder.push(b"id: 42\rdata: one\r\rdata: two\r\r")?;
        assert_eq!(events.len(), 2);
        assert_eq!(events[0].id.as_deref(), Some("42"));
        assert_eq!(events[1].id.as_deref(), Some("42"));
        assert_eq!(events[1].data, "two");
        Ok(())
    }

    /// 功能：验证 EOF 时未由空行提交的 SSE 残片不会被当作完整事件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn discards_uncommitted_event_at_eof() -> Result<(), crate::error::AgentError> {
        let mut decoder = SseDecoder::new(1024);
        assert!(decoder.push(b"data: partial")?.is_empty());
        assert!(decoder.finish()?.is_empty());
        Ok(())
    }

    /// 功能：验证 EOF 前字段行已结束但没有额外空行时仍提交完整 SSE 事件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn commits_complete_event_at_eof_without_blank_line() -> Result<(), crate::error::AgentError> {
        let mut decoder = SseDecoder::new(1024);
        assert!(
            decoder
                .push(b"event: response.completed\r\ndata: {\"type\":\"response.completed\"}\r\n")?
                .is_empty()
        );
        let events = decoder.finish()?;
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].event.as_deref(), Some("response.completed"));
        assert_eq!(events[0].data, "{\"type\":\"response.completed\"}");
        Ok(())
    }

    /// 功能：验证单次大分片可包含多个各自未超限的 SSE 事件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn applies_limit_per_event_not_per_chunk() -> Result<(), crate::error::AgentError> {
        let mut decoder = SseDecoder::new(12);
        let events = decoder.push(b"data: one\n\ndata: two\n\n")?;
        assert_eq!(events.len(), 2);
        Ok(())
    }

    /// 功能：验证 Retry-After 秒数和 HTTP-date 均转换为有界毫秒数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn parses_and_bounds_retry_after() -> Result<(), Box<dyn Error>> {
        let now = Utc
            .with_ymd_and_hms(2026, 7, 10, 0, 0, 0)
            .single()
            .ok_or_else(|| io::Error::other("invalid test time"))?;
        assert_eq!(
            retry_after_ms(&HeaderValue::from_static("2"), now),
            Some(2_000)
        );
        assert_eq!(
            retry_after_ms(
                &HeaderValue::from_static("Fri, 10 Jul 2026 00:00:03 GMT"),
                now
            ),
            Some(3_000)
        );
        assert_eq!(
            retry_after_ms(&HeaderValue::from_static("999999999999"), now),
            Some(86_400_000)
        );
        Ok(())
    }

    /// 功能：验证原生 endpoint 只在 HTTPS base 上追加固定 path/query 并拒绝 userinfo。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn validates_native_endpoint_origin_and_target() -> Result<(), crate::error::AgentError> {
        let endpoint = native_endpoint(
            "https://example.test/v1beta",
            "/models/mock:streamGenerateContent",
            &[("alt", "sse")],
        )?;
        assert_eq!(
            endpoint.as_str(),
            "https://example.test/v1beta/models/mock:streamGenerateContent?alt=sse"
        );
        assert!(native_endpoint("http://example.test", "/responses", &[]).is_err());
        assert!(native_endpoint("https://user@example.test", "/responses", &[]).is_err());
        Ok(())
    }

    /// 功能：通过本地 HTTP mock 验证 429 详情结构化且原始正文不会进入错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn redacts_status_body_and_exposes_retry_hint() -> Result<(), Box<dyn Error>> {
        let raw_body = "qxnm-forge-secret-canary-provider-body";
        let response = format!(
            "HTTP/1.1 429 Too Many Requests\r\nRetry-After: 2\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{raw_body}",
            raw_body.len()
        )
        .into_bytes();
        let (url, server) = spawn_mock_server(response, std::time::Duration::ZERO)?;
        let client = build_http_client()?;
        let response = client.get(url).send().await?;
        let error = provider_status_error(&response);
        assert_eq!(error.code, ErrorCode::ProviderRateLimited);
        assert!(error.retryable);
        assert_eq!(error.details["httpStatus"], 429);
        assert_eq!(error.details["retryAfterMs"], 2_000);
        assert!(!error.message.contains(raw_body));
        assert!(!error.details.to_string().contains(raw_body));
        drop(response);
        join_mock_server(server)?;
        Ok(())
    }

    /// 功能：通过本地 HTTP mock 验证 503 及 Retry-After 也使用结构化可重试详情。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn classifies_retryable_server_status() -> Result<(), Box<dyn Error>> {
        let response = b"HTTP/1.1 503 Service Unavailable\r\nRetry-After: 3\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".to_vec();
        let (url, server) = spawn_mock_server(response, std::time::Duration::ZERO)?;
        let client = build_http_client()?;
        let response = client.get(url).send().await?;
        let error = provider_status_error(&response);
        assert_eq!(error.code, ErrorCode::ProviderUnavailable);
        assert!(error.retryable);
        assert_eq!(error.details["httpStatus"], 503);
        assert_eq!(error.details["retryAfterMs"], 3_000);
        drop(response);
        join_mock_server(server)?;
        Ok(())
    }

    /// 功能：通过本地 302 mock 验证客户端不会自动向其他 origin 转发认证请求。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn refuses_automatic_redirects_with_credentials() -> Result<(), Box<dyn Error>> {
        let response = b"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/credential-target\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".to_vec();
        let (url, server) = spawn_mock_server(response, std::time::Duration::ZERO)?;
        let client = build_http_client()?;
        let token = CancellationToken::new();
        let response = send_provider_request(
            client
                .get(url)
                .header("authorization", "Bearer qxnm-forge-secret-canary-redirect"),
            &token,
        )
        .await?;
        assert_eq!(response.status().as_u16(), 302);
        drop(response);
        join_mock_server(server)?;
        Ok(())
    }

    /// 功能：通过本地停滞响应验证正文读取 idle timeout 会中止等待并返回可重试超时。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn enforces_stream_idle_timeout() -> Result<(), Box<dyn Error>> {
        let response =
            b"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"
                .to_vec();
        let (url, server) = spawn_mock_server(response, std::time::Duration::from_millis(80))?;
        let client = build_http_client()?;
        let mut response = client.get(url).send().await?;
        let token = CancellationToken::new();
        let result = next_response_chunk_with_timeout(
            &mut response,
            &token,
            std::time::Duration::from_millis(10),
        )
        .await;
        let Err(error) = result else {
            return Err(io::Error::other("idle response unexpectedly produced data").into());
        };
        assert_eq!(error.code, ErrorCode::Timeout);
        assert!(error.retryable);
        assert_eq!(error.details["operation"], "provider_stream_idle");
        drop(response);
        join_mock_server(server)?;
        Ok(())
    }

    /// 功能：验证预先取消的令牌会在建立任何 Provider 连接前优先终止请求。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn cancels_before_sending_request() -> Result<(), Box<dyn Error>> {
        let client = build_http_client()?;
        let token = CancellationToken::new();
        token.cancel();
        let result =
            send_provider_request(client.get("http://127.0.0.1:1/never-send"), &token).await;
        let Err(error) = result else {
            return Err(io::Error::other("cancelled request unexpectedly succeeded").into());
        };
        assert_eq!(error.code, ErrorCode::Cancelled);
        Ok(())
    }
}
