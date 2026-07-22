//! 第三方 Session v3 到 portable Session v0.1 的 clean-room 一次性导入器。
//!
//! 作者：高宏顺 <18272669457@163.com>

use std::collections::{BTreeMap, BTreeSet};
use std::fs::{File, Metadata, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::OnceLock;

use base64::Engine;
use chrono::{DateTime, Utc};
use regex::Regex;
use serde::Serialize;
use serde_json::value::RawValue;
use serde_json::{Map, Value};
use sha2::{Digest, Sha256};
use tokio::task;
use uuid::Uuid;

use super::{read_journal, sync_directory};
use crate::error::{AgentError, ErrorCode};
use crate::protocol::parse_strict_value;

const REFERENCE_COMMIT: &str = "3f9aa5d10b35223abf6146f960ff5cb5c68053ee";
const IMPORT_NAMESPACE: &str = "org.agentprotocol.pi-v3";
const REPORT_MEDIA_TYPE: &str = "application/vnd.qxnm-forge.pi-v3-import-report+json";
// 冻结的 portable displayName；保留旧值以避免既有 journal 与 conformance fixture 迁移。
const REPORT_DISPLAY_NAME: &str = "PI Session v3 import report";
const MAX_SOURCE_BYTES: usize = 16_777_216;
const MAX_LINE_BYTES: usize = 1_048_576;
const MAX_SOURCE_ENTRIES: usize = 100_000;
const MAX_JSON_DEPTH: usize = 64;
const MAX_JSON_NODES: usize = 200_000;
const MAX_REPORT_BYTES: usize = 4_194_304;
const MAX_CONTENT_BLOCKS: usize = 1_024;
const MAX_IMAGE_BYTES: usize = 8_388_608;
const CONFORMANCE_SOURCE_SHA256: &str =
    "f31b9b7a784a3af526fec462e5b16c5bffaaedf45b7a8fa101d946bfd3d02b39";
const CONFORMANCE_SOURCE_SESSION_ID: &str = "pi-v3-fixture-session";
const CONFORMANCE_TARGET_SESSION_ID: &str = "session-import-pi-v3-fixture";
const CONFORMANCE_CREATED_AT: &str = "2026-01-02T00:00:00Z";
const CONFORMANCE_WORKSPACE: &str = "[CONFORMANCE_WORKSPACE]";

const KNOWN_ENTRY_TYPES: &[&str] = &[
    "message",
    "thinking_level_change",
    "model_change",
    "compaction",
    "branch_summary",
    "custom",
    "custom_message",
    "label",
    "session_info",
];

const FORBIDDEN_IMPORTED_KINDS: &[&str] = &[
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
    "faux.configured",
];

const WARNING_ORDER: &[&str] = &[
    "compaction_details_quarantined",
    "compaction_summary_usage_unavailable",
    "custom_message_context_excluded",
    "custom_semantics_excluded",
    "extension_details_quarantined",
    "label_semantics_extension_only",
    "sensitive_value_redacted",
    "source_path_not_persisted",
    "unknown_entry_type",
    "unsupported_content_block",
    "unsupported_message_role",
];

/// 一次 第三方 Session v3 导入所需的全部调用方授权参数。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Clone)]
pub struct LegacySessionV3ImportOptions {
    pub source: PathBuf,
    pub workspace: PathBuf,
    pub state_root: PathBuf,
    pub session_id: Option<String>,
    pub conformance: bool,
}

/// 第三方 Session v3 导入成功后允许输出的三个安全字段。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LegacySessionV3ImportOutcome {
    pub status: String,
    pub session_id: String,
    pub report_artifact_id: String,
}

#[derive(Debug)]
struct SourceEntry {
    line: usize,
    raw_line: Vec<u8>,
    entry_id: String,
    entry_type: String,
    parent_id: Option<String>,
    timestamp: String,
    value: Value,
}

#[derive(Debug)]
struct SourceDocument {
    file: File,
    path: PathBuf,
    fingerprint: FileFingerprint,
    raw: Vec<u8>,
    sha256: String,
    session_id: String,
    entries: Vec<SourceEntry>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct FileFingerprint {
    length: u64,
    modified_nanos: Option<u128>,
    #[cfg(unix)]
    device: u64,
    #[cfg(unix)]
    inode: u64,
}

impl FileFingerprint {
    /// 功能：提取用于检测 source 替换或写入竞态的稳定文件身份。
    ///
    /// 输入：已通过 regular-file 检查的文件 metadata。
    /// 输出：长度、mtime 以及 Unix device/inode 组合。
    /// 不变量：指纹不包含或泄漏调用方路径。
    /// 失败：metadata 时间早于 epoch 时仍安全返回无时间指纹。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn from_metadata(metadata: &Metadata) -> Self {
        #[cfg(unix)]
        use std::os::unix::fs::MetadataExt;

        Self {
            length: metadata.len(),
            modified_nanos: metadata
                .modified()
                .ok()
                .and_then(|time| time.duration_since(std::time::UNIX_EPOCH).ok())
                .map(|duration| duration.as_nanos()),
            #[cfg(unix)]
            device: metadata.dev(),
            #[cfg(unix)]
            inode: metadata.ino(),
        }
    }
}

impl SourceDocument {
    /// 功能：在发布 target 前复核 source 路径与持有句柄仍指向同一未变化普通文件。
    ///
    /// 输入：导入期间始终保持打开的只读 source 句柄及最初指纹。
    /// 输出：路径、句柄、长度和文件身份完全一致时成功。
    /// 不变量：不修改、重命名、chmod、truncate 或锁破坏 source。
    /// 失败：替换、symlink、特殊文件、mtime/size/identity 漂移或 I/O 错误均关闭导入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn verify_unchanged(&self) -> Result<(), AgentError> {
        let path_metadata = std::fs::symlink_metadata(&self.path)
            .map_err(|_| source_error("第三方 Session v3 source identity changed during import"))?;
        let handle_metadata = self
            .file
            .metadata()
            .map_err(|_| source_error("第三方 Session v3 source identity cannot be revalidated"))?;
        if path_metadata.file_type().is_symlink()
            || !path_metadata.is_file()
            || !handle_metadata.is_file()
            || FileFingerprint::from_metadata(&path_metadata) != self.fingerprint
            || FileFingerprint::from_metadata(&handle_metadata) != self.fingerprint
        {
            return Err(source_error(
                "第三方 Session v3 source changed during import",
            ));
        }
        Ok(())
    }
}

#[derive(Debug)]
struct ImportRecord {
    kind: String,
    record_id: String,
    session_id: String,
    seq: u64,
    parent_id: Option<String>,
    time: String,
    data: Box<RawValue>,
    extensions: Option<Box<RawValue>>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ImportRecordWire<'a> {
    schema_version: &'static str,
    kind: &'a str,
    record_id: &'a str,
    session_id: &'a str,
    seq: u64,
    parent_id: Option<&'a str>,
    time: &'a str,
    data: &'a RawValue,
    #[serde(skip_serializing_if = "Option::is_none")]
    extensions: Option<&'a RawValue>,
}

impl ImportRecord {
    /// 功能：按 portable journal 字段顺序编码一条完整 JSONL record（不含 LF）。
    ///
    /// 输入：已验证的记录字段和预先构造的严格 raw JSON data/extensions。
    /// 输出：可直接写入 journal 的 UTF-8 JSON 字节。
    /// 不变量：raw 子值只由导入器内部生成，调用方输入不能注入尾随 JSON。
    /// 失败：内部序列化失败返回不含 source 内容的结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn encode(&self) -> Result<Vec<u8>, AgentError> {
        serde_json::to_vec(&ImportRecordWire {
            schema_version: "0.1",
            kind: &self.kind,
            record_id: &self.record_id,
            session_id: &self.session_id,
            seq: self.seq,
            parent_id: self.parent_id.as_deref(),
            time: &self.time,
            data: &self.data,
            extensions: self.extensions.as_deref(),
        })
        .map_err(internal_serialization_error)
    }
}

#[derive(Debug, Clone)]
struct ArtifactBytes {
    artifact_id: String,
    bytes: Vec<u8>,
}

#[derive(Debug)]
enum MappedContentBlock {
    Json(String),
    Image {
        media_type: String,
        bytes: Vec<u8>,
        alt: Option<String>,
    },
}

#[derive(Debug, Clone)]
struct SourceMapping {
    leaf_record_id: String,
    message_role: Option<String>,
}

#[derive(Debug)]
struct ReportItem {
    source_line: u64,
    source_entry_id: Option<String>,
    source_type: String,
    disposition: String,
    reason_codes: Vec<String>,
    source_line_sha256: String,
    target_record_ids: Vec<String>,
    context_excluded: bool,
    quarantined_json: Option<String>,
}

#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "camelCase")]
struct ImportCounts {
    source_entries: u64,
    target_records: u64,
    mapped_source_entries: u64,
    extension_source_entries: u64,
    reported_source_entries: u64,
    redacted_values: u64,
    skipped_source_entries: u64,
}

#[derive(Debug)]
struct IdFactory {
    deterministic: bool,
}

impl IdFactory {
    /// 功能：为一个 source entry 生成新 target record ID。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn record(&self, source_entry_id: &str) -> String {
        if self.deterministic {
            format!("record-{source_entry_id}")
        } else {
            format!("record-{}", Uuid::new_v4())
        }
    }

    /// 功能：为一个 source message 生成新 portable message ID。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn message(&self, source_entry_id: &str) -> String {
        if self.deterministic {
            format!("message-{source_entry_id}")
        } else {
            format!("message-{}", Uuid::new_v4())
        }
    }

    /// 功能：生成 source parent jump 对应的 auditable branch selection ID。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn selection(&self, target_source_entry_id: &str) -> String {
        if self.deterministic {
            format!("record-select-{target_source_entry_id}")
        } else {
            format!("record-{}", Uuid::new_v4())
        }
    }

    /// 功能：生成 compaction synthetic summary 的 record/message ID。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn compaction_summary(&self) -> (String, String) {
        if self.deterministic {
            (
                "record-pi-compaction-summary".to_owned(),
                "message-pi-compaction-summary".to_owned(),
            )
        } else {
            (
                format!("record-{}", Uuid::new_v4()),
                format!("message-{}", Uuid::new_v4()),
            )
        }
    }

    /// 功能：生成 mandatory report artifact 及其 journal record ID。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn report(&self) -> (String, String) {
        if self.deterministic {
            (
                "artifact-pi-v3-import-report".to_owned(),
                "record-pi-import-report".to_owned(),
            )
        } else {
            (
                format!("artifact-{}", Uuid::new_v4()),
                format!("record-{}", Uuid::new_v4()),
            )
        }
    }

    /// 功能：为解码后的图片生成独立 artifact 与 artifact.created record ID。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn image(&self) -> (String, String) {
        (
            format!("artifact-{}", Uuid::new_v4()),
            format!("record-{}", Uuid::new_v4()),
        )
    }
}

/// 功能：异步执行严格、离线、一次性的 第三方 Session v3 导入。
///
/// 输入：只读 source、已存在 workspace、授权 state root、可选新 Session ID 与 conformance 开关。
/// 输出：报告状态、新 Session ID 与 report artifact ID；所有 target 字节已验证并发布。
/// 不变量：不执行第三方 runtime/Provider/工具，不修改 source，不覆盖/合并 target，发布前完整 flush。
/// 失败：source、tree、隐私、映射、目标冲突、验证或 I/O 失败时不留下可用 target。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub async fn import_legacy_session_v3(
    options: LegacySessionV3ImportOptions,
) -> Result<LegacySessionV3ImportOutcome, AgentError> {
    task::spawn_blocking(move || import_legacy_session_v3_blocking(options))
        .await
        .map_err(|error| AgentError::new(ErrorCode::InternalError, error.to_string()))?
}

