using System.Globalization;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：在 daemon 启动最后边界从环境构造已配置原生 Provider registry。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public static class ProviderRegistryFactory
{
    /// <summary>
    /// 功能：最早处理 route/identity presence；否则区分 synthetic conformance 与六条 canonical 生产 route。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="fauxProvider">可选共享 faux 实例。</param>
    /// <param name="conformanceMode">daemon 是否显式启用通用 conformance 功能。</param>
    /// <param name="stateRoot">可选工作区外状态根；普通生产用于加载本地推广 route。</param>
    /// <param name="workspace">与 stateRoot 成对提供的 canonical workspace。</param>
    /// <param name="allowCustomProviderLoopback">是否已通过自定义 Provider loopback 的独立三门授权。</param>
    /// <returns>拥有全部原生 HTTP 资源的 registry。</returns>
    /// <remarks>presence 分支先于 transport policy、endpoint 与 credential 读取；生产 route 从冻结 manifest/catalog 同一快照生成广告与执行。</remarks>
    /// <exception cref="ProviderIdentityAdvertisementException">生产出现 presence 或 presence/snapshot 无效。</exception>
    /// <exception cref="ProviderExecutableRouteException">route presence 未获双开关授权，或 config/snapshot 无效。</exception>
    /// <exception cref="ArgumentException">普通 live endpoint 或 transport 环境配置不安全/越界。</exception>
    public static ProviderRegistry CreateFromEnvironment(
        FauxProvider? fauxProvider = null,
        bool conformanceMode = false,
        string? stateRoot = null,
        string? workspace = null,
        bool allowCustomProviderLoopback = false)
    {
        var routeConfiguration = Environment.GetEnvironmentVariable(
            ProviderExecutableRouteSnapshot.ConfigurationEnvironmentVariable);
        var identityConfiguration = Environment.GetEnvironmentVariable(
            ProviderIdentityAdvertisement.ConfigurationEnvironmentVariable);
        if (routeConfiguration is not null && identityConfiguration is not null)
        {
            throw new ProviderExecutableRouteException();
        }

        if (routeConfiguration is not null)
        {
            if (!conformanceMode || !ProviderConformanceEnabled())
            {
                throw new ProviderExecutableRouteException();
            }

            var routes = ProviderExecutableRouteSnapshot.Load(routeConfiguration);
            return CreateExecutableRegistry(
                fauxProvider ?? new FauxProvider(),
                routes,
                ReadTransportOptions());
        }

        if (identityConfiguration is not null)
        {
            if (!conformanceMode)
            {
                throw new ProviderIdentityAdvertisementException();
            }

            return new ProviderRegistry(
                [fauxProvider ?? new FauxProvider()],
                ProviderIdentityAdvertisement.Load(identityConfiguration));
        }

        var conformance = ProviderConformanceEnabled();
        if (!conformance)
        {
            return CreateExecutableRegistry(
                fauxProvider ?? new FauxProvider(),
                ProviderExecutableRouteSnapshot.Load(configurationPath: null),
                ReadTransportOptions(),
                stateRoot,
                workspace,
                allowCustomProviderLoopback);
        }

        var providers = new List<IProvider> { fauxProvider ?? new FauxProvider() };
        var customModels = new List<QxnmForge.Domain.ModelDescriptor>();
        var options = ReadTransportOptions();
        var openAiCredential = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var anthropicCredential = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var mistralCredential = Environment.GetEnvironmentVariable("MISTRAL_API_KEY");
        var azureCredential = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var googleCredential = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        var openRouterCredential = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");

        AddOpenAiChat(providers, options, conformance, openAiCredential);
        AddOpenAiResponses(providers, options, conformance, openAiCredential);
        AddAnthropic(providers, options, conformance, anthropicCredential);
        AddMistralConversations(providers, options, conformance, mistralCredential);
        AddAzureOpenAiResponses(providers, options, conformance, azureCredential);
        AddGoogleGenerativeAi(providers, options, conformance, googleCredential);
        AddGoogleVertex(providers, options, conformance);
        AddBedrockConverseStream(providers, options, conformance);
        AddOpenAiCodexResponses(providers, options, conformance);
        AddOpenRouterImages(providers, options, conformance, openRouterCredential);
        AddCustomProviderConnections(
            providers,
            customModels,
            options,
            stateRoot,
            workspace,
            allowCustomProviderLoopback);
        return new ProviderRegistry(providers, executableModels: customModels);
    }

    /// <summary>
    /// 功能：从同一 executable snapshot 注册当前 credential 可用的 route adapters 与 descriptors。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="fauxProvider">始终保留的离线 faux 实例。</param>
    /// <param name="routes">已完成生产或 conformance 收窄的不可变 route plans。</param>
    /// <param name="options">有界 HTTP/SSE/retry 策略。</param>
    /// <param name="stateRoot">可选本地推广商业状态根。</param>
    /// <param name="workspace">与 stateRoot 成对的 workspace 安全边界。</param>
    /// <param name="allowCustomProviderLoopback">是否允许自定义连接使用 literal loopback HTTP。</param>
    /// <returns>route-keyed adapters 与同一模型快照闭合的 registry。</returns>
    /// <remarks>不变量：缺 credential route 在 initialize/models/list 消失；credential 值不传入 adapter 构造器。</remarks>
    private static ProviderRegistry CreateExecutableRegistry(
        FauxProvider fauxProvider,
        IReadOnlyList<ProviderExecutableRoutePlan> routes,
        ProviderTransportOptions options,
        string? stateRoot = null,
        string? workspace = null,
        bool allowCustomProviderLoopback = false)
    {
        var providers = new List<IProvider> { fauxProvider };
        var models = new List<QxnmForge.Domain.ModelDescriptor>();
        foreach (var route in routes)
        {
            if (!CredentialIsPresent(route.CredentialEnvironmentName))
            {
                continue;
            }

            providers.Add(CreateExecutableAdapter(route, options));
            models.AddRange(route.Models);
        }

        AddSponsoredRoutes(providers, models, options, stateRoot, workspace);
        AddCustomProviderConnections(
            providers,
            models,
            options,
            stateRoot,
            workspace,
            allowCustomProviderLoopback);

        return new ProviderRegistry(providers, executableModels: models);
    }

    /// <summary>
    /// 功能：从同一启动快照注册已启用且 credential configured 的自定义 OpenAI Chat 连接。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providers">目标原生 Provider 列表。</param>
    /// <param name="models">与 adapter 一一闭合的模型描述列表。</param>
    /// <param name="options">有界 HTTP/SSE 策略。</param>
    /// <param name="stateRoot">可信应用状态根。</param>
    /// <param name="workspace">CredentialStore 必须位于其外的 workspace。</param>
    /// <param name="allowCustomProviderLoopback">是否允许持久化连接使用 literal loopback HTTP。</param>
    /// <remarks>不变量：未启用或缺 credential 的连接不进入 initialize、models/list 或 adapter registry。</remarks>
    private static void AddCustomProviderConnections(
        List<IProvider> providers,
        List<QxnmForge.Domain.ModelDescriptor> models,
        ProviderTransportOptions options,
        string? stateRoot,
        string? workspace,
        bool allowCustomProviderLoopback)
    {
        if (stateRoot is null || workspace is null)
        {
            return;
        }

        var credentials = new ProviderCredentialStore(stateRoot, workspace);
        var configured = new HashSet<string>(credentials.List(), StringComparer.Ordinal);
        var connections = new CustomProviderConnectionStore(
            stateRoot,
            allowCustomProviderLoopback).List();
        var occupiedProviderIds = new HashSet<string>(
            providers.Select(static provider => provider.Id),
            StringComparer.Ordinal);
        foreach (var connection in connections)
        {
            if (!connection.Enabled ||
                connection.ModelIds.Count == 0 ||
                !configured.Contains(connection.ProviderId) ||
                !occupiedProviderIds.Add(connection.ProviderId))
            {
                continue;
            }

            providers.Add(CustomProviderConnectionAdapterFactory.Create(connection, credentials, options));
            models.AddRange(
                CustomProviderConnectionAdapterFactory.CreateModelDescriptors(connection));
        }
    }

    /// <summary>
    /// 功能：把用户明确安装的推广 routes 接入现有 family adapters 与模型快照。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providers">目标原生 Provider 列表。</param>
    /// <param name="models">与当前 credential 可用 route 同源的模型 descriptors。</param>
    /// <param name="options">有界 HTTP/SSE 策略。</param>
    /// <param name="stateRoot">工作区外状态根。</param>
    /// <param name="workspace">当前 workspace。</param>
    /// <remarks>不变量：adapter 只持有 store 路径/Provider ID；缺 credential route 不广告且接受前失败关闭。</remarks>
    private static void AddSponsoredRoutes(
        List<IProvider> providers,
        List<QxnmForge.Domain.ModelDescriptor> models,
        ProviderTransportOptions options,
        string? stateRoot,
        string? workspace)
    {
        if (stateRoot is null || workspace is null)
        {
            return;
        }

        var credentials = new ProviderCredentialStore(stateRoot, workspace);
        var routeStore = new InstalledSponsoredRouteStore(stateRoot);
        foreach (var route in routeStore.List())
        {
            providers.Add(SponsoredProviderAdapterFactory.Create(route, credentials, options));
            if (credentials.TryReadForRequest(route.ProviderId, out _))
            {
                models.AddRange(InstalledSponsoredRouteStore.CreateModelDescriptors(route));
            }
        }
    }

    /// <summary>
    /// 功能：按 manifest 固定 adapter ID 创建六条 canonical family adapter 之一。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="route">严格验证后的 route plan。</param>
    /// <param name="options">公共有界 transport 策略。</param>
    /// <returns>只保存 credential 环境名称且绑定 catalog allowlist 的 adapter。</returns>
    /// <exception cref="ProviderExecutableRouteException">snapshot 出现不属于 ADR 0018 的 adapter/route 组合。</exception>
    private static IProvider CreateExecutableAdapter(
        ProviderExecutableRoutePlan route,
        ProviderTransportOptions options)
    {
        return (route.ProviderId, route.ApiFamily, route.AdapterId) switch
        {
            ("groq", "openai-completions", "openai-completions-v1") =>
                new GroqOpenAiCompletionsProvider(route, options),
            ("minimax", "anthropic-messages", "anthropic-messages-v1") =>
                new MiniMaxAnthropicMessagesProvider(route, options),
            ("mistral", "mistral-conversations", "mistral-conversations-v1") =>
                new MistralConversationsProvider(
                    route.EndpointBase,
                    ProviderCredentialSource.FromEnvironment(route.CredentialEnvironmentName),
                    route.Models.Select(static model => model.ModelId).ToArray(),
                    options),
            ("openai", "openai-responses", "openai-responses-v1") =>
                new CanonicalOpenAiResponsesProvider(route, options),
            ("google", "google-generative-ai", "google-generative-ai-v1") =>
                new CanonicalGoogleGenerativeAiProvider(route, options),
            ("openrouter", "openrouter-images", "openrouter-images-v1") =>
                new OpenRouterImagesProvider(
                    route.EndpointBase,
                    ProviderCredentialSource.FromEnvironment(route.CredentialEnvironmentName),
                    route.Models.Select(static model => model.ModelId).ToArray(),
                    options),
            _ => throw new ProviderExecutableRouteException(),
        };
    }

    /// <summary>
    /// 功能：检查固定环境 credential 当前存在且能安全进入单个 HTTP header，但不保留其值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="environmentName">冻结 manifest 提供的唯一 credential 环境名称。</param>
    /// <returns>值非空、长度有界且不含 CR/LF 时为 true。</returns>
    /// <remarks>不变量：值只存在于本次栈局部；adapter 构造仅接收环境名称并在最终请求边界重读。</remarks>
    private static bool CredentialIsPresent(string environmentName)
    {
        var credential = Environment.GetEnvironmentVariable(environmentName);
        return !string.IsNullOrEmpty(credential) &&
            credential.Length <= 16_384 &&
            !credential.Contains('\r', StringComparison.Ordinal) &&
            !credential.Contains('\n', StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：计算自定义 Provider literal loopback HTTP 的三重 conformance 授权。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="cliConformance">CLI 是否显式携带 `--conformance`。</param>
    /// <returns>CLI、`QXNM_FORGE_CONFORMANCE=1` 与 `QXNM_FORGE_PROVIDER_CONFORMANCE=1` 同时满足时 true。</returns>
    /// <remarks>不变量：任一门缺失或值不精确为 `1` 时均失败关闭；此结果只授权 literal loopback HTTP。</remarks>
    public static bool IsCustomProviderLoopbackConformanceEnabled(bool cliConformance)
    {
        return cliConformance &&
            string.Equals(
                Environment.GetEnvironmentVariable("QXNM_FORGE_CONFORMANCE"),
                "1",
                StringComparison.Ordinal) &&
            ProviderConformanceEnabled();
    }

    /// <summary>
    /// 功能：判断旧 synthetic family 或新 route runner 是否显式启用 Provider conformance。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>QXNM_FORGE_PROVIDER_CONFORMANCE 精确为 `1` 时为 true。</returns>
    private static bool ProviderConformanceEnabled()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("QXNM_FORGE_PROVIDER_CONFORMANCE"),
            "1",
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：按 OpenAI-compatible endpoint/key 配置添加 Chat Completions adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providers">目标注册列表。</param>
    /// <param name="options">传输策略。</param>
    /// <param name="conformance">是否限制为显式 loopback endpoint。</param>
    /// <param name="credential">进程环境 API key，仅在构造 adapter 时传入。</param>
    private static void AddOpenAiChat(
        List<IProvider> providers,
        ProviderTransportOptions options,
        bool conformance,
        string? credential)
    {
        const string variable = "QXNM_FORGE_OPENAI_COMPATIBLE_ENDPOINT";
        var configuredEndpoint = Environment.GetEnvironmentVariable(variable);
        if ((conformance && string.IsNullOrEmpty(configuredEndpoint)) ||
            (!conformance && string.IsNullOrEmpty(configuredEndpoint) && string.IsNullOrEmpty(credential)))
        {
            return;
        }

        var endpoint = ParseEndpoint(
            configuredEndpoint ?? "https://api.openai.com/v1/chat/completions",
            conformance);
        providers.Add(new OpenAiChatProvider(endpoint, credential, options));
    }

    /// <summary>
    /// 功能：按 Responses endpoint/key 配置添加 OpenAI Responses adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providers">目标注册列表。</param>
    /// <param name="options">传输策略。</param>
    /// <param name="conformance">是否限制为显式 loopback endpoint。</param>
    /// <param name="credential">进程环境 API key。</param>
    private static void AddOpenAiResponses(
        List<IProvider> providers,
        ProviderTransportOptions options,
        bool conformance,
        string? credential)
    {
        const string variable = "QXNM_FORGE_OPENAI_RESPONSES_ENDPOINT";
        var configuredEndpoint = Environment.GetEnvironmentVariable(variable);
        if ((conformance && string.IsNullOrEmpty(configuredEndpoint)) ||
            (!conformance && string.IsNullOrEmpty(configuredEndpoint) && string.IsNullOrEmpty(credential)))
        {
            return;
        }

        var endpoint = ParseEndpoint(
            configuredEndpoint ?? "https://api.openai.com/v1/responses",
            conformance);
        providers.Add(new OpenAiResponsesProvider(endpoint, credential, options));
    }

    /// <summary>
    /// 功能：按 Anthropic endpoint/key 配置添加 Messages adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providers">目标注册列表。</param>
    /// <param name="options">传输策略。</param>
    /// <param name="conformance">是否限制为显式 loopback endpoint。</param>
    /// <param name="credential">进程环境 Anthropic key。</param>
    private static void AddAnthropic(
        List<IProvider> providers,
        ProviderTransportOptions options,
        bool conformance,
        string? credential)
    {
        const string variable = "QXNM_FORGE_ANTHROPIC_ENDPOINT";
        var configuredEndpoint = Environment.GetEnvironmentVariable(variable);
        if ((conformance && string.IsNullOrEmpty(configuredEndpoint)) ||
            (!conformance && string.IsNullOrEmpty(configuredEndpoint) && string.IsNullOrEmpty(credential)))
        {
            return;
        }

        var endpoint = ParseEndpoint(
            configuredEndpoint ?? "https://api.anthropic.com/v1/messages",
            conformance);
        providers.Add(new AnthropicMessagesProvider(endpoint, credential, options));
    }

    /// <summary>
    /// 功能：按固定生产 origin 或显式 conformance base 添加 Mistral Conversations adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providers">目标注册列表。</param>
    /// <param name="options">有界传输策略。</param>
    /// <param name="conformance">是否隔离到专用 literal loopback endpoint。</param>
    /// <param name="credential">进程环境 Mistral API key。</param>
    /// <remarks>不变量：生产忽略测试 endpoint；conformance 不回退到生产 origin。</remarks>
    private static void AddMistralConversations(
        List<IProvider> providers,
        ProviderTransportOptions options,
        bool conformance,
        string? credential)
    {
        if (string.IsNullOrEmpty(credential))
        {
            return;
        }

        var configuredEndpoint = Environment.GetEnvironmentVariable("QXNM_FORGE_MISTRAL_CONVERSATIONS_ENDPOINT");
        if (conformance && string.IsNullOrEmpty(configuredEndpoint))
        {
            return;
        }

        var baseEndpoint = ParseEndpoint(
            conformance ? configuredEndpoint! : "https://api.mistral.ai",
            conformance);
        providers.Add(new MistralConversationsProvider(baseEndpoint, credential, options));
    }

    /// <summary>
    /// 功能：只在 key 与显式 runtime base 同时存在时添加 Azure OpenAI Responses adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providers">目标注册列表。</param>
    /// <param name="options">有界传输策略。</param>
    /// <param name="conformance">是否隔离到专用 literal loopback base 和固定 v1。</param>
    /// <param name="credential">进程环境 Azure API key。</param>
    /// <remarks>不变量：生产永不猜测 Azure resource；conformance 忽略生产 base/version。</remarks>
    private static void AddAzureOpenAiResponses(
        List<IProvider> providers,
        ProviderTransportOptions options,
        bool conformance,
        string? credential)
    {
        if (string.IsNullOrEmpty(credential))
        {
            return;
        }

        var configuredEndpoint = Environment.GetEnvironmentVariable(
            conformance
                ? "QXNM_FORGE_AZURE_OPENAI_RESPONSES_ENDPOINT"
                : "AZURE_OPENAI_BASE_URL");
        if (string.IsNullOrEmpty(configuredEndpoint))
        {
            return;
        }

        var apiVersion = conformance
            ? "v1"
            : Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION") ?? "v1";
        if (!NativeProviderEndpoint.IsSafeApiVersion(apiVersion))
        {
            throw new ArgumentException("Azure OpenAI API version is invalid");
        }

        var baseEndpoint = ParseEndpoint(configuredEndpoint, conformance);
        providers.Add(new AzureOpenAiResponsesProvider(baseEndpoint, apiVersion, credential, options));
    }

    /// <summary>
    /// 功能：按固定 `/v1beta` 生产 origin 或显式 conformance base 添加 Google Generative AI adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providers">目标注册列表。</param>
    /// <param name="options">有界传输策略。</param>
    /// <param name="conformance">是否隔离到专用 literal loopback endpoint。</param>
    /// <param name="credential">进程环境 Gemini API key。</param>
    /// <remarks>不变量：生产忽略测试 endpoint；conformance 不回退到真实 Google origin。</remarks>
    private static void AddGoogleGenerativeAi(
        List<IProvider> providers,
        ProviderTransportOptions options,
        bool conformance,
        string? credential)
    {
        if (string.IsNullOrEmpty(credential))
        {
            return;
        }

        var configuredEndpoint = Environment.GetEnvironmentVariable("QXNM_FORGE_GOOGLE_GENERATIVE_AI_ENDPOINT");
        if (conformance && string.IsNullOrEmpty(configuredEndpoint))
        {
            return;
        }

        var baseEndpoint = ParseEndpoint(
            conformance
                ? configuredEndpoint!
                : "https://generativelanguage.googleapis.com/v1beta",
            conformance);
        providers.Add(new GoogleGenerativeAiProvider(baseEndpoint, credential, options));
    }

    /// <summary>
    /// 功能：仅在明确 conformance 回环 endpoint、合成 OAuth token 与 project/location 齐全时注册 Vertex。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providers">目标注册列表。</param>
    /// <param name="options">有界传输策略。</param>
    /// <param name="conformance">是否由公共 runner 显式启用回环注入。</param>
    /// <remarks>不变量：测试 OAuth 环境名绝不在生产模式使用；尚未以它伪装 ADC 流程。</remarks>
    private static void AddGoogleVertex(
        List<IProvider> providers,
        ProviderTransportOptions options,
        bool conformance)
    {
        if (!conformance)
        {
            return;
        }

        var configuredEndpoint = Environment.GetEnvironmentVariable("QXNM_FORGE_GOOGLE_VERTEX_ENDPOINT");
        var token = Environment.GetEnvironmentVariable("QXNM_FORGE_GOOGLE_VERTEX_OAUTH_TOKEN");
        var project = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT")
            ?? Environment.GetEnvironmentVariable("GCLOUD_PROJECT");
        var location = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_LOCATION");
        if (string.IsNullOrEmpty(configuredEndpoint) ||
            string.IsNullOrEmpty(token) ||
            string.IsNullOrEmpty(project) ||
            string.IsNullOrEmpty(location))
        {
            return;
        }

        providers.Add(new GoogleVertexProvider(
            ParseEndpoint(configuredEndpoint, conformance: true),
            project,
            location,
            token,
            options));
    }

    /// <summary>
    /// 功能：按静态 AWS 环境凭据与 region 注册原生 Bedrock ConverseStream adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providers">目标注册列表。</param>
    /// <param name="options">有界传输策略。</param>
    /// <param name="conformance">是否必须使用 runner 提供的 literal loopback base。</param>
    /// <remarks>不变量：凭据不完整时不广告；生产 endpoint 只从已验证 region 派生 HTTPS origin。</remarks>
    private static void AddBedrockConverseStream(
        List<IProvider> providers,
        ProviderTransportOptions options,
        bool conformance)
    {
        var accessKeyId = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        var secretAccessKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        var sessionToken = Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN");
        var region = Environment.GetEnvironmentVariable("AWS_REGION")
            ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");
        var configuredEndpoint = Environment.GetEnvironmentVariable("QXNM_FORGE_BEDROCK_CONVERSE_STREAM_ENDPOINT");
        if (string.IsNullOrEmpty(accessKeyId) ||
            string.IsNullOrEmpty(secretAccessKey) ||
            string.IsNullOrEmpty(region) ||
            (conformance && string.IsNullOrEmpty(configuredEndpoint)))
        {
            return;
        }

        if (!NativeProviderEndpoint.IsSafeResourceSegment(region))
        {
            throw new ArgumentException("AWS region is invalid");
        }

        var endpoint = ParseEndpoint(
            conformance
                ? configuredEndpoint!
                : $"https://bedrock-runtime.{region}.amazonaws.com",
            conformance);
        providers.Add(new BedrockConverseStreamProvider(
            endpoint,
            accessKeyId,
            secretAccessKey,
            sessionToken,
            region,
            options));
    }

    /// <summary>
    /// 功能：仅在公共 conformance 显式注入回环 endpoint 和合成 OAuth token 时注册 Codex Responses。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providers">目标注册列表。</param>
    /// <param name="options">有界传输策略。</param>
    /// <param name="conformance">是否处于显式 Provider conformance 模式。</param>
    /// <remarks>不变量：合成 token 环境名不是生产 OAuth 存储接口，生产模式不使用它。</remarks>
    private static void AddOpenAiCodexResponses(
        List<IProvider> providers,
        ProviderTransportOptions options,
        bool conformance)
    {
        if (!conformance)
        {
            return;
        }

        var configuredEndpoint = Environment.GetEnvironmentVariable("QXNM_FORGE_OPENAI_CODEX_RESPONSES_ENDPOINT");
        var token = Environment.GetEnvironmentVariable("QXNM_FORGE_OPENAI_CODEX_OAUTH_TOKEN");
        if (string.IsNullOrEmpty(configuredEndpoint) || string.IsNullOrEmpty(token))
        {
            return;
        }

        providers.Add(new OpenAiCodexResponsesProvider(
            ParseEndpoint(configuredEndpoint, conformance: true),
            token,
            options));
    }

    /// <summary>
    /// 功能：按固定生产 origin 或显式 conformance loopback base 注册 OpenRouter Images family。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providers">目标原生 Provider 列表。</param>
    /// <param name="options">公共有界 HTTP/retry 策略。</param>
    /// <param name="conformance">是否仅允许 runner 提供的 literal 127.0.0.1 base。</param>
    /// <param name="credential">OPENROUTER_API_KEY；只传入 adapter 内存认证边界。</param>
    /// <remarks>不变量：生产忽略测试 endpoint 并固定 HTTPS；缺 key 时不广告。</remarks>
    private static void AddOpenRouterImages(
        List<IProvider> providers,
        ProviderTransportOptions options,
        bool conformance,
        string? credential)
    {
        var configuredEndpoint = Environment.GetEnvironmentVariable("QXNM_FORGE_OPENROUTER_IMAGES_ENDPOINT");
        if (string.IsNullOrEmpty(credential) || (conformance && string.IsNullOrEmpty(configuredEndpoint)))
        {
            return;
        }

        var endpoint = ParseEndpoint(
            conformance ? configuredEndpoint! : "https://openrouter.ai/api/v1",
            conformance);
        providers.Add(new OpenRouterImagesProvider(endpoint, credential, options));
    }

    /// <summary>
    /// 功能：验证 endpoint 无 user-info/query/fragment，并执行 production HTTPS 或 conformance loopback 策略。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">环境 endpoint 文本；失败诊断不会回显。</param>
    /// <param name="conformance">是否只允许 http://127.0.0.1:port。</param>
    /// <returns>安全绝对 URI。</returns>
    private static Uri ParseEndpoint(string value, bool conformance)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException("provider endpoint is invalid");
        }

        if (conformance)
        {
            if (endpoint.Scheme != Uri.UriSchemeHttp ||
                endpoint.Host != "127.0.0.1" ||
                endpoint.Port is <= 0 or > 65535)
            {
                throw new ArgumentException("provider conformance endpoint is not exact IPv4 loopback");
            }
        }
        else if (endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("live provider endpoint must use HTTPS");
        }

        return endpoint;
    }

    /// <summary>
    /// 功能：读取公共 test-only timeout/retry 环境并应用严格十进制范围。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>通过 Validate 的传输策略。</returns>
    private static ProviderTransportOptions ReadTransportOptions()
    {
        var defaults = ProviderTransportOptions.Default;
        var connect = ReadMilliseconds("QXNM_FORGE_PROVIDER_CONNECT_TIMEOUT_MS", defaults.ConnectTimeout, allowZero: false);
        var idle = ReadMilliseconds("QXNM_FORGE_PROVIDER_IDLE_TIMEOUT_MS", defaults.IdleTimeout, allowZero: false);
        var total = ReadMilliseconds("QXNM_FORGE_PROVIDER_TOTAL_TIMEOUT_MS", defaults.TotalTimeout, allowZero: false);
        var retryDelay = ReadMilliseconds(
            "QXNM_FORGE_PROVIDER_RETRY_MAX_DELAY_MS",
            defaults.RetryMaxDelay,
            allowZero: true);
        var attempts = ReadInteger("QXNM_FORGE_PROVIDER_MAX_ATTEMPTS", defaults.MaxAttempts, 1, 100);
        return new ProviderTransportOptions(
            connect,
            connect,
            idle,
            total,
            attempts,
            retryDelay,
            defaults.MaxSseEventBytes);
    }

    /// <summary>
    /// 功能：读取毫秒环境值，缺失时使用默认，且不在错误中回显原值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="name">固定非 secret 环境名。</param>
    /// <param name="fallback">缺失时默认值。</param>
    /// <param name="allowZero">是否允许零延迟。</param>
    /// <returns>有界 TimeSpan。</returns>
    private static TimeSpan ReadMilliseconds(string name, TimeSpan fallback, bool allowZero)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        var minimum = allowZero ? 0 : 1;
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) ||
            milliseconds < minimum ||
            milliseconds > 3_600_000)
        {
            throw new ArgumentException("provider timeout environment is invalid");
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>
    /// 功能：读取十进制整数环境值并应用闭区间限制。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="name">固定非 secret 环境名。</param>
    /// <param name="fallback">缺失时默认值。</param>
    /// <param name="minimum">最小值。</param>
    /// <param name="maximum">最大值。</param>
    /// <returns>有界整数。</returns>
    private static int ReadInteger(string name, int fallback, int minimum, int maximum)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value < minimum ||
            value > maximum)
        {
            throw new ArgumentException("provider attempt environment is invalid");
        }

        return value;
    }
}
