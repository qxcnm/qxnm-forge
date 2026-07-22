using System.Text;
using System.Text.Json;
using QxnmForge.Agent;
using QxnmForge.Daemon;
using QxnmForge.Provider;
using QxnmForge.Session;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 providerCatalog/list 的 stdio capability、响应形状和严格空参数边界。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ProviderCatalogDaemonTests
{
    /// <summary>
    /// 功能：确认模板目录始终可查询、未知参数被拒绝且模板不会进入可执行 Provider 广告。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>daemon 处理三帧并完成响应写入的异步任务。</returns>
    [Fact]
    public async Task ListIsAdvertisedStrictAndConfigurationOnly()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        await using var repository = new SessionRepository(
            Path.Combine(temporary.Path, "sessions"),
            workspace);
        var daemon = new StdioDaemon(
            repository,
            new AgentService(repository, new FauxProvider()),
            conformanceMode: false);
        var requests = string.Join('\n',
            "{\"jsonrpc\":\"2.0\",\"id\":\"initialize\",\"method\":\"initialize\",\"params\":{\"protocolVersions\":[\"0.1\"],\"client\":{\"name\":\"test\",\"version\":\"0.1\"},\"capabilities\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"catalog\",\"method\":\"providerCatalog/list\",\"params\":{}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"invalid\",\"method\":\"providerCatalog/list\",\"params\":{\"configured\":true}}") + "\n";
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(requests));
        await using var output = new MemoryStream();

        await daemon.RunAsync(input, output, CancellationToken.None);
        var frames = Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => JsonDocument.Parse(line))
            .ToArray();
        try
        {
            Assert.Equal(3, frames.Length);
            var capabilities = frames[0].RootElement
                .GetProperty("result")
                .GetProperty("capabilities");
            var methods = capabilities.GetProperty("methods")
                .EnumerateArray()
                .Select(static method => method.GetString())
                .ToArray();
            Assert.Contains("providerCatalog/list", methods);
            Assert.Equal("models/list", methods[^2]);
            Assert.Equal("providerCatalog/list", methods[^1]);
            var executableProvider = Assert.Single(
                capabilities.GetProperty("providers").EnumerateArray());
            Assert.Equal("faux", executableProvider.GetProperty("id").GetString());

            var templates = frames[1].RootElement
                .GetProperty("result")
                .GetProperty("templates");
            Assert.Equal(23, templates.GetArrayLength());
            Assert.Equal("ant-ling", templates[0].GetProperty("templateId").GetString());
            Assert.Equal("zai-coding-cn", templates[22].GetProperty("templateId").GetString());
            Assert.Contains(templates.EnumerateArray(), static template =>
                template.GetProperty("templateId").GetString() == "anthropic" &&
                template.GetProperty("suggestedProviderId").GetString() == "custom-anthropic" &&
                template.GetProperty("defaultBaseUrl").GetString() == "https://api.anthropic.com/v1");
            Assert.Contains(templates.EnumerateArray(), static template =>
                template.GetProperty("templateId").GetString() == "google" &&
                template.GetProperty("suggestedProviderId").GetString() == "custom-google" &&
                template.GetProperty("defaultBaseUrl").GetString() ==
                    "https://generativelanguage.googleapis.com/v1beta/openai");
            Assert.Contains(templates.EnumerateArray(), static template =>
                template.GetProperty("templateId").GetString() == "openai" &&
                template.GetProperty("suggestedProviderId").GetString() == "custom-openai" &&
                template.GetProperty("defaultBaseUrl").GetString() == "https://api.openai.com/v1");
            Assert.All(templates.EnumerateArray(), static template =>
            {
                Assert.Equal("openai-models", template.GetProperty("modelDiscovery").GetString());
                Assert.Equal(JsonValueKind.Null, template.GetProperty("logoAssetId").ValueKind);
                Assert.False(template.TryGetProperty("credentialConfigured", out _));
                Assert.False(template.TryGetProperty("executable", out _));
            });

            var error = frames[2].RootElement.GetProperty("error");
            Assert.Equal(-32602, error.GetProperty("code").GetInt32());
            Assert.Equal(
                "invalid_params",
                error.GetProperty("details").GetProperty("kind").GetString());
        }
        finally
        {
            foreach (var frame in frames)
            {
                frame.Dispose();
            }
        }
    }
}
