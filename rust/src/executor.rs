use std::ffi::OsString;
use std::path::PathBuf;
use std::process::{ExitStatus, Stdio};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::time::{Duration, Instant};

#[cfg(target_os = "linux")]
use std::collections::BTreeMap;
#[cfg(target_os = "linux")]
use std::sync::Mutex;

use serde::{Deserialize, Serialize};
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWriteExt};
use tokio::process::Command;
use tokio::task::JoinHandle;
use tokio::time;
use tokio_util::sync::CancellationToken;

use crate::error::{AgentError, ErrorCode};
use crate::hard_sandbox::{HardSandboxRequest, LinuxBwrapSandbox, sandbox_unavailable};

#[cfg(target_os = "linux")]
use crate::hard_sandbox::command_exit_observed;

const MAX_STDIN_BYTES: usize = 1024 * 1024;

#[derive(Debug, Clone)]
pub struct ProcessRequest {
    pub executable: OsString,
    pub args: Vec<OsString>,
    pub cwd: PathBuf,
    pub stdin: Option<Vec<u8>>,
    pub timeout: Duration,
    pub stdout_limit_bytes: usize,
    pub stderr_limit_bytes: usize,
    pub total_output_limit_bytes: usize,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ShellKind {
    Bash,
    Sh,
    Pwsh,
    PowerShell,
    Cmd,
}

#[derive(Debug, Clone)]
pub struct ShellRequest {
    pub shell: ShellKind,
    pub script: String,
    pub cwd: PathBuf,
    pub stdin: Option<Vec<u8>>,
    pub timeout: Duration,
    pub stdout_limit_bytes: usize,
    pub stderr_limit_bytes: usize,
    pub total_output_limit_bytes: usize,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ProcessTerminationReason {
    Exit,
    Signal,
    Timeout,
    Cancelled,
    OutputLimit,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StreamCapture {
    pub encoding: String,
    pub text: String,
    pub captured_bytes: u64,
    pub total_bytes: u64,
    pub omitted_bytes: u64,
    pub truncated: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProcessResult {
    pub exit_code: Option<i32>,
    pub signal: Option<String>,
    pub termination_reason: ProcessTerminationReason,
    pub duration_ms: u64,
    pub containment: String,
    pub stdout: StreamCapture,
    pub stderr: StreamCapture,
}

#[derive(Debug, Clone, Default)]
pub struct ProcessExecutor;

impl ProcessExecutor {
    /// 功能：不经过 shell 启动指定可执行文件，持续分离排空双流，并统一实施 stdin、取消、超时、总输出和进程树限制。
    ///
    /// 输入：包含 executable、原始 argv、工作目录、可选 bounded stdin 与独立资源上限的请求，以及运行级取消令牌。
    /// 输出：子进程一旦成功启动，始终返回规范化 execution；非零退出、signal、timeout、cancelled 和 output_limit 都是可观察终止结果。
    /// 不变量：argv 不做 shell 重解析；子环境从固定白名单开始；捕获字节与省略字节之和等于已排空总字节；所有终止路径共用整树清理。
    /// 失败：请求边界非法、进程无法启动、Linux 后代守卫无法建立、管道读取或等待失败时返回结构化错误；失败结果不伪造 execution。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn exec(
        &self,
        request: ProcessRequest,
        cancellation: CancellationToken,
    ) -> Result<ProcessResult, AgentError> {
        self.execute(request, ExecutionMode::Host, cancellation)
            .await
    }

    /// 功能：只沿已启动自检的 `linux-bwrap-v1` 后端执行原始 process argv。
    ///
    /// 输入：普通有界 process 请求、不可变 backend、审批绑定的 sandbox 请求和运行级取消令牌。
    /// 输出：用户 command 实际启动后的规范 execution，containment 固定为 `os_isolation`。
    /// 不变量：setup/status/namespace/FD bind 任一失败返回 `sandbox_unavailable`，本方法绝不调用 host `exec` 兜底。
    /// 失败：非 Linux、backend/workspace 身份变化、setup 失败或监督基础设施异常返回结构化错误且不伪造 execution。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn exec_sandboxed(
        &self,
        request: ProcessRequest,
        backend: &LinuxBwrapSandbox,
        sandbox: HardSandboxRequest,
        cancellation: CancellationToken,
    ) -> Result<ProcessResult, AgentError> {
        #[cfg(target_os = "linux")]
        {
            return self
                .execute(
                    request,
                    ExecutionMode::Sandbox {
                        backend,
                        sandbox,
                        host_canary: None,
                    },
                    cancellation,
                )
                .await;
        }
        #[cfg(not(target_os = "linux"))]
        {
            let _ = (request, backend, sandbox, cancellation);
            Err(sandbox_unavailable())
        }
    }

    /// 功能：为 startup self-test 向 bwrap 宿主环境注入随机 canary，同时验证 sandbox child 环境为空。
    ///
    /// 输入：与 `exec_sandboxed` 相同，另加只在当前自检内存存在的 canary。
    /// 输出：与生产 sandbox 路径相同的规范 execution。
    /// 不变量：canary 不进入 argv、输出、错误、Session 或用户 child；生产工具调用不能访问本入口。
    /// 失败：与 `exec_sandboxed` 相同，任何失败绝不 host fallback。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) async fn exec_sandboxed_with_canary(
        &self,
        request: ProcessRequest,
        backend: &LinuxBwrapSandbox,
        sandbox: HardSandboxRequest,
        host_canary: Option<&std::ffi::OsStr>,
        cancellation: CancellationToken,
    ) -> Result<ProcessResult, AgentError> {
        #[cfg(target_os = "linux")]
        {
            return self
                .execute(
                    request,
                    ExecutionMode::Sandbox {
                        backend,
                        sandbox,
                        host_canary,
                    },
                    cancellation,
                )
                .await;
        }
        #[cfg(not(target_os = "linux"))]
        {
            let _ = (request, backend, sandbox, host_canary, cancellation);
            Err(sandbox_unavailable())
        }
    }

