//! Linux Bubblewrap hard-sandbox 配置、启动自检与不可变执行快照。
//!
//! 作者：高宏顺 <18272669457@163.com>

use std::ffi::{OsStr, OsString};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use serde_json::{Value, json};

use crate::error::{AgentError, ErrorCode};

pub const LINUX_BWRAP_PROFILE: &str = "linux-bwrap-v1";
const PRODUCTION_BACKEND: &str = "/usr/bin/bwrap";
const PROFILE_ENV: &str = "AGENT_HARD_SANDBOX_PROFILE";
const CONFORMANCE_BACKEND_ENV: &str = "AGENT_HARD_SANDBOX_CONFORMANCE_BACKEND";
const SELF_TEST_TIMEOUT: Duration = Duration::from_secs(20);

/// Linux hard-sandbox 中工作区的显式挂载权限。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WorkspaceAccess {
    ReadOnly,
    ReadWrite,
}

impl WorkspaceAccess {
    /// 功能：返回 Bubblewrap 工作区 FD bind 使用的固定参数。
    ///
    /// 输入：已从封闭 wire 枚举解析的工作区权限。
    /// 输出：`--ro-bind-fd` 或 `--bind-fd`。
    /// 不变量：只影响 `/workspace`，不放宽系统目录或网络策略。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    const fn bind_option(self) -> &'static str {
        match self {
            Self::ReadOnly => "--ro-bind-fd",
            Self::ReadWrite => "--bind-fd",
        }
    }
}

/// 一个已经严格解析、并将参与审批 operation hash 的 hard-sandbox 请求。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct HardSandboxRequest {
    pub workspace_access: WorkspaceAccess,
}

/// 启动期 hard-sandbox 状态；Unavailable 只保留失败关闭事实，不保留宿主诊断。
#[derive(Debug, Clone, Default)]
pub enum HardSandboxState {
    #[default]
    Disabled,
    Ready(Arc<LinuxBwrapSandbox>),
    Unavailable,
}

impl HardSandboxState {
    /// 功能：按冻结环境门控初始化可选 `linux-bwrap-v1` 并完成真实启动自检。
    ///
    /// 输入：canonical workspace、CLI `--conformance` 门及 `QXNM_FORGE_CONFORMANCE=1` 门。
    /// 输出：未配置时 Disabled，完整自检通过时 Ready，其余所有显式 presence/失败均为 Unavailable。
    /// 不变量：生产 backend 固定 `/usr/bin/bwrap`；override 只有双 conformance 门同时成立才读取；任何失败不降级为宿主执行。
    /// 失败：外部失败被收敛为 Unavailable，避免在日志、协议或 Session 中泄露路径与 OS 诊断。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn from_environment(
        workspace: &Path,
        cli_conformance: bool,
        environment_conformance: bool,
    ) -> Self {
        let profile = std::env::var_os(PROFILE_ENV);
        let backend_override = std::env::var_os(CONFORMANCE_BACKEND_ENV);
        let backend = match select_backend(
            profile.as_deref(),
            backend_override.as_deref(),
            cli_conformance,
            environment_conformance,
        ) {
            Ok(None) => return Self::Disabled,
            Ok(Some(backend)) => backend,
            Err(()) => return Self::Unavailable,
        };
        match tokio::time::timeout(
            SELF_TEST_TIMEOUT,
            LinuxBwrapSandbox::initialize(backend, workspace),
        )
        .await
        {
            Ok(Ok(backend)) => Self::Ready(Arc::new(backend)),
            Ok(Err(_)) | Err(_) => Self::Unavailable,
        }
    }

    /// 功能：返回 initialize 可广告的完整 hard-sandbox capability，或显式配置失败。
    ///
    /// 输入：当前不可变启动状态。
    /// 输出：Disabled 返回 None；Ready 返回 Schema 完整 capability；Unavailable 返回 `-32603/sandbox_unavailable`。
    /// 不变量：只有当前进程完成 startup self-test 才能产生 `selfTested:true`；不会返回乐观布尔值。
    /// 失败：显式 profile、backend 或 self-test 失败统一为不可重试 `sandbox_unavailable`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn capability(&self) -> Result<Option<Value>, AgentError> {
        match self {
            Self::Disabled => Ok(None),
            Self::Ready(_) => Ok(Some(json!({
                "profile":LINUX_BWRAP_PROFILE,
                "platform":"linux",
                "filesystem":"mount_namespace_allowlist",
                "network":"network_namespace_isolated",
                "process":"pid_namespace_die_with_parent",
                "credentials":"empty_environment",
                "execution":{
                    "timeout":"bounded",
                    "output":"bounded",
                    "descendants":"fork_setsid_terminated_on_cancel_or_parent_exit"
                },
                "failureMode":"sandbox_unavailable_no_host_fallback",
                "selfTested":true
            }))),
            Self::Unavailable => Err(sandbox_unavailable()),
        }
    }

    /// 功能：在执行边界取得已自检 backend，并让 Disabled/Unavailable 请求统一失败关闭。
    ///
    /// 输入：当前启动状态。
    /// 输出：仅 Ready 返回不可变 backend 引用。
    /// 不变量：调用方不得在错误后尝试普通 `ProcessExecutor::exec`；本方法不触发自检或路径查找。
    /// 失败：未配置或不可用均返回 `sandbox_unavailable`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn require_backend(&self) -> Result<&LinuxBwrapSandbox, AgentError> {
        match self {
            Self::Ready(backend) => Ok(backend),
            Self::Disabled | Self::Unavailable => Err(sandbox_unavailable()),
        }
    }
}

