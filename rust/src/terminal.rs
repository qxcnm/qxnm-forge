//! 原生 Unix PTY、connection-bound attachment、bounded replay 与统一生命周期清理。
//!
//! 作者：高宏顺 <18272669457@163.com>

use serde::Serialize;
use serde_json::Value;

/// terminal RPC 在真实 PTY 能力和显式宿主策略同时可用时广告的方法。
pub const TERMINAL_METHODS: &[&str] = &[
    "terminal/open",
    "terminal/write",
    "terminal/resize",
    "terminal/signal",
    "terminal/attach",
    "terminal/close",
];

/// terminal RPC 独立于 Agent event envelope 广告的通知类型。
pub const TERMINAL_EVENT_TYPES: &[&str] = &[
    "terminal.output",
    "terminal.truncated",
    "terminal.exited",
    "terminal.closed",
];

/// 可直接写入 NDJSON stdout 的 `terminal/event` 通知。
#[derive(Debug, Clone, Serialize)]
pub struct TerminalNotification {
    jsonrpc: &'static str,
    method: &'static str,
    params: TerminalEvent,
}

/// 由 daemon 在成功响应 flush 后执行的 terminal 动作。
#[derive(Debug)]
pub enum TerminalAfterResponse {
    Activate(String),
    Replay {
        terminal_id: String,
        frames: Vec<TerminalNotification>,
    },
    Terminate(String),
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct TerminalEvent {
    session_id: String,
    terminal_id: String,
    seq: u64,
    time: String,
    #[serde(rename = "type")]
    event_type: String,
    data: Value,
}

#[cfg(unix)]
mod platform {
    use std::collections::{BTreeMap, HashMap, VecDeque};
    use std::fs::File;
    #[cfg(target_os = "linux")]
    use std::fs::OpenOptions;
    #[cfg(target_os = "linux")]
    use std::io::{Read, Seek, SeekFrom, Write};
    use std::os::fd::{AsRawFd, OwnedFd, RawFd};
    #[cfg(target_os = "linux")]
    use std::os::unix::fs::{MetadataExt, OpenOptionsExt};
    use std::path::{Path, PathBuf};
    use std::process::{ExitStatus, Stdio};
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::sync::{Arc, Mutex as StdMutex};
    use std::time::{Duration, Instant};

    use chrono::{SecondsFormat, Utc};
    #[cfg(target_os = "linux")]
    use nix::fcntl::{FcntlArg, OFlag, SealFlag, fcntl, open};
    use nix::pty::Winsize;
    #[cfg(target_os = "linux")]
    use nix::pty::{grantpt, posix_openpt, ptsname_r, unlockpt};
    #[cfg(target_os = "linux")]
    use nix::sys::memfd::{MFdFlags, memfd_create};
    use nix::sys::signal::{Signal, killpg};
    #[cfg(target_os = "linux")]
    use nix::sys::stat::Mode;
    use nix::unistd::Pid;
    use serde::Deserialize;
    use serde_json::{Value, json};
    #[cfg(target_os = "linux")]
    use sha2::{Digest, Sha256};
    use tokio::io::unix::AsyncFd;
    use tokio::process::{Child, Command};
    use tokio::sync::{Mutex, Notify, mpsc, oneshot, watch};
    use tokio::task::JoinHandle;
    use tokio::time;
    use uuid::Uuid;

    use super::{
        TERMINAL_EVENT_TYPES, TERMINAL_METHODS, TerminalAfterResponse, TerminalEvent,
        TerminalNotification,
    };
    use crate::error::{AgentError, ErrorCode};
    use crate::executor::ProcessTreeGuard;

    #[cfg(target_os = "linux")]
    const FIXTURE_HELPER_SHA256: &str =
        "9e5a600229a3632e7444b03dbaa4c3359aec63dd481f934225aa20ad22a351f0";
    const MAX_SAFE_INTEGER: u64 = 9_007_199_254_740_991;
    #[cfg(target_os = "linux")]
    const MAX_HELPER_BYTES: u64 = 1024 * 1024;
    const TERMINAL_CHANNEL_CAPACITY: usize = 256;
    const MAX_TERMINALS: usize = 4;
    const MAX_TOTAL_RETENTION_BYTES: usize = 1024 * 1024;
    const MAX_RETAINED_EVENTS_PER_TERMINAL: usize = 1024;
    const MAX_INPUT_QUEUE_ITEMS: usize = 64;
    const MAX_TERMINAL_EVENT_RAW_BYTES: u64 = 32 * 1024;
    const MAX_TERMINAL_EVENT_FRAME_BYTES: usize = 262_144;
    const TERMINATION_GRACE: Duration = Duration::from_millis(200);
    const CHILD_REAP_TIMEOUT: Duration = Duration::from_secs(1);
    const OUTPUT_DRAIN_TIMEOUT: Duration = Duration::from_secs(1);
    const OUTPUT_COALESCE_WINDOW: Duration = Duration::from_millis(5);
    const WIRE_DELIVERY_TIMEOUT: Duration = Duration::from_secs(2);

    /// 管理一个 stdio 连接内全部 Unix PTY 与 attachment capability。
    #[derive(Clone)]
    pub struct TerminalManager {
        inner: Arc<TerminalInner>,
    }

    struct TerminalInner {
        workspace: PathBuf,
        enabled: bool,
        open_gate: Mutex<()>,
        terminals: Mutex<HashMap<String, Arc<Terminal>>>,
        event_sender: StdMutex<Option<mpsc::Sender<TerminalNotification>>>,
        fatal_sender: watch::Sender<bool>,
        shutting_down: AtomicBool,
    }

    struct Terminal {
        terminal_id: String,
        session_id: String,
        limits: TerminalLimits,
        state: Mutex<TerminalState>,
        event_gate: Mutex<()>,
        written_sender: watch::Sender<u64>,
        cleanup_started: AtomicBool,
        reader_done: AtomicBool,
        output_delivery_failed: AtomicBool,
        exit_done: AtomicBool,
        waiter_done: AtomicBool,
        closed_done: AtomicBool,
        reader_notify: Notify,
        waiter_notify: Notify,
        replay_sender: watch::Sender<bool>,
        closed_notify: Notify,
    }

    struct TerminalState {
        attachment_id: Option<String>,
        columns: u16,
        rows: u16,
        seq: u64,
        latest_output_seq: u64,
        input_seq: u64,
        pending_input_bytes: usize,
        retained_bytes: usize,
        retained: VecDeque<RetainedOutput>,
        decoder_pending: Vec<u8>,
        lifecycle: TerminalLifecycle,
        active: bool,
        closing: bool,
        last_activity: Instant,
        master: Option<Arc<AsyncFd<OwnedFd>>>,
        child: Option<Child>,
        tree: Option<ProcessTreeGuard>,
        control_sender: Option<mpsc::Sender<TerminalControl>>,
        input_sender: Option<mpsc::Sender<Vec<u8>>>,
        input_receiver: Option<mpsc::Receiver<Vec<u8>>>,
        tasks: Vec<JoinHandle<()>>,
    }

    enum TerminalControl {
        Signal {
            signal: Signal,
            response: oneshot::Sender<Result<(), AgentError>>,
        },
        Terminate,
    }

    struct WaiterCompletion {
        terminal: Arc<Terminal>,
    }

    impl Drop for WaiterCompletion {
        /// 功能：无论 waiter 正常返回、wait error 或 task panic，都发布唯一 waiter ownership 完成屏障。
        ///
        /// 输入：即将结束的 waiter 栈守卫。
        /// 输出：原子设置 waiterDone 并唤醒 close。
        /// 不变量：不设置 exitDone、不修改 Running lifecycle，也不发送任何 PID/PGID 信号。
        /// 失败：Drop 不返回错误。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        fn drop(&mut self) {
            self.terminal.waiter_done.store(true, Ordering::Release);
            self.terminal.waiter_notify.notify_waiters();
        }
    }

    #[derive(Debug, Clone, Copy)]
    struct TerminalLimits {
        lifetime: Duration,
        idle_timeout: Duration,
        retention_bytes: usize,
        input_buffer_bytes: usize,
        event_bytes: usize,
    }

    #[derive(Debug, Clone)]
    struct RetainedOutput {
        seq: u64,
        frame: TerminalNotification,
        byte_count: usize,
    }

    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    enum TerminalLifecycle {
        Running,
        Exited,
        Closed,
    }

