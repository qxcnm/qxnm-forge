use std::collections::{BTreeSet, HashSet};
use std::fs::{File, OpenOptions};
use std::io::{BufRead, BufReader, Read, Write};
use std::path::{Path, PathBuf};

use chrono::{DateTime, Utc};
use fs2::FileExt;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use tokio::task;
use uuid::Uuid;

use crate::error::{AgentError, ErrorCode};
use crate::protocol::{JsonRpcResponse, parse_strict_value};
use crate::session::{
    JOURNAL_VERSION, JournalRecord, RECORD_SCHEMA_VERSION, SessionHeader, SessionStore,
};

#[cfg(unix)]
use std::os::unix::fs::{OpenOptionsExt as _, PermissionsExt as _};
#[cfg(windows)]
use std::os::windows::fs::{MetadataExt as _, OpenOptionsExt as _, OsStrExt as _};

const ARCHIVE_SCHEMA_VERSION: &str = "0.1";
const DEFAULT_PAGE_LIMIT: usize = 64;
const MAX_PAGE_LIMIT: usize = 128;
const MAX_ARCHIVE_BYTES: u64 = 1_048_576;
const MAX_ARCHIVED_SESSIONS: usize = 4_096;
const MAX_DISCOVERED_ENTRIES: usize = 16_384;
const MAX_JOURNAL_LINE_BYTES: usize = 4 * 1024 * 1024;
const MAX_JOURNAL_RECORDS: usize = 100_000;
const MAX_DELETE_ENTRIES: usize = 100_000;
const MAX_DELETE_DEPTH: usize = 64;
const MAX_FRAME_BYTES: usize = 1_048_576;

const RECORD_KINDS: &[&str] = &[
    "message.appended",
    "event.emitted",
    "run.accepted",
    "run.started",
    "run.cancellation_requested",
    "run.terminal",
    "turn.started",
    "turn.completed",
    "provider.attempt",
    "tool.intent",
    "tool.result",
    "approval.requested",
    "approval.resolved",
    "queue.appended",
    "queue.consumed",
    "context.compacted",
    "model.selected",
    "session.metadata",
    "branch.selected",
    "artifact.created",
    "faux.configured",
    "extension",
];

/// application service 可返回的品牌中立 Session 摘要。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionSummary {
    pub session_id: String,
    pub title: String,
    pub project: String,
    pub updated_at: DateTime<Utc>,
    pub archived: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub status: Option<String>,
}

/// `session/list` 的有界分页结果。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionListResult {
    pub sessions: Vec<SessionSummary>,
    pub next_cursor: Option<String>,
    pub has_more: bool,
}

/// 归档状态文件的严格闭合 DTO。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ArchiveDocument {
    schema_version: String,
    archived_session_ids: Vec<String>,
}

/// journal 流式检查后保留的最小安全投影。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug)]
struct JournalInspection {
    title: String,
    project: String,
    updated_at: DateTime<Utc>,
}

/// 持有归档文档跨进程独占锁的 RAII 句柄。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
struct LifecycleLock {
    _file: File,
}

/// Rust 原生 Session 列表、归档、恢复与永久删除 application service。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Clone)]
pub struct SessionLifecycleService {
    store: SessionStore,
    sessions_root: PathBuf,
    lifecycle_root: PathBuf,
    archive_path: PathBuf,
    tombstone_root: PathBuf,
}