/// 已验证 backend、系统 bind 列表和 canonical workspace 身份的不可变执行适配器。
#[derive(Debug)]
pub struct LinuxBwrapSandbox {
    #[cfg(target_os = "linux")]
    backend: PathBuf,
    #[cfg(target_os = "linux")]
    workspace: PathBuf,
    #[cfg(target_os = "linux")]
    workspace_identity: FileIdentity,
    #[cfg(target_os = "linux")]
    system_paths: Vec<PathBuf>,
}

impl LinuxBwrapSandbox {
    /// 功能：验证固定 Bubblewrap 身份、运行版本探针和完整隔离自检，再冻结生产 workspace 身份。
    ///
    /// 输入：配置选择出的绝对 backend 与 daemon canonical workspace。
    /// 输出：仅 backend、隔离、边界、fork/setsid 和 parent-exit 自检全部通过时返回 adapter。
    /// 不变量：自检只使用临时 workspace、回环 listener 和随机 canary；不访问 Provider 或公网；失败后绝不构造 Ready 状态。
    /// 失败：平台、backend、namespace、bind、network、credential、timeout、output 或后代清理任一失败返回 `sandbox_unavailable`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    async fn initialize(backend: PathBuf, workspace: &Path) -> Result<Self, AgentError> {
        validate_trusted_binary(&backend)?;
        let python_probe = Path::new("/usr/bin/python3")
            .canonicalize()
            .map_err(|_| sandbox_unavailable())?;
        validate_trusted_binary(&python_probe)?;
        probe_version(&backend).await?;
        let temporary = tempfile::tempdir().map_err(|_| sandbox_unavailable())?;
        let test_workspace = temporary.path().join("workspace");
        std::fs::create_dir(&test_workspace).map_err(|_| sandbox_unavailable())?;
        let test_backend = Self::freeze(backend.clone(), &test_workspace)?;
        test_backend.run_self_test(temporary.path()).await?;
        Self::freeze(backend, workspace)
    }

    /// 功能：在非 Linux 平台拒绝初始化 Linux 专属 profile。
    ///
    /// 输入：未使用的 backend 与 workspace。
    /// 输出：无成功值。
    /// 不变量：Windows/macOS 不因类型存在而获得 capability。
    /// 失败：始终返回 `sandbox_unavailable`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(not(target_os = "linux"))]
    async fn initialize(_backend: PathBuf, _workspace: &Path) -> Result<Self, AgentError> {
        Err(sandbox_unavailable())
    }

    /// 功能：冻结 canonical workspace 的 device/inode 以及当前存在的只读系统 bind 列表。
    ///
    /// 输入：已验证 backend 和必须存在的 workspace 目录。
    /// 输出：尚未对外可用的不可变 adapter。
    /// 不变量：后续每次执行通过 O_NOFOLLOW directory FD 重验同一 device/inode；从不 bind 宿主 `/`。
    /// 失败：workspace 不可 canonicalize、不是目录或身份不可读时返回 `sandbox_unavailable`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    fn freeze(backend: PathBuf, workspace: &Path) -> Result<Self, AgentError> {
        use std::os::unix::fs::MetadataExt;

        let workspace = workspace
            .canonicalize()
            .map_err(|_| sandbox_unavailable())?;
        let metadata = std::fs::metadata(&workspace).map_err(|_| sandbox_unavailable())?;
        if !metadata.is_dir() {
            return Err(sandbox_unavailable());
        }
        let system_paths = ["/usr", "/bin", "/lib", "/lib64"]
            .into_iter()
            .map(PathBuf::from)
            .filter(|path| path.exists())
            .collect();
        Ok(Self {
            backend,
            workspace,
            workspace_identity: FileIdentity {
                device: metadata.dev(),
                inode: metadata.ino(),
            },
            system_paths,
        })
    }

    /// 功能：为一次已批准请求构造只含固定 namespace、FD bind、空环境和原始用户 argv 的 Bubblewrap 命令。
    ///
    /// 输入：原生 process 请求、显式 workspace access 和仅供 startup self-test 的可选随机 canary。
    /// 输出：持有 CLOEXEC workspace/status FD 的单次命令；调用方必须在 spawn child 中原子清除这些 FD 的 CLOEXEC。
    /// 不变量：cwd 必须位于冻结 workspace；系统只读、网络隔离；workspace 使用身份重验 FD 而非竞态路径；不包含 host fallback 分支。
    /// 失败：backend/workspace 身份变化、FD 建立、cwd 越界或请求边界异常返回 `sandbox_unavailable`/`executor_invalid`，用户代码尚未启动。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    pub(crate) fn prepare_command(
        &self,
        request: &crate::executor::ProcessRequest,
        sandbox: HardSandboxRequest,
        host_canary: Option<&OsStr>,
    ) -> Result<SandboxCommand, AgentError> {
        use std::os::fd::AsRawFd;

        use nix::fcntl::{OFlag, open};
        use nix::sys::stat::{Mode, fstat};
        use nix::unistd::pipe2;

        validate_trusted_binary(&self.backend)?;
        let relative_cwd = request
            .cwd
            .strip_prefix(&self.workspace)
            .map_err(|_| executor_invalid())?;
        let workspace_fd = open(
            &self.workspace,
            OFlag::O_PATH | OFlag::O_DIRECTORY | OFlag::O_NOFOLLOW | OFlag::O_CLOEXEC,
            Mode::empty(),
        )
        .map_err(|_| sandbox_unavailable())?;
        let stat = fstat(&workspace_fd).map_err(|_| sandbox_unavailable())?;
        if stat.st_dev != self.workspace_identity.device
            || stat.st_ino != self.workspace_identity.inode
        {
            return Err(sandbox_unavailable());
        }
        let (status_read, status_write) =
            pipe2(OFlag::O_CLOEXEC).map_err(|_| sandbox_unavailable())?;
        let mut args = vec![
            OsString::from("--unshare-user"),
            OsString::from("--unshare-pid"),
            OsString::from("--unshare-net"),
            OsString::from("--unshare-ipc"),
            OsString::from("--unshare-uts"),
            OsString::from("--unshare-cgroup-try"),
            OsString::from("--die-with-parent"),
            OsString::from("--new-session"),
            OsString::from("--proc"),
            OsString::from("/proc"),
            OsString::from("--dev"),
            OsString::from("/dev"),
            OsString::from("--tmpfs"),
            OsString::from("/tmp"),
        ];
        for path in &self.system_paths {
            args.extend([
                OsString::from("--ro-bind"),
                path.as_os_str().to_owned(),
                path.as_os_str().to_owned(),
            ]);
        }
        args.extend([
            OsString::from(sandbox.workspace_access.bind_option()),
            OsString::from(workspace_fd.as_raw_fd().to_string()),
            OsString::from("/workspace"),
            OsString::from("--chdir"),
            Path::new("/workspace").join(relative_cwd).into_os_string(),
            OsString::from("--clearenv"),
            OsString::from("--setenv"),
            OsString::from("PATH"),
            OsString::from("/usr/bin:/bin"),
            OsString::from("--json-status-fd"),
            OsString::from(status_write.as_raw_fd().to_string()),
            OsString::from("/usr/bin/env"),
            OsString::from("-i"),
            OsString::from("PATH=/usr/bin:/bin"),
            map_executable(&request.executable, &self.workspace),
        ]);
        args.extend(request.args.iter().cloned());
        Ok(SandboxCommand {
            executable: self.backend.as_os_str().to_owned(),
            args,
            cwd: PathBuf::from("/"),
            status_read: Some(status_read),
            status_write: Some(status_write),
            workspace_fd: Some(workspace_fd),
            host_canary: host_canary.map(OsStr::to_owned),
        })
    }

    /// 功能：在临时工作区真实验证文件、网络、环境、边界、后代取消与 parent-exit 维度。
    ///
    /// 输入：仅当前 self-test 拥有的临时根。
    /// 输出：全部 ADR0016 维度成立时成功。
    /// 不变量：探针只连接宿主随机 loopback listener，不访问公网；所有用户代码均在当前 backend 内运行。
    /// 失败：任一断言失败统一返回 `sandbox_unavailable`，且清理路径不执行同一代码的 host fallback。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    async fn run_self_test(&self, temporary_root: &Path) -> Result<(), AgentError> {
        self.run_isolation_test(temporary_root, WorkspaceAccess::ReadOnly)
            .await?;
        self.run_isolation_test(temporary_root, WorkspaceAccess::ReadWrite)
            .await?;
        self.run_tree_test("timeout").await?;
        self.run_tree_test("output").await?;
        self.run_parent_exit_test().await
    }

    /// 功能：真实验证一个只读或读写 workspace 的 mount/network/credential/cwd/argv 隔离。
    ///
    /// 输入：self-test 临时根和封闭 workspace access。
    /// 输出：固定 `isolated` 结果及宿主可见写入状态符合预期时成功。
    /// 不变量：outside canary 位于 bind 外；network 只探测 runner 自建 loopback listener；随机 credential 只进入 bwrap 宿主环境。
    /// 失败：setup、脚本、输出或任一隔离断言失败返回 `sandbox_unavailable`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    async fn run_isolation_test(
        &self,
        temporary_root: &Path,
        access: WorkspaceAccess,
    ) -> Result<(), AgentError> {
        use tokio::net::{TcpListener, TcpStream};

        let inside = self.workspace.join("inside.txt");
        std::fs::write(&inside, b"inside").map_err(|_| sandbox_unavailable())?;
        let child_cwd = self.workspace.join("sub");
        std::fs::create_dir_all(&child_cwd).map_err(|_| sandbox_unavailable())?;
        let outside = temporary_root.join(format!("outside-{}", uuid::Uuid::new_v4()));
        std::fs::write(&outside, b"outside").map_err(|_| sandbox_unavailable())?;
        let listener = TcpListener::bind("127.0.0.1:0")
            .await
            .map_err(|_| sandbox_unavailable())?;
        let address = listener.local_addr().map_err(|_| sandbox_unavailable())?;
        let positive = TcpStream::connect(address)
            .await
            .map_err(|_| sandbox_unavailable())?;
        drop(positive);
        let expected_write = if access == WorkspaceAccess::ReadWrite {
            "yes"
        } else {
            "no"
        };
        let network_code = "import socket,sys;client=socket.socket();client.settimeout(.5);result=client.connect_ex(('127.0.0.1',int(sys.argv[1])));client.close();sys.exit(44 if result==0 else 0)";
        let script = format!(
            "set -eu; test \"$(cat /workspace/inside.txt)\" = inside; \
             test \"$PWD\" = /workspace/sub; test \"$1\" = '参数'; \
             if test -e {}; then exit 41; fi; \
             if touch /usr/qxnm-forge-sandbox-write 2>/dev/null; then exit 42; fi; \
             if env | grep -q QXNM_FORGE_SANDBOX_SECRET; then exit 43; fi; \
             /usr/bin/python3 -c {} {}; \
             write=no; if touch /workspace/write.txt 2>/dev/null; then write=yes; fi; \
             test \"$write\" = {}; touch /tmp/works; test -f /tmp/works; printf isolated",
            shell_quote(outside.as_os_str()),
            shell_quote(OsStr::new(network_code)),
            address.port(),
            expected_write,
        );
        let request = crate::executor::ProcessRequest {
            executable: OsString::from("/bin/sh"),
            args: vec![
                OsString::from("-c"),
                OsString::from(script),
                OsString::from("qxnm-forge-self-test"),
                OsString::from("参数"),
            ],
            cwd: child_cwd,
            stdin: None,
            timeout: Duration::from_secs(3),
            stdout_limit_bytes: 65_536,
            stderr_limit_bytes: 65_536,
            total_output_limit_bytes: 65_536,
        };
        let result = crate::executor::ProcessExecutor
            .exec_sandboxed_with_canary(
                request,
                self,
                HardSandboxRequest {
                    workspace_access: access,
                },
                Some(OsStr::new("credential-canary")),
                tokio_util::sync::CancellationToken::new(),
            )
            .await?;
        drop(listener);
        if result.exit_code != Some(0)
            || result.termination_reason != crate::executor::ProcessTerminationReason::Exit
            || result.stdout.text != "isolated"
            || result.containment != "os_isolation"
        {
            return Err(sandbox_unavailable());
        }
        let wrote = self.workspace.join("write.txt").is_file();
        if wrote != (access == WorkspaceAccess::ReadWrite) {
            return Err(sandbox_unavailable());
        }
        if wrote {
            std::fs::remove_file(self.workspace.join("write.txt"))
                .map_err(|_| sandbox_unavailable())?;
        }
        Ok(())
    }

    /// 功能：触发 timeout 或 output-limit，并用延迟 marker 证明 fork/setsid 后代未逃逸。
    ///
    /// 输入：固定 `timeout` 或 `output` 模式。
    /// 输出：相应边界触发且延迟 marker 未出现时成功。
    /// 不变量：探针只在临时 read-write workspace 内执行；取消后绝不在 host 重新运行探针。
    /// 失败：模式非法、边界未触发或后代存活返回 `sandbox_unavailable`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    async fn run_tree_test(&self, mode: &str) -> Result<(), AgentError> {
        if !matches!(mode, "timeout" | "output") {
            return Err(sandbox_unavailable());
        }
        let survived_name = format!("{mode}.survived");
        let ready_name = format!("{mode}.ready");
        let code = "import os,sys,time\nready=sys.argv[1]\nsurvived=sys.argv[2]\nmode=sys.argv[3]\nchild=os.fork()\nif child==0:\n os.setsid()\n open(ready,'w').write(str(os.getpid()))\n time.sleep(.8)\n open(survived,'w').write('escaped')\n os._exit(0)\ndeadline=time.monotonic()+.5\nwhile not os.path.exists(ready) and time.monotonic()<deadline: time.sleep(.005)\nif mode=='output':\n chunk=b'x'*8192\n while True: os.write(1,chunk)\nwhile True: time.sleep(.05)\n";
        let request = crate::executor::ProcessRequest {
            executable: OsString::from("/usr/bin/python3"),
            args: vec![
                OsString::from("-c"),
                OsString::from(code),
                OsString::from(format!("/workspace/{ready_name}")),
                OsString::from(format!("/workspace/{survived_name}")),
                OsString::from(mode),
            ],
            cwd: self.workspace.clone(),
            stdin: None,
            timeout: if mode == "timeout" {
                Duration::from_millis(350)
            } else {
                Duration::from_secs(3)
            },
            stdout_limit_bytes: 65_536,
            stderr_limit_bytes: 65_536,
            total_output_limit_bytes: 65_536,
        };
        let result = crate::executor::ProcessExecutor
            .exec_sandboxed(
                request,
                self,
                HardSandboxRequest {
                    workspace_access: WorkspaceAccess::ReadWrite,
                },
                tokio_util::sync::CancellationToken::new(),
            )
            .await?;
        let expected = if mode == "timeout" {
            crate::executor::ProcessTerminationReason::Timeout
        } else {
            crate::executor::ProcessTerminationReason::OutputLimit
        };
        if result.termination_reason != expected || !self.workspace.join(&ready_name).is_file() {
            return Err(sandbox_unavailable());
        }
        tokio::time::sleep(Duration::from_millis(900)).await;
        if self.workspace.join(&survived_name).exists() {
            return Err(sandbox_unavailable());
        }
        let _ = std::fs::remove_file(self.workspace.join(ready_name));
        Ok(())
    }

    /// 功能：让 Bubblewrap 的直接 helper 父进程在探针就绪后退出，并验证 setsid 后代随之消失。
    ///
    /// 输入：当前已验证的临时 self-test adapter。
    /// 输出：`--die-with-parent` 使 bwrap 身份消失且延迟 survivor marker 不出现时成功。
    /// 不变量：helper 只渲染代码固定的 bwrap argv；失败清理按 PID/start-time 重验，不裸杀复用 PID；没有 host fallback。
    /// 失败：helper、marker、身份消失或后代清理任一超时返回 `sandbox_unavailable`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    async fn run_parent_exit_test(&self) -> Result<(), AgentError> {
        use std::process::Stdio;

        let ready = self.workspace.join("parent.ready");
        let survived = self.workspace.join("parent.survived");
        let control = self.workspace.join("parent.control");
        let code = "import os,time\nchild=os.fork()\nif child==0:\n os.setsid()\n open('/workspace/parent.ready','w').write(str(os.getpid()))\n time.sleep(.8)\n open('/workspace/parent.survived','w').write('escaped')\n os._exit(0)\nwhile True: time.sleep(.05)\n";
        let command = self.path_bound_test_command([
            OsString::from("/usr/bin/python3"),
            OsString::from("-c"),
            OsString::from(code),
        ]);
        let rendered = command
            .iter()
            .map(|argument| shell_quote(argument))
            .collect::<Vec<_>>()
            .join(" ");
        let script = format!(
            "{rendered} >/dev/null 2>&1 & child=$!; printf '%s' \"$child\" > {}; \
             n=0; while test ! -s {} && test \"$n\" -lt 200; do \
             if ! kill -0 \"$child\" 2>/dev/null; then exit 71; fi; n=$((n+1)); sleep .01; done; test -s {}",
            shell_quote(control.as_os_str()),
            shell_quote(ready.as_os_str()),
            shell_quote(ready.as_os_str()),
        );
        let mut helper = tokio::process::Command::new("/bin/sh");
        helper
            .args(["-c", &script])
            .env_clear()
            .env("PATH", "/usr/bin:/bin")
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .kill_on_drop(true);
        let mut helper = helper.spawn().map_err(|_| sandbox_unavailable())?;
        wait_for_file(&control, Duration::from_secs(2)).await?;
        let pid = std::fs::read_to_string(&control)
            .ok()
            .and_then(|value| value.parse::<i32>().ok())
            .filter(|pid| *pid > 1)
            .ok_or_else(sandbox_unavailable)?;
        let identity = read_process_identity(pid).ok_or_else(sandbox_unavailable)?;
        let status = tokio::time::timeout(Duration::from_secs(2), helper.wait())
            .await
            .map_err(|_| sandbox_unavailable())?
            .map_err(|_| sandbox_unavailable())?;
        if !status.success() {
            terminate_identity(identity);
            return Err(sandbox_unavailable());
        }
        wait_identity_gone(identity, Duration::from_secs(2)).await?;
        tokio::time::sleep(Duration::from_millis(900)).await;
        if survived.exists() {
            return Err(sandbox_unavailable());
        }
        Ok(())
    }

    /// 功能：构造 parent-exit self-test 专用的临时路径 bind Bubblewrap argv。
    ///
    /// 输入：代码固定的用户命令 argv。
    /// 输出：完整 namespace、只读系统、临时 workspace bind、空环境的 argv。
    /// 不变量：仅用于进程自建 tempdir self-test；生产请求始终使用 FD bind；不 bind 宿主 `/`。
    /// 失败：本方法不返回错误；backend/namespace 失败由 helper 状态与 marker 门禁发现。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    fn path_bound_test_command(
        &self,
        command: impl IntoIterator<Item = OsString>,
    ) -> Vec<OsString> {
        let mut result = vec![
            self.backend.as_os_str().to_owned(),
            OsString::from("--unshare-user"),
            OsString::from("--unshare-pid"),
            OsString::from("--unshare-net"),
            OsString::from("--unshare-ipc"),
            OsString::from("--unshare-uts"),
            OsString::from("--unshare-cgroup-try"),
            OsString::from("--die-with-parent"),
            OsString::from("--new-session"),
            OsString::from("--proc"),
            OsString::from("/proc"),
            OsString::from("--dev"),
            OsString::from("/dev"),
            OsString::from("--tmpfs"),
            OsString::from("/tmp"),
        ];
        for path in &self.system_paths {
            result.extend([
                OsString::from("--ro-bind"),
                path.as_os_str().to_owned(),
                path.as_os_str().to_owned(),
            ]);
        }
        result.extend([
            OsString::from("--bind"),
            self.workspace.as_os_str().to_owned(),
            OsString::from("/workspace"),
            OsString::from("--chdir"),
            OsString::from("/workspace"),
            OsString::from("--clearenv"),
            OsString::from("--setenv"),
            OsString::from("PATH"),
            OsString::from("/usr/bin:/bin"),
            OsString::from("/usr/bin/env"),
            OsString::from("-i"),
            OsString::from("PATH=/usr/bin:/bin"),
        ]);
        result.extend(command);
        result
    }
}

