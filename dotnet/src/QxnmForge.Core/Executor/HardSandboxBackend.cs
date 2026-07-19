using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using QxnmForge.Domain;
using QxnmForge.Tools;

namespace QxnmForge.Executor;

/// <summary>
/// 功能：描述已通过启动自检的 Linux Bubblewrap hard-sandbox 公共能力。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Profile">固定 linux-bwrap-v1。</param>
/// <param name="Platform">固定 linux。</param>
/// <param name="Filesystem">固定 mount namespace allowlist。</param>
/// <param name="Network">固定 network namespace isolated。</param>
/// <param name="Process">固定 PID namespace 与父退出清理语义。</param>
/// <param name="Credentials">固定 empty environment。</param>
/// <param name="Execution">有界执行与后代清理能力。</param>
/// <param name="FailureMode">固定失败关闭且不回退宿主 executor。</param>
/// <param name="SelfTested">只有真实启动自检成功时才为 true。</param>
public sealed record HardSandboxCapability(
    string Profile,
    string Platform,
    string Filesystem,
    string Network,
    string Process,
    string Credentials,
    HardSandboxExecutionCapability Execution,
    string FailureMode,
    bool SelfTested);

/// <summary>
/// 功能：描述 hard-sandbox 的 timeout、输出与后代清理不变量。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Timeout">固定 bounded。</param>
/// <param name="Output">固定 bounded。</param>
/// <param name="Descendants">固定 fork/setsid 清理声明。</param>
public sealed record HardSandboxExecutionCapability(
    string Timeout,
    string Output,
    string Descendants);

