using QxnmForge.Executor;
using QxnmForge.Tools;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证默认危险策略和工作区路径边界基础。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class SecurityFoundationTests
{
    /// <summary>
    /// 功能：覆盖共享默认策略 fixture 的核心 allow/ask/deny 组合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="mode">interactive/headless。</param>
    /// <param name="action">工具操作。</param>
    /// <param name="inside">是否在工作区。</param>
    /// <param name="expected">规范期望决策。</param>
    [Theory]
    [InlineData(OperationMode.Interactive, ToolAction.FileRead, true, PolicyDecision.Allow)]
    [InlineData(OperationMode.Interactive, ToolAction.FileWrite, true, PolicyDecision.Ask)]
    [InlineData(OperationMode.Headless, ToolAction.ProcessExec, true, PolicyDecision.Deny)]
    [InlineData(OperationMode.Headless, ToolAction.ShellExec, true, PolicyDecision.Deny)]
    [InlineData(OperationMode.Interactive, ToolAction.FileRead, false, PolicyDecision.Deny)]
    public void EvaluateMatchesDefaultPolicyFixture(
        OperationMode mode,
        ToolAction action,
        bool inside,
        PolicyDecision expected)
    {
        Assert.Equal(expected, DefaultToolPolicy.Evaluate(mode, action, inside));
    }

    /// <summary>
    /// 功能：确认正常相对路径允许，而 parent traversal 与 sibling-prefix 路径拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void ResolveExistingRejectsLexicalWorkspaceEscape()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "project")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(temporary.Path, "project-evil")).FullName;
        var insideFile = Path.Combine(workspace, "inside.txt");
        var outsideFile = Path.Combine(sibling, "secret.txt");
        File.WriteAllText(insideFile, "inside");
        File.WriteAllText(outsideFile, "outside");
        using var boundary = new WorkspaceBoundary(workspace);

        Assert.Equal(insideFile, boundary.ResolveExisting("inside.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => boundary.ResolveExisting("../project-evil/secret.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => boundary.ResolveExisting(outsideFile));
    }

    /// <summary>
    /// 功能：在支持符号链接的平台确认 link-out 逃逸被拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void ResolveExistingRejectsSymlinkEscape()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "project")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(temporary.Path, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside");
        Directory.CreateSymbolicLink(Path.Combine(workspace, "link-out"), outside);
        using var boundary = new WorkspaceBoundary(workspace);

        Assert.Throws<UnauthorizedAccessException>(() => boundary.ResolveExisting("link-out/secret.txt"));
    }
}
