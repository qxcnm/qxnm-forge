using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace QxnmForge.Executor;

/// <summary>
/// 功能：封装 Linux 工作区句柄访问需要的固定 libc 调用与安全句柄构造。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal static partial class LinuxFileSystemNative
{
    internal const ushort DirectoryFileType = 0x4000;
    internal const ushort RegularFileType = 0x8000;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxBasicStats = 0x000007ff;
    private const ushort FileTypeMask = 0xf000;
    internal const int OpenReadOnly = 0;
    internal const int OpenWriteOnly = 1;
    internal const int OpenCreate = 0x00000040;
    internal const int OpenExclusive = 0x00000080;
    internal const int OpenNonBlocking = 0x00000800;
    internal const int OpenDirectory = 0x00010000;
    internal const int OpenNoFollow = 0x00020000;
    internal const int OpenCloseOnExec = 0x00080000;

    /// <summary>
    /// 功能：以固定 Linux flags 打开绝对路径并把 fd 交给拥有所有权的 SafeFileHandle。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">仅来自已验证 workspace 或双门控配置的绝对路径。</param>
    /// <param name="flags">调用方固定组合的 no-follow、目录或只读标志。</param>
    /// <param name="mode">仅 O_CREAT 使用的权限位；其他调用固定为零。</param>
    /// <returns>拥有原生 fd 且由调用方释放的安全句柄。</returns>
    /// <remarks>不变量：失败消息不包含宿主路径或 errno；返回句柄始终设置 O_CLOEXEC。</remarks>
    /// <exception cref="IOException">open 失败。</exception>
    internal static SafeFileHandle OpenAbsolute(string path, int flags, uint mode = 0)
    {
        var descriptor = Open(path, flags | OpenCloseOnExec, mode);
        if (descriptor < 0)
        {
            throw new IOException("secure Linux file open failed");
        }

        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    /// <summary>
    /// 功能：相对已打开目录执行 Linux openat 并返回拥有 fd 的 SafeFileHandle。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="directory">已由 no-follow 方式打开且仍有效的目录句柄。</param>
    /// <param name="name">单个已验证路径组件或固定点号。</param>
    /// <param name="flags">调用方固定组合的访问与 no-follow 标志。</param>
    /// <param name="mode">仅 O_CREAT 使用的权限位；其他调用固定为零。</param>
    /// <returns>拥有新 fd 且由调用方释放的安全句柄。</returns>
    /// <remarks>不变量：解析锚定 directory inode，不重新解析 workspace 的宿主绝对路径。</remarks>
    /// <exception cref="IOException">openat 失败。</exception>
    internal static SafeFileHandle OpenRelative(
        SafeFileHandle directory,
        string name,
        int flags,
        uint mode = 0)
    {
        var descriptor = OpenAt(directory, name, flags | OpenCloseOnExec, mode);
        if (descriptor < 0)
        {
            throw new IOException("secure Linux relative open failed");
        }

        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    /// <summary>
    /// 功能：尝试相对目录打开 release 文件并区分不存在与安全打开失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="directory">0700 conformance 控制目录句柄。</param>
    /// <param name="name">固定 release 文件名。</param>
    /// <param name="flags">只读、非阻塞与 no-follow 标志。</param>
    /// <param name="handle">成功时返回拥有 fd 的句柄。</param>
    /// <param name="error">失败时返回线程局部 errno。</param>
    /// <returns>openat 成功时为 true。</returns>
    /// <remarks>不变量：失败不跟随链接且不会阻塞打开 FIFO；调用方只可把 ENOENT 解释为尚未发布。</remarks>
    internal static bool TryOpenRelative(
        SafeFileHandle directory,
        string name,
        int flags,
        out SafeFileHandle? handle,
        out int error)
    {
        var descriptor = OpenAt(directory, name, flags | OpenCloseOnExec, 0);
        if (descriptor < 0)
        {
            handle = null;
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        error = 0;
        return true;
    }

    /// <summary>
    /// 功能：在同一已打开父目录内原子替换临时名称为目标叶名称。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parent">同时锚定源名和目标名的稳定父目录句柄。</param>
    /// <param name="temporaryName">本次调用独占创建的临时叶名称。</param>
    /// <param name="targetName">Provider 已审批相对路径的最终叶名称。</param>
    /// <remarks>不变量：renameat 替换符号链接目录项本身而不跟随其目标；两个名称不能跨父目录。</remarks>
    /// <exception cref="IOException">renameat 失败且目标未由本调用确认发布。</exception>
    internal static void RenameRelative(
        SafeFileHandle parent,
        string temporaryName,
        string targetName)
    {
        if (RenameAt(parent, temporaryName, parent, targetName) != 0)
        {
            throw new IOException("secure Linux atomic rename failed");
        }
    }

    /// <summary>
    /// 功能：刷新已打开普通文件或目录的内容与目录项到持久存储。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="handle">有效且支持 fsync 的文件或只读目录句柄。</param>
    /// <remarks>不变量：调用期间 SafeFileHandle 保持引用；失败不会被误报为已持久化成功。</remarks>
    /// <exception cref="IOException">fsync 失败。</exception>
    internal static void Synchronize(SafeFileHandle handle)
    {
        if (Fsync(handle) != 0)
        {
            throw new IOException("secure Linux fsync failed");
        }
    }

    /// <summary>
    /// 功能：尽力删除稳定父目录内属于本次调用的临时名称。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="parent">创建临时文件时使用的同一父目录句柄。</param>
    /// <param name="temporaryName">随机且进程内生成的临时叶名称。</param>
    /// <remarks>不变量：只删除 parent 下的单个固定叶名称；清理失败不得覆盖原始工具结果。</remarks>
    internal static void TryUnlinkRelative(SafeFileHandle parent, string temporaryName)
    {
        _ = UnlinkAt(parent, temporaryName, 0);
    }

    /// <summary>
    /// 功能：以 Linux statx AT_EMPTY_PATH 读取已打开 fd 的类型与稳定 device/inode 身份。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="handle">已由 no-follow open/openat 建立的有效句柄。</param>
    /// <param name="expectedType">固定 S_IFDIR 或 S_IFREG 类型位。</param>
    /// <returns>可用于 startup root 与当前可见根比较的 device/inode 身份。</returns>
    /// <remarks>不变量：类型和身份均从同一 fd 获取，不重新按 pathname 查询。</remarks>
    /// <exception cref="IOException">statx 失败或句柄类型不符。</exception>
    internal static LinuxFileIdentity ReadIdentity(SafeFileHandle handle, ushort expectedType)
    {
        if (Statx(handle, string.Empty, AtEmptyPath, StatxBasicStats, out var metadata) != 0 ||
            (metadata.Mode & FileTypeMask) != expectedType)
        {
            throw new IOException("secure Linux handle identity is invalid");
        }

        return new LinuxFileIdentity(metadata.Inode, metadata.DeviceMajor, metadata.DeviceMinor);
    }

    /// <summary>
    /// 功能：调用 libc open 并始终传入确定 mode，避免托管路径 API 的再次解析。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">UTF-8 绝对路径。</param>
    /// <param name="flags">Linux open flags。</param>
    /// <param name="mode">创建权限或零。</param>
    /// <returns>非负 fd；失败为 -1。</returns>
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags, uint mode);

    /// <summary>
    /// 功能：调用 libc openat，把名称解析锚定到 SafeFileHandle 目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="directory">稳定目录句柄。</param>
    /// <param name="name">UTF-8 相对名称。</param>
    /// <param name="flags">Linux open flags。</param>
    /// <param name="mode">创建权限或零。</param>
    /// <returns>非负 fd；失败为 -1。</returns>
    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAt(SafeFileHandle directory, string name, int flags, uint mode);

    /// <summary>
    /// 功能：调用 libc renameat 在稳定目录句柄间原子移动目录项。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="oldDirectory">源目录句柄。</param>
    /// <param name="oldName">源叶名称。</param>
    /// <param name="newDirectory">目标目录句柄。</param>
    /// <param name="newName">目标叶名称。</param>
    /// <returns>成功为零，失败为 -1。</returns>
    [LibraryImport("libc", EntryPoint = "renameat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameAt(
        SafeFileHandle oldDirectory,
        string oldName,
        SafeFileHandle newDirectory,
        string newName);

    /// <summary>
    /// 功能：调用 libc unlinkat 清理稳定父目录内的单个临时文件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="directory">稳定父目录句柄。</param>
    /// <param name="name">临时叶名称。</param>
    /// <param name="flags">普通文件固定为零。</param>
    /// <returns>成功为零，失败为 -1。</returns>
    [LibraryImport("libc", EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int UnlinkAt(SafeFileHandle directory, string name, int flags);

    /// <summary>
    /// 功能：调用 libc fsync 持久化已打开文件或目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="handle">有效 SafeFileHandle。</param>
    /// <returns>成功为零，失败为 -1。</returns>
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(SafeFileHandle handle);

    /// <summary>
    /// 功能：调用 Linux statx 读取 SafeFileHandle 对应对象的稳定身份与类型。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="handle">有效文件或目录句柄。</param>
    /// <param name="path">AT_EMPTY_PATH 使用的固定空字符串。</param>
    /// <param name="flags">固定 AT_EMPTY_PATH。</param>
    /// <param name="mask">固定 STATX_BASIC_STATS。</param>
    /// <param name="buffer">内核填充的 256 字节 statx 结构。</param>
    /// <returns>成功为零，失败为 -1。</returns>
    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(
        SafeFileHandle handle,
        string path,
        int flags,
        uint mask,
        out StatxBuffer buffer);

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct StatxBuffer
    {
        internal uint Mask;
        internal uint BlockSize;
        internal ulong Attributes;
        internal uint HardLinkCount;
        internal uint UserId;
        internal uint GroupId;
        internal ushort Mode;
        internal ushort Spare;
        internal ulong Inode;
        internal ulong Size;
        internal ulong Blocks;
        internal ulong AttributesMask;
        internal StatxTimestamp AccessTime;
        internal StatxTimestamp BirthTime;
        internal StatxTimestamp ChangeTime;
        internal StatxTimestamp ModifiedTime;
        internal uint DeviceSpecialMajor;
        internal uint DeviceSpecialMinor;
        internal uint DeviceMajor;
        internal uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp
    {
        internal long Seconds;
        internal uint Nanoseconds;
        internal int Reserved;
    }
}

/// <summary>
/// 功能：表示 Linux statx 返回且可跨 pathname rebind 比较的目录身份。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Inode">文件系统 inode。</param>
/// <param name="DeviceMajor">宿主设备 major。</param>
/// <param name="DeviceMinor">宿主设备 minor。</param>
internal readonly record struct LinuxFileIdentity(ulong Inode, uint DeviceMajor, uint DeviceMinor);

/// <summary>
/// 功能：以持久 workspace root fd 和逐组件 openat 实现 Linux 文件读写边界。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class LinuxWorkspaceFileAccess : IDisposable
{
    private const int DirectoryFlags = LinuxFileSystemNative.OpenReadOnly |
        LinuxFileSystemNative.OpenDirectory |
        LinuxFileSystemNative.OpenNoFollow;
    private readonly PathBoundaryConformanceBarrier? barrier;
    private readonly LinuxFileIdentity rootIdentity;
    private readonly SafeFileHandle rootHandle;
    private readonly string rootPath;
    private bool disposed;

    /// <summary>
    /// 功能：在 daemon/registry 生命周期开始时冻结 canonical workspace 根目录句柄。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="root">已规范化且当时存在的 workspace 根路径。</param>
    /// <param name="barrier">可选、已通过双门控严格解析的一次性 conformance barrier。</param>
    /// <remarks>不变量：后续工具执行不再按 root 宿主路径解析；根路径 rename/rebind 只会改变名称，不会改变本句柄 inode。</remarks>
    /// <exception cref="IOException">根无法以 no-follow 目录方式打开或句柄类型错误。</exception>
    internal LinuxWorkspaceFileAccess(string root, PathBoundaryConformanceBarrier? barrier)
    {
        this.barrier = barrier;
        rootPath = root;
        rootHandle = LinuxFileSystemNative.OpenAbsolute(root, DirectoryFlags);
        try
        {
            EnsureDirectory(rootHandle);
            rootIdentity = LinuxFileSystemNative.ReadIdentity(
                rootHandle,
                LinuxFileSystemNative.DirectoryFileType);
        }
        catch
        {
            rootHandle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 功能：从稳定 root/parent/leaf 句柄打开 regular file，并在首字节读取前执行匹配 barrier。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="relativePath">Prepare 保存的 canonical workspace 相对路径。</param>
    /// <param name="toolCallId">当前 Provider 工具调用 ID；只用于精确匹配测试 barrier。</param>
    /// <param name="maximumBytes">leaf fd 在 barrier 前必须满足的正字节上限。</param>
    /// <param name="cancellationToken">barrier 等待与后续读取的取消信号。</param>
    /// <returns>拥有 leaf fd、异步可读且由调用方释放的 FileStream。</returns>
    /// <remarks>不变量：所有目录组件和 leaf 均 O_NOFOLLOW；barrier 后的叶名称替换不会改变返回 fd。</remarks>
    /// <exception cref="IOException">路径组件、叶类型或原生打开失败。</exception>
    /// <exception cref="OperationCanceledException">等待 barrier 时取消。</exception>
    internal async Task<FileStream> OpenReadAsync(
        string relativePath,
        string? toolCallId,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var (parent, leaf) = await OpenParentAsync(relativePath, toolCallId, cancellationToken).ConfigureAwait(false);
        using (parent)
        {
            SafeFileHandle? leafHandle = null;
            FileStream? stream = null;
            try
            {
                leafHandle = LinuxFileSystemNative.OpenRelative(
                    parent,
                    leaf,
                    LinuxFileSystemNative.OpenReadOnly |
                    LinuxFileSystemNative.OpenNonBlocking |
                    LinuxFileSystemNative.OpenNoFollow);
                EnsureRegularFile(leafHandle);
                stream = new FileStream(leafHandle, FileAccess.Read, 8192, isAsync: false);
                leafHandle = null;
                if (!stream.CanSeek)
                {
                    throw new IOException("workspace leaf is not a seekable regular file");
                }

                if (maximumBytes <= 0 || stream.Length < 0 || stream.Length > maximumBytes)
                {
                    throw new IOException("workspace leaf exceeds the bounded read limit");
                }

                if (barrier is not null)
                {
                    await barrier.ReachAsync(
                        toolCallId,
                        PathBoundaryCheckpoint.AfterLeafOpenBeforeRead,
                        cancellationToken).ConfigureAwait(false);
                }

                var result = stream;
                stream = null;
                return result;
            }
            finally
            {
                stream?.Dispose();
                leafHandle?.Dispose();
            }
        }
    }

    /// <summary>
    /// 功能：确认匹配工具调用的配置 checkpoint 已在成功返回前命中一次。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="toolCallId">即将返回成功的 Provider 工具调用 ID。</param>
    /// <remarks>不变量：生产无 barrier 与非匹配调用均为空操作；匹配但未命中时失败关闭。</remarks>
    /// <exception cref="IOException">匹配调用未到达配置 checkpoint。</exception>
    internal void EnsureBarrierSatisfied(string? toolCallId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        barrier?.EnsureSatisfied(toolCallId);
    }

    /// <summary>
    /// 功能：在文件工具 I/O 前验证匹配 barrier 的工具/checkpoint 组合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="toolCallId">即将执行的工具调用 ID。</param>
    /// <param name="toolName">file.read、file.write 或其他规范工具名。</param>
    /// <remarks>不变量：生产和非匹配调用为空操作；不可能命中的配置在任何路径副作用前失败。</remarks>
    /// <exception cref="IOException">工具不可能到达配置 checkpoint。</exception>
    internal void ValidateBarrierToolCall(string? toolCallId, string toolName)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        barrier?.ValidateToolCall(toolCallId, toolName);
    }

    /// <summary>
    /// 功能：在稳定父目录 fd 内创建、刷新并原子发布 UTF-8 文件内容。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="relativePath">Prepare 保存的 canonical workspace 相对目标。</param>
    /// <param name="bytes">已在工具层完成上限检查的完整 UTF-8 字节。</param>
    /// <param name="toolCallId">当前 Provider 工具调用 ID；只用于精确匹配测试 barrier。</param>
    /// <param name="cancellationToken">写入和 barrier 等待取消信号。</param>
    /// <remarks>不变量：temp 与 target 始终相对同一 parent fd；temp fsync 先于 barrier/renameat，renameat 先于 parent fsync。</remarks>
    /// <exception cref="IOException">组件打开、写入、fsync 或 renameat 失败。</exception>
    /// <exception cref="OperationCanceledException">发布前取消，临时文件会尽力清理。</exception>
    internal async Task WriteAtomicallyAsync(
        string relativePath,
        byte[] bytes,
        string? toolCallId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var (parent, leaf) = await OpenParentAsync(relativePath, toolCallId, cancellationToken).ConfigureAwait(false);
        using (parent)
        {
            var temporaryName = ".agent-write-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var temporaryHandle = LinuxFileSystemNative.OpenRelative(
                           parent,
                           temporaryName,
                           LinuxFileSystemNative.OpenWriteOnly |
                           LinuxFileSystemNative.OpenCreate |
                           LinuxFileSystemNative.OpenExclusive |
                           LinuxFileSystemNative.OpenNoFollow,
                           0x180))
                {
                    await using var stream = new FileStream(
                        temporaryHandle,
                        FileAccess.Write,
                        8192,
                        isAsync: false);
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    LinuxFileSystemNative.Synchronize(stream.SafeFileHandle);
                }

                if (barrier is not null)
                {
                    await barrier.ReachAsync(
                        toolCallId,
                        PathBoundaryCheckpoint.AfterTempFsyncBeforeRename,
                        cancellationToken).ConfigureAwait(false);
                }

                LinuxFileSystemNative.RenameRelative(parent, temporaryName, leaf);
                using var directoryFlushHandle = LinuxFileSystemNative.OpenRelative(parent, ".", DirectoryFlags);
                EnsureDirectory(directoryFlushHandle);
                LinuxFileSystemNative.Synchronize(directoryFlushHandle);
            }
            finally
            {
                LinuxFileSystemNative.TryUnlinkRelative(parent, temporaryName);
            }
        }
    }

    /// <summary>
    /// 功能：从持久 root fd 逐组件打开目标父目录并触发父目录阶段 barrier。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="relativePath">canonical workspace 相对文件路径。</param>
    /// <param name="toolCallId">当前工具调用 ID。</param>
    /// <param name="cancellationToken">barrier 等待取消信号。</param>
    /// <returns>调用方拥有的稳定父目录 SafeFileHandle 与单个叶名称。</returns>
    /// <remarks>不变量：before_parent_walk 发生在任何每次调用的 openat 前；after_parent_open 发生在最终 parent fd 建立后。</remarks>
    /// <exception cref="IOException">路径词法无效或任一组件不是 no-follow 目录。</exception>
    private async Task<(SafeFileHandle Parent, string Leaf)> OpenParentAsync(
        string relativePath,
        string? toolCallId,
        CancellationToken cancellationToken)
    {
        var components = ParseRelativeComponents(relativePath);
        VerifyVisibleRoot();
        if (barrier is not null)
        {
            await barrier.ReachAsync(
                toolCallId,
                PathBoundaryCheckpoint.BeforeParentWalk,
                cancellationToken).ConfigureAwait(false);
        }

        var current = LinuxFileSystemNative.OpenRelative(rootHandle, ".", DirectoryFlags);
        try
        {
            EnsureDirectory(current);
            for (var index = 0; index < components.Length - 1; index++)
            {
                var next = LinuxFileSystemNative.OpenRelative(current, components[index], DirectoryFlags);
                try
                {
                    EnsureDirectory(next);
                }
                catch
                {
                    next.Dispose();
                    throw;
                }

                current.Dispose();
                current = next;
            }

            if (barrier is not null)
            {
                await barrier.ReachAsync(
                    toolCallId,
                    PathBoundaryCheckpoint.AfterParentOpen,
                    cancellationToken).ConfigureAwait(false);
            }

            var result = current;
            current = null!;
            return (result, components[^1]);
        }
        finally
        {
            current?.Dispose();
        }
    }

    /// <summary>
    /// 功能：在 before_parent_walk 前确认可见 root 路径仍绑定 startup root fd 身份。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <remarks>不变量：检查只发生在 ready 前；release 后执行只依赖持久 root/parent/leaf fd。</remarks>
    /// <exception cref="IOException">root 消失、变成链接/非目录或 device/inode 已变化。</exception>
    private void VerifyVisibleRoot()
    {
        using var visibleRoot = LinuxFileSystemNative.OpenAbsolute(rootPath, DirectoryFlags);
        EnsureDirectory(visibleRoot);
        var visibleIdentity = LinuxFileSystemNative.ReadIdentity(
            visibleRoot,
            LinuxFileSystemNative.DirectoryFileType);
        if (visibleIdentity != rootIdentity)
        {
            throw new IOException("workspace root identity changed before execution");
        }
    }

    /// <summary>
    /// 功能：验证 PreparedToolCall 携带的 portable 相对路径只含普通组件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="relativePath">工具 Prepare 阶段生成的 canonical 相对路径。</param>
    /// <returns>至少包含 leaf 的路径组件数组。</returns>
    /// <remarks>不变量：拒绝绝对路径、空段、点号、父级、NUL 与反斜杠，避免不同平台分隔符歧义。</remarks>
    /// <exception cref="IOException">相对路径不满足句柄 walker 约束。</exception>
    private static string[] ParseRelativeComponents(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) ||
            relativePath.Length > 4096 ||
            relativePath[0] == '/' ||
            relativePath.Contains('\\', StringComparison.Ordinal) ||
            relativePath.Contains('\0', StringComparison.Ordinal))
        {
            throw new IOException("workspace relative path is invalid");
        }

        var components = relativePath.Split('/', StringSplitOptions.None);
        if (components.Length == 0 || components.Any(static component => component is "" or "." or ".."))
        {
            throw new IOException("workspace relative path is invalid");
        }

        return components;
    }

    /// <summary>
    /// 功能：通过已打开句柄属性确认目标是目录且不是 device/reparse point。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="handle">O_DIRECTORY/O_NOFOLLOW 打开的有效句柄。</param>
    /// <remarks>不变量：类型判断基于 fd，不重新按名称查询。</remarks>
    /// <exception cref="IOException">句柄类型不是普通目录。</exception>
    private static void EnsureDirectory(SafeFileHandle handle)
    {
        _ = LinuxFileSystemNative.ReadIdentity(handle, LinuxFileSystemNative.DirectoryFileType);
    }

    /// <summary>
    /// 功能：通过已打开叶 fd 确认目标不是目录、设备或 reparse point。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="handle">O_NOFOLLOW 打开的 leaf 句柄。</param>
    /// <remarks>不变量：检查对象与后续 FileStream 读取对象为同一 fd。</remarks>
    /// <exception cref="IOException">句柄不是允许读取的 regular file。</exception>
    private static void EnsureRegularFile(SafeFileHandle handle)
    {
        _ = LinuxFileSystemNative.ReadIdentity(handle, LinuxFileSystemNative.RegularFileType);
    }

    /// <summary>
    /// 功能：释放持久 root fd 与其拥有的一次性 barrier，不删除工作区或控制文件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        rootHandle.Dispose();
        barrier?.Dispose();
        disposed = true;
    }
}
