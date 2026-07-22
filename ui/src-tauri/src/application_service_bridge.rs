use std::collections::{BTreeMap, HashMap};
use std::ffi::OsString;
use std::path::{Path, PathBuf};
use std::process::Stdio;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::time::Duration;

use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use tauri::{AppHandle, Emitter as _, Manager as _};
use tokio::io::{AsyncBufRead, AsyncBufReadExt as _, AsyncWriteExt as _, BufReader};
use tokio::process::{Child, ChildStdin, Command};
use tokio::sync::{Mutex, RwLock, oneshot};

const MAX_FRAME_BYTES: usize = 1_048_576;
const MAX_EVENT_BYTES: usize = 262_144;
const REQUEST_TIMEOUT: Duration = Duration::from_secs(30);

type ResponseSender = oneshot::Sender<Result<Value, BridgeError>>;
type PendingResponses = Mutex<HashMap<String, ResponseSender>>;
type SharedPendingResponses = Arc<PendingResponses>;

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Deserialize, Serialize)]
#[serde(rename_all = "lowercase")]
pub(crate) enum BackendKind {
    Rust,
    Dotnet,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct BridgeError {
    #[serde(skip_serializing_if = "Option::is_none")]
    code: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    kind: Option<String>,
    message: String,
}

impl BridgeError {
    /// 创建不携带宿主路径、进程参数或 secret 的 bridge 错误。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn internal(message: impl Into<String>) -> Self {
        Self {
            code: None,
            kind: Some("bridge_error".to_owned()),
            message: message.into(),
        }
    }

    /// 从 daemon 的 portable error 投影前端可消费的稳定字段。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn from_rpc(error: &Value) -> Self {
        let message = error
            .get("message")
            .and_then(Value::as_str)
            .filter(|value| value.len() <= 512)
            .unwrap_or("application service request failed")
            .to_owned();
        let kind = error
            .pointer("/details/kind")
            .and_then(Value::as_str)
            .filter(|value| value.len() <= 128)
            .map(str::to_owned);
        Self {
            code: error.get("code").and_then(Value::as_i64),
            kind,
            message,
        }
    }
}

pub(crate) struct ApplicationServiceBridge {
    connections: Mutex<BTreeMap<BackendKind, Arc<DaemonConnection>>>,
    state_lifecycle: RwLock<()>,
}

impl Default for ApplicationServiceBridge {
    /// 创建尚未启动任何 daemon 的 host bridge。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn default() -> Self {
        Self {
            connections: Mutex::new(BTreeMap::new()),
            state_lifecycle: RwLock::new(()),
        }
    }
}

impl ApplicationServiceBridge {
    /// 返回共享 application state 的 daemon 生命周期门禁。
    ///
    /// 输出：所有 Rust/.NET 普通请求共用的读锁，以及 Provider 状态变更共用的独占写锁。
    /// 不变量：两套 daemon 指向同一 state root；Provider 快照变更持有写锁直到两套旧连接均被移除，普通请求持有读锁直到响应完成。
    /// 失败：本方法不执行 I/O 且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn lifecycle(&self) -> &RwLock<()> {
        &self.state_lifecycle
    }
}

struct DaemonConnection {
    writer: Mutex<ChildStdin>,
    pending: SharedPendingResponses,
    next_request_id: AtomicU64,
    healthy: Arc<AtomicBool>,
    initialize_result: Mutex<Option<Value>>,
    _process: ManagedChild,
}

struct ManagedChild {
    child: Child,
    process_id: u32,
}

impl Drop for ManagedChild {
    /// 终止 daemon 及其进程树，避免桌面壳退出后遗留工具进程。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn drop(&mut self) {
        terminate_process_tree(self.process_id);
        let _ = self.child.start_kill();
    }
}

