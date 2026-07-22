use std::ffi::OsString;
use std::path::Path;
use std::time::Duration;

use regex::Regex;
use serde_json::{Value, json};
use tokio_util::sync::CancellationToken;
use uuid::Uuid;
use walkdir::WalkDir;

use crate::computer::{ComputerAction, DesktopComputer};
use crate::domain::{ToolDefinition, ToolEffect, ToolOutput};
use crate::error::{AgentError, ErrorCode};
use crate::executor::{
    ProcessExecutor, ProcessRequest, ProcessTerminationReason, ShellKind, ShellRequest,
};
use crate::hard_sandbox::{HardSandboxRequest, HardSandboxState, WorkspaceAccess};
use crate::policy::{PolicyDecision, ToolPolicy, WorkspaceGuard};
use crate::session::SessionStore;

#[cfg(target_os = "linux")]
/// Linux `file.read` 从固定 leaf FD 读取的硬字节上限。
///
/// 该上限独立于 256 KiB 的内联阈值，使 256 KiB 至 1 MiB 的内容仍可转存 artifact，
/// 同时阻止普通文件在检查后增长导致无界内存读取。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) const MAX_FILE_READ_BYTES: usize = 1024 * 1024;

#[derive(Debug, Clone)]
pub struct ToolRegistry {
    guard: WorkspaceGuard,
    policy: ToolPolicy,
    sessions: SessionStore,
    executor: ProcessExecutor,
    hard_sandbox: HardSandboxState,
    computer: Option<DesktopComputer>,
    max_read_bytes: usize,
    max_search_results: usize,
}

struct ExecutionOptions {
    cwd: std::path::PathBuf,
    stdin: Option<Vec<u8>>,
    timeout: Duration,
    output_limit_bytes: usize,
    sandbox: Option<HardSandboxRequest>,
}

