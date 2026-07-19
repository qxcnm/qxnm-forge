# ADR 0005: Fail-closed permissions and precise sandbox claims

- Status: Accepted
- Date: 2026-07-10
- Project: qxnm-forge
- Author: 高宏顺 `<18272669457@163.com>`

## Context

Model/tool inputs are untrusted. Workspace path checks do not contain arbitrary
executed code, lexical prefix checks permit symlink/junction escapes, and
terminating one process leaves descendants alive. Calling these checks a
sandbox would create a dangerous false assurance.

## Decision

Allow bounded workspace read/list/search by default. Ask interactively for
writes and execution; deny dangerous operations in headless mode without an
explicit policy. Deny workspace escape. Resolve filesystem access with
platform-aware no-follow/handle checks. Execute Unix children in process
groups and Windows children in kill-on-close Job Objects, terminating the whole
tree on cancellation. Only a named, tested container/VM/OS isolation backend may
be advertised as a hard sandbox.

Approvals bind to normalized operation hashes and are durable before execution.
Secrets never enter prompts, journals, fixtures, normal logs, or protocol error
text.

## Consequences

- Some platform operations may be unsupported until safely implemented.
- Interactive use has visible approval friction; headless automation needs an
  explicit policy.
- Security capability reporting distinguishes policy restriction, process
  constraint, and hard sandboxing.
