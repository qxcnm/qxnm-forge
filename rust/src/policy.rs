#[cfg(target_os = "linux")]
use std::os::fd::OwnedFd;
use std::path::{Component, Path, PathBuf};
#[cfg(target_os = "linux")]
use std::sync::Arc;

use serde::{Deserialize, Serialize};

use crate::domain::ToolEffect;
use crate::error::{AgentError, ErrorCode};
use crate::path_boundary::PathBoundaryConformance;
#[cfg(target_os = "linux")]
use crate::path_boundary::{open_workspace_root, read_pinned_file, write_pinned_file};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum PolicyDecision {
    Allow,
    RequireApproval,
    Deny,
}

/// 默认策略允许工作区内只读工具，危险工具必须审批；无头模式无法审批时按拒绝处理。
#[derive(Debug, Clone)]
pub struct ToolPolicy {
    headless: bool,
    allow_dangerous: bool,
}

impl ToolPolicy {
    /// 功能：创建无头模式默认策略，允许只读操作并拒绝所有危险操作。
    ///
    /// 输入：无。
    /// 输出：无头环境安全默认值。
    /// 不变量：危险权限不能由模型工具参数开启；缺少显式宿主策略时保持拒绝。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub const fn headless_default() -> Self {
        Self {
            headless: true,
            allow_dangerous: false,
        }
    }

    /// 功能：创建交互模式默认策略，允许只读操作并要求危险操作审批。
    ///
    /// 输入：无。
    /// 输出：交互环境安全默认值。
    /// 不变量：未经审批的危险操作不会被直接允许。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub const fn interactive_default() -> Self {
        Self {
            headless: false,
            allow_dangerous: false,
        }
    }

    /// 功能：创建由可信宿主明确授权全部危险操作的策略。
    ///
    /// 输入：无；授权必须来自宿主配置，不能来自模型参数。
    /// 输出：允许普通危险工具效果、但仍保留 computer 逐次审批边界的策略。
    /// 不变量：调用此构造器本身就是宿主的显式授权边界；它不能缓存或跳过桌面截图/交互审批。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub const fn allow_all_for_host() -> Self {
        Self {
            headless: true,
            allow_dangerous: true,
        }
    }

    /// 功能：根据工具效果和当前运行模式作出允许、审批或拒绝决定。
    ///
    /// 输入：规范化的工具副作用等级。
    /// 输出：封闭的策略决定枚举。
    /// 不变量：只读效果默认允许；普通危险效果只有宿主明确授权时才直接允许；computer 始终留给逐次审批层且在本层拒绝。
    /// 失败：本方法不返回错误；未知效果无法绕过封闭枚举进入本方法。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub const fn decide(&self, effect: ToolEffect) -> PolicyDecision {
        match effect {
            ToolEffect::Read => PolicyDecision::Allow,
            ToolEffect::ComputerObserve | ToolEffect::ComputerInteract => PolicyDecision::Deny,
            ToolEffect::Write | ToolEffect::Process | ToolEffect::Shell | ToolEffect::Terminal => {
                if self.allow_dangerous {
                    PolicyDecision::Allow
                } else if self.headless {
                    PolicyDecision::Deny
                } else {
                    PolicyDecision::RequireApproval
                }
            }
        }
    }
}

/// 工作区路径守卫。它提供边界检查，但不宣称是 hard sandbox。
#[derive(Debug, Clone)]
pub struct WorkspaceGuard {
    root: PathBuf,
    #[cfg(target_os = "linux")]
    root_descriptor: Arc<OwnedFd>,
    path_conformance: PathBoundaryConformance,
}

