# qxnm-forge security and approval specification v0.1

## Threat model

The model, repository files, tool output, provider responses, imported sessions,
and extension data are untrusted. They may contain prompt injection, malicious
paths, terminal control sequences, huge output, malformed Unicode/JSON, secrets,
or instructions to destroy/exfiltrate data. A valid model tool call expresses
intent but grants no authority.

The daemon protects:

- files and processes outside the selected workspace;
- workspace integrity and concurrent user edits;
- credentials, environment variables, logs, and session artifacts;
- availability (memory, disk, CPU, descriptors, process count, network time);
- the approval boundary and accurate audit trail;
- protocol stdout from injection/corruption.

Local code deliberately approved for execution may have the user's OS
privileges unless a hard sandbox is active. This is a stated residual risk.

## Default policy

The baseline classes are:

| Operation | Interactive default | Headless without explicit policy |
| --- | --- | --- |
| bounded read/list/search inside workspace | allow | allow |
| write/edit inside workspace | ask | deny |
| process/shell/terminal | ask | deny |
| network or credential access | ask | deny |
| experimental desktop observe/screenshot | ask (`high`) | deny |
| experimental desktop mouse/keyboard input | ask (`critical`) | deny |
| any path outside workspace | deny | deny |

Deployments MAY be stricter. They MUST NOT be more permissive without an
explicit user/admin policy identifying operation, scope, duration, and principal.
Provider network calls configured for the selected model are a separate daemon
capability and do not grant tools general network access.

无头模式没有可交互审批者时，危险操作必须默认拒绝，不能因为模型“要求执行”就放行。

## Approval flow

After schema and resource preflight but before side effects, policy returns
`allow`, `deny`, or `ask`. An `ask` creates a durable `approval.requested` and
event containing:

- opaque approval/tool/run IDs;
- normalized operation and an explicit `arguments` object with secrets
  redacted;
- affected paths/origins/executable;
- reason, risk class, and requested scope;
- expiry and choices (`allow_once`, `deny`). v0.1 does not define a session-scoped
  approval grant; persistent policy changes require a separate management interface.

An approval is bound to an exact normalized operation hash. Mutation of
arguments, resolved path, executable, environment, or workspace invalidates it.
The response is durable before execution starts. Approval IDs are single-use;
late/duplicate responses are conflicts. Timeout/disconnect denies. “Always
allow” requires a separate policy-management interface outside model control
and is not a v0.1 approval choice.

Approval text is presentation only. Policy decisions use structured fields and
MUST NOT parse prose.

The initializing client sets `capabilities.interactiveApprovals:true` only when
it has an active approval responder. When false or absent, `ask` is resolved as
the headless default deny without creating an approval ID or approval events.
The call still receives a deterministic error tool result with
`terminationReason:"denied"` and structured `permission_denied` error, then may
enter the next Provider turn. This is not a failed `run/start` because the run
and Provider tool call were already accepted.

An interactive resolution carries server-authored `resolutionSource`:
`client`, `policy`, `timeout`, `cancellation`, or `disconnect`. Only the client
may supply the `decision`; it cannot choose or spoof the source. Timeout,
disconnect, and cancellation resolve the pending approval as `deny`. A
cancellation resolution finalizes the call with a cancelled error tool result
and ends the run as cancelled; it never starts the executor.

Approval execution is additionally guarded by the response-delivery barrier in
[ADR 0019](adr/0019-approval-failure-conformance.md). A client decision is
durable before `{accepted:true}`, but `allow_once` authority is released only
after that success frame is written and flushed. If delivery fails, the single
durable client resolution remains the audit fact; the daemon MUST NOT append a
second deny resolution, emit `tool.started`, or execute the operation. It
fail-closes the unresolved delivery barrier with one error tool result and one
run terminal.

The common failure suite also requires bounded automatic timeout, stdin
EOF/disconnect denial, late and duplicate response conflicts, and crash recovery
after durable `approval.requested`. Timeout conformance uses 100 ms only when the
daemon argv contains the exact `--conformance` element and
`QXNM_FORGE_CONFORMANCE=1`; only then may the runner inject
`QXNM_FORGE_APPROVAL_TIMEOUT_MS=100`. Unknown, expired, timed-out, or duplicate
approval responses return `code=-32010`, `retryable=false`, without journal or
workspace mutation.

