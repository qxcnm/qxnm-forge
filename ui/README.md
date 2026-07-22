# QXNM Forge UI

QXNM Forge Community 的 TypeScript + React 工作台。界面参考 Codex 桌面应用的信息架构，使用 Vite、shadcn/ui、TanStack Query、Zustand 与 Tauri 2，工程目标覆盖 Web、Windows、macOS、Linux 和 Android。

## 当前能力

- 左侧工作区与真实 Session 导航、中央任务记录和底部消息输入器；浏览器 faux 模式另提供明确标记的预览变更抽屉。
- 可在 Rust 与 .NET 后端画像之间切换；功能显隐以后端 `initialize.capabilities` 为准，不按语言名称猜测。
- 模型目录只在当前后端成功 `initialize` 并广告 `models/list` 后加载；选择和运行始终携带完整 `(providerId, modelId, apiFamily)`，Provider 变更会刷新 capability 与模型快照。
- 自定义智能体管理支持创建、编辑、复制、删除与会话选择，可配置指令、模型、工具 allowlist、审批模式和回复行为。
- 设置页通过 `providerConnections/*` 管理不含 secret 的自定义 Provider，并用专用 Tauri command 写入或移除 CredentialStore 凭据；支持导入 `newapi_channel_conn` JSON，但不保存原始 JSON 或 Key。
- 最近任务和归档页通过 `session/list|archive|restore|delete` 操作真实服务状态，永久删除需要二次确认；消息通过 `session/get` 重建。
- 会话中的待审批卡从 `session/get` durable events 重建，展示结构化风险、资源、参数、hash 与有效期；只有后端广告 `approval/respond` 时才能按请求自身 choices 操作，v0.1 当前仅允许 `allow_once` 或 `deny`，不会自行增加会话级持续授权。
- 插件市场支持搜索、分类、详情和设备本地安装/卸载、启用/停用偏好；目录包含 Product Design、Computer Use、OpenAI Docs、Data Analytics 与 GitHub Workflow，页面只展示插件需求和后端 `initialize.capabilities` 的真实交集，运行启用还须通过目录 readiness policy。
- 界面支持 `light`、`dark` 与跟随系统三种主题，以及 `zh-CN`、`en-US` 两种界面语言；主题和语言分别只以品牌中立、显式版本化的 `agent-client.interface-theme.v1`、`agent-client.interface-language.v1` 保存在当前设备。Session 标题、用户消息、Provider 名称和智能体自定义内容属于用户数据，不做自动翻译。
- `ApplicationServiceClient` 是唯一生产后端服务边界。React 不读取 SQLite、Session journal、CredentialStore、许可证或宿主私有文件。
- 普通浏览器默认使用公开 faux 数据的零网络预览客户端，不会隐式调用付费 Provider；Tauri 桌面壳自动切换到真实 application service。
- shadcn/ui 组件由 CLI 生成并保存在 `src/components/ui/`；业务层不自行实现 Button、Select、Sheet、Tooltip 等基础组件。

当前 transport 边界分为三种：普通浏览器使用零网络 faux 预览；Windows、macOS 与 Linux 的 Tauri host 通过受限 NDJSON bridge 启动所选 Rust/.NET daemon；Android 预留经过认证的 HTTPS/WebSocket 远程 service，当前尚未实现该远程 transport。桌面 bridge 只转发固定 application-service allowlist，不向 WebView 暴露 executable、argv、状态目录、workspace 或通用 shell。

## 自定义智能体

桌面端通过 `agentProfiles/list|create|update|delete` 访问所选 daemon 的 `application.db`，使用 revision CAS 更新/删除，并在 `run/start` 只提交 `{profileId, revision}`。daemon 会重新验证完整模型三元组和工具子集，并把精确 `agentProfileSnapshot` 写入 `run.accepted` journal；切换 Rust/.NET 或重启应用后仍可读取同一品牌中立数据。普通浏览器仍由进程内 `FauxAgentProfileService` 提供内存 CRUD，刷新即重置且不读取数据库或宿主文件。

