using System.Text.Json;
using QxnmForge.Domain;
using QxnmForge.Serialization;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：表示工作区外 CredentialStore 或本地推广 route 安全失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ProviderCommercialStateException : Exception
{
    /// <summary>
    /// 功能：创建不包含路径、endpoint 或 credential 的稳定商业状态异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">安全细分类。</param>
    /// <param name="message">可面向用户显示的中文消息。</param>
    internal ProviderCommercialStateException(string kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    /// <summary>
    /// 功能：取得不含敏感内容的稳定错误分类。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string Kind { get; }
}

/// <summary>
/// 功能：表示一个只存于本机敏感状态文件的 Provider API key。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ProviderId">品牌中立 Provider ID。</param>
/// <param name="ApiKey">不得进入日志、协议、Session 或 fixture 的 secret。</param>
internal sealed record StoredProviderCredential(string ProviderId, string ApiKey);

/// <summary>
/// 功能：定义严格 CredentialStore JSON 根对象。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="SchemaVersion">固定 0.1。</param>
/// <param name="Credentials">最多 128 个唯一 Provider credential。</param>
internal sealed record ProviderCredentialDocument(
    string SchemaVersion,
    IReadOnlyList<StoredProviderCredential> Credentials);

/// <summary>
/// 功能：表示不含 secret、可由 Rust 与 .NET 共同读取的本地推广 route 快照。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="EntryId">远程目录稳定 entry ID。</param>
/// <param name="ProviderId">固定 relay-entryId 本地身份。</param>
/// <param name="DisplayName">安装时固定的展示名称。</param>
/// <param name="ApiFamily">三种 allowlist family 之一。</param>
/// <param name="ApiBaseUrl">安装时固定的 HTTPS base。</param>
/// <param name="CatalogVersion">产生快照的签名目录版本。</param>
/// <param name="CommissionDisclosure">安装时固定的返佣披露。</param>
/// <param name="Models">本地显式模型 allowlist。</param>
/// <param name="InstalledAt">UTC RFC 3339 安装时间。</param>
public sealed record InstalledSponsoredRoute(
    string EntryId,
    string ProviderId,
    string DisplayName,
    string ApiFamily,
    string ApiBaseUrl,
    ulong CatalogVersion,
    string CommissionDisclosure,
    IReadOnlyList<string> Models,
    string InstalledAt);

/// <summary>
/// 功能：定义非敏感本地推广 route JSON 根对象。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="SchemaVersion">固定 0.1。</param>
/// <param name="Routes">最多 64 条本地确认 route。</param>
internal sealed record InstalledSponsoredRouteDocument(
    string SchemaVersion,
    IReadOnlyList<InstalledSponsoredRoute> Routes);

/// <summary>
/// 功能：提供工作区外、权限受限且按请求读取的文件型 Provider CredentialStore。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ProviderCredentialStore
{
    private const string SchemaVersion = "0.1";
    private const int MaximumDocumentBytes = 2 * 1024 * 1024;
    private readonly string root;

    /// <summary>
    /// 功能：创建或打开工作区外 CredentialStore，并建立私有目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="stateRoot">用户状态根。</param>
    /// <param name="workspace">当前 canonical workspace。</param>
    /// <remarks>不变量：CredentialStore 绝不能位于 workspace 内；Unix 目录权限固定 0700。</remarks>
    /// <exception cref="ProviderCommercialStateException">路径、目录、reparse 或边界不安全。</exception>
    public ProviderCredentialStore(string stateRoot, string workspace)
    {
        ArgumentException.ThrowIfNullOrEmpty(stateRoot);
        ArgumentException.ThrowIfNullOrEmpty(workspace);
        root = Path.GetFullPath(Path.Combine(stateRoot, "credentials"));
        CommercialFileSafety.CreatePrivateDirectory(root);
        CommercialFileSafety.RejectReparsePoint(root);
        var workspacePath = Path.GetFullPath(workspace);
        if (CommercialFileSafety.IsWithin(root, workspacePath))
        {
            throw Error("credential_workspace", "CredentialStore 必须位于 workspace 外");
        }
    }

    /// <summary>
    /// 功能：保存或轮换一个只从 stdin 获得的 Provider API key。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerId">合法品牌中立 Provider ID。</param>
    /// <param name="apiKey">非空、header-safe secret。</param>
    /// <remarks>不变量：Unix 文件 0600；返回值、异常和日志均不含 secret。</remarks>
    /// <exception cref="ProviderCommercialStateException">字段、锁、symlink、权限、JSON 或写入失败。</exception>
    public void Set(string providerId, string apiKey)
    {
        ValidateProviderId(providerId);
        ValidateApiKey(apiKey);
        using var stateLock = AcquireLock();
        var document = ReadDocumentUnlocked();
        var credentials = document.Credentials
            .Where(entry => !string.Equals(entry.ProviderId, providerId, StringComparison.Ordinal))
            .Append(new StoredProviderCredential(providerId, apiKey))
            .OrderBy(static entry => entry.ProviderId, StringComparer.Ordinal)
            .ToArray();
        WriteDocumentUnlocked(new ProviderCredentialDocument(SchemaVersion, credentials));
    }

    /// <summary>
    /// 功能：只列出拥有 stored credential 的 Provider ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>按 ordinal 升序且不含 API key 的数组。</returns>
    /// <exception cref="ProviderCommercialStateException">锁、权限或文档验证失败。</exception>
    public IReadOnlyList<string> List()
    {
        using var stateLock = AcquireLock();
        return ReadDocumentUnlocked().Credentials
            .Select(static entry => entry.ProviderId)
            .ToArray();
    }

    /// <summary>
    /// 功能：移除一个 stored credential，不返回旧 secret。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerId">目标 Provider ID。</param>
    /// <returns>存在并删除时 true，不存在时 false。</returns>
    /// <exception cref="ProviderCommercialStateException">字段、锁、权限、JSON 或写入失败。</exception>
    public bool Remove(string providerId)
    {
        ValidateProviderId(providerId);
        using var stateLock = AcquireLock();
        var document = ReadDocumentUnlocked();
        var credentials = document.Credentials
            .Where(entry => !string.Equals(entry.ProviderId, providerId, StringComparison.Ordinal))
            .ToArray();
        if (credentials.Length == document.Credentials.Count)
        {
            return false;
        }

        WriteDocumentUnlocked(new ProviderCredentialDocument(SchemaVersion, credentials));
        return true;
    }

    /// <summary>
    /// 功能：在最终 Provider request header 边界读取最新 stored credential。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerId">构造 route 时固定的 Provider ID。</param>
    /// <param name="credential">成功时仅由本次请求局部持有的 key。</param>
    /// <returns>整个 store 安全且目标存在时 true；任何失败为 false。</returns>
    /// <remarks>不变量：失败绝不回退环境变量，也不抛出包含文件细节的异常。</remarks>
    internal bool TryReadForRequest(string providerId, out string? credential)
    {
        credential = null;
        try
        {
            ValidateProviderId(providerId);
            using var stateLock = AcquireLock();
            credential = ReadDocumentUnlocked().Credentials
                .FirstOrDefault(entry => string.Equals(entry.ProviderId, providerId, StringComparison.Ordinal))
                ?.ApiKey;
            return credential is not null;
        }
        catch (ProviderCommercialStateException)
        {
            credential = null;
            return false;
        }
    }

    /// <summary>
    /// 功能：以 FileShare.None 获取跨进程非阻塞 writer lock。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>释放时解除锁的独占 FileStream。</returns>
    private FileStream AcquireLock()
    {
        var path = Path.Combine(root, "provider-credentials.lock");
        CommercialFileSafety.RejectReparsePointIfPresent(path);
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            CommercialFileSafety.RestrictFilePermissions(path);
            return stream;
        }
        catch (IOException)
        {
            throw Error("credential_locked", "CredentialStore 正被其他进程使用");
        }
        catch (UnauthorizedAccessException)
        {
            throw Error("credential_lock", "CredentialStore lock 打开失败");
        }
    }

    /// <summary>
    /// 功能：在独占锁内读取并验证完整 CredentialStore 文档。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>不存在时返回空 v0.1 文档。</returns>
    private ProviderCredentialDocument ReadDocumentUnlocked()
    {
        var path = Path.Combine(root, "provider-credentials.json");
        if (!File.Exists(path))
        {
            return new ProviderCredentialDocument(SchemaVersion, []);
        }

        var document = CommercialFileSafety.ParseStrict<ProviderCredentialDocument>(
            CommercialFileSafety.ReadBounded(path, MaximumDocumentBytes, sensitive: true),
            "credential_json");
        ValidateDocument(document);
        return document;
    }

    /// <summary>
    /// 功能：在独占锁内原子发布敏感 CredentialStore 文档。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="document">已排序的完整文档。</param>
    private void WriteDocumentUnlocked(ProviderCredentialDocument document)
    {
        ValidateDocument(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonDefaults.Options);
        CommercialFileSafety.AtomicWrite(
            Path.Combine(root, "provider-credentials.json"),
            bytes,
            sensitive: true);
    }

    /// <summary>
    /// 功能：验证版本、数量、唯一 Provider ID 和每个 API key 的 header 边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="document">严格 DTO。</param>
    private static void ValidateDocument(ProviderCredentialDocument document)
    {
        if (document.SchemaVersion != SchemaVersion || document.Credentials.Count > 128)
        {
            throw Error("credential_shape", "CredentialStore 文档无效");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Credentials)
        {
            ValidateProviderId(entry.ProviderId);
            ValidateApiKey(entry.ApiKey);
            if (!ids.Add(entry.ProviderId))
            {
                throw Error("credential_duplicate", "CredentialStore Provider ID 重复");
            }
        }
    }

    /// <summary>
    /// 功能：验证品牌中立 Provider ID 的受限 ASCII 语法。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选 ID。</param>
    internal static void ValidateProviderId(string value)
    {
        if (value.Length is < 1 or > 128 ||
            !IsLowerAlphaNumeric(value[0]) ||
            value.Any(static character => !IsLowerAlphaNumeric(character) && character != '-'))
        {
            throw Error("provider_id", "Provider ID 无效");
        }
    }

    /// <summary>
    /// 功能：验证 API key 非空、有界且不能注入 HTTP header。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">只存在调用方内存中的候选 key。</param>
    internal static void ValidateApiKey(string value)
    {
        if (value.Length is < 1 or > 16_384 || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw Error("credential_value", "Provider credential 无效");
        }
    }

    /// <summary>
    /// 功能：判断字符是否为 ASCII 小写字母或数字。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选字符。</param>
    /// <returns>属于 allowlist 时 true。</returns>
    private static bool IsLowerAlphaNumeric(char value)
    {
        return value is >= 'a' and <= 'z' or >= '0' and <= '9';
    }

    /// <summary>
    /// 功能：构造 CredentialStore 脱敏异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">稳定分类。</param>
    /// <param name="message">中文安全消息。</param>
    /// <returns>不含 secret 或路径的异常。</returns>
    private static ProviderCommercialStateException Error(string kind, string message)
    {
        return new ProviderCommercialStateException(kind, message);
    }
}