/// 功能：在线程阻塞边界内完成 第三方 Session v3 导入的全部文件事务。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn import_legacy_session_v3_blocking(
    options: LegacySessionV3ImportOptions,
) -> Result<LegacySessionV3ImportOutcome, AgentError> {
    let workspace = options.workspace.canonicalize().map_err(|_| {
        AgentError::new(
            ErrorCode::InvalidParams,
            "workspace cannot be canonicalized",
        )
    })?;
    if !workspace.is_dir() {
        return Err(AgentError::new(
            ErrorCode::InvalidParams,
            "workspace must be an existing directory",
        ));
    }
    std::fs::create_dir_all(&options.state_root)?;
    let state_root = options
        .state_root
        .canonicalize()
        .map_err(|_| AgentError::new(ErrorCode::IoError, "state root cannot be canonicalized"))?;
    if !state_root.is_dir() {
        return Err(AgentError::new(
            ErrorCode::InvalidParams,
            "state root must be a directory",
        ));
    }
    let source = load_source(&options.source)?;
    let requested_session_id = options
        .session_id
        .unwrap_or_else(|| format!("session-{}", Uuid::new_v4()));
    validate_opaque_id(&requested_session_id, "target Session ID")?;
    if requested_session_id == source.session_id {
        return Err(AgentError::new(
            ErrorCode::InvalidParams,
            "target Session ID must differ from source Session ID",
        ));
    }
    let deterministic = options.conformance
        && source.sha256 == CONFORMANCE_SOURCE_SHA256
        && source.session_id == CONFORMANCE_SOURCE_SESSION_ID
        && requested_session_id == CONFORMANCE_TARGET_SESSION_ID;
    if options.conformance && !deterministic {
        return Err(AgentError::new(
            ErrorCode::InvalidParams,
            "conformance IDs are restricted to the bundled synthetic 第三方 Session v3 fixture",
        ));
    }
    let ids = IdFactory { deterministic };
    let (report_artifact_id, report_record_id) = ids.report();
    let workspace_value = if deterministic {
        CONFORMANCE_WORKSPACE.to_owned()
    } else {
        workspace.to_string_lossy().into_owned()
    };
    let created_at = if deterministic {
        CONFORMANCE_CREATED_AT.to_owned()
    } else {
        Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Nanos, true)
    };
    let mut conversion = Conversion::new(&source, &requested_session_id, &report_artifact_id, ids);
    conversion.convert_all()?;
    let expected_target_records = conversion
        .records
        .len()
        .checked_add(1)
        .ok_or_else(|| source_error("第三方 Session v3 target record count exceeds limit"))?;
    let report = conversion.build_report(expected_target_records as u64)?;
    if report.len() > MAX_REPORT_BYTES {
        return Err(source_error(
            "第三方 Session v3 import report exceeds limit",
        ));
    }
    let report_sha256 = encode_lower_sha256(&report);
    conversion.append_report_record(
        &report_record_id,
        &report_artifact_id,
        report.len() as u64,
        &report_sha256,
        &created_at,
    )?;
    if conversion.records.len() != expected_target_records {
        return Err(internal_error(
            "第三方 Session v3 target count changed during report binding",
        ));
    }
    let header = encode_header(
        &requested_session_id,
        &created_at,
        &workspace_value,
        &source,
        &report_artifact_id,
    )?;
    let journal = encode_journal(&header, &conversion.records)?;
    source.verify_unchanged()?;
    publish_import(
        &state_root,
        &requested_session_id,
        &journal,
        &report_artifact_id,
        &report,
        &conversion.artifacts,
        &source,
    )?;
    Ok(LegacySessionV3ImportOutcome {
        status: "completed_with_warnings".to_owned(),
        session_id: requested_session_id,
        report_artifact_id,
    })
}

/// 功能：只读打开并严格解析 第三方 Session v3 source，同时保持句柄供发布前复核。
///
/// 输入：调用方选择的 source 路径。
/// 输出：完整字节/hash、header、tree entries 和只读文件身份。
/// 不变量：只接受 regular、BOM-free、严格 UTF-8/LF JSONL；不跟随 source symlink。
/// 失败：大小/行/深度、重复键、版本、唯一 ID、earlier parent 或已知字段违规即拒绝。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn load_source(path: &Path) -> Result<SourceDocument, AgentError> {
    let path_metadata = std::fs::symlink_metadata(path)
        .map_err(|_| source_error("第三方 Session v3 source cannot be opened"))?;
    if path_metadata.file_type().is_symlink() || !path_metadata.is_file() {
        return Err(source_error(
            "第三方 Session v3 source must be a regular non-symlink file",
        ));
    }
    let mut file = OpenOptions::new()
        .read(true)
        .open(path)
        .map_err(|_| source_error("第三方 Session v3 source cannot be opened read-only"))?;
    let handle_metadata = file
        .metadata()
        .map_err(|_| source_error("第三方 Session v3 source metadata cannot be read"))?;
    if !handle_metadata.is_file()
        || FileFingerprint::from_metadata(&path_metadata)
            != FileFingerprint::from_metadata(&handle_metadata)
    {
        return Err(source_error(
            "第三方 Session v3 source identity changed while opening",
        ));
    }
    if handle_metadata.len() as usize > MAX_SOURCE_BYTES {
        return Err(source_error("第三方 Session v3 source exceeds size limit"));
    }
    let mut raw = Vec::with_capacity(handle_metadata.len() as usize);
    Read::by_ref(&mut file)
        .take((MAX_SOURCE_BYTES + 1) as u64)
        .read_to_end(&mut raw)
        .map_err(|_| source_error("第三方 Session v3 source cannot be read"))?;
    if raw.len() > MAX_SOURCE_BYTES {
        return Err(source_error("第三方 Session v3 source exceeds size limit"));
    }
    let fingerprint = FileFingerprint::from_metadata(&handle_metadata);
    let (session_id, entries) = parse_source_bytes(&raw)?;
    let document = SourceDocument {
        file,
        path: path.to_path_buf(),
        fingerprint,
        sha256: encode_lower_sha256(&raw),
        raw,
        session_id,
        entries,
    };
    document.verify_unchanged()?;
    Ok(document)
}

/// 功能：严格解析 第三方 Session v3 JSONL 字节并验证 header 与 earlier-only 单根树。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_source_bytes(raw: &[u8]) -> Result<(String, Vec<SourceEntry>), AgentError> {
    if raw.starts_with(&[0xef, 0xbb, 0xbf]) || !raw.ends_with(b"\n") || raw.contains(&b'\r') {
        return Err(source_error(
            "第三方 Session v3 source must be BOM-free LF JSONL",
        ));
    }
    let body = &raw[..raw.len().saturating_sub(1)];
    let lines = body.split(|byte| *byte == b'\n').collect::<Vec<_>>();
    if lines.is_empty()
        || lines.len() > MAX_SOURCE_ENTRIES + 1
        || lines
            .iter()
            .any(|line| line.is_empty() || line.len() > MAX_LINE_BYTES)
    {
        return Err(source_error(
            "第三方 Session v3 source line count or size is invalid",
        ));
    }
    let mut values = Vec::with_capacity(lines.len());
    for line in &lines {
        let text = std::str::from_utf8(line)
            .map_err(|_| source_error("第三方 Session v3 source is not strict UTF-8"))?;
        let value = parse_strict_value(text).map_err(|_| {
            source_error("第三方 Session v3 source contains invalid or duplicate-key JSON")
        })?;
        validate_json_limits(&value)?;
        values.push(value);
    }
    let header = require_object(&values[0], "第三方 Session v3 header")?;
    require_exact_fields(
        header,
        &["type", "version", "id", "timestamp", "cwd", "parentSession"],
        "第三方 Session v3 header",
    )?;
    if string_field(header, "type")? != "session" || integer_field(header, "version")? != 3 {
        return Err(source_error(
            "第三方 Session v3 source header/version is invalid",
        ));
    }
    let session_id = bounded_string_field(header, "id", 256)?;
    validate_utc(string_field(header, "timestamp")?)?;
    if !header.get("cwd").is_some_and(Value::is_string)
        || header
            .get("parentSession")
            .is_some_and(|value| !value.is_string())
    {
        return Err(source_error(
            "第三方 Session v3 header path fields are invalid",
        ));
    }
    let mut prior = BTreeMap::<String, Value>::new();
    let mut entries = Vec::with_capacity(values.len().saturating_sub(1));
    for (offset, value) in values.into_iter().skip(1).enumerate() {
        let line = offset + 2;
        let object = require_object(&value, "第三方 Session v3 entry")?;
        if object.get("type").and_then(Value::as_str) == Some("session") {
            return Err(source_error(
                "第三方 Session v3 source contains a second header",
            ));
        }
        let entry_id = bounded_string_field(object, "id", 256)?;
        if prior.contains_key(&entry_id) {
            return Err(source_error(
                "第三方 Session v3 source contains duplicate entry IDs",
            ));
        }
        let parent_id = match object.get("parentId") {
            Some(Value::Null) => None,
            Some(Value::String(value)) if !value.is_empty() && value.len() <= 256 => {
                Some(value.clone())
            }
            _ => return Err(source_error("第三方 Session v3 entry parent is invalid")),
        };
        if (line == 2 && parent_id.is_some())
            || (line > 2
                && parent_id
                    .as_ref()
                    .is_none_or(|parent| !prior.contains_key(parent)))
        {
            return Err(source_error(
                "第三方 Session v3 parent must reference one earlier entry",
            ));
        }
        let entry_type = bounded_string_field(object, "type", 128)?;
        if !entry_type
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
        {
            return Err(source_error("第三方 Session v3 entry type is invalid"));
        }
        let timestamp = string_field(object, "timestamp")?.to_owned();
        validate_utc(&timestamp)?;
        if KNOWN_ENTRY_TYPES.contains(&entry_type.as_str()) {
            validate_known_entry(object, &entry_type, &prior)?;
        }
        prior.insert(entry_id.clone(), value.clone());
        entries.push(SourceEntry {
            line,
            raw_line: lines[line - 1].to_vec(),
            entry_id,
            entry_type,
            parent_id,
            timestamp,
            value,
        });
    }
    if entries.is_empty() {
        return Err(source_error(
            "第三方 Session v3 source must contain at least one entry",
        ));
    }
    Ok((session_id, entries))
}

/// 功能：验证已知 第三方格式 entry 只能携带冻结字段并满足必要类型/引用。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_known_entry(
    entry: &Map<String, Value>,
    entry_type: &str,
    prior: &BTreeMap<String, Value>,
) -> Result<(), AgentError> {
    let allowed: &[&str] = match entry_type {
        "message" => &["type", "id", "parentId", "timestamp", "message"],
        "thinking_level_change" => &["type", "id", "parentId", "timestamp", "thinkingLevel"],
        "model_change" => &["type", "id", "parentId", "timestamp", "provider", "modelId"],
        "compaction" => &[
            "type",
            "id",
            "parentId",
            "timestamp",
            "summary",
            "firstKeptEntryId",
            "tokensBefore",
            "details",
            "fromHook",
        ],
        "branch_summary" => &[
            "type",
            "id",
            "parentId",
            "timestamp",
            "fromId",
            "summary",
            "details",
            "fromHook",
        ],
        "custom" => &["type", "id", "parentId", "timestamp", "customType", "data"],
        "custom_message" => &[
            "type",
            "id",
            "parentId",
            "timestamp",
            "customType",
            "content",
            "details",
            "display",
        ],
        "label" => &["type", "id", "parentId", "timestamp", "targetId", "label"],
        "session_info" => &["type", "id", "parentId", "timestamp", "name"],
        _ => {
            return Err(internal_error(
                "known 第三方格式 entry routing is incomplete",
            ));
        }
    };
    require_exact_fields(entry, allowed, "known 第三方 Session v3 entry")?;
    match entry_type {
        "message" => {
            let message = entry
                .get("message")
                .and_then(Value::as_object)
                .ok_or_else(|| source_error("第三方 Session v3 message object is invalid"))?;
            let role = string_field(message, "role")?;
            if !matches!(
                role,
                "user" | "assistant" | "toolResult" | "bashExecution" | "custom"
            ) {
                return Err(source_error(
                    "第三方 Session v3 message role is unsupported",
                ));
            }
        }
        "model_change" => {
            bounded_string_field(entry, "provider", 128)?;
            bounded_string_field(entry, "modelId", 128)?;
        }
        "thinking_level_change" => {
            if !matches!(
                string_field(entry, "thinkingLevel")?,
                "off" | "minimal" | "low" | "medium" | "high" | "xhigh"
            ) {
                return Err(source_error("第三方 Session v3 thinking level is invalid"));
            }
        }
        "compaction" => {
            bounded_string_field(entry, "summary", 1_048_576)?;
            let retained = bounded_string_field(entry, "firstKeptEntryId", 256)?;
            if !prior.contains_key(&retained)
                || !source_ancestor_contains(entry.get("parentId"), &retained, prior)?
            {
                return Err(source_error(
                    "第三方 Session v3 compaction boundary is invalid",
                ));
            }
            non_negative_integer(entry.get("tokensBefore"), "第三方 Session v3 tokensBefore")?;
        }
        "branch_summary" => {
            let from_id = bounded_string_field(entry, "fromId", 256)?;
            if from_id != "root" && !prior.contains_key(&from_id) {
                return Err(source_error(
                    "第三方 Session v3 branch summary source is invalid",
                ));
            }
            bounded_string_field(entry, "summary", 1_048_576)?;
        }
        "custom" | "custom_message" => {
            bounded_string_field(entry, "customType", 256)?;
            if entry_type == "custom_message"
                && !entry.get("display").is_some_and(Value::is_boolean)
            {
                return Err(source_error(
                    "第三方 Session v3 custom message display is invalid",
                ));
            }
        }
        "label" => {
            let target = bounded_string_field(entry, "targetId", 256)?;
            if !prior.contains_key(&target)
                || entry
                    .get("label")
                    .is_some_and(|value| !value.is_null() && !value.is_string())
            {
                return Err(source_error("第三方 Session v3 label is invalid"));
            }
        }
        "session_info"
            if entry
                .get("name")
                .is_some_and(|value| !value.is_null() && !value.is_string()) =>
        {
            return Err(source_error(
                "第三方 Session v3 session metadata is invalid",
            ));
        }
        _ => {}
    }
    Ok(())
}

