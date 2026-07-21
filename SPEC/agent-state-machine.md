# qxnm-forge Agent state machine v0.1

## Entities and invariants

A **session** is a durable branched conversation. A **run** begins with one
accepted user input and ends with exactly one terminal run event. A **turn** is
one provider response plus every tool call/result requested by that response.
A **message** is a durable user, assistant, or tool-result item. A **tool
execution** is one validated call and its single result.

Opaque IDs are unique within their session and never reused. All accepted
inputs, queue operations, approvals, messages, tool intents/results, terminal
states, and context-compaction records are persisted before the corresponding
success response or observable event.

The canonical run states are:

```text
accepting -> accepted -> running -> terminal
                              \-> waiting_approval -> running
terminal := completed | failed | cancelled | interrupted
```

Only `accepted` is externally acknowledged by `run/start`. Transitions are
monotonic. Terminal states cannot transition. Cancellation is a request that is
observed at every await/I/O boundary; it is not itself a terminal state until
cleanup and terminal persistence finish.

## Normal run ordering

1. Validate request, model, optional Agent Profile reference/snapshot, context, tools/policy, and
   session lock without side effects. Profile instructions are guidance, never an authorization
   input.
2. Append the input message and `run.accepted`（including the immutable Profile snapshot when
   selected）；flush；respond with `runId`.
3. Append/emit `run.started`.
4. For each provider turn, append/emit `turn.started`, then start the assistant
   message.
5. Stream normalized message deltas. Persist the completed assistant message;
   emit `message.completed` only after persistence. A non-streaming image
   family emits no image delta: it validates the complete response, durably
   publishes every image artifact and its `artifact.created` journal record,
   then persists one assistant message containing `image_ref` blocks before
   `message.completed` becomes observable.
6. If it contains tool calls, process the tool batch as below, append the tool
   result messages, and emit `turn.completed`.
7. Apply steering, continuation, retry, compaction, and follow-up rules below.
8. Persist one terminal state; emit the matching terminal event; release the
   active-run claim.

An event listener/observer failure MUST NOT corrupt the run or suppress later
events. The implementation records a redacted diagnostic and continues. A
failure to persist an event/state is different: the daemon MUST stop producing
later observable events and recover the run as interrupted.

## Provider stream

A provider attempt may produce content deltas followed by one completed
assistant message, or a structured failure. A failed/cancelled partial stream
does not become a normal assistant message. It is journaled as an interrupted
attempt for diagnostics and the run follows retry or terminal rules. Tool calls
are actionable only after their complete arguments have been assembled and the
assistant message is durably completed.

Within a message, `message.delta` order is provider-normalized source order.
Partial JSON never enters a tool executor. Reasoning may stream as a separate
delta type, but redacted/opaque reasoning MUST not be converted to user-visible
text.

Image bytes, base64, data URLs, remote URLs, and host paths never enter a
`message.delta`, event, canonical message, error, or diagnostic. An image
family may emit bounded text deltas only when its normative transport is
streaming; `openrouter-images` v0.1 is non-streaming and therefore publishes its
optional text together with final `image_ref` content. If any image fails
response validation, no assistant message is committed. If durable publication
fails after an earlier artifact in the same response was committed, those
earlier artifact records remain valid but unreferenced and the run fails; an
append-only journal is never rolled back or rewritten.

## Tool batch ordering

Each call is processed in assistant source order through lookup, restricted
JSON Schema validation, path/resource preflight, and permission evaluation.
Unknown tools and invalid arguments produce deterministic error tool results;
they never reach an executor.

For every complete Provider call, append exactly one `tool.intent` before
emitting `tool.requested`. Its immutable status records the disposition known
at that append point: `rejected` for lookup/argument failure, `denied` for
direct policy denial, `awaiting_approval` for `ask`, `started` for direct
execution, and `prepared`/`approved` only where a host policy can establish
those states without another intent. Approval transitions are represented by
`approval.requested`/`approval.resolved`, never by a duplicate intent.

In `sequential` mode, each allowed call follows:

`tool.requested -> approval if needed -> tool.started -> zero or more tool.delta -> tool.completed`

before the next call begins.

In `parallel` mode:

1. preflight and approval requests are created sequentially in assistant source
   order;
2. all allowed calls start after the batch's required approvals resolve;
3. execution events and `tool.completed` are emitted in actual completion order;
4. tool-result messages are appended to model context in assistant source order,
   regardless of completion order.

This preserves deterministic model context without concealing real execution
timing. Implementations MUST bound concurrency. Tools that write overlapping
resources MUST be serialized or rejected as a conflict. A denied call becomes
an error tool result in its original source position.

