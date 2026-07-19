using Microsoft.EntityFrameworkCore;
using QxnmForge.Storage;
using QxnmForge.Serialization;
using System.Text.Json;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 EF Core 应用数据库配置、默认 SQLite 和远程 provider 关闭边界。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ApplicationDatabaseTests
{
    /// <summary>
    /// 功能：验证默认配置创建 application.db 并写入共同 schema 版本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task DefaultSqliteBootstrapsMetadata()
    {
        using var directory = new TemporaryDirectory();
        var configuration = DatabaseConfiguration.ForStateRoot(directory.Path);

        await using var context = await ApplicationDatabaseFactory.OpenAsync(configuration);

        Assert.Equal(DatabaseProvider.Sqlite, configuration.Provider);
        Assert.Equal(Path.Combine(directory.Path, "application.db"), configuration.SqlitePath);
        Assert.True(File.Exists(configuration.SqlitePath));
        var metadata = await context.Metadata.AsNoTracking().SingleAsync(item => item.Key == "schema_version");
        Assert.Equal("0.1", metadata.Value);
    }

    /// <summary>
    /// 功能：验证相对状态目录在创建配置前即被拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void RelativeStateRootIsRejected()
    {
        Assert.Throws<ArgumentException>(() => DatabaseConfiguration.ForStateRoot("relative-state"));
    }

    /// <summary>
    /// 功能：验证预留远程 provider 不会在 driver 未启用时读取或连接 secret。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ReservedRemoteProviderFailsClosed()
    {
        var configuration = new DatabaseConfiguration
        {
            SchemaVersion = "0.1",
            Provider = DatabaseProvider.PostgreSql,
            ConnectionEnvironment = "QXNM_FORGE_DATABASE_URL",
            MaxConnections = 8,
        };

        await Assert.ThrowsAsync<NotSupportedException>(() => ApplicationDatabaseFactory.OpenAsync(configuration));
    }

    /// <summary>
    /// 功能：验证 .NET 数据库 provider 名称与共享配置 schema 完全一致。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void ProviderNamesMatchWireSchema()
    {
        Assert.Equal("\"sqlite\"", JsonSerializer.Serialize(DatabaseProvider.Sqlite, JsonDefaults.Options));
        Assert.Equal("\"postgresql\"", JsonSerializer.Serialize(DatabaseProvider.PostgreSql, JsonDefaults.Options));
        Assert.Equal("\"mysql\"", JsonSerializer.Serialize(DatabaseProvider.MySql, JsonDefaults.Options));
    }
}
