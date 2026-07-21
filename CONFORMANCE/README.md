# qxnm-forge conformance suite

This directory contains the language-neutral, black-box conformance harness for
qxnm-forge. Its core runner deliberately uses only the Python standard library; the
optional Draft 2020-12 gate is development-only test infrastructure. Nothing
here is a runtime dependency of any implementation.

Maintainer: 高宏顺 <18272669457@163.com>

Python 方法级注释统一使用中文 docstring，并明确写出功能、作者高宏顺和
邮箱 `18272669457@163.com`。新增或修改函数时必须维持这一格式。

## 签名远程推广 Provider 目录

Rust/.NET 双向门禁：

```sh
python3 sponsored_catalog_runner.py \
  --rust-command-json '["../rust/target/debug/qxnm-forge"]' \
  --dotnet-command-json '["dotnet","../dotnet/src/QxnmForge.Cli/bin/Release/net10.0/qxnm-forge-dotnet.dll"]'
```

runner 只使用运行时临时 P-256 密钥和空目录 fixture，执行两个签名方向、golden JSON 比较
和两个 payload 篡改负例；不会访问真实中转站或 Provider。私钥只存在于 `0700` 临时目录
并在结束时清理。

## CredentialStore 与本地推广 route

```sh
python3 commercial_state_runner.py \
  --rust-command-json '["../rust/target/debug/qxnm-forge"]' \
  --dotnet-command-json '["dotnet","../dotnet/src/QxnmForge.Cli/bin/Release/net10.0/qxnm-forge-dotnet.dll"]'
```

该门禁生成运行期随机 canary，覆盖 Rust 写/.NET 读、.NET 轮换/Rust 读、共同非敏感 route
格式、`sponsors use` 明确确认，以及两端缺 credential 时 Session 前拒绝。它不访问目录 URL
或 Provider，输出固定报告 `10 cases / 2 cross-runtime directions / 0 network requests`。


## Initial contract

The runner launches a daemon and exchanges UTF-8 JSON-RPC 2.0 frames over
NDJSON on stdin/stdout. Diagnostic output is allowed on stderr only. The first
request is `initialize`; the selected protocol version must be one offered by
the client. A conformance-mode daemon also exposes `faux/configure`, which
installs a deterministic scenario for a session. `run/start` acknowledges an
accepted run immediately with `{ "runId": ... }`. Completion is represented
only by exactly one terminal `event` notification, never by a delayed second
response.

The common event envelope is:

```json
{
  "jsonrpc": "2.0",
  "method": "event",
  "params": {
    "sessionId": "opaque",
    "runId": "opaque",
    "turnId": "opaque-if-applicable",
    "seq": 1,
    "time": "2026-07-10T08:00:00Z",
    "type": "run.started",
    "data": {}
  }
}
```

`run.completed`, `run.failed`, `run.cancelled`, and `run.interrupted` are the
terminal event types. Event sequence numbers are strictly increasing within a
session. Events for a run may not precede its `run/start` response, and no event
may follow that run's terminal event.

The faux scenario format is versioned independently. Version `0.1` supports
tagged `text`, `tool_call`, `error`, `delay`, and `disconnect` steps. Production
builds MUST NOT advertise or accept `faux/configure`; only an explicitly enabled
conformance mode may expose it. The Agent Profile fixture records the current Rust
production-advertisement regression as a mandatory negative expectation, not as an
allowed implementation difference.

The live runner remains Python-stdlib-only unless `--schema-root` is explicitly
selected; framing and critical cross-message invariants never need third-party
packages. The optional gate requires `jsonschema` plus `referencing` and checks
actual daemon frames against the canonical Draft 2020-12 schemas. The same
optional packages let `tests/test_spec_schemas.py` validate golden messages and
faux scenarios; when unavailable, those optional tests are reported as skipped,
never falsely passed.

## Running the golden trace

From the repository root:

```sh
python3 CONFORMANCE/runner.py \
  --requests CONFORMANCE/fixtures/golden/hello.requests.ndjson \
  --golden CONFORMANCE/fixtures/golden/hello.trace.ndjson \
  --schema-root SPEC/schemas \
  -- python3 CONFORMANCE/tests/fake_daemon.py
```

The command exits zero only when transport, negotiation, request correlation,
event lifecycle, terminal-state, and golden-trace checks pass. Use
`--actual PATH` to save the normalized observed trace when diagnosing a
difference. This never updates the golden fixture automatically.

Run the self-tests with:

```sh
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s CONFORMANCE/tests -v
```

Agent Profile v0.2 的静态门禁可单独运行：

```sh
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest \
  CONFORMANCE.tests.test_spec_schemas.SpecSchemaTests.test_agent_profile_v02_contract_and_security_negatives -v
```

它验证 `fixtures/agent-profile/profile-cases.json` 的 CRUD/run wire、最小 durable snapshot、
固定错误表、生产 `faux/configure` 禁止项，以及长度、枚举、重复工具、secret/endpoint 和未知
字段负例。该静态门禁不冒充 native CRUD/migration/run-binding 黑盒通过；能力在共同动态
runner 完成前最多登记为 `implemented`。

## Fixture layout

- `fixtures/faux/`: deterministic provider scenarios, with no network access.
- `fixtures/golden/`: requests and normalized observable daemon traces.
- `fixtures/agent-profile/`：Agent Profile v0.2 的封闭 DTO、CRUD/run wire、错误与生产广告预期。
- `fixtures/provider/`: three independent machine suites for nine native
  Provider API families.
- `fixtures/provider-identity/`：35 Provider / 45 route 的 manifest-driven
  广告案例，以及复用既有 `initialize`、`models/list` 的专项 golden trace。
- `fixtures/provider-route/`：ADR 0018 首批六条 executable route、133 个 catalog
  allowlist 模型 census 与九个失败关闭案例。
