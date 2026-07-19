using System.Reflection;
using System.Runtime.CompilerServices;
using QxnmForge.Domain;
using QxnmForge.Provider;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 .NET ADR 0018 route snapshot、双门控、catalog allowlist 与 credential 生命周期。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
[Collection(ProviderEnvironmentGroup.Name)]
public sealed class ProviderExecutableRouteTests
{
    private static readonly string[] EnvironmentNames =
    [
        "AGENT_PROVIDER_IDENTITY_CONFORMANCE_CONFIG",
        "AGENT_PROVIDER_ROUTE_CONFORMANCE_CONFIG",
        "QXNM_FORGE_PROVIDER_CONFORMANCE",
        "QXNM_FORGE_PROVIDER_CONNECT_TIMEOUT_MS",
        "GROQ_API_KEY",
        "MINIMAX_API_KEY",
        "MISTRAL_API_KEY",
        "OPENAI_API_KEY",
        "GEMINI_API_KEY",
        "OPENROUTER_API_KEY",
    ];

    /// <summary>
    /// 功能：确认生产六条 route 的 133 个模型来自同一 catalog snapshot 并保持能力收窄。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ProductionBuildsSixCatalogBoundRoutes()
    {
        using var environment = new RouteEnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        foreach (var name in EnvironmentNames.Where(static name => name.EndsWith("_API_KEY", StringComparison.Ordinal)))
        {
            environment.Set(name, "unit-route-key");
        }

        await using var registry = ProviderRegistryFactory.CreateFromEnvironment();
        Assert.Equal(
            ["faux", "google", "groq", "minimax", "mistral", "openai", "openrouter"],
            registry.Advertisements.Select(static provider => provider.Id));
        Assert.Equal(7, registry.ListModels("groq").Count);
        Assert.Equal(3, registry.ListModels("minimax").Count);
        Assert.Equal(30, registry.ListModels("mistral").Count);
        Assert.Equal(42, registry.ListModels("openai").Count);
        Assert.Equal(16, registry.ListModels("google").Count);
        Assert.Equal(35, registry.ListModels("openrouter").Count);

        var google = Assert.Single(
            registry.ListModels("google"),
            static model => model.ModelId == "gemini-2.5-flash");
        Assert.Equal(["text"], google.Capabilities.Input);
        Assert.Equal(["text"], google.Capabilities.Output);
        Assert.True(google.Capabilities.Streaming);
        Assert.True(google.Capabilities.Tools);
        Assert.False(google.Capabilities.Reasoning);

        var image = Assert.Single(
            registry.ListModels("openrouter"),
            static model => model.ModelId == "google/gemini-2.5-flash-image");
        Assert.Equal(["image", "text"], image.Capabilities.Input);
        Assert.Equal(["image", "text"], image.Capabilities.Output);
        Assert.False(image.Capabilities.Streaming);
        Assert.False(image.Capabilities.Tools);
        Assert.False(image.Capabilities.Reasoning);
    }

    /// <summary>
    /// 功能：确认双 conformance 开关下配置只保留一个 Groq 模型并替换为 literal loopback base。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ConformanceConfigurationNarrowsRouteAndEndpoint()
    {
        using var temporary = new TemporaryDirectory();
        var configurationPath = WriteConfiguration(
            temporary.Path,
            "groq",
            "openai-completions",
            "llama-3.1-8b-instant",
            "http://127.0.0.1:45123/case/groq");
        using var environment = new RouteEnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        environment.Set("AGENT_PROVIDER_ROUTE_CONFORMANCE_CONFIG", configurationPath);
        environment.Set("QXNM_FORGE_PROVIDER_CONFORMANCE", "1");
        environment.Set("GROQ_API_KEY", "unit-route-key");

        await using var registry = ProviderRegistryFactory.CreateFromEnvironment(conformanceMode: true);
        var model = Assert.Single(registry.ListModels("groq"));
        Assert.Equal("llama-3.1-8b-instant", model.ModelId);
        var provider = Assert.Single(registry.Providers, static item => item.Id == "groq");
        var target = CreateRequestEndpoint(provider, model.ModelId);
        Assert.Equal("127.0.0.1", target.Host);
        Assert.Equal(45123, target.Port);
        Assert.Equal("/case/groq/chat/completions", target.AbsolutePath);
        Assert.Same(
            provider,
            registry.GetRequired(new ProviderSelection(
                "groq",
                model.ModelId,
                "openai-completions")));
    }

