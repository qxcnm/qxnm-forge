using System.Text.Json;
using QxnmForge.Domain;
using QxnmForge.Tools;

namespace QxnmForge.Agent;

/// <summary>
/// 功能：表示持久化并绑定到精确工具操作 hash 的 portable 审批请求。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ApprovalId">会话内单次使用的 opaque ID。</param>
/// <param name="ToolCallId">对应 Provider 工具调用 ID。</param>
/// <param name="Operation">规范工具名。</param>
/// <param name="Arguments">已验证、规范化且脱敏的参数 object。</param>
/// <param name="OperationHash">参数与资源绑定的 SHA-256。</param>
/// <param name="Risk">medium、high 或 critical。</param>
/// <param name="Reason">仅供展示的安全说明。</param>
/// <param name="Resources">受影响 path/executable 等资源。</param>
/// <param name="Choices">固定 allow_once 与 deny。</param>
/// <param name="ExpiresAt">UTC 失效时间。</param>
public sealed record ApprovalRequest(
    string ApprovalId,
    string ToolCallId,
    string Operation,
    JsonElement Arguments,
    string OperationHash,
    string Risk,
    string Reason,
    IReadOnlyList<ApprovalResource> Resources,
    IReadOnlyList<string> Choices,
    DateTimeOffset ExpiresAt);

/// <summary>
/// 功能：表示客户端或安全生命周期产生的审批选择。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Choice">allow_once 或 deny。</param>
/// <param name="Reason">可选有界说明；不参与权限判断。</param>
public sealed record ApprovalDecision(string Choice, string? Reason = null);

/// <summary>
/// 功能：表示审批的唯一 durable 决议及非客户端可伪造的来源。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Decision">最终选择。</param>
/// <param name="ResolutionSource">client、timeout、cancellation 或 disconnect。</param>
internal sealed record ApprovalResolution(ApprovalDecision Decision, string ResolutionSource);

/// <summary>
/// 功能：区分普通审批决议与 client success frame 是否真正完成交付。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Resolution">唯一 durable 审批决议。</param>
/// <param name="ResponseDelivered">client success frame 已完整 flush 时为 true。</param>
internal sealed record ApprovalWaitOutcome(
    ApprovalResolution Resolution,
    bool ResponseDelivered);

/// <summary>
/// 功能：保存 active run 内一个尚待 Agent 消费的单次审批 waiter。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class PendingApproval
{
    /// <summary>
    /// 功能：创建与 portable request 一一对应的异步决议源。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="request">已 durable 前待注册的审批请求。</param>
    internal PendingApproval(ApprovalRequest request)
    {
        Request = request;
    }

    /// <summary>
    /// 功能：取得 immutable 审批请求。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal ApprovalRequest Request { get; }

    /// <summary>
    /// 功能：取得只允许首次 TrySetResult 生效的异步决议源。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal TaskCompletionSource<ApprovalWaitOutcome> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// 功能：把 durable client approval resolution 与 JSON-RPC success flush 建立显式顺序屏障。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class ApprovalResponseReceipt
{
    private readonly AcceptedRun run;
    private readonly PendingApproval pending;
    private readonly ApprovalResolution resolution;

    /// <summary>
    /// 功能：保存已 durable 但尚不能让 executor 观察的 client resolution。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="run">approval 所属 active run。</param>
    /// <param name="pending">原 approval waiter。</param>
    /// <param name="resolution">client resolution。</param>
    internal ApprovalResponseReceipt(
        AcceptedRun run,
        PendingApproval pending,
        ApprovalResolution resolution)
    {
        this.run = run;
        this.pending = pending;
        this.resolution = resolution;
    }

    /// <summary>
    /// 功能：在 approval/respond success frame flush 后唤醒 Agent，保证 resolved/started 不会抢先。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal void AcknowledgeResponse()
    {
        pending.Completion.TrySetResult(new ApprovalWaitOutcome(resolution, ResponseDelivered: true));
    }

    /// <summary>
    /// 功能：approval 成功响应无法 flush 时以未交付结果唤醒 waiter，禁止执行已批准工具。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <remarks>不变量：不追加第二条审批决议、不发布 cancellation intent，也不把 durable allow 转成执行授权。</remarks>
    internal void AbortResponse()
    {
        pending.Completion.TrySetResult(new ApprovalWaitOutcome(resolution, ResponseDelivered: false));
    }

    /// <summary>
    /// 功能：取得 response delivery failure 后用于等待唯一 durable run 终态的完成信号。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal Task RunCompletion => run.TerminalCompletion.Task;
}

/// <summary>
/// 功能：表示 approval/respond 的 unknown、expired、duplicate 或无 active run 冲突。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ApprovalResponseException : Exception
{
    /// <summary>
    /// 功能：创建不泄露审批参数的结构化控制请求错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="error">portable protocol error。</param>
    public ApprovalResponseException(PortableError error)
        : base(error.Message)
    {
        Error = error;
    }

    /// <summary>
    /// 功能：取得 JSON-RPC 使用的安全 portable error。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public PortableError Error { get; }
}
