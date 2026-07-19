# ADR 0019: Approval failure and delivery-barrier conformance

- Status: Accepted
- Date: 2026-07-16
- Project: qxnm-forge
- Author: 高宏顺 `<18272669457@163.com>`

## Context

ADR 0006 和既有十一项工具门禁已经证明正常 `allow_once`、client deny、headless
default deny、等待审批期间取消以及 append-before-event/ack。它们尚不能证明无人响应、
控制连接断开、审批成功响应无法交付或 daemon 在 `approval.requested` 后崩溃时仍然
fail closed。只测试正常响应会遗漏一种危险状态：client 的 allow 决定已经 durable，
但允许执行的成功响应并没有到达 controller。

## Decision

### Single durable resolution and response delivery barrier

每个 approval 恰有一条 durable `approval.resolved`。Client 决定先落盘，daemon 再写并
flush `{accepted:true}`；只有该成功帧完成交付后，内存中的 approval release barrier
才可打开。`approval.resolved` 记录本身不授予执行权。

如果 client `allow_once` 已 durable、但成功响应写入或 flush 失败，daemon MUST NOT
追加第二条 deny resolution，也 MUST NOT emit `approval.resolved`/`tool.started` 或执行
工具。它把未释放的调用作为 fail-closed delivery failure 收尾，持久化一个错误
`tool.result` 和唯一 run terminal。原 client resolution 保持
`decision.choice:"allow_once"`、`resolutionSource:"client"`，用于准确审计发生过的决定；
可观察副作用仍等价于 deny。

Timeout 在尚无 durable resolution 时追加唯一
`decision.choice:"deny"`、`resolutionSource:"timeout"`。Stdio EOF/disconnect 在尚无
resolution 时追加唯一 deny/disconnect。未知、过期、timeout 后或重复的
`approval/respond` 返回 `code=-32010`、`retryable=false` 的 conflict，绝不改变 journal
或恢复执行。

进程在 durable `approval.requested` 后被强制终止时，下一 daemon 打开 Session 必须：

1. 保留原 journal 字节前缀；
2. 追加唯一 deny/disconnect resolution；
3. 追加 outcome-known、未执行的 tool result 和 canonical tool message；
4. 不追加 `tool.started`，不修改 workspace；
5. 追加唯一 `run.terminal(status:"interrupted")` 和对应 durable event。

### Governed timeout override

共同 runner 的 timeout 固定为 100 ms。它只有在渲染后的 daemon argv 含一个精确、
独立的 `--conformance` 元素，并且子进程环境中 `QXNM_FORGE_CONFORMANCE` 精确等于 `1`
时，才注入 `QXNM_FORGE_APPROVAL_TIMEOUT_MS=100`。`--conformance=true`、前缀匹配、环境
默认或任一单门均不授权读取该 override。该变量只缩短测试 deadline，不放宽 policy。

### Common black-box gates

`tool-approval-cases.json` 和 `tool_runner.py` 冻结以下门禁：

1. client `allow_once` 的 resolution durable-before-ack，ack 前无 tool event；
2. client deny 的 resolution durable-before-ack 且零执行；
3. 100 ms timeout 自动 deny，随后两次 late/duplicate response 都 conflict；
4. stdin EOF/disconnect 自动 deny，允许 run 最终 completed 或 interrupted；
5. approval success-frame delivery failure 保留唯一 client resolution，但 barrier 不 release；
6. `approval.requested` 后强制终止并重开，恢复为唯一 interrupted terminal。

所有故障案例必须证明：一个 approval request、一个 resolution、零 `tool.started`、零目标
workspace mutation、一个 tool result 和唯一允许的 run terminal。Runner 仅使用
`file.write` 合成案例、临时 workspace/state、faux Provider 和安全 JSON argv；不访问
真实 Provider、凭据或外部网络。

## Consequences

- Approval durability 与执行授权被明确分离，审计记录不会为了补偿 I/O 故障而伪造第二个
  决定。
- Daemon 必须在 stdout delivery failure、EOF、timeout 与 crash recovery 之间复用单一
  resolution/terminal CAS。
- `security.approval` 只有在正常门禁与本 ADR 的故障门禁共同通过后才能提升为
  `conformant`。
- Failure runner 会主动关闭 pipe 或强制终止自己启动的 daemon；它仍受总 timeout、输出
  上限、临时目录和进程树清理约束。