## 实验性桌面 Computer 边界

桌面工具遵循 [ADR 0032](adr/0032-experimental-desktop-computer.md)，是默认关闭的 Community
共同切面。只有 `AGENT_CLIENT_DESKTOP_COMPUTER=1` 与
`AGENT_CLIENT_EXPERIMENTAL_DESKTOP_COMPUTER=1` 两个门同时精确满足，且实现正向证明当前为
Linux 原生 X11 Session 后，才可探测并按真实 backend 能力广告工具。该证明要求
`XDG_SESSION_TYPE` 仅 ASCII trim 并按 ASCII lowercase 后精确为 `x11`，且 `WAYLAND_DISPLAY` 缺失或
为空；`DISPLAY` 还必须在启动探测、审批前预检和执行边界都符合 ADR 冻结的本地
`:N[.S]` / `unix/:N[.S]`、64 字符及 `0..65535` 数字 allowlist。session type
缺失/空/非 x11、非空 `WAYLAND_DISPLAY`、缺失/远程/SSH-forwarded/非 ASCII `DISPLAY`、
Windows、macOS、未知远程 display 和任一单门都必须零广告。环境门本身不授权读取或输入。
通过 allowlist 的值必须重建为显式 `unix/:N[.S]` 连接目标，禁止默认 display 和 Unix socket
失败后的 localhost TCP fallback；operator 必须把所选本地 X server/socket 作为受信任宿主边界。

`computer.observe` 与 `computer.screenshot` 是 `high/outside_workspace`，
`computer.interact` 是 `critical/outside_workspace`。每次调用都必须经过参数/resource
preflight 和独立 `allow_once|deny` 审批；资源固定为 `desktop:screen` 或精确动作
`desktop:move|click|scroll|key`。无头模式、审批 responder 缺失、过期、取消或连接断开均拒绝。
坐标在 schema 范围内仍须按执行时 root geometry 复验；scroll `0/0` 是无副作用成功 no-op。

桌面输入不是 hard sandbox。阻塞 X11/XTEST 调用、取消、超时、服务端异步错误和部分输入释放都
必须由独立门禁证明；当前没有超时强隔离证据，不能把停止等待 worker 描述为已经中止操作。
Rust x11rb 0.13 还会在适配层长度检查前按服务端 reply header 扩容 packet buffer，因此显式
Unix-only 连接和捕获前几何检查不能冒充恶意 X server 下的 hard allocation cap。
.NET 的进程内 Xlib 普通协议错误可由临时 handler 与 `XSync` 捕获，但致命 XIO 断连仍可能触发
进程级默认诊断与 daemon 终止；没有 helper process/可恢复 transport 前不能宣称故障隔离。

## Workspace boundary

Lexical prefix checks and `realpath` checks performed only once are insufficient.
Executors follow [executor.md](executor.md)'s handle/no-follow approach and
defend against traversal, symlinks, junctions, mount changes, Windows reparse
points, device/UNC paths, drive aliases, and time-of-check/time-of-use races.
The workspace root itself is opened/canonicalized at session start and changing
its identity invalidates pending approvals.

### Linux `file.read`/`file.write` 确定性竞态证据

[ADR 0021](adr/0021-linux-file-path-race-conformance.md) 冻结 Linux 上的窄化
handle-relative 合同：daemon 启动时持续持有 canonical workspace root directory
handle；每次访问都从该 handle 逐组件 no-follow 打开最终 parent；`file.read` 只从已验证的
bounded regular leaf handle 读取，并在 leaf checkpoint 前完成静态大小上限检查、在读取时
继续使用 `limit + 1` 探针拒绝检查后增长；`file.write` 只在同一 parent handle 下创建、同步临时文件并以
相对名称原子替换，再同步 parent。pathname 在检查后被 rename 或重绑为 symlink，不得把
已经固定的 I/O 转向工作区外对象。

