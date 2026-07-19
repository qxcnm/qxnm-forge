namespace QxnmForge.Executor;

/// <summary>
/// 功能：提供工作区路径与现存符号链接边界的 policy-restricted 前置检查。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class WorkspaceBoundary : IDisposable
{
    private readonly LinuxWorkspaceFileAccess? linuxFileAccess;
    private readonly StringComparison pathComparison;
    private bool disposed;

    /// <summary>
    /// 功能：规范化并绑定必须存在的工作区根目录，Linux 同时冻结持久 root fd。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="workspace">必须存在的工作区目录。</param>
    /// <remarks>不变量：Linux 文件工具从本对象持有的 root fd 开始解析；其他平台保留路径式 policy fallback。</remarks>
    /// <exception cref="ArgumentException">路径不存在或不是目录。</exception>
    /// <exception cref="IOException">Linux 无法安全打开或验证 root directory handle。</exception>
    public WorkspaceBoundary(string workspace)
        : this(workspace, barrier: null)
    {
    }

    /// <summary>
    /// 功能：规范化 workspace，并在 Linux 上绑定持久 root fd 与可选双门控 barrier。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="workspace">必须存在的工作区目录。</param>
    /// <param name="barrier">仅双门控严格配置成功时存在的一次性测试 barrier。</param>
    /// <remarks>不变量：Linux backend 拥有 barrier；其他平台立即释放 barrier 并保留原路径 fallback。</remarks>
    /// <exception cref="ArgumentException">路径不存在或不是目录。</exception>
    /// <exception cref="IOException">Linux 无法冻结安全 root 目录句柄。</exception>
    internal WorkspaceBoundary(string workspace, PathBoundaryConformanceBarrier? barrier)
    {
        if (!Directory.Exists(workspace))
        {
            throw new ArgumentException("workspace must be an existing directory", nameof(workspace));
        }

        Root = Path.TrimEndingDirectorySeparator(CanonicalizeExisting(workspace));
        pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (OperatingSystem.IsLinux())
        {
            try
            {
                linuxFileAccess = new LinuxWorkspaceFileAccess(Root, barrier);
            }
            catch
            {
                barrier?.Dispose();
                throw;
            }
        }
        else
        {
            barrier?.Dispose();
        }
    }

    /// <summary>
    /// 功能：取得绝对规范化工作区根路径。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string Root { get; }

    /// <summary>
    /// 功能：解析必须存在的 read/list/search 路径，并拒绝 lexical traversal 与已存在 symlink 逃逸。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="requestedPath">相对工作区或绝对候选路径。</param>
    /// <returns>经过当前文件系统链接解析、仍位于工作区的绝对路径。</returns>
    /// <remarks>不变量：返回路径在检查时位于 Root；这是 policy 边界而非 hard sandbox，executor 仍需 handle/no-follow 防 TOCTOU。</remarks>
    /// <exception cref="UnauthorizedAccessException">路径穿越、工作区外路径或 symlink 逃逸。</exception>
    /// <exception cref="FileNotFoundException">候选路径不存在。</exception>
    public string ResolveExisting(string requestedPath)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var lexical = Path.GetFullPath(requestedPath, Root);
        EnsureInside(lexical, "workspace_boundary");
        if (!File.Exists(lexical) && !Directory.Exists(lexical))
        {
            throw new FileNotFoundException("workspace path does not exist");
        }

        var relative = Path.GetRelativePath(Root, lexical);
        var current = Root;
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            var attributes = File.GetAttributes(current);
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (info.LinkTarget is null)
            {
                continue;
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new UnauthorizedAccessException("symlink target cannot be resolved");
            current = Path.GetFullPath(target.FullName);
            EnsureInside(current, "symlink_escape");
        }

        EnsureInside(current, "workspace_boundary");
        return current;
    }

    /// <summary>
    /// 功能：解析工具提供的严格工作区相对现存路径，拒绝绝对路径、父级分量和符号链接逃逸。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="requestedPath">不受信任的工作区相对路径。</param>
    /// <returns>检查时位于 canonical workspace 内的真实绝对路径。</returns>
    /// <remarks>不变量：本方法仅建立 policy-restricted 边界，不声明 hard sandbox；调用方仍需在打开/替换前复验。</remarks>
    /// <exception cref="UnauthorizedAccessException">绝对路径、穿越、设备语法或链接逃逸。</exception>
    /// <exception cref="FileNotFoundException">目标不存在。</exception>
    public string ResolveExistingRelative(string requestedPath)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureToolRelativePath(requestedPath);
        return ResolveExisting(requestedPath);
    }

    /// <summary>
    /// 功能：解析工具写入目标并验证每个现存祖先不含链接/重解析逃逸。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="requestedPath">不受信任的工作区相对文件路径。</param>
    /// <returns>位于 workspace 内的词法绝对目标。</returns>
    /// <remarks>不变量：返回时目标或最近现存祖先位于 Root；实际副作用前必须调用 RevalidateForWrite。</remarks>
    /// <exception cref="UnauthorizedAccessException">路径语法、链接、重解析点或边界无效。</exception>
    public string ResolveForWrite(string requestedPath)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureToolRelativePath(requestedPath);
        var candidate = Path.GetFullPath(requestedPath, Root);
        EnsureInside(candidate, "workspace_boundary");
        var relative = Path.GetRelativePath(Root, candidate);
        var current = Root;
        var components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < components.Length; index++)
        {
            current = Path.Combine(current, components[index]);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            var attributes = File.GetAttributes(current);
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (info.LinkTarget is not null ||
                OperatingSystem.IsWindows() && (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("write path contains a link or reparse point");
            }

            if (index < components.Length - 1 && (attributes & FileAttributes.Directory) == 0)
            {
                throw new UnauthorizedAccessException("write ancestor is not a directory");
            }
        }

        return candidate;
    }

    /// <summary>
    /// 功能：在实际写入/替换前重新验证预检目标，发现链接交换或路径变化时失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="absolutePath">此前 ResolveForWrite 返回的目标。</param>
    /// <exception cref="UnauthorizedAccessException">目标已变化、越界或出现链接/重解析点。</exception>
    public void RevalidateForWrite(string absolutePath)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var normalized = Path.GetFullPath(absolutePath);
        EnsureInside(normalized, "workspace_boundary");
        var relative = Path.GetRelativePath(Root, normalized);
        var current = ResolveForWrite(relative);
        if (!string.Equals(current, normalized, pathComparison))
        {
            throw new UnauthorizedAccessException("write target changed after preflight");
        }
    }

    /// <summary>
    /// 功能：指示当前 Linux workspace 是否已建立持久 root fd 文件访问 backend。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>仅 Linux 安全 backend 存在且对象未释放时为 true。</returns>
    internal bool UsesLinuxFileHandles => !disposed && linuxFileAccess is not null;

    /// <summary>
    /// 功能：通过 Linux root/parent/leaf fd 打开审批相对路径的 regular file。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="relativePath">PreparedToolCall 保存的 canonical 相对路径。</param>
    /// <param name="toolCallId">当前工具调用 ID，用于匹配测试 barrier。</param>
    /// <param name="maximumBytes">barrier 前必须验证的 regular file 字节上限。</param>
    /// <param name="cancellationToken">barrier 与读取取消信号。</param>
    /// <returns>调用方拥有的异步只读 FileStream。</returns>
    /// <remarks>不变量：只允许 UsesLinuxFileHandles=true 时调用；绝不退回绝对路径打开。</remarks>
    /// <exception cref="PlatformNotSupportedException">当前平台没有 Linux handle backend。</exception>
    /// <exception cref="IOException">组件或 leaf 无法安全打开。</exception>
    internal Task<FileStream> OpenLinuxReadAsync(
        string relativePath,
        string? toolCallId,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (linuxFileAccess is null)
        {
            throw new PlatformNotSupportedException("Linux workspace handles are unavailable");
        }

        return linuxFileAccess.OpenReadAsync(relativePath, toolCallId, maximumBytes, cancellationToken);
    }

    /// <summary>
    /// 功能：通过 Linux 稳定 parent fd 同目录临时写入、fsync 与 renameat 发布目标。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="relativePath">PreparedToolCall 保存的 canonical 相对目标。</param>
    /// <param name="bytes">完整且有界的 UTF-8 内容。</param>
    /// <param name="toolCallId">当前工具调用 ID，用于匹配测试 barrier。</param>
    /// <param name="cancellationToken">写入与 barrier 取消信号。</param>
    /// <returns>目录 fsync 完成后的 Task。</returns>
    /// <remarks>不变量：只允许 UsesLinuxFileHandles=true 时调用；temp/target 始终锚定相同 parent fd。</remarks>
    /// <exception cref="PlatformNotSupportedException">当前平台没有 Linux handle backend。</exception>
    /// <exception cref="IOException">组件、写入、fsync 或 renameat 失败。</exception>
    internal Task WriteLinuxAtomicallyAsync(
        string relativePath,
        byte[] bytes,
        string? toolCallId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (linuxFileAccess is null)
        {
            throw new PlatformNotSupportedException("Linux workspace handles are unavailable");
        }

        return linuxFileAccess.WriteAtomicallyAsync(relativePath, bytes, toolCallId, cancellationToken);
    }

    /// <summary>
    /// 功能：在 Linux 文件工具返回成功前确认匹配 conformance checkpoint 已命中。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="toolCallId">即将完成的工具调用 ID。</param>
    /// <remarks>不变量：生产与非 Linux 调用为空操作；配置匹配但未命中时不允许成功结果。</remarks>
    /// <exception cref="IOException">配置 checkpoint 未命中。</exception>
    internal void EnsureLinuxBarrierSatisfied(string? toolCallId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        linuxFileAccess?.EnsureBarrierSatisfied(toolCallId);
    }

    /// <summary>
    /// 功能：在工具执行前验证匹配的 Linux conformance 工具/checkpoint 组合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="toolCallId">即将执行的工具调用 ID。</param>
    /// <param name="toolName">规范工具名。</param>
    /// <remarks>不变量：无 barrier、非 Linux 或非匹配调用为空操作；不兼容配置在工具副作用前失败。</remarks>
    /// <exception cref="IOException">匹配调用不可能到达配置 checkpoint。</exception>
    internal void ValidateLinuxBarrierToolCall(string? toolCallId, string toolName)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        linuxFileAccess?.ValidateBarrierToolCall(toolCallId, toolName);
    }

    /// <summary>
    /// 功能：释放 Linux 持久 root fd 与 conformance control fd，不修改任何工作区内容。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        linuxFileAccess?.Dispose();
        disposed = true;
    }

    /// <summary>
    /// 功能：拒绝工具路径中的绝对、父级、空值、NUL 与 Windows 设备/ADS 语法。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="requestedPath">未经信任的路径文本。</param>
    /// <exception cref="UnauthorizedAccessException">路径不是普通工作区相对路径。</exception>
    private static void EnsureToolRelativePath(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath) ||
            requestedPath.Contains('\0', StringComparison.Ordinal) ||
            Path.IsPathRooted(requestedPath) ||
            requestedPath.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(static component => component == "..") ||
            OperatingSystem.IsWindows() &&
            (requestedPath.Contains(':', StringComparison.Ordinal) ||
             requestedPath.StartsWith("\\\\", StringComparison.Ordinal) ||
             requestedPath.StartsWith("\\?\\", StringComparison.Ordinal)))
        {
            throw new UnauthorizedAccessException("tool path is not workspace-relative");
        }
    }

    /// <summary>
    /// 功能：逐组件解析现存路径中的链接，得到可作为边界基准的 canonical 绝对路径。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">必须存在的文件系统路径。</param>
    /// <returns>解析最终链接后的绝对路径。</returns>
    /// <exception cref="ArgumentException">路径无法完整解析。</exception>
    private static string CanonicalizeExisting(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(pathRoot))
        {
            throw new ArgumentException("path has no filesystem root", nameof(path));
        }

        var current = pathRoot;
        var relative = Path.GetRelativePath(pathRoot, fullPath);
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, component);
            if (!File.Exists(next) && !Directory.Exists(next))
            {
                throw new ArgumentException("path cannot be canonicalized", nameof(path));
            }

            var attributes = File.GetAttributes(next);
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(next)
                : new FileInfo(next);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            current = target is null ? next : Path.GetFullPath(target.FullName);
        }

        return Path.GetFullPath(current);
    }

    /// <summary>
    /// 功能：执行组件感知的工作区包含关系判断，避免 sibling-prefix 绕过。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="candidate">绝对候选路径。</param>
    /// <param name="reason">拒绝时的稳定安全原因。</param>
    /// <exception cref="UnauthorizedAccessException">候选不是 Root 本身或其子路径。</exception>
    private void EnsureInside(string candidate, string reason)
    {
        var relative = Path.GetRelativePath(Root, candidate);
        if (relative == ".")
        {
            return;
        }

        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", pathComparison) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, pathComparison) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, pathComparison))
        {
            throw new UnauthorizedAccessException(reason);
        }
    }
}
