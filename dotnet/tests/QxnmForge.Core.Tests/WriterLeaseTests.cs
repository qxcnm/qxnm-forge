using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using QxnmForge.Session;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 .NET 原生跨语言 writer witness lease 的竞争、恶意输入、stale 接管与安全释放。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class WriterLeaseTests
{
    /// <summary>
    /// 功能：确认 live journal 发布严格 owner，竞争者得到 session_locked，clean release 保留 advisory target。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task LiveOwnerLocksContenderAndCleanReleaseRemovesOnlyLeaseDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessions = Path.Combine(temporary.Path, "sessions");
        var sessionDirectory = Path.Combine(sessions, "session-live-owner");
        var journal = await PortableSessionJournal.OpenAsync(
            sessions,
            "session-live-owner",
            workspace,
            CancellationToken.None);

        var ownerPath = Path.Combine(sessionDirectory, "writer.lock.d", "owner.json");
        using (var owner = JsonDocument.Parse(await File.ReadAllBytesAsync(ownerPath, CancellationToken.None)))
        {
            var root = owner.RootElement;
            Assert.Equal(
                ["schemaVersion", "protocol", "sessionId", "token", "endpoint", "pid", "createdAt", "implementation"],
                root.EnumerateObject().Select(static property => property.Name));
            Assert.Equal("tcp-loopback-writer-lease-v1", root.GetProperty("protocol").GetString());
            Assert.Equal("session-live-owner", root.GetProperty("sessionId").GetString());
            Assert.Matches("^[0-9a-f]{64}$", root.GetProperty("token").GetString());
            Assert.Equal("127.0.0.1", root.GetProperty("endpoint").GetProperty("host").GetString());
            Assert.InRange(root.GetProperty("endpoint").GetProperty("port").GetInt32(), 1, 65_535);
            Assert.Equal("dotnet", root.GetProperty("implementation").GetProperty("language").GetString());
        }

        var exception = await Assert.ThrowsAsync<SessionWriterLeaseException>(() =>
            PortableSessionJournal.OpenAsync(
                sessions,
                "session-live-owner",
                workspace,
                CancellationToken.None));
        Assert.Equal("session_locked", exception.Kind);
        Assert.True(exception.Retryable);

        await journal.DisposeAsync();
        Assert.True(File.Exists(Path.Combine(sessionDirectory, "lock")));
        Assert.False(Directory.Exists(Path.Combine(sessionDirectory, "writer.lock.d")));
    }

    /// <summary>
    /// 功能：确认只 listen 而不 accept/response 的 owner 仍由一次成功 connect 证明为 live。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task ConnectedWithoutResponseRemainsLockedAndPreservesOwner()
    {
        using var temporary = new TemporaryDirectory();
        var setup = await CreateClosedSessionAsync(temporary.Path, "session-unresponsive");
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(backlog: 1);
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        var bytes = CreateOwnerBytes("session-unresponsive", "1".PadLeft(64, '1'), port);
        var ownerPath = await WriteOwnerBytesAsync(setup.SessionDirectory, bytes);

        var exception = await Assert.ThrowsAsync<SessionWriterLeaseException>(() =>
            PortableSessionJournal.OpenAsync(
                setup.SessionsRoot,
                "session-unresponsive",
                setup.Workspace,
                CancellationToken.None));

        Assert.Equal("session_locked", exception.Kind);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(ownerPath, CancellationToken.None));
    }

    /// <summary>
    /// 功能：确认 probe timeout、权限或网络歧义结果永不触发 stale takeover。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task AmbiguousProbeOutcomeRemainsLockedAndPreservesOwner()
    {
        using var temporary = new TemporaryDirectory();
        var sessionDirectory = Directory.CreateDirectory(Path.Combine(temporary.Path, "session-timeout")).FullName;
        var bytes = CreateOwnerBytes("session-timeout", new string('2', 64), 43_103);
        var ownerPath = await WriteOwnerBytesAsync(sessionDirectory, bytes);
        var options = CreateOptions(
            probeOverride: static (_, _) => ValueTask.FromResult(WriterLeaseProbeOutcome.Ambiguous));

        var exception = await Assert.ThrowsAsync<SessionWriterLeaseException>(() =>
            WriterLease.AcquireAsync(
                sessionDirectory,
                "session-timeout",
                options,
                CancellationToken.None));

        Assert.Equal("session_locked", exception.Kind);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(ownerPath, CancellationToken.None));
        Assert.Empty(Directory.EnumerateDirectories(sessionDirectory, "writer.stale.*"));
    }

    /// <summary>
    /// 功能：确认 crash 遗留的严格 owner 只有在 literal loopback 明确 connection refused 后才接管。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task ExplicitConnectionRefusedAllowsAtomicCrashTakeover()
    {
        using var temporary = new TemporaryDirectory();
        var setup = await CreateClosedSessionAsync(temporary.Path, "session-crash");
        using var refusedGuard = CreateBoundNonListeningSocket();
        var port = ((IPEndPoint)refusedGuard.LocalEndPoint!).Port;
        var staleToken = new string('3', 64);
        _ = await WriteOwnerBytesAsync(
            setup.SessionDirectory,
            CreateOwnerBytes("session-crash", staleToken, port));

        await using var recovered = await PortableSessionJournal.OpenAsync(
            setup.SessionsRoot,
            "session-crash",
            setup.Workspace,
            CancellationToken.None);
        using var owner = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(setup.SessionDirectory, "writer.lock.d", "owner.json"),
            CancellationToken.None));

        Assert.NotEqual(staleToken, owner.RootElement.GetProperty("token").GetString());
        Assert.Empty(Directory.EnumerateDirectories(setup.SessionDirectory, "writer.stale.*"));
    }

    /// <summary>
    /// 功能：确认两个 contender 同时接管同一 stale owner 时恰好一个 journal writer 成功。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task SimultaneousStaleTakeoverProducesExactlyOneWriter()
    {
        using var temporary = new TemporaryDirectory();
        var setup = await CreateClosedSessionAsync(temporary.Path, "session-stale-race");
        using var refusedGuard = CreateBoundNonListeningSocket();
        var port = ((IPEndPoint)refusedGuard.LocalEndPoint!).Port;
        _ = await WriteOwnerBytesAsync(
            setup.SessionDirectory,
            CreateOwnerBytes("session-stale-race", new string('4', 64), port));
        using var start = new ManualResetEventSlim(initialState: false);
        var contenders = new[]
        {
            TryOpenAfterSignalAsync(start, setup, "session-stale-race"),
            TryOpenAfterSignalAsync(start, setup, "session-stale-race"),
        };
        start.Set();

        var results = await Task.WhenAll(contenders);
        var winner = Assert.Single(results, static result => result.Journal is not null);
        var loser = Assert.Single(results, static result => result.Error is not null);
        var error = Assert.IsType<SessionWriterLeaseException>(loser.Error);
        Assert.Equal("session_locked", error.Kind);
        Assert.Empty(Directory.EnumerateDirectories(setup.SessionDirectory, "writer.stale.*"));
        await winner.Journal!.DisposeAsync();
    }

    /// <summary>
    /// 功能：确认 grace 内 missing 与 incomplete metadata 只等待到 acquisition deadline 并保持锁定。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="incomplete">true 写 partial JSON，false 保持 owner 缺失。</param>
    /// <returns>异步测试 Task。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrIncompleteMetadataWithinGraceWaitsAndRemainsLocked(bool incomplete)
    {
        using var temporary = new TemporaryDirectory();
        var sessionDirectory = Directory.CreateDirectory(Path.Combine(temporary.Path, "session-initializing")).FullName;
        var leaseDirectory = Directory.CreateDirectory(Path.Combine(sessionDirectory, "writer.lock.d")).FullName;
        if (incomplete)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(leaseDirectory, "owner.json"),
                Encoding.UTF8.GetBytes("{\"schemaVersion\":\"0.1\",\"protocol\":"),
                CancellationToken.None);
        }

        var options = CreateOptions(
            initializationGrace: TimeSpan.FromSeconds(10),
            acquisitionTimeout: TimeSpan.FromMilliseconds(120),
            conflictRetryDelay: TimeSpan.FromMilliseconds(10));

        var exception = await Assert.ThrowsAsync<SessionWriterLeaseException>(() =>
            WriterLease.AcquireAsync(
                sessionDirectory,
                "session-initializing",
                options,
                CancellationToken.None));

        Assert.Equal("session_locked", exception.Kind);
        Assert.True(Directory.Exists(leaseDirectory));
        Assert.Equal(incomplete, File.Exists(Path.Combine(leaseDirectory, "owner.json")));
    }

    /// <summary>
    /// 功能：确认超 grace 且移动后二次验证未变化的 missing/incomplete metadata 可以安全接管。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="incomplete">true 写 partial JSON，false 保持 owner 缺失。</param>
    /// <returns>异步测试 Task。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrIncompleteMetadataAfterGraceIsTakenOver(bool incomplete)
    {
        using var temporary = new TemporaryDirectory();
        var sessionDirectory = Directory.CreateDirectory(Path.Combine(temporary.Path, "session-abandoned")).FullName;
        var leaseDirectory = Directory.CreateDirectory(Path.Combine(sessionDirectory, "writer.lock.d")).FullName;
        if (incomplete)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(leaseDirectory, "owner.json"),
                Encoding.UTF8.GetBytes("{\"schemaVersion\":\"0.1\",\"protocol\":"),
                CancellationToken.None);
        }

        Directory.SetLastWriteTimeUtc(leaseDirectory, DateTime.UtcNow - TimeSpan.FromSeconds(3));
        var lease = await WriterLease.AcquireAsync(
            sessionDirectory,
            "session-abandoned",
            cancellationToken: CancellationToken.None);
        try
        {
            Assert.True(File.Exists(Path.Combine(leaseDirectory, "owner.json")));
            Assert.Empty(Directory.EnumerateDirectories(sessionDirectory, "writer.stale.*"));
        }
        finally
        {
            await ReleaseLeaseAsync(lease);
        }
    }

    /// <summary>
    /// 功能：确认 external host owner 在 probe 前即拒绝，且绝不调用注入探针或移动 canonical 数据。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task ExternalHostInjectionIsRejectedWithoutAnyProbe()
    {
        using var temporary = new TemporaryDirectory();
        var sessionDirectory = Directory.CreateDirectory(Path.Combine(temporary.Path, "session-external")).FullName;
        var bytes = CreateOwnerBytes(
            "session-external",
            new string('5', 64),
            443,
            host: "203.0.113.7");
        var ownerPath = await WriteOwnerBytesAsync(sessionDirectory, bytes);
        var probeCount = 0;
        var options = CreateOptions(
            probeOverride: (_, _) =>
            {
                Interlocked.Increment(ref probeCount);
                return ValueTask.FromResult(WriterLeaseProbeOutcome.Connected);
            });

        var exception = await Assert.ThrowsAsync<SessionWriterLeaseException>(() =>
            WriterLease.AcquireAsync(
                sessionDirectory,
                "session-external",
                options,
                CancellationToken.None));

        Assert.Equal("writer_lock_invalid", exception.Kind);
        Assert.False(exception.Retryable);
        Assert.Equal(0, probeCount);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(ownerPath, CancellationToken.None));
    }

    /// <summary>
    /// 功能：确认 duplicate key、错误 Session、错误 token 与未知字段均严格拒绝且不 probe。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="invalidCase">要注入的恶意 owner 变化。</param>
    /// <returns>异步测试 Task。</returns>
    [Theory]
    [InlineData("duplicate")]
    [InlineData("wrong-session")]
    [InlineData("bad-token")]
    [InlineData("unknown-field")]
    [InlineData("bad-implementation")]
    [InlineData("non-finite")]
    public async Task InvalidOwnerSchemaIsRejectedAndPreserved(string invalidCase)
    {
        using var temporary = new TemporaryDirectory();
        var sessionDirectory = Directory.CreateDirectory(Path.Combine(temporary.Path, "session-invalid")).FullName;
        var validText = Encoding.UTF8.GetString(
            CreateOwnerBytes("session-invalid", new string('6', 64), 43_104)).TrimEnd('\n');
        var invalidText = invalidCase switch
        {
            "duplicate" => validText.Replace(
                "\"schemaVersion\":\"0.1\"",
                "\"schemaVersion\":\"0.1\",\"schemaVersion\":\"0.1\"",
                StringComparison.Ordinal),
            "wrong-session" => validText.Replace(
                "\"sessionId\":\"session-invalid\"",
                "\"sessionId\":\"another-session\"",
                StringComparison.Ordinal),
            "bad-token" => validText.Replace(new string('6', 64), new string('A', 64), StringComparison.Ordinal),
            "unknown-field" => validText.Insert(1, "\"unexpected\":true,"),
            "bad-implementation" => validText.Replace(
                "\"language\":\"dotnet\"",
                "\"language\":\"java\"",
                StringComparison.Ordinal),
            "non-finite" => validText.Replace("\"pid\":", "\"pid\":1e999,\"oldPid\":", StringComparison.Ordinal),
            _ => throw new InvalidOperationException("unknown invalid owner case"),
        };
        var bytes = Encoding.UTF8.GetBytes(invalidText + '\n');
        var ownerPath = await WriteOwnerBytesAsync(sessionDirectory, bytes);
        var probeCount = 0;
        var options = CreateOptions(
            probeOverride: (_, _) =>
            {
                Interlocked.Increment(ref probeCount);
                return ValueTask.FromResult(WriterLeaseProbeOutcome.Connected);
            });

        var exception = await Assert.ThrowsAsync<SessionWriterLeaseException>(() =>
            WriterLease.AcquireAsync(
                sessionDirectory,
                "session-invalid",
                options,
                CancellationToken.None));

        Assert.Equal("writer_lock_invalid", exception.Kind);
        Assert.Equal(0, probeCount);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(ownerPath, CancellationToken.None));
    }

    /// <summary>
    /// 功能：确认 invalid UTF-8 owner 立即 fail closed，而不是按 stale partial JSON 删除。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task InvalidUtf8OwnerIsRejectedAndPreserved()
    {
        using var temporary = new TemporaryDirectory();
        var sessionDirectory = Directory.CreateDirectory(Path.Combine(temporary.Path, "session-utf8")).FullName;
        byte[] bytes = [0x7B, 0x22, 0x78, 0x22, 0x3A, 0x22, 0xFF, 0x22, 0x7D];
        var ownerPath = await WriteOwnerBytesAsync(sessionDirectory, bytes);

        var exception = await Assert.ThrowsAsync<SessionWriterLeaseException>(() =>
            WriterLease.AcquireAsync(
                sessionDirectory,
                "session-utf8",
                cancellationToken: CancellationToken.None));

        Assert.Equal("writer_lock_invalid", exception.Kind);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(ownerPath, CancellationToken.None));
    }

    /// <summary>
    /// 功能：确认超过 16384 字节上限的 owner 在解析或 probe 前立即拒绝并原样保留。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task OversizedOwnerIsRejectedAndPreserved()
    {
        using var temporary = new TemporaryDirectory();
        var sessionDirectory = Directory.CreateDirectory(Path.Combine(temporary.Path, "session-oversized")).FullName;
        var bytes = Enumerable.Repeat((byte)'x', 16_385).ToArray();
        var ownerPath = await WriteOwnerBytesAsync(sessionDirectory, bytes);

        var exception = await Assert.ThrowsAsync<SessionWriterLeaseException>(() =>
            WriterLease.AcquireAsync(
                sessionDirectory,
                "session-oversized",
                cancellationToken: CancellationToken.None));

        Assert.Equal("writer_lock_invalid", exception.Kind);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(ownerPath, CancellationToken.None));
    }

    /// <summary>
    /// 功能：确认 clean release 前 token 被替换时报告 cleanup failure 并原样保留 canonical owner。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task ReleaseTokenMismatchPreservesCanonicalOwner()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessions = Path.Combine(temporary.Path, "sessions");
        var sessionDirectory = Path.Combine(sessions, "session-release-token");
        var journal = await PortableSessionJournal.OpenAsync(
            sessions,
            "session-release-token",
            workspace,
            CancellationToken.None);
        var ownerPath = Path.Combine(sessionDirectory, "writer.lock.d", "owner.json");
        var journalBefore = await File.ReadAllBytesAsync(journal.JournalPath, CancellationToken.None);
        var original = await File.ReadAllTextAsync(ownerPath, CancellationToken.None);
        using var document = JsonDocument.Parse(original);
        var originalToken = document.RootElement.GetProperty("token").GetString()!;
        var changed = original.Replace(originalToken, new string('7', 64), StringComparison.Ordinal);
        await File.WriteAllBytesAsync(ownerPath, Encoding.UTF8.GetBytes(changed), CancellationToken.None);

        var writeException = await Assert.ThrowsAsync<SessionWriterLeaseException>(() =>
            journal.AppendAsync("session.metadata", new { Name = "must-not-write" }, CancellationToken.None));
        var exception = await Assert.ThrowsAsync<SessionWriterLeaseException>(() => journal.DisposeAsync().AsTask());

        Assert.Equal("writer_lock_unavailable", writeException.Kind);
        Assert.Equal(journalBefore, await File.ReadAllBytesAsync(journal.JournalPath, CancellationToken.None));
        Assert.Equal("writer_lock_cleanup_failed", exception.Kind);
        Assert.True(File.Exists(ownerPath));
        Assert.Equal(changed, await File.ReadAllTextAsync(ownerPath, CancellationToken.None));
        await journal.DisposeAsync();
    }

    /// <summary>
    /// 功能：确认 stale 目录移动后 metadata 改变时恢复并保留不匹配目录，绝不删除。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task StaleMovedRevalidationMismatchIsNeverDeleted()
    {
        using var temporary = new TemporaryDirectory();
        var sessionDirectory = Directory.CreateDirectory(Path.Combine(temporary.Path, "session-stale-mutation")).FullName;
        var original = CreateOwnerBytes("session-stale-mutation", new string('8', 64), 43_105);
        _ = await WriteOwnerBytesAsync(sessionDirectory, original);
        byte[]? changed = null;
        var options = CreateOptions(
            probeOverride: static (_, _) => ValueTask.FromResult(WriterLeaseProbeOutcome.ConnectionRefused),
            afterStaleMove: async (moved, cancellationToken) =>
            {
                changed = CreateOwnerBytes("session-stale-mutation", new string('9', 64), 43_105);
                await File.WriteAllBytesAsync(
                    Path.Combine(moved, "owner.json"),
                    changed,
                    cancellationToken);
            });

        var exception = await Assert.ThrowsAsync<SessionWriterLeaseException>(() =>
            WriterLease.AcquireAsync(
                sessionDirectory,
                "session-stale-mutation",
                options,
                CancellationToken.None));

        Assert.Equal("session_locked", exception.Kind);
        var canonicalOwner = Path.Combine(sessionDirectory, "writer.lock.d", "owner.json");
        Assert.True(File.Exists(canonicalOwner));
        Assert.Equal(changed, await File.ReadAllBytesAsync(canonicalOwner, CancellationToken.None));
    }

    /// <summary>
    /// 功能：确认 release 目录移动后二次验证失败时不删除变更目录，并关闭本 owner listener。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task ReleaseMovedRevalidationMismatchIsNeverDeleted()
    {
        using var temporary = new TemporaryDirectory();
        var sessionDirectory = Directory.CreateDirectory(Path.Combine(temporary.Path, "session-release-mutation")).FullName;
        byte[]? changed = null;
        var options = CreateOptions(
            afterReleaseMove: async (moved, cancellationToken) =>
            {
                changed = CreateOwnerBytes("session-release-mutation", new string('a', 64), 43_106);
                await File.WriteAllBytesAsync(
                    Path.Combine(moved, "owner.json"),
                    changed,
                    cancellationToken);
            });
        var lease = await WriterLease.AcquireAsync(
            sessionDirectory,
            "session-release-mutation",
            options,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<SessionWriterLeaseException>(() =>
            lease.BeginReleaseAsync(CancellationToken.None));
        await lease.FinishReleaseAsync();

        Assert.Equal("writer_lock_cleanup_failed", exception.Kind);
        var canonicalOwner = Path.Combine(sessionDirectory, "writer.lock.d", "owner.json");
        Assert.True(File.Exists(canonicalOwner));
        Assert.Equal(changed, await File.ReadAllBytesAsync(canonicalOwner, CancellationToken.None));
    }

    /// <summary>
    /// 功能：创建一次完整 Session 后 clean close，为 crash/malicious owner 测试准备持久 journal 与 lock。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="root">测试临时根目录。</param>
    /// <param name="sessionId">目标 Session ID。</param>
    /// <returns>工作区、sessions 根和具体 Session 目录。</returns>
    private static async Task<SessionSetup> CreateClosedSessionAsync(string root, string sessionId)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        var sessions = Path.Combine(root, "sessions");
        await using (var journal = await PortableSessionJournal.OpenAsync(
                         sessions,
                         sessionId,
                         workspace,
                         CancellationToken.None))
        {
        }

        return new SessionSetup(workspace, sessions, Path.Combine(sessions, sessionId));
    }

    /// <summary>
    /// 功能：构造严格 owner JSON 测试字节，可仅替换 host 以验证禁止外连。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionId">owner Session ID。</param>
    /// <param name="token">64 位 lowercase token。</param>
    /// <param name="port">测试端口。</param>
    /// <param name="host">endpoint literal host。</param>
    /// <returns>带 LF 的紧凑 UTF-8 JSON。</returns>
    private static byte[] CreateOwnerBytes(
        string sessionId,
        string token,
        int port,
        string host = "127.0.0.1")
    {
        var owner = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = "0.1",
            ["protocol"] = "tcp-loopback-writer-lease-v1",
            ["sessionId"] = sessionId,
            ["token"] = token,
            ["endpoint"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["host"] = host,
                ["port"] = port,
            },
            ["pid"] = Math.Max(1, Environment.ProcessId),
            ["createdAt"] = "2026-07-14T00:00:00Z",
            ["implementation"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = "qxnm-forge-dotnet-test",
                ["version"] = "0.1.0",
                ["language"] = "dotnet",
            },
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(owner);
        var bytes = new byte[json.Length + 1];
        json.CopyTo(bytes, 0);
        bytes[^1] = (byte)'\n';
        return bytes;
    }

    /// <summary>
    /// 功能：在测试 Session 中创建 canonical writer.lock.d 并写入指定 raw owner bytes。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionDirectory">真实测试 Session 目录。</param>
    /// <param name="bytes">要保留并断言的 owner 原始字节。</param>
    /// <returns>owner.json 路径。</returns>
    private static async Task<string> WriteOwnerBytesAsync(string sessionDirectory, byte[] bytes)
    {
        var leaseDirectory = Directory.CreateDirectory(Path.Combine(sessionDirectory, "writer.lock.d")).FullName;
        var ownerPath = Path.Combine(leaseDirectory, "owner.json");
        await File.WriteAllBytesAsync(ownerPath, bytes, CancellationToken.None);
        return ownerPath;
    }

    /// <summary>
    /// 功能：创建已 bind 但不 listen 的 loopback socket，稳定产生 connection refused 且阻止端口复用。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>调用者持有期间保留端口的 socket。</returns>
    private static Socket CreateBoundNonListeningSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    /// <summary>
    /// 功能：构造安全默认值附近的短时测试 options，并允许确定性 probe/move 注入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="initializationGrace">可选初始化 grace。</param>
    /// <param name="acquisitionTimeout">可选总获取超时。</param>
    /// <param name="conflictRetryDelay">可选冲突重试间隔。</param>
    /// <param name="probeOverride">可选确定性 probe。</param>
    /// <param name="afterStaleMove">可选 stale moved 观察器。</param>
    /// <param name="afterReleaseMove">可选 release moved 观察器。</param>
    /// <returns>只用于本测试调用的 WriterLeaseOptions。</returns>
    private static WriterLeaseOptions CreateOptions(
        TimeSpan? initializationGrace = null,
        TimeSpan? acquisitionTimeout = null,
        TimeSpan? conflictRetryDelay = null,
        Func<int, CancellationToken, ValueTask<WriterLeaseProbeOutcome>>? probeOverride = null,
        Func<string, CancellationToken, ValueTask>? afterStaleMove = null,
        Func<string, CancellationToken, ValueTask>? afterReleaseMove = null)
    {
        return new WriterLeaseOptions(
            initializationGrace ?? TimeSpan.FromSeconds(2),
            acquisitionTimeout ?? TimeSpan.FromSeconds(3),
            conflictRetryDelay ?? TimeSpan.FromMilliseconds(25),
            TimeSpan.FromMilliseconds(250),
            probeOverride: probeOverride,
            afterStaleMove: afterStaleMove,
            afterReleaseMove: afterReleaseMove);
    }

    /// <summary>
    /// 功能：在共同信号后从独立线程打开 journal，并把成功资源或 lease 错误作为值返回。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="start">同时释放 contender 的信号。</param>
    /// <param name="setup">共享测试 Session 路径。</param>
    /// <param name="sessionId">竞争 Session ID。</param>
    /// <returns>恰含 Journal 或 Error 之一的结果。</returns>
    private static Task<OpenResult> TryOpenAfterSignalAsync(
        ManualResetEventSlim start,
        SessionSetup setup,
        string sessionId)
    {
        return Task.Run(async () =>
        {
            start.Wait(TimeSpan.FromSeconds(5));
            try
            {
                var journal = await PortableSessionJournal.OpenAsync(
                    setup.SessionsRoot,
                    sessionId,
                    setup.Workspace,
                    CancellationToken.None);
                return new OpenResult(journal, null);
            }
            catch (SessionWriterLeaseException exception)
            {
                return new OpenResult(null, exception);
            }
        });
    }

    /// <summary>
    /// 功能：按 begin release、listener close、moved delete 顺序释放直接测试 lease。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="lease">待释放 lease。</param>
    /// <returns>所有 lease 资源清理完成的 Task。</returns>
    private static async Task ReleaseLeaseAsync(WriterLease lease)
    {
        await lease.BeginReleaseAsync(CancellationToken.None);
        await lease.FinishReleaseAsync();
    }

    /// <summary>
    /// 功能：保存测试创建的工作区、sessions 根与 Session 目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="Workspace">工作区绝对路径。</param>
    /// <param name="SessionsRoot">sessions 根路径。</param>
    /// <param name="SessionDirectory">具体 Session 目录。</param>
    private sealed record SessionSetup(string Workspace, string SessionsRoot, string SessionDirectory);

    /// <summary>
    /// 功能：保存并发 contender 的唯一成功 journal 或预期 lease 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="Journal">成功取得所有权时的 journal。</param>
    /// <param name="Error">失败 contender 的 lease 错误。</param>
    private sealed record OpenResult(PortableSessionJournal? Journal, Exception? Error);
}
