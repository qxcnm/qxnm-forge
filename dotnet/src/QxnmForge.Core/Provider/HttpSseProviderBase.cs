using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using QxnmForge.Domain;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：保存 live Provider HTTP、SSE、总时限和有界重试策略。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ConnectTimeout">TCP/TLS 连接上限。</param>
/// <param name="HeadersTimeout">请求至响应 headers 上限。</param>
/// <param name="IdleTimeout">相邻响应字节读取上限。</param>
/// <param name="TotalTimeout">全部 attempts 与 delay 的总上限。</param>
/// <param name="MaxAttempts">含首次请求的最大 attempt 数。</param>
/// <param name="RetryMaxDelay">Retry-After 可采用的最大延迟。</param>
/// <param name="MaxSseEventBytes">单个 SSE 事件最大字节数。</param>
public sealed record ProviderTransportOptions(
    TimeSpan ConnectTimeout,
    TimeSpan HeadersTimeout,
    TimeSpan IdleTimeout,
    TimeSpan TotalTimeout,
    int MaxAttempts,
    TimeSpan RetryMaxDelay,
    int MaxSseEventBytes)
{
    /// <summary>
    /// 功能：返回适合正常运行的有限默认传输策略。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public static ProviderTransportOptions Default { get; } = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(5),
        3,
        TimeSpan.FromSeconds(30),
        1_048_576);

    /// <summary>
    /// 功能：验证所有 timeout、attempt 和 SSE 上限为可执行的有限范围。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">任一配置无效。</exception>
    public void Validate()
    {
        if (ConnectTimeout <= TimeSpan.Zero ||
            HeadersTimeout <= TimeSpan.Zero ||
            IdleTimeout <= TimeSpan.Zero ||
            TotalTimeout <= TimeSpan.Zero ||
            MaxAttempts is < 1 or > 100 ||
            RetryMaxDelay < TimeSpan.Zero ||
            MaxSseEventBytes is < 1024 or > 16_777_216)
        {
            throw new ArgumentOutOfRangeException(nameof(ProviderTransportOptions));
        }
    }
}

