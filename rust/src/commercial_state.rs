//! 推广 Provider 本地安装状态与工作区外 CredentialStore。
//!
//! 作者：高宏顺 <18272669457@163.com>

use std::collections::{BTreeMap, BTreeSet};
use std::fs::{File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::Arc;

use async_trait::async_trait;
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
const MAX_CREDENTIAL_STORE_BYTES: usize = 2 * 1024 * 1024;
const MAX_INSTALLATION_BYTES: usize = 512 * 1024;

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
}

impl ProviderCredentialStore {
    /// 功能：创建或打开工作区外 CredentialStore，并建立私有目录。
    ///
    /// 输入：用户状态根和当前 canonical workspace。
    /// 输出：只保存固定状态路径的 store 句柄。
    /// 不变量：credential 根 canonical 后不得位于 workspace 内；Unix 目录权限收窄到 0700。
    /// 失败：目录、canonical 路径、权限或边界验证失败时不读取或写入 secret。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn new(state_root: impl Into<PathBuf>, workspace: &Path) -> Result<Self, AgentError> {
        let root = state_root.into().join("credentials");
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
        Ok(Self { root })
    }

    /// 功能：从调用方内存写入或轮换一个 Provider API key。
    ///
    /// 输入：合法 Provider ID 和只从 stdin 获得的非空 key。
    /// 输出：独占锁内原子发布排序后的敏感 JSON。
    /// 不变量：Unix 文件权限为 0600；值不进入错误、日志或返回对象。
    /// 失败：ID/key、锁、symlink、权限、JSON 或原子写入失败时安全拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn set(&self, provider_id: &str, api_key: &str) -> Result<(), AgentError> {
        validate_provider_id(provider_id)?;
        validate_api_key(api_key)?;
        let _lock = self.acquire_lock()?;
        let mut document = self.read_document_unlocked()?;
        if let Some(existing) = document
            .credentials
            .iter_mut()
            .find(|entry| entry.provider_id == provider_id)
        {
            existing.api_key = api_key.to_owned();
        } else {
            document.credentials.push(StoredCredential {
                provider_id: provider_id.to_owned(),
                api_key: api_key.to_owned(),
            });
        }
        document
            .credentials
            .sort_by(|left, right| left.provider_id.cmp(&right.provider_id));
        self.write_document_unlocked(&document)
    }

    /// 功能：列出拥有本地 stored credential 的 Provider ID。
    ///
    /// 输入：当前 store 固定路径。
    /// 输出：按 ordinal 升序、不含 secret 的 ID 数组。
    /// 不变量：即使序列化也不会返回 apiKey。
    /// 失败：锁、symlink、权限或文档损坏时失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn list(&self) -> Result<Vec<String>, AgentError> {
        let _lock = self.acquire_lock()?;
        Ok(self
            .read_document_unlocked()?
            .credentials
            .into_iter()
            .map(|entry| entry.provider_id)
            .collect())
    }

    /// 功能：移除一个 Provider credential，且不返回旧 secret。
    ///
    /// 输入：合法 Provider ID。
    /// 输出：存在并删除时 true，不存在时 false。
    /// 不变量：更新始终在独占锁内发布；任何输出都不包含 key。
    /// 失败：锁、文件安全、JSON 或写入失败时原状态保留。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn remove(&self, provider_id: &str) -> Result<bool, AgentError> {
        validate_provider_id(provider_id)?;
        let _lock = self.acquire_lock()?;
        let mut document = self.read_document_unlocked()?;
        let before = document.credentials.len();
        document
            .credentials
            .retain(|entry| entry.provider_id != provider_id);
        if document.credentials.len() == before {
            return Ok(false);
        }
        self.write_document_unlocked(&document)?;
        Ok(true)
    }

    /// 功能：在 Provider 请求边界读取指定 API key。
    ///
    /// 输入：固定本地 Provider ID。
    /// 输出：仅由最终 HTTP header 构造局部持有的 key。
    /// 不变量：stored source 读取失败或缺失时不回退环境变量。
    /// 失败：锁、权限、文档或目标缺失时返回脱敏 ProviderUnavailable。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn read_for_request(&self, provider_id: &str) -> Result<String, AgentError> {
        validate_provider_id(provider_id).map_err(|_| provider_unavailable())?;
        let _lock = self.acquire_lock().map_err(|_| provider_unavailable())?;
        self.read_document_unlocked()
            .map_err(|_| provider_unavailable())?
            .credentials
            .into_iter()
            .find(|entry| entry.provider_id == provider_id)
            .map(|entry| entry.api_key)
            .ok_or_else(provider_unavailable)
    }

    /// 功能：只判断指定 stored credential 当前能否安全读取。
    ///
    /// 输入：固定本地 Provider ID。
    /// 输出：存在且整个 store 仍满足安全不变量时 true。
    /// 不变量：不缓存、不回显也不返回 credential 值。
    /// 失败：任何读取失败安全映射为 false。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn contains(&self, provider_id: &str) -> bool {
        self.read_for_request(provider_id).is_ok()
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

    /// 功能：在调用方持有独占锁时读取并验证完整 credential 文档。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn read_document_unlocked(&self) -> Result<CredentialDocument, AgentError> {
        let path = self.root.join("provider-credentials.json");
        if !path.exists() {
            return Ok(CredentialDocument {
                schema_version: SCHEMA_VERSION.to_owned(),
                credentials: Vec::new(),
            });
        }
        let bytes = read_secure_file(&path, MAX_CREDENTIAL_STORE_BYTES, true)?;
        let document: CredentialDocument = parse_strict_json(&bytes, "credential_json")?;
        validate_credential_document(&document)?;
        Ok(document)
    }

    /// 功能：在调用方持有独占锁时以敏感权限原子发布 credential 文档。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn write_document_unlocked(&self, document: &CredentialDocument) -> Result<(), AgentError> {
        validate_credential_document(document)?;
        let mut bytes = serde_json::to_vec(document)
            .map_err(|_| commercial_error("credential_json", "CredentialStore 序列化失败"))?;
        bytes.push(b'\n');
        atomic_write(&self.root.join("provider-credentials.json"), &bytes, true)
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
    if document.schema_version != SCHEMA_VERSION || document.credentials.len() > 128 {
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
        || !value
            .bytes()
            .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'-')
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

