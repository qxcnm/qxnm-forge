using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using QxnmForge.Domain;
using QxnmForge.Tools;

namespace QxnmForge.Executor;

/// <summary>
/// 功能：描述不经 shell 的原生进程启动与有限资源策略。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Executable">预检解析后的可执行文件。</param>
/// <param name="Arguments">保持边界的 argv。</param>
/// <param name="WorkingDirectory">已验证工作目录。</param>
/// <param name="StandardInput">可选、最大 1 MiB 的独立 stdin 字节流。</param>
/// <param name="ResumeAfterContainment">命令是否会在安全固定前缀中自停并等待守卫建立后恢复。</param>
/// <param name="Timeout">总执行时限。</param>
/// <param name="OutputLimitBytes">stdout 与 stderr 合计硬上限。</param>
public sealed record ProcessExecutionRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    ReadOnlyMemory<byte>? StandardInput,
    bool ResumeAfterContainment,
    TimeSpan Timeout,
    int OutputLimitBytes);

/// <summary>
/// 功能：保存单个 pipe 的 UTF-8 replacement 捕获文本与精确原始字节账。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Text">由捕获字节前缀解码的文本。</param>
/// <param name="CapturedBytes">实际保留的原始字节数。</param>
/// <param name="TotalBytes">pipe 关闭前实际读取的原始字节数。</param>
public sealed record ProcessStreamResult(
    string Text,
    long CapturedBytes,
    long TotalBytes);

/// <summary>
/// 功能：保存已启动进程完成清理后的规范终态、隔离等级和分离双流结果。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ExitCode">仅 exit 终态的退出码。</param>
/// <param name="Signal">可可靠识别的 signal 名；当前 System.Diagnostics 后端不能识别时为 null。</param>
/// <param name="TerminationReason">exit、signal、timeout、cancelled 或 output_limit。</param>
/// <param name="DurationMs">启动成功至完成进程树清理的单调毫秒数。</param>
/// <param name="Containment">本次实际建立的 portable containment 等级。</param>
/// <param name="Stdout">有界 stdout。</param>
/// <param name="Stderr">有界 stderr。</param>
public sealed record ProcessExecutionResult(
    int? ExitCode,
    string? Signal,
    string TerminationReason,
    long DurationMs,
    string Containment,
    ProcessStreamResult Stdout,
    ProcessStreamResult Stderr);