/// 功能：确认 retained source ID 位于当前 第三方格式 entry parent ancestry。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn source_ancestor_contains(
    parent: Option<&Value>,
    retained: &str,
    prior: &BTreeMap<String, Value>,
) -> Result<bool, AgentError> {
    let mut current = parent.and_then(Value::as_str);
    let mut seen = BTreeSet::new();
    while let Some(entry_id) = current {
        if entry_id == retained {
            return Ok(true);
        }
        if !seen.insert(entry_id.to_owned()) {
            return Err(source_error(
                "第三方 Session v3 source ancestry contains a cycle",
            ));
        }
        let entry = prior
            .get(entry_id)
            .and_then(Value::as_object)
            .ok_or_else(|| source_error("第三方 Session v3 source ancestry is invalid"))?;
        current = entry.get("parentId").and_then(Value::as_str);
    }
    Ok(false)
}

/// 功能：限制已解析 JSON 的深度与总节点数，避免递归/内存攻击。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_json_limits(value: &Value) -> Result<(), AgentError> {
    let mut stack = vec![(value, 1_usize)];
    let mut visited = 0_usize;
    while let Some((current, depth)) = stack.pop() {
        visited = visited.saturating_add(1);
        if visited > MAX_JSON_NODES || depth > MAX_JSON_DEPTH {
            return Err(source_error(
                "第三方 Session v3 JSON depth or node count exceeds limit",
            ));
        }
        match current {
            Value::Array(values) => {
                stack.extend(values.iter().map(|child| (child, depth + 1)));
            }
            Value::Object(values) => {
                stack.extend(values.values().map(|child| (child, depth + 1)));
            }
            _ => {}
        }
    }
    Ok(())
}

/// 功能：验证 UTC timestamp 使用明确 `Z` 且能解析为 RFC 3339。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_utc(value: &str) -> Result<(), AgentError> {
    if !value.ends_with('Z')
        || DateTime::parse_from_rfc3339(value)
            .ok()
            .is_none_or(|time| time.offset().local_minus_utc() != 0)
    {
        return Err(source_error(
            "第三方 Session v3 timestamp must be UTC RFC 3339",
        ));
    }
    Ok(())
}

/// 功能：要求 JSON 值为对象并返回借用。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn require_object<'a>(
    value: &'a Value,
    _context: &str,
) -> Result<&'a Map<String, Value>, AgentError> {
    value
        .as_object()
        .ok_or_else(|| source_error("第三方 Session v3 value must be an object"))
}

/// 功能：拒绝已知对象上的任何未冻结字段。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn require_exact_fields(
    object: &Map<String, Value>,
    allowed: &[&str],
    _context: &str,
) -> Result<(), AgentError> {
    if object.keys().any(|key| !allowed.contains(&key.as_str())) {
        return Err(source_error(
            "known 第三方 Session v3 object contains unknown fields",
        ));
    }
    Ok(())
}

/// 功能：读取必需字符串字段。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn string_field<'a>(object: &'a Map<String, Value>, field: &str) -> Result<&'a str, AgentError> {
    object
        .get(field)
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| source_error("第三方 Session v3 required string field is invalid"))
}

/// 功能：读取并限制必需字符串字段长度。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn bounded_string_field(
    object: &Map<String, Value>,
    field: &str,
    maximum: usize,
) -> Result<String, AgentError> {
    let value = string_field(object, field)?;
    if value.len() > maximum {
        return Err(source_error("第三方 Session v3 string field exceeds limit"));
    }
    Ok(value.to_owned())
}

/// 功能：读取必需非负 safe integer。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn non_negative_integer(value: Option<&Value>, _context: &str) -> Result<u64, AgentError> {
    value
        .and_then(Value::as_u64)
        .filter(|value| *value <= 9_007_199_254_740_991)
        .ok_or_else(|| source_error("第三方 Session v3 integer field is invalid"))
}

/// 功能：读取必需整数字段。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn integer_field(object: &Map<String, Value>, field: &str) -> Result<u64, AgentError> {
    non_negative_integer(object.get(field), field)
}

/// 功能：验证 portable opaque ID，而不回显调用方输入。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_opaque_id(value: &str, _context: &str) -> Result<(), AgentError> {
    let valid = !value.is_empty()
        && value.len() <= 128
        && value.bytes().enumerate().all(|(index, byte)| {
            byte.is_ascii_alphanumeric() || (index > 0 && matches!(byte, b'-' | b'_' | b'.' | b':'))
        });
    if valid {
        Ok(())
    } else {
        Err(AgentError::new(
            ErrorCode::InvalidParams,
            "target Session ID is not a portable opaque ID",
        ))
    }
}

/// 功能：计算字节串的 64 位小写 SHA-256。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn encode_lower_sha256(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

/// 功能：构造不泄漏 source 内容或路径的 source validation 错误。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn source_error(message: &'static str) -> AgentError {
    AgentError::new(ErrorCode::JournalCorrupt, message)
        .with_details(serde_json::json!({"kind":"pi_v3_import_invalid"}))
}

/// 功能：构造导入器内部不变量错误。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn internal_error(message: &'static str) -> AgentError {
    AgentError::new(ErrorCode::InternalError, message)
}

/// 功能：把 serde 序列化失败收敛为安全内部错误。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn internal_serialization_error(error: serde_json::Error) -> AgentError {
    AgentError::new(
        ErrorCode::InternalError,
        format!("第三方 Session v3 import serialization failed: {error}"),
    )
}

#[derive(Debug)]
struct Conversion<'a> {
    source: &'a SourceDocument,
    session_id: &'a str,
    report_artifact_id: &'a str,
    ids: IdFactory,
    records: Vec<ImportRecord>,
    artifacts: Vec<ArtifactBytes>,
    mappings: BTreeMap<String, SourceMapping>,
    selected_head: Option<String>,
    current_provider: Option<(String, String)>,
    current_thinking: String,
    report_items: Vec<ReportItem>,
    mapped_source_entries: u64,
    extension_source_entries: u64,
    redacted_values: u64,
    warnings: BTreeSet<String>,
}

