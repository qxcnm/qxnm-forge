using System.Collections.Concurrent;
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
/// 功能：按 session ID 原生管理独占 journal，不借助其他语言运行时。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class SessionRepository : IAsyncDisposable
{
    private readonly string sessionsRoot;
    private readonly string workspace;
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
    public async Task<SessionRuntime> GetAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var lazy = sessions.GetOrAdd(
            sessionId,
            id => new Lazy<Task<SessionRuntime>>(
                () => OpenRuntimeAsync(id, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (lazy.IsValueCreated && lazy.Value.IsFaulted)
            {
                sessions.TryRemove(new KeyValuePair<string, Lazy<Task<SessionRuntime>>>(sessionId, lazy));
            }

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
        var runtime = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await runtime.StateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
        finally
        {
            runtime.StateGate.Release();
        }
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
        var runtime = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await runtime.Journal.GetSnapshotAsync(afterSeq, cancellationToken).ConfigureAwait(false);
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
        var runtime = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!await runtime.StateGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new SessionMutationException(-32004, true, "session_busy");
        }

        try
        {
            if (runtime.ActiveRun is not null)
            {
                throw new SessionMutationException(-32004, true, "session_busy");
            }

            return await runtime.Journal.SelectBranchAsync(
                expectedHeadRecordId,
                targetLeafRecordId,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            runtime.StateGate.Release();
        }
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
        var runtime = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!await runtime.StateGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new SessionMutationException(-32004, true, "session_busy");
        }

        try
        {
            if (runtime.ActiveRun is not null)
            {
                throw new SessionMutationException(-32004, true, "session_busy");
            }

            return await runtime.Journal.CompactAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            runtime.StateGate.Release();
        }
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
        var journal = await PortableSessionJournal.OpenAsync(
            sessionsRoot,
            sessionId,
            workspace,
            cancellationToken).ConfigureAwait(false);
        return new SessionRuntime(journal);
    }
}
