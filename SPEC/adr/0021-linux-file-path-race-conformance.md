# ADR 0021: Linux 文件路径竞态确定性门禁

- 状态：Accepted
- 日期：2026-07-17
- 作者：高宏顺 `<18272669457@163.com>`

## 背景

工作区路径先做 `realpath`、`lstat` 或逐组件检查，再通过同一绝对路径执行
`open`、临时文件创建或 `rename`，中间仍存在 time-of-check/time-of-use
窗口。概率式 racer 不能证明实现是否真正持有 workspace、parent、leaf 或临时文件的
OS handle，也会因调度差异产生假阴性。

本 ADR 只冻结 Linux 上 `file.read` 与 `file.write` 的确定性竞态证据。它不把路径检查
描述为 hard sandbox，也不把 Linux 结果外推到 Windows reparse/junction、UNC、ADS、
device path、case/short-name alias，或尚未覆盖的 `file.list`、`search.text`、
`file.edit`、process cwd 和 mount change。

## 决策

### 固定对象语义

daemon/工具注册表启动时必须打开并持续持有 canonical workspace root directory handle。
每次访问从该 handle 开始，以 no-follow 语义逐组件打开最终 parent。成功打开的 root、
parent 和 read leaf handle 所代表的对象不会因外部 pathname rename/rebind 改变。
`file.read` 必须在发布 leaf checkpoint 前，以该 leaf handle 的 `fstat` 确认目标为 regular
file 且静态大小不超过实现已公开的硬字节上限；checkpoint 后的实际读取最多观察
`limit + 1` 字节，文件在检查后增长也必须以 `output_limit` 失败，不能无界缓冲。

`file.write` 必须在同一 parent handle 下 create-exclusive 一个随机临时 regular file，
完整写入并 `fsync` 后，以同一 parent handle 下的原 leaf 名称做原子 replace，再刷新
parent directory。replace 不跟随目的 leaf symlink。若攻击者把旧 leaf rename-away，成功
写入的是 pinned parent 下原 leaf 名称；被移走的旧 inode 保持旧内容。

### Conformance-only hook

测试 hook 只有以下两道宿主门同时成立时才能读取：

1. daemon argv 含一个精确、独立的 `--conformance` 元素；
2. 环境 `QXNM_FORGE_CONFORMANCE` 精确等于 `1`。

单一环境项 `AGENT_PATH_BOUNDARY_CONFORMANCE_CONFIG` 指向 runner 创建的 strict JSON
配置文件。该名称和下述配置都不是 wire、Session 或持久化 extension。配置对象只允许：

```json
{
  "schemaVersion": "0.1",
  "caseId": "read_workspace_root_rebind",
  "toolCallId": "path-race-read-root-1",
  "checkpoint": "before_parent_walk",
  "controlDirectory": "/runner-owned/absolute/control",
  "timeoutMs": 5000
}
```

禁止未知字段、重复键、相对 control path、symlink control directory、非 `0700` control
directory 和非固定 `timeoutMs`。hook 一次性命中；重复命中、配置的 checkpoint 未命中或
等待超时都失败关闭，绝不改变路径授权结果。

control directory 中只使用 `ready.json` 与 `release.json`。两者必须是 create-exclusive、
no-follow 的 bounded regular file，JSON 属性集合精确为 `schemaVersion`、`caseId`、
`toolCallId`、`checkpoint`、`state`。daemon 写入 `state:"ready"` 并刷新后等待；runner 完成
mutation 后写入 `state:"release"`。JSON 属性顺序和空白不具语义，但重复键、未知字段、
错误关联值、symlink、非 regular file 或超限内容一律拒绝。

四个 checkpoint 的前置不变量是：

| checkpoint | daemon 暂停时已经成立的事实 |
| --- | --- |
| `before_parent_walk` | startup root handle 已固定，本次逐组件 parent walk 尚未开始 |
| `after_parent_open` | 最终 parent handle 已从固定 root 逐组件 no-follow 打开并验证 |
| `after_leaf_open_before_read` | read leaf handle 已 no-follow 打开，且 `fstat` 已确认 regular、非负大小和不超过公开硬字节上限；后续读取仍受 `limit + 1` 探针约束 |
| `after_temp_fsync_before_rename` | write temp 已在同一 parent handle 下完整写入并 `fsync`，尚未 replace |

### 六个动态案例

机器夹具 `CONFORMANCE/fixtures/security/path-boundary-race-cases.json` 由
`urn:agent-protocol:security:path-boundary-race-cases:0.1` 冻结，顺序为：

1. `file.read` 在 `before_parent_walk` 后遭遇 workspace root rename→outside symlink；
2. `file.write` 在同一点遭遇同一 root rebind；
3. `file.read` 在 `after_parent_open` 后遭遇 nested parent rename→outside symlink；
4. `file.write` 在同一点遭遇同一 nested parent rebind；
5. `file.read` 在 `after_leaf_open_before_read` 后遭遇 leaf rename→outside symlink；
6. `file.write` 在 `after_temp_fsync_before_rename` 后遭遇同一 leaf swap。

六例都必须成功，不能以统一 deny 冒充 handle pinning。read 返回 pinned original 内容；
write 的新内容只出现在 pinned original workspace/parent 下。leaf-write 还要求 moved old leaf
保留旧内容，原 leaf 名已成为新的 regular file。

write 使用真实 interactive approval：唯一 `approval.requested`、client `allow_once`、durable
唯一 `approval.resolved` 和成功响应交付后，才可出现 `tool.started` 与 ready barrier。

### 外部树与持久化断言

每例使用 fresh daemon、workspace、state、outside 和 control。outside 含每轮随机 canary。
runner 在 mutation 前后比较不跟随 symlink 的完整 manifest：路径集合、对象类型、raw
symlink target、mode、uid/gid、device/inode、link count、size、SHA-256、mtime 和 ctime 必须
逐项相同；atime 不作为断言，因为 runner 自身的读取会影响它。

canary 不得进入协议 stdout、普通 stderr、Session/journal、control 或 pinned workspace。
每个工具调用必须有唯一 `tool.intent`、`tool.result`、canonical tool message、
`tool.completed` 与 run terminal；write 另有唯一 approval request/resolution。所有事件继续
满足 append-before-observe。

### 两项生产缺门门禁

runner 另以永不提供 writer 的 FIFO 作为配置路径，分别验证“只有 CLI 门”和“只有环境
门”。实现必须在打开 FIFO、创建 ready、创建 Session/journal 或执行工具之前，让
`initialize` 返回：

```text
code=-32603
retryable=false
details.kind=conformance_configuration_invalid
```

presence 不得被忽略，也不得回显配置路径。双门完整但配置非法时使用相同错误。

## 能力声明

本门禁通过只构成 Linux `file.read`/`file.write` 的确定性 race evidence。现有宽能力
`security.path_boundary` 继续保持 `implemented`；在 Windows 路径语义、其他文件工具、
process cwd 与 mount change 取得共同证据前不得提升为 `conformant`。

## 后果

- 五种语言必须使用各自原生 OS/runtime handle 能力，不能委托另一种实现。
- 不支持安全 relative-handle 操作的平台必须 fail closed，不能回退到一次性 `realpath`。
- hook 不增加生产授权，不改变 wire DTO 或 Session format，因此不需要协议迁移。
- 测试临时文件名使用 brand-neutral 名称，不把项目工作名写入可观察持久化标识符。
