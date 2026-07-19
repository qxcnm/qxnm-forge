use std::ffi::OsString;
use std::path::{Component, Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

#[cfg(target_os = "linux")]
use std::fs::File;
#[cfg(target_os = "linux")]
use std::io::{Read, Write};
#[cfg(target_os = "linux")]
use std::os::fd::OwnedFd;

use serde::{Deserialize, Serialize};
use serde_json::json;

use crate::error::{AgentError, ErrorCode};
#[cfg(target_os = "linux")]
use crate::protocol::parse_strict_value;

pub(crate) const PATH_BOUNDARY_CONFIG_ENV: &str = "AGENT_PATH_BOUNDARY_CONFORMANCE_CONFIG";

#[cfg(target_os = "linux")]
const MAX_CONFIG_BYTES: u64 = 16 * 1024;
#[cfg(target_os = "linux")]
const MAX_BARRIER_BYTES: u64 = 4 * 1024;
#[cfg(target_os = "linux")]
const REQUIRED_TIMEOUT_MILLIS: u64 = 5_000;

/// 路径竞态 conformance 的启动期冻结状态。
#[derive(Debug, Clone, Default)]
pub(crate) enum PathBoundaryConformance {
    #[default]
    Disabled,
    Ready(Arc<PathBoundaryBarrier>),
    Invalid,
}

impl PathBoundaryConformance {
    /// 功能：按 CLI 与环境双门加载一次测试专用路径竞态配置。
    ///
    /// 输入：argv 是否含精确 `--conformance`，以及 `QXNM_FORGE_CONFORMANCE` 是否精确为 `1`。
    /// 输出：环境缺失时禁用；双门且配置严格有效时返回冻结 barrier；其他 presence 返回失败状态。
    /// 不变量：任一门缺失时绝不打开、stat 或读取配置路径；配置值不会进入错误、日志或 Session。
    /// 失败：门控、平台、文件类型、大小、UTF-8、strict JSON、字段或控制目录无效时冻结为 `Invalid`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn from_environment(cli_conformance: bool, environment_conformance: bool) -> Self {
        let Some(path) = std::env::var_os(PATH_BOUNDARY_CONFIG_ENV) else {
            return Self::Disabled;
        };
        if !cli_conformance || !environment_conformance {
            return Self::Invalid;
        }
        #[cfg(target_os = "linux")]
        {
            load_barrier(Path::new(&path))
                .map(|barrier| Self::Ready(Arc::new(barrier)))
                .unwrap_or(Self::Invalid)
        }
        #[cfg(not(target_os = "linux"))]
        {
            let _ = path;
            Self::Invalid
        }
    }

    /// 功能：在 initialize 边界传播路径竞态 conformance 配置失败。
    ///
    /// 输入：启动期冻结状态。
    /// 输出：未配置或严格可用时成功。
    /// 不变量：失败固定为 `-32603`、不可重试且 kind 为 `conformance_configuration_invalid`。
    /// 失败：显式配置未通过双门或任一严格验证时返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn ensure_ready(&self) -> Result<(), AgentError> {
        if matches!(self, Self::Invalid) {
            Err(configuration_error())
        } else {
            Ok(())
        }
    }

    /// 功能：仅在真实工具调用命中冻结 ID 与 checkpoint 时执行一次有界双向 barrier。
    ///
    /// 输入：实际 `toolCallId` 与代码固定 checkpoint 名。
    /// 输出：未配置或不匹配时立即成功；匹配时 ready 已持久化且严格 release 已收到后成功。
    /// 不变量：最多一个 checkpoint 可由配置触发；barrier 后端不读取 workspace、Provider 或 credential。
    /// 失败：控制文件被替换、超时、非法 JSON 或 I/O 不确定时返回固定脱敏内部错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) async fn checkpoint(
        &self,
        tool_call_id: &str,
        checkpoint: &'static str,
    ) -> Result<(), AgentError> {
        match self {
            Self::Disabled => Ok(()),
            Self::Invalid => Err(configuration_error()),
            Self::Ready(barrier) => barrier.checkpoint(tool_call_id, checkpoint).await,
        }
    }
}

/// 单次路径竞态 checkpoint 的严格配置。
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct PathBoundaryConfig {
    schema_version: String,
    case_id: String,
    tool_call_id: String,
    checkpoint: String,
    control_directory: PathBuf,
    timeout_ms: u64,
}

/// ready/release 控制文件的语言中立记录。
#[derive(Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BarrierRecord {
    schema_version: String,
    case_id: String,
    tool_call_id: String,
    checkpoint: String,
    state: String,
}

/// 已打开控制目录并限制为一次触发的测试 barrier。
#[derive(Debug)]
pub(crate) struct PathBoundaryBarrier {
    config: PathBoundaryConfig,
    #[cfg(target_os = "linux")]
    control_directory: Arc<OwnedFd>,
    triggered: AtomicBool,
}