共同门禁在四个冻结 checkpoint 上确定性暂停，并执行 workspace root、nested parent、leaf
三层各一组 read/write rebind，共六个 race。六例都必须成功命中 pinned 对象；统一拒绝
不能冒充句柄固定。另有两项生产缺门门禁，分别只提供 argv 中精确独立的
`--conformance` 或只提供 `QXNM_FORGE_CONFORMANCE=1`。只要
`AGENT_PATH_BOUNDARY_CONFORMANCE_CONFIG` presence 存在但双门不完整，daemon 必须在打开
配置源、创建 control/Session/journal 或执行工具之前，让 `initialize` 以不可重试
`-32603/conformance_configuration_invalid` 失败关闭。

该证据只覆盖 Linux `file.read`/`file.write`，不构成 OS/container/VM hard sandbox，也
不外推到 Windows reparse/junction、UNC、ADS、device/drive alias，macOS fallback、
`file.list`、`file.edit`、`search.text`、process cwd 或 mount change。因此宽能力
`security.path_boundary` 继续保持 `implemented`，不能仅凭这 6 个竞态案例与 2 个生产门禁
提升为 `conformant`。

## Sandbox terminology

qxnm-forge distinguishes:

- **policy restricted** — checks and approvals in the daemon; bypass may be
  possible after executing arbitrary local code;
- **process constrained** — resource/process-tree controls without filesystem or
  network isolation;
- **hard sandboxed** — an enforced container, VM, or OS security boundary with
  separately configured filesystem, network, process, and credential isolation.

Path checks, a changed working directory, environment filtering, or process
groups alone MUST NOT be described as a sandbox. An implementation may advertise
`hardSandbox: true` only for a named, tested OS isolation backend and must state
which resources are isolated. If setup fails, it fails closed or accurately
downgrades before approval; it never silently retains the claim.

The first named hard-sandbox profile is optional Linux
`linux-bwrap-v1`, specified by [ADR 0016](adr/0016-linux-bubblewrap-hard-sandbox.md)
and `schemas/hard-sandbox.schema.json`. It requires a successful startup
self-test and isolates mount/filesystem view, network, PID/process lifetime and
credential environment before user code starts. Merely locating `bwrap`,
rendering its argv, or running inside an unknown outer container is not enough
to advertise the capability. A requested profile that cannot establish every
declared dimension fails with `sandbox_unavailable`; it never falls back to an
unsandboxed process.

## Secrets and redaction

Credentials come from provider-specific environment/config stores, OAuth token
stores, or cloud-native credential chains. They MUST NOT enter source code,
prompts, session journals, approval payloads, fixtures, golden traces, telemetry,
or normal logs. Provider adapters inject credentials at the final native request
boundary.

The v0.2 local `ProviderCredentialStore` is a permission-restricted per-credential
file backend, not an OS-encrypted keychain. It MUST live outside the canonical
workspace at `credentials/provider-credentials.d/`. Each direct leaf name is the
canonical unpadded base64url encoding of the Provider ID UTF-8 bytes plus
`.credential`; each body is a raw UTF-8 key of 1..16384 bytes with no CR, LF, or
NUL, and the store holds at most 128 leaves. It reads new secrets only from
redirected stdin or an equivalent write-only local management boundary, rejects
symlink/reparse files, devices, unknown leaves, unsafe ownership or permissions,
and uses an exclusive writer lock, a controlled create-new temporary leaf under
the `credentials/` root, durable atomic movement into the final directory, and
both final-directory and root sync. Crash cleanup may remove only the fixed
temporary-name pattern after no-follow metadata validation; unknown entries fail
closed. Unix directories MUST be exactly `0700`, leaves exactly `0600`, and both
owned by the effective user. Presence/list/capability queries inspect only
canonical names and no-follow metadata; only final native request-header
construction opens the exact target leaf once. A stored source that is missing,
corrupt, or unsafe MUST fail closed and MUST NOT silently fall back to environment
credentials. Windows DPAPI/Credential Manager protection is not claimed until a
separate platform gate passes.

The old `credentials/provider-credentials.json` and its bundled v0.1 schema are
legacy migration input only. Under the same exclusive lock, and only while the
final directory is absent, an implementation may strictly read that bounded
aggregate once, write and sync the fixed
`credentials/.provider-credentials.d.migrating/` staging directory, atomically
rename it, sync the parent, then remove the legacy file by safe metadata. Once
the final directory exists, the legacy body is never parsed again. Crash recovery
may clean only canonical, metadata-safe leaves from that fixed staging directory.

