use std::fs::{File, OpenOptions};
use std::io::{ErrorKind, Read, Write};
use std::net::{Ipv4Addr, SocketAddrV4, TcpListener, TcpStream};
use std::path::{Path, PathBuf};
use std::thread;
use std::time::{Duration, Instant};

use chrono::{DateTime, SecondsFormat, Utc};
use serde::{Deserialize, Serialize};

use crate::error::{AgentError, ErrorCode};
use crate::protocol::parse_strict_value;

use super::sync_directory;

const CLAIM_DIRECTORY: &str = "writer.lock.d";
const OWNER_FILE: &str = "owner.json";
const MAX_OWNER_BYTES: u64 = 16 * 1024;
const INITIALIZATION_GRACE: Duration = Duration::from_secs(2);
const ACQUISITION_TIMEOUT: Duration = Duration::from_secs(3);
const RETRY_INTERVAL: Duration = Duration::from_millis(25);
const PROBE_TIMEOUT: Duration = Duration::from_millis(250);

/// writer-lock Schema 中固定的 literal loopback endpoint。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct WriterEndpoint {
    host: String,
    port: u16,
}

/// writer-lock Schema 中语言中立的实现身份。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct WriterImplementation {
    name: String,
    version: String,
    language: String,
}

/// portable writer lease 的封闭 owner.json DTO。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct WriterOwner {
    schema_version: String,
    protocol: String,
    session_id: String,
    token: String,
    endpoint: WriterEndpoint,
    pid: u64,
    created_at: String,
    implementation: WriterImplementation,
}

/// stale 判断时读取的不可变 owner 快照。
#[derive(Debug)]
enum StaleCandidate {
    Missing,
    Owner(Box<(WriterOwner, Vec<u8>)>),
}

/// contender 对 canonical claim 的保守分类。
#[derive(Debug)]
enum ClaimDisposition {
    Retry,
    Wait,
    Locked,
    Stale(Box<StaleCandidate>),
}

/// literal loopback socket 探测的三态结果。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ProbeDisposition {
    Connected,
    Refused,
    Ambiguous,
}

/// Rust 原生持有的 atomic-directory + TCP witness lease。
#[derive(Debug)]
pub(super) struct PortableWriterLease {
    directory: PathBuf,
    session_id: String,
    token: String,
    listener: Option<TcpListener>,
    released: bool,
}