impl PathBoundaryBarrier {
    /// 功能：发布 exact ready 记录并有界等待同一案例的 strict release 记录。
    ///
    /// 输入：实际工具调用 ID 与当前执行阶段。
    /// 输出：配置不匹配时为空操作；匹配时 runner 完成变更并释放后返回。
    /// 不变量：ready 使用 `O_EXCL|O_NOFOLLOW`；release 只接受 bounded regular no-follow 文件；一个配置只触发一次。
    /// 失败：重复命中、文件异常、内容不匹配或五秒期限耗尽时返回脱敏配置错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn checkpoint(
        &self,
        tool_call_id: &str,
        checkpoint: &'static str,
    ) -> Result<(), AgentError> {
        if self.config.tool_call_id != tool_call_id || self.config.checkpoint != checkpoint {
            return Ok(());
        }
        if self
            .triggered
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .is_err()
        {
            return Err(configuration_error());
        }
        #[cfg(target_os = "linux")]
        {
            let ready = self.record("ready");
            write_ready(&self.control_directory, &ready)?;
            let deadline = Instant::now() + Duration::from_millis(self.config.timeout_ms);
            loop {
                if let Some(release) = read_release(&self.control_directory)? {
                    if release.schema_version == "0.1"
                        && release.case_id == self.config.case_id
                        && release.tool_call_id == self.config.tool_call_id
                        && release.checkpoint == self.config.checkpoint
                        && release.state == "release"
                    {
                        return Ok(());
                    }
                    return Err(configuration_error());
                }
                if Instant::now() >= deadline {
                    return Err(configuration_error());
                }
                tokio::time::sleep(Duration::from_millis(5)).await;
            }
        }
        #[cfg(not(target_os = "linux"))]
        {
            Err(configuration_error())
        }
    }

    /// 功能：构造与冻结配置完全绑定的 ready 或 release 控制记录。
    ///
    /// 输入：闭集状态 `ready` 或 `release`。
    /// 输出：拥有独立字符串所有权的 barrier DTO。
    /// 不变量：case、tool call 与 checkpoint 不从调用时参数重建。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn record(&self, state: &str) -> BarrierRecord {
        BarrierRecord {
            schema_version: "0.1".to_owned(),
            case_id: self.config.case_id.clone(),
            tool_call_id: self.config.tool_call_id.clone(),
            checkpoint: self.config.checkpoint.clone(),
            state: state.to_owned(),
        }
    }
}

/// 功能：构造固定且不泄漏配置路径的 conformance 配置错误。
///
/// 输入：无。
/// 输出：`-32603`、不可重试、固定 kind 的公共错误。
/// 不变量：消息和 details 不含 case、控制路径、OS 错误或配置内容。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn configuration_error() -> AgentError {
    AgentError::new(
        ErrorCode::InternalError,
        "Path-boundary conformance configuration is invalid.",
    )
    .with_details(json!({"kind":"conformance_configuration_invalid"}))
}

/// 功能：验证 opaque conformance 标识符的 ASCII 闭集与长度。
///
/// 输入：配置提供的 case 或 tool-call ID。
/// 输出：满足 1..128 字节且首字符为字母数字时为 true。
/// 不变量：后续只接受字母数字、点、下划线、冒号和连字符。
/// 失败：本方法不返回错误；非法值返回 false。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn valid_opaque_id(value: &str) -> bool {
    let bytes = value.as_bytes();
    (1..=128).contains(&bytes.len())
        && bytes.first().is_some_and(u8::is_ascii_alphanumeric)
        && bytes
            .iter()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b':' | b'-'))
}

/// 功能：判断配置 checkpoint 是否属于 ADR0021 的四值闭集。
///
/// 输入：配置字符串。
/// 输出：精确匹配一个固定阶段时为 true。
/// 不变量：不接受大小写、前后空白或扩展阶段。
/// 失败：本方法不返回错误；未知值返回 false。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn valid_checkpoint(value: &str) -> bool {
    matches!(
        value,
        "before_parent_walk"
            | "after_parent_open"
            | "after_leaf_open_before_read"
            | "after_temp_fsync_before_rename"
    )
}

