# ADR 0011: Portable Session branch selection and context compaction

- Status: Accepted
- Date: 2026-07-14
- Project: qxnm-forge
- Author: 高宏顺 `<18272669457@163.com>`

## Context

The journal record already has an earlier-record `parentId`, and the v0.1 kind
set already reserves `branch.selected` and `context.compacted`. The five linear
implementations currently require every parent to be the immediately preceding
record and reject both reserved kinds. That is safe but cannot represent PI v3
branches, user branch switching, or a bounded Provider context without deleting
history.

Leaving the two records underspecified would be worse than leaving them
unsupported: implementations could disagree about whether selection itself is
on the old or new branch, whether events belong to branch context, whether a
summary replaces or supplements retained messages, and whether a later
implementation may safely continue after compaction.

## Decision

### One append order and one selected parent chain

Journal `seq` remains a single consecutive append order for durability,
recovery, diagnostics, and event allocation. `parentId` is a separate tree edge
and MAY reference any earlier record in the same Session. It never references a
future record and never crosses Session boundaries.

Writers maintain one selected head:

- before the first record, the selected head is null;
- an ordinary appended record MUST use the current selected head as `parentId`
  and then becomes the new selected head;
- `branch.selected` is the only core operation that may use another earlier
  record as its parent;
- its `data.leafRecordId` MUST equal its own non-null `parentId` and names the
  target that was selected;
- the `branch.selected` record itself becomes the new selected head, so the next
  ordinary append descends from the target through an auditable selection
  control node.

The latest valid append-order `branch.selected` therefore determines the active
branch without rewriting an old record. Selecting a branch is accepted only
when `expectedHeadRecordId` still equals the current selected head and the
target parent chain is quiescent: it contains no accepted run without a
terminal, unresolved tool outcome, or pending approval. A failed compare or
non-quiescent target appends nothing.

`session/get.messages` and Provider input use only `message.appended` records on
the selected parent chain after compaction projection. `session/get.events`
remains the Session-global durable event stream filtered by event `seq`; branch
selection never reuses, rewinds, or hides event sequence numbers.

### Durable compaction pair

Compaction is two ordinary append-only records on the selected branch:

1. a canonical assistant `message.appended` containing one nonempty text
   summary, with `finishReason: "stop"`, the summarizer Provider selection and
   usage; its parent is the selected source leaf;
2. `context.compacted`, whose parent is that summary-message record and whose
   data contains:
   - `sourceLeafRecordId`: the selected head before the summary;
   - `summaryMessageId`: the exact message ID in the preceding summary record;
   - `firstRetainedRecordId`: a user `message.appended` record on the source
     leaf's ancestor chain;
   - `tokensBefore`, optional `tokensAfter`, and a bounded versioned `strategy`.

The compaction becomes active only after the complete `context.compacted` record
is durable. A crash after the summary record but before the compaction record
leaves an ordinary assistant message on that branch but does not activate a
partial compaction. Automatic compaction runs only at a quiescent between-turn
boundary and uses an expected-head compare; it never runs with an active run,
pending approval, or unresolved tool.

For the newest `context.compacted` on the selected chain, Provider context is
constructed in this exact order:

1. the referenced summary message;
2. message records from `firstRetainedRecordId` through
   `sourceLeafRecordId`, in parent-chain order;
3. message records strictly after the compaction record through the selected
   head, in parent-chain order.

The summary record is not emitted a second time from its physical position.
Messages before `firstRetainedRecordId`, messages on unselected siblings, event
records, and control records are excluded. The retained boundary MUST be a user
message so a tool result or assistant tool call cannot be orphaned. All
referenced records and IDs are validated before projection. An invalid
compaction makes the journal incompatible/read-only; implementations never
silently fall back to full history.

When multiple compactions occur on one selected chain, the newest valid one
supersedes earlier projections. Its summary is required to carry everything the
new compactor intends to retain from the already-compacted prefix; continuing
an imported Session never invokes the original summarizer.

### Head compare and RPC results

`session/branch/select` requires `sessionId`, `expectedHeadRecordId`, and
`targetLeafRecordId`. It returns the target, the appended selection record ID,
and the new selected head.

`session/compact` requires `sessionId`, `expectedHeadRecordId`,
`firstRetainedRecordId`, a bounded summary text, summarizer Provider and usage,
token estimates, and strategy. It returns the summary message/record IDs, the
compaction record ID, and the new selected head. `tokensAfter` cannot exceed
`tokensBefore`.

Both operations acquire the cross-language writer lease, validate the complete
journal and compare before appending, and acknowledge only after required
records are flushed. The result IDs are opaque. Protocol and persistence
identifiers remain brand-neutral.

Mutation failures use one portable mapping so clients never inspect English
error text:

| Condition | Code | Retryable | `details.kind` |
| --- | ---: | :---: | --- |
| expected head no longer selected | `-32010` | true | `stale_session_head` |
| Session currently has an active run or mutation | `-32004` | true | `session_busy` |
| target is unknown or not an earlier record | `-32602` | false | `record_not_found` |
| target ancestry is not quiescent | `-32010` | false | `branch_not_quiescent` |
| retained boundary is invalid | `-32602` | false | `invalid_compaction_boundary` |
| `tokensAfter` exceeds `tokensBefore` | `-32602` | false | `invalid_compaction_tokens` |
| journal is corrupt or incompatible | `-32008` | false | `journal_corrupt` |

The non-quiescent historical target is non-retryable because retrying the same
immutable target cannot add terminal records to that target ancestry. A caller
may instead choose a later quiescent target. Validation order is journal
integrity, current busy state, expected-head compare, referenced-record checks,
then operation-specific invariants. Every failure above appends zero bytes.

`session/get` adds optional `selectedHeadRecordId` and
`compactionRecordId`. Older clients may ignore them; a server claiming branch or
compaction support returns them.

## Conformance

The common fixture contains an old prefix, an active compaction with a retained
recent pair, two sibling continuations, and a final switch back to the first
sibling. Static validation proves append sequence, tree edges, selected-head
transitions, compaction references, exclusions, and exact projected message
order. The black-box runner requires each implementation to open the fixture,
return only the selected projection, supply it to faux `expectedContext`, and
continue without rewriting the existing byte prefix.

Creation tests additionally cover stale expected heads, unknown/future targets,
non-quiescent targets, invalid retained boundaries, `tokensAfter` growth, crash
between summary and compaction, repeated selection, and a second compaction.
Only a full common runner result can advance branch or compaction capability to
`conformant`.

## Consequences

- Complete history and sibling branches remain durable and inspectable while
  Provider context is bounded.
- Journal append order remains simple, but implementations must stop assuming
  that `parentId` equals the immediately preceding record.
- Session-global event replay stays monotonic and independent of UI branch
  selection.
- A summary is portable data rather than a dependency on its generating model
  or implementation.
- Compaction is not deletion, redaction, or a security boundary; sensitive data
  remains in the immutable journal and follows the Session retention policy.