    /// 功能：在宿主或 hard-sandbox 单一路径上复用同一双流、deadline、取消和整树监督器。
    ///
    /// 输入：已选择且不可在本次调用中改变的执行模式、process 请求与取消令牌。
    /// 输出：子 command 实际启动后的结构化结果。
    /// 不变量：Sandbox 模式只 spawn bwrap；JSON status 没有用户 exit 记录的自然退出视为 setup 失败；任何错误不切换模式。
    /// 失败：请求、spawn、containment、status、pipe 或 wait 失败返回对应结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn execute(
        &self,
        request: ProcessRequest,
        mode: ExecutionMode<'_>,
        cancellation: CancellationToken,
    ) -> Result<ProcessResult, AgentError> {
        validate_request(&request)?;
        let started = Instant::now();
        #[cfg(target_os = "linux")]
        let mut sandbox_command = match mode {
            ExecutionMode::Host => None,
            ExecutionMode::Sandbox {
                backend,
                sandbox,
                host_canary,
            } => Some(backend.prepare_command(&request, sandbox, host_canary)?),
        };
        #[cfg(target_os = "linux")]
        let sandboxed = sandbox_command.is_some();
        #[cfg(not(target_os = "linux"))]
        let sandboxed = matches!(mode, ExecutionMode::SandboxUnavailable);
        #[cfg(target_os = "linux")]
        let (executable, args, cwd) = sandbox_command
            .as_ref()
            .map(|prepared| (&prepared.executable, &prepared.args, &prepared.cwd))
            .unwrap_or((&request.executable, &request.args, &request.cwd));
        #[cfg(not(target_os = "linux"))]
        let (executable, args, cwd) = (&request.executable, &request.args, &request.cwd);
        let mut command = Command::new(executable);
        command
            .args(args)
            .current_dir(cwd)
            .env_clear()
            .stdin(if request.stdin.is_some() {
                Stdio::piped()
            } else {
                Stdio::null()
            })
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .kill_on_drop(true);
        if sandboxed {
            command.env("PATH", "/usr/bin:/bin");
            #[cfg(target_os = "linux")]
            if let Some(canary) = sandbox_command
                .as_ref()
                .and_then(|prepared| prepared.host_canary.as_ref())
            {
                command.env("QXNM_FORGE_SANDBOX_SECRET", canary);
            }
        } else {
            apply_safe_environment(&mut command);
        }
        configure_process_group(&mut command);
        #[cfg(target_os = "linux")]
        if let Some(prepared) = sandbox_command.as_ref() {
            configure_inherited_fds(&mut command, prepared.inherited_fds()?);
        }
        let mut child = command.spawn().map_err(|error| {
            if sandboxed {
                sandbox_unavailable()
            } else {
                process_start_error(error)
            }
        })?;
        #[cfg(target_os = "linux")]
        let sandbox_status = match sandbox_command.as_mut() {
            Some(prepared) => Some(prepared.after_spawn()?),
            None => None,
        };
        let child_id = child.id().ok_or_else(|| {
            AgentError::new(ErrorCode::InternalError, "spawned process omitted its pid")
                .with_details(serde_json::json!({"kind":"process_tree_unavailable"}))
        })?;
        let mut tree = match ProcessTreeGuard::start(child_id) {
            Ok(tree) => tree,
            Err(error) => {
                terminate_process_group(child_id);
                let _ = child.kill().await;
                let _ = child.wait().await;
                return Err(if sandboxed {
                    sandbox_unavailable()
                } else {
                    error
                });
            }
        };
        let containment = if sandboxed {
            "os_isolation".to_owned()
        } else {
            tree.containment().to_owned()
        };
        let stdout = child.stdout.take().ok_or_else(|| {
            AgentError::new(ErrorCode::InternalError, "child stdout was not piped")
        })?;
        let stderr = child.stderr.take().ok_or_else(|| {
            AgentError::new(ErrorCode::InternalError, "child stderr was not piped")
        })?;

        let overflow = CancellationToken::new();
        let budget = Arc::new(OutputBudget::new(request.total_output_limit_bytes));
        let stdout_task = tokio::spawn(read_bounded(
            stdout,
            request.stdout_limit_bytes,
            Arc::clone(&budget),
            overflow.clone(),
        ));
        let stderr_task = tokio::spawn(read_bounded(
            stderr,
            request.stderr_limit_bytes,
            Arc::clone(&budget),
            overflow.clone(),
        ));
        let stdin_task = child
            .stdin
            .take()
            .zip(request.stdin)
            .map(|(mut stdin, bytes)| {
                tokio::spawn(async move {
                    stdin.write_all(&bytes).await?;
                    stdin.shutdown().await
                })
            });

        let outcome = tokio::select! {
            biased;
            () = cancellation.cancelled() => ProcessOutcome::Cancelled,
            () = overflow.cancelled() => ProcessOutcome::OutputLimit,
            () = time::sleep(request.timeout) => ProcessOutcome::Timeout,
            status = child.wait() => match status {
                Ok(status) => ProcessOutcome::Exited(status),
                Err(error) => ProcessOutcome::WaitFailed(error),
            },
        };

        tree.cleanup().await;
        if !matches!(outcome, ProcessOutcome::Exited(_)) {
            let _ = child.kill().await;
            let _ = child.wait().await;
        }
        if let Some(task) = stdin_task {
            let _ = task.await;
        }
        let stdout = join_output(stdout_task).await?;
        let stderr = join_output(stderr_task).await?;
        #[cfg(target_os = "linux")]
        if let Some(status) = sandbox_status {
            let command_exited = command_exit_observed(status).await?;
            if matches!(outcome, ProcessOutcome::Exited(_)) && !command_exited {
                return Err(sandbox_unavailable());
            }
        }
        let duration_ms = u64::try_from(started.elapsed().as_millis()).unwrap_or(u64::MAX);

        let (exit_code, signal, termination_reason) = match outcome {
            ProcessOutcome::Exited(status) => status_result(status),
            ProcessOutcome::Cancelled => (None, None, ProcessTerminationReason::Cancelled),
            ProcessOutcome::Timeout => (None, None, ProcessTerminationReason::Timeout),
            ProcessOutcome::OutputLimit => (None, None, ProcessTerminationReason::OutputLimit),
            ProcessOutcome::WaitFailed(error) => return Err(AgentError::from(error)),
        };
        Ok(ProcessResult {
            exit_code,
            signal,
            termination_reason,
            duration_ms,
            containment,
            stdout: StreamCapture::from_bounded(stdout),
            stderr: StreamCapture::from_bounded(stderr),
        })
    }

