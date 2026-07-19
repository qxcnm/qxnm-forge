using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using QxnmForge.Domain;
using QxnmForge.Serialization;

namespace QxnmForge.Executor;

/// <summary>
/// 功能：列出 ADR 0021 Linux 路径竞态 runner 可暂停的四个精确执行检查点。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal enum PathBoundaryCheckpoint
{
    BeforeParentWalk,
    AfterParentOpen,
    AfterLeafOpenBeforeRead,
    AfterTempFsyncBeforeRename,
}

/// <summary>
/// 功能：实现双门控、一次性、有界且不跟随链接的 ADR 0021 文件 barrier。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class PathBoundaryConformanceBarrier : IDisposable
{
    internal const string ConfigurationEnvironment = "AGENT_PATH_BOUNDARY_CONFORMANCE_CONFIG";
    private const string SchemaVersion = "0.1";
    private const string ReadyFile = "ready.json";
    private const string ReleaseFile = "release.json";
    private const int ExpectedTimeoutMilliseconds = 5000;
    private const int MaximumConfigurationBytes = 16_384;
    private const int MaximumBarrierBytes = 4096;
    private const int MissingFileError = 2;
    private static readonly HashSet<string> ConfigurationProperties =
    [
        "schemaVersion",
        "caseId",
        "toolCallId",
        "checkpoint",
        "controlDirectory",
        "timeoutMs",
    ];
    private static readonly HashSet<string> BarrierProperties =
    [
        "schemaVersion",
        "caseId",
        "toolCallId",
        "checkpoint",
        "state",
    ];
    private readonly string caseId;
    private readonly string toolCallId;
    private readonly PathBoundaryCheckpoint checkpoint;
    private readonly SafeFileHandle controlDirectoryHandle;
    private int reached;
    private bool disposed;

    /// <summary>
    /// 功能：保存已严格验证的 barrier 身份与 0700 控制目录句柄。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="caseId">runner 本次唯一案例 ID。</param>
    /// <param name="toolCallId">只允许触发 barrier 的工具调用 ID。</param>
    /// <param name="checkpoint">只允许触发一次的执行检查点。</param>
    /// <param name="controlDirectoryHandle">由 no-follow 方式打开并验证为 0700 的目录句柄。</param>
    private PathBoundaryConformanceBarrier(
        string caseId,
        string toolCallId,
        PathBoundaryCheckpoint checkpoint,
        SafeFileHandle controlDirectoryHandle)
    {
        this.caseId = caseId;
        this.toolCallId = toolCallId;
        this.checkpoint = checkpoint;
        this.controlDirectoryHandle = controlDirectoryHandle;
    }

    /// <summary>
    /// 功能：按 raw CLI/environment 双门创建 barrier，或产生 initialize 阶段固定配置错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="cliConformance">argv 是否包含独立精确的 --conformance 元素。</param>
    /// <param name="environmentConformance">QXNM_FORGE_CONFORMANCE 是否精确等于 ASCII `1`。</param>
    /// <param name="error">无配置时为 null；非法 presence、平台或配置时为固定 portable error。</param>
    /// <returns>双门、Linux、严格配置全部有效时返回 barrier；其他情况返回 null。</returns>
    /// <remarks>不变量：配置 env 存在但任一 raw gate 缺失时绝不 open/stat/read 配置路径，并固定失败关闭 initialize。</remarks>
    internal static PathBoundaryConformanceBarrier? CreateFromEnvironment(
        bool cliConformance,
        bool environmentConformance,
        out PortableError? error)
    {
        error = null;
        var configurationPath = Environment.GetEnvironmentVariable(ConfigurationEnvironment);
        if (configurationPath is null)
        {
            return null;
        }

        if (!cliConformance || !environmentConformance || !OperatingSystem.IsLinux())
        {
            error = ConfigurationInvalid();
            return null;
        }

        try
        {
            return Load(configurationPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException)
        {
            error = ConfigurationInvalid();
            return null;
        }
    }

    /// <summary>
    /// 功能：在工具调用 ID 与配置 checkpoint 同时匹配时发布 ready 并有界等待严格 release。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="currentToolCallId">当前已准备工具调用的原始 ID。</param>
    /// <param name="currentCheckpoint">Linux handle backend 到达的执行阶段。</param>
    /// <param name="cancellationToken">run/daemon 取消信号。</param>
    /// <remarks>不变量：不匹配调用无副作用；匹配调用最多触发一次，ready 发布后最多等待 5000 ms。</remarks>
    /// <exception cref="IOException">ready/release 类型、内容、持久化或 timeout 无效。</exception>
    /// <exception cref="OperationCanceledException">等待期间 run 被取消。</exception>
    internal async Task ReachAsync(
        string? currentToolCallId,
        PathBoundaryCheckpoint currentCheckpoint,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (currentCheckpoint != checkpoint ||
            !string.Equals(currentToolCallId, toolCallId, StringComparison.Ordinal))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref reached, 1, 0) != 0)
        {
            throw new IOException("path boundary conformance barrier was reached more than once");
        }

        await WriteReadyAsync(cancellationToken).ConfigureAwait(false);
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < ExpectedTimeoutMilliseconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LinuxFileSystemNative.TryOpenRelative(
                    controlDirectoryHandle,
                    ReleaseFile,
                    LinuxFileSystemNative.OpenReadOnly |
                    LinuxFileSystemNative.OpenNonBlocking |
                    LinuxFileSystemNative.OpenNoFollow,
                    out var releaseHandle,
                    out var nativeError))
            {
                using (releaseHandle)
                {
                    var bytes = ReadBoundedRegularFile(releaseHandle!, MaximumBarrierBytes);
                    ValidateBarrierDocument(bytes, "release");
                    return;
                }
            }

            if (nativeError != MissingFileError)
            {
                throw new IOException("path boundary release cannot be opened safely");
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        throw new IOException("path boundary conformance barrier timed out");
    }

    /// <summary>
    /// 功能：在任何工具 I/O 前确认匹配 ID 的工具类型能够到达配置 checkpoint。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="currentToolCallId">即将执行的工具调用 ID。</param>
    /// <param name="toolName">即将执行的规范工具名。</param>
    /// <remarks>不变量：非匹配调用为空操作；read/write 与 checkpoint 不兼容时在路径副作用前失败关闭。</remarks>
    /// <exception cref="IOException">匹配调用不可能到达配置 checkpoint。</exception>
    internal void ValidateToolCall(string? currentToolCallId, string toolName)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!string.Equals(currentToolCallId, toolCallId, StringComparison.Ordinal))
        {
            return;
        }

        var compatible = toolName switch
        {
            "file.read" => checkpoint is
                PathBoundaryCheckpoint.BeforeParentWalk or
                PathBoundaryCheckpoint.AfterParentOpen or
                PathBoundaryCheckpoint.AfterLeafOpenBeforeRead,
            "file.write" => checkpoint is
                PathBoundaryCheckpoint.BeforeParentWalk or
                PathBoundaryCheckpoint.AfterParentOpen or
                PathBoundaryCheckpoint.AfterTempFsyncBeforeRename,
            _ => false,
        };
        if (!compatible)
        {
            throw new IOException("path boundary checkpoint cannot be reached by this tool");
        }
    }

    /// <summary>
    /// 功能：在匹配工具成功结束前确认配置 checkpoint 已精确命中一次。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="currentToolCallId">即将返回成功的工具调用 ID。</param>
    /// <remarks>不变量：其他工具调用无副作用；匹配调用若未发布并消费 release 则失败关闭成功结果。</remarks>
    /// <exception cref="IOException">匹配调用未命中配置 checkpoint。</exception>
    internal void EnsureSatisfied(string? currentToolCallId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (string.Equals(currentToolCallId, toolCallId, StringComparison.Ordinal) &&
            Volatile.Read(ref reached) != 1)
        {
            throw new IOException("path boundary conformance checkpoint was not reached");
        }
    }

    /// <summary>
    /// 功能：严格打开、解析配置并冻结同目录 0700 control fd。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="configurationPath">双门控后才允许读取的配置路径。</param>
    /// <returns>完全验证且拥有控制目录句柄的 barrier。</returns>
    /// <remarks>不变量：配置必须是 bounded regular/no-follow 文件，controlDirectory 独立按绝对路径、no-follow 与 0700 验证。</remarks>
    /// <exception cref="IOException">路径、文件类型、权限、大小或内容无效。</exception>
    /// <exception cref="JsonException">JSON 语法或字段类型无效。</exception>
    [SupportedOSPlatform("linux")]
    private static PathBoundaryConformanceBarrier Load(string configurationPath)
    {
        if (string.IsNullOrEmpty(configurationPath) || configurationPath.Length > 4096)
        {
            throw new IOException("path boundary configuration path is invalid");
        }

        var fullConfigurationPath = Path.GetFullPath(configurationPath);
        using var configurationHandle = LinuxFileSystemNative.OpenAbsolute(
            fullConfigurationPath,
            LinuxFileSystemNative.OpenReadOnly |
            LinuxFileSystemNative.OpenNonBlocking |
            LinuxFileSystemNative.OpenNoFollow);
        var bytes = ReadBoundedRegularFile(configurationHandle, MaximumConfigurationBytes);
        using var document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
        ValidateNoDuplicateKeys(document.RootElement);
        EnsureExactProperties(document.RootElement, ConfigurationProperties);

        var root = document.RootElement;
        var parsedSchemaVersion = RequireString(root, "schemaVersion");
        var parsedCaseId = RequireBoundedIdentifier(root, "caseId");
        var parsedToolCallId = RequireBoundedIdentifier(root, "toolCallId");
        var parsedCheckpoint = ParseCheckpoint(RequireString(root, "checkpoint"));
        var controlDirectory = RequireString(root, "controlDirectory");
        if (parsedSchemaVersion != SchemaVersion ||
            controlDirectory.Length == 0 ||
            controlDirectory.Length > 4096 ||
            !Path.IsPathFullyQualified(controlDirectory) ||
            !root.GetProperty("timeoutMs").TryGetInt32(out var timeoutMilliseconds) ||
            timeoutMilliseconds != ExpectedTimeoutMilliseconds)
        {
            throw new IOException("path boundary configuration fields are invalid");
        }

        var fullControlDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(controlDirectory));
        var controlHandle = LinuxFileSystemNative.OpenAbsolute(
            fullControlDirectory,
            LinuxFileSystemNative.OpenReadOnly |
            LinuxFileSystemNative.OpenDirectory |
            LinuxFileSystemNative.OpenNoFollow);
        try
        {
            var attributes = File.GetAttributes(controlHandle);
            var expectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0 ||
                File.GetUnixFileMode(controlHandle) != expectedMode)
            {
                throw new IOException("path boundary control directory is invalid");
            }

            return new PathBoundaryConformanceBarrier(
                parsedCaseId,
                parsedToolCallId,
                parsedCheckpoint,
                controlHandle);
        }
        catch
        {
            controlHandle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 功能：创建并持久化 exact ready.json，再刷新控制目录项。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="cancellationToken">ready 写入取消信号。</param>
    /// <remarks>不变量：ready 使用 O_CREAT|O_EXCL|O_NOFOLLOW，仅由匹配工具调用发布一次。</remarks>
    /// <exception cref="IOException">ready 已存在、写入或 fsync 失败。</exception>
    private async Task WriteReadyAsync(CancellationToken cancellationToken)
    {
        var payload = SerializeBarrierDocument("ready");
        using var readyHandle = LinuxFileSystemNative.OpenRelative(
            controlDirectoryHandle,
            ReadyFile,
            LinuxFileSystemNative.OpenWriteOnly |
            LinuxFileSystemNative.OpenCreate |
            LinuxFileSystemNative.OpenExclusive |
            LinuxFileSystemNative.OpenNoFollow,
            0x180);
        await using (var stream = new FileStream(readyHandle, FileAccess.Write, 4096, isAsync: false))
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            LinuxFileSystemNative.Synchronize(stream.SafeFileHandle);
        }

        LinuxFileSystemNative.Synchronize(controlDirectoryHandle);
    }

    /// <summary>
    /// 功能：序列化 exact 五字段 ready/release barrier 文档并追加 LF。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="state">仅允许 ready 或 release；本实现只生成 ready。</param>
    /// <returns>小于固定上限的 UTF-8 JSON 与单个 LF。</returns>
    /// <remarks>不变量：case/tool/checkpoint 均取自已验证配置，不接受运行时路径或模型文本。</remarks>
    private byte[] SerializeBarrierDocument(string state)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new BarrierDocument(
                SchemaVersion,
                caseId,
                toolCallId,
                ToWireCheckpoint(checkpoint),
                state),
            JsonDefaults.Options);
        if (json.Length + 1 > MaximumBarrierBytes)
        {
            throw new IOException("path boundary barrier document is too large");
        }

        var result = new byte[json.Length + 1];
        json.CopyTo(result, 0);
        result[^1] = (byte)'\n';
        return result;
    }

    /// <summary>
    /// 功能：验证 release 文档严格匹配本 barrier 的五个字段和值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="bytes">从 no-follow regular release fd 有界读取的完整字节。</param>
    /// <param name="expectedState">固定 release。</param>
    /// <remarks>不变量：未知/重复字段、错误 case/tool/checkpoint/state 均失败关闭。</remarks>
    /// <exception cref="IOException">文档字段不匹配。</exception>
    /// <exception cref="JsonException">JSON 或字段类型无效。</exception>
    private void ValidateBarrierDocument(byte[] bytes, string expectedState)
    {
        using var document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
        ValidateNoDuplicateKeys(document.RootElement);
        EnsureExactProperties(document.RootElement, BarrierProperties);
        var root = document.RootElement;
        if (RequireString(root, "schemaVersion") != SchemaVersion ||
            RequireString(root, "caseId") != caseId ||
            RequireString(root, "toolCallId") != toolCallId ||
            RequireString(root, "checkpoint") != ToWireCheckpoint(checkpoint) ||
            RequireString(root, "state") != expectedState)
        {
            throw new IOException("path boundary release does not match ready");
        }
    }

    /// <summary>
    /// 功能：从已安全打开的 seekable regular fd 有界读取完整文件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="handle">调用方仍拥有的 no-follow 文件句柄。</param>
    /// <param name="maximumBytes">严格正字节上限。</param>
    /// <returns>长度稳定的完整文件字节。</returns>
    /// <remarks>不变量：类型、长度与 EOF 均基于同一 fd；FIFO/device 不会进入阻塞读取。</remarks>
    /// <exception cref="IOException">类型、长度、增长或缩短无效。</exception>
    private static byte[] ReadBoundedRegularFile(SafeFileHandle handle, int maximumBytes)
    {
        var attributes = File.GetAttributes(handle);
        if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException("conformance file is not regular");
        }

        using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        if (!stream.CanSeek || stream.Length < 1 || stream.Length > maximumBytes)
        {
            throw new IOException("conformance file size is invalid");
        }

        var expectedLength = checked((int)stream.Length);
        var result = new byte[expectedLength];
        stream.ReadExactly(result);
        if (stream.ReadByte() != -1 || stream.Length != expectedLength)
        {
            throw new IOException("conformance file changed while reading");
        }

        return result;
    }

    /// <summary>
    /// 功能：递归拒绝 strict 配置和 barrier 文档中的重复 JSON key。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">待验证 JSON 根或子值。</param>
    /// <exception cref="IOException">任一对象层级存在重复 key。</exception>
    private static void ValidateNoDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new IOException("conformance JSON contains a duplicate key");
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
    /// 功能：确认 JSON 根对象精确包含约定字段且没有未知或缺失成员。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">配置或 barrier 根值。</param>
    /// <param name="expected">区分大小写的 exact 属性集合。</param>
    /// <exception cref="IOException">根不是对象或属性集合不同。</exception>
    private static void EnsureExactProperties(JsonElement element, IReadOnlySet<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new IOException("conformance JSON root is invalid");
        }

        var actual = element.EnumerateObject().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
        {
            throw new IOException("conformance JSON properties are invalid");
        }
    }

    /// <summary>
    /// 功能：读取配置或 barrier 的必需 JSON string 且拒绝其他类型与空值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="root">已验证 exact 属性的对象。</param>
    /// <param name="name">必需属性名。</param>
    /// <returns>非 null 字符串。</returns>
    /// <exception cref="IOException">属性不是 JSON string 或为 null。</exception>
    private static string RequireString(JsonElement root, string name)
    {
        var element = root.GetProperty(name);
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } value)
        {
            throw new IOException("conformance JSON string is invalid");
        }

        return value;
    }

    /// <summary>
    /// 功能：读取不含控制字符且长度有界的 case/tool 调用标识符。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="root">配置根对象。</param>
    /// <param name="name">caseId 或 toolCallId。</param>
    /// <returns>1..128 ASCII 字节且属于固定字符闭集的原始标识符。</returns>
    /// <exception cref="IOException">长度或字符范围无效。</exception>
    private static string RequireBoundedIdentifier(JsonElement root, string name)
    {
        var value = RequireString(root, name);
        if (value.Length is < 1 or > 128 ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')))
        {
            throw new IOException("conformance identifier is invalid");
        }

        return value;
    }

    /// <summary>
    /// 功能：把 strict wire checkpoint 映射为封闭内部枚举。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">配置中的 checkpoint 原文。</param>
    /// <returns>四个 ADR 0021 检查点之一。</returns>
    /// <exception cref="IOException">checkpoint 未知。</exception>
    private static PathBoundaryCheckpoint ParseCheckpoint(string value)
    {
        return value switch
        {
            "before_parent_walk" => PathBoundaryCheckpoint.BeforeParentWalk,
            "after_parent_open" => PathBoundaryCheckpoint.AfterParentOpen,
            "after_leaf_open_before_read" => PathBoundaryCheckpoint.AfterLeafOpenBeforeRead,
            "after_temp_fsync_before_rename" => PathBoundaryCheckpoint.AfterTempFsyncBeforeRename,
            _ => throw new IOException("path boundary checkpoint is invalid"),
        };
    }

    /// <summary>
    /// 功能：把内部 checkpoint 映射回 exact barrier wire 字符串。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">内部封闭 checkpoint。</param>
    /// <returns>ADR 0021 规范字符串。</returns>
    private static string ToWireCheckpoint(PathBoundaryCheckpoint value)
    {
        return value switch
        {
            PathBoundaryCheckpoint.BeforeParentWalk => "before_parent_walk",
            PathBoundaryCheckpoint.AfterParentOpen => "after_parent_open",
            PathBoundaryCheckpoint.AfterLeafOpenBeforeRead => "after_leaf_open_before_read",
            PathBoundaryCheckpoint.AfterTempFsyncBeforeRename => "after_temp_fsync_before_rename",
            _ => throw new InvalidOperationException("path boundary checkpoint is invalid"),
        };
    }

    /// <summary>
    /// 功能：创建 initialize 使用的固定、不可重试且不泄露配置路径的错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>code=-32603、kind=conformance_configuration_invalid 的 portable error。</returns>
    private static PortableError ConfigurationInvalid()
    {
        return new PortableError(
            -32603,
            "path boundary conformance configuration is invalid",
            false,
            new ErrorDetails("conformance_configuration_invalid"));
    }

    /// <summary>
    /// 功能：释放 0700 control directory fd；不删除 runner 拥有的 ready/release/config 文件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        controlDirectoryHandle.Dispose();
        disposed = true;
    }

    /// <summary>
    /// 功能：定义 ready/release exact 五字段 JSON 文档。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="SchemaVersion">固定 0.1。</param>
    /// <param name="CaseId">runner case ID。</param>
    /// <param name="ToolCallId">匹配工具调用 ID。</param>
    /// <param name="Checkpoint">wire checkpoint。</param>
    /// <param name="State">ready 或 release。</param>
    private sealed record BarrierDocument(
        string SchemaVersion,
        string CaseId,
        string ToolCallId,
        string Checkpoint,
        string State);
}
