# ADR 0018：Manifest 驱动的 Provider 可执行 Route Spine

- 状态：Accepted
- 日期：2026-07-15
- 项目：qxnm-forge
- 作者：高宏顺 `<18272669457@163.com>`

## 背景

ADR 0017 已让五种实现从同一份 `providers.v1.json` 与 `models.v1.json`
生成 identity-only 广告快照，但该快照明确不可执行。现有九个文本 API family 和
一个图像 family 已分别通过本机 mock conformance；这仍不能证明 canonical
`providerId`、route、catalog model、credential source 与 family adapter 在生产路径上
正确绑定。

一次性启用全部 35 Provider / 45 route 也不安全。冻结 manifest 中有 15 类
compatibility quirk，当前只有数据枚举，没有完整请求/响应语义；OAuth、Google ADC、
AWS default chain 与 Cloudflare 特殊 header 也需要独立安全契约。不得根据字段名猜测
语义，也不得把未实现 quirk 静默忽略后仍广告对应 route。

## 决策

### 1. 第一批 Route Spine 范围

冻结六条无 quirk、固定 catalog endpoint、单一 API-key、默认 header policy 的
canonical route：

| Provider | API family | Adapter | Credential source | 代表模型 | catalog 模型数 |
| --- | --- | --- | --- | --- | ---: |
| `groq` | `openai-completions` | `openai-completions-v1` | `GROQ_API_KEY` | `llama-3.1-8b-instant` | 7 |
| `minimax` | `anthropic-messages` | `anthropic-messages-v1` | `MINIMAX_API_KEY` | `MiniMax-M2.7` | 3 |
| `mistral` | `mistral-conversations` | `mistral-conversations-v1` | `MISTRAL_API_KEY` | `codestral-latest` | 30 |
| `openai` | `openai-responses` | `openai-responses-v1` | `OPENAI_API_KEY` | `gpt-4.1` | 42 |
| `google` | `google-generative-ai` | `google-generative-ai-v1` | `GEMINI_API_KEY` | `gemini-2.5-flash` | 16 |
| `openrouter` | `openrouter-images` | `openrouter-images-v1` | `OPENROUTER_API_KEY` | `google/gemini-2.5-flash-image` | 35 |

六条 route 的冻结 catalog allowlist 合计 133 个模型。

其它 39 条 route 在本 ADR 阶段保持 fail closed。它们不会因为共享 family adapter
存在、catalog 有模型或 identity-only gate 通过而变成可执行 route。

### 2. 不可变 executable snapshot

每个实现必须在 daemon 启动期严格加载并验证冻结 manifest/catalog，生成不可变
executable snapshot。route key 是 `(providerId, apiFamily)`；模型 key 是
`(providerId, modelId, apiFamily)`。snapshot 至少保存：

- route 与 adapter 的精确关联；
- catalog 中该 route 的模型 allowlist 与归一化 descriptor；
- catalog endpoint base；
- manifest auth profile 与唯一环境 credential source 名；
- 本批次固定为 `api-family-default` 的 header policy；
- route quirk 集必须为空；
- 当前实现已达到 `conformant`/`live-verified` 的 family feature 下界。

广告与执行必须来自同一个 snapshot。`models/list` 出现的本批次 descriptor 必须能由
同一进程的 `run/start` 精确解析；可执行模型也必须已经出现在该快照中。不得保留
“动态任意 modelId”后门。

### 3. 生产可用性

普通启动时，六条 route 分别读取 manifest 指定的 credential 环境名称是否存在且非空。
缺少 credential 时整条 route 在 `initialize` 和 `models/list` 中消失，`run/start`
在创建 Session 或追加 durable record 前返回 `-32005/provider_unavailable`。

credential 值只允许在最终 HTTP request header 构造边界读取。Registry、route plan、
descriptor、审批、Session、事件、日志、异常与 debug 输出不得长期保存或复制 credential
值。credential 轮换后下一次请求读取新值；空值视为不可用。

生产 endpoint 只能来自对应 catalog 行的同一固定 HTTPS base。六条 route 内所有模型
必须具有同一 endpoint base；不一致时 daemon fail closed。family adapter 在该 base
上追加冻结的原生 request target：

- Chat：`/chat/completions`；
- Anthropic：`/v1/messages`；
- Mistral：`/v1/chat/completions`；
- Responses：`/responses`；
- Google：`/models/{modelId}:streamGenerateContent?alt=sse`；
- OpenRouter Images：`/chat/completions`。

追加后必须保持 scheme/host/effective port 不变，且继续执行 ADR 0008 的 URL、redirect、
proxy 与 credential-origin 规则。

### 4. 能力投影

catalog 证据不得放大 runtime 能力。文本 route 在本阶段只允许 `authentication`、
`text`、`streaming` 与 `tools`；catalog 的 image input 或 reasoning 证据在对应 family
尚无矩阵 claim 时必须移除或置为 false。图像 route 只允许 `authentication`、`text`、
`image_input` 与 `image_output`，并固定 `streaming:false`、`tools:false`、
`reasoning:false`。

模型 input/output 投影后为空时必须省略模型。`contextWindow` 与
`maxOutputTokens` 继续映射为 portable descriptor limits。排序与 OpenRouter 歧义规则
沿用 ADR 0017。

### 5. Conformance-only endpoint override

