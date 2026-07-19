using System.Runtime.CompilerServices;
using System.Text.Json;
using QxnmForge.Domain;
using QxnmForge.Serialization;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：表示发送给原生 faux Provider 的完整请求，包含场景和从 durable Session 重建的有序消息上下文。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Scenario">本次请求要执行的确定性 faux 场景。</param>
/// <param name="Messages">按选定 Session 分支顺序排列的 portable user、assistant 与 tool 消息。</param>
public sealed record FauxProviderRequest(
    FauxScenario Scenario,
    IReadOnlyList<JsonElement> Messages);

/// <summary>
/// 功能：包装 faux 场景要求产生的结构化 Provider 失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class FauxProviderException : ProviderOperationException
{
    /// <summary>
    /// 功能：使用已脱敏 portable error 创建 faux Provider 异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="error">安全且机器可读的错误。</param>
    public FauxProviderException(PortableError error)
        : base(error)
    {
    }
}

/// <summary>
/// 功能：表示 faux 场景模拟的未知结果断流。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class FauxDisconnectException : ProviderOperationException
{
    /// <summary>
    /// 功能：创建不携带敏感 Provider 数据的断流异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="reason">可选安全说明。</param>
    public FauxDisconnectException(string? reason)
        : base(
            new PortableError(
                -32005,
                reason ?? "faux provider disconnected",
                true,
                new ErrorDetails("provider_disconnected", ProviderId: "faux", ModelId: "faux-v1")),
            "interrupted")
    {
    }
}

