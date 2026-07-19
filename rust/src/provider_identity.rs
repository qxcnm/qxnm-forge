//! Manifest 驱动的 Provider 身份广告快照与严格 presence 解析。
//!
//! 作者：高宏顺 <18272669457@163.com>

use std::collections::{BTreeMap, BTreeSet};
use std::fs::{File, OpenOptions};
use std::io::{Read, Take};
use std::path::Path;

#[cfg(unix)]
use std::os::unix::fs::OpenOptionsExt;
#[cfg(windows)]
use std::os::windows::fs::OpenOptionsExt;

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value, json};
use sha2::{Digest, Sha256};

use crate::error::{AgentError, ErrorCode};
use crate::protocol::parse_strict_value;

pub const PROVIDER_IDENTITY_CONFIG_ENV: &str = "AGENT_PROVIDER_IDENTITY_CONFORMANCE_CONFIG";

const ADVERTISEMENT_ERROR_MESSAGE: &str =
    "Provider identity advertisement configuration is invalid.";
const EXPECTED_MANIFEST_SHA256: &str =
    "7b420d7b1ff89248be186525cb6da9cb038a29b16dcb4d5339a68ce5f0e615d1";
const EXPECTED_CATALOG_RECORDS_SHA256: &str =
    "348afa7405fa435492ec0514f9a8dc42d0861a5f99bc1d898f75b9a81f611bfa";
const MANIFEST_DIGEST_ALGORITHM: &str = "sha256-canonical-json-excluding-manifest-digest-v1";
const CATALOG_DIGEST_ALGORITHM: &str = "sha256-canonical-json-v1";
const MAX_CONFIG_BYTES: u64 = 1024 * 1024;
const MAX_MANIFEST_BYTES: u64 = 2 * 1024 * 1024;
const MAX_CATALOG_BYTES: u64 = 16 * 1024 * 1024;
#[cfg(windows)]
const FILE_FLAG_OPEN_REPARSE_POINT: u32 = 0x0020_0000;

const CAPABILITY_FEATURES: [&str; 7] = [
    "authentication",
    "text",
    "streaming",
    "tools",
    "reasoning",
    "image_input",
    "image_output",
];

pub(crate) type RouteKey = (String, String);
pub(crate) type AuthKey = (String, String);
pub(crate) type ModelKey = (String, String, String);

#[derive(Debug)]
pub(crate) struct StrictInputError;

#[derive(Debug, Clone)]
pub(crate) struct ManifestAuthProfile {
    pub(crate) kind: String,
    pub(crate) environment: Vec<String>,
}

#[derive(Debug, Clone)]
pub(crate) struct ManifestRoute {
    pub(crate) media: String,
    pub(crate) adapter_id: String,
    pub(crate) auth_profile_ids: Vec<String>,
    pub(crate) header_policy_id: String,
    pub(crate) quirks: Vec<String>,
    pub(crate) template_bindings: Vec<Vec<String>>,
    pub(crate) runtime_endpoint_env: Vec<String>,
}

#[derive(Debug)]
pub(crate) struct ManifestIndex {
    pub(crate) routes: BTreeMap<RouteKey, ManifestRoute>,
    pub(crate) provider_ids: BTreeSet<String>,
    pub(crate) adapter_ids: BTreeSet<String>,
    pub(crate) environment_names: BTreeSet<String>,
    pub(crate) auth_profile_ids: BTreeMap<String, BTreeSet<String>>,
    pub(crate) auth_profiles: BTreeMap<AuthKey, ManifestAuthProfile>,
}

#[derive(Debug, Clone)]
pub(crate) struct CatalogModel {
    pub(crate) media: String,
    pub(crate) provider_id: String,
    pub(crate) model_id: String,
    pub(crate) name: String,
    pub(crate) api_family: String,
    pub(crate) endpoint_strategy: String,
    pub(crate) endpoint_base: Option<String>,
    pub(crate) input: Vec<String>,
    pub(crate) output: Vec<String>,
    pub(crate) reasoning: bool,
    pub(crate) limits: Option<(u64, u64)>,
}

#[derive(Debug)]
struct PresenceSnapshot {
    implemented_adapter_ids: BTreeSet<String>,
    capability_allowances: BTreeMap<RouteKey, BTreeSet<String>>,
    usable_auth_profiles: BTreeSet<AuthKey>,
    configured_environment_names: BTreeSet<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct PresenceConfig {
    schema_version: String,
    implemented_adapter_ids: Vec<String>,
    capability_allowances: Vec<CapabilityAllowance>,
    usable_auth_profiles: Vec<UsableAuthProfile>,
    configured_environment_names: Vec<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct CapabilityAllowance {
    provider_id: String,
    api_family: String,
    features: Vec<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct UsableAuthProfile {
    provider_id: String,
    auth_profile_id: String,
}

/// 一个公开 model descriptor 的能力投影。
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AdvertisedModelCapabilities {
    input: Vec<String>,
    output: Vec<String>,
    streaming: bool,
    tools: bool,
    reasoning: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    context_tokens: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    max_output_tokens: Option<u64>,
}

/// 一个保留 Provider、model 与 API family 三元身份的公开 descriptor。
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AdvertisedModel {
    pub(crate) provider_id: String,
    pub(crate) model_id: String,
    pub(crate) display_name: String,
    pub(crate) api_family: String,
    pub(crate) capabilities: AdvertisedModelCapabilities,
}

/// 启动期构造、只用于协议广告且不能执行 Provider 请求的快照。
#[derive(Debug, Clone)]
pub struct ProviderIdentityAdvertisement {
    models: Vec<AdvertisedModel>,
}

impl ProviderIdentityAdvertisement {
    /// 功能：最早检查 conformance-only presence 环境并按需构造冻结广告快照。
    ///
    /// 输入：daemon 已明确判定的 conformance 模式；presence 路径只来自固定环境名。
    /// 输出：环境不存在时为 `None`；存在且严格有效时为不可执行广告快照。
    /// 不变量：生产模式仅凭 presence 环境出现即拒绝；不读取 credential/canary、Provider
    /// endpoint、DNS、metadata 或 OAuth，不构造 HTTP adapter，shared 路径不可由环境覆盖。
    /// 失败：生产注入、路径、文件、JSON、digest、引用或 presence 无效时返回统一脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn from_environment(conformance: bool) -> Result<Option<Self>, AgentError> {
        let Some(config_path) = std::env::var_os(PROVIDER_IDENTITY_CONFIG_ENV) else {
            return Ok(None);
        };
        if !conformance {
            return Err(advertisement_error());
        }
        load_advertisement(Path::new(&config_path))
            .map(Some)
            .map_err(|_| advertisement_error())
    }

    /// 功能：生成 `initialize.capabilities.providers` 的排序模型去重并集。
    ///
    /// 输入：当前启动期 canonical descriptor 快照。
    /// 输出：固定含 faux，随后按 Provider ID 排序且各模型 ID 排序去重的公共 DTO。
    /// 不变量：所有 live 项只从同一个 `models/list` 快照派生，不暴露 family 内部配置。
    /// 失败：本方法不执行 I/O 且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn provider_capabilities(&self) -> Vec<Value> {
        let mut grouped = BTreeMap::<String, BTreeSet<String>>::new();
        grouped
            .entry("faux".to_owned())
            .or_default()
            .insert("faux-v1".to_owned());
        for model in &self.models {
            grouped
                .entry(model.provider_id.clone())
                .or_default()
                .insert(model.model_id.clone());
        }
        grouped
            .into_iter()
            .map(|(provider_id, model_ids)| {
                json!({"id":provider_id,"models":model_ids.into_iter().collect::<Vec<_>>()})
            })
            .collect()
    }

    /// 功能：按可选 Provider ID 返回 route-qualified `models/list` 稳定快照。
    ///
    /// 输入：缺省表示全量；字符串过滤只比较公共 Provider ID。
    /// 输出：全新 descriptor 列表；全量固定包含 faux，未知 Provider 返回空数组。
    /// 不变量：排序键始终为 `(providerId, modelId, apiFamily)`，跨 family 重名不折叠。
    /// 失败：本方法不执行 I/O 且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn models(&self, provider_id: Option<&str>) -> Vec<AdvertisedModel> {
        let mut models = Vec::new();
        if provider_id.is_none() || provider_id == Some("faux") {
            models.push(faux_model());
        }
        models.extend(
            self.models
                .iter()
                .filter(|model| provider_id.is_none_or(|value| model.provider_id == value))
                .cloned(),
        );
        models.sort_by_key(model_key);
        models
    }