impl DaemonConnection {
    /// 向已初始化 daemon 写入一帧，并按 host 生成的 request ID 等待对应响应。
    ///
    /// 输入：allowlist 内的方法与严格对象参数。
    /// 输出：daemon 成功响应的 result。
    /// 不变量：写入前注册 waiter；超时或 I/O 失败不会遗留 pending capability。
    /// 失败：连接关闭、帧超限、写入失败、RPC error 或 30 秒超时。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn request(&self, method: &str, params: Value) -> Result<Value, BridgeError> {
        if !self.healthy.load(Ordering::Acquire) || !params.is_object() {
            return Err(BridgeError::internal(
                "application service connection is unavailable",
            ));
        }
        let id = format!(
            "desktop-{}",
            self.next_request_id.fetch_add(1, Ordering::Relaxed)
        );
        let mut frame = serde_json::to_vec(&json!({
            "jsonrpc":"2.0",
            "id":id,
            "method":method,
            "params":params
        }))
        .map_err(|_| BridgeError::internal("application service request encoding failed"))?;
        if frame.len() > MAX_FRAME_BYTES {
            return Err(BridgeError::internal(
                "application service request exceeds frame limit",
            ));
        }
        frame.push(b'\n');
        let (sender, receiver) = oneshot::channel();
        self.pending.lock().await.insert(id.clone(), sender);
        let write_result = {
            let mut writer = self.writer.lock().await;
            async {
                writer.write_all(&frame).await?;
                writer.flush().await
            }
            .await
        };
        if write_result.is_err() {
            self.pending.lock().await.remove(&id);
            self.healthy.store(false, Ordering::Release);
            return Err(BridgeError::internal(
                "application service request write failed",
            ));
        }
        match tokio::time::timeout(REQUEST_TIMEOUT, receiver).await {
            Ok(Ok(result)) => result,
            Ok(Err(_)) => Err(BridgeError::internal(
                "application service connection closed",
            )),
            Err(_) => {
                self.pending.lock().await.remove(&id);
                Err(BridgeError::internal(
                    "application service request timed out",
                ))
            }
        }
    }

    /// 返回启动握手时保存的独立 initialize 投影。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn initialize_result(&self) -> Result<Value, BridgeError> {
        self.initialize_result
            .lock()
            .await
            .clone()
            .ok_or_else(|| BridgeError::internal("application service is not initialized"))
    }
}

/// 启动或复用所选 daemon，并返回经过实际握手的 initialize result。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[tauri::command]
pub(crate) async fn application_service_initialize(
    app: AppHandle,
    state: tauri::State<'_, ApplicationServiceBridge>,
    backend: BackendKind,
) -> Result<Value, BridgeError> {
    let _lifecycle = state.lifecycle().read().await;
    ensure_connection(&app, &state, backend)
        .await?
        .initialize_result()
        .await
}

/// 仅转发桌面客户端所需的品牌中立 application-service 方法。
///
/// 输入：后端选择、固定 allowlist 方法和 JSON object params。
/// 输出：对应 JSON-RPC success result。
/// 不变量：WebView 不能指定 executable、argv、state path、workspace 或通用 shell；Provider 变更帧一旦发出，无论回执结果都丢弃共享 state root 的两套旧启动快照。
/// 失败：移动平台、未知方法、连接、协议、daemon error 或超时均拒绝。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[tauri::command]
pub(crate) async fn application_service_request(
    app: AppHandle,
    state: tauri::State<'_, ApplicationServiceBridge>,
    backend: BackendKind,
    method: String,
    params: Value,
) -> Result<Value, BridgeError> {
    if !allowed_method(&method) {
        return Err(BridgeError::internal(
            "application service method is not allowed",
        ));
    }
    validate_forwarded_request(&method, &params)?;
    if invalidates_daemon_snapshot(&method) {
        let _lifecycle = state.lifecycle().write().await;
        let connection = ensure_connection(&app, &state, backend).await?;
        let result = connection.request(&method, params).await;
        state.connections.lock().await.clear();
        return result;
    }
    let _lifecycle = state.lifecycle().read().await;
    ensure_connection(&app, &state, backend)
        .await?
        .request(&method, params)
        .await
}

/// 把 Provider credential 送到本地 daemon 的专用写入边界。
///
/// 输入：后端、受限 Provider ID、封闭 credential 用途与密码输入框中的 credential。
/// 输出：只含 `credentialConfigured` 的脱敏结果。
/// 不变量：此方法不属于通用 RPC allowlist；credential 不进入 argv、日志、Session 或返回值；写入帧发出后无论回执结果都丢弃共享 state root 的两套旧 daemon。
/// 失败：Provider ID、credential、连接、CredentialStore 或协议失败时返回脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[tauri::command]
pub(crate) async fn provider_credential_set(
    app: AppHandle,
    state: tauri::State<'_, ApplicationServiceBridge>,
    backend: BackendKind,
    provider_id: String,
    credential_kind: String,
    credential: String,
) -> Result<Value, BridgeError> {
    if !valid_provider_id(&provider_id)
        || !valid_credential_kind(&credential_kind)
        || !valid_credential(&credential)
    {
        return Err(BridgeError::internal(
            "provider credential input is invalid",
        ));
    }
    let _lifecycle = state.lifecycle().write().await;
    let connection = ensure_connection(&app, &state, backend).await?;
    let result = connection
        .request(
            "providerCredentials/set",
            json!({
                "providerId":provider_id,
                "credentialKind":credential_kind,
                "credential":credential
            }),
        )
        .await;
    state.connections.lock().await.clear();
    result
}

