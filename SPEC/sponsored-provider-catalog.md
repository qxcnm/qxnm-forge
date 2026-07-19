# 远程推广 Provider 目录 v0.1

状态：Draft。作者：高宏顺 `<18272669457@163.com>`。

## 目标与边界

远程推广目录允许发行方在不重新发布客户端的情况下增加、下线和调整中转站推广位。
目录是商业展示数据，不是 Provider 支持声明、远程代码执行入口或权限策略。客户端必须
明确显示“推广”和返佣披露，且不得把推广条目伪装为官方 Provider、静默设为默认路由，
或因目录出现一条记录就扩大本地可执行 route allowlist。

Rust 与 .NET 分别实现本规范；两端共享的只有 Schema、fixture 和黑盒行为，不调用对方
运行时。目录、缓存和信任配置使用品牌中立字段。

## 信任引导与发布

管理员第一次使用 `sponsors configure` 显式安装一个 `source`：固定 HTTPS URL、`keyId`
及 ECDSA P-256 公钥。公钥是 SEC1 uncompressed point 的 65 字节编码，首字节必须为
`0x04`。本地拥有状态目录写权限的用户可更换 source；远端响应不能更换信任公钥。

管理员在离线可信机器上运行 `sponsors keygen` 创建 PKCS#8 私钥和公开 trust-key JSON，
编辑 catalog payload 后运行 `sponsors sign`。私钥不得上传、提交、记录或复制到客户端。
远程只发布 envelope JSON。

Envelope 的 `payload` 是 catalog JSON 原始 UTF-8 字节的标准 Base64；签名输入正是解码后
字节，不做 JSON 重新序列化或字段排序。`signature.algorithm` 固定为
`ecdsa-p256-sha256-asn1`，签名使用 SHA-256 和 RFC 3279 DER sequence 格式。

## 获取、验证与缓存

客户端获取顺序固定为：

1. 读取并严格验证本地 source；未配置时返回空推广列表而不是内置虚假站点。
2. 普通刷新只允许 HTTPS、禁止重定向、环境代理和 userinfo，并施加 5 秒及 256 KiB 上限。
3. Base64 解码 payload/signature，按 source 公钥和 keyId 验签，再严格解析 catalog。
4. 校验 UTC 时间、最长 90 天有效期、条目数量、唯一 ID、HTTPS URL 和允许的 API family。
5. 若本地已有更高 `catalogVersion`，拒绝远程回滚；同版本不同 payload 也拒绝。
6. 通过验证的完整 envelope 以 create-new 内容寻址文件写入状态目录并同步。
7. 网络、HTTP 或远端验证失败时，可回退到仍在有效期内的最高已验证缓存；缓存每次读取
   都重新验签，不能把“曾经缓存”当作信任。

`--offline` 不访问网络，只读取有效缓存。过期目录不展示；客户端不得为了广告可用性
放宽时钟、签名或 HTTPS 检查。

## 条目语义

每个条目必须包含稳定 `id`、展示名称、简短说明、API family、API base、注册链接、返佣
披露和排序优先级。v0.1 只接受：

- `openai-completions`
- `openai-responses`
- `anthropic-messages`

API base 只能是无 userinfo、query、fragment 的 HTTPS URL；注册链接允许 query 携带公开
affiliate 参数，但同样禁止 userinfo。任何 URL 都不得包含 API key。列表按 `priority`
降序、再按 `id` 升序展示。

推广目录不得下发环境变量名、请求 header/body、Shell 命令、动态库、插件、更新地址、
凭据或模型 Prompt。把条目转换为可执行本地 Provider 配置时，必须另经用户确认、
CredentialStore 和 route policy；其合同定义在 `provider-commercial-state.md`，但仍不改变
本目录的只读候选语义。

## 隐私与商业披露

客户端不会向目录服务发送 Session、Prompt、API key、工作区路径、模型使用记录或设备
标识。发行方若通过注册链接获得佣金，CLI 必须逐条显示 catalog 提供的中文披露文本。
用户始终可以不配置目录、离线运行或使用官方/自定义 Provider。
