# PI Session v3 clean-room import fixture

This fixture is entirely synthetic. It contains no real prompt, repository
path, credential, tool output or Provider response. It was derived from the
documented types and static behavior of the read-only MIT reference snapshot at
`3f9aa5d10b35223abf6146f960ff5cb5c68053ee`; no PI code is executed.

Files:

- `source.pi-v3.jsonl`: strict synthetic PI v3 source with two branches,
  messages, model/thinking changes, compaction, custom/custom-message, label,
  session metadata and one unknown future entry.
- `expected.journal.jsonl`: deterministic qxnm-forge v0.1 clean-room projection.
- `artifacts/import-report.json`: mandatory loss/quarantine/provenance report.
- `expectations.json`: machine mapping, counts, selected context and forbidden
  lifecycle records.

The fixture IDs and timestamps are deterministic test values. Production
importers generate a fresh opaque Session ID and record IDs; byte-for-byte ID
agreement is not required outside conformance mode.

Author: 高宏顺 `<18272669457@163.com>`
