using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using QxnmForge.Serialization;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：表示推广目录公开验签密钥；不包含私钥或凭据。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="SchemaVersion">固定 0.1。</param>
/// <param name="KeyId">稳定签名密钥标识。</param>
/// <param name="Algorithm">固定 ECDSA P-256 算法标识。</param>
/// <param name="PublicKey">SEC1 uncompressed point 的标准 Base64。</param>
public sealed record SponsoredCatalogTrustKey(
    string SchemaVersion,
    string KeyId,
    string Algorithm,
    string PublicKey);

/// <summary>
/// 功能：表示客户端显式安装的远程推广目录 URL 与固定公钥。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="SchemaVersion">固定 0.1。</param>
/// <param name="CatalogUrl">禁止重定向的 HTTPS envelope URL。</param>
/// <param name="KeyId">预期签名 keyId。</param>
/// <param name="Algorithm">固定签名算法。</param>
/// <param name="PublicKey">原始 P-256 公钥 Base64。</param>
public sealed record SponsoredCatalogSource(
    string SchemaVersion,
    string CatalogUrl,
    string KeyId,
    string Algorithm,
    string PublicKey);

/// <summary>
/// 功能：表示一个明确标注推广和返佣关系的 Provider 条目。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Id">稳定小写条目 ID。</param>
/// <param name="DisplayName">单行展示名称。</param>
/// <param name="Description">单行推广说明。</param>
/// <param name="ApiFamily">受限 API family。</param>
/// <param name="ApiBaseUrl">无凭据的 HTTPS API base。</param>
/// <param name="SignupUrl">允许公开 affiliate query 的 HTTPS 注册链接。</param>
/// <param name="CommissionDisclosure">必须显示的返佣披露。</param>
/// <param name="Priority">0..1000 展示优先级。</param>
public sealed record SponsoredProviderEntry(
    string Id,
    string DisplayName,
    string Description,
    string ApiFamily,
    string ApiBaseUrl,
    string SignupUrl,
    string CommissionDisclosure,
    ushort Priority);

/// <summary>
/// 功能：表示签名 payload 内经过严格验证的推广 Provider 目录。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="SchemaVersion">固定 0.1。</param>
/// <param name="CatalogVersion">单调递增且防回滚的版本。</param>
/// <param name="IssuedAt">带 Z 的 UTC 签发时间。</param>
/// <param name="ExpiresAt">最长 90 天的 UTC 过期时间。</param>
/// <param name="Entries">最多 64 个推广条目。</param>
public sealed record SponsoredProviderCatalog(
    string SchemaVersion,
    ulong CatalogVersion,
    string IssuedAt,
    string ExpiresAt,
    IReadOnlyList<SponsoredProviderEntry> Entries);

/// <summary>
/// 功能：标识推广目录来自未配置、远端或重新验签的缓存。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public enum SponsoredCatalogOrigin
{
    Unconfigured,
    Remote,
    Cache,
}

/// <summary>
/// 功能：携带推广目录、来源及不含敏感信息的降级诊断。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Origin">实际来源。</param>
/// <param name="Catalog">未配置时为 null，否则为有效目录。</param>
/// <param name="Warning">远端失败并使用缓存时的固定中文警告。</param>
public sealed record SponsoredCatalogLoad(
    SponsoredCatalogOrigin Origin,
    SponsoredProviderCatalog? Catalog,
    string? Warning);