Every finalized call appends `tool.result` and then its canonical tool message
before emitting `tool.completed`. Lookup, validation, and denial results
include a portable structured error. After the complete batch, the tool
messages enter the next Provider context in assistant source order. A faux
continuation with `expectedContext` is the normative black-box proof of this
handoff.

Headless default denial does not create approval events. Interactive `ask`
appends/emits `approval.requested` and enters `waiting_approval`. A successful
`approval/respond` appends the resolution before its response; only after that
response may the run emit `approval.resolved` and either start the tool or
finalize a denied result. Timeout or disconnect follows the denied path with a
server-authored resolution source.

`terminate` hints may stop after the current batch only if every finalized call
in the batch requests termination. Cancellation differs: it attempts to stop
all outstanding calls immediately and waits for process-tree/resource cleanup.

## Steering and follow-up queues

Queue insertion is append-only and acknowledged only after persistence. Queue
mode is `all` or `one-at-a-time`; FIFO order is mandatory.

- Steering is checked after the current assistant response and entire tool
  batch finish, before a new provider request. It never skips already requested
  tool calls. Selected steering messages are appended to context, then another
  turn begins.
- Follow-up is checked only when the run would otherwise complete: the last
  assistant response has no pending tool use and no steering input was selected.
  Selected follow-ups begin another turn in the same run.

In `all` mode every item currently present at the drain point is injected. In
`one-at-a-time` mode only the oldest is injected. Items arriving after the
atomic drain remain for the next drain point. Each queue item has an ID and a
durable consumed record, so crash recovery never duplicates it.

## Cancellation

`run/cancel` is idempotent. The first accepted request appends a
`run.cancellation_requested` intent and signals provider requests, approval
waits, tool operations, and child processes. Later requests do not append
another intent. The daemon waits
for cleanup, persists `run.cancelled`, then emits it. Process and shell tools
must terminate the whole process tree as defined in [executor.md](executor.md).

If the requested run is already terminal, cancellation succeeds with state
`terminal` and emits no event. Cancellation racing with natural completion has
one winner selected by the durable terminal append; exactly one terminal event
is emitted. Cancellation MUST NOT turn a known completed tool side effect into
an apparent rollback.

When cancellation wins while waiting for approval, the daemon resolves the
approval once with decision `deny` and `resolutionSource:"cancellation"`,
appends a known cancelled `tool.result` plus canonical tool message, emits
`approval.resolved` and `tool.completed` in that order, and then
persists/emits `run.cancelled`. It MUST NOT emit `tool.started` or enter the
executor.

## Retry

Automatic retry is permitted only for failures classified retryable before any
tool execution from that provider response. Retry delay honors bounded
`Retry-After` plus deterministic jitter derived from run/attempt IDs. Retry
policy has maximum attempts and total elapsed time. Every attempt and scheduled
delay is journaled and observable as `retry.scheduled`.

A provider response whose complete tool calls were committed is never
automatically requested again. A tool is automatically retried only if its
declaration explicitly says `idempotent`, the failure proves the operation did
not complete, and policy permits it. Non-idempotent tools are **never** replayed
after crash or an ambiguous result.

Rate limits, 408/429, selected 5xx, connection setup failure, and idle disconnect
may be retryable. Authentication, invalid request, permission denial, schema
validation, context overflow without a successful compaction, and safety
refusal are not retryable by default.

## Context compaction

Compaction is a durable derivation, never deletion or mutation of history. It
records the source branch/range, summary message, token estimates, strategy
version, and first retained record. The next provider context is rebuilt from
the selected compaction plus retained descendants. Failed/cancelled compaction
leaves prior context active.

The exact two-record activation, retained user-message boundary, selected-head
compare, projection order, and branch interaction are normative in
[ADR 0011](adr/0011-portable-session-branch-and-compaction.md). In particular,
the summary alone is an ordinary assistant message; only its durable child
`context.compacted` activates the projection, and `tokensAfter` cannot exceed
`tokensBefore`.

Automatic compaction may occur only between turns. It MUST NOT run while tools
or approvals are pending. Implementations normalize the compaction record so a
different language can continue the session without needing the original
implementation.

## Crash recovery and continuation

On open, the implementation validates the journal and reconstructs state from
records, not from emitted-event guesses. Every accepted run without a terminal
record is appended as `interrupted`; any open provider message is marked
interrupted. A tool intent with no result is classified:

- known not started: append a cancelled/error result;
- known idempotent and proven not completed: MAY offer an explicit retry;
- non-idempotent or ambiguous: append an unknown-outcome result and require
  user acknowledgement before continuation.

Recovery never automatically invokes a provider or tool. A new `run/start`
continues from durable context after recovery is complete.
