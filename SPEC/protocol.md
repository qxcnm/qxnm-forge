# Headless protocol v0.1

Status: draft. Project: qxnm-forge. Author: 高宏顺
`<18272669457@163.com>`.

The protocol uses JSON-RPC 2.0 semantics over UTF-8 NDJSON. Method names are
project-neutral so a later product rename does not alter the wire protocol.

## Framing and process channels

In stdio mode, each JSON-RPC message is encoded as one compact JSON value
followed by LF (`0x0A`). A receiver MUST accept CRLF by removing a single CR
immediately before LF. It MUST split only on LF, not on other Unicode line
separators. JSON strings contain escaped newlines and therefore never span
frames. Decoders MUST preserve incomplete UTF-8 code points across read chunks.

An inbound final frame without LF MAY be accepted at clean EOF if it is valid,
complete JSON and within `maxFrameBytes`. An incomplete frame at EOF is a parse
error. Blank lines are invalid. Duplicate object keys MUST be rejected. Numbers
used for IDs or counters MUST be integers in their schema range.

In RPC mode stdout contains protocol frames only. Logs, diagnostics, crash
reports, progress text, and provider output go to stderr or configured log
sinks. Before writing a frame the sender MUST serialize it completely and
enforce the negotiated byte limit; concurrent writers MUST NOT interleave.

中文说明：RPC 模式的 stdout 是协议专用通道，任何调试输出都不得混入；读取时必须
正确处理 UTF-8 被任意拆包的情况。

## Connection lifecycle

The first valid request MUST be `initialize`. The server MUST NOT emit events,
accept notifications, or process any other method first. A second `initialize`
on the same connection is invalid request. A failed initialization closes the
connection after its error response.

Request IDs are opaque strings or integers. Servers copy them byte-for-byte in
the response and MUST NOT infer ordering from them. Clients SHOULD use strings
to avoid integer precision differences. `null` and fractional IDs are invalid.

The initialize request is exactly:

```json
{"jsonrpc":"2.0","id":"init-1","method":"initialize","params":{"protocolVersions":["0.1"],"client":{"name":"conformance-runner","version":"0.1.0"},"capabilities":{}}}
```

The server selects one offered version and returns:

```json
{"jsonrpc":"2.0","id":"init-1","result":{"protocolVersion":"0.1","implementation":{"name":"qxnm-forge-dotnet","version":"0.1.0","language":"dotnet"},"capabilities":{"methods":["initialize","faux/configure","run/start"],"eventTypes":["run.started","turn.started","message.started","message.delta","message.completed","run.completed","run.failed","run.cancelled","run.interrupted"],"providers":[{"id":"faux","models":["faux-v1"]}],"tools":[],"transports":["stdio"]},"limits":{"maxFrameBytes":1048576,"maxEventBytes":262144,"maxArtifactBytes":1073741824,"maxConcurrentRuns":1}}}
```

`capabilities` and `limits` describe actual behavior, not planned work. Every
conformance implementation exposes `faux` / `faux-v1`; other Provider IDs are
listed only when that daemon can actually accept them in the current
configuration. An empty `models` array means models are dynamically configured,
not that a transport is implemented. The initial transport suite uses the
synthetic conformance IDs `openai-compatible`, `openai-responses`, and
`anthropic`; they probe adapters and are not the complete v1 Provider set.
Production advertisements use canonical Provider IDs from
`providers.v1.json`, and an incomplete vertical slice MUST NOT advertise either
synthetic or canonical IDs merely to resemble a golden trace.

For a canonical live Provider, `capabilities.providers` is derived from the
same usable-route snapshot as `models/list`; it is not an independent registry.
The Provider entry is absent when that snapshot has no usable route. Its
`models` array is the lexicographically sorted, duplicate-free union of the
`modelId` values returned for all of that Provider's usable routes. Therefore a
model present in two API families appears once in `initialize`, while
`models/list` retains both route-qualified descriptors. The `faux` conformance
Provider is outside the live manifest and is not evidence that any live route
is configured. With no live configuration, no canonical Provider is
advertised. The Provider entries themselves are sorted lexicographically by
`id`, including `faux` when conformance mode enables it.