    /// 功能：通过调用方明确选择的 shell 执行一个完整脚本参数，并复用 process.exec 的 stdin、双流和整树终止实现。
    ///
    /// 输入：封闭 shell 枚举、脚本文本、工作目录、可选 stdin、资源限制和取消令牌。
    /// 输出：与 `exec` 相同的规范化 execution 结果。
    /// 不变量：解释器与非交互参数由固定映射产生，脚本文本只占一个 argv 元素，不猜测 shell、不加载 profile。
    /// 失败：解释器不可用、请求非法或执行基础设施失败时返回结构化错误；已启动进程的普通失败仍返回 execution。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn shell(
        &self,
        request: ShellRequest,
        cancellation: CancellationToken,
    ) -> Result<ProcessResult, AgentError> {
        let (executable, args): (&str, Vec<OsString>) = match request.shell {
            ShellKind::Bash => (
                "bash",
                vec![
                    "--noprofile".into(),
                    "--norc".into(),
                    "-c".into(),
                    request.script.into(),
                ],
            ),
            ShellKind::Sh => ("sh", vec!["-c".into(), request.script.into()]),
            ShellKind::Pwsh => (
                "pwsh",
                vec![
                    "-NoLogo".into(),
                    "-NoProfile".into(),
                    "-NonInteractive".into(),
                    "-Command".into(),
                    request.script.into(),
                ],
            ),
            ShellKind::PowerShell => (
                "powershell",
                vec![
                    "-NoLogo".into(),
                    "-NoProfile".into(),
                    "-NonInteractive".into(),
                    "-Command".into(),
                    request.script.into(),
                ],
            ),
            ShellKind::Cmd => (
                "cmd",
                vec!["/D".into(), "/S".into(), "/C".into(), request.script.into()],
            ),
        };
        self.exec(
            ProcessRequest {
                executable: executable.into(),
                args,
                cwd: request.cwd,
                stdin: request.stdin,
                timeout: request.timeout,
                stdout_limit_bytes: request.stdout_limit_bytes,
                stderr_limit_bytes: request.stderr_limit_bytes,
                total_output_limit_bytes: request.total_output_limit_bytes,
            },
            cancellation,
        )
        .await
    }

    /// 功能：把显式 shell 映射为固定非交互 argv 后，仅沿已自检 Bubblewrap backend 执行。
    ///
    /// 输入：封闭 shell 请求、backend、审批绑定 sandbox 请求和取消令牌。
    /// 输出：containment 为 `os_isolation` 的规范结果。
    /// 不变量：脚本文本仍只占一个 argv；任何 backend/setup 失败不调用普通 `shell` 或 `exec`。
    /// 失败：解释器、backend、setup、监督或输出失败返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn shell_sandboxed(
        &self,
        request: ShellRequest,
        backend: &LinuxBwrapSandbox,
        sandbox: HardSandboxRequest,
        cancellation: CancellationToken,
    ) -> Result<ProcessResult, AgentError> {
        let (executable, args): (&str, Vec<OsString>) = match request.shell {
            ShellKind::Bash => (
                "bash",
                vec![
                    "--noprofile".into(),
                    "--norc".into(),
                    "-c".into(),
                    request.script.into(),
                ],
            ),
            ShellKind::Sh => ("sh", vec!["-c".into(), request.script.into()]),
            ShellKind::Pwsh => (
                "pwsh",
                vec![
                    "-NoLogo".into(),
                    "-NoProfile".into(),
                    "-NonInteractive".into(),
                    "-Command".into(),
                    request.script.into(),
                ],
            ),
            ShellKind::PowerShell => (
                "powershell",
                vec![
                    "-NoLogo".into(),
                    "-NoProfile".into(),
                    "-NonInteractive".into(),
                    "-Command".into(),
                    request.script.into(),
                ],
            ),
            ShellKind::Cmd => (
                "cmd",
                vec!["/D".into(), "/S".into(), "/C".into(), request.script.into()],
            ),
        };
        self.exec_sandboxed(
            ProcessRequest {
                executable: executable.into(),
                args,
                cwd: request.cwd,
                stdin: request.stdin,
                timeout: request.timeout,
                stdout_limit_bytes: request.stdout_limit_bytes,
                stderr_limit_bytes: request.stderr_limit_bytes,
                total_output_limit_bytes: request.total_output_limit_bytes,
            },
            backend,
            sandbox,
            cancellation,
        )
        .await
    }
}

enum ExecutionMode<'a> {
    Host,
    #[cfg(target_os = "linux")]
    Sandbox {
        backend: &'a LinuxBwrapSandbox,
        sandbox: HardSandboxRequest,
        host_canary: Option<&'a std::ffi::OsStr>,
    },
    #[cfg(not(target_os = "linux"))]
    SandboxUnavailable,
}

impl StreamCapture {
    /// 功能：把有界原始流转换为 UTF-8 replacement 展示文本和严格字节账。
    ///
    /// 输入：已持续排空完成的有界字节与总读取量。
    /// 输出：满足 `capturedBytes + omittedBytes == totalBytes` 的公共 stream capture。
    /// 不变量：截断只体现在元数据，不向 text 插入伪造标记；无效 UTF-8 仅在展示层替换。
    /// 失败：本方法不返回错误；计数使用饱和减法抵御异常平台计数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn from_bounded(output: BoundedOutput) -> Self {
        let captured_bytes = u64::try_from(output.bytes.len()).unwrap_or(u64::MAX);
        let omitted_bytes = output.total.saturating_sub(captured_bytes);
        Self {
            encoding: "utf-8-replacement".to_owned(),
            text: String::from_utf8_lossy(&output.bytes).into_owned(),
            captured_bytes,
            total_bytes: output.total,
            omitted_bytes,
            truncated: omitted_bytes > 0,
        }
    }
}

enum ProcessOutcome {
    Exited(ExitStatus),
    Cancelled,
    Timeout,
    OutputLimit,
    WaitFailed(std::io::Error),
}

struct BoundedOutput {
    bytes: Vec<u8>,
    total: u64,
}

struct OutputBudget {
    total_observed: AtomicU64,
    captured: AtomicUsize,
    limit: usize,
}

impl OutputBudget {
    /// 功能：创建 stdout/stderr 共用的总输出与内存捕获预算。
    ///
    /// 输入：严格正的总字节上限。
    /// 输出：初始计数为零的并发预算。
    /// 不变量：双流共享同一总量与捕获量，不能各自重复获得完整总预算。
    /// 失败：上限已由请求验证，本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    const fn new(limit: usize) -> Self {
        Self {
            total_observed: AtomicU64::new(0),
            captured: AtomicUsize::new(0),
            limit,
        }
    }

    /// 功能：登记一个输出块并为其原子保留仍可捕获的共享字节数。
    ///
    /// 输入：本次已从管道读出的字节数和当前流仍允许捕获的字节数。
    /// 输出：调用方可以追加到内存的前缀字节数，以及总输出是否已越过硬上限。
    /// 不变量：累计观察量单调递增；所有成功保留之和不超过总限制。
    /// 失败：原子计数饱和时保守判定超限，不发生无界分配。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn observe(&self, count: usize, stream_remaining: usize) -> (usize, bool) {
        let count_u64 = u64::try_from(count).unwrap_or(u64::MAX);
        let previous = self.total_observed.fetch_add(count_u64, Ordering::AcqRel);
        let total = previous.saturating_add(count_u64);
        let requested = count.min(stream_remaining);
        let reserved = reserve_capture(&self.captured, self.limit, requested);
        (reserved, total > self.limit as u64)
    }
}

/// 功能：验证执行请求的 stdin、时间和三个输出上限均为安全有限值。
///
/// 输入：尚未产生副作用的 process 请求。
/// 输出：全部边界合法时返回成功。
/// 不变量：任何失败发生在 spawn 前；stdin 最大 1 MiB，输出限制必须为正。
/// 失败：非法限制返回 `InvalidParams/executor_invalid`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_request(request: &ProcessRequest) -> Result<(), AgentError> {
    if request.timeout.is_zero()
        || request.stdout_limit_bytes == 0
        || request.stderr_limit_bytes == 0
        || request.total_output_limit_bytes == 0
        || request
            .stdin
            .as_ref()
            .is_some_and(|stdin| stdin.len() > MAX_STDIN_BYTES)
    {
        return Err(AgentError::new(
            ErrorCode::InvalidParams,
            "executor limits or stdin are outside the supported range",
        )
        .with_details(serde_json::json!({"kind":"executor_invalid"})));
    }
    Ok(())
}

