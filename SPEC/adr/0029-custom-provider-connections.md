# ADR 0029：自定义 Provider Connection 与只写 Credential 边界

- 状态：Accepted
- 日期：2026-07-22
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
- `providerConnections/delete {connectionId,expectedRevision}`；
- `providerConnections/discoverModels {connectionId,expectedRevision}`。

DTO 由 `custom-provider-connection.schema.json` 定义。连接明确选择通用
`apiFamily:"openai-responses"|"openai-completions"`，不得借用 `codex-responses` 的登录、
endpoint、header 或任何 Codex 本机配置。连接同时保存独立 HTTPS `baseUrl`、精确
`modelsUrl`、显式 `supportsTools` 和最多 512 项的模型 allowlist；尚未发现模型
时 allowlist 可以为空，此时连接不得进入可执行快照。URL 不得携带 userinfo、query 或 fragment。
`connectionId` 由服务生成，mutation 使用 revision CAS。列表只
返回 `credentialConfigured:Boolean`（Responses/Completions key）与
`imageCredentialConfigured:Boolean`（Image key），绝不返回、摘要或部分回显 credential。

自定义 `providerId` 使用 schema 冻结的小写 ASCII、数字、点号与连字符语法，但不得等于
`faux`、不得使用保留的 `relay-*` 前缀，也不得遮蔽 `providers.v1.json` 当前任一 canonical
Provider ID。读取旧文档时也必须执行同一检查，manifest 新增的冲突身份在升级后失败关闭。

Credential 写入和删除使用带 `credentialKind:"responses"|"image"` 的
`providerCredentials/set|remove` 本地管理边界。两类 key 必须使用独立 CredentialStore identity，
模型发现和文本 route 只能读取 `responses` key；Image key 不得回退到 Responses key，也不得复用
Codex 或环境中的其它 secret。桌面 WebView
不能通过通用方法转发器调用它们，只能调用固定的 Tauri command；host 固定 daemon、state
root 与 workspace，值只存在于本次内存/本地管道，响应仅返回状态。浏览器预览不保存值，
远程移动端需要另行定义认证、TLS、`no-store` 且无访问日志的管理 endpoint。

连接配置与 credential 分开持久化。credential 继续使用工作区外的
`ProviderCredentialStore`；非敏感连接使用严格、有界、原子发布的状态文档。只有
`enabled:true`、配置有效且 credential 当前可读的连接才能在 daemon 启动快照中注册。
adapter、`models/list` 与 `initialize.capabilities.providers` 必须来自同一快照。mutation 成功
返回 `restartRequired:true`，桌面 host 不能假装热更新。Rust 与 .NET daemon 指向同一品牌中立
state root；任一 Provider connection 或 credential mutation 一旦发出，host 必须与两套 daemon
的在途请求串行化，并在成功、错误、超时或连接丢失后同时丢弃两套旧 daemon。下一次调用必须
重新启动所选实现并完成握手，避免响应丢失后复用可能已经过期的 endpoint、credential 或模型快照。
创建连接必须在固定 `connection -> credential` 锁顺序内先删除相同 Provider ID 的历史
orphan credential；Provider ID 改名必须先清目标 ID、再清旧 ID。旧 secret 不得因 CLI
遗留状态或配置重建而隐式绑定到新的 endpoint。

模型发现是用户显式触发的有副作用管理操作，不改变 `models/list` 的零网络快照语义。服务以
`connectionId + expectedRevision` 读取精确连接，并只在最终请求边界从 CredentialStore 读取
该连接 Responses credential；随后向连接中保存的精确 `modelsUrl` 发送一个 Bearer 认证的 GET，
不得再从 `baseUrl` 猜测或拼接模型路径。
请求总时限为 20 秒、禁止 redirect，响应正文最多 1 MiB，只接受至少包含
`{"data":[{"id":"..."}]}` 的 OpenAI-compatible 对象；必须拒绝重复 JSON key、非数组
`data`、非对象项和缺失/非字符串 `id`，但可以忽略且不得投影其它模型元数据。模型 ID 必须是
1..256 字节的无控制字符字符串，去重排序后最多
512 项且不能为空。成功后在同一连接锁和 revision CAS 下仅替换 `modelIds`，revision 精确加一，
返回 `{connection,discoveredCount,restartRequired:true}`。远端失败、畸形响应、空目录、并发
revision 变化或 CredentialStore 不可用时不得修改连接。错误、日志和响应不得包含 URL、响应
正文、credential、认证 header 或远端私有细节。

模型只广告有实现证据的 text input/output 与 streaming。`supportsTools` 默认 false；用户显式
开启后，模型 descriptor 的 `tools` 为 true，并使用已经通过共同 adapter 测试的 Chat/Responses
function-tool 映射与续轮实现。`reasoning` 仍固定为 false。未开启时不得仅因接口兼容而推断工具能力。

界面的“New API”只是导入 preset：`_type:"newapi_channel_conn"` 可映射到上述普通 DTO，
不会进入 wire-format 分支或新增私有 adapter。提供商 Logo 只引用受信任的本地 asset ID；
不得把任意远程 URL 注入 WebView CSP。

## 安全与测试

输入严格拒绝未知字段、重复模型、非 HTTPS、userinfo、query、fragment、CR/LF credential
和过期 revision。默认测试只使用 faux 或 loopback mock；不得隐式访问用户配置的服务。
测试必须扫描 stdout、stderr、Session、非敏感配置与响应，确认 canary credential 零泄漏。
Windows/macOS 在 OS keychain 后端通过独立门禁前，只能声明“权限受限 CredentialStore”，
不能声称系统密钥链保护。
