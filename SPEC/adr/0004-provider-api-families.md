# ADR 0004: Implement providers by API family

- Status: Accepted
- Date: 2026-07-10
- Project: qxnm-forge
- Author: 高宏顺 `<18272669457@163.com>`

## Context

The frozen v1 scope contains 35 text provider brands but only nine text wire
families, plus one image family. Copying HTTP/stream parsers for each brand
would multiply fragmentation, retry, redaction, and cancellation defects.

## Decision

Implement native transport logic once per API family in each language. A shared
manifest selects family, authentication, endpoint, model capabilities, and
fixture-backed compatibility flags. A brand-specific behavior must be an
explicit documented wire quirk, not duplicated general logic. Support is
tracked per capability, not as a provider Boolean.

The deterministic `faux` family is a conformance-only provider with no network
access and is outside the 35 live-provider count.

Transport adapters are tested against a shared Python-standard-library mock
bound to an ephemeral IPv4 loopback port. Machine-readable family cases drive
request-shape/auth-presence checks and deterministic HTTP/SSE fragmentation,
retry, timeout, cancellation, and disconnect behavior. A small set of common
conformance-only environment names supplies the exact local endpoint and
bounded timing policy. Authentication values are generated in memory and never
recorded; observations contain presence booleans only.

Every text-family request probe supplies the same non-empty portable
`file.read` definition. The fixture requires the family-native function-tool
shape, exact tool name, and input Schema field in the outbound request before
the response parser cases run. Merely accepting a tool-call response while
omitting tool definitions is therefore not family tool conformance. Full Agent
execution ordering and durable tool results remain governed by ADR 0006 and its
separate black-box suite.

The first-batch `partial_tool_arguments` case additionally crosses the real
Agent continuation boundary. Its first native response delivers one
`file.read({"path":"README.md"})` call in partial argument events. The Agent
must durably append the intent, execute the workspace read against the isolated
fixed `README.md`, durably append the exact successful text result and canonical
tool message, and only then expose
`tool.completed`. Exactly one second HTTP request must contain both the
original call and the correlated non-empty result in the family-native form:
Chat `assistant.tool_calls` followed by a `role:tool` message, Responses
`function_call` followed by `function_call_output`, or Anthropic assistant
`tool_use` followed by user `tool_result` with `is_error:false`. In all three
families the decoded result text must equal `provider tool continuation
fixture\n`; a merely non-empty or correlated value is insufficient. The second
mock response is a text response that completes the same run. The runner requires
the ordered `tool.requested -> tool.started -> tool.completed -> message.delta
-> run.completed` lifecycle, exactly two requests, and cumulative normalized
usage `14/10/24` from two `7/5/12` Provider responses.

The loopback mock records only fixed error labels, counts, and boolean request
shape summaries. It never retains an authentication value, prompt, tool-result
body, or complete HTTP request. Consequently, the continuation assertion proves
the native wire shape without turning the mock observation channel into a body
or credential log.

## Consequences

- Parser/security tests apply broadly and consistently.
- Manifests become governed inputs and require validation.
- Special gateways remain possible but must document and test their delta.
- “Can send text” cannot be advertised as full tools/reasoning/image/auth
  support.
- Static mock/parser tests do not raise capability status; each native adapter
  must pass the same opt-in black-box cases without contacting a live origin.
