using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QxnmForge.Domain;
using QxnmForge.Serialization;
using QxnmForge.Session;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 portable session header、记录 schema 形状和 append-before-ack 可观察性。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class PortableSessionJournalTests
{
    /// <summary>
    /// 功能：确认 AppendAsync 返回时 header 和记录均已成为完整 LF 终止行。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task AppendAsyncWritesPortableHeaderAndDurableRecordBeforeReturn()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessions = Path.Combine(temporary.Path, "sessions");
        await using var journal = await PortableSessionJournal.OpenAsync(
            sessions,
            "session-test",
            workspace,
            CancellationToken.None);

        var receipt = await journal.AppendAsync(
            "session.metadata",
            new { Name = "测试" },
            CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(journal.JournalPath, CancellationToken.None);
        Assert.Equal((byte)'\n', bytes[^1]);

        var lines = await File.ReadAllLinesAsync(journal.JournalPath, CancellationToken.None);
        Assert.Equal(2, lines.Length);
        using var header = JsonDocument.Parse(lines[0]);
        using var record = JsonDocument.Parse(lines[1]);
        Assert.Equal("session", header.RootElement.GetProperty("kind").GetString());
        Assert.Equal("0.1", header.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("dotnet", header.RootElement.GetProperty("createdBy").GetProperty("language").GetString());
        Assert.EndsWith("Z", header.RootElement.GetProperty("createdAt").GetString());
        Assert.Equal(receipt.RecordId, record.RootElement.GetProperty("recordId").GetString());
        Assert.Equal(1, record.RootElement.GetProperty("seq").GetInt64());
        Assert.Equal(JsonValueKind.Null, record.RootElement.GetProperty("parentId").ValueKind);
    }

    /// <summary>
    /// 功能：确认同一 session 的第二个 writer 在第一个释放前无法取得 lock。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task OpenAsyncRejectsConcurrentWriter()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessions = Path.Combine(temporary.Path, "sessions");
        await using var first = await PortableSessionJournal.OpenAsync(
            sessions,
            "session-lock",
            workspace,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<SessionWriterLeaseException>(() => PortableSessionJournal.OpenAsync(
            sessions,
            "session-lock",
            workspace,
            CancellationToken.None));
        Assert.Equal("session_locked", exception.Kind);
        Assert.True(exception.Retryable);
    }

    /// <summary>
    /// 功能：确认事件以规范核心 event.emitted 记录保存 exact envelope，并在重开后从最大事件序号继续。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task AppendEventAsyncWritesCoreEventRecordAndRestoresSequence()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessions = Path.Combine(temporary.Path, "sessions");
        string journalPath;

        await using (var journal = await PortableSessionJournal.OpenAsync(
                         sessions,
                         "session-event",
                         workspace,
                         CancellationToken.None))
        {
            var portableEvent = await journal.AppendEventAsync(
                "run-event",
                null,
                "run.started",
                new { },
                CancellationToken.None);
            Assert.Equal(1, portableEvent.Seq);
            journalPath = journal.JournalPath;
        }

        var lines = await File.ReadAllLinesAsync(journalPath, CancellationToken.None);
        Assert.Equal(2, lines.Length);
        using (var record = JsonDocument.Parse(lines[1]))
        {
            var root = record.RootElement;
            Assert.Equal("event.emitted", root.GetProperty("kind").GetString());
            var data = root.GetProperty("data");
            Assert.Equal(["event"], data.EnumerateObject().Select(static property => property.Name));
            var persistedEvent = data.GetProperty("event");
            Assert.Equal("session-event", persistedEvent.GetProperty("sessionId").GetString());
            Assert.Equal("run-event", persistedEvent.GetProperty("runId").GetString());
            Assert.False(persistedEvent.TryGetProperty("turnId", out _));
            Assert.Equal(1, persistedEvent.GetProperty("seq").GetInt64());
            Assert.Equal("run.started", persistedEvent.GetProperty("type").GetString());
            Assert.Equal(JsonValueKind.Object, persistedEvent.GetProperty("data").ValueKind);
        }

        await using var reopened = await PortableSessionJournal.OpenAsync(
            sessions,
            "session-event",
            workspace,
            CancellationToken.None);
        Assert.Equal(1, reopened.LatestEventSequence);
        var nextEvent = await reopened.AppendEventAsync(
            "run-event",
            "turn-event",
            "turn.started",
            new { },
            CancellationToken.None);
        Assert.Equal(2, nextEvent.Seq);
    }

    /// <summary>
    /// 功能：确认 session 快照的 latestSeq 与 afterSeq 都使用 event.seq，messages 不受事件过滤影响。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task GetSnapshotAsyncFiltersOnlyEventsByEventSequence()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        await using var journal = await PortableSessionJournal.OpenAsync(
            Path.Combine(temporary.Path, "sessions"),
            "session-snapshot",
            workspace,
            CancellationToken.None);
        var message = new UserMessage(
            "message-snapshot",
            "user",
            [new TextContent("保留消息")],
            DateTimeOffset.UtcNow);
        await journal.AppendAsync(
            "message.appended",
            new { Message = message, RunId = "run-snapshot" },
            CancellationToken.None);
        await journal.AppendEventAsync(
            "run-snapshot",
            null,
            "run.started",
            new { },
            CancellationToken.None);
        await journal.AppendAsync(
            "session.metadata",
            new { Name = "journal seq 不影响 event seq" },
            CancellationToken.None);
        await journal.AppendEventAsync(
            "run-snapshot",
            null,
            "run.completed",
            new { Status = "completed", Usage = Usage.Zero },
            CancellationToken.None);

        var snapshot = await journal.GetSnapshotAsync(1, CancellationToken.None);

        Assert.Equal(2, snapshot.LatestSeq);
        Assert.Null(snapshot.ActiveRunId);
        Assert.Single(snapshot.Messages);
        var portableEvent = Assert.Single(snapshot.Events);
        Assert.Equal(2, portableEvent.GetProperty("seq").GetInt64());
        Assert.Equal("run.completed", portableEvent.GetProperty("type").GetString());
    }

    /// <summary>
    /// 功能：确认即使最终记录 JSON/schema 完整，只要缺少 LF 也会逐字节备份、截断并追加一次 brand-neutral recovery extension。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task OpenAsyncTruncatesValidUnterminatedRecordAndRecoversIdempotently()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessions = Path.Combine(temporary.Path, "sessions");
        string journalPath;
        byte[] committedBytes;
        JournalAppendReceipt committedReceipt;
        await using (var journal = await PortableSessionJournal.OpenAsync(
                         sessions,
                         "session-valid-uncommitted-tail",
                         workspace,
                         CancellationToken.None))
        {
            committedReceipt = await journal.AppendAsync(
                "session.metadata",
                new { Name = "committed" },
                CancellationToken.None);
            committedBytes = await File.ReadAllBytesAsync(journal.JournalPath, CancellationToken.None);
            await journal.AppendAsync(
                "session.metadata",
                new { Name = "must-be-discarded" },
                CancellationToken.None);
            journalPath = journal.JournalPath;
        }

        var fullyTerminated = await File.ReadAllBytesAsync(journalPath, CancellationToken.None);
        Assert.Equal((byte)'\n', fullyTerminated[^1]);
        var originalBytes = fullyTerminated[..^1];
        await File.WriteAllBytesAsync(journalPath, originalBytes, CancellationToken.None);
        var originalSha256 = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
        var backupFile = "journal.recovery-" + originalSha256 + ".bak";
        var backupPath = Path.Combine(Path.GetDirectoryName(journalPath)!, backupFile);

        await using (var recovered = await PortableSessionJournal.OpenAsync(
                         sessions,
                         "session-valid-uncommitted-tail",
                         workspace,
                         CancellationToken.None))
        {
            Assert.Null((await recovered.GetSnapshotAsync(0, CancellationToken.None)).ActiveRunId);
        }

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(backupPath, CancellationToken.None));
        var recoveredBytes = await File.ReadAllBytesAsync(journalPath, CancellationToken.None);
        Assert.Equal((byte)'\n', recoveredBytes[^1]);
        Assert.True(recoveredBytes.AsSpan().StartsWith(committedBytes));
        Assert.DoesNotContain("must-be-discarded", Encoding.UTF8.GetString(recoveredBytes), StringComparison.Ordinal);
        var lines = await File.ReadAllLinesAsync(journalPath, CancellationToken.None);
        Assert.Equal(3, lines.Length);
        using (var diagnostic = JsonDocument.Parse(lines[^1]))
        {
            var root = diagnostic.RootElement;
            Assert.Equal("extension", root.GetProperty("kind").GetString());
            Assert.Equal(2, root.GetProperty("seq").GetInt64());
            Assert.Equal(committedReceipt.RecordId, root.GetProperty("parentId").GetString());
            var data = root.GetProperty("data");
            Assert.Equal("org.agent-session.recovery", data.GetProperty("namespace").GetString());
            var value = data.GetProperty("value");
            Assert.Equal("truncate_uncommitted_tail", value.GetProperty("action").GetString());
            Assert.Equal(originalBytes.Length - committedBytes.Length, value.GetProperty("discardedBytes").GetInt64());
            Assert.Equal(backupFile, value.GetProperty("backupFile").GetString());
            Assert.Equal(originalSha256, value.GetProperty("originalSha256").GetString());
        }

        await using (var reopened = await PortableSessionJournal.OpenAsync(
                         sessions,
                         "session-valid-uncommitted-tail",
                         workspace,
                         CancellationToken.None))
        {
            Assert.Equal(recoveredBytes, await File.ReadAllBytesAsync(reopened.JournalPath, CancellationToken.None));
        }

        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(journalPath)!, "journal.recovery-*.bak"));
    }

    /// <summary>
    /// 功能：确认 LF 完整的无效 UTF-8、JSON、schema 与递归 duplicate key 均 fail closed，且 journal 和 recovery 备份集合零修改。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task OpenAsyncRejectsCompleteCorruptLinesWithoutRecoveryMutation()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessions = Path.Combine(temporary.Path, "sessions");
        for (var index = 0; index < 4; index++)
        {
            var sessionId = "session-complete-corrupt-" + index;
            string journalPath;
            await using (var journal = await PortableSessionJournal.OpenAsync(
                             sessions,
                             sessionId,
                             workspace,
                             CancellationToken.None))
            {
                journalPath = journal.JournalPath;
            }

            var commonRecordPrefix =
                "{\"schemaVersion\":\"0.1\",\"kind\":\"extension\",\"recordId\":\"record-corrupt\",\"sessionId\":\"" +
                sessionId +
                "\",\"seq\":1,\"parentId\":null,\"time\":\"2026-07-17T00:00:00Z\",\"data\":{\"namespace\":\"org.example.test\",\"value\":";
            byte[] corruptLine;
            if (index == 0)
            {
                corruptLine = Encoding.UTF8.GetBytes(commonRecordPrefix + "\"~\"}}\n");
                corruptLine[Array.LastIndexOf(corruptLine, (byte)'~')] = 0xff;
            }
            else if (index == 1)
            {
                corruptLine = Encoding.UTF8.GetBytes("{\"broken\":}\n");
            }
            else if (index == 2)
            {
                corruptLine = "{}\n"u8.ToArray();
            }
            else
            {
                corruptLine = Encoding.UTF8.GetBytes(
                    commonRecordPrefix + "{\"nested\":{\"key\":1,\"key\":2}}}}\n");
            }

            await using (var stream = new FileStream(
                             journalPath,
                             FileMode.Append,
                             FileAccess.Write,
                             FileShare.Read,
                             bufferSize: 1,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(corruptLine, CancellationToken.None);
                await stream.FlushAsync(CancellationToken.None);
                stream.Flush(flushToDisk: true);
            }

            var before = await File.ReadAllBytesAsync(journalPath, CancellationToken.None);
            await Assert.ThrowsAsync<JournalCorruptException>(() => PortableSessionJournal.OpenAsync(
                sessions,
                sessionId,
                workspace,
                CancellationToken.None));
            Assert.Equal(before, await File.ReadAllBytesAsync(journalPath, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(journalPath)!, "journal.recovery-*.bak"));
        }
    }

    /// <summary>
    /// 功能：确认固定 SHA-256 basename 已存在但内容不一致时拒绝覆盖、拒绝截断并返回 JournalCorrupt。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task OpenAsyncRejectsMismatchedExistingRecoveryBackupWithoutTruncation()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessions = Path.Combine(temporary.Path, "sessions");
        string journalPath;
        await using (var journal = await PortableSessionJournal.OpenAsync(
                         sessions,
                         "session-backup-collision",
                         workspace,
                         CancellationToken.None))
        {
            journalPath = journal.JournalPath;
        }

        var committedBytes = await File.ReadAllBytesAsync(journalPath, CancellationToken.None);
        var tail = "{\"partial\":"u8.ToArray();
        var originalBytes = new byte[committedBytes.Length + tail.Length];
        committedBytes.CopyTo(originalBytes, 0);
        tail.CopyTo(originalBytes, committedBytes.Length);
        await File.WriteAllBytesAsync(journalPath, originalBytes, CancellationToken.None);
        var originalSha256 = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
        var backupPath = Path.Combine(
            Path.GetDirectoryName(journalPath)!,
            "journal.recovery-" + originalSha256 + ".bak");
        var mismatchedBackup = "not-the-journal"u8.ToArray();
        await File.WriteAllBytesAsync(backupPath, mismatchedBackup, CancellationToken.None);

        await Assert.ThrowsAsync<JournalCorruptException>(() => PortableSessionJournal.OpenAsync(
            sessions,
            "session-backup-collision",
            workspace,
            CancellationToken.None));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(journalPath, CancellationToken.None));
        Assert.Equal(mismatchedBackup, await File.ReadAllBytesAsync(backupPath, CancellationToken.None));
    }

    /// <summary>
    /// 功能：确认 .NET 可打开 Rust header、不同 JSON 格式以及含未知 extensions 的 user/assistant/tool 线性历史。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task OpenAsyncPreservesRustFormattedLinearMessageHistoryAndExtensions()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessions = Path.Combine(temporary.Path, "sessions");
        var journalPath = PortableSessionFixture.InstallRustLinearSession(sessions);
        var originalBytes = await File.ReadAllBytesAsync(journalPath, CancellationToken.None);
        await using var journal = await PortableSessionJournal.OpenAsync(
            sessions,
            "rust-linear",
            workspace,
            CancellationToken.None);

        var snapshot = await journal.GetSnapshotAsync(0, CancellationToken.None);

        Assert.Equal(0, snapshot.LatestSeq);
        Assert.Null(snapshot.ActiveRunId);
        Assert.Equal(
            ["user", "assistant", "tool", "assistant"],
            snapshot.Messages.Select(static message => message.GetProperty("role").GetString()));
        Assert.Equal(
            "用户扩展",
            snapshot.Messages[0]
                .GetProperty("content")[0]
                .GetProperty("extensions")
                .GetProperty("org.agentprotocol.content")
                .GetProperty("marker")
                .GetString());
        Assert.True(
            snapshot.Messages[1]
                .GetProperty("content")[1]
                .GetProperty("extensions")
                .GetProperty("org.agentprotocol.tool-call")
                .GetProperty("kept")
                .GetBoolean());

        await journal.AppendAsync(
            "session.metadata",
            new { Name = "continued-by-dotnet" },
            CancellationToken.None);
        var appendedBytes = await File.ReadAllBytesAsync(journalPath, CancellationToken.None);
        Assert.True(appendedBytes.AsSpan().StartsWith(originalBytes));
    }

    /// <summary>
    /// 功能：确认崩溃恢复只写 outcomeKnown=false 与 interrupted terminal，并禁止未知非幂等工具 continuation。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task OpenAsyncRecoversAmbiguousToolWithoutReplayAndClearsActiveRun()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessions = Path.Combine(temporary.Path, "sessions");
        string journalPath;
        await using (var journal = await PortableSessionJournal.OpenAsync(
                         sessions,
                         "session-recovery",
                         workspace,
                         CancellationToken.None))
        {
            await journal.AppendAsync(
                "run.accepted",
                new
                {
                    RunId = "run-recovery",
                    InputMessageId = "message-recovery",
                    Provider = new ProviderSelection("faux", "faux-v1"),
                },
                CancellationToken.None);
            await journal.AppendAsync(
                "tool.intent",
                new
                {
                    RunId = "run-recovery",
                    TurnId = "turn-recovery",
                    ToolCallId = "call-recovery",
                    Name = "file.write",
                    Arguments = new { Path = "never-written" },
                    Idempotent = false,
                    Status = "started",
                },
                CancellationToken.None);
            journalPath = journal.JournalPath;
        }

        await using var recovered = await PortableSessionJournal.OpenAsync(
            sessions,
            "session-recovery",
            workspace,
            CancellationToken.None);
        var snapshot = await recovered.GetSnapshotAsync(0, CancellationToken.None);
        Assert.Null(snapshot.ActiveRunId);

        var records = (await File.ReadAllLinesAsync(journalPath, CancellationToken.None))
            .Skip(1)
            .Select(static line => JsonDocument.Parse(line))
            .ToArray();
        try
        {
            var toolResult = Assert.Single(
                records,
                static record => record.RootElement.GetProperty("kind").GetString() == "tool.result");
            Assert.False(toolResult.RootElement.GetProperty("data").GetProperty("outcomeKnown").GetBoolean());
            var terminal = Assert.Single(
                records,
                static record => record.RootElement.GetProperty("kind").GetString() == "run.terminal");
            Assert.Equal("interrupted", terminal.RootElement.GetProperty("data").GetProperty("status").GetString());
            var kinds = records
                .Select(static record => record.RootElement.GetProperty("kind").GetString())
                .ToArray();
            Assert.True(Array.IndexOf(kinds, "tool.result") < Array.IndexOf(kinds, "run.terminal"));
        }
        finally
        {
            foreach (var record in records)
            {
                record.Dispose();
            }
        }
    }

    /// <summary>
    /// 功能：确认补写多个缺失 interrupted 事件时遵循原 terminal journal seq，而不是 RunId 字典序。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task OpenAsyncCompletesRecoveredTerminalEventsInOriginalSequence()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessions = Path.Combine(temporary.Path, "sessions");
        var error = new PortableError(
            -32005,
            "Run was interrupted by a previous process exit.",
            false,
            new ErrorDetails("recovered_interrupted_run"));
        await using (var journal = await PortableSessionJournal.OpenAsync(
                         sessions,
                         "session-event-recovery-order",
                         workspace,
                         CancellationToken.None))
        {
            foreach (var runId in new[] { "run-z-first", "run-a-second" })
            {
                await journal.AppendAsync(
                    "run.accepted",
                    new
                    {
                        RunId = runId,
                        InputMessageId = "message-" + runId,
                        Provider = new ProviderSelection("faux", "faux-v1"),
                    },
                    CancellationToken.None);
                await journal.AppendAsync(
                    "run.terminal",
                    new { RunId = runId, Status = "interrupted", Error = error },
                    CancellationToken.None);
            }
        }

        await using var recovered = await PortableSessionJournal.OpenAsync(
            sessions,
            "session-event-recovery-order",
            workspace,
            CancellationToken.None);
        var snapshot = await recovered.GetSnapshotAsync(0, CancellationToken.None);

        Assert.Equal(2, snapshot.LatestSeq);
        Assert.Equal(
            ["run-z-first", "run-a-second"],
            snapshot.Events.Select(static item => item.GetProperty("runId").GetString()));
        Assert.All(
            snapshot.Events,
            static item => Assert.Equal("run.interrupted", item.GetProperty("type").GetString()));
    }

    /// <summary>
    /// 功能：确认公共 tree fixture 按 selected chain 与最新 compaction 返回精确消息投影。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task GetSnapshotAsyncProjectsSelectedBranchAndLatestCompaction()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessionsRoot = Path.Combine(temporary.Path, "sessions");
        var journalPath = PortableSessionFixture.InstallSharedBranchCompactionSession(sessionsRoot);
        var original = await File.ReadAllBytesAsync(journalPath);
        await using var journal = await PortableSessionJournal.OpenAsync(
            sessionsRoot,
            "session-branch-compaction",
            workspace,
            CancellationToken.None);
        var snapshot = await journal.GetSnapshotAsync(0, CancellationToken.None);

        Assert.Equal("record-select-branch-a", snapshot.SelectedHeadRecordId);
        Assert.Equal("record-compaction", snapshot.CompactionRecordId);
        Assert.Equal(
            [
                "message-summary",
                "message-recent-user",
                "message-recent-assistant",
                "message-branch-a-user",
                "message-branch-a-assistant",
            ],
            snapshot.Messages.Select(static message => message.GetProperty("messageId").GetString()));
        Assert.Equal(original, await File.ReadAllBytesAsync(journalPath));
    }

    /// <summary>
    /// 功能：确认 JSON 设置把 UTC 时间编码为规范要求的 Z 后缀。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void JsonDefaultsWritesUtcTimeWithZSuffix()
    {
        var json = JsonSerializer.Serialize(
            new { Time = new DateTimeOffset(2026, 7, 10, 2, 3, 4, TimeSpan.Zero) },
            JsonDefaults.Options);
        Assert.Contains("2026-07-10T02:03:04.000Z", json, StringComparison.Ordinal);
    }
}