中文说明：`initialize` 只给出 Provider 级模型并集，路由身份以
`models/list` 中的 `apiFamily` 为准；两者必须来自同一次可用路由计算，不能分别维护
而产生不一致。

`capabilities.hardSandbox`, when present, is the complete named capability
object from `hard-sandbox.schema.json`, never a bare optimistic Boolean. It may
be emitted only after that process has successfully run the backend self-test.
Absence means no hard-sandbox claim. A later setup failure for an explicitly
requested profile fails closed and does not silently fall back.

For `linux-bwrap-v1`, explicit profile configuration and backend discovery obey
ADR 0016. If that configured startup self-test fails, `initialize` returns
`-32603` with `details.kind:"sandbox_unavailable"` and the connection closes;
the daemon MUST NOT return a successful initialization with the capability
silently omitted. This differs from an unconfigured daemon, which initializes
normally without `capabilities.hardSandbox` and rejects any injected explicit
sandbox execution request before host child startup.

Every method and event type the implementation can produce MUST be listed.
Conversely, listed capabilities MUST be implemented. `tools` may be empty. An
agent lists a tool only when it can validate, authorize, execute, and return
that tool in the current mode; domain DTOs or planned handlers are insufficient.
In particular, an agent that can execute tools or compact context advertises the associated tool,
approval, retry, and compaction event types even when one text-only golden trace
does not exercise them.

`agentProfiles/list|create|update|delete` 是一个原子 capability 组。只有 application
database v0.2、CAS CRUD、`run/start.agentProfile`、durable run snapshot 与恢复路径全部可用
时，服务才同时广告四项；不得广告部分集合。生产模式 MUST NOT 广告
`faux/configure`，该方法只允许在显式 conformance mode 出现。

Profile mutation 先对原始 wire 字符串按 Unicode scalar value 执行 schema 长度边界，再只
trim `displayName/description/instructions` 两端的 Unicode `White_Space`；规范化后的必填文本
仍须非空。`model/provider/apiFamily`、Profile ID 与工具 ID 不 trim、不折叠大小写，作为精确
身份比较；`modelId` 的边缘 Unicode `White_Space` 必须拒绝。

update 的 `expectedRevision` 合法命中 `9007199254740991` 时，服务必须在写入前返回
`-32009/agent_profile_revision_exhausted` 且 `retryable:false`；不得误报 stale，也不得生成超出
JSON 安全整数范围的下一 revision。

`maxFrameBytes` applies to inbound and outbound JSON before LF.
`maxEventBytes` is the maximum serialized `event` notification and cannot
exceed `maxFrameBytes`. Larger data is stored as an artifact reference.

## Starting a run

`run/start` parameters are:

```json
{"sessionId":"s-opaque","input":{"role":"user","content":[{"type":"text","text":"hello"}]},"provider":{"id":"faux","modelId":"faux-v1"}}
```

选择已持久化 Agent Profile 时增加精确 revision 引用；不能使用 `extensions` 夹带：

```json
{"sessionId":"s-opaque","input":{"role":"user","content":[{"type":"text","text":"review"}]},"provider":{"id":"faux","modelId":"faux-v1","apiFamily":"faux"},"agentProfile":{"profileId":"profile-opaque","revision":2}}
```

`provider.apiFamily` is an optional brand-neutral route discriminator. It MUST
be present when one configured `provider.id` / `modelId` pair has more than one
usable family. In particular an image run selects
`{"id":"openrouter","modelId":"...","apiFamily":"openrouter-images"}`.
Omitting it when selection is ambiguous is `-32602/invalid_params`; the daemon
never guesses from prompt content or output modality.

存在 `agentProfile` 时，`provider.id/modelId/apiFamily` 必须与 Profile 的完整
`model.providerId/modelId/apiFamily` 精确相等，且 apiFamily 不再可省略。服务在任何 Session
或 run 副作用前验证 Profile 存在、revision、enabled、route 可用性和工具上限。有效工具先取
`requestedToolIds ∩ initialize.capabilities.tools`，再受 runtime policy、project trust、
审批与 sandbox 继续收窄。`dangerousActionMode:ask` 不等于批准，`deny` 直接拒绝危险操作。

