# ADR 0023：推广 Provider 本地确认、CredentialStore 与可执行 route

- 状态：Accepted
- 日期：2026-07-18
- 项目：qxnm-forge
- 作者：高宏顺 `<18272669457@163.com>`

## 背景

ADR 0022 让发行方可以远程维护推广位，但目录条目故意不能直接执行。Community 版本需要一
条能产生实际价值和返佣机会的短链路，同时不能让远程广告后台获得 secret、请求头或执行
权限。

## 决策

采用三段式信任模型：signed catalog 是候选；`sponsors use` 在明确披露和用户确认后写入
本地非敏感 route snapshot；`auth set` 通过 stdin 独立写入工作区外 CredentialStore。
远程目录更新不会修改已安装 snapshot，credential 也不进入 snapshot。

Rust 与 .NET 使用同一 JSON schema，但各自实现锁、文件安全、Provider 注册和 API-family
transport。首版只开放 OpenAI Chat Completions、OpenAI Responses 和 Anthropic Messages
三种已有 family adapter。运行时只注册有本地 stored credential 的 route，并在请求构造
边界重新读取，以允许安全轮换且避免 adapter 长期持有 secret。

## 后果

- 发行方可远程新增合作候选，但用户仍掌握每条可执行 route 的安装决定。
- 用户必须分别完成 `sponsors use` 和 `auth set`，多一步换取供应链边界和清晰返佣披露。
- 文件型 store 可在 24 小时 MVP 内双实现并互操作；它不等同 OS keychain，Windows 强保护
  需后续 CredentialStore backend ADR。
- Pro 激活不与本地 Provider secret 共用文件或信任密钥；收费许可在本闭环稳定后另行设计。