#[cfg(target_os = "linux")]
/// 功能：用 bounded regular no-follow 文件加载 strict 路径竞态配置并冻结控制目录 FD。
///
/// 输入：仅来自双门后固定环境名的配置路径。
/// 输出：字段闭集、值域和 0700 控制目录均验证完成的 barrier。
/// 不变量：配置和控制目录最终分量均不跟随 symlink；FIFO 通过 `O_NONBLOCK` 打开后按类型拒绝，不会挂起 initialize。
/// 失败：文件、UTF-8、duplicate key、字段、模式或目录 FD 异常时返回内部单位错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn load_barrier(path: &Path) -> Result<PathBoundaryBarrier, ()> {
    use nix::fcntl::{OFlag, open};
    use nix::sys::stat::{Mode, fstat};

    let descriptor = open(
        path,
        OFlag::O_RDONLY | OFlag::O_NOFOLLOW | OFlag::O_CLOEXEC | OFlag::O_NONBLOCK,
        Mode::empty(),
    )
    .map_err(|_| ())?;
    let metadata = fstat(&descriptor).map_err(|_| ())?;
    if !is_regular(metadata.st_mode)
        || metadata.st_size < 0
        || metadata.st_size as u64 > MAX_CONFIG_BYTES
    {
        return Err(());
    }
    let mut bytes = Vec::with_capacity(metadata.st_size as usize);
    File::from(descriptor)
        .take(MAX_CONFIG_BYTES + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| ())?;
    if bytes.len() as u64 > MAX_CONFIG_BYTES {
        return Err(());
    }
    let text = std::str::from_utf8(&bytes).map_err(|_| ())?;
    let value = parse_strict_value(text).map_err(|_| ())?;
    let config: PathBoundaryConfig = serde_json::from_value(value).map_err(|_| ())?;
    if config.schema_version != "0.1"
        || !valid_opaque_id(&config.case_id)
        || !valid_opaque_id(&config.tool_call_id)
        || !valid_checkpoint(&config.checkpoint)
        || config.timeout_ms != REQUIRED_TIMEOUT_MILLIS
        || !config.control_directory.is_absolute()
    {
        return Err(());
    }
    let control_directory = open(
        &config.control_directory,
        OFlag::O_RDONLY | OFlag::O_DIRECTORY | OFlag::O_NOFOLLOW | OFlag::O_CLOEXEC,
        Mode::empty(),
    )
    .map_err(|_| ())?;
    let directory_metadata = fstat(&control_directory).map_err(|_| ())?;
    if !is_directory(directory_metadata.st_mode) || directory_metadata.st_mode & 0o777 != 0o700 {
        return Err(());
    }
    Ok(PathBoundaryBarrier {
        config,
        control_directory: Arc::new(control_directory),
        triggered: AtomicBool::new(false),
    })
}

#[cfg(target_os = "linux")]
/// 功能：以 exclusive regular no-follow 文件发布 ready 记录并同步控制目录。
///
/// 输入：启动期冻结的控制目录 FD 与 exact ready DTO。
/// 输出：完整 JSON 在 `ready.json` durable 后成功。
/// 不变量：不覆盖既有条目，不使用绝对控制路径，不留下部分成功记录。
/// 失败：创建、序列化、写入、fsync 或 close 异常时返回固定配置错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn write_ready(directory: &OwnedFd, record: &BarrierRecord) -> Result<(), AgentError> {
    use nix::fcntl::{OFlag, openat};
    use nix::sys::stat::Mode;
    use nix::unistd::fsync;

    let descriptor = openat(
        directory,
        "ready.json",
        OFlag::O_WRONLY | OFlag::O_CREAT | OFlag::O_EXCL | OFlag::O_NOFOLLOW | OFlag::O_CLOEXEC,
        Mode::from_bits_truncate(0o600),
    )
    .map_err(|_| configuration_error())?;
    let bytes = serde_json::to_vec(record).map_err(|_| configuration_error())?;
    let mut file = File::from(descriptor);
    file.write_all(&bytes).map_err(|_| configuration_error())?;
    file.sync_all().map_err(|_| configuration_error())?;
    drop(file);
    fsync(directory).map_err(|_| configuration_error())?;
    Ok(())
}

#[cfg(target_os = "linux")]
/// 功能：尝试从控制目录读取一个 bounded strict regular no-follow release 记录。
///
/// 输入：启动期冻结的控制目录 FD。
/// 输出：文件尚不存在时为 None；存在且 strict JSON 合法时返回 DTO。
/// 不变量：只按 parent FD 打开固定文件名；不会等待 FIFO、跟随 symlink 或读取超限数据。
/// 失败：存在但类型、大小、UTF-8、duplicate key 或字段闭集非法时返回固定配置错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn read_release(directory: &OwnedFd) -> Result<Option<BarrierRecord>, AgentError> {
    use nix::errno::Errno;
    use nix::fcntl::{OFlag, openat};
    use nix::sys::stat::{Mode, fstat};

    let descriptor = match openat(
        directory,
        "release.json",
        OFlag::O_RDONLY | OFlag::O_NOFOLLOW | OFlag::O_CLOEXEC | OFlag::O_NONBLOCK,
        Mode::empty(),
    ) {
        Ok(descriptor) => descriptor,
        Err(Errno::ENOENT) => return Ok(None),
        Err(_) => return Err(configuration_error()),
    };
    let metadata = fstat(&descriptor).map_err(|_| configuration_error())?;
    if !is_regular(metadata.st_mode)
        || metadata.st_size < 0
        || metadata.st_size as u64 > MAX_BARRIER_BYTES
    {
        return Err(configuration_error());
    }
    let mut bytes = Vec::with_capacity(metadata.st_size as usize);
    File::from(descriptor)
        .take(MAX_BARRIER_BYTES + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| configuration_error())?;
    if bytes.len() as u64 > MAX_BARRIER_BYTES {
        return Err(configuration_error());
    }
    let text = std::str::from_utf8(&bytes).map_err(|_| configuration_error())?;
    let value = parse_strict_value(text).map_err(|_| configuration_error())?;
    serde_json::from_value(value)
        .map(Some)
        .map_err(|_| configuration_error())
}