    /// 功能：判断一次 run 选择命中了几个已广告的 family 路由。
    ///
    /// 输入：客户端 Provider/model ID 和可选显式 API family。
    /// 输出：精确 family 时为 0/1；省略 family 时可大于 1，用于拒绝歧义选择。
    /// 不变量：只读取 identity-only 快照，不将广告转换为可执行 adapter。
    /// 失败：本方法不执行 I/O 且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn matching_route_count(
        &self,
        provider_id: &str,
        model_id: &str,
        api_family: Option<&str>,
    ) -> usize {
        self.models
            .iter()
            .filter(|model| {
                model.provider_id == provider_id
                    && model.model_id == model_id
                    && api_family.is_none_or(|family| model.api_family == family)
            })
            .count()
    }
}

/// 功能：构造统一、不含路径、字段名、digest 或实例值的启动错误。
///
/// 输出：稳定 `InvalidParams/invalid_params` 错误。
/// 不变量：错误文本不泄漏 presence、credential 或 shared snapshot 细节。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn advertisement_error() -> AgentError {
    AgentError::new(ErrorCode::InvalidParams, ADVERTISEMENT_ERROR_MESSAGE)
}

/// 功能：从固定 shared snapshot 与一个临时 presence 文件构建广告。
///
/// 输入：仅 conformance 分支传入的配置路径。
/// 输出：严格验证、稳定排序的 canonical live descriptors，不含 faux。
/// 不变量：配置先于 shared snapshot 读取；路径和内容不进入失败文本。
/// 失败：任一文件、JSON、digest、引用或投影不变量无效时返回内部脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn load_advertisement(
    config_path: &Path,
) -> Result<ProviderIdentityAdvertisement, StrictInputError> {
    let config = load_json_object(config_path, MAX_CONFIG_BYTES)?;
    let shared_root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../SPEC");
    let manifest = load_json_object(&shared_root.join("providers.v1.json"), MAX_MANIFEST_BYTES)?;
    let catalog = load_json_object(&shared_root.join("models.v1.json"), MAX_CATALOG_BYTES)?;
    build_advertisement(&manifest, &catalog, config)
}

/// 功能：加载并严格验证代码冻结的 manifest/catalog，返回 route 与 catalog 内部索引。
///
/// 输出：35 Provider、45 route 与 1,076 模型的闭合不可变索引。
/// 不变量：文件位置由编译期工程根固定，no-follow、有界读取且 digest 必须与代码常量一致。
/// 失败：文件、UTF-8、strict JSON、digest、envelope、引用或 census 漂移时返回内部脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) fn load_frozen_indexes()
-> Result<(ManifestIndex, BTreeMap<RouteKey, Vec<CatalogModel>>), StrictInputError> {
    let shared_root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../SPEC");
    let manifest = load_json_object(&shared_root.join("providers.v1.json"), MAX_MANIFEST_BYTES)?;
    let catalog = load_json_object(&shared_root.join("models.v1.json"), MAX_CATALOG_BYTES)?;
    verify_frozen_digests(&manifest, &catalog)?;
    validate_catalog_envelope(&catalog)?;
    let manifest = manifest_index(&manifest)?;
    let catalog = catalog_index(&catalog, &manifest)?;
    Ok((manifest, catalog))
}

/// 功能：不跟随最终 symlink 地有界读取一个非空 regular file。
///
/// 输入：代码固定或 conformance 临时路径，以及正的 bytes 硬上限。
/// 输出：完整文件 bytes，长度不超过上限。
/// 不变量：Unix 使用 `O_NOFOLLOW`，Windows 打开 reparse point 本身并拒绝 symlink；全部
/// 平台在 descriptor 上复核 regular-file 和大小，最多额外读取一个 byte 发现增长；诊断不含路径或内容。
/// 失败：打开、类型、大小、竞态增长或读取失败时返回内部脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn read_bounded_regular_file(path: &Path, max_bytes: u64) -> Result<Vec<u8>, StrictInputError> {
    if max_bytes == 0 {
        return Err(StrictInputError);
    }
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(unix)]
    options.custom_flags(libc::O_CLOEXEC | libc::O_NOFOLLOW);
    #[cfg(windows)]
    options.custom_flags(FILE_FLAG_OPEN_REPARSE_POINT);
    #[cfg(not(any(unix, windows)))]
    if std::fs::symlink_metadata(path)
        .map_err(|_| StrictInputError)?
        .file_type()
        .is_symlink()
    {
        return Err(StrictInputError);
    }
    let file = options.open(path).map_err(|_| StrictInputError)?;
    let metadata = file.metadata().map_err(|_| StrictInputError)?;
    if metadata.file_type().is_symlink()
        || !metadata.is_file()
        || metadata.len() == 0
        || metadata.len() > max_bytes
    {
        return Err(StrictInputError);
    }
    read_limited(file.take(max_bytes.saturating_add(1)), max_bytes)
}

