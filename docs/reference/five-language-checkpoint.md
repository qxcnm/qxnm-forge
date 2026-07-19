# qxnm-forge

> 暂定项目名；协议与持久化格式刻意不绑定品牌，后续可安全更名。

- 作者：高宏顺
- 邮箱：18272669457@163.com

代码约定：每个新增函数/方法都使用对应语言的标准方法级文档注释，写明中文功能描述、作者与邮箱；公共接口及安全敏感方法还应说明输入、输出、不变量和失败条件。

qxnm-forge 正在建设 .NET、Rust、Go、TypeScript 和 Python 五套相互独立的原生 Agent 底座。实现之间只共享语言中立的规范与黑盒一致性测试，不共享运行时代码。

规范性真相来源如下：

- `SPEC/`：协议、领域模型、状态机、持久化、Provider、执行器、CLI 与安全语义；
- `CONFORMANCE/`：确定性夹具与跨实现黑盒测试；
- `SPEC/capabilities.json`：公开支持能力声明；
- `docs/`：架构、威胁模型、兼容策略、验证证据与第三方归属。

`PI/` 是并排放置、固定于 `3f9aa5d10b35223abf6146f960ff5cb5c68053ee`
的只读 MIT 参考 checkout；任何实现都不得在构建或运行时依赖它。

## 当前里程碑

当前里程碑已经建立治理、SPEC v0.1、确定性 conformance，以及相互独立的 Rust、
.NET、Python、TypeScript 和 Go 原生 core/CLI 纵切面。五套 daemon 均已通过共同
9 帧 golden trace、portable Session 恢复与续接、15 个工具/审批/取消动态案例（含
timeout、stdin EOF、success-frame delivery failure 与 `SIGKILL` 恢复），
以及 Rust→.NET→Python→TypeScript→Go 和反向五阶段线性 Session handoff。

五种语言现在都原生实现并通过前两批共 42 个本机 mock HTTP/SSE 案例，覆盖
OpenAI-compatible Chat、OpenAI Responses、Anthropic Messages、Mistral
Conversations、Azure OpenAI Responses 与 Google Generative AI。共同 Linux
writer lease runner 还真实通过五语言有序 5×5 的 25 个 live-reject、SIGKILL、
stale-takeover、恢复和 clean-release pair；Windows/macOS 未实机验证，因此该能力
仍保持 `implemented`。

第三批 Vertex、Bedrock ConverseStream 与 Codex Responses 公共契约已经冻结；Rust、
.NET、Go、TypeScript 和 Python 均已分别通过 21/21 动态门禁，其中 Bedrock 全部使用
原生 AWS EventStream 双 CRC 二进制解析而非 SSE。九个文本 API family 的五语言
text/streaming/retry/cancellation 共同证据现已闭环；所有结论均为本机合成凭据证据，
不等于真实 Provider live smoke。

首批 OpenAI-compatible Chat、OpenAI Responses 与 Anthropic Messages 的工具案例现已
形成完整两轮 HTTP 闭环：第一轮增量组装 `file.read`，本地真实执行固定 README，随后
以 family-native 结果精确回注第二次请求，并由第二个 Provider 响应完成同一 run。共同
runner 还验证唯一 `requested→started→completed` 生命周期、固定 ToolResult、累计 usage
`14/10/24` 和 durable journal 顺序；五套 fresh daemon 均为 21/21，因此这 15 个
API-family `tools` 单元已提升为 `conformant`。

35 Provider / 45 route / 1,076 模型的 manifest-driven 广告契约也已冻结。静态 census
与安全加固的假 daemon 黑盒门禁验证 manifest、认证 presence、endpoint、native adapter
和 capability allowance 五项交集，以及 OpenRouter 跨 family 重名模型保留。Rust、.NET、
Go、TypeScript 和 Python 五套原生 daemon 均已接入只读 identity-only advertisement
snapshot，并各自通过同一 runner 的 14 个正例与四个 strict 负例。`35 providers / 45
routes / 1076 models` 只是广告选择算法与冻结目录的证据；identity-only route 不注册执行
adapter，对应 `run/start` 以 `-32005/provider_unavailable` 失败且不创建 Session。Provider
identity-only gate 本身不产生任何执行支持 claim，也不得据此表述为“35 个 Provider 已接入”；
ADR 0018 的精确 route-scoped 记录在下文单独说明。