#[cfg(target_os = "linux")]
/// 功能：打开并持有 canonical workspace 根目录的 Linux 文件描述符。
///
/// 输入：已经 canonicalize 且验证为目录的根路径。
/// 输出：调用方拥有的 `O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC` FD。
/// 不变量：后续根路径 rename 或 symlink rebind 不改变此 FD 指向的目录对象。
/// 失败：根最终分量为链接、权限不足、被替换或无法打开时返回脱敏 I/O 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) fn open_workspace_root(root: &Path) -> Result<OwnedFd, AgentError> {
    use nix::fcntl::{OFlag, open};
    use nix::sys::stat::Mode;

    open(
        root,
        OFlag::O_RDONLY | OFlag::O_DIRECTORY | OFlag::O_NOFOLLOW | OFlag::O_CLOEXEC,
        Mode::empty(),
    )
    .map_err(|_| path_io_error())
}

#[cfg(target_os = "linux")]
/// 功能：在发布 `before_parent_walk` ready 前确认可见根路径仍绑定持久 root FD 身份。
///
/// 输入：canonical 展示路径与启动期持有的 root FD。
/// 输出：当前 pathname 打开的 dev/inode 与持久 FD 相同时成功。
/// 不变量：本检查只发生在 ready 之前；release 后所有安全决策只依赖持久句柄，不再次解析 root pathname。
/// 失败：root 消失、变成 symlink/非目录或身份变化时返回路径拒绝且不执行工具 I/O。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn verify_visible_root(root: &Path, pinned: &OwnedFd) -> Result<(), AgentError> {
    use nix::sys::stat::fstat;

    let visible = open_workspace_root(root)?;
    let visible_stat = fstat(&visible).map_err(|_| path_io_error())?;
    let pinned_stat = fstat(pinned).map_err(|_| path_io_error())?;
    if visible_stat.st_dev != pinned_stat.st_dev || visible_stat.st_ino != pinned_stat.st_ino {
        return Err(AgentError::new(
            ErrorCode::PathOutsideWorkspace,
            "workspace root identity changed",
        ));
    }
    Ok(())
}

#[cfg(target_os = "linux")]
/// 已打开且不会随 pathname rebind 改变目标的父目录与叶文件名。
struct PinnedParent {
    descriptor: OwnedFd,
    leaf: OsString,
}

#[cfg(target_os = "linux")]
/// 功能：从持久 root FD 逐组件 no-follow 打开目标父目录。
///
/// 输入：持久 root FD 与未经信任的 workspace 相对路径。
/// 输出：调用方拥有的最终 parent FD 和单一 leaf 名。
/// 不变量：只接受 Normal 组件；每级使用 `openat(O_DIRECTORY|O_NOFOLLOW)`，已开 parent 不受后续 rename/rebind 影响。
/// 失败：空路径、绝对/父级/当前目录组件、NUL、链接、缺失或非目录分量均 fail closed。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn open_parent(pinned_root: &OwnedFd, relative: &Path) -> Result<PinnedParent, AgentError> {
    use nix::fcntl::{OFlag, openat};
    use nix::sys::stat::Mode;

    let mut parts = Vec::new();
    for component in relative.components() {
        match component {
            Component::Normal(part) if !part.is_empty() => parts.push(part.to_os_string()),
            _ => {
                return Err(AgentError::new(
                    ErrorCode::PathOutsideWorkspace,
                    "workspace path must contain only relative normal components",
                ));
            }
        }
    }
    let leaf = parts.pop().ok_or_else(|| {
        AgentError::new(
            ErrorCode::PathOutsideWorkspace,
            "workspace file path is empty",
        )
    })?;
    let mut current = pinned_root.try_clone().map_err(|_| path_io_error())?;
    for part in parts {
        current = openat(
            &current,
            part.as_os_str(),
            OFlag::O_RDONLY | OFlag::O_DIRECTORY | OFlag::O_NOFOLLOW | OFlag::O_CLOEXEC,
            Mode::empty(),
        )
        .map_err(|_| path_io_error())?;
    }
    Ok(PinnedParent {
        descriptor: current,
        leaf,
    })
}

