# TypeScript + React UI 边界规范 v0.2

状态：Draft  
作者：高宏顺 `<18272669457@163.com>`

## 决策

未来 QXNM Forge Web/Desktop/Mobile UI 使用 TypeScript + React。UI 是客户端，不是第三套 Agent runtime：它 MUST 通过品牌中立 JSON-RPC/HTTP application service 使用 Rust 或 .NET 后端，MUST NOT 直接读取 SQLite、portable Session、CredentialStore 或许可证文件。

Open UI 代码位于本发行的 `ui/`；Pro UI 可以增加收费页面和 entitlement 展示，但公共组件、wire DTO 与主题 token 保持兼容。浏览器默认使用 faux 交互预览，Tauri 桌面壳通过受限 bridge 连接所选 Rust 或 .NET application service；任何入口仍必须按真实 capability 广告启用，不能把浏览器预览状态冒充生产持久化结果。

## Application service

UI transport 可以是 loopback HTTP + WebSocket，也可以桥接现有 NDJSON JSON-RPC，但必须复用同一方法和事件语义。后端负责：

- Agent/Session 状态机、Provider 调用和工具审批；
- credential、数据库和许可证访问；
- 路径边界、项目 trust、取消和进程清理；
- 把用户可见错误脱敏后映射为稳定 wire error。

UI 只维护可丢弃的视图状态。刷新或重连后必须能从 application service 重建 transcript、pending approval、queue、usage、retry、compaction 和 entitlement 投影。

UI 可以在同源浏览器存储中保存闭合的非敏感界面偏好，包括后端选择、输入快捷键、导航宽度、减少动态效果、界面主题、界面语言和固定插件目录状态。持久化 key 必须保持品牌中立，并使用显式版本；该快照不得包含 Provider credential、连接导入原文、Session、消息、审批、工具授权、Agent Profile、宿主路径、远程 endpoint 或插件代码。损坏、未知版本或存储不可用时必须回退安全默认值，不能阻断 application service 初始化。

主题使用 `light|dark|system` 三态，语言首版使用 `zh-CN|en-US`；二者分别保存在
`agent-client.interface-theme.v1` 与 `agent-client.interface-language.v1`。切换必须即时更新实际
配色或可见文案，语言同时同步 `html.lang`。用户消息、Session 标题、Provider 名称及其他用户
提供内容保持原文，不得被当成界面资源自动翻译。

模型发现必须以当前后端一次成功的 `initialize` 为前置条件。只有
`initialize.capabilities.methods` 广告 `models/list` 后，UI 才能请求或刷新模型目录；未广告、
初始化失败或切换 Rust/.NET 时不得沿用另一后端的目录与选择。Provider 连接、启用状态或
credential 状态变更后，UI 必须重新取得对应后端的 capability 与模型快照。目录项和选择值均
使用完整 `(providerId, modelId, apiFamily)` identity，同名 `modelId` 不能合并；所选路由从最新
快照消失时必须回退到快照中的有效路由或不可提交状态，不能猜测 Provider 或 API family。
重新握手与新代际查询尚未完成时必须禁用提交，但可以保留上一次用户选择的非敏感 identity
作为待校验偏好；只有新模型和 Agent 快照稳定后才能确认保留或清除，不能因暂时 loading 把
有效选择改回第一项，也不能把失效 Agent 静默重绑。

普通浏览器 faux 目录仅用于零网络交互预览：除固定公开 faux 路由外，只投影当前后端隔离状态
中 `enabled=true` 且 Responses `credentialConfigured=true` 的自定义 Provider 所声明模型。该投影不读取
credential 正文、不探测远程 endpoint，也不证明模型可访问、余额充足或生产 Provider 已配置；
预览 `run/start` 仍必须拒绝不在当前完整三元路由快照中的选择。

## 自定义智能体与当前交互预览

当前 Community UI 只通过前端进程内的 `FauxAgentProfileService` 预览自定义智能体交互。该服务仅提供内存中的列表、创建、更新和删除；“复制”是使用新 identity 的创建操作。页面刷新、WebView 重载或应用重启后必须回到固定 faux 种子数据，不得写入 `localStorage`、IndexedDB、SQLite、Tauri store、Session journal 或任何宿主文件。

预览 profile 可以包含名称、描述、system instructions、回复风格、plan-first/auto-review 等视图行为、工具 allowlist、审批模式，以及完整模型 identity `(providerId, modelId, apiFamily)`。`modelId` 不是独立 identity，UI 不得丢弃或猜测 `providerId` 与 `apiFamily`。在 faux 会话中选择 profile 只会改变视图投影，不代表后端 Agent 已消费这些字段。

