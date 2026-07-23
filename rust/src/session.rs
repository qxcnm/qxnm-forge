use std::collections::{BTreeMap, BTreeSet};
use std::fs::{File, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::time::Duration;

use chrono::{DateTime, Utc};
use fs2::FileExt;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};
use tokio::task;
use tokio_util::sync::CancellationToken;
use uuid::Uuid;

use crate::domain::{ArtifactRef, ProviderImage, validate_image_signature};
use crate::error::{AgentError, ErrorCode};
use crate::protocol::parse_strict_value;

const COMPUTER_EXTENSION_NAMESPACE: &str = "org.agentprotocol.computer";
const MAX_COMPUTER_PNG_BYTES: usize = 33_554_432;

pub mod legacy_session_v3_import;
mod writer_lease;

use writer_lease::{PortableWriterLease, create_session_directory};

pub const JOURNAL_VERSION: &str = "0.1";
pub const RECORD_SCHEMA_VERSION: &str = "0.1";
const MAX_SAFE_INTEGER: u64 = 9_007_199_254_740_991;
const SESSION_RECOVERY_NAMESPACE: &str = "org.agent-session.recovery";

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

/// Session journal 首行的语言中立 header；它不参与 record `seq`。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SessionHeader {
    pub kind: String,
    pub schema_version: String,
    pub session_id: String,
    pub created_at: DateTime<Utc>,
    pub workspace: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub created_by: Option<CreatedBy>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub provenance: Option<SessionProvenance>,
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub extensions: BTreeMap<String, Value>,
}

/// Session 来源与一次性转换证据；字段保持品牌中立且与公共 journal Schema 一致。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SessionProvenance {
    pub source: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub source_session_id: Option<String>,
    pub source_sha256: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reference_commit: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub report_artifact_id: Option<String>,
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub extensions: BTreeMap<String, Value>,
}

/// 创建 Session 的原生实现标识。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CreatedBy {
    pub name: String,
    pub version: String,
    pub language: String,
}

/// 语言中立的 append-only journal 记录，与公共 JSON Schema 一致。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct JournalRecord {
    pub schema_version: String,
    pub kind: String,
    pub record_id: String,
    pub session_id: String,
    pub seq: u64,
    pub parent_id: Option<String>,
    pub time: DateTime<Utc>,
    pub data: Value,
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub extensions: BTreeMap<String, Value>,
}

impl JournalRecord {
    /// 功能：创建一条符合 Session v0.1 Schema 的不可变 journal 记录。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// 输入包含 session、连续正序号、可选父记录、规范 kind 与对象 data；ID 和时间自动生成。
    #[must_use]
    pub fn new(
        session_id: impl Into<String>,
        seq: u64,
        parent_id: Option<String>,
        kind: impl Into<String>,
        data: Value,
    ) -> Self {
        Self {
            schema_version: RECORD_SCHEMA_VERSION.to_owned(),
            kind: kind.into(),
            record_id: format!("record-{}", Uuid::new_v4()),
            session_id: session_id.into(),
            seq,
            parent_id,
            time: Utc::now(),
            data,
            extensions: BTreeMap::new(),
        }
    }
}

#[derive(Debug, Clone)]
pub struct SessionStore {
    root: PathBuf,
    inline_output_limit: usize,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RecoverySummary {
    pub interrupted_runs: usize,
    pub ambiguous_tools: usize,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct RecoveryToolIntent {
    run_id: String,
    turn_id: String,
    tool_call_id: String,
    name: String,
    status: String,
}

/// `session/get` 返回的完整 portable 消息快照与 durable 事件增量。
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionSnapshot {
    pub session_id: String,
    pub latest_seq: u64,
    pub active_run_id: Option<String>,
    pub messages: Vec<Value>,
    pub events: Vec<Value>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub selected_head_record_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub compaction_record_id: Option<String>,
}

/// branch selection durable 后返回的三项 opaque record 身份。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BranchSelectionResult {
    pub target_leaf_record_id: String,
    pub selection_record_id: String,
    pub selected_head_record_id: String,
}

/// context compaction 两条记录 durable 后返回的 portable 身份。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ContextCompactionResult {
    pub summary_message_id: String,
    pub summary_record_id: String,
    pub compaction_record_id: String,
    pub selected_head_record_id: String,
}

/// SessionStore 执行 compaction 所需的已解码 portable 参数。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Clone)]
pub struct ContextCompactionInput {
    pub expected_head_record_id: String,
    pub first_retained_record_id: String,
    pub summary_text: String,
    pub provider: Value,
    pub usage: Value,
    pub tokens_before: u64,
    pub tokens_after: u64,
    pub strategy: String,
}

#[derive(Debug)]
pub struct SessionLease {
    session_id: String,
    file: File,
    portable: PortableWriterLease,
    _coordination: SessionCoordinationLease,
}

/// 固定 sessions 根下、不会随 Session 目录移动的跨进程 per-ID lease。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug)]
pub(crate) struct SessionCoordinationLease {
    _portable: PortableWriterLease,
}

impl SessionLease {
    /// 功能：返回当前跨进程 writer lease 绑定的 Session ID。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn session_id(&self) -> &str {
        &self.session_id
    }
}

impl Drop for SessionLease {
    /// 功能：在 lease 生命周期结束时释放 OS 级 Session writer lock。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn drop(&mut self) {
        let _ = release_writer_locks(&mut self.portable, &self.file);
    }
}

impl SessionStore {
    /// 功能：初始化 Session 状态根和 sessions 目录。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// 返回前目录已创建；默认不读取、执行或迁移任何现有 journal。
    pub async fn new(
        root: impl AsRef<Path>,
        inline_output_limit: usize,
    ) -> Result<Self, AgentError> {
        let root = root.as_ref().to_path_buf();
        tokio::fs::create_dir_all(root.join("sessions")).await?;
        let root = tokio::fs::canonicalize(root).await?;
        let sessions = tokio::fs::canonicalize(root.join("sessions")).await?;
        if sessions != root.join("sessions") {
            return Err(AgentError::new(
                ErrorCode::InternalError,
                "session storage root is invalid",
            )
            .with_details(serde_json::json!({"kind":"session_store_invalid"})));
        }
        Ok(Self {
            root,
            inline_output_limit,
        })
    }