/// 功能：从已打开 regular-file descriptor 读取至 EOF 并实施增长硬上限。
///
/// 输入：最多暴露 `max_bytes + 1` 的 reader 和原始硬上限。
/// 输出：非空且不超过上限的完整 bytes。
/// 不变量：只读已打开 descriptor，不重新解析路径。
/// 失败：读取失败、空文件或观察到超限 byte 时返回内部脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn read_limited(mut reader: Take<File>, max_bytes: u64) -> Result<Vec<u8>, StrictInputError> {
    let mut bytes = Vec::new();
    reader
        .read_to_end(&mut bytes)
        .map_err(|_| StrictInputError)?;
    if bytes.is_empty() || u64::try_from(bytes.len()).map_err(|_| StrictInputError)? > max_bytes {
        return Err(StrictInputError);
    }
    Ok(bytes)
}

/// 功能：组合有界 regular-file 读取与严格 JSON object 解码。
///
/// 输入：文件路径和 bytes 上限。
/// 输出：拒绝重复键、非有限数、尾随值且顶层为 object 的 JSON。
/// 不变量：原始内容和解析诊断不越过内部边界。
/// 失败：文件或严格 JSON 不变量失败时返回统一内部错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) fn load_json_object(path: &Path, max_bytes: u64) -> Result<Value, StrictInputError> {
    let bytes = read_bounded_regular_file(path, max_bytes)?;
    let text = std::str::from_utf8(&bytes).map_err(|_| StrictInputError)?;
    let value = parse_strict_value(text).map_err(|_| StrictInputError)?;
    require_object(&value)?;
    Ok(value)
}

/// 功能：把动态 JSON 值收窄为 object。
///
/// 输入：任意严格解析值。
/// 输出：原 object 引用。
/// 失败：类型不符时返回内部脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn require_object(value: &Value) -> Result<&Map<String, Value>, StrictInputError> {
    value.as_object().ok_or(StrictInputError)
}

/// 功能：把动态 JSON 值收窄为 array。
///
/// 输入：任意严格解析值。
/// 输出：原 array 引用。
/// 失败：类型不符时返回内部脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn require_array(value: &Value) -> Result<&Vec<Value>, StrictInputError> {
    value.as_array().ok_or(StrictInputError)
}

/// 功能：把动态 JSON 值收窄为非空字符串。
///
/// 输入：任意严格解析值。
/// 输出：原字符串切片。
/// 失败：类型不符或为空时返回内部脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn require_string(value: &Value) -> Result<&str, StrictInputError> {
    value
        .as_str()
        .filter(|text| !text.is_empty())
        .ok_or(StrictInputError)
}

/// 功能：按字段名从 object 取得必需值。
///
/// 输入：已验证 object 和代码固定字段名。
/// 输出：对应值引用。
/// 失败：字段缺失时返回内部脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn required<'a>(object: &'a Map<String, Value>, key: &str) -> Result<&'a Value, StrictInputError> {
    object.get(key).ok_or(StrictInputError)
}

/// 功能：验证 object 的字段集合与固定 allowlist 完全相等。
///
/// 输入：严格 object 与代码固定字段名数组。
/// 输出：没有缺失或未知字段时成功。
/// 不变量：失败不回显攻击者控制字段名。
/// 失败：字段集合不相等时返回内部脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn require_exact_keys(
    object: &Map<String, Value>,
    expected: &[&str],
) -> Result<(), StrictInputError> {
    if object.len() != expected.len() || expected.iter().any(|key| !object.contains_key(*key)) {
        return Err(StrictInputError);
    }
    Ok(())
}

/// 功能：验证 JSON array 只含保持顺序的唯一非空字符串。
///
/// 输入：任意动态值。
/// 输出：字符串副本数组。
/// 失败：类型、空字符串或重复项无效时返回内部脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn require_unique_strings(value: &Value) -> Result<Vec<String>, StrictInputError> {
    let mut seen = BTreeSet::new();
    let mut values = Vec::new();
    for item in require_array(value)? {
        let item = require_string(item)?.to_owned();
        if !seen.insert(item.clone()) {
            return Err(StrictInputError);
        }
        values.push(item);
    }
    Ok(values)
}

/// 功能：计算规范定义的排序键、紧凑 UTF-8 canonical JSON SHA-256。
///
/// 输入：严格标准 JSON 值。
/// 输出：小写十六进制 SHA-256。
/// 不变量：当前 serde_json 未启用 preserve_order，object map 以稳定字典序编码。
/// 失败：序列化失败时返回内部脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn canonical_sha256(value: &Value) -> Result<String, StrictInputError> {
    let canonical = serde_json::to_vec(value).map_err(|_| StrictInputError)?;
    Ok(format!("{:x}", Sha256::digest(canonical)))
}

/// 功能：验证 shared manifest 与 catalog records 精确绑定代码冻结 digest。
///
/// 输入：两个严格 JSON object。
/// 输出：声明与重算 digest 都匹配时成功。
/// 不变量：manifest 全树和 catalog 模型行的任何修改都要求显式代码更新。
/// 失败：算法、声明、结构或重算 digest 漂移时返回内部脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn verify_frozen_digests(manifest: &Value, catalog: &Value) -> Result<(), StrictInputError> {
    let manifest_object = require_object(manifest)?;
    if required(manifest_object, "manifestDigestAlgorithm")?.as_str()
        != Some(MANIFEST_DIGEST_ALGORITHM)
        || required(manifest_object, "manifestSha256")?.as_str() != Some(EXPECTED_MANIFEST_SHA256)
    {
        return Err(StrictInputError);
    }
    let mut digest_input = manifest.clone();
    let digest_object = digest_input.as_object_mut().ok_or(StrictInputError)?;
    digest_object.remove("manifestDigestAlgorithm");
    digest_object.remove("manifestSha256");
    if canonical_sha256(&digest_input)? != EXPECTED_MANIFEST_SHA256 {
        return Err(StrictInputError);
    }

    let catalog_object = require_object(catalog)?;
    let source = require_object(required(catalog_object, "source")?)?;
    if required(catalog_object, "schemaVersion")?.as_str() != Some("1.0")
        || required(catalog_object, "catalogId")?.as_str() != Some("pi-v1-frozen")
        || required(source, "recordsDigestAlgorithm")?.as_str() != Some(CATALOG_DIGEST_ALGORITHM)
        || required(source, "recordsSha256")?.as_str() != Some(EXPECTED_CATALOG_RECORDS_SHA256)
        || canonical_sha256(required(catalog_object, "models")?)? != EXPECTED_CATALOG_RECORDS_SHA256
    {
        return Err(StrictInputError);
    }
    Ok(())
}

