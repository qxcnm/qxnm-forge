//! QXNM Forge 可替换应用数据库与默认 SQLite 启动层。
//!
//! 作者：高宏顺 <18272669457@163.com>

use std::path::{Path, PathBuf};

use sea_orm::sqlx::{Connection as _, Row as _};
use sea_orm::{
    ConnectOptions, ConnectionTrait, Database, DatabaseBackend, DatabaseConnection, DbErr,
};
use serde::{Deserialize, Serialize};

/// 应用数据库 provider；Session portable journal 仍保持独立格式。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum DatabaseProvider {
    #[serde(rename = "sqlite")]
    Sqlite,
    #[serde(rename = "postgresql")]
    PostgreSql,
    #[serde(rename = "mysql")]
    MySql,
}

/// 不直接包含远程数据库密码的数据库配置。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct DatabaseConfiguration {
    pub schema_version: String,
    pub provider: DatabaseProvider,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sqlite_path: Option<PathBuf>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub connection_environment: Option<String>,
    pub max_connections: u32,
}

impl DatabaseConfiguration {
    /// 功能：为给定用户状态根创建默认 SQLite 配置。
    ///
    /// 输入：工作区外的应用状态根。
    /// 输出：指向 `application.db`、最多 8 个连接的配置。
    /// 不变量：不读取环境 secret，也不创建数据库文件。
    /// 失败：本方法不失败；路径安全由连接边界再次验证。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn sqlite_default(state_root: &Path) -> Self {
        Self {
            schema_version: "0.2".to_owned(),
            provider: DatabaseProvider::Sqlite,
            sqlite_path: Some(state_root.join("application.db")),
            connection_environment: None,
            max_connections: 8,
        }
    }

    /// 功能：验证 provider 专属字段、连接池上限和 secret 来源互斥。
    ///
    /// 输出：配置可交给 ORM 时成功。
    /// 不变量：SQLite 不接受 connection environment；远程 provider 不接受本地路径。
    /// 失败：版本、字段组合、相对路径或环境变量名无效时返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn validate(&self) -> Result<(), DbErr> {
        if self.schema_version != "0.2" || !(1..=128).contains(&self.max_connections) {
            return Err(DbErr::Custom(
                "database configuration is invalid".to_owned(),
            ));
        }
        match self.provider {
            DatabaseProvider::Sqlite => {
                if !self
                    .sqlite_path
                    .as_ref()
                    .is_some_and(|path| path.is_absolute())
                    || self.connection_environment.is_some()
                {
                    return Err(DbErr::Custom("sqlite configuration is invalid".to_owned()));
                }
            }
            DatabaseProvider::PostgreSql | DatabaseProvider::MySql => {
                let valid_environment = self
                    .connection_environment
                    .as_deref()
                    .is_some_and(valid_environment_name);
                if self.sqlite_path.is_some() || !valid_environment {
                    return Err(DbErr::Custom(
                        "remote database configuration is invalid".to_owned(),
                    ));
                }
            }
        }
        Ok(())
    }
}

/// 功能：通过 SeaORM 连接默认 SQLite，并建立最小版本元数据表。
///
/// 输入：已验证的数据库配置；远程连接串只从指定环境变量在此刻读取。
/// 输出：完成 bootstrap 的 SeaORM `DatabaseConnection`。
/// 不变量：配置和错误不回显连接串；当前构建只启用 SQLite driver。
/// 失败：配置、目录、secret、driver、连接或 bootstrap 失败时返回 `DbErr`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub async fn connect_application_database(
    configuration: &DatabaseConfiguration,
) -> Result<DatabaseConnection, DbErr> {
    configuration.validate()?;
    let connection_string = match configuration.provider {
        DatabaseProvider::Sqlite => {
            let path = configuration
                .sqlite_path
                .as_ref()
                .ok_or_else(|| DbErr::Custom("sqlite configuration is invalid".to_owned()))?;
            let parent = path
                .parent()
                .ok_or_else(|| DbErr::Custom("sqlite path is invalid".to_owned()))?;
            std::fs::create_dir_all(parent)
                .map_err(|_| DbErr::Custom("sqlite directory creation failed".to_owned()))?;
            format!("sqlite://{}?mode=rwc", path.display())
        }
        DatabaseProvider::PostgreSql | DatabaseProvider::MySql => {
            return Err(DbErr::Custom(
                "remote database provider is reserved but not enabled".to_owned(),
            ));
        }
    };
    let mut options = ConnectOptions::new(connection_string);
    options
        .max_connections(configuration.max_connections)
        .map_sqlx_sqlite_opts(|options| options.foreign_keys(true))
        .sqlx_logging(false);
    let connection = Database::connect(options).await?;
    bootstrap_metadata(&connection).await?;
    Ok(connection)
}