    /// 功能：返回 canonical 应用状态根，供同 crate 的生命周期服务派生固定状态子目录。
    ///
    /// 输入：当前已初始化的 Session store。
    /// 输出：只读 canonical 状态根引用。
    /// 不变量：Session 始终只位于返回路径的 `sessions` 子目录；调用方不得把该路径暴露给协议客户端。
    /// 失败：本方法不执行 I/O 且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub(crate) fn state_root(&self) -> &Path {
        &self.root
    }

    /// 功能：为永久删除取得不会随 Session rename 移动的跨进程 coordination lease。
    ///
    /// 输入：符合 portable opaque ID 语法的 Session ID。
    /// 输出：释放前阻止当前 Rust/.NET writer 打开、创建或移动同 ID Session 的 lease。
    /// 不变量：lease 固定在 `sessions/.session-coordination/<sessionId>`，不读取 journal 或凭据。
    /// 失败：ID、coordination 路径或 live owner 无法安全确认时返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn acquire_delete_coordination(
        &self,
        session_id: &str,
    ) -> Result<SessionCoordinationLease, AgentError> {
        validate_id(session_id, "session")?;
        acquire_session_coordination(&self.root.join("sessions"), session_id)
    }

    /// 功能：返回工具输出进入 artifact 前允许内联的字节数。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub const fn inline_output_limit(&self) -> usize {
        self.inline_output_limit
    }

    /// 功能：非阻塞获取整个 run 生命周期持有的跨进程 Session writer lease。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// 同一 Session 已被另一进程/daemon 使用时立即返回可重试冲突，不静默排队。
    pub async fn acquire_writer(&self, session_id: &str) -> Result<SessionLease, AgentError> {
        validate_id(session_id, "session")?;
        let directory = self.session_dir(session_id)?;
        let sessions_root = self.root.join("sessions");
        let session_id = session_id.to_owned();
        task::spawn_blocking(move || {
            let coordination = acquire_session_coordination(&sessions_root, &session_id)?;
            reject_pending_tombstone(&sessions_root, &session_id)?;
            let mut portable = PortableWriterLease::acquire(&directory, &session_id)?;
            let file = open_writer_lock(&directory)?;
            if let Err(error) = file.try_lock_exclusive() {
                let _ = portable.release_without_advisory();
                return Err(writer_conflict_error(&error));
            }
            if let Err(error) = recover_tail_with_held_writer(&directory, &session_id) {
                let _ = release_writer_locks(&mut portable, &file);
                return Err(error);
            }
            Ok(SessionLease {
                session_id,
                file,
                portable,
                _coordination: coordination,
            })
        })
        .await
        .map_err(|error| AgentError::new(ErrorCode::InternalError, error.to_string()))?
    }

    /// 功能：幂等创建符合公共 Schema 的 Session header、lock 和 artifacts 目录。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// 新 header 在成功返回前 `sync_all`；已存在 Session 会验证 ID、版本与 header 类型。
    pub async fn create_session(
        &self,
        session_id: &str,
        workspace: &Path,
    ) -> Result<(), AgentError> {
        validate_id(session_id, "session")?;
        let directory = self.session_dir(session_id)?;
        let sessions_root = self.root.join("sessions");
        let session_id = session_id.to_owned();
        let workspace = workspace.to_string_lossy().into_owned();
        task::spawn_blocking(move || {
            let _coordination = acquire_session_coordination(&sessions_root, &session_id)?;
            reject_pending_tombstone(&sessions_root, &session_id)?;
            create_session_directory(&directory)?;
            let mut portable = PortableWriterLease::acquire(&directory, &session_id)?;
            let lock = open_writer_lock(&directory)?;
            if let Err(error) = lock.try_lock_exclusive() {
                let _ = portable.release_without_advisory();
                return Err(writer_conflict_error(&error));
            }
            let result = (|| {
                std::fs::create_dir_all(directory.join("artifacts"))?;
                let path = directory.join("journal.jsonl");
                if path.exists() {
                    recover_tail_with_held_writer(&directory, &session_id)?;
                    let journal = read_journal(&path)?;
                    if journal.header.session_id != session_id {
                        return Err(AgentError::new(
                            ErrorCode::JournalCorrupt,
                            "session header id does not match directory",
                        ));
                    }
                    return Ok(());
                }
                let header = SessionHeader {
                    kind: "session".to_owned(),
                    schema_version: JOURNAL_VERSION.to_owned(),
                    session_id,
                    created_at: Utc::now(),
                    workspace,
                    created_by: Some(CreatedBy {
                        name: "qxnm-forge-rust".to_owned(),
                        version: env!("CARGO_PKG_VERSION").to_owned(),
                        language: "rust".to_owned(),
                    }),
                    provenance: None,
                    extensions: BTreeMap::new(),
                };
                let mut file = OpenOptions::new().create_new(true).write(true).open(path)?;
                write_line(&mut file, &header)?;
                file.sync_all()?;
                sync_directory(&directory)?;
                Ok(())
            })();
            let release = release_writer_locks(&mut portable, &lock);
            result.and(release)
        })
        .await
        .map_err(|error| AgentError::new(ErrorCode::InternalError, error.to_string()))?
    }

    /// 功能：在跨进程锁内自动分配 `seq` 与当前 leaf `parentId` 并持久化记录。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// 成功返回意味着完整 JSON 行已追加且执行 `sync_data`；并发写者不会复用序号。
    pub async fn append_record(
        &self,
        session_id: &str,
        kind: &str,
        data: Value,
    ) -> Result<JournalRecord, AgentError> {
        validate_id(session_id, "session")?;
        validate_record_kind(kind)?;
        if !data.is_object() {
            return Err(AgentError::new(
                ErrorCode::InvalidParams,
                "journal record data must be an object",
            ));
        }
        let directory = self.session_dir(session_id)?;
        let session_id = session_id.to_owned();
        let kind = kind.to_owned();
        task::spawn_blocking(move || {
            let lock = open_append_lock(&directory)?;
            lock.lock_exclusive()?;
            let result = (|| {
                let path = directory.join("journal.jsonl");
                let journal = read_journal(&path)?;
                let seq = next_record_sequence(&journal.records)?;
                let parent_id = journal
                    .records
                    .last()
                    .map(|record| record.record_id.clone());
                let record = JournalRecord::new(session_id, seq, parent_id, kind, data);
                let mut candidate = journal.records;
                candidate.push(record.clone());
                validate_journal_semantics(&candidate)?;
                append_unlocked(&path, &record)?;
                Ok(record)
            })();
            let unlock = FileExt::unlock(&lock);
            result.and_then(|record| unlock.map(|()| record).map_err(AgentError::from))
        })
        .await
        .map_err(|error| AgentError::new(ErrorCode::InternalError, error.to_string()))?
    }

    /// 功能：追加调用方构造的记录，并严格验证序号、父记录与 Session 归属。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// 此入口用于导入/互操作测试；常规运行应使用 `append_record` 自动分配顺序。
    pub async fn append(&self, record: JournalRecord) -> Result<(), AgentError> {
        validate_id(&record.session_id, "session")?;
        validate_record_kind(&record.kind)?;
        let directory = self.session_dir(&record.session_id)?;
        task::spawn_blocking(move || {
            let lock = open_append_lock(&directory)?;
            lock.lock_exclusive()?;
            let result = (|| {
                let path = directory.join("journal.jsonl");
                let journal = read_journal(&path)?;
                validate_next_record(&journal, &record)?;
                let mut candidate = journal.records;
                candidate.push(record.clone());
                validate_journal_semantics(&candidate)?;
                append_unlocked(&path, &record)
            })();
            let unlock = FileExt::unlock(&lock);
            result.and_then(|()| unlock.map_err(AgentError::from))
        })
        .await
        .map_err(|error| AgentError::new(ErrorCode::InternalError, error.to_string()))?
    }

    /// 功能：在既有 writer lease 下以 expected-head CAS 选择一个 earlier quiescent branch。
    ///
    /// 输入：与 Session 绑定的 lease、调用方观察的 head 和待选 earlier record。
    /// 输出：目标、selection record 与新 selected head 的 opaque ID。
    /// 不变量：比较、target ancestry 静止校验、追加与 flush 位于同一 append lock；失败零追加。
    /// 失败：stale head、未知 target、非静止 target、journal 损坏或 I/O 失败返回冻结结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn select_branch(
        &self,
        lease: &SessionLease,
        expected_head_record_id: &str,
        target_leaf_record_id: &str,
    ) -> Result<BranchSelectionResult, AgentError> {
        validate_id(expected_head_record_id, "expected head record")?;
        validate_id(target_leaf_record_id, "target record")?;
        let session_id = lease.session_id().to_owned();
        let directory = self.session_dir(&session_id)?;
        let expected_head_record_id = expected_head_record_id.to_owned();
        let target_leaf_record_id = target_leaf_record_id.to_owned();
        task::spawn_blocking(move || {
            let lock = open_append_lock(&directory)?;
            lock.lock_exclusive()?;
            let result = (|| {
                let path = directory.join("journal.jsonl");
                let journal = read_journal(&path)?;
                let topology = validate_journal_semantics(&journal.records)?;
                if topology.selected_head_record_id.as_deref()
                    != Some(expected_head_record_id.as_str())
                {
                    return Err(stale_session_head_error());
                }
                if !topology.record_indexes.contains_key(&target_leaf_record_id) {
                    return Err(record_not_found_error("targetLeafRecordId"));
                }
                if !chain_is_quiescent(
                    &journal.records,
                    &topology.record_indexes,
                    &target_leaf_record_id,
                )? {
                    return Err(branch_not_quiescent_error());
                }
                let seq = next_record_sequence(&journal.records)?;
                let selection = JournalRecord::new(
                    session_id,
                    seq,
                    Some(target_leaf_record_id.clone()),
                    "branch.selected",
                    serde_json::json!({"leafRecordId":target_leaf_record_id}),
                );
                let mut candidate = journal.records;
                candidate.push(selection.clone());
                validate_journal_semantics(&candidate)?;
                append_unlocked(&path, &selection)?;
                Ok(BranchSelectionResult {
                    target_leaf_record_id,
                    selection_record_id: selection.record_id.clone(),
                    selected_head_record_id: selection.record_id,
                })
            })();
            let unlock = FileExt::unlock(&lock);
            result.and_then(|value| unlock.map(|()| value).map_err(AgentError::from))
        })
        .await
        .map_err(|error| AgentError::new(ErrorCode::InternalError, error.to_string()))?
    }

    /// 功能：在既有 writer lease 下以 expected-head CAS durable 写入 summary + compaction 两记录。
    ///
    /// 输入：当前 Session lease，以及已经完成 wire 解码的 retained、summary、Provider、usage 与 token 参数。
    /// 输出：summary message/record、compaction record 和新 selected head 的 opaque ID。
    /// 不变量：完整校验和 CAS 先于首条写；summary 先独立 flush，compaction 再 flush 后才成功。
    /// 失败：stale/busy/boundary/token/字段、journal 或 I/O 错误返回冻结结构化错误；首条后崩溃仍是合法普通消息。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn compact_context(
        &self,
        lease: &SessionLease,
        input: ContextCompactionInput,
    ) -> Result<ContextCompactionResult, AgentError> {
        validate_compaction_input(&input)?;
        let session_id = lease.session_id().to_owned();
        let directory = self.session_dir(&session_id)?;
        task::spawn_blocking(move || {
            let lock = open_append_lock(&directory)?;
            lock.lock_exclusive()?;
            let result = (|| {
                let path = directory.join("journal.jsonl");
                let journal = read_journal(&path)?;
                let topology = validate_journal_semantics(&journal.records)?;
                if topology.selected_head_record_id.as_deref()
                    != Some(input.expected_head_record_id.as_str())
                {
                    return Err(stale_session_head_error());
                }
                let source_leaf_record_id = topology
                    .selected_head_record_id
                    .clone()
                    .ok_or_else(stale_session_head_error)?;
                if !chain_is_quiescent(
                    &journal.records,
                    &topology.record_indexes,
                    &source_leaf_record_id,
                )? {
                    return Err(session_busy_error());
                }
                validate_compaction_boundary(
                    &journal.records,
                    &topology.record_indexes,
                    &source_leaf_record_id,
                    &input.first_retained_record_id,
                )?;
                let summary_message_id = format!("message-{}", Uuid::new_v4());
                let summary_seq = next_record_sequence(&journal.records)?;
                let summary = JournalRecord::new(
                    session_id.clone(),
                    summary_seq,
                    Some(source_leaf_record_id.clone()),
                    "message.appended",
                    serde_json::json!({
                        "message":{
                            "messageId":summary_message_id,
                            "role":"assistant",
                            "content":[{"type":"text","text":input.summary_text}],
                            "provider":input.provider,
                            "finishReason":"stop",
                            "usage":input.usage,
                            "time":Utc::now()
                        }
                    }),
                );
                let compaction_seq = summary_seq.checked_add(1).ok_or_else(|| {
                    AgentError::new(
                        ErrorCode::OutputLimitExceeded,
                        "journal sequence exceeds the safe integer maximum",
                    )
                })?;
                if compaction_seq > MAX_SAFE_INTEGER {
                    return Err(AgentError::new(
                        ErrorCode::OutputLimitExceeded,
                        "journal sequence exceeds the safe integer maximum",
                    ));
                }
                let compaction = JournalRecord::new(
                    session_id,
                    compaction_seq,
                    Some(summary.record_id.clone()),
                    "context.compacted",
                    serde_json::json!({
                        "sourceLeafRecordId":source_leaf_record_id,
                        "summaryMessageId":summary_message_id,
                        "firstRetainedRecordId":input.first_retained_record_id,
                        "tokensBefore":input.tokens_before,
                        "tokensAfter":input.tokens_after,
                        "strategy":input.strategy
                    }),
                );
                let mut candidate = journal.records;
                candidate.push(summary.clone());
                candidate.push(compaction.clone());
                validate_journal_semantics(&candidate)?;
                append_unlocked(&path, &summary)?;
                append_unlocked(&path, &compaction)?;
                Ok(ContextCompactionResult {
                    summary_message_id,
                    summary_record_id: summary.record_id,
                    compaction_record_id: compaction.record_id.clone(),
                    selected_head_record_id: compaction.record_id,
                })
            })();
            let unlock = FileExt::unlock(&lock);
            result.and_then(|value| unlock.map(|()| value).map_err(AgentError::from))
        })
        .await
        .map_err(|error| AgentError::new(ErrorCode::InternalError, error.to_string()))?
    }

    /// 功能：读取并验证 Session header 之后的全部 portable records。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// 验证连续序号、唯一 ID、向后 parent、版本、Session ID 与已知 core kind。
    pub async fn load(&self, session_id: &str) -> Result<Vec<JournalRecord>, AgentError> {
        validate_id(session_id, "session")?;
        let directory = self.session_dir(session_id)?;
        let session_id = session_id.to_owned();
        task::spawn_blocking(move || {
            let journal = read_journal_shared(&directory, &session_id)?;
            if journal.header.session_id != session_id {
                return Err(AgentError::new(
                    ErrorCode::JournalCorrupt,
                    "session header id does not match requested id",
                ));
            }
            Ok(journal.records)
        })
        .await
        .map_err(|error| AgentError::new(ErrorCode::InternalError, error.to_string()))?
    }

    /// 功能：读取并验证指定 Session 的 header。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn header(&self, session_id: &str) -> Result<SessionHeader, AgentError> {
        validate_id(session_id, "session")?;
        let directory = self.session_dir(session_id)?;
        let session_id = session_id.to_owned();
        task::spawn_blocking(move || {
            read_journal_shared(&directory, &session_id).map(|journal| journal.header)
        })
        .await
        .map_err(|error| AgentError::new(ErrorCode::InternalError, error.to_string()))?
    }

    /// 功能：投影 selected branch/最新 compaction 消息和按 event.seq 增量过滤的全局 durable 事件。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// `latestSeq` 始终取全部 durable `event.emitted` 的最大事件序号；`afterSeq` 只过滤
    /// 返回事件。消息只来自 selected parent chain；事件保持 Session 全局且不因 branch 隐藏或重排。
    pub async fn snapshot(
        &self,
        session_id: &str,
        after_seq: u64,
        active_run_id: Option<String>,
    ) -> Result<SessionSnapshot, AgentError> {
        if after_seq > MAX_SAFE_INTEGER {
            return Err(AgentError::new(
                ErrorCode::InvalidParams,
                "afterSeq exceeds the safe integer maximum",
            )
            .with_details(serde_json::json!({"kind":"invalid_params","field":"afterSeq"})));
        }
        let records = self.load(session_id).await?;
        let projection = project_selected_messages(&records)?;
        let mut latest_seq = 0;
        let mut events = Vec::new();
        for record in &records {
            if record.kind != "event.emitted" {
                continue;
            }
            let event = record.data.get("event").ok_or_else(|| {
                AgentError::new(
                    ErrorCode::JournalCorrupt,
                    "event.emitted is missing data.event",
                )
            })?;
            let sequence = event.get("seq").and_then(Value::as_u64).ok_or_else(|| {
                AgentError::new(
                    ErrorCode::JournalCorrupt,
                    "event.emitted event.seq must be a positive safe integer",
                )
            })?;
            if sequence == 0
                || sequence > MAX_SAFE_INTEGER
                || sequence <= latest_seq
                || event.get("sessionId").and_then(Value::as_str) != Some(session_id)
            {
                return Err(AgentError::new(
                    ErrorCode::JournalCorrupt,
                    "durable event sequence or session identity is invalid",
                ));
            }
            latest_seq = sequence;
            if sequence > after_seq {
                events.push(event.clone());
            }
        }
        Ok(SessionSnapshot {
            session_id: session_id.to_owned(),
            latest_seq,
            active_run_id,
            messages: projection.messages,
            events,
            selected_head_record_id: projection.selected_head_record_id,
            compaction_record_id: projection.compaction_record_id,
        })
    }

    /// 功能：返回下一次 Provider 调用所需的 selected branch + 最新 compaction portable 历史。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// 返回值保持规范投影顺序和原 JSON 字段；无效引用明确 journal_corrupt，绝不回退全历史。
    pub async fn context_messages(&self, session_id: &str) -> Result<Vec<Value>, AgentError> {
        self.snapshot(session_id, MAX_SAFE_INTEGER, None)
            .await
            .map(|snapshot| snapshot.messages)
    }

    /// 功能：备份并截断任何无独立 durability witness 的非空未换行尾部，然后追加恢复 extension 记录。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// 完整 LF 行或非尾部损坏绝不跳过；恢复副本在原文件旁保留并先于截断同步落盘。
    pub async fn recover_corrupt_tail(&self, session_id: &str) -> Result<bool, AgentError> {
        validate_id(session_id, "session")?;
        let directory = self.session_dir(session_id)?;
        let session_id = session_id.to_owned();
        task::spawn_blocking(move || recover_tail_locked(&directory, &session_id))
            .await
            .map_err(|error| AgentError::new(ErrorCode::InternalError, error.to_string()))?
    }

    /// 功能：恢复无终态 run 与无结果工具意图，并保证不会自动重放 Provider 或工具。
    ///
    /// 输入：已经通过结构校验并取得 writer lease 的 Session ID。
    /// 输出：恢复的 interrupted run 数和真正 ambiguous 工具数；所有缺失结果/消息/终态已按序 durable。
    /// 不变量：started 或 durable `tool.started` 才按未知结果处理；denied/rejected/pending 等确定未执行路径写 known 结果；任何既有 result 缺消息都会精确补齐且不重放工具。
    /// 失败：记录关联、审批决定、结果形状、事件序号或 journal I/O 无效时返回 `JournalCorrupt`/结构化错误，不跳过损坏。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn recover_interrupted(
        &self,
        session_id: &str,
    ) -> Result<RecoverySummary, AgentError> {
        let records = self.load(session_id).await?;
        let mut intents = BTreeMap::<String, RecoveryToolIntent>::new();
        let mut intent_order = Vec::new();
        let mut resolved_tools = BTreeSet::new();
        let mut started_tools = BTreeSet::new();
        let mut approval_by_tool = BTreeMap::<String, (String, String)>::new();
        let mut resolved_approvals = BTreeMap::<String, String>::new();
        for record in &records {
            if record.kind == "tool.intent" {
                let run_id = required_record_string(record, "runId")?;
                let turn_id = required_record_string(record, "turnId")?;
                let tool_call_id = required_record_string(record, "toolCallId")?;
                let name = required_record_string(record, "name")?;
                let status = required_record_string(record, "status")?;
                let intent = RecoveryToolIntent {
                    run_id: run_id.to_owned(),
                    turn_id: turn_id.to_owned(),
                    tool_call_id: tool_call_id.to_owned(),
                    name: name.to_owned(),
                    status: status.to_owned(),
                };
                if let Some(existing) = intents.get(tool_call_id)
                    && existing != &intent
                {
                    return Err(AgentError::new(
                        ErrorCode::JournalCorrupt,
                        "tool intent identity changed within one tool call",
                    ));
                }
                if !intents.contains_key(tool_call_id) {
                    intent_order.push(tool_call_id.to_owned());
                    intents.insert(tool_call_id.to_owned(), intent);
                }
            } else if record.kind == "tool.result" {
                resolved_tools.insert(required_record_string(record, "toolCallId")?.to_owned());
            } else if record.kind == "approval.requested" {
                let run_id = required_record_string(record, "runId")?;
                let approval = record
                    .data
                    .get("approval")
                    .and_then(Value::as_object)
                    .ok_or_else(|| {
                        AgentError::new(
                            ErrorCode::JournalCorrupt,
                            "approval.requested is missing approval object",
                        )
                    })?;
                let tool_call_id = approval
                    .get("toolCallId")
                    .and_then(Value::as_str)
                    .ok_or_else(|| {
                        AgentError::new(
                            ErrorCode::JournalCorrupt,
                            "approval.requested is missing toolCallId",
                        )
                    })?;
                let approval_id = approval
                    .get("approvalId")
                    .and_then(Value::as_str)
                    .ok_or_else(|| {
                        AgentError::new(
                            ErrorCode::JournalCorrupt,
                            "approval.requested is missing approvalId",
                        )
                    })?;
                approval_by_tool.insert(
                    tool_call_id.to_owned(),
                    (run_id.to_owned(), approval_id.to_owned()),
                );
            } else if record.kind == "approval.resolved" {
                let approval_id = required_record_string(record, "approvalId")?;
                let choice = record
                    .data
                    .pointer("/decision/choice")
                    .and_then(Value::as_str)
                    .ok_or_else(|| {
                        AgentError::new(
                            ErrorCode::JournalCorrupt,
                            "approval.resolved is missing decision.choice",
                        )
                    })?;
                resolved_approvals.insert(approval_id.to_owned(), choice.to_owned());
            } else if record.kind == "event.emitted"
                && record.data.pointer("/event/type").and_then(Value::as_str)
                    == Some("tool.started")
            {
                let tool_call_id = record
                    .data
                    .pointer("/event/data/toolCallId")
                    .and_then(Value::as_str)
                    .ok_or_else(|| {
                        AgentError::new(
                            ErrorCode::JournalCorrupt,
                            "tool.started event is missing toolCallId",
                        )
                    })?;
                started_tools.insert(tool_call_id.to_owned());
            }
        }
        let unresolved = intent_order
            .iter()
            .filter(|tool_call_id| !resolved_tools.contains(*tool_call_id))
            .cloned()
            .collect::<Vec<_>>();
        let ambiguous = unresolved
            .iter()
            .filter(|tool_call_id| {
                intents[*tool_call_id].status == "started" || started_tools.contains(*tool_call_id)
            })
            .cloned()
            .collect::<Vec<_>>();
        for tool_call_id in &unresolved {
            let intent = &intents[tool_call_id];
            let mut recovery_intent = intent.clone();
            if intent.status == "awaiting_approval" {
                if let Some((approval_run_id, approval_id)) = approval_by_tool.get(tool_call_id) {
                    if let Some(choice) = resolved_approvals.get(approval_id) {
                        recovery_intent.status = if choice == "deny" {
                            "denied".to_owned()
                        } else {
                            "approved".to_owned()
                        };
                    } else {
                        self.append_record(
                            session_id,
                            "approval.resolved",
                            serde_json::json!({
                                "runId":approval_run_id,
                                "approvalId":approval_id,
                                "decision":{"choice":"deny"},
                                "resolutionSource":"disconnect"
                            }),
                        )
                        .await?;
                        resolved_approvals.insert(approval_id.clone(), "deny".to_owned());
                    }
                } else {
                    recovery_intent.status = "prepared".to_owned();
                }
            }
            let outcome_known = intent.status != "started" && !started_tools.contains(tool_call_id);
            self.append_record(
                session_id,
                "tool.result",
                serde_json::json!({
                    "runId":intent.run_id,
                    "turnId":intent.turn_id,
                    "toolCallId":tool_call_id,
                    "result":recovered_tool_result(&recovery_intent, outcome_known),
                    "outcomeKnown":outcome_known
                }),
            )
            .await?;
        }

        let records_after_results = self.load(session_id).await?;
        let existing_tool_messages = records_after_results
            .iter()
            .filter(|record| record.kind == "message.appended")
            .filter_map(|record| {
                record
                    .data
                    .pointer("/message/toolCallId")
                    .and_then(Value::as_str)
            })
            .map(str::to_owned)
            .collect::<BTreeSet<_>>();
        let tool_results = records_after_results
            .iter()
            .filter(|record| record.kind == "tool.result")
            .map(|record| {
                let tool_call_id = required_record_string(record, "toolCallId")?.to_owned();
                let result = record
                    .data
                    .get("result")
                    .and_then(Value::as_object)
                    .ok_or_else(|| {
                        AgentError::new(
                            ErrorCode::JournalCorrupt,
                            "tool.result is missing result object",
                        )
                    })?;
                let content = result.get("content").cloned().ok_or_else(|| {
                    AgentError::new(ErrorCode::JournalCorrupt, "tool.result is missing content")
                })?;
                let is_error = result
                    .get("isError")
                    .and_then(Value::as_bool)
                    .ok_or_else(|| {
                        AgentError::new(ErrorCode::JournalCorrupt, "tool.result is missing isError")
                    })?;
                Ok::<_, AgentError>((tool_call_id, content, is_error))
            })
            .collect::<Result<Vec<_>, _>>()?;
        for (tool_call_id, content, is_error) in tool_results {
            if existing_tool_messages.contains(&tool_call_id) {
                continue;
            }
            let intent = intents.get(&tool_call_id).ok_or_else(|| {
                AgentError::new(
                    ErrorCode::JournalCorrupt,
                    "tool result without canonical message has no matching intent",
                )
            })?;
            self.append_record(
                session_id,
                "message.appended",
                serde_json::json!({
                    "runId":intent.run_id,
                    "turnId":intent.turn_id,
                    "message":{
                        "messageId":format!("message-{}",Uuid::new_v4()),
                        "role":"tool",
                        "toolCallId":tool_call_id,
                        "toolName":intent.name,
                        "content":content,
                        "isError":is_error,
                        "time":Utc::now()
                    }
                }),
            )
            .await?;
        }

        let terminal_runs = records
            .iter()
            .filter(|record| record.kind == "run.terminal")
            .filter_map(|record| record.data["runId"].as_str())
            .collect::<BTreeSet<_>>();
        let interrupted = records
            .iter()
            .filter(|record| record.kind == "run.accepted")
            .map(|record| required_record_string(record, "runId"))
            .collect::<Result<Vec<_>, _>>()?
            .into_iter()
            .filter(|run_id| !terminal_runs.contains(run_id))
            .map(str::to_owned)
            .collect::<Vec<_>>();
        for run_id in &interrupted {
            self.append_record(
                session_id,
                "run.terminal",
                serde_json::json!({
                    "runId":run_id,
                    "status":"interrupted",
                    "error":{
                        "code":-32005,
                        "message":"Run was interrupted by a previous process exit.",
                        "retryable":false,
                        "details":{"kind":"recovered_interrupted_run"}
                    }
                }),
            )
            .await?;
        }

        let recovered_records = self.load(session_id).await?;
        let existing_terminal_events = recovered_records
            .iter()
            .filter(|record| record.kind == "event.emitted")
            .filter_map(|record| {
                let event = record.data.get("event")?;
                let event_type = event.get("type")?.as_str()?;
                if !matches!(
                    event_type,
                    "run.completed" | "run.failed" | "run.cancelled" | "run.interrupted"
                ) {
                    return None;
                }
                Some((
                    event.get("runId")?.as_str()?.to_owned(),
                    event_type.to_owned(),
                ))
            })
            .collect::<BTreeSet<_>>();
        let mut next_event_seq = recovered_records
            .iter()
            .filter(|record| record.kind == "event.emitted")
            .filter_map(|record| record.data.pointer("/event/seq").and_then(Value::as_u64))
            .max()
            .unwrap_or(0)
            .saturating_add(1);
        for record in recovered_records
            .iter()
            .filter(|record| record.kind == "run.terminal")
        {
            let run_id = required_record_string(record, "runId")?;
            let status = required_record_string(record, "status")?;
            let event_type = match status {
                "completed" => "run.completed",
                "failed" => "run.failed",
                "cancelled" => "run.cancelled",
                "interrupted" => "run.interrupted",
                _ => {
                    return Err(AgentError::new(
                        ErrorCode::JournalCorrupt,
                        "run.terminal status is invalid",
                    ));
                }
            }
            .to_owned();
            if existing_terminal_events.contains(&(run_id.to_owned(), event_type.clone())) {
                continue;
            }
            if next_event_seq == 0 || next_event_seq > MAX_SAFE_INTEGER {
                return Err(AgentError::new(
                    ErrorCode::JournalCorrupt,
                    "recovered event sequence exceeds the safe integer maximum",
                ));
            }
            let mut data = record.data.clone();
            data.as_object_mut()
                .ok_or_else(|| {
                    AgentError::new(
                        ErrorCode::JournalCorrupt,
                        "run.terminal data must be an object",
                    )
                })?
                .remove("runId");
            self.append_record(
                session_id,
                "event.emitted",
                serde_json::json!({
                    "event":{
                        "sessionId":session_id,
                        "runId":run_id,
                        "seq":next_event_seq,
                        "time":Utc::now(),
                        "type":event_type,
                        "data":data
                    }
                }),
            )
            .await?;
            next_event_seq += 1;
        }
        Ok(RecoverySummary {
            interrupted_runs: interrupted.len(),
            ambiguous_tools: ambiguous.len(),
        })
    }

    /// 功能：在 Session 内按 SHA-256 内容寻址原子保存 artifact 并追加 `artifact.created`。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// 返回引用前先写入、同步、无覆盖发布、同步目录，再持久化引用记录；协议不暴露主机路径。
    pub async fn store_artifact(
        &self,
        session_id: &str,
        bytes: &[u8],
        media_type: &str,
    ) -> Result<ArtifactRef, AgentError> {
        validate_id(session_id, "session")?;
        validate_media_type(media_type)?;
        let digest = Sha256::digest(bytes);
        let sha256 = format!("{digest:x}");
        let artifact_id = format!("artifact-{sha256}");
        let directory = self.session_dir(session_id)?.join("artifacts");
        let publish_directory = directory.clone();
        let publish_artifact_id = artifact_id.clone();
        let publish_bytes = bytes.to_vec();
        task::spawn_blocking(move || {
            publish_artifact_no_replace(&publish_directory, &publish_artifact_id, &publish_bytes)
        })
        .await
        .map_err(|_| {
            AgentError::new(
                ErrorCode::InternalError,
                "artifact publication worker failed",
            )
        })??;
        let artifact = ArtifactRef {
            artifact_id,
            media_type: media_type.to_owned(),
            byte_length: bytes.len() as u64,
            sha256,
            display_name: None,
            extensions: BTreeMap::new(),
        };
        self.append_record(
            session_id,
            "artifact.created",
            serde_json::json!({"artifact":artifact}),
        )
        .await?;
        Ok(artifact)
    }

    /// 功能：以唯一 opaque ID 无覆盖发布一张已验证图像并追加 `artifact.created`。
    ///
    /// 输入：Session ID、完整图像 bytes 与 PNG/JPEG/WebP/GIF media type。
    /// 输出：含唯一 artifactId、准确长度和 SHA-256 的 portable reference。
    /// 不变量：基础魔数先验证；临时文件 flush、无覆盖发布、目录 flush 均早于 journal；相同图像多次输出也不复用 ID。
    /// 失败：MIME/魔数、文件发布、同步或 journal append 失败时返回脱敏错误；已发布未引用文件保持有效且不回滚。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn store_image_artifact(
        &self,
        session_id: &str,
        bytes: &[u8],
        media_type: &str,
    ) -> Result<ArtifactRef, AgentError> {
        validate_id(session_id, "session")?;
        validate_image_signature(media_type, bytes)?;
        if bytes.is_empty() {
            return Err(invalid_image_artifact_error());
        }
        let sha256 = format!("{:x}", Sha256::digest(bytes));
        let artifact_id = format!("artifact-{}", Uuid::new_v4());
        let directory = self.session_dir(session_id)?.join("artifacts");
        let publish_directory = directory.clone();
        let publish_artifact_id = artifact_id.clone();
        let publish_bytes = bytes.to_vec();
        task::spawn_blocking(move || {
            publish_artifact_no_replace(&publish_directory, &publish_artifact_id, &publish_bytes)
        })
        .await
        .map_err(|_| {
            AgentError::new(
                ErrorCode::InternalError,
                "image artifact publication worker failed",
            )
        })??;
        let artifact = ArtifactRef {
            artifact_id,
            media_type: media_type.to_owned(),
            byte_length: bytes.len() as u64,
            sha256,
            display_name: None,
            extensions: BTreeMap::new(),
        };
        self.append_record(
            session_id,
            "artifact.created",
            serde_json::json!({"artifact":artifact}),
        )
        .await?;
        Ok(artifact)
    }

    /// 功能：把 Provider 输出图片及其 assistant 消息作为一个连续 journal 批次发布。
    ///
    /// 输入：Session ID、已经由 Provider adapter 解码但仍视为不可信的完整图片批次，以及只根据最终引用构造消息记录 data 的闭包。
    /// 输出：保持源顺序的 portable artifact 引用；成功时所有文件、`artifact.created` 和 assistant `message.appended` 均已同步。
    /// 不变量：任一图片的 MIME、魔数、空内容或引用候选无效时，在第一项文件/记录发布前失败；
    /// assistant data 在文件发布前构造并随全部 artifact 记录一起验证；文件全部发布成功后才以一个预编码缓冲批次追加；
    /// 确认 journal 已回退时尽力清理本批文件，回退状态不确定时保留文件以免可见引用悬空。
    /// 失败：空批次、图片验证、assistant data、路径、锁、文件发布或 journal 批次追加失败时返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) async fn store_image_completion<F>(
        &self,
        session_id: &str,
        images: &[ProviderImage],
        message_data_builder: F,
    ) -> Result<Vec<ArtifactRef>, AgentError>
    where
        F: FnOnce(&[ArtifactRef]) -> Value + Send + 'static,
    {
        validate_id(session_id, "session")?;
        if images.is_empty() {
            return Err(invalid_image_artifact_error());
        }
        for image in images {
            if image.bytes.is_empty() {
                return Err(invalid_image_artifact_error());
            }
            validate_image_signature(&image.media_type, &image.bytes)
                .map_err(|_| invalid_image_artifact_error())?;
        }
        let directory = self.session_dir(session_id)?;
        let session_id = session_id.to_owned();
        let images = images.to_vec();
        task::spawn_blocking(move || {
            store_image_completion_sync(
                &directory,
                &session_id,
                &images,
                message_data_builder,
                append_records_unlocked,
            )
        })
        .await
        .map_err(|_| {
            AgentError::new(
                ErrorCode::InternalError,
                "image completion batch publication worker failed",
            )
        })?
    }

    /// 功能：把 active Agent run 捕获的敏感桌面 PNG 原子发布并追加带双重扩展的 `artifact.created`。
    ///
    /// 输入：Session ID、来自 Agent 调用链的可信 run ID、完整 PNG 字节与同一 run 的取消令牌。
    /// 输出：携带封闭 `org.agentprotocol.computer` 扩展的 portable image artifact reference。
    /// 不变量：复制前后检查 token；可取消地等待 append lock；锁内把 durable cancellation_requested/terminal 与 token 一并视为非 active，再验证候选、发布并追加；该锁是取消记录与 artifact.created 的持久化线性化点。
    /// 失败：取消、run 非 active、PNG 魔数/大小、路径、发布、journal 校验或同步失败时失败关闭；已发布未引用文件不回滚。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn store_computer_screenshot_artifact(
        &self,
        session_id: &str,
        run_id: &str,
        bytes: &[u8],
        cancellation: &CancellationToken,
    ) -> Result<ArtifactRef, AgentError> {
        ensure_computer_screenshot_not_cancelled(cancellation)?;
        validate_id(session_id, "session")?;
        validate_id(run_id, "run")?;
        validate_image_signature("image/png", bytes)?;
        if bytes.is_empty() || bytes.len() > MAX_COMPUTER_PNG_BYTES {
            return Err(invalid_image_artifact_error());
        }
        ensure_computer_screenshot_not_cancelled(cancellation)?;
        let directory = self.session_dir(session_id)?;
        let session_id = session_id.to_owned();
        let run_id = run_id.to_owned();
        let publish_bytes = bytes.to_vec();
        ensure_computer_screenshot_not_cancelled(cancellation)?;
        let cancellation = cancellation.clone();
        task::spawn_blocking(move || {
            store_computer_screenshot_artifact_sync(
                &directory,
                &session_id,
                &run_id,
                &publish_bytes,
                &cancellation,
                || {},
            )
        })
        .await
        .map_err(|_| {
            AgentError::new(
                ErrorCode::InternalError,
                "computer image artifact publication worker failed",
            )
        })?
    }

    /// 功能：从当前 selected Session chain 安全读取并复核一个输入图像 artifact。
    ///
    /// 输入：Session ID、portable artifact reference 和本次允许读取的最大字节数。
    /// 输出：与 durable reference 的长度、SHA-256、MIME 和基础魔数全部一致的图像字节。
    /// 不变量：路径只由已验证 Session/artifact ID 派生；最终文件 no-follow 打开且必须为 regular file；host path 不进入错误。
    /// 失败：非本 Session/branch 引用、元数据不符、symlink、非普通文件、超限、hash/MIME/魔数或 I/O 错误均脱敏返回。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn read_verified_image_artifact(
        &self,
        session_id: &str,
        artifact: &ArtifactRef,
        max_bytes: usize,
    ) -> Result<Vec<u8>, AgentError> {
        validate_id(session_id, "session")?;
        validate_id(&artifact.artifact_id, "artifact")?;
        validate_image_reference_shape(artifact, max_bytes)?;
        let directory = self.session_dir(session_id)?;
        let session_id = session_id.to_owned();
        let artifact = artifact.clone();
        task::spawn_blocking(move || {
            read_verified_image_artifact_sync(&directory, &session_id, &artifact, max_bytes)
        })
        .await
        .map_err(|_| {
            AgentError::new(
                ErrorCode::InternalError,
                "artifact verification worker failed",
            )
        })?
    }

    /// 功能：按 artifact ID 从当前 selected Session chain 解析引用并完整复核图片字节。
    ///
    /// 输入：Session ID、opaque artifact ID 与单 artifact 最大 32 MiB 类调用上限。
    /// 输出：durable portable 引用及一次性完成 length/hash/MIME/魔数验证的完整字节。
    /// 不变量：引用只能来自同 Session selected chain；路径 no-follow，返回值不含主机路径。
    /// 失败：归属、journal、ID、文件形状、长度、hash、MIME、魔数或上限无效时返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn read_verified_image_artifact_by_id(
        &self,
        session_id: &str,
        artifact_id: &str,
        max_bytes: usize,
    ) -> Result<(ArtifactRef, Vec<u8>), AgentError> {
        validate_id(session_id, "session")?;
        validate_id(artifact_id, "artifact")?;
        let directory = self.session_dir(session_id)?;
        let session_id = session_id.to_owned();
        let artifact_id = artifact_id.to_owned();
        task::spawn_blocking(move || {
            let artifact = durable_image_reference_sync(&directory, &session_id, &artifact_id)?;
            validate_image_reference_shape(&artifact, max_bytes)?;
            let bytes = read_verified_image_bytes_sync(&directory, &artifact, max_bytes)?;
            Ok((artifact, bytes))
        })
        .await
        .map_err(|_| {
            AgentError::new(
                ErrorCode::InternalError,
                "artifact verification worker failed",
            )
        })?
    }

    /// 功能：验证 Session ID 并返回其固定目录。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn session_dir(&self, session_id: &str) -> Result<PathBuf, AgentError> {
        validate_id(session_id, "session")?;
        Ok(self.root.join("sessions").join(session_id))
    }
}