- `fixtures/sponsored-provider-commercial/`：不含 secret 的本地推广 route 跨语言 fixture。
- `fixtures/tools/`: safe tool, approval, cancellation, durability, and
  recovery machine cases.
- `fixtures/executor/`：离线 `process.exec`、`shell.exec`、进程树与真实 PTY
  helper 夹具；不包含任意仓库脚本或网络命令。
- `fixtures/transport/`: executable UTF-8, NDJSON, partial JSON, and SSE cases.
- `fixtures/security/`：共享路径边界、默认策略攻击案例，以及 ADR 0021 的 Linux
  `file.read`/`file.write` 六项确定性 rebind 机器夹具。
- `fixtures/session/`: portable recovery、branch/compaction、writer lease 与
  PI Session v3 clean-room 导入夹具。
- `tests/`: harness unit tests and a deterministic test daemon.

Golden placeholders such as `$IMPLEMENTATION` and `$TIME` are literal output
of normalization, not permissive wildcards. Opaque run/turn/message/tool IDs
are mapped by first appearance; the normative deterministic faux scenario ID
(`faux:<name>`) is preserved. Unknown extra wire fields remain visible and
therefore cause a golden mismatch.

Initialize capabilities are validated before normalization. The daemon must
declare every method exercised by the request fixture, stdio, faux/faux-v1,
all emitted event types, and the four terminal event types. Honest additional
Providers, methods, tools, and events may differ by implementation; the golden
trace normalizes the validated result to the minimal exercised baseline, so an
implementation is never forced to advertise planned Provider or tool support.

## Provider 身份广告与模型过滤门禁

`provider_identity_runner.py` 验证运行时广告是否严格来自同一个可用路由交集：

```text
manifest route ∩ usable authentication ∩ configured endpoint
               ∩ implemented adapter ∩ capability allowance
```

它不新增 Provider 品牌 RPC。`initialize.capabilities.providers[].models` 必须是
同一 Provider 所有可用 family 的模型 ID 排序去重并集；`models/list` 则保留
`(providerId, modelId, apiFamily)` 三元身份。OpenRouter 两个同时存在于文本和图像
目录的 model ID 在 `models/list` 各出现两次、在 `initialize` 并集中各出现一次。

先运行不启动 daemon 的严格静态 gate：

```sh
python3 CONFORMANCE/provider_identity_runner.py --schema-root SPEC/schemas
```

它会严格加载 JSON（所有层级拒绝重复键），验证 fixture/manifest/catalog Schema、
35 身份、45 route、1,076 catalog 行、manifest 引用闭包、所有案例计数和两个
OpenRouter 歧义对。默认不会修改 `SPEC/capabilities.json`，也不会形成 Provider
支持声明。

用独立 fake daemon 执行完整黑盒自测：

```sh
python3 CONFORMANCE/provider_identity_runner.py \
  --schema-root SPEC/schemas \
  --daemon-command-json '["python3","CONFORMANCE/tests/fake_provider_identity_daemon.py"]'
```

若测试真实语言实现，把 JSON argv 替换为该实现的 stdio daemon argv。runner 不经
shell 启动子进程，并为每个案例创建临时 home、随机 canary 和临时
`AGENT_PROVIDER_IDENTITY_CONFORMANCE_CONFIG`。该文件符合
`provider-identity-config.schema.json`，只保存 manifest 身份与 presence，不保存
credential/endpoint 值。runner 清除继承的 Provider/cloud endpoint 和其它
secret-shaped 环境名称；把大小写 proxy 全部覆盖为
`http://127.0.0.1:9`，`NO_PROXY` 只允许 literal loopback；仅把合成 canary 放入
所选 secret 环境，把 endpoint 配置限制为模板安全值或保留 `.invalid` 域名。整个
套件只调用 `initialize` 与 `models/list`，不发送 `run/start`，不开监听器，不访问
真实 endpoint，也不需要真实凭据。

daemon 命令仍是直接 JSON argv：最多 128 项、每项最多 4,096 字符且不含 NUL，
从不经过 shell。stdin 总量最多 2 MiB 并由独立 writer 写入，候选 daemon 拒读也
不能阻塞总 deadline。监督器用两个 reader 持续排空 stdout/stderr，通过小型有界
队列施加背压；默认合并输出硬上限为 16 MiB、stderr 为 1 MiB，并使用覆盖整个进程
生命周期的总 deadline。超限或 timeout 会立即清理进程树：POSIX 根进程以新
session 启动并对自有进程组执行 TERM/KILL，非 POSIX 平台至少直接
terminate/kill 根进程；正常退出后仍存在的 POSIX 后代也会使门禁失败。诊断不复制
argv、环境或原始输出。

14 个正例覆盖默认无 live 配置、完整 35/45 census、auth/adapter/capability 单项
缺失、能力字段收窄、`runtime-required` endpoint、有/无模板配置、同 Provider 四个
family、单个可用替代 auth profile，以及 OpenRouter 文本/图像歧义。四个负例要求
wire unknown field 为 `-32602`、wire duplicate key 为 `-32700`，presence unknown
field/duplicate key 在启动时 fail closed。每个正例还逐一查询 35 个 `providerId`，
验证未配置 route 不会被共享 adapter 或兄弟 family 带出。

runner 同时检查 stdout、stderr 中不存在随机 credential canary，协议中不存在
manifest credential/config source 名称，也不存在 endpoint、adapter、auth profile
等内部字段。`fixtures/provider-identity/deepseek.trace.ndjson` 是该既有 RPC 组合的
小型 golden；它不包含 credential、endpoint 或 presence 配置。通过本门禁只是选择
算法证据，只有 capability matrix 达到 `conformant`/`live-verified` 才能公开宣称
对应 Provider 支持。