    impl TerminalLifecycle {
        /// 功能：把内部终端生命周期映射为 frozen wire state。
        ///
        /// 输入：当前封闭生命周期枚举。
        /// 输出：`running`、`exited` 或 `closed`。
        /// 不变量：映射不包含 Rust 类型名或平台细节。
        /// 失败：本方法不返回错误。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        const fn wire_name(self) -> &'static str {
            match self {
                Self::Running => "running",
                Self::Exited => "exited",
                Self::Closed => "closed",
            }
        }
    }

    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct OpenParams {
        session_id: String,
        executable: String,
        args: Vec<String>,
        cwd: String,
        environment: Vec<Value>,
        size: SizeParams,
        limits: LimitParams,
        disconnect_policy: String,
        #[serde(default)]
        extensions: BTreeMap<String, Value>,
    }

    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct SizeParams {
        columns: u64,
        rows: u64,
    }

    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct LimitParams {
        lifetime_ms: u64,
        idle_timeout_ms: u64,
        retention_bytes: u64,
        input_buffer_bytes: u64,
        event_bytes: u64,
    }

    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct WriteParams {
        session_id: String,
        terminal_id: String,
        attachment_id: String,
        input_seq: u64,
        data: String,
        #[serde(default)]
        extensions: BTreeMap<String, Value>,
    }

    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct ResizeParams {
        session_id: String,
        terminal_id: String,
        attachment_id: String,
        size: SizeParams,
        #[serde(default)]
        extensions: BTreeMap<String, Value>,
    }

    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct SignalParams {
        session_id: String,
        terminal_id: String,
        attachment_id: String,
        signal: String,
        #[serde(default)]
        extensions: BTreeMap<String, Value>,
    }

    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct AttachParams {
        session_id: String,
        terminal_id: String,
        after_seq: u64,
        takeover: bool,
        #[serde(default)]
        extensions: BTreeMap<String, Value>,
    }

    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct CloseParams {
        session_id: String,
        terminal_id: String,
        attachment_id: String,
        mode: String,
        #[serde(default)]
        extensions: BTreeMap<String, Value>,
    }

    struct ApprovedOpen {
        session_id: String,
        interpreter: File,
        helper: File,
        columns: u16,
        rows: u16,
        limits: TerminalLimits,
    }

    impl TerminalManager {
        /// 功能：创建 fail-closed Unix PTY 管理器和有界 terminal event 通道。
        ///
        /// 输入：canonicalizable workspace 与 daemon 的显式 conformance 启动状态。
        /// 输出：管理器及其唯一通知 receiver；只有 conformance + fixture-only 外部策略同时存在时启用。
        /// 不变量：构造阶段不启动子进程；`--conformance` 单独不能授权 terminal；非 Unix stub 永不广告 ConPTY。
        /// 失败：workspace 不存在、不是目录或无法 canonicalize 时返回结构化错误。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub fn new(
            workspace: impl AsRef<Path>,
            conformance: bool,
        ) -> Result<(Self, mpsc::Receiver<TerminalNotification>), AgentError> {
            let workspace = std::fs::canonicalize(workspace.as_ref()).map_err(|error| {
                AgentError::new(
                    ErrorCode::InvalidParams,
                    format!("terminal workspace cannot be canonicalized: {error}"),
                )
            })?;
            if !workspace.is_dir() {
                return Err(AgentError::new(
                    ErrorCode::InvalidParams,
                    "terminal workspace must be a directory",
                ));
            }
            let enabled = cfg!(target_os = "linux")
                && conformance
                && std::env::var("QXNM_FORGE_TERMINAL_CONFORMANCE_POLICY").as_deref()
                    == Ok("fixture-only");
            let (event_sender, event_receiver) = mpsc::channel(TERMINAL_CHANNEL_CAPACITY);
            let (fatal_sender, _fatal_receiver) = watch::channel(false);
            Ok((
                Self {
                    inner: Arc::new(TerminalInner {
                        workspace,
                        enabled,
                        open_gate: Mutex::new(()),
                        terminals: Mutex::new(HashMap::new()),
                        event_sender: StdMutex::new(Some(event_sender)),
                        fatal_sender,
                        shutting_down: AtomicBool::new(false),
                    }),
                },
                event_receiver,
            ))
        }

        /// 功能：报告当前连接是否具备可诚实广告的 fixture-only Unix PTY 能力。
        ///
        /// 输入：当前管理器。
        /// 输出：显式双门控通过时为 true。
        /// 不变量：false 时 daemon 必须省略全部 terminal methods/event types，而不是使用 pipe 降级。
        /// 失败：本方法不返回错误。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[must_use]
        pub fn enabled(&self) -> bool {
            self.inner.enabled
        }

        /// 功能：返回可广告的 terminal methods 与独立 event types。
        ///
        /// 输入：当前连接策略状态。
        /// 输出：启用时返回完整 frozen 列表，否则返回两个空列表。
        /// 不变量：方法和事件能力总是成对出现。
        /// 失败：本方法不返回错误。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[must_use]
        pub fn capabilities(&self) -> (Vec<&'static str>, Vec<&'static str>) {
            if self.enabled() {
                (TERMINAL_METHODS.to_vec(), TERMINAL_EVENT_TYPES.to_vec())
            } else {
                (Vec::new(), Vec::new())
            }
        }

        /// 功能：在 daemon 已把一个 terminal notification 完整写入并 flush stdout 后推进 wire delivery 水位。
        ///
        /// 输入：刚完成 wire flush 的 connection-local terminal notification。
        /// 输出：对应 terminal 仍存在且身份匹配时单调推进 written seq，并唤醒 attach barrier。
        /// 不变量：水位绝不因 replay 的旧 seq 回退；入 mpsc、开始 write 或部分 write 均不能调用本方法。
        /// 失败：terminal 已关闭/移除或身份不匹配时安全忽略，本方法不返回错误。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub(crate) async fn acknowledge_written(&self, notification: &TerminalNotification) {
            let terminal = self
                .inner
                .terminals
                .lock()
                .await
                .get(&notification.params.terminal_id)
                .cloned();
            let Some(terminal) = terminal else {
                return;
            };
            if terminal.session_id != notification.params.session_id {
                return;
            }
            terminal.written_sender.send_if_modified(|written| {
                if notification.params.seq > *written {
                    *written = notification.params.seq;
                    true
                } else {
                    false
                }
            });
        }

        /// 功能：为 daemon event writer 提供 terminal delivery fatal 状态订阅，用于绕过已满事件队列立即失败关闭 transport。
        ///
        /// 输入：当前 connection-local manager。
        /// 输出：初值 false、任一不可恢复 terminal event/replay 投递失败后变为 true 的 watch receiver。
        /// 不变量：fatal 状态单调且不承载输出、路径或 secret；daemon 必须把 true 当作连接级终止条件。
        /// 失败：本方法不返回错误。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub(crate) fn subscribe_delivery_failure(&self) -> watch::Receiver<bool> {
            self.inner.fatal_sender.subscribe()
        }

        /// 功能：把 terminal 事件管线标记为不可恢复，并立即唤醒 daemon transport fail-close 观察者。
        ///
        /// 输入：发生 output/replay/lifecycle 投递失败的目标 terminal。
        /// 输出：单调设置 terminal 与 connection 两级 fatal 状态。
        /// 不变量：不通过已满 mpsc 发送错误、不包含动态输出；重复调用幂等。
        /// 失败：本方法不返回错误。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        fn fail_delivery(&self, terminal: &Terminal) {
            terminal
                .output_delivery_failed
                .store(true, Ordering::Release);
            self.inner.fatal_sender.send_replace(true);
        }

        /// 功能：按 hash-bound fixture-only 策略创建一个真实 controlling Unix PTY。
        ///
        /// 输入：完整 `terminal/open` JSON 参数。
        /// 输出：frozen openResult 与必须在响应 flush 后执行的激活动作。
        /// 不变量：只执行 Linux runner parent 的已打开解释器身份、sealed SHA-256 helper、`-I` 与固定 `terminal` argv；child 环境为空，attachment 仅在本连接内存保存。
        /// 失败：策略、路径、hash、limits、atomic CLOEXEC PTY、setsid/TIOCSCTTY、spawn 或进程树守卫失败时返回 portable 错误且不以 pipe 降级。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub async fn open(
            &self,
            value: Value,
        ) -> Result<(Value, TerminalAfterResponse), AgentError> {
            self.require_enabled()?;
            let params: OpenParams = parse_params(value)?;
            let approved = approve_fixture_open(&self.inner.workspace, params)?;
            let _open_gate = self.inner.open_gate.lock().await;
            self.check_capacity(approved.limits.retention_bytes).await?;
            let winsize = Winsize {
                ws_row: approved.rows,
                ws_col: approved.columns,
                ws_xpixel: 0,
                ws_ypixel: 0,
            };
            let (master, slave) = open_cloexec_pty(&winsize).map_err(|error| {
                process_start_error(format!("cannot create atomic CLOEXEC PTY: {error}"))
            })?;
            let master = Arc::new(AsyncFd::new(master).map_err(|error| {
                terminal_io_error(format!("cannot register PTY master: {error}"))
            })?);

            let slave_file = File::from(slave);
            let stdin = Stdio::from(slave_file.try_clone().map_err(process_start_io_error)?);
            let stdout = Stdio::from(slave_file.try_clone().map_err(process_start_io_error)?);
            let stderr = Stdio::from(slave_file);
            let helper_fd = approved.helper.as_raw_fd();
            let interpreter_fd = approved.interpreter.as_raw_fd();
            let helper_argument = inherited_fd_path(helper_fd)?;
            let interpreter_argument = inherited_fd_path(interpreter_fd)?;
            let mut command = Command::new(interpreter_argument);
            command
                .arg("-I")
                .arg(helper_argument)
                .arg("terminal")
                .current_dir(&self.inner.workspace)
                .env_clear()
                .stdin(stdin)
                .stdout(stdout)
                .stderr(stderr)
                .kill_on_drop(true);
            configure_pty_child(&mut command, interpreter_fd, helper_fd);
            let mut child = command.spawn().map_err(process_start_io_error)?;
            let child_id = child.id().ok_or_else(|| {
                process_start_error("spawned terminal omitted its pid".to_owned())
            })?;
            let tree = match ProcessTreeGuard::start(child_id) {
                Ok(tree) => tree,
                Err(error) => {
                    signal_process_group(child_id, Signal::SIGKILL);
                    let _ = child.kill().await;
                    let _ = child.wait().await;
                    return Err(error);
                }
            };
            let terminal_id = new_id("terminal");
            let attachment_id = new_id("attachment");
            let (input_sender, input_receiver) = mpsc::channel(MAX_INPUT_QUEUE_ITEMS);
            let (replay_sender, _replay_receiver) = watch::channel(false);
            let (written_sender, _written_receiver) = watch::channel(0_u64);
            let terminal = Arc::new(Terminal {
                terminal_id: terminal_id.clone(),
                session_id: approved.session_id,
                limits: approved.limits,
                state: Mutex::new(TerminalState {
                    attachment_id: Some(attachment_id.clone()),
                    columns: approved.columns,
                    rows: approved.rows,
                    seq: 0,
                    latest_output_seq: 0,
                    input_seq: 0,
                    pending_input_bytes: 0,
                    retained_bytes: 0,
                    retained: VecDeque::new(),
                    decoder_pending: Vec::new(),
                    lifecycle: TerminalLifecycle::Running,
                    active: false,
                    closing: false,
                    last_activity: Instant::now(),
                    master: Some(master),
                    child: Some(child),
                    tree: Some(tree),
                    control_sender: None,
                    input_sender: Some(input_sender),
                    input_receiver: Some(input_receiver),
                    tasks: Vec::new(),
                }),
                event_gate: Mutex::new(()),
                written_sender,
                cleanup_started: AtomicBool::new(false),
                reader_done: AtomicBool::new(false),
                output_delivery_failed: AtomicBool::new(false),
                exit_done: AtomicBool::new(false),
                waiter_done: AtomicBool::new(false),
                closed_done: AtomicBool::new(false),
                reader_notify: Notify::new(),
                waiter_notify: Notify::new(),
                replay_sender,
                closed_notify: Notify::new(),
            });
            self.inner
                .terminals
                .lock()
                .await
                .insert(terminal_id.clone(), terminal);
            Ok((
                json!({
                    "terminalId":terminal_id,
                    "attachmentId":attachment_id,
                    "state":"running",
                    "ptyKind":"unix_pty",
                    "size":{"columns":approved.columns,"rows":approved.rows},
                    "latestOutputSeq":0
                }),
                TerminalAfterResponse::Activate(terminal_id),
            ))
        }

        /// 功能：原子接受严格递增的 terminal input 并放入 bounded 字节队列。
        ///
        /// 输入：session/terminal/attachment、正 inputSeq 与最多 65536 UTF-8 字节。
        /// 输出：accepted、原 inputSeq 和完整 acceptedBytes。
        /// 不变量：队列不足时不接受任何前缀；stale attachment 不能写；数据不进入 argv 或日志。
        /// 失败：参数、ownership、顺序非法或队列已满时返回 invalid/stale/`terminal_backpressure`。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub async fn write(&self, value: Value) -> Result<Value, AgentError> {
            self.require_enabled()?;
            let params: WriteParams = parse_params(value)?;
            validate_extensions(&params.extensions)?;
            validate_terminal_ids(&params.session_id, &params.terminal_id)?;
            if !is_opaque_id(&params.attachment_id)
                || params.input_seq == 0
                || params.input_seq > MAX_SAFE_INTEGER
            {
                return Err(invalid_terminal_params(
                    "invalid terminal input identity or sequence",
                ));
            }
            let data = params.data.into_bytes();
            if data.is_empty() {
                return Err(invalid_terminal_params("terminal input must not be empty"));
            }
            if data.len() > 65_536 {
                return Err(invalid_terminal_params(
                    "terminal input exceeds 65536 bytes",
                ));
            }
            let terminal = self.lookup(&params.session_id, &params.terminal_id).await?;
            let mut state = terminal.state.lock().await;
            require_running(&state)?;
            require_attachment(&state, &params.attachment_id)?;
            if params.input_seq != state.input_seq.saturating_add(1) {
                return Err(invalid_terminal_params(
                    "terminal inputSeq is not the next value",
                ));
            }
            if state.pending_input_bytes.saturating_add(data.len())
                > terminal.limits.input_buffer_bytes
            {
                return Err(terminal_backpressure_error());
            }
            let sender = state.input_sender.as_ref().ok_or_else(|| {
                terminal_io_error("terminal input writer is unavailable".to_owned())
            })?;
            match sender.try_send(data.clone()) {
                Ok(()) => {}
                Err(mpsc::error::TrySendError::Full(_)) => {
                    return Err(terminal_backpressure_error());
                }
                Err(mpsc::error::TrySendError::Closed(_)) => {
                    return Err(terminal_not_running_error());
                }
            }
            state.input_seq = params.input_seq;
            state.pending_input_bytes = state.pending_input_bytes.saturating_add(data.len());
            state.last_activity = Instant::now();
            Ok(json!({
                "accepted":true,
                "inputSeq":params.input_seq,
                "acceptedBytes":data.len()
            }))
        }

        /// 功能：验证当前 attachment 后用 TIOCSWINSZ 调整真实 PTY 尺寸。
        ///
        /// 输入：session/terminal/attachment 和 1..1000 的 columns/rows。
        /// 输出：accepted 及实际应用的相同尺寸。
        /// 不变量：成功响应前 ioctl 已完成；不会只修改内存中的伪尺寸。
        /// 失败：参数、ownership、closed fd 或 ioctl 失败时返回 portable 错误。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub async fn resize(&self, value: Value) -> Result<Value, AgentError> {
            self.require_enabled()?;
            let params: ResizeParams = parse_params(value)?;
            validate_extensions(&params.extensions)?;
            validate_terminal_ids(&params.session_id, &params.terminal_id)?;
            if !is_opaque_id(&params.attachment_id) {
                return Err(invalid_terminal_params("invalid terminal attachment ID"));
            }
            let (columns, rows) = validate_size(&params.size)?;
            let terminal = self.lookup(&params.session_id, &params.terminal_id).await?;
            let mut state = terminal.state.lock().await;
            require_running(&state)?;
            require_attachment(&state, &params.attachment_id)?;
            let master = state.master.as_ref().ok_or_else(terminal_not_found_error)?;
            set_pty_size(master.get_ref().as_raw_fd(), columns, rows)
                .map_err(|error| terminal_io_error(format!("cannot resize terminal: {error}")))?;
            state.columns = columns;
            state.rows = rows;
            state.last_activity = Instant::now();
            Ok(json!({"accepted":true,"size":{"columns":columns,"rows":rows}}))
        }

        /// 功能：把 portable terminal signal 映射为当前 PTY 前台进程组信号。
        ///
        /// 输入：session/terminal/attachment 与 interrupt/terminate/kill/hangup。
        /// 输出：信号已发送或目标已竞态退出时返回 accepted。
        /// 不变量：wire 不接受任意数字信号；只向本管理器创建的正 PGID 发送。
        /// 失败：参数或 attachment 非法时返回 portable 错误；权限等信号错误返回 terminal_io。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub async fn signal(&self, value: Value) -> Result<Value, AgentError> {
            self.require_enabled()?;
            let params: SignalParams = parse_params(value)?;
            validate_extensions(&params.extensions)?;
            validate_terminal_ids(&params.session_id, &params.terminal_id)?;
            if !is_opaque_id(&params.attachment_id) {
                return Err(invalid_terminal_params("invalid terminal attachment ID"));
            }
            let selected = match params.signal.as_str() {
                "interrupt" => Signal::SIGINT,
                "terminate" => Signal::SIGTERM,
                "kill" => Signal::SIGKILL,
                "hangup" => Signal::SIGHUP,
                _ => return Err(invalid_terminal_params("unsupported terminal signal")),
            };
            let terminal = self.lookup(&params.session_id, &params.terminal_id).await?;
            let control = {
                let mut state = terminal.state.lock().await;
                require_running(&state)?;
                require_attachment(&state, &params.attachment_id)?;
                state.last_activity = Instant::now();
                state
                    .control_sender
                    .as_ref()
                    .cloned()
                    .ok_or_else(terminal_not_running_error)?
            };
            let (response, received) = oneshot::channel();
            control
                .send(TerminalControl::Signal {
                    signal: selected,
                    response,
                })
                .await
                .map_err(|_| terminal_not_running_error())?;
            received.await.map_err(|_| terminal_not_running_error())??;
            Ok(json!({"accepted":true}))
        }

        /// 功能：按显式 takeover 旋转 connection-bound attachment 并截取 bounded replay。
        ///
        /// 输入：session/terminal、0..latest output seq cursor 与 takeover 决策。
        /// 输出：新 attachment、当前状态/尺寸、最后 output seq、实际 replay 尾 seq，以及 response 后 replay 动作。
        /// 不变量：attach success 前等待全部旧 event 真正 flush 到 stdout；旧 token 立即失效；replayThroughSeq 等于实际 replay 最后一帧 seq，无 replay 时等于 afterSeq。
        /// 失败：wire ack、cursor/ID、terminal ownership 或 takeover 非法时不旋转 token、不承诺 replay。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub async fn attach(
            &self,
            value: Value,
        ) -> Result<(Value, TerminalAfterResponse), AgentError> {
            self.require_enabled()?;
            let params: AttachParams = parse_params(value)?;
            validate_extensions(&params.extensions)?;
            validate_terminal_ids(&params.session_id, &params.terminal_id)?;
            let terminal = self.lookup(&params.session_id, &params.terminal_id).await?;
            let _event_gate = terminal.event_gate.lock().await;
            let flush_through = {
                let state = terminal.state.lock().await;
                validate_attach_state(&state, &params)?;
                state.seq
            };
            if terminal.output_delivery_failed.load(Ordering::Acquire) {
                return Err(terminal_io_error(
                    "terminal output delivery previously failed".to_owned(),
                ));
            }
            wait_until_written(&terminal, flush_through).await?;
            let mut state = terminal.state.lock().await;
            validate_attach_state(&state, &params)?;
            let attachment_id = new_id("attachment");
            state.attachment_id = Some(attachment_id.clone());
            let replay = state
                .retained
                .iter()
                .filter(|item| item.seq > params.after_seq)
                .map(|item| item.frame.clone())
                .collect::<Vec<_>>();
            let latest = state.latest_output_seq;
            let replay_through = replay
                .last()
                .map_or(params.after_seq, |frame| frame.params.seq);
            terminal.replay_sender.send_replace(true);
            Ok((
                json!({
                    "terminalId":terminal.terminal_id,
                    "attachmentId":attachment_id,
                    "state":state.lifecycle.wire_name(),
                    "ptyKind":"unix_pty",
                    "size":{"columns":state.columns,"rows":state.rows},
                    "latestOutputSeq":latest,
                    "replayThroughSeq":replay_through
                }),
                TerminalAfterResponse::Replay {
                    terminal_id: terminal.terminal_id.clone(),
                    frames: replay,
                },
            ))
        }

        /// 功能：在 fixture-only 策略下仅接受 terminate，并安排 response 后的统一整树终止与 closed 清理。
        ///
        /// 输入：session/terminal/current attachment 与 mode。
        /// 输出：mode 严格为 terminate 时返回 accepted 及 terminate after-response 动作。
        /// 不变量：验证成功时在 response 构造前原子设置 closing，之后旧 attachment 的 write/resize/signal/attach 全部 fail closed；cleanup 仍只在 response flush 后启动一次。
        /// 失败：参数、terminal 或 attachment 非法时不改变进程和控制权。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub async fn close(
            &self,
            value: Value,
        ) -> Result<(Value, Option<TerminalAfterResponse>), AgentError> {
            self.require_enabled()?;
            let params: CloseParams = parse_params(value)?;
            validate_extensions(&params.extensions)?;
            validate_terminal_ids(&params.session_id, &params.terminal_id)?;
            if !is_opaque_id(&params.attachment_id) {
                return Err(invalid_terminal_params("invalid terminal attachment ID"));
            }
            if params.mode != "terminate" {
                return Err(permission_denied_error(
                    "terminal detach is outside the fixture-only policy",
                ));
            }
            let terminal = self.lookup(&params.session_id, &params.terminal_id).await?;
            let mut state = terminal.state.lock().await;
            if state.closing {
                return Err(terminal_not_running_error());
            }
            require_attachment(&state, &params.attachment_id)?;
            state.closing = true;
            Ok((
                json!({"accepted":true}),
                Some(TerminalAfterResponse::Terminate(
                    terminal.terminal_id.clone(),
                )),
            ))
        }

        /// 功能：在对应 success response 已 flush 后执行激活、replay 或 terminate。
        ///
        /// 输入：dispatch 阶段返回的 connection-local after-response capability。
        /// 输出：动作完整排队后成功；terminate 在独立 cleanup task 中执行。
        /// 不变量：open 输出和 attach replay 不可能先于各自响应；全部 replay 共享一个总 deadline且先于 live；未知 terminal 不创建替代对象。
        /// 失败：activate/replay 任一步失败返回 terminal_io，先在 gate 内标记 connection fatal，再由独立 cleanup 与 daemon shutdown 统一回收。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub async fn after_response(
            &self,
            action: TerminalAfterResponse,
        ) -> Result<(), AgentError> {
            match action {
                TerminalAfterResponse::Activate(terminal_id) => {
                    self.activate(&terminal_id).await?;
                }
                TerminalAfterResponse::Replay {
                    terminal_id,
                    frames,
                } => {
                    let terminal = self.inner.terminals.lock().await.get(&terminal_id).cloned();
                    let Some(terminal) = terminal else {
                        return Ok(());
                    };
                    let event_gate = terminal.event_gate.lock().await;
                    let replay_deadline = time::Instant::now() + WIRE_DELIVERY_TIMEOUT;
                    let mut failure = None;
                    for frame in frames {
                        if let Err(error) = self.emit_replay(frame, replay_deadline).await {
                            failure = Some(error);
                            break;
                        }
                    }
                    if failure.is_some() {
                        self.fail_delivery(&terminal);
                    }
                    terminal.replay_sender.send_replace(false);
                    drop(event_gate);
                    if let Some(error) = failure {
                        let manager = self.clone();
                        tokio::spawn(async move {
                            manager
                                .close_terminal(&terminal_id, "daemon_shutdown")
                                .await;
                        });
                        return Err(error);
                    }
                }
                TerminalAfterResponse::Terminate(terminal_id) => {
                    let manager = self.clone();
                    tokio::spawn(async move {
                        manager.close_terminal(&terminal_id, "client").await;
                    });
                }
            }
            Ok(())
        }

        /// 功能：daemon 连接结束时回收全部 PTY、关闭 terminal event sender 并等待并发 close。
        ///
        /// 输入：当前连接拥有的所有 terminal。
        /// 输出：全部 terminal 到达 closed 后返回。
        /// 不变量：daemon shutdown 无视 retain 而终止进程树；attachment 和 replay 不持久化。
        /// 失败：单个进程竞态、signal 或 fd 关闭错误按幂等清理处理，不阻止其余 terminal 回收。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub async fn shutdown(&self) {
            if self.inner.shutting_down.swap(true, Ordering::AcqRel) {
                return;
            }
            let terminals = self
                .inner
                .terminals
                .lock()
                .await
                .values()
                .cloned()
                .collect::<Vec<_>>();
            for terminal in &terminals {
                self.cancel_replay_barrier(terminal).await;
            }
            for terminal in terminals {
                self.close_terminal(&terminal.terminal_id, "daemon_shutdown")
                    .await;
            }
            if let Ok(mut sender) = self.inner.event_sender.lock() {
                sender.take();
            }
        }

        /// 功能：连接终止且响应无法交付时取消 attach replay barrier，允许统一 cleanup 继续。
        ///
        /// 输入：仍可能等待 attach after-response 的 terminal。
        /// 输出：barrier 清除并唤醒被暂停的 live event producer。
        /// 不变量：只由 daemon shutdown 调用，因此不会在成功 attach response 前泄露 replay/live；不发送未交付响应对应的 replay。
        /// 失败：本方法不返回错误；terminal 已无 barrier 时为幂等空操作。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        async fn cancel_replay_barrier(&self, terminal: &Arc<Terminal>) {
            let _event_gate = terminal.event_gate.lock().await;
            terminal.replay_sender.send_replace(false);
        }

        /// 功能：在 spawn 前执行连接级 terminal 数量与总 replay retention 预算门禁。
        ///
        /// 输入：新 terminal 已验证的 retentionBytes。
        /// 输出：当前 map 少于四项且声明总 retention 不超过 1 MiB 时成功。
        /// 不变量：检查发生在协议单线程 open 分派内；closed terminal 在完成 closed 发布后从 map 移除，不永久占用预算。
        /// 失败：任一容量耗尽返回 retryable `terminal_busy`，且不创建 PTY/子进程。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        async fn check_capacity(&self, requested_retention: usize) -> Result<(), AgentError> {
            let terminals = self.inner.terminals.lock().await;
            let used = terminals.values().fold(0_usize, |total, terminal| {
                total.saturating_add(terminal.limits.retention_bytes)
            });
            if terminals.len() >= MAX_TERMINALS
                || used.saturating_add(requested_retention) > MAX_TOTAL_RETENTION_BYTES
            {
                return Err(AgentError::new(
                    ErrorCode::RunConflict,
                    "terminal connection capacity is exhausted",
                )
                .retryable(true)
                .with_details(json!({"kind":"terminal_busy"})));
            }
            Ok(())
        }

        /// 功能：拒绝没有显式 fixture-only host policy 的 terminal 调用。
        ///
        /// 输入：当前管理器 capability 状态。
        /// 输出：启用时成功。
        /// 不变量：conformance 标志本身永不授权进程启动。
        /// 失败：未启用时返回 `-32601/method_not_found`。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        fn require_enabled(&self) -> Result<(), AgentError> {
            if self.enabled() {
                Ok(())
            } else {
                Err(AgentError::new(
                    ErrorCode::MethodNotFound,
                    "terminal methods are not enabled",
                ))
            }
        }

        /// 功能：按 terminal/session identity 查找当前连接内未关闭 PTY。
        ///
        /// 输入：已验证 opaque session 和 terminal ID。
        /// 输出：共享 terminal state。
        /// 不变量：跨 Session 查询与已关闭对象均表现为 not-found，避免泄露 ownership。
        /// 失败：不存在、跨 Session 或 closed 返回 `terminal_not_found`。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        async fn lookup(
            &self,
            session_id: &str,
            terminal_id: &str,
        ) -> Result<Arc<Terminal>, AgentError> {
            let terminal = self
                .inner
                .terminals
                .lock()
                .await
                .get(terminal_id)
                .cloned()
                .ok_or_else(terminal_not_found_error)?;
            let state = terminal.state.lock().await;
            if terminal.session_id != session_id || state.lifecycle == TerminalLifecycle::Closed {
                return Err(terminal_not_found_error());
            }
            drop(state);
            Ok(terminal)
        }

        /// 功能：响应已交付后启动 PTY reader/writer/wait/lifetime/idle 任务。
        ///
        /// 输入：open 阶段创建且仍归本连接所有的 terminal ID。
        /// 输出：五类任务均登记后返回。
        /// 不变量：reader 在此方法前不会产生任何 output；child/master/receiver 各只转移一次。
        /// 失败：状态缺失或重复激活返回内部错误，并由 daemon shutdown 兜底清理。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        async fn activate(&self, terminal_id: &str) -> Result<(), AgentError> {
            let terminal = self
                .inner
                .terminals
                .lock()
                .await
                .get(terminal_id)
                .cloned()
                .ok_or_else(terminal_not_found_error)?;
            let (master, child, tree, receiver, control_receiver) = {
                let mut state = terminal.state.lock().await;
                if state.active || state.lifecycle != TerminalLifecycle::Running {
                    return Err(terminal_io_error(
                        "terminal cannot be activated twice".to_owned(),
                    ));
                }
                if state.master.is_none()
                    || state.child.is_none()
                    || state.tree.is_none()
                    || state.input_receiver.is_none()
                {
                    return Err(terminal_io_error(
                        "terminal activation resources are incomplete".to_owned(),
                    ));
                }
                state.active = true;
                let master = state
                    .master
                    .as_ref()
                    .cloned()
                    .ok_or_else(terminal_not_found_error)?;
                let child = state
                    .child
                    .take()
                    .ok_or_else(|| terminal_io_error("terminal child is unavailable".to_owned()))?;
                let tree = state.tree.take().ok_or_else(|| {
                    terminal_io_error("terminal process-tree owner is unavailable".to_owned())
                })?;
                let receiver = state.input_receiver.take().ok_or_else(|| {
                    terminal_io_error("terminal input receiver is unavailable".to_owned())
                })?;
                let (control_sender, control_receiver) = mpsc::channel(8);
                state.control_sender = Some(control_sender);
                (master, child, tree, receiver, control_receiver)
            };
            let reader = tokio::spawn(read_terminal(
                self.clone(),
                terminal.clone(),
                master.clone(),
            ));
            let writer = tokio::spawn(write_terminal(terminal.clone(), master, receiver));
            let waiter = tokio::spawn(wait_terminal(
                self.clone(),
                terminal.clone(),
                child,
                tree,
                control_receiver,
            ));
            let lifetime = tokio::spawn(watch_lifetime(self.clone(), terminal.clone()));
            let idle = tokio::spawn(watch_idle(self.clone(), terminal.clone()));
            terminal
                .state
                .lock()
                .await
                .tasks
                .extend([reader, writer, waiter, lifetime, idle]);
            Ok(())
        }

        /// 功能：把一个 live/replay terminal notification 放入连接级有界发送队列。
        ///
        /// 输入：已完成 Schema 形状构造的 immutable notification。
        /// 输出：立即进入有界 channel 后返回，不等待慢 stdout consumer。
        /// 不变量：队列容量固定，且最坏 JSON escape 后的精确 frame 不超过 262144 字节；channel 满时 fail closed 而不阻塞 shutdown 或形成无界内存。
        /// 失败：frame 超限、channel 满/关闭、daemon shutdown 或 sender lock 中毒时返回内部错误。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        async fn emit(&self, frame: TerminalNotification) -> Result<(), AgentError> {
            let encoded = serde_json::to_vec(&frame).map_err(|error| {
                terminal_io_error(format!("cannot serialize terminal event: {error}"))
            })?;
            if encoded.len() > MAX_TERMINAL_EVENT_FRAME_BYTES {
                return Err(terminal_io_error(
                    "terminal event exceeds maxEventBytes".to_owned(),
                ));
            }
            let sender = self
                .inner
                .event_sender
                .lock()
                .map_err(|_| terminal_io_error("terminal event sender lock poisoned".to_owned()))?
                .clone()
                .ok_or_else(|| terminal_io_error("terminal event sender is closed".to_owned()))?;
            match sender.try_send(frame) {
                Ok(()) => Ok(()),
                Err(mpsc::error::TrySendError::Full(_)) => Err(terminal_io_error(
                    "terminal event channel is full".to_owned(),
                )),
                Err(mpsc::error::TrySendError::Closed(_)) => Err(terminal_io_error(
                    "terminal event receiver is closed".to_owned(),
                )),
            }
        }

        /// 功能：在 attach success response 之后以 bounded await 流式排队 replay，允许 replay 数量超过 channel 容量。
        ///
        /// 输入：已经在 wire barrier 下冻结、且曾作为 live output 验证过的 notification。
        /// 输出：完整 frame 在当前 replay action 的绝对 deadline 前进入连接 terminal event channel 后返回。
        /// 不变量：不使用 try_send 截断已承诺 replay；所有帧共享同一两秒总 deadline，after_response 持有 event gate 时 live 不能插队或令超时逐帧重置。
        /// 失败：frame 超限、sender 关闭/锁中毒或整段容量等待超时返回 terminal_io，调用方永久禁止后续伪完整生命周期事件。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        async fn emit_replay(
            &self,
            frame: TerminalNotification,
            deadline: time::Instant,
        ) -> Result<(), AgentError> {
            let encoded = serde_json::to_vec(&frame).map_err(|error| {
                terminal_io_error(format!("cannot serialize terminal replay event: {error}"))
            })?;
            if encoded.len() > MAX_TERMINAL_EVENT_FRAME_BYTES {
                return Err(terminal_io_error(
                    "terminal replay event exceeds maxEventBytes".to_owned(),
                ));
            }
            let sender = self
                .inner
                .event_sender
                .lock()
                .map_err(|_| terminal_io_error("terminal event sender lock poisoned".to_owned()))?
                .clone()
                .ok_or_else(|| terminal_io_error("terminal event sender is closed".to_owned()))?;
            match time::timeout_at(deadline, sender.send(frame)).await {
                Ok(Ok(())) => Ok(()),
                Ok(Err(_)) => Err(terminal_io_error(
                    "terminal event receiver is closed".to_owned(),
                )),
                Err(_) => Err(terminal_io_error(
                    "terminal replay enqueue timed out".to_owned(),
                )),
            }
        }

        /// 功能：在 per-terminal replay barrier 后分配并发送一个 live lifecycle event。
        ///
        /// 输入：terminal、frozen event type 与 Schema data。
        /// 输出：事件按严格 seq 进入连接有界队列后返回。
        /// 不变量：attach response 后的 replay 全部先于本事件；event gate 覆盖 seq 分配到 channel send，避免 live 插队；已发生 output delivery failure 时不再发送生命周期事件。
        /// 失败：barrier channel、历史 output delivery、seq、序列化、frame 上限或 event sink 失败时返回 terminal_io。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        async fn emit_ordered_event(
            &self,
            terminal: &Arc<Terminal>,
            event_type: &str,
            data: Value,
        ) -> Result<(), AgentError> {
            loop {
                if terminal.output_delivery_failed.load(Ordering::Acquire) {
                    return Err(terminal_io_error(
                        "terminal output delivery previously failed".to_owned(),
                    ));
                }
                let event_gate = terminal.event_gate.lock().await;
                if terminal.output_delivery_failed.load(Ordering::Acquire) {
                    return Err(terminal_io_error(
                        "terminal output delivery previously failed".to_owned(),
                    ));
                }
                if *terminal.replay_sender.borrow() {
                    let mut replay = terminal.replay_sender.subscribe();
                    drop(event_gate);
                    replay.wait_for(|blocked| !*blocked).await.map_err(|_| {
                        terminal_io_error("terminal replay barrier closed".to_owned())
                    })?;
                    continue;
                }
                let frame = {
                    let mut state = terminal.state.lock().await;
                    next_event(terminal, &mut state, event_type, data.clone())
                };
                let frame = match frame {
                    Ok(frame) => frame,
                    Err(error) => {
                        self.fail_delivery(terminal);
                        return Err(error);
                    }
                };
                if let Err(error) = self.emit(frame).await {
                    self.fail_delivery(terminal);
                    return Err(error);
                }
                drop(event_gate);
                return Ok(());
            }
        }

        /// 功能：增量解码 PTY bytes、保留 bounded output replay 并发布 truncation 元数据。
        ///
        /// 输入：目标 terminal、最多 eventBytes 的原始块和 EOF flush 标志。
        /// 输出：零或一条 output 以及可选 truncated 通知按 seq 顺序进入发送队列。
        /// 不变量：retained 原始字节账不超过 retentionBytes；无效 UTF-8 只替换展示字符；任一帧投递失败后永久停止该 terminal 的后续事件，不伪造 stdout/stderr。
        /// 失败：seq 耗尽或 event sink 关闭时设置 outputDeliveryFailed 并停止发布，不扩大保留窗口。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        async fn record_output(
            &self,
            terminal: &Arc<Terminal>,
            chunk: &[u8],
            final_chunk: bool,
        ) -> Result<(), AgentError> {
            loop {
                if terminal.output_delivery_failed.load(Ordering::Acquire) {
                    return Err(terminal_io_error(
                        "terminal output delivery previously failed".to_owned(),
                    ));
                }
                let event_gate = terminal.event_gate.lock().await;
                if terminal.output_delivery_failed.load(Ordering::Acquire) {
                    return Err(terminal_io_error(
                        "terminal output delivery previously failed".to_owned(),
                    ));
                }
                if *terminal.replay_sender.borrow() {
                    let mut replay = terminal.replay_sender.subscribe();
                    drop(event_gate);
                    replay.wait_for(|blocked| !*blocked).await.map_err(|_| {
                        terminal_io_error("terminal replay barrier closed".to_owned())
                    })?;
                    continue;
                }
                let frames = {
                    let mut state = terminal.state.lock().await;
                    if state.lifecycle == TerminalLifecycle::Closed {
                        return Ok(());
                    }
                    let (text, byte_count) =
                        decode_utf8(&mut state.decoder_pending, chunk, final_chunk);
                    if text.is_empty() || byte_count == 0 {
                        return Ok(());
                    }
                    let output = next_event(
                        terminal,
                        &mut state,
                        "terminal.output",
                        json!({"stream":"pty","data":text,"byteCount":byte_count}),
                    )?;
                    let output_seq = output.params.seq;
                    state.retained.push_back(RetainedOutput {
                        seq: output_seq,
                        frame: output.clone(),
                        byte_count,
                    });
                    state.retained_bytes = state.retained_bytes.saturating_add(byte_count);
                    state.last_activity = Instant::now();
                    let mut omitted = 0_usize;
                    while state.retained_bytes > terminal.limits.retention_bytes
                        || state.retained.len() > MAX_RETAINED_EVENTS_PER_TERMINAL
                    {
                        let Some(removed) = state.retained.pop_front() else {
                            break;
                        };
                        state.retained_bytes =
                            state.retained_bytes.saturating_sub(removed.byte_count);
                        omitted = omitted.saturating_add(removed.byte_count);
                    }
                    let mut frames = vec![output];
                    if omitted > 0 {
                        let retained_through =
                            state.retained.front().map_or(output_seq, |item| item.seq);
                        frames.push(next_event(
                            terminal,
                            &mut state,
                            "terminal.truncated",
                            json!({
                                "omittedBytes":omitted,
                                "retainedThroughSeq":retained_through
                            }),
                        )?);
                    }
                    frames
                };
                for frame in frames {
                    if let Err(error) = self.emit(frame).await {
                        self.fail_delivery(terminal);
                        return Err(error);
                    }
                }
                drop(event_gate);
                return Ok(());
            }
        }

        /// 功能：幂等执行整树清理、任务/fd 回收并发布唯一 terminal.closed。
        ///
        /// 输入：terminal ID 与 Schema 允许的 client/daemon_shutdown/idle_timeout/lifetime reason。
        /// 输出：terminal 到达 closed 且所有已登记任务被回收后返回。
        /// 不变量：cleanup_started CAS 保证所有主动终止原因只由一个独立执行者清理；inactive child 的 TERM/KILL/reap 有界；只有 exited 已成功排队且 output 完整时才发布最终 closed。
        /// 失败：OS 竞态和单个任务 panic 被安全吸收；event sink 失败不会复活进程或 attachment。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        async fn close_terminal(&self, terminal_id: &str, reason: &'static str) {
            let Some(terminal) = self.inner.terminals.lock().await.get(terminal_id).cloned() else {
                return;
            };
            let execute = {
                let mut state = terminal.state.lock().await;
                if state.lifecycle == TerminalLifecycle::Closed {
                    return;
                }
                state.closing = true;
                terminal
                    .cleanup_started
                    .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
                    .is_ok()
            };
            if !execute {
                wait_until_closed(&terminal).await;
                return;
            }

            let (active, control, mut tree, mut inactive_child, lifecycle) = {
                let mut state = terminal.state.lock().await;
                if state.active {
                    (
                        true,
                        state.control_sender.as_ref().cloned(),
                        None,
                        None,
                        state.lifecycle,
                    )
                } else {
                    (
                        false,
                        None,
                        state.tree.take(),
                        state.child.take(),
                        state.lifecycle,
                    )
                }
            };
            if active {
                if lifecycle == TerminalLifecycle::Running
                    && let Some(control) = control
                {
                    let _ = control.send(TerminalControl::Terminate).await;
                }
                wait_until_waiter(&terminal).await;
            } else {
                if lifecycle == TerminalLifecycle::Running {
                    if let Some(tree) = tree.as_mut() {
                        tree.terminate_gracefully(TERMINATION_GRACE).await;
                    } else if let Some(child) = inactive_child.as_mut() {
                        terminate_inactive_child(child).await;
                    }
                }
                if let Some(child) = inactive_child.as_mut() {
                    let _ = child.start_kill();
                    if time::timeout(CHILD_REAP_TIMEOUT, child.wait())
                        .await
                        .is_err()
                    {
                        let _ = child.start_kill();
                    }
                }
            }

            let tasks = {
                let mut state = terminal.state.lock().await;
                state.input_sender.take();
                state.control_sender.take();
                state.master.take();
                state.attachment_id = None;
                let tasks = std::mem::take(&mut state.tasks);
                state.lifecycle = TerminalLifecycle::Closed;
                tasks
            };
            for task in &tasks {
                task.abort();
            }
            for task in tasks {
                let _ = task.await;
            }
            if terminal.exit_done.load(Ordering::Acquire)
                && !terminal.output_delivery_failed.load(Ordering::Acquire)
            {
                let _ = self
                    .emit_ordered_event(&terminal, "terminal.closed", json!({"reason":reason}))
                    .await;
            }
            let mut terminals = self.inner.terminals.lock().await;
            if terminals
                .get(terminal_id)
                .is_some_and(|stored| Arc::ptr_eq(stored, &terminal))
            {
                terminals.remove(terminal_id);
            }
            drop(terminals);
            terminal.closed_done.store(true, Ordering::Release);
            terminal.closed_notify.notify_waiters();
        }
    }

    /// 功能：从 JSON Value 严格反序列化 terminal 方法 DTO。
    ///
    /// 输入：尚未产生副作用的 params 值。
    /// 输出：deny-unknown-fields 的具体 DTO。
    /// 不变量：错误发生在任何进程、signal 或 fd mutation 前。
    /// 失败：类型、缺失或未知字段返回 `-32602/invalid_params`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn parse_params<T: serde::de::DeserializeOwned>(value: Value) -> Result<T, AgentError> {
        serde_json::from_value(value)
            .map_err(|error| invalid_terminal_params(format!("invalid terminal params: {error}")))
    }

    /// 功能：将 open 参数收窄为 frozen helper 的 hash-bound fixture-only 权限。
    ///
    /// 输入：canonical workspace 与严格反序列化的 open DTO。
    /// 输出：runner parent 解释器 fd、sealed helper snapshot、尺寸与 limits。
    /// 不变量：空 environment、`.` cwd、固定 argv、`disconnectPolicy=terminate`、parent executable identity 和冻结 SHA-256 必须全部匹配。
    /// 失败：字段漂移返回 invalid/permission_denied；I/O 身份或 hash 不符一律 fail closed。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn approve_fixture_open(
        workspace: &Path,
        params: OpenParams,
    ) -> Result<ApprovedOpen, AgentError> {
        if !is_opaque_id(&params.session_id) {
            return Err(invalid_terminal_params("invalid terminal session ID"));
        }
        if params.args != ["executor_helper.py", "terminal"]
            || params.cwd != "."
            || !params.environment.is_empty()
            || !params.extensions.is_empty()
        {
            return Err(permission_denied_error(
                "terminal operation is outside the fixture-only policy",
            ));
        }
        if params.disconnect_policy != "terminate" {
            return Err(permission_denied_error(
                "terminal disconnect policy is not authorized",
            ));
        }
        let (columns, rows) = validate_size(&params.size)?;
        let limits = validate_limits(&params.limits)?;
        let interpreter = validate_interpreter(workspace, &params.executable)?;
        let helper = open_verified_helper(workspace)?;
        Ok(ApprovedOpen {
            session_id: params.session_id,
            interpreter,
            helper,
            columns,
            rows,
            limits,
        })
    }

    /// 功能：验证 terminal columns/rows 并转换为 PTY ioctl 使用的 u16。
    ///
    /// 输入：反序列化的无符号尺寸。
    /// 输出：1..1000 的 columns/rows。
    /// 不变量：转换后可无损进入 libc winsize。
    /// 失败：越界返回 invalid_params。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn validate_size(size: &SizeParams) -> Result<(u16, u16), AgentError> {
        if !(1..=1000).contains(&size.columns) || !(1..=1000).contains(&size.rows) {
            return Err(invalid_terminal_params("terminal size is outside 1..1000"));
        }
        Ok((
            u16::try_from(size.columns)
                .map_err(|_| invalid_terminal_params("terminal columns overflow"))?,
            u16::try_from(size.rows)
                .map_err(|_| invalid_terminal_params("terminal rows overflow"))?,
        ))
    }

    /// 功能：应用 fixture-only 的 lifetime/idle/replay/input/event 硬上限。
    ///
    /// 输入：完整 frozen limit DTO。
    /// 输出：可直接用于任务和内存预算的本机限制。
    /// 不变量：所有 usize 转换有界且 retention/input 不超过 1 MiB，event 原始块不超过 32 KiB，使最坏 JSON escaping 仍低于 maxEventBytes。
    /// 失败：任一限制漂移或平台不可表示时返回 invalid_params。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn validate_limits(limits: &LimitParams) -> Result<TerminalLimits, AgentError> {
        if !(100..=60_000).contains(&limits.lifetime_ms)
            || !(100..=60_000).contains(&limits.idle_timeout_ms)
            || !(1024..=1_048_576).contains(&limits.retention_bytes)
            || !(1024..=1_048_576).contains(&limits.input_buffer_bytes)
            || !(256..=MAX_TERMINAL_EVENT_RAW_BYTES).contains(&limits.event_bytes)
        {
            return Err(invalid_terminal_params(
                "terminal limits are outside the fixture-only bounds",
            ));
        }
        Ok(TerminalLimits {
            lifetime: Duration::from_millis(limits.lifetime_ms),
            idle_timeout: Duration::from_millis(limits.idle_timeout_ms),
            retention_bytes: usize::try_from(limits.retention_bytes)
                .map_err(|_| invalid_terminal_params("retentionBytes overflow"))?,
            input_buffer_bytes: usize::try_from(limits.input_buffer_bytes)
                .map_err(|_| invalid_terminal_params("inputBufferBytes overflow"))?,
            event_bytes: usize::try_from(limits.event_bytes)
                .map_err(|_| invalid_terminal_params("eventBytes overflow"))?,
        })
    }

    #[cfg(target_os = "linux")]
    /// 功能：把请求 executable 绑定到 Linux runner parent 的已打开 `/proc/<ppid>/exe` 身份。
    ///
    /// 输入：canonical workspace 和未经信任的 executable 文本。
    /// 输出：保持 CLOEXEC 到 pre_exec 的 parent executable File；child 将通过自身 `/proc/self/fd/N` 执行它。
    /// 不变量：不信任 PATH；请求真实文件、parent link 和已打开 fd 的 device/inode 必须一致，且 parent basename 是受限 Python 名称、目标不在 workspace。
    /// 失败：parent 非 Python、PID/link/metadata 漂移、路径替换或文件不可读时返回 permission_denied。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn validate_interpreter(workspace: &Path, executable: &str) -> Result<File, AgentError> {
        if executable.is_empty() || executable.len() > 4096 || executable.contains('\0') {
            return Err(permission_denied_error("terminal executable is denied"));
        }
        let requested = std::fs::canonicalize(executable)
            .map_err(|_| permission_denied_error("terminal executable is denied"))?;
        if requested.starts_with(workspace)
            || !requested.is_file()
            || !looks_like_python(&requested)
        {
            return Err(permission_denied_error("terminal executable is denied"));
        }
        let parent_pid = nix::unistd::getppid().as_raw();
        if parent_pid <= 1 {
            return Err(permission_denied_error("terminal executable is denied"));
        }
        let parent_link = PathBuf::from(format!("/proc/{parent_pid}/exe"));
        let parent_target = std::fs::read_link(&parent_link)
            .map_err(|_| permission_denied_error("terminal executable is denied"))?;
        if !looks_like_python(&parent_target) {
            return Err(permission_denied_error("terminal executable is denied"));
        }
        let parent = OpenOptions::new()
            .read(true)
            .custom_flags(libc::O_CLOEXEC)
            .open(&parent_link)
            .map_err(|_| permission_denied_error("terminal executable is denied"))?;
        let requested_metadata = std::fs::metadata(&requested)
            .map_err(|_| permission_denied_error("terminal executable is denied"))?;
        let parent_metadata = parent
            .metadata()
            .map_err(|_| permission_denied_error("terminal executable is denied"))?;
        if !parent_metadata.file_type().is_file()
            || requested_metadata.dev() != parent_metadata.dev()
            || requested_metadata.ino() != parent_metadata.ino()
            || requested_metadata.len() != parent_metadata.len()
        {
            return Err(permission_denied_error("terminal executable is denied"));
        }
        Ok(parent)
    }

    #[cfg(not(target_os = "linux"))]
    /// 功能：在没有 Linux `/proc` parent executable identity 的 Unix 平台拒绝 fixture terminal。
    ///
    /// 输入：workspace 与 executable，仅为保持跨平台接口。
    /// 输出：无成功值。
    /// 不变量：不回退到 PATH、basename 或普通 canonical path 信任，不外推 macOS terminal 能力。
    /// 失败：固定返回 permission_denied。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn validate_interpreter(_workspace: &Path, _executable: &str) -> Result<File, AgentError> {
        Err(permission_denied_error("terminal executable is denied"))
    }

    /// 功能：检查可执行文件 basename 是否为 python 加数字/点版本后缀。
    ///
    /// 输入：canonical 或只读候选路径。
    /// 输出：`python`、`python3`、`python3.14` 等返回 true。
    /// 不变量：拒绝连字符、路径注入和无关解释器名称。
    /// 失败：非 UTF-8 basename 返回 false。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn looks_like_python(path: &Path) -> bool {
        path.file_name()
            .and_then(|name| name.to_str())
            .and_then(|name| name.strip_prefix("python"))
            .is_some_and(|suffix| {
                suffix
                    .chars()
                    .all(|character| character.is_ascii_digit() || character == '.')
            })
    }

    #[cfg(target_os = "linux")]
    /// 功能：O_NOFOLLOW 读取冻结 helper，验证 SHA-256 后复制到完整 sealing 的匿名 memfd snapshot。
    ///
    /// 输入：canonical workspace。
    /// 输出：cursor 归零、CLOEXEC 且带 WRITE/GROW/SHRINK/SEAL 四项 seal 的 immutable helper File。
    /// 不变量：child 只读取 sealed snapshot，不再解析或依赖可被同 inode 原地改写的 workspace 文件；源最多 1 MiB 且 hash 必须精确冻结。
    /// 失败：symlink/类型/大小/identity/hash/memfd/write/seal 任一步失败均返回 permission_denied。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn open_verified_helper(workspace: &Path) -> Result<File, AgentError> {
        let path = workspace.join("executor_helper.py");
        let before = std::fs::symlink_metadata(&path)
            .map_err(|_| permission_denied_error("terminal helper is denied"))?;
        if before.file_type().is_symlink()
            || !before.file_type().is_file()
            || before.len() == 0
            || before.len() > MAX_HELPER_BYTES
        {
            return Err(permission_denied_error("terminal helper is denied"));
        }
        let mut file = OpenOptions::new()
            .read(true)
            .custom_flags(libc::O_NOFOLLOW | libc::O_CLOEXEC)
            .open(&path)
            .map_err(|_| permission_denied_error("terminal helper is denied"))?;
        let opened = file
            .metadata()
            .map_err(|_| permission_denied_error("terminal helper is denied"))?;
        if !opened.file_type().is_file()
            || opened.dev() != before.dev()
            || opened.ino() != before.ino()
            || opened.len() != before.len()
        {
            return Err(permission_denied_error("terminal helper is denied"));
        }
        let mut digest = Sha256::new();
        let mut source = Vec::with_capacity(usize::try_from(opened.len()).unwrap_or(0));
        let mut total = 0_u64;
        let mut buffer = [0_u8; 8192];
        loop {
            let count = file
                .read(&mut buffer)
                .map_err(|_| permission_denied_error("terminal helper is denied"))?;
            if count == 0 {
                break;
            }
            total = total.saturating_add(u64::try_from(count).unwrap_or(u64::MAX));
            if total > MAX_HELPER_BYTES {
                return Err(permission_denied_error("terminal helper is denied"));
            }
            digest.update(&buffer[..count]);
            source.extend_from_slice(&buffer[..count]);
        }
        let after = file
            .metadata()
            .map_err(|_| permission_denied_error("terminal helper is denied"))?;
        if total != opened.len()
            || after.dev() != opened.dev()
            || after.ino() != opened.ino()
            || after.len() != opened.len()
            || format!("{:x}", digest.finalize()) != FIXTURE_HELPER_SHA256
        {
            return Err(permission_denied_error("terminal helper is denied"));
        }
        let owned = memfd_create(
            "qxnm-forge-terminal-helper",
            MFdFlags::MFD_CLOEXEC | MFdFlags::MFD_ALLOW_SEALING,
        )
        .map_err(|_| permission_denied_error("terminal helper snapshot is denied"))?;
        let mut snapshot = File::from(owned);
        snapshot
            .write_all(&source)
            .map_err(|_| permission_denied_error("terminal helper snapshot is denied"))?;
        snapshot
            .flush()
            .map_err(|_| permission_denied_error("terminal helper snapshot is denied"))?;
        let seals = SealFlag::F_SEAL_WRITE
            | SealFlag::F_SEAL_GROW
            | SealFlag::F_SEAL_SHRINK
            | SealFlag::F_SEAL_SEAL;
        fcntl(&snapshot, FcntlArg::F_ADD_SEALS(seals))
            .map_err(|_| permission_denied_error("terminal helper snapshot is denied"))?;
        let applied = fcntl(&snapshot, FcntlArg::F_GET_SEALS)
            .map_err(|_| permission_denied_error("terminal helper snapshot is denied"))?;
        if applied & seals.bits() != seals.bits() {
            return Err(permission_denied_error(
                "terminal helper snapshot is denied",
            ));
        }
        snapshot
            .seek(SeekFrom::Start(0))
            .map_err(|_| permission_denied_error("terminal helper snapshot is denied"))?;
        Ok(snapshot)
    }

    #[cfg(not(target_os = "linux"))]
    /// 功能：在没有 Linux sealed memfd 执行对象的平台拒绝 helper 授权。
    ///
    /// 输入：canonical workspace，仅为保持跨平台接口。
    /// 输出：无成功值。
    /// 不变量：不退化为可变普通 fd、临时路径或 pipe。
    /// 失败：固定返回 permission_denied。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn open_verified_helper(_workspace: &Path) -> Result<File, AgentError> {
        Err(permission_denied_error("terminal helper is denied"))
    }

    /// 功能：为 child 生成指向已验证继承 fd 的只读脚本路径。
    ///
    /// 输入：仍由 parent 持有的正 helper fd。
    /// 输出：Linux `/proc/self/fd/N` 或其他 Unix `/dev/fd/N` 参数。
    /// 不变量：child 读取的是已验证打开对象而不是重新解析 workspace 名称。
    /// 失败：fd 非正或宿主无 fd filesystem 时返回 process_start_failed。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn inherited_fd_path(fd: RawFd) -> Result<PathBuf, AgentError> {
        if fd < 0 {
            return Err(process_start_error("invalid helper fd".to_owned()));
        }
        #[cfg(target_os = "linux")]
        let path = PathBuf::from(format!("/proc/self/fd/{fd}"));
        #[cfg(not(target_os = "linux"))]
        let path = PathBuf::from(format!("/dev/fd/{fd}"));
        if path.parent().is_some_and(Path::is_dir) {
            Ok(path)
        } else {
            Err(process_start_error(
                "platform fd filesystem is unavailable".to_owned(),
            ))
        }
    }

    /// 功能：为 PTY child 安装 async-signal-safe setsid/TIOCSCTTY 与 helper-fd 继承步骤。
    ///
    /// 输入：尚未 spawn 的 Command、runner parent interpreter fd 与 sealed helper fd。
    /// 输出：child 在 exec 前成为新 Session/进程组组长并取得 fd 0 controlling TTY。
    /// 不变量：closure 只调用 setsid/ioctl/fcntl，不分配、不访问环境；两个已验证 fd 仅在 child 清除 CLOEXEC。
    /// 失败：任一步系统调用失败使 spawn 返回 process_start_failed，绝不退化为 pipe。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[allow(unsafe_code)]
    fn configure_pty_child(command: &mut Command, interpreter_fd: RawFd, helper_fd: RawFd) {
        // SAFETY: pre_exec 中只执行 POSIX async-signal-safe 系统调用；所有捕获值均为 Copy fd。
        unsafe {
            command.pre_exec(move || {
                if libc::setsid() == -1 {
                    return Err(std::io::Error::last_os_error());
                }
                if libc::ioctl(libc::STDIN_FILENO, libc::TIOCSCTTY as _, 0) == -1 {
                    return Err(std::io::Error::last_os_error());
                }
                for fd in [interpreter_fd, helper_fd] {
                    let flags = libc::fcntl(fd, libc::F_GETFD);
                    if flags == -1
                        || libc::fcntl(fd, libc::F_SETFD, flags & !libc::FD_CLOEXEC) == -1
                    {
                        return Err(std::io::Error::last_os_error());
                    }
                }
                Ok(())
            });
        }
    }

    #[cfg(target_os = "linux")]
    /// 功能：用原子 O_CLOEXEC 系统调用创建 Linux PTY master/slave，并在交给任何并发 spawn 前设置窗口尺寸。
    ///
    /// 输入：已验证的 winsize。
    /// 输出：master 为 O_NONBLOCK|O_CLOEXEC，slave 为 O_CLOEXEC 的两个 OwnedFd。
    /// 不变量：master 由 posix_openpt(O_CLOEXEC) 原子创建，slave 由 open(O_CLOEXEC|O_NOFOLLOW) 原子创建；不存在 post-open fcntl 的 fork/exec 泄漏窗口。
    /// 失败：grantpt/unlockpt/ptsname_r/open/ioctl 任一步失败均关闭已拥有 fd 并返回 std I/O error。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn open_cloexec_pty(winsize: &Winsize) -> std::io::Result<(OwnedFd, OwnedFd)> {
        let master =
            posix_openpt(OFlag::O_RDWR | OFlag::O_NOCTTY | OFlag::O_CLOEXEC | OFlag::O_NONBLOCK)
                .map_err(errno_to_io)?;
        grantpt(&master).map_err(errno_to_io)?;
        unlockpt(&master).map_err(errno_to_io)?;
        let slave_name = ptsname_r(&master).map_err(errno_to_io)?;
        let slave = open(
            Path::new(&slave_name),
            OFlag::O_RDWR | OFlag::O_NOCTTY | OFlag::O_CLOEXEC | OFlag::O_NOFOLLOW,
            Mode::empty(),
        )
        .map_err(errno_to_io)?;
        set_pty_size(slave.as_raw_fd(), winsize.ws_col, winsize.ws_row)?;
        Ok((master.into(), slave))
    }

    #[cfg(not(target_os = "linux"))]
    /// 功能：在非 Linux Unix 上拒绝缺少原子 CLOEXEC/thread-safe ptsname 证明的 fixture PTY 创建。
    ///
    /// 输入：已验证 winsize，仅用于统一接口。
    /// 输出：无成功值。
    /// 不变量：不回退到 libc openpty 或 post-open FD_CLOEXEC，不外推 macOS terminal capability。
    /// 失败：固定返回 Unsupported I/O error。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn open_cloexec_pty(_winsize: &Winsize) -> std::io::Result<(OwnedFd, OwnedFd)> {
        Err(std::io::Error::new(
            std::io::ErrorKind::Unsupported,
            "atomic CLOEXEC PTY creation is unavailable",
        ))
    }

    /// 功能：用原生 TIOCSWINSZ 原子设置 PTY 行列尺寸。
    ///
    /// 输入：仍打开的 master fd 和 1..1000 columns/rows。
    /// 输出：ioctl 成功后返回。
    /// 不变量：winsize 像素字段固定为零；调用方已验证范围。
    /// 失败：系统 ioctl 失败返回最后一个 OS error。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[allow(unsafe_code)]
    fn set_pty_size(fd: RawFd, columns: u16, rows: u16) -> std::io::Result<()> {
        let size = libc::winsize {
            ws_row: rows,
            ws_col: columns,
            ws_xpixel: 0,
            ws_ypixel: 0,
        };
        // SAFETY: fd 由当前 Terminal 持有，指针指向完整初始化且调用期间存活的 winsize。
        if unsafe { libc::ioctl(fd, libc::TIOCSWINSZ as _, &size) } == -1 {
            Err(std::io::Error::last_os_error())
        } else {
            Ok(())
        }
    }

    /// 功能：持续从 nonblocking PTY 读取 eventBytes 分片并交给 bounded replay 管线。
    ///
    /// 输入：管理器、terminal 与共享 master AsyncFd。
    /// 输出：EOF/EIO 后 flush UTF-8 尾部并通知 waiter。
    /// 不变量：单次分配不超过 eventBytes；每次 read 先扣除 decoder pending，保证合并后的单事件 byteCount 仍不超过 eventBytes；读取错误不会产生伪输出。
    /// 失败：非 EOF/EIO 的 readiness/read 错误或任一 output enqueue 失败都会设置不可逆投递失败标志并终止 reader；最终仍通知 wait/cleanup。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn read_terminal(
        manager: TerminalManager,
        terminal: Arc<Terminal>,
        master: Arc<AsyncFd<OwnedFd>>,
    ) {
        let mut buffer = vec![0_u8; terminal.limits.event_bytes];
        let mut filled = 0_usize;
        let mut read_budget = 0_usize;
        'read: loop {
            if filled == 0 {
                let pending_bytes = terminal.state.lock().await.decoder_pending.len();
                let Some(next_budget) =
                    terminal_read_budget(terminal.limits.event_bytes, pending_bytes)
                else {
                    manager.fail_delivery(&terminal);
                    break;
                };
                read_budget = next_budget;
            }
            let readiness_result = if filled == 0 {
                master.readable().await
            } else {
                match time::timeout(OUTPUT_COALESCE_WINDOW, master.readable()).await {
                    Ok(result) => result,
                    Err(_) => {
                        if !deliver_reader_output(&manager, &terminal, &buffer[..filled], false)
                            .await
                        {
                            break;
                        }
                        filled = 0;
                        continue;
                    }
                }
            };
            let mut readiness = match readiness_result {
                Ok(readiness) => readiness,
                Err(_) => {
                    manager.fail_delivery(&terminal);
                    break;
                }
            };
            let result = readiness.try_io(|inner| {
                nix::unistd::read(inner.get_ref(), &mut buffer[filled..read_budget])
                    .map_err(errno_to_io)
            });
            match result {
                Err(_) => continue,
                Ok(Ok(0)) => {
                    if filled > 0 {
                        let _ =
                            deliver_reader_output(&manager, &terminal, &buffer[..filled], false)
                                .await;
                    }
                    break;
                }
                Ok(Ok(count)) => {
                    filled = filled.saturating_add(count);
                    if filled == read_budget {
                        if !deliver_reader_output(&manager, &terminal, &buffer[..filled], false)
                            .await
                        {
                            break 'read;
                        }
                        filled = 0;
                    }
                }
                Ok(Err(error)) if error.raw_os_error() == Some(libc::EIO) => {
                    if filled > 0 {
                        let _ =
                            deliver_reader_output(&manager, &terminal, &buffer[..filled], false)
                                .await;
                    }
                    break;
                }
                Ok(Err(_)) => {
                    manager.fail_delivery(&terminal);
                    break;
                }
            }
        }
        let _ = deliver_reader_output(&manager, &terminal, &[], true).await;
        let delivery_failed = terminal.output_delivery_failed.load(Ordering::Acquire);
        terminal.reader_done.store(true, Ordering::Release);
        terminal.reader_notify.notify_waiters();
        if delivery_failed {
            let terminal_id = terminal.terminal_id.clone();
            tokio::spawn(async move {
                manager
                    .close_terminal(&terminal_id, "daemon_shutdown")
                    .await;
            });
        }
    }

    /// 功能：把 reader 聚合的一块 PTY bytes 交给严格排序事件管线，并统一记录不可恢复投递失败。
    ///
    /// 输入：terminal 管理器、目标 terminal、受 eventBytes 约束的块和 EOF decoder flush 标志。
    /// 输出：完整记录并排队时返回 true；任一步失败时返回 false。
    /// 不变量：失败标志单调从 false 变为 true；失败后 waiter 不会发布 exited/closed 伪装完整输出。
    /// 失败：decoder、seq、replay barrier、序列化、frame 上限或有界 event channel 失败均 fail closed。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn deliver_reader_output(
        manager: &TerminalManager,
        terminal: &Arc<Terminal>,
        chunk: &[u8],
        final_chunk: bool,
    ) -> bool {
        if manager
            .record_output(terminal, chunk, final_chunk)
            .await
            .is_ok()
        {
            true
        } else {
            manager.fail_delivery(terminal);
            false
        }
    }

    /// 功能：按 inputSeq acceptance 顺序完整写入 nonblocking PTY 并归还字节预算。
    ///
    /// 输入：terminal、共享 master 与单消费者 input receiver。
    /// 输出：sender 关闭或 PTY 写失败后返回。
    /// 不变量：EAGAIN 时等待 writable；一个已接受块不会只写前缀后静默丢弃。
    /// 失败：fd 错误终止 writer；pending 账在每块结束时饱和扣减。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn write_terminal(
        terminal: Arc<Terminal>,
        master: Arc<AsyncFd<OwnedFd>>,
        mut receiver: mpsc::Receiver<Vec<u8>>,
    ) {
        while let Some(data) = receiver.recv().await {
            let result = write_all_pty(&master, &data).await;
            let mut state = terminal.state.lock().await;
            state.pending_input_bytes = state.pending_input_bytes.saturating_sub(data.len());
            drop(state);
            if result.is_err() {
                break;
            }
        }
    }

    /// 功能：把一个已接受输入块完整写入 AsyncFd PTY。
    ///
    /// 输入：nonblocking master 与 bounded 字节切片。
    /// 输出：全部字节写入后返回。
    /// 不变量：WouldBlock 清 readiness 后重试；零字节写视为错误。
    /// 失败：readiness、write 或 WriteZero 返回 std I/O error。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn write_all_pty(master: &AsyncFd<OwnedFd>, data: &[u8]) -> std::io::Result<()> {
        let mut offset = 0;
        while offset < data.len() {
            let mut readiness = master.writable().await?;
            let result = readiness.try_io(|inner| {
                nix::unistd::write(inner.get_ref(), &data[offset..]).map_err(errno_to_io)
            });
            match result {
                Err(_) => continue,
                Ok(Ok(0)) => {
                    return Err(std::io::Error::new(
                        std::io::ErrorKind::WriteZero,
                        "PTY write returned zero",
                    ));
                }
                Ok(Ok(count)) => offset = offset.saturating_add(count),
                Ok(Err(error)) => return Err(error),
            }
        }
        Ok(())
    }

    /// 功能：唯一拥有 active Child/ProcessTreeGuard，串行处理 signal/terminate、wait、整树封闭、尾输出与 exited 发布。
    ///
    /// 输入：管理器、terminal、唯一 Child/tree ownership 与有界 control receiver。
    /// 输出：child 成功 wait 时在全部 PTY output 之后发布 exited；任意返回均发布 waiterDone。
    /// 不变量：自然退出后立即 cleanup tracked tree，再在一秒上限内等待 reader EOF；主动关闭固定 TERM→200ms grace→KILL 并最多等待一秒 reap；任何 signal 都经 root identity 重验，不使用旧 PID/PGID fallback。
    /// 失败：wait error 保持 lifecycle 非 Exited、绝不设置 exitDone；尾 output 投递失败或 drain 超时时不发布 exited；event/control 错误最终仍释放唯一 tree ownership。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn wait_terminal(
        manager: TerminalManager,
        terminal: Arc<Terminal>,
        mut child: Child,
        mut tree: ProcessTreeGuard,
        mut controls: mpsc::Receiver<TerminalControl>,
    ) {
        let _completion = WaiterCompletion {
            terminal: terminal.clone(),
        };
        let status = loop {
            tokio::select! {
                biased;
                status = child.wait() => break status,
                control = controls.recv() => match control {
                    Some(TerminalControl::Signal { signal, response }) => {
                        let result = tree
                            .signal_group_checked(signal)
                            .map_err(|error| {
                                terminal_io_error(format!(
                                    "cannot signal terminal process group: {error}"
                                ))
                            })
                            .and_then(|delivered| {
                                if delivered {
                                    Ok(())
                                } else {
                                    Err(terminal_not_running_error())
                                }
                            });
                        let _ = response.send(result);
                    }
                    Some(TerminalControl::Terminate) | None => {
                        tree.terminate_gracefully(TERMINATION_GRACE).await;
                        let _ = child.start_kill();
                        break match time::timeout(CHILD_REAP_TIMEOUT, child.wait()).await {
                            Ok(status) => status,
                            Err(_) => Err(std::io::Error::new(
                                std::io::ErrorKind::TimedOut,
                                "terminal child reap timed out",
                            )),
                        };
                    }
                }
            }
        };

        tree.cleanup().await;
        let status = match status {
            Ok(status) => {
                let mut state = terminal.state.lock().await;
                if state.lifecycle != TerminalLifecycle::Closed {
                    state.lifecycle = TerminalLifecycle::Exited;
                }
                Some(status)
            }
            Err(_) => None,
        };

        let reader_drained = time::timeout(OUTPUT_DRAIN_TIMEOUT, async {
            while !terminal.reader_done.load(Ordering::Acquire) {
                let notified = terminal.reader_notify.notified();
                if terminal.reader_done.load(Ordering::Acquire) {
                    break;
                }
                notified.await;
            }
        })
        .await
        .is_ok();
        if !reader_drained {
            manager.fail_delivery(&terminal);
        }

        if let Some(status) = status
            && !terminal.output_delivery_failed.load(Ordering::Acquire)
        {
            let (exit_code, signal, reason) = terminal_exit_status(status);
            if manager
                .emit_ordered_event(
                    &terminal,
                    "terminal.exited",
                    json!({
                        "exitCode":exit_code,
                        "signal":signal,
                        "terminationReason":reason
                    }),
                )
                .await
                .is_ok()
            {
                terminal.exit_done.store(true, Ordering::Release);
            }
        }
    }

    /// 功能：在绝对 lifetime 到期后从独立清理任务关闭 PTY。
    ///
    /// 输入：管理器与 terminal 的固定 lifetime。
    /// 输出：到期或 watcher 被 abort 后返回。
    /// 不变量：retain 不能让 terminal 超过 lifetime；清理不在 watcher 自身 task 内回收自身 handle。
    /// 失败：并发 client/shutdown close 由幂等路径合并。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn watch_lifetime(manager: TerminalManager, terminal: Arc<Terminal>) {
        time::sleep(terminal.limits.lifetime).await;
        let terminal_id = terminal.terminal_id.clone();
        tokio::spawn(async move {
            manager.close_terminal(&terminal_id, "lifetime").await;
        });
    }

    /// 功能：定期检查无输入/输出/resize/signal 活动的 idle timeout 并统一关闭。
    ///
    /// 输入：管理器、terminal 与固定 idle timeout。
    /// 输出：closed、到期或 watcher 被 abort 后返回。
    /// 不变量：轮询间隔有 50..500ms 界；检查不延长 last_activity。
    /// 失败：并发 close 通过 closing/closed Notify 合并。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn watch_idle(manager: TerminalManager, terminal: Arc<Terminal>) {
        let interval = terminal
            .limits
            .idle_timeout
            .div_f64(4.0)
            .clamp(Duration::from_millis(50), Duration::from_millis(500));
        loop {
            time::sleep(interval).await;
            let expired = {
                let state = terminal.state.lock().await;
                if state.lifecycle == TerminalLifecycle::Closed {
                    return;
                }
                state.last_activity.elapsed() >= terminal.limits.idle_timeout
            };
            if expired {
                let terminal_id = terminal.terminal_id.clone();
                tokio::spawn(async move {
                    manager.close_terminal(&terminal_id, "idle_timeout").await;
                });
                return;
            }
        }
    }

    /// 功能：等待 active waiter 完成 tree cleanup、reader EOF 和可用的 exited 发布屏障。
    ///
    /// 输入：正在统一清理的 terminal。
    /// 输出：waiter 正常、wait error 或 panic 释放唯一 ownership 后返回。
    /// 不变量：无超时抢跑和 PID/PGID fallback；Notify 前后双检避免 missed notification。
    /// 失败：本函数不返回错误，wait error 由 waiterDone 与未设置 exitDone 的组合表达。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn wait_until_waiter(terminal: &Arc<Terminal>) {
        while !terminal.waiter_done.load(Ordering::Acquire) {
            let notified = terminal.waiter_notify.notified();
            if terminal.waiter_done.load(Ordering::Acquire) {
                break;
            }
            let _ = time::timeout(Duration::from_millis(100), notified).await;
        }
    }

    /// 功能：让并发 close 调用等待唯一清理执行者发布 closed。
    ///
    /// 输入：closing terminal。
    /// 输出：唯一清理执行者完成 closed 发布后返回。
    /// 不变量：周期性重检只用于防 missed notification，绝不超时抢跑；本函数不重复 signal 或关闭 fd。
    /// 失败：本函数不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn wait_until_closed(terminal: &Arc<Terminal>) {
        while !terminal.closed_done.load(Ordering::Acquire) {
            let notified = terminal.closed_notify.notified();
            if terminal.closed_done.load(Ordering::Acquire) {
                break;
            }
            let _ = time::timeout(Duration::from_millis(100), notified).await;
        }
    }

    /// 功能：等待 attach gate 之前已排队的全部 terminal event 真正写入并 flush 到 daemon stdout。
    ///
    /// 输入：目标 terminal 与 event gate 下截取的全事件 seq 水位。
    /// 输出：written 水位达到 through seq 后返回。
    /// 不变量：只等待 daemon 在成功 flush 后确认的水位，不能以 mpsc dequeue/enqueue 冒充 wire delivery；等待最多两秒。
    /// 失败：ack channel 关闭或超时返回 terminal_io，attach 不旋转 attachment、不承诺 replay。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn wait_until_written(
        terminal: &Arc<Terminal>,
        through_seq: u64,
    ) -> Result<(), AgentError> {
        if *terminal.written_sender.borrow() >= through_seq {
            return Ok(());
        }
        let mut written = terminal.written_sender.subscribe();
        match time::timeout(
            WIRE_DELIVERY_TIMEOUT,
            written.wait_for(|seq| *seq >= through_seq),
        )
        .await
        {
            Ok(Ok(_)) => Ok(()),
            Ok(Err(_)) => Err(terminal_io_error(
                "terminal wire delivery acknowledgement closed".to_owned(),
            )),
            Err(_) => Err(terminal_io_error(
                "terminal wire delivery acknowledgement timed out".to_owned(),
            )),
        }
    }

    /// 功能：为 terminal 分配严格递增 seq 并构造 frozen notification。
    ///
    /// 输入：terminal immutable identity、独占 mutable state、event type 与 Schema data。
    /// 输出：可发送/保留的完整 terminal/event frame。
    /// 不变量：全事件 seq 不超过 JavaScript safe integer；仅 output 同步推进 latestOutputSeq，truncated/exited/closed 不扩大 replay cursor；时间固定 UTC Z 毫秒格式。
    /// 失败：seq 耗尽返回 terminal_io 而不复用编号。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn next_event(
        terminal: &Terminal,
        state: &mut TerminalState,
        event_type: &str,
        data: Value,
    ) -> Result<TerminalNotification, AgentError> {
        state.seq = state
            .seq
            .checked_add(1)
            .filter(|seq| *seq <= MAX_SAFE_INTEGER)
            .ok_or_else(|| terminal_io_error("terminal event sequence exhausted".to_owned()))?;
        if event_type == "terminal.output" {
            state.latest_output_seq = state.seq;
        }
        Ok(TerminalNotification {
            jsonrpc: "2.0",
            method: "terminal/event",
            params: TerminalEvent {
                session_id: terminal.session_id.clone(),
                terminal_id: terminal.terminal_id.clone(),
                seq: state.seq,
                time: Utc::now().to_rfc3339_opts(SecondsFormat::Millis, true),
                event_type: event_type.to_owned(),
                data,
            },
        })
    }

    /// 功能：增量 UTF-8 replacement 解码并保留最多三字节的不完整尾部。
    ///
    /// 输入：跨 read pending、当前原始块和 EOF 标志。
    /// 输出：展示文本及本次实际消费的原始字节数。
    /// 不变量：无效序列产生 U+FFFD；非 final 的合法不完整尾部不提前替换；pending 永远有界。
    /// 失败：本函数不返回错误；输入分配受 eventBytes + 3 限制。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn decode_utf8(pending: &mut Vec<u8>, chunk: &[u8], final_chunk: bool) -> (String, usize) {
        let mut bytes = std::mem::take(pending);
        bytes.extend_from_slice(chunk);
        let original_len = bytes.len();
        let mut output = String::new();
        let mut cursor = 0;
        while cursor < bytes.len() {
            match std::str::from_utf8(&bytes[cursor..]) {
                Ok(text) => {
                    output.push_str(text);
                    cursor = bytes.len();
                }
                Err(error) => {
                    let valid = error.valid_up_to();
                    if valid > 0 {
                        // valid_up_to is guaranteed to end on a UTF-8 boundary.
                        if let Ok(text) = std::str::from_utf8(&bytes[cursor..cursor + valid]) {
                            output.push_str(text);
                        }
                        cursor += valid;
                    }
                    if let Some(length) = error.error_len() {
                        output.push('\u{fffd}');
                        cursor = cursor.saturating_add(length);
                    } else if final_chunk {
                        output.push('\u{fffd}');
                        cursor = bytes.len();
                    } else {
                        pending.extend_from_slice(&bytes[cursor..]);
                        break;
                    }
                }
            }
        }
        (output, original_len.saturating_sub(pending.len()))
    }

    /// 功能：从单事件原始字节上限中扣除 UTF-8 decoder 尚未消费的跨 read 前缀。
    ///
    /// 输入：正 eventBytes 上限与当前 decoder pending 字节数。
    /// 输出：仍可安全读取的正字节预算；pending 已达到或超过上限时返回 None。
    /// 不变量：按返回预算读取后，pending 与新块之和不超过 eventBytes，因此单条 output byteCount 也不超过声明上限。
    /// 失败：内部状态违反 pending < eventBytes 时 fail closed 为 None，不执行零长度或越界 read。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn terminal_read_budget(event_bytes: usize, pending_bytes: usize) -> Option<usize> {
        event_bytes
            .checked_sub(pending_bytes)
            .filter(|budget| *budget > 0)
    }

    /// 功能：把 Unix ExitStatus 归一化为 terminal.exited data 三元组。
    ///
    /// 输入：已由当前 Child wait/reap 的退出状态。
    /// 输出：exitCode、稳定 signal 名和 exit/signal reason。
    /// 不变量：普通 0/非零 code 均为 exit；没有 code 才读取 Unix signal。
    /// 失败：未知/缺失 signal 使用 null 或 `SIGNAL_N`，本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn terminal_exit_status(status: ExitStatus) -> (Option<i32>, Option<String>, &'static str) {
        if let Some(code) = status.code() {
            return (Some(code), None, "exit");
        }
        use std::os::unix::process::ExitStatusExt;
        let signal = status.signal().map(signal_name);
        (None, signal, "signal")
    }

    /// 功能：把 Unix signal 编号转换为协议允许的大写稳定名称。
    ///
    /// 输入：ExitStatus 报告的 signal number。
    /// 输出：常见 SIG 名或 `SIGNAL_N`。
    /// 不变量：输出只含大写字母、数字和下划线。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn signal_name(signal: i32) -> String {
        match signal {
            libc::SIGINT => "SIGINT".to_owned(),
            libc::SIGTERM => "SIGTERM".to_owned(),
            libc::SIGKILL => "SIGKILL".to_owned(),
            libc::SIGHUP => "SIGHUP".to_owned(),
            libc::SIGQUIT => "SIGQUIT".to_owned(),
            other => format!("SIGNAL_{other}"),
        }
    }

    /// 功能：验证 terminal/session opaque IDs 的共同字符和长度约束。
    ///
    /// 输入：sessionId 与 terminalId。
    /// 输出：两者均合法时成功。
    /// 不变量：只接受 ASCII frozen pattern，避免 Unicode 长度歧义。
    /// 失败：任一非法返回 invalid_params。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn validate_terminal_ids(session_id: &str, terminal_id: &str) -> Result<(), AgentError> {
        if is_opaque_id(session_id) && is_opaque_id(terminal_id) {
            Ok(())
        } else {
            Err(invalid_terminal_params("invalid terminal/session ID"))
        }
    }

    /// 功能：检查 common.schema opaqueId 的 ASCII pattern 与 128 字节上限。
    ///
    /// 输入：任意字符串。
    /// 输出：匹配 `[A-Za-z0-9][A-Za-z0-9._:-]{0,127}` 时 true。
    /// 不变量：字符均为单字节 ASCII，因此 byte/character 长度一致。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn is_opaque_id(value: &str) -> bool {
        let bytes = value.as_bytes();
        (1..=128).contains(&bytes.len())
            && bytes[0].is_ascii_alphanumeric()
            && bytes
                .iter()
                .all(|byte| byte.is_ascii_alphanumeric() || b"._:-".contains(byte))
    }

    /// 功能：验证显式 extensions namespace 的 portable property-name 约束。
    ///
    /// 输入：反序列化的 extension map。
    /// 输出：所有 key 含至少一个点/连字符分段且仅小写安全字符时成功。
    /// 不变量：extension 值不参与权限或 terminal 行为。
    /// 失败：非法 key 返回 invalid_params。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn validate_extensions(extensions: &BTreeMap<String, Value>) -> Result<(), AgentError> {
        if extensions.keys().all(|key| {
            (key.contains('.') || key.contains('-'))
                && key.split(['.', '-']).all(|segment| {
                    !segment.is_empty()
                        && segment.bytes().all(|byte| {
                            byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'-'
                        })
                })
        }) {
            Ok(())
        } else {
            Err(invalid_terminal_params(
                "invalid terminal extension namespace",
            ))
        }
    }

    /// 功能：验证 mutation 使用当前 connection-bound attachment token。
    ///
    /// 输入：locked terminal state 与请求 token。
    /// 输出：精确匹配时成功。
    /// 不变量：空/detached/已旋转 token 永远不能控制 terminal。
    /// 失败：不匹配返回 `-32010/terminal_attachment_stale`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn require_attachment(state: &TerminalState, attachment_id: &str) -> Result<(), AgentError> {
        if state.attachment_id.as_deref() == Some(attachment_id) {
            Ok(())
        } else {
            Err(
                AgentError::new(ErrorCode::Conflict, "terminal attachment is stale")
                    .with_details(json!({"kind":"terminal_attachment_stale"})),
            )
        }
    }

    /// 功能：在 attach 旋转 ownership 前统一验证 lifecycle、output cursor 与 takeover 决策。
    ///
    /// 输入：locked terminal state 和严格解码的 attach params。
    /// 输出：可继续等待 wire barrier/截取 replay 时成功。
    /// 不变量：afterSeq 只相对最后 output event seq；truncated/exited/closed seq 不会扩大可承诺 replay cursor。
    /// 失败：closing 返回 not-running，cursor 非法返回 invalid_params，已有 controller 且未 takeover 返回 retryable terminal_busy。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn validate_attach_state(
        state: &TerminalState,
        params: &AttachParams,
    ) -> Result<(), AgentError> {
        if state.closing {
            return Err(terminal_not_running_error());
        }
        if params.after_seq > state.latest_output_seq || params.after_seq > MAX_SAFE_INTEGER {
            return Err(invalid_terminal_params("terminal replay cursor is invalid"));
        }
        if state.attachment_id.is_some() && !params.takeover {
            return Err(AgentError::new(
                ErrorCode::RunConflict,
                "terminal already has a controller",
            )
            .retryable(true)
            .with_details(json!({"kind":"terminal_busy"})));
        }
        Ok(())
    }

    /// 功能：在 write/resize/signal 产生任何 fd 或进程副作用前要求 terminal 仍为 Running 且未进入 closing。
    ///
    /// 输入：当前 locked terminal state。
    /// 输出：唯一可变状态 Running + !closing 时成功。
    /// 不变量：Exited/Closed/closing 均 fail closed；调用方不得在失败后继续使用旧 PID、PGID 或 master fd。
    /// 失败：返回 `-32010/terminal_not_found` 形状，避免暴露跨连接生命周期细节。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn require_running(state: &TerminalState) -> Result<(), AgentError> {
        if state.lifecycle == TerminalLifecycle::Running && !state.closing {
            Ok(())
        } else {
            Err(terminal_not_running_error())
        }
    }

    /// 功能：向本管理器创建的正 Unix process group 尽力发送信号。
    ///
    /// 输入：child PID/PGID 与封闭 Signal。
    /// 输出：无。
    /// 不变量：PID 转换成功前不调用 killpg；ESRCH 等竞态被容忍。
    /// 失败：本尽力清理函数不报告错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn signal_process_group(pid: u32, signal: Signal) {
        if let Ok(pid) = i32::try_from(pid) {
            let _ = killpg(Pid::from_raw(pid), signal);
        }
    }

    /// 功能：仅在 open response 未激活 waiter 且内部 tree 缺失的异常路径，对仍由 Child handle 占有的直接进程执行 TERM→grace→KILL。
    ///
    /// 输入：尚未 wait/reap、因此 PID 不可能复用的独占 Child。
    /// 输出：直接子进程收到 TERM，200ms 后仍存在则由 Child handle 请求 KILL。
    /// 不变量：绝不使用旧 PGID，也不在 active waiter ownership 路径调用；正常路径始终使用 ProcessTreeGuard。
    /// 失败：信号竞态被容忍，后续调用方仍执行 Child.wait。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn terminate_inactive_child(child: &mut Child) {
        if let Some(pid) = child.id().and_then(|pid| i32::try_from(pid).ok()) {
            let _ = nix::sys::signal::kill(Pid::from_raw(pid), Signal::SIGTERM);
        }
        time::sleep(TERMINATION_GRACE).await;
        let _ = child.start_kill();
    }

    /// 功能：把 nix Errno 转换为 std I/O error 供 AsyncFd readiness 正确识别 WouldBlock。
    ///
    /// 输入：nix 系统调用错误码。
    /// 输出：保留 raw_os_error 的 std I/O error。
    /// 不变量：EAGAIN/EWOULDBLOCK 能被 Tokio try_io 清 readiness 后重试。
    /// 失败：转换本身不失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn errno_to_io(error: nix::errno::Errno) -> std::io::Error {
        std::io::Error::from_raw_os_error(error as i32)
    }

    /// 功能：生成不持久化、connection-local 的 opaque terminal/attachment ID。
    ///
    /// 输入：固定 terminal 或 attachment 前缀。
    /// 输出：符合 opaqueId pattern 的 UUID v4 ID。
    /// 不变量：token 不写 Session、日志或 artifact。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn new_id(prefix: &str) -> String {
        format!("{prefix}-{}", Uuid::new_v4())
    }

    /// 功能：构造稳定的 terminal 参数错误。
    ///
    /// 输入：不含 secret/动态输入值的诊断文本。
    /// 输出：`-32602/invalid_params`。
    /// 不变量：客户端不解析 message。
    /// 失败：构造本身不失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn invalid_terminal_params(message: impl Into<String>) -> AgentError {
        AgentError::new(ErrorCode::InvalidParams, message)
    }

    /// 功能：构造 fixture-only 策略拒绝错误。
    ///
    /// 输入：不回显 executable/path/hash 的固定诊断文本。
    /// 输出：`-32003/permission_denied`。
    /// 不变量：策略失败不会泄露哪个受限字段最接近通过。
    /// 失败：构造本身不失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn permission_denied_error(message: &str) -> AgentError {
        AgentError::new(ErrorCode::PermissionDenied, message)
    }

    /// 功能：构造 unknown/cross-session/closed terminal 错误。
    ///
    /// 输入：无。
    /// 输出：`-32010/terminal_not_found`。
    /// 不变量：三种情况共享形状，避免 ownership oracle。
    /// 失败：构造本身不失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn terminal_not_found_error() -> AgentError {
        AgentError::new(ErrorCode::Conflict, "terminal was not found")
            .with_details(json!({"kind":"terminal_not_found"}))
    }

    /// 功能：构造 terminal 已退出、正在关闭或 control owner 已结束时的 fail-closed 错误。
    ///
    /// 输入：无。
    /// 输出：与 unknown terminal 相同的 `-32010/terminal_not_found` 公共形状。
    /// 不变量：不泄露旧 PID/PGID、退出时刻或 attachment 之外的状态。
    /// 失败：构造本身不失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn terminal_not_running_error() -> AgentError {
        terminal_not_found_error()
    }

    /// 功能：构造输入字节预算或有界 channel 条目容量耗尽错误。
    ///
    /// 输入：无。
    /// 输出：retryable `-32011/terminal_backpressure`。
    /// 不变量：错误表示整块零接受，调用方可用同一 inputSeq 重试。
    /// 失败：构造本身不失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn terminal_backpressure_error() -> AgentError {
        AgentError::new(ErrorCode::Backpressure, "terminal input buffer is full")
            .retryable(true)
            .with_details(json!({"kind":"terminal_backpressure"}))
    }

    /// 功能：构造 PTY I/O/内部生命周期错误。
    ///
    /// 输入：不含 secret 的诊断文本。
    /// 输出：`-32603/terminal_io`。
    /// 不变量：不得借此把 pipe 启动当成功。
    /// 失败：构造本身不失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn terminal_io_error(message: String) -> AgentError {
        AgentError::new(ErrorCode::InternalError, message)
            .with_details(json!({"kind":"terminal_io"}))
    }

    /// 功能：构造 PTY child 启动失败错误。
    ///
    /// 输入：不含 environment/helper 内容的诊断文本。
    /// 输出：`-32603/process_start_failed`。
    /// 不变量：失败没有 terminal/open success result。
    /// 失败：构造本身不失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn process_start_error(message: String) -> AgentError {
        AgentError::new(ErrorCode::InternalError, message)
            .with_details(json!({"kind":"process_start_failed"}))
    }

    /// 功能：把 spawn/slave clone I/O 错误转换为 process_start_failed。
    ///
    /// 输入：标准库 I/O error。
    /// 输出：不暴露语言对象的 portable 启动错误。
    /// 不变量：不包含环境值或 helper 内容。
    /// 失败：转换本身不失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn process_start_io_error(error: std::io::Error) -> AgentError {
        process_start_error(format!("cannot start terminal process: {error}"))
    }

    #[cfg(test)]
    mod tests {
        use std::collections::{HashMap, VecDeque};
        use std::path::Path;
        #[cfg(target_os = "linux")]
        use std::process::Stdio;
        use std::sync::atomic::{AtomicBool, Ordering};
        use std::sync::{Arc, Mutex as StdMutex};
        use std::time::{Duration, Instant};

        use serde_json::json;
        use tempfile::tempdir;
        #[cfg(target_os = "linux")]
        use tokio::io::{AsyncBufReadExt, BufReader};
        #[cfg(target_os = "linux")]
        use tokio::process::Command;
        use tokio::sync::{Mutex, Notify, mpsc, watch};

        #[cfg(target_os = "linux")]
        use crate::executor::ProcessTreeGuard;

        use super::{
            LimitParams, MAX_INPUT_QUEUE_ITEMS, MAX_TERMINAL_EVENT_FRAME_BYTES,
            MAX_TERMINAL_EVENT_RAW_BYTES, RetainedOutput, Terminal, TerminalAfterResponse,
            TerminalControl, TerminalEvent, TerminalInner, TerminalLifecycle, TerminalLimits,
            TerminalManager, TerminalNotification, TerminalState, decode_utf8, is_opaque_id,
            looks_like_python, next_event, open_cloexec_pty, open_verified_helper,
            terminal_read_budget, validate_limits, wait_until_closed,
        };

        /// 功能：构造不含 OS pid/fd/child 的 test-only terminal，用于安全验证 lifecycle、队列和 replay 排序。
        ///
        /// 输入：目标 lifecycle。
        /// 输出：enabled manager、共享 terminal、event receiver 与未消费 control receiver。
        /// 不变量：任何测试回归都无法向宿主 PID/PGID 发信号；所有 channel 仍使用生产容量。
        /// 失败：本函数不返回错误。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        fn test_terminal(
            lifecycle: TerminalLifecycle,
        ) -> (
            TerminalManager,
            Arc<Terminal>,
            mpsc::Receiver<TerminalNotification>,
            mpsc::Receiver<TerminalControl>,
        ) {
            let (events, event_receiver) = mpsc::channel(16);
            let (inputs, input_receiver) = mpsc::channel(MAX_INPUT_QUEUE_ITEMS);
            let (controls, control_receiver) = mpsc::channel(8);
            let (replay_sender, _replay_receiver) = watch::channel(false);
            let (written_sender, _written_receiver) = watch::channel(0_u64);
            let (fatal_sender, _fatal_receiver) = watch::channel(false);
            let terminal = Arc::new(Terminal {
                terminal_id: "terminal-test".to_owned(),
                session_id: "session-test".to_owned(),
                limits: TerminalLimits {
                    lifetime: Duration::from_secs(10),
                    idle_timeout: Duration::from_secs(5),
                    retention_bytes: 65_536,
                    input_buffer_bytes: 65_536,
                    event_bytes: 16_384,
                },
                state: Mutex::new(TerminalState {
                    attachment_id: Some("attachment-test".to_owned()),
                    columns: 80,
                    rows: 24,
                    seq: 0,
                    latest_output_seq: 0,
                    input_seq: 0,
                    pending_input_bytes: 0,
                    retained_bytes: 0,
                    retained: VecDeque::new(),
                    decoder_pending: Vec::new(),
                    lifecycle,
                    active: true,
                    closing: false,
                    last_activity: Instant::now(),
                    master: None,
                    child: None,
                    tree: None,
                    control_sender: Some(controls),
                    input_sender: Some(inputs),
                    input_receiver: Some(input_receiver),
                    tasks: Vec::new(),
                }),
                event_gate: Mutex::new(()),
                written_sender,
                cleanup_started: AtomicBool::new(false),
                reader_done: AtomicBool::new(true),
                output_delivery_failed: AtomicBool::new(false),
                exit_done: AtomicBool::new(lifecycle == TerminalLifecycle::Exited),
                waiter_done: AtomicBool::new(true),
                closed_done: AtomicBool::new(false),
                reader_notify: Notify::new(),
                waiter_notify: Notify::new(),
                replay_sender,
                closed_notify: Notify::new(),
            });
            let manager = TerminalManager {
                inner: Arc::new(TerminalInner {
                    workspace: std::env::temp_dir(),
                    enabled: true,
                    open_gate: Mutex::new(()),
                    terminals: Mutex::new(HashMap::from([(
                        terminal.terminal_id.clone(),
                        terminal.clone(),
                    )])),
                    event_sender: StdMutex::new(Some(events)),
                    fatal_sender,
                    shutting_down: AtomicBool::new(false),
                }),
            };
            (manager, terminal, event_receiver, control_receiver)
        }

        /// 功能：验证增量 UTF-8 解码跨块保留尾部且替换确定非法序列。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[test]
        fn incremental_utf8_keeps_incomplete_suffix() {
            let mut pending = Vec::new();
            let (first, count) = decode_utf8(&mut pending, &[b'a', 0xe4, 0xb8], false);
            assert_eq!(first, "a");
            assert_eq!(count, 1);
            let (second, count) = decode_utf8(&mut pending, &[0xad, 0xff], false);
            assert_eq!(second, "中\u{fffd}");
            assert_eq!(count, 4);
            assert!(pending.is_empty());
        }

        /// 功能：验证四字节 UTF-8 跨 read 的 pending 会从下一次读取预算扣除，使任一 output byteCount 不超过 eventBytes。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[test]
        fn utf8_pending_is_charged_to_next_event_budget() {
            let event_bytes = 4;
            let mut pending = Vec::new();
            let (prefix, prefix_bytes) =
                decode_utf8(&mut pending, &[b'x', 0xf0, 0x9f, 0x98], false);
            assert_eq!(prefix, "x");
            assert_eq!(prefix_bytes, 1);
            assert_eq!(pending.len(), 3);
            let budget = terminal_read_budget(event_bytes, pending.len()).expect("read budget");
            assert_eq!(budget, 1);
            let (suffix, suffix_bytes) = decode_utf8(&mut pending, &[0x80], false);
            assert_eq!(suffix, "😀");
            assert_eq!(suffix_bytes, event_bytes);
            assert!(suffix_bytes <= event_bytes);
            assert!(pending.is_empty());
            assert_eq!(terminal_read_budget(event_bytes, event_bytes), None);
        }

        /// 功能：验证 opaque ID 与可信 Python basename 使用 frozen ASCII 边界。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[test]
        fn validates_opaque_ids_and_python_names() {
            assert!(is_opaque_id("terminal-abc_1"));
            assert!(!is_opaque_id("终端"));
            assert!(looks_like_python(Path::new("/usr/bin/python3.14")));
            assert!(!looks_like_python(Path::new("/tmp/python-wrapper")));
        }

        #[cfg(target_os = "linux")]
        /// 功能：验证冻结 helper 被复制到带完整 seal 的 immutable memfd，而不是继续执行可原地改写的 workspace inode。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[test]
        fn helper_executes_from_fully_sealed_snapshot() -> Result<(), crate::error::AgentError> {
            use std::io::Read;

            use nix::fcntl::{FcntlArg, SealFlag, fcntl};

            let workspace = tempdir()?;
            let source = include_bytes!("../../CONFORMANCE/fixtures/executor/executor_helper.py");
            std::fs::write(workspace.path().join("executor_helper.py"), source)?;
            let mut snapshot = open_verified_helper(workspace.path())?;
            let seals = fcntl(&snapshot, FcntlArg::F_GET_SEALS)
                .map_err(|error| std::io::Error::from_raw_os_error(error as i32))?;
            let required = SealFlag::F_SEAL_WRITE
                | SealFlag::F_SEAL_GROW
                | SealFlag::F_SEAL_SHRINK
                | SealFlag::F_SEAL_SEAL;
            assert_eq!(seals & required.bits(), required.bits());
            let mut copied = Vec::new();
            snapshot.read_to_end(&mut copied)?;
            assert_eq!(copied, source);
            Ok(())
        }

        #[cfg(target_os = "linux")]
        /// 功能：验证 atomic PTY factory 返回的 master/slave 从创建瞬间即带 FD_CLOEXEC，master 同时为 nonblocking。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[test]
        fn atomic_pty_factory_sets_cloexec_before_publication() {
            use nix::fcntl::{FcntlArg, FdFlag, OFlag, fcntl};
            use nix::pty::Winsize;

            let size = Winsize {
                ws_row: 24,
                ws_col: 80,
                ws_xpixel: 0,
                ws_ypixel: 0,
            };
            let (master, slave) = open_cloexec_pty(&size).expect("atomic CLOEXEC PTY");
            for fd in [&master, &slave] {
                let raw_flags = fcntl(fd, FcntlArg::F_GETFD).expect("descriptor flags");
                assert!(FdFlag::from_bits_truncate(raw_flags).contains(FdFlag::FD_CLOEXEC));
            }
            let status_flags = fcntl(&master, FcntlArg::F_GETFL).expect("master status flags");
            assert!(OFlag::from_bits_truncate(status_flags).contains(OFlag::O_NONBLOCK));
        }

        /// 功能：安全模拟 exited terminal 的 kill 请求，验证在接触 control channel 或任何 OS PID/PGID 前 fail closed。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[tokio::test]
        async fn signal_after_exit_is_rejected_without_control_delivery() {
            let (manager, _terminal, _events, mut controls) =
                test_terminal(TerminalLifecycle::Exited);
            let error = manager
                .signal(json!({
                    "sessionId":"session-test",
                    "terminalId":"terminal-test",
                    "attachmentId":"attachment-test",
                    "signal":"kill"
                }))
                .await
                .expect_err("exited terminal must reject signal");
            assert_eq!(error.details["kind"], "terminal_not_found");
            assert!(matches!(
                controls.try_recv(),
                Err(mpsc::error::TryRecvError::Empty)
            ));
        }

        /// 功能：验证 input channel 的条目硬上限和空输入拒绝，不能用零字节消息制造无界队列。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[tokio::test]
        async fn input_queue_has_item_cap_and_rejects_empty_messages() {
            let (manager, _terminal, _events, _controls) =
                test_terminal(TerminalLifecycle::Running);
            let empty = manager
                .write(json!({
                    "sessionId":"session-test",
                    "terminalId":"terminal-test",
                    "attachmentId":"attachment-test",
                    "inputSeq":1,
                    "data":""
                }))
                .await
                .expect_err("empty input must be rejected");
            assert_eq!(empty.details["kind"], "invalid_params");
            for input_seq in 1..=MAX_INPUT_QUEUE_ITEMS {
                manager
                    .write(json!({
                        "sessionId":"session-test",
                        "terminalId":"terminal-test",
                        "attachmentId":"attachment-test",
                        "inputSeq":input_seq,
                        "data":"x"
                    }))
                    .await
                    .expect("bounded input item must fit");
            }
            let full = manager
                .write(json!({
                    "sessionId":"session-test",
                    "terminalId":"terminal-test",
                    "attachmentId":"attachment-test",
                    "inputSeq":MAX_INPUT_QUEUE_ITEMS + 1,
                    "data":"x"
                }))
                .await
                .expect_err("item cap must apply before byte cap");
            assert_eq!(full.details["kind"], "terminal_backpressure");
        }

        /// 功能：验证 close success 在响应前原子预留 closing，使同批旧 attachment mutation 立即 fail closed且 cleanup 只启动一次。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[tokio::test]
        async fn accepted_close_rejects_pipelined_old_attachment_write()
        -> Result<(), crate::error::AgentError> {
            let (manager, terminal, _events, _controls) = test_terminal(TerminalLifecycle::Running);
            let (result, action) = manager
                .close(json!({
                    "sessionId":"session-test",
                    "terminalId":"terminal-test",
                    "attachmentId":"attachment-test",
                    "mode":"terminate"
                }))
                .await?;
            assert_eq!(result["accepted"], true);
            assert!(terminal.state.lock().await.closing);
            let error = manager
                .write(json!({
                    "sessionId":"session-test",
                    "terminalId":"terminal-test",
                    "attachmentId":"attachment-test",
                    "inputSeq":1,
                    "data":"must-not-run"
                }))
                .await
                .expect_err("accepted close must reject pipelined write");
            assert_eq!(error.details["kind"], "terminal_not_found");
            manager
                .after_response(action.expect("terminate after-response"))
                .await?;
            tokio::time::timeout(Duration::from_millis(250), wait_until_closed(&terminal))
                .await
                .map_err(|_| {
                    crate::error::AgentError::new(
                        crate::error::ErrorCode::Timeout,
                        "reserved close did not start cleanup",
                    )
                })?;
            assert!(terminal.cleanup_started.load(Ordering::Acquire));
            Ok(())
        }

        /// 功能：验证 attach barrier 在 response 后严格发送 replay，再释放并发 live event。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[tokio::test]
        async fn attach_orders_response_then_replay_then_live()
        -> Result<(), crate::error::AgentError> {
            let (manager, terminal, mut events, _controls) =
                test_terminal(TerminalLifecycle::Running);
            manager.record_output(&terminal, b"replay", false).await?;
            let prefetched_live = events.recv().await.expect("prefetched live event");
            let attach_manager = manager.clone();
            let attach = tokio::spawn(async move {
                attach_manager
                    .attach(json!({
                        "sessionId":"session-test",
                        "terminalId":"terminal-test",
                        "afterSeq":0,
                        "takeover":true
                    }))
                    .await
            });
            tokio::time::sleep(Duration::from_millis(10)).await;
            assert!(!attach.is_finished());
            manager.acknowledge_written(&prefetched_live).await;
            let (_result, action) = attach.await.map_err(|error| {
                crate::error::AgentError::new(
                    crate::error::ErrorCode::InternalError,
                    error.to_string(),
                )
            })??;
            let live_manager = manager.clone();
            let live_terminal = terminal.clone();
            let live = tokio::spawn(async move {
                live_manager
                    .emit_ordered_event(
                        &live_terminal,
                        "terminal.output",
                        json!({"stream":"pty","data":"live","byteCount":4}),
                    )
                    .await
            });
            tokio::task::yield_now().await;
            assert!(matches!(
                events.try_recv(),
                Err(mpsc::error::TryRecvError::Empty)
            ));
            manager.after_response(action).await?;
            let replay = events.recv().await.expect("replay event");
            let live_event = events.recv().await.expect("live event");
            assert_eq!(replay.params.seq, 1);
            assert_eq!(replay.params.data["data"], "replay");
            assert_eq!(live_event.params.seq, 2);
            assert_eq!(live_event.params.data["data"], "live");
            live.await.map_err(|error| {
                crate::error::AgentError::new(
                    crate::error::ErrorCode::InternalError,
                    error.to_string(),
                )
            })??;
            Ok(())
        }

        /// 功能：复现 output/truncated 交错尾序列，验证 attach 只承诺实际 replay 最后一条 output seq。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[tokio::test]
        async fn attach_cursor_excludes_non_output_tail_events()
        -> Result<(), crate::error::AgentError> {
            let (manager, terminal, mut events, _controls) =
                test_terminal(TerminalLifecycle::Running);
            {
                let mut state = terminal.state.lock().await;
                for _ in 0..15 {
                    let output = next_event(
                        &terminal,
                        &mut state,
                        "terminal.output",
                        json!({"stream":"pty","data":"x","byteCount":1}),
                    )?;
                    if output.params.seq >= 23 {
                        state.retained.push_back(RetainedOutput {
                            seq: output.params.seq,
                            frame: output,
                            byte_count: 1,
                        });
                        state.retained_bytes += 1;
                    }
                    let _ = next_event(
                        &terminal,
                        &mut state,
                        "terminal.truncated",
                        json!({"omittedBytes":1,"retainedThroughSeq":1}),
                    )?;
                }
                assert_eq!(state.seq, 30);
                assert_eq!(state.latest_output_seq, 29);
            }
            terminal.written_sender.send_replace(30);
            let (result, action) = manager
                .attach(json!({
                    "sessionId":"session-test",
                    "terminalId":"terminal-test",
                    "afterSeq":0,
                    "takeover":true
                }))
                .await?;
            assert_eq!(result["latestOutputSeq"], 29);
            assert_eq!(result["replayThroughSeq"], 29);
            let TerminalAfterResponse::Replay { frames, .. } = &action else {
                panic!("attach must return replay action");
            };
            assert_eq!(
                frames
                    .iter()
                    .map(|frame| frame.params.seq)
                    .collect::<Vec<_>>(),
                vec![23, 25, 27, 29]
            );
            manager.after_response(action).await?;
            for expected in [23, 25, 27, 29] {
                assert_eq!(
                    events.recv().await.expect("replay frame").params.seq,
                    expected
                );
            }
            Ok(())
        }

        /// 功能：验证超过 terminal mpsc 容量的 retained replay 以一个总 deadline 流式完整排队，不在第 256 帧截断。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[tokio::test]
        async fn replay_larger_than_channel_capacity_streams_completely()
        -> Result<(), crate::error::AgentError> {
            let (manager, terminal, mut events, _controls) =
                test_terminal(TerminalLifecycle::Running);
            {
                let mut state = terminal.state.lock().await;
                for _ in 0..346 {
                    let output = next_event(
                        &terminal,
                        &mut state,
                        "terminal.output",
                        json!({"stream":"pty","data":"x","byteCount":1}),
                    )?;
                    state.retained.push_back(RetainedOutput {
                        seq: output.params.seq,
                        frame: output,
                        byte_count: 1,
                    });
                    state.retained_bytes += 1;
                }
            }
            terminal.written_sender.send_replace(346);
            let (result, action) = manager
                .attach(json!({
                    "sessionId":"session-test",
                    "terminalId":"terminal-test",
                    "afterSeq":0,
                    "takeover":true
                }))
                .await?;
            assert_eq!(result["latestOutputSeq"], 346);
            assert_eq!(result["replayThroughSeq"], 346);
            let consumer = tokio::spawn(async move {
                for expected in 1..=346 {
                    let frame = events.recv().await.expect("streamed replay frame");
                    assert_eq!(frame.params.seq, expected);
                }
            });
            manager.after_response(action).await?;
            consumer.await.map_err(|error| {
                crate::error::AgentError::new(
                    crate::error::ErrorCode::InternalError,
                    error.to_string(),
                )
            })?;
            assert!(!terminal.output_delivery_failed.load(Ordering::Acquire));
            Ok(())
        }

        /// 功能：验证 replay 整段 deadline 失败时先设置 fatal 再释放 barrier，live 不得插入 partial replay且独立 cleanup 最终回收 terminal。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[tokio::test]
        async fn replay_failure_fails_transport_before_releasing_live()
        -> Result<(), crate::error::AgentError> {
            let (manager, terminal, mut events, _controls) =
                test_terminal(TerminalLifecycle::Running);
            {
                let mut state = terminal.state.lock().await;
                for _ in 0..32 {
                    let output = next_event(
                        &terminal,
                        &mut state,
                        "terminal.output",
                        json!({"stream":"pty","data":"x","byteCount":1}),
                    )?;
                    state.retained.push_back(RetainedOutput {
                        seq: output.params.seq,
                        frame: output,
                        byte_count: 1,
                    });
                    state.retained_bytes += 1;
                }
            }
            terminal.written_sender.send_replace(32);
            let (_result, action) = manager
                .attach(json!({
                    "sessionId":"session-test",
                    "terminalId":"terminal-test",
                    "afterSeq":0,
                    "takeover":true
                }))
                .await?;
            let live_manager = manager.clone();
            let live_terminal = terminal.clone();
            let live = tokio::spawn(async move {
                live_manager
                    .emit_ordered_event(
                        &live_terminal,
                        "terminal.output",
                        json!({"stream":"pty","data":"must-not-leak","byteCount":13}),
                    )
                    .await
            });
            let slow_consumer = tokio::spawn(async move {
                for _ in 0..8 {
                    tokio::time::sleep(Duration::from_millis(300)).await;
                    let frame = events.recv().await.expect("partial replay frame");
                    assert_ne!(frame.params.data["data"], "must-not-leak");
                }
            });
            let replay_error =
                tokio::time::timeout(Duration::from_secs(3), manager.after_response(action))
                    .await
                    .map_err(|_| {
                        crate::error::AgentError::new(
                            crate::error::ErrorCode::Timeout,
                            "replay total deadline was not bounded",
                        )
                    })?
                    .expect_err("undrained replay must fail transport");
            assert_eq!(replay_error.details["kind"], "terminal_io");
            assert!(*manager.subscribe_delivery_failure().borrow());
            assert!(live.await.expect("live task").is_err());
            slow_consumer.await.map_err(|error| {
                crate::error::AgentError::new(
                    crate::error::ErrorCode::InternalError,
                    error.to_string(),
                )
            })?;
            tokio::time::timeout(Duration::from_millis(250), wait_until_closed(&terminal))
                .await
                .map_err(|_| {
                    crate::error::AgentError::new(
                        crate::error::ErrorCode::Timeout,
                        "replay failure did not start independent cleanup",
                    )
                })?;
            assert!(
                !manager
                    .inner
                    .terminals
                    .lock()
                    .await
                    .contains_key(&terminal.terminal_id)
            );
            Ok(())
        }

        /// 功能：验证任一 output enqueue 失败后不可再发送 exited/closed 等生命周期事件伪装输出完整。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[tokio::test]
        async fn output_delivery_failure_suppresses_lifecycle_events()
        -> Result<(), crate::error::AgentError> {
            let (manager, terminal, mut events, _controls) =
                test_terminal(TerminalLifecycle::Running);
            let sender = manager
                .inner
                .event_sender
                .lock()
                .expect("event sender lock")
                .as_ref()
                .expect("event sender")
                .clone();
            for seq in 1..=16 {
                sender
                    .try_send(TerminalNotification {
                        jsonrpc: "2.0",
                        method: "terminal/event",
                        params: TerminalEvent {
                            session_id: "session-test".to_owned(),
                            terminal_id: "terminal-test".to_owned(),
                            seq,
                            time: "2026-07-15T00:00:00.000Z".to_owned(),
                            event_type: "terminal.output".to_owned(),
                            data: json!({"stream":"pty","data":"filler","byteCount":6}),
                        },
                    })
                    .expect("test channel capacity");
            }
            assert!(
                manager
                    .record_output(&terminal, b"must-not-be-followed-by-exited", false)
                    .await
                    .is_err()
            );
            assert!(terminal.output_delivery_failed.load(Ordering::Acquire));
            assert!(*manager.subscribe_delivery_failure().borrow());
            while events.try_recv().is_ok() {}
            assert!(
                manager
                    .emit_ordered_event(
                        &terminal,
                        "terminal.exited",
                        json!({"exitCode":0,"signal":null,"terminationReason":"exit"}),
                    )
                    .await
                    .is_err()
            );
            assert!(matches!(
                events.try_recv(),
                Err(mpsc::error::TryRecvError::Empty)
            ));
            Ok(())
        }

        /// 功能：验证 terminal event 队列被慢消费者填满时，closed 发布失败不会阻塞内部关闭和 map 回收。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[tokio::test]
        async fn slow_event_consumer_does_not_block_terminal_close()
        -> Result<(), crate::error::AgentError> {
            let (manager, terminal, _events, _controls) = test_terminal(TerminalLifecycle::Exited);
            let sender = manager
                .inner
                .event_sender
                .lock()
                .expect("event sender lock")
                .as_ref()
                .expect("event sender")
                .clone();
            for seq in 1..=16 {
                sender
                    .try_send(TerminalNotification {
                        jsonrpc: "2.0",
                        method: "terminal/event",
                        params: TerminalEvent {
                            session_id: "session-test".to_owned(),
                            terminal_id: "terminal-test".to_owned(),
                            seq,
                            time: "2026-07-15T00:00:00.000Z".to_owned(),
                            event_type: "terminal.output".to_owned(),
                            data: json!({"stream":"pty","data":"filler","byteCount":6}),
                        },
                    })
                    .expect("test channel capacity");
            }
            tokio::time::timeout(
                Duration::from_millis(100),
                manager.close_terminal(&terminal.terminal_id, "client"),
            )
            .await
            .map_err(|_| {
                crate::error::AgentError::new(
                    crate::error::ErrorCode::Timeout,
                    "terminal close blocked on a slow event consumer",
                )
            })?;
            assert!(terminal.closed_done.load(Ordering::Acquire));
            assert!(*manager.subscribe_delivery_failure().borrow());
            assert!(
                !manager
                    .inner
                    .terminals
                    .lock()
                    .await
                    .contains_key(&terminal.terminal_id)
            );
            Ok(())
        }

        #[cfg(target_os = "linux")]
        /// 功能：模拟 open response 未交付而尚未 activate 的 TERM-resistant child，验证 shutdown 使用有界 TERM/KILL/reap 且回收 map。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[tokio::test]
        async fn inactive_terminal_shutdown_has_bounded_reap()
        -> Result<(), crate::error::AgentError> {
            use std::os::unix::process::CommandExt;

            let (manager, terminal, _events, _controls) = test_terminal(TerminalLifecycle::Running);
            let mut command = Command::new("sh");
            command
                .arg("-c")
                .arg("trap '' TERM; printf 'ready\n'; while :; do sleep 1; done")
                .stdin(Stdio::null())
                .stdout(Stdio::piped())
                .stderr(Stdio::null())
                .kill_on_drop(true);
            command.as_std_mut().process_group(0);
            let mut child = command.spawn()?;
            let child_id = child.id().ok_or_else(|| {
                crate::error::AgentError::new(
                    crate::error::ErrorCode::InternalError,
                    "inactive terminal test child omitted pid",
                )
            })?;
            let stdout = child.stdout.take().ok_or_else(|| {
                crate::error::AgentError::new(
                    crate::error::ErrorCode::InternalError,
                    "inactive terminal test child omitted stdout",
                )
            })?;
            let mut line = String::new();
            BufReader::new(stdout).read_line(&mut line).await?;
            assert_eq!(line, "ready\n");
            let tree = ProcessTreeGuard::start(child_id)?;
            {
                let mut state = terminal.state.lock().await;
                state.active = false;
                state.control_sender = None;
                state.child = Some(child);
                state.tree = Some(tree);
            }
            tokio::time::timeout(
                Duration::from_secs(2),
                manager.close_terminal(&terminal.terminal_id, "daemon_shutdown"),
            )
            .await
            .map_err(|_| {
                crate::error::AgentError::new(
                    crate::error::ErrorCode::Timeout,
                    "inactive terminal cleanup exceeded bounded reap",
                )
            })?;
            assert!(terminal.closed_done.load(Ordering::Acquire));
            assert!(
                !manager
                    .inner
                    .terminals
                    .lock()
                    .await
                    .contains_key(&terminal.terminal_id)
            );
            Ok(())
        }

        /// 功能：验证最坏控制字符 JSON 膨胀仍低于 maxEventBytes，且更大的 eventBytes policy 被拒绝。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[test]
        fn event_frame_respects_advertised_max_event_bytes() {
            let frame = TerminalNotification {
                jsonrpc: "2.0",
                method: "terminal/event",
                params: TerminalEvent {
                    session_id: "session-test".to_owned(),
                    terminal_id: "terminal-test".to_owned(),
                    seq: 1,
                    time: "2026-07-15T00:00:00.000Z".to_owned(),
                    event_type: "terminal.output".to_owned(),
                    data: json!({
                        "stream":"pty",
                        "data":"\0".repeat(MAX_TERMINAL_EVENT_RAW_BYTES as usize),
                        "byteCount":MAX_TERMINAL_EVENT_RAW_BYTES
                    }),
                },
            };
            assert!(
                serde_json::to_vec(&frame).expect("event JSON").len()
                    <= MAX_TERMINAL_EVENT_FRAME_BYTES
            );
            let invalid = LimitParams {
                lifetime_ms: 10_000,
                idle_timeout_ms: 5_000,
                retention_bytes: 65_536,
                input_buffer_bytes: 65_536,
                event_bytes: MAX_TERMINAL_EVENT_RAW_BYTES + 1,
            };
            assert!(validate_limits(&invalid).is_err());
        }
    }
}

