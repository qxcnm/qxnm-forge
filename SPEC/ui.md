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

UI 可以在同源浏览器存储中保存闭合的非敏感界面偏好，包括后端选择、输入快捷键、导航宽度和减少动态效果。持久化 key 必须保持品牌中立，并使用显式版本；该快照不得包含 Provider credential、连接导入原文、Session、消息、审批、工具授权、Agent Profile、宿主路径或远程 endpoint。损坏、未知版本或存储不可用时必须回退安全默认值，不能阻断 application service 初始化。

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

Provider 设置遵循 ADR 0029。连接配置与 credential 必须分两次提交；credential 输入只能保留
在密码框组件的局部状态，提交后立即清空，不得进入 TanStack Query cache、Zustand、
`localStorage`、错误消息或分析事件。`providerConnections/list` 只返回
`credentialConfigured`。界面可以把受支持的第三方 JSON 映射成普通输入 DTO，但不得保存
原始 JSON 或把 `_type` 当成新的 transport 权限。

会话列表、归档、恢复和删除只能调用 application service。React 不得枚举 Session 目录、
读取 journal 或删除 artifact。删除使用明确确认；服务未广告对应方法时，界面展示不可用状态，
不能仅从列表移除来伪装持久化成功。

插件/Computer 页面是 capability 投影，不是 WebView 插件 runtime。只有
`initialize.capabilities.tools` 实际包含 `computer.*` 且后端提供对应权限/审批时，界面才能
显示可启用控制；否则必须明确显示“不可用”。React 不能加载插件提供的 JS/HTML，也不能因
展示插件卡片而追加工具 ID。动态扩展的安装、签名、隔离与工具目录留待后续 ADR。

## 高定制化

前端应以设计 token、主题包、布局槽位和 renderer registry 提供定制能力。自定义 renderer 只能处理经过 schema 验证的 view model；不得取得 secret、宿主文件句柄或任意后端对象。新增 command、tool 或 Provider 必须经过后端 extension 权限协议注册，不能由 React 插件绕开审批。

推荐技术基线：TypeScript strict mode、React、Vite、TanStack Query、Zustand（仅视图状态）、基于生成 schema type 的 API client。ORM 不属于前端依赖。

## Open/Pro 与部署

Community 后端返回公开 capability 集；Pro 后端在授权服务验证后追加 entitlement，不让 UI 自行计算试用时间或验证卡密。任何广告/推广位必须标注“推广/返佣”，远程目录只能改变候选内容，不能注入脚本、HTML 或自动启用 Provider。

浏览器部署必须增加 origin、CSRF、authentication 和 WebSocket session 绑定；在这些规则和共同 trace 未完成前，daemon 不得直接监听公网地址。
