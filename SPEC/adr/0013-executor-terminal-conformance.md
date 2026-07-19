# ADR 0013：原生执行器与 PTY/ConPTY 生命周期契约

- 状态：Accepted
- 日期：2026-07-14
- 项目：qxnm-forge
- 作者：高宏顺 `<18272669457@163.com>`

## 背景

Rust 与 .NET 两种 active 原生实现都已经有 `process.exec`/`shell.exec` 的局部实现；历史
Go、TypeScript、Python 只保留冻结检查点。当前请求和结果
字段不完全一致：有的只接受扁平 `timeoutMs`，有的已经分别统计 stdout/stderr，
有的把结果渲染成文本；Unix 主要使用进程组，Windows 多数实现尚未提供 Job
Object。现有实现也没有共同的 PTY/ConPTY RPC、attachment ownership、resize、
backpressure 或 replay 语义。仅凭“子进程退出”不能证明后代已被清理，普通 pipe
也不能证明是终端。

## 决策

### 1. 结构化执行结果

`SPEC/schemas/executor.schema.json` 冻结语言中立的 normalized request/result。
Provider tool 的既有最小参数仍然有效；实现把扁平兼容字段归一化为该 schema，逐步
增加 `stdin`、显式环境覆写和独立 stdout/stderr/total limits。`domain/toolResult`
增加可选 `execution` 字段，因此旧客户端可以忽略它，支持新契约的实现必须提供它。

`execution` 必须报告：

1. `exitCode`、`signal`、`terminationReason` 和 `durationMs`；
2. `stdout`、`stderr` 的 captured/total/omitted bytes、显式 truncation 和
   UTF-8 replacement 文本；
3. `containment` 级别。`direct_process` 可以诚实表示当前实现能力，但不能作为
   adversarial tree conformance 证据。

非零退出是正常完成的 `terminationReason: "exit"`，并在 ToolResult 中设置
`isError: true`。启动失败没有 `execution`，只返回 `process_start_failed`。

### 2. 环境与输出安全

子进程环境从 allowlist 开始；wire `environment` 只表示显式 set/unset，不能开启
“继承全部 daemon 环境”。凭据名称和值在审批、journal、事件和 artifact 中都必须
脱敏。两个输出管道必须持续排空，内存保留量和 retained replay 窗口有硬上限。

### 3. 进程树等级

- Unix 普通等级创建独立 process group/session，并在取消/超时/输出超限时 TERM→
  KILL→reap；只有实现能够处理 `setsid`、double-fork 等逃逸后代，或使用 OS
  isolation，才可把 `unix_process_group_descendant_guard` 声称为 adversarial
  conformance。
- Windows conformance 必须使用 `CREATE_SUSPENDED`，先创建并配置 kill-on-close
  Job Object、完成 `AssignProcessToJobObject`，再 ResumeThread。先正常启动、之后
  绑定 Job 的实现即使普通样例通过，也不具备该能力声明。
- process、shell、terminal 的 timeout、cancel、disconnect、daemon shutdown 和
  output-limit 都必须走同一个 tree cleanup path。

### 4. terminal RPC 与事件

在不改变既有 Agent `event` envelope 的前提下，新增可选 JSON-RPC 方法：
`terminal/open`、`terminal/write`、`terminal/resize`、`terminal/signal`、
`terminal/attach`、`terminal/close`，以及 server notification `terminal/event`。
完整 wire DTO 位于 `SPEC/schemas/protocol/terminal.schema.json`。

- `terminal/open` 返回 connection-bound `attachmentId` 和 `ptyKind`；只能是
  `unix_pty` 或 `windows_conpty`，不能把 pipe/socket 当作 PTY。
- 一个 terminal 同时只有一个控制 attachment。write 使用严格递增 `inputSeq`；
  输入队列满时返回 retryable `-32011/terminal_backpressure`，不接受部分输入。
- output 使用 per-terminal `seq`，合并为 `stream: "pty"`；attach 返回
  `replayThroughSeq`，响应之后才发送 replay notifications。live 轨迹必须按
  output/truncated → 唯一 exited → 唯一 closed 排序，closed 后不得再发事件；只有
  response-delimited replay window 可以重用旧 output seq。
- `limits.eventBytes` 限制单个 output event 表示的 PTY 原始字节，不替代协商的
  `maxEventBytes`/`maxFrameBytes`；实现必须把 JSON escaping 与 envelope 开销计入
  实际序列化帧预算。
- retain 也受 lifetime/idle/retention 限制；默认断开策略是 terminate。attachment
  token 不得写入 Session、日志或 artifact。

### 5. 最小版本兼容

协议版本仍为 `0.1`：新增字段全部 optional，新增方法只有在 initialize capability
中声明时才可调用；旧客户端继续使用现有 process/shell/tool 字段。旧实现没有
terminal 能力时必须诚实省略方法和 `terminalEventTypes`，不得返回 pipe 冒充 PTY。
采用独立 `terminal/event` schema 避免让旧 Agent event 消费者误解析 terminal
事件。

## Conformance 与平台策略

`CONFORMANCE/fixtures/executor/executor-cases.json` 是无网络机器夹具，
`CONFORMANCE/fixtures/executor/executor_helper.py` 只执行本机解释器和临时工作区内
的固定动作。`CONFORMANCE/executor_runner.py` 默认执行静态 Schema/安全门禁和 helper
自测；显式提供 daemon argv 才运行黑盒 process/shell/terminal 探针。

必测行为包括 argv 边界、双流/非零、stdin、超时、取消、输出上限、secret canary
隔离、Windows suspended Job 绑定、PTY 身份、write 背压、resize、signal、attach replay
和 close cleanup。当前 Unix helper 的长期存活 `setsid` delayed-write 只是一项清理回归，
不能证明毫秒级 `/proc` 采样封闭首次采样前的快速 double-fork/reparent；因此这类实现
只能返回 `unix_process_group` 并保持 `implemented`。只有 OS containment 或等价的
pre-resume 绑定通过专门 double-fork 门禁后，才可返回
`unix_process_group_descendant_guard`。runner 永远创建临时 workspace，删除
Provider/cloud credentials、proxy 和 endpoint 环境，不联网。

当前宿主不是 Windows 时 Windows Job/ConPTY 案例标记 `skip(platform_unavailable)`；
没有真实 PTY/ConPTY 的实现标记 `skip(capability_unavailable)`，不能伪造 PASS。普通
process-group 只能报告 `implemented`，不能把 Unix 后代攻击案例提升为 `conformant`。

Terminal 动态探针不会把 `--conformance` 当成通用授权。runner 额外设置
`QXNM_FORGE_TERMINAL_CONFORMANCE_POLICY=fixture-only`，其语义只允许当前临时 workspace
内、与冻结 helper SHA-256 一致的 `executor_helper.py terminal`，并要求空环境覆写、
固定本机解释器和受限 limits；任一字段漂移必须拒绝。该变量由启动 daemon 的进程
所有者显式提供，是测试专用的窄策略输入，不得在生产模式生效，也不能授权任意
executable、argv、shell、路径或后续 terminal。没有生产级显式策略的实现即使通过
此 fixture，也只能把 terminal 记为 `implemented`，不能公开宣称通用 terminal 支持。

## 取舍

本 ADR 不定义 hard sandbox；只有 container/VM/OS isolation 才能声明 hard sandbox。
也不规定各语言的具体 PTY 库或 Job Object 封装，具体 API 留给五个原生实现。规范、
Schema、fixtures 和 normalized trace 才是共同真相，任何单一实现未记录的行为都不
自动成为标准。
