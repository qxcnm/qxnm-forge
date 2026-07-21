# QXNM Forge UI

QXNM Forge Community 的 TypeScript + React 工作台。界面参考 Codex 桌面应用的信息架构，使用 Vite、shadcn/ui、TanStack Query、Zustand 与 Tauri 2，工程目标覆盖 Web、Windows、macOS、Linux 和 Android。

## 当前能力

- 左侧工作区与最近任务导航、中央任务记录、底部消息输入器和变更审阅抽屉。
- 可在 Rust 与 .NET 后端画像之间切换；功能显隐以后端 `initialize.capabilities` 为准，不按语言名称猜测。
- 自定义智能体管理支持创建、编辑、复制、删除与会话选择，可配置指令、模型、工具 allowlist、审批模式和回复行为。
- `ApplicationServiceClient` 是唯一生产后端服务边界。React 不读取 SQLite、Session journal、CredentialStore、许可证或宿主私有文件。
- 普通浏览器默认使用公开 faux 数据的零网络预览客户端，不会隐式调用付费 Provider；Tauri 桌面壳自动切换到真实 application service。
- shadcn/ui 组件由 CLI 生成并保存在 `src/components/ui/`；业务层不自行实现 Button、Select、Sheet、Tooltip 等基础组件。

当前 transport 边界分为三种：普通浏览器使用零网络 faux 预览；Windows、macOS 与 Linux 的 Tauri host 通过受限 NDJSON bridge 启动所选 Rust/.NET daemon；Android 预留经过认证的 HTTPS/WebSocket 远程 service，当前尚未实现该远程 transport。桌面 bridge 只转发固定 application-service allowlist，不向 WebView 暴露 executable、argv、状态目录、workspace 或通用 shell。

## 自定义智能体

桌面端通过 `agentProfiles/list|create|update|delete` 访问所选 daemon 的 `application.db`，使用 revision CAS 更新/删除，并在 `run/start` 只提交 `{profileId, revision}`。daemon 会重新验证完整模型三元组和工具子集，并把精确 `agentProfileSnapshot` 写入 `run.accepted` journal；切换 Rust/.NET 或重启应用后仍可读取同一品牌中立数据。普通浏览器仍由进程内 `FauxAgentProfileService` 提供内存 CRUD，刷新即重置且不读取数据库或宿主文件。

每个 profile 的模型始终使用完整 `(providerId, modelId, apiFamily)` identity，不能仅保存 `modelId` 或由 UI 猜测路由。工具选择只是对 `initialize.capabilities.tools` 的收紧 allowlist，审批选项也不能降低后端 policy。profile 不保存 secret、真实宿主路径或待执行命令。详细 Draft 边界见 [`SPEC/ui.md`](../SPEC/ui.md)。

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
