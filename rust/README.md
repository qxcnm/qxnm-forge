# qxnm-forge Rust 实现

作者：高宏顺 `<18272669457@163.com>`

本目录是完全独立的 Rust 原生实现。构建和运行均不导入、不启动其他语言实现，也不依赖
或执行任何参考项目 runtime。公共行为以 `../SPEC/` 为准，`../CONFORMANCE/` 只通过 daemon 黑盒协议验证。

## Architecture

- `domain`：语言中立消息、内容块、Provider 流事件、usage 与工具结果。
- `provider`：按 API family 复用的原生 HTTP/SSE adapter，以及确定性 faux Provider。
- `agent`：run/turn/message/tool 状态机、审批、取消、steer/follow-up 和恢复协调。
- `session`：版本化 append-only JSONL tree、selected head、compaction 投影、artifact 与 writer lease。
- `tools` / `executor` / `terminal` / `policy`：工具注册、进程执行、Unix PTY 生命周期、审批决策与 canonical workspace 边界。
- `protocol` / `daemon` / `cli`：严格 JSON-RPC DTO、UTF-8 NDJSON stdio daemon 和纯文本 CLI。

公开 wire DTO 不暴露 Rust 泛型、错误对象或字节缓冲区。未知扩展只能进入显式
`extensions` 命名空间；Provider 原始分片与敏感错误正文不会写入 portable Session。

## Run

```sh
cargo run -- daemon --workspace /path/to/workspace --state-dir /path/to/state
cargo run -- run --workspace /path/to/workspace "hello"
```

显式 `--state-dir` 优先；省略时使用 `QXNM_FORGE_SESSION_ROOT`（公共 conformance runner），
若该环境变量也未设置则使用平台用户级状态目录（Windows 的 `LOCALAPPDATA`，或
`XDG_STATE_HOME` / `$HOME/.local/state`），不会默认把 Session 放进 Agent 可操作的工作区。

交互审批默认五分钟到期。`QXNM_FORGE_APPROVAL_TIMEOUT_MS` 仅在 daemon argv 含独立
`--conformance` 且 `QXNM_FORGE_CONFORMANCE=1` 时读取，有效范围为 1 毫秒到 1 小时；任一
单门或非法文本都回到生产默认值。到期或 stdio 客户端断连都会 durable 记录 `deny`，
不会让 Agent 永久等待。

默认 Provider 是确定性 `faux`。默认工具策略允许工作区内读取，拒绝写入以及
process/shell 执行。RPC stdout 只输出协议帧，诊断只进入 stderr。

## 签名远程推广 Provider 目录

Rust 原生提供 `sponsors keygen/sign/verify/configure/list/use/installed/remove`，可在不重新发布客户端的情况
下从 HTTPS 刷新明确标注 `[推广]` 和返佣披露的中转站目录。签名、回滚、缓存、空目录和
双端命令详见 [中文运维文档](../docs/sponsored-provider-commercialization.md)。远端条目不会
自动执行；`sponsors use` 必须明确接受披露并固定非敏感 route，`auth set` 再从重定向 stdin
把 API key 写入工作区外 0600 CredentialStore。已安装的 `openai-completions`、
`openai-responses`、`anthropic-messages` 复用现有 family adapter，密钥在每次请求 header
边界重新读取，缺失时在 Session 创建前拒绝。

Linux `file.read`/`file.write` 从 daemon 启动期持有的 workspace root directory FD 开始，
逐组件使用 `openat` 与 no-follow 语义打开最终 parent。读取只从已经 `fstat` 为普通文件的
leaf FD 取字节；在 leaf checkpoint 前还会确认静态大小不超过 1 MiB，实际读取最多观察
`1 MiB + 1` 字节以拒绝检查后增长。256 KiB 到 1 MiB 的内容仍按既有行为转存 Session
artifact，超过 1 MiB 返回 `output_limit`。写入在同一 parent FD 下 create-exclusive 随机临时文件，完整写入并
`fsync` 后用 `renameat` 替换 leaf 名称，失败清理使用 `unlinkat`。因此 workspace root、
nested parent 或 leaf 在检查后的 rename/symlink rebind 不会把 I/O 转向工作区外对象。
该实现不改变非 Linux fallback，也尚未覆盖 `file.edit`、`search.text` 和 process cwd，
所以宽泛的 `security.path_boundary` 能力仍保持 `implemented`。

