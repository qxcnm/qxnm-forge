using System.Text;
using System.Text.Json;
using QxnmForge.Agent;
using QxnmForge.Daemon;
using QxnmForge.Domain;
using QxnmForge.Provider;
using QxnmForge.Session;
using QxnmForge.Storage;
using QxnmForge.Tools;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 daemon 的 initialize/configure/start 端到端帧顺序和协议隔离。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class StdioDaemonTests
{
    /// <summary>
    /// 功能：确认内存 stdio 运行精确产生三响应后六事件且 runId 响应领先所有 run 事件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task RunAsyncProducesGoldenFlowAndOrdering()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        await using var repository = new SessionRepository(Path.Combine(temporary.Path, "sessions"), workspace);
        var daemon = new StdioDaemon(
            repository,
            new AgentService(repository, new FauxProvider()),
            conformanceMode: true);
        var requests = string.Join('\n',
            "{\"jsonrpc\":\"2.0\",\"id\":\"initialize-1\",\"method\":\"initialize\",\"params\":{\"protocolVersions\":[\"0.1\"],\"client\":{\"name\":\"test\",\"version\":\"0.1\"},\"capabilities\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"configure-1\",\"method\":\"faux/configure\",\"params\":{\"sessionId\":\"daemon-session\",\"scenario\":{\"schemaVersion\":\"0.1\",\"name\":\"hello\",\"seed\":1,\"steps\":[{\"type\":\"text\",\"text\":\"你好\"}],\"usage\":{\"inputTokens\":1,\"outputTokens\":2,\"totalTokens\":3}}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"start-1\",\"method\":\"run/start\",\"params\":{\"sessionId\":\"daemon-session\",\"input\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hello\"}]},\"provider\":{\"id\":\"faux\",\"modelId\":\"faux-v1\"}}}") + "\n";
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(requests));
        await using var output = new MemoryStream();

        await daemon.RunAsync(input, output, CancellationToken.None);
        var frames = ParseFrames(output);

        Assert.Equal(9, frames.Count);
        Assert.Equal("initialize-1", frames[0].RootElement.GetProperty("id").GetString());
        Assert.Equal("dotnet", frames[0].RootElement.GetProperty("result").GetProperty("implementation").GetProperty("language").GetString());
        Assert.Equal("start-1", frames[2].RootElement.GetProperty("id").GetString());
        Assert.All(frames.Skip(3), static frame => Assert.Equal("event", frame.RootElement.GetProperty("method").GetString()));
        Assert.Equal("run.started", frames[3].RootElement.GetProperty("params").GetProperty("type").GetString());
        Assert.Equal("run.completed", frames[^1].RootElement.GetProperty("params").GetProperty("type").GetString());

        foreach (var frame in frames)
        {
            frame.Dispose();
        }
    }

    /// <summary>
    /// 功能：确认 artifacts/create 返回前已 durable 发布图片文件和 artifact.created，且响应与 journal 不回显 Base64 或路径。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task ArtifactCreatePublishesDurableInputImageWithoutLeakingBytes()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessionsRoot = Path.Combine(temporary.Path, "sessions");
        await using var repository = new SessionRepository(sessionsRoot, workspace);
        var daemon = new StdioDaemon(
            repository,
            new AgentService(repository, new FauxProvider()),
            conformanceMode: true);
        const string encoded = "iVBORw0KGgpmaXh0dXJl";
        var requests = string.Join('\n',
            "{\"jsonrpc\":\"2.0\",\"id\":\"initialize-artifact\",\"method\":\"initialize\",\"params\":{\"protocolVersions\":[\"0.1\"],\"client\":{\"name\":\"test\",\"version\":\"0.1\"},\"capabilities\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"create-artifact\",\"method\":\"artifacts/create\",\"params\":{\"sessionId\":\"artifact-session\",\"mediaType\":\"image/png\",\"dataBase64\":\"" + encoded + "\"}}") + "\n";
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(requests));
        await using var output = new MemoryStream();

        await daemon.RunAsync(input, output, CancellationToken.None);
        var frames = ParseFrames(output);
        try
        {
            Assert.Equal(2, frames.Count);
            Assert.Contains(
                frames[0].RootElement
                    .GetProperty("result")
                    .GetProperty("capabilities")
                    .GetProperty("methods")
                    .EnumerateArray(),
                static method => method.GetString() == "artifacts/create");
            var artifact = frames[1].RootElement.GetProperty("result").GetProperty("artifact");
            Assert.Equal("image/png", artifact.GetProperty("mediaType").GetString());
            Assert.Equal(15, artifact.GetProperty("byteLength").GetInt64());
            var artifactId = artifact.GetProperty("artifactId").GetString()!;
            Assert.DoesNotContain(encoded, Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);

            var runtime = await repository.GetAsync("artifact-session", CancellationToken.None);
            var journalText = await File.ReadAllTextAsync(
                runtime.Journal.JournalPath,
                CancellationToken.None);
            Assert.DoesNotContain(encoded, journalText, StringComparison.Ordinal);
            Assert.DoesNotContain(runtime.Journal.DirectoryPath, journalText, StringComparison.Ordinal);
            var records = journalText
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(static line => JsonDocument.Parse(line))
                .ToArray();
            try
            {
                var record = Assert.Single(records);
                Assert.Equal("artifact.created", record.RootElement.GetProperty("kind").GetString());
                Assert.Equal(
                    artifactId,
                    record.RootElement
                        .GetProperty("data")
                        .GetProperty("artifact")
                        .GetProperty("artifactId")
                        .GetString());
            }
            finally
            {
                foreach (var record in records)
                {
                    record.Dispose();
                }
            }

            var expectedBytes = Convert.FromBase64String(encoded);
            var storedBytes = await File.ReadAllBytesAsync(
                Path.Combine(runtime.Journal.DirectoryPath, "artifacts", artifactId),
                CancellationToken.None);
            Assert.Equal(expectedBytes, storedBytes);
        }
        finally
        {
            foreach (var frame in frames)
            {
                frame.Dispose();
            }
        }
    }

    /// <summary>
    /// 功能：确认 daemon 的 session/get 返回完整消息、event afterSeq 增量，并只广告真实 Provider、工具和方法。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task SessionGetReturnsDurableSnapshotAndTruthfulCapabilities()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        await using var repository = new SessionRepository(Path.Combine(temporary.Path, "sessions"), workspace);
        var daemon = new StdioDaemon(
            repository,
            new AgentService(repository, new FauxProvider()),
            conformanceMode: true);
        var firstRequests = string.Join('\n',
            "{\"jsonrpc\":\"2.0\",\"id\":\"initialize-1\",\"method\":\"initialize\",\"params\":{\"protocolVersions\":[\"0.1\"],\"client\":{\"name\":\"test\",\"version\":\"0.1\"},\"capabilities\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"configure-1\",\"method\":\"faux/configure\",\"params\":{\"sessionId\":\"snapshot-session\",\"scenario\":{\"schemaVersion\":\"0.1\",\"name\":\"snapshot\",\"seed\":1,\"steps\":[{\"type\":\"text\",\"text\":\"answer\"}]}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"start-1\",\"method\":\"run/start\",\"params\":{\"sessionId\":\"snapshot-session\",\"input\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"question\"}]},\"provider\":{\"id\":\"faux\",\"modelId\":\"faux-v1\"}}}") + "\n";
        await using (var firstInput = new MemoryStream(Encoding.UTF8.GetBytes(firstRequests)))
        await using (var firstOutput = new MemoryStream())
        {
            await daemon.RunAsync(firstInput, firstOutput, CancellationToken.None);
        }

        var secondRequests = string.Join('\n',
            "{\"jsonrpc\":\"2.0\",\"id\":\"initialize-2\",\"method\":\"initialize\",\"params\":{\"protocolVersions\":[\"0.1\"],\"client\":{\"name\":\"test\",\"version\":\"0.1\"},\"capabilities\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"get-1\",\"method\":\"session/get\",\"params\":{\"sessionId\":\"snapshot-session\",\"afterSeq\":4}}") + "\n";
        await using var secondInput = new MemoryStream(Encoding.UTF8.GetBytes(secondRequests));
        await using var secondOutput = new MemoryStream();

        await daemon.RunAsync(secondInput, secondOutput, CancellationToken.None);
        var frames = ParseFrames(secondOutput);
        try
        {
            Assert.Equal(2, frames.Count);
            var capabilities = frames[0].RootElement.GetProperty("result").GetProperty("capabilities");
            Assert.Contains(
                capabilities.GetProperty("methods").EnumerateArray(),
                static method => method.GetString() == "session/get");
            var provider = Assert.Single(capabilities.GetProperty("providers").EnumerateArray());
            Assert.Equal("faux", provider.GetProperty("id").GetString());
            Assert.Equal(
                ["file.edit", "file.read", "file.write", "process.exec", "search.text", "shell.exec"],
                capabilities.GetProperty("tools").EnumerateArray().Select(static tool => tool.GetString()));

            var result = frames[1].RootElement.GetProperty("result");
            Assert.Equal("snapshot-session", result.GetProperty("sessionId").GetString());
            Assert.Equal(6, result.GetProperty("latestSeq").GetInt64());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("activeRunId").ValueKind);
            Assert.Equal(2, result.GetProperty("messages").GetArrayLength());
            Assert.Equal([5L, 6L], result.GetProperty("events").EnumerateArray().Select(
                static portableEvent => portableEvent.GetProperty("seq").GetInt64()));
        }
        finally
        {
            foreach (var frame in frames)
            {
                frame.Dispose();
            }
        }
    }

    /// <summary>
    /// 功能：确认 identity-only 快照可查询但不可执行，且 models/list 未知参数严格拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task IdentityOnlyModelsAreQueryableButNotExecutable()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessionsRoot = Path.Combine(temporary.Path, "sessions");
        await using var repository = new SessionRepository(sessionsRoot, workspace);
        await using var registry = new ProviderRegistry(
            [new FauxProvider()],
            [
                new ModelDescriptor(
                    "deepseek",
                    "deepseek-chat",
                    "DeepSeek Chat",
                    "openai-completions",
                    new ModelCapabilities(
                        ["text"],
                        ["text"],
                        Streaming: true,
                        Tools: true,
                        Reasoning: false,
                        ContextTokens: 128_000,
                        MaxOutputTokens: 8_192)),
            ]);
        var daemon = new StdioDaemon(
            repository,
            new AgentService(repository, registry),
            conformanceMode: true);
        var requests = string.Join('\n',
            "{\"jsonrpc\":\"2.0\",\"id\":\"initialize-identity\",\"method\":\"initialize\",\"params\":{\"protocolVersions\":[\"0.1\"],\"client\":{\"name\":\"test\",\"version\":\"0.1\"},\"capabilities\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"models-all\",\"method\":\"models/list\",\"params\":{}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"models-invalid\",\"method\":\"models/list\",\"params\":{\"unknown\":true}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"run-identity\",\"method\":\"run/start\",\"params\":{\"sessionId\":\"identity-session\",\"input\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hello\"}]},\"provider\":{\"id\":\"deepseek\",\"modelId\":\"deepseek-chat\",\"apiFamily\":\"openai-completions\"}}}") + "\n";
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(requests));
        await using var output = new MemoryStream();

        await daemon.RunAsync(input, output, CancellationToken.None);
        var frames = ParseFrames(output);
        try
        {
            Assert.Equal(4, frames.Count);
            var capabilities = frames[0].RootElement.GetProperty("result").GetProperty("capabilities");
            Assert.Contains(
                capabilities.GetProperty("methods").EnumerateArray(),
                static method => method.GetString() == "models/list");
            Assert.Equal(
                ["deepseek", "faux"],
                capabilities.GetProperty("providers").EnumerateArray().Select(
                    static provider => provider.GetProperty("id").GetString()));
            Assert.Equal(
                ["deepseek", "faux"],
                frames[1].RootElement.GetProperty("result").GetProperty("models").EnumerateArray().Select(
                    static model => model.GetProperty("providerId").GetString()));
            Assert.Equal(
                -32602,
                frames[2].RootElement.GetProperty("error").GetProperty("code").GetInt32());
            var unavailable = frames[3].RootElement.GetProperty("error");
            Assert.Equal(-32005, unavailable.GetProperty("code").GetInt32());
            Assert.Equal(
                "provider_unavailable",
                unavailable.GetProperty("details").GetProperty("kind").GetString());
            Assert.False(Directory.Exists(sessionsRoot));
        }
        finally
        {
            foreach (var frame in frames)
            {
                frame.Dispose();
            }
        }
    }

    /// <summary>
    /// 功能：验证 Profile CRUD 能力校验、固定错误、UTC wire 与显式 faux family run 端到端闭合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task AgentProfilesValidateCapabilitiesAndBindFauxRun()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var sessionsRoot = Path.Combine(temporary.Path, "sessions");
        await using var repository = new SessionRepository(sessionsRoot, workspace);
        await using var registry = new ProviderRegistry([new FauxProvider()]);
        using var tools = new ToolRegistry(workspace);
        var database = await ApplicationDatabaseFactory.OpenFactoryAsync(
            DatabaseConfiguration.ForStateRoot(sessionsRoot));
        var profileService = new AgentProfileService(
            database,
            registry.ListModels()
                .Select(static model => new AgentProfileModel(
                    model.ProviderId,
                    model.ModelId,
                    model.ApiFamily))
                .ToArray(),
            tools.Names);
        var daemon = new StdioDaemon(
            repository,
            new AgentService(
                repository,
                registry,
                tools,
                TimeSpan.FromSeconds(1),
                profileService),
            conformanceMode: true,
            profileService);
        const string initialize =
            "{\"jsonrpc\":\"2.0\",\"id\":\"initialize-profile\",\"method\":\"initialize\",\"params\":{\"protocolVersions\":[\"0.1\"],\"client\":{\"name\":\"test\",\"version\":\"0.1\"},\"capabilities\":{}}}";
        const string validProfile =
            "{\"displayName\":\"Reviewer\",\"description\":\"Reviews changes.\",\"enabled\":true,\"instructions\":\"Review the change.\",\"model\":{\"providerId\":\"faux\",\"modelId\":\"faux-v1\",\"apiFamily\":\"faux\"},\"requestedToolIds\":[\"file.read\"],\"dangerousActionMode\":\"ask\",\"behavior\":{\"responseStyle\":\"concise\",\"planFirst\":true,\"reviewChanges\":true}}";
        var unknownModel = validProfile.Replace(
            "\"providerId\":\"faux\"",
            "\"providerId\":\"unknown\"",
            StringComparison.Ordinal);
        var unknownTool = validProfile.Replace(
            "\"file.read\"",
            "\"unknown.tool\"",
            StringComparison.Ordinal);
        var firstRequests = string.Join('\n',
            initialize,
            "{\"jsonrpc\":\"2.0\",\"id\":\"create-model\",\"method\":\"agentProfiles/create\",\"params\":{\"profile\":" + unknownModel + "}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"create-tool\",\"method\":\"agentProfiles/create\",\"params\":{\"profile\":" + unknownTool + "}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"create-valid\",\"method\":\"agentProfiles/create\",\"params\":{\"profile\":" + validProfile + "}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"list-profiles\",\"method\":\"agentProfiles/list\",\"params\":{}}") + "\n";
        await using var firstInput = new MemoryStream(Encoding.UTF8.GetBytes(firstRequests));
        await using var firstOutput = new MemoryStream();

        await daemon.RunAsync(firstInput, firstOutput, CancellationToken.None);
        var firstFrames = ParseFrames(firstOutput);
        string profileId;
        try
        {
            Assert.Equal(5, firstFrames.Count);
            var methods = firstFrames[0].RootElement
                .GetProperty("result")
                .GetProperty("capabilities")
                .GetProperty("methods");
            Assert.Contains(methods.EnumerateArray(), static method =>
                method.GetString() == "agentProfiles/create");
            const string invalidError =
                "{\"code\":-32602,\"message\":\"agent profile is invalid\",\"retryable\":false,\"details\":{\"kind\":\"agent_profile_invalid\",\"field\":\"profile\"}}";
            Assert.Equal(invalidError, firstFrames[1].RootElement.GetProperty("error").GetRawText());
            Assert.Equal(invalidError, firstFrames[2].RootElement.GetProperty("error").GetRawText());
            var created = firstFrames[3].RootElement.GetProperty("result").GetProperty("profile");
            profileId = created.GetProperty("profileId").GetString()!;
            Assert.EndsWith("Z", created.GetProperty("createdAt").GetString(), StringComparison.Ordinal);
            Assert.EndsWith("Z", created.GetProperty("updatedAt").GetString(), StringComparison.Ordinal);
            var profiles = firstFrames[4].RootElement
                .GetProperty("result")
                .GetProperty("profiles");
            Assert.Equal(1, profiles.GetArrayLength());
            Assert.Equal(profileId, profiles[0].GetProperty("profileId").GetString());
        }
        finally
        {
            foreach (var frame in firstFrames)
            {
                frame.Dispose();
            }
        }

        var secondRequests = string.Join('\n',
            initialize.Replace("initialize-profile", "initialize-run", StringComparison.Ordinal),
            "{\"jsonrpc\":\"2.0\",\"id\":\"configure-profile\",\"method\":\"faux/configure\",\"params\":{\"sessionId\":\"profile-session\",\"scenario\":{\"schemaVersion\":\"0.1\",\"name\":\"profile\",\"seed\":1,\"steps\":[{\"type\":\"text\",\"text\":\"done\"}]}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"run-profile\",\"method\":\"run/start\",\"params\":{\"sessionId\":\"profile-session\",\"input\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"review\"}]},\"provider\":{\"id\":\"faux\",\"modelId\":\"faux-v1\",\"apiFamily\":\"faux\"},\"agentProfile\":{\"profileId\":\"" + profileId + "\",\"revision\":1}}}") + "\n";
        await using var secondInput = new MemoryStream(Encoding.UTF8.GetBytes(secondRequests));
        await using var secondOutput = new MemoryStream();

        await daemon.RunAsync(secondInput, secondOutput, CancellationToken.None);
        var secondFrames = ParseFrames(secondOutput);
        try
        {
            Assert.DoesNotContain(secondFrames, static frame =>
                frame.RootElement.TryGetProperty("error", out _));
            Assert.Contains(secondFrames, static frame =>
                frame.RootElement.TryGetProperty("id", out var id) && id.GetString() == "run-profile");
        }
        finally
        {
            foreach (var frame in secondFrames)
            {
                frame.Dispose();
            }
        }

        var runtime = await repository.GetAsync("profile-session", CancellationToken.None);
        var acceptedLine = (await File.ReadAllLinesAsync(
                runtime.Journal.JournalPath,
                CancellationToken.None))
            .Select(static line => JsonDocument.Parse(line))
            .Single(static document =>
                document.RootElement.GetProperty("kind").GetString() == "run.accepted");
        using (acceptedLine)
        {
            var snapshot = acceptedLine.RootElement
                .GetProperty("data")
                .GetProperty("agentProfileSnapshot");
            Assert.Equal(profileId, snapshot.GetProperty("profileId").GetString());
            Assert.Equal("faux", snapshot.GetProperty("model").GetProperty("apiFamily").GetString());
            Assert.Equal(["file.read"], snapshot.GetProperty("effectiveToolIds")
                .EnumerateArray()
                .Select(static tool => tool.GetString()));
        }
    }

    /// <summary>
    /// 功能：把 daemon 输出的 UTF-8 NDJSON 转成独立 JsonDocument 列表。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="output">已完成的内存 stdout。</param>
    /// <returns>每行一个 JSON 文档。</returns>
    private static List<JsonDocument> ParseFrames(MemoryStream output)
    {
        var text = Encoding.UTF8.GetString(output.ToArray());
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => JsonDocument.Parse(line))
            .ToList();
    }
}
