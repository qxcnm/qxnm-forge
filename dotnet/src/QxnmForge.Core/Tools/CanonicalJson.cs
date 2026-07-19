using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QxnmForge.Tools;

/// <summary>
/// 功能：为审批 operation hash 生成属性排序稳定的 JSON 与 SHA-256。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal static class CanonicalJson
{
    /// <summary>
    /// 功能：按对象键序递归写入无空白 UTF-8 JSON，不改变数组顺序或标量语义。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">待规范化的独立 JSON 值。</param>
    /// <returns>稳定 UTF-8 JSON 字节。</returns>
    internal static byte[] Serialize(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteElement(writer, element);
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// 功能：对工具名、规范化参数和资源计算小写十六进制 SHA-256 operation hash。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="name">稳定工具名。</param>
    /// <param name="arguments">已验证参数。</param>
    /// <param name="resources">已规范化资源。</param>
    /// <returns>64 字符小写 SHA-256。</returns>
    internal static string HashOperation(
        string name,
        JsonElement arguments,
        IReadOnlyList<ApprovalResource> resources)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(name));
        hash.AppendData([0]);
        hash.AppendData(Serialize(arguments));
        foreach (var resource in resources)
        {
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(resource.Kind));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(resource.Value));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// 功能：递归写入单个 JSON 值，并对对象属性按 ordinal 排序。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="writer">目标 UTF-8 writer。</param>
    /// <param name="element">当前 JSON 节点。</param>
    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(static item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
