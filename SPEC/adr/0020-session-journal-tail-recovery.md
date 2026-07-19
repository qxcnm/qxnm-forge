# ADR 0020: Session journal tail recovery and strict corruption boundary

- Status: Accepted
- Date: 2026-07-17
- Project: qxnm-forge
- Author: 高宏顺 `<18272669457@163.com>`

## Context

Portable Session v0.1 requires every committed journal record to end with LF, but
the previous text left two cross-language ambiguities. First, a syntactically valid
JSON value without LF could be accepted when an implementation believed it was
durable, even though v0.1 defines no portable durability witness. Second, the
recovery diagnostic and backup filename were not frozen, so five implementations
could repair the same bytes into incompatible persistent records.

Treating a complete corrupt LF-delimited line as a torn write is unsafe. It can
silently discard an attacker-controlled duplicate key, invalid UTF-8, invalid JSON,
Schema violation, or state-machine violation. Conversely, accepting a no-LF value
merely because a JSON parser accepts it invents a commit boundary that another
runtime cannot observe.

## Decision

### LF is the v0.1 commit witness

Session journal v0.1 defines no independent portable durability witness. A nonempty
suffix after the last LF is therefore uncommitted, including when that suffix is
strict UTF-8, valid JSON, and a valid journal record. A recoverable journal MUST
still contain a complete, valid LF-terminated prefix whose first line is its Session
header. Implementations MUST NOT infer commitment from JSON validity, file length,
mtime, an implementation-private sidecar, or a successful previous parse.

Before mutation, the runtime acquires the exclusive Session writer lease, rereads
the journal under that lease, and validates every LF-terminated line using strict
UTF-8, recursive duplicate-key rejection, finite JSON numbers, the public journal
Schema, and portable state invariants.

If any LF-terminated line is invalid, opening fails with
`code=-32008`, `retryable=false`, and `details.kind="journal_corrupt"`. The journal
bytes remain exactly unchanged and no new recovery backup or diagnostic is created.
This rule applies even when the invalid line is physically last.

### Frozen backup and recovery extension

For a recoverable nonempty no-LF suffix, let `originalBytes` be the complete journal
bytes observed under the writer lease, `committedBytes` be the prefix ending at the
last LF, and `originalSha256` be lowercase SHA-256 of `originalBytes`.

The runtime MUST durably create this sibling regular-file backup before changing the
journal:

```text
journal.recovery-<originalSha256>.bak
```

Its content is byte-for-byte `originalBytes`, including the uncommitted suffix. The
basename contains the full 64-character lowercase digest. The runtime MUST reject a
symlink or non-regular-file destination. If the content-addressed path already
exists, it may be reused only when its complete bytes equal `originalBytes`; a
mismatch fails closed without changing the journal. Recovery never overwrites a
backup.

After the backup is durable, the runtime removes exactly
`len(originalBytes)-len(committedBytes)` bytes, leaving `committedBytes`, and appends
one ordinary portable `extension` record. Its `seq` is the next consecutive journal
sequence and its `parentId` is the selected head reconstructed from
`committedBytes`; the new extension then becomes the selected head. Its event
sequence is unchanged. The record has this exact `data` shape:

```json
{
  "namespace": "org.agent-session.recovery",
  "value": {
    "action": "truncate_uncommitted_tail",
    "discardedBytes": 37,
    "backupFile": "journal.recovery-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.bak",
    "originalSha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
  }
}
```

`discardedBytes` is a positive integer and `backupFile` MUST equal
`journal.recovery-<originalSha256>.bak`. No implementation name, product name,
absolute path, workspace path, exception, or arbitrary diagnostic object is stored
in this extension. `recordId` and `time` remain ordinary implementation-generated
portable fields.

Only after the backup, truncation, extension append, and required file/directory
durability primitives succeed may open continue to semantic crash recovery or
return a successful `session/get` response.

### Idempotence and current crash-in-repair scope

A completed repair followed by a clean reopen MUST create no second backup, recovery
extension, unknown-outcome `tool.result`, canonical tool message, or run terminal.
Existing unknown reverse-DNS extensions stay byte-for-byte in the committed prefix.
Durable event sequences may contain gaps; semantic recovery allocates strictly above
the greatest durable event sequence, never from journal record count.

This ADR does not define a portable pending-repair sidecar and the v0.1 conformance
runner does not inject a process crash between backup durability, truncation, and
diagnostic durability. The backup MUST precede destructive mutation, and any
reported I/O failure remains fail closed. Crash-atomic resumption inside that narrow
repair transaction requires a later accepted contract and dedicated fault-injection
evidence; passing the present synchronous-open and clean-reopen cases does not prove
that stronger property.

### Common black-box cases

`journal-tail-recovery-cases.json` and `session_tail_runner.py` freeze six isolated
Session cases:

1. incomplete invalid JSON without LF is backed up, discarded, and diagnosed;
2. a complete Schema-valid journal record without LF receives the same treatment;
3. an LF-terminated invalid UTF-8 line is immutable corruption;
4. an LF-terminated invalid JSON line is immutable corruption;
5. an LF-terminated Schema-invalid line is immutable corruption;
6. an otherwise valid LF-terminated record with a recursively nested duplicate key
   is immutable corruption.

Each repair case opens twice. The runner verifies exact original backup bytes,
content-addressed basename, one recovery extension, retained unknown extensions,
event-sequence gap handling, one unknown-outcome tool recovery result, one terminal,
and byte-identical second reopen. Each corruption case opens twice and verifies the
same `journal_corrupt` response, unchanged journal bytes, and no recovery backup.

## Consequences

- LF is a language-neutral commit boundary; JSON parser permissiveness cannot create
  cross-runtime state.
- Complete corrupt records remain available for explicit forensic repair instead of
  being silently truncated.
- Recovery evidence has a deterministic, brand-neutral namespace and filename.
- Content-addressed backups make repeated clean recovery auditable without timestamp
  collisions.
- This ADR alone does not change capability status; an implementation may reach
  `conformant` only after the portable Session runner and this tail runner both pass.
- Crash injection inside the recovery transaction remains an explicit follow-up,
  rather than an unstated claim.