#[cfg(target_os = "linux")]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct FileIdentity {
    device: u64,
    inode: u64,
}

/// 一次 Bubblewrap spawn 前持有的 argv、workspace FD 与 JSON status pipe。
#[cfg(target_os = "linux")]
pub(crate) struct SandboxCommand {
    pub(crate) executable: OsString,
    pub(crate) args: Vec<OsString>,
    pub(crate) cwd: PathBuf,
    status_read: Option<std::os::fd::OwnedFd>,
    status_write: Option<std::os::fd::OwnedFd>,
    workspace_fd: Option<std::os::fd::OwnedFd>,
    pub(crate) host_canary: Option<OsString>,
}

#[cfg(target_os = "linux")]
impl SandboxCommand {
    /// 功能：返回 child pre-exec 必须原子清除 CLOEXEC 的固定 FD 集合。
    ///
    /// 输入：尚未 spawn 的单次命令。
    /// 输出：JSON status write FD 与 workspace O_PATH FD。
    /// 不变量：read FD 不继承；所有返回 FD 仍由父进程拥有且带 CLOEXEC。
    /// 失败：内部 FD 已被消费时返回 `sandbox_unavailable`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn inherited_fds(&self) -> Result<[std::os::fd::RawFd; 2], AgentError> {
        use std::os::fd::AsRawFd;