/// <summary>
/// 功能：只保存固定 credential 来源身份，并在可用性或最终请求边界按需读取值。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class ProviderCredentialSource
{
    private readonly string? literalCredential;
    private readonly string? environmentName;
    private readonly ProviderCredentialStore? credentialStore;
    private readonly string? storedProviderId;
    private readonly bool required;

    /// <summary>
    /// 功能：创建兼容旧 synthetic adapter 的进程内 literal credential 来源。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="credential">可选测试或旧 family credential。</param>
    /// <returns>不会读取环境的来源。</returns>
    /// <exception cref="ArgumentException">credential 超限或包含 CR/LF。</exception>
    internal static ProviderCredentialSource FromLiteral(string? credential)
    {
        ValidateCredential(credential);
        return new ProviderCredentialSource(credential, null, null, null, required: false);
    }

    /// <summary>
    /// 功能：创建只保存固定环境名称、且要求每次请求重新读取非空值的 canonical route 来源。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="environmentName">已由冻结 manifest 验证的唯一 API-key 环境名称。</param>
    /// <returns>不持有 credential 值的环境来源。</returns>
    /// <remarks>不变量：返回对象只保存名称；值不会进入 registry、route plan、Session、日志或异常。</remarks>
    /// <exception cref="ArgumentException">环境名称不符合固定大写 ASCII 语法。</exception>
    internal static ProviderCredentialSource FromEnvironment(string environmentName)
    {
        if (environmentName.Length is < 2 or > 128 ||
            environmentName[0] is < 'A' or > 'Z' ||
            environmentName.Any(static character =>
                character is not (>= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
        {
            throw new ArgumentException("provider credential environment name is invalid", nameof(environmentName));
        }

        return new ProviderCredentialSource(null, environmentName, null, null, required: true);
    }

    /// <summary>
    /// 功能：创建只保存工作区外 store 路径和本地 Provider ID 的请求期来源。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="store">已验证 CredentialStore。</param>
    /// <param name="providerId">安装 route 固定的 Provider ID。</param>
    /// <returns>不持有 API key 且失败不回退环境的来源。</returns>
    internal static ProviderCredentialSource FromStore(
        ProviderCredentialStore store,
        string providerId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ProviderCredentialStore.ValidateProviderId(providerId);
        return new ProviderCredentialSource(null, null, store, providerId, required: true);
    }

    /// <summary>
    /// 功能：判断当前来源是否具有可用 credential，且不保存或返回该值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>可选 literal 来源恒为 true；必需环境来源当前非空且格式安全时为 true。</returns>
    /// <remarks>不变量：环境值只存在于本次栈局部检查期间，不复制到对象字段。</remarks>
    internal bool IsAvailable()
    {
        if (credentialStore is not null)
        {
            return credentialStore.ContainsAll(storedProviderId!);
        }

        if (environmentName is null)
        {
            return true;
        }

        var credential = Environment.GetEnvironmentVariable(environmentName);
        return !string.IsNullOrEmpty(credential) && IsCredentialValid(credential);
    }

    /// <summary>
    /// 功能：仅在最终 HTTP header 构造边界解析本次请求的 credential。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="credential">成功时为本次 header 使用的值；可选 literal 缺失时为 null。</param>
    /// <returns>必需来源存在安全非空值，或可选来源合法时为 true。</returns>
    /// <remarks>不变量：调用方不得持久化、记录、缓存或放入异常；轮换环境值在下一次调用生效。</remarks>
    internal bool TryReadForRequest(out string? credential)
    {
        if (credentialStore is not null)
        {
            return credentialStore.TryReadForRequest(storedProviderId!, out credential);
        }

        credential = environmentName is null
            ? literalCredential
            : Environment.GetEnvironmentVariable(environmentName);
        if (required && string.IsNullOrEmpty(credential))
        {
            credential = null;
            return false;
        }

        if (!IsCredentialValid(credential))
        {
            credential = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// 功能：创建固定 literal 或环境来源内部状态，不执行任何外部读取。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="literalCredential">兼容路径可选 literal 值。</param>
    /// <param name="environmentName">canonical 路径固定环境名称。</param>
    /// <param name="credentialStore">本地推广 route 可选工作区外 store。</param>
    /// <param name="storedProviderId">与 store 成对的固定 Provider ID。</param>
    /// <param name="required">缺失值是否使 route 不可用。</param>
    private ProviderCredentialSource(
        string? literalCredential,
        string? environmentName,
        ProviderCredentialStore? credentialStore,
        string? storedProviderId,
        bool required)
    {
        this.literalCredential = literalCredential;
        this.environmentName = environmentName;
        this.credentialStore = credentialStore;
        this.storedProviderId = storedProviderId;
        this.required = required;
    }

    /// <summary>
    /// 功能：验证 credential 长度和 header 注入控制字符边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="credential">可选待检查值。</param>
    /// <exception cref="ArgumentException">值越界或包含 CR/LF。</exception>
    private static void ValidateCredential(string? credential)
    {
        if (!IsCredentialValid(credential))
        {
            throw new ArgumentException("provider credential is invalid", nameof(credential));
        }
    }

    /// <summary>
    /// 功能：判断可选 credential 能否安全进入单个 HTTP header value。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="credential">可选候选值。</param>
    /// <returns>null 或长度不超过 16384 且不含 CR/LF 时为 true。</returns>
    private static bool IsCredentialValid(string? credential)
    {
        return credential is not { Length: > 16_384 } &&
            credential?.Contains('\r', StringComparison.Ordinal) is not true &&
            credential?.Contains('\n', StringComparison.Ordinal) is not true;
    }
}

/// <summary>
/// 功能：集中实现禁止重定向的原生 HttpClient、ResponseHeadersRead、timeout 与安全重试。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public abstract class HttpSseProviderBase : IProvider, IDisposable
{
    private readonly Uri endpoint;
    private readonly ProviderCredentialSource credentialSource;
    private readonly string authenticationHeader;
    private readonly string authenticationPrefix;
    private readonly IReadOnlyDictionary<string, string> additionalHeaders;
    private readonly string? apiFamily;
    private readonly IReadOnlyList<string> models;
    private readonly HashSet<string>? modelSet;
    private readonly ProviderTransportOptions options;
    private readonly HttpClient httpClient;
    private bool disposed;

    /// <summary>
    /// 功能：创建共享安全 HTTP transport；credential 只保存在请求边界内存，不进入异常或 DTO。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="id">能力与 run/start 使用的 Provider ID。</param>
    /// <param name="endpoint">已经过 scheme/origin/query 安全验证的精确 endpoint。</param>
    /// <param name="credential">可选凭据；仅用于最终请求 header。</param>
    /// <param name="authenticationHeader">认证 header 名。</param>
    /// <param name="authenticationPrefix">认证值前缀。</param>
    /// <param name="additionalHeaders">不含 secret 的 family 固定 headers。</param>
    /// <param name="options">有界 transport 策略。</param>
    /// <exception cref="ArgumentException">endpoint、认证 header 或凭据不符合安全边界。</exception>
    protected HttpSseProviderBase(
        string id,
        Uri endpoint,
        string? credential,
        string authenticationHeader,
        string authenticationPrefix,
        IReadOnlyDictionary<string, string>? additionalHeaders,
        ProviderTransportOptions options)
        : this(
            id,
            endpoint,
            ProviderCredentialSource.FromLiteral(credential),
            authenticationHeader,
            authenticationPrefix,
            additionalHeaders,
            options,
            null,
            null)
    {
    }

    /// <summary>
    /// 功能：为 canonical route 创建只保存 credential 环境名称和 catalog allowlist 的共享安全 HTTP transport。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="id">canonical Provider ID。</param>
    /// <param name="endpoint">catalog base 或 conformance loopback override。</param>
    /// <param name="credentialSource">只保存固定来源身份的 credential 解析器。</param>
    /// <param name="authenticationHeader">family 固定认证 header 名。</param>
    /// <param name="authenticationPrefix">family 固定认证值前缀。</param>
    /// <param name="additionalHeaders">不含 secret 的 family 固定 headers。</param>
    /// <param name="options">有界 transport 策略。</param>
    /// <param name="apiFamily">route key 中的显式 API family。</param>
    /// <param name="models">同一 executable snapshot 的非空 catalog allowlist。</param>
    /// <remarks>不变量：adapter 不持有环境 credential 值；route/model 与 request-target 都不能改变认证 origin。</remarks>
    /// <exception cref="ArgumentException">身份、endpoint、header、route 或模型集合无效。</exception>
    private protected HttpSseProviderBase(
        string id,
        Uri endpoint,
        ProviderCredentialSource credentialSource,
        string authenticationHeader,
        string authenticationPrefix,
        IReadOnlyDictionary<string, string>? additionalHeaders,
        ProviderTransportOptions options,
        string? apiFamily,
        IReadOnlyList<string>? models)
    {
        if (string.IsNullOrEmpty(id) ||
            !endpoint.IsAbsoluteUri ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            string.IsNullOrWhiteSpace(authenticationHeader) ||
            authenticationHeader.Contains('\r', StringComparison.Ordinal) ||
            authenticationHeader.Contains('\n', StringComparison.Ordinal) ||
            authenticationPrefix.Contains('\r', StringComparison.Ordinal) ||
            authenticationPrefix.Contains('\n', StringComparison.Ordinal) ||
            (apiFamily is not null && string.IsNullOrWhiteSpace(apiFamily)) ||
            additionalHeaders?.Any(static pair =>
                string.IsNullOrWhiteSpace(pair.Key) ||
                pair.Key.Contains('\r', StringComparison.Ordinal) ||
                pair.Key.Contains('\n', StringComparison.Ordinal) ||
                pair.Value.Contains('\r', StringComparison.Ordinal) ||
                pair.Value.Contains('\n', StringComparison.Ordinal)) is true)
        {
            throw new ArgumentException("provider HTTP configuration is invalid");
        }

        options.Validate();
        Id = id;
        this.endpoint = endpoint;
        this.credentialSource = credentialSource;
        this.authenticationHeader = authenticationHeader;
        this.authenticationPrefix = authenticationPrefix;
        this.additionalHeaders = additionalHeaders ?? new Dictionary<string, string>();
        this.apiFamily = apiFamily;
        this.models = models is null
            ? Array.Empty<string>()
            : Array.AsReadOnly(models.ToArray());
        if (this.models.Any(static model => string.IsNullOrEmpty(model) || model.Length > 256) ||
            this.models.Distinct(StringComparer.Ordinal).Count() != this.models.Count)
        {
            throw new ArgumentException("provider route model allowlist is invalid", nameof(models));
        }

        modelSet = this.models.Count == 0
            ? null
            : new HashSet<string>(this.models, StringComparer.Ordinal);
        this.options = options;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = options.ConnectTimeout,
            UseCookies = false,
            UseProxy = false,
        };
        httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// 功能：取得注册和协议能力使用的 Provider ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// 功能：取得需要显式参与路由的 API family；唯一旧式文本 adapter 默认为 null。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public virtual string? ApiFamily => apiFamily;

    /// <summary>
    /// 功能：取得动态模型清单；首批 family 接受配置给出的非空模型 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public virtual IReadOnlyList<string> Models => models;

    /// <summary>
    /// 功能：在 run/start durable 副作用前检查 canonical credential 来源当前仍可用。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>必需环境 credential 当前非空且 header-safe，或兼容来源不要求 credential 时为 true。</returns>
    /// <remarks>不变量：检查不缓存或返回 credential 值；最终请求仍会重新读取以支持轮换。</remarks>
    public virtual bool IsAvailable()
    {
        return credentialSource.IsAvailable();
    }

    /// <summary>
    /// 功能：向派生 family 暴露已验证的有界传输策略，不包含 endpoint 或凭据。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    protected ProviderTransportOptions TransportOptions => options;

    /// <summary>
    /// 功能：取得 family 原生响应 Accept media type；流式 family 默认为 text/event-stream。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    protected virtual string AcceptMediaType => "text/event-stream";

    /// <summary>
    /// 功能：确认动态模型 ID 非空且在公共长度上限内。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="modelId">调用方选择的模型。</param>
    /// <returns>长度 1 至 256 时为 true。</returns>
    public virtual bool SupportsModel(string modelId)
    {
        return modelSet?.Contains(modelId) ?? modelId.Length is >= 1 and <= 256;
    }

    /// <summary>
    /// 功能：取得此 HTTP Provider route 是否接受 function tool 定义。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <remarks>原生 family 默认支持；自定义连接必须按启动快照中的显式声明覆盖。</remarks>
    public virtual bool SupportsTools => true;

    /// <summary>
    /// 功能：默认声明通用 SSE route 不接受图片输入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public virtual bool SupportsImageInput => false;

    /// <summary>
    /// 功能：默认声明通用 SSE route 不产生图片输出。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public virtual bool SupportsImageOutput => false;

    /// <summary>
    /// 功能：执行有总时限的 HTTP attempts，并把 retry 决策作为规范化信号返回 Agent。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">语言中立消息、模型与工具声明。</param>
    /// <param name="cancellationToken">本地 run 取消信号。</param>
    /// <returns>文本、工具、用量和 retry.scheduled 来源信号。</returns>
    public async IAsyncEnumerable<ProviderSignal> StreamAsync(
        ProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (request.Selection.Id != Id ||
            !SupportsModel(request.Selection.ModelId) ||
            !string.Equals(ApiFamily, request.Selection.ApiFamily, StringComparison.Ordinal) ||
            !IsAvailable())
        {
            throw CreateFailure("invalid_params", retryable: false);
        }

        using var totalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalCancellation.CancelAfter(options.TotalTimeout);
        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            HttpResponseMessage? response = null;
            ProviderOperationException? sendFailure = null;
            try
            {
                response = await SendAsync(request, cancellationToken, totalCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ProviderOperationException exception)
            {
                sendFailure = exception;
            }

            if (sendFailure is not null)
            {
                if (sendFailure.Error.Retryable && attempt < options.MaxAttempts)
                {
                    var delay = BoundRetryDelay(null);
                    yield return new ProviderRetrySignal(attempt + 1, (long)delay.TotalMilliseconds, sendFailure.Error);
                    await DelayAsync(delay, cancellationToken, totalCancellation.Token).ConfigureAwait(false);
                    continue;
                }

                throw sendFailure;
            }

            var receivedResponse = response ?? throw CreateFailure("provider_connection_failed", retryable: true);
            using (receivedResponse)
            {
                if (!receivedResponse.IsSuccessStatusCode)
                {
                    var statusFailure = MapHttpStatus(receivedResponse.StatusCode);
                    if (statusFailure.Error.Retryable)
                    {
                        var delay = BoundRetryDelay(receivedResponse.Headers.RetryAfter);
                        var detailedError = statusFailure.Error with
                        {
                            Details = statusFailure.Error.Details with
                            {
                                RetryAfterMs = receivedResponse.Headers.RetryAfter is null
                                    ? null
                                    : (long)delay.TotalMilliseconds,
                            },
                        };
                        statusFailure = new ProviderOperationException(detailedError);
                        if (attempt < options.MaxAttempts)
                        {
                            yield return new ProviderRetrySignal(
                                attempt + 1,
                                (long)delay.TotalMilliseconds,
                                detailedError);
                            await DelayAsync(delay, cancellationToken, totalCancellation.Token).ConfigureAwait(false);
                            continue;
                        }
                    }

                    throw statusFailure;
                }

                await foreach (var signal in ParseResponseAsync(
                                   receivedResponse,
                                   cancellationToken,
                                   totalCancellation.Token).ConfigureAwait(false))
                {
                    yield return signal;
                }

                yield break;
            }
        }
    }

    /// <summary>
    /// 功能：释放 HttpClient、连接池和 handler，不输出 endpoint 或认证信息。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 功能：由 family adapter 构造只含公开消息、模型和工具声明的 JSON request body。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">语言中立 Provider 请求。</param>
    /// <returns>顶层 JSON object。</returns>
    protected abstract JsonElement CreateRequestBody(ProviderRequest request);

    /// <summary>
    /// 功能：为本轮请求返回同源 endpoint；默认使用构造时的精确 endpoint。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">已经通过 Provider ID 与模型校验的公共请求。</param>
    /// <returns>与构造 endpoint 同 scheme、host、port 且路径不逃逸其前缀的绝对 URI。</returns>
    /// <remarks>不变量：派生 family 只能追加原生 path/query，不能改变凭据发送 origin。</remarks>
    /// <exception cref="ProviderOperationException">派生 family 无法安全构造请求目标。</exception>
    protected virtual Uri CreateRequestEndpoint(ProviderRequest request)
    {
        return endpoint;
    }

    /// <summary>
    /// 功能：在请求体完成序列化后为特定 API family 补充非通用 header 或原生签名。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="message">尚未发送、已注入通用认证与 Content-Type 的请求。</param>
    /// <param name="body">将与请求一致发送的精确 UTF-8 JSON 字节。</param>
    /// <param name="request">已通过 Provider ID 和模型校验的公共请求。</param>
    /// <remarks>不变量：实现不得更改 request-target、请求体或把凭据写入任何可持久化对象。</remarks>
    /// <exception cref="ProviderOperationException">原生 header 或签名无法安全构造。</exception>
    protected virtual void ConfigureRequestHeaders(
        HttpRequestMessage message,
        ReadOnlySpan<byte> body,
        ProviderRequest request)
    {
    }

    /// <summary>
    /// 功能：由 family adapter 消费 SSE 并产生公共 Provider signals。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="response">成功且尚未读取 body 的 HTTP 响应。</param>
    /// <param name="callerCancellation">run 本地取消信号。</param>
    /// <param name="totalCancellation">Provider 总时限信号。</param>
    /// <returns>规范化信号流。</returns>
    protected abstract IAsyncEnumerable<ProviderSignal> ParseResponseAsync(
        HttpResponseMessage response,
        CancellationToken callerCancellation,
        CancellationToken totalCancellation);

    /// <summary>
    /// 功能：以 idle timeout 读取 ResponseHeadersRead 内容并增量解码 SSE 事件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="response">成功响应。</param>
    /// <param name="callerCancellation">run 本地取消。</param>
    /// <param name="totalCancellation">总时限取消。</param>
    /// <returns>按 wire 顺序完成的 SSE 事件。</returns>
    /// <exception cref="ProviderOperationException">idle、总超时、断流或无效 UTF-8。</exception>
    protected async IAsyncEnumerable<SseEvent> ReadSseAsync(
        HttpResponseMessage response,
        CancellationToken callerCancellation,
        [EnumeratorCancellation] CancellationToken totalCancellation)
    {
        Stream stream;
        try
        {
            stream = await response.Content.ReadAsStreamAsync(totalCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested)
        {
            throw CreateFailure("provider_total_timeout", retryable: false);
        }

        await using (stream.ConfigureAwait(false))
        {
            var decoder = new SseDecoder(options.MaxSseEventBytes);
            var buffer = new byte[8192];
            while (true)
            {
                int read;
                using var idleCancellation = CancellationTokenSource.CreateLinkedTokenSource(totalCancellation);
                idleCancellation.CancelAfter(options.IdleTimeout);
                try
                {
                    read = await stream.ReadAsync(buffer, idleCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (totalCancellation.IsCancellationRequested)
                {
                    throw CreateFailure("provider_total_timeout", retryable: false);
                }
                catch (OperationCanceledException)
                {
                    throw CreateFailure("provider_idle_timeout", retryable: false);
                }
                catch (IOException)
                {
                    throw CreateFailure("provider_disconnected", retryable: true, terminalStatus: "interrupted");
                }

                if (read == 0)
                {
                    IReadOnlyList<SseEvent> finalEvents;
                    try
                    {
                        finalEvents = decoder.Finish();
                    }
                    catch (InvalidDataException)
                    {
                        throw CreateFailure("provider_disconnected", retryable: true, terminalStatus: "interrupted");
                    }

                    foreach (var item in finalEvents)
                    {
                        yield return item;
                    }

                    yield break;
                }

                IReadOnlyList<SseEvent> events;
                try
                {
                    events = decoder.Feed(buffer.AsSpan(0, read));
                }
                catch (InvalidDataException)
                {
                    throw CreateFailure("provider_protocol_error", retryable: false);
                }

                foreach (var item in events)
                {
                    yield return item;
                }
            }
        }
    }

    /// <summary>
    /// 功能：创建 family 专属安全错误，不包含 endpoint、query、header、key 或 raw body。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">稳定 details.kind。</param>
    /// <param name="retryable">是否允许 attempt 级重试。</param>
    /// <param name="terminalStatus">failed 或 interrupted。</param>
    /// <returns>可安全持久化的 ProviderOperationException。</returns>
    protected ProviderOperationException CreateFailure(
        string kind,
        bool retryable,
        string terminalStatus = "failed")
    {
        return new ProviderOperationException(
            new PortableError(
                -32005,
                "provider request failed",
                retryable,
                new ErrorDetails(kind, ProviderId: Id)),
            terminalStatus);
    }

    /// <summary>
    /// 功能：构造请求、在最终边界注入 credential，并仅等待 response headers。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">语言中立 Provider 请求。</param>
    /// <param name="callerCancellation">run 本地取消。</param>
    /// <param name="totalCancellation">总时限取消。</param>
    /// <returns>调用方负责释放的 HttpResponseMessage。</returns>
    private async Task<HttpResponseMessage> SendAsync(
        ProviderRequest request,
        CancellationToken callerCancellation,
        CancellationToken totalCancellation)
    {
        var requestEndpoint = CreateRequestEndpoint(request);
        ValidateRequestEndpoint(requestEndpoint);
        using var message = new HttpRequestMessage(HttpMethod.Post, requestEndpoint);
        var body = JsonSerializer.SerializeToUtf8Bytes(CreateRequestBody(request));
        message.Content = new ByteArrayContent(body);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Json)
        {
            CharSet = Encoding.UTF8.WebName,
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(AcceptMediaType));
        foreach (var pair in additionalHeaders)
        {
            message.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }

        if (!credentialSource.TryReadForRequest(out var credential))
        {
            throw CreateFailure("provider_unavailable", retryable: false);
        }

        if (credential is not null)
        {
            message.Headers.TryAddWithoutValidation(
                authenticationHeader,
                authenticationPrefix + credential);
        }

        ConfigureRequestHeaders(message, body, request);

        using var headersCancellation = CancellationTokenSource.CreateLinkedTokenSource(totalCancellation);
        headersCancellation.CancelAfter(options.HeadersTimeout);
        try
        {
            return await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                headersCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (totalCancellation.IsCancellationRequested)
        {
            throw CreateFailure("provider_total_timeout", retryable: false);
        }
        catch (OperationCanceledException)
        {
            throw CreateFailure("provider_headers_timeout", retryable: true);
        }
        catch (HttpRequestException)
        {
            throw CreateFailure("provider_connection_failed", retryable: true);
        }
    }

    /// <summary>
    /// 功能：在认证注入前复核派生 family 的动态 request-target 仍位于构造 endpoint 的同一 origin 与路径前缀。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="requestEndpoint">派生 family 为本轮生成的绝对 URI。</param>
    /// <remarks>不变量：scheme、IDN host、port、userinfo 与 base path 边界不可由模型或版本改变。</remarks>
    /// <exception cref="ProviderOperationException">目标非绝对、跨 origin、含 user-info/fragment 或路径逃逸。</exception>
    private void ValidateRequestEndpoint(Uri requestEndpoint)
    {
        if (!requestEndpoint.IsAbsoluteUri)
        {
            throw CreateFailure("provider_request_target_invalid", retryable: false);
        }

        var basePath = endpoint.AbsolutePath.TrimEnd('/');
        var pathMatches = requestEndpoint.AbsolutePath.Equals(endpoint.AbsolutePath, StringComparison.Ordinal) ||
            requestEndpoint.AbsolutePath.StartsWith(basePath + "/", StringComparison.Ordinal);
        if (!requestEndpoint.Scheme.Equals(endpoint.Scheme, StringComparison.Ordinal) ||
            !requestEndpoint.IdnHost.Equals(endpoint.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            requestEndpoint.Port != endpoint.Port ||
            !string.IsNullOrEmpty(requestEndpoint.UserInfo) ||
            !string.IsNullOrEmpty(requestEndpoint.Fragment) ||
            !pathMatches)
        {
            throw CreateFailure("provider_request_target_invalid", retryable: false);
        }
    }

    /// <summary>
    /// 功能：把 HTTP 状态映射为安全结构化错误和规范重试类别，完全忽略 raw body。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="statusCode">响应状态。</param>
    /// <returns>无敏感内容的 ProviderOperationException。</returns>
    private ProviderOperationException MapHttpStatus(HttpStatusCode statusCode)
    {
        var number = (int)statusCode;
        var retryable = number is 408 or 429 or 500 or 502 or 503 or 504;
        var kind = number == 429 ? "provider_rate_limited" :
            retryable ? "provider_unavailable" : "provider_http_error";
        return new ProviderOperationException(new PortableError(
            -32005,
            "provider request failed",
            retryable,
            new ErrorDetails(kind, ProviderId: Id, HttpStatus: number)));
    }

    /// <summary>
    /// 功能：解析 seconds 或 HTTP-date Retry-After 并应用非负最大延迟。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="retryAfter">可选标准 Retry-After header。</param>
    /// <returns>零到 RetryMaxDelay 的延迟。</returns>
    private TimeSpan BoundRetryDelay(RetryConditionHeaderValue? retryAfter)
    {
        var delay = retryAfter?.Delta;
        if (delay is null && retryAfter?.Date is not null)
        {
            delay = retryAfter.Date.Value - DateTimeOffset.UtcNow;
        }

        delay ??= TimeSpan.FromMilliseconds(10);
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        return delay > options.RetryMaxDelay ? options.RetryMaxDelay : delay.Value;
    }

    /// <summary>
    /// 功能：执行受 run 取消与 Provider 总时限共同约束的 retry delay。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="delay">已完成上限处理的延迟。</param>
    /// <param name="callerCancellation">run 本地取消。</param>
    /// <param name="totalCancellation">总时限取消。</param>
    /// <returns>延迟完成的 Task。</returns>
    private async Task DelayAsync(
        TimeSpan delay,
        CancellationToken callerCancellation,
        CancellationToken totalCancellation)
    {
        try
        {
            await Task.Delay(delay, totalCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw CreateFailure("provider_total_timeout", retryable: false);
        }
    }
}

/// <summary>
/// 功能：提供 Provider JSON 的严格对象解析、重复键拒绝和安全字段读取。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal static class ProviderJson
{
    /// <summary>
    /// 功能：严格解析 Provider SSE data 为独立 JSON object，错误不包含 raw data。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">完整 SSE data。</param>
    /// <returns>独立 JsonElement object。</returns>
    /// <exception cref="ProviderOperationException">JSON 无效、重复键或顶层非对象。</exception>
    internal static JsonElement ParseObject(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128,
            });
            ValidateNoDuplicateKeys(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException();
            }

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ProviderOperationException(
                new PortableError(
                    -32005,
                    "provider stream contained invalid JSON",
                    false,
                    new ErrorDetails("provider_protocol_error")),
                "failed");
        }
    }

    /// <summary>
    /// 功能：递归拒绝 Provider JSON 对象重复键。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">待检查 JSON。</param>
    /// <exception cref="JsonException">发现重复键。</exception>
    private static void ValidateNoDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException();
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
    /// 功能：把 partial tool arguments 严格解析为有界 JSON object。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="arguments">已按上游顺序完整拼接的参数文本。</param>
    /// <returns>独立 JSON object。</returns>
    internal static JsonElement ParseToolArguments(StringBuilder arguments)
    {
        if (arguments.Length > 1_048_576)
        {
            throw new ProviderOperationException(new PortableError(
                -32005,
                "provider tool arguments exceeded the limit",
                false,
                new ErrorDetails("provider_output_limit")));
        }

        return ParseObject(arguments.ToString());
    }

    /// <summary>
    /// 功能：从 portable content 数组拼接公开 text 块，忽略 reasoning 等非文本类型。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="message">portable message object。</param>
    /// <returns>按 content 顺序拼接的文本。</returns>
    internal static string ExtractText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object &&
                block.TryGetProperty("type", out var type) &&
                type.GetString() == "text" &&
                block.TryGetProperty("text", out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                text.Append(value.GetString());
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// 功能：读取 JSON object 中必需字符串，缺失时返回安全协议错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">字段名。</param>
    /// <returns>字符串值。</returns>
    internal static string RequireString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(property.GetString()))
        {
            throw new ProviderOperationException(new PortableError(
                -32005,
                "provider stream omitted a required field",
                false,
                new ErrorDetails("provider_protocol_error")));
        }

        return property.GetString()!;
    }
}
