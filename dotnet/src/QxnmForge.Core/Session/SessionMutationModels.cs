using System.Text.Json;
using QxnmForge.Domain;

namespace QxnmForge.Session;

/// <summary>
/// 功能：保存一次 durable branch selection 的目标、记录与新 head 身份。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="TargetLeafRecordId">被选择的 earlier target record。</param>
/// <param name="SelectionRecordId">新追加 branch.selected 记录 ID。</param>
/// <param name="SelectedHeadRecordId">成功后的 selected head，等于 selection record ID。</param>
public sealed record BranchSelectionReceipt(
    string TargetLeafRecordId,
    string SelectionRecordId,
    string SelectedHeadRecordId);

/// <summary>
/// 功能：保存 session/compact 已严格解析的语言中立参数。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="ExpectedHeadRecordId">CAS 预期 selected head。</param>
/// <param name="FirstRetainedRecordId">source ancestry 上首个保留 user message record。</param>
/// <param name="SummaryText">单一非空 assistant summary 文本。</param>
/// <param name="Provider">已严格验证并保留 extensions 的 summarizer Provider 选择 JSON。</param>
/// <param name="Usage">已严格验证并保留可选字段的规范化 usage JSON。</param>
/// <param name="TokensBefore">压缩前估算。</param>
/// <param name="TokensAfter">压缩后估算。</param>
/// <param name="Strategy">有界版本化策略标识。</param>
public sealed record SessionCompactionCommand(
    string ExpectedHeadRecordId,
    string FirstRetainedRecordId,
    string SummaryText,
    JsonElement Provider,
    JsonElement Usage,
    long TokensBefore,
    long TokensAfter,
    string Strategy);

/// <summary>
/// 功能：保存 durable compaction pair 的 summary、compaction 与新 head 身份。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="SummaryMessageId">canonical assistant summary message ID。</param>
/// <param name="SummaryRecordId">summary message.appended 记录 ID。</param>
/// <param name="CompactionRecordId">context.compacted 记录 ID。</param>
/// <param name="SelectedHeadRecordId">成功后的 selected head，等于 compaction record ID。</param>
public sealed record SessionCompactionReceipt(
    string SummaryMessageId,
    string SummaryRecordId,
    string CompactionRecordId,
    string SelectedHeadRecordId);

/// <summary>
/// 功能：表示 branch/compaction mutation 的冻结 portable 失败，不要求客户端解析文本。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class SessionMutationException : Exception
{
    /// <summary>
    /// 功能：从 ADR 0011 固定 error triple 创建 mutation 异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="code">冻结 JSON-RPC error code。</param>
    /// <param name="retryable">同一请求状态是否可能通过重试改变。</param>
    /// <param name="kind">冻结 details.kind。</param>
    /// <remarks>不变量：异常 message 固定且不包含 record ID、路径、summary 或 journal 内容。</remarks>
    public SessionMutationException(int code, bool retryable, string kind)
        : base("session mutation was rejected")
    {
        Error = new PortableError(code, "session mutation was rejected", retryable, new ErrorDetails(kind));
    }

    /// <summary>
    /// 功能：取得可直接写入 JSON-RPC 且不含敏感实例值的 portable error。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public PortableError Error { get; }
}
