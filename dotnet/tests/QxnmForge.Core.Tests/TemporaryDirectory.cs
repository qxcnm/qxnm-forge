namespace QxnmForge.Tests;

/// <summary>
/// 功能：为测试创建并尽力清理独占临时目录。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class TemporaryDirectory : IDisposable
{
    /// <summary>
    /// 功能：在系统 temp 下创建随机测试目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public TemporaryDirectory()
    {
        Path = Directory.CreateDirectory(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "qxnm-forge-dotnet-tests",
            Guid.NewGuid().ToString("N"))).FullName;
    }

    /// <summary>
    /// 功能：取得测试临时目录绝对路径。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// 功能：递归删除测试目录；不存在时幂等返回。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
