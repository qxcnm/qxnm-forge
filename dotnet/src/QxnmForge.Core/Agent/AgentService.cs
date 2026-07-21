using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using QxnmForge.Domain;
using QxnmForge.Provider;
using QxnmForge.Serialization;
using QxnmForge.Session;
using QxnmForge.Tools;
using QxnmForge.Executor;

namespace QxnmForge.Agent;

/// <summary>
/// 功能：表示已 append-before-ack 接受、但尚未必然完成的 run。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class AcceptedRun
{
    /// <summary>
    /// 功能：创建包含持久化上下文和独立取消源的 accepted run。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="runtime">会话运行时。</param>
    /// <param name="runId">会话内唯一 run ID。</param>
    /// <param name="inputMessageId">已 durable 的用户消息 ID。</param>
    /// <param name="provider">已验证 Provider 选择。</param>
    /// <param name="providerAdapter">已注册的原生 Provider 实现。</param>
    /// <param name="scenario">faux run 消费的场景；live Provider 为 null。</param>
    /// <param name="contextMessages">包含当前输入的 durable portable 消息历史。</param>
    /// <param name="agentProfile">可选的不可变 Agent Profile run 绑定。</param>
    /// <param name="interactiveApprovals">客户端是否声明并维持交互审批通道。</param>
    internal AcceptedRun(
        SessionRuntime runtime,
        string runId,
        string inputMessageId,
        ProviderSelection provider,
        IProvider providerAdapter,
        FauxScenario? scenario,
        IReadOnlyList<JsonElement> contextMessages,
        AgentProfileRunBinding? agentProfile,
        bool interactiveApprovals)
    {
        Runtime = runtime;
        RunId = runId;
        InputMessageId = inputMessageId;
        Provider = provider;
        ProviderAdapter = providerAdapter;
        Scenario = scenario;
        ContextMessages = contextMessages;
        AgentProfile = agentProfile;
        InteractiveApprovals = interactiveApprovals;
    }

    /// <summary>
    /// 功能：取得 accepted run 的会话运行时。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal SessionRuntime Runtime { get; }

    /// <summary>
    /// 功能：取得返回给客户端的 opaque run ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string RunId { get; }

    /// <summary>
    /// 功能：取得已持久化输入消息 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string InputMessageId { get; }

    /// <summary>
    /// 功能：取得本 run 的 Provider 选择。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public ProviderSelection Provider { get; }

    /// <summary>
    /// 功能：取得本 run 独占消费的 faux 场景。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal FauxScenario? Scenario { get; }

    /// <summary>
    /// 功能：取得本 run 独立调用的原生 Provider adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal IProvider ProviderAdapter { get; }

    /// <summary>
    /// 功能：取得从 durable journal 选定线性分支无损重建、并包含本次用户输入的 Provider 消息上下文。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <remarks>每个 JsonElement 生命周期独立；顺序与 portable message.appended 记录一致。</remarks>
    public IReadOnlyList<JsonElement> ContextMessages { get; }

    /// <summary>
    /// 功能：取得接受阶段冻结的可选 Agent Profile run 绑定。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public AgentProfileRunBinding? AgentProfile { get; }

    /// <summary>
    /// 功能：取得传播到 Provider await 边界的本地取消源。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal CancellationTokenSource Cancellation { get; } = new();

    /// <summary>
    /// 功能：标识取消 intent 是否已经 durable，支持幂等响应。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal bool CancellationRequested { get; set; }

    /// <summary>
    /// 功能：保存已在 session gate 内 durable 仲裁的唯一 run 终态；null 表示尚未终止。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal string? TerminalStatus { get; set; }

    /// <summary>
    /// 功能：指出本连接是否协商 interactiveApprovals；false 时危险操作默认拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal bool InteractiveApprovals { get; }

    /// <summary>
    /// 功能：保存尚未 resolved 的 approval waiter；仅在 SessionRuntime.StateGate 内访问。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal Dictionary<string, PendingApproval> PendingApprovals { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 功能：保存已使用/过期审批 ID，确保 duplicate response 不会重新授予权限。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal HashSet<string> ResolvedApprovalIds { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 功能：在唯一 run 终态 durable 且本地清理完成后唤醒 daemon 故障收尾。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal TaskCompletionSource TerminalCompletion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// 功能：实现独立 .NET Agent 文本循环、durable 状态机与异步事件流。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class AgentService
{
    private const long MaxArtifactBytes = 1_073_741_824;
    private static readonly TimeSpan DefaultApprovalTimeout = TimeSpan.FromMinutes(5);

    private readonly SessionRepository sessions;
    private readonly ProviderRegistry providers;
    private readonly ToolRegistry tools;
    private readonly TimeSpan approvalTimeout;
    private readonly AgentProfileService? profiles;

    /// <summary>
    /// 功能：绑定 session repository 与原生 faux Provider。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessions">负责 portable journal 的会话仓库。</param>
    /// <param name="fauxProvider">确定性离线 Provider。</param>
    public AgentService(SessionRepository sessions, FauxProvider fauxProvider)
        : this(sessions, new ProviderRegistry([fauxProvider]), new ToolRegistry(sessions.Workspace))
    {
    }

    /// <summary>
    /// 功能：绑定 session repository 与当前已配置的原生 Provider registry。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessions">负责 portable journal 的会话仓库。</param>
    /// <param name="providers">包含 faux 和可选 live family 的原生注册表。</param>
    public AgentService(SessionRepository sessions, ProviderRegistry providers)
        : this(sessions, providers, new ToolRegistry(sessions.Workspace))
    {
    }

    /// <summary>
    /// 功能：绑定 session、Provider registry 与当前 workspace 的独立原生工具 registry。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessions">负责 portable journal 的会话仓库。</param>
    /// <param name="providers">包含 faux 和可选 live family 的原生注册表。</param>
    /// <param name="tools">已绑定同一 workspace 的可执行工具注册表。</param>
    public AgentService(SessionRepository sessions, ProviderRegistry providers, ToolRegistry tools)
        : this(sessions, providers, tools, DefaultApprovalTimeout)
    {
    }

    /// <summary>
    /// 功能：绑定完整依赖并接受可信宿主注入的有界审批等待上限。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessions">负责 portable journal 的会话仓库。</param>
    /// <param name="providers">包含 faux 和可选 live family 的原生注册表。</param>
    /// <param name="tools">已绑定同一 workspace 的可执行工具注册表。</param>
    /// <param name="approvalTimeout">严格位于 1 毫秒至 1 小时的审批等待上限。</param>
    /// <param name="profiles">可选 production Agent Profile application service。</param>
    /// <remarks>不变量：协议客户端、模型和工具参数不能修改该宿主上限；构造不执行工具或访问网络。</remarks>
    /// <exception cref="ArgumentOutOfRangeException">timeout 为零、负数或超过一小时时抛出。</exception>
    public AgentService(
        SessionRepository sessions,
        ProviderRegistry providers,
        ToolRegistry tools,
        TimeSpan approvalTimeout,
        AgentProfileService? profiles = null)
    {
        if (approvalTimeout < TimeSpan.FromMilliseconds(1) || approvalTimeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(approvalTimeout));
        }

        this.sessions = sessions;
        this.providers = providers;
        this.tools = tools;
        this.approvalTimeout = approvalTimeout;
        this.profiles = profiles;
    }

    /// <summary>
    /// 功能：取得 initialize 应真实广告的已配置 Provider 实现。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public IReadOnlyList<IProvider> ConfiguredProviders => providers.Providers;

    /// <summary>
    /// 功能：取得 initialize 从同一 registry 模型快照派生的 Provider 级模型并集。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public IReadOnlyList<ProviderAdvertisement> ProviderAdvertisements => providers.Advertisements;

    /// <summary>
    /// 功能：取得 faux 与 identity-only 快照中可选 Provider 过滤后的公共模型描述。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerId">可选 Provider ID；未知合法身份返回空列表。</param>
    /// <returns>稳定三元排序的防御性 descriptor 数组。</returns>
    public IReadOnlyList<ModelDescriptor> ListModels(string? providerId = null)
    {
        return providers.ListModels(providerId);
    }

    /// <summary>
    /// 功能：取得 initialize 和 Provider 请求应真实广告的可执行工具名。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public IReadOnlyList<string> ConfiguredTools => tools.Names;

    /// <summary>
    /// 功能：取得仅在 startup self-test 成功后可由 initialize 广告的 hard-sandbox descriptor。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public HardSandboxCapability? HardSandboxCapability => tools.HardSandboxCapability;

    /// <summary>
    /// 功能：在 initialize 成功响应前传播工具边界配置错误并执行 hard-sandbox 启动自检。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="cancellationToken">daemon 初始化取消信号。</param>
    /// <returns>未启用或成功时为 null；失败时返回路径配置或 sandbox 的固定 portable error。</returns>
    /// <remarks>不变量：失败不广告能力且连接不会继续到 Session 创建或工具执行。</remarks>
    public Task<PortableError?> InitializeHardSandboxAsync(CancellationToken cancellationToken)
    {
        return tools.InitializeHardSandboxAsync(cancellationToken);
    }

    /// <summary>
    /// 功能：验证并接受 run，先 durable 追加输入消息和 run.accepted，再返回 runId。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标会话 ID。</param>
    /// <param name="input">至少包含一个文本块的 user 输入。</param>
    /// <param name="provider">必须匹配当前 registry 已配置的 Provider 与模型。</param>
    /// <param name="interactiveApprovals">客户端是否协商交互审批；缺失时危险操作默认拒绝。</param>
    /// <param name="cancellationToken">仅接受阶段的取消信号。</param>
    /// <returns>已 durable 接受、可立即响应客户端的 run。</returns>
    /// <remarks>不变量：同一 session 最多一个 active run；pending faux 场景恰好消费一次。</remarks>
    /// <exception cref="ArgumentException">输入或 Provider 选择无效。</exception>
    /// <exception cref="InvalidOperationException">会话 busy，或 faux 没有 pending 场景。</exception>
    /// <exception cref="IOException">任一 append/flush 失败，调用者不得确认 run。</exception>
    public Task<AcceptedRun> AcceptAsync(
        string sessionId,
        InputMessage input,
        ProviderSelection provider,
        bool interactiveApprovals,
        CancellationToken cancellationToken = default)
    {
        return AcceptAsync(
            sessionId,
            input,
            provider,
            agentProfile: null,
            interactiveApprovals,
            cancellationToken);
    }

    /// <summary>
    /// 功能：验证可选 Agent Profile 并在任何 Session append 前冻结其执行快照。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标会话 ID。</param>
    /// <param name="input">至少包含一个文本块的 user 输入。</param>
    /// <param name="provider">必须与 profile 及当前 registry 精确匹配的模型选择。</param>
    /// <param name="agentProfile">可选 profile ID/revision 引用。</param>
    /// <param name="interactiveApprovals">客户端是否协商交互审批。</param>
    /// <param name="cancellationToken">仅接受阶段的取消信号。</param>
    /// <returns>已 durable 接受且携带不可变 profile snapshot 的 run。</returns>
    /// <remarks>不变量：Profile 错误、route 不可用或输入错误时不创建 Session 记录。</remarks>
    /// <exception cref="AgentProfileException">Profile 服务缺失、引用、状态、模型或 route 无效。</exception>
    public async Task<AcceptedRun> AcceptAsync(
        string sessionId,
        InputMessage input,
        ProviderSelection provider,
        AgentProfileReference? agentProfile,
        bool interactiveApprovals,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(input);
        AgentProfileRunBinding? profileBinding = null;
        if (agentProfile is not null)
        {
            if (profiles is null)
            {
                throw AgentProfileException.Invalid();
            }

            profileBinding = await profiles.ResolveRunBindingAsync(
                agentProfile,
                provider,
                tools.Names,
                cancellationToken).ConfigureAwait(false);
        }

        IProvider providerAdapter;
        try
        {
            providerAdapter = providers.GetRequired(provider);
        }
        catch (ProviderUnavailableException) when (profileBinding is not null)
        {
            throw AgentProfileException.ModelUnavailable(profileBinding.Snapshot.Model);
        }

        if (profileBinding is null && provider.ApiFamily is null && providerAdapter.ApiFamily is not null)
        {
            provider = provider with { ApiFamily = providerAdapter.ApiFamily };
        }

        using var runtimeUse = await sessions.AcquireRuntimeUseAsync(
            sessionId,
            waitForStateGate: true,
            cancellationToken).ConfigureAwait(false);
        var runtime = runtimeUse.Runtime;
        if (runtime.ActiveRun is not null)
        {
            throw new InvalidOperationException("session is busy");
        }

        if (provider.Id == "faux" &&
            (runtime.PendingScenario is null || runtime.PendingScenarioRecordId is null))
        {
            throw new InvalidOperationException("faux scenario is not configured");
        }

        await ValidateProviderInputAsync(
            runtime.Journal,
            input,
            providerAdapter,
            cancellationToken).ConfigureAwait(false);
        await runtime.Journal.EnsureContinuationSupportedAsync(cancellationToken).ConfigureAwait(false);

        var runId = "run-" + Guid.NewGuid().ToString("N");
        var inputMessageId = "message-" + Guid.NewGuid().ToString("N");
        var userMessage = new UserMessage(inputMessageId, "user", input.Content, DateTimeOffset.UtcNow);
        await runtime.Journal.AppendAsync(
            "message.appended",
            new { Message = userMessage, RunId = runId },
            cancellationToken).ConfigureAwait(false);
        var acceptedData = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["runId"] = runId,
            ["inputMessageId"] = inputMessageId,
            ["provider"] = provider,
        };
        if (provider.Id == "faux")
        {
            acceptedData["fauxScenarioRecordId"] = runtime.PendingScenarioRecordId;
        }
        if (profileBinding is not null)
        {
            acceptedData["agentProfileSnapshot"] = profileBinding.Snapshot;
        }

        await runtime.Journal.AppendAsync(
            "run.accepted",
            acceptedData,
            cancellationToken).ConfigureAwait(false);

        var contextMessages = await runtime.Journal.GetContextMessagesAsync(cancellationToken).ConfigureAwait(false);
        var accepted = new AcceptedRun(
            runtime,
            runId,
            inputMessageId,
            provider,
            providerAdapter,
            runtime.PendingScenario,
            contextMessages,
            profileBinding,
            interactiveApprovals &&
            profileBinding?.Snapshot.DangerousActionMode != "deny");
        if (provider.Id == "faux")
        {
            runtime.PendingScenario = null;
            runtime.PendingScenarioRecordId = null;
        }
        runtime.ActiveRun = accepted;
        return accepted;
    }

    /// <summary>
    /// 功能：以无交互审批的安全默认值接受 run，保持现有原生 API 调用兼容。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">目标会话 ID。</param>
    /// <param name="input">至少包含一个文本块的 user 输入。</param>
    /// <param name="provider">已配置 Provider 与模型。</param>
    /// <param name="cancellationToken">仅接受阶段的取消信号。</param>
    /// <returns>已 durable 接受的 headless run。</returns>
    public Task<AcceptedRun> AcceptAsync(
        string sessionId,
        InputMessage input,
        ProviderSelection provider,
        CancellationToken cancellationToken = default)
    {
        return AcceptAsync(sessionId, input, provider, interactiveApprovals: false, cancellationToken);
    }

    /// <summary>
    /// 功能：启动 accepted run 的 durable Agent 循环并以 IAsyncEnumerable 返回已落盘事件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="acceptedRun">已通过 AcceptAsync durable 接受的 run。</param>
    /// <param name="cancellationToken">daemon 生命周期取消信号。</param>
    /// <returns>严格按状态机顺序产生的事件流。</returns>
    /// <remarks>不变量：每个 accepted run 最多一个 terminal event；所有 yield 事件此前已有 event.emitted。</remarks>
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AcceptedRun acceptedRun,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
        _ = ProduceAsync(acceptedRun, channel.Writer, cancellationToken);
        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    /// <summary>
    /// 功能：durable 记录首次取消 intent 并传播 CancellationTokenSource，重复调用保持幂等。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">run 所属 session。</param>
    /// <param name="runId">目标 run。</param>
    /// <param name="cancellationToken">控制请求持久化取消信号。</param>
    /// <returns>requested、alreadyRequested 或 terminal。</returns>
    /// <remarks>不变量：第一次成功响应前 intent 已落盘；terminal run 不产生新事件。</remarks>
    /// <exception cref="KeyNotFoundException">run 不属于该 session。</exception>
    /// <exception cref="IOException">intent 无法持久化。</exception>
    public async Task<string> RequestCancellationAsync(
        string sessionId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        using var runtimeUse = await sessions.AcquireRuntimeUseAsync(
            sessionId,
            waitForStateGate: true,
            cancellationToken).ConfigureAwait(false);
        var runtime = runtimeUse.Runtime;
        if (runtime.TerminalRunIds.Contains(runId))
        {
            return "terminal";
        }

        var run = runtime.ActiveRun;
        if (run is null || run.RunId != runId)
        {
            throw new KeyNotFoundException("run was not found");
        }

        if (run.CancellationRequested)
        {
            return "alreadyRequested";
        }

        await runtime.Journal.AppendAsync(
            "run.cancellation_requested",
            new { RunId = runId },
            cancellationToken).ConfigureAwait(false);
        run.CancellationRequested = true;
        run.Cancellation.Cancel();
        return "requested";
    }

    /// <summary>
    /// 功能：durable 解析一个 active approval，并在 append 成功后唤醒唯一等待工具调用。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">approval 所属会话。</param>
    /// <param name="runId">approval 所属 active run。</param>
    /// <param name="approvalId">单次审批 ID。</param>
    /// <param name="decision">只允许 allow_once 或 deny。</param>
    /// <param name="cancellationToken">控制请求持久化信号。</param>
    /// <returns>decision durable 后、等待 daemon flush success 的顺序屏障。</returns>
    /// <remarks>不变量：重复、过期、未知或其他 run 的 ID 绝不执行工具；客户端不能指定 resolutionSource。</remarks>
    /// <exception cref="ApprovalResponseException">decision 非法、审批未知/过期/重复或 run 已终止。</exception>
    internal async Task<ApprovalResponseReceipt> RespondApprovalAsync(
        string sessionId,
        string runId,
        string approvalId,
        ApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        ValidateApprovalDecision(decision);
        using var runtimeUse = await sessions.AcquireRuntimeUseAsync(
            sessionId,
            waitForStateGate: true,
            cancellationToken).ConfigureAwait(false);
        var runtime = runtimeUse.Runtime;
        var run = runtime.ActiveRun;
        if (run is null || run.RunId != runId)
        {
            throw CreateApprovalConflict("approval_run_inactive");
        }

        if (run.ResolvedApprovalIds.Contains(approvalId))
        {
            throw CreateApprovalConflict("approval_already_resolved");
        }

        if (!run.PendingApprovals.TryGetValue(approvalId, out var pending))
        {
            throw CreateApprovalConflict("approval_not_found");
        }

        if (DateTimeOffset.UtcNow >= pending.Request.ExpiresAt)
        {
            var timeoutDecision = new ApprovalDecision("deny", "approval expired");
            await AppendApprovalResolutionAsync(
                runtime,
                run,
                pending,
                timeoutDecision,
                "timeout",
                CancellationToken.None).ConfigureAwait(false);
            throw CreateApprovalConflict("approval_expired");
        }

        await AppendApprovalResolutionAsync(
            runtime,
            run,
            pending,
            decision,
            "client",
            cancellationToken,
            completeWaiter: false).ConfigureAwait(false);
        return new ApprovalResponseReceipt(run, pending, new ApprovalResolution(decision, "client"));
    }

    /// <summary>
    /// 功能：连接 EOF/断开时 durable 拒绝该连接 run 的所有 pending approvals。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="run">当前连接接受的 run。</param>
    /// <param name="resolutionSource">固定 disconnect 或 cancellation。</param>
    /// <returns>全部首次决议 append 并唤醒 waiter 后的 Task。</returns>
    internal static async Task DenyPendingApprovalsAsync(AcceptedRun run, string resolutionSource)
    {
        await run.Runtime.StateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            foreach (var pending in run.PendingApprovals.Values.ToArray())
            {
                await AppendApprovalResolutionAsync(
                    run.Runtime,
                    run,
                    pending,
                    new ApprovalDecision("deny", resolutionSource),
                    resolutionSource,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            run.Runtime.StateGate.Release();
        }
    }

    /// <summary>
    /// 功能：把 steer/follow-up 输入按 FIFO 语义 durable 追加到对应队列。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">run 所属 session。</param>
    /// <param name="runId">当前 active run。</param>
    /// <param name="queue">steering 或 follow_up。</param>
    /// <param name="input">待排队 user 输入。</param>
    /// <param name="cancellationToken">持久化取消信号。</param>
    /// <returns>durable 后的 queue item ID。</returns>
    /// <remarks>不变量：成功响应前 queue.appended 已落盘；本纵切面尚不在 Provider 间消费队列。</remarks>
    /// <exception cref="KeyNotFoundException">run 不是该会话 active run。</exception>
    /// <exception cref="ArgumentException">queue 或 input 无效。</exception>
    public async Task<string> AppendQueueAsync(
        string sessionId,
        string runId,
        string queue,
        InputMessage input,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(input);
        if (queue is not ("steering" or "follow_up"))
        {
            throw new ArgumentException("queue is invalid", nameof(queue));
        }

        using var runtimeUse = await sessions.AcquireRuntimeUseAsync(
            sessionId,
            waitForStateGate: true,
            cancellationToken).ConfigureAwait(false);
        var runtime = runtimeUse.Runtime;
        if (runtime.ActiveRun is null || runtime.ActiveRun.RunId != runId)
        {
            throw new KeyNotFoundException("run was not found");
        }

        var queueItemId = "queue-" + Guid.NewGuid().ToString("N");
        await runtime.Journal.AppendAsync(
            "queue.appended",
            new { RunId = runId, QueueItemId = queueItemId, Queue = queue, Input = input },
            cancellationToken).ConfigureAwait(false);
        return queueItemId;
    }

    /// <summary>
    /// 功能：在后台执行 Agent 状态机并始终完成事件 channel。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="run">已接受 run。</param>
    /// <param name="writer">单写者事件 channel。</param>
    /// <param name="daemonCancellation">daemon 生命周期取消信号。</param>
    /// <returns>状态机结束后的 Task。</returns>
    private async Task ProduceAsync(
        AcceptedRun run,
        ChannelWriter<AgentEvent> writer,
        CancellationToken daemonCancellation)
    {
        Exception? completionError = null;
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                run.Cancellation.Token,
                daemonCancellation);
            await ProduceCoreAsync(run, writer, linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            completionError = exception;
        }
        finally
        {
            writer.TryComplete(completionError);
        }
    }

    /// <summary>
    /// 功能：执行多 turn Agent 循环，串行处理 Provider 源顺序工具并按 append-before-observe 生成唯一终态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="run">已接受 run。</param>
    /// <param name="writer">事件 channel writer。</param>
    /// <param name="cancellationToken">合并后的 run/daemon 取消信号。</param>
    /// <returns>terminal 记录和事件持久化后的 Task。</returns>
    private async Task ProduceCoreAsync(
        AcceptedRun run,
        ChannelWriter<AgentEvent> writer,
        CancellationToken cancellationToken)
    {
        var journal = run.Runtime.Journal;
        var usage = Usage.Zero;
        var contextMessages = run.ContextMessages.Select(static message => message.Clone()).ToList();
        var observedToolCallIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await journal.AppendAsync("run.started", new { run.RunId }, cancellationToken).ConfigureAwait(false);
            await EmitAsync(journal, writer, run.RunId, null, "run.started", new { }, cancellationToken).ConfigureAwait(false);
            for (var providerTurnIndex = 0; providerTurnIndex <= 100; providerTurnIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var turnId = "turn-" + Guid.NewGuid().ToString("N");
                var assistantMessageId = "message-" + Guid.NewGuid().ToString("N");
                await journal.AppendAsync(
                    "turn.started",
                    new { run.RunId, TurnId = turnId, Attempt = 1 },
                    cancellationToken).ConfigureAwait(false);
                await EmitAsync(
                    journal,
                    writer,
                    run.RunId,
                    turnId,
                    "turn.started",
                    new { },
                    cancellationToken).ConfigureAwait(false);
                await EmitAsync(
                    journal,
                    writer,
                    run.RunId,
                    turnId,
                    "message.started",
                    new { MessageId = assistantMessageId, Role = "assistant" },
                    cancellationToken).ConfigureAwait(false);

                var turn = await ExecuteProviderTurnAsync(
                    run,
                    writer,
                    contextMessages,
                    SelectFauxTurnScenario(run, providerTurnIndex),
                    turnId,
                    assistantMessageId,
                    cancellationToken).ConfigureAwait(false);
                foreach (var toolCall in turn.ToolCalls)
                {
                    if (!observedToolCallIds.Add(toolCall.ToolCallId))
                    {
                        throw new ProviderOperationException(new PortableError(
                            -32005,
                            "provider reused a tool call identifier",
                            false,
                            new ErrorDetails("provider_protocol_error", ProviderId: run.Provider.Id)));
                    }
                }

                usage = AddUsage(usage, turn.Usage);
                contextMessages.Add(turn.Message.Clone());

                if (turn.ToolCalls.Count == 0)
                {
                    await PersistTerminalAsync(
                        run,
                        writer,
                        "completed",
                        null,
                        usage).ConfigureAwait(false);
                    return;
                }

                var toolResultCount = 0;
                var cancelled = false;
                foreach (var toolCall in turn.ToolCalls)
                {
                    cancelled = await ProcessToolCallAsync(
                        run,
                        writer,
                        contextMessages,
                        turnId,
                        toolCall,
                        cancelled || cancellationToken.IsCancellationRequested,
                        cancellationToken).ConfigureAwait(false);
                    toolResultCount++;
                    cancelled = cancelled || cancellationToken.IsCancellationRequested;
                }

                var turnFinishReason = cancelled ? "cancelled" : "tool_use";
                await journal.AppendAsync(
                    "turn.completed",
                    new { run.RunId, TurnId = turnId, FinishReason = turnFinishReason },
                    CancellationToken.None).ConfigureAwait(false);
                await EmitAsync(
                    journal,
                    writer,
                    run.RunId,
                    turnId,
                    "turn.completed",
                    new { FinishReason = turnFinishReason, ToolResultCount = toolResultCount },
                    CancellationToken.None).ConfigureAwait(false);
                if (cancelled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            throw new ProviderOperationException(new PortableError(
                -32009,
                "agent exceeded the provider turn limit",
                false,
                new ErrorDetails("turn_limit", Limit: 101, Observed: 101)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await PersistTerminalAsync(
                run,
                writer,
                "cancelled",
                null,
                usage).ConfigureAwait(false);
        }
        catch (ProviderOperationException exception)
        {
            await PersistTerminalAsync(
                run,
                writer,
                exception.TerminalStatus,
                exception.Error,
                usage).ConfigureAwait(false);
        }
        catch (Exception)
        {
            var error = new PortableError(
                -32603,
                "internal agent failure",
                false,
                new ErrorDetails("internal_error"));
            await PersistTerminalAsync(
                run,
                writer,
                "failed",
                error,
                usage).ConfigureAwait(false);
        }
        finally
        {
            await MarkTerminalAsync(run).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 功能：执行一个 Provider turn，收集全部工具调用并在任何终止路径关闭当前 attempt。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="run">当前 accepted run。</param>
    /// <param name="writer">事件 channel writer。</param>
    /// <param name="contextMessages">本轮完整 Provider 上下文。</param>
    /// <param name="fauxScenario">faux 当前 FIFO turn；live Provider 为 null。</param>
    /// <param name="turnId">当前 turn ID。</param>
    /// <param name="assistantMessageId">当前 assistant message ID。</param>
    /// <param name="cancellationToken">run 与 daemon 合并取消信号。</param>
    /// <returns>durable assistant 消息、源顺序工具调用、本轮用量和 finish reason。</returns>
    /// <remarks>不变量：正常返回前 message.appended 和 provider attempt completed 均已落盘。</remarks>
    private async Task<(
        JsonElement Message,
        IReadOnlyList<ProviderToolCall> ToolCalls,
        Usage Usage,
        string FinishReason)> ExecuteProviderTurnAsync(
        AcceptedRun run,
        ChannelWriter<AgentEvent> writer,
        IReadOnlyList<JsonElement> contextMessages,
        FauxScenario? fauxScenario,
        string turnId,
        string assistantMessageId,
        CancellationToken cancellationToken)
    {
        var journal = run.Runtime.Journal;
        var attempt = 1;
        var providerAttemptOpen = false;
        try
        {
            await journal.AppendAsync(
                "provider.attempt",
                new { run.RunId, TurnId = turnId, Attempt = attempt, Status = "started" },
                cancellationToken).ConfigureAwait(false);
            providerAttemptOpen = true;
            var text = new StringBuilder();
            var toolCalls = new List<ProviderToolCall>();
            var turnUsage = Usage.Zero;
            ProviderImageCompletionSignal? imageCompletion = null;
            var resolvedImages = await ResolveProviderImagesAsync(
                journal,
                contextMessages,
                run.ProviderAdapter,
                cancellationToken).ConfigureAwait(false);
            var providerRequest = new ProviderRequest(
                run.Provider,
                contextMessages.Select(static message => message.Clone()).ToArray(),
                tools.ProviderDefinitions(run.AgentProfile?.Snapshot.EffectiveToolIds),
                fauxScenario)
            {
                ResolvedImages = resolvedImages,
                SystemInstructions = run.AgentProfile?.SystemInstructions,
            };
            await foreach (var signal in run.ProviderAdapter.StreamAsync(
                               providerRequest,
                               cancellationToken).ConfigureAwait(false))
            {
                switch (signal)
                {
                    case ProviderTextSignal textSignal:
                        if (run.ProviderAdapter.ApiFamily == "openrouter-images" || imageCompletion is not null)
                        {
                            throw new ProviderOperationException(new PortableError(
                                -32005,
                                "provider returned an invalid mixed completion",
                                false,
                                new ErrorDetails("provider_protocol_error", ProviderId: run.Provider.Id)));
                        }

                        text.Append(textSignal.Text);
                        await EmitAsync(
                            journal,
                            writer,
                            run.RunId,
                            turnId,
                            "message.delta",
                            new { MessageId = assistantMessageId, Delta = new { Type = "text", textSignal.Text } },
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case ProviderToolCallSignal toolSignal:
                        if (imageCompletion is not null)
                        {
                            throw new ProviderOperationException(new PortableError(
                                -32005,
                                "provider returned an invalid mixed completion",
                                false,
                                new ErrorDetails("provider_protocol_error", ProviderId: run.Provider.Id)));
                        }

                        toolCalls.Add(toolSignal.ToolCall);
                        break;
                    case ProviderUsageSignal usageSignal:
                        turnUsage = usageSignal.Usage;
                        break;
                    case ProviderImageCompletionSignal completionSignal:
                        if (imageCompletion is not null || text.Length > 0 || toolCalls.Count > 0)
                        {
                            throw new ProviderOperationException(new PortableError(
                                -32005,
                                "provider returned an invalid mixed completion",
                                false,
                                new ErrorDetails("provider_protocol_error", ProviderId: run.Provider.Id)));
                        }

                        imageCompletion = completionSignal;
                        turnUsage = completionSignal.Usage;
                        break;
                    case ProviderRetrySignal retrySignal:
                        await journal.AppendAsync(
                            "provider.attempt",
                            new
                            {
                                run.RunId,
                                TurnId = turnId,
                                Attempt = attempt,
                                Status = "failed",
                                Error = retrySignal.Reason,
                                RetryAfterMs = retrySignal.DelayMs,
                            },
                            cancellationToken).ConfigureAwait(false);
                        providerAttemptOpen = false;
                        await EmitAsync(
                            journal,
                            writer,
                            run.RunId,
                            turnId,
                            "retry.scheduled",
                            new
                            {
                                retrySignal.Attempt,
                                retrySignal.DelayMs,
                                Reason = retrySignal.Reason,
                            },
                            cancellationToken).ConfigureAwait(false);
                        attempt = retrySignal.Attempt;
                        await journal.AppendAsync(
                            "provider.attempt",
                            new { run.RunId, TurnId = turnId, Attempt = attempt, Status = "started" },
                            cancellationToken).ConfigureAwait(false);
                        providerAttemptOpen = true;
                        break;
                }
            }

            var finishReason = toolCalls.Count == 0 ? "stop" : "tool_use";
            var content = new List<object>();
            if (imageCompletion is not null)
            {
                if (!string.IsNullOrEmpty(imageCompletion.Text))
                {
                    content.Add(new TextContent(imageCompletion.Text));
                }

                foreach (var image in imageCompletion.Images)
                {
                    var artifact = await SessionArtifactStore.PublishImageAsync(
                        journal.DirectoryPath,
                        image.MediaType,
                        image.Bytes,
                        MaxArtifactBytes,
                        cancellationToken).ConfigureAwait(false);
                    await journal.AppendAsync(
                        "artifact.created",
                        new { Artifact = artifact },
                        cancellationToken).ConfigureAwait(false);
                    content.Add(new ImageReferenceContent(artifact));
                }
            }
            else if (text.Length > 0)
            {
                content.Add(new TextContent(text.ToString()));
            }

            foreach (var toolCall in toolCalls)
            {
                content.Add(new
                {
                    Type = "tool_call",
                    toolCall.ToolCallId,
                    toolCall.Name,
                    Arguments = toolCall.Arguments.Clone(),
                });
            }

            var assistantMessage = JsonSerializer.SerializeToElement(
                new
                {
                    MessageId = assistantMessageId,
                    Role = "assistant",
                    Content = content,
                    Provider = run.Provider,
                    FinishReason = finishReason,
                    Usage = turnUsage,
                    Time = DateTimeOffset.UtcNow,
                },
                JsonDefaults.Options);
            await journal.AppendAsync(
                "message.appended",
                new { Message = assistantMessage, run.RunId, TurnId = turnId },
                cancellationToken).ConfigureAwait(false);
            await journal.AppendAsync(
                "provider.attempt",
                new { run.RunId, TurnId = turnId, Attempt = attempt, Status = "completed" },
                cancellationToken).ConfigureAwait(false);
            providerAttemptOpen = false;
            await EmitAsync(
                journal,
                writer,
                run.RunId,
                turnId,
                "message.completed",
                new { MessageId = assistantMessageId, FinishReason = finishReason },
                cancellationToken).ConfigureAwait(false);
            return (assistantMessage.Clone(), toolCalls, turnUsage, finishReason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (providerAttemptOpen)
            {
                await CloseProviderAttemptAsync(
                    journal,
                    run.RunId,
                    turnId,
                    attempt,
                    "cancelled",
                    null,
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        catch (ProviderOperationException exception)
        {
            if (providerAttemptOpen)
            {
                await CloseProviderAttemptAsync(
                    journal,
                    run.RunId,
                    turnId,
                    attempt,
                    exception.TerminalStatus,
                    exception.Error,
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        catch
        {
            if (providerAttemptOpen)
            {
                var error = new PortableError(
                    -32603,
                    "internal provider turn failure",
                    false,
                    new ErrorDetails("internal_error"));
                await CloseProviderAttemptAsync(
                    journal,
                    run.RunId,
                    turnId,
                    attempt,
                    "failed",
                    error,
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// 功能：验证、授权、执行并持久化一个工具调用及其 canonical tool message。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="run">当前 accepted run。</param>
    /// <param name="writer">事件 channel writer。</param>
    /// <param name="contextMessages">下一 Provider turn 将使用的可变上下文。</param>
    /// <param name="turnId">工具所属 turn。</param>
    /// <param name="toolCall">Provider 源顺序完整工具调用。</param>
    /// <param name="forceCancelled">此前调用已观察取消，当前调用只写 cancelled 结果且不预检资源。</param>
    /// <param name="cancellationToken">run 与 daemon 合并取消信号。</param>
    /// <returns>工具因 run cancellation 结束时为 true，否则为 false。</returns>
    /// <remarks>不变量：精确一个 intent；result 和 tool message 均先于 tool.completed。</remarks>
    private async Task<bool> ProcessToolCallAsync(
        AcceptedRun run,
        ChannelWriter<AgentEvent> writer,
        List<JsonElement> contextMessages,
        string turnId,
        ProviderToolCall toolCall,
        bool forceCancelled,
        CancellationToken cancellationToken)
    {
        PreparedToolCall? prepared = null;
        ToolOperationException? preparationFailure = null;
        var effectiveToolIds = run.AgentProfile?.Snapshot.EffectiveToolIds;
        var toolAllowed = effectiveToolIds is null ||
            effectiveToolIds.Contains(toolCall.Name, StringComparer.Ordinal);
        ToolDefinition? registeredDefinition = null;
        if (toolAllowed)
        {
            _ = tools.TryGetDefinition(toolCall.Name, out registeredDefinition);
        }
        if (!forceCancelled)
        {
            try
            {
                prepared = tools.Prepare(
                    toolCall.Name,
                    toolCall.Arguments,
                    toolCall.ToolCallId,
                    effectiveToolIds);
            }
            catch (ToolOperationException exception)
            {
                preparationFailure = exception;
            }
        }

        var idempotent = prepared?.Definition.Idempotent ?? registeredDefinition?.Idempotent ?? false;
        var operationHash = prepared?.OperationHash ?? ToolRegistry.HashUnprepared(toolCall.Name, toolCall.Arguments);
        var policy = prepared is null
            ? PolicyDecision.Deny
            : DefaultToolPolicy.Evaluate(
                run.InteractiveApprovals ? OperationMode.Interactive : OperationMode.Headless,
                prepared.Definition.Action,
                insideWorkspace: true);
        var intentStatus = forceCancelled
            ? "denied"
            : preparationFailure is not null
            ? "rejected"
            : policy switch
            {
                PolicyDecision.Allow => "prepared",
                PolicyDecision.Ask => "awaiting_approval",
                _ => "denied",
            };
        await run.Runtime.Journal.AppendAsync(
            "tool.intent",
            new
            {
                run.RunId,
                TurnId = turnId,
                toolCall.ToolCallId,
                toolCall.Name,
                Arguments = toolCall.Arguments.Clone(),
                Idempotent = idempotent,
                Status = intentStatus,
                OperationHash = operationHash,
            },
            CancellationToken.None).ConfigureAwait(false);
        await EmitAsync(
            run.Runtime.Journal,
            writer,
            run.RunId,
            turnId,
            "tool.requested",
            new
            {
                toolCall.ToolCallId,
                toolCall.Name,
                Arguments = toolCall.Arguments.Clone(),
            },
            CancellationToken.None).ConfigureAwait(false);

        if (forceCancelled || cancellationToken.IsCancellationRequested)
        {
            await FinalizeToolResultAsync(
                run,
                writer,
                contextMessages,
                turnId,
                toolCall,
                ToolRegistry.CancelledResult(),
                CancellationToken.None).ConfigureAwait(false);
            return true;
        }

        if (preparationFailure is not null)
        {
            await FinalizeToolResultAsync(
                run,
                writer,
                contextMessages,
                turnId,
                toolCall,
                ToolRegistry.FailureResult(preparationFailure),
                CancellationToken.None).ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested;
        }

        if (policy == PolicyDecision.Deny)
        {
            await FinalizeToolResultAsync(
                run,
                writer,
                contextMessages,
                turnId,
                toolCall,
                CreatePermissionDeniedResult(toolCall.Name),
                CancellationToken.None).ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested;
        }

        if (policy == PolicyDecision.Ask)
        {
            var approvalOutcome = await RequestApprovalAsync(
                run,
                writer,
                turnId,
                toolCall,
                prepared!,
                cancellationToken).ConfigureAwait(false);
            if (!approvalOutcome.ResponseDelivered)
            {
                var deliveryError = new PortableError(
                    -32005,
                    "approval response delivery failed",
                    false,
                    new ErrorDetails("approval_delivery_failed", ToolName: toolCall.Name));
                await FinalizeToolResultAsync(
                    run,
                    writer,
                    contextMessages,
                    turnId,
                    toolCall,
                    CreateApprovalDeliveryFailureResult(deliveryError),
                    CancellationToken.None).ConfigureAwait(false);
                throw new ProviderOperationException(deliveryError, "interrupted");
            }

            var resolution = approvalOutcome.Resolution;
            if (resolution.ResolutionSource == "cancellation" || cancellationToken.IsCancellationRequested)
            {
                await FinalizeToolResultAsync(
                    run,
                    writer,
                    contextMessages,
                    turnId,
                    toolCall,
                    ToolRegistry.CancelledResult(),
                    CancellationToken.None).ConfigureAwait(false);
                return true;
            }

            if (resolution.Decision.Choice != "allow_once")
            {
                await FinalizeToolResultAsync(
                    run,
                    writer,
                    contextMessages,
                    turnId,
                    toolCall,
                    CreatePermissionDeniedResult(toolCall.Name),
                    CancellationToken.None).ConfigureAwait(false);
                return cancellationToken.IsCancellationRequested;
            }
        }

        if (!await TryStartToolAsync(
                run,
                writer,
                turnId,
                toolCall,
                cancellationToken).ConfigureAwait(false))
        {
            await FinalizeToolResultAsync(
                run,
                writer,
                contextMessages,
                turnId,
                toolCall,
                ToolRegistry.CancelledResult(),
                CancellationToken.None).ConfigureAwait(false);
            return true;
        }

        PortableToolResult result;
        var cancelledDuringExecution = false;
        try
        {
            result = await tools.ExecuteAsync(prepared!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = ToolRegistry.CancelledResult();
            cancelledDuringExecution = true;
        }
        catch (ToolOperationException exception)
        {
            result = ToolRegistry.FailureResult(exception);
        }
        catch
        {
            result = CreateInternalToolFailure(toolCall.Name);
        }

        await FinalizeToolResultAsync(
            run,
            writer,
            contextMessages,
            turnId,
            toolCall,
            result,
            CancellationToken.None).ConfigureAwait(false);
        return cancelledDuringExecution || cancellationToken.IsCancellationRequested;
    }

    /// <summary>
    /// 功能：与 run/cancel 共用 session gate，原子复查取消并 durable 发送 tool.started 授权点。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="run">当前 accepted run。</param>
    /// <param name="writer">事件 channel writer。</param>
    /// <param name="turnId">工具所属 turn。</param>
    /// <param name="toolCall">即将启动的 Provider 工具调用。</param>
    /// <param name="cancellationToken">daemon/run 合并取消信号。</param>
    /// <returns>tool.started 先于任何取消 intent 获得 gate 时为 true；已取消或终止时为 false。</returns>
    /// <remarks>不变量：返回 true 时 tool.started 已 durable；返回后新取消只能终止执行，不能追溯撤销 started。</remarks>
    private static async Task<bool> TryStartToolAsync(
        AcceptedRun run,
        ChannelWriter<AgentEvent> writer,
        string turnId,
        ProviderToolCall toolCall,
        CancellationToken cancellationToken)
    {
        await run.Runtime.StateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (run.CancellationRequested ||
                run.TerminalStatus is not null ||
                cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            await EmitAsync(
                run.Runtime.Journal,
                writer,
                run.RunId,
                turnId,
                "tool.started",
                new { toolCall.ToolCallId, toolCall.Name },
                CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        finally
        {
            run.Runtime.StateGate.Release();
        }
    }

    /// <summary>
    /// 功能：durable 注册审批、等待 client 或生命周期决议，并在返回前发送 approval.resolved。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="run">当前 accepted run。</param>
    /// <param name="writer">事件 channel writer。</param>
    /// <param name="turnId">审批所属 turn。</param>
    /// <param name="toolCall">审批绑定的 Provider 工具调用。</param>
    /// <param name="prepared">已规范化且无副作用的工具执行计划。</param>
    /// <param name="cancellationToken">run 与 daemon 合并取消信号。</param>
    /// <returns>唯一 durable 决议及 client success frame 是否完成交付。</returns>
    /// <remarks>不变量：approval.requested 记录先于事件；未交付的 client 决议不会 emit resolved 或释放工具。</remarks>
    private async Task<ApprovalWaitOutcome> RequestApprovalAsync(
        AcceptedRun run,
        ChannelWriter<AgentEvent> writer,
        string turnId,
        ProviderToolCall toolCall,
        PreparedToolCall prepared,
        CancellationToken cancellationToken)
    {
        var request = new ApprovalRequest(
            "approval-" + Guid.NewGuid().ToString("N"),
            toolCall.ToolCallId,
            prepared.Definition.Name,
            prepared.Arguments.Clone(),
            prepared.OperationHash,
            GetApprovalRisk(prepared.Definition.Action),
            "该操作会修改工作区或启动本地进程，需要明确批准。",
            prepared.Resources,
            ["allow_once", "deny"],
            DateTimeOffset.UtcNow.Add(approvalTimeout));
        var pending = new PendingApproval(request);
        await run.Runtime.StateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (run.CancellationRequested ||
                run.TerminalStatus is not null ||
                cancellationToken.IsCancellationRequested)
            {
                return new ApprovalWaitOutcome(
                    new ApprovalResolution(
                        new ApprovalDecision("deny", "run cancelled"),
                        "cancellation"),
                    ResponseDelivered: true);
            }

            await run.Runtime.Journal.AppendAsync(
                "approval.requested",
                new { run.RunId, Approval = request },
                CancellationToken.None).ConfigureAwait(false);
            if (!run.PendingApprovals.TryAdd(request.ApprovalId, pending))
            {
                throw new InvalidOperationException("approval identifier collision");
            }

            await EmitAsync(
                run.Runtime.Journal,
                writer,
                run.RunId,
                turnId,
                "approval.requested",
                new { Approval = request },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            run.Runtime.StateGate.Release();
        }

        ApprovalWaitOutcome? outcome;
        try
        {
            var remaining = request.ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("approval expired");
            }

            outcome = await pending.Completion.Task.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var resolution = await ResolvePendingApprovalAsync(
                run,
                pending,
                new ApprovalDecision("deny", "approval expired"),
                "timeout").ConfigureAwait(false);
            outcome = resolution is null
                ? null
                : new ApprovalWaitOutcome(resolution, ResponseDelivered: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var resolution = await ResolvePendingApprovalAsync(
                run,
                pending,
                new ApprovalDecision("deny", "run cancelled"),
                "cancellation").ConfigureAwait(false);
            outcome = resolution is null
                ? null
                : new ApprovalWaitOutcome(resolution, ResponseDelivered: true);
        }

        outcome ??= await pending.Completion.Task.ConfigureAwait(false);
        if (!outcome.ResponseDelivered)
        {
            return outcome;
        }

        var releasedResolution = outcome.Resolution;
        await EmitAsync(
            run.Runtime.Journal,
            writer,
            run.RunId,
            turnId,
            "approval.resolved",
            new
            {
                request.ApprovalId,
                releasedResolution.Decision,
                releasedResolution.ResolutionSource,
            },
            CancellationToken.None).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// 功能：在 session gate 内为 timeout 或 cancellation 首次 durable 解析 pending approval。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="run">approval 所属 active run。</param>
    /// <param name="pending">原始 pending waiter。</param>
    /// <param name="decision">安全生命周期产生的 deny 决议。</param>
    /// <param name="resolutionSource">timeout 或 cancellation。</param>
    /// <returns>本方法完成首次决议时返回该决议；已由其他路径决议时返回 null。</returns>
    private static async Task<ApprovalResolution?> ResolvePendingApprovalAsync(
        AcceptedRun run,
        PendingApproval pending,
        ApprovalDecision decision,
        string resolutionSource)
    {
        await run.Runtime.StateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!run.PendingApprovals.TryGetValue(pending.Request.ApprovalId, out var current) ||
                !ReferenceEquals(current, pending))
            {
                return null;
            }

            var resolution = new ApprovalResolution(decision, resolutionSource);
            await AppendApprovalResolutionAsync(
                run.Runtime,
                run,
                pending,
                decision,
                resolutionSource,
                CancellationToken.None).ConfigureAwait(false);
            return resolution;
        }
        finally
        {
            run.Runtime.StateGate.Release();
        }
    }

    /// <summary>
    /// 功能：持久化工具结果和 canonical tool message，再发送包含同一结果的 tool.completed。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="run">当前 accepted run。</param>
    /// <param name="writer">事件 channel writer。</param>
    /// <param name="contextMessages">下一 Provider turn 上下文。</param>
    /// <param name="turnId">工具所属 turn。</param>
    /// <param name="toolCall">Provider 工具调用。</param>
    /// <param name="result">最终 portable 结果。</param>
    /// <param name="cancellationToken">收尾持久化信号。</param>
    /// <returns>tool.completed 可观察后的 Task。</returns>
    private static async Task FinalizeToolResultAsync(
        AcceptedRun run,
        ChannelWriter<AgentEvent> writer,
        List<JsonElement> contextMessages,
        string turnId,
        ProviderToolCall toolCall,
        PortableToolResult result,
        CancellationToken cancellationToken)
    {
        await run.Runtime.Journal.AppendAsync(
            "tool.result",
            new
            {
                run.RunId,
                TurnId = turnId,
                toolCall.ToolCallId,
                Result = result,
                OutcomeKnown = true,
            },
            cancellationToken).ConfigureAwait(false);
        var toolMessage = JsonSerializer.SerializeToElement(
            new
            {
                MessageId = "message-" + Guid.NewGuid().ToString("N"),
                Role = "tool",
                toolCall.ToolCallId,
                ToolName = toolCall.Name,
                result.Content,
                result.IsError,
                Time = DateTimeOffset.UtcNow,
            },
            JsonDefaults.Options);
        await run.Runtime.Journal.AppendAsync(
            "message.appended",
            new { Message = toolMessage, run.RunId, TurnId = turnId },
            cancellationToken).ConfigureAwait(false);
        contextMessages.Add(toolMessage.Clone());
        await EmitAsync(
            run.Runtime.Journal,
            writer,
            run.RunId,
            turnId,
            "tool.completed",
            new { toolCall.ToolCallId, Result = result },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：选择 faux 首轮场景或 FIFO continuation，并拒绝在工具 turn 后隐式复用旧步骤。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="run">当前 accepted run。</param>
    /// <param name="providerTurnIndex">从零开始的 Provider turn 索引。</param>
    /// <returns>faux 当前 turn 场景；live Provider 返回 null。</returns>
    /// <exception cref="ProviderOperationException">faux 场景缺失或 continuation 已耗尽。</exception>
    private static FauxScenario? SelectFauxTurnScenario(AcceptedRun run, int providerTurnIndex)
    {
        if (run.Provider.Id != "faux")
        {
            return null;
        }

        var scenario = run.Scenario ?? throw new ProviderOperationException(new PortableError(
            -32602,
            "faux scenario is missing",
            false,
            new ErrorDetails("invalid_params", ProviderId: "faux")));
        if (providerTurnIndex == 0)
        {
            return scenario;
        }

        var continuationIndex = providerTurnIndex - 1;
        if (scenario.Continuations is null || continuationIndex >= scenario.Continuations.Count)
        {
            throw new ProviderOperationException(new PortableError(
                -32005,
                "faux continuation was exhausted",
                false,
                new ErrorDetails("faux_continuation_exhausted", ProviderId: "faux")));
        }

        var continuation = scenario.Continuations[continuationIndex];
        return new FauxScenario(
            scenario.SchemaVersion,
            scenario.Name,
            scenario.Seed,
            continuation.Steps,
            continuation.Usage,
            continuation.ExpectedContext);
    }

    /// <summary>
    /// 功能：把本轮 token 用量安全累加到 run 总用量，并拒绝负数或超出 JSON safe integer。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="current">此前累计用量。</param>
    /// <param name="next">当前 Provider turn 用量。</param>
    /// <returns>三个字段分别累加后的新用量。</returns>
    private static Usage AddUsage(Usage current, Usage next)
    {
        return new Usage(
            AddTokenCount(current.InputTokens, next.InputTokens),
            AddTokenCount(current.OutputTokens, next.OutputTokens),
            AddTokenCount(current.TotalTokens, next.TotalTokens));
    }

    /// <summary>
    /// 功能：累加两个非负 token 计数并保持语言中立 JSON safe integer 边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="left">已累计计数。</param>
    /// <param name="right">新增计数。</param>
    /// <returns>未越界的计数和。</returns>
    /// <exception cref="ProviderOperationException">任一计数为负或加和越界。</exception>
    private static long AddTokenCount(long left, long right)
    {
        const long maxSafeInteger = 9_007_199_254_740_991;
        if (left < 0 || right < 0 || left > maxSafeInteger - right)
        {
            throw new ProviderOperationException(new PortableError(
                -32005,
                "provider usage was outside portable limits",
                false,
                new ErrorDetails("provider_protocol_error")));
        }

        return left + right;
    }

    /// <summary>
    /// 功能：把危险工具类别映射为仅供审批展示的稳定风险等级。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="action">已注册工具操作类别。</param>
    /// <returns>medium、high 或 critical。</returns>
    private static string GetApprovalRisk(ToolAction action)
    {
        return action switch
        {
            ToolAction.FileWrite or ToolAction.FileEdit => "high",
            ToolAction.ProcessExec or ToolAction.ShellExec or ToolAction.TerminalOpen => "critical",
            _ => "medium",
        };
    }

    /// <summary>
    /// 功能：创建 headless 默认拒绝或审批 deny 使用的结构化工具结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="toolName">被拒绝的规范工具名。</param>
    /// <returns>terminationReason=denied 且 kind=permission_denied 的结果。</returns>
    private static PortableToolResult CreatePermissionDeniedResult(string toolName)
    {
        var error = new PortableError(
            -32003,
            "tool execution was denied by policy",
            false,
            new ErrorDetails("permission_denied", ToolName: toolName));
        return new PortableToolResult([new TextContent(error.Message)], true, "denied", error);
    }

    /// <summary>
    /// 功能：把 approval success frame 未交付转换为 outcome-known 且未执行的工具错误结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="error">不含 host I/O 文本的固定 delivery failure 错误。</param>
    /// <returns>terminationReason=internal_error 的 portable 工具结果。</returns>
    /// <remarks>不变量：该结果不代表 executor 已启动，也不改变唯一 durable client resolution。</remarks>
    private static PortableToolResult CreateApprovalDeliveryFailureResult(PortableError error)
    {
        return new PortableToolResult(
            [new TextContent(error.Message)],
            true,
            "internal_error",
            error);
    }

    /// <summary>
    /// 功能：把未预期 host 异常转换为不泄露异常文本的结构化工具结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="toolName">失败的规范工具名。</param>
    /// <returns>terminationReason=internal_error 的安全结果。</returns>
    private static PortableToolResult CreateInternalToolFailure(string toolName)
    {
        var error = new PortableError(
            -32603,
            "internal tool execution failure",
            false,
            new ErrorDetails("internal_error", ToolName: toolName));
        return new PortableToolResult([new TextContent(error.Message)], true, "internal_error", error);
    }

    /// <summary>
    /// 功能：在 run 终态之前关闭当前 Provider attempt，并仅为失败/中断保存脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="journal">run journal。</param>
    /// <param name="runId">run ID。</param>
    /// <param name="turnId">当前 turn ID。</param>
    /// <param name="attempt">当前 attempt 序号。</param>
    /// <param name="status">failed、interrupted 或 cancelled。</param>
    /// <param name="error">失败/中断时可持久化的结构化错误。</param>
    /// <param name="cancellationToken">清理持久化信号。</param>
    /// <returns>attempt 终态 durable 后的 append receipt。</returns>
    private static Task<JournalAppendReceipt> CloseProviderAttemptAsync(
        PortableSessionJournal journal,
        string runId,
        string turnId,
        int attempt,
        string status,
        PortableError? error,
        CancellationToken cancellationToken)
    {
        return journal.AppendAsync(
            "provider.attempt",
            new { RunId = runId, TurnId = turnId, Attempt = attempt, Status = status, Error = error },
            cancellationToken);
    }

    /// <summary>
    /// 功能：在持有 session state gate 时先 append approval.resolved，再更新单次 waiter 状态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="runtime">approval 会话运行时。</param>
    /// <param name="run">active run。</param>
    /// <param name="pending">待决议 approval。</param>
    /// <param name="decision">最终 allow_once/deny。</param>
    /// <param name="resolutionSource">client、timeout、cancellation 或 disconnect。</param>
    /// <param name="cancellationToken">持久化取消信号。</param>
    /// <param name="completeWaiter">是否立即唤醒 Agent；client 响应需等 success flush 后再唤醒。</param>
    /// <returns>记录和 waiter 状态均完成后的 Task。</returns>
    private static async Task AppendApprovalResolutionAsync(
        SessionRuntime runtime,
        AcceptedRun run,
        PendingApproval pending,
        ApprovalDecision decision,
        string resolutionSource,
        CancellationToken cancellationToken,
        bool completeWaiter = true)
    {
        await runtime.Journal.AppendAsync(
            "approval.resolved",
            new
            {
                run.RunId,
                pending.Request.ApprovalId,
                Decision = decision,
                ResolutionSource = resolutionSource,
            },
            cancellationToken).ConfigureAwait(false);
        run.PendingApprovals.Remove(pending.Request.ApprovalId);
        run.ResolvedApprovalIds.Add(pending.Request.ApprovalId);
        if (completeWaiter)
        {
            pending.Completion.TrySetResult(new ApprovalWaitOutcome(
                new ApprovalResolution(decision, resolutionSource),
                ResponseDelivered: true));
        }
    }

    /// <summary>
    /// 功能：验证客户端审批选择和可选说明边界，不解析说明文本决定权限。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="decision">客户端 decision。</param>
    /// <exception cref="ApprovalResponseException">choice 或 reason 无效。</exception>
    private static void ValidateApprovalDecision(ApprovalDecision decision)
    {
        if (decision.Choice is not ("allow_once" or "deny") || decision.Reason?.Length > 4096)
        {
            throw new ApprovalResponseException(new PortableError(
                -32602,
                "approval decision is invalid",
                false,
                new ErrorDetails("invalid_params", Field: "decision")));
        }
    }

    /// <summary>
    /// 功能：创建 unknown、duplicate、expired 或 inactive approval 的结构化 conflict。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">稳定 details.kind。</param>
    /// <returns>retryable=false 的 approval response 异常。</returns>
    private static ApprovalResponseException CreateApprovalConflict(string kind)
    {
        return new ApprovalResponseException(new PortableError(
            -32010,
            "approval is unavailable or already resolved",
            false,
            new ErrorDetails(kind)));
    }

    /// <summary>
    /// 功能：先 durable 写 event.emitted，再把 exact event 交给异步消费者。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="journal">事件所属 journal。</param>
    /// <param name="writer">事件 channel writer。</param>
    /// <param name="runId">run ID。</param>
    /// <param name="turnId">可选 turn ID。</param>
    /// <param name="type">事件类型。</param>
    /// <param name="data">事件数据。</param>
    /// <param name="cancellationToken">持久化取消信号。</param>
    /// <returns>channel 接受事件后的 Task。</returns>
    private static async Task EmitAsync(
        PortableSessionJournal journal,
        ChannelWriter<AgentEvent> writer,
        string runId,
        string? turnId,
        string type,
        object data,
        CancellationToken cancellationToken)
    {
        var portableEvent = await journal.AppendEventAsync(
            runId,
            turnId,
            type,
            data,
            cancellationToken).ConfigureAwait(false);
        await writer.WriteAsync(portableEvent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：在 session gate 内仲裁并持久化唯一 terminal state，再发送不可被 run cancellation 打断的终态事件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="run">要终止的 accepted run。</param>
    /// <param name="writer">事件 channel writer。</param>
    /// <param name="status">terminal 状态。</param>
    /// <param name="error">failed/interrupted 的 portable error。</param>
    /// <param name="usage">截至失败或取消时已知的规范化用量。</param>
    /// <returns>terminal event 可观察后的 Task。</returns>
    /// <remarks>不变量：run/cancel 与 terminal append 共享 StateGate；先取得 gate 的状态转换获胜且不会追加第二终态。</remarks>
    private static async Task PersistTerminalAsync(
        AcceptedRun run,
        ChannelWriter<AgentEvent> writer,
        string status,
        PortableError? error,
        Usage usage)
    {
        string? effectiveStatus = null;
        PortableError? effectiveError = null;
        await run.Runtime.StateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (run.TerminalStatus is not null)
            {
                return;
            }

            effectiveStatus = run.CancellationRequested ? "cancelled" : status;
            effectiveError = effectiveStatus == "cancelled" ? null : error;
            if (effectiveStatus == "cancelled")
            {
                await run.Runtime.Journal.AppendAsync(
                    "run.terminal",
                    new { run.RunId, Status = effectiveStatus, Usage = usage },
                    CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await run.Runtime.Journal.AppendAsync(
                    "run.terminal",
                    new { run.RunId, Status = effectiveStatus, Error = effectiveError, Usage = usage },
                    CancellationToken.None).ConfigureAwait(false);
            }

            run.TerminalStatus = effectiveStatus;
            run.Runtime.TerminalRunIds.Add(run.RunId);
        }
        finally
        {
            run.Runtime.StateGate.Release();
        }

        var eventType = effectiveStatus == "cancelled" ? "run.cancelled" : "run." + effectiveStatus;
        var eventData = effectiveStatus == "cancelled"
            ? new { Status = effectiveStatus, Usage = usage }
            : (object)new { Status = effectiveStatus, Error = effectiveError, Usage = usage };
        try
        {
            await EmitAsync(
                run.Runtime.Journal,
                writer,
                run.RunId,
                null,
                eventType,
                eventData,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await run.Runtime.StateGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(run.Runtime.ActiveRun, run))
                {
                    run.Runtime.ActiveRun = null;
                }
            }
            finally
            {
                run.Runtime.StateGate.Release();
            }
        }
    }

    /// <summary>
    /// 功能：在 terminal 事件处理结束后释放 run 专属取消资源；active claim 已由终态仲裁释放。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="run">刚完成的 run。</param>
    /// <returns>状态更新完成的 Task。</returns>
    private static async Task MarkTerminalAsync(AcceptedRun run)
    {
        await run.Runtime.StateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            run.Cancellation.Dispose();
        }
        finally
        {
            run.Runtime.StateGate.Release();
            run.TerminalCompletion.TrySetResult();
        }
    }

    /// <summary>
    /// 功能：按已选择 Provider family 验证文字上限，并在接受 run 前复核每个输入 image_ref。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="journal">输入所属 Session journal。</param>
    /// <param name="input">尚未追加的 user 消息，因此现有 artifact.created 必然 earlier。</param>
    /// <param name="provider">已经过 registry route 选择的 adapter。</param>
    /// <param name="cancellationToken">no-follow artifact 读取取消信号。</param>
    /// <returns>全部输入可安全进入 Provider 请求时完成。</returns>
    /// <remarks>不变量：非图像 family 不接受 image_ref；校验失败发生在用户消息和 run.accepted append 前。</remarks>
    /// <exception cref="ArgumentException">family、数量、文字、引用、文件、hash、MIME 或魔数无效。</exception>
    private static async Task ValidateProviderInputAsync(
        PortableSessionJournal journal,
        InputMessage input,
        IProvider provider,
        CancellationToken cancellationToken)
    {
        var imageContent = input.Content.OfType<ImageReferenceContent>().ToArray();
        if (provider.ApiFamily != "openrouter-images")
        {
            if (imageContent.Length > 0)
            {
                throw new ArgumentException("selected provider does not accept image_ref", nameof(input));
            }

            return;
        }

        long textBytes = 0;
        foreach (var text in input.Content.OfType<TextContent>())
        {
            textBytes = AddBounded(textBytes, Encoding.UTF8.GetByteCount(text.Text!));
        }

        if (textBytes > OpenRouterImagesProvider.MaxTextBytes ||
            imageContent.Length > OpenRouterImagesProvider.MaxInputImages)
        {
            throw new ArgumentException("OpenRouter image input exceeded a limit", nameof(input));
        }

        long imageBytes = 0;
        var validated = new Dictionary<string, ArtifactReference>(StringComparer.Ordinal);
        foreach (var image in imageContent)
        {
            var reference = image.Artifact!;
            imageBytes = AddBounded(imageBytes, reference.ByteLength);
            if (imageBytes > OpenRouterImagesProvider.MaxInputImageBytes)
            {
                throw new ArgumentException("OpenRouter image input exceeded a limit", nameof(input));
            }

            if (validated.TryGetValue(reference.ArtifactId, out var existing))
            {
                if (!ArtifactCoreEquals(existing, reference))
                {
                    throw new ArgumentException("input image artifact is invalid", nameof(input));
                }

                continue;
            }

            validated.Add(reference.ArtifactId, reference);

            try
            {
                _ = await journal.ReadImageArtifactAsync(
                    reference,
                    OpenRouterImagesProvider.MaxInputImageBytes,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ArtifactValidationException)
            {
                throw new ArgumentException("input image artifact is invalid", nameof(input));
            }
        }
    }

    /// <summary>
    /// 功能：为图像 family 从 selected context 收集、去重并重新复核全部 image_ref 输入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="journal">当前 run 的 portable journal。</param>
    /// <param name="messages">本轮将发送的完整 selected context。</param>
    /// <param name="provider">已选择 Provider adapter。</param>
    /// <param name="cancellationToken">artifact 文件读取取消信号。</param>
    /// <returns>按首次引用顺序排列、每个 artifact ID 唯一的已验证输入。</returns>
    /// <remarks>不变量：仅图像 family读取 artifact；bytes 不进入 message、journal、event 或日志。</remarks>
    /// <exception cref="ProviderOperationException">引用数量/累计大小、元数据或文件绑定无效。</exception>
    private static async Task<IReadOnlyList<ProviderResolvedImage>> ResolveProviderImagesAsync(
        PortableSessionJournal journal,
        IReadOnlyList<JsonElement> messages,
        IProvider provider,
        CancellationToken cancellationToken)
    {
        if (provider.ApiFamily != "openrouter-images")
        {
            return [];
        }

        var references = new List<ArtifactReference>();
        long totalBytes = 0;
        foreach (var message in messages)
        {
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object ||
                    !block.TryGetProperty("type", out var type) ||
                    type.ValueKind != JsonValueKind.String ||
                    type.GetString() != "image_ref")
                {
                    continue;
                }

                var reference = ParseContextArtifactReference(block);
                references.Add(reference);
                totalBytes = AddBounded(totalBytes, reference.ByteLength);
                if (references.Count > OpenRouterImagesProvider.MaxInputImages ||
                    totalBytes > OpenRouterImagesProvider.MaxInputImageBytes)
                {
                    throw ImageInputFailure("provider_input_limit", provider.Id);
                }
            }
        }

        var resolved = new List<ProviderResolvedImage>();
        var byId = new Dictionary<string, ArtifactReference>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            if (byId.TryGetValue(reference.ArtifactId, out var existing))
            {
                if (!ArtifactCoreEquals(existing, reference))
                {
                    throw ImageInputFailure("provider_input_artifact_invalid", provider.Id);
                }

                continue;
            }

            byId.Add(reference.ArtifactId, reference);
            try
            {
                var bytes = await journal.ReadImageArtifactAsync(
                    reference,
                    OpenRouterImagesProvider.MaxInputImageBytes,
                    cancellationToken).ConfigureAwait(false);
                resolved.Add(new ProviderResolvedImage(reference, bytes));
            }
            catch (ArtifactValidationException)
            {
                throw ImageInputFailure("provider_input_artifact_invalid", provider.Id);
            }
        }

        return resolved;
    }

    /// <summary>
    /// 功能：从 durable message 的 image_ref block 解析四个 artifact 内容绑定核心字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="block">selected Session context 中的 image_ref。</param>
    /// <returns>不含路径和字节的 portable 引用。</returns>
    /// <exception cref="ProviderOperationException">artifact 对象缺失、类型、长度或安全语法无效。</exception>
    private static ArtifactReference ParseContextArtifactReference(JsonElement block)
    {
        if (!block.TryGetProperty("artifact", out var artifact) ||
            artifact.ValueKind != JsonValueKind.Object ||
            !artifact.TryGetProperty("artifactId", out var id) ||
            id.ValueKind != JsonValueKind.String ||
            !artifact.TryGetProperty("mediaType", out var media) ||
            media.ValueKind != JsonValueKind.String ||
            !artifact.TryGetProperty("byteLength", out var length) ||
            !length.TryGetInt64(out var byteLength) ||
            !artifact.TryGetProperty("sha256", out var hash) ||
            hash.ValueKind != JsonValueKind.String)
        {
            throw ImageInputFailure("provider_input_artifact_invalid");
        }

        var reference = new ArtifactReference(
            id.GetString()!,
            media.GetString()!,
            byteLength,
            hash.GetString()!);
        try
        {
            ImageArtifactValidation.ValidateReference(reference, OpenRouterImagesProvider.MaxInputImageBytes);
        }
        catch (ArgumentException)
        {
            throw ImageInputFailure("provider_input_artifact_invalid");
        }

        return reference;
    }

    /// <summary>
    /// 功能：构造不回显 ID、路径、URL、hash 或字节的图像输入 Provider 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">稳定 details.kind。</param>
    /// <param name="providerId">可选公共 Provider ID。</param>
    /// <returns>retryable=false 的安全 ProviderOperationException。</returns>
    private static ProviderOperationException ImageInputFailure(string kind, string? providerId = null)
    {
        return new ProviderOperationException(new PortableError(
            -32005,
            "provider image input is invalid",
            false,
            new ErrorDetails(kind, ProviderId: providerId)));
    }

    /// <summary>
    /// 功能：比较两个 image_ref 的 artifact 内容绑定核心字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="left">首次引用。</param>
    /// <param name="right">重复引用。</param>
    /// <returns>ID、MIME、长度和 SHA-256 全部一致时为 true。</returns>
    private static bool ArtifactCoreEquals(ArtifactReference left, ArtifactReference right)
    {
        return string.Equals(left.ArtifactId, right.ArtifactId, StringComparison.Ordinal) &&
            string.Equals(left.MediaType, right.MediaType, StringComparison.Ordinal) &&
            left.ByteLength == right.ByteLength &&
            string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：以饱和语义累加非负资源计数，避免恶意长度造成整数回绕。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="left">当前非负计数。</param>
    /// <param name="right">新增计数。</param>
    /// <returns>未溢出的和；负值或溢出时为 long.MaxValue。</returns>
    private static long AddBounded(long left, long right)
    {
        return right < 0 || left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    /// <summary>
    /// 功能：验证 portable v0.1 用户输入的角色、数量和 text/image_ref 领域形状。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="input">待接受输入。</param>
    /// <exception cref="ArgumentException">角色、数量、块类型、必需字段或空引用无效。</exception>
    private static void ValidateInput(InputMessage input)
    {
        if (input.Role != "user" ||
            input.Content.Count is < 1 or > 128 ||
            input.Content.Any(static item => item is null) ||
            input.Content.Any(static item => item switch
            {
                TextContent text => text.Type != "text" || text.Text is null || text.Artifact is not null,
                ImageReferenceContent image => image.Type != "image_ref" || image.Text is not null || image.Artifact is null,
                _ => true,
            }))
        {
            throw new ArgumentException("input message is invalid", nameof(input));
        }
    }
}