共同 runner 通过 `AGENT_PROVIDER_ROUTE_CONFORMANCE_CONFIG` 指向一个严格、只读、
no-follow、大小有界的 JSON 文件。文件只包含 `providerId`、`apiFamily`、`modelId` 与
`endpointBase`，不包含 credential、credential 名、adapter、header 或 quirk。

该配置只有在 daemon 同时显式启用一般 conformance 与
`QXNM_FORGE_PROVIDER_CONFORMANCE=1` 时可读取。`endpointBase` 必须是精确 literal
`http://127.0.0.1:<port>/...`，无 userinfo、query、fragment、反斜杠、编码 authority
delimiter 或非 loopback host。配置只把 snapshot 收窄为一条 route 和一个 catalog 模型，
并替换其 endpoint base；它不能创造 manifest 外 route/model，也不能改变 credential
source、adapter 或能力。

生产模式只要看到该 presence 就必须在读取配置文件或 credential 前固定脱敏拒绝。
unknown field、duplicate key、非法 UTF-8、尾随 JSON、symlink、非普通文件和超限输入
全部 fail closed。

### 6. Registry 与路由

Registry 必须以 route key 存储可执行 adapter。省略 `apiFamily` 只在同一
`providerId + modelId` 恰好命中一个可用 route 时允许；零个或多个命中均返回
`provider_unavailable` 或既有歧义 `invalid_params`，不能按注册顺序猜测。

任何 wrong family、unknown model、缺 credential、未入本批次 allowlist 或 presence
非法的拒绝都发生在 Session 创建、`run.accepted`、用户消息和 Provider attempt durable
写入之前。

### 7. 共同门禁

`provider_route_runner.py` 使用本机回环 mock 和合成 credential，逐条执行：

```text
initialize -> models/list -> run/start(providerId, modelId, apiFamily)
           -> family-native HTTP request -> normalized terminal event
```

它验证六条 happy path，以及 missing credential、wrong family、unknown model、未广告
route、生产 presence、strict config/wire 负例。每案使用 fresh daemon/workspace/state，
清除真实 Provider/cloud 配置，设置 dead proxy，限制 argv/stdin/stdout/stderr/总 deadline，
并扫描协议、stderr、Session、artifact 与 mock observation 中不存在 credential canary、
配置路径或原始 endpoint。

共同 runner 还必须独立执行四项普通生产门禁，且不改变六 route/九负例 fixture census：

1. 不设置 route/identity presence 或 conformance，只设置六个合成 credential；同一进程的
   `initialize` 与六次逐 Provider `models/list` 必须精确广告七个 Provider（含 `faux`）、
   134 个 Provider 模型 ID（含 `faux-v1`）以及六 route 的 133 个 catalog descriptor；
2. 只设置 allowlist 外 Azure/Anthropic credential 与 endpoint 配置时必须只广告 `faux`；
3. route/identity 双 presence 即使都指向不存在的 canary 路径，也必须在读取任一路径前拒绝；
4. route presence 环境项存在但值为空时必须在启动前拒绝，不能降级视为 absent。

四项均使用 fresh daemon/workspace/state、合成 canary 与 loopback network tripwire；不得发出
HTTP 连接、创建 Session，或在协议、stderr、workspace/state 中泄漏 credential、endpoint
或 presence 路径。动态成功摘要固定包含
`6 positives / 9 negatives / 4 production gates`。

文本 route 复用既有 family mock 的 `text_success` 原生 wire；图像 route 复用
OpenRouter Images artifact-first happy path。静态门禁还逐字段核对六条 route 与冻结
manifest/catalog，并证明其 quirk/header/auth/endpoint 条件仍符合本 ADR。

`CONFORMANCE/fixtures/golden/provider-route-groq.requests.ndjson` 与
`provider-route-groq.trace.ndjson` 额外冻结 Groq 代表路径的最小规范化协议投影：
`initialize -> models/list -> run/start -> run.completed`。golden 只表达 runner 的本机
回环 synthetic credential 场景，不携带 credential、配置路径、endpoint、header 或 HTTP
body；静态 gate 必须交叉核对三元 route 身份、descriptor、文本与 usage，并在替换标准
实现/时间占位符后通过协议生命周期和 Draft 2020-12 Schema。

通过 family mock 但没有通过本 runner，只能保持 API-family claim；不能形成 Provider
品牌 claim。通过本 runner后可把对应 Provider feature 标为 `conformant`，但
`live_smoke` 仍为 `unsupported`，不得称为 `live-verified`。

Provider 品牌能力矩阵 target 必须使用
`(implementation, providerId, apiFamily, feature)` 作为唯一身份，不能只用
`providerId + feature`。这保证 OpenRouter 的 `openrouter-images` 离线证据不会被误读为
同品牌 `openai-completions` route 已受支持，也让其余 39 条 route 继续保持
`unsupported`。

## 后果

- 五语言获得同一条从 manifest/catalog 到 canonical Provider 执行的最小可信 spine。
- 六个 Provider 的离线 conformance 不外推到同 Provider 的其它 family 或未覆盖 quirk。
- 后续 endpoint/header/quirk 与复杂凭据链可在不改 route/model identity 的前提下分批加入。
- 默认 CI 仍不接触付费模型；真实 smoke 必须显式启用并独立记录。