impl SessionLifecycleService {
    /// 功能：从 SessionStore 的 canonical state root 建立固定生命周期状态边界。
    ///
    /// 输入：已经初始化并验证 `stateRoot/sessions` 的 SessionStore。
    /// 输出：归档状态位于 `stateRoot/session-lifecycle`、tombstone 位于 sessions 根的服务。
    /// 不变量：Provider、credential、数据库等 stateRoot 同级目录永远不作为 Session 扫描或删除。
    /// 失败：固定目录缺失、为链接/特殊对象、越界或无法创建时返回脱敏 store 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn new(store: SessionStore) -> Result<Self, AgentError> {
        let state_root = store.state_root().to_path_buf();
        let sessions_root = state_root.join("sessions");
        let lifecycle_root = state_root.join("session-lifecycle");
        let tombstone_root = sessions_root.join(".session-tombstones");
        ensure_private_real_directory(&state_root)?;
        ensure_private_real_directory(&sessions_root)?;
        ensure_private_real_directory(&lifecycle_root)?;
        ensure_private_real_directory(&tombstone_root)?;
        ensure_private_real_directory(&sessions_root.join(".session-coordination"))?;
        Ok(Self {
            store,
            sessions_root,
            archive_path: lifecycle_root.join("archive-state.json"),
            lifecycle_root,
            tombstone_root,
        })
    }

    /// 功能：严格扫描真实 Session journal 并返回稳定排序的脱敏摘要。
    ///
    /// 输入：固定 sessions 根及外置归档状态文档。
    /// 输出：按 updatedAt 降序、再按 ASCII Session ID 升序的全部有界摘要。
    /// 不变量：只流式读取完整 LF 记录；不恢复、不追加且不返回 transcript、路径或 Provider 数据。
    /// 失败：归档状态或根目录不可安全读取时 fail closed；单个损坏 journal 降级为固定摘要。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn list(&self) -> Result<Vec<SessionSummary>, AgentError> {
        let service = self.clone();
        task::spawn_blocking(move || service.list_blocking())
            .await
            .map_err(|_| lifecycle_store_error("session_lifecycle_store_io"))?
    }

    /// 功能：在 Session 静止且 journal 健康时 durable 设置归档状态。
    ///
    /// 输入：符合 portable opaque ID 的 Session ID。
    /// 输出：`archived=true` 且与归档文档一致的真实摘要。
    /// 不变量：不修改 journal；跨进程状态锁与 Session advisory lock 覆盖检查和原子发布。
    /// 失败：不存在、损坏、live writer、状态锁冲突或 I/O 失败时返回不含宿主路径的错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn archive(&self, session_id: &str) -> Result<SessionSummary, AgentError> {
        self.set_archived(session_id, true).await
    }

    /// 功能：在 Session 静止且 journal 健康时 durable 移除归档状态。
    ///
    /// 输入：符合 portable opaque ID 的 Session ID。
    /// 输出：`archived=false` 且与归档文档一致的真实摘要。
    /// 不变量：不修改 journal；幂等恢复仍原子发布严格归档文档。
    /// 失败：不存在、损坏、live writer、状态锁冲突或 I/O 失败时返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn restore(&self, session_id: &str) -> Result<SessionSummary, AgentError> {
        self.set_archived(session_id, false).await
    }

    /// 功能：验证静止健康 Session，原子移入固定 tombstone 并安全删除普通目录树。
    ///
    /// 输入：符合 portable opaque ID 的 Session ID。
    /// 输出：tombstone、归档元数据和目录同步均完成时成功。
    /// 不变量：coordination lease 覆盖 rename 到最终清理；不跟随 symlink/reparse/device；中断后同 ID 可续删。
    /// 失败：不存在、损坏、live writer、非普通树、遍历上限、锁或 I/O 异常时 fail closed。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn delete(&self, session_id: &str) -> Result<(), AgentError> {
        validate_session_id(session_id)?;
        let service = self.clone();
        let session_id = session_id.to_owned();
        let worker_session_id = session_id.clone();
        task::spawn_blocking(move || service.delete_blocking(&worker_session_id))
            .await
            .map_err(|_| lifecycle_delete_unsafe(&session_id))?
    }

    /// 功能：按 opaque offset cursor 和页大小构造完整 JSON-RPC 帧不超限的列表页。
    ///
    /// 输入：稳定排序摘要、零基 offset、1..128 limit 及原样响应 ID。
    /// 输出：必含 sessions/nextCursor/hasMore 的最大安全前缀页。
    /// 不变量：`hasMore=false` 时 cursor 显式 null；完整响应序列化长度不超过 1 MiB。
    /// 失败：即使空页也无法满足帧上限或参数越界时返回固定 store/参数错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn create_page(
        summaries: &[SessionSummary],
        offset: usize,
        limit: usize,
        request_id: &Value,
    ) -> Result<SessionListResult, AgentError> {
        if !(1..=MAX_PAGE_LIMIT).contains(&limit) || offset > MAX_DISCOVERED_ENTRIES {
            return Err(invalid_lifecycle_param("limit"));
        }
        let bounded_offset = offset.min(summaries.len());
        let mut count = limit.min(summaries.len().saturating_sub(bounded_offset));
        loop {
            if count == 0 && bounded_offset < summaries.len() {
                return Err(lifecycle_store_error("session_lifecycle_store_invalid"));
            }
            let has_more = bounded_offset + count < summaries.len();
            let result = SessionListResult {
                sessions: summaries[bounded_offset..bounded_offset + count].to_vec(),
                next_cursor: has_more.then(|| format!("v1:{}", bounded_offset + count)),
                has_more,
            };
            let value = serde_json::to_value(&result)
                .map_err(|_| lifecycle_store_error("session_lifecycle_store_invalid"))?;
            let frame = JsonRpcResponse::success(request_id.clone(), value);
            let length = serde_json::to_vec(&frame)
                .map_err(|_| lifecycle_store_error("session_lifecycle_store_invalid"))?
                .len();
            if length <= MAX_FRAME_BYTES {
                return Ok(result);
            }
            if count == 0 {
                return Err(lifecycle_store_error("session_lifecycle_store_invalid"));
            }
            count -= 1;
        }
    }

    /// 功能：返回冻结的默认 Session 页大小。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub const fn default_page_limit() -> usize {
        DEFAULT_PAGE_LIMIT
    }

    /// 功能：在阻塞 worker 中扫描 sessions 根与归档文档。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn list_blocking(&self) -> Result<Vec<SessionSummary>, AgentError> {
        let archive = {
            let _state_lock = self.acquire_lifecycle_lock()?;
            self.read_archive_unlocked()?
        };
        let archived = archive
            .archived_session_ids
            .into_iter()
            .collect::<BTreeSet<_>>();
        let entries = std::fs::read_dir(&self.sessions_root)
            .map_err(|_| lifecycle_store_error("session_lifecycle_store_io"))?;
        let mut summaries = Vec::new();
        let mut observed = 0usize;
        for entry in entries {
            observed = observed.saturating_add(1);
            if observed > MAX_DISCOVERED_ENTRIES {
                return Err(lifecycle_store_error("session_lifecycle_store_invalid"));
            }
            let entry = entry.map_err(|_| lifecycle_store_error("session_lifecycle_store_io"))?;
            let Some(session_id) = entry.file_name().to_str().map(str::to_owned) else {
                continue;
            };
            if !session_id_is_valid(&session_id) || !is_real_directory(&entry.path()) {
                continue;
            }
            let journal = entry.path().join("journal.jsonl");
            match std::fs::symlink_metadata(&journal) {
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => continue,
                Err(_) => {
                    summaries.push(corrupt_summary(&session_id, archived.contains(&session_id)));
                    continue;
                }
                Ok(_) => {}
            }
            let summary = match inspect_journal(&session_id, &journal, true) {
                Ok(inspection) => SessionSummary {
                    session_id: session_id.clone(),
                    title: inspection.title,
                    project: inspection.project,
                    updated_at: inspection.updated_at,
                    archived: archived.contains(&session_id),
                    status: None,
                },
                Err(_) => corrupt_summary(&session_id, archived.contains(&session_id)),
            };
            summaries.push(summary);
        }
        summaries.sort_by(|left, right| {
            right
                .updated_at
                .cmp(&left.updated_at)
                .then_with(|| left.session_id.as_bytes().cmp(right.session_id.as_bytes()))
        });
        Ok(summaries)
    }

    /// 功能：在阻塞 worker 中原子设置或移除单个归档 ID。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn set_archived(
        &self,
        session_id: &str,
        archived: bool,
    ) -> Result<SessionSummary, AgentError> {
        validate_session_id(session_id)?;
        let service = self.clone();
        let session_id = session_id.to_owned();
        task::spawn_blocking(move || service.set_archived_blocking(&session_id, archived))
            .await
            .map_err(|_| lifecycle_store_error("session_lifecycle_store_io"))?
    }

    /// 功能：在生命周期与 Session 两层锁内提交归档状态。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn set_archived_blocking(
        &self,
        session_id: &str,
        archived: bool,
    ) -> Result<SessionSummary, AgentError> {
        let _state_lock = self.acquire_lifecycle_lock()?;
        let directory = self.resolve_session_directory(session_id)?;
        let _session_lock = acquire_session_mutation_lock(&directory, session_id)?;
        reject_writer_presence(&directory, session_id)?;
        let inspection = inspect_journal(session_id, &directory.join("journal.jsonl"), false)?;
        let document = self.read_archive_unlocked()?;
        let mut ids = document
            .archived_session_ids
            .into_iter()
            .collect::<BTreeSet<_>>();
        if archived {
            ids.insert(session_id.to_owned());
        } else {
            ids.remove(session_id);
        }
        if ids.len() > MAX_ARCHIVED_SESSIONS {
            return Err(lifecycle_store_error("session_lifecycle_store_invalid"));
        }
        self.write_archive_unlocked(&ArchiveDocument {
            schema_version: ARCHIVE_SCHEMA_VERSION.to_owned(),
            archived_session_ids: ids.into_iter().collect(),
        })?;
        Ok(SessionSummary {
            session_id: session_id.to_owned(),
            title: inspection.title,
            project: inspection.project,
            updated_at: inspection.updated_at,
            archived,
            status: None,
        })
    }

    /// 功能：在固定 coordination 与生命周期锁内完成可续删 tombstone 状态机。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn delete_blocking(&self, session_id: &str) -> Result<(), AgentError> {
        let _coordination = self
            .store
            .acquire_delete_coordination(session_id)
            .map_err(|_| lifecycle_busy(session_id))?;
        let _state_lock = self.acquire_lifecycle_lock()?;
        let session = self.sessions_root.join(session_id);
        let tombstone = self.tombstone_root.join(session_id);
        let session_exists = path_exists_no_follow(&session)?;
        let tombstone_exists = path_exists_no_follow(&tombstone)?;
        if session_exists && tombstone_exists {
            return Err(lifecycle_delete_unsafe(session_id));
        }
        if !session_exists {
            if !tombstone_exists {
                return Err(lifecycle_not_found(session_id));
            }
            require_real_directory_for_session(&tombstone, session_id)?;
            return self.complete_tombstone_delete(&tombstone, session_id);
        }

        let directory = self.resolve_session_directory(session_id)?;
        let session_lock = acquire_session_mutation_lock(&directory, session_id)?;
        reject_writer_presence(&directory, session_id)?;
        let _ = inspect_journal(session_id, &directory.join("journal.jsonl"), false)?;
        let mut entries = 0usize;
        validate_ordinary_tree(&directory, session_id, 0, &mut entries)?;
        if path_exists_no_follow(&tombstone)? {
            return Err(lifecycle_delete_unsafe(session_id));
        }
        std::fs::rename(&directory, &tombstone).map_err(|_| lifecycle_delete_unsafe(session_id))?;
        require_real_directory_for_session(&tombstone, session_id)?;
        sync_directory(&self.sessions_root).map_err(|_| lifecycle_delete_unsafe(session_id))?;
        sync_directory(&self.tombstone_root).map_err(|_| lifecycle_delete_unsafe(session_id))?;
        drop(session_lock);
        self.complete_tombstone_delete(&tombstone, session_id)
    }

    /// 功能：先 durable 清理归档元数据，再可重入删除固定 tombstone 普通树。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn complete_tombstone_delete(
        &self,
        tombstone: &Path,
        session_id: &str,
    ) -> Result<(), AgentError> {
        require_real_directory_for_session(tombstone, session_id)?;
        let mut validated_entries = 0usize;
        validate_ordinary_tree(tombstone, session_id, 0, &mut validated_entries)?;
        let document = self.read_archive_unlocked()?;
        let filtered = document
            .archived_session_ids
            .iter()
            .filter(|id| id.as_str() != session_id)
            .cloned()
            .collect::<Vec<_>>();
        if filtered.len() != document.archived_session_ids.len() {
            self.write_archive_unlocked(&ArchiveDocument {
                schema_version: ARCHIVE_SCHEMA_VERSION.to_owned(),
                archived_session_ids: filtered,
            })?;
        }
        // 预验证负责拒绝特殊条目；标准库 remover 负责平台级 no-follow 递归清理。
        std::fs::remove_dir_all(tombstone).map_err(|_| lifecycle_delete_unsafe(session_id))?;
        sync_directory(&self.tombstone_root).map_err(|_| lifecycle_delete_unsafe(session_id))
    }

    /// 功能：严格解析并验证归档文档；缺失时返回空 v0.1 文档。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn read_archive_unlocked(&self) -> Result<ArchiveDocument, AgentError> {
        let mut file = match open_regular_read_only(&self.archive_path) {
            Ok(file) => file,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(ArchiveDocument {
                    schema_version: ARCHIVE_SCHEMA_VERSION.to_owned(),
                    archived_session_ids: Vec::new(),
                });
            }
            Err(_) => return Err(lifecycle_store_error("session_lifecycle_store_invalid")),
        };
        let length = file
            .metadata()
            .map_err(|_| lifecycle_store_error("session_lifecycle_store_invalid"))?
            .len();
        if length == 0 || length > MAX_ARCHIVE_BYTES {
            return Err(lifecycle_store_error("session_lifecycle_store_invalid"));
        }
        let mut bytes = Vec::with_capacity(length as usize);
        file.read_to_end(&mut bytes)
            .map_err(|_| lifecycle_store_error("session_lifecycle_store_invalid"))?;
        let text = std::str::from_utf8(&bytes)
            .map_err(|_| lifecycle_store_error("session_lifecycle_store_invalid"))?;
        let value = parse_strict_value(text)
            .map_err(|_| lifecycle_store_error("session_lifecycle_store_invalid"))?;
        let document: ArchiveDocument = serde_json::from_value(value)
            .map_err(|_| lifecycle_store_error("session_lifecycle_store_invalid"))?;
        validate_archive_document(&document)?;
        Ok(document)
    }

    /// 功能：用同目录私有临时文件、fsync 与原子替换发布完整归档文档。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn write_archive_unlocked(&self, document: &ArchiveDocument) -> Result<(), AgentError> {
        validate_archive_document(document)?;
        let bytes = serde_json::to_vec(document)
            .map_err(|_| lifecycle_store_error("session_lifecycle_store_invalid"))?;
        if bytes.len() as u64 > MAX_ARCHIVE_BYTES {
            return Err(lifecycle_store_error("session_lifecycle_store_invalid"));
        }
        let temporary = self.lifecycle_root.join(format!(
            ".session-lifecycle-{}.tmp",
            Uuid::new_v4().simple()
        ));
        let result = (|| {
            let mut file = open_private_create_new(&temporary)?;
            file.write_all(&bytes)?;
            file.sync_all()?;
            drop(file);
            atomic_replace(&temporary, &self.archive_path)?;
            sync_directory(&self.lifecycle_root)?;
            Ok::<(), std::io::Error>(())
        })();
        let _ = std::fs::remove_file(&temporary);
        result.map_err(|_| lifecycle_store_error("session_lifecycle_store_io"))
    }

    /// 功能：非阻塞取得 `session-lifecycle.lock` 的跨进程独占锁。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn acquire_lifecycle_lock(&self) -> Result<LifecycleLock, AgentError> {
        let path = self.lifecycle_root.join("session-lifecycle.lock");
        let file = open_private_read_write_create(&path)
            .map_err(|_| lifecycle_store_error("session_lifecycle_store_io"))?;
        file.try_lock_exclusive().map_err(|_| lifecycle_locked())?;
        Ok(LifecycleLock { _file: file })
    }

    /// 功能：解析目标真实 Session 目录并验证 journal 固定入口存在。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn resolve_session_directory(&self, session_id: &str) -> Result<PathBuf, AgentError> {
        let directory = self.sessions_root.join(session_id);
        match std::fs::symlink_metadata(&directory) {
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Err(lifecycle_not_found(session_id));
            }
            Err(_) => return Err(lifecycle_corrupt(session_id)),
            Ok(_) => {}
        }
        require_real_directory_for_session(&directory, session_id)?;
        match std::fs::symlink_metadata(directory.join("journal.jsonl")) {
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                Err(lifecycle_not_found(session_id))
            }
            Err(_) => Err(lifecycle_corrupt(session_id)),
            Ok(_) => Ok(directory),
        }
    }
}

