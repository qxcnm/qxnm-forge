# ADR 0032：实验性桌面 Computer 共同边界

- 状态：Accepted
- 日期：2026-07-23
- 项目：qxnm-forge
- 作者：高宏顺 `<18272669457@163.com>`

## 背景

Rust 与 .NET 已有 Linux X11 桌面捕获和 XTEST 输入注入原型，但三个工具的参数、审批风险、
artifact 绑定、资源上限和平台广告尚未由共同规范冻结。仅凭能打开 X display 或环境变量存在就
广告工具，会把 XWayland 当作完整桌面控制、把默认跳过的实机测试当作支持证据，并让两套实现
产生不同的审批与 Session 记录。

桌面截图会捕获工作区之外的完整可见内容，可能包含 credential、卡密、聊天、通知或其他敏感
信息。截图虽然可以成为 portable artifact，当前各 Provider adapter 尚未把工具结果中的
`image_ref` 转换为视觉输入，application service/UI 也没有经过边界验证的 artifact 读取与渲染
闭环。因此“能够生成 PNG”不等于模型能够观察屏幕，也不等于端到端 Computer Use 可用。

## 决策

### Community、状态与广告门禁

`computer.observe`、`computer.screenshot` 与 `computer.interact` 是 Open/Pro 共同的
Community 行为，不是 Pro entitlement。两套原生实现可把对应 foundation claim 登记为
`implemented`，但在共同动态 conformance 完成前不得登记为 `conformant`、
`live-verified` 或公开支持。

工具默认完全不广告。只有以下两个品牌中立环境门同时精确等于字符串 `1`，实现才可以继续探测
桌面 backend：

- `AGENT_CLIENT_DESKTOP_COMPUTER=1`；
- `AGENT_CLIENT_EXPERIMENTAL_DESKTOP_COMPUTER=1`。

缺少任一门、值为空或使用其他值都等价于未启用；实现不得因此探测 display、读取像素或注册任一
`computer.*` 工具。环境门只允许进入探测，不授予工具执行权，也不能替代 policy、project
trust、逐次审批或 Provider 能力交集。

首版只允许 Linux 原生 X11 Session。实现必须正向证明当前 Session 是原生 X11，并在
`XDG_SESSION_TYPE=wayland`、存在 Wayland compositor 连接、XWayland 或无法排除 XWayland
时拒绝全部工具；仅仅成功 `XOpenDisplay`、`query_pointer` 或 XTEST query 不足以证明原生
X11。共同判定要求 `XDG_SESSION_TYPE` 仅去除 ASCII 首尾空白并按 ASCII lowercase 规范化后精确等于
`x11`，同时 `WAYLAND_DISPLAY` 必须缺失或为空；session type 缺失、空、非 `x11` 或
`WAYLAND_DISPLAY` 非空都必须在 display 探测前零广告。Windows、macOS、Wayland portal、
远程桌面和容器内未知 display 均不在本 ADR 的支持范围。

`DISPLAY` 还必须在每次启动探测、审批前 runtime preflight 与审批后执行边界重新通过保守的
本地 Unix-display allowlist。首版只接受 `:N[.S]` 或 `unix/:N[.S]`：整个值最多 64 个 ASCII
字符，`N` 与可选 `S` 都必须是非空 ASCII 十进制且数值位于 `0..65535`。缺失、空、首尾空白、
非 ASCII、`unix:N`、hostname/TCP、`localhost:10.0` 一类 SSH forwarding、空数字段、多余点或
越界数字全部失败关闭，并且必须在 `XOpenDisplay`/连接调用前拒绝。这个语法 allowlist 只排除
明确的远程表示，不把容器挂载的本地 socket 或其他未知宿主边界提升为受支持环境。通过语法
验证后，两种输入都必须重建为 `unix/:N[.S]` 并显式传给连接库；不得使用默认 display 参数，
也不得在 Unix socket 失败后回退 localhost TCP。allowlist 不认证 socket 对端；显式开启双门的
operator 仍必须把所选本地 X server/socket 作为受信任宿主边界。
探测结果和 durable 数据不得保存原始 `DISPLAY`、backend 名、display ID、socket、主机名或宿主
路径。

### 工具与审批合同

`SPEC/schemas/domain/computer.schema.json` 是参数和结果的共同机器合同：

- `computer.observe {}` 与 `computer.screenshot {}` 只接受空对象；两者都生成一次当前
  root desktop PNG，permission class 为 `outside_workspace`、risk 为 `high`、resource 为
  `{kind:"other",value:"desktop:screen"}`。
- `computer.interact` 只接受以 `action` 区分的 `move`、`click`、`scroll`、`key`
  四类封闭对象；不接受文本批量输入、单字符快捷路径或未知动作。permission class 为
  `outside_workspace`，risk 为 `critical`。
- `move` 要求 `x/y`；`click` 还要求 `button=left|middle|right` 与 `clicks=1..3`；
  `scroll` 要求 `deltaX/deltaY=-20..20`；两者同时为零是安全成功 no-op，以保持受限工具
  schema 与 preflight 的接受集合一致。`key` 只允许 schema 中冻结的 named key 与唯一的
  `shift|control|alt|meta` 修饰键。
- 坐标 schema 范围是 `0..16383`，但执行前仍必须按本次真实 root geometry 重新验证
  `x < width && y < height`；schema 上限不能授权屏幕外坐标。
- observe/screenshot 与每一个 interact 动作都必须单独产生审批，只提供
  `allow_once|deny`。不得缓存、合并或从之前的截图/动作推导持续授权。interact resource 必须
  精确绑定为 `desktop:move|click|scroll|key`，operation hash 绑定完整规范化参数。