/// 删除本地 daemon `CredentialStore` 中指定 Provider 的 credential。
///
/// 输入：后端、受限 Provider ID 与封闭 credential 用途。
/// 输出：只含 `credentialConfigured:false` 的脱敏结果。
/// 不变量：WebView 无法指定 state root、文件路径或任意 daemon 方法；写入帧发出后无论回执结果都丢弃共享 state root 的两套旧 daemon。
/// 失败：Provider ID、连接、CredentialStore 或协议失败时返回脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[tauri::command]
pub(crate) async fn provider_credential_remove(
    app: AppHandle,
    state: tauri::State<'_, ApplicationServiceBridge>,
    backend: BackendKind,
    provider_id: String,
    credential_kind: String,
) -> Result<Value, BridgeError> {
    if !valid_provider_id(&provider_id) || !valid_credential_kind(&credential_kind) {
        return Err(BridgeError::internal(
            "provider credential input is invalid",
        ));
    }
    let _lifecycle = state.lifecycle().write().await;
    let connection = ensure_connection(&app, &state, backend).await?;
    let result = connection
        .request(
            "providerCredentials/remove",
            json!({"providerId":provider_id,"credentialKind":credential_kind}),
        )
        .await;
    state.connections.lock().await.clear();
    result
}

/// 在同一后端连接健康时复用，否则完成一次 fail-closed 重启与握手。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn ensure_connection(
    app: &AppHandle,
    state: &ApplicationServiceBridge,
    backend: BackendKind,
) -> Result<Arc<DaemonConnection>, BridgeError> {
    if cfg!(any(target_os = "android", target_os = "ios")) {
        return Err(BridgeError::internal(
            "mobile clients require an authenticated remote application service",
        ));
    }
    let mut connections = state.connections.lock().await;
    if let Some(connection) = connections.get(&backend)
        && connection.healthy.load(Ordering::Acquire)
    {
        return Ok(Arc::clone(connection));
    }
    connections.remove(&backend);
    let connection = spawn_connection(app, backend).await?;
    connections.insert(backend, Arc::clone(&connection));
    Ok(connection)
}

/// 启动受信任路径中的 daemon、建立有界 reader，并在返回前完成 initialize。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn spawn_connection(
    app: &AppHandle,
    backend: BackendKind,
) -> Result<Arc<DaemonConnection>, BridgeError> {
    let executable = resolve_daemon_path(app, backend)?;
    let workspace = resolve_workspace()?;
    let state_root = app
        .path()
        .app_local_data_dir()
        .map_err(|_| BridgeError::internal("application state directory is unavailable"))?
        .join("service-state");
    std::fs::create_dir_all(&state_root)
        .map_err(|_| BridgeError::internal("application state directory creation failed"))?;
    let mut command = Command::new(executable);
    command
        .args(daemon_arguments(backend, &workspace, &state_root))
        .current_dir(&workspace)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .kill_on_drop(true);
    configure_process_group(&mut command);
    let mut child = command
        .spawn()
        .map_err(|_| BridgeError::internal("application service daemon failed to start"))?;
    let process_id = child.id().ok_or_else(|| {
        BridgeError::internal("application service process identity is unavailable")
    })?;
    let stdin = child
        .stdin
        .take()
        .ok_or_else(|| BridgeError::internal("application service stdin is unavailable"))?;
    let stdout = child
        .stdout
        .take()
        .ok_or_else(|| BridgeError::internal("application service stdout is unavailable"))?;
    let pending = Arc::new(Mutex::new(HashMap::new()));
    let healthy = Arc::new(AtomicBool::new(true));
    let connection = Arc::new(DaemonConnection {
        writer: Mutex::new(stdin),
        pending: Arc::clone(&pending),
        next_request_id: AtomicU64::new(1),
        healthy: Arc::clone(&healthy),
        initialize_result: Mutex::new(None),
        _process: ManagedChild { child, process_id },
    });
    let reader_app = app.clone();
    tauri::async_runtime::spawn(async move {
        reader_loop(stdout, reader_app, backend, pending, healthy).await;
    });
    let initialize = connection
        .request(
            "initialize",
            json!({
                "protocolVersions":["0.1"],
                "client":{"name":"agent-client-desktop","version":env!("CARGO_PKG_VERSION")},
                "capabilities":{"eventReplay":true,"interactiveApprovals":true}
            }),
        )
        .await?;
    *connection.initialize_result.lock().await = Some(initialize);
    Ok(connection)
}

