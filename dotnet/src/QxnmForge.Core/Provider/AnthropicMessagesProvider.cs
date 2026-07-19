using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using QxnmForge.Domain;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：原生实现 Anthropic Messages 请求、typed SSE、usage 与 tool input_json 归一化。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public class AnthropicMessagesProvider : HttpSseProviderBase
{
    private static readonly IReadOnlyDictionary<string, string> Headers =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic-version"] = "2023-06-01",
        };

    /// <summary>
    /// 功能：创建最终注入 x-api-key 且固定 anthropic-version 的 Messages adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="endpoint">精确 Messages endpoint。</param>
    /// <param name="credential">可选 Anthropic API key。</param>
    /// <param name="options">HTTP/SSE/retry 策略。</param>
    public AnthropicMessagesProvider(
        Uri endpoint,
        string? credential,
        ProviderTransportOptions options)
        : base(
            "anthropic",
            endpoint,
            credential,
            "x-api-key",
            string.Empty,
            Headers,
            options)
    {
    }

    /// <summary>
    /// 功能：为 catalog-bound Messages route 创建显式 Provider/family、allowlist 与请求期环境 credential 边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="id">canonical Provider ID。</param>
    /// <param name="endpoint">catalog base 或 conformance loopback base。</param>
    /// <param name="credentialSource">只保存冻结环境名称的请求期来源。</param>
    /// <param name="apiFamily">canonical Messages API family。</param>
    /// <param name="models">同一 snapshot 的 catalog allowlist。</param>
    /// <param name="options">HTTP/SSE/retry 策略。</param>
    /// <remarks>不变量：固定 anthropic-version 保持不变，credential 值不进入 adapter 字段。</remarks>
    private protected AnthropicMessagesProvider(
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
            "x-api-key",
            string.Empty,
            Headers,
            options,
            apiFamily,
            models)
    {
    }

    /// <summary>
    /// 功能：把 portable 历史和工具声明映射为 Anthropic messages/tools streaming JSON。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">公共 Provider 请求。</param>
    /// <returns>含 max_tokens 的 Messages request object。</returns>
    protected override JsonElement CreateRequestBody(ProviderRequest request)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Selection.ModelId,
            ["messages"] = MapMessages(request.Messages),
            ["stream"] = true,
            ["max_tokens"] = 4096,
        };
        if (request.Tools.Count > 0)
        {
            body["tools"] = request.Tools.Select(static tool =>
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = tool.InputSchema.Clone(),
                }).ToArray();
        }

        return JsonSerializer.SerializeToElement(body);
    }

    /// <summary>
    /// 功能：解析 Anthropic message/content typed events、partial input_json 与分段 usage。
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
        long inputTokens = 0;
        long outputTokens = 0;
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
                case "message_start":
                    if (payload.TryGetProperty("message", out var message) &&
                        message.ValueKind == JsonValueKind.Object &&
                        message.TryGetProperty("usage", out var startUsage) &&
                        startUsage.ValueKind == JsonValueKind.Object &&
                        startUsage.TryGetProperty("input_tokens", out var input) &&
                        input.TryGetInt64(out var parsedInput))
                    {
                        inputTokens = parsedInput;
                    }

                    break;
                case "content_block_start":
                    if (payload.TryGetProperty("index", out var indexElement) &&
                        indexElement.TryGetInt32(out var index) &&
                        payload.TryGetProperty("content_block", out var block) &&
                        block.ValueKind == JsonValueKind.Object &&
                        block.TryGetProperty("type", out var blockType) &&
                        blockType.GetString() == "tool_use")
                    {
                        tools[index] = new ToolAccumulator(
                            ProviderJson.RequireString(block, "id"),
                            ProviderJson.RequireString(block, "name"));
                    }

                    break;
                case "content_block_delta":
                    if (!payload.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                    {
                        throw CreateFailure("provider_protocol_error", retryable: false);
                    }

                    var deltaType = ProviderJson.RequireString(delta, "type");
                    if (deltaType == "text_delta" &&
                        delta.TryGetProperty("text", out var text) &&
                        text.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(text.GetString()))
                    {
                        yield return new ProviderTextSignal(text.GetString()!);
                    }
                    else if (deltaType == "input_json_delta" &&
                             payload.TryGetProperty("index", out var toolIndexElement) &&
                             toolIndexElement.TryGetInt32(out var toolIndex) &&
                             tools.TryGetValue(toolIndex, out var accumulator) &&
                             delta.TryGetProperty("partial_json", out var fragment) &&
                             fragment.ValueKind == JsonValueKind.String)
                    {
                        accumulator.Append(fragment.GetString()!);
                    }

                    break;
                case "message_delta":
                    if (payload.TryGetProperty("usage", out var deltaUsage) &&
                        deltaUsage.ValueKind == JsonValueKind.Object &&
                        deltaUsage.TryGetProperty("output_tokens", out var output) &&
                        output.TryGetInt64(out var parsedOutput))
                    {
                        outputTokens = parsedOutput;
                    }

                    break;
                case "message_stop":
                    completed = true;
                    break;
            }
        }

        if (!completed)
        {
            throw CreateFailure("provider_disconnected", retryable: true, terminalStatus: "interrupted");
        }

        foreach (var tool in tools.OrderBy(static pair => pair.Key).Select(static pair => pair.Value))
        {
            yield return new ProviderToolCallSignal(tool.Build());
        }

        yield return new ProviderUsageSignal(new Usage(
            inputTokens,
            outputTokens,
            checked(inputTokens + outputTokens)));
    }

    /// <summary>
    /// 功能：把 portable user/assistant/tool 消息映射为 Anthropic content blocks。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="messages">有序 portable 消息。</param>
    /// <returns>Messages API message objects。</returns>
    private static List<object> MapMessages(IReadOnlyList<JsonElement> messages)
    {
        var result = new List<object>(messages.Count);
        foreach (var message in messages)
        {
            var role = ProviderJson.RequireString(message, "role");
            if (role == "tool")
            {
                result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["role"] = "user",
                    ["content"] = new[]
                    {
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = ProviderJson.RequireString(message, "toolCallId"),
                            ["content"] = ProviderJson.ExtractText(message),
                            ["is_error"] = message.TryGetProperty("isError", out var isError) && isError.GetBoolean(),
                        },
                    },
                });
                continue;
            }

            var blocks = new List<object>();
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (!block.TryGetProperty("type", out var type))
                    {
                        continue;
                    }

                    if (type.GetString() == "text")
                    {
                        blocks.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["type"] = "text",
                            ["text"] = block.GetProperty("text").GetString() ?? string.Empty,
                        });
                    }
                    else if (type.GetString() == "tool_call")
                    {
                        blocks.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["type"] = "tool_use",
                            ["id"] = ProviderJson.RequireString(block, "toolCallId"),
                            ["name"] = ProviderJson.RequireString(block, "name"),
                            ["input"] = block.GetProperty("arguments").Clone(),
                        });
                    }
                }
            }

            result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["role"] = role,
                ["content"] = blocks,
            });
        }

        return result;
    }

    /// <summary>
    /// 功能：累积 Anthropic tool_use 的 partial input_json。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class ToolAccumulator
    {
        private readonly StringBuilder arguments = new();

        /// <summary>
        /// 功能：创建具有稳定 tool use ID 与名称的累积器。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="callId">Anthropic tool_use.id。</param>
        /// <param name="name">规范工具名。</param>
        internal ToolAccumulator(string callId, string name)
        {
            CallId = callId;
            Name = name;
        }

        private string CallId { get; }

        private string Name { get; }

        /// <summary>
        /// 功能：追加一个有界 partial_json 字符串。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="fragment">JSON 参数片段。</param>
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
        /// 功能：严格解析完整 input_json 并返回公共工具调用。
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
