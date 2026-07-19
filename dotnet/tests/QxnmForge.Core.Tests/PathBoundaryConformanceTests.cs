using System.Diagnostics;
using System.Text.Json;
using QxnmForge.Executor;
using QxnmForge.Tools;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 .NET Linux handle path boundary、双门配置和一次性 race barrier。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
[Collection(ProviderEnvironmentGroup.Name)]
public sealed class PathBoundaryConformanceTests
{
    private static readonly string[] EnvironmentNames =
    [
        PathBoundaryConformanceBarrier.ConfigurationEnvironment,
    ];

    /// <summary>
    /// 功能：确认 Prepare 冻结 canonical relative path 与 toolCallId，且普通句柄读写保持现有结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task PreparedFileCallsRetainRelativePathAndExecute()
    {
        using var environment = new EnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        Directory.CreateDirectory(Path.Combine(workspace, "nested"));
        File.WriteAllText(Path.Combine(workspace, "nested", "target.txt"), "before\n");
        using var registry = new ToolRegistry(workspace);

        var read = registry.Prepare(
            "file.read",
            JsonSerializer.SerializeToElement(new { path = "nested/target.txt" }),
            "relative-read-1");
        var readResult = await registry.ExecuteAsync(read);
        Assert.Equal("nested/target.txt", read.WorkspaceRelativePath);
        Assert.Equal("relative-read-1", read.ToolCallId);
        Assert.Equal("before\n", readResult.Content.Single().Text);

        var write = registry.Prepare(
            "file.write",
            JsonSerializer.SerializeToElement(new { path = "nested/target.txt", content = "after\n" }),
            "relative-write-1");
        var writeResult = await registry.ExecuteAsync(write);
        Assert.False(writeResult.IsError);
        Assert.Equal("after\n", File.ReadAllText(Path.Combine(workspace, "nested", "target.txt")));
    }

