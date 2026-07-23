using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QxnmForge.Domain;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：原生实现 OpenRouter Images 非流式请求、整批严格验证与内存图像完成信号。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class OpenRouterImagesProvider : HttpSseProviderBase
{
    internal const int MaxInputImages = 8;
    internal const int MaxOutputImages = 8;
    internal const long MaxInputImageBytes = 16_777_216;
    internal const long MaxOutputImageBytes = 33_554_432;
    internal const int MaxResponseBytes = 50_331_648;
    internal const int MaxTextBytes = 262_144;
    private const long MaxSafeInteger = 9_007_199_254_740_991;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly string[] ImageOnlyModalities = ["image"];

    private static readonly string[] ImageAndTextModalities = ["image", "text"];

    private static readonly string[] SupportedMediaTypes =
        ["image/png", "image/jpeg", "image/webp", "image/gif"];

    private static readonly IReadOnlyList<string> FrozenModels = Array.AsReadOnly(
    [
        "black-forest-labs/flux.2-flex",
        "black-forest-labs/flux.2-klein-4b",
        "black-forest-labs/flux.2-max",
        "black-forest-labs/flux.2-pro",
        "bytedance-seed/seedream-4.5",
        "google/gemini-2.5-flash-image",
        "google/gemini-3-pro-image",
        "google/gemini-3-pro-image-preview",
        "google/gemini-3.1-flash-image",
        "google/gemini-3.1-flash-image-preview",
        "google/gemini-3.1-flash-lite-image",
        "microsoft/mai-image-2.5",
        "openai/gpt-5-image",
        "openai/gpt-5-image-mini",
        "openai/gpt-5.4-image-2",
        "openai/gpt-image-1",
        "openai/gpt-image-1-mini",
        "openai/gpt-image-2",
        "openrouter/auto",
        "recraft/recraft-v3",
        "recraft/recraft-v4",
        "recraft/recraft-v4-pro",
        "recraft/recraft-v4-pro-vector",
        "recraft/recraft-v4-vector",
        "recraft/recraft-v4.1",
        "recraft/recraft-v4.1-pro",
        "recraft/recraft-v4.1-pro-vector",
        "recraft/recraft-v4.1-utility",
        "recraft/recraft-v4.1-utility-pro",
        "recraft/recraft-v4.1-vector",
        "sourceful/riverflow-v2-fast",
        "sourceful/riverflow-v2-pro",
        "sourceful/riverflow-v2.5-fast",
        "sourceful/riverflow-v2.5-pro",
        "x-ai/grok-imagine-image-quality",
    ]);

    private static readonly HashSet<string> FrozenModelSet = new(FrozenModels, StringComparer.Ordinal);

    private static readonly HashSet<string> TextOutputModels = new(StringComparer.Ordinal)
    {
        "google/gemini-2.5-flash-image",
        "google/gemini-3-pro-image",
        "google/gemini-3-pro-image-preview",
        "google/gemini-3.1-flash-image",
        "google/gemini-3.1-flash-image-preview",
        "google/gemini-3.1-flash-lite-image",
        "openai/gpt-5-image",
        "openai/gpt-5-image-mini",
        "openai/gpt-5.4-image-2",
        "openrouter/auto",
    };

    private readonly Uri baseEndpoint;

    /// <summary>
    /// 功能：创建禁止重定向/代理并在最终请求边界注入 OpenRouter Bearer key 的图像 adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="endpoint">生产 `/api/v1` 或显式 conformance case base。</param>
    /// <param name="credential">只保留在进程内并仅进入 Authorization header 的 key。</param>
    /// <param name="options">公共 connect/headers/idle/total/retry 有界策略。</param>
    /// <remarks>不变量：endpoint 不重定向；credential、response body 和 data URL 不进入诊断。</remarks>
    /// <exception cref="ArgumentException">endpoint、credential 或 transport 策略无效。</exception>
    public OpenRouterImagesProvider(
        Uri endpoint,
        string credential,
        ProviderTransportOptions options)
        : base(
            "openrouter",
            endpoint,
            credential,
            "Authorization",
            "Bearer ",
            null,
            options)
    {
        if (string.IsNullOrEmpty(credential))
        {
            throw new ArgumentException("OpenRouter credential is required", nameof(credential));
        }

        baseEndpoint = endpoint;
    }

    /// <summary>
    /// 功能：为 canonical OpenRouter Images route 创建 catalog allowlist 与请求期环境 credential adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="endpoint">catalog `/api/v1` base 或 conformance loopback base。</param>
    /// <param name="credentialSource">只保存 OPENROUTER_API_KEY 名称的请求期来源。</param>
    /// <param name="models">同一 executable snapshot 的图像模型 allowlist。</param>
    /// <param name="options">公共有界 HTTP/retry 策略。</param>
    /// <remarks>不变量：credential 值不进入 adapter 字段，且 route 只执行 snapshot 内模型。</remarks>
    internal OpenRouterImagesProvider(
        Uri endpoint,
        ProviderCredentialSource credentialSource,
        IReadOnlyList<string> models,
        ProviderTransportOptions options)
        : base(
            "openrouter",
            endpoint,
            credentialSource,
            "Authorization",
            "Bearer ",
            null,
            options,
            "openrouter-images",
            models)
    {
        baseEndpoint = endpoint;
    }

    /// <summary>
    /// 功能：取得选择图像模型时必须显式提供的冻结 API family。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public override string ApiFamily => "openrouter-images";

    /// <summary>
    /// 功能：声明 OpenRouter Images 接受已复核图片输入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public override bool SupportsImageInput => true;

    /// <summary>
    /// 功能：声明 OpenRouter Images 产生严格验证的图片完成结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public override bool SupportsImageOutput => true;

    /// <summary>
    /// 功能：取得 v1 冻结的三十五个 OpenRouter 图像模型。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public override IReadOnlyList<string> Models => base.Models.Count == 0
        ? FrozenModels
        : base.Models;

    /// <summary>
    /// 功能：要求 OpenRouter 非流式图像请求返回 application/json。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    protected override string AcceptMediaType => MediaTypeNames.Application.Json;

    /// <summary>
    /// 功能：只接受冻结图像 catalog 中的精确 model ID，拒绝动态或路径型值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="modelId">run/start 模型 ID。</param>
    /// <returns>精确命中冻结清单时为 true。</returns>
    public override bool SupportsModel(string modelId)
    {
        return base.Models.Count == 0
            ? FrozenModelSet.Contains(modelId)
            : base.SupportsModel(modelId);
    }

    /// <summary>
    /// 功能：在已验证同源 base 后追加 OpenRouter 原生 `/chat/completions` 路径。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">已通过 family/model 路由的请求。</param>
    /// <returns>保持 scheme、host、port 与 base path 前缀的绝对 URI。</returns>
    protected override Uri CreateRequestEndpoint(ProviderRequest request)
    {
        return NativeProviderEndpoint.Append(baseEndpoint, "/chat/completions");
    }

    /// <summary>
    /// 功能：构造非流式 OpenRouter Images body，并仅在本次请求内生成 canonical data URL。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">portable 消息、显式 route 与已复核输入 artifact。</param>
    /// <returns>仅含 model、messages、stream:false 与冻结 modalities 的 JSON object。</returns>
    /// <remarks>不变量：host path 不进入 body；输入 base64 只存在于返回对象至 HTTP 序列化完成期间。</remarks>
    /// <exception cref="ProviderOperationException">消息、文字、引用、长度、摘要或图像签名无效。</exception>
    protected override JsonElement CreateRequestBody(ProviderRequest request)
    {
        var counters = new InputCounters();
        var messages = new List<Dictionary<string, object?>>(request.Messages.Count + 1);
        if (request.SystemInstructions is not null)
        {
            messages.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["role"] = "system",
                ["content"] = request.SystemInstructions,
            });
        }

        foreach (var message in request.Messages)
        {
            messages.Add(MapMessage(message, request.ResolvedImages, counters));
        }

        if (messages.Count == 0)
        {
            throw CreateFailure("provider_input_invalid", retryable: false);
        }

        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Selection.ModelId,
            ["messages"] = messages,
            ["stream"] = false,
            ["modalities"] = TextOutputModels.Contains(request.Selection.ModelId)
                ? ImageAndTextModalities
                : ImageOnlyModalities,
        });
    }

    /// <summary>
    /// 功能：完整读取并严格解析一次非流式 JSON 响应，整批成功后只产生一个图像完成信号。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="response">成功且尚未读取 body 的 HTTP 响应。</param>
    /// <param name="callerCancellation">run 本地取消信号。</param>
    /// <param name="totalCancellation">覆盖全部 attempts 的总时限信号。</param>
    /// <returns>恰好一个全批验证完成信号。</returns>
    /// <remarks>不变量：在全部 URL/base64/MIME/魔数/usage 验证完成前不 yield 任何图像。</remarks>
    protected override async IAsyncEnumerable<ProviderSignal> ParseResponseAsync(
        HttpResponseMessage response,
        CancellationToken callerCancellation,
        [EnumeratorCancellation] CancellationToken totalCancellation)
    {
        var bytes = await ReadResponseBodyAsync(
            response,
            callerCancellation,
            totalCancellation).ConfigureAwait(false);
        var completion = ParseCompletion(bytes);
        if (totalCancellation.IsCancellationRequested && !callerCancellation.IsCancellationRequested)
        {
            throw CreateFailure("provider_total_timeout", retryable: false);
        }

        yield return completion;
    }

    /// <summary>
    /// 功能：把一个 portable user/assistant 消息映射为 OpenAI-compatible 多模态 content parts。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="message">来自同一 selected Session branch 的消息。</param>
    /// <param name="resolvedImages">no-follow/hash 强绑定的 request-local 输入。</param>
    /// <param name="counters">整个请求共享的文字/图像计数器。</param>
    /// <returns>不含 portable 元数据的原生 message object。</returns>
    private Dictionary<string, object?> MapMessage(
        JsonElement message,
        IReadOnlyList<ProviderResolvedImage> resolvedImages,
        InputCounters counters)
    {
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("role", out var roleElement) ||
            roleElement.ValueKind != JsonValueKind.String ||
            roleElement.GetString() is not ("user" or "assistant") ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            throw CreateFailure("provider_input_invalid", retryable: false);
        }

        var parts = new List<object>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object ||
                !block.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                throw CreateFailure("provider_input_invalid", retryable: false);
            }

            switch (typeElement.GetString())
            {
                case "text":
                    var text = RequireStringAllowEmpty(block, "text");
                    counters.TextBytes = CheckedAdd(counters.TextBytes, StrictUtf8.GetByteCount(text));
                    if (counters.TextBytes > MaxTextBytes)
                    {
                        throw CreateFailure("provider_input_limit", retryable: false);
                    }

                    parts.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = "text",
                        ["text"] = text,
                    });
                    break;
                case "image_ref":
                    var reference = ParseReference(block);
                    var resolved = FindResolvedImage(reference, resolvedImages);
                    counters.ImageCount++;
                    counters.ImageBytes = CheckedAdd(counters.ImageBytes, resolved.Bytes.LongLength);
                    if (counters.ImageCount > MaxInputImages || counters.ImageBytes > MaxInputImageBytes)
                    {
                        throw CreateFailure("provider_input_limit", retryable: false);
                    }

                    ValidateResolvedImage(reference, resolved.Bytes);
                    parts.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["url"] = "data:" + reference.MediaType + ";base64," +
                                Convert.ToBase64String(resolved.Bytes),
                        },
                    });
                    break;
                default:
                    throw CreateFailure("provider_input_invalid", retryable: false);
            }
        }

        if (parts.Count == 0)
        {
            throw CreateFailure("provider_input_invalid", retryable: false);
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = roleElement.GetString(),
            ["content"] = parts,
        };
    }

    /// <summary>
    /// 功能：按 artifact ID 与全部强绑定核心字段查找已由 Session 复核的图像。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="reference">消息中的 portable 引用。</param>
    /// <param name="resolvedImages">Agent 在本次请求前复核的输入集合。</param>
    /// <returns>唯一字段完全一致的 request-local 图像。</returns>
    /// <exception cref="ProviderOperationException">缺失、重复或元数据不一致。</exception>
    private ProviderResolvedImage FindResolvedImage(
        ArtifactReference reference,
        IReadOnlyList<ProviderResolvedImage> resolvedImages)
    {
        ProviderResolvedImage? match = null;
        foreach (var candidate in resolvedImages)
        {
            if (!string.Equals(candidate.Reference.ArtifactId, reference.ArtifactId, StringComparison.Ordinal))
            {
                continue;
            }

            if (match is not null || !CoreReferenceEquals(candidate.Reference, reference))
            {
                throw CreateFailure("provider_input_artifact_invalid", retryable: false);
            }

            match = candidate;
        }

        return match ?? throw CreateFailure("provider_input_artifact_invalid", retryable: false);
    }

    /// <summary>
    /// 功能：复核内存图像仍与引用的长度、SHA-256、MIME 和魔数完全一致。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="reference">earlier artifact.created 引用。</param>
    /// <param name="bytes">no-follow 读取的精确字节。</param>
    /// <exception cref="ProviderOperationException">任一绑定不变量失配。</exception>
    private void ValidateResolvedImage(ArtifactReference reference, byte[] bytes)
    {
        try
        {
            ImageArtifactValidation.ValidateReference(reference, MaxInputImageBytes);
        }
        catch (ArgumentException)
        {
            throw CreateFailure("provider_input_artifact_invalid", retryable: false);
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (bytes.LongLength != reference.ByteLength ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(hash),
                Encoding.ASCII.GetBytes(reference.Sha256)) ||
            !ImageArtifactValidation.HasMatchingSignature(reference.MediaType, bytes))
        {
            throw CreateFailure("provider_input_artifact_invalid", retryable: false);
        }
    }

    /// <summary>
    /// 功能：在 content-length、idle、total、cancel 与断流边界内读取完整 JSON response bytes。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="response">成功 HTTP 响应。</param>
    /// <param name="callerCancellation">run 本地取消信号。</param>
    /// <param name="totalCancellation">Provider 总时限信号。</param>
    /// <returns>不超过 48 MiB 的精确 body 字节。</returns>
    /// <remarks>不变量：raw body 不进入异常、日志、Session 或 protocol。</remarks>
    /// <exception cref="ProviderOperationException">类型、大小、idle、total 或断流失败。</exception>
    private async Task<byte[]> ReadResponseBodyAsync(
        HttpResponseMessage response,
        CancellationToken callerCancellation,
        CancellationToken totalCancellation)
    {
        var contentType = response.Content.Headers.ContentType;
        if (!string.Equals(
                contentType?.MediaType,
                MediaTypeNames.Application.Json,
                StringComparison.OrdinalIgnoreCase) ||
            contentType?.CharSet is string charset &&
                !string.Equals(charset.Trim('"'), Encoding.UTF8.WebName, StringComparison.OrdinalIgnoreCase) ||
            response.Content.Headers.ContentEncoding.Count > 0 ||
            response.Content.Headers.ContentLength is < 0 or > MaxResponseBytes)
        {
            throw CreateFailure("provider_protocol_error", retryable: false);
        }

        Stream stream;
        try
        {
            stream = await response.Content.ReadAsStreamAsync(totalCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw CreateFailure("provider_total_timeout", retryable: false);
        }
        catch (IOException)
        {
            throw CreateFailure("provider_disconnected", retryable: true, terminalStatus: "interrupted");
        }

        await using (stream.ConfigureAwait(false))
        using (var output = response.Content.Headers.ContentLength is long declaredLength
                   ? new MemoryStream((int)declaredLength)
                   : new MemoryStream())
        {
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
                    break;
                }

                if (output.Length + read > MaxResponseBytes)
                {
                    throw CreateFailure("provider_output_limit", retryable: false);
                }

                output.Write(buffer, 0, read);
            }

            if (response.Content.Headers.ContentLength is long expected && output.Length != expected)
            {
                throw CreateFailure("provider_disconnected", retryable: true, terminalStatus: "interrupted");
            }

            return output.ToArray();
        }
    }

    /// <summary>
    /// 功能：严格解码完整 UTF-8/JSON 响应并验证 choices[0] 图像、文字与 usage。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="bytes">已通过 HTTP 大小和断流检查的 body。</param>
    /// <returns>整批已解码图像完成信号。</returns>
    /// <remarks>不变量：返回前全部图像均已 canonical base64 解码并通过 MIME/魔数检查。</remarks>
    /// <exception cref="ProviderOperationException">UTF-8、JSON、结构、URL、base64、图像或 usage 无效。</exception>
    private ProviderImageCompletionSignal ParseCompletion(byte[] bytes)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw CreateFailure("provider_protocol_error", retryable: false);
        }

        if (text.Length > 0 && text[0] == '\ufeff')
        {
            throw CreateFailure("provider_protocol_error", retryable: false);
        }

        JsonElement root;
        try
        {
            root = ProviderJson.ParseObject(text);
        }
        catch (ProviderOperationException)
        {
            throw CreateFailure("provider_protocol_error", retryable: false);
        }

        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() < 1 ||
            choices[0].ValueKind != JsonValueKind.Object ||
            !choices[0].TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("images", out var images) ||
            images.ValueKind != JsonValueKind.Array ||
            images.GetArrayLength() is < 1 or > MaxOutputImages)
        {
            throw CreateFailure("provider_protocol_error", retryable: false);
        }

        string? outputText = null;
        if (message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.Null)
            {
                outputText = null;
            }
            else if (content.ValueKind != JsonValueKind.String)
            {
                throw CreateFailure("provider_protocol_error", retryable: false);
            }
            else
            {
                outputText = content.GetString();
                if (StrictUtf8.GetByteCount(outputText!) > MaxTextBytes)
                {
                    throw CreateFailure("provider_output_limit", retryable: false);
                }

                if (outputText!.Length == 0)
                {
                    outputText = null;
                }
            }
        }

        var decoded = new List<ProviderImagePayload>(images.GetArrayLength());
        long totalBytes = 0;
        foreach (var image in images.EnumerateArray())
        {
            var payload = ParseImage(image);
            totalBytes = CheckedAdd(totalBytes, payload.Bytes.LongLength);
            if (totalBytes > MaxOutputImageBytes)
            {
                throw CreateFailure("provider_output_limit", retryable: false);
            }

            decoded.Add(payload);
        }

        var usage = root.TryGetProperty("usage", out var usageElement)
            ? ParseUsage(usageElement)
            : Usage.Zero;
        return new ProviderImageCompletionSignal(outputText, decoded, usage);
    }

    /// <summary>
    /// 功能：从 OpenRouter image_url wrapper 取得并严格解码一个 canonical data URL。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="image">choices[0].message.images 的单项。</param>
    /// <returns>MIME 与精确解码字节。</returns>
    /// <exception cref="ProviderOperationException">wrapper、URL、base64、MIME、空数据或魔数无效。</exception>
    private ProviderImagePayload ParseImage(JsonElement image)
    {
        if (image.ValueKind != JsonValueKind.Object ||
            !image.TryGetProperty("image_url", out var imageUrl) ||
            imageUrl.ValueKind != JsonValueKind.Object ||
            !imageUrl.TryGetProperty("url", out var urlElement) ||
            urlElement.ValueKind != JsonValueKind.String)
        {
            throw CreateFailure("provider_protocol_error", retryable: false);
        }

        var url = urlElement.GetString()!;
        foreach (var mediaType in SupportedMediaTypes)
        {
            var prefix = "data:" + mediaType + ";base64,";
            if (!url.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var encoded = url[prefix.Length..];
            if (!IsCanonicalBase64(encoded))
            {
                throw CreateFailure("provider_protocol_error", retryable: false);
            }

            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                throw CreateFailure("provider_protocol_error", retryable: false);
            }

            if (decoded.Length == 0 ||
                !string.Equals(Convert.ToBase64String(decoded), encoded, StringComparison.Ordinal) ||
                !ImageArtifactValidation.HasMatchingSignature(mediaType, decoded))
            {
                throw CreateFailure("provider_protocol_error", retryable: false);
            }

            return new ProviderImagePayload(mediaType, decoded);
        }

        throw CreateFailure("provider_protocol_error", retryable: false);
    }

    /// <summary>
    /// 功能：验证 base64 文本无空白、字符宽松、错位 padding 或非四字符分组。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">data URL 逗号后的 ASCII 文本。</param>
    /// <returns>符合 canonical RFC 4648 alphabet/padding 基础形状时为 true。</returns>
    private static bool IsCanonicalBase64(string value)
    {
        if (value.Length == 0 || value.Length % 4 != 0)
        {
            return false;
        }

        var padding = 0;
        for (var index = value.Length - 1; index >= 0 && value[index] == '='; index--)
        {
            padding++;
        }

        if (padding > 2)
        {
            return false;
        }

        for (var index = 0; index < value.Length - padding; index++)
        {
            var character = value[index];
            if (!(character is >= 'A' and <= 'Z' or
                  >= 'a' and <= 'z' or
                  >= '0' and <= '9' or '+' or '/'))
            {
                return false;
            }
        }

        return value.AsSpan(value.Length - padding).IndexOfAnyExcept('=') < 0;
    }

    /// <summary>
    /// 功能：把 OpenRouter snake_case usage 映射为安全、守恒的 portable token 计数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">可选 response.usage 值。</param>
    /// <returns>满足 total=input+output 的 Usage。</returns>
    /// <exception cref="ProviderOperationException">usage 缺字段、非整数、越界或不守恒。</exception>
    private Usage ParseUsage(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw CreateFailure("provider_protocol_error", retryable: false);
        }

        var input = RequireSafeInteger(element, "prompt_tokens");
        var output = RequireSafeInteger(element, "completion_tokens");
        var total = RequireSafeInteger(element, "total_tokens");
        if (input + output != total)
        {
            throw CreateFailure("provider_protocol_error", retryable: false);
        }

        return new Usage(input, output, total);
    }

    /// <summary>
    /// 功能：从 portable image_ref block 读取四个强绑定 artifact 核心字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="block">portable image_ref 对象。</param>
    /// <returns>不含路径或字节的 artifact 引用。</returns>
    /// <exception cref="ProviderOperationException">字段缺失、类型或范围无效。</exception>
    private ArtifactReference ParseReference(JsonElement block)
    {
        if (!block.TryGetProperty("artifact", out var artifact) || artifact.ValueKind != JsonValueKind.Object)
        {
            throw CreateFailure("provider_input_artifact_invalid", retryable: false);
        }

        var artifactId = RequireString(artifact, "artifactId");
        var mediaType = RequireString(artifact, "mediaType");
        var sha256 = RequireString(artifact, "sha256");
        if (!artifact.TryGetProperty("byteLength", out var lengthElement) ||
            !lengthElement.TryGetInt64(out var byteLength))
        {
            throw CreateFailure("provider_input_artifact_invalid", retryable: false);
        }

        var reference = new ArtifactReference(artifactId, mediaType, byteLength, sha256);
        try
        {
            ImageArtifactValidation.ValidateReference(reference, MaxInputImageBytes);
        }
        catch (ArgumentException)
        {
            throw CreateFailure("provider_input_artifact_invalid", retryable: false);
        }

        return reference;
    }

    /// <summary>
    /// 功能：读取 Provider JSON 必需字符串但允许 text 字段为空。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">固定字段名。</param>
    /// <returns>字符串值。</returns>
    /// <exception cref="ProviderOperationException">字段缺失或类型错误。</exception>
    private string RequireStringAllowEmpty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw CreateFailure("provider_input_invalid", retryable: false);
        }

        return value.GetString()!;
    }

    /// <summary>
    /// 功能：读取 Provider JSON 必需非空字符串并返回安全协议失败而不回显值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">固定字段名。</param>
    /// <returns>非空字符串。</returns>
    /// <exception cref="ProviderOperationException">字段缺失、空或类型错误。</exception>
    private string RequireString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(value.GetString()))
        {
            throw CreateFailure("provider_protocol_error", retryable: false);
        }

        return value.GetString()!;
    }

    /// <summary>
    /// 功能：读取 Provider usage 的非负 JavaScript 安全整数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">usage 对象。</param>
    /// <param name="name">固定 token 字段名。</param>
    /// <returns>零至 2^53-1 的整数。</returns>
    /// <exception cref="ProviderOperationException">字段缺失、浮点、负数或越界。</exception>
    private long RequireSafeInteger(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            !value.TryGetInt64(out var result) ||
            result is < 0 or > MaxSafeInteger)
        {
            throw CreateFailure("provider_protocol_error", retryable: false);
        }

        return result;
    }

    /// <summary>
    /// 功能：比较两个 artifact 引用的安全核心绑定字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="left">已复核引用。</param>
    /// <param name="right">消息引用。</param>
    /// <returns>ID、MIME、长度与 SHA-256 完全一致时为 true。</returns>
    private static bool CoreReferenceEquals(ArtifactReference left, ArtifactReference right)
    {
        return string.Equals(left.ArtifactId, right.ArtifactId, StringComparison.Ordinal) &&
            string.Equals(left.MediaType, right.MediaType, StringComparison.Ordinal) &&
            left.ByteLength == right.ByteLength &&
            string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：以 checked 语义累加有界字节计数并统一映射溢出为最大值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="left">当前计数。</param>
    /// <param name="right">新增非负计数。</param>
    /// <returns>未溢出的和；溢出时为 long.MaxValue。</returns>
    private static long CheckedAdd(long left, long right)
    {
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    /// <summary>
    /// 功能：保存单次原生请求的输入图像、字节和 UTF-8 文字累计值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class InputCounters
    {
        /// <summary>
        /// 功能：取得或设置请求内 image_ref 出现次数。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        internal int ImageCount { get; set; }

        /// <summary>
        /// 功能：取得或设置请求内解码图像累计字节。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        internal long ImageBytes { get; set; }

        /// <summary>
        /// 功能：取得或设置请求内 portable text 累计 UTF-8 字节。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        internal long TextBytes { get; set; }
    }
}
