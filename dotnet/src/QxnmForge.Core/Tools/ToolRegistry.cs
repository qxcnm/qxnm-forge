using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QxnmForge.Domain;
using QxnmForge.Executor;
using QxnmForge.Provider;

namespace QxnmForge.Tools;

/// <summary>
/// 功能：注册、验证、预检并原生执行六个 v0.1 内置工具。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class ToolRegistry : IDisposable
{
    private const int DefaultOutputLimit = 262_144;
    private const int MaxFileBytes = 1_048_576;
    private const int MaxSearchResults = 1_000;
    private const int MaxSearchFiles = 10_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly WorkspaceBoundary boundary;
    private readonly Dictionary<string, ToolDefinition> definitions;
    private readonly HardSandboxBackend? hardSandbox;
    private readonly PortableError? pathBoundaryConfigurationError;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private PortableError? hardSandboxError;
    private bool disposed;

    /// <summary>
    /// 功能：为 canonical workspace 创建默认内置工具，并在注册时验证全部 inputSchema。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="workspace">必须存在的工作区根目录。</param>
    /// <exception cref="ArgumentException">workspace 或内置 schema 无效。</exception>
    public ToolRegistry(string workspace)
        : this(workspace, cliConformance: false, environmentConformance: false)
    {
    }

    /// <summary>
    /// 功能：为 daemon 创建工具注册表，并严格解析路径竞态 hook、hard-sandbox profile 与双门控测试 override。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="workspace">必须存在的工作区根目录。</param>
    /// <param name="cliConformance">argv 是否显式启用 conformance。</param>
    /// <param name="environmentConformance">QXNM_FORGE_CONFORMANCE 是否精确为 1。</param>
    /// <remarks>不变量：两个 raw conformance gate 分开传入；任一缺失时路径配置 presence 只冻结错误且绝不打开配置源。</remarks>
    /// <exception cref="ArgumentException">workspace 或内置 schema 无效。</exception>
    /// <exception cref="IOException">Linux workspace root 无法冻结为安全目录句柄。</exception>
    public ToolRegistry(string workspace, bool cliConformance, bool environmentConformance)
    {
        var pathBoundaryBarrier = PathBoundaryConformanceBarrier.CreateFromEnvironment(
            cliConformance,
            environmentConformance,
            out pathBoundaryConfigurationError);
        boundary = new WorkspaceBoundary(workspace, pathBoundaryBarrier);
        hardSandbox = HardSandboxBackend.CreateFromEnvironment(
            boundary.Root,
            cliConformance,
            environmentConformance,
            out hardSandboxError);
        definitions = CreateDefinitions().ToDictionary(static item => item.Name, StringComparer.Ordinal);
        foreach (var definition in definitions.Values)
        {
            RestrictedJsonSchemaValidator.ValidateSchema(definition.InputSchema);
        }
    }

    /// <summary>
    /// 功能：取得按固定 source 顺序排列的可执行工具定义。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public IReadOnlyList<ToolDefinition> Definitions =>
        definitions.Values.OrderBy(static item => item.Name, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// 功能：取得 initialize 应广告的稳定工具名。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public IReadOnlyList<string> Names => Definitions.Select(static item => item.Name).ToArray();

    /// <summary>
    /// 功能：取得仅在 startup self-test 成功后可进入 initialize 的 hard-sandbox descriptor。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public HardSandboxCapability? HardSandboxCapability => hardSandbox?.Capability;

    /// <summary>
    /// 功能：在 initialize 响应前传播路径 hook 配置错误并执行 hard-sandbox 真实启动自检。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="cancellationToken">daemon 初始化取消信号。</param>
    /// <returns>未启用或成功时为 null；失败为 conformance_configuration_invalid 或 sandbox_unavailable。</returns>
    /// <remarks>不变量：任一 startup 错误均先于 initialize 成功；失败后不广告能力且不执行工具。</remarks>
    public async Task<PortableError?> InitializeHardSandboxAsync(CancellationToken cancellationToken)
    {
        if (pathBoundaryConfigurationError is not null)
        {
            return pathBoundaryConfigurationError;
        }

        if (hardSandboxError is not null || hardSandbox is null)
        {
            return hardSandboxError;
        }

        try
        {
            await hardSandbox.EnsureSelfTestedAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (ToolOperationException exception)
        {
            hardSandboxError = exception.Error;
            return hardSandboxError;
        }
    }

    /// <summary>
    /// 功能：把内置定义投影为 Provider family 请求使用的语言中立工具声明，并固定 file.read 为首个声明。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="allowedToolIds">可选 run-local allowlist；null 表示完整 registry。</param>
    /// <returns>file.read 优先、其余名称有序的名称、说明与受限 schema 独立列表。</returns>
    /// <remarks>不变量：allowlist 只能收窄 registry，未知名称不会被声明。</remarks>
    public IReadOnlyList<ProviderToolDefinition> ProviderDefinitions(
        IReadOnlyCollection<string>? allowedToolIds = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return Definitions
            .Where(item => allowedToolIds is null ||
                allowedToolIds.Contains(item.Name, StringComparer.Ordinal))
            .OrderBy(static item => item.Name == "file.read" ? 0 : 1)
            .ThenBy(static item => item.Name, StringComparer.Ordinal)
            .Select(static item => new ProviderToolDefinition(
                item.Name,
                item.Description,
                item.InputSchema.Clone()))
            .ToArray();
    }

    /// <summary>
    /// 功能：无副作用查找工具定义，供 Agent 在参数失败时仍写 canonical intent 元数据。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="name">规范工具名。</param>
    /// <param name="definition">存在时返回注册定义。</param>
    /// <returns>找到且 registry 未释放时为 true。</returns>
    public bool TryGetDefinition(string name, out ToolDefinition? definition)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return definitions.TryGetValue(name, out definition);
    }

    /// <summary>
    /// 功能：查找工具并完成 schema 验证、路径/executable 预检、参数 clone 与 operation hash。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="name">Provider 返回的规范工具名。</param>
    /// <param name="arguments">已完整组装的 JSON object 参数。</param>
    /// <param name="toolCallId">可选 Provider 工具调用 ID；仅供执行期测试 barrier 精确匹配。</param>
    /// <param name="allowedToolIds">可选 run-local allowlist；null 表示完整 registry。</param>
    /// <returns>审批与执行共同绑定的不可变计划。</returns>
    /// <remarks>不变量：本方法无工具副作用；未配置/未自检 sandbox 在 approval 前失败，不会创建文件或启动进程。</remarks>
    /// <exception cref="ToolOperationException">未知工具、参数无效、sandbox 不可用、路径越界或 executable 不可解析。</exception>
    public PreparedToolCall Prepare(
        string name,
        JsonElement arguments,
        string? toolCallId = null,
        IReadOnlyCollection<string>? allowedToolIds = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (allowedToolIds is not null &&
            !allowedToolIds.Contains(name, StringComparer.Ordinal) ||
            !definitions.TryGetValue(name, out var definition))
        {
            throw CreateFailure("tool_not_found", "validation_error", -32601, name);
        }

        RestrictedJsonSchemaValidator.ValidateArguments(definition.InputSchema, arguments);
        try
        {
            var normalizedArguments = NormalizeArguments(arguments);
            if (normalizedArguments.TryGetProperty("sandbox", out _) &&
                (hardSandbox is null || hardSandbox.Capability is null || hardSandboxError is not null))
            {
                throw new ToolOperationException(
                    new PortableError(
                        -32603,
                        "hard sandbox is unavailable",
                        false,
                        new ErrorDetails("sandbox_unavailable", ToolName: name)),
                    "internal_error");
            }

            var resources = new List<ApprovalResource>();
            string? resolvedPath = null;
            string? resolvedExecutable = null;
            string? workspaceRelativePath = null;
            switch (name)
            {
                case "file.read":
                case "file.edit":
                    resolvedPath = boundary.ResolveExistingRelative(RequireString(normalizedArguments, "path"));
                    EnsureRegularFile(resolvedPath);
                    workspaceRelativePath = ToPortableRelativePath(resolvedPath);
                    resources.Add(new ApprovalResource("path", workspaceRelativePath));
                    break;
                case "file.write":
                    resolvedPath = boundary.ResolveForWrite(RequireString(normalizedArguments, "path"));
                    workspaceRelativePath = ToPortableRelativePath(resolvedPath);
                    resources.Add(new ApprovalResource("path", workspaceRelativePath));
                    break;
                case "search.text":
                    resolvedPath = normalizedArguments.TryGetProperty("path", out var searchPath)
                        ? boundary.ResolveExistingRelative(searchPath.GetString()!)
                        : boundary.Root;
                    resources.Add(new ApprovalResource("path", ToPortableRelativePath(resolvedPath)));
                    ValidateSearchPattern(RequireString(normalizedArguments, "pattern"));
                    break;
                case "process.exec":
                    resolvedExecutable = ResolveExecutable(RequireString(normalizedArguments, "executable"));
                    resolvedPath = ResolveWorkingDirectory(normalizedArguments);
                    resources.Add(new ApprovalResource("executable", resolvedExecutable));
                    resources.Add(new ApprovalResource("path", ToPortableRelativePath(resolvedPath)));
                    break;
                case "shell.exec":
                    resolvedExecutable = ResolveShell(RequireString(normalizedArguments, "shell"));
                    resolvedPath = ResolveWorkingDirectory(normalizedArguments);
                    resources.Add(new ApprovalResource("executable", resolvedExecutable));
                    resources.Add(new ApprovalResource("path", ToPortableRelativePath(resolvedPath)));
                    break;
            }

            if (normalizedArguments.TryGetProperty("sandbox", out var sandbox))
            {
                resources.Add(new ApprovalResource(
                    "sandbox",
                    string.Join(
                        ':',
                        sandbox.GetProperty("profile").GetString(),
                        sandbox.GetProperty("workspaceAccess").GetString(),
                        sandbox.GetProperty("network").GetString())));
            }

            return new PreparedToolCall(
                definition,
                normalizedArguments,
                resources,
                CanonicalJson.HashOperation(name, normalizedArguments, resources),
                resolvedPath,
                resolvedExecutable,
                workspaceRelativePath,
                toolCallId);
        }
        catch (ToolOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
        {
            throw CreateFailure("permission_denied", "denied", -32003, name);
        }
    }

    /// <summary>
    /// 功能：为未知/无效调用计算同样稳定的 operation hash，支持单一 canonical tool.intent。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="name">工具名。</param>
    /// <param name="arguments">Provider 参数 object。</param>
    /// <returns>不含资源的 SHA-256。</returns>
    public static string HashUnprepared(string name, JsonElement arguments)
    {
        return CanonicalJson.HashOperation(name, arguments, Array.Empty<ApprovalResource>());
    }

    /// <summary>
    /// 功能：执行已经完成 schema、资源和审批绑定的工具计划。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="prepared">不可变预检结果。</param>
    /// <param name="cancellationToken">run 取消信号。</param>
    /// <returns>可进入 journal、事件和下一 Provider turn 的 portable 结果。</returns>
    /// <remarks>调用方必须先执行 policy/approval；本方法本身不会把模型参数视为授权，匹配测试配置必须命中一次 checkpoint 才能成功。</remarks>
    /// <exception cref="OperationCanceledException">取消已终止工具/进程树。</exception>
    /// <exception cref="ToolOperationException">I/O、timeout、输出限制或执行器失败。</exception>
    public async Task<PortableToolResult> ExecuteAsync(
        PreparedToolCall prepared,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        boundary.ValidateLinuxBarrierToolCall(prepared.ToolCallId, prepared.Definition.Name);
        var result = prepared.Definition.Name switch
        {
            "file.read" => await ReadFileAsync(prepared, cancellationToken).ConfigureAwait(false),
            "file.write" => await WriteFileAsync(prepared, cancellationToken).ConfigureAwait(false),
            "file.edit" => await EditFileAsync(prepared, cancellationToken).ConfigureAwait(false),
            "search.text" => await SearchTextAsync(prepared, cancellationToken).ConfigureAwait(false),
            "process.exec" => await ExecuteProcessAsync(prepared, cancellationToken).ConfigureAwait(false),
            "shell.exec" => await ExecuteShellAsync(prepared, cancellationToken).ConfigureAwait(false),
            _ => throw CreateFailure("tool_not_found", "validation_error", -32601, prepared.Definition.Name),
        };
        boundary.EnsureLinuxBarrierSatisfied(prepared.ToolCallId);
        return result;
    }

    /// <summary>
    /// 功能：把工具异常转换为至少一个安全文本块和结构化 error 的 portable 结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="exception">受控工具异常。</param>
    /// <returns>isError=true 的结果。</returns>
    public static PortableToolResult FailureResult(ToolOperationException exception)
    {
        return new PortableToolResult(
            [new TextContent(exception.Error.Message)],
            true,
            exception.TerminationReason,
            exception.Error);
    }

    /// <summary>
    /// 功能：创建取消等待或执行时进入 Provider 上下文的标准错误结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>terminationReason=cancelled 的 portable 结果。</returns>
    public static PortableToolResult CancelledResult()
    {
        var error = new PortableError(-32007, "tool execution was cancelled", false, new ErrorDetails("cancelled"));
        return new PortableToolResult([new TextContent(error.Message)], true, "cancelled", error);
    }

    /// <summary>
    /// 功能：释放并发写 gate；不会删除工作区内容或终止不属于当前调用的进程。
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
        hardSandbox?.Dispose();
        boundary.Dispose();
        writeGate.Dispose();
    }

    /// <summary>
    /// 功能：创建基础文件工具，并仅在强 Linux 后代守卫可用时加入进程与 shell 工具。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>不含重复名称且不夸大当前平台 containment 的定义列表。</returns>
    private static List<ToolDefinition> CreateDefinitions()
    {
        List<ToolDefinition> result =
        [
            Define("file.read", "读取工作区内的 UTF-8 文本文件", ToolAction.FileRead, "workspace_read", true,
                """{"type":"object","properties":{"path":{"type":"string","minLength":1,"maxLength":4096}},"required":["path"],"additionalProperties":false}"""),
            Define("file.write", "原子写入工作区内的 UTF-8 文本文件", ToolAction.FileWrite, "workspace_write", false,
                """{"type":"object","properties":{"path":{"type":"string","minLength":1,"maxLength":4096},"content":{"type":"string","maxLength":1048576}},"required":["path","content"],"additionalProperties":false}"""),
            Define("file.edit", "唯一匹配并原子替换工作区文本", ToolAction.FileEdit, "workspace_write", false,
                """{"type":"object","properties":{"path":{"type":"string","minLength":1,"maxLength":4096},"oldText":{"type":"string","minLength":1,"maxLength":1048576},"newText":{"type":"string","maxLength":1048576},"expectedSha256":{"type":"string","pattern":"^[0-9a-f]{64}$"}},"required":["path","oldText","newText"],"additionalProperties":false}"""),
            Define("search.text", "在工作区 UTF-8 文本文件中搜索正则表达式", ToolAction.SearchText, "workspace_read", true,
                """{"type":"object","properties":{"pattern":{"type":"string","minLength":1,"maxLength":4096},"path":{"type":"string","minLength":1,"maxLength":4096}},"required":["pattern"],"additionalProperties":false}"""),
        ];
        if (!ProcessExecutor.IsConformantPlatformAvailable())
        {
            return result;
        }

        result.Add(Define(
            "process.exec",
            "以 executable 与 argv 运行进程，不经过 shell",
            ToolAction.ProcessExec,
            "process",
            false,
            """
            {"type":"object","properties":{"executable":{"type":"string","minLength":1,"maxLength":4096},"args":{"type":"array","items":{"type":"string","maxLength":4096},"maxItems":256},"cwd":{"type":"string","minLength":1,"maxLength":4096},"stdin":{"x-discriminator":"type","oneOf":[{"type":"object","properties":{"type":{"type":"string","const":"none"}},"required":["type"],"additionalProperties":false},{"type":"object","properties":{"type":{"type":"string","const":"text"},"text":{"type":"string","maxLength":1048576}},"required":["type","text"],"additionalProperties":false}]},"timeoutMs":{"type":"integer","minimum":1,"maximum":600000},"outputLimitBytes":{"type":"integer","minimum":1,"maximum":16777216},"sandbox":{"type":"object","properties":{"profile":{"type":"string","const":"linux-bwrap-v1"},"workspaceAccess":{"type":"string","enum":["read_only","read_write"]},"network":{"type":"string","const":"isolated"}},"required":["profile","workspaceAccess","network"],"additionalProperties":false}},"required":["executable","args"],"additionalProperties":false}
            """));
        result.Add(Define(
            "shell.exec",
            "通过显式选择的非交互 shell 运行脚本",
            ToolAction.ShellExec,
            "shell",
            false,
            """
            {"type":"object","properties":{"shell":{"type":"string","enum":["bash","sh","pwsh","powershell","cmd"]},"script":{"type":"string","maxLength":1048576},"cwd":{"type":"string","minLength":1,"maxLength":4096},"stdin":{"x-discriminator":"type","oneOf":[{"type":"object","properties":{"type":{"type":"string","const":"none"}},"required":["type"],"additionalProperties":false},{"type":"object","properties":{"type":{"type":"string","const":"text"},"text":{"type":"string","maxLength":1048576}},"required":["type","text"],"additionalProperties":false}]},"timeoutMs":{"type":"integer","minimum":1,"maximum":600000},"outputLimitBytes":{"type":"integer","minimum":1,"maximum":16777216},"sandbox":{"type":"object","properties":{"profile":{"type":"string","const":"linux-bwrap-v1"},"workspaceAccess":{"type":"string","enum":["read_only","read_write"]},"network":{"type":"string","const":"isolated"}},"required":["profile","workspaceAccess","network"],"additionalProperties":false}},"required":["shell","script"],"additionalProperties":false}
            """));
        return result;
    }

    /// <summary>
    /// 功能：从固定 JSON schema 文本构造一个内置工具定义。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="name">工具名。</param>
    /// <param name="description">中文说明。</param>
    /// <param name="action">策略类别。</param>
    /// <param name="permissionClass">portable 权限类。</param>
    /// <param name="idempotent">幂等声明。</param>
    /// <param name="schemaJson">编译期固定 schema JSON。</param>
    /// <returns>独立 schema 生命周期的定义。</returns>
    private static ToolDefinition Define(
        string name,
        string description,
        ToolAction action,
        string permissionClass,
        bool idempotent,
        string schemaJson)
    {
        using var document = JsonDocument.Parse(schemaJson);
        return new ToolDefinition(
            name,
            description,
            document.RootElement.Clone(),
            action,
            permissionClass,
            idempotent,
            DefaultOutputLimit);
    }

    /// <summary>
    /// 功能：读取有界 UTF-8 regular file，拒绝读取期间增长和二进制替换。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="prepared">file.read 计划。</param>
    /// <param name="cancellationToken">读取取消信号。</param>
    /// <returns>文件正文文本结果。</returns>
    private async Task<PortableToolResult> ReadFileAsync(
        PreparedToolCall prepared,
        CancellationToken cancellationToken)
    {
        if (boundary.UsesLinuxFileHandles)
        {
            var relativePath = prepared.WorkspaceRelativePath ??
                throw CreateFailure("tool_arguments_invalid", "validation_error", -32602, prepared.Definition.Name);
            await using var linuxStream = await boundary.OpenLinuxReadAsync(
                relativePath,
                prepared.ToolCallId,
                MaxFileBytes,
                cancellationToken).ConfigureAwait(false);
            var result = await ReadOpenedFileAsync(
                prepared,
                linuxStream,
                linuxStream.Length,
                cancellationToken).ConfigureAwait(false);
            return result;
        }

        var path = prepared.ResolvedPath!;
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
        {
            throw CreateLimitFailure(prepared.Definition.Name, MaxFileBytes, info.Length);
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
                             8192,
                             FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadOpenedFileAsync(
            prepared,
            stream,
            info.Length,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：从已打开的同一文件句柄读取固定长度、验证 EOF 并严格解码 UTF-8。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="prepared">file.read 不可变执行计划。</param>
    /// <param name="stream">Linux leaf fd 或其他平台 fallback 路径打开的流。</param>
    /// <param name="expectedLength">打开后观察到且不超过 1 MiB 的长度。</param>
    /// <param name="cancellationToken">读取取消信号。</param>
    /// <returns>完整 strict UTF-8 文本工具结果。</returns>
    /// <remarks>不变量：只从传入 handle 读取，expectedLength 后还必须立即 EOF；增长/缩短均作为冲突失败。</remarks>
    /// <exception cref="ToolOperationException">长度、并发变化或 UTF-8 无效。</exception>
    private static async Task<PortableToolResult> ReadOpenedFileAsync(
        PreparedToolCall prepared,
        FileStream stream,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        if (expectedLength > MaxFileBytes)
        {
            throw CreateLimitFailure(prepared.Definition.Name, MaxFileBytes, expectedLength);
        }

        var bytes = new byte[checked((int)expectedLength)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        if (offset != bytes.Length || stream.ReadByte() != -1)
        {
            throw CreateFailure("resource_conflict", "internal_error", -32010, prepared.Definition.Name);
        }

        try
        {
            return Success(StrictUtf8.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            throw CreateFailure("tool_arguments_invalid", "validation_error", -32602, prepared.Definition.Name);
        }
    }

    /// <summary>
    /// 功能：在全局写 gate 内复验路径并通过同目录临时文件原子写入文本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="prepared">file.write 计划。</param>
    /// <param name="cancellationToken">写入取消信号。</param>
    /// <returns>写入字节数结果。</returns>
    private async Task<PortableToolResult> WriteFileAsync(
        PreparedToolCall prepared,
        CancellationToken cancellationToken)
    {
        var content = RequireString(prepared.Arguments, "content");
        var bytes = StrictUtf8.GetBytes(content);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (boundary.UsesLinuxFileHandles)
            {
                var relativePath = prepared.WorkspaceRelativePath ??
                    throw CreateFailure("tool_arguments_invalid", "validation_error", -32602, prepared.Definition.Name);
                await boundary.WriteLinuxAtomicallyAsync(
                    relativePath,
                    bytes,
                    prepared.ToolCallId,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await AtomicWriteAsync(prepared.ResolvedPath!, bytes, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            writeGate.Release();
        }

        return Success(string.Create(CultureInfo.InvariantCulture, $"wrote {bytes.Length} bytes"));
    }

    /// <summary>
    /// 功能：在原始 hash 可选校验和 oldText 唯一匹配后原子替换文本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="prepared">file.edit 计划。</param>
    /// <param name="cancellationToken">编辑取消信号。</param>
    /// <returns>成功替换一次的结果。</returns>
    private async Task<PortableToolResult> EditFileAsync(
        PreparedToolCall prepared,
        CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = prepared.ResolvedPath!;
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes.Length > MaxFileBytes)
            {
                throw CreateLimitFailure(prepared.Definition.Name, MaxFileBytes, bytes.Length);
            }

            if (prepared.Arguments.TryGetProperty("expectedSha256", out var expected) &&
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected.GetString()!),
                    Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA256.HashData(bytes)))))
            {
                throw CreateFailure("resource_conflict", "internal_error", -32010, prepared.Definition.Name);
            }

            string original;
            try
            {
                original = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw CreateFailure("tool_arguments_invalid", "validation_error", -32602, prepared.Definition.Name);
            }

            var oldText = RequireString(prepared.Arguments, "oldText");
            var first = original.IndexOf(oldText, StringComparison.Ordinal);
            if (first < 0 || original.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0)
            {
                throw CreateFailure("resource_conflict", "internal_error", -32010, prepared.Definition.Name);
            }

            var updated = string.Concat(
                original.AsSpan(0, first),
                RequireString(prepared.Arguments, "newText"),
                original.AsSpan(first + oldText.Length));
            await AtomicWriteAsync(path, StrictUtf8.GetBytes(updated), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }

        return Success("edited 1 occurrence");
    }

    /// <summary>
    /// 功能：不跟随目录链接递归搜索有界 UTF-8 文件并限制文件数、匹配数和输出。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="prepared">search.text 计划。</param>
    /// <param name="cancellationToken">搜索取消信号。</param>
    /// <returns>relativePath:line:text 格式的有序匹配。</returns>
    private async Task<PortableToolResult> SearchTextAsync(
        PreparedToolCall prepared,
        CancellationToken cancellationToken)
    {
        var regex = CreateSearchRegex(RequireString(prepared.Arguments, "pattern"));
        var files = EnumerateSearchFiles(prepared.ResolvedPath!).Take(MaxSearchFiles + 1).ToArray();
        if (files.Length > MaxSearchFiles)
        {
            throw CreateLimitFailure(prepared.Definition.Name, MaxSearchFiles, files.Length);
        }

        var matches = new List<string>();
        var outputBytes = 0;
        foreach (var path in files.OrderBy(static item => item, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes)
            {
                continue;
            }

            string content;
            try
            {
                content = StrictUtf8.GetString(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            var lineNumber = 0;
            using var reader = new StringReader(content);
            while (reader.ReadLine() is { } line)
            {
                lineNumber++;
                if (!regex.IsMatch(line))
                {
                    continue;
                }

                var rendered = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{ToPortableRelativePath(path)}:{lineNumber}:{line}");
                outputBytes = checked(outputBytes + StrictUtf8.GetByteCount(rendered) + 1);
                if (matches.Count >= MaxSearchResults || outputBytes > prepared.Definition.MaxOutputBytes)
                {
                    throw CreateLimitFailure(prepared.Definition.Name, prepared.Definition.MaxOutputBytes, outputBytes);
                }

                matches.Add(rendered);
            }
        }

        return Success(string.Join('\n', matches));
    }

    /// <summary>
    /// 功能：解析 process.exec argv 和有限策略后调用统一进程树执行器。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="prepared">process.exec 计划。</param>
    /// <param name="cancellationToken">run 取消信号。</param>
    /// <returns>分离 stdout/stderr 与退出码文本结果。</returns>
    private async Task<PortableToolResult> ExecuteProcessAsync(
        PreparedToolCall prepared,
        CancellationToken cancellationToken)
    {
        var arguments = prepared.Arguments.GetProperty("args")
            .EnumerateArray()
            .Select(static item => item.GetString()!)
            .ToArray();
        return await ExecuteProcessCoreAsync(
            prepared,
            prepared.ResolvedExecutable!,
            arguments,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：把显式 shell 和脚本文本映射为固定非交互 argv 后调用统一执行器。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="prepared">shell.exec 计划。</param>
    /// <param name="cancellationToken">run 取消信号。</param>
    /// <returns>分离 stdout/stderr 与退出码文本结果。</returns>
    private async Task<PortableToolResult> ExecuteShellAsync(
        PreparedToolCall prepared,
        CancellationToken cancellationToken)
    {
        var script = RequireString(prepared.Arguments, "script");
        var shell = RequireString(prepared.Arguments, "shell");
        var sandboxed = prepared.Arguments.TryGetProperty("sandbox", out _);
        var usesPosixPause = !sandboxed && OperatingSystem.IsLinux() && shell is "bash" or "sh";
        var guardedScript = usesPosixPause
            ? "kill -STOP $$\n" + script
            : script;
        IReadOnlyList<string> arguments = shell switch
        {
            "bash" => ["--noprofile", "--norc", "-c", guardedScript],
            "sh" => ["-c", guardedScript],
            "pwsh" or "powershell" => ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", guardedScript],
            "cmd" => ["/d", "/s", "/c", guardedScript],
            _ => throw CreateFailure("tool_arguments_invalid", "validation_error", -32602, prepared.Definition.Name),
        };
        return await ExecuteProcessCoreAsync(
            prepared,
            prepared.ResolvedExecutable!,
            arguments,
            cancellationToken,
            resumeAfterContainment: usesPosixPause).ConfigureAwait(false);
    }

    /// <summary>
    /// 功能：执行统一 process request，并把所有已启动终态投影为 structured execution 工具结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="prepared">进程或 shell 计划。</param>
    /// <param name="executable">解析后 executable。</param>
    /// <param name="arguments">边界明确的 argv。</param>
    /// <param name="cancellationToken">run 取消信号。</param>
    /// <param name="resumeAfterContainment">固定 shell 自停前缀是否需要在守卫建立后恢复。</param>
    /// <returns>带独立双流字节账的 portable 工具结果。</returns>
    /// <remarks>不变量：子进程成功启动后即使 timeout/cancel/output-limit 也不会丢失 execution；非零退出不是基础设施异常。</remarks>
    private async Task<PortableToolResult> ExecuteProcessCoreAsync(
        PreparedToolCall prepared,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool resumeAfterContainment = false)
    {
        var timeout = prepared.Arguments.TryGetProperty("timeoutMs", out var timeoutElement)
            ? TimeSpan.FromMilliseconds(timeoutElement.GetInt32())
            : TimeSpan.FromSeconds(120);
        var outputLimit = prepared.Arguments.TryGetProperty("outputLimitBytes", out var limitElement)
            ? limitElement.GetInt32()
            : prepared.Definition.MaxOutputBytes;
        var standardInput = ParseStandardInput(prepared.Arguments, prepared.Definition.Name);
        ProcessExecutionResult result;
        if (prepared.Arguments.TryGetProperty("sandbox", out var sandbox))
        {
            if (hardSandbox is null || hardSandboxError is not null)
            {
                throw new ToolOperationException(
                    new PortableError(
                        -32603,
                        "hard sandbox is unavailable",
                        false,
                        new ErrorDetails("sandbox_unavailable", ToolName: prepared.Definition.Name)),
                    "internal_error");
            }

            result = await hardSandbox.ExecuteAsync(
                boundary.Root,
                prepared.ResolvedPath!,
                sandbox.GetProperty("workspaceAccess").GetString()!,
                executable,
                arguments,
                standardInput,
                timeout,
                outputLimit,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await ProcessExecutor.ExecuteAsync(
                new ProcessExecutionRequest(
                    executable,
                    arguments,
                    prepared.ResolvedPath!,
                    standardInput,
                    resumeAfterContainment,
                    timeout,
                    outputLimit),
                cancellationToken).ConfigureAwait(false);
        }
        var execution = ToPortableExecution(result);
        var rendered = string.Create(
            CultureInfo.InvariantCulture,
            $"exitCode: {(result.ExitCode is null ? "null" : result.ExitCode.Value)}\nstdout:\n{result.Stdout.Text}\nstderr:\n{result.Stderr.Text}");
        if (StrictUtf8.GetByteCount(rendered) > prepared.Definition.MaxOutputBytes)
        {
            rendered = "process output is available in the structured bounded execution result";
        }

        if (result.TerminationReason == "exit" && result.ExitCode is 0)
        {
            return new PortableToolResult(
                [new TextContent(rendered)],
                false,
                "exit",
                Execution: execution);
        }

        var error = result.TerminationReason switch
        {
            "cancelled" => new PortableError(
                -32007,
                "process execution was cancelled",
                false,
                new ErrorDetails("cancelled", ToolName: prepared.Definition.Name)),
            "timeout" => new PortableError(
                -32006,
                "process execution timed out",
                false,
                new ErrorDetails("process_timeout", ToolName: prepared.Definition.Name)),
            "output_limit" => new PortableError(
                -32009,
                "process output exceeded the configured limit",
                false,
                new ErrorDetails("output_limit", ToolName: prepared.Definition.Name, Limit: outputLimit)),
            "signal" => new PortableError(
                -32603,
                "process ended after a signal",
                false,
                new ErrorDetails("process_signalled", ToolName: prepared.Definition.Name)),
            _ => new PortableError(
                -32603,
                "process exited unsuccessfully",
                false,
                new ErrorDetails(
                    "process_nonzero_exit",
                    ToolName: prepared.Definition.Name,
                    ExitCode: result.ExitCode)),
        };
        return new PortableToolResult(
            [new TextContent(rendered)],
            true,
            result.TerminationReason,
            error,
            execution);
    }

    /// <summary>
    /// 功能：严格解析缺省、none 或最大 1 MiB 的 UTF-8 text stdin tagged union。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="arguments">已经通过受限 schema 的进程或 shell 参数。</param>
    /// <param name="toolName">结构化失败使用的规范工具名。</param>
    /// <returns>none/缺省返回 null，text 返回独立 UTF-8 字节。</returns>
    /// <remarks>不变量：stdin 不拼入 argv、shell 脚本、环境或错误；字节数而非 UTF-16 长度限制为 1 MiB。</remarks>
    /// <exception cref="ToolOperationException">variant 或 UTF-8 字节上限非法。</exception>
    private static ReadOnlyMemory<byte>? ParseStandardInput(JsonElement arguments, string toolName)
    {
        if (!arguments.TryGetProperty("stdin", out var standardInput))
        {
            return null;
        }

        var type = standardInput.GetProperty("type").GetString();
        if (type == "none")
        {
            return null;
        }

        if (type != "text")
        {
            throw CreateFailure("tool_arguments_invalid", "validation_error", -32602, toolName);
        }

        var text = standardInput.GetProperty("text").GetString()!;
        var byteCount = StrictUtf8.GetByteCount(text);
        if (byteCount > 1_048_576)
        {
            throw CreateFailure("tool_arguments_invalid", "validation_error", -32602, toolName);
        }

        return StrictUtf8.GetBytes(text);
    }

    /// <summary>
    /// 功能：把原生 executor 结果转换为满足公共双流字节账不变量的 wire DTO。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="result">已完成整树清理的原生进程结果。</param>
    /// <returns>可直接嵌入 ToolResult 的 portable execution。</returns>
    /// <remarks>不变量：每个流 capturedBytes + omittedBytes == totalBytes，truncated 与 omittedBytes 大于零等价。</remarks>
    private static PortableExecutionResult ToPortableExecution(ProcessExecutionResult result)
    {
        return new PortableExecutionResult(
            result.ExitCode,
            result.Signal,
            result.TerminationReason,
            result.DurationMs,
            result.Containment,
            ToPortableStream(result.Stdout),
            ToPortableStream(result.Stderr));
    }

    /// <summary>
    /// 功能：把单个原生 pipe 结果投影为 UTF-8 replacement 捕获及精确 omitted 计数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="stream">有界捕获与实际读取总数。</param>
    /// <returns>满足非负计数不变量的 portable stream。</returns>
    private static PortableExecutionCapture ToPortableStream(ProcessStreamResult stream)
    {
        var omitted = checked(stream.TotalBytes - stream.CapturedBytes);
        return new PortableExecutionCapture(
            "utf-8-replacement",
            stream.Text,
            stream.CapturedBytes,
            stream.TotalBytes,
            omitted,
            omitted > 0);
    }

    /// <summary>
    /// 功能：同目录 CreateNew 临时写入、flush 并复验后原子替换目标。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">预检后的绝对目标。</param>
    /// <param name="bytes">完整 UTF-8 内容。</param>
    /// <param name="cancellationToken">写入取消信号。</param>
    /// <returns>rename 完成后的 Task。</returns>
    private async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        boundary.RevalidateForWrite(path);
        var parent = Path.GetDirectoryName(path) ?? throw CreateFailure("tool_arguments_invalid", "validation_error", -32602);
        if (!Directory.Exists(parent))
        {
            throw CreateFailure("tool_arguments_invalid", "validation_error", -32602);
        }

        var temporary = Path.Combine(parent, ".qxnm-forge-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             8192,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            boundary.RevalidateForWrite(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 失败清理不得覆盖原始工具结果。
            }
        }
    }

    /// <summary>
    /// 功能：无副作用规范化参数 object 属性顺序，保留值语义并便于跨语言 hash。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="arguments">已验证参数。</param>
    /// <returns>按 canonical JSON 重解析的独立对象。</returns>
    private static JsonElement NormalizeArguments(JsonElement arguments)
    {
        using var document = JsonDocument.Parse(CanonicalJson.Serialize(arguments));
        return document.RootElement.Clone();
    }

    /// <summary>
    /// 功能：枚举搜索起点下不跟随 link/reparse 目录的 regular files。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="start">已验证文件或目录。</param>
    /// <returns>惰性绝对文件路径序列。</returns>
    private static IEnumerable<string> EnumerateSearchFiles(string start)
    {
        if (File.Exists(start))
        {
            yield return start;
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else if ((attributes & FileAttributes.Device) == 0)
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>
    /// 功能：创建有超时、文化无关且不回溯的 search regex。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="pattern">模型提供且已长度验证的正则。</param>
    /// <returns>安全 Regex。</returns>
    private static Regex CreateSearchRegex(string pattern)
    {
        try
        {
            return new Regex(
                pattern,
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            throw CreateFailure("tool_arguments_invalid", "validation_error", -32602, "search.text");
        }
    }

    /// <summary>
    /// 功能：在预检阶段验证 search pattern 可由非回溯引擎编译。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="pattern">正则文本。</param>
    private static void ValidateSearchPattern(string pattern)
    {
        _ = CreateSearchRegex(pattern);
    }

    /// <summary>
    /// 功能：解析 process cwd；缺失时固定为 workspace root。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="arguments">已验证参数。</param>
    /// <returns>工作区内现存目录。</returns>
    private string ResolveWorkingDirectory(JsonElement arguments)
    {
        var path = arguments.TryGetProperty("cwd", out var cwd)
            ? boundary.ResolveExistingRelative(cwd.GetString()!)
            : boundary.Root;
        if (!Directory.Exists(path))
        {
            throw CreateFailure("tool_arguments_invalid", "validation_error", -32602);
        }

        return path;
    }

    /// <summary>
    /// 功能：按绝对路径、工作区相对路径或 allowlisted PATH 确定解析 executable。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">executable 参数。</param>
    /// <returns>存在的确定性绝对 executable。</returns>
    private string ResolveExecutable(string value)
    {
        if (Path.IsPathRooted(value))
        {
            var absolute = Path.GetFullPath(value);
            if (File.Exists(absolute))
            {
                return absolute;
            }
        }
        else if (value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
        {
            var relative = boundary.ResolveExistingRelative(value);
            if (File.Exists(relative))
            {
                return relative;
            }
        }
        else
        {
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                         .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var candidate in ExecutableCandidates(directory, value))
                {
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
            }
        }

        throw CreateFailure("tool_arguments_invalid", "validation_error", -32602, "process.exec");
    }

    /// <summary>
    /// 功能：将显式 shell 名映射为平台可用 executable，不根据脚本内容猜测。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="shell">封闭 shell 枚举字符串。</param>
    /// <returns>解析后的 executable。</returns>
    private string ResolveShell(string shell)
    {
        var executable = shell == "cmd" && OperatingSystem.IsWindows() ? "cmd.exe" : shell;
        return ResolveExecutable(executable);
    }

    /// <summary>
    /// 功能：生成当前平台 PATH lookup 的原始名及 Windows PATHEXT 候选。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="directory">PATH 目录。</param>
    /// <param name="name">简单 executable 名。</param>
    /// <returns>有限候选序列。</returns>
    private static IEnumerable<string> ExecutableCandidates(string directory, string name)
    {
        yield return Path.Combine(directory, name);
        if (!OperatingSystem.IsWindows() || Path.HasExtension(name))
        {
            yield break;
        }

        foreach (var extension in (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                     .Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return Path.Combine(directory, name + extension);
        }
    }

    /// <summary>
    /// 功能：把 canonical 绝对路径转换为使用斜杠的 workspace 相对审批资源。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">工作区内绝对路径。</param>
    /// <returns>根目录为点号，其余为 portable 相对路径。</returns>
    private string ToPortableRelativePath(string path)
    {
        return Path.GetRelativePath(boundary.Root, path).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// 功能：确认预检路径是 regular file 而非目录、设备或链接。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">已解析路径。</param>
    private static void EnsureRegularFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw CreateFailure("tool_arguments_invalid", "validation_error", -32602);
        }
    }

    /// <summary>
    /// 功能：读取已验证参数中的必需字符串。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="arguments">参数 object。</param>
    /// <param name="name">属性名。</param>
    /// <returns>字符串值。</returns>
    private static string RequireString(JsonElement arguments, string name)
    {
        return arguments.GetProperty(name).GetString()!;
    }

    /// <summary>
    /// 功能：创建成功的单文本块工具结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="text">有界结果文本。</param>
    /// <returns>isError=false 的 portable 结果。</returns>
    private static PortableToolResult Success(string text)
    {
        return new PortableToolResult([new TextContent(text)], false);
    }

    /// <summary>
    /// 功能：创建带工具名但不回显参数值的受控工具失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="kind">ErrorDetails.kind。</param>
    /// <param name="terminationReason">portable 终止原因。</param>
    /// <param name="code">portable error code。</param>
    /// <param name="toolName">可选工具名。</param>
    /// <returns>ToolOperationException。</returns>
    private static ToolOperationException CreateFailure(
        string kind,
        string terminationReason,
        int code,
        string? toolName = null)
    {
        return new ToolOperationException(
            new PortableError(code, "tool operation failed", false, new ErrorDetails(kind, ToolName: toolName)),
            terminationReason);
    }

    /// <summary>
    /// 功能：创建含 allowlisted limit/observed 计数的输出资源上限失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="toolName">工具名。</param>
    /// <param name="limit">配置上限。</param>
    /// <param name="observed">观察值。</param>
    /// <returns>output_limit 工具异常。</returns>
    private static ToolOperationException CreateLimitFailure(string toolName, long limit, long observed)
    {
        return new ToolOperationException(
            new PortableError(
                -32009,
                "tool output exceeded the configured limit",
                false,
                new ErrorDetails("output_limit", ToolName: toolName, Limit: limit, Observed: observed)),
            "output_limit");
    }
}
