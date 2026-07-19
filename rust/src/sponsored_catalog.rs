use std::collections::{BTreeMap, BTreeSet};
use std::ffi::OsStr;
use std::fs::{File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::time::Duration;

use base64::{Engine as _, engine::general_purpose::STANDARD as BASE64};
use chrono::{DateTime, TimeDelta, Utc};
use futures_util::StreamExt;
use reqwest::Url;
use ring::rand::SystemRandom;
use ring::signature::{
    ECDSA_P256_SHA256_ASN1, ECDSA_P256_SHA256_ASN1_SIGNING, EcdsaKeyPair, KeyPair,
    UnparsedPublicKey,
};
use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use uuid::Uuid;

use crate::error::{AgentError, ErrorCode};
use crate::protocol::parse_strict_value;

const SCHEMA_VERSION: &str = "0.1";
const SIGNATURE_ALGORITHM: &str = "ecdsa-p256-sha256-asn1";
const MAX_DOCUMENT_BYTES: usize = 256 * 1024;
const MAX_ENTRIES: usize = 64;
const MAX_VALIDITY_DAYS: i64 = 90;
const MAX_CLOCK_SKEW_MINUTES: i64 = 5;

/// 管理员离线保存、客户端可公开安装的推广目录验签公钥。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SponsoredCatalogTrustKey {
    pub schema_version: String,
    pub key_id: String,
    pub algorithm: String,
    pub public_key: String,
}

/// 客户端本地安装的远程目录 URL 与固定信任公钥。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SponsoredCatalogSource {
    pub schema_version: String,
    pub catalog_url: String,
    pub key_id: String,
    pub algorithm: String,
    pub public_key: String,
}

/// 一个必须明确披露返佣关系的远程推广 Provider 条目。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SponsoredProviderEntry {
    pub id: String,
    pub display_name: String,
    pub description: String,
    pub api_family: String,
    pub api_base_url: String,
    pub signup_url: String,
    pub commission_disclosure: String,
    pub priority: u16,
}

/// 已签名 payload 内的推广 Provider 目录。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SponsoredProviderCatalog {
    pub schema_version: String,
    pub catalog_version: u64,
    pub issued_at: String,
    pub expires_at: String,
    pub entries: Vec<SponsoredProviderEntry>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct CatalogSignature {
    algorithm: String,
    key_id: String,
    value: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct CatalogEnvelope {
    schema_version: String,
    payload: String,
    signature: CatalogSignature,
}

/// 推广目录本次加载采用的数据来源。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum SponsoredCatalogOrigin {
    Unconfigured,
    Remote,
    Cache,
}

/// 推广目录加载结果及不含敏感信息的降级诊断。
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SponsoredCatalogLoad {
    pub origin: SponsoredCatalogOrigin,
    pub catalog: Option<SponsoredProviderCatalog>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub warning: Option<String>,
}

#[derive(Debug, Clone)]
struct VerifiedCatalog {
    catalog: SponsoredProviderCatalog,
    payload: Vec<u8>,
    envelope: Vec<u8>,
}

#[derive(Debug, Default)]
struct CatalogCacheState {
    active: Option<VerifiedCatalog>,
    seen_payloads: BTreeMap<u64, BTreeSet<String>>,
}

/// 使用原生 HTTP、密码学和状态目录管理远程推广 Provider 目录。
pub struct SponsoredCatalogService {
    state_root: PathBuf,
    client: reqwest::Client,
}

