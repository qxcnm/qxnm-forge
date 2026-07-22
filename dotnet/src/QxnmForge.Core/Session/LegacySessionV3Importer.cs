using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using QxnmForge.Serialization;

namespace QxnmForge.Session;

/// <summary>
/// 功能：保存 第三方 Session v3 一次性导入所需的调用方授权路径与运行模式。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="SourcePath">只读 第三方 Session v3 JSONL 源文件。</param>
/// <param name="Workspace">已存在的规范工作区。</param>
/// <param name="StateDirectory">调用方授权的本地状态根目录。</param>
/// <param name="SessionId">可选的新 Session ID；不得等于源 ID 或已存在。</param>
/// <param name="Conformance">是否启用固定合成夹具的确定性 ID 与字节。</param>
public sealed record LegacySessionV3ImportOptions(
    string SourcePath,
    string Workspace,
    string StateDirectory,
    string? SessionId,
    bool Conformance);

/// <summary>
/// 功能：返回不包含源路径、目标路径或导入内容的安全成功摘要。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Status">报告中的 completed 或 completed_with_warnings。</param>
/// <param name="SessionId">新建的 portable Session ID。</param>
/// <param name="ReportArtifactId">强绑定到 header 的导入报告 artifact ID。</param>
public sealed record LegacySessionV3ImportResult(string Status, string SessionId, string ReportArtifactId);

/// <summary>
/// 功能：表示 第三方 Session v3 源、映射、隐私或原子发布不满足 clean-room 契约。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class LegacySessionV3ImportException : Exception
{
    /// <summary>
    /// 功能：用稳定错误类别和 portable 退出码创建不回显源内容的导入异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">不含实例值、路径或 secret 的稳定错误类别。</param>
    /// <param name="exitCode">CLI portable 退出码。</param>
    public LegacySessionV3ImportException(string kind, int exitCode)
        : base(kind)
    {
        Kind = kind;
        ExitCode = exitCode;
    }

    /// <summary>
    /// 功能：取得可安全写入 stderr 的稳定错误类别。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// 功能：取得 CLI portable 退出码。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public int ExitCode { get; }
}

