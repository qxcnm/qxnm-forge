# ADR 0015：OpenRouter Images 的 artifact-first 一致性

- 状态：已接受，协议 v0.1
- 日期：2026-07-14
- 作者：高宏顺 `<18272669457@163.com>`
- 参考快照：PI `3f9aa5d10b35223abf6146f960ff5cb5c68053ee`

## 背景

冻结的 v1 清单包含 35 个 OpenRouter 图像模型和一个
`openrouter-images` API family。PI 快照证明其原生 wire 是
`POST /chat/completions`、Bearer 认证、`stream:false`、按模型输出能力选择
`modalities:["image"]` 或 `["image","text"]`，图像位于
`choices[0].message.images[].image_url` 的 data URL 中。

PI 将 base64 放在进程内领域对象里；这不能直接成为公共语义，因为大图会突破
NDJSON 帧限制并可能泄漏到 Session、日志、错误或 UI。现有协议已经具备
`image_ref`、`artifact.created` 和 assistant message，但
`assistantContent` 尚未接受图像，而且同一 `providerId/modelId` 可能同时有文本和
图像路由。

## 决策

1. 不新增 `openrouter/*` RPC。图像生成复用 `run/start`、普通事件、
   `session/get` 和 assistant message。
2. `providerSelection` 增加可选、品牌中立的 `apiFamily`。选择图像路由时必须显式
   为 `openrouter-images`；唯一的文本路由可继续省略，保持向后兼容。
3. `assistantContent` 接受 `image_ref`。图像不存在 delta；完整响应通过验证后才
   artifact-first 发布，再追加 assistant message，最后发送 `message.completed`。
4. wire 只接受 canonical base64 data URL 里的 PNG/JPEG/WebP/GIF。拒绝 SVG、
   remote URL、percent encoding、空数据、宽松 base64、MIME/魔数不匹配、重复 JSON
   key、无效 UTF-8 和尾随 JSON。
5. 输入 `image_ref` 必须属于同一 Session 的更早 `artifact.created`，并通过
   no-follow regular-file、length、SHA-256、media type 和魔数复核。host path 从不
   进入 wire。
6. 上限冻结为 8 张输入、8 张输出、输入解码总计 16 MiB、输出解码总计
   32 MiB、HTTP response 48 MiB、输入/输出文字各 256 KiB；每张图还受协商的
   `maxArtifactBytes` 约束。
7. 先验证并解码整批图像，再发布第一张。每张 artifact 单独完成临时文件、大小
   限制、flush、无覆盖原子发布、目录 flush、`artifact.created` append。若后续发布
   失败，先前 artifact 保持有效但不被 message 引用；append-only journal 不回滚。
8. 可选 usage 必须是非负安全整数且 `totalTokens = inputTokens + outputTokens`。
9. 429、5xx、`Retry-After`、timeout、cancel、断流、redirect/origin、proxy 和秘密
   脱敏沿用公共 HTTP Provider 规则。默认测试只使用 loopback mock 与 synthetic
   credential。
10. stdout、Session、artifact metadata、日志、错误和测试诊断不得包含 base64、
    data URL、remote URL、credential、原始 response body 或 host path。协议只公开
    artifact reference；读取 artifact bytes 属于受授权的宿主/客户端数据面，不通过
    NDJSON 内联传输。

## 一致性证据

公共 image suite 必须至少验证：请求 path/header/body/modalities、文字与多图顺序、
artifact/message/event 的 durable 顺序、PNG/JPEG/WebP/GIF 正例、remote URL、SVG、
坏 base64、MIME 欺骗、超数/超限、usage、429/503 retry、断流、timeout、cancel，
以及 credential/base64/data URL 不进入 stdout、stderr、journal 或非 artifact 文件。

通过静态 fixture 或语言单元测试只能标记 `implemented`。只有该实现用本语言原生
HTTP、artifact store、Session 与 daemon 完整通过共同黑盒 suite，能力矩阵才可标记
`conformant`；真实 OpenRouter smoke 显式启用并成功后才可标记 `live-verified`。

## 后果

- 后续改名不会改变 RPC、event、Session kind 或 content type。
- 图像结果可以跨五语言打开和继续，不要求另一语言理解 Provider 原始响应。
- `apiFamily` 消除了同模型多路由歧义，但旧的唯一文本路由请求仍有效。
- 基础魔数检查只验证封装一致性，不等于安全解码，更不能声明 hard sandbox。
- 未引用 artifact 的垃圾回收必须证明所有分支均无引用，不能按 selected branch
  简单删除。

