using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace QxnmForge.Storage;

/// <summary>
/// 功能：标识应用产品数据使用的数据库 provider；portable Session journal 不受其影响。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DatabaseProvider>))]
public enum DatabaseProvider
{
    [JsonStringEnumMemberName("sqlite")]
    Sqlite,
    [JsonStringEnumMemberName("postgresql")]
    PostgreSql,
    [JsonStringEnumMemberName("mysql")]
    MySql,
}

/// <summary>
/// 功能：描述不直接携带远程数据库 secret 的应用数据库配置。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class DatabaseConfiguration
{
    /// <summary>
    /// 功能：声明当前应用数据库 schema 版本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public const string CurrentSchemaVersion = "0.2";

    /// <summary>
    /// 功能：取得当前配置 schema 版本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public required string SchemaVersion { get; init; }

    /// <summary>
    /// 功能：取得选定的数据库 provider。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public required DatabaseProvider Provider { get; init; }

    /// <summary>
    /// 功能：取得 SQLite 文件绝对路径；远程 provider 时必须为空。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string? SqlitePath { get; init; }

    /// <summary>
    /// 功能：取得远程连接串的环境变量名称；本字段不是连接串本身。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string? ConnectionEnvironment { get; init; }

    /// <summary>
    /// 功能：取得为未来 provider pool 预留的最大连接数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public required int MaxConnections { get; init; }

    /// <summary>
    /// 功能：为给定用户状态根创建默认 SQLite 配置。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="stateRoot">工作区外的应用状态根绝对路径。</param>
    /// <returns>指向 <c>application.db</c> 的配置，不读取 secret 或创建文件。</returns>
    /// <exception cref="ArgumentException">状态根不是绝对路径。</exception>
    public static DatabaseConfiguration ForStateRoot(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        if (!Path.IsPathFullyQualified(stateRoot))
        {
            throw new ArgumentException("Database state root must be absolute.", nameof(stateRoot));
        }

        return new DatabaseConfiguration
        {
            SchemaVersion = CurrentSchemaVersion,
            Provider = DatabaseProvider.Sqlite,
            SqlitePath = Path.Combine(stateRoot, "application.db"),
            MaxConnections = 8,
        };
    }

    /// <summary>
    /// 功能：严格验证 provider 专属字段、连接池范围和 secret 来源互斥。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <remarks>不变量：SQLite 不接受连接串环境变量；远程 provider 不接受本地路径。</remarks>
    /// <exception cref="InvalidOperationException">版本、字段组合、路径或环境变量名称无效。</exception>
    public void Validate()
    {
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal) ||
            MaxConnections is < 1 or > 128)
        {
            throw new InvalidOperationException("Database configuration is invalid.");
        }

        if (Provider == DatabaseProvider.Sqlite)
        {
            if (string.IsNullOrWhiteSpace(SqlitePath) ||
                !Path.IsPathFullyQualified(SqlitePath) ||
                ConnectionEnvironment is not null)
            {
                throw new InvalidOperationException("SQLite configuration is invalid.");
            }

            return;
        }

        if (SqlitePath is not null || !IsValidEnvironmentName(ConnectionEnvironment))
        {
            throw new InvalidOperationException("Remote database configuration is invalid.");
        }
    }

    /// <summary>
    /// 功能：验证远程连接串环境变量是受限 ASCII 名称。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">待验证名称。</param>
    /// <returns>名称可安全交给环境读取边界时为 <see langword="true"/>。</returns>
    private static bool IsValidEnvironmentName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128)
        {
            return false;
        }

        return (value[0] is '_' || value[0] is >= 'A' and <= 'Z') &&
            value.All(character =>
                character is '_' ||
                character is >= 'A' and <= 'Z' ||
                character is >= '0' and <= '9');
    }
}

/// <summary>
/// 功能：表示应用数据库的稳定 schema 元数据行。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ApplicationMetadata
{
    /// <summary>
    /// 功能：取得或设置元数据键。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public required string Key { get; set; }

    /// <summary>
    /// 功能：取得或设置元数据值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public required string Value { get; set; }
}

