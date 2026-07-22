using System.Buffers;
using System.Text.Json;
using QxnmForge.Agent;
using QxnmForge.Domain;
using QxnmForge.Provider;
using QxnmForge.Session;

namespace QxnmForge.Protocol;

/// <summary>
/// 功能：表示可安全映射为 JSON-RPC error response 的请求错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ProtocolRequestException : Exception
{
    /// <summary>
    /// 功能：创建带 portable error 和可选 request ID 的协议异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="error">已脱敏 portable error。</param>
    /// <param name="requestId">可可靠关联时的 ID，否则 null。</param>
    public ProtocolRequestException(PortableError error, object? requestId = null)
        : base(error.Message)
    {
        Error = error;
        RequestId = requestId;
    }

    /// <summary>
    /// 功能：取得 wire error 对象。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public PortableError Error { get; }

    /// <summary>
    /// 功能：取得可关联原请求的 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public object? RequestId { get; }
}

/// <summary>
/// 功能：严格解析 JSON-RPC 对象、拒绝重复键并提取 text-only wire DTO。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public static class ProtocolCodec
{
    private const int MaximumInputImageBytes = 524_288;
    private const long MaxSafeInteger = 9_007_199_254_740_991;

    private static readonly SearchValues<char> MediaTypeCharacters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!#$&^_.+-");

    /// <summary>
    /// 功能：解析一帧 JSON-RPC 请求并拒绝重复键、未知顶层字段、通知和非法 ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="frame">已严格 UTF-8 解码的单帧 JSON。</param>
    /// <returns>生命周期独立的 request DTO。</returns>
    /// <remarks>不变量：opaque ID 类型和值保持原样；params 被 Clone 后不依赖 JsonDocument。</remarks>
    /// <exception cref="ProtocolRequestException">JSON 或 JSON-RPC 结构无效。</exception>
    public static JsonRpcRequest ParseRequest(string frame)
    {
        try
        {
            using var document = JsonDocument.Parse(frame, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128,
            });
            var root = document.RootElement;
            ValidateNoDuplicateKeys(root);
            RequireObject(root, "request");
            EnsureOnlyProperties(root, "jsonrpc", "id", "method", "params");
            if (RequireString(root, "jsonrpc") != "2.0")
            {
                throw InvalidRequest("jsonrpc must equal 2.0");
            }

            var id = RequireProperty(root, "id");
            ValidateRequestId(id);
            var method = RequireString(root, "method");
            var parameters = RequireProperty(root, "params");
            RequireObject(parameters, "params");
            return new JsonRpcRequest(id.Clone(), method, parameters.Clone());
        }
        catch (ProtocolRequestException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new ProtocolRequestException(
                new PortableError(-32700, "parse error", false, new ErrorDetails("parse_error")),
                null);
        }
    }

    /// <summary>
    /// 功能：验证 initialize 参数、确认客户端提供协议 0.1，并读取真实交互审批能力。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">initialize params。</param>
    /// <returns>选中的协议版本 0.1 与 interactiveApprovals 协商值。</returns>
    /// <exception cref="ProtocolRequestException">参数结构无效或版本不兼容。</exception>
    public static (string ProtocolVersion, bool InteractiveApprovals) ParseInitialize(JsonElement parameters)
    {
        EnsureOnlyProperties(parameters, "protocolVersions", "client", "capabilities");
        var versions = RequireProperty(parameters, "protocolVersions");
        if (versions.ValueKind != JsonValueKind.Array || versions.GetArrayLength() is < 1 or > 16)
        {
            throw InvalidParams("protocolVersions is invalid", "protocolVersions");
        }

        var offered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var version in versions.EnumerateArray())
        {
            if (version.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(version.GetString()) || !offered.Add(version.GetString()!))
            {
                throw InvalidParams("protocolVersions is invalid", "protocolVersions");
            }
        }

        var client = RequireProperty(parameters, "client");
        RequireObject(client, "client");
        EnsureOnlyProperties(client, "name", "version");
        _ = RequireString(client, "name");
        _ = RequireString(client, "version");
        var capabilities = RequireProperty(parameters, "capabilities");
        RequireObject(capabilities, "capabilities");
        EnsureOnlyProperties(capabilities, "eventReplay", "interactiveApprovals", "terminalEvents");
        if (capabilities.TryGetProperty("eventReplay", out var eventReplay) &&
            eventReplay.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw InvalidParams("eventReplay must be a boolean", "capabilities.eventReplay");
        }

        if (capabilities.TryGetProperty("terminalEvents", out var terminalEvents) &&
            terminalEvents.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw InvalidParams("terminalEvents must be a boolean", "capabilities.terminalEvents");
        }

        var interactiveApprovals = false;
        if (capabilities.TryGetProperty("interactiveApprovals", out var interactive))
        {
            if (interactive.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw InvalidParams(
                    "interactiveApprovals must be a boolean",
                    "capabilities.interactiveApprovals");
            }

            interactiveApprovals = interactive.GetBoolean();
        }

        if (!offered.Contains("0.1"))
        {
            throw new ProtocolRequestException(new PortableError(
                -32001,
                "incompatible protocol",
                false,
                new ErrorDetails("incompatible_protocol", SupportedVersions: ["0.1"])));
        }

        return ("0.1", interactiveApprovals);
    }

    /// <summary>
    /// 功能：解析 faux/configure 的 session ID 和场景 JSON。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">方法 params。</param>
    /// <returns>session ID 与场景元素。</returns>
    /// <exception cref="ProtocolRequestException">字段缺失、未知或类型错误。</exception>
    public static (string SessionId, JsonElement Scenario) ParseFauxConfigure(JsonElement parameters)
    {
        EnsureOnlyProperties(parameters, "sessionId", "scenario");
        return (RequireOpaqueId(parameters, "sessionId"), RequireProperty(parameters, "scenario").Clone());
    }

    /// <summary>
    /// 功能：严格解析并解码 artifacts/create 的同 Session 图片输入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">只含 sessionId、mediaType 与 canonical dataBase64 的对象。</param>
    /// <returns>opaque Session ID、受支持 MIME 与最多 512 KiB 的已验签名字节。</returns>
    /// <remarks>不变量：Base64 正文、解码字节和摘要不进入错误、日志或返回 DTO。</remarks>
    /// <exception cref="ProtocolRequestException">字段、Base64、大小、MIME 或魔数无效。</exception>
    public static (string SessionId, string MediaType, byte[] Bytes) ParseArtifactCreate(
        JsonElement parameters)
    {
        EnsureOnlyProperties(parameters, "sessionId", "mediaType", "dataBase64");
        var sessionId = RequireOpaqueId(parameters, "sessionId");
        var mediaType = RequireString(parameters, "mediaType");
        var encoded = RequireString(parameters, "dataBase64");
        if (encoded.Length > 699_052 || encoded.Any(char.IsWhiteSpace))
        {
            throw InvalidParams("artifact image data is invalid", "dataBase64");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw InvalidParams("artifact image data is invalid", "dataBase64");
        }

        if (bytes.Length is < 1 or > MaximumInputImageBytes ||
            !string.Equals(Convert.ToBase64String(bytes), encoded, StringComparison.Ordinal) ||
            !ImageArtifactValidation.IsSupportedMediaType(mediaType) ||
            !ImageArtifactValidation.HasMatchingSignature(mediaType, bytes))
        {
            throw InvalidParams("artifact image data is invalid", "dataBase64");
        }

        return (sessionId, mediaType, bytes);
    }

    /// <summary>
    /// 功能：解析支持显式 family 与 portable image_ref 的 run/start 参数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">run/start params。</param>
    /// <returns>session、输入、Provider 选择与可选 Agent Profile 引用。</returns>
    /// <exception cref="ProtocolRequestException">参数违反核心 schema、引用字段或 family 语法。</exception>
    public static (
        string SessionId,
        InputMessage Input,
        ProviderSelection Provider,
        AgentProfileReference? AgentProfile) ParseRunStart(JsonElement parameters)
    {
        EnsureOnlyProperties(
            parameters,
            "sessionId",
            "input",
            "provider",
            "agentProfile",
            "options",
            "extensions");
        var sessionId = RequireOpaqueId(parameters, "sessionId");
        var input = ParseInput(RequireProperty(parameters, "input"));
        var providerElement = RequireProperty(parameters, "provider");
        RequireObject(providerElement, "provider");
        EnsureOnlyProperties(providerElement, "id", "modelId", "apiFamily", "extensions");
        var apiFamily = ParseOptionalApiFamily(providerElement);
        var provider = new ProviderSelection(
            RequireString(providerElement, "id"),
            RequireString(providerElement, "modelId"),
            apiFamily);
        AgentProfileReference? agentProfile = null;
        if (parameters.TryGetProperty("agentProfile", out var profileElement))
        {
            try
            {
                agentProfile = ParseAgentProfileReference(profileElement);
            }
            catch (ProtocolRequestException)
            {
                throw AgentProfileException.Invalid();
            }
        }

        return (sessionId, input, provider, agentProfile);
    }

    /// <summary>
    /// 功能：解析 run/cancel 的 sessionId 与 runId。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">方法 params。</param>
    /// <returns>两个 opaque ID。</returns>
    public static (string SessionId, string RunId) ParseRunReference(JsonElement parameters)
    {
        EnsureOnlyProperties(parameters, "sessionId", "runId");
        return (RequireOpaqueId(parameters, "sessionId"), RequireOpaqueId(parameters, "runId"));
    }

    /// <summary>
    /// 功能：严格解析 approval/respond 的 run 引用、单次 approval ID 与 allow_once/deny 决议。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">approval/respond params。</param>
    /// <returns>session、run、approval ID 和受限审批决议。</returns>
    /// <exception cref="ProtocolRequestException">字段缺失、未知、类型错误或 choice 不受支持。</exception>
    public static (
        string SessionId,
        string RunId,
        string ApprovalId,
        Agent.ApprovalDecision Decision) ParseApprovalResponse(JsonElement parameters)
    {
        EnsureOnlyProperties(parameters, "sessionId", "runId", "approvalId", "decision");
        var decisionElement = RequireProperty(parameters, "decision");
        RequireObject(decisionElement, "decision");
        EnsureOnlyProperties(decisionElement, "choice", "reason", "extensions");
        var choice = RequireString(decisionElement, "choice");
        if (choice is not ("allow_once" or "deny"))
        {
            throw InvalidParams("approval choice is unsupported", "decision.choice");
        }

        string? reason = null;
        if (decisionElement.TryGetProperty("reason", out var reasonElement))
        {
            if (reasonElement.ValueKind != JsonValueKind.String || reasonElement.GetString()!.Length > 4096)
            {
                throw InvalidParams("approval reason is invalid", "decision.reason");
            }

            reason = reasonElement.GetString();
        }

        return (
            RequireOpaqueId(parameters, "sessionId"),
            RequireOpaqueId(parameters, "runId"),
            RequireOpaqueId(parameters, "approvalId"),
            new Agent.ApprovalDecision(choice, reason));
    }

    /// <summary>
    /// 功能：解析 steer/follow-up 的 run 引用和 user 输入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">方法 params。</param>
    /// <returns>session、run 和输入 DTO。</returns>
    public static (string SessionId, string RunId, InputMessage Input) ParseQueue(JsonElement parameters)
    {
        EnsureOnlyProperties(parameters, "sessionId", "runId", "input");
        return (
            RequireOpaqueId(parameters, "sessionId"),
            RequireOpaqueId(parameters, "runId"),
            ParseInput(RequireProperty(parameters, "input")));
    }

    /// <summary>
    /// 功能：解析 session/get 的 sessionId 与可选非负事件 afterSeq。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">session/get params。</param>
    /// <returns>session ID 和事件过滤下界；省略 afterSeq 时为零。</returns>
    /// <exception cref="ProtocolRequestException">字段未知、ID 无效或 afterSeq 不是安全整数。</exception>
    public static (string SessionId, long AfterSeq) ParseSessionGet(JsonElement parameters)
    {
        EnsureOnlyProperties(parameters, "sessionId", "afterSeq");
        var sessionId = RequireOpaqueId(parameters, "sessionId");
        if (!parameters.TryGetProperty("afterSeq", out var afterSeq))
        {
            return (sessionId, 0);
        }

        if (!afterSeq.TryGetInt64(out var value) || value is < 0 or > MaxSafeInteger)
        {
            throw InvalidParams("afterSeq must be a non-negative safe integer", "afterSeq");
        }

        return (sessionId, value);
    }

    /// <summary>
    /// 功能：严格解析 session/list 的可选 opaque cursor 与有界 page limit。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">只允许 cursor 与 limit 的方法 params。</param>
    /// <returns>零基 offset 与 1..128 的 page limit；省略时为 0、64。</returns>
    /// <exception cref="ProtocolRequestException">字段未知、类型错误或 cursor 不可解析。</exception>
    public static (int Offset, int Limit) ParseSessionList(JsonElement parameters)
    {
        EnsureOnlyProperties(parameters, "cursor", "limit");
        var offset = 0;
        if (parameters.TryGetProperty("cursor", out var cursorElement))
        {
            if (cursorElement.ValueKind != JsonValueKind.String)
            {
                throw InvalidParams("cursor is invalid", "cursor");
            }

            var cursor = cursorElement.GetString()!;
            if (!cursor.StartsWith("v1:", StringComparison.Ordinal) ||
                cursor.Length is < 4 or > 12 ||
                !int.TryParse(
                    cursor.AsSpan(3),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out offset) ||
                offset is < 0 or > 16_384)
            {
                throw InvalidParams("cursor is invalid", "cursor");
            }
        }

        var limit = 64;
        if (parameters.TryGetProperty("limit", out var limitElement) &&
            (limitElement.ValueKind != JsonValueKind.Number ||
             !limitElement.TryGetInt32(out limit) ||
             limit is < 1 or > 128))
        {
            throw InvalidParams("limit is invalid", "limit");
        }

        return (offset, limit);
    }

    /// <summary>
    /// 功能：严格解析 archive、restore 或 delete 的唯一 Session 引用。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">只允许 sessionId 的方法 params。</param>
    /// <returns>符合 portable opaque ID 语法的 Session ID。</returns>
    /// <exception cref="ProtocolRequestException">字段缺失、未知或 ID 无效。</exception>
    public static string ParseSessionLifecycleMutation(JsonElement parameters)
    {
        EnsureOnlyProperties(parameters, "sessionId");
        return RequireOpaqueId(parameters, "sessionId");
    }

    /// <summary>
    /// 功能：严格解析 <c>models/list</c> 的可选 Provider 过滤器。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">只允许可选 providerId 的 params 对象。</param>
    /// <returns>未提供时为 null；否则为品牌中立、大小写敏感 Provider ID。</returns>
    /// <exception cref="ProtocolRequestException">未知字段、类型、长度或字符集无效。</exception>
    public static string? ParseModelsList(JsonElement parameters)
    {
        EnsureOnlyProperties(parameters, "providerId");
        if (!parameters.TryGetProperty("providerId", out var providerId))
        {
            return null;
        }

        if (providerId.ValueKind != JsonValueKind.String)
        {
            throw InvalidParams("models/list providerId must be a string", "providerId");
        }

        var value = providerId.GetString()!;
        if (value.Length is < 1 or > 128 ||
            !(value[0] is >= 'a' and <= 'z' or >= '0' and <= '9') ||
            value.Any(static character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.')))
        {
            throw InvalidParams("models/list providerId is invalid", "providerId");
        }

        return value;
    }

    /// <summary>
    /// 功能：严格验证 providerCatalog/list 只接受空 params。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">必须是不含字段的 JSON object。</param>
    /// <remarks>不变量：解析不读取 Provider 环境、credential、网络或目录内容。</remarks>
    /// <exception cref="ProtocolRequestException">包含任何未知字段。</exception>
    public static void ParseProviderCatalogList(JsonElement parameters)
    {
        EnsureOnlyProperties(parameters);
    }

    /// <summary>
    /// 功能：严格验证 providerConnections/list 只接受空 params。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">方法 params。</param>
    /// <exception cref="ProtocolRequestException">包含任何未知字段。</exception>
    public static void ParseProviderConnectionsList(JsonElement parameters)
    {
        EnsureOnlyProperties(parameters);
    }

    /// <summary>
    /// 功能：严格解析 providerConnections/create 的完整非敏感连接输入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">只允许 connection 的 params。</param>
    /// <returns>不含 credential 的连接输入。</returns>
    /// <exception cref="ProtocolRequestException">字段缺失、未知或类型错误。</exception>
    public static ProviderConnectionInput ParseProviderConnectionsCreate(JsonElement parameters)
    {
        EnsureOnlyProperties(parameters, "connection");
        return ParseProviderConnectionInput(RequireProperty(parameters, "connection"));
    }

    /// <summary>
    /// 功能：严格解析 providerConnections/update 的连接 ID、CAS revision 与完整输入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">方法 params。</param>
    /// <returns>connectionId、expectedRevision 与完整非敏感输入。</returns>
    /// <exception cref="ProtocolRequestException">字段缺失、未知、类型或 revision 错误。</exception>
    public static (
        string ConnectionId,
        long ExpectedRevision,
        ProviderConnectionInput Connection) ParseProviderConnectionsUpdate(JsonElement parameters)
    {
        EnsureOnlyProperties(parameters, "connectionId", "expectedRevision", "connection");
        return (
            RequireString(parameters, "connectionId"),
            RequirePositiveSafeInteger(parameters, "expectedRevision"),
            ParseProviderConnectionInput(RequireProperty(parameters, "connection")));
    }

    /// <summary>
    /// 功能：严格解析 providerConnections/delete 的连接 ID 与 CAS revision。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">方法 params。</param>
    /// <returns>connectionId 与 expectedRevision。</returns>
    /// <exception cref="ProtocolRequestException">字段缺失、未知、类型或 revision 错误。</exception>
    public static (string ConnectionId, long ExpectedRevision) ParseProviderConnectionsDelete(
        JsonElement parameters)
    {
        EnsureOnlyProperties(parameters, "connectionId", "expectedRevision");
        return (
            RequireString(parameters, "connectionId"),
            RequirePositiveSafeInteger(parameters, "expectedRevision"));
    }

    /// <summary>
    /// 功能：严格解析 providerConnections/discoverModels 的连接 ID 与 CAS revision。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">只允许 connectionId 与 expectedRevision 的 params。</param>
    /// <returns>目标 connectionId 与用户确认的 expectedRevision。</returns>
    /// <exception cref="ProtocolRequestException">字段缺失、未知、类型或 revision 错误。</exception>
    public static (string ConnectionId, long ExpectedRevision) ParseProviderConnectionsDiscoverModels(
        JsonElement parameters)
    {
        EnsureOnlyProperties(parameters, "connectionId", "expectedRevision");
        return (
            RequireString(parameters, "connectionId"),
            RequirePositiveSafeInteger(parameters, "expectedRevision"));
    }

    /// <summary>
    /// 功能：严格解析 providerCredentials/set 的 Provider ID 与瞬时 credential。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">方法 params。</param>
    /// <returns>Provider ID、封闭 credential 用途与只应传入 CredentialStore 的明文 credential。</returns>
    /// <remarks>不变量：解析器不持久化、记录或复制 credential 到任何 DTO 之外。</remarks>
    /// <exception cref="ProtocolRequestException">字段缺失、未知、类型错误或空 credential。</exception>
    public static (string ProviderId, string CredentialKind, string Credential) ParseProviderCredentialsSet(
        JsonElement parameters)
    {
        EnsureOnlyProperties(parameters, "providerId", "credentialKind", "credential");
        return (
            RequireString(parameters, "providerId"),
            RequireString(parameters, "credentialKind"),
            RequireString(parameters, "credential"));
    }

    /// <summary>
    /// 功能：严格解析 providerCredentials/remove 的 Provider ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">方法 params。</param>
    /// <returns>待清理 credential 的 Provider ID 与封闭用途。</returns>
    /// <exception cref="ProtocolRequestException">字段缺失、未知或类型错误。</exception>
    public static (string ProviderId, string CredentialKind) ParseProviderCredentialsRemove(
        JsonElement parameters)
    {
        EnsureOnlyProperties(parameters, "providerId", "credentialKind");
        return (
            RequireString(parameters, "providerId"),
            RequireString(parameters, "credentialKind"));
    }

    /// <summary>
    /// 功能：严格验证 agentProfiles/list 只接受空 params。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">方法 params。</param>
    /// <exception cref="ProtocolRequestException">包含任何未知字段。</exception>
    public static void ParseAgentProfilesList(JsonElement parameters)
    {
        EnsureOnlyProperties(parameters);
    }

    /// <summary>
    /// 功能：严格解析 agentProfiles/create 的完整 profile 输入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">方法 params。</param>
    /// <returns>尚需服务层规范化和语义校验的输入。</returns>
    /// <exception cref="AgentProfileException">结构、字段或类型不符合共同 schema。</exception>
    public static AgentProfileInput ParseAgentProfilesCreate(JsonElement parameters)
    {
        try
        {
            EnsureOnlyProperties(parameters, "profile");
            return ParseAgentProfileInput(RequireProperty(parameters, "profile"));
        }
        catch (ProtocolRequestException)
        {
            throw AgentProfileException.Invalid();
        }
    }

    /// <summary>
    /// 功能：严格解析 agentProfiles/update 的 CAS 引用与完整替换输入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">方法 params。</param>
    /// <returns>profile ID、expected revision 与完整输入。</returns>
    /// <exception cref="AgentProfileException">结构、字段或类型不符合共同 schema。</exception>
    public static (
        string ProfileId,
        long ExpectedRevision,
        AgentProfileInput Profile) ParseAgentProfilesUpdate(JsonElement parameters)
    {
        try
        {
            EnsureOnlyProperties(parameters, "profileId", "expectedRevision", "profile");
            return (
                RequireOpaqueId(parameters, "profileId"),
                RequirePositiveSafeInteger(parameters, "expectedRevision"),
                ParseAgentProfileInput(RequireProperty(parameters, "profile")));
        }
        catch (ProtocolRequestException)
        {
            throw AgentProfileException.Invalid();
        }
    }

    /// <summary>
    /// 功能：严格解析 agentProfiles/delete 的 CAS 引用。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">方法 params。</param>
    /// <returns>profile ID 与 expected revision。</returns>
    /// <exception cref="AgentProfileException">结构、字段或类型不符合共同 schema。</exception>
    public static (string ProfileId, long ExpectedRevision) ParseAgentProfilesDelete(
        JsonElement parameters)
    {
        try
        {
            EnsureOnlyProperties(parameters, "profileId", "expectedRevision");
            return (
                RequireOpaqueId(parameters, "profileId"),
                RequirePositiveSafeInteger(parameters, "expectedRevision"));
        }
        catch (ProtocolRequestException)
        {
            throw AgentProfileException.Invalid();
        }
    }

    /// <summary>
    /// 功能：严格解析 session/branch/select 的 Session、expected head 与 earlier target ID。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">branch selection params 对象。</param>
    /// <returns>三个保持 opaque identity 的 ID。</returns>
    /// <remarks>不变量：解析不读取 journal，也不从 ID 文本推断先后关系。</remarks>
    /// <exception cref="ProtocolRequestException">字段缺失、未知或不符合 opaque ID 语法。</exception>
    public static (
        string SessionId,
        string ExpectedHeadRecordId,
        string TargetLeafRecordId) ParseSessionBranchSelect(JsonElement parameters)
    {
        EnsureOnlyProperties(
            parameters,
            "sessionId",
            "expectedHeadRecordId",
            "targetLeafRecordId");
        return (
            RequireOpaqueId(parameters, "sessionId"),
            RequireOpaqueId(parameters, "expectedHeadRecordId"),
            RequireOpaqueId(parameters, "targetLeafRecordId"));
    }

    /// <summary>
    /// 功能：严格解析 session/compact 的 summary、Provider、usage、token 估算与策略。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parameters">compaction params 对象。</param>
    /// <returns>Session ID 与保留 Provider/usage extensions 的 mutation command。</returns>
    /// <remarks>不变量：tokensAfter 与 tokensBefore 的大小关系留给原子 mutation 层，以返回冻结 invalid_compaction_tokens。</remarks>
    /// <exception cref="ProtocolRequestException">字段形状、长度、Provider 或 usage 违反公共 Schema。</exception>
    public static (string SessionId, SessionCompactionCommand Command) ParseSessionCompact(
        JsonElement parameters)
    {
        EnsureOnlyProperties(
            parameters,
            "sessionId",
            "expectedHeadRecordId",
            "firstRetainedRecordId",
            "summaryText",
            "provider",
            "usage",
            "tokensBefore",
            "tokensAfter",
            "strategy");
        var sessionId = RequireOpaqueId(parameters, "sessionId");
        var expectedHeadRecordId = RequireOpaqueId(parameters, "expectedHeadRecordId");
        var firstRetainedRecordId = RequireOpaqueId(parameters, "firstRetainedRecordId");
        var summaryText = RequireString(parameters, "summaryText");
        if (summaryText.Length > 262_144)
        {
            throw InvalidParams("summaryText is too long", "summaryText");
        }

        var provider = ParseProviderSelection(RequireProperty(parameters, "provider"));
        var usage = ParseUsage(RequireProperty(parameters, "usage"));
        var tokensBefore = RequireNonNegativeSafeInteger(parameters, "tokensBefore");
        var tokensAfter = RequireNonNegativeSafeInteger(parameters, "tokensAfter");
        var strategy = RequireString(parameters, "strategy");
        if (strategy.Length > 128)
        {
            throw InvalidParams("strategy is too long", "strategy");
        }

        return (
            sessionId,
            new SessionCompactionCommand(
                expectedHeadRecordId,
                firstRetainedRecordId,
                summaryText,
                provider,
                usage,
                tokensBefore,
                tokensAfter,
                strategy));
    }

    /// <summary>
    /// 功能：创建稳定 invalid request 异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="message">不含用户原文的安全消息。</param>
    /// <returns>错误码 -32600 的异常。</returns>
    public static ProtocolRequestException InvalidRequest(string message)
    {
        return new ProtocolRequestException(new PortableError(
            -32600,
            message,
            false,
            new ErrorDetails("invalid_request")));
    }

    /// <summary>
    /// 功能：创建稳定 invalid params 异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="message">安全消息。</param>
    /// <param name="field">可选字段名。</param>
    /// <returns>错误码 -32602 的异常。</returns>
    public static ProtocolRequestException InvalidParams(string message, string? field = null)
    {
        return new ProtocolRequestException(new PortableError(
            -32602,
            message,
            false,
            new ErrorDetails("invalid_params", Field: field)));
    }

    /// <summary>
    /// 功能：验证并克隆公共 providerSelection，保留显式 extensions 命名空间。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">Provider selection JSON 对象。</param>
    /// <returns>生命周期独立且可直接写入 summary message 的克隆。</returns>
    /// <exception cref="ProtocolRequestException">Provider ID、model ID、字段集合或 extensions 无效。</exception>
    private static JsonElement ParseProviderSelection(JsonElement element)
    {
        RequireObject(element, "provider");
        EnsureOnlyProperties(element, "id", "modelId", "apiFamily", "extensions");
        var providerId = RequireString(element, "id");
        if (providerId.Length > 128 ||
            !(providerId[0] is >= 'a' and <= 'z' or >= '0' and <= '9') ||
            providerId.Any(static character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-')))
        {
            throw InvalidParams("provider.id is invalid", "provider.id");
        }

        var modelId = RequireString(element, "modelId");
        if (modelId.Length > 256)
        {
            throw InvalidParams("provider.modelId is too long", "provider.modelId");
        }

        _ = ParseOptionalApiFamily(element);

        if (element.TryGetProperty("extensions", out var extensions))
        {
            RequireObject(extensions, "provider.extensions");
        }

        return element.Clone();
    }

    /// <summary>
    /// 功能：解析 closed Provider 连接输入对象，并保留非敏感文本供服务层语义校验。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">connection JSON 对象。</param>
    /// <returns>不含 credential 的完整连接输入。</returns>
    /// <exception cref="ProtocolRequestException">字段缺失、未知、类型错误或数组越界。</exception>
    private static ProviderConnectionInput ParseProviderConnectionInput(JsonElement element)
    {
        RequireObject(element, "connection");
        EnsureOnlyProperties(
            element,
            "displayName",
            "providerId",
            "apiFamily",
            "baseUrl",
            "modelsUrl",
            "modelIds",
            "supportsTools",
            "logoAssetId",
            "enabled");
        var modelIdsElement = RequireProperty(element, "modelIds");
        if (modelIdsElement.ValueKind != JsonValueKind.Array ||
            modelIdsElement.GetArrayLength() > 512)
        {
            throw InvalidParams("modelIds is invalid", "connection.modelIds");
        }

        var modelIds = new List<string>(modelIdsElement.GetArrayLength());
        foreach (var modelId in modelIdsElement.EnumerateArray())
        {
            if (modelId.ValueKind != JsonValueKind.String)
            {
                throw InvalidParams("modelIds is invalid", "connection.modelIds");
            }

            modelIds.Add(modelId.GetString()!);
        }

        var logoElement = RequireProperty(element, "logoAssetId");
        string? logoAssetId;
        if (logoElement.ValueKind == JsonValueKind.Null)
        {
            logoAssetId = null;
        }
        else if (logoElement.ValueKind == JsonValueKind.String)
        {
            logoAssetId = logoElement.GetString();
        }
        else
        {
            throw InvalidParams("logoAssetId is invalid", "connection.logoAssetId");
        }

        return new ProviderConnectionInput(
            RequireString(element, "displayName"),
            RequireString(element, "providerId"),
            RequireString(element, "apiFamily"),
            RequireString(element, "baseUrl"),
            RequireString(element, "modelsUrl"),
            modelIds,
            RequireBoolean(element, "supportsTools"),
            logoAssetId,
            RequireBoolean(element, "enabled"));
    }

    /// <summary>
    /// 功能：解析 closed Agent Profile 输入对象及全部嵌套 closed 对象。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">profile JSON 对象。</param>
    /// <returns>保持原始文本、供服务层规范化的 DTO。</returns>
    /// <exception cref="ProtocolRequestException">字段缺失、未知、类型错误或数组越界。</exception>
    private static AgentProfileInput ParseAgentProfileInput(JsonElement element)
    {
        RequireObject(element, "profile");
        EnsureOnlyProperties(
            element,
            "displayName",
            "description",
            "enabled",
            "instructions",
            "model",
            "requestedToolIds",
            "dangerousActionMode",
            "behavior");
        var model = RequireProperty(element, "model");
        RequireObject(model, "profile.model");
        EnsureOnlyProperties(model, "providerId", "modelId", "apiFamily");
        var requestedTools = RequireProperty(element, "requestedToolIds");
        if (requestedTools.ValueKind != JsonValueKind.Array || requestedTools.GetArrayLength() > 256)
        {
            throw InvalidParams("requestedToolIds is invalid", "profile.requestedToolIds");
        }

        var toolIds = new List<string>(requestedTools.GetArrayLength());
        foreach (var toolId in requestedTools.EnumerateArray())
        {
            if (toolId.ValueKind != JsonValueKind.String)
            {
                throw InvalidParams("requestedToolIds is invalid", "profile.requestedToolIds");
            }

            toolIds.Add(toolId.GetString()!);
        }

        var behavior = RequireProperty(element, "behavior");
        RequireObject(behavior, "profile.behavior");
        EnsureOnlyProperties(behavior, "responseStyle", "planFirst", "reviewChanges");
        return new AgentProfileInput(
            RequireString(element, "displayName"),
            RequireString(element, "description", allowEmpty: true),
            RequireBoolean(element, "enabled"),
            RequireString(element, "instructions"),
            new AgentProfileModel(
                RequireString(model, "providerId"),
                RequireString(model, "modelId"),
                RequireString(model, "apiFamily")),
            toolIds,
            RequireString(element, "dangerousActionMode"),
            new AgentProfileBehavior(
                RequireString(behavior, "responseStyle"),
                RequireBoolean(behavior, "planFirst"),
                RequireBoolean(behavior, "reviewChanges")));
    }

    /// <summary>
    /// 功能：解析 closed Agent Profile revision 引用。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">agentProfile JSON 对象。</param>
    /// <returns>已验证 opaque ID 与正安全整数 revision。</returns>
    /// <exception cref="ProtocolRequestException">字段缺失、未知或类型无效。</exception>
    private static AgentProfileReference ParseAgentProfileReference(JsonElement element)
    {
        RequireObject(element, "agentProfile");
        EnsureOnlyProperties(element, "profileId", "revision");
        return new AgentProfileReference(
            RequireOpaqueId(element, "profileId"),
            RequirePositiveSafeInteger(element, "revision"));
    }

    /// <summary>
    /// 功能：验证并克隆规范 usage，包括可选缓存、推理、费用与 extensions 字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">usage JSON 对象。</param>
    /// <returns>保留所有规范可选字段的独立克隆。</returns>
    /// <exception cref="ProtocolRequestException">token、费用或字段形状违反公共 usage Schema。</exception>
    private static JsonElement ParseUsage(JsonElement element)
    {
        RequireObject(element, "usage");
        EnsureOnlyProperties(
            element,
            "inputTokens",
            "outputTokens",
            "totalTokens",
            "cachedInputTokens",
            "cacheWriteTokens",
            "reasoningTokens",
            "cost",
            "extensions");
        _ = RequireNonNegativeSafeInteger(element, "inputTokens");
        _ = RequireNonNegativeSafeInteger(element, "outputTokens");
        _ = RequireNonNegativeSafeInteger(element, "totalTokens");
        foreach (var optionalName in new[] { "cachedInputTokens", "cacheWriteTokens", "reasoningTokens" })
        {
            if (element.TryGetProperty(optionalName, out _))
            {
                _ = RequireNonNegativeSafeInteger(element, optionalName);
            }
        }

        if (element.TryGetProperty("cost", out var cost))
        {
            RequireObject(cost, "usage.cost");
            EnsureOnlyProperties(cost, "currency", "amount");
            var currency = RequireString(cost, "currency");
            var amount = RequireString(cost, "amount");
            if (currency.Length != 3 || currency.Any(static character => character is < 'A' or > 'Z'))
            {
                throw InvalidParams("usage.cost.currency is invalid", "usage.cost.currency");
            }

            if (!IsCanonicalMoneyAmount(amount))
            {
                throw InvalidParams("usage.cost.amount is invalid", "usage.cost.amount");
            }
        }

        if (element.TryGetProperty("extensions", out var extensions))
        {
            RequireObject(extensions, "usage.extensions");
        }

        return element.Clone();
    }

    /// <summary>
    /// 功能：读取公共 nonNegativeSafeInteger 并拒绝负数、浮点和 JavaScript 非安全整数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">包含目标字段的对象。</param>
    /// <param name="name">目标字段名。</param>
    /// <returns>范围内 Int64。</returns>
    /// <exception cref="ProtocolRequestException">字段缺失或不是非负安全整数。</exception>
    private static long RequireNonNegativeSafeInteger(JsonElement element, string name)
    {
        var property = RequireProperty(element, name);
        if (!property.TryGetInt64(out var value) || value is < 0 or > MaxSafeInteger)
        {
            throw InvalidParams(name + " must be a non-negative safe integer", name);
        }

        return value;
    }

    /// <summary>
    /// 功能：判断费用 amount 是否为无符号规范十进制且小数位不超过十二位。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">待检查的 JSON 字符串值。</param>
    /// <returns>符合 usage.money pattern 时为 true。</returns>
    private static bool IsCanonicalMoneyAmount(string value)
    {
        var dot = value.IndexOf('.');
        var integerPart = dot < 0 ? value : value[..dot];
        var fractionPart = dot < 0 ? null : value[(dot + 1)..];
        if (integerPart.Length == 0 ||
            (integerPart.Length > 1 && integerPart[0] == '0') ||
            integerPart.Any(static character => character is < '0' or > '9'))
        {
            return false;
        }

        return fractionPart is null ||
            (fractionPart.Length is >= 1 and <= 12 &&
             fractionPart.All(static character => character is >= '0' and <= '9'));
    }

    /// <summary>
    /// 功能：解析 portable user text/image_ref 输入并严格验证 artifact 元数据。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">input 对象。</param>
    /// <returns>输入 DTO。</returns>
    private static InputMessage ParseInput(JsonElement element)
    {
        RequireObject(element, "input");
        EnsureOnlyProperties(element, "role", "content", "extensions");
        if (RequireString(element, "role") != "user")
        {
            throw InvalidParams("input.role must equal user", "input.role");
        }

        var contentElement = RequireProperty(element, "content");
        if (contentElement.ValueKind != JsonValueKind.Array || contentElement.GetArrayLength() is < 1 or > 128)
        {
            throw InvalidParams("input.content is invalid", "input.content");
        }

        var content = new List<MessageContent>();
        foreach (var item in contentElement.EnumerateArray())
        {
            RequireObject(item, "input content");
            var type = RequireString(item, "type");
            if (type == "text")
            {
                EnsureOnlyProperties(item, "type", "text", "extensions");
                content.Add(new TextContent(
                    RequireString(item, "text", allowEmpty: true),
                    CloneOptionalObject(item, "extensions", "input content extensions")));
                continue;
            }

            if (type == "image_ref")
            {
                EnsureOnlyProperties(item, "type", "artifact", "alt", "extensions");
                string? alt = null;
                if (item.TryGetProperty("alt", out var altElement))
                {
                    if (altElement.ValueKind != JsonValueKind.String || altElement.GetString()!.Length > 4096)
                    {
                        throw InvalidParams("image_ref.alt is invalid", "input.content.alt");
                    }

                    alt = altElement.GetString();
                }

                content.Add(new ImageReferenceContent(
                    ParseArtifactReference(RequireProperty(item, "artifact")),
                    alt,
                    CloneOptionalObject(item, "extensions", "input content extensions")));
                continue;
            }

            throw InvalidParams("input content type is not implemented", "input.content.type");
        }

        return new InputMessage("user", content);
    }

    /// <summary>
    /// 功能：解析可选 apiFamily 并应用品牌中立 family 标识符语法。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="provider">providerSelection 对象。</param>
    /// <returns>字段缺失时为 null，否则返回已验证值。</returns>
    /// <exception cref="ProtocolRequestException">值不是小写 ASCII family 标识符。</exception>
    private static string? ParseOptionalApiFamily(JsonElement provider)
    {
        if (!provider.TryGetProperty("apiFamily", out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw InvalidParams("provider.apiFamily must be a string", "provider.apiFamily");
        }

        var value = element.GetString()!;
        if (value.Length is < 1 or > 128 ||
            !(value[0] is >= 'a' and <= 'z' or >= '0' and <= '9') ||
            value.Any(static character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
        {
            throw InvalidParams("provider.apiFamily is invalid", "provider.apiFamily");
        }

        return value;
    }

    /// <summary>
    /// 功能：严格解析 portable artifact reference，不接受路径、URL、宽松 hash 或未知字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">image_ref.artifact 对象。</param>
    /// <returns>仅含 ID、MIME、长度、摘要与显式扩展的引用。</returns>
    /// <remarks>不变量：返回值不含 artifact 字节或 host path。</remarks>
    /// <exception cref="ProtocolRequestException">任一字段违反 artifact Schema。</exception>
    private static ArtifactReference ParseArtifactReference(JsonElement element)
    {
        RequireObject(element, "artifact");
        EnsureOnlyProperties(
            element,
            "artifactId",
            "mediaType",
            "byteLength",
            "sha256",
            "displayName",
            "extensions");
        var artifactId = RequireOpaqueId(element, "artifactId");
        var mediaType = RequireString(element, "mediaType");
        if (!IsValidMediaType(mediaType))
        {
            throw InvalidParams("artifact mediaType is invalid", "mediaType");
        }

        var byteLength = RequireNonNegativeSafeInteger(element, "byteLength");
        var sha256 = RequireString(element, "sha256");
        if (sha256.Length != 64 || sha256.Any(static character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw InvalidParams("artifact sha256 is invalid", "sha256");
        }

        string? displayName = null;
        if (element.TryGetProperty("displayName", out var displayNameElement))
        {
            if (displayNameElement.ValueKind != JsonValueKind.String ||
                displayNameElement.GetString()!.Length is < 1 or > 256)
            {
                throw InvalidParams("artifact displayName is invalid", "displayName");
            }

            displayName = displayNameElement.GetString();
        }

        return new ArtifactReference(
            artifactId,
            mediaType,
            byteLength,
            sha256,
            displayName,
            CloneOptionalObject(element, "extensions", "artifact extensions"));
    }

    /// <summary>
    /// 功能：验证 MIME type 为公共 Schema 允许的 ASCII type/subtype。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">候选 media type。</param>
    /// <returns>长度、单斜杠与 token 字符均有效时为 true。</returns>
    private static bool IsValidMediaType(string value)
    {
        if (value.Length is < 3 or > 128)
        {
            return false;
        }

        var slash = value.IndexOf('/');
        return slash is > 0 && slash == value.LastIndexOf('/') && slash < value.Length - 1 &&
            !value.AsSpan(0, slash).ContainsAnyExcept(MediaTypeCharacters) &&
            !value.AsSpan(slash + 1).ContainsAnyExcept(MediaTypeCharacters);
    }

    /// <summary>
    /// 功能：克隆可选显式扩展对象并拒绝非对象值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parent">包含可选字段的对象。</param>
    /// <param name="name">字段名。</param>
    /// <param name="context">不含实例数据的安全错误上下文。</param>
    /// <returns>字段缺失时为 null，否则为独立对象克隆。</returns>
    /// <exception cref="ProtocolRequestException">字段存在但不是对象。</exception>
    private static JsonElement? CloneOptionalObject(JsonElement parent, string name, string context)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        RequireObject(value, context);
        return value.Clone();
    }

    /// <summary>
    /// 功能：递归检查每个 JSON 对象属性名唯一。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">任意 JSON 值。</param>
    /// <exception cref="ProtocolRequestException">发现重复键。</exception>
    private static void ValidateNoDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ProtocolRequestException(
                        new PortableError(
                            -32700,
                            "parse error",
                            false,
                            new ErrorDetails("parse_error")),
                        null);
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
    /// 功能：验证 request ID 为非空字符串或非负安全整数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="id">ID 元素。</param>
    private static void ValidateRequestId(JsonElement id)
    {
        if (id.ValueKind == JsonValueKind.String)
        {
            var value = id.GetString()!;
            if (value.Length is >= 1 and <= 128)
            {
                return;
            }
        }
        else if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number) && number is >= 0 and <= MaxSafeInteger)
        {
            return;
        }

        throw InvalidRequest("request id is invalid");
    }

    /// <summary>
    /// 功能：要求元素为 JSON object。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">待检查元素。</param>
    /// <param name="context">安全上下文。</param>
    private static void RequireObject(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidParams(context + " must be an object");
        }
    }

    /// <summary>
    /// 功能：取得必需属性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>属性元素。</returns>
    private static JsonElement RequireProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            throw InvalidParams(name + " is required", name);
        }

        return property;
    }

    /// <summary>
    /// 功能：读取必需字符串。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <param name="allowEmpty">是否允许空串。</param>
    /// <returns>字符串值。</returns>
    private static string RequireString(JsonElement element, string name, bool allowEmpty = false)
    {
        var property = RequireProperty(element, name);
        if (property.ValueKind != JsonValueKind.String)
        {
            throw InvalidParams(name + " must be a string", name);
        }

        var value = property.GetString()!;
        if (!allowEmpty && value.Length == 0)
        {
            throw InvalidParams(name + " must not be empty", name);
        }

        return value;
    }

    /// <summary>
    /// 功能：读取必需布尔字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>布尔值。</returns>
    /// <exception cref="ProtocolRequestException">字段缺失或不是布尔值。</exception>
    private static bool RequireBoolean(JsonElement element, string name)
    {
        var property = RequireProperty(element, name);
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw InvalidParams(name + " must be a boolean", name);
        }

        return property.GetBoolean();
    }

    /// <summary>
    /// 功能：读取正安全整数字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>1 到 JavaScript 安全整数上限的值。</returns>
    /// <exception cref="ProtocolRequestException">字段缺失、非整数或越界。</exception>
    private static long RequirePositiveSafeInteger(JsonElement element, string name)
    {
        var property = RequireProperty(element, name);
        if (!property.TryGetInt64(out var value) || value is < 1 or > MaxSafeInteger)
        {
            throw InvalidParams(name + " must be a positive safe integer", name);
        }

        return value;
    }

    /// <summary>
    /// 功能：读取并验证 opaque ID 字符集。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">属性名。</param>
    /// <returns>有效 opaque ID。</returns>
    private static string RequireOpaqueId(JsonElement element, string name)
    {
        var value = RequireString(element, name);
        if (value.Length > 128 || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')))
        {
            throw InvalidParams(name + " is not an opaque ID", name);
        }

        return value;
    }

    /// <summary>
    /// 功能：拒绝 schema 未命名属性。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">待检查对象。</param>
    /// <param name="allowed">允许字段。</param>
    private static void EnsureOnlyProperties(JsonElement element, params ReadOnlySpan<string> allowed)
    {
        foreach (var property in element.EnumerateObject())
        {
            var found = false;
            foreach (var name in allowed)
            {
                if (string.Equals(property.Name, name, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                throw InvalidParams("object contains an unknown field");
            }
        }
    }
}