/// 功能：严格解析服务签发的 v1 offset cursor。
///
/// 输入：可选 cursor；缺失表示 offset 0。
/// 输出：0..16384 的零基偏移。
/// 不变量：只接受 `v1:` 加无符号 ASCII 十进制，长度 4..12；客户端不能注入路径或负值。
/// 失败：格式、长度或范围无效时返回 `invalid_params` 且 field=cursor。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub fn parse_session_cursor(cursor: Option<&str>) -> Result<usize, AgentError> {
    let Some(cursor) = cursor else {
        return Ok(0);
    };
    if !(4..=12).contains(&cursor.len()) || !cursor.starts_with("v1:") {
        return Err(invalid_lifecycle_param("cursor"));
    }
    let digits = &cursor[3..];
    if digits.is_empty() || !digits.bytes().all(|byte| byte.is_ascii_digit()) {
        return Err(invalid_lifecycle_param("cursor"));
    }
    let offset = digits
        .parse::<usize>()
        .map_err(|_| invalid_lifecycle_param("cursor"))?;
    if offset > MAX_DISCOVERED_ENTRIES {
        return Err(invalid_lifecycle_param("cursor"));
    }
    Ok(offset)
}

/// 功能：验证 archive 文档版本、数量、Session ID、唯一性与 ASCII 严格升序。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_archive_document(document: &ArchiveDocument) -> Result<(), AgentError> {
    if document.schema_version != ARCHIVE_SCHEMA_VERSION
        || document.archived_session_ids.len() > MAX_ARCHIVED_SESSIONS
        || document
            .archived_session_ids
            .iter()
            .any(|id| !session_id_is_valid(id))
        || document
            .archived_session_ids
            .windows(2)
            .any(|pair| pair[0].as_bytes() >= pair[1].as_bytes())
    {
        return Err(lifecycle_store_error("session_lifecycle_store_invalid"));
    }
    Ok(())
}