If `sessionId` does not exist, `run/start` atomically creates it in the current
workspace. If it exists, the daemon MUST hold the session lock. Before success,
the daemon validates the complete request, assigns opaque message/run IDs, and
durably appends both the input and accepted-run record. Only then it responds:

```json
{"jsonrpc":"2.0","id":"run-request-1","result":{"runId":"run-opaque"}}
```

The response MUST appear on stdout before every event for that run. Returning a
`runId` means the run was accepted, not completed. Completion MUST NOT be a
delayed response. Every accepted run produces exactly one terminal event even
if it is cancelled, the provider disconnects, or recovery finds it after a
crash. A synchronous validation/permission/locking failure returns an error and
creates no run.

绑定 Profile 的 accepted record 必须同时包含符合
`agent-profile.schema.json#/$defs/runSnapshot` 的 `agentProfileSnapshot`。后续更新、禁用或
删除 Profile 不影响已接受 run；恢复只使用该快照。Profile instructions 与 behavior 只形成
本 run 的 system guidance，不能修改权限、工具 registry、credential、路径或 entitlement。

Only one run per session may execute in v0.1. Implementations may run different
sessions concurrently up to `maxConcurrentRuns`. A busy session produces error
`-32004` rather than silently queuing a second start.

## Events

Events are JSON-RPC notifications using the one method `event`:

```json
{"jsonrpc":"2.0","method":"event","params":{"sessionId":"s-opaque","runId":"run-opaque","turnId":"turn-opaque","seq":1,"time":"2026-07-10T02:03:04.005Z","type":"message.delta","data":{"messageId":"m-opaque","delta":{"type":"text","text":"hello"}}}}
```

For each session, `seq` is strictly increasing across all live and replayed
events. Gaps are allowed; reuse or regression is not. `time` is UTC RFC 3339
with a trailing `Z` and is informational—ordering comes only from `seq`.
`turnId` is present for turn/message/tool events and absent for run-only events.
Events from different sessions and responses to unrelated request IDs may
interleave.

The terminal types are exclusively:

- `run.completed`
- `run.failed`
- `run.cancelled`
- `run.interrupted`

No event for a terminal run may follow its terminal event. `run.failed` denotes
a handled provider, validation, tool, policy, or internal failure.
`run.cancelled` denotes an acknowledged local cancellation.
`run.interrupted` denotes recovery of a run whose outcome cannot safely be
known, including a process crash or lost provider stream.

The minimal successful trace is `run.started`, `turn.started`,
`message.started`, zero or more `message.delta`, `message.completed`, and
`run.completed`. `turn.completed` is normative when a turn includes tool
activity and MAY be omitted by a text-only v0.1 implementation. Golden traces
normalize opaque IDs and timestamps but never reorder events.

For non-streaming image output, zero `message.delta` events are normative.
Every final `image_ref` names an earlier durable Session artifact; artifact
bytes, base64, data URLs, remote URLs and host paths never appear in protocol
frames. The durable order is one `artifact.created` per validated image in
source order, then the assistant `message.appended`, then the
`message.completed` event record and notification.

For a tool call, `tool.requested` means the complete Provider arguments have
been durably normalized into one `tool.intent`; it does not mean execution was
authorized. Lookup, argument validation, preflight, and policy failures still
produce one `tool.requested` followed by one error `tool.completed`, allowing a
controller to correlate the Provider request with its deterministic result.
Unknown tools use structured error kind `tool_not_found`; invalid arguments use
`tool_arguments_invalid`; policy or approval denial uses `permission_denied`.
Clients MUST NOT distinguish these cases by parsing tool-result text.

Every semantic event is append-before-observe. In particular:

- `tool.intent` and its `event.emitted(tool.requested)` are durable before
  `tool.requested` is written to stdout;
- `approval.requested` and its event record are durable before
  `approval.requested` is written;
- `approval.resolved` and its event record are durable before
  `approval.resolved` is written;
- `tool.result`, the canonical tool `message.appended`, and the event record are
  durable before `tool.completed` is written.

There is exactly one `tool.intent`, one `tool.result`, one canonical tool
message, and one `tool.completed` per complete Provider tool call.
Implementations do not append a second intent to represent an approval
transition.

## Control methods