/// 持续解析 daemon stdout，响应按 ID 路由，事件只通过固定 Tauri event 上送。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn reader_loop(
    stdout: tokio::process::ChildStdout,
    app: AppHandle,
    backend: BackendKind,
    pending: SharedPendingResponses,
    healthy: Arc<AtomicBool>,
) {
    let mut reader = BufReader::new(stdout);
    let failure = loop {
        match read_bounded_line(&mut reader, MAX_FRAME_BYTES).await {
            Ok(Some(frame)) => {
                if let Err(error) = handle_frame(&app, backend, &pending, &frame).await {
                    break error;
                }
            }
            Ok(None) => break BridgeError::internal("application service daemon closed"),
            Err(error) => break error,
        }
    };
    healthy.store(false, Ordering::Release);
    let senders = pending
        .lock()
        .await
        .drain()
        .map(|(_, sender)| sender)
        .collect::<Vec<_>>();
    for sender in senders {
        let _ = sender.send(Err(failure.clone()));
    }
}

/// 严格分派单个 response 或 event notification，拒绝其他 stdout 数据。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn handle_frame(
    app: &AppHandle,
    backend: BackendKind,
    pending: &PendingResponses,
    frame: &[u8],
) -> Result<(), BridgeError> {
    let value: Value = serde_json::from_slice(frame)
        .map_err(|_| BridgeError::internal("application service emitted invalid JSON"))?;
    let object = value
        .as_object()
        .ok_or_else(|| BridgeError::internal("application service emitted a non-object frame"))?;
    if object.get("method").and_then(Value::as_str) == Some("event") {
        if frame.len() > MAX_EVENT_BYTES {
            return Err(BridgeError::internal(
                "application service event exceeds limit",
            ));
        }
        let _ = app.emit(
            "application-service-event",
            json!({"backend":backend,"notification":value}),
        );
        return Ok(());
    }
    if object.get("jsonrpc").and_then(Value::as_str) != Some("2.0") {
        return Err(BridgeError::internal(
            "application service response is invalid",
        ));
    }
    let id = object
        .get("id")
        .and_then(Value::as_str)
        .ok_or_else(|| BridgeError::internal("application service response ID is invalid"))?;
    let result = match (object.get("result"), object.get("error")) {
        (Some(result), None) => Ok(result.clone()),
        (None, Some(error)) if error.is_object() => Err(BridgeError::from_rpc(error)),
        _ => {
            return Err(BridgeError::internal(
                "application service response shape is invalid",
            ));
        }
    };
    if let Some(sender) = pending.lock().await.remove(id) {
        let _ = sender.send(result);
    }
    Ok(())
}

/// 读取一条有界 LF/CRLF 帧，超限时排空到边界并失败关闭。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn read_bounded_line<R>(reader: &mut R, limit: usize) -> Result<Option<Vec<u8>>, BridgeError>
where
    R: AsyncBufRead + Unpin,
{
    let mut output = Vec::new();
    let mut oversized = false;
    loop {
        let available = reader
            .fill_buf()
            .await
            .map_err(|_| BridgeError::internal("application service response read failed"))?;
        if available.is_empty() {
            if oversized {
                return Err(BridgeError::internal(
                    "application service frame exceeds limit",
                ));
            }
            if output.is_empty() {
                return Ok(None);
            }
            return Ok(Some(output));
        }
        let newline = available.iter().position(|byte| *byte == b'\n');
        let take = newline.unwrap_or(available.len());
        if !oversized {
            if output.len().saturating_add(take) > limit {
                oversized = true;
                output.clear();
            } else {
                output.extend_from_slice(&available[..take]);
            }
        }
        let consumed = take + usize::from(newline.is_some());
        reader.consume(consumed);
        if newline.is_some() {
            if oversized {
                return Err(BridgeError::internal(
                    "application service frame exceeds limit",
                ));
            }
            if output.last() == Some(&b'\r') {
                output.pop();
            }
            return Ok(Some(output));
        }
    }
}