/// 功能：流式验证 portable journal 并提取有界标题、项目和更新时间。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn inspect_journal(
    session_id: &str,
    path: &Path,
    allow_uncommitted_tail: bool,
) -> Result<JournalInspection, AgentError> {
    let file = open_regular_read_only(path).map_err(|_| lifecycle_corrupt(session_id))?;
    let length = file
        .metadata()
        .map_err(|_| lifecycle_corrupt(session_id))?
        .len();
    if length == 0 {
        return Err(lifecycle_corrupt(session_id));
    }
    let mut line_number = 0usize;
    let mut record_ids = HashSet::new();
    let mut title = None;
    let mut saw_user_message = false;
    let mut project = None;
    let mut updated_at = DateTime::<Utc>::from(std::time::UNIX_EPOCH);
    for_each_committed_line(file, session_id, allow_uncommitted_tail, |line| {
        let text = std::str::from_utf8(line).map_err(|_| lifecycle_corrupt(session_id))?;
        let value = parse_strict_value(text).map_err(|_| lifecycle_corrupt(session_id))?;
        if line_number == 0 {
            validate_time_text(&value, "createdAt", session_id)?;
            let header: SessionHeader =
                serde_json::from_value(value).map_err(|_| lifecycle_corrupt(session_id))?;
            if header.kind != "session"
                || header.schema_version != JOURNAL_VERSION
                || header.session_id != session_id
                || header.workspace.is_empty()
                || header.workspace.chars().count() > 32_768
                || header.workspace.contains('\0')
                || header.created_by.as_ref().is_some_and(|created_by| {
                    !matches!(
                        created_by.language.as_str(),
                        "dotnet" | "rust" | "go" | "typescript" | "python"
                    )
                })
            {
                return Err(lifecycle_corrupt(session_id));
            }
            project = Some(project_name(&header.workspace));
            updated_at = header.created_at;
        } else {
            if line_number > MAX_JOURNAL_RECORDS {
                return Err(lifecycle_corrupt(session_id));
            }
            validate_time_text(&value, "time", session_id)?;
            let record: JournalRecord =
                serde_json::from_value(value).map_err(|_| lifecycle_corrupt(session_id))?;
            validate_record(&record, session_id, line_number, &mut record_ids)?;
            updated_at = record.time;
            if !saw_user_message {
                let (is_user, candidate) = extract_user_title(&record, session_id)?;
                saw_user_message = is_user;
                title = candidate;
            }
        }
        line_number = line_number.saturating_add(1);
        Ok(())
    })?;
    let Some(project) = project else {
        return Err(lifecycle_corrupt(session_id));
    };
    Ok(JournalInspection {
        title: title.unwrap_or_else(|| session_id.to_owned()),
        project,
        updated_at,
    })
}