#[cfg(target_os = "linux")]
/// 功能：通过持久 root、逐组件 parent 与 leaf FD 完整读取一个普通文件。
///
/// 输入：canonical root 展示路径、持久 root FD、冻结 barrier、真实 tool-call ID、相对文件路径与工具硬字节上限。
/// 输出：从同一个已验证 leaf FD 读取且不超过硬上限的完整字节。
/// 不变量：root 身份、regular 类型与静态大小检查均在 ready 前完成；checkpoint 后只从 leaf FD 最多读取 `max_bytes + 1` 字节，不再解析 workspace 路径。
/// 失败：路径竞态、链接/特殊文件、静态或增长后超限、barrier、打开或读取错误时返回结构化失败且不返回部分成功。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) async fn read_pinned_file(
    root: &Path,
    pinned_root: &OwnedFd,
    conformance: &PathBoundaryConformance,
    tool_call_id: &str,
    relative: &Path,
    max_bytes: usize,
) -> Result<Vec<u8>, AgentError> {
    use nix::fcntl::{OFlag, openat};
    use nix::sys::stat::{Mode, fstat};

    verify_visible_root(root, pinned_root)?;
    conformance
        .checkpoint(tool_call_id, "before_parent_walk")
        .await?;
    let parent = open_parent(pinned_root, relative)?;
    conformance
        .checkpoint(tool_call_id, "after_parent_open")
        .await?;
    let descriptor = openat(
        &parent.descriptor,
        parent.leaf.as_os_str(),
        OFlag::O_RDONLY | OFlag::O_NONBLOCK | OFlag::O_NOFOLLOW | OFlag::O_CLOEXEC,
        Mode::empty(),
    )
    .map_err(|_| path_io_error())?;
    let metadata = fstat(&descriptor).map_err(|_| path_io_error())?;
    if !is_regular(metadata.st_mode) {
        return Err(AgentError::new(
            ErrorCode::PathOutsideWorkspace,
            "workspace read target is not a regular file",
        ));
    }
    let static_size =
        usize::try_from(metadata.st_size).map_err(|_| file_read_limit_error(max_bytes))?;
    let read_limit = u64::try_from(max_bytes)
        .ok()
        .and_then(|limit| limit.checked_add(1))
        .ok_or_else(|| file_read_limit_error(max_bytes))?;
    if static_size > max_bytes {
        return Err(file_read_limit_error(max_bytes));
    }
    conformance
        .checkpoint(tool_call_id, "after_leaf_open_before_read")
        .await?;
    let mut bytes = Vec::with_capacity(static_size);
    File::from(descriptor)
        .take(read_limit)
        .read_to_end(&mut bytes)
        .map_err(|_| path_io_error())?;
    if bytes.len() > max_bytes {
        return Err(file_read_limit_error(max_bytes));
    }
    Ok(bytes)
}

#[cfg(target_os = "linux")]
/// 功能：在持久 parent FD 中创建、同步并原子 rename 一个完整普通文件。
///
/// 输入：canonical root 展示路径、持久 root FD、冻结 barrier、真实 tool-call ID、相对目标与内容。
/// 输出：同一 pinned parent 中 leaf 被完整新文件替换后成功。
/// 不变量：临时文件使用 exclusive no-follow openat；fsync 后 checkpoint；提交与清理只用同一 parent FD 的 renameat/unlinkat。
/// 失败：路径、barrier、临时写入、同步或 rename 异常时尽力按 parent FD 删除临时项，绝不跟随目标 symlink。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) async fn write_pinned_file(
    root: &Path,
    pinned_root: &OwnedFd,
    conformance: &PathBoundaryConformance,
    tool_call_id: &str,
    relative: &Path,
    bytes: &[u8],
) -> Result<(), AgentError> {
    use nix::fcntl::{OFlag, openat, renameat};
    use nix::sys::stat::Mode;
    use nix::unistd::{UnlinkatFlags, fsync, unlinkat};

    verify_visible_root(root, pinned_root)?;
    conformance
        .checkpoint(tool_call_id, "before_parent_walk")
        .await?;
    let parent = open_parent(pinned_root, relative)?;
    conformance
        .checkpoint(tool_call_id, "after_parent_open")
        .await?;
    let temporary = OsString::from(format!(".agent-write-{}.tmp", uuid::Uuid::new_v4()));
    let descriptor = openat(
        &parent.descriptor,
        temporary.as_os_str(),
        OFlag::O_WRONLY | OFlag::O_CREAT | OFlag::O_EXCL | OFlag::O_NOFOLLOW | OFlag::O_CLOEXEC,
        Mode::from_bits_truncate(0o600),
    )
    .map_err(|_| path_io_error())?;
    let result = async {
        let mut file = File::from(descriptor);
        file.write_all(bytes).map_err(|_| path_io_error())?;
        file.sync_all().map_err(|_| path_io_error())?;
        drop(file);
        conformance
            .checkpoint(tool_call_id, "after_temp_fsync_before_rename")
            .await?;
        renameat(
            &parent.descriptor,
            temporary.as_os_str(),
            &parent.descriptor,
            parent.leaf.as_os_str(),
        )
        .map_err(|_| path_io_error())?;
        fsync(&parent.descriptor).map_err(|_| path_io_error())?;
        Ok(())
    }
    .await;
    if result.is_err() {
        let _ = unlinkat(
            &parent.descriptor,
            temporary.as_os_str(),
            UnlinkatFlags::NoRemoveDir,
        );
    }
    result
}

