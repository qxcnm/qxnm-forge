using QxnmForge.Executor;
using QxnmForge.Tools;
using System.Text.Json;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 .NET Bubblewrap backend 的显式配置、启动自检与失败关闭身份门禁。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
[Collection(ProviderEnvironmentGroup.Name)]
public sealed class HardSandboxBackendTests
{
    private static readonly string[] EnvironmentNames =
    [
        HardSandboxBackend.ProfileEnvironment,
        HardSandboxBackend.ConformanceBackendEnvironment,
    ];
    private static readonly string[] SandboxWriteArguments =
    [
        "-c",
        "import os;open('written.txt','w').write('ok');print(os.getcwd(),end='')",
    ];

    /// <summary>
    /// 功能：在 Linux 本机真实执行固定 backend 自检并只在成功后产生完整 descriptor。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task FixedBackendSelfTestProducesCapability()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(HardSandboxBackend.ProductionBackend))
        {
            return;
        }

        using var environment = new EnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        environment.Set(HardSandboxBackend.ProfileEnvironment, HardSandboxBackend.ProfileName);
        using var temporary = new TemporaryDirectory();

        var backend = HardSandboxBackend.CreateFromEnvironment(
            temporary.Path,
            cliConformance: false,
            environmentConformance: false,
            out var error);