ADR0021 的确定性 race hook 仅供公共 conformance runner 使用。只有 daemon argv 含精确
`--conformance` 且 `QXNM_FORGE_CONFORMANCE=1` 时，才读取
`AGENT_PATH_BOUNDARY_CONFORMANCE_CONFIG` 指向的 strict JSON；缺任一道门或配置非法都会在
`initialize` 返回不可重试 `-32603/conformance_configuration_invalid`，不会打开配置路径。
生产部署必须保持该环境名未设置。hook 固定为 `before_parent_walk`、
`after_parent_open`、`after_leaf_open_before_read` 和
`after_temp_fsync_before_rename`，控制目录必须是非链接的 `0700` 目录。

Linux 原生 executor 使用 `unix_process_group` containment，支持严格 argv、bounded stdin、
独立 stdout/stderr、secret 环境隔离、超时、输出上限和显式 POSIX shell。5ms `/proc`
身份采样只用于对已观察后代做 best-effort cleanup，不证明覆盖 adversarial `setsid`、
double-fork 或快速 reparent。Linux `terminal/open` 提供真 PTY、write/resize/signal、
attachment rotation、bounded replay、lifetime/idle 回收和统一进程树清理；当前只在
`QXNM_FORGE_CONFORMANCE=1` 与 `QXNM_FORGE_TERMINAL_CONFORMANCE_POLICY=fixture-only` 同时存在时
广告，并将权限收窄到 runner parent executable fd identity、完整 sealing 的 helper memfd、
Python `-I`、空 environment、`disconnectPolicy=terminate`、`terminal/close mode=terminate`
和受限 limits。能力矩阵仅将该 Linux fixture-only `terminal.open` 登记为 `implemented`；
它是测试专用实现证据，不是 `conformant`、`live-verified` 或公开支持。当前三个动态案例
覆盖 PTY identity/I/O/resize/signal、response-delimited replay/takeover/旧 attachment、
事件帧预算与 64 KiB 输入背压原子拒绝；尚未动态覆盖 retain/detach、owner disconnect、
daemon shutdown/`SIGKILL`、idle/lifetime expiry 或生产 terminal policy。macOS、Windows
及任何非 Linux 平台均不会用 pipe 冒充或广告 terminal；Windows suspended Job/ConPTY
尚未接入。

## 第三方 Session v3 一次性导入

Rust CLI 提供完全离线的 clean-room 导入命令：

```sh
cargo run -- session import-session-v3 \
  --source /path/to/session.jsonl \
  --workspace /path/to/workspace \
  --state-dir /path/to/state \
  --session session-new-id \
  --format json
```

`--source`、`--workspace` 和 `--state-dir` 必填；省略 `--session` 时生成新的 opaque
Session ID。目标 ID 必须不同于 source header ID，且目标目录已存在时拒绝，不覆盖、
不合并、不续写。成功 JSON stdout 只包含 `status`、`sessionId` 和
`reportArtifactId`，不会输出 source 路径、source cwd、quarantine 内容或目标文件系统路径。
旧命令名 `import-pi-v3` 仅作为兼容 alias 保留。

导入器只读打开普通非符号链接 source，严格检查 UTF-8/LF、重复 JSON 键、大小、行长、
深度、唯一 ID 与 earlier-only parent tree，并在发布前复核文件身份。它不会加载或执行
第三方 runtime、Provider、工具、扩展或历史 run。转换先在同父 staging 目录写入并 flush artifacts，
再写入完整 journal；通过普通 Session parser、provenance、报告 hash 和禁止生命周期记录
检查后，Linux 使用 no-replace 原子 rename 发布。任何失败都会清理未发布 staging。

