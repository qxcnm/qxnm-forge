# Provider 商业状态 v0.1

状态：Draft。作者：高宏顺 `<18272669457@163.com>`。

## 目标

本规范定义 Community 版本把已验签推广目录中的一个条目，经过本地用户明确确认后，固定为
可执行 Provider route 的最小闭环。Rust 与 .NET 必须独立实现相同的非敏感安装格式和凭据
行为，不调用对方运行时。

远程目录、已安装 route 与 credential 是三个不同的信任域：远程目录只提供候选信息；已
安装 route 是本地用户确认后的不可变快照；credential 只保存在工作区外的敏感状态文件。

## CLI

- `sponsors use <entry-id> --model <model-id> --accept-disclosure [--offline] [--state-dir PATH]`
- `sponsors installed [--state-dir PATH] [--format text|json]`
- `sponsors remove <entry-id> [--state-dir PATH]`
- `auth set --provider <provider-id> [--workspace PATH] [--state-dir PATH]`
- `auth list [--workspace PATH] [--state-dir PATH] [--format text|json]`
- `auth remove --provider <provider-id> [--workspace PATH] [--state-dir PATH]`

`sponsors use` 必须显示 `[推广]`、展示名称和返佣披露，并要求字面
`--accept-disclosure`。确认只固定 endpoint、family、model、目录版本和披露文本，不保存
secret。相同 `entryId` 再次安装是显式替换；远程目录后续变化不能静默修改既有 route。

`auth set` 只从 stdin 读取一个 API key；argv、stdout、stderr、Session 和日志均不得包含
该值。`auth list` 只输出拥有本地 credential 的 Provider ID。stored credential 已存在但
损坏、权限不安全或读取失败时必须失败关闭，不能回退环境变量。

## 持久化

非敏感安装文件固定为 `commercial/installed-sponsored-routes.json`，格式由
`sponsored-provider-installation.schema.json` 定义。Provider ID 固定为
`relay-<entry-id>`；它是品牌中立的本地 route 身份，不随产品改名。

敏感 credential 文件固定为 `credentials/provider-credentials.json`，格式由
`provider-credential-store.schema.json` 定义。实现必须有界、严格解析、拒绝重复 Provider
ID、拒绝 symlink/reparse file，并用独占 writer lock 和同目录临时文件更新。Unix 文件权限
固定为 `0600`、私有目录为 `0700`。Windows ACL/DPAPI 尚未通过发布门禁前，不得声称系统
密钥链级保护。

## 运行时

启动时只注册同时满足以下条件的本地推广 route：安装文件严格有效、API base 为 HTTPS、
family 属于 v0.1 allowlist、模型非空且 credential 当前可安全读取。缺 credential 必须在
Session 创建和网络访问前返回 `provider_unavailable`。

请求 target 在已固定 base 上同源追加：

- `openai-completions`：`/chat/completions`
- `openai-responses`：`/responses`
- `anthropic-messages`：`/messages`

实现复用现有 API-family request/stream parser；不得从目录下载 header、body、环境变量名、
脚本、插件或任意 transport 逻辑。HTTP redirect 和环境 proxy 继续禁用。

## 明确限制

v0.1 是权限受限文件型 CredentialStore，不是 OS 加密 keychain。目录中的 signup URL 仅用于
用户主动打开或复制，Provider 请求不会访问它。真实 Provider 测试必须显式 live opt-in；
默认门禁仅使用本地 mock，且测试 secret 不写入 fixture。