All accepted state-changing control requests are appended before their success
response.

Agent Profile application methods 使用封闭 DTO：

- `agentProfiles/list {}` 返回 `{profiles:[...]}`，按 `updatedAt` 降序、再按 ASCII
  `profileId` 升序；
- `agentProfiles/create {profile}` 由服务生成 opaque ID、`revision:1` 与 UTC 时间戳，返回
  `{profile}`；
- `agentProfiles/update {profileId,expectedRevision,profile}` 是完整替换，CAS 成功时 revision
  精确加一并返回 `{profile}`；
- `agentProfiles/delete {profileId,expectedRevision}` 使用同一 CAS，成功返回
  `{deleted:true}`。

Profile 字段、边界、模型三元组与运行快照以 `agent-profile.schema.json` 和 ADR 0028 为准。
未知参数、未知 Profile 字段、credential/endpoint 字段都属于
`-32602/agent_profile_invalid`。失败的 mutation 不修改 application database。

自定义 Provider connection 使用 ADR 0029 的封闭 DTO：

- `providerConnections/list {}` 返回 `{connections:[...]}`，每项只有非敏感配置和
  `credentialConfigured`；
- `providerConnections/create {connection}` 返回 `{connection,restartRequired:true}`；
- `providerConnections/update {connectionId,expectedRevision,connection}` 使用 CAS 并返回
  `{connection,restartRequired:true}`；
- `providerConnections/delete {connectionId,expectedRevision}` 返回
  `{deleted:true,restartRequired:true}`；
- `providerConnections/discoverModels {connectionId,expectedRevision}` 是用户显式触发的远端
  模型发现，成功以 CAS 仅替换该连接的 `modelIds`，返回
  `{connection,discoveredCount,restartRequired:true}`。它使用 ADR 0029 的 redirect、时限、正文
  和模型数量边界；失败不得修改连接或泄漏 endpoint、credential 与远端正文。

本地管理边界还定义 `providerCredentials/set {providerId,credential}` 与
`providerCredentials/remove {providerId}`，成功只返回
`{providerId,credentialConfigured,restartRequired:true}`。它们不得由通用 WebView 方法
转发器开放。credential 绝不进入 connection DTO、模型、Session、日志或错误。连接 mutation
成功后旧 daemon capability 快照必须失效；新的 `models/list` 与 initialize Provider 广告从
同一启用且 credential 可用的连接快照重建。

`models/list` 不执行 Provider 请求；远端发现只能通过上述显式 mutation 发生。模型发现成功后
旧 daemon capability 快照同样必须失效，客户端重新 `initialize` 后才能使用新模型三元组。

- `run/cancel {sessionId, runId}` is idempotent. It returns the current
  `cancellationState`: `requested`, `alreadyRequested`, or `terminal`. A return
  does not replace the eventual terminal event.
- `run/steer {sessionId, runId, input}` appends input to the steering queue.
  Injection occurs only at the state-machine point defined in
  [agent-state-machine.md](agent-state-machine.md).
- `run/followUp {sessionId, runId, input}` appends input to the follow-up queue.
- `approval/respond {sessionId, runId, approvalId, decision}` durably records a
  decision. Unknown, expired, or already-resolved IDs are errors. A successful
  response is `{accepted:true}`. The daemon appends the resolution, responds to
  this request, then may emit `approval.resolved` and resume the run; therefore
  no `tool.started` for that approval may precede the success response.
- `session/get {sessionId, afterSeq?}` returns the complete selected-branch
  message snapshot plus optional durable replay events. `latestSeq` is the
  greatest durable `event.emitted.data.event.seq`, or `0` when there is no
  durable event; it is never the journal record `seq`. `afterSeq` filters only
  returned events to `event.seq > afterSeq`, in increasing event-sequence
  order. It does not filter `messages` or change `latestSeq`. Recovery records
  are appended before this snapshot is returned, so a recovered session has
  `activeRunId: null` and includes its durable `run.interrupted` event.
- `session/list {cursor?, limit?}` 返回真实 Session 的脱敏摘要页。默认 `limit` 为 64，
  合法范围为 1..128；结果严格包含 `{sessions,nextCursor,hasMore}`，终页也返回
  `nextCursor:null`。排序、live-view cursor 语义、帧上限和摘要字段遵循
  [ADR 0030](adr/0030-session-lifecycle-application-service.md)。
