using System.Text.Json;
using System.Text.Json.Serialization;

namespace QxnmForge.Domain;

/// <summary>
/// 功能：定义可进入用户或 assistant 消息的品牌中立内容块公共字段。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public abstract record MessageContent
{
    /// <summary>
    /// 功能：初始化具有固定 wire 类型及其受限可选字段的内容块。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="type">公共 content discriminator。</param>
    /// <param name="text">仅 text 块使用的正文。</param>
    /// <param name="artifact">仅引用块使用的 artifact 元数据。</param>
    /// <param name="alt">仅 image_ref 使用的可选替代文字。</param>
    /// <param name="extensions">显式扩展命名空间的独立 JSON 值。</param>
    protected MessageContent(
        string type,
        string? text,
        ArtifactReference? artifact,
        string? alt,
        JsonElement? extensions)
    {
        Type = type;
        Text = text;
        Artifact = artifact;
        Alt = alt;
        Extensions = extensions?.Clone();
    }

    /// <summary>
    /// 功能：取得公共 content discriminator。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// 功能：取得 text 块正文；其他类型为 null。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string? Text { get; }

    /// <summary>
    /// 功能：取得 image_ref 或 artifact_ref 的 artifact 引用；非引用块为 null。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public ArtifactReference? Artifact { get; }

    /// <summary>
    /// 功能：取得 image_ref 可选替代文字。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string? Alt { get; }

    /// <summary>
    /// 功能：取得显式 extensions 命名空间的独立 JSON 克隆。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public JsonElement? Extensions { get; }
}

/// <summary>
/// 功能：表示规范化文本内容块。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed record TextContent : MessageContent
{
    /// <summary>
    /// 功能：创建固定 wire 类型为 text 的文本块并保留显式扩展命名空间。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="text">文本正文。</param>
    /// <param name="extensions">可选扩展对象。</param>
    public TextContent(string text, JsonElement? extensions = null)
        : base("text", text, null, null, extensions)
    {
    }
}

/// <summary>
/// 功能：表示不内联图像字节或主机路径的 portable image_ref 内容块。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed record ImageReferenceContent : MessageContent
{
    /// <summary>
    /// 功能：创建仅携带强绑定 artifact 元数据的 image_ref。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="artifact">同一 Session 内的 durable artifact 引用。</param>
    /// <param name="alt">不超过公共限制的可选替代文字。</param>
    /// <param name="extensions">可选扩展对象。</param>
    /// <remarks>不变量：本对象从不包含 base64、data URL、remote URL 或 host path。</remarks>
    public ImageReferenceContent(
        ArtifactReference artifact,
        string? alt = null,
        JsonElement? extensions = null)
        : base("image_ref", null, artifact, alt, extensions)
    {
    }
}

/// <summary>
/// 功能：表示不内联字节或主机路径的 portable artifact_ref 内容块。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed record ArtifactReferenceContent : MessageContent
{
    /// <summary>
    /// 功能：创建只携带 durable artifact 元数据的 artifact_ref。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="artifact">同一 Session 内已经 durable 的 artifact 引用。</param>
    /// <param name="extensions">可选扩展对象。</param>
    /// <remarks>不变量：本对象从不包含 base64、URL 或 host path。</remarks>
    public ArtifactReferenceContent(ArtifactReference artifact, JsonElement? extensions = null)
        : base("artifact_ref", null, artifact, null, extensions)
    {
    }
}

/// <summary>
/// 功能：表示 portable artifact 的内容寻址元数据，不携带本地路径或字节。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ArtifactId">Session 内 opaque artifact ID。</param>
/// <param name="MediaType">规范 MIME 类型。</param>
/// <param name="ByteLength">精确非负安全整数字节长度。</param>
/// <param name="Sha256">小写十六进制 SHA-256。</param>
/// <param name="DisplayName">可选公开显示名。</param>
/// <param name="Extensions">可选显式扩展命名空间。</param>
public sealed record ArtifactReference(
    string ArtifactId,
    string MediaType,
    long ByteLength,
    string Sha256,
    string? DisplayName = null,
    JsonElement? Extensions = null);

/// <summary>
/// 功能：表示客户端提交的用户输入消息。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Role">固定为 user 的角色。</param>
/// <param name="Content">至少一个 text 或 image_ref 输入内容块。</param>
public sealed record InputMessage(string Role, IReadOnlyList<MessageContent> Content);

