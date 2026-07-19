# qxnm-forge tool and executor contract v0.1

## Tool declaration and validation

Every tool has a stable lowercase dotted name, human description, restricted
JSON Schema input, permission class, idempotency declaration, maximum output,
and cancellation behavior. Tools return portable text/image/artifact content
plus a structured error flag; they MUST NOT return a host exception or language
object on the wire.

The portable tool-schema subset permits:

- object, array, string, integer, number, Boolean, and null;
- `properties`, `required`, `additionalProperties: false`;
- bounded `items`, `minItems`, `maxItems`, `minLength`, `maxLength`;
- `enum`, `const`, numeric bounds, and anchored RE2-compatible `pattern`;
- `oneOf` only as a tagged union with a required string discriminator.

It forbids remote `$ref`, recursive schemas, `patternProperties`, code-bearing
formats, content decoding, custom validation keywords, and unbounded containers.
Default values are documentation only and MUST NOT silently add authority.
Validation occurs before approval because an approver must see normalized,
complete arguments.

## File tools

v0.1 standard names are:

- `file.read` — bounded bytes/lines from a regular file;
- `file.list` — bounded directory listing without implicit recursion;
- `search.text` — bounded literal/regular-expression search;
- `file.write` — create or replace one file;
- `file.edit` — compare-and-swap patch using expected content/hash.

The minimal portable parameters used by tool conformance are
`file.read {path}` and `file.write {path,content}`. Paths are workspace-relative
UTF-8 strings. A successful `file.read` of a bounded UTF-8 regular file returns
its exact text as one text content block. A successful `file.write` returns one
text block `wrote <N> bytes`, where `N` is the UTF-8 byte length of `content`.
Implementations may expose additional bounded optional fields only after the
shared schemas and conformance fixture define them.

All user paths are interpreted relative to an explicit workspace root unless a
schema explicitly calls a field an absolute path. Normalization alone is not
authorization. Before access, the tool walks each existing path component with
no-follow semantics where the OS permits, resolves links/reparse points, and
verifies the resulting object remains beneath the canonical workspace root.
For creation, it verifies the nearest existing ancestor, creates through a safe
directory handle, then re-verifies the final object. `..`, absolute-path
injection, alternate data streams, device paths, Windows drive-relative paths,
UNC paths, case-fold aliases, short-name aliases, mount/junction changes, and
symlink races MUST be considered for the target platform.

Workspace escape is denied by default even when the lexical string begins with
the workspace path. A tool that intentionally accesses outside needs a
separate explicit capability and approval; it cannot reuse ordinary file
permission.

Writes use a same-directory temporary file and atomic replace when replacement
semantics are requested. Expected hash/content prevents lost updates.
Concurrent overlapping writes are serialized or return conflict. Permissions
and line endings are preserved only where declared; surprising metadata loss is
reported. Never follow a destination symlink for an approved regular-file
write.

### Linux `file.read`/`file.write` handle-relative 竞态 profile

[ADR 0021](adr/0021-linux-file-path-race-conformance.md) 定义 Linux 专项 profile。
实现 MUST 在 daemon/工具注册表生命周期内持续持有 canonical workspace root directory
handle，并从该 handle 逐组件 no-follow 打开最终 parent。`file.read` MUST 在同一个已打开
leaf handle 上完成 `fstat`、regular 类型和公开硬字节上限检查；只有这些检查成功后才能
发布 `after_leaf_open_before_read`。checkpoint 后不得重新按 pathname 打开，实际读取最多
观察 `limit + 1` 字节，检查后增长也必须返回 `output_limit`，不能无界缓冲。
`file.write` MUST 在固定 parent handle 下 create-exclusive 临时 regular file，完整写入并
`fsync`，再在同一个 parent handle 下以 leaf 名原子 replace，失败时也只相对该 handle
清理临时文件，成功后刷新 parent directory。

