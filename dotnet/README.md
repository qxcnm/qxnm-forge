# qxnm-forge .NET vertical slice

独立的 .NET 10 挑战实现，作者高宏顺 `<18272669457@163.com>`。运行时不调用
Rust、任何参考项目 runtime 或其他语言实现。

当前可验证纵切面包含：portable JSONL session、deterministic faux Provider、
多 turn Agent loop、JSON-RPC/NDJSON stdio daemon、纯文本 `run` CLI、默认权限、
交互审批，以及原生文件/搜索/进程/shell 工具。它已通过公共未知工具结果恢复、
`session/get`、历史上下文重建、Rust↔.NET 双向线性 Session continuation 和
15 项工具/审批动态黑盒案例。

## 签名远程推广 Provider 目录

.NET 原生提供 `sponsors keygen/sign/verify/configure/list/use/installed/remove`，格式与 Rust 交叉兼容，但网络、
密码学、JSON 和缓存实现完全独立。远端目录只能展示受限 API-family 兼容信息、注册链接
和强制返佣披露，不能自动执行 Agent route。用户用 `sponsors use` 明确接受披露并固定
route，再用 `auth set` 从重定向 stdin 保存工作区外 credential；三种文本 family 复用已有
adapter。`run` 已移除 faux-only 限制，与 daemon 使用同一 registry。完整发布、安装、更新、
缓存、轮换和双向验证命令见
[中文运维文档](../docs/sponsored-provider-commercialization.md)。

普通 `run`、`daemon` 与推广目录省略 `--state-dir` 时，状态默认位于平台用户级状态目录，
不再写入工作区 `.qxnm-forge`；显式参数优先，其次是 `QXNM_FORGE_SESSION_ROOT`。

Session 已实现 append-order 与 parent tree 分离、selected chain 投影、
`session/branch/select` expected-head CAS 和两条 durable record 的
`session/compact`。最新 compaction 按 summary、retained source、后续消息的固定顺序
进入 Provider context，sibling 与旧前缀不会泄漏；active run/并发 mutation 会立即返回
`session_busy`，其余六类失败使用 ADR 0011 冻结错误映射且不追加字节。公共
branch/compaction runner 的 mutation、重复选择、summary-only crash、七类错误和 faux
continuation 套件已全部通过。

stdio daemon 还原生实现了九类文本 API family 与 `openrouter-images`：
OpenAI-compatible Chat Completions、OpenAI Responses、Anthropic Messages、Mistral
Conversations、Azure OpenAI Responses、Google Generative AI、Google Vertex、AWS
Bedrock ConverseStream、OpenAI Codex Responses，以及 OpenRouter Images。文本 family
使用 `System.Net.Http` 与各自 SSE/AWS EventStream parser；图像 family 使用非流式
严格 JSON。三批独立 localhost 文本探针各 21 项、图像 artifact-first 探针 13 项均已
通过，覆盖任意 UTF-8/JSON 分片、partial/完整工具参数、429/503 重试、Retry-After、
断流、idle timeout 和取消。凭据只在最终 HTTP header 边界注入；transport 禁止重定向、
环境代理及错误 body 回显。未配置对应凭据或 endpoint 时不会广告该 live Provider。

## Manifest 驱动的 Provider 身份广告

.NET daemon 已实现 ADR 0017 的离线 identity-only 选择层。仅在 daemon 显式启用
`--conformance`（或 `QXNM_FORGE_CONFORMANCE=1`）且存在
`AGENT_PROVIDER_IDENTITY_CONFORMANCE_CONFIG` 时，启动过程才读取 runner 创建的无值
presence 文件；该分支先于 HTTP transport policy、endpoint 与 credential 环境读取，
不会构造任何 live adapter，也不会读取 runner 的 credential canary。生产模式只要发现
该 presence 名称就拒绝启动。