/// 功能：把 spawn I/O 失败转换为规范规定的 `process_start_failed`，不暴露语言对象。
///
/// 输入：标准库返回的进程启动错误。
/// 输出：不可重试的内部基础设施错误及稳定 details.kind。
/// 不变量：错误中不包含环境变量值或 stdin；消息仅供人阅读。
/// 失败：转换本身不失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn process_start_error(error: std::io::Error) -> AgentError {
    AgentError::new(
        ErrorCode::InternalError,
        format!("failed to spawn executable: {error}"),
    )
    .with_details(serde_json::json!({"kind":"process_start_failed"}))
}

/// 功能：为工具进程构造不含 Provider 凭据、代理和任意宿主变量的最小跨平台基础环境。
///
/// 输入：尚未启动的 Tokio Command。
/// 输出：命令只获得固定 allowlist 中当前宿主确实存在的值。
/// 不变量：模型参数不能扩展继承白名单；KEY/TOKEN/SECRET、代理和 conformance canary 均不会继承。
/// 失败：环境赋值由 Command 延迟验证，启动失败统一归类为 process_start_failed。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) fn apply_safe_environment(command: &mut Command) {
    const ALLOWED: &[&str] = &[
        "PATH",
        "HOME",
        "USER",
        "LOGNAME",
        "LANG",
        "LC_ALL",
        "LC_CTYPE",
        "TMPDIR",
        "TMP",
        "TEMP",
        "SystemRoot",
        "ComSpec",
        "PATHEXT",
    ];
    for name in ALLOWED {
        if let Some(value) = std::env::var_os(name) {
            command.env(name, value);
        }
    }
}

/// 功能：持续排空单个输出流，只保留共享及单流预算允许的前缀并累计真实读取量。
///
/// 输入：异步管道、单流上限、双流共享预算和超限通知令牌。
/// 输出：有界捕获字节及直到 EOF 的实际读取字节数。
/// 不变量：达到捕获上限后仍继续读取，避免子进程因 pipe 背压死锁；内存不会随省略字节增长。
/// 失败：管道读取错误转换为统一 I/O 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn read_bounded(
    mut reader: impl AsyncRead + Unpin,
    stream_limit: usize,
    budget: Arc<OutputBudget>,
    overflow: CancellationToken,
) -> Result<BoundedOutput, AgentError> {
    let mut captured = Vec::with_capacity(stream_limit.min(64 * 1024));
    let mut total = 0_u64;
    let mut buffer = [0_u8; 8192];
    loop {
        let count = reader.read(&mut buffer).await?;
        if count == 0 {
            break;
        }
        total = total.saturating_add(u64::try_from(count).unwrap_or(u64::MAX));
        let remaining = stream_limit.saturating_sub(captured.len());
        let (retain, exceeded) = budget.observe(count, remaining);
        captured.extend_from_slice(&buffer[..retain]);
        if exceeded {
            overflow.cancel();
        }
    }
    Ok(BoundedOutput {
        bytes: captured,
        total,
    })
}

/// 功能：使用无锁 compare-exchange 从共享捕获预算中保留有限字节。
///
/// 输入：已使用计数、总限制和本次请求量。
/// 输出：本次实际保留量，范围为 0..=requested。
/// 不变量：并发调用成功保留总和不超过 limit；计数只增不减。
/// 失败：竞争时内部重试；计数已满时返回零。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn reserve_capture(used: &AtomicUsize, limit: usize, requested: usize) -> usize {
    let mut current = used.load(Ordering::Acquire);
    loop {
        let available = limit.saturating_sub(current);
        let granted = available.min(requested);
        if granted == 0 {
            return 0;
        }
        match used.compare_exchange_weak(
            current,
            current.saturating_add(granted),
            Ordering::AcqRel,
            Ordering::Acquire,
        ) {
            Ok(_) => return granted,
            Err(observed) => current = observed,
        }
    }
}

/// 功能：回收输出读取任务并把 panic/cancel 与管道错误转换为公共错误。
///
/// 输入：由当前执行调用创建的 bounded reader task。
/// 输出：完整有界读取结果。
/// 不变量：不会静默丢弃 reader 失败；调用时进程树已完成清理，任务应能观察 EOF。
/// 失败：JoinError 或读取错误返回 InternalError/IoError。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn join_output(
    task: JoinHandle<Result<BoundedOutput, AgentError>>,
) -> Result<BoundedOutput, AgentError> {
    task.await
        .map_err(|error| AgentError::new(ErrorCode::InternalError, error.to_string()))?
}

/// 功能：把自然 wait 状态归一化为 exit 或 signal 三元组。
///
/// 输入：操作系统返回的 ExitStatus。
/// 输出：可选 exitCode、可选稳定 signal 名和对应 terminationReason。
/// 不变量：正常零/非零退出均为 exit；只有缺少 exit code 的 Unix 状态才为 signal。
/// 失败：未知信号安全显示为 `SIGNAL_<N>`，本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn status_result(status: ExitStatus) -> (Option<i32>, Option<String>, ProcessTerminationReason) {
    if let Some(code) = status.code() {
        return (Some(code), None, ProcessTerminationReason::Exit);
    }
    (None, exit_signal(&status), ProcessTerminationReason::Signal)
}

#[cfg(unix)]
/// 功能：读取 Unix ExitStatus 的 signal 编号并转换为协议允许的大写名称。
///
/// 输入：没有普通 exit code 的 Unix 状态。
/// 输出：SIGINT/SIGTERM/SIGKILL 等稳定名称，未知编号使用 `SIGNAL_<N>`。
/// 不变量：输出只含大写字母、数字和下划线。
/// 失败：状态无 signal 时返回 None。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn exit_signal(status: &ExitStatus) -> Option<String> {
    use std::os::unix::process::ExitStatusExt;

    status.signal().map(|signal| match signal {
        libc::SIGINT => "SIGINT".to_owned(),
        libc::SIGTERM => "SIGTERM".to_owned(),
        libc::SIGKILL => "SIGKILL".to_owned(),
        libc::SIGHUP => "SIGHUP".to_owned(),
        libc::SIGQUIT => "SIGQUIT".to_owned(),
        value => format!("SIGNAL_{value}"),
    })
}

#[cfg(not(unix))]
/// 功能：在不提供 ExitStatus signal 的平台诚实返回未知信号。
///
/// 输入：非 Unix ExitStatus。
/// 输出：None。
/// 不变量：不把平台专属状态码伪装成 Unix signal。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn exit_signal(_status: &ExitStatus) -> Option<String> {
    None
}

#[cfg(unix)]
/// 功能：在 Unix 启动前把直接子进程设为新进程组组长，建立第一层整树信号边界。
///
/// 输入：尚未 spawn 的命令。
/// 输出：命令带 process_group(0) 启动属性。
/// 不变量：argv 和环境不受修改；Linux 的 `/proc` 身份采样仅用于 best-effort 清理，不能证明覆盖 setsid/double-fork 逃逸。
/// 失败：设置动作本身不返回错误，spawn 阶段统一报告平台失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn configure_process_group(command: &mut Command) {
    use std::os::unix::process::CommandExt;
    command.as_std_mut().process_group(0);
}

