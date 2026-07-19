using System.Reflection;
using System.Text.Json;
using QxnmForge.Domain;
using QxnmForge.Provider;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证六类 live Provider 对工具声明、历史调用、结果和 request-target 使用各自原生 wire 字段。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ProviderRequestMappingTests
{
    private static readonly string[] RequiredPath = ["path"];

    /// <summary>
    /// 功能：确认 Chat Completions 使用 function.parameters、tool_calls 和 tool_call_id。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void OpenAiChatUsesNativeToolFieldNames()
    {
        using var provider = new OpenAiChatProvider(
            new Uri("https://example.invalid/v1/chat/completions"),
            null,
            ProviderTransportOptions.Default);

        var body = CreateRequestBody(provider, CreateRequest(provider.Id));
        var tool = Assert.Single(body.GetProperty("tools").EnumerateArray());
        var function = tool.GetProperty("function");
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("file.read", function.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, function.GetProperty("parameters").ValueKind);
        Assert.False(function.TryGetProperty("Parameters", out _));

        var messages = body.GetProperty("messages").EnumerateArray().ToArray();
        var historicalCall = Assert.Single(messages[1].GetProperty("tool_calls").EnumerateArray());
        Assert.Equal("call-1", historicalCall.GetProperty("id").GetString());
        Assert.Equal("file.read", historicalCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("call-1", messages[2].GetProperty("tool_call_id").GetString());
        Assert.False(messages[2].TryGetProperty("ToolCallId", out _));
    }

    /// <summary>
    /// 功能：确认 Responses 使用 call_id、function_call_output 和小写 function 工具字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void OpenAiResponsesUsesNativeToolFieldNames()
    {
        using var provider = new OpenAiResponsesProvider(
            new Uri("https://example.invalid/v1/responses"),
            null,
            ProviderTransportOptions.Default);

        var body = CreateRequestBody(provider, CreateRequest(provider.Id));
        var tool = Assert.Single(body.GetProperty("tools").EnumerateArray());
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("file.read", tool.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, tool.GetProperty("parameters").ValueKind);
        Assert.False(tool.TryGetProperty("Parameters", out _));

        var input = body.GetProperty("input").EnumerateArray().ToArray();
        var historicalCall = Assert.Single(input, static item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "function_call");
        var historicalResult = Assert.Single(input, static item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
        Assert.Equal("call-1", historicalCall.GetProperty("call_id").GetString());
        Assert.Equal("call-1", historicalResult.GetProperty("call_id").GetString());
        Assert.False(historicalResult.TryGetProperty("CallId", out _));
    }

    /// <summary>
    /// 功能：确认 Anthropic 使用 input_schema、tool_use_id 与 is_error 原生字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void AnthropicMessagesUsesNativeToolFieldNames()
    {
        using var provider = new AnthropicMessagesProvider(
            new Uri("https://example.invalid/v1/messages"),
            null,
            ProviderTransportOptions.Default);

        var body = CreateRequestBody(provider, CreateRequest(provider.Id));
        var tool = Assert.Single(body.GetProperty("tools").EnumerateArray());
        Assert.Equal("file.read", tool.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, tool.GetProperty("input_schema").ValueKind);
        Assert.False(tool.TryGetProperty("InputSchema", out _));

        var messages = body.GetProperty("messages").EnumerateArray().ToArray();
        var toolUse = Assert.Single(
            messages[1].GetProperty("content").EnumerateArray(),
            static block => block.GetProperty("type").GetString() == "tool_use");
        var toolResult = Assert.Single(messages[2].GetProperty("content").EnumerateArray());
        Assert.Equal("call-1", toolUse.GetProperty("id").GetString());
        Assert.Equal("call-1", toolResult.GetProperty("tool_use_id").GetString());
        Assert.False(toolResult.GetProperty("is_error").GetBoolean());
        Assert.False(toolResult.TryGetProperty("ToolUseId", out _));
    }

    /// <summary>
    /// 功能：确认 Mistral 使用独立原生路径、strict:false function tools 且不携带 OpenAI stream_options。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void MistralConversationsUsesNativeTargetAndContinuationFields()
    {
        using var provider = new MistralConversationsProvider(
            new Uri("https://example.invalid/base"),
            null,
            ProviderTransportOptions.Default);

        var request = CreateRequest(provider.Id);
        var body = CreateRequestBody(provider, request);
        Assert.False(body.TryGetProperty("stream_options", out _));
        var tool = Assert.Single(body.GetProperty("tools").EnumerateArray());
        var function = tool.GetProperty("function");
        Assert.Equal("file.read", function.GetProperty("name").GetString());
        Assert.False(function.GetProperty("strict").GetBoolean());
        var messages = body.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("call-1", messages[2].GetProperty("tool_call_id").GetString());

        var endpoint = CreateRequestEndpoint(provider, request);
        Assert.Equal("https://example.invalid/base/v1/chat/completions", endpoint.AbsoluteUri);
    }

    /// <summary>
    /// 功能：确认 Azure Responses 使用 api-version 目标、store:false、非严格工具和 function_call_output 续轮。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void AzureResponsesUsesNativeTargetAndContinuationFields()
    {
        using var provider = new AzureOpenAiResponsesProvider(
            new Uri("https://resource.example.invalid/openai/v1"),
            "v1",
            null,
            ProviderTransportOptions.Default);

        var request = CreateRequest(provider.Id);
        var body = CreateRequestBody(provider, request);
        Assert.False(body.GetProperty("store").GetBoolean());
        var tool = Assert.Single(body.GetProperty("tools").EnumerateArray());
        Assert.Equal("file.read", tool.GetProperty("name").GetString());
        Assert.False(tool.GetProperty("strict").GetBoolean());
        var input = body.GetProperty("input").EnumerateArray().ToArray();
        var result = Assert.Single(input, static item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
        Assert.Equal("call-1", result.GetProperty("call_id").GetString());

        var endpoint = CreateRequestEndpoint(provider, request);
        Assert.Equal("/openai/v1/responses", endpoint.AbsolutePath);
        Assert.Equal("?api-version=v1", endpoint.Query);
        Assert.Equal("resource.example.invalid", endpoint.Host);
    }

    /// <summary>
    /// 功能：确认 Google 使用 contents、functionDeclarations、functionResponse 和安全模型路径目标。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void GoogleGenerativeAiUsesNativeTargetAndContinuationFields()
    {
        using var provider = new GoogleGenerativeAiProvider(
            new Uri("https://generativelanguage.googleapis.com/v1beta"),
            null,
            ProviderTransportOptions.Default);

        var request = CreateRequest(provider.Id);
        var body = CreateRequestBody(provider, request);
        Assert.False(body.TryGetProperty("model", out _));
        Assert.False(body.TryGetProperty("stream", out _));
        var tools = Assert.Single(body.GetProperty("tools").EnumerateArray());
        var declaration = Assert.Single(tools.GetProperty("functionDeclarations").EnumerateArray());
        Assert.Equal("file.read", declaration.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, declaration.GetProperty("parametersJsonSchema").ValueKind);

        var contents = body.GetProperty("contents").EnumerateArray().ToArray();
        Assert.Equal(["user", "model", "user"], contents.Select(static item => item.GetProperty("role").GetString()));
        var modelCall = Assert.Single(
                contents[1].GetProperty("parts").EnumerateArray(),
                static part => part.TryGetProperty("functionCall", out _))
            .GetProperty("functionCall");
        Assert.Equal("call-1", modelCall.GetProperty("id").GetString());
        var toolResult = Assert.Single(contents[2].GetProperty("parts").EnumerateArray())
            .GetProperty("functionResponse");
        Assert.Equal("call-1", toolResult.GetProperty("id").GetString());
        Assert.Equal("文件内容", toolResult.GetProperty("response").GetProperty("output").GetString());

        var endpoint = CreateRequestEndpoint(provider, request);
        Assert.Equal("/v1beta/models/mock-model:streamGenerateContent", endpoint.AbsolutePath);
        Assert.Equal("?alt=sse", endpoint.Query);
        Assert.False(provider.SupportsModel("../escape"));
        Assert.False(provider.SupportsModel("gemini?alt=json"));
        Assert.True(provider.SupportsModel("gemini-2.5-pro"));
    }

    /// <summary>
    /// 功能：确认 Vertex 保持 GenerateContent body，但使用独立项目/location/publisher 资源路径。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void GoogleVertexUsesNativeResourceTargetAndToolSchema()
    {
        using var provider = new GoogleVertexProvider(
            new Uri("https://us-central1-aiplatform.googleapis.com"),
            "mock-project",
            "us-central1",
            null,
            ProviderTransportOptions.Default);
        var request = CreateRequest(provider.Id) with
        {
            Selection = new ProviderSelection(provider.Id, "mock-vertex-v1"),
        };
        var body = CreateRequestBody(provider, request);
        var tools = Assert.Single(body.GetProperty("tools").EnumerateArray());
        var declaration = Assert.Single(tools.GetProperty("functionDeclarations").EnumerateArray());
        Assert.Equal("file.read", declaration.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, declaration.GetProperty("parametersJsonSchema").ValueKind);

        var endpoint = CreateRequestEndpoint(provider, request);
        Assert.Equal(
            "/v1/projects/mock-project/locations/us-central1/publishers/google/models/mock-vertex-v1:streamGenerateContent",
            endpoint.AbsolutePath);
        Assert.Equal("?alt=sse", endpoint.Query);
    }

    /// <summary>
    /// 功能：确认 Bedrock 使用 messages/toolConfig/toolSpec 原生字段并保持工具续轮。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void BedrockUsesNativeConverseToolFieldsAndTarget()
    {
        using var provider = new BedrockConverseStreamProvider(
            new Uri("https://bedrock-runtime.us-east-1.amazonaws.com"),
            "unit-access-key",
            "unit-secret-key",
            "unit-session-token",
            "us-east-1",
            ProviderTransportOptions.Default);
        var request = CreateRequest(provider.Id) with
        {
            Selection = new ProviderSelection(provider.Id, "mock-bedrock-v1"),
        };
        var body = CreateRequestBody(provider, request);
        var tool = Assert.Single(body.GetProperty("toolConfig").GetProperty("tools").EnumerateArray())
            .GetProperty("toolSpec");
        Assert.Equal("file.read", tool.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, tool.GetProperty("inputSchema").GetProperty("json").ValueKind);
        var messages = body.GetProperty("messages").EnumerateArray().ToArray();
        var toolUse = Assert.Single(
            messages[1].GetProperty("content").EnumerateArray(),
            static item => item.TryGetProperty("toolUse", out _)).GetProperty("toolUse");
        var toolResult = Assert.Single(messages[2].GetProperty("content").EnumerateArray())
            .GetProperty("toolResult");
        Assert.Equal("call-1", toolUse.GetProperty("toolUseId").GetString());
        Assert.Equal("call-1", toolResult.GetProperty("toolUseId").GetString());

        var endpoint = CreateRequestEndpoint(provider, request);
        Assert.Equal("/model/mock-bedrock-v1/converse-stream", endpoint.AbsolutePath);
    }

    /// <summary>
    /// 功能：确认 Codex 使用独立路径、`store:false` 和 Responses function tool 字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void CodexResponsesUsesNativeTargetAndStorageBoundary()
    {
        using var provider = new OpenAiCodexResponsesProvider(
            new Uri("https://chatgpt.com/backend-api"),
            null,
            ProviderTransportOptions.Default);
        var request = CreateRequest(provider.Id) with
        {
            Selection = new ProviderSelection(provider.Id, "mock-codex-v1"),
        };
        var body = CreateRequestBody(provider, request);
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.False(body.GetProperty("store").GetBoolean());
        var tool = Assert.Single(body.GetProperty("tools").EnumerateArray());
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("file.read", tool.GetProperty("name").GetString());

        var endpoint = CreateRequestEndpoint(provider, request);
        Assert.Equal("/backend-api/codex/responses", endpoint.AbsolutePath);
        Assert.Equal(string.Empty, endpoint.Query);
    }

    /// <summary>
    /// 功能：构造同时含用户文本、历史工具调用、工具结果和工具声明的 portable 请求。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerId">目标 family 的公共 Provider ID。</param>
    /// <returns>可覆盖嵌套字段映射的公共请求。</returns>
    private static ProviderRequest CreateRequest(string providerId)
    {
        var user = JsonSerializer.SerializeToElement(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = "user",
            ["content"] = new[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "text",
                    ["text"] = "读取文件",
                },
            },
        });
        var assistant = JsonSerializer.SerializeToElement(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = "assistant",
            ["content"] = new object[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "text",
                    ["text"] = "开始读取",
                },
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "tool_call",
                    ["toolCallId"] = "call-1",
                    ["name"] = "file.read",
                    ["arguments"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["path"] = "README.md",
                    },
                },
            },
        });
        var toolResult = JsonSerializer.SerializeToElement(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = "tool",
            ["toolCallId"] = "call-1",
            ["toolName"] = "file.read",
            ["isError"] = false,
            ["content"] = new[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "text",
                    ["text"] = "文件内容",
                },
            },
        });
        var schema = JsonSerializer.SerializeToElement(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "string",
                },
            },
            ["required"] = RequiredPath,
            ["additionalProperties"] = false,
        });
        return new ProviderRequest(
            new ProviderSelection(providerId, "mock-model"),
            [user, assistant, toolResult],
            [new ProviderToolDefinition("file.read", "读取工作区文件", schema)]);
    }

    /// <summary>
    /// 功能：在不发送网络请求的前提下调用 family 的受保护请求映射边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="provider">待验证的 family adapter。</param>
    /// <param name="request">portable Provider 请求。</param>
    /// <returns>adapter 生成的完整 wire JSON object。</returns>
    private static JsonElement CreateRequestBody(IProvider provider, ProviderRequest request)
    {
        var method = provider.GetType().GetMethod(
            "CreateRequestBody",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<JsonElement>(method.Invoke(provider, [request]));
    }

    /// <summary>
    /// 功能：在不发送网络请求的前提下调用 family 的受保护 request-target 构造边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="provider">待验证的原生 family adapter。</param>
    /// <param name="request">模型与 Provider ID 已匹配的公共请求。</param>
    /// <returns>adapter 将在认证注入前使用的绝对 URI。</returns>
    private static Uri CreateRequestEndpoint(IProvider provider, ProviderRequest request)
    {
        var method = provider.GetType().GetMethod(
            "CreateRequestEndpoint",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<Uri>(method.Invoke(provider, [request]));
    }
}
