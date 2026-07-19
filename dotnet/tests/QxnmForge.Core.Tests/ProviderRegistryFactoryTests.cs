using System.Reflection;
using QxnmForge.Domain;
using QxnmForge.Provider;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：禁止依赖进程环境的 Provider registry 测试与其他测试并行。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProviderEnvironmentGroup
{
    public const string Name = "provider-environment";
}

/// <summary>
/// 功能：验证第二批 Provider 的生产配置、runtime-required Azure base 与 conformance origin 隔离。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
[Collection(ProviderEnvironmentGroup.Name)]
public sealed class ProviderRegistryFactoryTests
{
    private static readonly string[] EnvironmentNames =
    [
        "AGENT_PROVIDER_IDENTITY_CONFORMANCE_CONFIG",
        "AGENT_PROVIDER_IDENTITY_CREDENTIAL_CANARY",
        "AGENT_PROVIDER_ROUTE_CONFORMANCE_CONFIG",
        "QXNM_FORGE_PROVIDER_CONFORMANCE",
        "QXNM_FORGE_OPENAI_COMPATIBLE_ENDPOINT",
        "QXNM_FORGE_OPENAI_RESPONSES_ENDPOINT",
        "QXNM_FORGE_ANTHROPIC_ENDPOINT",
        "QXNM_FORGE_MISTRAL_CONVERSATIONS_ENDPOINT",
        "QXNM_FORGE_AZURE_OPENAI_RESPONSES_ENDPOINT",
        "QXNM_FORGE_GOOGLE_GENERATIVE_AI_ENDPOINT",
        "QXNM_FORGE_GOOGLE_VERTEX_ENDPOINT",
        "QXNM_FORGE_BEDROCK_CONVERSE_STREAM_ENDPOINT",
        "QXNM_FORGE_OPENAI_CODEX_RESPONSES_ENDPOINT",
        "QXNM_FORGE_OPENROUTER_IMAGES_ENDPOINT",
        "OPENAI_API_KEY",
        "GROQ_API_KEY",
        "MINIMAX_API_KEY",
        "ANTHROPIC_API_KEY",
        "MISTRAL_API_KEY",
        "AZURE_OPENAI_API_KEY",
        "AZURE_OPENAI_BASE_URL",
        "AZURE_OPENAI_API_VERSION",
        "GEMINI_API_KEY",
        "QXNM_FORGE_GOOGLE_VERTEX_OAUTH_TOKEN",
        "QXNM_FORGE_OPENAI_CODEX_OAUTH_TOKEN",
        "OPENROUTER_API_KEY",
        "GOOGLE_CLOUD_PROJECT",
        "GCLOUD_PROJECT",
        "GOOGLE_CLOUD_LOCATION",
        "AWS_ACCESS_KEY_ID",
        "AWS_SECRET_ACCESS_KEY",
        "AWS_SESSION_TOKEN",
        "AWS_REGION",
        "AWS_DEFAULT_REGION",
        "QXNM_FORGE_PROVIDER_CONNECT_TIMEOUT_MS",
        "QXNM_FORGE_PROVIDER_IDLE_TIMEOUT_MS",
        "QXNM_FORGE_PROVIDER_TOTAL_TIMEOUT_MS",
        "QXNM_FORGE_PROVIDER_MAX_ATTEMPTS",
        "QXNM_FORGE_PROVIDER_RETRY_MAX_DELAY_MS",
    ];

