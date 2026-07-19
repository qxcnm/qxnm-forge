using System.Text;
using QxnmForge.Provider;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 .NET manifest-driven identity-only 广告、严格输入与最早 presence 分支。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
[Collection(ProviderEnvironmentGroup.Name)]
public sealed class ProviderIdentityAdvertisementTests
{
    private const string DeepSeekPresence = """
        {
          "schemaVersion": "0.1",
          "implementedAdapterIds": ["openai-completions-v1"],
          "capabilityAllowances": [
            {
              "providerId": "deepseek",
              "apiFamily": "openai-completions",
              "features": ["authentication", "text"]
            }
          ],
          "usableAuthProfiles": [
            {"providerId": "deepseek", "authProfileId": "api-key"}
          ],
          "configuredEnvironmentNames": []
        }
        """;

    private static readonly string[] EnvironmentNames =
    [
        "AGENT_PROVIDER_IDENTITY_CONFORMANCE_CONFIG",
        "AGENT_PROVIDER_ROUTE_CONFORMANCE_CONFIG",
        "AGENT_PROVIDER_IDENTITY_CREDENTIAL_CANARY",
        "DEEPSEEK_API_KEY",
        "QXNM_FORGE_PROVIDER_CONNECT_TIMEOUT_MS",
    ];

    /// <summary>
    /// 功能：确认 DeepSeek presence 只产生两个不可执行模型，并按 allowance 缩小能力。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void DeepSeekPresenceBuildsProjectedIdentityOnlySnapshot()
    {
        var (manifest, catalog) = ReadSharedSnapshots();
        var models = ProviderIdentityAdvertisement.BuildForTesting(
            manifest,
            catalog,
            Encoding.UTF8.GetBytes(DeepSeekPresence));

        Assert.Equal(2, models.Count);
        Assert.All(models, static model =>
        {
            Assert.Equal("deepseek", model.ProviderId);
            Assert.Equal("openai-completions", model.ApiFamily);
            Assert.Equal(["text"], model.Capabilities.Input);
            Assert.Equal(["text"], model.Capabilities.Output);
            Assert.False(model.Capabilities.Streaming);
            Assert.False(model.Capabilities.Tools);
            Assert.False(model.Capabilities.Reasoning);
            Assert.NotNull(model.Capabilities.ContextTokens);
            Assert.NotNull(model.Capabilities.MaxOutputTokens);
        });
        Assert.Equal(
            ["deepseek-v4-flash", "deepseek-v4-pro"],
            models.Select(static model => model.ModelId));
    }

    /// <summary>
    /// 功能：确认 presence 分支早于 transport/credential，并且生产模式无条件拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task PresencePrecedesTransportAndProductionRejects()
    {
        using var temporary = new TemporaryDirectory();
        var configurationPath = Path.Combine(temporary.Path, "presence.json");
        await File.WriteAllBytesAsync(configurationPath, Encoding.UTF8.GetBytes(DeepSeekPresence));
        using var environment = new IdentityEnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        environment.Set("AGENT_PROVIDER_IDENTITY_CONFORMANCE_CONFIG", configurationPath);
        environment.Set("AGENT_PROVIDER_IDENTITY_CREDENTIAL_CANARY", "unit-canary-must-not-be-read");
        environment.Set("DEEPSEEK_API_KEY", "unit-canary-must-not-be-read");
        environment.Set("QXNM_FORGE_PROVIDER_CONNECT_TIMEOUT_MS", "not-a-number");

        await using (var registry = ProviderRegistryFactory.CreateFromEnvironment(
                         conformanceMode: true))
        {
            Assert.Single(registry.Providers);
            Assert.Equal(["deepseek", "faux"], registry.Advertisements.Select(static item => item.Id));
            Assert.Equal(2, registry.ListModels("deepseek").Count);
            Assert.Throws<ProviderUnavailableException>(() => registry.GetRequired(
                new QxnmForge.Domain.ProviderSelection(
                    "deepseek",
                    "deepseek-v4-flash",
                    "openai-completions")));
        }

        Assert.Throws<ProviderIdentityAdvertisementException>(() =>
            ProviderRegistryFactory.CreateFromEnvironment(conformanceMode: false));
    }

    /// <summary>
    /// 功能：确认 presence 的未知字段、重复键和非标准 NaN 全部 fail closed。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="invalidConfiguration">畸形且不含 secret 的 presence JSON。</param>
    [Theory]
    [InlineData("{\"schemaVersion\":\"0.1\",\"implementedAdapterIds\":[],\"capabilityAllowances\":[],\"usableAuthProfiles\":[],\"configuredEnvironmentNames\":[],\"unknown\":true}")]
    [InlineData("{\"schemaVersion\":\"0.1\",\"implementedAdapterIds\":[],\"capabilityAllowances\":[],\"usableAuthProfiles\":[],\"configuredEnvironmentNames\":[],\"schemaVersion\":\"0.1\"}")]
    [InlineData("{\"schemaVersion\":\"0.1\",\"implementedAdapterIds\":[],\"capabilityAllowances\":[],\"usableAuthProfiles\":[],\"configuredEnvironmentNames\":[],\"unknown\":NaN}")]
    public void StrictPresenceRejectsMalformedJson(string invalidConfiguration)
    {
        var (manifest, catalog) = ReadSharedSnapshots();
        Assert.Throws<ProviderIdentityAdvertisementException>(() =>
            ProviderIdentityAdvertisement.BuildForTesting(
                manifest,
                catalog,
                Encoding.UTF8.GetBytes(invalidConfiguration)));
    }

