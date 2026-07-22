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

The v0.1 local `ProviderCredentialStore` is a permission-restricted file backend,
not an OS-encrypted keychain. It MUST live outside the canonical workspace, read
new secrets only from redirected stdin or an equivalent write-only local management boundary,
reject symlink/reparse files, use an exclusive writer
lock and atomic replacement, and require Unix mode `0600`. A stored source that
is missing, corrupt, or unsafe MUST fail closed and MUST NOT silently fall back
to environment credentials. Windows DPAPI/Credential Manager protection is not
claimed until a separate platform gate passes.

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

Session 列表、归档和永久删除遵循 ADR 0030。Web UI 只能接收脱敏摘要，不能直接访问
journal、归档文件、SQLite 或宿主 Session 目录。Session 固定隔离在 `stateRoot/sessions`，
生命周期元数据位于其外的专用目录；Provider、credential、数据库和其他状态不得被扫描或删除。
永久删除必须在 writer/reservation 锁内先证明整棵树只含有界普通文件/目录，再原子移动到固定
tombstone；symlink、junction、reparse point、device、活动 writer 或损坏 journal 均失败关闭。
移动后的失败只能由同一 Session ID 续删，错误和日志不得泄露宿主路径或 transcript。

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
