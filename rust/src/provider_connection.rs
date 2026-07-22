//! 用户自定义 Provider 连接的非敏感配置、Credential 协调与启动快照。
//!
//! 作者：高宏顺 <18272669457@163.com>

use std::collections::{BTreeMap, BTreeSet};
use std::fs::{File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

#[cfg(windows)]
use std::os::windows::fs::{MetadataExt as _, OpenOptionsExt as _};

use async_trait::async_trait;
use chrono::{SecondsFormat, Utc};
use fs2::FileExt as _;
use futures_util::StreamExt as _;
use reqwest::header::{AUTHORIZATION, HeaderValue};
use reqwest::redirect::Policy;
use reqwest::{StatusCode, Url};
use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use tokio_util::sync::CancellationToken;
use uuid::Uuid;

use crate::commercial_state::ProviderCredentialStore;
use crate::error::{AgentError, ErrorCode};
use crate::protocol::parse_strict_value;
use crate::provider::{
    OpenAiChatProvider, OpenAiResponsesProvider, Provider, ProviderCredentialSource,
    ProviderRequest, ProviderStream, native_endpoint,
};
use crate::provider_identity::{
    AdvertisedModel, custom_openai_model, is_canonical_provider_id, model_key,
};

const DOCUMENT_SCHEMA_VERSION: &str = "0.2";
const LEGACY_DOCUMENT_SCHEMA_VERSION: &str = "0.1";
const MAX_DOCUMENT_BYTES: usize = 2 * 1024 * 1024;
const MAX_CONNECTIONS: usize = 128;
const MAX_MODEL_IDS: usize = 512;
const MAX_MODEL_ID_BYTES: usize = 256;
const MAX_MODEL_DISCOVERY_BYTES: usize = 1024 * 1024;
const MODEL_DISCOVERY_TIMEOUT: Duration = Duration::from_secs(20);
const MAX_SAFE_INTEGER: u64 = 9_007_199_254_740_991;
const CHAT_API_FAMILY: &str = "openai-completions";
const RESPONSES_API_FAMILY: &str = "openai-responses";
const RESPONSES_CREDENTIAL_KIND: &str = "responses";
const IMAGE_CREDENTIAL_KIND: &str = "image";
#[cfg(windows)]
const FILE_FLAG_OPEN_REPARSE_POINT: u32 = 0x0020_0000;

/// Provider 连接允许持久化的完整非敏感输入。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CustomProviderConnectionInput {
    pub display_name: String,
    pub provider_id: String,
    pub api_family: String,
    pub base_url: String,
    pub models_url: String,
    pub model_ids: Vec<String>,
    pub supports_tools: bool,
    pub logo_asset_id: Option<String>,
    pub enabled: bool,
}

/// Provider 连接对 RPC 客户端公开的脱敏投影。
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CustomProviderConnectionView {
    pub connection_id: String,
    pub revision: u64,
    pub display_name: String,
    pub provider_id: String,
    pub api_family: String,
    pub base_url: String,
    pub models_url: String,
    pub model_ids: Vec<String>,
    pub supports_tools: bool,
    pub logo_asset_id: Option<String>,
    pub enabled: bool,
    pub credential_configured: bool,
    pub image_credential_configured: bool,
    pub created_at: String,
    pub updated_at: String,
}

/// `providerConnections/list` 的严格空参数。
#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ProviderConnectionsListParams {}

/// `providerConnections/create` 的严格参数。
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ProviderConnectionsCreateParams {
    pub connection: CustomProviderConnectionInput,
}

/// `providerConnections/update` 的严格参数。
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ProviderConnectionsUpdateParams {
    pub connection_id: String,
    pub expected_revision: u64,
    pub connection: CustomProviderConnectionInput,
}

/// `providerConnections/discoverModels` 的严格参数。
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ProviderConnectionsDiscoverModelsParams {
    pub connection_id: String,
    pub expected_revision: u64,
}

/// `providerConnections/delete` 的严格参数。
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ProviderConnectionsDeleteParams {
    pub connection_id: String,
    pub expected_revision: u64,
}

/// `providerCredentials/set` 的严格参数；credential 只短暂存在于请求内存。
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ProviderCredentialsSetParams {
    pub provider_id: String,
    pub credential_kind: String,
    pub credential: String,
}

/// `providerCredentials/remove` 的严格参数。
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ProviderCredentialsRemoveParams {
    pub provider_id: String,
    pub credential_kind: String,
}

/// 固定 JSON 文档内的一条非敏感连接记录。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct StoredConnection {
    connection_id: String,
    revision: u64,
    display_name: String,
    provider_id: String,
    api_family: String,
    base_url: String,
    #[serde(default)]
    models_url: String,
    model_ids: Vec<String>,
    #[serde(default)]
    supports_tools: bool,
    logo_asset_id: Option<String>,
    enabled: bool,
    created_at: String,
    updated_at: String,
}

/// 自定义 Provider 连接的严格、有界 JSON 根对象。
#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ConnectionDocument {
    schema_version: String,
    connections: Vec<StoredConnection>,
}

/// OpenAI-compatible 模型目录中必须存在的最小响应根对象。
#[derive(Debug, Deserialize)]
struct ModelDiscoveryResponse {
    data: Vec<ModelDiscoveryItem>,
}

/// OpenAI-compatible 模型目录单项中必须存在的最小字段。
#[derive(Debug, Deserialize)]
struct ModelDiscoveryItem {
    id: String,
}

/// 在作用域结束时释放 Provider 连接文档的跨进程 writer lock。
struct ConnectionFileLock(File);

impl Drop for ConnectionFileLock {
    /// 功能：尽力释放文件锁，且不覆盖原业务结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn drop(&mut self) {
        let _ = fs2::FileExt::unlock(&self.0);
    }
}

/// state root 下严格、有界、原子保存非敏感 Provider 连接的文件 store。
#[derive(Clone)]
pub struct CustomProviderConnectionStore {
    root: PathBuf,
    allow_conformance_loopback: bool,
}

