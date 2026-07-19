using System.Runtime.CompilerServices;
using System.Text.Json;
using QxnmForge.Domain;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：原生实现 Google Generative AI GenerateContent 请求、SSE、完整工具参数与 usage 归一化。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public class GoogleGenerativeAiProvider : HttpSseProviderBase
{
    private readonly Uri baseEndpoint;

    /// <summary>
    /// 功能：创建使用 x-goog-api-key 与动态 streamGenerateContent 目标的 Google adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="baseEndpoint">包含 `/v1beta` 的固定生产 base 或显式 conformance loopback base。</param>
    /// <param name="credential">可选 Gemini API key。</param>
    /// <param name="options">有界 HTTP/SSE/retry 策略。</param>
    /// <remarks>不变量：模型只可作为一个 allowlist 路径段；credential 不进入 URL、body、错误或 Session。</remarks>
    /// <exception cref="ArgumentException">base、认证或 transport 配置不安全。</exception>
    public GoogleGenerativeAiProvider(
        Uri baseEndpoint,
        string? credential,
        ProviderTransportOptions options)
        : this(
            "google",
            baseEndpoint,
            credential,
            "x-goog-api-key",
            string.Empty,
            options)
    {
    }

    /// <summary>
    /// 功能：为共享 GenerateContent wire 文档模型的独立 family 创建显式认证边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="id">独立 Provider ID。</param>
    /// <param name="baseEndpoint">已验证的生产或 conformance base。</param>
    /// <param name="credential">只在最终 HTTP 边界使用的可选凭据。</param>
    /// <param name="authenticationHeader">原生认证 header 名。</param>
    /// <param name="authenticationPrefix">原生认证值前缀。</param>
    /// <param name="options">有界 HTTP/SSE/retry 策略。</param>
    /// <remarks>不变量：只共享 GenerateContent body 与 parser，派生 family 必须独立定义 request-target。</remarks>
    protected GoogleGenerativeAiProvider(
        string id,
        Uri baseEndpoint,
        string? credential,
        string authenticationHeader,
        string authenticationPrefix,
        ProviderTransportOptions options)
        : base(
            id,
            baseEndpoint,
            credential,
            authenticationHeader,
            authenticationPrefix,
            null,
            options)
    {
        this.baseEndpoint = baseEndpoint;
    }

    /// <summary>
    /// 功能：为 catalog-bound Google route 创建显式 family、allowlist 与请求期环境 credential 边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="id">canonical Provider ID。</param>
    /// <param name="baseEndpoint">catalog base 或 conformance loopback base。</param>
    /// <param name="credentialSource">只保存冻结环境名称的请求期来源。</param>
    /// <param name="apiFamily">canonical GenerateContent family。</param>
    /// <param name="models">同一 snapshot 的 catalog allowlist。</param>
    /// <param name="options">HTTP/SSE/retry 策略。</param>
    /// <remarks>不变量：credential 值不进入 adapter 字段；模型同时受 allowlist 与安全 path segment 约束。</remarks>
    private protected GoogleGenerativeAiProvider(
        string id,
        Uri baseEndpoint,
        ProviderCredentialSource credentialSource,
        string apiFamily,
        IReadOnlyList<string> models,
        ProviderTransportOptions options)
        : base(
            id,
            baseEndpoint,
            credentialSource,
            "x-goog-api-key",
            string.Empty,
            null,
            options,
            apiFamily,
            models)
    {
        this.baseEndpoint = baseEndpoint;
    }

    /// <summary>
    /// 功能：仅接受可安全进入单一路径 segment 的 Google 模型 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="modelId">run/start 选择的模型 ID。</param>
    /// <returns>符合冻结目录字符边界时为 true。</returns>
    /// <remarks>不变量：合法模型不能改变 path、query、fragment 或 authority。</remarks>
    public override bool SupportsModel(string modelId)
    {
        return base.SupportsModel(modelId) && NativeProviderEndpoint.IsSafeModelSegment(modelId);
    }

    /// <summary>
    /// 功能：构造 `/models/{model}:streamGenerateContent?alt=sse` 的同源原生目标。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">已验证 Google 模型的公共请求。</param>
    /// <returns>保持 base origin 且带唯一 alt=sse 查询的 URI。</returns>
    /// <remarks>不变量：模型先经单路径段 allowlist，再进入后缀。</remarks>
    protected override Uri CreateRequestEndpoint(ProviderRequest request)
    {
        if (!SupportsModel(request.Selection.ModelId))
        {
            throw CreateFailure("provider_request_target_invalid", retryable: false);
        }

        return NativeProviderEndpoint.Append(
            baseEndpoint,
            $"/models/{request.Selection.ModelId}:streamGenerateContent",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["alt"] = "sse",
            });
    }

    /// <summary>
    /// 功能：把 portable 历史、system 指令、工具定义和工具结果映射为 GenerateContent body。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">完整语言中立 Provider 请求。</param>
    /// <returns>含 contents 及可选 systemInstruction/functionDeclarations 的 JSON object。</returns>
    /// <remarks>不变量：模型与 stream 选择只进入 URL；工具 Schema 使用 parametersJsonSchema。</remarks>
    protected override JsonElement CreateRequestBody(ProviderRequest request)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["contents"] = MapContents(request.Messages),
        };
        var systemInstruction = MapSystemInstruction(request.Messages);
        if (systemInstruction is not null)
        {
            body["systemInstruction"] = systemInstruction;
        }

        if (request.Tools.Count > 0)
        {
            body["tools"] = new[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["functionDeclarations"] = request.Tools.Select(static tool =>
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["name"] = tool.Name,
                            ["description"] = tool.Description,
                            ["parametersJsonSchema"] = tool.InputSchema.Clone(),
                        }).ToArray(),
                },
            };
        }

        return JsonSerializer.SerializeToElement(body);
    }

    /// <summary>
    /// 功能：解析 GenerateContentResponse SSE 中的文本、完整 functionCall.args、usage 与完成原因。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="response">成功且尚未读取的 SSE 响应。</param>
    /// <param name="callerCancellation">run 本地取消。</param>
    /// <param name="totalCancellation">Provider 总时限取消。</param>
    /// <returns>按上游顺序产生文本、工具和 usage 信号。</returns>
    /// <remarks>不变量：args 必须在单个完整 Provider JSON 对象中出现；HTTP 字节分片不伪造成参数 delta。</remarks>
    protected override async IAsyncEnumerable<ProviderSignal> ParseResponseAsync(
        HttpResponseMessage response,
        CancellationToken callerCancellation,
        [EnumeratorCancellation] CancellationToken totalCancellation)
    {
        var completed = false;
        var fallbackCallIndex = 0;
        var seenCallIds = new HashSet<string>(StringComparer.Ordinal);
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

            if (payload.TryGetProperty("error", out _))
            {
                throw CreateFailure("provider_stream_error", retryable: false);
            }

            if (payload.TryGetProperty("candidates", out var candidates) &&
                candidates.ValueKind == JsonValueKind.Array)
            {
                foreach (var candidate in candidates.EnumerateArray())
                {
                    if (candidate.ValueKind != JsonValueKind.Object)
                    {
                        throw CreateFailure("provider_protocol_error", retryable: false);
                    }

                    if (candidate.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.Object &&
                        content.TryGetProperty("parts", out var parts) &&
                        parts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (part.ValueKind != JsonValueKind.Object)
                            {
                                throw CreateFailure("provider_protocol_error", retryable: false);
                            }

                            if (part.TryGetProperty("text", out var text) &&
                                text.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrEmpty(text.GetString()) &&
                                (!part.TryGetProperty("thought", out var thought) ||
                                 thought.ValueKind != JsonValueKind.True))
                            {
                                yield return new ProviderTextSignal(text.GetString()!);
                            }

                            if (part.TryGetProperty("functionCall", out var functionCall))
                            {
                                if (functionCall.ValueKind != JsonValueKind.Object)
                                {
                                    throw CreateFailure("provider_protocol_error", retryable: false);
                                }

                                var name = ProviderJson.RequireString(functionCall, "name");
                                string callId;
                                if (functionCall.TryGetProperty("id", out var callIdElement) &&
                                    callIdElement.ValueKind == JsonValueKind.String &&
                                    !string.IsNullOrEmpty(callIdElement.GetString()))
                                {
                                    callId = callIdElement.GetString()!;
                                }
                                else
                                {
                                    fallbackCallIndex++;
                                    callId = $"call_google_{fallbackCallIndex}";
                                }

                                if (!seenCallIds.Add(callId))
                                {
                                    throw CreateFailure("provider_protocol_error", retryable: false);
                                }

                                var arguments = EmptyArguments();
                                if (functionCall.TryGetProperty("args", out var args))
                                {
                                    if (args.ValueKind != JsonValueKind.Object)
                                    {
                                        throw CreateFailure("provider_tool_arguments_invalid", retryable: false);
                                    }

                                    arguments = args.Clone();
                                }

                                yield return new ProviderToolCallSignal(
                                    new ProviderToolCall(callId, name, arguments));
                            }
                        }
                    }

                    if (candidate.TryGetProperty("finishReason", out var finishReason) &&
                        finishReason.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(finishReason.GetString()))
                    {
                        completed = true;
                    }
                }
            }

            if (payload.TryGetProperty("usageMetadata", out var usage) &&
                usage.ValueKind == JsonValueKind.Object)
            {
                yield return new ProviderUsageSignal(ParseUsage(usage));
            }
        }

        if (!completed)
        {
            throw CreateFailure("provider_disconnected", retryable: true, terminalStatus: "interrupted");
        }
    }

    /// <summary>
    /// 功能：把非 system portable 消息映射为 Google user/model contents 与工具续轮 parts。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="messages">按 durable 顺序排列的 portable 消息。</param>
    /// <returns>保持消息顺序的 Google contents。</returns>
    /// <remarks>不变量：assistant 工具调用使用 functionCall，tool 结果使用 functionResponse 并保留关联 ID/名称。</remarks>
    private static List<object> MapContents(IReadOnlyList<JsonElement> messages)
    {
        var contents = new List<object>(messages.Count);
        foreach (var message in messages)
        {
            var role = ProviderJson.RequireString(message, "role");
            if (role == "system")
            {
                continue;
            }

            var parts = new List<object>();
            if (role == "tool")
            {
                var isError = message.TryGetProperty("isError", out var errorElement) &&
                    errorElement.ValueKind == JsonValueKind.True;
                var response = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [isError ? "error" : "output"] = ProviderJson.ExtractText(message),
                };
                parts.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["functionResponse"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["id"] = ProviderJson.RequireString(message, "toolCallId"),
                        ["name"] = ProviderJson.RequireString(message, "toolName"),
                        ["response"] = response,
                    },
                });
            }
            else if (message.TryGetProperty("content", out var content) &&
                     content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (!block.TryGetProperty("type", out var type) ||
                        type.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    if (type.GetString() == "text")
                    {
                        parts.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["text"] = block.TryGetProperty("text", out var text) &&
                                text.ValueKind == JsonValueKind.String
                                ? text.GetString()
                                : string.Empty,
                        });
                    }
                    else if (type.GetString() == "tool_call")
                    {
                        var arguments = block.TryGetProperty("arguments", out var args) &&
                            args.ValueKind == JsonValueKind.Object
                            ? args.Clone()
                            : EmptyArguments();
                        parts.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["functionCall"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["id"] = ProviderJson.RequireString(block, "toolCallId"),
                                ["name"] = ProviderJson.RequireString(block, "name"),
                                ["args"] = arguments,
                            },
                        });
                    }
                }
            }

            if (parts.Count > 0)
            {
                contents.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["role"] = role == "assistant" ? "model" : "user",
                    ["parts"] = parts,
                });
            }
        }

        return contents;
    }

    /// <summary>
    /// 功能：把全部 system 文本按消息顺序合并为 Google systemInstruction。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="messages">完整 portable 历史。</param>
    /// <returns>有 system 文本时返回 parts 对象，否则返回 null。</returns>
    /// <remarks>不变量：工具调用、结果和实现元数据不会进入 systemInstruction。</remarks>
    private static Dictionary<string, object?>? MapSystemInstruction(IReadOnlyList<JsonElement> messages)
    {
        var text = messages
            .Where(static message =>
                message.TryGetProperty("role", out var role) &&
                role.ValueKind == JsonValueKind.String &&
                role.GetString() == "system")
            .Select(ProviderJson.ExtractText)
            .Where(static value => !string.IsNullOrEmpty(value))
            .ToArray();
        return text.Length == 0
            ? null
            : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["parts"] = new[]
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["text"] = string.Join('\n', text),
                    },
                },
            };
    }

    /// <summary>
    /// 功能：把 Google usageMetadata 归一为公共 Usage，并对缺失 total 使用安全加和。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="usage">GenerateContentResponse usageMetadata object。</param>
    /// <returns>input、output（候选加思考）与 total token。</returns>
    /// <exception cref="OverflowException">上游 token 计数加和超出 Int64。</exception>
    private static Usage ParseUsage(JsonElement usage)
    {
        var input = ReadTokenCount(usage, "promptTokenCount");
        var output = checked(
            ReadTokenCount(usage, "candidatesTokenCount") +
            ReadTokenCount(usage, "thoughtsTokenCount"));
        var total = usage.TryGetProperty("totalTokenCount", out var totalElement) &&
                    totalElement.TryGetInt64(out var parsedTotal)
            ? parsedTotal
            : checked(input + output);
        return new Usage(input, output, total);
    }

    /// <summary>
    /// 功能：从 usageMetadata 读取一个非负 Int64 token 字段，缺失或非法时返回零。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="usage">usageMetadata object。</param>
    /// <param name="name">代码内固定字段名。</param>
    /// <returns>非负计数或零。</returns>
    private static long ReadTokenCount(JsonElement usage, string name)
    {
        return usage.TryGetProperty(name, out var element) &&
               element.TryGetInt64(out var value) &&
               value >= 0
            ? value
            : 0;
    }

    /// <summary>
    /// 功能：创建独立空 JSON object 作为缺失 Google args 的规范默认参数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>可安全跨异步边界持有的独立 JsonElement object。</returns>
    private static JsonElement EmptyArguments()
    {
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
    }
}
