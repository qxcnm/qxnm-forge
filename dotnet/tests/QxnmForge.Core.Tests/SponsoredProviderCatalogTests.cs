using System.Text.Json;
using System.Text.Json.Nodes;
using QxnmForge.Provider;
using QxnmForge.Serialization;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 .NET 推广目录密钥、签名、严格字段和终端安全边界。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class SponsoredProviderCatalogTests
{
    /// <summary>
    /// 功能：确认远端验签成功后写入内容缓存，随后 offline 重新验签并读取同一版本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task RemoteCatalogPersistsAndOfflineModeRevalidatesCache()
    {
        using var directory = new TemporaryDirectory();
        var privateKey = System.IO.Path.Combine(directory.Path, "private.der");
        var publicKey = System.IO.Path.Combine(directory.Path, "public.json");
        var catalog = System.IO.Path.Combine(directory.Path, "catalog.json");
        var envelope = System.IO.Path.Combine(directory.Path, "envelope.json");
        var state = System.IO.Path.Combine(directory.Path, "state");
        File.WriteAllBytes(catalog, ValidCatalogBytes(7));
        SponsoredCatalogPublisher.GenerateKeyPair("owner-2026-01", privateKey, publicKey);
        SponsoredCatalogPublisher.Sign(catalog, privateKey, publicKey, envelope);

        using (var service = new SponsoredCatalogService(
                   state,
                   new HttpClient(new StaticResponseHandler(File.ReadAllBytes(envelope)))))
        {
            service.Configure("https://catalog.example/sponsors.json", publicKey);
            var remote = await service.LoadAsync(false, CancellationToken.None);
            Assert.Equal(SponsoredCatalogOrigin.Remote, remote.Origin);
            Assert.Equal<ulong>(7, remote.Catalog!.CatalogVersion);
        }

        using (var service = new SponsoredCatalogService(
                   state,
                   new HttpClient(new StaticResponseHandler([]))))
        {
            var cached = await service.LoadAsync(true, CancellationToken.None);
            Assert.Equal(SponsoredCatalogOrigin.Cache, cached.Origin);
            Assert.Equal<ulong>(7, cached.Catalog!.CatalogVersion);
        }
    }

    /// <summary>
    /// 功能：确认运行时临时密钥可以签名并由 .NET 原生验签器读取。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void GeneratedKeySignsAndVerifiesCatalog()
    {
        using var directory = new TemporaryDirectory();
        var privateKey = System.IO.Path.Combine(directory.Path, "private.der");
        var publicKey = System.IO.Path.Combine(directory.Path, "public.json");
        var catalog = System.IO.Path.Combine(directory.Path, "catalog.json");
        var envelope = System.IO.Path.Combine(directory.Path, "envelope.json");
        File.WriteAllBytes(catalog, ValidCatalogBytes(1));

        SponsoredCatalogPublisher.GenerateKeyPair(
            "owner-2026-01",
            privateKey,
            publicKey);
        SponsoredCatalogPublisher.Sign(catalog, privateKey, publicKey, envelope);
        var verified = SponsoredCatalogPublisher.Verify(envelope, publicKey);

        Assert.Equal<ulong>(1, verified.CatalogVersion);
        Assert.Equal("example-relay", Assert.Single(verified.Entries).Id);
    }

    /// <summary>
    /// 功能：确认签名后修改 payload 会被拒绝且不会因为 JSON envelope 仍合法而通过。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void TamperedPayloadIsRejected()
    {
        using var directory = new TemporaryDirectory();
        var privateKey = System.IO.Path.Combine(directory.Path, "private.der");
        var publicKey = System.IO.Path.Combine(directory.Path, "public.json");
        var catalog = System.IO.Path.Combine(directory.Path, "catalog.json");
        var envelope = System.IO.Path.Combine(directory.Path, "envelope.json");
        var tampered = System.IO.Path.Combine(directory.Path, "tampered.json");
        File.WriteAllBytes(catalog, ValidCatalogBytes(1));
        SponsoredCatalogPublisher.GenerateKeyPair("owner-2026-01", privateKey, publicKey);
        SponsoredCatalogPublisher.Sign(catalog, privateKey, publicKey, envelope);

        var node = JsonNode.Parse(File.ReadAllBytes(envelope))!.AsObject();
        var payload = Convert.FromBase64String(node["payload"]!.GetValue<string>());
        payload[0] ^= 1;
        node["payload"] = Convert.ToBase64String(payload);
        File.WriteAllText(tampered, node.ToJsonString(JsonDefaults.Options));

        var exception = Assert.Throws<SponsoredCatalogException>(() =>
            SponsoredCatalogPublisher.Verify(tampered, publicKey));
        Assert.Equal("signature_invalid", exception.Kind);
    }

    /// <summary>
    /// 功能：确认控制字符广告和非 HTTPS API base 会使整个目录失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void TerminalInjectionAndInsecureEndpointAreRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var catalog = new SponsoredProviderCatalog(
            "0.1",
            1,
            UtcText(now),
            UtcText(now.AddDays(30)),
            [
                new SponsoredProviderEntry(
                    "bad",
                    "恶意\u001b[31m",
                    "bad",
                    "openai-completions",
                    "http://relay.example/v1",
                    "https://relay.example/register",
                    "推广",
                    1),
            ]);

        Assert.Throws<SponsoredCatalogException>(() =>
            SponsoredCatalogService.ValidateCatalog(catalog, now));
    }

    /// <summary>
    /// 功能：生成当前有效、带一个明确返佣披露条目的严格 catalog JSON。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="version">单调目录版本。</param>
    /// <returns>UTF-8 JSON 字节。</returns>
    private static byte[] ValidCatalogBytes(ulong version)
    {
        var now = DateTimeOffset.UtcNow;
        return JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = "0.1",
                catalogVersion = version,
                issuedAt = UtcText(now),
                expiresAt = UtcText(now.AddDays(30)),
                entries = new[]
                {
                    new
                    {
                        id = "example-relay",
                        displayName = "示例中转站",
                        description = "仅用于测试的推广条目",
                        apiFamily = "openai-completions",
                        apiBaseUrl = "https://relay.example/v1",
                        signupUrl = "https://relay.example/register?ref=test",
                        commissionDisclosure = "通过该链接注册，发行方可能获得佣金",
                        priority = 100,
                    },
                },
            },
            JsonDefaults.Options);
    }

    /// <summary>
    /// 功能：把测试时点编码为规范要求的秒精度 Z 后缀 UTC 文本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">任意 offset 时点。</param>
    /// <returns>UTC RFC 3339 文本。</returns>
    private static string UtcText(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 功能：为推广目录测试返回固定 HTTPS response，不访问真实网络。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class StaticResponseHandler(byte[] bytes) : HttpMessageHandler
    {
        /// <summary>
        /// 功能：按每次调用创建独立 200 response 和 byte content。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="request">未携带用户数据的 GET；测试不检查外网。</param>
        /// <param name="cancellationToken">测试取消信号。</param>
        /// <returns>固定内存响应。</returns>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            });
        }
    }
}
