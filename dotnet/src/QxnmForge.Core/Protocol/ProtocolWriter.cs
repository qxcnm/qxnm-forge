using System.Text.Json;
using QxnmForge.Domain;
using QxnmForge.Serialization;

namespace QxnmForge.Protocol;

/// <summary>
/// 功能：把完整 JSON-RPC 对象串行写入协议专用 stdout，防止并发帧交错。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ProtocolWriter : IDisposable
{
    private readonly Stream output;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly int maxFrameBytes;
    private readonly int maxEventBytes;

    /// <summary>
    /// 功能：绑定 stdout 字节流与协商前固定资源上限。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="output">协议专用可写流。</param>
    /// <param name="maxFrameBytes">所有 frame 的最大 UTF-8 字节数。</param>
    /// <param name="maxEventBytes">event notification 的更小上限。</param>
    public ProtocolWriter(Stream output, int maxFrameBytes, int maxEventBytes)
    {
        this.output = output;
        this.maxFrameBytes = maxFrameBytes;
        this.maxEventBytes = maxEventBytes;
    }

    /// <summary>
    /// 功能：写入成功响应并在返回前刷新 stdout。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <typeparam name="T">语言中立结果 DTO。</typeparam>
    /// <param name="id">原 request ID。</param>
    /// <param name="result">方法结果。</param>
    /// <param name="cancellationToken">写入取消信号。</param>
    /// <returns>完整 LF 帧 flush 后的 Task。</returns>
    public Task WriteSuccessAsync<T>(
        JsonElement id,
        T result,
        CancellationToken cancellationToken = default)
    {
        return WriteFrameAsync(
            new JsonRpcSuccessResponse<T>("2.0", id, result),
            eventFrame: false,
            cancellationToken);
    }

    /// <summary>
    /// 功能：写入结构化错误响应，parse error 可使用 null ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="id">JsonElement ID 或 null。</param>
    /// <param name="error">已脱敏 portable error。</param>
    /// <param name="cancellationToken">写入取消信号。</param>
    /// <returns>完整 LF 帧 flush 后的 Task。</returns>
    public Task WriteErrorAsync(
        object? id,
        PortableError error,
        CancellationToken cancellationToken = default)
    {
        return WriteFrameAsync(
            new JsonRpcErrorResponse("2.0", id, error),
            eventFrame: false,
            cancellationToken);
    }

    /// <summary>
    /// 功能：写入已由 journal durable 的 event notification。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="portableEvent">已持久化 exact event。</param>
    /// <param name="cancellationToken">写入取消信号。</param>
    /// <returns>event LF 帧 flush 后的 Task。</returns>
    /// <remarks>不变量：调用者必须只传入 AppendEventAsync 返回的对象。</remarks>
    /// <exception cref="InvalidDataException">序列化事件超过 maxEventBytes。</exception>
    public Task WriteEventAsync(
        AgentEvent portableEvent,
        CancellationToken cancellationToken = default)
    {
        return WriteFrameAsync(
            new JsonRpcEventNotification("2.0", "event", portableEvent),
            eventFrame: true,
            cancellationToken);
    }

    /// <summary>
    /// 功能：释放内部写入互斥器；不关闭调用者拥有的 stdout 流。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public void Dispose()
    {
        writeGate.Dispose();
    }

    /// <summary>
    /// 功能：先完整序列化并检查字节数，再在单写 gate 内原子追加 LF 和 flush。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="frame">完整 response 或 notification DTO。</param>
    /// <param name="eventFrame">是否执行 maxEventBytes 限制。</param>
    /// <param name="cancellationToken">写入取消信号。</param>
    /// <returns>flush 完成的 Task。</returns>
    private async Task WriteFrameAsync(
        object frame,
        bool eventFrame,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, JsonDefaults.Options);
        var limit = eventFrame ? maxEventBytes : maxFrameBytes;
        if (bytes.Length > limit)
        {
            throw new InvalidDataException(eventFrame
                ? "event exceeds maxEventBytes"
                : "frame exceeds maxFrameBytes");
        }

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }
}