参数 schema、完整动作可执行性、当前 geometry 和 backend 都必须在审批前预检；审批后执行边界
再次验证不变量。取消、超时或部分失败必须尽力释放已经按下的按键/按钮，并准确报告 outcome；
不能把仅停止等待阻塞线程描述为已中止原生输入。当前没有阻塞桌面调用的超时强隔离证据。

### 捕获上限与 portable artifact

实现必须在请求 X11 root image 或分配像素缓冲之前，用 checked arithmetic 同时验证：

- `width <= 16384` 且 `height <= 16384`；
- `width * height <= 16,777,216` pixels；
- 预计 RGBA/raw buffer `<= 67,108,864` bytes；
- 编码完成的 PNG `<= 33,554,432` bytes。

任何维度、乘法、原始缓冲或 PNG 上限失败都必须在持久化前拒绝，不能先分配完整超限图像再检查。
通用 Session artifact 总配额仍可更严格；达到更严格配额同样失败关闭。

Rust 当前使用的 x11rb 0.13 transport 会在适配层取得 reply 前按服务端 header 的 `u32 length`
扩容 packet buffer，`GetImage` parser 随后还会复制 payload。因此上述检查约束合法本地 X server
返回和本实现后续缓冲，却不是恶意或被替换 X socket 下的 transport hard allocation cap。显式
Unix-only 连接降低暴露面但不消除该风险；在引入有界 transport/fork 或进程级硬隔离并完成动态
攻击门禁前，这一缺口阻止 Rust desktop computer 提升为 `conformant`。

.NET 当前通过进程内 Xlib 执行桌面调用。临时 `XSetErrorHandler` 与 `XSync` 可以把普通异步
协议错误映射为脱敏失败，但 X server 致命断连仍可能进入进程级 XIO handler；Xlib 不提供可安全
返回到托管调用栈的恢复合同，默认行为可能写入原生诊断并终止 daemon。在把 Xlib 边界移入受控
helper process 或替换为可恢复 transport 并完成断连攻击门禁前，该风险同样阻止 .NET desktop
computer 提升为 `conformant`。

成功捕获生成 `image/png` 的 durable artifact，并在工具结果中使用 `image_ref`。完整桌面
PNG 按 `desktop_sensitive` 处理，不内联到 journal、日志或审批；artifact reference 与
`artifact.created.data.extensions` 可以携带同一个
`org.agentprotocol.computer` 值：

```json
{
  "source": "desktop_capture",
  "runId": "opaque-run-id",
  "sensitivity": "desktop_sensitive",
  "retention": "session_lifecycle"
}
```

该 extension 是封闭对象。它不得包含 backend/display ID、原始 `DISPLAY`、宿主路径、窗口标题、
像素、base64 或 secret。捕获发布前必须复查 `runId` 仍是同一 Session 的 active run；
`runId` 放在 extension 中，不得作为现行 `artifact.created.data` 的未知核心字段。

归档 Session 会连同 PNG 一起保留；ADR 0030 的永久 `session/delete` 必须连同该 Session 的
computer artifacts 删除。失败诊断不得复制 PNG、屏幕文字、窗口标题或可逆宿主标识。审批原因
必须明确说明会读取完整可见桌面并在 Session 生命周期内持久化敏感截图；未来向 Provider 发送
像素时还必须明确披露该边界。

### 视觉闭环与 capability 声明

端到端模型观察至少要求同一精确 Provider route 同时达到 `tools` 与 `image_input`
`conformant`，并由 adapter 有界读取、复核 hash/MIME 后映射 `image_ref` 为该 family 的原生
视觉工具结果。当前 capability matrix 没有一条 route 同时满足这两个条件，各 adapter 仍只消费
工具结果文本。

当前 application service 也没有经过规范与大小约束的 artifact 读取方法，React transcript
不能渲染 durable screenshot。因此 UI 必须把 Computer Use 显示为实验性且未就绪；仅看到三个
工具和审批方法不能标为可用。启用双环境门也不能改变 Provider/UI 的真实能力声明。

## 共同 fixture 与状态提升

`CONFORMANCE/fixtures/computer/computer-cases.json` 只包含合成平台/环境门真值表、空观察参数、
安全合成动作、synthetic artifact reference、固定限制和 extension；测试不得把这些动作发送到
真实桌面，也不包含真实 PNG、路径、窗口内容、credential 或 Provider 请求。当前共同测试只证明
广告算法合同、Schema、风险、resource、限制、extension 和 capability claim 的静态一致性，
不证明任一实现确实在 display 探测前执行了该算法。

提升为 `conformant` 前，Rust 与 .NET 必须分别通过同一个真实 daemon 黑盒 runner，至少覆盖：
双门缺失时零广告、Wayland/XWayland 拒绝、观察可用而 XTEST 不可用、审批与 operation hash、
捕获前限制、PNG/Session 配额、取消与输入释放、X11 错误检查、active-run 绑定、journal schema、
Provider 视觉续接和 UI artifact 读取/渲染；Rust 还必须证明恶意 reply length 不会在适配层校验
前触发超限 transport 分配，.NET 还必须证明 XIO 断连不会终止 daemon 或泄漏原生诊断。实机
Xvfb/XTEST 测试只能作为独立显式门禁，不能用默认静默跳过代替共同证据。

## 后果

- Open 与 Pro 使用同一实验性 Community 合同，不通过 entitlement 扩大或收窄核心语义。
- 默认安装不读取桌面、不注入输入，也不广告 `computer.*`。
- portable Session 保持品牌中立，现行 journal schema 不因顶层 `runId` 被破坏。
- 原生 X11、Provider 图像续接、UI artifact 读取、跨平台与强取消证据未齐全时，能力保持
  `implemented` 且不构成产品支持声明。