    /// <summary>
    /// 功能：确认生产只从冻结 route spine 广告 Mistral/Google，并忽略测试 endpoint 与未列入 Azure route。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ProductionUsesFixedOriginsAndRequiresAzureBase()
    {
        using var environment = new EnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        environment.Set("MISTRAL_API_KEY", "unit-test-placeholder");
        environment.Set("AZURE_OPENAI_API_KEY", "unit-test-placeholder");
        environment.Set("GEMINI_API_KEY", "unit-test-placeholder");
        environment.Set("QXNM_FORGE_MISTRAL_CONVERSATIONS_ENDPOINT", "http://127.0.0.1:41001/test");
        environment.Set("QXNM_FORGE_AZURE_OPENAI_RESPONSES_ENDPOINT", "http://127.0.0.1:41002/test");
        environment.Set("QXNM_FORGE_GOOGLE_GENERATIVE_AI_ENDPOINT", "http://127.0.0.1:41003/test");

        await using var registry = ProviderRegistryFactory.CreateFromEnvironment();
        Assert.Contains(registry.Providers, static provider => provider.Id == "mistral");
        Assert.Contains(registry.Providers, static provider => provider.Id == "google");
        Assert.DoesNotContain(registry.Providers, static provider => provider.Id == "azure-openai-responses");

        var mistral = Assert.Single(registry.Providers, static provider => provider.Id == "mistral");
        var mistralTarget = CreateRequestEndpoint(mistral, "codestral-latest");
        Assert.Equal("api.mistral.ai", mistralTarget.Host);
        Assert.Equal(Uri.UriSchemeHttps, mistralTarget.Scheme);
        var google = Assert.Single(registry.Providers, static provider => provider.Id == "google");
        var googleTarget = CreateRequestEndpoint(google, "gemini-2.5-flash");
        Assert.Equal("generativelanguage.googleapis.com", googleTarget.Host);
        Assert.Equal("/v1beta/models/gemini-2.5-flash:streamGenerateContent", googleTarget.AbsolutePath);
    }

    /// <summary>
    /// 功能：确认显式 Azure key/base 也不能绕过 ADR 0018 六条生产 route allowlist。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task AzureProductionRemainsDisabledOutsideRouteSpine()
    {
        using var environment = new EnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        environment.Set("AZURE_OPENAI_API_KEY", "unit-test-placeholder");
        environment.Set("AZURE_OPENAI_BASE_URL", "https://resource.example.invalid/openai/v1");
        environment.Set("AZURE_OPENAI_API_VERSION", "2025-04-01-preview");

        await using var registry = ProviderRegistryFactory.CreateFromEnvironment();
        Assert.DoesNotContain(
            registry.Providers,
            static provider => provider.Id == "azure-openai-responses");
        Assert.Equal(["faux"], registry.Advertisements.Select(static provider => provider.Id));
    }

    /// <summary>
    /// 功能：确认 conformance 只采用三类显式 literal loopback base，并忽略生产 Azure origin/version。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ConformanceUsesOnlyExplicitLoopbackOrigins()
    {
        using var environment = new EnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        environment.Set("QXNM_FORGE_PROVIDER_CONFORMANCE", "1");
        environment.Set("MISTRAL_API_KEY", "unit-test-placeholder");
        environment.Set("AZURE_OPENAI_API_KEY", "unit-test-placeholder");
        environment.Set("GEMINI_API_KEY", "unit-test-placeholder");
        environment.Set("AZURE_OPENAI_BASE_URL", "https://production.example.invalid/openai/v1");
        environment.Set("AZURE_OPENAI_API_VERSION", "production-version");
        environment.Set("QXNM_FORGE_MISTRAL_CONVERSATIONS_ENDPOINT", "http://127.0.0.1:42001/mistral");
        environment.Set("QXNM_FORGE_AZURE_OPENAI_RESPONSES_ENDPOINT", "http://127.0.0.1:42002/azure");
        environment.Set("QXNM_FORGE_GOOGLE_GENERATIVE_AI_ENDPOINT", "http://127.0.0.1:42003/google");

        await using var registry = ProviderRegistryFactory.CreateFromEnvironment();
        var cases = new[]
        {
            (ProviderId: "mistral", ModelId: "mock-mistral-v1", Port: 42001, Path: "/mistral/v1/chat/completions", Query: string.Empty),
            (ProviderId: "azure-openai-responses", ModelId: "mock-azure-v1", Port: 42002, Path: "/azure/responses", Query: "?api-version=v1"),
            (ProviderId: "google", ModelId: "mock-google-v1", Port: 42003, Path: "/google/models/mock-google-v1:streamGenerateContent", Query: "?alt=sse"),
        };
        foreach (var item in cases)
        {
            var provider = Assert.Single(registry.Providers, provider => provider.Id == item.ProviderId);
            var target = CreateRequestEndpoint(provider, item.ModelId);
            Assert.Equal(Uri.UriSchemeHttp, target.Scheme);
            Assert.Equal("127.0.0.1", target.Host);
            Assert.Equal(item.Port, target.Port);
            Assert.Equal(item.Path, target.AbsolutePath);
            Assert.Equal(item.Query, target.Query);
        }
    }