/// 功能：在 Session append lock 内预构造并连续发布一批已验证图片、引用记录与 assistant 消息。
///
/// 输入：固定 Session 目录/身份、不可变图片批次、assistant data builder 与可替换的批次 append 边界。
/// 输出：与图片顺序一致、文件及全部记录均 durable 的引用集合。
/// 不变量：先构造并验证完整 artifact/message 候选 journal，再发布全部唯一文件，最后追加一个预编码记录缓冲；
/// 文件失败或已确认 journal 回退时尽力清理本批唯一 ID。append 边界参数仅用于确定性故障测试，生产固定传入标准实现。
/// 失败：journal、候选、assistant data、路径、发布、同步或批次追加失败时返回结构化错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn store_image_completion_sync<F, A>(
    directory: &Path,
    session_id: &str,
    images: &[ProviderImage],
    message_data_builder: F,
    append_records: A,
) -> Result<Vec<ArtifactRef>, AgentError>
where
    F: FnOnce(&[ArtifactRef]) -> Value,
    A: FnOnce(&Path, &[JournalRecord]) -> Result<(), (AgentError, bool)>,
{
    let lock = open_append_lock(directory)?;
    lock.lock_exclusive()?;
    let result = (|| {
        let path = directory.join("journal.jsonl");
        let journal = read_journal(&path)?;
        validate_journal_semantics(&journal.records)?;
        let artifact_directory = directory.join("artifacts");
        let mut candidate = journal.records;
        let mut artifacts = Vec::with_capacity(images.len());
        let mut records = Vec::with_capacity(images.len());

        for image in images {
            let artifact_id = loop {
                let candidate_id = format!("artifact-{}", Uuid::new_v4());
                match std::fs::symlink_metadata(artifact_directory.join(&candidate_id)) {
                    Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                        break candidate_id;
                    }
                    Ok(_) => continue,
                    Err(_) => return Err(artifact_io_error()),
                }
            };
            let artifact = ArtifactRef {
                artifact_id,
                media_type: image.media_type.clone(),
                byte_length: u64::try_from(image.bytes.len())
                    .map_err(|_| invalid_image_artifact_error())?,
                sha256: format!("{:x}", Sha256::digest(&image.bytes)),
                display_name: None,
                extensions: BTreeMap::new(),
            };
            let record = JournalRecord::new(
                session_id.to_owned(),
                next_record_sequence(&candidate)?,
                candidate.last().map(|record| record.record_id.clone()),
                "artifact.created",
                serde_json::json!({"artifact":artifact}),
            );
            candidate.push(record.clone());
            artifacts.push(artifact);
            records.push(record);
        }
        let message_data = message_data_builder(&artifacts);
        if !message_data.is_object() {
            return Err(AgentError::new(
                ErrorCode::InvalidParams,
                "image completion message data must be an object",
            ));
        }
        let message_record = JournalRecord::new(
            session_id.to_owned(),
            next_record_sequence(&candidate)?,
            candidate.last().map(|record| record.record_id.clone()),
            "message.appended",
            message_data,
        );
        candidate.push(message_record.clone());
        records.push(message_record);
        validate_journal_semantics(&candidate)?;

        let mut published = Vec::with_capacity(images.len());
        for (artifact, image) in artifacts.iter().zip(images) {
            if let Err(error) = publish_artifact_no_replace(
                &artifact_directory,
                &artifact.artifact_id,
                &image.bytes,
            ) {
                let _ = std::fs::remove_file(artifact_directory.join(&artifact.artifact_id));
                for artifact_id in &published {
                    let _ = std::fs::remove_file(artifact_directory.join(artifact_id));
                }
                let _ = sync_directory(&artifact_directory);
                return Err(error);
            }
            published.push(artifact.artifact_id.clone());
        }
        if let Err((error, journal_unchanged)) = append_records(&path, &records) {
            if journal_unchanged {
                for artifact_id in &published {
                    let _ = std::fs::remove_file(artifact_directory.join(artifact_id));
                }
                let _ = sync_directory(&artifact_directory);
            }
            return Err(error);
        }
        Ok(artifacts)
    })();
    let unlock = FileExt::unlock(&lock);
    result.and_then(|artifacts| unlock.map(|()| artifacts).map_err(AgentError::from))
}

/// 功能：在阻塞 worker 内把敏感桌面 PNG 发布与 `artifact.created` 线性化到 Session append lock。
///
/// 输入：固定 Session 目录/身份、已复制 PNG、同 run 取消令牌及仅供测试观察锁竞争的回调。
/// 输出：文件与记录均 durable 后的敏感 image artifact reference。
/// 不变量：锁内先复核 token 和 durable run 生命周期，再构造并验证候选记录；发布前、发布后及 append 前继续复核 token；取消记录无法越过同一锁后仍先于 artifact.created。
/// 失败：锁等待取消、run cancellation_requested/terminal、候选无效、发布或追加失败均返回原结构化错误；已发布未引用文件不回滚。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn store_computer_screenshot_artifact_sync(
    directory: &Path,
    session_id: &str,
    run_id: &str,
    publish_bytes: &[u8],
    cancellation: &CancellationToken,
    mut on_lock_contention: impl FnMut(),
) -> Result<ArtifactRef, AgentError> {
    ensure_computer_screenshot_not_cancelled(cancellation)?;
    let lock = open_append_lock(directory)?;
    lock_exclusive_cancellable(&lock, cancellation, &mut on_lock_contention)?;
    let result = (|| {
        ensure_computer_screenshot_not_cancelled(cancellation)?;
        let path = directory.join("journal.jsonl");
        let journal = read_journal(&path)?;
        validate_journal_semantics(&journal.records)?;
        if !run_is_only_active(&journal.records, run_id)? {
            return Err(inactive_computer_run_error());
        }
        ensure_computer_screenshot_not_cancelled(cancellation)?;

        let sha256 = format!("{:x}", Sha256::digest(publish_bytes));
        ensure_computer_screenshot_not_cancelled(cancellation)?;
        let artifact_id = format!("artifact-{}", Uuid::new_v4());
        let extension = serde_json::json!({
            "source":"desktop_capture",
            "runId":run_id,
            "sensitivity":"desktop_sensitive",
            "retention":"session_lifecycle"
        });
        let mut extensions = BTreeMap::new();
        extensions.insert(COMPUTER_EXTENSION_NAMESPACE.to_owned(), extension.clone());
        let mut record_extensions = BTreeMap::new();
        record_extensions.insert(COMPUTER_EXTENSION_NAMESPACE.to_owned(), extension);
        let artifact = ArtifactRef {
            artifact_id: artifact_id.clone(),
            media_type: "image/png".to_owned(),
            byte_length: u64::try_from(publish_bytes.len())
                .map_err(|_| invalid_image_artifact_error())?,
            sha256,
            display_name: None,
            extensions,
        };
        let seq = next_record_sequence(&journal.records)?;
        let parent_id = journal
            .records
            .last()
            .map(|record| record.record_id.clone());
        let record = JournalRecord::new(
            session_id.to_owned(),
            seq,
            parent_id,
            "artifact.created",
            serde_json::json!({
                "artifact":artifact,
                "extensions":record_extensions
            }),
        );
        let mut candidate = journal.records;
        candidate.push(record.clone());
        validate_journal_semantics(&candidate)?;

        ensure_computer_screenshot_not_cancelled(cancellation)?;
        publish_artifact_no_replace(&directory.join("artifacts"), &artifact_id, publish_bytes)?;
        ensure_computer_screenshot_not_cancelled(cancellation)?;
        append_unlocked(&path, &record)?;
        Ok(artifact)
    })();
    let unlock = FileExt::unlock(&lock);
    result.and_then(|artifact| unlock.map(|()| artifact).map_err(AgentError::from))
}

/// 功能：可取消地获取 Session append lock，供敏感截图等待期间及时失败关闭。
///
/// 输入：append lock 文件、同 run 取消令牌与锁竞争观察回调。
/// 输出：当前线程持有独占锁时成功。
/// 不变量：每次 WouldBlock 后先通知观察者并复核 token，再短暂退避；非竞争 I/O 错误不重试。
/// 失败：token 取消返回 Cancelled，其他锁错误保留 I/O 映射。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn lock_exclusive_cancellable(
    lock: &File,
    cancellation: &CancellationToken,
    on_contention: &mut impl FnMut(),
) -> Result<(), AgentError> {
    loop {
        ensure_computer_screenshot_not_cancelled(cancellation)?;
        match lock.try_lock_exclusive() {
            Ok(()) => return Ok(()),
            Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                on_contention();
                ensure_computer_screenshot_not_cancelled(cancellation)?;
                std::thread::sleep(Duration::from_millis(1));
            }
            Err(error) => return Err(error.into()),
        }
    }
}

/// 功能：在截图复制、锁等待与持久化边界统一检查协作取消。
///
/// 输入：同一 Agent run 的 CancellationToken。
/// 输出：未取消时成功。
/// 不变量：错误不携带 PNG、run ID、Session ID 或宿主路径。
/// 失败：已取消时返回稳定 Cancelled。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn ensure_computer_screenshot_not_cancelled(
    cancellation: &CancellationToken,
) -> Result<(), AgentError> {
    if cancellation.is_cancelled() {
        return Err(inactive_computer_run_error());
    }
    Ok(())
}

/// 功能：验证输入图像 artifact reference 的 MIME、长度与 SHA-256 基础形状。
///
/// 输入：未经信任的 portable reference 和调用方字节上限。
/// 输出：字段可进入 selected-chain 与文件复核时成功。
/// 不变量：只接受 PNG/JPEG/WebP/GIF、非空且不超限的长度以及小写 64 位十六进制摘要。
/// 失败：字段或限制违规返回不含 path 和原始图像数据的 `InvalidParams`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_image_reference_shape(
    artifact: &ArtifactRef,
    max_bytes: usize,
) -> Result<(), AgentError> {
    let media_supported = matches!(
        artifact.media_type.as_str(),
        "image/png" | "image/jpeg" | "image/webp" | "image/gif"
    );
    let length_valid = artifact.byte_length > 0
        && usize::try_from(artifact.byte_length)
            .ok()
            .is_some_and(|length| length <= max_bytes);
    let digest_valid = artifact.sha256.len() == 64
        && artifact
            .sha256
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte));
    if media_supported && length_valid && digest_valid {
        Ok(())
    } else {
        Err(invalid_image_artifact_error())
    }
}

/// 功能：同步验证 selected-chain durable 记录后 no-follow 读取一个图像 artifact。
///
/// 输入：固定 Session 目录、期望 Session ID、portable reference 和最大字节数。
/// 输出：长度、摘要、媒体与魔数全部匹配的字节。
/// 不变量：artifact 文件名只来自已验证 ID；只接受当前 selected parent chain 上更早的 `artifact.created`。
/// 失败：journal、branch、引用、文件类型、symlink、长度、hash、魔数或 I/O 违规均返回脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn read_verified_image_artifact_sync(
    directory: &Path,
    session_id: &str,
    artifact: &ArtifactRef,
    max_bytes: usize,
) -> Result<Vec<u8>, AgentError> {
    let durable = durable_image_reference_sync(directory, session_id, &artifact.artifact_id)?;
    if &durable != artifact {
        return Err(invalid_image_artifact_error());
    }
    read_verified_image_bytes_sync(directory, artifact, max_bytes)
}

/// 功能：从当前 selected parent chain 按 ID 解析唯一 durable 图片引用。
///
/// 输入：固定 Session 目录、期望 Session ID 与安全 artifact ID。
/// 输出：journal 中完整 portable artifact 引用。
/// 不变量：只搜索 selected chain 上的 `artifact.created`，不读取 artifact 文件。
/// 失败：journal、拓扑、归属、重复/缺失或引用 JSON 无效时返回脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn durable_image_reference_sync(
    directory: &Path,
    session_id: &str,
    artifact_id: &str,
) -> Result<ArtifactRef, AgentError> {
    let journal = read_journal_shared(directory, session_id)?;
    if journal.header.session_id != session_id {
        return Err(invalid_image_artifact_error());
    }
    let topology = validate_journal_semantics(&journal.records)?;
    let selected_head = topology
        .selected_head_record_id
        .as_deref()
        .ok_or_else(invalid_image_artifact_error)?;
    let chain = parent_chain_indexes(&journal.records, &topology.record_indexes, selected_head)?;
    let durable_reference = chain
        .into_iter()
        .rev()
        .filter_map(|index| {
            let record = &journal.records[index];
            (record.kind == "artifact.created")
                .then(|| record.data.get("artifact"))
                .flatten()
        })
        .find(|value| value.get("artifactId").and_then(Value::as_str) == Some(artifact_id))
        .ok_or_else(invalid_image_artifact_error)?;
    let durable: ArtifactRef = serde_json::from_value(durable_reference.clone())
        .map_err(|_| invalid_image_artifact_error())?;
    Ok(durable)
}

/// 功能：no-follow 读取并复核一个已由 journal 绑定的图片文件。
///
/// 输入：固定 Session 目录、完整 durable 引用和调用上限。
/// 输出：长度、SHA-256、MIME 魔数全部匹配的完整字节。
/// 不变量：最终路径仅由安全 artifact ID 派生且必须是 regular file。
/// 失败：目录、symlink、文件类型、长度、hash、MIME、魔数或 I/O 无效时返回脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn read_verified_image_bytes_sync(
    directory: &Path,
    artifact: &ArtifactRef,
    max_bytes: usize,
) -> Result<Vec<u8>, AgentError> {
    let artifacts_directory = directory.join("artifacts");
    validate_real_directory(directory)?;
    validate_real_directory(&artifacts_directory)?;
    let path = artifacts_directory.join(&artifact.artifact_id);
    let metadata = std::fs::symlink_metadata(&path).map_err(|_| artifact_io_error())?;
    if !metadata.file_type().is_file() || metadata.file_type().is_symlink() {
        return Err(invalid_image_artifact_error());
    }
    let file = open_artifact_no_follow(&path)?;
    let opened_metadata = file.metadata().map_err(|_| artifact_io_error())?;
    if !opened_metadata.is_file()
        || opened_metadata.len() != artifact.byte_length
        || opened_metadata.len() > max_bytes as u64
    {
        return Err(invalid_image_artifact_error());
    }
    let mut bytes = Vec::with_capacity(usize::try_from(artifact.byte_length).unwrap_or(max_bytes));
    let read_limit = u64::try_from(max_bytes)
        .unwrap_or(u64::MAX - 1)
        .saturating_add(1);
    file.take(read_limit)
        .read_to_end(&mut bytes)
        .map_err(|_| artifact_io_error())?;
    if bytes.len() != artifact.byte_length as usize || bytes.len() > max_bytes {
        return Err(invalid_image_artifact_error());
    }
    let actual_digest = format!("{:x}", Sha256::digest(&bytes));
    if actual_digest != artifact.sha256 {
        return Err(invalid_image_artifact_error());
    }
    validate_image_signature(&artifact.media_type, &bytes)
        .map_err(|_| invalid_image_artifact_error())?;
    Ok(bytes)
}

/// 功能：把 artifact bytes 以临时文件、flush、无覆盖 link、目录 flush 的顺序发布。
///
/// 输入：已建立的 Session artifacts 目录、内容寻址 artifact ID 和完整字节。
/// 输出：目标文件存在且内容与输入完全一致时成功。
/// 不变量：永不覆盖既有路径；既有 regular file 必须逐字节一致；journal append 由调用方在成功后执行。
/// 失败：目录/symlink、临时写入、无覆盖发布、既有内容或同步失败返回不含 host path 的错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn publish_artifact_no_replace(
    directory: &Path,
    artifact_id: &str,
    bytes: &[u8],
) -> Result<(), AgentError> {
    validate_real_directory(directory)?;
    let target = directory.join(artifact_id);
    let temporary = directory.join(format!(".{artifact_id}.{}.tmp", Uuid::new_v4()));
    let mut options = OpenOptions::new();
    options.create_new(true).write(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;

        options.mode(0o600);
    }
    let mut file = options.open(&temporary).map_err(|_| artifact_io_error())?;
    let write_result = (|| {
        file.write_all(bytes).map_err(|_| artifact_io_error())?;
        file.flush().map_err(|_| artifact_io_error())?;
        file.sync_all().map_err(|_| artifact_io_error())?;
        match std::fs::hard_link(&temporary, &target) {
            Ok(()) => Ok(()),
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => {
                verify_existing_artifact(&target, bytes)
            }
            Err(_) => Err(artifact_io_error()),
        }
    })();
    drop(file);
    let _ = std::fs::remove_file(&temporary);
    write_result?;
    sync_directory(directory)
}

