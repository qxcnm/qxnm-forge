using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using QxnmForge.Domain;
using QxnmForge.Storage;

namespace QxnmForge.Agent;

/// <summary>
/// 功能：表示 Agent Profile 绑定的完整模型路由身份。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ProviderId">Provider ID。</param>
/// <param name="ModelId">Provider 内模型 ID。</param>
/// <param name="ApiFamily">原生 API family。</param>
public sealed record AgentProfileModel(string ProviderId, string ModelId, string ApiFamily);

/// <summary>
/// 功能：表示不授予权限的 Agent 回复行为偏好。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ResponseStyle">concise、balanced 或 detailed。</param>
/// <param name="PlanFirst">是否偏好先给计划。</param>
/// <param name="ReviewChanges">是否偏好完成后审阅变更。</param>
public sealed record AgentProfileBehavior(
    string ResponseStyle,
    bool PlanFirst,
    bool ReviewChanges);

/// <summary>
/// 功能：表示创建或完整替换 Agent Profile 的不可信输入。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="DisplayName">用户可见名称。</param>
/// <param name="Description">用户可见说明。</param>
/// <param name="Enabled">是否允许绑定新 run。</param>
/// <param name="Instructions">仅在 Provider 请求边界注入的系统指令。</param>
/// <param name="Model">完整模型三元组。</param>
/// <param name="RequestedToolIds">仅用于收窄 daemon 工具能力的请求集合。</param>
/// <param name="DangerousActionMode">ask 或 deny。</param>
/// <param name="Behavior">不授予权限的回复偏好。</param>
public sealed record AgentProfileInput(
    string DisplayName,
    string Description,
    bool Enabled,
    string Instructions,
    AgentProfileModel Model,
    IReadOnlyList<string> RequestedToolIds,
    string DangerousActionMode,
    AgentProfileBehavior Behavior);

