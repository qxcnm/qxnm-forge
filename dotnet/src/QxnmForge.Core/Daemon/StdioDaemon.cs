using System.Collections.Concurrent;
using System.Text.Json;
using QxnmForge.Agent;
using QxnmForge.Domain;
using QxnmForge.Protocol;
using QxnmForge.Provider;
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
    public StdioDaemon(SessionRepository sessions, AgentService agent, bool conformanceMode)
    {
        this.sessions = sessions;
        this.agent = agent;
        this.conformanceMode = conformanceMode;
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
        catch (SessionMutationException exception)
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
        var (sessionId, input, provider) = ProtocolCodec.ParseRunStart(request.Params);
        var acceptedRun = await agent.AcceptAsync(
            sessionId,
            input,
            provider,
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
        IReadOnlyList<string> methods = conformanceMode
            ?
            [
                "initialize", "faux/configure", "run/start", "run/cancel", "approval/respond",
                "session/get", "session/branch/select", "session/compact", "models/list",
            ]
            :
            [
                "initialize", "run/start", "run/cancel", "approval/respond", "session/get",
                "session/branch/select", "session/compact", "models/list",
            ];
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