        Ok([
            self.status_write
                .as_ref()
                .ok_or_else(sandbox_unavailable)?
                .as_raw_fd(),
            self.workspace_fd
                .as_ref()
                .ok_or_else(sandbox_unavailable)?
                .as_raw_fd(),
        ])
    }

    /// 功能：spawn 成功后关闭父进程 write/workspace FD，并转交唯一 status read FD。
    ///
    /// 输入：已成功 spawn 的单次命令。
    /// 输出：用于判断 setup 与用户 exit 边界的 status read FD。
    /// 不变量：父进程不保留 pipe 写端，bwrap 退出后 reader 必然观察 EOF；workspace FD 不泄露到后续 spawn。
    /// 失败：重复消费返回 `sandbox_unavailable`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn after_spawn(&mut self) -> Result<std::os::fd::OwnedFd, AgentError> {
        drop(self.status_write.take());
        drop(self.workspace_fd.take());
        self.status_read.take().ok_or_else(sandbox_unavailable)
    }
}

/// 功能：读取 Bubblewrap 有界 JSON status，并判断用户 command 是否实际产生 exit 记录。
///
/// 输入：当前 bwrap 独占的 status read FD。
/// 输出：至少一个严格 JSON object 中存在整数 `exit-code` 时为 true。
/// 不变量：最多读取 8193 字节；malformed/超界状态绝不被当作 setup 成功；不解析 stderr 文本。
/// 失败：I/O、UTF-8、JSON 或边界异常返回 `sandbox_unavailable`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[cfg(target_os = "linux")]
pub(crate) async fn command_exit_observed(
    status_read: std::os::fd::OwnedFd,
) -> Result<bool, AgentError> {
    use tokio::io::AsyncReadExt;

    let file = std::fs::File::from(status_read);
    let mut stream = tokio::fs::File::from_std(file).take(8_193);
    let mut bytes = Vec::new();
    stream
        .read_to_end(&mut bytes)
        .await
        .map_err(|_| sandbox_unavailable())?;
    if bytes.len() > 8_192 {
        return Err(sandbox_unavailable());
    }
    let text = std::str::from_utf8(&bytes).map_err(|_| sandbox_unavailable())?;
    let mut saw_object = false;
    let mut saw_exit = false;
    for line in text.lines().filter(|line| !line.trim().is_empty()) {
        let value: Value = serde_json::from_str(line).map_err(|_| sandbox_unavailable())?;
        let object = value.as_object().ok_or_else(sandbox_unavailable)?;
        saw_object = true;
        if object.get("exit-code").is_some_and(Value::is_i64) {
            saw_exit = true;
        }
    }
    if !saw_object {
        return Err(sandbox_unavailable());
    }
    Ok(saw_exit)
}