/// <summary>
/// 功能：使用 System.Diagnostics.Process 原生执行 argv，并约束环境、输出、时间和整棵进程树。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ProcessExecutor
{
    private const int MaxStandardInputBytes = 1_048_576;
    private static readonly object ProcessSpawnGate = new();
    private static readonly string[] AllowedEnvironmentNames =
    [
        "PATH", "HOME", "USER", "LOGNAME", "LANG", "LC_ALL", "LC_CTYPE", "TMPDIR", "TMP", "TEMP",
        "SystemRoot", "ComSpec", "PATHEXT",
    ];

    /// <summary>
    /// 功能：探测当前宿主是否可建立 Linux 进程组和 PID/start-time 后代守卫。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>仅 Linux、procfs 与固定 setsid 入口都可用时为 true。</returns>
    /// <remarks>不变量：本方法只读探测且不启动进程；Windows/macOS 未实机证明的能力不会被广告。</remarks>
    public static bool IsConformantPlatformAvailable()
    {
        return OperatingSystem.IsLinux() &&
            File.Exists("/proc/self/stat") &&
            (File.Exists("/usr/bin/setsid") || File.Exists("/bin/setsid"));
    }

    /// <summary>
    /// 功能：启动独立进程组，持续排空 stdout/stderr，并在取消、超时或超限时终止整树。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">已预检 executable、argv、cwd 与有限资源策略。</param>
    /// <param name="cancellationToken">run 取消信号。</param>
    /// <returns>子进程启动成功后的所有终态及分离输出；非零、取消、超时和超限不是 executor 异常。</returns>
    /// <remarks>不变量：不经过 shell、不继承 credential 环境；stdin 独立有界；所有终态共用整树清理；本能力不是 hard sandbox。</remarks>
    /// <exception cref="ArgumentException">请求边界非法且尚未启动进程。</exception>
    /// <exception cref="ToolOperationException">启动或强后代 containment 建立失败。</exception>
    public static async Task<ProcessExecutionResult> ExecuteAsync(
        ProcessExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var started = Stopwatch.GetTimestamp();
        using var process = new Process
        {
            StartInfo = CreateStartInfo(request),
            EnableRaisingEvents = true,
        };
        try
        {
            if (!StartWithSpawnGate(process))
            {
                throw CreateToolFailure("process_start_failed", "internal_error", -32603);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw CreateToolFailure("process_start_failed", "internal_error", -32603);
        }

        await using var tree = ProcessTreeController.Attach(process);
        if (request.ResumeAfterContainment)
        {
            tree.Resume(process);
        }

        var stdout = process.StandardOutput.BaseStream;
        var stderr = process.StandardError.BaseStream;
        var budget = new SharedOutputBudget(request.OutputLimitBytes);
        var stdoutTask = ReadBoundedAsync(stdout, budget, cancellationToken: CancellationToken.None);
        var stderrTask = ReadBoundedAsync(stderr, budget, cancellationToken: CancellationToken.None);
        var stdinTask = WriteStandardInputAsync(
            process.StandardInput.BaseStream,
            request.StandardInput,
            CancellationToken.None);
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        var cancelTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var timeoutTask = Task.Delay(request.Timeout, CancellationToken.None);
        var completed = await Task.WhenAny(exitTask, cancelTask, timeoutTask, budget.Overflow).ConfigureAwait(false);
        var cancellationWon = ReferenceEquals(completed, cancelTask);
        var timeoutWon = ReferenceEquals(completed, timeoutTask);
        var overflowWon = ReferenceEquals(completed, budget.Overflow);
        var terminationReason = cancellationWon
            ? "cancelled"
            : timeoutWon
            ? "timeout"
            : overflowWon
            ? "output_limit"
            : "exit";

        if (!ReferenceEquals(completed, exitTask))
        {
            await tree.TerminateAsync(process).ConfigureAwait(false);
        }

        try
        {
            await exitTask.ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            await tree.TerminateAsync(process).ConfigureAwait(false);
        }

        await tree.TerminateRemainingDescendantsAsync(process).ConfigureAwait(false);
        await stdinTask.ConfigureAwait(false);
        var stdoutResult = await stdoutTask.ConfigureAwait(false);
        var stderrResult = await stderrTask.ConfigureAwait(false);
        if (budget.Exceeded)
        {
            terminationReason = "output_limit";
        }

        int? exitCode = terminationReason == "exit" && process.HasExited
            ? process.ExitCode
            : null;
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        return new ProcessExecutionResult(
            exitCode,
            null,
            terminationReason,
            Math.Max(0, checked((long)elapsed)),
            tree.Containment,
            new ProcessStreamResult(
                Encoding.UTF8.GetString(stdoutResult.Bytes),
                stdoutResult.Bytes.LongLength,
                stdoutResult.TotalBytes),
            new ProcessStreamResult(
                Encoding.UTF8.GetString(stderrResult.Bytes),
                stderrResult.Bytes.LongLength,
                stderrResult.TotalBytes));
    }

    /// <summary>
    /// 功能：在进程级全局 spawn gate 内同步执行一段不得跨 await 的启动准备操作。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <typeparam name="T">同步操作返回类型。</typeparam>
    /// <param name="operation">只允许完成 fd 准备、同步 Process.Start 与父 fd 关闭的短操作。</param>
    /// <returns>操作的同步返回值。</returns>
    /// <remarks>不变量：所有本实现 Process.Start 共用同一 gate；调用方不得在 operation 内阻塞等待子进程或执行 await。</remarks>
    /// <exception cref="ArgumentNullException">operation 为 null。</exception>
    internal static T RunWithSpawnGate<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (ProcessSpawnGate)
        {
            return operation();
        }
    }

    /// <summary>
    /// 功能：在全局 spawn gate 内同步启动单个已配置 Process。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="process">尚未启动且由调用方独占的 Process。</param>
    /// <returns>底层 Process.Start 的成功布尔值。</returns>
    /// <remarks>不变量：与 hard-sandbox 临时可继承 fd 窗口互斥；Monitor 重入允许持 gate 的 sandbox 调用本方法。</remarks>
    /// <exception cref="InvalidOperationException">Process 已启动或启动配置无效。</exception>
    internal static bool StartWithSpawnGate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return RunWithSpawnGate(process.Start);
    }

    /// <summary>
    /// 功能：验证 executor 请求的 cwd、stdin、timeout、输出上限及 argv 字符边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">待执行请求。</param>
    /// <exception cref="ArgumentException">请求含空/NUL/超界值或无效目录。</exception>
    private static void ValidateRequest(ProcessExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Executable) || request.Executable.Contains('\0', StringComparison.Ordinal) ||
            request.Executable.Length > 4096 || request.Arguments.Count > 512 ||
            request.Arguments.Any(static argument => argument is null || argument.Length > 4096 || argument.Contains('\0', StringComparison.Ordinal)) ||
            request.StandardInput is { } standardInput && standardInput.Length > MaxStandardInputBytes ||
            !Directory.Exists(request.WorkingDirectory) || request.Timeout <= TimeSpan.Zero ||
            request.Timeout > TimeSpan.FromMinutes(10) || request.OutputLimitBytes is < 1 or > 16_777_312)
        {
            throw new ArgumentException("process execution request is invalid", nameof(request));
        }
    }

    /// <summary>
    /// 功能：构造不经 shell、重定向独立 stdin、分离输出并只继承最小非 secret 环境的启动信息。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">已验证执行请求。</param>
    /// <returns>可直接交给 Process.Start 的安全配置。</returns>
    private static ProcessStartInfo CreateStartInfo(ProcessExecutionRequest request)
    {
        var useSetSid = OperatingSystem.IsLinux();
        var startInfo = new ProcessStartInfo
        {
            FileName = useSetSid ? ResolveSetSidExecutable() : request.Executable,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.CreateNewProcessGroup = true;
        }

        if (useSetSid)
        {
            startInfo.ArgumentList.Add(request.Executable);
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var inherited = startInfo.Environment;
        inherited.Clear();
        foreach (var name in AllowedEnvironmentNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                inherited[name] = value;
            }
        }

        return startInfo;
    }

    /// <summary>
    /// 功能：在 Linux 选择固定系统 setsid，以便子程序恢复执行前已经进入独立 session/进程组。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>已存在的 /usr/bin/setsid 或 /bin/setsid。</returns>
    /// <exception cref="ToolOperationException">系统缺少可靠新建进程组入口时失败关闭。</exception>
    private static string ResolveSetSidExecutable()
    {
        if (File.Exists("/usr/bin/setsid"))
        {
            return "/usr/bin/setsid";
        }

        if (File.Exists("/bin/setsid"))
        {
            return "/bin/setsid";
        }

        throw CreateToolFailure("process_tree_unavailable", "internal_error", -32603);
    }

    /// <summary>
    /// 功能：持续读取一个输出 pipe，只在共享总上限内保留字节并始终累计原始大小。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="stream">stdout 或 stderr pipe。</param>
    /// <param name="budget">两个流共享的硬上限。</param>
    /// <param name="cancellationToken">仅用于 daemon 强制关闭时中止读取。</param>
    /// <returns>当前流保留字节和观察总数。</returns>
    private static async Task<BoundedStreamResult> ReadBoundedAsync(
        Stream stream,
        SharedOutputBudget budget,
        CancellationToken cancellationToken)
    {
        using var captured = new MemoryStream();
        var buffer = new byte[8192];
        long total = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            total = checked(total + count);
            var retained = budget.Consume(buffer.AsSpan(0, count));
            if (retained > 0)
            {
                captured.Write(buffer, 0, retained);
            }
        }

        return new BoundedStreamResult(captured.ToArray(), total);
    }

    /// <summary>
    /// 功能：把可选 bounded stdin 原样写入独立 pipe，并始终关闭写端让子进程观察 EOF。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="stream">当前子进程的 stdin pipe。</param>
    /// <param name="standardInput">已验证不超过 1 MiB 的可选字节。</param>
    /// <param name="cancellationToken">daemon 强制关闭时可中止写入的信号。</param>
    /// <returns>完整写入或子进程提前关闭 pipe 后的完成任务。</returns>
    /// <remarks>不变量：stdin 不进入 argv、环境、日志或 executor 错误；pipe 关闭竞态不改变已经观察到的进程终态。</remarks>
    private static async Task WriteStandardInputAsync(
        Stream stream,
        ReadOnlyMemory<byte>? standardInput,
        CancellationToken cancellationToken)
    {
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                if (standardInput is { } bytes && !bytes.IsEmpty)
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // 子进程可在读取全部 stdin 前正常退出；关闭 pipe 是预期竞态且不得泄露系统诊断。
            }
        }
    }

    /// <summary>
    /// 功能：创建不包含 executable、argv、cwd 或 OS 异常文本的结构化执行失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">稳定错误类别。</param>
    /// <param name="terminationReason">portable 终止原因。</param>
    /// <param name="code">portable error code。</param>
    /// <returns>安全 ToolOperationException。</returns>
    private static ToolOperationException CreateToolFailure(string kind, string terminationReason, int code)
    {
        return new ToolOperationException(
            new PortableError(code, "process execution failed", false, new ErrorDetails(kind)),
            terminationReason);
    }

    /// <summary>
    /// 功能：保存单个 pipe 的有界字节和观察总数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="Bytes">上限内保留的字节。</param>
    /// <param name="TotalBytes">从 pipe 实际读取的字节数。</param>
    private sealed record BoundedStreamResult(byte[] Bytes, long TotalBytes);

    /// <summary>
    /// 功能：在 stdout/stderr 之间原子分配总输出预算并通知首次超限。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class SharedOutputBudget
    {
        private readonly object gate = new();
        private readonly int limit;
        private readonly TaskCompletionSource overflow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int retained;
        private long observed;

        /// <summary>
        /// 功能：创建正数硬上限的共享输出预算。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="limit">stdout/stderr 合计上限。</param>
        internal SharedOutputBudget(int limit)
        {
            this.limit = limit;
        }

        /// <summary>
        /// 功能：取得首次观察到超限时完成的任务。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        internal Task Overflow => overflow.Task;

        /// <summary>
        /// 功能：指出读取结束时总观察字节是否超过上限。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        internal bool Exceeded
        {
            get
            {
                lock (gate)
                {
                    return observed > limit;
                }
            }
        }

        /// <summary>
        /// 功能：记录一块输出并返回允许当前流保留的前缀长度。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="bytes">刚从某个 pipe 读取的字节。</param>
        /// <returns>不超过共享上限的保留字节数。</returns>
        internal int Consume(ReadOnlySpan<byte> bytes)
        {
            lock (gate)
            {
                observed = checked(observed + bytes.Length);
                var keep = Math.Min(bytes.Length, Math.Max(0, limit - retained));
                retained += keep;
                if (observed > limit)
                {
                    overflow.TrySetResult();
                }

                return keep;
            }
        }
    }
}