/// <summary>
/// 功能：表示写入 portable journal 的完整用户消息。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="MessageId">会话内唯一消息 ID。</param>
/// <param name="Role">固定为 user。</param>
/// <param name="Content">输入内容。</param>
/// <param name="Time">UTC 创建时间。</param>
public sealed record UserMessage(
    string MessageId,
    string Role,
    IReadOnlyList<MessageContent> Content,
    DateTimeOffset Time);

/// <summary>
/// 功能：表示 Provider 和模型的语言中立选择。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Id">Provider ID。</param>
/// <param name="ModelId">模型 ID。</param>
/// <param name="ApiFamily">可选显式 API family；图像路由必须提供。</param>
public sealed record ProviderSelection(string Id, string ModelId, string? ApiFamily = null);

/// <summary>
/// 功能：表示 <c>models/list</c> 中经过运行时策略缩小的公共模型能力。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Input">保持 catalog 顺序的可用输入媒介。</param>
/// <param name="Output">保持 catalog 顺序的可用输出媒介。</param>
/// <param name="Streaming">当前原生 route 是否允许流式输出。</param>
/// <param name="Tools">当前原生 route 是否允许工具调用。</param>
/// <param name="Reasoning">catalog 证据与 route 策略是否同时允许推理。</param>
/// <param name="ContextTokens">可选上下文 token 上限。</param>
/// <param name="MaxOutputTokens">可选最大输出 token 上限。</param>
public sealed record ModelCapabilities(
    IReadOnlyList<string> Input,
    IReadOnlyList<string> Output,
    bool Streaming,
    bool Tools,
    bool Reasoning,
    int? ContextTokens = null,
    int? MaxOutputTokens = null);

/// <summary>
/// 功能：表示以 Provider、模型和 API family 三元组唯一标识的公共模型描述。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ProviderId">canonical Provider 身份。</param>
/// <param name="ModelId">Provider 内 opaque 模型身份。</param>
/// <param name="DisplayName">冻结 catalog 的公开显示名。</param>
/// <param name="ApiFamily">选择该 route 所需的 API family。</param>
/// <param name="Capabilities">只会缩小 catalog 证据的运行时能力。</param>
public sealed record ModelDescriptor(
    string ProviderId,
    string ModelId,
    string DisplayName,
    string ApiFamily,
    ModelCapabilities Capabilities);

/// <summary>
/// 功能：表示跨 Provider 归一化 token 用量。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="InputTokens">输入 token 数。</param>
/// <param name="OutputTokens">输出 token 数。</param>
/// <param name="TotalTokens">总 token 数。</param>
public sealed record Usage(long InputTokens, long OutputTokens, long TotalTokens)
{
    /// <summary>
    /// 功能：返回未报告 token 时使用的零值用量。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public static Usage Zero { get; } = new(0, 0, 0);
}

/// <summary>
/// 功能：表示写入 portable journal 的完整 assistant 消息。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="MessageId">会话内唯一消息 ID。</param>
/// <param name="Role">固定为 assistant。</param>
/// <param name="Content">完成后的规范化内容。</param>
/// <param name="Provider">本轮 Provider 选择。</param>
/// <param name="FinishReason">规范化结束原因。</param>
/// <param name="Usage">规范化 token 用量。</param>
/// <param name="Time">UTC 完成时间。</param>
public sealed record AssistantMessage(
    string MessageId,
    string Role,
    IReadOnlyList<MessageContent> Content,
    ProviderSelection Provider,
    string FinishReason,
    Usage Usage,
    DateTimeOffset Time);

/// <summary>
/// 功能：表示可移植错误 details 的受限核心字段。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Kind">稳定的机器可读错误类别。</param>
/// <param name="Field">可选的错误字段。</param>
/// <param name="ProviderId">可选 Provider ID。</param>
/// <param name="ModelId">可选模型 ID。</param>
/// <param name="SupportedVersions">可选支持协议版本。</param>
/// <param name="HttpStatus">可选、允许持久化的 HTTP 状态码。</param>
/// <param name="RetryAfterMs">可选、已应用安全上限的 Retry-After 毫秒数。</param>
/// <param name="ToolName">可选规范工具名。</param>
/// <param name="Limit">可选资源上限。</param>
/// <param name="Observed">可选实际资源计数。</param>
/// <param name="ExitCode">可选进程退出码。</param>
/// <param name="ApiFamily">可选 API family。</param>
/// <param name="ExpectedRevision">可选调用方 CAS revision。</param>
/// <param name="CurrentRevision">可选服务端当前 revision。</param>
/// <param name="ResourceId">可选品牌中立资源 ID。</param>
public sealed record ErrorDetails(
    string Kind,
    string? Field = null,
    string? ProviderId = null,
    string? ModelId = null,
    IReadOnlyList<string>? SupportedVersions = null,
    int? HttpStatus = null,
    long? RetryAfterMs = null,
    string? ToolName = null,
    long? Limit = null,
    long? Observed = null,
    int? ExitCode = null,
    string? ApiFamily = null,
    long? ExpectedRevision = null,
    long? CurrentRevision = null,
    string? ResourceId = null);