/// 功能：按生产固定路径和 conformance 双门控选择可选 backend。
///
/// 输入：两个环境 presence 的 opaque 值及 CLI/环境 conformance 布尔门。
/// 输出：无 profile 为 None；合法 profile 返回固定/测试路径；非法 presence 返回 Err。
/// 不变量：双门控外永不采用 override 值；unknown/空 profile 与孤立 override 均失败关闭。
/// 失败：返回单位错误，不回显配置值或路径。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn select_backend(
    profile: Option<&OsStr>,
    backend_override: Option<&OsStr>,
    cli_conformance: bool,
    environment_conformance: bool,
) -> Result<Option<PathBuf>, ()> {
    let double_gate = cli_conformance && environment_conformance;
    if backend_override.is_some() && !double_gate {
        return Err(());
    }
    let Some(profile) = profile else {
        return if backend_override.is_some() {
            Err(())
        } else {
            Ok(None)
        };
    };
    if profile != OsStr::new(LINUX_BWRAP_PROFILE) {
        return Err(());
    }
    let backend = if let Some(value) = backend_override {
        PathBuf::from(value)
    } else {
        PathBuf::from(PRODUCTION_BACKEND)
    };
    if !backend.is_absolute() {
        return Err(());
    }
    Ok(Some(backend))
}

