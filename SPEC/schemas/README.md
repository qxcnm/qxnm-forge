# qxnm-forge Schema catalog

All qxnm-forge wire and durable-record schemas use JSON Schema Draft 2020-12.
Author: 高宏顺 `<18272669457@163.com>`.

- `common.schema.json`: opaque IDs, UTC times, safe integers, hashes, and
  reverse-DNS extension namespaces.
- `domain/domain.schema.json`: aggregate portable domain object; sibling files
  provide the precise error, usage, artifact, content, message, model, tool,
  approval, and restricted tool-parameter schemas.
- `protocol/jsonrpc.schema.json`: request/response frames and exact method
  definitions.
- `protocol/event.schema.json`: common event envelope and type-specific data.
- `protocol/faux-scenario.schema.json`: deterministic conformance provider.
- `agent-profile.schema.json`：v0.2 封闭 Profile input/entity、CAS reference 与最小 run
  snapshot；不提供 secret、endpoint 或 extension 字段。
- `agent-profile-cases.schema.json`：共同 CRUD/run wire、portable error 与 capability 广告
  预期 fixture。
- `provider-mock-cases.schema.json`: two fixed local transport suites covering
  OpenAI-compatible Chat, OpenAI Responses, Anthropic Messages, Mistral
  Conversations, Azure OpenAI Responses, and Google Generative AI.
- `tool-approval-cases.schema.json`: governed safe file-tool, approval,
  persistence-order, cancellation, and no-replay cases.
- `session/journal.schema.json`: header and portable append-only records.
- `session/journal-tail-recovery-cases.schema.json`: ADR 0020 固定的两个
  no-LF repair、四个 LF-complete corruption 案例与 brand-neutral recovery 常量。
- `session/writer-lock.schema.json`: bounded cross-language writer owner using
  the fixed `tcp-loopback-writer-lease-v1` protocol and literal IPv4 loopback.
- `session/writer-lock-cases.schema.json`: the ordered live, ambiguous, stale,
  initialization, host-injection, takeover-race, and release-CAS lock cases.
- `session/lifecycle.schema.json`：ADR 0030 的脱敏 Session 摘要、分页 RPC DTO 与工作区外
  归档状态文档；永久删除的文件系统不变量由实现和 conformance 共同验证。
- `executor.schema.json`: `process.exec` and `shell.exec` DTOs.
- `provider-manifest.schema.json`: closed Provider route metadata including
  adapters, auth environment names, endpoint bindings, model-catalog and
  header-policy references, and compatibility quirks.
- `model-catalog.schema.json`: the attributed 1,076-row text/image evidence
  snapshot with constrained endpoints, capabilities, limits, fixed headers,
  and compatibility values.
- `provider-identity-config.schema.json`：品牌中立、无值的离线 presence
  配置；只表达 adapter、认证、endpoint 配置和能力判定是否存在，不允许携带
  credential 或 endpoint 值。
- `provider-identity-cases.schema.json`：35 Provider、45 route 的广告/模型
  过滤案例、固定 census 及 wire/config strict-input 负例。
- `provider-route-config.schema.json`：ADR 0018 单 route、单模型、literal-loopback
  conformance override；禁止 credential、adapter、header 与 quirk 字段。
- `provider-route-cases.schema.json`：六条 canonical executable route、133 模型 census、
  descriptor、原生 request target 与九类失败关闭案例。
- `sponsored-provider-catalog.schema.json`：签名远程推广候选目录、source 与 trust key。
- `sponsored-provider-installation.schema.json`：用户明确确认后固定且不含 secret 的本地 route。
- `provider-credential-store.schema.json`：只允许工作区外敏感文件使用的 API-key store；
  不得把其实例放入 fixture、协议或 Session。
- `custom-provider-connection.schema.json`：ADR 0029 的非敏感自定义连接、CAS 与显式模型发现
  参数、credential 状态；连接实体、发现结果和列表永远不含 credential。
- `provider-catalog.schema.json`：ADR 0031 从冻结目录投影的兼容配置模板与空参数只读 RPC；
  模板不含 secret，也不构成 configured、executable 或远端 verified 声明。
- `storage-config.schema.json`：不携带远程连接串值的 SQLite/PostgreSQL/MySQL provider 配置。
- `language-profiles.schema.json`：共享项目检测与工具链操作元数据。
- `capability-matrix.schema.json`：稀疏支持矩阵；Provider target 必须同时包含
  `id`（语义为 `providerId`）、`apiFamily` 与 `feature`，以 route-scoped identity 防止
  sibling family 证据被合并。

Schemas reject unknown core fields. Arbitrary values occur only at explicit
boundaries: validated tool arguments and reverse-DNS extension values. A schema
file being valid does not prove cross-record invariants. Conformance additionally
checks unique provider/model IDs; manifest cross-references, HTTPS/template
boundaries and source counts; matrix claim uniqueness/scope membership; token
totals; journal sequence, IDs, parents and state transitions; event sequence and
terminal uniqueness; tagged-union discriminator coverage; operation hashes;
artifact length/hash; and frame byte limits.

JSON Schema `format` assertions such as `date-time` MUST be enabled in validators.
Implementations MUST resolve only the bundled schema IDs and MUST NOT retrieve a
schema from the network at runtime.
