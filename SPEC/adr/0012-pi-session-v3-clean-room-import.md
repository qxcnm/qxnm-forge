# ADR 0012: PI Session v3 clean-room one-way import

- Status: Accepted
- Date: 2026-07-14
- Project: qxnm-forge
- Author: 高宏顺 `<18272669457@163.com>`
- Reference evidence: PI commit
  `3f9aa5d10b35223abf6146f960ff5cb5c68053ee` (MIT, read-only)

## Context

PI Session v3 is a JSONL tree, but it is not the qxnm-forge journal format. Its
header contains `type=session`, `version=3`, an ID, timestamp and cwd. Entries
have `type/id/parentId/timestamp`; the snapshot recognizes message,
model/thinking changes, compaction, branch summary, custom/custom-message,
label and session-info entries.

The semantic differences make a byte-for-byte or line-for-line conversion
unsafe:

- PI reconstructs the current leaf as the last non-header file entry. Moving
  the in-memory leaf to an earlier node is not itself persisted.
- qxnm-forge requires every branch jump to be an auditable `branch.selected`
  record; ordinary records may only extend the selected head.
- one PI compaction entry contains a summary and retained boundary, while
  qxnm-forge activates compaction only after a canonical summary message followed
  by `context.compacted` is durable.
- PI custom-message and branch-summary entries can inject user-equivalent model
  context. Importing arbitrary extension context silently would cross a trust
  boundary.
- PI files are permissively parsed by the reference implementation. A clean-room
  importer cannot copy its skip-malformed behavior because silent omissions
  would make provenance incomplete.

The existing qxnm-forge header provenance carries the source format, source session
ID, SHA-256 and pinned reference commit. It cannot strongly identify the
mandatory import report artifact. Discovering a report by display name is
ambiguous, so a minimal optional `reportArtifactId` field is required. PI
import semantics require that field even though other provenance sources do
not.

## Decision

### Trust and I/O boundary

Import is an offline, one-way operation. It MUST:

1. open a caller-selected regular source file without executing PI, loading PI
   extensions, importing a PI package, invoking a tool, contacting a Provider,
   or restoring a run;
2. hold the source open read-only, reject replacement/size ambiguity where the
   platform can detect it, hash the exact accepted bytes, and never modify,
   rename, chmod, truncate or lock-break the source;
3. enforce strict UTF-8, LF JSONL, finite size/line/depth limits, duplicate-key
   rejection, one v3 header, unique IDs, and earlier-only parent references;
4. create a different qxnm-forge Session ID in a sibling temporary target, write
   artifacts first, write and validate the complete journal, flush, then
   publish atomically where available;
5. fail without a usable target when source bytes change, a known core entry is
   malformed, a parent is missing/future, the report does not cover every
   omission/loss, or the final target does not pass the public journal and
   import validators.

The operation is not PI continuation and MUST NOT emit `run.accepted`,
`run.started`, `provider.attempt`, `tool.intent`, `tool.result`,
approval, queue, recovery or event records. Historical message tool-call/result
content may be converted as inert transcript data, but it MUST NOT be executed
or treated as an unresolved tool lifecycle.

### Provenance and privacy

The target header MUST use:

- `provenance.source = "pi-session-v3"`;
- the accepted PI header ID as `sourceSessionId`;
- SHA-256 of the complete source bytes as `sourceSha256`;
- `referenceCommit =
  "3f9aa5d10b35223abf6146f960ff5cb5c68053ee"`;
- `reportArtifactId` naming the mandatory
  `application/vnd.qxnm-forge.pi-v3-import-report+json` artifact.

The target Session ID MUST differ from the PI source ID. Source cwd and
parent-session paths are untrusted host identifiers and MUST NOT be copied to
the portable header, report, extensions, diagnostics or protocol output. The
report records `sourcePathDisposition = "not_persisted"`; the interactive CLI
may display the caller's source path only for the duration of explicit
confirmation. It must not persist a raw path or a reversible/guessable path
hash.

Secret-shaped keys and values are scanned recursively before conversion and
again over every target/report value. Recognized portable content is redacted
in place with an explicit report item. Unknown/custom values are quarantined in
the report only after the same scan. A secret or path is never copied merely
because it appears under an unknown extension key.

### Deterministic mapping

Every PI entry is processed in file order. The importer keeps a source-entry to
target-record map and the selected qxnm-forge head.