实现从程序集内固定的 `SPEC/providers.v1.json` 与 `SPEC/models.v1.json` 构建一次快照，
严格拒绝重复键、NaN/Infinity、未知字段、悬空引用及 digest 漂移，并固定核对 35 个
Provider、45 条 route、1,076 个模型。`models/list` 按
`(providerId, modelId, apiFamily)` 排序，OpenRouter 两个跨 family 重名模型保留为两条；
`initialize.capabilities.providers[].models` 从同一快照生成排序去重并集。快照模型不会
注册为可执行 Provider，因而对应 `run/start` 在任何 Session 副作用前返回
`-32005/provider_unavailable`。公共 provider identity runner 的 14 个正例与 4 个 strict
负例均已通过；这只是离线选择算法证据，identity-only gate 本身不产生 claim，也不得
表述为“35 个 Provider 已接入”。下节 ADR 0018 route 使用独立执行证据。

## Manifest 驱动的 Provider Route Spine

普通生产启动实现 ADR 0018 的六条最小 executable route：

- `groq/openai-completions`：`GROQ_API_KEY`；
- `minimax/anthropic-messages`：`MINIMAX_API_KEY`；
- `mistral/mistral-conversations`：`MISTRAL_API_KEY`；
- `openai/openai-responses`：`OPENAI_API_KEY`；
- `google/google-generative-ai`：`GEMINI_API_KEY`；
- `openrouter/openrouter-images`：`OPENROUTER_API_KEY`。

Registry 以 `(providerId, apiFamily)` 为 route key。上述 route、adapter、API-key 环境
名称、固定 HTTPS base、模型 allowlist 与 descriptor 都由程序集内同一份冻结
manifest/catalog 快照产生；六条 allowlist 合计 133 个模型，缺 key 的整条 route 不进入
`initialize` 或 `models/list`。
wrong family、unknown model、catalog 外 model 与当前空 credential 均在 Session 查找或
durable append 前返回 `-32005/provider_unavailable`。省略 `apiFamily` 只在
`providerId + modelId` 恰好命中一个当前可用 route 时允许，多命中返回
`-32602/invalid_params`。

Adapter 不保存 credential 值，只保存 manifest 固定环境名称；每个最终 HTTP request
构造 header 时重新读取，因此非空轮换在下一请求生效。请求 target 只能在 catalog base
上追加 family 原生路径，追加前后 scheme、host 与 effective port 必须相同。文本模型能力
只投影 `text/streaming/tools` 且 `reasoning:false`；图像 route 固定
`streaming:false`、`tools:false`、`reasoning:false`，catalog 的额外证据不会放大 runtime
能力。

`AGENT_PROVIDER_ROUTE_CONFORMANCE_CONFIG` 只有在 daemon 通用 conformance 与
`QXNM_FORGE_PROVIDER_CONFORMANCE=1` 同时启用时才读取。配置严格限制为单 route、单 catalog
model 与 literal `http://127.0.0.1:<port>/...` base；Linux 使用 `O_NOFOLLOW`，Windows
使用 `OPEN_REPARSE_POINT` 检查，且读取大小有界。生产只要看见该 presence 就在读取文件、
transport 或 credential 前脱敏拒绝。公共 route runner 的六个正例、九个负例与四项普通
生产门禁均已通过，摘要为 `6 positives / 9 negatives / 4 production gates`。
这是六条离线 canonical route 的 conformance 证据，不代表其它 39 条 route、真实付费
调用，也不得外推到同品牌 sibling family。

公开 Provider claim 必须按 `(providerId, apiFamily, feature)` 分别登记，不能把 sibling
route 合并为品牌级能力。
本实现中五条文本 route 的 `authentication/text/streaming/tools` 与图像 route 的
`authentication/text/image_input/image_output` 为 `conformant`；其余 39 条 route 和全部
`live_smoke` 仍为 `unsupported`。
这些 claim 只覆盖 Linux offline loopback；缺少原子 no-follow 的平台 fail closed，不在
本批 claim 范围。

