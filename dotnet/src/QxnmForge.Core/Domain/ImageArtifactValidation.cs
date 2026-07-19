using System.Buffers;

namespace QxnmForge.Domain;

/// <summary>
/// 功能：集中验证 portable 图像 MIME、魔数和 artifact 引用的品牌中立安全边界。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal static class ImageArtifactValidation
{
    private const long MaxSafeInteger = 9_007_199_254_740_991;

    private static readonly SearchValues<char> OpaqueIdCharacters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._:-");

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    /// <summary>
    /// 功能：判断 MIME 是否属于 v0.1 冻结的四种栅格图像类型。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="mediaType">候选规范 MIME。</param>
    /// <returns>PNG、JPEG、WebP 或 GIF 时为 true。</returns>
    internal static bool IsSupportedMediaType(string mediaType)
    {
        return mediaType is "image/png" or "image/jpeg" or "image/webp" or "image/gif";
    }

    /// <summary>
    /// 功能：对受支持 MIME 执行最小魔数绑定检查。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="mediaType">已声明图像 MIME。</param>
    /// <param name="bytes">完整候选 artifact 字节。</param>
    /// <returns>声明与 PNG/JPEG/WebP/GIF 文件头一致时为 true。</returns>
    /// <remarks>不变量：本检查不宣称安全解码或 hard sandbox，仅阻止明显 MIME 欺骗。</remarks>
    internal static bool HasMatchingSignature(string mediaType, ReadOnlySpan<byte> bytes)
    {
        return mediaType switch
        {
            "image/png" => bytes.StartsWith(PngSignature),
            "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
            "image/webp" => bytes.Length >= 12 &&
                bytes[..4].SequenceEqual("RIFF"u8) &&
                bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
            "image/gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
            _ => false,
        };
    }

    /// <summary>
    /// 功能：验证 artifact 引用可安全派生叶文件名并满足图像读取上限。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="reference">来自 wire 或 journal 的 portable 引用。</param>
    /// <param name="maximumBytes">本次调用允许的单 artifact 最大字节数。</param>
    /// <remarks>不变量：有效 ID 不能形成相对/绝对路径；hash 必须为小写规范形式。</remarks>
    /// <exception cref="ArgumentException">ID、MIME、长度、hash 或可选显示字段无效。</exception>
    internal static void ValidateReference(ArtifactReference reference, long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!IsOpaqueId(reference.ArtifactId) ||
            !IsSupportedMediaType(reference.MediaType) ||
            reference.ByteLength is <= 0 or > MaxSafeInteger ||
            reference.ByteLength > maximumBytes ||
            !IsCanonicalSha256(reference.Sha256) ||
            reference.DisplayName is { Length: < 1 or > 256 } ||
            reference.Extensions is { ValueKind: not System.Text.Json.JsonValueKind.Object })
        {
            throw new ArgumentException("image artifact reference is invalid", nameof(reference));
        }
    }

    /// <summary>
    /// 功能：判断 artifact ID 是否符合不可形成路径的公共 opaque ID 语法。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选 artifact ID。</param>
    /// <returns>长度和 allowlist 字符均有效时为 true。</returns>
    private static bool IsOpaqueId(string value)
    {
        return value.Length is >= 1 and <= 128 &&
            char.IsAsciiLetterOrDigit(value[0]) &&
            !value.AsSpan().ContainsAnyExcept(OpaqueIdCharacters);
    }

    /// <summary>
    /// 功能：判断 SHA-256 是否为恰好六十四位小写十六进制。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选摘要。</param>
    /// <returns>完全规范时为 true。</returns>
    private static bool IsCanonicalSha256(string value)
    {
        return value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}
