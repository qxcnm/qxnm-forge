using System.Buffers;
using System.Globalization;
using System.Text.Json;
using QxnmForge.Domain;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：把严格 wire JSON 转换为确定性 faux 场景 DTO。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public static class FauxScenarioParser
{
    private static readonly SearchValues<char> ValidNameCharacters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-");

    /// <summary>
    /// 功能：严格解析并验证 faux-scenario v0.1，拒绝未知字段和越界值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">必须为场景对象的 JSON 元素。</param>
    /// <returns>不可变、可由原生 Provider 执行的场景。</returns>
    /// <remarks>不变量：返回步骤保持源顺序；失败时不产生持久化或 Provider 副作用。</remarks>
    /// <exception cref="ArgumentException">对象缺字段、含未知字段、类型错误或违反 schema 边界。</exception>
    public static FauxScenario Parse(JsonElement element)
    {
        RequireObject(element, "scenario");
        RejectUnknown(
            element,
            "scenario",
            "schemaVersion",
            "name",
            "seed",
            "steps",
            "expectedContext",
            "usage",
            "continuations",
            "extensions");

        var schemaVersion = RequireString(element, "schemaVersion");
        if (!string.Equals(schemaVersion, "0.1", StringComparison.Ordinal))
        {
            throw new ArgumentException("scenario.schemaVersion must equal 0.1");
        }

        var name = RequireString(element, "name");
        if (name.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(name[0]) || name.AsSpan().ContainsAnyExcept(ValidNameCharacters))
        {
            throw new ArgumentException("scenario.name is invalid");
        }

        var seedElement = RequireProperty(element, "seed");
        if (!seedElement.TryGetUInt32(out var seed))
        {
            throw new ArgumentException("scenario.seed must be an unsigned 32-bit integer");
        }

        var steps = ParseSteps(element, "scenario");

        Usage? usage = null;
        if (element.TryGetProperty("usage", out var usageElement))
        {
            usage = ParseUsage(usageElement);
        }

        var expectedContext = ParseExpectedContext(element);
        var continuations = ParseContinuations(element);
        return new FauxScenario(schemaVersion, name, seed, steps, usage, expectedContext, continuations);
    }

    /// <summary>
    /// 功能：解析对象的必需 steps 数组并保持 Provider 源顺序。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="owner">scenario 或 continuation object。</param>
    /// <param name="context">安全诊断上下文。</param>
    /// <returns>最多 10000 个封闭 faux 步骤。</returns>
    private static List<FauxStep> ParseSteps(JsonElement owner, string context)
    {
        var stepsElement = RequireProperty(owner, "steps");
        if (stepsElement.ValueKind != JsonValueKind.Array || stepsElement.GetArrayLength() > 10_000)
        {
            throw new ArgumentException(context + ".steps must be an array of at most 10000 items");
        }

        var steps = new List<FauxStep>(stepsElement.GetArrayLength());
        foreach (var step in stepsElement.EnumerateArray())
        {
            steps.Add(ParseStep(step));
        }

        return steps;
    }

    /// <summary>
    /// 功能：解析最多 100 个 FIFO faux continuation turn。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="scenario">完整场景 object。</param>
    /// <returns>缺失时为 null，否则为不可变续段列表。</returns>
    private static List<FauxContinuation>? ParseContinuations(JsonElement scenario)
    {
        if (!scenario.TryGetProperty("continuations", out var continuations))
        {
            return null;
        }

        if (continuations.ValueKind != JsonValueKind.Array || continuations.GetArrayLength() > 100)
        {
            throw new ArgumentException("scenario.continuations must be an array of at most 100 items");
        }

        var result = new List<FauxContinuation>(continuations.GetArrayLength());
        foreach (var continuation in continuations.EnumerateArray())
        {
            RequireObject(continuation, "continuation");
            RejectUnknown(continuation, "continuation", "steps", "expectedContext", "usage", "extensions");
            Usage? usage = null;
            if (continuation.TryGetProperty("usage", out var usageElement))
            {
                usage = ParseUsage(usageElement);
            }

            result.Add(new FauxContinuation(
                ParseSteps(continuation, "continuation"),
                ParseExpectedContext(continuation),
                usage));
        }

        return result;
    }

    /// <summary>
    /// 功能：解析 faux expectedContext 黑盒断言，并保留每个规范化消息内容的 JSON 结构。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="scenario">完整场景对象。</param>
    /// <returns>字段缺失时为 null，否则为独立 JsonElement 消息数组。</returns>
    /// <exception cref="ArgumentException">数组、角色、tool 元数据或 content 形状无效。</exception>
    private static List<JsonElement>? ParseExpectedContext(JsonElement scenario)
    {
        if (!scenario.TryGetProperty("expectedContext", out var expectedContext))
        {
            return null;
        }

        if (expectedContext.ValueKind != JsonValueKind.Array || expectedContext.GetArrayLength() > 10_000)
        {
            throw new ArgumentException("scenario.expectedContext must be an array of at most 10000 items");
        }

        var messages = new List<JsonElement>(expectedContext.GetArrayLength());
        foreach (var message in expectedContext.EnumerateArray())
        {
            RequireObject(message, "expected context message");
            RejectUnknown(
                message,
                "expected context message",
                "role",
                "content",
                "toolCallId",
                "toolName",
                "isError");
            var role = RequireString(message, "role");
            if (role is not ("user" or "assistant" or "tool"))
            {
                throw new ArgumentException("expected context role is invalid");
            }

            var content = RequireProperty(message, "content");
            if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() > 1024)
            {
                throw new ArgumentException("expected context content is invalid");
            }

            foreach (var item in content.EnumerateArray())
            {
                RequireObject(item, "expected context content");
                _ = RequireString(item, "type");
            }

            if (role == "tool")
            {
                _ = RequireString(message, "toolCallId");
                _ = RequireString(message, "toolName");
                var isError = RequireProperty(message, "isError");
                if (isError.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new ArgumentException("expected context isError must be boolean");
                }
            }
            else if (message.TryGetProperty("toolCallId", out _) ||
                     message.TryGetProperty("toolName", out _) ||
                     message.TryGetProperty("isError", out _))
            {
                throw new ArgumentException("non-tool expected context contains tool metadata");
            }

            messages.Add(message.Clone());
        }

        return messages;
    }

    /// <summary>
    /// 功能：按 type 判别并解析单个 faux 步骤。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">步骤对象。</param>
    /// <returns>对应的封闭步骤 DTO。</returns>
    /// <exception cref="ArgumentException">步骤无效或类型未知。</exception>
    private static FauxStep ParseStep(JsonElement element)
    {
        RequireObject(element, "scenario step");
        var type = RequireString(element, "type");
        return type switch
        {
            "text" => ParseTextStep(element),
            "tool_call" => ParseToolCallStep(element),
            "error" => ParseErrorStep(element),
            "delay" => ParseDelayStep(element),
            "disconnect" => ParseDisconnectStep(element),
            _ => throw new ArgumentException("scenario step type is unsupported"),
        };
    }

    /// <summary>
    /// 功能：解析文本输出步骤。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">文本步骤对象。</param>
    /// <returns>文本步骤 DTO。</returns>
    private static FauxTextStep ParseTextStep(JsonElement element)
    {
        RejectUnknown(element, "text step", "type", "text");
        return new FauxTextStep(RequireString(element, "text", allowEmpty: true));
    }

    /// <summary>
    /// 功能：解析具有完整 JSON 参数的工具调用步骤。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">工具调用步骤对象。</param>
    /// <returns>工具调用步骤 DTO。</returns>
    private static FauxToolCallStep ParseToolCallStep(JsonElement element)
    {
        RejectUnknown(element, "tool-call step", "type", "toolCallId", "name", "arguments");
        var arguments = RequireProperty(element, "arguments");
        RequireObject(arguments, "tool-call arguments");
        return new FauxToolCallStep(
            RequireString(element, "toolCallId"),
            RequireString(element, "name"),
            arguments.Clone());
    }

    /// <summary>
    /// 功能：解析结构化 Provider 失败步骤。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">错误步骤对象。</param>
    /// <returns>错误步骤 DTO。</returns>
    private static FauxErrorStep ParseErrorStep(JsonElement element)
    {
        RejectUnknown(element, "error step", "type", "error");
        var errorElement = RequireProperty(element, "error");
        RequireObject(errorElement, "error");
        RejectUnknown(errorElement, "error", "code", "message", "retryable", "details");
        var detailsElement = RequireProperty(errorElement, "details");
        RequireObject(detailsElement, "error.details");
        RejectUnknown(detailsElement, "error.details", "kind", "field", "providerId", "modelId", "supportedVersions");
        var codeElement = RequireProperty(errorElement, "code");
        if (!codeElement.TryGetInt32(out var code) || code is >= 0 or < -32_768)
        {
            throw new ArgumentException("error.code is invalid");
        }

        var retryElement = RequireProperty(errorElement, "retryable");
        if (retryElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException("error.retryable must be boolean");
        }

        var details = new ErrorDetails(
            RequireString(detailsElement, "kind"),
            OptionalString(detailsElement, "field"),
            OptionalString(detailsElement, "providerId"),
            OptionalString(detailsElement, "modelId"),
            OptionalStringArray(detailsElement, "supportedVersions"));
        return new FauxErrorStep(new PortableError(
            code,
            RequireString(errorElement, "message"),
            retryElement.GetBoolean(),
            details));
    }

    /// <summary>
    /// 功能：解析有上限且可取消的延迟步骤。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">延迟步骤对象。</param>
    /// <returns>延迟步骤 DTO。</returns>
    private static FauxDelayStep ParseDelayStep(JsonElement element)
    {
        RejectUnknown(element, "delay step", "type", "durationMs");
        var value = RequireProperty(element, "durationMs");
        if (!value.TryGetInt32(out var durationMs) || durationMs is < 0 or > 60_000)
        {
            throw new ArgumentException("delay durationMs is invalid");
        }

        return new FauxDelayStep(durationMs);
    }

    /// <summary>
    /// 功能：解析确定性断流步骤。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">断流步骤对象。</param>
    /// <returns>断流步骤 DTO。</returns>
    private static FauxDisconnectStep ParseDisconnectStep(JsonElement element)
    {
        RejectUnknown(element, "disconnect step", "type", "reason");
        var reason = OptionalString(element, "reason");
        if (reason?.Length > 4096)
        {
            throw new ArgumentException("disconnect reason is too long");
        }

        return new FauxDisconnectStep(reason);
    }

    /// <summary>
    /// 功能：解析三项非负且总数一致的 token 用量。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">usage 对象。</param>
    /// <returns>规范化用量 DTO。</returns>
    private static Usage ParseUsage(JsonElement element)
    {
        RequireObject(element, "usage");
        RejectUnknown(element, "usage", "inputTokens", "outputTokens", "totalTokens");
        return new Usage(
            RequireNonNegativeInteger(element, "inputTokens"),
            RequireNonNegativeInteger(element, "outputTokens"),
            RequireNonNegativeInteger(element, "totalTokens"));
    }

    /// <summary>
    /// 功能：读取必需的非负安全整数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>0 到 JavaScript 安全整数上限内的值。</returns>
    private static long RequireNonNegativeInteger(JsonElement element, string name)
    {
        var value = RequireProperty(element, name);
        if (!value.TryGetInt64(out var parsed) || parsed is < 0 or > 9_007_199_254_740_991)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"{name} must be a non-negative safe integer"));
        }

        return parsed;
    }

    /// <summary>
    /// 功能：要求 JSON 值为对象。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">待检查元素。</param>
    /// <param name="context">安全诊断上下文。</param>
    private static void RequireObject(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(context + " must be an object");
        }
    }

    /// <summary>
    /// 功能：取得必需 JSON 属性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>属性元素。</returns>
    private static JsonElement RequireProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            throw new ArgumentException(name + " is required");
        }

        return property;
    }

    /// <summary>
    /// 功能：读取必需字符串并按需允许空串。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <param name="allowEmpty">是否允许空字符串。</param>
    /// <returns>字符串值。</returns>
    private static string RequireString(JsonElement element, string name, bool allowEmpty = false)
    {
        var property = RequireProperty(element, name);
        if (property.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(name + " must be a string");
        }

        var value = property.GetString()!;
        if (!allowEmpty && value.Length == 0)
        {
            throw new ArgumentException(name + " must not be empty");
        }

        return value;
    }

    /// <summary>
    /// 功能：读取可选字符串属性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>缺失时为 null，否则为字符串。</returns>
    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(name + " must be a string");
        }

        return property.GetString();
    }

    /// <summary>
    /// 功能：读取可选的非空字符串数组。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>缺失时为 null，否则为字符串列表。</returns>
    private static List<string>? OptionalStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException(name + " must be an array");
        }

        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(item.GetString()))
            {
                throw new ArgumentException(name + " must contain non-empty strings");
            }

            values.Add(item.GetString()!);
        }

        return values;
    }

    /// <summary>
    /// 功能：拒绝核心 schema 未命名的字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">待检查对象。</param>
    /// <param name="context">安全诊断上下文。</param>
    /// <param name="allowed">允许字段集合。</param>
    private static void RejectUnknown(JsonElement element, string context, params ReadOnlySpan<string> allowed)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new ArgumentException(context + " contains an unknown field");
            }
        }
    }
}
