using System.Text;
using QxnmForge.Provider;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 live Provider 共用 SSE 解码器的增量、边界和故障语义。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class SseDecoderTests
{
    /// <summary>
    /// 功能：确认 UTF-8 中文和补充平面字符可在任意单字节边界分片后无损还原。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void FeedRestoresUtf8SplitAtEveryByteBoundary()
    {
        var decoder = new SseDecoder();
        var bytes = Encoding.UTF8.GetBytes("event: token\ndata: 你好🙂\n\n");
        var events = new List<SseEvent>();

        foreach (var value in bytes)
        {
            events.AddRange(decoder.Feed([value]));
        }

        Assert.Collection(
            events,
            item =>
            {
                Assert.Equal("token", item.Event);
                Assert.Equal("你好🙂", item.Data);
                Assert.False(item.EndOfStream);
            });
        Assert.Empty(decoder.Finish());
    }

    /// <summary>
    /// 功能：确认 LF、CRLF 和 CR 三种行结束符均能独立提交事件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void FeedAcceptsAllSseLineEndings()
    {
        var decoder = new SseDecoder();
        var wire = "data: lf\n\ndata: crlf\r\n\r\ndata: cr\r\r";

        var events = decoder.Feed(Encoding.UTF8.GetBytes(wire));

        Assert.Equal(["lf", "crlf", "cr"], events.Select(static item => item.Data));
        Assert.Empty(decoder.Finish());
    }

    /// <summary>
    /// 功能：确认 comment 与未知字段被忽略，多行 data 使用单个 LF 合并并保留 event 名。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void FeedCombinesMultilineDataAndIgnoresNonPayloadFields()
    {
        var decoder = new SseDecoder();
        var wire = ": keepalive\nretry: 10\nevent: delta\ndata:first\ndata: second\n\n";

        var events = decoder.Feed(Encoding.UTF8.GetBytes(wire));

        var item = Assert.Single(events);
        Assert.Equal("delta", item.Event);
        Assert.Equal("first\nsecond", item.Data);
        Assert.False(item.EndOfStream);
    }

    /// <summary>
    /// 功能：确认 EOF 会提交没有最终空行的完整 data 事件并显式标记断流边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void FinishDispatchesFinalEventWithoutBlankLine()
    {
        var decoder = new SseDecoder();

        Assert.Empty(decoder.Feed(Encoding.UTF8.GetBytes("event: final\ndata: payload")));
        var item = Assert.Single(decoder.Finish());

        Assert.Equal("final", item.Event);
        Assert.Equal("payload", item.Data);
        Assert.True(item.EndOfStream);
    }

    /// <summary>
    /// 功能：确认传输中的非法 UTF-8 立即转换为受控 InvalidDataException。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void FeedRejectsInvalidUtf8()
    {
        var decoder = new SseDecoder();

        Assert.Throws<InvalidDataException>(() => decoder.Feed([0xFF]));
    }

    /// <summary>
    /// 功能：确认 EOF 落在 UTF-8 code point 中间时报告残缺传输而不产生替换字符。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void FinishRejectsTruncatedUtf8CodePoint()
    {
        var decoder = new SseDecoder();
        decoder.Feed([0xE4, 0xBD]);

        Assert.Throws<InvalidDataException>(() => decoder.Finish());
    }

    /// <summary>
    /// 功能：确认单事件字节数超过显式上限时停止累积以防止无界内存占用。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void FeedRejectsEventAboveConfiguredByteLimit()
    {
        var decoder = new SseDecoder(16);

        Assert.Throws<InvalidDataException>(
            () => decoder.Feed(Encoding.UTF8.GetBytes("data: 0123456789abcdefg\n")));
    }

    /// <summary>
    /// 功能：确认 Finish 是终止操作，结束后不能继续输入或重复结束。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void FinishPreventsDecoderReuse()
    {
        var decoder = new SseDecoder();
        decoder.Finish();

        Assert.Throws<InvalidOperationException>(() => decoder.Feed([]));
        Assert.Throws<InvalidOperationException>(() => decoder.Finish());
    }
}