/// 功能：逐块读取 1..4MiB 的完整 LF journal 行，并按调用方策略处理唯一未终止尾部。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn for_each_committed_line(
    file: File,
    session_id: &str,
    allow_uncommitted_tail: bool,
    mut visit: impl FnMut(&[u8]) -> Result<(), AgentError>,
) -> Result<(), AgentError> {
    let mut reader = BufReader::with_capacity(16_384, file);
    let mut line = Vec::with_capacity(16_384);
    loop {
        let available = reader
            .fill_buf()
            .map_err(|_| lifecycle_corrupt(session_id))?;
        if available.is_empty() {
            return if line.is_empty() || allow_uncommitted_tail {
                Ok(())
            } else {
                Err(lifecycle_corrupt(session_id))
            };
        }
        let newline = available.iter().position(|byte| *byte == b'\n');
        let take = newline.unwrap_or(available.len());
        if line.len().saturating_add(take) > MAX_JOURNAL_LINE_BYTES {
            return Err(lifecycle_corrupt(session_id));
        }
        line.extend_from_slice(&available[..take]);
        let consumed = take + usize::from(newline.is_some());
        reader.consume(consumed);
        if newline.is_some() {
            if line.is_empty() {
                return Err(lifecycle_corrupt(session_id));
            }
            visit(&line)?;
            line.clear();
        }
    }
}

/// 功能：验证 journal record envelope、连续 seq、先前 parent 与唯一 record ID。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_record(
    record: &JournalRecord,
    session_id: &str,
    expected_sequence: usize,
    prior_ids: &mut HashSet<String>,
) -> Result<(), AgentError> {
    let expected_sequence =
        u64::try_from(expected_sequence).map_err(|_| lifecycle_corrupt(session_id))?;
    if record.schema_version != RECORD_SCHEMA_VERSION
        || record.session_id != session_id
        || record.seq != expected_sequence
        || !session_id_is_valid(&record.record_id)
        || !RECORD_KINDS.contains(&record.kind.as_str())
        || !record.data.is_object()
        || prior_ids.contains(&record.record_id)
    {
        return Err(lifecycle_corrupt(session_id));
    }
    if expected_sequence == 1 {
        if record.parent_id.is_some() {
            return Err(lifecycle_corrupt(session_id));
        }
    } else if record
        .parent_id
        .as_ref()
        .is_none_or(|parent| !prior_ids.contains(parent))
    {
        return Err(lifecycle_corrupt(session_id));
    }
    prior_ids.insert(record.record_id.clone());
    Ok(())
}

/// 功能：从首条 user message 的 text blocks 生成空白折叠且最多 96 字符的标题。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn extract_user_title(
    record: &JournalRecord,
    session_id: &str,
) -> Result<(bool, Option<String>), AgentError> {
    if record.kind != "message.appended" {
        return Ok((false, None));
    }
    let message = record
        .data
        .get("message")
        .and_then(Value::as_object)
        .ok_or_else(|| lifecycle_corrupt(session_id))?;
    let role = message
        .get("role")
        .and_then(Value::as_str)
        .filter(|role| !role.is_empty() && role.chars().count() <= 32)
        .ok_or_else(|| lifecycle_corrupt(session_id))?;
    let content = message
        .get("content")
        .and_then(Value::as_array)
        .ok_or_else(|| lifecycle_corrupt(session_id))?;
    if role != "user" {
        return Ok((false, None));
    }
    let mut output = String::new();
    let mut pending_space = false;
    for block in content {
        let Some(object) = block.as_object() else {
            continue;
        };
        if object.get("type").and_then(Value::as_str) != Some("text") {
            continue;
        }
        let Some(text) = object.get("text").and_then(Value::as_str) else {
            continue;
        };
        for character in text.chars() {
            if character.is_whitespace() || character.is_control() {
                pending_space = !output.is_empty();
                continue;
            }
            if pending_space && output.chars().count() < 96 {
                output.push(' ');
            }
            pending_space = false;
            if output.chars().count() >= 96 {
                break;
            }
            output.push(character);
        }
        if output.chars().count() >= 96 {
            break;
        }
        pending_space = !output.is_empty();
    }
    Ok((true, (!output.is_empty()).then_some(output)))
}

/// 功能：从 portable workspace 文本提取不访问宿主文件系统的安全 basename。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn project_name(workspace: &str) -> String {
    let trimmed = workspace.trim_end_matches(['/', '\\']);
    let value = trimmed
        .rsplit(['/', '\\'])
        .next()
        .filter(|value| !value.is_empty() && !value.chars().any(char::is_control))
        .unwrap_or("未知项目");
    value.chars().take(128).collect()
}

/// 功能：验证时间字段是长度有界、以 `Z` 结尾的 UTC RFC3339 字符串。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_time_text(value: &Value, field: &str, session_id: &str) -> Result<(), AgentError> {
    let Some(text) = value.get(field).and_then(Value::as_str) else {
        return Err(lifecycle_corrupt(session_id));
    };
    if text.is_empty()
        || text.len() > 64
        || !text.ends_with('Z')
        || DateTime::parse_from_rfc3339(text)
            .ok()
            .is_none_or(|time| time.offset().local_minus_utc() != 0)
    {
        return Err(lifecycle_corrupt(session_id));
    }
    Ok(())
}

/// 功能：构造损坏 Session 的固定安全摘要。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn corrupt_summary(session_id: &str, archived: bool) -> SessionSummary {
    SessionSummary {
        session_id: session_id.to_owned(),
        title: "会话数据不可用".to_owned(),
        project: "未知项目".to_owned(),
        updated_at: DateTime::<Utc>::from(std::time::UNIX_EPOCH),
        archived,
        status: None,
    }
}