impl PortableWriterLease {
    /// 功能：获取跨语言 atomic-directory + literal loopback writer lease。
    ///
    /// 输入：canonical Session 目录与匹配的 opaque Session ID。
    /// 输出：持有 OS listener 与 fresh 256-bit token 的 lease。
    /// 不变量：只有明确 connection-refused 或超龄缺失 metadata 才允许 stale 接管。
    /// 失败：live/歧义 owner 返回 retryable session_locked，非法 metadata 返回 writer_lock_invalid。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) fn acquire(directory: &Path, session_id: &str) -> Result<Self, AgentError> {
        validate_session_directory(directory)?;
        let deadline = Instant::now() + ACQUISITION_TIMEOUT;
        loop {
            if Instant::now() >= deadline {
                return Err(locked_error());
            }
            let (mut lease, owner) = Self::candidate(directory, session_id)?;
            let claim_path = directory.join(CLAIM_DIRECTORY);
            match std::fs::create_dir(&claim_path) {
                Ok(()) => {
                    sync_directory(directory)?;
                    write_owner(&claim_path, &owner)?;
                    return Ok(lease);
                }
                Err(error) if error.kind() == ErrorKind::AlreadyExists => {
                    lease.close_listener();
                }
                Err(error) => {
                    lease.close_listener();
                    return Err(AgentError::from(error));
                }
            }
            match inspect_claim(directory, session_id)? {
                ClaimDisposition::Retry => {}
                ClaimDisposition::Wait => wait_for_retry(deadline)?,
                ClaimDisposition::Locked => return Err(locked_error()),
                ClaimDisposition::Stale(candidate) => {
                    if !take_over_stale(directory, &lease.token, &candidate)? {
                        wait_for_retry(deadline)?;
                    }
                }
            }
        }
    }

    /// 功能：在 listener 仍存活时 token-CAS 并原子移走 canonical claim。
    ///
    /// 输入：当前持有的 portable lease。
    /// 输出：只允许本 lease 后续删除的 release sibling。
    /// 不变量：token 不匹配时 canonical 目录完全保留。
    /// 失败：metadata 变化、非法或 rename/sync 失败时 fail closed。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) fn prepare_release(&mut self) -> Result<Option<PathBuf>, AgentError> {
        if self.released {
            return Ok(None);
        }
        let claim_path = self.directory.join(CLAIM_DIRECTORY);
        let (owner, _) = read_owner(&claim_path, &self.session_id)?;
        if owner.token != self.token {
            return Err(invalid_lock_error("writer release token changed"));
        }
        let moved_path = self
            .directory
            .join(format!("writer.release.{}", self.token));
        if moved_path.try_exists()? {
            return Err(invalid_lock_error("writer release destination exists"));
        }
        std::fs::rename(&claim_path, &moved_path)?;
        sync_directory(&self.directory)?;
        Ok(Some(moved_path))
    }

    /// 功能：关闭 witness 并只删除 prepare 阶段产生的 owned release sibling。
    ///
    /// 输入：可选 moved 路径；advisory lock 必须已先释放。
    /// 输出：listener 已关闭且 owned sibling 已清除时成功。
    /// 不变量：永不删除 canonical claim 或其他 token 的 sibling。
    /// 失败：路径身份、删除或目录同步失败时返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) fn finish_release(&mut self, moved_path: Option<PathBuf>) -> Result<(), AgentError> {
        self.close_listener();
        if let Some(path) = moved_path {
            let expected = self
                .directory
                .join(format!("writer.release.{}", self.token));
            if path != expected {
                return Err(invalid_lock_error("writer release path is not owned"));
            }
            std::fs::remove_dir_all(path)?;
            sync_directory(&self.directory)?;
        }
        self.released = true;
        Ok(())
    }

    /// 功能：在尚无 advisory lock 时完整释放一个已发布 portable lease。
    ///
    /// 输入：当前 lease。
    /// 输出：token-safe prepare/finish 均完成时成功。
    /// 不变量：prepare 失败仍关闭本进程 listener，但不触碰 canonical claim。
    /// 失败：优先返回 prepare 错误，否则返回 finish 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) fn release_without_advisory(&mut self) -> Result<(), AgentError> {
        let prepared = self.prepare_release();
        let moved = prepared.as_ref().ok().cloned().flatten();
        let finished = self.finish_release(moved);
        prepared.and(finished)
    }

    /// 功能：先绑定 127.0.0.1:0 并生成 256-bit token，再构造 owner DTO。
    ///
    /// 输入：Session 目录和 ID。
    /// 输出：尚未发布 claim 的 lease candidate 与 owner。
    /// 不变量：不解析 hostname、不绑定 wildcard、不读取配置或凭据。
    /// 失败：OS 随机源或 loopback listener 不可用时 fail closed。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn candidate(directory: &Path, session_id: &str) -> Result<(Self, WriterOwner), AgentError> {
        let mut token_bytes = [0_u8; 32];
        getrandom::fill(&mut token_bytes).map_err(|error| {
            AgentError::new(
                ErrorCode::InternalError,
                format!("writer token random source failed: {error}"),
            )
        })?;
        let token = encode_lower_hex(&token_bytes);
        let listener = TcpListener::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))?;
        let address = listener.local_addr()?;
        if address.ip() != std::net::IpAddr::V4(Ipv4Addr::LOCALHOST) || address.port() == 0 {
            return Err(AgentError::new(
                ErrorCode::InternalError,
                "writer witness did not bind literal IPv4 loopback",
            ));
        }
        let owner = WriterOwner {
            schema_version: "0.1".to_owned(),
            protocol: "tcp-loopback-writer-lease-v1".to_owned(),
            session_id: session_id.to_owned(),
            token: token.clone(),
            endpoint: WriterEndpoint {
                host: "127.0.0.1".to_owned(),
                port: address.port(),
            },
            pid: u64::from(std::process::id()),
            created_at: Utc::now().to_rfc3339_opts(SecondsFormat::Millis, true),
            implementation: WriterImplementation {
                name: "qxnm-forge-rust".to_owned(),
                version: env!("CARGO_PKG_VERSION").to_owned(),
                language: "rust".to_owned(),
            },
        };
        Ok((
            Self {
                directory: directory.to_path_buf(),
                session_id: session_id.to_owned(),
                token,
                listener: Some(listener),
                released: false,
            },
            owner,
        ))
    }

    /// 功能：关闭 OS witness listener 并保持幂等。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn close_listener(&mut self) {
        self.listener.take();
    }
}