impl<'a> Conversion<'a> {
    /// 功能：创建尚未产生 target record 的 第三方 Session v3 映射状态机。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn new(
        source: &'a SourceDocument,
        session_id: &'a str,
        report_artifact_id: &'a str,
        ids: IdFactory,
    ) -> Self {
        let mut warnings = BTreeSet::new();
        warnings.insert("source_path_not_persisted".to_owned());
        Self {
            source,
            session_id,
            report_artifact_id,
            ids,
            records: Vec::new(),
            artifacts: Vec::new(),
            mappings: BTreeMap::new(),
            selected_head: None,
            current_provider: None,
            current_thinking: "off".to_owned(),
            report_items: Vec::new(),
            mapped_source_entries: 0,
            extension_source_entries: 0,
            redacted_values: 0,
            warnings,
        }
    }

    /// 功能：按 source 文件顺序转换全部 entry，并显式记录每次 branch parent jump。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn convert_all(&mut self) -> Result<(), AgentError> {
        for entry in &self.source.entries {
            self.select_source_parent(entry)?;
            match entry.entry_type.as_str() {
                "message" => self.convert_message(entry)?,
                "model_change" => self.convert_model_change(entry)?,
                "thinking_level_change" => self.convert_thinking_change(entry)?,
                "compaction" => self.convert_compaction(entry)?,
                "branch_summary" => self.convert_branch_summary(entry)?,
                "custom" => self.convert_custom(entry)?,
                "custom_message" => self.convert_custom_message(entry)?,
                "label" => self.convert_label(entry)?,
                "session_info" => self.convert_session_info(entry)?,
                _ => self.convert_unknown(entry)?,
            }
        }
        Ok(())
    }

    /// 功能：在 source parent 不等于当前 selected head 时追加 auditable `branch.selected`。
    ///
    /// 输入：当前按文件序待转换 entry 及已经完成的 source-to-target map。
    /// 输出：selected head 与 mapped parent 对齐，后续普通 record 可线性延伸。
    /// 不变量：selection parent 与 leafRecordId 相同且只指向 earlier target record。
    /// 失败：缺少 source parent 映射或第一根状态不一致时拒绝整个导入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn select_source_parent(&mut self, entry: &SourceEntry) -> Result<(), AgentError> {
        let Some(source_parent) = entry.parent_id.as_deref() else {
            if self.selected_head.is_some() {
                return Err(source_error(
                    "第三方 Session v3 source contains more than one root",
                ));
            }
            return Ok(());
        };
        let target_parent = self
            .mappings
            .get(source_parent)
            .map(|mapping| mapping.leaf_record_id.clone())
            .ok_or_else(|| source_error("第三方 Session v3 source parent mapping is missing"))?;
        if self.selected_head.as_deref() == Some(target_parent.as_str()) {
            return Ok(());
        }
        let selection_id = self.ids.selection(source_parent);
        let data = raw_json(format!(
            "{{\"leafRecordId\":{}}}",
            json_string(&target_parent)?
        ))?;
        let extensions = raw_json(format!(
            "{{\"{IMPORT_NAMESPACE}\":{{\"reason\":\"source-parent-jump\",\"nextSourceEntryId\":{}}}}}",
            json_string(&entry.entry_id)?
        ))?;
        self.push_record(ImportRecord {
            kind: "branch.selected".to_owned(),
            record_id: selection_id.clone(),
            session_id: self.session_id.to_owned(),
            seq: self.next_seq()?,
            parent_id: Some(target_parent),
            time: entry.timestamp.clone(),
            data,
            extensions: Some(extensions),
        })?;
        self.selected_head = Some(selection_id);
        Ok(())
    }

    /// 功能：映射 user/assistant/toolResult 消息，其他历史角色进入隔离 extension。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn convert_message(&mut self, entry: &SourceEntry) -> Result<(), AgentError> {
        let value = entry
            .value
            .as_object()
            .and_then(|object| object.get("message"))
            .and_then(Value::as_object)
            .ok_or_else(|| source_error("第三方 Session v3 message is malformed"))?;
        match string_field(value, "role")? {
            "user" => self.convert_user_message(entry, value),
            "assistant" => self.convert_assistant_message(entry, value),
            "toolResult" => self.convert_tool_result_message(entry, value),
            "bashExecution" | "custom" => {
                let quarantine = sanitized_json(&entry.value, &mut self.redacted_values)?;
                self.quarantine_entry(
                    entry,
                    "unsupported_message_role",
                    "quarantined",
                    true,
                    true,
                    quarantine,
                )
            }
            _ => Err(source_error(
                "第三方 Session v3 message role is unsupported",
            )),
        }
    }

    /// 功能：把 第三方格式 user content 转为 canonical user message。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn convert_user_message(
        &mut self,
        entry: &SourceEntry,
        message: &Map<String, Value>,
    ) -> Result<(), AgentError> {
        let (content, redacted, unsupported) = map_content(message.get("content"), false)?;
        self.redacted_values = self.redacted_values.saturating_add(redacted);
        if content.is_empty() {
            let quarantine = sanitized_json(&entry.value, &mut self.redacted_values)?;
            return self.quarantine_entry(
                entry,
                "unsupported_content_block",
                "quarantined",
                true,
                true,
                quarantine,
            );
        }
        let (content, mut target_ids) = self.materialize_content(entry, content)?;
        let message_id = self.ids.message(&entry.entry_id);
        let record_id = self.ids.record(&entry.entry_id);
        let data = raw_json(format!(
            "{{\"message\":{{\"messageId\":{},\"role\":\"user\",\"content\":{},\"time\":{}}}}}",
            json_string(&message_id)?,
            content_array(&content),
            json_string(&entry.timestamp)?
        ))?;
        self.append_source_record(entry, record_id.clone(), "message.appended", data, None)?;
        target_ids.push(record_id.clone());
        self.mappings.insert(
            entry.entry_id.clone(),
            SourceMapping {
                leaf_record_id: record_id.clone(),
                message_role: Some("user".to_owned()),
            },
        );
        self.mapped_source_entries = self.mapped_source_entries.saturating_add(1);
        self.report_mapping_warnings(entry, target_ids, redacted, unsupported, false)
    }

    /// 功能：把 第三方格式 assistant 内容、Provider、finish reason 与 usage 归一化为完成消息。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn convert_assistant_message(
        &mut self,
        entry: &SourceEntry,
        message: &Map<String, Value>,
    ) -> Result<(), AgentError> {
        let (content, redacted, unsupported) = map_content(message.get("content"), true)?;
        self.redacted_values = self.redacted_values.saturating_add(redacted);
        let (content, mut target_ids) = self.materialize_content(entry, content)?;
        let provider = bounded_string_field(message, "provider", 128)?;
        let model = bounded_string_field(message, "model", 128)?;
        validate_opaque_component(&provider)?;
        validate_opaque_component(&model)?;
        let finish = match string_field(message, "stopReason")? {
            "toolUse" => "tool_use",
            "aborted" => "cancelled",
            "stop" => "stop",
            "length" => "length",
            "error" => "error",
            "cancelled" => "cancelled",
            "interrupted" => "interrupted",
            _ => {
                return Err(source_error(
                    "第三方 Session v3 assistant stop reason is invalid",
                ));
            }
        };
        let usage = map_usage(message.get("usage"))?;
        let message_id = self.ids.message(&entry.entry_id);
        let record_id = self.ids.record(&entry.entry_id);
        let data = raw_json(format!(
            "{{\"message\":{{\"messageId\":{},\"role\":\"assistant\",\"content\":{},\"provider\":{{\"id\":{},\"modelId\":{}}},\"finishReason\":{},\"usage\":{},\"time\":{}}}}}",
            json_string(&message_id)?,
            content_array(&content),
            json_string(&provider)?,
            json_string(&model)?,
            json_string(finish)?,
            usage,
            json_string(&entry.timestamp)?
        ))?;
        self.append_source_record(entry, record_id.clone(), "message.appended", data, None)?;
        target_ids.push(record_id.clone());
        self.current_provider = Some((provider, model));
        self.mappings.insert(
            entry.entry_id.clone(),
            SourceMapping {
                leaf_record_id: record_id.clone(),
                message_role: Some("assistant".to_owned()),
            },
        );
        self.mapped_source_entries = self.mapped_source_entries.saturating_add(1);
        self.report_mapping_warnings(entry, target_ids, redacted, unsupported, false)
    }

    /// 功能：把历史 第三方格式 toolResult 转为惰性 canonical tool message，不创建工具生命周期。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn convert_tool_result_message(
        &mut self,
        entry: &SourceEntry,
        message: &Map<String, Value>,
    ) -> Result<(), AgentError> {
        let call_id = message
            .get("toolCallId")
            .or_else(|| message.get("callId"))
            .and_then(Value::as_str)
            .ok_or_else(|| source_error("第三方 Session v3 tool result call ID is missing"))?;
        let name = message
            .get("toolName")
            .or_else(|| message.get("name"))
            .and_then(Value::as_str)
            .ok_or_else(|| source_error("第三方 Session v3 tool result name is missing"))?;
        validate_opaque_component(call_id)?;
        validate_tool_name(name)?;
        let (content, redacted, unsupported) = map_content(message.get("content"), false)?;
        if content.is_empty() {
            let quarantine = sanitized_json(&entry.value, &mut self.redacted_values)?;
            return self.quarantine_entry(
                entry,
                "unsupported_content_block",
                "quarantined",
                true,
                true,
                quarantine,
            );
        }
        self.redacted_values = self.redacted_values.saturating_add(redacted);
        let (content, mut target_ids) = self.materialize_content(entry, content)?;
        let is_error = message
            .get("isError")
            .and_then(Value::as_bool)
            .unwrap_or(false);
        let message_id = self.ids.message(&entry.entry_id);
        let record_id = self.ids.record(&entry.entry_id);
        let data = raw_json(format!(
            "{{\"message\":{{\"messageId\":{},\"role\":\"tool\",\"toolCallId\":{},\"toolName\":{},\"content\":{},\"isError\":{},\"time\":{}}}}}",
            json_string(&message_id)?,
            json_string(call_id)?,
            json_string(name)?,
            content_array(&content),
            is_error,
            json_string(&entry.timestamp)?
        ))?;
        self.append_source_record(entry, record_id.clone(), "message.appended", data, None)?;
        target_ids.push(record_id.clone());
        self.mappings.insert(
            entry.entry_id.clone(),
            SourceMapping {
                leaf_record_id: record_id.clone(),
                message_role: Some("tool".to_owned()),
            },
        );
        self.mapped_source_entries = self.mapped_source_entries.saturating_add(1);
        self.report_mapping_warnings(entry, target_ids, redacted, unsupported, false)
    }

    /// 功能：映射 第三方格式 model_change 并更新后续 thinking/compaction 使用的模型状态。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn convert_model_change(&mut self, entry: &SourceEntry) -> Result<(), AgentError> {
        let object = require_object(&entry.value, "model change")?;
        let provider = bounded_string_field(object, "provider", 128)?;
        let model = bounded_string_field(object, "modelId", 128)?;
        validate_opaque_component(&provider)?;
        validate_opaque_component(&model)?;
        let record_id = self.ids.record(&entry.entry_id);
        let data = raw_json(format!(
            "{{\"provider\":{{\"id\":{},\"modelId\":{}}},\"thinking\":{}}}",
            json_string(&provider)?,
            json_string(&model)?,
            json_string(&self.current_thinking)?
        ))?;
        self.append_source_record(entry, record_id.clone(), "model.selected", data, None)?;
        self.current_provider = Some((provider, model));
        self.register_simple_mapping(entry, record_id, None, true)
    }

    /// 功能：在已有模型时映射 thinking level；缺少模型时隔离而不发明 Provider。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn convert_thinking_change(&mut self, entry: &SourceEntry) -> Result<(), AgentError> {
        let object = require_object(&entry.value, "thinking change")?;
        let thinking = bounded_string_field(object, "thinkingLevel", 16)?;
        let Some((provider, model)) = self.current_provider.clone() else {
            return self.quarantine_entry(
                entry,
                "extension_details_quarantined",
                "quarantined",
                true,
                true,
                format!("{{\"thinkingLevel\":{}}}", json_string(&thinking)?),
            );
        };
        let record_id = self.ids.record(&entry.entry_id);
        let data = raw_json(format!(
            "{{\"provider\":{{\"id\":{},\"modelId\":{}}},\"thinking\":{}}}",
            json_string(&provider)?,
            json_string(&model)?,
            json_string(&thinking)?
        ))?;
        self.append_source_record(entry, record_id.clone(), "model.selected", data, None)?;
        self.current_thinking = thinking;
        self.register_simple_mapping(entry, record_id, None, true)
    }

    /// 功能：把单条 第三方格式 compaction 展开为 summary message 与 `context.compacted` 两条记录。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn convert_compaction(&mut self, entry: &SourceEntry) -> Result<(), AgentError> {
        let object = require_object(&entry.value, "compaction")?;
        let summary = bounded_string_field(object, "summary", 1_048_576)?;
        let (summary, redacted) = sanitize_text(&summary);
        self.redacted_values = self.redacted_values.saturating_add(u64::from(redacted));
        let retained_source = bounded_string_field(object, "firstKeptEntryId", 256)?;
        let retained_mapping = self.mappings.get(&retained_source).ok_or_else(|| {
            source_error("第三方 Session v3 compaction boundary mapping is missing")
        })?;
        if retained_mapping.message_role.as_deref() != Some("user") {
            return Err(source_error(
                "第三方 Session v3 compaction boundary must map to a user message",
            ));
        }
        let retained_record_id = retained_mapping.leaf_record_id.clone();
        let source_leaf_record_id = self
            .selected_head
            .clone()
            .ok_or_else(|| source_error("第三方 Session v3 compaction has no selected source"))?;
        let tokens_before = integer_field(object, "tokensBefore")?;
        let (provider, model) = self
            .current_provider
            .clone()
            .ok_or_else(|| source_error("第三方 Session v3 compaction has no selected model"))?;
        let (summary_record_id, summary_message_id) = self.ids.compaction_summary();
        let summary_data = raw_json(format!(
            "{{\"message\":{{\"messageId\":{},\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":{}}}],\"provider\":{{\"id\":{},\"modelId\":{}}},\"finishReason\":\"stop\",\"usage\":{{\"inputTokens\":0,\"outputTokens\":0,\"totalTokens\":0}},\"time\":{},\"extensions\":{{\"{IMPORT_NAMESPACE}\":{{\"syntheticUsage\":true}}}}}}}}",
            json_string(&summary_message_id)?,
            json_string(&summary)?,
            json_string(&provider)?,
            json_string(&model)?,
            json_string(&entry.timestamp)?
        ))?;
        self.append_source_record(
            entry,
            summary_record_id.clone(),
            "message.appended",
            summary_data,
            Some("\"mappingPart\":\"summary\""),
        )?;
        let compaction_record_id = self.ids.record(&entry.entry_id);
        let compaction_data = raw_json(format!(
            "{{\"sourceLeafRecordId\":{},\"summaryMessageId\":{},\"firstRetainedRecordId\":{},\"tokensBefore\":{},\"strategy\":\"pi-v3-import\"}}",
            json_string(&source_leaf_record_id)?,
            json_string(&summary_message_id)?,
            json_string(&retained_record_id)?,
            tokens_before
        ))?;
        self.append_source_record(
            entry,
            compaction_record_id.clone(),
            "context.compacted",
            compaction_data,
            Some("\"mappingPart\":\"compaction\""),
        )?;
        let target_ids = vec![summary_record_id, compaction_record_id.clone()];
        self.mappings.insert(
            entry.entry_id.clone(),
            SourceMapping {
                leaf_record_id: compaction_record_id,
                message_role: None,
            },
        );
        self.mapped_source_entries = self.mapped_source_entries.saturating_add(1);
        let mut reasons = vec!["compaction_summary_usage_unavailable".to_owned()];
        let quarantine = if object.contains_key("details") || object.contains_key("fromHook") {
            reasons.insert(0, "compaction_details_quarantined".to_owned());
            Some(compaction_quarantine_json(
                object,
                &mut self.redacted_values,
            )?)
        } else {
            None
        };
        if redacted {
            reasons.push("sensitive_value_redacted".to_owned());
        }
        self.add_report_item(
            entry,
            "mapped_with_loss",
            reasons,
            target_ids,
            false,
            quarantine,
        )
    }

    /// 功能：把 第三方格式 branch summary 转成固定 wrapper 的 canonical user message。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn convert_branch_summary(&mut self, entry: &SourceEntry) -> Result<(), AgentError> {
        let object = require_object(&entry.value, "branch summary")?;
        let summary = bounded_string_field(object, "summary", 1_048_576)?;
        let (summary, redacted) = sanitize_text(&summary);
        self.redacted_values = self.redacted_values.saturating_add(u64::from(redacted));
        let from_id = bounded_string_field(object, "fromId", 256)?;
        let text = format!(
            "The following is a summary of a branch that this conversation came back from:\n\n<summary>\n{summary}\n</summary>"
        );
        let message_id = self.ids.message(&entry.entry_id);
        let record_id = self.ids.record(&entry.entry_id);
        let mut data_json = format!(
            "{{\"message\":{{\"messageId\":{},\"role\":\"user\",\"content\":[{{\"type\":\"text\",\"text\":{}}}],\"time\":{},\"extensions\":",
            json_string(&message_id)?,
            json_string(&text)?,
            json_string(&entry.timestamp)?,
        );
        data_json.push_str(&format!(
            "{{\"{IMPORT_NAMESPACE}\":{{\"sourceRole\":\"branchSummary\",\"fromSourceEntryId\":{}}}}}",
            json_string(&from_id)?
        ));
        data_json.push_str("}}");
        let data = raw_json(data_json)?;
        self.append_source_record(entry, record_id.clone(), "message.appended", data, None)?;
        self.mappings.insert(
            entry.entry_id.clone(),
            SourceMapping {
                leaf_record_id: record_id.clone(),
                message_role: Some("user".to_owned()),
            },
        );
        self.mapped_source_entries = self.mapped_source_entries.saturating_add(1);
        self.report_mapping_warnings(entry, vec![record_id], u64::from(redacted), false, false)
    }

    /// 功能：把 第三方格式 custom 数据隔离为 context-free extension 并完整登记报告。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn convert_custom(&mut self, entry: &SourceEntry) -> Result<(), AgentError> {
        let object = require_object(&entry.value, "custom")?;
        let quarantine = custom_quarantine_json(object, &mut self.redacted_values)?;
        self.quarantine_entry(
            entry,
            "custom_semantics_excluded",
            "preserved_extension",
            false,
            true,
            quarantine,
        )
    }

    /// 功能：把 第三方格式 custom_message 隔离，明确禁止其进入 Provider context。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn convert_custom_message(&mut self, entry: &SourceEntry) -> Result<(), AgentError> {
        let object = require_object(&entry.value, "custom message")?;
        let quarantine = custom_message_quarantine_json(object, &mut self.redacted_values)?;
        self.quarantine_entry(
            entry,
            "custom_message_context_excluded",
            "quarantined",
            true,
            true,
            quarantine,
        )
    }

    /// 功能：把 第三方格式 label 保存为 extension-only inspectable metadata。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn convert_label(&mut self, entry: &SourceEntry) -> Result<(), AgentError> {
        let object = require_object(&entry.value, "label")?;
        let target_source_id = bounded_string_field(object, "targetId", 256)?;
        let target_record_id = self
            .mappings
            .get(&target_source_id)
            .map(|mapping| mapping.leaf_record_id.clone())
            .ok_or_else(|| source_error("第三方 Session v3 label target mapping is missing"))?;
        let label = object.get("label").cloned().unwrap_or(Value::Null);
        let (label, redactions) = sanitize_value(&label);
        self.redacted_values = self.redacted_values.saturating_add(redactions);
        let record_id = self.ids.record(&entry.entry_id);
        let value_json = format!(
            "{{\"sourceEntryId\":{},\"sourceType\":\"label\",\"targetSourceEntryId\":{},\"targetRecordId\":{},\"label\":{},\"disposition\":\"preserved_extension\",\"reportArtifactId\":{}}}",
            json_string(&entry.entry_id)?,
            json_string(&target_source_id)?,
            json_string(&target_record_id)?,
            serde_json::to_string(&label).map_err(internal_serialization_error)?,
            json_string(self.report_artifact_id)?
        );
        self.append_extension_record(entry, record_id.clone(), value_json)?;
        self.register_extension_mapping(entry, record_id.clone());
        let quarantine = label_quarantine_json(object, &mut self.redacted_values)?;
        let mut reasons = vec!["label_semantics_extension_only".to_owned()];
        if redactions > 0 {
            reasons.push("sensitive_value_redacted".to_owned());
        }
        self.add_report_item(
            entry,
            "preserved_extension",
            reasons,
            vec![record_id],
            true,
            Some(quarantine),
        )
    }

    /// 功能：把 第三方 session_info 的 bounded name 映射为 portable session.metadata。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn convert_session_info(&mut self, entry: &SourceEntry) -> Result<(), AgentError> {
        let object = require_object(&entry.value, "session info")?;
        let name = object.get("name").cloned().unwrap_or(Value::Null);
        if name.as_str().is_some_and(|value| value.len() > 256) {
            return Err(source_error("第三方 Session v3 session name exceeds limit"));
        }
        let (name, redactions) = sanitize_value(&name);
        self.redacted_values = self.redacted_values.saturating_add(redactions);
        let record_id = self.ids.record(&entry.entry_id);
        let data = raw_json(format!(
            "{{\"name\":{}}}",
            serde_json::to_string(&name).map_err(internal_serialization_error)?
        ))?;
        self.append_source_record(entry, record_id.clone(), "session.metadata", data, None)?;
        self.register_simple_mapping(entry, record_id.clone(), None, true)?;
        if redactions > 0 {
            self.add_report_item(
                entry,
                "redacted",
                vec!["sensitive_value_redacted".to_owned()],
                vec![record_id],
                false,
                None,
            )?;
        }
        Ok(())
    }

    /// 功能：隔离 base/tree 合法但类型未知的 第三方格式 future entry。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn convert_unknown(&mut self, entry: &SourceEntry) -> Result<(), AgentError> {
        let quarantine = unknown_quarantine_json(entry, &mut self.redacted_values)?;
        self.quarantine_entry(
            entry,
            "unknown_entry_type",
            "quarantined",
            true,
            true,
            quarantine,
        )
    }

    /// 功能：创建 context-free extension record 并添加一项 mandatory report evidence。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn quarantine_entry(
        &mut self,
        entry: &SourceEntry,
        reason: &str,
        disposition: &str,
        include_context_flag: bool,
        context_excluded: bool,
        quarantined_json: String,
    ) -> Result<(), AgentError> {
        let record_id = self.ids.record(&entry.entry_id);
        let value = format!(
            "{{\"sourceEntryId\":{},\"sourceType\":{},\"disposition\":{},{}\"reportArtifactId\":{}}}",
            json_string(&entry.entry_id)?,
            json_string(&entry.entry_type)?,
            json_string(disposition)?,
            if include_context_flag {
                "\"contextExcluded\":true,"
            } else {
                ""
            },
            json_string(self.report_artifact_id)?
        );
        self.append_extension_record(entry, record_id.clone(), value)?;
        self.register_extension_mapping(entry, record_id.clone());
        let mut reasons = vec![reason.to_owned()];
        if quarantined_json.contains("\"[REDACTED]\"") {
            reasons.push("sensitive_value_redacted".to_owned());
        }
        self.add_report_item(
            entry,
            disposition,
            reasons,
            vec![record_id],
            context_excluded,
            Some(quarantined_json),
        )
    }

    /// 功能：追加标准 第三方格式 namespace extension record。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn append_extension_record(
        &mut self,
        entry: &SourceEntry,
        record_id: String,
        value_json: String,
    ) -> Result<(), AgentError> {
        let data = raw_json(format!(
            "{{\"namespace\":\"{IMPORT_NAMESPACE}\",\"value\":{value_json}}}"
        ))?;
        self.append_source_record(entry, record_id, "extension", data, None)
    }

    /// 功能：追加带 source entry provenance 的普通 target record。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn append_source_record(
        &mut self,
        entry: &SourceEntry,
        record_id: String,
        kind: &str,
        data: Box<RawValue>,
        extra_origin_fields: Option<&str>,
    ) -> Result<(), AgentError> {
        let extensions = raw_json(origin_extensions(
            &entry.entry_id,
            entry.line,
            extra_origin_fields,
        )?)?;
        let parent_id = self.selected_head.clone();
        self.push_record(ImportRecord {
            kind: kind.to_owned(),
            record_id: record_id.clone(),
            session_id: self.session_id.to_owned(),
            seq: self.next_seq()?,
            parent_id,
            time: entry.timestamp.clone(),
            data,
            extensions: Some(extensions),
        })?;
        self.selected_head = Some(record_id);
        Ok(())
    }

    /// 功能：注册一个单 record source mapping 并更新映射计数。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn register_simple_mapping(
        &mut self,
        entry: &SourceEntry,
        record_id: String,
        message_role: Option<String>,
        mapped: bool,
    ) -> Result<(), AgentError> {
        if self
            .mappings
            .insert(
                entry.entry_id.clone(),
                SourceMapping {
                    leaf_record_id: record_id,
                    message_role,
                },
            )
            .is_some()
        {
            return Err(internal_error(
                "第三方 Session v3 source mapping was duplicated",
            ));
        }
        if mapped {
            self.mapped_source_entries = self.mapped_source_entries.saturating_add(1);
        }
        Ok(())
    }

    /// 功能：注册一个 extension source mapping 并更新隔离计数。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn register_extension_mapping(&mut self, entry: &SourceEntry, record_id: String) {
        self.mappings.insert(
            entry.entry_id.clone(),
            SourceMapping {
                leaf_record_id: record_id,
                message_role: None,
            },
        );
        self.extension_source_entries = self.extension_source_entries.saturating_add(1);
    }

    /// 功能：为 message/summary 的 redaction 或 unsupported block 添加报告项。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn report_mapping_warnings(
        &mut self,
        entry: &SourceEntry,
        target_ids: Vec<String>,
        redactions: u64,
        unsupported: bool,
        context_excluded: bool,
    ) -> Result<(), AgentError> {
        let mut reasons = Vec::new();
        if redactions > 0 {
            reasons.push("sensitive_value_redacted".to_owned());
        }
        if unsupported {
            reasons.push("unsupported_content_block".to_owned());
        }
        if reasons.is_empty() {
            return Ok(());
        }
        self.add_report_item(
            entry,
            if redactions > 0 && !unsupported {
                "redacted"
            } else {
                "mapped_with_loss"
            },
            reasons,
            target_ids,
            context_excluded,
            None,
        )
    }

    /// 功能：先持久化已解码图片 artifact records，再返回 message 可引用的 content JSON。
    ///
    /// 输入：当前 source entry 与已验证的文本/图片块。
    /// 输出：有序 portable blocks 和属于该 source entry 的图片 record IDs。
    /// 不变量：图片字节先进入 artifact 列表，artifact.created 再先于引用它的 message。
    /// 失败：ID、media/hash、record 序号或 JSON 构造失败时中止整个导入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn materialize_content(
        &mut self,
        entry: &SourceEntry,
        blocks: Vec<MappedContentBlock>,
    ) -> Result<(Vec<String>, Vec<String>), AgentError> {
        let mut content = Vec::with_capacity(blocks.len());
        let mut target_record_ids = Vec::new();
        for block in blocks {
            match block {
                MappedContentBlock::Json(value) => content.push(value),
                MappedContentBlock::Image {
                    media_type,
                    bytes,
                    alt,
                } => {
                    let (artifact_id, record_id) = self.ids.image();
                    let sha256 = encode_lower_sha256(&bytes);
                    let byte_length = bytes.len();
                    let data = raw_json(format!(
                        "{{\"artifact\":{{\"artifactId\":{},\"mediaType\":{},\"byteLength\":{},\"sha256\":{}}}}}",
                        json_string(&artifact_id)?,
                        json_string(&media_type)?,
                        byte_length,
                        json_string(&sha256)?
                    ))?;
                    self.append_source_record(
                        entry,
                        record_id.clone(),
                        "artifact.created",
                        data,
                        Some("\"mappingPart\":\"image\""),
                    )?;
                    self.artifacts.push(ArtifactBytes {
                        artifact_id: artifact_id.clone(),
                        bytes,
                    });
                    target_record_ids.push(record_id);
                    let alt_json = match alt {
                        Some(value) => format!(",\"alt\":{}", json_string(&value)?),
                        None => String::new(),
                    };
                    content.push(format!(
                        "{{\"type\":\"image_ref\",\"artifact\":{{\"artifactId\":{},\"mediaType\":{},\"byteLength\":{},\"sha256\":{}}}{alt_json}}}",
                        json_string(&artifact_id)?,
                        json_string(&media_type)?,
                        byte_length,
                        json_string(&sha256)?
                    ));
                }
            }
        }
        Ok((content, target_record_ids))
    }

    /// 功能：追加一项逐行 SHA-256 绑定的 mandatory report evidence。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn add_report_item(
        &mut self,
        entry: &SourceEntry,
        disposition: &str,
        reason_codes: Vec<String>,
        target_record_ids: Vec<String>,
        context_excluded: bool,
        quarantined_json: Option<String>,
    ) -> Result<(), AgentError> {
        for reason in &reason_codes {
            if !WARNING_ORDER.contains(&reason.as_str()) {
                return Err(internal_error(
                    "第三方 Session v3 report reason is not governed",
                ));
            }
            self.warnings.insert(reason.clone());
        }
        self.report_items.push(ReportItem {
            source_line: entry.line as u64,
            source_entry_id: Some(entry.entry_id.clone()),
            source_type: entry.entry_type.clone(),
            disposition: disposition.to_owned(),
            reason_codes,
            source_line_sha256: encode_lower_sha256(&entry.raw_line),
            target_record_ids,
            context_excluded,
            quarantined_json,
        });
        Ok(())
    }

    /// 功能：返回下一条 target record 的 safe positive seq。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn next_seq(&self) -> Result<u64, AgentError> {
        u64::try_from(self.records.len())
            .ok()
            .and_then(|value| value.checked_add(1))
            .filter(|value| *value <= 9_007_199_254_740_991)
            .ok_or_else(|| source_error("第三方 Session v3 target sequence exceeds limit"))
    }

    /// 功能：在内存 target 中追加一条 record 并拒绝重复 ID/非法父引用。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn push_record(&mut self, record: ImportRecord) -> Result<(), AgentError> {
        if self
            .records
            .iter()
            .any(|existing| existing.record_id == record.record_id)
        {
            return Err(internal_error(
                "第三方 Session v3 target generated a duplicate record ID",
            ));
        }
        if record.parent_id.as_ref().is_some_and(|parent| {
            !self
                .records
                .iter()
                .any(|existing| &existing.record_id == parent)
        }) {
            return Err(internal_error(
                "第三方 Session v3 target generated a future parent",
            ));
        }
        self.records.push(record);
        Ok(())
    }

    /// 功能：生成并编码 mandatory import report artifact。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn build_report(&self, target_records: u64) -> Result<Vec<u8>, AgentError> {
        let warnings = WARNING_ORDER
            .iter()
            .filter(|reason| self.warnings.contains(**reason))
            .map(|reason| (*reason).to_owned())
            .collect::<Vec<_>>();
        let counts = ImportCounts {
            source_entries: self.source.entries.len() as u64,
            target_records,
            mapped_source_entries: self.mapped_source_entries,
            extension_source_entries: self.extension_source_entries,
            reported_source_entries: self.report_items.len() as u64,
            redacted_values: self.redacted_values,
            skipped_source_entries: 0,
        };
        let entries = self
            .report_items
            .iter()
            .map(ReportEntryWire::try_from)
            .collect::<Result<Vec<_>, _>>()?;
        let report = ImportReportWire {
            schema_version: "0.1",
            kind: "pi-session-v3-import-report",
            status: "completed_with_warnings",
            source: ImportReportSourceWire {
                format: "pi-session-v3",
                version: 3,
                session_id: &self.source.session_id,
                sha256: &self.source.sha256,
                byte_length: self.source.raw.len() as u64,
                reference_commit: REFERENCE_COMMIT,
                source_path_disposition: "not_persisted",
            },
            target: ImportReportTargetWire {
                session_id: self.session_id,
                report_artifact_id: self.report_artifact_id,
            },
            counts,
            warnings,
            entries,
        };
        let mut bytes = serde_json::to_vec(&report).map_err(internal_serialization_error)?;
        bytes.push(b'\n');
        Ok(bytes)
    }

    /// 功能：把 report artifact reference 作为最终 selected-head record 追加。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn append_report_record(
        &mut self,
        record_id: &str,
        artifact_id: &str,
        byte_length: u64,
        sha256: &str,
        time: &str,
    ) -> Result<(), AgentError> {
        let data = raw_json(format!(
            "{{\"artifact\":{{\"artifactId\":{},\"mediaType\":{},\"byteLength\":{},\"sha256\":{},\"displayName\":{}}}}}",
            json_string(artifact_id)?,
            json_string(REPORT_MEDIA_TYPE)?,
            byte_length,
            json_string(sha256)?,
            json_string(REPORT_DISPLAY_NAME)?
        ))?;
        let extension_body = if self.ids.deterministic {
            "\"synthetic\":\"report-artifact\""
        } else {
            "\"importReport\":true"
        };
        let extensions = raw_json(format!("{{\"{IMPORT_NAMESPACE}\":{{{extension_body}}}}}"))?;
        let parent_id = self.selected_head.clone();
        self.push_record(ImportRecord {
            kind: "artifact.created".to_owned(),
            record_id: record_id.to_owned(),
            session_id: self.session_id.to_owned(),
            seq: self.next_seq()?,
            parent_id,
            time: time.to_owned(),
            data,
            extensions: Some(extensions),
        })?;
        self.selected_head = Some(record_id.to_owned());
        Ok(())
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ImportReportWire<'a> {
    schema_version: &'static str,
    kind: &'static str,
    status: &'static str,
    source: ImportReportSourceWire<'a>,
    target: ImportReportTargetWire<'a>,
    counts: ImportCounts,
    warnings: Vec<String>,
    entries: Vec<ReportEntryWire>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ImportReportSourceWire<'a> {
    format: &'static str,
    version: u8,
    session_id: &'a str,
    sha256: &'a str,
    byte_length: u64,
    reference_commit: &'static str,
    source_path_disposition: &'static str,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ImportReportTargetWire<'a> {
    session_id: &'a str,
    report_artifact_id: &'a str,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ReportEntryWire {
    source_line: u64,
    #[serde(skip_serializing_if = "Option::is_none")]
    source_entry_id: Option<String>,
    source_type: String,
    disposition: String,
    reason_codes: Vec<String>,
    source_line_sha256: String,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    target_record_ids: Vec<String>,
    context_excluded: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    quarantined_value: Option<Box<RawValue>>,
}

impl TryFrom<&ReportItem> for ReportEntryWire {
    type Error = AgentError;

    /// 功能：把内部报告证据转换为字段顺序稳定的 wire DTO。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn try_from(item: &ReportItem) -> Result<Self, Self::Error> {
        Ok(Self {
            source_line: item.source_line,
            source_entry_id: item.source_entry_id.clone(),
            source_type: item.source_type.clone(),
            disposition: item.disposition.clone(),
            reason_codes: item.reason_codes.clone(),
            source_line_sha256: item.source_line_sha256.clone(),
            target_record_ids: item.target_record_ids.clone(),
            context_excluded: item.context_excluded,
            quarantined_value: item
                .quarantined_json
                .as_ref()
                .map(|value| raw_json(value.clone()))
                .transpose()?,
        })
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ImportHeaderWire<'a> {
    kind: &'static str,
    schema_version: &'static str,
    session_id: &'a str,
    created_at: &'a str,
    workspace: &'a str,
    provenance: ImportProvenanceWire<'a>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ImportProvenanceWire<'a> {
    source: &'static str,
    source_session_id: &'a str,
    source_sha256: &'a str,
    reference_commit: &'static str,
    report_artifact_id: &'a str,
    extensions: &'a RawValue,
}

/// 功能：编码 provenance 完整且不含 source cwd/path 的 import Session header。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn encode_header(
    session_id: &str,
    created_at: &str,
    workspace: &str,
    source: &SourceDocument,
    report_artifact_id: &str,
) -> Result<Vec<u8>, AgentError> {
    let extensions = raw_json(format!(
        "{{\"{IMPORT_NAMESPACE}\":{{\"sourceVersion\":3,\"sourcePathDisposition\":\"not_persisted\"}}}}"
    ))?;
    serde_json::to_vec(&ImportHeaderWire {
        kind: "session",
        schema_version: "0.1",
        session_id,
        created_at,
        workspace,
        provenance: ImportProvenanceWire {
            source: "pi-session-v3",
            source_session_id: &source.session_id,
            source_sha256: &source.sha256,
            reference_commit: REFERENCE_COMMIT,
            report_artifact_id,
            extensions: &extensions,
        },
    })
    .map_err(internal_serialization_error)
}

/// 功能：把 header 与完整 records 编码为 LF 结尾 portable journal。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn encode_journal(header: &[u8], records: &[ImportRecord]) -> Result<Vec<u8>, AgentError> {
    let mut output = Vec::new();
    output.extend_from_slice(header);
    output.push(b'\n');
    for record in records {
        output.extend_from_slice(&record.encode()?);
        output.push(b'\n');
    }
    Ok(output)
}

