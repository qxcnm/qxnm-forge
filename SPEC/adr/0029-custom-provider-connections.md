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
`modelsUrl`、显式 `supportsTools`、`supportsImageInput`、`supportsImageOutput` 和最多
512 项的模型 allowlist；尚未发现模型
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
`ProviderCredentialStore` v0.2 逐叶目录；非敏感连接使用严格、有界、原子发布的状态文档。只有
`enabled:true`、配置有效且 credential presence 元数据安全的连接才能在 daemon 启动快照中注册；
列表、presence 和 capability 投影不得打开 secret 正文，最终 Provider 请求才读取精确目标叶。
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
function-tool 映射与续轮实现。未开启时 Agent 发给 Provider 的工具集合必须为空，不得仅因 family
adapter 支持工具而扩大请求；`reasoning` 仍固定为 false。

图片能力同样是显式、逐方向且 fail-closed 的声明。`supportsImageInput:true` 只允许 Agent 把同
Session 已发布并重新校验的 `image_ref` 映射为 request-local data URL：Responses 使用
`input_image.image_url`，Chat Completions 使用 `image_url.url`。`supportsImageOutput:true` 还必须
存在独立 Image credential 才能在 descriptor 的 `capabilities.output` 中广告 `image`；缺少该
credential 时连接仍可用 Responses credential 提供文字与已声明的图片输入，绝不回退或复用该
Responses key 作为 Image key。只有声明与独立 Image credential 同时成立才构成“有效图片输出”；
此时 descriptor 的 `tools` 必须强制为 false，Agent 发出的 function tools 必须为空，即使持久化
`supportsTools` 为 true 也不能把 function tools 与图片生成混在同一次请求。该收窄不修改持久化
声明；移除 Image credential 并重启快照后，图片输出消失，原 `supportsTools` 声明重新决定工具能力。

启用图片输出后该 route 固定切换为有界非流式响应。Responses 只解析
`image_generation_call.result` 的 canonical PNG Base64；Chat 只解析
`message.images[].image_url.url` 的 canonical PNG/JPEG/WebP/GIF data URL。最多八张且解码总计
4 MiB，完整响应、所有图片的 Base64、MIME 与魔数必须整批通过后，Agent 才能把字节发布为
durable Session artifact 并在 assistant message 中写 `image_ref`。远程 URL、部分有效批次、未知
MIME 和超限结果均产生脱敏失败；Provider data URL、图片正文和宿主路径不进入 Session 消息、
事件、日志或错误。有效图片输出的 Responses 请求必须使用 `stream:false` 和精确
`tools:[{"type":"image_generation"}]`；Chat Completions 必须使用 `stream:false` 和精确
`modalities:["text","image"]`。两种请求都不得携带 function-tool 定义。Responses 非流式对象
还必须有非空 `id`、顶层 `status:"completed"` 和 `output` 数组；只有完整 completed 对象才能产生
`stop`。Chat 响应一旦包含图片，原始 `finish_reason` 必须精确为 `"stop"`；纯文本完成继续使用既有
Chat finish-reason 归一化规则。

非敏感连接文档当前版本为 `0.3`。读取 `0.1` 时先执行既有 `modelsUrl`/`supportsTools` 保守迁移，
读取 `0.1` 或 `0.2` 时都把两项图片能力初始化为 false，再在下一次 mutation 原子写回 `0.3`；
不得根据模型名称、endpoint 或已有 credential 自动开启。未知未来版本继续失败关闭。该迁移只改变
非敏感配置，不读取、复制或改名任何 credential identity。

界面的“New API”只是导入 preset：`_type:"newapi_channel_conn"` 可映射到上述普通 DTO，
不会进入 wire-format 分支或新增私有 adapter。提供商 Logo 只引用受信任的本地 asset ID；
不得把任意远程 URL 注入 WebView CSP。

## 安全与测试

输入严格拒绝未知字段、重复模型、非 HTTPS、userinfo、query、fragment、CR/LF credential
和过期 revision。默认测试只使用 faux 或 loopback mock；不得隐式访问用户配置的服务。
测试必须扫描 stdout、stderr、Session、非敏感配置与响应，确认 canary credential 零泄漏。
图片测试还必须覆盖两种 family 的输入映射、有效文字/图片非流式完成、远程 URL、坏 Base64、
MIME/魔数错配、数量/总字节上限，以及缺 Image key 时零图片输出广告且不回退 Responses key。
Windows/macOS 在 OS keychain 后端通过独立门禁前，只能声明“权限受限 CredentialStore”，
不能声称系统密钥链保护。