impl Drop for PortableWriterLease {
    /// 功能：异常路径中尽力执行 token-safe release，绝不删除不匹配 claim。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn drop(&mut self) {
        if !self.released {
            let _ = self.release_without_advisory();
        }
    }
}

/// 功能：创建 Session 目录并确认最终实体不是符号链接。
///
/// 输入：由已校验 Session ID 派生的目录。
/// 输出：目录存在、canonical 且父目录变更已同步时成功。
/// 不变量：不会把同名 symlink 或普通文件当作 Session 目录。
/// 失败：创建、canonical 校验或目录同步失败时返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(super) fn create_session_directory(directory: &Path) -> Result<(), AgentError> {
    match std::fs::create_dir(directory) {
        Ok(()) => sync_directory(directory.parent().ok_or_else(|| {
            AgentError::new(ErrorCode::InternalError, "session directory has no parent")
        })?)?,
        Err(error) if error.kind() == ErrorKind::AlreadyExists => {}
        Err(error) => return Err(AgentError::from(error)),
    }
    validate_session_directory(directory)
}

/// 功能：确认 Session 路径是 canonical 的真实目录而非 symlink。
///
/// 输入：预期绝对 Session 目录。
/// 输出：路径解析后仍与自身相同则成功。
/// 不变量：不依靠字符串前缀判断路径身份。
/// 失败：缺失、非目录、symlink 或 canonical 变化返回 writer_lock_invalid。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_session_directory(directory: &Path) -> Result<(), AgentError> {
    let metadata = std::fs::symlink_metadata(directory)?;
    if metadata.file_type().is_symlink() || !metadata.is_dir() {
        return Err(invalid_lock_error("session path is not a real directory"));
    }
    if std::fs::canonicalize(directory)? != directory {
        return Err(invalid_lock_error("session directory is not canonical"));
    }
    Ok(())
}

/// 功能：用同目录临时文件、fsync 和 atomic rename 发布完整 owner.json。
///
/// 输入：本 contender 原子创建的 claim 目录和 owner DTO。
/// 输出：metadata 与目录项持久化时成功。
/// 不变量：临时文件 create_new，最终路径不覆盖既有对象。
/// 失败：编码、写入、同步或 rename 错误保留 fail-closed claim。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn write_owner(claim_path: &Path, owner: &WriterOwner) -> Result<(), AgentError> {
    let raw = serde_json::to_vec(owner)
        .map_err(|error| AgentError::new(ErrorCode::InternalError, error.to_string()))?;
    if raw.len() as u64 > MAX_OWNER_BYTES {
        return Err(invalid_lock_error("writer owner exceeds byte limit"));
    }
    let temporary = claim_path.join(format!(".owner.{}.tmp", owner.token));
    let mut file = OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(&temporary)?;
    file.write_all(&raw)?;
    file.sync_all()?;
    drop(file);
    let destination = claim_path.join(OWNER_FILE);
    if destination.try_exists()? {
        return Err(invalid_lock_error("writer owner destination exists"));
    }
    std::fs::rename(temporary, destination)?;
    sync_directory(claim_path)
}

/// 功能：严格读取 claim 并保守分类其 liveness/stale 状态。
///
/// 输入：canonical Session 目录和期望 Session ID。
/// 输出：retry、初始化等待、locked 或携带快照的 stale。
/// 不变量：owner 完整验证前绝不连接 endpoint，外部 host 永不探测。
/// 失败：symlink、重复键、超大或其他非法 metadata 返回 writer_lock_invalid。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn inspect_claim(directory: &Path, session_id: &str) -> Result<ClaimDisposition, AgentError> {
    let claim_path = directory.join(CLAIM_DIRECTORY);
    let metadata = match std::fs::symlink_metadata(&claim_path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(ClaimDisposition::Retry),
        Err(error) => return Err(AgentError::from(error)),
    };
    if metadata.file_type().is_symlink() || !metadata.is_dir() {
        return Err(invalid_lock_error("writer claim is not a real directory"));
    }
    match read_owner(&claim_path, session_id) {
        Ok((owner, raw)) => {
            if probe_owner(&owner) == ProbeDisposition::Refused {
                Ok(ClaimDisposition::Stale(Box::new(StaleCandidate::Owner(
                    Box::new((owner, raw)),
                ))))
            } else {
                Ok(ClaimDisposition::Locked)
            }
        }
        Err(error) if error.code == ErrorCode::SessionNotFound => {
            let initializing = metadata
                .modified()
                .ok()
                .and_then(|modified| modified.elapsed().ok())
                .is_none_or(|age| age < INITIALIZATION_GRACE);
            if initializing {
                Ok(ClaimDisposition::Wait)
            } else {
                Ok(ClaimDisposition::Stale(Box::new(StaleCandidate::Missing)))
            }
        }
        Err(error) => Err(error),
    }
}

