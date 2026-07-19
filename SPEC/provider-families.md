# qxnm-forge provider and model family contract

The frozen provider list is machine-readable in
[providers.v1.json](providers.v1.json); its frozen model evidence is in
[models.v1.json](models.v1.json). This document defines how they are used.

## Provider manifest v0.2 and frozen catalog

The manifest contains 35 canonical Provider identities and 45 routes across
nine text API families plus `openrouter-images`. Each route references exactly
one family adapter, the `pi-v1-frozen` catalog, one or more named auth profiles,
and one named header policy. Environment entries are names and sensitivity
roles only; values are runtime state and MUST NOT enter either shared file.

`models.v1.json` is a mechanically normalized evidence snapshot from PI commit
`3f9aa5d10b35223abf6146f960ff5cb5c68053ee`: 1,041 text rows under all 35
Provider identities and 35 OpenRouter image rows. Its recorded identity,
family, endpoint, input/output/reasoning capability, limits, fixed non-secret
headers, and compatibility values are observations, not support claims. A
runtime MUST filter `models/list` by configured route, usable authentication,
implemented capability, and matrix status.

Cross-document conformance requires route groups, adapter/catalog/header/auth
references, endpoint template variables, fixed-header policy, compatibility
quirks, and per-source counts to agree exactly. A catalog base URL is fixed
HTTPS, bounded HTTPS template, or `runtime-required`; the last form is used for
the PI Azure rows whose source base URL is empty. Credential, origin, template,
and redirect rules are normative in
[ADR 0008](adr/0008-provider-credential-origin-and-redirect-policy.md).
The catalog records digest covers every normalized model row, and the manifest
digest covers all fields except its two digest fields. Both use canonical UTF-8
JSON and are recomputed by the offline semantic validator; this prevents a
valid-looking auth, endpoint, capability, or quirk edit from bypassing the
frozen evidence review merely by preserving counts.

## Manifest-driven runtime advertisement

The frozen manifest is an allowlist, not an advertisement. For every process
configuration, a live route is usable exactly when all of these predicates are
true at the same time:

1. the exact `(providerId, apiFamily)` route exists in
   `providers.v1.json` and its referenced catalog rows exist;
2. at least one auth profile named by that route resolves to usable
   authentication without copying credential material into a DTO;
3. every required endpoint/template input is explicitly configured and valid;
4. the referenced `adapterId` is implemented by that native runtime; and
5. the capability policy permits the route. Public policy permits a live route
   only after its mandatory Provider/API-family cells are `conformant` or
   `live-verified`; a conformance-only synthetic allowance is test evidence,
   not a matrix update or support claim.

The resulting set is the intersection of those five predicates. Failure of
any one predicate removes the whole route before `initialize` and
`models/list` are materialized. A sibling route under the same Provider is
evaluated independently. An auth profile list is an OR: one usable listed
profile is sufficient. Within endpoint configuration, every template binding
must resolve from one of its listed environment names; when
`runtimeEndpointEnv` is nonempty at least one listed alternative must be
configured. A `runtime-required` catalog row never becomes usable from a
credential alone.

Advertisement is a local, side-effect-free snapshot. `initialize` and
`models/list` MUST NOT make a Provider request, resolve Provider DNS, contact a
cloud metadata/credential endpoint, start an OAuth browser flow, refresh a
token, or probe an endpoint. An auth path that would require such I/O is not
usable for that snapshot until an independently authorized credential flow has
already produced locally available state. Merely listing a metadata endpoint
or OAuth profile in the manifest never makes the route usable.

For the frozen v1 catalog, `models/list` contains exactly the catalog rows whose
`(providerId, apiFamily)` belongs to the usable set. Descriptor identity is the
triple `(providerId, modelId, apiFamily)`. `displayName`, media capabilities,
reasoning evidence, and bounded limits are normalized from the catalog, then
restricted by capabilities the runtime may truthfully advertise; catalog
evidence never upgrades an unsupported runtime feature. Stable output order is
`providerId`, then `modelId`, then `apiFamily`. `initialize` groups this same
snapshot by Provider and publishes a sorted, duplicate-free model-ID union.

