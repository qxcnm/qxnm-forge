//! 推广 Provider 本地安装状态与工作区外 CredentialStore。
//!
//! 作者：高宏顺 <18272669457@163.com>

use std::collections::{BTreeMap, BTreeSet};
use std::fs::{File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::Arc;
#[cfg(test)]
use std::sync::atomic::{AtomicUsize, Ordering};

#[cfg(windows)]
use std::os::windows::fs::{MetadataExt as _, OpenOptionsExt as _};

use async_trait::async_trait;
use base64::Engine as _;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use chrono::{SecondsFormat, Utc};
use fs2::FileExt as _;
use reqwest::Url;
use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};
use tokio_util::sync::CancellationToken;
use uuid::Uuid;

use crate::error::{AgentError, ErrorCode};
use crate::protocol::parse_strict_value;
use crate::provider::{
    AnthropicMessagesProvider, OpenAiChatProvider, OpenAiResponsesProvider, Provider,
    ProviderCredentialSource, ProviderRequest, ProviderStream, native_endpoint,
};
use crate::sponsored_catalog::{SponsoredProviderCatalog, SponsoredProviderEntry};

const SCHEMA_VERSION: &str = "0.1";
const MAX_CREDENTIAL_STORE_BYTES: usize = 16 * 1024 * 1024;
const MAX_CREDENTIAL_COUNT: usize = 128;
const MAX_CREDENTIAL_BYTES: usize = 16_384;
const CREDENTIAL_DIRECTORY_NAME: &str = "provider-credentials.d";
const CREDENTIAL_MIGRATION_DIRECTORY_NAME: &str = ".provider-credentials.d.migrating";
const LEGACY_CREDENTIAL_FILE_NAME: &str = "provider-credentials.json";
const CREDENTIAL_LEAF_SUFFIX: &str = ".credential";
const MAX_INSTALLATION_BYTES: usize = 512 * 1024;
#[cfg(windows)]
const FILE_FLAG_OPEN_REPARSE_POINT: u32 = 0x0020_0000;
#[cfg(windows)]
const FILE_FLAG_BACKUP_SEMANTICS: u32 = 0x0200_0000;

/// 在作用域结束时显式释放跨进程商业状态 writer lock。
struct CommercialFileLock(File);

impl Drop for CommercialFileLock {
    /// 功能：尽力释放文件锁；释放失败不覆盖原业务结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn drop(&mut self) {
        let _ = fs2::FileExt::unlock(&self.0);
    }
}

/// 一个仅保存在本机敏感状态文件中的 Provider API key。
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct StoredCredential {
    provider_id: String,
    api_key: String,
}

/// CredentialStore 的严格、有界 JSON 根对象。
#[derive(Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct CredentialDocument {
    schema_version: String,
    credentials: Vec<StoredCredential>,
}

/// 不含 secret、可由 Rust 与 .NET 共同读取的本地推广 route 快照。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct InstalledSponsoredRoute {
    pub entry_id: String,
    pub provider_id: String,
    pub display_name: String,
    pub api_family: String,
    pub api_base_url: String,
    pub catalog_version: u64,
    pub commission_disclosure: String,
    pub models: Vec<String>,
    pub installed_at: String,
}

/// 本地推广 route JSON 根对象。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct InstalledRouteDocument {
    schema_version: String,
    routes: Vec<InstalledSponsoredRoute>,
}

/// 工作区外、权限受限且按请求读取的文件型 Provider CredentialStore。
#[derive(Clone)]
pub struct ProviderCredentialStore {
    root: PathBuf,
    #[cfg(test)]
    request_read_count: Arc<AtomicUsize>,
}