确定性 runner 使用四个 checkpoint：`before_parent_walk`、`after_parent_open`、
`after_leaf_open_before_read`、`after_temp_fsync_before_rename`。机器夹具固定六个必须成功
的 root/parent/leaf read/write rebind 案例。read 输出必须是 pinned original 内容；write
内容只能出现在 pinned original workspace/parent，outside manifest 必须逐项不变。leaf
write 还要求被 rename-away 的旧 inode 保持旧内容，而 pinned parent 下原 leaf 名成为新的
regular file。write 案例继续走真实 interactive approval；唯一 durable
`approval.resolved` 与成功响应交付均先于 `tool.started` 和 ready barrier。

测试 hook 不是生产功能。它只有在 daemon argv 含精确独立 `--conformance` 且环境
`QXNM_FORGE_CONFORMANCE` 精确为 `1` 时才能读取
`AGENT_PATH_BOUNDARY_CONFORMANCE_CONFIG`。任一门缺失但 presence 存在、双门内配置非法、
checkpoint 未命中、重复命中或 barrier timeout 都必须 fail closed。两项无 writer FIFO
生产门禁要求缺门时在任何配置 `open`、Session/journal 创建或工具执行前返回不可重试
`-32603`，`details.kind="conformance_configuration_invalid"`。

本 profile 仅提供 Linux `file.read`/`file.write` 的 6 个竞态案例与 2 个生产门禁
证据；它不覆盖 Windows 路径语义、非 Linux fallback、`file.list`、`file.edit`、
`search.text`、process cwd 或 mount change，也不得被描述为 hard sandbox。

## `process.exec`

`process.exec` accepts an executable and `argv` array. It never concatenates a
shell command and never performs glob, variable, command, or tilde expansion.
Its portable request fields are:

- `executable`, `args`;
- workspace-relative `cwd`;
- optional environment changes as explicit key/value entries;
- `stdin` as none, bounded text, or artifact reference;
- connect/start and total timeout;
- stdout/stderr byte limits.

The effective environment begins from a policy-selected allowlist, not
unconditionally from the daemon environment. Secret-bearing variables are
excluded unless the invoked profile explicitly needs them and policy approves.
Executable resolution is deterministic: an absolute approved executable or a
PATH lookup against a captured allowlisted PATH. The result records the resolved
executable without revealing secret environment values.

stdout and stderr are captured separately as raw bytes, decoded incrementally
with a declared/default UTF-8 policy, and emitted with stream identity. Invalid
UTF-8 is replaced for display while the exact bounded bytes may be an artifact.
The normalized result MUST include the `execution` object defined by
`schemas/executor.schema.json` when the child was started. It records exit code
or signal, duration, containment level, and independent stdout/stderr capture
objects. `capturedBytes + omittedBytes == totalBytes`; truncation is explicit
and never silently inserted into text. Nonzero exit is a completed process
result with `isError: true` and `terminationReason: "exit"`, not an internal
exception. A start failure has no execution object and uses the structured
`process_start_failed` error kind.

The `environment` request is an explicit override list, not an inheritance
switch. The base environment is a policy-selected allowlist. Secret-looking
names (`*_KEY`, `*_TOKEN`, `*_SECRET`, credentials, cookies and authorization)
are denied even when supplied as an override unless a separate credential
capability and approval explicitly authorize them. Duplicate names are invalid;
case-fold collisions are duplicates on Windows. A conformance runner injects a
secret canary and requires both absence from the child and absence from all
wire/journal/artifact output.

## `shell.exec`

`shell.exec` is distinct and always names one interpreter: `bash`, `sh`,
`pwsh`, `powershell`, or `cmd`. It accepts a command string intentionally and is
therefore dangerous by default. The daemon does not guess a shell from syntax.
It uses a documented noninteractive invocation:

- bash: `bash --noprofile --norc -c <command>`;
- sh: `sh -c <command>`;
- PowerShell Core: `pwsh -NoLogo -NoProfile -NonInteractive -Command <command>`;
- Windows PowerShell: analogous `powershell` flags;
- cmd: `cmd.exe /d /s /c <command>`.

Shell profile/startup loading is disabled unless separately approved. Quoting
belongs to the selected shell; qxnm-forge passes the command as one argument rather
than re-quoting fragments.

## `terminal.open`