生产广告 smoke 还在不设置 conformance/config、使用 dead proxy 的环境中同时注入六个
合成非空 credential；`initialize` 返回 7 个 Provider（含 `faux`）与 134 个 model ID
（含 `faux-v1`），过程中未发网络请求。这只验证普通启动 snapshot 的六 route/133 模型
交集，不是 live Provider smoke。
其它三项 production gate 还证明 allowlist 外 Azure/Anthropic 环境只剩 `faux`、
identity/route 双 presence 在读取任一路径前拒绝、空 route presence 启动拒绝；全部使用
network tripwire 并保持零 HTTP/Session。

原有九个文本 family 与一个图像 family 的 synthetic localhost mock 路径继续只在
`QXNM_FORGE_PROVIDER_CONFORMANCE=1` 下用于 parser/transport 回归；Azure、Vertex、Bedrock、
Codex 等未列入六条 spine 的 route 不会因此在普通生产启用。

## OpenRouter Images 配置、API、Session 与安全

生产模式只从 `OPENROUTER_API_KEY` 读取凭据，固定 base 为
`https://openrouter.ai/api/v1`；建议 `run/start.provider` 显式携带
`"apiFamily":"openrouter-images"`（省略时只在唯一命中规则成立时解析），模型必须属于
`SPEC/models.v1.json` 冻结的 35 个
图像模型。请求复用普通 `run/start`，不新增品牌 RPC；输入 content 可混合 `text` 与
`image_ref`。`image_ref` 仅含 `artifactId`、`mediaType`、`byteLength`、`sha256`，不接受
host path、remote URL 或内联 base64。

输入图像必须来自同一 Session、当前 selected parent chain 上更早的
`artifact.created`。发送前实现使用 no-follow 句柄重新检查 regular file、长度、
SHA-256、MIME 与 PNG/JPEG/WebP/GIF 魔数；通过后才在单次 HTTP request 内临时生成
canonical data URL。响应先在 48 MiB 上限内完成严格 UTF-8/JSON、重复键、尾随 JSON、
canonical base64、MIME/魔数、数量/累计字节和 usage 守恒验证，整批成功后才发布第一张。
每张图像先写同目录临时文件并 flush，以无覆盖 rename 发布、刷新目录、追加
`artifact.created`；所有图像记录早于 assistant `message.appended`，后者只含
`image_ref`，随后才发送 `message.completed`。图像不会产生 `message.delta`。

本地 conformance 仅在 `QXNM_FORGE_PROVIDER_CONFORMANCE=1` 时读取
`QXNM_FORGE_OPENROUTER_IMAGES_ENDPOINT`，且 endpoint 必须为带端口的 literal
`http://127.0.0.1`；测试使用 synthetic key，不访问真实 OpenRouter。若 initialize 未
广告 `openrouter`，先检查 key 是否存在、图像 family 是否显式选择以及模型是否在冻结
清单；若 run 失败，检查引用是否属于 selected branch、artifact 文件是否被替换、长度/hash
是否漂移或 MIME 与魔数是否一致。安全错误不会回显 credential、base64、data/remote URL、
raw response body 或主机路径。

## Linux 文件路径竞态边界

Linux `file.read` 与 `file.write` 从 `ToolRegistry` 启动期持续持有的 workspace root
`SafeFileHandle` 开始，逐组件通过 `openat(O_DIRECTORY|O_NOFOLLOW)` 打开最终 parent。读取只从
已经 `statx` 为 bounded regular file 的 leaf fd 取字节；写入在同一 parent fd 下
create-exclusive 随机临时文件，完整写入并 `fsync` 后通过 `renameat` 替换 leaf 名称，失败
清理使用 `unlinkat`，成功后再刷新 parent directory。workspace root、nested parent 或 leaf
在检查后的 rename/symlink rebind 因此不会把 I/O 转向工作区外对象。

ADR 0021 的四个确定性 checkpoint 只用于共同 conformance runner。仅当 daemon argv 含精确
`--conformance` 且 `QXNM_FORGE_CONFORMANCE=1` 时，才读取
`AGENT_PATH_BOUNDARY_CONFORMANCE_CONFIG` 指向的 strict JSON。配置 presence 缺任一道门或
配置非法时，`initialize` 固定返回不可重试
`-32603/conformance_configuration_invalid`，并且缺门路径绝不打开配置源。生产环境必须保持
该变量未设置。此证据仅覆盖 Linux `file.read`/`file.write`；Windows/macOS fallback、
`file.edit`、`search.text`、process cwd 与 mount change 尚未由该门禁覆盖，因此宽能力
`security.path_boundary` 仍只能声明为 `implemented`。