#[cfg(target_os = "linux")]
/// 功能：判断 Linux stat mode 是否精确表示普通文件。
///
/// 输入：`fstat` 返回的 mode。
/// 输出：文件类型位为 `S_IFREG` 时为 true。
/// 不变量：权限位不影响类型判断；symlink、目录、FIFO、socket 和 device 均为 false。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn is_regular(mode: libc::mode_t) -> bool {
    mode & libc::S_IFMT == libc::S_IFREG
}

#[cfg(target_os = "linux")]
/// 功能：判断 Linux stat mode 是否精确表示目录。
///
/// 输入：`fstat` 返回的 mode。
/// 输出：文件类型位为 `S_IFDIR` 时为 true。
/// 不变量：权限位不影响类型判断；链接与其他特殊文件均为 false。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn is_directory(mode: libc::mode_t) -> bool {
    mode & libc::S_IFMT == libc::S_IFDIR
}

#[cfg(target_os = "linux")]
/// 功能：把 handle-relative 文件系统失败收敛为不泄漏宿主路径的固定 I/O 错误。
///
/// 输入：无；调用方丢弃底层 errno 与路径。
/// 输出：不可重试 `IoError/path_io_error`。
/// 不变量：错误消息与 details 不含 workspace、leaf、临时名或控制目录。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn path_io_error() -> AgentError {
    AgentError::new(ErrorCode::IoError, "Workspace file operation failed.")
        .with_details(json!({"kind":"path_io_error"}))
}

#[cfg(target_os = "linux")]
/// 功能：构造不泄漏 workspace 路径的 `file.read` 硬上限错误。
///
/// 输入：工具层冻结并传入句柄读取后端的最大字节数。
/// 输出：不可重试 `OutputLimitExceeded`，details 只公开固定上限。
/// 不变量：消息与 details 不包含文件路径、实际大小、内容或底层 errno。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn file_read_limit_error(max_bytes: usize) -> AgentError {
    AgentError::new(
        ErrorCode::OutputLimitExceeded,
        "Workspace file exceeds the read limit.",
    )
    .with_details(json!({
        "kind": ErrorCode::OutputLimitExceeded.detail_kind(),
        "limit": max_bytes
    }))
}

#[cfg(all(test, target_os = "linux"))]
mod tests {
    use std::fs::{self, OpenOptions};
    use std::io::Write;
    use std::os::unix::fs::{PermissionsExt, symlink};
    use std::path::{Path, PathBuf};
    use std::sync::Arc;
    use std::time::{Duration, Instant};

    use serde_json::json;
    use tempfile::tempdir;

    use super::{
        PathBoundaryConformance, load_barrier, open_workspace_root, read_pinned_file,
        write_pinned_file,
    };
    use crate::error::ErrorCode;
    use crate::tools::MAX_FILE_READ_BYTES;