/// <summary>
/// 功能：封装 Unix 进程组或 Windows kill-on-close Job Object 的生命周期。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class ProcessTreeController : IAsyncDisposable
{
    private readonly int processGroupId;
    private readonly SafeJobHandle? job;
    private readonly LinuxDescendantGuard? linuxGuard;

    /// <summary>
    /// 功能：保存平台树容器；构造只由 Attach 完成。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="processGroupId">Unix 新进程组 ID。</param>
    /// <param name="job">Windows 尽力清理使用的 kill-on-close job；不构成 suspended-bind 声明。</param>
    /// <param name="linuxGuard">Linux PID/start-time 后代守卫。</param>
    /// <param name="containment">实际建立的 portable containment。</param>
    private ProcessTreeController(
        int processGroupId,
        SafeJobHandle? job,
        LinuxDescendantGuard? linuxGuard,
        string containment)
    {
        this.processGroupId = processGroupId;
        this.job = job;
        this.linuxGuard = linuxGuard;
        Containment = containment;
    }

    /// <summary>
    /// 功能：取得本次已经实际建立的 portable containment 等级。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal string Containment { get; }

    /// <summary>
    /// 功能：把刚启动进程绑定到其平台整树终止容器。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="process">已启动且仍由当前 executor 独占管理的直接子进程。</param>
    /// <returns>拥有 Job/进程组生命周期的控制器。</returns>
    /// <exception cref="ToolOperationException">Windows Job Object 配置或 assignment 失败。</exception>
    internal static ProcessTreeController Attach(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            var job = WindowsJobApi.CreateKillOnCloseJob(process);
            return new ProcessTreeController(0, job, null, "direct_process");
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var guard = LinuxDescendantGuard.Start(process.Id);
                var groupEstablished = UnixProcessGroupApi.EnsureOwnGroup(process.Id);
                if (!groupEstablished && !process.HasExited)
                {
                    guard.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    process.Kill(entireProcessTree: true);
                    throw new ToolOperationException(
                        new PortableError(
                            -32603,
                            "process tree containment failed",
                            false,
                            new ErrorDetails("process_tree_unavailable")),
                        "internal_error");
                }

                return new ProcessTreeController(
                    groupEstablished ? process.Id : 0,
                    null,
                    guard,
                    "unix_process_group");
            }
            catch (ToolOperationException)
            {
                UnixProcessGroupApi.Signal(process.Id, 9);
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // containment 建立失败后尽力清理，不回显宿主诊断。
                }

                throw;
            }
        }

        if (!UnixProcessGroupApi.EnsureOwnGroup(process.Id))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // 未能建立进程组后尽力清理，不回显系统诊断。
            }

            throw new ToolOperationException(
                new PortableError(-32603, "process tree containment failed", false, new ErrorDetails("process_tree_unavailable")),
                "internal_error");
        }

        return new ProcessTreeController(process.Id, null, null, "unix_process_group");
    }

    /// <summary>
    /// 功能：按平台终止容器中的直接子进程及全部后代，并以 Process.Kill 整树兜底。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="process">直接子进程。</param>
    /// <returns>终止信号发送完成。</returns>
    internal async Task TerminateAsync(Process process)
    {
        if (linuxGuard is not null)
        {
            await linuxGuard.TerminateAsync(processGroupId, signalProcessGroup: true).ConfigureAwait(false);
        }
        else if (OperatingSystem.IsWindows())
        {
            if (job is not null)
            {
                WindowsJobApi.Terminate(job);
            }
        }
        else if (processGroupId > 0)
        {
            UnixProcessGroupApi.Signal(processGroupId, 9);
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // 平台容器已先执行；兜底失败不回显 OS 诊断。
        }
    }

    /// <summary>
    /// 功能：在 containment 与后代监视均建立后恢复由固定 POSIX shell 前缀自停的直接进程。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="process">当前执行调用拥有且仍处于 SIGSTOP 的直接子进程。</param>
    /// <remarks>不变量：仅向本次已验证进程组或精确直接 PID 发送 SIGCONT；不把普通进程伪装为 suspended Windows Job。</remarks>
    internal void Resume(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        if (processGroupId > 0)
        {
            UnixProcessGroupApi.Signal(processGroupId, 18);
        }
        else if (!process.HasExited)
        {
            UnixProcessGroupApi.SignalProcess(process.Id, 18);
        }
    }

    /// <summary>
    /// 功能：直接子进程退出后清理仍持有 pipe 或继续运行的后台后代。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="process">直接子进程。</param>
    /// <returns>后代终止请求完成。</returns>
    internal async Task TerminateRemainingDescendantsAsync(Process process)
    {
        if (linuxGuard is not null)
        {
            await linuxGuard.TerminateAsync(
                processGroupId,
                signalProcessGroup: !process.HasExited).ConfigureAwait(false);
            return;
        }

        await TerminateAsync(process).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：关闭 Windows Job handle；kill-on-close 确保 daemon 异常路径不遗留后代。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>已释放平台资源的完成操作。</returns>
    public async ValueTask DisposeAsync()
    {
        if (linuxGuard is not null)
        {
            await linuxGuard.DisposeAsync().ConfigureAwait(false);
        }

        job?.Dispose();
    }
}

