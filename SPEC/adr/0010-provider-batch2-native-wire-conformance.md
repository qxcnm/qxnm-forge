# ADR 0010: Freeze the second native Provider transport conformance batch

- Status: Accepted
- Date: 2026-07-14
- Project: qxnm-forge
- Author: 高宏顺 `<18272669457@163.com>`

## Context

The first local transport suite characterizes OpenAI Chat, OpenAI Responses,
and Anthropic Messages. It cannot prove the three other API families in this
batch because brand identity is not a wire adapter: Mistral has its own SDK
operation and tool-call constraints, Azure changes the Responses origin/auth
contract, and Google has a different request/response document model.

The one-time evidence remains the immutable PI commit
`3f9aa5d10b35223abf6146f960ff5cb5c68053ee`. The relevant adapter files are:

- `PI/packages/ai/src/api/mistral-conversations.ts` with
  `@mistralai/mistralai` 2.2.6;
- `PI/packages/ai/src/api/azure-openai-responses.ts` with `openai` 6.26.0;
- `PI/packages/ai/src/api/google-generative-ai.ts` and
  `google-shared.ts` with `@google/genai` 1.52.0;
- their Provider factories and `PI/package-lock.json` for base URL,
  credential source, operation, and exact dependency evidence.

The extraction read that checkout only. It did not execute PI, contact a
Provider, inspect a credential, or make PI or an SDK a qxnm-forge dependency.

## Decision

### Independent suite

Keep the original 21 cases unchanged in `mock-cases.json`. Add the independent
`batch2-native-v1` suite in `mock-cases-batch2.json`, using the same Draft
2020-12 Schema and seven fault/lifecycle cases per family. Selecting the second
fixture is explicit; passing it does not imply passing the first suite and does
not update the capability matrix.

Fixture `id`, mock model IDs, endpoint environment names, loopback paths, and
canary credentials are synthetic conformance identifiers. They MUST be
accepted only when explicit Provider conformance mode is active. Canonical
`apiFamily` and `providerId` retain their normal manifest meaning, but a mock
probe is not provider-level or live support evidence.

### Frozen request and stream boundaries

| API family | Request target after configured base | Authentication | Request core | Stream payload |
| --- | --- | --- | --- | --- |
| `mistral-conversations` | `POST /v1/chat/completions` | `Authorization: Bearer …` | `model`, `messages`, `stream:true`, Mistral function tools | SSE `data` containing Chat completion chunks; tool `function.arguments` may arrive as JSON string fragments; `[DONE]` terminates a normal stream |
| `azure-openai-responses` | `POST /responses?api-version=v1` | `api-key: …` | deployment name in `model`, `input`, `stream:true`, `store:false`, Responses function tools | named OpenAI Responses SSE events; `response.function_call_arguments.delta` carries JSON string fragments and `response.completed` carries usage |
| `google-generative-ai` | `POST /models/{model}:streamGenerateContent?alt=sse` under the configured `/v1beta` base | `x-goog-api-key: …` | `contents` and `functionDeclarations` using `parametersJsonSchema`; model and stream selection are in the target | SSE `data` containing `GenerateContentResponse`; text parts are streamed, while one `functionCall.args` is a complete JSON object rather than a fabricated partial-argument event |

The Google family therefore is not modeled as arbitrary chunked JSON. Its
REST streaming operation uses SSE because `alt=sse` is part of the native
request target. HTTP byte chunks may split any UTF-8 code point or JSON token,
including inside the complete Google argument object; this transport
fragmentation MUST NOT be reinterpreted as a Provider-level partial tool event.

All three request fixtures include a nonempty `file.read` tool declaration.
The response fixture proves Mistral and Azure partial-string reassembly and
Google complete-object normalization. A tool request is intentionally denied
by the headless test policy after normalization, so this batch tests transport
and argument assembly, not the later approved tool-result continuation loop.

### Shared failure and parser requirements

Each family has exactly these cases: successful Unicode text, tool arguments,
429 plus numeric `Retry-After`, 503 plus HTTP-date `Retry-After`, incomplete
disconnect, idle timeout, and cancellation. The generated streams exercise
CRLF, arbitrary UTF-8/JSON byte boundaries, and a final complete SSE field line
at EOF without another blank line. A field line that itself lacks CR or LF is
still an interrupted fragment and MUST NOT be dispatched.

The mock validates the exact second-batch path/query and required body shape.
It records only fixed error labels, presence booleans, top-level key names, and
array counts. It never records request-target values, request bodies, prompts,
model values, environment values, or authentication header values.

The runner removes inherited Provider/cloud credentials and all six suite
endpoint names before every child process. It creates one fresh in-memory
canary, injects it only into the selected credential name, forces the exact
ephemeral `127.0.0.1` origin with dead proxies, disables redirects by contract,
and scans protocol output, stderr, journals, artifacts, and observations for
the canary.

## Consequences

- Implementations have a common no-network target before any live smoke test.
- Mistral and Google cannot be implemented by relabeling an OpenAI Provider.
- Azure can reuse normalized Responses domain semantics, but its base URL,
  query, and authentication remain an independent adapter boundary.
- Google tools must preserve complete-object semantics while transport parsers
  remain safe under arbitrary byte fragmentation.
- No capability becomes `implemented`, `conformant`, or `live-verified` merely
  because the Schema, mock, runner, or fake daemon self-test passes.