自定义 Provider connection 遵循 ADR 0029。通用 WebView RPC allowlist 不得包含
`providerCredentials/set|remove`；桌面 host 只能把受限 Provider ID 与本次密码输入转发给固定
daemon，不能接受 argv、state root、workspace、文件路径或方法名。非敏感连接列表只分别报告
Responses 与 Image credential presence，不得返回 credential、hash、前后缀或可用于离线验证的摘要；
两类 secret 使用独立 identity 且禁止互相或向 Codex 配置回退。连接
mutation 后必须重建启动期 Provider/model capability 快照。
`providerConnections/discoverModels` 只能由用户显式触发，并在 daemon 最终 HTTP 边界读取
Responses credential，并只请求持久化的精确 `modelsUrl`。实现必须禁止 redirect，设置 20 秒总时限和 1 MiB 响应上限，拒绝空目录、畸形
JSON、超过 512 项的模型目录及 CAS 冲突；失败信息不得回显 URL、认证 header、响应正文或
credential。`models/list` 继续是零网络、零 credential 读取的启动快照查询。

自定义连接的图片能力必须逐方向显式声明。图片输入只解析同 Session durable `image_ref`，在发送
前复核普通文件身份、MIME、魔数、长度与 SHA-256，并只在请求内构造 data URL；artifact 路径、
display name 和 Provider 返回的远程 URL 都不能成为读取依据。图片输出必须同时存在独立 Image
credential；缺失时从 `models/list.capabilities.output` 移除 `image`，但不得尝试 Responses key、
环境变量或其它 Provider secret 作为回退。有效输出期间必须把工具能力和 Agent function-tool 集合
强制收窄为空；该收窄不改写持久化 `supportsTools`，Image credential 移除并重启后必须恢复原工具
声明。Responses 只能发送 `stream:false` 与单一 `image_generation` tool，Chat 只能发送
`stream:false` 与 `modalities:["text","image"]`，均不得夹带 function-tool 定义。启用输出时使用禁重定向的有界非流式响应，strict JSON
整批解析后才产生图片完成信号；远程 URL、未知 MIME、坏/非 canonical Base64、魔数错配、超过
八张或总计 4 MiB 的结果全部失败关闭。原始响应、data URL 与图片正文不得进入日志或错误。

Session 列表、归档和永久删除遵循 ADR 0030。Web UI 只能接收脱敏摘要，不能直接访问
journal、归档文件、SQLite 或宿主 Session 目录。Session 固定隔离在 `stateRoot/sessions`，
生命周期元数据位于其外的专用目录；Provider、credential、数据库和其他状态不得被扫描或删除。
永久删除必须在 writer/reservation 锁内先证明整棵树只含有界普通文件/目录，再原子移动到固定
tombstone；symlink、junction、reparse point、device、活动 writer 或损坏 journal 均失败关闭。
移动后的失败只能由同一 Session ID 续删，错误和日志不得泄露宿主路径或 transcript。

Desktop capture PNG 是可能直接包含 credential 和其他屏幕秘密的
`desktop_sensitive` artifact。它只能在明确的逐次审批后以 `image_ref` 持久化，使用
封闭 `org.agentprotocol.computer` extension 绑定 active `runId` 与
`retention:session_lifecycle`；不得持久化 backend/display ID、原始 `DISPLAY`、窗口标题、
宿主路径或内联像素。Session 归档保留截图，永久删除必须连同截图删除。

通用 `artifacts/read` 不是任意文件下载接口。它只接受 opaque Session/artifact ID 与连续 offset，
只解析同 Session journal 中先前发布的 PNG/JPEG/WebP/GIF，逐次最多 512 KiB、单图最多 32 MiB、
每连接最多两个完整验证的读取游标。
服务在读取时重新拒绝 symlink、reparse point、device、目录、跨 Session 引用与文件身份变化；
每块回显不可变元数据，客户端在最终展示前复核总长度、MIME/魔数和 SHA-256。响应和错误不含路径，
该方法不跟随 URL、不读取普通 artifact，并且只有完整实现这些边界时才可广告。UI 的 Session 图片
预算还限制为 32 张/64 MiB、并发 2，超额只显示占位，不能通过无限回读耗尽 WebView 内存。