/// 功能：有界、严格且拒绝 symlink 地读取 owner.json。
///
/// 输入：已确认为真实目录的 claim 路径和当前 Session ID。
/// 输出：通过完整 Schema 值域检查的 owner 与原始字节。
/// 不变量：文件大小、UTF-8、duplicate key、host 和字段 allowlist 均先于 socket probe。
/// 失败：缺失使用 SessionNotFound 内部信号，其他非法输入返回 writer_lock_invalid。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn read_owner(
    claim_path: &Path,
    expected_session_id: &str,
) -> Result<(WriterOwner, Vec<u8>), AgentError> {
    let claim_before = std::fs::canonicalize(claim_path)?;
    if claim_before != claim_path {
        return Err(invalid_lock_error("writer claim path changed"));
    }
    let owner_path = claim_path.join(OWNER_FILE);
    let metadata = match std::fs::symlink_metadata(&owner_path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == ErrorKind::NotFound => {
            return Err(AgentError::new(
                ErrorCode::SessionNotFound,
                "writer owner metadata is not initialized",
            ));
        }
        Err(error) => return Err(AgentError::from(error)),
    };
    if metadata.file_type().is_symlink()
        || !metadata.is_file()
        || metadata.len() == 0
        || metadata.len() > MAX_OWNER_BYTES
    {
        return Err(invalid_lock_error(
            "writer owner must be a bounded regular file",
        ));
    }
    let mut file = open_owner_without_follow(&owner_path)?;
    let mut raw = Vec::with_capacity(metadata.len() as usize);
    Read::by_ref(&mut file)
        .take(MAX_OWNER_BYTES + 1)
        .read_to_end(&mut raw)?;
    if raw.is_empty() || raw.len() as u64 > MAX_OWNER_BYTES {
        return Err(invalid_lock_error("writer owner byte length is invalid"));
    }
    if std::fs::canonicalize(claim_path)? != claim_before {
        return Err(invalid_lock_error("writer claim changed while reading"));
    }
    let text =
        std::str::from_utf8(&raw).map_err(|_| invalid_lock_error("writer owner is not UTF-8"))?;
    let value = parse_strict_value(text)
        .map_err(|_| invalid_lock_error("writer owner is not strict JSON"))?;
    let owner: WriterOwner = serde_json::from_value(value)
        .map_err(|_| invalid_lock_error("writer owner shape is invalid"))?;
    validate_owner(&owner, expected_session_id)?;
    Ok((owner, raw))
}

/// 功能：按 writer-lock Schema 值域验证反序列化 owner。
///
/// 输入：封闭 owner 与期望 Session ID。
/// 输出：全部固定协议和值域成立时成功。
/// 不变量：host 必须逐字等于 127.0.0.1，token 必须 64 位小写十六进制。
/// 失败：任一不变量返回 writer_lock_invalid。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_owner(owner: &WriterOwner, expected_session_id: &str) -> Result<(), AgentError> {
    let valid_language = matches!(
        owner.implementation.language.as_str(),
        "dotnet" | "rust" | "go" | "typescript" | "python"
    );
    let valid_time =
        owner.created_at.ends_with('Z') && DateTime::parse_from_rfc3339(&owner.created_at).is_ok();
    if owner.schema_version != "0.1"
        || owner.protocol != "tcp-loopback-writer-lease-v1"
        || owner.session_id != expected_session_id
        || !valid_token(&owner.token)
        || owner.endpoint.host != "127.0.0.1"
        || owner.endpoint.port == 0
        || owner.pid == 0
        || owner.pid > 9_007_199_254_740_991
        || !valid_time
        || owner.implementation.name.is_empty()
        || owner.implementation.name.chars().count() > 128
        || owner.implementation.version.is_empty()
        || owner.implementation.version.chars().count() > 64
        || !valid_language
    {
        return Err(invalid_lock_error("writer owner value is invalid"));
    }
    Ok(())
}