impl ToolRegistry {
    /// 功能：使用工作区守卫、权限策略和会话存储构建内置工具注册表。
    ///
    /// 输入：已验证的工作区边界、宿主策略和 artifact/session 存储。
    /// 输出：采用默认读取、搜索及执行输出上限的注册表。
    /// 不变量：工具执行始终先经过策略判断和路径守卫；默认状态不广告 hard sandbox，显式请求会失败关闭而非宿主回退。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn new(guard: WorkspaceGuard, policy: ToolPolicy, sessions: SessionStore) -> Self {
        Self::new_with_hard_sandbox(guard, policy, sessions, HardSandboxState::Disabled)
    }

    /// 功能：使用启动期不可变 hard-sandbox 状态构造工具注册表。
    ///
    /// 输入：已验证工作区守卫、权限策略、Session store 与完成启动自检或失败关闭的 sandbox 状态。
    /// 输出：宿主 process/shell 与显式 `linux-bwrap-v1` 共用审批和结果边界的注册表。
    /// 不变量：Ready 状态只能由 startup self-test 产生；Disabled/Unavailable 的 sandbox 请求绝不调用宿主 executor。
    /// 失败：构造本身不返回错误；Unavailable 由 initialize/request 边界报告 `sandbox_unavailable`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn new_with_hard_sandbox(
        guard: WorkspaceGuard,
        policy: ToolPolicy,
        sessions: SessionStore,
        hard_sandbox: HardSandboxState,
    ) -> Self {
        Self {
            guard,
            policy,
            sessions,
            executor: ProcessExecutor,
            hard_sandbox,
            computer: DesktopComputer::detect(),
            max_read_bytes: 256 * 1024,
            max_search_results: 1_000,
        }
    }

    /// 功能：返回模型可见的内置工具定义及受限 JSON Schema 参数约束。
    ///
    /// 输入：当前工具注册表。
    /// 输出：四个文件工具，以及仅在 Linux 普通进程组与身份重验清理可用时出现的 process.exec/shell.exec 定义。
    /// 不变量：每个工具的副作用等级与实际执行分支保持一致；`/proc` 采样不被描述为 adversarial containment，未实现的平台不广告执行能力。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn definitions(&self) -> Vec<ToolDefinition> {
        let mut definitions = vec![
            definition(
                "file.read",
                "读取工作区内的 UTF-8 文本文件",
                ToolEffect::Read,
                json!({
                    "type":"object",
                    "properties":{"path":{"type":"string"}},
                    "required":["path"],
                    "additionalProperties":false
                }),
            ),
            definition(
                "file.write",
                "原子写入工作区内的 UTF-8 文本文件",
                ToolEffect::Write,
                json!({
                    "type":"object",
                    "properties":{"path":{"type":"string"},"content":{"type":"string"}},
                    "required":["path","content"],
                    "additionalProperties":false
                }),
            ),
            definition(
                "file.edit",
                "精确替换工作区文本文件中的唯一片段",
                ToolEffect::Write,
                json!({
                    "type":"object",
                    "properties":{
                        "path":{"type":"string"},
                        "oldText":{"type":"string"},
                        "newText":{"type":"string"}
                    },
                    "required":["path","oldText","newText"],
                    "additionalProperties":false
                }),
            ),
            definition(
                "search.text",
                "在工作区文本文件中搜索正则表达式",
                ToolEffect::Read,
                json!({
                    "type":"object",
                    "properties":{"pattern":{"type":"string"},"path":{"type":"string"}},
                    "required":["pattern"],
                    "additionalProperties":false
                }),
            ),
        ];
        definitions.extend(executor_definitions());
        if let Some(computer) = self.computer {
            definitions.extend(computer_definitions(computer.interaction_available()));
        }
        definitions
    }

    /// 功能：取得 initialize 使用的完整 hard-sandbox capability，并传播路径测试配置失败。
    ///
    /// 输入：当前不可变注册表状态。
    /// 输出：未配置为 None，自检通过为完整 capability。
    /// 不变量：不在 initialize 时重新探测 backend 或重读路径 hook 配置，不产生乐观 claim。
    /// 失败：路径 conformance 配置无效返回 `conformance_configuration_invalid`；sandbox 配置或自测失败返回 `sandbox_unavailable`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn hard_sandbox_capability(&self) -> Result<Option<Value>, AgentError> {
        self.guard.ensure_path_conformance_ready()?;
        self.hard_sandbox.capability()
    }

    /// 功能：在权限判断、参数校验和取消控制下分派并执行指定工具。
    ///
    /// 输入：工具名、语言中立 JSON 参数以及运行级取消令牌。
    /// 输出：内联文本或 artifact 句柄形式的规范化工具结果。
    /// 不变量：策略拒绝或要求审批时绝不进入实际工具分支；路径访问必须通过工作区守卫。
    /// 失败：未知工具、参数非法、审批/权限拒绝、路径越界、I/O 或执行器错误均返回稳定错误码。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn execute(
        &self,
        session_id: &str,
        name: &str,
        arguments: &Value,
        cancellation: CancellationToken,
    ) -> Result<ToolOutput, AgentError> {
        let effect = self.preflight(name, arguments)?;
        match self.policy.decide(effect) {
            PolicyDecision::Allow => {}
            PolicyDecision::RequireApproval => {
                return Err(AgentError::new(
                    ErrorCode::ApprovalRequired,
                    format!("tool '{name}' requires approval"),
                ));
            }
            PolicyDecision::Deny => {
                return Err(AgentError::new(
                    ErrorCode::PermissionDenied,
                    format!("tool '{name}' is denied by headless policy"),
                ));
            }
        }

        self.execute_authorized(session_id, name, arguments, cancellation)
            .await
    }

    /// 功能：在任何副作用和审批判断前完成工具查找、参数与工作区资源预检。
    ///
    /// 输入：未经信任的工具名和 JSON 参数对象。
    /// 输出：工具存在且参数/资源边界有效时返回规范化副作用等级。
    /// 不变量：本方法不写文件、不启动进程且不执行 shell；所有路径均先通过工作区守卫。
    /// 失败：未知工具、参数形状、正则、路径或资源预检失败时返回稳定结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn preflight(&self, name: &str, arguments: &Value) -> Result<ToolEffect, AgentError> {
        let effect = self.effect(name)?;
        match name {
            "file.read" => {
                require_only(arguments, &["path"])?;
                let path = self
                    .guard
                    .resolve_existing(required_string(arguments, "path")?)?;
                if !path.is_file() {
                    return Err(invalid_arguments("read path must be a file"));
                }
            }
            "file.write" => {
                require_only(arguments, &["path", "content"])?;
                self.guard
                    .resolve_for_write(required_string(arguments, "path")?)?;
                let _ = required_string(arguments, "content")?;
            }
            "file.edit" => {
                require_only(arguments, &["path", "oldText", "newText"])?;
                self.guard
                    .resolve_existing(required_string(arguments, "path")?)?;
                if required_string(arguments, "oldText")?.is_empty() {
                    return Err(invalid_arguments("oldText must not be empty"));
                }
                let _ = required_string(arguments, "newText")?;
            }
            "search.text" => {
                require_only(arguments, &["pattern", "path"])?;
                Regex::new(required_string(arguments, "pattern")?)
                    .map_err(|error| invalid_arguments(format!("invalid regex: {error}")))?;
                if let Some(path) = optional_string(arguments, "path")? {
                    self.guard.resolve_existing(path)?;
                }
            }
            "process.exec" => {
                require_only(
                    arguments,
                    &[
                        "executable",
                        "args",
                        "cwd",
                        "stdin",
                        "timeoutMs",
                        "outputLimitBytes",
                        "sandbox",
                    ],
                )?;
                validate_executable(required_string(arguments, "executable")?)?;
                let _ = process_args(arguments)?;
                let options = self.execution_options(arguments)?;
                if options.sandbox.is_some() {
                    let _ = self.hard_sandbox.require_backend()?;
                }
            }
            "shell.exec" => {
                require_only(
                    arguments,
                    &[
                        "shell",
                        "script",
                        "cwd",
                        "stdin",
                        "timeoutMs",
                        "outputLimitBytes",
                        "sandbox",
                    ],
                )?;
                if !matches!(
                    required_string(arguments, "shell")?,
                    "bash" | "sh" | "pwsh" | "powershell" | "cmd"
                ) {
                    return Err(invalid_arguments("unsupported shell"));
                }
                validate_script(required_string(arguments, "script")?)?;
                let options = self.execution_options(arguments)?;
                if options.sandbox.is_some() {
                    let _ = self.hard_sandbox.require_backend()?;
                }
            }
            "computer.observe" | "computer.screenshot" => {
                require_only(arguments, &[])?;
                self.computer
                    .ok_or_else(computer_unavailable)?
                    .preflight_observe()?;
            }
            "computer.interact" => {
                let action = parse_computer_action(arguments)?;
                self.computer
                    .ok_or_else(computer_unavailable)?
                    .preflight_interact(&action)?;
            }
            _ => unreachable!("effect rejects unknown tools"),
        }
        Ok(effect)
    }

    /// 功能：结合可信宿主策略与客户端交互能力决定工具是否允许、询问或拒绝。
    ///
    /// 输入：已预检的副作用等级及初始化时协商的交互审批能力。
    /// 输出：封闭的 allow/require-approval/deny 决策。
    /// 不变量：只读操作保持默认允许；客户端能力本身不能绕过宿主显式 allow。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn policy_decision(
        &self,
        effect: ToolEffect,
        interactive_approvals: bool,
    ) -> PolicyDecision {
        if matches!(
            effect,
            ToolEffect::ComputerObserve | ToolEffect::ComputerInteract
        ) {
            return if interactive_approvals {
                PolicyDecision::RequireApproval
            } else {
                PolicyDecision::Deny
            };
        }
        match self.policy.decide(effect) {
            PolicyDecision::Allow => PolicyDecision::Allow,
            PolicyDecision::RequireApproval | PolicyDecision::Deny if interactive_approvals => {
                PolicyDecision::RequireApproval
            }
            PolicyDecision::RequireApproval | PolicyDecision::Deny => PolicyDecision::Deny,
        }
    }

    /// 功能：返回内置工具声明是否允许在已证明未完成时按幂等规则处理。
    ///
    /// 输入：已知或未知工具名。
    /// 输出：只读 file.read/search.text 返回 true，其余返回 false。
    /// 不变量：写入、进程、shell、computer 和未知工具永不声明为幂等。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn is_idempotent(&self, name: &str) -> bool {
        matches!(name, "file.read" | "search.text")
    }

    /// 功能：执行已经过 preflight 和宿主授权的工具，同时在执行边界再次校验参数。
    ///
    /// 输入：Session ID、工具名、精确参数和运行级取消令牌。
    /// 输出：规范化内联文本或 artifact 工具输出。
    /// 不变量：调用方必须先取得 allow/有效 approval；本方法仍重复路径与参数校验以防竞态或变更。
    /// 失败：工具消失、参数/路径变化、I/O、进程、超时、取消或输出限制返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn execute_authorized(
        &self,
        session_id: &str,
        name: &str,
        arguments: &Value,
        cancellation: CancellationToken,
    ) -> Result<ToolOutput, AgentError> {
        self.execute_authorized_for_call(session_id, None, "", name, arguments, cancellation)
            .await
    }

    /// 功能：携带真实工具调用 ID 执行已完成审批交付屏障的工具。
    ///
    /// 输入：Session ID、可信 Agent run ID、Provider 原始 tool-call ID、工具名、精确参数与取消令牌。
    /// 输出：规范化内联文本、artifact 或执行结果。
    /// 不变量：tool-call ID 仅用于双门 conformance checkpoint 精确匹配，不改变审批、operation hash 或生产工具语义。
    /// 失败：重复 preflight、路径、barrier、I/O、进程或取消错误按既有结构化边界返回。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) async fn execute_authorized_for_call(
        &self,
        session_id: &str,
        run_id: Option<&str>,
        tool_call_id: &str,
        name: &str,
        arguments: &Value,
        cancellation: CancellationToken,
    ) -> Result<ToolOutput, AgentError> {
        let _ = self.preflight(name, arguments)?;

        match name {
            "file.read" => self.read(session_id, tool_call_id, arguments).await,
            "file.write" => self.write(tool_call_id, arguments).await,
            "file.edit" => self.edit(arguments).await,
            "search.text" => self.search(arguments).await,
            "process.exec" => self.process(session_id, arguments, cancellation).await,
            "shell.exec" => self.shell(session_id, arguments, cancellation).await,
            "computer.observe" => {
                self.capture_desktop(session_id, run_id, "observed", cancellation)
                    .await
            }
            "computer.screenshot" => {
                self.capture_desktop(session_id, run_id, "captured", cancellation)
                    .await
            }
            "computer.interact" => self.interact_with_desktop(arguments, cancellation).await,
            _ => Err(AgentError::new(
                ErrorCode::ToolNotFound,
                format!("unknown tool: {name}"),
            )),
        }
    }

    /// 功能：将内置工具名映射为权限策略使用的副作用等级。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn effect(&self, name: &str) -> Result<ToolEffect, AgentError> {
        match name {
            "file.read" | "search.text" => Ok(ToolEffect::Read),
            "file.write" | "file.edit" => Ok(ToolEffect::Write),
            "process.exec" => Ok(ToolEffect::Process),
            "shell.exec" => Ok(ToolEffect::Shell),
            "computer.observe" | "computer.screenshot" if self.computer.is_some() => {
                Ok(ToolEffect::ComputerObserve)
            }
            "computer.interact"
                if self
                    .computer
                    .is_some_and(DesktopComputer::interaction_available) =>
            {
                Ok(ToolEffect::ComputerInteract)
            }
            _ => Err(AgentError::new(
                ErrorCode::ToolNotFound,
                format!("unknown tool: {name}"),
            )),
        }
    }

    /// 功能：捕获桌面、durable 保存 PNG，并返回不含主机路径的 artifact 结果。
    ///
    /// 输入：当前 Session ID、可信 Agent run ID、稳定动作描述与运行级取消令牌。
    /// 输出：包含尺寸、指针坐标和 PNG artifact 引用的工具结果。
    /// 不变量：截图字节先发布并追加 artifact.created，随后才返回工具结果。
    /// 失败：后端、阻塞任务或 artifact 发布失败时返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn capture_desktop(
        &self,
        session_id: &str,
        run_id: Option<&str>,
        action: &str,
        cancellation: CancellationToken,
    ) -> Result<ToolOutput, AgentError> {
        let run_id = run_id
            .filter(|value| !value.is_empty())
            .ok_or_else(computer_context_unavailable)?;
        let computer = self.computer.ok_or_else(computer_unavailable)?;
        let blocking_cancellation = cancellation.clone();
        let capture = tokio::task::spawn_blocking(move || computer.capture(&blocking_cancellation))
            .await
            .map_err(|_| computer_unavailable())??;
        if cancellation.is_cancelled() {
            return Err(computer_cancelled());
        }
        let artifact = self
            .sessions
            .store_computer_screenshot_artifact(session_id, run_id, &capture.png, &cancellation)
            .await?;
        let mut metadata = std::collections::BTreeMap::new();
        metadata.insert("width".to_owned(), json!(capture.width));
        metadata.insert("height".to_owned(), json!(capture.height));
        metadata.insert("pointerX".to_owned(), json!(capture.pointer_x));
        metadata.insert("pointerY".to_owned(), json!(capture.pointer_y));
        Ok(ToolOutput {
            text: Some(format!(
                "desktop {action}: {}x{}, pointer=({}, {})",
                capture.width, capture.height, capture.pointer_x, capture.pointer_y
            )),
            artifact: Some(artifact),
            termination_reason: None,
            execution: None,
            metadata,
        })
    }

    /// 功能：解析并在阻塞桌面 backend 中执行一次已授权交互。
    ///
    /// 输入：经过 Provider 传入的封闭 JSON 参数。
    /// 输出：动作成功后的有界文本结果。
    /// 不变量：本方法只在 `execute_authorized` 边界调用，且重新验证完整参数。
    /// 失败：参数、backend 或输入注入失败时返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn interact_with_desktop(
        &self,
        arguments: &Value,
        cancellation: CancellationToken,
    ) -> Result<ToolOutput, AgentError> {
        let action = parse_computer_action(arguments)?;
        let computer = self.computer.ok_or_else(computer_unavailable)?;
        tokio::task::spawn_blocking(move || computer.interact(&action, &cancellation))
            .await
            .map_err(|_| computer_unavailable())??;
        Ok(text_output("desktop interaction completed"))
    }

    /// 功能：读取工作区内 UTF-8 文件，并将超过内联阈值但未超过硬读取上限的内容转存为 artifact。
    ///
    /// 输入：Session ID、真实 tool-call ID 与只含 workspace 相对 path 的参数。
    /// 输出：不超过内联阈值时返回 UTF-8 文本；较大但仍在 Linux 1 MiB 硬上限内时返回 artifact。
    /// 不变量：Linux 实际 I/O 只从 pinned leaf FD 有界读取；artifact 分支不能扩大句柄读取上限。
    /// 失败：参数、路径、类型、硬上限、读取、UTF-8 或 artifact 持久化失败时返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn read(
        &self,
        session_id: &str,
        tool_call_id: &str,
        arguments: &Value,
    ) -> Result<ToolOutput, AgentError> {
        require_only(arguments, &["path"])?;
        let relative = required_string(arguments, "path")?;
        #[cfg(target_os = "linux")]
        let bytes = self
            .guard
            .read_file_pinned(tool_call_id, relative, MAX_FILE_READ_BYTES)
            .await?;
        #[cfg(not(target_os = "linux"))]
        let bytes = {
            let path = self.guard.resolve_existing(relative)?;
            let metadata = tokio::fs::metadata(&path).await?;
            if !metadata.is_file() {
                return Err(invalid_arguments("read path must be a file"));
            }
            tokio::fs::read(path).await?
        };
        if bytes.len() > self.max_read_bytes {
            let artifact = self
                .sessions
                .store_artifact(session_id, &bytes, "application/octet-stream")
                .await?;
            return Ok(ToolOutput {
                text: Some(format!(
                    "文件过大（{} 字节），内容已保存为 artifact。",
                    bytes.len()
                )),
                artifact: Some(artifact),
                termination_reason: None,
                execution: None,
                metadata: Default::default(),
            });
        }
        let text = String::from_utf8(bytes)
            .map_err(|_| invalid_arguments("read currently accepts UTF-8 text files only"))?;
        Ok(text_output(text))
    }

    /// 功能：在工作区边界内原子写入 UTF-8 文本内容。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn write(&self, tool_call_id: &str, arguments: &Value) -> Result<ToolOutput, AgentError> {
        require_only(arguments, &["path", "content"])?;
        let relative = required_string(arguments, "path")?;
        let content = required_string(arguments, "content")?;
        #[cfg(target_os = "linux")]
        self.guard
            .write_file_pinned(tool_call_id, relative, content.as_bytes())
            .await?;
        #[cfg(not(target_os = "linux"))]
        {
            let path = self.guard.resolve_for_write(relative)?;
            atomic_write(&path, content.as_bytes()).await?;
        }
        Ok(text_output(format!("wrote {} bytes", content.len())))
    }

    /// 功能：仅在旧文本唯一匹配时原子替换工作区文件片段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn edit(&self, arguments: &Value) -> Result<ToolOutput, AgentError> {
        require_only(arguments, &["path", "oldText", "newText"])?;
        let path = self
            .guard
            .resolve_existing(required_string(arguments, "path")?)?;
        let old = required_string(arguments, "oldText")?;
        let new = required_string(arguments, "newText")?;
        if old.is_empty() {
            return Err(invalid_arguments("oldText must not be empty"));
        }
        let original = tokio::fs::read_to_string(&path).await?;
        let count = original.match_indices(old).count();
        if count != 1 {
            return Err(invalid_arguments(format!(
                "oldText must match exactly once; matched {count} times"
            )));
        }
        let updated = original.replacen(old, new, 1);
        atomic_write(&path, updated.as_bytes()).await?;
        Ok(text_output("edited 1 occurrence"))
    }

    /// 功能：递归搜索工作区 UTF-8 文本文件中的正则匹配并限制结果数量。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn search(&self, arguments: &Value) -> Result<ToolOutput, AgentError> {
        require_only(arguments, &["pattern", "path"])?;
        let pattern = Regex::new(required_string(arguments, "pattern")?)
            .map_err(|error| invalid_arguments(format!("invalid regex: {error}")))?;
        let start = match optional_string(arguments, "path")? {
            Some(path) => self.guard.resolve_existing(path)?,
            None => self.guard.root().to_path_buf(),
        };
        let mut matches = Vec::new();
        for entry in WalkDir::new(start)
            .follow_links(false)
            .into_iter()
            .filter_map(Result::ok)
        {
            if matches.len() >= self.max_search_results || !entry.file_type().is_file() {
                continue;
            }
            let Ok(path) = self.guard.resolve_existing(
                entry
                    .path()
                    .strip_prefix(self.guard.root())
                    .unwrap_or(entry.path()),
            ) else {
                continue;
            };
            let Ok(content) = std::fs::read_to_string(&path) else {
                continue;
            };
            for (line_index, line) in content.lines().enumerate() {
                if pattern.is_match(line) {
                    let relative = path.strip_prefix(self.guard.root()).unwrap_or(&path);
                    matches.push(format!(
                        "{}:{}:{}",
                        relative.display(),
                        line_index + 1,
                        line
                    ));
                    if matches.len() >= self.max_search_results {
                        break;
                    }
                }
            }
        }
        Ok(text_output(matches.join("\n")))
    }

    /// 功能：重复校验 process.exec 扁平 fixture 参数并归一化为不经过 shell 的原生执行请求。
    ///
    /// 输入：已批准的 executable/args/cwd/stdin/timeout/outputLimit 参数和运行级取消令牌。
    /// 输出：带结构化 execution 的 ToolOutput。
    /// 不变量：cwd 在执行边界重新通过工作区真实路径守卫；argv 元素保持独立；调用方提供的输出上限优先于安全默认值。
    /// 失败：参数/路径竞态、启动、管道或 containment 失败返回结构化错误；已启动进程的非零、timeout、cancel 和超限作为 ToolOutput 返回。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn process(
        &self,
        session_id: &str,
        arguments: &Value,
        cancellation: CancellationToken,
    ) -> Result<ToolOutput, AgentError> {
        require_only(
            arguments,
            &[
                "executable",
                "args",
                "cwd",
                "stdin",
                "timeoutMs",
                "outputLimitBytes",
                "sandbox",
            ],
        )?;
        let executable_text = required_string(arguments, "executable")?;
        validate_executable(executable_text)?;
        let executable = OsString::from(executable_text);
        let args = process_args(arguments)?;
        let options = self.execution_options(arguments)?;
        let request = ProcessRequest {
            executable,
            args,
            cwd: options.cwd,
            stdin: options.stdin,
            timeout: options.timeout,
            stdout_limit_bytes: options.output_limit_bytes,
            stderr_limit_bytes: options.output_limit_bytes,
            total_output_limit_bytes: options.output_limit_bytes,
        };
        let result = match options.sandbox {
            Some(sandbox) => {
                self.executor
                    .exec_sandboxed(
                        request,
                        self.hard_sandbox.require_backend()?,
                        sandbox,
                        cancellation,
                    )
                    .await?
            }
            None => self.executor.exec(request, cancellation).await?,
        };
        self.process_output(session_id, result)
    }

    /// 功能：重复校验 shell.exec 扁平 fixture 参数并通过固定非交互映射执行一个脚本文本 argv。
    ///
    /// 输入：已批准的 shell/script/cwd/stdin/timeout/outputLimit 参数和运行级取消令牌。
    /// 输出：带结构化 execution 的 ToolOutput。
    /// 不变量：只接受封闭解释器枚举；脚本不被 qxnm-forge 再次拼接或引用；与 process.exec 共用环境和整树清理。
    /// 失败：参数/路径、解释器启动、管道或 containment 失败返回结构化错误；普通进程终止作为 ToolOutput 返回。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn shell(
        &self,
        session_id: &str,
        arguments: &Value,
        cancellation: CancellationToken,
    ) -> Result<ToolOutput, AgentError> {
        require_only(
            arguments,
            &[
                "shell",
                "script",
                "cwd",
                "stdin",
                "timeoutMs",
                "outputLimitBytes",
                "sandbox",
            ],
        )?;
        let shell = match required_string(arguments, "shell")? {
            "bash" => ShellKind::Bash,
            "sh" => ShellKind::Sh,
            "pwsh" => ShellKind::Pwsh,
            "powershell" => ShellKind::PowerShell,
            "cmd" => ShellKind::Cmd,
            _ => return Err(invalid_arguments("unsupported shell")),
        };
        let script = required_string(arguments, "script")?;
        validate_script(script)?;
        let options = self.execution_options(arguments)?;
        let request = ShellRequest {
            shell,
            script: script.to_owned(),
            cwd: options.cwd,
            stdin: options.stdin,
            timeout: options.timeout,
            stdout_limit_bytes: options.output_limit_bytes,
            stderr_limit_bytes: options.output_limit_bytes,
            total_output_limit_bytes: options.output_limit_bytes,
        };
        let result = match options.sandbox {
            Some(sandbox) => {
                self.executor
                    .shell_sandboxed(
                        request,
                        self.hard_sandbox.require_backend()?,
                        sandbox,
                        cancellation,
                    )
                    .await?
            }
            None => self.executor.shell(request, cancellation).await?,
        };
        self.process_output(session_id, result)
    }

    /// 功能：把执行器结果渲染为 Provider 可读文本，并保留协议必需的完整 structured execution。
    ///
    /// 输入：Session ID（仅用于保持工具边界签名稳定）和已经有界的原生 ProcessResult。
    /// 输出：terminationReason 与 execution 显式命名的 ToolOutput；过长文本只显示有界摘要，execution 仍保存精确流账。
    /// 不变量：不把 containment 或终止原因藏入任意 metadata；非零退出不转换为基础设施异常。
    /// 失败：ProcessResult JSON 序列化失败时返回 InternalError。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn process_output(
        &self,
        _session_id: &str,
        result: crate::executor::ProcessResult,
    ) -> Result<ToolOutput, AgentError> {
        let rendered = format!(
            "exitCode: {:?}\nstdout:\n{}\nstderr:\n{}",
            result.exit_code, result.stdout.text, result.stderr.text
        );
        let text = if rendered.len() > self.sessions.inline_output_limit() {
            format!(
                "process output exceeded inline display (stdout {} bytes, stderr {} bytes)",
                result.stdout.total_bytes, result.stderr.total_bytes
            )
        } else {
            rendered
        };
        let termination_reason = termination_reason_text(result.termination_reason).to_owned();
        let execution = serde_json::to_value(&result)
            .map_err(|error| AgentError::new(ErrorCode::InternalError, error.to_string()))?;
        Ok(ToolOutput {
            text: Some(text),
            artifact: None,
            termination_reason: Some(termination_reason),
            execution: Some(execution),
            metadata: Default::default(),
        })
    }

    /// 功能：解析 process/shell 共用的 cwd、bounded stdin、timeout 和扁平 outputLimitBytes。
    ///
    /// 输入：已经限制顶层字段的工具参数。
    /// 输出：执行器可直接消费的规范化选项。
    /// 不变量：cwd 必须是工作区内真实目录并在每次执行前重新解析；stdin 最大 1 MiB；输出上限为 1..16 MiB。
    /// 失败：路径逃逸、类型、未知 stdin variant 或数值越界返回稳定参数错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn execution_options(&self, arguments: &Value) -> Result<ExecutionOptions, AgentError> {
        let cwd_text = optional_string(arguments, "cwd")?.unwrap_or(".");
        let cwd = self.guard.resolve_existing(cwd_text)?;
        if !cwd.is_dir() {
            return Err(invalid_arguments(
                "cwd must resolve to a workspace directory",
            ));
        }
        Ok(ExecutionOptions {
            cwd,
            stdin: optional_stdin(arguments)?,
            timeout: optional_timeout(arguments)?,
            output_limit_bytes: optional_output_limit(
                arguments,
                self.sessions.inline_output_limit(),
            )?,
            sandbox: optional_sandbox(arguments)?,
        })
    }
}

