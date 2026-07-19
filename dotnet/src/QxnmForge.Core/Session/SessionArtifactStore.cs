using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using QxnmForge.Domain;

namespace QxnmForge.Session;

/// <summary>
/// 功能：表示 Session artifact 的 ID、元数据、文件类型或内容绑定不可信。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class ArtifactValidationException : ArgumentException
{
    /// <summary>
    /// 功能：创建不包含 artifact ID、主机路径、hash 或文件字节的安全验证错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal ArtifactValidationException()
        : base("image artifact is unavailable or invalid")
    {
    }
}

/// <summary>
/// 功能：在单个 Session 内执行图像 artifact 的 no-follow 读取与无覆盖 durable 发布。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal static partial class SessionArtifactStore
{
    private const int UnixOpenReadOnly = 0;
    private const int LinuxOpenCloseOnExec = 0x80000;
    private const int LinuxOpenNoFollow = 0x20000;
    private const int MacOpenCloseOnExec = 0x1000000;
    private const int MacOpenNoFollow = 0x100;

    /// <summary>
    /// 功能：从已验证 artifact ID 派生叶路径，以 O_NOFOLLOW 打开并复核长度、hash、MIME 和魔数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionDirectory">由 Session journal 自身持有、从不来自 wire 的目录。</param>
    /// <param name="reference">earlier artifact.created 的 portable 引用。</param>
    /// <param name="maximumBytes">调用场景单 artifact 上限。</param>
    /// <param name="cancellationToken">读取期间取消信号。</param>
    /// <returns>精确匹配引用的图像字节。</returns>
    /// <remarks>不变量：拒绝符号链接、reparse point、目录、长度漂移和摘要漂移；诊断不公开路径。</remarks>
    /// <exception cref="ArtifactValidationException">引用、文件类型、长度、hash、MIME 或魔数不匹配。</exception>
    /// <exception cref="OperationCanceledException">调用方取消读取。</exception>
    internal static async Task<byte[]> ReadImageAsync(
        string sessionDirectory,
        ArtifactReference reference,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            ImageArtifactValidation.ValidateReference(reference, maximumBytes);
            var artifactDirectory = ValidateArtifactDirectory(sessionDirectory);
            var target = Path.Combine(artifactDirectory, reference.ArtifactId);
            await using var stream = OpenReadOnlyNoFollow(target);
            if (!stream.CanSeek || stream.Length != reference.ByteLength || stream.Length > int.MaxValue)
            {
                throw new ArtifactValidationException();
            }

            var bytes = new byte[(int)stream.Length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await stream.ReadAsync(
                    bytes.AsMemory(offset),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new ArtifactValidationException();
                }

                offset += read;
            }

            if (stream.Position != reference.ByteLength || stream.Length != reference.ByteLength)
            {
                throw new ArtifactValidationException();
            }

            var expectedHash = Convert.FromHexString(reference.Sha256);
            var actualHash = SHA256.HashData(bytes);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash) ||
                !ImageArtifactValidation.HasMatchingSignature(reference.MediaType, bytes))
            {
                throw new ArtifactValidationException();
            }

            return bytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArtifactValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or
            NotSupportedException or CryptographicException)
        {
            throw new ArtifactValidationException();
        }
    }

    /// <summary>
    /// 功能：把完整已验证图像写入同目录临时文件，flush 后无覆盖原子发布并刷新目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionDirectory">当前 journal 所属 Session 目录。</param>
    /// <param name="mediaType">已由 Provider 与本方法双重魔数校验的 MIME。</param>
    /// <param name="bytes">整批响应验证完成后的单张图像字节。</param>
    /// <param name="maximumBytes">协商的单 artifact 上限。</param>
    /// <param name="cancellationToken">临时写入和发布前取消信号。</param>
    /// <returns>只含 ID、MIME、长度和 SHA-256 的 portable 引用。</returns>
    /// <remarks>不变量：目标永不覆盖；返回前文件和目录已在支持的平台 durable，journal append 由调用方随后执行。</remarks>
    /// <exception cref="ArtifactValidationException">MIME、空数据、大小或魔数无效。</exception>
    /// <exception cref="IOException">临时写入、flush、无覆盖发布或目录 flush 失败。</exception>
    /// <exception cref="OperationCanceledException">发布前调用方取消。</exception>
    internal static async Task<ArtifactReference> PublishImageAsync(
        string sessionDirectory,
        string mediaType,
        ReadOnlyMemory<byte> bytes,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!ImageArtifactValidation.IsSupportedMediaType(mediaType) ||
            bytes.Length == 0 ||
            bytes.Length > maximumBytes ||
            !ImageArtifactValidation.HasMatchingSignature(mediaType, bytes.Span))
        {
            throw new ArtifactValidationException();
        }

        var artifactDirectory = ValidateArtifactDirectory(sessionDirectory);
        var artifactId = "artifact-" + Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(artifactDirectory, ".tmp-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(artifactDirectory, artifactId);
        var published = false;
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16_384,
                             FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: false);
            published = true;
            FlushDirectory(artifactDirectory);
            var hash = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();
            return new ArtifactReference(artifactId, mediaType, bytes.Length, hash);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArtifactValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new IOException("artifact publication failed");
        }
        finally
        {
            if (!published)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    _ = exception;
                }
            }
        }
    }

    /// <summary>
    /// 功能：验证 Session 的固定 artifacts 目录存在、不是链接/reparse point 且仍为目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionDirectory">journal 自身持有的 Session 目录。</param>
    /// <returns>规范绝对 artifacts 目录。</returns>
    /// <remarks>不变量：wire artifact ID 永不影响父目录选择。</remarks>
    /// <exception cref="ArtifactValidationException">目录缺失、链接化或类型错误。</exception>
    private static string ValidateArtifactDirectory(string sessionDirectory)
    {
        try
        {
            var directory = Path.GetFullPath(Path.Combine(sessionDirectory, "artifacts"));
            var info = new DirectoryInfo(directory);
            info.Refresh();
            if (!info.Exists || info.LinkTarget is not null ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArtifactValidationException();
            }

            return directory;
        }
        catch (ArtifactValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ArtifactValidationException();
        }
    }

    /// <summary>
    /// 功能：在 Unix 使用 O_NOFOLLOW，其他平台以路径和打开句柄双检拒绝叶链接与目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">由安全 opaque ID 派生的绝对 artifact 叶路径。</param>
    /// <returns>异步只读且不共享写/删除的 FileStream。</returns>
    /// <exception cref="ArtifactValidationException">打开失败或句柄不是普通非链接文件。</exception>
    private static FileStream OpenReadOnlyNoFollow(string path)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var flags = UnixOpenReadOnly |
                (OperatingSystem.IsLinux()
                    ? LinuxOpenCloseOnExec | LinuxOpenNoFollow
                    : MacOpenCloseOnExec | MacOpenNoFollow);
            var descriptor = OpenUnixPath(path, flags);
            if (descriptor < 0)
            {
                throw new ArtifactValidationException();
            }

            handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        }
        else
        {
            var info = new FileInfo(path);
            info.Refresh();
            if (!info.Exists || info.LinkTarget is not null ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArtifactValidationException();
            }

            try
            {
                handle = File.OpenHandle(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new ArtifactValidationException();
            }
        }

        try
        {
            var attributes = File.GetAttributes(handle);
            if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            {
                throw new ArtifactValidationException();
            }

            var stream = new FileStream(handle, FileAccess.Read, bufferSize: 16_384, isAsync: false);
            if (!stream.CanSeek)
            {
                stream.Dispose();
                throw new ArtifactValidationException();
            }

            return stream;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 功能：在支持目录 fsync 的 Unix 平台刷新无覆盖 rename 的目录项。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="directory">已验证非链接 artifacts 目录。</param>
    /// <remarks>不变量：Windows 依赖平台 rename durability；Unix 的 open/fsync 失败会阻止 journal 引用。</remarks>
    /// <exception cref="IOException">Unix 目录无法打开或刷新。</exception>
    private static void FlushDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var flags = UnixOpenReadOnly |
            (OperatingSystem.IsLinux()
                ? LinuxOpenCloseOnExec | LinuxOpenNoFollow
                : MacOpenCloseOnExec | MacOpenNoFollow);
        var descriptor = OpenUnixPath(directory, flags);
        if (descriptor < 0)
        {
            throw new IOException("artifact directory flush failed");
        }

        using var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        if (Fsync(descriptor) != 0)
        {
            throw new IOException("artifact directory flush failed");
        }
    }

    /// <summary>
    /// 功能：通过 libc open 对 artifact 叶或目录应用 no-follow/close-on-exec 标志。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">进程内派生的本地路径。</param>
    /// <param name="flags">平台只读、O_NOFOLLOW 与 O_CLOEXEC 标志。</param>
    /// <returns>非负文件描述符；失败为 -1。</returns>
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenUnixPath(string path, int flags);

    /// <summary>
    /// 功能：调用 libc fsync 强制已打开 artifacts 目录项进入持久存储。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="descriptor">由 OpenUnixPath 打开的目录描述符。</param>
    /// <returns>成功为零，失败为 -1。</returns>
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int descriptor);
}