#[cfg(unix)]
pub use platform::TerminalManager;

#[cfg(not(unix))]
mod unsupported {
    use std::path::Path;
    use std::sync::{Arc, Mutex};

    use serde_json::Value;
    use tokio::sync::{mpsc, watch};

    use super::{TerminalAfterResponse, TerminalNotification};
    use crate::error::{AgentError, ErrorCode};

    /// 在未实现 suspended Job/ConPTY 的平台保持 fail-closed 的 terminal 管理器。
    #[derive(Clone)]
    pub struct TerminalManager {
        event_sender: Arc<Mutex<Option<mpsc::Sender<TerminalNotification>>>>,
        fatal_sender: watch::Sender<bool>,
    }

    impl TerminalManager {
        /// 功能：创建不广告 terminal 的非 Unix stub 及关闭用事件 channel。
        ///
        /// 输入：忽略的 workspace/conformance 状态。
        /// 输出：disabled manager 和 receiver。
        /// 不变量：绝不把 pipe 冒充 Windows ConPTY。
        /// 失败：本方法不访问文件系统且不返回错误。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub fn new(
            _workspace: impl AsRef<Path>,
            _conformance: bool,
        ) -> Result<(Self, mpsc::Receiver<TerminalNotification>), AgentError> {
            let (sender, receiver) = mpsc::channel(1);
            let (fatal_sender, _fatal_receiver) = watch::channel(false);
            Ok((
                Self {
                    event_sender: Arc::new(Mutex::new(Some(sender))),
                    fatal_sender,
                },
                receiver,
            ))
        }