/// <summary>
/// 功能：管理不含 secret 的已安装推广 Provider route。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class InstalledSponsoredRouteStore
{
    private const string SchemaVersion = "0.1";
    private const int MaximumDocumentBytes = 512 * 1024;
    private readonly string root;

    /// <summary>
    /// 功能：创建指向用户状态根固定 commercial 子目录的 route store。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="stateRoot">用户状态根。</param>
    public InstalledSponsoredRouteStore(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(stateRoot);
        root = Path.GetFullPath(Path.Combine(stateRoot, "commercial"));
    }

    /// <summary>
    /// 功能：把已验签 catalog 中一个条目明确固定为本地可执行 route。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="catalog">当前已验证目录。</param>
    /// <param name="entryId">用户选择的 entry ID。</param>
    /// <param name="modelId">用户显式选择的模型。</param>
    /// <returns>新安装或显式替换后的非敏感快照。</returns>
    /// <remarks>不变量：providerId 固定为 relay-entryId；不保存 signup URL 或 credential。</remarks>
    public InstalledSponsoredRoute Install(
        SponsoredProviderCatalog catalog,
        string entryId,
        string modelId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var entry = catalog.Entries.FirstOrDefault(
            item => string.Equals(item.Id, entryId, StringComparison.Ordinal))
            ?? throw Error("entry_missing", "推广目录中没有该条目");
        ValidateModel(modelId);
        var route = new InstalledSponsoredRoute(
            entry.Id,
            "relay-" + entry.Id,
            entry.DisplayName,
            entry.ApiFamily,
            entry.ApiBaseUrl,
            catalog.CatalogVersion,
            entry.CommissionDisclosure,
            [modelId],
            DateTimeOffset.UtcNow.UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                System.Globalization.CultureInfo.InvariantCulture));
        ValidateRoute(route);
        CommercialFileSafety.CreatePrivateDirectory(root);
        using var stateLock = AcquireLock();
        var routes = ReadDocumentUnlocked().Routes
            .Where(item => !string.Equals(item.EntryId, entryId, StringComparison.Ordinal))
            .Append(route)
            .OrderBy(static item => item.EntryId, StringComparer.Ordinal)
            .ToArray();
        WriteDocumentUnlocked(new InstalledSponsoredRouteDocument(SchemaVersion, routes));
        return route;
    }

    /// <summary>
    /// 功能：列出全部本地固定且不含 secret 的推广 route。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>按 entryId 升序的防御性数组。</returns>
    public IReadOnlyList<InstalledSponsoredRoute> List()
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        using var stateLock = AcquireLock();
        return ReadDocumentUnlocked().Routes.ToArray();
    }

    /// <summary>
    /// 功能：移除一个本地推广 route，credential 保持独立不自动删除。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="entryId">远程 entry ID。</param>
    /// <returns>存在并删除时 true。</returns>
    public bool Remove(string entryId)
    {
        ValidateEntryId(entryId);
        CommercialFileSafety.CreatePrivateDirectory(root);
        using var stateLock = AcquireLock();
        var document = ReadDocumentUnlocked();
        var routes = document.Routes
            .Where(route => !string.Equals(route.EntryId, entryId, StringComparison.Ordinal))
            .ToArray();
        if (routes.Length == document.Routes.Count)
        {
            return false;
        }

        WriteDocumentUnlocked(new InstalledSponsoredRouteDocument(SchemaVersion, routes));
        return true;
    }

    /// <summary>
    /// 功能：为 CLI provider/model 选择解析本地推广 API family。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerId">本地 Provider ID。</param>
    /// <param name="modelId">用户选择模型。</param>
    /// <returns>精确命中时的 family，否则 null。</returns>
    public string? ResolveFamily(string providerId, string modelId)
    {
        return List().FirstOrDefault(route =>
            string.Equals(route.ProviderId, providerId, StringComparison.Ordinal) &&
            route.Models.Contains(modelId, StringComparer.Ordinal))?.ApiFamily;
    }

    /// <summary>
    /// 功能：取得跨语言非敏感安装文档的固定路径。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string DocumentPath => Path.Combine(root, "installed-sponsored-routes.json");

    /// <summary>
    /// 功能：获取非敏感 route 文档的跨进程独占锁。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>释放时解除锁的 FileStream。</returns>
    private FileStream AcquireLock()
    {
        var path = Path.Combine(root, "installed-sponsored-routes.lock");
        CommercialFileSafety.RejectReparsePointIfPresent(path);
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            CommercialFileSafety.RestrictFilePermissions(path);
            return stream;
        }
        catch (IOException)
        {
            throw Error("route_locked", "推广 route 正被其他进程使用");
        }
        catch (UnauthorizedAccessException)
        {
            throw Error("route_lock", "推广 route lock 打开失败");
        }
    }

    /// <summary>
    /// 功能：在独占锁内读取并验证完整 route 文档。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>不存在时返回空 v0.1 文档。</returns>
    private InstalledSponsoredRouteDocument ReadDocumentUnlocked()
    {
        if (!File.Exists(DocumentPath))
        {
            return new InstalledSponsoredRouteDocument(SchemaVersion, []);
        }

        var document = CommercialFileSafety.ParseStrict<InstalledSponsoredRouteDocument>(
            CommercialFileSafety.ReadBounded(DocumentPath, MaximumDocumentBytes, sensitive: false),
            "route_json");
        ValidateDocument(document);
        return document;
    }

    /// <summary>
    /// 功能：在独占锁内原子发布非敏感 route 文档。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="document">完整排序文档。</param>
    private void WriteDocumentUnlocked(InstalledSponsoredRouteDocument document)
    {
        ValidateDocument(document);
        CommercialFileSafety.AtomicWrite(
            DocumentPath,
            JsonSerializer.SerializeToUtf8Bytes(document, JsonDefaults.Options),
            sensitive: false);
    }

    /// <summary>
    /// 功能：验证安装文档版本、数量及 entry/provider 唯一性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="document">严格 DTO。</param>
    private static void ValidateDocument(InstalledSponsoredRouteDocument document)
    {
        if (document.SchemaVersion != SchemaVersion || document.Routes.Count > 64)
        {
            throw Error("route_shape", "已安装推广 route 文档无效");
        }

        var entries = new HashSet<string>(StringComparer.Ordinal);
        var providers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in document.Routes)
        {
            ValidateRoute(route);
            if (!entries.Add(route.EntryId) || !providers.Add(route.ProviderId))
            {
                throw Error("route_duplicate", "已安装推广 route 身份重复");
            }
        }
    }

    /// <summary>
    /// 功能：验证单条 route 的身份、HTTPS、family、模型、时间和披露边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="route">候选 route。</param>
    internal static void ValidateRoute(InstalledSponsoredRoute route)
    {
        ValidateEntryId(route.EntryId);
        ProviderCredentialStore.ValidateProviderId(route.ProviderId);
        if (route.ProviderId != "relay-" + route.EntryId ||
            route.CatalogVersion is 0 or > 9_007_199_254_740_991 ||
            route.Models.Count is < 1 or > 32 ||
            route.ApiFamily is not ("openai-completions" or "openai-responses" or "anthropic-messages") ||
            !TryValidateHttpsBase(route.ApiBaseUrl) ||
            !TryValidateText(route.DisplayName, 80) ||
            !TryValidateText(route.CommissionDisclosure, 240) ||
            !route.InstalledAt.EndsWith('Z') ||
            !DateTimeOffset.TryParse(
                route.InstalledAt,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out _))
        {
            throw Error("route_field", "已安装推广 route 字段无效");
        }

        var models = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in route.Models)
        {
            ValidateModel(model);
            if (!models.Add(model))
            {
                throw Error("route_model", "已安装推广 route 模型重复");
            }
        }
    }

    /// <summary>
    /// 功能：把本地 route 转换为 registry 使用的公开文本模型 descriptors。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="route">已验证 route。</param>
    /// <returns>与模型 allowlist 一一对应的 descriptor。</returns>
    internal static IReadOnlyList<ModelDescriptor> CreateModelDescriptors(InstalledSponsoredRoute route)
    {
        return route.Models.Select(model => new ModelDescriptor(
            route.ProviderId,
            model,
            route.DisplayName + " / " + model,
            route.ApiFamily,
            new ModelCapabilities(
                ["text"],
                ["text"],
                Streaming: true,
                Tools: true,
                Reasoning: false))).ToArray();
    }

    /// <summary>
    /// 功能：验证 entry ID 的小写 ASCII 语法和 64 字节上限。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选 entry ID。</param>
    private static void ValidateEntryId(string value)
    {
        if (value.Length > 64)
        {
            throw Error("entry_id", "推广 entry ID 无效");
        }

        ProviderCredentialStore.ValidateProviderId(value);
    }

    /// <summary>
    /// 功能：验证模型 ID 非空、有界且没有控制字符。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">用户选择模型。</param>
    private static void ValidateModel(string value)
    {
        if (value.Length is < 1 or > 256 || value.Any(char.IsControl))
        {
            throw Error("model_id", "Provider model ID 无效");
        }
    }

    /// <summary>
    /// 功能：判断公开展示文本非空、有界且没有终端控制字符。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选文本。</param>
    /// <param name="maximum">最大字符数。</param>
    /// <returns>安全时 true。</returns>
    private static bool TryValidateText(string value, int maximum)
    {
        return value.Length is > 0 && value.Length <= maximum && !value.Any(char.IsControl);
    }

    /// <summary>
    /// 功能：判断 API base 是无 userinfo/query/fragment 的 HTTPS URL。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选 URL。</param>
    /// <returns>满足安全边界时 true。</returns>
    private static bool TryValidateHttpsBase(string value)
    {
        return value.Length <= 2048 &&
            Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            !string.IsNullOrEmpty(uri.Host) &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment);
    }

    /// <summary>
    /// 功能：构造本地 route 脱敏异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">稳定分类。</param>
    /// <param name="message">中文安全消息。</param>
    /// <returns>不含 endpoint、路径或 secret 的异常。</returns>
    private static ProviderCommercialStateException Error(string kind, string message)
    {
        return new ProviderCommercialStateException(kind, message);
    }
}