impl CustomProviderConnectionStore {
    /// 功能：在状态根下创建固定的 Provider 连接私有目录。
    ///
    /// 输入：可信状态根，以及 CLI 与环境双门确认后的 conformance loopback 权限。
    /// 输出：尚未读取配置文档的轻量 store。
    /// 不变量：路径不受 RPC 输入控制；目录不得是 symlink；Unix 权限收窄为 0700。
    /// 失败：目录创建、canonical 边界或权限设置失败时返回脱敏存储错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn new(
        state_root: impl AsRef<Path>,
        allow_conformance_loopback: bool,
    ) -> Result<Self, AgentError> {
        let state_root = state_root.as_ref();
        create_private_directory(state_root)?;
        let canonical_state_root = std::fs::canonicalize(state_root)
            .map_err(|_| connection_store_error("path", "Provider 连接状态目录无效"))?;
        let root = canonical_state_root.join("provider-connections");
        create_private_directory(&root)?;
        let canonical_root = std::fs::canonicalize(&root)
            .map_err(|_| connection_store_error("path", "Provider 连接状态目录无效"))?;
        if !canonical_root.starts_with(&canonical_state_root) {
            return Err(connection_store_error(
                "path_boundary",
                "Provider 连接状态目录越界",
            ));
        }
        Ok(Self {
            root: canonical_root,
            allow_conformance_loopback,
        })
    }

    /// 功能：读取并验证全部非敏感 Provider 连接。
    ///
    /// 输出：按 connection ID ordinal 稳定排序的独立记录数组。
    /// 不变量：读取有界、no-follow、strict JSON；文档任一记录无效即全部失败关闭。
    /// 失败：锁、文件形状、JSON、版本、唯一性或字段不变量失败时返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn list(&self) -> Result<Vec<StoredConnection>, AgentError> {
        let _lock = self.acquire_lock()?;
        Ok(self.read_document_unlocked()?.connections)
    }

    /// 功能：创建一条全局 Provider ID 唯一的非敏感连接，并清理同 ID 的历史 credential。
    ///
    /// 输入：严格 DTO 解码后的完整连接输入。
    /// 输出：revision 为 1 且已 durable 原子发布的新记录。
    /// 不变量：固定锁顺序为 connection -> credential；发布前删除同 Provider ID 的 orphan
    /// credential，防止旧 secret 绑定新 endpoint；Provider ID 与 connection ID 在文档内唯一。
    /// 失败：字段、容量、唯一性、credential 清理、锁或原子发布失败时不发布连接；发布失败最多
    /// 留下已清理 credential 的安全状态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn create_with_credential_removal(
        &self,
        input: CustomProviderConnectionInput,
        remove_credential: impl FnOnce(&str) -> Result<(), AgentError>,
    ) -> Result<StoredConnection, AgentError> {
        validate_connection_input(&input, self.allow_conformance_loopback)?;
        let _lock = self.acquire_lock()?;
        let mut document = self.read_document_unlocked()?;
        if document.connections.len() >= MAX_CONNECTIONS {
            return Err(invalid_connection(
                "connections",
                "Provider 连接数量已达上限",
            ));
        }
        if document
            .connections
            .iter()
            .any(|connection| connection.provider_id == input.provider_id)
        {
            return Err(provider_id_conflict());
        }
        remove_credential(&input.provider_id)?;
        let now = current_timestamp();
        let connection = StoredConnection {
            connection_id: format!("connection-{}", Uuid::new_v4()),
            revision: 1,
            display_name: input.display_name,
            provider_id: input.provider_id,
            api_family: input.api_family,
            base_url: input.base_url,
            models_url: input.models_url,
            model_ids: input.model_ids,
            supports_tools: input.supports_tools,
            logo_asset_id: input.logo_asset_id,
            enabled: input.enabled,
            created_at: now.clone(),
            updated_at: now,
        };
        document.connections.push(connection.clone());
        sort_connections(&mut document.connections);
        self.write_document_unlocked(&document)?;
        Ok(connection)
    }

    /// 功能：以 revision CAS 替换一条连接的完整非敏感输入。
    ///
    /// 输入：安全 connection ID、1..MAX_SAFE_INTEGER 的期望 revision 和完整输入。
    /// 输出：revision 加一且 durable 的更新记录。
    /// 不变量：创建时间与 connection ID 不变；Provider ID 在全局仍唯一；ID 变化时在连接锁内移除新旧 credential，禁止继承 orphan secret。
    /// 失败：不存在、CAS 冲突、字段/唯一性、credential 清理、锁或原子发布失败时拒绝；发布失败最多保留无旧 credential 的安全连接。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn update_with_provider_cleanup(
        &self,
        connection_id: &str,
        expected_revision: u64,
        input: CustomProviderConnectionInput,
        remove_transition_credentials: impl FnOnce(&str, &str) -> Result<(), AgentError>,
    ) -> Result<StoredConnection, AgentError> {
        validate_safe_id(connection_id, 128, "connectionId")?;
        validate_revision(expected_revision)?;
        validate_connection_input(&input, self.allow_conformance_loopback)?;
        let _lock = self.acquire_lock()?;
        let mut document = self.read_document_unlocked()?;
        let index = document
            .connections
            .iter()
            .position(|connection| connection.connection_id == connection_id)
            .ok_or_else(connection_not_found)?;
        if document.connections[index].revision != expected_revision {
            return Err(revision_conflict());
        }
        if document
            .connections
            .iter()
            .enumerate()
            .any(|(other, connection)| {
                other != index && connection.provider_id == input.provider_id
            })
        {
            return Err(provider_id_conflict());
        }
        let previous = &document.connections[index];
        if previous.provider_id != input.provider_id {
            remove_transition_credentials(&previous.provider_id, &input.provider_id)?;
        }
        let connection = StoredConnection {
            connection_id: previous.connection_id.clone(),
            revision: previous
                .revision
                .checked_add(1)
                .filter(|revision| *revision <= MAX_SAFE_INTEGER)
                .ok_or_else(revision_conflict)?,
            display_name: input.display_name,
            provider_id: input.provider_id,
            api_family: input.api_family,
            base_url: input.base_url,
            models_url: input.models_url,
            model_ids: input.model_ids,
            supports_tools: input.supports_tools,
            logo_asset_id: input.logo_asset_id,
            enabled: input.enabled,
            created_at: previous.created_at.clone(),
            updated_at: current_timestamp_not_before(&previous.updated_at),
        };
        document.connections[index] = connection.clone();
        sort_connections(&mut document.connections);
        self.write_document_unlocked(&document)?;
        Ok(connection)
    }

    /// 功能：以 connection ID 与 revision 读取一次模型发现使用的不可变连接快照。
    ///
    /// 输入：安全 connection ID 与 JSON 安全整数范围内的期望 revision。
    /// 输出：精确命中的完整非敏感连接副本。
    /// 不变量：只在读取文档期间持有连接锁；调用方联网期间不持锁。
    /// 失败：ID、revision、目标、CAS、锁或文档无效时返回不含实例值的结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn read_for_model_discovery(
        &self,
        connection_id: &str,
        expected_revision: u64,
    ) -> Result<StoredConnection, AgentError> {
        validate_safe_id(connection_id, 128, "connectionId")?;
        validate_revision(expected_revision)?;
        let _lock = self.acquire_lock()?;
        let document = self.read_document_unlocked()?;
        let connection = document
            .connections
            .into_iter()
            .find(|connection| connection.connection_id == connection_id)
            .ok_or_else(connection_not_found)?;
        if connection.revision != expected_revision {
            return Err(revision_conflict());
        }
        Ok(connection)
    }

    /// 功能：以原 revision CAS 仅替换一条连接的已发现模型 allowlist。
    ///
    /// 输入：模型发现前的 connection ID、revision，以及已验证、去重和排序的非空模型列表。
    /// 输出：模型列表已 durable 发布且 revision 精确加一的新记录。
    /// 不变量：除 `modelIds`、`revision`、`updatedAt` 外所有字段保持原值；CAS 失败不修改文档。
    /// 失败：目标、CAS、模型边界、revision 溢出、锁或原子发布失败时原记录保持不变。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn replace_discovered_models(
        &self,
        connection_id: &str,
        expected_revision: u64,
        model_ids: Vec<String>,
    ) -> Result<StoredConnection, AgentError> {
        validate_safe_id(connection_id, 128, "connectionId")?;
        validate_revision(expected_revision)?;
        if !model_ids_are_valid(&model_ids, false) {
            return Err(model_discovery_invalid_response());
        }
        let _lock = self.acquire_lock()?;
        let mut document = self.read_document_unlocked()?;
        let index = document
            .connections
            .iter()
            .position(|connection| connection.connection_id == connection_id)
            .ok_or_else(connection_not_found)?;
        if document.connections[index].revision != expected_revision {
            return Err(revision_conflict());
        }
        let previous = &document.connections[index];
        let mut connection = previous.clone();
        connection.revision = previous
            .revision
            .checked_add(1)
            .filter(|revision| *revision <= MAX_SAFE_INTEGER)
            .ok_or_else(revision_conflict)?;
        connection.model_ids = model_ids;
        connection.updated_at = current_timestamp_not_before(&previous.updated_at);
        document.connections[index] = connection.clone();
        self.write_document_unlocked(&document)?;
        Ok(connection)
    }

    /// 功能：以 revision CAS 删除一条非敏感连接。
    ///
    /// 输入：安全 connection ID 与期望 revision。
    /// 输出：被删除记录的 Provider ID，供上层构造不含 secret 的结果。
    /// 不变量：本操作不读取或自动删除独立 CredentialStore；原子发布后记录才视为删除。
    /// 失败：不存在、CAS 冲突、锁或发布失败时原文档不变。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn delete_with_credential_removal(
        &self,
        connection_id: &str,
        expected_revision: u64,
        remove_credentials: impl FnOnce(&str, &str) -> Result<(), AgentError>,
    ) -> Result<String, AgentError> {
        validate_safe_id(connection_id, 128, "connectionId")?;
        validate_revision(expected_revision)?;
        let _lock = self.acquire_lock()?;
        let mut document = self.read_document_unlocked()?;
        let index = document
            .connections
            .iter()
            .position(|connection| connection.connection_id == connection_id)
            .ok_or_else(connection_not_found)?;
        if document.connections[index].revision != expected_revision {
            return Err(revision_conflict());
        }
        let provider_id = document.connections[index].provider_id.clone();
        remove_credentials(&provider_id, &document.connections[index].connection_id)?;
        document.connections.remove(index);
        self.write_document_unlocked(&document)?;
        Ok(provider_id)
    }

    /// 功能：在连接 writer lock 持有期间对一条已存在 Provider 连接执行 credential 操作。
    ///
    /// 输入：安全 Provider ID。
    /// 输出：回调成功时返回其非敏感结果。
    /// 不变量：固定锁顺序为 connection -> credential；回调不得再次获取 connection lock；连接不存在时不执行回调。
    /// 失败：ID、文档、锁、目标不存在或回调失败时返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn with_provider_lock<T>(
        &self,
        provider_id: &str,
        operation: impl FnOnce(&StoredConnection) -> Result<T, AgentError>,
    ) -> Result<T, AgentError> {
        validate_safe_id(provider_id, 128, "providerId")?;
        let _lock = self.acquire_lock()?;
        let document = self.read_document_unlocked()?;
        let connection = document
            .connections
            .into_iter()
            .find(|connection| connection.provider_id == provider_id)
            .ok_or_else(connection_not_found)?;
        operation(&connection)
    }

    /// 功能：在连接 writer lock 内执行不限定连接存在性的 credential 协调操作。
    ///
    /// 输入：不得再次获取 connection lock 的同步回调。
    /// 输出：回调产生的非敏感结果。
    /// 不变量：通用 CLI 与连接 rename/delete 共同遵守 connection -> credential 锁顺序。
    /// 失败：连接 lock 或回调失败时不吞掉错误；本方法不读取 credential 值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn with_configuration_lock<T>(
        &self,
        operation: impl FnOnce() -> Result<T, AgentError>,
    ) -> Result<T, AgentError> {
        let _lock = self.acquire_lock()?;
        operation()
    }

    /// 功能：获取固定 lock file 的非阻塞独占锁。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn acquire_lock(&self) -> Result<ConnectionFileLock, AgentError> {
        let path = self.root.join("provider-connections.lock");
        reject_symlink_if_present(&path)?;
        let file = open_read_write_create(&path)?;
        file.try_lock_exclusive()
            .map_err(|_| connection_store_error("locked", "Provider 连接配置正被其他进程使用"))?;
        Ok(ConnectionFileLock(file))
    }

    /// 功能：在调用方持有锁时 no-follow 读取并验证完整文档。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn read_document_unlocked(&self) -> Result<ConnectionDocument, AgentError> {
        let path = self.root.join("provider-connections.json");
        match std::fs::symlink_metadata(&path) {
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(ConnectionDocument {
                    schema_version: DOCUMENT_SCHEMA_VERSION.to_owned(),
                    connections: Vec::new(),
                });
            }
            Err(_) => {
                return Err(connection_store_error(
                    "metadata",
                    "Provider 连接配置元数据无效",
                ));
            }
            Ok(metadata) if metadata_is_unsafe(&metadata) => {
                return Err(connection_store_error(
                    "symlink",
                    "Provider 连接配置不能是符号链接",
                ));
            }
            Ok(_) => {}
        }
        let bytes = read_bounded_file(&path, MAX_DOCUMENT_BYTES)?;
        let mut document: ConnectionDocument = parse_strict_json(&bytes)?;
        if document.schema_version == LEGACY_DOCUMENT_SCHEMA_VERSION {
            for connection in &mut document.connections {
                if connection.models_url.is_empty() {
                    connection.models_url = native_endpoint(&connection.base_url, "/models", &[])
                        .map_err(|_| connection_store_error("shape", "Provider 连接配置文档无效"))?
                        .to_string();
                }
            }
            document.schema_version = DOCUMENT_SCHEMA_VERSION.to_owned();
        }
        validate_document(&document, self.allow_conformance_loopback)?;
        Ok(document)
    }

    /// 功能：在调用方持有锁时以同目录临时文件原子发布完整文档。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn write_document_unlocked(&self, document: &ConnectionDocument) -> Result<(), AgentError> {
        validate_document(document, self.allow_conformance_loopback)?;
        let mut bytes = serde_json::to_vec(document)
            .map_err(|_| connection_store_error("json", "Provider 连接配置序列化失败"))?;
        bytes.push(b'\n');
        if bytes.len() > MAX_DOCUMENT_BYTES {
            return Err(connection_store_error(
                "size",
                "Provider 连接配置超过大小上限",
            ));
        }
        atomic_write(&self.root.join("provider-connections.json"), &bytes)
    }
}

/// 协调非敏感连接 Store 与独立 CredentialStore 的 application service。
#[derive(Clone)]
pub struct CustomProviderConnectionService {
    connections: CustomProviderConnectionStore,
    credentials: ProviderCredentialStore,
}

/// daemon 持有的 Provider 连接 CRUD 服务与不可变启动快照组合。
#[derive(Clone)]
pub struct CustomProviderConnectionRuntime {
    service: CustomProviderConnectionService,
    snapshot: CustomProviderRuntimeSnapshot,
}

impl CustomProviderConnectionRuntime {
    /// 功能：把可变持久化服务与本进程不可变能力快照组合为单一 daemon 依赖。
    ///
    /// 输入：共享同一物理 store 的连接 service 和启动快照。
    /// 输出：不会动态替换快照的 runtime context。
    /// 不变量：CRUD 只影响下一次启动；当前 `snapshot` 始终驱动 adapter 与能力广告。
    /// 失败：本方法不访问文件系统且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn new(
        service: CustomProviderConnectionService,
        snapshot: CustomProviderRuntimeSnapshot,
    ) -> Self {
        Self { service, snapshot }
    }

    /// 功能：借用可执行 CRUD 与 credential 协调服务。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn service(&self) -> &CustomProviderConnectionService {
        &self.service
    }

    /// 功能：借用本进程固定的能力与 adapter 同源快照。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn snapshot(&self) -> &CustomProviderRuntimeSnapshot {
        &self.snapshot
    }
}