The descriptor projection is deterministic. `displayName` is catalog `name`;
catalog `input`/`output` arrays retain their relative order after unsupported
media are removed; a text row has text output; `streaming` and `tools` reflect
the allowed native route features; and `reasoning` is true only when both the
catalog row and route capability allow it. Catalog `limits.contextWindow` and
`limits.maxOutputTokens` become `capabilities.contextTokens` and
`capabilities.maxOutputTokens`. Image routes remain non-streaming and do not
advertise tools or reasoning. A projection that would leave a model without
input or output media is not usable and MUST be omitted rather than emitting an
invalid descriptor.

OpenRouter deliberately proves why the triple matters. The frozen text and
image routes share two `(providerId, modelId)` pairs. Both descriptors remain
in `models/list`, the Provider-level `initialize.models` union contains each ID
once, and `run/start` still requires explicit `apiFamily` for either ambiguous
pair. No implementation may collapse the descriptors or guess the route.

The common black-box gate supplies only synthetic presence decisions for auth,
endpoint, adapter, and capability predicates. It removes inherited Provider
and cloud configuration, replaces proxies with a dead loopback proxy whose
`NO_PROXY` contains only literal loopback, uses a temporary home, performs no
Provider request, and checks that a fresh canary never enters stdout, stderr,
protocol DTOs, or a checked-in fixture. Daemon argv, total runtime, captured
stdout/stderr, reader queues, and cleanup are bounded; timeout/output-limit
paths terminate the runner-owned process group on POSIX and the directly owned
process on other platforms. The presence configuration is conformance-only and
MUST NOT be interpreted as a production credential store. Unknown fields and
duplicate JSON keys fail closed. Passing this gate alone does not change
`capabilities.json` or create a public Provider claim.

## 首批 canonical Provider Route Spine

[ADR 0018](adr/0018-manifest-driven-provider-route-spine.md) 把 identity-only
广告推进到一个受限的可执行纵切面，但没有放开全部 manifest。首批 executable snapshot
只包含以下六个精确 `(providerId, apiFamily)` key；其 catalog allowlist 合计 133 个模型：

| Provider | API family | catalog 模型数 | 生产 credential 环境 |
| --- | --- | ---: | --- |
| `groq` | `openai-completions` | 7 | `GROQ_API_KEY` |
| `minimax` | `anthropic-messages` | 3 | `MINIMAX_API_KEY` |
| `mistral` | `mistral-conversations` | 30 | `MISTRAL_API_KEY` |
| `openai` | `openai-responses` | 42 | `OPENAI_API_KEY` |
| `google` | `google-generative-ai` | 16 | `GEMINI_API_KEY` |
| `openrouter` | `openrouter-images` | 35 | `OPENROUTER_API_KEY` |

这六条 route 必须同时满足空 `quirks`、`api-family-default` header policy、唯一
`api-key` auth profile 和同 route 固定 HTTPS catalog base。广告、descriptor、执行
解析与 adapter 注册来自同一个启动期不可变 snapshot；catalog 外 model、wrong family、
缺 credential 或空 credential 都必须在创建 Session 或追加 durable record 前返回
`-32005/provider_unavailable`。省略 `apiFamily` 只在当前 snapshot 中
`providerId + modelId` 恰好唯一时允许，不能按注册顺序猜测。

Registry 与 route plan 只保存 manifest 固定的 credential 环境名称，不保存值。值只在
最终 HTTP authentication header 构造边界读取，因此合法轮换从下一次请求生效；值不得进入
descriptor、协议、Session、审批、日志、错误或 fixture。文本 route 只投影
`authentication/text/streaming/tools` 且 `reasoning:false`；图像 route 只投影
`authentication/text/image_input/image_output`，并固定 `streaming:false`、
`tools:false`、`reasoning:false`。catalog 中更宽的 image/reasoning 证据不能放大 runtime
能力。

