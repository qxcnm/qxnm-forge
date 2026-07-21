using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QxnmForge.Domain;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：原生实现 Bedrock ConverseStream body、AWS SigV4 header 和双 CRC EventStream 归一化。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class BedrockConverseStreamProvider : HttpSseProviderBase
{
    private const string EventStreamMediaType = "application/vnd.amazon.eventstream";
    private readonly Uri baseEndpoint;
    private readonly string accessKeyId;
    private readonly string secretAccessKey;
    private readonly string? sessionToken;
    private readonly string region;

    /// <summary>
    /// 功能：创建一个使用显式 AWS 凭据与 region 的 ConverseStream adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="baseEndpoint">生产 Bedrock runtime origin 或 conformance 回环 base。</param>
    /// <param name="accessKeyId">只在 SigV4 Credential scope 中使用的 access key ID。</param>
    /// <param name="secretAccessKey">只在内存 HMAC 派生中使用的 secret access key。</param>
    /// <param name="sessionToken">可选临时凭据 token，最终只进入 `x-amz-security-token`。</param>
    /// <param name="region">用于 SigV4 scope 的 AWS region。</param>
    /// <param name="options">有界 HTTP/EventStream/retry 策略。</param>
    /// <remarks>不变量：凭据不进入 URL、body、错误、日志或 Session；不跟随重定向。</remarks>
    /// <exception cref="ArgumentException">凭据、region、endpoint 或 transport 配置无效。</exception>
    public BedrockConverseStreamProvider(
        Uri baseEndpoint,
        string accessKeyId,
        string secretAccessKey,
        string? sessionToken,
        string region,
        ProviderTransportOptions options)
        : base(
            "amazon-bedrock",
            baseEndpoint,
            null,
            "Authorization",
            string.Empty,
            null,
            options)
    {
        if (!IsSafeCredential(accessKeyId) ||
            !IsSafeCredential(secretAccessKey) ||
            (sessionToken is not null && !IsSafeCredential(sessionToken)) ||
            !NativeProviderEndpoint.IsSafeResourceSegment(region))
        {
            throw new ArgumentException("Bedrock credential configuration is invalid");
        }

        this.baseEndpoint = baseEndpoint;
        this.accessKeyId = accessKeyId;
        this.secretAccessKey = secretAccessKey;
        this.sessionToken = sessionToken;
        this.region = region;
    }

    /// <summary>
    /// 功能：只接受可安全进入 `/model/{model}/converse-stream` 的单路径段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="modelId">run/start 选择的 Bedrock 模型 ID。</param>
    /// <returns>字符与长度符合单段 allowlist 时为 true。</returns>
    public override bool SupportsModel(string modelId)
    {
        return NativeProviderEndpoint.IsSafeModelSegment(modelId);
    }

    /// <summary>
    /// 功能：构造 Bedrock 模型专属 ConverseStream 同源目标。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">已校验 Provider ID 与模型的公共请求。</param>
    /// <returns>不含 query 的 `/model/{model}/converse-stream` URI。</returns>
    protected override Uri CreateRequestEndpoint(ProviderRequest request)
    {
        if (!SupportsModel(request.Selection.ModelId))
        {
            throw CreateFailure("provider_request_target_invalid", retryable: false);
        }

        return NativeProviderEndpoint.Append(
            baseEndpoint,
            $"/model/{request.Selection.ModelId}/converse-stream");
    }

    /// <summary>
    /// 功能：把 portable 消息和工具定义映射为 Bedrock Converse 原生 JSON body。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">有序 portable 历史与本轮工具声明。</param>
    /// <returns>含 messages、可选 system 与 `toolConfig.tools[].toolSpec.inputSchema.json` 的 JSON object。</returns>
    /// <remarks>不变量：历史 tool use/result 关联 ID 保持；凭据不进入 body。</remarks>
    protected override JsonElement CreateRequestBody(ProviderRequest request)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["messages"] = MapMessages(request.Messages),
        };
        var system = MapSystem(request.Messages, request.SystemInstructions);
        if (system.Count > 0)
        {
            body["system"] = system;
        }

        if (request.Tools.Count > 0)
        {
            body["toolConfig"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tools"] = request.Tools.Select(static tool =>
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["toolSpec"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["name"] = tool.Name,
                            ["description"] = tool.Description,
                            ["inputSchema"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["json"] = tool.InputSchema.Clone(),
                            },
                        },
                    }).ToArray(),
            };
        }

        return JsonSerializer.SerializeToElement(body);
    }

    /// <summary>
    /// 功能：对精确 JSON body 计算 SHA-256，构造 SigV4 必需 headers 并请求 EventStream 响应。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="message">已设定同源 request-target 与 JSON content 的请求。</param>
    /// <param name="body">将实际发送的完整 JSON 字节。</param>
    /// <param name="request">已校验的公共 Provider 请求。</param>
    /// <remarks>不变量：canonical URI/query/headers 与最终 HTTP 值一致；不暴露任何签名中间值。</remarks>
    protected override void ConfigureRequestHeaders(
        HttpRequestMessage message,
        ReadOnlySpan<byte> body,
        ProviderRequest request)
    {
        _ = request;
        var endpoint = message.RequestUri ?? throw CreateFailure("provider_request_target_invalid", retryable: false);
        var now = DateTimeOffset.UtcNow;
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(body));
        var host = endpoint.IsDefaultPort ? endpoint.IdnHost : $"{endpoint.IdnHost}:{endpoint.Port}";
        var signedHeaderNames = sessionToken is null
            ? "content-type;host;x-amz-content-sha256;x-amz-date"
            : "content-type;host;x-amz-content-sha256;x-amz-date;x-amz-security-token";
        var canonicalHeaders = new StringBuilder()
            .Append("content-type:application/json; charset=utf-8\n")
            .Append("host:").Append(host).Append('\n')
            .Append("x-amz-content-sha256:").Append(payloadHash).Append('\n')
            .Append("x-amz-date:").Append(amzDate).Append('\n');
        if (sessionToken is not null)
        {
            canonicalHeaders.Append("x-amz-security-token:").Append(sessionToken).Append('\n');
        }

        var canonicalRequest = string.Join(
            '\n',
            "POST",
            endpoint.AbsolutePath,
            endpoint.Query.TrimStart('?'),
            canonicalHeaders.ToString(),
            signedHeaderNames,
            payloadHash);
        var scope = $"{dateStamp}/{region}/bedrock/aws4_request";
        var stringToSign = string.Join(
            '\n',
            "AWS4-HMAC-SHA256",
            amzDate,
            scope,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
        var secretSeed = Encoding.UTF8.GetBytes("AWS4" + secretAccessKey);
        var dateKey = Sign(Encoding.UTF8.GetBytes(dateStamp), secretSeed);
        var regionKey = Sign(Encoding.UTF8.GetBytes(region), dateKey);
        var serviceKey = Sign(Encoding.UTF8.GetBytes("bedrock"), regionKey);
        var signingKey = Sign(Encoding.UTF8.GetBytes("aws4_request"), serviceKey);
        var signatureBytes = Sign(Encoding.UTF8.GetBytes(stringToSign), signingKey);
        var signature = Convert.ToHexStringLower(signatureBytes);
        CryptographicOperations.ZeroMemory(secretSeed);
        CryptographicOperations.ZeroMemory(dateKey);
        CryptographicOperations.ZeroMemory(regionKey);
        CryptographicOperations.ZeroMemory(serviceKey);
        CryptographicOperations.ZeroMemory(signingKey);
        CryptographicOperations.ZeroMemory(signatureBytes);

        message.Headers.Accept.Clear();
        message.Headers.TryAddWithoutValidation("Accept", EventStreamMediaType);
        message.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        message.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        if (sessionToken is not null)
        {
            message.Headers.TryAddWithoutValidation("x-amz-security-token", sessionToken);
        }

        message.Headers.TryAddWithoutValidation(
            "Authorization",
            $"AWS4-HMAC-SHA256 Credential={accessKeyId}/{scope}, SignedHeaders={signedHeaderNames}, Signature={signature}");
    }

    /// <summary>
    /// 功能：以 idle timeout 增量解码 Bedrock 二进制 EventStream 并归一化文本、工具和 usage。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="response">成功且尚未读取的 EventStream HTTP 响应。</param>
    /// <param name="callerCancellation">run 本地取消信号。</param>
    /// <param name="totalCancellation">Provider 总时限信号。</param>
    /// <returns>保持 AWS 事件顺序的公共 Provider signals。</returns>
    /// <remarks>不变量：未通过长度/双 CRC/header/UTF-8/JSON 验证的帧绝不产生部分输出。</remarks>
    protected override async IAsyncEnumerable<ProviderSignal> ParseResponseAsync(
        HttpResponseMessage response,
        CancellationToken callerCancellation,
        [EnumeratorCancellation] CancellationToken totalCancellation)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(contentType, EventStreamMediaType, StringComparison.OrdinalIgnoreCase))
        {
            throw CreateFailure("provider_protocol_error", retryable: false);
        }

        var tools = new Dictionary<int, ToolAccumulator>();
        var state = new StreamState();
        Stream stream;
        try
        {
            stream = await response.Content.ReadAsStreamAsync(totalCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested)
        {
            throw CreateFailure("provider_total_timeout", retryable: false);
        }

        await using (stream.ConfigureAwait(false))
        {
            var decoder = new AwsEventStreamDecoder(1_048_576);
            var buffer = new byte[8192];
            while (true)
            {
                int read;
                using var idleCancellation = CancellationTokenSource.CreateLinkedTokenSource(totalCancellation);
                idleCancellation.CancelAfter(TransportOptions.IdleTimeout);
                try
                {
                    read = await stream.ReadAsync(buffer, idleCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (totalCancellation.IsCancellationRequested)
                {
                    throw CreateFailure("provider_total_timeout", retryable: false);
                }
                catch (OperationCanceledException)
                {
                    throw CreateFailure("provider_idle_timeout", retryable: false);
                }
                catch (IOException)
                {
                    throw CreateFailure("provider_disconnected", retryable: true, terminalStatus: "interrupted");
                }

                if (read == 0)
                {
                    try
                    {
                        decoder.Finish();
                    }
                    catch (InvalidDataException)
                    {
                        throw CreateFailure("provider_disconnected", retryable: true, terminalStatus: "interrupted");
                    }

                    break;
                }

                IReadOnlyList<AwsEventStreamMessage> messages;
                try
                {
                    messages = decoder.Feed(buffer.AsSpan(0, read));
                }
                catch (InvalidDataException)
                {
                    throw CreateFailure("provider_protocol_error", retryable: false);
                }

                foreach (var message in messages)
                {
                    foreach (var signal in ProcessEvent(message, tools, state))
                    {
                        yield return signal;
                    }
                }
            }
        }

        if (!state.MessageStopped || !state.MetadataSeen)
        {
            throw CreateFailure("provider_disconnected", retryable: true, terminalStatus: "interrupted");
        }

        foreach (var tool in tools.OrderBy(static pair => pair.Key).Select(static pair => pair.Value))
        {
            yield return new ProviderToolCallSignal(tool.Build());
        }
    }

    /// <summary>
    /// 功能：校验 EventStream 必需 header 并将单个 ConverseStream 事件映射为零个或多个 signal。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="message">已通过双 CRC 与 JSON 验证的 EventStream 消息。</param>
    /// <param name="tools">按 contentBlockIndex 累积的工具参数。</param>
    /// <param name="state">记录 messageStop 与 metadata 是否已出现的本响应状态。</param>
    /// <returns>该事件立即产生的文本或 usage signal。</returns>
    /// <exception cref="ProviderOperationException">必需 header/字段缺失、工具序列冲突或参数超限。</exception>
    private IEnumerable<ProviderSignal> ProcessEvent(
        AwsEventStreamMessage message,
        Dictionary<int, ToolAccumulator> tools,
        StreamState state)
    {
        if (!message.Headers.TryGetValue(":message-type", out var messageType) || messageType != "event" ||
            !message.Headers.TryGetValue(":event-type", out var eventType) || string.IsNullOrEmpty(eventType) ||
            !message.Headers.TryGetValue(":content-type", out var contentType) || contentType != "application/json" ||
            message.Headers.Count != 3)
        {
            throw CreateFailure("provider_protocol_error", retryable: false);
        }

        switch (eventType)
        {
            case "messageStart":
                if (ProviderJson.RequireString(message.Payload, "role") != "assistant")
                {
                    throw CreateFailure("provider_protocol_error", retryable: false);
                }

                break;
            case "contentBlockStart":
                var startIndex = RequireIndex(message.Payload);
                if (!message.Payload.TryGetProperty("start", out var start) ||
                    start.ValueKind != JsonValueKind.Object ||
                    !start.TryGetProperty("toolUse", out var toolUse) ||
                    toolUse.ValueKind != JsonValueKind.Object ||
                    tools.ContainsKey(startIndex))
                {
                    throw CreateFailure("provider_protocol_error", retryable: false);
                }

                tools.Add(startIndex, new ToolAccumulator(
                    ProviderJson.RequireString(toolUse, "toolUseId"),
                    ProviderJson.RequireString(toolUse, "name")));
                break;
            case "contentBlockDelta":
                var deltaIndex = RequireIndex(message.Payload);
                if (!message.Payload.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                {
                    throw CreateFailure("provider_protocol_error", retryable: false);
                }

                if (delta.TryGetProperty("text", out var text))
                {
                    if (text.ValueKind != JsonValueKind.String)
                    {
                        throw CreateFailure("provider_protocol_error", retryable: false);
                    }

                    var value = text.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        yield return new ProviderTextSignal(value);
                    }
                }

                if (delta.TryGetProperty("toolUse", out var deltaTool))
                {
                    if (!tools.TryGetValue(deltaIndex, out var tool) ||
                        deltaTool.ValueKind != JsonValueKind.Object ||
                        !deltaTool.TryGetProperty("input", out var input) ||
                        input.ValueKind != JsonValueKind.String)
                    {
                        throw CreateFailure("provider_protocol_error", retryable: false);
                    }

                    tool.Append(input.GetString()!);
                }

                break;
            case "contentBlockStop":
                _ = RequireIndex(message.Payload);
                break;
            case "messageStop":
                _ = ProviderJson.RequireString(message.Payload, "stopReason");
                state.MessageStopped = true;
                break;
            case "metadata":
                if (!message.Payload.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                {
                    throw CreateFailure("provider_protocol_error", retryable: false);
                }

                state.MetadataSeen = true;
                yield return new ProviderUsageSignal(ParseUsage(usage));
                break;
        }
    }

    /// <summary>
    /// 功能：把非 system portable 历史映射为 Bedrock user/assistant 消息和 toolUse/toolResult content。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="messages">按 durable 顺序排列的 portable 消息。</param>
    /// <returns>保持顺序的 Bedrock messages 数组。</returns>
    private static List<object> MapMessages(IReadOnlyList<JsonElement> messages)
    {
        var mapped = new List<object>();
        foreach (var message in messages)
        {
            var role = ProviderJson.RequireString(message, "role");
            if (role == "system")
            {
                continue;
            }

            if (role == "tool")
            {
                var status = message.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True
                    ? "error"
                    : "success";
                mapped.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["role"] = "user",
                    ["content"] = new[]
                    {
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["toolResult"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["toolUseId"] = ProviderJson.RequireString(message, "toolCallId"),
                                ["content"] = new[]
                                {
                                    new Dictionary<string, object?>(StringComparer.Ordinal)
                                    {
                                        ["text"] = ProviderJson.ExtractText(message),
                                    },
                                },
                                ["status"] = status,
                            },
                        },
                    },
                });
                continue;
            }

            var content = new List<object>();
            if (message.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in blocks.EnumerateArray())
                {
                    if (!block.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    if (type.GetString() == "text" && block.TryGetProperty("text", out var text))
                    {
                        content.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["text"] = text.GetString() ?? string.Empty,
                        });
                    }
                    else if (type.GetString() == "tool_call")
                    {
                        content.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["toolUse"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["toolUseId"] = ProviderJson.RequireString(block, "toolCallId"),
                                ["name"] = ProviderJson.RequireString(block, "name"),
                                ["input"] = block.GetProperty("arguments").Clone(),
                            },
                        });
                    }
                }
            }

            mapped.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["role"] = role == "assistant" ? "assistant" : "user",
                ["content"] = content,
            });
        }

        return mapped;
    }

    /// <summary>
    /// 功能：把所有 system 消息映射为 Bedrock system text 数组。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="messages">portable 上下文。</param>
    /// <param name="requestInstructions">可选 request-local Profile 指令。</param>
    /// <returns>非空 system text 项。</returns>
    private static List<object> MapSystem(
        IReadOnlyList<JsonElement> messages,
        string? requestInstructions)
    {
        var result = new List<object>();
        if (requestInstructions is not null)
        {
            result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["text"] = requestInstructions,
            });
        }

        result.AddRange(messages
            .Where(static message =>
                message.TryGetProperty("role", out var role) && role.GetString() == "system")
            .Select(static message => ProviderJson.ExtractText(message))
            .Where(static text => !string.IsNullOrEmpty(text))
            .Select(static text => (object)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["text"] = text,
            }));
        return result;
    }

    /// <summary>
    /// 功能：从 ConverseStream 事件取得非负 contentBlockIndex。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="payload">单个事件 payload object。</param>
    /// <returns>0..65535 的 content block index。</returns>
    private int RequireIndex(JsonElement payload)
    {
        if (!payload.TryGetProperty("contentBlockIndex", out var index) ||
            !index.TryGetInt32(out var value) ||
            value is < 0 or > 65_535)
        {
            throw CreateFailure("provider_protocol_error", retryable: false);
        }

        return value;
    }

    /// <summary>
    /// 功能：将 Bedrock metadata.usage 映射为公共 token 计数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="usage">Bedrock usage object。</param>
    /// <returns>含 input/output/total 的 Usage。</returns>
    private Usage ParseUsage(JsonElement usage)
    {
        if (!usage.TryGetProperty("inputTokens", out var input) || !input.TryGetInt64(out var inputValue) || inputValue < 0 ||
            !usage.TryGetProperty("outputTokens", out var output) || !output.TryGetInt64(out var outputValue) || outputValue < 0 ||
            !usage.TryGetProperty("totalTokens", out var total) || !total.TryGetInt64(out var totalValue) || totalValue < 0)
        {
            throw CreateFailure("provider_protocol_error", retryable: false);
        }

        return new Usage(inputValue, outputValue, totalValue);
    }

    /// <summary>
    /// 功能：拒绝空、过大或含 header 控制字符的 AWS 凭据部件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">只在进程内存使用的 credential 字符串。</param>
    /// <returns>长度 1..16384 且不含 CR/LF/NUL 时为 true。</returns>
    private static bool IsSafeCredential(string value)
    {
        return value.Length is >= 1 and <= 16_384 && value.IndexOfAny(['\r', '\n', '\0']) < 0;
    }

    /// <summary>
    /// 功能：用给定 HMAC-SHA256 key 签名一段 SigV4 派生数据。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="data">当前派生步骤文本字节。</param>
    /// <param name="key">上一派生步骤 key。</param>
    /// <returns>32 字节 HMAC 结果。</returns>
    private static byte[] Sign(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        return HMACSHA256.HashData(key, data);
    }

    /// <summary>
    /// 功能：保存单个 Bedrock HTTP 响应的必需终止事件观测状态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class StreamState
    {
        /// <summary>
        /// 功能：取得或设置是否已处理 messageStop。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        internal bool MessageStopped { get; set; }

        /// <summary>
        /// 功能：取得或设置是否已处理带 usage 的 metadata。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        internal bool MetadataSeen { get; set; }
    }

    /// <summary>
    /// 功能：按 content block 顺序累积 Bedrock partial tool input JSON。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class ToolAccumulator
    {
        private readonly StringBuilder arguments = new();

        /// <summary>
        /// 功能：创建绑定不可变 toolUseId 与 name 的参数累积器。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="callId">Bedrock toolUseId。</param>
        /// <param name="name">规范工具名。</param>
        internal ToolAccumulator(string callId, string name)
        {
            CallId = callId;
            Name = name;
        }

        private string CallId { get; }

        private string Name { get; }

        /// <summary>
        /// 功能：追加一段 Bedrock toolUse.input JSON 字符串。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="fragment">按 wire 顺序到达的参数片段。</param>
        /// <exception cref="ProviderOperationException">累积后超过 1 MiB 边界。</exception>
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
        /// 功能：严格解析累积 JSON object 并构造公共工具调用。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <returns>具有完整参数的 ProviderToolCall。</returns>
        internal ProviderToolCall Build()
        {
            return new ProviderToolCall(CallId, Name, ProviderJson.ParseToolArguments(arguments));
        }
    }
}
