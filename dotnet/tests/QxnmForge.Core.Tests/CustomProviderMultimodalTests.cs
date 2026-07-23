using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using QxnmForge.Agent;
using QxnmForge.Domain;
using QxnmForge.Provider;
using QxnmForge.Session;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证自定义 Provider 图片输入输出的能力收窄、非流式解析与 durable 发布顺序。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
[Collection(ProviderEnvironmentGroup.Name)]
public sealed class CustomProviderMultimodalTests
{
    /// <summary>
    /// 功能：确认有效图片输出关闭 function tools，移除独立 Image key 后恢复原工具与 streaming 声明。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="apiFamily">待验证的自定义 OpenAI-compatible family。</param>
    /// <returns>异步测试 Task。</returns>
    [Theory]
    [InlineData("openai-completions")]
    [InlineData("openai-responses")]
    public async Task ImageCredentialNarrowsOutputAndToolsWithoutChangingStoredDeclaration(
        string apiFamily)
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var providerId = "multimodal-" + apiFamily;
        var service = new CustomProviderConnectionService(stateRoot, workspace);
        var created = service.Create(new ProviderConnectionInput(
            "Multimodal",
            providerId,
            apiFamily,
            "https://example.invalid/v1",
            "https://example.invalid/v1/models",
            ["model-a"],
            SupportsTools: true,
            SupportsImageInput: true,
            SupportsImageOutput: true,
            LogoAssetId: null,
            Enabled: true));
        service.SetCredential(providerId, "responses", "responses-test-key");
        service.SetCredential(providerId, "image", "image-test-key");

        await using var enabledRegistry = ProviderRegistryFactory.CreateFromEnvironment(
            stateRoot: stateRoot,
            workspace: workspace);
        var enabledProvider = Assert.Single(
            enabledRegistry.Providers,
            provider => provider.Id == providerId);
        var enabledModel = Assert.Single(enabledRegistry.ListModels(providerId));
        Assert.Equal(["text", "image"], enabledModel.Capabilities.Input);
        Assert.Equal(["text", "image"], enabledModel.Capabilities.Output);
        Assert.False(enabledModel.Capabilities.Streaming);
        Assert.False(enabledModel.Capabilities.Tools);
        Assert.True(enabledProvider.SupportsImageInput);
        Assert.True(enabledProvider.SupportsImageOutput);
        Assert.False(enabledProvider.SupportsTools);

        var enabledBody = CreateRequestBody(enabledProvider);
        Assert.False(enabledBody.GetProperty("stream").GetBoolean());
        if (apiFamily == "openai-completions")
        {
            Assert.Equal(
                ["text", "image"],
                enabledBody.GetProperty("modalities").EnumerateArray().Select(static item => item.GetString()));
            Assert.False(enabledBody.TryGetProperty("tools", out _));
            Assert.False(enabledBody.TryGetProperty("stream_options", out _));
        }
        else
        {
            var imageTool = Assert.Single(enabledBody.GetProperty("tools").EnumerateArray());
            Assert.Equal("image_generation", imageTool.GetProperty("type").GetString());
        }

        service.RemoveCredential(providerId, "image");
        Assert.False(enabledProvider.IsAvailable());
        Assert.True(Assert.Single(service.List()).SupportsTools);

        await using var restoredRegistry = ProviderRegistryFactory.CreateFromEnvironment(
            stateRoot: stateRoot,
            workspace: workspace);
        var restoredProvider = Assert.Single(
            restoredRegistry.Providers,
            provider => provider.Id == providerId);
        var restoredModel = Assert.Single(restoredRegistry.ListModels(providerId));
        Assert.Equal(["text", "image"], restoredModel.Capabilities.Input);
        Assert.Equal(["text"], restoredModel.Capabilities.Output);
        Assert.True(restoredModel.Capabilities.Streaming);
        Assert.True(restoredModel.Capabilities.Tools);
        Assert.True(restoredProvider.SupportsImageInput);
        Assert.False(restoredProvider.SupportsImageOutput);
        Assert.True(restoredProvider.SupportsTools);
        Assert.True(restoredProvider.IsAvailable());