impl ProviderCredentialStore {
    /// 功能：创建或打开工作区外 CredentialStore，并建立私有目录。
    ///
    /// 输入：用户状态根和当前 canonical workspace。
    /// 输出：完成必要格式升级、只保存固定状态路径的 store 句柄。
    /// 不变量：credential 根 canonical 后不得位于 workspace 内；Unix 目录权限收窄到 0700；
    /// v0.1 聚合文件仅在最终逐条目录不存在时于独占锁内读取一次并原子迁移，常规启动 presence 检查不打开任何 secret 叶文件。
    /// 失败：目录、canonical 路径、权限、迁移或边界验证失败时不返回半迁移 store。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn new(state_root: impl Into<PathBuf>, workspace: &Path) -> Result<Self, AgentError> {
        let state_root = state_root.into();
        create_private_directory(&state_root)?;
        let canonical_state_root = std::fs::canonicalize(&state_root)
            .map_err(|_| commercial_error("credential_path", "CredentialStore 路径无效"))?;
        let root = canonical_state_root.join("credentials");
        create_private_directory(&root)?;
        let canonical_root = std::fs::canonicalize(&root)
            .map_err(|_| commercial_error("credential_path", "CredentialStore 路径无效"))?;
        let canonical_workspace = std::fs::canonicalize(workspace)
            .map_err(|_| commercial_error("workspace", "workspace 路径无效"))?;
        if canonical_root.starts_with(&canonical_workspace) {
            return Err(commercial_error(
                "credential_workspace",
                "CredentialStore 必须位于 workspace 外",
            ));
        }
        let store = Self {
            root: canonical_root,
            #[cfg(test)]
            request_read_count: Arc::new(AtomicUsize::new(0)),
        };
        let _lock = store.acquire_lock()?;
        store.ensure_current_layout_unlocked()?;
        Ok(store)
    }

    /// 功能：从调用方内存写入或轮换一个 Provider API key。
    ///
    /// 输入：合法 Provider ID 和只从 stdin 获得的非空 key。
    /// 输出：独占锁内原子发布单个敏感叶文件。
    /// 不变量：Provider ID 仅以 canonical base64url 文件名出现；Unix 文件权限为 0600；值不进入错误、日志或返回对象。
    /// 失败：ID/key、容量、锁、symlink、权限或原子写入失败时安全拒绝并保留旧叶。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn set(&self, provider_id: &str, api_key: &str) -> Result<(), AgentError> {
        validate_provider_id(provider_id)?;
        validate_api_key(api_key)?;
        let _lock = self.acquire_lock()?;
        self.ensure_current_layout_unlocked()?;
        let configured = self.list_unlocked()?;
        if !configured.iter().any(|item| item == provider_id)
            && configured.len() >= MAX_CREDENTIAL_COUNT
        {
            return Err(commercial_error(
                "credential_shape",
                "CredentialStore 条目超过上限",
            ));
        }
        self.write_credential_leaf_unlocked(provider_id, api_key)
    }

    /// 功能：列出拥有本地 stored credential 的 Provider ID。
    ///
    /// 输入：当前 store 固定路径。
    /// 输出：按 ordinal 升序、不含 secret 的 ID 数组。
    /// 不变量：只枚举、解码并验证 canonical 叶名与文件元数据，绝不打开或反序列化 secret 正文。
    /// 失败：锁、目录、叶名、symlink/reparse、权限、数量或元数据异常时失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn list(&self) -> Result<Vec<String>, AgentError> {
        let _lock = self.acquire_lock()?;
        self.ensure_current_layout_unlocked()?;
        self.list_unlocked()
    }

    /// 功能：移除一个 Provider credential，且不返回旧 secret。
    ///
    /// 输入：合法 Provider ID。
    /// 输出：存在并删除时 true，不存在时 false。
    /// 不变量：更新始终在独占锁内 no-follow 删除精确 canonical 叶并同步目录；任何输出都不包含 key。
    /// 失败：锁、目录、文件安全、权限、删除或同步失败时安全拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn remove(&self, provider_id: &str) -> Result<bool, AgentError> {
        validate_provider_id(provider_id)?;
        let _lock = self.acquire_lock()?;
        self.ensure_current_layout_unlocked()?;
        let path = self.credential_path(provider_id);
        let metadata = match std::fs::symlink_metadata(&path) {
            Ok(metadata) => metadata,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(false),
            Err(_) => {
                return Err(commercial_error(
                    "file_metadata",
                    "CredentialStore 叶文件元数据读取失败",
                ));
            }
        };
        validate_sensitive_regular_metadata(&metadata, MAX_CREDENTIAL_BYTES, false)?;
        std::fs::remove_file(&path)
            .map_err(|_| commercial_error("file_remove", "CredentialStore 叶文件删除失败"))?;
        sync_directory(&self.credential_directory())?;
        Ok(true)
    }

    /// 功能：在 Provider 最终请求边界读取指定 API key 叶文件。
    ///
    /// 输入：固定本地 Provider ID。
    /// 输出：仅由最终 HTTP header 构造局部持有的 UTF-8 key。
    /// 不变量：精确叶 no-follow 打开并复核 regular/reparse/owner/mode/长度；stored source 失败或缺失时不回退环境变量。
    /// 失败：锁、目录、权限、叶名、正文 UTF-8/header 边界或目标缺失时返回脱敏 ProviderUnavailable。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn read_for_request(&self, provider_id: &str) -> Result<String, AgentError> {
        validate_provider_id(provider_id).map_err(|_| provider_unavailable())?;
        let _lock = self.acquire_lock().map_err(|_| provider_unavailable())?;
        self.ensure_current_layout_unlocked()
            .map_err(|_| provider_unavailable())?;
        #[cfg(test)]
        self.request_read_count.fetch_add(1, Ordering::SeqCst);
        let bytes = read_secure_file(
            &self.credential_path(provider_id),
            MAX_CREDENTIAL_BYTES,
            true,
        )
        .map_err(|_| provider_unavailable())?;
        let credential = String::from_utf8(bytes).map_err(|_| provider_unavailable())?;
        validate_api_key(&credential).map_err(|_| provider_unavailable())?;
        Ok(credential)
    }

    /// 功能：只根据 canonical 叶路径及安全元数据判断 stored credential presence。
    ///
    /// 输入：固定本地 Provider ID。
    /// 输出：目标叶存在且满足 regular/reparse/owner/mode/长度不变量时 true。
    /// 不变量：本方法不打开叶文件、不读取或反序列化 credential 正文。
    /// 失败：任何 ID、锁、迁移、目录或元数据异常安全映射为 false。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn contains(&self, provider_id: &str) -> bool {
        if validate_provider_id(provider_id).is_err() {
            return false;
        }
        let Ok(_lock) = self.acquire_lock() else {
            return false;
        };
        if self.ensure_current_layout_unlocked().is_err() {
            return false;
        }
        std::fs::symlink_metadata(self.credential_path(provider_id))
            .map_err(AgentError::from)
            .and_then(|metadata| {
                validate_sensitive_regular_metadata(&metadata, MAX_CREDENTIAL_BYTES, false)
            })
            .is_ok()
    }

    /// 功能：返回测试期间实际进入 secret 叶读取边界的次数。
    ///
    /// 输出：当前 clone 集合共享的单调计数。
    /// 不变量：list/contains/runtime snapshot 不增加；仅 `read_for_request` 在打开叶文件前增加。
    /// 失败：本方法不执行 I/O 且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(test)]
    pub(crate) fn request_read_count_for_test(&self) -> usize {
        self.request_read_count.load(Ordering::SeqCst)
    }

    /// 功能：以 create-new lock file 获取一次非阻塞独占文件锁。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn acquire_lock(&self) -> Result<CommercialFileLock, AgentError> {
        let path = self.root.join("provider-credentials.lock");
        reject_symlink_if_present(&path)?;
        let file = open_read_write_create(&path, true)?;
        file.try_lock_exclusive().map_err(|_| {
            commercial_error("credential_locked", "CredentialStore 正被其他进程使用")
        })?;
        Ok(CommercialFileLock(file))
    }

    /// 功能：返回 v0.2 逐 credential 私有叶目录的固定路径。
    ///
    /// 输出：只由 canonical state root 与常量 basename 组成的路径。
    /// 不变量：不读取文件系统或 secret。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn credential_directory(&self) -> PathBuf {
        self.root.join(CREDENTIAL_DIRECTORY_NAME)
    }

    /// 功能：把已验证 Provider ID 映射为 canonical base64url 敏感叶路径。
    ///
    /// 输入：已通过受限 ASCII 校验的 Provider ID。
    /// 输出：位于 v0.2 目录内、无 padding 的 URL-safe Base64 文件名。
    /// 不变量：Windows 保留名、尾点和路径分隔符不能由编码结果产生；路径不含 secret。
    /// 失败：调用方必须先验证 ID，本方法不执行 I/O。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn credential_path(&self, provider_id: &str) -> PathBuf {
        self.credential_directory()
            .join(credential_leaf_name(provider_id))
    }

    /// 功能：在独占锁内确保 v0.2 目录为唯一权威，并完成一次性 v0.1 聚合文件迁移或遗留清理。
    ///
    /// 输出：存在且已验证的逐 credential 私有目录。
    /// 不变量：最终目录存在时绝不解析 legacy 正文；最终目录缺失时才严格读取 v0.1，并通过私有 staging 全量同步后原子 rename。
    /// 失败：路径、权限、legacy JSON、叶写入、同步、rename 或清理异常时失败关闭；legacy 在最终目录发布前保持权威。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn ensure_current_layout_unlocked(&self) -> Result<(), AgentError> {
        let directory = self.credential_directory();
        match std::fs::symlink_metadata(&directory) {
            Ok(metadata) => {
                validate_private_directory_metadata(&metadata)?;
                self.remove_legacy_after_publication_unlocked()?;
                self.cleanup_orphaned_leaf_temporaries_unlocked()?;
                return Ok(());
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(_) => {
                return Err(commercial_error(
                    "directory",
                    "CredentialStore 目录元数据读取失败",
                ));
            }
        }

        self.cleanup_migration_directory_unlocked()?;
        let legacy_path = self.root.join(LEGACY_CREDENTIAL_FILE_NAME);
        match std::fs::symlink_metadata(&legacy_path) {
            Ok(_) => self.migrate_legacy_document_unlocked(&legacy_path),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                create_private_directory(&directory)?;
                sync_directory(&self.root)
            }
            Err(_) => Err(commercial_error(
                "file_metadata",
                "CredentialStore legacy 元数据读取失败",
            )),
        }
    }

    /// 功能：只枚举并验证 v0.2 叶名和安全元数据，投影排序后的 Provider ID。
    ///
    /// 输出：最多 128 个 canonical Provider ID。
    /// 不变量：不打开任何叶文件；未知文件、非 canonical 编码或不安全元数据使整个列表失败关闭。
    /// 失败：目录枚举、文件名、ID、数量、类型、owner/mode 或长度异常时返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn list_unlocked(&self) -> Result<Vec<String>, AgentError> {
        let directory = self.credential_directory();
        let metadata = std::fs::symlink_metadata(&directory)
            .map_err(|_| commercial_error("directory", "CredentialStore 目录无效"))?;
        validate_private_directory_metadata(&metadata)?;
        let mut provider_ids = Vec::new();
        for entry in std::fs::read_dir(&directory)
            .map_err(|_| commercial_error("directory_read", "CredentialStore 目录读取失败"))?
        {
            let entry = entry
                .map_err(|_| commercial_error("directory_read", "CredentialStore 目录读取失败"))?;
            let name = entry
                .file_name()
                .into_string()
                .map_err(|_| commercial_error("credential_leaf", "CredentialStore 叶名无效"))?;
            let provider_id = provider_id_from_credential_leaf_name(&name)?;
            let leaf_metadata = std::fs::symlink_metadata(entry.path()).map_err(|_| {
                commercial_error("file_metadata", "CredentialStore 叶文件元数据读取失败")
            })?;
            validate_sensitive_regular_metadata(&leaf_metadata, MAX_CREDENTIAL_BYTES, false)?;
            provider_ids.push(provider_id);
            if provider_ids.len() > MAX_CREDENTIAL_COUNT {
                return Err(commercial_error(
                    "credential_shape",
                    "CredentialStore 条目超过上限",
                ));
            }
        }
        provider_ids.sort();
        if provider_ids.windows(2).any(|items| items[0] == items[1]) {
            return Err(commercial_error(
                "credential_duplicate",
                "CredentialStore Provider ID 重复",
            ));
        }
        Ok(provider_ids)
    }

    /// 功能：在独占锁内以根目录临时文件原子写入或轮换一个 v0.2 credential 叶。
    ///
    /// 输入：已验证 Provider ID 与 header-safe UTF-8 key。
    /// 输出：目标叶及其目录 durable 后成功。
    /// 不变量：临时文件固定 0600 且位于同一文件系统；目标既有条目必须是安全普通文件；失败保留旧叶并尽力清理临时文件。
    /// 失败：目标元数据、临时创建、写入、同步、rename 或目录同步异常时返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn write_credential_leaf_unlocked(
        &self,
        provider_id: &str,
        api_key: &str,
    ) -> Result<(), AgentError> {
        let directory = self.credential_directory();
        let path = self.credential_path(provider_id);
        match std::fs::symlink_metadata(&path) {
            Ok(metadata) => {
                validate_sensitive_regular_metadata(&metadata, MAX_CREDENTIAL_BYTES, false)?;
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(_) => {
                return Err(commercial_error(
                    "file_metadata",
                    "CredentialStore 叶文件元数据读取失败",
                ));
            }
        }
        let temporary = self
            .root
            .join(format!(".provider-credential-{}.tmp", Uuid::new_v4()));
        let result = (|| {
            write_new_sensitive_file(&temporary, api_key.as_bytes())?;
            atomic_replace_credential_leaf(&temporary, &path)
                .map_err(|_| commercial_error("file_publish", "CredentialStore 叶文件发布失败"))?;
            sync_directory(&directory)?;
            sync_directory(&self.root)
        })();
        if result.is_err() {
            let _ = std::fs::remove_file(&temporary);
            let _ = sync_directory(&self.root);
        }
        result
    }

    /// 功能：把唯一权威的 legacy v0.1 聚合文档迁移为 v0.2 逐 credential 叶目录。
    ///
    /// 输入：固定 legacy 文件路径；调用方持有全局 credential lock 且最终目录不存在。
    /// 输出：全部叶、staging、最终 rename 与父目录均同步，随后 legacy 文件删除并同步。
    /// 不变量：所有 legacy 字段先严格验证；发布前 legacy 始终权威；发布后最终目录唯一权威，删除失败可由下次启动只按元数据重试。
    /// 失败：legacy 安全读取/JSON、staging、叶写入、同步、rename 或删除失败时返回脱敏错误，不把 key 放入错误或日志。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn migrate_legacy_document_unlocked(&self, legacy_path: &Path) -> Result<(), AgentError> {
        let bytes = read_secure_file(legacy_path, MAX_CREDENTIAL_STORE_BYTES, true)?;
        let document: CredentialDocument = parse_strict_json(&bytes, "credential_json")?;
        validate_credential_document(&document)?;
        let staging = self.root.join(CREDENTIAL_MIGRATION_DIRECTORY_NAME);
        create_private_directory(&staging)?;
        let result = (|| {
            for entry in &document.credentials {
                write_new_sensitive_file(
                    &staging.join(credential_leaf_name(&entry.provider_id)),
                    entry.api_key.as_bytes(),
                )?;
            }
            sync_directory(&staging)?;
            std::fs::rename(&staging, self.credential_directory()).map_err(|_| {
                commercial_error("credential_migration", "CredentialStore 迁移发布失败")
            })?;
            sync_directory(&self.root)?;
            std::fs::remove_file(legacy_path)
                .map_err(|_| commercial_error("file_remove", "CredentialStore legacy 删除失败"))?;
            sync_directory(&self.root)
        })();
        if result.is_err() {
            let _ = self.cleanup_migration_directory_unlocked();
        }
        result
    }

    /// 功能：最终 v0.2 目录已存在时，仅按元数据清理遗留 legacy 聚合文件。
    ///
    /// 输出：legacy 不存在，且若发生删除则父目录已同步。
    /// 不变量：绝不打开、读取或解析 legacy 正文；只删除固定路径的安全普通文件。
    /// 失败：链接/reparse、类型、owner/mode、大小、删除或同步异常时失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn remove_legacy_after_publication_unlocked(&self) -> Result<(), AgentError> {
        let path = self.root.join(LEGACY_CREDENTIAL_FILE_NAME);
        let metadata = match std::fs::symlink_metadata(&path) {
            Ok(metadata) => metadata,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(()),
            Err(_) => {
                return Err(commercial_error(
                    "file_metadata",
                    "CredentialStore legacy 元数据读取失败",
                ));
            }
        };
        validate_sensitive_regular_metadata(&metadata, MAX_CREDENTIAL_STORE_BYTES, true)?;
        std::fs::remove_file(&path)
            .map_err(|_| commercial_error("file_remove", "CredentialStore legacy 删除失败"))?;
        sync_directory(&self.root)
    }

    /// 功能：在最终目录尚未发布时安全清理上次崩溃遗留的固定 migration staging。
    ///
    /// 输出：staging 不存在且父目录已同步，或原本不存在。
    /// 不变量：只删除 canonical credential 叶名对应的安全普通文件；未知条目和链接一律拒绝，不递归跟随。
    /// 失败：目录/叶元数据、名称、权限、删除或同步异常时失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn cleanup_migration_directory_unlocked(&self) -> Result<(), AgentError> {
        let staging = self.root.join(CREDENTIAL_MIGRATION_DIRECTORY_NAME);
        let metadata = match std::fs::symlink_metadata(&staging) {
            Ok(metadata) => metadata,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(()),
            Err(_) => {
                return Err(commercial_error(
                    "directory",
                    "CredentialStore migration 目录无效",
                ));
            }
        };
        validate_private_directory_metadata(&metadata)?;
        for entry in std::fs::read_dir(&staging).map_err(|_| {
            commercial_error("directory_read", "CredentialStore migration 目录读取失败")
        })? {
            let entry = entry.map_err(|_| {
                commercial_error("directory_read", "CredentialStore migration 目录读取失败")
            })?;
            let name = entry.file_name().into_string().map_err(|_| {
                commercial_error("credential_leaf", "CredentialStore migration 叶名无效")
            })?;
            let _ = provider_id_from_credential_leaf_name(&name)?;
            let leaf_metadata = std::fs::symlink_metadata(entry.path()).map_err(|_| {
                commercial_error("file_metadata", "CredentialStore migration 叶元数据无效")
            })?;
            validate_sensitive_regular_metadata(&leaf_metadata, MAX_CREDENTIAL_BYTES, true)?;
            std::fs::remove_file(entry.path()).map_err(|_| {
                commercial_error("file_remove", "CredentialStore migration 叶删除失败")
            })?;
        }
        std::fs::remove_dir(&staging).map_err(|_| {
            commercial_error("directory_remove", "CredentialStore migration 目录删除失败")
        })?;
        sync_directory(&self.root)
    }

    /// 功能：清理同一全局锁下因进程崩溃遗留在 credential 根的敏感原子写临时文件。
    ///
    /// 输出：所有受控临时文件被删除并在需要时同步根目录。
    /// 不变量：只匹配本实现固定前后缀并要求安全普通文件；其他状态项完全保留。
    /// 失败：匹配项元数据、权限、删除或目录同步异常时失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn cleanup_orphaned_leaf_temporaries_unlocked(&self) -> Result<(), AgentError> {
        let mut removed = false;
        for entry in std::fs::read_dir(&self.root)
            .map_err(|_| commercial_error("directory_read", "CredentialStore 根目录读取失败"))?
        {
            let entry = entry.map_err(|_| {
                commercial_error("directory_read", "CredentialStore 根目录读取失败")
            })?;
            let Some(name) = entry.file_name().to_str().map(str::to_owned) else {
                continue;
            };
            if !name.starts_with(".provider-credential-") || !name.ends_with(".tmp") {
                continue;
            }
            let metadata = std::fs::symlink_metadata(entry.path()).map_err(|_| {
                commercial_error("file_metadata", "CredentialStore 临时文件元数据无效")
            })?;
            validate_sensitive_regular_metadata(&metadata, MAX_CREDENTIAL_BYTES, true)?;
            std::fs::remove_file(entry.path())
                .map_err(|_| commercial_error("file_remove", "CredentialStore 临时文件删除失败"))?;
            removed = true;
        }
        if removed {
            sync_directory(&self.root)?;
        }
        Ok(())
    }
}