#[cfg(target_os = "linux")]
/// 功能：为 Bubblewrap child 原子清除 workspace/status FD 的 CLOEXEC 标志。
///
/// 输入：尚未 spawn 的命令和两个由 O_CLOEXEC 原子创建/打开的正 FD。
/// 输出：child pre-exec 时只在自身 FD table 清除 CLOEXEC，父进程及并发 spawn 仍保持封闭。
/// 不变量：closure 只调用 async-signal-safe fcntl；不分配、不读取环境、不改变 argv；失败使 spawn 返回且不 host fallback。
/// 失败：F_GETFD/F_SETFD 任一步失败传播为 spawn 失败并映射 `sandbox_unavailable`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[allow(unsafe_code)]
fn configure_inherited_fds(command: &mut Command, inherited_fds: [std::os::fd::RawFd; 2]) {
    // SAFETY: pre_exec closure 仅对两个预先打开的 Copy fd 调用 POSIX async-signal-safe fcntl。
    unsafe {
        command.pre_exec(move || {
            for fd in inherited_fds {
                let flags = libc::fcntl(fd, libc::F_GETFD);
                if flags == -1 || libc::fcntl(fd, libc::F_SETFD, flags & !libc::FD_CLOEXEC) == -1 {
                    return Err(std::io::Error::last_os_error());
                }
            }
            Ok(())
        });
    }
}

#[cfg(not(unix))]
/// 功能：在尚无原生 Job Object 接线的平台保留进程组配置入口且不伪造能力。
///
/// 输入：尚未 spawn 的命令。
/// 输出：命令不变。
/// 不变量：调用方不会据此广告 Windows suspended Job containment。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn configure_process_group(_command: &mut Command) {}

#[cfg(unix)]
/// 功能：向当前执行器创建的直接进程组发送强制终止，作为守卫建立失败和最终回收兜底。
///
/// 输入：直接子进程 PID，同时也是配置后的 PGID。
/// 输出：尽力发送 SIGKILL。
/// 不变量：只操作本次 spawn 的正 PID；进程已退出视为成功竞态。
/// 失败：信号错误被容忍，调用方仍使用 Child.kill/wait 回收直接子进程。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn terminate_process_group(child_id: u32) {
    if let Ok(pid) = i32::try_from(child_id) {
        let _ = nix::sys::signal::killpg(
            nix::unistd::Pid::from_raw(pid),
            nix::sys::signal::Signal::SIGKILL,
        );
    }
}

#[cfg(not(unix))]
/// 功能：在非 Unix 平台保留组终止兜底入口，由 Child.kill 处理直接进程。
///
/// 输入：本次子进程 PID。
/// 输出：无。
/// 不变量：不声称已终止 Windows 整棵进程树。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn terminate_process_group(_child_id: u32) {}

#[cfg(target_os = "linux")]
#[derive(Debug, Clone, Copy)]
struct ProcIdentity {
    pid: i32,
    parent_pid: i32,
    start_time: u64,
}

#[cfg(target_os = "linux")]
pub(crate) struct ProcessTreeGuard {
    root_id: u32,
    tracked: Arc<Mutex<BTreeMap<i32, u64>>>,
    stop: CancellationToken,
    monitor: Option<JoinHandle<()>>,
    active: bool,
}

#[cfg(target_os = "linux")]
impl ProcessTreeGuard {
    /// 功能：为刚启动的 Linux 进程建立基于 PID start-time 身份的持续后代追踪器。
    ///
    /// 输入：本次直接子进程 PID。
    /// 输出：已记录根身份并每 5ms 尽力刷新 `/proc` 后代关系的普通进程组 guard。
    /// 不变量：已采样 PID 必须同时匹配 start-time 才会被发信号；采样不能证明捕获 adversarial double-fork/快速 reparent。
    /// 失败：`/proc` 不可读或根身份已消失时返回 `process_tree_unavailable`，调用方在返回前终止直接组。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn start(child_id: u32) -> Result<Self, AgentError> {
        let pid = i32::try_from(child_id).map_err(|_| process_tree_error("child pid overflow"))?;
        let root = read_proc_identity(pid)
            .map_err(|_| process_tree_error("cannot establish Linux descendant identity guard"))?;
        let tracked = Arc::new(Mutex::new(BTreeMap::from([(pid, root.start_time)])));
        let stop = CancellationToken::new();
        let monitor = Some(spawn_descendant_monitor(Arc::clone(&tracked), stop.clone()));
        Ok(Self {
            root_id: child_id,
            tracked,
            stop,
            monitor,
            active: true,
        })
    }

    /// 功能：返回 Linux 普通进程组对应的公共 containment 等级。
    ///
    /// 输入：已成功建立的 guard。
    /// 输出：固定 `unix_process_group`。
    /// 不变量：`/proc` 采样只用于 best-effort cleanup，不能升级 wire containment claim。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    const fn containment(&self) -> &'static str {
        "unix_process_group"
    }

    /// 功能：仅在根 PID 仍匹配启动 start-time 时向其原进程组发送一个封闭 Unix 信号。
    ///
    /// 输入：当前 guard 与调用方已限制的 nix Signal。
    /// 输出：成功发送返回 true；根已退出/身份变化/guard inactive 返回 false。
    /// 不变量：每次 killpg 前重读 `/proc/<pid>/stat`，绝不向复用的旧 PID/PGID 裸发信号。
    /// 失败：锁中毒、PID overflow 或非 ESRCH 信号错误转换为 std I/O error。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn signal_group_checked(
        &self,
        signal: nix::sys::signal::Signal,
    ) -> std::io::Result<bool> {
        if !self.active {
            return Ok(false);
        }
        let pid = i32::try_from(self.root_id)
            .map_err(|_| std::io::Error::other("process-group pid overflow"))?;
        let expected = self
            .tracked
            .lock()
            .map_err(|_| std::io::Error::other("descendant guard lock poisoned"))?
            .get(&pid)
            .copied()
            .ok_or_else(|| std::io::Error::other("root process identity is missing"))?;
        if !read_proc_identity(pid).is_ok_and(|identity| identity.start_time == expected) {
            return Ok(false);
        }
        match nix::sys::signal::killpg(nix::unistd::Pid::from_raw(pid), signal) {
            Ok(()) => Ok(true),
            Err(nix::errno::Errno::ESRCH) => Ok(false),
            Err(error) => Err(std::io::Error::from_raw_os_error(error as i32)),
        }
    }

    /// 功能：先向 Linux 根进程组与已追踪后代发送 SIGTERM，经过有界 grace 后复用强制整树清理。
    ///
    /// 输入：调用方请求的 grace；内部限制为 10 毫秒至 1 秒。
    /// 输出：TERM 阶段和最终 STOP/KILL/reap 信号阶段均完成后返回。
    /// 不变量：持续 `/proc` monitor 在 grace 内保持运行，最终每个 tracked PID 仍按 start-time 重验；绝不根据旧 PID 裸发 fallback 信号。
    /// 失败：进程自然退出、单个信号或 `/proc` 竞态被容忍；本方法不扩大目标集合到未证明的 PID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) async fn terminate_gracefully(&mut self, grace: Duration) {
        if !self.active {
            return;
        }
        let _ = refresh_descendants(&self.tracked);
        let _ = self.signal_group_checked(nix::sys::signal::Signal::SIGTERM);
        signal_tracked(&self.tracked, nix::sys::signal::Signal::SIGTERM);
        tokio::time::sleep(grace.clamp(Duration::from_millis(10), Duration::from_secs(1))).await;
        self.cleanup().await;
    }

    /// 功能：冻结并强制终止仍可证明的根进程组及采样到的后代，然后停止监视任务。
    ///
    /// 输入：当前 guard 的独占可变引用。
    /// 输出：已采样身份被发送 SIGSTOP/SIGKILL，监视任务已回收。
    /// 不变量：每次发信号前重验 PID start-time；5ms 采样可能漏掉 adversarial 逃逸，wire 只声明 `unix_process_group`。
    /// 失败：进程竞态退出和单个 `/proc` 条目消失被容忍；无法读取全局 `/proc` 时仍杀根组和已知身份。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) async fn cleanup(&mut self) {
        if !self.active {
            return;
        }
        self.stop.cancel();
        if let Some(monitor) = self.monitor.take() {
            let _ = monitor.await;
        }
        let _ = self.signal_group_checked(nix::sys::signal::Signal::SIGSTOP);
        for _ in 0..3 {
            let _ = refresh_descendants(&self.tracked);
            signal_tracked(&self.tracked, nix::sys::signal::Signal::SIGSTOP);
            tokio::time::sleep(Duration::from_millis(2)).await;
        }
        let _ = self.signal_group_checked(nix::sys::signal::Signal::SIGKILL);
        signal_tracked(&self.tracked, nix::sys::signal::Signal::SIGKILL);
        for _ in 0..2 {
            tokio::time::sleep(Duration::from_millis(5)).await;
            let _ = refresh_descendants(&self.tracked);
            signal_tracked(&self.tracked, nix::sys::signal::Signal::SIGKILL);
        }
        self.active = false;
    }
}