/// 功能：打开 owner 文件且在 Unix 使用 O_NOFOLLOW 拒绝最终 symlink。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[cfg(unix)]
fn open_owner_without_follow(path: &Path) -> Result<File, AgentError> {
    use std::os::unix::fs::OpenOptionsExt;

    Ok(OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_NOFOLLOW)
        .open(path)?)
}

/// 功能：在无 O_NOFOLLOW 标准扩展的平台打开已由 symlink_metadata 验证的 owner。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[cfg(not(unix))]
fn open_owner_without_follow(path: &Path) -> Result<File, AgentError> {
    Ok(OpenOptions::new().read(true).open(path)?)
}

/// 功能：直连 literal IPv4 loopback 并保守区分 refused 与歧义错误。
///
/// 输入：已经完整验证的 owner。
/// 输出：connected、explicit refused 或 ambiguous。
/// 不变量：不解析 DNS、不使用代理、不等待应用响应。
/// 失败：timeout、权限和未知错误归为 ambiguous/live。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn probe_owner(owner: &WriterOwner) -> ProbeDisposition {
    let address = SocketAddrV4::new(Ipv4Addr::LOCALHOST, owner.endpoint.port);
    match TcpStream::connect_timeout(&address.into(), PROBE_TIMEOUT) {
        Ok(stream) => {
            drop(stream);
            ProbeDisposition::Connected
        }
        Err(error) if error.kind() == ErrorKind::ConnectionRefused => ProbeDisposition::Refused,
        Err(_) => ProbeDisposition::Ambiguous,
    }
}

/// 功能：用唯一 sibling rename 选出 stale takeover 唯一赢家并二次验证。
///
/// 输入：Session 目录、contender token 与 probe 快照。
/// 输出：成功删除原 stale candidate 为 true；竞争丢失为 false。
/// 不变量：变化、新生或二次 probe 歧义的 owner 永不删除。
/// 失败：权限、metadata 或同步异常 fail closed。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn take_over_stale(
    directory: &Path,
    contender_token: &str,
    candidate: &StaleCandidate,
) -> Result<bool, AgentError> {
    let claim_path = directory.join(CLAIM_DIRECTORY);
    let moved_path = directory.join(format!("writer.stale.{contender_token}"));
    if moved_path.try_exists()? {
        return Err(locked_error());
    }
    match std::fs::rename(&claim_path, &moved_path) {
        Ok(()) => sync_directory(directory)?,
        Err(error) if matches!(error.kind(), ErrorKind::NotFound | ErrorKind::AlreadyExists) => {
            return Ok(false);
        }
        Err(_) => return Err(locked_error()),
    }
    match revalidate_stale(&moved_path, candidate) {
        Ok(true) => {}
        Ok(false) => {
            restore_moved_claim(directory, &moved_path)?;
            return Err(locked_error());
        }
        Err(error) => {
            let _ = restore_moved_claim(directory, &moved_path);
            return Err(error);
        }
    }
    std::fs::remove_dir_all(moved_path)?;
    sync_directory(directory)?;
    Ok(true)
}

/// 功能：确认 moved 对象仍是同一 stale candidate 且 listener 仍明确缺失。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn revalidate_stale(moved_path: &Path, candidate: &StaleCandidate) -> Result<bool, AgentError> {
    match candidate {
        StaleCandidate::Missing => Ok(!moved_path.join(OWNER_FILE).try_exists()?),
        StaleCandidate::Owner(snapshot) => {
            let (owner, raw) = snapshot.as_ref();
            let (current, current_raw) = read_owner(moved_path, &owner.session_id)?;
            Ok(current.token == owner.token
                && current_raw == *raw
                && probe_owner(&current) == ProbeDisposition::Refused)
        }
    }
}

