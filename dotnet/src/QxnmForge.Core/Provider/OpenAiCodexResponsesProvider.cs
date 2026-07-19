using System.Text.Json;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：原生实现 Codex Responses 路径、OAuth Bearer、固定客户端 header 与 typed SSE。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class OpenAiCodexResponsesProvider : OpenAiResponsesProvider
{
    private static readonly IReadOnlyDictionary<string, string> FixedHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OpenAI-Beta"] = "responses=experimental",
            ["originator"] = "qxnm-forge",
        };
    private readonly Uri baseEndpoint;

    /// <summary>
    /// 功能：创建独立 Codex Responses adapter，凭据只在最终 Authorization header 使用。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="baseEndpoint">生产 origin 或显式 conformance 回环 base。</param>
    /// <param name="oauthToken">交互 OAuth 流程或 conformance 注入的 access token。</param>
    /// <param name="options">有界 HTTP/SSE/retry 策略。</param>
    /// <remarks>不变量：`OpenAI-Beta` 与 `originator` 只由 adapter 写入，不接受模型或调用方覆盖。</remarks>
    public OpenAiCodexResponsesProvider(
        Uri baseEndpoint,
        string? oauthToken,
        ProviderTransportOptions options)
        : base(
            "openai-codex",
            baseEndpoint,
            oauthToken,
            "Authorization",
            "Bearer ",
            options,
            FixedHeaders)
    {
        this.baseEndpoint = baseEndpoint;
    }

    /// <summary>
    /// 功能：为 Codex 构造固定 `/codex/responses` 同源请求目标。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">已通过 Provider 选择校验的公共请求。</param>
    /// <returns>不含 query 的 Codex Responses URI。</returns>
    protected override Uri CreateRequestEndpoint(ProviderRequest request)
    {
        return NativeProviderEndpoint.Append(baseEndpoint, "/codex/responses");
    }

    /// <summary>
    /// 功能：映射 Codex Responses 请求并强制 `stream:true` 与 `store:false`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">完整 portable 历史和工具定义。</param>
    /// <returns>含 Responses input 与 function tools 的 JSON object。</returns>
    /// <remarks>不变量：服务端存储不可由调用方开启；工具顺序保持。</remarks>
    protected override JsonElement CreateRequestBody(ProviderRequest request)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Selection.ModelId,
            ["input"] = MapInput(request.Messages),
            ["stream"] = true,
            ["store"] = false,
        };
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