impl CustomProviderConnectionService {
    /// 功能：创建不读取配置或 credential 的 Provider 连接服务。
    ///
    /// 输入：固定连接 store 与工作区外 CredentialStore。
    /// 输出：可供 daemon RPC 使用的轻量服务。
    /// 不变量：两个 store 保持物理分离，非敏感配置永不携带 secret。
    /// 失败：本方法不访问文件系统且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn new(
        connections: CustomProviderConnectionStore,
        credentials: ProviderCredentialStore,
    ) -> Self {
        Self {
            connections,
            credentials,
        }
    }

    /// 功能：列出全部 Provider 连接的脱敏实时投影。
    ///
    /// 输出：稳定排序的连接数组，每项只含 `credentialConfigured` 布尔状态。
    /// 不变量：CredentialStore 值永不读取到返回对象；只读取已配置 Provider ID 集合。
    /// 失败：任一 store 锁定、损坏或不可读时失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn list(&self) -> Result<Vec<CustomProviderConnectionView>, AgentError> {
        let configured = self
            .credentials
            .list()
            .map_err(map_credential_error)?
            .into_iter()
            .collect::<BTreeSet<_>>();
        Ok(self
            .connections
            .list()?
            .into_iter()
            .map(|connection| project_connection(connection, &configured))
            .collect())
    }

    /// 功能：创建连接、清理同 ID 的历史 credential，并返回不含 secret 的实时投影。
    ///
    /// 输入：完整非敏感连接输入。
    /// 输出：durable 新连接，且 credentialConfigured 固定为 false。
    /// 不变量：在 connection writer lock 内先移除同 Provider ID 的 orphan credential，再发布连接，
    /// 防止 CLI 历史 secret 被隐式绑定到新的 endpoint。
    /// 失败：配置、唯一性、credential 清理、连接 store 或 CredentialStore 状态无效时返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn create(
        &self,
        input: CustomProviderConnectionInput,
    ) -> Result<CustomProviderConnectionView, AgentError> {
        let connection = self
            .connections
            .create_with_credential_removal(input, |provider_id| {
                let _ = self
                    .credentials
                    .remove(provider_id)
                    .map_err(map_credential_error)?;
                Ok(())
            })?;
        let configured = self.configured_provider_ids()?;
        Ok(project_connection(connection, &configured))
    }

    /// 功能：以 CAS 更新连接并返回不含 secret 的实时投影。
    ///
    /// 输入：connection ID、期望 revision 与完整非敏感连接输入。
    /// 输出：durable 更新记录及其 Provider ID 的 credential presence。
    /// 不变量：本方法不读取 credential 值；Provider ID 变化时先删除新旧 ID credential，避免不可达或意外继承的 secret。
    /// 失败：CAS、配置、唯一性、credential 清理或任一 store 状态无效时返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn update(
        &self,
        connection_id: &str,
        expected_revision: u64,
        input: CustomProviderConnectionInput,
    ) -> Result<CustomProviderConnectionView, AgentError> {
        let connection = self.connections.update_with_provider_cleanup(
            connection_id,
            expected_revision,
            input,
            |old_provider_id, new_provider_id| {
                let _ = self
                    .credentials
                    .remove(new_provider_id)
                    .map_err(map_credential_error)?;
                let _ = self
                    .credentials
                    .remove(old_provider_id)
                    .map_err(map_credential_error)?;
                Ok(())
            },
        )?;
        let configured = self.configured_provider_ids()?;
        Ok(project_connection(connection, &configured))
    }

    /// 功能：显式请求自定义 Provider 的严格模型目录，并以 CAS 更新模型 allowlist。
    ///
    /// 输入：connection ID 与开始联网前精确匹配的期望 revision。
    /// 输出：只替换模型列表、revision 加一的脱敏连接投影。
    /// 不变量：连接锁不跨网络请求持有；credential 仅在最终 HTTP 边界读取；成功后不再次读取 credential store。
    /// 失败：credential、endpoint、超时、网络、状态、正文、解析、模型边界或 CAS 失败均不得修改连接。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn discover_models(
        &self,
        connection_id: &str,
        expected_revision: u64,
    ) -> Result<CustomProviderConnectionView, AgentError> {
        let snapshot = self
            .connections
            .read_for_model_discovery(connection_id, expected_revision)?;
        let endpoint = Url::parse(&snapshot.models_url)
            .map_err(|_| invalid_connection("modelsUrl", "Provider 模型目录 URL 无效"))?;
        let client = build_model_discovery_client()?;
        let credential = self.credentials.read_for_request(&snapshot.provider_id)?;
        let model_ids = fetch_discovered_model_ids(&client, endpoint, credential).await?;
        let connection = self.connections.replace_discovered_models(
            connection_id,
            expected_revision,
            model_ids,
        )?;
        let configured = self.configured_provider_ids()?;
        Ok(project_connection(connection, &configured))
    }

    /// 功能：以 CAS 删除一条非敏感 Provider 连接。
    ///
    /// 输入：connection ID 与期望 revision。
    /// 输出：被删除连接的 Provider ID；不包含旧配置或 credential。
    /// 不变量：先在连接 writer lock 内验证 CAS，再删除关联 credential，最后发布配置删除；
    /// stale revision 不得产生 secret 副作用，任何输出都不包含旧 credential。
    /// 失败：目标不存在、CAS、CredentialStore 或配置发布失败时返回结构化错误；配置发布失败最多保留一条无 credential 的安全连接。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn delete(
        &self,
        connection_id: &str,
        expected_revision: u64,
    ) -> Result<String, AgentError> {
        self.connections.delete_with_credential_removal(
            connection_id,
            expected_revision,
            |provider_id, deleted_connection_id| {
                let _ = self
                    .credentials
                    .remove(provider_id)
                    .map_err(map_credential_error)?;
                let _ = self
                    .credentials
                    .remove(&image_credential_id(deleted_connection_id))
                    .map_err(map_credential_error)?;
                Ok(())
            },
        )
    }

    /// 功能：为已存在的 Provider 连接写入或轮换 credential。
    ///
    /// 输入：连接的 Provider ID 与非空、最多 16 KiB、无 CR/LF/NUL 的 credential。
    /// 输出：成功时只表示 configured=true。
    /// 不变量：固定 connection -> credential 锁顺序；secret 仅交给 CredentialStore，不进入配置、日志或返回对象。
    /// 失败：连接不存在、secret header 边界、锁、权限或原子发布失败时安全拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn set_credential(
        &self,
        provider_id: &str,
        credential_kind: &str,
        credential: &str,
    ) -> Result<(), AgentError> {
        validate_credential(credential)?;
        validate_credential_kind(credential_kind)?;
        self.connections
            .with_provider_lock(provider_id, |connection| {
                let credential_id = credential_store_id(connection, credential_kind);
                self.credentials
                    .set(&credential_id, credential)
                    .map_err(map_credential_error)
            })
    }

    /// 功能：移除已存在 Provider 连接对应的 credential。
    ///
    /// 输入：连接的 Provider ID。
    /// 输出：无论此前是否存在 secret，成功后 configured=false。
    /// 不变量：固定 connection -> credential 锁顺序；不返回、摘要或记录被移除的 secret。
    /// 失败：连接不存在或 CredentialStore 无法安全读取/发布时拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn remove_credential(
        &self,
        provider_id: &str,
        credential_kind: &str,
    ) -> Result<(), AgentError> {
        validate_credential_kind(credential_kind)?;
        self.connections
            .with_provider_lock(provider_id, |connection| {
                let credential_id = credential_store_id(connection, credential_kind);
                let _ = self
                    .credentials
                    .remove(&credential_id)
                    .map_err(map_credential_error)?;
                Ok(())
            })
    }

    /// 功能：为通用 CLI 在固定锁顺序内写入或轮换任意合法 Provider credential。
    ///
    /// 输入：canonical、推广或自定义 Provider ID，以及 stdin 取得的 credential。
    /// 输出：成功时不返回 credential 或其摘要。
    /// 不变量：先持有 connection writer lock，再进入 CredentialStore；不要求自定义连接已存在。
    /// 失败：ID、credential、任一 lock、权限或原子发布失败时安全拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn set_credential_coordinated(
        &self,
        provider_id: &str,
        credential: &str,
    ) -> Result<(), AgentError> {
        validate_safe_id(provider_id, 128, "providerId")?;
        validate_credential(credential)?;
        self.connections.with_configuration_lock(|| {
            self.credentials
                .set(provider_id, credential)
                .map_err(map_credential_error)
        })
    }

    /// 功能：为通用 CLI 在固定锁顺序内移除任意合法 Provider credential。
    ///
    /// 输入：canonical、推广或自定义 Provider ID。
    /// 输出：存在并删除时 true，不存在时 false；不返回旧 credential。
    /// 不变量：先持有 connection writer lock，再进入 CredentialStore；不要求自定义连接已存在。
    /// 失败：ID、任一 lock、权限或原子发布失败时安全拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn remove_credential_coordinated(&self, provider_id: &str) -> Result<bool, AgentError> {
        validate_safe_id(provider_id, 128, "providerId")?;
        self.connections.with_configuration_lock(|| {
            self.credentials
                .remove(provider_id)
                .map_err(map_credential_error)
        })
    }

    /// 功能：构造本次进程生命周期固定的可执行 Provider 快照。
    ///
    /// 输出：仅包含启动瞬间 enabled、有非空模型 allowlist 且 credentialConfigured 的连接及模型。
    /// 不变量：快照保存非敏感配置和 CredentialStore 句柄但不保存 secret；运行时重新检查 presence。
    /// 失败：连接文档或 CredentialStore 无法安全读取时 daemon 启动失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn runtime_snapshot(&self) -> Result<CustomProviderRuntimeSnapshot, AgentError> {
        let configured = self
            .credentials
            .list()
            .map_err(map_credential_error)?
            .into_iter()
            .collect::<BTreeSet<_>>();
        let connections = self
            .connections
            .list()?
            .into_iter()
            .filter(|connection| {
                connection.enabled
                    && !connection.model_ids.is_empty()
                    && configured.contains(&connection.provider_id)
            })
            .collect();
        Ok(CustomProviderRuntimeSnapshot {
            connections,
            credentials: self.credentials.clone(),
        })
    }

    /// 功能：创建不读取动态配置的空运行时快照。
    ///
    /// 输出：持有同一 CredentialStore 句柄但不广告或注册任何自定义连接的快照。
    /// 不变量：用于 identity-only 隔离分支，不访问连接文档或 credential 内容。
    /// 失败：本方法不执行 I/O 且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn empty_runtime_snapshot(&self) -> CustomProviderRuntimeSnapshot {
        CustomProviderRuntimeSnapshot::empty(self.credentials.clone())
    }

    /// 功能：读取并收敛为不含 secret 的 configured Provider ID 集合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn configured_provider_ids(&self) -> Result<BTreeSet<String>, AgentError> {
        Ok(self
            .credentials
            .list()
            .map_err(map_credential_error)?
            .into_iter()
            .collect())
    }
}

/// daemon 启动期固定、同时驱动 adapter、initialize 与 models/list 的自定义连接快照。
#[derive(Clone)]
pub struct CustomProviderRuntimeSnapshot {
    connections: Vec<StoredConnection>,
    credentials: ProviderCredentialStore,
}