#[cfg(target_os = "linux")]
/// 功能：在 Linux 普通进程组和 `/proc` 身份重验清理可用时构造 process.exec 与 shell.exec 的模型可见定义。
///
/// 输入：当前宿主只读 `/proc/self/stat` 可用性。
/// 输出：基础进程组 guard 可建立时返回两个执行工具定义，否则返回空列表。
/// 不变量：扁平 cwd/stdin/timeout/outputLimitBytes 仅作为冻结 conformance/CLI 兼容入口；5ms `/proc` 采样只是 best-effort cleanup，不升级 wire containment。
/// 失败：探测失败时诚实不广告，不启动进程、不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn executor_definitions() -> Vec<ToolDefinition> {
    if std::fs::read_to_string("/proc/self/stat").is_err() {
        return Vec::new();
    }
    vec![
        definition(
            "process.exec",
            "以 executable 与原始 argv 运行进程，不经过 shell",
            ToolEffect::Process,
            json!({
                "type":"object",
                "properties":{
                    "executable":{"type":"string","minLength":1,"maxLength":4096},
                    "args":{"type":"array","items":{"type":"string","maxLength":1048576},"maxItems":4096},
                    "cwd":{"type":"string","maxLength":4096},
                    "stdin":{
                        "oneOf":[
                            {"type":"object","properties":{"type":{"const":"none"}},"required":["type"],"additionalProperties":false},
                            {"type":"object","properties":{"type":{"const":"text"},"text":{"type":"string","maxLength":1048576}},"required":["type","text"],"additionalProperties":false}
                        ]
                    },
                    "timeoutMs":{"type":"integer","minimum":1,"maximum":600000},
                    "outputLimitBytes":{"type":"integer","minimum":1,"maximum":16777216},
                    "sandbox":sandbox_input_schema()
                },
                "required":["executable","args"],
                "additionalProperties":false
            }),
        ),
        definition(
            "shell.exec",
            "通过显式指定的非交互 shell 运行一个脚本文本参数",
            ToolEffect::Shell,
            json!({
                "type":"object",
                "properties":{
                    "shell":{"type":"string","enum":["bash","sh","pwsh","powershell","cmd"]},
                    "script":{"type":"string","minLength":1,"maxLength":1048576},
                    "cwd":{"type":"string","maxLength":4096},
                    "stdin":{
                        "oneOf":[
                            {"type":"object","properties":{"type":{"const":"none"}},"required":["type"],"additionalProperties":false},
                            {"type":"object","properties":{"type":{"const":"text"},"text":{"type":"string","maxLength":1048576}},"required":["type","text"],"additionalProperties":false}
                        ]
                    },
                    "timeoutMs":{"type":"integer","minimum":1,"maximum":600000},
                    "outputLimitBytes":{"type":"integer","minimum":1,"maximum":16777216},
                    "sandbox":sandbox_input_schema()
                },
                "required":["shell","script"],
                "additionalProperties":false
            }),
        ),
    ]
}