impl SponsoredCatalogService {
    /// 功能：创建禁止重定向、环境代理且带固定超时的推广目录服务。
    ///
    /// 输入：工作区外的用户状态根。
    /// 输出：尚未进行网络或文件访问的服务。
    /// 不变量：远程请求固定禁用 redirect/proxy，并受 5 秒总超时约束。
    /// 失败：HTTP client 初始化失败时返回脱敏 `InternalError`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn new(state_root: impl Into<PathBuf>) -> Result<Self, AgentError> {
        let client = reqwest::Client::builder()
            .redirect(reqwest::redirect::Policy::none())
            .no_proxy()
            .connect_timeout(Duration::from_secs(3))
            .timeout(Duration::from_secs(5))
            .build()
            .map_err(|_| catalog_error("http_client", "推广目录 HTTP client 初始化失败"))?;
        Ok(Self {
            state_root: state_root.into(),
            client,
        })
    }

    /// 功能：显式安装远程推广目录 URL 和固定验签公钥。
    ///
    /// 输入：HTTPS catalog URL 与管理员公开 trust-key JSON 文件。
    /// 输出：source JSON 已写入状态目录后成功。
    /// 不变量：不复制私钥；远端响应不能更换本地 keyId 或 public key。
    /// 失败：URL、公钥、JSON、目录创建或同步失败时不接受配置。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn configure(&self, catalog_url: &str, trust_key_path: &Path) -> Result<(), AgentError> {
        validate_catalog_url(catalog_url)?;
        let trust_bytes = read_bounded_file(trust_key_path, 16 * 1024, false)?;
        let trust: SponsoredCatalogTrustKey = parse_strict_json(&trust_bytes, "trust_key")?;
        let public_key = validate_trust_key(&trust)?;
        let source = SponsoredCatalogSource {
            schema_version: SCHEMA_VERSION.to_owned(),
            catalog_url: catalog_url.to_owned(),
            key_id: trust.key_id,
            algorithm: SIGNATURE_ALGORITHM.to_owned(),
            public_key: BASE64.encode(public_key),
        };
        let bytes = serde_json::to_vec(&source)
            .map_err(|_| catalog_error("source_json", "推广目录 source 序列化失败"))?;
        atomic_replace_file(&self.source_path(), &bytes, false)
    }

    /// 功能：从签名远端或已验证缓存加载推广目录。
    ///
    /// 输入：`offline=true` 时禁止任何网络访问。
    /// 输出：未配置、远端或缓存来源，以及可选的安全降级诊断。
    /// 不变量：每次读取缓存都重新验签；远端降版本或同版本换内容均不接受。
    /// 失败：已配置但没有任何有效远端/缓存时返回 `ProviderUnavailable`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn load(&self, offline: bool) -> Result<SponsoredCatalogLoad, AgentError> {
        let Some(source) = self.read_source()? else {
            return Ok(SponsoredCatalogLoad {
                origin: SponsoredCatalogOrigin::Unconfigured,
                catalog: None,
                warning: None,
            });
        };
        let public_key = validate_source(&source)?;
        let cached = self.read_cache_state(&source, &public_key)?;
        if offline {
            return cached.active.map_or_else(
                || {
                    Err(catalog_error(
                        "cache_unavailable",
                        "离线模式没有仍在有效期内的推广目录缓存",
                    ))
                },
                |value| {
                    Ok(SponsoredCatalogLoad {
                        origin: SponsoredCatalogOrigin::Cache,
                        catalog: Some(value.catalog),
                        warning: None,
                    })
                },
            );
        }

        let remote = match self.fetch(&source.catalog_url).await {
            Ok(bytes) => verify_envelope(&bytes, &source.key_id, &public_key, Utc::now()),
            Err(error) => Err(error),
        };
        match remote {
            Ok(value) => {
                let remote_version = value.catalog.catalog_version;
                let highest_seen = cached.seen_payloads.keys().next_back().copied();
                if highest_seen.is_some_and(|version| remote_version < version) {
                    return cached.active.as_ref().map_or_else(
                        || Err(catalog_error("rollback", "远端推广目录版本回滚")),
                        |existing| {
                            Ok(cache_fallback(
                                existing,
                                "远端推广目录版本回滚，已继续使用缓存",
                            ))
                        },
                    );
                }
                let remote_digest = lower_hex(&Sha256::digest(&value.payload));
                if cached
                    .seen_payloads
                    .get(&remote_version)
                    .is_some_and(|digests| !digests.contains(&remote_digest))
                {
                    return cached.active.as_ref().map_or_else(
                        || Err(catalog_error("equivocation", "远端推广目录同版本内容冲突")),
                        |existing| {
                            Ok(cache_fallback(
                                existing,
                                "远端推广目录同版本内容冲突，已继续使用缓存",
                            ))
                        },
                    );
                }
                self.persist_cache(&source, &value)?;
                Ok(SponsoredCatalogLoad {
                    origin: SponsoredCatalogOrigin::Remote,
                    catalog: Some(value.catalog),
                    warning: None,
                })
            }
            Err(error) => cached.active.map_or_else(
                || Err(error),
                |value| {
                    Ok(cache_fallback(
                        &value,
                        "远端推广目录不可用，已继续使用有效缓存",
                    ))
                },
            ),
        }
    }

    /// 功能：读取已经显式安装的推广目录 source。
    ///
    /// 输入：服务状态根下的固定 source 路径。
    /// 输出：文件不存在时 None，存在时返回严格 DTO。
    /// 不变量：source 最大 16 KiB 且递归拒绝重复 JSON key。
    /// 失败：读取、UTF-8、JSON 或字段验证失败时拒绝继续。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn read_source(&self) -> Result<Option<SponsoredCatalogSource>, AgentError> {
        let path = self.source_path();
        if !path.exists() {
            return Ok(None);
        }
        let bytes = read_bounded_file(&path, 16 * 1024, false)?;
        parse_strict_json(&bytes, "source").map(Some)
    }

    /// 功能：通过 HTTPS 获取一个有界 signed envelope。
    ///
    /// 输入：已经过 source 验证的 URL。
    /// 输出：最多 256 KiB 的原始响应字节。
    /// 不变量：不跟随 redirect、不使用环境代理、不发送用户数据或凭据。
    /// 失败：非 200、超时、流错误或超限返回可安全回退的结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn fetch(&self, url: &str) -> Result<Vec<u8>, AgentError> {
        let response = self
            .client
            .get(url)
            .send()
            .await
            .map_err(|_| catalog_error("transport", "远程推广目录请求失败"))?;
        if response.status() != reqwest::StatusCode::OK {
            return Err(catalog_error("http_status", "远程推广目录返回非 200 状态"));
        }
        if response
            .content_length()
            .is_some_and(|length| length > MAX_DOCUMENT_BYTES as u64)
        {
            return Err(catalog_error(
                "document_too_large",
                "远程推广目录超过大小上限",
            ));
        }
        let mut bytes = Vec::new();
        let mut stream = response.bytes_stream();
        while let Some(chunk) = stream.next().await {
            let chunk = chunk.map_err(|_| catalog_error("transport", "远程推广目录响应中断"))?;
            if bytes.len().saturating_add(chunk.len()) > MAX_DOCUMENT_BYTES {
                return Err(catalog_error(
                    "document_too_large",
                    "远程推广目录超过大小上限",
                ));
            }
            bytes.extend_from_slice(&chunk);
        }
        Ok(bytes)
    }

    /// 功能：扫描当前 source 隔离缓存并选择最高有效版本。
    ///
    /// 输入：已验证 source 与原始 P-256 公钥。
    /// 输出：仍有效的最高目录，以及含过期版本在内的已见版本/payload 摘要集合。
    /// 不变量：损坏、过期或错误 keyId 缓存不会展示；合法内容寻址文件名仍保留防回滚状态。
    /// 失败：缓存目录枚举发生真实 I/O 错误时返回 `IoError`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn read_cache_state(
        &self,
        source: &SponsoredCatalogSource,
        public_key: &[u8],
    ) -> Result<CatalogCacheState, AgentError> {
        let directory = self.cache_directory(source);
        if !directory.exists() {
            return Ok(CatalogCacheState::default());
        }
        let mut state = CatalogCacheState::default();
        for entry in std::fs::read_dir(directory)? {
            let entry = entry?;
            if !entry.file_type()?.is_file()
                || entry.path().extension().and_then(|value| value.to_str()) != Some("json")
            {
                continue;
            }
            let Some((version, digest)) = parse_cache_filename(&entry.file_name()) else {
                continue;
            };
            state
                .seen_payloads
                .entry(version)
                .or_default()
                .insert(digest);
            let Ok(bytes) = read_bounded_file(&entry.path(), MAX_DOCUMENT_BYTES, false) else {
                continue;
            };
            let Ok(candidate) = verify_envelope(&bytes, &source.key_id, public_key, Utc::now())
            else {
                continue;
            };
            if state.active.as_ref().is_none_or(|current| {
                candidate.catalog.catalog_version > current.catalog.catalog_version
            }) {
                state.active = Some(candidate);
            }
        }
        Ok(state)
    }

    /// 功能：把已验证 envelope 写为不可覆盖的内容寻址缓存。
    ///
    /// 输入：当前 source 和验签成功的目录。
    /// 输出：相同内容已存在或新文件同步完成后成功。
    /// 不变量：文件名只由版本及 SHA-256 产生，永不覆盖既有缓存。
    /// 失败：目录创建、create-new、写入或同步失败返回 I/O 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn persist_cache(
        &self,
        source: &SponsoredCatalogSource,
        value: &VerifiedCatalog,
    ) -> Result<(), AgentError> {
        let directory = self.cache_directory(source);
        std::fs::create_dir_all(&directory)?;
        let digest = lower_hex(&Sha256::digest(&value.payload));
        let path = directory.join(format!(
            "catalog-{}-{digest}.json",
            value.catalog.catalog_version
        ));
        match OpenOptions::new().create_new(true).write(true).open(path) {
            Ok(mut file) => {
                file.write_all(&value.envelope)?;
                file.sync_all()?;
            }
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => {}
            Err(error) => return Err(error.into()),
        }
        Ok(())
    }

    /// 功能：返回状态根下固定的本地 source 配置路径。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn source_path(&self) -> PathBuf {
        self.state_root
            .join("sponsored-provider-catalog")
            .join("source.json")
    }

    /// 功能：按 source URL、公钥和 keyId 派生互不混用的内容缓存目录。
    ///
    /// 输入：已验证 source。
    /// 输出：状态根下不含 URL 明文的 SHA-256 scope 路径。
    /// 不变量：不同信任根或 URL 不共享 rollback 状态。
    /// 失败：本方法不访问文件系统且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn cache_directory(&self, source: &SponsoredCatalogSource) -> PathBuf {
        let mut digest = Sha256::new();
        digest.update(source.catalog_url.as_bytes());
        digest.update([0]);
        digest.update(source.key_id.as_bytes());
        digest.update([0]);
        digest.update(source.public_key.as_bytes());
        self.state_root
            .join("sponsored-provider-catalog")
            .join("cache")
            .join(lower_hex(&digest.finalize()))
    }
}

