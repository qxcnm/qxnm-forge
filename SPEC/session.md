# qxnm-forge portable session journal v0.1

## Layout

A session is a directory owned by one session ID:

```text
<sessions>/<sessionId>/
  journal.jsonl
  artifacts/
  lock
  writer.lock.d/owner.json
  <implementation>.writer-claim (optional)
```

The first `journal.jsonl` line is a `session` header. Every following line is a
record conforming to `schemas/session/journal.schema.json`. The file is UTF-8
NDJSON with LF; writers emit no BOM and end each committed record with LF.
Records form a tree through `parentId`; the session's selected branch is derived
from explicit branch/leaf records, not file order alone.

Journal `seq` starts at 1 and increases by exactly one per record. `recordId` is
opaque and unique in the session. A non-null `parentId` refers to an earlier
record. Timestamps are UTC RFC 3339 and are not used for ordering. Durable
artifacts use opaque IDs; journal entries never require another language's
object serialization.

## Append and acknowledgement

A state-changing operation follows:

1. acquire the exclusive session writer lock;
2. validate expected state and construct a complete immutable record;
3. write the JSON plus LF in append mode without interleaving;
4. flush language/runtime buffers and request the OS durability primitive
   (`fsync`/`fdatasync`, or Windows `FlushFileBuffers`);
5. only then update observable state, return success, or emit its event.

For a newly created session, the directory and header are flushed before
`run/start` success. Implementations SHOULD batch records only where doing so
does not acknowledge any one record before the batch is durable. Filesystems or
mounts that cannot provide the required primitive MUST disclose a reduced
durability capability and MUST NOT claim crash-recovery conformance.

中文说明：所有“已接受”操作必须先落盘再响应；不能先返回成功、稍后异步写 Session。

Exactly one process holds the writer lock. Readers either take a shared lock or
read only through the last validated complete line. Lock metadata may aid
diagnosis but MUST NOT be used alone to break a live OS lock. Concurrent
`run/start` on the same session is rejected.

`lock` is the persistent, cross-language OS advisory-lock target. Its mere
existence, including as an empty file after a clean close, MUST NOT be treated
as evidence that a writer is active. A runtime that cannot expose a compatible
OS advisory lock MAY use an implementation-namespaced `O_EXCL` writer-claim
sidecar as a conservative fallback. Such a sidecar is removed on a clean close,
MUST NOT reuse or delete another implementation's lock metadata, and is not
evidence of cross-language concurrent-writer conformance. Implementations MUST
ignore unknown auxiliary files while opening the portable journal; only
`journal.jsonl` and referenced artifacts contribute Session semantics.

The mandatory cross-language writer lease is defined by
[ADR 0009](adr/0009-cross-language-writer-lease.md). A writer starts a literal
IPv4 loopback liveness listener, atomically creates `writer.lock.d`, durably
writes bounded `owner.json`, and keeps the listener alive until all writes stop
and ownership is atomically renamed away. Only an explicit connection-refused
result permits stale takeover; timeouts and ambiguous probes remain locked.
This internal listener is not a client transport and grants no general network
authority. OS advisory locks and implementation-namespaced claims remain
defence in depth, but neither alone is cross-language conformance evidence.

## Record model

Core kinds are:

- `message.appended`, carrying a portable message;
- `event.emitted`, carrying the exact portable event envelope sent to clients;
- `run.accepted`, `run.started`, `run.cancellation_requested`, `run.terminal`;
- `turn.started`, `turn.completed`, `provider.attempt`;
- `tool.intent`, `tool.result`;
- `approval.requested`, `approval.resolved`;
- `queue.appended`, `queue.consumed`;
- `context.compacted`;
- `model.selected`, `session.metadata`, `branch.selected`;
- `artifact.created`;
- `faux.configured` for conformance mode.

For conformance-mode replay, each accepted run whose selected Provider is
`faux` consumes the earliest preceding `faux.configured` record that has not
already been consumed. `run.accepted.data.fauxScenarioRecordId` MAY record that
association explicitly. Because the field is optional in v0.1, importers MUST
reconstruct the same FIFO association from journal order when it is absent; an
explicit reference MUST name a preceding, still-unconsumed configuration.
This inference affects only the deterministic faux test queue and never chooses
a production Provider credential or model.

Unknown core kinds are incompatible. Extension records use kind `extension`
and a reverse-DNS namespace. They do not participate in model context unless a
future core record explicitly imports their portable content.