/// 功能：按启动期真实 backend 能力构造 computer 工具定义。
///
/// 输入：XTEST 交互是否已探测成功。
/// 输出：始终包含 observe/screenshot，条件包含 interact 的定义列表。
/// 不变量：交互参数使用封闭 action 枚举和有界坐标、滚动、键盘字段。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn computer_definitions(interaction_available: bool) -> Vec<ToolDefinition> {
    let empty_schema = json!({
        "type":"object",
        "properties":{},
        "required":[],
        "additionalProperties":false
    });
    let mut definitions = vec![
        definition(
            "computer.observe",
            "观察当前桌面，返回显示几何、指针位置和 PNG artifact",
            ToolEffect::ComputerObserve,
            empty_schema.clone(),
        ),
        definition(
            "computer.screenshot",
            "截取当前桌面并返回 durable PNG artifact",
            ToolEffect::ComputerObserve,
            empty_schema,
        ),
    ];
    if interaction_available {
        definitions.push(definition(
            "computer.interact",
            "在逐次审批后执行鼠标移动、点击、滚动或受限命名按键",
            ToolEffect::ComputerInteract,
            json!({
                "type":"object",
                "x-discriminator":"action",
                "oneOf":[
                    {
                        "type":"object",
                        "properties":{
                            "action":{"const":"move"},
                            "x":{"type":"integer","minimum":0,"maximum":16383},
                            "y":{"type":"integer","minimum":0,"maximum":16383}
                        },
                        "required":["action","x","y"],
                        "additionalProperties":false
                    },
                    {
                        "type":"object",
                        "properties":{
                            "action":{"const":"click"},
                            "x":{"type":"integer","minimum":0,"maximum":16383},
                            "y":{"type":"integer","minimum":0,"maximum":16383},
                            "button":{"type":"string","enum":["left","middle","right"]},
                            "clicks":{"type":"integer","minimum":1,"maximum":3}
                        },
                        "required":["action","x","y","button","clicks"],
                        "additionalProperties":false
                    },
                    {
                        "type":"object",
                        "properties":{
                            "action":{"const":"scroll"},
                            "deltaX":{"type":"integer","minimum":-20,"maximum":20},
                            "deltaY":{"type":"integer","minimum":-20,"maximum":20}
                        },
                        "required":["action","deltaX","deltaY"],
                        "additionalProperties":false
                    },
                    {
                        "type":"object",
                        "properties":{
                            "action":{"const":"key"},
                            "key":{
                                "type":"string",
                                "enum":[
                                    "enter","tab","escape","backspace","delete","home","end",
                                    "page_up","page_down","left","right","up","down","space"
                                ]
                            },
                            "modifiers":{
                                "type":"array",
                                "items":{"type":"string","enum":["shift","control","alt","meta"]},
                                "maxItems":4,
                                "uniqueItems":true
                            }
                        },
                        "required":["action","key","modifiers"],
                        "additionalProperties":false
                    }
                ]
            }),
        ));
    }
    definitions
}