`AGENT_PROVIDER_ROUTE_CONFORMANCE_CONFIG` 不是生产配置。只有 daemon 的一般
conformance 开关与 `QXNM_FORGE_PROVIDER_CONFORMANCE=1` 同时启用时，runtime 才可严格读取
该五字段文件，并且 `endpointBase` 只能是 runner 分配的 literal
`http://127.0.0.1:<port>/...`。配置只能把上述 snapshot 收窄到一个 catalog 模型，不能
改变 credential 名、adapter、header 或 quirk；生产模式看见该 presence 时必须在读取文件
或 credential 前 fail closed。它与 ADR 0017 identity presence 互斥。

其余 39 条 manifest route 继续 fail closed。共享 family adapter、catalog row 或旧 family
mock 通过都不能使这些 route 变为生产可执行。`provider_route_runner.py` 的六个回环正例、
九个负例、四项普通生产门禁及 Groq dedicated golden 都只使用 synthetic credential 和
本机 mock/network tripwire；通过表示
offline `conformant` 证据，绝不等于真实 Provider `live-verified`。

任何能力矩阵提升都必须落到精确 `(providerId, apiFamily, feature)` cell；不得把六条
route 折叠为 Provider 品牌级 claim，也不得把 sibling family 的证据合并。首批 claim
严格限定 Linux offline loopback；缺少原子 no-follow 的平台 fail closed 且不在范围。

## Family adapter responsibilities

Each of the nine text family adapters and the image family adapter owns native:

- request and response mapping;
- HTTP status and structured error mapping;
- SSE or WebSocket framing where applicable;
- arbitrary UTF-8 and JSON chunk reassembly;
- incremental tool-argument assembly and final schema validation;
- cancellation, connect timeout, idle timeout, total timeout, and retry hints;
- redacted diagnostics and normalized usage;
- family-specific authentication injection, without logging credentials.

Provider entries supply endpoints, auth strategy, and documented quirks. They
MUST NOT cause duplicated general HTTP logic. Redirects MUST be constrained by
the credential policy: authorization headers and cloud credentials MUST NOT be
forwarded to a different origin unless that transition is explicitly declared
and tested.

## Streaming invariants

Parsers MUST handle CRLF, multi-line SSE `data` fields, comments/heartbeats,
arbitrary byte boundaries within UTF-8 code points, arbitrary boundaries within
JSON tokens, partial tool arguments, a final record without an extra blank
line, 429/5xx responses, `Retry-After` in seconds or HTTP-date form, disconnect,
idle timeout, and local cancellation.

A provider attempt produces either one normalized successful completion or one
structured failure. Raw provider chunks are never exposed as portable domain
objects. Provider-specific opaque continuation values may use named core fields
when specified or an approved extension namespace.

## Local provider transport conformance

The first transport suite covers canonical families `openai-completions`,
`openai-responses`, and `anthropic-messages`. Its synthetic Provider IDs are
respectively `openai-compatible`, `openai-responses`, and `anthropic`; these
identify adapter probes and MUST NOT be interpreted as provider-level support.
An implementation started with `QXNM_FORGE_PROVIDER_CONFORMANCE=1`
MAY accept an `http://127.0.0.1:<ephemeral-port>/...` endpoint supplied by the
test runner. This exception is limited to the exact loopback origin allocated
by that process, never follows redirects, and grants no general tool-network
permission. Outside explicit conformance mode, normal endpoint/TLS policy is
unchanged.

The black-box runner uses these common environment names:

| Canonical family | Synthetic Provider ID | Endpoint | Synthetic credential |
| --- | --- | --- | --- |
| `openai-completions` | `openai-compatible` | `QXNM_FORGE_OPENAI_COMPATIBLE_ENDPOINT` | `OPENAI_API_KEY` |
| `openai-responses` | `openai-responses` | `QXNM_FORGE_OPENAI_RESPONSES_ENDPOINT` | `OPENAI_API_KEY` |
| `anthropic-messages` | `anthropic` | `QXNM_FORGE_ANTHROPIC_ENDPOINT` | `ANTHROPIC_API_KEY` |

It also sets bounded test-only transport policy:

