namespace QxnmForge.Session;

/// <summary>
/// 功能：使用不会随 Session 目录移动的固定 per-ID lease 协调打开、创建与 tombstone 删除。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class SessionCoordinationLease : IAsyncDisposable
{
    private readonly WriterLease lease;
    private bool disposed;

    /// <summary>
    /// 功能：保存已取得的底层 portable writer lease。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="lease">绑定固定 coordination 目录的 lease。</param>
    private SessionCoordinationLease(WriterLease lease)
    {
        this.lease = lease;
    }

    /// <summary>
    /// 功能：在 sessions 根的隐藏 coordination 区为单个 Session ID 取得跨进程 lease。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionsRoot">只包含 Session 的可信规范根。</param>
    /// <param name="sessionId">符合 portable opaque ID 语法的 Session ID。</param>
    /// <param name="cancellationToken">获取 lease 的取消信号。</param>
    /// <returns>释放前阻止其他进程打开、创建或移动同 ID Session 的 lease。</returns>
    /// <remarks>不变量：coordination 目录以点开头，不能与合法 Session ID 冲突。</remarks>
    internal static async Task<SessionCoordinationLease> AcquireAsync(
        string sessionsRoot,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionsRoot);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        var canonicalRoot = Path.GetFullPath(sessionsRoot);
        RequireRealDirectory(canonicalRoot);
        var coordinationRoot = Path.Combine(canonicalRoot, ".session-coordination");
        Directory.CreateDirectory(coordinationRoot);
        RequireRealDirectory(coordinationRoot);
        var sessionCoordination = Path.Combine(coordinationRoot, sessionId);
        Directory.CreateDirectory(sessionCoordination);
        RequireRealDirectory(sessionCoordination);
        var acquired = await WriterLease.AcquireAsync(
            sessionCoordination,
            sessionId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new SessionCoordinationLease(acquired);
    }

    /// <summary>
    /// 功能：判断固定 tombstone 根中是否存在同 ID 删除中目录或异常路径项。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionsRoot">可信 sessions 根。</param>
    /// <param name="sessionId">目标 Session ID。</param>
    /// <returns>真实目录、普通文件或链接项任一种存在时 true。</returns>
    internal static bool HasPendingTombstone(string sessionsRoot, string sessionId)
    {
        var path = Path.Combine(Path.GetFullPath(sessionsRoot), ".session-tombstones", sessionId);
        var directory = new DirectoryInfo(path);
        directory.Refresh();
        if (directory.Exists || directory.LinkTarget is not null)
        {
            return true;
        }

        var file = new FileInfo(path);
        file.Refresh();
        return file.Exists || file.LinkTarget is not null;
    }

    /// <summary>
    /// 功能：durable 释放底层 coordination owner 与 loopback witness。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>owner 目录安全清理完成后的异步操作。</returns>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await lease.BeginReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        await lease.FinishReleaseAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：验证 coordination 路径是存在的真实目录而非链接或 reparse point。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">待验证绝对目录。</param>
    /// <exception cref="SessionWriterLeaseException">目录不存在、为链接或属性不可验证。</exception>
    private static void RequireRealDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            info.Refresh();
            if (!info.Exists || info.LinkTarget is not null ||
                (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw SessionWriterLeaseException.Invalid();
            }
        }
        catch (SessionWriterLeaseException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw SessionWriterLeaseException.Invalid(exception);
        }
    }
}
