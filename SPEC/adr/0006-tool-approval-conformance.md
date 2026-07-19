# ADR 0006: Durable tool and approval conformance

- Status: Accepted
- Date: 2026-07-11
- Project: qxnm-forge
- Author: 高宏顺 `<18272669457@163.com>`

## Context

A Provider tool call is untrusted intent, not authority. Five native agents
must agree on lookup/validation failures, headless denial, interactive
approvals, source ordering, cancellation, durable observation, and the exact
point at which a tool result enters the next Provider turn. Event-only tests
cannot prove crash safety, while journal-only tests cannot prove the daemon
actually supplied results back to the Provider.

## Decision

Use one immutable `tool.intent` and one final `tool.result` per complete
Provider call. Persist the intent before `tool.requested`; persist approval
records before their events; persist the result and canonical tool message
before `tool.completed`. Unknown tools, invalid arguments, and denials are
normal error tool results with structured error kinds and never reach an
executor. Headless missing policy denies dangerous calls without fabricating an
approval flow.

Interactive clients advertise `interactiveApprovals`, receive a durable
approval bound to normalized redacted arguments and an operation hash, and
respond with `allow_once` or `deny`. The server records a non-client-controlled
resolution source. Cancellation while waiting resolves once as denied, creates
a cancelled tool result, never starts the executor, and ends the run cancelled.

Extend the conformance-only faux scenario with FIFO `continuations`. A later
turn may assert its complete `expectedContext`, proving tool results entered the
Provider context in source order. Shared machine cases and a black-box runner
check protocol frames, workspace effects, journals, append-before-event, and
the existing portable recovery fixture's no-replay rule. Fixtures contain no
arbitrary shell commands and the runner never invokes a shell.

## Consequences

- Implementations need an approval waiter keyed by opaque ID and integrated
  with run cancellation.
- Tool errors remain model-visible while machine clients branch on structured
  fields rather than text.
- Journals can be audited across languages without interpreting mutable intent
  records.
- Faux continuations are test-only and do not make faux a production Provider.
- Passing static fixtures or the fake daemon does not change capability status;
  each native implementation must pass the same opt-in black-box cases.