第三方 branch jump 映射为显式 `branch.selected`；compaction 映射为 summary message 加
`context.compacted`。custom、custom message、label 与未知 entry 进入
`org.agentprotocol.pi-v3` 隔离边界并登记 mandatory report，不会进入 Provider context。
报告固定使用 `application/vnd.qxnm-forge.pi-v3-import-report+json`，header 绑定 source
SHA-256、source Session ID、报告 artifact 和固定参考提交。生产模式生成新 record/artifact
ID；`--conformance` 只接受公共合成 fixture 的固定 SHA/ID，不能放宽解析或安全策略。

## Session journal 尾部恢复

普通 `header`/`load` 和 writer open 会把任何非空无 LF 尾部视为未提交，即使它是合法
JSON 或完整 record。恢复先取得 portable writer lease、advisory lock 和独占 append lock，
完整验证 LF prefix，再把修复前全部原始字节无覆盖保存到
`journal.recovery-<originalSha256>.bak`；只有 backup durable 后才截断并追加
`org.agent-session.recovery` extension。相同 backup 只在逐字节一致时复用，重复打开不会
再次写入。

任何 LF 完整行中的 UTF-8、JSON、递归重复键、Schema、序号、引用或状态机损坏均返回
`-32008/journal_corrupt`，journal 原字节不变且不创建 recovery backup。Rust 原生黑盒门禁：

```sh
cargo build
python3 ../CONFORMANCE/session_tail_runner.py \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["./target/debug/qxnm-forge","daemon","--conformance","--workspace","{workspace}","--state-dir","{stateRoot}"]'
```

## Session branch 与 compaction

Rust daemon 已实现 `session/get`、`session/branch/select` 和 `session/compact`。Journal
通过 `parentId` 形成不可变树，普通追加只能延伸当前 selected head；
`branch.selected` 是唯一允许把 parent 指向 earlier record 的核心控制记录，并且该控制
记录本身成为新的 selected head。`session/get` 在能力启用时返回
`selectedHeadRecordId`，存在有效压缩时另返回 `compactionRecordId`。

压缩严格追加两条已同步记录：先写一个 canonical assistant summary，再写
`context.compacted` 激活投影。Provider 的后续上下文依次为最新 summary、retained
boundary 到 source 的消息、以及压缩记录之后至 selected head 的消息；旧前缀和未选
sibling 仍保留在 journal 中，但不进入 Provider 上下文。若进程在 summary 后、
compaction 前退出，该 summary 仍只是普通 assistant 消息，不会激活半次压缩。

两项 mutation 都与 `run/start` acceptance 共用进程内互斥，并在跨进程 writer lease
内先恢复、再执行 expected-head CAS、最后追加和 flush。冻结错误映射如下：

| 条件 | code | retryable | `details.kind` |
| --- | ---: | :---: | --- |
| expected head 已变化 | `-32010` | true | `stale_session_head` |
| 当前 Session 有活动生命周期 | `-32004` | true | `session_busy` |
| target 不存在 | `-32602` | false | `record_not_found` |
| historical target 非静止 | `-32010` | false | `branch_not_quiescent` |
| retained boundary 非法 | `-32602` | false | `invalid_compaction_boundary` |
| `tokensAfter > tokensBefore` | `-32602` | false | `invalid_compaction_tokens` |
| journal tree/引用损坏 | `-32008` | false | `journal_corrupt` |
| writer 被其他进程持有 | `-32002` | true | `session_locked` |

失败 mutation 不得追加 journal。Durable events 始终按 Session 全局 `event.seq` 返回，
不会随 branch 过滤或重新编号。当前实现兼容公共线性 fixture 与 branch/compaction tree
fixture，并能在保持原字节前缀的前提下继续 faux run。

## 原生 Provider family