/// 功能：将严格 JSON 字符串包装为可保持字段顺序的 RawValue。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn raw_json(value: String) -> Result<Box<RawValue>, AgentError> {
    let length = value.len();
    RawValue::from_string(value).map_err(|error| {
        AgentError::new(
            ErrorCode::InternalError,
            format!("第三方 Session v3 internal JSON failed at bounded length {length}: {error}"),
        )
    })
}

/// 功能：把字符串编码成 JSON string literal。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn json_string(value: &str) -> Result<String, AgentError> {
    serde_json::to_string(value).map_err(internal_serialization_error)
}

/// 功能：构造每条 source-mapped record 的标准 provenance extension JSON。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn origin_extensions(
    source_entry_id: &str,
    source_line: usize,
    extra_fields: Option<&str>,
) -> Result<String, AgentError> {
    Ok(format!(
        "{{\"{IMPORT_NAMESPACE}\":{{\"sourceEntryId\":{},\"sourceLine\":{}{}{}{}}}}}",
        json_string(source_entry_id)?,
        source_line,
        if extra_fields.is_some() { "," } else { "" },
        extra_fields.unwrap_or(""),
        ""
    ))
}

/// 功能：拼接已经严格编码的 portable content blocks。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn content_array(blocks: &[String]) -> String {
    format!("[{}]", blocks.join(","))
}