/// 功能：生成推广目录 ECDSA P-256 私钥和公开 trust-key 文件。
///
/// 输入：合法 keyId、两个尚不存在且应位于可信位置的输出路径。
/// 输出：PKCS#8 私钥与公开 JSON 均同步完成。
/// 不变量：私钥使用 create-new；Unix 权限固定为 0600；私钥内容不输出到终端。
/// 失败：路径已存在、随机源、密钥生成、序列化或 I/O 失败时不覆盖任何既有文件。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub fn generate_catalog_keypair(
    key_id: &str,
    private_key_path: &Path,
    public_key_path: &Path,
) -> Result<(), AgentError> {
    validate_key_id(key_id)?;
    if public_key_path.exists() {
        return Err(catalog_error("path_exists", "公钥输出路径已经存在"));
    }
    let rng = SystemRandom::new();
    let pkcs8 = EcdsaKeyPair::generate_pkcs8(&ECDSA_P256_SHA256_ASN1_SIGNING, &rng)
        .map_err(|_| catalog_error("key_generation", "推广目录签名密钥生成失败"))?;
    let pair = EcdsaKeyPair::from_pkcs8(&ECDSA_P256_SHA256_ASN1_SIGNING, pkcs8.as_ref(), &rng)
        .map_err(|_| catalog_error("key_generation", "推广目录签名密钥初始化失败"))?;
    write_create_new(private_key_path, pkcs8.as_ref(), true)?;
    let trust = SponsoredCatalogTrustKey {
        schema_version: SCHEMA_VERSION.to_owned(),
        key_id: key_id.to_owned(),
        algorithm: SIGNATURE_ALGORITHM.to_owned(),
        public_key: BASE64.encode(pair.public_key().as_ref()),
    };
    let mut public_bytes = serde_json::to_vec_pretty(&trust)
        .map_err(|_| catalog_error("trust_key_json", "推广目录公钥序列化失败"))?;
    public_bytes.push(b'\n');
    if let Err(error) = write_create_new(public_key_path, &public_bytes, false) {
        let _ = std::fs::remove_file(private_key_path);
        return Err(error);
    }
    Ok(())
}

