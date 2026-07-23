using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using QxnmForge.Domain;
using QxnmForge.Serialization;

namespace QxnmForge.Session;

/// <summary>
/// 功能：表示 durable append 完成后才能观察到的 journal 记录回执。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="RecordId">会话内唯一记录 ID。</param>
/// <param name="Seq">连续 journal 序号。</param>
public sealed record JournalAppendReceipt(string RecordId, long Seq);

/// <summary>
/// 功能：表示图片完成 journal 批次写失败，并明确原长度回滚是否已 durable 完成。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class JournalBatchAppendException : IOException
{
    /// <summary>
    /// 功能：创建不携带路径或 journal 正文的批次失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="rollbackCompleted">journal 是否已截回批次前长度并强制落盘。</param>
    internal JournalBatchAppendException(bool rollbackCompleted)
        : base("journal image completion batch append failed")
    {
        RollbackCompleted = rollbackCompleted;
    }

    /// <summary>
    /// 功能：取得调用方能否确认本批没有任何 durable journal 引用。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal bool RollbackCompleted { get; }
}

/// <summary>
/// 功能：表示无法安全打开或继续写入的 portable journal。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class JournalCorruptException : Exception
{
    /// <summary>
    /// 功能：用不包含 journal 原文的安全诊断创建损坏异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="message">安全诊断消息。</param>
    public JournalCorruptException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// 功能：用安全诊断和根异常创建损坏异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="message">安全诊断消息。</param>
    /// <param name="innerException">底层解析或 I/O 异常。</param>
    public JournalCorruptException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 功能：表示 journal 基础 JSON 可读但 tree 或 compaction 跨记录语义不能安全解释。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class JournalIncompatibleException : Exception
{
    /// <summary>
    /// 功能：创建不泄露消息正文、record ID 或主机路径的 journal 不兼容异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="message">稳定安全诊断。</param>
    public JournalIncompatibleException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 功能：表示 session/get 返回的 durable 消息与事件投影。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="SessionId">会话 ID。</param>
/// <param name="LatestSeq">整个会话最新事件序号，不是 journal 记录序号。</param>
/// <param name="ActiveRunId">真实本进程 active run ID；无 active run 时为 null。</param>
/// <param name="SelectedHeadRecordId">当前 selected parent chain 的 head；空会话为 null。</param>
/// <param name="CompactionRecordId">selected chain 上最新生效的 context.compacted；没有时为 null。</param>
/// <param name="Messages">选定线性分支上的完整 portable message.appended 消息。</param>
/// <param name="Events">event.seq 严格大于 afterSeq 的 durable exact event envelopes。</param>
public sealed record PortableSessionSnapshot(
    string SessionId,
    long LatestSeq,
    string? ActiveRunId,
    string? SelectedHeadRecordId,
    string? CompactionRecordId,
    IReadOnlyList<JsonElement> Messages,
    IReadOnlyList<JsonElement> Events);

/// <summary>
/// 功能：以跨语言 JSONL schema 和 append-before-ack 规则维护单个会话。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed partial class PortableSessionJournal : IAsyncDisposable
{
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private const int UnixOpenReadOnly = 0;
    private const int LinuxOpenCloseOnExec = 0x80000;
    private const int LinuxOpenNoFollow = 0x20000;
    private const int MacOpenCloseOnExec = 0x1000000;
    private const int MacOpenNoFollow = 0x100;
    private const string RecoveryNamespace = "org.agent-session.recovery";
    private const string RecoveryBackupPrefix = "journal.recovery-";
    private const string RecoveryBackupSuffix = ".bak";

    private static readonly SearchValues<char> OpaqueIdCharacters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._:-");

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

    private readonly SessionCoordinationLease coordinationLease;
    private readonly WriterLease writerLease;
    private readonly FileStream lockStream;
    private readonly FileStream journalStream;
    private readonly SemaphoreSlim appendGate = new(1, 1);
    private readonly HashSet<string> recordIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JournalTreeRecord> treeRecords = new(StringComparer.Ordinal);
    private readonly HashSet<string> messageIds = new(StringComparer.Ordinal);
    private readonly List<PersistedEvent> events = [];
    private readonly HashSet<string> terminalRunIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolIntentState> toolIntents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> toolResults = new(StringComparer.Ordinal);
    private readonly HashSet<string> toolMessageCallIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> startedToolCallIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecoveryApprovalState> approvalRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> approvalResolutionChoices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecoveredTerminalState> recoveredTerminals = new(StringComparer.Ordinal);
    private readonly HashSet<string> terminalEventRunIds = new(StringComparer.Ordinal);
    private int? imageCompletionBatchFailureAfterBytesForTest;
    private string? lastRecordId;
    private string? activeRunId;
    private long journalSequence;
    private long eventSequence;
    private bool disposed;

    /// <summary>
    /// 功能：保存已在内存预构造、尚未写入 journal 的单条图片完成批次记录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="Line">最终单行 wire 记录。</param>
    /// <param name="TreeRecord">成功 flush 后加入 selected tree 的投影。</param>
    /// <param name="Kind">核心记录 kind。</param>
    /// <param name="Data">生命周期独立的 data object。</param>
    private sealed record PreparedImageCompletionRecord(
        JournalRecordLine Line,
        JournalTreeRecord TreeRecord,
        string Kind,
        JsonElement Data);

    /// <summary>
    /// 功能：保存已独占打开的 lock 和 journal 句柄。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">已验证会话 ID。</param>
    /// <param name="directoryPath">会话目录。</param>
    /// <param name="journalPath">journal 文件路径。</param>
    /// <param name="coordinationLease">固定路径 per-ID 打开/删除 coordination lease。</param>
    /// <param name="writerLease">已提交 owner 且 listener 开放的 portable writer lease。</param>
    /// <param name="lockStream">独占 lock 句柄。</param>
    /// <param name="journalStream">append journal 句柄。</param>
    private PortableSessionJournal(
        string sessionId,
        string directoryPath,
        string journalPath,
        SessionCoordinationLease coordinationLease,
        WriterLease writerLease,
        FileStream lockStream,
        FileStream journalStream)
    {
        SessionId = sessionId;
        DirectoryPath = directoryPath;
        JournalPath = journalPath;
        this.coordinationLease = coordinationLease;
        this.writerLease = writerLease;
        this.lockStream = lockStream;
        this.journalStream = journalStream;
    }

    /// <summary>
    /// 功能：取得 journal 所属的 opaque 会话 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string SessionId { get; }

    /// <summary>
    /// 功能：取得会话目录，仅供本地诊断与测试，不进入 wire DTO。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string DirectoryPath { get; }

    /// <summary>
    /// 功能：取得 journal 文件路径，仅供本地诊断与测试，不进入 wire DTO。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string JournalPath { get; }

    /// <summary>
    /// 功能：取得最近已 durable flush 的事件序号。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public long LatestEventSequence => Interlocked.Read(ref eventSequence);

    /// <summary>
    /// 功能：取得打开恢复时读取或追加的 terminal run ID 副本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal IReadOnlyCollection<string> RestoredTerminalRunIds => terminalRunIds.ToArray();

    /// <summary>
    /// 功能：独占打开或创建 portable 会话 journal，恢复歧义工具与中断 run 后才返回。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionsRoot">受调用者控制的 sessions 根目录。</param>
    /// <param name="sessionId">符合 opaque ID 语法且不可形成路径的会话 ID。</param>
    /// <param name="workspace">已选择工作区；新 header 保存其绝对规范路径。</param>
    /// <param name="cancellationToken">打开、扫描和恢复持久化取消信号。</param>
    /// <returns>持有独占 writer lock 且不存在 stale active run 的 journal。</returns>
    /// <remarks>不变量：LF committed 行不会被重写；无 LF 尾部先完整备份再截断并记录诊断；未知工具结果只记录 outcomeKnown=false，绝不执行工具。</remarks>
    /// <exception cref="ArgumentException">sessionId 或 workspace 无效。</exception>
    /// <exception cref="IOException">会话已锁定或持久化失败。</exception>
    /// <exception cref="JournalCorruptException">已有 committed 行违反 UTF-8/JSON/schema/state，或固定 recovery 备份无法证明逐字节一致。</exception>
    public static async Task<PortableSessionJournal> OpenAsync(
        string sessionsRoot,
        string sessionId,
        string workspace,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        var canonicalWorkspace = Path.GetFullPath(workspace);
        if (!Directory.Exists(canonicalWorkspace))
        {
            throw new ArgumentException("workspace must exist", nameof(workspace));
        }

        var root = Path.GetFullPath(sessionsRoot);
        var directory = Path.Combine(root, sessionId);
        SessionCoordinationLease? coordinationLease = null;
        WriterLease? writerLease = null;
        FileStream? lockHandle = null;
        FileStream? journalHandle = null;
        try
        {
            Directory.CreateDirectory(root);
            coordinationLease = await SessionCoordinationLease.AcquireAsync(
                root,
                sessionId,
                cancellationToken).ConfigureAwait(false);
            if (SessionCoordinationLease.HasPendingTombstone(root, sessionId))
            {
                throw SessionWriterLeaseException.Locked();
            }

            Directory.CreateDirectory(directory);
            writerLease = await WriterLease.AcquireAsync(
                directory,
                sessionId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            lockHandle = OpenAdvisoryLock(directory);
            await writerLease.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.Combine(directory, "artifacts"));
            var journalPath = Path.Combine(directory, "journal.jsonl");
            journalHandle = new FileStream(
                journalPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 16_384,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            var journal = new PortableSessionJournal(
                sessionId,
                directory,
                journalPath,
                coordinationLease,
                writerLease,
                lockHandle,
                journalHandle);
            coordinationLease = null;
            writerLease = null;
            lockHandle = null;
            journalHandle = null;
            try
            {
                if (journal.journalStream.Length == 0)
                {
                    await journal.WriteHeaderAsync(canonicalWorkspace, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var originalLength = journal.journalStream.Length;
                    var committedLength = await journal.FindCommittedLengthAsync(
                        originalLength,
                        cancellationToken).ConfigureAwait(false);
                    if (committedLength > 0)
                    {
                        await journal.LoadStateAsync(committedLength, cancellationToken).ConfigureAwait(false);
                    }

                    if (committedLength < originalLength)
                    {
                        await journal.RecoverUncommittedTailAsync(
                            canonicalWorkspace,
                            originalLength,
                            committedLength,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else if (committedLength == 0)
                    {
                        throw new JournalCorruptException("journal header is missing");
                    }

                    await journal.RecoverInterruptedStateAsync(cancellationToken).ConfigureAwait(false);
                }

                return journal;
            }
            catch
            {
                await journal.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception openException)
        {
            Exception? cleanupException = null;
            if (journalHandle is not null)
            {
                try
                {
                    await journalHandle.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupException = exception;
                }
            }

            if (writerLease is not null)
            {
                try
                {
                    await writerLease.BeginReleaseAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupException ??= exception;
                }
            }

            if (lockHandle is not null)
            {
                try
                {
                    await lockHandle.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupException ??= exception;
                }
            }

            if (writerLease is not null)
            {
                try
                {
                    await writerLease.FinishReleaseAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupException ??= exception;
                }
            }

            if (coordinationLease is not null)
            {
                try
                {
                    await coordinationLease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupException ??= exception;
                }
            }

            if (cleanupException is not null)
            {
                ExceptionDispatchInfo.Capture(cleanupException).Throw();
            }

            ExceptionDispatchInfo.Capture(openException).Throw();
            throw new UnreachableException();
        }
    }

    /// <summary>
    /// 功能：追加完整 immutable 记录并强制落盘，只有 durability primitive 成功后才返回回执。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">SPEC/session.md 中的核心记录 kind。</param>
    /// <param name="data">符合该 kind schema 的语言中立 DTO。</param>
    /// <param name="cancellationToken">写入开始前及异步写入期间的取消信号。</param>
    /// <returns>durable 后的记录 ID 和连续序号。</returns>
    /// <remarks>不变量：序号只在 Flush(true) 成功后推进；并行调用不会交错字节。</remarks>
    /// <exception cref="ObjectDisposedException">journal 已关闭。</exception>
    /// <exception cref="ArgumentException">kind 未知或 data 不是 JSON object。</exception>
    /// <exception cref="IOException">写入或持久化失败；调用者不得发送成功响应或事件。</exception>
    public async Task<JournalAppendReceipt> AppendAsync(
        string kind,
        object data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (kind is "branch.selected" or "context.compacted")
        {
            throw new ArgumentException(
                "branch and compaction records require their atomic mutation API",
                nameof(kind));
        }

        await appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AppendLockedAsync(kind, data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            appendGate.Release();
        }
    }

    /// <summary>
    /// 功能：仅供测试让下一次图片完成批次在单次 journal write 的指定字节后失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="byteCount">写入此前缀后抛出合成 IOException；零表示写入前失败。</param>
    /// <remarks>不变量：hook 一次性消费且只影响图片完成批次；生产路径从不调用。</remarks>
    internal void FailNextImageCompletionBatchAfterBytesForTest(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        imageCompletionBatchFailureAfterBytesForTest = byteCount;
    }

    /// <summary>
    /// 功能：在同一 append lock 内预构造全部 artifact.created 与 assistant message，并以一次 write/flush 提交。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="artifacts">已整批发布文件但尚未被 journal 引用的有序图片引用。</param>
    /// <param name="assistantMessage">只含 portable text/image_ref 的完整 assistant 消息。</param>
    /// <param name="runId">当前 active run ID。</param>
    /// <param name="turnId">当前 Provider turn ID。</param>
    /// <param name="cancellationToken">等待锁和开始提交前的取消信号。</param>
    /// <returns>同批最后一条 assistant message.appended 的 durable 回执。</returns>
    /// <remarks>不变量：全部记录先验证并串成连续 parent/seq，再一次写入并 Flush(true)；开始写后忽略普通取消。写入或 flush 失败时在锁内截回原长度，成功前不推进任何内存投影。</remarks>
    /// <exception cref="ArgumentException">批次为空、超限、引用重复或输入基础形状无效。</exception>
    /// <exception cref="JournalCorruptException">run、assistant 或 image_ref 绑定不满足当前 journal 状态。</exception>
    /// <exception cref="IOException">批量写、flush 或失败回滚无法 durable 完成。</exception>
    internal async Task<JournalAppendReceipt> AppendImageCompletionBatchAsync(
        IReadOnlyList<ArtifactReference> artifacts,
        JsonElement assistantMessage,
        string runId,
        string turnId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (artifacts.Count is < 1 or > 8 ||
            !IsValidOpaqueId(runId) ||
            !IsValidOpaqueId(turnId))
        {
            throw new ArgumentException("image completion batch shape is invalid", nameof(artifacts));
        }

        await appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writerLease.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
            var artifactIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var artifact in artifacts)
            {
                try
                {
                    ImageArtifactValidation.ValidateReference(artifact, MaxSafeInteger);
                }
                catch (ArgumentException exception)
                {
                    throw new ArgumentException(
                        "image completion artifact is invalid",
                        nameof(artifacts),
                        exception);
                }

                if (!artifactIds.Add(artifact.ArtifactId))
                {
                    throw new ArgumentException(
                        "image completion artifacts must be unique",
                        nameof(artifacts));
                }
            }

            ValidatePortableMessage(assistantMessage);
            var messageId = RequireString(assistantMessage, "messageId");
            if (activeRunId != runId ||
                RequireString(assistantMessage, "role") != "assistant" ||
                RequireString(assistantMessage, "finishReason") != "stop" ||
                messageIds.Contains(messageId))
            {
                throw new JournalCorruptException("image completion message state is invalid");
            }

            var referencedArtifacts = new List<ArtifactReference>();
            foreach (var block in RequireProperty(assistantMessage, "content").EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var type) || type.GetString() != "image_ref")
                {
                    continue;
                }

                if (!block.TryGetProperty("artifact", out var artifactElement) ||
                    artifactElement.ValueKind != JsonValueKind.Object)
                {
                    throw new JournalCorruptException("image completion reference is invalid");
                }

                try
                {
                    referencedArtifacts.Add(
                        artifactElement.Deserialize<ArtifactReference>(JsonDefaults.Options)
                        ?? throw new JournalCorruptException("image completion reference is invalid"));
                }
                catch (JsonException exception)
                {
                    throw new JournalCorruptException("image completion reference is invalid", exception);
                }
            }

            if (referencedArtifacts.Count != artifacts.Count ||
                referencedArtifacts.Where((artifact, index) =>
                    !ArtifactCoreEquals(artifact, artifacts[index])).Any())
            {
                throw new JournalCorruptException("image completion references do not match the batch");
            }

            var items = new List<(string Kind, object Data)>(artifacts.Count + 1);
            items.AddRange(artifacts.Select(static artifact =>
                ("artifact.created", (object)new { Artifact = artifact })));
            items.Add((
                "message.appended",
                new { Message = assistantMessage.Clone(), RunId = runId, TurnId = turnId }));

            var prepared = new List<PreparedImageCompletionRecord>(items.Count);
            var sequence = journalSequence;
            var parentId = lastRecordId;
            foreach (var item in items)
            {
                var data = JsonSerializer.SerializeToElement(item.Data, JsonDefaults.Options);
                if (data.ValueKind != JsonValueKind.Object)
                {
                    throw new ArgumentException("journal data must serialize as an object", nameof(artifacts));
                }

                sequence++;
                var recordId = "record-" + Guid.NewGuid().ToString("N");
                var treeRecord = new JournalTreeRecord(
                    recordId,
                    sequence,
                    parentId,
                    item.Kind,
                    data.Clone());
                var line = new JournalRecordLine(
                    "0.1",
                    item.Kind,
                    recordId,
                    SessionId,
                    sequence,
                    parentId,
                    DateTimeOffset.UtcNow,
                    data);
                prepared.Add(new PreparedImageCompletionRecord(line, treeRecord, item.Kind, data));
                parentId = recordId;
            }

            var output = new ArrayBufferWriter<byte>();
            foreach (var item in prepared)
            {
                output.Write(JsonSerializer.SerializeToUtf8Bytes(item.Line, JsonDefaults.Options));
                output.Write("\n"u8);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var originalLength = journalStream.Length;
            journalStream.Position = originalLength;
            var injectedFailure = imageCompletionBatchFailureAfterBytesForTest;
            imageCompletionBatchFailureAfterBytesForTest = null;
            try
            {
                if (injectedFailure is int prefixLength)
                {
                    var length = Math.Min(prefixLength, output.WrittenCount);
                    if (length > 0)
                    {
                        await journalStream.WriteAsync(
                            output.WrittenMemory[..length],
                            CancellationToken.None).ConfigureAwait(false);
                    }

                    throw new IOException("synthetic image completion batch failure");
                }

                await journalStream.WriteAsync(
                    output.WrittenMemory,
                    CancellationToken.None).ConfigureAwait(false);
                await journalStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                journalStream.Flush(flushToDisk: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                ObjectDisposedException or NotSupportedException)
            {
                try
                {
                    journalStream.SetLength(originalLength);
                    journalStream.Position = originalLength;
                    await journalStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    journalStream.Flush(flushToDisk: true);
                }
                catch (Exception rollbackException) when (
                    rollbackException is IOException or UnauthorizedAccessException or
                    ObjectDisposedException or NotSupportedException)
                {
                    throw new JournalBatchAppendException(rollbackCompleted: false);
                }

                throw new JournalBatchAppendException(rollbackCompleted: true);
            }

            try
            {
                foreach (var item in prepared)
                {
                    recordIds.Add(item.TreeRecord.RecordId);
                    treeRecords.Add(item.TreeRecord.RecordId, item.TreeRecord);
                    ApplyRecordProjection(item.Kind, item.Data, item.TreeRecord.Sequence);
                    journalSequence = item.TreeRecord.Sequence;
                    lastRecordId = item.TreeRecord.RecordId;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or JournalCorruptException or InvalidOperationException)
            {
                throw new JournalBatchAppendException(rollbackCompleted: false);
            }

            var completed = prepared[^1].TreeRecord;
            return new JournalAppendReceipt(completed.RecordId, completed.Sequence);
        }
        finally
        {
            appendGate.Release();
        }
    }

    /// <summary>
    /// 功能：分配会话级单调事件序号，将 exact event envelope 包入核心 event.emitted 并 durable 后返回。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="runId">所属 run ID。</param>
    /// <param name="turnId">run-only 事件为 null，其余事件为 turn ID。</param>
    /// <param name="type">规范事件类型。</param>
    /// <param name="data">对应事件 schema 数据。</param>
    /// <param name="cancellationToken">写入取消信号。</param>
    /// <returns>可在返回后安全发送到协议 stdout 的事件。</returns>
    /// <remarks>不变量：返回事件已经完整出现在 journal，seq 严格大于所有历史事件。</remarks>
    /// <exception cref="IOException">事件无法 durable 持久化，此时不得向客户端发送。</exception>
    public async Task<AgentEvent> AppendEventAsync(
        string runId,
        string? turnId,
        string type,
        object data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var portableEvent = new AgentEvent(
                SessionId,
                runId,
                turnId,
                eventSequence + 1,
                DateTimeOffset.UtcNow,
                type,
                data);
            await AppendLockedAsync(
                "event.emitted",
                new { Event = portableEvent },
                cancellationToken).ConfigureAwait(false);
            return portableEvent;
        }
        finally
        {
            appendGate.Release();
        }
    }

    /// <summary>
    /// 功能：以 expected-head CAS 选择一个 earlier quiescent record，并 durable 追加 branch.selected。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="expectedHeadRecordId">调用者观察到的 selected head。</param>
    /// <param name="targetLeafRecordId">准备成为新分支父节点的 earlier record。</param>
    /// <param name="cancellationToken">等待 journal 一致性锁和 durable 写入的取消信号。</param>
    /// <returns>目标、selection record 与新 selected head 的 durable 回执。</returns>
    /// <remarks>不变量：CAS、引用检查、quiescence 检查与单条 append 位于同一 append gate；任一固定校验失败追加零字节。</remarks>
    /// <exception cref="SessionMutationException">head stale、目标未知或目标 parent chain 非静止。</exception>
    /// <exception cref="IOException">writer lease、写入或 durability primitive 失败。</exception>
    internal async Task<BranchSelectionReceipt> SelectBranchAsync(
        string expectedHeadRecordId,
        string targetLeafRecordId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writerLease.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
            EnsureExpectedHead(expectedHeadRecordId);
            if (!treeRecords.ContainsKey(targetLeafRecordId))
            {
                throw new SessionMutationException(-32602, false, "record_not_found");
            }

            if (!IsParentChainQuiescent(targetLeafRecordId))
            {
                throw new SessionMutationException(-32010, false, "branch_not_quiescent");
            }

            var receipt = await AppendLockedAsync(
                "branch.selected",
                new { LeafRecordId = targetLeafRecordId },
                cancellationToken,
                targetLeafRecordId).ConfigureAwait(false);
            return new BranchSelectionReceipt(targetLeafRecordId, receipt.RecordId, receipt.RecordId);
        }
        finally
        {
            appendGate.Release();
        }
    }

    /// <summary>
    /// 功能：以 expected-head CAS 在静止 selected branch 上 durable 写入 summary message 与 context.compacted 配对。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="command">已通过 wire 形状验证的压缩参数和 token 估算。</param>
    /// <param name="cancellationToken">等待 journal 一致性锁和两次 durable 写入的取消信号。</param>
    /// <returns>summary message/record、compaction record 与新 selected head 的回执。</returns>
    /// <remarks>不变量：所有固定 mutation 校验在第一条 append 前完成；只有第二条 durable 后才返回成功。若第一条 durable 后发生 I/O 崩溃，它保持普通 assistant message 语义。</remarks>
    /// <exception cref="SessionMutationException">head stale、边界无效、token 增长或 selected chain 非静止。</exception>
    /// <exception cref="IOException">writer lease、写入或 durability primitive 失败。</exception>
    internal async Task<SessionCompactionReceipt> CompactAsync(
        SessionCompactionCommand command,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writerLease.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
            EnsureExpectedHead(command.ExpectedHeadRecordId);
            var sourceLeafRecordId = lastRecordId!;
            var sourceChain = BuildParentChain(sourceLeafRecordId);
            var retainedIndex = sourceChain.FindIndex(
                record => string.Equals(record.RecordId, command.FirstRetainedRecordId, StringComparison.Ordinal));
            if (retainedIndex < 0 ||
                sourceChain[retainedIndex].Kind != "message.appended" ||
                RequireString(
                    RequireProperty(sourceChain[retainedIndex].Data, "message"),
                    "role") != "user")
            {
                throw new SessionMutationException(-32602, false, "invalid_compaction_boundary");
            }

            if (command.TokensAfter > command.TokensBefore)
            {
                throw new SessionMutationException(-32602, false, "invalid_compaction_tokens");
            }

            if (!IsParentChainQuiescent(sourceLeafRecordId))
            {
                throw new SessionMutationException(-32004, true, "session_busy");
            }

            var summaryMessageId = "message-" + Guid.NewGuid().ToString("N");
            var summaryReceipt = await AppendLockedAsync(
                "message.appended",
                new
                {
                    Message = new
                    {
                        MessageId = summaryMessageId,
                        Role = "assistant",
                        Content = new[] { new TextContent(command.SummaryText) },
                        Provider = command.Provider.Clone(),
                        FinishReason = "stop",
                        Usage = command.Usage.Clone(),
                        Time = DateTimeOffset.UtcNow,
                    },
                },
                cancellationToken).ConfigureAwait(false);
            var compactionReceipt = await AppendLockedAsync(
                "context.compacted",
                new
                {
                    SourceLeafRecordId = sourceLeafRecordId,
                    SummaryMessageId = summaryMessageId,
                    command.FirstRetainedRecordId,
                    command.TokensBefore,
                    command.TokensAfter,
                    command.Strategy,
                },
                cancellationToken).ConfigureAwait(false);
            return new SessionCompactionReceipt(
                summaryMessageId,
                summaryReceipt.RecordId,
                compactionReceipt.RecordId,
                compactionReceipt.RecordId);
        }
        finally
        {
            appendGate.Release();
        }
    }

    /// <summary>
    /// 功能：返回 session/get 投影，messages 始终完整，events 仅包含 event.seq 大于 afterSeq 的记录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="afterSeq">客户端已经观察到的事件序号；零表示返回全部事件。</param>
    /// <param name="cancellationToken">等待一致性快照锁的取消信号。</param>
    /// <returns>事件计数、active run、selected head、active compaction 和动态 parent-chain 消息投影。</returns>
    /// <remarks>不变量：消息只来自 selected chain，最新有效 compaction 的 summary 只出现一次；JsonElement 均为独立克隆。</remarks>
    /// <exception cref="ArgumentOutOfRangeException">afterSeq 不是非负安全整数。</exception>
    /// <exception cref="JournalIncompatibleException">当前 journal 含尚不能安全解释的分支或压缩。</exception>
    public async Task<PortableSessionSnapshot> GetSnapshotAsync(
        long afterSeq = 0,
        CancellationToken cancellationToken = default)
    {
        if (afterSeq is < 0 or > MaxSafeInteger)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSeq));
        }

        ObjectDisposedException.ThrowIf(disposed, this);
        await appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writerLease.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
            var projection = ProjectSelectedMessages();
            return new PortableSessionSnapshot(
                SessionId,
                eventSequence,
                activeRunId,
                lastRecordId,
                projection.CompactionRecordId,
                projection.Messages,
                events
                    .Where(item => item.Seq > afterSeq)
                    .Select(static item => item.Event.Clone())
                    .ToArray());
        }
        finally
        {
            appendGate.Release();
        }
    }

    /// <summary>
    /// 功能：返回发送给下一 Provider 请求的 durable user、assistant 与 tool 消息上下文。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="cancellationToken">等待一致性投影锁的取消信号。</param>
    /// <returns>按 selected parent chain 和最新 compaction 规则排列的独立 JsonElement 数组。</returns>
    /// <exception cref="JournalIncompatibleException">selected chain 的 compaction 引用或边界无法安全解释。</exception>
    internal async Task<IReadOnlyList<JsonElement>> GetContextMessagesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writerLease.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
            return ProjectSelectedMessages().Messages;
        }
        finally
        {
            appendGate.Release();
        }
    }

    /// <summary>
    /// 功能：在当前 selected parent chain 查找唯一 earlier artifact.created，并安全复核图像文件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="reference">portable image_ref 携带的核心 artifact 元数据。</param>
    /// <param name="maximumBytes">当前 Provider 输入允许的单 artifact 上限。</param>
    /// <param name="cancellationToken">等待 journal gate 与读取文件的取消信号。</param>
    /// <returns>通过 no-follow、regular-file、length、SHA-256、MIME 与魔数检查的字节。</returns>
    /// <remarks>不变量：只认可 selected chain，sibling/unrecorded artifact 即使文件存在也不可读取。</remarks>
    /// <exception cref="ArtifactValidationException">记录缺失、重复、元数据不匹配或文件验证失败。</exception>
    internal async Task<byte[]> ReadImageArtifactAsync(
        ArtifactReference reference,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                ImageArtifactValidation.ValidateReference(reference, maximumBytes);
                if (lastRecordId is null)
                {
                    throw new ArtifactValidationException();
                }

                ArtifactReference? recorded = null;
                foreach (var record in BuildParentChain(lastRecordId))
                {
                    if (record.Kind != "artifact.created" ||
                        !record.Data.TryGetProperty("artifact", out var artifact) ||
                        artifact.ValueKind != JsonValueKind.Object ||
                        !artifact.TryGetProperty("artifactId", out var idElement) ||
                        idElement.ValueKind != JsonValueKind.String ||
                        !string.Equals(idElement.GetString(), reference.ArtifactId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (recorded is not null ||
                        !artifact.TryGetProperty("mediaType", out var mediaElement) ||
                        mediaElement.ValueKind != JsonValueKind.String ||
                        !artifact.TryGetProperty("byteLength", out var lengthElement) ||
                        !lengthElement.TryGetInt64(out var byteLength) ||
                        !artifact.TryGetProperty("sha256", out var hashElement) ||
                        hashElement.ValueKind != JsonValueKind.String)
                    {
                        throw new ArtifactValidationException();
                    }

                    recorded = new ArtifactReference(
                        idElement.GetString()!,
                        mediaElement.GetString()!,
                        byteLength,
                        hashElement.GetString()!);
                }

                if (recorded is null || !ArtifactCoreEquals(recorded, reference))
                {
                    throw new ArtifactValidationException();
                }

                return await SessionArtifactStore.ReadImageAsync(
                    DirectoryPath,
                    reference,
                    maximumBytes,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ArtifactValidationException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException or JournalCorruptException or JournalIncompatibleException)
            {
                throw new ArtifactValidationException();
            }
        }
        finally
        {
            appendGate.Release();
        }
    }

    /// <summary>
    /// 功能：按 opaque ID 从当前 selected parent chain 解析唯一 durable 图片引用并完整复核文件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="artifactId">不得形成路径的 artifact ID。</param>
    /// <param name="maximumBytes">本次读取允许的单 artifact 最大字节数。</param>
    /// <param name="cancellationToken">等待 journal gate 与读取文件的取消信号。</param>
    /// <returns>journal 中完整 portable 引用和一次性通过 no-follow、长度、hash、MIME、魔数验证的字节。</returns>
    /// <remarks>不变量：只认可当前 selected chain；同 ID 重复、sibling、未发布文件或可选字段异常均拒绝。</remarks>
    /// <exception cref="ArtifactValidationException">ID、记录归属、引用形状或文件身份与内容无效。</exception>
    internal async Task<(ArtifactReference Artifact, byte[] Bytes)> ReadImageArtifactByIdAsync(
        string artifactId,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                if (string.IsNullOrEmpty(artifactId) || !IsValidOpaqueId(artifactId) || lastRecordId is null)
                {
                    throw new ArtifactValidationException();
                }

                ArtifactReference? recorded = null;
                foreach (var record in BuildParentChain(lastRecordId))
                {
                    if (record.Kind != "artifact.created" ||
                        !record.Data.TryGetProperty("artifact", out var artifact) ||
                        artifact.ValueKind != JsonValueKind.Object ||
                        !artifact.TryGetProperty("artifactId", out var idElement) ||
                        idElement.ValueKind != JsonValueKind.String ||
                        !string.Equals(idElement.GetString(), artifactId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (recorded is not null)
                    {
                        throw new ArtifactValidationException();
                    }

                    recorded = artifact.Deserialize<ArtifactReference>(JsonDefaults.Options)
                        ?? throw new ArtifactValidationException();
                }

                if (recorded is null)
                {
                    throw new ArtifactValidationException();
                }

                ImageArtifactValidation.ValidateReference(recorded, maximumBytes);
                var bytes = await SessionArtifactStore.ReadImageAsync(
                    DirectoryPath,
                    recorded,
                    maximumBytes,
                    cancellationToken).ConfigureAwait(false);
                return (recorded, bytes);
            }
            catch (ArtifactValidationException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException or JsonException or JournalCorruptException or
                JournalIncompatibleException)
            {
                throw new ArtifactValidationException();
            }
        }
        finally
        {
            appendGate.Release();
        }
    }

    /// <summary>
    /// 功能：在接受新 run 前完整构造 selected branch/compaction 投影，确认 continuation 可安全执行。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="cancellationToken">等待一致性投影锁的取消信号。</param>
    /// <returns>安全检查完成的 Task。</returns>
    /// <remarks>不变量：outcomeKnown=false 作为 durable tool message 进入历史，该检查从不执行、重试或重放工具。</remarks>
    /// <exception cref="JournalIncompatibleException">分支或压缩使自动 continuation 不安全。</exception>
    internal async Task EnsureContinuationSupportedAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writerLease.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
            _ = ProjectSelectedMessages();
        }
        finally
        {
            appendGate.Release();
        }
    }

    /// <summary>
    /// 功能：释放 journal 与独占 lock 句柄。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步释放操作。</returns>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await appendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Exception? firstException = null;
            try
            {
                await journalStream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                firstException = exception;
            }

            try
            {
                await writerLease.BeginReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                firstException ??= exception;
            }

            try
            {
                await lockStream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                firstException ??= exception;
            }

            try
            {
                await writerLease.FinishReleaseAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                firstException ??= exception;
            }

            try
            {
                await coordinationLease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                firstException ??= exception;
            }

            if (firstException is not null)
            {
                ExceptionDispatchInfo.Capture(firstException).Throw();
            }
        }
        finally
        {
            appendGate.Release();
            appendGate.Dispose();
        }
    }

    /// <summary>
    /// 功能：在新 journal 的第一行写入 schema header 并请求 OS 级落盘。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="workspace">绝对规范工作区。</param>
    /// <param name="cancellationToken">写入取消信号。</param>
    /// <returns>header durable 后完成的 Task。</returns>
    private async Task WriteHeaderAsync(string workspace, CancellationToken cancellationToken)
    {
        var header = new
        {
            Kind = "session",
            SchemaVersion = "0.1",
            SessionId,
            CreatedAt = DateTimeOffset.UtcNow,
            Workspace = workspace,
            CreatedBy = new { Name = "qxnm-forge-dotnet", Version = "0.1.0", Language = "dotnet" },
        };
        await WriteLineAndFlushAsync(header, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：严格扫描既有 journal 的 LF 完整前缀，接受任意 JSON 空白/属性顺序及五语言 header，并建立无损投影。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="committedLength">已确认以 LF 结束且不超过当前文件长度的 committed 前缀字节数。</param>
    /// <param name="cancellationToken">读取取消信号。</param>
    /// <returns>结构与状态恢复完成的 Task。</returns>
    /// <remarks>输入只来自同一已锁定 journal；输出投影只包含完整 LF 行。不变量：严格 UTF-8、递归 duplicate key 与状态验证先于任何恢复修改。</remarks>
    /// <exception cref="JournalCorruptException">前缀为空、未以 LF 结束、含空行、无效 UTF-8/JSON/schema/state、重复键或不连续序号。</exception>
    /// <exception cref="IOException">已锁定文件在扫描期间缩短或读取失败。</exception>
    private async Task LoadStateAsync(long committedLength, CancellationToken cancellationToken)
    {
        await writerLease.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
        if (committedLength <= 0 || committedLength > journalStream.Length)
        {
            throw new JournalCorruptException("journal committed prefix is invalid");
        }

        journalStream.Position = 0;
        var readBuffer = new byte[16_384];
        var lineBuffer = new ArrayBufferWriter<byte>();
        var hasHeader = false;
        try
        {
            var remaining = committedLength;
            while (remaining > 0)
            {
                var requested = (int)Math.Min(readBuffer.Length, remaining);
                var read = await journalStream.ReadAsync(
                    readBuffer.AsMemory(0, requested),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("journal changed during locked scan");
                }

                remaining -= read;
                var unprocessed = readBuffer.AsSpan(0, read);
                while (!unprocessed.IsEmpty)
                {
                    var lineFeed = unprocessed.IndexOf((byte)'\n');
                    if (lineFeed < 0)
                    {
                        lineBuffer.Write(unprocessed);
                        break;
                    }

                    lineBuffer.Write(unprocessed[..lineFeed]);
                    if (lineBuffer.WrittenCount == 0)
                    {
                        throw new JournalCorruptException("journal contains a blank record");
                    }

                    _ = StrictUtf8.GetCharCount(lineBuffer.WrittenSpan);
                    using var document = JsonDocument.Parse(lineBuffer.WrittenMemory);
                    if (!hasHeader)
                    {
                        ValidateHeader(document.RootElement);
                        hasHeader = true;
                    }
                    else
                    {
                        LoadRecordState(document.RootElement);
                    }

                    lineBuffer.Clear();
                    unprocessed = unprocessed[(lineFeed + 1)..];
                }
            }

            if (!hasHeader)
            {
                throw new JournalCorruptException("journal header is missing");
            }

            if (lineBuffer.WrittenCount != 0)
            {
                throw new JournalCorruptException("journal committed prefix does not end with LF");
            }
        }
        catch (JournalCorruptException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or DecoderFallbackException or InvalidOperationException or FormatException)
        {
            throw new JournalCorruptException("journal contains an invalid record", exception);
        }
        finally
        {
            journalStream.Position = journalStream.Length;
        }
    }

    /// <summary>
    /// 功能：在已持 writer lease 与 advisory lock 的 journal 中定位最后一个 LF 后的 committed 边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="originalLength">打开锁后观察到的非负文件长度。</param>
    /// <param name="cancellationToken">只读扫描取消信号。</param>
    /// <returns>若末字节为 LF 则返回原长度，否则返回最后 LF 的下一字节位置；无 LF 时返回零。</returns>
    /// <remarks>输入是当前锁定句柄长度；输出不解释尾部 JSON。不变量：方法只读，不会创建备份、截断或接受无 LF 记录。</remarks>
    /// <exception cref="JournalCorruptException">长度为负、超出当前句柄或扫描期间文件发生缩短。</exception>
    /// <exception cref="IOException">底层定位或读取失败。</exception>
    private async Task<long> FindCommittedLengthAsync(
        long originalLength,
        CancellationToken cancellationToken)
    {
        if (originalLength < 0 || originalLength > journalStream.Length)
        {
            throw new JournalCorruptException("journal length is invalid");
        }

        if (originalLength == 0)
        {
            return 0;
        }

        var buffer = new byte[16_384];
        var offset = originalLength;
        while (offset > 0)
        {
            var count = (int)Math.Min(buffer.Length, offset);
            offset -= count;
            journalStream.Position = offset;
            try
            {
                await journalStream.ReadExactlyAsync(
                    buffer.AsMemory(0, count),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException exception)
            {
                throw new JournalCorruptException("journal changed during locked tail scan", exception);
            }

            var lineFeed = buffer.AsSpan(0, count).LastIndexOf((byte)'\n');
            if (lineFeed >= 0)
            {
                return offset + lineFeed + 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// 功能：在完整前缀验证通过后备份、截断无 LF 未提交尾部，并追加 brand-neutral recovery extension。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="workspace">仅在整个 header 都未提交时用于创建新 header 的绝对规范工作区。</param>
    /// <param name="originalLength">恢复前 journal 的完整字节长度。</param>
    /// <param name="committedLength">最后 LF 后的位置，严格小于原长度。</param>
    /// <param name="cancellationToken">备份完成前可取消的信号。</param>
    /// <returns>备份、截断和 recovery extension 全部 durable 后完成的 Task。</returns>
    /// <remarks>输入已由严格前缀扫描验证；输出保留原文件逐字节备份。不变量：备份 durable 前 journal 零修改，备份成立后忽略普通取消并完成截断与诊断。</remarks>
    /// <exception cref="JournalCorruptException">边界、现有备份或锁内文件身份/内容不一致。</exception>
    /// <exception cref="IOException">备份、截断、flush 或诊断追加失败。</exception>
    private async Task RecoverUncommittedTailAsync(
        string workspace,
        long originalLength,
        long committedLength,
        CancellationToken cancellationToken)
    {
        if (originalLength <= 0 ||
            committedLength < 0 ||
            committedLength >= originalLength ||
            originalLength - committedLength > MaxSafeInteger)
        {
            throw new JournalCorruptException("journal recovery boundary is invalid");
        }

        await writerLease.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
        var originalSha256 = await ComputeJournalSha256Async(
            originalLength,
            cancellationToken).ConfigureAwait(false);
        var backupFile = RecoveryBackupPrefix + originalSha256 + RecoveryBackupSuffix;
        var backupPath = Path.Combine(DirectoryPath, backupFile);
        await EnsureRecoveryBackupAsync(
            backupPath,
            originalLength,
            originalSha256,
            cancellationToken).ConfigureAwait(false);
        FlushRecoveryDirectoryBestEffort(DirectoryPath);

        await writerLease.EnsureOwnershipAsync(CancellationToken.None).ConfigureAwait(false);
        if (journalStream.Length != originalLength)
        {
            throw new JournalCorruptException("journal changed during locked tail recovery");
        }

        journalStream.SetLength(committedLength);
        journalStream.Position = committedLength;
        await journalStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        journalStream.Flush(flushToDisk: true);
        if (committedLength == 0)
        {
            await WriteHeaderAsync(workspace, CancellationToken.None).ConfigureAwait(false);
        }

        await AppendLockedAsync(
            "extension",
            new
            {
                Namespace = RecoveryNamespace,
                Value = new
                {
                    Action = "truncate_uncommitted_tail",
                    DiscardedBytes = originalLength - committedLength,
                    BackupFile = backupFile,
                    OriginalSha256 = originalSha256,
                },
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：计算锁内 journal 原始全部字节的完整小写 SHA-256，不接受长度漂移。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="expectedLength">调用方在持锁后观察到的原始长度。</param>
    /// <param name="cancellationToken">只读摘要取消信号。</param>
    /// <returns>64 个小写十六进制字符组成的 SHA-256。</returns>
    /// <remarks>输入来自同一 journal 句柄；输出绑定完整原文件。不变量：恰好哈希 expectedLength 字节且不泄露内容。</remarks>
    /// <exception cref="JournalCorruptException">文件在摘要期间发生长度漂移或提前结束。</exception>
    /// <exception cref="IOException">底层读取失败。</exception>
    private async Task<string> ComputeJournalSha256Async(
        long expectedLength,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[16_384];
        journalStream.Position = 0;
        var remaining = expectedLength;
        while (remaining > 0)
        {
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = await journalStream.ReadAsync(
                buffer.AsMemory(0, requested),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new JournalCorruptException("journal changed during locked digest scan");
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        if (journalStream.Length != expectedLength)
        {
            throw new JournalCorruptException("journal changed during locked digest scan");
        }

        journalStream.Position = journalStream.Length;
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// 功能：创建 durable recovery 备份，或仅在已有固定 basename 文件与原 journal 逐字节相同时复用。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="backupPath">由固定前后缀与完整 SHA-256 派生、位于 Session 目录内的安全路径。</param>
    /// <param name="expectedLength">必须完整保存的原 journal 长度。</param>
    /// <param name="expectedSha256">原 journal 的完整小写 SHA-256。</param>
    /// <param name="cancellationToken">创建或验证备份期间取消信号。</param>
    /// <returns>备份已逐字节验证且新文件已 Flush(true) 后完成的 Task。</returns>
    /// <remarks>不变量：从不覆盖已有 basename，不跟随 reparse point；碰撞只在内容完全相同时复用，否则 fail closed。</remarks>
    /// <exception cref="JournalCorruptException">已有目标不是普通文件、长度或任意字节不同，或源摘要漂移。</exception>
    /// <exception cref="IOException">创建、读取、写入或 durable flush 失败。</exception>
    private async Task EnsureRecoveryBackupAsync(
        string backupPath,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (TryGetExistingAttributes(backupPath, out _))
        {
            await ValidateExistingRecoveryBackupAsync(
                backupPath,
                expectedLength,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var created = false;
        var complete = false;
        try
        {
            await using var backup = new FileStream(
                backupPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 16_384,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            created = true;
            journalStream.Position = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[16_384];
            var remaining = expectedLength;
            while (remaining > 0)
            {
                var requested = (int)Math.Min(buffer.Length, remaining);
                var read = await journalStream.ReadAsync(
                    buffer.AsMemory(0, requested),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new JournalCorruptException("journal changed during locked backup copy");
                }

                hash.AppendData(buffer, 0, read);
                await backup.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }

            var copiedSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (journalStream.Length != expectedLength ||
                !string.Equals(copiedSha256, expectedSha256, StringComparison.Ordinal))
            {
                throw new JournalCorruptException("journal changed during locked backup copy");
            }

            await backup.FlushAsync(cancellationToken).ConfigureAwait(false);
            backup.Flush(flushToDisk: true);
            complete = true;
        }
        catch (IOException) when (!created && TryGetExistingAttributes(backupPath, out _))
        {
            await ValidateExistingRecoveryBackupAsync(
                backupPath,
                expectedLength,
                cancellationToken).ConfigureAwait(false);
            return;
        }
        finally
        {
            journalStream.Position = journalStream.Length;
            if (created && !complete)
            {
                try
                {
                    File.Delete(backupPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

    }

    /// <summary>
    /// 功能：用独立只读句柄核对固定 recovery 备份与当前锁内 journal 的每一个原始字节。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="backupPath">预期固定 basename 的 Session 本地路径。</param>
    /// <param name="expectedLength">原 journal 长度。</param>
    /// <param name="cancellationToken">比较取消信号。</param>
    /// <returns>文件类型、长度与全部字节一致后完成的 Task。</returns>
    /// <remarks>不变量：拒绝目录、设备、reparse point 和任意差异；不从备份内容恢复协议状态。</remarks>
    /// <exception cref="JournalCorruptException">目标不安全、无法读取或与原 journal 不同。</exception>
    private async Task ValidateExistingRecoveryBackupAsync(
        string backupPath,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryGetExistingAttributes(backupPath, out var pathAttributes) ||
                (pathAttributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            {
                throw new JournalCorruptException("journal recovery backup is not a regular file");
            }

            await using var backup = new FileStream(
                backupPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16_384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var handleAttributes = File.GetAttributes(backup.SafeFileHandle);
            if ((handleAttributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0 ||
                backup.Length != expectedLength ||
                journalStream.Length != expectedLength)
            {
                throw new JournalCorruptException("journal recovery backup does not match");
            }

            journalStream.Position = 0;
            var journalBuffer = new byte[16_384];
            var backupBuffer = new byte[16_384];
            var remaining = expectedLength;
            while (remaining > 0)
            {
                var requested = (int)Math.Min(journalBuffer.Length, remaining);
                await journalStream.ReadExactlyAsync(
                    journalBuffer.AsMemory(0, requested),
                    cancellationToken).ConfigureAwait(false);
                await backup.ReadExactlyAsync(
                    backupBuffer.AsMemory(0, requested),
                    cancellationToken).ConfigureAwait(false);
                if (!journalBuffer.AsSpan(0, requested).SequenceEqual(backupBuffer.AsSpan(0, requested)))
                {
                    throw new JournalCorruptException("journal recovery backup does not match");
                }

                remaining -= requested;
            }
        }
        catch (JournalCorruptException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new JournalCorruptException("journal recovery backup cannot be verified", exception);
        }
        finally
        {
            journalStream.Position = journalStream.Length;
        }
    }

    /// <summary>
    /// 功能：在 Unix 尽力 fsync Session 目录，使新 recovery backup 的目录项在崩溃后可发现。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="directory">已由 writer lease 占有且不是 wire 路径的 Session 目录。</param>
    /// <remarks>输出为空；不变量：只以只读 no-follow 方式打开目录。平台不支持、open 或 fsync 失败均按“尽力”语义忽略，不弱化备份文件自身 Flush(true)。</remarks>
    private static void FlushRecoveryDirectoryBestEffort(string directory)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var flags = UnixOpenReadOnly |
            (OperatingSystem.IsLinux()
                ? LinuxOpenCloseOnExec | LinuxOpenNoFollow
                : MacOpenCloseOnExec | MacOpenNoFollow);
        var descriptor = OpenUnixRecoveryDirectory(directory, flags);
        if (descriptor < 0)
        {
            return;
        }

        using var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        _ = FsyncRecoveryDirectory(descriptor);
    }

    /// <summary>
    /// 功能：调用 libc open，以只读 no-follow 标志取得待尽力同步的 Session 目录描述符。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">由已占有 Session 目录派生的本地路径。</param>
    /// <param name="flags">当前 Unix 平台的只读、close-on-exec 与 no-follow 标志。</param>
    /// <returns>成功为非负文件描述符，失败为 -1 且由调用方按尽力语义处理。</returns>
    /// <remarks>不变量：路径从不来自 wire，且调用方不会把失败解释为 backup 文件已 durable 的反证。</remarks>
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenUnixRecoveryDirectory(string path, int flags);

    /// <summary>
    /// 功能：调用 libc fsync 尽力持久化 recovery backup 所在 Session 目录项。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="descriptor">由 OpenUnixRecoveryDirectory 打开的目录描述符。</param>
    /// <returns>成功为零，失败为 -1 且由调用方按尽力语义处理。</returns>
    /// <remarks>不变量：只对本方法打开且仍存活的目录描述符调用，不影响 backup 文件自身的强制 flush。</remarks>
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int FsyncRecoveryDirectory(int descriptor);

    /// <summary>
    /// 功能：验证跨语言 Session header，同时允许规范 extensions 中的未知命名空间和值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="header">已解析 header JSON。</param>
    /// <exception cref="JournalCorruptException">header 版本、会话、实现语言或基本形状无效。</exception>
    private void ValidateHeader(JsonElement header)
    {
        ValidateNoDuplicateKeys(header);
        EnsureObject(header, "journal header");
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
        if (RequireString(header, "kind") != "session" ||
            RequireString(header, "schemaVersion") != "0.1" ||
            RequireString(header, "sessionId") != SessionId ||
            string.IsNullOrEmpty(RequireString(header, "workspace")))
        {
            throw new JournalCorruptException("journal header is incompatible");
        }

        _ = RequireUtcTime(header, "createdAt");
        if (header.TryGetProperty("createdBy", out var createdBy))
        {
            EnsureObject(createdBy, "createdBy");
            var language = RequireString(createdBy, "language");
            if (!ImplementationLanguages.Contains(language))
            {
                throw new JournalCorruptException("journal header implementation language is invalid");
            }
        }

        EnsureOptionalObject(header, "extensions");
    }

    /// <summary>
    /// 功能：验证并吸收一个既有 journal 记录的结构、树关系和可恢复投影状态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="record">已解析 JSON 记录。</param>
    /// <exception cref="JournalCorruptException">记录版本、ID、parent、data 或状态转换无效。</exception>
    private void LoadRecordState(JsonElement record)
    {
        ValidateNoDuplicateKeys(record);
        EnsureObject(record, "journal record");
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
        var sequence = RequireSafeInteger(record, "seq", minimum: 1);
        var recordId = RequireString(record, "recordId");
        var kind = RequireString(record, "kind");
        if (RequireString(record, "schemaVersion") != "0.1" ||
            RequireString(record, "sessionId") != SessionId ||
            sequence != journalSequence + 1 ||
            !IsValidOpaqueId(recordId) ||
            recordIds.Contains(recordId) ||
            !CoreRecordKinds.Contains(kind))
        {
            throw new JournalCorruptException("journal record identity or sequence is invalid");
        }

        string? parentId = null;
        var parent = RequireProperty(record, "parentId");
        if (parent.ValueKind == JsonValueKind.String)
        {
            parentId = parent.GetString();
            if (string.IsNullOrEmpty(parentId) || !recordIds.Contains(parentId))
            {
                throw new JournalCorruptException("journal parentId must reference an earlier record");
            }
        }
        else if (parent.ValueKind != JsonValueKind.Null)
        {
            throw new JournalCorruptException("journal parentId is invalid");
        }

        _ = RequireUtcTime(record, "time");
        var data = RequireProperty(record, "data");
        EnsureObject(data, "journal record data");
        EnsureOptionalObject(record, "extensions");
        if (kind == "branch.selected")
        {
            var targetLeafRecordId = RequireString(data, "leafRecordId");
            if (parentId is null ||
                !string.Equals(parentId, targetLeafRecordId, StringComparison.Ordinal))
            {
                throw new JournalCorruptException("branch selection target must equal its parentId");
            }

            if (!IsParentChainQuiescent(targetLeafRecordId))
            {
                throw new JournalCorruptException("branch selection target is not quiescent");
            }
        }
        else if (!string.Equals(parentId, lastRecordId, StringComparison.Ordinal))
        {
            throw new JournalCorruptException("ordinary journal record must extend the selected head");
        }

        recordIds.Add(recordId);
        var treeRecord = new JournalTreeRecord(recordId, sequence, parentId, kind, data.Clone());
        treeRecords.Add(recordId, treeRecord);
        ApplyRecordProjection(kind, data, sequence);
        if (kind == "context.compacted")
        {
            _ = ValidateCompactionRecord(treeRecord);
        }

        journalSequence = sequence;
        lastRecordId = recordId;
    }

    /// <summary>
    /// 功能：把一条已验证记录应用到消息、事件、run 和工具恢复投影。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">已验证核心 kind。</param>
    /// <param name="data">该 kind 的对象 data。</param>
    /// <param name="sequence">原始 journal 记录序号，恢复时保持工具顺序。</param>
    /// <exception cref="JournalCorruptException">关键投影字段或状态转换无效。</exception>
    private void ApplyRecordProjection(string kind, JsonElement data, long sequence)
    {
        switch (kind)
        {
            case "message.appended":
                var message = RequireProperty(data, "message");
                ValidatePortableMessage(message);
                var messageId = RequireString(message, "messageId");
                if (!messageIds.Add(messageId))
                {
                    throw new JournalCorruptException("journal contains a duplicate messageId");
                }

                if (RequireString(message, "role") == "tool")
                {
                    var messageToolCallId = RequireString(message, "toolCallId");
                    if (!toolMessageCallIds.Add(messageToolCallId))
                    {
                        throw new JournalCorruptException("duplicate tool result message");
                    }
                }

                break;
            case "event.emitted":
                var portableEvent = RequireProperty(data, "event");
                EnsureObject(portableEvent, "event envelope");
                if (RequireString(portableEvent, "sessionId") != SessionId)
                {
                    throw new JournalCorruptException("event sessionId does not match journal");
                }

                var persistedEventSequence = RequireSafeInteger(portableEvent, "seq", minimum: 1);
                if (persistedEventSequence <= eventSequence)
                {
                    throw new JournalCorruptException("journal event sequence regressed");
                }

                eventSequence = persistedEventSequence;
                events.Add(new PersistedEvent(persistedEventSequence, portableEvent.Clone()));
                var eventType = RequireString(portableEvent, "type");
                if (eventType == "tool.started")
                {
                    var eventData = RequireProperty(portableEvent, "data");
                    EnsureObject(eventData, "tool.started event data");
                    startedToolCallIds.Add(RequireString(eventData, "toolCallId"));
                }

                if (eventType is "run.completed" or "run.failed" or "run.cancelled" or "run.interrupted")
                {
                    terminalEventRunIds.Add(RequireString(portableEvent, "runId"));
                }

                break;
            case "run.accepted":
                var acceptedRunId = RequireString(data, "runId");
                if (!IsValidOpaqueId(acceptedRunId) || activeRunId is not null || terminalRunIds.Contains(acceptedRunId))
                {
                    throw new JournalCorruptException("run.accepted state is invalid");
                }

                activeRunId = acceptedRunId;
                break;
            case "run.terminal":
                var terminalRunId = RequireString(data, "runId");
                if (!IsValidOpaqueId(terminalRunId) ||
                    activeRunId != terminalRunId ||
                    !terminalRunIds.Add(terminalRunId))
                {
                    throw new JournalCorruptException("run.terminal state is invalid");
                }

                activeRunId = null;
                if (RequireString(data, "status") == "interrupted" &&
                    data.TryGetProperty("error", out var interruptedError) &&
                    interruptedError.ValueKind == JsonValueKind.Object &&
                    interruptedError.TryGetProperty("details", out var errorDetails) &&
                    errorDetails.ValueKind == JsonValueKind.Object &&
                    errorDetails.TryGetProperty("kind", out var errorKind) &&
                    errorKind.GetString() == "recovered_interrupted_run")
                {
                    recoveredTerminals[terminalRunId] = new RecoveredTerminalState(
                        sequence,
                        terminalRunId,
                        interruptedError.Clone());
                }

                break;
            case "tool.intent":
                var toolCallId = RequireString(data, "toolCallId");
                if (!IsValidOpaqueId(toolCallId) ||
                    !data.TryGetProperty("idempotent", out var idempotentElement) ||
                    idempotentElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !toolIntents.TryAdd(
                        toolCallId,
                        new ToolIntentState(
                            sequence,
                            RequireString(data, "runId"),
                            RequireString(data, "turnId"),
                            toolCallId,
                            RequireString(data, "name"),
                            RequireString(data, "status"),
                            idempotentElement.GetBoolean())))
                {
                    throw new JournalCorruptException("tool.intent state is invalid");
                }

                break;
            case "approval.requested":
                var approval = RequireProperty(data, "approval");
                EnsureObject(approval, "approval.requested.approval");
                var approvalId = RequireString(approval, "approvalId");
                var approvalToolCallId = RequireString(approval, "toolCallId");
                if (!IsValidOpaqueId(approvalId) ||
                    approvalRequests.Values.Any(
                        existing => existing.ToolCallId == approvalToolCallId) ||
                    !approvalRequests.TryAdd(
                        approvalId,
                        new RecoveryApprovalState(
                            sequence,
                            RequireString(data, "runId"),
                            approvalId,
                            approvalToolCallId)))
                {
                    throw new JournalCorruptException("approval.requested state is invalid");
                }

                break;
            case "approval.resolved":
                var resolvedApprovalId = RequireString(data, "approvalId");
                var resolutionDecision = RequireProperty(data, "decision");
                EnsureObject(resolutionDecision, "approval.resolved.decision");
                var resolutionChoice = RequireString(resolutionDecision, "choice");
                if (!approvalRequests.ContainsKey(resolvedApprovalId) ||
                    !approvalResolutionChoices.TryAdd(resolvedApprovalId, resolutionChoice))
                {
                    throw new JournalCorruptException("approval.resolved state is invalid");
                }

                break;
            case "tool.result":
                var resultToolCallId = RequireString(data, "toolCallId");
                if (!data.TryGetProperty("outcomeKnown", out var outcomeKnownElement) ||
                    outcomeKnownElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !toolResults.TryAdd(resultToolCallId, outcomeKnownElement.GetBoolean()))
                {
                    throw new JournalCorruptException("tool.result state is invalid");
                }
                break;
        }
    }

    /// <summary>
    /// 功能：先拒绝崩溃遗留审批，再按是否曾 tool.started 补 known/unknown 工具结果和 interrupted terminal。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="cancellationToken">恢复记录持久化取消信号。</param>
    /// <returns>全部恢复记录 durable 后完成的 Task。</returns>
    /// <remarks>不变量：方法不调用 Provider、不执行工具；未 durable tool.started 的调用一律 outcomeKnown=true 且不重放。</remarks>
    private async Task RecoverInterruptedStateAsync(CancellationToken cancellationToken)
    {
        foreach (var approval in approvalRequests.Values
                     .Where(approval => !approvalResolutionChoices.ContainsKey(approval.ApprovalId))
                     .OrderBy(static approval => approval.Sequence))
        {
            await AppendLockedAsync(
                "approval.resolved",
                new
                {
                    approval.RunId,
                    approval.ApprovalId,
                    Decision = new { Choice = "deny", Reason = "client disconnected" },
                    ResolutionSource = "disconnect",
                },
                cancellationToken).ConfigureAwait(false);
        }

        var unresolvedTools = toolIntents.Values
            .Where(intent =>
                !toolResults.TryGetValue(intent.ToolCallId, out var outcomeKnown) ||
                (!outcomeKnown && !toolMessageCallIds.Contains(intent.ToolCallId)))
            .OrderBy(static intent => intent.Sequence)
            .ToArray();
        foreach (var intent in unresolvedTools)
        {
            var outcomeKnown = intent.Status != "started" && !startedToolCallIds.Contains(intent.ToolCallId);
            var approval = approvalRequests.Values.SingleOrDefault(
                approval => approval.ToolCallId == intent.ToolCallId);
            var resolvedChoice = approval is not null &&
                approvalResolutionChoices.TryGetValue(approval.ApprovalId, out var choice)
                    ? choice
                    : null;
            var terminationReason = intent.Status switch
            {
                "rejected" => "validation_error",
                "denied" => "denied",
                "awaiting_approval" when resolvedChoice == "deny" => "denied",
                _ => "internal_error",
            };
            var explanation = outcomeKnown
                ? "Tool did not start before the previous process exited; it was not replayed."
                : intent.Idempotent
                    ? "Tool outcome is unknown after crash; it was not replayed."
                    : "Tool outcome is unknown after crash; the non-idempotent operation was not replayed.";
            if (!toolResults.ContainsKey(intent.ToolCallId))
            {
                await AppendLockedAsync(
                    "tool.result",
                    new
                    {
                        intent.RunId,
                        intent.TurnId,
                        intent.ToolCallId,
                        Result = new
                        {
                            Content = new[]
                            {
                                new TextContent(explanation),
                            },
                            IsError = true,
                            TerminationReason = terminationReason,
                        },
                        OutcomeKnown = outcomeKnown,
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            if (!toolMessageCallIds.Contains(intent.ToolCallId))
            {
                await AppendLockedAsync(
                    "message.appended",
                    new
                    {
                        Message = new
                        {
                            MessageId = "message-" + Guid.NewGuid().ToString("N"),
                            Role = "tool",
                            intent.ToolCallId,
                            ToolName = intent.Name,
                            Content = new[] { new TextContent(explanation) },
                            IsError = true,
                            Time = DateTimeOffset.UtcNow,
                        },
                        intent.RunId,
                        intent.TurnId,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (activeRunId is not null)
        {
            var interruptedRunId = activeRunId;
            var error = new PortableError(
                -32005,
                "Run was interrupted by a previous process exit.",
                false,
                new ErrorDetails("recovered_interrupted_run"));
            await AppendLockedAsync(
                "run.terminal",
                new
                {
                    RunId = interruptedRunId,
                    Status = "interrupted",
                    Error = error,
                },
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var terminal in recoveredTerminals.Values
                     .Where(terminal => !terminalEventRunIds.Contains(terminal.RunId))
                     .OrderBy(static terminal => terminal.Sequence))
        {
            await AppendEventAsync(
                terminal.RunId,
                null,
                "run.interrupted",
                new { Status = "interrupted", Error = terminal.Error.Clone() },
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 功能：在 append gate 内构造下一记录、强制落盘，再推进全部内存投影。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">核心记录 kind。</param>
    /// <param name="data">kind 对应数据。</param>
    /// <param name="cancellationToken">写入取消信号。</param>
    /// <param name="parentIdOverride">仅 branch.selected 使用的 earlier target；其他记录必须为 null。</param>
    /// <returns>durable 记录回执。</returns>
    private async Task<JournalAppendReceipt> AppendLockedAsync(
        string kind,
        object data,
        CancellationToken cancellationToken,
        string? parentIdOverride = null)
    {
        if (!CoreRecordKinds.Contains(kind))
        {
            throw new ArgumentException("journal kind is unknown", nameof(kind));
        }

        var dataElement = JsonSerializer.SerializeToElement(data, JsonDefaults.Options);
        if (dataElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("journal data must serialize as an object", nameof(data));
        }

        var nextSequence = journalSequence + 1;
        var recordId = "record-" + Guid.NewGuid().ToString("N");
        var parentId = parentIdOverride ?? lastRecordId;
        if (kind == "branch.selected")
        {
            var targetLeafRecordId = RequireString(dataElement, "leafRecordId");
            if (parentIdOverride is null ||
                !treeRecords.ContainsKey(targetLeafRecordId) ||
                !string.Equals(parentId, targetLeafRecordId, StringComparison.Ordinal) ||
                !IsParentChainQuiescent(targetLeafRecordId))
            {
                throw new JournalCorruptException("branch selection append invariants are invalid");
            }
        }
        else if (parentIdOverride is not null ||
            !string.Equals(parentId, lastRecordId, StringComparison.Ordinal))
        {
            throw new JournalCorruptException("ordinary append must extend the selected head");
        }

        var treeRecord = new JournalTreeRecord(
            recordId,
            nextSequence,
            parentId,
            kind,
            dataElement.Clone());
        if (kind == "context.compacted")
        {
            _ = ValidateCompactionRecord(treeRecord);
        }

        var record = new JournalRecordLine(
            "0.1",
            kind,
            recordId,
            SessionId,
            nextSequence,
            parentId,
            DateTimeOffset.UtcNow,
            dataElement);
        await WriteLineAndFlushAsync(record, cancellationToken).ConfigureAwait(false);
        recordIds.Add(recordId);
        treeRecords.Add(recordId, treeRecord);
        ApplyRecordProjection(kind, dataElement, nextSequence);
        journalSequence = nextSequence;
        lastRecordId = recordId;
        return new JournalAppendReceipt(recordId, nextSequence);
    }

    /// <summary>
    /// 功能：在 append gate 内核对调用者 expected head 与当前 selected head 完全一致。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="expectedHeadRecordId">客户端先前观察到的 opaque record ID。</param>
    /// <remarks>不变量：比较只使用 ordinal opaque identity，不从 ID 推断时间或顺序。</remarks>
    /// <exception cref="SessionMutationException">head 已变化或 Session 仍为空。</exception>
    private void EnsureExpectedHead(string expectedHeadRecordId)
    {
        if (!string.Equals(lastRecordId, expectedHeadRecordId, StringComparison.Ordinal))
        {
            throw new SessionMutationException(-32010, true, "stale_session_head");
        }
    }

    /// <summary>
    /// 功能：沿 earlier-only parentId 重建从根到指定 record 的唯一 parent chain。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="headRecordId">已存在或待验证的 head record ID。</param>
    /// <returns>根到 head 的稳定有序 tree record 列表。</returns>
    /// <remarks>不变量：每个 record 最多访问一次；append seq 不用于替代 parent edge。</remarks>
    /// <exception cref="JournalIncompatibleException">目标、父记录缺失或 tree 出现循环。</exception>
    private List<JournalTreeRecord> BuildParentChain(string headRecordId)
    {
        var reversed = new List<JournalTreeRecord>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? currentId = headRecordId;
        while (currentId is not null)
        {
            if (!visited.Add(currentId) || !treeRecords.TryGetValue(currentId, out var current))
            {
                throw new JournalIncompatibleException("journal parent chain is invalid");
            }

            reversed.Add(current);
            currentId = current.ParentId;
        }

        reversed.Reverse();
        return reversed;
    }

    /// <summary>
    /// 功能：仅扫描目标 parent chain，确认没有未终止 run、未完成工具或未决审批。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="headRecordId">待选择或待压缩的 chain head。</param>
    /// <returns>三类生命周期均已闭合时为 true。</returns>
    /// <remarks>不变量：未选 sibling 的状态绝不影响结果，terminal/result/resolution 必须位于目标 ancestry。</remarks>
    /// <exception cref="JournalCorruptException">生命周期记录缺少规范 ID 字段。</exception>
    /// <exception cref="JournalIncompatibleException">parent chain 结构无效。</exception>
    private bool IsParentChainQuiescent(string headRecordId)
    {
        var acceptedRuns = new HashSet<string>(StringComparer.Ordinal);
        var terminalRuns = new HashSet<string>(StringComparer.Ordinal);
        var toolIntentsOnChain = new HashSet<string>(StringComparer.Ordinal);
        var toolResultsOnChain = new HashSet<string>(StringComparer.Ordinal);
        var requestedApprovals = new HashSet<string>(StringComparer.Ordinal);
        var resolvedApprovals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in BuildParentChain(headRecordId))
        {
            switch (record.Kind)
            {
                case "run.accepted":
                    acceptedRuns.Add(RequireString(record.Data, "runId"));
                    break;
                case "run.terminal":
                    terminalRuns.Add(RequireString(record.Data, "runId"));
                    break;
                case "tool.intent":
                    toolIntentsOnChain.Add(RequireString(record.Data, "toolCallId"));
                    break;
                case "tool.result":
                    toolResultsOnChain.Add(RequireString(record.Data, "toolCallId"));
                    break;
                case "approval.requested":
                    var approval = RequireProperty(record.Data, "approval");
                    EnsureObject(approval, "approval.requested.approval");
                    requestedApprovals.Add(RequireString(approval, "approvalId"));
                    break;
                case "approval.resolved":
                    resolvedApprovals.Add(RequireString(record.Data, "approvalId"));
                    break;
            }
        }

        return acceptedRuns.IsSubsetOf(terminalRuns) &&
            toolIntentsOnChain.IsSubsetOf(toolResultsOnChain) &&
            requestedApprovals.IsSubsetOf(resolvedApprovals);
    }

    /// <summary>
    /// 功能：按 selected parent chain 及其最新有效 compaction 构造精确 Provider/session 消息投影。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>独立消息克隆和当前 active compaction record ID。</returns>
    /// <remarks>不变量：summary 位于首位且只出现一次；retained source、post-compaction 消息保持 parent-chain 顺序；sibling 永不进入。</remarks>
    /// <exception cref="JournalIncompatibleException">selected chain 或 compaction 引用无法安全解释。</exception>
    private SelectedMessageProjection ProjectSelectedMessages()
    {
        if (lastRecordId is null)
        {
            return new SelectedMessageProjection([], null);
        }

        var selectedChain = BuildParentChain(lastRecordId);
        var compactionIndex = selectedChain.FindLastIndex(
            static record => record.Kind == "context.compacted");
        var projected = new List<JsonElement>();
        if (compactionIndex < 0)
        {
            foreach (var record in selectedChain.Where(static record => record.Kind == "message.appended"))
            {
                projected.Add(MessageFromRecord(record, "selected message").Clone());
            }

            return new SelectedMessageProjection(projected, null);
        }

        var compactionRecord = selectedChain[compactionIndex];
        var validated = ValidateCompactionRecord(compactionRecord);
        projected.Add(validated.SummaryMessage.Clone());
        foreach (var record in validated.RetainedRecords.Where(
                     static record => record.Kind == "message.appended"))
        {
            projected.Add(MessageFromRecord(record, "retained message").Clone());
        }

        foreach (var record in selectedChain
                     .Skip(compactionIndex + 1)
                     .Where(static record => record.Kind == "message.appended"))
        {
            projected.Add(MessageFromRecord(record, "post-compaction message").Clone());
        }

        return new SelectedMessageProjection(projected, compactionRecord.RecordId);
    }

    /// <summary>
    /// 功能：验证 context.compacted 的 summary 配对、source ancestry、retained user 边界及 token 不变量。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="compactionRecord">已解析但可尚未写入 tree 索引的 compaction record。</param>
    /// <returns>canonical summary message 与 retained boundary 到 source 的 record 列表。</returns>
    /// <remarks>不变量：summary 是 source 的直接 assistant text 后代；所有引用均指向 earlier record；tokensAfter 不增长。</remarks>
    /// <exception cref="JournalIncompatibleException">任一跨记录 compaction 语义不成立。</exception>
    /// <exception cref="JournalCorruptException">必需字段缺失或基本 JSON 类型无效。</exception>
    private ValidatedCompaction ValidateCompactionRecord(JournalTreeRecord compactionRecord)
    {
        if (compactionRecord.Kind != "context.compacted")
        {
            throw new JournalIncompatibleException("compaction validator received another record kind");
        }

        var sourceLeafRecordId = RequireString(compactionRecord.Data, "sourceLeafRecordId");
        var summaryMessageId = RequireString(compactionRecord.Data, "summaryMessageId");
        var firstRetainedRecordId = RequireString(compactionRecord.Data, "firstRetainedRecordId");
        var strategy = RequireString(compactionRecord.Data, "strategy");
        var tokensBefore = RequireSafeInteger(compactionRecord.Data, "tokensBefore", minimum: 0);
        if (strategy.Length > 128)
        {
            throw new JournalIncompatibleException("compaction strategy is invalid");
        }

        if (compactionRecord.Data.TryGetProperty("tokensAfter", out var tokensAfterElement) &&
            (!tokensAfterElement.TryGetInt64(out var tokensAfter) ||
             tokensAfter is < 0 or > MaxSafeInteger ||
             tokensAfter > tokensBefore))
        {
            throw new JournalIncompatibleException("compaction token estimates are invalid");
        }

        if (compactionRecord.ParentId is null ||
            !treeRecords.TryGetValue(compactionRecord.ParentId, out var summaryRecord) ||
            summaryRecord.Kind != "message.appended" ||
            !string.Equals(summaryRecord.ParentId, sourceLeafRecordId, StringComparison.Ordinal) ||
            summaryRecord.Sequence >= compactionRecord.Sequence)
        {
            throw new JournalIncompatibleException("compaction summary record is invalid");
        }

        var summaryMessage = MessageFromRecord(summaryRecord, "compaction summary");
        if (!string.Equals(RequireString(summaryMessage, "messageId"), summaryMessageId, StringComparison.Ordinal) ||
            RequireString(summaryMessage, "role") != "assistant" ||
            RequireString(summaryMessage, "finishReason") != "stop")
        {
            throw new JournalIncompatibleException("compaction summary identity or role is invalid");
        }

        var summaryContent = RequireProperty(summaryMessage, "content");
        if (summaryContent.ValueKind != JsonValueKind.Array || summaryContent.GetArrayLength() != 1)
        {
            throw new JournalIncompatibleException("compaction summary content is invalid");
        }

        var summaryText = summaryContent[0];
        EnsureObject(summaryText, "compaction summary text");
        var summaryTextValue = RequireProperty(summaryText, "text");
        if (RequireString(summaryText, "type") != "text" ||
            summaryTextValue.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(summaryTextValue.GetString()))
        {
            throw new JournalIncompatibleException("compaction summary must contain one nonempty text");
        }

        var provider = RequireProperty(summaryMessage, "provider");
        EnsureObject(provider, "compaction summary provider");
        _ = RequireString(provider, "id");
        _ = RequireString(provider, "modelId");
        var usage = RequireProperty(summaryMessage, "usage");
        EnsureObject(usage, "compaction summary usage");
        _ = RequireSafeInteger(usage, "inputTokens", minimum: 0);
        _ = RequireSafeInteger(usage, "outputTokens", minimum: 0);
        _ = RequireSafeInteger(usage, "totalTokens", minimum: 0);

        var sourceChain = BuildParentChain(sourceLeafRecordId);
        var retainedIndex = sourceChain.FindIndex(
            record => string.Equals(record.RecordId, firstRetainedRecordId, StringComparison.Ordinal));
        if (retainedIndex < 0 ||
            sourceChain[retainedIndex].Sequence >= compactionRecord.Sequence ||
            sourceChain[retainedIndex].Kind != "message.appended" ||
            RequireString(
                MessageFromRecord(sourceChain[retainedIndex], "first retained record"),
                "role") != "user")
        {
            throw new JournalIncompatibleException("compaction retained boundary is invalid");
        }

        return new ValidatedCompaction(
            summaryMessage.Clone(),
            sourceChain.Skip(retainedIndex).ToArray());
    }

    /// <summary>
    /// 功能：从 message.appended tree record 提取 canonical message 对象。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="record">目标 tree record。</param>
    /// <param name="context">不含实例数据的安全诊断标签。</param>
    /// <returns>由 record 自身持有生命周期的 message JsonElement。</returns>
    /// <exception cref="JournalIncompatibleException">目标 record kind 不是 message.appended。</exception>
    /// <exception cref="JournalCorruptException">message 字段缺失或不是对象。</exception>
    private static JsonElement MessageFromRecord(JournalTreeRecord record, string context)
    {
        if (record.Kind != "message.appended")
        {
            throw new JournalIncompatibleException(context + " must reference message.appended");
        }

        var message = RequireProperty(record.Data, "message");
        EnsureObject(message, context + ".message");
        return message;
    }

    /// <summary>
    /// 功能：验证并保留 user、assistant 或 tool portable message 的基础无损投影形状。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="message">message.appended.data.message。</param>
    /// <exception cref="JournalCorruptException">消息 ID、角色或 content 形状无效。</exception>
    private static void ValidatePortableMessage(JsonElement message)
    {
        EnsureObject(message, "portable message");
        var messageId = RequireString(message, "messageId");
        var role = RequireString(message, "role");
        var content = RequireProperty(message, "content");
        if (!IsValidOpaqueId(messageId) ||
            role is not ("user" or "assistant" or "tool") ||
            content.ValueKind != JsonValueKind.Array)
        {
            throw new JournalCorruptException("portable message shape is invalid");
        }
    }

    /// <summary>
    /// 功能：递归拒绝 JSON 对象重复键，避免跨实现 last-value-wins 差异。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">待检查 JSON 值。</param>
    /// <exception cref="JournalCorruptException">任意对象包含重复属性。</exception>
    private static void ValidateNoDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JournalCorruptException("journal contains a duplicate JSON property");
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
    /// 功能：确保对象只包含规范核心字段和显式 extensions 容器。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">待检查对象。</param>
    /// <param name="allowed">允许属性名。</param>
    /// <exception cref="JournalCorruptException">出现未命名扩展字段。</exception>
    private static void EnsureOnlyProperties(JsonElement element, params ReadOnlySpan<string> allowed)
    {
        foreach (var property in element.EnumerateObject())
        {
            var found = false;
            foreach (var name in allowed)
            {
                if (string.Equals(property.Name, name, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                throw new JournalCorruptException("journal object contains an unknown core field");
            }
        }
    }

    /// <summary>
    /// 功能：确保 JSON 元素为对象。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">待检查元素。</param>
    /// <param name="context">安全诊断上下文。</param>
    /// <exception cref="JournalCorruptException">元素不是对象。</exception>
    private static void EnsureObject(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JournalCorruptException(context + " must be an object");
        }
    }

    /// <summary>
    /// 功能：若可选字段存在则确保其值为对象，供 extensions 等开放值容器使用。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">可选字段名。</param>
    /// <exception cref="JournalCorruptException">存在字段但值不是对象。</exception>
    private static void EnsureOptionalObject(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var property))
        {
            EnsureObject(property, name);
        }
    }

    /// <summary>
    /// 功能：取得 journal 对象必需属性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">必需属性名。</param>
    /// <returns>属性 JsonElement。</returns>
    /// <exception cref="JournalCorruptException">属性缺失。</exception>
    private static JsonElement RequireProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            throw new JournalCorruptException("journal required field is missing: " + name);
        }

        return property;
    }

    /// <summary>
    /// 功能：读取 journal 对象必需非空字符串。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>非空字符串。</returns>
    /// <exception cref="JournalCorruptException">字段缺失、类型错误或为空。</exception>
    private static string RequireString(JsonElement element, string name)
    {
        var property = RequireProperty(element, name);
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(property.GetString()))
        {
            throw new JournalCorruptException("journal string field is invalid: " + name);
        }

        return property.GetString()!;
    }

    /// <summary>
    /// 功能：读取范围受限的 JSON 安全整数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <param name="minimum">允许最小值。</param>
    /// <returns>已验证 Int64。</returns>
    /// <exception cref="JournalCorruptException">字段不是范围内整数。</exception>
    private static long RequireSafeInteger(JsonElement element, string name, long minimum)
    {
        var property = RequireProperty(element, name);
        if (!property.TryGetInt64(out var value) || value < minimum || value > MaxSafeInteger)
        {
            throw new JournalCorruptException("journal integer field is invalid: " + name);
        }

        return value;
    }

    /// <summary>
    /// 功能：读取并验证 Z 后缀 UTC 时间。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">时间字段名。</param>
    /// <returns>零偏移 DateTimeOffset。</returns>
    /// <exception cref="JournalCorruptException">时间不是规范 UTC 字符串。</exception>
    private static DateTimeOffset RequireUtcTime(JsonElement element, string name)
    {
        var text = RequireString(element, name);
        if (!text.EndsWith('Z') ||
            !DateTimeOffset.TryParse(text, out var value) ||
            value.Offset != TimeSpan.Zero)
        {
            throw new JournalCorruptException("journal UTC time is invalid: " + name);
        }

        return value;
    }

    /// <summary>
    /// 功能：序列化完整单行、追加 LF、刷新运行时缓冲并调用 Flush(true)。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">完整 header 或记录对象。</param>
    /// <param name="cancellationToken">异步写取消信号。</param>
    /// <returns>OS durability primitive 返回后的 Task。</returns>
    private async Task WriteLineAndFlushAsync(object value, CancellationToken cancellationToken)
    {
        await writerLease.EnsureOwnershipAsync(cancellationToken).ConfigureAwait(false);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options);
        await journalStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await journalStream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await journalStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        journalStream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// 功能：打开持久 `lock` 文件并取得 .NET FileShare.None/Unix flock defence-in-depth advisory lock。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="directory">已由 portable writer lease 占有的真实 Session 目录。</param>
    /// <returns>在 journal 关闭前持续持有的独占 FileStream。</returns>
    /// <remarks>不变量：该锁只作 defence in depth，不能替代 writer.lock.d witness；稳定链接与目录目标均拒绝。</remarks>
    /// <exception cref="SessionWriterLeaseException">目标无效或已有 advisory owner。</exception>
    private static FileStream OpenAdvisoryLock(string directory)
    {
        var lockPath = Path.Combine(directory, "lock");
        try
        {
            if (TryGetExistingAttributes(lockPath, out var attributes) &&
                (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                throw SessionWriterLeaseException.Invalid();
            }

            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            try
            {
                attributes = File.GetAttributes(stream.SafeFileHandle);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
                    !TryGetExistingAttributes(lockPath, out var pathAttributes) ||
                    (pathAttributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                {
                    throw SessionWriterLeaseException.Invalid();
                }

                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (SessionWriterLeaseException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SessionWriterLeaseException.Locked(exception);
        }
    }

    /// <summary>
    /// 功能：读取 advisory lock 路径属性，并把不存在与其他 I/O 失败分离。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">规范 lock 文件路径。</param>
    /// <param name="attributes">存在时的属性。</param>
    /// <returns>路径存在返回 true。</returns>
    private static bool TryGetExistingAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    /// <summary>
    /// 功能：验证 sessionId 符合 opaque ID 且不能注入路径分隔符。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">待验证 wire ID。</param>
    /// <exception cref="ArgumentException">ID 为空、过长、首字符无效或含路径字符。</exception>
    private static void ValidateSessionId(string sessionId)
    {
        if (!IsValidOpaqueId(sessionId))
        {
            throw new ArgumentException("sessionId is not a valid opaque ID", nameof(sessionId));
        }
    }

    /// <summary>
    /// 功能：判断字符串是否符合公共 opaque ID 长度、首字符和字符集规则。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">待检查 ID。</param>
    /// <returns>有效返回 true。</returns>
    private static bool IsValidOpaqueId(string value)
    {
        return value.Length is >= 1 and <= 128 &&
            char.IsAsciiLetterOrDigit(value[0]) &&
            !value.AsSpan().ContainsAnyExcept(OpaqueIdCharacters);
    }

    /// <summary>
    /// 功能：比较 journal artifact.created 与 image_ref 的四个内容绑定核心字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="left">journal 记录引用。</param>
    /// <param name="right">消息携带引用。</param>
    /// <returns>ID、MIME、长度与 SHA-256 全部 ordinal 相等时为 true。</returns>
    private static bool ArtifactCoreEquals(ArtifactReference left, ArtifactReference right)
    {
        return string.Equals(left.ArtifactId, right.ArtifactId, StringComparison.Ordinal) &&
            string.Equals(left.MediaType, right.MediaType, StringComparison.Ordinal) &&
            left.ByteLength == right.ByteLength &&
            string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal);
    }
}

/// <summary>
/// 功能：保存 tree projection 所需的不可变 journal record 最小字段和独立 data 克隆。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="RecordId">会话内唯一 record ID。</param>
/// <param name="Sequence">物理 append 序号。</param>
/// <param name="ParentId">earlier tree parent；根记录为 null。</param>
/// <param name="Kind">核心 journal kind。</param>
/// <param name="Data">生命周期独立的 record data 对象。</param>
internal sealed record JournalTreeRecord(
    string RecordId,
    long Sequence,
    string? ParentId,
    string Kind,
    JsonElement Data);

/// <summary>
/// 功能：保存 selected branch 动态消息投影及其最新 active compaction 身份。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Messages">已按 compaction 语义克隆的 Provider messages。</param>
/// <param name="CompactionRecordId">最新生效 compaction record；没有时为 null。</param>
internal sealed record SelectedMessageProjection(
    IReadOnlyList<JsonElement> Messages,
    string? CompactionRecordId);

/// <summary>
/// 功能：保存跨记录验证后的 compaction summary 与 retained source record 区间。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="SummaryMessage">canonical assistant summary 的独立 JSON 克隆。</param>
/// <param name="RetainedRecords">firstRetainedRecordId 到 source leaf 的 parent-chain 区间。</param>
internal sealed record ValidatedCompaction(
    JsonElement SummaryMessage,
    IReadOnlyList<JournalTreeRecord> RetainedRecords);

/// <summary>
/// 功能：确保第一条 journal record 也显式序列化必需的 null parentId。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="SchemaVersion">固定 schema 版本。</param>
/// <param name="Kind">核心记录 kind。</param>
/// <param name="RecordId">唯一记录 ID。</param>
/// <param name="SessionId">所属 session ID。</param>
/// <param name="Seq">连续 journal 序号。</param>
/// <param name="ParentId">前一记录 ID；第一条记录必须显式为 null。</param>
/// <param name="Time">UTC 记录时间。</param>
/// <param name="Data">kind 对应数据对象。</param>
internal sealed record JournalRecordLine(
    string SchemaVersion,
    string Kind,
    string RecordId,
    string SessionId,
    long Seq,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ParentId,
    DateTimeOffset Time,
    JsonElement Data);

/// <summary>
/// 功能：保存 session/get 过滤所需的事件序号和 exact portable envelope。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Seq">事件序号。</param>
/// <param name="Event">独立生命周期的事件 JSON。</param>
internal sealed record PersistedEvent(long Seq, JsonElement Event);

/// <summary>
/// 功能：保存崩溃恢复判断工具结果是否歧义所需的最小 intent 字段。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Sequence">原始 intent journal seq。</param>
/// <param name="RunId">所属 run。</param>
/// <param name="TurnId">所属 turn。</param>
/// <param name="ToolCallId">工具调用 ID。</param>
/// <param name="Name">规范工具名，仅用于 unknown-outcome tool message。</param>
/// <param name="Status">原始 prepared/awaiting_approval/started/denied/rejected 状态。</param>
/// <param name="Idempotent">工具声明是否幂等；恢复过程无论取值都不自动执行。</param>
internal sealed record ToolIntentState(
    long Sequence,
    string RunId,
    string TurnId,
    string ToolCallId,
    string Name,
    string Status,
    bool Idempotent);

/// <summary>
/// 功能：保存崩溃恢复所需的审批请求与精确工具绑定。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Sequence">原始 approval.requested journal seq。</param>
/// <param name="RunId">审批所属 run。</param>
/// <param name="ApprovalId">单次审批 ID。</param>
/// <param name="ToolCallId">绑定的工具调用 ID。</param>
internal sealed record RecoveryApprovalState(
    long Sequence,
    string RunId,
    string ApprovalId,
    string ToolCallId);

/// <summary>
/// 功能：保存已 durable 恢复终态及其 error，供崩溃后幂等补写缺失 run.interrupted 事件。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Sequence">interrupted terminal 的原 journal seq。</param>
/// <param name="RunId">已恢复为 interrupted 的 run。</param>
/// <param name="Error">规范 recovered_interrupted_run portable error。</param>
internal sealed record RecoveredTerminalState(long Sequence, string RunId, JsonElement Error);
