# ADR 0022：双实现签名远程推广 Provider 目录

- 状态：Accepted
- 日期：2026-07-18
- 项目：qxnm-forge
- 作者：高宏顺 `<18272669457@163.com>`

## 背景

Community 版本需要在不重新发布 Rust/.NET 客户端的情况下增加合作中转站，但直接下载
任意 endpoint、header 或运行配置会把广告后台变成供应链执行入口。目录还必须明确披露
推广和返佣，不能损害 Community 用户对 Provider 选择与隐私边界的信任。

## 决策

采用 `SPEC/schemas/sponsored-provider-catalog.schema.json` 的 brand-neutral source、catalog
和 signed envelope。source 由本地用户首次显式配置；远端 envelope 使用 ECDSA P-256、
SHA-256、ASN.1 DER 签名，签名覆盖 payload 的精确 UTF-8 字节。两种实现分别使用原生网络、
密码学、JSON 和文件系统 API。

目录只包含受限展示与 API-family 兼容信息，不直接注册 Agent route。客户端禁止重定向和
环境代理，限制响应大小与时间，严格校验 HTTPS、有效期、单调版本和同版本内容一致性。
缓存使用 create-new 内容寻址文件，并在每次读取时重新验签。

私钥只由离线 `sponsors keygen/sign` 管理工具读取；客户端配置和远端文件只含公钥。
没有合作站时目录为空，禁止提交虚假站点或未披露 affiliate 链接。

## 后果

- 发行方可通过上传一个新签名 envelope 远程增删推广条目。
- 攻击者无法仅修改静态托管文件注入站点；持有签名私钥仍是高价值安全边界。
- 被签名的旧目录仍可能重放，因此客户端额外保存并拒绝降低 `catalogVersion`。
- v0.1 不让远端目录自动获得 Provider 执行权；CredentialStore 和用户确认属于后续 ADR。
- Rust 单端完成只能记为 `implemented`；Rust/.NET 及共同黑盒门禁都通过后才能登记
  `conformant`。
