using System.Runtime.CompilerServices;
using System.Text.Json;
using QxnmForge.Domain;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：定义所有原生 Provider 归一化流信号的封闭基础类型。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public abstract record ProviderSignal;

/// <summary>
/// 功能：表示 Provider 文本增量。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Text">按上游顺序归一化的非空文本。</param>
public sealed record ProviderTextSignal(string Text) : ProviderSignal;

/// <summary>
/// 功能：表示已从 partial JSON 参数流完整组装并严格解析的工具调用。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ToolCall">语言中立工具调用。</param>
public sealed record ProviderToolCallSignal(ProviderToolCall ToolCall) : ProviderSignal;

/// <summary>
/// 功能：表示 Provider 报告的最终规范化 token 用量。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Usage">input、output 与 total token。</param>
public sealed record ProviderUsageSignal(Usage Usage) : ProviderSignal;

/// <summary>
/// 功能：表示 OpenRouter Images 已完整验证的一张解码图像，仅在 Provider/Agent 内存边界流转。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="MediaType">已与魔数匹配的受支持 MIME。</param>
/// <param name="Bytes">尚未发布、不会进入 wire DTO 的精确图像字节。</param>
internal sealed record ProviderImagePayload(string MediaType, byte[] Bytes);

/// <summary>
/// 功能：以单个信号交付整批已验证图像、可选文字和 usage，阻止逐图提前发布。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Text">可选最终文字；不得作为 delta 发送。</param>
/// <param name="Images">按 Provider source 顺序完成全批验证的图像。</param>
/// <param name="Usage">严格归一化的可选 Provider usage；缺失为零值。</param>
internal sealed record ProviderImageCompletionSignal(
    string? Text,
    IReadOnlyList<ProviderImagePayload> Images,
    Usage Usage) : ProviderSignal;

/// <summary>
/// 功能：表示在下一 HTTP attempt 前已决定的有界重试。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Attempt">即将开始的 attempt，从 2 开始。</param>
/// <param name="DelayMs">已应用 Retry-After 和策略上限的延迟。</param>
/// <param name="Reason">不含 endpoint、header、凭据或 raw body 的结构化原因。</param>
public sealed record ProviderRetrySignal(
    int Attempt,
    long DelayMs,
    PortableError Reason) : ProviderSignal;

/// <summary>
/// 功能：表示完整且不含 Provider 私有对象的工具调用。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ToolCallId">opaque 工具调用 ID。</param>
/// <param name="Name">规范工具名。</param>
/// <param name="Arguments">严格 JSON object 参数。</param>
public sealed record ProviderToolCall(
    string ToolCallId,
    string Name,
    JsonElement Arguments);

/// <summary>
/// 功能：表示映射到 live Provider 请求的语言中立工具声明。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Name">规范工具名。</param>
/// <param name="Description">公开工具功能描述。</param>
/// <param name="InputSchema">受限 JSON Schema object。</param>
public sealed record ProviderToolDefinition(
    string Name,
    string Description,
    JsonElement InputSchema);

/// <summary>
/// 功能：表示从同一 Session artifact 安全复核后提供给图像 adapter 的请求期输入。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Reference">已与 earlier artifact.created 强绑定的 portable 引用。</param>
/// <param name="Bytes">no-follow 读取并通过 length/hash/MIME/魔数复核的字节。</param>
internal sealed record ProviderResolvedImage(ArtifactReference Reference, byte[] Bytes);

/// <summary>
/// 功能：表示发送给任一原生 Provider family 的完整请求上下文。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Selection">Provider ID 与模型 ID。</param>
/// <param name="Messages">有序 portable user、assistant 与 tool 消息。</param>
/// <param name="Tools">本次允许映射到 Provider 的工具声明；空数组表示不声明工具。</param>
/// <param name="FauxScenario">仅 faux 使用的确定性场景，live family 必须忽略。</param>
public sealed record ProviderRequest(
    ProviderSelection Selection,
    IReadOnlyList<JsonElement> Messages,
    IReadOnlyList<ProviderToolDefinition> Tools,
    FauxScenario? FauxScenario = null)
{
    /// <summary>
    /// 功能：取得仅供图像 family 构造 request-local data URL 的已复核输入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <remarks>不变量：该列表不序列化、不持久化且不含 host path。</remarks>
    internal IReadOnlyList<ProviderResolvedImage> ResolvedImages { get; init; } = [];

    /// <summary>
    /// 功能：取得可选 request-local system instructions。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <remarks>不变量：该文本不作为普通 Session message 持久化或回显到日志。</remarks>
    public string? SystemInstructions { get; init; }
}

