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
    /// 功能：保存一张已完整落盘但尚未对 Session 可见的图片及最终引用。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="Reference">预先计算的 portable 内容绑定引用。</param>
    /// <param name="TemporaryPath">artifacts 目录内的随机临时叶路径。</param>
    /// <param name="TargetPath">最终 opaque artifact 叶路径。</param>
    private sealed record StagedImage(
        ArtifactReference Reference,
        string TemporaryPath,
        string TargetPath);

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
    /// 功能：把完整已验证图像作为单项批次暂存并无覆盖发布。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionDirectory">当前 journal 所属 Session 目录。</param>
    /// <param name="mediaType">已由 Provider 与本方法双重魔数校验的 MIME。</param>
    /// <param name="bytes">整批响应验证完成后的单张图像字节。</param>
    /// <param name="maximumBytes">协商的单 artifact 上限。</param>
    /// <param name="cancellationToken">临时写入和发布前取消信号。</param>
    /// <returns>只含 ID、MIME、长度和 SHA-256 的 portable 引用。</returns>
    /// <remarks>不变量：目标永不覆盖；Unix 临时文件与 Move 后目标固定为 owner-only 0600；返回前文件和目录已在支持的平台 durable，journal append 由调用方随后执行。</remarks>
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
        var published = await PublishImageBatchAsync(
            sessionDirectory,
            [(mediaType, bytes)],
            maximumBytes,
            cancellationToken).ConfigureAwait(false);
        return published[0];
    }

    /// <summary>
    /// 功能：先验证并完整暂存整批图片，再统一发布最终叶文件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionDirectory">当前 journal 所属 Session 目录。</param>
    /// <param name="images">已经由 Provider 完整解析的有序 MIME 与图片字节。</param>
    /// <param name="maximumBytes">每张 artifact 的协商上限。</param>
    /// <param name="cancellationToken">验证、暂存和最终发布取消信号。</param>
    /// <returns>全部成功发布后的有序 portable 引用；空批次返回空数组。</returns>
    /// <remarks>不变量：任一候选先验无效时不创建文件；全部临时文件 durable 后才开始最终 rename；失败时尽力移除本批临时文件与尚未被 journal 引用的目标文件。</remarks>
    /// <exception cref="ArtifactValidationException">任一 MIME、空数据、大小或魔数无效。</exception>
    /// <exception cref="IOException">暂存、flush、无覆盖发布或目录 flush 失败。</exception>
    /// <exception cref="OperationCanceledException">最终批次提交前调用方取消。</exception>
    internal static async Task<IReadOnlyList<ArtifactReference>> PublishImageBatchAsync(
        string sessionDirectory,
        IReadOnlyList<(string MediaType, ReadOnlyMemory<byte> Bytes)> images,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var frozenImages = new List<(string MediaType, ReadOnlyMemory<byte> Bytes)>(images.Count);
        foreach (var image in images)
        {
            ValidateImageCandidate(image.MediaType, image.Bytes, maximumBytes);
            var frozenBytes = (ReadOnlyMemory<byte>)image.Bytes.ToArray();
            ValidateImageCandidate(image.MediaType, frozenBytes, maximumBytes);
            frozenImages.Add((image.MediaType, frozenBytes));
        }

        if (images.Count == 0)
        {
            return [];
        }

        var artifactDirectory = ValidateArtifactDirectory(sessionDirectory);
        var staged = new List<StagedImage>(images.Count);
        var publishedTargets = new List<string>(images.Count);
        var committed = false;
        try
        {
            foreach (var image in frozenImages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var artifactId = "artifact-" + Guid.NewGuid().ToString("N");
                var temporary = Path.Combine(
                    artifactDirectory,
                    ".tmp-" + Guid.NewGuid().ToString("N"));
                var target = Path.Combine(artifactDirectory, artifactId);
                await StageImageAsync(temporary, image.Bytes, cancellationToken).ConfigureAwait(false);
                var hash = Convert.ToHexString(SHA256.HashData(image.Bytes.Span)).ToLowerInvariant();
                staged.Add(new StagedImage(
                    new ArtifactReference(
                        artifactId,
                        image.MediaType,
                        image.Bytes.Length,
                        hash),
                    temporary,
                    target));
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (var image in staged)
            {
                File.Move(image.TemporaryPath, image.TargetPath, overwrite: false);
                publishedTargets.Add(image.TargetPath);
            }

            FlushDirectory(artifactDirectory);
            committed = true;
            return staged.Select(static image => image.Reference).ToArray();
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
            foreach (var image in staged)
            {
                TryDeleteFile(image.TemporaryPath);
            }

            if (!committed)
            {
                foreach (var target in publishedTargets)
                {
                    TryDeleteFile(target);
                }
            }
        }
    }

    /// <summary>
    /// 功能：在 journal 批次提交失败后删除整批尚未被任何 durable 记录引用的图片文件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionDirectory">当前 journal 自持 Session 目录。</param>
    /// <param name="artifacts">刚由同一次批量发布返回的有序引用。</param>
    /// <param name="maximumBytes">发布时使用的同一单 artifact 上限。</param>
    /// <remarks>不变量：调用方只能在对应 journal 批次确认未提交时调用；全部引用先验证后才删除，绝不接受 wire 路径。</remarks>
    /// <exception cref="ArtifactValidationException">任一引用不能安全派生目标叶文件。</exception>
    /// <exception cref="IOException">删除任一目标或刷新目录失败。</exception>
    internal static void DeleteUncommittedImageBatch(
        string sessionDirectory,
        IReadOnlyList<ArtifactReference> artifacts,
        long maximumBytes)
    {
        var artifactDirectory = ValidateArtifactDirectory(sessionDirectory);
        var targets = new List<string>(artifacts.Count);
        foreach (var artifact in artifacts)
        {
            try
            {
                ImageArtifactValidation.ValidateReference(artifact, maximumBytes);
            }
            catch (ArgumentException)
            {
                throw new ArtifactValidationException();
            }

            targets.Add(Path.Combine(artifactDirectory, artifact.ArtifactId));
        }

        try
        {
            foreach (var target in targets)
            {
                File.Delete(target);
            }

            FlushDirectory(artifactDirectory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new IOException("uncommitted artifact batch cleanup failed");
        }
    }

    /// <summary>
    /// 功能：验证单张待暂存图片的 MIME、大小和魔数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="mediaType">候选规范图片 MIME。</param>
    /// <param name="bytes">完整候选字节。</param>
    /// <param name="maximumBytes">单 artifact 上限。</param>
    /// <exception cref="ArtifactValidationException">任一内容绑定条件不满足。</exception>
    private static void ValidateImageCandidate(
        string mediaType,
        ReadOnlyMemory<byte> bytes,
        long maximumBytes)
    {
        if (!ImageArtifactValidation.IsSupportedMediaType(mediaType) ||
            bytes.Length == 0 ||
            bytes.Length > maximumBytes ||
            !ImageArtifactValidation.HasMatchingSignature(mediaType, bytes.Span))
        {
            throw new ArtifactValidationException();
        }
    }

    /// <summary>
    /// 功能：把一张已验证图片完整写入 owner-only 同目录临时文件并强制落盘。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="temporary">artifacts 目录内随机且尚不存在的临时叶路径。</param>
    /// <param name="bytes">已通过批次先验验证的完整图片字节。</param>
    /// <param name="cancellationToken">写入与 flush 取消信号。</param>
    /// <returns>临时文件 durable 后完成的 Task。</returns>
    private static async Task StageImageAsync(
        string temporary,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 16_384,
            Options = FileOptions.Asynchronous |
                FileOptions.SequentialScan |
                FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var completed = false;
        try
        {
            await using (var stream = new FileStream(temporary, options))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        temporary,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            completed = true;
        }
        finally
        {
            if (!completed)
            {
                TryDeleteFile(temporary);
            }
        }
    }

    /// <summary>
    /// 功能：在失败清理路径尽力删除单个已知随机临时或未引用目标叶文件。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">仅由本批次在已验证 artifacts 目录内生成的绝对路径。</param>
    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _ = exception;
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