/// <summary>
/// 功能：调用 libc kill 向本执行器创建的 Unix 进程组发送信号。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal static partial class UnixProcessGroupApi
{
    /// <summary>
    /// 功能：确认直接子进程已成为自己的进程组长，并在非 Linux fallback 上尝试 setpgid。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="processId">直接子进程 PID。</param>
    /// <returns>getpgid 等于 PID 时为 true。</returns>
    internal static bool EnsureOwnGroup(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (GetProcessGroup(processId) == processId)
            {
                return true;
            }

            if (!OperatingSystem.IsLinux())
            {
                break;
            }

            Thread.Sleep(1);
        }

        if (OperatingSystem.IsLinux())
        {
            return false;
        }

        return SetProcessGroup(processId, processId) == 0 && GetProcessGroup(processId) == processId;
    }

    /// <summary>
    /// 功能：向负 PID 表示的进程组发送 SIGTERM/SIGKILL，忽略已退出组。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="processGroupId">由本执行器创建的正数组 ID。</param>
    /// <param name="signal">POSIX 信号编号。</param>
    internal static void Signal(int processGroupId, int signal)
    {
        if (processGroupId > 0)
        {
            _ = Kill(-processGroupId, signal);
        }
    }

    /// <summary>
    /// 功能：向 PID/start-time 已由调用方复验的单个 Unix 进程发送信号。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="processId">当前仍匹配已记录身份的正 PID。</param>
    /// <param name="signal">POSIX 信号编号。</param>
    internal static void SignalProcess(int processId, int signal)
    {
        if (processId > 0)
        {
            _ = Kill(processId, signal);
        }
    }

    /// <summary>
    /// 功能：导入 libc kill 系统调用；仅由 Signal 在非 Windows 平台调用。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="pid">负进程组 ID。</param>
    /// <param name="signal">信号编号。</param>
    /// <returns>零成功，负数表示组已退出或系统拒绝。</returns>
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);

    /// <summary>
    /// 功能：导入 getpgid 检查子进程当前进程组。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="pid">子进程 PID。</param>
    /// <returns>进程组 ID，失败为负数。</returns>
    [LibraryImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    private static partial int GetProcessGroup(int pid);

    /// <summary>
    /// 功能：导入 setpgid 作为无 setsid 平台的失败关闭 fallback。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="pid">子进程 PID。</param>
    /// <param name="processGroupId">目标进程组 ID。</param>
    /// <returns>零成功，负数失败。</returns>
    [LibraryImport("libc", EntryPoint = "setpgid", SetLastError = true)]
    private static partial int SetProcessGroup(int pid, int processGroupId);
}

