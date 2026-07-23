using System.Collections.Concurrent;
using System.Text.Json;
using QxnmForge.Agent;
using QxnmForge.Domain;

namespace QxnmForge.Session;

/// <summary>
/// 功能：保存单会话的 journal、faux 配置和运行互斥状态。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class SessionRuntime
{
    /// <summary>
    /// 功能：创建一个由独占 portable journal 支撑的会话运行时。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="journal">保持 writer lock 的会话 journal。</param>
    internal SessionRuntime(PortableSessionJournal journal)
    {
        Journal = journal;
        foreach (var runId in journal.RestoredTerminalRunIds)
        {
            TerminalRunIds.Add(runId);
        }
    }

    /// <summary>
    /// 功能：取得该会话唯一的 portable journal writer。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public PortableSessionJournal Journal { get; }

    /// <summary>
    /// 功能：取得保护配置、active run 与 terminal 集合的异步互斥器。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal SemaphoreSlim StateGate { get; } = new(1, 1);

    /// <summary>
    /// 功能：保存下一次 run 将消费的 faux 场景。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal FauxScenario? PendingScenario { get; set; }

    /// <summary>
    /// 功能：保存 pending faux.configured 的 journal record ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal string? PendingScenarioRecordId { get; set; }

    /// <summary>
    /// 功能：保存 v0.1 单会话唯一 active run。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal AcceptedRun? ActiveRun { get; set; }

    /// <summary>
    /// 功能：记住本进程已完成的 run，以支持幂等 cancel terminal 响应。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal HashSet<string> TerminalRunIds { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// 功能：在单个 Session 生命周期 mutation 期间阻止 repository 重新打开同一 writer。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class SessionLifecycleReservation : IDisposable
{
    private readonly SessionRepository repository;
    private readonly string sessionId;
    private bool disposed;

    /// <summary>
    /// 功能：创建由 repository 独占登记的生命周期 reservation。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="repository">负责释放登记的 repository。</param>
    /// <param name="sessionId">被阻止重新打开的 Session ID。</param>
    internal SessionLifecycleReservation(SessionRepository repository, string sessionId)
    {
        this.repository = repository;
        this.sessionId = sessionId;
    }

    /// <summary>
    /// 功能：释放 reservation，使后续显式请求可以重新打开或创建该 Session。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        repository.ReleaseLifecycleReservation(sessionId);
    }
}

/// <summary>
/// 功能：在一次 repository 或 Agent 操作期间持有 SessionRuntime 的 StateGate 使用权。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class SessionRuntimeUse : IDisposable
{
    private bool disposed;

    /// <summary>
    /// 功能：包装已取得 StateGate 的 runtime。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="runtime">StateGate 已由调用方取得的 runtime。</param>
    internal SessionRuntimeUse(SessionRuntime runtime)
    {
        Runtime = runtime;
    }

    /// <summary>
    /// 功能：取得在本 lease 释放前不会被生命周期服务 evict 的 runtime。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal SessionRuntime Runtime { get; }

    /// <summary>
    /// 功能：释放 runtime StateGate，使后续操作或生命周期 reservation 可以继续。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Runtime.StateGate.Release();
    }
}