/// 功能：no-follow 复核内容寻址目标已经存在且与待发布 bytes 完全一致。
///
/// 输入：派生目标路径与本次待发布字节。
/// 输出：regular file 长度和内容完全一致时成功。
/// 不变量：不接受 symlink、目录、设备或仅 digest 碰巧相同的替代内容。
/// 失败：文件类型、长度、读取或内容不符返回脱敏 artifact 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn verify_existing_artifact(path: &Path, expected: &[u8]) -> Result<(), AgentError> {
    let metadata = std::fs::symlink_metadata(path).map_err(|_| artifact_io_error())?;
    if !metadata.file_type().is_file()
        || metadata.file_type().is_symlink()
        || metadata.len() != expected.len() as u64
    {
        return Err(invalid_image_artifact_error());
    }
    let file = open_artifact_no_follow(path)?;
    if !file.metadata().map_err(|_| artifact_io_error())?.is_file() {
        return Err(invalid_image_artifact_error());
    }
    let mut actual = Vec::with_capacity(expected.len());
    file.take(expected.len().saturating_add(1) as u64)
        .read_to_end(&mut actual)
        .map_err(|_| artifact_io_error())?;
    if actual == expected {
        Ok(())
    } else {
        Err(invalid_image_artifact_error())
    }
}

/// 功能：拒绝 Session 或 artifacts 目录 symlink/non-directory 形状。
///
/// 输入：仅由 Session store 派生的目录路径。
/// 输出：路径当前为真实目录时成功。
/// 不变量：错误不回显目录；本检查与最终文件 O_NOFOLLOW 共同缩小路径替换面。
/// 失败：缺失、symlink、非目录或元数据读取错误返回脱敏 artifact I/O 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_real_directory(path: &Path) -> Result<(), AgentError> {
    let metadata = std::fs::symlink_metadata(path).map_err(|_| artifact_io_error())?;
    if metadata.file_type().is_dir() && !metadata.file_type().is_symlink() {
        Ok(())
    } else {
        Err(artifact_io_error())
    }
}

/// 功能：在 Unix 以 O_NOFOLLOW 打开最终 artifact 文件。
///
/// 输入：由已验证 artifact ID 派生的最终路径。
/// 输出：只读文件句柄。
/// 不变量：最终路径为 symlink 时内核拒绝跟随。
/// 失败：打开失败返回不含 path 的统一 artifact 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[cfg(unix)]
fn open_artifact_no_follow(path: &Path) -> Result<File, AgentError> {
    use std::os::unix::fs::OpenOptionsExt;

    OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_NOFOLLOW)
        .open(path)
        .map_err(|_| artifact_io_error())
}

/// 功能：在无 O_NOFOLLOW 标准扩展的平台打开已由 symlink metadata 验证的 artifact。
///
/// 输入：由已验证 artifact ID 派生的最终路径。
/// 输出：只读文件句柄。
/// 不变量：调用方仍会对打开后 metadata、长度与 digest 复核；不宣称 OS hard sandbox。
/// 失败：打开失败返回不含 path 的统一 artifact 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[cfg(not(unix))]
fn open_artifact_no_follow(path: &Path) -> Result<File, AgentError> {
    OpenOptions::new()
        .read(true)
        .open(path)
        .map_err(|_| artifact_io_error())
}

/// 功能：创建输入 artifact 引用或文件内容不一致的稳定错误。
///
/// 输入：无。
/// 输出：不可重试且不含 host path/bytes 的 `InvalidParams`。
/// 不变量：不同拒绝原因不通过错误文本泄漏 Session 文件状态。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn invalid_image_artifact_error() -> AgentError {
    AgentError::new(
        ErrorCode::InvalidParams,
        "image artifact reference is invalid",
    )
    .with_details(serde_json::json!({"kind":"invalid_image_artifact"}))
}

/// 功能：创建 artifact 文件操作失败的脱敏错误。
///
/// 输入：无。
/// 输出：不含底层 OS 文本和 host path 的 `IoError`。
/// 不变量：调用方不得把原始 `std::io::Error` 追加到该错误。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn artifact_io_error() -> AgentError {
    AgentError::new(ErrorCode::IoError, "artifact file operation failed")
}

struct LoadedJournal {
    header: SessionHeader,
    records: Vec<JournalRecord>,
}

struct JournalTopology {
    record_indexes: BTreeMap<String, usize>,
    selected_head_record_id: Option<String>,
}

struct JournalProjection {
    selected_head_record_id: Option<String>,
    compaction_record_id: Option<String>,
    messages: Vec<Value>,
}

/// 功能：为崩溃恢复生成区分“确定未启动”和“已经真实启动”的 portable 工具结果。
///
/// 输入：原始 immutable intent 以及执行结果是否确定已知。
/// 输出：符合 toolResult Schema 的结构化错误；未知执行保持 internal_error，已知未启动按 disposition 分类。
/// 不变量：不调用或重放工具；`outcomeKnown:false` 永远使用 unknown-outcome 文本，denied/rejected/awaiting 不伪报副作用可能发生。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn recovered_tool_result(intent: &RecoveryToolIntent, outcome_known: bool) -> Value {
    if !outcome_known {
        return serde_json::json!({
            "content":[{"type":"text","text":"Tool outcome is unknown after crash; the non-idempotent operation was not replayed."}],
            "isError":true,
            "terminationReason":"internal_error",
            "error":{
                "code":-32603,
                "message":"Tool outcome is unknown after crash.",
                "retryable":false,
                "details":{"kind":"recovered_unknown_outcome","toolName":intent.name}
            }
        });
    }
    let (termination_reason, code, kind, message) = match intent.status.as_str() {
        "denied" => (
            "denied",
            -32003,
            "permission_denied",
            "Tool was denied before execution and was not replayed.",
        ),
        "rejected" => (
            "validation_error",
            -32602,
            "tool_rejected",
            "Tool was rejected before execution and was not replayed.",
        ),
        "awaiting_approval" => (
            "denied",
            -32003,
            "permission_denied",
            "Pending approval was denied after disconnect; the tool did not start.",
        ),
        _ => (
            "cancelled",
            -32007,
            "cancelled",
            "Tool had not started before interruption and was not replayed.",
        ),
    };
    serde_json::json!({
        "content":[{"type":"text","text":message}],
        "isError":true,
        "terminationReason":termination_reason,
        "error":{
            "code":code,
            "message":message,
            "retryable":false,
            "details":{"kind":kind,"toolName":intent.name}
        }
    })
}

/// 功能：从 journal record data 读取必需字符串并统一映射损坏错误。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn required_record_string<'a>(
    record: &'a JournalRecord,
    field: &str,
) -> Result<&'a str, AgentError> {
    record
        .data
        .get(field)
        .and_then(Value::as_str)
        .ok_or_else(|| {
            AgentError::new(
                ErrorCode::JournalCorrupt,
                format!("{} data.{field} must be a string", record.kind),
            )
        })
}

/// 功能：判断指定 run 是否是 Session journal 中唯一尚未终止的 active run。
///
/// 输入：已完成结构验证的全部 records 与可信 run ID。
/// 输出：仅当 accepted 减去 terminal/cancellation_requested 后恰好只含目标 run 时为 true。
/// 不变量：durable cancellation_requested 立即使 run 不可发布敏感截图；不接受来自 artifact/tool 参数的 run 身份；所有生命周期字段使用公共 record 读取边界。
/// 失败：相关生命周期记录缺少字符串 runId 时返回 journal_corrupt。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn run_is_only_active(records: &[JournalRecord], run_id: &str) -> Result<bool, AgentError> {
    let mut accepted = BTreeSet::new();
    let mut terminal = BTreeSet::new();
    let mut cancellation_requested = BTreeSet::new();
    for record in records {
        match record.kind.as_str() {
            "run.accepted" => {
                accepted.insert(required_record_string(record, "runId")?.to_owned());
            }
            "run.terminal" => {
                terminal.insert(required_record_string(record, "runId")?.to_owned());
            }
            "run.cancellation_requested" => {
                cancellation_requested.insert(required_record_string(record, "runId")?.to_owned());
            }
            _ => {}
        }
    }
    let active = accepted
        .difference(&terminal)
        .filter(|candidate| !cancellation_requested.contains(*candidate))
        .collect::<Vec<_>>();
    Ok(active.len() == 1 && active[0].as_str() == run_id)
}

/// 功能：创建截图发布边界发现 run 已非 active 时的稳定失败关闭错误。
///
/// 输入：无；调用方已经在持久化线性化点确认目标 run 非唯一 active。
/// 输出：固定 code、message 与 kind 的结构化取消错误。
/// 不变量：不包含 Session 路径、截图内容、run ID 或底层锁诊断。
/// 失败：本函数始终构造错误值，本身不返回失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn inactive_computer_run_error() -> AgentError {
    AgentError::new(
        ErrorCode::Cancelled,
        "desktop screenshot run is no longer active",
    )
    .with_details(serde_json::json!({"kind":"cancelled"}))
}

/// 功能：限制持久化 ID 为 Schema `opaqueId` 的安全 ASCII 子集。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_id(value: &str, kind: &str) -> Result<(), AgentError> {
    if !id_is_valid(value) {
        return Err(AgentError::new(
            ErrorCode::InvalidParams,
            format!("invalid {kind} id"),
        ));
    }
    Ok(())
}

/// 功能：判断字符串是否符合 portable opaqueId 的 ASCII 与长度边界。
///
/// 输入：任意 UTF-8 字符串。
/// 输出：首字符字母数字、其余仅字母数字或 `-_.:` 且不超过 128 字节时为 true。
/// 不变量：判断不分配内存，也不把值写入错误或日志。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn id_is_valid(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value.bytes().enumerate().all(|(index, byte)| {
            byte.is_ascii_alphanumeric() || (index > 0 && matches!(byte, b'-' | b'_' | b'.' | b':'))
        })
}

/// 功能：拒绝未版本化的未知 core record kind。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_record_kind(kind: &str) -> Result<(), AgentError> {
    if RECORD_KINDS.contains(&kind) {
        Ok(())
    } else {
        Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            format!("unknown core journal kind: {kind}"),
        ))
    }
}

/// 功能：执行 artifact media type 的严格无参数 `type/subtype` 校验。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_media_type(value: &str) -> Result<(), AgentError> {
    let valid_char = |byte: u8| {
        byte.is_ascii_alphanumeric()
            || matches!(
                byte,
                b'!' | b'#' | b'$' | b'&' | b'^' | b'_' | b'.' | b'+' | b'-'
            )
    };
    let mut parts = value.split('/');
    let valid = parts
        .next()
        .is_some_and(|part| !part.is_empty() && part.bytes().all(valid_char))
        && parts
            .next()
            .is_some_and(|part| !part.is_empty() && part.bytes().all(valid_char))
        && parts.next().is_none();
    if valid {
        Ok(())
    } else {
        Err(AgentError::new(
            ErrorCode::InvalidParams,
            "artifact media type must be an unparameterized type/subtype",
        ))
    }
}

/// 功能：在 sessions 根的固定隐藏目录中取得不会随目标 Session rename 的 portable lease。
///
/// 输入：已经 canonical 的 sessions 根和已验证 Session ID。
/// 输出：持有 literal-loopback witness 的 per-ID coordination lease。
/// 不变量：coordination 根和 ID 目录都必须是真实 canonical 目录；不会创建或读取 Session journal。
/// 失败：路径、live owner、owner metadata、随机源或同步失败时返回 portable writer 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn acquire_session_coordination(
    sessions_root: &Path,
    session_id: &str,
) -> Result<SessionCoordinationLease, AgentError> {
    let coordination_root = sessions_root.join(".session-coordination");
    create_session_directory(&coordination_root)?;
    let directory = coordination_root.join(session_id);
    create_session_directory(&directory)?;
    let portable = PortableWriterLease::acquire(&directory, session_id)?;
    Ok(SessionCoordinationLease {
        _portable: portable,
    })
}

/// 功能：普通 writer 打开或创建 Session 前拒绝同 ID 的固定删除 tombstone。
///
/// 输入：可信 sessions 根和已验证 Session ID。
/// 输出：目标 tombstone 不存在时成功。
/// 不变量：任意真实目录、普通文件、链接或特殊条目都视为 pending delete，不跟随该路径。
/// 失败：存在或无法可靠读取时返回可重试 `session_locked`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn reject_pending_tombstone(sessions_root: &Path, session_id: &str) -> Result<(), AgentError> {
    let tombstone = sessions_root.join(".session-tombstones").join(session_id);
    match std::fs::symlink_metadata(tombstone) {
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Ok(_) | Err(_) => Err(AgentError::new(
            ErrorCode::SessionLocked,
            "session deletion is pending",
        )
        .retryable(true)
        .with_details(serde_json::json!({"kind":"session_locked"}))),
    }
}

/// 功能：打开或创建 Session 的跨进程 lock 文件。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn open_writer_lock(directory: &Path) -> Result<File, AgentError> {
    Ok(OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(directory.join("lock"))?)
}

/// 功能：按 portable rename、advisory unlock、listener close 的规范顺序释放 writer。
///
/// 输入：当前 portable lease 与已持有的 persistent `lock` 文件。
/// 输出：所有 release 阶段成功时返回空值。
/// 不变量：token 校验失败仍释放本进程 advisory/listener，但不删除 canonical claim。
/// 失败：优先报告 portable prepare，其次 advisory unlock 和最终清理错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn release_writer_locks(
    portable: &mut PortableWriterLease,
    advisory: &File,
) -> Result<(), AgentError> {
    let prepared = portable.prepare_release();
    let moved = prepared.as_ref().ok().cloned().flatten();
    let unlock = FileExt::unlock(advisory).map_err(AgentError::from);
    let finish = portable.finish_release(moved);
    prepared.and(unlock).and(finish)
}

/// 功能：把 advisory lock 冲突映射为稳定 retryable session_locked 错误。
///
/// 输入：平台 I/O 锁错误，仅用于人类诊断。
/// 输出：语言中立 SessionLocked 与 details.kind=session_locked。
/// 不变量：客户端无需解析底层错误文本。
/// 失败：本函数本身不失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn writer_conflict_error(error: &std::io::Error) -> AgentError {
    AgentError::new(
        ErrorCode::SessionLocked,
        format!("session writer lock is busy: {error}"),
    )
    .retryable(true)
    .with_details(serde_json::json!({"kind":"session_locked"}))
}

/// 功能：打开或创建只用于短时原子追加的内部 append lock。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn open_append_lock(directory: &Path) -> Result<File, AgentError> {
    Ok(OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(directory.join("append.lock"))?)
}

/// 功能：将一个可序列化对象写成完整 UTF-8 JSONL 行。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn write_line(file: &mut File, value: &impl Serialize) -> Result<(), AgentError> {
    serde_json::to_writer(&mut *file, value)
        .map_err(|error| AgentError::new(ErrorCode::IoError, error.to_string()))?;
    file.write_all(b"\n")?;
    Ok(())
}

/// 功能：在已持有 Session lock 时追加并同步一条记录。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn append_unlocked(path: &Path, record: &JournalRecord) -> Result<(), AgentError> {
    let mut file = OpenOptions::new().create(false).append(true).open(path)?;
    write_line(&mut file, record)?;
    file.sync_data()?;
    Ok(())
}

/// 功能：把一组已经完整验证的连续记录编码为一个缓冲后追加并同步。
///
/// 输入：现有 journal 路径及至少一条父链连续的记录。
/// 输出：全部记录 durable 后成功；失败返回错误与“已确认 journal 未改变/已回退”的布尔证据。
/// 不变量：序列化在打开写边界前完成；`write_all` 可能包含多个系统调用，失败后只在 `set_len+sync` 全部成功时声明可安全清理 artifact 文件。
/// 失败：序列化、打开、写入、同步或尽力回退失败时返回 I/O 错误；不回显记录正文。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn append_records_unlocked(
    path: &Path,
    records: &[JournalRecord],
) -> Result<(), (AgentError, bool)> {
    append_records_unlocked_with_writer(path, records, |file, encoded| {
        file.write_all(encoded)?;
        file.sync_data()
    })
}

/// 功能：以可替换的单次写入边界批量追加记录，并在部分写入或同步失败后尝试 durable 回滚。
///
/// 输入：现有 journal、连续记录和必须完成“写入全部编码字节并同步”的闭包。
/// 输出：成功时整批 durable；失败时同时报告是否已确认恢复原长度。
/// 不变量：原长度在调用写入闭包前取得；闭包失败后只有 `set_len` 与 `sync_data` 均成功才返回可安全清理证据。
/// 失败：空批次、编码、打开、元数据、注入写入或回滚失败时返回脱敏 I/O 错误；生产调用方固定使用完整 `write_all+sync_data`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn append_records_unlocked_with_writer<F>(
    path: &Path,
    records: &[JournalRecord],
    write_and_sync: F,
) -> Result<(), (AgentError, bool)>
where
    F: FnOnce(&mut File, &[u8]) -> std::io::Result<()>,
{
    if records.is_empty() {
        return Err((
            AgentError::new(ErrorCode::InternalError, "journal record batch is empty"),
            true,
        ));
    }
    let mut encoded = Vec::new();
    for record in records {
        serde_json::to_writer(&mut encoded, record).map_err(|_| {
            (
                AgentError::new(ErrorCode::IoError, "journal encoding failed"),
                true,
            )
        })?;
        encoded.push(b'\n');
    }
    let mut file = OpenOptions::new()
        .create(false)
        .append(true)
        .open(path)
        .map_err(|error| (AgentError::from(error), true))?;
    let original_length = file
        .metadata()
        .map_err(|error| (AgentError::from(error), true))?
        .len();
    if let Err(error) = write_and_sync(&mut file, &encoded) {
        let journal_unchanged = file
            .set_len(original_length)
            .and_then(|()| file.sync_data())
            .is_ok();
        return Err((AgentError::from(error), journal_unchanged));
    }
    Ok(())
}

/// 功能：验证调用方记录正好是当前 journal 的合法下一条记录。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_next_record(journal: &LoadedJournal, record: &JournalRecord) -> Result<(), AgentError> {
    if record.session_id != journal.header.session_id {
        return Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            "record session id does not match header",
        ));
    }
    let expected_seq = next_record_sequence(&journal.records)?;
    if record.seq != expected_seq {
        return Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            "journal append sequence is not contiguous",
        ));
    }
    let known_ids = journal
        .records
        .iter()
        .map(|item| item.record_id.as_str())
        .collect::<BTreeSet<_>>();
    if let Some(parent_id) = &record.parent_id
        && !known_ids.contains(parent_id.as_str())
    {
        return Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            "parentId does not reference an earlier record",
        ));
    }
    Ok(())
}

/// 功能：在共享 append lock 下读取一致的完整 journal 快照，并在发现未提交尾部时升级恢复。
///
/// 输入：canonical Session 目录及其已经验证的 opaque Session ID。
/// 输出：完整验证且必要时已经持久恢复的 journal。
/// 不变量：完整 LF 损坏只读失败；升级恢复前释放共享 append lock，避免锁顺序反转。
/// 失败：writer 冲突、journal 损坏、备份冲突或 I/O 失败返回结构化错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
///
/// 共享锁与所有原子追加使用的独占锁配对，避免读取到序列化 JSON 与 LF 之间的半条记录。
fn read_journal_shared(directory: &Path, session_id: &str) -> Result<LoadedJournal, AgentError> {
    let lock = open_append_lock(directory)?;
    FileExt::lock_shared(&lock)?;
    let path = directory.join("journal.jsonl");
    let requires_recovery = journal_has_uncommitted_tail(&path);
    if requires_recovery.as_ref().is_ok_and(|required| *required) {
        FileExt::unlock(&lock)?;
        recover_tail_locked(directory, session_id)?;
        let lock = open_append_lock(directory)?;
        FileExt::lock_shared(&lock)?;
        let result = read_journal(&path);
        let unlock = FileExt::unlock(&lock);
        return result.and_then(|journal| unlock.map(|()| journal).map_err(AgentError::from));
    }
    requires_recovery?;
    let result = read_journal(&path);
    let unlock = FileExt::unlock(&lock);
    result.and_then(|journal| unlock.map(|()| journal).map_err(AgentError::from))
}

/// 功能：只读取 journal 最后一个字节以判定是否存在无 LF 的未提交尾部。
///
/// 输入：固定 Session 目录内的 journal 路径。
/// 输出：非空文件最后一字节不是 LF 时为 true。
/// 不变量：调用方持有共享或独占 append lock，判定期间合法 writer 不可改变文件。
/// 失败：文件缺失返回 SessionNotFound，其他读取失败映射为 I/O 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn journal_has_uncommitted_tail(path: &Path) -> Result<bool, AgentError> {
    let mut file = File::open(path).map_err(|error| {
        if error.kind() == std::io::ErrorKind::NotFound {
            AgentError::new(ErrorCode::SessionNotFound, "session journal not found")
        } else {
            error.into()
        }
    })?;
    if file.metadata()?.len() == 0 {
        return Ok(false);
    }
    file.seek(SeekFrom::End(-1))?;
    let mut final_byte = [0_u8; 1];
    file.read_exact(&mut final_byte)?;
    Ok(final_byte[0] != b'\n')
}

/// 功能：计算下一条 journal record 的安全连续序号。
///
/// 输入：已经完整验证的 append-order records。
/// 输出：1 或最后序号加一。
/// 不变量：结果始终位于 JavaScript safe-integer 正整数范围。
/// 失败：序号溢出或超过公共上限时返回 OutputLimitExceeded。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn next_record_sequence(records: &[JournalRecord]) -> Result<u64, AgentError> {
    let next = records
        .last()
        .map_or(Some(1), |record| record.seq.checked_add(1))
        .ok_or_else(|| {
            AgentError::new(
                ErrorCode::OutputLimitExceeded,
                "journal sequence exceeds the safe integer maximum",
            )
        })?;
    if next == 0 || next > MAX_SAFE_INTEGER {
        return Err(AgentError::new(
            ErrorCode::OutputLimitExceeded,
            "journal sequence exceeds the safe integer maximum",
        ));
    }
    Ok(next)
}