/// 管理不含 secret 的已安装推广 Provider route。
#[derive(Clone)]
pub struct InstalledSponsoredRouteStore {
    root: PathBuf,
}

impl InstalledSponsoredRouteStore {
    /// 功能：创建指向用户状态根中固定 commercial 子目录的 route store。
    ///
    /// 输入：用户状态根。
    /// 输出：尚未读取或创建文件的轻量句柄。
    /// 不变量：路径不由远程目录字段控制。
    /// 失败：本方法不访问文件系统且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn new(state_root: impl Into<PathBuf>) -> Self {
        Self {
            root: state_root.into().join("commercial"),
        }
    }

    /// 功能：把当前已验签 catalog 中一个条目明确固定为本地 route。
    ///
    /// 输入：已验证 catalog、entry ID 和用户选择的单个 model ID。
    /// 输出：新安装或显式替换后的 route 快照。
    /// 不变量：providerId 固定派生为 relay-entryId；不复制 signup URL 或 secret。
    /// 失败：条目缺失、模型/route 非法、锁或写入失败时不安装。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn install(
        &self,
        catalog: &SponsoredProviderCatalog,
        entry_id: &str,
        model_id: &str,
    ) -> Result<InstalledSponsoredRoute, AgentError> {
        let entry = catalog
            .entries
            .iter()
            .find(|entry| entry.id == entry_id)
            .ok_or_else(|| commercial_error("entry_missing", "推广目录中没有该条目"))?;
        let route = route_from_entry(catalog.catalog_version, entry, model_id)?;
        create_private_directory(&self.root)?;
        let _lock = self.acquire_lock()?;
        let mut document = self.read_document_unlocked()?;
        document.routes.retain(|item| item.entry_id != entry_id);
        document.routes.push(route.clone());
        document
            .routes
            .sort_by(|left, right| left.entry_id.cmp(&right.entry_id));
        self.write_document_unlocked(&document)?;
        Ok(route)
    }

    /// 功能：列出所有本地固定的非敏感推广 route。
    ///
    /// 输入：固定安装文件。
    /// 输出：按 entryId 升序的防御性列表。
    /// 不变量：输出不含 API key。
    /// 失败：锁、symlink、JSON 或 route 验证失败时拒绝全部列表。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn list(&self) -> Result<Vec<InstalledSponsoredRoute>, AgentError> {
        if !self.root.exists() {
            return Ok(Vec::new());
        }
        let _lock = self.acquire_lock()?;
        Ok(self.read_document_unlocked()?.routes)
    }

    /// 功能：按 entryId 移除一个已安装 route，credential 保持独立不自动删除。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn remove(&self, entry_id: &str) -> Result<bool, AgentError> {
        validate_entry_id(entry_id)?;
        create_private_directory(&self.root)?;
        let _lock = self.acquire_lock()?;
        let mut document = self.read_document_unlocked()?;
        let before = document.routes.len();
        document.routes.retain(|route| route.entry_id != entry_id);
        if document.routes.len() == before {
            return Ok(false);
        }
        self.write_document_unlocked(&document)?;
        Ok(true)
    }

    /// 功能：为 CLI 的 provider/model 选择解析本地推广 API family。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn resolve_family(
        &self,
        provider_id: &str,
        model_id: &str,
    ) -> Result<Option<String>, AgentError> {
        Ok(self
            .list()?
            .into_iter()
            .find(|route| {
                route.provider_id == provider_id
                    && route.models.iter().any(|model| model == model_id)
            })
            .map(|route| route.api_family))
    }

    /// 功能：从已安装 route 和 stored credentials 构造复用既有 family parser 的 Provider。
    ///
    /// 输入：工作区外 CredentialStore。
    /// 输出：按 providerId|family 索引的本地 adapter；缺 credential 的 route 保留为不可用以便接受前稳定拒绝。
    /// 不变量：adapter 只持有 store 路径/Provider ID，不持有 key；endpoint 同源追加固定 family path。
    /// 失败：安装文件或 endpoint 无效时整个本地 route 集合失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn build_providers(
        &self,
        credentials: &ProviderCredentialStore,
    ) -> Result<BTreeMap<String, Arc<dyn Provider>>, AgentError> {
        let mut providers = BTreeMap::<String, Arc<dyn Provider>>::new();
        for route in self.list()? {
            let source = ProviderCredentialSource::from_store(
                credentials.clone(),
                route.provider_id.clone(),
            );
            let suffix = match route.api_family.as_str() {
                "openai-completions" => "/chat/completions",
                "openai-responses" => "/responses",
                "anthropic-messages" => "/messages",
                _ => {
                    return Err(commercial_error(
                        "route_family",
                        "已安装推广 route family 无效",
                    ));
                }
            };
            let endpoint = native_endpoint(&route.api_base_url, suffix, &[])?.to_string();
            let inner: Arc<dyn Provider> = match route.api_family.as_str() {
                "openai-completions" => Arc::new(OpenAiChatProvider::with_credential_source(
                    &route.provider_id,
                    endpoint,
                    source,
                )),
                "openai-responses" => Arc::new(OpenAiResponsesProvider::with_credential_source(
                    &route.provider_id,
                    endpoint,
                    source,
                )),
                "anthropic-messages" => {
                    Arc::new(AnthropicMessagesProvider::with_credential_source(
                        &route.provider_id,
                        endpoint,
                        source,
                    ))
                }
                _ => unreachable!("family checked above"),
            };
            let key = format!("{}|{}", route.provider_id, route.api_family);
            providers.insert(
                key,
                Arc::new(InstalledRouteProvider {
                    route,
                    credentials: credentials.clone(),
                    inner,
                }),
            );
        }
        Ok(providers)
    }

    /// 功能：取得非敏感安装文件的固定公开路径，供测试验证跨语言格式。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn document_path(&self) -> PathBuf {
        self.root.join("installed-sponsored-routes.json")
    }

    /// 功能：获取非敏感安装文件的非阻塞独占 writer lock。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn acquire_lock(&self) -> Result<CommercialFileLock, AgentError> {
        let path = self.root.join("installed-sponsored-routes.lock");
        reject_symlink_if_present(&path)?;
        let file = open_read_write_create(&path, false)?;
        file.try_lock_exclusive()
            .map_err(|_| commercial_error("route_locked", "推广 route 正被其他进程使用"))?;
        Ok(CommercialFileLock(file))
    }

    /// 功能：在独占锁内读取并严格验证非敏感 route 文档。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn read_document_unlocked(&self) -> Result<InstalledRouteDocument, AgentError> {
        let path = self.document_path();
        if !path.exists() {
            return Ok(InstalledRouteDocument {
                schema_version: SCHEMA_VERSION.to_owned(),
                routes: Vec::new(),
            });
        }
        let bytes = read_secure_file(&path, MAX_INSTALLATION_BYTES, false)?;
        let document: InstalledRouteDocument = parse_strict_json(&bytes, "route_json")?;
        validate_route_document(&document)?;
        Ok(document)
    }

    /// 功能：在独占锁内原子发布排序后的非敏感 route 文档。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn write_document_unlocked(&self, document: &InstalledRouteDocument) -> Result<(), AgentError> {
        validate_route_document(document)?;
        let mut bytes = serde_json::to_vec(document)
            .map_err(|_| commercial_error("route_json", "推广 route 序列化失败"))?;
        bytes.push(b'\n');
        atomic_write(&self.document_path(), &bytes, false)
    }
}