/// 功能：验证 manifest/catalog digest 之外的 catalog 顶层与来源字段闭合。
///
/// 输入：冻结 catalog object。
/// 输出：所有非 records-digest 字段没有未知项且固定来源证据一致时成功。
/// 不变量：records 自身由 digest 锁定；这里补齐顶层/source 未参与 records digest 的闭合。
/// 失败：未知、缺失或固定 census/source 值漂移时返回内部脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_catalog_envelope(catalog: &Value) -> Result<(), StrictInputError> {
    let object = require_object(catalog)?;
    require_exact_keys(
        object,
        &[
            "$schema",
            "schemaVersion",
            "catalogId",
            "project",
            "author",
            "source",
            "models",
        ],
    )?;
    if required(object, "$schema")?.as_str() != Some("./schemas/model-catalog.schema.json")
        || required(object, "schemaVersion")?.as_str() != Some("1.0")
        || required(object, "catalogId")?.as_str() != Some("pi-v1-frozen")
        || required(object, "project")?.as_str() != Some("qxnm-forge")
    {
        return Err(StrictInputError);
    }
    let author = require_object(required(object, "author")?)?;
    require_exact_keys(author, &["name", "email"])?;
    if required(author, "name")?.as_str() != Some("高宏顺")
        || required(author, "email")?.as_str() != Some("18272669457@163.com")
    {
        return Err(StrictInputError);
    }
    let source = require_object(required(object, "source")?)?;
    require_exact_keys(
        source,
        &[
            "project",
            "commit",
            "license",
            "textEntrypoint",
            "imageEntrypoint",
            "extraction",
            "sourceDigestAlgorithm",
            "textEntrypointSha256",
            "textProviderSourcesSha256",
            "imageEntrypointSha256",
            "recordsDigestAlgorithm",
            "recordsSha256",
            "observedCounts",
            "textModelsByProvider",
            "imageModelsByProvider",
        ],
    )?;
    if required(source, "project")?.as_str() != Some("PI")
        || required(source, "commit")?.as_str() != Some("3f9aa5d10b35223abf6146f960ff5cb5c68053ee")
        || required(source, "license")?.as_str() != Some("MIT")
        || required(source, "textEntrypoint")?.as_str()
            != Some("packages/ai/src/models.generated.ts")
        || required(source, "imageEntrypoint")?.as_str()
            != Some("packages/ai/src/image-models.generated.ts")
        || required(source, "extraction")?.as_str() != Some("native-esm-object-enumeration-v1")
        || required(source, "sourceDigestAlgorithm")?.as_str()
            != Some("sha256-path-nul-bytes-nul-v1")
        || required(source, "textEntrypointSha256")?.as_str()
            != Some("ca7059ec42b51e1ca9aacc92a2be24e552c5025a849a62f8b7f2327d80dea46b")
        || required(source, "textProviderSourcesSha256")?.as_str()
            != Some("c4cd2b4fb05478c737c5d1dfb77a9b693593388e052182cb79d81d414ac5ec35")
        || required(source, "imageEntrypointSha256")?.as_str()
            != Some("255db32f84f1a94f579f061506ced014ccf33d0b719b4a855eace464cb395a00")
        || required(source, "recordsDigestAlgorithm")?.as_str() != Some(CATALOG_DIGEST_ALGORITHM)
        || required(source, "recordsSha256")?.as_str() != Some(EXPECTED_CATALOG_RECORDS_SHA256)
    {
        return Err(StrictInputError);
    }
    let counts = require_object(required(source, "observedCounts")?)?;
    require_exact_keys(
        counts,
        &["providers", "textModels", "imageProviders", "imageModels"],
    )?;
    if required(counts, "providers")?.as_u64() != Some(35)
        || required(counts, "textModels")?.as_u64() != Some(1041)
        || required(counts, "imageProviders")?.as_u64() != Some(1)
        || required(counts, "imageModels")?.as_u64() != Some(35)
    {
        return Err(StrictInputError);
    }
    let text_counts = require_object(required(source, "textModelsByProvider")?)?;
    let image_counts = require_object(required(source, "imageModelsByProvider")?)?;
    if text_counts.len() != 35
        || text_counts.values().any(|count| count.as_u64().is_none())
        || image_counts.len() != 1
        || image_counts.get("openrouter").and_then(Value::as_u64) != Some(35)
    {
        return Err(StrictInputError);
    }
    Ok(())
}

/// 功能：判断字符串是否符合 manifest slug 的 ASCII 闭集。
///
/// 输入：未经信任的字符串。
/// 输出：长度 1..=128、首字符小写字母/数字且其余只含小写字母/数字/连字符时为 true。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn is_slug(value: &str) -> bool {
    let bytes = value.as_bytes();
    (1..=128).contains(&bytes.len())
        && bytes
            .first()
            .is_some_and(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit())
        && bytes
            .iter()
            .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || *byte == b'-')
}

/// 功能：判断字符串是否符合 canonical Provider ID 的 ASCII 闭集。
///
/// 输入：未经信任的字符串。
/// 输出：slug 规则外额外允许点号时为 true。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) fn is_provider_id(value: &str) -> bool {
    let bytes = value.as_bytes();
    (1..=128).contains(&bytes.len())
        && bytes
            .first()
            .is_some_and(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit())
        && bytes.iter().all(|byte| {
            byte.is_ascii_lowercase() || byte.is_ascii_digit() || *byte == b'-' || *byte == b'.'
        })
}

/// 功能：判断字符串是否符合 manifest 环境变量名称闭集。
///
/// 输入：未经信任的字符串。
/// 输出：2..=128 ASCII 字符、首字符大写且其余只含大写/数字/下划线时为 true。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn is_environment_name(value: &str) -> bool {
    let bytes = value.as_bytes();
    (2..=128).contains(&bytes.len())
        && bytes.first().is_some_and(u8::is_ascii_uppercase)
        && bytes
            .iter()
            .all(|byte| byte.is_ascii_uppercase() || byte.is_ascii_digit() || *byte == b'_')
}

/// 功能：验证字符串数组无重复且全部满足一个固定 predicate。
///
/// 输入：字符串切片和无副作用验证函数。
/// 输出：全部有效且唯一时成功。
/// 失败：重复或 predicate 拒绝时返回内部脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_unique_by(
    values: &[String],
    predicate: impl Fn(&str) -> bool,
) -> Result<(), StrictInputError> {
    if values.iter().any(|value| !predicate(value))
        || values.iter().collect::<BTreeSet<_>>().len() != values.len()
    {
        return Err(StrictInputError);
    }
    Ok(())
}