/// 功能：在单一事务内把应用数据库从逻辑 schema 0.1 迁移到 0.2。
///
/// 输入：已打开且启用 SQLite foreign keys 的应用数据库连接。
/// 输出：Profile 主表、规范化工具子表和 schema 0.2 元数据全部提交。
/// 不变量：版本标记最后写入；未知版本拒绝迁移；失败时不留下部分 0.2 schema。
/// 失败：backend、事务、DDL、版本读取或提交失败时返回 `DbErr`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn bootstrap_metadata(connection: &DatabaseConnection) -> Result<(), DbErr> {
    if connection.get_database_backend() != DatabaseBackend::Sqlite {
        return Err(DbErr::Custom(
            "remote database provider is reserved but not enabled".to_owned(),
        ));
    }
    let pool = connection.get_sqlite_connection_pool();
    let mut connection = pool.acquire().await.map_err(migration_error)?;
    sea_orm::sqlx::query("PRAGMA foreign_keys=ON")
        .execute(&mut *connection)
        .await
        .map_err(migration_error)?;
    let mut transaction = connection
        .begin_with("BEGIN IMMEDIATE")
        .await
        .map_err(migration_error)?;
    let forge_exists = sqlite_table_exists(&mut transaction, "forge_metadata").await?;
    let application_exists = sqlite_table_exists(&mut transaction, "application_metadata").await?;
    match (forge_exists, application_exists) {
        (false, false) => {
            let existing_objects = sea_orm::sqlx::query_scalar::<_, i64>(
                "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'",
            )
            .fetch_one(&mut *transaction)
            .await
            .map_err(migration_error)?;
            if existing_objects != 0 {
                return Err(invalid_migration_state());
            }
            create_application_schema(&mut transaction).await?;
            sea_orm::sqlx::query(
                "INSERT INTO application_metadata(key,value) VALUES('schema_version','0.2')",
            )
            .execute(&mut *transaction)
            .await
            .map_err(migration_error)?;
        }
        (true, false) => {
            require_schema_version(&mut transaction, "forge_metadata", "0.1").await?;
            sea_orm::sqlx::query(APPLICATION_METADATA_DDL)
                .execute(&mut *transaction)
                .await
                .map_err(migration_error)?;
            sea_orm::sqlx::query(
                "INSERT INTO application_metadata(key,value) SELECT key,value FROM forge_metadata",
            )
            .execute(&mut *transaction)
            .await
            .map_err(migration_error)?;
            create_profile_schema(&mut transaction).await?;
            let updated = sea_orm::sqlx::query(
                "UPDATE application_metadata SET value='0.2' WHERE key='schema_version'",
            )
            .execute(&mut *transaction)
            .await
            .map_err(migration_error)?;
            if updated.rows_affected() != 1 {
                return Err(invalid_migration_state());
            }
            sea_orm::sqlx::query("DROP TABLE forge_metadata")
                .execute(&mut *transaction)
                .await
                .map_err(migration_error)?;
        }
        (false, true) => {
            require_schema_version(&mut transaction, "application_metadata", "0.2").await?;
            validate_application_schema(&mut transaction).await?;
        }
        (true, true) => return Err(invalid_migration_state()),
    }
    require_schema_version(&mut transaction, "application_metadata", "0.2").await?;
    if sqlite_table_exists(&mut transaction, "forge_metadata").await? {
        return Err(invalid_migration_state());
    }
    transaction.commit().await.map_err(migration_error)
}

const APPLICATION_METADATA_DDL: &str =
    "CREATE TABLE application_metadata (key TEXT PRIMARY KEY NOT NULL,value TEXT NOT NULL)";