/// 把本地固定 route 语义包裹在共享 API-family Provider 外层。
struct InstalledRouteProvider {
    route: InstalledSponsoredRoute,
    credentials: ProviderCredentialStore,
    inner: Arc<dyn Provider>,
}

#[async_trait]
impl Provider for InstalledRouteProvider {
    /// 功能：返回本地安装时固定的品牌中立 Provider ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn id(&self) -> &str {
        &self.route.provider_id
    }

    /// 功能：返回本地安装时固定的 API family。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn api_family(&self) -> Option<&str> {
        Some(&self.route.api_family)
    }

    /// 功能：只接受安装 snapshot 中显式固定的模型。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn supports_model(&self, model_id: &str) -> bool {
        self.route.models.iter().any(|model| model == model_id)
    }

    /// 功能：在 durable Session 副作用前重新检查 stored credential。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn is_available(&self) -> bool {
        self.credentials.contains(&self.route.provider_id)
    }

    /// 功能：把已验证请求委托给现有 family adapter，并保持取消与流顺序。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn stream(
        &self,
        request: ProviderRequest,
        cancellation: CancellationToken,
    ) -> Result<ProviderStream, AgentError> {
        if !self.is_available() {
            return Err(provider_unavailable());
        }
        self.inner.stream(request, cancellation).await
    }
}

