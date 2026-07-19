using System.Reflection;
using System.Text.Json;
using QxnmForge.Domain;
using QxnmForge.Provider;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 .NET CredentialStore、本地推广 route 和运行时注册闭环。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ProviderCommercialStateTests
{
    /// <summary>
    /// 功能：确认 credential 可保存、轮换、只列 ID 并删除，且 Unix 文件权限不向 group/other 开放。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void CredentialStoreLifecycleDoesNotExposeSecretInStatus()
    {
        using var directory = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "workspace")).FullName;
        var state = System.IO.Path.Combine(directory.Path, "state");
        var store = new ProviderCredentialStore(state, workspace);

        store.Set("relay-example-relay", "first-local-test-key");
        Assert.Equal(["relay-example-relay"], store.List());
        store.Set("relay-example-relay", "second-local-test-key");
        Assert.True(store.TryReadForRequest("relay-example-relay", out var secret));
        Assert.Equal("second-local-test-key", secret);
        Assert.True(store.Remove("relay-example-relay"));
        Assert.Empty(store.List());

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(System.IO.Path.Combine(
                state,
                "credentials",
                "provider-credentials.json"));
            const UnixFileMode exposed = UnixFileMode.GroupRead |
                UnixFileMode.GroupWrite |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead |
                UnixFileMode.OtherWrite |
                UnixFileMode.OtherExecute;
            Assert.Equal((UnixFileMode)0, mode & exposed);
        }
    }

    /// <summary>
    /// 功能：确认安装快照固定目录版本、family、HTTPS base 和模型且不包含 credential。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void InstalledRouteUsesSharedNonSecretJsonShape()
    {
        using var directory = new TemporaryDirectory();
        var store = new InstalledSponsoredRouteStore(directory.Path);
        var route = store.Install(TestCatalog(), "example-relay", "model-v1");

        Assert.Equal("relay-example-relay", route.ProviderId);
        Assert.Equal<ulong>(7, route.CatalogVersion);
        Assert.Equal(["model-v1"], route.Models);
        var text = File.ReadAllText(store.DocumentPath);
        Assert.Contains("\"schemaVersion\":\"0.1\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("local-test-key", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：确认 production registry 能独立加载有 credential 的本地 route 并构造同源 family target。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task RegistryLoadsInstalledRouteThroughExistingFamilyAdapter()
    {
        using var directory = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "workspace")).FullName;
        var state = System.IO.Path.Combine(directory.Path, "state");
        var route = new InstalledSponsoredRouteStore(state)
            .Install(TestCatalog(), "example-relay", "model-v1");
        new ProviderCredentialStore(state, workspace)
            .Set(route.ProviderId, "registry-local-test-key");

        await using var registry = ProviderRegistryFactory.CreateFromEnvironment(
            stateRoot: state,
            workspace: workspace);
        var provider = registry.GetRequired(new ProviderSelection(
            route.ProviderId,
            "model-v1",
            "openai-completions"));
        Assert.Equal(route.ProviderId, provider.Id);
        Assert.Equal("openai-completions", provider.ApiFamily);
        Assert.Contains(registry.Advertisements, item => item.Id == route.ProviderId);
        Assert.Equal(
            "https://relay.example/v1/chat/completions",
            CreateRequestEndpoint(provider, "model-v1").AbsoluteUri);
    }

    /// <summary>
    /// 功能：确认 CredentialStore 位于 workspace 内时在任何 secret 写入前拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void CredentialStoreInsideWorkspaceIsRejected()
    {
        using var directory = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "workspace")).FullName;
        Assert.Throws<ProviderCommercialStateException>(() =>
            new ProviderCredentialStore(System.IO.Path.Combine(workspace, "state"), workspace));
    }

    /// <summary>
    /// 功能：确认 credential 文档被 symlink 替换时读取失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void SymlinkedCredentialDocumentIsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "workspace")).FullName;
        var state = System.IO.Path.Combine(directory.Path, "state");
        var store = new ProviderCredentialStore(state, workspace);
        var outside = System.IO.Path.Combine(directory.Path, "outside.json");
        File.WriteAllText(outside, "{\"schemaVersion\":\"0.1\",\"credentials\":[]}");
        File.CreateSymbolicLink(
            System.IO.Path.Combine(state, "credentials", "provider-credentials.json"),
            outside);

        Assert.Throws<ProviderCommercialStateException>(() => store.List());
    }


    /// <summary>
    /// 功能：构造严格有效且只用于本地测试的推广 catalog DTO。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>包含一条 Chat family 测试 route 的目录。</returns>
    private static SponsoredProviderCatalog TestCatalog()
    {
        var now = DateTimeOffset.UtcNow;
        return new SponsoredProviderCatalog(
            "0.1",
            7,
            UtcText(now),
            UtcText(now.AddDays(1)),
            [
                new SponsoredProviderEntry(
                    "example-relay",
                    "示例中转站",
                    "测试推广条目",
                    "openai-completions",
                    "https://relay.example/v1",
                    "https://relay.example/register",
                    "通过该链接注册，发行方可能获得佣金",
                    1),
            ]);
    }

    /// <summary>
    /// 功能：通过保护方法反射测试 adapter 生成的最终公开 request target。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="provider">本地推广 adapter。</param>
    /// <param name="modelId">allowlist 模型。</param>
    /// <returns>同源追加完成的 URI。</returns>
    private static Uri CreateRequestEndpoint(IProvider provider, string modelId)
    {
        var method = provider.GetType().GetMethod(
            "CreateRequestEndpoint",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("provider endpoint method is missing");
        var request = new ProviderRequest(
            new ProviderSelection(provider.Id, modelId, provider.ApiFamily),
            [],
            []);
        return (Uri)(method.Invoke(provider, [request])
            ?? throw new InvalidOperationException("provider endpoint is missing"));
    }

    /// <summary>
    /// 功能：把测试时间编码为秒精度 UTC Z 文本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">任意 offset 时间。</param>
    /// <returns>严格 RFC 3339 UTC 文本。</returns>
    private static string UtcText(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