/// 功能：验证 catalog 并用离线私钥生成可远程发布的 signed envelope。
///
/// 输入：catalog JSON、PKCS#8 私钥、匹配的公开 trust-key 和尚不存在的输出路径。
/// 输出：签名覆盖 catalog 原始 UTF-8 字节的 envelope JSON。
/// 不变量：私钥与公钥必须匹配；输出不包含私钥、凭据或主机路径。
/// 失败：任一输入、时间、URL、密钥、签名或 create-new 写入失败时不发布 envelope。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub fn sign_catalog_file(
    catalog_path: &Path,
    private_key_path: &Path,
    trust_key_path: &Path,
    output_path: &Path,
) -> Result<(), AgentError> {
    let catalog_bytes = read_bounded_file(catalog_path, MAX_DOCUMENT_BYTES, false)?;
    let mut catalog: SponsoredProviderCatalog = parse_strict_json(&catalog_bytes, "catalog")?;
    validate_catalog(&mut catalog, Utc::now())?;
    let private_bytes = read_bounded_file(private_key_path, 16 * 1024, true)?;
    let trust_bytes = read_bounded_file(trust_key_path, 16 * 1024, false)?;
    let trust: SponsoredCatalogTrustKey = parse_strict_json(&trust_bytes, "trust_key")?;
    let expected_public = validate_trust_key(&trust)?;
    let rng = SystemRandom::new();
    let pair = EcdsaKeyPair::from_pkcs8(&ECDSA_P256_SHA256_ASN1_SIGNING, &private_bytes, &rng)
        .map_err(|_| catalog_error("private_key", "推广目录私钥无效"))?;
    if pair.public_key().as_ref() != expected_public {
        return Err(catalog_error("key_mismatch", "推广目录私钥与公钥不匹配"));
    }
    let signature = pair
        .sign(&rng, &catalog_bytes)
        .map_err(|_| catalog_error("signature", "推广目录签名失败"))?;
    let envelope = CatalogEnvelope {
        schema_version: SCHEMA_VERSION.to_owned(),
        payload: BASE64.encode(&catalog_bytes),
        signature: CatalogSignature {
            algorithm: SIGNATURE_ALGORITHM.to_owned(),
            key_id: trust.key_id,
            value: BASE64.encode(signature.as_ref()),
        },
    };
    let mut output = serde_json::to_vec_pretty(&envelope)
        .map_err(|_| catalog_error("envelope_json", "推广目录 envelope 序列化失败"))?;
    output.push(b'\n');
    write_create_new(output_path, &output, false)
}

/// 功能：离线验证待上传或已下载 envelope 与公开 trust-key。
///
/// 输入：有界 signed envelope 和公开 trust-key JSON 路径。
/// 输出：验签、时间、URL 和条目规则全部通过的规范化 catalog。
/// 不变量：不访问网络、不读取私钥，不因本方法成功而注册可执行 Provider route。
/// 失败：文件、JSON、Base64、keyId、签名或 catalog 约束不符时安全拒绝。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub fn verify_catalog_file(
    envelope_path: &Path,
    trust_key_path: &Path,
) -> Result<SponsoredProviderCatalog, AgentError> {
    let envelope = read_bounded_file(envelope_path, MAX_DOCUMENT_BYTES, false)?;
    let trust_bytes = read_bounded_file(trust_key_path, 16 * 1024, false)?;
    let trust: SponsoredCatalogTrustKey = parse_strict_json(&trust_bytes, "trust_key")?;
    let public_key = validate_trust_key(&trust)?;
    verify_envelope(&envelope, &trust.key_id, &public_key, Utc::now())
        .map(|verified| verified.catalog)
}

/// 功能：验证 signed envelope、精确 payload 签名和 catalog 业务约束。
///
/// 输入：有界 envelope 字节、预期 keyId、SEC1 公钥和可信当前时间。
/// 输出：按固定顺序规范化 entries 的已验证目录及原始签名字节。
/// 不变量：签名覆盖 Base64 解码后的原始 payload，绝不重序列化后验签。
/// 失败：重复键、未知字段、Base64、签名、时间、URL 或条目约束不符即拒绝。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn verify_envelope(
    envelope_bytes: &[u8],
    expected_key_id: &str,
    public_key: &[u8],
    now: DateTime<Utc>,
) -> Result<VerifiedCatalog, AgentError> {
    if envelope_bytes.len() > MAX_DOCUMENT_BYTES {
        return Err(catalog_error(
            "document_too_large",
            "推广目录 envelope 超过大小上限",
        ));
    }
    let envelope: CatalogEnvelope = parse_strict_json(envelope_bytes, "envelope")?;
    if envelope.schema_version != SCHEMA_VERSION
        || envelope.signature.algorithm != SIGNATURE_ALGORITHM
        || envelope.signature.key_id != expected_key_id
    {
        return Err(catalog_error(
            "signature_metadata",
            "推广目录签名元数据不匹配",
        ));
    }
    let payload = decode_base64(&envelope.payload, MAX_DOCUMENT_BYTES, "payload")?;
    let signature = decode_base64(&envelope.signature.value, 128, "signature")?;
    UnparsedPublicKey::new(&ECDSA_P256_SHA256_ASN1, public_key)
        .verify(&payload, &signature)
        .map_err(|_| catalog_error("signature_invalid", "推广目录签名无效"))?;
    let mut catalog: SponsoredProviderCatalog = parse_strict_json(&payload, "catalog")?;
    validate_catalog(&mut catalog, now)?;
    Ok(VerifiedCatalog {
        catalog,
        payload,
        envelope: envelope_bytes.to_vec(),
    })
}