/// 功能：从已验证 catalog entry 构造不含 secret 的本地 route 快照。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn route_from_entry(
    catalog_version: u64,
    entry: &SponsoredProviderEntry,
    model_id: &str,
) -> Result<InstalledSponsoredRoute, AgentError> {
    validate_model(model_id)?;
    let route = InstalledSponsoredRoute {
        entry_id: entry.id.clone(),
        provider_id: format!("relay-{}", entry.id),
        display_name: entry.display_name.clone(),
        api_family: entry.api_family.clone(),
        api_base_url: entry.api_base_url.clone(),
        catalog_version,
        commission_disclosure: entry.commission_disclosure.clone(),
        models: vec![model_id.to_owned()],
        installed_at: Utc::now().to_rfc3339_opts(SecondsFormat::Millis, true),
    };
    validate_route(&route)?;
    Ok(route)
}

/// 功能：验证 credential 文档版本、数量、唯一 ID 和所有 secret header 边界。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_credential_document(document: &CredentialDocument) -> Result<(), AgentError> {
    if document.schema_version != SCHEMA_VERSION
        || document.credentials.len() > MAX_CREDENTIAL_COUNT
    {
        return Err(commercial_error(
            "credential_shape",
            "CredentialStore 文档无效",
        ));
    }
    let mut ids = BTreeSet::new();
    for entry in &document.credentials {
        validate_provider_id(&entry.provider_id)?;
        validate_api_key(&entry.api_key)?;
        if !ids.insert(&entry.provider_id) {
            return Err(commercial_error(
                "credential_duplicate",
                "CredentialStore Provider ID 重复",
            ));
        }
    }
    Ok(())
}

/// 功能：把合法 Provider ID 编码为跨平台 canonical credential 叶文件名。
///
/// 输入：已经通过受限 ASCII 语法校验的 Provider ID。
/// 输出：URL-safe、无 padding 的 Base64 文件名及固定 `.credential` 后缀。
/// 不变量：结果不含路径分隔符、Windows 保留 basename 或尾点；不包含 secret。
/// 失败：调用方必须先验证 ID，本函数不执行 I/O。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn credential_leaf_name(provider_id: &str) -> String {
    format!(
        "{}{}",
        URL_SAFE_NO_PAD.encode(provider_id.as_bytes()),
        CREDENTIAL_LEAF_SUFFIX
    )
}

/// 功能：从 credential 叶名严格恢复并复核 canonical Provider ID。
///
/// 输入：不可信目录项文件名。
/// 输出：通过语法校验且重新编码完全一致的 Provider ID。
/// 不变量：拒绝 padding、大小写/别名编码、非 UTF-8、未知后缀和路径语义；不读取文件正文。
/// 失败：任何非 canonical 形状返回脱敏 credential_leaf 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn provider_id_from_credential_leaf_name(name: &str) -> Result<String, AgentError> {
    let encoded = name
        .strip_suffix(CREDENTIAL_LEAF_SUFFIX)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| commercial_error("credential_leaf", "CredentialStore 叶名无效"))?;
    let bytes = URL_SAFE_NO_PAD
        .decode(encoded)
        .map_err(|_| commercial_error("credential_leaf", "CredentialStore 叶名无效"))?;
    let provider_id = String::from_utf8(bytes)
        .map_err(|_| commercial_error("credential_leaf", "CredentialStore 叶名无效"))?;
    validate_provider_id(&provider_id)
        .map_err(|_| commercial_error("credential_leaf", "CredentialStore 叶名无效"))?;
    if credential_leaf_name(&provider_id) != name {
        return Err(commercial_error(
            "credential_leaf",
            "CredentialStore 叶名不是 canonical 编码",
        ));
    }
    Ok(provider_id)
}

/// 功能：验证安装文档版本、数量、顺序无关唯一身份和所有 route。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_route_document(document: &InstalledRouteDocument) -> Result<(), AgentError> {
    if document.schema_version != SCHEMA_VERSION || document.routes.len() > 64 {
        return Err(commercial_error("route_shape", "已安装推广 route 文档无效"));
    }
    let mut entries = BTreeSet::new();
    let mut providers = BTreeSet::new();
    for route in &document.routes {
        validate_route(route)?;
        if !entries.insert(&route.entry_id) || !providers.insert(&route.provider_id) {
            return Err(commercial_error(
                "route_duplicate",
                "已安装推广 route 身份重复",
            ));
        }
    }
    Ok(())
}

/// 功能：验证单条本地推广 route 的身份、HTTPS、family、模型、时间和披露边界。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_route(route: &InstalledSponsoredRoute) -> Result<(), AgentError> {
    validate_entry_id(&route.entry_id)?;
    validate_provider_id(&route.provider_id)?;
    if route.provider_id != format!("relay-{}", route.entry_id)
        || route.catalog_version == 0
        || route.catalog_version > 9_007_199_254_740_991
        || route.models.is_empty()
        || route.models.len() > 32
        || !matches!(
            route.api_family.as_str(),
            "openai-completions" | "openai-responses" | "anthropic-messages"
        )
    {
        return Err(commercial_error("route_field", "已安装推广 route 字段无效"));
    }
    validate_public_text(&route.display_name, 80)?;
    validate_public_text(&route.commission_disclosure, 240)?;
    validate_https_base(&route.api_base_url)?;
    if !route.installed_at.ends_with('Z')
        || chrono::DateTime::parse_from_rfc3339(&route.installed_at).is_err()
    {
        return Err(commercial_error("route_time", "已安装推广 route 时间无效"));
    }
    let mut models = BTreeSet::new();
    for model in &route.models {
        validate_model(model)?;
        if !models.insert(model) {
            return Err(commercial_error("route_model", "已安装推广 route 模型重复"));
        }
    }
    Ok(())
}

