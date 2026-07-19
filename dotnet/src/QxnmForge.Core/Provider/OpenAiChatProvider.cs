using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using QxnmForge.Domain;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：原生实现 OpenAI-compatible Chat Completions 请求与 SSE 归一化。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public class OpenAiChatProvider : HttpSseProviderBase
{
    /// <summary>
    /// 功能：创建禁用重定向并在最终 header 边界注入 Bearer credential 的 Chat adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="endpoint">精确 Chat Completions endpoint。</param>
    /// <param name="credential">可选 API key。</param>
    /// <param name="options">HTTP/SSE/retry 策略。</param>
    public OpenAiChatProvider(
        Uri endpoint,
        string? credential,
        ProviderTransportOptions options)
        : this("openai-compatible", endpoint, credential, options)
    {
    }

    /// <summary>
    /// 功能：为兼容 Chat SSE 语义的独立 API family 创建具有显式 Provider ID 的安全 adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="id">独立 family 的公共 Provider ID。</param>
    /// <param name="endpoint">已验证且不会自动重定向的 base 或精确 endpoint。</param>
    /// <param name="credential">可选 Bearer API key。</param>
    /// <param name="options">HTTP/SSE/retry 策略。</param>
    /// <remarks>不变量：共享的只有 Chat wire parser；派生 family 仍需覆盖自身 path 与请求体。</remarks>
    protected OpenAiChatProvider(
        string id,
        Uri endpoint,
        string? credential,
        ProviderTransportOptions options)
        : base(
            id,
            endpoint,
            credential,
            "Authorization",
            "Bearer ",
            null,
            options)
    {
    }

    /// <summary>
    /// 功能：为 catalog-bound Chat route 创建显式 family、模型 allowlist 与请求期环境 credential 边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="id">canonical Provider ID。</param>
    /// <param name="endpoint">catalog base 或 conformance loopback base。</param>
    /// <param name="credentialSource">只保存冻结环境名称的请求期来源。</param>
    /// <param name="apiFamily">canonical Chat API family。</param>
    /// <param name="models">同一 snapshot 的 catalog allowlist。</param>
    /// <param name="options">有界 HTTP/SSE/retry 策略。</param>
    /// <remarks>不变量：credential 值不进入 adapter 字段，且模型不能绕过 allowlist。</remarks>
    private protected OpenAiChatProvider(
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
    /// 功能：把 portable 历史与工具声明映射为 Chat Completions streaming JSON。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">公共 Provider 请求。</param>
    /// <returns>包含 model、messages、stream 和 stream_options 的 JSON object。</returns>
    protected override JsonElement CreateRequestBody(ProviderRequest request)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Selection.ModelId,
            ["messages"] = MapMessages(request.Messages),
            ["stream"] = true,
            ["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true },
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
                    },
                }).ToArray();
        }

        return JsonSerializer.SerializeToElement(body);
    }

    /// <summary>
    /// 功能：解析 Chat chunk、partial tool arguments、finish reason 与 stream usage。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="response">成功 SSE 响应。</param>
    /// <param name="callerCancellation">run 本地取消。</param>
    /// <param name="totalCancellation">总时限取消。</param>
    /// <returns>归一化文本、工具和 usage 信号。</returns>
    protected override async IAsyncEnumerable<ProviderSignal> ParseResponseAsync(
        HttpResponseMessage response,
        CancellationToken callerCancellation,
        [EnumeratorCancellation] CancellationToken totalCancellation)
    {
        var tools = new Dictionary<int, ToolAccumulator>();
        var completed = false;
        await foreach (var item in ReadSseAsync(
                           response,
                           callerCancellation,
                           totalCancellation).ConfigureAwait(false))
        {
            if (item.Data == "[DONE]")
            {
                completed = true;
                break;
            }

            JsonElement payload;
            try
            {
                payload = ProviderJson.ParseObject(item.Data);
            }
            catch (ProviderOperationException) when (item.EndOfStream)
            {
                throw CreateFailure("provider_disconnected", retryable: true, terminalStatus: "interrupted");
            }

            if (payload.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
            {
                yield return new ProviderUsageSignal(ParseUsage(usageElement));
            }

            if (!payload.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (delta.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(content.GetString()))
                {
                    yield return new ProviderTextSignal(content.GetString()!);
                }

                if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var toolCall in toolCalls.EnumerateArray())
                    {
                        var index = toolCall.TryGetProperty("index", out var indexElement) &&
                                    indexElement.TryGetInt32(out var parsedIndex)
                            ? parsedIndex
                            : 0;
                        if (!tools.TryGetValue(index, out var accumulator))
                        {
                            accumulator = new ToolAccumulator();
                            tools[index] = accumulator;
                        }

                        accumulator.ApplyChatDelta(toolCall);
                    }
                }
            }
        }

        if (!completed)
        {
            throw CreateFailure("provider_disconnected", retryable: true, terminalStatus: "interrupted");
        }

        foreach (var accumulator in tools.OrderBy(static pair => pair.Key).Select(static pair => pair.Value))
        {
            yield return new ProviderToolCallSignal(accumulator.Build());
        }
    }

    /// <summary>
    /// 功能：把 portable message 投影为 Chat role/content/tool_calls 结构。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="messages">有序 portable 消息。</param>
    /// <returns>保持顺序的 Chat message objects。</returns>
    protected static List<object> MapMessages(IReadOnlyList<JsonElement> messages)
    {
        var result = new List<object>(messages.Count);
        foreach (var message in messages)
        {
            var role = ProviderJson.RequireString(message, "role");
            if (role == "tool")
            {
                result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = ProviderJson.RequireString(message, "toolCallId"),
                    ["content"] = ProviderJson.ExtractText(message),
                });
                continue;
            }

            var toolCalls = new List<object>();
            if (role == "assistant" &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var type) && type.GetString() == "tool_call")
                    {
                        toolCalls.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["id"] = ProviderJson.RequireString(block, "toolCallId"),
                            ["type"] = "function",
                            ["function"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["name"] = ProviderJson.RequireString(block, "name"),
                                ["arguments"] = block.GetProperty("arguments").GetRawText(),
                            },
                        });
                    }
                }
            }

            var mapped = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["role"] = role,
                ["content"] = ProviderJson.ExtractText(message),
            };
            if (toolCalls.Count > 0)
            {
                mapped["tool_calls"] = toolCalls;
            }

            result.Add(mapped);
        }

        return result;
    }

    /// <summary>
    /// 功能：把 Chat usage 字段归一为公共 token 计数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="usage">上游 usage object。</param>
    /// <returns>规范化 Usage。</returns>
    private static Usage ParseUsage(JsonElement usage)
    {
        var input = usage.TryGetProperty("prompt_tokens", out var inputElement) && inputElement.TryGetInt64(out var inputValue)
            ? inputValue
            : 0;
        var output = usage.TryGetProperty("completion_tokens", out var outputElement) && outputElement.TryGetInt64(out var outputValue)
            ? outputValue
            : 0;
        var total = usage.TryGetProperty("total_tokens", out var totalElement) && totalElement.TryGetInt64(out var totalValue)
            ? totalValue
            : input + output;
        return new Usage(input, output, total);
    }

    /// <summary>
    /// 功能：增量保存一个 Chat tool_call 的 ID、名称和 JSON 参数片段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class ToolAccumulator
    {
        private readonly StringBuilder arguments = new();
        private string? id;
        private string? name;

        /// <summary>
        /// 功能：应用一个可能只含部分字段的 Chat tool_calls delta。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="toolCall">单个 delta tool call object。</param>
        internal void ApplyChatDelta(JsonElement toolCall)
        {
            if (toolCall.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
            {
                id = idElement.GetString();
            }

            if (!toolCall.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (function.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                name = nameElement.GetString();
            }

            if (function.TryGetProperty("arguments", out var fragment) && fragment.ValueKind == JsonValueKind.String)
            {
                arguments.Append(fragment.GetString());
                if (arguments.Length > 1_048_576)
                {
                    throw new ProviderOperationException(new PortableError(
                        -32005,
                        "provider tool arguments exceeded the limit",
                        false,
                        new ErrorDetails("provider_output_limit")));
                }
            }
        }

        /// <summary>
        /// 功能：完成 partial 参数解析并构造一个公共工具调用。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <returns>严格参数 object 的工具调用。</returns>
        internal ProviderToolCall Build()
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            {
                throw new ProviderOperationException(new PortableError(
                    -32005,
                    "provider tool call omitted required metadata",
                    false,
                    new ErrorDetails("provider_protocol_error")));
            }

            return new ProviderToolCall(id, name, ProviderJson.ParseToolArguments(arguments));
        }
    }
}