impl WorkspaceGuard {
    /// 功能：规范化并验证工作区根目录，建立后续路径检查的可信基准。
    ///
    /// 输入：应当已经存在的工作区路径。
    /// 输出：持有规范绝对根路径的守卫。
    /// 不变量：保存的根路径已 canonicalize 且为目录；此守卫只是边界检查而非 hard sandbox。
    /// 失败：路径不存在、不可访问、无法规范化或不是目录时返回 `InvalidParams`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn new(root: impl AsRef<Path>) -> Result<Self, AgentError> {
        Self::new_with_path_state(root, PathBoundaryConformance::Disabled)
    }

    /// 功能：规范化工作区并按可信宿主双门冻结可选路径竞态 conformance 状态。
    ///
    /// 输入：现有工作区、argv 精确 `--conformance` 门与 `QXNM_FORGE_CONFORMANCE=1` 环境门。
    /// 输出：持有 canonical 路径、Linux root FD 和启动期不可变测试状态的守卫。
    /// 不变量：普通构造器不读取测试环境；配置 presence 缺任一门时不打开配置并延迟到 initialize 固定失败。
    /// 失败：工作区本身不存在、不可规范化、不是目录或 Linux root FD 无法安全打开时立即返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn new_with_conformance(
        root: impl AsRef<Path>,
        cli_conformance: bool,
        environment_conformance: bool,
    ) -> Result<Self, AgentError> {
        let path_conformance =
            PathBoundaryConformance::from_environment(cli_conformance, environment_conformance);
        Self::new_with_path_state(root, path_conformance)
    }

    /// 功能：以调用方已经冻结的路径 conformance 状态建立 canonical 工作区与 Linux root FD。
    ///
    /// 输入：现有 workspace 路径，以及 Disabled/Ready/Invalid 三态之一。
    /// 输出：路径展示身份与内核目录对象同时固定的 WorkspaceGuard。
    /// 不变量：不读取环境或配置；Linux FD 在所有 guard clone 释放前持续存活且保持 CLOEXEC。
    /// 失败：路径无法 canonicalize、不是目录或 root 最终分量无法 no-follow 打开时返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn new_with_path_state(
        root: impl AsRef<Path>,
        path_conformance: PathBoundaryConformance,
    ) -> Result<Self, AgentError> {
        let root = std::fs::canonicalize(root.as_ref()).map_err(|error| {
            AgentError::new(
                ErrorCode::InvalidParams,
                format!("workspace cannot be canonicalized: {error}"),
            )
        })?;
        if !root.is_dir() {
            return Err(AgentError::new(
                ErrorCode::InvalidParams,
                "workspace must be a directory",
            ));
        }
        #[cfg(target_os = "linux")]
        let root_descriptor = Arc::new(open_workspace_root(&root)?);
        Ok(Self {
            root,
            #[cfg(target_os = "linux")]
            root_descriptor,
            path_conformance,
        })
    }

    /// 功能：返回已规范化的工作区根路径。
    ///
    /// 输入：当前工作区守卫。
    /// 输出：生命周期受守卫约束的根路径引用。
    /// 不变量：返回路径为构造时验证过的目录，不发生重新解析。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn root(&self) -> &Path {
        &self.root
    }

    /// 功能：在协议 initialize 时传播路径竞态 conformance 的冻结配置失败。
    ///
    /// 输入：当前工作区守卫。
    /// 输出：未配置或严格有效时成功。
    /// 不变量：不在 initialize 重新读取环境或配置文件；生产误注入固定 fail closed。
    /// 失败：配置 presence 未通过双门或严格验证时返回 `-32603/conformance_configuration_invalid`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn ensure_path_conformance_ready(&self) -> Result<(), AgentError> {
        self.path_conformance.ensure_ready()
    }

    /// 功能：在 Linux 通过持久 root FD、逐组件 parent FD 和 leaf FD 读取文件。
    ///
    /// 输入：真实 tool-call ID、workspace 相对路径与工具层冻结的硬字节上限。
    /// 输出：从固定普通文件 FD 得到且不超过上限的完整字节。
    /// 不变量：任一 ADR0021 checkpoint release 后不再使用 workspace pathname 做安全重验；上限原样传递给 leaf fstat 与实际读取。
    /// 失败：路径、链接、类型、上限、barrier 或 I/O 异常时 fail closed 且不返回部分结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    pub(crate) async fn read_file_pinned(
        &self,
        tool_call_id: &str,
        relative: &str,
        max_bytes: usize,
    ) -> Result<Vec<u8>, AgentError> {
        read_pinned_file(
            &self.root,
            &self.root_descriptor,
            &self.path_conformance,
            tool_call_id,
            Path::new(relative),
            max_bytes,
        )
        .await
    }

    /// 功能：在 Linux 通过持久 root/parent FD 原子替换一个 workspace 文件。
    ///
    /// 输入：真实 tool-call ID、workspace 相对路径和完整内容。
    /// 输出：同一 pinned parent 中 renameat 提交成功时返回。
    /// 不变量：临时创建、清理和提交全部相对同一 parent FD，目标 symlink rebind 只会被替换而不会被跟随。
    /// 失败：路径、barrier、写入、同步或 rename 异常时 fail closed 并尽力删除临时条目。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    pub(crate) async fn write_file_pinned(
        &self,
        tool_call_id: &str,
        relative: &str,
        bytes: &[u8],
    ) -> Result<(), AgentError> {
        write_pinned_file(
            &self.root,
            &self.root_descriptor,
            &self.path_conformance,
            tool_call_id,
            Path::new(relative),
            bytes,
        )
        .await
    }

    /// 功能：解析已存在的工作区相对路径并阻止父级穿越和符号链接逃逸。
    ///
    /// 输入：不允许绝对路径或 `..` 分量的工作区相对路径。
    /// 输出：确认位于规范工作区根下的 canonical 路径。
    /// 不变量：成功结果等于根路径或以根路径为真实祖先。
    /// 失败：非法路径返回 `PathOutsideWorkspace`；目标不存在或不可规范化返回 I/O 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn resolve_existing(&self, path: impl AsRef<Path>) -> Result<PathBuf, AgentError> {
        let candidate = self.lexical_candidate(path.as_ref())?;
        let canonical = std::fs::canonicalize(&candidate).map_err(|error| {
            AgentError::new(
                ErrorCode::IoError,
                format!("cannot resolve workspace path: {error}"),
            )
        })?;
        self.require_inside(canonical)
    }

    /// 功能：解析待写入的工作区相对路径，并通过最近存在祖先阻止符号链接逃逸。
    ///
    /// 输入：不允许绝对路径或 `..` 分量的工作区相对写入路径。
    /// 输出：位于已验证工作区祖先之下的词法目标路径。
    /// 不变量：现有目标按真实路径验证；新目标的最近现有祖先必须位于工作区内。
    /// 失败：穿越、绝对路径、祖先逃逸或无法访问时返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn resolve_for_write(&self, path: impl AsRef<Path>) -> Result<PathBuf, AgentError> {
        let candidate = self.lexical_candidate(path.as_ref())?;
        if candidate.exists() {
            return self.resolve_existing(path);
        }

        // 新文件尚不能 canonicalize：向上寻找最近的已存在父目录并验证其真实路径。
        let mut ancestor = candidate.as_path();
        while !ancestor.exists() {
            ancestor = ancestor.parent().ok_or_else(|| {
                AgentError::new(
                    ErrorCode::PathOutsideWorkspace,
                    "write path has no existing workspace ancestor",
                )
            })?;
        }
        let canonical_ancestor = std::fs::canonicalize(ancestor)?;
        self.require_inside(canonical_ancestor)?;
        Ok(candidate)
    }

    /// 功能：执行不访问文件系统的相对路径词法校验并拼接工作区根目录。
    ///
    /// 输入：未经信任的工具路径参数。
    /// 输出：通过词法检查后拼接根目录的候选路径。
    /// 不变量：成功路径不含绝对、根、平台前缀或父目录分量。
    /// 失败：检测到任何逃逸语法时返回 `PathOutsideWorkspace`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn lexical_candidate(&self, path: &Path) -> Result<PathBuf, AgentError> {
        if path.as_os_str().is_empty() {
            return Ok(self.root.clone());
        }
        if path.is_absolute()
            || path.components().any(|component| {
                matches!(
                    component,
                    Component::ParentDir | Component::RootDir | Component::Prefix(_)
                )
            })
        {
            return Err(AgentError::new(
                ErrorCode::PathOutsideWorkspace,
                "path must be workspace-relative and must not contain '..'",
            ));
        }
        Ok(self.root.join(path))
    }

    /// 功能：确认已规范化路径仍位于规范工作区根目录之内。
    ///
    /// 输入：文件系统解析后的 canonical 绝对路径。
    /// 输出：边界内原路径所有权，便于调用方继续访问。
    /// 不变量：成功路径等于工作区根或真实地位于其后代中。
    /// 失败：符号链接或其他解析导致路径逃逸时返回 `PathOutsideWorkspace`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn require_inside(&self, canonical: PathBuf) -> Result<PathBuf, AgentError> {
        if canonical == self.root || canonical.starts_with(&self.root) {
            Ok(canonical)
        } else {
            Err(AgentError::new(
                ErrorCode::PathOutsideWorkspace,
                "resolved path escapes the workspace",
            ))
        }
    }
}