从 `dotnet/` 目录构建 Release 产物并运行共同 `6 race + 2 production gates`：

```bash
dotnet build QxnmForge.slnx -c Release -warnaserror
python3 ../CONFORMANCE/path_boundary_runner.py \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["dotnet","src/QxnmForge.Cli/bin/Release/net10.0/qxnm-forge-dotnet.dll","daemon","--stdio","--conformance","--workspace","{workspace}"]'
```

runner 只使用 fresh workspace/state/outside/control、faux Provider、真实 interactive
approval 与随机 canary；六个 race 必须成功命中 pinned 对象，两项缺门 FIFO 必须在任何
配置读取和 Session 副作用前失败关闭。.NET fresh daemon 已实际通过完整 `6+2`；该专项
结果不等于 Windows 路径验证或 hard sandbox。

## Linux Bubblewrap Hard Sandbox

### 安装与配置

`linux-bwrap-v1` 只在 Linux 可用，Bubblewrap 是 .NET 程序之外的可选系统依赖，不随
NuGet 包或构建产物分发。Debian/Ubuntu 使用 `sudo apt-get install bubblewrap`，Fedora 使用
`sudo dnf install bubblewrap`，Arch Linux 使用 `sudo pacman -S bubblewrap`。生产后端固定
为 `/usr/bin/bwrap`，不搜索 `PATH`、不跟随符号链接，也不允许配置替代路径。可先执行：

```bash
test -x /usr/bin/bwrap
test ! -L /usr/bin/bwrap
stat -c '%U %F %A' /usr/bin/bwrap
/usr/bin/bwrap --version
```

daemon 还会要求它是 root 所有的普通文件、当前用户不可写，并在 `initialize` 成功前完成
有界版本探针和完整 namespace 自测。构建并显式启用 profile：

```bash
dotnet build QxnmForge.slnx -c Release -warnaserror
AGENT_HARD_SANDBOX_PROFILE=linux-bwrap-v1 \
  dotnet src/QxnmForge.Cli/bin/Release/net10.0/qxnm-forge-dotnet.dll \
  daemon --stdio --workspace /path/to/workspace
```

未配置 profile 时不会广告 `initialize.capabilities.hardSandbox`，不带 sandbox 参数的
`process.exec`/`shell.exec` 继续走现有 host executor。已配置 profile 但 backend 身份、
版本或任一自测失败时，初始化返回 `-32603`、
`details.kind="sandbox_unavailable"` 并失败关闭，不能退化为普通执行。
`AGENT_HARD_SANDBOX_CONFORMANCE_BACKEND` 只供共同 runner 制造失败案例；它只有在 daemon
argv 含精确 `--conformance` 且 `QXNM_FORGE_CONFORMANCE=1` 时可读，生产环境不得设置。

### 工具请求与审批

审批默认五分钟到期；测试短 timeout 只有 argv 精确包含 `--conformance` 且
`QXNM_FORGE_CONFORMANCE=1` 时才读取 `QXNM_FORGE_APPROVAL_TIMEOUT_MS`。公共 15 项动态门禁包含
100 ms timeout/late conflict、stdin EOF、stdout success-frame 交付失败与 `SIGKILL` 恢复。
`allow_once` 的 durable 记录不是执行授权；只有成功响应真实 flush 后才唤醒 waiter。
交付失败与崩溃恢复都必须产生一个未执行 tool result、规范 tool message、零
`tool.started`、零目标文件修改和唯一 completed/interrupted 终态。

本能力复用 `initialize -> run/start -> approval/respond -> process.exec|shell.exec`，不新增
品牌或 sandbox RPC。Provider 产生的工具 arguments 必须显式选择 profile：

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

