using System.Text.Json;
using QxnmForge.Protocol;
using QxnmForge.Session;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 .NET portable Session branch selection、compaction pair、CAS 与零写入失败语义。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class SessionBranchCompactionTests
{
    /// <summary>
    /// 功能：确认选择 Branch B 只追加一个 control record，并切换精确 selected projection。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task SelectBranchAsyncAppendsOneRecordAndSwitchesProjection()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessionsRoot = Path.Combine(temporary.Path, "sessions");
        var journalPath = PortableSessionFixture.InstallSharedBranchCompactionSession(sessionsRoot);
        var prefix = await File.ReadAllBytesAsync(journalPath);
        await using var repository = new SessionRepository(sessionsRoot, workspace);

        var receipt = await repository.SelectBranchAsync(
            "session-branch-compaction",
            "record-select-branch-a",
            "record-branch-b-assistant",
            CancellationToken.None);
        var snapshot = await repository.GetSnapshotAsync(
            "session-branch-compaction",
            cancellationToken: CancellationToken.None);

        Assert.Equal("record-branch-b-assistant", receipt.TargetLeafRecordId);
        Assert.Equal(receipt.SelectionRecordId, receipt.SelectedHeadRecordId);
        Assert.Equal(receipt.SelectedHeadRecordId, snapshot.SelectedHeadRecordId);
        Assert.Equal("record-compaction", snapshot.CompactionRecordId);
        Assert.Equal(
            [
                "message-summary",
                "message-recent-user",
                "message-recent-assistant",
                "message-branch-b-user",
                "message-branch-b-assistant",
            ],
            snapshot.Messages.Select(static message => message.GetProperty("messageId").GetString()));
        var final = await File.ReadAllBytesAsync(journalPath);
        Assert.True(final.AsSpan().StartsWith(prefix));
        Assert.True(final.Length > prefix.Length);
        Assert.Equal(14, await CountJournalLinesAsync(journalPath));
    }

    /// <summary>
    /// 功能：确认 compact 原子追加 canonical summary 与 context.compacted，并激活三消息新投影。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task CompactAsyncAppendsDurablePairAndActivatesProjection()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessionsRoot = Path.Combine(temporary.Path, "sessions");
        var journalPath = PortableSessionFixture.InstallSharedBranchCompactionSession(sessionsRoot);
        var prefix = await File.ReadAllBytesAsync(journalPath);
        await using var repository = new SessionRepository(sessionsRoot, workspace);
        var command = ParseCompactionCommand(tokensBefore: 90, tokensAfter: 40);

        var receipt = await repository.CompactAsync(
            "session-branch-compaction",
            command,
            CancellationToken.None);
        var snapshot = await repository.GetSnapshotAsync(
            "session-branch-compaction",
            cancellationToken: CancellationToken.None);

        Assert.Equal(receipt.CompactionRecordId, receipt.SelectedHeadRecordId);
        Assert.Equal(receipt.SelectedHeadRecordId, snapshot.SelectedHeadRecordId);
        Assert.Equal(receipt.CompactionRecordId, snapshot.CompactionRecordId);
        Assert.Equal(
            [receipt.SummaryMessageId, "message-branch-a-user", "message-branch-a-assistant"],
            snapshot.Messages.Select(static message => message.GetProperty("messageId").GetString()));
        Assert.Equal(
            "Conformance summary for the selected branch.",
            snapshot.Messages[0].GetProperty("content")[0].GetProperty("text").GetString());
        var final = await File.ReadAllBytesAsync(journalPath);
        Assert.True(final.AsSpan().StartsWith(prefix));
        Assert.True(final.Length > prefix.Length);
        Assert.Equal(15, await CountJournalLinesAsync(journalPath));
    }

    /// <summary>
    /// 功能：确认 stale、unknown、invalid boundary 与 token growth 返回冻结 kind 且 journal 零字节变化。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task MutationValidationFailuresPreserveJournalBytes()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessionsRoot = Path.Combine(temporary.Path, "sessions");
        var journalPath = PortableSessionFixture.InstallSharedBranchCompactionSession(sessionsRoot);
        var original = await File.ReadAllBytesAsync(journalPath);
        await using var repository = new SessionRepository(sessionsRoot, workspace);

        var stale = await Assert.ThrowsAsync<SessionMutationException>(() => repository.SelectBranchAsync(
            "session-branch-compaction",
            "record-stale",
            "record-branch-b-assistant",
            CancellationToken.None));
        var unknown = await Assert.ThrowsAsync<SessionMutationException>(() => repository.SelectBranchAsync(
            "session-branch-compaction",
            "record-select-branch-a",
            "record-unknown",
            CancellationToken.None));
        var invalidBoundary = await Assert.ThrowsAsync<SessionMutationException>(() => repository.CompactAsync(
            "session-branch-compaction",
            ParseCompactionCommand(90, 40, "record-recent-assistant"),
            CancellationToken.None));
        var invalidTokens = await Assert.ThrowsAsync<SessionMutationException>(() => repository.CompactAsync(
            "session-branch-compaction",
            ParseCompactionCommand(40, 41),
            CancellationToken.None));

        Assert.Equal("stale_session_head", stale.Error.Details.Kind);
        Assert.Equal("record_not_found", unknown.Error.Details.Kind);
        Assert.Equal("invalid_compaction_boundary", invalidBoundary.Error.Details.Kind);
        Assert.Equal("invalid_compaction_tokens", invalidTokens.Error.Details.Kind);
        Assert.Equal(original, await File.ReadAllBytesAsync(journalPath));
    }

    /// <summary>
    /// 功能：确认已被其他状态转换占用的 StateGate 使 mutation 立即返回 session_busy 而不排队。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task ConcurrentMutationReturnsSessionBusyWithoutWaiting()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessionsRoot = Path.Combine(temporary.Path, "sessions");
        var journalPath = PortableSessionFixture.InstallSharedBranchCompactionSession(sessionsRoot);
        var original = await File.ReadAllBytesAsync(journalPath);
        await using var repository = new SessionRepository(sessionsRoot, workspace);
        var runtime = await repository.GetAsync("session-branch-compaction", CancellationToken.None);
        await runtime.StateGate.WaitAsync(CancellationToken.None);
        try
        {
            var mutation = repository.SelectBranchAsync(
                "session-branch-compaction",
                "record-select-branch-a",
                "record-branch-b-assistant",
                CancellationToken.None);
            var exception = await Assert.ThrowsAsync<SessionMutationException>(() => mutation);
            Assert.Equal("session_busy", exception.Error.Details.Kind);
            Assert.True(mutation.IsCompleted);
        }
        finally
        {
            runtime.StateGate.Release();
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(journalPath));
    }

    /// <summary>
    /// 功能：通过正式协议解析器构造测试 compaction command，并保持字段验证与生产路径一致。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="tokensBefore">压缩前 token 估算。</param>
    /// <param name="tokensAfter">压缩后 token 估算。</param>
    /// <param name="firstRetainedRecordId">retained boundary record ID。</param>
    /// <returns>生命周期独立的 Session compaction command。</returns>
    private static SessionCompactionCommand ParseCompactionCommand(
        long tokensBefore,
        long tokensAfter,
        string firstRetainedRecordId = "record-branch-a-user")
    {
        var parameters = JsonSerializer.SerializeToElement(new
        {
            SessionId = "session-branch-compaction",
            ExpectedHeadRecordId = "record-select-branch-a",
            FirstRetainedRecordId = firstRetainedRecordId,
            SummaryText = "Conformance summary for the selected branch.",
            Provider = new { Id = "faux", ModelId = "faux-v1" },
            Usage = new { InputTokens = 30, OutputTokens = 8, TotalTokens = 38 },
            TokensBefore = tokensBefore,
            TokensAfter = tokensAfter,
            Strategy = "conformance-summary-v1",
        }, QxnmForge.Serialization.JsonDefaults.Options);
        return ProtocolCodec.ParseSessionCompact(parameters).Command;
    }

    /// <summary>
    /// 功能：统计测试 journal 的完整 LF 行数，证明 mutation 追加记录数量精确。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="journalPath">测试临时 journal 路径。</param>
    /// <returns>header 加全部 records 的行数。</returns>
    private static async Task<int> CountJournalLinesAsync(string journalPath)
    {
        var lines = await File.ReadAllLinesAsync(journalPath);
        return lines.Length;
    }
}
