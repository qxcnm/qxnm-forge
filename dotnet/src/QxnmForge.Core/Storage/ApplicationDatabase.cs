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
            SchemaVersion = "0.1",
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
        if (!string.Equals(SchemaVersion, "0.1", StringComparison.Ordinal) ||
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
public sealed class ForgeMetadata
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
    public DbSet<ForgeMetadata> Metadata => Set<ForgeMetadata>();

    /// <summary>
    /// 功能：把语言无关的逻辑表名和键约束映射到当前 provider。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="modelBuilder">EF Core model builder。</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ForgeMetadata>(entity =>
        {
            entity.ToTable("forge_metadata");
            entity.HasKey(item => item.Key);
            entity.Property(item => item.Key).HasColumnName("key").HasMaxLength(128);
            entity.Property(item => item.Value).HasColumnName("value").IsRequired();
        });
    }
}

/// <summary>
/// 功能：在 secret 最后读取边界创建 provider 专属 EF Core context。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public static class ApplicationDatabaseFactory
{
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
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection.ToString())
            .EnableSensitiveDataLogging(false)
            .Options;
        var context = new ApplicationDbContext(options);
        try
        {
            await BootstrapAsync(context, cancellationToken).ConfigureAwait(false);
            return context;
        }
        catch
        {
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 功能：建立应用 schema 并以幂等方式写入共同逻辑版本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">待初始化的应用数据库 context。</param>
    /// <param name="cancellationToken">取消 schema 或写入操作。</param>
    /// <returns>表示 bootstrap 完成的任务。</returns>
    /// <exception cref="DbUpdateException">建表或版本记录持久化失败。</exception>
    public static async Task BootstrapAsync(
        ApplicationDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var metadata = await context.Metadata
            .SingleOrDefaultAsync(item => item.Key == "schema_version", cancellationToken)
            .ConfigureAwait(false);
        if (metadata is null)
        {
            context.Metadata.Add(new ForgeMetadata { Key = "schema_version", Value = "0.1" });
        }
        else
        {
            metadata.Value = "0.1";
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