#[cfg(not(target_os = "linux"))]
/// 功能：在尚未实现受支持进程组或 Windows suspended Job 绑定的平台返回空执行工具定义。
///
/// 输入：当前非 Linux 宿主。
/// 输出：空列表。
/// 不变量：daemon 不会把 direct_process 或普通进程组伪装为共同 executor conformance。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn executor_definitions() -> Vec<ToolDefinition> {
    Vec::new()
}

/// 功能：构造工具 inputSchema 内可选 `linux-bwrap-v1` 请求的封闭对象定义。
///
/// 输入：无。
/// 输出：profile/workspaceAccess/network 三个必需字段且禁止额外字段的受限 JSON Schema。
/// 不变量：network 只能 isolated，profile 不允许别名；该 schema 不代表 backend 已可用或已授权。
/// 失败：内存 JSON 构造不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn sandbox_input_schema() -> Value {
    json!({
        "type":"object",
        "properties":{
            "profile":{"type":"string","const":"linux-bwrap-v1"},
            "workspaceAccess":{"type":"string","enum":["read_only","read_write"]},
            "network":{"type":"string","const":"isolated"}
        },
        "required":["profile","workspaceAccess","network"],
        "additionalProperties":false
    })
}

/// 功能：严格解析可选 hard-sandbox 请求而不探测 backend 或启动进程。
///
/// 输入：已经限制顶层字段的 process/shell 参数。
/// 输出：缺失为 None，精确 profile/access/network 为 typed 请求。
/// 不变量：unknown 字段、profile、network 或 access 均在审批前拒绝；完整参数仍由 operation hash 绑定。
/// 失败：形状或枚举不符返回 `-32602/executor_invalid`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn optional_sandbox(arguments: &Value) -> Result<Option<HardSandboxRequest>, AgentError> {
    let Some(value) = object(arguments)?.get("sandbox") else {
        return Ok(None);
    };
    require_only(value, &["profile", "workspaceAccess", "network"])?;
    if required_string(value, "profile")? != "linux-bwrap-v1"
        || required_string(value, "network")? != "isolated"
    {
        return Err(invalid_arguments(
            "hard sandbox profile or network is unsupported",
        ));
    }
    let workspace_access = match required_string(value, "workspaceAccess")? {
        "read_only" => WorkspaceAccess::ReadOnly,
        "read_write" => WorkspaceAccess::ReadWrite,
        _ => {
            return Err(invalid_arguments(
                "hard sandbox workspace access is unsupported",
            ));
        }
    };
    Ok(Some(HardSandboxRequest { workspace_access }))
}

/// 功能：严格解析 process.exec argv 并保持每个参数边界不变。
///
/// 输入：含必需 `args` 数组的工具参数。
/// 输出：最多 4096 个独立 OsString。
/// 不变量：不拼接、不展开通配符/变量/波浪号；单项最大 1 MiB 且拒绝 NUL。
/// 失败：数组、元素类型或边界非法时返回 executor 参数错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn process_args(arguments: &Value) -> Result<Vec<OsString>, AgentError> {
    let values = required_array(arguments, "args")?;
    if values.len() > 4096 {
        return Err(invalid_arguments("args must contain at most 4096 items"));
    }
    values
        .iter()
        .map(|item| {
            let text = item
                .as_str()
                .ok_or_else(|| invalid_arguments("all args must be strings"))?;
            if text.len() > 1024 * 1024 || text.contains('\0') {
                return Err(invalid_arguments("an argv item exceeds its safe boundary"));
            }
            Ok(OsString::from(text))
        })
        .collect()
}

/// 功能：验证 process executable 字段可作为一个原生 argv[0] 使用。
///
/// 输入：未经信任的 executable 文本。
/// 输出：长度、非空与 NUL 检查通过时返回成功。
/// 不变量：不做 shell 或路径字符串拼接；绝对 executable 是否获批由既有工具审批绑定。
/// 失败：空值、超过 4096 字节或 NUL 返回参数错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_executable(executable: &str) -> Result<(), AgentError> {
    if executable.is_empty() || executable.len() > 4096 || executable.contains('\0') {
        return Err(invalid_arguments("executable is outside its safe boundary"));
    }
    Ok(())
}

/// 功能：验证 shell script 作为单个解释器参数的有界文本形状。
///
/// 输入：未经信任的脚本文本。
/// 输出：非空、最大 1 MiB 且无 NUL 时返回成功。
/// 不变量：不解析或改写脚本语义；危险性由 shell 权限审批覆盖。
/// 失败：边界非法时返回参数错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_script(script: &str) -> Result<(), AgentError> {
    if script.is_empty() || script.len() > 1024 * 1024 || script.contains('\0') {
        return Err(invalid_arguments("script is outside its safe boundary"));
    }
    Ok(())
}

/// 功能：解析缺省/none 或最大 1 MiB 的 UTF-8 text stdin tagged union。
///
/// 输入：工具参数中的可选 stdin 对象。
/// 输出：None 或待写入独立 stdin pipe 的 UTF-8 字节。
/// 不变量：不把 stdin 拼入 argv；artifact variant 尚未实现时失败关闭且不广告。
/// 失败：未知字段、variant、类型或字节上限非法时返回参数错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn optional_stdin(arguments: &Value) -> Result<Option<Vec<u8>>, AgentError> {
    let Some(value) = object(arguments)?.get("stdin") else {
        return Ok(None);
    };
    if value.is_null() {
        return Ok(None);
    }
    let kind = required_string(value, "type")?;
    match kind {
        "none" => {
            require_only(value, &["type"])?;
            Ok(None)
        }
        "text" => {
            require_only(value, &["type", "text"])?;
            let text = required_string(value, "text")?;
            if text.len() > 1024 * 1024 {
                return Err(invalid_arguments("stdin text exceeds 1 MiB"));
            }
            Ok(Some(text.as_bytes().to_vec()))
        }
        _ => Err(invalid_arguments("unsupported stdin type")),
    }
}