/// 功能：验证并规范化一个推广目录 payload。
///
/// 输入：严格解码的 catalog 和可信当前 UTC 时间。
/// 输出：成功时 entries 已按 priority 降序、ID 升序排列。
/// 不变量：最长有效期 90 天，ID 唯一，文本不可注入控制字符，URL 仅 HTTPS。
/// 失败：Schema 版本、时钟、条目、URL 或长度约束不符返回 `InvalidRequest`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_catalog(
    catalog: &mut SponsoredProviderCatalog,
    now: DateTime<Utc>,
) -> Result<(), AgentError> {
    if catalog.schema_version != SCHEMA_VERSION
        || catalog.catalog_version == 0
        || catalog.catalog_version > 9_007_199_254_740_991
        || catalog.entries.len() > MAX_ENTRIES
    {
        return Err(catalog_error("catalog_shape", "推广目录基础字段无效"));
    }
    let issued = parse_utc(&catalog.issued_at, "issuedAt")?;
    let expires = parse_utc(&catalog.expires_at, "expiresAt")?;
    if issued > now + TimeDelta::minutes(MAX_CLOCK_SKEW_MINUTES)
        || expires <= now
        || expires <= issued
        || expires - issued > TimeDelta::days(MAX_VALIDITY_DAYS)
    {
        return Err(catalog_error("catalog_time", "推广目录签发或过期时间无效"));
    }
    let mut ids = BTreeSet::new();
    for entry in &catalog.entries {
        validate_entry(entry)?;
        if !ids.insert(entry.id.as_str()) {
            return Err(catalog_error("duplicate_entry", "推广目录包含重复条目 ID"));
        }
    }
    catalog.entries.sort_by(|left, right| {
        right
            .priority
            .cmp(&left.priority)
            .then_with(|| left.id.cmp(&right.id))
    });
    Ok(())
}

/// 功能：验证单个推广条目的文本、family、HTTPS 与披露边界。
///
/// 输入：严格 JSON DTO。
/// 输出：所有展示和兼容字段安全时成功。
/// 不变量：推广文本无控制字符，API URL 不含 query/fragment/userinfo，优先级不超过 1000。
/// 失败：任一字段不满足公开目录合同即拒绝整个目录。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_entry(entry: &SponsoredProviderEntry) -> Result<(), AgentError> {
    validate_entry_id(&entry.id)?;
    validate_text(&entry.display_name, 80, "displayName")?;
    validate_text(&entry.description, 280, "description")?;
    validate_text(&entry.commission_disclosure, 240, "commissionDisclosure")?;
    if !matches!(
        entry.api_family.as_str(),
        "openai-completions" | "openai-responses" | "anthropic-messages"
    ) || entry.priority > 1000
    {
        return Err(catalog_error(
            "entry_field",
            "推广目录条目 family 或优先级无效",
        ));
    }
    validate_https_url(&entry.api_base_url, false, "apiBaseUrl")?;
    validate_https_url(&entry.signup_url, true, "signupUrl")
}

/// 功能：验证 source 的固定版本、HTTPS URL 和原始 SEC1 P-256 公钥。
///
/// 输入：本地 source DTO。
/// 输出：解码后的 65 字节公钥。
/// 不变量：首字节固定为 0x04，远端不能声明另一算法或 keyId。
/// 失败：字段或 Base64 不合法时拒绝网络访问。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_source(source: &SponsoredCatalogSource) -> Result<Vec<u8>, AgentError> {
    if source.schema_version != SCHEMA_VERSION || source.algorithm != SIGNATURE_ALGORITHM {
        return Err(catalog_error(
            "source_metadata",
            "推广目录 source 元数据无效",
        ));
    }
    validate_key_id(&source.key_id)?;
    validate_catalog_url(&source.catalog_url)?;
    validate_public_key_text(&source.public_key)
}

/// 功能：验证公开 trust-key DTO 并返回 SEC1 P-256 公钥。
///
/// 输入：管理员公开 JSON。
/// 输出：解码后的 65 字节公钥。
/// 不变量：只接受 v0.1 和固定 ECDSA 算法。
/// 失败：版本、keyId、算法或公钥编码不符时拒绝。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_trust_key(key: &SponsoredCatalogTrustKey) -> Result<Vec<u8>, AgentError> {
    if key.schema_version != SCHEMA_VERSION || key.algorithm != SIGNATURE_ALGORITHM {
        return Err(catalog_error(
            "trust_key_metadata",
            "推广目录公钥元数据无效",
        ));
    }
    validate_key_id(&key.key_id)?;
    validate_public_key_text(&key.public_key)
}

/// 功能：严格验证远程 catalog URL 的 HTTPS 与无 userinfo 边界。
///
/// 输入：配置 URL 文本。
/// 输出：合法时成功。
/// 不变量：目录 source 可含路径和 query，但不得含 fragment 或用户名密码。
/// 失败：解析或安全边界不符时拒绝保存/联网。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_catalog_url(value: &str) -> Result<(), AgentError> {
    validate_https_url(value, true, "catalogUrl")
}

/// 功能：验证一个 HTTPS URL 及可选 query 策略。
///
/// 输入：URL、是否允许 query 和安全字段名。
/// 输出：scheme/host/userinfo/query/fragment 合法时成功。
/// 不变量：任何 URL 都不能携带用户名、密码或 fragment。
/// 失败：非法 URI 或策略不符返回脱敏字段分类。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_https_url(value: &str, allow_query: bool, field: &str) -> Result<(), AgentError> {
    if value.len() > 2048 {
        return Err(catalog_error("url", format!("推广目录 {field} 无效")));
    }
    let url =
        Url::parse(value).map_err(|_| catalog_error("url", format!("推广目录 {field} 无效")))?;
    if url.scheme() != "https"
        || url.host_str().is_none()
        || !url.username().is_empty()
        || url.password().is_some()
        || url.fragment().is_some()
        || (!allow_query && url.query().is_some())
    {
        return Err(catalog_error("url", format!("推广目录 {field} 无效")));
    }
    Ok(())
}

