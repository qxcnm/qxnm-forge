using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text.Json;
using QxnmForge.Domain;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：表示 canonical executable Provider route 配置或冻结证据被统一脱敏拒绝。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ProviderExecutableRouteException : Exception
{
    /// <summary>
    /// 功能：创建不包含路径、endpoint、环境名称、credential 或 JSON 原文的稳定启动异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public ProviderExecutableRouteException()
        : base("Provider executable route configuration is invalid.")
    {
    }
}

/// <summary>
/// 功能：保存冻结 manifest/catalog 严格交集得到的一条不可变 executable route plan。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ProviderId">canonical Provider ID。</param>
/// <param name="ApiFamily">canonical API family。</param>
/// <param name="AdapterId">manifest 固定 adapter ID。</param>
/// <param name="CredentialEnvironmentName">唯一 API-key 环境源名称，不包含值。</param>
/// <param name="EndpointBase">catalog 固定 HTTPS base 或 conformance literal loopback override。</param>
/// <param name="Models">同一 route 的 catalog allowlist 与能力投影。</param>
internal sealed record ProviderExecutableRoutePlan(
    string ProviderId,
    string ApiFamily,
    string AdapterId,
    string CredentialEnvironmentName,
    Uri EndpointBase,
    IReadOnlyList<ModelDescriptor> Models);

