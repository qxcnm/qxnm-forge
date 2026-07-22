using System.Text.Json;
using System.Text.Json.Serialization;
using QxnmForge.Domain;

namespace QxnmForge.Tools;

/// <summary>
/// 功能：描述可验证、授权并由当前 .NET runtime 原生执行的内置工具。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Name">稳定小写 dotted 工具名。</param>
/// <param name="Description">提供给模型和审批者的中文功能说明。</param>
/// <param name="InputSchema">受限 JSON Schema 参数定义。</param>
/// <param name="Action">默认权限策略使用的操作类别。</param>
/// <param name="PermissionClass">portable tool definition 权限类。</param>
/// <param name="Idempotent">崩溃恢复语义使用的幂等声明。</param>
/// <param name="MaxOutputBytes">单次工具内联输出硬上限。</param>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement InputSchema,
    ToolAction Action,
    string PermissionClass,
    bool Idempotent,
    int MaxOutputBytes);

/// <summary>
/// 功能：表示审批请求中可显示但不授予额外权限的规范化资源。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Kind">path、executable、process 或 other。</param>
/// <param name="Value">已规范化且不含 credential 的资源标识。</param>
public sealed record ApprovalResource(string Kind, string Value);

/// <summary>
/// 功能：表示工具参数验证和资源预检完成后的不可变执行计划。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Definition">匹配的内置工具定义。</param>
/// <param name="Arguments">独立生命周期的规范化参数对象。</param>
/// <param name="Resources">审批绑定的规范化资源。</param>
/// <param name="OperationHash">工具名、参数和资源的 SHA-256。</param>
/// <param name="ResolvedPath">文件/搜索工具预检后的可选绝对路径。</param>
/// <param name="ResolvedExecutable">进程/外壳工具预检后的可选可执行文件。</param>
/// <param name="WorkspaceRelativePath">文件工具由审批资源冻结的 canonical workspace 相对路径。</param>
/// <param name="ToolCallId">可选 Provider 工具调用 ID；不参与 operation hash，仅匹配执行期 conformance barrier。</param>
public sealed record PreparedToolCall(
    ToolDefinition Definition,
    JsonElement Arguments,
    IReadOnlyList<ApprovalResource> Resources,
    string OperationHash,
    string? ResolvedPath = null,
    string? ResolvedExecutable = null,
    string? WorkspaceRelativePath = null,
    string? ToolCallId = null);

/// <summary>
/// 功能：表示进程单个输出流的 UTF-8 replacement 文本和精确有界字节账。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Encoding">固定为 utf-8-replacement。</param>
/// <param name="Text">由捕获前缀解码的安全显示文本。</param>
/// <param name="CapturedBytes">实际保留并用于 Text 的原始字节数。</param>
/// <param name="TotalBytes">pipe 关闭前实际读取的原始字节数。</param>
/// <param name="OmittedBytes">因限制未保留的原始字节数。</param>
/// <param name="Truncated">是否存在未保留字节。</param>
public sealed record PortableExecutionCapture(
    string Encoding,
    string Text,
    long CapturedBytes,
    long TotalBytes,
    long OmittedBytes,
    bool Truncated);

/// <summary>
/// 功能：表示已成功启动进程的语言中立终态、隔离等级与分离双流结果。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ExitCode">仅 exit 终态的退出码；其他终态为 null。</param>
/// <param name="Signal">仅可可靠识别的 signal 名；未知或非 signal 为 null。</param>
/// <param name="TerminationReason">exit、signal、timeout、cancelled 或 output_limit。</param>
/// <param name="DurationMs">从启动成功到完成清理的单调毫秒数。</param>
/// <param name="Containment">实际建立而非期望的 portable containment 等级。</param>
/// <param name="Stdout">独立 stdout 捕获。</param>
/// <param name="Stderr">独立 stderr 捕获。</param>
public sealed record PortableExecutionResult(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? ExitCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Signal,
    string TerminationReason,
    long DurationMs,
    string Containment,
    PortableExecutionCapture Stdout,
    PortableExecutionCapture Stderr);

/// <summary>
/// 功能：表示可持久化、可进入 Provider 上下文的 portable 工具结果。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Content">至少一个有界文本内容块。</param>
/// <param name="IsError">工具是否未正常完成。</param>
/// <param name="TerminationReason">exit、timeout、cancelled、output_limit、denied、validation_error 或 internal_error。</param>
/// <param name="Error">可选结构化工具错误；不得包含 host 异常、路径外内容或命令输出。</param>
/// <param name="Execution">子进程成功启动后必有的结构化执行结果。</param>
public sealed record PortableToolResult(
    IReadOnlyList<MessageContent> Content,
    bool IsError,
    string? TerminationReason = null,
    PortableError? Error = null,
    PortableExecutionResult? Execution = null);

/// <summary>
/// 功能：携带参数验证、权限预检或工具执行的安全结构化错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ToolOperationException : Exception
{
    /// <summary>
    /// 功能：创建不回显不受信任参数值或 host 异常文本的工具错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="error">可进入 journal 和协议的 portable error。</param>
    /// <param name="terminationReason">规范工具终止原因。</param>
    public ToolOperationException(PortableError error, string terminationReason)
        : base(error.Message)
    {
        Error = error;
        TerminationReason = terminationReason;
    }

    /// <summary>
    /// 功能：取得已脱敏 portable error。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public PortableError Error { get; }

    /// <summary>
    /// 功能：取得 portable tool result 使用的终止原因。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string TerminationReason { get; }
}