## Canonical Provider Route Spine 门禁

`provider_route_runner.py` 把 manifest/catalog snapshot 与现有 family-native mock
连接起来。首批只允许以下六条 `(providerId, apiFamily)`：

| Provider | API family | 代表模型 | catalog 模型数 | credential 环境 |
| --- | --- | --- | ---: | --- |
| `groq` | `openai-completions` | `llama-3.1-8b-instant` | 7 | `GROQ_API_KEY` |
| `minimax` | `anthropic-messages` | `MiniMax-M2.7` | 3 | `MINIMAX_API_KEY` |
| `mistral` | `mistral-conversations` | `codestral-latest` | 30 | `MISTRAL_API_KEY` |
| `openai` | `openai-responses` | `gpt-4.1` | 42 | `OPENAI_API_KEY` |
| `google` | `google-generative-ai` | `gemini-2.5-flash` | 16 | `GEMINI_API_KEY` |
| `openrouter` | `openrouter-images` | `google/gemini-2.5-flash-image` | 35 | `OPENROUTER_API_KEY` |

先运行完全静态的 Schema、manifest/catalog、mock 和 golden 交叉门禁：

```sh
make provider-routes
# 等价于：
python3 CONFORMANCE/provider_route_runner.py --schema-root SPEC/schemas
```

该入口核对六条 route、133 个模型、九类负例、空 quirk、默认 header policy、唯一
API-key auth、固定生产 HTTPS base、能力收窄和 descriptor。它还严格加载
`fixtures/golden/provider-route-groq.requests.ndjson` 与
`fixtures/golden/provider-route-groq.trace.ndjson`：三条请求覆盖
`initialize -> models/list -> run/start`，规范化 trace 冻结 Groq 三元身份、descriptor、
两个 native text delta 和唯一 `run.completed`。`$IMPLEMENTATION`、`$VERSION`、
`$LANGUAGE`、`$TIME` 是既有 golden 规范化占位符；静态 gate 会替换为合法值再执行协议
生命周期与 Draft 2020-12 Schema 验证。golden 不含 credential、配置路径、endpoint、
header 或 HTTP body；它是动态门禁的最小协议投影，不代替六条真实回环 request observation。

对原生 daemon 运行六个正例、九个负例与四项普通生产门禁：

```sh
python3 CONFORMANCE/provider_route_runner.py \
  --schema-root SPEC/schemas \
  --daemon-command-json \
  '["/absolute/qxnm-forge","daemon","--stdio","--conformance","--workspace","{workspace}","--state-dir","{stateRoot}"]'
```

每案使用 fresh daemon、workspace、state、配置与随机 synthetic credential，只连接
runner 绑定的 ephemeral literal `127.0.0.1` mock。候选进程必须同时启用一般 conformance
和 `QXNM_FORGE_PROVIDER_CONFORMANCE=1`，才可读取
`AGENT_PROVIDER_ROUTE_CONFORMANCE_CONFIG`；该 strict 五字段文件只收窄到一个已冻结
route/model，并把 endpoint 替换为 loopback base。生产模式看到该 presence、配置含未知/
重复字段、非回环 endpoint 或未列 route 时必须在读取 credential 和创建 Session 前拒绝。

happy path 验证同一 snapshot 的广告与执行、family-native target/header/request、终态、
Session Schema 和隐私扫描；负例覆盖 missing credential、wrong family、unknown model、
unlisted route、production presence、非法 endpoint、config unknown/duplicate key 与 wire
duplicate key。credential 值只允许在最终 HTTP header 构造边界读取，不能进入 Registry、
协议、stderr、Session、artifact 或 fixture。其余 39 条 manifest route 继续 fail closed。

四项 production gate 不设置 conformance/config：六个合成非空 credential 必须精确广告
六 route/133 descriptor（连同 `faux` 为 7 Provider/134 model ID）；只设置 allowlist 外
Azure/Anthropic 环境时必须只剩 `faux`；identity/route 双 presence 必须在读取任一路径前
拒绝；值为空的 route presence 也必须启动拒绝。每项都有 loopback network tripwire，且
不得创建 Session 或泄漏 canary、endpoint、presence 路径。

Rust、.NET、Go、TypeScript 与 Python 的 fresh daemon 均已通过同一 `6 positives / 9
negatives / 4 production gates` 门禁。以上 fixture、golden 与动态请求全部使用 synthetic credential，
不访问付费模型；该结果是 offline `conformant` 证据，不是 `live-verified`。公开支持仍以
`SPEC/capabilities.json` 中审计后的 route-scoped 记录为准，且 Provider claim 必须落到精确
`(providerId, apiFamily, feature)` cell，不能合并 sibling route。五语言 120 个 cell 受
共享 Schema 单测约束，仅声明 Linux offline loopback；无原子 no-follow 的平台不在范围。

## Local Provider transport suite

`provider_mock.py` is a Python-standard-library HTTP/SSE/AWS-EventStream server
for these API families:

- OpenAI-compatible Chat;
- OpenAI Responses;
- Anthropic Messages.

The explicit second-batch fixture adds:

- Mistral Conversations;
- Azure OpenAI Responses;
- Google Generative AI `streamGenerateContent?alt=sse`.

The explicit third-batch fixture adds:

- Google Vertex OAuth plus project/location/publisher routing;
- Amazon Bedrock ConverseStream with SigV4 header presence and binary AWS
  EventStream CRC framing;
- OpenAI Codex OAuth, Codex Responses routing, native Responses events, and
  fixed non-secret adapter headers.