/// 功能：验证可见推广文本长度并拒绝终端控制字符。
///
/// 输入：文本、Unicode scalar 上限和字段名。
/// 输出：非空且可安全单行展示时成功。
/// 不变量：空白首尾、ESC、换行和其他控制字符均拒绝。
/// 失败：长度或字符策略不符返回字段分类。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_text(value: &str, maximum: usize, field: &str) -> Result<(), AgentError> {
    let length = value.chars().count();
    if value.trim() != value
        || length == 0
        || length > maximum
        || value.chars().any(char::is_control)
    {
        return Err(catalog_error("text", format!("推广目录 {field} 无效")));
    }
    Ok(())
}

/// 功能：验证稳定小写推广条目 ID。
///
/// 输入：候选 ID。
/// 输出：1..64 个小写字母、数字或连字符且首字符为字母数字时成功。
/// 不变量：ID 可安全用于展示与本地索引，但永不直接用作路径。
/// 失败：字符或长度不符返回 `entry_id`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_entry_id(value: &str) -> Result<(), AgentError> {
    let mut characters = value.chars();
    let first = characters.next();
    if value.len() > 64
        || !first
            .is_some_and(|character| character.is_ascii_lowercase() || character.is_ascii_digit())
        || characters.any(|character| {
            !(character.is_ascii_lowercase() || character.is_ascii_digit() || character == '-')
        })
    {
        return Err(catalog_error("entry_id", "推广目录条目 ID 无效"));
    }
    Ok(())
}

/// 功能：验证签名 keyId 的稳定 ASCII 语法。
///
/// 输入：候选 keyId。
/// 输出：1..64 个允许字符时成功。
/// 不变量：首字符必须为小写字母或数字。
/// 失败：非法值返回 `key_id`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_key_id(value: &str) -> Result<(), AgentError> {
    let mut characters = value.chars();
    let first = characters.next();
    if value.len() > 64
        || !first
            .is_some_and(|character| character.is_ascii_lowercase() || character.is_ascii_digit())
        || characters.any(|character| {
            !(character.is_ascii_lowercase()
                || character.is_ascii_digit()
                || matches!(character, '.' | '_' | '-'))
        })
    {
        return Err(catalog_error("key_id", "推广目录 keyId 无效"));
    }
    Ok(())
}

/// 功能：解码并验证 SEC1 uncompressed P-256 公钥文本。
///
/// 输入：标准 Base64 字符串。
/// 输出：首字节 0x04 的精确 65 字节公钥。
/// 不变量：拒绝压缩点、非 canonical Base64 和其他曲线长度。
/// 失败：编码或形状不符返回 `public_key`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_public_key_text(value: &str) -> Result<Vec<u8>, AgentError> {
    let bytes = decode_base64(value, 65, "public_key")?;
    if bytes.len() != 65 || bytes.first() != Some(&4) {
        return Err(catalog_error("public_key", "推广目录公钥形状无效"));
    }
    Ok(bytes)
}

/// 功能：以 canonical 标准 Base64 解码一个有界字段。
///
/// 输入：文本、解码字节上限和安全字段分类。
/// 输出：解码字节。
/// 不变量：重新编码必须与输入逐字节一致，拒绝宽松或非 canonical 表示。
/// 失败：Base64 或长度不符返回对应分类。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn decode_base64(value: &str, maximum: usize, kind: &str) -> Result<Vec<u8>, AgentError> {
    let bytes = BASE64
        .decode(value)
        .map_err(|_| catalog_error(kind, "推广目录 Base64 字段无效"))?;
    if bytes.len() > maximum || BASE64.encode(&bytes) != value {
        return Err(catalog_error(kind, "推广目录 Base64 字段无效"));
    }
    Ok(bytes)
}

/// 功能：解析必须使用 Z 后缀的 RFC 3339 UTC 时间。
///
/// 输入：时间文本和字段名。
/// 输出：归一化 UTC 时点。
/// 不变量：拒绝非 Z offset 和非法日期。
/// 失败：返回安全的 `catalog_time` 分类。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_utc(value: &str, field: &str) -> Result<DateTime<Utc>, AgentError> {
    if !value.ends_with('Z') {
        return Err(catalog_error(
            "catalog_time",
            format!("推广目录 {field} 无效"),
        ));
    }
    DateTime::parse_from_rfc3339(value)
        .map(|time| time.with_timezone(&Utc))
        .map_err(|_| catalog_error("catalog_time", format!("推广目录 {field} 无效")))
}

/// 功能：严格解析 UTF-8 JSON，递归拒绝重复 key、未知字段和 trailing data。
///
/// 输入：有界原始字节与不含路径的文档分类。
/// 输出：目标 DTO。
/// 不变量：错误不会回显远程正文、签名、URL query 或主机路径。
/// 失败：UTF-8、JSON 或 DTO 不符返回 `InvalidRequest`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_strict_json<T: DeserializeOwned>(bytes: &[u8], kind: &str) -> Result<T, AgentError> {
    let text =
        std::str::from_utf8(bytes).map_err(|_| catalog_error(kind, "推广目录文档不是 UTF-8"))?;
    let value = parse_strict_value(text).map_err(|_| catalog_error(kind, "推广目录 JSON 无效"))?;
    serde_json::from_value(value).map_err(|_| catalog_error(kind, "推广目录字段无效"))
}

