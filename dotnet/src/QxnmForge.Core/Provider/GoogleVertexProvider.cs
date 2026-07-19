namespace QxnmForge.Provider;

/// <summary>
/// 功能：原生实现 Vertex GenerateContent 资源路径、OAuth Bearer 认证与 SSE 归一化。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class GoogleVertexProvider : GoogleGenerativeAiProvider
{
    private readonly Uri baseEndpoint;
    private readonly string project;
    private readonly string location;

    /// <summary>
    /// 功能：创建使用显式 project/location 与最终 Bearer 注入的 Vertex adapter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="baseEndpoint">生产 Vertex origin 或 conformance 回环 base。</param>
    /// <param name="project">Google Cloud project 资源段。</param>
    /// <param name="location">Vertex location 资源段。</param>
    /// <param name="oauthToken">只在 HTTP header 最终边界使用的 OAuth access token。</param>
    /// <param name="options">有界 HTTP/SSE/retry 策略。</param>
    /// <remarks>不变量：project、location 和 model 均是单一 allowlist 路径段，不能改变 origin/query。</remarks>
    /// <exception cref="ArgumentException">资源段、endpoint 或认证值不安全。</exception>
    public GoogleVertexProvider(
        Uri baseEndpoint,
        string project,
        string location,
        string? oauthToken,
        ProviderTransportOptions options)
        : base(
            "google-vertex",
            baseEndpoint,
            oauthToken,
            "Authorization",
            "Bearer ",
            options)
    {
        if (!NativeProviderEndpoint.IsSafeResourceSegment(project) ||
            !NativeProviderEndpoint.IsSafeResourceSegment(location))
        {
            throw new ArgumentException("Vertex resource configuration is invalid");
        }

        this.baseEndpoint = baseEndpoint;
        this.project = project;
        this.location = location;
    }

    /// <summary>
    /// 功能：构造 Vertex projects/locations/publishers/google/models 完整同源目标。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">已校验 Provider ID 与模型的公共请求。</param>
    /// <returns>带唯一 `alt=sse` 查询的 Vertex streamGenerateContent URI。</returns>
    /// <remarks>不变量：所有资源变量都已通过 ASCII 单段 allowlist。</remarks>
    protected override Uri CreateRequestEndpoint(ProviderRequest request)
    {
        if (!SupportsModel(request.Selection.ModelId))
        {
            throw CreateFailure("provider_request_target_invalid", retryable: false);
        }

        return NativeProviderEndpoint.Append(
            baseEndpoint,
            $"/v1/projects/{project}/locations/{location}/publishers/google/models/{request.Selection.ModelId}:streamGenerateContent",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["alt"] = "sse",
            });
    }
}