    /// <summary>
    /// 功能：确认冻结 manifest/catalog 的重复键、NaN、未知字段与引用漂移均被拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="mutation">固定非敏感 mutation 名称。</param>
    [Theory]
    [InlineData("manifest-duplicate")]
    [InlineData("manifest-unknown")]
    [InlineData("manifest-reference")]
    [InlineData("catalog-nan")]
    [InlineData("catalog-unknown")]
    public void FrozenSnapshotsRejectStrictMutations(string mutation)
    {
        var (manifest, catalog) = ReadSharedSnapshots();
        var (mutatedManifest, mutatedCatalog) = MutateSnapshots(manifest, catalog, mutation);
        Assert.Throws<ProviderIdentityAdvertisementException>(() =>
            ProviderIdentityAdvertisement.BuildForTesting(
                mutatedManifest,
                mutatedCatalog,
                Encoding.UTF8.GetBytes(DeepSeekPresence)));
    }

    /// <summary>
    /// 功能：读取测试输出中由项目链接的 canonical manifest/catalog bytes。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>当前 shared snapshot 的两个独立 byte 数组。</returns>
    private static (byte[] Manifest, byte[] Catalog) ReadSharedSnapshots()
    {
        var fixtureRoot = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "provider-identity");
        return (
            File.ReadAllBytes(Path.Combine(fixtureRoot, "providers.v1.json")),
            File.ReadAllBytes(Path.Combine(fixtureRoot, "models.v1.json")));
    }

    /// <summary>
    /// 功能：生成五种固定 shared snapshot 漂移，不把实例正文写入诊断。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="manifest">canonical manifest bytes。</param>
    /// <param name="catalog">canonical catalog bytes。</param>
    /// <param name="mutation">固定 mutation 名称。</param>
    /// <returns>只修改目标文档的一对 UTF-8 bytes。</returns>
    private static (byte[] Manifest, byte[] Catalog) MutateSnapshots(
        byte[] manifest,
        byte[] catalog,
        string mutation)
    {
        var manifestText = Encoding.UTF8.GetString(manifest);
        var catalogText = Encoding.UTF8.GetString(catalog);
        switch (mutation)
        {
            case "manifest-duplicate":
                manifestText = manifestText.Replace(
                    "{\n  \"$schema\"",
                    "{\n  \"schemaVersion\": \"0.2\",\n  \"$schema\"",
                    StringComparison.Ordinal);
                break;
            case "manifest-unknown":
                manifestText = manifestText.Replace(
                    "{\n  \"$schema\"",
                    "{\n  \"unknown\": true,\n  \"$schema\"",
                    StringComparison.Ordinal);
                break;
            case "manifest-reference":
                manifestText = manifestText.Replace(
                    "\"adapterId\": \"openai-completions-v1\"",
                    "\"adapterId\": \"missing-adapter\"",
                    StringComparison.Ordinal);
                break;
            case "catalog-nan":
                catalogText = catalogText.Replace(
                    "\"contextWindow\": 128000",
                    "\"contextWindow\": NaN",
                    StringComparison.Ordinal);
                break;
            case "catalog-unknown":
                catalogText = catalogText.Replace(
                    "{\n  \"$schema\"",
                    "{\n  \"unknown\": true,\n  \"$schema\"",
                    StringComparison.Ordinal);
                break;
            default:
                throw new ArgumentException("unknown snapshot mutation", nameof(mutation));
        }

        return (Encoding.UTF8.GetBytes(manifestText), Encoding.UTF8.GetBytes(catalogText));
    }

    /// <summary>
    /// 功能：临时覆盖固定 Provider identity 环境名称并在释放时完整恢复。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class IdentityEnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> original;

        /// <summary>
        /// 功能：捕获测试独占环境名称的进入值。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="names">固定环境名称集合。</param>
        internal IdentityEnvironmentScope(IEnumerable<string> names)
        {
            original = names.ToDictionary(
                static name => name,
                static name => Environment.GetEnvironmentVariable(name),
                StringComparer.Ordinal);
        }

        /// <summary>
        /// 功能：清除本 scope 管理的全部环境名称。
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
        /// 功能：设置一个已由本 scope 捕获的测试环境值。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="name">必须属于固定集合的名称。</param>
        /// <param name="value">非真实测试值。</param>
        internal void Set(string name, string value)
        {
            if (!original.ContainsKey(name))
            {
                throw new ArgumentException("environment name is outside identity test scope", nameof(name));
            }

            Environment.SetEnvironmentVariable(name, value);
        }

        /// <summary>
        /// 功能：恢复构造时捕获的全部环境值。
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
