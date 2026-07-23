using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using QxnmForge.Daemon;
using QxnmForge.Domain;
using QxnmForge.Provider;
using QxnmForge.Serialization;
using QxnmForge.Session;
using QxnmForge.Tools;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证自定义 Provider 连接持久化、RPC、凭据隔离与启动快照。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
[Collection(ProviderEnvironmentGroup.Name)]
public sealed class CustomProviderConnectionTests
{
    private const string Credential = "unit-secret-never-return";

    /// <summary>
    /// 功能：确认连接 CRUD 使用 CAS、连接文档不含 secret，凭据状态只以布尔值投影。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void StoreSeparatesCredentialsAndEnforcesCas()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var service = new CustomProviderConnectionService(stateRoot, workspace);
        var first = service.Create(CreateInput("alpha", "Alpha"));

        Assert.Equal(1, first.Revision);
        Assert.False(first.CredentialConfigured);
        Assert.Equal(first.CreatedAt, first.UpdatedAt);
        Assert.Equal(TimeSpan.Zero, first.CreatedAt.Offset);

        service.SetCredential("alpha", "responses", Credential);
        var configured = Assert.Single(service.List());
        Assert.True(configured.CredentialConfigured);
        var connectionDocument = File.ReadAllText(Path.Combine(
            stateRoot,
            "provider-connections",
            "provider-connections.json"));
        Assert.DoesNotContain(Credential, connectionDocument, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", connectionDocument, StringComparison.OrdinalIgnoreCase);

        var updated = service.Update(
            first.ConnectionId,
            first.Revision,
            CreateInput("alpha", "Alpha Updated"));
        Assert.Equal(2, updated.Revision);
        Assert.True(updated.CredentialConfigured);
        Assert.Equal(first.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt >= first.UpdatedAt);

        var stale = Assert.Throws<ProviderConnectionException>(() => service.Update(
            first.ConnectionId,
            first.Revision,
            CreateInput("alpha", "Stale")));
        Assert.Equal("provider_connection_revision_conflict", stale.Error.Details.Kind);
        Assert.Null(stale.Error.Details.ExpectedRevision);
        Assert.Null(stale.Error.Details.CurrentRevision);
        Assert.Null(stale.Error.Details.ResourceId);

        var duplicate = Assert.Throws<ProviderConnectionException>(() =>
            service.Create(CreateInput("alpha", "Duplicate")));
        Assert.Equal("provider_id_conflict", duplicate.Error.Details.Kind);
        Assert.True(Assert.Single(service.List()).CredentialConfigured);

        service.Delete(updated.ConnectionId, updated.Revision);
        Assert.Empty(service.List());
        Assert.Empty(new ProviderCredentialStore(stateRoot, workspace).List());
    }

