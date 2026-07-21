using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using QxnmForge.Domain;
using QxnmForge.Serialization;

namespace QxnmForge.Session;

/// <summary>
/// 功能：表示 application service 可返回的品牌中立 Session 摘要。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="SessionId">portable Session ID。</param>
/// <param name="Title">首条用户消息摘要，缺失时为 Session ID。</param>
/// <param name="Project">header workspace 的安全 basename。</param>
/// <param name="UpdatedAt">最后一条完整记录或 header 的 UTC 时间。</param>
/// <param name="Archived">工作区外归档元数据状态。</param>
/// <param name="Status">可选 active 或 approval；无法可靠证明时省略。</param>
public sealed record SessionSummary(
    string SessionId,
    string Title,
    string Project,
    DateTimeOffset UpdatedAt,
    bool Archived,
    string? Status = null);

/// <summary>
/// 功能：定义严格且不含 transcript 的 Session 归档状态文档。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="SchemaVersion">固定 0.1。</param>
/// <param name="ArchivedSessionIds">按 ordinal 排序的唯一 Session ID。</param>
internal sealed record SessionArchiveDocument(
    string SchemaVersion,
    IReadOnlyList<string> ArchivedSessionIds);

/// <summary>
/// 功能：表示只读 journal 摘要扫描结果。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Title">首条用户文本或 Session ID。</param>
/// <param name="Project">header workspace basename。</param>
/// <param name="UpdatedAt">最后完整记录/header 时间。</param>
internal sealed record SessionJournalInspection(
    string Title,
    string Project,
    DateTimeOffset UpdatedAt);

/// <summary>
/// 功能：以脱敏 portable error 表示 Session 生命周期读取、锁定、损坏或删除失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class SessionLifecycleException : Exception
{
    /// <summary>
    /// 功能：从不含主机路径、journal 正文或私有状态的 portable error 创建异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="error">可直接进入 JSON-RPC 的安全错误。</param>
    public SessionLifecycleException(PortableError error)
        : base(error.Message)
    {
        Error = error;
    }

    /// <summary>
    /// 功能：取得已脱敏 portable error。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public PortableError Error { get; }

    /// <summary>
    /// 功能：创建 Session 不存在错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">已验证 Session ID。</param>
    /// <returns>固定 session_not_found 错误。</returns>
    internal static SessionLifecycleException NotFound(string sessionId)
    {
        return new SessionLifecycleException(new PortableError(
            -32602,
            "session was not found",
            false,
            new ErrorDetails("session_not_found", ResourceId: sessionId)));
    }

    /// <summary>
    /// 功能：创建当前或外部 writer 导致的保守 busy 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标 Session ID。</param>
    /// <returns>可重试 session_busy 错误。</returns>
    internal static SessionLifecycleException Busy(string sessionId)
    {
        return new SessionLifecycleException(new PortableError(
            -32004,
            "session is busy",
            true,
            new ErrorDetails("session_busy", ResourceId: sessionId)));
    }

    /// <summary>
    /// 功能：创建 journal 或 Session 目录损坏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标 Session ID。</param>
    /// <returns>固定且不泄露路径的 session_corrupt 错误。</returns>
    internal static SessionLifecycleException Corrupt(string sessionId)
    {
        return new SessionLifecycleException(new PortableError(
            -32008,
            "session is corrupt or incompatible",
            false,
            new ErrorDetails("session_corrupt", ResourceId: sessionId)));
    }

    /// <summary>
    /// 功能：创建生命周期状态跨进程锁冲突错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>可重试 session_lifecycle_locked 错误。</returns>
    internal static SessionLifecycleException Locked()
    {
        return new SessionLifecycleException(new PortableError(
            -32002,
            "session lifecycle state is locked",
            true,
            new ErrorDetails("session_lifecycle_locked")));
    }

    /// <summary>
    /// 功能：创建归档元数据无效或 I/O 失败错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">session_lifecycle_store_invalid 或 session_lifecycle_store_io。</param>
    /// <returns>不可重试且不包含路径的内部错误。</returns>
    internal static SessionLifecycleException Store(string kind)
    {
        return new SessionLifecycleException(new PortableError(
            -32603,
            "session lifecycle storage is unavailable",
            false,
            new ErrorDetails(kind)));
    }

    /// <summary>
    /// 功能：创建 tombstone 删除无法证明安全的错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标 Session ID。</param>
    /// <returns>固定 session_delete_unsafe 错误。</returns>
    internal static SessionLifecycleException DeleteUnsafe(string sessionId)
    {
        return new SessionLifecycleException(new PortableError(
            -32603,
            "session deletion could not be completed safely",
            false,
            new ErrorDetails("session_delete_unsafe", ResourceId: sessionId)));
    }
}

