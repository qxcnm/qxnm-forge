using System.Reflection;
using System.Text;
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
        var entryPath = CredentialEntryPath(state, "relay-example-relay");
        Assert.Equal("first-local-test-key", File.ReadAllText(entryPath));
        store.Set("relay-example-relay", "second-local-test-key");
        Assert.True(store.TryReadForRequest("relay-example-relay", out var secret));
        Assert.Equal("second-local-test-key", secret);

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(entryPath);
            const UnixFileMode exposed = UnixFileMode.GroupRead |
                UnixFileMode.GroupWrite |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead |
                UnixFileMode.OtherWrite |
                UnixFileMode.OtherExecute;
            Assert.Equal((UnixFileMode)0, mode & exposed);
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                mode);
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.Combine(state, "credentials")));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.Combine(state, "credentials", "provider-credentials.d")));
        }

        Assert.True(store.Remove("relay-example-relay"));
        Assert.Empty(store.List());
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
    /// 功能：确认独立 credential 叶被 symlink 替换时 presence 读取失败关闭。
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
        File.WriteAllText(outside, "outside-test-key");
        File.CreateSymbolicLink(
            CredentialEntryPath(state, "relay-example-relay"),
            outside);

        Assert.Throws<ProviderCommercialStateException>(() => store.List());
    }

    /// <summary>
    /// 功能：确认 presence 只读取 canonical 叶名与元数据，即使正文损坏也不会提前打开 secret。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void CredentialPresenceDoesNotReadEntryBody()
    {
        using var directory = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(directory.Path, "workspace")).FullName;
        var state = Path.Combine(directory.Path, "state");
        var store = new ProviderCredentialStore(state, workspace);
        store.Set("con", "body-must-not-be-read");
        var entryPath = CredentialEntryPath(state, "con");
        Assert.Equal("Y29u.credential", Path.GetFileName(entryPath));
        File.WriteAllBytes(entryPath, [0xff]);

        Assert.Equal(["con"], store.List());
        Assert.True(store.ContainsAll("con"));
        Assert.False(store.TryReadForRequest("con", out var credential));
        Assert.Null(credential);
    }

    /// <summary>
    /// 功能：确认构造期把旧聚合 JSON 经 staging 原子迁移为独立原始 UTF-8 条目。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void LegacyCredentialDocumentMigratesToIndependentEntries()
    {
        using var directory = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(directory.Path, "workspace")).FullName;
        var state = Path.Combine(directory.Path, "state");
        var legacyPath = Path.Combine(state, "credentials", "provider-credentials.json");
        var legacy = new ProviderCredentialDocument(
            "0.1",
            [
                new StoredProviderCredential("alpha", "alpha-migration-key"),
                new StoredProviderCredential("beta", "beta-migration-key"),
            ]);
        CommercialFileSafety.AtomicWrite(
            legacyPath,
            JsonSerializer.SerializeToUtf8Bytes(legacy, QxnmForge.Serialization.JsonDefaults.Options),
            sensitive: true);
        var stagingRoot = Path.Combine(
            state,
            "credentials",
            ".provider-credentials.d.migrating");
        CommercialFileSafety.CreatePrivateDirectory(stagingRoot);
        CommercialFileSafety.WriteCreateNewSecure(
            Path.Combine(stagingRoot, Path.GetFileName(CredentialEntryPath(state, "stale"))),
            Encoding.UTF8.GetBytes("stale-partial-key"));

        var store = new ProviderCredentialStore(state, workspace);

        Assert.False(File.Exists(legacyPath));
        Assert.Equal(["alpha", "beta"], store.List());
        Assert.Equal("alpha-migration-key", File.ReadAllText(CredentialEntryPath(state, "alpha")));
        Assert.True(store.TryReadForRequest("beta", out var beta));
        Assert.Equal("beta-migration-key", beta);
        Assert.False(Directory.Exists(stagingRoot));
    }

    /// <summary>
    /// 功能：确认最终 v0.2 目录存在时遗留旧 JSON 只删除不解析，不能覆盖当前条目。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void PublishedCredentialDirectoryIsAuthoritativeOverLegacyDocument()
    {
        using var directory = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(directory.Path, "workspace")).FullName;
        var state = Path.Combine(directory.Path, "state");
        var store = new ProviderCredentialStore(state, workspace);
        store.Set("alpha", "current-entry-key");
        var legacyPath = Path.Combine(state, "credentials", "provider-credentials.json");
        CommercialFileSafety.AtomicWrite(
            legacyPath,
            Encoding.UTF8.GetBytes("not-json-and-must-not-be-parsed"),
            sensitive: true);
        var orphanTemporaryPath = Path.Combine(
            state,
            "credentials",
            ".provider-credential-0123456789abcdef0123456789abcdef.tmp");
        CommercialFileSafety.WriteCreateNewSecure(
            orphanTemporaryPath,
            Encoding.UTF8.GetBytes("orphan-temporary-key"));

        var reopened = new ProviderCredentialStore(state, workspace);

        Assert.False(File.Exists(legacyPath));
        Assert.False(File.Exists(orphanTemporaryPath));
        Assert.Equal(["alpha"], reopened.List());
        Assert.True(reopened.TryReadForRequest("alpha", out var credential));
        Assert.Equal("current-entry-key", credential);
    }

    /// <summary>
    /// 功能：确认 credential 限制以 raw UTF-8 bytes 而非 .NET UTF-16 字符数计算。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void CredentialLengthUsesUtf8Bytes()
    {
        using var directory = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(directory.Path, "workspace")).FullName;
        var state = Path.Combine(directory.Path, "state");
        var store = new ProviderCredentialStore(state, workspace);
        var accepted = new string('中', 5_461) + "a";
        Assert.Equal(16_384, Encoding.UTF8.GetByteCount(accepted));
        store.Set("utf8", accepted);
        Assert.True(store.TryReadForRequest("utf8", out var read));
        Assert.Equal(accepted, read);

        var rejected = accepted + "a";
        Assert.Equal(16_385, Encoding.UTF8.GetByteCount(rejected));
        Assert.Throws<ProviderCommercialStateException>(() => store.Set("too-large", rejected));
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
    /// 功能：按生产 canonical base64url 合同构造测试 credential 叶路径。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="stateRoot">隔离测试状态根。</param>
    /// <param name="providerId">合法 Provider ID。</param>
    /// <returns>不包含原始 Provider ID 的 `.credential` 叶路径。</returns>
    private static string CredentialEntryPath(string stateRoot, string providerId)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(providerId))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return Path.Combine(
            stateRoot,
            "credentials",
            "provider-credentials.d",
            encoded + ".credential");
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