/// 判断 `WebView` 可调用的方法，明确排除 faux、terminal 与任意扩展方法。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn allowed_method(method: &str) -> bool {
    matches!(
        method,
        "models/list"
            | "artifacts/create"
            | "run/start"
            | "run/cancel"
            | "run/steer"
            | "run/followUp"
            | "approval/respond"
            | "providerCatalog/list"
            | "session/get"
            | "session/branch/select"
            | "session/compact"
            | "agentProfiles/list"
            | "agentProfiles/create"
            | "agentProfiles/update"
            | "agentProfiles/delete"
            | "providerConnections/list"
            | "providerConnections/create"
            | "providerConnections/update"
            | "providerConnections/delete"
            | "providerConnections/discoverModels"
            | "session/list"
            | "session/archive"
            | "session/restore"
            | "session/delete"
    )
}

/// 在启动或失效 daemon 前验证转发请求一定能形成有界 JSON object 帧。
///
/// 输入：已通过 allowlist 的方法和来自 `WebView` 的未信任 params。
/// 输出：使用最大长度 host request ID 仍不超过帧上限时成功。
/// 不变量：失败发生在获取生命周期写锁和启动 daemon 之前；本函数不读取状态或发送数据。
/// 失败：params 不是对象、JSON 编码失败或完整帧超过 1 MiB 时返回脱敏 bridge 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_forwarded_request(method: &str, params: &Value) -> Result<(), BridgeError> {
    if !params.is_object() {
        return Err(BridgeError::internal(
            "application service request parameters are invalid",
        ));
    }
    let frame = serde_json::to_vec(&json!({
        "jsonrpc":"2.0",
        "id":"desktop-18446744073709551615",
        "method":method,
        "params":params
    }))
    .map_err(|_| BridgeError::internal("application service request encoding failed"))?;
    if frame.len() > MAX_FRAME_BYTES {
        return Err(BridgeError::internal(
            "application service request exceeds frame limit",
        ));
    }
    Ok(())
}

/// 判断请求一旦发出是否必须丢弃启动期 capability/model 快照。
///
/// 输入：已通过 allowlist 的 application-service 方法；输出：是否属于 Provider 配置变更。
/// 不变量：返回 true 的请求即使回执失败也必须失效旧快照，以覆盖 durable mutation 与响应丢失的歧义。
/// 失败：本函数不执行 I/O 且不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn invalidates_daemon_snapshot(method: &str) -> bool {
    matches!(
        method,
        "providerConnections/create"
            | "providerConnections/update"
            | "providerConnections/delete"
            | "providerConnections/discoverModels"
    )
}

/// 验证 Provider ID 是有界、品牌中立的 ASCII 标识符。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn valid_provider_id(value: &str) -> bool {
    let bytes = value.as_bytes();
    (1..=128).contains(&bytes.len())
        && (bytes[0].is_ascii_lowercase() || bytes[0].is_ascii_digit())
        && bytes.iter().all(|byte| {
            byte.is_ascii_lowercase() || byte.is_ascii_digit() || matches!(byte, b'.' | b'-')
        })
}

/// 验证 Provider credential 用途属于互不回退的封闭枚举。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn valid_credential_kind(value: &str) -> bool {
    matches!(value, "responses" | "image")
}

/// 验证 credential 可安全进入单个本地写入请求且不能注入协议行。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn valid_credential(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 16_384
        && !value.bytes().any(|byte| matches!(byte, b'\r' | b'\n' | 0))
}

/// 构造实现专属但不可由 `WebView` 修改的 daemon argv。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn daemon_arguments(backend: BackendKind, workspace: &Path, state_root: &Path) -> Vec<OsString> {
    let mut arguments = vec![OsString::from("daemon")];
    if backend == BackendKind::Dotnet {
        arguments.push(OsString::from("--stdio"));
    }
    arguments.extend([
        OsString::from("--workspace"),
        workspace.as_os_str().to_owned(),
        OsString::from("--state-dir"),
        state_root.as_os_str().to_owned(),
    ]);
    arguments
}