/// 功能：有界读取普通文件并可要求私钥文件在 Unix 上不向 group/other 暴露。
///
/// 输入：路径、最大字节和敏感权限标志。
/// 输出：完整文件字节。
/// 不变量：拒绝非普通文件、空文件、超限文件；敏感文件在 Unix 上拒绝 077 权限位。
/// 失败：元数据、权限或读取失败返回不回显路径的错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn read_bounded_file(path: &Path, maximum: usize, sensitive: bool) -> Result<Vec<u8>, AgentError> {
    let mut file =
        File::open(path).map_err(|_| catalog_error("file_read", "推广目录文件读取失败"))?;
    let metadata = file
        .metadata()
        .map_err(|_| catalog_error("file_read", "推广目录文件元数据读取失败"))?;
    if !metadata.is_file() || metadata.len() == 0 || metadata.len() > maximum as u64 {
        return Err(catalog_error("file_shape", "推广目录文件形状无效"));
    }
    #[cfg(unix)]
    if sensitive {
        use std::os::unix::fs::PermissionsExt as _;
        if metadata.permissions().mode() & 0o077 != 0 {
            return Err(catalog_error("file_permissions", "推广目录私钥权限过宽"));
        }
    }
    let mut bytes = Vec::with_capacity(metadata.len() as usize);
    file.read_to_end(&mut bytes)
        .map_err(|_| catalog_error("file_read", "推广目录文件读取失败"))?;
    Ok(bytes)
}

/// 功能：以 create-new、可选 Unix 0600 和同步语义写入文件。
///
/// 输入：目标路径、字节和敏感标志。
/// 输出：完整字节已同步后成功。
/// 不变量：永不覆盖既有路径；敏感文件在 Unix 创建时即为 0600。
/// 失败：父目录缺失、路径存在、写入或同步失败返回安全错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn write_create_new(path: &Path, bytes: &[u8], sensitive: bool) -> Result<(), AgentError> {
    let mut options = OpenOptions::new();
    options.create_new(true).write(true);
    #[cfg(unix)]
    if sensitive {
        use std::os::unix::fs::OpenOptionsExt as _;
        options.mode(0o600);
    }
    let mut file = options
        .open(path)
        .map_err(|_| catalog_error("file_create", "推广目录输出文件创建失败"))?;
    file.write_all(bytes)
        .map_err(|_| catalog_error("file_write", "推广目录输出文件写入失败"))?;
    file.sync_all()
        .map_err(|_| catalog_error("file_sync", "推广目录输出文件同步失败"))
}

/// 功能：用同目录临时文件同步后替换非敏感本地 source 配置。
///
/// 输入：固定状态路径、完整 JSON 字节和敏感标志。
/// 输出：新配置成为目标路径后成功。
/// 不变量：临时文件 create-new；失败时尽力清理；不用于私钥。
/// 失败：目录、写入、同步或 rename 失败返回安全错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn atomic_replace_file(path: &Path, bytes: &[u8], sensitive: bool) -> Result<(), AgentError> {
    let parent = path
        .parent()
        .ok_or_else(|| catalog_error("file_path", "推广目录配置路径无效"))?;
    std::fs::create_dir_all(parent)?;
    let temporary = parent.join(format!(".source-{}.tmp", Uuid::new_v4()));
    write_create_new(&temporary, bytes, sensitive)?;
    if path.exists() {
        std::fs::remove_file(path)
            .map_err(|_| catalog_error("file_replace", "推广目录旧配置替换失败"))?;
    }
    if let Err(error) = std::fs::rename(&temporary, path) {
        let _ = std::fs::remove_file(&temporary);
        return Err(catalog_error(
            "file_replace",
            format!("推广目录配置发布失败：{}", error.kind()),
        ));
    }
    Ok(())
}

/// 功能：把摘要编码为固定小写十六进制文本。
///
/// 输入：任意摘要字节。
/// 输出：每字节两个 ASCII 字符的字符串。
/// 不变量：输出不包含路径分隔符并可安全用于内容寻址文件名。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn lower_hex(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut output = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        output.push(char::from(HEX[usize::from(byte >> 4)]));
        output.push(char::from(HEX[usize::from(byte & 0x0f)]));
    }
    output
}

/// 功能：从内容寻址缓存文件名恢复单调版本与 payload SHA-256。
///
/// 输入：目录枚举返回的原始文件名。
/// 输出：严格匹配 `catalog-<u64>-<64 lower hex>.json` 时返回身份。
/// 不变量：只用于本地防回滚状态，不把文件名内容直接当成已验证可展示 catalog。
/// 失败：异常 Unicode、数字、大小写或长度返回 None 并忽略文件。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_cache_filename(value: &OsStr) -> Option<(u64, String)> {
    let text = value.to_str()?;
    let body = text.strip_prefix("catalog-")?.strip_suffix(".json")?;
    let (version, digest) = body.split_once('-')?;
    if digest.len() != 64
        || !digest
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return None;
    }
    Some((version.parse().ok()?, digest.to_owned()))
}

/// 功能：从有效缓存构造不含远端细节的明确降级结果。
///
/// 输入：已验证缓存和固定中文警告。
/// 输出：来源为 cache 的加载结果。
/// 不变量：不复制 envelope、签名或 URL 到用户输出。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn cache_fallback(value: &VerifiedCatalog, warning: &str) -> SponsoredCatalogLoad {
    SponsoredCatalogLoad {
        origin: SponsoredCatalogOrigin::Cache,
        catalog: Some(value.catalog.clone()),
        warning: Some(warning.to_owned()),
    }
}

