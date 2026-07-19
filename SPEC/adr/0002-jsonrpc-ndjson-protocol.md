# ADR 0002: JSON-RPC semantics over UTF-8 NDJSON

- Status: Accepted
- Date: 2026-07-10
- Project: qxnm-forge
- Author: 高宏顺 `<18272669457@163.com>`

## Context

CLIs, test runners, and future UIs need a language-neutral asynchronous daemon
interface. Long model/tool runs cannot hold a conventional request open as the
only completion signal, and arbitrary log output must not corrupt automation.

## Decision

Use JSON-RPC 2.0 semantics, one UTF-8 JSON value per LF-delimited frame. The
first request is `initialize` and negotiates the version. `run/start` appends
accepted state durably, returns `{runId}`, and only then may events appear. All
events are notifications with method `event` and a common envelope. Exactly one
terminal event ends an accepted run. stdout is protocol-only; stderr is for
diagnostics. Method names are product-neutral.

The same message semantics will be reused on Unix sockets, Windows named pipes,
and WebSockets; transport security is layered around them.

Initialize capabilities are runtime facts, not a product roadmap or a golden
trace fingerprint. Common conformance requires only the methods, Provider,
events, and stdio transport exercised by that test. Additional Provider and
tool entries may differ across implementations and configurations, but every
advertised entry must be usable.

## Consequences

- Controllers can multiplex opaque request IDs and event streams.
- Implementations need a single serialized stdout writer and strict frame
  limits.
- Completion is state/event driven; clients must not await a delayed start
  response.
- Reconnection/event replay needs a later compatible extension, while session
  recovery remains durable in v0.1.
- Differential normalization may ignore honest capability supersets only after
  validating the minimum exercised subset and the shape of every advertised
  entry; it never fabricates support claims.