/// <summary>
/// 功能：表示应用数据库中的品牌中立 Agent Profile 聚合根。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class AgentProfileEntity
{
    /// <summary>
    /// 功能：取得或设置稳定 profile ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public required string ProfileId { get; set; }

    /// <summary>
    /// 功能：取得或设置用于乐观并发控制的单调 revision。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public long Revision { get; set; }

    /// <summary>
    /// 功能：取得或设置用户可见名称。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public required string DisplayName { get; set; }

    /// <summary>
    /// 功能：取得或设置用户可见说明。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public required string Description { get; set; }

    /// <summary>
    /// 功能：取得或设置 profile 是否允许绑定新 run。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 功能：取得或设置仅在 Provider 请求边界使用的系统指令。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public required string Instructions { get; set; }

    /// <summary>
    /// 功能：取得或设置完整 Provider ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public required string ProviderId { get; set; }

    /// <summary>
    /// 功能：取得或设置完整模型 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public required string ModelId { get; set; }

    /// <summary>
    /// 功能：取得或设置完整 API family。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public required string ApiFamily { get; set; }

    /// <summary>
    /// 功能：取得或设置危险操作处理模式。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public required string DangerousActionMode { get; set; }

    /// <summary>
    /// 功能：取得或设置回复风格偏好。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public required string ResponseStyle { get; set; }

    /// <summary>
    /// 功能：取得或设置是否偏好先给计划。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public bool PlanFirst { get; set; }

    /// <summary>
    /// 功能：取得或设置是否偏好完成后审阅变更。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public bool ReviewChanges { get; set; }

    /// <summary>
    /// 功能：取得或设置创建时间的 UTC RFC 3339 文本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public required string CreatedAt { get; set; }

    /// <summary>
    /// 功能：取得或设置最近更新时间的 UTC RFC 3339 文本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public required string UpdatedAt { get; set; }

    /// <summary>
    /// 功能：取得当前 profile 请求的规范化工具关联行。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public ICollection<AgentProfileToolEntity> Tools { get; } = [];
}

/// <summary>
/// 功能：表示 Agent Profile 请求的单个工具 ID。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class AgentProfileToolEntity
{
    /// <summary>
    /// 功能：取得或设置所属 profile ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public required string ProfileId { get; set; }

    /// <summary>
    /// 功能：取得或设置品牌中立工具 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public required string ToolId { get; set; }

    /// <summary>
    /// 功能：取得或设置所属 profile 导航属性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public AgentProfileEntity? Profile { get; set; }
}