/// 功能：验证 backend/probe 为 root-owned、普通、可执行且当前用户不可写的绝对文件。
///
/// 输入：代码固定或双门控选择的绝对路径。
/// 输出：身份策略成立时成功。
/// 不变量：拒绝 symlink、非 root owner、group/other writable、当前用户 writable 与不可执行对象；每次用户执行前重验 backend。
/// 失败：任一 metadata/access 异常统一为 `sandbox_unavailable`，不执行候选文件。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[cfg(target_os = "linux")]
fn validate_trusted_binary(path: &Path) -> Result<(), AgentError> {
    use std::os::unix::fs::{MetadataExt, PermissionsExt};

    use nix::unistd::{AccessFlags, access};

    if !path.is_absolute() {
        return Err(sandbox_unavailable());
    }
    let metadata = std::fs::symlink_metadata(path).map_err(|_| sandbox_unavailable())?;
    let mode = metadata.permissions().mode();
    if !metadata.file_type().is_file()
        || metadata.file_type().is_symlink()
        || metadata.uid() != 0
        || mode & 0o111 == 0
        || mode & 0o022 != 0
        || access(path, AccessFlags::W_OK).is_ok()
        || access(path, AccessFlags::X_OK).is_err()
    {
        return Err(sandbox_unavailable());
    }
    Ok(())
}

