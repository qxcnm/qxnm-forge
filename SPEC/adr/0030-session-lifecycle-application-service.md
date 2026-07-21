# ADR 0030：Session 列表、归档与永久删除边界

- 状态：Accepted
- 日期：2026-07-21
- 项目：qxnm-forge
- 作者：高宏顺 `<18272669457@163.com>`

## 背景

桌面与移动工作台需要展示真实 Session、归档历史并执行经过确认的永久删除。Web UI 不能直接
扫描 journal、读取 SQLite 或删除宿主文件；Rust 与 .NET 也不能把 Provider、credential、数据库
等状态目录误识别为 Session。生命周期操作因此必须成为品牌中立 application service 的共同能力。

## 决策

所有原生 Session 固定存放在 `stateRoot/sessions/<sessionId>`。Provider connection、credential、
application database 与归档元数据仍位于 `stateRoot` 下各自的专用边界，不能作为 Session 被列出、
归档或删除。该布局与现有 Rust portable Session 路径一致；.NET 不再把整个 state root 当作
Session 根。

冻结以下 RPC：

- `session/list {cursor?,limit?}` 返回严格的 `{sessions,nextCursor,hasMore}`；默认页大小 64，
  `limit` 范围为 1..128，终页也必须显式返回 `nextCursor:null`；
- `session/archive {sessionId}` 返回 `{session}`，其中 `archived:true`；
- `session/restore {sessionId}` 返回 `{session}`，其中 `archived:false`；
- `session/delete {sessionId}` 只在安全删除全部完成后返回 `{deleted:true}`。

列表按 `updatedAt` 降序、再按 ASCII `sessionId` 升序。摘要只包含
`sessionId/title/project/updatedAt/archived/status?`，不得包含 transcript、workspace 绝对路径、
Provider、credential、工具参数或 journal 正文。标题来自首条 user 文本的有界单行投影；项目只取
header workspace 的安全 basename。损坏 journal 可以显示固定占位摘要，但 mutation 必须拒绝。

可选 `status` 只从 journal 的完整 LF durable 前缀重建。`run.accepted` 到同 `runId` 的
`run.terminal` 之间为活动 run；至少一个活动 run 时为 `active`。活动 run 的
`approval.requested` 到同 `(runId,approvalId)` 的 `approval.resolved` 之间为未决审批；只要还有
一个未决审批，`approval` 优先于 `active`。解决其中一个请求不能清除其他未决请求；全部解决后
恢复为 `active`，匹配的 terminal 后省略 `status`。已经 terminal 的 `runId` 再次 accepted、重复或
错配审批、无活动 run 的审批、仍有未决审批却出现 terminal 都不能投影为可操作状态，必须把该
Session 降级为固定损坏摘要并省略 `status`。未终止尾部不参与投影。`session/archive` 与
`session/restore` 返回的摘要使用同一扫描结果；该状态只用于列表导航，审批详情仍必须通过
`session/get` 的 durable 事件重建。

归档状态使用 `stateRoot/session-lifecycle/archive-state.json` 的严格、有界、排序 ID 集合，并通过
专用跨进程锁和同目录原子替换发布。归档与恢复不改写 portable journal。未知、损坏、活动中、
持有 pending faux 或存在另一 writer 的 Session 必须失败关闭。

永久删除先验证完整 Session 普通目录树，不跟随 symlink、junction、reparse point 或 device，且受
深度、条目数和 journal 大小限制。验证与 writer/reservation 锁成立后，把目录原子移动到固定
`stateRoot/sessions/.session-tombstones/<sessionId>`，同步目录，再清理归档元数据和 tombstone。
移动后的中途失败必须保留同一 ID 可识别的 tombstone；后续相同 `session/delete` 继续完成，不能
创建随机且不可恢复的半删除目录。结构性边界错误发生在移动前时必须保留原 Session。

## 分页与并发

Cursor 是服务签发的 opaque 值，客户端不得解析。首版实现可以编码稳定排序后的 offset，但必须
验证版本、长度和上限。列表是 live view，不提供跨页快照；客户端应按 `sessionId` 去重，并拒绝
重复 cursor、空进度页或超过资源上限的分页循环。每个完整 JSON-RPC 帧仍必须小于协商的
`maxFrameBytes`。

## 安全与测试

生命周期错误只能携带公开 Session ID 和固定 `details.kind`，不得包含宿主路径、journal 内容、
归档文档、底层异常或用户消息。共同实现至少覆盖：严格 params、排序与分页、跨语言 journal 摘要、
归档/恢复 durability、busy writer、损坏 journal、symlink/reparse 拒绝、固定 tombstone 续删、
归档元数据清理、帧上限和状态根隔离。Rust 与 .NET 分别原生实现，不得通过 FFI、子进程或另一
语言 runtime 代理。

共同 runner 只选择 `faux/faux-v1`，清除继承的真实 Provider credential/endpoint，并关闭
Provider conformance 与 live test 开关；其报告的 `providerHttpConfigured:false` 表示测试未
配置或调用 Provider HTTP，不表示已观测全部 socket syscall。portable writer lease 为跨进程
协调使用的本地 loopback witness 不属于 Provider 请求，也不构成真实 Provider 或付费模型证据。
