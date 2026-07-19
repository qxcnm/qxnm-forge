using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using QxnmForge.Domain;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：原生实现 OpenAI Responses API 请求、typed SSE 和 function arguments 归一化。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public class OpenAiResponsesProvider : HttpSseProviderBase
{
    /// <summary>
    /// 功能：创建禁用重定向并最终注入 Bearer credential 的 Responses adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="endpoint">精确 Responses endpoint。</param>
    /// <param name="credential">可选 API key。</param>
    /// <param name="options">HTTP/SSE/retry 策略。</param>
    public OpenAiResponsesProvider(
        Uri endpoint,
        string? credential,
        ProviderTransportOptions options)
        : this(
            "openai-responses",
            endpoint,
            credential,
            "Authorization",
            "Bearer ",
            options)
    {
    }

    /// <summary>
    /// 功能：为兼容 Responses typed SSE 的独立 API family 创建显式认证边界 adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="id">独立 family 的公共 Provider ID。</param>
    /// <param name="endpoint">已验证且不会自动重定向的 base 或精确 endpoint。</param>
    /// <param name="credential">可选 API key。</param>
    /// <param name="authenticationHeader">固定认证 header 名。</param>
    /// <param name="authenticationPrefix">固定认证值前缀。</param>
    /// <param name="options">HTTP/SSE/retry 策略。</param>
    /// <param name="additionalHeaders">独立 family 持有的非 secret 固定 headers。</param>
    /// <remarks>不变量：共享的只有 Responses stream parser；派生 family 必须独立定义 request target 与 body。</remarks>
    protected OpenAiResponsesProvider(
        string id,
        Uri endpoint,
        string? credential,
        string authenticationHeader,
        string authenticationPrefix,
        ProviderTransportOptions options,
        IReadOnlyDictionary<string, string>? additionalHeaders = null)
        : base(
            id,
            endpoint,
            credential,
            authenticationHeader,
            authenticationPrefix,
            additionalHeaders,
            options)
    {
    }

    /// <summary>
    /// 功能：为 catalog-bound Responses route 创建显式 family、allowlist 与请求期环境 credential 边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="id">canonical Provider ID。</param>
    /// <param name="endpoint">catalog base 或 conformance loopback base。</param>
    /// <param name="credentialSource">只保存冻结环境名称的请求期来源。</param>
    /// <param name="apiFamily">canonical Responses API family。</param>
    /// <param name="models">同一 snapshot 的 catalog allowlist。</param>
    /// <param name="options">HTTP/SSE/retry 策略。</param>
    /// <remarks>不变量：credential 值不进入 adapter 字段，且模型必须精确命中 allowlist。</remarks>
    private protected OpenAiResponsesProvider(
        string id,
        Uri endpoint,
        ProviderCredentialSource credentialSource,
        string apiFamily,
        IReadOnlyList<string> models,
        ProviderTransportOptions options)
        : base(
            id,
            endpoint,
            credentialSource,
            "Authorization",
            "Bearer ",
            null,
            options,
            apiFamily,
            models)
    {
    }

    /// <summary>
    /// 功能：把 portable 历史和工具声明映射为 Responses input 与 function tools。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">公共 Provider 请求。</param>
    /// <returns>Responses streaming JSON object。</returns>
    protected override JsonElement CreateRequestBody(ProviderRequest request)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Selection.ModelId,
            ["input"] = MapInput(request.Messages),
            ["stream"] = true,
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
                }).ToArray();
        }

        return JsonSerializer.SerializeToElement(body);
    }

    /// <summary>
    /// 功能：解析 Responses typed events、文本、partial function arguments 和最终 usage。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="response">成功 SSE 响应。</param>
    /// <param name="callerCancellation">run 本地取消。</param>
    /// <param name="totalCancellation">总时限取消。</param>
    /// <returns>归一化 Provider signals。</returns>
    protected override async IAsyncEnumerable<ProviderSignal> ParseResponseAsync(
        HttpResponseMessage response,
        CancellationToken callerCancellation,
        [EnumeratorCancellation] CancellationToken totalCancellation)
    {
        var tools = new Dictionary<string, ToolAccumulator>(StringComparer.Ordinal);
        var completed = false;
        await foreach (var item in ReadSseAsync(
                           response,
                           callerCancellation,
                           totalCancellation).ConfigureAwait(false))
        {
            JsonElement payload;
            try
            {
                payload = ProviderJson.ParseObject(item.Data);
            }
            catch (ProviderOperationException) when (item.EndOfStream)
            {
                throw CreateFailure("provider_disconnected", retryable: true, terminalStatus: "interrupted");
            }

            var type = payload.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : item.Event;
            switch (type)
            {
                case "response.output_text.delta":
                    if (payload.TryGetProperty("delta", out var delta) &&
                        delta.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(delta.GetString()))
                    {
                        yield return new ProviderTextSignal(delta.GetString()!);
                    }

                    break;
                case "response.output_item.added":
                    if (payload.TryGetProperty("item", out var outputItem) &&
                        outputItem.ValueKind == JsonValueKind.Object &&
                        outputItem.TryGetProperty("type", out var outputType) &&
                        outputType.GetString() == "function_call")
                    {
                        var itemId = ProviderJson.RequireString(outputItem, "id");
                        var addedTool = new ToolAccumulator(
                            ProviderJson.RequireString(outputItem, "call_id"),
                            ProviderJson.RequireString(outputItem, "name"));
                        if (outputItem.TryGetProperty("arguments", out var initialArguments) &&
                            initialArguments.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrEmpty(initialArguments.GetString()))
                        {
                            addedTool.Append(initialArguments.GetString()!);
                        }

                        tools[itemId] = addedTool;
                    }

                    break;
                case "response.function_call_arguments.delta":
                    var deltaItemId = ProviderJson.RequireString(payload, "item_id");
                    if (!tools.TryGetValue(deltaItemId, out var accumulator) ||
                        !payload.TryGetProperty("delta", out var argumentDelta) ||
                        argumentDelta.ValueKind != JsonValueKind.String)
                    {
                        throw CreateFailure("provider_protocol_error", retryable: false);
                    }

                    accumulator.Append(argumentDelta.GetString()!);
                    break;
                case "response.function_call_arguments.done":
                    var completedItemId = ProviderJson.RequireString(payload, "item_id");
                    if (!tools.TryGetValue(completedItemId, out var completedAccumulator) ||
                        !payload.TryGetProperty("arguments", out var completeArguments) ||
                        completeArguments.ValueKind != JsonValueKind.String)
                    {
                        throw CreateFailure("provider_protocol_error", retryable: false);
                    }

                    completedAccumulator.Complete(completeArguments.GetString()!);
                    break;
                case "response.completed":
                    completed = true;
                    if (payload.TryGetProperty("response", out var completedResponse) &&
                        completedResponse.ValueKind == JsonValueKind.Object &&
                        completedResponse.TryGetProperty("usage", out var usage) &&
                        usage.ValueKind == JsonValueKind.Object)
                    {
                        yield return new ProviderUsageSignal(ParseUsage(usage));
                    }

                    break;
            }
        }

        if (!completed)
        {
            throw CreateFailure("provider_disconnected", retryable: true, terminalStatus: "interrupted");
        }

        foreach (var tool in tools.Values)
        {
            yield return new ProviderToolCallSignal(tool.Build());
        }
    }

    /// <summary>
    /// 功能：把 portable messages 映射为 Responses input message/function_call/function_call_output items。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="messages">有序 portable 消息。</param>
    /// <returns>Responses input 数组。</returns>
    protected static List<object> MapInput(IReadOnlyList<JsonElement> messages)
    {
        var input = new List<object>();
        foreach (var message in messages)
        {
            var role = ProviderJson.RequireString(message, "role");
            if (role == "tool")
            {
                input.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = ProviderJson.RequireString(message, "toolCallId"),
                    ["output"] = ProviderJson.ExtractText(message),
                });
                continue;
            }

            input.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["role"] = role,
                ["content"] = new[]
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = role == "assistant" ? "output_text" : "input_text",
                        ["text"] = ProviderJson.ExtractText(message),
                    },
                },
            });
            if (role == "assistant" &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var type) && type.GetString() == "tool_call")
                    {
                        input.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["type"] = "function_call",
                            ["call_id"] = ProviderJson.RequireString(block, "toolCallId"),
                            ["name"] = ProviderJson.RequireString(block, "name"),
                            ["arguments"] = block.GetProperty("arguments").GetRawText(),
                        });
                    }
                }
            }
        }

        return input;
    }

    /// <summary>
    /// 功能：把 Responses usage 字段归一为公共 Usage。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="usage">上游 usage object。</param>
    /// <returns>规范化 token 计数。</returns>
    private static Usage ParseUsage(JsonElement usage)
    {
        var input = usage.TryGetProperty("input_tokens", out var inputElement) && inputElement.TryGetInt64(out var inputValue)
            ? inputValue
            : 0;
        var output = usage.TryGetProperty("output_tokens", out var outputElement) && outputElement.TryGetInt64(out var outputValue)
            ? outputValue
            : 0;
        var total = usage.TryGetProperty("total_tokens", out var totalElement) && totalElement.TryGetInt64(out var totalValue)
            ? totalValue
            : input + output;
        return new Usage(input, output, total);
    }

    /// <summary>
    /// 功能：累积一个 Responses function_call 的参数片段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class ToolAccumulator
    {
        private readonly StringBuilder arguments = new();

        /// <summary>
        /// 功能：创建包含稳定 call ID 与函数名的累积器。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="callId">公共 tool call ID。</param>
        /// <param name="name">规范工具名。</param>
        internal ToolAccumulator(string callId, string name)
        {
            CallId = callId;
            Name = name;
        }

        private string CallId { get; }

        private string Name { get; }

        /// <summary>
        /// 功能：按事件顺序追加一个有界 arguments delta。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="fragment">JSON 字符串片段。</param>
        internal void Append(string fragment)
        {
            arguments.Append(fragment);
            if (arguments.Length > 1_048_576)
            {
                throw new ProviderOperationException(new PortableError(
                    -32005,
                    "provider tool arguments exceeded the limit",
                    false,
                    new ErrorDetails("provider_output_limit")));
            }
        }

        /// <summary>
        /// 功能：接受 Responses done 事件的完整 arguments，并与先前 delta 严格核对。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="complete">Provider 在 done 事件报告的完整 JSON 字符串。</param>
        /// <remarks>不变量：已有 delta 必须逐字符等于完整值；无 delta 时完整值成为唯一参数来源。</remarks>
        /// <exception cref="ProviderOperationException">完整值与先前 partial 片段不一致或超过上限。</exception>
        internal void Complete(string complete)
        {
            if (arguments.Length == 0)
            {
                Append(complete);
                return;
            }

            if (!arguments.ToString().Equals(complete, StringComparison.Ordinal))
            {
                throw new ProviderOperationException(new PortableError(
                    -32005,
                    "provider tool arguments were inconsistent",
                    false,
                    new ErrorDetails("provider_protocol_error")));
            }
        }

        /// <summary>
        /// 功能：严格解析完整参数并返回公共工具调用。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <returns>完整 ProviderToolCall。</returns>
        internal ProviderToolCall Build()
        {
            return new ProviderToolCall(CallId, Name, ProviderJson.ParseToolArguments(arguments));
        }
    }
}
