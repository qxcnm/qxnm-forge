using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QxnmForge.Daemon;
using QxnmForge.Domain;
using QxnmForge.Provider;
using QxnmForge.Session;
using QxnmForge.Tools;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证真实 Session 摘要、工作区外归档、writer 拒绝与 tombstone 删除边界。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class SessionLifecycleServiceTests
{
    /// <summary>
    /// 功能：确认列表读取真实 header/首条用户消息/最后完整记录，损坏项使用固定安全摘要。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ListUsesRealJournalAndSafelyProjectsCorruption()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace-project")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var sessionsRoot = Path.Combine(stateRoot, "sessions");
        var journalPath = await CreateSessionAsync(
            sessionsRoot,
            "session-real",
            workspace,
            "  实现   设置中心\n第二行  ");
        await File.AppendAllTextAsync(journalPath, "{\"incomplete\":true}", Encoding.UTF8);
        var corruptDirectory = Directory.CreateDirectory(
            Path.Combine(sessionsRoot, "session-corrupt")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(corruptDirectory, "journal.jsonl"),
            "{\"kind\":\"session\"}\n",
            Encoding.UTF8);
        await using var repository = new SessionRepository(sessionsRoot, workspace);
        var service = new SessionLifecycleService(stateRoot, workspace, repository);

        var summaries = service.List();

        Assert.Equal(2, summaries.Count);
        var valid = Assert.Single(summaries, static summary => summary.SessionId == "session-real");
        Assert.Equal("实现 设置中心 第二行", valid.Title);
        Assert.Equal("workspace-project", valid.Project);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, valid.UpdatedAt);
        Assert.False(valid.Archived);
        Assert.Null(valid.Status);
        var corrupt = Assert.Single(summaries, static summary => summary.SessionId == "session-corrupt");
        Assert.Equal("会话数据不可用", corrupt.Title);
        Assert.Equal("未知项目", corrupt.Project);
        Assert.Equal(DateTimeOffset.UnixEpoch, corrupt.UpdatedAt);
    }

    /// <summary>
    /// 功能：确认列表从 committed journal 重建多审批状态，并对重复 resolution 固定降级。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ListProjectsDurableRunAndApprovalStatus()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var sessionsRoot = Path.Combine(stateRoot, "sessions");
        var journalPath = await CreateSessionAsync(
            sessionsRoot,
            "session-approval",
            workspace,
            "Approve durable write");
        await using var repository = new SessionRepository(sessionsRoot, workspace);
        var service = new SessionLifecycleService(stateRoot, workspace, repository);
        await using var journal = await PortableSessionJournal.OpenAsync(
            sessionsRoot,
            "session-approval",
            workspace,
            CancellationToken.None);
        string[] approvalChoices = ["allow_once", "deny"];

        await journal.AppendAsync(
            "run.accepted",
            new
            {
                RunId = "run-approval",
                InputMessageId = "message-session-approval",
                Provider = new { Id = "faux", ModelId = "faux-v1" },
            },
            CancellationToken.None);
        Assert.Equal("active", Assert.Single(service.List()).Status);

        await journal.AppendAsync(
            "approval.requested",
            new
            {
                RunId = "run-approval",
                Approval = new
                {
                    ApprovalId = "approval-1",
                    ToolCallId = "tool-call-1",
                    Operation = "file.write",
                    Arguments = new { Path = "approval.txt" },
                    OperationHash = new string('a', 64),
                    Risk = "medium",
                    Resources = new[] { new { Kind = "path", Value = "approval.txt" } },
                    Choices = approvalChoices,
                    ExpiresAt = "2099-07-22T00:00:00Z",
                },
            },
            CancellationToken.None);
        await journal.AppendAsync(
            "approval.requested",
            new
            {
                RunId = "run-approval",
                Approval = new
                {
                    ApprovalId = "approval-2",
                    ToolCallId = "tool-call-2",
                    Operation = "file.write",
                    Arguments = new { Path = "approval-two.txt" },
                    OperationHash = new string('b', 64),
                    Risk = "medium",
                    Resources = new[] { new { Kind = "path", Value = "approval-two.txt" } },
                    Choices = approvalChoices,
                    ExpiresAt = "2099-07-22T00:00:00Z",
                },
            },
            CancellationToken.None);
        Assert.Equal("approval", Assert.Single(service.List()).Status);

        await journal.AppendAsync(
            "approval.resolved",
            new
            {
                RunId = "run-approval",
                ApprovalId = "approval-1",
                Decision = new { Choice = "deny" },
                ResolutionSource = "client",
            },
            CancellationToken.None);
        Assert.Equal("approval", Assert.Single(service.List()).Status);

        await journal.AppendAsync(
            "approval.resolved",
            new
            {
                RunId = "run-approval",
                ApprovalId = "approval-2",
                Decision = new { Choice = "deny" },
                ResolutionSource = "client",
            },
            CancellationToken.None);
        Assert.Equal("active", Assert.Single(service.List()).Status);

        await journal.AppendAsync(
            "run.terminal",
            new { RunId = "run-approval", Status = "cancelled" },
            CancellationToken.None);
        Assert.Null(Assert.Single(service.List()).Status);
        await journal.DisposeAsync();

        await AppendRawRecordAsync(
            journalPath,
            "session-approval",
            "approval.resolved",
            "record-duplicate-resolution",
            new
            {
                runId = "run-approval",
                approvalId = "approval-2",
                decision = new { choice = "deny" },
                resolutionSource = "client",
            });
        var corrupt = Assert.Single(service.List());
        Assert.Equal("会话数据不可用", corrupt.Title);
        Assert.Equal("未知项目", corrupt.Project);
        Assert.Null(corrupt.Status);

        var reusedJournalPath = await CreateSessionAsync(
            sessionsRoot,
            "session-reused-run",
            workspace,
            "Reject reused run id");
        await using (var reusedJournal = await PortableSessionJournal.OpenAsync(
                         sessionsRoot,
                         "session-reused-run",
                         workspace,
                         CancellationToken.None))
        {
            await reusedJournal.AppendAsync(
                "run.accepted",
                new
                {
                    RunId = "run-reused",
                    InputMessageId = "message-session-reused-run",
                    Provider = new { Id = "faux", ModelId = "faux-v1" },
                },
                CancellationToken.None);
            await reusedJournal.AppendAsync(
                "run.terminal",
                new { RunId = "run-reused", Status = "cancelled" },
                CancellationToken.None);
        }

        await AppendRawRecordAsync(
            reusedJournalPath,
            "session-reused-run",
            "run.accepted",
            "record-reused-run",
            new
            {
                runId = "run-reused",
                inputMessageId = "message-session-reused-run",
                provider = new { id = "faux", modelId = "faux-v1" },
            });
        var reused = Assert.Single(
            service.List(),
            static summary => summary.SessionId == "session-reused-run");
        Assert.Equal("会话数据不可用", reused.Title);
        Assert.Null(reused.Status);
    }

    /// <summary>
    /// 功能：确认 archive/restore 跨服务持久化，且两种 mutation 不改变 journal 任一字节。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ArchivePersistsOutsideWorkspaceWithoutChangingJournal()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var sessionsRoot = Path.Combine(stateRoot, "sessions");
        var journalPath = await CreateSessionAsync(
            sessionsRoot,
            "session-archive",
            workspace,
            "归档这个会话");
        var before = await File.ReadAllBytesAsync(journalPath);
        var beforeHash = SHA256.HashData(before);
        await using var firstRepository = new SessionRepository(sessionsRoot, workspace);
        var first = new SessionLifecycleService(stateRoot, workspace, firstRepository);

        var archived = await first.ArchiveAsync("session-archive");

        Assert.True(archived.Archived);
        Assert.Equal(before, await File.ReadAllBytesAsync(journalPath));
        Assert.Equal(beforeHash, SHA256.HashData(await File.ReadAllBytesAsync(journalPath)));
        var archivePath = Path.Combine(
            stateRoot,
            "session-lifecycle",
            "archive-state.json");
        var archiveText = await File.ReadAllTextAsync(archivePath);
        Assert.Contains("session-archive", archiveText, StringComparison.Ordinal);
        Assert.DoesNotContain("归档这个会话", archiveText, StringComparison.Ordinal);
        Assert.False(archivePath.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        var archivedAgain = await first.ArchiveAsync("session-archive");
        Assert.True(archivedAgain.Archived);
        Assert.Equal(archiveText, await File.ReadAllTextAsync(archivePath));

        await using var secondRepository = new SessionRepository(sessionsRoot, workspace);
        var second = new SessionLifecycleService(stateRoot, workspace, secondRepository);
        Assert.True(Assert.Single(second.List()).Archived);
        var restored = await second.RestoreAsync("session-archive");
        Assert.False(restored.Archived);
        var restoredAgain = await second.RestoreAsync("session-archive");
        Assert.False(restoredAgain.Archived);
        Assert.Equal(before, await File.ReadAllBytesAsync(journalPath));
        Assert.False(Assert.Single(second.List()).Archived);
    }

    /// <summary>
    /// 功能：确认 idle repository writer 可安全释放，而另一个进程语义的外部 writer 仍阻止 mutation。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task MutationsReleaseIdleTrackedWriterAndRejectExternalWriter()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var sessionsRoot = Path.Combine(stateRoot, "sessions");
        _ = await CreateSessionAsync(sessionsRoot, "session-tracked", workspace, "Tracked");
        _ = await CreateSessionAsync(sessionsRoot, "session-external", workspace, "External");
        await using var repository = new SessionRepository(sessionsRoot, workspace);
        var service = new SessionLifecycleService(stateRoot, workspace, repository);
        _ = await repository.GetAsync("session-tracked");

        var archived = await service.ArchiveAsync("session-tracked");
        Assert.True(archived.Archived);

        await using var external = await PortableSessionJournal.OpenAsync(
            sessionsRoot,
            "session-external",
            workspace,
            CancellationToken.None);
        var externalError = await Assert.ThrowsAsync<SessionLifecycleException>(() =>
            service.DeleteAsync("session-external"));
        Assert.Equal("session_busy", externalError.Error.Details.Kind);
        Assert.True(Directory.Exists(Path.Combine(sessionsRoot, "session-external")));
    }

    /// <summary>
    /// 功能：确认生命周期 reservation 拒绝并发重开，StateGate 与 pending faux 状态不会被静默丢弃。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task LifecycleReservationRejectsConcurrentUseAndPreservesPendingState()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var sessionsRoot = Path.Combine(stateRoot, "sessions");
        _ = await CreateSessionAsync(sessionsRoot, "session-gated", workspace, "Gated");
        await using var repository = new SessionRepository(sessionsRoot, workspace);
        var service = new SessionLifecycleService(stateRoot, workspace, repository);
        using (var runtimeUse = await repository.AcquireRuntimeUseAsync(
                   "session-gated",
                   waitForStateGate: true))
        {
            var busy = await Assert.ThrowsAsync<SessionLifecycleException>(() =>
                service.ArchiveAsync("session-gated"));
            Assert.Equal("session_busy", busy.Error.Details.Kind);
        }

        using (var reservation = await repository.TryReserveLifecycleMutationAsync("session-gated"))
        {
            Assert.NotNull(reservation);
            var concurrent = await Assert.ThrowsAsync<SessionMutationException>(() =>
                repository.GetAsync("session-gated"));
            Assert.Equal("session_busy", concurrent.Error.Details.Kind);
        }

        await repository.ConfigureFauxAsync(
            "session-gated",
            new FauxScenario(
                "0.1",
                "pending",
                0,
                [new FauxTextStep("pending")],
                Usage.Zero));
        var pending = await Assert.ThrowsAsync<SessionLifecycleException>(() =>
            service.DeleteAsync("session-gated"));
        Assert.Equal("session_busy", pending.Error.Details.Kind);
        Assert.True(Directory.Exists(Path.Combine(sessionsRoot, "session-gated")));
    }

    /// <summary>
    /// 功能：确认健康 Session 先移入 tombstone 后完整删除，并同步清理归档元数据。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task DeleteRemovesOnlyValidatedTombstoneTree()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var sessionsRoot = Path.Combine(stateRoot, "sessions");
        _ = await CreateSessionAsync(sessionsRoot, "session-delete", workspace, "Delete me");
        var artifactPath = Path.Combine(
            sessionsRoot,
            "session-delete",
            "artifacts",
            "artifact-test");
        await File.WriteAllTextAsync(artifactPath, "safe artifact", Encoding.UTF8);
        await using var repository = new SessionRepository(sessionsRoot, workspace);
        var service = new SessionLifecycleService(stateRoot, workspace, repository);
        _ = await service.ArchiveAsync("session-delete");

        await service.DeleteAsync("session-delete");

        Assert.False(Directory.Exists(Path.Combine(sessionsRoot, "session-delete")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            Path.Combine(sessionsRoot, ".session-tombstones")));
        Assert.Empty(service.List());
        var archive = await File.ReadAllTextAsync(Path.Combine(
            stateRoot,
            "session-lifecycle",
            "archive-state.json"));
        Assert.DoesNotContain("session-delete", archive, StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：确认 tombstone 遍历遇到 symlink 时保守失败，且绝不删除链接外部目标。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task DeleteRejectsSymlinkWithoutFollowingExternalTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var sessionsRoot = Path.Combine(stateRoot, "sessions");
        _ = await CreateSessionAsync(sessionsRoot, "session-link", workspace, "Link safety");
        var outside = Path.Combine(temporary.Path, "outside.txt");
        await File.WriteAllTextAsync(outside, "must survive", Encoding.UTF8);
        File.CreateSymbolicLink(
            Path.Combine(sessionsRoot, "session-link", "artifacts", "linked"),
            outside);
        await using var repository = new SessionRepository(sessionsRoot, workspace);
        var service = new SessionLifecycleService(stateRoot, workspace, repository);

        var exception = await Assert.ThrowsAsync<SessionLifecycleException>(() =>
            service.DeleteAsync("session-link"));

        Assert.Equal("session_delete_unsafe", exception.Error.Details.Kind);
        Assert.Equal("must survive", await File.ReadAllTextAsync(outside));
        Assert.True(Directory.Exists(Path.Combine(sessionsRoot, "session-link")));
        Assert.Empty(Directory.EnumerateDirectories(
            Path.Combine(sessionsRoot, ".session-tombstones")));
    }

    /// <summary>
    /// 功能：确认进程在原子 move 后中断时，相同 Session ID 的 delete 会识别固定 tombstone 并续删。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task DeleteResumesDeterministicTombstoneAfterInterruptedMove()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var sessionsRoot = Path.Combine(stateRoot, "sessions");
        _ = await CreateSessionAsync(sessionsRoot, "session-resume", workspace, "Resume delete");
        await using var repository = new SessionRepository(sessionsRoot, workspace);
        var service = new SessionLifecycleService(stateRoot, workspace, repository);
        _ = await service.ArchiveAsync("session-resume");
        var tombstone = Path.Combine(sessionsRoot, ".session-tombstones", "session-resume");
        Directory.Move(Path.Combine(sessionsRoot, "session-resume"), tombstone);
        _ = await Assert.ThrowsAsync<IOException>(() => repository.GetAsync("session-resume"));

        await service.DeleteAsync("session-resume");

        Assert.False(Directory.Exists(tombstone));
        Assert.Empty(service.List());
        var archive = await File.ReadAllTextAsync(Path.Combine(
            stateRoot,
            "session-lifecycle",
            "archive-state.json"));
        Assert.DoesNotContain("session-resume", archive, StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：确认有效 header 提供所有权证明后，即使后续 durable 记录损坏也仍可安全删除。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task DeleteAllowsOwnedSessionWithCorruptRecordBody()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var sessionsRoot = Path.Combine(stateRoot, "sessions");
        var journal = await CreateSessionAsync(
            sessionsRoot,
            "session-corrupt-delete",
            workspace,
            "Corrupt later");
        await File.AppendAllTextAsync(journal, "{\"broken\":true}\n", Encoding.UTF8);
        await using var repository = new SessionRepository(sessionsRoot, workspace);
        var service = new SessionLifecycleService(stateRoot, workspace, repository);
        Assert.Equal("会话数据不可用", Assert.Single(service.List()).Title);

        await service.DeleteAsync("session-corrupt-delete");

        Assert.Empty(service.List());
        Assert.False(Directory.Exists(Path.Combine(sessionsRoot, "session-corrupt-delete")));
    }

    /// <summary>
    /// 功能：确认 Session 固定隔离在 stateRoot/sessions，合法保留字 ID 不能删除状态根同名目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task SessionIdCannotAliasApplicationStateDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var sessionsRoot = Path.Combine(stateRoot, "sessions");
        var credentialDirectory = Directory.CreateDirectory(Path.Combine(stateRoot, "credentials")).FullName;
        var sentinel = Path.Combine(credentialDirectory, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "must survive", Encoding.UTF8);
        _ = await CreateSessionAsync(sessionsRoot, "credentials", workspace, "Same public ID");
        await using var repository = new SessionRepository(sessionsRoot, workspace);
        var service = new SessionLifecycleService(stateRoot, workspace, repository);

        await service.DeleteAsync("credentials");

        Assert.Equal("must survive", await File.ReadAllTextAsync(sentinel));
        Assert.True(Directory.Exists(credentialDirectory));
    }

    /// <summary>
    /// 功能：确认生命周期服务拒绝与 repository 不同的 workspace，且不会先创建状态目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ConstructorRejectsWorkspaceMismatchBeforeCreatingState()
    {
        using var temporary = new TemporaryDirectory();
        var firstWorkspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace-a")).FullName;
        var secondWorkspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace-b")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        await using var repository = new SessionRepository(
            Path.Combine(stateRoot, "sessions"),
            firstWorkspace);

        var exception = Assert.Throws<SessionLifecycleException>(() =>
            new SessionLifecycleService(stateRoot, secondWorkspace, repository));

        Assert.Equal("session_lifecycle_store_invalid", exception.Error.Details.Kind);
        Assert.False(Directory.Exists(stateRoot));
    }

    /// <summary>
    /// 功能：确认 session/list 使用有界 cursor 分页，终页显式返回 nextCursor null 并拒绝伪造 cursor。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ListRpcUsesBoundedCursorPagesWithExplicitTerminalNull()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var sessionsRoot = Path.Combine(stateRoot, "sessions");
        foreach (var sessionId in new[] { "session-page-a", "session-page-b", "session-page-c" })
        {
            _ = await CreateSessionAsync(sessionsRoot, sessionId, workspace, sessionId);
        }

        await using var repository = new SessionRepository(sessionsRoot, workspace);
        await using var registry = new ProviderRegistry([new FauxProvider()]);
        using var tools = new ToolRegistry(workspace);
        var lifecycle = new SessionLifecycleService(stateRoot, workspace, repository);
        var daemon = new StdioDaemon(
            repository,
            new QxnmForge.Agent.AgentService(repository, registry, tools),
            conformanceMode: false,
            profiles: null,
            providerConnections: null,
            lifecycle);
        var requests = string.Join('\n',
            InitializeRequest("init"),
            "{\"jsonrpc\":\"2.0\",\"id\":\"page-1\",\"method\":\"session/list\",\"params\":{\"limit\":2}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"page-2\",\"method\":\"session/list\",\"params\":{\"cursor\":\"v1:2\",\"limit\":2}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"bad\",\"method\":\"session/list\",\"params\":{\"cursor\":\"forged\"}}") + "\n";

        var frames = await ExchangeAsync(daemon, requests);

        Assert.Equal(4, frames.Count);
        var first = frames[1].GetProperty("result");
        Assert.Equal(2, first.GetProperty("sessions").GetArrayLength());
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        Assert.Equal("v1:2", first.GetProperty("nextCursor").GetString());
        var second = frames[2].GetProperty("result");
        Assert.Single(second.GetProperty("sessions").EnumerateArray());
        Assert.False(second.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        Assert.Equal(
            "invalid_params",
            frames[3].GetProperty("error").GetProperty("details").GetProperty("kind").GetString());
    }

    /// <summary>
    /// 功能：确认四个生命周期 RPC 一次性广告、closed 参数严格且 wire shape 与 UI 一致。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task DaemonAdvertisesAtomicLifecycleRpcWithExactShape()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var sessionsRoot = Path.Combine(stateRoot, "sessions");
        _ = await CreateSessionAsync(sessionsRoot, "session-rpc", workspace, "RPC session");
        await using var repository = new SessionRepository(sessionsRoot, workspace);
        await using var registry = new ProviderRegistry([new FauxProvider()]);
        using var tools = new ToolRegistry(workspace);
        var lifecycle = new SessionLifecycleService(stateRoot, workspace, repository);
        var daemon = new StdioDaemon(
            repository,
            new QxnmForge.Agent.AgentService(repository, registry, tools),
            conformanceMode: false,
            profiles: null,
            providerConnections: null,
            lifecycle);
        var requests = string.Join('\n',
            InitializeRequest("init"),
            "{\"jsonrpc\":\"2.0\",\"id\":\"list\",\"method\":\"session/list\",\"params\":{}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"archive\",\"method\":\"session/archive\",\"params\":{\"sessionId\":\"session-rpc\"}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"restore\",\"method\":\"session/restore\",\"params\":{\"sessionId\":\"session-rpc\"}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"unknown\",\"method\":\"session/archive\",\"params\":{\"sessionId\":\"session-rpc\",\"extra\":true}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"delete\",\"method\":\"session/delete\",\"params\":{\"sessionId\":\"session-rpc\"}}") + "\n";

        var frames = await ExchangeAsync(daemon, requests);

        Assert.Equal(6, frames.Count);
        var methods = frames[0].GetProperty("result").GetProperty("capabilities").GetProperty("methods");
        Assert.Contains(methods.EnumerateArray(), static item => item.GetString() == "session/list");
        Assert.Contains(methods.EnumerateArray(), static item => item.GetString() == "session/archive");
        Assert.Contains(methods.EnumerateArray(), static item => item.GetString() == "session/restore");
        Assert.Contains(methods.EnumerateArray(), static item => item.GetString() == "session/delete");
        var listed = Assert.Single(frames[1]
            .GetProperty("result")
            .GetProperty("sessions")
            .EnumerateArray());
        Assert.Equal("session-rpc", listed.GetProperty("sessionId").GetString());
        Assert.Equal("RPC session", listed.GetProperty("title").GetString());
        Assert.Equal("workspace", listed.GetProperty("project").GetString());
        Assert.False(listed.GetProperty("archived").GetBoolean());
        Assert.False(listed.TryGetProperty("status", out _));
        Assert.True(frames[2].GetProperty("result").GetProperty("session").GetProperty("archived").GetBoolean());
        Assert.False(frames[3].GetProperty("result").GetProperty("session").GetProperty("archived").GetBoolean());
        Assert.Equal(
            "invalid_params",
            frames[4].GetProperty("error").GetProperty("details").GetProperty("kind").GetString());
        Assert.True(frames[5].GetProperty("result").GetProperty("deleted").GetBoolean());
    }

    /// <summary>
    /// 功能：创建一个 header、首条用户消息与末条 metadata 均 durable 的真实 Session。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionsRoot">测试状态根。</param>
    /// <param name="sessionId">目标 Session ID。</param>
    /// <param name="workspace">header workspace。</param>
    /// <param name="title">首条用户文本。</param>
    /// <returns>关闭 writer 后的 journal 路径。</returns>
    private static async Task<string> CreateSessionAsync(
        string sessionsRoot,
        string sessionId,
        string workspace,
        string title)
    {
        string journalPath;
        await using (var journal = await PortableSessionJournal.OpenAsync(
                         sessionsRoot,
                         sessionId,
                         workspace,
                         CancellationToken.None))
        {
            journalPath = journal.JournalPath;
            await journal.AppendAsync(
                "message.appended",
                new
                {
                    Message = new UserMessage(
                        "message-" + sessionId,
                        "user",
                        [new TextContent(title)],
                        DateTimeOffset.UtcNow),
                    RunId = "run-" + sessionId,
                },
                CancellationToken.None);
            await journal.AppendAsync(
                "session.metadata",
                new { Name = "updated" },
                CancellationToken.None);
        }

        return journalPath;
    }

    /// <summary>
    /// 功能：在关闭 writer 后追加 envelope 合法的原始记录，用于验证列表对语义损坏 journal 的降级。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="journalPath">目标测试 journal。</param>
    /// <param name="sessionId">record 所属 Session ID。</param>
    /// <param name="kind">已冻结的 record kind。</param>
    /// <param name="recordId">本记录唯一 opaque ID。</param>
    /// <param name="data">保持协议字段大小写的 record data。</param>
    /// <returns>记录完成追加后的异步操作。</returns>
    private static async Task AppendRawRecordAsync(
        string journalPath,
        string sessionId,
        string kind,
        string recordId,
        object data)
    {
        var lines = await File.ReadAllLinesAsync(journalPath, Encoding.UTF8);
        using var lastRecord = JsonDocument.Parse(lines[^1]);
        var record = JsonSerializer.Serialize(
            new
            {
                schemaVersion = "0.1",
                kind,
                recordId,
                sessionId,
                seq = lastRecord.RootElement.GetProperty("seq").GetInt64() + 1,
                parentId = lastRecord.RootElement.GetProperty("recordId").GetString(),
                time = "2099-07-22T00:00:00Z",
                data,
            });
        await File.AppendAllTextAsync(journalPath, record + "\n", Encoding.UTF8);
    }

    /// <summary>
    /// 功能：构造测试客户端 initialize 请求。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="id">JSON-RPC 请求 ID。</param>
    /// <returns>单行 initialize JSON。</returns>
    private static string InitializeRequest(string id)
    {
        return "{\"jsonrpc\":\"2.0\",\"id\":\"" + id +
            "\",\"method\":\"initialize\",\"params\":{\"protocolVersions\":[\"0.1\"],\"client\":{\"name\":\"test\",\"version\":\"0.1\"},\"capabilities\":{}}}";
    }

    /// <summary>
    /// 功能：运行一次内存 NDJSON 连接并克隆全部响应帧。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="daemon">待测 daemon。</param>
    /// <param name="requests">以 LF 结尾的请求流。</param>
    /// <returns>生命周期独立的响应 JSON 元素。</returns>
    private static async Task<IReadOnlyList<JsonElement>> ExchangeAsync(
        StdioDaemon daemon,
        string requests)
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(requests));
        await using var output = new MemoryStream();
        await daemon.RunAsync(input, output, CancellationToken.None);
        return Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .ToArray();
    }
}
