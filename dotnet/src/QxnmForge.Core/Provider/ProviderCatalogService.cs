using System.Text.Json;
using System.Text.Json.Serialization;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：表示可用于创建自定义 OpenAI-compatible 连接的非敏感配置模板。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="TemplateId">来自冻结 Provider ID 或受控官方入口表的稳定模板 ID。</param>
/// <param name="DisplayName">受控用户可见名称。</param>
/// <param name="SuggestedProviderId">避开 canonical 保留身份的自定义 Provider ID 建议。</param>
/// <param name="ApiFamily">当前固定为 openai-completions。</param>
/// <param name="DefaultBaseUrl">冻结模型目录推导或经 ADR 审计的唯一 HTTPS base。</param>
/// <param name="ModelDiscovery">保存连接后可显式尝试的模型发现契约。</param>
/// <param name="LogoAssetId">可选本地公开 Logo 资源 ID；当前目录不提供。</param>
public sealed record ProviderConnectionTemplate(
    string TemplateId,
    string DisplayName,
    string SuggestedProviderId,
    string ApiFamily,
    string DefaultBaseUrl,
    string ModelDiscovery,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? LogoAssetId);

/// <summary>
/// 功能：从已验证冻结目录和受控官方兼容入口构造品牌中立连接模板目录。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ProviderCatalogService
{
    private const string ApiFamily = "openai-completions";
    private const string AdapterId = "openai-completions-v1";
    private const string ModelCatalogId = "pi-v1-frozen";
    private const string ModelDiscovery = "openai-models";

    private static readonly IReadOnlyDictionary<string, string> DisplayNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ant-ling"] = "Ant Ling",
            ["cerebras"] = "Cerebras",
            ["deepseek"] = "DeepSeek",
            ["fireworks"] = "Fireworks AI",
            ["groq"] = "Groq",
            ["huggingface"] = "Hugging Face",
            ["moonshotai"] = "Moonshot AI",
            ["moonshotai-cn"] = "Moonshot AI (China)",
            ["nvidia"] = "NVIDIA NIM",
            ["opencode"] = "OpenCode",
            ["opencode-go"] = "OpenCode Go",
            ["openrouter"] = "OpenRouter",
            ["together"] = "Together AI",
            ["xai"] = "xAI",
            ["xiaomi"] = "Xiaomi MiMo",
            ["xiaomi-token-plan-ams"] = "Xiaomi MiMo Token Plan (Amsterdam)",
            ["xiaomi-token-plan-cn"] = "Xiaomi MiMo Token Plan (China)",
            ["xiaomi-token-plan-sgp"] = "Xiaomi MiMo Token Plan (Singapore)",
            ["zai"] = "Z.AI",
            ["zai-coding-cn"] = "Z.AI Coding (China)",
        };

    private static readonly Lazy<ProviderConnectionTemplate[]> SharedTemplates =
        new(LoadTemplates, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly ProviderConnectionTemplate[] templates;

    /// <summary>
    /// 功能：加载一次冻结目录与受控官方入口并创建只读 Provider 配置模板服务。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <remarks>不变量：构造不读取环境变量、CredentialStore、网络或可执行 registry 状态。</remarks>
    /// <exception cref="ProviderIdentityAdvertisementException">冻结目录、官方入口或模板筛选不满足固定安全边界。</exception>
    public ProviderCatalogService()
    {
        templates = SharedTemplates.Value;
    }

    /// <summary>
    /// 功能：返回按 templateId ordinal 排序的自定义 Provider 配置模板。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>不含 secret、credential presence 或可执行状态的独立数组。</returns>
    /// <remarks>不变量：modelDiscovery 仅表示保存后可尝试 GET /models，绝不声明远端已验证或 route 已配置。</remarks>
    public IReadOnlyList<ProviderConnectionTemplate> List()
    {
        return templates.ToArray();
    }

    /// <summary>
    /// 功能：读取冻结文档并合并受控官方入口，再构造稳定模板快照。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>精确二十三条、按模板 ID 排序的不可变值数组。</returns>
    /// <remarks>不变量：任何失败都映射为不含 URL、目录内容或环境名称的统一异常。</remarks>
    /// <exception cref="ProviderIdentityAdvertisementException">冻结文档、JSON、官方入口或候选闭包无效。</exception>
    private static ProviderConnectionTemplate[] LoadTemplates()
    {
        try
        {
            var (manifestBytes, catalogBytes) =
                ProviderIdentityAdvertisement.LoadValidatedFrozenDocuments();
            using var manifest = JsonDocument.Parse(manifestBytes);
            using var catalog = JsonDocument.Parse(catalogBytes);
            var templates = BuildTemplates(manifest.RootElement, catalog.RootElement)
                .Concat(BuildAuditedOfficialTemplates())
                .OrderBy(static template => template.TemplateId, StringComparer.Ordinal)
                .ToArray();
            if (templates.Length != 23 ||
                templates.Select(static template => template.TemplateId)
                    .Distinct(StringComparer.Ordinal)
                    .Count() != templates.Length ||
                templates.Select(static template => template.SuggestedProviderId)
                    .Distinct(StringComparer.Ordinal)
                    .Count() != templates.Length)
            {
                throw new InvalidDataException("provider template set drifted");
            }

            return templates;
        }
        catch (ProviderIdentityAdvertisementException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ProviderIdentityAdvertisementException();
        }
    }

    /// <summary>
    /// 功能：把已全量验证的 manifest route 与 model endpoint 交叉筛选为连接模板。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="manifest">已通过 digest、引用和严格 JSON 验证的 Provider manifest。</param>
    /// <param name="catalog">已通过 digest、计数和严格 JSON 验证的模型目录。</param>
    /// <returns>只包含现有 custom Chat/Bearer 与显式模型发现可尝试配置的模板。</returns>
    /// <remarks>不变量：模板资格只来自冻结 route、单一 API-key auth 与全部模型的共同 fixed HTTPS endpoint 证据。</remarks>
    /// <exception cref="InvalidDataException">候选缺少受控名称、固定单一 HTTPS base 或候选集合漂移。</exception>
    private static ProviderConnectionTemplate[] BuildTemplates(
        JsonElement manifest,
        JsonElement catalog)
    {
        var templates = new List<ProviderConnectionTemplate>();
        var candidateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in manifest.GetProperty("providers").EnumerateArray())
        {
            var providerId = provider.GetProperty("id").GetString()!;
            var routes = provider.GetProperty("routes")
                .EnumerateArray()
                .Where(IsEligibleRoute)
                .ToArray();
            if (routes.Length == 0)
            {
                continue;
            }

            if (routes.Length != 1 ||
                !candidateIds.Add(providerId) ||
                !DisplayNames.TryGetValue(providerId, out var displayName))
            {
                throw new InvalidDataException("provider template identity drifted");
            }

            var baseUrl = ReadFixedBaseUrl(catalog, providerId);
            templates.Add(new ProviderConnectionTemplate(
                providerId,
                displayName,
                "custom-" + providerId,
                ApiFamily,
                baseUrl,
                ModelDiscovery,
                LogoAssetId: null));
        }

        if (candidateIds.Count != DisplayNames.Count ||
            !candidateIds.SetEquals(DisplayNames.Keys))
        {
            throw new InvalidDataException("provider template set drifted");
        }

        return templates
            .OrderBy(static template => template.TemplateId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 功能：构造经过固定 endpoint 审计的官方 OpenAI-compatible 连接模板。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>OpenAI、Anthropic Claude 与 Google Gemini 三条非敏感模板。</returns>
    /// <remarks>不变量：建议 ID 不遮蔽 canonical 身份，base 仅使用官方 HTTPS OpenAI-compatible 入口。</remarks>
    /// <exception cref="ProviderConnectionException">任一固定模板不再满足自定义连接安全边界。</exception>
    private static ProviderConnectionTemplate[] BuildAuditedOfficialTemplates()
    {
        ProviderConnectionTemplate[] templates =
        [
            new(
                "anthropic",
                "Anthropic Claude",
                "custom-anthropic",
                ApiFamily,
                "https://api.anthropic.com/v1",
                ModelDiscovery,
                LogoAssetId: null),
            new(
                "google",
                "Google Gemini",
                "custom-google",
                ApiFamily,
                "https://generativelanguage.googleapis.com/v1beta/openai",
                ModelDiscovery,
                LogoAssetId: null),
            new(
                "openai",
                "OpenAI",
                "custom-openai",
                ApiFamily,
                "https://api.openai.com/v1",
                ModelDiscovery,
                LogoAssetId: null),
        ];
        foreach (var template in templates)
        {
            CustomProviderConnectionStore.ValidateInput(
                new ProviderConnectionInput(
                    template.DisplayName,
                    template.SuggestedProviderId,
                    template.ApiFamily,
                    template.DefaultBaseUrl,
                    NativeProviderEndpoint.Append(
                        new Uri(template.DefaultBaseUrl, UriKind.Absolute),
                        "/models").AbsoluteUri,
                    [],
                    SupportsTools: false,
                    template.LogoAssetId,
                    Enabled: true),
                allowLoopbackHttp: false);
        }

        return templates;
    }

    /// <summary>
    /// 功能：判断 manifest route 是否能映射到现有自定义 OpenAI Chat 连接形状。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="route">当前 Provider 的单条已验证 route。</param>
    /// <returns>family、adapter、单一 API-key auth 与目录引用全部匹配时为 true。</returns>
    /// <remarks>不变量：不读取 credential 值，也不把 header policy 投影为 custom adapter 的能力声明。</remarks>
    private static bool IsEligibleRoute(JsonElement route)
    {
        var authentication = route.GetProperty("authProfileIds")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .ToArray();
        var endpoint = route.GetProperty("endpoint");
        return route.GetProperty("media").GetString() == "text" &&
            route.GetProperty("apiFamily").GetString() == ApiFamily &&
            route.GetProperty("adapterId").GetString() == AdapterId &&
            route.GetProperty("modelCatalogId").GetString() == ModelCatalogId &&
            authentication.SequenceEqual(["api-key"], StringComparer.Ordinal) &&
            endpoint.GetProperty("source").GetString() == "model-catalog" &&
            endpoint.GetProperty("templateBindings").GetArrayLength() == 0;
    }

    /// <summary>
    /// 功能：确认候选 Provider 的全部 openai-completions 模型共享唯一 fixed HTTPS base。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="catalog">已完整验证的冻结模型目录。</param>
    /// <param name="providerId">待闭合的候选 Provider ID。</param>
    /// <returns>可直接作为自定义连接 defaultBaseUrl 的绝对 HTTPS 文本。</returns>
    /// <remarks>不变量：不解析模板变量，不接受 user-info、query、fragment 或多个 endpoint。</remarks>
    /// <exception cref="InvalidDataException">没有模型、endpoint 非 fixed/HTTPS 或 base 不一致。</exception>
    private static string ReadFixedBaseUrl(JsonElement catalog, string providerId)
    {
        string? baseUrl = null;
        var modelCount = 0;
        foreach (var model in catalog.GetProperty("models").EnumerateArray())
        {
            if (model.GetProperty("providerId").GetString() != providerId ||
                model.GetProperty("apiFamily").GetString() != ApiFamily ||
                model.GetProperty("media").GetString() != "text")
            {
                continue;
            }

            modelCount++;
            var endpoint = model.GetProperty("endpoint");
            if (endpoint.GetProperty("strategy").GetString() != "fixed")
            {
                throw new InvalidDataException("provider template endpoint is not fixed");
            }

            var current = endpoint.GetProperty("baseUrl").GetString()!;
            if (!Uri.TryCreate(current, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                string.IsNullOrEmpty(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                (baseUrl is not null && !string.Equals(baseUrl, current, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("provider template endpoint is invalid");
            }

            baseUrl = current;
        }

        return modelCount > 0 && baseUrl is not null
            ? baseUrl
            : throw new InvalidDataException("provider template has no catalog models");
    }
}
