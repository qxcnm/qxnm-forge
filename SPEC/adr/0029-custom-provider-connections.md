# ADR 0029：自定义 Provider Connection 与只写 Credential 边界

- 状态：Accepted
- 日期：2026-07-21
- 项目：qxnm-forge
- 作者：高宏顺 `<18272669457@163.com>`

## 背景

桌面工作台需要配置 New API 等 OpenAI-compatible 服务。把 endpoint 和 API key 混成一段
前端配置会让 secret 进入 Query cache、浏览器存储、日志或 Session；复用推广 Provider
安装格式又会错误引入目录签名、返佣和固定 `relay-*` 身份。因此，自定义连接必须成为独立、
品牌中立的 application-service 资源。

## 决策

冻结以下非敏感 RPC：

- `providerConnections/list {}`；
- `providerConnections/create {connection}`；
- `providerConnections/update {connectionId,expectedRevision,connection}`；
- `providerConnections/delete {connectionId,expectedRevision}`。

DTO 由 `custom-provider-connection.schema.json` 定义。首版只允许
`apiFamily:"openai-completions"`、HTTPS base URL 和显式非空模型 allowlist；URL 不得携带
userinfo、query 或 fragment。`connectionId` 由服务生成，mutation 使用 revision CAS。列表只
返回 `credentialConfigured:Boolean`，绝不返回、摘要或部分回显 credential。

自定义 `providerId` 使用 schema 冻结的小写 ASCII、数字、点号与连字符语法，但不得等于
`faux`、不得使用保留的 `relay-*` 前缀，也不得遮蔽 `providers.v1.json` 当前任一 canonical
Provider ID。读取旧文档时也必须执行同一检查，manifest 新增的冲突身份在升级后失败关闭。

Credential 写入和删除使用 `providerCredentials/set|remove` 的本地管理边界。桌面 WebView
不能通过通用方法转发器调用它们，只能调用固定的 Tauri command；host 固定 daemon、state
root 与 workspace，值只存在于本次内存/本地管道，响应仅返回状态。浏览器预览不保存值，
远程移动端需要另行定义认证、TLS、`no-store` 且无访问日志的管理 endpoint。

连接配置与 credential 分开持久化。credential 继续使用工作区外的
`ProviderCredentialStore`；非敏感连接使用严格、有界、原子发布的状态文档。只有
`enabled:true`、配置有效且 credential 当前可读的连接才能在 daemon 启动快照中注册。
adapter、`models/list` 与 `initialize.capabilities.providers` 必须来自同一快照。mutation 成功
返回 `restartRequired:true`，桌面 host 丢弃旧 daemon 并重新握手，不能假装热更新。
创建连接必须在固定 `connection -> credential` 锁顺序内先删除相同 Provider ID 的历史
orphan credential；Provider ID 改名必须先清目标 ID、再清旧 ID。旧 secret 不得因 CLI
遗留状态或配置重建而隐式绑定到新的 endpoint。

首版 DTO 没有模型 capability 声明字段，因此只广告有实现证据的 text input/output 与
streaming；`tools` 和 `reasoning` 必须为 false。未来需要工具调用时应先扩展规范，让用户对
具体连接显式声明且由 adapter 测试验证，不能仅因接口兼容 OpenAI Chat 就推断模型能力。

界面的“New API”只是导入 preset：`_type:"newapi_channel_conn"` 可映射到上述普通 DTO，
不会进入 wire-format 分支或新增私有 adapter。提供商 Logo 只引用受信任的本地 asset ID；
不得把任意远程 URL 注入 WebView CSP。

## 安全与测试

输入严格拒绝未知字段、重复模型、非 HTTPS、userinfo、query、fragment、CR/LF credential
和过期 revision。默认测试只使用 faux 或 loopback mock；不得隐式访问用户配置的服务。
测试必须扫描 stdout、stderr、Session、非敏感配置与响应，确认 canary credential 零泄漏。
Windows/macOS 在 OS keychain 后端通过独立门禁前，只能声明“权限受限 CredentialStore”，
不能声称系统密钥链保护。
