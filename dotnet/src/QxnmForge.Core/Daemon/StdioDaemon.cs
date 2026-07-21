using System.Collections.Concurrent;
using System.Text.Json;
using QxnmForge.Agent;
using QxnmForge.Domain;
using QxnmForge.Protocol;
using QxnmForge.Provider;
using QxnmForge.Serialization;
using QxnmForge.Session;

namespace QxnmForge.Daemon;

/// <summary>
/// 功能：通过 UTF-8 NDJSON/stdin-stdout 提供 JSON-RPC 2.0 headless/interactive Agent 协议。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class StdioDaemon
{
    public const int MaxFrameBytes = 1_048_576;
    public const int MaxEventBytes = 262_144;
    public const long MaxArtifactBytes = 1_073_741_824;

    private readonly SessionRepository sessions;
    private readonly AgentService agent;
    private readonly AgentProfileService? profiles;
    private readonly ProviderCatalogService providerCatalog;
    private readonly CustomProviderConnectionService? providerConnections;
    private readonly SessionLifecycleService? sessionLifecycle;
    private readonly bool conformanceMode;
    private readonly ConcurrentBag<Task> activeTasks = [];

    /// <summary>
    /// 功能：创建绑定独立 .NET session/agent 实现的 stdio daemon。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessions">portable session repository。</param>
    /// <param name="agent">原生 .NET Agent 服务。</param>
    /// <param name="conformanceMode">是否允许 faux/configure 测试方法。</param>
    /// <param name="profiles">成功完成数据库 bootstrap 时注入的 production Profile 服务。</param>
    /// <param name="providerCatalog">不读取 credential 或可执行状态的冻结 Provider 配置模板服务。</param>
    /// <param name="providerConnections">工作区外 Provider 连接与凭据 application service。</param>
    /// <param name="sessionLifecycle">真实 Session 摘要、归档与安全删除服务。</param>
    /// <remarks>不变量：Provider catalog 只投影冻结配置建议，不能提升任何 route 的可执行状态。</remarks>
    /// <exception cref="ProviderIdentityAdvertisementException">冻结 Provider/model 目录无效。</exception>
    public StdioDaemon(
        SessionRepository sessions,
        AgentService agent,
        bool conformanceMode,
        AgentProfileService? profiles = null,
        CustomProviderConnectionService? providerConnections = null,
        SessionLifecycleService? sessionLifecycle = null,
        ProviderCatalogService? providerCatalog = null)
    {
        this.sessions = sessions;
        this.agent = agent;
        this.conformanceMode = conformanceMode;
        this.profiles = profiles;
        this.providerCatalog = providerCatalog ?? new ProviderCatalogService();
        this.providerConnections = providerConnections;
        this.sessionLifecycle = sessionLifecycle;
    }

    /// <summary>
    /// 功能：处理严格 NDJSON 请求直到 stdin clean EOF，并等待本连接已接受 run 完成。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="input">协议专用 stdin 字节流。</param>
    /// <param name="output">协议专用 stdout 字节流；不得混入日志。</param>
    /// <param name="cancellationToken">daemon 生命周期取消信号。</param>
    /// <returns>EOF 且所有本连接事件写完后的 Task。</returns>
    /// <remarks>不变量：第一条成功请求必须是 initialize；run/start 响应 flush 后才启动该 run 的事件 writer。</remarks>
    /// <exception cref="IOException">stdout 断开或底层 journal I/O 失败。</exception>
    public async Task RunAsync(
        Stream input,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        var frameReader = new NdjsonFrameReader(MaxFrameBytes);
        using var writer = new ProtocolWriter(output, MaxFrameBytes, MaxEventBytes);
        var initialized = false;
        var interactiveApprovals = false;
        var acceptedRuns = new List<AcceptedRun>();
        try
        {
            try
            {
                await foreach (var frame in frameReader.ReadFramesAsync(input, cancellationToken).ConfigureAwait(false))
                {
                    JsonRpcRequest request;
                    try
                    {
                        request = ProtocolCodec.ParseRequest(frame);
                    }
                    catch (ProtocolRequestException exception)
                    {
                        await writer.WriteErrorAsync(exception.RequestId, exception.Error, cancellationToken).ConfigureAwait(false);
                        if (!initialized)
                        {
                            break;
                        }

                        continue;
                    }

                    if (!initialized)
                    {
                        if (request.Method != "initialize")
                        {
                            await writer.WriteErrorAsync(
                                request.Id,
                                ProtocolCodec.InvalidRequest("first request must be initialize").Error,
                                cancellationToken).ConfigureAwait(false);
                            break;
                        }

                        var negotiatedApprovals = await InitializeAsync(
                            request,
                            writer,
                            cancellationToken).ConfigureAwait(false);
                        if (!negotiatedApprovals.HasValue)
                        {
                            break;
                        }

                        interactiveApprovals = negotiatedApprovals.Value;
                        initialized = true;
                        continue;
                    }

                    await DispatchAsync(
                        request,
                        writer,
                        interactiveApprovals,
                        acceptedRuns,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (InvalidDataException)
            {
                await writer.WriteErrorAsync(
                    null,
                    new PortableError(-32700, "parse error", false, new ErrorDetails("parse_error")),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var acceptedRun in acceptedRuns)
            {
                await AgentService.DenyPendingApprovalsAsync(acceptedRun, "disconnect").ConfigureAwait(false);
            }

            await Task.WhenAll(activeTasks.ToArray()).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 功能：协商 0.1 并返回与共享 golden 完全一致的能力和限制结构。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">initialize 请求。</param>
    /// <param name="writer">协议 writer。</param>
    /// <param name="cancellationToken">操作取消信号。</param>
    /// <returns>成功时返回 interactiveApprovals；错误已发送时返回 null。</returns>
    private async Task<bool?> InitializeAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        try
        {
            var negotiation = ProtocolCodec.ParseInitialize(request.Params);
            var sandboxError = await agent.InitializeHardSandboxAsync(cancellationToken).ConfigureAwait(false);
            if (sandboxError is not null)
            {
                await writer.WriteErrorAsync(request.Id, sandboxError, cancellationToken).ConfigureAwait(false);
                return null;
            }

            await writer.WriteSuccessAsync(
                request.Id,
                CreateInitializeResult(negotiation.ProtocolVersion),
                cancellationToken).ConfigureAwait(false);
            return negotiation.InteractiveApprovals;
        }
        catch (ProtocolRequestException exception)
        {
            await writer.WriteErrorAsync(request.Id, exception.Error, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// 功能：分派初始化后的有 ID 请求并把所有同步失败映射为 portable error。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">已验证 JSON-RPC 请求。</param>
    /// <param name="writer">协议 writer。</param>
    /// <param name="interactiveApprovals">本连接 initialize 协商的交互审批能力。</param>
    /// <param name="acceptedRuns">本连接已确认、用于 EOF 安全决议的 run。</param>
    /// <param name="cancellationToken">daemon 取消信号。</param>
    /// <returns>响应 flush 后的 Task；run 事件在独立 Task 中继续。</returns>
    private async Task DispatchAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        bool interactiveApprovals,
        List<AcceptedRun> acceptedRuns,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Method)
            {
                case "initialize":
                    throw ProtocolCodec.InvalidRequest("initialize may only be called once");
                case "faux/configure" when conformanceMode:
                    await ConfigureFauxAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "agentProfiles/list" when profiles is not null:
                    await ListAgentProfilesAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "agentProfiles/create" when profiles is not null:
                    await CreateAgentProfileAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "agentProfiles/update" when profiles is not null:
                    await UpdateAgentProfileAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "agentProfiles/delete" when profiles is not null:
                    await DeleteAgentProfileAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "providerCatalog/list":
                    await ListProviderCatalogAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "providerConnections/list" when providerConnections is not null:
                    await ListProviderConnectionsAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "providerConnections/create" when providerConnections is not null:
                    await CreateProviderConnectionAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "providerConnections/update" when providerConnections is not null:
                    await UpdateProviderConnectionAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "providerConnections/delete" when providerConnections is not null:
                    await DeleteProviderConnectionAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "providerConnections/discoverModels" when providerConnections is not null:
                    await DiscoverProviderModelsAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "providerCredentials/set" when providerConnections is not null:
                    await SetProviderCredentialAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "providerCredentials/remove" when providerConnections is not null:
                    await RemoveProviderCredentialAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "run/start":
                    acceptedRuns.Add(await StartRunAsync(
                        request,
                        writer,
                        interactiveApprovals,
                        cancellationToken).ConfigureAwait(false));
                    break;
                case "run/cancel":
                    await CancelRunAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "approval/respond":
                    await RespondApprovalAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "session/get":
                    await GetSessionAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "session/list" when sessionLifecycle is not null:
                    await ListSessionsAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "session/archive" when sessionLifecycle is not null:
                    await ArchiveSessionAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "session/restore" when sessionLifecycle is not null:
                    await RestoreSessionAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "session/delete" when sessionLifecycle is not null:
                    await DeleteSessionAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "session/branch/select":
                    await SelectSessionBranchAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "session/compact":
                    await CompactSessionAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case "models/list":
                    await ListModelsAsync(request, writer, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await writer.WriteErrorAsync(
                        request.Id,
                        new PortableError(-32601, "method not found", false, new ErrorDetails("method_not_found")),
                        cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (ProtocolRequestException exception)
        {
            await writer.WriteErrorAsync(request.Id, exception.Error, cancellationToken).ConfigureAwait(false);
        }
        catch (ApprovalResponseException exception)
        {
            await writer.WriteErrorAsync(request.Id, exception.Error, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentProfileException exception)
        {
            await writer.WriteErrorAsync(request.Id, exception.Error, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderConnectionException exception)
        {
            await writer.WriteErrorAsync(request.Id, exception.Error, cancellationToken).ConfigureAwait(false);
        }
        catch (SessionMutationException exception)
        {
            await writer.WriteErrorAsync(request.Id, exception.Error, cancellationToken).ConfigureAwait(false);
        }
        catch (SessionLifecycleException exception)
        {
            await writer.WriteErrorAsync(request.Id, exception.Error, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException or JournalCorruptException or JournalIncompatibleException)
        {
            await writer.WriteErrorAsync(request.Id, MapException(exception), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 功能：严格解析场景，durable faux.configured 后才发送 scenarioId。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">faux/configure 请求。</param>
    /// <param name="writer">协议 writer。</param>
    /// <param name="cancellationToken">持久化取消信号。</param>
    /// <returns>响应 flush 后的 Task。</returns>
    private async Task ConfigureFauxAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var (sessionId, scenarioElement) = ProtocolCodec.ParseFauxConfigure(request.Params);
        FauxScenario scenario;
        try
        {
            scenario = FauxScenarioParser.Parse(scenarioElement);
        }
        catch (ArgumentException exception)
        {
            throw ProtocolCodec.InvalidParams(exception.Message, "scenario");
        }

        var scenarioId = await sessions.ConfigureFauxAsync(sessionId, scenario, cancellationToken).ConfigureAwait(false);
        await writer.WriteSuccessAsync(
            request.Id,
            new FauxConfigureResult(scenarioId),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：执行 append-before-ack 接受流程，flush runId 响应后才调度事件流。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">run/start 请求。</param>
    /// <param name="writer">协议 writer。</param>
    /// <param name="interactiveApprovals">当前连接是否能响应审批。</param>
    /// <param name="cancellationToken">接受和 daemon 生命周期信号。</param>
    /// <returns>runId 响应 flush 且事件任务已调度的 accepted run。</returns>
    private async Task<AcceptedRun> StartRunAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        bool interactiveApprovals,
        CancellationToken cancellationToken)
    {
        var (sessionId, input, provider, agentProfile) = ProtocolCodec.ParseRunStart(request.Params);
        var acceptedRun = await agent.AcceptAsync(
            sessionId,
            input,
            provider,
            agentProfile,
            interactiveApprovals,
            cancellationToken).ConfigureAwait(false);
        await writer.WriteSuccessAsync(
            request.Id,
            new RunStartResult(acceptedRun.RunId),
            cancellationToken).ConfigureAwait(false);
        activeTasks.Add(WriteRunEventsAsync(acceptedRun, writer, cancellationToken));
        return acceptedRun;
    }

    /// <summary>
    /// 功能：处理幂等取消并在 intent durable 后返回当前 cancellationState。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">run/cancel 请求。</param>
    /// <param name="writer">协议 writer。</param>
    /// <param name="cancellationToken">持久化取消信号。</param>
    /// <returns>响应 flush 后的 Task。</returns>
    private async Task CancelRunAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var (sessionId, runId) = ProtocolCodec.ParseRunReference(request.Params);
        var state = await agent.RequestCancellationAsync(sessionId, runId, cancellationToken).ConfigureAwait(false);
        await writer.WriteSuccessAsync(request.Id, new CancelResult(state), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：先 durable 保存 approval/respond 决议并 flush 成功响应，再解除 Agent 执行屏障。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">approval/respond 请求。</param>
    /// <param name="writer">协议 writer。</param>
    /// <param name="cancellationToken">决议持久化和响应写入信号。</param>
    /// <returns>成功响应 flush 且 waiter 已获准继续后的 Task。</returns>
    /// <remarks>不变量：approval.resolved/tool.started 事件不能先于本方法的成功响应。</remarks>
    private async Task RespondApprovalAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var (sessionId, runId, approvalId, decision) =
            ProtocolCodec.ParseApprovalResponse(request.Params);
        var receipt = await agent.RespondApprovalAsync(
            sessionId,
            runId,
            approvalId,
            decision,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteSuccessAsync(
                request.Id,
                new ApprovalAcceptedResult(true),
                cancellationToken).ConfigureAwait(false);
            receipt.AcknowledgeResponse();
        }
        catch
        {
            receipt.AbortResponse();
            try
            {
                await receipt.RunCompletion.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 原始 stdout 错误仍由外层处理；进程退出恢复会补齐未完成 run。
            }

            throw;
        }
    }

    /// <summary>
    /// 功能：返回完整 durable 消息、真实 active run 和按 event.seq 过滤的事件增量。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">session/get 请求。</param>
    /// <param name="writer">协议 writer。</param>
    /// <param name="cancellationToken">打开 journal 与构造快照的取消信号。</param>
    /// <returns>响应 flush 后的 Task。</returns>
    private async Task GetSessionAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var (sessionId, afterSeq) = ProtocolCodec.ParseSessionGet(request.Params);
        var snapshot = await sessions.GetSnapshotAsync(
            sessionId,
            afterSeq,
            cancellationToken).ConfigureAwait(false);
        await writer.WriteSuccessAsync(
            request.Id,
            new SessionGetResult(
                snapshot.SessionId,
                snapshot.LatestSeq,
                snapshot.ActiveRunId,
                snapshot.SelectedHeadRecordId,
                snapshot.CompactionRecordId,
                snapshot.Messages,
                snapshot.Events),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：列出 application service 扫描到的真实 Session 摘要与归档状态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">session/list 请求。</param>
    /// <param name="writer">协议专用 writer。</param>
    /// <param name="cancellationToken">响应写入取消信号。</param>
    /// <returns>成功响应 flush 后的任务。</returns>
    private async Task ListSessionsAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var (offset, limit) = ProtocolCodec.ParseSessionList(request.Params);
        var result = CreateSessionsListPage(
            request.Id,
            sessionLifecycle!.List(),
            offset,
            limit);
        await writer.WriteSuccessAsync(
            request.Id,
            result,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：按 cursor offset 构造不超过协议单帧上限的 Session 摘要页。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="requestId">用于精确测量完整 JSON-RPC 响应的 request ID。</param>
    /// <param name="sessions">已按稳定服务顺序排列的全部摘要。</param>
    /// <param name="offset">cursor 解析出的零基偏移。</param>
    /// <param name="limit">客户端请求的 1..128 页大小。</param>
    /// <returns>完整帧一定不超过 maxFrameBytes 的分页结果。</returns>
    private static SessionsListResult CreateSessionsListPage(
        JsonElement requestId,
        IReadOnlyList<SessionSummary> sessions,
        int offset,
        int limit)
    {
        var boundedOffset = Math.Min(offset, sessions.Count);
        var count = Math.Min(limit, sessions.Count - boundedOffset);
        while (count >= 0)
        {
            var hasMore = boundedOffset + count < sessions.Count;
            var result = new SessionsListResult(
                sessions.Skip(boundedOffset).Take(count).ToArray(),
                hasMore ? "v1:" + (boundedOffset + count).ToString(
                    System.Globalization.CultureInfo.InvariantCulture) : null,
                hasMore);
            var frame = new JsonRpcSuccessResponse<SessionsListResult>("2.0", requestId, result);
            if ((count > 0 || !hasMore) &&
                JsonSerializer.SerializeToUtf8Bytes(frame, JsonDefaults.Options).Length <= MaxFrameBytes)
            {
                return result;
            }

            count--;
        }

        throw SessionLifecycleException.Store("session_lifecycle_store_invalid");
    }

    /// <summary>
    /// 功能：在 Session 静止时持久化归档状态并返回真实摘要。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">session/archive 请求。</param>
    /// <param name="writer">协议专用 writer。</param>
    /// <param name="cancellationToken">响应写入取消信号。</param>
    /// <returns>归档状态 durable 后响应 flush 的任务。</returns>
    private async Task ArchiveSessionAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var sessionId = ProtocolCodec.ParseSessionLifecycleMutation(request.Params);
        var summary = await sessionLifecycle!.ArchiveAsync(
            sessionId,
            cancellationToken).ConfigureAwait(false);
        await writer.WriteSuccessAsync(
            request.Id,
            new SessionSummaryResult(summary),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：在 Session 静止时移除归档状态并返回真实摘要。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">session/restore 请求。</param>
    /// <param name="writer">协议专用 writer。</param>
    /// <param name="cancellationToken">响应写入取消信号。</param>
    /// <returns>恢复状态 durable 后响应 flush 的任务。</returns>
    private async Task RestoreSessionAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var sessionId = ProtocolCodec.ParseSessionLifecycleMutation(request.Params);
        var summary = await sessionLifecycle!.RestoreAsync(
            sessionId,
            cancellationToken).ConfigureAwait(false);
        await writer.WriteSuccessAsync(
            request.Id,
            new SessionSummaryResult(summary),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：在 Session 静止健康时执行 tombstone 安全删除。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">session/delete 请求。</param>
    /// <param name="writer">协议专用 writer。</param>
    /// <param name="cancellationToken">响应写入取消信号。</param>
    /// <returns>完整普通树删除后响应 flush 的任务。</returns>
    private async Task DeleteSessionAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var sessionId = ProtocolCodec.ParseSessionLifecycleMutation(request.Params);
        await sessionLifecycle!.DeleteAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await writer.WriteSuccessAsync(
            request.Id,
            new SessionDeleteResult(Deleted: true),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：严格解析 branch request，并仅在 branch.selected durable 后返回三个一致 record ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">session/branch/select JSON-RPC 请求。</param>
    /// <param name="writer">协议专用串行 stdout writer。</param>
    /// <param name="cancellationToken">解析、Session mutation 与响应写入取消信号。</param>
    /// <returns>durable selection 响应 flush 后完成的 Task。</returns>
    /// <remarks>不变量：失败 mutation 不写 journal；响应 ID 直接来自同一次 durable append 回执。</remarks>
    private async Task SelectSessionBranchAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var (sessionId, expectedHeadRecordId, targetLeafRecordId) =
            ProtocolCodec.ParseSessionBranchSelect(request.Params);
        var receipt = await sessions.SelectBranchAsync(
            sessionId,
            expectedHeadRecordId,
            targetLeafRecordId,
            cancellationToken).ConfigureAwait(false);
        await writer.WriteSuccessAsync(
            request.Id,
            new SessionBranchSelectResult(
                receipt.TargetLeafRecordId,
                receipt.SelectionRecordId,
                receipt.SelectedHeadRecordId),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：严格解析 compaction request，并仅在 summary/context pair 均 durable 后返回身份结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">session/compact JSON-RPC 请求。</param>
    /// <param name="writer">协议专用串行 stdout writer。</param>
    /// <param name="cancellationToken">解析、pair mutation 与响应写入取消信号。</param>
    /// <returns>compaction 成功响应 flush 后完成的 Task。</returns>
    /// <remarks>不变量：第二条 context.compacted 未 durable 时绝不发送成功；Provider/usage extensions 原样进入 summary。</remarks>
    private async Task CompactSessionAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var (sessionId, command) = ProtocolCodec.ParseSessionCompact(request.Params);
        var receipt = await sessions.CompactAsync(
            sessionId,
            command,
            cancellationToken).ConfigureAwait(false);
        await writer.WriteSuccessAsync(
            request.Id,
            new SessionCompactResult(
                receipt.SummaryMessageId,
                receipt.SummaryRecordId,
                receipt.CompactionRecordId,
                receipt.SelectedHeadRecordId),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：严格解析 Provider 过滤器并返回当前启动期 route-qualified 模型快照。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">models/list JSON-RPC 请求。</param>
    /// <param name="writer">协议专用串行 stdout writer。</param>
    /// <param name="cancellationToken">响应写入取消信号。</param>
    /// <returns>完整成功响应 flush 后完成的 Task。</returns>
    /// <remarks>不变量：本方法不访问网络、credential、DNS、metadata、OAuth 或 endpoint。</remarks>
    private async Task ListModelsAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var providerId = ProtocolCodec.ParseModelsList(request.Params);
        await writer.WriteSuccessAsync(
            request.Id,
            new ModelsListResult(agent.ListModels(providerId)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：严格解析空参数并返回冻结的自定义 Provider 配置模板目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">providerCatalog/list JSON-RPC 请求。</param>
    /// <param name="writer">协议专用串行 stdout writer。</param>
    /// <param name="cancellationToken">响应写入取消信号。</param>
    /// <returns>完整成功响应 flush 后完成的 Task。</returns>
    /// <remarks>不变量：响应不读取或包含 credential、configured、available、executable 或 conformance 状态。</remarks>
    private async Task ListProviderCatalogAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        ProtocolCodec.ParseProviderCatalogList(request.Params);
        await writer.WriteSuccessAsync(
            request.Id,
            new ProviderCatalogListResult(providerCatalog.List()),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：返回全部不含 secret 的自定义 Provider 连接和凭据 presence。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">providerConnections/list 请求。</param>
    /// <param name="writer">协议专用 writer。</param>
    /// <param name="cancellationToken">响应写入取消信号。</param>
    /// <returns>成功响应 flush 后的任务。</returns>
    private async Task ListProviderConnectionsAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        ProtocolCodec.ParseProviderConnectionsList(request.Params);
        await writer.WriteSuccessAsync(
            request.Id,
            new ProviderConnectionsListResult(providerConnections!.List()),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：严格解析并创建 revision 1 的非敏感 Provider 连接。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">providerConnections/create 请求。</param>
    /// <param name="writer">协议专用 writer。</param>
    /// <param name="cancellationToken">响应写入取消信号。</param>
    /// <returns>durable 回执 flush 后的任务。</returns>
    private async Task CreateProviderConnectionAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var input = ProtocolCodec.ParseProviderConnectionsCreate(request.Params);
        var connection = providerConnections!.Create(input);
        await writer.WriteSuccessAsync(
            request.Id,
            new ProviderConnectionResult(connection, RestartRequired: true),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：严格解析并按 revision CAS 更新非敏感 Provider 连接。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">providerConnections/update 请求。</param>
    /// <param name="writer">协议专用 writer。</param>
    /// <param name="cancellationToken">响应写入取消信号。</param>
    /// <returns>durable 回执 flush 后的任务。</returns>
    private async Task UpdateProviderConnectionAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var (connectionId, expectedRevision, input) =
            ProtocolCodec.ParseProviderConnectionsUpdate(request.Params);
        var connection = providerConnections!.Update(connectionId, expectedRevision, input);
        await writer.WriteSuccessAsync(
            request.Id,
            new ProviderConnectionResult(connection, RestartRequired: true),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：严格解析并按 revision CAS 删除连接及其 credential。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">providerConnections/delete 请求。</param>
    /// <param name="writer">协议专用 writer。</param>
    /// <param name="cancellationToken">响应写入取消信号。</param>
    /// <returns>删除回执 flush 后的任务。</returns>
    private async Task DeleteProviderConnectionAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var (connectionId, expectedRevision) =
            ProtocolCodec.ParseProviderConnectionsDelete(request.Params);
        providerConnections!.Delete(connectionId, expectedRevision);
        await writer.WriteSuccessAsync(
            request.Id,
            new ProviderConnectionDeleteResult(Deleted: true, RestartRequired: true),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：显式发现远端模型，并按请求前 revision CAS 发布新的模型 allowlist。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">providerConnections/discoverModels 请求。</param>
    /// <param name="writer">协议专用 writer。</param>
    /// <param name="cancellationToken">20 秒总时限之外的 daemon 取消信号。</param>
    /// <returns>durable 脱敏回执 flush 后的任务。</returns>
    private async Task DiscoverProviderModelsAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var (connectionId, expectedRevision) =
            ProtocolCodec.ParseProviderConnectionsDiscoverModels(request.Params);
        var connection = await providerConnections!.DiscoverModelsAsync(
            connectionId,
            expectedRevision,
            cancellationToken).ConfigureAwait(false);
        await writer.WriteSuccessAsync(
            request.Id,
            new ProviderModelDiscoveryResult(
                connection,
                connection.ModelIds.Count,
                RestartRequired: true),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：把瞬时 credential 写入独立 CredentialStore，并只返回 presence。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">providerCredentials/set 请求。</param>
    /// <param name="writer">协议专用 writer。</param>
    /// <param name="cancellationToken">响应写入取消信号。</param>
    /// <returns>脱敏状态 flush 后的任务。</returns>
    private async Task SetProviderCredentialAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var (providerId, credential) = ProtocolCodec.ParseProviderCredentialsSet(request.Params);
        providerConnections!.SetCredential(providerId, credential);
        await writer.WriteSuccessAsync(
            request.Id,
            new ProviderCredentialStatusResult(
                providerId,
                CredentialConfigured: true,
                RestartRequired: true),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：移除独立 CredentialStore 中的 Provider secret，并只返回 false presence。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">providerCredentials/remove 请求。</param>
    /// <param name="writer">协议专用 writer。</param>
    /// <param name="cancellationToken">响应写入取消信号。</param>
    /// <returns>脱敏状态 flush 后的任务。</returns>
    private async Task RemoveProviderCredentialAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var providerId = ProtocolCodec.ParseProviderCredentialsRemove(request.Params);
        providerConnections!.RemoveCredential(providerId);
        await writer.WriteSuccessAsync(
            request.Id,
            new ProviderCredentialStatusResult(
                providerId,
                CredentialConfigured: false,
                RestartRequired: true),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：返回 application database 中全部 Agent Profile。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">agentProfiles/list 请求。</param>
    /// <param name="writer">协议 writer。</param>
    /// <param name="cancellationToken">查询与响应写入取消信号。</param>
    /// <returns>成功响应 flush 后的任务。</returns>
    private async Task ListAgentProfilesAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        ProtocolCodec.ParseAgentProfilesList(request.Params);
        var result = await profiles!.ListAsync(cancellationToken).ConfigureAwait(false);
        await writer.WriteSuccessAsync(
            request.Id,
            new AgentProfilesListResult(result),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：严格解析并创建 revision 1 的 Agent Profile。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">agentProfiles/create 请求。</param>
    /// <param name="writer">协议 writer。</param>
    /// <param name="cancellationToken">写库与响应写入取消信号。</param>
    /// <returns>成功响应 flush 后的任务。</returns>
    private async Task CreateAgentProfileAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var input = ProtocolCodec.ParseAgentProfilesCreate(request.Params);
        var result = await profiles!.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        await writer.WriteSuccessAsync(
            request.Id,
            new AgentProfileResult(result),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：严格解析并按 expected revision 完整更新 Agent Profile。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">agentProfiles/update 请求。</param>
    /// <param name="writer">协议 writer。</param>
    /// <param name="cancellationToken">写库与响应写入取消信号。</param>
    /// <returns>成功响应 flush 后的任务。</returns>
    private async Task UpdateAgentProfileAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var (profileId, expectedRevision, input) =
            ProtocolCodec.ParseAgentProfilesUpdate(request.Params);
        var result = await profiles!
            .UpdateAsync(profileId, expectedRevision, input, cancellationToken)
            .ConfigureAwait(false);
        await writer.WriteSuccessAsync(
            request.Id,
            new AgentProfileResult(result),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：严格解析并按 expected revision 删除 Agent Profile。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">agentProfiles/delete 请求。</param>
    /// <param name="writer">协议 writer。</param>
    /// <param name="cancellationToken">写库与响应写入取消信号。</param>
    /// <returns>成功响应 flush 后的任务。</returns>
    private async Task DeleteAgentProfileAsync(
        JsonRpcRequest request,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var (profileId, expectedRevision) =
            ProtocolCodec.ParseAgentProfilesDelete(request.Params);
        await profiles!
            .DeleteAsync(profileId, expectedRevision, cancellationToken)
            .ConfigureAwait(false);
        await writer.WriteSuccessAsync(
            request.Id,
            new AgentProfilesDeleteResult(true),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：消费 Agent 的已 durable 事件流并逐帧写到协议 stdout。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="run">已响应客户端的 accepted run。</param>
    /// <param name="writer">共享串行 writer。</param>
    /// <param name="cancellationToken">daemon 生命周期信号。</param>
    /// <returns>terminal event flush 后的 Task。</returns>
    private async Task WriteRunEventsAsync(
        AcceptedRun run,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        await foreach (var portableEvent in agent.RunAsync(run, cancellationToken).ConfigureAwait(false))
        {
            await writer.WriteEventAsync(portableEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 功能：构造与当前共享 golden 顺序和值精确一致的 initialize 结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="protocolVersion">已协商版本。</param>
    /// <returns>能力和限制 DTO。</returns>
    private InitializeResult CreateInitializeResult(string protocolVersion)
    {
        var methods = new List<string> { "initialize" };
        if (conformanceMode)
        {
            methods.Add("faux/configure");
        }

        if (profiles is not null)
        {
            methods.AddRange(
                [
                    "agentProfiles/list",
                    "agentProfiles/create",
                    "agentProfiles/update",
                    "agentProfiles/delete",
                ]);
        }

        if (providerConnections is not null)
        {
            methods.AddRange(
                [
                    "providerConnections/list",
                    "providerConnections/create",
                    "providerConnections/update",
                    "providerConnections/delete",
                    "providerConnections/discoverModels",
                    "providerCredentials/set",
                    "providerCredentials/remove",
                ]);
        }

        if (sessionLifecycle is not null)
        {
            methods.AddRange(
                [
                    "session/list",
                    "session/archive",
                    "session/restore",
                    "session/delete",
                ]);
        }

        methods.AddRange(
            [
                "run/start", "run/cancel", "approval/respond", "session/get",
                "session/branch/select", "session/compact", "models/list", "providerCatalog/list",
            ]);
        return new InitializeResult(
            protocolVersion,
            new ImplementationInfo("qxnm-forge-dotnet", "0.1.0", "dotnet"),
            new ServerCapabilities(
                methods,
                [
                    "run.started", "turn.started", "message.started", "message.delta", "message.completed",
                    "turn.completed", "tool.requested", "approval.requested", "approval.resolved",
                    "tool.started", "tool.completed", "retry.scheduled", "run.completed", "run.failed",
                    "context.compacted", "run.cancelled", "run.interrupted",
                ],
                agent.ProviderAdvertisements
                    .Select(static provider => new ProviderCapability(provider.Id, provider.Models))
                    .ToArray(),
                agent.ConfiguredTools,
                ["stdio"],
                agent.HardSandboxCapability),
            new ProtocolLimits(MaxFrameBytes, MaxEventBytes, MaxArtifactBytes, 1));
    }

    /// <summary>
    /// 功能：把预期本地异常映射为不泄露路径内容或栈的 portable error。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="exception">捕获的预期异常。</param>
    /// <returns>稳定错误码、retryable 和 details。</returns>
    private static PortableError MapException(Exception exception)
    {
        return exception switch
        {
            ProviderUnavailableException unavailable => new PortableError(
                -32005,
                "requested Provider or model is unavailable",
                false,
                new ErrorDetails(
                    "provider_unavailable",
                    ProviderId: unavailable.ProviderId,
                    ModelId: unavailable.ModelId)),
            SessionWriterLeaseException writerLeaseException => new PortableError(
                -32002,
                writerLeaseException.Message,
                writerLeaseException.Retryable,
                new ErrorDetails(writerLeaseException.Kind)),
            JournalCorruptException => new PortableError(
                -32008, "journal is corrupt", false, new ErrorDetails("journal_corrupt")),
            JournalIncompatibleException => new PortableError(
                -32008, "journal is corrupt or incompatible", false, new ErrorDetails("journal_corrupt")),
            IOException => new PortableError(
                -32002, "session is locked or unavailable", true, new ErrorDetails("session_locked")),
            InvalidOperationException when exception.Message == "session is busy" => new PortableError(
                -32004, "session is busy", true, new ErrorDetails("run_busy")),
            KeyNotFoundException => new PortableError(
                -32010, "run was not found", true, new ErrorDetails("stale_state")),
            _ => new PortableError(
                -32602, "invalid params", false, new ErrorDetails("invalid_params")),
        };
    }
}
