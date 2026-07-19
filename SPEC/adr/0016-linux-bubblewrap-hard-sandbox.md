# ADR 0016：Linux Bubblewrap hard sandbox 后端

- 状态：已接受，实验性 foundation capability
- 日期：2026-07-14
- 作者：高宏顺 `<18272669457@163.com>`

## 背景

路径检查、环境过滤、cwd、进程组和 `/proc` 后代守卫都不能阻止已批准的任意本机
代码直接读取宿主文件、联网或启动旁路进程，因此不能声明 hard sandbox。当前 Linux
环境提供 Bubblewrap；它能在启动用户代码前建立 user/mount/PID/network/IPC/UTS
namespace，并以只读系统运行时、显式工作区 bind 和空临时目录构造新的根视图。

## 决策

新增可选 `linux-bwrap-v1` 后端。它必须显式配置，不改变现有 process/shell 默认
策略，也不绕过审批。只有以下全部成立时才可广告 `security.hard_sandbox`：

1. Bubblewrap 通过固定绝对路径发现，文件为 root 所有、普通文件且不可被当前用户
   写入；版本探针在有界超时内成功。
2. startup self-test 真实创建 user、mount、PID、network、IPC、UTS namespace；
   检查宿主工作区外的随机 canary 文件不可见、sandbox `/tmp` 可写、系统只读路径
   不可写、network namespace 已隔离、整体执行有 deadline、stdout/stderr 合并输出有
   硬上限，且 timeout、output-limit 或父进程结束都能清理完整进程树。
3. 每次执行使用参数数组而非 shell 拼接，并至少启用
   `--unshare-user --unshare-pid --unshare-net --unshare-ipc --unshare-uts
   --unshare-cgroup-try --die-with-parent --new-session`。
4. 建立空 root 视图；只读 bind 必需的 `/usr`、`/bin`、`/lib`、`/lib64`，挂载新的
   `/proc`、最小 `/dev`、tmpfs `/tmp`，只把 canonical workspace bind 到
   `/workspace`。系统不存在的兼容路径可省略，不能改为 bind 整个宿主 `/`。
5. 工作区权限由策略选择 `ro-bind` 或 `bind`；write 模式仍需要精确审批。cwd 映射到
   `/workspace` 下的已验证相对路径。宿主路径、credential 和完整环境不进入子进程。
6. 默认网络为完全隔离。未来若允许网络，必须是另一个命名 profile 与独立 origin
   policy；不能去掉 `--unshare-net` 后仍沿用此 capability claim。
7. sandbox setup、bind、namespace 或 self-test 任一步失败均在用户代码启动前失败关闭，
   返回 `sandbox_unavailable`；同一次请求不得重试 host executor、直接调用 host shell，
   或以任何其他方式静默降级到 host process。
8. 结果 `containment` 使用现有 `os_isolation`。能力描述同时公开后端 ID、平台、
   filesystem/network/process/credential 隔离维度，以及有界执行、fork/setsid 后代
   清理和 `sandbox_unavailable_no_host_fallback` 失败模式；不得只返回布尔值掩盖边界。

### 配置、发现与测试边界

`AGENT_HARD_SANDBOX_PROFILE=linux-bwrap-v1` 是唯一启用本 profile 的环境配置；缺失时
daemon 不广告 hard sandbox，普通 `process.exec` 仍走既有 host executor。生产 backend
固定为 `/usr/bin/bwrap`，不得搜索 `PATH`、跟随符号链接或从另一个环境变量选择替代
二进制。

共同 runner 需要制造 backend 失败时，可以设置
`AGENT_HARD_SANDBOX_CONFORMANCE_BACKEND`，但实现只有在 daemon argv 含精确
`--conformance` 且 `QXNM_FORGE_CONFORMANCE=1` 同时成立时才可读取它。任一门缺失时，该
变量的 presence 必须在读取其值或执行候选 backend 前失败关闭。`--conformance`、
`QXNM_FORGE_CONFORMANCE=1` 或 backend override 单独都不启用 profile，也不授权工具执行。

profile 被显式配置后，startup self-test 必须在接受 `initialize` 成功前完成。backend
身份、版本或任一完整 profile self-test 失败时，`initialize` 返回
`-32603/sandbox_unavailable` 后关闭连接；不得成功初始化为无 hard-sandbox capability
的 host 模式。只有 profile 未配置才允许正常初始化且省略 `capabilities.hardSandbox`。

## 共同门禁

共享 runner 必须创建宿主外 canary、工作区内 fixture 与只监听 `127.0.0.1` 随机端口的
network listener，并在 sandbox 内验证：workspace 读、可选写、cwd、argv、UTF-8、
`/tmp`、宿主外不可见、系统路径只读、宿主 listener 不可达、secret 环境不存在。
network 断言必须执行确定存在且经过身份检查的 `/usr/bin/python3`，以真实
`socket.connect_ex()` 尝试连接 listener；不得使用 dash 不支持的 `/dev/tcp`，也不得把
“探针命令不存在或语法失败”误当成网络隔离证据。

runner 的每个子进程都必须由同一有界监督路径持续排空 stdout/stderr；达到整体或案例
deadline、合并输出硬上限时终止完整进程树，并以 PID/starttime 身份确认没有残留。动态
门禁必须在 sandbox 内真实 `fork()` 后调用 `setsid()`：分别证明 timeout/output-limit
取消路径会清理该后代，并证明 Bubblewrap 的直接父进程结束后该后代也会消失。只杀直接
child 或原 process group 不足以通过。

runner 还必须通过正常 profile 启动入口故意传入不存在或用户可写的 bwrap 路径，并制造
一次可信 backend 的 bind/setup 失败；三者都必须返回 `sandbox_unavailable`、不启动用户
marker，且不存在 host fallback。setup 失败后再在 host 上执行同一脚本以“保持可用”是
明确不符合本 profile 的行为。

daemon 动态门禁不新增专用 RPC。它通过现有
`initialize -> faux/configure -> run/start -> approval/respond -> process.exec` 链驱动
`process.exec` 的 `sandbox` 参数；完整 `sandbox` 对象必须进入参数验证、规范化
operation hash 和人工审批 arguments。执行结果必须报告 `containment:"os_isolation"`。
runner 还会关闭 daemon 进程，验证已在 sandbox 内 `fork()+setsid()` 的后代不能越过
`--die-with-parent` 留下延迟 marker。测试 helper、workspace、listener 和 credential
canary 均为每次运行新建的离线资源；真实 Provider、公网和用户项目不在门禁范围内。

静态命令渲染或“本机有 bwrap”只能标 `implemented`。本语言 daemon 通过共同动态
runner 后，才可在 Linux 对该实现标 `conformant`；Windows/macOS 不由此获得任何
支持。容器宿主禁用 unprivileged user namespace 时，能力应诚实不可用。

## 后果

- Linux 可以提供真实 OS 隔离而不误称路径检查为 sandbox。
- 该后端是每种语言独立调用 OS primitive 的适配器；不共享其他语言 runtime。
- Bubblewrap 本身是可选外部系统依赖，不进入源代码或共享二进制。
- 它不是 VM，也不承诺防御内核漏洞、资源侧信道或已授权工作区内容泄漏；这些残余
  风险必须在中文 Wiki 中明确说明。
