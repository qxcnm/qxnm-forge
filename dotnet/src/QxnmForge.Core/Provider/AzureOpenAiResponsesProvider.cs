using System.Text.Json;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：原生实现 Azure OpenAI Responses request-target、api-key 认证与 Responses SSE 归一化。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class AzureOpenAiResponsesProvider : OpenAiResponsesProvider
{
    private readonly Uri baseEndpoint;
    private readonly string apiVersion;

    /// <summary>
    /// 功能：创建只使用显式 Azure base、固定 Responses 路径和 api-key 的原生 adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="baseEndpoint">运行时显式生产 base 或 conformance loopback base。</param>
    /// <param name="apiVersion">有界 Azure api-version；缺省策略由 registry 决定。</param>
    /// <param name="credential">可选 Azure OpenAI API key。</param>
    /// <param name="options">有界 HTTP/SSE/retry 策略。</param>
    /// <remarks>不变量：实现不猜测 resource、私有云或 sovereign cloud origin。</remarks>
    /// <exception cref="ArgumentException">base、版本、认证或 transport 配置不安全。</exception>
    public AzureOpenAiResponsesProvider(
        Uri baseEndpoint,
        string apiVersion,
        string? credential,
        ProviderTransportOptions options)
        : base(
            "azure-openai-responses",
            baseEndpoint,
            credential,
            "api-key",
            string.Empty,
            options)
    {
        if (!NativeProviderEndpoint.IsSafeApiVersion(apiVersion))
        {
            throw new ArgumentException("Azure OpenAI API version is invalid", nameof(apiVersion));
        }

        this.baseEndpoint = baseEndpoint;
        this.apiVersion = apiVersion;
    }

    /// <summary>
    /// 功能：为本轮构造 `/responses?api-version=...` 的同源 Azure 原生目标。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">已验证部署模型与 Provider ID 的公共请求。</param>
    /// <returns>保持显式 base origin 的 Azure Responses URI。</returns>
    /// <remarks>不变量：版本经过 ASCII allowlist 并由 URI encoder 形成唯一 query 值。</remarks>
    protected override Uri CreateRequestEndpoint(ProviderRequest request)
    {
        return NativeProviderEndpoint.Append(
            baseEndpoint,
            "/responses",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["api-version"] = apiVersion,
            });
    }

    /// <summary>
    /// 功能：把 portable 历史和工具续轮映射为 Azure Responses streaming JSON。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">完整语言中立消息和工具声明。</param>
    /// <returns>含 model、input、stream、store:false 及可选 function tools 的 JSON object。</returns>
    /// <remarks>不变量：工具顺序保持且 strict=false；服务端存储不可由调用方开启。</remarks>
    protected override JsonElement CreateRequestBody(ProviderRequest request)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Selection.ModelId,
            ["input"] = MapInput(request.Messages),
            ["stream"] = true,
            ["store"] = false,
        };
        if (request.SystemInstructions is not null)
        {
            body["instructions"] = request.SystemInstructions;
        }

        if (request.Tools.Count > 0)
        {
            body["tools"] = request.Tools.Select(static tool =>
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "function",
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.InputSchema.Clone(),
                    ["strict"] = false,
                }).ToArray();
        }

        return JsonSerializer.SerializeToElement(body);
    }
}
