# ADR 0028：品牌中立 Agent Profile、CAS 与运行快照

- 状态：Accepted
- 日期：2026-07-21
- 项目：qxnm-forge
- 作者：高宏顺 `<18272669457@163.com>`

## 背景

React 工作台需要创建、编辑、删除和选择自定义 Agent，但前端内存预览不能成为第三套
runtime，也不能绕过 Rust/.NET application service 直接写数据库。仅把 Profile 塞入
`run/start.extensions` 会失去跨语言 Schema、并发控制、恢复语义和安全审计；仅保存
`modelId` 又会在同一模型存在多个 API family 时产生歧义。

Profile 同时影响 Provider system guidance 与工具收窄。若运行时每次重新读取“最新”
Profile，已经接受的 run 会被后续更新悄然改变；若 Profile 可以直接授权工具或审批，
则它会成为绕过后端 policy 的持久化提权入口。因此需要稳定引用、CAS 和本 run 快照。

## 决策

新增品牌中立 `agent-profile.schema.json` v0.2，并在 application service 中冻结四个方法：

- `agentProfiles/list {}` → `{profiles}`；
- `agentProfiles/create {profile}` → `{profile}`；
- `agentProfiles/update {profileId, expectedRevision, profile}` → `{profile}`；
- `agentProfiles/delete {profileId, expectedRevision}` → `{deleted:true}`。

方法名、DTO、数据库表与 Session 字段均不包含产品名。核心对象
`additionalProperties:false`；Profile 没有 `extensions`、credential、secret、endpoint、
header、宿主路径、命令或 entitlement 字段。完整输入字段是：

- `displayName`：1..48；`description`：0..160；`enabled`：Boolean；
- `instructions`：1..12000；
- 完整 `model {providerId, modelId, apiFamily}`；
- 唯一 `requestedToolIds`，每个 ID 使用公共工具标识格式；
- `dangerousActionMode` 仅 `ask|deny`；
- `behavior {responseStyle, planFirst, reviewChanges}`，其中 `responseStyle` 仅
  `concise|balanced|detailed`。

所有字符串长度先对原始 wire 值按 Unicode scalar value 计数并执行 schema 边界；不得先
trim 再让越界输入通过。随后只对 `displayName/description/instructions` 的两端移除 Unicode
`White_Space`，且规范化后的 `displayName/instructions` 仍须非空；持久化和返回均使用该
规范化文本。模型三元组、Profile ID 与工具 ID 是精确协议身份，不做 trim 或大小写折叠；
`modelId` 两端存在 Unicode `White_Space` 时作为 `agent_profile_invalid` 拒绝。

服务端生成不带固定品牌前缀的 opaque `profileId`。创建从 `revision:1` 开始；更新是完整
替换且只在 `expectedRevision` 命中时把 revision 精确加一，并保留 `profileId/createdAt`；
删除也使用同一 CAS。CAS 检查与写入必须在一笔数据库事务中完成。列表按 `updatedAt`
降序、再按 ASCII `profileId` 升序返回，所有工具 ID 按 ASCII 升序规范化。

创建和更新时，模型三元组必须精确对应同一 application service 当前 `models/list` 的一个
descriptor；请求工具必须是同一已初始化服务当前广告工具的子集。模型或工具能力后来收缩
不会改写 Profile，而是在新 run 验证时 fail closed 或进一步取交集。

四个 Profile 方法作为原子 capability 组广告。实现只有在 CRUD、CAS、迁移、run binding
和恢复快照全部可用时才广告全部四项；部分实现不得广告任一项。`faux/configure` 仍只属于
显式 conformance mode，生产初始化绝对不得广告或接受该方法。

## Run binding

`run/start` 新增可选封闭引用 `agentProfile {profileId, revision}`。存在该引用时，服务必须
在创建 Session、追加用户消息或分配可观察 run 之前依次验证：

1. Profile 存在、revision 精确相等且 `enabled:true`；
2. `run/start.provider {id, modelId, apiFamily}` 与 Profile 的
   `{providerId, modelId, apiFamily}` 精确相等；
3. 该完整 route 在本次运行快照中仍可用；
4. 请求工具、后端 capability、当前 project trust、运行级 capability 与 policy 均允许。

本 run 的 `effectiveToolIds` 首先取
`profile.requestedToolIds ∩ initialize.capabilities.tools`，随后仍受更严格的 runtime
policy、workspace boundary、审批和 sandbox 控制。Profile 永远只能收窄：
`dangerousActionMode:"deny"` 直接拒绝危险操作；`"ask"` 也只表示“允许后端继续执行自己的
审批判定”，不表示批准。缺少交互审批的 headless 环境继续拒绝危险操作。

