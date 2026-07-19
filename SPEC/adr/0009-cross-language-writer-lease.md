# ADR 0009: Cross-language Session writer lease

- Status: Accepted
- Date: 2026-07-14
- Project: qxnm-forge
- Author: 高宏顺 `<18272669457@163.com>`

## Context

The persistent `lock` file is already the common advisory-lock target, but the
five standard libraries do not expose one interoperable primitive. Rust, Go,
and Python can use Unix `flock`; .NET file sharing has platform-dependent
interaction with it; Node exposes no portable advisory lock without a native
addon. An implementation-namespaced `O_EXCL` claim prevents only same-runtime
writers and leaves a stale file after a crash. Neither is sufficient evidence
for cross-language concurrent-writer conformance.

Using FFI, another language's helper executable, or a shared native lock binary
would violate the five-native-implementations boundary. Treating a timestamp as
a lease would be unsafe when a process is paused or clocks move. Deleting a
claim merely because its PID is absent is vulnerable to PID reuse and differs
across the three target platforms.

## Decision

### Canonical lease resources

The portable writer lease v1 uses both resources below in the Session
directory:

```text
lock                         persistent advisory-lock target
writer.lock.d/               atomically created portable ownership directory
  owner.json                 bounded metadata conforming to writer-lock schema
```

Every implementation MUST respect `writer.lock.d`, including implementations
that also acquire an OS advisory lock on `lock`. Advisory locking remains a
defence in depth and fast same-platform conflict signal; it is not by itself a
five-language claim.

The portable liveness witness is a TCP listener bound before acquisition to
the literal IPv4 loopback address `127.0.0.1` and an OS-assigned port. It never
binds a wildcard, accepts a configured host, traverses a proxy, or exposes a
Provider/network permission. The owner holds the listener until all Session
writes have stopped and ownership has been atomically relinquished.

`owner.json` contains only protocol version, Session ID, a 256-bit random
lowercase hexadecimal token, `127.0.0.1`, port, process ID, UTC creation time,
and implementation identity. It contains no workspace path, command line,
environment, credential, prompt, or hostname. The token is an ownership nonce,
not an authentication credential, and still MUST NOT be reused across leases.

### Acquisition

An acquirer performs these steps in order:

1. validate the Session ID and canonical Session directory;
2. create and start a `127.0.0.1:0` listener;
3. generate a fresh token;
4. atomically create `writer.lock.d` with create-directory semantics;
5. if creation loses, close its listener and execute the conflict/stale flow;
6. if creation wins, write a same-directory temporary metadata file, flush it,
   atomically rename it to `owner.json`, and flush the directory;
7. acquire the platform advisory lock on `lock` where the runtime exposes a
   compatible primitive;
8. only then read, recover, append, or acknowledge Session state.

Failure after step 4 removes the directory only when the on-disk token still
matches the acquirer's token. Uncertain cleanup fails closed.

### Conflict and stale-owner recovery

When `writer.lock.d` already exists, an implementation reads `owner.json`
without following links and applies strict size, UTF-8, duplicate-key, Schema,
Session-ID, token, and literal-host validation.

- Missing or incomplete metadata younger than the two-second initialization
  grace is `initializing`; the contender waits within its bounded acquisition
  timeout and retries.
- Valid metadata is probed only at its literal `127.0.0.1` port. A successful
  local TCP connection proves a live or conservatively indistinguishable
  owner, so acquisition returns retryable `session_locked` immediately. A
  response is not required; an event loop temporarily busy after `listen()`
  cannot be mistaken for a dead owner.
- Timeout, permission failure, or any ambiguous socket outcome is also treated
  as live and returns `session_locked`.
- Only an explicit local connection-refused/no-listener result makes the owner
  a stale candidate. Invalid external hosts are never contacted and produce
  `writer_lock_invalid`, not stale takeover.
- Metadata still missing after the initialization grace is a stale candidate.

A stale contender atomically renames `writer.lock.d` to a unique sibling
`writer.stale.<new-token>`. Exactly one contender can win that rename. The
winner revalidates that the moved metadata is the same stale candidate, removes
the moved directory, and restarts acquisition from listener creation. A loser
restarts without deleting anything. No process breaks the persistent `lock`
file or another implementation's namespaced fallback.

A port reused by an unrelated listener after an owner crash may conservatively
cause `session_locked`; this is a safe liveness failure and may require explicit
repair. It can never authorize concurrent writers.

### Release and crash behavior

Clean release first stops and joins all Provider/tool/append work. While the
liveness listener is still open, it verifies the exact token in `owner.json`
and atomically renames `writer.lock.d` to a unique release sibling. It then
releases the advisory lock, closes the listener, and deletes only the renamed
directory it owns. A token mismatch leaves all files untouched and reports a
safe cleanup failure.

On process termination, the OS closes the listener and any advisory lock. The
ownership directory remains, but the next implementation can prove the absence
of the listener and perform the atomic stale takeover. Thus a crash does not
require guessing wall-clock expiry and does not permanently strand an
implementation-specific claim.

### Capability and conformance

An implementation advertises `session.cross_language_writer_lock` only after
the common runner has exercised it as both holder and contender against every
other implementation on that platform, including simultaneous stale takeover.
Merely creating `lock`, opening with exclusive share flags, holding `flock`, or
using a namespaced `O_EXCL` claim remains `implemented` at most.

Hard sandboxes that deny loopback listen MUST fail closed or disclose the lock
capability as unavailable. They do not silently downgrade to an unsafe claim.
Windows, macOS, and Linux results are recorded separately.

## Consequences

- The protocol is implementable using each target language's standard network
  and filesystem APIs; it needs no FFI or shared binary.
- Live ownership has an OS-managed liveness witness, while stale takeover is an
  atomic filesystem race with one winner.
- The design prefers false `session_locked` over concurrent append.
- Session opening now has an internal loopback listener cost. It is not a
  daemon transport and is never advertised as a client endpoint.
- Existing journals remain compatible because auxiliary lock resources do not
  participate in transcript semantics.