impl CustomProviderRuntimeSnapshot {
    /// 功能：创建不含连接的空快照，供 identity-only conformance 分支隔离动态配置。
    ///
    /// 输入：CredentialStore 句柄，仅维持类型一致性。
    /// 输出：不广告也不构造任何动态 adapter 的快照。
    /// 不变量：本方法不读取文件或 credential。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn empty(credentials: ProviderCredentialStore) -> Self {
        Self {
            connections: Vec::new(),
            credentials,
        }
    }

    /// 功能：按可选 Provider ID 投影自定义模型 descriptor。
    ///
    /// 输入：None 表示全部，Some 进行 Provider ID 精确过滤。
    /// 输出：按三元身份排序的 OpenAI Chat 文本模型 descriptor。
    /// 不变量：模型只来自已注册 adapter 的同一启动快照；不读取外部状态。
    /// 失败：本方法不执行 I/O 且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn models(&self, provider_id: Option<&str>) -> Vec<AdvertisedModel> {
        let mut models = self
            .connections
            .iter()
            .filter(|connection| provider_id.is_none_or(|value| connection.provider_id == value))
            .flat_map(|connection| {
                connection.model_ids.iter().map(|model_id| {
                    custom_openai_model(
                        &connection.provider_id,
                        model_id,
                        &connection.api_family,
                        connection.supports_tools,
                    )
                })
            })
            .collect::<Vec<_>>();
        models.sort_by_key(model_key);
        models
    }

    /// 功能：判断 Provider ID 是否属于本次启动已注册的自定义连接。
    ///
    /// 输入：未经信任的 Provider ID。
    /// 输出：与启动快照连接精确匹配时为 true。
    /// 不变量：只读取不可变快照，不根据当前配置或 credential 动态扩大集合。
    /// 失败：本方法不执行 I/O 且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn contains_provider(&self, provider_id: &str) -> bool {
        self.connections
            .iter()
            .any(|connection| connection.provider_id == provider_id)
    }

    /// 功能：把自定义 Provider/model/family selection 解析为固定 Chat API family。
    ///
    /// 输入：Provider ID、model ID 与可选显式 API family。
    /// 输出：三元身份命中时返回 `openai-completions`，否则返回 None 交由其他 registry 处理。
    /// 不变量：只接受启动快照 model allowlist；错误 family 不回退或猜测。
    /// 失败：本方法不执行 I/O 且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn resolve(
        &self,
        provider_id: &str,
        model_id: &str,
        api_family: Option<&str>,
    ) -> Option<String> {
        self.connections
            .iter()
            .any(|connection| {
                connection.provider_id == provider_id
                    && connection.model_ids.iter().any(|model| model == model_id)
                    && api_family.is_none_or(|family| family == connection.api_family)
            })
            .then(|| {
                self.connections
                    .iter()
                    .find(|connection| {
                        connection.provider_id == provider_id
                            && connection.model_ids.iter().any(|model| model == model_id)
                    })
                    .expect("matching connection")
                    .api_family
                    .clone()
            })
    }

    /// 功能：把自定义连接的模型集合并入实际 Provider capabilities。
    ///
    /// 输入：Agent 从真实已注册 adapter 生成的 capabilities。
    /// 输出：按 Provider ID 排序、model ID 去重的 capabilities。
    /// 不变量：自定义模型只来自同一启动快照，不从当前可变配置推断。
    /// 失败：畸形 base DTO 被安全忽略，本方法不执行 I/O。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn merged_provider_capabilities(&self, base: Vec<Value>) -> Vec<Value> {
        let mut grouped = BTreeMap::<String, BTreeSet<String>>::new();
        for provider in base {
            let Some(provider_id) = provider.get("id").and_then(Value::as_str) else {
                continue;
            };
            let models = grouped.entry(provider_id.to_owned()).or_default();
            if let Some(values) = provider.get("models").and_then(Value::as_array) {
                models.extend(values.iter().filter_map(Value::as_str).map(str::to_owned));
            }
        }
        for connection in &self.connections {
            grouped
                .entry(connection.provider_id.clone())
                .or_default()
                .extend(connection.model_ids.iter().cloned());
        }
        grouped
            .into_iter()
            .map(|(provider_id, models)| {
                json!({"id":provider_id,"models":models.into_iter().collect::<Vec<_>>()})
            })
            .collect()
    }

    /// 功能：从快照构造带模型 allowlist 与请求期 CredentialStore 的 Chat adapters。
    ///
    /// 输出：以 `providerId|openai-completions` 为内部 key 的原生 Rust Provider map。
    /// 不变量：adapter 不保存 secret；base URL 只追加固定 `/chat/completions` path；不联网。
    /// 失败：快照 endpoint 无法安全构造时返回脱敏 Provider 配置错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn build_providers(&self) -> Result<BTreeMap<String, Arc<dyn Provider>>, AgentError> {
        let mut providers = BTreeMap::<String, Arc<dyn Provider>>::new();
        for connection in &self.connections {
            let request_path = match connection.api_family.as_str() {
                CHAT_API_FAMILY => "/chat/completions",
                RESPONSES_API_FAMILY => "/responses",
                _ => return Err(invalid_connection("apiFamily", "Provider API family 无效")),
            };
            let endpoint = native_endpoint(&connection.base_url, request_path, &[])?;
            let source = ProviderCredentialSource::from_store(
                self.credentials.clone(),
                connection.provider_id.clone(),
            );
            let inner: Arc<dyn Provider> = match connection.api_family.as_str() {
                CHAT_API_FAMILY => Arc::new(OpenAiChatProvider::with_credential_source(
                    &connection.provider_id,
                    endpoint.to_string(),
                    source,
                )),
                RESPONSES_API_FAMILY => Arc::new(OpenAiResponsesProvider::with_credential_source(
                    &connection.provider_id,
                    endpoint.to_string(),
                    source,
                )),
                _ => unreachable!("validated API family"),
            };
            let provider = CustomConnectionProvider {
                provider_id: connection.provider_id.clone(),
                api_family: connection.api_family.clone(),
                model_ids: connection.model_ids.iter().cloned().collect(),
                supports_tools: connection.supports_tools,
                credentials: self.credentials.clone(),
                inner,
            };
            providers.insert(
                format!("{}|{}", connection.provider_id, connection.api_family),
                Arc::new(provider),
            );
        }
        Ok(providers)
    }

    /// 功能：把自定义 adapters 注册进现有运行时 registry 并实施 Provider ID 全局唯一性。
    ///
    /// 输入：已包含 faux、canonical、推广与 legacy adapters 的完整 registry。
    /// 输出：无 ID 冲突时追加本快照 adapters。
    /// 不变量：即使 API family 不同，自定义 Provider ID 也不得与任何现有 adapter 重名；冲突前不修改 registry。
    /// 失败：endpoint 无效或 Provider ID 冲突时返回不含 ID/URL 的结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn register_into(
        &self,
        providers: &mut BTreeMap<String, Arc<dyn Provider>>,
    ) -> Result<(), AgentError> {
        let custom = self.build_providers()?;
        if custom.values().any(|candidate| {
            providers
                .values()
                .any(|existing| existing.id() == candidate.id())
        }) {
            return Err(provider_id_conflict());
        }
        providers.extend(custom);
        Ok(())
    }
}

/// 为自定义 Chat adapter 固定 API family、模型 allowlist 与请求期 credential presence。
struct CustomConnectionProvider {
    provider_id: String,
    api_family: String,
    model_ids: BTreeSet<String>,
    supports_tools: bool,
    credentials: ProviderCredentialStore,
    inner: Arc<dyn Provider>,
}

#[async_trait]
impl Provider for CustomConnectionProvider {
    /// 功能：返回启动快照固定的 Provider ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn id(&self) -> &str {
        &self.provider_id
    }

    /// 功能：返回连接显式选择的通用 OpenAI API family。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn api_family(&self) -> Option<&str> {
        Some(&self.api_family)
    }

    /// 功能：按启动快照 allowlist 精确判断 model ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn supports_model(&self, model_id: &str) -> bool {
        self.model_ids.contains(model_id)
    }

    /// 功能：返回连接启动快照中的显式 function tool 能力声明。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn supports_tools(&self) -> bool {
        self.supports_tools
    }

    /// 功能：在 durable Session 副作用前重新检查 stored credential presence。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn is_available(&self) -> bool {
        self.credentials.contains(&self.provider_id)
    }

    /// 功能：把已验证请求委托给原生 OpenAI Chat adapter。
    ///
    /// 输入：通用 Provider 请求与运行取消令牌。
    /// 输出：Chat SSE parser 产生的规范化事件流。
    /// 不变量：wrapper 不接触 secret；inner 仅在最终 header 边界读取 credential。
    /// 失败：credential 竞态、HTTP、状态、SSE 或取消失败时返回脱敏结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn stream(
        &self,
        request: ProviderRequest,
        cancellation: CancellationToken,
    ) -> Result<ProviderStream, AgentError> {
        if !self.is_available() {
            return Err(AgentError::new(
                ErrorCode::ProviderUnavailable,
                "provider credential is unavailable",
            )
            .with_details(json!({"kind":"provider_unavailable"})));
        }
        self.inner.stream(request, cancellation).await
    }
}

/// 功能：把 stored record 与 credential ID 集合投影为公开连接 DTO。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn project_connection(
    connection: StoredConnection,
    configured: &BTreeSet<String>,
) -> CustomProviderConnectionView {
    let credential_configured = configured.contains(&connection.provider_id);
    let image_credential_configured =
        configured.contains(&image_credential_id(&connection.connection_id));
    CustomProviderConnectionView {
        connection_id: connection.connection_id,
        revision: connection.revision,
        display_name: connection.display_name,
        provider_id: connection.provider_id,
        api_family: connection.api_family,
        base_url: connection.base_url,
        models_url: connection.models_url,
        model_ids: connection.model_ids,
        supports_tools: connection.supports_tools,
        logo_asset_id: connection.logo_asset_id,
        enabled: connection.enabled,
        credential_configured,
        image_credential_configured,
        created_at: connection.created_at,
        updated_at: connection.updated_at,
    }
}

/// 功能：构造模型发现专用的固定安全 HTTP 客户端。
///
/// 输出：禁止代理和重定向、总时限固定为 20 秒且保持 TLS 校验的客户端。
/// 不变量：不从环境读取代理配置，不自动跨 origin 转发 Bearer header。
/// 失败：HTTP/TLS 客户端初始化失败时返回不含底层错误的 ProviderUnavailable。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn build_model_discovery_client() -> Result<reqwest::Client, AgentError> {
    reqwest::Client::builder()
        .redirect(Policy::none())
        .no_proxy()
        .timeout(MODEL_DISCOVERY_TIMEOUT)
        .build()
        .map_err(|_| model_discovery_unavailable())
}

/// 功能：执行一次 Bearer 认证的模型目录 GET，并解析为规范化模型列表。
///
/// 输入：固定安全客户端、已规范化 `/models` URL，以及最终请求边界读取的 credential 所有权。
/// 输出：严格响应中去重并按 ordinal 排序的 1..512 个模型 ID。
/// 不变量：只接受 200；正文按流累计且最多 1 MiB；credential 不进入错误、日志或返回值。
/// 失败：header、超时、传输、状态、响应超限或严格解析失败时返回固定脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn fetch_discovered_model_ids(
    client: &reqwest::Client,
    endpoint: Url,
    credential: String,
) -> Result<Vec<String>, AgentError> {
    let mut authorization = HeaderValue::from_str(&format!("Bearer {credential}"))
        .map_err(|_| model_discovery_unavailable())?;
    authorization.set_sensitive(true);
    let request = client.get(endpoint).header(AUTHORIZATION, authorization);
    drop(credential);

    let response = request
        .send()
        .await
        .map_err(map_model_discovery_transport_error)?;
    if response.status() != StatusCode::OK {
        return Err(model_discovery_http_error());
    }
    if response
        .content_length()
        .is_some_and(|length| length > MAX_MODEL_DISCOVERY_BYTES as u64)
    {
        return Err(model_discovery_output_limit());
    }

    let mut bytes = Vec::new();
    let mut stream = response.bytes_stream();
    while let Some(chunk) = stream.next().await {
        let chunk = chunk.map_err(map_model_discovery_transport_error)?;
        if bytes.len().saturating_add(chunk.len()) > MAX_MODEL_DISCOVERY_BYTES {
            return Err(model_discovery_output_limit());
        }
        bytes.extend_from_slice(&chunk);
    }
    parse_discovered_model_ids(&bytes)
}

