using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QxnmForge.Serialization;

namespace QxnmForge.Session;

/// <summary>
/// 功能：表示跨语言 Session writer lease 无法安全获取、验证或释放。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class SessionWriterLeaseException : IOException
{
    /// <summary>
    /// 功能：创建携带稳定错误 kind 与 retryable 语义的安全 lease 异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="message">不包含路径、token 或 owner 原文的安全消息。</param>
    /// <param name="kind">协议 details.kind 使用的稳定机器可读值。</param>
    /// <param name="retryable">客户端是否可在状态变化后重试。</param>
    /// <param name="innerException">可选底层本机异常，仅用于进程内诊断。</param>
    private SessionWriterLeaseException(
        string message,
        string kind,
        bool retryable,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Retryable = retryable;
    }

    /// <summary>
    /// 功能：取得协议 details.kind 使用的稳定机器可读错误类型。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// 功能：取得客户端是否可在 owner 状态变化后重试。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public bool Retryable { get; }

    /// <summary>
    /// 功能：构造 live 或探测结果歧义时的保守锁定错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="innerException">可选底层本机异常。</param>
    /// <returns>retryable session_locked 异常。</returns>
    internal static SessionWriterLeaseException Locked(Exception? innerException = null)
    {
        return new SessionWriterLeaseException(
            "Session is locked.",
            "session_locked",
            retryable: true,
            innerException);
    }

    /// <summary>
    /// 功能：构造 owner Schema、路径类型或安全边界无效错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="innerException">可选底层本机异常。</param>
    /// <returns>不可重试 writer_lock_invalid 异常。</returns>
    internal static SessionWriterLeaseException Invalid(Exception? innerException = null)
    {
        return new SessionWriterLeaseException(
            "Session writer lease metadata is invalid.",
            "writer_lock_invalid",
            retryable: false,
            innerException);
    }

    /// <summary>
    /// 功能：构造本机 listener 或必要文件系统能力不可用错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="innerException">可选底层本机异常。</param>
    /// <returns>不可重试 writer_lock_unavailable 异常。</returns>
    internal static SessionWriterLeaseException Unavailable(Exception? innerException = null)
    {
        return new SessionWriterLeaseException(
            "Session writer lease is unavailable.",
            "writer_lock_unavailable",
            retryable: false,
            innerException);
    }

    /// <summary>
    /// 功能：构造无法证明 token 或 moved directory 身份时的安全清理失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="innerException">可选底层本机异常。</param>
    /// <returns>不可重试 writer_lock_cleanup_failed 异常。</returns>
    internal static SessionWriterLeaseException CleanupFailed(Exception? innerException = null)
    {
        return new SessionWriterLeaseException(
            "Session writer lease cleanup could not be proven safe.",
            "writer_lock_cleanup_failed",
            retryable: false,
            innerException);
    }
}

/// <summary>
/// 功能：描述 loopback owner 探测的保守分类结果。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal enum WriterLeaseProbeOutcome
{
    Connected,
    ConnectionRefused,
    Ambiguous,
}

/// <summary>
/// 功能：提供 writer lease 的有界时间参数与确定性 conformance 注入点。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class WriterLeaseOptions
{
    /// <summary>
    /// 功能：创建 lease 时间边界和可选测试探针；生产调用使用固定安全默认值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="initializationGrace">缺失或 incomplete owner 可等待的初始化窗口。</param>
    /// <param name="acquisitionTimeout">一次获取最多等待时间。</param>
    /// <param name="conflictRetryDelay">初始化冲突之间的有界重试间隔。</param>
    /// <param name="probeTimeout">单次 literal loopback connect 的超时。</param>
    /// <param name="timeProvider">单调 deadline 与 UTC grace 年龄来源。</param>
    /// <param name="probeOverride">仅供确定性 conformance 测试替换 socket 结果。</param>
    /// <param name="afterStaleMove">仅供测试在 stale 原子移动后制造身份变化。</param>
    /// <param name="afterReleaseMove">仅供测试在 release 原子移动后制造身份变化。</param>
    internal WriterLeaseOptions(
        TimeSpan initializationGrace,
        TimeSpan acquisitionTimeout,
        TimeSpan conflictRetryDelay,
        TimeSpan probeTimeout,
        TimeProvider? timeProvider = null,
        Func<int, CancellationToken, ValueTask<WriterLeaseProbeOutcome>>? probeOverride = null,
        Func<string, CancellationToken, ValueTask>? afterStaleMove = null,
        Func<string, CancellationToken, ValueTask>? afterReleaseMove = null)
    {
        InitializationGrace = initializationGrace;
        AcquisitionTimeout = acquisitionTimeout;
        ConflictRetryDelay = conflictRetryDelay;
        ProbeTimeout = probeTimeout;
        TimeProvider = timeProvider ?? TimeProvider.System;
        ProbeOverride = probeOverride;
        AfterStaleMove = afterStaleMove;
        AfterReleaseMove = afterReleaseMove;
    }

    /// <summary>
    /// 功能：取得规范要求的生产 lease 参数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal static WriterLeaseOptions Default { get; } = new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(250));

    internal TimeSpan InitializationGrace { get; }

    internal TimeSpan AcquisitionTimeout { get; }

    internal TimeSpan ConflictRetryDelay { get; }

    internal TimeSpan ProbeTimeout { get; }

    internal TimeProvider TimeProvider { get; }

    internal Func<int, CancellationToken, ValueTask<WriterLeaseProbeOutcome>>? ProbeOverride { get; }

    internal Func<string, CancellationToken, ValueTask>? AfterStaleMove { get; }

    internal Func<string, CancellationToken, ValueTask>? AfterReleaseMove { get; }
}