It always asks the kernel for an ephemeral port on the literal IPv4 address
`127.0.0.1`; it never binds a wildcard address, redirects a request, resolves a
hostname, or makes an outbound connection. The 21 normative machine cases in
`fixtures/provider/mock-cases.json` cover each family's request shape and auth
scheme plus CRLF, multi-line SSE, arbitrary UTF-8/JSON fragmentation, partial
tool arguments, an EOF-final event without an extra blank line, usage, 429 with numeric `Retry-After`, 503 with HTTP-date
`Retry-After`, disconnect, idle timeout, and cancellation.

`fixtures/provider/mock-cases-batch2.json` is an independent 21-case suite. It
uses the native Mistral `/v1/chat/completions` Bearer contract, Azure
`/responses?api-version=v1` plus `api-key`, and Google
`/models/{model}:streamGenerateContent?alt=sse` plus `x-goog-api-key`. All three
requests contain a nonempty tool declaration. Mistral and Azure stream partial
JSON argument strings; Google returns one complete `functionCall.args` object,
although arbitrary HTTP chunking still splits its UTF-8/JSON bytes. The Google
wire is SSE, not a synthetic bare chunked-JSON protocol.

`fixtures/provider/mock-cases-batch3.json` is another independent 21-case
suite. Vertex requires `Authorization: Bearer`, the exact synthetic
`projects/mock-project/locations/us-central1/publishers/google` path, and a
complete `functionCall.args` object. Bedrock requires the
`AWS4-HMAC-SHA256` authorization scheme plus signing-header presence and emits
`application/vnd.amazon.eventstream`; each binary message carries big-endian
lengths, a prelude CRC32, typed headers, JSON payload, and a message CRC32.
Codex requires bearer auth, `/codex/responses`, Responses argument deltas, and
the non-secret `OpenAI-Beta`/`originator` adapter headers. The suite only checks
the synthetic SigV4 shape and does not claim cryptographic signature or live
OAuth verification.

Run the default offline/static gate with:

```sh
python3 CONFORMANCE/provider_runner.py --schema-root SPEC/schemas
```

This validates the fixture, renders every native wire stream, and exits without
starting a daemon or opening a listener. Passing it is not a Provider support
claim and does not change `SPEC/capabilities.json`.

Run the second offline/static gate explicitly with:

```sh
python3 CONFORMANCE/provider_runner.py \
  --fixture CONFORMANCE/fixtures/provider/mock-cases-batch2.json \
  --schema-root SPEC/schemas
```

Run the third offline/static gate explicitly with:

```sh
python3 CONFORMANCE/provider_runner.py \
  --fixture CONFORMANCE/fixtures/provider/mock-cases-batch3.json \
  --schema-root SPEC/schemas
```

All three commands report `3 families / 21 cases`; together they cover nine
family contracts and 63 cases. The default remains the original first-batch
fixture, so existing implementation scripts retain their 21-case behavior.

For direct parser development, start the standalone mock:

```sh
python3 CONFORMANCE/provider_mock.py
```

Pass `--fixture CONFORMANCE/fixtures/provider/mock-cases-batch2.json` or
`--fixture CONFORMANCE/fixtures/provider/mock-cases-batch3.json` to serve a
later suite instead. The server accepts only family/case routes present in the
selected fixture and checks its exact native path suffix/query without
recording values. Bedrock responses use binary EventStream rather than SSE.

It prints one `READY http://127.0.0.1:<port>` line. Readiness and sanitized
observations are available at `/_control/health` and
`/_control/observations`. Observations retain only family/case/attempt, required
header presence, auth/fixed-header presence-and-scheme booleans, top-level key
names, and shape counts. Header values, signatures, complete bodies, prompts,
model values, credentials, and environment variables are never retained or
returned.

An implementation probe is explicitly opt-in and accepts only JSON argv:

```sh
python3 CONFORMANCE/provider_runner.py \
  --family openai-compatible \
  --case text_success \
  --schema-root SPEC/schemas \
  --daemon-command-json '["/absolute/path/to/daemon", "daemon", "--stdio"]'
```

For a second-batch probe, add the explicit `--fixture` above and select one of
`mistral-conversations`, `azure-openai-responses`, or `google-generative-ai`.
For a third-batch probe, select `google-vertex`, `bedrock-converse-stream`, or
`openai-codex-responses` with `mock-cases-batch3.json`. The synthetic endpoint,
OAuth/AWS credential injection, project/location, and mock model identifiers
are valid only while the child is in explicit Provider conformance mode.

The argv is passed directly without a shell. For every case, the runner removes
inherited Provider/cloud credentials and endpoints, creates a fresh in-memory
canary, injects only the selected loopback endpoint, applies bounded test
timeouts/retries and dead proxies, then checks normalized events, retry count,
sanitized mock observations, stderr/protocol output, and the runner-assigned
temporary session root for canary leakage. RPC stdout must contain protocol
frames only. Failure diagnostics show at most the executable filename and a
4 KiB stderr tail, never full argv or environment values.

When `--schema-root` is present on a dynamic probe, the runner also validates
every actual JSON-RPC request, response, and event against the bundled protocol
Schemas. It recursively locates every regular `journal.jsonl` below the fresh
runner-assigned session root (including direct .NET-style and
`sessions/...` Rust-style layouts) and validates every strict UTF-8/LF JSON line
against `session/journal.schema.json`. Symlinks are not followed, reads are
bounded, and failures expose only a relative journal path, line number, and a
redacted instance-path shape—not journal contents or field values. A daemon
that does not create a journal for a probe yields a valid zero-journal scan;
the protocol trace is still Schema-checked.

## Tool and approval suite

`fixtures/tools/tool-approval-cases.json` 是受治理的 v0.1 矩阵，包含 11 个常规
daemon 案例、4 个 approval failure 案例和 1 个 portable non-idempotent recovery
案例。常规与 portable recovery 案例覆盖：

- workspace `file.read` without approval and headless `file.write` default
  denial;
- interactive `allow_once`, interactive deny, and cancellation while waiting
  for approval;
