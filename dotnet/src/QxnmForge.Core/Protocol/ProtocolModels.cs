using System.Text.Json;
using System.Text.Json.Serialization;
using QxnmForge.Domain;
using QxnmForge.Executor;

namespace QxnmForge.Protocol;

/// <summary>
/// 功能：表示验证后的 JSON-RPC 2.0 请求。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Id">保持字符串或整数类型的 opaque request ID。</param>
/// <param name="Method">语言中立方法名。</param>
/// <param name="Params">独立于输入文档生命周期的 params 克隆。</param>
public sealed record JsonRpcRequest(JsonElement Id, string Method, JsonElement Params);

/// <summary>
/// 功能：表示 JSON-RPC 成功响应 DTO。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Jsonrpc">固定 2.0。</param>
/// <param name="Id">原样关联的 request ID。</param>
/// <param name="Result">方法专属结果。</param>
public sealed record JsonRpcSuccessResponse<T>(string Jsonrpc, JsonElement Id, T Result);

/// <summary>
/// 功能：表示 JSON-RPC 结构化错误响应 DTO。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Jsonrpc">固定 2.0。</param>
/// <param name="Id">可解析时关联 request ID，否则 null。</param>
/// <param name="Error">语言中立 portable error。</param>
public sealed record JsonRpcErrorResponse(
    string Jsonrpc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] object? Id,
    PortableError Error);

/// <summary>
/// 功能：表示唯一 event 方法的 JSON-RPC notification。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Jsonrpc">固定 2.0。</param>
/// <param name="Method">固定 event。</param>
/// <param name="Params">已 durable 的事件 envelope。</param>
public sealed record JsonRpcEventNotification(string Jsonrpc, string Method, AgentEvent Params);

/// <summary>
/// 功能：表示 daemon 实现身份，wire 标识与产品可改名名称分离。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Name">实现包名称。</param>
/// <param name="Version">实现版本。</param>
/// <param name="Language">固定 dotnet。</param>
public sealed record ImplementationInfo(string Name, string Version, string Language);

/// <summary>
/// 功能：表示单个 Provider 能力清单条目。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Id">Provider family/profile ID。</param>
/// <param name="Models">静态模型 ID；空数组表示动态配置。</param>
public sealed record ProviderCapability(string Id, IReadOnlyList<string> Models);

/// <summary>
/// 功能：表示 daemon 当前公开的实际能力集合。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Methods">支持方法。</param>
/// <param name="EventTypes">可能产生的事件类型。</param>
/// <param name="Providers">Provider profiles。</param>
/// <param name="Tools">工具 registry 名称。</param>
/// <param name="Transports">当前 transport。</param>
/// <param name="HardSandbox">仅 startup self-test 成功时出现的 OS 隔离 descriptor。</param>
public sealed record ServerCapabilities(
    IReadOnlyList<string> Methods,
    IReadOnlyList<string> EventTypes,
    IReadOnlyList<ProviderCapability> Providers,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Transports,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HardSandboxCapability? HardSandbox = null);

/// <summary>
/// 功能：表示协商后的协议资源上限。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="MaxFrameBytes">单帧 UTF-8 最大字节数。</param>
/// <param name="MaxEventBytes">事件最大字节数。</param>
/// <param name="MaxArtifactBytes">单 artifact 最大字节数。</param>
/// <param name="MaxConcurrentRuns">当前 daemon 并发 run 上限。</param>
public sealed record ProtocolLimits(
    int MaxFrameBytes,
    int MaxEventBytes,
    long MaxArtifactBytes,
    int MaxConcurrentRuns);

/// <summary>
/// 功能：表示 initialize 成功协商结果。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ProtocolVersion">选中的客户端已提供版本。</param>
/// <param name="Implementation">实现身份。</param>
/// <param name="Capabilities">实际能力。</param>
/// <param name="Limits">协议资源上限。</param>
public sealed record InitializeResult(
    string ProtocolVersion,
    ImplementationInfo Implementation,
    ServerCapabilities Capabilities,
    ProtocolLimits Limits);

/// <summary>
/// 功能：表示 <c>models/list</c> 从当前启动快照返回的 route-qualified 模型集合。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Models">按 Provider、model、API family 排序的公共描述。</param>
public sealed record ModelsListResult(IReadOnlyList<ModelDescriptor> Models);

/// <summary>
/// 功能：表示 run/start 的异步接受回执。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="RunId">accepted run ID。</param>
public sealed record RunStartResult(string RunId);

/// <summary>
/// 功能：表示 session/get 的 durable 快照与事件增量结果。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="SessionId">会话 ID。</param>
/// <param name="LatestSeq">最新事件序号，不是 journal record seq。</param>
/// <param name="ActiveRunId">真实本进程 active run；无活动任务时为 null。</param>
/// <param name="SelectedHeadRecordId">当前 selected parent chain head；空 Session 时省略。</param>
/// <param name="CompactionRecordId">selected chain 上最新生效 compaction；没有时省略。</param>
/// <param name="Messages">选定分支的完整 portable 消息。</param>
/// <param name="Events">event.seq 大于请求 afterSeq 的 exact durable 事件。</param>
public sealed record SessionGetResult(
    string SessionId,
    long LatestSeq,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ActiveRunId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SelectedHeadRecordId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CompactionRecordId,
    IReadOnlyList<JsonElement> Messages,
    IReadOnlyList<JsonElement> Events);

/// <summary>
/// 功能：表示 session/branch/select 已 durable 完成的 tree selection 身份。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="TargetLeafRecordId">被选择的 earlier target。</param>
/// <param name="SelectionRecordId">新追加 branch.selected record ID。</param>
/// <param name="SelectedHeadRecordId">成功后的 selected head，等于 selection record ID。</param>
public sealed record SessionBranchSelectResult(
    string TargetLeafRecordId,
    string SelectionRecordId,
    string SelectedHeadRecordId);

/// <summary>
/// 功能：表示 session/compact 两条 durable 记录和 summary message 的一致身份。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="SummaryMessageId">canonical assistant summary message ID。</param>
/// <param name="SummaryRecordId">summary message.appended record ID。</param>
/// <param name="CompactionRecordId">context.compacted record ID。</param>
/// <param name="SelectedHeadRecordId">成功后的 selected head，等于 compaction record ID。</param>
public sealed record SessionCompactResult(
    string SummaryMessageId,
    string SummaryRecordId,
    string CompactionRecordId,
    string SelectedHeadRecordId);

/// <summary>
/// 功能：表示 faux/configure 的 durable 回执。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ScenarioId">固定 faux:name。</param>
public sealed record FauxConfigureResult(string ScenarioId);

/// <summary>
/// 功能：表示 run/cancel 的幂等状态。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="CancellationState">requested、alreadyRequested 或 terminal。</param>
public sealed record CancelResult(string CancellationState);

/// <summary>
/// 功能：表示 approval/respond 已 durable 接受的单次回执。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Accepted">固定 true；不表示 run 已终止。</param>
public sealed record ApprovalAcceptedResult(bool Accepted);

/// <summary>
/// 功能：表示 steer/follow-up 已 durable 入队结果。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Accepted">固定 true。</param>
/// <param name="QueueItemId">opaque FIFO item ID。</param>
public sealed record QueueAcceptedResult(bool Accepted, string QueueItemId);