/// <summary>
/// 功能：按 session ID 原生管理独占 journal，不借助其他语言运行时。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class SessionRepository : IAsyncDisposable
{
    private const int MaximumTrackedSessionIds = 16_384;
    private readonly string sessionsRoot;
    private readonly string workspace;
    private readonly ConcurrentDictionary<string, object> lifecycleGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> lifecycleReservations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<SessionRuntime>>> sessions =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 功能：绑定 session 根目录和规范工作区。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionsRoot">所有 session 目录的本地根路径。</param>
    /// <param name="workspace">必须存在的工作区。</param>
    public SessionRepository(string sessionsRoot, string workspace)
    {
        this.sessionsRoot = Path.GetFullPath(sessionsRoot);
        this.workspace = Path.GetFullPath(workspace);
    }

    /// <summary>
    /// 功能：取得创建 Session header 与工具边界共同使用的规范工作区绝对路径。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string Workspace => workspace;

    /// <summary>
    /// 功能：取得只存放 Session 目录的规范根，供同进程生命周期服务绑定同一边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal string SessionsRoot => sessionsRoot;

    /// <summary>
    /// 功能：取得或原子创建会话运行时；创建路径会先 durable 写 header。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">opaque 会话 ID。</param>
    /// <param name="cancellationToken">打开操作取消信号。</param>
    /// <returns>进程内共享的单会话运行时。</returns>
    /// <remarks>不变量：同一 repository 中每个 ID 只有一个 journal writer。</remarks>
    /// <exception cref="IOException">跨进程 lock 获取失败。</exception>
    /// <exception cref="JournalCorruptException">已有 journal 损坏。</exception>
    internal async Task<SessionRuntime> GetAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var lifecycleGate = GetLifecycleGate(sessionId);
        Lazy<Task<SessionRuntime>> lazy;
        lock (lifecycleGate)
        {
            if (lifecycleReservations.ContainsKey(sessionId))
            {
                throw new SessionMutationException(-32004, true, "session_busy");
            }

            lazy = sessions.GetOrAdd(
                sessionId,
                id => new Lazy<Task<SessionRuntime>>(
                    () => OpenRuntimeAsync(id, CancellationToken.None),
                    LazyThreadSafetyMode.ExecutionAndPublication));
        }

        try
        {
            var runtime = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (lifecycleGate)
            {
                if (lifecycleReservations.ContainsKey(sessionId))
                {
                    throw new SessionMutationException(-32004, true, "session_busy");
                }
            }

            return runtime;
        }
        catch
        {
            if (lazy.IsValueCreated && lazy.Value.IsFaulted)
            {
                lock (lifecycleGate)
                {
                    sessions.TryRemove(new KeyValuePair<string, Lazy<Task<SessionRuntime>>>(sessionId, lazy));
                }
            }

            throw;
        }
    }

    /// <summary>
    /// 功能：取得 runtime 与 StateGate 的原子使用 lease，并在 reservation 竞争后再次确认所有权。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标 Session ID。</param>
    /// <param name="waitForStateGate">true 时等待普通串行操作；false 时 busy 立即失败。</param>
    /// <param name="cancellationToken">打开与等待 StateGate 的取消信号。</param>
    /// <returns>释放前 runtime 不会被生命周期服务 evict 的使用 lease。</returns>
    /// <exception cref="SessionMutationException">存在生命周期 reservation 或非等待模式未取得 gate。</exception>
    internal async Task<SessionRuntimeUse> AcquireRuntimeUseAsync(
        string sessionId,
        bool waitForStateGate,
        CancellationToken cancellationToken = default)
    {
        var runtime = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var acquired = waitForStateGate
            ? await runtime.StateGate.WaitAsync(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false)
            : await runtime.StateGate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        if (!acquired)
        {
            throw new SessionMutationException(-32004, true, "session_busy");
        }

        try
        {
            var lifecycleGate = GetLifecycleGate(sessionId);
            lock (lifecycleGate)
            {
                if (lifecycleReservations.ContainsKey(sessionId) ||
                    !sessions.TryGetValue(sessionId, out var lazy) ||
                    !lazy.IsValueCreated ||
                    !lazy.Value.IsCompletedSuccessfully ||
                    !ReferenceEquals(lazy.Value.Result, runtime))
                {
                    throw new SessionMutationException(-32004, true, "session_busy");
                }
            }

            return new SessionRuntimeUse(runtime);
        }
        catch
        {
            runtime.StateGate.Release();
            throw;
        }
    }

    /// <summary>
    /// 功能：durable 追加 faux.configured 后，把场景设为下一 run 的一次性输入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标会话 ID。</param>
    /// <param name="scenario">已验证 faux 场景。</param>
    /// <param name="cancellationToken">持久化取消信号。</param>
    /// <returns>规范要求的 faux:name 场景 ID。</returns>
    /// <remarks>不变量：journal flush 完成前绝不更新可观察 pending 场景或返回成功。</remarks>
    /// <exception cref="InvalidOperationException">会话已有 active run。</exception>
    /// <exception cref="IOException">持久化失败。</exception>
    public async Task<string> ConfigureFauxAsync(
        string sessionId,
        FauxScenario scenario,
        CancellationToken cancellationToken = default)
    {
        using var runtimeUse = await AcquireRuntimeUseAsync(
            sessionId,
            waitForStateGate: true,
            cancellationToken).ConfigureAwait(false);
        var runtime = runtimeUse.Runtime;
        if (runtime.ActiveRun is not null)
        {
            throw new InvalidOperationException("session is busy");
        }

        var scenarioId = "faux:" + scenario.Name;
        var receipt = await runtime.Journal.AppendAsync(
            "faux.configured",
            new { ScenarioId = scenarioId, Scenario = scenario },
            cancellationToken).ConfigureAwait(false);
        runtime.PendingScenario = scenario;
        runtime.PendingScenarioRecordId = receipt.RecordId;
        return scenarioId;
    }

    /// <summary>
    /// 功能：在静止 Session 中 durable 发布客户端输入图片及其 artifact.created 记录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">opaque Session ID；不存在时先创建 portable journal。</param>
    /// <param name="mediaType">PNG、JPEG、WebP 或 GIF 的规范 MIME。</param>
    /// <param name="bytes">已由协议边界严格解码且不超过 512 KiB 的完整图片。</param>
    /// <param name="cancellationToken">打开、发布和 journal flush 取消信号。</param>
    /// <returns>文件与 artifact.created 均 durable 后的不含路径引用。</returns>
    /// <remarks>不变量：与 run 接受共享 StateGate；活动 Session 拒绝发布；append 失败留下的未引用文件可安全回收。</remarks>
    /// <exception cref="SessionMutationException">Session 生命周期变更或正在运行。</exception>
    /// <exception cref="ArtifactValidationException">MIME、大小或魔数无效。</exception>
    /// <exception cref="IOException">文件或 journal durable 发布失败。</exception>
    internal async Task<ArtifactReference> PublishInputImageAsync(
        string sessionId,
        string mediaType,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        using var runtimeUse = await AcquireRuntimeUseAsync(
            sessionId,
            waitForStateGate: true,
            cancellationToken).ConfigureAwait(false);
        var runtime = runtimeUse.Runtime;
        if (runtime.ActiveRun is not null)
        {
            throw new SessionMutationException(-32004, true, "session_busy");
        }

        var artifact = await SessionArtifactStore.PublishImageAsync(
            runtime.Journal.DirectoryPath,
            mediaType,
            bytes,
            524_288,
            cancellationToken).ConfigureAwait(false);
        await runtime.Journal.AppendAsync(
            "artifact.created",
            new { Artifact = artifact },
            cancellationToken).ConfigureAwait(false);
        return artifact;
    }

    /// <summary>
    /// 功能：从指定 Session 当前 selected chain 按 ID 读取并完整验证一个 durable 图片 artifact。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标 opaque Session ID。</param>
    /// <param name="artifactId">journal 已发布的 opaque artifact ID。</param>
    /// <param name="maximumBytes">单图片读取上限。</param>
    /// <param name="cancellationToken">打开 Session、等待状态门禁与读取文件的取消信号。</param>
    /// <returns>完整 portable 引用和已验证字节；不含任何主机路径。</returns>
    /// <remarks>不变量：StateGate 与 journal append gate 共同冻结 selected chain；文件只由 journal 自持目录和安全 ID 派生。</remarks>
    /// <exception cref="ArtifactValidationException">归属、引用、文件身份或内容无效。</exception>
    /// <exception cref="JournalCorruptException">Session journal 基础结构损坏。</exception>
    /// <exception cref="JournalIncompatibleException">selected tree 不能安全解释。</exception>
    internal async Task<(ArtifactReference Artifact, byte[] Bytes)> ReadImageArtifactByIdAsync(
        string sessionId,
        string artifactId,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        using var runtimeUse = await AcquireRuntimeUseAsync(
            sessionId,
            waitForStateGate: true,
            cancellationToken).ConfigureAwait(false);
        return await runtimeUse.Runtime.Journal.ReadImageArtifactByIdAsync(
            artifactId,
            maximumBytes,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：在指定 active run 内 durable 发布 computer 工具生成的 PNG artifact。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">工具调用所属 opaque Session ID。</param>
    /// <param name="runId">必须仍为该 Session 唯一 active run 的 ID。</param>
    /// <param name="bytes">已在内存完成编码且不超过 32 MiB 的 PNG。</param>
    /// <param name="cancellationToken">发布与 journal flush 取消信号。</param>
    /// <returns>文件和 artifact.created 均 durable 后的不含路径引用。</returns>
    /// <remarks>不变量：与 lifecycle/run 状态共享 StateGate；只接受未请求取消且未终止的当前 active run，不能向其他 Session 注入 artifact。</remarks>
    /// <exception cref="SessionMutationException">run 已结束、被替换或 Session 生命周期变更。</exception>
    /// <exception cref="ArtifactValidationException">PNG 大小或魔数非法。</exception>
    internal async Task<ArtifactReference> PublishToolScreenshotAsync(
        string sessionId,
        string runId,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        using var runtimeUse = await AcquireRuntimeUseAsync(
            sessionId,
            waitForStateGate: true,
            cancellationToken).ConfigureAwait(false);
        var runtime = runtimeUse.Runtime;
        if (runtime.ActiveRun is null ||
            !string.Equals(runtime.ActiveRun.RunId, runId, StringComparison.Ordinal) ||
            runtime.ActiveRun.CancellationRequested ||
            runtime.ActiveRun.TerminalStatus is not null)
        {
            throw new SessionMutationException(-32004, true, "session_run_changed");
        }

        var publishedArtifact = await SessionArtifactStore.PublishImageAsync(
            runtime.Journal.DirectoryPath,
            "image/png",
            bytes,
            32 * 1024 * 1024,
            cancellationToken).ConfigureAwait(false);
        var computerMetadata = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["source"] = "desktop_capture",
            ["runId"] = runId,
            ["sensitivity"] = "desktop_sensitive",
            ["retention"] = "session_lifecycle",
        };
        var artifact = publishedArtifact with
        {
            Extensions = JsonSerializer.SerializeToElement(
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["org.agentprotocol.computer"] = computerMetadata,
                }),
        };
        await runtime.Journal.AppendAsync(
            "artifact.created",
            new
            {
                Artifact = artifact,
                Extensions = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["org.agentprotocol.computer"] = computerMetadata,
                },
            },
            cancellationToken).ConfigureAwait(false);
        return artifact;
    }

    /// <summary>
    /// 功能：取得 session/get 使用的一致 durable 投影，并按事件序号应用 afterSeq 过滤。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标 opaque session ID。</param>
    /// <param name="afterSeq">仅返回 event.seq 大于该值的事件；messages 始终完整。</param>
    /// <param name="cancellationToken">打开 journal 和等待投影锁的取消信号。</param>
    /// <returns>最新事件序号、真实 active run、完整消息和过滤事件。</returns>
    /// <exception cref="JournalIncompatibleException">journal 含当前实现不能安全解释的分支或压缩。</exception>
    public async Task<PortableSessionSnapshot> GetSnapshotAsync(
        string sessionId,
        long afterSeq = 0,
        CancellationToken cancellationToken = default)
    {
        using var runtimeUse = await AcquireRuntimeUseAsync(
            sessionId,
            waitForStateGate: true,
            cancellationToken).ConfigureAwait(false);
        return await runtimeUse.Runtime.Journal.GetSnapshotAsync(
            afterSeq,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：为 archive、restore 或 delete 建立 reservation，并安全释放已打开但静止的 writer。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标 Session ID。</param>
    /// <param name="cancellationToken">等待正在打开的 runtime 时的取消信号。</param>
    /// <returns>成功时返回必须释放的 reservation；active run、pending faux 或并发使用时返回 null。</returns>
    /// <remarks>不变量：reservation 建立到释放期间 GetAsync 对同一 ID fail closed；writer 释放前已从 repository 精确移除。</remarks>
    internal async Task<SessionLifecycleReservation?> TryReserveLifecycleMutationAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var lifecycleGate = GetLifecycleGate(sessionId);
        Lazy<Task<SessionRuntime>>? lazy;
        lock (lifecycleGate)
        {
            if (!lifecycleReservations.TryAdd(sessionId, 0))
            {
                return null;
            }

            sessions.TryGetValue(sessionId, out lazy);
        }

        var reservation = new SessionLifecycleReservation(this, sessionId);
        var reservationTransferred = false;
        SessionRuntime? runtime = null;
        var gateAcquired = false;
        try
        {
            if (lazy is null)
            {
                reservationTransferred = true;
                return reservation;
            }

            try
            {
                runtime = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or JournalCorruptException or JournalIncompatibleException)
            {
                lock (lifecycleGate)
                {
                    sessions.TryRemove(new KeyValuePair<string, Lazy<Task<SessionRuntime>>>(sessionId, lazy));
                }

                reservationTransferred = true;
                return reservation;
            }

            gateAcquired = await runtime.StateGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!gateAcquired || runtime.ActiveRun is not null || runtime.PendingScenario is not null)
            {
                return null;
            }

            lock (lifecycleGate)
            {
                if (!sessions.TryRemove(
                        new KeyValuePair<string, Lazy<Task<SessionRuntime>>>(sessionId, lazy)))
                {
                    return null;
                }
            }

            await runtime.Journal.DisposeAsync().ConfigureAwait(false);
            runtime.StateGate.Release();
            gateAcquired = false;
            runtime.StateGate.Dispose();
            reservationTransferred = true;
            return reservation;
        }
        finally
        {
            if (gateAcquired && runtime is not null)
            {
                runtime.StateGate.Release();
            }

            if (!reservationTransferred)
            {
                reservation.Dispose();
            }
        }
    }

    /// <summary>
    /// 功能：非排队地取得 Session mutation 权限，并以 expected-head CAS durable 选择历史 branch target。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标 opaque Session ID。</param>
    /// <param name="expectedHeadRecordId">调用者观察到的 selected head。</param>
    /// <param name="targetLeafRecordId">准备选择的 earlier quiescent record。</param>
    /// <param name="cancellationToken">打开 Session、竞争 gate 与 durable append 的取消信号。</param>
    /// <returns>只有 branch.selected durable 后才完成的 selection 回执。</returns>
    /// <remarks>不变量：journal 完整打开先于 busy 判断；StateGate 不排队，active run 或并发 mutation 立即返回 session_busy。</remarks>
    /// <exception cref="SessionMutationException">Session busy、head stale、目标未知或目标非静止。</exception>
    /// <exception cref="JournalCorruptException">journal 基础结构损坏。</exception>
    /// <exception cref="JournalIncompatibleException">journal tree/compaction 语义不兼容。</exception>
    public async Task<BranchSelectionReceipt> SelectBranchAsync(
        string sessionId,
        string expectedHeadRecordId,
        string targetLeafRecordId,
        CancellationToken cancellationToken = default)
    {
        using var runtimeUse = await AcquireRuntimeUseAsync(
            sessionId,
            waitForStateGate: false,
            cancellationToken).ConfigureAwait(false);
        var runtime = runtimeUse.Runtime;
        if (runtime.ActiveRun is not null)
        {
            throw new SessionMutationException(-32004, true, "session_busy");
        }

        return await runtime.Journal.SelectBranchAsync(
            expectedHeadRecordId,
            targetLeafRecordId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：非排队地取得 Session mutation 权限，并以 expected-head CAS durable 写入 compaction pair。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标 opaque Session ID。</param>
    /// <param name="command">已解析的 summary、retained 边界、Provider、usage、token 和策略。</param>
    /// <param name="cancellationToken">打开 Session、竞争 gate 与 durable pair 写入的取消信号。</param>
    /// <returns>只有 context.compacted durable 后才完成的 compaction 回执。</returns>
    /// <remarks>不变量：active run 或并发 mutation 不等待而失败；StateGate 覆盖 journal 内 CAS 与两条 append。</remarks>
    /// <exception cref="SessionMutationException">Session busy、head stale、边界或 token 不变量无效。</exception>
    /// <exception cref="JournalCorruptException">journal 基础结构损坏。</exception>
    /// <exception cref="JournalIncompatibleException">journal tree/compaction 语义不兼容。</exception>
    public async Task<SessionCompactionReceipt> CompactAsync(
        string sessionId,
        SessionCompactionCommand command,
        CancellationToken cancellationToken = default)
    {
        using var runtimeUse = await AcquireRuntimeUseAsync(
            sessionId,
            waitForStateGate: false,
            cancellationToken).ConfigureAwait(false);
        var runtime = runtimeUse.Runtime;
        if (runtime.ActiveRun is not null)
        {
            throw new SessionMutationException(-32004, true, "session_busy");
        }

        return await runtime.Journal.CompactAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：释放所有成功创建的 session journal 与 writer lock。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>所有句柄释放完毕的异步操作。</returns>
    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in sessions.Values)
        {
            if (!lazy.IsValueCreated)
            {
                continue;
            }

            SessionRuntime? runtime = null;
            try
            {
                runtime = await lazy.Value.ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or JournalCorruptException or JournalIncompatibleException)
            {
                continue;
            }

            runtime.StateGate.Dispose();
            await runtime.Journal.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 功能：打开 journal 并包装为会话运行时。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">opaque 会话 ID。</param>
    /// <param name="cancellationToken">打开取消信号。</param>
    /// <returns>新运行时。</returns>
    private async Task<SessionRuntime> OpenRuntimeAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var pendingTombstone = Path.Combine(sessionsRoot, ".session-tombstones", sessionId);
        if (Directory.Exists(pendingTombstone) || File.Exists(pendingTombstone))
        {
            throw new IOException("session deletion is pending");
        }

        var journal = await PortableSessionJournal.OpenAsync(
            sessionsRoot,
            sessionId,
            workspace,
            cancellationToken).ConfigureAwait(false);
        return new SessionRuntime(journal);
    }

    /// <summary>
    /// 功能：取得用于原子协调 GetAsync 与生命周期 reservation 的稳定 per-Session gate。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标 Session ID。</param>
    /// <returns>repository 生命周期内稳定的同步 gate。</returns>
    private object GetLifecycleGate(string sessionId)
    {
        if (lifecycleGates.TryGetValue(sessionId, out var existing))
        {
            return existing;
        }

        if (lifecycleGates.Count >= MaximumTrackedSessionIds)
        {
            throw new IOException("session repository capacity was exceeded");
        }

        return lifecycleGates.GetOrAdd(sessionId, static _ => new object());
    }

    /// <summary>
    /// 功能：释放生命周期 reservation，使同一 Session ID 可再次进入 GetAsync。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">已登记 reservation 的 Session ID。</param>
    internal void ReleaseLifecycleReservation(string sessionId)
    {
        var lifecycleGate = GetLifecycleGate(sessionId);
        lock (lifecycleGate)
        {
            lifecycleReservations.TryRemove(sessionId, out _);
        }
    }
}
