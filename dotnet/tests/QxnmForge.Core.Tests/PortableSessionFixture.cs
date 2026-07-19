namespace QxnmForge.Tests;

/// <summary>
/// 功能：把只读跨实现 JSONL fixture 安装到测试临时 Session 根目录。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal static class PortableSessionFixture
{
    /// <summary>
    /// 功能：安装由 Rust 格式、非规范空白和未知 extensions 组成的线性 portable Session。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionsRoot">测试临时 sessions 根目录。</param>
    /// <returns>目标 journal 文件路径。</returns>
    internal static string InstallRustLinearSession(string sessionsRoot)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "rust-linear-session.jsonl");
        var sessionDirectory = Directory.CreateDirectory(Path.Combine(sessionsRoot, "rust-linear")).FullName;
        Directory.CreateDirectory(Path.Combine(sessionDirectory, "artifacts"));
        var destination = Path.Combine(sessionDirectory, "journal.jsonl");
        File.Copy(source, destination);
        return destination;
    }

    /// <summary>
    /// 功能：安装公共 portable-v0.1 崩溃前 Session fixture，供恢复与 continuation 黑盒测试。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionsRoot">测试临时 sessions 根目录。</param>
    /// <returns>目标 journal 文件路径。</returns>
    internal static string InstallSharedRecoverySession(string sessionsRoot)
    {
        var source = GetSharedFixturePath("journal.before-recovery.jsonl");
        var sessionDirectory = Directory.CreateDirectory(
            Path.Combine(sessionsRoot, "session-portable-1")).FullName;
        Directory.CreateDirectory(Path.Combine(sessionDirectory, "artifacts"));
        var destination = Path.Combine(sessionDirectory, "journal.jsonl");
        File.Copy(source, destination);
        return destination;
    }

    /// <summary>
    /// 功能：安装公共 branch-compaction-v0.1 tree fixture，供 selected projection 与 mutation 测试。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="sessionsRoot">测试临时 sessions 根目录。</param>
    /// <returns>目标 journal 文件路径。</returns>
    internal static string InstallSharedBranchCompactionSession(string sessionsRoot)
    {
        var source = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "branch-compaction-v0.1",
            "journal.jsonl");
        var sessionDirectory = Directory.CreateDirectory(
            Path.Combine(sessionsRoot, "session-branch-compaction")).FullName;
        Directory.CreateDirectory(Path.Combine(sessionDirectory, "artifacts"));
        var destination = Path.Combine(sessionDirectory, "journal.jsonl");
        File.Copy(source, destination);
        return destination;
    }

    /// <summary>
    /// 功能：取得复制到测试输出的公共 continuation faux 场景路径。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>存在的 continuation.scenario.json 绝对路径。</returns>
    internal static string GetSharedContinuationScenarioPath()
    {
        return GetSharedFixturePath("continuation.scenario.json");
    }

    /// <summary>
    /// 功能：解析公共 portable-v0.1 fixture 在测试输出目录中的稳定路径。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="name">已由项目文件显式链接的 fixture 文件名。</param>
    /// <returns>fixture 绝对路径。</returns>
    private static string GetSharedFixturePath(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-v0.1", name);
    }
}