/// <summary>
/// 功能：用 Linux PID 与 /proc start-time 身份持续追踪进程组外的全部后代。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class LinuxDescendantGuard : IAsyncDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(5);
    private readonly object gate = new();
    private readonly Dictionary<int, ulong> tracked;
    private readonly CancellationTokenSource monitorCancellation = new();
    private readonly SemaphoreSlim cleanupGate = new(1, 1);
    private Task? monitor;
    private bool terminated;
    private bool disposed;

    /// <summary>
    /// 功能：创建已绑定根身份但尚未启动后台监视的守卫。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="root">从同一 /proc 快照读取的直接子进程身份。</param>
    private LinuxDescendantGuard(LinuxProcessIdentity root)
    {
        tracked = new Dictionary<int, ulong>
        {
            [root.ProcessId] = root.StartTime,
        };
    }

    /// <summary>
    /// 功能：为刚启动的 Linux 子进程建立 PID/start-time 绑定并启动 5ms 后代监视。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="processId">直接子进程正 PID。</param>
    /// <returns>可清理已采样 setsid 后代、但不构成快速 double-fork containment 的活跃守卫。</returns>
    /// <remarks>不变量：只有全局 /proc 快照和根身份同时可读时才构造成功；发信号前始终重新验证 start-time。</remarks>
    /// <exception cref="ToolOperationException">procfs 不可读或根身份已经消失。</exception>
    internal static LinuxDescendantGuard Start(int processId)
    {
        if (processId <= 0 || !TryReadProcessIdentity(processId, out var root))
        {
            throw CreateContainmentFailure();
        }

        var identities = ScanProcessIdentities();
        if (identities is null)
        {
            throw CreateContainmentFailure();
        }

        var guard = new LinuxDescendantGuard(root);
        guard.Capture(identities);
        guard.monitor = Task.Run(guard.MonitorAsync);
        return guard;
    }

    /// <summary>
    /// 功能：冻结并终止仍匹配 start-time 的已采样后代，尽力覆盖离开进程组的 setsid 子进程。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="processGroupId">本次直接子进程创建的进程组 ID。</param>
    /// <param name="signalProcessGroup">根尚未退出时是否同时安全冻结并终止原进程组。</param>
    /// <returns>后台监视已回收且已知身份均完成强制终止请求。</returns>
    /// <remarks>不变量：先 SIGSTOP 封闭继续派生窗口，再刷新身份并 SIGKILL；从不向 start-time 已变化的复用 PID 发信号。</remarks>
    internal async Task TerminateAsync(int processGroupId, bool signalProcessGroup)
    {
        await cleanupGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (terminated)
            {
                return;
            }

            await StopMonitorAsync().ConfigureAwait(false);
            if (signalProcessGroup)
            {
                UnixProcessGroupApi.Signal(processGroupId, 19);
            }

            for (var round = 0; round < 3; round++)
            {
                Capture(ScanProcessIdentities());
                SignalTracked(19);
                await Task.Delay(TimeSpan.FromMilliseconds(2)).ConfigureAwait(false);
            }

            if (signalProcessGroup)
            {
                UnixProcessGroupApi.Signal(processGroupId, 9);
            }

            SignalTracked(9);
            for (var round = 0; round < 2; round++)
            {
                await Task.Delay(ScanInterval).ConfigureAwait(false);
                Capture(ScanProcessIdentities());
                SignalTracked(9);
            }

            terminated = true;
        }
        finally
        {
            cleanupGate.Release();
        }
    }

    /// <summary>
    /// 功能：周期扫描 /proc，把从任一已知精确身份派生的进程加入守卫集合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>收到停止信号后的正常完成任务。</returns>
    private async Task MonitorAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(ScanInterval, monitorCancellation.Token).ConfigureAwait(false);
                Capture(ScanProcessIdentities());
            }
        }
        catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
        {
            // 当前执行结束时的预期停止路径。
        }
    }

    /// <summary>
    /// 功能：停止后台扫描并等待监视任务退出，保证不会继续观察后续无关 PID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>监视任务已完全退出。</returns>
    private async Task StopMonitorAsync()
    {
        monitorCancellation.Cancel();
        if (monitor is not null)
        {
            await monitor.ConfigureAwait(false);
            monitor = null;
        }
    }

    /// <summary>
    /// 功能：对一个 /proc 快照做 PPID 与父 start-time 闭包，扩展可证明的后代身份集合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="identities">同一时刻尽力读取的 PID 身份映射；不可用时保持既有集合。</param>
    /// <remarks>不变量：同 PID 的 start-time 改变会先移除旧身份；只有父身份仍在快照中精确匹配时才接受新后代。</remarks>
    private void Capture(Dictionary<int, LinuxProcessIdentity>? identities)
    {
        if (identities is null)
        {
            return;
        }

        lock (gate)
        {
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var pair in identities)
                {
                    var identity = pair.Value;
                    if (tracked.TryGetValue(identity.ProcessId, out var knownStart))
                    {
                        if (knownStart != identity.StartTime)
                        {
                            tracked.Remove(identity.ProcessId);
                        }

                        continue;
                    }

                    if (tracked.TryGetValue(identity.ParentProcessId, out var parentStart) &&
                        identities.TryGetValue(identity.ParentProcessId, out var parent) &&
                        parent.StartTime == parentStart)
                    {
                        tracked[identity.ProcessId] = identity.StartTime;
                        changed = true;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 功能：向所有当前仍匹配已记录 start-time 的进程发送一个 Unix 信号。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="signal">SIGSTOP 或 SIGKILL 的数值。</param>
    /// <remarks>不变量：每次发送前重新读取 /proc 身份，PID 复用或读取失败均跳过。</remarks>
    private void SignalTracked(int signal)
    {
        KeyValuePair<int, ulong>[] snapshot;
        lock (gate)
        {
            snapshot = tracked.ToArray();
        }

        foreach (var pair in snapshot)
        {
            if (TryReadProcessIdentity(pair.Key, out var current) && current.StartTime == pair.Value)
            {
                UnixProcessGroupApi.SignalProcess(pair.Key, signal);
            }
        }
    }

    /// <summary>
    /// 功能：读取单一尽力 /proc 快照中的 PID、PPID 和启动时钟 tick。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>可读身份映射；procfs 根不可枚举时返回 null。</returns>
    /// <remarks>不变量：只读取 /proc 下纯数字目录的 stat，不跟随工作区路径或接收外部路径参数。</remarks>
    private static Dictionary<int, LinuxProcessIdentity>? ScanProcessIdentities()
    {
        try
        {
            var identities = new Dictionary<int, LinuxProcessIdentity>();
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                var name = Path.GetFileName(directory);
                if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) &&
                    processId > 0 &&
                    TryReadProcessIdentity(processId, out var identity))
                {
                    identities[processId] = identity;
                }
            }

            return identities;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 功能：严格解析 /proc/&lt;pid&gt;/stat 的 PPID 与 start-time，兼容 comm 中的空格和括号。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="processId">待读取的正 PID。</param>
    /// <param name="identity">成功时返回与 PID 绑定的身份。</param>
    /// <returns>条目存在且字段完整、数值合法时为 true。</returns>
    /// <remarks>不变量：调用方在发信号前必须再次调用并比较 start-time；失败不暴露 stat 正文。</remarks>
    private static bool TryReadProcessIdentity(int processId, out LinuxProcessIdentity identity)
    {
        identity = default;
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            var text = File.ReadAllText(
                Path.Combine("/proc", processId.ToString(CultureInfo.InvariantCulture), "stat"),
                Encoding.UTF8);
            var closing = text.LastIndexOf(") ", StringComparison.Ordinal);
            if (closing < 0)
            {
                return false;
            }

            var fields = text[(closing + 2)..].Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length <= 19 ||
                !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentProcessId) ||
                !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var startTime))
            {
                return false;
            }

            identity = new LinuxProcessIdentity(processId, parentProcessId, startTime);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 功能：创建不包含 PID、路径或宿主诊断的 containment 建立失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>稳定 process_tree_unavailable 工具异常。</returns>
    private static ToolOperationException CreateContainmentFailure()
    {
        return new ToolOperationException(
            new PortableError(
                -32603,
                "process tree containment failed",
                false,
                new ErrorDetails("process_tree_unavailable")),
            "internal_error");
    }

    /// <summary>
    /// 功能：在异常路径也强制终止已知身份、停止监视并释放同步资源。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>守卫不再持有后台任务或取消句柄。</returns>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await TerminateAsync(0, signalProcessGroup: false).ConfigureAwait(false);
        disposed = true;
        monitorCancellation.Dispose();
        cleanupGate.Dispose();
    }
}

