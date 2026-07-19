using System.Text;
using System.Text.Json;
using QxnmForge.Protocol;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证严格 JSON/NDJSON transport 边界。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ProtocolTests
{
    /// <summary>
    /// 功能：确认重复 JSON 键被拒绝且不会被 last-value-wins 静默覆盖。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void ParseRequestRejectsDuplicateObjectKeys()
    {
        const string frame = "{\"jsonrpc\":\"2.0\",\"id\":\"one\",\"id\":\"two\",\"method\":\"initialize\",\"params\":{}}";
        var exception = Assert.Throws<ProtocolRequestException>(() => ProtocolCodec.ParseRequest(frame));
        Assert.Equal(-32700, exception.Error.Code);
    }

    /// <summary>
    /// 功能：确认 reader 接受 CRLF 和 clean EOF 末帧，同时保留中文 UTF-8。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task ReadFramesAsyncAcceptsCrLfAndFinalFrameWithoutLf()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"text\":\"你好\"}\r\n{\"text\":\"再见\"}");
        await using var stream = new MemoryStream(bytes);
        var reader = new NdjsonFrameReader(1024);
        var frames = new List<string>();
        await foreach (var frame in reader.ReadFramesAsync(stream, CancellationToken.None))
        {
            frames.Add(frame);
        }

        Assert.Equal(["{\"text\":\"你好\"}", "{\"text\":\"再见\"}"], frames);
    }

    /// <summary>
    /// 功能：确认空 NDJSON 帧按规范失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task ReadFramesAsyncRejectsBlankFrame()
    {
        await using var stream = new MemoryStream("\n"u8.ToArray());
        var reader = new NdjsonFrameReader(1024);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var unused in reader.ReadFramesAsync(stream, CancellationToken.None))
            {
                _ = unused;
            }
        });
    }

    /// <summary>
    /// 功能：确认 session/get 正确解析可选 afterSeq，且该值表示事件过滤下界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void ParseSessionGetAcceptsOptionalNonNegativeSafeEventSequence()
    {
        using var omittedDocument = JsonDocument.Parse("{\"sessionId\":\"session-one\"}");
        using var suppliedDocument = JsonDocument.Parse("{\"sessionId\":\"session-one\",\"afterSeq\":42}");

        Assert.Equal(
            ("session-one", 0L),
            ProtocolCodec.ParseSessionGet(omittedDocument.RootElement));
        Assert.Equal(
            ("session-one", 42L),
            ProtocolCodec.ParseSessionGet(suppliedDocument.RootElement));
    }

    /// <summary>
    /// 功能：确认 session/get 拒绝负数 afterSeq 和未知字段，不允许模糊过滤语义。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Theory]
    [InlineData("{\"sessionId\":\"session-one\",\"afterSeq\":-1}")]
    [InlineData("{\"sessionId\":\"session-one\",\"afterSeq\":0,\"unknown\":true}")]
    public void ParseSessionGetRejectsInvalidParams(string json)
    {
        using var document = JsonDocument.Parse(json);

        var exception = Assert.Throws<ProtocolRequestException>(() =>
            ProtocolCodec.ParseSessionGet(document.RootElement));

        Assert.Equal(-32602, exception.Error.Code);
    }
}