/// <summary>
/// 功能：使用 literal IPv4 loopback listener 和原子目录维护跨语言 Session writer 所有权。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class WriterLease
{
    private const string LeaseDirectoryName = "writer.lock.d";
    private const string OwnerFileName = "owner.json";
    private const string LeaseProtocol = "tcp-loopback-writer-lease-v1";
    private const string LoopbackHost = "127.0.0.1";
    private const int MaxOwnerBytes = 16_384;
    private const long MaxSafeInteger = 9_007_199_254_740_991;

    private static readonly HashSet<string> RootOwnerProperties =
    [
        "schemaVersion",
        "protocol",
        "sessionId",
        "token",
        "endpoint",
        "pid",
        "createdAt",
        "implementation",
    ];

    private static readonly HashSet<string> EndpointProperties = ["host", "port"];
    private static readonly HashSet<string> ImplementationProperties = ["name", "version", "language"];
    private static readonly HashSet<string> ImplementationLanguages =
        ["dotnet", "rust", "go", "typescript", "python"];
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string sessionDirectory;
    private readonly string sessionId;
    private readonly string token;
    private readonly LoopbackWitness witness;
    private readonly WriterLeaseOptions options;
    private string? movedDirectory;
    private bool closed;

    /// <summary>
    /// 功能：保存已经原子提交 owner.json 且 listener 持续开放的 lease 资源。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionDirectory">已验证的真实 Session 目录。</param>
    /// <param name="sessionId">owner 绑定的 opaque Session ID。</param>
    /// <param name="token">本次唯一 256-bit owner token。</param>
    /// <param name="witness">已 listen 的 literal IPv4 loopback witness。</param>
    /// <param name="options">获取与验证时间边界。</param>
    private WriterLease(
        string sessionDirectory,
        string sessionId,
        string token,
        LoopbackWitness witness,
        WriterLeaseOptions options)
    {
        this.sessionDirectory = sessionDirectory;
        this.sessionId = sessionId;
        this.token = token;
        this.witness = witness;
        this.options = options;
    }

    /// <summary>
    /// 功能：原子获取 portable writer lease，并仅在明确 connection refused 或超 grace 缺失元数据时接管。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionDirectory">canonical Session 目录；必须是真实目录而非链接。</param>
    /// <param name="sessionId">符合公共 opaque ID Schema 的 Session ID。</param>
    /// <param name="options">可选确定性 conformance 参数；生产调用不得降低默认安全值。</param>
    /// <param name="cancellationToken">等待初始化 grace、探测与文件 I/O 的取消信号。</param>
    /// <returns>owner 已 durable 写入且 witness 正在 listen 的唯一 lease。</returns>
    /// <remarks>不变量：外部 host 永不连接；PID 和 createdAt 绝不用于证明 stale；每轮使用新 token/listener。</remarks>
    /// <exception cref="SessionWriterLeaseException">owner live、无效、探测歧义或安全文件操作失败。</exception>
    internal static async Task<WriterLease> AcquireAsync(
        string sessionDirectory,
        string sessionId,
        WriterLeaseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidOpaqueId(sessionId))
        {
            throw new ArgumentException("sessionId is not a valid opaque ID", nameof(sessionId));
        }

        var canonicalDirectory = Path.GetFullPath(sessionDirectory);
        RequireRealDirectory(canonicalDirectory);
        var effectiveOptions = options ?? WriterLeaseOptions.Default;
        var startedAt = effectiveOptions.TimeProvider.GetTimestamp();
        while (GetRemaining(effectiveOptions, startedAt) > TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var witness = LoopbackWitness.Start();
            var contenderToken = CreateToken();
            var transferred = false;
            try
            {
                var installed = await TryInstallOwnerAsync(
                    canonicalDirectory,
                    sessionId,
                    contenderToken,
                    witness.Port,
                    cancellationToken).ConfigureAwait(false);
                if (installed)
                {
                    try
                    {
                        witness.EnsureOpen();
                    }
                    catch (SessionWriterLeaseException)
                    {
                        await CleanupFailedOwnerAsync(
                            canonicalDirectory,
                            sessionId,
                            contenderToken,
                            cancellationToken).ConfigureAwait(false);
                        throw;
                    }

                    transferred = true;
                    return new WriterLease(
                        canonicalDirectory,
                        sessionId,
                        contenderToken,
                        witness,
                        effectiveOptions);
                }

                await witness.CloseAsync().ConfigureAwait(false);
                var retry = await ResolveConflictAsync(
                    canonicalDirectory,
                    sessionId,
                    contenderToken,
                    startedAt,
                    effectiveOptions,
                    cancellationToken).ConfigureAwait(false);
                if (!retry)
                {
                    throw SessionWriterLeaseException.Locked();
                }
            }
            finally
            {
                if (!transferred)
                {
                    await witness.CloseAsync().ConfigureAwait(false);
                }
            }
        }

        throw SessionWriterLeaseException.Locked();
    }

    /// <summary>
    /// 功能：在每次 journal 写入前确认本 lease 的 listener 与 canonical owner 状态仍可用。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <remarks>不变量：检查失败会阻止追加；不会尝试重建 listener 或静默降级到普通文件锁。</remarks>
    /// <exception cref="SessionWriterLeaseException">lease 已释放、已移动或 accept loop 意外终止。</exception>
    internal void EnsureLive()
    {
        if (closed || movedDirectory is not null)
        {
            throw SessionWriterLeaseException.Unavailable();
        }

        witness.EnsureOpen();
    }

    /// <summary>
    /// 功能：在 Session 读写前同时确认 listener 存活且 canonical owner exact token 仍属于本 lease。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="cancellationToken">有界 owner 读取取消信号。</param>
    /// <returns>listener 与 canonical token 前后一致时完成的 Task。</returns>
    /// <remarks>不变量：owner 消失、被替换或无法严格验证时立即阻止 Session I/O，绝不凭 advisory lock 单独继续。</remarks>
    /// <exception cref="SessionWriterLeaseException">portable ownership 已丢失或验证结果歧义。</exception>
    internal async Task EnsureOwnershipAsync(CancellationToken cancellationToken = default)
    {
        EnsureLive();
        OwnerSnapshot owner;
        try
        {
            owner = await ReadOwnerAsync(LeaseDirectoryPath, sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsOwnerReadFailure(exception))
        {
            throw SessionWriterLeaseException.Unavailable(exception);
        }

        if (!TokensEqual(owner.Token, token))
        {
            throw SessionWriterLeaseException.Unavailable();
        }

        witness.EnsureOpen();
    }

    /// <summary>
    /// 功能：listener 仍开放时验证 exact token，并把 canonical lease 原子移动到唯一 release sibling。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="cancellationToken">读取和测试钩子的取消信号。</param>
    /// <returns>移动后逐字节与 token 二次验证完成的 Task。</returns>
    /// <remarks>不变量：token mismatch 或 moved 身份变化时不删除任何目录，并尽力恢复 canonical 名称。</remarks>
    /// <exception cref="SessionWriterLeaseException">无法证明当前 owner 或原子移动身份。</exception>
    internal async Task BeginReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (closed || movedDirectory is not null)
        {
            return;
        }

        witness.EnsureOpen();
        OwnerSnapshot original;
        try
        {
            original = await ReadOwnerAsync(LeaseDirectoryPath, sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOwnerReadFailure(exception))
        {
            throw SessionWriterLeaseException.CleanupFailed(exception);
        }

        if (!TokensEqual(original.Token, token))
        {
            throw SessionWriterLeaseException.CleanupFailed();
        }

        var moved = Path.Combine(sessionDirectory, "writer.release." + CreateToken());
        try
        {
            Directory.Move(LeaseDirectoryPath, moved);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SessionWriterLeaseException.CleanupFailed(exception);
        }

        try
        {
            if (options.AfterReleaseMove is not null)
            {
                await options.AfterReleaseMove(moved, cancellationToken).ConfigureAwait(false);
            }

            var movedOwner = await ReadOwnerAsync(moved, sessionId, cancellationToken).ConfigureAwait(false);
            if (!original.Bytes.AsSpan().SequenceEqual(movedOwner.Bytes) ||
                !TokensEqual(movedOwner.Token, token))
            {
                throw new LeaseChangedException();
            }
        }
        catch (Exception exception) when (IsOwnerReadFailure(exception))
        {
            RestoreCanonicalOrThrow(moved);
            throw SessionWriterLeaseException.CleanupFailed(exception);
        }

        movedDirectory = moved;
    }

    /// <summary>
    /// 功能：advisory lock 释放后关闭 listener，并只删除本 lease 原子赢得且验证完成的 sibling。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>listener 停止且私有 moved directory 清理完成的 Task。</returns>
    /// <remarks>不变量：BeginReleaseAsync 未成功时 canonical writer.lock.d 永不删除。</remarks>
    /// <exception cref="SessionWriterLeaseException">listener 无法有界停止或 moved sibling 无法安全删除。</exception>
    internal async Task FinishReleaseAsync()
    {
        if (closed)
        {
            return;
        }

        closed = true;
        SessionWriterLeaseException? firstError = null;
        try
        {
            await witness.CloseAsync().ConfigureAwait(false);
        }
        catch (SessionWriterLeaseException exception)
        {
            firstError = exception;
        }

        if (movedDirectory is not null)
        {
            try
            {
                RemoveMovedDirectory(movedDirectory);
            }
            catch (SessionWriterLeaseException exception)
            {
                firstError ??= exception;
            }
        }

        if (firstError is not null)
        {
            throw firstError;
        }
    }

    private string LeaseDirectoryPath => Path.Combine(sessionDirectory, LeaseDirectoryName);

    /// <summary>
    /// 功能：在 canonical 目录不存在时创建目录，并用 create-new owner rename 竞争唯一 metadata 所有权。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionDirectory">真实 Session 目录。</param>
    /// <param name="sessionId">owner 绑定 Session ID。</param>
    /// <param name="token">本轮随机 token。</param>
    /// <param name="port">已 listen 的 loopback 端口。</param>
    /// <param name="cancellationToken">owner durable 写入取消信号。</param>
    /// <returns>exact owner 成功安装返回 true；已有 canonical owner 返回 false。</returns>
    private static async Task<bool> TryInstallOwnerAsync(
        string sessionDirectory,
        string sessionId,
        string token,
        int port,
        CancellationToken cancellationToken)
    {
        var leaseDirectory = Path.Combine(sessionDirectory, LeaseDirectoryName);
        if (TryGetAttributes(leaseDirectory, out var existingAttributes))
        {
            ValidateDirectoryAttributes(existingAttributes);
            return false;
        }

        try
        {
            Directory.CreateDirectory(leaseDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (TryGetAttributes(leaseDirectory, out existingAttributes))
            {
                ValidateDirectoryAttributes(existingAttributes);
                return false;
            }

            throw SessionWriterLeaseException.Unavailable(exception);
        }

        RequireRealDirectory(leaseDirectory);
        var ownerBytes = SerializeOwner(sessionId, token, port);
        var temporaryPath = Path.Combine(leaseDirectory, ".owner." + token + ".tmp");
        var ownerPath = Path.Combine(leaseDirectory, OwnerFileName);
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4_096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(ownerBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            RequireRealDirectory(leaseDirectory);
            try
            {
                File.Move(temporaryPath, ownerPath, overwrite: false);
            }
            catch (IOException) when (TryGetAttributes(ownerPath, out _))
            {
                DeleteUniqueTemporary(temporaryPath);
                return false;
            }

            var committed = await ReadOwnerAsync(leaseDirectory, sessionId, cancellationToken).ConfigureAwait(false);
            if (!ownerBytes.AsSpan().SequenceEqual(committed.Bytes) || !TokensEqual(committed.Token, token))
            {
                return false;
            }

            return true;
        }
        catch (SessionWriterLeaseException)
        {
            DeleteUniqueTemporary(temporaryPath);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DeleteUniqueTemporary(temporaryPath);
            throw SessionWriterLeaseException.Unavailable(exception);
        }
    }

    /// <summary>
    /// 功能：获取中途失败时仅在 canonical exact token 仍属于本轮时原子移动、重验证并删除。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionDirectory">真实 Session 目录。</param>
    /// <param name="sessionId">预期 owner Session ID。</param>
    /// <param name="token">失败 acquisition 的唯一 token。</param>
    /// <param name="cancellationToken">owner 重验证取消信号。</param>
    /// <returns>安全清理完成或无法证明归属而原样保留后的 Task。</returns>
    private static async Task CleanupFailedOwnerAsync(
        string sessionDirectory,
        string sessionId,
        string token,
        CancellationToken cancellationToken)
    {
        var canonical = Path.Combine(sessionDirectory, LeaseDirectoryName);
        OwnerSnapshot original;
        try
        {
            original = await ReadOwnerAsync(canonical, sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOwnerReadFailure(exception))
        {
            return;
        }

        if (!TokensEqual(original.Token, token))
        {
            return;
        }

        var moved = Path.Combine(sessionDirectory, "writer.release." + CreateToken());
        try
        {
            Directory.Move(canonical, moved);
            var movedOwner = await ReadOwnerAsync(moved, sessionId, cancellationToken).ConfigureAwait(false);
            if (!original.Bytes.AsSpan().SequenceEqual(movedOwner.Bytes) ||
                !TokensEqual(movedOwner.Token, token))
            {
                throw new LeaseChangedException();
            }
        }
        catch (Exception exception) when (IsOwnerReadFailure(exception))
        {
            if (TryGetAttributes(moved, out _))
            {
                RestoreCanonicalOrThrow(sessionDirectory, moved);
            }

            throw SessionWriterLeaseException.CleanupFailed(exception);
        }

        RemoveMovedDirectory(moved);
    }

    /// <summary>
    /// 功能：读取冲突 owner、应用 grace 和 probe 规则，并在 stale 时执行原子接管。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionDirectory">真实 Session 目录。</param>
    /// <param name="sessionId">预期 owner Session ID。</param>
    /// <param name="contenderToken">本轮唯一 takeover sibling token。</param>
    /// <param name="startedAt">单调获取起点。</param>
    /// <param name="options">时间边界和探测器。</param>
    /// <param name="cancellationToken">等待和探测取消信号。</param>
    /// <returns>canonical 状态变化后应重启获取返回 true；live/歧义直接抛 locked。</returns>
    private static async Task<bool> ResolveConflictAsync(
        string sessionDirectory,
        string sessionId,
        string contenderToken,
        long startedAt,
        WriterLeaseOptions options,
        CancellationToken cancellationToken)
    {
        var leaseDirectory = Path.Combine(sessionDirectory, LeaseDirectoryName);
        StaleCandidate candidate;
        try
        {
            var owner = await ReadOwnerAsync(leaseDirectory, sessionId, cancellationToken).ConfigureAwait(false);
            var outcome = options.ProbeOverride is null
                ? await ProbeOwnerAsync(owner.Port, options.ProbeTimeout, cancellationToken).ConfigureAwait(false)
                : await options.ProbeOverride(owner.Port, cancellationToken).ConfigureAwait(false);
            if (outcome != WriterLeaseProbeOutcome.ConnectionRefused)
            {
                throw SessionWriterLeaseException.Locked();
            }

            candidate = new StaleCandidate(owner.Bytes);
        }
        catch (OwnerMissingException)
        {
            var incompleteCandidate = await ResolveIncompleteOwnerAsync(
                leaseDirectory,
                ownerBytes: null,
                startedAt,
                options,
                cancellationToken).ConfigureAwait(false);
            if (incompleteCandidate is null)
            {
                return true;
            }

            candidate = incompleteCandidate;
        }
        catch (OwnerIncompleteException exception)
        {
            var incompleteCandidate = await ResolveIncompleteOwnerAsync(
                leaseDirectory,
                exception.Bytes,
                startedAt,
                options,
                cancellationToken).ConfigureAwait(false);
            if (incompleteCandidate is null)
            {
                return true;
            }

            candidate = incompleteCandidate;
        }
        catch (LeaseChangedException)
        {
            return true;
        }

        return await TakeOverStaleAsync(
            sessionDirectory,
            sessionId,
            contenderToken,
            candidate,
            options,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：对 missing/incomplete owner 应用两秒初始化 grace 与总 acquisition deadline。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="leaseDirectory">canonical lease 目录。</param>
    /// <param name="ownerBytes">incomplete 精确字节；missing 时为 null。</param>
    /// <param name="startedAt">单调获取起点。</param>
    /// <param name="options">时间边界。</param>
    /// <param name="cancellationToken">等待取消信号。</param>
    /// <returns>超 grace 且未变化时返回 stale 候选；仍在 grace 内等待后返回 null 以重试。</returns>
    private static async Task<StaleCandidate?> ResolveIncompleteOwnerAsync(
        string leaseDirectory,
        byte[]? ownerBytes,
        long startedAt,
        WriterLeaseOptions options,
        CancellationToken cancellationToken)
    {
        var age = GetDirectoryAge(leaseDirectory, options.TimeProvider);
        if (age < TimeSpan.Zero)
        {
            throw SessionWriterLeaseException.Locked();
        }

        if (age < options.InitializationGrace)
        {
            var remaining = GetRemaining(options, startedAt);
            var untilGrace = options.InitializationGrace - age;
            var delay = Min(options.ConflictRetryDelay, untilGrace, remaining);
            if (delay <= TimeSpan.Zero)
            {
                throw SessionWriterLeaseException.Locked();
            }

            await Task.Delay(delay, options.TimeProvider, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return new StaleCandidate(ownerBytes);
    }

    /// <summary>
    /// 功能：原子移动 stale canonical lease，逐字节重验证候选后才删除并要求重新获取。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionDirectory">真实 Session 目录。</param>
    /// <param name="sessionId">预期 owner Session ID。</param>
    /// <param name="contenderToken">唯一 moved sibling 后缀。</param>
    /// <param name="candidate">probe/grace 阶段保存的 exact 候选。</param>
    /// <param name="options">可选 moved 测试观察器。</param>
    /// <param name="cancellationToken">重验证取消信号。</param>
    /// <returns>成功清理或输掉 rename 时返回 true，以全新 listener/token 重试。</returns>
    private static async Task<bool> TakeOverStaleAsync(
        string sessionDirectory,
        string sessionId,
        string contenderToken,
        StaleCandidate candidate,
        WriterLeaseOptions options,
        CancellationToken cancellationToken)
    {
        var canonical = Path.Combine(sessionDirectory, LeaseDirectoryName);
        var moved = Path.Combine(sessionDirectory, "writer.stale." + contenderToken);
        try
        {
            Directory.Move(canonical, moved);
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException exception)
        {
            if (!TryGetAttributes(canonical, out _) || TryGetAttributes(moved, out _))
            {
                return true;
            }

            throw SessionWriterLeaseException.Locked(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw SessionWriterLeaseException.Locked(exception);
        }

        try
        {
            if (options.AfterStaleMove is not null)
            {
                await options.AfterStaleMove(moved, cancellationToken).ConfigureAwait(false);
            }

            await RevalidateCandidateAsync(moved, candidate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOwnerReadFailure(exception))
        {
            RestoreCanonicalOrThrow(sessionDirectory, moved);
            throw SessionWriterLeaseException.Locked(exception);
        }

        RemoveMovedDirectory(moved);
        return true;
    }

    /// <summary>
    /// 功能：确认 moved metadata 与 probe/grace 前候选逐字节完全相同。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="movedDirectory">本 contender 原子赢得的 sibling。</param>
    /// <param name="candidate">移动前候选字节；missing 候选为 null。</param>
    /// <param name="cancellationToken">读取取消信号。</param>
    /// <returns>身份完全一致时完成的 Task。</returns>
    private static async Task RevalidateCandidateAsync(
        string movedDirectory,
        StaleCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (candidate.OwnerBytes is null)
        {
            try
            {
                _ = await ReadOwnerBytesAsync(movedDirectory, cancellationToken).ConfigureAwait(false);
            }
            catch (OwnerMissingException)
            {
                return;
            }

            throw new LeaseChangedException();
        }

        var movedBytes = await ReadOwnerBytesAsync(movedDirectory, cancellationToken).ConfigureAwait(false);
        if (!candidate.OwnerBytes.AsSpan().SequenceEqual(movedBytes))
        {
            throw new LeaseChangedException();
        }
    }

    /// <summary>
    /// 功能：只探测 literal 127.0.0.1 port，并把除明确 refused 外的所有结果归为 live/ambiguous。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="port">已严格验证为 1..65535 的 owner 端口。</param>
    /// <param name="timeout">单次 connect 的有界超时。</param>
    /// <param name="cancellationToken">调用者取消信号。</param>
    /// <returns>连接成功、明确拒绝或歧义结果。</returns>
    private static async ValueTask<WriterLeaseProbeOutcome> ProbeOwnerAsync(
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, port),
                timeoutSource.Token).ConfigureAwait(false);
            return WriterLeaseProbeOutcome.Connected;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return WriterLeaseProbeOutcome.ConnectionRefused;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WriterLeaseProbeOutcome.Ambiguous;
        }
        catch (SocketException)
        {
            return WriterLeaseProbeOutcome.Ambiguous;
        }
        catch (UnauthorizedAccessException)
        {
            return WriterLeaseProbeOutcome.Ambiguous;
        }
    }

    /// <summary>
    /// 功能：严格读取并验证 owner.json，返回 exact bytes、token 与安全 loopback port。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="leaseDirectory">canonical 或已原子移动的真实 lease 目录。</param>
    /// <param name="sessionId">必须与 owner 完全一致的 Session ID。</param>
    /// <param name="cancellationToken">有界读取取消信号。</param>
    /// <returns>不包含可变 JsonDocument 生命周期的验证快照。</returns>
    private static async Task<OwnerSnapshot> ReadOwnerAsync(
        string leaseDirectory,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadOwnerBytesAsync(leaseDirectory, cancellationToken).ConfigureAwait(false);
        return ParseOwner(bytes, sessionId);
    }

    /// <summary>
    /// 功能：拒绝稳定链接/非文件并有界读取 owner exact bytes，前后指纹变化按 lease race 处理。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="leaseDirectory">预期真实 canonical/moved lease 目录。</param>
    /// <param name="cancellationToken">读取取消信号。</param>
    /// <returns>最多 16384 字节的 owner 原始内容。</returns>
    private static async Task<byte[]> ReadOwnerBytesAsync(
        string leaseDirectory,
        CancellationToken cancellationToken)
    {
        RequireRealDirectory(leaseDirectory);
        var ownerPath = Path.Combine(leaseDirectory, OwnerFileName);
        var before = ReadFileFingerprint(ownerPath);
        if (before.Length > MaxOwnerBytes)
        {
            throw SessionWriterLeaseException.Invalid();
        }

        try
        {
            await using var stream = new FileStream(
                ownerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4_096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var handleAttributes = File.GetAttributes(stream.SafeFileHandle);
            ValidateOwnerFileAttributes(handleAttributes);
            if (stream.Length < 0 || stream.Length > MaxOwnerBytes)
            {
                throw SessionWriterLeaseException.Invalid();
            }

            var buffer = new byte[MaxOwnerBytes + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(total, buffer.Length - total),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > MaxOwnerBytes)
            {
                throw SessionWriterLeaseException.Invalid();
            }

            var after = ReadFileFingerprint(ownerPath);
            if (!before.Equals(after) || total != stream.Length)
            {
                throw new LeaseChangedException();
            }

            return buffer.AsSpan(0, total).ToArray();
        }
        catch (FileNotFoundException exception)
        {
            throw new LeaseChangedException(exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new LeaseChangedException(exception);
        }
        catch (SessionWriterLeaseException)
        {
            throw;
        }
        catch (LeaseChangedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SessionWriterLeaseException.Locked(exception);
        }
    }

    /// <summary>
    /// 功能：按 writer-lock Schema 严格解析 UTF-8 JSON，拒绝重复键、未知字段和任何外部 host。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="bytes">有界 owner exact bytes。</param>
    /// <param name="sessionId">当前 canonical Session ID。</param>
    /// <returns>仅含安全 probe port 和 exact token 的快照。</returns>
    private static OwnerSnapshot ParseOwner(byte[] bytes, string sessionId)
    {
        if (bytes.Length == 0)
        {
            throw new OwnerIncompleteException(bytes);
        }

        if (bytes.Length > MaxOwnerBytes ||
            (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF))
        {
            throw SessionWriterLeaseException.Invalid();
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw SessionWriterLeaseException.Invalid(exception);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
        }
        catch (JsonException exception)
        {
            if (LooksStructurallyIncomplete(text) &&
                !text.Contains("NaN", StringComparison.Ordinal) &&
                !text.Contains("Infinity", StringComparison.Ordinal))
            {
                throw new OwnerIncompleteException(bytes, exception);
            }

            throw SessionWriterLeaseException.Invalid(exception);
        }

        using (document)
        {
            var root = document.RootElement;
            ValidateNoDuplicateKeys(root);
            ValidateFiniteNumbers(root);
            if (root.ValueKind != JsonValueKind.Object || !HasExactProperties(root, RootOwnerProperties))
            {
                throw SessionWriterLeaseException.Invalid();
            }

            if (!TryGetString(root, "schemaVersion", out var schemaVersion) || schemaVersion != "0.1" ||
                !TryGetString(root, "protocol", out var protocol) || protocol != LeaseProtocol ||
                !TryGetString(root, "sessionId", out var ownerSessionId) || ownerSessionId != sessionId ||
                !TryGetString(root, "token", out var ownerToken) || !IsValidToken(ownerToken) ||
                !TryGetString(root, "createdAt", out var createdAt) || !IsValidUtcTime(createdAt))
            {
                throw SessionWriterLeaseException.Invalid();
            }

            if (!root.TryGetProperty("pid", out var pidElement) ||
                !pidElement.TryGetInt64(out var pid) ||
                pid is < 1 or > MaxSafeInteger)
            {
                throw SessionWriterLeaseException.Invalid();
            }

            if (!root.TryGetProperty("endpoint", out var endpoint) ||
                endpoint.ValueKind != JsonValueKind.Object ||
                !HasExactProperties(endpoint, EndpointProperties) ||
                !TryGetString(endpoint, "host", out var host) ||
                host != LoopbackHost ||
                !endpoint.TryGetProperty("port", out var portElement) ||
                !portElement.TryGetInt32(out var port) ||
                port is < 1 or > 65_535)
            {
                throw SessionWriterLeaseException.Invalid();
            }

            if (!root.TryGetProperty("implementation", out var implementation) ||
                !IsValidImplementation(implementation))
            {
                throw SessionWriterLeaseException.Invalid();
            }

            return new OwnerSnapshot(bytes, ownerToken, port);
        }
    }

    /// <summary>
    /// 功能：在 strict parser 失败后仅把未闭合 object/array/string 识别为 crash partial metadata。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="text">已通过 strict UTF-8 解码但 JSON parser 拒绝的 owner 文本。</param>
    /// <returns>顶层 object 尚未结构闭合时返回 true；平衡但语法错误返回 false。</returns>
    private static bool LooksStructurallyIncomplete(string text)
    {
        var trimmed = text.AsSpan().Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            return false;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        foreach (var character in trimmed)
        {
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character is '{' or '[')
            {
                depth++;
            }
            else if (character is '}' or ']')
            {
                depth--;
                if (depth < 0)
                {
                    return false;
                }
            }
        }

        return inString || depth > 0;
    }

    /// <summary>
    /// 功能：序列化固定字段 owner 元数据并以 LF 结尾，禁止写入路径、环境或凭据。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">已验证 Session ID。</param>
    /// <param name="token">本次 256-bit lowercase token。</param>
    /// <param name="port">literal loopback listener 端口。</param>
    /// <returns>小于 owner 大小上限的严格 UTF-8 JSON bytes。</returns>
    private static byte[] SerializeOwner(string sessionId, string token, int port)
    {
        var owner = new
        {
            SchemaVersion = "0.1",
            Protocol = LeaseProtocol,
            SessionId = sessionId,
            Token = token,
            Endpoint = new { Host = LoopbackHost, Port = port },
            Pid = Environment.ProcessId,
            CreatedAt = DateTimeOffset.UtcNow,
            Implementation = new { Name = "qxnm-forge-dotnet", Version = "0.1.0", Language = "dotnet" },
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(owner, JsonDefaults.Options);
        if (json.Length + 1 > MaxOwnerBytes)
        {
            throw SessionWriterLeaseException.Unavailable();
        }

        var bytes = new byte[json.Length + 1];
        json.CopyTo(bytes, 0);
        bytes[^1] = (byte)'\n';
        return bytes;
    }

    /// <summary>
    /// 功能：递归拒绝 owner 任意对象层级中的 duplicate JSON key。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">待验证 JSON 元素。</param>
    private static void ValidateNoDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw SessionWriterLeaseException.Invalid();
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
    /// 功能：递归拒绝指数溢出为 NaN/Infinity 的 JSON number。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">待验证 JSON 元素。</param>
    private static void ValidateFiniteNumbers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number &&
            (!element.TryGetDouble(out var value) || !double.IsFinite(value)))
        {
            throw SessionWriterLeaseException.Invalid();
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                ValidateFiniteNumbers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateFiniteNumbers(item);
            }
        }
    }

    /// <summary>
    /// 功能：确认 JSON object 的属性集合与 Schema allowlist 完全相同。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">必须为 object 的 JSON 元素。</param>
    /// <param name="expected">必需且唯一允许的属性名集合。</param>
    /// <returns>无缺失、未知或重复字段时返回 true。</returns>
    private static bool HasExactProperties(JsonElement element, HashSet<string> expected)
    {
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            count++;
            if (!expected.Contains(property.Name))
            {
                return false;
            }
        }

        return count == expected.Count;
    }

    /// <summary>
    /// 功能：读取必需 JSON string 属性且拒绝 null 与其他类型。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父 object。</param>
    /// <param name="name">必需属性名。</param>
    /// <param name="value">成功时的非 null 字符串。</param>
    /// <returns>属性存在且为 string 时返回 true。</returns>
    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    /// <summary>
    /// 功能：验证 implementation 与公共 JSON-RPC implementation Schema 完全一致。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="implementation">待验证 owner 子对象。</param>
    /// <returns>固定字段、长度和五语言枚举均有效时返回 true。</returns>
    private static bool IsValidImplementation(JsonElement implementation)
    {
        return implementation.ValueKind == JsonValueKind.Object &&
            HasExactProperties(implementation, ImplementationProperties) &&
            TryGetString(implementation, "name", out var name) &&
            name.Length is >= 1 and <= 128 &&
            TryGetString(implementation, "version", out var version) &&
            version.Length is >= 1 and <= 64 &&
            TryGetString(implementation, "language", out var language) &&
            ImplementationLanguages.Contains(language);
    }

    /// <summary>
    /// 功能：验证 createdAt 为以 Z 结尾且可解析的 UTC RFC 3339 日期时间。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">owner createdAt 字符串。</param>
    /// <returns>形状、日历值和 UTC offset 均有效时返回 true。</returns>
    private static bool IsValidUtcTime(string value)
    {
        if (value.Length is < 20 or > 64 ||
            value[4] != '-' || value[7] != '-' ||
            (value[10] != 'T' && value[10] != 't') ||
            value[13] != ':' || value[16] != ':' || value[^1] != 'Z')
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (index is 4 or 7 or 10 or 13 or 16 || index == value.Length - 1)
            {
                continue;
            }

            if (index == 19 && value[index] == '.')
            {
                continue;
            }

            if (!char.IsAsciiDigit(value[index]))
            {
                return false;
            }
        }

        var normalized = value[10] == 't' ? value[..10] + 'T' + value[11..] : value;
        return DateTimeOffset.TryParse(
            normalized,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed) && parsed.Offset == TimeSpan.Zero;
    }

    /// <summary>
    /// 功能：验证 token 为恰好 64 位 lowercase hexadecimal。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">待验证 token。</param>
    /// <returns>符合 256-bit lowercase hex 形状时返回 true。</returns>
    private static bool IsValidToken(string value)
    {
        return value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    /// <summary>
    /// 功能：验证公共 opaque ID 长度、首字符与 ASCII allowlist。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">待验证 Session ID。</param>
    /// <returns>无法形成路径的合法 opaque ID 返回 true。</returns>
    private static bool IsValidOpaqueId(string value)
    {
        return value.Length is >= 1 and <= 128 &&
            char.IsAsciiLetterOrDigit(value[0]) &&
            value.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');
    }

    /// <summary>
    /// 功能：生成不可复用的 256-bit lowercase hexadecimal token。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>64 个 lowercase hex 字符。</returns>
    private static string CreateToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    /// <summary>
    /// 功能：以固定时序比较两个已验证 token，避免把 token 作为普通可变字符串身份判断。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="left">第一个 token。</param>
    /// <param name="right">第二个 token。</param>
    /// <returns>字节完全一致返回 true。</returns>
    private static bool TokensEqual(string left, string right)
    {
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
    }

    /// <summary>
    /// 功能：读取路径属性并把不存在与其他 I/O 失败分离，供原子竞争 loser 判断。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">待检查 canonical 或 sibling 路径。</param>
    /// <param name="attributes">存在时的稳定属性快照。</param>
    /// <returns>路径存在返回 true；不存在返回 false。</returns>
    private static bool TryGetAttributes(string path, out FileAttributes attributes)
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SessionWriterLeaseException.Locked(exception);
        }
    }

    /// <summary>
    /// 功能：确认 lease/Session 路径是真实目录而不是 symlink、junction 或普通文件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="directory">待验证 canonical 目录。</param>
    private static void RequireRealDirectory(string directory)
    {
        if (!TryGetAttributes(directory, out var attributes))
        {
            throw new LeaseChangedException();
        }

        ValidateDirectoryAttributes(attributes);
    }

    /// <summary>
    /// 功能：拒绝 reparse/link 与非目录 lease 路径属性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="attributes">File.GetAttributes 返回值。</param>
    private static void ValidateDirectoryAttributes(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            (attributes & FileAttributes.Directory) == 0)
        {
            throw SessionWriterLeaseException.Invalid();
        }
    }

    /// <summary>
    /// 功能：拒绝 owner.json 的 reparse/link 与目录属性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="attributes">路径或已打开 handle 属性。</param>
    private static void ValidateOwnerFileAttributes(FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw SessionWriterLeaseException.Invalid();
        }
    }

    /// <summary>
    /// 功能：读取 owner 文件长度和时间指纹，并把 missing 与链接/非文件分别分类。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="ownerPath">canonical/moved owner.json 路径。</param>
    /// <returns>读取前后用于检测替换或原地变化的文件指纹。</returns>
    private static OwnerFileFingerprint ReadFileFingerprint(string ownerPath)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(ownerPath);
        }
        catch (FileNotFoundException exception)
        {
            throw new OwnerMissingException(exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new OwnerMissingException(exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SessionWriterLeaseException.Locked(exception);
        }

        ValidateOwnerFileAttributes(attributes);
        var information = new FileInfo(ownerPath);
        information.Refresh();
        return new OwnerFileFingerprint(
            information.Length,
            information.CreationTimeUtc,
            information.LastWriteTimeUtc,
            attributes);
    }

    /// <summary>
    /// 功能：计算 canonical lease 目录 mtime 相对 UTC now 的初始化年龄。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="leaseDirectory">缺失/incomplete owner 所在目录。</param>
    /// <param name="timeProvider">测试可替换的 UTC 时间源。</param>
    /// <returns>负值表示未来时间，必须 fail closed。</returns>
    private static TimeSpan GetDirectoryAge(string leaseDirectory, TimeProvider timeProvider)
    {
        RequireRealDirectory(leaseDirectory);
        try
        {
            return timeProvider.GetUtcNow() - new DateTimeOffset(Directory.GetLastWriteTimeUtc(leaseDirectory));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SessionWriterLeaseException.Locked(exception);
        }
    }

    /// <summary>
    /// 功能：计算 acquisition 总 deadline 的单调剩余时间。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="options">总超时和单调时间源。</param>
    /// <param name="startedAt">TimeProvider timestamp 起点。</param>
    /// <returns>可为负的剩余 TimeSpan。</returns>
    private static TimeSpan GetRemaining(WriterLeaseOptions options, long startedAt)
    {
        return options.AcquisitionTimeout - options.TimeProvider.GetElapsedTime(startedAt);
    }

    /// <summary>
    /// 功能：返回三个 TimeSpan 中的最小值，限制 grace retry 不越过任何 deadline。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="first">第一个候选。</param>
    /// <param name="second">第二个候选。</param>
    /// <param name="third">第三个候选。</param>
    /// <returns>最小候选。</returns>
    private static TimeSpan Min(TimeSpan first, TimeSpan second, TimeSpan third)
    {
        return first <= second
            ? first <= third ? first : third
            : second <= third ? second : third;
    }

    /// <summary>
    /// 功能：在 moved 二次验证失败时仅当 canonical 仍缺失才尝试恢复原目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="moved">已原子移动的 release sibling。</param>
    private void RestoreCanonicalOrThrow(string moved)
    {
        RestoreCanonicalOrThrow(sessionDirectory, moved);
    }

    /// <summary>
    /// 功能：在 moved 二次验证失败时仅当 canonical 仍缺失才尝试恢复原目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionDirectory">canonical lease 的父 Session 目录。</param>
    /// <param name="moved">已原子移动的 stale/release sibling。</param>
    private static void RestoreCanonicalOrThrow(string sessionDirectory, string moved)
    {
        var canonical = Path.Combine(sessionDirectory, LeaseDirectoryName);
        if (TryGetAttributes(canonical, out _))
        {
            throw SessionWriterLeaseException.CleanupFailed();
        }

        try
        {
            Directory.Move(moved, canonical);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SessionWriterLeaseException.CleanupFailed(exception);
        }
    }

    /// <summary>
    /// 功能：删除只由本 contender 原子赢得的 moved 目录，不递归跟随任何链接或子目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="moved">名称包含本轮随机 token 的已验证 sibling。</param>
    private static void RemoveMovedDirectory(string moved)
    {
        try
        {
            RequireRealDirectory(moved);
            foreach (var entry in Directory.EnumerateFileSystemEntries(moved))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                {
                    throw SessionWriterLeaseException.CleanupFailed();
                }

                File.Delete(entry);
            }

            RequireRealDirectory(moved);
            Directory.Delete(moved, recursive: false);
        }
        catch (SessionWriterLeaseException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SessionWriterLeaseException.CleanupFailed(exception);
        }
    }

    /// <summary>
    /// 功能：尽力删除仅含本轮随机 token 的 owner 临时文件，歧义时保留供 stale recovery。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="temporaryPath">本轮唯一临时文件路径。</param>
    private static void DeleteUniqueTemporary(string temporaryPath)
    {
        try
        {
            if (!TryGetAttributes(temporaryPath, out var attributes) ||
                (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                return;
            }

            File.Delete(temporaryPath);
        }
        catch (SessionWriterLeaseException)
        {
            // 安全清理为 best effort；无法证明时保留随机临时文件，不触碰 canonical owner。
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ = exception;
        }
    }

    /// <summary>
    /// 功能：判断异常是否属于 moved owner 身份重验证的安全失败集合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="exception">待分类异常。</param>
    /// <returns>必须保留 moved/canonical 数据并 fail closed 时返回 true。</returns>
    private static bool IsOwnerReadFailure(Exception exception)
    {
        return exception is OwnerMissingException or OwnerIncompleteException or LeaseChangedException or
            SessionWriterLeaseException or IOException or UnauthorizedAccessException or OperationCanceledException;
    }

    /// <summary>
    /// 功能：保存 strict owner 解析后的 exact bytes、token 与 literal loopback port。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="Bytes">原始 owner.json 字节。</param>
    /// <param name="Token">严格 64 位 lowercase token。</param>
    /// <param name="Port">严格 1..65535 端口。</param>
    private sealed record OwnerSnapshot(byte[] Bytes, string Token, int Port);

    /// <summary>
    /// 功能：保存 stale 原子移动前必须在 moved 目录重验证的候选字节。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="OwnerBytes">missing metadata 为 null；incomplete/valid 为 exact bytes。</param>
    private sealed record StaleCandidate(byte[]? OwnerBytes);

    /// <summary>
    /// 功能：保存 owner 路径读取前后的替换检测指纹。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="Length">文件长度。</param>
    /// <param name="CreationTimeUtc">创建时间。</param>
    /// <param name="LastWriteTimeUtc">最后写时间。</param>
    /// <param name="Attributes">路径属性。</param>
    private sealed record OwnerFileFingerprint(
        long Length,
        DateTime CreationTimeUtc,
        DateTime LastWriteTimeUtc,
        FileAttributes Attributes);

    /// <summary>
    /// 功能：表示 canonical lease 存在但 owner.json 尚未提交。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class OwnerMissingException : Exception
    {
        /// <summary>
        /// 功能：创建无底层异常的 owner metadata missing 标记。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        internal OwnerMissingException()
            : base("owner metadata is missing")
        {
        }

        /// <summary>
        /// 功能：创建保留路径消失根因的 owner metadata missing 标记。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="innerException">文件或父目录消失异常。</param>
        internal OwnerMissingException(Exception innerException)
            : base("owner metadata is missing", innerException)
        {
        }
    }

    /// <summary>
    /// 功能：保存可能由崩溃留下的 incomplete owner exact bytes。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class OwnerIncompleteException : Exception
    {
        /// <summary>
        /// 功能：创建不带 parser 根异常的 incomplete owner 状态。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="bytes">有界 incomplete 原始字节。</param>
        internal OwnerIncompleteException(byte[] bytes)
            : base("owner metadata is incomplete")
        {
            Bytes = bytes;
        }

        /// <summary>
        /// 功能：创建带 parser 根异常的 incomplete owner 状态。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="bytes">有界 incomplete 原始字节。</param>
        /// <param name="innerException">严格 JSON parser 异常。</param>
        internal OwnerIncompleteException(byte[] bytes, Exception innerException)
            : base("owner metadata is incomplete", innerException)
        {
            Bytes = bytes;
        }

        internal byte[] Bytes { get; }
    }

    /// <summary>
    /// 功能：表示 owner 路径在读取、移动或重验证期间发生身份变化。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class LeaseChangedException : Exception
    {
        /// <summary>
        /// 功能：创建无底层异常的 lease race 标记。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        internal LeaseChangedException()
            : base("writer lease changed during validation")
        {
        }

        /// <summary>
        /// 功能：创建保留本机 I/O 根因的 lease race 标记。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="innerException">路径消失或替换异常。</param>
        internal LeaseChangedException(Exception innerException)
            : base("writer lease changed during validation", innerException)
        {
        }
    }
}