/// 功能：重验证失败时在 canonical 名称仍空闲的情况下恢复 moved claim。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn restore_moved_claim(directory: &Path, moved_path: &Path) -> Result<(), AgentError> {
    let claim_path = directory.join(CLAIM_DIRECTORY);
    if claim_path.try_exists()? {
        return Err(locked_error());
    }
    std::fs::rename(moved_path, claim_path)?;
    sync_directory(directory)
}

/// 功能：执行不越过 acquisition deadline 的短退避。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn wait_for_retry(deadline: Instant) -> Result<(), AgentError> {
    let remaining = deadline.saturating_duration_since(Instant::now());
    if remaining.is_zero() {
        return Err(locked_error());
    }
    thread::sleep(RETRY_INTERVAL.min(remaining));
    if Instant::now() >= deadline {
        return Err(locked_error());
    }
    Ok(())
}

/// 功能：验证 token 是 64 字节小写十六进制文本。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn valid_token(token: &str) -> bool {
    token.len() == 64
        && token
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

/// 功能：把 32 个随机字节编码为固定 64 位小写十六进制 token。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn encode_lower_hex(bytes: &[u8; 32]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut output = String::with_capacity(64);
    for byte in bytes {
        output.push(char::from(HEX[usize::from(byte >> 4)]));
        output.push(char::from(HEX[usize::from(byte & 0x0f)]));
    }
    output
}

/// 功能：构造客户端可按 details.kind 判断的 retryable session_locked 错误。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn locked_error() -> AgentError {
    AgentError::new(ErrorCode::SessionLocked, "session writer is locked")
        .retryable(true)
        .with_details(serde_json::json!({"kind":"session_locked"}))
}

/// 功能：构造拒绝自动接管的不可信 writer_lock_invalid 错误。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn invalid_lock_error(message: &str) -> AgentError {
    AgentError::new(ErrorCode::JournalCorrupt, message)
        .with_details(serde_json::json!({"kind":"writer_lock_invalid"}))
}

#[cfg(test)]
mod tests {
    use std::net::{Ipv4Addr, SocketAddrV4, TcpListener};

    use tempfile::tempdir;

    use super::{
        CLAIM_DIRECTORY, OWNER_FILE, PortableWriterLease, WriterEndpoint, WriterImplementation,
        WriterOwner, create_session_directory, write_owner,
    };
    use crate::error::ErrorCode;