/// <summary>
/// 功能：独立使用 .NET 解析、映射并原子发布 第三方 Session v3 clean-room 导入结果。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public static partial class LegacySessionV3Importer
{
    private const int MaxSourceBytes = 16_777_216;
    private const int MaxLineBytes = 1_048_576;
    private const int MaxSourceEntries = 100_000;
    private const int MaxJsonDepth = 64;
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private const string ImportNamespace = "org.agentprotocol.pi-v3";
    private const string ReferenceCommit = "3f9aa5d10b35223abf6146f960ff5cb5c68053ee";
    private const string ReportMediaType = "application/vnd.qxnm-forge.pi-v3-import-report+json";
    private const string ConformanceSourceHash = "f31b9b7a784a3af526fec462e5b16c5bffaaedf45b7a8fa101d946bfd3d02b39";
    private const string ConformanceSessionId = "session-import-pi-v3-fixture";
    private const string ConformanceArtifactId = "artifact-pi-v3-import-report";
    private const string ConformanceCreatedAt = "2026-01-02T00:00:00Z";
    private const string ConformanceWorkspace = "[CONFORMANCE_WORKSPACE]";
    private const int UnixOpenReadOnly = 0;
    private const int LinuxOpenCloseOnExec = 0x80000;
    private const int LinuxOpenNoFollow = 0x20000;
    private const int MacOpenCloseOnExec = 0x1000000;
    private const int MacOpenNoFollow = 0x100;

    private static readonly HashSet<string> HeaderFields =
        ["type", "version", "id", "timestamp", "cwd", "parentSession"];

    private static readonly Dictionary<string, HashSet<string>> KnownEntryFields = new(StringComparer.Ordinal)
    {
        ["message"] = ["type", "id", "parentId", "timestamp", "message"],
        ["thinking_level_change"] = ["type", "id", "parentId", "timestamp", "thinkingLevel"],
        ["model_change"] = ["type", "id", "parentId", "timestamp", "provider", "modelId"],
        ["compaction"] = ["type", "id", "parentId", "timestamp", "summary", "firstKeptEntryId", "tokensBefore", "details", "fromHook"],
        ["branch_summary"] = ["type", "id", "parentId", "timestamp", "fromId", "summary", "details", "fromHook"],
        ["custom"] = ["type", "id", "parentId", "timestamp", "customType", "data"],
        ["custom_message"] = ["type", "id", "parentId", "timestamp", "customType", "content", "details", "display"],
        ["label"] = ["type", "id", "parentId", "timestamp", "targetId", "label"],
        ["session_info"] = ["type", "id", "parentId", "timestamp", "name"],
    };

    private static readonly HashSet<string> ForbiddenKinds =
    [
        "event.emitted",
        "run.accepted",
        "run.started",
        "run.cancellation_requested",
        "run.terminal",
        "turn.started",
        "turn.completed",
        "provider.attempt",
        "tool.intent",
        "tool.result",
        "approval.requested",
        "approval.resolved",
        "queue.appended",
        "queue.consumed",
        "faux.configured",
    ];

    /// <summary>
    /// 功能：严格读取 第三方 Session v3、生成 portable journal/report 并以不覆盖方式发布新 Session。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="options">调用方显式选择的源、工作区、状态根、目标 ID 与 conformance 模式。</param>
    /// <param name="cancellationToken">读取、持久化与发布前检查的取消信号。</param>
    /// <returns>目标 durable 发布后才返回的安全成功摘要。</returns>
    /// <remarks>不变量：不执行第三方 runtime、工具或 Provider；不修改源；artifact 先于 journal 写入；目标从不覆盖。</remarks>
    /// <exception cref="LegacySessionV3ImportException">源不兼容、隐私边界失败、目标已存在或持久化失败。</exception>
    public static async Task<LegacySessionV3ImportResult> ImportAsync(
        LegacySessionV3ImportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var workspace = ValidateWorkspace(options.Workspace);
        var stateDirectory = ValidateStateDirectory(options.StateDirectory);
        await using var source = await SourceSnapshot.OpenAsync(options.SourcePath, cancellationToken).ConfigureAwait(false);
        var parsed = ParseSource(source.Bytes);
        var sourceSessionId = RequireBoundedIdentifier(parsed.Header, "id", 256);
        var sessionId = options.SessionId ?? "session-" + Guid.NewGuid().ToString("N");
        ValidateOpaqueId(sessionId, "target_session_invalid");
        if (string.Equals(sessionId, sourceSessionId, StringComparison.Ordinal))
        {
            throw InvalidSource("target_matches_source");
        }

        if (options.Conformance)
        {
            if (!string.Equals(source.Sha256, ConformanceSourceHash, StringComparison.Ordinal) ||
                !string.Equals(sourceSessionId, "pi-v3-fixture-session", StringComparison.Ordinal) ||
                !string.Equals(sessionId, ConformanceSessionId, StringComparison.Ordinal))
            {
                throw InvalidSource("conformance_fixture_mismatch");
            }
        }

        var idFactory = new ImportIdFactory(options.Conformance);
        var artifactId = idFactory.ReportArtifactId;
        var createdAt = options.Conformance
            ? ConformanceCreatedAt
            : DateTimeOffset.UtcNow.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var context = new ImportContext(
            sessionId,
            sourceSessionId,
            source.Sha256,
            source.Bytes.Length,
            options.Conformance ? ConformanceWorkspace : workspace,
            createdAt,
            artifactId,
            idFactory);
        foreach (var entry in parsed.Entries)
        {
            MapEntry(context, entry);
        }

        context.Warnings.Add("source_path_not_persisted");
        var reportBytes = BuildReportBytes(context);
        var reportHash = Convert.ToHexStringLower(SHA256.HashData(reportBytes));
        AppendReportArtifactRecord(context, reportBytes.Length, reportHash);
        var journalBytes = BuildJournalBytes(context);
        ValidateGeneratedJournal(context, journalBytes, reportBytes, reportHash);
        await source.VerifyIdentityAsync(cancellationToken).ConfigureAwait(false);
        await PublishAsync(
            stateDirectory,
            sessionId,
            artifactId,
            reportBytes,
            journalBytes,
            source,
            cancellationToken).ConfigureAwait(false);
        return new LegacySessionV3ImportResult(
            context.Warnings.Count == 0 ? "completed" : "completed_with_warnings",
            sessionId,
            artifactId);
    }

    /// <summary>
    /// 功能：验证工作区存在、不是符号链接叶节点并返回规范绝对路径。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="workspace">调用方选择的工作区。</param>
    /// <returns>规范绝对路径。</returns>
    /// <exception cref="LegacySessionV3ImportException">路径缺失、不是目录或叶节点是链接。</exception>
    private static string ValidateWorkspace(string workspace)
    {
        try
        {
            var path = Path.GetFullPath(workspace);
            var info = new DirectoryInfo(path);
            info.Refresh();
            if (!info.Exists || info.LinkTarget is not null)
            {
                throw InvalidUsage("workspace_invalid");
            }

            return path;
        }
        catch (LegacySessionV3ImportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw InvalidUsage("workspace_invalid");
        }
    }

    /// <summary>
    /// 功能：创建或验证调用方授权的状态根，拒绝符号链接叶节点。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="stateDirectory">显式 --state-dir 值。</param>
    /// <returns>规范绝对路径。</returns>
    /// <exception cref="LegacySessionV3ImportException">状态根不能安全创建或不是普通目录。</exception>
    private static string ValidateStateDirectory(string stateDirectory)
    {
        try
        {
            var path = Path.GetFullPath(stateDirectory);
            Directory.CreateDirectory(path);
            var info = new DirectoryInfo(path);
            info.Refresh();
            if (!info.Exists || info.LinkTarget is not null)
            {
                throw PublishFailure("state_root_invalid");
            }

            return path;
        }
        catch (LegacySessionV3ImportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw PublishFailure("state_root_invalid");
        }
    }

    /// <summary>
    /// 功能：按严格 UTF-8、LF、大小、深度、重复键和 earlier-parent 规则解析完整 第三方 Session v3 源。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="bytes">由持续打开的只读句柄取得的完整源字节。</param>
    /// <returns>克隆后的 header 与逐行 entry 证据。</returns>
    /// <exception cref="LegacySessionV3ImportException">任意 framing、JSON、header、tree 或 known-entry 约束失败。</exception>
    private static ParsedSource ParseSource(byte[] bytes)
    {
        if (bytes.Length is <= 0 or > MaxSourceBytes ||
            bytes.AsSpan().StartsWith("\uFEFF"u8) ||
            bytes[^1] != (byte)'\n' ||
            bytes.AsSpan().Contains((byte)'\r'))
        {
            throw InvalidSource("source_framing_invalid");
        }

        var lines = new List<ReadOnlyMemory<byte>>();
        var start = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n')
            {
                continue;
            }

            var length = index - start;
            if (length is <= 0 or > MaxLineBytes)
            {
                throw InvalidSource("source_line_invalid");
            }

            lines.Add(bytes.AsMemory(start, length));
            start = index + 1;
        }

        if (lines.Count is < 1 or > MaxSourceEntries + 1)
        {
            throw InvalidSource("source_line_count_invalid");
        }

        var values = new List<JsonElement>(lines.Count);
        foreach (var line in lines)
        {
            values.Add(ParseStrictObject(line));
        }

        var header = values[0];
        RequireOnlyFields(header, HeaderFields, "header_fields_invalid");
        if (!string.Equals(RequireString(header, "type"), "session", StringComparison.Ordinal) ||
            RequireSafeInteger(header, "version") != 3)
        {
            throw InvalidSource("header_version_invalid");
        }

        var headerId = RequireBoundedIdentifier(header, "id", 256);
        RejectSensitiveIdentifier(headerId);
        RequireUtc(header, "timestamp");
        RequireStringAllowEmpty(header, "cwd");
        if (header.TryGetProperty("parentSession", out var parentSession) && parentSession.ValueKind != JsonValueKind.String)
        {
            throw InvalidSource("header_parent_invalid");
        }

        var prior = new Dictionary<string, SourceEntry>(StringComparer.Ordinal);
        var entries = new List<SourceEntry>(values.Count - 1);
        for (var index = 1; index < values.Count; index++)
        {
            var value = values[index];
            var lineNumber = index + 1;
            var entryType = RequireBoundedIdentifier(value, "type", 128);
            ValidateSourceType(entryType);
            if (string.Equals(entryType, "session", StringComparison.Ordinal))
            {
                throw InvalidSource("second_header");
            }

            var id = RequireBoundedIdentifier(value, "id", 256);
            RejectSensitiveIdentifier(id);
            if (prior.ContainsKey(id))
            {
                throw InvalidSource("duplicate_entry_id");
            }

            string? parentId;
            if (!value.TryGetProperty("parentId", out var parentNode) || parentNode.ValueKind == JsonValueKind.Null)
            {
                parentId = null;
            }
            else if (parentNode.ValueKind == JsonValueKind.String)
            {
                parentId = parentNode.GetString();
            }
            else
            {
                throw InvalidSource("parent_invalid");
            }

            if ((index == 1 && parentId is not null) ||
                (index > 1 && (parentId is null || !prior.ContainsKey(parentId))))
            {
                throw InvalidSource("parent_not_earlier");
            }

            var timestamp = RequireUtc(value, "timestamp");
            if (KnownEntryFields.TryGetValue(entryType, out var fields))
            {
                RequireOnlyFields(value, fields, "known_entry_fields_invalid");
                ValidateKnownEntry(value, entryType, prior);
            }

            var rawLine = lines[index].ToArray();
            var entry = new SourceEntry(lineNumber, rawLine, value, id, entryType, parentId, timestamp);
            prior.Add(id, entry);
            entries.Add(entry);
        }

        return new ParsedSource(header, entries);
    }

    /// <summary>
    /// 功能：拒绝重复属性后用 System.Text.Json 严格解析单个顶层对象。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="line">不含 LF 的单行 UTF-8 JSON。</param>
    /// <returns>脱离 JsonDocument 生命周期的对象克隆。</returns>
    /// <exception cref="LegacySessionV3ImportException">UTF-8、语法、深度、重复键或顶层类型无效。</exception>
    private static JsonElement ParseStrictObject(ReadOnlyMemory<byte> line)
    {
        try
        {
            ValidateNoDuplicateKeys(line.Span);
            using var document = JsonDocument.Parse(
                line,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaxJsonDepth,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw InvalidSource("json_root_invalid");
            }

            return document.RootElement.Clone();
        }
        catch (LegacySessionV3ImportException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw InvalidSource("json_invalid");
        }
    }

    /// <summary>
    /// 功能：用 UTF-8 token 栈对每层对象执行 ordinal 重复键检测。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="json">单个 JSON 文档原始字节。</param>
    /// <remarks>不变量：数组层使用空哨兵；最多接受公共契约规定的 64 层。</remarks>
    /// <exception cref="LegacySessionV3ImportException">发现同一对象内重复属性。</exception>
    private static void ValidateNoDuplicateKeys(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(
            json,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaxJsonDepth,
            });
        var scopes = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    scopes.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    scopes.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (scopes.Count == 0 || scopes.Peek() is not { } names || !names.Add(reader.GetString()!))
                    {
                        throw InvalidSource("duplicate_json_key");
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// 功能：验证已知 第三方格式 entry 的必要字段、类型、引用和 compaction 祖先边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">已通过 base/tree 验证的 entry。</param>
    /// <param name="entryType">已验证类型。</param>
    /// <param name="prior">只含更早 entry 的索引。</param>
    /// <exception cref="LegacySessionV3ImportException">已知 entry 畸形或引用无效。</exception>
    private static void ValidateKnownEntry(
        JsonElement value,
        string entryType,
        IReadOnlyDictionary<string, SourceEntry> prior)
    {
        switch (entryType)
        {
            case "message":
                var message = RequireObject(value, "message");
                var role = RequireString(message, "role");
                if (role is not ("user" or "assistant" or "toolResult" or "bashExecution" or "custom"))
                {
                    throw InvalidSource("message_role_invalid");
                }

                break;
            case "model_change":
                ValidateProviderId(RequireString(value, "provider"));
                RequireBoundedString(value, "modelId", 256);
                break;
            case "thinking_level_change":
                ValidateThinking(RequireString(value, "thinkingLevel"));
                break;
            case "compaction":
                RequireString(value, "summary");
                var firstKept = RequireString(value, "firstKeptEntryId");
                if (!prior.ContainsKey(firstKept) || !IsAncestor(firstKept, RequireNullableString(value, "parentId"), prior))
                {
                    throw InvalidSource("compaction_boundary_invalid");
                }

                RequireSafeInteger(value, "tokensBefore");
                break;
            case "branch_summary":
                var fromId = RequireString(value, "fromId");
                if (!string.Equals(fromId, "root", StringComparison.Ordinal) && !prior.ContainsKey(fromId))
                {
                    throw InvalidSource("branch_summary_from_invalid");
                }

                RequireString(value, "summary");
                break;
            case "custom":
                RequireString(value, "customType");
                break;
            case "custom_message":
                RequireString(value, "customType");
                if (!value.TryGetProperty("display", out var display) || display.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw InvalidSource("custom_display_invalid");
                }

                break;
            case "label":
                var target = RequireString(value, "targetId");
                if (!prior.ContainsKey(target))
                {
                    throw InvalidSource("label_target_invalid");
                }

                RequireNullableBoundedString(value, "label", 256);
                break;
            case "session_info":
                RequireNullableBoundedString(value, "name", 256);
                break;
        }
    }

    /// <summary>
    /// 功能：确认 compaction retained entry 位于 source parent 的 earlier-only 祖先链。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="wanted">待确认祖先 ID。</param>
    /// <param name="parentId">当前 compaction 源 parent。</param>
    /// <param name="prior">更早 entry 索引。</param>
    /// <returns>祖先链包含 wanted 时为 true。</returns>
    private static bool IsAncestor(
        string wanted,
        string? parentId,
        IReadOnlyDictionary<string, SourceEntry> prior)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (parentId is not null)
        {
            if (!visited.Add(parentId) || !prior.TryGetValue(parentId, out var parent))
            {
                return false;
            }

            if (string.Equals(parentId, wanted, StringComparison.Ordinal))
            {
                return true;
            }

            parentId = parent.ParentId;
        }

        return false;
    }

    /// <summary>
    /// 功能：按源文件顺序映射一条 entry，并在 source parent 跳转时插入可审计 branch.selected。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">当前目标记录、模型状态、映射和报告上下文。</param>
    /// <param name="entry">严格验证后的源 entry。</param>
    /// <exception cref="LegacySessionV3ImportException">父映射缺失或已知内容不能安全转换。</exception>
    private static void MapEntry(ImportContext context, SourceEntry entry)
    {
        string? mappedParent = null;
        if (entry.ParentId is not null)
        {
            if (!context.Mappings.TryGetValue(entry.ParentId, out var parentMapping))
            {
                throw InvalidSource("parent_mapping_missing");
            }

            mappedParent = parentMapping.LastTargetId;
        }

        if (!string.Equals(mappedParent, context.SelectedHead, StringComparison.Ordinal))
        {
            if (mappedParent is null)
            {
                throw InvalidSource("branch_root_invalid");
            }

            var selectionId = context.Ids.BranchSelection(entry.ParentId!);
            var data = new JsonObject { ["leafRecordId"] = mappedParent };
            var extensions = ImportExtension(
                new JsonObject
                {
                    ["reason"] = "source-parent-jump",
                    ["nextSourceEntryId"] = entry.Id,
                });
            AddRecord(context, "branch.selected", selectionId, mappedParent, entry.Timestamp, data, extensions);
        }

        switch (entry.EntryType)
        {
            case "message":
                MapMessage(context, entry);
                break;
            case "model_change":
                MapModelChange(context, entry);
                break;
            case "thinking_level_change":
                MapThinkingChange(context, entry);
                break;
            case "compaction":
                MapCompaction(context, entry);
                break;
            case "branch_summary":
                MapBranchSummary(context, entry);
                break;
            case "custom":
                MapExtensionEntry(context, entry, "preserved_extension", true, ["custom_semantics_excluded"], ["customType", "data"]);
                break;
            case "custom_message":
                MapExtensionEntry(context, entry, "quarantined", true, ["custom_message_context_excluded"], ["customType", "content", "display", "details"]);
                break;
            case "label":
                MapLabel(context, entry);
                break;
            case "session_info":
                MapSessionInfo(context, entry);
                break;
            default:
                MapUnknown(context, entry);
                break;
        }
    }

    /// <summary>
    /// 功能：把 user、assistant 或 toolResult 历史消息映射为 inert portable message；其余角色隔离为 extension。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">导入上下文。</param>
    /// <param name="entry">message 源 entry。</param>
    private static void MapMessage(ImportContext context, SourceEntry entry)
    {
        var sourceMessage = RequireObject(entry.Value, "message");
        var role = RequireString(sourceMessage, "role");
        if (role is "bashExecution" or "custom")
        {
            MapExtensionEntry(context, entry, "quarantined", true, ["unsupported_message_role"], ["message"]);
            return;
        }

        var reasons = new List<string>();
        JsonObject targetMessage;
        if (role == "user")
        {
            var content = MapTextContent(context, RequireProperty(sourceMessage, "content"), reasons, allowReasoning: false, allowToolCalls: false);
            if (content.Count == 0)
            {
                MapExtensionEntry(context, entry, "quarantined", true, ["unsupported_content_block"], ["message"]);
                return;
            }

            targetMessage = new JsonObject
            {
                ["messageId"] = context.Ids.Message(entry.Id),
                ["role"] = "user",
                ["content"] = content,
                ["time"] = entry.Timestamp,
            };
        }
        else if (role == "assistant")
        {
            var provider = RequireString(sourceMessage, "provider");
            ValidateProviderId(provider);
            var model = RequireBoundedString(sourceMessage, "model", 256);
            RejectSensitiveIdentifier(model);
            var content = MapTextContent(context, RequireProperty(sourceMessage, "content"), reasons, allowReasoning: true, allowToolCalls: true);
            var finishReason = MapFinishReason(RequireString(sourceMessage, "stopReason"));
            var usage = MapUsage(sourceMessage);
            targetMessage = new JsonObject
            {
                ["messageId"] = context.Ids.Message(entry.Id),
                ["role"] = "assistant",
                ["content"] = content,
                ["provider"] = ProviderSelection(provider, model),
                ["finishReason"] = finishReason,
                ["usage"] = usage,
                ["time"] = entry.Timestamp,
            };
            context.ProviderId = provider;
            context.ModelId = model;
        }
        else
        {
            var toolCallId = RequireBoundedString(sourceMessage, "toolCallId", 128);
            ValidateOpaqueId(toolCallId, "tool_call_id_invalid");
            var toolName = RequireBoundedString(sourceMessage, "toolName", 128);
            ValidateToolName(toolName);
            var content = MapTextContent(context, RequireProperty(sourceMessage, "content"), reasons, allowReasoning: false, allowToolCalls: false);
            if (content.Count == 0)
            {
                content.Add(new JsonObject { ["type"] = "text", ["text"] = string.Empty });
            }

            targetMessage = new JsonObject
            {
                ["messageId"] = context.Ids.Message(entry.Id),
                ["role"] = "tool",
                ["toolCallId"] = toolCallId,
                ["toolName"] = toolName,
                ["content"] = content,
                ["isError"] = RequireBoolean(sourceMessage, "isError"),
                ["time"] = entry.Timestamp,
            };
        }

        var recordId = context.Ids.Record(entry.Id);
        AddRecord(
            context,
            "message.appended",
            recordId,
            context.SelectedHead,
            entry.Timestamp,
            new JsonObject { ["message"] = targetMessage },
            EntryExtension(entry));
        CompleteMapping(
            context,
            entry,
            [recordId],
            reasons.Count == 0 ? "mapped" : "mapped_with_loss",
            reasons,
            contextExcluded: false,
            quarantinedValue: null);
    }

    /// <summary>
    /// 功能：映射 第三方格式 model_change 并更新随后 compaction 使用的模型状态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">导入上下文。</param>
    /// <param name="entry">model_change entry。</param>
    private static void MapModelChange(ImportContext context, SourceEntry entry)
    {
        var provider = RequireString(entry.Value, "provider");
        var model = RequireBoundedString(entry.Value, "modelId", 256);
        ValidateProviderId(provider);
        RejectSensitiveIdentifier(model);
        context.ProviderId = provider;
        context.ModelId = model;
        var recordId = context.Ids.Record(entry.Id);
        var data = new JsonObject
        {
            ["provider"] = ProviderSelection(provider, model),
            ["thinking"] = context.Thinking,
        };
        AddRecord(context, "model.selected", recordId, context.SelectedHead, entry.Timestamp, data, EntryExtension(entry));
        CompleteMapping(context, entry, [recordId], "mapped", [], false, null);
    }

    /// <summary>
    /// 功能：映射 thinking level；没有此前模型时隔离而不虚构 Provider。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">导入上下文。</param>
    /// <param name="entry">thinking_level_change entry。</param>
    private static void MapThinkingChange(ImportContext context, SourceEntry entry)
    {
        var thinking = RequireString(entry.Value, "thinkingLevel");
        ValidateThinking(thinking);
        if (context.ProviderId is null || context.ModelId is null)
        {
            MapExtensionEntry(context, entry, "quarantined", true, ["extension_details_quarantined"], ["thinkingLevel"]);
            return;
        }

        context.Thinking = thinking;
        var recordId = context.Ids.Record(entry.Id);
        var data = new JsonObject
        {
            ["provider"] = ProviderSelection(context.ProviderId, context.ModelId),
            ["thinking"] = thinking,
        };
        AddRecord(context, "model.selected", recordId, context.SelectedHead, entry.Timestamp, data, EntryExtension(entry));
        CompleteMapping(context, entry, [recordId], "mapped", [], false, null);
    }

    /// <summary>
    /// 功能：把 第三方格式 compaction 展开为零 usage assistant summary 和 durable context.compacted pair。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">导入上下文。</param>
    /// <param name="entry">compaction entry。</param>
    private static void MapCompaction(ImportContext context, SourceEntry entry)
    {
        if (entry.ParentId is null || !context.Mappings.TryGetValue(entry.ParentId, out var sourceLeaf) ||
            context.ProviderId is null || context.ModelId is null)
        {
            throw InvalidSource("compaction_state_invalid");
        }

        var retainedSourceId = RequireString(entry.Value, "firstKeptEntryId");
        if (!context.Mappings.TryGetValue(retainedSourceId, out var retained) ||
            retained.TargetKind != "message.appended" ||
            retained.TargetRole != "user")
        {
            throw InvalidSource("compaction_retained_mapping_invalid");
        }

        var summaryRecordId = context.Ids.CompactionSummaryRecord(entry.Id);
        var summaryMessageId = context.Ids.CompactionSummaryMessage(entry.Id);
        var reasons = new List<string>();
        var summary = SanitizeText(context, RequireString(entry.Value, "summary"), reasons);
        var summaryMessage = new JsonObject
        {
            ["messageId"] = summaryMessageId,
            ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = summary }),
            ["provider"] = ProviderSelection(context.ProviderId, context.ModelId),
            ["finishReason"] = "stop",
            ["usage"] = new JsonObject { ["inputTokens"] = 0, ["outputTokens"] = 0, ["totalTokens"] = 0 },
            ["time"] = entry.Timestamp,
            ["extensions"] = ImportExtension(new JsonObject { ["syntheticUsage"] = true }),
        };
        AddRecord(
            context,
            "message.appended",
            summaryRecordId,
            context.SelectedHead,
            entry.Timestamp,
            new JsonObject { ["message"] = summaryMessage },
            EntryExtension(entry, "summary"));
        var compactionRecordId = context.Ids.Record(entry.Id);
        var data = new JsonObject
        {
            ["sourceLeafRecordId"] = sourceLeaf.LastTargetId,
            ["summaryMessageId"] = summaryMessageId,
            ["firstRetainedRecordId"] = retained.LastTargetId,
            ["tokensBefore"] = RequireSafeInteger(entry.Value, "tokensBefore"),
            ["strategy"] = "pi-v3-import",
        };
        AddRecord(
            context,
            "context.compacted",
            compactionRecordId,
            summaryRecordId,
            entry.Timestamp,
            data,
            EntryExtension(entry, "compaction"));
        var quarantineNames = new List<string>();
        if (entry.Value.TryGetProperty("details", out _))
        {
            reasons.Add("compaction_details_quarantined");
            quarantineNames.Add("details");
        }

        if (entry.Value.TryGetProperty("fromHook", out _))
        {
            if (!reasons.Contains("compaction_details_quarantined", StringComparer.Ordinal))
            {
                reasons.Add("compaction_details_quarantined");
            }

            quarantineNames.Add("fromHook");
        }

        reasons.Add("compaction_summary_usage_unavailable");
        CompleteMapping(
            context,
            entry,
            [summaryRecordId, compactionRecordId],
            "mapped_with_loss",
            reasons,
            false,
            quarantineNames.Count == 0 ? null : CloneSelected(context, entry.Value, quarantineNames, reasons));
    }

    /// <summary>
    /// 功能：按冻结 wrapper 把 第三方格式 branch summary 映射为用户文本，并隔离可选 details/fromHook。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">导入上下文。</param>
    /// <param name="entry">branch_summary entry。</param>
    private static void MapBranchSummary(ImportContext context, SourceEntry entry)
    {
        var reasons = new List<string>();
        var summary = SanitizeText(context, RequireString(entry.Value, "summary"), reasons);
        var fromId = RequireString(entry.Value, "fromId");
        var text = "The following is a summary of a branch that this conversation came back from:\n\n<summary>\n" + summary + "\n</summary>";
        var message = new JsonObject
        {
            ["messageId"] = context.Ids.Message(entry.Id),
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["time"] = entry.Timestamp,
            ["extensions"] = ImportExtension(
                new JsonObject
                {
                    ["sourceRole"] = "branchSummary",
                    ["fromSourceEntryId"] = fromId,
                }),
        };
        var recordId = context.Ids.Record(entry.Id);
        AddRecord(
            context,
            "message.appended",
            recordId,
            context.SelectedHead,
            entry.Timestamp,
            new JsonObject { ["message"] = message },
            EntryExtension(entry));
        var quarantineNames = new List<string>();
        if (entry.Value.TryGetProperty("details", out _))
        {
            quarantineNames.Add("details");
        }

        if (entry.Value.TryGetProperty("fromHook", out _))
        {
            quarantineNames.Add("fromHook");
        }

        if (quarantineNames.Count > 0)
        {
            reasons.Add("extension_details_quarantined");
        }

        CompleteMapping(
            context,
            entry,
            [recordId],
            reasons.Count == 0 ? "mapped" : "mapped_with_loss",
            reasons,
            false,
            quarantineNames.Count == 0 ? null : CloneSelected(context, entry.Value, quarantineNames, reasons));
    }

    /// <summary>
    /// 功能：把 label 保存为 namespace extension，同时绑定 earlier target 的 portable record ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">导入上下文。</param>
    /// <param name="entry">label entry。</param>
    private static void MapLabel(ImportContext context, SourceEntry entry)
    {
        var targetSourceId = RequireString(entry.Value, "targetId");
        if (!context.Mappings.TryGetValue(targetSourceId, out var target))
        {
            throw InvalidSource("label_target_mapping_missing");
        }

        var reasons = new List<string> { "label_semantics_extension_only" };
        var value = new JsonObject
        {
            ["sourceEntryId"] = entry.Id,
            ["sourceType"] = entry.EntryType,
            ["targetSourceEntryId"] = targetSourceId,
            ["targetRecordId"] = target.LastTargetId,
        };
        if (entry.Value.TryGetProperty("label", out var label))
        {
            value["label"] = label.ValueKind == JsonValueKind.Null
                ? null
                : SanitizeText(context, label.GetString()!, reasons);
        }

        value["disposition"] = "preserved_extension";
        value["reportArtifactId"] = context.ReportArtifactId;
        var recordId = context.Ids.Record(entry.Id);
        AddRecord(
            context,
            "extension",
            recordId,
            context.SelectedHead,
            entry.Timestamp,
            new JsonObject { ["namespace"] = ImportNamespace, ["value"] = value },
            EntryExtension(entry));
        CompleteMapping(
            context,
            entry,
            [recordId],
            "preserved_extension",
            reasons,
            true,
            CloneSelected(context, entry.Value, ["targetId", "label"], reasons));
    }

    /// <summary>
    /// 功能：把有界 session_info.name 映射为 portable session.metadata。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">导入上下文。</param>
    /// <param name="entry">session_info entry。</param>
    private static void MapSessionInfo(ImportContext context, SourceEntry entry)
    {
        var reasons = new List<string>();
        JsonNode? name = null;
        if (entry.Value.TryGetProperty("name", out var nameValue) && nameValue.ValueKind != JsonValueKind.Null)
        {
            name = SanitizeText(context, nameValue.GetString()!, reasons);
        }

        var recordId = context.Ids.Record(entry.Id);
        AddRecord(
            context,
            "session.metadata",
            recordId,
            context.SelectedHead,
            entry.Timestamp,
            new JsonObject { ["name"] = name },
            EntryExtension(entry));
        CompleteMapping(
            context,
            entry,
            [recordId],
            reasons.Count == 0 ? "mapped" : "mapped_with_loss",
            reasons,
            false,
            null);
    }

    /// <summary>
    /// 功能：把未知 source type 的非 tree 字段经递归脱敏后隔离到报告和 context-free extension。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">导入上下文。</param>
    /// <param name="entry">base/tree 合法的未知 entry。</param>
    private static void MapUnknown(ImportContext context, SourceEntry entry)
    {
        var fields = entry.Value.EnumerateObject()
            .Where(property => property.Name is not ("type" or "id" or "parentId" or "timestamp"))
            .Select(property => property.Name)
            .ToArray();
        MapExtensionEntry(context, entry, "quarantined", true, ["unknown_entry_type"], fields);
    }

    /// <summary>
    /// 功能：创建统一 context-free 第三方格式 provenance extension，并生成逐行损失报告。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">导入上下文。</param>
    /// <param name="entry">待隔离 entry。</param>
    /// <param name="disposition">preserved_extension 或 quarantined。</param>
    /// <param name="contextExcluded">是否明确排除 Provider context。</param>
    /// <param name="reasonCodes">稳定原因码。</param>
    /// <param name="quarantineFields">进入已脱敏报告的源字段名。</param>
    private static void MapExtensionEntry(
        ImportContext context,
        SourceEntry entry,
        string disposition,
        bool contextExcluded,
        IReadOnlyList<string> reasonCodes,
        IReadOnlyList<string> quarantineFields)
    {
        var reasons = reasonCodes.ToList();
        var value = new JsonObject
        {
            ["sourceEntryId"] = entry.Id,
            ["sourceType"] = entry.EntryType,
            ["disposition"] = disposition,
        };
        if (contextExcluded && disposition == "quarantined")
        {
            value["contextExcluded"] = true;
        }

        value["reportArtifactId"] = context.ReportArtifactId;
        var recordId = context.Ids.Record(entry.Id);
        AddRecord(
            context,
            "extension",
            recordId,
            context.SelectedHead,
            entry.Timestamp,
            new JsonObject { ["namespace"] = ImportNamespace, ["value"] = value },
            EntryExtension(entry));
        CompleteMapping(
            context,
            entry,
            [recordId],
            disposition,
            reasons,
            contextExcluded,
            CloneSelected(context, entry.Value, quarantineFields, reasons));
    }

    /// <summary>
    /// 功能：把 第三方格式 文本、thinking 和有效 toolCall block 映射为 portable content，并报告其余 block。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">导入上下文。</param>
    /// <param name="source">字符串或 content 数组。</param>
    /// <param name="reasons">当前 source entry 的损失原因集合。</param>
    /// <param name="allowReasoning">是否允许 thinking -> reasoning。</param>
    /// <param name="allowToolCalls">是否允许 inert toolCall。</param>
    /// <returns>不含 inline base64、秘密或主机路径的 portable content。</returns>
    private static JsonArray MapTextContent(
        ImportContext context,
        JsonElement source,
        List<string> reasons,
        bool allowReasoning,
        bool allowToolCalls)
    {
        var result = new JsonArray();
        if (source.ValueKind == JsonValueKind.String)
        {
            result.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = SanitizeText(context, source.GetString()!, reasons),
            });
            return result;
        }

        if (source.ValueKind != JsonValueKind.Array || source.GetArrayLength() > 1024)
        {
            throw InvalidSource("message_content_invalid");
        }

        foreach (var item in source.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var typeNode) || typeNode.ValueKind != JsonValueKind.String)
            {
                AddReason(reasons, "unsupported_content_block");
                continue;
            }

            switch (typeNode.GetString())
            {
                case "text":
                    result.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = SanitizeText(context, RequireString(item, "text"), reasons),
                    });
                    break;
                case "thinking" when allowReasoning:
                    result.Add(new JsonObject
                    {
                        ["type"] = "reasoning",
                        ["text"] = SanitizeText(context, RequireString(item, "thinking"), reasons),
                    });
                    break;
                case "toolCall" when allowToolCalls:
                    if (!TryMapToolCall(context, item, reasons, out var toolCall))
                    {
                        AddReason(reasons, "unsupported_content_block");
                    }
                    else
                    {
                        result.Add(toolCall);
                    }

                    break;
                default:
                    AddReason(reasons, "unsupported_content_block");
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// 功能：验证并映射一个历史 toolCall block，保持其 inert transcript 语义。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">导入上下文。</param>
    /// <param name="source">第三方格式 toolCall block。</param>
    /// <param name="reasons">脱敏或损失原因集合。</param>
    /// <param name="target">成功时的 portable tool_call。</param>
    /// <returns>ID、名称和对象参数均有效时为 true。</returns>
    private static bool TryMapToolCall(
        ImportContext context,
        JsonElement source,
        List<string> reasons,
        out JsonObject? target)
    {
        target = null;
        if (!TryGetString(source, "id", out var id) || !IsOpaqueId(id) ||
            !TryGetString(source, "name", out var name) || !ToolNamePattern().IsMatch(name) ||
            !source.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var sanitized = RedactClone(context, arguments, reasons);
        target = new JsonObject
        {
            ["type"] = "tool_call",
            ["toolCallId"] = id,
            ["name"] = name,
            ["arguments"] = sanitized,
        };
        return true;
    }

    /// <summary>
    /// 功能：按 ADR 0012 把 第三方格式 usage 聚合 cache token、验证 total 并归一 USD cost。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="message">assistant source message。</param>
    /// <returns>满足 portable usage Schema 的对象。</returns>
    /// <exception cref="LegacySessionV3ImportException">token、总和或 cost 非法。</exception>
    private static JsonObject MapUsage(JsonElement message)
    {
        var usage = RequireObject(message, "usage");
        var input = RequireSafeInteger(usage, "input");
        var output = RequireSafeInteger(usage, "output");
        var cacheRead = RequireSafeInteger(usage, "cacheRead");
        var cacheWrite = RequireSafeInteger(usage, "cacheWrite");
        var total = RequireSafeInteger(usage, "totalTokens");
        long normalizedInput;
        try
        {
            normalizedInput = checked(input + cacheRead + cacheWrite);
        }
        catch (OverflowException)
        {
            throw InvalidSource("usage_overflow");
        }

        if (normalizedInput > MaxSafeInteger || output > MaxSafeInteger - normalizedInput || total != normalizedInput + output)
        {
            throw InvalidSource("usage_total_invalid");
        }

        var cost = RequireObject(usage, "cost");
        if (!cost.TryGetProperty("total", out var totalCost) ||
            totalCost.ValueKind != JsonValueKind.Number ||
            !decimal.TryParse(totalCost.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) ||
            amount < 0)
        {
            throw InvalidSource("usage_cost_invalid");
        }

        var result = new JsonObject
        {
            ["inputTokens"] = normalizedInput,
            ["outputTokens"] = output,
            ["totalTokens"] = total,
            ["cachedInputTokens"] = cacheRead,
            ["cacheWriteTokens"] = cacheWrite,
        };
        if (usage.TryGetProperty("reasoning", out _))
        {
            result["reasoningTokens"] = RequireSafeInteger(usage, "reasoning");
        }

        result["cost"] = new JsonObject
        {
            ["currency"] = "USD",
            ["amount"] = amount.ToString("G29", CultureInfo.InvariantCulture),
        };
        return result;
    }

    /// <summary>
    /// 功能：把 第三方格式 stopReason 映射为 portable finishReason 并拒绝未知值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="reason">第三方格式 stop reason。</param>
    /// <returns>portable spelling。</returns>
    private static string MapFinishReason(string reason)
    {
        return reason switch
        {
            "toolUse" => "tool_use",
            "aborted" => "cancelled",
            "stop" or "length" or "error" or "interrupted" => reason,
            _ => throw InvalidSource("finish_reason_invalid"),
        };
    }

    /// <summary>
    /// 功能：把目标记录加入 append-only 序列并立即推进 selected head。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">导入上下文。</param>
    /// <param name="kind">允许的 portable kind。</param>
    /// <param name="recordId">新 opaque record ID。</param>
    /// <param name="parentId">此前记录或 null。</param>
    /// <param name="time">源 UTC 时间或固定报告时间。</param>
    /// <param name="data">kind-specific data。</param>
    /// <param name="extensions">显式命名空间扩展。</param>
    private static void AddRecord(
        ImportContext context,
        string kind,
        string recordId,
        string? parentId,
        string time,
        JsonObject data,
        JsonObject extensions)
    {
        if (ForbiddenKinds.Contains(kind) || context.RecordIds.Contains(recordId))
        {
            throw InvalidSource("generated_record_invalid");
        }

        var record = new JsonObject
        {
            ["schemaVersion"] = "0.1",
            ["kind"] = kind,
            ["recordId"] = recordId,
            ["sessionId"] = context.SessionId,
            ["seq"] = context.Records.Count + 1,
            ["parentId"] = parentId,
            ["time"] = time,
            ["data"] = data,
            ["extensions"] = extensions,
        };
        context.RecordIds.Add(recordId);
        context.Records.Add(record);
        context.SelectedHead = recordId;
    }

    /// <summary>
    /// 功能：登记 source-to-target 映射并为所有非无损 disposition 创建 mandatory report item。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">导入上下文。</param>
    /// <param name="entry">源 entry。</param>
    /// <param name="targetIds">按生成顺序排列的目标 record IDs。</param>
    /// <param name="disposition">映射 disposition。</param>
    /// <param name="reasons">稳定原因码。</param>
    /// <param name="contextExcluded">是否排除 Provider context。</param>
    /// <param name="quarantinedValue">可选的已递归脱敏值。</param>
    private static void CompleteMapping(
        ImportContext context,
        SourceEntry entry,
        IReadOnlyList<string> targetIds,
        string disposition,
        List<string> reasons,
        bool contextExcluded,
        JsonObject? quarantinedValue)
    {
        if (targetIds.Count == 0 || context.Mappings.ContainsKey(entry.Id))
        {
            throw InvalidSource("mapping_invalid");
        }

        var targetRecord = context.Records.First(record =>
            string.Equals(record["recordId"]!.GetValue<string>(), targetIds[^1], StringComparison.Ordinal));
        var targetKind = targetRecord["kind"]!.GetValue<string>();
        string? targetRole = null;
        if (targetKind == "message.appended" &&
            targetRecord["data"] is JsonObject data &&
            data["message"] is JsonObject message &&
            message["role"] is JsonValue role)
        {
            targetRole = role.GetValue<string>();
        }

        context.Mappings.Add(entry.Id, new SourceMapping(targetIds[^1], targetIds, disposition, targetKind, targetRole));
        if (reasons.Count == 0 && disposition == "mapped")
        {
            return;
        }

        foreach (var reason in reasons)
        {
            context.Warnings.Add(reason);
        }

        var report = new JsonObject
        {
            ["sourceLine"] = entry.LineNumber,
            ["sourceEntryId"] = entry.Id,
            ["sourceType"] = entry.EntryType,
            ["disposition"] = disposition,
            ["reasonCodes"] = new JsonArray(reasons.Distinct(StringComparer.Ordinal).Select(value => JsonValue.Create(value)).ToArray()),
            ["sourceLineSha256"] = Convert.ToHexStringLower(SHA256.HashData(entry.RawLine)),
            ["targetRecordIds"] = new JsonArray(targetIds.Select(value => JsonValue.Create(value)).ToArray()),
            ["contextExcluded"] = contextExcluded,
        };
        if (quarantinedValue is not null && quarantinedValue.Count > 0)
        {
            report["quarantinedValue"] = quarantinedValue;
        }

        context.ReportEntries.Add(report);
    }

    /// <summary>
    /// 功能：生成 mandatory import report 的规范紧凑 UTF-8 加 LF 字节。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">全部 source mapping 与记录已完成的上下文。</param>
    /// <returns>用于 artifact 长度与 SHA-256 强绑定的精确字节。</returns>
    private static byte[] BuildReportBytes(ImportContext context)
    {
        var mappedCount = context.Mappings.Values.Count(value => value.Disposition is "mapped" or "mapped_with_loss");
        var extensionCount = context.Mappings.Values.Count(value => value.Disposition is "preserved_extension" or "quarantined");
        var warnings = context.Warnings.Order(StringComparer.Ordinal).Select(value => JsonValue.Create(value)).ToArray();
        var report = new JsonObject
        {
            ["schemaVersion"] = "0.1",
            ["kind"] = "pi-session-v3-import-report",
            ["status"] = warnings.Length == 0 ? "completed" : "completed_with_warnings",
            ["source"] = new JsonObject
            {
                ["format"] = "pi-session-v3",
                ["version"] = 3,
                ["sessionId"] = context.SourceSessionId,
                ["sha256"] = context.SourceSha256,
                ["byteLength"] = context.SourceByteLength,
                ["referenceCommit"] = ReferenceCommit,
                ["sourcePathDisposition"] = "not_persisted",
            },
            ["target"] = new JsonObject
            {
                ["sessionId"] = context.SessionId,
                ["reportArtifactId"] = context.ReportArtifactId,
            },
            ["counts"] = new JsonObject
            {
                ["sourceEntries"] = context.Mappings.Count,
                ["targetRecords"] = context.Records.Count + 1,
                ["mappedSourceEntries"] = mappedCount,
                ["extensionSourceEntries"] = extensionCount,
                ["reportedSourceEntries"] = context.ReportEntries.Count,
                ["redactedValues"] = context.RedactedValues,
                ["skippedSourceEntries"] = 0,
            },
            ["warnings"] = new JsonArray(warnings),
            ["entries"] = new JsonArray(context.ReportEntries.Select(value => value.DeepClone()).ToArray()),
        };
        return SerializeLine(report);
    }

    /// <summary>
    /// 功能：在报告 artifact 字节和 hash 已知后追加唯一 artifact.created 记录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">导入上下文。</param>
    /// <param name="byteLength">含末尾 LF 的报告 artifact 长度。</param>
    /// <param name="sha256">报告精确字节 SHA-256。</param>
    private static void AppendReportArtifactRecord(ImportContext context, int byteLength, string sha256)
    {
        var artifact = new JsonObject
        {
            ["artifactId"] = context.ReportArtifactId,
            ["mediaType"] = ReportMediaType,
            ["byteLength"] = byteLength,
            ["sha256"] = sha256,
            // 冻结的 portable displayName；保留旧值以避免既有 journal 与 conformance fixture 迁移。
            ["displayName"] = "PI Session v3 import report",
        };
        AddRecord(
            context,
            "artifact.created",
            context.Ids.ReportRecordId,
            context.SelectedHead,
            context.CreatedAt,
            new JsonObject { ["artifact"] = artifact },
            ImportExtension(new JsonObject { ["synthetic"] = "report-artifact" }));
    }

    /// <summary>
    /// 功能：按固定属性顺序生成 header 与全部 append-only record 的 LF JSONL。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">含最终 artifact.created 的导入上下文。</param>
    /// <returns>完整 journal 字节。</returns>
    private static byte[] BuildJournalBytes(ImportContext context)
    {
        var header = new JsonObject
        {
            ["kind"] = "session",
            ["schemaVersion"] = "0.1",
            ["sessionId"] = context.SessionId,
            ["createdAt"] = context.CreatedAt,
            ["workspace"] = context.Workspace,
            ["provenance"] = new JsonObject
            {
                ["source"] = "pi-session-v3",
                ["sourceSessionId"] = context.SourceSessionId,
                ["sourceSha256"] = context.SourceSha256,
                ["referenceCommit"] = ReferenceCommit,
                ["reportArtifactId"] = context.ReportArtifactId,
                ["extensions"] = ImportExtension(
                    new JsonObject
                    {
                        ["sourceVersion"] = 3,
                        ["sourcePathDisposition"] = "not_persisted",
                    }),
            },
        };
        using var output = new MemoryStream();
        output.Write(SerializeLine(header));
        foreach (var record in context.Records)
        {
            output.Write(SerializeLine(record));
        }

        return output.ToArray();
    }

    /// <summary>
    /// 功能：发布前重新解析生成字节并核对序号、parent、禁用 kind、artifact hash 与报告隐私。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">生成上下文。</param>
    /// <param name="journalBytes">待发布 journal。</param>
    /// <param name="reportBytes">待发布 report。</param>
    /// <param name="reportHash">调用方计算的 report SHA-256。</param>
    /// <exception cref="LegacySessionV3ImportException">任何生成不变量失败。</exception>
    private static void ValidateGeneratedJournal(
        ImportContext context,
        byte[] journalBytes,
        byte[] reportBytes,
        string reportHash)
    {
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(reportBytes)), reportHash, StringComparison.Ordinal))
        {
            throw PublishFailure("report_hash_invalid");
        }

        var lines = journalBytes.AsSpan(0, journalBytes.Length - 1).ToArray().AsMemory();
        var lineRanges = new List<ReadOnlyMemory<byte>>();
        var start = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines.Span[index] == (byte)'\n')
            {
                lineRanges.Add(lines[start..index]);
                start = index + 1;
            }
        }

        lineRanges.Add(lines[start..]);
        if (lineRanges.Count != context.Records.Count + 1)
        {
            throw PublishFailure("journal_count_invalid");
        }

        var recordIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < lineRanges.Count; index++)
        {
            var record = ParseStrictObject(lineRanges[index]);
            var id = RequireString(record, "recordId");
            var kind = RequireString(record, "kind");
            var seq = RequireSafeInteger(record, "seq");
            var parent = RequireNullableString(record, "parentId");
            if (seq != index || !recordIds.Add(id) || ForbiddenKinds.Contains(kind) ||
                (parent is not null && !recordIds.Contains(parent)))
            {
                throw PublishFailure("journal_tree_invalid");
            }
        }

        if (ContainsSensitiveShape(reportBytes))
        {
            throw PublishFailure("report_privacy_invalid");
        }
    }

    /// <summary>
    /// 功能：以 artifact-first、完整 journal、源身份复核和同文件系统 Directory.Move 发布目标。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="stateDirectory">调用方授权状态根。</param>
    /// <param name="sessionId">新目标 Session ID。</param>
    /// <param name="artifactId">报告 artifact ID。</param>
    /// <param name="reportBytes">已验证报告字节。</param>
    /// <param name="journalBytes">已验证 journal 字节。</param>
    /// <param name="source">仍持续打开的 source snapshot。</param>
    /// <param name="cancellationToken">写入与发布前取消信号。</param>
    /// <remarks>失败时只清理由本次创建的随机临时目录；绝不删除或覆盖目标。</remarks>
    private static async Task PublishAsync(
        string stateDirectory,
        string sessionId,
        string artifactId,
        byte[] reportBytes,
        byte[] journalBytes,
        SourceSnapshot source,
        CancellationToken cancellationToken)
    {
        var sessionsRoot = Path.Combine(stateDirectory, "sessions");
        var temporary = Path.Combine(sessionsRoot, ".pi-v3-import-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(sessionsRoot, sessionId);
        var published = false;
        try
        {
            Directory.CreateDirectory(sessionsRoot);
            RejectDirectoryLink(sessionsRoot);
            if (Directory.Exists(target) || File.Exists(target))
            {
                throw PublishFailure("target_exists");
            }

            Directory.CreateDirectory(Path.Combine(temporary, "artifacts"));
            RejectDirectoryLink(temporary);
            var reportPath = Path.Combine(temporary, "artifacts", artifactId + ".json");
            await WriteDurableFileAsync(reportPath, reportBytes, cancellationToken).ConfigureAwait(false);
            await WriteDurableFileAsync(Path.Combine(temporary, "journal.jsonl"), journalBytes, cancellationToken).ConfigureAwait(false);
            await source.VerifyIdentityAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(temporary, target);
            published = true;
        }
        catch (LegacySessionV3ImportException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw PublishFailure("target_publish_failed");
        }
        finally
        {
            if (!published)
            {
                TryDeleteOwnedDirectory(temporary);
            }
        }
    }

    /// <summary>
    /// 功能：以 CreateNew 和 WriteThrough 写完整文件，并在返回前请求 Flush(true)。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">本次随机临时目录内的新文件。</param>
    /// <param name="bytes">完整不可变 artifact 或 journal 字节。</param>
    /// <param name="cancellationToken">写入取消信号。</param>
    private static async Task WriteDurableFileAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16_384,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// 功能：拒绝发布路径中的稳定符号链接目录叶节点。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">必须由本导入器创建或验证的目录。</param>
    private static void RejectDirectoryLink(string path)
    {
        var info = new DirectoryInfo(path);
        info.Refresh();
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw PublishFailure("target_directory_invalid");
        }
    }

    /// <summary>
    /// 功能：尽力删除仅由本次随机名字创建且尚未发布的临时目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">本次调用生成的 sibling 临时目录。</param>
    private static void TryDeleteOwnedDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不能扩大到删除目标或源；调用方仍收到原始失败。
        }
    }

    /// <summary>
    /// 功能：递归克隆指定源字段并将 secret、绝对路径和凭据形状替换为显式占位符。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">累计 redactedValues 的导入上下文。</param>
    /// <param name="source">源 entry 对象。</param>
    /// <param name="fieldNames">按报告所需顺序选择的字段。</param>
    /// <param name="reasons">发现脱敏时加入 sensitive_value_redacted。</param>
    /// <returns>不含未扫描未知值的 quarantine 对象。</returns>
    private static JsonObject CloneSelected(
        ImportContext context,
        JsonElement source,
        IReadOnlyList<string> fieldNames,
        List<string> reasons)
    {
        var result = new JsonObject();
        foreach (var name in fieldNames)
        {
            if (source.TryGetProperty(name, out var value))
            {
                result[name] = RedactClone(context, value, reasons);
            }
        }

        return result;
    }

    /// <summary>
    /// 功能：有界递归复制 JSON 值，敏感键、token 文本和明显主机路径统一替换为 [REDACTED]。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">累计脱敏计数的上下文。</param>
    /// <param name="value">严格 JSON 源值。</param>
    /// <param name="reasons">当前 entry 原因列表。</param>
    /// <returns>可安全持久化的新 JsonNode。</returns>
    private static JsonNode? RedactClone(ImportContext context, JsonElement value, List<string> reasons)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var objectResult = new JsonObject();
                foreach (var property in value.EnumerateObject())
                {
                    if (SensitiveKeyPattern().IsMatch(property.Name))
                    {
                        var redactedName = "redactedField";
                        var suffix = 1;
                        while (objectResult.ContainsKey(redactedName))
                        {
                            redactedName = "redactedField" + suffix.ToString(CultureInfo.InvariantCulture);
                            suffix++;
                        }

                        objectResult[redactedName] = Redacted(context, reasons);
                    }
                    else
                    {
                        objectResult[property.Name] = RedactClone(context, property.Value, reasons);
                    }
                }

                return objectResult;
            case JsonValueKind.Array:
                var arrayResult = new JsonArray();
                foreach (var item in value.EnumerateArray())
                {
                    arrayResult.Add(RedactClone(context, item, reasons));
                }

                return arrayResult;
            case JsonValueKind.String:
                return SanitizeText(context, value.GetString()!, reasons);
            case JsonValueKind.Number:
                return JsonNode.Parse(value.GetRawText());
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            case JsonValueKind.Null:
                return null;
            default:
                throw InvalidSource("json_value_invalid");
        }
    }

    /// <summary>
    /// 功能：扫描一个将进入 portable 内容的字符串并在敏感或主机路径形状时整体脱敏。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">累计脱敏计数的上下文。</param>
    /// <param name="value">源字符串。</param>
    /// <param name="reasons">可选的当前 entry 原因列表。</param>
    /// <returns>原字符串或 [REDACTED]。</returns>
    private static string SanitizeText(ImportContext context, string value, List<string>? reasons)
    {
        if (SensitiveTextPattern().IsMatch(value) || IsHostPath(value))
        {
            context.RedactedValues++;
            if (reasons is not null)
            {
                AddReason(reasons, "sensitive_value_redacted");
            }

            return "[REDACTED]";
        }

        return value;
    }

    /// <summary>
    /// 功能：记录一次敏感键替换并返回统一占位符。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="context">累计脱敏计数的上下文。</param>
    /// <param name="reasons">当前 entry 原因列表。</param>
    /// <returns>[REDACTED]。</returns>
    private static string Redacted(ImportContext context, List<string> reasons)
    {
        context.RedactedValues++;
        AddReason(reasons, "sensitive_value_redacted");
        return "[REDACTED]";
    }

    /// <summary>
    /// 功能：去重追加稳定报告原因码并保持首次发现顺序。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="reasons">当前 entry 原因列表。</param>
    /// <param name="reason">待追加原因码。</param>
    private static void AddReason(List<string> reasons, string reason)
    {
        if (!reasons.Contains(reason, StringComparer.Ordinal))
        {
            reasons.Add(reason);
        }
    }

    /// <summary>
    /// 功能：保守识别 POSIX、home、UNC 与 Windows drive 绝对路径。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">待检查字符串。</param>
    /// <returns>明显主机绝对路径时为 true。</returns>
    private static bool IsHostPath(string value)
    {
        return value.StartsWith('/') ||
            value.StartsWith("~/", StringComparison.Ordinal) ||
            value.StartsWith("\\\\", StringComparison.Ordinal) ||
            WindowsPathPattern().IsMatch(value);
    }

    /// <summary>
    /// 功能：确认最终报告 JSON 不再包含 secret 形状；不回显命中值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="bytes">严格 UTF-8 report bytes。</param>
    /// <returns>发现敏感键或文本时为 true。</returns>
    private static bool ContainsSensitiveShape(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var stack = new Stack<JsonElement>();
        stack.Push(document.RootElement);
        var visited = 0;
        while (stack.Count > 0)
        {
            if (++visited > 200_000)
            {
                return true;
            }

            var current = stack.Pop();
            if (current.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in current.EnumerateObject())
                {
                    if (SensitiveKeyPattern().IsMatch(property.Name))
                    {
                        return true;
                    }

                    stack.Push(property.Value);
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.EnumerateArray())
                {
                    stack.Push(item);
                }
            }
            else if (current.ValueKind == JsonValueKind.String &&
                (SensitiveTextPattern().IsMatch(current.GetString()!) || IsHostPath(current.GetString()!)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 功能：创建 第三方格式 source entry 的标准 top-level provenance extension。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="entry">源 entry。</param>
    /// <param name="mappingPart">compaction 双记录的可选部分名。</param>
    /// <returns>显式 org.agentprotocol.pi-v3 extensions。</returns>
    private static JsonObject EntryExtension(SourceEntry entry, string? mappingPart = null)
    {
        var value = new JsonObject
        {
            ["sourceEntryId"] = entry.Id,
            ["sourceLine"] = entry.LineNumber,
        };
        if (mappingPart is not null)
        {
            value["mappingPart"] = mappingPart;
        }

        return ImportExtension(value);
    }

    /// <summary>
    /// 功能：把扩展值包装在唯一导入命名空间下。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">命名空间内 JSON 对象。</param>
    /// <returns>portable extensions 对象。</returns>
    private static JsonObject ImportExtension(JsonObject value)
    {
        return new JsonObject { [ImportNamespace] = value };
    }

    /// <summary>
    /// 功能：创建属性顺序固定的 portable Provider selection。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="provider">规范 Provider ID。</param>
    /// <param name="model">模型 ID。</param>
    /// <returns>含 id/modelId 的对象。</returns>
    private static JsonObject ProviderSelection(string provider, string model)
    {
        return new JsonObject { ["id"] = provider, ["modelId"] = model };
    }

    /// <summary>
    /// 功能：把一个 JsonNode 序列化为紧凑 UTF-8 并追加唯一 LF。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">不含循环父引用的 JSON node。</param>
    /// <returns>紧凑 JSON 加 LF。</returns>
    private static byte[] SerializeLine(JsonNode value)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options);
        var result = new byte[payload.Length + 1];
        payload.CopyTo(result, 0);
        result[^1] = (byte)'\n';
        return result;
    }

    /// <summary>
    /// 功能：要求对象只含白名单字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">待检查对象。</param>
    /// <param name="fields">允许字段集合。</param>
    /// <param name="failure">稳定失败类别。</param>
    private static void RequireOnlyFields(JsonElement value, HashSet<string> fields, string failure)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(property => !fields.Contains(property.Name)))
        {
            throw InvalidSource(failure);
        }
    }

    /// <summary>
    /// 功能：取得必需 JSON 属性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>属性值。</returns>
    private static JsonElement RequireProperty(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property))
        {
            throw InvalidSource("required_field_missing");
        }

        return property;
    }

    /// <summary>
    /// 功能：取得必需非空字符串。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>非空字符串。</returns>
    private static string RequireString(JsonElement value, string name)
    {
        var property = RequireProperty(value, name);
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(property.GetString()))
        {
            throw InvalidSource("string_field_invalid");
        }

        return property.GetString()!;
    }

    /// <summary>
    /// 功能：取得允许空值的必需字符串。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>字符串值。</returns>
    private static string RequireStringAllowEmpty(JsonElement value, string name)
    {
        var property = RequireProperty(value, name);
        if (property.ValueKind != JsonValueKind.String)
        {
            throw InvalidSource("string_field_invalid");
        }

        return property.GetString()!;
    }

    /// <summary>
    /// 功能：取得有最大 UTF-16 长度限制的非空字符串。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <param name="maximum">最大字符数。</param>
    /// <returns>有界字符串。</returns>
    private static string RequireBoundedString(JsonElement value, string name, int maximum)
    {
        var text = RequireString(value, name);
        if (text.Length > maximum)
        {
            throw InvalidSource("string_field_too_long");
        }

        return text;
    }

    /// <summary>
    /// 功能：取得 source ID/type 等有界标识符字符串。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <param name="maximum">最大字符数。</param>
    /// <returns>有界非空标识符。</returns>
    private static string RequireBoundedIdentifier(JsonElement value, string name, int maximum)
    {
        var text = RequireBoundedString(value, name, maximum);
        if (text.Any(char.IsControl))
        {
            throw InvalidSource("identifier_invalid");
        }

        return text;
    }

    /// <summary>
    /// 功能：读取可为 null 的字符串属性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>null 或字符串。</returns>
    private static string? RequireNullableString(JsonElement value, string name)
    {
        var property = RequireProperty(value, name);
        return property.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => property.GetString(),
            _ => throw InvalidSource("nullable_string_invalid"),
        };
    }

    /// <summary>
    /// 功能：验证可选 null/有界字符串字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <param name="maximum">最大字符数。</param>
    private static void RequireNullableBoundedString(JsonElement value, string name, int maximum)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (property.ValueKind != JsonValueKind.String || property.GetString()!.Length > maximum)
        {
            throw InvalidSource("nullable_string_invalid");
        }
    }

    /// <summary>
    /// 功能：取得必需 JSON 对象。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>对象 JsonElement。</returns>
    private static JsonElement RequireObject(JsonElement value, string name)
    {
        var property = RequireProperty(value, name);
        if (property.ValueKind != JsonValueKind.Object)
        {
            throw InvalidSource("object_field_invalid");
        }

        return property;
    }

    /// <summary>
    /// 功能：取得非负 JSON safe integer。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>0..2^53-1 的 Int64。</returns>
    private static long RequireSafeInteger(JsonElement value, string name)
    {
        var property = RequireProperty(value, name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var result) || result < 0 || result > MaxSafeInteger)
        {
            throw InvalidSource("integer_field_invalid");
        }

        return result;
    }

    /// <summary>
    /// 功能：取得必需 JSON boolean。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>布尔值。</returns>
    private static bool RequireBoolean(JsonElement value, string name)
    {
        var property = RequireProperty(value, name);
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw InvalidSource("boolean_field_invalid"),
        };
    }

    /// <summary>
    /// 功能：尝试取得字符串属性且不抛出包含实例值的异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <param name="text">成功时字符串。</param>
    /// <returns>属性存在且为非空字符串时为 true。</returns>
    private static bool TryGetString(JsonElement value, string name, out string text)
    {
        text = string.Empty;
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        text = property.GetString() ?? string.Empty;
        return text.Length > 0;
    }

    /// <summary>
    /// 功能：严格验证 Z 结尾、零偏移且可解析的 RFC 3339 时间并保留源拼写。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">父对象。</param>
    /// <param name="name">时间字段名。</param>
    /// <returns>原 UTC 字符串。</returns>
    private static string RequireUtc(JsonElement value, string name)
    {
        var text = RequireString(value, name);
        if (!text.EndsWith('Z') ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp) ||
            timestamp.Offset != TimeSpan.Zero)
        {
            throw InvalidSource("utc_time_invalid");
        }

        return text;
    }

    /// <summary>
    /// 功能：验证 portable opaque ID 语法。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">待验证 ID。</param>
    /// <param name="failure">稳定失败类别。</param>
    private static void ValidateOpaqueId(string value, string failure)
    {
        if (!IsOpaqueId(value))
        {
            throw InvalidUsage(failure);
        }
    }

    /// <summary>
    /// 功能：判断字符串是否满足公共 opaque ID 长度与字符集。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">待检查字符串。</param>
    /// <returns>有效时为 true。</returns>
    private static bool IsOpaqueId(string value)
    {
        return OpaqueIdPattern().IsMatch(value);
    }

    /// <summary>
    /// 功能：验证 portable Provider ID 语法并拒绝 secret/path 形状。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">Provider ID。</param>
    private static void ValidateProviderId(string value)
    {
        if (!ProviderIdPattern().IsMatch(value))
        {
            throw InvalidSource("provider_id_invalid");
        }

        RejectSensitiveIdentifier(value);
    }

    /// <summary>
    /// 功能：验证 portable tool name 语法。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">工具名。</param>
    private static void ValidateToolName(string value)
    {
        if (!ToolNamePattern().IsMatch(value))
        {
            throw InvalidSource("tool_name_invalid");
        }
    }

    /// <summary>
    /// 功能：验证公共 thinking 枚举。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">thinking level。</param>
    private static void ValidateThinking(string value)
    {
        if (value is not ("off" or "minimal" or "low" or "medium" or "high" or "xhigh"))
        {
            throw InvalidSource("thinking_invalid");
        }
    }

    /// <summary>
    /// 功能：验证 source type 适合进入报告和 extension 身份字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">source type。</param>
    private static void ValidateSourceType(string value)
    {
        if (!SourceTypePattern().IsMatch(value))
        {
            throw InvalidSource("source_type_invalid");
        }
    }

    /// <summary>
    /// 功能：拒绝不能原样写入 provenance 的 secret 或 host-path 形状标识符。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">source/session/model 标识符。</param>
    private static void RejectSensitiveIdentifier(string value)
    {
        if (SensitiveTextPattern().IsMatch(value) || IsHostPath(value))
        {
            throw InvalidSource("sensitive_identifier_invalid");
        }
    }

    /// <summary>
    /// 功能：创建 source/journal 不兼容错误，固定映射到 portable 退出码 7。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">稳定安全类别。</param>
    /// <returns>导入异常。</returns>
    private static LegacySessionV3ImportException InvalidSource(string kind)
    {
        return new LegacySessionV3ImportException(kind, 7);
    }

    /// <summary>
    /// 功能：创建 CLI 参数/目标 ID 错误，固定映射到 portable 退出码 2。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">稳定安全类别。</param>
    /// <returns>导入异常。</returns>
    private static LegacySessionV3ImportException InvalidUsage(string kind)
    {
        return new LegacySessionV3ImportException(kind, 2);
    }

    /// <summary>
    /// 功能：创建原子持久化/发布错误，固定映射到 portable 退出码 8。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">稳定安全类别。</param>
    /// <returns>导入异常。</returns>
    private static LegacySessionV3ImportException PublishFailure(string kind)
    {
        return new LegacySessionV3ImportException(kind, 8);
    }

    /// <summary>
    /// 功能：通过 libc open(O_NOFOLLOW) 打开 Unix source，阻止叶符号链接替换。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">调用方 source 路径。</param>
    /// <param name="flags">只读、close-on-exec 与 no-follow 标志。</param>
    /// <returns>非负文件描述符，失败为 -1。</returns>
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenUnixSource(string path, int flags);

    /// <summary>
    /// 功能：取得识别凭据、cookie 与 token 键名的编译期正则。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>大小写不敏感的敏感键正则。</returns>
    [GeneratedRegex("^(?:authorization|cookie|credential|password|passwd|secret|api[_-]?key|access[_-]?token|refresh[_-]?token)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveKeyPattern();

    /// <summary>
    /// 功能：取得识别 Bearer、sk token 和 key=value secret 文本的编译期正则。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>大小写不敏感的敏感文本正则。</returns>
    [GeneratedRegex("(?:bearer\\s+[A-Za-z0-9._~+/=-]{8,}|sk-[A-Za-z0-9_-]{8,}|(?:api[_-]?key|password|secret|access[_-]?token)\\s*[:=]\\s*[^\\s,;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveTextPattern();

    /// <summary>
    /// 功能：取得识别 Windows drive 绝对路径前缀的编译期正则。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>Windows drive 路径正则。</returns>
    [GeneratedRegex("^[A-Za-z]:[\\\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathPattern();

    /// <summary>
    /// 功能：取得公共 opaque ID 长度与字符集编译期正则。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>opaque ID 正则。</returns>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueIdPattern();

    /// <summary>
    /// 功能：取得 portable Provider ID 编译期正则。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>小写 Provider ID 正则。</returns>
    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderIdPattern();

    /// <summary>
    /// 功能：取得 portable tool name 编译期正则。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>安全工具名正则。</returns>
    [GeneratedRegex("^[a-z][a-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNamePattern();

    /// <summary>
    /// 功能：取得可进入报告的 第三方格式 source type 编译期正则。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>有界 source type 正则。</returns>
    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SourceTypePattern();

    /// <summary>
    /// 功能：保存严格 第三方格式 source 的 header 与按文件顺序排列的 entry。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="Header">v3 header。</param>
    /// <param name="Entries">含逐行 hash 证据的 entry。</param>
    private sealed record ParsedSource(JsonElement Header, IReadOnlyList<SourceEntry> Entries);

    /// <summary>
    /// 功能：保存一条严格 第三方格式 entry 的来源、tree 与逐行字节证据。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="LineNumber">一基 source 行号。</param>
    /// <param name="RawLine">不含 LF 的原始字节。</param>
    /// <param name="Value">严格 JSON 对象。</param>
    /// <param name="Id">source entry ID。</param>
    /// <param name="EntryType">source type。</param>
    /// <param name="ParentId">earlier parent 或 null。</param>
    /// <param name="Timestamp">原 UTC 时间。</param>
    private sealed record SourceEntry(
        int LineNumber,
        byte[] RawLine,
        JsonElement Value,
        string Id,
        string EntryType,
        string? ParentId,
        string Timestamp);

    /// <summary>
    /// 功能：保存一个 source entry 的最后目标、全部目标、disposition 与最终 kind。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="LastTargetId">后续 source child 使用的最后目标。</param>
    /// <param name="TargetIds">该 entry 产生的有序目标 IDs。</param>
    /// <param name="Disposition">报告 disposition。</param>
    /// <param name="TargetKind">最后目标 kind。</param>
    /// <param name="TargetRole">最后目标是 message.appended 时的 portable role。</param>
    private sealed record SourceMapping(
        string LastTargetId,
        IReadOnlyList<string> TargetIds,
        string Disposition,
        string TargetKind,
        string? TargetRole);

    /// <summary>
    /// 功能：集中保存单次导入的记录、映射、模型状态、报告和确定性 ID 工厂。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class ImportContext
    {
        /// <summary>
        /// 功能：初始化一次不与其他导入共享可变状态的映射上下文。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="sessionId">新目标 Session ID。</param>
        /// <param name="sourceSessionId">第三方格式 header ID。</param>
        /// <param name="sourceSha256">完整源字节 hash。</param>
        /// <param name="sourceByteLength">完整源长度。</param>
        /// <param name="workspace">portable workspace 或 conformance placeholder。</param>
        /// <param name="createdAt">header/report record 时间。</param>
        /// <param name="reportArtifactId">mandatory report artifact ID。</param>
        /// <param name="ids">生产随机或固定夹具 ID 工厂。</param>
        public ImportContext(
            string sessionId,
            string sourceSessionId,
            string sourceSha256,
            int sourceByteLength,
            string workspace,
            string createdAt,
            string reportArtifactId,
            ImportIdFactory ids)
        {
            SessionId = sessionId;
            SourceSessionId = sourceSessionId;
            SourceSha256 = sourceSha256;
            SourceByteLength = sourceByteLength;
            Workspace = workspace;
            CreatedAt = createdAt;
            ReportArtifactId = reportArtifactId;
            Ids = ids;
        }

        public string SessionId { get; }

        public string SourceSessionId { get; }

        public string SourceSha256 { get; }

        public int SourceByteLength { get; }

        public string Workspace { get; }

        public string CreatedAt { get; }

        public string ReportArtifactId { get; }

        public ImportIdFactory Ids { get; }

        public List<JsonObject> Records { get; } = [];

        public HashSet<string> RecordIds { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, SourceMapping> Mappings { get; } = new(StringComparer.Ordinal);

        public List<JsonObject> ReportEntries { get; } = [];

        public HashSet<string> Warnings { get; } = new(StringComparer.Ordinal);

        public string? SelectedHead { get; set; }

        public string? ProviderId { get; set; }

        public string? ModelId { get; set; }

        public string Thinking { get; set; } = "off";

        public int RedactedValues { get; set; }
    }

    /// <summary>
    /// 功能：为生产导入生成全新 opaque ID，并仅为固定合成夹具生成规范 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class ImportIdFactory
    {
        private readonly bool conformance;

        /// <summary>
        /// 功能：选择生产随机或固定合成夹具 ID 模式。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="conformance">仅在 source hash 已固定验证后为 true。</param>
        public ImportIdFactory(bool conformance)
        {
            this.conformance = conformance;
            ReportArtifactId = conformance ? ConformanceArtifactId : "artifact-" + Guid.NewGuid().ToString("N");
            ReportRecordId = conformance ? "record-pi-import-report" : NewId("record");
        }

        public string ReportArtifactId { get; }

        public string ReportRecordId { get; }

        /// <summary>
        /// 功能：生成一个 source entry 主目标 record ID。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="sourceId">已验证 source ID。</param>
        /// <returns>固定 fixture 或随机 record ID。</returns>
        public string Record(string sourceId)
        {
            return conformance ? "record-" + sourceId : NewId("record");
        }

        /// <summary>
        /// 功能：生成一个 source message 的 portable message ID。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="sourceId">已验证 source ID。</param>
        /// <returns>固定 fixture 或随机 message ID。</returns>
        public string Message(string sourceId)
        {
            return conformance ? "message-" + sourceId : NewId("message");
        }

        /// <summary>
        /// 功能：生成 source parent jump 的 branch.selected ID。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="sourceParentId">被重新选择的 source parent ID。</param>
        /// <returns>固定 fixture 或随机 record ID。</returns>
        public string BranchSelection(string sourceParentId)
        {
            return conformance ? "record-select-" + sourceParentId : NewId("record");
        }

        /// <summary>
        /// 功能：生成 compaction synthetic summary record ID。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="sourceId">compaction source ID。</param>
        /// <returns>固定 fixture 或随机 record ID。</returns>
        public string CompactionSummaryRecord(string sourceId)
        {
            return conformance ? "record-pi-compaction-summary" : NewId("record");
        }

        /// <summary>
        /// 功能：生成 compaction synthetic summary message ID。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="sourceId">compaction source ID。</param>
        /// <returns>固定 fixture 或随机 message ID。</returns>
        public string CompactionSummaryMessage(string sourceId)
        {
            return conformance ? "message-pi-compaction-summary" : NewId("message");
        }

        /// <summary>
        /// 功能：生成指定前缀的全新 opaque ID。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="prefix">record、message 或 artifact。</param>
        /// <returns>前缀加 32 位十六进制 GUID。</returns>
        private static string NewId(string prefix)
        {
            return prefix + "-" + Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>
    /// 功能：保持 source 只读句柄、完整字节、hash 和可复核文件身份直至目标发布。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class SourceSnapshot : IAsyncDisposable
    {
        private readonly string path;
        private readonly FileStream stream;
        private readonly SourceIdentity identity;

        /// <summary>
        /// 功能：保存持续打开的只读 stream 与首次身份快照。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="path">规范 source 路径；不会持久化或写入诊断。</param>
        /// <param name="stream">no-follow 打开的只读 stream。</param>
        /// <param name="identity">句柄身份摘要。</param>
        /// <param name="bytes">完整有界源字节。</param>
        private SourceSnapshot(string path, FileStream stream, SourceIdentity identity, byte[] bytes)
        {
            this.path = path;
            this.stream = stream;
            this.identity = identity;
            Bytes = bytes;
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        }

        public byte[] Bytes { get; }

        public string Sha256 { get; }

        /// <summary>
        /// 功能：no-follow 只读打开 source、验证普通文件、读取有界完整字节并保持句柄。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="sourcePath">调用方显式 source。</param>
        /// <param name="cancellationToken">读取取消信号。</param>
        /// <returns>直到 DisposeAsync 都持有原句柄的快照。</returns>
        public static async Task<SourceSnapshot> OpenAsync(string sourcePath, CancellationToken cancellationToken)
        {
            FileStream? stream = null;
            try
            {
                var path = Path.GetFullPath(sourcePath);
                stream = OpenReadOnlyNoFollow(path);
                var identity = CaptureIdentity(stream.SafeFileHandle);
                if (identity.Length is <= 0 or > MaxSourceBytes)
                {
                    throw InvalidSource("source_size_invalid");
                }

                var bytes = new byte[identity.Length];
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (stream.Position != identity.Length || CaptureIdentity(stream.SafeFileHandle) != identity)
                {
                    throw InvalidSource("source_changed_during_read");
                }

                var result = new SourceSnapshot(path, stream, identity, bytes);
                stream = null;
                return result;
            }
            catch (LegacySessionV3ImportException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw InvalidSource("source_open_failed");
            }
            finally
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// 功能：比较持续打开句柄与重新 no-follow 打开的当前路径身份，检测替换、大小或时间变化。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="cancellationToken">发布前取消信号。</param>
        /// <returns>身份稳定时完成的 Task。</returns>
        public async Task VerifyIdentityAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CaptureIdentity(stream.SafeFileHandle) != identity)
            {
                throw InvalidSource("source_identity_changed");
            }

            await using var current = OpenReadOnlyNoFollow(path);
            if (CaptureIdentity(current.SafeFileHandle) != identity)
            {
                throw InvalidSource("source_identity_replaced");
            }
        }

        /// <summary>
        /// 功能：释放持续打开的 source 只读句柄；从不修改 mode、内容或路径。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <returns>句柄释放完成的 ValueTask。</returns>
        public ValueTask DisposeAsync()
        {
            return stream.DisposeAsync();
        }

        /// <summary>
        /// 功能：按平台使用 O_NOFOLLOW 或安全句柄打开 source，并拒绝目录/reparse point。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="path">规范绝对 source 路径。</param>
        /// <returns>FileAccess.Read 且不共享写/删除的 stream。</returns>
        private static FileStream OpenReadOnlyNoFollow(string path)
        {
            SafeFileHandle handle;
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                var flags = UnixOpenReadOnly |
                    (OperatingSystem.IsLinux()
                        ? LinuxOpenCloseOnExec | LinuxOpenNoFollow
                        : MacOpenCloseOnExec | MacOpenNoFollow);
                var descriptor = OpenUnixSource(path, flags);
                if (descriptor < 0)
                {
                    throw InvalidSource("source_open_failed");
                }

                handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
            }
            else
            {
                var info = new FileInfo(path);
                info.Refresh();
                if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw InvalidSource("source_not_regular");
                }

                handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
            }

            try
            {
                var attributes = File.GetAttributes(handle);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    throw InvalidSource("source_not_regular");
                }

                return new FileStream(handle, FileAccess.Read, bufferSize: 16_384, isAsync: false);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 功能：从已打开句柄捕获长度、创建/修改时间、属性和 Unix mode 组成的替换检测摘要。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="handle">持续有效的 source 只读句柄。</param>
        /// <returns>不包含路径或内容的身份摘要。</returns>
        private static SourceIdentity CaptureIdentity(SafeFileHandle handle)
        {
            var attributes = File.GetAttributes(handle);
            UnixFileMode? mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(handle);
            return new SourceIdentity(
                RandomAccess.GetLength(handle),
                File.GetCreationTimeUtc(handle).Ticks,
                File.GetLastWriteTimeUtc(handle).Ticks,
                attributes,
                mode);
        }
    }

    /// <summary>
    /// 功能：保存 source 句柄可跨复核的非内容身份字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="Length">句柄长度。</param>
    /// <param name="CreationTicks">创建时间 UTC ticks。</param>
    /// <param name="LastWriteTicks">最后写时间 UTC ticks。</param>
    /// <param name="Attributes">文件属性。</param>
    /// <param name="UnixMode">Unix mode；Windows 为 null。</param>
    private readonly record struct SourceIdentity(
        long Length,
        long CreationTicks,
        long LastWriteTicks,
        FileAttributes Attributes,
        UnixFileMode? UnixMode);
}
