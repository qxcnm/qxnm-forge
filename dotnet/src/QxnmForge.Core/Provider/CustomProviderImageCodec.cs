using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QxnmForge.Domain;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：集中实现自定义 OpenAI-compatible 图片请求与响应的严格内存边界。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal static class CustomProviderImageCodec
{
    internal const int MaxImageCount = 8;
    internal const int MaxImageBytes = 524_288;
    internal const int MaxTotalImageBytes = 4_194_304;
    internal const int MaxJsonResponseBytes = 6_291_456;

    /// <summary>
    /// 功能：把 portable image_ref 映射为经请求期复核的 canonical data URL。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="block">当前消息的 image_ref block。</param>
    /// <param name="resolvedImages">Agent 从同 Session selected chain 复核的图片。</param>
    /// <returns>仅在本次 HTTP 请求内存在的 Base64 data URL。</returns>
    /// <exception cref="ProviderOperationException">引用、元数据、字节、hash、MIME 或魔数不一致。</exception>
    internal static string ResolveDataUrl(
        JsonElement block,
        IReadOnlyList<ProviderResolvedImage> resolvedImages)
    {
        var reference = ParseReference(block);
        try
        {
            ImageArtifactValidation.ValidateReference(reference, MaxImageBytes);
        }
        catch (ArgumentException)
        {
            throw ProtocolFailure();
        }

        ProviderResolvedImage? match = null;
        foreach (var candidate in resolvedImages)
        {
            if (!string.Equals(candidate.Reference.ArtifactId, reference.ArtifactId, StringComparison.Ordinal))
            {
                continue;
            }

            if (match is not null || !CoreEquals(candidate.Reference, reference))
            {
                throw ProtocolFailure();
            }

            match = candidate;
        }

        if (match is null ||
            match.Bytes.LongLength != reference.ByteLength ||
            !ImageArtifactValidation.HasMatchingSignature(reference.MediaType, match.Bytes) ||
            !Convert.ToHexString(SHA256.HashData(match.Bytes))
                .Equals(reference.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw ProtocolFailure();
        }

        return "data:" + reference.MediaType + ";base64," + Convert.ToBase64String(match.Bytes);
    }

    /// <summary>
    /// 功能：严格解码 canonical Base64 图片并绑定 MIME 与魔数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="encoded">无空白且重新编码完全相同的 Base64。</param>
    /// <param name="mediaType">四种受支持图片 MIME 之一。</param>
    /// <returns>不会进入 wire DTO 的已验证图片候选。</returns>
    /// <exception cref="ProviderOperationException">编码、MIME、大小或魔数无效。</exception>
    internal static ProviderImagePayload DecodeBase64Image(string encoded, string mediaType)
    {
        if (string.IsNullOrEmpty(encoded) ||
            encoded.Any(char.IsWhiteSpace) ||
            !ImageArtifactValidation.IsSupportedMediaType(mediaType))
        {
            throw ProtocolFailure();
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw ProtocolFailure();
        }

        if (bytes.Length is < 1 or > MaxImageBytes ||
            !Convert.ToBase64String(bytes).Equals(encoded, StringComparison.Ordinal) ||
            !ImageArtifactValidation.HasMatchingSignature(mediaType, bytes))
        {
            throw ProtocolFailure();
        }

        return new ProviderImagePayload(mediaType, bytes);
    }

    /// <summary>
    /// 功能：严格解析 Chat image_url.url 的内联图片。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">必须为 data:MIME;base64,payload 的完整字符串。</param>
    /// <returns>经 Base64、MIME、魔数与单图上限验证的候选。</returns>
    internal static ProviderImagePayload DecodeDataUrl(string value)
    {
        if (!value.StartsWith("data:", StringComparison.Ordinal))
        {
            throw ProtocolFailure();
        }

        const string marker = ";base64,";
        var markerIndex = value.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex <= 5 || value.IndexOf(marker, markerIndex + marker.Length, StringComparison.Ordinal) >= 0)
        {
            throw ProtocolFailure();
        }

        return DecodeBase64Image(
            value[(markerIndex + marker.Length)..],
            value[5..markerIndex]);
    }

    /// <summary>
    /// 功能：完整、有界读取并严格解析自定义 Provider 非流式 JSON object。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="response">成功 HTTP response。</param>
    /// <param name="cancellationToken">总时限与调用方取消的联合信号。</param>
    /// <returns>拒绝重复 key、注释和尾逗号的独立 JSON object。</returns>
    /// <exception cref="ProviderOperationException">长度、断流、UTF-8 或 JSON 无效。</exception>
    internal static async Task<JsonElement> ReadJsonObjectAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is < 0 or > MaxJsonResponseBytes)
        {
            throw OutputLimitFailure();
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = response.Content.Headers.ContentLength is long declaredLength
            ? new MemoryStream((int)declaredLength)
            : new MemoryStream();
        var buffer = new byte[16_384];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaxJsonResponseBytes)
            {
                throw OutputLimitFailure();
            }

            output.Write(buffer, 0, read);
        }

        if (response.Content.Headers.ContentLength is long expected && output.Length != expected)
        {
            throw ProtocolFailure();
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, (int)output.Length);
        }
        catch (DecoderFallbackException)
        {
            throw ProtocolFailure();
        }

        return ProviderJson.ParseObject(text);
    }

    /// <summary>
    /// 功能：从 portable image_ref block 解析内容绑定核心字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="block">包含 artifact object 的消息块。</param>
    /// <returns>不包含路径或图片字节的引用。</returns>
    private static ArtifactReference ParseReference(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object ||
            !block.TryGetProperty("artifact", out var artifact) ||
            artifact.ValueKind != JsonValueKind.Object ||
            !artifact.TryGetProperty("artifactId", out var id) ||
            id.ValueKind != JsonValueKind.String ||
            !artifact.TryGetProperty("mediaType", out var mediaType) ||
            mediaType.ValueKind != JsonValueKind.String ||
            !artifact.TryGetProperty("byteLength", out var byteLength) ||
            !byteLength.TryGetInt64(out var length) ||
            !artifact.TryGetProperty("sha256", out var sha256) ||
            sha256.ValueKind != JsonValueKind.String)
        {
            throw ProtocolFailure();
        }

        return new ArtifactReference(
            id.GetString()!,
            mediaType.GetString()!,
            length,
            sha256.GetString()!);
    }

    /// <summary>
    /// 功能：比较两个 artifact 引用的四个内容绑定核心字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private static bool CoreEquals(ArtifactReference left, ArtifactReference right)
    {
        return string.Equals(left.ArtifactId, right.ArtifactId, StringComparison.Ordinal) &&
            string.Equals(left.MediaType, right.MediaType, StringComparison.Ordinal) &&
            left.ByteLength == right.ByteLength &&
            string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：构造不泄漏正文、路径、hash 或 URL 的图片协议错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private static ProviderOperationException ProtocolFailure()
    {
        return new ProviderOperationException(new PortableError(
            -32005,
            "provider image data is invalid",
            false,
            new ErrorDetails("provider_protocol_error")));
    }

    /// <summary>
    /// 功能：构造图片响应超过固定字节上限的错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private static ProviderOperationException OutputLimitFailure()
    {
        return new ProviderOperationException(new PortableError(
            -32005,
            "provider image response exceeded the byte limit",
            false,
            new ErrorDetails("provider_output_limit")));
    }
}