/// 功能：严格解析 OpenAI-compatible 模型目录并规范化模型 ID。
///
/// 输入：已通过 1 MiB 网络正文上限的原始字节。
/// 输出：去重、ordinal 排序且非空的最多 512 个合法模型 ID。
/// 不变量：递归拒绝重复 JSON key；只投影根 `data` 和每项 `id`，忽略其余元数据；模型 ID 以 UTF-8 字节数计 1..256 且无控制字符。
/// 失败：UTF-8、JSON、DTO、ID 或空目录错误返回 ProviderError，模型数量超限返回 OutputLimitExceeded。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_discovered_model_ids(bytes: &[u8]) -> Result<Vec<String>, AgentError> {
    let text = std::str::from_utf8(bytes).map_err(|_| model_discovery_invalid_response())?;
    let value = parse_strict_value(text).map_err(|_| model_discovery_invalid_response())?;
    let response: ModelDiscoveryResponse =
        serde_json::from_value(value).map_err(|_| model_discovery_invalid_response())?;
    let mut model_ids = BTreeSet::new();
    for item in response.data {
        if !model_id_is_valid(&item.id) {
            return Err(model_discovery_invalid_response());
        }
        model_ids.insert(item.id);
        if model_ids.len() > MAX_MODEL_IDS {
            return Err(model_discovery_output_limit());
        }
    }
    if model_ids.is_empty() {
        return Err(model_discovery_invalid_response());
    }
    Ok(model_ids.into_iter().collect())
}

/// 功能：验证一个模型 ID 的冻结字节和字符边界。
///
/// 输入：来自 RPC 或远端模型目录的 UTF-8 字符串。
/// 输出：字节数为 1..256 且不含 Unicode 控制字符时为 true。
/// 不变量：使用字节长度而非字符数量，与跨语言协议限制一致。
/// 失败：本函数不执行 I/O 且不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn model_id_is_valid(value: &str) -> bool {
    !value.is_empty() && value.len() <= MAX_MODEL_ID_BYTES && !value.chars().any(char::is_control)
}

/// 功能：验证持久化模型 allowlist 的容量、单项边界与唯一性。
///
/// 输入：模型 ID 切片，以及是否允许尚未完成模型发现的空列表。
/// 输出：全部冻结约束满足时为 true。
/// 不变量：列表最多 512 项；非空项均满足统一模型 ID 边界且不得重复。
/// 失败：本函数不执行 I/O 且不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn model_ids_are_valid(model_ids: &[String], allow_empty: bool) -> bool {
    if (!allow_empty && model_ids.is_empty()) || model_ids.len() > MAX_MODEL_IDS {
        return false;
    }
    let mut unique = BTreeSet::new();
    model_ids
        .iter()
        .all(|model_id| model_id_is_valid(model_id) && unique.insert(model_id))
}

/// 功能：验证完整连接文档的版本、容量、唯一性、排序无关字段边界。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_document(
    document: &ConnectionDocument,
    allow_conformance_loopback: bool,
) -> Result<(), AgentError> {
    if document.schema_version != DOCUMENT_SCHEMA_VERSION
        || document.connections.len() > MAX_CONNECTIONS
    {
        return Err(connection_store_error("shape", "Provider 连接配置文档无效"));
    }
    let mut connection_ids = BTreeSet::new();
    let mut provider_ids = BTreeSet::new();
    let mut previous_connection_id: Option<&str> = None;
    for connection in &document.connections {
        validate_stored_connection(connection, allow_conformance_loopback)?;
        if !connection_ids.insert(&connection.connection_id)
            || previous_connection_id
                .is_some_and(|previous| previous >= connection.connection_id.as_str())
        {
            return Err(connection_store_error(
                "duplicate_connection",
                "Provider 连接标识重复或顺序无效",
            ));
        }
        if !provider_ids.insert(&connection.provider_id) {
            return Err(connection_store_error(
                "duplicate_provider",
                "Provider ID 重复",
            ));
        }
        previous_connection_id = Some(&connection.connection_id);
    }
    Ok(())
}

/// 功能：验证 stored record 的 ID、revision、输入与 RFC3339 时间。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_stored_connection(
    connection: &StoredConnection,
    allow_conformance_loopback: bool,
) -> Result<(), AgentError> {
    validate_safe_id(&connection.connection_id, 128, "connectionId")?;
    validate_revision(connection.revision)?;
    validate_connection_input(
        &CustomProviderConnectionInput {
            display_name: connection.display_name.clone(),
            provider_id: connection.provider_id.clone(),
            api_family: connection.api_family.clone(),
            base_url: connection.base_url.clone(),
            models_url: connection.models_url.clone(),
            model_ids: connection.model_ids.clone(),
            supports_tools: connection.supports_tools,
            logo_asset_id: connection.logo_asset_id.clone(),
            enabled: connection.enabled,
        },
        allow_conformance_loopback,
    )?;
    validate_timestamp(&connection.created_at)?;
    validate_timestamp(&connection.updated_at)?;
    let created_at = chrono::DateTime::parse_from_rfc3339(&connection.created_at)
        .map_err(|_| connection_store_error("timestamp", "Provider 连接时间无效"))?;
    let updated_at = chrono::DateTime::parse_from_rfc3339(&connection.updated_at)
        .map_err(|_| connection_store_error("timestamp", "Provider 连接时间无效"))?;
    if created_at > updated_at {
        return Err(connection_store_error(
            "timestamp",
            "Provider 连接时间顺序无效",
        ));
    }
    Ok(())
}

/// 功能：验证 RPC 连接输入的全部品牌中立边界。
///
/// 输入：显示名、Provider ID、0..512 项唯一模型列表、可选 Logo ID、family、base URL 与 enabled。
/// 输出：全部字段满足约束时成功。
/// 不变量：生产 URL 仅 HTTPS；conformance 例外仅允许字面 `127.0.0.1` HTTP。
/// 失败：任一字段无效时返回不回显实例值的 `invalid_params`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_connection_input(
    input: &CustomProviderConnectionInput,
    allow_conformance_loopback: bool,
) -> Result<(), AgentError> {
    if input.display_name.chars().count() == 0
        || input.display_name.chars().count() > 64
        || input.display_name.chars().any(char::is_control)
        || input.display_name.trim() != input.display_name
    {
        return Err(invalid_connection("displayName", "Provider 显示名无效"));
    }
    validate_safe_id(&input.provider_id, 128, "providerId")?;
    if input.provider_id == "faux"
        || input.provider_id.starts_with("relay-")
        || is_canonical_provider_id(&input.provider_id)
    {
        return Err(invalid_connection("providerId", "Provider ID 已被保留"));
    }
    if !matches!(
        input.api_family.as_str(),
        CHAT_API_FAMILY | RESPONSES_API_FAMILY
    ) {
        return Err(invalid_connection("apiFamily", "Provider API family 无效"));
    }
    validate_base_url(&input.base_url, allow_conformance_loopback)?;
    validate_base_url(&input.models_url, allow_conformance_loopback)
        .map_err(|_| invalid_connection("modelsUrl", "Provider 模型目录 URL 无效"))?;
    if !model_ids_are_valid(&input.model_ids, true) {
        return Err(invalid_connection("modelIds", "Provider 模型列表无效"));
    }
    if let Some(logo_asset_id) = &input.logo_asset_id {
        validate_safe_id(logo_asset_id, 128, "logoAssetId")?;
    }
    Ok(())
}

/// 功能：在进入 CredentialStore 前验证 secret 的 HTTP header 安全边界。
///
/// 输入：RPC 请求内存中的 credential 文本。
/// 输出：1..16384 bytes 且无 CR/LF/NUL 时成功。
/// 不变量：错误不包含 credential、长度或摘要；本函数不写持久化。
/// 失败：空值、超限或 header injection 字符返回固定 `invalid_params/credential`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_credential(value: &str) -> Result<(), AgentError> {
    if value.is_empty() || value.len() > 16_384 || value.contains(['\r', '\n', '\0']) {
        return Err(invalid_connection("credential", "Provider credential 无效"));
    }
    Ok(())
}

/// 功能：验证自定义 Provider credential 的独立用途。
///
/// 输入：协议中的 `responses` 或 `image`。
/// 输出：命中封闭枚举时成功。
/// 不变量：未知用途不能退回通用、Codex 或环境 credential。
/// 失败：未知值返回固定 `invalid_params/credentialKind`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_credential_kind(value: &str) -> Result<(), AgentError> {
    if matches!(value, RESPONSES_CREDENTIAL_KIND | IMAGE_CREDENTIAL_KIND) {
        Ok(())
    } else {
        Err(invalid_connection(
            "credentialKind",
            "Provider credential kind 无效",
        ))
    }
}

/// 功能：派生只属于当前连接的 Image credential store identity。
///
/// 输入：服务生成且已验证的 connection ID。
/// 输出：符合 CredentialStore ID 语法且不会与 Responses Provider ID 共用的 identity。
/// 不变量：结果不含 secret，且长度保持在 128 字节内。
/// 失败：调用方已验证 connection ID，本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn image_credential_id(connection_id: &str) -> String {
    format!("{connection_id}.image")
}

/// 功能：把 credential 用途映射到互不回退的 CredentialStore identity。
///
/// 输入：已验证连接与已验证用途。
/// 输出：Responses 使用公开 Provider ID，Image 使用连接级派生 ID。
/// 不变量：两类 key 永不共享同一 store entry。
/// 失败：调用方已验证用途，本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn credential_store_id(connection: &StoredConnection, credential_kind: &str) -> String {
    match credential_kind {
        RESPONSES_CREDENTIAL_KIND => connection.provider_id.clone(),
        IMAGE_CREDENTIAL_KIND => image_credential_id(&connection.connection_id),
        _ => unreachable!("validated credential kind"),
    }
}

/// 功能：验证 base URL 为无 userinfo/query/fragment 的 HTTPS origin/path。
///
/// 输入：URL 文本与双门 conformance loopback 权限。
/// 输出：生产 HTTPS 或 conformance `http://127.0.0.1[:port]` 时成功。
/// 不变量：URL 长度最多 2048，必须有 host，且固定 path 拼接不能改变 authority。
/// 失败：解析、scheme、host 或敏感 URL 部分无效时返回不含原值的参数错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_base_url(value: &str, allow_conformance_loopback: bool) -> Result<(), AgentError> {
    let url =
        Url::parse(value).map_err(|_| invalid_connection("baseUrl", "Provider base URL 无效"))?;
    let loopback =
        allow_conformance_loopback && url.scheme() == "http" && url.host_str() == Some("127.0.0.1");
    if value.len() > 2048
        || (url.scheme() != "https" && !loopback)
        || url.host().is_none()
        || !url.username().is_empty()
        || url.password().is_some()
        || url.query().is_some()
        || url.fragment().is_some()
    {
        return Err(invalid_connection("baseUrl", "Provider base URL 无效"));
    }
    Ok(())
}

/// 功能：验证品牌中立安全 ID 的受限 ASCII 语法。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_safe_id(value: &str, maximum: usize, field: &str) -> Result<(), AgentError> {
    if value.is_empty()
        || value.len() > maximum
        || !value
            .bytes()
            .next()
            .is_some_and(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit())
        || !value.bytes().all(|byte| {
            byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'-' || byte == b'.'
        })
    {
        return Err(invalid_connection(field, "Provider 连接标识无效"));
    }
    Ok(())
}

/// 功能：验证 CAS revision 位于 JSON 安全整数范围。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_revision(value: u64) -> Result<(), AgentError> {
    if value == 0 || value > MAX_SAFE_INTEGER {
        return Err(invalid_connection(
            "expectedRevision",
            "Provider revision 无效",
        ));
    }
    Ok(())
}