#[cfg(test)]
mod tests {
    use std::fs;

    use tempfile::tempdir;

    use super::{PolicyDecision, ToolPolicy, WorkspaceGuard};
    use crate::domain::ToolEffect;
    use crate::error::ErrorCode;

    /// 功能：验证无头默认策略允许读取且拒绝写入和 shell 等危险效果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn headless_default_denies_dangerous_effects() {
        let policy = ToolPolicy::headless_default();
        assert_eq!(policy.decide(ToolEffect::Read), PolicyDecision::Allow);
        assert_eq!(policy.decide(ToolEffect::Write), PolicyDecision::Deny);
        assert_eq!(policy.decide(ToolEffect::Shell), PolicyDecision::Deny);
    }

    /// 功能：验证宿主 allow-all 也不能把 computer 工具提升为无需逐次审批的 Allow。
    ///
    /// 输入：allow_all_for_host 策略与观察/交互效果。
    /// 输出：两者在基础策略层均固定 Deny，供 ToolRegistry 按交互审批能力升级为单次 RequireApproval。
    /// 不变量：普通写入仍保持可信宿主显式 Allow，避免改变既有危险工具语义。
    /// 失败：computer 被缓存授权或普通宿主授权回归时断言失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn host_allow_all_never_bypasses_computer_approval() {
        let policy = ToolPolicy::allow_all_for_host();
        assert_eq!(policy.decide(ToolEffect::Write), PolicyDecision::Allow);
        assert_eq!(
            policy.decide(ToolEffect::ComputerObserve),
            PolicyDecision::Deny
        );
        assert_eq!(
            policy.decide(ToolEffect::ComputerInteract),
            PolicyDecision::Deny
        );
    }

    /// 功能：验证包含父目录分量的写入路径会被工作区守卫拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn rejects_parent_traversal() -> Result<(), crate::error::AgentError> {
        let root = tempdir()?;
        let guard = WorkspaceGuard::new(root.path())?;
        let error = guard
            .resolve_for_write("../escape")
            .expect_err("parent traversal must fail");
        assert_eq!(error.code, ErrorCode::PathOutsideWorkspace);
        Ok(())
    }

    #[cfg(unix)]
    /// 功能：验证指向工作区外部的符号链接不能绕过真实路径边界检查。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn rejects_symlink_escape() -> Result<(), Box<dyn std::error::Error>> {
        use std::os::unix::fs::symlink;

        let root = tempdir()?;
        let outside = tempdir()?;
        fs::write(outside.path().join("secret"), "no")?;
        symlink(outside.path(), root.path().join("escape"))?;
        let guard = WorkspaceGuard::new(root.path())?;
        let error = guard
            .resolve_existing("escape/secret")
            .expect_err("symlink escape must fail");
        assert_eq!(error.code, ErrorCode::PathOutsideWorkspace);
        Ok(())
    }
}
