# ADR 0017：Manifest 驱动的 Provider 广告与模型过滤

- Status: Accepted
- Date: 2026-07-15
- Project: qxnm-forge
- Author: 高宏顺 `<18272669457@163.com>`

## 背景

v1 已冻结 35 个 Provider 身份、45 条路由和 1,076 条模型证据，但这些静态数据并不
等于当前进程可用能力。现有实现如果分别维护 `initialize` Provider 列表、
`models/list` 模型列表和 adapter 注册表，容易出现有凭据却无 endpoint、有 adapter
却无公共能力，或同一 Provider 的一个 family 被另一个 family 错误带出的情况。

现有协议已经有两个足够的入口：`initialize.capabilities.providers` 给控制器一个
Provider 级摘要，`models/list` 用 `apiFamily` 给出可选择的模型路由。因此无需新增
Provider 品牌 RPC，也无需把 adapter、凭据来源或能力矩阵内部结构暴露到 wire。

## 决策

1. 每条 canonical 路由独立计算以下交集：manifest 中存在、认证可用、必需
   endpoint/template 配置可用、native adapter 已实现、公共能力策略允许。任一条件
   不成立，整条路由都不得进入广告。默认无 live 配置时只保留 conformance `faux`，
   不广告任何 canonical Provider。
2. `models/list` 是路由级事实源。live 模型身份为
   `(providerId, modelId, apiFamily)`，按该三元组稳定排序。相同
   `(providerId, modelId)` 跨 family 时必须保留多条描述。
3. `initialize.capabilities.providers` 从同一个可用模型快照派生。每个 Provider 的
   `models` 是各可用 family 模型 ID 的排序去重并集；没有可用路由的 Provider 整项
   省略。它不是第二份独立配置。
4. OpenRouter 的文本与图像目录有两个重名模型。`models/list` 保留两条 family
   描述，`initialize` 并集只保留一个 model ID，`run/start` 的歧义规则继续要求显式
   `apiFamily`。不得根据 prompt 或输出模态猜测。
5. `runtime-required` 端点没有显式配置时必定不可用。template 的每个 binding 都要
   满足；`runtimeEndpointEnv` 非空时至少有一个列出的替代项可用。路由列出的多个
   auth profile 按 OR 处理。
6. 广告计算是本地、无副作用快照。`initialize`/`models/list` 不得发送 Provider
   请求、解析 Provider DNS、访问 cloud metadata/credential endpoint、启动 OAuth
   浏览器、刷新 token 或探测 endpoint；需要这些 I/O 才能成立的认证路径在当前快照
   中视为不可用。
7. 离线黑盒 gate 使用 runner 临时生成的品牌中立 presence 配置，只表达 auth、
   endpoint、adapter 和 capability 判定是否存在，不保存真实值。runner 清除继承的
   Provider/cloud 环境，把 proxy 固定为 dead loopback 且只为 literal loopback
   设置 `NO_PROXY`，使用临时 home 和随机 canary，不发送模型请求，也不连接真实
   endpoint。argv、双流 reader 队列、总输出与总 deadline 必须有硬上限；超限和
   timeout 必须清理 runner 自有进程树。未知字段与重复键 fail closed，canary 不得
   进入协议或诊断。
8. presence gate 只验证选择算法，不修改 `capabilities.json`。只有共同门禁通过并且
   矩阵状态达到 `conformant` 或 `live-verified`，才能形成公共 Provider 支持声明。

## 协议兼容性

协议版本保持 `0.1`。本决策收紧既有字段的派生与一致性规则，没有增加 RPC、必需
字段或枚举值。现有 `providerCapability.models` 继续是字符串并集；路由详情继续由
既有 `models/list` 的 `apiFamily` 表达。核心对象仍拒绝未知字段，NDJSON 对象仍拒绝
重复键，因此不需要修改 JSON Schema wire shape。

## 后果

- 35 个静态身份不会因为被写进 manifest 就自动出现在运行时广告中。
- 同 Provider 多 family 可以部分可用，且不会相互放大支持范围。
- credential、endpoint、adapter ID 和矩阵内部值都留在进程内部。
- 五种语言可以独立实现相同交集算法，并由一个完全离线的黑盒 runner 比较结果。
- gate 通过是实现证据，不是 live credential 或付费 Provider 可用性的证明。
