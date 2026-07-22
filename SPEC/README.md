# qxnm-forge core specification v0.2

Status: **draft**  
Protocol version: `0.1`  
Session format version: `0.1`  
Application database logical version: `0.2`
Agent Profile contract version: `0.2`
Author: 高宏顺 `<18272669457@163.com>`  
Reference snapshot: PI commit `3f9aa5d10b35223abf6146f960ff5cb5c68053ee` (MIT, evidence only)

This directory is the language-neutral source of truth for the two active
qxnm-forge implementations. Rust and .NET implement the same observable semantics
independently. No implementation is the implicit standard, including Rust.

本目录是 Rust 与 .NET 两种 active 原生实现的唯一公共语义来源。Go、TypeScript、Python
只保留历史快照说明，不进入 vNext 门禁。任何公共行为变更都必须先修改规范、ADR
和 golden trace，再修改实现；某个实现中未记录的行为不会自动成为标准。

## Normative language

The key words **MUST**, **MUST NOT**, **REQUIRED**, **SHOULD**, **SHOULD NOT**,
and **MAY** are to be interpreted as described by RFC 2119 and RFC 8174.
Chinese explanatory text is informative unless it repeats an English
requirement containing one of those key words.

## Documents

- [sponsored-provider-catalog.md](sponsored-provider-catalog.md) — Rust/.NET 共享的签名远程推广目录、返佣披露、回滚和缓存边界。
- [provider-commercial-state.md](provider-commercial-state.md) — 用户明确确认的本地推广 route 与工作区外 CredentialStore。
- [storage.md](storage.md) — 默认 SQLite、SeaORM/EF Core、远程 provider 和 migration 安全边界。
- [ui.md](ui.md) — 未来 TypeScript + React 客户端与 application service 的权限边界。
- [agent-profile.schema.json](schemas/agent-profile.schema.json) — 品牌中立 Profile 输入、实体、
  revision 引用与 durable run snapshot。
- [architecture.md](architecture.md) — component boundaries and native API
  requirements.
- [protocol.md](protocol.md) — JSON-RPC 2.0 message semantics over UTF-8 NDJSON.
- [agent-state-machine.md](agent-state-machine.md) — run, turn, message, tool,
  queue, cancellation, and retry ordering.
- [session.md](session.md) — portable append-only JSONL session tree/journal.
- [executor.md](executor.md) — file, process, shell, terminal, output, and
  project-profile contracts.
- [security.md](security.md) — threat model, approval policy, and sandbox
  terminology.
- [cli.md](cli.md) — daemon and pure-text Coding Agent CLI behavior.
- [compatibility.md](compatibility.md) — protocol/session evolution and the
  support-level contract.
- [provider-families.md](provider-families.md) — API-family adapter、faux
  Provider、identity-only 广告与首批六条 canonical executable route 语义。
- [providers.v1.json](providers.v1.json) — v0.2 route manifest for the frozen
  v1 Provider/API-family scope.
- [models.v1.json](models.v1.json) — frozen, attributed PI model evidence
  snapshot; catalog presence is not a support claim.
- [capabilities.json](capabilities.json) — sparse machine-readable support
  matrix；Provider claim 以 `(providerId, apiFamily, feature)` 为 identity，省略的
  Cartesian cell 均为 `unsupported`。
- [language-profiles.json](language-profiles.json) — project detection and
  toolchain operations, independent of implementation language.
- [schemas/](schemas/) — JSON Schema 2020-12 wire and journal definitions.
- [adr/](adr/) — architecture decision records.

Community 发行许可由 [ADR 0027](adr/0027-community-noncommercial-license.md)
冻结为 PolyForm Noncommercial 1.0.0；源码公开不代表授权商业使用。历史参考资料中的
`MIT` 是第三方来源归属，不是本项目 Community 发行许可。

Provider Route Spine 的当前公共决策是
[ADR 0018](adr/0018-manifest-driven-provider-route-spine.md)：只冻结六条 route、133 个
catalog allowlist 模型、生产 credential 环境名称、late-read 边界和 conformance-only
loopback override。其余 39 条 route 保持 fail closed；离线 mock conformance 不等于
`live-verified`，也不会自动修改能力矩阵。