    /// 功能：验证 clean release 保留 lock 父布局并移除 canonical portable claim。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn publishes_and_releases_portable_owner() -> Result<(), crate::error::AgentError> {
        let root = tempdir()?;
        let directory = root.path().join("session-rust-owner");
        create_session_directory(&directory)?;
        let mut lease = PortableWriterLease::acquire(&directory, "session-rust-owner")?;
        assert!(directory.join(CLAIM_DIRECTORY).join(OWNER_FILE).is_file());
        let moved = lease.prepare_release()?;
        lease.finish_release(moved)?;
        assert!(!directory.join(CLAIM_DIRECTORY).exists());
        Ok(())
    }

    /// 功能：验证可连接但不响应的其他语言 witness 会被保守判定为 locked。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn rejects_live_unresponsive_owner() -> Result<(), crate::error::AgentError> {
        let root = tempdir()?;
        let directory = root.path().join("session-rust-live");
        create_session_directory(&directory)?;
        let listener = TcpListener::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))?;
        let owner = fixture_owner(
            "session-rust-live",
            "1".repeat(64),
            listener.local_addr()?.port(),
        );
        let claim = directory.join(CLAIM_DIRECTORY);
        std::fs::create_dir(&claim)?;
        write_owner(&claim, &owner)?;
        let error = PortableWriterLease::acquire(&directory, "session-rust-live")
            .expect_err("live owner must block contender");
        assert_eq!(error.code, ErrorCode::SessionLocked);
        assert_eq!(error.code.rpc_code(), -32002);
        assert_eq!(error.details["kind"], "session_locked");
        drop(listener);
        Ok(())
    }

    /// 功能：验证外部 host 注入在任何 socket 连接前返回 writer_lock_invalid。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn rejects_external_host_owner() -> Result<(), crate::error::AgentError> {
        let root = tempdir()?;
        let directory = root.path().join("session-rust-host");
        create_session_directory(&directory)?;
        let mut owner = fixture_owner("session-rust-host", "2".repeat(64), 443);
        owner.endpoint.host = "203.0.113.8".to_owned();
        let claim = directory.join(CLAIM_DIRECTORY);
        std::fs::create_dir(&claim)?;
        write_owner(&claim, &owner)?;
        let error = PortableWriterLease::acquire(&directory, "session-rust-host")
            .expect_err("external owner host must be rejected");
        assert_eq!(error.details["kind"], "writer_lock_invalid");
        Ok(())
    }

    /// 功能：验证明确 connection-refused 的 crash owner 可被原子接管且 token 不复用。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn takes_over_explicitly_refused_owner() -> Result<(), crate::error::AgentError> {
        let root = tempdir()?;
        let directory = root.path().join("session-rust-stale");
        create_session_directory(&directory)?;
        let listener = TcpListener::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))?;
        let port = listener.local_addr()?.port();
        drop(listener);
        let old_token = "3".repeat(64);
        let owner = fixture_owner("session-rust-stale", old_token.clone(), port);
        let claim = directory.join(CLAIM_DIRECTORY);
        std::fs::create_dir(&claim)?;
        write_owner(&claim, &owner)?;
        let mut lease = PortableWriterLease::acquire(&directory, "session-rust-stale")?;
        assert_ne!(lease.token, old_token);
        let moved = lease.prepare_release()?;
        lease.finish_release(moved)?;
        Ok(())
    }

    /// 功能：验证 release 前 owner token 被篡改时 canonical claim 原样保留。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn preserves_claim_when_release_token_changes() -> Result<(), crate::error::AgentError> {
        let root = tempdir()?;
        let directory = root.path().join("session-rust-release");
        create_session_directory(&directory)?;
        let mut lease = PortableWriterLease::acquire(&directory, "session-rust-release")?;
        let claim = directory.join(CLAIM_DIRECTORY);
        std::fs::remove_file(claim.join(OWNER_FILE))?;
        let replacement = fixture_owner(
            "session-rust-release",
            "b".repeat(64),
            lease
                .listener
                .as_ref()
                .expect("listener exists")
                .local_addr()?
                .port(),
        );
        write_owner(&claim, &replacement)?;
        let error = lease
            .prepare_release()
            .expect_err("changed token must fail release");
        assert_eq!(error.details["kind"], "writer_lock_invalid");
        lease.finish_release(None)?;
        assert!(claim.is_dir());
        Ok(())
    }

    /// 功能：验证 owner duplicate JSON key 在 socket probe 前被拒绝。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn rejects_duplicate_owner_keys() -> Result<(), crate::error::AgentError> {
        let root = tempdir()?;
        let directory = root.path().join("session-rust-duplicate");
        create_session_directory(&directory)?;
        let claim = directory.join(CLAIM_DIRECTORY);
        std::fs::create_dir(&claim)?;
        let raw = format!(
            "{{\"schemaVersion\":\"0.1\",\"schemaVersion\":\"0.1\",\"protocol\":\"tcp-loopback-writer-lease-v1\",\"sessionId\":\"session-rust-duplicate\",\"token\":\"{}\",\"endpoint\":{{\"host\":\"127.0.0.1\",\"port\":1}},\"pid\":1,\"createdAt\":\"2026-07-14T00:00:00Z\",\"implementation\":{{\"name\":\"qxnm-forge-go\",\"version\":\"0.1.0\",\"language\":\"go\"}}}}",
            "4".repeat(64)
        );
        std::fs::write(claim.join(OWNER_FILE), raw)?;
        let error = PortableWriterLease::acquire(&directory, "session-rust-duplicate")
            .expect_err("duplicate owner key must be rejected");
        assert_eq!(error.details["kind"], "writer_lock_invalid");
        Ok(())
    }

    /// 功能：构造符合公共 Schema 的其他语言 owner 测试值。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn fixture_owner(session_id: &str, token: String, port: u16) -> WriterOwner {
        WriterOwner {
            schema_version: "0.1".to_owned(),
            protocol: "tcp-loopback-writer-lease-v1".to_owned(),
            session_id: session_id.to_owned(),
            token,
            endpoint: WriterEndpoint {
                host: "127.0.0.1".to_owned(),
                port,
            },
            pid: 9001,
            created_at: "2026-07-14T00:00:00Z".to_owned(),
            implementation: WriterImplementation {
                name: "qxnm-forge-go".to_owned(),
                version: "0.1.0".to_owned(),
                language: "go".to_owned(),
            },
        }
    }
}
