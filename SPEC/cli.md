# qxnm-forge CLI and daemon behavior v0.1

## Executables and modes

Each implementation provides one native executable with an implementation-
idiomatic package name and these logical modes:

- `agent` (default): pure-text Coding Agent interaction;
- `run`: one prompt, streaming text to the terminal, then exit;
- `daemon --stdio`: headless JSON-RPC NDJSON;
- `models`: list configured models/capabilities without exposing credentials;
- `sponsors`: publish/list the signed promotion catalog and explicitly pin/remove local routes;
- `auth`: set/list/remove worktree-external Provider credentials; `set` reads the secret only from stdin;
- `session list|show|continue|import-pi-v3`;
- `doctor`: read-only toolchain/config diagnostics.

Exact flag spelling MAY follow ecosystem conventions only where not described
here. Cross-language automation SHOULD use daemon RPC. CLI scripts rely on the
portable flags below.

## Portable flags and precedence

The portable set is:

```text
--workspace <path>
--state-dir <path>
--session <opaque-id>
--provider <id>
--model <id>
--policy <path>
--thinking <off|minimal|low|medium|high|xhigh>
--format <text|json>
--no-color
--log-level <error|warn|info|debug|trace>
--conformance
```

Precedence is explicit CLI flag, session selection where applicable, workspace
config, user config, provider-specific environment, built-in default. A secret
MUST NOT be accepted directly in command-line arguments because process listings
and shell history expose it. Config diagnostics report source names, not values.

`--workspace` is canonicalized and must exist. The CLI never silently changes
to a parent repository. `--session` that belongs to a different workspace
requires explicit confirmation interactively and is denied headlessly unless
policy permits it.

`--state-dir` selects the caller-authorized Session storage root for CLI and
conformance automation. It never grants model tools access to that directory.
Normal defaults remain platform user-state directories outside the workspace.

`--conformance` gates deterministic test methods such as `faux/configure`; it
MUST NOT enable permissive tool policy or disable TLS/path checks.

## PI Session v3 import command

The portable one-way import form is:

```text
session import-pi-v3 --source <regular-jsonl-file> --workspace <path>
                     --state-dir <path> [--session <new-opaque-id>]
                     [--format <text|json>] [--conformance]
```

`--source`, `--workspace`, and `--state-dir` are required. The source is opened
read-only and its path is never written to the journal, report, diagnostics, or
machine output. `--session` chooses the new target ID; it MUST differ from the
PI header ID and MUST NOT already exist. Without it, the implementation
generates a fresh opaque ID. The command never overwrites, resumes, or merges a
target Session.

On `--format json`, successful stdout is exactly one UTF-8 JSON object followed
by LF:

```json
{"status":"completed_with_warnings","sessionId":"session-new","reportArtifactId":"artifact-report"}
```

`status` is `completed` or `completed_with_warnings` and must equal the report.
Text mode may render the same three safe fields for a person. Neither mode
prints the source path, target filesystem path, source contents, quarantined
values, credentials, stack traces, or PI cwd. Failure uses the portable exit
table and leaves no published target.

In explicit conformance mode, only the bundled synthetic fixture may request
its deterministic Session/record/artifact IDs and placeholder workspace value.
This makes cross-language byte-for-byte fixture comparison possible; it does
not relax strict parsing, path/privacy checks, source immutability, atomic
publication, or secret scanning. Production imports always generate fresh
record/artifact IDs even when the caller chooses the target Session ID.

## Text mode

The default interactive mode uses stdin for user lines and stdout for assistant
text. Status, diagnostics, approval prompts, and tool summaries go to stderr so
piped assistant output stays usable. It supports multi-line input through an
explicit delimiter or editor option; it does not infer shell commands from user
text.

Approvals display normalized structured operation, affected resources, risk,
and bounded choices. EOF while an approval is pending denies it. Ctrl-C once
requests cancellation and waits for cleanup; a second Ctrl-C MAY request a
forced local shutdown but MUST still attempt full process-tree termination and
journal recovery. Ctrl-D/EOF with no active run exits cleanly.

In `run` noninteractive mode, dangerous tools are denied unless `--policy`
explicitly grants them. The final assistant text is stdout. Diagnostics and
tool logs remain stderr. With `--format json`, stdout is one documented result
object and must not contain progress text; daemon mode is preferred for streams.

## Daemon mode

`daemon --stdio` owns stdin/stdout exclusively for protocol frames. It MUST NOT
print a banner. TTY detection does not change framing. Logs are stderr; normal
log level SHOULD be `warn`. Broken stdout causes cancellation/recovery of owned
runs according to controller-disconnect policy and a nonzero exit.

The daemon handles signals as follows:

- graceful termination stops new requests, cancels or drains active runs within
  a configured bound, flushes journals, cleans process trees, and exits;
- immediate/second termination still attempts cleanup but may cause recovery to
  append `run.interrupted` on next open;
- reload signals MUST NOT replace active policy/config without a versioned
  operation and are ignored in v0.1.

Future socket/named-pipe/WebSocket modes reuse JSON-RPC semantics but must add
peer authentication. A UI is a client; it does not read session files, store
provider keys, or execute tools itself.

## Exit codes

Portable meanings are:

| Code | Meaning |
| ---: | --- |
| 0 | requested operation completed |
| 2 | CLI usage/configuration error |
| 3 | permission/policy denial |
| 4 | provider/model/auth unavailable |
| 5 | run failed |
| 6 | run cancelled/interrupted |
| 7 | corrupt/incompatible session |
| 8 | internal daemon/executor failure |

Provider HTTP statuses are not process exit codes. In interactive mode a failed
individual run need not exit the whole CLI; when the process does exit because
of that run, it uses the table.

## Output and accessibility

Text output is UTF-8. Color is disabled when stdout is not a TTY, when
`NO_COLOR` is set, or with `--no-color`. Machine-readable formats never contain
ANSI. Untrusted control characters are escaped. Output truncation and artifact
handles are explicit. Progress indicators do not rely solely on color.

## Configuration safety

Unknown keys are errors in project policy/config. The CLI displays the file and
field but not adjacent secret values. Project-controlled config cannot grant
itself additional permission; user/admin policy is stored outside the
workspace. Plugins/extensions are outside v0.1 and cannot be auto-loaded from an
untrusted repository.