/// 功能：在移动前递归证明目标只包含有界普通目录与普通文件。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_ordinary_tree(
    path: &Path,
    session_id: &str,
    depth: usize,
    entries: &mut usize,
) -> Result<(), AgentError> {
    if depth > MAX_DELETE_DEPTH {
        return Err(lifecycle_delete_unsafe(session_id));
    }
    let metadata =
        std::fs::symlink_metadata(path).map_err(|_| lifecycle_delete_unsafe(session_id))?;
    if metadata_is_unsafe(&metadata) {
        return Err(lifecycle_delete_unsafe(session_id));
    }
    if metadata.is_file() {
        return Ok(());
    }
    if !metadata.is_dir() {
        return Err(lifecycle_delete_unsafe(session_id));
    }
    for entry in std::fs::read_dir(path).map_err(|_| lifecycle_delete_unsafe(session_id))? {
        *entries = entries.saturating_add(1);
        if *entries > MAX_DELETE_ENTRIES {
            return Err(lifecycle_delete_unsafe(session_id));
        }
        let entry = entry.map_err(|_| lifecycle_delete_unsafe(session_id))?;
        validate_ordinary_tree(&entry.path(), session_id, depth + 1, entries)?;
    }
    Ok(())
}

/// 功能：取得 Session advisory lock 并拒绝跨进程 live writer。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn acquire_session_mutation_lock(directory: &Path, session_id: &str) -> Result<File, AgentError> {
    let file = open_private_read_write_create(&directory.join("lock"))
        .map_err(|_| lifecycle_busy(session_id))?;
    file.try_lock_exclusive()
        .map_err(|_| lifecycle_busy(session_id))?;
    let metadata = file.metadata().map_err(|_| lifecycle_corrupt(session_id))?;
    if metadata_is_unsafe(&metadata) || !metadata.is_file() {
        return Err(lifecycle_corrupt(session_id));
    }
    Ok(file)
}

/// 功能：advisory lock 已持有时拒绝 portable writer claim 的任何存在形态。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn reject_writer_presence(directory: &Path, session_id: &str) -> Result<(), AgentError> {
    match std::fs::symlink_metadata(directory.join("writer.lock.d")) {
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Ok(_) | Err(_) => Err(lifecycle_busy(session_id)),
    }
}

/// 功能：创建并固定私有真实目录，拒绝 symlink/reparse/device。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn ensure_private_real_directory(path: &Path) -> Result<(), AgentError> {
    std::fs::create_dir_all(path)
        .map_err(|_| lifecycle_store_error("session_lifecycle_store_io"))?;
    if !is_real_directory(path) {
        return Err(lifecycle_store_error("session_lifecycle_store_invalid"));
    }
    #[cfg(unix)]
    std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o700))
        .map_err(|_| lifecycle_store_error("session_lifecycle_store_io"))?;
    Ok(())
}

/// 功能：判断路径当前是未链接、非 reparse/device 的真实目录。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn is_real_directory(path: &Path) -> bool {
    let Ok(metadata) = std::fs::symlink_metadata(path) else {
        return false;
    };
    if !metadata.is_dir() || metadata_is_unsafe(&metadata) {
        return false;
    }
    #[cfg(unix)]
    {
        std::fs::canonicalize(path).is_ok_and(|canonical| canonical == path)
    }
    #[cfg(not(unix))]
    {
        true
    }
}

/// 功能：在 Session 错误上下文中要求真实普通目录。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn require_real_directory_for_session(path: &Path, session_id: &str) -> Result<(), AgentError> {
    if is_real_directory(path) {
        Ok(())
    } else {
        Err(lifecycle_corrupt(session_id))
    }
}

/// 功能：不跟随目标地判断固定路径是否存在。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn path_exists_no_follow(path: &Path) -> Result<bool, AgentError> {
    match std::fs::symlink_metadata(path) {
        Ok(_) => Ok(true),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(false),
        Err(_) => Err(lifecycle_store_error("session_lifecycle_store_io")),
    }
}

/// 功能：判断 metadata 是否表示 symlink、Windows reparse point 或 device。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn metadata_is_unsafe(metadata: &std::fs::Metadata) -> bool {
    if metadata.file_type().is_symlink() {
        return true;
    }
    #[cfg(windows)]
    {
        const FILE_ATTRIBUTE_DEVICE: u32 = 0x40;
        const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;
        metadata.file_attributes() & (FILE_ATTRIBUTE_DEVICE | FILE_ATTRIBUTE_REPARSE_POINT) != 0
    }
    #[cfg(not(windows))]
    {
        false
    }
}

/// 功能：以 no-follow 语义打开已有普通只读文件。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn open_regular_read_only(path: &Path) -> std::io::Result<File> {
    let metadata = std::fs::symlink_metadata(path)?;
    if metadata_is_unsafe(&metadata) || !metadata.is_file() {
        return Err(std::io::Error::other("path is not a regular file"));
    }
    let mut options = OpenOptions::new();
    options.read(true);
    set_no_follow_flags(&mut options);
    let file = options.open(path)?;
    let opened = file.metadata()?;
    if metadata_is_unsafe(&opened) || !opened.is_file() {
        return Err(std::io::Error::other("opened path is not a regular file"));
    }
    Ok(file)
}

/// 功能：以 no-follow 与私有权限打开或创建读写状态文件。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn open_private_read_write_create(path: &Path) -> std::io::Result<File> {
    if let Ok(metadata) = std::fs::symlink_metadata(path)
        && (metadata_is_unsafe(&metadata) || !metadata.is_file())
    {
        return Err(std::io::Error::other("state path is not a regular file"));
    }
    let mut options = OpenOptions::new();
    options.read(true).write(true).create(true).truncate(false);
    set_private_create_flags(&mut options);
    let file = options.open(path)?;
    let metadata = file.metadata()?;
    if metadata_is_unsafe(&metadata) || !metadata.is_file() {
        return Err(std::io::Error::other("opened state path is not regular"));
    }
    Ok(file)
}

/// 功能：以 create-new、no-follow 与私有权限创建原子发布临时文件。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn open_private_create_new(path: &Path) -> std::io::Result<File> {
    let mut options = OpenOptions::new();
    options.write(true).create_new(true);
    set_private_create_flags(&mut options);
    options.open(path)
}

/// 功能：为已有文件打开设置平台 no-follow 标志。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn set_no_follow_flags(options: &mut OpenOptions) {
    #[cfg(unix)]
    options.custom_flags(libc::O_NOFOLLOW | libc::O_CLOEXEC);
    #[cfg(windows)]
    options.custom_flags(windows_sys::Win32::Storage::FileSystem::FILE_FLAG_OPEN_REPARSE_POINT);
}

