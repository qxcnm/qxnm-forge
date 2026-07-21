# ADR 0031：品牌中立 Provider 配置模板目录

- 状态：Accepted
- 日期：2026-07-22
- 项目：qxnm-forge
- 作者：高宏顺 `<18272669457@163.com>`

## 背景

工作台需要向用户提供 OpenAI-compatible Provider 的常见配置起点，但前端不能维护一份与
`providers.v1.json`、`models.v1.json` 和后端 adapter 漂移的私有 preset 列表。另一方面，
Provider identity 存在于冻结目录并不代表当前 daemon 已有 credential、可执行 adapter 或经过
远端验证；直接把 35 个 canonical Provider 当作可选连接会产生虚假的能力声明。

## 决策

application service 新增只读方法 `providerCatalog/list {}`，成功返回
`{templates:[...]}`。每项严格包含：

- `templateId`：冻结 Provider ID，仅作为模板身份；
- `displayName`：稳定、面向用户的显示名；
- `suggestedProviderId`：使用 `custom-` 前缀的建议连接 ID，避免遮蔽 canonical ID；
- `apiFamily`：首版固定为 `openai-completions`；
- `defaultBaseUrl`：从冻结模型目录中同 Provider/family 的共同 fixed HTTPS endpoint 推导；
- `modelDiscovery`：首版固定为 `openai-models`；
- `logoAssetId`：只有发行包确实携带受信任本地资源时才非空，当前全部为 `null`。

模板候选必须同时满足：manifest 存在 text `openai-completions` route、使用
`openai-completions-v1` adapter 与单一 `api-key` auth profile，并且该 Provider/family 的全部
冻结模型行都使用同一个无占位符 fixed HTTPS base URL。Cloudflare 的 template endpoint、特殊
认证 route 和 GitHub OAuth/token route 不进入目录。当前规则得到 20 个模板，按 ASCII
`templateId` 升序返回。

`modelDiscovery:"openai-models"` 只表示用户保存连接后可以显式尝试 ADR 0029 的 Bearer
`GET baseUrl + /models` 契约，不是远端支持声明。失败时 UI 必须保留手工模型 ID 回退，不能把
冻结 catalog 模型自动写成远端发现结果。模板也不表示 configured、credential available、
executable、conformant 或 live verified；只有随后创建的自定义连接同时满足启用、credential 与
非空模型 allowlist，且 daemon 重启后出现在 `models/list`，才能进入运行选择。

目录完全由本地冻结文件计算，不读取 CredentialStore、不发送网络请求、不修改 connection、
Session 或 application database。返回值不得包含环境变量名、credential 状态、认证 header、
私有 endpoint、推广链接或收费权益。两套实现必须原生解析同一冻结目录，并由测试锁定候选数量、
顺序、endpoint 推导和排除规则；不能通过调用另一语言实现共享运行时结果。

## 后果

React 可以用同一个 capability-gated RPC 替换手写 preset，但仍需把模板明确标为“兼容配置
建议”。未来新增 family、非 Bearer 认证、带 endpoint binding 的模板或真实 Logo 时，必须先扩展
Schema 与筛选规则，不能用未知字段或显示文案暗示额外能力。
