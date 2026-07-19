# QXNM Forge Open instructions

本目录是 QXNM Forge 的非商业 Community 源码发行。作者：高宏顺（`18272669457@163.com`）。采用 PolyForm Noncommercial 1.0.0；任何商业使用必须另行联系作者取得书面商业授权。根目录 `AGENTS.md` 的通用安全、文档、方法注释和质量门禁规则全部适用。

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