- unknown tools, invalid arguments, sequential Provider source order, and a
  faux continuation whose exact `expectedContext` proves the tool result
  entered the next Provider turn;
- append-before-event for `tool.intent`, approval records, `tool.result`, the
  canonical tool message, and exact `event.emitted` records;
- duplicate `run/cancel` idempotency, source-ordered cancelled results for every
  remaining complete call in a multi-tool batch, and the existing portable
  recovery fixture's non-idempotent no-replay rule.

审批契约由六个黑盒门禁共同冻结：正常 client `allow_once`、正常 client deny、100 ms
timeout 后两次 late/duplicate response conflict、stdin EOF/disconnect、
`approval/respond` 成功帧 delivery failure，以及 durable `approval.requested` 后
SIGKILL 并重开同一 Session。每个故障案例都必须得到唯一 resolution、唯一 tool
result、零 `tool.started`、零目标 workspace mutation 和唯一允许的 run terminal。

Client 决定必须先持久化为唯一 `approval.resolved`；`allow_once` 只有在
`{accepted:true}` 成功写出并 flush 后才可释放执行屏障。成功帧交付失败时保留原
`decision.choice:"allow_once"` / `resolutionSource:"client"`，不得补写第二条 deny，
且必须以错误 tool result 和 run terminal fail closed。SIGKILL recovery 必须保留崩溃
前 journal 的完整字节前缀，重开后追加唯一 deny/disconnect resolution、
outcome-known 未执行结果和唯一 `interrupted` terminal，仍不得执行工具或修改目标文件。

Runner 仅在渲染后的 daemon argv 含精确、独立元素 `--conformance`，并且子进程环境中
`QXNM_FORGE_CONFORMANCE=1` 时注入 `QXNM_FORGE_APPROVAL_TIMEOUT_MS=100`。两道门缺一不可；
`--conformance=true`、前缀匹配或仅设置环境变量均不授权 timeout override。

Run the offline/static gate with:

```sh
python3 CONFORMANCE/tool_runner.py --schema-root SPEC/schemas
```

Run the 11 normal dynamic fake-daemon harness self-tests with safe JSON argv:

```sh
python3 CONFORMANCE/tool_runner.py \
  --schema-root SPEC/schemas \
  --kind daemon \
  --daemon-command-json \
  '["python3","CONFORMANCE/tests/fake_tool_daemon.py"]'
```

四项 transport/crash 故障不由简化 fake daemon 冒充；统一验证会以同一 runner 分别驱动
五个原生 daemon，执行真实 stdout 断开、stdin EOF、短 timeout 与 `SIGKILL` 恢复。

For a native implementation, argv items may contain `{workspace}`,
`{stateRoot}`, `{sessionsRoot}`, `{sessionId}`, and `{repoRoot}` placeholders.
The runner replaces only these values while retaining argv boundaries. It also
sets `QXNM_FORGE_WORKSPACE` and `QXNM_FORGE_SESSION_ROOT` to the same fresh temporary
roots for implementations that use the common conformance environment.

Dynamic fixtures may invoke only `file.read` and `file.write` on governed safe
relative paths; `fixture.unknown` is lookup-only. Static validation rejects
process, shell, terminal, network, credentials, absolute paths, traversal, and
arbitrary command text before a daemon starts. Provider/cloud credentials and
endpoints are removed from the child environment, and no shell parses the
daemon argv.

At each observed tool or approval event, the runner reads only the journal's
last complete LF-delimited append-only prefix. This proves the corresponding
records already existed without treating a concurrently written next record as
corruption. After the terminal event it strictly validates every complete
journal against `session/journal.schema.json`. A successful
`approval/respond` frame must be delivered before `tool.started`; the durable
`approval.resolved` client decision precedes that frame and does not by itself
release execution. `run/cancel` permits events to race its response because the
protocol does not impose that ordering. Both the first and repeated cancel
responses are checked immediately against the single durable
`run.cancellation_requested` record.

The dynamic fake-daemon gate validates the runner, not a native implementation,
and never changes `SPEC/capabilities.json`. A native capability is supportable
only after that native daemon passes the same selected/all cases and the shared
capability governance is updated separately.

## Linux `file.read`/`file.write` 路径竞态门禁

`fixtures/security/path-boundary-race-cases.json`、
`../SPEC/schemas/path-boundary-race-cases.schema.json`、
`path_boundary_runner.py` 与 [ADR 0021](../SPEC/adr/0021-linux-file-path-race-conformance.md)
共同冻结 Linux handle-relative 路径证据。静态入口只验证 Schema、六个案例的闭集、四个
checkpoint、tool/case 关联值和生产双门定义，不启动任何语言实现：

```sh
python3 CONFORMANCE/path_boundary_runner.py --schema-root SPEC/schemas
```

动态入口必须接收安全 JSON argv 数组，并为每个案例创建 fresh daemon、workspace、state、
outside 与 `0700` control directory。一个通用示例是：

```sh
python3 CONFORMANCE/path_boundary_runner.py \
  --schema-root SPEC/schemas \
  --daemon-command-json \
  '["/absolute/path/to/native-daemon","daemon","--stdio","--conformance","--workspace","{workspace}","--state-dir","{stateRoot}"]'
```

六个动态案例按 fixture 顺序覆盖：workspace root 的 read/write
`before_parent_walk` rebind、nested parent 的 read/write `after_parent_open` rebind、read
leaf 的 `after_leaf_open_before_read` rebind，以及 write leaf 的
`after_temp_fsync_before_rename` swap。每例都必须成功命中 pinned root/parent/leaf；统一
deny 不算通过。read 必须返回 original 内容；write 新内容只能进入 pinned original tree；
outside 的 no-follow manifest 和随机 canary 必须逐项不变且不得进入协议、stderr、Session、
journal 或 control。