- `session/archive {sessionId}` 在 Session 静止且 journal 健康时 durable 更新工作区外
  归档元数据，返回 `{session}` 且 `archived:true`；它不改写 journal。
- `session/restore {sessionId}` 使用相同边界移除归档状态，返回 `{session}` 且
  `archived:false`。
- `session/delete {sessionId}` 只在 writer/reservation 锁、完整普通树验证、固定 tombstone
  删除和归档元数据清理全部完成后返回 `{deleted:true}`。活动 Session、损坏 journal、链接、
  reparse point、device 或不安全半删除状态必须失败关闭。
- `session/branch/select {sessionId, expectedHeadRecordId,
  targetLeafRecordId}` appends one `branch.selected` whose parent and target are
  the same earlier quiescent record. The compare, tree validation, append and
  response follow [ADR 0011](adr/0011-portable-session-branch-and-compaction.md).
  Success returns the target, selection record ID and new selected head.
- `session/compact {sessionId, expectedHeadRecordId, firstRetainedRecordId,
  summaryText, provider, usage, tokensBefore, tokensAfter, strategy}` appends a
  canonical assistant summary followed by `context.compacted`. It is allowed
  only at a quiescent boundary, `tokensAfter` cannot exceed `tokensBefore`, and
  it acknowledges only after the compaction record is durable. Success returns
  the summary message/record, compaction record and selected-head IDs.
- `terminal/open` starts an explicitly approved PTY/ConPTY process and returns
  an opaque terminal/attachment pair. Its parameters and limits are defined by
  `protocol/terminal.schema.json`; `ptyKind` is always `unix_pty` or
  `windows_conpty`, never `pipe`.
- `terminal/write {sessionId, terminalId, attachmentId, inputSeq, data}` sends
  bounded UTF-8 input. `inputSeq` is strictly increasing; a full input queue
  returns retryable `-32011/terminal_backpressure` and accepts no partial bytes.
- `terminal/resize {sessionId, terminalId, attachmentId, size}` changes columns
  and rows using the native PTY/ConPTY resize primitive before returning the
  accepted size.
- `terminal/signal {sessionId, terminalId, attachmentId, signal}` accepts only
  `interrupt`, `terminate`, `kill`, or `hangup`; numeric host signals are never
  exposed on the wire.
- `terminal/attach {sessionId, terminalId, afterSeq, takeover}` obtains one
  connection-bound control attachment. The response includes
  `replayThroughSeq`; replay `terminal/event` notifications are sent only after
  that response. `takeover` must be explicit when another live attachment owns
  the terminal.
- `terminal/close {sessionId, terminalId, attachmentId, mode}` uses `detach` or
  `terminate`. Daemon shutdown, lifetime/idle expiry, and a disconnect under
  the `terminate` policy use the same process-tree cleanup path.
- `models/list {providerId?}` returns only configured, usable models and their
  normalized capabilities. A live descriptor is identified by the triple
  `(providerId, modelId, apiFamily)`, and the result is sorted by that triple.
  Equal `(providerId, modelId)` pairs in different families are retained as
  distinct descriptors; in particular the text and image OpenRouter rows are
  never collapsed or selected from prompt content. `providerId` filters the
  already-usable snapshot and an unknown well-formed ID returns `models: []`.
  Unknown parameter fields are `-32602/invalid_params`. Provider routes,
  credential values, credential-source names, endpoints, adapter IDs, and
  capability-matrix internals are not returned by this method.
- `server/shutdown {graceful, timeoutMs?}` stops acceptance. Graceful shutdown
  waits up to the limit, then cancels active runs; it does not weaken journal
  durability.

Notifications from clients are not accepted in v0.1 because state-changing
operations require an acknowledgement.

### Terminal notifications

Terminal output is deliberately separate from the Agent `event` envelope:

```json
{"jsonrpc":"2.0","method":"terminal/event","params":{"sessionId":"s-opaque","terminalId":"term-opaque","seq":1,"time":"2026-07-14T02:03:04.005Z","type":"terminal.output","data":{"stream":"pty","data":"hello\r\n","byteCount":7}}}
```