The journal stores normalized events and final messages, never raw provider
transport bytes. Before a daemon writes an `event` notification, it appends the
matching `event.emitted` record; the event's session sequence is therefore
recoverable after restart. Implementations MAY coalesce very high-frequency
tool progress into a bounded artifact, but MUST NOT coalesce semantic message
deltas in a way that changes the normalized trace. The completed message is the
canonical transcript; replayed deltas never override it.

Journal `seq` and event `seq` are distinct counters. Journal sequence is
consecutive for every record. Event sequence is strictly increasing but may
have gaps; `event.emitted` preserves the assigned value. Recovery allocates
above the greatest durable event sequence.

Each complete Provider tool call has exactly one `tool.intent` and one
`tool.result`. A durable `tool.intent` precedes the observable
`tool.requested`; approval records describe later permission transitions rather
than mutating or duplicating that intent. A `tool.result` and canonical tool
message both precede the observable `tool.completed`. The first accepted
`run/cancel` appends one `run.cancellation_requested`; repeated cancellation
requests do not append duplicates.

## Artifacts

Large tool output, images, binary data, and oversized diagnostics are written to
`artifacts/` before an `artifact.created` record points to them. A reference
contains ID, media type, byte length, and SHA-256 digest. Paths are derived from
validated IDs and are never accepted from wire clients.

Artifact creation uses a temporary file in the same directory, size limiting
while streaming, flush, atomic rename, directory flush where supported, then
journal append. A crash may leave an unreferenced temporary file, which safe
garbage collection may remove. A journal MUST never reference a partially
written artifact. Protocol responses return only artifact references, not host
paths.

An assistant `image_ref` MUST name an earlier `artifact.created` record on the
same selected parent chain, and the referenced regular file MUST match its
declared byte length and SHA-256 when read. Provider base64, data URLs, remote
URLs, and absolute or relative host paths are never persisted. Multi-image
responses validate and decode every image before publishing the first artifact;
each artifact is then an independent durable append. A later publication
failure may leave earlier valid but unreferenced artifacts, which are safe for
conservative garbage collection only after proving that no selected or
unselected branch references them.

## Corruption and recovery

At open, validate header, UTF-8, each JSON value/schema, consecutive `seq`,
unique IDs, backward-only parents, artifact hashes when accessed, and state
transitions.

- Session v0.1 has no independent portable durability witness. Any nonempty
  suffix after the last LF is uncommitted, even when it is strict UTF-8, valid
  JSON, and a Schema-valid record. Do not infer commitment from parser success,
  file metadata, or an implementation-private sidecar.
- Under the exclusive writer lease, first validate the complete LF-terminated
  prefix. Preserve the complete pre-repair journal bytes in the sibling regular
  file `journal.recovery-<originalSha256>.bak`, where the digest is lowercase
  SHA-256 of those exact bytes. The backup is durable before journal mutation,
  is never overwritten, and an existing path is reusable only when its bytes
  match exactly.
- Truncate exactly to the last LF, then append one `extension` record whose
  namespace is `org.agent-session.recovery`. Its value has exactly
  `action:"truncate_uncommitted_tail"`, positive `discardedBytes`, the backup
  basename in `backupFile`, and matching `originalSha256`. Its journal `seq` is
  next and its parent is the selected head reconstructed after truncation.
- Invalid UTF-8, JSON, recursive duplicate keys, Schema, sequence, reference, or
  state in any LF-terminated line is complete corruption. Return
  `-32008`/`retryable:false`/`journal_corrupt` with journal bytes unchanged and
  without creating a recovery backup or diagnostic; never skip or truncate the
  bad complete line.
- Missing or hash-mismatched referenced artifacts make the affected record
  unavailable and require explicit repair; they are not silently dropped.

The exact recovery extension, backup, strict-corruption and clean-reopen
idempotence contract is frozen by
[ADR 0020](adr/0020-session-journal-tail-recovery.md). That ADR does not yet
claim crash-atomic recovery between backup durability, truncation, and diagnostic
durability; a stronger claim requires a separately frozen pending transaction and
fault-injection gate.

After structural validation, any accepted run lacking `run.terminal` receives a
durable interrupted terminal record. An open provider attempt is interrupted.
Ambiguous tool intents follow the no-replay rule in the agent specification.

Recovery is a deterministic append-only transition. Process unresolved tool
intents by original journal `seq`, then unresolved runs by their `run.accepted`
`seq`. For every unresolved non-idempotent or otherwise ambiguous tool intent:

1. do not invoke the tool and do not append another `tool.intent`;
2. append `tool.result` with `outcomeKnown:false`, `result.isError:true`, and
   `result.terminationReason:"internal_error"`;
3. append a canonical `message.appended` tool-result message with the same
   `toolCallId`, tool name, error state, and a safe unknown-outcome explanation.