#[cfg(target_os = "linux")]
impl Drop for ProcessTreeGuard {
    /// 功能：在执行 future 被异常丢弃时同步终止已知 Linux 后代，防止 detached 进程继续运行。
    ///
    /// 输入：仍可能活跃的 guard。
    /// 输出：取消监视并向根组、已知精确身份发送 SIGKILL。
    /// 不变量：不等待异步任务，不向 start-time 已变化的复用 PID 发信号。
    /// 失败：Drop 不能报告信号错误，所有竞态均失败关闭为尽力清理。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn drop(&mut self) {
        if !self.active {
            return;
        }
        self.stop.cancel();
        let _ = self.signal_group_checked(nix::sys::signal::Signal::SIGKILL);
        signal_tracked(&self.tracked, nix::sys::signal::Signal::SIGKILL);
    }
}

#[cfg(target_os = "linux")]
/// 功能：启动有限频率的 `/proc` 后代发现任务，记录逃离原进程组但仍具父子链的进程身份。
///
/// 输入：共享 PID/start-time 表和停止令牌。
/// 输出：可等待的 Tokio task handle。
/// 不变量：任务不保留输出、环境或命令行；5ms 轮询只记录观察到的整数身份，停止后不会继续扫描，不能作为完备 containment 证明。
/// 失败：临时扫描失败会等待下一轮，最终清理仍使用已记录集合。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn spawn_descendant_monitor(
    tracked: Arc<Mutex<BTreeMap<i32, u64>>>,
    stop: CancellationToken,
) -> JoinHandle<()> {
    tokio::spawn(async move {
        loop {
            let _ = refresh_descendants(&tracked);
            tokio::select! {
                () = stop.cancelled() => break,
                () = tokio::time::sleep(Duration::from_millis(5)) => {}
            }
        }
    })
}

#[cfg(target_os = "linux")]
/// 功能：从一次 `/proc` 快照递归扩展已知后代集合，并用父 PID start-time 防止 PID 复用误归属。
///
/// 输入：共享的已知 PID/start-time 表。
/// 输出：扫描成功返回空值，表内新增所有当前可证明的传递后代。
/// 不变量：只沿精确父身份扩展，不读取 argv、环境或文件内容。
/// 失败：`/proc` 根不可读或锁已中毒时返回 I/O 错误；单进程竞态消失会被跳过。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn refresh_descendants(tracked: &Arc<Mutex<BTreeMap<i32, u64>>>) -> std::io::Result<()> {
    let snapshot = proc_snapshot()?;
    let mut tracked = tracked
        .lock()
        .map_err(|_| std::io::Error::other("descendant guard lock poisoned"))?;
    let mut changed = true;
    while changed {
        changed = false;
        for process in snapshot.values() {
            if tracked.contains_key(&process.pid) {
                continue;
            }
            let Some(parent_start) = tracked.get(&process.parent_pid) else {
                continue;
            };
            if snapshot
                .get(&process.parent_pid)
                .is_some_and(|parent| parent.start_time == *parent_start)
            {
                tracked.insert(process.pid, process.start_time);
                changed = true;
            }
        }
    }
    Ok(())
}

#[cfg(target_os = "linux")]
/// 功能：读取 Linux `/proc` 中当前可见进程的 PID、PPID 和 start-time 快照。
///
/// 输入：宿主只读 `/proc`。
/// 输出：以 PID 索引的身份表。
/// 不变量：不跟随任意用户路径、不读取环境或命令行；无法解析的竞态条目被跳过。
/// 失败：`/proc` 根目录不可读时返回 I/O 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn proc_snapshot() -> std::io::Result<BTreeMap<i32, ProcIdentity>> {
    let mut snapshot = BTreeMap::new();
    for entry in std::fs::read_dir("/proc")? {
        let Ok(entry) = entry else {
            continue;
        };
        let Some(name) = entry.file_name().to_str().map(str::to_owned) else {
            continue;
        };
        let Ok(pid) = name.parse::<i32>() else {
            continue;
        };
        if let Ok(identity) = read_proc_identity(pid) {
            snapshot.insert(pid, identity);
        }
    }
    Ok(snapshot)
}

#[cfg(target_os = "linux")]
/// 功能：严格解析一个 `/proc/<pid>/stat` 的 PPID 与不可复用 start-time 身份。
///
/// 输入：正 Linux PID。
/// 输出：包含 pid、parent_pid 和字段 22 start_time 的身份。
/// 不变量：使用最后一个右括号跳过可含空格/括号的 comm 字段，不依赖进程名。
/// 失败：进程竞态消失、字段缺失或数字非法时返回 InvalidData/I/O 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn read_proc_identity(pid: i32) -> std::io::Result<ProcIdentity> {
    let stat = std::fs::read_to_string(format!("/proc/{pid}/stat"))?;
    let end = stat
        .rfind(')')
        .ok_or_else(|| std::io::Error::new(std::io::ErrorKind::InvalidData, "missing comm"))?;
    let fields = stat[end + 1..].split_whitespace().collect::<Vec<_>>();
    if fields.len() <= 19 {
        return Err(std::io::Error::new(
            std::io::ErrorKind::InvalidData,
            "short proc stat",
        ));
    }
    let parent_pid = fields[1]
        .parse::<i32>()
        .map_err(|_| std::io::Error::new(std::io::ErrorKind::InvalidData, "invalid ppid"))?;
    let start_time = fields[19]
        .parse::<u64>()
        .map_err(|_| std::io::Error::new(std::io::ErrorKind::InvalidData, "invalid start time"))?;
    Ok(ProcIdentity {
        pid,
        parent_pid,
        start_time,
    })
}