/// 功能：验证品牌中立 Provider ID 的 ASCII 语法。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_provider_id(value: &str) -> Result<(), AgentError> {
    if value.len() > 128
        || value.is_empty()
        || !value
            .bytes()
            .next()
            .is_some_and(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit())
        || !value.bytes().all(|byte| {
            byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'-' || byte == b'.'
        })
    {
        return Err(commercial_error("provider_id", "Provider ID 无效"));
    }
    Ok(())
}

/// 功能：验证远程 entry ID 的受限 ASCII 语法。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_entry_id(value: &str) -> Result<(), AgentError> {
    if value.len() > 64 {
        return Err(commercial_error("entry_id", "推广 entry ID 无效"));
    }
    validate_provider_id(value)
}

/// 功能：验证 API key 非空、有界且不能注入 HTTP header。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_api_key(value: &str) -> Result<(), AgentError> {
    if value.is_empty() || value.len() > 16_384 || value.contains(['\r', '\n', '\0']) {
        return Err(commercial_error(
            "credential_value",
            "Provider credential 无效",
        ));
    }
    Ok(())
}

/// 功能：验证用户选择的模型 ID 是单行、有界且非空的公开文本。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_model(value: &str) -> Result<(), AgentError> {
    if value.is_empty() || value.len() > 256 || value.chars().any(char::is_control) {
        return Err(commercial_error("model_id", "Provider model ID 无效"));
    }
    Ok(())
}

/// 功能：验证安装快照中的公开展示文本没有终端控制字符。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_public_text(value: &str, maximum: usize) -> Result<(), AgentError> {
    if value.is_empty() || value.len() > maximum || value.chars().any(char::is_control) {
        return Err(commercial_error("display_text", "推广 route 展示文本无效"));
    }
    Ok(())
}

/// 功能：验证 API base 为无 userinfo/query/fragment 的 HTTPS URL。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_https_base(value: &str) -> Result<(), AgentError> {
    let url =
        Url::parse(value).map_err(|_| commercial_error("route_url", "推广 route API base 无效"))?;
    if value.len() > 2048
        || url.scheme() != "https"
        || url.host_str().is_none()
        || !url.username().is_empty()
        || url.password().is_some()
        || url.query().is_some()
        || url.fragment().is_some()
    {
        return Err(commercial_error("route_url", "推广 route API base 无效"));
    }
    Ok(())
}

/// 功能：仅用 no-follow 元数据验证 CredentialStore 私有目录。
///
/// 输入：由 `symlink_metadata` 或 no-follow handle 取得的目录元数据。
/// 输出：普通、非 reparse 且属于当前用户的私有目录通过。
/// 不变量：Unix 拒绝非当前有效用户 owner 或 group/other 任意权限；Windows 拒绝 device/reparse。
/// 失败：类型、owner 或 mode 不安全时返回脱敏目录错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_private_directory_metadata(metadata: &std::fs::Metadata) -> Result<(), AgentError> {
    if metadata_is_unsafe(metadata) || !metadata.is_dir() {
        return Err(commercial_error(
            "directory_shape",
            "CredentialStore 目录不是安全普通目录",
        ));
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::{MetadataExt as _, PermissionsExt as _};
        if metadata.uid() != nix::unistd::geteuid().as_raw()
            || metadata.permissions().mode() & 0o777 != 0o700
        {
            return Err(commercial_error(
                "directory_permissions",
                "CredentialStore 目录权限无效",
            ));
        }
    }
    Ok(())
}

/// 功能：仅用 no-follow 元数据验证敏感 credential 普通文件的类型、owner、mode 与长度。
///
/// 输入：文件元数据、最大字节数及清理路径是否允许零长度崩溃残留。
/// 输出：满足全部安全边界时成功。
/// 不变量：不打开或读取正文；Unix 仅接受当前有效用户且无 group/other 权限，Windows 拒绝 device/reparse。
/// 失败：链接、类型、owner、mode、空值或上限违规时返回脱敏文件错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_sensitive_regular_metadata(
    metadata: &std::fs::Metadata,
    maximum: usize,
    allow_empty: bool,
) -> Result<(), AgentError> {
    if metadata_is_unsafe(metadata)
        || !metadata.is_file()
        || (!allow_empty && metadata.len() == 0)
        || metadata.len() > maximum as u64
    {
        return Err(commercial_error(
            "file_shape",
            "CredentialStore 敏感文件形状无效",
        ));
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::{MetadataExt as _, PermissionsExt as _};
        if metadata.uid() != nix::unistd::geteuid().as_raw()
            || metadata.permissions().mode() & 0o777 != 0o600
        {
            return Err(commercial_error(
                "file_permissions",
                "CredentialStore 文件权限过宽或 owner 无效",
            ));
        }
    }
    Ok(())
}

/// 功能：以 create-new/no-follow 语义写入并同步一个新的敏感 credential 文件。
///
/// 输入：固定私有目录中的目标路径与已验证非空、最多 16 KiB 的 key 字节。
/// 输出：0600 普通文件完整写入并 `sync_all` 后成功。
/// 不变量：绝不覆盖既有路径；失败尽力删除本次新文件；错误不包含正文或路径。
/// 失败：空值/上限、创建、写入、同步或最终元数据复核异常时返回脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn write_new_sensitive_file(path: &Path, bytes: &[u8]) -> Result<(), AgentError> {
    if bytes.is_empty() || bytes.len() > MAX_CREDENTIAL_BYTES {
        return Err(commercial_error(
            "credential_value",
            "Provider credential 无效",
        ));
    }
    reject_symlink_if_present(path)?;
    let mut options = OpenOptions::new();
    options.create_new(true).write(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt as _;
        options.custom_flags(libc::O_NOFOLLOW).mode(0o600);
    }
    #[cfg(windows)]
    options.custom_flags(FILE_FLAG_OPEN_REPARSE_POINT);
    let result = (|| {
        let mut file = options
            .open(path)
            .map_err(|_| commercial_error("file_create", "CredentialStore 叶文件创建失败"))?;
        file.write_all(bytes)
            .map_err(|_| commercial_error("file_write", "CredentialStore 叶文件写入失败"))?;
        file.sync_all()
            .map_err(|_| commercial_error("file_sync", "CredentialStore 叶文件同步失败"))?;
        let metadata = file.metadata().map_err(|_| {
            commercial_error("file_metadata", "CredentialStore 叶文件元数据读取失败")
        })?;
        validate_sensitive_regular_metadata(&metadata, MAX_CREDENTIAL_BYTES, false)
    })();
    if result.is_err() {
        let _ = std::fs::remove_file(path);
    }
    result
}

/// 功能：原子替换同目录 credential 叶，并在 Windows 请求 replace-existing 与 write-through。
///
/// 输入：已完整同步的临时文件与同一文件系统中的 canonical 目标叶。
/// 输出：目标目录项原子指向新文件。
/// 不变量：Unix 使用 rename replacement；Windows 使用 MoveFileExW(REPLACE_EXISTING|WRITE_THROUGH)。
/// 失败：平台原子替换失败时返回原始 I/O 错误，由调用方脱敏映射。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn atomic_replace_credential_leaf(source: &Path, destination: &Path) -> std::io::Result<()> {
    #[cfg(windows)]
    {
        atomic_replace_credential_leaf_windows(source, destination)
    }
    #[cfg(not(windows))]
    {
        std::fs::rename(source, destination)
    }
}

