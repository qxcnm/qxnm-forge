//! QXNM Forge 可替换应用数据库与默认 SQLite 启动层。
//!
//! 作者：高宏顺 <18272669457@163.com>

use std::path::{Path, PathBuf};

use sea_orm::{
    ConnectOptions, ConnectionTrait, Database, DatabaseBackend, DatabaseConnection, DbErr,
    Statement,
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
            schema_version: "0.1".to_owned(),
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
        if self.schema_version != "0.1" || !(1..=128).contains(&self.max_connections) {
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
        .sqlx_logging(false);
    let connection = Database::connect(options).await?;
    bootstrap_metadata(&connection).await?;
    Ok(connection)
}

/// 功能：建立 ORM schema 版本表并写入当前版本。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn bootstrap_metadata(connection: &DatabaseConnection) -> Result<(), DbErr> {
    if connection.get_database_backend() != DatabaseBackend::Sqlite {
        return Err(DbErr::Custom(
            "remote database provider is reserved but not enabled".to_owned(),
        ));
    }
    connection
        .execute(Statement::from_string(
            DatabaseBackend::Sqlite,
            "CREATE TABLE IF NOT EXISTS forge_metadata (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL)".to_owned(),
        ))
        .await?;
    connection
        .execute(Statement::from_string(
            DatabaseBackend::Sqlite,
            "INSERT INTO forge_metadata(key,value) VALUES('schema_version','0.1') ON CONFLICT(key) DO UPDATE SET value=excluded.value".to_owned(),
        ))
        .await?;
    Ok(())
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
    use sea_orm::{ConnectionTrait as _, DatabaseBackend, Statement};
    use tempfile::tempdir;

    use super::{DatabaseConfiguration, DatabaseProvider, connect_application_database};

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
                "SELECT value FROM forge_metadata WHERE key='schema_version'".to_owned(),
            ))
            .await?
            .ok_or("metadata row is missing")?;
        assert_eq!(row.try_get::<String>("", "value")?, "0.1");
        Ok(())
    }

    /// 功能：验证预留远程 provider 在读取 secret 之前明确失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn reserved_remote_provider_fails_closed() {
        let configuration = DatabaseConfiguration {
            schema_version: "0.1".to_owned(),
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