ADR 0018 进一步冻结首批六条 canonical executable Route Spine：
`groq/openai-completions`、`minimax/anthropic-messages`、
`mistral/mistral-conversations`、`openai/openai-responses`、
`google/google-generative-ai` 与 `openrouter/openrouter-images`，对应 catalog allowlist
合计 133 个模型。五套原生 daemon 均已通过共同 runner 的六个回环 happy path 与九个
失败关闭案例，以及四项普通生产门禁。生产 route 只在 manifest 指定的 API-key 环境值
非空时广告；model 必须
命中同一不可变 snapshot 的 catalog allowlist，credential 值只在最终 HTTP header 边界
读取。测试 override 需要一般 conformance 与 `QXNM_FORGE_PROVIDER_CONFORMANCE=1` 双门控，
并且只接受 runner 分配的 literal `127.0.0.1` endpoint。其余 39 条 route 继续 fail
closed。能力矩阵只把这六条精确 route 的已验证 feature 登记为 `conformant`：五条文本
route 为 `authentication/text/streaming/tools`，图像 route 为
`authentication/text/image_input/image_output`；其余 39 条 route 与全部 `live_smoke`
仍为 `unsupported`。以上证据全部来自 synthetic credential 和本机 mock，不是
`live-verified`。五种语言各 24 个 route-feature cell，共 120 条精确 claim，不是 Provider
品牌级布尔值；这些 cell 受共享 Schema 单测约束，并严格限定 Linux offline loopback。
缺少原子 no-follow 的平台 fail closed，不在本批 claim 范围。

本检查点的完整统一离线门禁结果为 `PASS=103 FAIL=0 SKIP=0`。

另一个不启用 conformance/config 的 initialize-only 生产广告 smoke 在五种语言均得到
7 个 Provider（含 `faux`）与 134 个 model ID（含 `faux-v1`），且没有发网络请求；它只
证明六 route/133 模型的本地 presence 广告，不是 live Provider smoke。
另外三项生产门禁证明 allowlist 外 Azure/Anthropic 环境不能注册 route、identity/route
双 presence 在读取任一路径前拒绝、空 route presence 也必须启动拒绝；四项都不创建
Session 或发出 HTTP 连接。

唯一图像 family `openrouter-images` 也已在五种语言独立原生实现，并由主线程用 fresh
daemon 分别复验共同 artifact-first 13/13。门禁覆盖显式 `apiFamily` 路由、冻结 35
模型、PNG/JPEG/WebP/GIF、输入 `image_ref` 身份/hash 复核、重试/断流/超时/取消、无
`message.delta`、无覆盖 artifact 发布，以及协议/Session 全树无 credential、base64、
data URL、远程 URL 或宿主路径泄漏。该能力为 `conformant`，尚未 `live-verified`。

本里程碑不是完整 v1.0。branch/compaction 的规范、tree fixture、固定错误映射和
真实 mutation runner 已冻结，五种语言均通过完整公共门禁。共同 runner 还完成
Rust→.NET→Python→TypeScript→Go 与反向五阶段 branch/compaction handoff，
每阶段都保持 append-only 旧字节前缀和 compaction-aware selected projection。
PI v3 clean-room 导入规范、脱敏夹具、报告 Schema 和安全负例也已冻结；五种语言的
原生 importer 均已通过共同逐字节黑盒门禁。尚需完成 35 个 Provider 的真实
route/auth/quirks 原生执行接入与 live 验证，生产 PTY/ConPTY、handle/no-follow
TOCTOU、五语言 hard-sandbox runtime adapter、
Windows/macOS 实机矩阵和显式 live smoke，之后才会进入 v1.0。

Executor/PTY 的公共 Schema、ADR、固定安全 helper 和黑盒 runner 已冻结。五套 daemon
都已通过 Linux process/shell 和长期存活 `setsid` 后代的清理回归；独立审查确认毫秒级
`/proc` 采样不能封闭快速 double-fork/reparent，因此五语言 execution wire 只报告
`unix_process_group`，`executor.process_tree_unix` 保持 `implemented`。Python PTY 在后续
安全复审中发现 helper 启动竞态、PID/PGID 身份、资源上限与 replay/event 顺序等不变量
尚未闭合，现已 fail-closed：不广告 `terminal/*` 或 terminal event type，直接 RPC 返回
`-32601/method_not_found`，并撤销 `terminal.open` 能力记录。Rust 仅在 Linux 且
`QXNM_FORGE_CONFORMANCE=1` 与 `QXNM_FORGE_TERMINAL_CONFORMANCE_POLICY=fixture-only` 双门控下
广告真实 PTY，并已通过 identity/I/O/resize/signal、严格 attachment replay/takeover 和
64 KiB 输入背压原子拒绝三个动态案例；能力矩阵仅登记 `terminal.open=implemented`。
该测试专用证据不覆盖 retain/detach、owner disconnect、生产 policy、hard sandbox、macOS
或 Windows/ConPTY，不是 `conformant`、`live-verified` 或公开支持。Python、.NET、Go、
TypeScript 仍无 `terminal.open` claim，其生产 PTY 与跨平台进程树不能由 Linux fixture 外推。
Linux Bubblewrap 公共 hard-sandbox 契约已通过真实 socket、timeout/output、fork+setsid
清理和失败关闭本机门禁，但尚无五语言 runtime adapter，因此不登记实现支持。

能力状态只使用 `unsupported`、`implemented`、`conformant` 和
`live-verified` 四级；只有机器可读能力矩阵中的记录构成支持声明。
Provider 记录的 claim identity 是 `(providerId, apiFamily, feature)`，不是品牌级
`(providerId, feature)`；同一 Provider 的 sibling route 必须分别取证，不能互相外推。

组件边界见 `docs/architecture.md`，可复现验证命令见 `docs/verification.md`。