`terminal.output` is one merged PTY stream; stdout/stderr identity is not
invented for a terminal. `terminal.truncated`, `terminal.exited`, and
`terminal.closed` carry the bounded retention, process completion, and cleanup
transitions defined by `terminal.schema.json`. A terminal event sequence is
per-terminal and independent of Session event `seq`. Servers drain the native
PTY continuously, keep a bounded replay window, and either apply backpressure
or terminate according to the declared limits; they never buffer unbounded
output in memory. Raw output chunks respect `limits.eventBytes`, while the
complete serialized `terminal/event` notification—including JSON escaping and
envelope overhead—respects initialized `maxEventBytes` and `maxFrameBytes`.
Live output/truncation precedes the unique exited transition, closed follows
exited and is final. An attach replay is the only response-delimited interval
in which retained output may repeat earlier per-terminal sequence numbers.

## Faux provider method

`faux/configure` is available only when the daemon explicitly enables
conformance mode. Its parameters are `{sessionId, scenario}` where `scenario`
conforms to `faux-scenario.schema.json`. Success is `{scenarioId}` and
`scenarioId` MUST equal `"faux:" + scenario.name`. The configuration is
durably appended before response and is consumed by the next accepted run for
that session. If the session does not yet exist, this method atomically creates
its header in the current workspace before appending the scenario; this makes
the conformance handshake `initialize -> faux/configure -> run/start`
self-contained. Production daemons MUST behave as if this method does not exist，且 MUST NOT 在
`initialize.capabilities.methods` 中广告它。

The top-level `steps`, `expectedContext`, and `usage` describe the first
Provider turn. The optional `continuations` array is a FIFO script for later
Provider turns in the same run. Each item contains its own `steps`, optional
`expectedContext`, and optional `usage`; exactly one item is consumed per later
turn. If `expectedContext` is present it is checked before that turn emits any
Provider output. A required later turn with no remaining continuation is a
non-retryable faux configuration failure. This extension is backward
compatible: scenarios without `continuations` keep the single-turn v0.1
behavior.

## Errors

Error responses use the JSON-RPC error position but a fixed portable shape:

```json
{"jsonrpc":"2.0","id":"run-request-1","error":{"code":-32003,"message":"workspace path denied","retryable":false,"details":{"kind":"permission_denied"}}}
```

Clients branch on `code`, `retryable`, and documented fields in `details`; they
MUST NOT parse `message`. Messages MUST be safe for display and MUST NOT include
keys, authorization headers, credential-file contents, raw provider bodies,
stack traces, or unredacted user content. Standard and reserved codes are:

| Code | Meaning | Default retryable |
| ---: | --- | :---: |
| -32700 | parse error | false |
| -32600 | invalid request / lifecycle violation | false |
| -32601 | method not found or gated | false |
| -32602 | invalid params | false |
| -32603 | redacted internal error | false |
| -32001 | incompatible protocol | false |
| -32002 | session locked | true |
| -32003 | permission denied / approval unavailable | false |
| -32004 | run/session busy | true |
| -32005 | provider unavailable or rate limited | true |
| -32006 | timeout | true |
| -32007 | cancelled before acceptance | false |
| -32008 | journal corrupt / migration required | false |
| -32009 | output or resource limit | false |
| -32010 | conflict / stale state | true |
| -32011 | terminal input backpressure | true |

Agent Profile 的固定失败分类是：

| Condition | Code | Retryable | `details.kind` |
| --- | ---: | :---: | --- |
| invalid input or unknown Profile field | -32602 | false | `agent_profile_invalid` |
| missing Profile | -32602 | false | `agent_profile_not_found` |
| stale update/delete `expectedRevision` | -32010 | true | `stale_agent_profile_revision` |
| update at maximum safe revision | -32009 | false | `agent_profile_revision_exhausted` |
| stale `run/start.agentProfile.revision` | -32010 | false | `stale_agent_profile_revision` |
| disabled Profile used by a run | -32003 | false | `agent_profile_disabled` |
| run/Profile model mismatch | -32602 | false | `agent_profile_model_mismatch` |
| Profile model route unavailable | -32005 | true | `agent_profile_model_unavailable` |