/// 功能：建立 35 Provider、45 route 的唯一且引用闭合 manifest 索引。
///
/// 输入：已通过全树冻结 digest 的 canonical manifest。
/// 输出：route、Provider、adapter、环境名和 auth profile 索引。
/// 不变量：route 身份为 `(providerId, apiFamily)`；adapter/catalog/header/auth/environment
/// 引用均闭合，且 template/runtime endpoint 名称属于所属 Provider allowlist。
/// 失败：计数、类型、身份唯一性或任一引用漂移时返回内部脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn manifest_index(manifest: &Value) -> Result<ManifestIndex, StrictInputError> {
    let object = require_object(manifest)?;
    if required(object, "schemaVersion")?.as_str() != Some("0.2") {
        return Err(StrictInputError);
    }

    let adapters = require_array(required(object, "adapters")?)?;
    if adapters.len() != 10 {
        return Err(StrictInputError);
    }
    let mut adapter_families = BTreeMap::<String, String>::new();
    for raw_adapter in adapters {
        let adapter = require_object(raw_adapter)?;
        let adapter_id = require_string(required(adapter, "id")?)?;
        let family = require_string(required(adapter, "apiFamily")?)?;
        if !is_slug(adapter_id)
            || !is_slug(family)
            || adapter_families
                .insert(adapter_id.to_owned(), family.to_owned())
                .is_some()
        {
            return Err(StrictInputError);
        }
    }

    let catalogs = require_array(required(object, "modelCatalogs")?)?;
    if catalogs.len() != 1 {
        return Err(StrictInputError);
    }
    let mut catalog_ids = BTreeSet::new();
    for raw_catalog in catalogs {
        let catalog = require_object(raw_catalog)?;
        let catalog_id = require_string(required(catalog, "id")?)?;
        if !is_slug(catalog_id) || !catalog_ids.insert(catalog_id.to_owned()) {
            return Err(StrictInputError);
        }
    }

    let header_policies = require_array(required(object, "headerPolicies")?)?;
    if header_policies.len() != 3 {
        return Err(StrictInputError);
    }
    let mut header_policy_ids = BTreeSet::new();
    for raw_policy in header_policies {
        let policy = require_object(raw_policy)?;
        let policy_id = require_string(required(policy, "id")?)?;
        if !is_slug(policy_id) || !header_policy_ids.insert(policy_id.to_owned()) {
            return Err(StrictInputError);
        }
    }

    let providers = require_array(required(object, "providers")?)?;
    if providers.len() != 35 {
        return Err(StrictInputError);
    }
    let mut routes = BTreeMap::new();
    let mut provider_ids = BTreeSet::new();
    let mut environment_names = BTreeSet::new();
    let mut auth_profile_ids = BTreeMap::new();
    let mut auth_profiles = BTreeMap::new();
    for raw_provider in providers {
        let provider = require_object(raw_provider)?;
        let provider_id = require_string(required(provider, "id")?)?.to_owned();
        if !is_provider_id(&provider_id) || !provider_ids.insert(provider_id.clone()) {
            return Err(StrictInputError);
        }

        let mut provider_environment = BTreeSet::new();
        for raw_environment in require_array(required(provider, "environment")?)? {
            let environment = require_object(raw_environment)?;
            let name = require_string(required(environment, "name")?)?;
            if !is_environment_name(name) || !provider_environment.insert(name.to_owned()) {
                return Err(StrictInputError);
            }
            environment_names.insert(name.to_owned());
        }

        let mut profile_ids = BTreeSet::new();
        for raw_profile in require_array(required(provider, "authProfiles")?)? {
            let profile = require_object(raw_profile)?;
            let profile_id = require_string(required(profile, "id")?)?;
            let profile_kind = require_string(required(profile, "kind")?)?;
            let profile_environment = require_unique_strings(required(profile, "environment")?)?;
            validate_unique_by(&profile_environment, is_environment_name)?;
            if !is_slug(profile_id)
                || !is_slug(profile_kind)
                || !profile_ids.insert(profile_id.to_owned())
                || !profile_environment
                    .iter()
                    .all(|name| provider_environment.contains(name))
                || auth_profiles
                    .insert(
                        (provider_id.clone(), profile_id.to_owned()),
                        ManifestAuthProfile {
                            kind: profile_kind.to_owned(),
                            environment: profile_environment,
                        },
                    )
                    .is_some()
            {
                return Err(StrictInputError);
            }
        }
        if profile_ids.is_empty() {
            return Err(StrictInputError);
        }
        auth_profile_ids.insert(provider_id.clone(), profile_ids.clone());

        for raw_route in require_array(required(provider, "routes")?)? {
            let route = require_object(raw_route)?;
            let media = require_string(required(route, "media")?)?.to_owned();
            let family = require_string(required(route, "apiFamily")?)?.to_owned();
            let adapter_id = require_string(required(route, "adapterId")?)?.to_owned();
            let catalog_id = require_string(required(route, "modelCatalogId")?)?;
            let header_policy_id = require_string(required(route, "headerPolicyId")?)?;
            let quirks = require_unique_strings(required(route, "quirks")?)?;
            let route_auth = require_unique_strings(required(route, "authProfileIds")?)?;
            validate_unique_by(&route_auth, is_slug)?;
            if !matches!(media.as_str(), "text" | "image")
                || !is_slug(&family)
                || adapter_families.get(&adapter_id) != Some(&family)
                || !catalog_ids.contains(catalog_id)
                || !header_policy_ids.contains(header_policy_id)
                || route_auth.is_empty()
                || !route_auth
                    .iter()
                    .all(|profile| profile_ids.contains(profile))
                || (media == "image") != (family == "openrouter-images")
            {
                return Err(StrictInputError);
            }

            let endpoint = require_object(required(route, "endpoint")?)?;
            let mut template_bindings = Vec::new();
            for raw_binding in require_array(required(endpoint, "templateBindings")?)? {
                let binding = require_object(raw_binding)?;
                let names = require_unique_strings(required(binding, "environment")?)?;
                validate_unique_by(&names, is_environment_name)?;
                if names.is_empty() || !names.iter().all(|name| provider_environment.contains(name))
                {
                    return Err(StrictInputError);
                }
                template_bindings.push(names);
            }
            let runtime_endpoint_env =
                require_unique_strings(required(endpoint, "runtimeEndpointEnv")?)?;
            validate_unique_by(&runtime_endpoint_env, is_environment_name)?;
            if !runtime_endpoint_env
                .iter()
                .all(|name| provider_environment.contains(name))
            {
                return Err(StrictInputError);
            }

            let key = (provider_id.clone(), family);
            if routes
                .insert(
                    key,
                    ManifestRoute {
                        media,
                        adapter_id,
                        auth_profile_ids: route_auth,
                        header_policy_id: header_policy_id.to_owned(),
                        quirks,
                        template_bindings,
                        runtime_endpoint_env,
                    },
                )
                .is_some()
            {
                return Err(StrictInputError);
            }
        }
    }
    if routes.len() != 45 {
        return Err(StrictInputError);
    }
    Ok(ManifestIndex {
        routes,
        provider_ids,
        adapter_ids: adapter_families.into_keys().collect(),
        environment_names,
        auth_profile_ids,
        auth_profiles,
    })
}

