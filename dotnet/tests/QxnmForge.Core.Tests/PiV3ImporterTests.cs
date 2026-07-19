using System.Security.Cryptography;
using QxnmForge.Session;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 .NET 原生 PI Session v3 clean-room 导入的确定性、安全失败与通用生产映射。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class PiV3ImporterTests
{
    /// <summary>
    /// 功能：确认固定合成 source 生成与公共 journal/report 逐字节相同的 durable Session。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ConformanceFixtureProducesExactPortableBytesAsync()
    {
        using var temporary = new TemporaryDirectory();
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pi-v3-import-v0.1");
        var source = Path.Combine(temporary.Path, "source.jsonl");
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var state = Directory.CreateDirectory(Path.Combine(temporary.Path, "state")).FullName;
        File.Copy(Path.Combine(fixture, "source.pi-v3.jsonl"), source);
        var sourceBefore = await File.ReadAllBytesAsync(source);
        var hashBefore = SHA256.HashData(sourceBefore);

        var result = await PiV3Importer.ImportAsync(
            new PiV3ImportOptions(
                source,
                workspace,
                state,
                "session-import-pi-v3-fixture",
                Conformance: true));

        Assert.Equal("completed_with_warnings", result.Status);
        Assert.Equal("artifact-pi-v3-import-report", result.ReportArtifactId);
        var session = Path.Combine(state, "sessions", result.SessionId);
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(fixture, "expected.journal.jsonl")),
            await File.ReadAllBytesAsync(Path.Combine(session, "journal.jsonl")));
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(fixture, "artifacts", "import-report.json")),
            await File.ReadAllBytesAsync(Path.Combine(session, "artifacts", "artifact-pi-v3-import-report.json")));
        Assert.Equal(hashBefore, SHA256.HashData(await File.ReadAllBytesAsync(source)));
    }

    /// <summary>
    /// 功能：确认 source 中的重复 JSON key 在创建任何目标 journal 前失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task DuplicateJsonKeyLeavesNoPublishedTargetAsync()
    {
        using var temporary = new TemporaryDirectory();
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pi-v3-import-v0.1", "source.pi-v3.jsonl");
        var source = Path.Combine(temporary.Path, "duplicate.jsonl");
        var raw = await File.ReadAllTextAsync(fixture);
        await File.WriteAllTextAsync(source, raw.Replace("\"version\":3", "\"version\":3,\"version\":3", StringComparison.Ordinal));
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var state = Directory.CreateDirectory(Path.Combine(temporary.Path, "state")).FullName;

        var exception = await Assert.ThrowsAsync<PiV3ImportException>(() => PiV3Importer.ImportAsync(
            new PiV3ImportOptions(source, workspace, state, "session-duplicate", Conformance: false)));

        Assert.Equal(7, exception.ExitCode);
        Assert.Empty(Directory.EnumerateFiles(state, "journal.jsonl", SearchOption.AllDirectories));
    }

    /// <summary>
    /// 功能：确认 source 叶符号链接被 no-follow 打开策略拒绝且不发布 Session。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task SourceLeafSymlinkIsRejectedAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pi-v3-import-v0.1", "source.pi-v3.jsonl");
        var source = Path.Combine(temporary.Path, "source-link.jsonl");
        File.CreateSymbolicLink(source, fixture);
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var state = Directory.CreateDirectory(Path.Combine(temporary.Path, "state")).FullName;

        var exception = await Assert.ThrowsAsync<PiV3ImportException>(() => PiV3Importer.ImportAsync(
            new PiV3ImportOptions(source, workspace, state, "session-link", Conformance: false)));

        Assert.Equal(7, exception.ExitCode);
        Assert.Empty(Directory.EnumerateFiles(state, "journal.jsonl", SearchOption.AllDirectories));
    }

    /// <summary>
    /// 功能：确认生产模式为通用最小 PI v3 tree 生成 fresh ID，且不把 source/cwd 路径持久化。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ProductionModeMapsGenericUserMessageWithoutPersistingSourcePathAsync()
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "private-source-name.jsonl");
        var sourceText =
            "{\"type\":\"session\",\"version\":3,\"id\":\"pi-generic-source\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"cwd\":\"/private/source/workspace\"}\n" +
            "{\"type\":\"message\",\"id\":\"entry-user\",\"parentId\":null,\"timestamp\":\"2026-01-01T00:00:01Z\",\"message\":{\"role\":\"user\",\"content\":\"hello\"}}\n";
        await File.WriteAllTextAsync(source, sourceText);
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var state = Directory.CreateDirectory(Path.Combine(temporary.Path, "state")).FullName;

        var result = await PiV3Importer.ImportAsync(
            new PiV3ImportOptions(source, workspace, state, SessionId: null, Conformance: false));

        Assert.StartsWith("session-", result.SessionId, StringComparison.Ordinal);
        Assert.NotEqual("pi-generic-source", result.SessionId);
        var session = Path.Combine(state, "sessions", result.SessionId);
        var journal = await File.ReadAllTextAsync(Path.Combine(session, "journal.jsonl"));
        var report = await File.ReadAllTextAsync(Directory.EnumerateFiles(Path.Combine(session, "artifacts")).Single());
        Assert.DoesNotContain(source, journal, StringComparison.Ordinal);
        Assert.DoesNotContain(source, report, StringComparison.Ordinal);
        Assert.DoesNotContain("/private/source/workspace", journal, StringComparison.Ordinal);
        Assert.DoesNotContain("/private/source/workspace", report, StringComparison.Ordinal);
        Assert.Contains("message.appended", journal, StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：确认 unknown/custom quarantine 中的敏感键、token 与主机路径在报告发布前递归脱敏。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task QuarantinedUnknownValuesAreRedactedRecursivelyAsync()
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "source.jsonl");
        const string secret = "sk-SYNTHETIC-CANARY-123456";
        var sourceText =
            "{\"type\":\"session\",\"version\":3,\"id\":\"pi-redaction-source\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"cwd\":\"[REDACTED]\"}\n" +
            "{\"type\":\"future_entry\",\"id\":\"entry-unknown\",\"parentId\":null,\"timestamp\":\"2026-01-01T00:00:01Z\",\"payload\":{\"apiKey\":\"" + secret + "\",\"path\":\"/private/host/file\"}}\n";
        await File.WriteAllTextAsync(source, sourceText);
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var state = Directory.CreateDirectory(Path.Combine(temporary.Path, "state")).FullName;

        var result = await PiV3Importer.ImportAsync(
            new PiV3ImportOptions(source, workspace, state, "session-redaction", Conformance: false));

        var session = Path.Combine(state, "sessions", result.SessionId);
        var report = await File.ReadAllTextAsync(Directory.EnumerateFiles(Path.Combine(session, "artifacts")).Single());
        Assert.DoesNotContain(secret, report, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/private/host/file", report, StringComparison.Ordinal);
        Assert.Contains("sensitive_value_redacted", report, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：确认已存在目标 Session 永不被覆盖或合并，原有文件保持不变。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ExistingTargetIsNeverOverwrittenAsync()
    {
        using var temporary = new TemporaryDirectory();
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pi-v3-import-v0.1", "source.pi-v3.jsonl");
        var source = Path.Combine(temporary.Path, "source.jsonl");
        File.Copy(fixture, source);
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var state = Directory.CreateDirectory(Path.Combine(temporary.Path, "state")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(state, "sessions", "session-existing")).FullName;
        var marker = Path.Combine(target, "owner-marker.txt");
        await File.WriteAllTextAsync(marker, "preserve-me");

        var exception = await Assert.ThrowsAsync<PiV3ImportException>(() => PiV3Importer.ImportAsync(
            new PiV3ImportOptions(source, workspace, state, "session-existing", Conformance: false)));

        Assert.Equal(8, exception.ExitCode);
        Assert.Equal("preserve-me", await File.ReadAllTextAsync(marker));
        Assert.Single(Directory.EnumerateFiles(target));
    }

    /// <summary>
    /// 功能：确认 known entry 偷塞字段与超过 64 层 JSON 在目标目录创建前失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task KnownEntryExtraFieldAndExcessDepthFailClosedAsync()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var state = Directory.CreateDirectory(Path.Combine(temporary.Path, "state")).FullName;
        var source = Path.Combine(temporary.Path, "invalid.jsonl");
        var header = "{\"type\":\"session\",\"version\":3,\"id\":\"pi-invalid-source\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"cwd\":\"[REDACTED]\"}\n";
        await File.WriteAllTextAsync(
            source,
            header + "{\"type\":\"message\",\"id\":\"entry-user\",\"parentId\":null,\"timestamp\":\"2026-01-01T00:00:01Z\",\"message\":{\"role\":\"user\",\"content\":\"hello\"},\"unexpected\":true}\n");

        await Assert.ThrowsAsync<PiV3ImportException>(() => PiV3Importer.ImportAsync(
            new PiV3ImportOptions(source, workspace, state, "session-extra-field", Conformance: false)));
        var nesting = new string('[', 65) + "0" + new string(']', 65);
        await File.WriteAllTextAsync(
            source,
            header + "{\"type\":\"future_entry\",\"id\":\"entry-deep\",\"parentId\":null,\"timestamp\":\"2026-01-01T00:00:01Z\",\"payload\":" + nesting + "}\n");

        await Assert.ThrowsAsync<PiV3ImportException>(() => PiV3Importer.ImportAsync(
            new PiV3ImportOptions(source, workspace, state, "session-deep", Conformance: false)));
        Assert.Empty(Directory.EnumerateFiles(state, "journal.jsonl", SearchOption.AllDirectories));
    }
}