/// 功能：验证持久化时间为规范 UTC RFC3339 文本。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_timestamp(value: &str) -> Result<(), AgentError> {
    if value.len() > 40
        || !value.ends_with('Z')
        || chrono::DateTime::parse_from_rfc3339(value).is_err()
    {
        return Err(connection_store_error("timestamp", "Provider 连接时间无效"));
    }
    Ok(())
}

/// 功能：生成 millisecond 精度的 UTC RFC3339 时间。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn current_timestamp() -> String {
    Utc::now().to_rfc3339_opts(SecondsFormat::Millis, true)
}

/// 功能：取得不早于上一更新时间的 millisecond UTC RFC3339 时间。
///
/// 输入：已通过 stored document 验证的上一更新时间。
/// 输出：系统当前时间或较晚的上一时间原值。
/// 不变量：系统时钟回拨不会破坏 `createdAt <= updatedAt` 或跨运行时文档不变量。
/// 失败：上一时间已由调用链验证；解析意外失败时安全保留上一原值。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn current_timestamp_not_before(previous: &str) -> String {
    let current = current_timestamp();
    match (
        chrono::DateTime::parse_from_rfc3339(&current),
        chrono::DateTime::parse_from_rfc3339(previous),
    ) {
        (Ok(current_time), Ok(previous_time)) if current_time >= previous_time => current,
        _ => previous.to_owned(),
    }
}

/// 功能：按 connection ID ordinal 稳定排序记录，保持跨运行时文档一致。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn sort_connections(connections: &mut [StoredConnection]) {
    connections.sort_by(|left, right| left.connection_id.cmp(&right.connection_id));
}

/// 功能：创建私有目录并拒绝 symlink 目录替换。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn create_private_directory(path: &Path) -> Result<(), AgentError> {
    reject_symlink_if_present(path)?;
    std::fs::create_dir_all(path)
        .map_err(|_| connection_store_error("directory", "Provider 连接状态目录创建失败"))?;
    let metadata = std::fs::symlink_metadata(path)
        .map_err(|_| connection_store_error("directory", "Provider 连接状态目录无效"))?;
    if metadata_is_unsafe(&metadata) || !metadata.is_dir() {
        return Err(connection_store_error(
            "directory_shape",
            "Provider 连接状态目录无效",
        ));
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt as _;
        std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o700)).map_err(|_| {
            connection_store_error("directory_permissions", "Provider 连接状态目录权限设置失败")
        })?;
    }
    Ok(())
}

/// 功能：存在路径时拒绝 symlink，避免状态文件被重定向。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn reject_symlink_if_present(path: &Path) -> Result<(), AgentError> {
    match std::fs::symlink_metadata(path) {
        Ok(metadata) if metadata_is_unsafe(&metadata) => Err(connection_store_error(
            "symlink",
            "Provider 连接状态路径不能是链接或 reparse point",
        )),
        Ok(_) => Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(_) => Err(connection_store_error(
            "metadata",
            "Provider 连接状态元数据读取失败",
        )),
    }
}

/// 功能：以 no-follow create 模式打开固定 lock file。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn open_read_write_create(path: &Path) -> Result<File, AgentError> {
    let mut options = OpenOptions::new();
    options.read(true).write(true).create(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt as _;
        options.custom_flags(libc::O_NOFOLLOW).mode(0o600);
    }
    #[cfg(windows)]
    options.custom_flags(FILE_FLAG_OPEN_REPARSE_POINT);
    let file = options
        .open(path)
        .map_err(|_| connection_store_error("lock_open", "Provider 连接 lock 打开失败"))?;
    let metadata = file
        .metadata()
        .map_err(|_| connection_store_error("metadata", "Provider 连接 lock 元数据无效"))?;
    if metadata_is_unsafe(&metadata) || !metadata.is_file() {
        return Err(connection_store_error(
            "lock_shape",
            "Provider 连接 lock 不是普通文件",
        ));
    }
    Ok(file)
}

/// 功能：no-follow、有界读取一个非空普通 JSON 文件。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn read_bounded_file(path: &Path, maximum: usize) -> Result<Vec<u8>, AgentError> {
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
        .map_err(|_| connection_store_error("file_read", "Provider 连接配置读取失败"))?;
    let metadata = file
        .metadata()
        .map_err(|_| connection_store_error("metadata", "Provider 连接配置元数据无效"))?;
    if metadata_is_unsafe(&metadata)
        || !metadata.is_file()
        || metadata.len() == 0
        || metadata.len() > maximum as u64
    {
        return Err(connection_store_error(
            "file_shape",
            "Provider 连接配置文件形状无效",
        ));
    }
    let mut bytes = Vec::with_capacity(metadata.len() as usize);
    Read::by_ref(&mut file)
        .take(maximum.saturating_add(1) as u64)
        .read_to_end(&mut bytes)
        .map_err(|_| connection_store_error("file_read", "Provider 连接配置读取失败"))?;
    if bytes.len() > maximum {
        return Err(connection_store_error(
            "file_shape",
            "Provider 连接配置文件形状无效",
        ));
    }
    Ok(bytes)
}

/// 功能：判断 metadata 是否表示 symlink、Windows reparse point 或 device。
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

/// 功能：通过 strict Value parser 解析 JSON 并拒绝重复 key 与未知 DTO 字段。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_strict_json<T: DeserializeOwned>(bytes: &[u8]) -> Result<T, AgentError> {
    let text = std::str::from_utf8(bytes)
        .map_err(|_| connection_store_error("json", "Provider 连接配置 JSON 无效"))?;
    let value = parse_strict_value(text)
        .map_err(|_| connection_store_error("json", "Provider 连接配置 JSON 无效"))?;
    serde_json::from_value(value)
        .map_err(|_| connection_store_error("json", "Provider 连接配置 JSON 字段无效"))
}

/// 功能：以同目录 0600 create-new 临时文件、sync 与跨平台 replace 原子发布文档。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn atomic_write(path: &Path, bytes: &[u8]) -> Result<(), AgentError> {
    let parent = path
        .parent()
        .ok_or_else(|| connection_store_error("path", "Provider 连接配置路径无效"))?;
    create_private_directory(parent)?;
    reject_symlink_if_present(path)?;
    let mut temporary = tempfile::Builder::new()
        .prefix(".provider-connections-")
        .suffix(".tmp")
        .tempfile_in(parent)
        .map_err(|_| connection_store_error("file_create", "Provider 连接临时文件创建失败"))?;
    temporary
        .write_all(bytes)
        .map_err(|_| connection_store_error("file_write", "Provider 连接配置写入失败"))?;
    temporary
        .as_file()
        .sync_all()
        .map_err(|_| connection_store_error("file_sync", "Provider 连接配置同步失败"))?;
    temporary
        .persist(path)
        .map_err(|_| connection_store_error("file_publish", "Provider 连接配置发布失败"))?;
    #[cfg(unix)]
    File::open(parent)
        .and_then(|directory| directory.sync_all())
        .map_err(|_| connection_store_error("directory_sync", "Provider 连接目录同步失败"))?;
    Ok(())
}

/// 功能：构造不含路径、URL、revision 或 secret 的配置参数错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn invalid_connection(field: &str, message: impl Into<String>) -> AgentError {
    AgentError::new(ErrorCode::InvalidParams, message)
        .with_details(json!({"kind":"invalid_params","field":field}))
}

/// 功能：构造不含实际 revision 的 CAS 冲突错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn revision_conflict() -> AgentError {
    AgentError::new(ErrorCode::Conflict, "Provider 连接 revision 冲突")
        .retryable(true)
        .with_details(json!({"kind":"provider_connection_revision_conflict"}))
}

/// 功能：构造不含请求 ID 的连接不存在错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn connection_not_found() -> AgentError {
    AgentError::new(ErrorCode::InvalidParams, "Provider 连接不存在")
        .with_details(json!({"kind":"provider_connection_not_found"}))
}

/// 功能：构造不含冲突 ID 的 Provider 全局唯一性错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn provider_id_conflict() -> AgentError {
    AgentError::new(ErrorCode::Conflict, "Provider ID 已存在")
        .retryable(true)
        .with_details(json!({"kind":"provider_id_conflict"}))
}

/// 功能：把模型发现 reqwest 错误收敛为不含网络私有细节的稳定错误。
///
/// 输入：可能携带 URL 或底层连接文本的 reqwest 错误。
/// 输出：超时映射为 Timeout，其余传输失败映射为 ProviderUnavailable。
/// 不变量：不传播原错误消息、URL、header 或响应内容。
/// 失败：本函数只转换错误且不返回成功值。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn map_model_discovery_transport_error(error: reqwest::Error) -> AgentError {
    if error.is_timeout() {
        AgentError::new(ErrorCode::Timeout, "Provider 模型发现超时")
            .retryable(true)
            .with_details(json!({"kind":"provider_model_discovery_timeout"}))
    } else {
        model_discovery_unavailable()
    }
}

/// 功能：构造模型发现客户端、凭据 header 或传输不可用的固定错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn model_discovery_unavailable() -> AgentError {
    AgentError::new(ErrorCode::ProviderUnavailable, "Provider 模型发现不可用")
        .retryable(true)
        .with_details(json!({"kind":"provider_model_discovery_unavailable"}))
}

/// 功能：构造模型目录返回非 200 状态时的固定错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn model_discovery_http_error() -> AgentError {
    AgentError::new(ErrorCode::ProviderError, "Provider 模型目录请求失败")
        .with_details(json!({"kind":"provider_model_discovery_http_error"}))
}

/// 功能：构造模型目录正文或模型数量超过冻结上限的固定错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn model_discovery_output_limit() -> AgentError {
    AgentError::new(
        ErrorCode::OutputLimitExceeded,
        "Provider 模型目录超过输出上限",
    )
    .with_details(json!({"kind":"provider_model_discovery_output_limit"}))
}

/// 功能：构造模型目录 JSON、DTO、ID 或空目录无效的固定错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn model_discovery_invalid_response() -> AgentError {
    AgentError::new(ErrorCode::ProviderError, "Provider 模型目录响应无效")
        .with_details(json!({"kind":"provider_model_discovery_invalid_response"}))
}

/// 功能：把 CredentialStore 内部错误收敛为 Provider connection 固定 store 分类。
///
/// 输入：可能包含内部操作 kind、但已保证不含 credential 值的结构化错误。
/// 输出：只保留 locked、invalid 或 io 公共分类的新错误。
/// 不变量：不传播原消息、路径、内部 kind 或底层错误详情。
/// 失败：本函数只转换错误且不返回成功值。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn map_credential_error(error: AgentError) -> AgentError {
    let kind = error
        .details
        .get("kind")
        .and_then(Value::as_str)
        .unwrap_or_default();
    if kind.contains("lock") {
        connection_store_error("locked", "Provider credential store 正被其他进程使用")
    } else if [
        "json",
        "shape",
        "duplicate",
        "symlink",
        "metadata",
        "permissions",
        "provider_id",
        "credential_value",
        "path",
    ]
    .iter()
    .any(|marker| kind.contains(marker))
    {
        connection_store_error("credential_invalid", "Provider credential store 无效")
    } else {
        connection_store_error("credential_io", "Provider credential store 不可用")
    }
}