/// 从可信宿主环境、bundle resource、同目录或开发构建目录解析 daemon。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn resolve_daemon_path(app: &AppHandle, backend: BackendKind) -> Result<PathBuf, BridgeError> {
    let environment_name = match backend {
        BackendKind::Rust => "QXNM_FORGE_RUST_DAEMON",
        BackendKind::Dotnet => "QXNM_FORGE_DOTNET_DAEMON",
    };
    if let Some(configured) = std::env::var_os(environment_name) {
        return canonical_executable(&PathBuf::from(configured));
    }
    let file_name = executable_name(backend);
    let mut candidates = Vec::new();
    if let Ok(resource_dir) = app.path().resource_dir() {
        candidates.push(resource_dir.join("binaries").join(&file_name));
    }
    if let Ok(current_executable) = std::env::current_exe()
        && let Some(parent) = current_executable.parent()
    {
        candidates.push(parent.join(&file_name));
    }
    let manifest = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    match backend {
        BackendKind::Rust => {
            candidates.push(manifest.join("../../rust/target/debug").join(&file_name));
            candidates.push(manifest.join("../../rust/target/release").join(&file_name));
        }
        BackendKind::Dotnet => {
            candidates.push(
                manifest
                    .join("../../dotnet/src/QxnmForge.Cli/bin/Debug/net10.0")
                    .join(&file_name),
            );
            candidates.push(
                manifest
                    .join("../../dotnet/src/QxnmForge.Cli/bin/Release/net10.0")
                    .join(&file_name),
            );
        }
    }
    candidates
        .into_iter()
        .find_map(|candidate| canonical_executable(&candidate).ok())
        .ok_or_else(|| BridgeError::internal("application service daemon is not installed"))
}

/// 规范化并验证 daemon 是普通文件；显式错误不回显宿主路径。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn canonical_executable(path: &Path) -> Result<PathBuf, BridgeError> {
    let canonical = path.canonicalize().map_err(|_| {
        BridgeError::internal("configured application service daemon is unavailable")
    })?;
    if !canonical.is_file() {
        return Err(BridgeError::internal(
            "configured application service daemon is unavailable",
        ));
    }
    Ok(canonical)
}

/// 解析宿主固定 workspace；WebView 不可提供路径。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn resolve_workspace() -> Result<PathBuf, BridgeError> {
    let path = std::env::var_os("QXNM_FORGE_WORKSPACE")
        .map(PathBuf::from)
        .map_or_else(std::env::current_dir, Ok)
        .and_then(|value| value.canonicalize())
        .map_err(|_| BridgeError::internal("application workspace is unavailable"))?;
    if !path.is_dir() {
        return Err(BridgeError::internal(
            "application workspace is unavailable",
        ));
    }
    Ok(path)
}

/// 返回当前目标平台的固定 daemon 文件名。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn executable_name(backend: BackendKind) -> OsString {
    let stem = match backend {
        BackendKind::Rust => "qxnm-forge",
        BackendKind::Dotnet => "qxnm-forge-dotnet",
    };
    OsString::from(format!("{stem}{}", std::env::consts::EXE_SUFFIX))
}

/// 为 daemon 配置独立进程组，供壳退出时执行进程树终止。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn configure_process_group(command: &mut Command) {
    #[cfg(unix)]
    {
        use std::os::unix::process::CommandExt as _;
        command.as_std_mut().process_group(0);
    }
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt as _;
        const CREATE_NEW_PROCESS_GROUP: u32 = 0x0000_0200;
        command
            .as_std_mut()
            .creation_flags(CREATE_NEW_PROCESS_GROUP);
    }
}

/// 按平台终止 daemon 进程组；参数只来自刚启动 child 的 OS PID。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn terminate_process_tree(process_id: u32) {
    #[cfg(unix)]
    {
        use nix::sys::signal::{Signal, killpg};
        use nix::unistd::Pid;
        if let Ok(raw) = i32::try_from(process_id) {
            let _ = killpg(Pid::from_raw(raw), Signal::SIGKILL);
        }
    }
    #[cfg(windows)]
    {
        let system_root =
            std::env::var_os("SystemRoot").unwrap_or_else(|| OsString::from(r"C:\Windows"));
        let taskkill = PathBuf::from(system_root)
            .join("System32")
            .join("taskkill.exe");
        let _ = std::process::Command::new(taskkill)
            .args(["/PID", &process_id.to_string(), "/T", "/F"])
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn();
    }
}

