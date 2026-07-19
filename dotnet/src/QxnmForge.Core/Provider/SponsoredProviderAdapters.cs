using QxnmForge.Domain;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：把本地推广 route 绑定到共享 OpenAI Chat request/SSE parser。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class SponsoredOpenAiCompletionsProvider : OpenAiChatProvider
{
    private readonly Uri baseEndpoint;

    /// <summary>
    /// 功能：创建只按请求读取 stored credential 的推广 Chat adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="route">用户明确安装的非敏感 route。</param>
    /// <param name="store">工作区外 CredentialStore。</param>
    /// <param name="options">有界 transport 策略。</param>
    internal SponsoredOpenAiCompletionsProvider(
        InstalledSponsoredRoute route,
        ProviderCredentialStore store,
        ProviderTransportOptions options)
        : base(
            route.ProviderId,
            new Uri(route.ApiBaseUrl, UriKind.Absolute),
            ProviderCredentialSource.FromStore(store, route.ProviderId),
            route.ApiFamily,
            route.Models,
            options)
    {
        baseEndpoint = new Uri(route.ApiBaseUrl, UriKind.Absolute);
    }

    /// <summary>
    /// 功能：在安装时固定 base 后同源追加 `/chat/completions`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">已通过 route/model allowlist 的请求。</param>
    /// <returns>保持 scheme/host/port 的完整 endpoint。</returns>
    protected override Uri CreateRequestEndpoint(ProviderRequest request)
    {
        return NativeProviderEndpoint.Append(baseEndpoint, "/chat/completions");
    }
}

/// <summary>
/// 功能：把本地推广 route 绑定到共享 OpenAI Responses request/SSE parser。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class SponsoredOpenAiResponsesProvider : OpenAiResponsesProvider
{
    private readonly Uri baseEndpoint;

    /// <summary>
    /// 功能：创建只按请求读取 stored credential 的推广 Responses adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="route">用户明确安装的非敏感 route。</param>
    /// <param name="store">工作区外 CredentialStore。</param>
    /// <param name="options">有界 transport 策略。</param>
    internal SponsoredOpenAiResponsesProvider(
        InstalledSponsoredRoute route,
        ProviderCredentialStore store,
        ProviderTransportOptions options)
        : base(
            route.ProviderId,
            new Uri(route.ApiBaseUrl, UriKind.Absolute),
            ProviderCredentialSource.FromStore(store, route.ProviderId),
            route.ApiFamily,
            route.Models,
            options)
    {
        baseEndpoint = new Uri(route.ApiBaseUrl, UriKind.Absolute);
    }

    /// <summary>
    /// 功能：在安装时固定 base 后同源追加 `/responses`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">已通过 route/model allowlist 的请求。</param>
    /// <returns>保持 scheme/host/port 的完整 endpoint。</returns>
    protected override Uri CreateRequestEndpoint(ProviderRequest request)
    {
        return NativeProviderEndpoint.Append(baseEndpoint, "/responses");
    }
}

/// <summary>
/// 功能：把本地推广 route 绑定到共享 Anthropic Messages request/SSE parser。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class SponsoredAnthropicMessagesProvider : AnthropicMessagesProvider
{
    private readonly Uri baseEndpoint;

    /// <summary>
    /// 功能：创建只按请求读取 stored credential 的推广 Messages adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="route">用户明确安装的非敏感 route。</param>
    /// <param name="store">工作区外 CredentialStore。</param>
    /// <param name="options">有界 transport 策略。</param>
    internal SponsoredAnthropicMessagesProvider(
        InstalledSponsoredRoute route,
        ProviderCredentialStore store,
        ProviderTransportOptions options)
        : base(
            route.ProviderId,
            new Uri(route.ApiBaseUrl, UriKind.Absolute),
            ProviderCredentialSource.FromStore(store, route.ProviderId),
            route.ApiFamily,
            route.Models,
            options)
    {
        baseEndpoint = new Uri(route.ApiBaseUrl, UriKind.Absolute);
    }

    /// <summary>
    /// 功能：在安装时固定 base 后同源追加 `/messages`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">已通过 route/model allowlist 的请求。</param>
    /// <returns>保持 scheme/host/port 的完整 endpoint。</returns>
    protected override Uri CreateRequestEndpoint(ProviderRequest request)
    {
        return NativeProviderEndpoint.Append(baseEndpoint, "/messages");
    }
}

/// <summary>
/// 功能：按本地推广 route family 创建共享 parser adapter 和模型 descriptors。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal static class SponsoredProviderAdapterFactory
{
    /// <summary>
    /// 功能：为一条严格安装 route 创建唯一允许的 family adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="route">已验证非敏感 route。</param>
    /// <param name="store">工作区外 CredentialStore。</param>
    /// <param name="options">有界 HTTP/SSE 策略。</param>
    /// <returns>不持有 secret 的 Provider。</returns>
    /// <exception cref="ProviderCommercialStateException">family 不属于 v0.1 allowlist。</exception>
    internal static IProvider Create(
        InstalledSponsoredRoute route,
        ProviderCredentialStore store,
        ProviderTransportOptions options)
    {
        InstalledSponsoredRouteStore.ValidateRoute(route);
        return route.ApiFamily switch
        {
            "openai-completions" => new SponsoredOpenAiCompletionsProvider(route, store, options),
            "openai-responses" => new SponsoredOpenAiResponsesProvider(route, store, options),
            "anthropic-messages" => new SponsoredAnthropicMessagesProvider(route, store, options),
            _ => throw new ProviderCommercialStateException(
                "route_family",
                "已安装推广 route family 无效"),
        };
    }
}
