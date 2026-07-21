# 应用数据库与 ORM 规范 v0.2

状态：Draft  
作者：高宏顺 `<18272669457@163.com>`

## 范围

本规范只定义 application service 产品层数据（Agent Profile、设置索引、推广目录缓存、未来用户与权益投影等）的数据库边界。portable Session 继续使用 `session.md` 定义的 append-only JSONL journal；实现不得把 Session wire format 隐式迁移进 ORM，也不得让 UI 直接读取数据库。

## Provider 与 ORM

- 默认 provider MUST 为 `sqlite`，默认文件名为用户状态根下的 `application.db`。
- Rust MUST 使用 SeaORM；.NET MUST 使用 Entity Framework Core。两端共享逻辑 schema 版本和黑盒行为，不共享 runtime、migration binary 或语言对象。
- `postgresql` 与 `mysql` 是预留 provider。未编译或未配置对应 adapter 时，实现 MUST 明确失败，MUST NOT 静默回退 SQLite。
- 数据库配置 MUST 通过 [`storage-config.schema.json`](schemas/storage-config.schema.json) 严格验证，未知字段 MUST 被拒绝。
- 当前逻辑版本为 `0.2`，使用品牌中立 `application_metadata(key, value)` 与
  `schema_version=0.2`。旧 v0.1 `forge_metadata` 只能作为事务 migration 输入，迁移完成后
  必须删除，不能继续成为持久化标识。

## Agent Profile 逻辑 schema

`agent_profiles` 以 `profile_id` 为主键，显式保存 revision、名称、说明、enabled、
instructions、完整 `provider_id/model_id/api_family`、危险操作模式、三个 behavior 值和 UTC
创建/更新时间。字段边界必须同时满足 `agent-profile.schema.json`；数据库约束不能取代
application service 的 wire/语义校验。

`agent_profile_tools(profile_id, tool_id)` 使用联合主键，`profile_id` 外键指向
`agent_profiles` 并 `ON DELETE CASCADE`。工具集合不存实现私有 blob 或 JSON；读取按 ASCII
`tool_id` 升序。列表索引是 `agent_profiles(updated_at DESC, profile_id ASC)`。

创建在一笔事务中写入 Profile 与工具行，初始 revision 固定为 1。update/delete 使用
`WHERE profile_id=? AND revision=?` 的 CAS；update 精确把 revision 加一并原子替换工具集合。
affected rows 为 0 时必须区分不存在和 revision conflict，二者都不能产生部分写入。

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

SQLite v0.1→v0.2 必须在 `BEGIN IMMEDIATE` 等价 ORM transaction 中先查询
`sqlite_master`，再按状态分支；不能用 `IF NOT EXISTS` 掩盖半迁移或损坏对象：

1. fresh DB（两张 metadata 表均不存在，且没有任何非 SQLite 内部 schema 对象）：创建
   `application_metadata`、两张 Profile 表与列表索引，写入 `schema_version=0.2`；无 metadata
   但已有任意用户 table/index/trigger/view 时属于 version 缺失，必须零修改拒绝；
2. 仅存在 `forge_metadata`：版本必须精确为 `0.1`；创建 `application_metadata`，复制全部
   metadata 行，创建 Profile 对象，更新版本为 `0.2`，删除旧表，最后提交；
3. 仅存在 `application_metadata`：版本必须精确为 `0.2`，并验证必需表、列、外键和索引；
   完整时幂等返回，不完整时零修改失败；
4. 两张 metadata 表并存，或版本缺失、未知、较新时，回滚并 fail closed。

连接必须在事务前启用 SQLite foreign keys。任一步 DDL、copy、version update、对象验证或
commit 失败都回滚；不得删除数据库、清空 Profile、重写 version 或退回 v0.1。未来启用
MySQL/PostgreSQL adapter 时必须提供等价逻辑 migration 后才可接受 provider 配置。

SQLite 应启用 ORM 参数绑定并关闭 sensitive-data logging。启用远程 provider 后，连接池上限必须遵守 `maxConnections`，TLS、重试和超时策略必须由 provider adapter 显式配置。

## 当前支持声明

Rust SeaORM 与 .NET EF Core 的 v0.2 Profile 实现只有在各自代码和测试存在时才能登记
`implemented`。在共同 migration/CRUD/run-binding runner 完成前不得登记
`agent.profile_crud`、`agent.profile_run_binding` 为 `conformant`；MySQL/PostgreSQL 实例
门禁完成前也不得宣称远程 provider 已可用。