/// <summary>
/// 功能：持有 literal IPv4 loopback listener，并持续接受后立即关闭 liveness probes。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class LoopbackWitness
{
    private readonly Socket listener;
    private readonly CancellationTokenSource stopSource;
    private readonly Task acceptTask;
    private bool closed;

    /// <summary>
    /// 功能：保存已 bind/listen 且 accept loop 已启动的 witness 资源。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="listener">只绑定 127.0.0.1 的 IPv4 listener。</param>
    /// <param name="port">OS 分配端口。</param>
    /// <param name="stopSource">有界停止信号。</param>
    private LoopbackWitness(Socket listener, int port, CancellationTokenSource stopSource)
    {
        this.listener = listener;
        this.stopSource = stopSource;
        Port = port;
        acceptTask = AcceptLoopAsync(listener, stopSource.Token);
    }

    /// <summary>
    /// 功能：取得 owner.json endpoint.port 使用的 OS 分配端口。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal int Port { get; }

    /// <summary>
    /// 功能：绑定 `127.0.0.1:0` 并启动不读取、不响应、不记录 peer 的 accept loop。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>已经进入 listen 状态的 witness。</returns>
    /// <remarks>不变量：永不绑定 wildcard、IPv6 或配置 host，也不使用代理。</remarks>
    /// <exception cref="SessionWriterLeaseException">socket/listen 无法建立时返回 writer_lock_unavailable。</exception>
    internal static LoopbackWitness Start()
    {
        Socket? listener = null;
        try
        {
            listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ExclusiveAddressUse = true,
            };
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(backlog: 64);
            if (listener.LocalEndPoint is not IPEndPoint endpoint ||
                !endpoint.Address.Equals(IPAddress.Loopback) ||
                endpoint.Port is < 1 or > 65_535)
            {
                throw new SocketException((int)SocketError.AddressNotAvailable);
            }

            var stopSource = new CancellationTokenSource();
            return new LoopbackWitness(listener, endpoint.Port, stopSource);
        }
        catch (Exception exception) when (exception is SocketException or UnauthorizedAccessException)
        {
            listener?.Dispose();
            throw SessionWriterLeaseException.Unavailable(exception);
        }
    }

    /// <summary>
    /// 功能：确认 listener handle、bind 状态与 accept loop 仍然存活。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <exception cref="SessionWriterLeaseException">资源关闭或 loop 意外结束时阻止继续写 Session。</exception>
    internal void EnsureOpen()
    {
        if (closed || listener.SafeHandle.IsClosed || !listener.IsBound || acceptTask.IsCompleted)
        {
            throw SessionWriterLeaseException.Unavailable();
        }
    }

    /// <summary>
    /// 功能：停止 accept loop 并关闭 liveness listener；重复调用保持幂等。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>accept loop 在一秒内完成后的 Task。</returns>
    /// <exception cref="SessionWriterLeaseException">后台 loop 无法有界停止时返回 cleanup failure。</exception>
    internal async Task CloseAsync()
    {
        if (closed)
        {
            return;
        }

        closed = true;
        stopSource.Cancel();
        listener.Dispose();
        try
        {
            await acceptTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw SessionWriterLeaseException.CleanupFailed(exception);
        }
        finally
        {
            stopSource.Dispose();
        }
    }

    /// <summary>
    /// 功能：持续接受并立即关闭 probe 连接，避免 backlog 被历史探测填满。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="listener">只绑定 literal loopback 的 listener。</param>
    /// <param name="cancellationToken">release 停止信号。</param>
    /// <returns>listener 关闭或停止请求后完成的 Task。</returns>
    private static async Task AcceptLoopAsync(Socket listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
        }
    }
}