### Manifest 驱动的 Provider 身份广告

Rust daemon 已实现 ADR 0017 的 conformance-only identity 选择层。仅在
`QXNM_FORGE_CONFORMANCE=1` 且存在 runner 生成的
`AGENT_PROVIDER_IDENTITY_CONFORMANCE_CONFIG` presence 文件时，启动过程才从代码固定的
`SPEC/providers.v1.json` 与 `SPEC/models.v1.json` 构建 immutable advertisement
snapshot；该分支先于 live transport、endpoint 和 credential 处理，不构造可执行 adapter，
也不读取 credential canary。生产模式发现该 presence 名称会立即 fail closed。

`models/list` 按 `(providerId, modelId, apiFamily)` 稳定排序并保留 OpenRouter 的跨 family
重名描述，`initialize.capabilities.providers[].models` 从同一快照生成排序去重并集。Rust
已通过公共 identity runner 的 14 个正例与四个 strict 负例，完整正例汇总为
`35 providers / 45 routes / 1076 models / 14 cases`。这些计数只是广告算法证据；
identity-only 模型不注册执行 adapter，对应 `run/start` 在任何 Session 副作用前返回
`-32005/provider_unavailable`。identity-only gate 本身不产生执行支持 claim，也不得
表述为“35 个 Provider 已接入”；下节 ADR 0018 route 使用独立证据。

### Manifest 驱动的可执行 Route Spine

Rust 已实现 ADR 0018 的首批六条 canonical route：`groq/openai-completions`、
`minimax/anthropic-messages`、`mistral/mistral-conversations`、
`openai/openai-responses`、`google/google-generative-ai` 与
`openrouter/openrouter-images`，六条 catalog allowlist 合计 133 个模型；其余 39 条
manifest route 继续 fail closed。普通启动严格验证冻结 manifest/catalog，只在对应 API-key
环境值非空时从同一不可变快照广告并注册 route；modelId 必须命中该 route 的 catalog
allowlist。省略 `apiFamily` 只在 `providerId + modelId` 唯一命中时允许；wrong family、
unknown model 或缺 credential 均在 Session 创建和 durable record 之前返回
`-32005/provider_unavailable`。五个文本 adapter 与图像 adapter 都不长期保存 credential
值，而是在最终 HTTP header 构造边界读取当前环境值，因此轮换对下一次请求生效。

公共 `provider_route_runner.py` 已通过六个回环 happy path、九个 strict/安全负例与四项
普通生产门禁（`6 positives / 9 negatives / 4 production gates`）。runner 的
`AGENT_PROVIDER_ROUTE_CONFORMANCE_CONFIG` 只有在 `QXNM_FORGE_CONFORMANCE=1` 和
`QXNM_FORGE_PROVIDER_CONFORMANCE=1` 同时存在时才会 no-follow、有界、strict JSON 读取；生产
只要发现该 presence 就在读取文件、CLI、transport 或 credential 前脱敏拒绝。该结果是
离线 canonical route 证据，不是 `live-verified`；真实模型调用仍需独立 live smoke。
公开 Provider claim 必须以 `(providerId, apiFamily, feature)` 为 identity；六条 route
分别登记，不能合并成 Provider 品牌级能力或向 sibling family 外推。
本实现中五条文本 route 的 `authentication/text/streaming/tools` 与图像 route 的
`authentication/text/image_input/image_output` 为 `conformant`；其余 39 条 route 和全部
`live_smoke` 仍为 `unsupported`。
这些 claim 只覆盖 Linux offline loopback；缺少原子 no-follow 的平台 fail closed，不在
本批 claim 范围。

额外的生产广告 smoke 同时设置六个合成非空 credential，不设置任何 conformance/config，
并使用 dead proxy；`initialize` 返回 7 个 Provider（含 `faux`）和 134 个 model ID（含
`faux-v1`），且没有发出网络请求。这只证明普通启动的六 route/133 模型广告交集，不是
真实 Provider smoke，也不把其余 route 或 live 能力视为支持。
其它三项 production gate 还证明 allowlist 外 Azure/Anthropic 环境只剩 `faux`、
identity/route 双 presence 在读取任一路径前拒绝、空 route presence 启动拒绝；全部使用
network tripwire 并保持零 HTTP/Session。