/// 功能：把内部操作分类收敛为不含路径、内容或底层错误链的固定 store 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn connection_store_error(kind: &str, message: impl Into<String>) -> AgentError {
    let invalid = matches!(
        kind,
        "path"
            | "path_boundary"
            | "directory_shape"
            | "symlink"
            | "metadata"
            | "lock_shape"
            | "file_shape"
            | "json"
            | "shape"
            | "duplicate_connection"
            | "duplicate_provider"
            | "timestamp"
            | "credential_invalid"
    );
    let (code, retryable, public_kind) = if kind == "locked" {
        (
            ErrorCode::SessionLocked,
            true,
            "provider_connection_store_locked",
        )
    } else if invalid {
        (
            ErrorCode::InternalError,
            false,
            "provider_connection_store_invalid",
        )
    } else {
        (
            ErrorCode::InternalError,
            false,
            "provider_connection_store_io",
        )
    };
    AgentError::new(code, message)
        .retryable(retryable)
        .with_details(json!({"kind":public_kind}))
}

#[cfg(test)]
mod tests {
    use std::fs;
    use std::io::{Read as _, Write as _};
    use std::net::TcpListener;
    use std::sync::mpsc;
    use std::thread;
    use std::time::Duration;

    use reqwest::Url;
    use serde_json::json;
    use tempfile::tempdir;

    use super::{
        CustomProviderConnectionInput, CustomProviderConnectionService,
        CustomProviderConnectionStore, ProviderConnectionsCreateParams,
        ProviderConnectionsDiscoverModelsParams, build_model_discovery_client,
        fetch_discovered_model_ids, parse_discovered_model_ids, validate_base_url,
    };
    use crate::commercial_state::ProviderCredentialStore;

