using Microsoft.Data.Sqlite;
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
    private const string CurrentSchemaSql = """
        CREATE TABLE application_metadata (
            key TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL
        );
        INSERT INTO application_metadata(key, value) VALUES ('schema_version', '0.2');
        CREATE TABLE agent_profiles (
            profile_id TEXT NOT NULL PRIMARY KEY,
            revision INTEGER NOT NULL CHECK (revision >= 1 AND revision <= 9007199254740991),
            display_name TEXT NOT NULL,
            description TEXT NOT NULL,
            enabled INTEGER NOT NULL,
            instructions TEXT NOT NULL,
            provider_id TEXT NOT NULL,
            model_id TEXT NOT NULL,
            api_family TEXT NOT NULL,
            dangerous_action_mode TEXT NOT NULL,
            response_style TEXT NOT NULL,
            plan_first INTEGER NOT NULL,
            review_changes INTEGER NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE TABLE agent_profile_tools (
            profile_id TEXT NOT NULL,
            tool_id TEXT NOT NULL,
            PRIMARY KEY (profile_id, tool_id),
            FOREIGN KEY (profile_id) REFERENCES agent_profiles(profile_id) ON DELETE CASCADE
        );
        CREATE INDEX idx_agent_profiles_list
            ON agent_profiles(updated_at DESC, profile_id ASC);
        """;

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
        Assert.Equal("0.2", metadata.Value);
        Assert.Equal(0, await context.AgentProfiles.CountAsync());
        Assert.Equal(0, await context.AgentProfileTools.CountAsync());
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
            SchemaVersion = "0.2",
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

    /// <summary>
    /// 功能：验证 0.1 forge_metadata 在单次 bootstrap 中迁移并删除品牌相关持久化标识。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task LegacyMetadataMigratesTransactionallyToVersionZeroPointTwo()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "application.db");
        await using (var connection = new SqliteConnection("Data Source=" + path))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE forge_metadata (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO forge_metadata(key, value) VALUES ('schema_version', '0.1');
                INSERT INTO forge_metadata(key, value) VALUES ('migration_marker', 'preserved');
                """;
            _ = await command.ExecuteNonQueryAsync();
        }

        await using var context = await ApplicationDatabaseFactory.OpenAsync(
            DatabaseConfiguration.ForStateRoot(directory.Path));

        Assert.Equal(
            "0.2",
            (await context.Metadata.AsNoTracking().SingleAsync(item => item.Key == "schema_version")).Value);
        Assert.Equal(
            "preserved",
            (await context.Metadata.AsNoTracking().SingleAsync(item => item.Key == "migration_marker")).Value);
        await using var verify = new SqliteConnection("Data Source=" + path);
        await verify.OpenAsync();
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='forge_metadata';";
        Assert.Equal(0L, (long)(await verifyCommand.ExecuteScalarAsync())!);
    }

    /// <summary>
    /// 功能：验证未来 schema 版本不会被启动流程覆盖或降级。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task FutureSchemaVersionFailsClosedWithoutMutation()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "application.db");
        await using (var connection = new SqliteConnection("Data Source=" + path))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE application_metadata (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO application_metadata(key, value) VALUES ('schema_version', '9.9');
                """;
            _ = await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApplicationDatabaseFactory.OpenAsync(DatabaseConfiguration.ForStateRoot(directory.Path)));

        await using var verify = new SqliteConnection("Data Source=" + path);
        await verify.OpenAsync();
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = "SELECT value FROM application_metadata WHERE key='schema_version';";
        Assert.Equal("9.9", (string)(await verifyCommand.ExecuteScalarAsync())!);
    }

    /// <summary>
    /// 功能：验证声明 0.2 但缺少 Agent Profile 对象的数据库不会被静默修复。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task IncompleteCurrentSchemaFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "application.db");
        await using (var connection = new SqliteConnection("Data Source=" + path))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE application_metadata (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO application_metadata(key, value) VALUES ('schema_version', '0.2');
                """;
            _ = await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApplicationDatabaseFactory.OpenAsync(DatabaseConfiguration.ForStateRoot(directory.Path)));
    }

    /// <summary>
    /// 功能：验证伪造同名对象不能绕过列、revision、复合键、外键或索引结构检查。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task MalformedCurrentSchemaObjectsFailClosed()
    {
        var malformedSchemas = new[]
        {
            CurrentSchemaSql.Replace(
                "revision INTEGER NOT NULL CHECK (revision >= 1 AND revision <= 9007199254740991)",
                "revision INTEGER NOT NULL CHECK (revision >= 1)",
                StringComparison.Ordinal),
            CurrentSchemaSql.Replace(
                "description TEXT NOT NULL",
                "description TEXT",
                StringComparison.Ordinal),
            CurrentSchemaSql.Replace(
                "PRIMARY KEY (profile_id, tool_id)",
                "PRIMARY KEY (tool_id, profile_id)",
                StringComparison.Ordinal),
            CurrentSchemaSql.Replace(
                "ON DELETE CASCADE",
                "ON DELETE RESTRICT",
                StringComparison.Ordinal),
            CurrentSchemaSql.Replace(
                "ON agent_profiles(updated_at DESC, profile_id ASC)",
                "ON agent_profiles(profile_id ASC, updated_at DESC)",
                StringComparison.Ordinal),
        };

        using var directory = new TemporaryDirectory();
        for (var index = 0; index < malformedSchemas.Length; index++)
        {
            var stateRoot = Directory.CreateDirectory(
                Path.Combine(directory.Path, "malformed-" + index)).FullName;
            await using (var connection = new SqliteConnection(
                             "Data Source=" + Path.Combine(stateRoot, "application.db")))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = malformedSchemas[index];
                _ = await command.ExecuteNonQueryAsync();
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ApplicationDatabaseFactory.OpenAsync(DatabaseConfiguration.ForStateRoot(stateRoot)));
        }
    }

    /// <summary>
    /// 功能：验证仅含用户 view 的未版本数据库不会被误判为空数据库并自动建表。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task UnversionedUserSchemaObjectFailsClosedWithoutMutation()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "application.db");
        await using (var connection = new SqliteConnection("Data Source=" + path))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE VIEW existing_data AS SELECT 1 AS value;";
            _ = await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApplicationDatabaseFactory.OpenAsync(DatabaseConfiguration.ForStateRoot(directory.Path)));

        await using var verify = new SqliteConnection("Data Source=" + path);
        await verify.OpenAsync();
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = """
            SELECT group_concat(type || ':' || name, ',')
            FROM sqlite_master
            WHERE name NOT LIKE 'sqlite_%';
            """;
        Assert.Equal("view:existing_data", (string)(await verifyCommand.ExecuteScalarAsync())!);
    }

    /// <summary>
    /// 功能：验证 metadata 表缺少唯一 schema_version 时启动失败且保留原有行。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task MetadataWithoutSchemaVersionFailsClosedWithoutMutation()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "application.db");
        await using (var connection = new SqliteConnection("Data Source=" + path))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE application_metadata (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO application_metadata(key, value) VALUES ('marker', 'preserved');
                """;
            _ = await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApplicationDatabaseFactory.OpenAsync(DatabaseConfiguration.ForStateRoot(directory.Path)));

        await using var verify = new SqliteConnection("Data Source=" + path);
        await verify.OpenAsync();
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = "SELECT key || ':' || value FROM application_metadata;";
        Assert.Equal("marker:preserved", (string)(await verifyCommand.ExecuteScalarAsync())!);
    }

    /// <summary>
    /// 功能：验证新旧 metadata 表并存时拒绝歧义且不删除、迁移或改写任一版本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ConflictingMetadataTablesFailClosedWithoutMutation()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "application.db");
        await using (var connection = new SqliteConnection("Data Source=" + path))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE forge_metadata (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO forge_metadata(key, value) VALUES ('schema_version', '0.1');
                CREATE TABLE application_metadata (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO application_metadata(key, value) VALUES ('schema_version', '0.2');
                """;
            _ = await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApplicationDatabaseFactory.OpenAsync(DatabaseConfiguration.ForStateRoot(directory.Path)));

        await using var verify = new SqliteConnection("Data Source=" + path);
        await verify.OpenAsync();
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = """
            SELECT group_concat(name || ':' || version, ',')
            FROM (
                SELECT 'application_metadata' AS name, value AS version
                FROM application_metadata WHERE key='schema_version'
                UNION ALL
                SELECT 'forge_metadata' AS name, value AS version
                FROM forge_metadata WHERE key='schema_version'
                ORDER BY name
            );
            """;
        Assert.Equal(
            "application_metadata:0.2,forge_metadata:0.1",
            (string)(await verifyCommand.ExecuteScalarAsync())!);
    }
}
