using System.Text;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：表示一个完成提交的 SSE 事件，不携带原始传输字节。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Event">可选 event 字段。</param>
/// <param name="Data">按规范用 LF 连接的一个或多个 data 字段。</param>
/// <param name="EndOfStream">事件是否仅因 EOF 而提交，便于区分残缺断流。</param>
public sealed record SseEvent(string? Event, string Data, bool EndOfStream = false);

/// <summary>
/// 功能：增量解码任意 UTF-8 字节分片、CR/LF/CRLF 与多行 data 的有界 SSE 流。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class SseDecoder
{
    private readonly Decoder utf8 = new UTF8Encoding(false, true).GetDecoder();
    private readonly int maxEventBytes;
    private readonly StringBuilder line = new();
    private readonly List<string> dataLines = [];
    private string? eventName;
    private int eventBytes;
    private bool previousWasCarriageReturn;
    private bool finished;

    /// <summary>
    /// 功能：创建具有单事件 UTF-8 字节上限的增量 SSE 解码器。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="maxEventBytes">单事件 data 与 event 字段的最大合计字节数。</param>
    /// <exception cref="ArgumentOutOfRangeException">上限不是正数。</exception>
    public SseDecoder(int maxEventBytes = 1_048_576)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEventBytes);

        this.maxEventBytes = maxEventBytes;
    }

    /// <summary>
    /// 功能：接收任意字节块并返回其中已由空行提交的 SSE 事件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="bytes">可能拆在 UTF-8 code point 或任意字段位置的字节。</param>
    /// <returns>按 wire 顺序完成的事件。</returns>
    /// <exception cref="InvalidDataException">UTF-8 无效或单事件超过上限。</exception>
    /// <exception cref="InvalidOperationException">Finish 后继续输入。</exception>
    public IReadOnlyList<SseEvent> Feed(ReadOnlySpan<byte> bytes)
    {
        if (finished)
        {
            throw new InvalidOperationException("SSE decoder is already finished");
        }

        var output = new List<SseEvent>();
        Span<char> characters = stackalloc char[4096];
        try
        {
            while (!bytes.IsEmpty)
            {
                utf8.Convert(
                    bytes,
                    characters,
                    flush: false,
                    out var bytesUsed,
                    out var charsUsed,
                    out _);
                ProcessCharacters(characters[..charsUsed], output);
                bytes = bytes[bytesUsed..];
                if (bytesUsed == 0 && charsUsed == 0)
                {
                    break;
                }
            }
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("provider SSE contains invalid UTF-8", exception);
        }

        return output;
    }

    /// <summary>
    /// 功能：结束 UTF-8 流并提交规范允许的无最终空行完整 SSE 事件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>EOF 时新完成的零个或一个事件。</returns>
    /// <exception cref="InvalidDataException">末尾 UTF-8 残缺或事件超过上限。</exception>
    /// <exception cref="InvalidOperationException">重复结束。</exception>
    public IReadOnlyList<SseEvent> Finish()
    {
        if (finished)
        {
            throw new InvalidOperationException("SSE decoder is already finished");
        }

        finished = true;
        var output = new List<SseEvent>();
        Span<char> characters = stackalloc char[16];
        try
        {
            utf8.Convert(
                ReadOnlySpan<byte>.Empty,
                characters,
                flush: true,
                out _,
                out var charsUsed,
                out _);
            ProcessCharacters(characters[..charsUsed], output);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("provider SSE ended inside a UTF-8 code point", exception);
        }

        if (line.Length > 0)
        {
            CommitLine(output);
        }

        DispatchEvent(output, endOfStream: true);
        return output;
    }

    /// <summary>
    /// 功能：逐字符处理换行状态并把完整行交给 SSE 字段解析。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="characters">已严格解码的字符。</param>
    /// <param name="output">本次调用完成事件的目标列表。</param>
    private void ProcessCharacters(ReadOnlySpan<char> characters, List<SseEvent> output)
    {
        foreach (var character in characters)
        {
            if (previousWasCarriageReturn)
            {
                previousWasCarriageReturn = false;
                if (character == '\n')
                {
                    continue;
                }
            }

            if (character == '\r')
            {
                CommitLine(output);
                previousWasCarriageReturn = true;
            }
            else if (character == '\n')
            {
                CommitLine(output);
            }
            else
            {
                line.Append(character);
                if (line.Length > maxEventBytes)
                {
                    throw new InvalidDataException("provider SSE line exceeds the event limit");
                }
            }
        }
    }

    /// <summary>
    /// 功能：按 SSE 字段语义应用一个完整行，空行提交当前事件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="output">完成事件目标列表。</param>
    private void CommitLine(List<SseEvent> output)
    {
        var value = line.ToString();
        line.Clear();
        if (Encoding.UTF8.GetByteCount(value) > maxEventBytes)
        {
            throw new InvalidDataException("provider SSE line exceeds the event limit");
        }

        if (value.Length == 0)
        {
            DispatchEvent(output);
            return;
        }

        if (value[0] == ':')
        {
            return;
        }

        var separator = value.IndexOf(':');
        var field = separator < 0 ? value : value[..separator];
        var fieldValue = separator < 0 ? string.Empty : value[(separator + 1)..];
        if (fieldValue.StartsWith(' '))
        {
            fieldValue = fieldValue[1..];
        }

        if (field == "event")
        {
            eventName = fieldValue;
            AddEventBytes(fieldValue);
        }
        else if (field == "data")
        {
            dataLines.Add(fieldValue);
            AddEventBytes(fieldValue);
        }
    }

    /// <summary>
    /// 功能：在出现 data 字段时把当前 SSE 事件转换为不可变结果并重置状态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="output">完成事件目标列表。</param>
    /// <param name="endOfStream">是否因 EOF 而非空行提交。</param>
    private void DispatchEvent(List<SseEvent> output, bool endOfStream = false)
    {
        if (dataLines.Count > 0)
        {
            output.Add(new SseEvent(eventName, string.Join('\n', dataLines), endOfStream));
        }

        dataLines.Clear();
        eventName = null;
        eventBytes = 0;
    }

    /// <summary>
    /// 功能：累计当前事件 UTF-8 大小并在读取期间执行上限。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">刚读取的字段值。</param>
    /// <exception cref="InvalidDataException">事件超过配置上限。</exception>
    private void AddEventBytes(string value)
    {
        eventBytes = checked(eventBytes + Encoding.UTF8.GetByteCount(value) + 1);
        if (eventBytes > maxEventBytes)
        {
            throw new InvalidDataException("provider SSE event exceeds the configured limit");
        }
    }
}