    /// <summary>
    /// 功能：确认 route presence 在生产模式先于配置文件、transport 与 credential 被固定拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void ProductionPresenceFailsBeforeOtherEnvironmentReads()
    {
        using var environment = new RouteEnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        environment.Set("AGENT_PROVIDER_ROUTE_CONFORMANCE_CONFIG", "/path/must/not/be/read");
        environment.Set("QXNM_FORGE_PROVIDER_CONNECT_TIMEOUT_MS", "not-a-number");
        environment.Set("GROQ_API_KEY", "unit-secret-must-not-be-retained");

        Assert.Throws<ProviderExecutableRouteException>(() =>
            ProviderRegistryFactory.CreateFromEnvironment(conformanceMode: false));
        Assert.Throws<ProviderExecutableRouteException>(() =>
            ProviderRegistryFactory.CreateFromEnvironment(conformanceMode: true));
    }

    /// <summary>
    /// 功能：确认 route 与 identity presence 同时存在时在读取任一文件、transport 或 credential 前固定拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void RouteAndIdentityPresencesAreMutuallyExclusiveAtEntry()
    {
        using var environment = new RouteEnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        const string routePath = "/route-presence-must-not-be-read";
        const string identityPath = "/identity-presence-must-not-be-read";
        const string credentialCanary = "dual-presence-credential-must-not-be-retained";
        environment.Set("AGENT_PROVIDER_ROUTE_CONFORMANCE_CONFIG", routePath);
        environment.Set("AGENT_PROVIDER_IDENTITY_CONFORMANCE_CONFIG", identityPath);
        environment.Set("QXNM_FORGE_PROVIDER_CONFORMANCE", "1");
        environment.Set("QXNM_FORGE_PROVIDER_CONNECT_TIMEOUT_MS", "not-a-number");
        environment.Set("GROQ_API_KEY", credentialCanary);

        var exception = Assert.Throws<ProviderExecutableRouteException>(() =>
            ProviderRegistryFactory.CreateFromEnvironment(conformanceMode: true));
        Assert.DoesNotContain(routePath, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(identityPath, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(credentialCanary, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：确认 unknown field、重复键、非回环 endpoint 与 spine 外 route 全部启动失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">固定畸形配置类别。</param>
    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("endpoint")]
    [InlineData("route")]
    public void StrictConfigurationRejectsInvalidInputs(string kind)
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "route.json");
        var json = kind switch
        {
            "unknown" => "{\"schemaVersion\":\"0.1\",\"providerId\":\"groq\",\"apiFamily\":\"openai-completions\",\"modelId\":\"llama-3.1-8b-instant\",\"endpointBase\":\"http://127.0.0.1:45123/case\",\"unknown\":true}",
            "duplicate" => "{\"schemaVersion\":\"0.1\",\"providerId\":\"groq\",\"providerId\":\"groq\",\"apiFamily\":\"openai-completions\",\"modelId\":\"llama-3.1-8b-instant\",\"endpointBase\":\"http://127.0.0.1:45123/case\"}",
            "endpoint" => "{\"schemaVersion\":\"0.1\",\"providerId\":\"groq\",\"apiFamily\":\"openai-completions\",\"modelId\":\"llama-3.1-8b-instant\",\"endpointBase\":\"http://localhost:45123/case\"}",
            "route" => "{\"schemaVersion\":\"0.1\",\"providerId\":\"deepseek\",\"apiFamily\":\"openai-completions\",\"modelId\":\"deepseek-v4-flash\",\"endpointBase\":\"http://127.0.0.1:45123/case\"}",
            _ => throw new ArgumentException("unknown test kind", nameof(kind)),
        };
        File.WriteAllText(path, json);
        using var environment = new RouteEnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        environment.Set("AGENT_PROVIDER_ROUTE_CONFORMANCE_CONFIG", path);
        environment.Set("QXNM_FORGE_PROVIDER_CONFORMANCE", "1");
        environment.Set("GROQ_API_KEY", "unit-route-key");

        Assert.Throws<ProviderExecutableRouteException>(() =>
            ProviderRegistryFactory.CreateFromEnvironment(conformanceMode: true));
    }

    /// <summary>
    /// 功能：确认 Linux route config 最终路径为 symlink 时由 O_NOFOLLOW 在读取前拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void ConfigurationSymlinkIsRejectedWithoutFollowingTarget()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var target = WriteConfiguration(
            temporary.Path,
            "groq",
            "openai-completions",
            "llama-3.1-8b-instant",
            "http://127.0.0.1:45123/case");
        var link = Path.Combine(temporary.Path, "route-link.json");
        File.CreateSymbolicLink(link, target);
        using var environment = new RouteEnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        environment.Set("AGENT_PROVIDER_ROUTE_CONFORMANCE_CONFIG", link);
        environment.Set("QXNM_FORGE_PROVIDER_CONFORMANCE", "1");
        environment.Set("GROQ_API_KEY", "unit-route-key");

