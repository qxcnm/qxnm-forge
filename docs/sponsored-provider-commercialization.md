# 推广 Provider 远程目录与双端发布

作者：高宏顺  
邮箱：18272669457@163.com

## 当前交付

Rust 与 .NET 已分别实现同一份签名推广目录协议。目录为空时不会伪造合作站；以后只需在
服务器替换一个经过签名的 envelope JSON，两套客户端刷新时就能显示新条目，不必重新
编译。两端的 source 与缓存格式兼容，但运行时密码学、HTTP、JSON 和文件系统代码相互
独立。

当前目录用于“发现、推广披露和注册链接”。它不会仅凭远端配置自动执行。用户运行
`sponsors use` 明确接受返佣披露后，客户端才把 endpoint、family、model 和目录版本固定为
本地 route；随后 `auth set` 从 stdin 单独写入工作区外 CredentialStore。Rust/.NET 都能用
该 route 复用现有 API-family adapter 发起请求，推广后台仍不能下发 header、secret 或代码。

## 一次性生成签名密钥

私钥应在离线可信机器生成，并备份到工作区和代码仓库之外。以下两条任选其一；生成的
格式跨语言兼容。

Rust：

```sh
qxnm-forge sponsors keygen \
  --key-id owner-2026-01 \
  --private-key /secure/catalog-private.pk8 \
  --public-key /secure/catalog-public.json
```

.NET：

```sh
qxnm-forge-dotnet sponsors keygen \
  --key-id owner-2026-01 \
  --private-key /secure/catalog-private.pk8 \
  --public-key /secure/catalog-public.json
```

两个输出路径都必须尚不存在。Unix 私钥创建权限固定为 `0600`；命令不会把私钥内容打印
到 stdout/stderr。`catalog-public.json` 可以分发给客户端，`catalog-private.pk8` 不得上传、
提交、进入日志或复制到部署服务器。

## 编辑空目录

仓库提供无合作站的初始模板：

```text
CONFORMANCE/fixtures/sponsored-provider-catalog/empty.catalog.json
```

加入合作站后的示例：

```json
{
  "schemaVersion": "0.1",
  "catalogVersion": 2,
  "issuedAt": "2026-07-18T12:00:00Z",
  "expiresAt": "2026-08-17T12:00:00Z",
  "entries": [
    {
      "id": "example-relay",
      "displayName": "示例中转站",
      "description": "OpenAI-compatible 服务，新用户可领取试用额度",
      "apiFamily": "openai-completions",
      "apiBaseUrl": "https://api.example.com/v1",
      "signupUrl": "https://example.com/register?ref=your-code",
      "commissionDisclosure": "通过该链接注册或充值，发行方可能获得佣金",
      "priority": 100
    }
  ]
}
```

每次发布必须增大 `catalogVersion`。`issuedAt` 不能明显晚于客户端时钟；`expiresAt` 必须
晚于签发时间且最长 90 天。这样即使旧签名文件被重放，已经见过更高版本的客户端也会
拒绝回滚。

v0.1 只接受 `openai-completions`、`openai-responses` 和 `anthropic-messages`。API base
只能使用无 userinfo、query、fragment 的 HTTPS；注册链接可以携带公开 affiliate query。
目录禁止 header、body、环境变量名、Shell、插件、Prompt、API key 和远程执行字段。

## 签名与交叉验证

Rust 签名：

```sh
qxnm-forge sponsors sign \
  --catalog ./catalog.json \
  --private-key /secure/catalog-private.pk8 \
  --public-key /secure/catalog-public.json \
  --output ./catalog.envelope.json
```

.NET 签名：

```sh
qxnm-forge-dotnet sponsors sign \
  --catalog ./catalog.json \
  --private-key /secure/catalog-private.pk8 \
  --public-key /secure/catalog-public.json \
  --output ./catalog.envelope.json
```

输出同样采用 create-new，避免覆盖一个已经审计的签名文件。上传前可用另一端验签：

```sh
qxnm-forge sponsors verify \
  --envelope ./catalog.envelope.json \
  --public-key /secure/catalog-public.json \
  --format json

qxnm-forge-dotnet sponsors verify \
  --envelope ./catalog.envelope.json \
  --public-key /secure/catalog-public.json \
  --json
```

签名使用 ECDSA P-256、SHA-256 和 RFC 3279 ASN.1 DER。签名覆盖 catalog 的精确 UTF-8
字节，不依赖两种语言是否采用相同 JSON pretty-print。

## 远程托管与更新

`catalog.envelope.json` 是不含密钥的静态文件，可放在 Cloudflare R2/Pages、GitHub Pages、
对象存储或自己的 HTTPS 站点。服务必须直接返回 `200`；客户端不会跟随重定向，也不会
使用环境代理。响应总上限 256 KiB、总超时 5 秒。

远程增加合作站：

1. 在本地 catalog 增加条目；
2. 增大 `catalogVersion`，更新签发和过期时间；
3. 用离线私钥生成一个新的 envelope 输出文件；
4. 先由 Rust 和 .NET 任一端执行 `verify`；
5. 在对象存储中原子替换公开 URL 对应的 envelope；
6. 两端下次 `sponsors list` 自动获取并缓存。

这就是当前“直接远程添加”的管理面。它不需要重新发布客户端；后续可以在同一签名发布
逻辑上增加网页登录后台，但后台仍不应长期持有明文私钥。更安全的做法是后台生成待签名
payload，由离线管理员签名后上传。

## 客户端配置与展示

Rust：

```sh
qxnm-forge sponsors configure \
  --catalog-url https://catalog.example.com/catalog.envelope.json \
  --public-key ./catalog-public.json

qxnm-forge sponsors list
qxnm-forge sponsors list --offline
qxnm-forge sponsors list --format json
```

