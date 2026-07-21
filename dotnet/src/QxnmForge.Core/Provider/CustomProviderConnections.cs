using System.Net;
using System.Text;
using System.Text.Json;
using QxnmForge.Domain;
using QxnmForge.Serialization;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：表示创建或完整更新自定义 Provider 连接的非敏感输入。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="DisplayName">长度 1..64 的用户可见名称。</param>
/// <param name="ProviderId">连接间唯一的品牌中立安全 ID。</param>
/// <param name="ApiFamily">当前固定为 openai-completions。</param>
/// <param name="BaseUrl">无认证信息、query 或 fragment 的 HTTPS base URL。</param>
/// <param name="ModelIds">0..512 个唯一模型 ID；为空时连接尚不可执行。</param>
/// <param name="LogoAssetId">可选的本地公开 Logo 资源安全 ID。</param>
/// <param name="Enabled">是否允许下次 daemon 启动注册该连接。</param>
public sealed record ProviderConnectionInput(
    string DisplayName,
    string ProviderId,
    string ApiFamily,
    string BaseUrl,
    IReadOnlyList<string> ModelIds,
    string? LogoAssetId,
    bool Enabled);

/// <summary>
/// 功能：表示持久化的非敏感自定义 Provider 连接配置。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ConnectionId">服务签发的稳定安全 ID。</param>
/// <param name="Revision">从 1 开始递增的安全整数 CAS revision。</param>
/// <param name="DisplayName">用户可见名称。</param>
/// <param name="ProviderId">连接间唯一的品牌中立 Provider ID。</param>
/// <param name="ApiFamily">固定 openai-completions。</param>
/// <param name="BaseUrl">经验证的 API base URL。</param>
/// <param name="ModelIds">显式模型 allowlist。</param>
/// <param name="LogoAssetId">可选本地公开 Logo 资源 ID。</param>
/// <param name="Enabled">是否允许下次启动注册。</param>
/// <param name="CreatedAt">UTC 创建时间。</param>
/// <param name="UpdatedAt">UTC 最近更新时间。</param>
public sealed record ProviderConnectionConfiguration(
    string ConnectionId,
    long Revision,
    string DisplayName,
    string ProviderId,
    string ApiFamily,
    string BaseUrl,
    IReadOnlyList<string> ModelIds,
    string? LogoAssetId,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// 功能：表示可由 application service 返回的脱敏 Provider 连接投影。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ConnectionId">稳定连接 ID。</param>
/// <param name="Revision">当前 CAS revision。</param>
/// <param name="DisplayName">用户可见名称。</param>
/// <param name="ProviderId">品牌中立 Provider ID。</param>
/// <param name="ApiFamily">固定 openai-completions。</param>
/// <param name="BaseUrl">公开 API base URL。</param>
/// <param name="ModelIds">显式模型 allowlist。</param>
/// <param name="LogoAssetId">可选本地公开 Logo 资源 ID。</param>
/// <param name="Enabled">是否允许启动注册。</param>
/// <param name="CredentialConfigured">CredentialStore 是否包含该 Provider ID。</param>
/// <param name="CreatedAt">UTC 创建时间。</param>
/// <param name="UpdatedAt">UTC 最近更新时间。</param>
public sealed record ProviderConnection(
    string ConnectionId,
    long Revision,
    string DisplayName,
    string ProviderId,
    string ApiFamily,
    string BaseUrl,
    IReadOnlyList<string> ModelIds,
    string? LogoAssetId,
    bool Enabled,
    bool CredentialConfigured,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// 功能：定义严格且不含 secret 的 Provider 连接 JSON 根对象。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="SchemaVersion">固定 0.1。</param>
/// <param name="Connections">最多 128 条非敏感连接。</param>
internal sealed record ProviderConnectionDocument(
    string SchemaVersion,
    IReadOnlyList<ProviderConnectionConfiguration> Connections);

/// <summary>
/// 功能：以 portable error 表示 Provider 连接配置、CAS 或存储失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ProviderConnectionException : Exception
{
    /// <summary>
    /// 功能：从不含 URL、路径或 credential 的 portable error 创建异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="error">可直接返回协议边界的脱敏错误。</param>
    public ProviderConnectionException(PortableError error)
        : base(error.Message)
    {
        Error = error;
    }

    /// <summary>
    /// 功能：取得已脱敏的 portable error。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public PortableError Error { get; }

    /// <summary>
    /// 功能：创建字段或文档校验失败错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="field">可安全公开的字段名；存储文档失败时省略。</param>
    /// <returns>固定 invalid_params 错误。</returns>
    internal static ProviderConnectionException Invalid(string? field = null)
    {
        return new ProviderConnectionException(new PortableError(
            -32602,
            "provider connection is invalid",
            false,
            new ErrorDetails("invalid_params", Field: field)));
    }

    /// <summary>
    /// 功能：创建连接不存在错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connectionId">已验证连接 ID。</param>
    /// <returns>固定 provider_connection_not_found 错误。</returns>
    internal static ProviderConnectionException NotFound(string connectionId)
    {
        return new ProviderConnectionException(new PortableError(
            -32602,
            "provider connection was not found",
            false,
            new ErrorDetails("provider_connection_not_found", ResourceId: connectionId)));
    }

    /// <summary>
    /// 功能：创建 Provider ID 唯一性冲突错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerId">发生冲突的公开 Provider ID。</param>
    /// <returns>可在刷新列表后重试的 provider_id_conflict 错误。</returns>
    internal static ProviderConnectionException ProviderIdConflict(string providerId)
    {
        return new ProviderConnectionException(new PortableError(
            -32010,
            "provider id is already configured",
            true,
            new ErrorDetails("provider_id_conflict", ProviderId: providerId)));
    }

    /// <summary>
    /// 功能：创建 revision CAS 冲突错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>可在刷新列表后重试的冲突错误。</returns>
    internal static ProviderConnectionException RevisionConflict()
    {
        return new ProviderConnectionException(new PortableError(
            -32010,
            "provider connection revision is stale",
            true,
            new ErrorDetails("provider_connection_revision_conflict")));
    }

    /// <summary>
    /// 功能：创建不泄漏 endpoint、credential 或远端正文的模型发现失败错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="retryable">传输或远端临时失败时为 true，目录或凭据无效时为 false。</param>
    /// <returns>固定 provider_model_discovery_failed 错误。</returns>
    internal static ProviderConnectionException DiscoveryFailed(bool retryable)
    {
        return new ProviderConnectionException(new PortableError(
            -32005,
            "provider model discovery failed",
            retryable,
            new ErrorDetails("provider_model_discovery_failed")));
    }

    /// <summary>
    /// 功能：把底层文件异常收敛为固定的连接存储错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">provider_connection_store_invalid、locked 或 io。</param>
    /// <param name="retryable">是否适合稍后重试。</param>
    /// <returns>不包含状态路径或 JSON 正文的错误。</returns>
    internal static ProviderConnectionException Store(string kind, bool retryable)
    {
        return new ProviderConnectionException(new PortableError(
            retryable ? -32002 : -32603,
            "provider connection storage is unavailable",
            retryable,
            new ErrorDetails(kind)));
    }
}

/// <summary>
/// 功能：在 state root 固定子目录中持久化严格、有界、原子且不含 secret 的 Provider 连接。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class CustomProviderConnectionStore
{
    private const string SchemaVersion = "0.1";
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private const int MaximumDocumentBytes = 2 * 1024 * 1024;
    private const int MaximumConnections = 128;
    private readonly bool allowLoopbackHttp;
    private readonly string root;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// 功能：创建 state root 下固定的非敏感 Provider 连接存储。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="stateRoot">应用状态根，不能由连接输入控制。</param>
    /// <param name="allowLoopbackHttp">仅 conformance 模式允许 literal loopback HTTP。</param>
    /// <param name="timeProvider">可选可信 UTC 时钟。</param>
    /// <remarks>不变量：文档固定在 stateRoot/provider-connections；目录拒绝 reparse point。</remarks>
    /// <exception cref="ProviderConnectionException">状态路径不安全或不能建立。</exception>
    public CustomProviderConnectionStore(
        string stateRoot,
        bool allowLoopbackHttp = false,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(stateRoot);
        this.allowLoopbackHttp = allowLoopbackHttp;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        var canonicalStateRoot = Path.GetFullPath(stateRoot);
        try
        {
            CommercialFileSafety.CreatePrivateDirectory(canonicalStateRoot);
            CommercialFileSafety.RejectReparsePoint(canonicalStateRoot);
            root = Path.GetFullPath(Path.Combine(canonicalStateRoot, "provider-connections"));
            if (!CommercialFileSafety.IsWithin(root, canonicalStateRoot))
            {
                throw ProviderConnectionException.Store("provider_connection_store_invalid", false);
            }

            CommercialFileSafety.CreatePrivateDirectory(root);
            CommercialFileSafety.RejectReparsePoint(root);
        }
        catch (ProviderCommercialStateException exception)
        {
            throw MapCommercialException(exception);
        }
    }

    /// <summary>
    /// 功能：读取全部非敏感连接配置。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>按 connectionId 排序的防御性快照。</returns>
    /// <exception cref="ProviderConnectionException">锁、权限、JSON 或文档边界失败。</exception>
    public IReadOnlyList<ProviderConnectionConfiguration> List()
    {
        try
        {
            using var stateLock = AcquireLock();
            return ReadDocumentUnlocked().Connections.Select(Clone).ToArray();
        }
        catch (ProviderCommercialStateException exception)
        {
            throw MapCommercialException(exception);
        }
    }

    /// <summary>
    /// 功能：以 connection ID 和 expected revision 读取模型发现使用的精确防御性快照。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connectionId">服务签发的连接 ID。</param>
    /// <param name="expectedRevision">用户确认的当前 revision。</param>
    /// <returns>网络请求前固定的非敏感连接快照。</returns>
    /// <remarks>不变量：本方法不读取 credential，也不在等待远端期间持有连接锁。</remarks>
    /// <exception cref="ProviderConnectionException">连接缺失、CAS 冲突或存储不可用。</exception>
    internal ProviderConnectionConfiguration ReadExact(
        string connectionId,
        long expectedRevision)
    {
        ValidateSafeId(connectionId, "connectionId");
        ValidateExpectedRevision(expectedRevision);
        try
        {
            using var stateLock = AcquireLock();
            var current = ReadDocumentUnlocked().Connections.FirstOrDefault(connection =>
                string.Equals(connection.ConnectionId, connectionId, StringComparison.Ordinal));
            if (current is null)
            {
                throw ProviderConnectionException.NotFound(connectionId);
            }

            if (current.Revision != expectedRevision)
            {
                throw ProviderConnectionException.RevisionConflict();
            }

            return Clone(current);
        }
        catch (ProviderCommercialStateException exception)
        {
            throw MapCommercialException(exception);
        }
    }

    /// <summary>
    /// 功能：创建 revision 1 的唯一 Provider 连接。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="input">完整非敏感连接输入。</param>
    /// <returns>原子发布后的连接配置。</returns>
    /// <exception cref="ProviderConnectionException">字段、数量、唯一性、锁或存储失败。</exception>
    public ProviderConnectionConfiguration Create(ProviderConnectionInput input)
    {
        return CreateCore(input, beforePublish: null);
    }

    /// <summary>
    /// 功能：在连接 writer lock 内验证创建输入、清理目标 ID orphan credential，再发布连接。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="input">完整非敏感连接输入。</param>
    /// <param name="beforePublish">全部连接校验成功后、发布前收到目标 Provider ID。</param>
    /// <returns>原子发布后的连接配置。</returns>
    /// <remarks>发布失败时最多留下无连接且无历史 credential 的安全状态，绝不把旧 secret 绑定新 endpoint。</remarks>
    internal ProviderConnectionConfiguration CreateWithCredentialCleanup(
        ProviderConnectionInput input,
        Action<string> beforePublish)
    {
        ArgumentNullException.ThrowIfNull(beforePublish);
        return CreateCore(input, beforePublish);
    }

    /// <summary>
    /// 功能：在单个连接 writer lock 内完成可选 credential 清理与连接创建。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="input">完整非敏感连接输入。</param>
    /// <param name="beforePublish">校验成功后、配置发布前的安全清理回调。</param>
    /// <returns>原子发布后的连接配置。</returns>
    private ProviderConnectionConfiguration CreateCore(
        ProviderConnectionInput input,
        Action<string>? beforePublish)
    {
        ValidateInput(input, allowLoopbackHttp);
        try
        {
            using var stateLock = AcquireLock();
            var document = ReadDocumentUnlocked();
            if (document.Connections.Count >= MaximumConnections)
            {
                throw ProviderConnectionException.Invalid("connection");
            }

            if (document.Connections.Any(connection =>
                    string.Equals(connection.ProviderId, input.ProviderId, StringComparison.Ordinal)))
            {
                throw ProviderConnectionException.ProviderIdConflict(input.ProviderId);
            }

            beforePublish?.Invoke(input.ProviderId);
            var now = GetUtcNow();
            var connection = new ProviderConnectionConfiguration(
                "connection-" + Guid.NewGuid().ToString("N"),
                1,
                input.DisplayName,
                input.ProviderId,
                input.ApiFamily,
                input.BaseUrl,
                input.ModelIds.ToArray(),
                input.LogoAssetId,
                input.Enabled,
                now,
                now);
            var connections = document.Connections
                .Append(connection)
                .OrderBy(static item => item.ConnectionId, StringComparer.Ordinal)
                .ToArray();
            WriteDocumentUnlocked(new ProviderConnectionDocument(SchemaVersion, connections));
            return Clone(connection);
        }
        catch (ProviderCommercialStateException exception)
        {
            throw MapCommercialException(exception);
        }
    }

    /// <summary>
    /// 功能：以 expected revision 完整替换非敏感连接配置。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connectionId">服务签发的连接 ID。</param>
    /// <param name="expectedRevision">客户端读取的当前 revision。</param>
    /// <param name="input">完整替换输入。</param>
    /// <returns>revision 加一并原子发布后的配置。</returns>
    /// <exception cref="ProviderConnectionException">连接缺失、CAS/Provider ID 冲突、字段或存储失败。</exception>
    public ProviderConnectionConfiguration Update(
        string connectionId,
        long expectedRevision,
        ProviderConnectionInput input)
    {
        return UpdateCore(connectionId, expectedRevision, input, beforePublish: null);
    }

    /// <summary>
    /// 功能：在连接 writer lock 内完成 CAS 后执行 Provider ID 迁移清理，再发布新配置。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connectionId">服务签发的连接 ID。</param>
    /// <param name="expectedRevision">客户端读取的当前 revision。</param>
    /// <param name="input">完整替换输入。</param>
    /// <param name="beforePublish">仅在 Provider ID 改变时、发布前收到旧 ID 和新 ID。</param>
    /// <returns>revision 加一并原子发布后的配置。</returns>
    internal ProviderConnectionConfiguration UpdateWithProviderTransition(
        string connectionId,
        long expectedRevision,
        ProviderConnectionInput input,
        Action<string, string> beforePublish)
    {
        ArgumentNullException.ThrowIfNull(beforePublish);
        return UpdateCore(connectionId, expectedRevision, input, beforePublish);
    }

    /// <summary>
    /// 功能：在单个连接 writer lock 内完成验证、可选迁移清理与原子更新。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connectionId">服务签发的连接 ID。</param>
    /// <param name="expectedRevision">客户端读取的当前 revision。</param>
    /// <param name="input">完整替换输入。</param>
    /// <param name="beforePublish">Provider ID 改变时的安全清理回调。</param>
    /// <returns>发布后的连接配置。</returns>
    private ProviderConnectionConfiguration UpdateCore(
        string connectionId,
        long expectedRevision,
        ProviderConnectionInput input,
        Action<string, string>? beforePublish)
    {
        ValidateSafeId(connectionId, "connectionId");
        ValidateExpectedRevision(expectedRevision);
        ValidateInput(input, allowLoopbackHttp);
        try
        {
            using var stateLock = AcquireLock();
            var document = ReadDocumentUnlocked();
            var current = document.Connections.FirstOrDefault(connection =>
                string.Equals(connection.ConnectionId, connectionId, StringComparison.Ordinal));
            if (current is null)
            {
                throw ProviderConnectionException.NotFound(connectionId);
            }

            if (current.Revision != expectedRevision)
            {
                throw ProviderConnectionException.RevisionConflict();
            }

            if (current.Revision >= MaxSafeInteger)
            {
                throw ProviderConnectionException.Invalid("expectedRevision");
            }

            if (document.Connections.Any(connection =>
                    !string.Equals(connection.ConnectionId, connectionId, StringComparison.Ordinal) &&
                    string.Equals(connection.ProviderId, input.ProviderId, StringComparison.Ordinal)))
            {
                throw ProviderConnectionException.ProviderIdConflict(input.ProviderId);
            }

            if (beforePublish is not null &&
                !string.Equals(current.ProviderId, input.ProviderId, StringComparison.Ordinal))
            {
                beforePublish(current.ProviderId, input.ProviderId);
            }

            var updatedAt = GetUtcNow();
            if (updatedAt < current.UpdatedAt)
            {
                updatedAt = current.UpdatedAt;
            }

            var updated = new ProviderConnectionConfiguration(
                current.ConnectionId,
                current.Revision + 1,
                input.DisplayName,
                input.ProviderId,
                input.ApiFamily,
                input.BaseUrl,
                input.ModelIds.ToArray(),
                input.LogoAssetId,
                input.Enabled,
                current.CreatedAt,
                updatedAt);
            var connections = document.Connections
                .Select(connection => connection.ConnectionId == connectionId ? updated : connection)
                .OrderBy(static item => item.ConnectionId, StringComparer.Ordinal)
                .ToArray();
            WriteDocumentUnlocked(new ProviderConnectionDocument(SchemaVersion, connections));
            return Clone(updated);
        }
        catch (ProviderCommercialStateException exception)
        {
            throw MapCommercialException(exception);
        }
    }

    /// <summary>
    /// 功能：在远端发现完成后以原 revision CAS 仅发布新的模型 allowlist。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connectionId">服务签发的连接 ID。</param>
    /// <param name="expectedRevision">网络请求前读取的 revision。</param>
    /// <param name="modelIds">已严格解析、去重并按 ordinal 排序的非空模型集合。</param>
    /// <returns>revision 精确加一并 durable 发布后的连接配置。</returns>
    /// <remarks>不变量：显示名、Provider ID、family、base URL、Logo、enabled 与创建时间保持不变。</remarks>
    /// <exception cref="ProviderConnectionException">模型无效、连接缺失、CAS 冲突或存储失败。</exception>
    internal ProviderConnectionConfiguration ReplaceDiscoveredModels(
        string connectionId,
        long expectedRevision,
        IReadOnlyList<string> modelIds)
    {
        ValidateSafeId(connectionId, "connectionId");
        ValidateExpectedRevision(expectedRevision);
        var replacement = modelIds?.ToArray()
            ?? throw ProviderConnectionException.Invalid("modelIds");
        ValidateModelIds(replacement, allowEmpty: false);
        try
        {
            using var stateLock = AcquireLock();
            var document = ReadDocumentUnlocked();
            var current = document.Connections.FirstOrDefault(connection =>
                string.Equals(connection.ConnectionId, connectionId, StringComparison.Ordinal));
            if (current is null)
            {
                throw ProviderConnectionException.NotFound(connectionId);
            }

            if (current.Revision != expectedRevision)
            {
                throw ProviderConnectionException.RevisionConflict();
            }

            if (current.Revision >= MaxSafeInteger)
            {
                throw ProviderConnectionException.Invalid("expectedRevision");
            }

            var updatedAt = GetUtcNow();
            if (updatedAt < current.UpdatedAt)
            {
                updatedAt = current.UpdatedAt;
            }

            var updated = current with
            {
                Revision = current.Revision + 1,
                ModelIds = replacement,
                UpdatedAt = updatedAt,
            };
            var connections = document.Connections
                .Select(connection => connection.ConnectionId == connectionId ? updated : connection)
                .OrderBy(static item => item.ConnectionId, StringComparer.Ordinal)
                .ToArray();
            WriteDocumentUnlocked(new ProviderConnectionDocument(SchemaVersion, connections));
            return Clone(updated);
        }
        catch (ProviderCommercialStateException exception)
        {
            throw MapCommercialException(exception);
        }
    }

    /// <summary>
    /// 功能：以 expected revision 原子删除连接配置并返回其 Provider ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connectionId">服务签发的连接 ID。</param>
    /// <param name="expectedRevision">客户端读取的当前 revision。</param>
    /// <returns>供服务清理独立 CredentialStore 的公开 Provider ID。</returns>
    /// <exception cref="ProviderConnectionException">连接缺失、CAS 冲突或存储失败。</exception>
    public string Delete(string connectionId, long expectedRevision)
    {
        return DeleteCore(connectionId, expectedRevision, beforePublish: null);
    }

    /// <summary>
    /// 功能：在连接 writer lock 内完成 CAS 后执行 credential 清理，再发布删除。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connectionId">服务签发的连接 ID。</param>
    /// <param name="expectedRevision">客户端读取的当前 revision。</param>
    /// <param name="beforePublish">CAS 成功后、连接发布删除前收到 Provider ID。</param>
    /// <returns>已删除连接的 Provider ID。</returns>
    internal string DeleteWithCredentialCleanup(
        string connectionId,
        long expectedRevision,
        Action<string> beforePublish)
    {
        ArgumentNullException.ThrowIfNull(beforePublish);
        return DeleteCore(connectionId, expectedRevision, beforePublish);
    }

    /// <summary>
    /// 功能：在单个连接 writer lock 内完成 CAS、可选安全清理与原子删除。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connectionId">服务签发的连接 ID。</param>
    /// <param name="expectedRevision">客户端读取的当前 revision。</param>
    /// <param name="beforePublish">CAS 成功后的安全清理回调。</param>
    /// <returns>已删除连接的 Provider ID。</returns>
    private string DeleteCore(
        string connectionId,
        long expectedRevision,
        Action<string>? beforePublish)
    {
        ValidateSafeId(connectionId, "connectionId");
        ValidateExpectedRevision(expectedRevision);
        try
        {
            using var stateLock = AcquireLock();
            var document = ReadDocumentUnlocked();
            var current = document.Connections.FirstOrDefault(connection =>
                string.Equals(connection.ConnectionId, connectionId, StringComparison.Ordinal));
            if (current is null)
            {
                throw ProviderConnectionException.NotFound(connectionId);
            }

            if (current.Revision != expectedRevision)
            {
                throw ProviderConnectionException.RevisionConflict();
            }

            beforePublish?.Invoke(current.ProviderId);

            var connections = document.Connections
                .Where(connection => !string.Equals(
                    connection.ConnectionId,
                    connectionId,
                    StringComparison.Ordinal))
                .ToArray();
            WriteDocumentUnlocked(new ProviderConnectionDocument(SchemaVersion, connections));
            return current.ProviderId;
        }
        catch (ProviderCommercialStateException exception)
        {
            throw MapCommercialException(exception);
        }
    }

    /// <summary>
    /// 功能：按公开 Provider ID 取得唯一连接配置。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerId">已验证品牌中立 Provider ID。</param>
    /// <returns>存在时返回防御性配置，否则 null。</returns>
    /// <exception cref="ProviderConnectionException">字段或存储失败。</exception>
    public ProviderConnectionConfiguration? FindByProviderId(string providerId)
    {
        ValidateSafeId(providerId, "providerId");
        return List()
            .FirstOrDefault(connection => string.Equals(
                connection.ProviderId,
                providerId,
                StringComparison.Ordinal));
    }

    /// <summary>
    /// 功能：持有连接 writer lock 验证 Provider 存在，并在同一临界区执行 credential 操作。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerId">必须对应唯一连接的 Provider ID。</param>
    /// <param name="operation">只接收公开 Provider ID、不得回调连接 Store 的敏感边界操作。</param>
    /// <remarks>不变量：所有组合写操作固定采用 connection lock 后 credential lock 的顺序。</remarks>
    /// <exception cref="ProviderConnectionException">连接缺失、连接锁冲突或文档无效。</exception>
    internal void ExecuteForProvider(string providerId, Action<string> operation)
    {
        ValidateSafeId(providerId, "providerId");
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            using var stateLock = AcquireLock();
            var document = ReadDocumentUnlocked();
            if (!document.Connections.Any(connection =>
                    string.Equals(connection.ProviderId, providerId, StringComparison.Ordinal)))
            {
                throw new ProviderConnectionException(new PortableError(
                    -32602,
                    "provider connection was not found",
                    false,
                    new ErrorDetails("provider_connection_not_found", ProviderId: providerId)));
            }

            operation(providerId);
        }
        catch (ProviderCommercialStateException exception)
        {
            throw MapCommercialException(exception);
        }
    }

    /// <summary>
    /// 功能：为通用 CLI 在连接 writer lock 内执行任意 CredentialStore 操作。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <typeparam name="TResult">脱敏操作结果类型。</typeparam>
    /// <param name="operation">不得回调连接 Store 的 CredentialStore 操作。</param>
    /// <returns>CredentialStore 操作的脱敏结果。</returns>
    /// <remarks>不变量：所有 CLI 与连接 CRUD 组合操作固定采用 connection lock 后 credential lock 的顺序。</remarks>
    internal TResult ExecuteCredentialOperation<TResult>(Func<TResult> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            using var stateLock = AcquireLock();
            return operation();
        }
        catch (ProviderCommercialStateException exception)
        {
            throw MapCommercialException(exception);
        }
    }

    /// <summary>
    /// 功能：验证完整非敏感连接输入的长度、ID、family、URL 与模型集合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="input">来自协议或持久化文档的候选输入。</param>
    /// <param name="allowLoopbackHttp">是否允许 conformance literal loopback HTTP。</param>
    /// <exception cref="ProviderConnectionException">任一字段不满足安全边界。</exception>
    internal static void ValidateInput(ProviderConnectionInput input, bool allowLoopbackHttp)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.ModelIds is null)
        {
            throw ProviderConnectionException.Invalid("modelIds");
        }

        ValidateDisplayName(input.DisplayName);
        ValidateSafeId(input.ProviderId, "providerId");
        if (string.Equals(input.ProviderId, "faux", StringComparison.Ordinal) ||
            ProviderIdentityAdvertisement.IsCanonicalProviderId(input.ProviderId) ||
            input.ProviderId.StartsWith("relay-", StringComparison.Ordinal))
        {
            throw ProviderConnectionException.Invalid("providerId");
        }

        if (!string.Equals(input.ApiFamily, "openai-completions", StringComparison.Ordinal))
        {
            throw ProviderConnectionException.Invalid("apiFamily");
        }

        ValidateBaseUrl(input.BaseUrl, allowLoopbackHttp);
        ValidateModelIds(input.ModelIds, allowEmpty: true);

        if (input.LogoAssetId is not null)
        {
            ValidateSafeId(input.LogoAssetId, "logoAssetId");
        }
    }

    /// <summary>
    /// 功能：验证模型 allowlist 的数量、UTF-8 字节长度、控制字符与 ordinal 唯一性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="modelIds">候选模型集合。</param>
    /// <param name="allowEmpty">普通连接输入允许尚未发现模型，发现结果不允许为空。</param>
    /// <exception cref="ProviderConnectionException">集合或任一模型 ID 不满足冻结边界。</exception>
    private static void ValidateModelIds(IReadOnlyList<string> modelIds, bool allowEmpty)
    {
        if (modelIds is null ||
            modelIds.Count > 512 ||
            (!allowEmpty && modelIds.Count == 0))
        {
            throw ProviderConnectionException.Invalid("modelIds");
        }

        var models = new HashSet<string>(StringComparer.Ordinal);
        foreach (var modelId in modelIds)
        {
            if (!IsValidModelId(modelId) || !models.Add(modelId))
            {
                throw ProviderConnectionException.Invalid("modelIds");
            }
        }
    }

    /// <summary>
    /// 功能：判断模型 ID 是否为 1..256 UTF-8 字节且不含控制字符或无效代理项。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选模型 ID。</param>
    /// <returns>满足 wire 与持久化共同边界时为 true。</returns>
    internal static bool IsValidModelId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(static character => char.IsControl(character)))
        {
            return false;
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetByteCount(value) <= 256;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// 功能：验证 connection/provider/logo 使用的受限小写 ASCII ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选 ID。</param>
    /// <param name="field">错误中可公开的字段名。</param>
    /// <exception cref="ProviderConnectionException">值不符合 1..128 和字符 allowlist。</exception>
    internal static void ValidateSafeId(string value, string field)
    {
        if (value is null ||
            value.Length is < 1 or > 128 ||
            !IsLowerAlphaNumeric(value[0]) ||
            value.Any(static character =>
                !IsLowerAlphaNumeric(character) && character is not ('-' or '.')))
        {
            throw ProviderConnectionException.Invalid(field);
        }
    }

    /// <summary>
    /// 功能：取得不早于上一更新时间的 UTC 当前时间。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>毫秒精度 UTC 时点。</returns>
    private DateTimeOffset GetUtcNow()
    {
        var value = timeProvider.GetUtcNow().ToUniversalTime();
        return new DateTimeOffset(
            value.Year,
            value.Month,
            value.Day,
            value.Hour,
            value.Minute,
            value.Second,
            value.Millisecond,
            TimeSpan.Zero);
    }

    /// <summary>
    /// 功能：以 FileShare.None 获取连接文档跨进程 writer lock。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>释放时解除锁的独占 FileStream。</returns>
    private FileStream AcquireLock()
    {
        var path = Path.Combine(root, "provider-connections.lock");
        try
        {
            CommercialFileSafety.RejectReparsePointIfPresent(path);
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            CommercialFileSafety.RestrictFilePermissions(path);
            return stream;
        }
        catch (ProviderCommercialStateException exception)
        {
            throw MapCommercialException(exception);
        }
        catch (IOException)
        {
            throw ProviderConnectionException.Store("provider_connection_store_locked", true);
        }
        catch (UnauthorizedAccessException)
        {
            throw ProviderConnectionException.Store("provider_connection_store_io", false);
        }
    }

    /// <summary>
    /// 功能：在独占锁内读取并验证完整连接文档。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>不存在时返回空 v0.1 文档。</returns>
    private ProviderConnectionDocument ReadDocumentUnlocked()
    {
        var path = Path.Combine(root, "provider-connections.json");
        if (!File.Exists(path))
        {
            return new ProviderConnectionDocument(SchemaVersion, []);
        }

        var document = CommercialFileSafety.ParseStrict<ProviderConnectionDocument>(
            CommercialFileSafety.ReadBounded(path, MaximumDocumentBytes, sensitive: false),
            "provider_connection_json");
        try
        {
            ValidateDocument(document);
        }
        catch (ProviderConnectionException exception)
            when (exception.Error.Details.Kind == "invalid_params")
        {
            throw ProviderConnectionException.Store("provider_connection_store_invalid", false);
        }

        return document;
    }

    /// <summary>
    /// 功能：在独占锁内原子发布完整非敏感连接文档。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="document">已验证且排序的完整文档。</param>
    private void WriteDocumentUnlocked(ProviderConnectionDocument document)
    {
        ValidateDocument(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonDefaults.Options);
        if (bytes.Length > MaximumDocumentBytes)
        {
            throw ProviderConnectionException.Store("provider_connection_store_invalid", false);
        }

        CommercialFileSafety.AtomicWrite(
            Path.Combine(root, "provider-connections.json"),
            bytes,
            sensitive: false);
    }

    /// <summary>
    /// 功能：验证持久化文档版本、数量、排序、唯一身份与全部字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="document">严格反序列化后的候选文档。</param>
    private void ValidateDocument(ProviderConnectionDocument document)
    {
        if (document.SchemaVersion != SchemaVersion ||
            document.Connections is null ||
            document.Connections.Count > MaximumConnections)
        {
            throw ProviderConnectionException.Store("provider_connection_store_invalid", false);
        }

        var connectionIds = new HashSet<string>(StringComparer.Ordinal);
        var providerIds = new HashSet<string>(StringComparer.Ordinal);
        string? previousConnectionId = null;
        foreach (var connection in document.Connections)
        {
            if (connection is null)
            {
                throw ProviderConnectionException.Store("provider_connection_store_invalid", false);
            }

            ValidateSafeId(connection.ConnectionId, "connectionId");
            ValidateExpectedRevision(connection.Revision);
            ValidateInput(
                new ProviderConnectionInput(
                    connection.DisplayName,
                    connection.ProviderId,
                    connection.ApiFamily,
                    connection.BaseUrl,
                    connection.ModelIds,
                    connection.LogoAssetId,
                    connection.Enabled),
                allowLoopbackHttp);
            if (!connectionIds.Add(connection.ConnectionId) ||
                !providerIds.Add(connection.ProviderId) ||
                (previousConnectionId is not null &&
                    string.CompareOrdinal(previousConnectionId, connection.ConnectionId) >= 0) ||
                connection.CreatedAt.Offset != TimeSpan.Zero ||
                connection.UpdatedAt.Offset != TimeSpan.Zero ||
                connection.CreatedAt > connection.UpdatedAt)
            {
                throw ProviderConnectionException.Store("provider_connection_store_invalid", false);
            }

            previousConnectionId = connection.ConnectionId;
        }
    }

    /// <summary>
    /// 功能：验证 CAS revision 是正的 JavaScript 安全整数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选 revision。</param>
    /// <exception cref="ProviderConnectionException">revision 越界。</exception>
    private static void ValidateExpectedRevision(long value)
    {
        if (value is < 1 or > MaxSafeInteger)
        {
            throw ProviderConnectionException.Invalid("expectedRevision");
        }
    }

    /// <summary>
    /// 功能：验证显示名长度、控制字符和首尾空白。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选显示名。</param>
    private static void ValidateDisplayName(string value)
    {
        if (value is null ||
            !IsBoundedText(value, 64) ||
            char.IsWhiteSpace(value[0]) ||
            char.IsWhiteSpace(value[^1]))
        {
            throw ProviderConnectionException.Invalid("displayName");
        }
    }

    /// <summary>
    /// 功能：验证 API base URL 的 scheme、authority 和不可注入字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选 URL。</param>
    /// <param name="allowLoopbackHttp">是否允许 conformance literal loopback HTTP。</param>
    private static void ValidateBaseUrl(string value, bool allowLoopbackHttp)
    {
        if (value is null ||
            value.Length is < 1 or > 2_048 ||
            value.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character)) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw ProviderConnectionException.Invalid("baseUrl");
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return;
        }

        if (!allowLoopbackHttp ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
            !IsLiteralLoopback(uri.Host))
        {
            throw ProviderConnectionException.Invalid("baseUrl");
        }
    }

    /// <summary>
    /// 功能：判断 host 是 localhost 或 literal loopback IP，绝不执行 DNS。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="host">Uri 已解析 host。</param>
    /// <returns>只在明确回环时为 true。</returns>
    private static bool IsLiteralLoopback(string host)
    {
        return string.Equals(host, "127.0.0.1", StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：检查文本非空、有界且不包含控制字符。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选文本。</param>
    /// <param name="maximumLength">允许最大字符数。</param>
    /// <returns>满足边界时 true。</returns>
    private static bool IsBoundedText(string value, int maximumLength)
    {
        return value is not null &&
            value.Length is >= 1 && value.Length <= maximumLength &&
            value.All(static character => !char.IsControl(character));
    }

    /// <summary>
    /// 功能：判断字符属于小写 ASCII 字母或数字。
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
    /// 功能：深复制连接模型数组，隔离持久化快照与调用方。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connection">已验证连接。</param>
    /// <returns>字段相同且模型数组独立的配置。</returns>
    private static ProviderConnectionConfiguration Clone(ProviderConnectionConfiguration connection)
    {
        return connection with { ModelIds = connection.ModelIds.ToArray() };
    }

    /// <summary>
    /// 功能：把公共商业文件层异常映射为连接专属脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="exception">不含路径与正文的底层异常。</param>
    /// <returns>固定 invalid、locked 或 io 分类。</returns>
    private static ProviderConnectionException MapCommercialException(
        ProviderCommercialStateException exception)
    {
        return exception.Kind.Contains("lock", StringComparison.Ordinal)
            ? ProviderConnectionException.Store("provider_connection_store_locked", true)
            : exception.Kind.Contains("json", StringComparison.Ordinal) ||
                exception.Kind.Contains("shape", StringComparison.Ordinal) ||
                exception.Kind.Contains("duplicate", StringComparison.Ordinal) ||
                exception.Kind.Contains("reparse", StringComparison.Ordinal) ||
                exception.Kind.Contains("metadata", StringComparison.Ordinal)
                ? ProviderConnectionException.Store("provider_connection_store_invalid", false)
                : ProviderConnectionException.Store("provider_connection_store_io", false);
    }
}

/// <summary>
/// 功能：组合非敏感连接 Store 与独立 CredentialStore，提供脱敏 application service 操作。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class CustomProviderConnectionService
{
    private const int MaximumDiscoveryBytes = 1024 * 1024;
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(20);
    private static readonly HttpClient SharedDiscoveryHttpClient = CreateDiscoveryHttpClient();
    private readonly ProviderCredentialStore credentials;
    private readonly CustomProviderConnectionStore connections;
    private readonly HttpClient discoveryHttpClient;

    /// <summary>
    /// 功能：从可信 state root、workspace 与 conformance 模式创建连接服务。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="stateRoot">工作区外应用状态根。</param>
    /// <param name="workspace">CredentialStore 必须逃离的 canonical workspace。</param>
    /// <param name="allowLoopbackHttp">是否已通过 CLI、通用环境与 Provider 环境三个 conformance 门。</param>
    /// <remarks>不变量：连接文档永不存 secret；CredentialStore 只按 Provider ID 独立持有 secret。</remarks>
    public CustomProviderConnectionService(
        string stateRoot,
        string workspace,
        bool allowLoopbackHttp = false)
        : this(stateRoot, workspace, allowLoopbackHttp, SharedDiscoveryHttpClient)
    {
    }

    /// <summary>
    /// 功能：为测试注入不会访问真实网络的模型发现 HTTP client。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="stateRoot">隔离应用状态根。</param>
    /// <param name="workspace">CredentialStore 必须逃离的 workspace。</param>
    /// <param name="allowLoopbackHttp">是否允许 conformance literal loopback HTTP。</param>
    /// <param name="discoveryHttpClient">由测试控制响应的 client；服务不取得其所有权。</param>
    internal CustomProviderConnectionService(
        string stateRoot,
        string workspace,
        bool allowLoopbackHttp,
        HttpClient discoveryHttpClient)
    {
        connections = new CustomProviderConnectionStore(stateRoot, allowLoopbackHttp);
        credentials = new ProviderCredentialStore(stateRoot, workspace);
        this.discoveryHttpClient = discoveryHttpClient
            ?? throw new ArgumentNullException(nameof(discoveryHttpClient));
    }

    /// <summary>
    /// 功能：列出全部连接并仅投影 credentialConfigured 布尔状态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>按 connectionId 排序且永不包含 credential 的数组。</returns>
    public IReadOnlyList<ProviderConnection> List()
    {
        var configured = ListConfiguredProviderIds();
        return connections.List().Select(connection => Project(connection, configured)).ToArray();
    }

    /// <summary>
    /// 功能：创建非敏感连接并返回当前凭据配置状态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="input">不含 secret 的完整连接输入。</param>
    /// <returns>已 durable 且 credentialConfigured=false 的脱敏连接。</returns>
    /// <remarks>在 connection lock 内先清理目标 ID orphan；发布失败时不恢复可能绑定其他 endpoint 的历史 secret。</remarks>
    public ProviderConnection Create(ProviderConnectionInput input)
    {
        var connection = connections.CreateWithCredentialCleanup(
            input,
            providerId =>
            {
                try
                {
                    _ = credentials.Remove(providerId);
                }
                catch (ProviderCommercialStateException exception)
                {
                    throw MapCredentialException(exception);
                }
            });
        return Project(connection, ListConfiguredProviderIds());
    }

    /// <summary>
    /// 功能：以 CAS 完整更新非敏感连接且不读取或移动凭据正文。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connectionId">目标连接 ID。</param>
    /// <param name="expectedRevision">期望 revision。</param>
    /// <param name="input">完整替换输入。</param>
    /// <returns>已 durable 的脱敏连接。</returns>
    /// <remarks>Provider ID 改变时先删除目标 ID 的历史 orphan，再删除旧 ID credential，防止隐式继承 secret。</remarks>
    public ProviderConnection Update(
        string connectionId,
        long expectedRevision,
        ProviderConnectionInput input)
    {
        var connection = connections.UpdateWithProviderTransition(
            connectionId,
            expectedRevision,
            input,
            (oldProviderId, newProviderId) =>
            {
                try
                {
                    _ = credentials.Remove(newProviderId);
                    _ = credentials.Remove(oldProviderId);
                }
                catch (ProviderCommercialStateException exception)
                {
                    throw MapCredentialException(exception);
                }
            });

        return Project(connection, ListConfiguredProviderIds());
    }

    /// <summary>
    /// 功能：显式请求自定义 Provider 模型目录，并以原 revision CAS 仅更新模型 allowlist。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connectionId">服务签发的连接 ID。</param>
    /// <param name="expectedRevision">用户触发发现时看到的 revision。</param>
    /// <param name="cancellationToken">调用方取消信号；另有固定 20 秒总时限。</param>
    /// <returns>已 durable 发布且不包含 credential 的连接投影。</returns>
    /// <remarks>不变量：credential 仅在最终 GET 请求边界读取；任何远端或 CAS 失败都不修改连接。</remarks>
    /// <exception cref="ProviderConnectionException">凭据、传输、目录、CAS 或存储失败。</exception>
    public async Task<ProviderConnection> DiscoverModelsAsync(
        string connectionId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var connection = connections.ReadExact(connectionId, expectedRevision);
        var endpoint = BuildDiscoveryEndpoint(connection.BaseUrl);
        var modelIds = await FetchModelsAsync(
            endpoint,
            connection.ProviderId,
            cancellationToken).ConfigureAwait(false);
        var updated = connections.ReplaceDiscoveredModels(
            connectionId,
            expectedRevision,
            modelIds);
        return Project(
            updated,
            new HashSet<string>([updated.ProviderId], StringComparer.Ordinal));
    }

    /// <summary>
    /// 功能：按 CAS 删除连接及其独立 stored credential。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connectionId">目标连接 ID。</param>
    /// <param name="expectedRevision">期望 revision。</param>
    /// <remarks>不变量：成功返回后连接和关联 credential 均不可达；任何响应都不包含旧 secret。</remarks>
    public void Delete(string connectionId, long expectedRevision)
    {
        _ = connections.DeleteWithCredentialCleanup(
            connectionId,
            expectedRevision,
            providerId =>
            {
                try
                {
                    _ = credentials.Remove(providerId);
                }
                catch (ProviderCommercialStateException exception)
                {
                    throw MapCredentialException(exception);
                }
            });
    }

    /// <summary>
    /// 功能：在最终敏感边界为已存在连接保存或轮换 Provider credential。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerId">必须对应唯一已存在连接的 Provider ID。</param>
    /// <param name="credential">1..16384 且无 CR/LF/NUL 的 secret。</param>
    /// <remarks>不变量：credential 不进入连接 JSON、返回值、日志、Session 或异常。</remarks>
    public void SetCredential(string providerId, string credential)
    {
        CustomProviderConnectionStore.ValidateSafeId(providerId, "providerId");
        if (credential is null ||
            credential.Length is < 1 or > 16_384 ||
            credential.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw ProviderConnectionException.Invalid("credential");
        }

        connections.ExecuteForProvider(
            providerId,
            id =>
            {
                try
                {
                    credentials.Set(id, credential);
                }
                catch (ProviderCommercialStateException exception)
                {
                    throw MapCredentialException(exception);
                }
            });
    }

    /// <summary>
    /// 功能：移除已存在连接的 stored credential，不返回旧值或摘要。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerId">必须对应唯一已存在连接的 Provider ID。</param>
    public void RemoveCredential(string providerId)
    {
        connections.ExecuteForProvider(
            providerId,
            id =>
            {
                try
                {
                    _ = credentials.Remove(id);
                }
                catch (ProviderCommercialStateException exception)
                {
                    throw MapCredentialException(exception);
                }
            });
    }

    /// <summary>
    /// 功能：为通用 CLI 在固定锁顺序内保存任意合法 Provider credential。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerId">canonical、推广或自定义 Provider ID。</param>
    /// <param name="credential">只从重定向 stdin 读取的 header-safe secret。</param>
    /// <remarks>不变量：先持有 connection writer lock，再写 CredentialStore；不要求自定义连接已存在。</remarks>
    public void SetCredentialCoordinated(string providerId, string credential)
    {
        CustomProviderConnectionStore.ValidateSafeId(providerId, "providerId");
        if (credential is null ||
            credential.Length is < 1 or > 16_384 ||
            credential.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw ProviderConnectionException.Invalid("credential");
        }

        _ = connections.ExecuteCredentialOperation(
            () =>
            {
                credentials.Set(providerId, credential);
                return true;
            });
    }

    /// <summary>
    /// 功能：为通用 CLI 在固定锁顺序内列出 configured Provider ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>按 ordinal 排序且不含 secret 的 Provider ID 数组。</returns>
    public IReadOnlyList<string> ListCredentialsCoordinated()
    {
        return connections.ExecuteCredentialOperation(
            () => credentials.List().ToArray());
    }

    /// <summary>
    /// 功能：为通用 CLI 在固定锁顺序内移除任意合法 Provider credential。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerId">canonical、推广或自定义 Provider ID。</param>
    /// <returns>存在并删除时 true，不存在时 false。</returns>
    public bool RemoveCredentialCoordinated(string providerId)
    {
        CustomProviderConnectionStore.ValidateSafeId(providerId, "providerId");
        return connections.ExecuteCredentialOperation(
            () => credentials.Remove(providerId));
    }

    /// <summary>
    /// 功能：读取 CredentialStore 的 Provider ID 集合，不接触任何 secret 正文。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>大小写敏感的 configured Provider ID 集合。</returns>
    private HashSet<string> ListConfiguredProviderIds()
    {
        try
        {
            return new HashSet<string>(credentials.List(), StringComparer.Ordinal);
        }
        catch (ProviderCommercialStateException exception)
        {
            throw MapCredentialException(exception);
        }
    }

    /// <summary>
    /// 功能：发送单次禁止 redirect 的 Bearer GET，并读取不超过 1 MiB 的严格模型目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="endpoint">读取 credential 前已完成规范化的同源 `/models` endpoint。</param>
    /// <param name="providerId">网络前固定、用于最终边界读取 credential 的 Provider ID。</param>
    /// <param name="cancellationToken">调用方取消信号。</param>
    /// <returns>去重并按 ordinal 排序的 1..512 个模型 ID。</returns>
    /// <exception cref="ProviderConnectionException">状态、大小、传输、超时或 JSON 不满足边界。</exception>
    private async Task<IReadOnlyList<string>> FetchModelsAsync(
        Uri endpoint,
        string providerId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!credentials.TryReadForRequest(providerId, out var credential) ||
                credential is null)
            {
                throw ProviderConnectionException.DiscoveryFailed(retryable: false);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (!request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + credential))
            {
                throw ProviderConnectionException.DiscoveryFailed(retryable: false);
            }

            using var totalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            totalCancellation.CancelAfter(DiscoveryTimeout);
            using var response = await discoveryHttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                totalCancellation.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw ProviderConnectionException.DiscoveryFailed(IsRetryable(response.StatusCode));
            }

            var bytes = await ReadDiscoveryBodyAsync(
                response,
                totalCancellation.Token).ConfigureAwait(false);
            return ParseDiscoveredModels(bytes);
        }
        catch (ProviderConnectionException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw ProviderConnectionException.DiscoveryFailed(retryable: true);
        }
        catch (HttpRequestException)
        {
            throw ProviderConnectionException.DiscoveryFailed(retryable: true);
        }
        catch (IOException)
        {
            throw ProviderConnectionException.DiscoveryFailed(retryable: true);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            throw ProviderConnectionException.DiscoveryFailed(retryable: false);
        }
    }

    /// <summary>
    /// 功能：在读取 credential 前构造同源且不含 query 的模型发现 endpoint。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="baseUrl">已由持久化连接验证的非敏感 base URL。</param>
    /// <returns>字面追加 `/models` 的绝对 URI。</returns>
    /// <exception cref="ProviderConnectionException">URI 状态意外失效时返回固定脱敏错误。</exception>
    private static Uri BuildDiscoveryEndpoint(string baseUrl)
    {
        try
        {
            return NativeProviderEndpoint.Append(
                new Uri(baseUrl, UriKind.Absolute),
                "/models");
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw ProviderConnectionException.DiscoveryFailed(retryable: false);
        }
    }

    /// <summary>
    /// 功能：流式读取模型目录并同时执行 Content-Length 与实际字节硬上限。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="response">已确认成功且仍由调用方持有的响应。</param>
    /// <param name="cancellationToken">20 秒总时限与调用方取消的组合信号。</param>
    /// <returns>长度不超过 1 MiB 的独立字节数组。</returns>
    /// <exception cref="ProviderConnectionException">声明或实际响应超过上限。</exception>
    private static async Task<byte[]> ReadDiscoveryBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumDiscoveryBytes)
        {
            throw ProviderConnectionException.DiscoveryFailed(retryable: false);
        }

        await using var input = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > MaximumDiscoveryBytes)
            {
                throw ProviderConnectionException.DiscoveryFailed(retryable: false);
            }

            output.Write(buffer, 0, read);
        }
    }

    /// <summary>
    /// 功能：严格提取 OpenAI-compatible 模型目录的 data 数组与每项 id，同时忽略非关键元数据。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="bytes">已通过 1 MiB 上限的 UTF-8 JSON。</param>
    /// <returns>去重、ordinal 排序且非空的模型 ID 数组。</returns>
    /// <exception cref="ProviderConnectionException">JSON shape、模型 ID 或数量不合法。</exception>
    internal static IReadOnlyList<string> ParseDiscoveredModels(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw ProviderConnectionException.DiscoveryFailed(retryable: false);
            }

            ValidateNoDuplicateDiscoveryKeys(root);
            JsonElement data = default;
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "data", StringComparison.Ordinal))
                {
                    if (property.Value.ValueKind != JsonValueKind.Array)
                    {
                        throw ProviderConnectionException.DiscoveryFailed(retryable: false);
                    }

                    data = property.Value;
                }
            }

            if (data.ValueKind != JsonValueKind.Array)
            {
                throw ProviderConnectionException.DiscoveryFailed(retryable: false);
            }

            var modelIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw ProviderConnectionException.DiscoveryFailed(retryable: false);
                }

                string? modelId = null;
                foreach (var property in item.EnumerateObject())
                {
                    if (string.Equals(property.Name, "id", StringComparison.Ordinal))
                    {
                        if (property.Value.ValueKind != JsonValueKind.String)
                        {
                            throw ProviderConnectionException.DiscoveryFailed(retryable: false);
                        }

                        modelId = property.Value.GetString();
                    }
                }

                if (modelId is null || !CustomProviderConnectionStore.IsValidModelId(modelId))
                {
                    throw ProviderConnectionException.DiscoveryFailed(retryable: false);
                }

                _ = modelIds.Add(modelId);
                if (modelIds.Count > 512)
                {
                    throw ProviderConnectionException.DiscoveryFailed(retryable: false);
                }
            }

            if (modelIds.Count == 0)
            {
                throw ProviderConnectionException.DiscoveryFailed(retryable: false);
            }

            return modelIds.Order(StringComparer.Ordinal).ToArray();
        }
        catch (ProviderConnectionException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw ProviderConnectionException.DiscoveryFailed(retryable: false);
        }
    }

    /// <summary>
    /// 功能：递归拒绝模型目录及被忽略元数据中任意层级的重复 JSON key。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">当前模型目录 JSON 节点。</param>
    /// <remarks>不变量：属性名按 ordinal 比较；数组内每个 object 都递归验证。</remarks>
    /// <exception cref="ProviderConnectionException">发现重复 key 时返回固定脱敏发现错误。</exception>
    private static void ValidateNoDuplicateDiscoveryKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw ProviderConnectionException.DiscoveryFailed(retryable: false);
                }

                ValidateNoDuplicateDiscoveryKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                ValidateNoDuplicateDiscoveryKeys(item);
            }
        }
    }

    /// <summary>
    /// 功能：判断远端状态是否代表适合稍后重试的临时失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="statusCode">未向调用方公开的远端 HTTP 状态。</param>
    /// <returns>408、429 或 5xx 时为 true。</returns>
    private static bool IsRetryable(HttpStatusCode statusCode)
    {
        var numeric = (int)statusCode;
        return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
            numeric >= 500;
    }

    /// <summary>
    /// 功能：创建进程级共享且禁止 redirect、proxy、cookie 与自动解压的模型发现 client。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>连接超时十秒、总时限由每次操作独立控制的 HttpClient。</returns>
    private static HttpClient CreateDiscoveryHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseCookies = false,
            UseProxy = false,
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// 功能：把持久化配置与凭据 presence 组合为脱敏 wire 投影。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connection">非敏感持久化配置。</param>
    /// <param name="configured">只含 Provider ID 的 presence 集合。</param>
    /// <returns>永不包含 credential 的完整连接投影。</returns>
    private static ProviderConnection Project(
        ProviderConnectionConfiguration connection,
        HashSet<string> configured)
    {
        return new ProviderConnection(
            connection.ConnectionId,
            connection.Revision,
            connection.DisplayName,
            connection.ProviderId,
            connection.ApiFamily,
            connection.BaseUrl,
            connection.ModelIds.ToArray(),
            connection.LogoAssetId,
            connection.Enabled,
            configured.Contains(connection.ProviderId),
            connection.CreatedAt,
            connection.UpdatedAt);
    }

    /// <summary>
    /// 功能：把 CredentialStore 异常映射为不泄露 secret 或路径的连接存储错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="exception">CredentialStore 的稳定安全异常。</param>
    /// <returns>固定 locked、invalid 或 io 分类。</returns>
    private static ProviderConnectionException MapCredentialException(
        ProviderCommercialStateException exception)
    {
        return exception.Kind.Contains("lock", StringComparison.Ordinal)
            ? ProviderConnectionException.Store("provider_connection_store_locked", true)
            : exception.Kind.Contains("shape", StringComparison.Ordinal) ||
                exception.Kind.Contains("json", StringComparison.Ordinal) ||
                exception.Kind.Contains("duplicate", StringComparison.Ordinal) ||
                exception.Kind.Contains("reparse", StringComparison.Ordinal) ||
                exception.Kind.Contains("provider_id", StringComparison.Ordinal) ||
                exception.Kind.Contains("credential_value", StringComparison.Ordinal)
                ? ProviderConnectionException.Store("provider_connection_store_invalid", false)
                : ProviderConnectionException.Store("provider_connection_store_io", false);
    }
}