生产 application service 使用 ADR 0028、`agent-profile.schema.json` 与
`agentProfiles/list|create|update|delete`，并通过 `run/start.agentProfile {profileId,revision}`
绑定本 run；禁止使用 `extensions` 夹带 Profile。UI 必须按 `initialize.capabilities.methods`
启用生产入口，保存和删除携带最近读取的 `expectedRevision`，冲突后重新 list，绝不能静默
覆盖。当前 `FauxAgentProfileService` 仍只是独立内存预览，不得伪装为这些 RPC 或持久化能力。

预览和未来生产实现都必须保持以下安全不变量：

- 有效工具集只能从 `profile.requestedToolIds ∩ initialize.capabilities.tools` 开始，并继续受后端当前 policy、project trust 与运行级 capability 约束；profile 只能收紧，不能授权。
- “标准审批”或“只读”只是期望的收紧模式。写入、编辑、进程、shell、terminal、网络、凭据和路径权限仍由后端审批与边界校验；前端不得缓存通用放行决定或降级后端 policy。
- profile 不得保存 Provider secret、OAuth token、真实卡密、数据库连接串、私有 endpoint、真实宿主路径或待执行命令。完整模型 identity 也不是 credential。
- Android 生产客户端只能连接经过认证和加密的远程 HTTPS/WebSocket application service，不得启动 Rust/.NET CLI sidecar，不得向 WebView 暴露本地 shell、terminal 或宿主 workspace。服务端必须把 Android 传入的 profile 视为不可信请求，在每次运行重新校验模型 identity、工具交集、审批、workspace 边界与授权；移动凭据只能在最终使用边界从平台 Keystore 获取。
- Community faux 数据只能引用公开 capability 和脱敏模型标识，不得出现 Pro entitlement、私有 endpoint、收费 fixture 或授权绕过逻辑。

## Provider 设置、Session 与插件状态

Composer 必须同时处理文件选择与 clipboard `files/items`。受支持图片先经
`artifacts/create` durable 发布，再以同 Session `image_ref` 进入 `run/start`；受支持文本
文件在客户端按 UTF-8 解码为附加 `text` 块。附件需要可见名称、类型、大小与移除操作；不支持
的二进制文件、超限文件、伪造图片 MIME 或上传失败必须明确拒绝，不能静默退化成路径或文件名文本。

Provider 设置遵循 ADR 0029。连接配置与 credential 必须分两次提交；credential 输入只能保留
在密码框组件的局部状态，提交后立即清空，不得进入 TanStack Query cache、Zustand、
`localStorage`、错误消息或分析事件。`providerConnections/list` 只返回
Responses/Image 两个 credential presence 布尔值。界面可以把受支持的第三方 JSON 映射成普通输入 DTO，但不得保存
原始 JSON 或把 `_type` 当成新的 transport 权限。
当 capability 广告 `providerCatalog/list` 时，UI 应从该只读 RPC 展示兼容配置模板，不能再维护
独立 endpoint preset。模板必须明确标为配置建议；选择模板只预填非敏感字段，不能自动创建连接、
写入 credential、发起模型发现或进入 `models/list`。`modelDiscovery:"openai-models"` 只表示后续
可以尝试标准发现，不能显示为已验证或在线；发现失败仍须允许手工模型 ID。
目录中的 OpenAI、Anthropic Claude 与 Google Gemini 是 ADR 0031 锁定的官方兼容入口，UI 仍须
原样使用其 `custom-*` 建议身份与 `openai-completions` family，不得将其展示成 canonical native
route、复制冻结模型或推断品牌专用认证能力。
界面必须分别提供 Responses 与 Image Key 输入，并提供可编辑的精确 `modelsUrl`，不得读取或复用
Codex 的配置。工具能力必须来自连接的显式 `supportsTools` 与 daemon 广告交集。
当 capability 广告 `providerConnections/discoverModels` 时，UI 可以提供明确的“获取模型”命令；
保存按钮只有在文案明确表示会连接 Provider 时才可串联触发该命令。发现请求必须引用最近读取的
`connectionId + revision`，不能由浏览器直接携带 credential 或绕过 application service 请求
远端。成功后先采用返回的新 revision，再重新 `initialize` 与 `models/list`；失败时保留现有
模型 allowlist，并提供手工模型 ID 回退，不能把猜测模型写入配置。普通浏览器 faux 预览不得
访问远端 `/models`。

会话列表、归档、恢复和删除只能调用 application service。React 不得枚举 Session 目录、
读取 journal 或删除 artifact。删除使用明确确认；服务未广告对应方法时，界面展示不可用状态，
不能仅从列表移除来伪装持久化成功。