每个 profile 的模型始终使用完整 `(providerId, modelId, apiFamily)` identity，不能仅保存 `modelId` 或由 UI 猜测路由。工具选择只是对 `initialize.capabilities.tools` 的收紧 allowlist，审批选项也不能降低后端 policy。profile 不保存 secret、真实宿主路径或待执行命令。详细 Draft 边界见 [`SPEC/ui.md`](../SPEC/ui.md)。

## 模型与审批

模型查询以当前 Rust/.NET 后端的 `initialize` 结果为门禁：只有 capability 中存在 `models/list` 才请求目录。设置页创建、更新、删除 Provider，或者写入、移除 credential 后，会同时刷新连接、初始化结果与模型快照；因为两套实现共享同一 state root，桌面 host 会先阻止在途请求并同时关闭两套旧 daemon，切换后端也不会复用变更前的 endpoint 或模型快照。选择器 key 包含完整 Provider、模型与 API family，同名模型不会跨路由合并；重新握手期间会禁用提交并保留待校验选择，新模型与 Agent 快照稳定后才确认保留或清除，不会因为短暂 loading 跳回首个模型。切换到与当前 Agent 默认路由不同的模型会解除该 Agent 绑定，提交前仍以最新目录校验路由。

普通浏览器 faux 模式不会请求远程 Provider。它始终提供一个公开 faux 路由，并仅把当前后端状态中已经启用且 `credentialConfigured=true` 的自定义 Provider 模型投影到目录；该状态按 Rust/.NET 隔离，不读取 credential 正文，也不代表远程模型实际可用。

对话页通过 `session/get` 的 `approval.requested` / `approval.resolved` durable events 重建未决审批，而不是把审批保存在 React 状态中。审批卡只接受结构完整的请求，并按服务端 `choices` 原样显示 v0.1 的 `allow_once`、`deny`；不会自行增加 choices，也不提供会话级持续放行。只有 `initialize.capabilities.methods` 广告 `approval/respond` 且 Session 快照读取成功并处于空闲状态时按钮才可用；未广告、加载中、读取失败、过期或提交中的请求会保留可审计详情但禁止操作。Tauri 壳转发的 event 先校验后端和 JSON-RPC/event 基础不变量，再失效 Session 列表与 durable 快照；读取失败时正文提供显式重试。提交成功后重新读取 Session，以 durable resolution 为准。浏览器中的 `approval-flow` 只是确定性的内存案例，不执行工具、不写宿主文件，也不代表真实 daemon 能力。

## 插件市场

插件市场当前是固定、有界的 Community 目录和设备本地偏好界面，不是远程代码下载器。点击“安装”只会在 `agent-client.plugin-marketplace-preferences.v1` 保存目录 ID、安装状态和启用状态；读取和写入都限制为当前构建内的 catalog ID，不保存插件代码、credential、工具授权或 Provider 配置。

插件是否能在运行时生效还要与所选后端 `initialize.capabilities` 相交。页面会逐项展示已广告和缺失的中立工具、方法与事件 ID，启用偏好不能生成、补全或授权后端未广告的能力。Computer Use 当前固定为 `experimental_unavailable`：即使三个 `computer.*`、`approval/respond` 以及 `approval.requested` / `approval.resolved` durable 事件全部广告，页面仍只展示真实能力交集，不能启用或显示“后端可用”。原因会以中英双语明确显示：application service 尚无经过边界验证的 screenshot artifact 读取/渲染，且没有同一路由同时达到 conformant `tools + image_input` 的 Provider 视觉闭环。Agent Profile 的工具子集仍在独立工具配置中管理，并继续只能收紧后端广告集合。

## Provider 与会话

Provider 连接配置和 credential 是两个独立资源。React 只缓存脱敏连接投影；API Key 只短暂存在于密码输入状态，提交前即从表单清空，服务响应只返回 `credentialConfigured`。当前 Rust/.NET CredentialStore 是工作区外、权限受限的文件型实现，不宣称 Windows Credential Manager、macOS Keychain 或硬件加密保护。

Session 生命周期遵循 [`ADR 0030`](../SPEC/adr/0030-session-lifecycle-application-service.md)。列表分页结果不包含 transcript 或绝对路径；归档状态不改写 portable journal；删除由 daemon 在 writer 锁、路径边界与固定 tombstone 内完成，React 不直接操作宿主文件。

## 本地开发

