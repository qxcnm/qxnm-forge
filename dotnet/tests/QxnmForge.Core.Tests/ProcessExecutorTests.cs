using System.Text;
using System.Text.Json;
using QxnmForge.Domain;
using QxnmForge.Executor;
using QxnmForge.Serialization;
using QxnmForge.Tools;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 .NET 原生 executor 的结构化终态、stdin 与 Linux 强后代基础。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ProcessExecutorTests
{
    /// <summary>
    /// 功能：确认 execution 必需的 nullable exitCode 与 signal 在 wire 上显式编码为 null。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void PortableExecutionWritesRequiredNullFields()
    {
        var empty = new PortableExecutionCapture("utf-8-replacement", string.Empty, 0, 0, 0, false);
        var execution = new PortableExecutionResult(
            null,
            null,
            "timeout",
            1,
            "unix_process_group",
            empty,
            empty);
        var result = new PortableToolResult(
            [new TextContent("timeout")],
            true,
            "timeout",
            Execution: execution);

        var json = JsonSerializer.SerializeToElement(result, JsonDefaults.Options);

        Assert.Equal(JsonValueKind.Null, json.GetProperty("execution").GetProperty("exitCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("execution").GetProperty("signal").ValueKind);
    }

    /// <summary>
    /// 功能：确认 bounded stdin 作为独立 pipe 原样传递且双流字节账精确。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncPassesBoundedStandardInput()
    {
        if (!ProcessExecutor.IsConformantPlatformAvailable())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var input = Encoding.UTF8.GetBytes("输入保持原样");

        var result = await ProcessExecutor.ExecuteAsync(new ProcessExecutionRequest(
            "/bin/sh",
            ["-c", "cat"],
            temporary.Path,
            input,
            false,
            TimeSpan.FromSeconds(3),
            4096));

        Assert.Equal("exit", result.TerminationReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("输入保持原样", result.Stdout.Text);
        Assert.Equal(input.LongLength, result.Stdout.CapturedBytes);
        Assert.Equal(input.LongLength, result.Stdout.TotalBytes);
        Assert.Equal("unix_process_group", result.Containment);
    }

    /// <summary>
    /// 功能：确认 timeout 完成整树清理后返回 structured 终态而不是抛出异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncReturnsTimeoutResult()
    {
        if (!ProcessExecutor.IsConformantPlatformAvailable())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();

        var result = await ProcessExecutor.ExecuteAsync(new ProcessExecutionRequest(
            "/bin/sh",
            ["-c", "sleep 5"],
            temporary.Path,
            null,
            false,
            TimeSpan.FromMilliseconds(100),
            4096));

        Assert.Equal("timeout", result.TerminationReason);
        Assert.Null(result.ExitCode);
        Assert.Equal("unix_process_group", result.Containment);
    }

    /// <summary>
    /// 功能：证明所有 Process.Start 与 hard-sandbox 可继承 fd 临界区由同一个全局 gate 串行化。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task SpawnGateExcludesConcurrentDescriptorWindows()
    {
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondAttempted = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        var first = Task.Run(() => ProcessExecutor.RunWithSpawnGate(() =>
        {
            firstEntered.Set();
            Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(2)));
            return 1;
        }));
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(2)));
        var second = Task.Run(() =>
        {
            secondAttempted.Set();
            return ProcessExecutor.RunWithSpawnGate(() =>
            {
                secondEntered.Set();
                return 2;
            });
        });
        Assert.True(secondAttempted.Wait(TimeSpan.FromSeconds(2)));

        try
        {
            Assert.False(secondEntered.Wait(TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            releaseFirst.Set();
        }

        var results = await Task.WhenAll(first, second);
        Assert.Equal([1, 2], results);
        Assert.True(secondEntered.IsSet);
    }
}