/// 功能：从 catalog model object 读取协议投影必需的严格字段。
///
/// 输入：已被 records digest 锁定的单个模型值。
/// 输出：不含 endpoint URL、header、quirk 或来源细节的内部模型行。
/// 不变量：media/family/capability/limits 组合符合文本或图像模型闭集。
/// 失败：字段缺失、类型、唯一性、范围或组合无效时返回内部脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_catalog_model(value: &Value) -> Result<CatalogModel, StrictInputError> {
    let model = require_object(value)?;
    let media = require_string(required(model, "media")?)?.to_owned();
    let provider_id = require_string(required(model, "providerId")?)?.to_owned();
    let model_id = require_string(required(model, "modelId")?)?.to_owned();
    let name = require_string(required(model, "name")?)?.to_owned();
    let api_family = require_string(required(model, "apiFamily")?)?.to_owned();
    if !matches!(media.as_str(), "text" | "image")
        || !is_provider_id(&provider_id)
        || model_id.len() > 512
        || name.len() > 512
        || !is_slug(&api_family)
        || (media == "image") != (api_family == "openrouter-images")
    {
        return Err(StrictInputError);
    }
    let endpoint = require_object(required(model, "endpoint")?)?;
    let endpoint_strategy = require_string(required(endpoint, "strategy")?)?.to_owned();
    if !matches!(
        endpoint_strategy.as_str(),
        "fixed" | "template" | "runtime-required"
    ) {
        return Err(StrictInputError);
    }
    let endpoint_base = if endpoint_strategy == "fixed" {
        Some(require_string(required(endpoint, "baseUrl")?)?.to_owned())
    } else {
        None
    };
    let capabilities = require_object(required(model, "capabilities")?)?;
    let input = require_unique_strings(required(capabilities, "input")?)?;
    if input.is_empty()
        || input
            .iter()
            .any(|item| !matches!(item.as_str(), "text" | "image"))
    {
        return Err(StrictInputError);
    }
    let (output, reasoning, limits) = if media == "text" {
        let reasoning = required(capabilities, "reasoning")?
            .as_bool()
            .ok_or(StrictInputError)?;
        let limits = require_object(required(model, "limits")?)?;
        let context = required(limits, "contextWindow")?
            .as_u64()
            .filter(|value| (1..=2_147_483_647).contains(value))
            .ok_or(StrictInputError)?;
        let maximum = required(limits, "maxOutputTokens")?
            .as_u64()
            .filter(|value| (1..=2_147_483_647).contains(value))
            .ok_or(StrictInputError)?;
        (vec!["text".to_owned()], reasoning, Some((context, maximum)))
    } else {
        let output = require_unique_strings(required(capabilities, "output")?)?;
        if output.is_empty()
            || output
                .iter()
                .any(|item| !matches!(item.as_str(), "text" | "image"))
            || model.contains_key("limits")
        {
            return Err(StrictInputError);
        }
        (output, false, None)
    };
    Ok(CatalogModel {
        media,
        provider_id,
        model_id,
        name,
        api_family,
        endpoint_strategy,
        endpoint_base,
        input,
        output,
        reasoning,
        limits,
    })
}

/// 功能：按 manifest route 索引 1,076 条模型并保留跨 family 重名。
///
/// 输入：已通过 envelope/records digest 的 catalog 与闭合 manifest 索引。
/// 输出：每条 route 下按 modelId 排序的内部 catalog 行。
/// 不变量：唯一身份为 `(providerId, modelId, apiFamily)`；每条 manifest route 非空。
/// 失败：计数、悬空 route、重复三元组、media 不匹配或 OpenRouter 歧义 census 漂移时拒绝。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn catalog_index(
    catalog: &Value,
    manifest: &ManifestIndex,
) -> Result<BTreeMap<RouteKey, Vec<CatalogModel>>, StrictInputError> {
    let catalog_object = require_object(catalog)?;
    let raw_models = require_array(required(catalog_object, "models")?)?;
    if raw_models.len() != 1076 {
        return Err(StrictInputError);
    }
    let mut grouped = BTreeMap::<RouteKey, Vec<CatalogModel>>::new();
    let mut seen = BTreeSet::<ModelKey>::new();
    let mut pair_families = BTreeMap::<(String, String), BTreeSet<String>>::new();
    let mut text_counts = BTreeMap::<String, u64>::new();
    let mut image_counts = BTreeMap::<String, u64>::new();
    for raw_model in raw_models {
        let model = parse_catalog_model(raw_model)?;
        let route_key = (model.provider_id.clone(), model.api_family.clone());
        let model_key = (
            model.provider_id.clone(),
            model.model_id.clone(),
            model.api_family.clone(),
        );
        let route = manifest.routes.get(&route_key).ok_or(StrictInputError)?;
        if route.media != model.media || !seen.insert(model_key) {
            return Err(StrictInputError);
        }
        pair_families
            .entry((model.provider_id.clone(), model.model_id.clone()))
            .or_default()
            .insert(model.api_family.clone());
        let counts = if model.media == "text" {
            &mut text_counts
        } else {
            &mut image_counts
        };
        *counts.entry(model.provider_id.clone()).or_default() += 1;
        grouped.entry(route_key).or_default().push(model);
    }
    if grouped.len() != manifest.routes.len()
        || grouped.keys().any(|key| !manifest.routes.contains_key(key))
    {
        return Err(StrictInputError);
    }
    for models in grouped.values_mut() {
        models.sort_by(|left, right| left.model_id.cmp(&right.model_id));
    }
    let ambiguous = pair_families
        .into_iter()
        .filter(|(_, families)| families.len() > 1)
        .collect::<BTreeMap<_, _>>();
    let expected_families = BTreeSet::from([
        "openai-completions".to_owned(),
        "openrouter-images".to_owned(),
    ]);
    if ambiguous.len() != 2
        || ambiguous.get(&(
            "openrouter".to_owned(),
            "google/gemini-3-pro-image".to_owned(),
        )) != Some(&expected_families)
        || ambiguous.get(&("openrouter".to_owned(), "openrouter/auto".to_owned()))
            != Some(&expected_families)
    {
        return Err(StrictInputError);
    }
    let source = require_object(required(catalog_object, "source")?)?;
    let declared_text = require_object(required(source, "textModelsByProvider")?)?;
    let declared_image = require_object(required(source, "imageModelsByProvider")?)?;
    if text_counts.len() != manifest.provider_ids.len()
        || text_counts
            .keys()
            .any(|id| !manifest.provider_ids.contains(id))
        || declared_text.len() != text_counts.len()
        || text_counts
            .iter()
            .any(|(id, count)| declared_text.get(id).and_then(Value::as_u64) != Some(*count))
        || declared_image.len() != image_counts.len()
        || image_counts
            .iter()
            .any(|(id, count)| declared_image.get(id).and_then(Value::as_u64) != Some(*count))
    {
        return Err(StrictInputError);
    }
    Ok(grouped)
}