/// <summary>
/// 功能：表示推广目录配置、签名、网络或缓存验证失败，且不携带原始远端正文。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class SponsoredCatalogException : Exception
{
    /// <summary>
    /// 功能：创建带稳定安全分类的推广目录异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">不含 URL、路径、签名和密钥的错误分类。</param>
    /// <param name="message">中文安全消息。</param>
    internal SponsoredCatalogException(string kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    /// <summary>
    /// 功能：取得调用方可稳定记录但不应解析消息文本的错误分类。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string Kind { get; }
}

/// <summary>
/// 功能：使用 .NET 原生 HTTP、ECDSA、JSON 与文件系统加载签名推广 Provider 目录。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class SponsoredCatalogService : IDisposable
{
    private const string SchemaVersion = "0.1";
    private const string SignatureAlgorithm = "ecdsa-p256-sha256-asn1";
    private const int MaximumDocumentBytes = 256 * 1024;
    private const int MaximumEntries = 64;
    private static readonly TimeSpan MaximumValidity = TimeSpan.FromDays(90);
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(5);
    private readonly string stateRoot;
    private readonly HttpClient httpClient;

    /// <summary>
    /// 功能：创建禁止 redirect、环境代理且总超时五秒的推广目录服务。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="stateRoot">工作区外的用户状态根。</param>
    /// <remarks>构造不访问网络或文件；调用方负责释放服务。</remarks>
    public SponsoredCatalogService(string stateRoot)
        : this(stateRoot, CreateHttpClient())
    {
    }

    /// <summary>
    /// 功能：为单元测试注入有界 HTTP client，同时保留全部 payload 验证。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="stateRoot">隔离状态根。</param>
    /// <param name="httpClient">由调用方独占且会随服务释放的 client。</param>
    internal SponsoredCatalogService(string stateRoot, HttpClient httpClient)
    {
        ArgumentException.ThrowIfNullOrEmpty(stateRoot);
        this.stateRoot = Path.GetFullPath(stateRoot);
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// 功能：显式安装 HTTPS 目录 URL 和公开验签密钥到用户状态目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="catalogUrl">远程 signed envelope URL。</param>
    /// <param name="trustKeyPath">公开 trust-key JSON；不得传私钥。</param>
    /// <remarks>不变量：远端响应不能更换 keyId/public key；source 不包含凭据。</remarks>
    /// <exception cref="SponsoredCatalogException">URL、公钥、JSON、I/O 或同步失败。</exception>
    public void Configure(string catalogUrl, string trustKeyPath)
    {
        ValidateCatalogUrl(catalogUrl);
        var trustBytes = ReadBoundedFile(trustKeyPath, 16 * 1024, sensitive: false);
        var trust = ParseStrict<SponsoredCatalogTrustKey>(trustBytes, "trust_key");
        var publicKey = ValidateTrustKey(trust);
        var source = new SponsoredCatalogSource(
            SchemaVersion,
            catalogUrl,
            trust.KeyId,
            SignatureAlgorithm,
            Convert.ToBase64String(publicKey));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source, JsonDefaults.Options);
        AtomicReplaceFile(SourcePath(), bytes);
    }

    /// <summary>
    /// 功能：从签名远端或仍有效的重新验签缓存加载推广目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="offline">为 true 时保证不访问网络。</param>
    /// <param name="cancellationToken">网络和响应读取取消信号。</param>
    /// <returns>来源、可选目录和安全降级警告。</returns>
    /// <remarks>不变量：拒绝远端降版本和同版本不同 payload；每次缓存读取重新验签。</remarks>
    /// <exception cref="SponsoredCatalogException">已配置但无有效远端或缓存。</exception>
    public async Task<SponsoredCatalogLoad> LoadAsync(
        bool offline,
        CancellationToken cancellationToken)
    {
        var source = ReadSource();
        if (source is null)
        {
            return new SponsoredCatalogLoad(SponsoredCatalogOrigin.Unconfigured, null, null);
        }

        var publicKey = ValidateSource(source);
        var cached = ReadCacheState(source, publicKey);
        if (offline)
        {
            return cached.Active is null
                ? throw Error("cache_unavailable", "离线模式没有仍在有效期内的推广目录缓存")
                : new SponsoredCatalogLoad(SponsoredCatalogOrigin.Cache, cached.Active.Catalog, null);
        }

        VerifiedCatalog? remote = null;
        SponsoredCatalogException? remoteError = null;
        try
        {
            var bytes = await FetchAsync(source.CatalogUrl, cancellationToken).ConfigureAwait(false);
            remote = VerifyEnvelope(bytes, source.KeyId, publicKey, DateTimeOffset.UtcNow);
        }
        catch (SponsoredCatalogException exception)
        {
            remoteError = exception;
        }

        if (remote is null)
        {
            return cached.Active is null
                ? throw remoteError ?? Error("remote", "远程推广目录不可用")
                : CacheFallback(cached.Active, "远端推广目录不可用，已继续使用有效缓存");
        }

        if (cached.SeenPayloads.Count > 0)
        {
            var highestSeen = cached.SeenPayloads.Keys.Last();
            if (remote.Catalog.CatalogVersion < highestSeen)
            {
                return cached.Active is null
                    ? throw Error("rollback", "远端推广目录版本回滚")
                    : CacheFallback(cached.Active, "远端推广目录版本回滚，已继续使用缓存");
            }

            var remoteDigest = Convert.ToHexStringLower(SHA256.HashData(remote.Payload));
            if (cached.SeenPayloads.TryGetValue(remote.Catalog.CatalogVersion, out var seen) &&
                !seen.Contains(remoteDigest))
            {
                return cached.Active is null
                    ? throw Error("equivocation", "远端推广目录同版本内容冲突")
                    : CacheFallback(cached.Active, "远端推广目录同版本内容冲突，已继续使用缓存");
            }
        }

        PersistCache(source, remote);
        return new SponsoredCatalogLoad(SponsoredCatalogOrigin.Remote, remote.Catalog, null);
    }

    /// <summary>
    /// 功能：释放推广目录专用 HttpClient 和连接池。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public void Dispose()
    {
        httpClient.Dispose();
    }

    /// <summary>
    /// 功能：创建禁用 redirect/proxy/decompression 的原生 HTTP client。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>连接超时三秒、总超时五秒的独占 client。</returns>
    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(3),
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    /// <summary>
    /// 功能：读取严格且有界的本地 source 配置。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>未配置时 null，否则返回 DTO。</returns>
    /// <exception cref="SponsoredCatalogException">文件或 JSON 不合法。</exception>
    private SponsoredCatalogSource? ReadSource()
    {
        var path = SourcePath();
        return File.Exists(path)
            ? ParseStrict<SponsoredCatalogSource>(ReadBoundedFile(path, 16 * 1024, sensitive: false), "source")
            : null;
    }

    /// <summary>
    /// 功能：获取一个禁止重定向且最多 256 KiB 的远程 envelope。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="url">已经过 source 验证的 HTTPS URL。</param>
    /// <param name="cancellationToken">外部取消信号。</param>
    /// <returns>完整有界响应字节。</returns>
    /// <exception cref="SponsoredCatalogException">网络、状态、超时、中断或超限。</exception>
    private async Task<byte[]> FetchAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw Error("http_status", "远程推广目录返回非 200 状态");
            }

            if (response.Content.Headers.ContentLength is > MaximumDocumentBytes)
            {
                throw Error("document_too_large", "远程推广目录超过大小上限");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > MaximumDocumentBytes)
                {
                    throw Error("document_too_large", "远程推广目录超过大小上限");
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        catch (SponsoredCatalogException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Error("timeout", "远程推广目录请求超时");
        }
        catch (HttpRequestException)
        {
            throw Error("transport", "远程推广目录请求失败");
        }
        catch (IOException)
        {
            throw Error("transport", "远程推广目录响应中断");
        }
    }

    /// <summary>
    /// 功能：扫描 source 隔离缓存并选择最高的仍有效签名版本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="source">已验证 source。</param>
    /// <param name="publicKey">原始 65 字节 P-256 公钥。</param>
    /// <returns>仍有效的最高目录，以及含过期版本在内的已见 payload 摘要。</returns>
    private CatalogCacheState ReadCacheState(SponsoredCatalogSource source, byte[] publicKey)
    {
        var directory = CacheDirectory(source);
        if (!Directory.Exists(directory))
        {
            return new CatalogCacheState(null, new SortedDictionary<ulong, HashSet<string>>());
        }

        VerifiedCatalog? best = null;
        var seen = new SortedDictionary<ulong, HashSet<string>>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (!TryParseCacheFileName(Path.GetFileName(path), out var version, out var digest))
            {
                continue;
            }

            if (!seen.TryGetValue(version, out var digests))
            {
                digests = new HashSet<string>(StringComparer.Ordinal);
                seen.Add(version, digests);
            }

            digests.Add(digest);
            try
            {
                var candidate = VerifyEnvelope(
                    ReadBoundedFile(path, MaximumDocumentBytes, sensitive: false),
                    source.KeyId,
                    publicKey,
                    DateTimeOffset.UtcNow);
                if (best is null || candidate.Catalog.CatalogVersion > best.Catalog.CatalogVersion)
                {
                    best = candidate;
                }
            }
            catch (SponsoredCatalogException)
            {
                // 单个无效缓存不获得信任，也不阻止选择另一个有效内容寻址版本。
            }
        }

        return new CatalogCacheState(best, seen);
    }

    /// <summary>
    /// 功能：把已验证 envelope 写成不可覆盖的内容寻址缓存。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="source">当前信任 source。</param>
    /// <param name="catalog">已完成验签的目录。</param>
    /// <remarks>同内容已存在视为幂等成功；既有文件永不覆盖。</remarks>
    private void PersistCache(SponsoredCatalogSource source, VerifiedCatalog catalog)
    {
        var directory = CacheDirectory(source);
        Directory.CreateDirectory(directory);
        var digest = Convert.ToHexStringLower(SHA256.HashData(catalog.Payload));
        var path = Path.Combine(directory, $"catalog-{catalog.Catalog.CatalogVersion}-{digest}.json");
        try
        {
            WriteCreateNew(path, catalog.Envelope, sensitive: false);
        }
        catch (SponsoredCatalogException exception) when (exception.Kind == "file_exists")
        {
            // 内容寻址路径已经存在时不需要重写。
        }
    }

    /// <summary>
    /// 功能：返回状态根下固定 source 配置路径。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>不依赖工作区的绝对路径。</returns>
    private string SourcePath()
    {
        return Path.Combine(stateRoot, "sponsored-provider-catalog", "source.json");
    }

    /// <summary>
    /// 功能：按 URL、keyId、公钥派生不暴露 URL 的缓存 scope。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="source">已验证 source。</param>
    /// <returns>SHA-256 scope 缓存目录。</returns>
    private string CacheDirectory(SponsoredCatalogSource source)
    {
        var material = System.Text.Encoding.UTF8.GetBytes(
            source.CatalogUrl + "\0" + source.KeyId + "\0" + source.PublicKey);
        var digest = Convert.ToHexStringLower(SHA256.HashData(material));
        return Path.Combine(stateRoot, "sponsored-provider-catalog", "cache", digest);
    }

    /// <summary>
    /// 功能：验证 signed envelope、原始 payload 签名和全部 catalog 业务约束。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="envelopeBytes">有界 envelope 原始字节。</param>
    /// <param name="expectedKeyId">本地 source 固定 keyId。</param>
    /// <param name="publicKey">SEC1 uncompressed 公钥。</param>
    /// <param name="now">可信 UTC 当前时间。</param>
    /// <returns>排序后的目录、原始 payload 与 envelope。</returns>
    /// <remarks>签名覆盖 Base64 解码后的精确 payload，绝不重序列化后验签。</remarks>
    internal static VerifiedCatalog VerifyEnvelope(
        byte[] envelopeBytes,
        string expectedKeyId,
        byte[] publicKey,
        DateTimeOffset now)
    {
        if (envelopeBytes.Length > MaximumDocumentBytes)
        {
            throw Error("document_too_large", "推广目录 envelope 超过大小上限");
        }

        var envelope = ParseStrict<CatalogEnvelope>(envelopeBytes, "envelope");
        if (envelope.SchemaVersion != SchemaVersion ||
            envelope.Signature.Algorithm != SignatureAlgorithm ||
            envelope.Signature.KeyId != expectedKeyId)
        {
            throw Error("signature_metadata", "推广目录签名元数据不匹配");
        }

        var payload = DecodeBase64(envelope.Payload, MaximumDocumentBytes, "payload");
        var signature = DecodeBase64(envelope.Signature.Value, 128, "signature");
        using var verifier = CreateVerifier(publicKey);
        if (!verifier.VerifyData(
                payload,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence))
        {
            throw Error("signature_invalid", "推广目录签名无效");
        }

        var catalog = ValidateCatalog(ParseStrict<SponsoredProviderCatalog>(payload, "catalog"), now);
        return new VerifiedCatalog(catalog, payload, envelopeBytes);
    }

    /// <summary>
    /// 功能：验证并按 priority 降序、ID 升序规范化 catalog。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="catalog">严格 DTO。</param>
    /// <param name="now">可信 UTC 当前时间。</param>
    /// <returns>条目顺序确定的不可变目录。</returns>
    internal static SponsoredProviderCatalog ValidateCatalog(
        SponsoredProviderCatalog catalog,
        DateTimeOffset now)
    {
        if (catalog.SchemaVersion != SchemaVersion ||
            catalog.CatalogVersion is 0 or > 9_007_199_254_740_991 ||
            catalog.Entries.Count > MaximumEntries ||
            !TryParseUtc(catalog.IssuedAt, out var issued) ||
            !TryParseUtc(catalog.ExpiresAt, out var expires) ||
            issued > now + MaximumClockSkew ||
            expires <= now ||
            expires <= issued ||
            expires - issued > MaximumValidity)
        {
            throw Error("catalog_shape", "推广目录基础字段或时间无效");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in catalog.Entries)
        {
            ValidateEntry(entry);
            if (!ids.Add(entry.Id))
            {
                throw Error("duplicate_entry", "推广目录包含重复条目 ID");
            }
        }

        return catalog with
        {
            Entries = catalog.Entries
                .OrderByDescending(static entry => entry.Priority)
                .ThenBy(static entry => entry.Id, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    /// <summary>
    /// 功能：验证单个推广条目的文本、family、URL 和披露边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="entry">待验证 DTO。</param>
    private static void ValidateEntry(SponsoredProviderEntry entry)
    {
        ValidateEntryId(entry.Id);
        ValidateText(entry.DisplayName, 80, "displayName");
        ValidateText(entry.Description, 280, "description");
        ValidateText(entry.CommissionDisclosure, 240, "commissionDisclosure");
        if (entry.ApiFamily is not ("openai-completions" or "openai-responses" or "anthropic-messages") ||
            entry.Priority > 1000)
        {
            throw Error("entry_field", "推广目录条目 family 或优先级无效");
        }

        ValidateHttpsUrl(entry.ApiBaseUrl, allowQuery: false, "apiBaseUrl");
        ValidateHttpsUrl(entry.SignupUrl, allowQuery: true, "signupUrl");
    }

    /// <summary>
    /// 功能：验证 source 的固定算法、HTTPS URL 和原始 P-256 公钥。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="source">本地 source DTO。</param>
    /// <returns>65 字节公钥。</returns>
    private static byte[] ValidateSource(SponsoredCatalogSource source)
    {
        if (source.SchemaVersion != SchemaVersion || source.Algorithm != SignatureAlgorithm)
        {
            throw Error("source_metadata", "推广目录 source 元数据无效");
        }

        ValidateKeyId(source.KeyId);
        ValidateCatalogUrl(source.CatalogUrl);
        return ValidatePublicKey(source.PublicKey);
    }

    /// <summary>
    /// 功能：验证公开 trust-key 固定版本、算法、keyId 和公钥。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="key">公开 JSON DTO。</param>
    /// <returns>65 字节 SEC1 公钥。</returns>
    internal static byte[] ValidateTrustKey(SponsoredCatalogTrustKey key)
    {
        if (key.SchemaVersion != SchemaVersion || key.Algorithm != SignatureAlgorithm)
        {
            throw Error("trust_key_metadata", "推广目录公钥元数据无效");
        }

        ValidateKeyId(key.KeyId);
        return ValidatePublicKey(key.PublicKey);
    }

    /// <summary>
    /// 功能：验证远程目录 URL 可安全用于无凭据 GET。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">URL 文本。</param>
    private static void ValidateCatalogUrl(string value)
    {
        ValidateHttpsUrl(value, allowQuery: true, "catalogUrl");
    }

    /// <summary>
    /// 功能：验证 HTTPS、host、userinfo、query 和 fragment 策略。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选 URL。</param>
    /// <param name="allowQuery">是否允许公开 query。</param>
    /// <param name="field">安全字段名。</param>
    private static void ValidateHttpsUrl(string value, bool allowQuery, string field)
    {
        if (value.Length > 2048 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.IdnHost) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (!allowQuery && !string.IsNullOrEmpty(uri.Query)))
        {
            throw Error("url", $"推广目录 {field} 无效");
        }
    }

    /// <summary>
    /// 功能：验证单行展示文本的长度、首尾空白和控制字符。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">展示文本。</param>
    /// <param name="maximum">Unicode 字符上限。</param>
    /// <param name="field">安全字段名。</param>
    private static void ValidateText(string value, int maximum, string field)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > maximum ||
            value.Trim() != value ||
            value.Any(char.IsControl))
        {
            throw Error("text", $"推广目录 {field} 无效");
        }
    }

    /// <summary>
    /// 功能：验证 1..64 字节小写推广条目 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选 ID。</param>
    private static void ValidateEntryId(string value)
    {
        if (!ValidateAsciiIdentifier(value, allowDotUnderscore: false))
        {
            throw Error("entry_id", "推广目录条目 ID 无效");
        }
    }

    /// <summary>
    /// 功能：验证 1..64 字节签名 keyId。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选 keyId。</param>
    internal static void ValidateKeyId(string value)
    {
        if (!ValidateAsciiIdentifier(value, allowDotUnderscore: true))
        {
            throw Error("key_id", "推广目录 keyId 无效");
        }
    }

    /// <summary>
    /// 功能：验证小写 ASCII 标识符的共同语法。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选文本。</param>
    /// <param name="allowDotUnderscore">是否允许 keyId 的点和下划线。</param>
    /// <returns>字符、长度和首字符都合法时 true。</returns>
    private static bool ValidateAsciiIdentifier(string value, bool allowDotUnderscore)
    {
        return value.Length is >= 1 and <= 64 &&
               IsLowerAlphaNumeric(value[0]) &&
               value.All(character =>
                   IsLowerAlphaNumeric(character) ||
                   character == '-' ||
                   (allowDotUnderscore && character is '.' or '_'));
    }

    /// <summary>
    /// 功能：判断字符是否为 ASCII 小写字母或数字。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选字符。</param>
    /// <returns>属于允许集合时 true。</returns>
    private static bool IsLowerAlphaNumeric(char value)
    {
        return value is >= 'a' and <= 'z' or >= '0' and <= '9';
    }

    /// <summary>
    /// 功能：解码并验证 SEC1 uncompressed P-256 公钥。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">标准 Base64。</param>
    /// <returns>首字节 0x04 的精确 65 字节。</returns>
    private static byte[] ValidatePublicKey(string value)
    {
        var bytes = DecodeBase64(value, 65, "public_key");
        return bytes.Length == 65 && bytes[0] == 4
            ? bytes
            : throw Error("public_key", "推广目录公钥形状无效");
    }

    /// <summary>
    /// 功能：从原始 SEC1 点构建 NIST P-256 验签器。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="publicKey">65 字节 uncompressed point。</param>
    /// <returns>由调用方释放的 ECDsa。</returns>
    private static ECDsa CreateVerifier(byte[] publicKey)
    {
        if (publicKey.Length != 65 || publicKey[0] != 4)
        {
            throw Error("public_key", "推广目录公钥形状无效");
        }

        var verifier = ECDsa.Create();
        verifier.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = publicKey[1..33],
                Y = publicKey[33..65],
            },
        });
        return verifier;
    }

    /// <summary>
    /// 功能：严格 Base64 解码并拒绝非 canonical 或超限表示。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选文本。</param>
    /// <param name="maximum">解码字节上限。</param>
    /// <param name="kind">安全错误分类。</param>
    /// <returns>解码字节。</returns>
    internal static byte[] DecodeBase64(string value, int maximum, string kind)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            throw Error(kind, "推广目录 Base64 字段无效");
        }

        if (bytes.Length > maximum || Convert.ToBase64String(bytes) != value)
        {
            throw Error(kind, "推广目录 Base64 字段无效");
        }

        return bytes;
    }

    /// <summary>
    /// 功能：解析必须带 Z 的 RFC 3339 UTC 时间。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选时间。</param>
    /// <param name="result">成功时 UTC 时点。</param>
    /// <returns>严格 Z 时间成功时 true。</returns>
    private static bool TryParseUtc(string value, out DateTimeOffset result)
    {
        result = default;
        return value.EndsWith('Z') &&
               DateTimeOffset.TryParse(
                   value,
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AssumeUniversal |
                   System.Globalization.DateTimeStyles.AdjustToUniversal,
                   out result);
    }

    /// <summary>
    /// 功能：严格解析 UTF-8 JSON 并递归拒绝重复 key、未知字段和 trailing data。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <typeparam name="T">关闭未知成员的目标 DTO。</typeparam>
    /// <param name="bytes">有界完整文档。</param>
    /// <param name="kind">安全文档分类。</param>
    /// <returns>目标 DTO。</returns>
    internal static T ParseStrict<T>(byte[] bytes, string kind)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            ValidateNoDuplicateKeys(document.RootElement);
            return JsonSerializer.Deserialize<T>(document.RootElement.GetRawText(), JsonDefaults.Options)
                ?? throw Error(kind, "推广目录 JSON 根无效");
        }
        catch (SponsoredCatalogException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw Error(kind, "推广目录 JSON 或字段无效");
        }
    }

    /// <summary>
    /// 功能：递归拒绝 JSON object 中大小写敏感的重复属性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">当前节点。</param>
    private static void ValidateNoDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Error("duplicate_key", "推广目录 JSON 包含重复字段");
                }

                ValidateNoDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateNoDuplicateKeys(item);
            }
        }
    }

    /// <summary>
    /// 功能：有界读取普通文件，并在 Unix 上验证敏感文件不向 group/other 开放。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">显式管理员或固定状态路径。</param>
    /// <param name="maximum">最大字节。</param>
    /// <param name="sensitive">是否应用私钥权限检查。</param>
    /// <returns>完整字节。</returns>
    internal static byte[] ReadBoundedFile(string path, int maximum, bool sensitive)
    {
        try
        {
            var information = new FileInfo(path);
            if (!information.Exists ||
                information.Attributes.HasFlag(FileAttributes.Directory) ||
                information.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                information.Length is <= 0 ||
                information.Length > maximum)
            {
                throw Error("file_shape", "推广目录文件形状无效");
            }

            if (sensitive && !OperatingSystem.IsWindows())
            {
                const UnixFileMode exposed = UnixFileMode.GroupRead |
                                             UnixFileMode.GroupWrite |
                                             UnixFileMode.GroupExecute |
                                             UnixFileMode.OtherRead |
                                             UnixFileMode.OtherWrite |
                                             UnixFileMode.OtherExecute;
                if ((File.GetUnixFileMode(path) & exposed) != 0)
                {
                    throw Error("file_permissions", "推广目录私钥权限过宽");
                }
            }

            return File.ReadAllBytes(path);
        }
        catch (SponsoredCatalogException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Error("file_read", "推广目录文件读取失败");
        }
    }

    /// <summary>
    /// 功能：以 create-new、WriteThrough 和可选 Unix 0600 写入文件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">必须尚不存在的目标。</param>
    /// <param name="bytes">完整内容。</param>
    /// <param name="sensitive">是否创建私钥权限。</param>
    internal static void WriteCreateNew(string path, byte[] bytes, bool sensitive)
    {
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
            };
            if (sensitive && !OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using var stream = new FileStream(path, options);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(path))
        {
            throw Error("file_exists", "推广目录输出路径已经存在");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Error("file_write", "推广目录输出文件写入失败");
        }
    }

    /// <summary>
    /// 功能：同目录 create-new 临时文件同步后替换非敏感 source 配置。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">固定 source 路径。</param>
    /// <param name="bytes">完整 JSON。</param>
    private static void AtomicReplaceFile(string path, byte[] bytes)
    {
        var parent = Path.GetDirectoryName(path) ?? throw Error("file_path", "推广目录配置路径无效");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, ".source-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            WriteCreateNew(temporary, bytes, sensitive: false);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // 原错误更重要；临时文件不含 secret。
            }

            throw;
        }
    }

    /// <summary>
    /// 功能：从内容寻址缓存文件名恢复单调版本和 payload SHA-256。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="name">不含目录的文件名。</param>
    /// <param name="version">成功时非零版本。</param>
    /// <param name="digest">成功时 64 字符小写 SHA-256。</param>
    /// <returns>严格匹配 `catalog-&lt;u64&gt;-&lt;digest&gt;.json` 时 true。</returns>
    /// <remarks>文件名只保留防回滚状态，实际展示仍必须重新验签完整文件。</remarks>
    private static bool TryParseCacheFileName(
        string name,
        out ulong version,
        out string digest)
    {
        version = 0;
        digest = string.Empty;
        const string prefix = "catalog-";
        const string suffix = ".json";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = name[prefix.Length..^suffix.Length];
        var separator = body.IndexOf('-');
        if (separator <= 0 ||
            !ulong.TryParse(
                body.AsSpan(0, separator),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out version))
        {
            return false;
        }

        digest = body[(separator + 1)..];
        return digest.Length == 64 &&
               digest.All(static character =>
                   character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    /// <summary>
    /// 功能：从有效缓存构造明确且不泄露 URL 的降级结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">已验证缓存。</param>
    /// <param name="warning">固定中文警告。</param>
    /// <returns>来源为 Cache 的结果。</returns>
    private static SponsoredCatalogLoad CacheFallback(VerifiedCatalog value, string warning)
    {
        return new SponsoredCatalogLoad(SponsoredCatalogOrigin.Cache, value.Catalog, warning);
    }

    /// <summary>
    /// 功能：创建不含原始正文、签名、密钥、URL 和路径的推广目录异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">稳定细分类。</param>
    /// <param name="message">中文安全消息。</param>
    /// <returns>异常对象。</returns>
    internal static SponsoredCatalogException Error(string kind, string message)
    {
        return new SponsoredCatalogException(kind, message);
    }

    internal sealed record CatalogSignature(string Algorithm, string KeyId, string Value);

    internal sealed record CatalogEnvelope(
        string SchemaVersion,
        string Payload,
        CatalogSignature Signature);

    internal sealed record VerifiedCatalog(
        SponsoredProviderCatalog Catalog,
        byte[] Payload,
        byte[] Envelope);

    private sealed record CatalogCacheState(
        VerifiedCatalog? Active,
        SortedDictionary<ulong, HashSet<string>> SeenPayloads);
}

/// <summary>
/// 功能：提供离线推广目录密钥生成与 envelope 签名，不执行网络请求。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public static class SponsoredCatalogPublisher
{
    private const string SchemaVersion = "0.1";
    private const string SignatureAlgorithm = "ecdsa-p256-sha256-asn1";

    /// <summary>
    /// 功能：生成 PKCS#8 ECDSA P-256 私钥和公开 trust-key JSON。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="keyId">稳定小写密钥 ID。</param>
    /// <param name="privateKeyPath">必须不存在的私钥输出。</param>
    /// <param name="publicKeyPath">必须不存在的公开 JSON 输出。</param>
    /// <remarks>不变量：私钥永不打印；Unix 创建权限为 0600；既有文件不覆盖。</remarks>
    /// <exception cref="SponsoredCatalogException">密钥、路径、序列化或 I/O 失败。</exception>
    public static void GenerateKeyPair(string keyId, string privateKeyPath, string publicKeyPath)
    {
        SponsoredCatalogService.ValidateKeyId(keyId);
        if (File.Exists(publicKeyPath))
        {
            throw SponsoredCatalogService.Error("file_exists", "公钥输出路径已经存在");
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateBytes = key.ExportPkcs8PrivateKey();
        var publicBytes = ExportPublicPoint(key);
        SponsoredCatalogService.WriteCreateNew(privateKeyPath, privateBytes, sensitive: true);
        try
        {
            var trust = new SponsoredCatalogTrustKey(
                SchemaVersion,
                keyId,
                SignatureAlgorithm,
                Convert.ToBase64String(publicBytes));
            var output = JsonSerializer.SerializeToUtf8Bytes(trust, JsonDefaults.Options);
            output = [.. output, (byte)'\n'];
            SponsoredCatalogService.WriteCreateNew(publicKeyPath, output, sensitive: false);
        }
        catch
        {
            try
            {
                File.Delete(privateKeyPath);
            }
            catch (IOException)
            {
                // 公钥发布失败时尽力删除本次新私钥；不覆盖原始错误。
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateBytes);
        }
    }

    /// <summary>
    /// 功能：验证 catalog 后用匹配私钥签署精确 UTF-8 字节并生成 envelope。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="catalogPath">有界严格 catalog JSON。</param>
    /// <param name="privateKeyPath">权限受限的 PKCS#8 私钥。</param>
    /// <param name="trustKeyPath">匹配公开 trust-key。</param>
    /// <param name="outputPath">必须不存在的 envelope 输出。</param>
    /// <remarks>输出只含 payload、公钥 ID 和签名，不包含私钥或凭据。</remarks>
    public static void Sign(
        string catalogPath,
        string privateKeyPath,
        string trustKeyPath,
        string outputPath)
    {
        var catalogBytes = SponsoredCatalogService.ReadBoundedFile(catalogPath, 256 * 1024, sensitive: false);
        _ = SponsoredCatalogService.ValidateCatalog(
            SponsoredCatalogService.ParseStrict<SponsoredProviderCatalog>(catalogBytes, "catalog"),
            DateTimeOffset.UtcNow);
        var privateBytes = SponsoredCatalogService.ReadBoundedFile(privateKeyPath, 16 * 1024, sensitive: true);
        try
        {
            var trust = SponsoredCatalogService.ParseStrict<SponsoredCatalogTrustKey>(
                SponsoredCatalogService.ReadBoundedFile(trustKeyPath, 16 * 1024, sensitive: false),
                "trust_key");
            var expectedPublic = SponsoredCatalogService.ValidateTrustKey(trust);
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(privateBytes, out var bytesRead);
            if (bytesRead != privateBytes.Length || !ExportPublicPoint(key).AsSpan().SequenceEqual(expectedPublic))
            {
                throw SponsoredCatalogService.Error("key_mismatch", "推广目录私钥与公钥不匹配");
            }

            var signature = key.SignData(
                catalogBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
            var envelope = new SponsoredCatalogService.CatalogEnvelope(
                SchemaVersion,
                Convert.ToBase64String(catalogBytes),
                new SponsoredCatalogService.CatalogSignature(
                    SignatureAlgorithm,
                    trust.KeyId,
                    Convert.ToBase64String(signature)));
            var output = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options);
            output = [.. output, (byte)'\n'];
            SponsoredCatalogService.WriteCreateNew(outputPath, output, sensitive: false);
        }
        catch (CryptographicException)
        {
            throw SponsoredCatalogService.Error("private_key", "推广目录私钥无效");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateBytes);
        }
    }

    /// <summary>
    /// 功能：离线验证待上传或已下载 envelope 与公开 trust-key。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="envelopePath">有界 signed envelope。</param>
    /// <param name="trustKeyPath">公开 trust-key JSON。</param>
    /// <returns>验签、时间、URL 和条目规则全部通过的规范化 catalog。</returns>
    /// <remarks>不访问网络、不读取私钥，也不注册可执行 Provider route。</remarks>
    public static SponsoredProviderCatalog Verify(string envelopePath, string trustKeyPath)
    {
        var envelope = SponsoredCatalogService.ReadBoundedFile(
            envelopePath,
            256 * 1024,
            sensitive: false);
        var trust = SponsoredCatalogService.ParseStrict<SponsoredCatalogTrustKey>(
            SponsoredCatalogService.ReadBoundedFile(trustKeyPath, 16 * 1024, sensitive: false),
            "trust_key");
        var publicKey = SponsoredCatalogService.ValidateTrustKey(trust);
        return SponsoredCatalogService.VerifyEnvelope(
            envelope,
            trust.KeyId,
            publicKey,
            DateTimeOffset.UtcNow).Catalog;
    }

    /// <summary>
    /// 功能：把 ECDsa 公钥导出为跨 Rust/.NET 一致的 65 字节 SEC1 uncompressed point。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="key">NIST P-256 key。</param>
    /// <returns>0x04、32 字节 X、32 字节 Y。</returns>
    private static byte[] ExportPublicPoint(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        if (parameters.Q.X is not { Length: 32 } x || parameters.Q.Y is not { Length: 32 } y)
        {
            throw SponsoredCatalogService.Error("public_key", "推广目录公钥形状无效");
        }

        var output = new byte[65];
        output[0] = 4;
        x.CopyTo(output, 1);
        y.CopyTo(output, 33);
        return output;
    }
}