    /// <summary>
    /// 功能：确认配置 env presence 缺任一 raw gate 时 initialize 固定失败且不等待配置源。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="cliGate">模拟 argv 是否精确包含 --conformance。</param>
    /// <param name="environmentGate">模拟 QXNM_FORGE_CONFORMANCE 是否精确为 1。</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ConfigurationPresenceRequiresBothRawGates(bool cliGate, bool environmentGate)
    {
        using var environment = new EnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        environment.Set(
            PathBoundaryConformanceBarrier.ConfigurationEnvironment,
            Path.Combine(temporary.Path, "source-must-not-be-opened"));
        var timer = Stopwatch.StartNew();

        using var registry = new ToolRegistry(workspace, cliGate, environmentGate);
        var error = await registry.InitializeHardSandboxAsync(CancellationToken.None);

        Assert.NotNull(error);
        Assert.Equal(-32603, error.Code);
        Assert.False(error.Retryable);
        Assert.Equal("conformance_configuration_invalid", error.Details.Kind);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// 功能：确认 workspace 根在 before-parent barrier 后被 rename/rebind 时，读取仍绑定 startup root fd。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task LinuxRootBarrierReadsFromPinnedDirectory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var environment = new EnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(temporary.Path, "outside")).FullName;
        File.WriteAllText(Path.Combine(workspace, "target.txt"), "inside before\n");
        File.WriteAllText(Path.Combine(outside, "target.txt"), "outside canary\n");
        var control = Directory.CreateDirectory(Path.Combine(temporary.Path, "root-control")).FullName;
        File.SetUnixFileMode(
            control,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        const string caseId = "dotnet-root-read";
        const string toolCallId = "dotnet-root-read-1";
        const string checkpoint = "before_parent_walk";
        var configuration = Path.Combine(temporary.Path, "root-config.json");
        File.WriteAllText(
            configuration,
            JsonSerializer.Serialize(new
            {
                schemaVersion = "0.1",
                caseId,
                toolCallId,
                checkpoint,
                controlDirectory = control,
                timeoutMs = 5000,
            }));
        environment.Set(PathBoundaryConformanceBarrier.ConfigurationEnvironment, configuration);
        using var registry = new ToolRegistry(workspace, cliConformance: true, environmentConformance: true);
        Assert.Null(await registry.InitializeHardSandboxAsync(CancellationToken.None));
        var read = registry.Prepare(
            "file.read",
            JsonSerializer.SerializeToElement(new { path = "target.txt" }),
            toolCallId);
        var execution = registry.ExecuteAsync(read);
        await WaitForFileAsync(Path.Combine(control, "ready.json"), TimeSpan.FromSeconds(2));
        var pinnedWorkspace = Path.Combine(temporary.Path, "workspace-pinned");
        Directory.Move(workspace, pinnedWorkspace);
        Directory.CreateSymbolicLink(workspace, outside);
        PublishRelease(control, caseId, toolCallId, checkpoint);

        var readResult = await execution;

        Assert.Equal("inside before\n", readResult.Content.Single().Text);
        Assert.Equal("inside before\n", File.ReadAllText(Path.Combine(pinnedWorkspace, "target.txt")));
        Assert.Equal("outside canary\n", File.ReadAllText(Path.Combine(outside, "target.txt")));
    }

    /// <summary>
    /// 功能：在真实 leaf-open barrier 后交换名称，确认 file.read 从已打开 fd 返回原内容。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task LinuxLeafBarrierReadsOpenedFileDescriptor()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var environment = new EnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var leafDirectory = Directory.CreateDirectory(Path.Combine(workspace, "leaf")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(temporary.Path, "outside")).FullName;
        var target = Path.Combine(leafDirectory, "target.txt");
        var moved = Path.Combine(leafDirectory, "target.moved");
        var outsideTarget = Path.Combine(outside, "target.txt");
        File.WriteAllText(target, "inside leaf\n");
        File.WriteAllText(outsideTarget, "outside canary\n");
        var control = Directory.CreateDirectory(Path.Combine(temporary.Path, "control")).FullName;
        File.SetUnixFileMode(
            control,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        const string caseId = "dotnet-leaf-read";
        const string toolCallId = "dotnet-leaf-read-1";
        const string checkpoint = "after_leaf_open_before_read";
        var configuration = Path.Combine(control, "config.json");
        File.WriteAllText(
            configuration,
            JsonSerializer.Serialize(new
            {
                schemaVersion = "0.1",
                caseId,
                toolCallId,
                checkpoint,
                controlDirectory = control,
                timeoutMs = 5000,
            }));
        environment.Set(PathBoundaryConformanceBarrier.ConfigurationEnvironment, configuration);
        using var registry = new ToolRegistry(workspace, cliConformance: true, environmentConformance: true);
        Assert.Null(await registry.InitializeHardSandboxAsync(CancellationToken.None));
        var prepared = registry.Prepare(
            "file.read",
            JsonSerializer.SerializeToElement(new { path = "leaf/target.txt" }),
            toolCallId);

        var execution = registry.ExecuteAsync(prepared);
        await WaitForFileAsync(Path.Combine(control, "ready.json"), TimeSpan.FromSeconds(2));
        File.Move(target, moved);
        File.CreateSymbolicLink(target, outsideTarget);
        PublishRelease(control, caseId, toolCallId, checkpoint);
        var result = await execution;

        Assert.Equal("inside leaf\n", result.Content.Single().Text);
        Assert.Equal("outside canary\n", File.ReadAllText(outsideTarget));
    }

    /// <summary>
    /// 功能：在 temp-fsync barrier 后交换 leaf，确认 renameat 只替换 pinned parent 下的原名称。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task LinuxLeafWriteBarrierReplacesNameInPinnedParent()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var environment = new EnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var leafDirectory = Directory.CreateDirectory(Path.Combine(workspace, "leaf")).FullName;
        var target = Path.Combine(leafDirectory, "target.txt");
        var moved = Path.Combine(leafDirectory, "target.moved");
        var outsideTarget = Path.Combine(temporary.Path, "outside.txt");
        File.WriteAllText(target, "old inside\n");
        File.WriteAllText(outsideTarget, "outside canary\n");
        var control = Directory.CreateDirectory(Path.Combine(temporary.Path, "write-control")).FullName;
        File.SetUnixFileMode(
            control,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        const string caseId = "dotnet-leaf-write";
        const string toolCallId = "dotnet-leaf-write-1";
        const string checkpoint = "after_temp_fsync_before_rename";
        var configuration = Path.Combine(temporary.Path, "write-config.json");
        File.WriteAllText(
            configuration,
            JsonSerializer.Serialize(new
            {
                schemaVersion = "0.1",
                caseId,
                toolCallId,
                checkpoint,
                controlDirectory = control,
                timeoutMs = 5000,
            }));
        environment.Set(PathBoundaryConformanceBarrier.ConfigurationEnvironment, configuration);
        using var registry = new ToolRegistry(workspace, cliConformance: true, environmentConformance: true);
        Assert.Null(await registry.InitializeHardSandboxAsync(CancellationToken.None));
        var prepared = registry.Prepare(
            "file.write",
            JsonSerializer.SerializeToElement(new { path = "leaf/target.txt", content = "new inside\n" }),
            toolCallId);

        var execution = registry.ExecuteAsync(prepared);
        await WaitForFileAsync(Path.Combine(control, "ready.json"), TimeSpan.FromSeconds(2));
        File.Move(target, moved);
        File.CreateSymbolicLink(target, outsideTarget);
        PublishRelease(control, caseId, toolCallId, checkpoint);
        var result = await execution;

        Assert.False(result.IsError);
        Assert.Null(new FileInfo(target).LinkTarget);
        Assert.Equal("new inside\n", File.ReadAllText(target));
        Assert.Equal("old inside\n", File.ReadAllText(moved));
        Assert.Equal("outside canary\n", File.ReadAllText(outsideTarget));
    }

    /// <summary>
    /// 功能：有界等待 barrier ready 文件出现，避免测试依赖概率 sleep。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">runner ready.json 路径。</param>
    /// <param name="timeout">测试最大等待时间。</param>
    /// <returns>文件出现后的 Task。</returns>
    /// <exception cref="TimeoutException">期限内未观察到 ready。</exception>
    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (File.Exists(path))
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("path boundary ready file was not published");
    }

