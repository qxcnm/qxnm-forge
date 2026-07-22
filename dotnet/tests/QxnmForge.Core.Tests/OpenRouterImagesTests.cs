using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QxnmForge.Domain;
using QxnmForge.Protocol;
using QxnmForge.Provider;
using QxnmForge.Serialization;
using QxnmForge.Session;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 .NET OpenRouter Images 路由、严格 wire 与 Session artifact 安全边界。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class OpenRouterImagesTests
{
    private static readonly byte[] Png =
    [
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
        0x71, 0x78, 0x6e, 0x6d, 0x61, 0x69, 0x2d, 0x69,
        0x6d, 0x61, 0x67, 0x65, 0x2d, 0x66, 0x69, 0x78,
        0x74, 0x75, 0x72, 0x65,
    ];

    /// <summary>
    /// 功能：确认 run/start 保留显式 family 与不含字节/路径的 portable image_ref。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void ProtocolParsesExplicitFamilyAndImageReference()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "sessionId":"image-session",
              "input":{"role":"user","content":[
                {"type":"text","text":"编辑这张图"},
                {"type":"image_ref","artifact":{
                  "artifactId":"artifact-image-1",
                  "mediaType":"image/png",
                  "byteLength":28,
                  "sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                }}
              ]},
              "provider":{"id":"openrouter","modelId":"google/gemini-2.5-flash-image","apiFamily":"openrouter-images"}
            }
            """);

        var parsed = ProtocolCodec.ParseRunStart(document.RootElement);
        Assert.Equal("openrouter-images", parsed.Provider.ApiFamily);
        Assert.Equal(2, parsed.Input.Content.Count);
        var image = Assert.IsType<ImageReferenceContent>(parsed.Input.Content[1]);
        Assert.Equal("artifact-image-1", image.Artifact!.ArtifactId);
        var serialized = JsonSerializer.Serialize(parsed.Input, JsonDefaults.Options);
        Assert.Contains("\"type\":\"image_ref\"", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("data:image", serialized, StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：确认 registry 在唯一图像 route 时允许省略 family，并拒绝冻结清单外动态模型。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task RegistryResolvesUniqueImageFamilyAndRequiresFrozenModel()
    {
        using var provider = CreateProvider();
        await using var registry = new ProviderRegistry([provider]);

        Assert.Same(
            provider,
            registry.GetRequired(new ProviderSelection(
                "openrouter",
                "google/gemini-2.5-flash-image",
                "openrouter-images")));
        Assert.Same(
            provider,
            registry.GetRequired(new ProviderSelection(
                "openrouter",
                "google/gemini-2.5-flash-image")));
        Assert.Throws<ProviderUnavailableException>(() => registry.GetRequired(new ProviderSelection(
            "openrouter",
            "dynamic/image-model",
            "openrouter-images")));
    }

    /// <summary>
    /// 功能：确认输入 artifact 只在原生请求体内成为 canonical data URL，且模型能力选择双 modalities。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void RequestBodyUsesCanonicalRequestLocalDataUrl()
    {
        using var provider = CreateProvider();
        var reference = ReferenceFor(Png);
        var message = JsonSerializer.SerializeToElement(
            new UserMessage(
                "message-1",
                "user",
                [new TextContent("编辑"), new ImageReferenceContent(reference)],
                DateTimeOffset.UnixEpoch),
            JsonDefaults.Options);
        var request = new ProviderRequest(
            new ProviderSelection(
                "openrouter",
                "google/gemini-2.5-flash-image",
                "openrouter-images"),
            [message],
            [])
        {
            ResolvedImages = [new ProviderResolvedImage(reference, Png.ToArray())],
        };

        var body = CreateRequestBody(provider, request);
        Assert.False(body.GetProperty("stream").GetBoolean());
        Assert.Equal(
            ["image", "text"],
            body.GetProperty("modalities").EnumerateArray().Select(static item => item.GetString()));
        var parts = Assert.Single(body.GetProperty("messages").EnumerateArray())
            .GetProperty("content")
            .EnumerateArray()
            .ToArray();
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        var url = parts[1].GetProperty("image_url").GetProperty("url").GetString();
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(Png), url);
        Assert.DoesNotContain(System.IO.Path.GetTempPath(), body.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：确认输出 parser 拒绝重复键、尾随 JSON 与无效 UTF-8，错误不回显原始响应。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="failureKind">要构造的严格解析失败类别。</param>
    [Theory]
    [InlineData("duplicate")]
    [InlineData("trailing")]
    [InlineData("utf8")]
    public void StrictResponseRejectsAmbiguousJsonAndUtf8(string failureKind)
    {
        using var provider = CreateProvider();
        var valid = ValidResponseBytes();
        byte[] bytes = failureKind switch
        {
            "duplicate" => Encoding.UTF8.GetBytes("{\"choices\":[],\"choices\":[]}"),
            "trailing" => [.. valid, .. "{}"u8],
            _ => [0xff, 0xfe],
        };

        var exception = Assert.Throws<TargetInvocationException>(() => ParseCompletion(provider, bytes));
        var failure = Assert.IsType<ProviderOperationException>(exception.InnerException);
        Assert.Equal("provider_protocol_error", failure.Error.Details.Kind);
        Assert.DoesNotContain("choices", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("data:image", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：确认 artifact 发布生成独立目标，读取复核精确字节且 hash/length/MIME 漂移均失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ArtifactStorePublishesWithoutOverwriteAndRevalidatesBindings()
    {
        using var temporary = new TemporaryDirectory();
        var session = Directory.CreateDirectory(System.IO.Path.Combine(temporary.Path, "session")).FullName;
        Directory.CreateDirectory(System.IO.Path.Combine(session, "artifacts"));

        var first = await SessionArtifactStore.PublishImageAsync(
            session,
            "image/png",
            Png,
            1024,
            CancellationToken.None);
        var second = await SessionArtifactStore.PublishImageAsync(
            session,
            "image/png",
            Png,
            1024,
            CancellationToken.None);
        Assert.NotEqual(first.ArtifactId, second.ArtifactId);
        Assert.Equal(Png, await SessionArtifactStore.ReadImageAsync(
            session,
            first,
            1024,
            CancellationToken.None));
        Assert.Equal(Png, await File.ReadAllBytesAsync(
            System.IO.Path.Combine(session, "artifacts", first.ArtifactId)));

        await Assert.ThrowsAsync<ArtifactValidationException>(() => SessionArtifactStore.ReadImageAsync(
            session,
            first with { Sha256 = new string('0', 64) },
            1024,
            CancellationToken.None));
        await Assert.ThrowsAsync<ArtifactValidationException>(() => SessionArtifactStore.ReadImageAsync(
            session,
            first with { ByteLength = first.ByteLength + 1 },
            1024,
            CancellationToken.None));
        await Assert.ThrowsAsync<ArtifactValidationException>(() => SessionArtifactStore.ReadImageAsync(
            session,
            first with { MediaType = "image/jpeg" },
            1024,
            CancellationToken.None));
    }

    /// <summary>
    /// 功能：确认 Unix artifact 不受调用者 umask 放宽并始终只允许 owner 读写。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task ArtifactStoreUsesOwnerOnlyUnixMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var session = Directory.CreateDirectory(System.IO.Path.Combine(temporary.Path, "session")).FullName;
        Directory.CreateDirectory(System.IO.Path.Combine(session, "artifacts"));
        var reference = await SessionArtifactStore.PublishImageAsync(
            session,
            "image/png",
            Png,
            1024,
            CancellationToken.None);

        var mode = File.GetUnixFileMode(System.IO.Path.Combine(
            session,
            "artifacts",
            reference.ArtifactId));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    /// <summary>
    /// 功能：确认 leaf symlink 即使目标字节/hash 正确也不能作为输入 artifact 打开。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ArtifactStoreRejectsLeafSymlink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var session = Directory.CreateDirectory(System.IO.Path.Combine(temporary.Path, "session")).FullName;
        var artifacts = Directory.CreateDirectory(System.IO.Path.Combine(session, "artifacts")).FullName;
        var outside = System.IO.Path.Combine(temporary.Path, "outside-image");
        await File.WriteAllBytesAsync(outside, Png);
        var reference = ReferenceFor(Png) with { ArtifactId = "artifact-symlink" };
        File.CreateSymbolicLink(System.IO.Path.Combine(artifacts, reference.ArtifactId), outside);

        await Assert.ThrowsAsync<ArtifactValidationException>(() => SessionArtifactStore.ReadImageAsync(
            session,
            reference,
            1024,
            CancellationToken.None));
    }

    /// <summary>
    /// 功能：确认存在 artifact 文件仍不足够，只有 selected chain earlier artifact.created 才可读取。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task JournalRequiresEarlierSelectedArtifactRecord()
    {
        using var temporary = new TemporaryDirectory();
        var sessions = System.IO.Path.Combine(temporary.Path, "sessions");
        await using var journal = await PortableSessionJournal.OpenAsync(
            sessions,
            "image-session",
            temporary.Path);
        var reference = await SessionArtifactStore.PublishImageAsync(
            journal.DirectoryPath,
            "image/png",
            Png,
            1024,
            CancellationToken.None);

        await Assert.ThrowsAsync<ArtifactValidationException>(() => journal.ReadImageArtifactAsync(
            reference,
            1024,
            CancellationToken.None));
        await journal.AppendAsync("artifact.created", new { Artifact = reference });
        Assert.Equal(Png, await journal.ReadImageArtifactAsync(
            reference,
            1024,
            CancellationToken.None));
    }

    /// <summary>
    /// 功能：创建不访问网络的 OpenRouter Images adapter 测试实例。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>绑定 example.invalid 与合成 credential 的 disposable adapter。</returns>
    private static OpenRouterImagesProvider CreateProvider()
    {
        return new OpenRouterImagesProvider(
            new Uri("https://example.invalid/api/v1"),
            "unit-test-credential",
            ProviderTransportOptions.Default);
    }

    /// <summary>
    /// 功能：通过受保护 family 边界构造 request body，不发送 HTTP。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="provider">目标图像 adapter。</param>
    /// <param name="request">包含 portable 消息和已复核图像的请求。</param>
    /// <returns>原生 JSON body。</returns>
    private static JsonElement CreateRequestBody(
        OpenRouterImagesProvider provider,
        ProviderRequest request)
    {
        var method = typeof(OpenRouterImagesProvider).GetMethod(
            "CreateRequestBody",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<JsonElement>(method.Invoke(provider, [request]));
    }

    /// <summary>
    /// 功能：调用私有整批 response parser 以验证严格 JSON/UTF-8 拒绝边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="provider">目标 adapter。</param>
    /// <param name="bytes">完整 synthetic response bytes。</param>
    private static void ParseCompletion(OpenRouterImagesProvider provider, byte[] bytes)
    {
        var method = typeof(OpenRouterImagesProvider).GetMethod(
            "ParseCompletion",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        _ = method.Invoke(provider, [bytes]);
    }

    /// <summary>
    /// 功能：创建通过 image response parser 的最小 PNG JSON 字节。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>紧凑 UTF-8 JSON。</returns>
    private static byte[] ValidResponseBytes()
    {
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        images = new[]
                        {
                            new
                            {
                                image_url = new
                                {
                                    url = "data:image/png;base64," + Convert.ToBase64String(Png),
                                },
                            },
                        },
                    },
                },
            },
        });
    }

    /// <summary>
    /// 功能：从 synthetic 图像字节构造精确 portable artifact reference。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="bytes">待绑定图像。</param>
    /// <returns>固定测试 ID、PNG MIME、长度和小写 SHA-256。</returns>
    private static ArtifactReference ReferenceFor(byte[] bytes)
    {
        return new ArtifactReference(
            "artifact-image-1",
            "image/png",
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
}