验证通过时，`run.accepted.data.agentProfileSnapshot` 与用户输入、accepted record 一起先
持久化再响应。快照只含 `profileId/revision/displayName/instructions/model`、
`requestedToolIds/effectiveToolIds`、`dangerousActionMode` 与 `behavior`。它不含说明、启用
状态、时间戳或任何 resolved credential/endpoint。run 被接受后的 Profile 更新、禁用或
删除不得改变该 run；崩溃恢复也只读取快照，不重新绑定“最新” revision。

`instructions` 与 `behavior` 作为本 run 的 Provider system guidance，位于实现的基础安全
system guidance 之后、用户消息之前。它们可以改变表达风格、是否先计划、是否在完成前
审阅变更，但不是权限输入，不能改变工具 registry、审批、路径、网络、credential、sandbox
或 entitlement。实现不得把 prompt 文本解释成授权。

## Application database v0.2

application database 逻辑版本从 `0.1` 升为 `0.2`。SQLite 使用单事务迁移，并把旧
`forge_metadata` 全部行迁入品牌中立 `application_metadata(key,value)` 后删除旧表。
Profile 使用规范化的 `agent_profiles` 与 `agent_profile_tools` 两表；工具表以
`(profile_id, tool_id)` 为联合主键并通过 `ON DELETE CASCADE` 绑定 Profile。可编辑标量、
模型三元组、行为字段、revision 和 UTC 时间戳均为显式列，不使用实现私有二进制或 JSON
对象保存工具集合。

迁移必须：只有没有任何非 SQLite 内部 schema 对象的数据库才视为 fresh；在事务开始后
确认唯一旧版本恰为 `0.1`；创建中立 metadata、两张业务表和
`(updated_at DESC, profile_id ASC)` 索引；更新 `schema_version` 为 `0.2`；删除旧 metadata；
最后提交。任一步失败全部回滚。已为 `0.2` 时只验证所需对象并幂等返回；两张 metadata 表
并存、较新/未知版本、版本缺失或 `0.2` 对象不完整都必须零修改失败，不能自动重置数据库。
portable Session JSONL 仍不迁入 ORM。

## Portable errors

错误消息只用于安全展示，客户端只按以下 code、retryable 与 `details.kind` 分支：

| 条件 | Code | Retryable | `details.kind` |
| --- | ---: | :---: | --- |
| Profile 字段或语义无效 | -32602 | false | `agent_profile_invalid` |
| Profile 不存在 | -32602 | false | `agent_profile_not_found` |
| update/delete revision 冲突 | -32010 | true | `stale_agent_profile_revision` |
| update 已命中最大安全 revision | -32009 | false | `agent_profile_revision_exhausted` |
| run 引用旧 revision | -32010 | false | `stale_agent_profile_revision` |
| run 引用 disabled Profile | -32003 | false | `agent_profile_disabled` |
| run Provider 与 Profile 不同 | -32602 | false | `agent_profile_model_mismatch` |
| Profile route 当前不可用 | -32005 | true | `agent_profile_model_unavailable` |

失败的 CRUD 不修改 Profile；失败的 run binding 不创建 Session、不追加输入或
`run.accepted`。revision conflict 可携带 `expectedRevision/currentRevision`，但不得泄露
Profile instructions 或其他用户内容。当前 revision 已是 `9007199254740991` 且 update 的
`expectedRevision` 命中时，必须在写入前返回 revision exhausted；它不是 stale，也不得把
数据库递增到超出 JSON 安全整数的值。

## Conformance 与后果

共同 fixture 冻结 CRUD wire、revision、模型三元组、run 快照、错误表、生产
`faux/configure` 禁止项与 Rust 已知误广告回归目标。Schema 门禁覆盖字段边界、非法枚举、
重复工具、未知字段、secret/endpoint 字段和 run 引用附加字段。Rust 与 .NET 必须分别通过
SQLite 重开持久化、0.1→0.2 migration、CAS 冲突、并发 update/delete、快照恢复、模型匹配、
工具交集与生产 capability 黑盒测试后，能力才能从 `implemented` 升到 `conformant`。

该决策让 Web、桌面和远程客户端共享同一状态边界。代价是 application DB 必须迁移，且
不能只实现 UI CRUD；后端必须同时实现完整 run binding 与安全恢复语义。