/// <summary>
/// 功能：把启动期固定的自定义 OpenAI Chat 连接绑定到共享 request/SSE parser。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class CustomOpenAiCompletionsProvider : OpenAiChatProvider
{
    private readonly Uri baseEndpoint;

    /// <summary>
    /// 功能：创建只在请求最终边界读取 stored credential 的自定义 adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connection">已验证、启用且凭据存在的启动快照。</param>
    /// <param name="store">工作区外 CredentialStore。</param>
    /// <param name="options">有界 HTTP/SSE 策略。</param>
    internal CustomOpenAiCompletionsProvider(
        ProviderConnectionConfiguration connection,
        ProviderCredentialStore store,
        ProviderTransportOptions options)
        : base(
            connection.ProviderId,
            new Uri(connection.BaseUrl, UriKind.Absolute),
            ProviderCredentialSource.FromStore(store, connection.ProviderId),
            connection.ApiFamily,
            connection.ModelIds,
            options)
    {
        baseEndpoint = new Uri(connection.BaseUrl, UriKind.Absolute);
    }

    /// <summary>
    /// 功能：在用户固定 API base 后同源追加 `/chat/completions`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">已通过 Provider/model allowlist 的请求。</param>
    /// <returns>保持 scheme、host、port 与 base path 的精确 endpoint。</returns>
    protected override Uri CreateRequestEndpoint(ProviderRequest request)
    {
        return NativeProviderEndpoint.Append(baseEndpoint, "/chat/completions");
    }
}