/// 功能：沿 earlier-only parent 边返回根到指定 record 的索引链。
///
/// 输入：全 append records、recordId 索引与目标 head ID。
/// 输出：从根到 head 的唯一索引顺序。
/// 不变量：同一索引最多访问一次，且链不跨 Session。
/// 失败：未知 parent、未知 head 或循环返回 journal_corrupt。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parent_chain_indexes(
    records: &[JournalRecord],
    indexes: &BTreeMap<String, usize>,
    head_record_id: &str,
) -> Result<Vec<usize>, AgentError> {
    let mut chain = Vec::new();
    let mut seen = BTreeSet::new();
    let mut current = Some(head_record_id);
    while let Some(record_id) = current {
        let index = *indexes.get(record_id).ok_or_else(|| {
            AgentError::new(
                ErrorCode::JournalCorrupt,
                "parent chain references an unknown record",
            )
        })?;
        if !seen.insert(index) {
            return Err(AgentError::new(
                ErrorCode::JournalCorrupt,
                "parent chain contains a cycle",
            ));
        }
        chain.push(index);
        current = records[index].parent_id.as_deref();
    }
    chain.reverse();
    Ok(chain)
}

/// 功能：从 canonical message.appended record 提取对象引用。
///
/// 输入：任一 journal record。
/// 输出：该记录的 data.message JSON 对象。
/// 不变量：只接受 message.appended 且 message 为对象。
/// 失败：类型或字段缺失返回 journal_corrupt。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn message_from_record(record: &JournalRecord) -> Result<&Value, AgentError> {
    if record.kind != "message.appended" {
        return Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            "record does not contain a canonical message",
        ));
    }
    let message = record.data.get("message").ok_or_else(|| {
        AgentError::new(
            ErrorCode::JournalCorrupt,
            "message.appended is missing data.message",
        )
    })?;
    if !message.is_object() {
        return Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            "message.appended data.message must be an object",
        ));
    }
    Ok(message)
}

/// 功能：判断一个 record parent chain 是否不存在未终止 run、工具和审批。
///
/// 输入：完整 records/index 及目标 record ID。
/// 输出：三类 lifecycle 集合均闭合时为 true。
/// 不变量：只扫描目标 ancestry，未选 sibling 和目标后代不参与判断。
/// 失败：生命周期记录缺少规范身份字段时返回 journal_corrupt。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn chain_is_quiescent(
    records: &[JournalRecord],
    indexes: &BTreeMap<String, usize>,
    head_record_id: &str,
) -> Result<bool, AgentError> {
    let mut accepted_runs = BTreeSet::new();
    let mut terminal_runs = BTreeSet::new();
    let mut tool_intents = BTreeSet::new();
    let mut tool_results = BTreeSet::new();
    let mut requested_approvals = BTreeSet::new();
    let mut resolved_approvals = BTreeSet::new();
    for index in parent_chain_indexes(records, indexes, head_record_id)? {
        let record = &records[index];
        match record.kind.as_str() {
            "run.accepted" => {
                accepted_runs.insert(required_record_string(record, "runId")?.to_owned());
            }
            "run.terminal" => {
                terminal_runs.insert(required_record_string(record, "runId")?.to_owned());
            }
            "tool.intent" => {
                tool_intents.insert(required_record_string(record, "toolCallId")?.to_owned());
            }
            "tool.result" => {
                tool_results.insert(required_record_string(record, "toolCallId")?.to_owned());
            }
            "approval.requested" => {
                let approval_id = record
                    .data
                    .pointer("/approval/approvalId")
                    .and_then(Value::as_str)
                    .ok_or_else(|| {
                        AgentError::new(
                            ErrorCode::JournalCorrupt,
                            "approval.requested is missing approvalId",
                        )
                    })?;
                requested_approvals.insert(approval_id.to_owned());
            }
            "approval.resolved" => {
                resolved_approvals.insert(required_record_string(record, "approvalId")?.to_owned());
            }
            _ => {}
        }
    }
    Ok(accepted_runs.is_subset(&terminal_runs)
        && tool_intents.is_subset(&tool_results)
        && requested_approvals.is_subset(&resolved_approvals))
}

struct ValidatedCompaction {
    summary: Value,
    retained_record_indexes: Vec<usize>,
}

/// 功能：验证一个 durable context.compacted 的全部跨记录引用。
///
/// 输入：compaction record、全 records 与 earlier-ID 索引。
/// 输出：summary message 及 retained boundary 到 source 的 parent-chain 索引。
/// 不变量：summary 紧接 source，boundary 是 source ancestry 上的 user message，token 不增长。
/// 失败：任一引用、角色、内容、strategy 或 token 不变量失败返回 journal_corrupt。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_compaction_record(
    record: &JournalRecord,
    records: &[JournalRecord],
    indexes: &BTreeMap<String, usize>,
) -> Result<ValidatedCompaction, AgentError> {
    let source_id = required_record_string(record, "sourceLeafRecordId")?;
    let retained_id = required_record_string(record, "firstRetainedRecordId")?;
    let summary_message_id = required_record_string(record, "summaryMessageId")?;
    let summary_record_id = record.parent_id.as_deref().ok_or_else(|| {
        AgentError::new(
            ErrorCode::JournalCorrupt,
            "context.compacted must have a summary parent",
        )
    })?;
    let summary_index = *indexes.get(summary_record_id).ok_or_else(|| {
        AgentError::new(
            ErrorCode::JournalCorrupt,
            "context.compacted summary record is unknown",
        )
    })?;
    let summary_record = &records[summary_index];
    if summary_record.parent_id.as_deref() != Some(source_id) {
        return Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            "compaction summary is not an immediate source descendant",
        ));
    }
    let summary = message_from_record(summary_record)?;
    let content = summary.get("content").and_then(Value::as_array);
    let valid_summary = summary.get("messageId").and_then(Value::as_str)
        == Some(summary_message_id)
        && summary.get("role").and_then(Value::as_str) == Some("assistant")
        && summary.get("finishReason").and_then(Value::as_str) == Some("stop")
        && content.is_some_and(|blocks| {
            blocks.len() == 1
                && blocks[0].get("type").and_then(Value::as_str) == Some("text")
                && blocks[0]
                    .get("text")
                    .and_then(Value::as_str)
                    .is_some_and(|text| !text.is_empty())
        })
        && summary
            .get("provider")
            .is_some_and(provider_selection_is_valid)
        && summary.get("usage").is_some_and(usage_is_valid);
    if !valid_summary {
        return Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            "compaction summary identity or content is invalid",
        ));
    }
    let source_chain = parent_chain_indexes(records, indexes, source_id)?;
    let retained_position = source_chain
        .iter()
        .position(|index| records[*index].record_id == retained_id)
        .ok_or_else(|| {
            AgentError::new(
                ErrorCode::JournalCorrupt,
                "compaction retained boundary is not on source ancestry",
            )
        })?;
    let retained_message = message_from_record(&records[source_chain[retained_position]])?;
    if retained_message.get("role").and_then(Value::as_str) != Some("user") {
        return Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            "compaction retained boundary must be a user message",
        ));
    }
    let tokens_before = record
        .data
        .get("tokensBefore")
        .and_then(Value::as_u64)
        .filter(|value| *value <= MAX_SAFE_INTEGER)
        .ok_or_else(|| {
            AgentError::new(
                ErrorCode::JournalCorrupt,
                "compaction tokensBefore is invalid",
            )
        })?;
    if record.data.get("tokensAfter").is_some_and(|value| {
        value.as_u64().is_none_or(|tokens_after| {
            tokens_after > MAX_SAFE_INTEGER || tokens_after > tokens_before
        })
    }) {
        return Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            "compaction tokensAfter is invalid",
        ));
    }
    let strategy = required_record_string(record, "strategy")?;
    if strategy.chars().count() > 128 {
        return Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            "compaction strategy is invalid",
        ));
    }
    Ok(ValidatedCompaction {
        summary: summary.clone(),
        retained_record_indexes: source_chain[retained_position..].to_vec(),
    })
}

/// 功能：重放 append order 并验证 selected-head、tree、branch 与 compaction 状态机。
///
/// 输入：已经通过基础字段/earlier-parent 校验的全部 records。
/// 输出：recordId 索引与最终 selected head。
/// 不变量：普通记录只延伸当前 head；branch.selected 是唯一 earlier-parent 跳转控制节点。
/// 失败：tree、消息身份、target quiescence 或 compaction 违规返回 journal_corrupt。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_journal_semantics(records: &[JournalRecord]) -> Result<JournalTopology, AgentError> {
    let mut indexes = BTreeMap::new();
    let mut selected_head_record_id: Option<String> = None;
    let mut message_ids = BTreeSet::new();
    for (index, record) in records.iter().enumerate() {
        if record.kind == "branch.selected" {
            let target = required_record_string(record, "leafRecordId")?;
            if record.parent_id.as_deref() != Some(target) || !indexes.contains_key(target) {
                return Err(AgentError::new(
                    ErrorCode::JournalCorrupt,
                    "branch.selected target must equal an earlier parentId",
                ));
            }
            if !chain_is_quiescent(records, &indexes, target)? {
                return Err(AgentError::new(
                    ErrorCode::JournalCorrupt,
                    "branch.selected target was not quiescent when selected",
                ));
            }
        } else if record.parent_id.as_deref() != selected_head_record_id.as_deref() {
            return Err(AgentError::new(
                ErrorCode::JournalCorrupt,
                "ordinary journal append did not extend the selected head",
            ));
        }
        indexes.insert(record.record_id.clone(), index);
        if record.kind == "message.appended" {
            let message = message_from_record(record)?;
            let message_id = message
                .get("messageId")
                .and_then(Value::as_str)
                .ok_or_else(|| {
                    AgentError::new(
                        ErrorCode::JournalCorrupt,
                        "canonical message is missing messageId",
                    )
                })?;
            if !message_ids.insert(message_id.to_owned()) {
                return Err(AgentError::new(
                    ErrorCode::JournalCorrupt,
                    "journal contains a duplicate messageId",
                ));
            }
        }
        if record.kind == "context.compacted" {
            validate_compaction_record(record, records, &indexes)?;
        }
        selected_head_record_id = Some(record.record_id.clone());
    }
    Ok(JournalTopology {
        record_indexes: indexes,
        selected_head_record_id,
    })
}

/// 功能：按 selected parent chain 上最新 compaction 构造 Provider/session 消息投影。
///
/// 输入：完整且不可变的 journal records。
/// 输出：selected head、active compaction 与精确 canonical message 顺序。
/// 不变量：summary 只出现一次，旧前缀和未选 sibling 永不进入投影，事件不参与消息上下文。
/// 失败：任何 tree/compaction 引用无效时返回 journal_corrupt，绝不回退全历史。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn project_selected_messages(records: &[JournalRecord]) -> Result<JournalProjection, AgentError> {
    let topology = validate_journal_semantics(records)?;
    let Some(selected_head) = topology.selected_head_record_id.clone() else {
        return Ok(JournalProjection {
            selected_head_record_id: None,
            compaction_record_id: None,
            messages: Vec::new(),
        });
    };
    let selected_chain = parent_chain_indexes(records, &topology.record_indexes, &selected_head)?;
    let active_compaction_position = selected_chain
        .iter()
        .rposition(|index| records[*index].kind == "context.compacted");
    let Some(compaction_position) = active_compaction_position else {
        let messages = selected_chain
            .iter()
            .filter(|index| records[**index].kind == "message.appended")
            .map(|index| message_from_record(&records[*index]))
            .collect::<Result<Vec<_>, _>>()?
            .into_iter()
            .cloned()
            .collect();
        return Ok(JournalProjection {
            selected_head_record_id: Some(selected_head),
            compaction_record_id: None,
            messages,
        });
    };
    let compaction_index = selected_chain[compaction_position];
    let compaction = &records[compaction_index];
    let validated = validate_compaction_record(compaction, records, &topology.record_indexes)?;
    let mut messages = vec![validated.summary];
    for index in validated.retained_record_indexes {
        if records[index].kind == "message.appended" {
            messages.push(message_from_record(&records[index])?.clone());
        }
    }
    for index in &selected_chain[compaction_position + 1..] {
        if records[*index].kind == "message.appended" {
            messages.push(message_from_record(&records[*index])?.clone());
        }
    }
    Ok(JournalProjection {
        selected_head_record_id: Some(selected_head),
        compaction_record_id: Some(compaction.record_id.clone()),
        messages,
    })
}

/// 功能：验证一次新 compaction 的 retained boundary 位于 source ancestry 且角色为 user。
///
/// 输入：当前 topology、source head 和客户端 boundary ID。
/// 输出：边界可安全保留完整 user 起点时成功。
/// 不变量：assistant/tool/control 或 sibling boundary 均不能孤立工具上下文。
/// 失败：未知、非祖先或非 user 返回冻结 invalid_compaction_boundary。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_compaction_boundary(
    records: &[JournalRecord],
    indexes: &BTreeMap<String, usize>,
    source_record_id: &str,
    retained_record_id: &str,
) -> Result<(), AgentError> {
    let chain = parent_chain_indexes(records, indexes, source_record_id)?;
    let Some(index) = chain
        .into_iter()
        .find(|index| records[*index].record_id == retained_record_id)
    else {
        return Err(invalid_compaction_boundary_error());
    };
    let message = if records[index].kind == "message.appended" {
        message_from_record(&records[index])?
    } else {
        return Err(invalid_compaction_boundary_error());
    };
    if message.get("role").and_then(Value::as_str) != Some("user") {
        return Err(invalid_compaction_boundary_error());
    }
    Ok(())
}

/// 功能：验证 compaction RPC 映射后的 summary、Provider、usage、token 与 strategy 边界。
///
/// 输入：尚未触碰 journal 的 ContextCompactionInput。
/// 输出：全部纯参数满足公共 Schema 时成功。
/// 不变量：token 使用 safe integer 且 tokensAfter 不增长；Provider/usage 不含任意未知字段。
/// 失败：token growth 使用 invalid_compaction_tokens，其余字段使用冻结 invalid_params/boundary。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_compaction_input(input: &ContextCompactionInput) -> Result<(), AgentError> {
    if !id_is_valid(&input.expected_head_record_id) {
        return Err(invalid_params_error("expectedHeadRecordId"));
    }
    if !id_is_valid(&input.first_retained_record_id) {
        return Err(invalid_compaction_boundary_error());
    }
    if input.summary_text.is_empty() || input.summary_text.chars().count() > 262_144 {
        return Err(invalid_params_error("summaryText"));
    }
    if input.strategy.is_empty() || input.strategy.chars().count() > 128 {
        return Err(invalid_params_error("strategy"));
    }
    if input.tokens_before > MAX_SAFE_INTEGER
        || input.tokens_after > MAX_SAFE_INTEGER
        || input.tokens_after > input.tokens_before
    {
        return Err(invalid_compaction_tokens_error());
    }
    if !provider_selection_is_valid(&input.provider) {
        return Err(invalid_params_error("provider"));
    }
    if !usage_is_valid(&input.usage) {
        return Err(invalid_params_error("usage"));
    }
    Ok(())
}

/// 功能：判断 portable Provider selection 值符合受限 Schema。
///
/// 输入：任意 JSON 值。
/// 输出：仅含 id/modelId/可选 extensions 且字符边界有效时为 true。
/// 不变量：本函数不读取环境或 Provider credential。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn provider_selection_is_valid(value: &Value) -> bool {
    let Some(object) = value.as_object() else {
        return false;
    };
    if object
        .keys()
        .any(|key| !matches!(key.as_str(), "id" | "modelId" | "apiFamily" | "extensions"))
        || object
            .get("extensions")
            .is_some_and(|extensions| !extensions.is_object())
    {
        return false;
    }
    let Some(provider_id) = object.get("id").and_then(Value::as_str) else {
        return false;
    };
    let Some(model_id) = object.get("modelId").and_then(Value::as_str) else {
        return false;
    };
    let api_family_valid = object.get("apiFamily").is_none_or(|value| {
        value.as_str().is_some_and(|family| {
            !family.is_empty()
                && family.len() <= 128
                && family.bytes().enumerate().all(|(index, byte)| {
                    byte.is_ascii_lowercase()
                        || byte.is_ascii_digit()
                        || (index > 0 && matches!(byte, b'.' | b'-'))
                })
        })
    });
    !provider_id.is_empty()
        && provider_id.len() <= 128
        && provider_id.bytes().enumerate().all(|(index, byte)| {
            byte.is_ascii_lowercase()
                || byte.is_ascii_digit()
                || (index > 0 && matches!(byte, b'.' | b'-'))
        })
        && !model_id.is_empty()
        && model_id.chars().count() <= 256
        && api_family_valid
}

/// 功能：判断 portable usage 对象具有必需 safe-integer 字段和受限可选字段。
///
/// 输入：任意 JSON 值。
/// 输出：符合 usage Schema 的对象时为 true。
/// 不变量：不会推断缺失 token，也不接受未知对象字段。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn usage_is_valid(value: &Value) -> bool {
    let Some(object) = value.as_object() else {
        return false;
    };
    if object.keys().any(|key| {
        !matches!(
            key.as_str(),
            "inputTokens"
                | "outputTokens"
                | "totalTokens"
                | "cachedInputTokens"
                | "cacheWriteTokens"
                | "reasoningTokens"
                | "cost"
                | "extensions"
        )
    }) || object
        .get("extensions")
        .is_some_and(|extensions| !extensions.is_object())
    {
        return false;
    }
    for field in [
        "inputTokens",
        "outputTokens",
        "totalTokens",
        "cachedInputTokens",
        "cacheWriteTokens",
        "reasoningTokens",
    ] {
        if object.get(field).is_some_and(|value| {
            value
                .as_u64()
                .is_none_or(|number| number > MAX_SAFE_INTEGER)
        }) {
            return false;
        }
    }
    if !["inputTokens", "outputTokens", "totalTokens"]
        .iter()
        .all(|field| object.get(*field).and_then(Value::as_u64).is_some())
    {
        return false;
    }
    object.get("cost").is_none_or(cost_is_valid)
}

/// 功能：判断 optional usage cost 使用三字母币种和有限十进制定点文本。
///
/// 输入：任意 JSON cost 值。
/// 输出：只含 currency/amount 且格式有效时为 true。
/// 不变量：金额保持字符串，不执行浮点转换。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn cost_is_valid(value: &Value) -> bool {
    let Some(object) = value.as_object() else {
        return false;
    };
    if object.len() != 2 || !object.contains_key("currency") || !object.contains_key("amount") {
        return false;
    }
    let currency = object.get("currency").and_then(Value::as_str);
    let amount = object.get("amount").and_then(Value::as_str);
    currency.is_some_and(|currency| {
        currency.len() == 3 && currency.bytes().all(|byte| byte.is_ascii_uppercase())
    }) && amount.is_some_and(valid_decimal_amount)
}

/// 功能：验证 usage cost amount 为最多十二位小数的非负规范十进制。
///
/// 输入：amount 字符串。
/// 输出：符合 `(0|[1-9][0-9]*)(.[0-9]{1,12})?` 时为 true。
/// 不变量：不接受符号、指数、前导零或空小数部分。
/// 失败：本函数不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn valid_decimal_amount(value: &str) -> bool {
    let (integer, fraction) = value
        .split_once('.')
        .map_or((value, None), |(integer, fraction)| {
            (integer, Some(fraction))
        });
    let integer_valid = integer == "0"
        || (!integer.starts_with('0')
            && !integer.is_empty()
            && integer.bytes().all(|byte| byte.is_ascii_digit()));
    integer_valid
        && fraction.is_none_or(|fraction| {
            !fraction.is_empty()
                && fraction.len() <= 12
                && fraction.bytes().all(|byte| byte.is_ascii_digit())
        })
}

/// 功能：构造 expected-head CAS 失败的冻结 conflict 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn stale_session_head_error() -> AgentError {
    AgentError::new(ErrorCode::Conflict, "session selected head changed")
        .retryable(true)
        .with_details(serde_json::json!({"kind":"stale_session_head"}))
}

/// 功能：构造 unknown target 的冻结 invalid-params 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn record_not_found_error(field: &str) -> AgentError {
    AgentError::new(
        ErrorCode::InvalidParams,
        "target journal record was not found",
    )
    .with_details(serde_json::json!({"kind":"record_not_found","field":field}))
}

/// 功能：构造 historical target ancestry 非静止的冻结 conflict 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn branch_not_quiescent_error() -> AgentError {
    AgentError::new(ErrorCode::Conflict, "branch target is not quiescent")
        .with_details(serde_json::json!({"kind":"branch_not_quiescent"}))
}

/// 功能：构造当前 selected Session 生命周期未闭合的冻结 busy 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) fn session_busy_error() -> AgentError {
    AgentError::new(ErrorCode::RunConflict, "session has active lifecycle work")
        .retryable(true)
        .with_details(serde_json::json!({"kind":"session_busy"}))
}

/// 功能：构造 compaction retained boundary 非法的冻结参数错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn invalid_compaction_boundary_error() -> AgentError {
    AgentError::new(
        ErrorCode::InvalidParams,
        "compaction retained boundary is invalid",
    )
    .with_details(serde_json::json!({"kind":"invalid_compaction_boundary"}))
}