const AGENT_PROFILES_DDL: &str = concat!(
    "CREATE TABLE agent_profiles (",
    "profile_id TEXT PRIMARY KEY NOT NULL,",
    "revision INTEGER NOT NULL CHECK(revision>=1 AND revision<=9007199254740991),",
    "display_name TEXT NOT NULL CHECK(length(display_name) BETWEEN 1 AND 48),",
    "description TEXT NOT NULL CHECK(length(description)<=160),",
    "enabled INTEGER NOT NULL CHECK(enabled IN(0,1)),",
    "instructions TEXT NOT NULL CHECK(length(instructions) BETWEEN 1 AND 12000),",
    "provider_id TEXT NOT NULL,",
    "model_id TEXT NOT NULL,",
    "api_family TEXT NOT NULL,",
    "dangerous_action_mode TEXT NOT NULL CHECK(dangerous_action_mode IN('ask','deny')),",
    "response_style TEXT NOT NULL CHECK(response_style IN('concise','balanced','detailed')),",
    "plan_first INTEGER NOT NULL CHECK(plan_first IN(0,1)),",
    "review_changes INTEGER NOT NULL CHECK(review_changes IN(0,1)),",
    "created_at TEXT NOT NULL,",
    "updated_at TEXT NOT NULL)"
);
const AGENT_PROFILE_TOOLS_DDL: &str = concat!(
    "CREATE TABLE agent_profile_tools (",
    "profile_id TEXT NOT NULL,",
    "tool_id TEXT NOT NULL,",
    "PRIMARY KEY(profile_id,tool_id),",
    "FOREIGN KEY(profile_id) REFERENCES agent_profiles(profile_id) ON DELETE CASCADE)"
);
const AGENT_PROFILES_INDEX_DDL: &str =
    "CREATE INDEX idx_agent_profiles_list ON agent_profiles(updated_at DESC,profile_id ASC)";

/// 功能：创建 fresh application metadata 和完整 Profile schema。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn create_application_schema(
    transaction: &mut sea_orm::sqlx::Transaction<'_, sea_orm::sqlx::Sqlite>,
) -> Result<(), DbErr> {
    sea_orm::sqlx::query(APPLICATION_METADATA_DDL)
        .execute(&mut **transaction)
        .await
        .map_err(migration_error)?;
    create_profile_schema(transaction).await
}

/// 功能：创建规范化 Profile 主表、工具子表和稳定列表索引。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn create_profile_schema(
    transaction: &mut sea_orm::sqlx::Transaction<'_, sea_orm::sqlx::Sqlite>,
) -> Result<(), DbErr> {
    for statement in [
        AGENT_PROFILES_DDL,
        AGENT_PROFILE_TOOLS_DDL,
        AGENT_PROFILES_INDEX_DDL,
    ] {
        sea_orm::sqlx::query(statement)
            .execute(&mut **transaction)
            .await
            .map_err(migration_error)?;
    }
    Ok(())
}

/// 功能：查询 SQLite schema 中指定表是否精确存在。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn sqlite_table_exists(
    transaction: &mut sea_orm::sqlx::Transaction<'_, sea_orm::sqlx::Sqlite>,
    table: &str,
) -> Result<bool, DbErr> {
    sea_orm::sqlx::query_scalar::<_, i64>(
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=?",
    )
    .bind(table)
    .fetch_one(&mut **transaction)
    .await
    .map(|count| count == 1)
    .map_err(migration_error)
}

/// 功能：验证元数据表仅有预期的应用 schema 版本。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn require_schema_version(
    transaction: &mut sea_orm::sqlx::Transaction<'_, sea_orm::sqlx::Sqlite>,
    table: &str,
    expected: &str,
) -> Result<(), DbErr> {
    let statement = match table {
        "forge_metadata" => "SELECT value FROM forge_metadata WHERE key='schema_version'",
        "application_metadata" => {
            "SELECT value FROM application_metadata WHERE key='schema_version'"
        }
        _ => return Err(invalid_migration_state()),
    };
    let version = sea_orm::sqlx::query_scalar::<_, String>(statement)
        .fetch_optional(&mut **transaction)
        .await
        .map_err(migration_error)?;
    if version.as_deref() == Some(expected) {
        Ok(())
    } else {
        Err(invalid_migration_state())
    }
}