Session journal 尾部恢复的公共决策是
[ADR 0020](adr/0020-session-journal-tail-recovery.md)：v0.1 没有独立 durability witness，
所以任何无 LF 尾行都未提交；修复使用 brand-neutral
`org.agent-session.recovery` extension 和内容寻址 backup。任何 LF 完整坏行都以
`journal_corrupt` 零修改失败，不能伪装成 torn tail。该门禁本身不修改 capability 状态。

Linux 文件路径竞态的公共决策是
[ADR 0021](adr/0021-linux-file-path-race-conformance.md)：`file.read`/`file.write` 从启动期
固定 workspace root handle 逐组件 no-follow 访问，通过 root、nested parent、leaf 的
六个确定性 rebind 案例和两项生产缺门门禁形成窄化证据。该证据不外推到 Windows、其他
文件工具、process cwd 或 mount change，`security.path_boundary` 因而继续保持
`implemented`，不能仅凭 Linux `6+2` 门禁登记为 `conformant`。

Agent Profile 的公共决策是
[ADR 0028](adr/0028-brand-neutral-agent-profiles.md)：application service 提供封闭 CAS CRUD，
`run/start` 使用精确 `(profileId,revision)` 并在 `run.accepted` 持久化最小安全快照；Profile
永远只能收窄工具和审批。application database 以单事务从旧 metadata v0.1 迁至品牌中立
v0.2，portable Session 格式版本仍独立保持 0.1。

Provider 设置边界由 [ADR 0029](adr/0029-custom-provider-connections.md) 与
[ADR 0031](adr/0031-brand-neutral-provider-template-catalog.md) 共同约束：连接配置与 credential
分离，`providerCatalog/list` 只返回 20 项冻结派生模板和 3 项 ADR 封闭列出的官方兼容模板。
这些非敏感配置建议不构成已配置、可执行或远端验证声明；只有重启后真实出现在 `models/list` 的
完整路由才能用于 `run/start`。

## Conformance contract

An implementation may advertise a public feature only at status `conformant`
or `live-verified` in the shared capability matrix. `implemented` means code
exists but is not public support. Default CI MUST use the deterministic faux
provider, mock transports, and redacted fixtures; live paid-provider tests MUST
be separately enabled.

The minimum v0.1 black-box path is:

1. client request `initialize`;
2. in conformance mode only, client request `faux/configure`;
3. client request `run/start`, whose result is `{ "runId": "..." }`;
4. server notifications with method `event`;
5. exactly one terminal event: `run.completed`, `run.failed`,
   `run.cancelled`, or `run.interrupted`.

The `run/start` response MUST be emitted before any event for that run and only
after the accepted input and run record are durably appended.

Agent Profile 静态 contract 由
`CONFORMANCE/fixtures/agent-profile/profile-cases.json` 与
`CONFORMANCE/tests/test_spec_schemas.py` 验证；动态 CRUD/migration/run-binding runner 未通过前，
相关能力最多为 `implemented`。

## Source documentation rule

Every newly added source-code method or function in either active
implementations MUST have a method-level documentation comment containing:

1. a concise functional description, including important inputs, outputs,
   side effects, cancellation, or error behavior where applicable;
2. author: `高宏顺`;
3. email: `18272669457@163.com`.

Documentation syntax follows the language (`///`, doc comments, docstrings,
JSDoc, or Go doc comments), but all three items are REQUIRED. Generated code
may use a generator-level declaration only when the generated file is clearly
marked and the generator emits equivalent API documentation.

中文规则：所有新增方法都必须写方法级注释，明确功能，并包含作者“高宏顺”和邮箱
`18272669457@163.com`；不能只在文件头统一写作者而省略方法注释。

## Scope of v0.2

v0.2 保留 protocol/session v0.1，新增 application database 与 Agent Profile v0.2 contract，
并允许 React/Tauri faux 交互预览。生产 UI transport、认证 HTTP/WebSocket、远程 event replay
仍未由本规范声明完成。所有 UI 都必须消费 application service，MUST NOT 直接执行工具、
持有 secret、打开 SQLite 或修改 Session journal。