### 可执行 family adapter

原生 HTTP adapter 按 API family 实现。首批支持 OpenAI-compatible Chat、OpenAI
Responses 和 Anthropic Messages；第二批实现 Mistral Conversations、Azure OpenAI
Responses 与 Google Generative AI；第三批实现 Google Vertex、Amazon Bedrock
ConverseStream 和 OpenAI Codex Responses；图像纵切面实现冻结的
`openrouter-images`。旧 family mock endpoint 仅在显式 conformance 模式注册；普通生产的
Mistral、Google 与 OpenRouter 请求改由上述 canonical Route Spine 使用冻结 HTTPS base。
Azure Responses 不在 ADR 0018 的六条 allowlist 中，普通生产即使存在
`AZURE_OPENAI_API_KEY`、`AZURE_OPENAI_BASE_URL` 或 `AZURE_OPENAI_API_VERSION` 也不会
读取 secret 或注册 route；旧 adapter 仅在显式 Provider conformance 下使用
`QXNM_FORGE_AZURE_OPENAI_RESPONSES_ENDPOINT` 与合成 credential 运行本机 mock。

第三批当前只在显式 `QXNM_FORGE_PROVIDER_CONFORMANCE=1` 的 clean-room 回环夹具中注入：
Vertex 使用 OAuth bearer 与 project/location/publisher/model 资源路径；Codex 使用
`/codex/responses`、`store:false` 和固定非敏感 adapter headers；Bedrock 每轮即时读取
合成 AWS 凭据并以纯 Rust 生成 SigV4，响应经独立 AWS EventStream parser 校验大端长度、
string typed headers、prelude CRC、message CRC、UTF-8 与严格 JSON，绝不通过 SSE parser。
这三项尚不构成真实云 credential flow 或 live-verified 支持声明。

OpenRouter Images 在显式 `"apiFamily":"openrouter-images"` 时精确路由；省略 family 仅按
Route Spine 的唯一命中规则解析，不能按注册顺序或动态 modelId 猜测。
生产配置使用 `OPENROUTER_API_KEY` 和固定 `https://openrouter.ai/api/v1` base；密钥值
不保存在 adapter 中，只在最终 `Authorization: Bearer` header 边界读取。请求为原生
非流式 `POST /chat/completions`，冻结目录中的 35 个模型按能力选择
`["image"]` 或 `["image","text"]` modalities。

输入 `image_ref` 必须来自同一 Session selected chain 上更早的 `artifact.created`，读取时
执行 regular-file/no-follow、长度、SHA-256、MIME 与 PNG/JPEG/WebP/GIF 魔数复核。
响应只接受 strict UTF-8 JSON 和 canonical base64 data URL；remote URL、SVG、宽松
base64、MIME 欺骗、重复 JSON key、尾随 JSON 及超限内容都会失败。整批图像先验证，随后
以临时文件、flush、无覆盖发布和目录 flush 的顺序逐张写入 artifact，再追加 assistant
`image_ref` message；图像不产生 `message.delta`，bytes、base64、data/remote URL 和 host
path 不进入协议、journal 元数据、日志或错误。