/// 功能：验证已存在 0.2 数据库具有完整主表、工具外键、CAS CHECK 与列表索引。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn validate_application_schema(
    transaction: &mut sea_orm::sqlx::Transaction<'_, sea_orm::sqlx::Sqlite>,
) -> Result<(), DbErr> {
    require_sqlite_columns(
        transaction,
        "application_metadata",
        &[("key", "TEXT", 1, 1), ("value", "TEXT", 1, 0)],
    )
    .await?;
    require_sqlite_columns(
        transaction,
        "agent_profiles",
        &[
            ("profile_id", "TEXT", 1, 1),
            ("revision", "INTEGER", 1, 0),
            ("display_name", "TEXT", 1, 0),
            ("description", "TEXT", 1, 0),
            ("enabled", "INTEGER", 1, 0),
            ("instructions", "TEXT", 1, 0),
            ("provider_id", "TEXT", 1, 0),
            ("model_id", "TEXT", 1, 0),
            ("api_family", "TEXT", 1, 0),
            ("dangerous_action_mode", "TEXT", 1, 0),
            ("response_style", "TEXT", 1, 0),
            ("plan_first", "INTEGER", 1, 0),
            ("review_changes", "INTEGER", 1, 0),
            ("created_at", "TEXT", 1, 0),
            ("updated_at", "TEXT", 1, 0),
        ],
    )
    .await?;
    require_sqlite_columns(
        transaction,
        "agent_profile_tools",
        &[("profile_id", "TEXT", 1, 1), ("tool_id", "TEXT", 1, 2)],
    )
    .await?;
    let profile_sql = sea_orm::sqlx::query_scalar::<_, String>(
        "SELECT sql FROM sqlite_master WHERE type='table' AND name='agent_profiles'",
    )
    .fetch_optional(&mut **transaction)
    .await
    .map_err(migration_error)?
    .ok_or_else(invalid_migration_state)?;
    if !compact_sql(&profile_sql)
        .contains("revisionintegernotnullcheck(revision>=1andrevision<=9007199254740991)")
    {
        return Err(invalid_migration_state());
    }
    let foreign_keys = sea_orm::sqlx::query("PRAGMA foreign_key_list(agent_profile_tools)")
        .fetch_all(&mut **transaction)
        .await
        .map_err(migration_error)?
        .into_iter()
        .map(|row| {
            Ok((
                row.try_get::<String, _>("table")?,
                row.try_get::<String, _>("from")?,
                row.try_get::<String, _>("to")?,
                row.try_get::<String, _>("on_delete")?.to_ascii_uppercase(),
            ))
        })
        .collect::<Result<Vec<_>, sea_orm::sqlx::Error>>()
        .map_err(migration_error)?;
    if foreign_keys
        != [(
            "agent_profiles".to_owned(),
            "profile_id".to_owned(),
            "profile_id".to_owned(),
            "CASCADE".to_owned(),
        )]
    {
        return Err(invalid_migration_state());
    }
    let indexes = sea_orm::sqlx::query("PRAGMA index_list(agent_profiles)")
        .fetch_all(&mut **transaction)
        .await
        .map_err(migration_error)?;
    if !indexes.iter().any(|row| {
        matches!(
            row.try_get::<String, _>("name").as_deref(),
            Ok("idx_agent_profiles_list")
        )
    }) {
        return Err(invalid_migration_state());
    }
    let index_columns = sea_orm::sqlx::query("PRAGMA index_xinfo(idx_agent_profiles_list)")
        .fetch_all(&mut **transaction)
        .await
        .map_err(migration_error)?
        .into_iter()
        .filter_map(|row| match row.try_get::<i64, _>("key") {
            Ok(1) => Some(
                row.try_get::<String, _>("name")
                    .and_then(|name| row.try_get::<i64, _>("desc").map(|desc| (name, desc)))
                    .map_err(migration_error),
            ),
            Ok(_) => None,
            Err(error) => Some(Err(migration_error(error))),
        })
        .collect::<Result<Vec<_>, _>>()?;
    if index_columns
        != [
            ("updated_at".to_owned(), 1_i64),
            ("profile_id".to_owned(), 0_i64),
        ]
    {
        return Err(invalid_migration_state());
    }
    Ok(())
}