write 案例使用真实 interactive approval。唯一 durable `approval.resolved` 与成功
`approval/respond` 响应交付都必须早于 `tool.started` 和 ready barrier。每个调用还要保持
唯一 `tool.intent`、`tool.result`、canonical tool message、`tool.completed` 与 run terminal，
并继续满足 append-before-observe。

另外两项 production gate 分别只保留 argv 中精确独立的 `--conformance` 或只保留环境
`QXNM_FORGE_CONFORMANCE=1`，同时把
`AGENT_PATH_BOUNDARY_CONFORMANCE_CONFIG` 指向永不提供 writer 的 FIFO。实现必须在打开
FIFO、创建 ready、Session/journal 或执行工具之前，让 `initialize` 返回不可重试
`-32603` 与 `details.kind="conformance_configuration_invalid"`；presence 不能被忽略或回显。

Rust、.NET、Go、TypeScript 与 Python 的 fresh 原生 daemon 已分别通过这套 `6 race + 2
production gates`。结果只支持 Linux `file.read`/`file.write` 窄化证据；Windows 路径
语义、其他文件工具、process cwd 和 mount change 尚未覆盖，因此宽能力
`security.path_boundary` 仍保持 `implemented`。

## Executor 与 PTY 契约门禁

`fixtures/executor/executor-cases.json`、`fixtures/executor/executor_helper.py`
和 `executor_runner.py` 共同冻结 `process.exec`、显式 `shell.exec`、进程树终止及
`terminal.open` 的最小黑盒语义。默认入口只在 runner 创建的临时工作区运行固定
helper，不读取 Provider 凭据、不访问网络、不执行仓库提供的任意 shell 文本：

```sh
PYTHONDONTWRITEBYTECODE=1 python3 CONFORMANCE/executor_runner.py \
  --schema-root SPEC/schemas
```

本机适用案例验证 argv 边界、stdout/stderr 分离、非零退出、bounded stdin、secret
环境隔离、timeout、output limit、显式 POSIX shell，以及真实 Unix PTY 的身份、I/O、
resize 与 signal。已广告 terminal 的 daemon 还必须通过 response-delimited attach replay、
takeover/旧 attachment、实际 wire event 大小、严格 `output→exited→closed` 和 64 KiB
输入背压原子拒绝。Windows suspended Job/ConPTY 只能在 Windows 实机通过；Unix 的
长期存活 `setsid` marker 案例只验证尽力清理回归，不等价于快速 double-fork 的 OS
containment；daemon attachment/replay/断线回收仍必须由真实 daemon 动态证明。
平台或实现能力缺失会明确输出 `SKIP`，绝不计为 `PASS` 或支持声明；普通 pipe 也不能
以 `ptyKind` 冒充 PTY。

原生 daemon 探针仅接受安全 JSON argv，不经过 shell：

```sh
python3 CONFORMANCE/executor_runner.py \
  --schema-root SPEC/schemas \
  --daemon-command-json \
  '["/absolute/path/to/native-daemon","daemon","--stdio","--conformance"]'
```

只有广告对应方法并通过适用动态案例的实现，才可在能力矩阵登记相应证据；当前
`unix_process_group` 加采样清理最多登记为 `implemented`。helper 自测通过本身不构成
任何语言实现的 `conformant` 声明。

当前只有 Rust 在 Linux 双门控 fixture-only 策略下通过三个适用 terminal 动态案例，
能力矩阵登记 `terminal.open=implemented`。该记录不包含生产 policy、retain/detach、
owner disconnect、hard sandbox、macOS 或 Windows/ConPTY，也不构成 `conformant`、
`live-verified` 或公开支持。Python、.NET、Go、TypeScript 均无 terminal claim，其 terminal
案例必须继续明确 `skip(capability_unavailable)`。

## Portable Session fixture

The shared case is under `fixtures/session/portable-v0.1/`. Validate every
journal line plus append-only recovery semantics with:

```sh
python3 CONFORMANCE/session_validation.py
python3 CONFORMANCE/session_runner.py
```

The static gate checks message reconstruction, event `latestSeq`/`afterSeq`, an
accepted run without a terminal, and an unresolved non-idempotent tool. The
recovered journal must append an `outcomeKnown:false` result and tool message,
then an interrupted terminal and core `event.emitted`, without a second tool
intent.

`session_runner.py` additionally provides an opt-in black-box handoff entry.
It copies the pre-recovery fixture to an isolated state root and drives
`session/get -> faux/configure(expectedContext) -> run/start`. Command templates
are JSON argv arrays and never pass through a shell. A Rust probe is:

```sh
python3 CONFORMANCE/session_runner.py \
  --daemon-command-json \
  '["{repoRoot}/rust/target/debug/qxnm-forge","daemon","--workspace","{workspace}","--state-dir","{stateRoot}"]'
```

A .NET probe is:

```sh
python3 CONFORMANCE/session_runner.py \
  --daemon-command-json \
  '["dotnet","{repoRoot}/dotnet/src/QxnmForge.Cli/bin/Release/net10.0/qxnm-forge-dotnet.dll","daemon","--stdio","--conformance","--workspace","{workspace}"]'
```

Repeat `--daemon-command-json` twice to test a real handoff on the same journal,
for example Rust then .NET or the reverse. Each stage must preserve the prior
byte prefix, return the full durable snapshot, pass faux `expectedContext`, and
append a new completed turn. This opt-in command is an entry point, not a
support claim; `session.cross_language` remains unchanged until both orders
pass the shared gate.

## Session journal 尾部恢复与严格损坏门禁

`fixtures/session/journal-tail-recovery-cases.json` 与 ADR 0020 固定六个案例：两个
无 LF 尾行必须修复，四个 LF 完整坏行必须以 `journal_corrupt` 零修改失败。静态入口
验证案例 Schema、生成字节、Schema-valid no-LF 记录和递归 duplicate key：

