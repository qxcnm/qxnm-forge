using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QxnmForge.Agent;
using QxnmForge.Domain;
using QxnmForge.Storage;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 EF Core Agent Profile CRUD、CAS、重启恢复与 run 快照收窄。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class AgentProfileServiceTests
{
    private static readonly AgentProfileModel[] AvailableModels =
        [new("faux", "faux-v1", "faux")];
    private static readonly string[] ConfiguredToolIds =
        ["file.read", "file.write", "future.tool", "search.text", "shell.exec"];

    /// <summary>
    /// 功能：验证 profile 可跨 factory 重开恢复，update/delete 严格执行 CAS 和级联清理。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CrudPersistsAcrossReopenAndRejectsStaleRevision()
    {
        using var directory = new TemporaryDirectory();
        var configuration = DatabaseConfiguration.ForStateRoot(directory.Path);
        var factory = await ApplicationDatabaseFactory.OpenFactoryAsync(configuration);
        var service = CreateService(factory);

        var created = await service.CreateAsync(CreateInput());

        Assert.StartsWith("profile-", created.ProfileId, StringComparison.Ordinal);
        Assert.Equal(1, created.Revision);
        Assert.Equal("Repository reviewer", created.DisplayName);
        Assert.Equal(["file.read", "file.write"], created.RequestedToolIds);
        Assert.Equal(TimeSpan.Zero, created.CreatedAt.Offset);

        var reopenedFactory = await ApplicationDatabaseFactory.OpenFactoryAsync(configuration);
        var reopened = CreateService(reopenedFactory);
        var persisted = Assert.Single(await reopened.ListAsync());
        Assert.Equal(created.ProfileId, persisted.ProfileId);
        Assert.Equal(created.Revision, persisted.Revision);
        Assert.Equal(created.DisplayName, persisted.DisplayName);
        Assert.Equal(created.Model, persisted.Model);
        Assert.Equal(created.RequestedToolIds, persisted.RequestedToolIds);
        Assert.Equal(created.CreatedAt, persisted.CreatedAt);

        var updated = await reopened.UpdateAsync(
            created.ProfileId,
            created.Revision,
            CreateInput(description: "Updated description"));
        Assert.Equal(2, updated.Revision);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        Assert.Equal("Updated description", updated.Description);

        var staleUpdate = await Assert.ThrowsAsync<AgentProfileException>(() => reopened.UpdateAsync(
            created.ProfileId,
            created.Revision,
            CreateInput(description: "Must not win")));
        Assert.Equal(-32010, staleUpdate.Error.Code);
        Assert.True(staleUpdate.Error.Retryable);
        Assert.Equal("stale_agent_profile_revision", staleUpdate.Error.Details.Kind);
        Assert.Equal(1, staleUpdate.Error.Details.ExpectedRevision);
        Assert.Equal(2, staleUpdate.Error.Details.CurrentRevision);
        Assert.Equal("Updated description", Assert.Single(await reopened.ListAsync()).Description);

        await Assert.ThrowsAsync<AgentProfileException>(() => reopened.DeleteAsync(
            created.ProfileId,
            created.Revision));
        await reopened.DeleteAsync(created.ProfileId, updated.Revision);
        Assert.Empty(await reopened.ListAsync());
        await using var context = await reopenedFactory.CreateDbContextAsync();
        Assert.Equal(0, await context.AgentProfileTools.CountAsync());
        Assert.True(context.Model.FindEntityType(typeof(AgentProfileEntity))!
            .FindProperty(nameof(AgentProfileEntity.Revision))!
            .IsConcurrencyToken);
    }

    /// <summary>
    /// 功能：验证 run 绑定冻结 revision、匹配完整模型并双层收窄工具集合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task RunBindingFreezesProfileAndNarrowsTools()
    {
        using var directory = new TemporaryDirectory();
        var factory = await ApplicationDatabaseFactory.OpenFactoryAsync(
            DatabaseConfiguration.ForStateRoot(directory.Path));
        var service = CreateService(factory);
        var ask = await service.CreateAsync(CreateInput(
            requestedToolIds: ["shell.exec", "future.tool", "file.read", "file.write"]));

        var binding = await service.ResolveRunBindingAsync(
            new AgentProfileReference(ask.ProfileId, ask.Revision),
            new ProviderSelection("faux", "faux-v1", "faux"),
            ["shell.exec", "file.read"]);

        Assert.Equal(["file.read", "shell.exec"], binding.Snapshot.EffectiveToolIds);
        Assert.Equal(
            "Inspect the requested change.\n\nBehavior preferences:\n" +
            "responseStyle=concise\nplanFirst=true\nreviewChanges=true",
            binding.SystemInstructions);

        var deny = await service.CreateAsync(CreateInput(
            dangerousActionMode: "deny",
            requestedToolIds: ["shell.exec", "file.read", "search.text"]));
        var denyBinding = await service.ResolveRunBindingAsync(
            new AgentProfileReference(deny.ProfileId, deny.Revision),
            new ProviderSelection("faux", "faux-v1", "faux"),
            ["shell.exec", "file.read", "search.text"]);
        Assert.Equal(["file.read", "search.text"], denyBinding.Snapshot.EffectiveToolIds);

        var mismatch = await Assert.ThrowsAsync<AgentProfileException>(() =>
            service.ResolveRunBindingAsync(
                new AgentProfileReference(ask.ProfileId, ask.Revision),
                new ProviderSelection("faux", "other", "faux"),
                []));
        Assert.Equal("agent_profile_model_mismatch", mismatch.Error.Details.Kind);

        var stale = await Assert.ThrowsAsync<AgentProfileException>(() =>
            service.ResolveRunBindingAsync(
                new AgentProfileReference(ask.ProfileId, ask.Revision + 1),
                new ProviderSelection("faux", "faux-v1", "faux"),
                []));
        Assert.Equal(-32010, stale.Error.Code);
        Assert.False(stale.Error.Retryable);

        var disabled = await service.CreateAsync(CreateInput(enabled: false));
        var disabledError = await Assert.ThrowsAsync<AgentProfileException>(() =>
            service.ResolveRunBindingAsync(
                new AgentProfileReference(disabled.ProfileId, disabled.Revision),
                new ProviderSelection("faux", "faux-v1", "faux"),
                []));
        Assert.Equal(-32003, disabledError.Error.Code);
        Assert.Equal("agent_profile_disabled", disabledError.Error.Details.Kind);
    }

    /// <summary>
    /// 功能：验证输入验证拒绝重复/非法工具和非法模型身份，且不会写入部分 profile。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task InvalidProfileInputFailsBeforeDatabaseMutation()
    {
        using var directory = new TemporaryDirectory();
        var factory = await ApplicationDatabaseFactory.OpenFactoryAsync(
            DatabaseConfiguration.ForStateRoot(directory.Path));
        var service = CreateService(factory);

        var duplicate = await Assert.ThrowsAsync<AgentProfileException>(() => service.CreateAsync(
            CreateInput(requestedToolIds: ["file.read", "file.read"])));
        Assert.Equal(-32602, duplicate.Error.Code);
        Assert.Equal("agent_profile_invalid", duplicate.Error.Details.Kind);
        Assert.Equal("profile", duplicate.Error.Details.Field);
        await Assert.ThrowsAsync<AgentProfileException>(() => service.CreateAsync(
            CreateInput(requestedToolIds: ["File.Read"])));
        await Assert.ThrowsAsync<AgentProfileException>(() => service.CreateAsync(
            CreateInput(model: new AgentProfileModel("Bad Provider", "faux-v1", "faux"))));
        await Assert.ThrowsAsync<AgentProfileException>(() => service.CreateAsync(
            CreateInput(model: new AgentProfileModel("faux ", "faux-v1", "faux"))));
        await Assert.ThrowsAsync<AgentProfileException>(() => service.CreateAsync(
            CreateInput(model: new AgentProfileModel("faux", " faux-v1 ", "faux"))));
        await Assert.ThrowsAsync<AgentProfileException>(() => service.CreateAsync(
            CreateInput(model: new AgentProfileModel("other", "faux-v1", "faux"))));
        await Assert.ThrowsAsync<AgentProfileException>(() => service.CreateAsync(
            CreateInput(requestedToolIds: ["unknown.tool"])));
        await Assert.ThrowsAsync<AgentProfileException>(() => service.CreateAsync(
            CreateInput(displayName: " " + new string('a', 48) + " ")));
        Assert.Empty(await service.ListAsync());
    }

    /// <summary>
    /// 功能：验证安全整数上限 revision 在任何字段或工具修改前返回固定 exhausted 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task UpdateRejectsExhaustedRevisionWithoutMutation()
    {
        using var directory = new TemporaryDirectory();
        var factory = await ApplicationDatabaseFactory.OpenFactoryAsync(
            DatabaseConfiguration.ForStateRoot(directory.Path));
        var service = CreateService(factory);
        var created = await service.CreateAsync(CreateInput());
        const long maximumRevision = 9_007_199_254_740_991;
        await using (var context = await factory.CreateDbContextAsync())
        {
            _ = await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE agent_profiles SET revision={maximumRevision} WHERE profile_id={created.ProfileId}");
        }

        var error = await Assert.ThrowsAsync<AgentProfileException>(() => service.UpdateAsync(
            created.ProfileId,
            maximumRevision,
            CreateInput(description: "Must not win", requestedToolIds: ["search.text"])));

        Assert.Equal(-32009, error.Error.Code);
        Assert.Equal("agent profile revision is exhausted", error.Error.Message);
        Assert.False(error.Error.Retryable);
        Assert.Equal("agent_profile_revision_exhausted", error.Error.Details.Kind);
        Assert.Equal(created.ProfileId, error.Error.Details.ResourceId);
        await using var verify = await factory.CreateDbContextAsync();
        var persisted = await verify.AgentProfiles
            .AsNoTracking()
            .Include(static profile => profile.Tools)
            .SingleAsync();
        Assert.Equal(maximumRevision, persisted.Revision);
        Assert.Equal(created.Description, persisted.Description);
        Assert.Equal(["file.read", "file.write"], persisted.Tools
            .Select(static tool => tool.ToolId)
            .Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// 功能：验证数据库中符合宽松 CHECK 但违反 wire 身份规则的行不会被投影。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CorruptPersistedProfileReturnsRedactedInternalError()
    {
        using var directory = new TemporaryDirectory();
        var factory = await ApplicationDatabaseFactory.OpenFactoryAsync(
            DatabaseConfiguration.ForStateRoot(directory.Path));
        var service = CreateService(factory);
        var created = await service.CreateAsync(CreateInput());
        await using (var context = await factory.CreateDbContextAsync())
        {
            _ = await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE agent_profiles SET provider_id={"faux "} WHERE profile_id={created.ProfileId}");
        }

        var error = await Assert.ThrowsAsync<AgentProfileException>(() => service.ListAsync());

        Assert.Equal(-32603, error.Error.Code);
        Assert.Equal("agent profile storage is corrupt", error.Error.Message);
        Assert.Equal("agent_profile_storage_corrupt", error.Error.Details.Kind);
        Assert.Null(error.Error.Details.ResourceId);
    }

    /// <summary>
    /// 功能：验证共享数据库中的严格 Z RFC3339 无小数和纳秒文本均可安全投影。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Rfc3339UtcTimestampsAcceptSupportedFractionPrecision()
    {
        using var directory = new TemporaryDirectory();
        var factory = await ApplicationDatabaseFactory.OpenFactoryAsync(
            DatabaseConfiguration.ForStateRoot(directory.Path));
        var service = CreateService(factory);
        var created = await service.CreateAsync(CreateInput());
        await using (var context = await factory.CreateDbContextAsync())
        {
            _ = await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE agent_profiles SET created_at={"2026-07-21T08:00:00Z"}, updated_at={"2026-07-21T08:00:00.123456789Z"} WHERE profile_id={created.ProfileId}");
        }

        var profile = Assert.Single(await service.ListAsync());

        Assert.Equal(
            new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.Zero),
            profile.CreatedAt);
        Assert.Equal(
            DateTimeOffset.Parse(
                "2026-07-21T08:00:00.1234567Z",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            profile.UpdatedAt);
    }

    /// <summary>
    /// 功能：验证 List 与 run 解析在同一读事务内拒绝孤立工具关联。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task OrphanToolRowsFailClosedForListAndRunBinding()
    {
        using var directory = new TemporaryDirectory();
        var configuration = DatabaseConfiguration.ForStateRoot(directory.Path);
        var factory = await ApplicationDatabaseFactory.OpenFactoryAsync(configuration);
        var service = CreateService(factory);
        var created = await service.CreateAsync(CreateInput());
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = configuration.SqlitePath,
            ForeignKeys = false,
            Pooling = false,
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys=OFF;
                INSERT INTO agent_profile_tools(profile_id, tool_id)
                VALUES ('missing-profile', 'file.read');
                """;
            _ = await command.ExecuteNonQueryAsync();
        }

        var listError = await Assert.ThrowsAsync<AgentProfileException>(() => service.ListAsync());
        var runError = await Assert.ThrowsAsync<AgentProfileException>(() =>
            service.ResolveRunBindingAsync(
                new AgentProfileReference(created.ProfileId, created.Revision),
                new ProviderSelection("faux", "faux-v1", "faux"),
                ["file.read"]));

        Assert.Equal("agent_profile_storage_corrupt", listError.Error.Details.Kind);
        Assert.Equal("agent_profile_storage_corrupt", runError.Error.Details.Kind);
    }

    /// <summary>
    /// 功能：以固定启动能力快照创建测试 Profile 服务。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="factory">已 bootstrap 的独立 context factory。</param>
    /// <returns>只允许 faux 模型与公开测试工具的服务。</returns>
    private static AgentProfileService CreateService(
        IDbContextFactory<ApplicationDbContext> factory)
    {
        return new AgentProfileService(factory, AvailableModels, ConfiguredToolIds);
    }

    /// <summary>
    /// 功能：创建供测试使用、完整满足共同 schema 的 profile 输入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="description">可覆盖的说明。</param>
    /// <param name="enabled">可覆盖的启用状态。</param>
    /// <param name="dangerousActionMode">可覆盖的危险模式。</param>
    /// <param name="requestedToolIds">可覆盖的工具请求。</param>
    /// <param name="model">可覆盖的模型身份。</param>
    /// <param name="displayName">可覆盖的原始显示名称。</param>
    /// <returns>新的独立输入 DTO。</returns>
    private static AgentProfileInput CreateInput(
        string description = "Reviews a workspace.",
        bool enabled = true,
        string dangerousActionMode = "ask",
        IReadOnlyList<string>? requestedToolIds = null,
        AgentProfileModel? model = null,
        string displayName = " Repository reviewer ")
    {
        return new AgentProfileInput(
            displayName,
            description,
            enabled,
            " Inspect the requested change. ",
            model ?? new AgentProfileModel("faux", "faux-v1", "faux"),
            requestedToolIds ?? ["file.write", "file.read"],
            dangerousActionMode,
            new AgentProfileBehavior("concise", PlanFirst: true, ReviewChanges: true));
    }
}