/// <summary>
/// 功能：创建自定义连接 adapter 与同源模型描述。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal static class CustomProviderConnectionAdapterFactory
{
    /// <summary>
    /// 功能：为单条启动连接创建 OpenAI Chat adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connection">已验证连接。</param>
    /// <param name="store">请求期 CredentialStore。</param>
    /// <param name="options">有界传输策略。</param>
    /// <returns>不持有 secret 的原生 Provider。</returns>
    internal static IProvider Create(
        ProviderConnectionConfiguration connection,
        ProviderCredentialStore store,
        ProviderTransportOptions options)
    {
        return new CustomOpenAiCompletionsProvider(connection, store, options);
    }

    /// <summary>
    /// 功能：把自定义连接模型 allowlist 转成 registry 公共描述。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connection">已验证启动连接。</param>
    /// <returns>与 modelIds 一一对应的文本、流式模型描述；未知能力不广告工具或 reasoning。</returns>
    internal static IReadOnlyList<ModelDescriptor> CreateModelDescriptors(
        ProviderConnectionConfiguration connection)
    {
        return connection.ModelIds.Select(modelId => new ModelDescriptor(
            connection.ProviderId,
            modelId,
            modelId,
            connection.ApiFamily,
            new ModelCapabilities(
                ["text"],
                ["text"],
                Streaming: true,
                Tools: false,
                Reasoning: false))).ToArray();
    }
}