/// 功能：在两秒 deadline 内运行可信 backend 的固定 `--version` 探针。
///
/// 输入：已完成 metadata 身份验证的 backend。
/// 输出：进程成功退出时成功。
/// 不变量：argv 固定、环境为空、stdout/stderr 丢弃；不执行 shell或用户代码。
/// 失败：spawn、timeout 或非零退出统一为 `sandbox_unavailable`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[cfg(target_os = "linux")]
async fn probe_version(backend: &Path) -> Result<(), AgentError> {
    use std::process::Stdio;

    let mut command = tokio::process::Command::new(backend);
    command
        .arg("--version")
        .env_clear()
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .kill_on_drop(true);
    let mut child = command.spawn().map_err(|_| sandbox_unavailable())?;
    let status = tokio::time::timeout(Duration::from_secs(2), child.wait())
        .await
        .map_err(|_| sandbox_unavailable())?
        .map_err(|_| sandbox_unavailable())?;
    if status.success() {
        Ok(())
    } else {
        Err(sandbox_unavailable())
    }
}

/// 功能：把 host workspace 内绝对 executable 映射到 sandbox `/workspace`，其余 argv[0] 保持原始边界。
///
/// 输入：已批准 executable 与冻结 canonical workspace。
/// 输出：workspace 内路径的 FD bind 视图路径，或未改写的系统/相对 executable。
/// 不变量：不做 PATH lookup、shell 拼接或宿主外路径授权；不可见宿主路径只会在 sandbox 内正常启动失败。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[cfg(target_os = "linux")]
fn map_executable(executable: &OsStr, workspace: &Path) -> OsString {
    let path = Path::new(executable);
    path.strip_prefix(workspace)
        .map(|relative| Path::new("/workspace").join(relative).into_os_string())
        .unwrap_or_else(|_| executable.to_owned())
}

/// 功能：把内部 self-test 的单个 argv 安全渲染为 POSIX shell 单引号字面量。
///
/// 输入：代码生成或随机临时路径的 OsStr。
/// 输出：不发生变量、命令或 glob 展开的 shell token。
/// 不变量：仅 parent-exit 固定 helper 使用；生产 process.exec 从不调用本函数；非 UTF-8 值失败为安全占位。
/// 失败：本函数不返回错误，非 UTF-8 输入渲染为空 token并使 self-test 失败关闭。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[cfg(target_os = "linux")]
fn shell_quote(value: &OsStr) -> String {
    let text = value.to_str().unwrap_or("");
    format!("'{}'", text.replace('\'', "'\\''"))
}

