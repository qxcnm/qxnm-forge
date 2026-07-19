# ADR 0003: Portable append-only JSONL session tree

- Status: Accepted
- Date: 2026-07-10
- Project: qxnm-forge
- Author: 高宏顺 `<18272669457@163.com>`

## Context

Sessions must survive crashes and be continued by any implementation. Mutable
language-native serialization makes partial writes, schema migration, branches,
and cross-language recovery difficult. Retrying ambiguous tools can duplicate
external side effects.

## Decision

Store a versioned append-only UTF-8 JSONL journal plus content-addressed
artifacts. Records have monotonic sequence, opaque ID, parent ID, UTC time,
kind, and strictly defined portable data. Accepted mutations are flushed before
acknowledgement. Branches append against earlier parents. Compaction is a
derivation, not deletion. Recovery marks nonterminal runs/provider streams
interrupted and never automatically replays ambiguous/non-idempotent tools.
Unknown outcomes are represented by a portable error `tool.result` and matching
canonical tool-result message before the interrupted terminal. Event replay is
derived only from core `event.emitted` records; its `event.seq` is independent
from journal `seq`.

Migration uses backup, new-file validation, and atomic replacement where
available. PI Session v3 import is one-way and preserves provenance without
modifying the source.

## Consequences

- History and crash decisions are auditable.
- Journals grow; artifacts and safe garbage collection manage large data.
- Writers require exclusive platform locks and real durability primitives.
- The portable `lock` path is a persistent advisory-lock target; file existence
  alone never means a writer is active. Runtimes without compatible advisory
  locking may use a namespaced conservative claim sidecar without deleting or
  colliding with another implementation's metadata, but cannot claim
  cross-language concurrent-writer conformance from that fallback.
- Every language must implement the same validation and state reconstruction.
- Cross-language conformance uses a shared pre/post-recovery journal pair and a
  faux context assertion, so merely parsing the file is not sufficient to claim
  continuation support.
