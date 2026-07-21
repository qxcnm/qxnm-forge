using System.Text.Json;
using QxnmForge.Protocol;
using QxnmForge.Provider;
using QxnmForge.Serialization;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证冻结 Provider 配置模板目录的筛选、稳定排序与非执行 wire 语义。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ProviderCatalogServiceTests
{
    /// <summary>
    /// 功能：逐字段锁定 .NET 模板目录与公共跨语言 golden fixture 完全一致。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void ListMatchesSharedConfigurationTemplateFixture()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "provider-catalog",
            "configuration-templates.json");
        using var expected = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
        var actual = JsonSerializer.SerializeToElement(
            new ProviderCatalogListResult(new ProviderCatalogService().List()),
            JsonDefaults.Options);

        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual));
    }

    /// <summary>
    /// 功能：锁定二十条跨语言模板、关键受控名称、固定 endpoint 与可创建连接 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void ListReturnsTwentyValidatedConfigurationTemplates()
    {
        var service = new ProviderCatalogService();
        var templates = service.List();

        Assert.Equal(
            [
                "ant-ling", "cerebras", "deepseek", "fireworks", "groq", "huggingface",
                "moonshotai", "moonshotai-cn", "nvidia", "opencode", "opencode-go",
                "openrouter", "together", "xai", "xiaomi", "xiaomi-token-plan-ams",
                "xiaomi-token-plan-cn", "xiaomi-token-plan-sgp", "zai", "zai-coding-cn",
            ],
            templates.Select(static template => template.TemplateId));
        Assert.NotSame(templates, service.List());

        var groq = Assert.Single(templates, static template => template.TemplateId == "groq");
        Assert.Equal("Groq", groq.DisplayName);
        Assert.Equal("custom-groq", groq.SuggestedProviderId);
        Assert.Equal("https://api.groq.com/openai/v1", groq.DefaultBaseUrl);

        var nvidia = Assert.Single(templates, static template => template.TemplateId == "nvidia");
        Assert.Equal("NVIDIA NIM", nvidia.DisplayName);
        Assert.Equal("https://integrate.api.nvidia.com/v1", nvidia.DefaultBaseUrl);

        Assert.All(templates, static template =>
        {
            Assert.Equal("openai-completions", template.ApiFamily);
            Assert.Equal("openai-models", template.ModelDiscovery);
            Assert.Null(template.LogoAssetId);
            CustomProviderConnectionStore.ValidateInput(
                new ProviderConnectionInput(
                    template.DisplayName,
                    template.SuggestedProviderId,
                    template.ApiFamily,
                    template.DefaultBaseUrl,
                    [],
                    template.LogoAssetId,
                    Enabled: true),
                allowLoopbackHttp: false);
        });
    }

    /// <summary>
    /// 功能：确认目录 wire 项精确包含七字段并显式输出 null logo，不携带可执行或 credential 状态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void WireProjectionContainsOnlyConfigurationTemplateFields()
    {
        var result = new ProviderCatalogListResult(new ProviderCatalogService().List());
        var json = JsonSerializer.SerializeToElement(result, JsonDefaults.Options);
        var template = json.GetProperty("templates")[0];

        Assert.Equal(
            [
                "templateId", "displayName", "suggestedProviderId", "apiFamily",
                "defaultBaseUrl", "modelDiscovery", "logoAssetId",
            ],
            template.EnumerateObject().Select(static property => property.Name));
        Assert.Equal(JsonValueKind.Null, template.GetProperty("logoAssetId").ValueKind);
        Assert.False(template.TryGetProperty("credentialConfigured", out _));
        Assert.False(template.TryGetProperty("configured", out _));
        Assert.False(template.TryGetProperty("available", out _));
        Assert.False(template.TryGetProperty("executable", out _));
        Assert.False(template.TryGetProperty("models", out _));
    }
}