/// 功能：映射 第三方格式 message content 中的 text/thinking/toolCall，标记不支持块。
///
/// 输入：第三方格式 content 值及是否允许 assistant reasoning/tool-call。
/// 输出：有序 portable block JSON、redaction 数和是否发生有损排除。
/// 不变量：tool call 仅在 ID/name/object arguments 全部安全时进入 inert transcript。
/// 失败：content 非字符串/数组、超限或已知块字段畸形时拒绝。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn map_content(
    value: Option<&Value>,
    assistant: bool,
) -> Result<(Vec<MappedContentBlock>, u64, bool), AgentError> {
    let owned;
    let values = if let Some(Value::String(text)) = value {
        owned = vec![serde_json::json!({"type":"text","text":text})];
        owned.as_slice()
    } else {
        value.and_then(Value::as_array).ok_or_else(|| {
            source_error("第三方 Session v3 message content must be string or array")
        })?
    };
    let maximum_blocks = if assistant { MAX_CONTENT_BLOCKS } else { 128 };
    if values.len() > maximum_blocks {
        return Err(source_error(
            "第三方 Session v3 message content block count exceeds limit",
        ));
    }
    let mut result = Vec::new();
    let mut redactions = 0_u64;
    let mut unsupported = false;
    for value in values {
        let block = value
            .as_object()
            .ok_or_else(|| source_error("第三方 Session v3 content block must be an object"))?;
        match string_field(block, "type")? {
            "text" => {
                let text = block
                    .get("text")
                    .and_then(Value::as_str)
                    .ok_or_else(|| source_error("第三方 Session v3 text block is invalid"))?;
                let (text, redacted) = sanitize_text(text);
                redactions = redactions.saturating_add(u64::from(redacted));
                result.push(MappedContentBlock::Json(format!(
                    "{{\"type\":\"text\",\"text\":{}}}",
                    json_string(&text)?
                )));
            }
            "thinking" if assistant => {
                let thinking = block
                    .get("thinking")
                    .and_then(Value::as_str)
                    .ok_or_else(|| source_error("第三方 Session v3 thinking block is invalid"))?;
                let (thinking, redacted) = sanitize_text(thinking);
                redactions = redactions.saturating_add(u64::from(redacted));
                result.push(MappedContentBlock::Json(format!(
                    "{{\"type\":\"reasoning\",\"text\":{}}}",
                    json_string(&thinking)?
                )));
            }
            "toolCall" if assistant => {
                let call_id = block
                    .get("id")
                    .or_else(|| block.get("toolCallId"))
                    .and_then(Value::as_str);
                let name = block.get("name").and_then(Value::as_str);
                let arguments = block.get("arguments").and_then(Value::as_object);
                if let (Some(call_id), Some(name), Some(arguments)) = (call_id, name, arguments) {
                    if validate_opaque_component(call_id).is_ok()
                        && validate_tool_name(name).is_ok()
                    {
                        let arguments = sanitized_value_json(
                            &Value::Object(arguments.clone()),
                            &mut redactions,
                        )?;
                        result.push(MappedContentBlock::Json(format!(
                            "{{\"type\":\"tool_call\",\"toolCallId\":{},\"name\":{},\"arguments\":{}}}",
                            json_string(call_id)?,
                            json_string(name)?,
                            arguments
                        )));
                    } else {
                        unsupported = true;
                    }
                } else {
                    unsupported = true;
                }
            }
            "image" if !assistant => {
                let media_type = block
                    .get("mediaType")
                    .or_else(|| block.get("mimeType"))
                    .and_then(Value::as_str);
                let encoded = block.get("data").and_then(Value::as_str);
                let image_count = result
                    .iter()
                    .filter(|block| matches!(block, MappedContentBlock::Image { .. }))
                    .count();
                if let (Some(media_type), Some(encoded)) = (media_type, encoded)
                    && matches!(
                        media_type,
                        "image/png" | "image/jpeg" | "image/gif" | "image/webp"
                    )
                    && encoded.len() <= MAX_IMAGE_BYTES.saturating_mul(4) / 3 + 4
                    && image_count < 7
                {
                    match base64::engine::general_purpose::STANDARD.decode(encoded) {
                        Ok(bytes) if !bytes.is_empty() && bytes.len() <= MAX_IMAGE_BYTES => {
                            let alt = block.get("alt").and_then(Value::as_str).map(|value| {
                                let (value, redacted) = sanitize_text(value);
                                redactions = redactions.saturating_add(u64::from(redacted));
                                value
                            });
                            result.push(MappedContentBlock::Image {
                                media_type: media_type.to_owned(),
                                bytes,
                                alt,
                            });
                        }
                        _ => unsupported = true,
                    }
                } else {
                    unsupported = true;
                }
            }
            _ => unsupported = true,
        }
    }
    Ok((result, redactions, unsupported))
}