revision conflict 可以在 details 中携带 `resourceId/expectedRevision/currentRevision`；模型错误
可以携带 `providerId/modelId/apiFamily`。错误与日志不得回显 instructions 或其他用户内容。

Provider connection 的固定失败分类是：

| Condition | Code | Retryable | `details.kind` |
| --- | ---: | :---: | --- |
| invalid/unknown connection or credential field | -32602 | false | `invalid_params` |
| missing connection/provider identity | -32602 | false | `provider_connection_not_found` |
| duplicate Provider ID | -32010 | true | `provider_id_conflict` |
| stale update/delete revision | -32010 | true | `provider_connection_revision_conflict` |
| connection store writer is locked | -32002 | true | `provider_connection_store_locked` |
| corrupt/unsafe connection store | -32603 | false | `provider_connection_store_invalid` |

这些错误只能携带公开 ID、字段名和 revision；不得携带 base URL、credential、状态路径、
连接 JSON 正文或底层异常。失败的 credential 写入不得修改 presence；连接创建必须在固定
`connection -> credential` 锁顺序内清理同 Provider ID 的历史 orphan credential，Provider ID
变更必须清理目标与旧 ID，连接删除必须清理旧 ID，避免跨进程竞态留下不可达 secret 或把旧
secret 绑定到新的 endpoint。

Session 生命周期的固定失败分类是：

| Condition | Code | Retryable | `details.kind` |
| --- | ---: | :---: | --- |
| invalid Session ID, cursor or limit | -32602 | false | `invalid_params` |
| missing Session | -32602 | false | `session_not_found` |
| active run, pending faux or live writer | -32004 | true | `session_busy` |
| corrupt or incompatible journal/tree | -32008 | false | `session_corrupt` |
| lifecycle store lock contention | -32002 | true | `session_lifecycle_locked` |
| corrupt lifecycle metadata | -32603 | false | `session_lifecycle_store_invalid` |
| lifecycle metadata I/O unavailable | -32603 | false | `session_lifecycle_store_io` |
| deletion cannot be proven safe | -32603 | false | `session_delete_unsafe` |

这些错误最多携带公开 `resourceId` 或输入 `field`，不得携带 transcript、workspace 绝对路径、
journal 行、tombstone 路径、归档文档或底层异常。

Provider status and retry hints belong in redacted `details`, for example
`{"kind":"provider_rate_limit","httpStatus":429,"retryAfterMs":1000}`.
Unknown details fields are disallowed in core schemas; provider-specific data
uses `details.extensions`.

Branch and compaction mutations override the generic retry default only for the
immutable historical conflict below. Their exact portable failures are:

| Condition | Code | Retryable | `details.kind` |
| --- | ---: | :---: | --- |
| stale `expectedHeadRecordId` | -32010 | true | `stale_session_head` |
| active run or Session mutation | -32004 | true | `session_busy` |
| unknown/future target record | -32602 | false | `record_not_found` |
| non-quiescent target ancestry | -32010 | false | `branch_not_quiescent` |
| invalid retained user boundary | -32602 | false | `invalid_compaction_boundary` |
| `tokensAfter > tokensBefore` | -32602 | false | `invalid_compaction_tokens` |
| corrupt or incompatible journal | -32008 | false | `journal_corrupt` |

The historical non-quiescent case is non-retryable because its immutable parent
chain cannot become quiescent later. All mutation errors leave the journal byte
prefix unchanged. A cross-language writer conflict remains exactly
`-32002`, retryable, with `details.kind=session_locked`; it is distinct from an
active-run `session_busy` response.

## Disconnect and replay

Stdio has no reconnect protocol. A controller that restarts a daemon reopens
the session and uses `session/get`/journal-backed event replay from a known
sequence when that capability is advertised. Recovery MUST synthesize and
persist `run.interrupted` for an accepted nonterminal run before accepting a
continuation. It MUST NOT rerun provider calls or non-idempotent tools merely to
reconstruct missing events.

The same message schemas and lifecycle apply to future sockets, named pipes,
and WebSockets. Those transports add authentication and reconnect requests in a
new compatible protocol version; they do not reinterpret v0.1 frames.
