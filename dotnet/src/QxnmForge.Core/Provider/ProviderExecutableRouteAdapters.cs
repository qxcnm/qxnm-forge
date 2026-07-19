using QxnmForge.Domain;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：把 canonical Groq route 绑定到共享 OpenAI Chat family parser 与冻结 request target。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class GroqOpenAiCompletionsProvider : OpenAiChatProvider
{
    private readonly Uri baseEndpoint;

    /// <summary>
    /// 功能：创建 groq/openai-completions catalog-bound adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="route">已验证且收窄完成的 canonical route plan。</param>
    /// <param name="options">有界 HTTP/SSE/retry 策略。</param>
    /// <remarks>不变量：credential 只从 route 固定环境名称在请求边界读取。</remarks>
    internal GroqOpenAiCompletionsProvider(
        ProviderExecutableRoutePlan route,
        ProviderTransportOptions options)
        : base(
            route.ProviderId,
            route.EndpointBase,
            ProviderCredentialSource.FromEnvironment(route.CredentialEnvironmentName),
            route.ApiFamily,
            route.Models.Select(static model => model.ModelId).ToArray(),
            options)
    {
        baseEndpoint = route.EndpointBase;
    }

    /// <summary>
    /// 功能：在 catalog base 上同源追加 Groq 原生 `/chat/completions` target。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">已通过 route/model allowlist 的请求。</param>
    /// <returns>scheme、host、effective port 不变的绝对 URI。</returns>
    protected override Uri CreateRequestEndpoint(ProviderRequest request)
    {
        return NativeProviderEndpoint.Append(baseEndpoint, "/chat/completions");
    }
}

/// <summary>
/// 功能：把 canonical MiniMax route 绑定到共享 Anthropic Messages family parser。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class MiniMaxAnthropicMessagesProvider : AnthropicMessagesProvider
{
    private readonly Uri baseEndpoint;

    /// <summary>
    /// 功能：创建 minimax/anthropic-messages catalog-bound adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="route">已验证且收窄完成的 canonical route plan。</param>
    /// <param name="options">有界 HTTP/SSE/retry 策略。</param>
    /// <remarks>不变量：固定 x-api-key 与 anthropic-version，credential 值不进入对象字段。</remarks>
    internal MiniMaxAnthropicMessagesProvider(
        ProviderExecutableRoutePlan route,
        ProviderTransportOptions options)
        : base(
            route.ProviderId,
            route.EndpointBase,
            ProviderCredentialSource.FromEnvironment(route.CredentialEnvironmentName),
            route.ApiFamily,
            route.Models.Select(static model => model.ModelId).ToArray(),
            options)
    {
        baseEndpoint = route.EndpointBase;
    }

    /// <summary>
    /// 功能：在 catalog base 上同源追加 MiniMax 原生 `/v1/messages` target。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">已通过 route/model allowlist 的请求。</param>
    /// <returns>scheme、host、effective port 不变的绝对 URI。</returns>
    protected override Uri CreateRequestEndpoint(ProviderRequest request)
    {
        return NativeProviderEndpoint.Append(baseEndpoint, "/v1/messages");
    }
}

/// <summary>
/// 功能：把 canonical OpenAI route 绑定到共享 Responses family parser 与冻结 target。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class CanonicalOpenAiResponsesProvider : OpenAiResponsesProvider
{
    private readonly Uri baseEndpoint;

    /// <summary>
    /// 功能：创建 openai/openai-responses catalog-bound adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="route">已验证且收窄完成的 canonical route plan。</param>
    /// <param name="options">有界 HTTP/SSE/retry 策略。</param>
    /// <remarks>不变量：credential 只从 OPENAI_API_KEY 在最终请求 header 边界读取。</remarks>
    internal CanonicalOpenAiResponsesProvider(
        ProviderExecutableRoutePlan route,
        ProviderTransportOptions options)
        : base(
            route.ProviderId,
            route.EndpointBase,
            ProviderCredentialSource.FromEnvironment(route.CredentialEnvironmentName),
            route.ApiFamily,
            route.Models.Select(static model => model.ModelId).ToArray(),
            options)
    {
        baseEndpoint = route.EndpointBase;
    }

    /// <summary>
    /// 功能：在 catalog base 上同源追加 OpenAI 原生 `/responses` target。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">已通过 route/model allowlist 的请求。</param>
    /// <returns>scheme、host、effective port 不变的绝对 URI。</returns>
    protected override Uri CreateRequestEndpoint(ProviderRequest request)
    {
        return NativeProviderEndpoint.Append(baseEndpoint, "/responses");
    }
}

/// <summary>
/// 功能：把 canonical Google route 绑定到共享 GenerateContent family parser 与 catalog allowlist。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class CanonicalGoogleGenerativeAiProvider : GoogleGenerativeAiProvider
{
    /// <summary>
    /// 功能：创建 google/google-generative-ai catalog-bound adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="route">已验证且收窄完成的 canonical route plan。</param>
    /// <param name="options">有界 HTTP/SSE/retry 策略。</param>
    /// <remarks>不变量：模型既命中 snapshot allowlist，又只能进入单个安全 path segment。</remarks>
    internal CanonicalGoogleGenerativeAiProvider(
        ProviderExecutableRoutePlan route,
        ProviderTransportOptions options)
        : base(
            route.ProviderId,
            route.EndpointBase,
            ProviderCredentialSource.FromEnvironment(route.CredentialEnvironmentName),
            route.ApiFamily,
            route.Models.Select(static model => model.ModelId).ToArray(),
            options)
    {
    }
}