        Assert.Throws<ProviderExecutableRouteException>(() =>
            ProviderRegistryFactory.CreateFromEnvironment(conformanceMode: true));
    }

    /// <summary>
    /// 功能：确认缺 credential 时 route 不广告，且 credential 变空后下一次 run 解析在 Session 前拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task MissingOrRotatedEmptyCredentialMakesRouteUnavailable()
    {
        using var temporary = new TemporaryDirectory();
        var configurationPath = WriteConfiguration(
            temporary.Path,
            "groq",
            "openai-completions",
            "llama-3.1-8b-instant",
            "http://127.0.0.1:45123/case");
        using var environment = new RouteEnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        environment.Set("AGENT_PROVIDER_ROUTE_CONFORMANCE_CONFIG", configurationPath);
        environment.Set("QXNM_FORGE_PROVIDER_CONFORMANCE", "1");

        await using (var unavailable = ProviderRegistryFactory.CreateFromEnvironment(conformanceMode: true))
        {
            Assert.Empty(unavailable.ListModels("groq"));
            Assert.Throws<ProviderUnavailableException>(() => unavailable.GetRequired(
                new ProviderSelection(
                    "groq",
                    "llama-3.1-8b-instant",
                    "openai-completions")));
        }

        environment.Set("GROQ_API_KEY", "unit-route-key");
        await using var registry = ProviderRegistryFactory.CreateFromEnvironment(conformanceMode: true);
        Assert.Single(registry.ListModels("groq"));
        environment.Set("GROQ_API_KEY", null);
        Assert.Throws<ProviderUnavailableException>(() => registry.GetRequired(
            new ProviderSelection(
                "groq",
                "llama-3.1-8b-instant",
                "openai-completions")));
    }

    /// <summary>
    /// 功能：确认 registry 以 route key 保存同 Provider 多 family，并对省略 family 的多命中返回歧义。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task RegistryUsesRouteKeysAndRejectsAmbiguousOmission()
    {
        var chat = new RouteStubProvider("shared", "openai-completions", "shared-model");
        var responses = new RouteStubProvider("shared", "openai-responses", "shared-model");
        await using var registry = new ProviderRegistry([chat, responses]);

        Assert.Same(
            chat,
            registry.GetRequired(new ProviderSelection(
                "shared",
                "shared-model",
                "openai-completions")));
        Assert.Same(
            responses,
            registry.GetRequired(new ProviderSelection(
                "shared",
                "shared-model",
                "openai-responses")));
        Assert.Throws<ArgumentException>(() => registry.GetRequired(
            new ProviderSelection("shared", "shared-model")));
    }

    /// <summary>
    /// 功能：写出一个只含 Schema 允许字段的 route conformance 配置。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="directory">临时目录。</param>
    /// <param name="providerId">canonical Provider ID。</param>
    /// <param name="apiFamily">canonical family。</param>
    /// <param name="modelId">catalog model。</param>
    /// <param name="endpointBase">literal loopback base。</param>
    /// <returns>新 regular 文件完整路径。</returns>
    private static string WriteConfiguration(
        string directory,
        string providerId,
        string apiFamily,
        string modelId,
        string endpointBase)
    {
        var path = Path.Combine(directory, "route.json");
        File.WriteAllText(
            path,
            $$"""
            {"schemaVersion":"0.1","providerId":"{{providerId}}","apiFamily":"{{apiFamily}}","modelId":"{{modelId}}","endpointBase":"{{endpointBase}}"}
            """);
        return path;
    }

    /// <summary>
    /// 功能：通过受保护边界取得 adapter 将使用的原生 request target，且不发送网络请求。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="provider">route adapter。</param>
    /// <param name="modelId">allowlist model ID。</param>
    /// <returns>认证注入前的绝对 URI。</returns>
    private static Uri CreateRequestEndpoint(IProvider provider, string modelId)
    {
        var method = provider.GetType().GetMethod(
            "CreateRequestEndpoint",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var request = new ProviderRequest(
            new ProviderSelection(provider.Id, modelId, provider.ApiFamily),
            [],
            []);
        return Assert.IsType<Uri>(method.Invoke(provider, [request]));
    }

    /// <summary>
    /// 功能：临时覆盖固定 Provider route 环境并在释放时恢复全部原值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class RouteEnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> original;

        /// <summary>
        /// 功能：捕获测试独占环境名称的进入值。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="names">固定环境名称集合。</param>
        internal RouteEnvironmentScope(IEnumerable<string> names)
        {
            original = names.ToDictionary(
                static name => name,
                static name => Environment.GetEnvironmentVariable(name),
                StringComparer.Ordinal);
        }

        /// <summary>
        /// 功能：清除 scope 内全部环境名称以隔离真实配置。
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
        /// 功能：设置一个属于 scope 的合成环境值。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="name">固定环境名称。</param>
        /// <param name="value">合成值或 null。</param>
        internal void Set(string name, string? value)
        {
            if (!original.ContainsKey(name))
            {
                throw new ArgumentException("environment name is outside test scope", nameof(name));
            }

            Environment.SetEnvironmentVariable(name, value);
        }

        /// <summary>
        /// 功能：恢复测试进入前的全部环境值。
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

    /// <summary>
    /// 功能：提供只验证 registry route-key 选择且不执行网络的最小测试 adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class RouteStubProvider : IProvider
    {
        /// <summary>
        /// 功能：创建固定 Provider/family/model 的无网络 adapter。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="id">测试 Provider ID。</param>
        /// <param name="apiFamily">唯一测试 family。</param>
        /// <param name="modelId">唯一测试模型。</param>
        internal RouteStubProvider(string id, string apiFamily, string modelId)
        {
            Id = id;
            ApiFamily = apiFamily;
            Models = [modelId];
        }

        /// <summary>
        /// 功能：取得测试 Provider ID。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// 功能：取得测试 route family。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        public string ApiFamily { get; }

        /// <summary>
        /// 功能：取得唯一测试模型。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        public IReadOnlyList<string> Models { get; }

        /// <summary>
        /// 功能：判断候选模型是否精确命中唯一测试模型。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="modelId">候选模型。</param>
        /// <returns>精确命中时为 true。</returns>
        public bool SupportsModel(string modelId)
        {
            return string.Equals(modelId, Models[0], StringComparison.Ordinal);
        }

        /// <summary>
        /// 功能：返回立即完成的空流；本测试不会调用该执行边界。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="request">未使用的测试请求。</param>
        /// <param name="cancellationToken">进入时检查的取消信号。</param>
        /// <returns>不产生信号的异步流。</returns>
        public async IAsyncEnumerable<ProviderSignal> StreamAsync(
            ProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }
}