/// <summary>
/// 功能：定义可独立调用的原生 Provider family 流式接口。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public interface IProvider
{
    /// <summary>
    /// 功能：取得 run/start 与能力清单使用的 Provider ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    string Id { get; }

    /// <summary>
    /// 功能：取得需要参与显式 route 选择的 API family；旧式唯一文本路由为 null。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    string? ApiFamily => null;

    /// <summary>
    /// 功能：取得静态模型列表；空列表表示接受配置提供的动态模型 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    IReadOnlyList<string> Models { get; }

    /// <summary>
    /// 功能：判断模型是否可由当前已配置 Provider 接受。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="modelId">run/start 请求模型。</param>
    /// <returns>动态模型或静态列表包含该模型时为 true。</returns>
    bool SupportsModel(string modelId);

    /// <summary>
    /// 功能：在任何 Session durable 副作用前判断当前 Provider route 是否仍可执行。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>当前请求可进入 adapter 时为 true；不依赖动态 credential 的实现默认为 true。</returns>
    /// <remarks>不变量：实现不得把 credential 值保存到 registry、DTO、Session、日志或异常。</remarks>
    bool IsAvailable() => true;

    /// <summary>
    /// 功能：原生执行一次 Provider 请求并产生文本、工具、用量或重试信号。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">完整语言中立 Provider 请求。</param>
    /// <param name="cancellationToken">贯穿 HTTP、SSE、延迟和本地取消的信号。</param>
    /// <returns>保持上游语义顺序的异步规范化信号。</returns>
    IAsyncEnumerable<ProviderSignal> StreamAsync(
        ProviderRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 功能：表示 Provider 失败应映射到 failed 或 interrupted 终态的安全结构化异常。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public class ProviderOperationException : Exception
{
    /// <summary>
    /// 功能：创建不携带 endpoint、认证头、key 或 raw body 的 Provider 异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="error">可持久化 portable error。</param>
    /// <param name="terminalStatus">仅允许 failed 或 interrupted。</param>
    public ProviderOperationException(PortableError error, string terminalStatus = "failed")
        : base(error.Message)
    {
        if (terminalStatus is not ("failed" or "interrupted"))
        {
            throw new ArgumentException("provider terminal status is invalid", nameof(terminalStatus));
        }

        Error = error;
        TerminalStatus = terminalStatus;
    }

    /// <summary>
    /// 功能：取得可安全进入协议和 Session 的结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public PortableError Error { get; }

    /// <summary>
    /// 功能：取得 failed 或 interrupted 终态映射。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string TerminalStatus { get; }
}

/// <summary>
/// 功能：表示 run/start 在任何 durable 副作用前无法解析到唯一可执行 Provider route。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ProviderUnavailableException : ArgumentException
{
    /// <summary>
    /// 功能：保存可安全进入 portable error 的 Provider/model 身份。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerId">已通过协议语法检查的 Provider ID。</param>
    /// <param name="modelId">已通过协议长度检查的模型 ID。</param>
    public ProviderUnavailableException(string providerId, string modelId)
        : base("provider/model is unavailable")
    {
        ProviderId = providerId;
        ModelId = modelId;
    }

    /// <summary>
    /// 功能：取得请求的公共 Provider ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string ProviderId { get; }

    /// <summary>
    /// 功能：取得请求的公共模型 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string ModelId { get; }
}

/// <summary>
/// 功能：表示 initialize 从同一模型快照派生的 Provider 级去重模型并集。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Id">Provider 身份。</param>
/// <param name="Models">按 ordinal 排序且无重复的模型 ID。</param>
public sealed record ProviderAdvertisement(string Id, IReadOnlyList<string> Models);

/// <summary>
/// 功能：原生注册可用 Provider，并拒绝未配置 family 与重复 ID。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ProviderRegistry : IAsyncDisposable
{
    private readonly Dictionary<RouteKey, IProvider> providers;
    private readonly ModelDescriptor[] identityModels;
    private readonly ModelDescriptor[] executableModels;

    /// <summary>
    /// 功能：从已构造 Provider 建立大小写敏感的只读注册表。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providers">至少包含 faux 的原生实现集合。</param>
    /// <param name="advertisedModels">可选、不可执行的 manifest-derived identity 快照。</param>
    /// <param name="executableModels">可选、与 route adapter 一一闭合的 catalog executable 快照。</param>
    /// <exception cref="ArgumentException">Provider route 或任一模型三元身份为空、重复、冲突或未闭合。</exception>
    public ProviderRegistry(
        IEnumerable<IProvider> providers,
        IEnumerable<ModelDescriptor>? advertisedModels = null,
        IEnumerable<ModelDescriptor>? executableModels = null)
    {
        this.providers = new Dictionary<RouteKey, IProvider>();
        foreach (var provider in providers)
        {
            var key = RouteKey.For(provider);
            if (string.IsNullOrEmpty(provider.Id) || !this.providers.TryAdd(key, provider))
            {
                throw new ArgumentException("provider registry contains an invalid or duplicate route", nameof(providers));
            }
        }

        if (this.providers.Count == 0)
        {
            throw new ArgumentException("provider registry must not be empty", nameof(providers));
        }

        var modelKeys = new HashSet<(string ProviderId, string ModelId, string ApiFamily)>();
        var snapshot = new List<ModelDescriptor>();
        foreach (var model in advertisedModels ?? [])
        {
            var key = (model.ProviderId, model.ModelId, model.ApiFamily);
            if (string.IsNullOrEmpty(model.ProviderId) ||
                model.ProviderId == "faux" ||
                string.IsNullOrEmpty(model.ModelId) ||
                string.IsNullOrEmpty(model.DisplayName) ||
                string.IsNullOrEmpty(model.ApiFamily) ||
                model.Capabilities.Input.Count == 0 ||
                model.Capabilities.Output.Count == 0 ||
                this.providers.ContainsKey(new RouteKey(model.ProviderId, model.ApiFamily)) ||
                !modelKeys.Add(key))
            {
                throw new ArgumentException(
                    "provider advertisement contains an invalid or executable identity",
                    nameof(advertisedModels));
            }

            snapshot.Add(CloneModel(model));
        }

        identityModels = snapshot
            .OrderBy(static model => model.ProviderId, StringComparer.Ordinal)
            .ThenBy(static model => model.ModelId, StringComparer.Ordinal)
            .ThenBy(static model => model.ApiFamily, StringComparer.Ordinal)
            .ToArray();

        modelKeys.Clear();
        snapshot.Clear();
        foreach (var model in executableModels ?? [])
        {
            var key = (model.ProviderId, model.ModelId, model.ApiFamily);
            var routeKey = new RouteKey(model.ProviderId, model.ApiFamily);
            if (string.IsNullOrEmpty(model.ProviderId) ||
                model.ProviderId == "faux" ||
                string.IsNullOrEmpty(model.ModelId) ||
                string.IsNullOrEmpty(model.DisplayName) ||
                string.IsNullOrEmpty(model.ApiFamily) ||
                model.Capabilities.Input.Count == 0 ||
                model.Capabilities.Output.Count == 0 ||
                !this.providers.TryGetValue(routeKey, out var provider) ||
                !provider.SupportsModel(model.ModelId) ||
                !modelKeys.Add(key))
            {
                throw new ArgumentException(
                    "provider executable model is not closed over a registered route",
                    nameof(executableModels));
            }

            snapshot.Add(CloneModel(model));
        }

        this.executableModels = snapshot
            .OrderBy(static model => model.ProviderId, StringComparer.Ordinal)
            .ThenBy(static model => model.ModelId, StringComparer.Ordinal)
            .ThenBy(static model => model.ApiFamily, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 功能：取得按 Provider ID 排序的当前可广告实现。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public IReadOnlyList<IProvider> Providers => providers.Values
        .OrderBy(static provider => provider.Id, StringComparer.Ordinal)
        .ThenBy(static provider => provider.ApiFamily, StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// 功能：从可执行 Provider 与 identity-only 模型快照生成 initialize 的唯一模型并集。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>按 Provider ID 排序的防御性能力数组。</returns>
    public IReadOnlyList<ProviderAdvertisement> Advertisements
    {
        get
        {
            var grouped = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var provider in providers.Values)
            {
                if (!provider.IsAvailable())
                {
                    continue;
                }

                if (!grouped.TryGetValue(provider.Id, out var models))
                {
                    models = new HashSet<string>(StringComparer.Ordinal);
                    grouped.Add(provider.Id, models);
                }

                var routeModels = executableModels
                    .Where(model =>
                        model.ProviderId == provider.Id &&
                        model.ApiFamily == provider.ApiFamily)
                    .Select(static model => model.ModelId)
                    .ToArray();
                models.UnionWith(routeModels.Length == 0 ? provider.Models : routeModels);
            }

            foreach (var model in identityModels)
            {
                if (!grouped.TryGetValue(model.ProviderId, out var models))
                {
                    models = new HashSet<string>(StringComparer.Ordinal);
                    grouped.Add(model.ProviderId, models);
                }

                models.Add(model.ModelId);
            }

            return grouped
                .OrderBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static item => new ProviderAdvertisement(
                    item.Key,
                    item.Value.Order(StringComparer.Ordinal).ToArray()))
                .ToArray();
        }
    }

    /// <summary>
    /// 功能：列出 faux 与 identity-only 快照中可选 Provider 过滤后的 route-qualified 模型。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="providerId">可选 Provider ID；未知但合法时返回空列表。</param>
    /// <returns>按 Provider/model/family 排序的防御性 descriptor 数组。</returns>
    /// <remarks>不变量：advertised model 永不注册成可执行 adapter。</remarks>
    public IReadOnlyList<ModelDescriptor> ListModels(string? providerId = null)
    {
        var result = new List<ModelDescriptor>();
        if (providerId is null or "faux")
        {
            result.Add(new ModelDescriptor(
                "faux",
                "faux-v1",
                "Deterministic Faux v1",
                "faux",
                new ModelCapabilities(
                    ["text"],
                    ["text"],
                    Streaming: true,
                    Tools: true,
                    Reasoning: false)));
        }

        foreach (var model in executableModels.Concat(identityModels))
        {
            if (providerId is null || string.Equals(providerId, model.ProviderId, StringComparison.Ordinal))
            {
                result.Add(CloneModel(model));
            }
        }

        return result
            .OrderBy(static model => model.ProviderId, StringComparer.Ordinal)
            .ThenBy(static model => model.ModelId, StringComparer.Ordinal)
            .ThenBy(static model => model.ApiFamily, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 功能：按 run/start 选择取得已配置 Provider，并验证模型可用性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="selection">Provider 与模型选择。</param>
    /// <returns>匹配原生实现。</returns>
    /// <exception cref="ProviderUnavailableException">Provider、family、模型、credential 或 allowlist 当前不可用。</exception>
    /// <exception cref="ArgumentException">省略 family 后命中多个可执行 route，无法唯一解析。</exception>
    public IProvider GetRequired(ProviderSelection selection)
    {
        IProvider[] candidates;
        if (selection.ApiFamily is not null)
        {
            var adapterFamily = selection.Id == "faux" && selection.ApiFamily == "faux"
                ? string.Empty
                : selection.ApiFamily;
            candidates = providers.TryGetValue(
                    new RouteKey(selection.Id, adapterFamily),
                    out var exact) &&
                SupportsExecutableModel(exact, selection.ModelId)
                    ? [exact]
                    : [];
        }
        else
        {
            candidates = providers.Values
                .Where(provider =>
                    string.Equals(provider.Id, selection.Id, StringComparison.Ordinal) &&
                    SupportsExecutableModel(provider, selection.ModelId) &&
                    provider.IsAvailable())
                .ToArray();
        }

        if (candidates.Length == 0 || !candidates[0].IsAvailable())
        {
            throw new ProviderUnavailableException(selection.Id, selection.ModelId);
        }

        if (candidates.Length > 1)
        {
            throw new ArgumentException("provider/model route is ambiguous", nameof(selection));
        }

        return candidates[0];
    }

    /// <summary>
    /// 功能：判断 provider/model 同时满足 adapter 与可选 executable snapshot allowlist。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="provider">已注册 route adapter。</param>
    /// <param name="modelId">run/start 模型 ID。</param>
    /// <returns>route 未绑定 executable snapshot 时沿用 adapter 规则；绑定时要求精确三元命中。</returns>
    private bool SupportsExecutableModel(IProvider provider, string modelId)
    {
        var routeModels = executableModels.Where(model =>
            model.ProviderId == provider.Id &&
            model.ApiFamily == provider.ApiFamily);
        var hasSnapshot = false;
        foreach (var model in routeModels)
        {
            hasSnapshot = true;
            if (string.Equals(model.ModelId, modelId, StringComparison.Ordinal))
            {
                return provider.SupportsModel(modelId);
            }
        }

        return !hasSnapshot && provider.SupportsModel(modelId);
    }

    /// <summary>
    /// 功能：深复制公共模型描述中的媒介数组，隔离 registry 与调用方可变集合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="model">已验证模型描述。</param>
    /// <returns>字段相同但集合独立的新描述。</returns>
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
    /// 功能：释放注册表中持有原生 HTTP 资源的 Provider。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>全部异步资源释放完成的操作。</returns>
    public async ValueTask DisposeAsync()
    {
        foreach (var provider in providers.Values)
        {
            if (provider is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (provider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private readonly record struct RouteKey(string ProviderId, string ApiFamily)
    {
        /// <summary>
        /// 功能：把 adapter 的可选 family 归一为 registry 内部 route key。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="provider">已构造 Provider adapter。</param>
        /// <returns>Provider ID 与 family；旧式唯一 route 使用空 family。</returns>
        internal static RouteKey For(IProvider provider)
        {
            return new RouteKey(provider.Id, provider.ApiFamily ?? string.Empty);
        }
    }
}

/// <summary>
/// 功能：提供无需状态机的空异步 Provider 流，供拒绝路径保持原生接口形状。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal static class EmptyProviderStream
{
    /// <summary>
    /// 功能：返回一个立即完成且响应取消的空 Provider 流。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="cancellationToken">进入流前检查的取消信号。</param>
    /// <returns>不产生元素的异步流。</returns>
    internal static async IAsyncEnumerable<ProviderSignal> Create(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