- `QXNM_FORGE_PROVIDER_CONNECT_TIMEOUT_MS`;
- `QXNM_FORGE_PROVIDER_IDLE_TIMEOUT_MS`;
- `QXNM_FORGE_PROVIDER_TOTAL_TIMEOUT_MS`;
- `QXNM_FORGE_PROVIDER_MAX_ATTEMPTS`;
- `QXNM_FORGE_PROVIDER_RETRY_MAX_DELAY_MS`.

Values are positive decimal integers, except retry delay may be zero. A native
implementation may expose a richer public configuration API, but conformance
mode must honor these names so timeout, cancellation, and retry cases finish
deterministically. The runner removes inherited Provider/cloud credentials,
generates an in-memory canary credential, and passes it only to the child. The
mock asserts the required authentication header and scheme but stores and
returns only booleans such as `authorizationPresent`; it never stores header
values, full request bodies, prompts, environment variables, or canary bytes.

The mock cases in `CONFORMANCE/fixtures/provider/mock-cases.json` are normative
clean-room wire fixtures. For all three families they cover request shape,
authentication presence, CRLF and multi-line SSE, arbitrary byte fragmentation
through UTF-8/JSON, partial tool arguments, normalized usage, 429 with
`Retry-After`, retryable 503, disconnect, idle timeout, and cancellation.
Every request also carries the same non-empty portable `file.read` definition;
the mock requires Chat `tools[].function.parameters`, Responses
`tools[].parameters`, or Anthropic `tools[].input_schema` with the exact
family-native name/type shape. This proves the adapter does not silently drop
tool declarations. Durable intent/result ordering, approval, execution, and
the canonical next-turn tool message are validated by the separate tool/Agent
runner rather than inferred from this transport probe.
Passing static mock self-tests is not a Provider support claim; an
implementation reaches conformance only after its opt-in black-box probe passes.

The independent second transport suite has `suiteId: batch2-native-v1` and is
stored in `CONFORMANCE/fixtures/provider/mock-cases-batch2.json`. It covers
canonical families `mistral-conversations`, `azure-openai-responses`, and
`google-generative-ai`, with adapter-probe IDs matching those family names and
canonical Provider IDs `mistral`, `azure-openai-responses`, and `google`.
Selecting this fixture is explicit and does not alter the default first-batch
21-case entry point.

| Canonical family | Conformance endpoint environment | Credential environment | Native request after base |
| --- | --- | --- | --- |
| `mistral-conversations` | `QXNM_FORGE_MISTRAL_CONVERSATIONS_ENDPOINT` | `MISTRAL_API_KEY` | `POST /v1/chat/completions` |
| `azure-openai-responses` | `QXNM_FORGE_AZURE_OPENAI_RESPONSES_ENDPOINT` | `AZURE_OPENAI_API_KEY` | `POST /responses?api-version=v1` |
| `google-generative-ai` | `QXNM_FORGE_GOOGLE_GENERATIVE_AI_ENDPOINT` | `GEMINI_API_KEY` | `POST /models/{model}:streamGenerateContent?alt=sse` under the `/v1beta` base |

These environment names, mock models, and loopback targets exist only in
explicit conformance mode. Mistral sends partial JSON strings in streamed
`tool_calls`; Azure sends named Responses argument-delta events; Google sends
SSE `GenerateContentResponse` objects and delivers `functionCall.args` as one
complete JSON object. Arbitrary HTTP byte boundaries may still split that
Google object and MUST be reassembled before JSON parsing. The normative source
evidence and exact redaction rules are in
[ADR 0010](adr/0010-provider-batch2-native-wire-conformance.md).

The independent third transport suite has `suiteId: batch3-native-v1` and is
stored in `CONFORMANCE/fixtures/provider/mock-cases-batch3.json`. It covers the
remaining canonical text families `google-vertex`,
`bedrock-converse-stream`, and `openai-codex-responses`. It remains an
explicitly selected 21-case suite and does not change either earlier entry
point.