`terminal.open` is a dangerous tool identity. Its headless lifecycle is exposed
through the version-compatible RPC methods `terminal/open`, `terminal/write`,
`terminal/resize`, `terminal/signal`, `terminal/attach`, and `terminal/close`.
The request/response and notification shapes are frozen in
`schemas/protocol/terminal.schema.json`.

`terminal/open` starts one executable with argv, workspace-relative `cwd`, the
same explicit environment policy as `process.exec`, an initial size, bounded
input/output/lifetime limits, and a disconnect policy. The success response
returns opaque `terminalId` and connection-bound `attachmentId`, a `ptyKind`
of exactly `unix_pty` or `windows_conpty`, and the latest per-terminal output
sequence. `pipe`, `socket`, or a merged stdout/stderr capture MUST NOT be
reported as a PTY kind.

Terminal output is a separate `terminal/event` notification, not an Agent
`event` envelope. Each terminal has an independent monotonically increasing
`seq`; `terminal.output` carries one merged `pty` stream, `terminal.truncated`
records bounded retention loss, `terminal.exited` reports process completion,
and `terminal.closed` reports cleanup. Output can be replayed after an attach
from `afterSeq`, but the attach response is always sent before replay events.
Within one live lifecycle, all `terminal.output`/`terminal.truncated` events
precede the unique `terminal.exited`, the unique `terminal.closed` follows it,
and no terminal event follows `terminal.closed`. Replay may reuse retained
output sequence numbers only inside the response-delimited attach replay
window; it does not relax live-event monotonicity.

`limits.eventBytes` bounds the raw PTY bytes represented by one
`terminal.output.byteCount`; it is not a second JSON frame-size budget. The
server still accounts for UTF-8/JSON escaping and the fixed notification
envelope so the actual serialized `terminal/event` remains within both the
initialized `maxEventBytes` and `maxFrameBytes`. If those negotiated budgets
cannot contain a worst-case escaped chunk, the implementation splits the raw
chunk further or rejects the requested terminal limits before launch.

Exactly one attachment controls writes, resize, signals, and close. `inputSeq`
is strictly increasing and a full input buffer returns retryable
`terminal_backpressure` without partial acceptance. `terminal/attach` requires
an explicit `takeover` decision when another live attachment owns the control
lease. Attachment IDs are capabilities bound to the transport connection and
MUST NOT be persisted in Session messages, logs, or artifacts.

`terminal/close` with `terminate`, daemon shutdown, lifetime expiry, idle
expiry, output-limit termination, and owner disconnect under the `terminate`
policy all use the same process-tree cleanup path. A `retain` policy is bounded
by the declared lifetime and never makes a terminal immortal. A terminal RPC
without an explicit approval/policy is denied in headless mode.

共同 runner 的 terminal 动态案例使用额外、窄化的
`QXNM_FORGE_TERMINAL_CONFORMANCE_POLICY=fixture-only` 启动策略。它只授权 runner 临时
workspace 中 hash 固定的离线 helper、固定解释器/argv 和 bounded limits；
`--conformance` 本身不授权 terminal。实现不得在生产模式读取该测试策略，也不得把
它扩展为任意命令权限。通过该案例证明 PTY 生命周期实现，不等于已经提供通用生产
审批策略。

## Cancellation and process trees

Before resuming user code, each process/shell/terminal launch is placed in a
killable tree container:

- Unix: a new process group/session; cancellation signals the group, waits a
  bounded grace period, then sends the force signal to the group and reaps
  children. A process-group-only implementation is sufficient for cooperative
  descendants but MUST NOT claim adversarial-descendant conformance. The
  stronger conformance profile additionally tracks/contains descendants that
  call `setsid`, double-fork, or otherwise leave the original group, using an
  OS-supported descendant guard or isolation boundary.
- Windows: a Job Object configured for kill-on-close MUST be created and
  configured before child user code can run. The conformant launch sequence is
  `CREATE_SUSPENDED` → assign to Job → configure kill-on-close → resume. Starting
  normally and assigning the Job afterwards is an explicit race and is not
  conformant, even if ordinary tests happen to kill the direct child.