/// 功能：通过 Windows MoveFileExW 原子轮换已存在或新建的 credential 叶。
///
/// 输入：同一文件系统内的源/目标路径。
/// 输出：replace-existing 且 write-through 的目录项更新。
/// 不变量：UTF-16 缓冲均 NUL 结尾并在 FFI 返回前存活；调用方已持有全局锁并验证目标元数据。
/// 失败：Win32 返回零时转换为 last_os_error，不包含路径或 secret。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[cfg(windows)]
#[allow(unsafe_code)]
fn atomic_replace_credential_leaf_windows(
    source: &Path,
    destination: &Path,
) -> std::io::Result<()> {
    use std::os::windows::ffi::OsStrExt as _;
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

/// 功能：no-follow 打开并同步一个已验证私有目录，作为 rename/remove 的 durable barrier。
///
/// 输入：固定 CredentialStore 目录路径。
/// 输出：目录元数据安全且 `sync_all` 成功。
/// 不变量：Unix 使用 O_DIRECTORY|O_NOFOLLOW；Windows 使用 BACKUP_SEMANTICS|OPEN_REPARSE_POINT 并复核 handle 元数据。
/// 失败：打开、类型/reparse、owner/mode 或同步失败时返回脱敏目录错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn sync_directory(path: &Path) -> Result<(), AgentError> {
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt as _;
        options.custom_flags(libc::O_DIRECTORY | libc::O_NOFOLLOW);
    }
    #[cfg(windows)]
    options.custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT);
    let directory = options
        .open(path)
        .map_err(|_| commercial_error("directory_sync", "CredentialStore 目录同步打开失败"))?;
    let metadata = directory
        .metadata()
        .map_err(|_| commercial_error("directory_sync", "CredentialStore 目录元数据读取失败"))?;
    validate_private_directory_metadata(&metadata)?;
    directory
        .sync_all()
        .map_err(|_| commercial_error("directory_sync", "CredentialStore 目录同步失败"))
}

/// 功能：创建目录并在 Unix 上把访问权限收窄到当前用户。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn create_private_directory(path: &Path) -> Result<(), AgentError> {
    reject_symlink_if_present(path)?;
    std::fs::create_dir_all(path)
        .map_err(|_| commercial_error("directory", "商业状态目录创建失败"))?;
    let metadata = std::fs::symlink_metadata(path)
        .map_err(|_| commercial_error("directory", "商业状态目录无效"))?;
    if metadata_is_unsafe(&metadata) || !metadata.is_dir() {
        return Err(commercial_error("directory_shape", "商业状态目录无效"));
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt as _;
        std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o700))
            .map_err(|_| commercial_error("directory_permissions", "商业状态目录权限设置失败"))?;
    }
    Ok(())
}

/// 功能：存在路径时拒绝符号链接，避免敏感文件被重定向。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn reject_symlink_if_present(path: &Path) -> Result<(), AgentError> {
    match std::fs::symlink_metadata(path) {
        Ok(metadata) if metadata_is_unsafe(&metadata) => Err(commercial_error(
            "file_symlink",
            "商业状态路径不能是链接或 reparse point",
        )),
        Ok(_) => Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(_) => Err(commercial_error(
            "file_metadata",
            "商业状态文件元数据读取失败",
        )),
    }
}

/// 功能：以 create 模式打开 lock file，并在 Unix 创建时固定安全权限。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn open_read_write_create(path: &Path, sensitive: bool) -> Result<File, AgentError> {
    let mut options = OpenOptions::new();
    options.read(true).write(true).create(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt as _;
        options.custom_flags(libc::O_NOFOLLOW);
        if sensitive {
            options.mode(0o600);
        }
    }
    #[cfg(windows)]
    options.custom_flags(FILE_FLAG_OPEN_REPARSE_POINT);
    let file = options
        .open(path)
        .map_err(|_| commercial_error("file_open", "商业状态 lock 打开失败"))?;
    let metadata = file
        .metadata()
        .map_err(|_| commercial_error("file_metadata", "商业状态 lock 元数据读取失败"))?;
    if metadata_is_unsafe(&metadata) || !metadata.is_file() {
        return Err(commercial_error("file_shape", "商业状态 lock 不是普通文件"));
    }
    Ok(file)
}

/// 功能：no-follow、有界读取商业状态普通文件并验证敏感权限。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn read_secure_file(path: &Path, maximum: usize, sensitive: bool) -> Result<Vec<u8>, AgentError> {
    reject_symlink_if_present(path)?;
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt as _;
        options.custom_flags(libc::O_NOFOLLOW);
    }
    #[cfg(windows)]
    options.custom_flags(FILE_FLAG_OPEN_REPARSE_POINT);
    let mut file = options
        .open(path)
        .map_err(|_| commercial_error("file_read", "商业状态文件读取失败"))?;
    let metadata = file
        .metadata()
        .map_err(|_| commercial_error("file_metadata", "商业状态文件元数据读取失败"))?;
    if sensitive {
        validate_sensitive_regular_metadata(&metadata, maximum, false)?;
    } else if metadata_is_unsafe(&metadata)
        || !metadata.is_file()
        || metadata.len() == 0
        || metadata.len() > maximum as u64
    {
        return Err(commercial_error("file_shape", "商业状态文件形状无效"));
    }
    let mut bytes = Vec::with_capacity(metadata.len() as usize);
    file.read_to_end(&mut bytes)
        .map_err(|_| commercial_error("file_read", "商业状态文件读取失败"))?;
    Ok(bytes)
}

/// 功能：判断文件元数据是否表示 symlink、Windows reparse point 或 device。
///
/// 输入：通过 `symlink_metadata` 或 no-follow handle 获得的元数据。
/// 输出：任何平台的 symlink，或 Windows device/reparse point 时为 true。
/// 不变量：普通文件和普通目录返回 false；不跟随或解析目标。
/// 失败：本方法不执行 I/O 且不返回错误。
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

/// 功能：以同目录 create-new 临时文件、同步和 rename 发布完整文档。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn atomic_write(path: &Path, bytes: &[u8], _sensitive: bool) -> Result<(), AgentError> {
    let parent = path
        .parent()
        .ok_or_else(|| commercial_error("file_path", "商业状态文件路径无效"))?;
    create_private_directory(parent)?;
    reject_symlink_if_present(path)?;
    let temporary = parent.join(format!(".commercial-{}.tmp", Uuid::new_v4()));
    let mut options = OpenOptions::new();
    options.create_new(true).write(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt as _;
        options.mode(0o600);
    }
    let result = (|| {
        let mut file = options
            .open(&temporary)
            .map_err(|_| commercial_error("file_create", "商业状态临时文件创建失败"))?;
        file.write_all(bytes)
            .map_err(|_| commercial_error("file_write", "商业状态文件写入失败"))?;
        file.sync_all()
            .map_err(|_| commercial_error("file_sync", "商业状态文件同步失败"))?;
        std::fs::rename(&temporary, path)
            .map_err(|_| commercial_error("file_publish", "商业状态文件原子发布失败"))?;
        File::open(parent)
            .and_then(|directory| directory.sync_all())
            .map_err(|_| commercial_error("directory_sync", "商业状态目录同步失败"))
    })();
    if result.is_err() {
        let _ = std::fs::remove_file(&temporary);
    }
    result
}

/// 功能：严格解析有界 UTF-8 JSON，并递归拒绝重复 key 与 DTO 未知字段。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_strict_json<T: DeserializeOwned>(bytes: &[u8], kind: &str) -> Result<T, AgentError> {
    let text = std::str::from_utf8(bytes)
        .map_err(|_| commercial_error(kind, "商业状态 JSON 不是 UTF-8"))?;
    let value =
        parse_strict_value(text).map_err(|_| commercial_error(kind, "商业状态 JSON 无效"))?;
    serde_json::from_value(value).map_err(|_| commercial_error(kind, "商业状态 JSON 字段无效"))
}