    /// <summary>
    /// 功能：通过同目录临时文件原子发布严格 release.json 测试文档。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="control">独占 0700 测试控制目录。</param>
    /// <param name="caseId">与配置相同的 case ID。</param>
    /// <param name="toolCallId">与配置相同的工具调用 ID。</param>
    /// <param name="checkpoint">与配置相同的 checkpoint。</param>
    private static void PublishRelease(
        string control,
        string caseId,
        string toolCallId,
        string checkpoint)
    {
        var temporary = Path.Combine(control, "release.tmp");
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(new
            {
                schemaVersion = "0.1",
                caseId,
                toolCallId,
                checkpoint,
                state = "release",
            }));
        File.Move(temporary, Path.Combine(control, "release.json"));
    }

    /// <summary>
    /// 功能：保存并恢复 path boundary 测试修改的进程环境变量。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> original;

        /// <summary>
        /// 功能：捕获固定环境名称的原始值供释放时恢复。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="names">本测试独占修改的环境名称。</param>
        internal EnvironmentScope(IEnumerable<string> names)
        {
            original = names.ToDictionary(
                static name => name,
                static name => Environment.GetEnvironmentVariable(name),
                StringComparer.Ordinal);
        }

        /// <summary>
        /// 功能：清除 scope 捕获的全部 path boundary 环境值。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        internal void ClearAll()
        {
            foreach (var name in original.Keys)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        /// <summary>
        /// 功能：设置属于本 scope 的单个环境变量。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="name">构造时捕获的固定名称。</param>
        /// <param name="value">测试配置路径或 null。</param>
        /// <exception cref="ArgumentException">名称不属于 scope。</exception>
        internal void Set(string name, string? value)
        {
            if (!original.ContainsKey(name))
            {
                throw new ArgumentException("environment name is outside test scope", nameof(name));
            }

            Environment.SetEnvironmentVariable(name, value);
        }

        /// <summary>
        /// 功能：恢复构造时捕获的全部原始环境值。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        public void Dispose()
        {
            foreach (var item in original)
            {
                Environment.SetEnvironmentVariable(item.Key, item.Value);
            }
        }
    }
}