`QXNM_FORGE_MISTRAL_CONVERSATIONS_ENDPOINT`、
`QXNM_FORGE_AZURE_OPENAI_RESPONSES_ENDPOINT` 和
`QXNM_FORGE_GOOGLE_GENERATIVE_AI_ENDPOINT`，以及第三批的
`QXNM_FORGE_GOOGLE_VERTEX_ENDPOINT`、`QXNM_FORGE_BEDROCK_CONVERSE_STREAM_ENDPOINT`、
`QXNM_FORGE_OPENAI_CODEX_RESPONSES_ENDPOINT` 和图像 family 的
`QXNM_FORGE_OPENROUTER_IMAGES_ENDPOINT` 仅用于显式
`QXNM_FORGE_PROVIDER_CONFORMANCE=1` 的字面 `127.0.0.1` 黑盒测试。生产 endpoint
必须是无 userinfo/query/fragment 的 HTTPS URL；所有 Provider 请求禁止自动重定向和
环境代理，凭据仅在最终请求边界进入认证 header。

## Linux Bubblewrap Hard Sandbox

### 安装与配置

`linux-bwrap-v1` 只支持 Linux，并把 Bubblewrap 作为可选外部系统依赖；Rust crate 不
vendor 或下载它。Debian/Ubuntu 可执行 `sudo apt-get install bubblewrap`，Fedora 可执行
`sudo dnf install bubblewrap`，Arch Linux 可执行 `sudo pacman -S bubblewrap`。生产后端固定
为 `/usr/bin/bwrap`，不会搜索 `PATH`、跟随符号链接或接受自定义路径。启动前可先检查：

```sh
test -x /usr/bin/bwrap
test ! -L /usr/bin/bwrap
stat -c '%U %F %A' /usr/bin/bwrap
/usr/bin/bwrap --version
```

文件还必须是 root 所有的普通文件且当前用户不可写；daemon 会自行复核，以上命令不替代
启动自测。显式配置 profile 后启动 daemon：

```sh
AGENT_HARD_SANDBOX_PROFILE=linux-bwrap-v1 \
  cargo run -- daemon --workspace /path/to/workspace --state-dir /path/to/state
```

profile 缺失时 `initialize.capabilities` 不含 `hardSandbox`，普通 `process.exec`/
`shell.exec` 继续使用既有 host executor。profile 存在时，daemon 必须在成功响应
`initialize` 前完成 backend 身份、版本和完整 namespace 自测；任一步失败都会返回
`-32603`、`details.kind="sandbox_unavailable"` 并关闭连接，不会静默降级。测试专用
`AGENT_HARD_SANDBOX_CONFORMANCE_BACKEND` 只有 CLI 精确包含 `--conformance` 且
`QXNM_FORGE_CONFORMANCE=1` 时才可读取，生产环境必须保持未设置。

### 工具请求与审批

公共工具/审批门禁共 15 个动态案例：原有 11 个正常/取消案例之外，还真实覆盖 100 ms
timeout 后 late/duplicate conflict、stdin EOF disconnect、success frame stdout 交付失败和
`SIGKILL` 后 Session 恢复。每个故障都要求唯一 `approval.resolved`、零
`tool.started`、一个 outcome-known 工具结果/规范 tool message、零工作区修改和唯一终态。
Client `allow_once` 先落盘但只有 `{accepted:true}` 完整写出并 flush 后才释放；交付失败
保留原 client resolution 并以 `run.interrupted` 收尾，不补写第二个 deny。

Hard Sandbox 不新增 RPC。Provider 仍通过既有 Agent 工具循环发起 `process.exec` 或
`shell.exec`；其工具 arguments 必须显式携带封闭的 `sandbox` 对象，例如：

```json
{
  "executable": "/usr/bin/python3",
  "args": ["-c", "print('你好')"],
  "cwd": ".",
  "timeoutMs": 3000,
  "outputLimitBytes": 65536,
  "sandbox": {
    "profile": "linux-bwrap-v1",
    "workspaceAccess": "read_only",
    "network": "isolated"
  }
}
```

`cwd` 是 canonical workspace 下的相对路径，在 sandbox 内映射到 `/workspace`；不能传宿主
绝对工作区路径。需要写工作区时把 `workspaceAccess` 改为 `read_write`，并仍须获得该次
危险操作的 `allow_once` 审批。审批 arguments 与 operation hash 会绑定 profile、工作区
权限和 network 模式，客户端不能在批准后替换它们。成功或已经启动后的 timeout、输出超限
与取消结果都必须报告 `execution.containment="os_isolation"`。

