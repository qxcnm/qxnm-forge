using System.Runtime.CompilerServices;
using System.Text.Json;
using QxnmForge.Agent;
using QxnmForge.Domain;
using QxnmForge.Provider;
using QxnmForge.Serialization;
using QxnmForge.Session;
using QxnmForge.Tools;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 text-only Agent 的状态机顺序与 durable terminal 行为。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class AgentServiceTests
{
    /// <summary>
    /// 功能：确认 successful text run 产生共享 minimal trace 的六类事件顺序。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task RunAsyncEmitsMinimalTextTraceInOrder()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        await using var repository = new SessionRepository(Path.Combine(temporary.Path, "sessions"), workspace);
        var service = new AgentService(repository, new FauxProvider());
        var scenario = new FauxScenario(
            "0.1",
            "hello",
            1,
            [new FauxTextStep("你好")],
            new Usage(2, 3, 5));
        await repository.ConfigureFauxAsync("session-agent", scenario, CancellationToken.None);
        var run = await service.AcceptAsync(
            "session-agent",
            new InputMessage("user", [new TextContent("hello")]),
            new ProviderSelection("faux", "faux-v1"),
            CancellationToken.None);

        var events = new List<AgentEvent>();
        await foreach (var portableEvent in service.RunAsync(run, CancellationToken.None))
        {
            events.Add(portableEvent);
        }

        Assert.Equal(
            ["run.started", "turn.started", "message.started", "message.delta", "message.completed", "run.completed"],
            events.Select(static item => item.Type));
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], events.Select(static item => item.Seq));
        Assert.Null(events[0].TurnId);
        Assert.NotNull(events[1].TurnId);
        Assert.Null(events[^1].TurnId);

        var journal = await repository.GetAsync("session-agent", CancellationToken.None);
        var lines = await File.ReadAllLinesAsync(journal.Journal.JournalPath, CancellationToken.None);
        var kinds = lines.Skip(1).Select(GetKind).ToList();
        Assert.Contains("message.appended", kinds);
        Assert.Contains("run.accepted", kinds);
        Assert.Contains("run.terminal", kinds);
        Assert.Equal(6, kinds.Count(static kind => kind == "event.emitted"));
    }

    /// <summary>
    /// 功能：确认不支持工具的 Provider route 收到空工具集合，即使宿主 registry 已注册工具。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task ProviderWithoutToolCapabilityReceivesNoToolDefinitions()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        await using var repository = new SessionRepository(
            Path.Combine(temporary.Path, "sessions"),
            workspace);
        using var tools = new ToolRegistry(workspace);
        Assert.NotEmpty(tools.ProviderDefinitions());
        var provider = new ToolCapabilityProvider();
        await using var providers = new ProviderRegistry([provider]);
        var service = new AgentService(repository, providers, tools);
        var run = await service.AcceptAsync(
            "session-no-provider-tools",
            new InputMessage("user", [new TextContent("plain text")]),
            new ProviderSelection(provider.Id, "plain-model"),
            CancellationToken.None);

        _ = await CollectAsync(service.RunAsync(run, CancellationToken.None));

        Assert.NotNull(provider.ObservedRequest);
        Assert.Empty(provider.ObservedRequest.Tools);
    }

    /// <summary>
    /// 功能：确认 delay await 边界接收取消并只产生 run.cancelled 终态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task RequestCancellationAsyncIsIdempotentAndProducesCancelledTerminal()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        await using var repository = new SessionRepository(Path.Combine(temporary.Path, "sessions"), workspace);
        var providerRequestObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new AgentService(
            repository,
            new FauxProvider(requestObserver: _ => providerRequestObserved.SetResult()));
        await repository.ConfigureFauxAsync(
            "session-cancel",
            new FauxScenario("0.1", "cancel", 0, [new FauxDelayStep(10_000)]),
            CancellationToken.None);
        var run = await service.AcceptAsync(
            "session-cancel",
            new InputMessage("user", [new TextContent("wait")]),
            new ProviderSelection("faux", "faux-v1"),
            CancellationToken.None);

        var collecting = CollectAsync(service.RunAsync(run, CancellationToken.None));
        var startup = await Task.WhenAny(providerRequestObserved.Task, collecting)
            .WaitAsync(TimeSpan.FromSeconds(5));
        if (ReferenceEquals(startup, collecting))
        {
            await collecting;
        }

        Assert.Same(providerRequestObserved.Task, startup);
        Assert.Equal(
            "requested",
            await service.RequestCancellationAsync("session-cancel", run.RunId, CancellationToken.None));
        var repeatedCancellation = await service.RequestCancellationAsync(
            "session-cancel",
            run.RunId,
            CancellationToken.None);
        Assert.True(repeatedCancellation is "alreadyRequested" or "terminal");
        var events = await collecting;

        Assert.Equal("run.cancelled", events[^1].Type);
        Assert.Single(
            events,
            static item => item.Type.StartsWith("run.", StringComparison.Ordinal) &&
                           item.Type is "run.completed" or "run.failed" or "run.cancelled" or "run.interrupted");
        var runtime = await repository.GetAsync("session-cancel", CancellationToken.None);
        var journalLines = await File.ReadAllLinesAsync(runtime.Journal.JournalPath, CancellationToken.None);
        Assert.Single(
            journalLines.Skip(1),
            static line => GetKind(line) == "run.cancellation_requested");
        var attemptTrace = ReadProviderAttemptTrace(journalLines);
        Assert.Equal(["started", "cancelled"], attemptTrace.Statuses);
        Assert.True(attemptTrace.LastAttemptIndex < attemptTrace.RunTerminalIndex);
        Assert.Equal(
            "terminal",
            await service.RequestCancellationAsync("session-cancel", run.RunId, CancellationToken.None));
    }

    /// <summary>
    /// 功能：确认注入的短审批 deadline 自动 durable timeout deny，且危险写工具从未启动。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task ApprovalTimeoutDeniesWithoutStartingTool()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        await using var repository = new SessionRepository(Path.Combine(temporary.Path, "sessions"), workspace);
        var faux = new FauxProvider();
        await using var registry = new ProviderRegistry([faux]);
        using var tools = new ToolRegistry(workspace);
        var service = new AgentService(
            repository,
            registry,
            tools,
            TimeSpan.FromMilliseconds(50));
        var arguments = JsonSerializer.SerializeToElement(
            new { Path = "approval-timeout.txt", Content = "must not be written" },
            JsonDefaults.Options);
        await repository.ConfigureFauxAsync(
            "session-approval-timeout",
            new FauxScenario(
                "0.1",
                "approval-timeout",
                1,
                [new FauxToolCallStep("approval-timeout-call", "file.write", arguments)],
                Continuations: [new FauxContinuation([new FauxTextStep("denied")])]),
            CancellationToken.None);
        var run = await service.AcceptAsync(
            "session-approval-timeout",
            new InputMessage("user", [new TextContent("request write")]),
            new ProviderSelection("faux", "faux-v1"),
            interactiveApprovals: true,
            CancellationToken.None);

        var events = await CollectAsync(service.RunAsync(run, CancellationToken.None));

        Assert.Contains(events, static item => item.Type == "approval.requested");
        var resolved = Assert.Single(events, static item => item.Type == "approval.resolved");
        var resolvedData = JsonSerializer.SerializeToElement(resolved.Data, JsonDefaults.Options);
        Assert.Equal("timeout", resolvedData.GetProperty("resolutionSource").GetString());
        Assert.Equal("deny", resolvedData.GetProperty("decision").GetProperty("choice").GetString());
        Assert.DoesNotContain(events, static item => item.Type == "tool.started");
        Assert.Equal("run.completed", events[^1].Type);
        Assert.False(File.Exists(Path.Combine(workspace, "approval-timeout.txt")));
    }

    /// <summary>
    /// 功能：确认重复/迟到审批固定 conflict，且 success 未交付只审计一次 client allow 并以 interrupted 零执行收尾。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task ApprovalDeliveryFailurePersistsToolErrorWithoutReleasingAllow()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        await using var repository = new SessionRepository(Path.Combine(temporary.Path, "sessions"), workspace);
        var faux = new FauxProvider();
        await using var registry = new ProviderRegistry([faux]);
        using var tools = new ToolRegistry(workspace);
        var service = new AgentService(repository, registry, tools);
        var arguments = JsonSerializer.SerializeToElement(
            new { Path = "approval-delivery.txt", Content = "must not be written" },
            JsonDefaults.Options);
        await repository.ConfigureFauxAsync(
            "session-approval-delivery",
            new FauxScenario(
                "0.1",
                "approval-delivery",
                2,
                [new FauxToolCallStep("approval-delivery-call", "file.write", arguments)],
                Continuations: [new FauxContinuation([new FauxTextStep("not reached")])]),
            CancellationToken.None);
        var run = await service.AcceptAsync(
            "session-approval-delivery",
            new InputMessage("user", [new TextContent("request write")]),
            new ProviderSelection("faux", "faux-v1"),
            interactiveApprovals: true,
            CancellationToken.None);
        var events = new List<AgentEvent>();
        await using var enumerator = service
            .RunAsync(run, CancellationToken.None)
            .GetAsyncEnumerator(CancellationToken.None);
        string? approvalId = null;
        while (await enumerator.MoveNextAsync())
        {
            events.Add(enumerator.Current);
            if (enumerator.Current.Type == "approval.requested")
            {
                var data = JsonSerializer.SerializeToElement(
                    enumerator.Current.Data,
                    JsonDefaults.Options);
                approvalId = data.GetProperty("approval").GetProperty("approvalId").GetString();
                break;
            }
        }

        Assert.False(string.IsNullOrEmpty(approvalId));
        var decision = new ApprovalDecision("allow_once");
        var receipt = await service.RespondApprovalAsync(
            "session-approval-delivery",
            run.RunId,
            approvalId!,
            decision,
            CancellationToken.None);
        var duplicate = await Assert.ThrowsAsync<ApprovalResponseException>(() =>
            service.RespondApprovalAsync(
                "session-approval-delivery",
                run.RunId,
                approvalId!,
                decision,
                CancellationToken.None));
        Assert.Equal(-32010, duplicate.Error.Code);
        Assert.False(duplicate.Error.Retryable);

        receipt.AbortResponse();
        while (await enumerator.MoveNextAsync())
        {
            events.Add(enumerator.Current);
        }

        Assert.DoesNotContain(events, static item => item.Type == "approval.resolved");
        Assert.DoesNotContain(events, static item => item.Type == "tool.started");
        Assert.Contains(events, static item => item.Type == "tool.completed");
        Assert.Equal("run.interrupted", events[^1].Type);
        Assert.False(File.Exists(Path.Combine(workspace, "approval-delivery.txt")));
        var late = await Assert.ThrowsAsync<ApprovalResponseException>(() =>
            service.RespondApprovalAsync(
                "session-approval-delivery",
                run.RunId,
                approvalId!,
                decision,
                CancellationToken.None));
        Assert.Equal(-32010, late.Error.Code);
        Assert.False(late.Error.Retryable);

        var runtime = await repository.GetAsync("session-approval-delivery", CancellationToken.None);
        var lines = await File.ReadAllLinesAsync(runtime.Journal.JournalPath, CancellationToken.None);
        Assert.Equal(1, lines.Skip(1).Count(static line => GetKind(line) == "approval.resolved"));
        Assert.Equal(1, lines.Skip(1).Count(static line => GetKind(line) == "tool.result"));
        Assert.Equal(1, lines.Skip(1).Count(static line => GetKind(line) == "run.terminal"));
        Assert.DoesNotContain(lines.Skip(1), static line => GetKind(line) == "run.cancellation_requested");
        using var resolutionRecord = JsonDocument.Parse(
            lines.Skip(1).Single(static line => GetKind(line) == "approval.resolved"));
        Assert.Equal(
            "allow_once",
            resolutionRecord.RootElement.GetProperty("data").GetProperty("decision").GetProperty("choice").GetString());
        Assert.Equal(
            "client",
            resolutionRecord.RootElement.GetProperty("data").GetProperty("resolutionSource").GetString());
        using var terminalRecord = JsonDocument.Parse(
            lines.Skip(1).Single(static line => GetKind(line) == "run.terminal"));
        Assert.Equal(
            "interrupted",
            terminalRecord.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    /// <summary>
    /// 功能：确认 Provider 直接失败或断流时先关闭当前 attempt，再持久化唯一 run 终态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="attemptStatus">journal 中预期的 attempt 终态。</param>
    /// <param name="terminalEvent">客户端预期的 run 终态事件。</param>
    /// <returns>异步测试 Task。</returns>
    [Theory]
    [InlineData("failed", "run.failed")]
    [InlineData("interrupted", "run.interrupted")]
    public async Task ProviderFailureClosesAttemptBeforeRunTerminal(
        string attemptStatus,
        string terminalEvent)
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        await using var repository = new SessionRepository(Path.Combine(temporary.Path, "sessions"), workspace);
        var provider = new FailingProvider(attemptStatus);
        await using var registry = new ProviderRegistry([provider]);
        var service = new AgentService(repository, registry);
        var run = await service.AcceptAsync(
            "session-provider-failure-" + attemptStatus,
            new InputMessage("user", [new TextContent("触发受控失败")]),
            new ProviderSelection(provider.Id, "test-model"),
            CancellationToken.None);

        var events = await CollectAsync(service.RunAsync(run, CancellationToken.None));

        Assert.Equal(terminalEvent, events[^1].Type);
        var runtime = await repository.GetAsync(
            "session-provider-failure-" + attemptStatus,
            CancellationToken.None);
        var attemptTrace = ReadProviderAttemptTrace(
            await File.ReadAllLinesAsync(runtime.Journal.JournalPath, CancellationToken.None));
        Assert.Equal(["started", attemptStatus], attemptTrace.Statuses);
        Assert.True(attemptTrace.LastAttemptIndex < attemptTrace.RunTerminalIndex);
    }

    /// <summary>
    /// 功能：确认新 run 从 Rust 格式 journal 无损重建 user/assistant/tool 历史并传给原生 faux Provider 请求。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task AcceptAsyncRebuildsDurableHistoryAndPassesItToProvider()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessions = Path.Combine(temporary.Path, "sessions");
        _ = PortableSessionFixture.InstallRustLinearSession(sessions);
        FauxProviderRequest? observedRequest = null;
        var provider = new FauxProvider(requestObserver: request => observedRequest = request);
        await using var repository = new SessionRepository(sessions, workspace);
        var service = new AgentService(repository, provider);
        await repository.ConfigureFauxAsync(
            "rust-linear",
            new FauxScenario("0.1", "continue-rust", 0, [new FauxTextStep("新回答")]),
            CancellationToken.None);

        var accepted = await service.AcceptAsync(
            "rust-linear",
            new InputMessage("user", [new TextContent("新问题")]),
            new ProviderSelection("faux", "faux-v1"),
            CancellationToken.None);

        Assert.Equal(
            ["user", "assistant", "tool", "assistant", "user"],
            accepted.ContextMessages.Select(static message => message.GetProperty("role").GetString()));
        Assert.Equal(
            "用户扩展",
            accepted.ContextMessages[0]
                .GetProperty("content")[0]
                .GetProperty("extensions")
                .GetProperty("org.agentprotocol.content")
                .GetProperty("marker")
                .GetString());
        Assert.Equal(
            "tool_call",
            accepted.ContextMessages[1].GetProperty("content")[1].GetProperty("type").GetString());

        _ = await CollectAsync(service.RunAsync(accepted, CancellationToken.None));

        Assert.NotNull(observedRequest);
        Assert.Equal(accepted.ContextMessages.Count, observedRequest.Messages.Count);
        Assert.Equal(
            "新问题",
            observedRequest.Messages[^1].GetProperty("content")[0].GetProperty("text").GetString());
    }

    /// <summary>
    /// 功能：确认恢复后的 outcomeKnown=false 作为 tool 历史进入 Provider，但 Agent 不自动重放原工具。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task AcceptAsyncContinuesWithUnknownToolMessageWithoutToolReplay()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessions = Path.Combine(temporary.Path, "sessions");
        await using (var firstRepository = new SessionRepository(sessions, workspace))
        {
            var runtime = await firstRepository.GetAsync("session-ambiguous", CancellationToken.None);
            await runtime.Journal.AppendAsync(
                "run.accepted",
                new
                {
                    RunId = "run-ambiguous",
                    InputMessageId = "message-ambiguous",
                    Provider = new ProviderSelection("faux", "faux-v1"),
                },
                CancellationToken.None);
            await runtime.Journal.AppendAsync(
                "tool.intent",
                new
                {
                    RunId = "run-ambiguous",
                    TurnId = "turn-ambiguous",
                    ToolCallId = "call-ambiguous",
                    Name = "process.exec",
                    Arguments = new { Executable = "never-run" },
                    Idempotent = false,
                    Status = "started",
                },
                CancellationToken.None);
        }

        var providerInvoked = false;
        var provider = new FauxProvider(requestObserver: _ => providerInvoked = true);
        await using var recoveredRepository = new SessionRepository(sessions, workspace);
        var service = new AgentService(recoveredRepository, provider);
        await recoveredRepository.ConfigureFauxAsync(
            "session-ambiguous",
            new FauxScenario("0.1", "must-not-run", 0, [new FauxTextStep("错误")]),
            CancellationToken.None);

        var accepted = await service.AcceptAsync(
            "session-ambiguous",
            new InputMessage("user", [new TextContent("不要重放")]),
            new ProviderSelection("faux", "faux-v1"),
            CancellationToken.None);
        var recoveredToolMessage = accepted.ContextMessages[^2];
        Assert.Equal("tool", recoveredToolMessage.GetProperty("role").GetString());
        Assert.True(recoveredToolMessage.GetProperty("isError").GetBoolean());
        Assert.Contains(
            "not replayed",
            recoveredToolMessage.GetProperty("content")[0].GetProperty("text").GetString(),
            StringComparison.Ordinal);

        _ = await CollectAsync(service.RunAsync(accepted, CancellationToken.None));
        Assert.True(providerInvoked);
    }

    /// <summary>
    /// 功能：用公共跨语言 fixture 验证恢复事件、afterSeq、activeRun 和 faux expectedContext continuation 全链路。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task SharedPortableSessionRecoversAndContinuesWithExpectedProviderContext()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessions = Path.Combine(temporary.Path, "sessions");
        var journalPath = PortableSessionFixture.InstallSharedRecoverySession(sessions);
        var originalBytes = await File.ReadAllBytesAsync(journalPath, CancellationToken.None);
        await using var repository = new SessionRepository(sessions, workspace);
        FauxProviderRequest? observedRequest = null;
        var service = new AgentService(
            repository,
            new FauxProvider(requestObserver: request => observedRequest = request));

        var recovered = await repository.GetSnapshotAsync(
            "session-portable-1",
            4,
            CancellationToken.None);

        Assert.Equal(9, recovered.LatestSeq);
        Assert.Null(recovered.ActiveRunId);
        Assert.Equal(5, recovered.Messages.Count);
        Assert.Equal([5L, 6L, 7L, 8L, 9L], recovered.Events.Select(GetEventSequence));
        Assert.Equal("tool", recovered.Messages[^1].GetProperty("role").GetString());
        Assert.True(recovered.Messages[^1].GetProperty("isError").GetBoolean());
        var recoveredBytes = await File.ReadAllBytesAsync(journalPath, CancellationToken.None);
        Assert.True(recoveredBytes.AsSpan().StartsWith(originalBytes));

        using var scenarioDocument = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                PortableSessionFixture.GetSharedContinuationScenarioPath(),
                CancellationToken.None));
        var scenario = FauxScenarioParser.Parse(scenarioDocument.RootElement);
        await repository.ConfigureFauxAsync("session-portable-1", scenario, CancellationToken.None);
        var accepted = await service.AcceptAsync(
            "session-portable-1",
            new InputMessage(
                "user",
                [new TextContent("What token did I ask you to remember?")]),
            new ProviderSelection("faux", "faux-v1"),
            CancellationToken.None);
        var active = await repository.GetSnapshotAsync(
            "session-portable-1",
            9,
            CancellationToken.None);
        Assert.Equal(accepted.RunId, active.ActiveRunId);

        var events = await CollectAsync(service.RunAsync(accepted, CancellationToken.None));

        Assert.Equal("run.completed", events[^1].Type);
        Assert.NotNull(observedRequest);
        Assert.Equal(6, observedRequest.Messages.Count);
        var completed = await repository.GetSnapshotAsync(
            "session-portable-1",
            9,
            CancellationToken.None);
        Assert.Null(completed.ActiveRunId);
        Assert.Equal(15, completed.LatestSeq);
        var kinds = (await File.ReadAllLinesAsync(journalPath, CancellationToken.None))
            .Skip(1)
            .Select(GetKind)
            .ToArray();
        Assert.Single(kinds, static kind => kind == "tool.intent");
        Assert.Single(kinds, static kind => kind == "tool.result");
    }

    /// <summary>
    /// 功能：收集完整 Agent 异步事件流。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="source">事件流。</param>
    /// <returns>terminal 后的事件列表。</returns>
    private static async Task<List<AgentEvent>> CollectAsync(IAsyncEnumerable<AgentEvent> source)
    {
        var events = new List<AgentEvent>();
        await foreach (var item in source)
        {
            events.Add(item);
        }

        return events;
    }

    /// <summary>
    /// 功能：从 journal JSON 行提取 kind。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="line">完整 JSON record 行。</param>
    /// <returns>kind 字符串。</returns>
    private static string GetKind(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("kind").GetString()!;
    }

    /// <summary>
    /// 功能：从 session/get 返回的 exact event JsonElement 读取事件序号。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="portableEvent">已验证事件 envelope。</param>
    /// <returns>event.seq。</returns>
    private static long GetEventSequence(JsonElement portableEvent)
    {
        return portableEvent.GetProperty("seq").GetInt64();
    }

    /// <summary>
    /// 功能：从完整 journal 提取 Provider attempt 状态及其与 run.terminal 的持久化顺序。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="lines">包含 header 的完整 JSONL 行。</param>
    /// <returns>有序 attempt 状态、最后 attempt 行和 run 终态行索引。</returns>
    private static ProviderAttemptTrace ReadProviderAttemptTrace(string[] lines)
    {
        var statuses = new List<string>();
        var lastAttemptIndex = -1;
        var runTerminalIndex = -1;
        for (var index = 1; index < lines.Length; index++)
        {
            using var document = JsonDocument.Parse(lines[index]);
            var root = document.RootElement;
            var kind = root.GetProperty("kind").GetString();
            if (kind == "provider.attempt")
            {
                var data = root.GetProperty("data");
                Assert.True(data.TryGetProperty("runId", out _));
                Assert.True(data.TryGetProperty("turnId", out _));
                Assert.True(data.TryGetProperty("attempt", out _));
                statuses.Add(data.GetProperty("status").GetString()!);
                lastAttemptIndex = index;
            }
            else if (kind == "run.terminal")
            {
                runTerminalIndex = index;
            }
        }

        return new ProviderAttemptTrace(statuses, lastAttemptIndex, runTerminalIndex);
    }

    /// <summary>
    /// 功能：保存测试读取的 Provider attempt 状态和关键 journal 顺序位置。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="Statuses">按 journal 顺序出现的 attempt 状态。</param>
    /// <param name="LastAttemptIndex">最后 attempt 记录行索引。</param>
    /// <param name="RunTerminalIndex">run.terminal 记录行索引。</param>
    private sealed record ProviderAttemptTrace(
        IReadOnlyList<string> Statuses,
        int LastAttemptIndex,
        int RunTerminalIndex);

    /// <summary>
    /// 功能：为 Agent attempt 终态测试产生不含敏感数据的受控 Provider 异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class ToolCapabilityProvider : IProvider
    {
        /// <summary>
        /// 功能：取得不支持工具的测试 Provider ID。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        public string Id => "tool-capability-provider";

        /// <summary>
        /// 功能：取得测试 Provider 的固定模型列表。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        public IReadOnlyList<string> Models { get; } = ["plain-model"];

        /// <summary>
        /// 功能：显式拒绝 function tool 定义。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        public bool SupportsTools => false;

        /// <summary>
        /// 功能：保存 Agent 最终交给测试 Provider 的请求。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        internal ProviderRequest? ObservedRequest { get; private set; }

        /// <summary>
        /// 功能：只接受固定测试模型。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="modelId">待验证模型。</param>
        /// <returns>恰为 plain-model 时返回 true。</returns>
        public bool SupportsModel(string modelId)
        {
            return modelId == "plain-model";
        }

        /// <summary>
        /// 功能：捕获请求并返回一条确定性文本与用量信号。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="request">待检查的 Provider 请求。</param>
        /// <param name="cancellationToken">测试取消信号。</param>
        /// <returns>不联网的确定性异步信号流。</returns>
        public async IAsyncEnumerable<ProviderSignal> StreamAsync(
            ProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ObservedRequest = request;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ProviderTextSignal("plain response");
            yield return new ProviderUsageSignal(new Usage(1, 1, 2));
        }
    }

    private sealed class FailingProvider : IProvider
    {
        private readonly ProviderOperationException failure;

        /// <summary>
        /// 功能：创建映射到 failed 或 interrupted 的测试 Provider。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="terminalStatus">Provider 异常终态。</param>
        internal FailingProvider(string terminalStatus)
        {
            failure = new ProviderOperationException(
                new PortableError(
                    -32005,
                    "synthetic provider failure",
                    false,
                    new ErrorDetails("provider_test_failure")),
                terminalStatus);
        }

        /// <summary>
        /// 功能：取得仅供测试 registry 使用的 Provider ID。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        public string Id => "test-provider";

        /// <summary>
        /// 功能：取得测试 Provider 的动态模型清单标记。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        public IReadOnlyList<string> Models { get; } = Array.Empty<string>();

        /// <summary>
        /// 功能：仅接受固定 test-model，避免测试掩盖模型校验。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="modelId">待验证模型。</param>
        /// <returns>恰为 test-model 时返回 true。</returns>
        public bool SupportsModel(string modelId)
        {
            return modelId == "test-model";
        }

        /// <summary>
        /// 功能：返回在首次异步边界后抛出结构化错误的测试信号流。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="request">已验证公共请求。</param>
        /// <param name="cancellationToken">测试取消信号。</param>
        /// <returns>不产生正常信号的失败异步流。</returns>
        public IAsyncEnumerable<ProviderSignal> StreamAsync(
            ProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            return FailAsync(cancellationToken);
        }

        /// <summary>
        /// 功能：经过可取消异步调度点后抛出预配置 Provider 错误。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="cancellationToken">测试取消信号。</param>
        /// <returns>不产生元素的异步流。</returns>
        private async IAsyncEnumerable<ProviderSignal> FailAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (!cancellationToken.IsCancellationRequested)
            {
                throw failure;
            }

            yield break;
        }
    }
}