`cwd` 必须是 canonical workspace 内的相对路径，在隔离视图中映射到 `/workspace`。写入
工作区必须改用 `workspaceAccess:"read_write"`，且两种模式都不会绕过危险操作审批；
`approval.requested.arguments` 和 operation hash 会绑定 profile、RO/RW 与 network 模式，
批准后不能替换。成功或已启动后的 timeout、输出超限、取消结果必须包含
`execution.containment:"os_isolation"`。

### 安全边界与故障排查

实现原生启动 Bubblewrap，建立 user/mount/PID/network/IPC/UTS namespace；系统运行时只读，
`/proc`、最小 `/dev` 与 tmpfs `/tmp` 在 sandbox 内新建，仅将 workspace 精确 `ro-bind` 或
`bind` 到 `/workspace`，不绑定宿主 `/`。网络始终隔离，子进程环境为空白最小集合，不继承
Provider、云或代理凭据。timeout、output-limit、取消和 daemon 父进程退出清理 sandbox 内
`fork()+setsid()` 后代。

该能力是 Linux OS namespace 隔离而非 VM，不覆盖 Linux 内核/Bubblewrap 漏洞、资源侧信道，
也不会隐藏用户明确授权的 workspace 内容。没有 `sandbox` 对象的调用仍是 host executor；
路径边界、进程组和 Windows Job Object 不是本能力的替代证据。

出现 `sandbox_unavailable` 时检查：运行平台是否为 Linux、profile 是否精确、固定 backend
是否存在且 root-owned/普通/不可写、`--version` 是否成功，以及宿主/容器是否允许
unprivileged user、mount 和 network namespace。read-only 模式写失败、network 连接失败、
宿主绝对路径不可见均是预期结果；工作文件应从 `/workspace` 访问。生产进程如果残留
conformance backend override 会主动拒绝，删除该变量后再启动。

### 验证

从 `dotnet/` 目录对 fresh Release daemon 运行离线共同门禁：

```bash
dotnet build QxnmForge.slnx -c Release -warnaserror
python3 ../CONFORMANCE/hard_sandbox_runner.py \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["dotnet","src/QxnmForge.Cli/bin/Release/net10.0/qxnm-forge-dotnet.dll","daemon","--stdio","--conformance","--workspace","{workspace}"]'
```

通过摘要固定为 `6 positives / 4 negatives / 2 production gates`。runner 只使用临时
workspace、faux Provider、随机 canary 和本机 loopback listener，不访问公网或付费服务；
它覆盖 RO/RW、cwd/argv/UTF-8、`/tmp`、系统只读、外部文件/网络/凭据隔离、
timeout/output/cancel/daemon-exit 后代清理，以及 missing、用户可写和伪 backend 的
`sandbox_unavailable`、零 Session 副作用和无 host fallback。

工具层原生实现 `file.read`、`file.write`、`file.edit`、`search.text`、
`process.exec` 和 `shell.exec`。无头危险操作默认拒绝；交互模式使用绑定 operation
hash 的 `allow_once`/`deny`，取消会为同批全部完整工具调用产生 canonical 结果。
Unix 进程组和 Windows Job Object 已实现，但 suspended-create、handle/no-follow
路径操作和三平台攻击测试仍未完成，所以这些安全/执行器能力只标记为 `implemented`。

Session writer 现在同时持有持久 `lock` advisory defence-in-depth 与规范
`writer.lock.d/owner.json` witness lease：先绑定 literal `127.0.0.1:0`，严格验证
有界 UTF-8 owner，再仅凭明确 connection refused 接管 stale 目录；clean release 按
token 校验、原子移动、移动后二次验证、advisory unlock、listener close 的顺序执行。
`.NET→.NET` 以及 `.NET↔Python` 有序 holder/crash/takeover 黑盒矩阵已通过；完整五语言
Linux 五语言有序 5×5 holder/contender、崩溃接管与恢复矩阵已经完成；Windows/macOS
尚未在实机运行同一矩阵，因此 `session.cross_language_writer_lock` 仍保守保持
`implemented`，不由 Linux 证据外推。