```sh
python3 CONFORMANCE/session_tail_runner.py --schema-root SPEC/schemas
```

动态入口在一个隔离 state root 中创建六个 Session，只启动两个 daemon 生命周期：第一轮
触发实际 `session/get` open/recovery，第二轮验证 clean reopen 幂等。命令模板与
`session_runner.py` 使用相同的 JSON argv 和占位符：

```sh
python3 CONFORMANCE/session_tail_runner.py \
  --schema-root SPEC/schemas \
  --daemon-command-json \
  '["{repoRoot}/rust/target/debug/qxnm-forge","daemon","--conformance","--workspace","{workspace}","--state-dir","{stateRoot}"]'
```

两个 repair 案例分别使用 invalid torn JSON 和完整、Schema-valid 的 JSON record，二者
都没有 LF。runner 要求 backup basename 为完整原 journal SHA-256，backup bytes 与原
journal 逐字节相同，并要求唯一 `org.agent-session.recovery` extension 的 value 精确匹配
ADR。基础 journal 额外包含未知 reverse-DNS extensions 和 durable event gap
`[1,2,3,4,5,6,7,10]`；修复必须保留其原始字节，并从最大 event seq 之上继续。

四个 corruption 案例分别是 LF 完整的非法 UTF-8、JSON、Schema 和递归 duplicate key。
每个案例两次 open 都必须返回 `-32008`、`retryable:false`、
`details.kind=journal_corrupt`，journal bytes 不变且不创建 recovery backup。动态通过只
形成该实现的候选证据；runner 不修改 `SPEC/capabilities.json`。当前合同不注入
backup/truncate/diagnostic 三步之间的进程崩溃，因此不能外推 crash-atomic repair 声明。

## Cross-language writer lease matrix

`writer_lock_runner.py` first validates the nine fixed decisions in
`fixtures/session/writer-lock-cases.json`. With one or more daemon command
templates it then runs every ordered holder/contender pair. A long-running faux
turn holds the Session, the contender must return retryable
`code=-32002` and `details.kind=session_locked` without changing the journal or owner metadata,
and the runner forcibly crashes the holder with no cleanup opportunity. The
same contender must then atomically take over the stale witness, recover the
interrupted run, preserve the prior journal byte prefix, and cleanly remove its
own canonical claim on exit.

Run the static safety table without starting a process:

```sh
python3 CONFORMANCE/writer_lock_runner.py --schema-root SPEC/schemas
```

Run an ordered Rust/Python 2×2 example with JSON argv boundaries:

```sh
python3 CONFORMANCE/writer_lock_runner.py \
  --schema-root SPEC/schemas \
  --daemon-command-json \
  '["{repoRoot}/rust/target/debug/qxnm-forge","daemon","--workspace","{workspace}","--state-dir","{stateRoot}"]' \
  --daemon-command-json \
  '["python3","{repoRoot}/PYTHON/qxnm-forge_cli.py","daemon","--stdio","--conformance","--workspace","{workspace}","--state-dir","{stateRoot}"]'
```

The runner removes Provider/cloud credentials, endpoints, and proxy variables;
it never invokes a live Provider. Passing one runtime or a subset matrix remains
`implemented` evidence only. `session.cross_language_writer_lock` becomes
conformant only after the full 5×5 matrix runs on each claimed platform.

## Branch and compaction fixture

`fixtures/session/branch-compaction-v0.1/` is the normative append-order/tree
case for ADR 0011. It contains an immutable old prefix, a summary plus retained
recent pair, Branch A, a switch back to the compacted point, Branch B, and a
final switch to Branch A. The selected Provider projection is exactly: summary,
retained recent user/assistant, then Branch A user/assistant. The old prefix and
Branch B remain durable but are excluded.

Run the static Schema and cross-record gate with:

```sh
python3 CONFORMANCE/branch_compaction_validation.py \
  --schema-root SPEC/schemas
```

The validator separately checks append `seq`, earlier-only tree edges, selected
head transitions, quiescent selection targets, summary/source/retained
references, token reduction, and exact projected message order. Implementations
must not silently fall back to linear/full-history context when this validation
fails.

`expectations.json` also freezes the seven portable mutation failures, including
stale-head, busy, unknown target, immutable non-quiescent ancestry, invalid
retained boundary, token growth and corrupt-journal cases. Implementations must
match `code`, `retryable` and `details.kind` exactly and append no bytes on each
failure.

`branch_compaction_runner.py` accepts the same safe, repeatable
`--daemon-command-json` templates as the Session runner. Each implementation
must first pass isolated success cases for both mutation RPCs, repeated branch
selection, a summary-only crash-state read, and the seven fixed error cases
(including active-run precedence and a semantic-corruption probe). It must then return the exact selected snapshot (including selected-head
and active compaction IDs), advertise the two mutation methods, satisfy faux
`expectedContext`, append one continuation without rewriting the byte prefix,
and leave a journal the next implementation can project. With no daemon command
it runs only the static gate. The static fixture alone is not a branch/compaction
support claim.

## PI Session v3 一次性导入夹具

`fixtures/session/pi-v3-import-v0.1/` 冻结一份完全合成且脱敏的 PI v3 JSONL、
对应 portable journal、强绑定 import-report artifact 与机器期望。它覆盖两个分支、
标准消息、model/thinking 变化、compaction、branch summary、session metadata、
label、custom/custom-message 和未知未来 entry；不会读取或运行 `PI/` 代码。

运行静态导入契约与安全边界门禁：

```sh
python3 CONFORMANCE/pi_v3_import_validation.py --schema-root SPEC/schemas
```