/// 功能：要求已知 SQLite 表具有精确列顺序、affinity、NOT NULL 与主键位置。
///
/// 输入：启动检查事务、三个固定表名之一及与共同 DDL 对应的列元数据。
/// 输出：所有 PRAGMA 行逐项精确匹配时成功。
/// 不变量：声明类型按 ASCII 大写比较；不接受缺列、额外列、nullable 放宽或主键漂移。
/// 失败：未知表、PRAGMA 读取失败或任一列元数据不匹配时返回脱敏迁移错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn require_sqlite_columns(
    transaction: &mut sea_orm::sqlx::Transaction<'_, sea_orm::sqlx::Sqlite>,
    table: &str,
    expected: &[(&str, &str, i64, i64)],
) -> Result<(), DbErr> {
    let statement = match table {
        "application_metadata" => "PRAGMA table_info(application_metadata)",
        "agent_profiles" => "PRAGMA table_info(agent_profiles)",
        "agent_profile_tools" => "PRAGMA table_info(agent_profile_tools)",
        _ => return Err(invalid_migration_state()),
    };
    let actual = sea_orm::sqlx::query(statement)
        .fetch_all(&mut **transaction)
        .await
        .map_err(migration_error)?
        .into_iter()
        .map(|row| {
            Ok((
                row.try_get::<String, _>("name")?,
                row.try_get::<String, _>("type")?.to_ascii_uppercase(),
                row.try_get::<i64, _>("notnull")?,
                row.try_get::<i64, _>("pk")?,
            ))
        })
        .collect::<Result<Vec<_>, sea_orm::sqlx::Error>>()
        .map_err(migration_error)?;
    let matches = actual.len() == expected.len()
        && actual.iter().zip(expected).all(
            |((name, declared_type, not_null, primary_key), expected)| {
                name == expected.0
                    && declared_type == expected.1
                    && *not_null == expected.2
                    && *primary_key == expected.3
            },
        );
    if matches {
        Ok(())
    } else {
        Err(invalid_migration_state())
    }
}

/// 功能：移除 SQL 空白并转小写，供有限的 schema CHECK 审计使用。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn compact_sql(statement: &str) -> String {
    statement
        .chars()
        .filter(|character| !character.is_whitespace())
        .flat_map(char::to_lowercase)
        .collect()
}

/// 功能：构造不泄漏路径、SQL 或数据库内容的迁移状态错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn invalid_migration_state() -> DbErr {
    DbErr::Migration("application database schema is invalid".to_owned())
}

/// 功能：把 SQLx 迁移错误映射为固定脱敏 SeaORM 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn migration_error(_error: sea_orm::sqlx::Error) -> DbErr {
    DbErr::Migration("application database migration failed".to_owned())
}

/// 功能：验证远程数据库连接串环境变量为受限 ASCII 名称。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn valid_environment_name(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value
            .bytes()
            .next()
            .is_some_and(|byte| byte.is_ascii_uppercase() || byte == b'_')
        && value
            .bytes()
            .all(|byte| byte.is_ascii_uppercase() || byte.is_ascii_digit() || byte == b'_')
}

#[cfg(test)]
mod tests {
    use sea_orm::{ConnectionTrait as _, Database, DatabaseBackend, Statement};
    use tempfile::tempdir;

    use super::{
        AGENT_PROFILE_TOOLS_DDL, AGENT_PROFILES_DDL, AGENT_PROFILES_INDEX_DDL,
        APPLICATION_METADATA_DDL, DatabaseConfiguration, DatabaseProvider,
        connect_application_database,
    };