Killing only the immediate child is nonconformant. Timeout, cancellation,
client disconnect policy, daemon shutdown, and output-limit termination use the
same tree cleanup path. The result distinguishes requested cancellation,
timeout, output limit, signal, and normal nonzero exit.

The portable structured error kinds for executor/terminal operations are:

| condition | code | retryable | `details.kind` |
| --- | ---: | :---: | --- |
| invalid request or unsafe path/argv | -32602 | false | `executor_invalid` |
| child could not start | -32603 | false | `process_start_failed` |
| containment could not be established | -32603 | false | `process_tree_unavailable` |
| requested hard sandbox unavailable | -32603 | false | `sandbox_unavailable` |
| timeout | -32006 | false | `process_timeout` |
| requested cancellation | -32007 | false | `cancelled` |
| output limit | -32009 | false | `output_limit` |
| terminal input buffer full | -32011 | true | `terminal_backpressure` |
| unknown terminal | -32010 | false | `terminal_not_found` |
| stale/wrong attachment | -32010 | false | `terminal_attachment_stale` |
| terminal already controlled | -32004 | true | `terminal_busy` |

The error message text is display-only. Clients branch only on code,
`retryable`, and `details.kind`.

## Backpressure and output limits

Readers continuously drain both stdout and stderr to prevent pipe deadlock.
Live events are bounded and flow-controlled. Once inline output reaches the
negotiated cap, the executor either streams remaining bytes into a size-limited
artifact or discards them while continuing to drain. It never buffers without
bound. Crossing the hard total output limit terminates the process tree when
policy says continued execution is unsafe.

Truncation is explicit and includes captured/omitted byte counts when known.
Truncation markers are metadata, not text silently inserted into command output.
Protocol frames never exceed `maxEventBytes`.

## Optional hard-sandbox execution

An executor may accept the explicitly named `sandbox` request from
`schemas/hard-sandbox.schema.json`. Absence means the ordinary approved host
executor path; it MUST NOT silently enable or claim hard isolation. Presence of
`linux-bwrap-v1` means the complete ADR 0016 profile is required before launch.
A successful child result uses `containment:"os_isolation"`; failure to build
the exact profile produces no child execution object and
`-32603/sandbox_unavailable`. Approval binds the profile, workspace access and
network mode into the normalized operation hash, so changing any of them
invalidates an earlier approval.

The hard-sandbox request is the optional `sandbox` member of the existing
`process.exec` tool arguments; it does not create another JSON-RPC method or a
second tool name. Implementations that advertise `process.exec` must understand
the closed `sandbox` shape even when no backend is configured: an explicit
request then produces a structured `sandbox_unavailable` tool result with no
`execution` object and no host fallback, rather than treating the member as an
unknown field. Provider-visible tool Schema includes this optional member only
when the implementation can preserve these validation and failure semantics.

`linux-bwrap-v1` configuration, fixed production backend discovery and the
dual-gated conformance override are defined by ADR 0016. A configured startup
self-test failure is an initialization failure, not permission to initialize in
host-executor mode. Ordinary approval remains mandatory and binds the exact
profile, workspace access and isolated-network choice before side effects.

## Project language profiles

[language-profiles.json](language-profiles.json) defines project detection,
toolchain detection, and safe argument-vector templates for .NET, Rust, Go,
TypeScript, and Python projects. Profiles describe the **target project**, not
the daemon implementation. Detection is read-only. A detected command still
passes the normal permission policy; a profile is not approval.

Command selection uses the nearest unambiguous manifest within the workspace.
Ambiguity is reported and requires a target. Toolchain absence is a structured
unsupported result. Lockfiles select package managers; the implementation MUST
NOT install a toolchain or dependencies without explicit approval.

Template placeholders normally expand to one argument. `${goFiles}` is the one
v0.1 list placeholder: it expands to a deterministic, workspace-bounded,
lexically sorted argv element for every discovered `.go` file, excluding
vendor/cache/generated directories according to the Go profile. It is never
shell-expanded. An empty list makes the format check succeed without launching
`gofmt`.