/// 功能：创建不含远端正文、密钥和主机路径的推广目录结构化错误。
///
/// 输入：稳定细分类和中文消息。
/// 输出：默认不可重试的 `InvalidRequest`，transport 类调用方可使用缓存降级。
/// 不变量：details 只含公开 kind。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn catalog_error(kind: &str, message: impl Into<String>) -> AgentError {
    AgentError::new(ErrorCode::InvalidRequest, message)
        .with_details(serde_json::json!({"kind":format!("sponsored_catalog_{kind}")}))
}

#[cfg(test)]
mod tests {
    use std::fs;

    use base64::Engine as _;
    use chrono::{TimeDelta, Utc};
    use serde_json::json;
    use tempfile::tempdir;

    use super::{
        SponsoredCatalogService, SponsoredProviderCatalog, generate_catalog_keypair,
        sign_catalog_file,
    };

    /// 功能：生成当前有效的最小 catalog JSON 测试字节。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn valid_catalog_bytes(version: u64) -> Vec<u8> {
        let now = Utc::now();
        serde_json::to_vec(&json!({
            "schemaVersion":"0.1",
            "catalogVersion":version,
            "issuedAt":now.to_rfc3339_opts(chrono::SecondsFormat::Secs, true),
            "expiresAt":(now + TimeDelta::days(30)).to_rfc3339_opts(chrono::SecondsFormat::Secs, true),
            "entries":[{
                "id":"example-relay",
                "displayName":"示例中转站",
                "description":"仅用于测试的推广条目",
                "apiFamily":"openai-completions",
                "apiBaseUrl":"https://relay.example/v1",
                "signupUrl":"https://relay.example/register?ref=test",
                "commissionDisclosure":"通过该链接注册，发行方可能获得佣金",
                "priority":100
            }]
        }))
        .expect("test catalog serialization must succeed")
    }

    /// 功能：验证运行时临时密钥可以签名、配置并从内容缓存被 Rust 独立读取。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn signs_configures_and_loads_cached_catalog() -> Result<(), Box<dyn std::error::Error>> {
        let directory = tempdir()?;
        let private_key = directory.path().join("private.der");
        let public_key = directory.path().join("public.json");
        let catalog = directory.path().join("catalog.json");
        let envelope = directory.path().join("envelope.json");
        fs::write(&catalog, valid_catalog_bytes(1))?;
        generate_catalog_keypair("owner-2026-01", &private_key, &public_key)?;
        sign_catalog_file(&catalog, &private_key, &public_key, &envelope)?;

        let service = SponsoredCatalogService::new(directory.path().join("state"))?;
        service.configure("https://catalog.example/sponsors.json", &public_key)?;
        let source = service.read_source()?.expect("source must exist");
        let key = super::validate_source(&source)?;
        let envelope_bytes = fs::read(&envelope)?;
        let verified = super::verify_envelope(&envelope_bytes, &source.key_id, &key, Utc::now())?;
        service.persist_cache(&source, &verified)?;

        let loaded = service.load(true).await?;
        assert_eq!(
            loaded.catalog.expect("cache must load").entries[0].id,
            "example-relay"
        );
        Ok(())
    }

    /// 功能：验证签名后修改 payload 会被拒绝而不能进入缓存。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn rejects_tampered_signed_payload() -> Result<(), Box<dyn std::error::Error>> {
        let directory = tempdir()?;
        let private_key = directory.path().join("private.der");
        let public_key = directory.path().join("public.json");
        let catalog = directory.path().join("catalog.json");
        let envelope = directory.path().join("envelope.json");
        fs::write(&catalog, valid_catalog_bytes(1))?;
        generate_catalog_keypair("owner-2026-01", &private_key, &public_key)?;
        sign_catalog_file(&catalog, &private_key, &public_key, &envelope)?;
        let trust: super::SponsoredCatalogTrustKey =
            super::parse_strict_json(&fs::read(&public_key)?, "trust")?;
        let key = super::validate_trust_key(&trust)?;
        let mut value: serde_json::Value = serde_json::from_slice(&fs::read(&envelope)?)?;
        let mut payload = base64::engine::general_purpose::STANDARD
            .decode(value["payload"].as_str().expect("payload string"))?;
        payload[0] ^= 1;
        value["payload"] = json!(base64::engine::general_purpose::STANDARD.encode(payload));
        let tampered = serde_json::to_vec(&value)?;
        assert!(super::verify_envelope(&tampered, &trust.key_id, &key, Utc::now()).is_err());
        Ok(())
    }

    /// 功能：验证控制字符广告和非 HTTPS API base 会使整个目录失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn rejects_terminal_injection_and_insecure_endpoint() {
        let now = Utc::now();
        let mut catalog: SponsoredProviderCatalog = serde_json::from_value(json!({
            "schemaVersion":"0.1",
            "catalogVersion":1,
            "issuedAt":now.to_rfc3339_opts(chrono::SecondsFormat::Secs, true),
            "expiresAt":(now + TimeDelta::days(30)).to_rfc3339_opts(chrono::SecondsFormat::Secs, true),
            "entries":[{
                "id":"bad",
                "displayName":"恶意\u{001b}[31m",
                "description":"bad",
                "apiFamily":"openai-completions",
                "apiBaseUrl":"http://relay.example/v1",
                "signupUrl":"https://relay.example/register",
                "commissionDisclosure":"推广",
                "priority":1
            }]
        }))
        .expect("test DTO must decode");
        assert!(super::validate_catalog(&mut catalog, now).is_err());
    }
}