| Canonical family | Conformance endpoint environment | Primary credential environment | Native request after base | Stream protocol |
| --- | --- | --- | --- | --- |
| `google-vertex` | `QXNM_FORGE_GOOGLE_VERTEX_ENDPOINT` | `QXNM_FORGE_GOOGLE_VERTEX_OAUTH_TOKEN` | `POST /v1/projects/{project}/locations/{location}/publishers/google/models/{model}:streamGenerateContent?alt=sse` | SSE `GenerateContentResponse` |
| `bedrock-converse-stream` | `QXNM_FORGE_BEDROCK_CONVERSE_STREAM_ENDPOINT` | `AWS_ACCESS_KEY_ID` plus runner-owned synthetic AWS companion values | `POST /model/{model}/converse-stream` | binary AWS EventStream with prelude/message CRC32 |
| `openai-codex-responses` | `QXNM_FORGE_OPENAI_CODEX_RESPONSES_ENDPOINT` | `QXNM_FORGE_OPENAI_CODEX_OAUTH_TOKEN` | `POST /codex/responses` | named Responses SSE events |

The two `QXNM_FORGE_*_OAUTH_TOKEN` names are local conformance injection points,
not production credential APIs. Vertex and Codex require bearer scheme
presence. Bedrock requires the `AWS4-HMAC-SHA256` authorization scheme plus
the presence of its fixed signing headers, but this local suite does not
cryptographically verify the synthetic signature. Codex also requires the
non-secret adapter headers `OpenAI-Beta: responses=experimental` and
`originator: qxnm-forge`; observations retain only presence/prefix-valid booleans.

Vertex delivers `functionCall.args` as one complete object. Bedrock delivers
partial tool input strings inside `contentBlockDelta.delta.toolUse.input`.
Codex delivers partial strings through
`response.function_call_arguments.delta`. Bedrock MUST NOT be decoded as SSE:
every EventStream message has big-endian total/header lengths, a prelude CRC,
typed headers, a JSON payload, and a full-message CRC. Parsers validate both
CRCs and all bounds before dispatch. The fixture splits bytes across both CRCs,
typed headers, UTF-8 code points, and JSON tokens. Exact request, redaction,
failure, and bounded-uncertainty decisions are in
[ADR 0014](adr/0014-provider-batch3-cloud-native-wire-conformance.md).

## OpenRouter Images v0.1

`openrouter-images` is the sole frozen image API family. It reuses
`run/start`, the portable assistant message, Session artifacts, and ordinary run
events; no Provider-branded RPC or event type is introduced. The provider
selection SHOULD set `apiFamily: "openrouter-images"` and MUST set it whenever
the current executable snapshot has more than one matching route. One
Provider/model pair can exist in both a text and image route, so
`providerId + modelId` alone is not always unambiguous. Any request may omit
`apiFamily` only when runtime route selection is unique; the runtime never
guesses from prompt, registration order, or catalog media.

The native request is a non-streaming `POST` to the selected model catalog base
plus `/chat/completions`, using `Authorization: Bearer` and the
`OPENROUTER_API_KEY` value read only at the final request boundary. The compact
strict-JSON body has this semantic shape:

```json
{"model":"<modelId>","messages":[{"role":"user","content":[{"type":"text","text":"draw a circle"}]}],"stream":false,"modalities":["image"]}
```

An input `image_ref` is read only from an earlier same-Session
`artifact.created` record after no-follow regular-file, length, digest, media
type, and image-signature validation. It becomes one OpenAI-compatible
`image_url` part whose URL is a request-local `data:<mediaType>;base64,...`
value. The data URL MUST NOT be retained after the HTTP request. The modalities
array is `['image','text']` exactly when the frozen image catalog row declares
text output; otherwise it is `['image']`. Dynamic models not present in the
frozen catalog are not publicly supported in v1.

The response is one strict JSON object. Only `choices[0].message.content` and
`choices[0].message.images[].image_url` are normalized. Each image URL MUST be
a canonical, whitespace-free base64 data URL; remote URLs, percent encoding,
SVG, empty images, duplicate JSON keys, invalid UTF-8, MIME/signature mismatch,
and trailing JSON are rejected. Accepted media types are `image/png`,
`image/jpeg`, `image/webp`, and `image/gif`. Basic magic-number validation does
not constitute safe image decoding or a hard sandbox.