/// 功能：构造不含路径、URL 或 credential 的商业状态结构化错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn commercial_error(kind: &str, message: impl Into<String>) -> AgentError {
    AgentError::new(ErrorCode::InvalidRequest, message)
        .with_details(serde_json::json!({"kind":format!("commercial_state_{kind}")}))
}

/// 功能：构造 stored credential 缺失或失效时的稳定脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn provider_unavailable() -> AgentError {
    AgentError::new(
        ErrorCode::ProviderUnavailable,
        "provider credential is unavailable",
    )
    .with_details(serde_json::json!({"kind":"provider_unavailable"}))
}

#[cfg(test)]
mod tests {
    use std::fs;

    use chrono::{SecondsFormat, TimeDelta, Utc};
    use tempfile::tempdir;

    use super::{InstalledSponsoredRouteStore, ProviderCredentialStore};
    use crate::sponsored_catalog::{SponsoredProviderCatalog, SponsoredProviderEntry};

    /// 功能：构造只在本地单测使用的已验证形状 catalog DTO。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn test_catalog() -> SponsoredProviderCatalog {
        let now = Utc::now();
        SponsoredProviderCatalog {
            schema_version: "0.1".to_owned(),
            catalog_version: 7,
            issued_at: now.to_rfc3339_opts(SecondsFormat::Secs, true),
            expires_at: (now + TimeDelta::days(1)).to_rfc3339_opts(SecondsFormat::Secs, true),
            entries: vec![SponsoredProviderEntry {
                id: "example-relay".to_owned(),
                display_name: "示例中转站".to_owned(),
                description: "测试".to_owned(),
                api_family: "openai-completions".to_owned(),
                api_base_url: "https://relay.example/v1".to_owned(),
                signup_url: "https://relay.example/register".to_owned(),
                commission_disclosure: "通过该链接注册，发行方可能获得佣金".to_owned(),
                priority: 1,
            }],
        }
    }

    /// 功能：验证 credential 只通过状态文件轮换，list 永不返回 secret。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn stores_lists_rotates_and_removes_credentials() -> Result<(), Box<dyn std::error::Error>> {
        let directory = tempdir()?;
        let workspace = directory.path().join("workspace");
        let state = directory.path().join("state");
        fs::create_dir(&workspace)?;
        let store = ProviderCredentialStore::new(&state, &workspace)?;
        store.set("relay-example-relay", "first-local-test-key")?;
        store.set("custom.example", "dotted-provider-key")?;
        assert_eq!(store.request_read_count_for_test(), 0);
        assert_eq!(store.list()?, ["custom.example", "relay-example-relay"]);
        assert!(store.contains("custom.example"));
        assert_eq!(store.request_read_count_for_test(), 0);
        assert_eq!(
            store.read_for_request("custom.example")?,
            "dotted-provider-key"
        );
        assert_eq!(store.request_read_count_for_test(), 1);
        assert!(store.remove("custom.example")?);
        store.set("relay-example-relay", "second-local-test-key")?;
        assert_eq!(
            store.read_for_request("relay-example-relay")?,
            "second-local-test-key"
        );
        assert!(store.remove("relay-example-relay")?);
        assert!(store.list()?.is_empty());
        Ok(())
    }

    /// 功能：验证 v0.1 聚合 credential 文档只在构造升级边界迁移一次，且旧用户 key 无需重录。
    ///
    /// 不变量：全部 synthetic secret 先写入工作区外临时状态；迁移后 legacy 删除、逐叶目录唯一权威，list 不增加 secret 读取计数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn migrates_legacy_credential_document_without_reentry()
    -> Result<(), Box<dyn std::error::Error>> {
        let directory = tempdir()?;
        let workspace = directory.path().join("workspace");
        let state = directory.path().join("state");
        let credential_root = state.join("credentials");
        fs::create_dir(&workspace)?;
        fs::create_dir_all(&credential_root)?;
        let legacy = credential_root.join("provider-credentials.json");
        fs::write(
            &legacy,
            br#"{"schemaVersion":"0.1","credentials":[{"providerId":"custom.example","apiKey":"legacy-first-key"},{"providerId":"relay-example-relay","apiKey":"legacy-second-key"}]}"#,
        )?;
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt as _;
            fs::set_permissions(&legacy, fs::Permissions::from_mode(0o600))?;
        }

        let store = ProviderCredentialStore::new(&state, &workspace)?;
        assert!(!legacy.exists());
        assert_eq!(store.request_read_count_for_test(), 0);
        assert_eq!(store.list()?, ["custom.example", "relay-example-relay"]);
        assert_eq!(store.request_read_count_for_test(), 0);
        assert_eq!(
            store.read_for_request("custom.example")?,
            "legacy-first-key"
        );
        assert_eq!(store.request_read_count_for_test(), 1);
        Ok(())
    }

    /// 功能：验证安装快照固定目录版本、endpoint、family 和模型，且不包含 secret。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn installs_non_secret_sponsored_route_snapshot() -> Result<(), Box<dyn std::error::Error>> {
        let directory = tempdir()?;
        let store = InstalledSponsoredRouteStore::new(directory.path());
        let route = store.install(&test_catalog(), "example-relay", "model-v1")?;
        assert_eq!(route.provider_id, "relay-example-relay");
        assert_eq!(route.catalog_version, 7);
        assert_eq!(route.models, ["model-v1"]);
        let bytes = fs::read(store.document_path())?;
        assert!(!String::from_utf8(bytes)?.contains("local-test-key"));
        Ok(())
    }

    /// 功能：验证显式把 CredentialStore 放进 workspace 会在写 secret 前拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn rejects_credential_store_inside_workspace() -> Result<(), Box<dyn std::error::Error>> {
        let directory = tempdir()?;
        let workspace = directory.path().join("workspace");
        fs::create_dir(&workspace)?;
        assert!(ProviderCredentialStore::new(workspace.join("state"), &workspace).is_err());
        Ok(())
    }

    /// 功能：验证 credential 文档被符号链接替换时读取失败关闭且不接触目标内容。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(unix)]
    #[test]
    fn rejects_symlinked_credential_document() -> Result<(), Box<dyn std::error::Error>> {
        use std::os::unix::fs::symlink;

        let directory = tempdir()?;
        let workspace = directory.path().join("workspace");
        let state = directory.path().join("state");
        fs::create_dir(&workspace)?;
        let outside = directory.path().join("outside.json");
        fs::write(&outside, b"{\"schemaVersion\":\"0.1\",\"credentials\":[]}")?;
        fs::create_dir_all(state.join("credentials"))?;
        symlink(
            &outside,
            state.join("credentials").join("provider-credentials.json"),
        )?;
        assert!(ProviderCredentialStore::new(&state, &workspace).is_err());
        Ok(())
    }

    /// 功能：验证整个 state root 被符号链接替换时在创建 credential 目录前拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(unix)]
    #[test]
    fn rejects_symlinked_credential_state_root() -> Result<(), Box<dyn std::error::Error>> {
        use std::os::unix::fs::symlink;

        let directory = tempdir()?;
        let workspace = directory.path().join("workspace");
        let outside = directory.path().join("outside-state");
        let linked_state = directory.path().join("linked-state");
        fs::create_dir(&workspace)?;
        fs::create_dir(&outside)?;
        symlink(&outside, &linked_state)?;
        assert!(ProviderCredentialStore::new(&linked_state, &workspace).is_err());
        assert!(!outside.join("credentials").exists());
        Ok(())
    }
}