### 安全边界与故障排查

该 profile 创建 user/mount/PID/network/IPC/UTS namespace，只读挂载必需系统运行时，
新建 `/proc`、最小 `/dev` 和 tmpfs `/tmp`，仅把 workspace 按请求精确 `ro-bind` 或
`bind` 到 `/workspace`；不会绑定宿主 `/`，也不会继承 Provider、云凭据或完整 daemon
环境。网络固定隔离，不能配置 host network。timeout、output-limit、取消和 daemon 父进程
退出共用完整进程树清理路径，并覆盖 sandbox 内 `fork()+setsid()` 后代。

它是 OS namespace 隔离，不是 VM；不承诺抵御 Linux 内核或 Bubblewrap 漏洞、资源侧信道，
也不能阻止已批准代码读取 `read_only`/`read_write` workspace 中本来就可见的内容。未携带
`sandbox` 的工具调用仍是 host executor，路径 canonicalization 和普通进程组也不能替代
本 profile。

若 `initialize` 返回 `sandbox_unavailable`，依次检查 Linux 平台、profile 拼写、
`/usr/bin/bwrap` 的类型/所有者/权限/版本，以及宿主或容器是否禁用了 unprivileged user
namespace、mount/network namespace 或相关 LSM 能力。read-only 写入失败和 sandbox 内无法
连接网络是预期行为；可执行文件只在只读系统运行时中可见，工作文件只在 `/workspace`
中可见。生产启动若发现 conformance backend override 会主动拒绝，应删除该环境变量。

### 验证

从 `rust/` 目录运行完全离线的 fresh daemon 动态门禁：

```sh
cargo build
python3 ../CONFORMANCE/hard_sandbox_runner.py \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["./target/debug/qxnm-forge","daemon","--conformance","--workspace","{workspace}","--state-dir","{stateRoot}"]'
```

预期摘要为 `6 positives / 4 negatives / 2 production gates`。套件使用临时 workspace、
loopback listener、随机 credential canary 和 faux Provider，不访问公网或付费模型；它还验证
RO/RW、cwd/argv/UTF-8、`/tmp`、系统只读、宿主外不可见、网络隔离、无凭据继承、
timeout/output/cancel/daemon-exit 整树清理，以及缺失、用户可写、伪隔离 backend 的
`sandbox_unavailable` 无 host fallback。

## Security boundary

Session 默认写入工作区之外的平台状态目录；跨进程写入使用 advisory lock、原子
`writer.lock.d/owner.json` 和字面 `127.0.0.1` 存活见证。崩溃接管必须保留既有 journal
字节前缀，恢复不会自动重放 Provider 或非幂等工具。工作区内 read/list/search 可按策略
直接运行，write/edit/process/shell 仍默认要求批准；无头模式缺少批准策略时拒绝危险操作。

路径 canonicalization 和 symlink escape 防护只是 workspace boundary；只有工具请求显式
选择并成功建立上节 `linux-bwrap-v1` 时才宣称 OS isolation。fixture-only terminal 会以
`O_NOFOLLOW` 打开并复核 helper 的 device/inode/
size/SHA-256，把内容复制到带 `F_SEAL_WRITE|GROW|SHRINK|SEAL` 的匿名 memfd；解释器绑定
Linux runner parent 的已打开 executable fd，child 通过继承 fd 以 `-I` 和空环境执行。
PTY child 在 exec 前完成 `setsid` 与 `TIOCSCTTY`。Linux `file.read/file.write` 的
handle-relative TOCTOU 已由 ADR0021 覆盖；生产级通用 terminal policy、其他文件工具、
process cwd、mount change、Windows reparse/UNC/ADS 与三平台攻击矩阵仍属于后续加固范围。

## Verify

Linux `file.read/file.write` 的六项确定性 race 与两项生产缺门门禁：

