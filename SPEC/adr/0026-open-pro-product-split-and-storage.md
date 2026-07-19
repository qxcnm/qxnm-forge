# ADR 0026：Open/Pro 产品拆分、QXNM Forge 品牌与可替换存储

- 状态：Accepted
- 日期：2026-07-18
- 项目：QXNM Forge
- 作者：高宏顺 `<18272669457@163.com>`

## 背景

产品需要先以 Community 源码公开版本获得用户和中转 Provider 返佣，再以一机一码授权销售 Pro。原工作名不适合公开品牌，单目录也容易把收费实现、私钥边界和发行许可证混在一起。后续 React UI 与业务数据还需要明确的服务层和数据库抽象。Community 许可后来由 ADR 0027 明确为仅限非商业使用。

## 决策

产品命名为 **QXNM Forge**，CLI/仓库使用 `qxnm-forge`。协议、Session 与持久化标识保持品牌中立。

发行物分为：

- `open/`：非商业 Community 源码仓库，不包含卡密、试用、许可证签发/验证和兑换服务源码；
- `pro/`：专有完整分发，包含 Community 功能与收费功能，当前不声称是源码 overlay。

只维护 Rust 与 .NET 两套 Agent runtime。未来 TypeScript + React 只实现 UI，并通过品牌中立 application service 访问后端。

应用产品数据默认存放于 SQLite `application.db`；Rust 使用 SeaORM，.NET 使用 Entity Framework Core。PostgreSQL/MySQL 通过独立 provider adapter 后续启用。portable Session JSONL 不迁入 ORM。数据库连接串只从部署 secret 边界读取。

## 后果

- Open 可以复制为独立 Git 仓库发布，且不暴露收费实现。
- Pro 是私有完整代码副本；公共修复在两套分发、两种语言间同步并分别验证，直到未来另行 ADR 选择安全的源码复用方式。
- 两个 ORM 产生的内部代码不要求相同，但逻辑 migration 版本、schema 行为和错误分类必须通过共同 conformance。
- React UI 不能成为第三套状态机，也不能直连数据库；这为 Web、桌面壳和远程管理保留统一接口。