```bash
cd open/ui
pnpm install
pnpm dev --host 0.0.0.0 --port 4173
```

质量检查：

```bash
pnpm typecheck
pnpm lint
pnpm test
pnpm build
```

## Windows、macOS 与 Linux

桌面开发通用前提是满足 Vite 7 要求的 Node.js、Corepack/pnpm 10.30.3、stable Rust toolchain、当前目标的 Rust standard library，以及 Tauri 2 要求的原生构建依赖。平台还需要：

- Windows：MSVC C++ Build Tools、Windows SDK 和 WebView2。
- macOS：Xcode Command Line Tools；发布签名和 notarization 需要对应 Apple 凭据。
- Linux：C/C++ 构建工具、`pkg-config`、WebKitGTK 4.1 及 Tauri 2 要求的 SSL、appindicator 和 SVG 系统库，具体包名随发行版而异。

Tauri 桌面调试和当前平台打包：

```bash
pnpm desktop:dev
pnpm desktop:build
```

该命令只构建当前 host 与已安装 target，不代表可在单台机器上交叉生成所有安装包。Windows 安装包必须在 Windows runner 生成，macOS `.app` / `.dmg` 必须在 macOS runner 生成，Linux 包必须在兼容发行版和架构的 Linux runner 生成；签名、notarization 和商店凭据只从 CI secret 或平台 CredentialStore 注入。

现有 Tauri Rust host 已负责启动所选 daemon、限制 NDJSON 行长、完成 `initialize` 握手、转发事件、取消运行并在退出时清理进程树，且不向 WebView 暴露通用 `shell execute/spawn` 权限。两套 daemon 参数不同：

- Rust：`qxnm-forge daemon --workspace <path> --state-dir <neutral-path>`
- .NET：`qxnm-forge-dotnet daemon --stdio --workspace <path> --state-dir <neutral-path>`

开发模式会从宿主环境变量或对应 debug/release 输出目录解析 daemon。正式安装包尚需按目标平台独立构建 sidecar、配置 Tauri `externalBin` 并精确 staging，不能把现有 `target/`、`bin/` 或 `obj/` 目录整体打包；未完成这一步的壳构建不能称为可发布安装包。

`src-tauri/icons/` 与 `public/favicon.png` 当前使用 Tauri CLI 模板生成的默认占位资源，未做手工修改；正式发布前应统一替换为已确认的品牌资产并重新执行各平台图标检查。

## Android

Tauri 2 Android 工程需要满足 Vite 7 要求的 Node.js、Corepack/pnpm 10.30.3、stable Rust、JDK 17、Android Studio Command-line Tools、Android SDK 36、NDK `29.0.13846066`、Gradle 所需组件与 Rust Android targets。配置 `JAVA_HOME`、`ANDROID_HOME`、`NDK_HOME` 后再初始化：

```bash
pnpm android:init --ci
pnpm android:dev
pnpm android:build --aab
```

当前开发机缺少 JDK、Android SDK/NDK 与 Rust Android targets，因此这里未生成 `src-tauri/gen/android`，也未虚构 APK/AAB 构建结果。

`src-tauri/gen/` 是可再生成目录并被 `.gitignore` 排除。CI 必须先以锁定的 Tauri CLI 执行 `android:init --ci`，再从 CI secret 临时注入 release keystore、alias 与密码并构建签名 AAB；这些签名材料及生成的 Gradle 私有配置不得提交。未完成签名与商店校验的 AAB 不能标为可发布。

Android 当前只支持远程 companion 模式：连接完成认证、TLS、授权、重连与 event replay 的 HTTPS/WebSocket application service。Rust 与 .NET 选择表示远程服务实现，不是在手机内启动 runtime；现有 Rust/.NET CLI 都不能作为 Tauri Android sidecar。Android WebView 不获得 shell、terminal、本地 workspace 或 CredentialStore 直接访问能力，远程服务必须把 profile 与工具选择作为不可信输入重新校验。

## 标识与隔离

Tauri bundle/persistence identifier 固定为品牌中立的 `com.gaohongshun.agentclient.community`，避免产品改名引发 WebView 数据目录迁移。未来 Pro UI 必须使用独立的 `.pro` identifier、独立源码与独立构建，不得通过路径依赖或 symlink 引用本目录。