/// <summary>
/// 功能：承载 QXNM Forge 产品层数据的 EF Core context。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ApplicationDbContext : DbContext
{
    /// <summary>
    /// 功能：使用已经由可信 factory 构造的选项创建应用数据库 context。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="options">不包含可回显 secret 的 EF Core 配置。</param>
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// 功能：取得应用 schema 版本元数据集合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public DbSet<ApplicationMetadata> Metadata => Set<ApplicationMetadata>();

    /// <summary>
    /// 功能：取得 Agent Profile 聚合根集合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public DbSet<AgentProfileEntity> AgentProfiles => Set<AgentProfileEntity>();

    /// <summary>
    /// 功能：取得 Agent Profile 工具关联集合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public DbSet<AgentProfileToolEntity> AgentProfileTools => Set<AgentProfileToolEntity>();

    /// <summary>
    /// 功能：把语言无关的逻辑表名和键约束映射到当前 provider。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="modelBuilder">EF Core model builder。</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ApplicationMetadata>(entity =>
        {
            entity.ToTable("application_metadata");
            entity.HasKey(item => item.Key);
            entity.Property(item => item.Key).HasColumnName("key").HasMaxLength(128);
            entity.Property(item => item.Value).HasColumnName("value").IsRequired();
        });
        modelBuilder.Entity<AgentProfileEntity>(entity =>
        {
            entity.ToTable("agent_profiles");
            entity.HasKey(item => item.ProfileId);
            entity.Property(item => item.ProfileId).HasColumnName("profile_id").HasMaxLength(128);
            entity.Property(item => item.Revision).HasColumnName("revision").IsConcurrencyToken();
            entity.Property(item => item.DisplayName).HasColumnName("display_name").HasMaxLength(48);
            entity.Property(item => item.Description).HasColumnName("description").HasMaxLength(160);
            entity.Property(item => item.Enabled).HasColumnName("enabled");
            entity.Property(item => item.Instructions).HasColumnName("instructions").HasMaxLength(12000);
            entity.Property(item => item.ProviderId).HasColumnName("provider_id").HasMaxLength(128);
            entity.Property(item => item.ModelId).HasColumnName("model_id").HasMaxLength(256);
            entity.Property(item => item.ApiFamily).HasColumnName("api_family").HasMaxLength(128);
            entity.Property(item => item.DangerousActionMode).HasColumnName("dangerous_action_mode").HasMaxLength(8);
            entity.Property(item => item.ResponseStyle).HasColumnName("response_style").HasMaxLength(8);
            entity.Property(item => item.PlanFirst).HasColumnName("plan_first");
            entity.Property(item => item.ReviewChanges).HasColumnName("review_changes");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at").HasMaxLength(32);
            entity.Property(item => item.UpdatedAt).HasColumnName("updated_at").HasMaxLength(32);
            entity.HasIndex(item => new { item.UpdatedAt, item.ProfileId })
                .HasDatabaseName("idx_agent_profiles_list");
        });
        modelBuilder.Entity<AgentProfileToolEntity>(entity =>
        {
            entity.ToTable("agent_profile_tools");
            entity.HasKey(item => new { item.ProfileId, item.ToolId });
            entity.Property(item => item.ProfileId).HasColumnName("profile_id").HasMaxLength(128);
            entity.Property(item => item.ToolId).HasColumnName("tool_id").HasMaxLength(128);
            entity.HasOne(item => item.Profile)
                .WithMany(profile => profile.Tools)
                .HasForeignKey(item => item.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>
/// 功能：为每次应用服务操作创建独立 EF Core context。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ApplicationDbContextFactory : IDbContextFactory<ApplicationDbContext>
{
    private readonly DbContextOptions<ApplicationDbContext> options;

    /// <summary>
    /// 功能：保存不含可回显 secret 的不可变 EF Core 选项。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="options">已绑定单一应用数据库的选项。</param>
    internal ApplicationDbContextFactory(DbContextOptions<ApplicationDbContext> options)
    {
        this.options = options;
    }

    /// <summary>
    /// 功能：同步创建一个由调用方独占并释放的 context。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>未共享 change tracker 的新 context。</returns>
    public ApplicationDbContext CreateDbContext()
    {
        return new ApplicationDbContext(options);
    }

    /// <summary>
    /// 功能：异步 factory 合约下创建一个由调用方独占并释放的 context。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="cancellationToken">创建前的取消信号。</param>
    /// <returns>未共享 change tracker 的新 context。</returns>
    public ValueTask<ApplicationDbContext> CreateDbContextAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CreateDbContext());
    }
}

/// <summary>
/// 功能：在 secret 最后读取边界创建 provider 专属 EF Core context。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public static class ApplicationDatabaseFactory
{
    private const string CreateApplicationMetadataSql = """
        CREATE TABLE application_metadata (
            key TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    private const string CreateProfilesSql = """
        CREATE TABLE agent_profiles (
            profile_id TEXT NOT NULL PRIMARY KEY,
            revision INTEGER NOT NULL CHECK (revision >= 1 AND revision <= 9007199254740991),
            display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 48),
            description TEXT NOT NULL CHECK (length(description) <= 160),
            enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
            instructions TEXT NOT NULL CHECK (length(instructions) BETWEEN 1 AND 12000),
            provider_id TEXT NOT NULL CHECK (length(provider_id) BETWEEN 1 AND 128),
            model_id TEXT NOT NULL CHECK (length(model_id) BETWEEN 1 AND 256),
            api_family TEXT NOT NULL CHECK (length(api_family) BETWEEN 1 AND 128),
            dangerous_action_mode TEXT NOT NULL CHECK (dangerous_action_mode IN ('ask', 'deny')),
            response_style TEXT NOT NULL CHECK (response_style IN ('concise', 'balanced', 'detailed')),
            plan_first INTEGER NOT NULL CHECK (plan_first IN (0, 1)),
            review_changes INTEGER NOT NULL CHECK (review_changes IN (0, 1)),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE TABLE agent_profile_tools (
            profile_id TEXT NOT NULL,
            tool_id TEXT NOT NULL CHECK (length(tool_id) BETWEEN 1 AND 128),
            PRIMARY KEY (profile_id, tool_id),
            FOREIGN KEY (profile_id) REFERENCES agent_profiles(profile_id) ON DELETE CASCADE
        );
        CREATE INDEX idx_agent_profiles_list
            ON agent_profiles(updated_at DESC, profile_id ASC);
        """;

    /// <summary>
    /// 功能：创建已 bootstrap 且可并发安全地产生独立 context 的 factory。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="configuration">不直接携带远程 secret 的严格配置。</param>
    /// <param name="cancellationToken">取消目录、连接和 schema migration。</param>
    /// <returns>绑定当前 SQLite 数据库的 context factory。</returns>
    /// <exception cref="InvalidOperationException">配置、目录或 schema 版本无效。</exception>
    /// <exception cref="NotSupportedException">PostgreSQL/MySQL adapter 尚未启用。</exception>
    public static async Task<ApplicationDbContextFactory> OpenFactoryAsync(
        DatabaseConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var factory = CreateFactory(configuration);
        await using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await BootstrapAsync(context, cancellationToken).ConfigureAwait(false);
        return factory;
    }

    /// <summary>
    /// 功能：打开默认 SQLite 数据库并建立最小版本元数据。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="configuration">不直接携带远程 secret 的严格配置。</param>
    /// <param name="cancellationToken">取消目录、连接和 bootstrap 操作。</param>
    /// <returns>由调用方异步释放、已完成 bootstrap 的 context。</returns>
    /// <remarks>不变量：当前构建在读取远程连接串之前明确拒绝未启用 provider。</remarks>
    /// <exception cref="InvalidOperationException">配置或 SQLite 目录无效。</exception>
    /// <exception cref="NotSupportedException">PostgreSQL/MySQL adapter 尚未启用。</exception>
    public static async Task<ApplicationDbContext> OpenAsync(
        DatabaseConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var factory = await OpenFactoryAsync(configuration, cancellationToken).ConfigureAwait(false);
        return await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：验证配置并构造尚未访问数据库的 SQLite context factory。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="configuration">不直接携带远程 secret 的严格配置。</param>
    /// <returns>绑定唯一 SQLite 路径的 factory。</returns>
    /// <exception cref="InvalidOperationException">配置或 SQLite 目录无效。</exception>
    /// <exception cref="NotSupportedException">远程 provider adapter 尚未启用。</exception>
    private static ApplicationDbContextFactory CreateFactory(DatabaseConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        if (configuration.Provider != DatabaseProvider.Sqlite)
        {
            throw new NotSupportedException("Remote database provider is reserved but not enabled.");
        }

        var sqlitePath = configuration.SqlitePath!;
        var parent = Path.GetDirectoryName(sqlitePath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException("SQLite path is invalid.");
        }

        Directory.CreateDirectory(parent);
        var connection = new SqliteConnectionStringBuilder
        {
            DataSource = sqlitePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection.ToString())
            .EnableSensitiveDataLogging(false)
            .Options;
        return new ApplicationDbContextFactory(options);
    }

    /// <summary>
    /// 功能：建立应用 schema，并在单事务中完成已知 0.1 到 0.2 migration。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">待初始化的应用数据库 context。</param>
    /// <param name="cancellationToken">取消 schema 或写入操作。</param>
    /// <returns>表示 bootstrap 完成的任务。</returns>
    /// <remarks>不变量：未知版本原样保留并被拒绝；版本只在所有 DDL 成功后更新。</remarks>
    /// <exception cref="InvalidOperationException">数据库声明未知或不完整 schema 版本。</exception>
    /// <exception cref="DbUpdateException">建表或版本记录持久化失败。</exception>
    public static async Task BootstrapAsync(
        ApplicationDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var objects = await ReadSchemaObjectsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var hasLegacyMetadata = objects.Contains("table:forge_metadata");
        var hasApplicationMetadata = objects.Contains("table:application_metadata");
        if (hasLegacyMetadata && hasApplicationMetadata)
        {
            throw new InvalidOperationException("Application database contains conflicting metadata tables.");
        }

        if (!hasLegacyMetadata && !hasApplicationMetadata)
        {
            if (objects.Count != 0)
            {
                throw new InvalidOperationException("Application database has no recognized schema metadata.");
            }

            await ExecuteNonQueryAsync(
                connection,
                transaction,
                CreateApplicationMetadataSql + CreateProfilesSql +
                "INSERT INTO application_metadata(key, value) VALUES ('schema_version', '0.2');",
                cancellationToken).ConfigureAwait(false);
        }
        else if (hasLegacyMetadata)
        {
            var version = await ReadSchemaVersionAsync(
                connection,
                transaction,
                "forge_metadata",
                cancellationToken).ConfigureAwait(false);
            if (version != "0.1")
            {
                throw new InvalidOperationException("Application database schema version is unsupported.");
            }

            await ExecuteNonQueryAsync(
                connection,
                transaction,
                CreateApplicationMetadataSql +
                "INSERT INTO application_metadata(key, value) SELECT key, value FROM forge_metadata;" +
                CreateProfilesSql +
                "UPDATE application_metadata SET value = '0.2' WHERE key = 'schema_version';" +
                "DROP TABLE forge_metadata;",
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var version = await ReadSchemaVersionAsync(
                connection,
                transaction,
                "application_metadata",
                cancellationToken).ConfigureAwait(false);
            if (version != DatabaseConfiguration.CurrentSchemaVersion ||
                !objects.Contains("table:agent_profiles") ||
                !objects.Contains("table:agent_profile_tools") ||
                !objects.Contains("index:idx_agent_profiles_list"))
            {
                throw new InvalidOperationException("Application database schema is unsupported or incomplete.");
            }

            await ValidateCurrentSchemaAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：读取当前事务可见的全部非 SQLite 内部 schema 对象名称。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connection">已打开的 SQLite 连接。</param>
    /// <param name="transaction">保护 bootstrap 的立即事务。</param>
    /// <param name="cancellationToken">查询取消信号。</param>
    /// <returns>以 <c>type:name</c> 表示的 table、index、trigger 与 view ordinal 集合。</returns>
    private static async Task<HashSet<string>> ReadSchemaObjectsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT type || ':' || name
            FROM sqlite_master
            WHERE type IN ('table', 'index', 'trigger', 'view')
              AND name NOT LIKE 'sqlite_%';
            """;
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>
    /// 功能：严格验证已声明 0.2 数据库的列、CAS CHECK、复合键、级联外键和列表索引。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connection">已打开的 SQLite 连接。</param>
    /// <param name="transaction">保护启动检查的一致事务。</param>
    /// <param name="cancellationToken">PRAGMA 与 sqlite_master 查询取消信号。</param>
    /// <returns>全部结构与共同 schema 精确匹配时完成的任务。</returns>
    /// <exception cref="InvalidOperationException">任一结构缺失、错序、放宽或指向错误对象。</exception>
    private static async Task ValidateCurrentSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await RequireTableColumnsAsync(
            connection,
            transaction,
            "application_metadata",
            [
                ("key", "TEXT", 1L, 1L),
                ("value", "TEXT", 1L, 0L),
            ],
            cancellationToken).ConfigureAwait(false);
        await RequireTableColumnsAsync(
            connection,
            transaction,
            "agent_profiles",
            [
                ("profile_id", "TEXT", 1L, 1L),
                ("revision", "INTEGER", 1L, 0L),
                ("display_name", "TEXT", 1L, 0L),
                ("description", "TEXT", 1L, 0L),
                ("enabled", "INTEGER", 1L, 0L),
                ("instructions", "TEXT", 1L, 0L),
                ("provider_id", "TEXT", 1L, 0L),
                ("model_id", "TEXT", 1L, 0L),
                ("api_family", "TEXT", 1L, 0L),
                ("dangerous_action_mode", "TEXT", 1L, 0L),
                ("response_style", "TEXT", 1L, 0L),
                ("plan_first", "INTEGER", 1L, 0L),
                ("review_changes", "INTEGER", 1L, 0L),
                ("created_at", "TEXT", 1L, 0L),
                ("updated_at", "TEXT", 1L, 0L),
            ],
            cancellationToken).ConfigureAwait(false);
        await RequireTableColumnsAsync(
            connection,
            transaction,
            "agent_profile_tools",
            [
                ("profile_id", "TEXT", 1L, 1L),
                ("tool_id", "TEXT", 1L, 2L),
            ],
            cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT sql FROM sqlite_master WHERE type='table' AND name='agent_profiles';";
            var profileSql = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            var compactSql = profileSql is null
                ? string.Empty
                : string.Concat(profileSql.Where(static character => !char.IsWhiteSpace(character)))
                    .ToLowerInvariant();
            if (!compactSql.Contains(
                    "revisionintegernotnullcheck(revision>=1andrevision<=9007199254740991)",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Application database schema is unsupported or incomplete.");
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "PRAGMA foreign_key_list(agent_profile_tools);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var foreignKeys = new List<(string Table, string From, string To, string OnDelete)>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                foreignKeys.Add((
                    reader.GetString(reader.GetOrdinal("table")),
                    reader.GetString(reader.GetOrdinal("from")),
                    reader.GetString(reader.GetOrdinal("to")),
                    reader.GetString(reader.GetOrdinal("on_delete"))));
            }

            if (foreignKeys.Count != 1 ||
                foreignKeys[0] != ("agent_profiles", "profile_id", "profile_id", "CASCADE"))
            {
                throw new InvalidOperationException("Application database schema is unsupported or incomplete.");
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type='index'
                  AND name='idx_agent_profiles_list'
                  AND tbl_name='agent_profiles';
                """;
            if (Convert.ToInt64(
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidOperationException("Application database schema is unsupported or incomplete.");
            }

            command.CommandText = "PRAGMA index_xinfo(idx_agent_profiles_list);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var columns = new List<(string Name, long Descending)>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetInt64(reader.GetOrdinal("key")) == 1)
                {
                    columns.Add((
                        reader.GetString(reader.GetOrdinal("name")),
                        reader.GetInt64(reader.GetOrdinal("desc"))));
                }
            }

            if (!columns.SequenceEqual(
                    [("updated_at", 1L), ("profile_id", 0L)]))
            {
                throw new InvalidOperationException("Application database schema is unsupported or incomplete.");
            }
        }
    }

    /// <summary>
    /// 功能：要求已知表具有精确列顺序、SQLite affinity、NOT NULL 与主键位置。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connection">已打开的 SQLite 连接。</param>
    /// <param name="transaction">保护启动检查的一致事务。</param>
    /// <param name="tableName">仅允许共同 schema 的三个固定表名。</param>
    /// <param name="expected">按声明顺序排列的精确列元数据。</param>
    /// <param name="cancellationToken">PRAGMA 查询取消信号。</param>
    /// <returns>表结构精确匹配时完成的任务。</returns>
    /// <exception cref="InvalidOperationException">表名未知或列元数据不匹配。</exception>
    private static async Task RequireTableColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        IReadOnlyList<(string Name, string Type, long NotNull, long PrimaryKey)> expected,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = tableName switch
        {
            "application_metadata" => "PRAGMA table_info(application_metadata);",
            "agent_profiles" => "PRAGMA table_info(agent_profiles);",
            "agent_profile_tools" => "PRAGMA table_info(agent_profile_tools);",
            _ => throw new InvalidOperationException("Application database schema is unsupported or incomplete."),
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var actual = new List<(string Name, string Type, long NotNull, long PrimaryKey)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            actual.Add((
                reader.GetString(reader.GetOrdinal("name")),
                reader.GetString(reader.GetOrdinal("type")).ToUpperInvariant(),
                reader.GetInt64(reader.GetOrdinal("notnull")),
                reader.GetInt64(reader.GetOrdinal("pk"))));
        }

        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidOperationException("Application database schema is unsupported or incomplete.");
        }
    }

    /// <summary>
    /// 功能：从受信任的已知 metadata 表读取唯一 schema_version。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connection">已打开的 SQLite 连接。</param>
    /// <param name="transaction">保护 bootstrap 的立即事务。</param>
    /// <param name="tableName">仅允许两个版本迁移已知表名。</param>
    /// <param name="cancellationToken">查询取消信号。</param>
    /// <returns>版本文本；缺失时为 null。</returns>
    /// <exception cref="ArgumentOutOfRangeException">调用方传入未知表名。</exception>
    private static async Task<string?> ReadSchemaVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = tableName switch
        {
            "forge_metadata" => "SELECT value FROM forge_metadata WHERE key = 'schema_version';",
            "application_metadata" => "SELECT value FROM application_metadata WHERE key = 'schema_version';",
            _ => throw new ArgumentOutOfRangeException(nameof(tableName)),
        };
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result as string;
    }

    /// <summary>
    /// 功能：在 bootstrap 事务中执行固定、不含外部输入的 DDL/DML 批次。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="connection">已打开的 SQLite 连接。</param>
    /// <param name="transaction">保护 bootstrap 的立即事务。</param>
    /// <param name="sql">源码内固定 SQL。</param>
    /// <param name="cancellationToken">执行取消信号。</param>
    /// <returns>执行完成任务。</returns>
    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