/// <summary>
/// 功能：用 PID、父 PID 与 Linux start-time 唯一标识一个进程实例。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ProcessId">进程 PID。</param>
/// <param name="ParentProcessId">同一快照中的父 PID。</param>
/// <param name="StartTime">/proc stat 第 22 字段启动 tick。</param>
internal readonly record struct LinuxProcessIdentity(
    int ProcessId,
    int ParentProcessId,
    ulong StartTime);

/// <summary>
/// 功能：创建并控制 Windows kill-on-close Job Object。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsJobApi
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    /// <summary>
    /// 功能：创建 kill-on-close Job 并立即绑定刚启动子进程。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="process">已启动且尚由执行器独占管理的子进程。</param>
    /// <returns>拥有 Job 生命周期的安全句柄。</returns>
    /// <exception cref="ToolOperationException">Job 创建、策略配置或 assignment 失败。</exception>
    internal static SafeJobHandle CreateKillOnCloseJob(Process process)
    {
        var rawJob = CreateJobObject(nint.Zero, null);
        var job = new SafeJobHandle(rawJob);
        if (job.IsInvalid)
        {
            job.Dispose();
            throw CreateJobFailure();
        }

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        var length = (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref information, length) ||
            !AssignProcessToJobObject(job, process.SafeHandle))
        {
            job.Dispose();
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // Job 配置失败后仍尽力终止，不回显 OS 信息。
            }

            throw CreateJobFailure();
        }

        return job;
    }

    /// <summary>
    /// 功能：终止 Job 中全部进程；失败由 Process.Kill 兜底。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="job">有效 Job handle。</param>
    internal static void Terminate(SafeJobHandle job)
    {
        _ = TerminateJobObject(job, 1);
    }

    /// <summary>
    /// 功能：创建不泄露 Windows 错误文本的 Job Object 失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>安全工具异常。</returns>
    private static ToolOperationException CreateJobFailure()
    {
        return new ToolOperationException(
            new PortableError(-32603, "process tree containment failed", false, new ErrorDetails("process_tree_unavailable")),
            "internal_error");
    }

    /// <summary>
    /// 功能：导入 CreateJobObjectW 创建匿名 Job。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObject(nint jobAttributes, string? name);

    /// <summary>
    /// 功能：导入 SetInformationJobObject 配置 kill-on-close flag。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    /// <summary>
    /// 功能：导入 AssignProcessToJobObject 将直接子进程纳入 Job。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    /// <summary>
    /// 功能：导入 TerminateJobObject 终止 Job 内整棵进程树。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize;
        internal nuint MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }
}

/// <summary>
/// 功能：拥有 Windows Job Object 原生 handle，并在释放时触发 kill-on-close。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>
    /// 功能：取得 CreateJobObject 返回值的所有权并包装为 Job 安全句柄。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">CreateJobObject 返回的原生 handle。</param>
    internal SafeJobHandle(nint value)
        : base(ownsHandle: true)
    {
        SetHandle(value);
    }

    /// <summary>
    /// 功能：关闭 Windows Job handle；kill-on-close 策略同时清理全部成员进程。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>CloseHandle 成功时为 true。</returns>
    protected override bool ReleaseHandle()
    {
        return CloseHandle(handle);
    }

    /// <summary>
    /// 功能：导入 CloseHandle 释放 Job 内核对象。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint value);
}