桌面图片粘贴的原生兜底只授予 `clipboard-manager:allow-read-image`；不得授予读文本、写入或清空
剪贴板。RGBA 在 PNG 编码和进入 React 状态前必须检查尺寸、乘法溢出与字节上限。粘贴监听不得
截获其它输入控件，busy 状态不得修改附件；测试使用合成图片，禁止读取真实剪贴板正文。

Provider conformance uses a freshly generated in-memory canary credential. The
runner removes inherited real credential variables before process creation, and
the mock records only whether required authentication headers and schemes were
present. Canary values, raw headers, full request bodies, and child environments
must not enter stdout, stderr, observations, fixtures, or failure diagnostics.

Redaction covers authorization/cookie headers, known secret environment names,
credential query parameters, OAuth tokens, cloud signed URLs, and configured
literal secrets. It runs before persistence and logging, including error paths.
Raw provider error bodies are bounded and treated as sensitive; only allowlisted
fields enter structured errors. Stack traces remain local debug output and are
redacted.

Tests use synthetically unique canary secrets and assert they do not appear in
stdout, stderr (at normal logging), journals, artifacts, or fixtures. Redaction
is defense-in-depth, not permission to send secrets to the model.

## Network and provider safety

Endpoints are validated schemes and origins. TLS verification is on by default.
Redirects never carry credentials to an undeclared origin. Proxy use is explicit
and visible. Local/private/link-local/metadata addresses are denied for
user-configurable tool URLs unless policy grants them; cloud credential adapters
that require metadata use a narrowly scoped native path.

Retry limits prevent amplification. Response headers/bodies and decompressed
bytes have caps. SSE/WebSocket parsers tolerate fragmentation but reject
oversized records. Provider output cannot write protocol stdout directly.

Explicit Provider conformance mode may connect only to the exact ephemeral
`127.0.0.1` origin created by the same runner. Redirects, wildcard binds,
non-loopback addresses, hostname resolution, proxies to other origins, and
metadata endpoints remain forbidden. This loopback exception is test plumbing,
not a hard sandbox and not permission for model-requested network tools.

## Resource and terminal safety

Sessions, artifact bytes, messages, tool calls, recursion/turns, provider
attempts, concurrent runs/tools/processes, frame sizes, and execution time all
have finite limits. Limits are enforced while streaming, not after buffering.
ANSI/control sequences in tool/model output are escaped or sanitized for a
plain-text terminal; raw bytes, when retained, are artifacts.

自定义 Provider 图片响应先以 6 MiB 有界 HTTP JSON 读取，再按八张/4 MiB 解码图片总量收窄；Session
artifact 回读独立遵守 1 MiB `maxFrameBytes`，因此使用 512 KiB 原始字节分块，不能把 32 MiB 图片
编码进单个 JSON-RPC frame。任何层的上限都不能被下一层更大的配额替代。

Desktop capture 还必须在请求 root image 和分配缓冲之前限制单边 16,384 pixels、总计
16,777,216 pixels 与预计 64 MiB raw bytes，并在持久化前限制 PNG 为 33,554,432 bytes。
checked arithmetic、通用 Session artifact 配额和执行期真实 geometry 复验都不能由编码后检查
替代。

Cancellation and timeout terminate full process trees. Daemon exit cleans up
owned jobs/groups. Persistent background work requires the future terminal
capability and cannot be smuggled through `process.exec`.

## Security conformance

Required fixtures include lexical traversal, symlink/junction escape, symlink
swap races where testable, concurrent write conflict, device/UNC/ADS paths on
Windows, huge output, forked descendants, timeout/cancellation, malicious ANSI,
malformed UTF-8, secret canaries, redirects, decompression bombs, corrupt
journals, and untrusted PI imports. Platform-specific hardening may be
`unsupported`, but the safe default must still deny the operation.

Desktop computer 当前只有不会执行动作或读取像素的静态 Schema/fixture 证据。双门零广告、
Wayland/XWayland/远程 DISPLAY 拒绝、X11/XTEST、限制、取消/释放、journal、Provider 视觉续接
和 UI artifact 渲染的双实现共同动态门禁、Rust transport reply 分配硬上限及 .NET XIO 断连
隔离未完成前，只能登记为 `implemented`，不得称为 `conformant`。
