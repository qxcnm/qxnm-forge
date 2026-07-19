using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QxnmForge.Serialization;

/// <summary>
/// 功能：集中提供协议和 journal 共用的语言中立 JSON 序列化设置。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public static class JsonDefaults
{
    /// <summary>
    /// 功能：取得只读的紧凑 camelCase JSON 设置。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>供 wire DTO 与持久化记录共同使用的序列化设置。</returns>
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>
    /// 功能：创建不转义中文、忽略 null 且拒绝未映射成员的 JSON 设置。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>一份初始化完成、可安全复用的设置。</returns>
    private static JsonSerializerOptions Create()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new UtcDateTimeOffsetConverter() },
        };
    }
}

/// <summary>
/// 功能：把 DateTimeOffset 固定编码为带 Z 的 UTC RFC 3339 时间。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    /// <summary>
    /// 功能：从 wire RFC 3339 字符串读取 DateTimeOffset。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="reader">位于字符串 token 的 JSON reader。</param>
    /// <param name="typeToConvert">目标类型。</param>
    /// <param name="options">当前 JSON 设置。</param>
    /// <returns>解析并保留时点语义的 DateTimeOffset。</returns>
    /// <exception cref="JsonException">输入不是合法时间字符串。</exception>
    public override DateTimeOffset Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        return reader.GetDateTimeOffset();
    }

    /// <summary>
    /// 功能：将时间归一到 UTC 并输出固定毫秒精度和 Z 后缀。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="writer">目标 JSON writer。</param>
    /// <param name="value">待编码时点。</param>
    /// <param name="options">当前 JSON 设置。</param>
    public override void Write(
        Utf8JsonWriter writer,
        DateTimeOffset value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
    }
}