/// <summary>
/// 功能：表示不依赖异常文本解析的协议和 Agent 结构化错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Code">稳定错误码。</param>
/// <param name="Message">已脱敏、可显示消息。</param>
/// <param name="Retryable">客户端是否可重试。</param>
/// <param name="Details">受限机器可读详情。</param>
public sealed record PortableError(int Code, string Message, bool Retryable, ErrorDetails Details);

/// <summary>
/// 功能：表示确定性 faux Provider 场景。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="SchemaVersion">场景 schema 版本。</param>
/// <param name="Name">确定性场景名称。</param>
/// <param name="Seed">场景随机种子。</param>
/// <param name="Steps">有序 Provider 步骤。</param>
/// <param name="Usage">可选的最终用量。</param>
/// <param name="ExpectedContext">可选 faux 黑盒断言使用的规范化 Provider 消息上下文。</param>
/// <param name="Continuations">工具结果后按 Provider turn FIFO 消费的确定性续段。</param>
public sealed record FauxScenario(
    string SchemaVersion,
    string Name,
    uint Seed,
    IReadOnlyList<FauxStep> Steps,
    Usage? Usage = null,
    IReadOnlyList<JsonElement>? ExpectedContext = null,
    IReadOnlyList<FauxContinuation>? Continuations = null);

/// <summary>
/// 功能：表示 faux 首轮工具结果之后的一个确定性 Provider turn。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Steps">本轮有序 faux 步骤。</param>
/// <param name="ExpectedContext">可选完整 Provider 上下文断言。</param>
/// <param name="Usage">可选本轮用量。</param>
public sealed record FauxContinuation(
    IReadOnlyList<FauxStep> Steps,
    IReadOnlyList<JsonElement>? ExpectedContext = null,
    Usage? Usage = null);

/// <summary>
/// 功能：定义 faux 场景步骤的封闭 wire 基类。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FauxTextStep), "text")]
[JsonDerivedType(typeof(FauxToolCallStep), "tool_call")]
[JsonDerivedType(typeof(FauxErrorStep), "error")]
[JsonDerivedType(typeof(FauxDelayStep), "delay")]
[JsonDerivedType(typeof(FauxDisconnectStep), "disconnect")]
public abstract record FauxStep;

/// <summary>
/// 功能：表示 faux 文本增量步骤。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Text">要按顺序输出的非空文本。</param>
public sealed record FauxTextStep(string Text) : FauxStep;

/// <summary>
/// 功能：表示 faux 工具调用步骤。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ToolCallId">场景指定工具调用 ID。</param>
/// <param name="Name">规范化工具名。</param>
/// <param name="Arguments">完整工具参数对象。</param>
public sealed record FauxToolCallStep(string ToolCallId, string Name, JsonElement Arguments) : FauxStep;

/// <summary>
/// 功能：表示 faux 结构化失败步骤。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Error">要抛给 Agent 的可移植错误。</param>
public sealed record FauxErrorStep(PortableError Error) : FauxStep;

/// <summary>
/// 功能：表示支持取消的 faux 延迟步骤。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="DurationMs">0 到 60000 毫秒延迟。</param>
public sealed record FauxDelayStep(int DurationMs) : FauxStep;

/// <summary>
/// 功能：表示 Provider 流确定性断开步骤。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Reason">可选的已脱敏断开说明。</param>
public sealed record FauxDisconnectStep(string? Reason = null) : FauxStep;

/// <summary>
/// 功能：表示已持久化、可发送的规范化 Agent 事件。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="SessionId">会话 ID。</param>
/// <param name="RunId">运行 ID。</param>
/// <param name="TurnId">仅 turn/message/tool 事件使用的 turn ID。</param>
/// <param name="Seq">会话内严格递增事件序号。</param>
/// <param name="Time">UTC 事件时间。</param>
/// <param name="Type">规范事件类型。</param>
/// <param name="Data">符合对应事件 schema 的数据。</param>
public sealed record AgentEvent(
    string SessionId,
    string RunId,
    string? TurnId,
    long Seq,
    DateTimeOffset Time,
    string Type,
    object Data);