#[cfg(test)]
mod tests {
    use super::{
        ApplicationServiceBridge, BackendKind, MAX_FRAME_BYTES, allowed_method, daemon_arguments,
        invalidates_daemon_snapshot, valid_credential, valid_credential_kind, valid_provider_id,
        validate_forwarded_request,
    };
    use serde_json::json;
    use std::path::Path;

    /// 验证 `WebView` allowlist 不开放测试、terminal 或任意 shell 方法。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn method_allowlist_is_narrow() {
        assert!(allowed_method("agentProfiles/create"));
        assert!(allowed_method("providerCatalog/list"));
        assert!(allowed_method("providerConnections/list"));
        assert!(allowed_method("providerConnections/discoverModels"));
        assert!(allowed_method("session/archive"));
        assert!(allowed_method("run/start"));
        assert!(allowed_method("artifacts/create"));
        assert!(!allowed_method("providerCredentials/set"));
        assert!(!allowed_method("faux/configure"));
        assert!(!allowed_method("terminal/open"));
        assert!(!allowed_method("shell/execute"));
    }

    /// 验证 Provider mutation 会刷新 daemon，而只读与 Session 请求不会误触发重启。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn provider_mutations_restart_capability_snapshot() {
        assert!(invalidates_daemon_snapshot("providerConnections/create"));
        assert!(invalidates_daemon_snapshot("providerConnections/update"));
        assert!(invalidates_daemon_snapshot(
            "providerConnections/discoverModels"
        ));
        assert!(!invalidates_daemon_snapshot("providerConnections/list"));
        assert!(!invalidates_daemon_snapshot("session/archive"));
    }

    /// 验证非法或超限 mutation params 在进入共享生命周期门禁前失败关闭。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn forwarded_request_is_validated_before_snapshot_invalidation() {
        assert!(validate_forwarded_request("providerConnections/create", &json!({})).is_ok());
        assert!(validate_forwarded_request("providerConnections/create", &json!([])).is_err());
        assert!(
            validate_forwarded_request(
                "providerConnections/create",
                &json!({"value":"x".repeat(MAX_FRAME_BYTES)})
            )
            .is_err()
        );
    }

    /// 验证两套后端的普通请求共享读门禁，并共同阻止 Provider 状态变更。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn shared_state_lifecycle_gates_snapshot_mutations() {
        let bridge = ApplicationServiceBridge::default();
        let first_reader = bridge
            .lifecycle()
            .try_read()
            .expect("first backend should acquire a shared lifecycle guard");
        let second_reader = bridge
            .lifecycle()
            .try_read()
            .expect("second backend should share the lifecycle read guard");

        assert!(bridge.lifecycle().try_write().is_err());
        drop(second_reader);
        drop(first_reader);
        assert!(bridge.lifecycle().try_write().is_ok());
    }

    /// 验证专用 credential command 拒绝协议注入与非法 Provider ID。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn provider_credential_inputs_are_bounded() {
        assert!(valid_provider_id("custom-newapi"));
        assert!(valid_credential_kind("responses"));
        assert!(valid_credential_kind("image"));
        assert!(!valid_credential_kind("codex"));
        assert!(!valid_provider_id("Custom NewAPI"));
        assert!(valid_credential("test-only-secret"));
        assert!(!valid_credential("line-one\nline-two"));
        assert!(!valid_credential(""));
    }

    /// 验证两套 daemon argv 差异只包含 .NET 必需的 stdio 门。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn daemon_arguments_are_host_owned() {
        let rust = daemon_arguments(
            BackendKind::Rust,
            Path::new("/workspace"),
            Path::new("/state"),
        );
        let dotnet = daemon_arguments(
            BackendKind::Dotnet,
            Path::new("/workspace"),
            Path::new("/state"),
        );
        assert!(!rust.iter().any(|value| value == "--stdio"));
        assert!(dotnet.iter().any(|value| value == "--stdio"));
        assert_eq!(
            rust.first().and_then(|value| value.to_str()),
            Some("daemon")
        );
    }
}
