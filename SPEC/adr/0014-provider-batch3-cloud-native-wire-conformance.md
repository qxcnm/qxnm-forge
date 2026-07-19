# ADR 0014: Freeze the third cloud-native Provider transport conformance batch

- Status: Accepted
- Date: 2026-07-14
- Project: qxnm-forge
- Author: 高宏顺 `<18272669457@163.com>`

## Context

The first two local transport suites cover six text API families. The remaining
three frozen text families cannot be represented by renaming one of those
adapters:

- Vertex uses OAuth bearer authentication and a project/location/publisher
  resource path around the Google GenerateContent document model;
- Bedrock ConverseStream uses AWS SigV4 request headers and binary AWS
  EventStream messages with two CRC32 boundaries, not SSE;
- Codex uses an OAuth bearer against the Codex Responses route, Responses native
  events, and adapter-owned non-secret client headers.

The canonical family, Provider, endpoint, and credential-source identities are
already frozen in `providers.v1.json` and `models.v1.json`. This ADR adds a
synthetic clean-room wire contract only. It does not run PI, an SDK, an OAuth
flow, an ambient cloud credential chain, or any real Provider. Public Codex
documentation was unavailable to this extraction environment, so the Codex
fixed headers below are deliberately scoped to the qxnm-forge adapter and local
conformance contract; they are not presented as independently live-verified
upstream requirements.

## Decision

### Independent suite and loopback-only credentials

Add `CONFORMANCE/fixtures/provider/mock-cases-batch3.json` with
`suiteId: batch3-native-v1`. It contains exactly seven lifecycle/fault cases for
each of `google-vertex`, `bedrock-converse-stream`, and
`openai-codex-responses`. Selecting it is explicit and does not change the
default first-batch runner entry point.

The endpoint, token, project, location, AWS key, region, and mock model values
are synthetic. They are accepted only while `QXNM_FORGE_PROVIDER_CONFORMANCE=1` is
active and the runner supplies an exact ephemeral `http://127.0.0.1` origin.
The runner removes inherited Provider, OAuth, cloud, endpoint, metadata, and
proxy configuration before starting the child. It sets dead proxies and
disables AWS metadata lookup. No redirect is followed.

The test-only credential injection names are:

| Family | Endpoint environment | Primary credential environment | Additional bounded configuration |
| --- | --- | --- | --- |
| `google-vertex` | `QXNM_FORGE_GOOGLE_VERTEX_ENDPOINT` | `QXNM_FORGE_GOOGLE_VERTEX_OAUTH_TOKEN` | `GOOGLE_CLOUD_PROJECT=mock-project`, `GOOGLE_CLOUD_LOCATION=us-central1` |
| `bedrock-converse-stream` | `QXNM_FORGE_BEDROCK_CONVERSE_STREAM_ENDPOINT` | `AWS_ACCESS_KEY_ID` | synthetic `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN`, `AWS_REGION=us-east-1`, metadata disabled |
| `openai-codex-responses` | `QXNM_FORGE_OPENAI_CODEX_RESPONSES_ENDPOINT` | `QXNM_FORGE_OPENAI_CODEX_OAUTH_TOKEN` | none |

The two `QXNM_FORGE_*_OAUTH_TOKEN` variables are conformance-only bearer injection
points, not production credential-store APIs. Production Vertex continues to
use its declared ADC/API-key profiles, and production Codex continues to use
its declared interactive OAuth profile.

### Frozen request targets and body boundaries

| Family | Request target after configured base | Authentication shape | Request core | Success framing |
| --- | --- | --- | --- | --- |
| `google-vertex` | `POST /v1/projects/mock-project/locations/us-central1/publishers/google/models/mock-vertex-v1:streamGenerateContent?alt=sse` | `Authorization: Bearer …` | `contents`; `functionDeclarations` with `parametersJsonSchema` | SSE containing `GenerateContentResponse`; `functionCall.args` is one complete object |
| `bedrock-converse-stream` | `POST /model/mock-bedrock-v1/converse-stream` | `Authorization` scheme `AWS4-HMAC-SHA256`; presence of `x-amz-date`, `x-amz-content-sha256`, and `x-amz-security-token` | `messages`; `toolConfig.tools[].toolSpec.inputSchema.json` | `application/vnd.amazon.eventstream` binary frames containing ConverseStream JSON events |
| `openai-codex-responses` | `POST /codex/responses` | `Authorization: Bearer …` | `model`, `input`, `stream:true`, `store:false`, Responses function tools | named Responses SSE events and partial function-argument strings |