/// 功能：按 ADR 0012 归一化 第三方格式 usage 和 aggregate USD cost。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn map_usage(value: Option<&Value>) -> Result<String, AgentError> {
    let usage = value
        .and_then(Value::as_object)
        .ok_or_else(|| source_error("第三方 Session v3 assistant usage is missing"))?;
    let input = integer_field(usage, "input")?;
    let output = integer_field(usage, "output")?;
    let cache_read = integer_field(usage, "cacheRead")?;
    let cache_write = integer_field(usage, "cacheWrite")?;
    let total = integer_field(usage, "totalTokens")?;
    let normalized_input = input
        .checked_add(cache_read)
        .and_then(|value| value.checked_add(cache_write))
        .ok_or_else(|| source_error("第三方 Session v3 usage input sum overflows"))?;
    if normalized_input.checked_add(output) != Some(total) {
        return Err(source_error(
            "第三方 Session v3 usage total is inconsistent",
        ));
    }
    let cost = usage
        .get("cost")
        .and_then(Value::as_object)
        .ok_or_else(|| source_error("第三方 Session v3 usage cost is missing"))?;
    let amount = decimal_amount(
        cost.get("total")
            .ok_or_else(|| source_error("第三方 Session v3 usage total cost is missing"))?,
    )?;
    let reasoning = usage
        .get("reasoning")
        .map(|value| non_negative_integer(Some(value), "第三方 Session v3 reasoning tokens"))
        .transpose()?;
    let reasoning_json =
        reasoning.map_or_else(String::new, |value| format!(",\"reasoningTokens\":{value}"));
    Ok(format!(
        "{{\"inputTokens\":{normalized_input},\"outputTokens\":{output},\"totalTokens\":{total},\"cachedInputTokens\":{cache_read},\"cacheWriteTokens\":{cache_write}{reasoning_json},\"cost\":{{\"currency\":\"USD\",\"amount\":{}}}}}",
        json_string(&amount)?
    ))
}

/// 功能：把非负 第三方格式 numeric cost 转为最多 12 位小数的 portable amount 字符串。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn decimal_amount(value: &Value) -> Result<String, AgentError> {
    if let Some(integer) = value.as_u64() {
        return Ok(integer.to_string());
    }
    let number = value
        .as_f64()
        .filter(|number| number.is_finite() && *number >= 0.0)
        .ok_or_else(|| source_error("第三方 Session v3 usage cost is invalid"))?;
    let mut result = format!("{number:.12}");
    while result.ends_with('0') {
        result.pop();
    }
    if result.ends_with('.') {
        result.pop();
    }
    if result.is_empty() {
        result.push('0');
    }
    Ok(result)
}

/// 功能：校验 Provider/model/tool-call 标识满足 portable opaque ID 字符集。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_opaque_component(value: &str) -> Result<(), AgentError> {
    let valid = !value.is_empty()
        && value.len() <= 128
        && value.bytes().enumerate().all(|(index, byte)| {
            byte.is_ascii_alphanumeric() || (index > 0 && matches!(byte, b'-' | b'_' | b'.' | b':'))
        });
    if valid {
        Ok(())
    } else {
        Err(source_error(
            "第三方 Session v3 identity cannot map to portable opaque ID",
        ))
    }
}

/// 功能：校验惰性历史 tool call 使用安全 portable 工具名。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_tool_name(value: &str) -> Result<(), AgentError> {
    let valid = !value.is_empty()
        && value.len() <= 128
        && value.bytes().enumerate().all(|(index, byte)| {
            (index == 0 && byte.is_ascii_lowercase())
                || (index > 0
                    && (byte.is_ascii_lowercase()
                        || byte.is_ascii_digit()
                        || matches!(byte, b'_' | b'.' | b'-')))
        });
    if valid {
        Ok(())
    } else {
        Err(source_error("第三方 Session v3 tool name is invalid"))
    }
}

/// 功能：递归清除 secret-shaped key/value 与明显主机路径。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn sanitize_value(value: &Value) -> (Value, u64) {
    match value {
        Value::String(text) => {
            let (text, redacted) = sanitize_text(text);
            (Value::String(text), u64::from(redacted))
        }
        Value::Array(values) => {
            let mut redactions = 0_u64;
            let values = values
                .iter()
                .map(|value| {
                    let (value, count) = sanitize_value(value);
                    redactions = redactions.saturating_add(count);
                    value
                })
                .collect();
            (Value::Array(values), redactions)
        }
        Value::Object(values) => {
            if values.keys().any(|key| sensitive_key_regex().is_match(key)) {
                return (Value::String("[REDACTED]".to_owned()), 1);
            }
            let mut redactions = 0_u64;
            let values = values
                .iter()
                .map(|(key, value)| {
                    let (value, count) = sanitize_value(value);
                    redactions = redactions.saturating_add(count);
                    (key.clone(), value)
                })
                .collect();
            (Value::Object(values), redactions)
        }
        _ => (value.clone(), 0),
    }
}

/// 功能：清除单个 string 中的 credential/token 或明显 host path。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn sanitize_text(value: &str) -> (String, bool) {
    if sensitive_text_regex().is_match(value) || looks_like_host_path(value) {
        ("[REDACTED]".to_owned(), true)
    } else {
        (value.to_owned(), false)
    }
}

/// 功能：返回缓存的敏感键检测正则表达式。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn sensitive_key_regex() -> &'static Regex {
    static REGEX: OnceLock<Regex> = OnceLock::new();
    REGEX.get_or_init(|| {
        Regex::new(
            r"(?i)^(authorization|cookie|credential|password|passwd|secret|api[_-]?key|access[_-]?token|refresh[_-]?token)$",
        )
        .expect("static sensitive-key regex must compile")
    })
}

/// 功能：返回缓存的敏感值检测正则表达式。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn sensitive_text_regex() -> &'static Regex {
    static REGEX: OnceLock<Regex> = OnceLock::new();
    REGEX.get_or_init(|| {
        Regex::new(
            r"(?i)(bearer\s+[A-Za-z0-9._~+/=-]{8,}|sk-[A-Za-z0-9_-]{8,}|(?:api[_-]?key|password|secret|access[_-]?token)\s*[:=]\s*[^\s,;]+)",
        )
        .expect("static sensitive-value regex must compile")
    })
}

/// 功能：识别 POSIX/home/UNC/Windows drive 绝对主机路径。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn looks_like_host_path(value: &str) -> bool {
    value.starts_with('/')
        || value.starts_with("~/")
        || value.starts_with("\\\\")
        || (value.len() >= 3
            && value.as_bytes()[0].is_ascii_alphabetic()
            && value.as_bytes()[1] == b':'
            && matches!(value.as_bytes()[2], b'/' | b'\\'))
}

/// 功能：sanitize 任意 JSON 后编码，并累计 redaction 数。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn sanitized_value_json(value: &Value, redactions: &mut u64) -> Result<String, AgentError> {
    let (value, count) = sanitize_value(value);
    *redactions = redactions.saturating_add(count);
    serde_json::to_string(&value).map_err(internal_serialization_error)
}

/// 功能：sanitize 任意 JSON 并返回 compact JSON。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn sanitized_json(value: &Value, redactions: &mut u64) -> Result<String, AgentError> {
    sanitized_value_json(value, redactions)
}

/// 功能：按冻结字段顺序构造 compaction quarantine JSON。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn compaction_quarantine_json(
    object: &Map<String, Value>,
    redactions: &mut u64,
) -> Result<String, AgentError> {
    let details = sanitized_value_json(object.get("details").unwrap_or(&Value::Null), redactions)?;
    let from_hook = object
        .get("fromHook")
        .cloned()
        .unwrap_or(Value::Bool(false));
    Ok(format!(
        "{{\"details\":{details},\"fromHook\":{}}}",
        serde_json::to_string(&from_hook).map_err(internal_serialization_error)?
    ))
}

/// 功能：按冻结字段顺序构造 custom quarantine JSON。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn custom_quarantine_json(
    object: &Map<String, Value>,
    redactions: &mut u64,
) -> Result<String, AgentError> {
    Ok(format!(
        "{{\"customType\":{},\"data\":{}}}",
        sanitized_value_json(
            object
                .get("customType")
                .ok_or_else(|| source_error("第三方格式 custom type is missing"))?,
            redactions,
        )?,
        sanitized_value_json(object.get("data").unwrap_or(&Value::Null), redactions)?
    ))
}

/// 功能：按冻结字段顺序构造 custom_message quarantine JSON。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn custom_message_quarantine_json(
    object: &Map<String, Value>,
    redactions: &mut u64,
) -> Result<String, AgentError> {
    Ok(format!(
        "{{\"customType\":{},\"content\":{},\"display\":{},\"details\":{}}}",
        sanitized_value_json(
            object
                .get("customType")
                .ok_or_else(|| source_error("第三方格式 custom message type is missing"))?,
            redactions,
        )?,
        sanitized_value_json(object.get("content").unwrap_or(&Value::Null), redactions)?,
        sanitized_value_json(
            object.get("display").unwrap_or(&Value::Bool(false)),
            redactions
        )?,
        sanitized_value_json(object.get("details").unwrap_or(&Value::Null), redactions)?
    ))
}

/// 功能：按冻结字段顺序构造 label quarantine JSON。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn label_quarantine_json(
    object: &Map<String, Value>,
    redactions: &mut u64,
) -> Result<String, AgentError> {
    Ok(format!(
        "{{\"targetId\":{},\"label\":{}}}",
        sanitized_value_json(
            object
                .get("targetId")
                .ok_or_else(|| source_error("第三方格式 label target is missing"))?,
            redactions,
        )?,
        sanitized_value_json(object.get("label").unwrap_or(&Value::Null), redactions)?
    ))
}

/// 功能：删除 unknown entry 的 base/tree 字段后构造有界 quarantine JSON。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn unknown_quarantine_json(
    entry: &SourceEntry,
    redactions: &mut u64,
) -> Result<String, AgentError> {
    let mut value = entry
        .value
        .as_object()
        .cloned()
        .ok_or_else(|| source_error("第三方格式 unknown entry is not an object"))?;
    for key in ["type", "id", "parentId", "timestamp"] {
        value.remove(key);
    }
    sanitized_value_json(&Value::Object(value), redactions)
}

#[derive(Debug)]
struct StagedDirectory {
    path: PathBuf,
    published: bool,
}

impl StagedDirectory {
    /// 功能：创建与最终 Session 同父目录的唯一 staging directory。
    ///
    /// 输入：canonical sessions root 与已验证 target Session ID。
    /// 输出：失败时自动递归清理、成功发布后解除清理的 guard。
    /// 不变量：staging 名由随机 UUID 生成且不会等于 target 名。
    /// 失败：目录已碰撞或 I/O 错误时不创建 target。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn create(sessions_root: &Path, session_id: &str) -> Result<Self, AgentError> {
        let path = sessions_root.join(format!(".import-{session_id}-{}.tmp", Uuid::new_v4()));
        std::fs::create_dir(&path)?;
        Ok(Self {
            path,
            published: false,
        })
    }

    /// 功能：标记 staging 已原子 rename 为最终 target，阻止 Drop 清理已发布目录。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn mark_published(&mut self) {
        self.published = true;
    }
}

impl Drop for StagedDirectory {
    /// 功能：导入失败时尽力删除未发布 staging，不触碰 caller source 或既有 target。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn drop(&mut self) {
        if !self.published {
            let _ = std::fs::remove_dir_all(&self.path);
        }
    }
}