/// <summary>
/// 功能：从同一冻结 manifest/catalog 构建 ADR 0018 六条 canonical executable route 快照。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal static partial class ProviderExecutableRouteSnapshot
{
    internal const string ConfigurationEnvironmentVariable =
        "AGENT_PROVIDER_ROUTE_CONFORMANCE_CONFIG";

    private const int MaxConfigurationBytes = 1_048_576;
    private const int MaxJsonDepth = 128;

    private static readonly RouteDefinition[] Definitions =
    [
        new("groq", "text", "openai-completions", "openai-completions-v1", "GROQ_API_KEY", 7),
        new("minimax", "text", "anthropic-messages", "anthropic-messages-v1", "MINIMAX_API_KEY", 3),
        new("mistral", "text", "mistral-conversations", "mistral-conversations-v1", "MISTRAL_API_KEY", 30),
        new("openai", "text", "openai-responses", "openai-responses-v1", "OPENAI_API_KEY", 42),
        new("google", "text", "google-generative-ai", "google-generative-ai-v1", "GEMINI_API_KEY", 16),
        new("openrouter", "image", "openrouter-images", "openrouter-images-v1", "OPENROUTER_API_KEY", 35),
    ];

    /// <summary>
    /// 功能：加载六条生产 route，或按严格 conformance 配置收窄为一条 route 与一个模型。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="configurationPath">null 表示生产快照；非 null 表示已通过双 conformance 开关授权的配置路径。</param>
    /// <returns>按 Provider/family 排序的不可变 route plan。</returns>
    /// <remarks>不变量：配置只能收窄并替换 endpoint，不能创造 manifest 外身份、adapter、credential source、header 或能力。</remarks>
    /// <exception cref="ProviderExecutableRouteException">冻结证据、文件、JSON、route、model 或 endpoint 任一约束失败。</exception>
    internal static IReadOnlyList<ProviderExecutableRoutePlan> Load(string? configurationPath)
    {
        try
        {
            var (manifestBytes, catalogBytes) =
                ProviderIdentityAdvertisement.LoadValidatedFrozenDocuments();
            using var manifest = ParseObject(manifestBytes);
            using var catalog = ParseObject(catalogBytes);
            var routes = BuildProductionSnapshot(manifest.RootElement, catalog.RootElement);
            if (configurationPath is null)
            {
                return routes;
            }

            var configurationBytes = ReadBoundedRegularFile(
                configurationPath,
                MaxConfigurationBytes);
            using var configuration = ParseObject(configurationBytes);
            var selection = ParseConformanceSelection(configuration.RootElement);
            var route = routes.SingleOrDefault(item =>
                string.Equals(item.ProviderId, selection.ProviderId, StringComparison.Ordinal) &&
                string.Equals(item.ApiFamily, selection.ApiFamily, StringComparison.Ordinal));
            if (route is null)
            {
                throw new InvalidDataException("provider route selection is outside the frozen spine");
            }

            var model = route.Models.SingleOrDefault(item =>
                string.Equals(item.ModelId, selection.ModelId, StringComparison.Ordinal));
            if (model is null)
            {
                throw new InvalidDataException("provider route model is outside the catalog allowlist");
            }

            return
            [
                route with
                {
                    EndpointBase = selection.EndpointBase,
                    Models = Array.AsReadOnly([CloneModel(model)]),
                },
            ];
        }
        catch (ProviderExecutableRouteException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ProviderExecutableRouteException();
        }
    }

    /// <summary>
    /// 功能：从已全量验证的 canonical JSON 中提取并再次收紧 ADR 0018 六条 route。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="manifest">已验证 manifest 根对象。</param>
    /// <param name="catalog">已验证 catalog 根对象。</param>
    /// <returns>六条按 route key 排序的生产 plan。</returns>
    /// <remarks>不变量：每条 route 必须无 quirk、使用 api-family-default、唯一 api-key env，且所有模型共享固定 HTTPS base。</remarks>
    private static ProviderExecutableRoutePlan[] BuildProductionSnapshot(
        JsonElement manifest,
        JsonElement catalog)
    {
        var result = new List<ProviderExecutableRoutePlan>(Definitions.Length);
        foreach (var definition in Definitions)
        {
            result.Add(BuildRoute(manifest, catalog, definition));
        }

        return result
            .OrderBy(static route => route.ProviderId, StringComparer.Ordinal)
            .ThenBy(static route => route.ApiFamily, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 功能：把一条代码固定 route 定义与 manifest auth/route 及全部 catalog 行精确闭合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="manifest">canonical manifest 根对象。</param>
    /// <param name="catalog">canonical catalog 根对象。</param>
    /// <param name="definition">ADR 0018 固定 route 身份与计数。</param>
    /// <returns>只含非 secret 计划字段与公共 descriptor 的 route plan。</returns>
    /// <exception cref="InvalidDataException">route/auth/header/quirk/endpoint/model 投影不满足固定下界。</exception>
    private static ProviderExecutableRoutePlan BuildRoute(
        JsonElement manifest,
        JsonElement catalog,
        RouteDefinition definition)
    {
        var provider = manifest.GetProperty("providers")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == definition.ProviderId);
        var environment = provider.GetProperty("environment").EnumerateArray().ToArray();
        if (environment.Length != 1 ||
            environment[0].GetProperty("name").GetString() != definition.CredentialEnvironmentName ||
            environment[0].GetProperty("role").GetString() != "secret")
        {
            throw new InvalidDataException("provider route credential source drifted");
        }

        var profile = provider.GetProperty("authProfiles")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "api-key");
        if (provider.GetProperty("authProfiles").GetArrayLength() != 1 ||
            profile.GetProperty("kind").GetString() != "api-key" ||
            !ReadStringArray(profile.GetProperty("environment"))
                .SequenceEqual([definition.CredentialEnvironmentName], StringComparer.Ordinal))
        {
            throw new InvalidDataException("provider route auth profile drifted");
        }

        var route = provider.GetProperty("routes")
            .EnumerateArray()
            .Single(item => item.GetProperty("apiFamily").GetString() == definition.ApiFamily);
        var endpointPolicy = route.GetProperty("endpoint");
        if (route.GetProperty("media").GetString() != definition.Media ||
            route.GetProperty("adapterId").GetString() != definition.AdapterId ||
            route.GetProperty("modelCatalogId").GetString() != "pi-v1-frozen" ||
            route.GetProperty("headerPolicyId").GetString() != "api-family-default" ||
            route.GetProperty("quirks").GetArrayLength() != 0 ||
            !ReadStringArray(route.GetProperty("authProfileIds"))
                .SequenceEqual(["api-key"], StringComparer.Ordinal) ||
            endpointPolicy.GetProperty("source").GetString() != "model-catalog" ||
            endpointPolicy.GetProperty("templateBindings").GetArrayLength() != 0 ||
            endpointPolicy.GetProperty("runtimeEndpointEnv").GetArrayLength() != 0 ||
            endpointPolicy.GetProperty("runtimeOverride").GetString() != "explicit-configuration-only")
        {
            throw new InvalidDataException("provider executable route policy drifted");
        }

        var rows = catalog.GetProperty("models")
            .EnumerateArray()
            .Where(item =>
                item.GetProperty("providerId").GetString() == definition.ProviderId &&
                item.GetProperty("apiFamily").GetString() == definition.ApiFamily)
            .ToArray();
        if (rows.Length != definition.ModelCount)
        {
            throw new InvalidDataException("provider executable route model count drifted");
        }

        string? endpointText = null;
        var models = new List<ModelDescriptor>(rows.Length);
        foreach (var row in rows)
        {
            var endpoint = row.GetProperty("endpoint");
            if (endpoint.GetProperty("strategy").GetString() != "fixed" ||
                row.TryGetProperty("fixedHeaders", out _) ||
                row.TryGetProperty("compatibility", out _))
            {
                throw new InvalidDataException("provider executable route catalog policy drifted");
            }

            var currentEndpoint = endpoint.GetProperty("baseUrl").GetString()!;
            endpointText ??= currentEndpoint;
            if (!string.Equals(endpointText, currentEndpoint, StringComparison.Ordinal))
            {
                throw new InvalidDataException("provider executable route endpoint is inconsistent");
            }

            models.Add(ProjectModel(row, definition));
        }

        var endpointBase = ParseProductionEndpoint(endpointText!);
        return new ProviderExecutableRoutePlan(
            definition.ProviderId,
            definition.ApiFamily,
            definition.AdapterId,
            definition.CredentialEnvironmentName,
            endpointBase,
            Array.AsReadOnly(models
                .OrderBy(static model => model.ModelId, StringComparer.Ordinal)
                .Select(CloneModel)
                .ToArray()));
    }

    /// <summary>
    /// 功能：按 ADR 0018 的 family 下界投影 catalog 能力，禁止 catalog 证据放大 runtime claim。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="row">已严格验证的单条 catalog model。</param>
    /// <param name="definition">该 route 的固定 text/image 类型。</param>
    /// <returns>可进入 models/list 与 run/start 同一快照的公共 descriptor。</returns>
    /// <exception cref="InvalidDataException">投影后 input/output 为空或 catalog 缺少必需 text 能力。</exception>
    private static ModelDescriptor ProjectModel(JsonElement row, RouteDefinition definition)
    {
        var capabilities = row.GetProperty("capabilities");
        var sourceInput = ReadStringArray(capabilities.GetProperty("input"));
        string[] input;
        string[] output;
        int? contextTokens = null;
        int? maxOutputTokens = null;
        if (definition.Media == "text")
        {
            if (!sourceInput.Contains("text", StringComparer.Ordinal))
            {
                throw new InvalidDataException("provider text route lacks text input");
            }

            input = ["text"];
            output = ["text"];
            var limits = row.GetProperty("limits");
            contextTokens = limits.GetProperty("contextWindow").GetInt32();
            maxOutputTokens = limits.GetProperty("maxOutputTokens").GetInt32();
        }
        else
        {
            input = sourceInput
                .Where(static media => media is "image" or "text")
                .ToArray();
            output = ReadStringArray(capabilities.GetProperty("output"))
                .Where(static media => media is "image" or "text")
                .ToArray();
        }

        if (input.Length == 0 || output.Length == 0)
        {
            throw new InvalidDataException("provider executable model projection is empty");
        }

        return new ModelDescriptor(
            definition.ProviderId,
            row.GetProperty("modelId").GetString()!,
            row.GetProperty("name").GetString()!,
            definition.ApiFamily,
            new ModelCapabilities(
                input,
                output,
                Streaming: definition.Media == "text",
                Tools: definition.Media == "text",
                Reasoning: false,
                contextTokens,
                maxOutputTokens));
    }

    /// <summary>
    /// 功能：严格解析 conformance 配置的五个封闭字段和 literal IPv4 loopback endpoint base。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="root">完成重复键拒绝的配置根对象。</param>
    /// <returns>单 route、单模型与安全 endpoint 选择。</returns>
    /// <remarks>不变量：配置不含 credential、env、adapter、header、quirk、query 或 fragment。</remarks>
    /// <exception cref="InvalidDataException">字段集合、常量、字符串边界或 endpoint literal 无效。</exception>
    private static ConformanceSelection ParseConformanceSelection(JsonElement root)
    {
        EnsureExactProperties(
            root,
            ["schemaVersion", "providerId", "apiFamily", "modelId", "endpointBase"]);
        if (RequireString(root, "schemaVersion", 3) != "0.1")
        {
            throw new InvalidDataException("provider route schema version is invalid");
        }

        var providerId = RequireString(root, "providerId", 128);
        var apiFamily = RequireString(root, "apiFamily", 128);
        var modelId = RequireString(root, "modelId", 256);
        if (!IsProviderId(providerId) || !IsSlug(apiFamily) ||
            modelId.Any(static character => character is <= '\u001f' or '\u007f'))
        {
            throw new InvalidDataException("provider route identity is invalid");
        }

        var endpointText = RequireString(root, "endpointBase", 2048);
        return new ConformanceSelection(
            providerId,
            apiFamily,
            modelId,
            ParseLoopbackEndpoint(endpointText));
    }

    /// <summary>
    /// 功能：有界读取 regular 配置文件，并在打开前后拒绝目录、reparse point、链接与增长竞态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">仅来自固定 presence 环境变量且不会进入诊断的路径。</param>
    /// <param name="maximumBytes">完整文件的正硬上限。</param>
    /// <returns>完整、非空且长度稳定的文件字节。</returns>
    /// <remarks>不变量：调用只发生在双 conformance 授权后；生产 presence 不进入本方法。</remarks>
    /// <exception cref="IOException">路径、文件类型、链接、长度或读取稳定性无效。</exception>
    private static byte[] ReadBoundedRegularFile(string path, int maximumBytes)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4096 || maximumBytes <= 0)
        {
            throw new IOException("provider route configuration is invalid");
        }

        using var stream = OpenReadNoFollow(path);
        if (!stream.CanSeek || stream.Length is <= 0 || stream.Length > maximumBytes)
        {
            throw new IOException("provider route configuration size is invalid");
        }

        var expectedLength = stream.Length;
        var result = new byte[checked((int)expectedLength)];
        stream.ReadExactly(result);
        if (stream.ReadByte() != -1 || stream.Length != expectedLength)
        {
            throw new IOException("provider route configuration changed while reading");
        }

        return result;
    }

    /// <summary>
    /// 功能：以 OS no-follow 语义打开只读配置句柄，并拒绝非 regular/seekable 文件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">不进入诊断的配置路径。</param>
    /// <returns>拥有底层句柄且由调用方释放的同步只读 FileStream。</returns>
    /// <remarks>不变量：Linux 使用 O_NOFOLLOW；Windows 使用 OPEN_REPARSE_POINT 并检查 handle attributes；其它平台失败关闭。</remarks>
    /// <exception cref="IOException">打开、链接/type 检查或句柄包装失败。</exception>
    private static FileStream OpenReadNoFollow(string path)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsLinux())
        {
            const int openReadOnly = 0;
            const int openNonBlocking = 0x00000800;
            const int openNoFollow = 0x00020000;
            const int openCloseOnExec = 0x00080000;
            var descriptor = OpenUnix(
                path,
                openReadOnly | openNonBlocking | openNoFollow | openCloseOnExec);
            if (descriptor < 0)
            {
                throw new IOException("provider route configuration cannot be opened safely");
            }

            handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        }
        else if (OperatingSystem.IsWindows())
        {
            const uint genericRead = 0x80000000;
            const uint shareRead = 0x00000001;
            const uint openExisting = 3;
            const uint sequentialScan = 0x08000000;
            const uint openReparsePoint = 0x00200000;
            handle = CreateFileWindows(
                path,
                genericRead,
                shareRead,
                0,
                openExisting,
                sequentialScan | openReparsePoint,
                0);
            if (handle.IsInvalid || !IsWindowsRegularFile(handle))
            {
                handle.Dispose();
                throw new IOException("provider route configuration cannot be opened safely");
            }
        }
        else
        {
            throw new IOException("provider route no-follow file access is unavailable");
        }

        try
        {
            var stream = new FileStream(handle, FileAccess.Read, 65_536, isAsync: false);
            if (!stream.CanSeek)
            {
                stream.Dispose();
                throw new IOException("provider route configuration is not a regular file");
            }

            return stream;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 功能：从 Windows handle 属性判断目标既非目录也非 reparse point。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="handle">以 OPEN_REPARSE_POINT 打开的有效句柄。</param>
    /// <returns>属性查询成功且目标为普通候选文件时为 true。</returns>
    private static bool IsWindowsRegularFile(SafeFileHandle handle)
    {
        const int fileAttributeTagInfo = 9;
        const uint directory = 0x00000010;
        const uint reparsePoint = 0x00000400;
        return GetFileInformationByHandleExWindows(
                handle,
                fileAttributeTagInfo,
                out var information,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()) != 0 &&
            (information.FileAttributes & (directory | reparsePoint)) == 0;
    }

    /// <summary>
    /// 功能：调用 Unix `open(2)` 获取带 O_NOFOLLOW/O_CLOEXEC 的配置 fd。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">UTF-8 文件路径。</param>
    /// <param name="flags">代码固定只读安全 flags。</param>
    /// <returns>成功 fd；失败为负数。</returns>
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenUnix(string path, int flags);

    /// <summary>
    /// 功能：调用 Windows CreateFileW 以 OPEN_REPARSE_POINT 打开配置句柄。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">UTF-16 文件路径。</param>
    /// <param name="desiredAccess">只读访问掩码。</param>
    /// <param name="shareMode">只允许共享读。</param>
    /// <param name="securityAttributes">固定 null。</param>
    /// <param name="creationDisposition">固定 OPEN_EXISTING。</param>
    /// <param name="flagsAndAttributes">no-follow 与顺序读 flags。</param>
    /// <param name="templateFile">固定 null。</param>
    /// <returns>拥有原生句柄的 SafeFileHandle。</returns>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileWindows(
        string path,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    /// <summary>
    /// 功能：查询 Windows handle 的 FILE_ATTRIBUTE_TAG_INFO 以拒绝目录与 reparse point。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="handle">有效文件句柄。</param>
    /// <param name="informationClass">固定 FileAttributeTagInfo。</param>
    /// <param name="information">返回属性与 reparse tag。</param>
    /// <param name="bufferSize">固定结构大小。</param>
    /// <returns>非零表示成功。</returns>
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static partial int GetFileInformationByHandleExWindows(
        SafeFileHandle handle,
        int informationClass,
        out FileAttributeTagInformation information,
        uint bufferSize);

    /// <summary>
    /// 功能：按标准 JSON、严格 UTF-8、单根对象、深度上限和无重复键解析完整文档。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="bytes">已完成来源与大小验证的 JSON bytes。</param>
    /// <returns>由调用方释放的独立 JsonDocument。</returns>
    /// <exception cref="InvalidDataException">UTF-8、JSON、深度、根类型或重复键无效。</exception>
    private static JsonDocument ParseObject(byte[] bytes)
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
            throw new InvalidDataException("provider route JSON is invalid", exception);
        }

        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("provider route JSON root is invalid");
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
    /// 功能：递归拒绝任意 object 中大小写敏感的重复 JSON 属性名。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">当前 JSON 节点。</param>
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
                    throw new InvalidDataException("provider route JSON contains a duplicate key");
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
    /// 功能：要求配置 object 精确包含固定字段且没有未知字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">待验证 object。</param>
    /// <param name="names">必需且唯一的字段名集合。</param>
    /// <exception cref="InvalidDataException">根非 object、字段缺失或出现未知字段。</exception>
    private static void EnsureExactProperties(JsonElement element, IReadOnlyList<string> names)
    {
        if (element.ValueKind != JsonValueKind.Object || element.GetPropertyCount() != names.Count)
        {
            throw new InvalidDataException("provider route object shape is invalid");
        }

        var expected = names.ToHashSet(StringComparer.Ordinal);
        if (element.EnumerateObject().Any(property => !expected.Remove(property.Name)) ||
            expected.Count != 0)
        {
            throw new InvalidDataException("provider route object fields are invalid");
        }
    }

    /// <summary>
    /// 功能：读取有界非空字符串属性且不回显实例值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父 object。</param>
    /// <param name="name">代码固定属性名。</param>
    /// <param name="maximumLength">正字符上限。</param>
    /// <returns>有效字符串。</returns>
    /// <exception cref="InvalidDataException">属性缺失、非字符串、为空或越界。</exception>
    private static string RequireString(JsonElement element, string name, int maximumLength)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("provider route string field is invalid");
        }

        var result = value.GetString()!;
        if (result.Length is 0 || result.Length > maximumLength)
        {
            throw new InvalidDataException("provider route string length is invalid");
        }

        return result;
    }

    /// <summary>
    /// 功能：读取 canonical 文档中已严格验证的字符串数组并保留源顺序。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">JSON array 节点。</param>
    /// <returns>独立字符串数组。</returns>
    /// <exception cref="InvalidDataException">节点或任一元素类型无效。</exception>
    private static string[] ReadStringArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("provider route array field is invalid");
        }

        return element.EnumerateArray()
            .Select(static item => item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw new InvalidDataException("provider route array item is invalid"))
            .ToArray();
    }

    /// <summary>
    /// 功能：验证 catalog 生产 endpoint 是无 userinfo/query/fragment 的固定 HTTPS base。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">冻结 catalog baseUrl。</param>
    /// <returns>安全绝对 HTTPS URI。</returns>
    /// <exception cref="InvalidDataException">URI 非绝对、非 HTTPS 或含可改变认证目标的组成部分。</exception>
    private static Uri ParseProductionEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(endpoint.Host) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidDataException("provider route production endpoint is invalid");
        }

        return endpoint;
    }

    /// <summary>
    /// 功能：按 Schema literal 文法验证 conformance endpoint 为显式端口的 127.0.0.1 HTTP base。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">配置 endpointBase 原文。</param>
    /// <returns>不会经 DNS、userinfo、query、fragment 或编码 authority delimiter 解释的 URI。</returns>
    /// <remarks>不变量：只允许 ASCII unreserved path segments，且端口在 1..65535。</remarks>
    /// <exception cref="InvalidDataException">literal 或解析结果不满足精确回环边界。</exception>
    private static Uri ParseLoopbackEndpoint(string value)
    {
        const string prefix = "http://127.0.0.1:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("provider route conformance endpoint is invalid");
        }

        var remainder = value.AsSpan(prefix.Length);
        var slash = remainder.IndexOf('/');
        var portText = slash < 0 ? remainder : remainder[..slash];
        var path = slash < 0 ? ReadOnlySpan<char>.Empty : remainder[slash..];
        if (portText.Length is < 1 or > 5 ||
            portText[0] == '0' ||
            !portText.ToString().All(static character => character is >= '0' and <= '9') ||
            !int.TryParse(portText, out var port) ||
            port is < 1 or > 65535 ||
            !IsLiteralPath(path) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttp ||
            endpoint.Host != "127.0.0.1" ||
            endpoint.Port != port ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidDataException("provider route conformance endpoint is invalid");
        }

        return endpoint;
    }

    /// <summary>
    /// 功能：验证 endpoint path 为空或由非空 ASCII unreserved segments 组成。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">端口后的原始 path span。</param>
    /// <returns>精确符合 `(?:/[A-Za-z0-9._~-]+)*` 时为 true。</returns>
    private static bool IsLiteralPath(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty)
        {
            return true;
        }

        if (path[0] != '/' || path[^1] == '/')
        {
            return false;
        }

        return path[1..]
            .ToString()
            .Split('/', StringSplitOptions.None)
            .All(static segment =>
                segment.Length > 0 && segment.All(static character =>
                    character is >= 'A' and <= 'Z' or
                        >= 'a' and <= 'z' or
                        >= '0' and <= '9' or '.' or '_' or '~' or '-'));
    }

    /// <summary>
    /// 功能：判断 Provider ID 是否符合允许点号的品牌中立小写 ASCII 语法。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选 Provider ID。</param>
    /// <returns>长度、首字符和字符集均有效时为 true。</returns>
    private static bool IsProviderId(string value)
    {
        return value.Length is >= 1 and <= 128 &&
            IsLowerAsciiLetterOrDigit(value[0]) &&
            value.All(static character =>
                IsLowerAsciiLetterOrDigit(character) || character is '-' or '.');
    }

    /// <summary>
    /// 功能：判断 API family 是否符合小写 ASCII slug 语法。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选 family。</param>
    /// <returns>长度、首字符和字符集均有效时为 true。</returns>
    private static bool IsSlug(string value)
    {
        return value.Length is >= 1 and <= 128 &&
            IsLowerAsciiLetterOrDigit(value[0]) &&
            value.All(static character => IsLowerAsciiLetterOrDigit(character) || character == '-');
    }

    /// <summary>
    /// 功能：判断字符是否为小写 ASCII 字母或数字。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选字符。</param>
    /// <returns>属于闭合允许集合时为 true。</returns>
    private static bool IsLowerAsciiLetterOrDigit(char value)
    {
        return value is >= 'a' and <= 'z' or >= '0' and <= '9';
    }

    /// <summary>
    /// 功能：深复制 descriptor 媒介数组，隔离 route snapshot 与 registry 调用方。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="model">已完成能力投影的 descriptor。</param>
    /// <returns>字段相同且数组独立的新 descriptor。</returns>
    private static ModelDescriptor CloneModel(ModelDescriptor model)
    {
        return model with
        {
            Capabilities = model.Capabilities with
            {
                Input = model.Capabilities.Input.ToArray(),
                Output = model.Capabilities.Output.ToArray(),
            },
        };
    }

    /// <summary>
    /// 功能：保存 ADR 0018 单条 route 的代码固定身份、credential source 与模型计数下界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="ProviderId">canonical Provider ID。</param>
    /// <param name="Media">text 或 image。</param>
    /// <param name="ApiFamily">canonical API family。</param>
    /// <param name="AdapterId">固定 manifest adapter ID。</param>
    /// <param name="CredentialEnvironmentName">唯一 API-key 环境源名称。</param>
    /// <param name="ModelCount">冻结 catalog route 模型数。</param>
    private sealed record RouteDefinition(
        string ProviderId,
        string Media,
        string ApiFamily,
        string AdapterId,
        string CredentialEnvironmentName,
        int ModelCount);

    /// <summary>
    /// 功能：保存严格 conformance 文件解析得到的单 route、单模型与 loopback endpoint 选择。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="ProviderId">canonical Provider ID。</param>
    /// <param name="ApiFamily">canonical API family。</param>
    /// <param name="ModelId">catalog allowlist model ID。</param>
    /// <param name="EndpointBase">literal 127.0.0.1 base。</param>
    private sealed record ConformanceSelection(
        string ProviderId,
        string ApiFamily,
        string ModelId,
        Uri EndpointBase);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }
}