    /// 功能：创建测试使用的合法连接输入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn input(provider_id: &str, enabled: bool) -> CustomProviderConnectionInput {
        CustomProviderConnectionInput {
            display_name: "本地 New API".to_owned(),
            provider_id: provider_id.to_owned(),
            api_family: "openai-completions".to_owned(),
            base_url: "https://example.test/v1".to_owned(),
            models_url: "https://example.test/v1/models".to_owned(),
            model_ids: vec!["model-a".to_owned(), "model-b".to_owned()],
            supports_tools: false,
            logo_asset_id: Some("new-api".to_owned()),
            enabled,
        }
    }

    /// 功能：构造物理分离的连接与 credential store 测试服务。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn service(
        state: &Path,
        workspace: &Path,
    ) -> Result<CustomProviderConnectionService, crate::error::AgentError> {
        let connections = CustomProviderConnectionStore::new(state, false)?;
        let credentials = ProviderCredentialStore::new(state, workspace)?;
        Ok(CustomProviderConnectionService::new(
            connections,
            credentials,
        ))
    }

    use std::path::Path;

    /// 功能：验证 CRUD 持久化、Provider ID 唯一性和 CAS 错误不泄漏 revision。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn persists_connections_and_enforces_cas() -> Result<(), Box<dyn std::error::Error>> {
        let root = tempdir()?;
        let state = root.path().join("state");
        let workspace = root.path().join("workspace");
        fs::create_dir_all(&workspace)?;
        let service = service(&state, &workspace)?;
        let created = service.create(input("custom-one", true))?;
        assert_eq!(created.revision, 1);
        assert!(!created.credential_configured);
        assert_eq!(service.list()?.len(), 1);

        let duplicate = service
            .create(input("custom-one", false))
            .expect_err("duplicate");
        assert_eq!(duplicate.details["kind"], "provider_id_conflict");
        let conflict = service
            .update(&created.connection_id, 2, input("custom-one", false))
            .expect_err("CAS conflict");
        assert_eq!(
            conflict.details,
            json!({"kind":"provider_connection_revision_conflict"})
        );
        assert!(!conflict.message.contains('2'));

        service.set_credential("custom-one", "responses", "stable-secret")?;
        let stale_delete = service
            .delete(&created.connection_id, 2)
            .expect_err("stale delete must fail before credential removal");
        assert_eq!(
            stale_delete.details["kind"],
            "provider_connection_revision_conflict"
        );
        assert!(service.list()?[0].credential_configured);

        let updated = service.update(
            &created.connection_id,
            created.revision,
            input("custom-one", false),
        )?;
        assert_eq!(updated.revision, 2);
        service.delete(&updated.connection_id, updated.revision)?;
        assert!(service.list()?.is_empty());
        Ok(())
    }

    /// 功能：验证 Responses 与 Image key 独立存取，且 Image key 不会让文本模型快照变为可执行。
    ///
    /// 不变量：移除一种 key 不影响另一种；删除连接必须清理两种 store identity。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn isolates_responses_and_image_credentials() -> Result<(), Box<dyn std::error::Error>> {
        let root = tempdir()?;
        let state = root.path().join("state");
        let workspace = root.path().join("workspace");
        fs::create_dir_all(&workspace)?;
        let service = service(&state, &workspace)?;
        let created = service.create(input("dual-key", true))?;
        let image_credential_id = format!("{}.image", created.connection_id);

        service.set_credential("dual-key", "responses", "responses-secret")?;
        service.set_credential("dual-key", "image", "image-secret")?;

        let configured = service.list()?.remove(0);
        assert!(configured.credential_configured);
        assert!(configured.image_credential_configured);
        assert_eq!(
            service.credentials.read_for_request("dual-key")?,
            "responses-secret"
        );
        assert_eq!(
            service.credentials.read_for_request(&image_credential_id)?,
            "image-secret"
        );

        service.remove_credential("dual-key", "responses")?;

        let image_only = service.list()?.remove(0);
        assert!(!image_only.credential_configured);
        assert!(image_only.image_credential_configured);
        assert!(service.credentials.read_for_request("dual-key").is_err());
        assert!(service.runtime_snapshot()?.models(None).is_empty());
        assert_eq!(
            service.credentials.read_for_request(&image_credential_id)?,
            "image-secret"
        );

        service.set_credential("dual-key", "responses", "responses-secret")?;
        service.delete(&created.connection_id, created.revision)?;
        assert!(service.credentials.list()?.is_empty());
        Ok(())
    }

    /// 功能：验证创建连接会先清理同 Provider ID 的 CLI orphan credential。
    ///
    /// 不变量：旧 secret 不得绑定到新 endpoint，创建响应必须报告 credential 未配置。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn create_clears_destination_orphan_credential() -> Result<(), Box<dyn std::error::Error>> {
        let root = tempdir()?;
        let state = root.path().join("state");
        let workspace = root.path().join("workspace");
        fs::create_dir_all(&workspace)?;
        let service = service(&state, &workspace)?;
        service.set_credential_coordinated("create.orphan", "historical-secret")?;

        let created = service.create(input("create.orphan", true))?;

        assert!(!created.credential_configured);
        assert!(
            service
                .credentials
                .read_for_request("create.orphan")
                .is_err()
        );
        Ok(())
    }

    /// 功能：验证所有 credential mutation 先持有 connection lock，且 Provider rename 不会继承任一历史 orphan credential。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn credential_mutations_follow_connection_then_credential_lock_order()
    -> Result<(), Box<dyn std::error::Error>> {
        let root = tempdir()?;
        let state = root.path().join("state");
        let workspace = root.path().join("workspace");
        fs::create_dir_all(&workspace)?;
        let service = service(&state, &workspace)?;
        let created = service.create(input("lock-ordered", true))?;
        service.set_credential("lock-ordered", "responses", "original-secret")?;

        let connection_lock = service.connections.acquire_lock()?;
        let set_error = service
            .set_credential("lock-ordered", "responses", "replacement-secret")
            .expect_err("set must acquire connection lock first");
        let remove_error = service
            .remove_credential("lock-ordered", "responses")
            .expect_err("remove must acquire connection lock first");
        let cli_set_error = service
            .set_credential_coordinated("canonical.example", "cli-secret")
            .expect_err("generic CLI set must acquire connection lock first");
        let cli_remove_error = service
            .remove_credential_coordinated("canonical.example")
            .expect_err("generic CLI remove must acquire connection lock first");
        assert_eq!(set_error.code, crate::error::ErrorCode::SessionLocked);
        assert_eq!(remove_error.code, crate::error::ErrorCode::SessionLocked);
        assert_eq!(cli_set_error.code, crate::error::ErrorCode::SessionLocked);
        assert_eq!(
            cli_remove_error.code,
            crate::error::ErrorCode::SessionLocked
        );
        drop(connection_lock);
        assert_eq!(
            service.credentials.read_for_request("lock-ordered")?,
            "original-secret"
        );
        service.set_credential_coordinated("renamed-provider", "orphan-secret")?;
        assert_eq!(
            service.credentials.read_for_request("renamed-provider")?,
            "orphan-secret"
        );

        let renamed = service.update(
            &created.connection_id,
            created.revision,
            input("renamed-provider", true),
        )?;
        assert!(!renamed.credential_configured);
        assert!(
            service
                .credentials
                .read_for_request("lock-ordered")
                .is_err()
        );
        assert!(
            service
                .credentials
                .read_for_request("renamed-provider")
                .is_err()
        );
        service.set_credential_coordinated("canonical.example", "cli-secret")?;
        assert_eq!(
            service.credentials.read_for_request("canonical.example")?,
            "cli-secret"
        );
        assert!(service.remove_credential_coordinated("canonical.example")?);
        Ok(())
    }

    /// 功能：验证 credential 仅进入独立 store，连接文档与所有 DTO 永不包含 secret。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn keeps_credentials_out_of_connection_document() -> Result<(), Box<dyn std::error::Error>> {
        let root = tempdir()?;
        let state = root.path().join("state");
        let workspace = root.path().join("workspace");
        fs::create_dir_all(&workspace)?;
        let service = service(&state, &workspace)?;
        let _ = service.create(input("custom-two", true))?;
        let invalid = service
            .set_credential("custom-two", "responses", "bad\r\ncredential")
            .expect_err("CRLF credential must be rejected before store mutation");
        assert_eq!(invalid.code, crate::error::ErrorCode::InvalidParams);
        assert_eq!(
            invalid.details,
            json!({"kind":"invalid_params","field":"credential"})
        );
        assert!(!service.list()?[0].credential_configured);
        service.set_credential("custom-two", "responses", "test-secret-value")?;

        let listed = service.list()?;
        assert!(listed[0].credential_configured);
        let projection = serde_json::to_string(&listed)?;
        assert!(!projection.contains("test-secret-value"));
        let document = fs::read_to_string(
            state
                .join("provider-connections")
                .join("provider-connections.json"),
        )?;
        assert!(!document.contains("secret"));
        assert!(!document.contains("credential"));

        service.remove_credential("custom-two", "responses")?;
        assert!(!service.list()?[0].credential_configured);
        Ok(())
    }

    /// 功能：验证启动快照只注册 enabled 且有 credential 的连接并固定模型 allowlist。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn runtime_snapshot_is_executable_and_model_qualified() -> Result<(), Box<dyn std::error::Error>>
    {
        let root = tempdir()?;
        let state = root.path().join("state");
        let workspace = root.path().join("workspace");
        fs::create_dir_all(&workspace)?;
        let service = service(&state, &workspace)?;
        let _ = service.create(input("enabled-provider", true))?;
        let _ = service.create(input("disabled-provider", false))?;
        service.set_credential("enabled-provider", "responses", "enabled-key")?;
        service.set_credential("disabled-provider", "responses", "disabled-key")?;

        let snapshot = service.runtime_snapshot()?;
        let models = snapshot.models(None);
        let model_json = serde_json::to_value(models)?;
        assert_eq!(model_json.as_array().map(Vec::len), Some(2));
        assert!(model_json.as_array().is_some_and(|models| {
            models
                .iter()
                .all(|model| model["providerId"] == "enabled-provider")
        }));
        assert!(model_json.as_array().is_some_and(|models| {
            models
                .iter()
                .all(|model| model["capabilities"]["tools"] == false)
        }));
        let providers = snapshot.build_providers()?;
        assert_eq!(providers.len(), 1);
        let provider = providers.values().next().ok_or("missing provider")?;
        assert_eq!(provider.id(), "enabled-provider");
        assert_eq!(provider.api_family(), Some("openai-completions"));
        assert!(provider.supports_model("model-a"));
        assert!(!provider.supports_model("unknown"));
        assert!(!provider.supports_tools());
        Ok(())
    }

    /// 功能：验证 URL 生产 HTTPS 边界和 conformance 字面 loopback 例外。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn validates_base_url_boundary() {
        assert!(validate_base_url("https://example.test/v1", false).is_ok());
        assert!(validate_base_url("https://user@example.test/v1", false).is_err());
        assert!(validate_base_url("https://example.test/v1?q=1", false).is_err());
        assert!(validate_base_url("https://example.test/v1#part", false).is_err());
        assert!(validate_base_url("http://127.0.0.1:8080/v1", false).is_err());
        assert!(validate_base_url("http://127.0.0.1:8080/v1", true).is_ok());
        assert!(validate_base_url("http://localhost:8080/v1", true).is_err());
    }

    /// 功能：验证点号 ID 与 schema 对齐，同时拒绝 faux、推广及全部 canonical 身份遮蔽。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn validates_provider_identity_namespace() -> Result<(), Box<dyn std::error::Error>> {
        let root = tempdir()?;
        let state = root.path().join("state");
        let workspace = root.path().join("workspace");
        fs::create_dir_all(&workspace)?;
        let service = service(&state, &workspace)?;

        let dotted = service.create(input("custom.example", true))?;
        assert_eq!(dotted.provider_id, "custom.example");
        for reserved in ["faux", "relay-example", "anthropic", "deepseek", "openai"] {
            let error = service
                .create(input(reserved, true))
                .expect_err("reserved Provider ID must fail closed");
            assert_eq!(error.details["field"], "providerId");
        }
        Ok(())
    }

    /// 功能：验证 dangling symlink 文档不会被误判为不存在的空配置。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(unix)]
    #[test]
    fn rejects_dangling_symlink_connection_document() -> Result<(), Box<dyn std::error::Error>> {
        use std::os::unix::fs::symlink;

        let root = tempdir()?;
        let state = root.path().join("state");
        let workspace = root.path().join("workspace");
        fs::create_dir_all(&workspace)?;
        let service = service(&state, &workspace)?;
        symlink(
            root.path().join("missing-target"),
            state
                .join("provider-connections")
                .join("provider-connections.json"),
        )?;
        let error = service
            .list()
            .expect_err("dangling symlink must fail closed");
        assert_eq!(error.details["kind"], "provider_connection_store_invalid");
        Ok(())
    }

    /// 功能：验证整个 state root 被符号链接替换时在创建连接目录前拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(unix)]
    #[test]
    fn rejects_symlinked_connection_state_root() -> Result<(), Box<dyn std::error::Error>> {
        use std::os::unix::fs::symlink;

        let root = tempdir()?;
        let outside = root.path().join("outside-state");
        let linked_state = root.path().join("linked-state");
        fs::create_dir(&outside)?;
        symlink(&outside, &linked_state)?;
        assert!(CustomProviderConnectionStore::new(&linked_state, false).is_err());
        assert!(!outside.join("provider-connections").exists());
        Ok(())
    }

    /// 功能：验证模型目录严格解析、去重排序以及冻结的 ID 和数量边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn parses_discovered_models_strictly() -> Result<(), Box<dyn std::error::Error>> {
        let models = parse_discovered_model_ids(
            br#"{
                "object":"list",
                "request_id":"request-redacted",
                "data":[
                    {"id":"model-b","object":"model","created":1,"owned_by":"provider"},
                    {"id":"model-a","metadata":{"tier":"standard"}},
                    {"id":"model-b"}
                ]
            }"#,
        )?;
        assert_eq!(models, vec!["model-a".to_owned(), "model-b".to_owned()]);

        for invalid in [
            br#"{"data":[]}"#.as_slice(),
            br#"{}"#.as_slice(),
            br#"{"data":{}}"#.as_slice(),
            br#"{"data":[{}]}"#.as_slice(),
            br#"{"data":[{"id":1}]}"#.as_slice(),
            br#"{"data":[{"id":"first","id":"second"}]}"#.as_slice(),
            br#"{"data":[],"data":[{"id":"model-a"}]}"#.as_slice(),
            br#"{"data":[{"id":"model-a","metadata":{"tier":1,"tier":2}}]}"#.as_slice(),
            br#"{"data":[{"id":""}]}"#.as_slice(),
            br#"{"data":[{"id":"bad\u0000id"}]}"#.as_slice(),
        ] {
            let error = parse_discovered_model_ids(invalid)
                .expect_err("strict model response must be rejected");
            assert_eq!(error.code, crate::error::ErrorCode::ProviderError);
        }

        let oversized_id = serde_json::to_vec(&json!({
            "data":[{"id":"x".repeat(257)}]
        }))?;
        let oversized_error = parse_discovered_model_ids(&oversized_id)
            .expect_err("257-byte model ID must be rejected");
        assert_eq!(oversized_error.code, crate::error::ErrorCode::ProviderError);

        let too_many = serde_json::to_vec(&json!({
            "data":(0..513)
                .map(|index| json!({"id":format!("model-{index:03}")}))
                .collect::<Vec<_>>()
        }))?;
        let limit_error =
            parse_discovered_model_ids(&too_many).expect_err("513 unique models must be rejected");
        assert_eq!(
            limit_error.code,
            crate::error::ErrorCode::OutputLimitExceeded
        );
        Ok(())
    }

    /// 功能：验证连接输入允许空与 512 项模型，同时空模型连接不会进入可执行快照。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn accepts_model_input_bounds_and_excludes_empty_runtime_connection()
    -> Result<(), Box<dyn std::error::Error>> {
        let root = tempdir()?;
        let state = root.path().join("state");
        let workspace = root.path().join("workspace");
        fs::create_dir_all(&workspace)?;
        let service = service(&state, &workspace)?;

        let mut empty = input("empty-models", true);
        empty.model_ids.clear();
        let _ = service.create(empty)?;
        service.set_credential("empty-models", "responses", "empty-model-secret")?;
        let snapshot = service.runtime_snapshot()?;
        assert!(!snapshot.contains_provider("empty-models"));
        assert!(snapshot.models(None).is_empty());
        assert!(snapshot.build_providers()?.is_empty());

        let mut maximum = input("maximum-models", true);
        maximum.model_ids = (0..512).map(|index| format!("model-{index:03}")).collect();
        let maximum_created = service.create(maximum.clone())?;
        assert_eq!(maximum_created.model_ids.len(), 512);

        maximum.provider_id = "too-many-models".to_owned();
        maximum.model_ids.push("model-512".to_owned());
        let error = service
            .create(maximum)
            .expect_err("513 input models must be rejected");
        assert_eq!(error.code, crate::error::ErrorCode::InvalidParams);
        assert_eq!(error.details["field"], "modelIds");
        Ok(())
    }

    /// 功能：验证模型发现的 CAS 仅替换模型字段，且 stale revision 不修改已发布连接。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn discovered_models_replace_only_models_with_cas() -> Result<(), Box<dyn std::error::Error>> {
        let root = tempdir()?;
        let state = root.path().join("state");
        let workspace = root.path().join("workspace");
        fs::create_dir_all(&workspace)?;
        let service = service(&state, &workspace)?;
        let created = service.create(input("discovery-cas", true))?;
        let before = service
            .connections
            .read_for_model_discovery(&created.connection_id, created.revision)?;
        thread::sleep(Duration::from_millis(2));

        let discovered = vec!["discovered-a".to_owned(), "discovered-z".to_owned()];
        let updated = service.connections.replace_discovered_models(
            &created.connection_id,
            created.revision,
            discovered.clone(),
        )?;
        let mut expected = before.clone();
        expected.revision += 1;
        expected.model_ids = discovered;
        expected.updated_at.clone_from(&updated.updated_at);
        assert_eq!(updated, expected);
        assert!(updated.updated_at >= before.updated_at);

        let conflict = service
            .connections
            .replace_discovered_models(
                &created.connection_id,
                created.revision,
                vec!["must-not-persist".to_owned()],
            )
            .expect_err("stale discovery CAS must fail");
        assert_eq!(conflict.code, crate::error::ErrorCode::Conflict);
        let persisted = service
            .connections
            .read_for_model_discovery(&created.connection_id, updated.revision)?;
        assert_eq!(persisted, updated);
        Ok(())
    }

    /// 功能：验证模型发现发送固定 GET 与 Bearer header，并规范化本机 mock 响应。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn fetches_models_with_bearer_authentication() -> Result<(), Box<dyn std::error::Error>> {
        let listener = TcpListener::bind(("127.0.0.1", 0))?;
        let address = listener.local_addr()?;
        let (request_sender, request_receiver) = mpsc::channel();
        let server = thread::spawn(move || -> Result<(), std::io::Error> {
            let (mut stream, _) = listener.accept()?;
            stream.set_read_timeout(Some(Duration::from_secs(2)))?;
            let mut request = Vec::new();
            let mut buffer = [0_u8; 1024];
            while !request.windows(4).any(|window| window == b"\r\n\r\n") {
                let read = stream.read(&mut buffer)?;
                if read == 0 {
                    break;
                }
                request.extend_from_slice(&buffer[..read]);
            }
            let _ = request_sender.send(String::from_utf8_lossy(&request).into_owned());
            let body = br#"{"data":[{"id":"model-z"},{"id":"model-a"},{"id":"model-z"}]}"#;
            let response = format!(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
                body.len()
            );
            stream.write_all(response.as_bytes())?;
            stream.write_all(body)?;
            stream.flush()?;
            Ok(())
        });

        let endpoint = Url::parse(&format!("http://{address}/models"))?;
        let models = fetch_discovered_model_ids(
            &build_model_discovery_client()?,
            endpoint,
            "model-discovery-canary".to_owned(),
        )
        .await?;
        server
            .join()
            .map_err(|_| std::io::Error::other("model mock server panicked"))??;
        let request = request_receiver.recv_timeout(Duration::from_secs(2))?;
        assert!(request.starts_with("GET /models HTTP/1.1\r\n"));
        assert!(
            request
                .to_ascii_lowercase()
                .contains("authorization: bearer model-discovery-canary\r\n")
        );
        assert_eq!(models, vec!["model-a".to_owned(), "model-z".to_owned()]);
        Ok(())
    }

    /// 功能：验证 RPC input 使用 strict camelCase 并拒绝未知字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn rejects_unknown_rpc_connection_fields() {
        let value = json!({
            "connection": {
                "displayName":"Test",
                "providerId":"test",
                "apiFamily":"openai-completions",
                "baseUrl":"https://example.test/v1",
                "modelIds":["model"],
                "logoAssetId":null,
                "enabled":true,
                "credential":"must-not-be-accepted"
            }
        });
        assert!(serde_json::from_value::<ProviderConnectionsCreateParams>(value).is_err());
        assert!(
            serde_json::from_value::<ProviderConnectionsDiscoverModelsParams>(json!({
                "connectionId":"connection-test",
                "expectedRevision":1,
                "credential":"must-not-be-accepted"
            }))
            .is_err()
        );
    }
}
