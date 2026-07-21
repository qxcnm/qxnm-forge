using System.Text.Json;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：原生实现 Mistral Conversations request-target、工具请求体与 Chat SSE 归一化。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class MistralConversationsProvider : OpenAiChatProvider
{
    private readonly Uri baseEndpoint;

    /// <summary>
    /// 功能：创建固定追加 `/v1/chat/completions` 且最终注入 Bearer credential 的 Mistral adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="baseEndpoint">生产固定 base 或显式 conformance loopback base。</param>
    /// <param name="credential">可选 Mistral API key。</param>
    /// <param name="options">有界 HTTP/SSE/retry 策略。</param>
    /// <remarks>不变量：base 只用于同源追加原生路径；不发送 OpenAI stream_options。</remarks>
    /// <exception cref="ArgumentException">base 或 transport 配置不安全。</exception>
    public MistralConversationsProvider(
        Uri baseEndpoint,
        string? credential,
        ProviderTransportOptions options)
        : base("mistral", baseEndpoint, credential, options)
    {
        this.baseEndpoint = baseEndpoint;
    }

    /// <summary>
    /// 功能：为 canonical Mistral route 创建 catalog allowlist 与请求期环境 credential adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="baseEndpoint">catalog base 或 conformance loopback base。</param>
    /// <param name="credentialSource">只保存 MISTRAL_API_KEY 名称的请求期来源。</param>
    /// <param name="models">同一 snapshot 的 catalog allowlist。</param>
    /// <param name="options">有界 HTTP/SSE/retry 策略。</param>
    /// <remarks>不变量：route 固定为 mistral/mistral-conversations，credential 值不进入 adapter 字段。</remarks>
    internal MistralConversationsProvider(
        Uri baseEndpoint,
        ProviderCredentialSource credentialSource,
        IReadOnlyList<string> models,
        ProviderTransportOptions options)
        : base(
            "mistral",
            baseEndpoint,
            credentialSource,
            "mistral-conversations",
            models,
            options)
    {
        this.baseEndpoint = baseEndpoint;
    }

    /// <summary>
    /// 功能：为本轮构造 Mistral 原生 `/v1/chat/completions` 同源目标。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">已验证模型与 Provider ID 的公共请求。</param>
    /// <returns>保持 base origin 的 Mistral Chat URI。</returns>
    /// <remarks>不变量：请求内容不能改变 path、query 或 authority。</remarks>
    protected override Uri CreateRequestEndpoint(ProviderRequest request)
    {
        return NativeProviderEndpoint.Append(baseEndpoint, "/v1/chat/completions");
    }

    /// <summary>
    /// 功能：把 portable 历史与工具续轮映射为 Mistral Conversations streaming JSON。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">完整语言中立消息和可执行工具声明。</param>
    /// <returns>含 model、messages、stream 及可选 Mistral function tools 的 JSON object。</returns>
    /// <remarks>不变量：工具顺序保持且 `strict:false`；不发送 stream_options。</remarks>
    protected override JsonElement CreateRequestBody(ProviderRequest request)
    {
        var messages = MapMessages(request.Messages);
        if (request.SystemInstructions is not null)
        {
            messages.Insert(0, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["role"] = "system",
                ["content"] = request.SystemInstructions,
            });
        }

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Selection.ModelId,
            ["messages"] = messages,
            ["stream"] = true,
        };
        if (request.Tools.Count > 0)
        {
            body["tools"] = request.Tools.Select(static tool =>
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = tool.InputSchema.Clone(),
                        ["strict"] = false,
                    },
                }).ToArray();
        }

        return JsonSerializer.SerializeToElement(body);
    }
}
