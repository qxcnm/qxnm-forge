using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using QxnmForge.Provider;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 .NET Bedrock AWS EventStream 解码器的任意分片、双 CRC 与 EOF 边界。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class AwsEventStreamDecoderTests
{
    /// <summary>
    /// 功能：确认 IEEE CRC32 实现匹配标准 `123456789` 检查向量。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void Crc32MatchesStandardCheckVector()
    {
        Assert.Equal(0xcbf43926u, Crc32.Compute("123456789"u8));
    }

    /// <summary>
    /// 功能：确认 prelude、CRC、typed header、UTF-8 和 JSON 被逐字节分片时仍只发布一个完整消息。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void DecoderAcceptsEveryByteBoundary()
    {
        var wire = BuildMessage(
            "contentBlockDelta",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["delta"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["text"] = "你好 👋",
                },
                ["contentBlockIndex"] = 0,
            });
        var decoder = new AwsEventStreamDecoder(1_048_576);
        var messages = new List<AwsEventStreamMessage>();
        foreach (var value in wire)
        {
            messages.AddRange(decoder.Feed([value]));
        }

        decoder.Finish();
        var message = Assert.Single(messages);
        Assert.Equal("event", message.Headers[":message-type"]);
        Assert.Equal("contentBlockDelta", message.Headers[":event-type"]);
        Assert.Equal("你好 👋", message.Payload.GetProperty("delta").GetProperty("text").GetString());
    }

    /// <summary>
    /// 功能：确认 prelude CRC 被单字节篡改后解码器在发布 payload 前拒绝帧。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void DecoderRejectsPreludeCrcCorruption()
    {
        var wire = BuildMessage("messageStart", new Dictionary<string, object?> { ["role"] = "assistant" });
        wire[8] ^= 0x01;
        var decoder = new AwsEventStreamDecoder(1_048_576);
        Assert.Throws<InvalidDataException>(() => decoder.Feed(wire));
    }

    /// <summary>
    /// 功能：确认 message CRC 被单字节篡改后解码器拒绝整个消息。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void DecoderRejectsMessageCrcCorruption()
    {
        var wire = BuildMessage("messageStart", new Dictionary<string, object?> { ["role"] = "assistant" });
        wire[^1] ^= 0x01;
        var decoder = new AwsEventStreamDecoder(1_048_576);
        Assert.Throws<InvalidDataException>(() => decoder.Feed(wire));
    }

    /// <summary>
    /// 功能：确认 HTTP EOF 前仅收到部分下一帧时 Finish 拒绝断流。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void DecoderRejectsIncompleteMessageAtEof()
    {
        var wire = BuildMessage("messageStart", new Dictionary<string, object?> { ["role"] = "assistant" });
        var decoder = new AwsEventStreamDecoder(1_048_576);
        Assert.Empty(decoder.Feed(wire.AsSpan(0, 10)));
        Assert.Throws<InvalidDataException>(decoder.Finish);
    }

    /// <summary>
    /// 功能：为单元测试构造一个含三个 string header 和双 CRC 的 EventStream 消息。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="eventType">ConverseStream 事件名。</param>
    /// <param name="payload">将严格序列化为 UTF-8 JSON 的对象。</param>
    /// <returns>完整 AWS EventStream wire 字节。</returns>
    private static byte[] BuildMessage(string eventType, object payload)
    {
        var headers = new List<byte>();
        EncodeHeader(headers, ":message-type", "event");
        EncodeHeader(headers, ":event-type", eventType);
        EncodeHeader(headers, ":content-type", "application/json");
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var totalLength = checked(16 + headers.Count + payloadBytes.Length);
        var frame = new byte[totalLength];
        BinaryPrimitives.WriteUInt32BigEndian(frame, checked((uint)totalLength));
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), checked((uint)headers.Count));
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), Crc32.Compute(frame.AsSpan(0, 8)));
        headers.CopyTo(frame, 12);
        payloadBytes.CopyTo(frame, 12 + headers.Count);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(totalLength - 4), Crc32.Compute(frame.AsSpan(0, totalLength - 4)));
        return frame;
    }

    /// <summary>
    /// 功能：按 AWS EventStream string type 7 格式追加一个短 ASCII header。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="target">目标字节列表。</param>
    /// <param name="name">1..255 字节 ASCII header 名。</param>
    /// <param name="value">不超过 65535 字节的 UTF-8 值。</param>
    private static void EncodeHeader(List<byte> target, string name, string value)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        target.Add(checked((byte)nameBytes.Length));
        target.AddRange(nameBytes);
        target.Add(7);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)valueBytes.Length));
        target.AddRange(length.ToArray());
        target.AddRange(valueBytes);
    }
}