/// 功能：为新建私有文件设置 0600 与平台 no-follow 标志。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn set_private_create_flags(options: &mut OpenOptions) {
    set_no_follow_flags(options);
    #[cfg(unix)]
    options.mode(0o600);
}

/// 功能：原子替换同目录归档文档，并在 Windows 请求 write-through。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn atomic_replace(source: &Path, destination: &Path) -> std::io::Result<()> {
    #[cfg(windows)]
    {
        atomic_replace_windows(source, destination)
    }
    #[cfg(not(windows))]
    {
        std::fs::rename(source, destination)
    }
}

/// 功能：通过 Windows MoveFileExW 原子替换同目录状态文件。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[cfg(windows)]
#[allow(unsafe_code)]
fn atomic_replace_windows(source: &Path, destination: &Path) -> std::io::Result<()> {
    use windows_sys::Win32::Storage::FileSystem::{
        MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW,
    };
    let source = source
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect::<Vec<_>>();
    let destination = destination
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect::<Vec<_>>();
    // SAFETY: 两个 UTF-16 缓冲均以 NUL 结尾，并在调用返回前保持存活。
    let result = unsafe {
        MoveFileExW(
            source.as_ptr(),
            destination.as_ptr(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
    };
    if result == 0 {
        Err(std::io::Error::last_os_error())
    } else {
        Ok(())
    }
}

/// 功能：尽力同步目录项，保证 rename/remove 的崩溃可见性。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn sync_directory(path: &Path) -> std::io::Result<()> {
    #[cfg(unix)]
    File::open(path)?.sync_all()?;
    #[cfg(not(unix))]
    let _ = path;
    Ok(())
}

/// 功能：验证 Session ID 符合 portable opaque ID 并返回闭合参数错误。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_session_id(session_id: &str) -> Result<(), AgentError> {
    if session_id_is_valid(session_id) {
        Ok(())
    } else {
        Err(invalid_lifecycle_param("sessionId"))
    }
}

/// 功能：判断字符串是否为 1..128 字节的 portable ASCII opaque ID。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn session_id_is_valid(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value.bytes().enumerate().all(|(index, byte)| {
            byte.is_ascii_alphanumeric() || (index > 0 && matches!(byte, b'.' | b'_' | b':' | b'-'))
        })
}

/// 功能：创建 Session 生命周期字段参数错误。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn invalid_lifecycle_param(field: &str) -> AgentError {
    AgentError::new(
        ErrorCode::InvalidParams,
        "session lifecycle parameter is invalid",
    )
    .with_details(serde_json::json!({"kind":"invalid_params","field":field}))
}

/// 功能：创建 Session 不存在错误且只回显已验证公开 ID。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn lifecycle_not_found(session_id: &str) -> AgentError {
    AgentError::new(ErrorCode::InvalidParams, "session was not found")
        .with_details(serde_json::json!({"kind":"session_not_found","resourceId":session_id}))
}

/// 功能：创建 active、pending 或 writer 导致的可重试 busy 错误。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) fn lifecycle_busy(session_id: &str) -> AgentError {
    AgentError::new(ErrorCode::RunConflict, "session is busy")
        .retryable(true)
        .with_details(serde_json::json!({"kind":"session_busy","resourceId":session_id}))
}

/// 功能：创建 journal 或 Session 目录损坏错误。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn lifecycle_corrupt(session_id: &str) -> AgentError {
    AgentError::new(
        ErrorCode::JournalCorrupt,
        "session is corrupt or incompatible",
    )
    .with_details(serde_json::json!({"kind":"session_corrupt","resourceId":session_id}))
}

/// 功能：创建归档状态锁冲突错误。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn lifecycle_locked() -> AgentError {
    AgentError::new(
        ErrorCode::SessionLocked,
        "session lifecycle state is locked",
    )
    .retryable(true)
    .with_details(serde_json::json!({"kind":"session_lifecycle_locked"}))
}

/// 功能：创建不含宿主路径的生命周期 store 错误。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn lifecycle_store_error(kind: &str) -> AgentError {
    AgentError::new(
        ErrorCode::InternalError,
        "session lifecycle storage is unavailable",
    )
    .with_details(serde_json::json!({"kind":kind}))
}

/// 功能：创建 tombstone 删除无法证明安全的固定错误。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn lifecycle_delete_unsafe(session_id: &str) -> AgentError {
    AgentError::new(
        ErrorCode::InternalError,
        "session deletion could not be completed safely",
    )
    .with_details(serde_json::json!({"kind":"session_delete_unsafe","resourceId":session_id}))
}

#[cfg(test)]
mod tests {
    use std::fs::OpenOptions;
    use std::io::Write as _;
    use std::path::Path;

    use serde_json::json;
    use tempfile::TempDir;

    use super::{SessionLifecycleService, parse_session_cursor};
    use crate::session::SessionStore;

    /// 功能：创建独立 workspace/state 和 lifecycle service 测试夹具。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn fixture()
    -> Result<(TempDir, TempDir, SessionStore, SessionLifecycleService), crate::error::AgentError>
    {
        let workspace = tempfile::tempdir()?;
        let state = tempfile::tempdir()?;
        let store = SessionStore::new(state.path(), 1_048_576).await?;
        let lifecycle = SessionLifecycleService::new(store.clone())?;
        Ok((workspace, state, store, lifecycle))
    }