    /// <summary>
    /// 功能：确认第三批三个 family 仅在 conformance 的显式回环起点与合成凭据齐全时广告。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task BatchThreeConformanceUsesOnlyExplicitLoopbackOrigins()
    {
        using var environment = new EnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        environment.Set("QXNM_FORGE_PROVIDER_CONFORMANCE", "1");
        environment.Set("QXNM_FORGE_GOOGLE_VERTEX_ENDPOINT", "http://127.0.0.1:43001/vertex");
        environment.Set("QXNM_FORGE_GOOGLE_VERTEX_OAUTH_TOKEN", "unit-token");
        environment.Set("GOOGLE_CLOUD_PROJECT", "mock-project");
        environment.Set("GOOGLE_CLOUD_LOCATION", "us-central1");
        environment.Set("QXNM_FORGE_BEDROCK_CONVERSE_STREAM_ENDPOINT", "http://127.0.0.1:43002/bedrock");
        environment.Set("AWS_ACCESS_KEY_ID", "unit-access-key");
        environment.Set("AWS_SECRET_ACCESS_KEY", "unit-secret-key");
        environment.Set("AWS_SESSION_TOKEN", "unit-session-token");
        environment.Set("AWS_REGION", "us-east-1");
        environment.Set("QXNM_FORGE_OPENAI_CODEX_RESPONSES_ENDPOINT", "http://127.0.0.1:43003/codex");
        environment.Set("QXNM_FORGE_OPENAI_CODEX_OAUTH_TOKEN", "unit-token");

        await using var registry = ProviderRegistryFactory.CreateFromEnvironment();
        var cases = new[]
        {
            (
                ProviderId: "google-vertex",
                ModelId: "mock-vertex-v1",
                Port: 43001,
                Path: "/vertex/v1/projects/mock-project/locations/us-central1/publishers/google/models/mock-vertex-v1:streamGenerateContent",
                Query: "?alt=sse"),
            (
                ProviderId: "amazon-bedrock",
                ModelId: "mock-bedrock-v1",
                Port: 43002,
                Path: "/bedrock/model/mock-bedrock-v1/converse-stream",
                Query: string.Empty),
            (
                ProviderId: "openai-codex",
                ModelId: "mock-codex-v1",
                Port: 43003,
                Path: "/codex/codex/responses",
                Query: string.Empty),
        };
        foreach (var item in cases)
        {
            var provider = Assert.Single(registry.Providers, provider => provider.Id == item.ProviderId);
            var target = CreateRequestEndpoint(provider, item.ModelId);
            Assert.Equal(Uri.UriSchemeHttp, target.Scheme);
            Assert.Equal("127.0.0.1", target.Host);
            Assert.Equal(item.Port, target.Port);
            Assert.Equal(item.Path, target.AbsolutePath);
            Assert.Equal(item.Query, target.Query);
        }
    }