.NET：

```sh
qxnm-forge-dotnet sponsors configure \
  --catalog-url https://catalog.example.com/catalog.envelope.json \
  --public-key ./catalog-public.json

qxnm-forge-dotnet sponsors list
qxnm-forge-dotnet sponsors list --offline
qxnm-forge-dotnet sponsors list --json
```

可用 `--state-dir PATH` 显式选择状态根。默认优先读取 `QXNM_FORGE_SESSION_ROOT`，否则使用
Windows `LOCALAPPDATA/qxnm-forge/state`、Linux `XDG_STATE_HOME/qxnm-forge` 或
`$HOME/.local/state/qxnm-forge`；不会把 source、缓存或 Session 默认放入 Agent 工作区。

CLI 固定以 `[推广]` 标记每条记录并显示 `commissionDisclosure`。目录未配置时输出
“尚未配置推广 Provider 目录”，空目录输出“当前没有推广 Provider”，不会用内置虚假站
填充广告位。

## 用户安装并调用中转站

看到目录条目后，用户先选择中转站和该站实际支持的模型。安装命令必须带明确披露确认：

```sh
# Rust
qxnm-forge sponsors use example-relay \
  --model model-v1 \
  --accept-disclosure

# .NET
qxnm-forge-dotnet sponsors use example-relay \
  --model model-v1 \
  --accept-disclosure
```

命令会再次打印 `[推广]` 和 `commissionDisclosure`，并生成固定 Provider ID
`relay-example-relay`。远程目录随后改 endpoint、model 或下线条目，都不会静默修改这个本地
快照。用以下命令审计或移除：

```sh
qxnm-forge sponsors installed --format json
qxnm-forge sponsors remove example-relay

qxnm-forge-dotnet sponsors installed --json
qxnm-forge-dotnet sponsors remove example-relay
```

API key 不能作为命令行参数。首版为避免终端回显，`auth set` 只接受重定向 stdin；推荐从
密码管理器或受限文件管道输入，不要把真实 key 写进 Shell history：

```sh
password-manager read relay-example-relay | \
  qxnm-forge auth set --provider relay-example-relay

password-manager read relay-example-relay | \
  qxnm-forge-dotnet auth set --provider relay-example-relay
```

`auth list` 只显示 `configured` 状态，`auth remove --provider relay-example-relay` 删除 key。
安装 route 与 credential 独立：移除一个不会偷偷删除或创建另一个。

调用时使用安装生成的 Provider ID 和自己选择的模型：

```sh
qxnm-forge run "你好" \
  --provider relay-example-relay \
  --model model-v1

qxnm-forge-dotnet run "你好" \
  --provider relay-example-relay \
  --model model-v1
```

CLI 能从唯一的本地 route 补全 API family；RPC 客户端应显式发送
`openai-completions`、`openai-responses` 或 `anthropic-messages`。请求目标只会在固定 HTTPS
base 后同源追加 `/chat/completions`、`/responses` 或 `/messages`。缺 credential、损坏的
store 或错误权限都在 Session durable 记录和网络请求之前失败关闭。

## CredentialStore 安全边界

v0.1 是权限受限文件型 store，不是系统加密 keychain。Unix 私有目录固定 `0700`、文档和
lock 固定 `0600`，写入使用独占锁、同目录 create-new 临时文件、flush 和 replace；读取
拒绝 symlink/reparse file、超限、重复 key、未知字段和过宽权限。stored source 失败时绝不
静默回退环境变量。

Windows 版本已经使用独占共享模式、reparse 检查和原子 replace，但 DPAPI/平台 Credential
Manager ACL 门禁尚未完成，不能声称等同系统密钥链保护。极高敏感部署应等待平台 backend
或在外层使用受管密钥代理。

## 缓存、故障与密钥轮换

验签成功的完整 envelope 写入工作区外的内容寻址 create-new 缓存。网络、HTTP 或远端
验证失败时，客户端可继续展示仍在有效期内的最高缓存，并打印明确警告。`--offline` 保证
不联网。缓存每次读取都重新验签；过期、损坏、错误 keyId 和非 canonical Base64 不会因
“曾经下载过”而获得信任。

私钥疑似泄露时：

1. 立即停止使用旧私钥；
2. 生成新 keyId 和新密钥；
3. 通过客户端更新或用户显式 `configure` 安装新公钥；
4. 再发布由新私钥签署的更高版本目录；
5. 删除远端旧 envelope，但把旧私钥按安全事件流程保全或销毁。

远端文件不能自行轮换公钥，否则掌握旧私钥或静态托管账号的攻击者可以替换信任根。

## 验证

双向签名共同门禁：

```sh
python3 CONFORMANCE/sponsored_catalog_runner.py \
  --rust-command-json '["./rust/target/debug/qxnm-forge"]' \
  --dotnet-command-json '["dotnet","./dotnet/src/QxnmForge.Cli/bin/Release/net10.0/qxnm-forge-dotnet.dll"]'
```

门禁执行 Rust 签名→.NET 验证、.NET 签名→Rust 验证、两个 golden JSON 比较和两个 payload
篡改负例。默认测试不访问中转站、模型服务或付费 Provider。

当前能力矩阵保持 `implemented`：签名和双端格式已经通过共同黑盒门禁，.NET 还以注入
HTTP 覆盖 remote→offline cache；但 Rust/.NET 尚未共同执行真实本机 HTTPS server 的网络
获取与回退门禁，因此不能提前登记为 `conformant`。目录本身也不代表中转站已可调用。