validator 固定检查 exact source-byte SHA-256、不同的新 Session ID、显式
`branch.selected`、summary + `context.compacted` 双记录、报告 artifact 的字节长度与
哈希、所有有损/隔离项覆盖，以及目标中不存在 run、Provider、工具、审批、队列或
event 生命周期记录。通过静态夹具只证明公共映射契约，不代表任一语言 importer 已
实现。`pi_v3_import_runner.py` 接受一个或多个安全 JSON argv 模板，逐实现创建只读
source 副本和独立 state root，并验证唯一 JSON stdout、source 零变化、source path
不泄漏、唯一 journal、精确 deterministic conformance journal 与报告 artifact：

```sh
python3 CONFORMANCE/pi_v3_import_runner.py \
  --schema-root SPEC/schemas \
  --import-command-json \
  '["/absolute/qxnm-forge","session","import-pi-v3","--source","{source}","--workspace","{workspace}","--state-dir","{stateRoot}","--session","{sessionId}","--format","json","--conformance"]'
```

只有原生 CLI 通过该黑盒 runner 后，对应实现才能登记导入支持。

## OpenRouter Images artifact-first 门禁

`fixtures/provider/mock-cases-images.json`、
`fixtures/golden/openrouter-images.requests.ndjson` 与
`fixtures/golden/openrouter-images.trace.ndjson` 冻结 ADR 0015 的 13 个离线案例和
durable 顺序。静态检查不会启动实现：

```sh
python3 CONFORMANCE/provider_image_runner.py --schema-root SPEC/schemas
```

动态门禁必须传入直接 argv；runner 会为每个案例创建独立 Session 根和 literal
`127.0.0.1` mock，注入内存 credential canary，并阻断非回环代理：

```sh
python3 CONFORMANCE/provider_image_runner.py \
  --schema-root SPEC/schemas \
  --daemon-command-json \
  '["/absolute/qxnm-forge","daemon","--stdio","--conformance","--workspace","/absolute/workspace"]'
```

套件覆盖 PNG/JPEG/WebP/GIF、可选文字、429/5xx、断流、idle timeout、取消、远程
URL、SVG、非 canonical base64、MIME/魔数欺骗，以及输入 `image_ref`/输出 artifact。
成功路径必须先追加全部 `artifact.created`，再追加唯一 assistant
`message.appended(image_ref)` 和 `message.completed`；图像不得产生 `message.delta`。
协议帧、stderr 和 Session 非 artifact 文件会扫描 credential、base64 与 data URL。
只有 fresh 原生 daemon 的 13/13 才构成该实现的 `conformant` 证据，仍不等于真实付费
Provider `live-verified`。

## Linux Bubblewrap hard-sandbox 门禁

`hard_sandbox_runner.py` 与 `fixtures/security/hard-sandbox-cases.json` 冻结
`linux-bwrap-v1`。默认只运行 Schema、四案例顺序和失败关闭静态契约：

```sh
python3 CONFORMANCE/hard_sandbox_runner.py --schema-root SPEC/schemas
```

真实本机探针必须显式启用：

```sh
python3 CONFORMANCE/hard_sandbox_runner.py \
  --schema-root SPEC/schemas \
  --run-local
```

runner 要求 backend 与网络探针解释器为 root-owned、普通、可执行且当前用户不可写的
绝对文件。它先证明宿主随机 loopback listener 可达，再在 network namespace 内用
Python `socket.connect_ex` 证明隔离，避免 dash `/dev/tcp` 语法错误造成假阳性。所有
命令受整体/案例 timeout、合并输出上限和 PID+starttime 进程树监督；timeout、输出超限
和直接父退出都必须清理 fork 后 `setsid` 的后代。缺失/用户可写 backend 或 bind setup
失败必须返回 `sandbox_unavailable` 且绝不 host fallback。

公共 runner 本机通过只证明隔离契约可执行；任一语言只有把同一 profile 接入真实 daemon
并通过共同动态门禁后，才能声明 `security.hard_sandbox`。路径检查、cwd、进程组或
`/proc` guard 不能替代 OS/container/VM 隔离证据。

原生 daemon 动态门禁复用现有 `process.exec` 和 approval 流，不新增 RPC：

```sh
python3 CONFORMANCE/hard_sandbox_runner.py \
  --schema-root SPEC/schemas \
  --daemon-command-json '["/path/to/daemon","daemon","--stdio","--conformance","--workspace","{workspace}","--state-dir","{stateRoot}"]'
```

实现仅在 `AGENT_HARD_SANDBOX_PROFILE=linux-bwrap-v1` 时启用 profile。生产 backend
固定 `/usr/bin/bwrap`；`AGENT_HARD_SANDBOX_CONFORMANCE_BACKEND` 只有 daemon argv
含精确 `--conformance` 且 `QXNM_FORGE_CONFORMANCE=1` 时可读，任一门缺失时 presence
必须失败关闭。runner 会验证完整 `initialize.capabilities.hardSandbox`，再通过
`faux/configure -> run/start -> approval/respond` 运行六个正例：read-only/read-write
workspace、cwd/argv/UTF-8、`/tmp`、系统只读、宿主外不可见、loopback listener 不可达、
credential 不继承，以及 timeout/output-limit/cancel/daemon-exit 对 fork+`setsid`
后代的清理。

四个负例覆盖未配置 profile 的显式 sandbox 请求、缺失 backend、用户可写 backend 和
身份/版本可通过但不建立 namespace 的 setup self-test backend；均要求
`sandbox_unavailable`、无 `execution`/Session 用户副作用且无 host fallback。两项生产
门禁分别证明固定 backend 可广告，以及 conformance override 在生产 argv 下仅有环境门
时拒绝。动态通过报告 `6 positives / 4 negatives / 2 production gates`；只有 fresh
原生 daemon 的该报告才构成 Linux `conformant` 证据。