/// 功能：构造 compaction token 增长或越界的冻结参数错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn invalid_compaction_tokens_error() -> AgentError {
    AgentError::new(
        ErrorCode::InvalidParams,
        "compaction token estimates are invalid",
    )
    .with_details(serde_json::json!({"kind":"invalid_compaction_tokens"}))
}

/// 功能：构造 compaction 其他纯参数字段非法的统一错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn invalid_params_error(field: &str) -> AgentError {
    AgentError::new(
        ErrorCode::InvalidParams,
        "session mutation parameter is invalid",
    )
    .with_details(serde_json::json!({"kind":"invalid_params","field":field}))
}

/// 功能：读取并结构验证以 LF 结束的完整 header 与全部 journal records。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn read_journal(path: &Path) -> Result<LoadedJournal, AgentError> {
    let bytes = std::fs::read(path).map_err(|error| {
        if error.kind() == std::io::ErrorKind::NotFound {
            AgentError::new(ErrorCode::SessionNotFound, "session journal not found")
        } else {
            error.into()
        }
    })?;
    parse_journal_bytes(&bytes)
}

/// 功能：从完整原始字节严格解析一个必须以 LF 结束的 portable journal。
///
/// 输入：调用方在稳定锁快照内读取的完整 journal 字节。
/// 输出：结构、序号、引用和状态机均已验证的 header 与 records。
/// 不变量：任何非空无 LF final bytes 都不被视为已提交；重复键仍由 strict parser 拒绝。
/// 失败：UTF-8、LF、JSON、Schema、序号、引用或状态机违规返回 JournalCorrupt。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_journal_bytes(bytes: &[u8]) -> Result<LoadedJournal, AgentError> {
    if !bytes.is_empty() && !bytes.ends_with(b"\n") {
        return Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            "journal contains uncommitted final bytes",
        ));
    }
    let text = std::str::from_utf8(bytes).map_err(|error| {
        AgentError::new(
            ErrorCode::JournalCorrupt,
            format!("journal is not valid UTF-8: {error}"),
        )
    })?;
    let mut lines = text.lines();
    let header_line = lines
        .next()
        .ok_or_else(|| AgentError::new(ErrorCode::JournalCorrupt, "journal header is missing"))?;
    let header: SessionHeader = parse_strict_value(header_line)
        .and_then(serde_json::from_value)
        .map_err(|error| {
            AgentError::new(
                ErrorCode::JournalCorrupt,
                format!("invalid journal header: {error}"),
            )
        })?;
    validate_header(&header)?;
    let mut records = Vec::new();
    let mut ids = BTreeSet::new();
    for (index, line) in lines.enumerate() {
        if line.is_empty() {
            return Err(AgentError::new(
                ErrorCode::JournalCorrupt,
                "blank journal line is invalid",
            ));
        }
        let record: JournalRecord = parse_strict_value(line)
            .and_then(serde_json::from_value)
            .map_err(|error| {
                AgentError::new(
                    ErrorCode::JournalCorrupt,
                    format!("invalid journal record at line {}: {error}", index + 2),
                )
            })?;
        validate_record(&header, &record, index as u64 + 1, &ids)?;
        ids.insert(record.record_id.clone());
        records.push(record);
    }
    validate_journal_semantics(&records)?;
    Ok(LoadedJournal { header, records })
}

/// 功能：验证 Session header 固定字段与 ID。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_header(header: &SessionHeader) -> Result<(), AgentError> {
    if header.kind != "session" || header.schema_version != JOURNAL_VERSION {
        return Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            "unsupported or invalid session header",
        ));
    }
    validate_id(&header.session_id, "session")?;
    if header.workspace.is_empty()
        || header.created_by.as_ref().is_some_and(|created_by| {
            created_by.name.is_empty()
                || created_by.version.is_empty()
                || !matches!(
                    created_by.language.as_str(),
                    "dotnet" | "rust" | "go" | "typescript" | "python"
                )
        })
    {
        return Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            "session header workspace or implementation identity is invalid",
        ));
    }
    if let Some(provenance) = &header.provenance {
        let valid_source = matches!(
            provenance.source.as_str(),
            "native" | "pi-session-v3" | "migration"
        );
        let valid_sha = provenance.source_sha256.len() == 64
            && provenance
                .source_sha256
                .bytes()
                .all(|byte| byte.is_ascii_hexdigit() && !byte.is_ascii_uppercase());
        let pi_fields_valid = provenance.source != "pi-session-v3"
            || (provenance
                .source_session_id
                .as_deref()
                .is_some_and(|value| !value.is_empty() && value.len() <= 256)
                && provenance.reference_commit.as_deref()
                    == Some("3f9aa5d10b35223abf6146f960ff5cb5c68053ee")
                && provenance
                    .report_artifact_id
                    .as_deref()
                    .is_some_and(id_is_valid));
        if !valid_source || !valid_sha || !pi_fields_valid {
            return Err(AgentError::new(
                ErrorCode::JournalCorrupt,
                "session provenance is invalid",
            ));
        }
    }
    Ok(())
}

/// 功能：验证单条记录的版本、顺序、归属、唯一性与向后 parent 引用。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_record(
    header: &SessionHeader,
    record: &JournalRecord,
    expected_seq: u64,
    prior_ids: &BTreeSet<String>,
) -> Result<(), AgentError> {
    if record.schema_version != RECORD_SCHEMA_VERSION
        || record.session_id != header.session_id
        || record.seq != expected_seq
        || !record.data.is_object()
    {
        return Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            "journal record version, session, sequence, or data is invalid",
        ));
    }
    validate_record_kind(&record.kind)?;
    validate_id(&record.record_id, "record")?;
    if prior_ids.contains(&record.record_id) {
        return Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            "duplicate recordId",
        ));
    }
    if let Some(parent_id) = &record.parent_id
        && !prior_ids.contains(parent_id)
    {
        return Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            "parentId must reference an earlier record",
        ));
    }
    Ok(())
}

/// 功能：尽力同步目录元数据，使新文件或 rename 在崩溃后可见。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn sync_directory(path: &Path) -> Result<(), AgentError> {
    #[cfg(unix)]
    {
        File::open(path)?.sync_all()?;
    }
    #[cfg(not(unix))]
    {
        let _ = path;
    }
    Ok(())
}

/// 功能：取得 portable writer lease 与 advisory lock 后恢复无 LF 的未提交 journal 尾部。
///
/// 输入：canonical Session 目录与匹配的 opaque Session ID。
/// 输出：完成恢复时为 true，无未提交尾部时为 false。
/// 不变量：固定 coordination、portable lease、advisory lock 和独占 append lock 覆盖重读、备份、截断及诊断追加。
/// 失败：live writer、完整行损坏、备份冲突或 I/O 失败时 fail closed。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn recover_tail_locked(directory: &Path, session_id: &str) -> Result<bool, AgentError> {
    let sessions_root = directory.parent().ok_or_else(|| {
        AgentError::new(
            ErrorCode::InternalError,
            "session directory has no sessions root",
        )
    })?;
    let _coordination = acquire_session_coordination(sessions_root, session_id)?;
    reject_pending_tombstone(sessions_root, session_id)?;
    let mut portable = PortableWriterLease::acquire(directory, session_id)?;
    let lock = open_writer_lock(directory)?;
    if let Err(error) = lock.try_lock_exclusive() {
        let _ = portable.release_without_advisory();
        return Err(writer_conflict_error(&error));
    }
    let result = recover_tail_with_held_writer(directory, session_id);
    let release = release_writer_locks(&mut portable, &lock);
    result.and_then(|value| release.map(|()| value))
}

/// 功能：在调用方已经持有 portable writer lease 与 advisory lock 时恢复未提交尾部。
///
/// 输入：canonical Session 目录与匹配的 opaque Session ID。
/// 输出：完成恢复时为 true，无尾部时为 false。
/// 不变量：本函数只补取独占 append lock，不重复获取 writer lease，避免同 owner 双锁死锁。
/// 失败：完整行损坏、备份冲突或 I/O 失败时 journal 在首次截断前保持原样。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn recover_tail_with_held_writer(directory: &Path, session_id: &str) -> Result<bool, AgentError> {
    let append_lock = open_append_lock(directory)?;
    append_lock.lock_exclusive()?;
    let result = recover_tail_unlocked(directory, session_id);
    let unlock = FileExt::unlock(&append_lock);
    result.and_then(|recovered| unlock.map(|()| recovered).map_err(AgentError::from))
}

/// 功能：在全部 writer 锁已持有时备份、截断并记录一个未提交 journal 尾部。
///
/// 输入：固定 Session 目录和与 header 匹配的 Session ID。
/// 输出：完成恢复时为 true，完整且合法的 LF journal 为 false。
/// 不变量：先完整验证 committed prefix 和候选诊断，再逐字节 durable 备份；任何非空无 LF 尾部均丢弃。
/// 失败：prefix/身份/序号、备份碰撞或 I/O 失败；备份发布前 journal 零修改。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn recover_tail_unlocked(directory: &Path, session_id: &str) -> Result<bool, AgentError> {
    let path = directory.join("journal.jsonl");
    let mut file = OpenOptions::new().read(true).write(true).open(&path)?;
    let mut bytes = Vec::new();
    file.read_to_end(&mut bytes)?;
    if bytes.is_empty() || bytes.ends_with(b"\n") {
        let journal = parse_journal_bytes(&bytes)?;
        if journal.header.session_id != session_id {
            return Err(AgentError::new(
                ErrorCode::JournalCorrupt,
                "session header id does not match requested id",
            ));
        }
        return Ok(false);
    }

    let valid_end = bytes
        .iter()
        .rposition(|byte| *byte == b'\n')
        .map_or(0, |position| position + 1);
    let journal = parse_journal_bytes(&bytes[..valid_end])?;
    if journal.header.session_id != session_id {
        return Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            "session header id does not match requested id",
        ));
    }
    let discarded_bytes = u64::try_from(bytes.len() - valid_end).map_err(|_| {
        AgentError::new(
            ErrorCode::JournalCorrupt,
            "uncommitted journal tail length is invalid",
        )
    })?;
    if discarded_bytes == 0 || discarded_bytes > MAX_SAFE_INTEGER {
        return Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            "uncommitted journal tail exceeds the portable size range",
        ));
    }
    let original_sha256 = format!("{:x}", Sha256::digest(&bytes));
    let backup_file = format!("journal.recovery-{original_sha256}.bak");
    let record = JournalRecord::new(
        session_id,
        next_record_sequence(&journal.records)?,
        journal.records.last().map(|last| last.record_id.clone()),
        "extension",
        serde_json::json!({
            "namespace":SESSION_RECOVERY_NAMESPACE,
            "value":{
                "action":"truncate_uncommitted_tail",
                "discardedBytes":discarded_bytes,
                "backupFile":backup_file,
                "originalSha256":original_sha256
            }
        }),
    );
    let mut candidate = journal.records.clone();
    candidate.push(record.clone());
    validate_journal_semantics(&candidate)?;
    let mut encoded_record = serde_json::to_vec(&record)
        .map_err(|error| AgentError::new(ErrorCode::IoError, error.to_string()))?;
    encoded_record.push(b'\n');

    publish_recovery_backup(directory, &backup_file, &bytes)?;
    file.set_len(valid_end as u64)?;
    file.seek(SeekFrom::End(0))?;
    file.sync_all()?;
    sync_directory(directory)?;
    file.write_all(&encoded_record)?;
    file.sync_all()?;
    sync_directory(directory)?;
    Ok(true)
}

/// 功能：把修复前完整 journal 原子、无覆盖发布为确定性 recovery backup。
///
/// 输入：canonical Session 目录、安全 basename 与修复前完整原始字节。
/// 输出：目标 backup durable 且逐字节等于输入时成功；既有同内容文件可复用。
/// 不变量：临时文件先完整 flush，再以同目录 hard link 无覆盖发布；绝不跟随既有目标 symlink。
/// 失败：既有目标非普通文件或内容不同返回 JournalCorrupt，发布/同步失败返回 I/O 错误，journal 由调用方保持未修改。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn publish_recovery_backup(
    directory: &Path,
    backup_file: &str,
    bytes: &[u8],
) -> Result<(), AgentError> {
    if Path::new(backup_file)
        .file_name()
        .and_then(|name| name.to_str())
        != Some(backup_file)
    {
        return Err(AgentError::new(
            ErrorCode::JournalCorrupt,
            "recovery backup name is unsafe",
        ));
    }
    let target = directory.join(backup_file);
    match std::fs::symlink_metadata(&target) {
        Ok(_) => return verify_existing_recovery_backup(&target, bytes),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(error) => return Err(error.into()),
    }

    let temporary = directory.join(format!(".{backup_file}.{}.tmp", Uuid::new_v4()));
    let mut temporary_file = OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(&temporary)?;
    let result = (|| {
        temporary_file.write_all(bytes)?;
        temporary_file.sync_all()?;
        match std::fs::hard_link(&temporary, &target) {
            Ok(()) => {
                sync_directory(directory)?;
                Ok(())
            }
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => {
                verify_existing_recovery_backup(&target, bytes)
            }
            Err(error) => Err(error.into()),
        }
    })();
    drop(temporary_file);
    let cleanup = std::fs::remove_file(&temporary).map_err(AgentError::from);
    let synced_cleanup = cleanup.and_then(|()| sync_directory(directory));
    result.and(synced_cleanup)
}

/// 功能：以 no-follow 语义验证既有 recovery backup 与期望 journal 逐字节相同。
///
/// 输入：由安全 basename 派生的 backup 路径及完整期望字节。
/// 输出：目标为同长度普通文件且内容完全相同时成功。
/// 不变量：symlink、目录、设备和摘要碰撞式替代内容均不被复用。
/// 失败：任何形状、读取或内容差异统一返回 JournalCorrupt，确保 journal 零修改。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn verify_existing_recovery_backup(path: &Path, expected: &[u8]) -> Result<(), AgentError> {
    let corrupt = || {
        AgentError::new(
            ErrorCode::JournalCorrupt,
            "existing recovery backup does not match the original journal",
        )
    };
    let metadata = std::fs::symlink_metadata(path).map_err(|_| corrupt())?;
    if !metadata.file_type().is_file()
        || metadata.file_type().is_symlink()
        || metadata.len() != expected.len() as u64
    {
        return Err(corrupt());
    }
    let mut file = open_recovery_backup_no_follow(path).map_err(|_| corrupt())?;
    if !file.metadata().map_err(|_| corrupt())?.is_file() {
        return Err(corrupt());
    }
    let mut actual = Vec::with_capacity(expected.len());
    file.read_to_end(&mut actual).map_err(|_| corrupt())?;
    if actual == expected {
        Ok(())
    } else {
        Err(corrupt())
    }
}

/// 功能：在 Unix 使用 O_NOFOLLOW 只读打开 recovery backup。
///
/// 输入：由内部安全 basename 派生的最终 backup 路径。
/// 输出：不跟随最终 symlink 的只读文件句柄。
/// 不变量：仅用于随后复核 regular-file、长度和完整内容。
/// 失败：内核拒绝或读取权限不足时返回原始 I/O 错误，由调用方收敛为 JournalCorrupt。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[cfg(unix)]
fn open_recovery_backup_no_follow(path: &Path) -> std::io::Result<File> {
    use std::os::unix::fs::OpenOptionsExt;

    OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_NOFOLLOW)
        .open(path)
}

/// 功能：在无 O_NOFOLLOW 标准扩展的平台只读打开已预检的 recovery backup。
///
/// 输入：由内部安全 basename 派生且已经过 symlink metadata 预检的路径。
/// 输出：供打开后 regular-file、长度和完整内容复核的只读句柄。
/// 不变量：本分支不宣称消除平台全部路径竞态；任何差异均 fail closed。
/// 失败：打开失败返回原始 I/O 错误，由调用方收敛为 JournalCorrupt。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[cfg(not(unix))]
fn open_recovery_backup_no_follow(path: &Path) -> std::io::Result<File> {
    OpenOptions::new().read(true).open(path)
}

#[cfg(test)]
mod tests {
    use std::fs;
    use std::fs::OpenOptions;
    use std::io::Write;
    use std::path::{Path, PathBuf};
    use std::sync::mpsc;
    use std::time::Duration;

    use chrono::Utc;
    use fs2::FileExt;
    use serde_json::json;
    use sha2::{Digest, Sha256};
    use tempfile::tempdir;
    use tokio_util::sync::CancellationToken;

    use super::{
        ContextCompactionInput, JournalRecord, SessionStore, append_records_unlocked_with_writer,
        open_append_lock, publish_artifact_no_replace, store_computer_screenshot_artifact_sync,
        store_image_completion_sync,
    };
    use crate::domain::ProviderImage;
    use crate::error::ErrorCode;