`session/list.status` 只是从 durable journal 折叠的列表导航提示：侧栏只有在值严格为
`approval` 时显示待审批入口，值缺失、未知或摘要损坏时不能猜测。进入 Session 后，待审批状态必须
从 `session/get` 返回的 durable `approval.requested` 与后续
`approval.resolved` 事件重建，不能依赖组件内瞬时状态。UI 只投影结构完整的未决请求，并展示
服务端提供的 operation、risk、reason、resources、规范化 arguments、operation hash 与
expiry；损坏的请求必须 fail closed，并显示可重试的不可审批错误，不能静默隐藏；后续同 ID 的
resolution 必须移除该请求。待审批操作区在桌面和移动布局中必须保持可见或提供明确的定位命令，
不能因长 transcript 滚动位置让用户只看到侧栏状态而找不到决策按钮。审批卡的可选
决定只能来自请求自身 `choices` 中的闭合值 `allow_once|deny`，不能补全、改写或扩大授权；
v0.1 不提供会话级持续放行。只有当前 `initialize.capabilities.methods` 广告
`approval/respond`，且当前 Session 快照处于成功、空闲状态时这些按钮才可提交；未广告、快照
加载或读取失败、请求过期或正在提交时仍可展示 durable 请求，但必须明确不可响应并禁用操作，
不能用本地状态伪造决议。成功响应后必须重新取得 Session 快照，以 durable
`approval.resolved` 为最终状态。Tauri host 转发的 application-service event 必须经过当前后端、
JSON-RPC envelope 与事件基础不变量校验后，仅用于失效对应 Session 列表和 durable 快照；React
不能把未校验 event 直接渲染为审批。快照读取失败时必须提供显式只读重试入口，不能让侧栏长期
显示“待审批”而正文没有恢复审批按钮的路径。

插件市场是固定、有界目录、设备本地偏好与 capability 投影，不是 WebView 插件 runtime。
`agent-client.plugin-marketplace-preferences.v1` 只允许当前构建 catalog 内的 ID、安装状态和
启用状态；“安装”不得下载、解析或执行远程 JS/HTML，也不得写入工具授权。插件能力始终是其
声明需求与 `initialize.capabilities` 的交集。

ADR 0032 的 desktop computer 是默认关闭的 experimental/implemented Community 能力。WebView
不得设置、提升或伪造 `AGENT_CLIENT_DESKTOP_COMPUTER` 与
`AGENT_CLIENT_EXPERIMENTAL_DESKTOP_COMPUTER`；只有受信任 host/operator 配置与 daemon
原生 X11 探测可以决定是否广告工具。Wayland/XWayland、任一单门或未知平台必须显示不可用。

Computer Use 只有在后端实际包含三个 `computer.*` 工具、广告 `approval/respond` 与
`approval.requested|approval.resolved` durable 事件、application service 提供经过边界验证和
大小限制的 artifact 读取、UI 能渲染 durable `image_ref`，并且所选精确 Provider route 同时以
`conformant` 状态支持 `tools + image_input` 时才能标为就绪。工具、审批、artifact 或
Provider route 任一缺失都必须展示实际缺口，不能用插件安装状态补足。当前没有同时满足
`tools + image_input` 的 conformant route，也没有 artifact 读取/渲染闭环，因此 UI 必须明确
显示实验性未就绪，不能把尺寸/指针文本冒充截图。展示、安装或启用插件卡片都不能追加工具 ID。
远程目录、可执行插件包、签名、更新、隔离和工具注册留待后续 ADR。

## 高定制化

前端应以设计 token、主题包、布局槽位和 renderer registry 提供定制能力。自定义 renderer 只能处理经过 schema 验证的 view model；不得取得 secret、宿主文件句柄或任意后端对象。新增 command、tool 或 Provider 必须经过后端 extension 权限协议注册，不能由 React 插件绕开审批。

推荐技术基线：TypeScript strict mode、React、Vite、TanStack Query、Zustand（仅视图状态）、基于生成 schema type 的 API client。ORM 不属于前端依赖。

## Open/Pro 与部署

Community 后端返回公开 capability 集；Pro 后端在授权服务验证后追加 entitlement，不让 UI 自行计算试用时间或验证卡密。任何广告/推广位必须标注“推广/返佣”，远程目录只能改变候选内容，不能注入脚本、HTML 或自动启用 Provider。

浏览器部署必须增加 origin、CSRF、authentication 和 WebSocket session 绑定；在这些规则和共同 trace 未完成前，daemon 不得直接监听公网地址。
