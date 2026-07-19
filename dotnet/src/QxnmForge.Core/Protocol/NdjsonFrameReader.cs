using System.Runtime.CompilerServices;
using System.Text;

namespace QxnmForge.Protocol;

/// <summary>
/// 功能：按 LF 增量切分严格 UTF-8 NDJSON，不把 bare CR 或 Unicode 分隔符当边界。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class NdjsonFrameReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly int maxFrameBytes;

    /// <summary>
    /// 功能：创建具有单帧字节上限的 NDJSON reader。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="maxFrameBytes">包含 JSON、不含 LF 的最大字节数。</param>
    /// <exception cref="ArgumentOutOfRangeException">上限小于 1。</exception>
    public NdjsonFrameReader(int maxFrameBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrameBytes);
        this.maxFrameBytes = maxFrameBytes;
    }

    /// <summary>
    /// 功能：从任意拆包 stream 读取完整 NDJSON 帧，并在 clean EOF 接受无 LF 的完整候选帧。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="stream">原始 stdin 字节流。</param>
    /// <param name="cancellationToken">读取取消信号。</param>
    /// <returns>严格 UTF-8 解码后的单帧文本流。</returns>
    /// <remarks>不变量：仅 0x0A 切帧；只移除 LF 前一个 0x0D；不丢失拆分 UTF-8 字节。</remarks>
    /// <exception cref="InvalidDataException">空帧、非法 UTF-8 或超限帧。</exception>
    public async IAsyncEnumerable<string> ReadFramesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var readBuffer = new byte[4096];
        using var frame = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var segmentStart = 0;
            for (var index = 0; index < read; index++)
            {
                if (readBuffer[index] != (byte)'\n')
                {
                    continue;
                }

                await frame.WriteAsync(
                    readBuffer.AsMemory(segmentStart, index - segmentStart),
                    cancellationToken).ConfigureAwait(false);
                yield return DecodeFrame(frame);
                frame.SetLength(0);
                segmentStart = index + 1;
            }

            if (segmentStart < read)
            {
                await frame.WriteAsync(
                    readBuffer.AsMemory(segmentStart, read - segmentStart),
                    cancellationToken).ConfigureAwait(false);
            }

            if (frame.Length > maxFrameBytes)
            {
                throw new InvalidDataException("NDJSON frame exceeds maxFrameBytes");
            }
        }

        if (frame.Length > 0)
        {
            yield return DecodeFrame(frame);
        }
    }

    /// <summary>
    /// 功能：验证帧长度、移除一个 CR 并严格解码 UTF-8。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="frame">不含 LF 的字节缓冲。</param>
    /// <returns>完整帧字符串。</returns>
    /// <exception cref="InvalidDataException">帧为空、超限或 UTF-8 非法。</exception>
    private string DecodeFrame(MemoryStream frame)
    {
        var length = checked((int)frame.Length);
        var buffer = frame.GetBuffer();
        if (length > 0 && buffer[length - 1] == (byte)'\r')
        {
            length--;
        }

        if (length == 0)
        {
            throw new InvalidDataException("blank NDJSON frame");
        }

        if (length > maxFrameBytes)
        {
            throw new InvalidDataException("NDJSON frame exceeds maxFrameBytes");
        }

        try
        {
            return StrictUtf8.GetString(buffer, 0, length);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("invalid UTF-8 in NDJSON frame", exception);
        }
    }
}
