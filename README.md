# QXNM Forge Community

QXNM Forge Community 是源码公开、仅限非商业使用的 AI Coding Agent 底座，由高宏顺（`18272669457@163.com`）开发。命令名为 `qxnm-forge`。

本项目采用 **PolyForm Noncommercial License 1.0.0**，不是允许任意商用的 MIT/Apache 许可。个人学习、研究、实验和非商业项目可以按许可证使用；公司业务、收费服务、商业产品集成、转售、代运营及其他商业用途必须先联系高宏顺取得单独的书面商业授权。

本仓库同时提供完全独立的 Rust 与 .NET 实现。公共行为由 `SPEC/`、ADR、JSON Schema 和 `CONFORMANCE/` 决定；任一实现都不通过 FFI、另一语言子进程或共享 runtime 完成功能。

## 已有功能

- 多轮 Agent loop、流式消息、工具调用、取消和交互审批；
- portable append-only Session、writer lease、尾部恢复、branch 与 compaction；
- 文件、搜索、process/shell、terminal 和 Linux Bubblewrap hard sandbox 基础；
- UTF-8 NDJSON JSON-RPC daemon 与纯文本 `run` CLI；
- 九种文本 API-family adapter、OpenRouter Images，以及 6 条 manifest 驱动的 production route；
- 可远程更新的签名推广 Provider 目录、明确返佣披露、本地 route pin 和工作区外 CredentialStore；
- 默认 SQLite 产品数据库：Rust 使用 SeaORM，.NET 使用 EF Core。

能力矩阵中的 `implemented` 不等于公开承诺；只有 `conformant` 或 `live-verified` 才表示相应门禁已经完成。当前不是完整 IDE，也没有可运行 React UI、全量 45 条 production route、OAuth、完整 Trust/Resources/Extensions 或 Windows TUI。

## Open 边界

此仓库不包含 Pro 卡密、在线兑换、离线试用、许可证签发/激活或授权私钥实现。收费功能只存在于私有 Pro 分发。公开可读源码不等于授予商业使用权。

Community 允许发行方远程添加合作中转站：客户端只接受固定公开密钥验证通过的 HTTPS catalog，远程内容必须标注“推广/返佣”，且不能携带凭据、脚本、Shell、插件或 Prompt。用户仍需执行 `sponsors use` 接受披露，再通过 stdin 执行 `auth set`；远程更新不会静默改动已安装 route。

没有合作站时可以先发布空的签名 catalog，后续只更新静态托管的 envelope，无需重新发布 Rust/.NET 客户端。运维说明见 [`docs/sponsored-provider-commercialization.md`](docs/sponsored-provider-commercialization.md)。

## 数据与未来 UI

应用产品数据默认写入用户状态根的 `application.db`。Rust 采用 SeaORM，.NET 采用 Entity Framework Core；PostgreSQL/MySQL provider 已冻结配置边界但尚未启用，选择未启用 provider 会明确失败而不会回退 SQLite。portable Session 继续使用独立 JSONL。

未来 UI 使用 TypeScript + React，放在独立公开仓库或 [`ui/`](ui/) 中。UI 只调用品牌中立 JSON-RPC/HTTP application service，不直连 SQLite、Session、CredentialStore 或工具执行器。详见 [`SPEC/storage.md`](SPEC/storage.md) 与 [`SPEC/ui.md`](SPEC/ui.md)。

## 构建

Rust（Rust 1.85+）：

```sh
cd rust
cargo build --locked
./target/debug/qxnm-forge --help
```

.NET（.NET 10 SDK）：

```sh
cd dotnet
dotnet restore QxnmForge.slnx
dotnet build QxnmForge.slnx -c Release --no-restore --warnaserror
dotnet src/QxnmForge.Cli/bin/Release/net10.0/qxnm-forge-dotnet.dll
```

最小离线运行：

```sh
./rust/target/debug/qxnm-forge run "你好" --provider faux --model faux-v1
```

Rust 与 .NET 均提供 `daemon`、`run`、`session`、`sponsors` 和 `auth`。普通状态默认保存在平台用户级状态目录；`--state-dir` 可显式覆盖。Provider secret 不接受命令行参数，只能按文档从环境或 stdin/CredentialStore 在最后边界读取。

## 验证

```sh
cd rust
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace

cd ../dotnet
dotnet format QxnmForge.slnx --verify-no-changes --no-restore
dotnet build QxnmForge.slnx -c Release --no-restore --warnaserror
dotnet test QxnmForge.slnx -c Release --no-restore

cd ..
python3 -m unittest discover -s CONFORMANCE/tests
```

默认验证只使用 faux provider、loopback mock、合成凭据和临时测试密钥，不访问付费模型。

## 许可证

Community 源码按 [PolyForm Noncommercial License 1.0.0](LICENSE) 发布，仅授权非商业用途。

商业使用包括但不限于公司内部业务使用、收费 SaaS/API、商业产品集成、客户交付、广告或返佣驱动的第三方再发行、转售和代运营。需要商用、OEM、私有部署或定制授权时，请联系：高宏顺 `<18272669457@163.com>`。具体许可范围以双方书面商业协议为准。