/// 功能：严格验证无值 presence 配置及其全部 manifest 引用。
///
/// 输入：严格 JSON config 与 canonical manifest 索引。
/// 输出：只含不可变 presence 身份集合的内部快照。
/// 不变量：只接受 Schema 的五字段，不接受 credential、endpoint 值或扩展字段。
/// 失败：未知字段、非法 pattern、重复项或悬空引用时返回内部脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_presence(
    config: Value,
    manifest: &ManifestIndex,
) -> Result<PresenceSnapshot, StrictInputError> {
    let config: PresenceConfig = serde_json::from_value(config).map_err(|_| StrictInputError)?;
    if config.schema_version != "0.1" {
        return Err(StrictInputError);
    }
    validate_unique_by(&config.implemented_adapter_ids, is_slug)?;
    if !config
        .implemented_adapter_ids
        .iter()
        .all(|adapter| manifest.adapter_ids.contains(adapter))
    {
        return Err(StrictInputError);
    }
    validate_unique_by(&config.configured_environment_names, is_environment_name)?;
    if !config
        .configured_environment_names
        .iter()
        .all(|name| manifest.environment_names.contains(name))
    {
        return Err(StrictInputError);
    }

    let mut allowances = BTreeMap::new();
    for allowance in config.capability_allowances {
        if !is_provider_id(&allowance.provider_id) || !is_slug(&allowance.api_family) {
            return Err(StrictInputError);
        }
        validate_unique_by(&allowance.features, |feature| {
            CAPABILITY_FEATURES.contains(&feature)
        })?;
        let key = (allowance.provider_id, allowance.api_family);
        if !manifest.routes.contains_key(&key)
            || allowances
                .insert(key, allowance.features.into_iter().collect())
                .is_some()
        {
            return Err(StrictInputError);
        }
    }

    let mut usable_auth_profiles = BTreeSet::new();
    for auth in config.usable_auth_profiles {
        if !is_provider_id(&auth.provider_id)
            || !is_slug(&auth.auth_profile_id)
            || !manifest
                .auth_profile_ids
                .get(&auth.provider_id)
                .is_some_and(|profiles| profiles.contains(&auth.auth_profile_id))
            || !usable_auth_profiles.insert((auth.provider_id, auth.auth_profile_id))
        {
            return Err(StrictInputError);
        }
    }
    Ok(PresenceSnapshot {
        implemented_adapter_ids: config.implemented_adapter_ids.into_iter().collect(),
        capability_allowances: allowances,
        usable_auth_profiles,
        configured_environment_names: config.configured_environment_names.into_iter().collect(),
    })
}

/// 功能：计算单条 route 的 adapter/auth/endpoint/capability presence 精确交集。
///
/// 输入：route 身份、manifest route、route catalog 行和已验证 presence。
/// 输出：整条 route 是否可进入广告快照。
/// 不变量：只检查无值身份集合；任一 predicate 缺失即 false，不读取环境或 credential 值。
/// 失败：本方法不执行 I/O；shared 结构已由调用方严格验证。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn route_is_usable(
    key: &RouteKey,
    route: &ManifestRoute,
    route_models: &[CatalogModel],
    presence: &PresenceSnapshot,
) -> bool {
    if !presence.implemented_adapter_ids.contains(&route.adapter_id) {
        return false;
    }
    let Some(features) = presence.capability_allowances.get(key) else {
        return false;
    };
    if !features.contains("authentication") {
        return false;
    }
    if route.media == "text" && !features.contains("text") {
        return false;
    }
    if route.media == "image"
        && (!features.contains("image_output")
            || (!features.contains("text") && !features.contains("image_input")))
    {
        return false;
    }
    if !route.auth_profile_ids.iter().any(|profile| {
        presence
            .usable_auth_profiles
            .contains(&(key.0.clone(), profile.clone()))
    }) {
        return false;
    }
    if route.template_bindings.iter().any(|names| {
        names
            .iter()
            .all(|name| !presence.configured_environment_names.contains(name))
    }) {
        return false;
    }
    let runtime_configured = route
        .runtime_endpoint_env
        .iter()
        .any(|name| presence.configured_environment_names.contains(name));
    if (!route.runtime_endpoint_env.is_empty() && !runtime_configured)
        || (route_models
            .iter()
            .any(|model| model.endpoint_strategy == "runtime-required")
            && (route.runtime_endpoint_env.is_empty() || !runtime_configured))
    {
        return false;
    }
    true
}

/// 功能：把冻结 catalog 行投影为 capability allowance 约束后的公共 descriptor。
///
/// 输入：一个 canonical catalog row 与该 route 获准公开的 feature 集合。
/// 输出：不含 endpoint、adapter、认证或兼容内部字段的新 descriptor。
/// 不变量：输入/输出/推理能力只取 catalog 证据与 allowance 的交集；图像路由不广告
/// streaming、tools 或 reasoning，文本 output 固定为 text。
/// 失败：本方法不执行 I/O；catalog 结构已由调用方严格验证。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) fn normalized_model(
    model: &CatalogModel,
    features: &BTreeSet<String>,
) -> AdvertisedModel {
    let input = model
        .input
        .iter()
        .filter(|media| {
            (media.as_str() == "text" && features.contains("text"))
                || (media.as_str() == "image" && features.contains("image_input"))
        })
        .cloned()
        .collect();
    let (output, streaming, tools, reasoning) = if model.media == "text" {
        (
            vec!["text".to_owned()],
            features.contains("streaming"),
            features.contains("tools"),
            model.reasoning && features.contains("reasoning"),
        )
    } else {
        (
            model
                .output
                .iter()
                .filter(|media| {
                    (media.as_str() == "text" && features.contains("text"))
                        || (media.as_str() == "image" && features.contains("image_output"))
                })
                .cloned()
                .collect(),
            false,
            false,
            false,
        )
    };
    AdvertisedModel {
        provider_id: model.provider_id.clone(),
        model_id: model.model_id.clone(),
        display_name: model.name.clone(),
        api_family: model.api_family.clone(),
        capabilities: AdvertisedModelCapabilities {
            input,
            output,
            streaming,
            tools,
            reasoning,
            context_tokens: model.limits.map(|limits| limits.0),
            max_output_tokens: model.limits.map(|limits| limits.1),
        },
    }
}

/// 功能：从三个严格对象构建一次启动期 canonical model 快照。
///
/// 输入：冻结 manifest/catalog 与无值 presence config。
/// 输出：按三元身份排序且不含 faux 的 identity-only descriptors。
/// 不变量：每条 route 独立判定；空输入或输出投影不得广告；不构造任何 Provider adapter。
/// 失败：digest、envelope、索引、引用、配置或投影不变量失败时返回内部脱敏错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn build_advertisement(
    manifest: &Value,
    catalog: &Value,
    config: Value,
) -> Result<ProviderIdentityAdvertisement, StrictInputError> {
    verify_frozen_digests(manifest, catalog)?;
    validate_catalog_envelope(catalog)?;
    let manifest = manifest_index(manifest)?;
    let catalog = catalog_index(catalog, &manifest)?;
    let presence = validate_presence(config, &manifest)?;
    let mut models = Vec::new();
    for (key, route) in &manifest.routes {
        let route_models = catalog.get(key).ok_or(StrictInputError)?;
        if !route_is_usable(key, route, route_models, &presence) {
            continue;
        }
        let features = presence
            .capability_allowances
            .get(key)
            .ok_or(StrictInputError)?;
        for model in route_models {
            let descriptor = normalized_model(model, features);
            if !descriptor.capabilities.input.is_empty()
                && !descriptor.capabilities.output.is_empty()
            {
                models.push(descriptor);
            }
        }
    }
    models.sort_by_key(model_key);
    Ok(ProviderIdentityAdvertisement { models })
}