        var restoredBody = CreateRequestBody(restoredProvider);
        Assert.True(restoredBody.GetProperty("stream").GetBoolean());
        var restoredTool = Assert.Single(restoredBody.GetProperty("tools").EnumerateArray());
        Assert.Equal("function", restoredTool.GetProperty("type").GetString());
        Assert.Equal(created.ConnectionId, Assert.Single(service.List()).ConnectionId);
    }

    /// <summary>
    /// 功能：确认复核后的 portable image_ref 只映射为 request-local canonical data URL。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="apiFamily">待验证的 OpenAI-compatible family。</param>
    [Theory]
    [InlineData("openai-completions")]
    [InlineData("openai-responses")]
    public void ImageInputUsesFamilyNativeDataUrlField(string apiFamily)
    {
        IProvider provider = apiFamily == "openai-completions"
            ? new OpenAiChatProvider(
                new Uri("https://example.invalid/v1/chat/completions"),
                null,
                ProviderTransportOptions.Default)
            : new OpenAiResponsesProvider(
                new Uri("https://example.invalid/v1/responses"),
                null,
                ProviderTransportOptions.Default);
        using var ownedProvider = Assert.IsAssignableFrom<IDisposable>(provider);
        var bytes = PngBytes(7);
        var reference = new ArtifactReference(
            "artifact-image-input",
            "image/png",
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var message = JsonSerializer.SerializeToElement(new
        {
            role = "user",
            content = new object[]
            {
                new { type = "text", text = "inspect" },
                new
                {
                    type = "image_ref",
                    artifact = new
                    {
                        artifactId = reference.ArtifactId,
                        mediaType = reference.MediaType,
                        byteLength = reference.ByteLength,
                        sha256 = reference.Sha256,
                    },
                },
            },
        });
        var request = new ProviderRequest(
            new ProviderSelection(provider.Id, "model-a", provider.ApiFamily),
            [message],
            [])
        {
            ResolvedImages = [new ProviderResolvedImage(reference, bytes)],
        };

        var body = InvokeRequestBody(provider, request);
        var expectedUrl = "data:image/png;base64," + Convert.ToBase64String(bytes);
        if (apiFamily == "openai-completions")
        {
            var content = Assert.Single(body.GetProperty("messages").EnumerateArray())
                .GetProperty("content").EnumerateArray().ToArray();
            Assert.Equal(expectedUrl, content[1].GetProperty("image_url").GetProperty("url").GetString());
        }
        else
        {
            var content = Assert.Single(body.GetProperty("input").EnumerateArray())
                .GetProperty("content").EnumerateArray().ToArray();
            Assert.Equal(expectedUrl, content[1].GetProperty("image_url").GetString());
        }

        Assert.DoesNotContain("artifact-image-input", body.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：确认第一轮 output-only 图片只作为 assistant 历史展示，第二轮 output-only 或 text-only 请求均只保留其文本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="apiFamily">待验证的自定义 OpenAI-compatible family。</param>
    /// <param name="secondImageOutput">第二轮是否继续使用 output-only route。</param>
    /// <returns>异步测试 Task。</returns>
    [Theory]
    [InlineData("openai-completions", true)]
    [InlineData("openai-completions", false)]
    [InlineData("openai-responses", true)]
    [InlineData("openai-responses", false)]
    public async Task AssistantImageHistoryIsTextOnlyOnSecondOutputOrTextRoute(
        string apiFamily,
        bool secondImageOutput)
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var providerId = "history-" + apiFamily;
        var service = new CustomProviderConnectionService(stateRoot, workspace);
        service.Create(new ProviderConnectionInput(
            "History",
            providerId,
            apiFamily,
            "https://example.invalid/v1",
            "https://example.invalid/v1/models",
            ["model-a"],
            SupportsTools: false,
            SupportsImageInput: false,
            SupportsImageOutput: true,
            LogoAssetId: null,
            Enabled: true));
        service.SetCredential(providerId, "responses", "responses-history-key");
        service.SetCredential(providerId, "image", "image-history-key");

        await using var outputRegistry = ProviderRegistryFactory.CreateFromEnvironment(
            stateRoot: stateRoot,
            workspace: workspace);
        var outputProvider = Assert.Single(
            outputRegistry.Providers,
            provider => provider.Id == providerId);
        Assert.True(outputProvider.SupportsImageOutput);
        Assert.False(outputProvider.SupportsImageInput);

        IProvider secondProvider = outputProvider;
        ProviderRegistry? textRegistry = null;
        try
        {
            if (!secondImageOutput)
            {
                service.RemoveCredential(providerId, "image");
                textRegistry = ProviderRegistryFactory.CreateFromEnvironment(
                    stateRoot: stateRoot,
                    workspace: workspace);
                secondProvider = Assert.Single(
                    textRegistry.Providers,
                    provider => provider.Id == providerId);
            }

            Assert.Equal(secondImageOutput, secondProvider.SupportsImageOutput);
            Assert.False(secondProvider.SupportsImageInput);
            var historicalBytes = PngBytes(9);
            var historicalArtifact = new ArtifactReference(
                "artifact-assistant-history",
                "image/png",
                historicalBytes.Length,
                Convert.ToHexString(SHA256.HashData(historicalBytes)).ToLowerInvariant());
            var history = new[]
            {
                JsonSerializer.SerializeToElement(new
                {
                    role = "user",
                    content = new[] { new { type = "text", text = "draw first" } },
                }),
                JsonSerializer.SerializeToElement(new
                {
                    role = "assistant",
                    content = new object[]
                    {
                        new { type = "text", text = "generated image" },
                        new
                        {
                            type = "image_ref",
                            artifact = new
                            {
                                artifactId = historicalArtifact.ArtifactId,
                                mediaType = historicalArtifact.MediaType,
                                byteLength = historicalArtifact.ByteLength,
                                sha256 = historicalArtifact.Sha256,
                            },
                        },
                    },
                }),
                JsonSerializer.SerializeToElement(new
                {
                    role = "assistant",
                    content = new object[]
                    {
                        new
                        {
                            type = "image_ref",
                            artifact = new
                            {
                                artifactId = historicalArtifact.ArtifactId,
                                mediaType = historicalArtifact.MediaType,
                                byteLength = historicalArtifact.ByteLength,
                                sha256 = historicalArtifact.Sha256,
                            },
                        },
                    },
                }),
                JsonSerializer.SerializeToElement(new
                {
                    role = "user",
                    content = new[] { new { type = "text", text = "describe it" } },
                }),
            };
            var request = new ProviderRequest(
                new ProviderSelection(secondProvider.Id, "model-a", secondProvider.ApiFamily),
                history,
                []);

            var bodies = new[]
            {
                InvokeRequestBody(secondProvider, request),
                InvokeRequestBody(
                    secondProvider,
                    request with
                    {
                        ResolvedImages =
                        [
                            new ProviderResolvedImage(historicalArtifact, historicalBytes),
                        ],
                    }),
            };
            foreach (var body in bodies)
            {
                Assert.Equal(!secondImageOutput, body.GetProperty("stream").GetBoolean());
                Assert.DoesNotContain(
                    historicalArtifact.ArtifactId,
                    body.GetRawText(),
                    StringComparison.Ordinal);
                Assert.DoesNotContain("\"image_url\"", body.GetRawText(), StringComparison.Ordinal);
                Assert.DoesNotContain("\"input_image\"", body.GetRawText(), StringComparison.Ordinal);
                if (apiFamily == "openai-completions")
                {
                    var assistant = Assert.Single(
                        body.GetProperty("messages").EnumerateArray(),
                        static message => message.GetProperty("role").GetString() == "assistant");
                    Assert.Equal("generated image", assistant.GetProperty("content").GetString());
                    Assert.Equal(3, body.GetProperty("messages").GetArrayLength());
                }
                else
                {
                    var assistant = Assert.Single(
                        body.GetProperty("input").EnumerateArray(),
                        static item => item.TryGetProperty("role", out var role) &&
                            role.GetString() == "assistant");
                    var assistantContent = Assert.Single(
                        assistant.GetProperty("content").EnumerateArray());
                    Assert.Equal("output_text", assistantContent.GetProperty("type").GetString());
                    Assert.Equal("generated image", assistantContent.GetProperty("text").GetString());
                    Assert.Equal(3, body.GetProperty("input").GetArrayLength());
                }
            }
        }
        finally
        {
            if (textRegistry is not null)
            {
                await textRegistry.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// 功能：确认 Chat 非流式合法纯文本保留结束原因，并按 Rust 规则归一化 usage。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void ChatPureTextCompletionIsAcceptedWithNormalizedUsage()
    {
        var completion = ParseCompletion(
            typeof(OpenAiChatProvider),
            "{\"id\":\"chat-1\",\"choices\":[{\"message\":{\"content\":\"plain\"},\"finish_reason\":\"length\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":-1}}");

        Assert.Equal("plain", completion.Text);
        Assert.Empty(completion.Images);
        Assert.Equal("length", completion.FinishReason);
        Assert.Equal(new Usage(3, 0, 0), completion.Usage);
    }

    /// <summary>
    /// 功能：确认 Responses 只接受 completed 对象，忽略未知 output item，并允许合法纯文本完成。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void ResponsesPureTextCompletionIgnoresUnknownItemsAndNormalizesUsage()
    {
        var completion = ParseCompletion(
            typeof(OpenAiResponsesProvider),
            "{\"id\":\"response-1\",\"status\":\"completed\",\"output\":[{\"type\":\"future_item\",\"private\":true},{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"\"},{\"type\":\"output_text\",\"text\":\"plain\"}]}],\"usage\":{\"input_tokens\":4,\"output_tokens\":-1}}");

        Assert.Equal("plain", completion.Text);
        Assert.Empty(completion.Images);
        Assert.Equal("stop", completion.FinishReason);
        Assert.Equal(new Usage(4, 0, 4), completion.Usage);
    }

    /// <summary>
    /// 功能：确认 Chat 图片只有恰好一个 choice 且 raw finish_reason 为 stop 时才完成。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void ChatImageCompletionRejectsMissingOrNonStopFinishReason()
    {
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(PngBytes(1));
        var invalid = new[]
        {
            "{\"id\":\"chat-1\",\"choices\":[{\"message\":{\"images\":[{\"image_url\":{\"url\":\"" + dataUrl + "\"}}]}}]}",
            "{\"id\":\"chat-1\",\"choices\":[{\"message\":{\"images\":[{\"image_url\":{\"url\":\"" + dataUrl + "\"}}]},\"finish_reason\":\"length\"}]}",
            "{\"id\":\"chat-1\",\"choices\":[{\"message\":{\"content\":\"a\"},\"finish_reason\":\"stop\"},{\"message\":{\"content\":\"b\"},\"finish_reason\":\"stop\"}]}",
        };

        foreach (var payload in invalid)
        {
            AssertParseFailure(typeof(OpenAiChatProvider), payload);
        }

        var valid = ParseCompletion(
            typeof(OpenAiChatProvider),
            "{\"id\":\"chat-1\",\"choices\":[{\"message\":{\"images\":[{\"image_url\":{\"url\":\"" + dataUrl + "\"}}]},\"finish_reason\":\"stop\"}]}");
        Assert.Single(valid.Images);
        Assert.Equal("stop", valid.FinishReason);
    }

    /// <summary>
    /// 功能：确认 Responses 缺 ID、非 completed、空文字或部分无效图片批次全部失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void ResponsesCompletionRejectsIncompleteEmptyAndPartiallyInvalidBatches()
    {
        var image = Convert.ToBase64String(PngBytes(2));
        var invalid = new[]
        {
            "{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"plain\"}]}]}",
            "{\"id\":\"response-1\",\"status\":\"incomplete\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"plain\"}]}]}",
            "{\"id\":\"response-1\",\"status\":\"completed\",\"output\":[{\"type\":\"future_item\"},{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"\"}]}]}",
            "{\"id\":\"response-1\",\"status\":\"completed\",\"output\":[{\"type\":\"image_generation_call\",\"result\":\"" + image + "\"},{\"type\":\"image_generation_call\",\"result\":\"bm90LWEtcG5n\"}]}" ,
        };

        foreach (var payload in invalid)
        {
            AssertParseFailure(typeof(OpenAiResponsesProvider), payload);
        }
    }

    /// <summary>
    /// 功能：确认 completion 后断流、非法 finish 或部分无效批次均不产生 artifact.created 或 assistant 假成功。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="mode">受控失败发生位置。</param>
    /// <returns>异步测试 Task。</returns>
    [Theory]
    [InlineData("throw_after_completion")]
    [InlineData("invalid_finish")]
    [InlineData("invalid_batch")]
    [InlineData("journal_batch_failure")]
    public async Task InvalidImageCompletionLeavesNoArtifactOrAssistantSuccess(string mode)
    {
        var evidence = await RunAgentAsync(mode);

        Assert.Equal("run.failed", evidence.Events[^1].Type);
        Assert.Equal(0, CountKind(evidence.JournalLines, "artifact.created"));
        Assert.DoesNotContain(
            evidence.JournalLines,
            IsAssistantMessageAppend);
        Assert.Equal(0, evidence.ArtifactFileCount);
    }

    /// <summary>
    /// 功能：确认合法非流式纯文本不发布 artifact，并把完成信号的 length 原样持久化。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task PureTextNonStreamingCompletionRemainsUsable()
    {
        var evidence = await RunAgentAsync("pure_text");

        Assert.Equal("run.completed", evidence.Events[^1].Type);
        Assert.Equal(0, CountKind(evidence.JournalLines, "artifact.created"));
        var assistant = Assert.Single(
            evidence.JournalLines,
            IsAssistantMessageAppend);
        Assert.Contains("\"finishReason\":\"length\"", assistant, StringComparison.Ordinal);
        Assert.Contains("plain non-streaming", assistant, StringComparison.Ordinal);
        Assert.Equal(0, evidence.ArtifactFileCount);
    }

    /// <summary>
    /// 功能：确认合法双图在完整 stop 后才整批发布，随后写两条 artifact.created 和一个 assistant message。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task ValidImageBatchPublishesAllArtifactsBeforeAssistantMessage()
    {
        var evidence = await RunAgentAsync("valid_batch");

        Assert.Equal("run.completed", evidence.Events[^1].Type);
        Assert.Equal(2, CountKind(evidence.JournalLines, "artifact.created"));
        Assert.Equal(2, evidence.ArtifactFileCount);
        var artifactIndices = evidence.JournalLines
            .Select((line, index) => (line, index))
            .Where(static item => item.line.Contains(
                "\"kind\":\"artifact.created\"",
                StringComparison.Ordinal))
            .Select(static item => item.index)
            .ToArray();
        var assistant = Array.FindIndex(
            evidence.JournalLines,
            IsAssistantMessageAppend);
        Assert.Equal([assistant - 2, assistant - 1], artifactIndices);
        Assert.Contains("\"type\":\"image_ref\"", evidence.JournalLines[assistant], StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：通过受保护边界生成包含一个 function tool 的自定义请求体。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="provider">启动快照中的自定义 adapter。</param>
    /// <returns>不会发送网络请求的原生 JSON body。</returns>
    private static JsonElement CreateRequestBody(IProvider provider)
    {
        var message = JsonSerializer.SerializeToElement(new
        {
            role = "user",
            content = new[] { new { type = "text", text = "draw" } },
        });
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            additionalProperties = false,
        });
        var request = new ProviderRequest(
            new ProviderSelection(provider.Id, "model-a", provider.ApiFamily),
            [message],
            [new ProviderToolDefinition("file.read", "read", schema)]);
        return InvokeRequestBody(provider, request);
    }

    /// <summary>
    /// 功能：通过反射调用共享 family 的 protected 请求映射边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="provider">待验证 adapter。</param>
    /// <param name="request">已构造的语言中立请求。</param>
    /// <returns>不会发网的原生 JSON body。</returns>
    private static JsonElement InvokeRequestBody(IProvider provider, ProviderRequest request)
    {
        var method = provider.GetType().GetMethod(
            "CreateRequestBody",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("provider request mapper was not found");
        return Assert.IsType<JsonElement>(method.Invoke(provider, [request]));
    }

    /// <summary>
    /// 功能：调用指定 family 的私有非流式完成解析器并取得唯一整包信号。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerType">Chat 或 Responses adapter 类型。</param>
    /// <param name="payload">不含 secret 的测试 JSON object。</param>
    /// <returns>唯一完成信号。</returns>
    private static ProviderImageCompletionSignal ParseCompletion(Type providerType, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var method = providerType.GetMethod(
            "ParseImageCompletion",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("provider completion parser was not found");
        var signals = Assert.IsAssignableFrom<IReadOnlyList<ProviderSignal>>(
            method.Invoke(null, [document.RootElement]));
        return Assert.IsType<ProviderImageCompletionSignal>(Assert.Single(signals));
    }

    /// <summary>
    /// 功能：确认私有非流式解析器以脱敏 Provider 协议错误拒绝 payload。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerType">Chat 或 Responses adapter 类型。</param>
    /// <param name="payload">不含 secret 的无效 JSON object。</param>
    private static void AssertParseFailure(Type providerType, string payload)
    {
        var exception = Assert.Throws<TargetInvocationException>(() =>
            ParseCompletion(providerType, payload));
        var failure = Assert.IsType<ProviderOperationException>(exception.InnerException);
        Assert.Equal("provider_protocol_error", failure.Error.Details.Kind);
    }

    /// <summary>
    /// 功能：运行一个完全离线的图片完成场景并采集 durable 证据。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="mode">测试 Provider 的确定性场景。</param>
    /// <returns>事件、journal 行和 artifact 文件计数。</returns>
    private static async Task<AgentRunEvidence> RunAgentAsync(string mode)
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        await using var repository = new SessionRepository(
            Path.Combine(temporary.Path, "state"),
            workspace);
        var provider = new ImageCompletionProvider(mode);
        await using var registry = new ProviderRegistry([provider]);
        var service = new AgentService(repository, registry);
        var run = await service.AcceptAsync(
            "session-images",
            new InputMessage("user", [new TextContent("draw")]),
            new ProviderSelection(provider.Id, "image-model"),
            CancellationToken.None);
        var runtime = await repository.GetAsync("session-images", CancellationToken.None);
        if (mode == "journal_batch_failure")
        {
            runtime.Journal.FailNextImageCompletionBatchAfterBytesForTest(1024);
        }

        var events = new List<AgentEvent>();
        await foreach (var item in service.RunAsync(run, CancellationToken.None))
        {
            events.Add(item);
        }

        var lines = await File.ReadAllLinesAsync(runtime.Journal.JournalPath);
        var artifactFiles = Directory.EnumerateFiles(
            Path.Combine(runtime.Journal.DirectoryPath, "artifacts")).Count();
        return new AgentRunEvidence(events, lines, artifactFiles);
    }

    /// <summary>
    /// 功能：统计 portable journal 中指定 kind 的完整记录数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="lines">含 header 的完整 journal 行。</param>
    /// <param name="kind">待统计核心记录 kind。</param>
    /// <returns>精确匹配数量。</returns>
    private static int CountKind(IEnumerable<string> lines, string kind)
    {
        return lines.Count(line => line.Contains(
            "\"kind\":\"" + kind + "\"",
            StringComparison.Ordinal));
    }

    /// <summary>
    /// 功能：判断 journal 行是否为真正 durable 的 assistant message.appended，而不是事件副本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="line">单条完整 journal JSON 行。</param>
    /// <returns>核心 kind 与 data.message.role 同时匹配时为 true。</returns>
    private static bool IsAssistantMessageAppend(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        return root.TryGetProperty("kind", out var kind) &&
            kind.GetString() == "message.appended" &&
            root.TryGetProperty("data", out var data) &&
            data.TryGetProperty("message", out var message) &&
            message.TryGetProperty("role", out var role) &&
            role.GetString() == "assistant";
    }

    /// <summary>
    /// 功能：生成具有 PNG 魔数和稳定差异尾字节的最小测试图片。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="marker">区分批内图片的非敏感尾字节。</param>
    /// <returns>通过最小魔数检查的字节。</returns>
    private static byte[] PngBytes(byte marker)
    {
        return [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, marker];
    }

    /// <summary>
    /// 功能：保存单次离线 Agent 场景的可验证结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="Events">协议事件副本。</param>
    /// <param name="JournalLines">portable journal 完整行。</param>
    /// <param name="ArtifactFileCount">场景结束后的 artifact 文件数。</param>
    private sealed record AgentRunEvidence(
        IReadOnlyList<AgentEvent> Events,
        string[] JournalLines,
        int ArtifactFileCount);

    /// <summary>
    /// 功能：产生图片整包、纯文本或受控结束错误的离线 Provider。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class ImageCompletionProvider(string mode) : IProvider
    {
        /// <summary>
        /// 功能：取得测试 Provider ID。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        public string Id => "image-completion-provider";

        /// <summary>
        /// 功能：取得固定测试模型 allowlist。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        public IReadOnlyList<string> Models { get; } = ["image-model"];

        /// <summary>
        /// 功能：声明测试 route 不接受 function tools。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        public bool SupportsTools => false;

        /// <summary>
        /// 功能：声明测试 route 会产生严格图片完成信号。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        public bool SupportsImageOutput => true;

        /// <summary>
        /// 功能：只接受固定测试模型。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="modelId">待验证模型 ID。</param>
        /// <returns>恰为 image-model 时为 true。</returns>
        public bool SupportsModel(string modelId)
        {
            return modelId == "image-model";
        }

        /// <summary>
        /// 功能：按场景产生一个完成信号，并可在信号后模拟断流。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="request">已通过固定 route 校验的请求。</param>
        /// <param name="cancellationToken">测试取消信号。</param>
        /// <returns>不联网且不读取 credential 的确定性信号流。</returns>
        public async IAsyncEnumerable<ProviderSignal> StreamAsync(
            ProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = request;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (mode == "pure_text")
            {
                yield return new ProviderImageCompletionSignal(
                    "plain non-streaming",
                    [],
                    new Usage(1, 2, 3),
                    "length");
                yield break;
            }

            var images = mode == "invalid_batch"
                ? new ProviderImagePayload[]
                {
                    new("image/png", PngBytes(1)),
                    new("image/png", "not-a-png"u8.ToArray()),
                }
                :
                [
                    new ProviderImagePayload("image/png", PngBytes(1)),
                    new ProviderImagePayload("image/png", PngBytes(2)),
                ];
            yield return new ProviderImageCompletionSignal(
                "image result",
                images,
                new Usage(1, 2, 3),
                mode == "invalid_finish" ? "length" : "stop");
            if (mode == "throw_after_completion")
            {
                throw new ProviderOperationException(new PortableError(
                    -32005,
                    "synthetic completion trailer failure",
                    false,
                    new ErrorDetails("provider_protocol_error")));
            }
        }
    }
}