    /// <summary>
    /// 功能：确认 Vertex/Codex 的 conformance-only OAuth 环境不在生产模式注册任何 Provider。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ProductionIgnoresConformanceOnlyOAuthInjection()
    {
        using var environment = new EnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        environment.Set("QXNM_FORGE_GOOGLE_VERTEX_ENDPOINT", "http://127.0.0.1:44001/vertex");
        environment.Set("QXNM_FORGE_GOOGLE_VERTEX_OAUTH_TOKEN", "unit-token");
        environment.Set("GOOGLE_CLOUD_PROJECT", "mock-project");
        environment.Set("GOOGLE_CLOUD_LOCATION", "us-central1");
        environment.Set("QXNM_FORGE_OPENAI_CODEX_RESPONSES_ENDPOINT", "http://127.0.0.1:44002/codex");
        environment.Set("QXNM_FORGE_OPENAI_CODEX_OAUTH_TOKEN", "unit-token");

        await using var registry = ProviderRegistryFactory.CreateFromEnvironment();
        Assert.DoesNotContain(registry.Providers, static provider => provider.Id == "google-vertex");
        Assert.DoesNotContain(registry.Providers, static provider => provider.Id == "openai-codex");
    }

    /// <summary>
    /// 功能：确认 OpenRouter Images 生产固定 HTTPS origin，而 conformance 只接受显式 loopback base 与 key。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task OpenRouterImagesUsesFixedProductionAndExplicitConformanceOrigins()
    {
        using var environment = new EnvironmentScope(EnvironmentNames);
        environment.ClearAll();
        environment.Set("OPENROUTER_API_KEY", "unit-test-placeholder");
        environment.Set("QXNM_FORGE_OPENROUTER_IMAGES_ENDPOINT", "http://127.0.0.1:45001/case/test");

        await using (var production = ProviderRegistryFactory.CreateFromEnvironment())
        {
            var provider = Assert.Single(production.Providers, static item => item.Id == "openrouter");
            Assert.Equal("openrouter-images", provider.ApiFamily);
            var target = CreateRequestEndpoint(provider, "google/gemini-2.5-flash-image");
            Assert.Equal(Uri.UriSchemeHttps, target.Scheme);
            Assert.Equal("openrouter.ai", target.Host);
            Assert.Equal("/api/v1/chat/completions", target.AbsolutePath);
        }

        environment.Set("QXNM_FORGE_PROVIDER_CONFORMANCE", "1");
        await using var conformance = ProviderRegistryFactory.CreateFromEnvironment();
        var conformanceProvider = Assert.Single(
            conformance.Providers,
            static item => item.Id == "openrouter");
        var conformanceTarget = CreateRequestEndpoint(
            conformanceProvider,
            "google/gemini-2.5-flash-image");
        Assert.Equal("127.0.0.1", conformanceTarget.Host);
        Assert.Equal(45001, conformanceTarget.Port);
        Assert.Equal("/case/test/chat/completions", conformanceTarget.AbsolutePath);
    }

    /// <summary>
    /// 功能：通过受保护边界取得 adapter 的原生 request-target，且不发送网络请求。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="provider">已由环境 factory 构造的 Provider。</param>
    /// <param name="modelId">目标模型或 Azure deployment 名。</param>
    /// <returns>认证注入前将使用的绝对 URI。</returns>
    private static Uri CreateRequestEndpoint(IProvider provider, string modelId)
    {
        var method = provider.GetType().GetMethod(
            "CreateRequestEndpoint",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var request = new ProviderRequest(
            new ProviderSelection(provider.Id, modelId),
            Array.Empty<System.Text.Json.JsonElement>(),
            Array.Empty<ProviderToolDefinition>());
        return Assert.IsType<Uri>(method.Invoke(provider, [request]));
    }

    /// <summary>
    /// 功能：保存、临时覆盖并在释放时恢复一组进程环境变量。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> original;

        /// <summary>
        /// 功能：捕获代码内固定环境变量的原始进程值。
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
        /// 功能：清除本 scope 捕获的全部 Provider 与策略环境，避免继承真实配置。
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
        /// 功能：为单元测试设置一个非真实、受 scope 管理的进程环境值。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="name">必须属于构造时固定集合的变量名。</param>
        /// <param name="value">测试占位文本或 null。</param>
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
            foreach (var pair in original)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }
}