机器定义由 `../CONFORMANCE/fixtures/security/path-boundary-race-cases.json`、
`../SPEC/schemas/path-boundary-race-cases.schema.json` 和 ADR 0021 共同冻结；不传 daemon
命令时只运行静态契约校验，传入下列 JSON argv 后才驱动 fresh Rust daemon：

```sh
cargo build
python3 ../CONFORMANCE/path_boundary_runner.py \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["./target/debug/qxnm-forge","daemon","--conformance","--workspace","{workspace}","--state-dir","{stateRoot}"]'
```

该套件只使用临时 workspace/state/outside/control、faux Provider 和随机 canary，不访问公网
或付费模型。六个 race 必须全部成功并命中 pinned root/parent/leaf；统一拒绝不能替代句柄
固定证据。两项缺门案例使用无 writer FIFO，验证实现会在读取配置和创建 Session 前失败。
Rust fresh daemon 已实际通过该 6 个竞态案例与 2 个生产门禁；结果只构成 Linux
`file.read/file.write` 专项证据，不改变
`security.path_boundary=implemented` 的宽能力状态。

```sh
cargo fmt --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
```

Provider identity-only 广告的 14 个正例与四个 strict 负例：

```sh
cargo build
QXNM_FORGE_CONFORMANCE=1 python3 ../CONFORMANCE/provider_identity_runner.py \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["./target/debug/qxnm-forge","daemon","--workspace",".."]'
```

该 runner 只生成无值 presence 并调用 `initialize`、`models/list`；它不发送模型请求，
也不把 35/45/1,076 的广告结果提升为 Provider 执行或 live 支持。

六条 canonical Route Spine 的原生请求与九个失败关闭案例：

```sh
cargo build
python3 ../CONFORMANCE/provider_route_runner.py \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["./target/debug/qxnm-forge","daemon","--workspace","{workspace}","--state-dir","{stateRoot}"]'
```

该 runner 只连接 ephemeral `127.0.0.1` mock，并注入合成 credential；不会访问付费模型。

Linux executor 与三个 PTY 动态案例（runner 自动注入窄 fixture-only 策略）：

```sh
cargo build
python3 ../CONFORMANCE/executor_runner.py \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["./target/debug/qxnm-forge","daemon","--workspace","{workspace}","--state-dir","{stateRoot}"]'
```

Branch/compaction 的静态、真实 mutation、冻结错误和 continuation 门禁：

```sh
cargo build
python3 ../CONFORMANCE/branch_compaction_runner.py \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["./target/debug/qxnm-forge","daemon","--workspace","{workspace}","--state-dir","{stateRoot}"]'
```

首批 Provider 共同 21 案例：

```sh
python3 ../CONFORMANCE/provider_runner.py \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["./target/debug/qxnm-forge","daemon","--workspace",".."]'
```

第二批共同黑盒门禁使用独立 21 案例夹具：

```sh
python3 ../CONFORMANCE/provider_runner.py \
  --fixture ../CONFORMANCE/fixtures/provider/mock-cases-batch2.json \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["./target/debug/qxnm-forge","daemon","--workspace",".."]'
```

第三批 Vertex、Bedrock 与 Codex 的共同 21 案例门禁：

```sh
python3 ../CONFORMANCE/provider_runner.py \
  --fixture ../CONFORMANCE/fixtures/provider/mock-cases-batch3.json \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["./target/debug/qxnm-forge","daemon","--workspace",".."]'
```

OpenRouter Images 的 artifact-first 共同 13 案例门禁：

```sh
python3 ../CONFORMANCE/provider_image_runner.py \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["./target/debug/qxnm-forge","daemon","--workspace",".."]'
```

这些门禁仅访问 ephemeral IPv4 loopback mock，不调用真实 Provider，也不把 mock
conformance 等同于 live verification。

方法注释与作者信息审计从仓库根运行：

```sh
python3 scripts/audit_method_docs.py RUST
```