/// 功能：读取扁平 outputLimitBytes 并施加本实现的安全内存上限。
///
/// 输入：工具参数和 Session 内联输出默认值。
/// 输出：1..16 MiB 的 usize 总输出限制。
/// 不变量：缺省值也被限制在同一硬范围；stdout/stderr 共享该总量。
/// 失败：非整数、零、过大或平台 usize 不可表示时返回参数错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn optional_output_limit(arguments: &Value, default: usize) -> Result<usize, AgentError> {
    const MAX_OUTPUT: u64 = 16 * 1024 * 1024;
    let value = match object(arguments)?.get("outputLimitBytes") {
        None | Some(Value::Null) => u64::try_from(default)
            .unwrap_or(MAX_OUTPUT)
            .clamp(1, MAX_OUTPUT),
        Some(value) => value
            .as_u64()
            .filter(|value| (1..=MAX_OUTPUT).contains(value))
            .ok_or_else(|| {
                invalid_arguments("outputLimitBytes must be an integer from 1 to 16777216")
            })?,
    };
    usize::try_from(value).map_err(|_| invalid_arguments("outputLimitBytes is not representable"))
}

/// 功能：把内部进程终止枚举映射为公共 toolResult 字符串。
///
/// 输入：封闭的 ProcessTerminationReason。
/// 输出：tool.schema.json 允许的 snake_case 值。
/// 不变量：映射与 executor JSON 序列化完全一致。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
const fn termination_reason_text(reason: ProcessTerminationReason) -> &'static str {
    match reason {
        ProcessTerminationReason::Exit => "exit",
        ProcessTerminationReason::Signal => "signal",
        ProcessTerminationReason::Timeout => "timeout",
        ProcessTerminationReason::Cancelled => "cancelled",
        ProcessTerminationReason::OutputLimit => "output_limit",
    }
}

/// 功能：把内置工具元数据组装为语言中立工具定义。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn definition(name: &str, description: &str, effect: ToolEffect, schema: Value) -> ToolDefinition {
    ToolDefinition {
        name: name.to_owned(),
        description: description.to_owned(),
        input_schema: schema,
        effect,
    }
}

/// 功能：创建仅包含内联文本且不携带 artifact 的工具结果。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn text_output(text: impl Into<String>) -> ToolOutput {
    ToolOutput {
        text: Some(text.into()),
        artifact: None,
        termination_reason: None,
        execution: None,
        metadata: Default::default(),
    }
}

/// 功能：创建稳定的工具参数非法错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn invalid_arguments(message: impl Into<String>) -> AgentError {
    AgentError::new(ErrorCode::ToolArgumentsInvalid, message)
}

/// 功能：要求工具参数顶层为 JSON 对象并返回其映射引用。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn object(arguments: &Value) -> Result<&serde_json::Map<String, Value>, AgentError> {
    arguments
        .as_object()
        .ok_or_else(|| invalid_arguments("tool arguments must be an object"))
}

/// 功能：拒绝工具定义未显式允许的任何参数字段。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn require_only(arguments: &Value, allowed: &[&str]) -> Result<(), AgentError> {
    let object = object(arguments)?;
    if let Some(key) = object.keys().find(|key| !allowed.contains(&key.as_str())) {
        return Err(invalid_arguments(format!("unknown argument: {key}")));
    }
    Ok(())
}

/// 功能：提取必需字符串参数并保持对原 JSON 值的借用。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn required_string<'a>(arguments: &'a Value, key: &str) -> Result<&'a str, AgentError> {
    object(arguments)?
        .get(key)
        .and_then(Value::as_str)
        .ok_or_else(|| invalid_arguments(format!("'{key}' must be a string")))
}

/// 功能：提取可选字符串参数，将缺失或 null 统一视为未提供。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn optional_string<'a>(arguments: &'a Value, key: &str) -> Result<Option<&'a str>, AgentError> {
    match object(arguments)?.get(key) {
        None | Some(Value::Null) => Ok(None),
        Some(value) => value
            .as_str()
            .map(Some)
            .ok_or_else(|| invalid_arguments(format!("'{key}' must be a string"))),
    }
}

/// 功能：提取必需数组参数并返回其切片视图。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn required_array<'a>(arguments: &'a Value, key: &str) -> Result<&'a [Value], AgentError> {
    object(arguments)?
        .get(key)
        .and_then(Value::as_array)
        .map(Vec::as_slice)
        .ok_or_else(|| invalid_arguments(format!("'{key}' must be an array")))
}

/// 功能：解析 computer.interact 的 action-specific 参数并拒绝含糊操作。
///
/// 输入：未经信任的 JSON 参数对象。
/// 输出：边界完整的封闭 ComputerAction。
/// 不变量：每个 action 只允许其精确字段；坐标为 0..16383，滚动最多 20 格且 0/0 是安全 no-op。
/// 失败：字段缺失、类型、范围、重复修饰键或命名键不在 allowlist 时返回参数错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_computer_action(arguments: &Value) -> Result<ComputerAction, AgentError> {
    match required_string(arguments, "action")? {
        "move" => {
            require_only(arguments, &["action", "x", "y"])?;
            Ok(ComputerAction::Move {
                x: required_coordinate(arguments, "x")?,
                y: required_coordinate(arguments, "y")?,
            })
        }
        "click" => {
            require_only(arguments, &["action", "x", "y", "button", "clicks"])?;
            let button = match required_string(arguments, "button")? {
                "left" => 1,
                "middle" => 2,
                "right" => 3,
                _ => return Err(invalid_arguments("unsupported mouse button")),
            };
            let clicks = required_integer(arguments, "clicks", 1, 3)? as u8;
            Ok(ComputerAction::Click {
                x: required_coordinate(arguments, "x")?,
                y: required_coordinate(arguments, "y")?,
                button,
                clicks,
            })
        }
        "scroll" => {
            require_only(arguments, &["action", "deltaX", "deltaY"])?;
            let delta_x = required_integer(arguments, "deltaX", -20, 20)? as i16;
            let delta_y = required_integer(arguments, "deltaY", -20, 20)? as i16;
            Ok(ComputerAction::Scroll { delta_x, delta_y })
        }
        "key" => {
            require_only(arguments, &["action", "key", "modifiers"])?;
            let key = required_string(arguments, "key")?;
            if !is_supported_computer_key(key) {
                return Err(invalid_arguments("unsupported computer key"));
            }
            let mut modifiers = Vec::new();
            for value in required_array(arguments, "modifiers")? {
                let modifier = value
                    .as_str()
                    .filter(|value| matches!(*value, "shift" | "control" | "alt" | "meta"))
                    .ok_or_else(|| invalid_arguments("unsupported key modifier"))?;
                if modifiers.iter().any(|existing| existing == modifier) {
                    return Err(invalid_arguments("key modifiers must be unique"));
                }
                modifiers.push(modifier.to_owned());
            }
            if modifiers.len() > 4 {
                return Err(invalid_arguments("too many key modifiers"));
            }
            Ok(ComputerAction::Key {
                key: key.to_owned(),
                modifiers,
            })
        }
        _ => Err(invalid_arguments("unsupported computer action")),
    }
}

/// 功能：纯函数校验 computer.interact 的封闭命名键集合。
///
/// 输入：未经信任的键名字符串。
/// 输出：仅固定十四个可移植命名键返回 true。
/// 不变量：不接受单字符、控制字符、别名、大小写折叠或 Unicode 近似值。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn is_supported_computer_key(key: &str) -> bool {
    matches!(
        key,
        "enter"
            | "tab"
            | "escape"
            | "backspace"
            | "delete"
            | "home"
            | "end"
            | "page_up"
            | "page_down"
            | "left"
            | "right"
            | "up"
            | "down"
            | "space"
    )
}

/// 功能：读取有符号整数并限制在闭区间内。
///
/// 输入：未经信任的参数对象、字段名与闭区间上下界。
/// 输出：字段存在、类型为整数且位于闭区间时返回其 i64 值。
/// 不变量：不做字符串转换、浮点截断或区间外钳制。
/// 失败：对象、字段、类型或范围无效时返回结构化参数错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn required_integer(
    arguments: &Value,
    key: &str,
    minimum: i64,
    maximum: i64,
) -> Result<i64, AgentError> {
    object(arguments)?
        .get(key)
        .and_then(Value::as_i64)
        .filter(|value| (minimum..=maximum).contains(value))
        .ok_or_else(|| invalid_arguments(format!("'{key}' is outside its integer boundary")))
}