九类文本 HTTP Provider 均发送非空工具定义，并在单元层验证 assistant 工具调用与
tool result 的下一轮原生映射；公共 Provider runner 的工具案例仍按无头策略拒绝实际
执行，因此 live tool smoke 尚未完成。OpenRouter Images 真实付费 smoke 也未显式启用；
当前证据仅来自 clean-room loopback mock。迁移、PTY 与完整三平台安全加固仍在后续范围；
公开支持状态以能力矩阵为准。

## Session journal 尾部恢复

portable v0.1 只把 LF 结尾的行视为 committed。正常打开遇到任意 no-LF 最后一行时，
即使它是合法 JSON/Schema record，也会先把修复前完整字节保存为
`journal.recovery-<originalSha256>.bak`，再截断并追加
`org.agent-session.recovery` extension；clean reopen 不会重复恢复。带 LF 的非法 UTF-8、
JSON、Schema 或递归 duplicate key 一律返回 `-32008/journal_corrupt`，journal 和 backup
集合保持不变。当前合同不声称 backup、truncate 与 diagnostic 三步之间的 crash-atomic。

```bash
python3 ../CONFORMANCE/session_tail_runner.py \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["dotnet","src/QxnmForge.Cli/bin/Release/net10.0/qxnm-forge-dotnet.dll","daemon","--stdio","--conformance","--workspace","{workspace}"]'
```

```bash
dotnet run --project src/QxnmForge.Cli -- run "你好"
dotnet run --project src/QxnmForge.Cli -- daemon --stdio --conformance
```

公共工具/审批探针（先构建 Release）：

```bash
python3 ../CONFORMANCE/tool_runner.py \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["dotnet","src/QxnmForge.Cli/bin/Release/net10.0/qxnm-forge-dotnet.dll","daemon","--stdio","--conformance","--workspace","{workspace}"]'
```

第二批 Provider 离线探针（不会读取真实凭据或访问外网）：

```bash
python3 ../CONFORMANCE/provider_runner.py \
  --fixture ../CONFORMANCE/fixtures/provider/mock-cases-batch2.json \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["dotnet","src/QxnmForge.Cli/bin/Release/net10.0/qxnm-forge-dotnet.dll","daemon","--stdio","--conformance","--workspace","/path/to/repository"]'
```

OpenRouter Images artifact-first 离线探针（十三项、不会访问真实 Provider）：

```bash
python3 ../CONFORMANCE/provider_image_runner.py \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["dotnet","src/QxnmForge.Cli/bin/Release/net10.0/qxnm-forge-dotnet.dll","daemon","--stdio","--conformance","--workspace","/path/to/repository"]'
```

Provider 身份广告离线探针（14 个正例与 4 个 strict 负例）：

```bash
python3 ../CONFORMANCE/provider_identity_runner.py \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["dotnet","src/QxnmForge.Cli/bin/Release/net10.0/qxnm-forge-dotnet.dll","daemon","--stdio","--conformance","--workspace","/path/to/repository"]'
```

Provider Route Spine 离线探针（6 个正例、9 个负例与 4 项普通生产门禁）：

```bash
python3 ../CONFORMANCE/provider_route_runner.py \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["dotnet","src/QxnmForge.Cli/bin/Release/net10.0/qxnm-forge-dotnet.dll","daemon","--stdio","--conformance","--workspace","{workspace}"]'
```

Session branch/compaction 公共探针：

```bash
python3 ../CONFORMANCE/branch_compaction_runner.py \
  --schema-root ../SPEC/schemas \
  --daemon-command-json \
  '["dotnet","{repoRoot}/dotnet/src/QxnmForge.Cli/bin/Release/net10.0/qxnm-forge-dotnet.dll","daemon","--stdio","--conformance","--workspace","{workspace}"]'
```

质量门禁：

```bash
dotnet format QxnmForge.slnx --verify-no-changes
dotnet build QxnmForge.slnx -warnaserror
dotnet test QxnmForge.slnx --no-build
```
