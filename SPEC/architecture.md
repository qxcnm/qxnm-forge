# qxnm-forge architecture v0.1

Metadata: draft; author 高宏顺 `<18272669457@163.com>`.

## Independence rule

qxnm-forge vNext consists of two complete active native implementations:

- `qxnm-forge-dotnet`
- `qxnm-forge-rust`

Each implementation MUST be able to call providers, stream model output, run
the agent state machine, execute tools, persist/reopen sessions, expose the
daemon protocol, and run the CLI without FFI, a subprocess, a shared binary, or
the runtime of another implementation. Shared assets are limited to this
specification, provider/model manifests, schemas, fixtures, golden traces, and
conformance tooling.

Go、旧 TypeScript/Python runtime 和 PI reference 不存在于当前发行目录，也不得作为
vNext runtime 恢复。为了读取历史数据，协议和 Session 仍接受五种 `createdBy.language`，
`language-profiles.json` 也继续覆盖五种目标项目语言。实现语言与被操作项目语言无关；
Rust 与 .NET 都必须能操作 .NET、Rust、Go、TypeScript 和 Python 项目。

## Logical modules

Every implementation exposes equivalent boundaries, with idiomatic names:

1. **domain** — content, messages, model capability, usage, errors, approvals,
   artifacts, and events;
2. **provider** — native HTTP/SSE/WebSocket/auth implementations grouped by API
   family;
3. **agent** — deterministic run/turn/tool state machine;
4. **session** — locking, JSONL journal, recovery, migration, and artifacts;
5. **tools** — declarations, validation, permission classification, and file
   operations;
6. **executor** — processes, shells, later terminals, cancellation, and output
   limiting;
7. **protocol** — transport-neutral JSON-RPC message handling;
8. **daemon** — stdio lifecycle and future local transports;
9. **cli** — pure-text user interface and command-line configuration.
10. **storage** — SeaORM/EF Core application data, migrations, and provider adapters.
11. **application service** — the single state/control boundary shared by CLI,
    daemon and React clients；它拥有 Agent Profile CRUD、CAS、模型/工具校验与 run binding，
    并把验证后的 immutable snapshot 交给 agent。

Dependencies point inward toward `domain`. Provider and tool adapters MUST NOT
own agent state. CLI、daemon 与 React MUST call public application services rather
than reading journal files or application database directly。`storage` 不向 UI/transport 暴露 ORM
entity；`agent` 不在运行中重新读取 Profile，而只消费 application service 绑定的 run snapshot。

## Native public APIs

The wire DTOs in `schemas/` are language-neutral. Native APIs SHOULD follow
their ecosystems while preserving cancellation and streaming semantics:

- .NET: `Task`, `IAsyncEnumerable<T>`, `CancellationToken`;
- Rust: `async` plus a `Stream`-compatible event source;

Wire DTOs MUST NOT expose language-specific generics, exceptions, buffers,
class names, stack traces, or arbitrary host objects. Binary data is represented
by artifact/image references, never an in-memory language buffer. Extension
data is legal only under the explicit `extensions` namespace described by
`common.schema.json`.

All new methods/functions follow the method-level documentation rule in the
root of this specification: functional description plus author 高宏顺 and
email `18272669457@163.com`. Review and language quality gates enforce it.

## Provider layering

HTTP behavior is implemented once per API family, not once per provider brand.
A provider manifest selects the family adapter, endpoint policy,
authentication source names, model-catalog reference, header policy, and
compatibility quirks. The referenced catalog supplies model identity,
capability, endpoint, limit, and non-secret fixed-header evidence. A
compatibility flag MUST represent a documented wire-level difference and MUST
have a fixture; it cannot be a hidden brand-specific code path.

The frozen v1 scope is the 35 text providers, nine text API families, and one
image API family in [providers.v1.json](providers.v1.json), with the attributed
snapshot in [models.v1.json](models.v1.json). A catalog row is not a support
claim. Sending plain text alone does not establish support. Streaming, tool
use, reasoning, image input, image output, and authentication are tracked
separately.

## Source of truth and change flow

For observable behavior, precedence is:

1. versioned JSON Schemas and normative prose in `SPEC/`;
2. accepted ADRs;
3. golden traces and conformance fixtures;
4. implementations.

If the first two disagree, the more recently accepted ADR MUST explicitly
amend the prose/schema in the same change. If two implementations expose a
useful but unspecified behavior, it remains non-portable until specified.

Public changes follow this order:

1. document the semantic change and security impact;
2. accept or amend an ADR when architectural;
3. update schemas and golden traces;
4. update the capability matrix to the non-public `implemented` level;
5. independently update all affected implementations;
6. promote to `conformant` only after common tests pass.

## Deployment boundaries

The v0.1 daemon uses stdin/stdout NDJSON. Unix sockets, Windows named pipes, and
WebSocket transports may be added later, but MUST carry the same JSON-RPC
messages and MUST negotiate a protocol version. Transport authentication and
origin rules are additions, not changes to run semantics.

The CLI and daemon may be packaged together in each language. Packaging MUST
NOT introduce a dependency on PI or another qxnm-forge implementation. Historical
evidence recorded in specifications does not make deleted reference source a runtime dependency.