        /// 功能：诚实报告非 Unix stub 没有 terminal 能力。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[must_use]
        pub const fn enabled(&self) -> bool {
            false
        }

        /// 功能：返回空 terminal method/event 能力列表。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        #[must_use]
        pub fn capabilities(&self) -> (Vec<&'static str>, Vec<&'static str>) {
            (Vec::new(), Vec::new())
        }

        /// 功能：忽略非 Unix stub 永远不会写出的 terminal notification wire ack。
        ///
        /// 输入：不可达的 terminal notification。
        /// 输出：无。
        /// 不变量：不创建 terminal、不推进虚假 delivery 水位。
        /// 失败：本方法不返回错误。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub(crate) async fn acknowledge_written(&self, _notification: &TerminalNotification) {}

        /// 功能：返回永远保持 false 的非 Unix terminal delivery failure 订阅。
        ///
        /// 输入：disabled manager。
        /// 输出：connection-local watch receiver。
        /// 不变量：stub 不产生事件，因此不会伪造 fatal delivery 状态。
        /// 失败：本方法不返回错误。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub(crate) fn subscribe_delivery_failure(&self) -> watch::Receiver<bool> {
            self.fatal_sender.subscribe()
        }

        /// 功能：拒绝非 Unix terminal/open，且不启动 pipe 子进程。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub async fn open(
            &self,
            _value: Value,
        ) -> Result<(Value, TerminalAfterResponse), AgentError> {
            Err(unsupported_error())
        }

        /// 功能：拒绝非 Unix terminal/write。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub async fn write(&self, _value: Value) -> Result<Value, AgentError> {
            Err(unsupported_error())
        }

        /// 功能：拒绝非 Unix terminal/resize。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub async fn resize(&self, _value: Value) -> Result<Value, AgentError> {
            Err(unsupported_error())
        }

        /// 功能：拒绝非 Unix terminal/signal。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub async fn signal(&self, _value: Value) -> Result<Value, AgentError> {
            Err(unsupported_error())
        }

        /// 功能：拒绝非 Unix terminal/attach。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub async fn attach(
            &self,
            _value: Value,
        ) -> Result<(Value, TerminalAfterResponse), AgentError> {
            Err(unsupported_error())
        }

        /// 功能：拒绝非 Unix terminal/close。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub async fn close(
            &self,
            _value: Value,
        ) -> Result<(Value, Option<TerminalAfterResponse>), AgentError> {
            Err(unsupported_error())
        }

        /// 功能：忽略非 Unix stub 永远不会产生的 after-response 动作。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub async fn after_response(
            &self,
            _action: TerminalAfterResponse,
        ) -> Result<(), AgentError> {
            Ok(())
        }

        /// 功能：关闭非 Unix stub 的空 terminal event channel。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub async fn shutdown(&self) {
            if let Ok(mut sender) = self.event_sender.lock() {
                sender.take();
            }
        }
    }

    /// 功能：构造非 Unix 未实现 terminal 的 method_not_found 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn unsupported_error() -> AgentError {
        AgentError::new(
            ErrorCode::MethodNotFound,
            "native terminal is unavailable on this platform",
        )
    }
}

#[cfg(not(unix))]
pub use unsupported::TerminalManager;