    /// <summary>
    /// 功能：确认 Responses 与 Image key 独立存取，Image key 不参与模型发现，删除连接会同时清理两者。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ResponsesAndImageCredentialsRemainIsolated()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var handler = new DiscoveryResponseHandler(
            HttpStatusCode.OK,
            Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"must-not-be-read\"}]}"));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new CustomProviderConnectionService(
            stateRoot,
            workspace,
            allowLoopbackHttp: false,
            client);
        var created = service.Create(CreateInput("dual-key", "Dual key"));
        var imageCredentialId = created.ConnectionId + ".image";
        var credentials = new ProviderCredentialStore(stateRoot, workspace);

        service.SetCredential("dual-key", "responses", Credential);
        service.SetCredential("dual-key", "image", "image-secret-never-return");

        var configured = Assert.Single(service.List());
        Assert.True(configured.CredentialConfigured);
        Assert.True(configured.ImageCredentialConfigured);
        Assert.Equal([imageCredentialId, "dual-key"], credentials.List());

        service.RemoveCredential("dual-key", "responses");

        var imageOnly = Assert.Single(service.List());
        Assert.False(imageOnly.CredentialConfigured);
        Assert.True(imageOnly.ImageCredentialConfigured);
        Assert.True(credentials.TryReadForRequest(imageCredentialId, out var imageCredential));
        Assert.Equal("image-secret-never-return", imageCredential);
        _ = await Assert.ThrowsAsync<ProviderConnectionException>(() =>
            service.DiscoverModelsAsync(
                created.ConnectionId,
                created.Revision,
                CancellationToken.None));
        Assert.Null(handler.Method);

        service.SetCredential("dual-key", "responses", Credential);
        service.Delete(created.ConnectionId, created.Revision);

        Assert.Empty(credentials.List());
    }

    /// <summary>
    /// 功能：确认 Provider rename 不会继承目标 ID 的历史 orphan credential，并清理新旧两端。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void RenameClearsSourceAndDestinationOrphanCredentials()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var service = new CustomProviderConnectionService(stateRoot, workspace);
        var connection = service.Create(CreateInput("alpha.source", "Alpha"));
        service.SetCredential("alpha.source", "responses", Credential);
        var credentials = new ProviderCredentialStore(stateRoot, workspace);
        credentials.Set("beta.orphan", "orphan-destination-secret");

        var updated = service.Update(
            connection.ConnectionId,
            connection.Revision,
            CreateInput("beta.orphan", "Beta"));

        Assert.Equal("beta.orphan", updated.ProviderId);
        Assert.False(updated.CredentialConfigured);
        Assert.Empty(credentials.List());
    }

    /// <summary>
    /// 功能：确认新建连接清理目标 ID 历史 orphan credential，不能把旧 secret 绑定新 endpoint。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void CreateClearsDestinationOrphanCredential()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var credentials = new ProviderCredentialStore(stateRoot, workspace);
        credentials.Set("orphan.create", "orphan-create-secret");
        var service = new CustomProviderConnectionService(stateRoot, workspace);

        var created = service.Create(CreateInput("orphan.create", "Fresh endpoint"));

        Assert.False(created.CredentialConfigured);
        Assert.Empty(credentials.List());
    }

    /// <summary>
    /// 功能：确认 orphan 清理后的连接发布失败不会恢复旧 secret 或产生部分连接记录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void CreatePublishFailureLeavesNoCredentialOrConnectionRecord()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var service = new CustomProviderConnectionService(stateRoot, workspace);
        var credentials = new ProviderCredentialStore(stateRoot, workspace);
        credentials.Set("orphan.failure", "orphan-failure-secret");
        var documentPath = Path.Combine(
            stateRoot,
            "provider-connections",
            "provider-connections.json");
        _ = Directory.CreateDirectory(documentPath);

        _ = Assert.Throws<ProviderConnectionException>(() =>
            service.Create(CreateInput("orphan.failure", "Cannot publish")));

        Assert.Empty(credentials.List());
        Assert.True(Directory.Exists(documentPath));
        Assert.Empty(Directory.EnumerateFiles(documentPath));
    }

    /// <summary>
    /// 功能：确认 128 个满长 credential 以独立叶发布，且第 129 个条目失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void CredentialStoreAcceptsMaximumIndependentEntrySet()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var store = new ProviderCredentialStore(stateRoot, workspace);
        var maximumCredential = new string('x', 16_384);

        foreach (var index in Enumerable.Range(0, 128))
        {
            store.Set($"bulk.{index:D3}", maximumCredential);
        }

        Assert.Equal(128, store.List().Count);
        var entryRoot = Path.Combine(stateRoot, "credentials", "provider-credentials.d");
        Assert.Equal(128, Directory.EnumerateFiles(entryRoot, "*.credential").Count());
        Assert.True(Directory.EnumerateFiles(entryRoot, "*.credential")
            .Sum(static path => new FileInfo(path).Length) >= 2L * 1024 * 1024);
        var exception = Assert.Throws<ProviderCommercialStateException>(() =>
            store.Set("bulk.128", maximumCredential));
        Assert.Equal("credential_shape", exception.Kind);
    }

    /// <summary>
    /// 功能：确认模型目录严格解析后按 ordinal 排序、去重，并允许完整 512 项结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void ModelDiscoveryParserSortsDeduplicatesAndAcceptsMaximumCount()
    {
        var sorted = CustomProviderConnectionService.ParseDiscoveredModels(
            Encoding.UTF8.GetBytes(
                "{\"object\":\"list\",\"metadata\":{\"nested\":{\"tier\":\"standard\"}},\"data\":[{\"id\":\"z-model\",\"object\":\"model\",\"created\":1,\"owned_by\":\"test\"},{\"id\":\"a-model\"},{\"id\":\"z-model\"}]}"));
        Assert.Equal(["a-model", "z-model"], sorted);

        var maximum = JsonSerializer.SerializeToUtf8Bytes(new
        {
            data = Enumerable.Range(0, 512).Select(index => new { id = $"model-{index:D3}" }),
        });
        Assert.Equal(512, CustomProviderConnectionService.ParseDiscoveredModels(maximum).Count);
    }

    /// <summary>
    /// 功能：确认重复键、缺少关键字段、空目录、控制字符、超长 UTF-8 ID 与 513 个唯一模型均失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void ModelDiscoveryParserRejectsMalformedOrUnboundedDirectories()
    {
        var tooMany = JsonSerializer.SerializeToUtf8Bytes(new
        {
            data = Enumerable.Range(0, 513).Select(index => new { id = $"model-{index:D3}" }),
        });
        var invalidPayloads = new List<byte[]>
        {
            Encoding.UTF8.GetBytes("{\"data\":[],\"data\":[]}"),
            Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"model-a\",\"id\":\"model-b\"}]}"),
            Encoding.UTF8.GetBytes("{\"metadata\":{\"nested\":{\"tier\":1,\"tier\":2}},\"data\":[{\"id\":\"model-a\"}]}"),
            Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"model-a\",\"metadata\":[{\"tier\":1,\"tier\":2}]}]}"),
            Encoding.UTF8.GetBytes("{\"object\":\"list\"}"),
            Encoding.UTF8.GetBytes("{\"data\":[{\"object\":\"model\"}]}"),
            Encoding.UTF8.GetBytes("{\"data\":[]}"),
            Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"bad\\u0001model\"}]}"),
            JsonSerializer.SerializeToUtf8Bytes(new { data = new[] { new { id = new string('界', 86) } } }),
            tooMany,
        };

        foreach (var payload in invalidPayloads)
        {
            var exception = Assert.Throws<ProviderConnectionException>(() =>
                CustomProviderConnectionService.ParseDiscoveredModels(payload));
            Assert.Equal(-32005, exception.Error.Code);
            Assert.Equal("provider_model_discovery_failed", exception.Error.Details.Kind);
        }
    }

    /// <summary>
    /// 功能：确认发现使用同源 /models Bearer GET，并只替换模型、递增 revision 与保持其他配置。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ModelDiscoveryUsesBearerGetAndPublishesOnlyModels()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var handler = new DiscoveryResponseHandler(
            HttpStatusCode.OK,
            Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"model-z\"},{\"id\":\"model-a\"},{\"id\":\"model-z\"}]}"));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new CustomProviderConnectionService(
            stateRoot,
            workspace,
            allowLoopbackHttp: false,
            client);
        var created = service.Create(CreateInput("discovery", "Discovery") with
        {
            ModelIds = [],
            LogoAssetId = "discovery-logo",
        });
        service.SetCredential("discovery", "responses", Credential);

        FileStream? credentialLock = null;
        handler.BeforeResponse = () => credentialLock = new FileStream(
            Path.Combine(stateRoot, "credentials", "provider-credentials.lock"),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        ProviderConnection discovered;
        try
        {
            discovered = await service.DiscoverModelsAsync(
                created.ConnectionId,
                created.Revision,
                CancellationToken.None);
        }
        finally
        {
            credentialLock?.Dispose();
        }

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("https://example.invalid/v1/models", handler.RequestUri?.AbsoluteUri);
        Assert.True(handler.SawExpectedBearer);
        Assert.Equal(2, discovered.Revision);
        Assert.Equal(["model-a", "model-z"], discovered.ModelIds);
        Assert.Equal(created.DisplayName, discovered.DisplayName);
        Assert.Equal(created.ProviderId, discovered.ProviderId);
        Assert.Equal(created.BaseUrl, discovered.BaseUrl);
        Assert.Equal(created.LogoAssetId, discovered.LogoAssetId);
        Assert.Equal(created.Enabled, discovered.Enabled);
        Assert.Equal(created.CreatedAt, discovered.CreatedAt);
        Assert.True(discovered.CredentialConfigured);
    }

    /// <summary>
    /// 功能：确认畸形、超限与非成功远端响应不修改连接，且错误不泄漏正文、URL 或 credential。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ModelDiscoveryRemoteFailuresAreRedactedAndNonMutating()
    {
        var cases = new (HttpStatusCode Status, byte[] Body)[]
        {
            (HttpStatusCode.OK, Encoding.UTF8.GetBytes("private-remote-body-" + Credential)),
            (HttpStatusCode.OK, new byte[(1024 * 1024) + 1]),
            (HttpStatusCode.Created, Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"must-not-persist\"}]}")),
            (HttpStatusCode.BadGateway, Encoding.UTF8.GetBytes("gateway-private-body")),
        };

        foreach (var item in cases)
        {
            using var temporary = new TemporaryDirectory();
            var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
            var stateRoot = Path.Combine(temporary.Path, "state");
            var handler = new DiscoveryResponseHandler(item.Status, item.Body);
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var service = new CustomProviderConnectionService(
                stateRoot,
                workspace,
                allowLoopbackHttp: false,
                client);
            var created = service.Create(CreateInput("failure", "Failure"));
            service.SetCredential("failure", "responses", Credential);

            var exception = await Assert.ThrowsAsync<ProviderConnectionException>(() =>
                service.DiscoverModelsAsync(
                    created.ConnectionId,
                    created.Revision,
                    CancellationToken.None));

            var error = JsonSerializer.Serialize(exception.Error, JsonDefaults.Options);
            Assert.Equal(-32005, exception.Error.Code);
            Assert.DoesNotContain(Credential, error, StringComparison.Ordinal);
            Assert.DoesNotContain("example.invalid", error, StringComparison.Ordinal);
            Assert.DoesNotContain("private-body", error, StringComparison.Ordinal);
            var unchanged = Assert.Single(service.List());
            Assert.Equal(created.Revision, unchanged.Revision);
            Assert.Equal(created.ModelIds, unchanged.ModelIds);
        }
    }

    /// <summary>
    /// 功能：确认远端请求期间发生的并发连接更新赢得 CAS，发现结果不能覆盖新配置。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task ModelDiscoveryCasConflictPreservesConcurrentUpdate()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var handler = new DiscoveryResponseHandler(
            HttpStatusCode.OK,
            Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"remote-model\"}]}"));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new CustomProviderConnectionService(
            stateRoot,
            workspace,
            allowLoopbackHttp: false,
            client);
        var created = service.Create(CreateInput("concurrent", "Before"));
        service.SetCredential("concurrent", "responses", Credential);
        handler.BeforeResponse = () => service.Update(
            created.ConnectionId,
            created.Revision,
            CreateInput("concurrent", "Concurrent") with { ModelIds = ["user-model"] });

        var exception = await Assert.ThrowsAsync<ProviderConnectionException>(() =>
            service.DiscoverModelsAsync(
                created.ConnectionId,
                created.Revision,
                CancellationToken.None));

        Assert.Equal("provider_connection_revision_conflict", exception.Error.Details.Kind);
        var current = Assert.Single(service.List());
        Assert.Equal(2, current.Revision);
        Assert.Equal("Concurrent", current.DisplayName);
        Assert.Equal(["user-model"], current.ModelIds);
    }

    /// <summary>
    /// 功能：确认 daemon 广告并执行模型发现 RPC，返回计数、重启要求且 stdout 不含 secret。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task DaemonModelDiscoveryRpcIsAdvertisedAndRedacted()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var handler = new DiscoveryResponseHandler(
            HttpStatusCode.OK,
            Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"rpc-b\"},{\"id\":\"rpc-a\"}]}"));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var connections = new CustomProviderConnectionService(
            stateRoot,
            workspace,
            allowLoopbackHttp: false,
            client);
        var created = connections.Create(CreateInput("rpc.discovery", "RPC") with { ModelIds = [] });
        connections.SetCredential("rpc.discovery", "responses", Credential);
        await using var repository = new SessionRepository(stateRoot, workspace);
        await using var registry = new ProviderRegistry([new FauxProvider()]);
        using var tools = new ToolRegistry(workspace);
        var daemon = new StdioDaemon(
            repository,
            new QxnmForge.Agent.AgentService(repository, registry, tools),
            conformanceMode: false,
            profiles: null,
            connections);

        var frames = await ExchangeAsync(
            daemon,
            InitializeRequest("init-discovery") + "\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":\"discover\",\"method\":\"providerConnections/discoverModels\",\"params\":{\"connectionId\":\"" +
            created.ConnectionId + "\",\"expectedRevision\":1}}\n");

        var methods = frames[0].GetProperty("result").GetProperty("capabilities").GetProperty("methods");
        Assert.Contains(methods.EnumerateArray(), static item =>
            item.GetString() == "providerConnections/discoverModels");
        var result = frames[1].GetProperty("result");
        Assert.Equal(2, result.GetProperty("discoveredCount").GetInt32());
        Assert.True(result.GetProperty("restartRequired").GetBoolean());
        Assert.Equal(2, result.GetProperty("connection").GetProperty("revision").GetInt64());
        Assert.DoesNotContain(
            Credential,
            string.Join('\n', frames.Select(static frame => frame.GetRawText())),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 功能：确认 RPC 只接受 closed camelCase shape、广告全部方法且绝不回显 credential。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task DaemonRpcIsStrictCasAwareAndRedacted()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        await using var repository = new SessionRepository(stateRoot, workspace);
        await using var registry = new ProviderRegistry([new FauxProvider()]);
        using var tools = new ToolRegistry(workspace);
        var connections = new CustomProviderConnectionService(stateRoot, workspace);
        var daemon = new StdioDaemon(
            repository,
            new QxnmForge.Agent.AgentService(repository, registry, tools),
            conformanceMode: false,
            profiles: null,
            connections);
        var initialize = InitializeRequest("init");
        const string connection =
            "{\"displayName\":\"New API\",\"providerId\":\"newapi\",\"apiFamily\":\"openai-completions\",\"baseUrl\":\"https://example.invalid/v1\",\"modelsUrl\":\"https://example.invalid/catalog/models\",\"modelIds\":[\"model-a\",\"model-b\"],\"supportsTools\":true,\"supportsImageInput\":false,\"supportsImageOutput\":false,\"logoAssetId\":\"newapi-logo\",\"enabled\":true}";
        var createFrames = await ExchangeAsync(
            daemon,
            initialize + "\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":\"create\",\"method\":\"providerConnections/create\",\"params\":{\"connection\":" + connection + "}}\n");
        Assert.Equal(2, createFrames.Count);
        var methods = createFrames[0].GetProperty("result").GetProperty("capabilities").GetProperty("methods");
        Assert.Contains(methods.EnumerateArray(), static item =>
            item.GetString() == "providerConnections/create");
        Assert.Contains(methods.EnumerateArray(), static item =>
            item.GetString() == "providerCredentials/set");
        var created = createFrames[1].GetProperty("result");
        Assert.True(created.GetProperty("restartRequired").GetBoolean());
        var createdConnection = created.GetProperty("connection");
        var connectionId = createdConnection.GetProperty("connectionId").GetString()!;
        Assert.Equal(1, createdConnection.GetProperty("revision").GetInt64());
        Assert.False(createdConnection.GetProperty("credentialConfigured").GetBoolean());
        Assert.EndsWith(
            "Z",
            createdConnection.GetProperty("createdAt").GetString(),
            StringComparison.Ordinal);

        var configuredFrames = await ExchangeAsync(
            daemon,
            InitializeRequest("init-2") + "\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":\"credential\",\"method\":\"providerCredentials/set\",\"params\":{\"providerId\":\"newapi\",\"credentialKind\":\"responses\",\"credential\":\"" + Credential + "\"}}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":\"list\",\"method\":\"providerConnections/list\",\"params\":{}}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":\"unknown\",\"method\":\"providerConnections/create\",\"params\":{\"connection\":" + connection + ",\"credential\":\"hidden\"}}\n");
        var configuredText = string.Join('\n', configuredFrames.Select(static frame => frame.GetRawText()));
        Assert.DoesNotContain(Credential, configuredText, StringComparison.Ordinal);
        var credentialResult = configuredFrames[1].GetProperty("result");
        Assert.Equal("newapi", credentialResult.GetProperty("providerId").GetString());
        Assert.Equal("responses", credentialResult.GetProperty("credentialKind").GetString());
        Assert.True(credentialResult.GetProperty("credentialConfigured").GetBoolean());
        Assert.True(credentialResult.GetProperty("restartRequired").GetBoolean());
        Assert.True(configuredFrames[2]
            .GetProperty("result")
            .GetProperty("connections")[0]
            .GetProperty("credentialConfigured")
            .GetBoolean());
        Assert.Equal(
            "invalid_params",
            configuredFrames[3].GetProperty("error").GetProperty("details").GetProperty("kind").GetString());

        var isolatedCredentialFrames = await ExchangeAsync(
            daemon,
            InitializeRequest("init-credential-isolation") + "\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":\"image-key\",\"method\":\"providerCredentials/set\",\"params\":{\"providerId\":\"newapi\",\"credentialKind\":\"image\",\"credential\":\"image-secret\"}}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":\"remove-responses\",\"method\":\"providerCredentials/remove\",\"params\":{\"providerId\":\"newapi\",\"credentialKind\":\"responses\"}}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":\"list-isolated\",\"method\":\"providerConnections/list\",\"params\":{}}\n");
        var imageCredentialResult = isolatedCredentialFrames[1].GetProperty("result");
        Assert.Equal("image", imageCredentialResult.GetProperty("credentialKind").GetString());
        Assert.True(imageCredentialResult.GetProperty("credentialConfigured").GetBoolean());
        var removedResponsesResult = isolatedCredentialFrames[2].GetProperty("result");
        Assert.Equal("responses", removedResponsesResult.GetProperty("credentialKind").GetString());
        Assert.False(removedResponsesResult.GetProperty("credentialConfigured").GetBoolean());
        var isolatedConnection = isolatedCredentialFrames[3]
            .GetProperty("result")
            .GetProperty("connections")[0];
        Assert.False(isolatedConnection.GetProperty("credentialConfigured").GetBoolean());
        Assert.True(isolatedConnection.GetProperty("imageCredentialConfigured").GetBoolean());

        var staleFrames = await ExchangeAsync(
            daemon,
            InitializeRequest("init-3") + "\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":\"stale\",\"method\":\"providerConnections/update\",\"params\":{\"connectionId\":\"" + connectionId + "\",\"expectedRevision\":2,\"connection\":" + connection + "}}\n");
        Assert.Equal(
            "provider_connection_revision_conflict",
            staleFrames[1].GetProperty("error").GetProperty("details").GetProperty("kind").GetString());
    }

    /// <summary>
    /// 功能：确认下次 registry 启动只注册 enabled 且 configured 的连接，并形成同源模型快照。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task StartupSnapshotRequiresEnabledConnectionAndCredential()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var service = new CustomProviderConnectionService(stateRoot, workspace);
        _ = service.Create(CreateInput("ready", "Ready"));
        _ = service.Create(CreateInput("missing-key", "Missing Key"));
        _ = service.Create(CreateInput("disabled", "Disabled") with { Enabled = false });
        _ = service.Create(CreateInput("undiscovered", "Undiscovered") with { ModelIds = [] });
        service.SetCredential("ready", "responses", Credential);
        service.SetCredential("disabled", "responses", Credential);
        service.SetCredential("undiscovered", "responses", Credential);

        using var environment = new ProviderEnvironmentIsolation();
        await using var registry = ProviderRegistryFactory.CreateFromEnvironment(
            stateRoot: stateRoot,
            workspace: workspace);
        var advertisement = Assert.Single(
            registry.Advertisements,
            static item => item.Id == "ready");
        Assert.Equal(["model-a"], advertisement.Models);
        Assert.DoesNotContain(registry.Advertisements, static item => item.Id == "missing-key");
        Assert.DoesNotContain(registry.Advertisements, static item => item.Id == "disabled");
        Assert.DoesNotContain(registry.Advertisements, static item => item.Id == "undiscovered");
        Assert.DoesNotContain(registry.Providers, static item => item.Id == "undiscovered");

        var model = Assert.Single(registry.ListModels("ready"));
        Assert.Equal("model-a", model.ModelId);
        Assert.Equal("model-a", model.DisplayName);
        Assert.Equal("openai-completions", model.ApiFamily);
        Assert.Equal(["text"], model.Capabilities.Input);
        Assert.False(model.Capabilities.Tools);
        var provider = Assert.Single(registry.Providers, static item => item.Id == "ready");
        Assert.False(provider.SupportsTools);
        Assert.Equal(
            "https://example.invalid/v1/chat/completions",
            CreateRequestEndpoint(provider, "model-a").AbsoluteUri);
    }

    /// <summary>
    /// 功能：确认自定义 adapter 的启动与 IsAvailable 只检查两个叶的 presence，不提前读取损坏正文。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="apiFamily">待验证的 Chat 或 Responses family。</param>
    /// <returns>异步测试 Task。</returns>
    [Theory]
    [InlineData("openai-completions")]
    [InlineData("openai-responses")]
    public async Task AvailabilityChecksCredentialPresenceWithoutReadingBodies(string apiFamily)
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var providerId = "presence-" + apiFamily;
        var service = new CustomProviderConnectionService(stateRoot, workspace);
        var created = service.Create(CreateInput(providerId, "Presence") with
        {
            ApiFamily = apiFamily,
            SupportsImageOutput = true,
        });
        service.SetCredential(providerId, "responses", Credential);
        service.SetCredential(providerId, "image", "image-presence-key");
        var imageCredentialId = created.ConnectionId + ".image";
        File.WriteAllBytes(CredentialEntryPath(stateRoot, providerId), [0xff]);
        File.WriteAllBytes(CredentialEntryPath(stateRoot, imageCredentialId), [0xff]);

        using var environment = new ProviderEnvironmentIsolation();
        await using var registry = ProviderRegistryFactory.CreateFromEnvironment(
            stateRoot: stateRoot,
            workspace: workspace);
        var provider = Assert.Single(registry.Providers, item => item.Id == providerId);
        var store = new ProviderCredentialStore(stateRoot, workspace);

        Assert.True(provider.IsAvailable());
        Assert.True(provider.SupportsImageOutput);
        Assert.False(store.TryReadForRequest(providerId, out _));
        Assert.False(store.TryReadForRequest(imageCredentialId, out _));
    }

    /// <summary>
    /// 功能：确认生产拒绝 HTTP，conformance 只允许不经 DNS 的 literal loopback HTTP。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void HttpBaseIsConformanceLoopbackOnly()
    {
        using var temporary = new TemporaryDirectory();
        var production = new CustomProviderConnectionStore(Path.Combine(temporary.Path, "production"));
        var invalid = Assert.Throws<ProviderConnectionException>(() => production.Create(
            CreateInput("alpha", "Alpha") with { BaseUrl = "http://127.0.0.1:41000/v1" }));
        Assert.Equal("invalid_params", invalid.Error.Details.Kind);

        var conformance = new CustomProviderConnectionStore(
            Path.Combine(temporary.Path, "conformance"),
            allowLoopbackHttp: true);
        var created = conformance.Create(
            CreateInput("alpha", "Alpha") with { BaseUrl = "http://127.0.0.1:41000/v1" });
        Assert.Equal("http://127.0.0.1:41000/v1", created.BaseUrl);
        Assert.Throws<ProviderConnectionException>(() => conformance.Create(
            CreateInput("ipv6", "IPv6") with { BaseUrl = "http://[::1]:41000/v1" }));
        Assert.Throws<ProviderConnectionException>(() => conformance.Create(
            CreateInput("remote", "Remote") with { BaseUrl = "http://example.invalid/v1" }));
    }

    /// <summary>
    /// 功能：确认自定义 Provider loopback 需要 CLI、通用环境与 Provider 环境三门，单旗标不能放大权限。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task CustomLoopbackRequiresThreeIndependentConformanceGates()
    {
        using var environment = new ProviderEnvironmentIsolation();
        Assert.False(ProviderRegistryFactory.IsCustomProviderLoopbackConformanceEnabled(false));
        Assert.False(ProviderRegistryFactory.IsCustomProviderLoopbackConformanceEnabled(true));
        environment.Set("QXNM_FORGE_CONFORMANCE", "1");
        Assert.False(ProviderRegistryFactory.IsCustomProviderLoopbackConformanceEnabled(true));
        environment.Set("QXNM_FORGE_PROVIDER_CONFORMANCE", "1");
        Assert.True(ProviderRegistryFactory.IsCustomProviderLoopbackConformanceEnabled(true));
        Assert.False(ProviderRegistryFactory.IsCustomProviderLoopbackConformanceEnabled(false));

        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var service = new CustomProviderConnectionService(
            stateRoot,
            workspace,
            allowLoopbackHttp: true);
        _ = service.Create(CreateInput("loopback.custom", "Loopback") with
        {
            BaseUrl = "http://127.0.0.1:41000/v1",
        });
        service.SetCredential("loopback.custom", "responses", Credential);

        _ = Assert.Throws<ProviderConnectionException>(() =>
            ProviderRegistryFactory.CreateFromEnvironment(
                conformanceMode: true,
                stateRoot: stateRoot,
                workspace: workspace));
        await using var registry = ProviderRegistryFactory.CreateFromEnvironment(
            conformanceMode: true,
            stateRoot: stateRoot,
            workspace: workspace,
            allowCustomProviderLoopback: true);
        Assert.Contains(registry.Providers, static provider => provider.Id == "loopback.custom");
        Assert.False(Assert.Single(registry.ListModels("loopback.custom")).Capabilities.Tools);
    }

    /// <summary>
    /// 功能：确认 faux、canonical 与 relay 命名空间不能被自定义连接遮蔽。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerId">必须拒绝的保留 Provider ID。</param>
    [Theory]
    [InlineData("faux")]
    [InlineData("anthropic")]
    [InlineData("relay-catalog")]
    public void ReservedProviderIdentitiesAreRejected(string providerId)
    {
        using var temporary = new TemporaryDirectory();
        var store = new CustomProviderConnectionStore(Path.Combine(temporary.Path, "state"));

        var exception = Assert.Throws<ProviderConnectionException>(() =>
            store.Create(CreateInput(providerId, "Reserved")));

        Assert.Equal("invalid_params", exception.Error.Details.Kind);
        Assert.Equal("providerId", exception.Error.Details.Field);
    }

    /// <summary>
    /// 功能：确认应用 state root 本身是 symlink 时自定义连接存储失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void StateRootSymlinkIsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var target = Directory.CreateDirectory(Path.Combine(temporary.Path, "target")).FullName;
        var linkedState = Path.Combine(temporary.Path, "linked-state");
        _ = Directory.CreateSymbolicLink(linkedState, target);

        var exception = Assert.Throws<ProviderConnectionException>(() =>
            _ = new CustomProviderConnectionStore(linkedState));

        Assert.Equal("provider_connection_store_invalid", exception.Error.Details.Kind);
    }

    /// <summary>
    /// 功能：确认凭据写入在连接锁竞争时先失败，绝不绕过存在性临界区产生 orphan secret。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public async Task CredentialMutationUsesConnectionThenCredentialLockOrder()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        var stateRoot = Path.Combine(temporary.Path, "state");
        var service = new CustomProviderConnectionService(stateRoot, workspace);
        _ = service.Create(CreateInput("alpha", "Alpha"));
        var store = new CustomProviderConnectionStore(stateRoot);
        using var entered = new ManualResetEventSlim(initialState: false);
        using var release = new ManualResetEventSlim(initialState: false);
        var holder = Task.Run(() => store.ExecuteForProvider(
            "alpha",
            _ =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var exception = Assert.Throws<ProviderConnectionException>(() =>
                service.SetCredential("alpha", "responses", Credential));
            Assert.Equal("provider_connection_store_locked", exception.Error.Details.Kind);
            var coordinated = Assert.Throws<ProviderConnectionException>(() =>
                service.SetCredentialCoordinated("canonical.test", Credential));
            Assert.Equal("provider_connection_store_locked", coordinated.Error.Details.Kind);
            Assert.Empty(new ProviderCredentialStore(stateRoot, workspace).List());
        }
        finally
        {
            release.Set();
            await holder;
        }
    }

    /// <summary>
    /// 功能：创建测试使用的有效非敏感连接输入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerId">唯一 Provider ID。</param>
    /// <param name="displayName">显示名。</param>
    /// <returns>固定 HTTPS、Chat family 和单模型输入。</returns>
    private static ProviderConnectionInput CreateInput(string providerId, string displayName)
    {
        return new ProviderConnectionInput(
            displayName,
            providerId,
            "openai-completions",
            "https://example.invalid/v1",
            "https://example.invalid/v1/models",
            ["model-a"],
            SupportsTools: false,
            SupportsImageInput: false,
            SupportsImageOutput: false,
            null,
            Enabled: true);
    }

    /// <summary>
    /// 功能：构造测试客户端 initialize 请求。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="id">JSON-RPC 请求 ID。</param>
    /// <returns>单行、无换行 initialize JSON。</returns>
    private static string InitializeRequest(string id)
    {
        return "{\"jsonrpc\":\"2.0\",\"id\":\"" + id +
            "\",\"method\":\"initialize\",\"params\":{\"protocolVersions\":[\"0.1\"],\"client\":{\"name\":\"test\",\"version\":\"0.1\"},\"capabilities\":{}}}";
    }

    /// <summary>
    /// 功能：向同一 daemon 建立一次内存 NDJSON 连接并克隆全部响应帧。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="daemon">待测 daemon。</param>
    /// <param name="requests">以 LF 结尾的完整请求流。</param>
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

    /// <summary>
    /// 功能：通过受保护边界取得 adapter 目标地址，且不发送网络请求。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="provider">待测 Provider。</param>
    /// <param name="modelId">allowlist 中模型。</param>
    /// <returns>adapter 构造的完整 endpoint。</returns>
    private static Uri CreateRequestEndpoint(IProvider provider, string modelId)
    {
        var method = provider.GetType().GetMethod(
            "CreateRequestEndpoint",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("provider endpoint method was not found");
        var request = new ProviderRequest(
            new ProviderSelection(provider.Id, modelId, provider.ApiFamily),
            [],
            [],
            null);
        return (Uri)(method.Invoke(provider, [request])
            ?? throw new InvalidOperationException("provider endpoint was null"));
    }

    /// <summary>
    /// 功能：按 canonical base64url 合同构造隔离测试 credential 叶路径。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="stateRoot">测试 state root。</param>
    /// <param name="providerId">合法 Provider ID。</param>
    /// <returns>对应 `.credential` 叶路径。</returns>
    private static string CredentialEntryPath(string stateRoot, string providerId)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(providerId))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return Path.Combine(
            stateRoot,
            "credentials",
            "provider-credentials.d",
            encoded + ".credential");
    }

    /// <summary>
    /// 功能：返回固定模型目录响应，并只记录发现请求的非敏感断言状态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class DiscoveryResponseHandler(
        HttpStatusCode statusCode,
        byte[] body) : HttpMessageHandler
    {
        /// <summary>
        /// 功能：取得测试观察到的请求方法。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        internal HttpMethod? Method { get; private set; }

        /// <summary>
        /// 功能：取得测试观察到的公开模型目录 URI。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        internal Uri? RequestUri { get; private set; }

        /// <summary>
        /// 功能：取得请求是否携带预期 Bearer credential，不保留原始 header。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        internal bool SawExpectedBearer { get; private set; }

        /// <summary>
        /// 功能：取得构造响应前可选执行的并发测试动作。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        internal Action? BeforeResponse { get; set; }

        /// <summary>
        /// 功能：记录非敏感请求形状并返回每次独立的固定响应。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="request">模型发现 GET。</param>
        /// <param name="cancellationToken">发现总时限与调用方取消信号。</param>
        /// <returns>状态和正文由测试构造的内存响应。</returns>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Method = request.Method;
            RequestUri = request.RequestUri;
            SawExpectedBearer = request.Headers.TryGetValues("Authorization", out var values) &&
                values.Single() == "Bearer " + Credential;
            BeforeResponse?.Invoke();
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(body),
            });
        }
    }

    /// <summary>
    /// 功能：在测试期间清空会改变 production registry 的环境变量，并在结束时恢复。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class ProviderEnvironmentIsolation : IDisposable
    {
        private static readonly string[] Names =
        [
            ProviderExecutableRouteSnapshot.ConfigurationEnvironmentVariable,
            ProviderIdentityAdvertisement.ConfigurationEnvironmentVariable,
            "QXNM_FORGE_CONFORMANCE",
            "QXNM_FORGE_PROVIDER_CONFORMANCE",
            "OPENAI_API_KEY",
            "GROQ_API_KEY",
            "MINIMAX_API_KEY",
            "MISTRAL_API_KEY",
            "ANTHROPIC_API_KEY",
            "GEMINI_API_KEY",
            "OPENROUTER_API_KEY",
        ];

        private readonly Dictionary<string, string?> values = Names.ToDictionary(
            static name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

        /// <summary>
        /// 功能：保存并清空 Provider 选择环境，保证测试不读取开发机 credential。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        internal ProviderEnvironmentIsolation()
        {
            foreach (var name in Names)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        /// <summary>
        /// 功能：为单个定向案例设置已纳入恢复集合的 Provider 环境门。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="name">Names allowlist 内的环境变量名。</param>
        /// <param name="value">测试值。</param>
        internal void Set(string name, string? value)
        {
            Assert.True(values.ContainsKey(name));
            Environment.SetEnvironmentVariable(name, value);
        }

        /// <summary>
        /// 功能：恢复测试前全部 Provider 环境变量。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        public void Dispose()
        {
            foreach (var pair in values)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }
}
