# Provider 商业状态与 CredentialStore v0.2

状态：Draft。作者：高宏顺 `<18272669457@163.com>`。

## 目标

本规范定义 Community 版本把已验签推广目录中的一个条目，经过本地用户明确确认后，固定为
可执行 Provider route 的最小闭环。Rust 与 .NET 必须独立实现相同的非敏感安装格式和
CredentialStore v0.2 行为，不调用对方运行时。

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
该值。`auth list` 只输出通过 canonical 叶名和 no-follow 元数据验证的 Provider ID，不得打开
credential 正文。叶名、类型、权限、owner 或大小不安全时 presence 查询必须失败关闭；正文只在
最终请求边界读取，届时无效 UTF-8、CR/LF/NUL 或读取失败同样失败关闭，不能回退环境变量。

## 持久化

非敏感安装文件固定为 `commercial/installed-sponsored-routes.json`，格式由
`sponsored-provider-installation.schema.json` 定义。Provider ID 固定为
`relay-<entry-id>`；它是品牌中立的本地 route 身份，不随产品改名。

CredentialStore v0.2 的唯一权威位置固定为 `credentials/provider-credentials.d/`。每个
credential 是该目录的一个直接普通文件，叶名精确为
`base64url_no_padding(UTF8(providerId)) + ".credential"`；解码后必须通过 Provider ID 语法并
重编码为同一 canonical 名称。正文是 raw UTF-8 key，长度 `1..16384` bytes，且不得
包含 CR、LF 或 NUL。store 最多 128 条，任何未知叶名、重复解码身份、目录、链接、device、
reparse point、空文件或超限文件都使操作失败关闭。

所有操作使用同一个跨进程独占锁。`list`、presence 检查与启动快照只枚举 canonical 名称及
no-follow 元数据，不得打开或反序列化 secret 正文；只有 Provider 构造最终 HTTP 认证 header
时，`read_for_request` 才能打开精确目标叶一次、做有界读取并重新验证正文。写入和轮换必须在
`credentials/` 根使用受控 `.provider-credential-<nonce>.tmp` create-new 普通文件，避免崩溃临时
项污染最终叶目录；先同步完整文件，再原子移动到目标叶，并同步最终目录和根目录。初始化只能按
固定临时名及 no-follow 安全元数据清理崩溃残留，不能删除未知项。Windows 替换使用具有
replace-existing 与 write-through 语义的原生操作。删除后也必须同步目录。Unix 私有目录 mode
精确为 `0700`，credential 叶 mode 精确为 `0600`，目录和叶 owner 必须是当前有效用户。
Windows ACL/DPAPI 尚未通过发布门禁前，不得声称系统密钥链级保护。

旧 `credentials/provider-credentials.json` 及
`provider-credential-store.schema.json` 仅是 legacy v0.1 migration input，不是当前 store
格式。只有最终 v0.2 目录不存在时，实现才可在同一独占锁内以 16 MiB 上限严格读取旧聚合文档，
验证版本、未知字段、重复 Provider ID、128 条上限和每条 raw UTF-8 字节上限，并写入固定隐藏
staging `credentials/.provider-credentials.d.migrating/`。全部叶和 staging 目录同步后，才以
同父 rename 发布最终目录并同步 `credentials/`；随后仅按 no-follow 元数据删除 legacy 文件并
再次同步。最终目录一旦存在就是唯一权威，遗留 legacy 文件绝不能再次解析，只能安全删除。
rename 前崩溃时，下次初始化只清理固定 staging 中 canonical、安全的普通叶并重新迁移；rename
后崩溃时则继续使用最终目录。这是允许批量读取 secret 的唯一迁移例外，旧用户无需重新录入 Key。

用户自定义连接不是推广 route，单独遵循 ADR 0029 与
`custom-provider-connection.schema.json`。它不得携带目录签名、返佣字段或 `relay-*` 身份，
但复用同一 CredentialStore 的安全读写边界；连接配置文件本身不得包含 secret。通用 CLI 与
自定义连接 RPC 的 credential mutation 必须共同遵守 `connection -> credential` 固定锁顺序，
避免与连接改名或删除交错后留下不可达 secret。

## 运行时

启动时只注册同时满足以下条件的本地推广 route：安装文件严格有效、API base 为 HTTPS、
family 属于 v0.1 allowlist、模型非空且 credential presence 元数据安全。缺 credential 必须在
Session 创建和网络访问前返回 `provider_unavailable`；正文仅在最终请求 header 边界读取并验证。

请求 target 在已固定 base 上同源追加：

- `openai-completions`：`/chat/completions`
- `openai-responses`：`/responses`
- `anthropic-messages`：`/messages`

实现复用现有 API-family request/stream parser；不得从目录下载 header、body、环境变量名、
脚本、插件或任意 transport 逻辑。HTTP redirect 和环境 proxy 继续禁用。

## 明确限制

v0.2 是权限受限文件型 CredentialStore，不是 OS 加密 keychain；非敏感推广 route 文档仍为
v0.1。目录中的 signup URL 仅用于
用户主动打开或复制，Provider 请求不会访问它。真实 Provider 测试必须显式 live opt-in；
默认门禁仅使用本地 mock，且测试 secret 不写入 fixture。