/// <summary>
/// 功能：原生调用可信 Bubblewrap 构造 linux-bwrap-v1，并复用有界进程监督器清理完整后代树。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class HardSandboxBackend : IDisposable
{
    public const string ProfileEnvironment = "AGENT_HARD_SANDBOX_PROFILE";
    public const string ConformanceBackendEnvironment = "AGENT_HARD_SANDBOX_CONFORMANCE_BACKEND";
    public const string ProfileName = "linux-bwrap-v1";
    public const string ProductionBackend = "/usr/bin/bwrap";

    private const int AtFileDescriptorCurrentWorkingDirectory = -100;
    private const int AtEmptyPath = 0x1000;
    private const int AtSymbolicLinkNoFollow = 0x100;
    private const int OpenCloseOnExec = 0x80000;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenPath = 0x200000;
    private const uint StatxBasicStats = 0x7ff;
    private const ushort DirectoryFileMode = 0x4000;
    private const ushort RegularFileMode = 0x8000;
    private const ushort FileTypeMask = 0xf000;
    private const ushort GroupOrOtherWriteMask = 0x0012;
    private const int ExecuteAccess = 1;
    private const int SignalKill = 9;
    private const int WriteAccess = 2;
    private static readonly string[] RuntimePaths = ["/usr", "/bin", "/lib", "/lib64"];
    private readonly TrustedFileIdentity backendIdentity;
    private readonly string backendPath;
    private readonly string workspace;
    private FileIdentity workspaceIdentity;
    private int workspaceDescriptor = -1;
    private bool disposed;
    private bool selfTesting;
    private bool selfTested;

    /// <summary>
    /// 功能：保存 Linux 文件系统对象的 device/inode/type 稳定身份。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="Inode">文件系统 inode。</param>
    /// <param name="DeviceMajor">设备 major。</param>
    /// <param name="DeviceMinor">设备 minor。</param>
    private readonly record struct FileIdentity(ulong Inode, uint DeviceMajor, uint DeviceMinor);

    /// <summary>
    /// 功能：冻结可信 Bubblewrap backend 的完整 statx 身份与安全相关元数据。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="Inode">文件系统 inode。</param>
    /// <param name="DeviceMajor">设备 major。</param>
    /// <param name="DeviceMinor">设备 minor。</param>
    /// <param name="UserId">固定 root 用户 ID 0。</param>
    /// <param name="Mode">文件类型及权限位。</param>
    /// <param name="Size">文件字节长度。</param>
    /// <param name="ModifiedSeconds">mtime 秒。</param>
    /// <param name="ModifiedNanoseconds">mtime 纳秒。</param>
    private readonly record struct TrustedFileIdentity(
        ulong Inode,
        uint DeviceMajor,
        uint DeviceMinor,
        uint UserId,
        ushort Mode,
        ulong Size,
        long ModifiedSeconds,
        uint ModifiedNanoseconds);

    /// <summary>
    /// 功能：保存 parent-exit 自检进程的 PID/start-time 身份以抵御 PID 复用。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="ProcessId">宿主 namespace 中的正 PID。</param>
    /// <param name="StartTime">Linux proc stat 字段 22 的启动时钟滴答。</param>
    /// <param name="ParentProcessId">捕获时 Linux proc stat 字段 4 的宿主父 PID。</param>
    /// <param name="SessionId">Linux proc stat 字段 6 的宿主 session leader PID。</param>
    /// <param name="NamespaceProcessId">Linux proc status NSpid 最内层 PID。</param>
    private readonly record struct ProcessIdentity(
        int ProcessId,
        ulong StartTime,
        int ParentProcessId,
        int SessionId,
        int NamespaceProcessId);

    /// <summary>
    /// 功能：保存已完成身份验证的固定 backend 与 canonical workspace。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="backendPath">root-owned、不可写的绝对 Bubblewrap 文件。</param>
    /// <param name="backendIdentity">创建时冻结的 backend statx 身份。</param>
    /// <param name="workspace">只允许 bind 进入 sandbox 的 canonical 工作区。</param>
    private HardSandboxBackend(
        string backendPath,
        TrustedFileIdentity backendIdentity,
        string workspace)
    {
        this.backendPath = backendPath;
        this.backendIdentity = backendIdentity;
        this.workspace = Path.GetFullPath(workspace);
    }

    /// <summary>
    /// 功能：按显式 profile 和双门控 conformance override 创建可自检的 hard-sandbox backend。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="workspace">现存 canonical 工作区。</param>
    /// <param name="cliConformance">daemon argv 是否显式包含 --conformance。</param>
    /// <param name="environmentConformance">QXNM_FORGE_CONFORMANCE 是否精确为 1。</param>
    /// <param name="error">配置缺失时为 null；非法 presence 或 backend 身份失败时返回安全错误。</param>
    /// <returns>profile 未启用时返回 null；启用且静态身份可信时返回 backend。</returns>
    /// <remarks>不变量：生产始终使用固定 /usr/bin/bwrap；任意路径 override 只在两个 conformance 门同时成立时读取。</remarks>
    public static HardSandboxBackend? CreateFromEnvironment(
        string workspace,
        bool cliConformance,
        bool environmentConformance,
        out PortableError? error)
    {
        error = null;
        var profile = Environment.GetEnvironmentVariable(ProfileEnvironment);
        var overridePath = Environment.GetEnvironmentVariable(ConformanceBackendEnvironment);
        if (overridePath is not null && (!cliConformance || !environmentConformance))
        {
            error = SandboxUnavailable();
            return null;
        }

        if (profile is null)
        {
            if (overridePath is not null)
            {
                error = SandboxUnavailable();
            }

            return null;
        }

        if (!OperatingSystem.IsLinux() || profile != ProfileName)
        {
            error = SandboxUnavailable();
            return null;
        }

        var selectedBackend = overridePath ?? ProductionBackend;
        try
        {
            var identity = ValidateBackend(selectedBackend);
            return new HardSandboxBackend(selectedBackend, identity, workspace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = SandboxUnavailable();
            return null;
        }
    }

    /// <summary>
    /// 功能：取得仅在真实 startup self-test 完成后可广告的完整 capability descriptor。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>自检成功返回冻结 descriptor，否则返回 null。</returns>
    /// <remarks>不变量：调用者不能仅凭 backend presence 获得 hardSandbox 广告。</remarks>
    public HardSandboxCapability? Capability => selfTested
        ? new HardSandboxCapability(
            ProfileName,
            "linux",
            "mount_namespace_allowlist",
            "network_namespace_isolated",
            "pid_namespace_die_with_parent",
            "empty_environment",
            new HardSandboxExecutionCapability(
                "bounded",
                "bounded",
                "fork_setsid_terminated_on_cancel_or_parent_exit"),
            "sandbox_unavailable_no_host_fallback",
            true)
        : null;

    /// <summary>
    /// 功能：有界验证 backend 版本及 namespace、文件、网络、环境和读写边界后允许能力广告。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="cancellationToken">daemon initialize 取消信号。</param>
    /// <returns>全部真实隔离探针成功时完成。</returns>
    /// <remarks>不变量：任一探针失败都保持 selfTested=false，且绝不转用宿主命令执行用户脚本。</remarks>
    /// <exception cref="ToolOperationException">版本、setup、隔离或执行界限失败，错误 kind 固定 sandbox_unavailable。</exception>
    public async Task EnsureSelfTestedAsync(CancellationToken cancellationToken)
    {
        if (selfTested)
        {
            return;
        }

        selfTested = false;
        RevalidateBackendIdentity();

        ProcessExecutionResult version;
        try
        {
            version = await ProcessExecutor.ExecuteAsync(
                new ProcessExecutionRequest(
                    "/bin/sh",
                    ["-c", "kill -STOP $$; exec \"$1\" --version", "qxnm-forge-bwrap-version", backendPath],
                    workspace,
                    null,
                    true,
                    TimeSpan.FromSeconds(2),
                    4096),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ToolOperationException)
        {
            throw SandboxUnavailableException();
        }

        if (version.TerminationReason != "exit" || version.ExitCode != 0)
        {
            throw SandboxUnavailableException();
        }

        EnsureWorkspaceHandle();

        var temporaryRoot = Path.Combine(Path.GetTempPath(), "qxnm-forge-sandbox-selftest-" + Guid.NewGuid().ToString("N"));
        var testWorkspace = Path.Combine(temporaryRoot, "workspace");
        Directory.CreateDirectory(testWorkspace);
        selfTesting = true;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(testWorkspace, "inside.txt"), "inside", cancellationToken).ConfigureAwait(false);
            var outside = Path.Combine(temporaryRoot, "outside-" + Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(outside, "outside", cancellationToken).ConfigureAwait(false);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using (var positiveControl = new TcpClient())
            {
                await positiveControl.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
            }

            var code = string.Join(
                '\n',
                "import os,socket,sys",
                "assert open('/workspace/inside.txt',encoding='utf-8').read() == 'inside'",
                "assert not os.path.exists(sys.argv[1])",
                "failed=False",
                "try:",
                "    open('/usr/qxnm-forge-sandbox-selftest','w').close()",
                "except OSError:",
                "    failed=True",
                "assert failed",
                "failed=False",
                "try:",
                "    open('/workspace/read-only-violation','w').close()",
                "except OSError:",
                "    failed=True",
                "assert failed",
                "client=socket.socket(socket.AF_INET,socket.SOCK_STREAM)",
                "client.settimeout(0.5)",
                "assert client.connect_ex(('127.0.0.1',int(sys.argv[2]))) != 0",
                "client.close()",
                "open('/tmp/qxnm-forge-selftest','w').write('ok')",
                "assert os.environ.get('QXNM_FORGE_EXECUTOR_SECRET_CANARY') is None",
                "print('sandbox-self-test',end='')");
            var readOnly = await ExecuteAsync(
                testWorkspace,
                testWorkspace,
                "read_only",
                "/usr/bin/python3",
                ["-c", code, outside, port.ToString(CultureInfo.InvariantCulture)],
                null,
                TimeSpan.FromSeconds(3),
                8192,
                cancellationToken).ConfigureAwait(false);
            if (readOnly.TerminationReason != "exit" || readOnly.ExitCode != 0 || readOnly.Stdout.Text != "sandbox-self-test")
            {
                throw SandboxUnavailableException();
            }

            var writeCode = "open('/workspace/write.txt','w').write('ok');print('write-self-test',end='')";
            var readWrite = await ExecuteAsync(
                testWorkspace,
                testWorkspace,
                "read_write",
                "/usr/bin/python3",
                ["-c", writeCode],
                null,
                TimeSpan.FromSeconds(3),
                8192,
                cancellationToken).ConfigureAwait(false);
            if (readWrite.TerminationReason != "exit" || readWrite.ExitCode != 0 ||
                readWrite.Stdout.Text != "write-self-test" || !File.Exists(Path.Combine(testWorkspace, "write.txt")))
            {
                throw SandboxUnavailableException();
            }

            await RunDescendantSelfTestAsync(testWorkspace, "timeout", cancellationToken).ConfigureAwait(false);
            await RunDescendantSelfTestAsync(testWorkspace, "output", cancellationToken).ConfigureAwait(false);
            await RunParentExitSelfTestAsync(testWorkspace, cancellationToken).ConfigureAwait(false);

            var bindProbe = await ExecuteAsync(
                workspace,
                workspace,
                "read_only",
                "/bin/true",
                [],
                null,
                TimeSpan.FromSeconds(3),
                4096,
                cancellationToken).ConfigureAwait(false);
            if (bindProbe.TerminationReason != "exit" || bindProbe.ExitCode != 0 ||
                bindProbe.Containment != "os_isolation")
            {
                throw SandboxUnavailableException();
            }

            RevalidateBackendIdentity();
            selfTested = true;
        }
        catch (ToolOperationException)
        {
            selfTested = false;
            throw SandboxUnavailableException();
        }
        catch (Exception exception) when (exception is IOException or SocketException or UnauthorizedAccessException or ArgumentException)
        {
            selfTested = false;
            throw SandboxUnavailableException();
        }
        finally
        {
            selfTesting = false;
            try
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 自检临时目录清理失败不改变隔离判定，且不向协议泄露宿主路径。
            }
        }
    }

    /// <summary>
    /// 功能：触发 timeout 或 output-limit，并用延迟 marker 证明 fork/setsid 后代未逃逸。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="testWorkspace">daemon 自建且只用于启动自检的临时可写工作区。</param>
    /// <param name="mode">固定 timeout 或 output。</param>
    /// <param name="cancellationToken">daemon initialize 取消信号。</param>
    /// <returns>相应执行边界触发、setsid 宿主身份消失且 escaped 未出现时完成。</returns>
    /// <remarks>不变量：namespace PID 必须映射到同一临时 workspace 的宿主 PID/start-time/session leader。</remarks>
    /// <exception cref="ToolOperationException">模式、终态、marker 或后代清理不符合 profile。</exception>
    private async Task RunDescendantSelfTestAsync(
        string testWorkspace,
        string mode,
        CancellationToken cancellationToken)
    {
        if (mode is not ("timeout" or "output"))
        {
            throw SandboxUnavailableException();
        }

        var readyName = mode + ".ready";
        var observedName = mode + ".observed";
        var escapedName = mode + ".escaped";
        var readyPath = Path.Combine(testWorkspace, readyName);
        var observedPath = Path.Combine(testWorkspace, observedName);
        var escapedPath = Path.Combine(testWorkspace, escapedName);
        var code = string.Join(
            '\n',
            "import os,sys,time",
            "ready,observed,escaped,mode=sys.argv[1:5]",
            "child=os.fork()",
            "if child==0:",
            "    os.setsid()",
            "    open(ready,'w',encoding='utf-8').write(str(os.getpid()))",
            "    deadline=time.monotonic()+0.7",
            "    while not os.path.exists(observed) and time.monotonic()<deadline:",
            "        time.sleep(0.005)",
            "    if not os.path.exists(observed): os._exit(73)",
            "    time.sleep(1.4)",
            "    open(escaped,'w',encoding='utf-8').write('escaped')",
            "    os._exit(0)",
            "deadline=time.monotonic()+0.5",
            "while not os.path.exists(ready) and time.monotonic()<deadline:",
            "    time.sleep(0.005)",
            "deadline=time.monotonic()+0.7",
            "while not os.path.exists(observed) and time.monotonic()<deadline:",
            "    time.sleep(0.005)",
            "if not os.path.exists(observed): sys.exit(73)",
            "if mode=='output':",
            "    chunk=b'x'*8192",
            "    while True:",
            "        os.write(1,chunk)",
            "while True:",
            "    time.sleep(0.05)");
        var execution = ExecuteAsync(
            testWorkspace,
            testWorkspace,
            "read_write",
            "/usr/bin/python3",
            [
                "-c",
                code,
                "/workspace/" + readyName,
                "/workspace/" + observedName,
                "/workspace/" + escapedName,
                mode,
            ],
            null,
            mode == "timeout" ? TimeSpan.FromMilliseconds(900) : TimeSpan.FromSeconds(3),
            65_536,
            cancellationToken);
        ProcessIdentity descendantIdentity;
        ProcessExecutionResult result;
        try
        {
            await WaitForNonEmptyRegularFileAsync(
                readyPath,
                TimeSpan.FromMilliseconds(700),
                cancellationToken).ConfigureAwait(false);
            descendantIdentity = FindSandboxSetSidIdentity(testWorkspace, readyPath);
            await File.WriteAllTextAsync(observedPath, "observed", cancellationToken).ConfigureAwait(false);
            result = await execution.ConfigureAwait(false);
        }
        catch
        {
            try
            {
                _ = await execution.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ToolOperationException or OperationCanceledException)
            {
                // 保留原始自检失败，同时确保同一监督任务已经完成清理。
            }

            throw;
        }

        var expectedReason = mode == "timeout" ? "timeout" : "output_limit";
        if (result.TerminationReason != expectedReason)
        {
            throw SandboxUnavailableException();
        }

        await WaitForIdentityGoneAsync(
            descendantIdentity,
            TimeSpan.FromSeconds(2),
            cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(1550), cancellationToken).ConfigureAwait(false);
        if (File.Exists(escapedPath))
        {
            throw SandboxUnavailableException();
        }

        File.Delete(readyPath);
        File.Delete(observedPath);
    }

    /// <summary>
    /// 功能：让 Bubblewrap 的真实宿主直接父进程退出，并验证 PID namespace 与 setsid 后代随之消失。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="testWorkspace">daemon 自建且只用于启动自检的临时可写工作区。</param>
    /// <param name="cancellationToken">daemon initialize 取消信号。</param>
    /// <returns>原 Bubblewrap 身份消失且延迟 escaped marker 未出现时完成。</returns>
    /// <remarks>不变量：helper 只启动代码生成的固定 argv；失败清理始终先重验 PID/start-time；不调用宿主 fallback。</remarks>
    /// <exception cref="ToolOperationException">helper、ready、身份消失或后代清理任一证据失败。</exception>
    private async Task RunParentExitSelfTestAsync(
        string testWorkspace,
        CancellationToken cancellationToken)
    {
        var temporaryRoot = Directory.GetParent(testWorkspace)?.FullName ?? throw SandboxUnavailableException();
        var controlPath = Path.Combine(temporaryRoot, "parent.control");
        var readyPath = Path.Combine(testWorkspace, "parent.ready");
        var observedPath = Path.Combine(testWorkspace, "parent.observed");
        var escapedPath = Path.Combine(testWorkspace, "parent.escaped");
        var probeCode = string.Join(
            '\n',
            "import os,time",
            "child=os.fork()",
            "if child==0:",
            "    os.setsid()",
            "    open('/workspace/parent.ready','w',encoding='utf-8').write(str(os.getpid()))",
            "    deadline=time.monotonic()+0.7",
            "    while not os.path.exists('/workspace/parent.observed') and time.monotonic()<deadline:",
            "        time.sleep(0.005)",
            "    if not os.path.exists('/workspace/parent.observed'): os._exit(73)",
            "    time.sleep(1.4)",
            "    open('/workspace/parent.escaped','w',encoding='utf-8').write('escaped')",
            "    os._exit(0)",
            "while True:",
            "    time.sleep(0.05)");
        var marker = "qxnm-forge-parent-self-test-" + Guid.NewGuid().ToString("N");
        var command = BuildCommand(
            testWorkspace,
            testWorkspace,
            false,
            testWorkspace,
            "read_write",
            "/usr/bin/python3",
            ["-c", probeCode],
            marker);
        using var helper = new Process
        {
            StartInfo = CreateParentExitHelperStartInfo(controlPath, readyPath, command),
            EnableRaisingEvents = true,
        };
        ProcessIdentity? helperIdentity = null;
        ProcessIdentity? identity = null;
        ProcessIdentity? descendantIdentity = null;
        var helperStarted = false;
        try
        {
            if (!ProcessExecutor.StartWithSpawnGate(helper))
            {
                throw SandboxUnavailableException();
            }

            helperStarted = true;
            helperIdentity = TryReadProcessIdentity(helper.Id) ?? throw SandboxUnavailableException();

            var stdoutTask = helper.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = helper.StandardError.ReadToEndAsync(CancellationToken.None);
            await WaitForNonEmptyRegularFileAsync(
                controlPath,
                TimeSpan.FromSeconds(2),
                cancellationToken).ConfigureAwait(false);
            identity = ReadProcessIdentity(controlPath, helperIdentity.Value);
            await WaitForNonEmptyRegularFileAsync(
                readyPath,
                TimeSpan.FromSeconds(2),
                cancellationToken).ConfigureAwait(false);
            descendantIdentity = FindSandboxSetSidIdentity(testWorkspace, readyPath);
            await File.WriteAllTextAsync(observedPath, "observed", cancellationToken).ConfigureAwait(false);
            RevalidateDirectChildIdentity(identity.Value, helperIdentity.Value);
            helper.StandardInput.Close();
            await WaitForHelperExitAsync(helper, cancellationToken).ConfigureAwait(false);
            _ = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            if (helper.ExitCode != 0)
            {
                throw SandboxUnavailableException();
            }

            await WaitForIdentityGoneAsync(
                identity.Value,
                TimeSpan.FromSeconds(2),
                cancellationToken).ConfigureAwait(false);
            await WaitForIdentityGoneAsync(
                descendantIdentity.Value,
                TimeSpan.FromSeconds(2),
                cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(1550), cancellationToken).ConfigureAwait(false);
            if (File.Exists(escapedPath))
            {
                throw SandboxUnavailableException();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or FormatException or OverflowException)
        {
            throw SandboxUnavailableException();
        }
        finally
        {
            await CleanupParentExitProbeAsync(
                helper,
                helperStarted,
                helperIdentity,
                controlPath,
                identity,
                descendantIdentity,
                testWorkspace,
                readyPath).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 功能：构造 parent-exit 自检专用的固定 Python helper 启动信息。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="controlPath">sandbox 外由 helper 独占写入 Bubblewrap PID 的文件。</param>
    /// <param name="readyPath">sandbox 内固定探针映射到宿主的 ready 文件。</param>
    /// <param name="command">不含 backend argv[0] 的完整 Bubblewrap 参数。</param>
    /// <returns>空环境、重定向 stdio 且直接启动 Bubblewrap 的 ProcessStartInfo。</returns>
    /// <remarks>不变量：helper 在 ready 后阻塞读取 stdin，调用方捕获身份后关闭 stdin 才触发真实父退出。</remarks>
    private ProcessStartInfo CreateParentExitHelperStartInfo(
        string controlPath,
        string readyPath,
        IReadOnlyList<string> command)
    {
        const string helperCode = "import os,subprocess,sys,time\ncontrol,ready=sys.argv[1:3]\nchild=subprocess.Popen(sys.argv[3:],stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,close_fds=True)\nstat=open(f'/proc/{child.pid}/stat',encoding='utf-8').read();fields=stat[stat.rfind(')')+1:].split();record=f'{child.pid}:{fields[19]}:{fields[1]}'\nif int(fields[1])!=os.getpid(): sys.exit(72)\ntemporary=control+'.tmp'\nwith open(temporary,'x',encoding='utf-8') as stream:\n stream.write(record);stream.flush();os.fsync(stream.fileno())\nos.replace(temporary,control)\ndeadline=time.monotonic()+2.0\nwhile not os.path.isfile(ready) or os.path.getsize(ready)==0:\n if child.poll() is not None or time.monotonic()>=deadline: sys.exit(71)\n time.sleep(0.01)\nsys.stdin.buffer.read(1)\nos._exit(0)\n";
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/python3",
            WorkingDirectory = Path.GetDirectoryName(controlPath) ?? "/tmp",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] = "/usr/bin:/bin";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(helperCode);
        startInfo.ArgumentList.Add(controlPath);
        startInfo.ArgumentList.Add(readyPath);
        startInfo.ArgumentList.Add(backendPath);
        foreach (var argument in command)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    /// <summary>
    /// 功能：有界等待 helper 在 stdin 关闭后退出，并区分调用取消与自检超时。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="helper">已启动且由本次自检独占的宿主 helper。</param>
    /// <param name="cancellationToken">daemon initialize 取消信号。</param>
    /// <returns>helper 在两秒内退出时完成。</returns>
    /// <exception cref="ToolOperationException">内部期限内未退出。</exception>
    private static async Task WaitForHelperExitAsync(Process helper, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await helper.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw SandboxUnavailableException();
        }
    }

    /// <summary>
    /// 功能：在成功、取消与异常路径统一关闭 helper，并按 PID/start-time 确认 Bubblewrap 身份消失。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="helper">本次自检独占的固定 Python helper。</param>
    /// <param name="helperStarted">Process.Start 已成功时为 true。</param>
    /// <param name="capturedHelperIdentity">spawn 后无 await 捕获的 helper PID/start-time。</param>
    /// <param name="controlPath">sandbox 外记录 Bubblewrap PID/start-time/PPID 的控制文件。</param>
    /// <param name="capturedIdentity">正常路径已捕获的身份；取消竞态时可为 null。</param>
    /// <param name="capturedDescendantIdentity">已映射的 sandbox setsid 后代身份；早期失败时可为 null。</param>
    /// <param name="testWorkspace">parent-exit 探针独占的临时 workspace。</param>
    /// <param name="readyPath">包含 namespace PID 的 setsid ready marker。</param>
    /// <returns>helper、Bubblewrap 与可识别 setsid 后代身份均消失时完成。</returns>
    /// <remarks>不变量：cleanup 使用独立有界期限，不复用已取消 token；只终止固定 helper 树或 start-time 匹配身份。</remarks>
    /// <exception cref="ToolOperationException">helper 或同一 Bubblewrap 身份在清理期限后仍存活。</exception>
    private static async Task CleanupParentExitProbeAsync(
        Process helper,
        bool helperStarted,
        ProcessIdentity? capturedHelperIdentity,
        string controlPath,
        ProcessIdentity? capturedIdentity,
        ProcessIdentity? capturedDescendantIdentity,
        string testWorkspace,
        string readyPath)
    {
        if (!helperStarted)
        {
            return;
        }

        var descendantIdentity = capturedDescendantIdentity;
        if (descendantIdentity is null && IsNonEmptyRegularFile(readyPath))
        {
            try
            {
                descendantIdentity = FindSandboxSetSidIdentity(testWorkspace, readyPath);
            }
            catch (Exception exception) when (exception is ToolOperationException or IOException or UnauthorizedAccessException)
            {
                // Bubblewrap 身份清理仍会终止 namespace；缺失映射使原自检保持失败。
            }
        }

        var identity = capturedIdentity;
        if (identity is null && capturedHelperIdentity is { } helperIdentity)
        {
            identity = await CaptureCleanupIdentityAsync(
                controlPath,
                helperIdentity,
                TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
        else if (identity is null)
        {
            var recorded = TryReadRecordedProcessIdentity(controlPath);
            if (recorded is { } recordedIdentity &&
                recordedIdentity.ParentProcessId == helper.Id &&
                IdentityIsAlive(recordedIdentity))
            {
                identity = recordedIdentity;
            }
        }
        var cleanupFailed = false;
        try
        {
            helper.StandardInput.Close();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ObjectDisposedException)
        {
            // stdin 已关闭或 helper 已退出时继续执行身份清理与消失确认。
        }

        try
        {
            if (!helper.HasExited)
            {
                helper.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // 继续使用控制文件或 PPID 捕获到的稳定身份清理 Bubblewrap。
        }

        using (var helperTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            try
            {
                await helper.WaitForExitAsync(helperTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cleanupFailed = true;
            }
            catch (InvalidOperationException)
            {
                cleanupFailed = true;
            }
        }

        if (identity is { } stableIdentity)
        {
            TerminateIdentity(stableIdentity);
            try
            {
                await WaitForIdentityGoneAsync(
                    stableIdentity,
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (ToolOperationException)
            {
                cleanupFailed = true;
            }
        }

        if (descendantIdentity is { } stableDescendantIdentity)
        {
            TerminateIdentity(stableDescendantIdentity);
            try
            {
                await WaitForIdentityGoneAsync(
                    stableDescendantIdentity,
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (ToolOperationException)
            {
                cleanupFailed = true;
            }
        }

        if (cleanupFailed)
        {
            throw SandboxUnavailableException();
        }
    }

    /// <summary>
    /// 功能：在取消竞态中从不可变控制记录或 helper 唯一直接子进程捕获稳定 Bubblewrap 身份。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="controlPath">sandbox 外控制记录路径。</param>
    /// <param name="helperIdentity">spawn 后捕获的固定 helper PID/start-time。</param>
    /// <param name="timeout">允许 helper 完成 Popen 与记录写入的短期限。</param>
    /// <returns>PID/start-time 身份；helper 尚未创建子进程即退出时为 null。</returns>
    /// <remarks>不变量：控制记录包含 helper 观察到的 PPID；proc 扫描只接受唯一直接子进程。</remarks>
    private static async Task<ProcessIdentity?> CaptureCleanupIdentityAsync(
        string controlPath,
        ProcessIdentity helperIdentity,
        TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        do
        {
            var recorded = TryReadRecordedProcessIdentity(controlPath);
            if (recorded is { } recordedIdentity &&
                recordedIdentity.ParentProcessId == helperIdentity.ProcessId &&
                IdentityIsAlive(recordedIdentity))
            {
                return recordedIdentity;
            }

            var directChild = TryFindUniqueDirectChild(helperIdentity);
            if (directChild is { })
            {
                return directChild;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), CancellationToken.None).ConfigureAwait(false);
        }
        while (Stopwatch.GetElapsedTime(started) < timeout);

        return null;
    }

    /// <summary>
    /// 功能：从 procfs 中寻找固定 helper 的唯一直接子进程并返回 PID/start-time 身份。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="helperIdentity">固定 Python helper 的 PID/start-time 身份。</param>
    /// <returns>恰有一个当前直接子进程时返回身份，否则返回 null。</returns>
    /// <remarks>不变量：不读取 cmdline/environment；多子进程或 procfs 竞态不会选择性误杀。</remarks>
    private static ProcessIdentity? TryFindUniqueDirectChild(ProcessIdentity helperIdentity)
    {
        if (!IdentityIsAlive(helperIdentity))
        {
            return null;
        }

        try
        {
            ProcessIdentity? result = null;
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
                {
                    continue;
                }

                var candidate = TryReadProcessIdentity(processId);
                if (candidate is not { ParentProcessId: var parent } || parent != helperIdentity.ProcessId)
                {
                    continue;
                }

                if (result is not null)
                {
                    return null;
                }

                result = candidate;
            }

            return IdentityIsAlive(helperIdentity) ? result : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 功能：把 sandbox ready 中的 namespace PID 映射为同一临时 workspace 下的宿主 setsid 身份。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="testWorkspace">daemon 自建并 bind 为 `/workspace` 的临时目录。</param>
    /// <param name="readyPath">固定探针写入最内层 namespace PID 的非空 marker。</param>
    /// <returns>PID/start-time 稳定且宿主 session leader 为自身的唯一进程身份。</returns>
    /// <remarks>不变量：候选 `/proc/&lt;pid&gt;/root/workspace` 必须与临时目录 device/inode 相同，避免其他 PID namespace 的同号 PID。</remarks>
    /// <exception cref="ToolOperationException">marker 非法、没有唯一匹配或 procfs 证据不可得。</exception>
    private static ProcessIdentity FindSandboxSetSidIdentity(string testWorkspace, string readyPath)
    {
        if (!int.TryParse(
            File.ReadAllText(readyPath, Encoding.UTF8).Trim(),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var namespaceProcessId) || namespaceProcessId <= 1)
        {
            throw SandboxUnavailableException();
        }

        var workspaceIdentity = ReadIdentity(
            AtFileDescriptorCurrentWorkingDirectory,
            Encoding.UTF8.GetBytes(Path.GetFullPath(testWorkspace) + '\0'),
            AtSymbolicLinkNoFollow,
            DirectoryFileMode);
        ProcessIdentity? result = null;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(
                    Path.GetFileName(directory),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var processId))
                {
                    continue;
                }

                var candidate = TryReadProcessIdentity(processId);
                if (candidate is not { } identity ||
                    identity.NamespaceProcessId != namespaceProcessId ||
                    identity.SessionId != identity.ProcessId)
                {
                    continue;
                }

                try
                {
                    var mountedWorkspace = ReadIdentity(
                        AtFileDescriptorCurrentWorkingDirectory,
                        Encoding.UTF8.GetBytes(
                            string.Create(CultureInfo.InvariantCulture, $"/proc/{processId}/root/workspace\0")),
                        0,
                        DirectoryFileMode);
                    if (mountedWorkspace != workspaceIdentity)
                    {
                        continue;
                    }
                }
                catch (ToolOperationException)
                {
                    continue;
                }

                if (result is not null)
                {
                    throw SandboxUnavailableException();
                }

                result = identity;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SandboxUnavailableException();
        }

        return result ?? throw SandboxUnavailableException();
    }

    /// <summary>
    /// 功能：有界等待 daemon 自建 marker 成为非空且非链接的普通文件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">daemon 自建临时根内的固定 marker 路径。</param>
    /// <param name="timeout">正等待期限。</param>
    /// <param name="cancellationToken">daemon initialize 取消信号。</param>
    /// <returns>marker 满足类型与非空条件时完成。</returns>
    /// <exception cref="ToolOperationException">期限结束仍未取得有效 marker。</exception>
    private static async Task WaitForNonEmptyRegularFileAsync(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            if (IsNonEmptyRegularFile(path))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }

        throw SandboxUnavailableException();
    }

    /// <summary>
    /// 功能：判断 self-test marker 是否为非链接且非空的普通文件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">daemon 自建临时根内的候选 marker。</param>
    /// <returns>存在、非 reparse point 且长度大于零时为 true。</returns>
    /// <remarks>不变量：异常、链接、目录和空文件统一按无效处理。</remarks>
    private static bool IsNonEmptyRegularFile(string path)
    {
        try
        {
            var information = new FileInfo(path);
            return information.Exists &&
                (information.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 &&
                information.Length > 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 功能：从 helper 控制文件解析 PID，并读取 Linux proc stat start-time 形成稳定身份。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="controlPath">sandbox 外只含 Bubblewrap PID/start-time/PPID 的控制文件。</param>
    /// <param name="expectedParentIdentity">当前仍存活的固定 helper PID/start-time。</param>
    /// <returns>当前仍存在的同一 Bubblewrap 身份。</returns>
    /// <exception cref="ToolOperationException">PID 非法、进程竞态消失或 proc stat 无法解析。</exception>
    private static ProcessIdentity ReadProcessIdentity(
        string controlPath,
        ProcessIdentity expectedParentIdentity)
    {
        var recorded = TryReadRecordedProcessIdentity(controlPath);
        if (!IdentityIsAlive(expectedParentIdentity) ||
            recorded is not { } identity ||
            identity.ParentProcessId != expectedParentIdentity.ProcessId)
        {
            throw SandboxUnavailableException();
        }

        var current = TryReadProcessIdentity(identity.ProcessId);
        if (current is not { } currentIdentity ||
            currentIdentity.StartTime != identity.StartTime ||
            currentIdentity.ParentProcessId != expectedParentIdentity.ProcessId)
        {
            throw SandboxUnavailableException();
        }

        return currentIdentity;
    }

    /// <summary>
    /// 功能：在关闭 helper stdin 的紧邻位置重验同一 Bubblewrap 仍由同一 helper 直接拥有。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="childIdentity">先前从原子 control 记录与 procfs 双重捕获的 Bubblewrap 身份。</param>
    /// <param name="parentIdentity">spawn 后无 await 捕获的固定 helper 身份。</param>
    /// <remarks>不变量：helper 与 child 的 start-time 都必须不变，child PPID 必须仍等于 helper PID；验证后不得再 await 才触发父退出。</remarks>
    /// <exception cref="ToolOperationException">任一身份消失、复用或父关系变化。</exception>
    private static void RevalidateDirectChildIdentity(
        ProcessIdentity childIdentity,
        ProcessIdentity parentIdentity)
    {
        var currentChild = TryReadProcessIdentity(childIdentity.ProcessId);
        if (!IdentityIsAlive(parentIdentity) ||
            currentChild is not { } currentIdentity ||
            currentIdentity.StartTime != childIdentity.StartTime ||
            currentIdentity.ParentProcessId != parentIdentity.ProcessId)
        {
            throw SandboxUnavailableException();
        }
    }

    /// <summary>
    /// 功能：解析 helper 原子写入的 PID/start-time/PPID 控制记录，格式或 I/O 异常安全返回 null。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="controlPath">sandbox 外由固定 helper 独占创建的控制文件。</param>
    /// <returns>字段均合法且 PID/PPID 为正时返回记录身份，否则为 null。</returns>
    /// <remarks>不变量：本方法只解析记录；调用方仍需对当前 proc stat 重验 start-time。</remarks>
    private static ProcessIdentity? TryReadRecordedProcessIdentity(string controlPath)
    {
        try
        {
            var fields = File.ReadAllText(controlPath, Encoding.UTF8)
                .Trim()
                .Split(':', StringSplitOptions.None);
            return fields.Length == 3 &&
                int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var processId) &&
                ulong.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var startTime) &&
                int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var parentProcessId) &&
                processId > 1 && parentProcessId > 0
                ? new ProcessIdentity(processId, startTime, parentProcessId, 0, processId)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 功能：读取指定 Linux PID 的 proc stat 与 NSpid，竞态或格式错误安全返回 null。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="processId">待读取的正宿主 PID。</param>
    /// <returns>PID/start-time/PPID/session/NSpid 身份；进程不存在或格式无效时为 null。</returns>
    /// <remarks>不变量：不读取 cmdline 或 environment，也不根据 PID 单独认定身份。</remarks>
    private static ProcessIdentity? TryReadProcessIdentity(int processId)
    {
        try
        {
            var text = File.ReadAllText(
                string.Create(CultureInfo.InvariantCulture, $"/proc/{processId}/stat"),
                Encoding.UTF8);
            var closing = text.LastIndexOf(')');
            if (closing < 0)
            {
                return null;
            }

            var fields = text[(closing + 1)..].Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length <= 19 ||
                !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentProcessId) ||
                !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var sessionId) ||
                !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var startTime))
            {
                return null;
            }

            var namespaceProcessId = processId;
            foreach (var line in File.ReadLines(
                string.Create(CultureInfo.InvariantCulture, $"/proc/{processId}/status"),
                Encoding.UTF8))
            {
                if (!line.StartsWith("NSpid:", StringComparison.Ordinal))
                {
                    continue;
                }

                var values = line.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (values.Length < 2 ||
                    !int.TryParse(values[^1], NumberStyles.None, CultureInfo.InvariantCulture, out namespaceProcessId))
                {
                    return null;
                }

                break;
            }

            return new ProcessIdentity(
                processId,
                startTime,
                parentProcessId,
                sessionId,
                namespaceProcessId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 功能：按 PID/start-time 有界等待原 Bubblewrap 身份消失。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="identity">helper 仍存活时捕获的 Bubblewrap 身份。</param>
    /// <param name="timeout">正等待期限。</param>
    /// <param name="cancellationToken">daemon initialize 取消信号。</param>
    /// <returns>同一身份消失或 PID 已复用时完成。</returns>
    /// <exception cref="ToolOperationException">期限内原身份仍存活；抛出前会按身份尽力 SIGKILL。</exception>
    private static async Task WaitForIdentityGoneAsync(
        ProcessIdentity identity,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (IdentityIsAlive(identity) && Stopwatch.GetElapsedTime(started) < timeout)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }

        if (IdentityIsAlive(identity))
        {
            TerminateIdentity(identity);
            throw SandboxUnavailableException();
        }
    }

    /// <summary>
    /// 功能：仅在 PID 与 start-time 均匹配时认定 parent-exit 自检进程仍存活。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="identity">先前捕获的 Bubblewrap 身份。</param>
    /// <returns>当前 proc stat 仍对应同一启动实例时为 true。</returns>
    private static bool IdentityIsAlive(ProcessIdentity identity)
    {
        return TryReadProcessIdentity(identity.ProcessId) is { } current &&
            current.StartTime == identity.StartTime;
    }

    /// <summary>
    /// 功能：只向 PID/start-time 仍匹配的 parent-exit 自检进程发送 SIGKILL。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="identity">先前捕获的 Bubblewrap 身份。</param>
    /// <remarks>不变量：拒绝 PID 1、当前 daemon 与已复用 PID；信号失败只影响当前自检失败路径。</remarks>
    private static void TerminateIdentity(ProcessIdentity identity)
    {
        if (identity.ProcessId > 1 && identity.ProcessId != Environment.ProcessId && IdentityIsAlive(identity))
        {
            _ = Kill(identity.ProcessId, SignalKill);
        }
    }

    /// <summary>
    /// 功能：把已经批准的 process/shell argv 放入精确 linux-bwrap-v1 profile 并有界执行。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="canonicalWorkspace">工具 registry 复验后的 canonical 工作区。</param>
    /// <param name="workingDirectory">工作区内已存在 cwd。</param>
    /// <param name="workspaceAccess">read_only 或 read_write。</param>
    /// <param name="executable">已解析且可映射到 /workspace 或只读系统 runtime 的 executable。</param>
    /// <param name="arguments">保持元素边界的用户 argv。</param>
    /// <param name="standardInput">可选有界 stdin。</param>
    /// <param name="timeout">包括 sandbox setup 在内的执行期限。</param>
    /// <param name="outputLimitBytes">用户 stdout/stderr 合计硬上限。</param>
    /// <param name="cancellationToken">run 取消信号。</param>
    /// <returns>完成清理且 containment 固定 os_isolation 的执行结果。</returns>
    /// <remarks>不变量：不 bind 宿主根；setup marker 未出现时返回 sandbox_unavailable，绝不 host fallback。</remarks>
    /// <exception cref="ToolOperationException">backend 未自检、路径不可映射或 sandbox setup 失败。</exception>
    public async Task<ProcessExecutionResult> ExecuteAsync(
        string canonicalWorkspace,
        string workingDirectory,
        string workspaceAccess,
        string executable,
        IReadOnlyList<string> arguments,
        ReadOnlyMemory<byte>? standardInput,
        TimeSpan timeout,
        int outputLimitBytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if ((!selfTested && !selfTesting) || Path.GetFullPath(canonicalWorkspace) != workspace && !selfTesting ||
            workspaceAccess is not ("read_only" or "read_write"))
        {
            throw SandboxUnavailableException();
        }

        RevalidateBackendIdentity();

        var requestedWorkspace = Path.GetFullPath(canonicalWorkspace);
        var marker = "qxnm-forge-sandbox-started-" + Guid.NewGuid().ToString("N");
        var markerText = marker + "\n";
        var markerBytes = Encoding.UTF8.GetByteCount(markerText);
        ProcessExecutionResult result;
        try
        {
            var bindSourceIsDescriptor = !selfTesting || requestedWorkspace == workspace;
            var execution = bindSourceIsDescriptor
                ? ProcessExecutor.RunWithSpawnGate(() =>
                {
                    var descriptor = DuplicateWorkspaceDescriptor();
                    try
                    {
                        return StartSandboxProcess(
                            canonicalWorkspace,
                            descriptor.ToString(CultureInfo.InvariantCulture),
                            true,
                            workingDirectory,
                            workspaceAccess,
                            executable,
                            arguments,
                            standardInput,
                            timeout,
                            outputLimitBytes,
                            marker,
                            markerBytes,
                            cancellationToken);
                    }
                    finally
                    {
                        _ = Close(descriptor);
                    }
                })
                : StartSandboxProcess(
                    canonicalWorkspace,
                    requestedWorkspace,
                    false,
                    workingDirectory,
                    workspaceAccess,
                    executable,
                    arguments,
                    standardInput,
                    timeout,
                    outputLimitBytes,
                    marker,
                    markerBytes,
                    cancellationToken);

            result = await execution.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException)
        {
            throw SandboxUnavailableException();
        }

        var markerIndex = result.Stderr.Text.IndexOf(markerText, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw SandboxUnavailableException();
        }

        var stderr = new ProcessStreamResult(
            result.Stderr.Text.Remove(markerIndex, markerText.Length),
            Math.Max(0, result.Stderr.CapturedBytes - markerBytes),
            Math.Max(0, result.Stderr.TotalBytes - markerBytes));
        return result with
        {
            Containment = "os_isolation",
            Stderr = stderr,
        };
    }

    /// <summary>
    /// 功能：构造单次 Bubblewrap 监督请求并在返回 Task 前同步完成 Process.Start。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="canonicalWorkspace">唯一允许进入 namespace 的宿主工作区。</param>
    /// <param name="bindSource">临时路径或已继承 fd 的十进制值。</param>
    /// <param name="bindSourceIsDescriptor">使用 --bind-fd/--ro-bind-fd 时为 true。</param>
    /// <param name="workingDirectory">已验证的工作区 cwd。</param>
    /// <param name="workspaceAccess">read_only 或 read_write。</param>
    /// <param name="executable">已映射到工作区或系统 allowlist 的 executable。</param>
    /// <param name="arguments">保持元素边界的用户 argv。</param>
    /// <param name="standardInput">可选有界 stdin。</param>
    /// <param name="timeout">包括 setup 的总期限。</param>
    /// <param name="outputLimitBytes">不含内部 setup marker 的用户输出上限。</param>
    /// <param name="marker">随机 setup marker。</param>
    /// <param name="markerBytes">marker 加 LF 的 UTF-8 字节数。</param>
    /// <param name="cancellationToken">run 取消信号。</param>
    /// <returns>已同步 spawn、正在由统一进程树监督器执行的 Task。</returns>
    /// <remarks>不变量：descriptor 模式只从 ProcessSpawnGate 临界区调用，使 fd 清除 CLOEXEC 的窗口不与其他 spawn 重叠。</remarks>
    private Task<ProcessExecutionResult> StartSandboxProcess(
        string canonicalWorkspace,
        string bindSource,
        bool bindSourceIsDescriptor,
        string workingDirectory,
        string workspaceAccess,
        string executable,
        IReadOnlyList<string> arguments,
        ReadOnlyMemory<byte>? standardInput,
        TimeSpan timeout,
        int outputLimitBytes,
        string marker,
        int markerBytes,
        CancellationToken cancellationToken)
    {
        var command = BuildCommand(
            canonicalWorkspace,
            bindSource,
            bindSourceIsDescriptor,
            workingDirectory,
            workspaceAccess,
            executable,
            arguments,
            marker);
        var supervisedArguments = new List<string>
        {
            "-c",
            "kill -STOP $$; exec \"$@\"",
            "qxnm-forge-bwrap-launch",
            backendPath,
        };
        supervisedArguments.AddRange(command);
        return ProcessExecutor.ExecuteAsync(
            new ProcessExecutionRequest(
                "/bin/sh",
                supervisedArguments,
                canonicalWorkspace,
                standardInput,
                true,
                timeout,
                checked(outputLimitBytes + markerBytes)),
            cancellationToken);
    }

    /// <summary>
    /// 功能：渲染固定 Bubblewrap argv、精确 workspace bind、cwd 映射与空环境启动包装器。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="canonicalWorkspace">唯一允许进入 namespace 的宿主工作区。</param>
    /// <param name="bindSource">自测临时根或单次继承的生产 workspace fd 十进制值。</param>
    /// <param name="bindSourceIsDescriptor">生产 bind 使用 fd 参数时为 true；临时自检路径 bind 为 false。</param>
    /// <param name="workingDirectory">工作区内 cwd。</param>
    /// <param name="workspaceAccess">精确 ro-bind/bind 选择。</param>
    /// <param name="executable">待映射 executable。</param>
    /// <param name="arguments">原样 argv。</param>
    /// <param name="marker">setup 成功后由固定包装器最先写入 stderr 的随机标记。</param>
    /// <returns>可直接交给可信 backend 的独立参数数组。</returns>
    /// <remarks>不变量：必含 user/PID/network/IPC/UTS/cgroup namespace、die-with-parent 与 new-session。</remarks>
    private static List<string> BuildCommand(
        string canonicalWorkspace,
        string bindSource,
        bool bindSourceIsDescriptor,
        string workingDirectory,
        string workspaceAccess,
        string executable,
        IReadOnlyList<string> arguments,
        string marker)
    {
        var workspace = Path.GetFullPath(canonicalWorkspace);
        var relativeCwd = Path.GetRelativePath(workspace, Path.GetFullPath(workingDirectory));
        if (relativeCwd == ".." || relativeCwd.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw SandboxUnavailableException();
        }

        var sandboxCwd = relativeCwd == "."
            ? "/workspace"
            : "/workspace/" + relativeCwd.Replace(Path.DirectorySeparatorChar, '/');
        var command = new List<string>
        {
            "--unshare-user",
            "--unshare-pid",
            "--unshare-net",
            "--unshare-ipc",
            "--unshare-uts",
            "--unshare-cgroup-try",
            "--die-with-parent",
            "--new-session",
            "--proc", "/proc",
            "--dev", "/dev",
            "--tmpfs", "/tmp",
        };
        foreach (var runtimePath in RuntimePaths)
        {
            if (Directory.Exists(runtimePath) || File.Exists(runtimePath))
            {
                command.Add("--ro-bind");
                command.Add(runtimePath);
                command.Add(runtimePath);
            }
        }

        command.Add(bindSourceIsDescriptor
            ? workspaceAccess == "read_write" ? "--bind-fd" : "--ro-bind-fd"
            : workspaceAccess == "read_write" ? "--bind" : "--ro-bind");
        command.Add(bindSource);
        command.Add("/workspace");
        command.Add("--chdir");
        command.Add(sandboxCwd);
        command.Add("--clearenv");
        command.Add("--setenv");
        command.Add("PATH");
        command.Add("/usr/bin:/bin");
        command.Add("/bin/sh");
        command.Add("-c");
        command.Add("printf '%s\\n' \"$1\" >&2; shift; exec \"$@\"");
        command.Add("qxnm-forge-sandbox-wrapper");
        command.Add(marker);
        command.Add(MapExecutable(workspace, executable));
        command.AddRange(arguments);
        return command;
    }

    /// <summary>
    /// 功能：把已解析 executable 限定映射到 /workspace 或四个只读系统 runtime 根。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="workspace">canonical 工作区。</param>
    /// <param name="executable">宿主绝对 executable。</param>
    /// <returns>sandbox 内绝对 executable。</returns>
    /// <exception cref="ToolOperationException">executable 位于未 bind 的宿主路径。</exception>
    private static string MapExecutable(string workspace, string executable)
    {
        var absolute = Path.GetFullPath(executable);
        var relative = Path.GetRelativePath(workspace, absolute);
        if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return "/workspace/" + relative.Replace(Path.DirectorySeparatorChar, '/');
        }

        if (RuntimePaths.Any(root =>
            absolute == root || absolute.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            return absolute;
        }

        throw SandboxUnavailableException();
    }

    /// <summary>
    /// 功能：以 O_PATH/O_DIRECTORY/O_NOFOLLOW 冻结生产 workspace 根并记录 device/inode 身份。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <remarks>不变量：后续 Bubblewrap bind 只引用该稳定 fd，而不是可被 rename 替换的缓存路径。</remarks>
    /// <exception cref="ToolOperationException">打开失败、路径为链接/非目录或 fd/path 身份不一致。</exception>
    private void EnsureWorkspaceHandle()
    {
        if (workspaceDescriptor >= 0)
        {
            RevalidateWorkspaceIdentity();
            return;
        }

        var nativePath = Encoding.UTF8.GetBytes(workspace + '\0');
        var descriptor = Open(
            nativePath,
            OpenPath | OpenDirectory | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0)
        {
            throw SandboxUnavailableException();
        }

        try
        {
            var fromDescriptor = ReadIdentity(descriptor, [0], AtEmptyPath, DirectoryFileMode);
            var fromPath = ReadIdentity(
                AtFileDescriptorCurrentWorkingDirectory,
                nativePath,
                AtSymbolicLinkNoFollow,
                DirectoryFileMode);
            if (fromDescriptor != fromPath)
            {
                throw SandboxUnavailableException();
            }

            workspaceDescriptor = descriptor;
            workspaceIdentity = fromDescriptor;
        }
        catch
        {
            _ = Close(descriptor);
            throw;
        }
    }

    /// <summary>
    /// 功能：逐次比较 canonical 路径、稳定 fd 与 startup 身份，拒绝根目录 rename/symlink/替换竞态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <exception cref="ToolOperationException">任一身份消失、类型变化或 device/inode 不同。</exception>
    private void RevalidateWorkspaceIdentity()
    {
        if (workspaceDescriptor < 0)
        {
            throw SandboxUnavailableException();
        }

        var fromDescriptor = ReadIdentity(
            workspaceDescriptor,
            [0],
            AtEmptyPath,
            DirectoryFileMode);
        var fromPath = ReadIdentity(
            AtFileDescriptorCurrentWorkingDirectory,
            Encoding.UTF8.GetBytes(workspace + '\0'),
            AtSymbolicLinkNoFollow,
            DirectoryFileMode);
        if (fromDescriptor != workspaceIdentity || fromPath != workspaceIdentity)
        {
            throw SandboxUnavailableException();
        }
    }

    /// <summary>
    /// 功能：逐次重验稳定 workspace 后复制一个仅供本次 Bubblewrap spawn 继承的 fd。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>FD_CLOEXEC 已清除且 device/inode 与 startup 身份一致的单次 fd。</returns>
    /// <remarks>不变量：原 O_PATH fd 始终 CLOEXEC；返回副本在 spawn 后由父进程立即关闭，Bubblewrap 以 --bind-fd 消费。</remarks>
    /// <exception cref="ToolOperationException">路径身份漂移、dup 或副本身份检查失败。</exception>
    private int DuplicateWorkspaceDescriptor()
    {
        RevalidateWorkspaceIdentity();
        var descriptor = Duplicate(workspaceDescriptor);
        if (descriptor < 0)
        {
            throw SandboxUnavailableException();
        }

        try
        {
            if (ReadIdentity(descriptor, [0], AtEmptyPath, DirectoryFileMode) != workspaceIdentity)
            {
                throw SandboxUnavailableException();
            }

            return descriptor;
        }
        catch
        {
            _ = Close(descriptor);
            throw;
        }
    }

    /// <summary>
    /// 功能：通过 statx 读取指定 fd/path 的目录或普通文件稳定身份。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="directoryFileDescriptor">AT_FDCWD 或 O_PATH fd。</param>
    /// <param name="path">NUL 终止 UTF-8 路径；AT_EMPTY_PATH 时为空串。</param>
    /// <param name="flags">AT_SYMLINK_NOFOLLOW 或 AT_EMPTY_PATH。</param>
    /// <param name="expectedType">S_IFDIR 或 S_IFREG。</param>
    /// <returns>device/inode 身份。</returns>
    /// <exception cref="ToolOperationException">statx 失败或类型不符。</exception>
    private static FileIdentity ReadIdentity(
        int directoryFileDescriptor,
        byte[] path,
        int flags,
        ushort expectedType)
    {
        if (Statx(directoryFileDescriptor, path, flags, StatxBasicStats, out var metadata) != 0 ||
            (metadata.Mode & FileTypeMask) != expectedType)
        {
            throw SandboxUnavailableException();
        }

        return new FileIdentity(metadata.Inode, metadata.DeviceMajor, metadata.DeviceMinor);
    }

    /// <summary>
    /// 功能：释放 startup 冻结的 workspace O_PATH fd，不删除或改写工作区。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        if (workspaceDescriptor >= 0)
        {
            _ = Close(workspaceDescriptor);
            workspaceDescriptor = -1;
        }

        disposed = true;
    }

    /// <summary>
    /// 功能：用 statx/access 验证 backend 为 root-owned、非链接 regular executable 且当前用户不可写。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">配置得到的候选绝对路径。</param>
    /// <remarks>不变量：身份检查完成前绝不执行候选文件。</remarks>
    /// <exception cref="ArgumentException">路径非绝对、身份/权限不可信或系统检查失败。</exception>
    private static TrustedFileIdentity ValidateBackend(string path)
    {
        var nativePath = Encoding.UTF8.GetBytes(path + '\0');
        if (!Path.IsPathFullyQualified(path) ||
            Statx(AtFileDescriptorCurrentWorkingDirectory, nativePath, AtSymbolicLinkNoFollow, StatxBasicStats, out var metadata) != 0 ||
            (metadata.Mode & FileTypeMask) != RegularFileMode ||
            metadata.UserId != 0 ||
            (metadata.Mode & GroupOrOtherWriteMask) != 0 ||
            Access(nativePath, WriteAccess) == 0 ||
            Access(nativePath, ExecuteAccess) != 0)
        {
            throw new ArgumentException("hard sandbox backend is unavailable", nameof(path));
        }

        return new TrustedFileIdentity(
            metadata.Inode,
            metadata.DeviceMajor,
            metadata.DeviceMinor,
            metadata.UserId,
            metadata.Mode,
            metadata.Size,
            metadata.ModifiedTime.Seconds,
            metadata.ModifiedTime.Nanoseconds);
    }

    /// <summary>
    /// 功能：在能力广告前及每次启动前重验 backend 身份、所有权、权限、长度与 mtime。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <remarks>不变量：任一身份漂移都在用户 argv spawn 前失败关闭；不得执行新路径对象或回退宿主 executor。</remarks>
    /// <exception cref="ToolOperationException">backend 消失、替换、权限变化或安全策略不再成立。</exception>
    private void RevalidateBackendIdentity()
    {
        try
        {
            if (ValidateBackend(backendPath) != backendIdentity)
            {
                throw SandboxUnavailableException();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw SandboxUnavailableException();
        }
    }

    /// <summary>
    /// 功能：创建不含 backend、路径或宿主诊断的稳定 sandbox_unavailable 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>-32603、retryable=false 的 portable error。</returns>
    private static PortableError SandboxUnavailable()
    {
        return new PortableError(
            -32603,
            "hard sandbox is unavailable",
            false,
            new ErrorDetails("sandbox_unavailable"));
    }

    /// <summary>
    /// 功能：创建工具/初始化共同使用且不含宿主细节的失败关闭异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>terminationReason=internal_error 的受控异常。</returns>
    private static ToolOperationException SandboxUnavailableException()
    {
        return new ToolOperationException(SandboxUnavailable(), "internal_error");
    }

    /// <summary>
    /// 功能：调用 Linux statx 且禁止跟随最终符号链接读取可信文件身份。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="directoryFileDescriptor">相对解析目录；本实现固定 AT_FDCWD。</param>
    /// <param name="path">以 NUL 终止的 UTF-8 backend 路径。</param>
    /// <param name="flags">固定 AT_SYMLINK_NOFOLLOW。</param>
    /// <param name="mask">请求 basic stats。</param>
    /// <param name="buffer">内核填充的 statx 身份前缀。</param>
    /// <returns>0 成功，其他值失败并由调用者关闭能力。</returns>
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int directoryFileDescriptor,
        [In] byte[] path,
        int flags,
        uint mask,
        out StatxBuffer buffer);

    /// <summary>
    /// 功能：调用 Linux access 检查当前真实用户是否可执行或可写候选 backend。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">以 NUL 终止且已 statx 验证的 backend 路径。</param>
    /// <param name="mode">X_OK 或 W_OK。</param>
    /// <returns>允许时 0，否则非零。</returns>
    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int Access(
        [In] byte[] path,
        int mode);

    /// <summary>
    /// 功能：调用 Linux open 创建不跟随链接的 workspace O_PATH 目录句柄。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">NUL 终止 UTF-8 路径。</param>
    /// <param name="flags">O_PATH/O_DIRECTORY/O_NOFOLLOW/O_CLOEXEC 组合。</param>
    /// <returns>非负 fd，失败时为 -1。</returns>
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([In] byte[] path, int flags);

    /// <summary>
    /// 功能：调用 Linux close 释放 backend 独占的 workspace fd。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="descriptor">非负 fd。</param>
    /// <returns>0 成功，其他值表示已尽力释放。</returns>
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    /// <summary>
    /// 功能：复制稳定 workspace fd，并由 Linux 保证新 fd 的 FD_CLOEXEC 初始为清除状态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="descriptor">startup 冻结且带 CLOEXEC 的 O_PATH fd。</param>
    /// <returns>非负单次继承 fd，失败时为 -1。</returns>
    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int Duplicate(int descriptor);

    /// <summary>
    /// 功能：向已按 PID/start-time 重验的 parent-exit 自检进程发送 Linux 信号。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="processId">已重验且不为 1/daemon 的正 PID。</param>
    /// <param name="signal">固定 SIGKILL。</param>
    /// <returns>0 成功，其他值表示进程已退出或权限拒绝。</returns>
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct StatxBuffer
    {
        internal uint Mask;
        internal uint BlockSize;
        internal ulong Attributes;
        internal uint HardLinkCount;
        internal uint UserId;
        internal uint GroupId;
        internal ushort Mode;
        internal ushort Spare;
        internal ulong Inode;
        internal ulong Size;
        internal ulong Blocks;
        internal ulong AttributesMask;
        internal StatxTimestamp AccessTime;
        internal StatxTimestamp BirthTime;
        internal StatxTimestamp ChangeTime;
        internal StatxTimestamp ModifiedTime;
        internal uint DeviceSpecialMajor;
        internal uint DeviceSpecialMinor;
        internal uint DeviceMajor;
        internal uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp
    {
        internal long Seconds;
        internal uint Nanoseconds;
        internal int Reserved;
    }
}