    /// 功能：把只读静态 JSONL fixture 安装到临时 SessionStore 目录而不改写内容。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn install_fixture(root: &Path, session_id: &str, content: &str) -> Result<(), std::io::Error> {
        let directory = root.join("sessions").join(session_id);
        fs::create_dir_all(directory.join("artifacts"))?;
        fs::write(directory.join("journal.jsonl"), content)
    }

    /// 功能：为 branch/compaction 单测追加一个 canonical user message。
    ///
    /// 输入：SessionStore、Session/message ID 和文本。
    /// 输出：已经 durable 的 message.appended record。
    /// 不变量：消息角色固定 user，内容恰好一个 text block。
    /// 失败：Session I/O 或 journal 状态违规时返回 AgentError。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn append_test_user(
        store: &SessionStore,
        session_id: &str,
        message_id: &str,
        text: &str,
    ) -> Result<JournalRecord, crate::error::AgentError> {
        store
            .append_record(
                session_id,
                "message.appended",
                json!({
                    "message":{
                        "messageId":message_id,
                        "role":"user",
                        "content":[{"type":"text","text":text}],
                        "time":chrono::Utc::now()
                    }
                }),
            )
            .await
    }

    /// 功能：为 branch/compaction 单测追加一个 canonical faux assistant message。
    ///
    /// 输入：SessionStore、Session/message ID 和文本。
    /// 输出：已经 durable 的 message.appended record。
    /// 不变量：角色 assistant、finishReason stop 且 usage 为零。
    /// 失败：Session I/O 或 journal 状态违规时返回 AgentError。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn append_test_assistant(
        store: &SessionStore,
        session_id: &str,
        message_id: &str,
        text: &str,
    ) -> Result<JournalRecord, crate::error::AgentError> {
        store
            .append_record(
                session_id,
                "message.appended",
                json!({
                    "message":{
                        "messageId":message_id,
                        "role":"assistant",
                        "content":[{"type":"text","text":text}],
                        "provider":{"id":"faux","modelId":"faux-v1"},
                        "finishReason":"stop",
                        "usage":{"inputTokens":0,"outputTokens":0,"totalTokens":0},
                        "time":chrono::Utc::now()
                    }
                }),
            )
            .await
    }

    /// 功能：构造 Rust SessionStore 单测使用的合法 compaction 输入。
    ///
    /// 输入：expected head、retained boundary、summary 文本和 token 估算。
    /// 输出：Provider/usage/strategy 固定且可直接提交的 ContextCompactionInput。
    /// 不变量：不读取环境或调用 Provider。
    /// 失败：本函数不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn test_compaction_input(
        expected_head_record_id: &str,
        first_retained_record_id: &str,
        summary_text: &str,
        tokens_before: u64,
        tokens_after: u64,
    ) -> ContextCompactionInput {
        ContextCompactionInput {
            expected_head_record_id: expected_head_record_id.to_owned(),
            first_retained_record_id: first_retained_record_id.to_owned(),
            summary_text: summary_text.to_owned(),
            provider: json!({"id":"faux","modelId":"faux-v1"}),
            usage: json!({"inputTokens":3,"outputTokens":2,"totalTokens":5}),
            tokens_before,
            tokens_after,
            strategy: "unit-summary-v1".to_owned(),
        }
    }

    /// 功能：列出一个测试 Session 目录内符合冻结命名规则的 recovery backup。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn recovery_backup_paths(directory: &Path) -> Result<Vec<PathBuf>, crate::error::AgentError> {
        let mut paths = fs::read_dir(directory)?
            .filter_map(Result::ok)
            .filter_map(|entry| {
                let name = entry.file_name();
                let name = name.to_str()?;
                (name.starts_with("journal.recovery-") && name.ends_with(".bak"))
                    .then(|| entry.path())
            })
            .collect::<Vec<_>>();
        paths.sort();
        Ok(paths)
    }

    /// 功能：验证 Schema header、自动序号、parentId 与加载闭环。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn appends_and_loads_records() -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 1024).await?;
        store.create_session("s1", directory.path()).await?;
        let first = store
            .append_record("s1", "run.started", json!({"runId":"r1"}))
            .await?;
        let second = store
            .append_record(
                "s1",
                "run.terminal",
                json!({"runId":"r1","status":"completed","usage":{"inputTokens":0,"outputTokens":0,"totalTokens":0}}),
            )
            .await?;
        assert_eq!(first.seq, 1);
        assert_eq!(second.parent_id.as_deref(), Some(first.record_id.as_str()));
        assert_eq!(store.load("s1").await?.len(), 2);
        Ok(())
    }

    /// 功能：验证显式追加拒绝断裂序号。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn rejects_non_contiguous_append() -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 1024).await?;
        store.create_session("s1", directory.path()).await?;
        let error = store
            .append(JournalRecord::new(
                "s1",
                2,
                None,
                "run.started",
                json!({"runId":"r1"}),
            ))
            .await
            .expect_err("sequence gap must fail");
        assert_eq!(error.code, ErrorCode::JournalCorrupt);
        Ok(())
    }

    /// 功能：验证普通 load 自动备份损坏无 LF 尾部，并以冻结诊断幂等继续。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn load_repairs_unterminated_tail_and_is_idempotent()
    -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 1024).await?;
        store.create_session("s1", directory.path()).await?;
        let path = directory.path().join("sessions/s1/journal.jsonl");
        let session_directory = path.parent().expect("journal must have a parent");
        OpenOptions::new()
            .append(true)
            .open(&path)?
            .write_all(b"{broken")?;
        let original = fs::read(&path)?;
        let original_sha256 = format!("{:x}", Sha256::digest(&original));
        let backup_file = format!("journal.recovery-{original_sha256}.bak");

        let records = store.load("s1").await?;
        assert_eq!(records.len(), 1);
        assert_eq!(records[0].kind, "extension");
        assert_eq!(records[0].seq, 1);
        assert_eq!(records[0].parent_id, None);
        assert_eq!(
            records[0].data,
            json!({
                "namespace":"org.agent-session.recovery",
                "value":{
                    "action":"truncate_uncommitted_tail",
                    "discardedBytes":7,
                    "backupFile":backup_file,
                    "originalSha256":original_sha256
                }
            })
        );
        assert_eq!(fs::read(session_directory.join(&backup_file))?, original);
        assert_eq!(recovery_backup_paths(session_directory)?.len(), 1);

        let recovered = fs::read(&path)?;
        assert_eq!(store.load("s1").await?.len(), 1);
        assert!(!store.recover_corrupt_tail("s1").await?);
        assert_eq!(fs::read(&path)?, recovered);
        assert_eq!(recovery_backup_paths(session_directory)?.len(), 1);
        Ok(())
    }

    /// 功能：验证 writer open 会丢弃合法 JSON 但无 LF 的记录，并安全复用完全相同的固定 backup。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn writer_open_discards_valid_unterminated_record_and_reuses_backup()
    -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 1024).await?;
        store.create_session("s1", directory.path()).await?;
        let path = directory.path().join("sessions/s1/journal.jsonl");
        let session_directory = path.parent().expect("journal must have a parent");
        let uncommitted = JournalRecord::new(
            "s1",
            1,
            None,
            "run.started",
            json!({"runId":"uncommitted-run"}),
        );
        let encoded = serde_json::to_vec(&uncommitted)?;
        OpenOptions::new()
            .append(true)
            .open(&path)?
            .write_all(&encoded)?;
        let original = fs::read(&path)?;
        let original_sha256 = format!("{:x}", Sha256::digest(&original));
        let backup_file = format!("journal.recovery-{original_sha256}.bak");
        fs::write(session_directory.join(&backup_file), &original)?;

        let lease = store.acquire_writer("s1").await?;
        let records = store.load("s1").await?;
        assert_eq!(records.len(), 1);
        assert_eq!(records[0].kind, "extension");
        assert_eq!(records[0].data["namespace"], "org.agent-session.recovery");
        assert_eq!(
            records[0].data["value"],
            json!({
                "action":"truncate_uncommitted_tail",
                "discardedBytes":encoded.len(),
                "backupFile":backup_file,
                "originalSha256":original_sha256
            })
        );
        drop(lease);
        assert_eq!(fs::read(session_directory.join(&backup_file))?, original);
        assert_eq!(recovery_backup_paths(session_directory)?.len(), 1);
        Ok(())
    }

    /// 功能：验证完整损坏记录不能被 tail recovery 静默丢弃。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn rejects_corrupt_complete_record() -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 1024).await?;
        store.create_session("s1", directory.path()).await?;
        let path = directory.path().join("sessions/s1/journal.jsonl");
        OpenOptions::new()
            .append(true)
            .open(&path)?
            .write_all(b"{broken}\n")?;
        let original = fs::read(&path)?;
        let error = store
            .load("s1")
            .await
            .expect_err("journal must be rejected");
        assert_eq!(error.code, ErrorCode::JournalCorrupt);
        assert_eq!(fs::read(&path)?, original);
        assert!(recovery_backup_paths(path.parent().expect("journal parent"))?.is_empty());
        Ok(())
    }

    /// 功能：验证 Session 加载会递归拒绝 journal 对象中的重复 JSON 键。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn rejects_duplicate_keys_in_journal() -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 1024).await?;
        store.create_session("s1", directory.path()).await?;
        let path = directory.path().join("sessions/s1/journal.jsonl");
        let record = JournalRecord::new("s1", 1, None, "run.started", json!({"runId":"r1"}));
        let serialized = serde_json::to_string(&record)?;
        let duplicated = serialized.replacen(
            "\"kind\":\"run.started\"",
            "\"kind\":\"run.started\",\"kind\":\"run.started\"",
            1,
        );
        OpenOptions::new()
            .append(true)
            .open(&path)?
            .write_all(format!("{duplicated}\n").as_bytes())?;
        let original = fs::read(&path)?;
        let error = store
            .load("s1")
            .await
            .expect_err("duplicate journal keys must be rejected");
        assert_eq!(error.code, ErrorCode::JournalCorrupt);
        assert_eq!(fs::read(&path)?, original);
        assert!(recovery_backup_paths(path.parent().expect("journal parent"))?.is_empty());
        Ok(())
    }

    /// 功能：验证固定 backup basename 已被不同字节占用时 journal 零修改并返回 JournalCorrupt。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn rejects_conflicting_recovery_backup_without_modifying_journal()
    -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 1024).await?;
        store.create_session("s1", directory.path()).await?;
        let path = directory.path().join("sessions/s1/journal.jsonl");
        let session_directory = path.parent().expect("journal must have a parent");
        OpenOptions::new()
            .append(true)
            .open(&path)?
            .write_all(b"{broken")?;
        let original = fs::read(&path)?;
        let original_sha256 = format!("{:x}", Sha256::digest(&original));
        let backup_file = format!("journal.recovery-{original_sha256}.bak");
        fs::write(session_directory.join(&backup_file), b"different")?;

        let error = store
            .load("s1")
            .await
            .expect_err("conflicting recovery backup must fail closed");
        assert_eq!(error.code, ErrorCode::JournalCorrupt);
        assert_eq!(fs::read(&path)?, original);
        assert_eq!(
            fs::read(session_directory.join(&backup_file))?,
            b"different"
        );
        assert_eq!(recovery_backup_paths(session_directory)?.len(), 1);
        Ok(())
    }

    /// 功能：验证 artifact 存入 Session 目录并产生必需 SHA-256 引用与 journal 记录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn stores_content_addressed_artifact() -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 4).await?;
        store.create_session("s1", directory.path()).await?;
        let artifact = store.store_artifact("s1", b"hello", "text/plain").await?;
        assert!(artifact.artifact_id.starts_with("artifact-"));
        assert_eq!(artifact.byte_length, 5);
        assert_eq!(json!(artifact)["mediaType"], "text/plain");
        assert_eq!(store.load("s1").await?.len(), 1);
        Ok(())
    }

    /// 功能：验证图像 artifact 使用唯一 ID，且输入读取复核 durable length/hash/media 与文件内容。
    ///
    /// 输入：临时 Session 中两次相同 PNG 输出和多种篡改 reference/file。
    /// 输出：合法引用可读、重复图像 ID 不复用，所有篡改均拒绝。
    /// 不变量：测试只使用本地 synthetic PNG，不访问 Provider 或输出 base64/data URL。
    /// 失败：Session 或断言失败传播测试错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn verifies_image_artifact_reference_and_bytes() -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 4).await?;
        store.create_session("s-image", directory.path()).await?;
        let png = b"\x89PNG\r\n\x1a\nfixture";
        let artifact = store
            .store_image_artifact("s-image", png, "image/png")
            .await?;
        let second = store
            .store_image_artifact("s-image", png, "image/png")
            .await?;
        assert_ne!(artifact.artifact_id, second.artifact_id);
        assert_eq!(
            store
                .read_verified_image_artifact("s-image", &artifact, 1024)
                .await?,
            png
        );

        let mut wrong_length = artifact.clone();
        wrong_length.byte_length += 1;
        assert!(
            store
                .read_verified_image_artifact("s-image", &wrong_length, 1024)
                .await
                .is_err()
        );
        let mut wrong_hash = artifact.clone();
        wrong_hash.sha256 = "0".repeat(64);
        assert!(
            store
                .read_verified_image_artifact("s-image", &wrong_hash, 1024)
                .await
                .is_err()
        );
        let mut wrong_media = artifact.clone();
        wrong_media.media_type = "image/jpeg".to_owned();
        assert!(
            store
                .read_verified_image_artifact("s-image", &wrong_media, 1024)
                .await
                .is_err()
        );

        let artifact_path = directory
            .path()
            .join("sessions/s-image/artifacts")
            .join(&artifact.artifact_id);
        fs::write(&artifact_path, b"\x89PNG\r\n\x1a\nchanged")?;
        assert!(
            store
                .read_verified_image_artifact("s-image", &artifact, 1024)
                .await
                .is_err()
        );
        Ok(())
    }

    /// 功能：验证图片批次会先全量校验，合法批次再按顺序一次发布全部 durable 引用。
    ///
    /// 不变量：第二张无效时第一张也不会留下文件或 `artifact.created`；测试只使用 synthetic 图片。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn image_artifact_batch_prevalidates_before_publication()
    -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 4).await?;
        let session_id = "s-image-batch";
        store.create_session(session_id, directory.path()).await?;
        let valid = ProviderImage {
            media_type: "image/png".to_owned(),
            bytes: b"\x89PNG\r\n\x1a\nfirst".to_vec(),
        };
        let invalid = ProviderImage {
            media_type: "image/jpeg".to_owned(),
            bytes: b"\x89PNG\r\n\x1a\nwrong-mime".to_vec(),
        };
        assert!(
            store
                .store_image_completion(
                    session_id,
                    &[valid.clone(), invalid],
                    |_| serde_json::json!({"message":{}}),
                )
                .await
                .is_err()
        );
        let artifacts_directory = directory
            .path()
            .join("sessions")
            .join(session_id)
            .join("artifacts");
        assert_eq!(fs::read_dir(&artifacts_directory)?.count(), 0);
        assert!(store.load(session_id).await?.is_empty());

        let second = ProviderImage {
            media_type: "image/png".to_owned(),
            bytes: b"\x89PNG\r\n\x1a\nsecond".to_vec(),
        };
        let artifacts = store
            .store_image_completion(session_id, &[valid, second], |artifacts| {
                serde_json::json!({
                    "runId":"run-image-batch",
                    "turnId":"turn-image-batch",
                    "message":{
                        "messageId":"message-image-batch",
                        "role":"assistant",
                        "content":artifacts.iter().map(|artifact| {
                            serde_json::json!({"type":"image_ref","artifact":artifact})
                        }).collect::<Vec<_>>(),
                        "provider":{"id":"faux","modelId":"faux-image"},
                        "finishReason":"stop",
                        "usage":{},
                        "time":Utc::now()
                    }
                })
            })
            .await?;
        assert_eq!(artifacts.len(), 2);
        assert_ne!(artifacts[0].artifact_id, artifacts[1].artifact_id);
        assert_eq!(fs::read_dir(artifacts_directory)?.count(), 2);
        assert_eq!(
            store
                .load(session_id)
                .await?
                .iter()
                .filter(|record| record.kind == "artifact.created")
                .count(),
            2
        );
        assert_eq!(
            store
                .load(session_id)
                .await?
                .iter()
                .filter(|record| record.kind == "message.appended")
                .count(),
            1
        );
        Ok(())
    }

    /// 功能：确定性注入 journal 部分写入失败，验证 durable 回滚后清理图片且不留下孤立 assistant。
    ///
    /// 不变量：synthetic writer 只写入预编码批次前缀再报错；生产回滚逻辑确认原长度后，文件、`artifact.created` 与 assistant 消息均为零。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn image_artifact_batch_cleans_files_after_confirmed_append_failure()
    -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 4).await?;
        let session_id = "s-image-batch-append-failure";
        store.create_session(session_id, directory.path()).await?;
        let session_directory = directory.path().join("sessions").join(session_id);
        let images = [ProviderImage {
            media_type: "image/png".to_owned(),
            bytes: b"\x89PNG\r\n\x1a\nappend-failure".to_vec(),
        }];
        let result = store_image_completion_sync(
            &session_directory,
            session_id,
            &images,
            |artifacts| {
                serde_json::json!({
                    "runId":"run-image-failure",
                    "turnId":"turn-image-failure",
                    "message":{
                        "messageId":"message-image-failure",
                        "role":"assistant",
                        "content":artifacts.iter().map(|artifact| {
                            serde_json::json!({"type":"image_ref","artifact":artifact})
                        }).collect::<Vec<_>>(),
                        "provider":{"id":"faux","modelId":"faux-image"},
                        "finishReason":"stop",
                        "usage":{},
                        "time":Utc::now()
                    }
                })
            },
            |path, records| {
                append_records_unlocked_with_writer(path, records, |file, encoded| {
                    let prefix_length = (encoded.len() / 2).max(1);
                    std::io::Write::write_all(file, &encoded[..prefix_length])?;
                    Err(std::io::Error::other(
                        "synthetic image completion batch failure",
                    ))
                })
            },
        );

        assert!(result.is_err());
        assert_eq!(
            fs::read_dir(session_directory.join("artifacts"))?.count(),
            0
        );
        assert!(store.load(session_id).await?.is_empty());
        Ok(())
    }

    /// 功能：验证 desktop screenshot 只绑定唯一 active run，并在引用与 journal data 写入相同敏感扩展。
    ///
    /// 输入：本地 synthetic PNG、active/terminal run 生命周期和被剥离扩展的引用。
    /// 输出：active 时成功，扩展严格四字段且 reader 强绑定；无 active 或已 terminal 时零发布并拒绝。
    /// 不变量：fixture 不包含真实桌面像素、backend、display ID、主机路径或 Provider 调用。
    /// 失败：run 竞态、扩展、journal、文件发布或 durable reference 复核漂移时测试失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn computer_screenshot_artifact_is_active_run_bound_and_sensitive()
    -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 4).await?;
        store.create_session("s-computer", directory.path()).await?;
        let png = b"\x89PNG\r\n\x1a\nsynthetic-computer";
        let cancellation = CancellationToken::new();
        let artifacts_directory = directory.path().join("sessions/s-computer/artifacts");
        let inactive = store
            .store_computer_screenshot_artifact("s-computer", "run-computer", png, &cancellation)
            .await
            .expect_err("screenshot without an active run must fail");
        assert_eq!(inactive.code, ErrorCode::Cancelled);
        assert_eq!(fs::read_dir(&artifacts_directory)?.count(), 0);

        store
            .append_record(
                "s-computer",
                "run.accepted",
                json!({
                    "runId":"run-computer","inputMessageId":"message-computer",
                    "provider":{"id":"faux","modelId":"faux-v1"}
                }),
            )
            .await?;
        let artifact = store
            .store_computer_screenshot_artifact("s-computer", "run-computer", png, &cancellation)
            .await?;
        let expected_extension = json!({
            "source":"desktop_capture",
            "runId":"run-computer",
            "sensitivity":"desktop_sensitive",
            "retention":"session_lifecycle"
        });
        assert_eq!(artifact.media_type, "image/png");
        assert_eq!(artifact.extensions.len(), 1);
        assert_eq!(
            artifact.extensions["org.agentprotocol.computer"],
            expected_extension
        );
        let records = store.load("s-computer").await?;
        let created = records
            .iter()
            .find(|record| record.kind == "artifact.created")
            .expect("artifact.created must be durable");
        assert_eq!(
            created.data["extensions"]["org.agentprotocol.computer"],
            expected_extension
        );
        assert_eq!(created.data["artifact"], json!(artifact));
        assert_eq!(
            store
                .read_verified_image_artifact("s-computer", &artifact, 1024)
                .await?,
            png
        );
        let mut stripped = artifact.clone();
        stripped.extensions.clear();
        assert!(
            store
                .read_verified_image_artifact("s-computer", &stripped, 1024)
                .await
                .is_err()
        );
        let journal =
            fs::read_to_string(directory.path().join("sessions/s-computer/journal.jsonl"))?;
        assert!(!journal.contains("displayId"));
        assert!(!journal.contains("backend"));
        assert!(!journal.contains("synthetic-computer"));

        store
            .append_record(
                "s-computer",
                "run.cancellation_requested",
                json!({"runId":"run-computer"}),
            )
            .await?;
        let durable_cancel = store
            .store_computer_screenshot_artifact("s-computer", "run-computer", png, &cancellation)
            .await
            .expect_err("durable cancellation must prevent screenshot publication");
        assert_eq!(durable_cancel.code, ErrorCode::Cancelled);
        assert_eq!(fs::read_dir(&artifacts_directory)?.count(), 1);
        assert_eq!(
            store
                .load("s-computer")
                .await?
                .iter()
                .filter(|record| record.kind == "artifact.created")
                .count(),
            1
        );

        store
            .append_record(
                "s-computer",
                "run.terminal",
                json!({"runId":"run-computer","status":"cancelled"}),
            )
            .await?;
        assert!(
            store
                .store_computer_screenshot_artifact(
                    "s-computer",
                    "run-computer",
                    png,
                    &cancellation,
                )
                .await
                .is_err()
        );
        assert_eq!(fs::read_dir(&artifacts_directory)?.count(), 1);
        Ok(())
    }

    /// 功能：验证截图 worker 在 append lock 竞争期间收到取消后不会发布文件或记录。
    ///
    /// 输入：已持有的真实 append lock、active synthetic run、PNG fixture 与共享取消令牌。
    /// 输出：worker 确认进入锁竞争后取消并返回 Cancelled，artifact 目录和 artifact.created 均保持为空。
    /// 不变量：测试以回调确认竞争而不依赖 sleep 猜测；主线程在 worker 返回前始终持锁。
    /// 失败：锁竞争信号超时、worker panic、取消未生效或任何 artifact 被发布时测试失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn computer_screenshot_cancellation_while_waiting_for_append_lock_publishes_nothing()
    -> Result<(), Box<dyn std::error::Error>> {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 4).await?;
        store
            .create_session("s-computer-lock-cancel", directory.path())
            .await?;
        store
            .append_record(
                "s-computer-lock-cancel",
                "run.accepted",
                json!({
                    "runId":"run-computer-lock-cancel",
                    "inputMessageId":"message-computer-lock-cancel",
                    "provider":{"id":"faux","modelId":"faux-v1"}
                }),
            )
            .await?;

        let session_directory = directory.path().join("sessions/s-computer-lock-cancel");
        let append_lock = open_append_lock(&session_directory)?;
        append_lock.lock_exclusive()?;
        let cancellation = CancellationToken::new();
        let worker_cancellation = cancellation.clone();
        let worker_directory = session_directory.clone();
        let (contention_sender, contention_receiver) = mpsc::channel();
        let worker = std::thread::spawn(move || {
            let mut contention_sender = Some(contention_sender);
            store_computer_screenshot_artifact_sync(
                &worker_directory,
                "s-computer-lock-cancel",
                "run-computer-lock-cancel",
                b"\x89PNG\r\n\x1a\nsynthetic-lock-cancel",
                &worker_cancellation,
                || {
                    if let Some(sender) = contention_sender.take() {
                        let _ = sender.send(());
                    }
                },
            )
        });

        contention_receiver.recv_timeout(Duration::from_secs(2))?;
        cancellation.cancel();
        let result = worker.join().expect("screenshot worker must not panic");
        assert_eq!(
            result.expect_err("cancelled worker must fail").code,
            ErrorCode::Cancelled
        );
        FileExt::unlock(&append_lock)?;

        assert_eq!(
            fs::read_dir(session_directory.join("artifacts"))?.count(),
            0
        );
        assert_eq!(
            store
                .load("s-computer-lock-cancel")
                .await?
                .iter()
                .filter(|record| record.kind == "artifact.created")
                .count(),
            0
        );
        Ok(())
    }

    /// 功能：验证通用 artifact 发布在 Unix 上不受宽松 umask 影响并保持 owner-only 权限。
    ///
    /// 输入：临时 Session 与本地 synthetic 普通 artifact。
    /// 输出：最终 artifact inode 的权限位精确为 `0600`。
    /// 不变量：通过通用 store_artifact 路径覆盖 screenshot/image 共用的发布原语；不修改进程 umask。
    /// 失败：Session I/O、metadata 读取或权限位不安全时测试失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(unix)]
    #[tokio::test]
    async fn published_artifact_permissions_are_owner_only()
    -> Result<(), Box<dyn std::error::Error>> {
        use std::os::unix::fs::PermissionsExt;

        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 4).await?;
        store
            .create_session("s-artifact-permissions", directory.path())
            .await?;
        let artifact = store
            .store_artifact(
                "s-artifact-permissions",
                b"synthetic-sensitive-artifact",
                "application/octet-stream",
            )
            .await?;
        let metadata = fs::metadata(
            directory
                .path()
                .join("sessions/s-artifact-permissions/artifacts")
                .join(artifact.artifact_id),
        )?;
        assert_eq!(metadata.permissions().mode() & 0o777, 0o600);
        Ok(())
    }

    /// 功能：验证无覆盖发布拒绝既有 symlink，且不会修改 symlink 指向的外部文件。
    ///
    /// 输入：临时 artifacts 目录、既有目标 symlink 和 synthetic bytes。
    /// 输出：发布失败且外部文件内容保持不变。
    /// 不变量：测试仅在 Unix 使用内核 symlink/O_NOFOLLOW 语义，不删除工作区文件。
    /// 失败：临时 I/O、发布意外成功或外部内容变化使测试失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(unix)]
    #[test]
    fn artifact_publish_never_overwrites_symlink() -> Result<(), Box<dyn std::error::Error>> {
        use std::os::unix::fs::symlink;

        let directory = tempdir()?;
        let artifacts = directory.path().join("artifacts");
        fs::create_dir(&artifacts)?;
        let outside = directory.path().join("outside.bin");
        fs::write(&outside, b"outside")?;
        symlink(&outside, artifacts.join("artifact-fixed"))?;
        assert!(publish_artifact_no_replace(&artifacts, "artifact-fixed", b"replacement").is_err());
        assert_eq!(fs::read(outside)?, b"outside");
        Ok(())
    }

    /// 功能：验证输入 image_ref 的最终 artifact 路径采用 no-follow 语义拒绝 symlink 替换。
    ///
    /// 输入：先 durable 发布的 PNG 引用，随后把对应文件替换为指向同字节外部文件的 symlink。
    /// 输出：即使长度、hash 和魔数都可匹配，读取仍因文件类型失败。
    /// 不变量：外部文件只位于测试临时目录；host path 不进入返回错误。
    /// 失败：临时 I/O、symlink 创建或错误接受使测试失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(unix)]
    #[tokio::test]
    async fn image_artifact_reader_rejects_symlink() -> Result<(), Box<dyn std::error::Error>> {
        use std::os::unix::fs::symlink;

        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 4).await?;
        store
            .create_session("s-image-link", directory.path())
            .await?;
        let png = b"\x89PNG\r\n\x1a\nfixture";
        let artifact = store
            .store_image_artifact("s-image-link", png, "image/png")
            .await?;
        let target = directory
            .path()
            .join("sessions/s-image-link/artifacts")
            .join(&artifact.artifact_id);
        let outside = directory.path().join("outside-image.bin");
        fs::write(&outside, png)?;
        fs::remove_file(&target)?;
        symlink(&outside, &target)?;
        assert!(
            store
                .read_verified_image_artifact("s-image-link", &artifact, 1024)
                .await
                .is_err()
        );
        Ok(())
    }

    /// 功能：验证崩溃恢复标记 run interrupted，并为歧义工具写入不重放结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn recovery_never_replays_ambiguous_tool() -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 1024).await?;
        store.create_session("s1", directory.path()).await?;
        store
            .append_record(
                "s1",
                "run.accepted",
                json!({
                    "runId":"r1","inputMessageId":"m1",
                    "provider":{"id":"faux","modelId":"faux-v1"}
                }),
            )
            .await?;
        store
            .append_record(
                "s1",
                "tool.intent",
                json!({
                    "runId":"r1","turnId":"t1","toolCallId":"c1","name":"file.write",
                    "arguments":{"path":"x","content":"x"},"idempotent":false,"status":"started"
                }),
            )
            .await?;
        let recovered = store.recover_interrupted("s1").await?;
        assert_eq!(recovered.interrupted_runs, 1);
        assert_eq!(recovered.ambiguous_tools, 1);
        let records = store.load("s1").await?;
        assert_eq!(
            records
                .iter()
                .filter(|record| record.kind == "tool.result")
                .count(),
            1
        );
        assert_eq!(
            records
                .iter()
                .filter(|record| record.kind == "run.terminal")
                .count(),
            1
        );
        assert_eq!(
            records
                .iter()
                .filter(|record| record.kind == "message.appended")
                .count(),
            1
        );
        assert_eq!(
            records
                .iter()
                .filter(|record| record.kind == "event.emitted")
                .count(),
            1
        );
        Ok(())
    }

    /// 功能：验证恢复把 denied/rejected/awaiting intent 视为确定未执行并关闭 pending approval。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn recovery_distinguishes_known_not_started_tools() -> Result<(), crate::error::AgentError>
    {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 1024).await?;
        store.create_session("s-known", directory.path()).await?;
        store
            .append_record(
                "s-known",
                "run.accepted",
                json!({
                    "runId":"r-known","inputMessageId":"m-known",
                    "provider":{"id":"faux","modelId":"faux-v1"}
                }),
            )
            .await?;
        for (call_id, status) in [
            ("call-denied", "denied"),
            ("call-rejected", "rejected"),
            ("call-awaiting", "awaiting_approval"),
        ] {
            store
                .append_record(
                    "s-known",
                    "tool.intent",
                    json!({
                        "runId":"r-known","turnId":"t-known","toolCallId":call_id,
                        "name":"file.write","arguments":{"path":"x","content":"x"},
                        "idempotent":false,"status":status
                    }),
                )
                .await?;
        }
        store
            .append_record(
                "s-known",
                "approval.requested",
                json!({
                    "runId":"r-known",
                    "approval":{
                        "approvalId":"approval-known",
                        "toolCallId":"call-awaiting",
                        "operation":"file.write",
                        "arguments":{"path":"x","content":"x"},
                        "operationHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                        "risk":"medium",
                        "resources":[{"kind":"path","value":"x"}],
                        "choices":["allow_once","deny"],
                        "expiresAt":"2037-10-21T07:28:00Z"
                    }
                }),
            )
            .await?;
        let recovered = store.recover_interrupted("s-known").await?;
        assert_eq!(recovered.ambiguous_tools, 0);
        let records = store.load("s-known").await?;
        let results = records
            .iter()
            .filter(|record| record.kind == "tool.result")
            .collect::<Vec<_>>();
        assert_eq!(results.len(), 3);
        assert!(
            results
                .iter()
                .all(|record| record.data["outcomeKnown"] == true)
        );
        assert_eq!(results[0].data["result"]["terminationReason"], "denied");
        assert_eq!(
            results[1].data["result"]["terminationReason"],
            "validation_error"
        );
        assert_eq!(results[2].data["result"]["terminationReason"], "denied");
        assert!(records.iter().any(|record| {
            record.kind == "approval.resolved" && record.data["resolutionSource"] == "disconnect"
        }));
        assert_eq!(
            records
                .iter()
                .filter(|record| record.kind == "message.appended")
                .count(),
            3
        );
        Ok(())
    }

    /// 功能：验证崩溃点位于 known tool.result 与 canonical message 之间时精确补齐消息。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn recovery_repairs_known_result_missing_message() -> Result<(), crate::error::AgentError>
    {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 1024).await?;
        store
            .create_session("s-known-result", directory.path())
            .await?;
        store
            .append_record(
                "s-known-result",
                "run.accepted",
                json!({
                    "runId":"r-known-result","inputMessageId":"m-known-result",
                    "provider":{"id":"faux","modelId":"faux-v1"}
                }),
            )
            .await?;
        store
            .append_record(
                "s-known-result",
                "tool.intent",
                json!({
                    "runId":"r-known-result","turnId":"t-known-result",
                    "toolCallId":"call-known-result","name":"file.read",
                    "arguments":{"path":"x"},"idempotent":true,"status":"started"
                }),
            )
            .await?;
        store
            .append_record(
                "s-known-result",
                "tool.result",
                json!({
                    "runId":"r-known-result","turnId":"t-known-result",
                    "toolCallId":"call-known-result","outcomeKnown":true,
                    "result":{"content":[{"type":"text","text":"known content"}],"isError":false}
                }),
            )
            .await?;
        let recovered = store.recover_interrupted("s-known-result").await?;
        assert_eq!(recovered.ambiguous_tools, 0);
        let records = store.load("s-known-result").await?;
        assert_eq!(
            records
                .iter()
                .filter(|record| record.kind == "tool.result")
                .count(),
            1
        );
        let message = records
            .iter()
            .find(|record| record.kind == "message.appended")
            .ok_or_else(|| {
                crate::error::AgentError::new(
                    crate::error::ErrorCode::InternalError,
                    "recovered canonical message is missing",
                )
            })?;
        assert_eq!(
            message.data["message"]["content"][0]["text"],
            "known content"
        );
        assert_eq!(message.data["message"]["isError"], false);
        Ok(())
    }

    /// 功能：验证歧义工具与未终止 run 恢复严格保持首次 journal 记录顺序而非 ID 排序。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn recovery_preserves_journal_order_for_reverse_ids()
    -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 1024).await?;
        store.create_session("s-order", directory.path()).await?;
        for (run_id, call_id) in [("run-z", "call-z"), ("run-a", "call-a")] {
            store
                .append_record(
                    "s-order",
                    "run.accepted",
                    json!({
                        "runId":run_id,
                        "inputMessageId":format!("message-{run_id}"),
                        "provider":{"id":"faux","modelId":"faux-v1"}
                    }),
                )
                .await?;
            store
                .append_record(
                    "s-order",
                    "tool.intent",
                    json!({
                        "runId":run_id,"turnId":format!("turn-{run_id}"),
                        "toolCallId":call_id,"name":"process.exec",
                        "arguments":{"executable":"never-run","args":[]},
                        "idempotent":false,"status":"started"
                    }),
                )
                .await?;
        }
        store.recover_interrupted("s-order").await?;
        let records = store.load("s-order").await?;
        let result_order = records
            .iter()
            .filter(|record| record.kind == "tool.result")
            .filter_map(|record| record.data["toolCallId"].as_str())
            .collect::<Vec<_>>();
        let terminal_order = records
            .iter()
            .filter(|record| record.kind == "run.terminal")
            .filter_map(|record| record.data["runId"].as_str())
            .collect::<Vec<_>>();
        assert_eq!(result_order, ["call-z", "call-a"]);
        assert_eq!(terminal_order, ["run-z", "run-a"]);
        Ok(())
    }

    /// 功能：验证 Rust 可按属性顺序无关方式读取 createdBy.language=dotnet 的线性 journal。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn reads_dotnet_linear_fixture_and_filters_events() -> Result<(), crate::error::AgentError>
    {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 1024).await?;
        install_fixture(
            directory.path(),
            "dotnet-linear",
            include_str!("../tests/fixtures/dotnet-linear-session.jsonl"),
        )?;
        let header = store.header("dotnet-linear").await?;
        assert_eq!(
            header
                .created_by
                .as_ref()
                .map(|created_by| created_by.language.as_str()),
            Some("dotnet")
        );
        let snapshot = store.snapshot("dotnet-linear", 3, None).await?;
        assert_eq!(snapshot.latest_seq, 4);
        assert_eq!(snapshot.messages.len(), 2);
        assert_eq!(snapshot.events.len(), 1);
        assert_eq!(snapshot.events[0]["seq"], 4);
        Ok(())
    }

    /// 功能：验证 Rust 精确投影公共 branch/compaction fixture 的 summary、retained 与 Branch A。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn projects_branch_and_compaction_fixture() -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 1024).await?;
        install_fixture(
            directory.path(),
            "session-branch-compaction",
            include_str!("../../CONFORMANCE/fixtures/session/branch-compaction-v0.1/journal.jsonl"),
        )?;
        let snapshot = store.snapshot("session-branch-compaction", 0, None).await?;
        assert_eq!(
            snapshot.selected_head_record_id.as_deref(),
            Some("record-select-branch-a")
        );
        assert_eq!(
            snapshot.compaction_record_id.as_deref(),
            Some("record-compaction")
        );
        assert_eq!(
            snapshot
                .messages
                .iter()
                .filter_map(|message| message["messageId"].as_str())
                .collect::<Vec<_>>(),
            [
                "message-summary",
                "message-recent-user",
                "message-recent-assistant",
                "message-branch-a-user",
                "message-branch-a-assistant"
            ]
        );
        Ok(())
    }

    /// 功能：验证 branch selection 的 earlier parent、控制 head、CAS、重复选择和全局事件流。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn selects_branch_with_cas_and_preserves_global_events()
    -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 1024).await?;
        let session_id = "session-branch-mutation";
        store.create_session(session_id, directory.path()).await?;
        append_test_user(&store, session_id, "message-root-user", "root").await?;
        let target =
            append_test_assistant(&store, session_id, "message-target-assistant", "target").await?;
        append_test_user(&store, session_id, "message-sibling-user", "sibling").await?;
        append_test_assistant(
            &store,
            session_id,
            "message-sibling-assistant",
            "sibling answer",
        )
        .await?;
        let first_event = store
            .append_record(
                session_id,
                "event.emitted",
                json!({
                    "event":{
                        "sessionId":session_id,"runId":"run-sibling","seq":1,
                        "time":chrono::Utc::now(),"type":"run.completed",
                        "data":{"status":"completed","usage":{"inputTokens":0,"outputTokens":0,"totalTokens":0}}
                    }
                }),
            )
            .await?;
        let lease = store.acquire_writer(session_id).await?;
        let selected = store
            .select_branch(&lease, &first_event.record_id, &target.record_id)
            .await?;
        assert_eq!(selected.target_leaf_record_id, target.record_id);
        assert_eq!(
            selected.selection_record_id,
            selected.selected_head_record_id
        );
        let second_event = store
            .append_record(
                session_id,
                "event.emitted",
                json!({
                    "event":{
                        "sessionId":session_id,"runId":"run-selected","seq":2,
                        "time":chrono::Utc::now(),"type":"run.completed",
                        "data":{"status":"completed","usage":{"inputTokens":0,"outputTokens":0,"totalTokens":0}}
                    }
                }),
            )
            .await?;
        let snapshot = store.snapshot(session_id, 0, None).await?;
        assert_eq!(
            snapshot
                .messages
                .iter()
                .filter_map(|message| message["messageId"].as_str())
                .collect::<Vec<_>>(),
            ["message-root-user", "message-target-assistant"]
        );
        assert_eq!(
            snapshot
                .events
                .iter()
                .filter_map(|event| event["seq"].as_u64())
                .collect::<Vec<_>>(),
            [1, 2]
        );

        let before_failed_compare = store.load(session_id).await?.len();
        let stale = store
            .select_branch(&lease, &first_event.record_id, &target.record_id)
            .await
            .expect_err("stale expected head must fail");
        assert_eq!(stale.code.rpc_code(), -32010);
        assert!(stale.retryable);
        assert_eq!(stale.details["kind"], "stale_session_head");
        assert_eq!(store.load(session_id).await?.len(), before_failed_compare);

        let unknown = store
            .select_branch(&lease, &second_event.record_id, "record-does-not-exist")
            .await
            .expect_err("unknown target must fail");
        assert_eq!(unknown.code, ErrorCode::InvalidParams);
        assert_eq!(unknown.details["kind"], "record_not_found");
        assert_eq!(unknown.details["field"], "targetLeafRecordId");
        assert_eq!(store.load(session_id).await?.len(), before_failed_compare);

        let repeated = store
            .select_branch(&lease, &second_event.record_id, &target.record_id)
            .await?;
        assert_ne!(repeated.selection_record_id, selected.selection_record_id);
        let records = store.load(session_id).await?;
        let last = records.last().ok_or_else(|| {
            crate::error::AgentError::new(ErrorCode::InternalError, "selection is missing")
        })?;
        assert_eq!(last.parent_id.as_deref(), Some(target.record_id.as_str()));
        assert_eq!(last.data["leafRecordId"], target.record_id);
        Ok(())
    }

    /// 功能：验证 target ancestry 停在 run.accepted 时即使后代已 terminal 仍不可选择。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn rejects_historical_non_quiescent_branch_target() -> Result<(), crate::error::AgentError>
    {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 1024).await?;
        let session_id = "session-non-quiescent-target";
        store.create_session(session_id, directory.path()).await?;
        let target = store
            .append_record(
                session_id,
                "run.accepted",
                json!({
                    "runId":"run-open","inputMessageId":"message-input",
                    "provider":{"id":"faux","modelId":"faux-v1"}
                }),
            )
            .await?;
        let current = store
            .append_record(
                session_id,
                "run.terminal",
                json!({
                    "runId":"run-open","status":"completed",
                    "usage":{"inputTokens":0,"outputTokens":0,"totalTokens":0}
                }),
            )
            .await?;
        let lease = store.acquire_writer(session_id).await?;
        let before = store.load(session_id).await?.len();
        let error = store
            .select_branch(&lease, &current.record_id, &target.record_id)
            .await
            .expect_err("target before terminal must stay non-quiescent");
        assert_eq!(error.code.rpc_code(), -32010);
        assert!(!error.retryable);
        assert_eq!(error.details["kind"], "branch_not_quiescent");
        assert_eq!(store.load(session_id).await?.len(), before);
        Ok(())
    }

    /// 功能：验证 compaction 两记录、boundary/token 零追加失败、summary-only 与第二次投影。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn compacts_context_and_supersedes_previous_projection()
    -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 1024).await?;
        let session_id = "session-compaction-mutation";
        store.create_session(session_id, directory.path()).await?;
        append_test_user(&store, session_id, "message-old-user", "old").await?;
        let old_assistant =
            append_test_assistant(&store, session_id, "message-old-assistant", "old answer")
                .await?;
        let retained =
            append_test_user(&store, session_id, "message-recent-user", "recent").await?;
        let source = append_test_assistant(
            &store,
            session_id,
            "message-recent-assistant",
            "recent answer",
        )
        .await?;
        let lease = store.acquire_writer(session_id).await?;
        let first = store
            .compact_context(
                &lease,
                test_compaction_input(
                    &source.record_id,
                    &retained.record_id,
                    "summary one",
                    100,
                    40,
                ),
            )
            .await?;
        assert_eq!(first.compaction_record_id, first.selected_head_record_id);
        let first_snapshot = store.snapshot(session_id, 0, None).await?;
        assert_eq!(
            first_snapshot
                .messages
                .iter()
                .filter_map(|message| message["messageId"].as_str())
                .collect::<Vec<_>>(),
            [
                first.summary_message_id.as_str(),
                "message-recent-user",
                "message-recent-assistant"
            ]
        );
        assert_eq!(
            first_snapshot.compaction_record_id.as_deref(),
            Some(first.compaction_record_id.as_str())
        );

        let before_invalid = store.load(session_id).await?.len();
        let token_error = store
            .compact_context(
                &lease,
                test_compaction_input(
                    &first.compaction_record_id,
                    &retained.record_id,
                    "invalid growth",
                    10,
                    11,
                ),
            )
            .await
            .expect_err("tokensAfter growth must fail");
        assert_eq!(token_error.details["kind"], "invalid_compaction_tokens");
        assert_eq!(store.load(session_id).await?.len(), before_invalid);

        let boundary_error = store
            .compact_context(
                &lease,
                test_compaction_input(
                    &first.compaction_record_id,
                    &old_assistant.record_id,
                    "invalid boundary",
                    10,
                    5,
                ),
            )
            .await
            .expect_err("assistant boundary must fail");
        assert_eq!(
            boundary_error.details["kind"],
            "invalid_compaction_boundary"
        );
        assert_eq!(store.load(session_id).await?.len(), before_invalid);

        let lone_summary = append_test_assistant(
            &store,
            session_id,
            "message-lone-summary",
            "summary durable before a simulated crash",
        )
        .await?;
        let partial_snapshot = store.snapshot(session_id, 0, None).await?;
        assert_eq!(
            partial_snapshot.compaction_record_id.as_deref(),
            Some(first.compaction_record_id.as_str())
        );
        assert_eq!(
            partial_snapshot
                .messages
                .last()
                .and_then(|message| message["messageId"].as_str()),
            Some("message-lone-summary")
        );

        let second_retained =
            append_test_user(&store, session_id, "message-second-user", "second").await?;
        let second_source = append_test_assistant(
            &store,
            session_id,
            "message-second-assistant",
            "second answer",
        )
        .await?;
        assert_eq!(
            second_retained.parent_id.as_deref(),
            Some(lone_summary.record_id.as_str())
        );
        let second = store
            .compact_context(
                &lease,
                test_compaction_input(
                    &second_source.record_id,
                    &second_retained.record_id,
                    "summary two",
                    40,
                    20,
                ),
            )
            .await?;
        let second_snapshot = store.snapshot(session_id, 0, None).await?;
        assert_eq!(
            second_snapshot.compaction_record_id.as_deref(),
            Some(second.compaction_record_id.as_str())
        );
        assert_eq!(
            second_snapshot
                .messages
                .iter()
                .filter_map(|message| message["messageId"].as_str())
                .collect::<Vec<_>>(),
            [
                second.summary_message_id.as_str(),
                "message-second-user",
                "message-second-assistant"
            ]
        );
        Ok(())
    }

    /// 功能：验证 writer lease 跨句柄互斥且释放后可以重新获取。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn writer_lease_rejects_concurrent_owner() -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let store = SessionStore::new(directory.path(), 1024).await?;
        store.create_session("s1", directory.path()).await?;
        let first = store.acquire_writer("s1").await?;
        let error = store
            .acquire_writer("s1")
            .await
            .expect_err("second writer must fail without waiting");
        assert_eq!(error.code, ErrorCode::SessionLocked);
        assert_eq!(error.code.rpc_code(), -32002);
        assert_eq!(error.details["kind"], "session_locked");
        drop(first);
        let second = store.acquire_writer("s1").await?;
        assert_eq!(second.session_id(), "s1");
        drop(second);
        assert!(
            !directory.path().join("sessions/s1/writer.lock.d").exists(),
            "clean SessionLease drop must remove canonical portable claim"
        );
        Ok(())
    }
}
