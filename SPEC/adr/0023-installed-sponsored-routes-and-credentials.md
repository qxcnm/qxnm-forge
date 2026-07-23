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

当前 CredentialStore v0.2 不再以聚合 JSON 作为权威格式，而使用
`credentials/provider-credentials.d/` 逐 credential 私有叶。叶名是 Provider ID UTF-8 字节的
canonical base64url 无 padding 编码加 `.credential`，正文是 `1..16384` bytes 的 raw UTF-8
key，最多 128 条；Unix 目录/叶/owner 固定为 `0700`/`0600`/当前有效用户。presence 与列表只
验证名称和 no-follow 元数据，最终 HTTP header 注入边界才打开目标叶一次。轮换使用
`credentials/` 根的受控临时普通文件，避免崩溃残留污染最终叶目录；临时残留只能按固定名称与
安全元数据清理。轮换、删除与目录发布都必须经过独占锁、文件同步、原子替换和目录同步，且拒绝
symlink、reparse point、device 与未知叶。

原 `credentials/provider-credentials.json` schemaVersion `0.1` 只保留为一次性迁移输入。
最终目录缺失时才可严格读取它，写入固定隐藏 staging
`credentials/.provider-credentials.d.migrating/`，同步全部叶和目录后同父 rename 发布；最终目录
存在时绝不再解析 legacy 正文，只按安全元数据删除。rename 前的中断清理 staging 后重试，rename
后的中断以最终目录为唯一权威，因此升级不要求用户重新录入已有 Key。

## 后果

- 发行方可远程新增合作候选，但用户仍掌握每条可执行 route 的安装决定。
- 用户必须分别完成 `sponsors use` 和 `auth set`，多一步换取供应链边界和清晰返佣披露。
- 逐叶文件型 store 可由两套实现互操作；它不等同 OS keychain，Windows 强保护
  需后续 CredentialStore backend ADR。
- Pro 激活不与本地 Provider secret 共用文件或信任密钥；收费许可在本闭环稳定后另行设计。