    /// 功能：验证默认配置创建 SQLite 并写入稳定 ORM schema 版本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn default_sqlite_bootstraps_metadata() -> Result<(), Box<dyn std::error::Error>> {
        let directory = tempdir()?;
        let configuration = DatabaseConfiguration::sqlite_default(directory.path());
        let connection = connect_application_database(&configuration).await?;
        let row = connection
            .query_one(Statement::from_string(
                DatabaseBackend::Sqlite,
                "SELECT value FROM application_metadata WHERE key='schema_version'".to_owned(),
            ))
            .await?
            .ok_or("metadata row is missing")?;
        assert_eq!(row.try_get::<String>("", "value")?, "0.2");
        Ok(())
    }

    /// 功能：验证 0.1 品牌化元数据在单事务中全量迁移并被删除。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn legacy_metadata_migrates_to_brand_neutral_schema()
    -> Result<(), Box<dyn std::error::Error>> {
        let directory = tempdir()?;
        let configuration = DatabaseConfiguration::sqlite_default(directory.path());
        let path = configuration.sqlite_path.as_ref().ok_or("sqlite path")?;
        let legacy = Database::connect(format!("sqlite://{}?mode=rwc", path.display())).await?;
        legacy
            .execute(Statement::from_string(
                DatabaseBackend::Sqlite,
                "CREATE TABLE forge_metadata (key TEXT PRIMARY KEY NOT NULL,value TEXT NOT NULL)"
                    .to_owned(),
            ))
            .await?;
        legacy
            .execute(Statement::from_string(
                DatabaseBackend::Sqlite,
                "INSERT INTO forge_metadata(key,value) VALUES('schema_version','0.1'),('fixture','kept')"
                    .to_owned(),
            ))
            .await?;
        legacy.close().await?;

        let migrated = connect_application_database(&configuration).await?;
        let fixture = migrated
            .query_one(Statement::from_string(
                DatabaseBackend::Sqlite,
                "SELECT value FROM application_metadata WHERE key='fixture'".to_owned(),
            ))
            .await?
            .ok_or("copied metadata")?;
        assert_eq!(fixture.try_get::<String>("", "value")?, "kept");
        let old_table_count = migrated
            .query_one(Statement::from_string(
                DatabaseBackend::Sqlite,
                "SELECT COUNT(*) AS count FROM sqlite_master WHERE type='table' AND name='forge_metadata'"
                    .to_owned(),
            ))
            .await?
            .ok_or("table count")?;
        assert_eq!(old_table_count.try_get::<i64>("", "count")?, 0);
        Ok(())
    }

    /// 功能：验证缺少 metadata 的非空数据库被零修改拒绝，不能伪装 fresh。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn versionless_nonempty_database_fails_without_partial_schema()
    -> Result<(), Box<dyn std::error::Error>> {
        let directory = tempdir()?;
        let configuration = DatabaseConfiguration::sqlite_default(directory.path());
        let path = configuration.sqlite_path.as_ref().ok_or("sqlite path")?;
        let raw = Database::connect(format!("sqlite://{}?mode=rwc", path.display())).await?;
        raw.execute(Statement::from_string(
            DatabaseBackend::Sqlite,
            "CREATE TABLE unrelated(value TEXT NOT NULL)".to_owned(),
        ))
        .await?;
        raw.close().await?;
        assert!(connect_application_database(&configuration).await.is_err());

        let inspected = Database::connect(format!("sqlite://{}?mode=rw", path.display())).await?;
        let rows = inspected
            .query_all(Statement::from_string(
                DatabaseBackend::Sqlite,
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name"
                    .to_owned(),
            ))
            .await?;
        let names = rows
            .into_iter()
            .map(|row| row.try_get::<String>("", "name"))
            .collect::<Result<Vec<_>, _>>()?;
        assert_eq!(names, ["unrelated"]);
        Ok(())
    }

    /// 功能：验证已有 application_metadata 缺少 schema_version 时零修改拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn application_metadata_without_version_fails_without_profile_objects()
    -> Result<(), Box<dyn std::error::Error>> {
        let directory = tempdir()?;
        let configuration = DatabaseConfiguration::sqlite_default(directory.path());
        let path = configuration.sqlite_path.as_ref().ok_or("sqlite path")?;
        let raw = Database::connect(format!("sqlite://{}?mode=rwc", path.display())).await?;
        raw.execute(Statement::from_string(
            DatabaseBackend::Sqlite,
            APPLICATION_METADATA_DDL.to_owned(),
        ))
        .await?;
        raw.execute(Statement::from_string(
            DatabaseBackend::Sqlite,
            "INSERT INTO application_metadata(key,value) VALUES('fixture','kept')".to_owned(),
        ))
        .await?;
        raw.close().await?;

        assert!(connect_application_database(&configuration).await.is_err());
        let inspected = Database::connect(format!("sqlite://{}?mode=rw", path.display())).await?;
        let objects = inspected
            .query_all(Statement::from_string(
                DatabaseBackend::Sqlite,
                "SELECT type || ':' || name AS object FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY object"
                    .to_owned(),
            ))
            .await?
            .into_iter()
            .map(|row| row.try_get::<String>("", "object"))
            .collect::<Result<Vec<_>, _>>()?;
        assert_eq!(objects, ["table:application_metadata"]);
        let rows = inspected
            .query_all(Statement::from_string(
                DatabaseBackend::Sqlite,
                "SELECT key || '=' || value AS entry FROM application_metadata ORDER BY key"
                    .to_owned(),
            ))
            .await?
            .into_iter()
            .map(|row| row.try_get::<String>("", "entry"))
            .collect::<Result<Vec<_>, _>>()?;
        assert_eq!(rows, ["fixture=kept"]);
        Ok(())
    }

    /// 功能：验证声明为 0.2 的列元数据放宽或额外外键均被零修改拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn corrupt_current_schema_metadata_and_foreign_keys_are_rejected()
    -> Result<(), Box<dyn std::error::Error>> {
        let cases = [
            (
                "CREATE TABLE application_metadata (key TEXT NOT NULL,value BLOB)",
                AGENT_PROFILE_TOOLS_DDL,
            ),
            (
                APPLICATION_METADATA_DDL,
                concat!(
                    "CREATE TABLE agent_profile_tools (",
                    "profile_id TEXT NOT NULL,",
                    "tool_id TEXT NOT NULL,",
                    "PRIMARY KEY(profile_id,tool_id),",
                    "FOREIGN KEY(profile_id) REFERENCES agent_profiles(profile_id) ON DELETE CASCADE,",
                    "FOREIGN KEY(tool_id) REFERENCES agent_profiles(profile_id) ON DELETE CASCADE)"
                ),
            ),
        ];
        for (metadata_ddl, tools_ddl) in cases {
            let directory = tempdir()?;
            let configuration = DatabaseConfiguration::sqlite_default(directory.path());
            let path = configuration.sqlite_path.as_ref().ok_or("sqlite path")?;
            let raw = Database::connect(format!("sqlite://{}?mode=rwc", path.display())).await?;
            for statement in [
                metadata_ddl,
                AGENT_PROFILES_DDL,
                tools_ddl,
                AGENT_PROFILES_INDEX_DDL,
            ] {
                raw.execute(Statement::from_string(
                    DatabaseBackend::Sqlite,
                    statement.to_owned(),
                ))
                .await?;
            }
            raw.execute(Statement::from_string(
                DatabaseBackend::Sqlite,
                "INSERT INTO application_metadata(key,value) VALUES('schema_version','0.2')"
                    .to_owned(),
            ))
            .await?;
            raw.close().await?;

            assert!(connect_application_database(&configuration).await.is_err());
            let inspected =
                Database::connect(format!("sqlite://{}?mode=rw", path.display())).await?;
            let objects = inspected
                .query_all(Statement::from_string(
                    DatabaseBackend::Sqlite,
                    "SELECT type || ':' || name AS object FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY object"
                        .to_owned(),
                ))
                .await?
                .into_iter()
                .map(|row| row.try_get::<String>("", "object"))
                .collect::<Result<Vec<_>, _>>()?;
            assert_eq!(
                objects,
                [
                    "index:idx_agent_profiles_list",
                    "table:agent_profile_tools",
                    "table:agent_profiles",
                    "table:application_metadata",
                ]
            );
        }
        Ok(())
    }

    /// 功能：验证预留远程 provider 在读取 secret 之前明确失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn reserved_remote_provider_fails_closed() {
        let configuration = DatabaseConfiguration {
            schema_version: "0.2".to_owned(),
            provider: DatabaseProvider::PostgreSql,
            sqlite_path: None,
            connection_environment: Some("QXNM_FORGE_DATABASE_URL".to_owned()),
            max_connections: 8,
        };

        let error = connect_application_database(&configuration)
            .await
            .expect_err("reserved provider must fail closed");
        assert_eq!(
            error.to_string(),
            "Custom Error: remote database provider is reserved but not enabled"
        );
    }

    /// 功能：验证 Rust 数据库 provider 名称与共享配置 schema 完全一致。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn provider_names_match_wire_schema() -> Result<(), serde_json::Error> {
        assert_eq!(
            serde_json::to_string(&DatabaseProvider::Sqlite)?,
            "\"sqlite\""
        );
        assert_eq!(
            serde_json::to_string(&DatabaseProvider::PostgreSql)?,
            "\"postgresql\""
        );
        assert_eq!(
            serde_json::to_string(&DatabaseProvider::MySql)?,
            "\"mysql\""
        );
        Ok(())
    }
}