/// 功能：把完整 artifact/journal 写入 sibling staging，验证并原子发布新 Session。
///
/// 输入：canonical state root、target ID、完整 journal/report、可选图片 artifacts 和 source witness。
/// 输出：最终 `<state>/sessions/<sessionId>` 可被普通 Rust SessionStore 打开。
/// 不变量：artifact 先于 journal；每个文件 create-new+flush；source 最终复核先于 rename；不覆盖 target。
/// 失败：任一 I/O、hash、journal、隐私、source 竞态或 target 冲突时 staging 自动清理。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn publish_import(
    state_root: &Path,
    session_id: &str,
    journal: &[u8],
    report_artifact_id: &str,
    report: &[u8],
    artifacts: &[ArtifactBytes],
    source: &SourceDocument,
) -> Result<(), AgentError> {
    let sessions_root = state_root.join("sessions");
    std::fs::create_dir_all(&sessions_root)?;
    sync_directory(state_root)?;
    let sessions_root = sessions_root.canonicalize()?;
    let target = sessions_root.join(session_id);
    if target.exists() {
        return Err(
            AgentError::new(ErrorCode::InvalidParams, "target Session already exists")
                .with_details(serde_json::json!({"kind":"target_session_exists"})),
        );
    }
    let mut staging = StagedDirectory::create(&sessions_root, session_id)?;
    let artifact_root = staging.path.join("artifacts");
    std::fs::create_dir(&artifact_root)?;
    let mut artifact_ids = BTreeSet::new();
    for artifact in artifacts {
        validate_opaque_id(&artifact.artifact_id, "artifact ID")?;
        if !artifact_ids.insert(artifact.artifact_id.clone()) {
            return Err(internal_error(
                "第三方 Session v3 import generated duplicate artifact ID",
            ));
        }
        write_new_synced(&artifact_root.join(&artifact.artifact_id), &artifact.bytes)?;
    }
    if !artifact_ids.insert(report_artifact_id.to_owned()) {
        return Err(internal_error(
            "第三方 Session v3 report artifact ID was reused",
        ));
    }
    write_new_synced(&artifact_root.join(report_artifact_id), report)?;
    sync_directory(&artifact_root)?;
    write_new_synced(&staging.path.join("journal.jsonl"), journal)?;
    sync_directory(&staging.path)?;
    validate_staged_target(
        &staging.path,
        session_id,
        report_artifact_id,
        report,
        source,
    )?;
    source.verify_unchanged()?;
    if target.exists() {
        return Err(AgentError::new(
            ErrorCode::InvalidParams,
            "target Session appeared during import",
        )
        .with_details(serde_json::json!({"kind":"target_session_exists"})));
    }
    publish_directory_no_replace(&sessions_root, &staging.path, &target, session_id)?;
    if let Err(error) = sync_directory(&sessions_root) {
        let _ = std::fs::remove_dir_all(&target);
        let _ = sync_directory(&sessions_root);
        return Err(error);
    }
    staging.mark_published();
    Ok(())
}

/// 功能：在支持的平台以 no-replace rename 原子发布 staging directory。
///
/// 输入：共同父目录、staging/target 路径和 portable Session ID。
/// 输出：target 不存在时原子改名成功。
/// 不变量：Linux/glibc 使用 `RENAME_NOREPLACE` 消除 exists-check 竞态；绝不覆盖既有 target。
/// 失败：目标已存在返回稳定参数冲突；其他平台 I/O 失败不删除既有目录。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn publish_directory_no_replace(
    sessions_root: &Path,
    staging: &Path,
    _target: &Path,
    session_id: &str,
) -> Result<(), AgentError> {
    #[cfg(all(target_os = "linux", target_env = "gnu"))]
    {
        use nix::errno::Errno;
        use nix::fcntl::{RenameFlags, renameat2};

        let directory = File::open(sessions_root)?;
        let staging_name = staging
            .file_name()
            .ok_or_else(|| internal_error("第三方 Session v3 staging name is missing"))?;
        match renameat2(
            &directory,
            staging_name,
            &directory,
            session_id,
            RenameFlags::RENAME_NOREPLACE,
        ) {
            Ok(()) => Ok(()),
            Err(Errno::EEXIST) => Err(AgentError::new(
                ErrorCode::InvalidParams,
                "target Session already exists",
            )
            .with_details(serde_json::json!({"kind":"target_session_exists"}))),
            Err(_) => Err(AgentError::new(
                ErrorCode::IoError,
                "atomic Session publication failed",
            )),
        }
    }
    #[cfg(not(all(target_os = "linux", target_env = "gnu")))]
    {
        if _target.exists() {
            return Err(
                AgentError::new(ErrorCode::InvalidParams, "target Session already exists")
                    .with_details(serde_json::json!({"kind":"target_session_exists"})),
            );
        }
        std::fs::rename(staging, _target)?;
        Ok(())
    }
}

/// 功能：以 create-new 写入完整文件并执行 data/metadata flush。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn write_new_synced(path: &Path, bytes: &[u8]) -> Result<(), AgentError> {
    let mut file = OpenOptions::new().create_new(true).write(true).open(path)?;
    file.write_all(bytes)?;
    file.sync_all()?;
    Ok(())
}

/// 功能：在发布前用普通 Session parser、artifact binding 与隐私扫描验证 staging。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_staged_target(
    staging: &Path,
    session_id: &str,
    report_artifact_id: &str,
    report: &[u8],
    source: &SourceDocument,
) -> Result<(), AgentError> {
    let journal_path = staging.join("journal.jsonl");
    let journal = read_journal(&journal_path)?;
    if journal.header.session_id != session_id {
        return Err(internal_error("imported journal Session ID differs"));
    }
    let provenance = journal
        .header
        .provenance
        .as_ref()
        .ok_or_else(|| internal_error("imported journal provenance is missing"))?;
    if provenance.source != "pi-session-v3"
        || provenance.source_session_id.as_deref() != Some(source.session_id.as_str())
        || provenance.source_sha256 != source.sha256
        || provenance.reference_commit.as_deref() != Some(REFERENCE_COMMIT)
        || provenance.report_artifact_id.as_deref() != Some(report_artifact_id)
    {
        return Err(internal_error(
            "imported journal provenance binding differs",
        ));
    }
    if journal
        .records
        .iter()
        .any(|record| FORBIDDEN_IMPORTED_KINDS.contains(&record.kind.as_str()))
    {
        return Err(internal_error(
            "imported journal contains lifecycle records",
        ));
    }
    let report_records = journal
        .records
        .iter()
        .filter(|record| {
            record.kind == "artifact.created"
                && record
                    .data
                    .pointer("/artifact/artifactId")
                    .and_then(Value::as_str)
                    == Some(report_artifact_id)
        })
        .collect::<Vec<_>>();
    if report_records.len() != 1
        || report_records[0]
            .data
            .pointer("/artifact/mediaType")
            .and_then(Value::as_str)
            != Some(REPORT_MEDIA_TYPE)
        || report_records[0]
            .data
            .pointer("/artifact/byteLength")
            .and_then(Value::as_u64)
            != Some(report.len() as u64)
        || report_records[0]
            .data
            .pointer("/artifact/sha256")
            .and_then(Value::as_str)
            != Some(encode_lower_sha256(report).as_str())
    {
        return Err(internal_error("import report artifact binding differs"));
    }
    let report_text =
        std::str::from_utf8(report).map_err(|_| internal_error("import report is not UTF-8"))?;
    if !report_text.ends_with('\n') {
        return Err(internal_error("import report lacks final LF"));
    }
    let report_value = parse_strict_value(&report_text[..report_text.len() - 1])
        .map_err(|_| internal_error("import report is not strict JSON"))?;
    validate_json_limits(&report_value)?;
    assert_no_persisted_sensitive_value(&report_value)?;
    let source_path = source.path.to_string_lossy();
    if (!source_path.is_empty() && bytes_contain(report, source_path.as_bytes()))
        || (!source_path.is_empty()
            && bytes_contain(&std::fs::read(&journal_path)?, source_path.as_bytes()))
    {
        return Err(internal_error(
            "import target persisted the caller source path",
        ));
    }
    let artifact_path = staging.join("artifacts").join(report_artifact_id);
    let artifact_bytes = std::fs::read(artifact_path)?;
    if artifact_bytes != report {
        return Err(internal_error("import report artifact bytes differ"));
    }
    Ok(())
}

/// 功能：递归确认最终 report 不含 sensitive key/value 或明显 host path。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn assert_no_persisted_sensitive_value(value: &Value) -> Result<(), AgentError> {
    let mut stack = vec![value];
    let mut visited = 0_usize;
    while let Some(current) = stack.pop() {
        visited = visited.saturating_add(1);
        if visited > MAX_JSON_NODES {
            return Err(internal_error("import report node count exceeds limit"));
        }
        match current {
            Value::String(text)
                if sensitive_text_regex().is_match(text) || looks_like_host_path(text) =>
            {
                return Err(internal_error("import report contains sensitive data"));
            }
            Value::Object(values) => {
                if values.keys().any(|key| sensitive_key_regex().is_match(key)) {
                    return Err(internal_error("import report contains a sensitive key"));
                }
                stack.extend(values.values());
            }
            Value::Array(values) => stack.extend(values),
            _ => {}
        }
    }
    Ok(())
}

/// 功能：无分配检查一个字节切片是否包含另一个非空切片。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn bytes_contain(haystack: &[u8], needle: &[u8]) -> bool {
    !needle.is_empty()
        && haystack
            .windows(needle.len())
            .any(|window| window == needle)
}

#[cfg(test)]
mod tests {
    use std::fs;

    use tempfile::tempdir;

    use super::{
        LegacySessionV3ImportOptions, import_legacy_session_v3, parse_source_bytes, sanitize_value,
    };

    /// 功能：确认 source parser 拒绝重复键、CRLF 与 future parent。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn rejects_ambiguous_source_encodings_and_tree_edges() {
        let duplicate = b"{\"type\":\"session\",\"version\":3,\"version\":3,\"id\":\"s\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"cwd\":\"x\"}\n";
        assert!(parse_source_bytes(duplicate).is_err());
        let future_parent = b"{\"type\":\"session\",\"version\":3,\"id\":\"s\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"cwd\":\"x\"}\n{\"type\":\"future_entry\",\"id\":\"a\",\"parentId\":\"b\",\"timestamp\":\"2026-01-01T00:00:01Z\"}\n";
        assert!(parse_source_bytes(future_parent).is_err());
        let crlf = b"{\"type\":\"session\",\"version\":3,\"id\":\"s\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"cwd\":\"x\"}\r\n";
        assert!(parse_source_bytes(crlf).is_err());
    }

    /// 功能：确认 quarantine scanner 移除 secret-shaped key/value 与 host path。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn recursively_redacts_sensitive_quarantine_values() {
        let value = serde_json::json!({"nested":{"apiKey":"do-not-copy"}});
        let (sanitized, count) = sanitize_value(&value);
        assert_eq!(sanitized["nested"], "[REDACTED]");
        assert_eq!(count, 1);
        let (path, count) = sanitize_value(&serde_json::json!("/host/private/source"));
        assert_eq!(path, "[REDACTED]");
        assert_eq!(count, 1);
    }

    /// 功能：确认生产导入创建新 Session、报告 artifact，且重复 target 被拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn publishes_once_without_modifying_source() -> Result<(), crate::error::AgentError> {
        let directory = tempdir()?;
        let workspace = directory.path().join("workspace");
        let state = directory.path().join("state");
        fs::create_dir_all(&workspace)?;
        fs::create_dir_all(&state)?;
        let source = directory.path().join("source.jsonl");
        let source_bytes = b"{\"type\":\"session\",\"version\":3,\"id\":\"pi-source\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"cwd\":\"ignored\"}\n{\"type\":\"message\",\"id\":\"u1\",\"parentId\":null,\"timestamp\":\"2026-01-01T00:00:01Z\",\"message\":{\"role\":\"user\",\"content\":\"hello\"}}\n";
        fs::write(&source, source_bytes)?;
        let options = LegacySessionV3ImportOptions {
            source: source.clone(),
            workspace,
            state_root: state.clone(),
            session_id: Some("session-import-test".to_owned()),
            conformance: false,
        };
        let result = import_legacy_session_v3(options.clone()).await?;
        assert_eq!(result.session_id, "session-import-test");
        assert_eq!(fs::read(&source)?, source_bytes);
        assert!(
            state
                .join("sessions/session-import-test/journal.jsonl")
                .is_file()
        );
        assert!(import_legacy_session_v3(options).await.is_err());
        Ok(())
    }
}