/// 功能：构造不属于 live manifest 的固定 faux model descriptor。
///
/// 输出：符合公共 model Schema 的全新对象。
/// 不变量：不读取配置、环境或外部服务。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn faux_model() -> AdvertisedModel {
    AdvertisedModel {
        provider_id: "faux".to_owned(),
        model_id: "faux-v1".to_owned(),
        display_name: "Deterministic Faux v1".to_owned(),
        api_family: "faux".to_owned(),
        capabilities: AdvertisedModelCapabilities {
            input: vec!["text".to_owned()],
            output: vec!["text".to_owned()],
            streaming: true,
            tools: true,
            reasoning: false,
            context_tokens: None,
            max_output_tokens: None,
        },
    }
}

/// 功能：提取公共 descriptor 的稳定三元排序键。
///
/// 输入：一个已归一化 descriptor 引用。
/// 输出：`(providerId, modelId, apiFamily)` 字符串 tuple。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) fn model_key(model: &AdvertisedModel) -> ModelKey {
    (
        model.provider_id.clone(),
        model.model_id.clone(),
        model.api_family.clone(),
    )
}

/// 功能：在没有 identity presence 时返回仅 faux 的公共模型列表。
///
/// 输入：可选 Provider ID filter。
/// 输出：缺省或 faux filter 返回单项，未知 filter 返回空数组。
/// 不变量：普通 live adapters 没有 manifest-derived snapshot 时不得凭空广告模型身份。
/// 失败：本方法不执行 I/O 且不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[must_use]
pub fn default_models(provider_id: Option<&str>) -> Vec<AdvertisedModel> {
    if provider_id.is_none() || provider_id == Some("faux") {
        vec![faux_model()]
    } else {
        Vec::new()
    }
}

#[cfg(test)]
mod tests {
    use std::path::Path;

    use serde_json::{Value, json};

    use super::{
        MAX_CATALOG_BYTES, MAX_MANIFEST_BYTES, build_advertisement, catalog_index,
        load_json_object, manifest_index, validate_catalog_envelope, verify_frozen_digests,
    };

    /// 功能：读取代码固定 shared manifest/catalog，供本模块纯离线测试复用。
    ///
    /// 输出：两个严格 JSON 值。
    /// 失败：测试仓库缺少或损坏 shared snapshot 时使测试失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn shared_documents() -> Result<(Value, Value), Box<dyn std::error::Error>> {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../SPEC");
        let manifest = load_json_object(&root.join("providers.v1.json"), MAX_MANIFEST_BYTES)
            .map_err(|_| "manifest rejected")?;
        let catalog = load_json_object(&root.join("models.v1.json"), MAX_CATALOG_BYTES)
            .map_err(|_| "catalog rejected")?;
        Ok((manifest, catalog))
    }

    /// 功能：验证 Rust canonical JSON digest 与冻结 35/45/1,076 snapshot 完全一致。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn validates_frozen_snapshot_counts_and_digests() -> Result<(), Box<dyn std::error::Error>> {
        let (manifest, catalog) = shared_documents()?;
        verify_frozen_digests(&manifest, &catalog).map_err(|_| "digest rejected")?;
        validate_catalog_envelope(&catalog).map_err(|_| "catalog envelope rejected")?;
        let manifest = manifest_index(&manifest).map_err(|_| "manifest rejected")?;
        let catalog = catalog_index(&catalog, &manifest).map_err(|_| "catalog rejected")?;
        assert_eq!(manifest.provider_ids.len(), 35);
        assert_eq!(manifest.routes.len(), 45);
        assert_eq!(catalog.values().map(Vec::len).sum::<usize>(), 1076);
        Ok(())
    }

    /// 功能：验证空 presence fail-close 且只留下协议层固定 faux，而不是 canonical live 模型。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn empty_presence_advertises_no_live_models() -> Result<(), Box<dyn std::error::Error>> {
        let (manifest, catalog) = shared_documents()?;
        let advertisement = build_advertisement(
            &manifest,
            &catalog,
            json!({
                "schemaVersion":"0.1",
                "implementedAdapterIds":[],
                "capabilityAllowances":[],
                "usableAuthProfiles":[],
                "configuredEnvironmentNames":[]
            }),
        )
        .map_err(|_| "advertisement rejected")?;
        assert!(advertisement.models.is_empty());
        assert_eq!(advertisement.models(None).len(), 1);
        Ok(())
    }

    /// 功能：验证 presence 未知字段严格失败，不被静默保存或忽略。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn rejects_unknown_presence_field() -> Result<(), Box<dyn std::error::Error>> {
        let (manifest, catalog) = shared_documents()?;
        assert!(
            build_advertisement(
                &manifest,
                &catalog,
                json!({
                    "schemaVersion":"0.1",
                    "implementedAdapterIds":[],
                    "capabilityAllowances":[],
                    "usableAuthProfiles":[],
                    "configuredEnvironmentNames":[],
                    "unknown":true
                })
            )
            .is_err()
        );
        Ok(())
    }

    /// 功能：验证配置文件任意层级重复键及 NaN/Infinity 扩展数值均被严格 JSON 入口拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn rejects_duplicate_keys_and_non_finite_numbers() -> Result<(), Box<dyn std::error::Error>> {
        let temporary = tempfile::tempdir()?;
        for (index, raw) in [
            br#"{"schemaVersion":"0.1","schemaVersion":"0.1"}"#.as_slice(),
            br#"{"schemaVersion":NaN}"#.as_slice(),
            br#"{"schemaVersion":Infinity}"#.as_slice(),
        ]
        .into_iter()
        .enumerate()
        {
            let path = temporary.path().join(format!("invalid-{index}.json"));
            std::fs::write(&path, raw)?;
            assert!(super::load_json_object(&path, super::MAX_CONFIG_BYTES).is_err());
        }
        Ok(())
    }

    /// 功能：验证 manifest/model 任意未知字段及 catalog envelope 未知字段都会 fail closed。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn rejects_unknown_shared_snapshot_fields() -> Result<(), Box<dyn std::error::Error>> {
        let (manifest, catalog) = shared_documents()?;
        let mut changed_manifest = manifest.clone();
        changed_manifest["providers"][0]["unknown"] = json!(true);
        assert!(verify_frozen_digests(&changed_manifest, &catalog).is_err());

        let mut changed_models = catalog.clone();
        changed_models["models"][0]["unknown"] = json!(true);
        assert!(verify_frozen_digests(&manifest, &changed_models).is_err());

        let mut changed_envelope = catalog;
        changed_envelope["unknown"] = json!(true);
        assert!(validate_catalog_envelope(&changed_envelope).is_err());
        Ok(())
    }
}