#[cfg(target_os = "linux")]
/// 功能：向仍匹配记录 start-time 的 Linux 后代集合发送指定信号。
///
/// 输入：共享身份表和 SIGSTOP/SIGKILL。
/// 输出：尽力完成全部信号发送。
/// 不变量：每个 PID 在信号前重新读取并比对 start-time，拒绝命中复用 PID。
/// 失败：进程已退出、权限或 `/proc` 竞态被安全忽略，调用方还会杀根组并重复扫描。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn signal_tracked(tracked: &Arc<Mutex<BTreeMap<i32, u64>>>, signal: nix::sys::signal::Signal) {
    let identities = match tracked.lock() {
        Ok(tracked) => tracked.clone(),
        Err(_) => return,
    };
    for (pid, start_time) in identities {
        if read_proc_identity(pid).is_ok_and(|identity| identity.start_time == start_time) {
            let _ = nix::sys::signal::kill(nix::unistd::Pid::from_raw(pid), signal);
        }
    }
}

#[cfg(target_os = "linux")]
/// 功能：构造 Linux 后代 containment 无法安全建立时的稳定错误。
///
/// 输入：不含路径、命令或环境值的诊断文本。
/// 输出：`-32603/process_tree_unavailable` 错误。
/// 不变量：调用方必须在返回前终止已启动的直接进程组。
/// 失败：构造本身不失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn process_tree_error(message: &str) -> AgentError {
    AgentError::new(ErrorCode::InternalError, message)
        .with_details(serde_json::json!({"kind":"process_tree_unavailable"}))
}

#[cfg(all(unix, not(target_os = "linux")))]
pub(crate) struct ProcessTreeGuard {
    child_id: u32,
}

#[cfg(all(unix, not(target_os = "linux")))]
impl ProcessTreeGuard {
    /// 功能：在非 Linux Unix 平台记录直接进程组，当前只提供协作式组终止。
    ///
    /// 输入：直接子 PID/PGID。
    /// 输出：普通 Unix 进程组 guard。
    /// 不变量：不得据此广告 adversarial descendant guard。
    /// 失败：PID 转换无效时返回 process_tree_unavailable。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn start(child_id: u32) -> Result<Self, AgentError> {
        i32::try_from(child_id).map_err(|_| {
            AgentError::new(ErrorCode::InternalError, "child pid overflow")
                .with_details(serde_json::json!({"kind":"process_tree_unavailable"}))
        })?;
        Ok(Self { child_id })
    }

    /// 功能：返回普通 Unix 进程组 containment 等级。
    ///
    /// 输入：当前 guard。
    /// 输出：`unix_process_group`。
    /// 不变量：不声称阻止 setsid 逃逸。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    const fn containment(&self) -> &'static str {
        "unix_process_group"
    }

    /// 功能：在非 Linux Unix 普通进程组上发送封闭信号，供平台代码保持统一接口。
    ///
    /// 输入：当前 guard 与 nix Signal。
    /// 输出：发送成功为 true，目标已退出为 false。
    /// 不变量：该平台没有 start-time identity 证明，terminal 能力因此保持禁用且不得据此外推安全声明。
    /// 失败：PID overflow 或非 ESRCH 信号错误转换为 std I/O error。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn signal_group_checked(
        &self,
        signal: nix::sys::signal::Signal,
    ) -> std::io::Result<bool> {
        let pid = i32::try_from(self.child_id)
            .map_err(|_| std::io::Error::other("process-group pid overflow"))?;
        match nix::sys::signal::killpg(nix::unistd::Pid::from_raw(pid), signal) {
            Ok(()) => Ok(true),
            Err(nix::errno::Errno::ESRCH) => Ok(false),
            Err(error) => Err(std::io::Error::from_raw_os_error(error as i32)),
        }
    }

    /// 功能：在非 Linux Unix 上对直接进程组执行 TERM→有界 grace→KILL。
    ///
    /// 输入：调用方请求的 grace；内部限制为 10 毫秒至 1 秒。
    /// 输出：协作式进程组 TERM 与最终 KILL 均已尽力发送。
    /// 不变量：只操作本次创建的正 PGID；不据此声称处理 setsid/double-fork 逃逸。
    /// 失败：进程自然退出或信号失败被容忍，本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) async fn terminate_gracefully(&mut self, grace: Duration) {
        if let Ok(pid) = i32::try_from(self.child_id) {
            let _ = nix::sys::signal::killpg(
                nix::unistd::Pid::from_raw(pid),
                nix::sys::signal::Signal::SIGTERM,
            );
        }
        tokio::time::sleep(grace.clamp(Duration::from_millis(10), Duration::from_secs(1))).await;
        self.cleanup().await;
    }

    /// 功能：终止非 Linux Unix 的直接进程组。
    ///
    /// 输入：当前 guard。
    /// 输出：尽力发送 SIGKILL。
    /// 不变量：只操作本次创建的 PGID。
    /// 失败：进程竞态退出被容忍。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) async fn cleanup(&mut self) {
        terminate_process_group(self.child_id);
    }
}

#[cfg(not(unix))]
pub(crate) struct ProcessTreeGuard;

#[cfg(not(unix))]
impl ProcessTreeGuard {
    /// 功能：在尚未接入 suspended Job Object 的平台只建立诚实的直接进程占位 guard。
    ///
    /// 输入：直接子 PID。
    /// 输出：direct_process guard；该平台不会向 daemon capability 广告 process/shell。
    /// 不变量：不得据此宣称进程树安全。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn start(_child_id: u32) -> Result<Self, AgentError> {
        Ok(Self)
    }

    /// 功能：返回诚实的直接进程 containment 等级。
    ///
    /// 输入：当前 guard。
    /// 输出：`direct_process`。
    /// 不变量：不声称 Windows Job 或 OS isolation。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    const fn containment(&self) -> &'static str {
        "direct_process"
    }

    /// 功能：保留统一清理接口，直接进程由调用方 Child.kill 回收。
    ///
    /// 输入：当前 guard。
    /// 输出：无。
    /// 不变量：不伪造后代清理。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) async fn cleanup(&mut self) {}
}

#[cfg(test)]
mod tests {
    use std::ffi::OsString;
    use std::process::Stdio;
    use std::time::Duration;

    use tempfile::tempdir;
    use tokio::io::{AsyncBufReadExt, BufReader};
    use tokio::process::Command;
    use tokio_util::sync::CancellationToken;

    use super::{
        ProcessExecutor, ProcessRequest, ProcessTerminationReason, ProcessTreeGuard,
        configure_process_group,
    };