/// <summary>
/// 功能：集中实现商业状态文件的 reparse、权限、有界 JSON 与原子更新安全边界。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal static class CommercialFileSafety
{
    /// <summary>
    /// 功能：创建私有目录，并在 Unix 把 mode 固定为 0700。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">固定本地状态目录。</param>
    internal static void CreatePrivateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Error("directory", "商业状态目录创建失败");
        }
    }

    /// <summary>
    /// 功能：拒绝已经存在的 reparse/symlink 路径。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">必须存在的文件或目录。</param>
    internal static void RejectReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw Error("reparse", "商业状态路径不能是 reparse point");
            }
        }
        catch (ProviderCommercialStateException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Error("metadata", "商业状态路径元数据读取失败");
        }
    }

    /// <summary>
    /// 功能：路径存在时拒绝 reparse/symlink，缺失时允许后续 create-new。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">候选文件路径。</param>
    internal static void RejectReparsePointIfPresent(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            RejectReparsePoint(path);
        }
    }

    /// <summary>
    /// 功能：判断 candidate 等于 root 或位于 root 的目录边界内。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="candidate">绝对候选路径。</param>
    /// <param name="root">绝对边界根。</param>
    /// <returns>位于边界内时 true。</returns>
    internal static bool IsWithin(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        return string.Equals(Path.TrimEndingDirectorySeparator(candidate), normalizedRoot, comparison) ||
            candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// 功能：在 Unix 将普通状态文件权限固定为 0600。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">已创建普通文件。</param>
    internal static void RestrictFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// 功能：no-reparse、有界读取普通状态文件，并验证敏感 Unix mode。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">固定文档路径。</param>
    /// <param name="maximum">最大字节数。</param>
    /// <param name="sensitive">是否要求 group/other 权限全为零。</param>
    /// <returns>完整文档字节。</returns>
    internal static byte[] ReadBounded(string path, int maximum, bool sensitive)
    {
        RejectReparsePoint(path);
        try
        {
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
                    throw Error("file_permissions", "CredentialStore 文件权限过宽");
                }
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 || stream.Length > maximum)
            {
                throw Error("file_shape", "商业状态文件形状无效");
            }

            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (ProviderCommercialStateException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            throw Error("file_read", "商业状态文件读取失败");
        }
    }

    /// <summary>
    /// 功能：严格解析 UTF-8 JSON，递归拒绝重复 key、未知成员和 trailing data。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <typeparam name="T">关闭未知成员的目标 DTO。</typeparam>
    /// <param name="bytes">有界完整文档。</param>
    /// <param name="kind">安全错误分类。</param>
    /// <returns>严格 DTO。</returns>
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
                ?? throw Error(kind, "商业状态 JSON 根无效");
        }
        catch (ProviderCommercialStateException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw Error(kind, "商业状态 JSON 或字段无效");
        }
    }

    /// <summary>
    /// 功能：用同目录 create-new 临时文件、flush-to-disk 和 replace 发布文档。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">固定目标路径。</param>
    /// <param name="bytes">完整 JSON 字节。</param>
    /// <param name="sensitive">保留用于区分安全审计；所有 v0.1 状态文件均使用 0600。</param>
    internal static void AtomicWrite(string path, byte[] bytes, bool sensitive)
    {
        _ = sensitive;
        var parent = Path.GetDirectoryName(path) ?? throw Error("file_path", "商业状态文件路径无效");
        CreatePrivateDirectory(parent);
        RejectReparsePointIfPresent(path);
        var temporary = Path.Combine(parent, ".commercial-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                RestrictFilePermissions(temporary);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Error("file_publish", "商业状态文件原子发布失败");
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 临时文件尽力清理；原始写入失败分类保持不变。
            }
        }
    }

    /// <summary>
    /// 功能：以 create-new、Unix 0600 和 flush-to-disk 写入推广目录密钥等敏感文件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">必须尚不存在的输出路径。</param>
    /// <param name="bytes">完整输出字节。</param>
    internal static void WriteCreateNewSecure(string path, byte[] bytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            RestrictFilePermissions(path);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Error("file_create", "商业敏感输出文件创建失败");
        }
    }

    /// <summary>
    /// 功能：递归拒绝任意 JSON object 的大小写敏感重复属性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">当前 JSON 节点。</param>
    private static void ValidateNoDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Error("duplicate_key", "商业状态 JSON 包含重复 key");
                }

                ValidateNoDuplicateKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                ValidateNoDuplicateKeys(item);
            }
        }
    }

    /// <summary>
    /// 功能：构造文件安全层脱敏异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">稳定分类。</param>
    /// <param name="message">中文安全消息。</param>
    /// <returns>不含路径、URL 或 secret 的异常。</returns>
    private static ProviderCommercialStateException Error(string kind, string message)
    {
        return new ProviderCommercialStateException(kind, message);
    }
}