/// <summary>
/// 功能：表示可通过 application service 返回的完整 Agent Profile。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ProfileId">服务签发的稳定 opaque ID。</param>
/// <param name="Revision">从 1 开始单调递增的 CAS revision。</param>
/// <param name="DisplayName">用户可见名称。</param>
/// <param name="Description">用户可见说明。</param>
/// <param name="Enabled">是否允许绑定新 run。</param>
/// <param name="Instructions">仅在 Provider 请求边界注入的系统指令。</param>
/// <param name="Model">完整模型三元组。</param>
/// <param name="RequestedToolIds">排序后的工具请求集合。</param>
/// <param name="DangerousActionMode">ask 或 deny。</param>
/// <param name="Behavior">不授予权限的回复偏好。</param>
/// <param name="CreatedAt">UTC 创建时间。</param>
/// <param name="UpdatedAt">UTC 最近更新时间。</param>
public sealed record AgentProfile(
    string ProfileId,
    long Revision,
    string DisplayName,
    string Description,
    bool Enabled,
    string Instructions,
    AgentProfileModel Model,
    IReadOnlyList<string> RequestedToolIds,
    string DangerousActionMode,
    AgentProfileBehavior Behavior,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// 功能：表示 run/start 对 Agent Profile 的精确 revision 引用。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ProfileId">服务签发的稳定 opaque ID。</param>
/// <param name="Revision">客户端读取到的正安全整数 revision。</param>
public sealed record AgentProfileReference(string ProfileId, long Revision);

/// <summary>
/// 功能：表示 run.accepted 持久化的不可变 Agent Profile 执行快照。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ProfileId">绑定的 profile ID。</param>
/// <param name="Revision">绑定的精确 revision。</param>
/// <param name="DisplayName">接受时名称。</param>
/// <param name="Instructions">接受时系统指令。</param>
/// <param name="Model">接受时完整模型三元组。</param>
/// <param name="RequestedToolIds">接受时请求工具集合。</param>
/// <param name="EffectiveToolIds">与当前 registry 及危险模式相交后的执行集合。</param>
/// <param name="DangerousActionMode">接受时危险操作模式。</param>
/// <param name="Behavior">接受时回复行为偏好。</param>
public sealed record AgentProfileRunSnapshot(
    string ProfileId,
    long Revision,
    string DisplayName,
    string Instructions,
    AgentProfileModel Model,
    IReadOnlyList<string> RequestedToolIds,
    IReadOnlyList<string> EffectiveToolIds,
    string DangerousActionMode,
    AgentProfileBehavior Behavior);

/// <summary>
/// 功能：承载已验证执行快照与仅请求期使用的 system instructions。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Snapshot">可持久化到 run.accepted 的快照。</param>
/// <param name="SystemInstructions">不得追加为普通 Session message 的请求期文本。</param>
public sealed record AgentProfileRunBinding(
    AgentProfileRunSnapshot Snapshot,
    string SystemInstructions);

/// <summary>
/// 功能：以 portable error 承载 Agent Profile 边界失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class AgentProfileException : Exception
{
    /// <summary>
    /// 功能：从已脱敏 portable error 创建服务异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="error">不得包含指令正文、路径或 secret 的错误。</param>
    public AgentProfileException(PortableError error)
        : base(error.Message)
    {
        Error = error;
    }

    /// <summary>
    /// 功能：取得可直接返回 JSON-RPC 的稳定错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public PortableError Error { get; }

    /// <summary>
    /// 功能：创建字段校验失败错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>固定 agent_profile_invalid 错误。</returns>
    public static AgentProfileException Invalid()
    {
        return new AgentProfileException(new PortableError(
            -32602,
            "agent profile is invalid",
            false,
            new ErrorDetails("agent_profile_invalid", Field: "profile")));
    }

    /// <summary>
    /// 功能：创建数据库 Profile 行损坏的脱敏内部错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>不包含数据库值、SQL 或路径的固定内部错误。</returns>
    public static AgentProfileException StorageCorrupt()
    {
        return new AgentProfileException(new PortableError(
            -32603,
            "agent profile storage is corrupt",
            false,
            new ErrorDetails("agent_profile_storage_corrupt")));
    }

    /// <summary>
    /// 功能：创建 profile revision 已达到安全整数上限的固定错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="profileId">已验证的 opaque profile ID。</param>
    /// <returns>不可重试且不修改数据库的 revision exhausted 错误。</returns>
    public static AgentProfileException RevisionExhausted(string profileId)
    {
        return new AgentProfileException(new PortableError(
            -32009,
            "agent profile revision is exhausted",
            false,
            new ErrorDetails("agent_profile_revision_exhausted", ResourceId: profileId)));
    }

    /// <summary>
    /// 功能：创建 profile 缺失错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="profileId">已验证的 opaque profile ID。</param>
    /// <returns>固定 agent_profile_not_found 错误。</returns>
    public static AgentProfileException NotFound(string profileId)
    {
        return new AgentProfileException(new PortableError(
            -32602,
            "agent profile was not found",
            false,
            new ErrorDetails("agent_profile_not_found", ResourceId: profileId)));
    }

    /// <summary>
    /// 功能：创建 CAS revision 冲突错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="profileId">已验证的 opaque profile ID。</param>
    /// <param name="expectedRevision">调用方 revision。</param>
    /// <param name="currentRevision">服务端当前 revision。</param>
    /// <param name="retryable">CRUD 为 true，run 绑定为 false。</param>
    /// <returns>固定 stale_agent_profile_revision 错误。</returns>
    public static AgentProfileException Stale(
        string profileId,
        long expectedRevision,
        long currentRevision,
        bool retryable)
    {
        return new AgentProfileException(new PortableError(
            -32010,
            "agent profile revision is stale",
            retryable,
            new ErrorDetails(
                "stale_agent_profile_revision",
                ExpectedRevision: expectedRevision,
                CurrentRevision: currentRevision,
                ResourceId: profileId)));
    }

    /// <summary>
    /// 功能：创建 profile 已禁用错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="profileId">已验证的 opaque profile ID。</param>
    /// <returns>固定 agent_profile_disabled 错误。</returns>
    public static AgentProfileException Disabled(string profileId)
    {
        return new AgentProfileException(new PortableError(
            -32003,
            "agent profile is disabled",
            false,
            new ErrorDetails("agent_profile_disabled", ResourceId: profileId)));
    }

    /// <summary>
    /// 功能：创建 run 与 profile 模型三元组不一致错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="model">profile 声明的完整模型身份。</param>
    /// <returns>固定 agent_profile_model_mismatch 错误。</returns>
    public static AgentProfileException ModelMismatch(AgentProfileModel model)
    {
        return new AgentProfileException(new PortableError(
            -32602,
            "run model does not match agent profile",
            false,
            new ErrorDetails(
                "agent_profile_model_mismatch",
                ProviderId: model.ProviderId,
                ModelId: model.ModelId,
                ApiFamily: model.ApiFamily)));
    }

    /// <summary>
    /// 功能：创建 profile 模型路由在当前启动快照不可执行错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="model">profile 声明的完整模型身份。</param>
    /// <returns>固定 agent_profile_model_unavailable 错误。</returns>
    public static AgentProfileException ModelUnavailable(AgentProfileModel model)
    {
        return new AgentProfileException(new PortableError(
            -32005,
            "agent profile model is unavailable",
            true,
            new ErrorDetails(
                "agent_profile_model_unavailable",
                ProviderId: model.ProviderId,
                ModelId: model.ModelId,
                ApiFamily: model.ApiFamily)));
    }
}

/// <summary>
/// 功能：通过 EF Core application database 提供 Agent Profile CRUD 与 run 快照绑定。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class AgentProfileService
{
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private static readonly HashSet<string> ResponseStyles =
        new(["concise", "balanced", "detailed"], StringComparer.Ordinal);
    private static readonly HashSet<string> DangerousModes =
        new(["ask", "deny"], StringComparer.Ordinal);
    private static readonly HashSet<string> ReadOnlyToolIds =
        new(["file.read", "search.text"], StringComparer.Ordinal);

    private readonly IDbContextFactory<ApplicationDbContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly HashSet<AgentProfileModel> availableModels;
    private readonly HashSet<string> configuredToolIds;

    /// <summary>
    /// 功能：使用系统 UTC 时钟创建 production Agent Profile 服务。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="contextFactory">每次操作提供独立 context 的可信 factory。</param>
    /// <param name="availableModels">当前 models/list 的完整三元身份快照。</param>
    /// <param name="configuredToolIds">当前 initialize 广告的工具 ID 快照。</param>
    public AgentProfileService(
        IDbContextFactory<ApplicationDbContext> contextFactory,
        IReadOnlyCollection<AgentProfileModel> availableModels,
        IReadOnlyCollection<string> configuredToolIds)
        : this(contextFactory, availableModels, configuredToolIds, TimeProvider.System)
    {
    }

    /// <summary>
    /// 功能：使用可信可注入时钟创建 Agent Profile 服务。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="contextFactory">每次操作提供独立 context 的可信 factory。</param>
    /// <param name="availableModels">当前 models/list 的完整三元身份快照。</param>
    /// <param name="configuredToolIds">当前 initialize 广告的工具 ID 快照。</param>
    /// <param name="timeProvider">仅由宿主或测试注入的 UTC 时钟。</param>
    public AgentProfileService(
        IDbContextFactory<ApplicationDbContext> contextFactory,
        IReadOnlyCollection<AgentProfileModel> availableModels,
        IReadOnlyCollection<string> configuredToolIds,
        TimeProvider timeProvider)
    {
        this.contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        ArgumentNullException.ThrowIfNull(availableModels);
        ArgumentNullException.ThrowIfNull(configuredToolIds);
        this.availableModels = new HashSet<AgentProfileModel>(availableModels);
        this.configuredToolIds = new HashSet<string>(configuredToolIds, StringComparer.Ordinal);
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// 功能：列出全部 profile 的独立快照。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="cancellationToken">数据库查询取消信号。</param>
    /// <returns>按 updatedAt 倒序、profileId 升序排列的 profile。</returns>
    public async Task<IReadOnlyList<AgentProfile>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnsureNoOrphanToolsAsync(context, cancellationToken).ConfigureAwait(false);
        var entities = await context.AgentProfiles
            .AsNoTracking()
            .Include(profile => profile.Tools)
            .OrderByDescending(profile => profile.UpdatedAt)
            .ThenBy(profile => profile.ProfileId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var result = entities.Select(MapEntity).ToArray();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// 功能：校验完整输入并创建 revision 1 的 profile。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="input">来自 application service 的不可信完整输入。</param>
    /// <param name="cancellationToken">数据库写入取消信号。</param>
    /// <returns>已提交的完整 profile。</returns>
    /// <exception cref="AgentProfileException">任一字段不符合共同 schema。</exception>
    public async Task<AgentProfile> CreateAsync(
        AgentProfileInput input,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeInput(input);
        var now = UtcText(timeProvider.GetUtcNow());
        var entity = new AgentProfileEntity
        {
            ProfileId = "profile-" + Guid.NewGuid().ToString("N"),
            Revision = 1,
            DisplayName = normalized.DisplayName,
            Description = normalized.Description,
            Enabled = normalized.Enabled,
            Instructions = normalized.Instructions,
            ProviderId = normalized.Model.ProviderId,
            ModelId = normalized.Model.ModelId,
            ApiFamily = normalized.Model.ApiFamily,
            DangerousActionMode = normalized.DangerousActionMode,
            ResponseStyle = normalized.Behavior.ResponseStyle,
            PlanFirst = normalized.Behavior.PlanFirst,
            ReviewChanges = normalized.Behavior.ReviewChanges,
            CreatedAt = now,
            UpdatedAt = now,
        };
        AddTools(entity, normalized.RequestedToolIds);
        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        context.AgentProfiles.Add(entity);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return MapEntity(entity);
    }

    /// <summary>
    /// 功能：按 expected revision 完整替换 profile 并把 revision 原子递增 1。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="profileId">稳定 opaque profile ID。</param>
    /// <param name="expectedRevision">调用方读取到的 CAS revision。</param>
    /// <param name="input">来自 application service 的不可信完整输入。</param>
    /// <param name="cancellationToken">数据库写入取消信号。</param>
    /// <returns>已提交的新 revision profile。</returns>
    /// <exception cref="AgentProfileException">ID、revision、字段或并发状态无效。</exception>
    public async Task<AgentProfile> UpdateAsync(
        string profileId,
        long expectedRevision,
        AgentProfileInput input,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(profileId, expectedRevision);
        var normalized = NormalizeInput(input);
        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var entity = await context.AgentProfiles
            .Include(profile => profile.Tools)
            .SingleOrDefaultAsync(profile => profile.ProfileId == profileId, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            throw AgentProfileException.NotFound(profileId);
        }

        if (entity.Revision is < 1 or > MaxSafeInteger)
        {
            throw AgentProfileException.StorageCorrupt();
        }

        if (entity.Revision != expectedRevision)
        {
            throw AgentProfileException.Stale(profileId, expectedRevision, entity.Revision, retryable: true);
        }

        if (entity.Revision == MaxSafeInteger)
        {
            throw AgentProfileException.RevisionExhausted(profileId);
        }

        context.AgentProfileTools.RemoveRange(entity.Tools);
        entity.Tools.Clear();
        ApplyInput(entity, normalized);
        entity.Revision = checked(entity.Revision + 1);
        entity.UpdatedAt = UtcText(timeProvider.GetUtcNow());
        AddTools(entity, normalized.RequestedToolIds);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw await ResolveConcurrencyAsync(
                profileId,
                expectedRevision,
                cancellationToken).ConfigureAwait(false);
        }

        return MapEntity(entity);
    }

    /// <summary>
    /// 功能：仅在 expected revision 匹配时原子删除 profile 与工具关联。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="profileId">稳定 opaque profile ID。</param>
    /// <param name="expectedRevision">调用方读取到的 CAS revision。</param>
    /// <param name="cancellationToken">数据库写入取消信号。</param>
    /// <returns>删除 durable 后完成的任务。</returns>
    /// <exception cref="AgentProfileException">ID、revision 或并发状态无效。</exception>
    public async Task DeleteAsync(
        string profileId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(profileId, expectedRevision);
        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var entity = await context.AgentProfiles
            .SingleOrDefaultAsync(profile => profile.ProfileId == profileId, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            throw AgentProfileException.NotFound(profileId);
        }

        if (entity.Revision is < 1 or > MaxSafeInteger)
        {
            throw AgentProfileException.StorageCorrupt();
        }

        if (entity.Revision != expectedRevision)
        {
            throw AgentProfileException.Stale(profileId, expectedRevision, entity.Revision, retryable: true);
        }

        context.AgentProfiles.Remove(entity);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw await ResolveConcurrencyAsync(
                profileId,
                expectedRevision,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 功能：在任何 Session append 前解析并冻结 run 所需 profile revision。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="reference">run/start 提供的精确 profile 引用。</param>
    /// <param name="provider">已补全 API family 的 run 模型选择。</param>
    /// <param name="configuredToolIds">当前 initialize 广告的工具能力上限。</param>
    /// <param name="cancellationToken">数据库查询取消信号。</param>
    /// <returns>不可变 journal snapshot 与请求期 system instructions。</returns>
    /// <remarks>不变量：工具只取 requested 与 registry 交集；deny 再限制为只读工具。</remarks>
    /// <exception cref="AgentProfileException">缺失、stale、禁用或模型不匹配。</exception>
    public async Task<AgentProfileRunBinding> ResolveRunBindingAsync(
        AgentProfileReference reference,
        ProviderSelection provider,
        IReadOnlyCollection<string> configuredToolIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(configuredToolIds);
        ValidateReference(reference.ProfileId, reference.Revision);
        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnsureNoOrphanToolsAsync(context, cancellationToken).ConfigureAwait(false);
        var entity = await context.AgentProfiles
            .AsNoTracking()
            .Include(profile => profile.Tools)
            .SingleOrDefaultAsync(
                profile => profile.ProfileId == reference.ProfileId,
                cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            throw AgentProfileException.NotFound(reference.ProfileId);
        }

        if (entity.Revision is < 1 or > MaxSafeInteger)
        {
            throw AgentProfileException.StorageCorrupt();
        }

        if (entity.Revision != reference.Revision)
        {
            throw AgentProfileException.Stale(
                reference.ProfileId,
                reference.Revision,
                entity.Revision,
                retryable: false);
        }

        if (!entity.Enabled)
        {
            throw AgentProfileException.Disabled(reference.ProfileId);
        }

        var profile = MapEntity(entity);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(provider.Id, profile.Model.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(provider.ModelId, profile.Model.ModelId, StringComparison.Ordinal) ||
            !string.Equals(provider.ApiFamily, profile.Model.ApiFamily, StringComparison.Ordinal))
        {
            throw AgentProfileException.ModelMismatch(profile.Model);
        }

        var configured = configuredToolIds.ToHashSet(StringComparer.Ordinal);
        IEnumerable<string> effective = profile.RequestedToolIds.Where(configured.Contains);
        if (profile.DangerousActionMode == "deny")
        {
            effective = effective.Where(ReadOnlyToolIds.Contains);
        }

        var snapshot = new AgentProfileRunSnapshot(
            profile.ProfileId,
            profile.Revision,
            profile.DisplayName,
            profile.Instructions,
            profile.Model,
            profile.RequestedToolIds,
            effective.Order(StringComparer.Ordinal).ToArray(),
            profile.DangerousActionMode,
            profile.Behavior);
        return new AgentProfileRunBinding(snapshot, CreateSystemInstructions(snapshot));
    }

    /// <summary>
    /// 功能：校验并规范化完整 profile 输入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="input">不可信输入。</param>
    /// <param name="requireAvailableCapabilities">是否要求模型与工具命中当前启动能力快照。</param>
    /// <returns>trim、去重验证且工具按 ordinal 排序的输入。</returns>
    /// <exception cref="AgentProfileException">任一共同 schema 约束失败。</exception>
    private AgentProfileInput NormalizeInput(
        AgentProfileInput input,
        bool requireAvailableCapabilities = true)
    {
        if (input is null || input.Model is null || input.Behavior is null || input.RequestedToolIds is null)
        {
            throw AgentProfileException.Invalid();
        }

        var displayName = NormalizeText(input.DisplayName, 1, 48);
        var description = NormalizeText(input.Description, 0, 160);
        var instructions = NormalizeText(input.Instructions, 1, 12000);
        var providerId = ValidateIdentityText(input.Model.ProviderId, 1, 128);
        var modelId = ValidateIdentityText(input.Model.ModelId, 1, 256);
        var apiFamily = ValidateIdentityText(input.Model.ApiFamily, 1, 128);
        var model = new AgentProfileModel(providerId, modelId, apiFamily);
        if (!IsProviderId(providerId) || !IsApiFamily(apiFamily) ||
            (requireAvailableCapabilities && !availableModels.Contains(model)) ||
            !DangerousModes.Contains(input.DangerousActionMode) ||
            !ResponseStyles.Contains(input.Behavior.ResponseStyle) ||
            input.RequestedToolIds.Count > 256)
        {
            throw AgentProfileException.Invalid();
        }

        var tools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var toolId in input.RequestedToolIds)
        {
            if (!IsToolId(toolId) ||
                (requireAvailableCapabilities && !configuredToolIds.Contains(toolId)) ||
                !tools.Add(toolId))
            {
                throw AgentProfileException.Invalid();
            }
        }

        return new AgentProfileInput(
            displayName,
            description,
            input.Enabled,
            instructions,
            model,
            tools.Order(StringComparer.Ordinal).ToArray(),
            input.DangerousActionMode,
            input.Behavior);
    }

    /// <summary>
    /// 功能：trim 并按 Unicode scalar 数验证文本长度。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">不可信字符串。</param>
    /// <param name="minimum">允许的最小 scalar 数。</param>
    /// <param name="maximum">允许的最大 scalar 数。</param>
    /// <returns>trim 后文本。</returns>
    /// <exception cref="AgentProfileException">null 或长度越界。</exception>
    private static string NormalizeText(string value, int minimum, int maximum)
    {
        if (value is null)
        {
            throw AgentProfileException.Invalid();
        }

        var rawLength = value.EnumerateRunes().Count();
        if (rawLength > maximum)
        {
            throw AgentProfileException.Invalid();
        }

        var normalized = value.Trim();
        if (normalized.EnumerateRunes().Count() < minimum)
        {
            throw AgentProfileException.Invalid();
        }

        return normalized;
    }

    /// <summary>
    /// 功能：按原始 Unicode scalar 长度验证模型身份并拒绝首尾空白。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">不得被 trim 或改写的不可信身份。</param>
    /// <param name="minimum">允许的最小 scalar 数。</param>
    /// <param name="maximum">允许的最大 scalar 数。</param>
    /// <returns>与输入逐字符相同的身份文本。</returns>
    /// <exception cref="AgentProfileException">null、长度越界或存在边缘 Unicode 空白。</exception>
    private static string ValidateIdentityText(string value, int minimum, int maximum)
    {
        if (value is null)
        {
            throw AgentProfileException.Invalid();
        }

        var length = value.EnumerateRunes().Count();
        if (length < minimum || length > maximum ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw AgentProfileException.Invalid();
        }

        return value;
    }

    /// <summary>
    /// 功能：验证 Provider ID 的受限 ASCII 语法。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">已完成长度验证的文本。</param>
    /// <returns>匹配共同 schema pattern 时为 true。</returns>
    private static bool IsProviderId(string value)
    {
        return IsLowerAsciiLetterOrDigit(value[0]) &&
            value.All(static character => IsLowerAsciiLetterOrDigit(character) || character is '.' or '-');
    }

    /// <summary>
    /// 功能：验证 API family 的受限 ASCII 语法。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">已完成长度验证的文本。</param>
    /// <returns>匹配共同 schema pattern 时为 true。</returns>
    private static bool IsApiFamily(string value)
    {
        return IsLowerAsciiLetterOrDigit(value[0]) &&
            value.All(static character => IsLowerAsciiLetterOrDigit(character) || character == '-');
    }

    /// <summary>
    /// 功能：验证工具 ID 的受限 ASCII 语法与长度。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">不可信工具 ID。</param>
    /// <returns>匹配共同 schema pattern 时为 true。</returns>
    private static bool IsToolId(string? value)
    {
        return value is { Length: >= 1 and <= 128 } &&
            value[0] is >= 'a' and <= 'z' &&
            value.All(static character =>
                IsLowerAsciiLetterOrDigit(character) || character is '_' or '.' or '-');
    }

    /// <summary>
    /// 功能：判断字符是否为小写 ASCII 字母或数字。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="character">待检查字符。</param>
    /// <returns>受限字符时为 true。</returns>
    private static bool IsLowerAsciiLetterOrDigit(char character)
    {
        return character is >= 'a' and <= 'z' or >= '0' and <= '9';
    }

    /// <summary>
    /// 功能：验证 profile opaque ID 与正安全整数 revision。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="profileId">不可信 profile ID。</param>
    /// <param name="revision">不可信 revision。</param>
    /// <exception cref="AgentProfileException">任一引用字段不符合共同 schema。</exception>
    private static void ValidateReference(string profileId, long revision)
    {
        if (string.IsNullOrEmpty(profileId) || profileId.Length > 128 ||
            !char.IsAsciiLetterOrDigit(profileId[0]) ||
            profileId.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')) ||
            revision is < 1 or > MaxSafeInteger)
        {
            throw AgentProfileException.Invalid();
        }
    }

    /// <summary>
    /// 功能：把规范化输入复制到现有 EF 聚合根，不修改身份、revision 或时间。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="entity">已追踪聚合根。</param>
    /// <param name="input">已规范化输入。</param>
    private static void ApplyInput(AgentProfileEntity entity, AgentProfileInput input)
    {
        entity.DisplayName = input.DisplayName;
        entity.Description = input.Description;
        entity.Enabled = input.Enabled;
        entity.Instructions = input.Instructions;
        entity.ProviderId = input.Model.ProviderId;
        entity.ModelId = input.Model.ModelId;
        entity.ApiFamily = input.Model.ApiFamily;
        entity.DangerousActionMode = input.DangerousActionMode;
        entity.ResponseStyle = input.Behavior.ResponseStyle;
        entity.PlanFirst = input.Behavior.PlanFirst;
        entity.ReviewChanges = input.Behavior.ReviewChanges;
    }

    /// <summary>
    /// 功能：向 EF 聚合根加入已排序且唯一的工具关联行。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="entity">目标聚合根。</param>
    /// <param name="toolIds">已验证工具 ID。</param>
    private static void AddTools(AgentProfileEntity entity, IReadOnlyList<string> toolIds)
    {
        foreach (var toolId in toolIds)
        {
            entity.Tools.Add(new AgentProfileToolEntity
            {
                ProfileId = entity.ProfileId,
                ToolId = toolId,
                Profile = entity,
            });
        }
    }

    /// <summary>
    /// 功能：把数据库聚合根映射为防御性 application-service DTO。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="entity">已加载工具关联的聚合根。</param>
    /// <returns>数组与嵌套对象均独立的 profile。</returns>
    private AgentProfile MapEntity(AgentProfileEntity entity)
    {
        try
        {
            ValidateReference(entity.ProfileId, entity.Revision);
            var toolIds = entity.Tools.Select(static tool => tool.ToolId).ToArray();
            if (entity.Tools.Any(tool =>
                    !string.Equals(tool.ProfileId, entity.ProfileId, StringComparison.Ordinal)))
            {
                throw AgentProfileException.Invalid();
            }

            var normalized = NormalizeInput(
                new AgentProfileInput(
                    entity.DisplayName,
                    entity.Description,
                    entity.Enabled,
                    entity.Instructions,
                    new AgentProfileModel(entity.ProviderId, entity.ModelId, entity.ApiFamily),
                    toolIds,
                    entity.DangerousActionMode,
                    new AgentProfileBehavior(
                        entity.ResponseStyle,
                        entity.PlanFirst,
                        entity.ReviewChanges)),
                requireAvailableCapabilities: false);
            if (!string.Equals(normalized.DisplayName, entity.DisplayName, StringComparison.Ordinal) ||
                !string.Equals(normalized.Description, entity.Description, StringComparison.Ordinal) ||
                !string.Equals(normalized.Instructions, entity.Instructions, StringComparison.Ordinal) ||
                normalized.RequestedToolIds.Count != toolIds.Length)
            {
                throw AgentProfileException.Invalid();
            }

            return new AgentProfile(
                entity.ProfileId,
                entity.Revision,
                normalized.DisplayName,
                normalized.Description,
                normalized.Enabled,
                normalized.Instructions,
                normalized.Model,
                normalized.RequestedToolIds,
                normalized.DangerousActionMode,
                normalized.Behavior,
                ParseUtc(entity.CreatedAt),
                ParseUtc(entity.UpdatedAt));
        }
        catch (AgentProfileException)
        {
            throw AgentProfileException.StorageCorrupt();
        }
        catch (InvalidOperationException)
        {
            throw AgentProfileException.StorageCorrupt();
        }
    }

    /// <summary>
    /// 功能：把 UTC 时点格式化为固定毫秒精度和 Z 后缀。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">待持久化时点。</param>
    /// <returns>可按 ordinal 排序的 UTC RFC 3339 文本。</returns>
    private static string UtcText(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 功能：严格解析由应用数据库保存的 UTC 时间。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">数据库时间文本。</param>
    /// <returns>零偏移时点。</returns>
    /// <exception cref="InvalidOperationException">数据库时间不满足固定格式。</exception>
    private static DateTimeOffset ParseUtc(string value)
    {
        if (value is null)
        {
            throw new InvalidOperationException("Agent profile timestamp is invalid.");
        }

        var fractionLength = value.Length >= 20 && value[^1] == 'Z'
            ? value.Length == 20
                ? 0
                : value.Length >= 22 && value[19] == '.'
                    ? value.Length - 21
                    : -1
            : -1;
        var hasFixedShape = value.Length >= 20 &&
            value[4] == '-' &&
            value[7] == '-' &&
            value[10] == 'T' &&
            value[13] == ':' &&
            value[16] == ':' &&
            value[..4].All(char.IsAsciiDigit) &&
            value[5..7].All(char.IsAsciiDigit) &&
            value[8..10].All(char.IsAsciiDigit) &&
            value[11..13].All(char.IsAsciiDigit) &&
            value[14..16].All(char.IsAsciiDigit) &&
            value[17..19].All(char.IsAsciiDigit) &&
            fractionLength is >= 0 and <= 9 &&
            (fractionLength == 0 || value.AsSpan(20, fractionLength).ToArray().All(char.IsAsciiDigit));
        if (!hasFixedShape)
        {
            throw new InvalidOperationException("Agent profile timestamp is invalid.");
        }

        var parseValue = fractionLength > 7
            ? value[..27] + "Z"
            : value;
        var format = fractionLength == 0
            ? "yyyy-MM-dd'T'HH:mm:ss'Z'"
            : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";
        if (!DateTimeOffset.TryParseExact(
                parseValue,
                format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var result))
        {
            throw new InvalidOperationException("Agent profile timestamp is invalid.");
        }

        return result;
    }

    /// <summary>
    /// 功能：在当前读事务内拒绝没有父 Profile 的工具关联行。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">已进入一致读事务的独立 EF context。</param>
    /// <param name="cancellationToken">孤儿检测查询取消信号。</param>
    /// <returns>不存在 orphan tool row 时完成的任务。</returns>
    /// <exception cref="AgentProfileException">检测到损坏关联时返回脱敏 storage corrupt。</exception>
    private static async Task EnsureNoOrphanToolsAsync(
        ApplicationDbContext context,
        CancellationToken cancellationToken)
    {
        var hasOrphan = await context.AgentProfileTools
            .AsNoTracking()
            .AnyAsync(
                tool => !context.AgentProfiles.Any(profile =>
                    profile.ProfileId == tool.ProfileId),
                cancellationToken)
            .ConfigureAwait(false);
        if (hasOrphan)
        {
            throw AgentProfileException.StorageCorrupt();
        }
    }

    /// <summary>
    /// 功能：在 EF CAS 失败后用新 context 读取当前 revision 并构造稳定错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="profileId">发生冲突的 profile ID。</param>
    /// <param name="expectedRevision">原 CAS revision。</param>
    /// <param name="cancellationToken">冲突查询取消信号。</param>
    /// <returns>缺失或 stale portable exception。</returns>
    private async Task<AgentProfileException> ResolveConcurrencyAsync(
        string profileId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var currentRevision = await context.AgentProfiles
            .AsNoTracking()
            .Where(profile => profile.ProfileId == profileId)
            .Select(profile => (long?)profile.Revision)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (currentRevision is null)
        {
            return AgentProfileException.NotFound(profileId);
        }

        return currentRevision is < 1 or > MaxSafeInteger
            ? AgentProfileException.StorageCorrupt()
            : AgentProfileException.Stale(
                profileId,
                expectedRevision,
                currentRevision.Value,
                retryable: true);
    }

    /// <summary>
    /// 功能：以跨实现固定顺序组合 instructions 与行为偏好。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="snapshot">已验证 run 快照。</param>
    /// <returns>仅 Provider 请求期使用、不会追加普通 Session message 的文本。</returns>
    private static string CreateSystemInstructions(AgentProfileRunSnapshot snapshot)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{snapshot.Instructions}\n\nBehavior preferences:\n" +
            $"responseStyle={snapshot.Behavior.ResponseStyle}\n" +
            $"planFirst={snapshot.Behavior.PlanFirst.ToString().ToLowerInvariant()}\n" +
            $"reviewChanges={snapshot.Behavior.ReviewChanges.ToString().ToLowerInvariant()}");
    }
}