    #[cfg(unix)]
    /// 功能：验证进程执行结果保持 stdout 与 stderr 相互独立并把非零退出归一化为 exit 结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn keeps_stdout_and_stderr_separate() -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let result = ProcessExecutor
            .exec(
                request(
                    directory.path(),
                    "sh",
                    &["-c", "printf out; printf err >&2; exit 7"],
                    1024,
                ),
                CancellationToken::new(),
            )
            .await?;
        assert_eq!(result.stdout.text, "out");
        assert_eq!(result.stderr.text, "err");
        assert_eq!(result.exit_code, Some(7));
        assert_eq!(result.termination_reason, ProcessTerminationReason::Exit);
        Ok(())
    }

    #[cfg(unix)]
    /// 功能：验证超限输出形成带截断字节账的 output_limit 结果，而不是丢失 execution 的异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn enforces_output_limit_with_structured_result() -> Result<(), crate::error::AgentError>
    {
        let directory = tempdir()?;
        let result = ProcessExecutor
            .exec(
                request(directory.path(), "sh", &["-c", "yes x"], 64),
                CancellationToken::new(),
            )
            .await?;
        assert_eq!(
            result.termination_reason,
            ProcessTerminationReason::OutputLimit
        );
        assert!(result.stdout.truncated);
        assert_eq!(
            result.stdout.captured_bytes + result.stdout.omitted_bytes,
            result.stdout.total_bytes
        );
        Ok(())
    }

    #[cfg(unix)]
    /// 功能：验证 bounded stdin 通过独立 pipe 原样交给子进程且不混入 argv。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn writes_bounded_stdin() -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let mut process = request(directory.path(), "sh", &["-c", "cat"], 1024);
        process.stdin = Some("输入保持原样".as_bytes().to_vec());
        let result = ProcessExecutor
            .exec(process, CancellationToken::new())
            .await?;
        assert_eq!(result.stdout.text, "输入保持原样");
        Ok(())
    }

    #[cfg(unix)]
    /// 功能：验证协作取消使用同一整树清理路径并返回 cancelled structured execution。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn cancellation_returns_structured_result() -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let cancellation = CancellationToken::new();
        let trigger = cancellation.clone();
        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(50)).await;
            trigger.cancel();
        });
        let result = ProcessExecutor
            .exec(
                request(directory.path(), "sh", &["-c", "sleep 5"], 1024),
                cancellation,
            )
            .await?;
        assert_eq!(
            result.termination_reason,
            ProcessTerminationReason::Cancelled
        );
        assert_eq!(result.exit_code, None);
        Ok(())
    }

    #[cfg(target_os = "linux")]
    /// 功能：验证 best-effort `/proc` 采样在本固定案例中清理 setsid 后代，但 wire containment 仍诚实降为普通进程组。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn kills_setsid_descendant_before_marker_write() -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let marker = directory.path().join("escaped.marker");
        let script = format!(
            "setsid sh -c 'sleep 0.4; printf escaped > {}' >/dev/null 2>&1 & sleep 5",
            marker.display()
        );
        let mut process = request(directory.path(), "sh", &["-c", &script], 1024);
        process.timeout = Duration::from_millis(100);
        let result = ProcessExecutor
            .exec(process, CancellationToken::new())
            .await?;
        assert_eq!(result.termination_reason, ProcessTerminationReason::Timeout);
        tokio::time::sleep(Duration::from_millis(600)).await;
        assert!(!marker.exists());
        assert_eq!(result.containment, "unix_process_group");
        Ok(())
    }

    #[cfg(target_os = "linux")]
    /// 功能：验证主动 tree cleanup 先交付 SIGTERM 并给协作式 trap 有界退出机会，再进入最终强制清理。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn graceful_tree_cleanup_delivers_term_before_kill()
    -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let mut command = Command::new("sh");
        command
            .arg("-c")
            .arg("trap 'printf term > term.marker; exit 0' TERM; printf 'ready\\n'; sleep 30")
            .current_dir(directory.path())
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .kill_on_drop(true);
        configure_process_group(&mut command);
        let mut child = command.spawn()?;
        let child_id = child.id().ok_or_else(|| {
            crate::error::AgentError::new(
                crate::error::ErrorCode::InternalError,
                "TERM cleanup test child omitted pid",
            )
        })?;
        let stdout = child.stdout.take().ok_or_else(|| {
            crate::error::AgentError::new(
                crate::error::ErrorCode::InternalError,
                "TERM cleanup test child omitted stdout",
            )
        })?;
        let mut line = String::new();
        BufReader::new(stdout).read_line(&mut line).await?;
        assert_eq!(line, "ready\n");
        let mut tree = ProcessTreeGuard::start(child_id)?;
        tree.terminate_gracefully(Duration::from_millis(200)).await;
        let status = tokio::time::timeout(Duration::from_secs(1), child.wait())
            .await
            .map_err(|_| {
                crate::error::AgentError::new(
                    crate::error::ErrorCode::Timeout,
                    "TERM cleanup test child did not exit",
                )
            })??;
        assert!(status.success());
        assert_eq!(
            tokio::fs::read_to_string(directory.path().join("term.marker")).await?,
            "term"
        );
        Ok(())
    }

    #[cfg(target_os = "linux")]
    /// 功能：验证 leader 已 wait/reap 后，checked group signal 因根 start-time 身份消失而拒绝向旧 PGID 发信号。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn reaped_leader_invalidates_process_group_signal() -> Result<(), crate::error::AgentError>
    {
        let directory = tempdir()?;
        let mut command = Command::new("sh");
        command
            .arg("-c")
            .arg("sleep 0.05")
            .current_dir(directory.path())
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .kill_on_drop(true);
        configure_process_group(&mut command);
        let mut child = command.spawn()?;
        let child_id = child.id().ok_or_else(|| {
            crate::error::AgentError::new(
                crate::error::ErrorCode::InternalError,
                "leader identity test child omitted pid",
            )
        })?;
        let mut tree = ProcessTreeGuard::start(child_id)?;
        let status = child.wait().await?;
        assert!(status.success());
        assert!(!tree.signal_group_checked(nix::sys::signal::Signal::SIGKILL)?);
        tree.cleanup().await;
        Ok(())
    }

    /// 功能：构造 executor 单元测试共用的有限 process 请求。
    ///
    /// 输入：临时工作区、可执行文件、argv 切片和总输出上限。
    /// 输出：无 stdin、两秒 timeout、三项输出上限一致的请求。
    /// 不变量：调用方只能在临时目录执行；本方法不启动进程。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn request(
        cwd: &std::path::Path,
        executable: &str,
        args: &[&str],
        output_limit: usize,
    ) -> ProcessRequest {
        ProcessRequest {
            executable: OsString::from(executable),
            args: args.iter().map(OsString::from).collect(),
            cwd: cwd.to_path_buf(),
            stdin: None,
            timeout: Duration::from_secs(2),
            stdout_limit_bytes: output_limit,
            stderr_limit_bytes: output_limit,
            total_output_limit_bytes: output_limit,
        }
    }
}
