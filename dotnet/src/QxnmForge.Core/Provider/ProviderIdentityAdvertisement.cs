using System.Buffers;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using QxnmForge.Domain;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：表示 manifest 驱动的 Provider 身份广告配置被统一脱敏地拒绝。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ProviderIdentityAdvertisementException : Exception
{
    /// <summary>
    /// 功能：创建不包含路径、digest、环境名称或实例值的稳定启动异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public ProviderIdentityAdvertisementException()
        : base("Provider identity advertisement configuration is invalid.")
    {
    }
}

/// <summary>
/// 功能：严格加载冻结 manifest/catalog，并从无值 presence 配置生成 identity-only 模型快照。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal static class ProviderIdentityAdvertisement
{
    internal const string ConfigurationEnvironmentVariable =
        "AGENT_PROVIDER_IDENTITY_CONFORMANCE_CONFIG";

    private const string ManifestResource = "QxnmForge.Spec.providers.v1.json";
    private const string CatalogResource = "QxnmForge.Spec.models.v1.json";
    private const string ExpectedManifestSha256 =
        "7b420d7b1ff89248be186525cb6da9cb038a29b16dcb4d5339a68ce5f0e615d1";
    private const string ExpectedCatalogRecordsSha256 =
        "348afa7405fa435492ec0514f9a8dc42d0861a5f99bc1d898f75b9a81f611bfa";
    private const int MaxConfigurationBytes = 1_048_576;
    private const int MaxManifestBytes = 2_097_152;
    private const int MaxCatalogBytes = 16_777_216;
    private const int MaxJsonDepth = 128;

    private static readonly HashSet<string> ApiFamilies = new(StringComparer.Ordinal)
    {
        "openai-completions",
        "mistral-conversations",
        "openai-responses",
        "azure-openai-responses",
        "openai-codex-responses",
        "anthropic-messages",
        "bedrock-converse-stream",
        "google-generative-ai",
        "google-vertex",
        "openrouter-images",
    };

    private static readonly HashSet<string> CapabilityFeatures = new(StringComparer.Ordinal)
    {
        "authentication",
        "text",
        "streaming",
        "tools",
        "reasoning",
        "image_input",
        "image_output",
    };

    private static readonly HashSet<string> Quirks = new(StringComparer.Ordinal)
    {
        "cacheControlFormat",
        "forceAdaptiveThinking",
        "maxTokensField",
        "requiresReasoningContentOnAssistantMessages",
        "sendSessionAffinityHeaders",
        "supportsCacheControlOnTools",
        "supportsDeveloperRole",
        "supportsEagerToolInputStreaming",
        "supportsLongCacheRetention",
        "supportsReasoningEffort",
        "supportsStore",
        "supportsStrictMode",
        "supportsTemperature",
        "thinkingFormat",
        "zaiToolStream",
    };

    private static readonly HashSet<string> CompatibilityFields = new(StringComparer.Ordinal)
    {
        "cacheControlFormat",
        "forceAdaptiveThinking",
        "maxTokensField",
        "requiresReasoningContentOnAssistantMessages",
        "sendSessionAffinityHeaders",
        "supportsCacheControlOnTools",
        "supportsDeveloperRole",
        "supportsEagerToolInputStreaming",
        "supportsLongCacheRetention",
        "supportsReasoningEffort",
        "supportsStore",
        "supportsStrictMode",
        "supportsTemperature",
        "thinkingFormat",
        "zaiToolStream",
    };

    private static readonly HashSet<string> ThinkingLevelFields = new(StringComparer.Ordinal)
    {
        "off", "minimal", "low", "medium", "high", "xhigh",
    };

    private static readonly Dictionary<string, int> TextModelCounts =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["amazon-bedrock"] = 106,
            ["ant-ling"] = 3,
            ["anthropic"] = 14,
            ["azure-openai-responses"] = 42,
            ["cerebras"] = 3,
            ["cloudflare-ai-gateway"] = 38,
            ["cloudflare-workers-ai"] = 13,
            ["deepseek"] = 2,
            ["fireworks"] = 16,
            ["github-copilot"] = 25,
            ["google"] = 16,
            ["google-vertex"] = 10,
            ["groq"] = 7,
            ["huggingface"] = 49,
            ["kimi-coding"] = 3,
            ["minimax"] = 3,
            ["minimax-cn"] = 3,
            ["mistral"] = 30,
            ["moonshotai"] = 9,
            ["moonshotai-cn"] = 9,
            ["nvidia"] = 20,
            ["openai"] = 42,
            ["openai-codex"] = 4,
            ["opencode"] = 51,
            ["opencode-go"] = 13,
            ["openrouter"] = 267,
            ["together"] = 20,
            ["vercel-ai-gateway"] = 188,
            ["xai"] = 8,
            ["xiaomi"] = 6,
            ["xiaomi-token-plan-ams"] = 3,
            ["xiaomi-token-plan-cn"] = 3,
            ["xiaomi-token-plan-sgp"] = 3,
            ["zai"] = 6,
            ["zai-coding-cn"] = 6,
        };

    private static readonly byte[] EmptyPresence =
        "{\"schemaVersion\":\"0.1\",\"implementedAdapterIds\":[],\"capabilityAllowances\":[],\"usableAuthProfiles\":[],\"configuredEnvironmentNames\":[]}"u8.ToArray();

    /// <summary>
    /// 功能：判断 Provider ID 是否属于当前冻结 manifest 的 canonical 身份集合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerId">已通过通用 ASCII 语法验证的候选 ID。</param>
    /// <returns>精确命中 35 个冻结 Provider 身份之一时 true。</returns>
    /// <remarks>不变量：结果与完整冻结模型计数索引同源，不依赖当前 credential 或 executable route presence。</remarks>
    internal static bool IsCanonicalProviderId(string providerId)
    {
        return providerId is not null && TextModelCounts.ContainsKey(providerId);
    }

    /// <summary>
    /// 功能：读取并完整验证程序集内冻结 manifest/catalog，供 executable route spine 在同一证据上建立快照。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>两个互不共享可变存储的完整 canonical JSON 字节数组。</returns>
    /// <remarks>不变量：先完成 digest、严格 JSON、引用闭包和全量 catalog 校验；不读取环境凭据、endpoint override 或网络。</remarks>
    /// <exception cref="ProviderIdentityAdvertisementException">嵌入资源缺失、越界或冻结数据任一不变量漂移。</exception>
    internal static (byte[] Manifest, byte[] Catalog) LoadValidatedFrozenDocuments()
    {
        try
        {
            var manifest = ReadEmbeddedResource(ManifestResource, MaxManifestBytes);
            var catalog = ReadEmbeddedResource(CatalogResource, MaxCatalogBytes);
            _ = BuildFromDocuments(manifest, catalog, EmptyPresence);
            return (manifest, catalog);
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
    /// 功能：从 conformance-only regular file 与程序集内冻结共享数据构建广告快照。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="configurationPath">仅来自固定 presence 环境变量的临时文件路径。</param>
    /// <returns>按 Provider、model、family 三元组排序且不含 faux 的模型描述。</returns>
    /// <remarks>不变量：不读取 credential/canary，不访问网络、DNS、metadata、OAuth 或 endpoint。</remarks>
    /// <exception cref="ProviderIdentityAdvertisementException">文件、JSON、digest、引用或 presence 任一不变量失败。</exception>
    internal static IReadOnlyList<ModelDescriptor> Load(string configurationPath)
    {
        try
        {
            var configuration = ReadBoundedRegularFile(configurationPath, MaxConfigurationBytes);
            var manifest = ReadEmbeddedResource(ManifestResource, MaxManifestBytes);
            var catalog = ReadEmbeddedResource(CatalogResource, MaxCatalogBytes);
            return BuildFromDocuments(manifest, catalog, configuration);
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
    /// 功能：为本程序集测试按与生产完全相同的严格规则构建三个 JSON 文档。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="manifestBytes">有界 manifest UTF-8 JSON。</param>
    /// <param name="catalogBytes">有界 catalog UTF-8 JSON。</param>
    /// <param name="configurationBytes">有界 presence UTF-8 JSON。</param>
    /// <returns>验证后的 identity-only 模型快照。</returns>
    /// <exception cref="ProviderIdentityAdvertisementException">任一严格不变量失败。</exception>
    internal static IReadOnlyList<ModelDescriptor> BuildForTesting(
        byte[] manifestBytes,
        byte[] catalogBytes,
        byte[] configurationBytes)
    {
        try
        {
            if (manifestBytes.Length is <= 0 or > MaxManifestBytes ||
                catalogBytes.Length is <= 0 or > MaxCatalogBytes ||
                configurationBytes.Length is <= 0 or > MaxConfigurationBytes)
            {
                throw new InvalidDataException("provider identity document is outside bounds");
            }

            return BuildFromDocuments(manifestBytes, catalogBytes, configurationBytes);
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
    /// 功能：组合严格 JSON、冻结 digest、引用闭包与 route 交集生成一次快照。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="manifestBytes">代码固定 manifest bytes。</param>
    /// <param name="catalogBytes">代码固定 catalog bytes。</param>
    /// <param name="configurationBytes">runner 临时 presence bytes。</param>
    /// <returns>不含内部 adapter、auth 或 endpoint 字段的公共 descriptors。</returns>
    private static ModelDescriptor[] BuildFromDocuments(
        byte[] manifestBytes,
        byte[] catalogBytes,
        byte[] configurationBytes)
    {
        using var manifestDocument = ParseStrictObject(manifestBytes);
        using var catalogDocument = ParseStrictObject(catalogBytes);
        using var configurationDocument = ParseStrictObject(configurationBytes);
        var manifest = ParseManifest(manifestDocument.RootElement);
        var catalog = ParseCatalog(catalogDocument.RootElement, manifest);
        var presence = ParsePresence(configurationDocument.RootElement, manifest);
        var result = new List<ModelDescriptor>();
        foreach (var routeEntry in manifest.Routes
                     .OrderBy(static item => item.Key.ProviderId, StringComparer.Ordinal)
                     .ThenBy(static item => item.Key.ApiFamily, StringComparer.Ordinal))
        {
            var routeModels = catalog[routeEntry.Key];
            if (!RouteIsUsable(routeEntry.Key, routeEntry.Value, routeModels, presence))
            {
                continue;
            }

            var features = presence.CapabilityAllowances[routeEntry.Key];
            foreach (var model in routeModels)
            {
                var descriptor = ProjectModel(model, features);
                if (descriptor.Capabilities.Input.Count > 0 && descriptor.Capabilities.Output.Count > 0)
                {
                    result.Add(descriptor);
                }
            }
        }

        return result
            .OrderBy(static model => model.ProviderId, StringComparer.Ordinal)
            .ThenBy(static model => model.ModelId, StringComparer.Ordinal)
            .ThenBy(static model => model.ApiFamily, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 功能：有界读取 conformance 临时 regular file，并在读取前后拒绝 reparse point。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">runner 创建且只在本次进程使用的配置路径。</param>
    /// <param name="maximumBytes">正的硬字节上限。</param>
    /// <returns>稳定打开句柄读取到的完整非空 bytes。</returns>
    /// <remarks>不变量：生产模式不会调用本方法；错误不包含路径或文件内容。</remarks>
    /// <exception cref="IOException">路径、类型、长度、竞态增长或读取无效。</exception>
    private static byte[] ReadBoundedRegularFile(string path, int maximumBytes)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4096 || maximumBytes <= 0)
        {
            throw new IOException("provider identity configuration is invalid");
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException("provider identity configuration is not a regular file");
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65_536,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
        {
            throw new IOException("provider identity configuration size is invalid");
        }

        var result = new byte[checked((int)stream.Length)];
        stream.ReadExactly(result);
        if (stream.ReadByte() != -1 ||
            (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException("provider identity configuration changed while reading");
        }

        return result;
    }

    /// <summary>
    /// 功能：从当前 Core 程序集有界读取一个代码固定的 canonical shared snapshot。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="name">代码内固定 manifest resource 名。</param>
    /// <param name="maximumBytes">资源硬字节上限。</param>
    /// <returns>完整非空资源 bytes。</returns>
    /// <exception cref="InvalidDataException">资源缺失、不可定位或长度越界。</exception>
    private static byte[] ReadEmbeddedResource(string name, int maximumBytes)
    {
        var assembly = typeof(ProviderIdentityAdvertisement).Assembly;
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException("provider identity snapshot is unavailable");
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
        {
            throw new InvalidDataException("provider identity snapshot size is invalid");
        }

        var result = new byte[checked((int)stream.Length)];
        stream.ReadExactly(result);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("provider identity snapshot grew while reading");
        }

        return result;
    }

    /// <summary>
    /// 功能：按严格 UTF-8、单值、标准数值与无重复键规则解析 JSON object。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="bytes">已完成有界读取的完整 JSON bytes。</param>
    /// <returns>由调用方释放的独立 JsonDocument。</returns>
    /// <exception cref="InvalidDataException">JSON、深度、重复键或顶层类型无效。</exception>
    private static JsonDocument ParseStrictObject(byte[] bytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaxJsonDepth,
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("provider identity JSON is invalid", exception);
        }

        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("provider identity JSON root is invalid");
            }

            ValidateNoDuplicateKeys(document.RootElement);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 功能：递归拒绝任意 object 内重复的大小写敏感 JSON 属性名。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">严格文档中的任意节点。</param>
    /// <exception cref="InvalidDataException">发现重复键。</exception>
    private static void ValidateNoDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("provider identity JSON contains a duplicate key");
                }

                ValidateNoDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateNoDuplicateKeys(item);
            }
        }
    }

    /// <summary>
    /// 功能：验证冻结 manifest digest，并建立 35 Provider、45 route 的引用闭包。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="root">严格 manifest 根对象。</param>
    /// <returns>route、adapter、auth 与环境身份索引。</returns>
    /// <exception cref="InvalidDataException">结构、digest、计数、身份或引用漂移。</exception>
    private static ManifestIndex ParseManifest(JsonElement root)
    {
        EnsureProperties(
            root,
            [
                "schemaVersion", "manifestDigestAlgorithm", "manifestSha256", "project",
                "author", "reference", "textApiFamilies", "imageApiFamilies", "adapters",
                "modelCatalogs", "headerPolicies", "providers",
            ],
            ["$schema"]);
        RequireConstString(root, "schemaVersion", "0.2");
        RequireConstString(
            root,
            "manifestDigestAlgorithm",
            "sha256-canonical-json-excluding-manifest-digest-v1");
        RequireConstString(root, "manifestSha256", ExpectedManifestSha256);
        RequireConstString(root, "project", "qxnm-forge");
        ValidateAuthor(RequireProperty(root, "author"));
        ValidateReference(RequireProperty(root, "reference"));
        if (!string.Equals(
                ComputeCanonicalSha256(
                    root,
                    new HashSet<string>(StringComparer.Ordinal)
                    {
                        "manifestDigestAlgorithm", "manifestSha256",
                    }),
                ExpectedManifestSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("provider manifest digest drifted");
        }

        var textFamilies = ReadUniqueStringArray(
            RequireProperty(root, "textApiFamilies"),
            9,
            9);
        if (textFamilies.Any(static family => family == "openrouter-images") ||
            !new HashSet<string>(textFamilies, StringComparer.Ordinal).SetEquals(
                ApiFamilies.Where(static family => family != "openrouter-images")))
        {
            throw new InvalidDataException("provider manifest text family set drifted");
        }

        var imageFamilies = ReadUniqueStringArray(
            RequireProperty(root, "imageApiFamilies"),
            1,
            1);
        if (imageFamilies[0] != "openrouter-images")
        {
            throw new InvalidDataException("provider manifest image family set drifted");
        }

        var adapters = ParseAdapters(RequireProperty(root, "adapters"));
        ValidateCatalogReference(RequireProperty(root, "modelCatalogs"));
        var headerPolicies = ParseHeaderPolicies(RequireProperty(root, "headerPolicies"));
        var routes = new Dictionary<RouteKey, ManifestRoute>();
        var providers = new HashSet<string>(StringComparer.Ordinal);
        var environmentNames = new HashSet<string>(StringComparer.Ordinal);
        var authProfiles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var providerItems = RequireArray(root, "providers");
        if (providerItems.GetArrayLength() != 35)
        {
            throw new InvalidDataException("provider manifest Provider count drifted");
        }

        foreach (var provider in providerItems.EnumerateArray())
        {
            ParseProvider(
                provider,
                adapters,
                headerPolicies,
                providers,
                environmentNames,
                authProfiles,
                routes);
        }

        if (routes.Count != 45)
        {
            throw new InvalidDataException("provider manifest route count drifted");
        }

        return new ManifestIndex(routes, providers, adapters.Keys.ToHashSet(StringComparer.Ordinal), environmentNames, authProfiles);
    }

    /// <summary>
    /// 功能：解析十个唯一 adapter 并验证 ID 与 API family。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">manifest.adapters 数组。</param>
    /// <returns>adapterId 到 apiFamily 的索引。</returns>
    private static Dictionary<string, string> ParseAdapters(JsonElement element)
    {
        RequireArray(element, "adapters");
        if (element.GetArrayLength() != 10)
        {
            throw new InvalidDataException("provider manifest adapter count drifted");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var adapter in element.EnumerateArray())
        {
            EnsureProperties(adapter, ["id", "apiFamily"], []);
            var adapterId = RequireString(adapter, "id", 128);
            var family = RequireString(adapter, "apiFamily", 128);
            if (!IsSlug(adapterId) || !ApiFamilies.Contains(family) || !result.TryAdd(adapterId, family))
            {
                throw new InvalidDataException("provider manifest adapter identity is invalid");
            }
        }

        return result;
    }

    /// <summary>
    /// 功能：验证 manifest 只引用固定 PI v1 catalog。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">manifest.modelCatalogs 数组。</param>
    private static void ValidateCatalogReference(JsonElement element)
    {
        RequireArray(element, "modelCatalogs");
        if (element.GetArrayLength() != 1)
        {
            throw new InvalidDataException("provider manifest catalog reference count drifted");
        }

        var item = element[0];
        EnsureProperties(item, ["id", "path", "sourceCommit"], []);
        RequireConstString(item, "id", "pi-v1-frozen");
        RequireConstString(item, "path", "./models.v1.json");
        RequireConstString(item, "sourceCommit", "3f9aa5d10b35223abf6146f960ff5cb5c68053ee");
    }

    /// <summary>
    /// 功能：解析三个固定 header policy 身份并拒绝未知字段或重复 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">manifest.headerPolicies 数组。</param>
    /// <returns>可供 route 引用的 policy ID 集合。</returns>
    private static HashSet<string> ParseHeaderPolicies(JsonElement element)
    {
        RequireArray(element, "headerPolicies");
        if (element.GetArrayLength() != 3)
        {
            throw new InvalidDataException("provider manifest header policy count drifted");
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policy in element.EnumerateArray())
        {
            EnsureProperties(policy, ["id", "credentialPlacement", "fixedHeaders"], []);
            var id = RequireString(policy, "id", 128);
            var placement = RequireString(policy, "credentialPlacement", 128);
            var fixedHeaders = RequireString(policy, "fixedHeaders", 128);
            if (!IsSlug(id) ||
                placement is not ("api-family" or "cloudflare-aig-authorization") ||
                fixedHeaders is not ("none" or "model-catalog") ||
                !result.Add(id))
            {
                throw new InvalidDataException("provider manifest header policy is invalid");
            }
        }

        return result;
    }

    /// <summary>
    /// 功能：解析一个 Provider 的环境、auth profile 与独立 routes。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="provider">manifest Provider 对象。</param>
    /// <param name="adapters">闭合 adapter 索引。</param>
    /// <param name="headerPolicies">闭合 header policy 集合。</param>
    /// <param name="providers">全局 Provider 唯一集合。</param>
    /// <param name="environmentNames">全局环境名称集合。</param>
    /// <param name="authProfiles">按 Provider 保存的 auth profile 集合。</param>
    /// <param name="routes">全局 route 唯一索引。</param>
    private static void ParseProvider(
        JsonElement provider,
        IReadOnlyDictionary<string, string> adapters,
        IReadOnlySet<string> headerPolicies,
        HashSet<string> providers,
        HashSet<string> environmentNames,
        Dictionary<string, HashSet<string>> authProfiles,
        Dictionary<RouteKey, ManifestRoute> routes)
    {
        EnsureProperties(provider, ["id", "environment", "authProfiles", "routes"], []);
        var providerId = RequireString(provider, "id", 128);
        if (!IsSlug(providerId) || !providers.Add(providerId))
        {
            throw new InvalidDataException("provider manifest Provider identity is invalid");
        }

        var providerEnvironment = ParseProviderEnvironment(
            RequireProperty(provider, "environment"),
            environmentNames);
        var profiles = ParseAuthProfiles(
            RequireProperty(provider, "authProfiles"),
            providerEnvironment);
        authProfiles.Add(providerId, profiles);
        var providerRoutes = RequireArray(provider, "routes");
        if (providerRoutes.GetArrayLength() == 0)
        {
            throw new InvalidDataException("provider manifest Provider has no route");
        }

        foreach (var route in providerRoutes.EnumerateArray())
        {
            var parsed = ParseRoute(
                providerId,
                route,
                providerEnvironment,
                profiles,
                adapters,
                headerPolicies);
            if (!routes.TryAdd(new RouteKey(providerId, parsed.ApiFamily), parsed))
            {
                throw new InvalidDataException("provider manifest route identity is duplicated");
            }
        }
    }

    /// <summary>
    /// 功能：解析一个 Provider 内唯一且角色受限的环境源名称。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">Provider.environment 数组。</param>
    /// <param name="globalNames">全局名称并集。</param>
    /// <returns>当前 Provider 可引用的环境名称。</returns>
    private static HashSet<string> ParseProviderEnvironment(
        JsonElement element,
        HashSet<string> globalNames)
    {
        RequireArray(element, "environment");
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            EnsureProperties(item, ["name", "role"], []);
            var name = RequireString(item, "name", 128);
            var role = RequireString(item, "role", 128);
            if (!IsEnvironmentName(name) ||
                role is not ("secret" or "configuration" or "credential-file" or
                    "credential-selector" or "credential-endpoint") ||
                !result.Add(name))
            {
                throw new InvalidDataException("provider manifest environment source is invalid");
            }

            globalNames.Add(name);
        }

        return result;
    }

    /// <summary>
    /// 功能：解析 Provider 内唯一 auth profiles 并验证环境引用闭包。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">Provider.authProfiles 数组。</param>
    /// <param name="providerEnvironment">当前 Provider 环境名称集合。</param>
    /// <returns>可供 routes OR 引用的 profile ID 集合。</returns>
    private static HashSet<string> ParseAuthProfiles(
        JsonElement element,
        HashSet<string> providerEnvironment)
    {
        RequireArray(element, "authProfiles");
        if (element.GetArrayLength() == 0)
        {
            throw new InvalidDataException("provider manifest auth profile list is empty");
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in element.EnumerateArray())
        {
            EnsureProperties(profile, ["id", "kind", "environment"], []);
            var id = RequireString(profile, "id", 128);
            var kind = RequireString(profile, "kind", 128);
            var environment = ReadUniqueStringArray(
                RequireProperty(profile, "environment"),
                0,
                128);
            if (!IsSlug(id) ||
                kind is not ("api-key" or "oauth" or "aws-credential-chain" or
                    "google-application-default" or "cloudflare-token") ||
                environment.Any(name => !providerEnvironment.Contains(name)) ||
                !result.Add(id))
            {
                throw new InvalidDataException("provider manifest auth profile is invalid");
            }
        }

        return result;
    }

    /// <summary>
    /// 功能：解析 route 的 family、adapter、认证与 endpoint presence 约束。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerId">所属 canonical Provider。</param>
    /// <param name="route">manifest route 对象。</param>
    /// <param name="providerEnvironment">当前 Provider 环境集合。</param>
    /// <param name="profiles">当前 Provider auth profile 集合。</param>
    /// <param name="adapters">adapter/family 索引。</param>
    /// <param name="headerPolicies">header policy 集合。</param>
    /// <returns>仅含 presence 判定所需身份的 route。</returns>
    private static ManifestRoute ParseRoute(
        string providerId,
        JsonElement route,
        HashSet<string> providerEnvironment,
        HashSet<string> profiles,
        IReadOnlyDictionary<string, string> adapters,
        IReadOnlySet<string> headerPolicies)
    {
        EnsureProperties(
            route,
            [
                "media", "apiFamily", "adapterId", "modelCatalogId", "endpoint",
                "authProfileIds", "headerPolicyId", "quirks",
            ],
            []);
        var media = RequireString(route, "media", 16);
        var family = RequireString(route, "apiFamily", 128);
        var adapterId = RequireString(route, "adapterId", 128);
        RequireConstString(route, "modelCatalogId", "pi-v1-frozen");
        var headerPolicy = RequireString(route, "headerPolicyId", 128);
        var routeProfiles = ReadUniqueStringArray(
            RequireProperty(route, "authProfileIds"),
            1,
            128);
        var routeQuirks = ReadUniqueStringArray(
            RequireProperty(route, "quirks"),
            0,
            Quirks.Count);
        if (media is not ("text" or "image") ||
            !ApiFamilies.Contains(family) ||
            (media == "image") != (family == "openrouter-images") ||
            !adapters.TryGetValue(adapterId, out var adapterFamily) ||
            adapterFamily != family ||
            !headerPolicies.Contains(headerPolicy) ||
            routeProfiles.Any(profile => !profiles.Contains(profile)) ||
            routeQuirks.Any(quirk => !Quirks.Contains(quirk)))
        {
            throw new InvalidDataException("provider manifest route is invalid");
        }

        var endpoint = ParseEndpointPolicy(
            RequireProperty(route, "endpoint"),
            providerEnvironment);
        return new ManifestRoute(
            providerId,
            media,
            family,
            adapterId,
            routeProfiles,
            endpoint.TemplateBindings,
            endpoint.RuntimeEnvironmentNames);
    }

    /// <summary>
    /// 功能：解析 route endpoint 的每个 AND template binding 与 runtime OR 名称。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">route.endpoint 对象。</param>
    /// <param name="providerEnvironment">当前 Provider 可引用环境集合。</param>
    /// <returns>template binding 集合和 runtime endpoint 名称。</returns>
    private static EndpointPresence ParseEndpointPolicy(
        JsonElement element,
        HashSet<string> providerEnvironment)
    {
        EnsureProperties(
            element,
            ["source", "templateBindings", "runtimeEndpointEnv", "runtimeOverride"],
            []);
        RequireConstString(element, "source", "model-catalog");
        RequireConstString(element, "runtimeOverride", "explicit-configuration-only");
        var runtimeNames = ReadUniqueStringArray(
            RequireProperty(element, "runtimeEndpointEnv"),
            0,
            128);
        if (runtimeNames.Any(name => !IsEnvironmentName(name) || !providerEnvironment.Contains(name)))
        {
            throw new InvalidDataException("provider manifest runtime endpoint source is invalid");
        }

        var bindingsElement = RequireArray(element, "templateBindings");
        var bindings = new List<HashSet<string>>();
        var variables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindingsElement.EnumerateArray())
        {
            EnsureProperties(binding, ["variable", "environment", "valuePattern"], []);
            var variable = RequireString(binding, "variable", 128);
            var pattern = RequireString(binding, "valuePattern", 128);
            var names = ReadUniqueStringArray(
                RequireProperty(binding, "environment"),
                1,
                128);
            var validPair = (variable, pattern) is
                ("CLOUDFLARE_ACCOUNT_ID", "cloudflare-identifier-v1") or
                ("CLOUDFLARE_GATEWAY_ID", "cloudflare-identifier-v1") or
                ("location", "google-location-v1");
            if (!validPair ||
                !variables.Add(variable) ||
                names.Any(name => !IsEnvironmentName(name) || !providerEnvironment.Contains(name)))
            {
                throw new InvalidDataException("provider manifest endpoint template binding is invalid");
            }

            bindings.Add(new HashSet<string>(names, StringComparer.Ordinal));
        }

        return new EndpointPresence(
            bindings,
            new HashSet<string>(runtimeNames, StringComparer.Ordinal));
    }

    /// <summary>
    /// 功能：验证 catalog metadata/digest，并按全部 manifest routes 索引 1,076 条模型。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="root">严格 catalog 根对象。</param>
    /// <param name="manifest">已验证 manifest 索引。</param>
    /// <returns>每条 route 下按 modelId 排序的模型行。</returns>
    private static Dictionary<RouteKey, List<CatalogModel>> ParseCatalog(
        JsonElement root,
        ManifestIndex manifest)
    {
        EnsureProperties(
            root,
            ["schemaVersion", "catalogId", "project", "author", "source", "models"],
            ["$schema"]);
        RequireConstString(root, "schemaVersion", "1.0");
        RequireConstString(root, "catalogId", "pi-v1-frozen");
        RequireConstString(root, "project", "qxnm-forge");
        ValidateAuthor(RequireProperty(root, "author"));
        ValidateCatalogSource(RequireProperty(root, "source"), manifest.ProviderIds);
        var models = RequireArray(root, "models");
        if (models.GetArrayLength() != 1076 ||
            !string.Equals(
                ComputeCanonicalSha256(models, null),
                ExpectedCatalogRecordsSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("provider catalog records drifted");
        }

        var grouped = new Dictionary<RouteKey, List<CatalogModel>>();
        var identities = new HashSet<ModelKey>();
        foreach (var element in models.EnumerateArray())
        {
            var model = ParseCatalogModel(element);
            var routeKey = new RouteKey(model.ProviderId, model.ApiFamily);
            var modelKey = new ModelKey(model.ProviderId, model.ModelId, model.ApiFamily);
            if (!manifest.Routes.ContainsKey(routeKey) || !identities.Add(modelKey))
            {
                throw new InvalidDataException("provider catalog model identity is invalid");
            }

            if (!grouped.TryGetValue(routeKey, out var routeModels))
            {
                routeModels = [];
                grouped.Add(routeKey, routeModels);
            }

            routeModels.Add(model);
        }

        if (!new HashSet<RouteKey>(grouped.Keys).SetEquals(manifest.Routes.Keys))
        {
            throw new InvalidDataException("provider catalog does not cover every route");
        }

        foreach (var routeModels in grouped.Values)
        {
            routeModels.Sort(static (left, right) =>
                StringComparer.Ordinal.Compare(left.ModelId, right.ModelId));
        }

        return grouped;
    }

    /// <summary>
    /// 功能：严格验证 catalog source 固定 provenance、计数与 Provider count map。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="source">catalog.source 对象。</param>
    /// <param name="providerIds">manifest 的 35 Provider 身份。</param>
    private static void ValidateCatalogSource(JsonElement source, IReadOnlySet<string> providerIds)
    {
        EnsureProperties(
            source,
            [
                "project", "commit", "license", "textEntrypoint", "imageEntrypoint",
                "extraction", "sourceDigestAlgorithm", "textEntrypointSha256",
                "textProviderSourcesSha256", "imageEntrypointSha256", "recordsDigestAlgorithm",
                "recordsSha256", "observedCounts", "textModelsByProvider", "imageModelsByProvider",
            ],
            []);
        RequireConstString(source, "project", "PI");
        RequireConstString(source, "commit", "3f9aa5d10b35223abf6146f960ff5cb5c68053ee");
        RequireConstString(source, "license", "MIT");
        RequireConstString(source, "textEntrypoint", "packages/ai/src/models.generated.ts");
        RequireConstString(source, "imageEntrypoint", "packages/ai/src/image-models.generated.ts");
        RequireConstString(source, "extraction", "native-esm-object-enumeration-v1");
        RequireConstString(source, "sourceDigestAlgorithm", "sha256-path-nul-bytes-nul-v1");
        RequireConstString(
            source,
            "textEntrypointSha256",
            "ca7059ec42b51e1ca9aacc92a2be24e552c5025a849a62f8b7f2327d80dea46b");
        RequireConstString(
            source,
            "textProviderSourcesSha256",
            "c4cd2b4fb05478c737c5d1dfb77a9b693593388e052182cb79d81d414ac5ec35");
        RequireConstString(
            source,
            "imageEntrypointSha256",
            "255db32f84f1a94f579f061506ced014ccf33d0b719b4a855eace464cb395a00");
        RequireConstString(source, "recordsDigestAlgorithm", "sha256-canonical-json-v1");
        RequireConstString(source, "recordsSha256", ExpectedCatalogRecordsSha256);
        var observed = RequireProperty(source, "observedCounts");
        EnsureProperties(observed, ["providers", "textModels", "imageProviders", "imageModels"], []);
        if (RequireInt32(observed, "providers", 1, 10_000) != 35 ||
            RequireInt32(observed, "textModels", 1, 10_000) != 1041 ||
            RequireInt32(observed, "imageProviders", 1, 10_000) != 1 ||
            RequireInt32(observed, "imageModels", 1, 10_000) != 35)
        {
            throw new InvalidDataException("provider catalog observed count drifted");
        }

        var textCounts = RequireProperty(source, "textModelsByProvider");
        EnsureProperties(textCounts, providerIds, []);
        var textTotal = 0;
        foreach (var providerId in providerIds)
        {
            var observedCount = RequireInt32(textCounts, providerId, 1, 10_000);
            if (!TextModelCounts.TryGetValue(providerId, out var expectedCount) ||
                observedCount != expectedCount)
            {
                throw new InvalidDataException("provider catalog Provider count map drifted");
            }

            textTotal += observedCount;
        }

        var imageCounts = RequireProperty(source, "imageModelsByProvider");
        EnsureProperties(imageCounts, ["openrouter"], []);
        if (textTotal != 1041 || RequireInt32(imageCounts, "openrouter", 1, 10_000) != 35)
        {
            throw new InvalidDataException("provider catalog Provider count map drifted");
        }
    }

    /// <summary>
    /// 功能：严格解析一条 text 或 image catalog row 的广告所需字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">catalog.models 数组项。</param>
    /// <returns>不含 endpoint URL、header 或 compatibility 值的内部模型。</returns>
    private static CatalogModel ParseCatalogModel(JsonElement element)
    {
        EnsureProperties(
            element,
            ["media", "providerId", "modelId", "name", "apiFamily", "endpoint", "capabilities"],
            ["limits", "thinkingLevelMap", "fixedHeaders", "compatibility"]);
        var media = RequireString(element, "media", 16);
        var providerId = RequireString(element, "providerId", 128);
        var modelId = RequireString(element, "modelId", 512);
        var name = RequireString(element, "name", 512);
        var family = RequireString(element, "apiFamily", 128);
        if (media is not ("text" or "image") ||
            !IsSlug(providerId) ||
            !ApiFamilies.Contains(family) ||
            (media == "image") != (family == "openrouter-images"))
        {
            throw new InvalidDataException("provider catalog model core identity is invalid");
        }

        var endpointStrategy = ValidateCatalogEndpoint(RequireProperty(element, "endpoint"));
        var capabilities = RequireProperty(element, "capabilities");
        IReadOnlyList<string> input;
        IReadOnlyList<string> output;
        bool reasoning;
        int? contextTokens = null;
        int? maxOutputTokens = null;
        if (media == "text")
        {
            EnsureProperties(capabilities, ["input", "reasoning"], []);
            input = ReadMediaArray(RequireProperty(capabilities, "input"));
            output = ["text"];
            reasoning = RequireBoolean(capabilities, "reasoning");
            var limits = RequireProperty(element, "limits");
            EnsureProperties(limits, ["contextWindow", "maxOutputTokens"], []);
            contextTokens = RequireInt32(limits, "contextWindow", 1, int.MaxValue);
            maxOutputTokens = RequireInt32(limits, "maxOutputTokens", 1, int.MaxValue);
        }
        else
        {
            EnsureProperties(capabilities, ["input", "output"], []);
            input = ReadMediaArray(RequireProperty(capabilities, "input"));
            output = ReadMediaArray(RequireProperty(capabilities, "output"));
            reasoning = false;
            if (element.TryGetProperty("limits", out _) ||
                element.TryGetProperty("thinkingLevelMap", out _) ||
                element.TryGetProperty("compatibility", out _))
            {
                throw new InvalidDataException("provider image catalog row has forbidden fields");
            }
        }

        ValidateOptionalCatalogFields(element);
        return new CatalogModel(
            media,
            providerId,
            modelId,
            name,
            family,
            endpointStrategy,
            input,
            output,
            reasoning,
            contextTokens,
            maxOutputTokens);
    }

    /// <summary>
    /// 功能：验证 catalog endpoint 三种封闭 shape 并返回策略名。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="endpoint">model.endpoint 对象。</param>
    /// <returns>fixed、template 或 runtime-required。</returns>
    private static string ValidateCatalogEndpoint(JsonElement endpoint)
    {
        RequireObject(endpoint, "model endpoint");
        var strategy = RequireString(endpoint, "strategy", 64);
        switch (strategy)
        {
            case "fixed":
                EnsureProperties(endpoint, ["strategy", "baseUrl"], []);
                _ = RequireString(endpoint, "baseUrl", 2048);
                break;
            case "template":
                EnsureProperties(endpoint, ["strategy", "baseUrl", "templateVariables"], []);
                _ = RequireString(endpoint, "baseUrl", 2048);
                var variables = ReadUniqueStringArray(
                    RequireProperty(endpoint, "templateVariables"),
                    1,
                    3);
                if (variables.Any(static variable => variable is not
                        ("CLOUDFLARE_ACCOUNT_ID" or "CLOUDFLARE_GATEWAY_ID" or "location")))
                {
                    throw new InvalidDataException("provider catalog template variable is invalid");
                }

                break;
            case "runtime-required":
                EnsureProperties(endpoint, ["strategy"], []);
                break;
            default:
                throw new InvalidDataException("provider catalog endpoint strategy is invalid");
        }

        return strategy;
    }

    /// <summary>
    /// 功能：验证不参与广告投影的 catalog 可选字段仍是封闭、有限 shape。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="model">catalog model 对象。</param>
    private static void ValidateOptionalCatalogFields(JsonElement model)
    {
        if (model.TryGetProperty("thinkingLevelMap", out var thinking))
        {
            RequireObject(thinking, "thinkingLevelMap");
            EnsureProperties(thinking, [], ThinkingLevelFields);
            if (thinking.GetPropertyCount() == 0)
            {
                throw new InvalidDataException("provider catalog thinking map is empty");
            }

            foreach (var property in thinking.EnumerateObject())
            {
                if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    throw new InvalidDataException("provider catalog thinking map value is invalid");
                }
            }
        }

        if (model.TryGetProperty("fixedHeaders", out var headers))
        {
            RequireArray(headers, "fixedHeaders");
            if (headers.GetArrayLength() == 0)
            {
                throw new InvalidDataException("provider catalog fixed header list is empty");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var header in headers.EnumerateArray())
            {
                EnsureProperties(header, ["name", "value"], []);
                var name = RequireString(header, "name", 128);
                _ = RequireString(header, "value", 512);
                if (name is not ("Copilot-Integration-Id" or "Editor-Plugin-Version" or
                        "Editor-Version" or "NVCF-POLL-SECONDS" or "User-Agent") ||
                    !seen.Add(header.GetRawText()))
                {
                    throw new InvalidDataException("provider catalog fixed header is invalid");
                }
            }
        }

        if (model.TryGetProperty("compatibility", out var compatibility))
        {
            RequireObject(compatibility, "compatibility");
            EnsureProperties(compatibility, [], CompatibilityFields);
            if (compatibility.GetPropertyCount() == 0)
            {
                throw new InvalidDataException("provider catalog compatibility is empty");
            }

            foreach (var property in compatibility.EnumerateObject())
            {
                if (property.Value.ValueKind is not
                    (JsonValueKind.String or JsonValueKind.True or JsonValueKind.False))
                {
                    throw new InvalidDataException("provider catalog compatibility value is invalid");
                }
            }
        }
    }

    /// <summary>
    /// 功能：解析无值 presence 配置并验证全部 manifest 引用和唯一性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="root">严格 presence 根对象。</param>
    /// <param name="manifest">冻结 manifest 索引。</param>
    /// <returns>只含不可变存在性集合的快照。</returns>
    private static PresenceSnapshot ParsePresence(JsonElement root, ManifestIndex manifest)
    {
        EnsureProperties(
            root,
            [
                "schemaVersion", "implementedAdapterIds", "capabilityAllowances",
                "usableAuthProfiles", "configuredEnvironmentNames",
            ],
            []);
        RequireConstString(root, "schemaVersion", "0.1");
        var adapters = ReadUniqueStringArray(
            RequireProperty(root, "implementedAdapterIds"),
            0,
            128);
        var configuredNames = ReadUniqueStringArray(
            RequireProperty(root, "configuredEnvironmentNames"),
            0,
            512);
        if (adapters.Any(id => !IsSlug(id) || !manifest.AdapterIds.Contains(id)) ||
            configuredNames.Any(name =>
                !IsEnvironmentName(name) || !manifest.EnvironmentNames.Contains(name)))
        {
            throw new InvalidDataException("provider identity presence identity is invalid");
        }

        var allowances = new Dictionary<RouteKey, HashSet<string>>();
        var allowanceItems = RequireArray(root, "capabilityAllowances");
        if (allowanceItems.GetArrayLength() > 128)
        {
            throw new InvalidDataException("provider identity presence allowance count is invalid");
        }

        foreach (var allowance in allowanceItems.EnumerateArray())
        {
            EnsureProperties(allowance, ["providerId", "apiFamily", "features"], []);
            var key = new RouteKey(
                RequireString(allowance, "providerId", 128),
                RequireString(allowance, "apiFamily", 128));
            var features = ReadUniqueStringArray(
                RequireProperty(allowance, "features"),
                0,
                CapabilityFeatures.Count);
            if (!IsProviderId(key.ProviderId) ||
                !IsSlug(key.ApiFamily) ||
                !manifest.Routes.ContainsKey(key) ||
                features.Any(feature => !CapabilityFeatures.Contains(feature)) ||
                !allowances.TryAdd(key, new HashSet<string>(features, StringComparer.Ordinal)))
            {
                throw new InvalidDataException("provider identity capability allowance is invalid");
            }
        }

        var usableAuth = new HashSet<AuthKey>();
        var authItems = RequireArray(root, "usableAuthProfiles");
        if (authItems.GetArrayLength() > 512)
        {
            throw new InvalidDataException("provider identity auth presence count is invalid");
        }

        foreach (var auth in authItems.EnumerateArray())
        {
            EnsureProperties(auth, ["providerId", "authProfileId"], []);
            var key = new AuthKey(
                RequireString(auth, "providerId", 128),
                RequireString(auth, "authProfileId", 128));
            if (!IsProviderId(key.ProviderId) ||
                !IsSlug(key.AuthProfileId) ||
                !manifest.AuthProfileIds.TryGetValue(key.ProviderId, out var profiles) ||
                !profiles.Contains(key.AuthProfileId) ||
                !usableAuth.Add(key))
            {
                throw new InvalidDataException("provider identity auth presence is invalid");
            }
        }

        return new PresenceSnapshot(
            new HashSet<string>(adapters, StringComparer.Ordinal),
            allowances,
            usableAuth,
            new HashSet<string>(configuredNames, StringComparer.Ordinal));
    }

    /// <summary>
    /// 功能：计算一条 route 的 manifest/auth/endpoint/adapter/capability 五项交集。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="key">Provider/family route 身份。</param>
    /// <param name="route">manifest route presence 约束。</param>
    /// <param name="models">该 route 的全部 catalog 行。</param>
    /// <param name="presence">已验证 presence 集合。</param>
    /// <returns>整条 route 可进入广告时为 true。</returns>
    private static bool RouteIsUsable(
        RouteKey key,
        ManifestRoute route,
        IReadOnlyList<CatalogModel> models,
        PresenceSnapshot presence)
    {
        if (!presence.ImplementedAdapterIds.Contains(route.AdapterId) ||
            !presence.CapabilityAllowances.TryGetValue(key, out var features) ||
            !features.Contains("authentication"))
        {
            return false;
        }

        if ((route.Media == "text" && !features.Contains("text")) ||
            (route.Media == "image" &&
             (!features.Contains("image_output") ||
              !(features.Contains("text") || features.Contains("image_input")))))
        {
            return false;
        }

        if (!route.AuthProfileIds.Any(profile =>
                presence.UsableAuthProfiles.Contains(new AuthKey(key.ProviderId, profile))))
        {
            return false;
        }

        if (route.TemplateBindings.Any(binding =>
                !binding.Overlaps(presence.ConfiguredEnvironmentNames)) ||
            (route.RuntimeEnvironmentNames.Count > 0 &&
             !route.RuntimeEnvironmentNames.Overlaps(presence.ConfiguredEnvironmentNames)))
        {
            return false;
        }

        return !models.Any(static model => model.EndpointStrategy == "runtime-required") ||
            (route.RuntimeEnvironmentNames.Count > 0 &&
             route.RuntimeEnvironmentNames.Overlaps(presence.ConfiguredEnvironmentNames));
    }

    /// <summary>
    /// 功能：将 catalog 行投影为 capability presence 只缩不增的公共模型描述。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="model">canonical catalog 模型。</param>
    /// <param name="features">该 route 获准公开的 features。</param>
    /// <returns>不含任何 endpoint、auth、adapter 或 compatibility 字段的 descriptor。</returns>
    private static ModelDescriptor ProjectModel(
        CatalogModel model,
        HashSet<string> features)
    {
        var input = model.Input
            .Where(media =>
                (media == "text" && features.Contains("text")) ||
                (media == "image" && features.Contains("image_input")))
            .ToArray();
        string[] output;
        bool streaming;
        bool tools;
        bool reasoning;
        if (model.Media == "text")
        {
            output = ["text"];
            streaming = features.Contains("streaming");
            tools = features.Contains("tools");
            reasoning = model.Reasoning && features.Contains("reasoning");
        }
        else
        {
            output = model.Output
                .Where(media =>
                    (media == "text" && features.Contains("text")) ||
                    (media == "image" && features.Contains("image_output")))
                .ToArray();
            streaming = false;
            tools = false;
            reasoning = false;
        }

        return new ModelDescriptor(
            model.ProviderId,
            model.ModelId,
            model.Name,
            model.ApiFamily,
            new ModelCapabilities(
                input,
                output,
                streaming,
                tools,
                reasoning,
                model.ContextTokens,
                model.MaxOutputTokens));
    }

    /// <summary>
    /// 功能：验证固定 author object，阻止 provenance 任意扩展。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="author">manifest/catalog author 对象。</param>
    private static void ValidateAuthor(JsonElement author)
    {
        EnsureProperties(author, ["name", "email"], []);
        RequireConstString(author, "name", "高宏顺");
        RequireConstString(author, "email", "18272669457@163.com");
    }

    /// <summary>
    /// 功能：验证固定 PI reference object，阻止 commit/license 漂移。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="reference">manifest.reference 对象。</param>
    private static void ValidateReference(JsonElement reference)
    {
        EnsureProperties(reference, ["project", "commit", "license"], []);
        RequireConstString(reference, "project", "PI");
        RequireConstString(reference, "commit", "3f9aa5d10b35223abf6146f960ff5cb5c68053ee");
        RequireConstString(reference, "license", "MIT");
    }

    /// <summary>
    /// 功能：计算键序、紧凑分隔符与 UTF-8 字符固定的 canonical JSON SHA-256。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">标准 JSON 子树。</param>
    /// <param name="excludedRootProperties">仅根层需要排除的字段；null 表示不排除。</param>
    /// <returns>小写十六进制 SHA-256。</returns>
    private static string ComputeCanonicalSha256(
        JsonElement element,
        IReadOnlySet<string>? excludedRootProperties)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
            SkipValidation = false,
        }))
        {
            WriteCanonicalJson(writer, element, excludedRootProperties, isRoot: true);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>
    /// 功能：递归写出 object 键排序、数组保序的 canonical JSON 值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="writer">目标 UTF-8 JSON writer。</param>
    /// <param name="element">当前 JSON 节点。</param>
    /// <param name="excludedRootProperties">仅根对象排除字段。</param>
    /// <param name="isRoot">当前节点是否为根。</param>
    private static void WriteCanonicalJson(
        Utf8JsonWriter writer,
        JsonElement element,
        IReadOnlySet<string>? excludedRootProperties,
        bool isRoot)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .Where(property =>
                                 !isRoot || excludedRootProperties is null ||
                                 !excludedRootProperties.Contains(property.Name))
                             .OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value, excludedRootProperties, isRoot: false);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item, excludedRootProperties, isRoot: false);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException("provider identity JSON value is invalid");
        }
    }

    /// <summary>
    /// 功能：要求 object 精确包含必需字段且只允许显式可选字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">待验证 object。</param>
    /// <param name="required">必须恰好出现一次的字段。</param>
    /// <param name="optional">可出现的额外字段。</param>
    private static void EnsureProperties(
        JsonElement element,
        IEnumerable<string> required,
        IEnumerable<string> optional)
    {
        RequireObject(element, "provider identity object");
        var requiredSet = new HashSet<string>(required, StringComparer.Ordinal);
        var allowed = new HashSet<string>(requiredSet, StringComparer.Ordinal);
        allowed.UnionWith(optional);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new InvalidDataException("provider identity object contains an unknown field");
            }

            requiredSet.Remove(property.Name);
        }

        if (requiredSet.Count != 0)
        {
            throw new InvalidDataException("provider identity object is missing a required field");
        }
    }

    /// <summary>
    /// 功能：要求动态 JSON 节点为 object。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">待检查节点。</param>
    /// <param name="context">仅代码固定的安全上下文。</param>
    private static void RequireObject(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(context + " must be an object");
        }
    }

    /// <summary>
    /// 功能：取得 object 的必需属性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父 object。</param>
    /// <param name="name">代码固定字段名。</param>
    /// <returns>属性节点。</returns>
    private static JsonElement RequireProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            throw new InvalidDataException("provider identity required field is missing");
        }

        return value;
    }

    /// <summary>
    /// 功能：要求 object 属性为 JSON array 并返回节点。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父 object 或直接 array 节点。</param>
    /// <param name="name">属性名；element 已是 array 时仅作安全上下文。</param>
    /// <returns>array 节点。</returns>
    private static JsonElement RequireArray(JsonElement element, string name)
    {
        var value = element.ValueKind == JsonValueKind.Array
            ? element
            : RequireProperty(element, name);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("provider identity field must be an array");
        }

        return value;
    }

    /// <summary>
    /// 功能：读取有长度上限的非空字符串属性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父 object。</param>
    /// <param name="name">代码固定属性名。</param>
    /// <param name="maximumLength">正的字符上限。</param>
    /// <returns>有效字符串。</returns>
    private static string RequireString(JsonElement element, string name, int maximumLength)
    {
        var value = RequireProperty(element, name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("provider identity field must be a string");
        }

        var result = value.GetString()!;
        if (result.Length is 0 || result.Length > maximumLength)
        {
            throw new InvalidDataException("provider identity string length is invalid");
        }

        return result;
    }

    /// <summary>
    /// 功能：要求字符串属性与一个代码固定规范常量精确相等。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父 object。</param>
    /// <param name="name">代码固定属性名。</param>
    /// <param name="expected">代码固定预期值。</param>
    private static void RequireConstString(JsonElement element, string name, string expected)
    {
        var value = RequireString(element, name, Math.Max(expected.Length, 1));
        if (!string.Equals(value, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException("provider identity constant drifted");
        }
    }

    /// <summary>
    /// 功能：读取必需 boolean 属性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父 object。</param>
    /// <param name="name">代码固定字段名。</param>
    /// <returns>JSON boolean 值。</returns>
    private static bool RequireBoolean(JsonElement element, string name)
    {
        var value = RequireProperty(element, name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException("provider identity field must be a boolean");
        }

        return value.GetBoolean();
    }

    /// <summary>
    /// 功能：读取范围内必需 Int32 属性并拒绝浮点或溢出。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父 object。</param>
    /// <param name="name">代码固定字段名。</param>
    /// <param name="minimum">允许最小值。</param>
    /// <param name="maximum">允许最大值。</param>
    /// <returns>范围内整数。</returns>
    private static int RequireInt32(
        JsonElement element,
        string name,
        int minimum,
        int maximum)
    {
        var value = RequireProperty(element, name);
        if (!value.TryGetInt32(out var result) || result < minimum || result > maximum)
        {
            throw new InvalidDataException("provider identity integer is invalid");
        }

        return result;
    }

    /// <summary>
    /// 功能：读取有界、无重复、只含非空字符串的 JSON array。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">直接 array 节点。</param>
    /// <param name="minimumCount">最少项数。</param>
    /// <param name="maximumCount">最多项数。</param>
    /// <returns>保持源顺序的字符串数组。</returns>
    private static List<string> ReadUniqueStringArray(
        JsonElement element,
        int minimumCount,
        int maximumCount)
    {
        RequireArray(element, "string array");
        if (element.GetArrayLength() < minimumCount || element.GetArrayLength() > maximumCount)
        {
            throw new InvalidDataException("provider identity string array length is invalid");
        }

        var result = new List<string>(element.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(item.GetString()) ||
                !seen.Add(item.GetString()!))
            {
                throw new InvalidDataException("provider identity string array is invalid");
            }

            result.Add(item.GetString()!);
        }

        return result;
    }

    /// <summary>
    /// 功能：读取只含 text/image 且保留 catalog 顺序的非空媒介数组。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">catalog capabilities 媒介数组。</param>
    /// <returns>唯一媒介列表。</returns>
    private static List<string> ReadMediaArray(JsonElement element)
    {
        var media = ReadUniqueStringArray(element, 1, 2);
        if (media.Any(static item => item is not ("text" or "image")))
        {
            throw new InvalidDataException("provider catalog media capability is invalid");
        }

        return media;
    }

    /// <summary>
    /// 功能：判断字符串是否符合 manifest slug 的小写 ASCII 语法。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选身份。</param>
    /// <returns>长度 1..128、首字符字母数字且其余仅字母数字或连字符时为 true。</returns>
    private static bool IsSlug(string value)
    {
        return value.Length is >= 1 and <= 128 &&
            IsLowerAsciiLetterOrDigit(value[0]) &&
            value.All(static character => IsLowerAsciiLetterOrDigit(character) || character == '-');
    }

    /// <summary>
    /// 功能：判断 presence Provider ID 是否符合允许点号的品牌中立 ASCII 语法。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选 Provider ID。</param>
    /// <returns>长度及字符集均有效时为 true。</returns>
    private static bool IsProviderId(string value)
    {
        return value.Length is >= 1 and <= 128 &&
            IsLowerAsciiLetterOrDigit(value[0]) &&
            value.All(static character =>
                IsLowerAsciiLetterOrDigit(character) || character is '-' or '.');
    }

    /// <summary>
    /// 功能：判断字符串是否符合 manifest 环境源名称语法。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选环境名称。</param>
    /// <returns>2..128 位、大写字母开头且其余为大写字母数字或下划线时为 true。</returns>
    private static bool IsEnvironmentName(string value)
    {
        return value.Length is >= 2 and <= 128 &&
            value[0] is >= 'A' and <= 'Z' &&
            value.All(static character =>
                character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
    }

    /// <summary>
    /// 功能：判断字符是否为小写 ASCII 字母或数字。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选字符。</param>
    /// <returns>属于允许集合时为 true。</returns>
    private static bool IsLowerAsciiLetterOrDigit(char value)
    {
        return value is >= 'a' and <= 'z' or >= '0' and <= '9';
    }

    private readonly record struct RouteKey(string ProviderId, string ApiFamily);

    private readonly record struct ModelKey(string ProviderId, string ModelId, string ApiFamily);

    private readonly record struct AuthKey(string ProviderId, string AuthProfileId);

    /// <summary>
    /// 功能：保存冻结 manifest 的 Provider、route、adapter、环境与认证引用索引。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed record ManifestIndex(
        Dictionary<RouteKey, ManifestRoute> Routes,
        HashSet<string> ProviderIds,
        HashSet<string> AdapterIds,
        HashSet<string> EnvironmentNames,
        Dictionary<string, HashSet<string>> AuthProfileIds);

    /// <summary>
    /// 功能：保存单条 manifest route 进行 presence 交集所需的封闭字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed record ManifestRoute(
        string ProviderId,
        string Media,
        string ApiFamily,
        string AdapterId,
        IReadOnlyList<string> AuthProfileIds,
        IReadOnlyList<HashSet<string>> TemplateBindings,
        HashSet<string> RuntimeEnvironmentNames);

    /// <summary>
    /// 功能：保存 endpoint 每个 template binding 与 runtime endpoint 的存在性名称。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed record EndpointPresence(
        IReadOnlyList<HashSet<string>> TemplateBindings,
        HashSet<string> RuntimeEnvironmentNames);

    /// <summary>
    /// 功能：保存 catalog 行投影广告所需且不含 URL/header/compatibility 的字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed record CatalogModel(
        string Media,
        string ProviderId,
        string ModelId,
        string Name,
        string ApiFamily,
        string EndpointStrategy,
        IReadOnlyList<string> Input,
        IReadOnlyList<string> Output,
        bool Reasoning,
        int? ContextTokens,
        int? MaxOutputTokens);

    /// <summary>
    /// 功能：保存已严格验证且不含任何 credential/endpoint 值的 presence 集合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed record PresenceSnapshot(
        HashSet<string> ImplementedAdapterIds,
        Dictionary<RouteKey, HashSet<string>> CapabilityAllowances,
        HashSet<AuthKey> UsableAuthProfiles,
        HashSet<string> ConfiguredEnvironmentNames);
}