| PI v3 value | qxnm-forge v0.1 mapping |
| --- | --- |
| header | new qxnm-forge header with the provenance above; source cwd and parent path are not persisted |
| user message | canonical `message.appended`; string content becomes one text block |
| assistant message | canonical `message.appended`; text/thinking/tool-call blocks and normalized usage are mapped when valid |
| toolResult message | inert canonical tool message; no `tool.intent` or execution record is created |
| model change | `model.selected` with the mapped Provider/model and current thinking value |
| thinking-level change | `model.selected` using the current selected Provider/model; without a prior model it is quarantined and reported |
| compaction | canonical assistant summary message, then `context.compacted`; `firstKeptEntryId` maps to an earlier user-message record |
| branch summary | canonical user message containing the PI snapshot's effective branch-summary wrapper and summary text |
| session info | `session.metadata` with a bounded name or explicit null |
| label | `org.agentprotocol.pi-v3` extension record; target identity is preserved but no unsupported core label semantics are invented |
| custom | `org.agentprotocol.pi-v3` extension record plus a report item; it never enters Provider context |
| custom message | quarantined `org.agentprotocol.pi-v3` extension plus report item; default import never promotes it into Provider context |
| unknown entry type | quarantined `org.agentprotocol.pi-v3` extension plus report item; import continues only when the generic base/tree fields are valid |

Before mapping a PI entry whose mapped parent is not the selected qxnm-forge head,
the importer appends `branch.selected` targeting that earlier mapped parent.
The converted record then extends the selection record. This preserves PI
branch ancestry without violating qxnm-forge's auditable selected-head invariant.
The last source entry remains represented on the selected target chain.

All imported records SHOULD carry a `org.agentprotocol.pi-v3` record extension with
the source entry ID and one-based source line. A PI compaction maps to two
target records; a synthetic branch selection and final report artifact have no
PI entry identity. IDs are newly generated opaque IDs; implementations are not
required to reproduce conformance fixture IDs outside deterministic test mode.

The portable CLI contract in `cli.md` exposes deterministic IDs only when
`--conformance` imports the bundled synthetic fixture. Normal imports generate
fresh record/artifact IDs. Conformance mode never weakens parsing, source
immutability, secret/path scanning, atomic publication, or lifecycle bans.

### Message and usage details

- PI `text` becomes portable `text`; `thinking` becomes `reasoning`.
- PI `toolCall` becomes inert `tool_call` only when ID, safe tool name and
  object arguments are valid.
- Base64 images are decoded under byte/media limits, written as artifacts and
  referenced by `image_ref`; malformed/unsupported images are quarantined and
  reported rather than copied inline.
- PI usage maps `inputTokens = input + cacheRead + cacheWrite`,
  `outputTokens = output`, `cachedInputTokens = cacheRead`,
  `cacheWriteTokens = cacheWrite`, optional reasoning tokens, and the
  self-consistent PI `totalTokens`. An inconsistent total is a known-entry
  validation failure.
- PI stop reasons map `toolUse` to `tool_use`, `aborted` to
  `cancelled`, and otherwise use the equal portable spelling.
- A PI compaction does not record summary-message usage. The synthetic summary
  therefore uses zero usage, is marked with import provenance, and the report
  records `compaction_summary_usage_unavailable`.

### Mandatory report

The report conforms to
`schemas/session/pi-v3-import-report.schema.json`. It binds source byte length,
SHA-256, source version/session ID, fixed reference commit, the new Session ID,
report artifact ID, deterministic counts, and every skipped, quarantined,
redacted, extension-only or lossily converted source entry. Each reported item
contains its source line/type, optional source entry ID, disposition, stable
reason codes and the SHA-256 of the exact source line. Quarantined JSON may be
included only after bounded recursive redaction and is always excluded from
model context.

A successful report may be `completed` or `completed_with_warnings`. There
is no successful state with an unreported omission. The artifact is written
before its `artifact.created` record, and the record's byte length/SHA-256
must match the exact artifact bytes.

## Consequences

- The importer can be implemented independently in five languages without PI
  at build or runtime.
- Imported branch and compaction behavior is auditable under the existing
  portable tree rules.
- Extension data remains recoverable for inspection without silently becoming
  a prompt or permission input.
- Raw source paths are intentionally not portable provenance; this tightens the
  earlier compatibility text to avoid cross-machine path disclosure.
- Import is not considered conformant until the common fixture, report,
  negative security mutations and public journal Schema all pass.
