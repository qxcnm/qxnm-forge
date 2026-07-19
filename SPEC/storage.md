# 应用数据库与 ORM 规范 v0.1

状态：Draft  
作者：高宏顺 `<18272669457@163.com>`

## 范围

本规范只定义 QXNM Forge 产品层数据（设置索引、推广目录缓存、未来用户与权益投影等）的数据库边界。portable Session 继续使用 `session.md` 定义的 append-only JSONL journal；实现不得把 Session wire format 隐式迁移进 ORM，也不得让 UI 直接读取数据库。

## Provider 与 ORM

- 默认 provider MUST 为 `sqlite`，默认文件名为用户状态根下的 `application.db`。
- Rust MUST 使用 SeaORM；.NET MUST 使用 Entity Framework Core。两端共享逻辑 schema 版本和黑盒行为，不共享 runtime、migration binary 或语言对象。
- `postgresql` 与 `mysql` 是预留 provider。未编译或未配置对应 adapter 时，实现 MUST 明确失败，MUST NOT 静默回退 SQLite。
- 数据库配置 MUST 通过 [`storage-config.schema.json`](schemas/storage-config.schema.json) 严格验证，未知字段 MUST 被拒绝。
- v0.1 建立 `forge_metadata(key, value)`，并以 `schema_version=0.1` 标记逻辑版本。

## 配置与 secret

非 secret 配置优先级为：显式 runtime/CLI 配置 > 受信任的用户级设置 > 内置默认值。项目配置 MUST NOT 指定远程连接串、连接串环境变量名称或提升数据库访问权限。

SQLite 路径必须是用户状态根或管理员显式授权位置下的绝对路径。远程配置只保存受限 ASCII 环境变量名称，连接串值只在创建 provider adapter 的最后时刻从环境或未来平台 CredentialStore 读取。连接串、密码、host 私有信息和 ORM 原始错误 MUST NOT 进入 Session、RPC、日志、fixture 或 UI transcript。

推荐部署变量：

- `QXNM_FORGE_DATABASE_PROVIDER`：`sqlite`、`postgresql` 或 `mysql`；
- `QXNM_FORGE_DATABASE_URL`：远程连接串 secret；
- `QXNM_FORGE_DATABASE_PATH`：管理员显式覆盖的 SQLite 绝对路径。

变量只是部署入口；实现必须先解析到 schema DTO，再按上述优先级和信任边界验证。

## Migration 与一致性

每次 schema 变更 MUST 先提升逻辑版本、记录 ADR、提供 SQLite/MySQL/PostgreSQL 等价 migration 和共同 conformance fixture。Migration 必须具备事务或明确的恢复标记；失败不得把较新数据库伪装成旧版本继续写入。

SQLite 应启用 ORM 参数绑定并关闭 sensitive-data logging。启用远程 provider 后，连接池上限必须遵守 `maxConnections`，TLS、重试和超时策略必须由 provider adapter 显式配置。

## 当前支持声明

Rust SeaORM 与 .NET EF Core 已实现 SQLite bootstrap 和 provider 严格关闭边界，能力登记为 `implemented`。在共同 migration/CRUD runner 与 MySQL/PostgreSQL 实例门禁完成前，不得登记为 `conformant`，也不得宣称远程 provider 已可用。