    /// 功能：追加 portable user 消息以生成真实列表摘要。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn create_session(
        store: &SessionStore,
        workspace: &Path,
        session_id: &str,
        text: &str,
    ) -> Result<(), crate::error::AgentError> {
        store.create_session(session_id, workspace).await?;
        let _lease = store.acquire_writer(session_id).await?;
        store
            .append_record(
                session_id,
                "message.appended",
                json!({
                    "message":{
                        "messageId":format!("message-{session_id}"),
                        "role":"user",
                        "content":[{"type":"text","text":text}],
                        "time":"2026-07-21T00:00:00Z"
                    }
                }),
            )
            .await?;
        Ok(())
    }

    /// 功能：验证列表摘要折叠标题、隔离 stateRoot 同级目录并使用稳定排序。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn lists_only_real_sessions_with_safe_summary() -> Result<(), crate::error::AgentError> {
        let (workspace, state, store, lifecycle) = fixture().await?;
        create_session(
            &store,
            workspace.path(),
            "session-b",
            "  first\n\tmessage  ",
        )
        .await?;
        create_session(&store, workspace.path(), "session-a", "second").await?;
        std::fs::create_dir_all(state.path().join("credentials/session-hidden"))?;
        let sessions = lifecycle.list().await?;
        assert_eq!(sessions.len(), 2);
        assert_eq!(sessions[0].session_id, "session-a");
        assert_eq!(sessions[1].session_id, "session-b");
        assert_eq!(sessions[1].title, "first message");
        assert_eq!(
            sessions[0].project,
            workspace
                .path()
                .file_name()
                .and_then(|v| v.to_str())
                .unwrap_or("unknown")
        );
        Ok(())
    }

    /// 功能：验证归档和恢复跨 service 实例 durable 且不改写 journal。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn archive_restore_is_durable_and_journal_unchanged()
    -> Result<(), crate::error::AgentError> {
        let (workspace, _state, store, lifecycle) = fixture().await?;
        create_session(&store, workspace.path(), "session-archive", "archive me").await?;
        let journal = store
            .state_root()
            .join("sessions/session-archive/journal.jsonl");
        let before = std::fs::read(&journal)?;
        assert!(lifecycle.archive("session-archive").await?.archived);
        let reopened = SessionLifecycleService::new(store.clone())?;
        assert!(reopened.list().await?[0].archived);
        assert!(!reopened.restore("session-archive").await?.archived);
        assert_eq!(std::fs::read(journal)?, before);
        Ok(())
    }

    /// 功能：验证 live writer 和损坏 journal 均拒绝 lifecycle mutation，列表仅显示固定占位。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn mutations_reject_busy_and_corrupt_sessions() -> Result<(), crate::error::AgentError> {
        let (workspace, _state, store, lifecycle) = fixture().await?;
        create_session(&store, workspace.path(), "session-gated", "gate").await?;
        let lease = store.acquire_writer("session-gated").await?;
        let busy = lifecycle
            .archive("session-gated")
            .await
            .expect_err("writer must be busy");
        assert_eq!(busy.details["kind"], "session_busy");
        drop(lease);
        let path = store
            .state_root()
            .join("sessions/session-gated/journal.jsonl");
        let mut file = OpenOptions::new().append(true).open(path)?;
        file.write_all(b"{not-json}\n")?;
        file.sync_all()?;
        let summaries = lifecycle.list().await?;
        assert_eq!(summaries[0].title, "会话数据不可用");
        let corrupt = lifecycle
            .delete("session-gated")
            .await
            .expect_err("corrupt journal must fail");
        assert_eq!(corrupt.details["kind"], "session_corrupt");
        Ok(())
    }

    /// 功能：验证列表忽略唯一未提交尾部，而任何 lifecycle mutation 都把它视为损坏。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn list_tolerates_uncommitted_tail_but_mutation_rejects_it()
    -> Result<(), crate::error::AgentError> {
        let (workspace, _state, store, lifecycle) = fixture().await?;
        create_session(&store, workspace.path(), "session-tail", "committed title").await?;
        let path = store
            .state_root()
            .join("sessions/session-tail/journal.jsonl");
        let mut file = OpenOptions::new().append(true).open(path)?;
        file.write_all(b"{\"schemaVersion\":\"0.1\"")?;
        file.sync_all()?;
        let summaries = lifecycle.list().await?;
        assert_eq!(summaries[0].title, "committed title");
        let error = lifecycle
            .archive("session-tail")
            .await
            .expect_err("uncommitted tail must reject mutation");
        assert_eq!(error.details["kind"], "session_corrupt");
        Ok(())
    }

    /// 功能：验证固定 tombstone 可续删且永久删除清理归档元数据。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn delete_completes_fixed_tombstone_and_archive_cleanup()
    -> Result<(), crate::error::AgentError> {
        let (workspace, _state, store, lifecycle) = fixture().await?;
        create_session(&store, workspace.path(), "session-delete", "delete me").await?;
        lifecycle.archive("session-delete").await?;
        let source = store.state_root().join("sessions/session-delete");
        let tombstone = store
            .state_root()
            .join("sessions/.session-tombstones/session-delete");
        std::fs::rename(source, &tombstone)?;
        lifecycle.delete("session-delete").await?;
        assert!(!tombstone.exists());
        assert!(lifecycle.list().await?.is_empty());
        Ok(())
    }

    /// 功能：验证 symlink 使永久删除失败且原 Session 目录保持存在。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(unix)]
    #[tokio::test]
    async fn delete_rejects_symlink_without_following() -> Result<(), crate::error::AgentError> {
        use std::os::unix::fs::symlink;
        let (workspace, _state, store, lifecycle) = fixture().await?;
        create_session(&store, workspace.path(), "session-link", "keep").await?;
        let outside = tempfile::tempdir()?;
        std::fs::write(outside.path().join("keep.txt"), b"keep")?;
        let session = store.state_root().join("sessions/session-link");
        symlink(outside.path(), session.join("escape"))?;
        let error = lifecycle
            .delete("session-link")
            .await
            .expect_err("link must fail");
        assert_eq!(error.details["kind"], "session_delete_unsafe");
        assert!(session.exists());
        assert!(outside.path().join("keep.txt").exists());
        Ok(())
    }

    /// 功能：验证 cursor 闭合语法、终页 null 与完整帧上限。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn pagination_contract_is_strict_and_bounded() -> Result<(), crate::error::AgentError> {
        assert_eq!(parse_session_cursor(None)?, 0);
        assert_eq!(parse_session_cursor(Some("v1:128"))?, 128);
        assert!(parse_session_cursor(Some("v2:0")).is_err());
        assert!(parse_session_cursor(Some("v1:-1")).is_err());
        let page = SessionLifecycleService::create_page(&[], 0, 64, &json!("list"))?;
        assert!(page.sessions.is_empty());
        assert_eq!(page.next_cursor, None);
        assert!(!page.has_more);
        let frame =
            crate::protocol::JsonRpcResponse::success(json!("list"), serde_json::to_value(page)?);
        assert!(serde_json::to_vec(&frame)?.len() <= 1_048_576);
        Ok(())
    }
}
