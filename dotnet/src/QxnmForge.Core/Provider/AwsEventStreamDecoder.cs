using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：表示已通过长度、typed header、UTF-8、JSON 与双 CRC 验证的 AWS EventStream 消息。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Headers">已拒绝重复键的 ordinal header 映射。</param>
/// <param name="Payload">已严格解析的独立 JSON object。</param>
internal sealed record AwsEventStreamMessage(
    IReadOnlyDictionary<string, string> Headers,
    JsonElement Payload);

/// <summary>
/// 功能：增量解码 AWS EventStream，严格验证大端长度、字符串 header 和 IEEE CRC32。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class AwsEventStreamDecoder
{
    private const int PreludeLength = 12;
    private const int MinimumMessageLength = 16;
    private readonly int maximumMessageBytes;
    private readonly List<byte> pending = [];

    /// <summary>
    /// 功能：创建单帧大小受限的 AWS EventStream 解码器。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="maximumMessageBytes">允许的单消息最大字节数，范围 1024..16 MiB。</param>
    /// <exception cref="ArgumentOutOfRangeException">上限不在安全范围。</exception>
    internal AwsEventStreamDecoder(int maximumMessageBytes)
    {
        if (maximumMessageBytes is < 1024 or > 16_777_216)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumMessageBytes));
        }

        this.maximumMessageBytes = maximumMessageBytes;
    }

    /// <summary>
    /// 功能：追加任意二进制分片并返回其中所有已完整验证的消息。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="bytes">可拆分 prelude、header、UTF-8 或 CRC 的响应字节。</param>
    /// <returns>保持 wire 顺序的完整消息。</returns>
    /// <remarks>不变量：只有双 CRC、typed header 和 JSON 全部通过后才发布消息。</remarks>
    /// <exception cref="InvalidDataException">长度、CRC、header、UTF-8 或 JSON 边界无效。</exception>
    internal IReadOnlyList<AwsEventStreamMessage> Feed(ReadOnlySpan<byte> bytes)
    {
        for (var index = 0; index < bytes.Length; index++)
        {
            pending.Add(bytes[index]);
        }

        if (pending.Count > maximumMessageBytes && pending.Count < PreludeLength)
        {
            throw new InvalidDataException("AWS EventStream buffer exceeded the limit");
        }

        var messages = new List<AwsEventStreamMessage>();
        Span<byte> prelude = stackalloc byte[PreludeLength];
        while (pending.Count >= PreludeLength)
        {
            for (var index = 0; index < PreludeLength; index++)
            {
                prelude[index] = pending[index];
            }

            var totalLength = BinaryPrimitives.ReadUInt32BigEndian(prelude);
            var headersLength = BinaryPrimitives.ReadUInt32BigEndian(prelude[4..]);
            if (totalLength < MinimumMessageLength ||
                totalLength > maximumMessageBytes ||
                headersLength > totalLength - MinimumMessageLength)
            {
                throw new InvalidDataException("AWS EventStream length is invalid");
            }

            var expectedPreludeCrc = BinaryPrimitives.ReadUInt32BigEndian(prelude[8..]);
            if (Crc32.Compute(prelude[..8]) != expectedPreludeCrc)
            {
                throw new InvalidDataException("AWS EventStream prelude CRC is invalid");
            }

            if (pending.Count < totalLength)
            {
                break;
            }

            var frame = pending.GetRange(0, checked((int)totalLength)).ToArray();
            var expectedMessageCrc = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(frame.Length - 4));
            if (Crc32.Compute(frame.AsSpan(0, frame.Length - 4)) != expectedMessageCrc)
            {
                throw new InvalidDataException("AWS EventStream message CRC is invalid");
            }

            messages.Add(DecodeFrame(frame, checked((int)headersLength)));
            pending.RemoveRange(0, frame.Length);
        }

        return messages;
    }

    /// <summary>
    /// 功能：在 HTTP EOF 时确认不存在残留 prelude 或未完整消息。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <exception cref="InvalidDataException">EOF 前仍有未完整字节。</exception>
    internal void Finish()
    {
        if (pending.Count != 0)
        {
            throw new InvalidDataException("AWS EventStream ended with an incomplete message");
        }
    }

    /// <summary>
    /// 功能：解码一个已验证长度和 CRC 的完整 EventStream frame。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="frame">包含完整 prelude/header/payload/CRC 的帧。</param>
    /// <param name="headersLength">来自 prelude 的 header 字节数。</param>
    /// <returns>严格 header 映射与 JSON object。</returns>
    /// <exception cref="InvalidDataException">typed header 或 payload 违反安全边界。</exception>
    private static AwsEventStreamMessage DecodeFrame(byte[] frame, int headersLength)
    {
        var headersStart = PreludeLength;
        var headersEnd = headersStart + headersLength;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        var cursor = headersStart;
        while (cursor < headersEnd)
        {
            var nameLength = frame[cursor++];
            if (nameLength == 0 || cursor + nameLength + 3 > headersEnd)
            {
                throw new InvalidDataException("AWS EventStream header name length is invalid");
            }

            var name = DecodeAscii(frame.AsSpan(cursor, nameLength));
            cursor += nameLength;
            if (frame[cursor++] != 7)
            {
                throw new InvalidDataException("AWS EventStream header type is unsupported");
            }

            var valueLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(cursor, 2));
            cursor += 2;
            if (cursor + valueLength > headersEnd)
            {
                throw new InvalidDataException("AWS EventStream header value length is invalid");
            }

            var value = DecodeUtf8(frame.AsSpan(cursor, valueLength));
            cursor += valueLength;
            if (!headers.TryAdd(name, value))
            {
                throw new InvalidDataException("AWS EventStream header is duplicated");
            }
        }

        if (cursor != headersEnd)
        {
            throw new InvalidDataException("AWS EventStream header boundary is invalid");
        }

        var payloadBytes = frame.AsSpan(headersEnd, frame.Length - headersEnd - 4);
        var payloadText = DecodeUtf8(payloadBytes);
        JsonElement payload;
        try
        {
            payload = ProviderJson.ParseObject(payloadText);
        }
        catch (ProviderOperationException exception)
        {
            throw new InvalidDataException("AWS EventStream payload JSON is invalid", exception);
        }

        return new AwsEventStreamMessage(headers, payload);
    }

    /// <summary>
    /// 功能：严格解码 EventStream header 名的 ASCII 字节。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="bytes">不含长度前缀的 header 名字节。</param>
    /// <returns>ASCII 字符串。</returns>
    /// <exception cref="InvalidDataException">发现非 ASCII 字节。</exception>
    private static string DecodeAscii(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IndexOfAnyInRange((byte)0x80, byte.MaxValue) >= 0)
        {
            throw new InvalidDataException("AWS EventStream header name is not ASCII");
        }

        return Encoding.ASCII.GetString(bytes);
    }

    /// <summary>
    /// 功能：以拒绝 BOM 和替换字符的严格 UTF-8 解码 EventStream 字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="bytes">header value 或 JSON payload 字节。</param>
    /// <returns>严格 UTF-8 字符串。</returns>
    /// <exception cref="InvalidDataException">字节序列不是合法 UTF-8。</exception>
    private static string DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("AWS EventStream value is not UTF-8", exception);
        }
    }
}

/// <summary>
/// 功能：计算 AWS EventStream 使用的 IEEE 802.3 CRC32（polynomial 0xEDB88320）。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    /// <summary>
    /// 功能：为一段完整字节计算标准 IEEE CRC32。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="bytes">待校验的 prelude 或除末 CRC 外的完整帧。</param>
    /// <returns>与 zlib.crc32 等价的无符号 32 位校验值。</returns>
    internal static uint Compute(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
        }

        return ~crc;
    }

    /// <summary>
    /// 功能：构造只读 CRC32 查找表，避免运行时外部依赖。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>256 项 polynomial 展开表。</returns>
    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320u;
            }

            table[index] = value;
        }

        return table;
    }
}