Then append one `run.terminal` with status `interrupted` for each accepted run
without a terminal record, followed by its core `event.emitted` containing the
durable `run.interrupted` envelope. Recovery never creates a second terminal or
unknown-outcome result when reopened again. Only after all recovery appends are
durable may `session/get` return or a new `run/start` be accepted.

## Branches and continuation

`parentId` provides the immutable tree and may name any earlier record in the
same Session; `seq` remains the independent consecutive append order. An
ordinary append uses the current selected head as its parent and becomes the new
head. A `branch.selected` record is the sole core exception: its non-null parent
and `data.leafRecordId` are the same earlier target, and the selection record
itself becomes the head for subsequent appends. The target chain must be
quiescent and selection uses an expected-head compare. Appending to an earlier
record creates a branch and never removes a sibling.

The exact projection and mutation rules are frozen by
[ADR 0011](adr/0011-portable-session-branch-and-compaction.md). Context is the
unique parent chain to the selected head, interpreted with the newest valid
compaction and model/session metadata. Session event replay remains global and
monotonic rather than being filtered or renumbered by branch.

All five implementations MUST open, inspect, select a branch, recover, and
continue a conforming journal. They MUST preserve recognized extension objects
while round-tripping and MUST NOT rewrite a journal merely because formatting
differs.

The Provider context for a continuation is reconstructed from canonical
`message.appended` records on the selected branch, in parent-chain order, after
applying the newest applicable compaction. Event deltas are replay material and
never substitute for completed messages. The shared Session conformance fixture
contains completed history, an interrupted run, and an ambiguous
non-idempotent tool; its faux `expectedContext` is the normative black-box
assertion that another language both opened the journal and actually supplied
the recovered history to its Provider.

## Context compaction

Compaction never deletes or rewrites history. At a quiescent between-turn
boundary it first appends a canonical assistant text summary whose parent is the
selected source leaf, then appends `context.compacted` whose parent is that
summary record. The compaction data binds the source leaf, summary message ID, a
retained user-message record on the source ancestry, token estimates, and a
bounded strategy identifier. It becomes active only when the second record is
durable; a lone summary after a crash is an ordinary message and not a partial
compaction.

For the newest compaction on the selected chain, Provider messages are the
summary, then messages from the retained boundary through the source leaf, then
messages after the compaction through the selected head. The physical summary
record is not emitted twice. Older prefix messages and unselected sibling
messages remain in the journal but are excluded from Provider context. Invalid
references, an orphaning retained boundary, or a growing `tokensAfter` value are
journal incompatibilities and never trigger a silent full-history fallback.

## Migration

Migration is offline with an exclusive lock:

1. validate the source without mutation;
2. copy it to a uniquely named, read-only backup;
3. write and validate a complete new journal in a sibling temporary directory;
4. flush data and directories;
5. atomically replace when the platform supports it, otherwise leave both and
   require explicit user choice;
6. record source format, migrator implementation/version, and any loss.

A migration is never in-place line editing. Failure leaves the original and
backup readable.

## PI Session v3 importer

The normative clean-room mapping is [ADR 0012](adr/0012-pi-session-v3-clean-room-import.md).
The importer accepts PI v3 only as a one-time input format. It MUST NOT execute
PI code or tools, load PI extensions, contact a Provider, restore a run, modify
the source, or trust source paths. The complete accepted source bytes are
strictly validated and hashed before a different qxnm-forge Session ID is written.

Standard messages, model/thinking changes, compactions, branch summaries and
session metadata are mapped where their portable semantics are valid. A PI
branch jump is represented by an explicit `branch.selected`. A PI compaction
expands into a canonical assistant summary message followed by
`context.compacted`; it never invokes the original summarizer.

PI labels, custom entries, custom messages and unknown entry types enter the
`org.agentprotocol.pi-v3` provenance extension/report boundary. They are excluded
from Provider context unless a future explicit core conversion is separately
specified. In particular, an imported custom message is not silently promoted
to a user prompt.

The new header records `source=pi-session-v3`, source Session ID, SHA-256 of
the exact source bytes, report artifact ID, and reference commit
`3f9aa5d10b35223abf6146f960ff5cb5c68053ee`. It does not persist PI cwd,
parent-session or caller source paths. Every omission, redaction, quarantine,
extension-only mapping and lossy conversion is listed in the mandatory
`application/vnd.qxnm-forge.pi-v3-import-report+json` artifact. Imported
credentials or likely secrets MUST be redacted/quarantined rather than copied.

A successful imported journal contains no run/provider-attempt/tool-lifecycle,
approval, queue, recovery or event records. Historical tool calls and results
are inert canonical message content only and are never replayed.