/// 功能：读取 XTEST 可表示的非负绝对坐标。
///
/// 输入：未经信任的参数对象与目标坐标字段名。
/// 输出：字段属于 0..=16383 时返回无损 i16 坐标。
/// 不变量：复用闭区间整数验证，不接受负数、浮点、字符串或更大坐标。
/// 失败：字段缺失、类型/范围无效或转换失败时返回结构化参数错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn required_coordinate(arguments: &Value, key: &str) -> Result<i16, AgentError> {
    i16::try_from(required_integer(arguments, key, 0, 16_383)?)
        .map_err(|_| invalid_arguments(format!("'{key}' is outside its coordinate boundary")))
}

/// 功能：创建 computer backend 运行期消失时的脱敏错误。
///
/// 输入：无。
/// 输出：固定 code、message 与 kind 的结构化 backend 不可用错误。
/// 不变量：不包含 DISPLAY、路径、窗口标题、像素、动作参数或原生库诊断。
/// 失败：本函数始终构造错误值，本身不返回失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn computer_unavailable() -> AgentError {
    AgentError::new(
        ErrorCode::InternalError,
        "desktop computer backend is unavailable",
    )
    .with_details(json!({"kind":"computer_unavailable"}))
}

/// 功能：创建缺少可信 Agent run 上下文时的 computer 截图失败关闭错误。
///
/// 输入：无；调用方已确认公共执行入口没有可信 run ID。
/// 输出：不包含模型参数或宿主标识的结构化内部错误。
/// 不变量：调用方不得回退为从工具 arguments 读取 runId。
/// 失败：本函数始终返回错误值。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn computer_context_unavailable() -> AgentError {
    AgentError::new(
        ErrorCode::InternalError,
        "desktop computer run context is unavailable",
    )
    .with_details(json!({"kind":"computer_context_unavailable"}))
}

/// 功能：创建 computer 阻塞任务结束后观察到取消时的稳定错误。
///
/// 输入：无；调用方已在阻塞任务返回后观察到运行级取消。
/// 输出：固定 code、message 与 kind 的结构化取消错误。
/// 不变量：不包含截图内容、DISPLAY、动作参数或宿主诊断。
/// 失败：本函数始终构造错误值，本身不返回失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn computer_cancelled() -> AgentError {
    AgentError::new(
        ErrorCode::Cancelled,
        "desktop computer operation was cancelled",
    )
    .with_details(json!({"kind":"cancelled"}))
}

/// 功能：读取 1 至 600000 毫秒的可选超时，缺失时使用 120 秒默认值。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn optional_timeout(arguments: &Value) -> Result<Duration, AgentError> {
    match object(arguments)?.get("timeoutMs") {
        None | Some(Value::Null) => Ok(Duration::from_secs(120)),
        Some(value) => {
            let milliseconds = value
                .as_u64()
                .filter(|value| (1..=600_000).contains(value))
                .ok_or_else(|| {
                    invalid_arguments("timeoutMs must be an integer from 1 to 600000")
                })?;
            Ok(Duration::from_millis(milliseconds))
        }
    }
}