The portable v0.1 limits are:

- at most 8 input images and 8 output images;
- at most 16 MiB decoded input-image bytes in aggregate;
- at most 32 MiB decoded output-image bytes in aggregate;
- at most 48 MiB of raw HTTP response bytes;
- at most 256 KiB of UTF-8 input or output text;
- the general Session `maxArtifactBytes` still limits each artifact.

Before the first durable append, the adapter validates and decodes the complete
response, including optional usage. It then publishes each image artifact in
source order, appends each `artifact.created`, appends one assistant
`message.appended` containing optional text followed by `image_ref` blocks, and
only then appends/emits `message.completed`. No image delta is emitted.
Canonical output and errors never contain base64, data/remote URLs, credential
values, raw response bodies, or host paths. Retry, timeout, cancellation,
redirect, proxy, origin, and credential-redaction rules are the same bounded
HTTP rules as text families; the dedicated image fixture additionally proves
artifact-first ordering and byte non-disclosure. The clean-room evidence and
full decisions are frozen in
[ADR 0015](adr/0015-openrouter-images-artifact-conformance.md).

## Faux provider

`faux` / `faux-v1` is deterministic and makes no network request. It is a test
family, not one of the frozen 35 live providers. In conformance mode,
`faux/configure` installs one scenario for the given session. The scenario is
consumed by the next accepted run and follows
`schemas/protocol/faux-scenario.schema.json`.

`faux/configure` creates the session header first when the named session is not
yet present; it does not require a separate session-creation RPC.

For a scenario named `hello-v1`, the response `scenarioId` is
`"faux:hello-v1"`. Implementations MUST derive this ID as `"faux:" + name`.
Reconfiguring the same session replaces an unconsumed scenario after a durable
journal append. Production mode SHOULD return method-not-found for
`faux/configure` unless explicitly started with conformance features enabled.

A scenario may include `expectedContext`. This is an assertion over the exact
ordered portable conversation slice sent to faux immediately before its steps
run. Implementations remove implementation-owned system instructions, then
project every remaining Provider input message as follows:

- user and assistant messages retain `role` and normalized public `content`;
- a tool-result message additionally retains `toolCallId`, `toolName`, and
  `isError`, while its output becomes public tool-result `content`;
- message IDs, timestamps, Provider response IDs, usage, and extensions are not
  compared.

The resulting array must equal `expectedContext` structurally, including order,
roles, content block types, text, tool-call arguments, and tool-result metadata.
Mismatch produces a non-retryable `run.failed` with details kind
`faux_context_mismatch`; diagnostics may identify the first index but must not
echo message content. This optional assertion is part of faux scenario `0.1`
because it only strengthens deterministic test behavior and old scenarios are
unchanged. It is the common black-box proof that a resumed session supplied
history to the Provider rather than merely returning history from
`session/get`.

Top-level `steps`, `expectedContext`, and `usage` describe the first Provider
turn. Optional `continuations` is a FIFO list of later-turn scripts; each entry
has `steps`, optional `expectedContext`, and optional `usage`. A tool batch
consumes exactly one continuation when it requests the Provider again, so a
continuation-level context assertion proves the canonical tool results entered
the next turn in source order. Requiring another turn with no remaining item is
a non-retryable faux configuration failure. Scenarios without continuations
retain their prior single-script behavior.

For a successful text-only scenario, the deterministic event sequence is:

1. `run.started`;
2. `turn.started`;
3. `message.started`;
4. one `message.delta` for each `text` step, in scenario order;
5. `message.completed`;
6. `turn.completed` (clients MUST tolerate its presence; the minimal golden
   trace may omit it in protocol `0.1`);
7. `run.completed` with the configured usage.

Delay uses logical milliseconds and MUST be cancellable. Error produces
`run.failed`; disconnect produces `run.interrupted`. Faux tool calls enter the
same real validation, approval, and tool state machine as live model calls.