/// 功能：创建目录并在 Unix 上把访问权限收窄到当前用户。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn create_private_directory(path: &Path) -> Result<(), AgentError> {
    std::fs::create_dir_all(path)
        .map_err(|_| commercial_error("directory", "商业状态目录创建失败"))?;
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
        Ok(metadata) if metadata.file_type().is_symlink() => Err(commercial_error(
            "file_symlink",
            "商业状态文件不能是符号链接",
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
    let file = options
        .open(path)
        .map_err(|_| commercial_error("file_open", "商业状态 lock 打开失败"))?;
    if !file
        .metadata()
        .map_err(|_| commercial_error("file_metadata", "商业状态 lock 元数据读取失败"))?
        .is_file()
    {
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
    let mut file = options
        .open(path)
        .map_err(|_| commercial_error("file_read", "商业状态文件读取失败"))?;
    let metadata = file
        .metadata()
        .map_err(|_| commercial_error("file_metadata", "商业状态文件元数据读取失败"))?;
    if !metadata.is_file() || metadata.len() == 0 || metadata.len() > maximum as u64 {
        return Err(commercial_error("file_shape", "商业状态文件形状无效"));
    }
    #[cfg(unix)]
    if sensitive {
        use std::os::unix::fs::PermissionsExt as _;
        if metadata.permissions().mode() & 0o077 != 0 {
            return Err(commercial_error(
                "file_permissions",
                "CredentialStore 文件权限过宽",
            ));
        }
    }
    let mut bytes = Vec::with_capacity(metadata.len() as usize);
    file.read_to_end(&mut bytes)
        .map_err(|_| commercial_error("file_read", "商业状态文件读取失败"))?;
    Ok(bytes)
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
        assert_eq!(store.list()?, ["relay-example-relay"]);
        store.set("relay-example-relay", "second-local-test-key")?;
        assert_eq!(
            store.read_for_request("relay-example-relay")?,
            "second-local-test-key"
        );
        assert!(store.remove("relay-example-relay")?);
        assert!(store.list()?.is_empty());
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
        let store = ProviderCredentialStore::new(&state, &workspace)?;
        let outside = directory.path().join("outside.json");
        fs::write(&outside, b"{\"schemaVersion\":\"0.1\",\"credentials\":[]}")?;
        symlink(
            &outside,
            state.join("credentials").join("provider-credentials.json"),
        )?;
        assert!(store.list().is_err());
        Ok(())
    }
}
