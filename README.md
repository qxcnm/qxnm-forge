# QXNM Forge

一套同时用 Rust 和 .NET 实现的 AI 编程助手核心。

这个项目还在早期，但已经不是只能看的空架子：它能连接模型、连续调用工具、保存会话，也提供了通过品牌中立 application service 接入 Rust 或 .NET daemon 的 React 桌面界面。

我做两套实现，不是为了堆语言数量。Rust 版用来快速往前走，.NET 版用来确认同一套协议和数据真的能在另一种技术栈里独立跑通。两边不会互相调用，也没有藏一个“真正的核心”在某个二进制里。

> 源码可以查看、学习和用于非商业项目。公司使用、收费服务、商业产品集成或客户交付需要单独取得商业授权：`18272669457@163.com`。

## 现在能做什么

- 和模型进行多轮对话，并在模型请求时执行文件、搜索、进程和 Shell 工具；
- 流式输出回答，支持取消、工具审批和失败恢复；
- 把会话保存为可移植的 JSONL，支持恢复、分支和上下文压缩；
- 通过 stdin/stdout 运行 JSON-RPC daemon，方便 CLI、编辑器或未来 UI 接入；
- 使用 OpenAI、Google、Groq、Mistral、MiniMax、OpenRouter 等已经接通的 route；
- 从远程签名目录发现中转 Provider，目录更新不需要重新发布客户端；
- 在 React + shadcn/ui 工作台中选择完整模型 route、管理 Provider 和智能体、处理工具审批与 Session；
- 通过 Tauri 2 打包 Windows、macOS、Linux 桌面端，并提供 Android companion 构建入口；
- 默认使用 SQLite 保存应用数据，Rust 版采用 SeaORM，.NET 版采用 EF Core。

CLI 与桌面 UI 共用同一套 JSON-RPC/HTTP application service 边界。更多 Provider 登录方式以及 MySQL/PostgreSQL adapter 仍在后续计划里。

## 先跑起来

项目默认带有一个离线 `faux` Provider，不需要 API key，也不会请求外网。它适合先确认环境和 Agent 流程是否正常。

### Rust

需要 Rust 1.85 或更高版本：

```bash
git clone https://github.com/qxcnm/qxnm-forge.git
cd qxnm-forge
cargo run --manifest-path rust/Cargo.toml -- run "你好，介绍一下你自己"
```

### .NET

需要 .NET 10 SDK：

```bash
git clone https://github.com/qxcnm/qxnm-forge.git
cd qxnm-forge
dotnet run --project dotnet/src/QxnmForge.Cli -- run "你好，介绍一下你自己"
```

如果两边都能输出回答，最基础的 Agent、Session 和 CLI 就已经工作了。

### React / Tauri 桌面界面

需要 Node.js 22、pnpm 10，以及对应平台的 Tauri 2 系统依赖：

```bash
cd ui
pnpm install --frozen-lockfile
pnpm dev
```

浏览器预览只使用离线 faux application service。启动原生桌面壳并连接所选 Rust 或 .NET daemon：

```bash
pnpm desktop:dev
```

平台打包和 Android companion 的完整前置条件见 [UI 说明](ui/README.md)。

## 使用真实模型

生产 route 只有在对应 API key 存在时才会启用。比如使用 Groq：

```bash
export GROQ_API_KEY="你的 API key"

cargo run --manifest-path rust/Cargo.toml -- run \
  "帮我看看这个项目的结构" \
  --provider groq \
  --model llama-3.1-8b-instant \
  --api-family openai-completions
```

.NET 版使用相同的 Provider、model 和 API-family 参数。API key 不会写进 Session，HTTP 错误正文也不会直接回显到聊天记录。

当前正式开放的 route 包括：

| Provider | API family | 环境变量 |
| --- | --- | --- |
| Groq | `openai-completions` | `GROQ_API_KEY` |
| MiniMax | `anthropic-messages` | `MINIMAX_API_KEY` |
| Mistral | `mistral-conversations` | `MISTRAL_API_KEY` |
| OpenAI | `openai-responses` | `OPENAI_API_KEY` |
| Google | `google-generative-ai` | `GEMINI_API_KEY` |
| OpenRouter | `openrouter-images` | `OPENROUTER_API_KEY` |

仓库里还包含其他 API-family 的解析和本地 mock 测试，但没有进入生产 route 的不能算“已经接入”。

## 作为 daemon 使用

Rust：

```bash
cargo run --manifest-path rust/Cargo.toml -- daemon --workspace .
```

.NET：

```bash
dotnet run --project dotnet/src/QxnmForge.Cli -- daemon --stdio --workspace .
```

daemon 使用 UTF-8 NDJSON JSON-RPC。CLI、未来的 React UI 和其他客户端都会走这一层，不会各自再实现一套 Agent 状态机。

## 关于中转 Provider

客户端支持一个由发行方签名的远程 Provider 目录。以后有新的合作站，只需要更新远程 catalog，不必为了加一条推广信息重新发版。

这套机制有意做得比较克制：

- 推广条目必须明确写出返佣关系；
- 远程目录只能提供候选 route，不能远程下发 API key、脚本或 Prompt；
- 用户需要主动确认并安装 route；
- 已经安装的 route 不会被远程目录静默替换。

相关命令是 `sponsors` 和 `auth`，完整操作见 [中转 Provider 运维说明](docs/sponsored-provider-commercialization.md)。

## 仓库结构

```text
rust/          Rust 实现
dotnet/        .NET 实现
SPEC/          两边共同遵守的协议和行为规范
CONFORMANCE/   跨语言黑盒测试
docs/          中文说明
ui/            React + shadcn/ui + Tauri 2 工作台
```

Rust 和 .NET 可以有不同的内部写法，但 Session、Provider 选择、错误分类和 daemon 行为必须符合共同规范。

## 开发和测试

Rust：

```bash
cd rust
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
```

.NET：

```bash
cd dotnet
dotnet restore QxnmForge.slnx
dotnet format QxnmForge.slnx --verify-no-changes --no-restore
dotnet build QxnmForge.slnx -c Release --no-restore --warnaserror
dotnet test QxnmForge.slnx -c Release --no-restore
```

共同规范：

```bash
python3 -m unittest discover -s CONFORMANCE/tests
```

默认测试只使用离线 Provider、loopback mock 和合成凭据，不会偷偷调用付费模型。

## 许可证

QXNM Forge Community 按 [PolyForm Noncommercial License 1.0.0](LICENSE) 提供，仅限非商业使用。

个人学习、研究、实验和非商业项目可以按照许可证使用。公司内部业务、收费 SaaS/API、商业产品集成、客户交付、OEM、转售或其他商业用途，请联系作者取得书面商业授权：

**高宏顺 · 18272669457@163.com**

如果你只是想研究 Agent、Provider 或跨语言 Session 的实现，直接看、直接跑就行。