Codex additionally sends the non-secret adapter headers
`OpenAI-Beta: responses=experimental` and `originator: qxnm-forge`. The mock validates
only their declared fixed prefix and stores only presence/prefix-valid booleans.
Account identifiers, OAuth claims, cookies, authorization values, and arbitrary
caller headers are neither required nor observed.

### AWS EventStream is a separate parser boundary

A Bedrock message is encoded as:

1. big-endian `totalByteLength` and `headersByteLength`;
2. CRC32 of those eight prelude bytes;
3. typed EventStream headers;
4. a UTF-8 JSON payload;
5. CRC32 of the complete message except the final CRC field.

The fixture uses `:message-type=event`, an exact `:event-type`, and
`:content-type=application/json`. Implementations MUST validate bounded lengths,
the prelude CRC, the message CRC, header types/lengths, UTF-8, and JSON before
dispatching an event. Unknown valid event types may be ignored only when the
family specification permits them; malformed lengths, CRCs, headers, UTF-8, or
JSON are structured interrupted/failed attempts and never become partial
portable output.

`chunkSizes` deliberately split the prelude, both CRCs, typed headers, UTF-8
code points, and JSON tokens. The disconnect case emits one complete frame and
then an incomplete next frame. Idle and cancellation cases emit one complete
frame and retain the connection. No `data:` line, SSE decoder, line-ending rule,
or EOF blank-line rule is used for Bedrock.

The suite checks only that a SigV4-shaped `Authorization` header and required
signing headers are present. It does not claim to cryptographically verify the
signature or exercise AWS credential-chain precedence; those require separate
credential and live-smoke gates.

### Native event normalization

Vertex reuses the Google GenerateContent response document model. Text parts
normalize to ordered `message.delta`; usage maps `promptTokenCount`,
`candidatesTokenCount`, and `totalTokenCount`. A Vertex tool request is a
complete `functionCall.args` object.

Bedrock normalizes `messageStart`, `contentBlockStart`,
`contentBlockDelta`, `contentBlockStop`, `messageStop`, and `metadata` events.
Text is taken from `delta.text`; partial tool JSON strings are taken from
`delta.toolUse.input` and assembled by content-block index; usage maps
`inputTokens`, `outputTokens`, and `totalTokens` from `metadata.usage`.

Codex consumes named Responses events. Text uses
`response.output_text.delta`; tool JSON uses
`response.function_call_arguments.delta`; terminal usage comes from
`response.completed`. A complete HTTP response without the required native
terminal event is not a successful completion.

### Shared failure and privacy requirements

Each family covers Unicode text plus usage, tool arguments, 429 with numeric
`Retry-After`, 503 with HTTP-date `Retry-After`, incomplete disconnect, idle
timeout, and cancellation. SSE families cover CRLF, multi-line fields,
arbitrary UTF-8/JSON fragmentation, and a complete final field line at EOF
without an additional blank line. Bedrock covers equivalent arbitrary binary
fragmentation and both CRC boundaries.

Mock observations contain only fixed identifiers, request counters, top-level
key names, array counts, validation labels, and header presence/scheme
booleans. They never contain a request target value, body, prompt, model value,
credential, signature, OAuth token, AWS key, environment value, or raw error
body. The runner scans protocol output, stderr, observations, journals, and
artifacts for the in-memory canary.

## Consequences

- Bedrock implementations need a real bounded AWS EventStream decoder and
  cannot pass by routing the response through an SSE parser.
- Vertex path construction is tested independently of ordinary Gemini API-key
  routing.
- Codex can share normalized Responses domain mapping while retaining its own
  OAuth, route, and fixed-header boundary.
- Passing fixture, mock, or fake-daemon tests is harness evidence only. No
  implementation or Provider capability becomes `implemented`, `conformant`,
  or `live-verified` until the corresponding native runtime passes the common
  black-box suite and any required live gate.
