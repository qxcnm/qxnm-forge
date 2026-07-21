# QXNM Forge Open instructions

本目录是 QXNM Forge 的非商业 Community 源码发行。作者：高宏顺（`18272669457@163.com`）。采用 PolyForm Noncommercial 1.0.0；任何商业使用必须另行联系作者取得书面商业授权。本文件是独立仓库的完整工程约束，不依赖仓库外的父级说明。

## 通用工程规则

- 每个新增函数或方法都必须使用对应语言的标准方法级文档注释，包含简洁中文功能说明、`作者：高宏顺` 与 `邮箱：18272669457@163.com`。公共 API 和安全敏感逻辑还必须说明输入、输出、不变量及有意义的失败条件；明确标识的生成或 vendor 代码除外。
- 面向用户和开发者的 Wiki、运维及工程文档使用中文；协议标识符、代码符号、命令和规范性英文协议文本可以保留原文。
- 默认测试只使用 faux provider、loopback mock 或脱敏 recording，不得隐式调用付费模型。secret 只能在最终使用边界从环境变量或平台 CredentialStore 读取，不得写入源码、日志、Session、fixture 或命令输出。
- 保留无关和并发工作，不得使用 destructive Git 命令覆盖现有修改。手写源码修改使用 `apply_patch`，生成输出不得进入源码仓库。
- Rust 必须通过 `cargo fmt --check`、workspace Clippy warnings-as-errors 和 workspace tests；.NET 必须通过 `dotnet format --verify-no-changes`、warnings-as-errors build 和 tests；React/Tauri 必须通过 typecheck、lint、tests 和 build；`CONFORMANCE/tests` 独立执行。

## Community 边界

- 只保留 Rust 与 .NET Community 实现、公共规范、公开 fixture 和 conformance tests。
- 禁止加入 Pro 权益判断、在线卡密兑换、离线试用计时、许可证签发/激活、授权私钥、完整卡密和私有服务地址。
- Community 可以包含签名的推广 Provider 目录、用户主动配置的 CredentialStore 和公开返佣披露，但不能自动执行未知远程代码或绕过用户授权。
- 未来 `ui/` 使用 TypeScript + React；当前只维护协议边界和实现说明，不恢复旧 TypeScript Agent runtime。
- 默认应用数据库为 SQLite：Rust 使用 SeaORM，.NET 使用 Entity Framework Core。远程数据库连接串不得保存在项目配置或 Session。

## 发布门禁

- 发布前扫描 Open 源码，确保没有 Pro symbol、私钥逻辑、真实卡密和兑换服务。
- Rust、.NET 和 `CONFORMANCE/tests` 必须分别通过本目录门禁。
- Open 的能力矩阵不得依据 Pro 测试结果升级。