/// 功能：通过同目录临时文件和 rename 原子替换目标，避免读者看到半写内容。
///
/// 输入：已通过工作区边界验证的目标路径和待写入字节。
/// 输出：rename 成功后返回空值。
/// 不变量：失败时尽力删除随机命名临时文件；目标只在完整内容写入后可见。
/// 失败：父目录、文件名、目录创建、临时写入或 rename 失败时返回结构化错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn atomic_write(path: &Path, bytes: &[u8]) -> Result<(), AgentError> {
    let parent = path
        .parent()
        .ok_or_else(|| invalid_arguments("write path has no parent"))?;
    tokio::fs::create_dir_all(parent).await?;
    let name = path
        .file_name()
        .and_then(|name| name.to_str())
        .ok_or_else(|| invalid_arguments("write path must have a UTF-8 filename"))?;
    let temporary = parent.join(format!(".{name}.{}.tmp", Uuid::new_v4()));
    tokio::fs::write(&temporary, bytes).await?;
    if let Err(error) = tokio::fs::rename(&temporary, path).await {
        let _ = tokio::fs::remove_file(&temporary).await;
        return Err(error.into());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use serde_json::json;
    use tempfile::tempdir;
    use tokio_util::sync::CancellationToken;

    #[cfg(target_os = "linux")]
    use super::MAX_FILE_READ_BYTES;
    use super::{ToolRegistry, computer_definitions, parse_computer_action};
    use crate::computer::ComputerAction;
    use crate::error::ErrorCode;
    use crate::policy::{ToolPolicy, WorkspaceGuard};
    use crate::session::SessionStore;

    /// 功能：验证无头策略允许工作区读取，同时在写入发生前拒绝危险操作。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn read_is_allowed_but_write_is_denied_headless() -> Result<(), crate::error::AgentError>
    {
        let workspace = tempdir()?;
        let state = tempdir()?;
        tokio::fs::write(workspace.path().join("hello.txt"), "你好").await?;
        let sessions = SessionStore::new(state.path(), 1024).await?;
        let registry = ToolRegistry::new(
            WorkspaceGuard::new(workspace.path())?,
            ToolPolicy::headless_default(),
            sessions,
        );
        let output = registry
            .execute(
                "s1",
                "file.read",
                &json!({"path":"hello.txt"}),
                CancellationToken::new(),
            )
            .await?;
        assert_eq!(output.text.as_deref(), Some("你好"));
        let error = registry
            .execute(
                "s1",
                "file.write",
                &json!({"path":"x","content":"x"}),
                CancellationToken::new(),
            )
            .await
            .expect_err("write must be denied");
        assert_eq!(error.code, ErrorCode::PermissionDenied);
        Ok(())
    }

    /// 功能：验证 edit 在旧文本出现多次时拒绝进行含糊替换。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn edit_requires_unique_match() -> Result<(), crate::error::AgentError> {
        let workspace = tempdir()?;
        let state = tempdir()?;
        tokio::fs::write(workspace.path().join("x.txt"), "x x").await?;
        let registry = ToolRegistry::new(
            WorkspaceGuard::new(workspace.path())?,
            ToolPolicy::allow_all_for_host(),
            SessionStore::new(state.path(), 1024).await?,
        );
        let error = registry
            .execute(
                "s1",
                "file.edit",
                &json!({"path":"x.txt","oldText":"x","newText":"y"}),
                CancellationToken::new(),
            )
            .await
            .expect_err("ambiguous edit must fail");
        assert_eq!(error.code, ErrorCode::ToolArgumentsInvalid);
        Ok(())
    }

    /// 功能：验证超过内联阈值但低于 Linux 句柄硬上限的读取仍转存 artifact。
    ///
    /// 输入：256 KiB 加一字节的 UTF-8 regular 文件和已创建的 Session。
    /// 输出：成功返回 artifact，不把中等大小文件误报为硬上限错误。
    /// 不变量：inline/artifact 分层独立于 1 MiB leaf 读取上限；读取全程仍受句柄硬上限约束。
    /// 失败：artifact 分支不可达、硬上限过早拒绝或 Session 持久化失败时测试失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    #[tokio::test]
    async fn read_above_inline_threshold_uses_artifact_below_hard_limit()
    -> Result<(), crate::error::AgentError> {
        let workspace = tempdir()?;
        let state = tempdir()?;
        let content = vec![b'a'; 256 * 1024 + 1];
        assert!(content.len() < MAX_FILE_READ_BYTES);
        tokio::fs::write(workspace.path().join("large.txt"), &content).await?;
        let sessions = SessionStore::new(state.path(), 1024).await?;
        sessions.create_session("s1", workspace.path()).await?;
        let registry = ToolRegistry::new(
            WorkspaceGuard::new(workspace.path())?,
            ToolPolicy::allow_all_for_host(),
            sessions,
        );
        let output = registry
            .execute(
                "s1",
                "file.read",
                &json!({"path":"large.txt"}),
                CancellationToken::new(),
            )
            .await?;
        assert!(output.artifact.is_some());
        assert!(
            output
                .text
                .as_deref()
                .is_some_and(|text| text.contains("内容已保存为 artifact"))
        );
        Ok(())
    }

    /// 功能：验证未配置 backend 的显式 sandbox 请求返回 sandbox_unavailable，且绝不执行同一 shell 的 host fallback。
    ///
    /// 输入：临时 workspace、allow-all 宿主策略和会创建 marker 的 shell 请求。
    /// 输出：结构化失败且 marker 不存在。
    /// 不变量：测试直接调用 authorized 边界，证明即使审批已完成也不能绕过 Disabled sandbox 状态。
    /// 失败：请求被宿主执行、错误类型漂移或 marker 出现时测试失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    #[tokio::test]
    async fn unavailable_sandbox_never_falls_back_to_host() -> Result<(), crate::error::AgentError>
    {
        let workspace = tempdir()?;
        let state = tempdir()?;
        let registry = ToolRegistry::new(
            WorkspaceGuard::new(workspace.path())?,
            ToolPolicy::allow_all_for_host(),
            SessionStore::new(state.path(), 1024).await?,
        );
        let error = registry
            .execute_authorized(
                "s1",
                "shell.exec",
                &json!({
                    "shell":"sh",
                    "script":"printf started > must-not-exist",
                    "sandbox":{
                        "profile":"linux-bwrap-v1",
                        "workspaceAccess":"read_write",
                        "network":"isolated"
                    }
                }),
                CancellationToken::new(),
            )
            .await
            .expect_err("missing sandbox backend must fail closed");
        assert_eq!(error.code, ErrorCode::InternalError);
        assert_eq!(error.details["kind"], "sandbox_unavailable");
        assert!(!workspace.path().join("must-not-exist").exists());
        Ok(())
    }

    /// 功能：验证 computer.interact schema 与 parser 共享同一四动作封闭接受集合。
    ///
    /// 输入：边界坐标、0/0 scroll、全部命名键，以及 text、单字符、控制字符、额外字段和超界反例。
    /// 输出：安全动作通过，所有 schema 禁止形状在 preflight parser 处拒绝。
    /// 不变量：测试不依赖环境门、display 或真实输入注入。
    /// 失败：oneOf/discriminator、动作字段或 parser allowlist 漂移时断言失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn computer_schema_and_parser_are_closed_and_consistent() {
        let definition = computer_definitions(true)
            .into_iter()
            .find(|definition| definition.name == "computer.interact")
            .expect("interact definition must exist when XTEST is available");
        assert_eq!(definition.input_schema["x-discriminator"], "action");
        let branches = definition.input_schema["oneOf"]
            .as_array()
            .expect("interact schema must use oneOf");
        assert_eq!(branches.len(), 4);
        let actions = branches
            .iter()
            .map(|branch| {
                assert_eq!(branch["additionalProperties"], false);
                branch["properties"]["action"]["const"]
                    .as_str()
                    .expect("each branch must have an action const")
            })
            .collect::<Vec<_>>();
        assert_eq!(actions, ["move", "click", "scroll", "key"]);
        assert_eq!(branches[0]["properties"]["x"]["maximum"], json!(16_383));
        assert_eq!(branches[0]["required"], json!(["action", "x", "y"]));
        assert!(matches!(
            parse_computer_action(&json!({"action":"move","x":16383,"y":0})),
            Ok(ComputerAction::Move { x: 16383, y: 0 })
        ));
        assert!(matches!(
            parse_computer_action(&json!({"action":"scroll","deltaX":0,"deltaY":0})),
            Ok(ComputerAction::Scroll {
                delta_x: 0,
                delta_y: 0
            })
        ));
        for key in [
            "enter",
            "tab",
            "escape",
            "backspace",
            "delete",
            "home",
            "end",
            "page_up",
            "page_down",
            "left",
            "right",
            "up",
            "down",
            "space",
        ] {
            assert!(
                parse_computer_action(&json!({"action":"key","key":key,"modifiers":[]})).is_ok()
            );
        }
        for invalid in [
            json!({"action":"move","x":16384,"y":0}),
            json!({"action":"move","x":0,"y":0,"button":"left"}),
            json!({"action":"text","text":"hello"}),
            json!({"action":"key","key":"a","modifiers":[]}),
            json!({"action":"key","key":"\n","modifiers":[]}),
            json!({"action":"key","key":"enter","modifiers":["shift","shift"]}),
        ] {
            assert!(
                parse_computer_action(&invalid).is_err(),
                "accepted {invalid}"
            );
        }
    }

    /// 功能：验证无可信 Agent run 上下文的截图入口在 backend 探测前失败关闭。
    ///
    /// 输入：普通测试 registry、空模型参数和缺失 run ID。
    /// 输出：稳定 computer_context_unavailable，且不需要 desktop 环境门。
    /// 不变量：runId 不能从 arguments 获取，也不连接 X11 或创建 artifact。
    /// 失败：错误边界漂移或意外进入捕获时测试失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn screenshot_without_trusted_run_context_fails_closed()
    -> Result<(), crate::error::AgentError> {
        let workspace = tempdir()?;
        let state = tempdir()?;
        let registry = ToolRegistry::new(
            WorkspaceGuard::new(workspace.path())?,
            ToolPolicy::allow_all_for_host(),
            SessionStore::new(state.path(), 1024).await?,
        );
        assert_eq!(
            registry.policy_decision(crate::domain::ToolEffect::ComputerObserve, true),
            crate::policy::PolicyDecision::RequireApproval
        );
        assert_eq!(
            registry.policy_decision(crate::domain::ToolEffect::ComputerInteract, true),
            crate::policy::PolicyDecision::RequireApproval
        );
        assert_eq!(
            registry.policy_decision(crate::domain::ToolEffect::ComputerObserve, false),
            crate::policy::PolicyDecision::Deny
        );
        assert_eq!(
            registry.policy_decision(crate::domain::ToolEffect::ComputerInteract, false),
            crate::policy::PolicyDecision::Deny
        );
        assert_eq!(
            registry.policy_decision(crate::domain::ToolEffect::Write, true),
            crate::policy::PolicyDecision::Allow
        );
        let error = registry
            .capture_desktop("s1", None, "captured", CancellationToken::new())
            .await
            .expect_err("missing trusted run context must fail");
        assert_eq!(error.details["kind"], "computer_context_unavailable");
        Ok(())
    }

    /// 功能：在显式独立原生 X11 fixture 下验证 computer 能力、参数预检和真实 PNG artifact。
    ///
    /// 输入：测试进程继承的独立原生 X11/XTEST fixture 与精确环境门。
    /// 输出：可观察 backend 可用时广告 observe/screenshot，仅 XTEST 可用时广告 interact；本 fixture 中三者均出现，截图具有 PNG 魔数且可由 Session store 复核。
    /// 不变量：默认门禁显式标为 ignored 而不伪装通过；手动执行时缺少任一环境/backend 条件必须失败；不执行鼠标或键盘动作。
    /// 失败：探测、定义、参数、捕获、持久化或内容绑定不一致时测试失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    #[tokio::test]
    #[ignore = "requires explicit double gate and isolated native X11/XTEST desktop fixture"]
    async fn enabled_desktop_computer_captures_durable_png() -> Result<(), crate::error::AgentError>
    {
        let workspace = tempdir()?;
        let state = tempdir()?;
        let sessions = SessionStore::new(state.path(), 1024).await?;
        sessions.create_session("s1", workspace.path()).await?;
        sessions
            .append_record(
                "s1",
                "run.accepted",
                json!({
                    "runId":"run-live","inputMessageId":"message-live",
                    "provider":{"id":"faux","modelId":"faux-v1"}
                }),
            )
            .await?;
        let registry = ToolRegistry::new(
            WorkspaceGuard::new(workspace.path())?,
            ToolPolicy::allow_all_for_host(),
            sessions.clone(),
        );
        let names = registry
            .definitions()
            .into_iter()
            .map(|definition| definition.name)
            .collect::<Vec<_>>();
        assert!(names.iter().any(|name| name == "computer.observe"));
        assert!(names.iter().any(|name| name == "computer.screenshot"));
        assert!(names.iter().any(|name| name == "computer.interact"));
        assert_eq!(
            registry.preflight("computer.interact", &json!({"action":"move","x":10,"y":10}),)?,
            crate::domain::ToolEffect::ComputerInteract
        );
        let output = registry
            .execute_authorized_for_call(
                "s1",
                Some("run-live"),
                "call-live",
                "computer.screenshot",
                &json!({}),
                CancellationToken::new(),
            )
            .await?;
        let artifact = output.artifact.as_ref().ok_or_else(|| {
            crate::error::AgentError::new(ErrorCode::InternalError, "screenshot artifact missing")
        })?;
        assert_eq!(
            artifact.extensions["org.agentprotocol.computer"]["runId"],
            "run-live"
        );
        let bytes = sessions
            .read_verified_image_artifact("s1", artifact, 32 * 1024 * 1024)
            .await?;
        assert!(bytes.starts_with(&[137, 80, 78, 71, 13, 10, 26, 10]));
        Ok(())
    }
}