        Assert.Null(error);
        Assert.NotNull(backend);
        Assert.Null(backend.Capability);
        await backend.EnsureSelfTestedAsync(CancellationToken.None);
        Assert.Equal(HardSandboxBackend.ProfileName, backend.Capability?.Profile);
        Assert.True(backend.Capability?.SelfTested);
    }

    /// <summary>
    /// 功能：验证 daemon 使用的 ToolRegistry 构造路径能完成同一 startup self-test。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ToolRegistryInitializesConfiguredBackend()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(HardSandboxBackend.ProductionBackend))
        {
            return;
        }

        using var environment = new EnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        environment.Set(HardSandboxBackend.ProfileEnvironment, HardSandboxBackend.ProfileName);
        using var temporary = new TemporaryDirectory();
        using var registry = new ToolRegistry(
            temporary.Path,
            cliConformance: true,
            environmentConformance: false);

        var error = await registry.InitializeHardSandboxAsync(CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(HardSandboxBackend.ProfileName, registry.HardSandboxCapability?.Profile);
    }

    /// <summary>
    /// 功能：验证 process.exec 的 sandbox 参数进入审批/hash，并在真实 namespace 中返回 os_isolation。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task SandboxedProcessBindsApprovalAndReturnsOsIsolation()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(HardSandboxBackend.ProductionBackend))
        {
            return;
        }

        using var environment = new EnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        environment.Set(HardSandboxBackend.ProfileEnvironment, HardSandboxBackend.ProfileName);
        using var temporary = new TemporaryDirectory();
        using var registry = new ToolRegistry(temporary.Path, false, false);
        Assert.Null(await registry.InitializeHardSandboxAsync(CancellationToken.None));
        var writableArguments = JsonSerializer.SerializeToElement(new
        {
            executable = "/usr/bin/python3",
            args = SandboxWriteArguments,
            cwd = ".",
            timeoutMs = 3000,
            outputLimitBytes = 8192,
            sandbox = new
            {
                profile = "linux-bwrap-v1",
                workspaceAccess = "read_write",
                network = "isolated",
            },
        });
        var readOnlyArguments = JsonSerializer.SerializeToElement(new
        {
            executable = "/usr/bin/python3",
            args = SandboxWriteArguments,
            cwd = ".",
            timeoutMs = 3000,
            outputLimitBytes = 8192,
            sandbox = new
            {
                profile = "linux-bwrap-v1",
                workspaceAccess = "read_only",
                network = "isolated",
            },
        });

        var writable = registry.Prepare("process.exec", writableArguments);
        var readOnly = registry.Prepare("process.exec", readOnlyArguments);
        var result = await registry.ExecuteAsync(writable);

        Assert.Contains(writable.Resources, static resource => resource.Kind == "sandbox");
        Assert.NotEqual(writable.OperationHash, readOnly.OperationHash);
        Assert.False(result.IsError);
        Assert.Equal("os_isolation", result.Execution?.Containment);
        Assert.Equal("/workspace", result.Execution?.Stdout.Text);
        Assert.Equal("ok", File.ReadAllText(Path.Combine(temporary.Path, "written.txt")));
    }

    /// <summary>
    /// 功能：证明 startup 后替换 canonical workspace 根会在 spawn 前失败关闭且不会执行替换目录内容。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ReplacedWorkspaceRootFailsClosedBeforeExecution()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(HardSandboxBackend.ProductionBackend))
        {
            return;
        }

        using var environment = new EnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        environment.Set(HardSandboxBackend.ProfileEnvironment, HardSandboxBackend.ProfileName);
        using var temporary = new TemporaryDirectory();
        using var backend = HardSandboxBackend.CreateFromEnvironment(
            temporary.Path,
            cliConformance: false,
            environmentConformance: false,
            out var error);
        Assert.Null(error);
        Assert.NotNull(backend);
        await backend.EnsureSelfTestedAsync(CancellationToken.None);

        var originalPath = temporary.Path;
        var movedPath = originalPath + "-original-" + Guid.NewGuid().ToString("N");
        Directory.Move(originalPath, movedPath);
        try
        {
            Directory.CreateDirectory(originalPath);
            await File.WriteAllTextAsync(Path.Combine(originalPath, "replacement.txt"), "replacement");

            var exception = await Assert.ThrowsAsync<ToolOperationException>(
                () => backend.ExecuteAsync(
                    originalPath,
                    originalPath,
                    "read_write",
                    "/usr/bin/python3",
                    ["-c", "open('/workspace/replacement-executed','w').write('unsafe')"],
                    null,
                    TimeSpan.FromSeconds(3),
                    8192,
                    CancellationToken.None));

            Assert.Equal("sandbox_unavailable", exception.Error.Details.Kind);
            Assert.False(File.Exists(Path.Combine(originalPath, "replacement-executed")));
        }
        finally
        {
            if (Directory.Exists(originalPath))
            {
                Directory.Delete(originalPath, recursive: true);
            }

            Directory.Move(movedPath, originalPath);
        }
    }

    /// <summary>
    /// 功能：证明未配置 profile 的 sandbox 调用在 approval/启动前以 sandbox_unavailable 拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void UnconfiguredSandboxFailsDuringPreflight()
    {
        using var environment = new EnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        using var temporary = new TemporaryDirectory();
        using var registry = new ToolRegistry(temporary.Path, false, false);
        var arguments = JsonSerializer.SerializeToElement(new
        {
            executable = "/usr/bin/python3",
            args = SandboxWriteArguments,
            sandbox = new
            {
                profile = "linux-bwrap-v1",
                workspaceAccess = "read_write",
                network = "isolated",
            },
        });

        var exception = Assert.Throws<ToolOperationException>(
            () => registry.Prepare("process.exec", arguments));

        Assert.Equal("sandbox_unavailable", exception.Error.Details.Kind);
        Assert.False(File.Exists(Path.Combine(temporary.Path, "written.txt")));
    }

    /// <summary>
    /// 功能：证明 conformance backend override 缺少任一门时都失败关闭且不构造 backend。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void BackendOverrideRequiresBothConformanceGates(
        bool cliConformance,
        bool environmentConformance)
    {
        using var environment = new EnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        environment.Set(HardSandboxBackend.ProfileEnvironment, HardSandboxBackend.ProfileName);
        environment.Set(HardSandboxBackend.ConformanceBackendEnvironment, "/usr/bin/true");
        using var temporary = new TemporaryDirectory();

        var backend = HardSandboxBackend.CreateFromEnvironment(
            temporary.Path,
            cliConformance,
            environmentConformance,
            out var error);

        Assert.Null(backend);
        Assert.Equal("sandbox_unavailable", error?.Details.Kind);
    }

    /// <summary>
    /// 功能：保存、覆盖并恢复 hard-sandbox 测试独占的进程环境变量。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> original;

        /// <summary>
        /// 功能：捕获固定环境名称的原始值。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="names">本 scope 独占管理的名称。</param>
        internal EnvironmentScope(IEnumerable<string> names)
        {
            original = names.ToDictionary(
                static name => name,
                static name => Environment.GetEnvironmentVariable(name),
                StringComparer.Ordinal);
        }

        /// <summary>
        /// 功能：清除全部捕获值以隔离宿主真实配置。
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
        /// 功能：设置 scope 内已登记的一个测试变量。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="name">已捕获名称。</param>
        /// <param name="value">测试值。</param>
        internal void Set(string name, string? value)
        {
            if (!original.ContainsKey(name))
            {
                throw new ArgumentException("environment name is outside test scope", nameof(name));
            }

            Environment.SetEnvironmentVariable(name, value);
        }

        /// <summary>
        /// 功能：恢复 scope 构造前的全部环境值。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        public void Dispose()
        {
            foreach (var pair in original)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }
}