    /// 功能：为一个真实文件 barrier 构造严格配置和 0700 控制目录。
    ///
    /// 输入：测试根、case/tool-call/checkpoint 三个关联值。
    /// 输出：可直接注入 handle-relative 后端的冻结 conformance 状态和控制目录路径。
    /// 不变量：配置与控制目录只位于当前临时测试根，不读取环境或用户 workspace。
    /// 失败：目录、权限、JSON 写入或 barrier 严格加载失败时测试返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn test_barrier(
        root: &Path,
        case_id: &str,
        tool_call_id: &str,
        checkpoint: &str,
    ) -> Result<(PathBoundaryConformance, PathBuf), Box<dyn std::error::Error>> {
        let control = root.join("control");
        fs::create_dir(&control)?;
        fs::set_permissions(&control, fs::Permissions::from_mode(0o700))?;
        let config_path = root.join("config.json");
        fs::write(
            &config_path,
            serde_json::to_vec(&json!({
                "schemaVersion":"0.1",
                "caseId":case_id,
                "toolCallId":tool_call_id,
                "checkpoint":checkpoint,
                "controlDirectory":control,
                "timeoutMs":5000
            }))?,
        )?;
        let barrier = load_barrier(&config_path).map_err(|()| "barrier config rejected")?;
        Ok((PathBoundaryConformance::Ready(Arc::new(barrier)), control))
    }

    /// 功能：等待 daemon 测试任务以 exclusive 文件发布 ready 控制记录。
    ///
    /// 输入：本测试独占的控制目录。
    /// 输出：五秒内观察到普通 `ready.json` 时成功。
    /// 不变量：仅检查固定临时路径，不读取 ready 内容或跟随测试外链接。
    /// 失败：期限耗尽时返回测试错误，避免 race 测试永久挂起。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn wait_ready(control: &Path) -> Result<(), Box<dyn std::error::Error>> {
        let deadline = Instant::now() + Duration::from_secs(5);
        while Instant::now() < deadline {
            if fs::symlink_metadata(control.join("ready.json"))
                .is_ok_and(|metadata| metadata.is_file())
            {
                return Ok(());
            }
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
        Err("ready checkpoint timed out".into())
    }

    /// 功能：以 create-new、完整写入和 fsync 发布 exact release 控制记录。
    ///
    /// 输入：控制目录与配置冻结的 case/tool-call/checkpoint。
    /// 输出：`release.json` durable 后成功。
    /// 不变量：不覆盖既有 release，不携带 workspace 内容或外部 canary。
    /// 失败：创建、JSON、写入或同步失败时测试返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn release(
        control: &Path,
        case_id: &str,
        tool_call_id: &str,
        checkpoint: &str,
    ) -> Result<(), Box<dyn std::error::Error>> {
        let bytes = serde_json::to_vec(&json!({
            "schemaVersion":"0.1",
            "caseId":case_id,
            "toolCallId":tool_call_id,
            "checkpoint":checkpoint,
            "state":"release"
        }))?;
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(control.join("release.json"))?;
        file.write_all(&bytes)?;
        file.sync_all()?;
        Ok(())
    }

    /// 功能：证明 root pathname 在 ready 后被重绑为 outside symlink 时读取仍命中启动期 root FD。
    ///
    /// 不变量：结果必须是 original root 内容，outside 文件字节保持不变。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn root_rebind_read_uses_pinned_directory() -> Result<(), Box<dyn std::error::Error>> {
        let temporary = tempdir()?;
        let workspace = temporary.path().join("workspace");
        let moved_workspace = temporary.path().join("workspace-moved");
        let outside = temporary.path().join("outside");
        fs::create_dir(&workspace)?;
        fs::create_dir(&outside)?;
        fs::write(workspace.join("target.txt"), b"inside\n")?;
        fs::write(outside.join("target.txt"), b"outside\n")?;
        let root_descriptor = Arc::new(open_workspace_root(&workspace)?);
        let (conformance, control) = test_barrier(
            temporary.path(),
            "read_workspace_root_rebind",
            "path-race-read-root-1",
            "before_parent_walk",
        )?;
        let task_root = workspace.clone();
        let task_descriptor = Arc::clone(&root_descriptor);
        let task_conformance = conformance.clone();
        let task = tokio::spawn(async move {
            read_pinned_file(
                &task_root,
                &task_descriptor,
                &task_conformance,
                "path-race-read-root-1",
                Path::new("target.txt"),
                MAX_FILE_READ_BYTES,
            )
            .await
        });
        wait_ready(&control).await?;
        fs::rename(&workspace, &moved_workspace)?;
        symlink(&outside, &workspace)?;
        release(
            &control,
            "read_workspace_root_rebind",
            "path-race-read-root-1",
            "before_parent_walk",
        )?;
        assert_eq!(task.await??, b"inside\n");
        assert_eq!(fs::read(outside.join("target.txt"))?, b"outside\n");
        Ok(())
    }

    /// 功能：验证 regular 文件静态大小超过工具硬上限时在 leaf checkpoint 前失败。
    ///
    /// 输入：大小为 `MAX_FILE_READ_BYTES + 1` 的 workspace regular 文件与 leaf checkpoint barrier。
    /// 输出：`OutputLimitExceeded`，且控制目录没有 ready 记录。
    /// 不变量：超限文件不能发布 after-leaf ready，也不能进入实际读取阶段。
    /// 失败：若发布 ready、等待 barrier 或返回其他错误码，测试失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn oversized_read_is_rejected_before_leaf_checkpoint()
    -> Result<(), Box<dyn std::error::Error>> {
        let temporary = tempdir()?;
        let workspace = temporary.path().join("workspace");
        let target = workspace.join("target.txt");
        fs::create_dir(&workspace)?;
        fs::write(&target, vec![b'x'; MAX_FILE_READ_BYTES + 1])?;
        let root_descriptor = Arc::new(open_workspace_root(&workspace)?);
        let (conformance, control) = test_barrier(
            temporary.path(),
            "read_static_oversize",
            "path-limit-read-static-1",
            "after_leaf_open_before_read",
        )?;
        let result = tokio::time::timeout(
            Duration::from_secs(1),
            read_pinned_file(
                &workspace,
                &root_descriptor,
                &conformance,
                "path-limit-read-static-1",
                Path::new("target.txt"),
                MAX_FILE_READ_BYTES,
            ),
        )
        .await?;
        let error = result.expect_err("static oversized file must fail before barrier");
        assert_eq!(error.code, ErrorCode::OutputLimitExceeded);
        assert!(!control.join("ready.json").exists());
        Ok(())
    }

    /// 功能：验证 leaf fstat 通过后文件增长会被 bounded reader 以超限错误拒绝。
    ///
    /// 输入：初始不超限的 regular 文件、after-leaf barrier，以及 release 前将同一 inode 扩大到上限以上的 mutation。
    /// 输出：`OutputLimitExceeded`，且任务在有界读取后及时结束。
    /// 不变量：checkpoint 后只从已打开 leaf FD 读取最多 `MAX_FILE_READ_BYTES + 1` 字节，不随文件增长无界分配。
    /// 失败：增长被静默接受、任务超时或返回其他错误码时测试失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn leaf_growth_after_fstat_is_bounded_and_rejected()
    -> Result<(), Box<dyn std::error::Error>> {
        let temporary = tempdir()?;
        let workspace = temporary.path().join("workspace");
        let target = workspace.join("target.txt");
        fs::create_dir(&workspace)?;
        fs::write(&target, b"inside\n")?;
        let root_descriptor = Arc::new(open_workspace_root(&workspace)?);
        let (conformance, control) = test_barrier(
            temporary.path(),
            "read_growth_after_fstat",
            "path-limit-read-growth-1",
            "after_leaf_open_before_read",
        )?;
        let task_root = workspace.clone();
        let task_descriptor = Arc::clone(&root_descriptor);
        let task_conformance = conformance.clone();
        let task = tokio::spawn(async move {
            read_pinned_file(
                &task_root,
                &task_descriptor,
                &task_conformance,
                "path-limit-read-growth-1",
                Path::new("target.txt"),
                MAX_FILE_READ_BYTES,
            )
            .await
        });
        wait_ready(&control).await?;
        let grown_size = u64::try_from(MAX_FILE_READ_BYTES)?.saturating_add(1);
        let file = OpenOptions::new().write(true).open(&target)?;
        file.set_len(grown_size)?;
        file.sync_all()?;
        drop(file);
        release(
            &control,
            "read_growth_after_fstat",
            "path-limit-read-growth-1",
            "after_leaf_open_before_read",
        )?;
        let result = tokio::time::timeout(Duration::from_secs(2), task).await??;
        let error = result.expect_err("growth beyond hard limit must fail");
        assert_eq!(error.code, ErrorCode::OutputLimitExceeded);
        Ok(())
    }

    /// 功能：证明 leaf 在 temp fsync 后被换成 outside symlink 时 renameat 只替换 pinned parent 名称。
    ///
    /// 不变量：outside 内容不变、moved old leaf 保持旧字节、原 leaf 名成为新 regular file。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn leaf_rebind_write_replaces_name_in_pinned_parent()
    -> Result<(), Box<dyn std::error::Error>> {
        let temporary = tempdir()?;
        let workspace = temporary.path().join("workspace");
        let leaf_directory = workspace.join("leaf");
        let target = leaf_directory.join("target.txt");
        let moved = leaf_directory.join("target-moved.txt");
        let outside = temporary.path().join("outside.txt");
        fs::create_dir(&workspace)?;
        fs::create_dir(&leaf_directory)?;
        fs::write(&target, b"old inside\n")?;
        fs::write(&outside, b"outside\n")?;
        let root_descriptor = Arc::new(open_workspace_root(&workspace)?);
        let (conformance, control) = test_barrier(
            temporary.path(),
            "write_leaf_rebind",
            "path-race-write-leaf-1",
            "after_temp_fsync_before_rename",
        )?;
        let task_root = workspace.clone();
        let task_descriptor = Arc::clone(&root_descriptor);
        let task_conformance = conformance.clone();
        let task = tokio::spawn(async move {
            write_pinned_file(
                &task_root,
                &task_descriptor,
                &task_conformance,
                "path-race-write-leaf-1",
                Path::new("leaf/target.txt"),
                b"new inside\n",
            )
            .await
        });
        wait_ready(&control).await?;
        fs::rename(&target, &moved)?;
        symlink(&outside, &target)?;
        release(
            &control,
            "write_leaf_rebind",
            "path-race-write-leaf-1",
            "after_temp_fsync_before_rename",
        )?;
        task.await??;
        assert_eq!(fs::read(&outside)?, b"outside\n");
        assert_eq!(fs::read(&moved)?, b"old inside\n");
        assert_eq!(fs::read(&target)?, b"new inside\n");
        assert!(!fs::symlink_metadata(&target)?.file_type().is_symlink());
        Ok(())
    }
}