/// <summary>
/// 功能：使用 IAsyncEnumerable 原生执行确定性离线 Provider 场景。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class FauxProvider : IProvider
{
    private readonly TimeProvider timeProvider;
    private readonly Action<FauxProviderRequest>? requestObserver;

    /// <summary>
    /// 功能：创建使用系统时间或测试时间源的确定性 faux Provider。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="timeProvider">仅用于 delay 步骤调度的时间源。</param>
    /// <param name="requestObserver">可选原生请求观察器，供确定性测试确认 Provider 收到完整历史；不得修改请求。</param>
    public FauxProvider(
        TimeProvider? timeProvider = null,
        Action<FauxProviderRequest>? requestObserver = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.requestObserver = requestObserver;
    }

    /// <summary>
    /// 功能：取得确定性离线 Provider ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string Id => "faux";

    /// <summary>
    /// 功能：取得 faux 唯一静态模型。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public IReadOnlyList<string> Models { get; } = ["faux-v1"];

    /// <summary>
    /// 功能：判断模型是否为 faux-v1。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="modelId">模型 ID。</param>
    /// <returns>精确匹配 faux-v1 时为 true。</returns>
    public bool SupportsModel(string modelId)
    {
        return string.Equals(modelId, "faux-v1", StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：通过公共 IProvider 接口执行 faux 场景，同时保留 expectedContext 黑盒断言。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">必须选择 faux/faux-v1 并携带 FauxScenario。</param>
    /// <param name="cancellationToken">贯穿场景延迟和流的取消信号。</param>
    /// <returns>公共规范化 Provider 信号。</returns>
    /// <exception cref="ProviderOperationException">请求缺少场景或选择不匹配。</exception>
    public async IAsyncEnumerable<ProviderSignal> StreamAsync(
        ProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request.Selection.Id != Id ||
            !SupportsModel(request.Selection.ModelId) ||
            request.FauxScenario is null)
        {
            throw new ProviderOperationException(new PortableError(
                -32602,
                "faux provider request is invalid",
                false,
                new ErrorDetails("invalid_params", ProviderId: Id, ModelId: request.Selection.ModelId)));
        }

        await foreach (var signal in StreamAsync(
                           new FauxProviderRequest(request.FauxScenario, request.Messages),
                           cancellationToken).ConfigureAwait(false))
        {
            yield return signal;
        }
    }

    /// <summary>
    /// 功能：依场景顺序流式产生文本或完整工具调用，并在每个 await 边界响应取消。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="scenario">已严格验证的 faux v0.1 场景。</param>
    /// <param name="cancellationToken">用于延迟及整个流的取消信号。</param>
    /// <returns>保持步骤顺序的原生异步信号流。</returns>
    /// <remarks>不变量：不访问网络、文件或其他语言运行时；相同场景产生相同信号。</remarks>
    /// <exception cref="FauxProviderException">场景包含 error 步骤。</exception>
    /// <exception cref="FauxDisconnectException">场景包含 disconnect 步骤。</exception>
    /// <exception cref="OperationCanceledException">取消在下一个 I/O/await 边界被观察。</exception>
    public async IAsyncEnumerable<ProviderSignal> StreamAsync(
        FauxScenario scenario,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var signal in StreamAsync(
                           new FauxProviderRequest(scenario, Array.Empty<JsonElement>()),
                           cancellationToken).ConfigureAwait(false))
        {
            yield return signal;
        }
    }

    /// <summary>
    /// 功能：接收包含 durable 历史的完整 Provider 请求，并依场景顺序流式产生规范化信号。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">场景以及从 Session 无损重建的 portable 消息上下文。</param>
    /// <param name="cancellationToken">用于观察器、延迟及整个流的取消信号。</param>
    /// <returns>保持步骤顺序的原生异步信号流。</returns>
    /// <remarks>不变量：观察器只收到独立 JsonElement；Provider 不执行历史中的工具调用。</remarks>
    /// <exception cref="FauxProviderException">场景包含 error 步骤。</exception>
    /// <exception cref="FauxDisconnectException">场景包含 disconnect 步骤。</exception>
    /// <exception cref="OperationCanceledException">取消在下一个 I/O/await 边界被观察。</exception>
    public async IAsyncEnumerable<ProviderSignal> StreamAsync(
        FauxProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateExpectedContext(request);
        requestObserver?.Invoke(request);
        foreach (var step in request.Scenario.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (step)
            {
                case FauxTextStep text when text.Text.Length > 0:
                    yield return new ProviderTextSignal(text.Text);
                    break;
                case FauxTextStep:
                    break;
                case FauxDelayStep delay:
                    await Task.Delay(TimeSpan.FromMilliseconds(delay.DurationMs), timeProvider, cancellationToken).ConfigureAwait(false);
                    break;
                case FauxToolCallStep toolCall:
                    yield return new ProviderToolCallSignal(new ProviderToolCall(
                        toolCall.ToolCallId,
                        toolCall.Name,
                        toolCall.Arguments.Clone()));
                    break;
                case FauxErrorStep error:
                    throw new FauxProviderException(error.Error);
                case FauxDisconnectStep disconnect:
                    throw new FauxDisconnectException(disconnect.Reason);
                default:
                    throw new InvalidOperationException("unknown validated faux step");
            }
        }

        if (request.Scenario.Usage is not null)
        {
            yield return new ProviderUsageSignal(request.Scenario.Usage);
        }
    }

    /// <summary>
    /// 功能：把 durable 完整消息归一为 faux expectedContext 形状并执行严格有序黑盒断言。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">包含可选 expectedContext 和实际完整消息的 Provider 请求。</param>
    /// <exception cref="FauxProviderException">消息数量、顺序、角色、内容或 tool 元数据不一致。</exception>
    private static void ValidateExpectedContext(FauxProviderRequest request)
    {
        var expected = request.Scenario.ExpectedContext;
        if (expected is null)
        {
            return;
        }

        if (expected.Count != request.Messages.Count)
        {
            throw CreateContextMismatch();
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var actual = NormalizeContextMessage(request.Messages[index]);
            if (!JsonEquivalent(expected[index], actual))
            {
                throw CreateContextMismatch();
            }
        }
    }

    /// <summary>
    /// 功能：从完整 portable message 删除 messageId、时间、Provider 等非上下文字段，同时保留 content 和 tool 关联。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="message">从 journal 无损恢复的 user、assistant 或 tool 消息。</param>
    /// <returns>符合 faux contextMessage schema 的独立 JSON。</returns>
    /// <exception cref="FauxProviderException">消息不含可识别角色、content 或 tool 元数据。</exception>
    private static JsonElement NormalizeContextMessage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("role", out var roleElement) ||
            roleElement.ValueKind != JsonValueKind.String ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            throw CreateContextMismatch();
        }

        var role = roleElement.GetString();
        if (role == "tool")
        {
            if (!message.TryGetProperty("toolCallId", out var toolCallId) ||
                toolCallId.ValueKind != JsonValueKind.String ||
                !message.TryGetProperty("toolName", out var toolName) ||
                toolName.ValueKind != JsonValueKind.String ||
                !message.TryGetProperty("isError", out var isError) ||
                isError.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw CreateContextMismatch();
            }

            return JsonSerializer.SerializeToElement(
                new
                {
                    Role = role,
                    Content = content.Clone(),
                    ToolCallId = toolCallId.GetString(),
                    ToolName = toolName.GetString(),
                    IsError = isError.GetBoolean(),
                },
                JsonDefaults.Options);
        }

        if (role is not ("user" or "assistant"))
        {
            throw CreateContextMismatch();
        }

        return JsonSerializer.SerializeToElement(
            new { Role = role, Content = content.Clone() },
            JsonDefaults.Options);
    }

    /// <summary>
    /// 功能：按 JSON 值语义比较对象属性、数组顺序和标量，不依赖原始属性顺序或空白。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="expected">expectedContext 值。</param>
    /// <param name="actual">由 durable message 归一化的实际值。</param>
    /// <returns>语义相同返回 true。</returns>
    private static bool JsonEquivalent(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            return false;
        }

        if (expected.ValueKind == JsonValueKind.Object)
        {
            var expectedProperties = expected.EnumerateObject().ToArray();
            var actualProperties = actual.EnumerateObject().ToArray();
            return expectedProperties.Length == actualProperties.Length &&
                expectedProperties.All(property =>
                    actual.TryGetProperty(property.Name, out var actualValue) &&
                    JsonEquivalent(property.Value, actualValue));
        }

        if (expected.ValueKind == JsonValueKind.Array)
        {
            var expectedItems = expected.EnumerateArray().ToArray();
            var actualItems = actual.EnumerateArray().ToArray();
            return expectedItems.Length == actualItems.Length &&
                expectedItems
                    .Zip(actualItems)
                    .All(pair => JsonEquivalent(pair.First, pair.Second));
        }

        return JsonElement.DeepEquals(expected, actual);
    }

    /// <summary>
    /// 功能：创建不泄露期望或实际消息正文的 faux context mismatch 结构化异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>稳定且不可重试的 Provider 错误。</returns>
    private static FauxProviderException CreateContextMismatch()
    {
        return new FauxProviderException(new PortableError(
            -32602,
            "faux provider context did not match expectedContext",
            false,
            new ErrorDetails("faux_context_mismatch")));
    }
}