/// 功能：在有界期限内等待 self-test marker 成为非空普通文件。
///
/// 输入：daemon 自建临时目录中的 marker 和正 timeout。
/// 输出：marker 就绪时成功。
/// 不变量：不跟随 symlink，不接受目录或空文件。
/// 失败：期限结束或 metadata 异常返回 `sandbox_unavailable`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[cfg(target_os = "linux")]
async fn wait_for_file(path: &Path, timeout: Duration) -> Result<(), AgentError> {
    let deadline = tokio::time::Instant::now() + timeout;
    loop {
        if std::fs::symlink_metadata(path)
            .is_ok_and(|metadata| metadata.file_type().is_file() && metadata.len() > 0)
        {
            return Ok(());
        }
        if tokio::time::Instant::now() >= deadline {
            return Err(sandbox_unavailable());
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
}

#[cfg(target_os = "linux")]
#[derive(Debug, Clone, Copy)]
struct ProcessIdentity {
    pid: i32,
    start_time: u64,
}

/// 功能：读取 Linux PID 与 start-time，供 parent-exit self-test 抵御 PID 复用。
///
/// 输入：正宿主 PID。
/// 输出：当前进程身份，竞态消失或解析失败为 None。
/// 不变量：不读取 cmdline/environment；start-time 使用 proc stat 字段 22。
/// 失败：所有 procfs 异常安全返回 None。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[cfg(target_os = "linux")]
fn read_process_identity(pid: i32) -> Option<ProcessIdentity> {
    let text = std::fs::read_to_string(format!("/proc/{pid}/stat")).ok()?;
    let closing = text.rfind(')')?;
    let fields = text[closing + 1..].split_whitespace().collect::<Vec<_>>();
    let start_time = fields.get(19)?.parse().ok()?;
    Some(ProcessIdentity { pid, start_time })
}

/// 功能：在有界期限内按 PID/start-time 等待原 self-test 进程消失。
///
/// 输入：先前记录身份与正 timeout。
/// 输出：身份消失时成功。
/// 不变量：同 PID 新进程不视为旧进程；超时后按身份重验清理。
/// 失败：原身份仍存活时终止它并返回 `sandbox_unavailable`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[cfg(target_os = "linux")]
async fn wait_identity_gone(
    identity: ProcessIdentity,
    timeout: Duration,
) -> Result<(), AgentError> {
    let deadline = tokio::time::Instant::now() + timeout;
    while identity_alive(identity) && tokio::time::Instant::now() < deadline {
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    if identity_alive(identity) {
        terminate_identity(identity);
        Err(sandbox_unavailable())
    } else {
        Ok(())
    }
}

/// 功能：按 PID/start-time 判断同一 self-test 进程是否仍存活。
///
/// 输入：先前记录身份。
/// 输出：PID 与 start-time 均匹配时为 true。
/// 不变量：PID 复用返回 false。
/// 失败：procfs 不可读按已消失处理。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[cfg(target_os = "linux")]
fn identity_alive(identity: ProcessIdentity) -> bool {
    read_process_identity(identity.pid)
        .is_some_and(|current| current.start_time == identity.start_time)
}

/// 功能：只向身份仍匹配的 parent-exit self-test 进程发送 SIGKILL。
///
/// 输入：先前记录身份。
/// 输出：尽力清理，无返回值。
/// 不变量：拒绝 PID 1/当前进程，信号前重验 start-time。
/// 失败：进程竞态退出或权限错误被忽略，调用方仍返回失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[cfg(target_os = "linux")]
fn terminate_identity(identity: ProcessIdentity) {
    if identity.pid > 1
        && identity.pid != i32::try_from(std::process::id()).unwrap_or(-1)
        && identity_alive(identity)
    {
        let _ = nix::sys::signal::kill(
            nix::unistd::Pid::from_raw(identity.pid),
            nix::sys::signal::Signal::SIGKILL,
        );
    }
}

/// 功能：构造不含 backend、workspace、argv 或 OS 诊断的稳定 hard-sandbox 失败。
///
/// 输入：无。
/// 输出：`-32603/sandbox_unavailable`、不可重试错误。
/// 不变量：调用方遇到此错误后不得执行 host fallback。
/// 失败：构造本身不失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) fn sandbox_unavailable() -> AgentError {
    AgentError::new(
        ErrorCode::InternalError,
        "requested hard sandbox is unavailable",
    )
    .with_details(json!({"kind":"sandbox_unavailable"}))
}

/// 功能：构造 cwd 或请求边界在 spawn 前不合法的稳定 executor 错误。
///
/// 输入：无。
/// 输出：`-32602/executor_invalid` 错误。
/// 不变量：不包含 host path 或未信任参数正文。
/// 失败：构造本身不失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[cfg(target_os = "linux")]
fn executor_invalid() -> AgentError {
    AgentError::new(
        ErrorCode::InvalidParams,
        "sandbox executor request is invalid",
    )
    .with_details(json!({"kind":"executor_invalid"}))
}

#[cfg(test)]
mod tests {
    use std::ffi::OsStr;
    use std::path::PathBuf;

    use super::{LINUX_BWRAP_PROFILE, select_backend};

    /// 功能：验证生产 profile 只能选择代码固定的绝对 Bubblewrap 路径。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn production_profile_uses_fixed_backend() {
        assert_eq!(
            select_backend(Some(OsStr::new(LINUX_BWRAP_PROFILE)), None, false, false),
            Ok(Some(PathBuf::from("/usr/bin/bwrap")))
        );
    }

    /// 功能：验证 conformance backend override 缺少任一门或显式 profile 时均失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn conformance_override_requires_both_gates_and_profile() {
        let override_path = Some(OsStr::new("/tmp/bwrap"));
        assert_eq!(
            select_backend(
                Some(OsStr::new(LINUX_BWRAP_PROFILE)),
                override_path,
                true,
                false
            ),
            Err(())
        );
        assert_eq!(select_backend(None, override_path, true, true), Err(()));
        assert_eq!(
            select_backend(
                Some(OsStr::new(LINUX_BWRAP_PROFILE)),
                Some(OsStr::new("/absolute/test-bwrap")),
                true,
                true
            ),
            Ok(Some(PathBuf::from("/absolute/test-bwrap")))
        );
    }

    /// 功能：验证未知或空 profile presence 不会退化为 Disabled 或宿主执行。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn unknown_profile_fails_closed() {
        assert_eq!(
            select_backend(Some(OsStr::new("")), None, false, false),
            Err(())
        );
        assert_eq!(
            select_backend(Some(OsStr::new("other")), None, false, false),
            Err(())
        );
    }
}
