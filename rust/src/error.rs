use serde::{Deserialize, Serialize};
use serde_json::Value;
use thiserror::Error;

/// 稳定且与语言无关的错误码；客户端不得解析错误消息文本来判断行为。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub enum ErrorCode {
    ParseError,
    InvalidRequest,
    NotInitialized,
    AlreadyInitialized,
    MethodNotFound,
    InvalidParams,
    ProtocolVersionUnsupported,
    SessionLocked,
    SessionNotFound,
    Conflict,
    RunNotFound,
    RunConflict,
    Cancelled,
    ProviderError,
    ProviderRateLimited,
    ProviderUnavailable,
    StreamInterrupted,
    ToolNotFound,
    ToolArgumentsInvalid,
    ApprovalRequired,
    PermissionDenied,
    PathOutsideWorkspace,
    Backpressure,
    OutputLimitExceeded,
    Timeout,
    JournalCorrupt,
    IoError,
    InternalError,
}

impl ErrorCode {
    /// 功能：将语言中立错误码映射为稳定的 JSON-RPC 数值错误码。
    ///
    /// 输入：当前错误码枚举值。
    /// 输出：规范定义的 JSON-RPC 整数错误码。
    /// 不变量：同一错误码跨进程、跨语言和跨版本补丁始终映射到同一数值。
    /// 失败：本方法不返回错误；所有枚举分支均有显式映射。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub const fn rpc_code(self) -> i64 {
        match self {
            Self::ParseError => -32700,
            Self::InvalidRequest => -32600,
            Self::NotInitialized | Self::AlreadyInitialized => -32600,
            Self::MethodNotFound => -32601,
            Self::InvalidParams | Self::ToolNotFound | Self::ToolArgumentsInvalid => -32602,
            Self::InternalError | Self::IoError => -32603,
            Self::ProtocolVersionUnsupported => -32001,
            Self::SessionLocked => -32002,
            Self::ApprovalRequired | Self::PermissionDenied | Self::PathOutsideWorkspace => -32003,
            Self::RunConflict => -32004,
            Self::ProviderError
            | Self::ProviderRateLimited
            | Self::ProviderUnavailable
            | Self::StreamInterrupted => -32005,
            Self::Timeout => -32006,
            Self::Cancelled => -32007,
            Self::JournalCorrupt => -32008,
            Self::OutputLimitExceeded => -32009,
            Self::SessionNotFound | Self::Conflict | Self::RunNotFound => -32010,
            Self::Backpressure => -32011,
        }
    }

    /// 功能：返回 portable error `details.kind` 使用的稳定 snake_case 分类。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// 该值属于公共协议，客户端可读取；不得包含实现语言或敏感文本。
    #[must_use]
    pub const fn detail_kind(self) -> &'static str {
        match self {
            Self::ParseError => "parse_error",
            Self::InvalidRequest => "invalid_request",
            Self::NotInitialized => "not_initialized",
            Self::AlreadyInitialized => "already_initialized",
            Self::MethodNotFound => "method_not_found",
            Self::InvalidParams => "invalid_params",
            Self::ProtocolVersionUnsupported => "protocol_version_unsupported",
            Self::SessionLocked => "session_locked",
            Self::SessionNotFound => "session_not_found",
            Self::Conflict => "conflict",
            Self::RunNotFound => "run_not_found",
            Self::RunConflict => "run_conflict",
            Self::Cancelled => "cancelled",
            Self::ProviderError => "provider_error",
            Self::ProviderRateLimited => "provider_rate_limited",
            Self::ProviderUnavailable => "provider_unavailable",
            Self::StreamInterrupted => "stream_interrupted",
            Self::ToolNotFound => "tool_not_found",
            Self::ToolArgumentsInvalid => "tool_arguments_invalid",
            Self::ApprovalRequired => "approval_required",
            Self::PermissionDenied => "permission_denied",
            Self::PathOutsideWorkspace => "path_outside_workspace",
            Self::Backpressure => "backpressure",
            Self::OutputLimitExceeded => "output_limit_exceeded",
            Self::Timeout => "timeout",
            Self::JournalCorrupt => "journal_corrupt",
            Self::IoError => "io_error",
            Self::InternalError => "internal_error",
        }
    }
}

/// 所有公共边界统一使用的结构化错误。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Error)]
#[error("{code:?}: {message}")]
pub struct AgentError {
    pub code: ErrorCode,
    pub message: String,
    pub retryable: bool,
    #[serde(default, skip_serializing_if = "Value::is_null")]
    pub details: Value,
}

impl AgentError {
    /// 功能：创建默认不可重试且不携带扩展详情的结构化错误。
    ///
    /// 输入：稳定错误码和供人阅读的错误消息。
    /// 输出：可在公共边界序列化的 `AgentError`。
    /// 不变量：客户端行为只能依赖错误码及结构化字段，不得解析消息文本。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn new(code: ErrorCode, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
            retryable: false,
            details: serde_json::json!({"kind":code.detail_kind()}),
        }
    }

    /// 功能：设置结构化错误是否允许调用方重试。
    ///
    /// 输入：目标重试标志。
    /// 输出：更新标志后的原错误值。
    /// 不变量：错误码、消息和详情保持不变。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub const fn retryable(mut self, value: bool) -> Self {
        self.retryable = value;
        self
    }

    /// 功能：为结构化错误附加语言中立的 JSON 详情。
    ///
    /// 输入：符合公共错误协议的 JSON 值。
    /// 输出：更新详情后的原错误值。
    /// 不变量：错误码、消息和重试标志保持不变。
    /// 失败：本方法不返回错误；调用方负责避免在详情中放入密钥等敏感信息。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn with_details(mut self, details: Value) -> Self {
        self.details = details;
        self
    }
}

impl From<std::io::Error> for AgentError {
    /// 功能：将标准 I/O 错误转换为统一的不可重试 `IoError`。
    ///
    /// 输入：标准库 I/O 错误。
    /// 输出：错误码为 `IoError` 的公共结构化错误。
    /// 不变量：只保留供人阅读的错误文本，不暴露语言专属错误对象。
    /// 失败：转换本身不失败；调用方必须避免底层错误文本包含敏感路径或密钥。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn from(value: std::io::Error) -> Self {
        Self::new(ErrorCode::IoError, value.to_string())
    }
}

impl From<serde_json::Error> for AgentError {
    /// 功能：将 JSON 编解码错误转换为统一的 `InvalidRequest` 错误。
    ///
    /// 输入：serde JSON 编解码错误。
    /// 输出：错误码为 `InvalidRequest` 的公共结构化错误。
    /// 不变量：公共 DTO 不暴露 Rust 专属错误类型。
    /// 失败：转换本身不失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn from(value: serde_json::Error) -> Self {
        Self::new(ErrorCode::InvalidRequest, value.to_string())
    }
}