/// <summary>
/// 功能：从真实 Session journal 提供工作区外归档、恢复、列表与 fail-closed 删除。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed partial class SessionLifecycleService
{
    private const string SchemaVersion = "0.1";
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private const int MaximumArchiveBytes = 1_048_576;
    private const int MaximumArchivedSessions = 4_096;
    private const int MaximumDiscoveredEntries = 16_384;
    private const int MaximumJournalLineBytes = 4 * 1024 * 1024;
    private const int MaximumDeleteEntries = 100_000;
    private const int MaximumDeleteDepth = 64;
    private const int UnixOpenReadOnly = 0;
    private const int LinuxOpenCloseOnExec = 0x80000;
    private const int LinuxOpenNoFollow = 0x20000;
    private const int LinuxOpenNonBlocking = 0x800;
    private const int MacOpenCloseOnExec = 0x1000000;
    private const int MacOpenNoFollow = 0x100;
    private const int MacOpenNonBlocking = 0x4;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly HashSet<string> CoreRecordKinds =
    [
        "message.appended",
        "event.emitted",
        "run.accepted",
        "run.started",
        "run.cancellation_requested",
        "run.terminal",
        "turn.started",
        "turn.completed",
        "provider.attempt",
        "tool.intent",
        "tool.result",
        "approval.requested",
        "approval.resolved",
        "queue.appended",
        "queue.consumed",
        "context.compacted",
        "model.selected",
        "session.metadata",
        "branch.selected",
        "artifact.created",
        "faux.configured",
        "extension",
    ];

    private static readonly HashSet<string> ImplementationLanguages =
        ["dotnet", "rust", "go", "typescript", "python"];

    private readonly string archiveDocumentPath;
    private readonly string lifecycleRoot;
    private readonly SessionRepository repository;
    private readonly string sessionsRoot;
    private readonly string tombstoneRoot;

    /// <summary>
    /// 功能：把专用 sessions 根、工作区外生命周期元数据与固定 tombstone 根绑定到同一状态根。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="stateRoot">应用状态根；Session 必须隔离在其固定 sessions 子目录。</param>
    /// <param name="workspace">生命周期元数据必须位于其外的工作区。</param>
    /// <param name="repository">用于拒绝本进程已打开 writer 的 repository。</param>
    /// <remarks>不变量：前端只能通过本服务访问摘要，不能直接读取 journal 或归档文件。</remarks>
    /// <exception cref="SessionLifecycleException">根目录、workspace 边界或 reparse 状态不安全。</exception>
    public SessionLifecycleService(
        string stateRoot,
        string workspace,
        SessionRepository repository)
    {
        ArgumentException.ThrowIfNullOrEmpty(stateRoot);
        ArgumentException.ThrowIfNullOrEmpty(workspace);
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        var canonicalStateRoot = Path.GetFullPath(stateRoot);
        this.sessionsRoot = repository.SessionsRoot;
        var canonicalWorkspace = Path.GetFullPath(workspace);
        lifecycleRoot = Path.Combine(canonicalStateRoot, "session-lifecycle");
        archiveDocumentPath = Path.Combine(lifecycleRoot, "archive-state.json");
        tombstoneRoot = Path.Combine(this.sessionsRoot, ".session-tombstones");
        try
        {
            RejectExistingAncestorReparsePoints(canonicalStateRoot);
            RejectExistingAncestorReparsePoints(canonicalWorkspace);
            RejectExistingAncestorReparsePoints(this.sessionsRoot);
            if (!PathsEqual(canonicalWorkspace, repository.Workspace) ||
                !PathsEqual(this.sessionsRoot, Path.Combine(canonicalStateRoot, "sessions")) ||
                IsWithin(canonicalStateRoot, canonicalWorkspace) ||
                IsWithin(this.sessionsRoot, canonicalWorkspace) ||
                IsWithin(lifecycleRoot, canonicalWorkspace))
            {
                throw SessionLifecycleException.Store("session_lifecycle_store_invalid");
            }

            CreatePrivateDirectory(canonicalStateRoot);
            RequireRealDirectory(canonicalStateRoot);
            CreatePrivateDirectory(this.sessionsRoot);
            RequireRealDirectory(this.sessionsRoot);
            CreatePrivateDirectory(lifecycleRoot);
            RequireRealDirectory(lifecycleRoot);
            CreatePrivateDirectory(tombstoneRoot);
            RequireRealDirectory(tombstoneRoot);
        }
        catch (SessionLifecycleException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw SessionLifecycleException.Store("session_lifecycle_store_io");
        }
    }

    /// <summary>
    /// 功能：发现真实 Session 目录并返回活动与归档摘要，损坏项只显示固定安全标题。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>按 updatedAt 降序、Session ID 升序排列的摘要。</returns>
    /// <remarks>列表只读完整 LF 记录，不修复、截断、追加或取得 writer lease。</remarks>
    public IReadOnlyList<SessionSummary> List()
    {
        HashSet<string> archived;
        using (var stateLock = AcquireLifecycleLock())
        {
            archived = new HashSet<string>(
                ReadArchiveDocumentUnlocked().ArchivedSessionIds,
                StringComparer.Ordinal);
        }

        var summaries = new List<SessionSummary>();
        var observed = 0;
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(sessionsRoot))
            {
                if (++observed > MaximumDiscoveredEntries)
                {
                    throw SessionLifecycleException.Store("session_lifecycle_store_invalid");
                }

                var sessionId = Path.GetFileName(path);
                if (!IsValidSessionId(sessionId) || !IsRealDirectory(path))
                {
                    continue;
                }

                var journalPath = Path.Combine(path, "journal.jsonl");
                if (!File.Exists(journalPath))
                {
                    continue;
                }

                try
                {
                    var inspection = InspectJournal(sessionId, journalPath);
                    summaries.Add(new SessionSummary(
                        sessionId,
                        inspection.Title,
                        inspection.Project,
                        inspection.UpdatedAt,
                        archived.Contains(sessionId)));
                }
                catch (SessionLifecycleException)
                {
                    summaries.Add(CreateCorruptSummary(sessionId, archived.Contains(sessionId)));
                }
            }
        }
        catch (SessionLifecycleException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw SessionLifecycleException.Store("session_lifecycle_store_io");
        }

        return summaries
            .OrderByDescending(static summary => summary.UpdatedAt)
            .ThenBy(static summary => summary.SessionId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 功能：在 journal 静止且健康时持久化归档状态，不修改任何 journal 字节。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标 Session ID。</param>
    /// <param name="cancellationToken">释放静止 repository writer 的取消信号。</param>
    /// <returns>archived=true 的真实摘要任务。</returns>
    public Task<SessionSummary> ArchiveAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        return SetArchivedAsync(sessionId, archived: true, cancellationToken);
    }

    /// <summary>
    /// 功能：在 journal 静止且健康时移除归档状态，不修改任何 journal 字节。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标 Session ID。</param>
    /// <param name="cancellationToken">释放静止 repository writer 的取消信号。</param>
    /// <returns>archived=false 的真实摘要任务。</returns>
    public Task<SessionSummary> RestoreAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        return SetArchivedAsync(sessionId, archived: false, cancellationToken);
    }

    /// <summary>
    /// 功能：验证静止健康 Session，原子移入可恢复 tombstone 后安全删除普通树。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标 Session ID。</param>
    /// <param name="cancellationToken">释放静止 repository writer 的取消信号。</param>
    /// <returns>tombstone 与归档状态清理完成后的任务。</returns>
    /// <remarks>不变量：任何链接、reparse、device、writer lease 或遍历越界都保守失败且不跟随；移动后的失败可按同一 ID 续删。</remarks>
    public async Task DeleteAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        using var reservation = await ReserveIdleSessionAsync(
            sessionId,
            cancellationToken).ConfigureAwait(false);
        await using var coordination = await AcquireDeleteCoordinationAsync(
            sessionId,
            cancellationToken).ConfigureAwait(false);
        using var stateLock = AcquireLifecycleLock();
        var sessionDirectory = Path.GetFullPath(Path.Combine(sessionsRoot, sessionId));
        var tombstone = Path.GetFullPath(Path.Combine(tombstoneRoot, sessionId));
        if (!IsWithin(sessionDirectory, sessionsRoot) || !IsWithin(tombstone, tombstoneRoot))
        {
            throw SessionLifecycleException.DeleteUnsafe(sessionId);
        }

        var sessionExists = Directory.Exists(sessionDirectory) || File.Exists(sessionDirectory);
        var tombstoneExists = Directory.Exists(tombstone) || File.Exists(tombstone);
        if (sessionExists && tombstoneExists)
        {
            throw SessionLifecycleException.DeleteUnsafe(sessionId);
        }

        if (!sessionExists)
        {
            if (!tombstoneExists)
            {
                throw SessionLifecycleException.NotFound(sessionId);
            }

            RequireRealDirectory(tombstone);
            CompleteTombstoneDelete(tombstone, sessionId);
            return;
        }

        sessionDirectory = ResolveExistingSessionDirectory(sessionId);
        FileStream? sessionLock = null;
        try
        {
            sessionLock = AcquireSessionMutationLock(sessionDirectory, sessionId);
            RejectWriterLease(sessionDirectory, sessionId);
            ValidateOwnedJournalHeader(sessionId, Path.Combine(sessionDirectory, "journal.jsonl"));
            RequireRealDirectory(sessionDirectory);
            var validatedEntries = 0;
            ValidateOrdinaryTree(
                sessionDirectory,
                sessionId,
                depth: 0,
                ref validatedEntries,
                Path.Combine(sessionDirectory, "lock"));
            if (File.Exists(tombstone) || Directory.Exists(tombstone))
            {
                throw SessionLifecycleException.DeleteUnsafe(sessionId);
            }

            Directory.Move(sessionDirectory, tombstone);
            RequireRealDirectory(tombstone);
            FlushDirectory(sessionsRoot);
            FlushDirectory(tombstoneRoot);
        }
        catch (SessionLifecycleException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw SessionLifecycleException.DeleteUnsafe(sessionId);
        }
        finally
        {
            sessionLock?.Dispose();
        }

        CompleteTombstoneDelete(tombstone, sessionId);
    }

    /// <summary>
    /// 功能：先提交归档状态清理，再完成固定 tombstone 的可重入普通树删除。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="tombstone">与 Session ID 一一对应的可信 tombstone 路径。</param>
    /// <param name="sessionId">用于归档清理与脱敏错误的 Session ID。</param>
    /// <remarks>不变量：任一步失败都保留可由后续相同 delete 请求识别的 tombstone 根。</remarks>
    private void CompleteTombstoneDelete(string tombstone, string sessionId)
    {
        try
        {
            RequireRealDirectory(tombstone);
            var document = ReadArchiveDocumentUnlocked();
            var archived = document.ArchivedSessionIds
                .Where(id => !string.Equals(id, sessionId, StringComparison.Ordinal))
                .ToArray();
            if (archived.Length != document.ArchivedSessionIds.Count)
            {
                WriteArchiveDocumentUnlocked(new SessionArchiveDocument(SchemaVersion, archived));
            }

            var deletedEntries = 0;
            DeleteOrdinaryTree(tombstone, sessionId, depth: 0, ref deletedEntries);
            FlushDirectory(tombstoneRoot);
        }
        catch (SessionLifecycleException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw SessionLifecycleException.DeleteUnsafe(sessionId);
        }
    }

    /// <summary>
    /// 功能：在全局生命周期锁和 Session advisory lock 内原子改变归档元数据。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标 Session ID。</param>
    /// <param name="archived">目标归档状态。</param>
    /// <returns>与持久化状态一致的摘要。</returns>
    private async Task<SessionSummary> SetArchivedAsync(
        string sessionId,
        bool archived,
        CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        using var reservation = await ReserveIdleSessionAsync(
            sessionId,
            cancellationToken).ConfigureAwait(false);
        using var stateLock = AcquireLifecycleLock();
        var sessionDirectory = ResolveExistingSessionDirectory(sessionId);
        using var sessionLock = AcquireSessionMutationLock(sessionDirectory, sessionId);
        RejectWriterLease(sessionDirectory, sessionId);
        var inspection = InspectJournal(sessionId, Path.Combine(sessionDirectory, "journal.jsonl"));
        var document = ReadArchiveDocumentUnlocked();
        var ids = new HashSet<string>(document.ArchivedSessionIds, StringComparer.Ordinal);
        if (archived)
        {
            ids.Add(sessionId);
        }
        else
        {
            ids.Remove(sessionId);
        }

        WriteArchiveDocumentUnlocked(new SessionArchiveDocument(
            SchemaVersion,
            ids.Order(StringComparer.Ordinal).ToArray()));
        return new SessionSummary(
            sessionId,
            inspection.Title,
            inspection.Project,
            inspection.UpdatedAt,
            archived);
    }

    /// <summary>
    /// 功能：阻止同一 Session 被重新打开，并释放 repository 中已打开但静止的 writer。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标 Session ID。</param>
    /// <param name="cancellationToken">等待正在打开 runtime 的取消信号。</param>
    /// <returns>调用方必须持有到文件 mutation 结束的 reservation。</returns>
    /// <exception cref="SessionLifecycleException">active run、pending faux 或并发操作导致 Session busy。</exception>
    private async Task<SessionLifecycleReservation> ReserveIdleSessionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await repository.TryReserveLifecycleMutationAsync(
                sessionId,
                cancellationToken).ConfigureAwait(false)
                ?? throw SessionLifecycleException.Busy(sessionId);
        }
        catch (SessionLifecycleException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            throw SessionLifecycleException.Store("session_lifecycle_store_io");
        }
    }

    /// <summary>
    /// 功能：取得不会随 Session move 的跨进程 coordination lease，阻止同 ID 在删除中重建。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标 Session ID。</param>
    /// <param name="cancellationToken">获取跨进程 lease 的取消信号。</param>
    /// <returns>必须持有到 tombstone 清理结束的 coordination lease。</returns>
    /// <exception cref="SessionLifecycleException">另一个 writer/open/delete 正持有同 ID lease。</exception>
    private async Task<SessionCoordinationLease> AcquireDeleteCoordinationAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SessionCoordinationLease.AcquireAsync(
                sessionsRoot,
                sessionId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SessionWriterLeaseException)
        {
            throw SessionLifecycleException.Busy(sessionId);
        }
    }

    /// <summary>
    /// 功能：解析固定 Session 目录并验证其位于根下、真实且包含普通 journal 文件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">已验证 Session ID。</param>
    /// <returns>可信 Session 目录绝对路径。</returns>
    private string ResolveExistingSessionDirectory(string sessionId)
    {
        var directory = Path.GetFullPath(Path.Combine(sessionsRoot, sessionId));
        if (!IsWithin(directory, sessionsRoot) || !Directory.Exists(directory))
        {
            throw SessionLifecycleException.NotFound(sessionId);
        }

        if (!IsRealDirectory(directory))
        {
            throw SessionLifecycleException.Corrupt(sessionId);
        }

        var journal = Path.Combine(directory, "journal.jsonl");
        if (!File.Exists(journal))
        {
            throw SessionLifecycleException.NotFound(sessionId);
        }

        return directory;
    }

    /// <summary>
    /// 功能：拒绝存在中的 portable writer lease，包括 advisory lock 前的并发 writer。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionDirectory">已验证 Session 目录。</param>
    /// <param name="sessionId">目标 Session ID。</param>
    private static void RejectWriterLease(string sessionDirectory, string sessionId)
    {
        var writerLease = Path.Combine(sessionDirectory, "writer.lock.d");
        if (Directory.Exists(writerLease) || File.Exists(writerLease))
        {
            throw SessionLifecycleException.Busy(sessionId);
        }
    }

    /// <summary>
    /// 功能：取得 Session 固定 lock 文件独占句柄，并允许删除时原子移动父目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionDirectory">已验证真实 Session 目录。</param>
    /// <param name="sessionId">用于脱敏错误的 Session ID。</param>
    /// <returns>释放前阻止任何 portable journal writer 的 FileStream。</returns>
    private static FileStream AcquireSessionMutationLock(
        string sessionDirectory,
        string sessionId)
    {
        var path = Path.Combine(sessionDirectory, "lock");
        try
        {
            RejectReparseIfPresent(path, sessionId);
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Delete,
                bufferSize: 1,
                FileOptions.WriteThrough);
            try
            {
                var attributes = File.GetAttributes(stream.SafeFileHandle);
                if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
                {
                    throw SessionLifecycleException.Corrupt(sessionId);
                }

                RejectReparseIfPresent(path, sessionId);
                RestrictFilePermissions(path);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (SessionLifecycleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SessionLifecycleException.Busy(sessionId);
        }
    }

    /// <summary>
    /// 功能：取得归档元数据跨进程独占 writer lock。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>释放时解除锁的 FileStream。</returns>
    private FileStream AcquireLifecycleLock()
    {
        var path = Path.Combine(lifecycleRoot, "session-lifecycle.lock");
        try
        {
            RejectReparseIfPresent(path, sessionId: null);
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            RestrictFilePermissions(path);
            return stream;
        }
        catch (SessionLifecycleException)
        {
            throw;
        }
        catch (IOException)
        {
            throw SessionLifecycleException.Locked();
        }
        catch (UnauthorizedAccessException)
        {
            throw SessionLifecycleException.Store("session_lifecycle_store_io");
        }
    }

    /// <summary>
    /// 功能：在生命周期锁内读取并严格验证归档状态文档。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>不存在时为空 v0.1 文档。</returns>
    private SessionArchiveDocument ReadArchiveDocumentUnlocked()
    {
        if (!File.Exists(archiveDocumentPath))
        {
            return new SessionArchiveDocument(SchemaVersion, []);
        }

        try
        {
            RejectReparseIfPresent(archiveDocumentPath, sessionId: null);
            using var stream = new FileStream(
                archiveDocumentPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            if (stream.Length is <= 0 or > MaximumArchiveBytes)
            {
                throw SessionLifecycleException.Store("session_lifecycle_store_invalid");
            }

            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            using var json = JsonDocument.Parse(bytes, StrictDocumentOptions());
            ValidateNoDuplicateKeys(json.RootElement);
            var document = JsonSerializer.Deserialize<SessionArchiveDocument>(
                json.RootElement.GetRawText(),
                JsonDefaults.Options)
                ?? throw SessionLifecycleException.Store("session_lifecycle_store_invalid");
            ValidateArchiveDocument(document);
            return document;
        }
        catch (SessionLifecycleException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or OverflowException)
        {
            throw SessionLifecycleException.Store("session_lifecycle_store_invalid");
        }
    }

    /// <summary>
    /// 功能：在生命周期锁内用同目录临时文件原子发布归档文档。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="document">已排序的完整状态。</param>
    private void WriteArchiveDocumentUnlocked(SessionArchiveDocument document)
    {
        ValidateArchiveDocument(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonDefaults.Options);
        if (bytes.Length > MaximumArchiveBytes)
        {
            throw SessionLifecycleException.Store("session_lifecycle_store_invalid");
        }

        var temporary = Path.Combine(
            lifecycleRoot,
            ".session-lifecycle-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16_384,
                       FileOptions.WriteThrough))
            {
                RestrictFilePermissions(temporary);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            RejectReparseIfPresent(archiveDocumentPath, sessionId: null);
            File.Move(temporary, archiveDocumentPath, overwrite: true);
            FlushDirectory(lifecycleRoot);
        }
        catch (SessionLifecycleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SessionLifecycleException.Store("session_lifecycle_store_io");
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _ = exception;
            }
        }
    }

    /// <summary>
    /// 功能：验证归档文档版本、数量、Session ID、唯一性与排序。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="document">严格反序列化文档。</param>
    private static void ValidateArchiveDocument(SessionArchiveDocument document)
    {
        if (document.SchemaVersion != SchemaVersion ||
            document.ArchivedSessionIds is null ||
            document.ArchivedSessionIds.Count > MaximumArchivedSessions)
        {
            throw SessionLifecycleException.Store("session_lifecycle_store_invalid");
        }

        string? previous = null;
        foreach (var sessionId in document.ArchivedSessionIds)
        {
            if (!IsValidSessionId(sessionId) ||
                (previous is not null && string.CompareOrdinal(previous, sessionId) >= 0))
            {
                throw SessionLifecycleException.Store("session_lifecycle_store_invalid");
            }

            previous = sessionId;
        }
    }

    /// <summary>
    /// 功能：只读并严格检查完整 LF journal 前缀，提取标题、项目与更新时间。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目录派生且已验证的 Session ID。</param>
    /// <param name="journalPath">固定 Session 目录下 journal.jsonl。</param>
    /// <returns>不含 transcript 的安全摘要字段。</returns>
    private static SessionJournalInspection InspectJournal(string sessionId, string journalPath)
    {
        try
        {
            using var stream = OpenRegularReadOnly(journalPath, sessionId);
            if (stream.Length <= 0)
            {
                throw SessionLifecycleException.Corrupt(sessionId);
            }

            var lineNumber = 0L;
            var recordIds = new HashSet<string>(StringComparer.Ordinal);
            string? title = null;
            var sawUserMessage = false;
            string? project = null;
            var updatedAt = DateTimeOffset.UnixEpoch;
            foreach (var line in ReadCommittedJournalLines(stream, sessionId))
            {
                _ = StrictUtf8.GetCharCount(line);
                using var json = JsonDocument.Parse(line, StrictDocumentOptions());
                ValidateNoDuplicateKeys(json.RootElement);
                if (lineNumber == 0)
                {
                    var header = InspectHeader(json.RootElement, sessionId);
                    project = ProjectName(header.Workspace);
                    updatedAt = header.CreatedAt;
                }
                else
                {
                    var record = InspectRecord(
                        json.RootElement,
                        sessionId,
                        lineNumber,
                        recordIds);
                    updatedAt = record.Time;
                    if (!sawUserMessage)
                    {
                        var candidate = ExtractUserTitle(record.Kind, record.Data);
                        sawUserMessage = candidate.IsUserMessage;
                        title = candidate.Title;
                    }
                }

                lineNumber++;
            }

            if (lineNumber == 0 || project is null)
            {
                throw SessionLifecycleException.Corrupt(sessionId);
            }

            return new SessionJournalInspection(title ?? sessionId, project, updatedAt);
        }
        catch (SessionLifecycleException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or
                OverflowException or ArgumentException or NotSupportedException)
        {
            throw SessionLifecycleException.Corrupt(sessionId);
        }
    }

    /// <summary>
    /// 功能：只验证第一条 durable header 对目录 Session ID 的所有权，不要求后续记录健康。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目录派生且已验证的 Session ID。</param>
    /// <param name="journalPath">固定 Session 目录下 journal.jsonl。</param>
    /// <remarks>不变量：允许用户删除 header 有效但后续损坏的 Session，仍拒绝无所有权证明的任意目录。</remarks>
    private static void ValidateOwnedJournalHeader(string sessionId, string journalPath)
    {
        try
        {
            using var stream = OpenRegularReadOnly(journalPath, sessionId);
            var firstLine = ReadCommittedJournalLines(stream, sessionId).FirstOrDefault()
                ?? throw SessionLifecycleException.Corrupt(sessionId);
            _ = StrictUtf8.GetCharCount(firstLine);
            using var json = JsonDocument.Parse(firstLine, StrictDocumentOptions());
            ValidateNoDuplicateKeys(json.RootElement);
            _ = InspectHeader(json.RootElement, sessionId);
        }
        catch (SessionLifecycleException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or
                OverflowException or ArgumentException or NotSupportedException)
        {
            throw SessionLifecycleException.Corrupt(sessionId);
        }
    }

    /// <summary>
    /// 功能：以固定缓冲流式产出全部 LF committed journal 行，并忽略唯一未终止尾部。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="stream">已 no-follow 打开的普通 journal 文件。</param>
    /// <param name="sessionId">用于脱敏边界错误的 Session ID。</param>
    /// <returns>每项独立且不含 LF 的 1..4MiB 行字节。</returns>
    /// <remarks>不变量：总 journal 长度不受内存上限约束；单行与工作缓冲始终有界。</remarks>
    private static IEnumerable<byte[]> ReadCommittedJournalLines(
        FileStream stream,
        string sessionId)
    {
        var readBuffer = ArrayPool<byte>.Shared.Rent(16_384);
        var line = new ArrayBufferWriter<byte>(16_384);
        try
        {
            while (true)
            {
                var read = stream.Read(readBuffer, 0, readBuffer.Length);
                if (read == 0)
                {
                    yield break;
                }

                var segmentStart = 0;
                for (var index = 0; index < read; index++)
                {
                    if (readBuffer[index] != (byte)'\n')
                    {
                        continue;
                    }

                    var segmentLength = index - segmentStart;
                    if (line.WrittenCount + segmentLength > MaximumJournalLineBytes)
                    {
                        throw SessionLifecycleException.Corrupt(sessionId);
                    }

                    line.Write(new ReadOnlySpan<byte>(readBuffer, segmentStart, segmentLength));
                    if (line.WrittenCount == 0)
                    {
                        throw SessionLifecycleException.Corrupt(sessionId);
                    }

                    var completed = line.WrittenMemory.ToArray();
                    line.Clear();
                    segmentStart = index + 1;
                    yield return completed;
                }

                var trailingLength = read - segmentStart;
                if (line.WrittenCount + trailingLength > MaximumJournalLineBytes)
                {
                    throw SessionLifecycleException.Corrupt(sessionId);
                }

                if (trailingLength > 0)
                {
                    line.Write(new ReadOnlySpan<byte>(readBuffer, segmentStart, trailingLength));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    /// <summary>
    /// 功能：严格验证 portable header 并返回 workspace 与 createdAt。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="header">第一条完整 JSON 行。</param>
    /// <param name="sessionId">目录要求的 Session ID。</param>
    /// <returns>安全摘要所需的 header 字段。</returns>
    private static (string Workspace, DateTimeOffset CreatedAt) InspectHeader(
        JsonElement header,
        string sessionId)
    {
        RequireObject(header);
        EnsureOnlyProperties(
            header,
            "kind",
            "schemaVersion",
            "sessionId",
            "createdAt",
            "workspace",
            "createdBy",
            "provenance",
            "extensions");
        var workspace = RequireString(header, "workspace", maximumLength: 32_768);
        if (RequireString(header, "kind") != "session" ||
            RequireString(header, "schemaVersion") != SchemaVersion ||
            RequireString(header, "sessionId") != sessionId ||
            workspace.Contains('\0', StringComparison.Ordinal))
        {
            throw SessionLifecycleException.Corrupt(sessionId);
        }

        if (header.TryGetProperty("createdBy", out var createdBy))
        {
            RequireObject(createdBy);
            if (!ImplementationLanguages.Contains(RequireString(createdBy, "language")))
            {
                throw SessionLifecycleException.Corrupt(sessionId);
            }
        }

        RequireOptionalObject(header, "provenance");
        RequireOptionalObject(header, "extensions");
        return (workspace, RequireUtcTime(header, "createdAt", sessionId));
    }

    /// <summary>
    /// 功能：严格验证通用 journal record envelope 与连续身份关系。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="record">完整记录 JSON。</param>
    /// <param name="sessionId">期望 Session ID。</param>
    /// <param name="sequence">从 1 开始的期望连续 seq。</param>
    /// <param name="recordIds">此前完整记录 ID 集合。</param>
    /// <returns>标题和更新时间所需的 kind、time、data。</returns>
    private static (string Kind, DateTimeOffset Time, JsonElement Data) InspectRecord(
        JsonElement record,
        string sessionId,
        long sequence,
        HashSet<string> recordIds)
    {
        RequireObject(record);
        EnsureOnlyProperties(
            record,
            "schemaVersion",
            "kind",
            "recordId",
            "sessionId",
            "seq",
            "parentId",
            "time",
            "data",
            "extensions");
        var kind = RequireString(record, "kind");
        var recordId = RequireString(record, "recordId", maximumLength: 128);
        if (RequireString(record, "schemaVersion") != SchemaVersion ||
            RequireString(record, "sessionId") != sessionId ||
            RequireSafeInteger(record, "seq") != sequence ||
            !IsValidOpaqueId(recordId) ||
            !recordIds.Add(recordId) ||
            !CoreRecordKinds.Contains(kind))
        {
            throw SessionLifecycleException.Corrupt(sessionId);
        }

        var parent = RequireProperty(record, "parentId");
        if (sequence == 1)
        {
            if (parent.ValueKind != JsonValueKind.Null)
            {
                throw SessionLifecycleException.Corrupt(sessionId);
            }
        }
        else if (parent.ValueKind != JsonValueKind.String ||
            !recordIds.Contains(parent.GetString() ?? string.Empty))
        {
            throw SessionLifecycleException.Corrupt(sessionId);
        }

        var data = RequireProperty(record, "data");
        RequireObject(data);
        RequireOptionalObject(record, "extensions");
        return (kind, RequireUtcTime(record, "time", sessionId), data.Clone());
    }

    /// <summary>
    /// 功能：从首条 user message 的文本内容生成有界单行标题。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">record kind。</param>
    /// <param name="data">record data。</param>
    /// <returns>是否为用户消息，以及该消息首个文本形成的可选标题。</returns>
    private static (bool IsUserMessage, string? Title) ExtractUserTitle(
        string kind,
        JsonElement data)
    {
        if (kind != "message.appended")
        {
            return (false, null);
        }

        var message = RequireProperty(data, "message");
        RequireObject(message);
        var role = RequireString(message, "role", maximumLength: 32);
        var content = RequireProperty(message, "content");
        if (content.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("message content is invalid");
        }

        if (role != "user")
        {
            return (false, null);
        }

        var builder = new StringBuilder(capacity: 96);
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                type.GetString() != "text" ||
                !item.TryGetProperty("text", out var text) ||
                text.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            AppendNormalizedTitle(builder, text.GetString()!);
            if (builder.Length >= 96)
            {
                break;
            }
        }

        return (true, builder.Length == 0 ? null : builder.ToString());
    }

    /// <summary>
    /// 功能：把任意用户文本压缩为空白分隔且最多 96 字符的安全标题。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="builder">跨 text block 复用的标题缓冲。</param>
    /// <param name="value">用户消息文本。</param>
    private static void AppendNormalizedTitle(StringBuilder builder, string value)
    {
        var pendingSpace = builder.Length > 0;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace && builder.Length < 96)
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            if (builder.Length >= 96)
            {
                break;
            }

            builder.Append(character);
        }
    }

    /// <summary>
    /// 功能：从 portable workspace 文本提取不依赖宿主路径访问的安全 basename。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="workspace">header 中的非空 workspace。</param>
    /// <returns>最多 128 字符的项目名，无法提取时为固定中文占位。</returns>
    private static string ProjectName(string workspace)
    {
        var trimmed = workspace.TrimEnd('/', '\\');
        var separator = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        var value = separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
        if (value.Length == 0 || value.Any(char.IsControl))
        {
            return "未知项目";
        }

        return value.Length <= 128 ? value : value[..128];
    }

    /// <summary>
    /// 功能：构造损坏 Session 的固定安全 UI 摘要，不泄露路径或 journal 内容。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">安全目录 ID。</param>
    /// <param name="archived">归档 presence。</param>
    /// <returns>固定健康降级摘要。</returns>
    private static SessionSummary CreateCorruptSummary(string sessionId, bool archived)
    {
        return new SessionSummary(
            sessionId,
            "会话数据不可用",
            "未知项目",
            DateTimeOffset.UnixEpoch,
            archived);
    }

    /// <summary>
    /// 功能：在移动前递归证明 Session 只包含有界普通目录树与普通文件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="directory">当前待验证真实目录。</param>
    /// <param name="sessionId">用于脱敏错误的 Session ID。</param>
    /// <param name="depth">当前递归深度。</param>
    /// <param name="observedEntries">跨递归累计条目数。</param>
    /// <param name="heldLockPath">当前进程已验证并独占打开、无需再次打开的根 lock 文件。</param>
    /// <remarks>不变量：本方法不修改目录树，也不跟随链接或打开 device/FIFO。</remarks>
    private static void ValidateOrdinaryTree(
        string directory,
        string sessionId,
        int depth,
        ref int observedEntries,
        string heldLockPath)
    {
        if (depth > MaximumDeleteDepth || !IsRealDirectory(directory))
        {
            throw SessionLifecycleException.DeleteUnsafe(sessionId);
        }

        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (++observedEntries > MaximumDeleteEntries)
            {
                throw SessionLifecycleException.DeleteUnsafe(sessionId);
            }

            entry.Refresh();
            if (!entry.Exists || entry.LinkTarget is not null ||
                (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw SessionLifecycleException.DeleteUnsafe(sessionId);
            }

            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                if (entry.Name == "writer.lock.d")
                {
                    throw SessionLifecycleException.Busy(sessionId);
                }

                ValidateOrdinaryTree(
                    entry.FullName,
                    sessionId,
                    depth + 1,
                    ref observedEntries,
                    heldLockPath);
            }
            else if (!PathsEqual(entry.FullName, heldLockPath))
            {
                VerifyRegularFile(entry.FullName, sessionId);
            }
        }
    }

    /// <summary>
    /// 功能：递归删除已移入 tombstone 的普通文件树，并拒绝链接、device 与 writer lease。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="directory">当前真实 tombstone 子目录。</param>
    /// <param name="sessionId">用于脱敏错误的 Session ID。</param>
    /// <param name="depth">当前递归深度。</param>
    /// <param name="deletedEntries">跨递归累计条目数。</param>
    private static void DeleteOrdinaryTree(
        string directory,
        string sessionId,
        int depth,
        ref int deletedEntries)
    {
        if (depth > MaximumDeleteDepth || !IsRealDirectory(directory))
        {
            throw SessionLifecycleException.DeleteUnsafe(sessionId);
        }

        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (++deletedEntries > MaximumDeleteEntries)
            {
                throw SessionLifecycleException.DeleteUnsafe(sessionId);
            }

            entry.Refresh();
            if (!entry.Exists || entry.LinkTarget is not null ||
                (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw SessionLifecycleException.DeleteUnsafe(sessionId);
            }

            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                if (entry.Name == "writer.lock.d")
                {
                    throw SessionLifecycleException.Busy(sessionId);
                }

                DeleteOrdinaryTree(entry.FullName, sessionId, depth + 1, ref deletedEntries);
            }
            else
            {
                VerifyRegularFile(entry.FullName, sessionId);
                File.Delete(entry.FullName);
            }
        }

        Directory.Delete(directory, recursive: false);
    }

    /// <summary>
    /// 功能：按平台路径大小写规则判断两个绝对路径是否相同。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="left">第一个绝对路径。</param>
    /// <param name="right">第二个绝对路径。</param>
    /// <returns>路径指向同一词法位置时 true。</returns>
    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：在 Unix 强制刷新关键 rename/unlink 的父目录项，Windows 采用平台 rename 语义。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="directory">已验证真实且不跟随链接的父目录。</param>
    /// <remarks>不变量：Linux/macOS 的 open 或 fsync 失败会阻止 durable 成功回执。</remarks>
    /// <exception cref="IOException">Unix 目录无法 no-follow 打开或 fsync。</exception>
    private static void FlushDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var flags = UnixOpenReadOnly |
            (OperatingSystem.IsLinux()
                ? LinuxOpenCloseOnExec | LinuxOpenNoFollow
                : MacOpenCloseOnExec | MacOpenNoFollow);
        var descriptor = OpenUnixPath(directory, flags);
        if (descriptor < 0)
        {
            throw new IOException("session lifecycle directory flush failed");
        }

        using var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        if (Fsync(descriptor) != 0)
        {
            throw new IOException("session lifecycle directory flush failed");
        }
    }

    /// <summary>
    /// 功能：以 no-follow 句柄证明删除目标是可 seek 的普通文件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">tombstone 内部候选叶文件。</param>
    /// <param name="sessionId">用于脱敏错误的 Session ID。</param>
    private static void VerifyRegularFile(string path, string sessionId)
    {
        try
        {
            using var stream = OpenRegularReadOnly(path, sessionId);
            _ = stream.Length;
        }
        catch (SessionLifecycleException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw SessionLifecycleException.DeleteUnsafe(sessionId);
        }
    }

    /// <summary>
    /// 功能：在 Unix 使用 O_NOFOLLOW/O_NONBLOCK，其他平台双检句柄后打开普通只读文件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">可信父目录下固定叶路径。</param>
    /// <param name="sessionId">用于脱敏错误的 Session ID。</param>
    /// <returns>可 seek 的普通文件流。</returns>
    private static FileStream OpenRegularReadOnly(string path, string sessionId)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var flags = UnixOpenReadOnly |
                (OperatingSystem.IsLinux()
                    ? LinuxOpenCloseOnExec | LinuxOpenNoFollow | LinuxOpenNonBlocking
                    : MacOpenCloseOnExec | MacOpenNoFollow | MacOpenNonBlocking);
            var descriptor = OpenUnixPath(path, flags);
            if (descriptor < 0)
            {
                throw SessionLifecycleException.Corrupt(sessionId);
            }

            handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        }
        else
        {
            var info = new FileInfo(path);
            info.Refresh();
            if (!info.Exists || info.LinkTarget is not null ||
                (info.Attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            {
                throw SessionLifecycleException.Corrupt(sessionId);
            }

            handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.SequentialScan);
        }

        try
        {
            var attributes = File.GetAttributes(handle);
            if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            {
                throw SessionLifecycleException.Corrupt(sessionId);
            }

            var stream = new FileStream(handle, FileAccess.Read, bufferSize: 16_384, isAsync: false);
            if (!stream.CanSeek)
            {
                stream.Dispose();
                throw SessionLifecycleException.Corrupt(sessionId);
            }

            return stream;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 功能：创建目录并在 Unix 固定为用户 0700。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">可信状态目录。</param>
    private static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// 功能：在 Unix 把状态或 lock 普通文件权限固定为 0600。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">已创建普通文件。</param>
    private static void RestrictFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// 功能：要求目录存在且不是 symlink/reparse point。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">候选目录。</param>
    private static void RequireRealDirectory(string path)
    {
        if (!IsRealDirectory(path))
        {
            throw SessionLifecycleException.Store("session_lifecycle_store_invalid");
        }
    }

    /// <summary>
    /// 功能：判断路径当前是非链接普通目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">候选路径。</param>
    /// <returns>存在且无 LinkTarget/ReparsePoint 时 true。</returns>
    private static bool IsRealDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        info.Refresh();
        return info.Exists && info.LinkTarget is null &&
            (info.Attributes & FileAttributes.ReparsePoint) == 0;
    }

    /// <summary>
    /// 功能：路径存在时拒绝文件或目录 reparse/link。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">固定状态路径。</param>
    /// <param name="sessionId">有 Session 上下文时用于 corrupt 错误，否则映射 store invalid。</param>
    private static void RejectReparseIfPresent(string path, string? sessionId)
    {
        var file = new FileInfo(path);
        file.Refresh();
        var directory = new DirectoryInfo(path);
        directory.Refresh();
        var isReparse = file.LinkTarget is not null || directory.LinkTarget is not null ||
            (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0) ||
            (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0);
        if (!isReparse)
        {
            return;
        }

        throw sessionId is null
            ? SessionLifecycleException.Store("session_lifecycle_store_invalid")
            : SessionLifecycleException.Corrupt(sessionId);
    }

    /// <summary>
    /// 功能：逐组件拒绝候选绝对路径中已经存在的 symlink、reparse point 或非目录祖先。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">待用于状态或 workspace 边界的绝对路径。</param>
    /// <remarks>不变量：缺失后缀只会在最后一个已证明真实的祖先下创建。</remarks>
    private static void RejectExistingAncestorReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var rootPath = Path.GetPathRoot(fullPath)
            ?? throw SessionLifecycleException.Store("session_lifecycle_store_invalid");
        var current = rootPath;
        var relative = fullPath[rootPath.Length..];
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            var directory = new DirectoryInfo(current);
            directory.Refresh();
            var file = new FileInfo(current);
            file.Refresh();
            if (directory.LinkTarget is not null || file.LinkTarget is not null ||
                (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) ||
                (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw SessionLifecycleException.Store("session_lifecycle_store_invalid");
            }

            if (file.Exists || !directory.Exists)
            {
                if (file.Exists)
                {
                    throw SessionLifecycleException.Store("session_lifecycle_store_invalid");
                }

                break;
            }
        }
    }

    /// <summary>
    /// 功能：判断 candidate 等于 root 或处于其目录边界内。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="candidate">绝对候选路径。</param>
    /// <param name="root">绝对根路径。</param>
    /// <returns>未越界时 true。</returns>
    private static bool IsWithin(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(candidate);
        return string.Equals(normalizedCandidate, normalizedRoot, comparison) ||
            normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// 功能：验证 Session ID 符合 portable opaque ID 且不能形成路径。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">候选 ID。</param>
    /// <exception cref="SessionLifecycleException">ID 无效。</exception>
    internal static void ValidateSessionId(string sessionId)
    {
        if (!IsValidSessionId(sessionId))
        {
            throw new SessionLifecycleException(new PortableError(
                -32602,
                "session id is invalid",
                false,
                new ErrorDetails("invalid_params", Field: "sessionId")));
        }
    }

    /// <summary>
    /// 功能：判断字符串符合 Session opaque ID 的 1..128 字符 allowlist。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选 ID。</param>
    /// <returns>安全时 true。</returns>
    private static bool IsValidSessionId(string? value)
    {
        return value is not null && value.Length is >= 1 and <= 128 &&
            char.IsAsciiLetterOrDigit(value[0]) &&
            value.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');
    }

    /// <summary>
    /// 功能：判断 record/message opaque ID 使用同一安全字符集。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选 opaque ID。</param>
    /// <returns>安全时 true。</returns>
    private static bool IsValidOpaqueId(string value)
    {
        return IsValidSessionId(value);
    }

    /// <summary>
    /// 功能：取得必需 JSON 属性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>存在属性。</returns>
    private static JsonElement RequireProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property)
            ? property
            : throw new JsonException("required property is missing");
    }

    /// <summary>
    /// 功能：取得非空且有界 JSON 字符串。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <param name="maximumLength">最大字符数。</param>
    /// <returns>字符串值。</returns>
    private static string RequireString(
        JsonElement element,
        string name,
        int maximumLength = 256)
    {
        var property = RequireProperty(element, name);
        var value = property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        return value is not null && value.Length is >= 1 && value.Length <= maximumLength
            ? value
            : throw new JsonException("string property is invalid");
    }

    /// <summary>
    /// 功能：取得 1..JavaScript 安全上限的整数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>安全整数。</returns>
    private static long RequireSafeInteger(JsonElement element, string name)
    {
        var property = RequireProperty(element, name);
        return property.TryGetInt64(out var value) && value is >= 1 and <= MaxSafeInteger
            ? value
            : throw new JsonException("integer property is invalid");
    }

    /// <summary>
    /// 功能：取得必须以 Z 结尾的 UTC 时间。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">时间属性名。</param>
    /// <param name="sessionId">用于脱敏损坏错误的 Session ID。</param>
    /// <returns>零偏移时间。</returns>
    private static DateTimeOffset RequireUtcTime(
        JsonElement element,
        string name,
        string sessionId)
    {
        var text = RequireString(element, name, maximumLength: 64);
        if (!text.EndsWith('Z') ||
            !DateTimeOffset.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var value) ||
            value.Offset != TimeSpan.Zero)
        {
            throw SessionLifecycleException.Corrupt(sessionId);
        }

        return value;
    }

    /// <summary>
    /// 功能：要求 JSON 元素是对象。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">候选元素。</param>
    private static void RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("object is required");
        }
    }

    /// <summary>
    /// 功能：可选属性存在时要求其为对象。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">属性名。</param>
    private static void RequireOptionalObject(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value))
        {
            RequireObject(value);
        }
    }

    /// <summary>
    /// 功能：拒绝 closed JSON 对象中的未知字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">已确认对象。</param>
    /// <param name="allowed">允许属性名。</param>
    private static void EnsureOnlyProperties(
        JsonElement element,
        params ReadOnlySpan<string> allowed)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new JsonException("unknown property");
            }
        }
    }

    /// <summary>
    /// 功能：递归拒绝 JSON object 的重复 key。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">当前 JSON 节点。</param>
    private static void ValidateNoDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException("duplicate property");
                }

                ValidateNoDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateNoDuplicateKeys(item);
            }
        }
    }

    /// <summary>
    /// 功能：创建禁止注释、trailing comma 且有界深度的 JSON 文档选项。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>严格解析选项。</returns>
    private static JsonDocumentOptions StrictDocumentOptions()
    {
        return new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 128,
        };
    }

    /// <summary>
    /// 功能：通过 libc open 对 journal 或删除叶应用 no-follow、nonblock 与 close-on-exec。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">进程内从可信根派生的路径。</param>
    /// <param name="flags">平台 O_RDONLY/O_NOFOLLOW/O_NONBLOCK/O_CLOEXEC 组合。</param>
    /// <returns>成功时非负 descriptor，失败为 -1。</returns>
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenUnixPath(string path, int flags);

    /// <summary>
    /// 功能：调用 libc fsync 持久化已打开的生命周期父目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="descriptor">由 OpenUnixPath 取得的目录 descriptor。</param>
    /// <returns>成功为零，失败为 -1。</returns>
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int descriptor);
}
