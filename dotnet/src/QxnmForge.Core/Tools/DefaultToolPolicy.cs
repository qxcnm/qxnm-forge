namespace QxnmForge.Tools;

/// <summary>
/// 功能：定义默认策略识别的工具操作类型。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public enum ToolAction
{
    FileRead,
    FileList,
    SearchText,
    FileWrite,
    FileEdit,
    ProcessExec,
    ShellExec,
    TerminalOpen,
    NetworkAccess,
    CredentialAccess,
    ComputerObserve,
    ComputerInteract,
}

/// <summary>
/// 功能：区分存在审批者的交互模式与无头模式。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public enum OperationMode
{
    Interactive,
    Headless,
}

/// <summary>
/// 功能：表示默认策略的 allow、ask 或 deny 决策。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public enum PolicyDecision
{
    Allow,
    Ask,
    Deny,
}

/// <summary>
/// 功能：实现 SPEC/security.md 的保守默认权限表，不把路径检查宣称为 sandbox。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public static class DefaultToolPolicy
{
    /// <summary>
    /// 功能：根据模式、操作、路径范围和显式策略是否存在作出无副作用的基线决策。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="mode">interactive 或 headless。</param>
    /// <param name="action">规范化工具操作。</param>
    /// <param name="insideWorkspace">所有受影响资源是否都位于已验证工作区内。</param>
    /// <param name="explicitPolicyPresent">是否存在独立用户/管理员策略；本方法不会自行授予该策略权限。</param>
    /// <returns>allow、ask 或 deny。</returns>
    /// <remarks>不变量：computer 在 interactive 始终 ask、headless 始终 deny；其他工作区外操作始终 deny。</remarks>
    public static PolicyDecision Evaluate(
        OperationMode mode,
        ToolAction action,
        bool insideWorkspace,
        bool explicitPolicyPresent = false)
    {
        if (action is ToolAction.ComputerObserve or ToolAction.ComputerInteract)
        {
            return mode == OperationMode.Interactive
                ? PolicyDecision.Ask
                : PolicyDecision.Deny;
        }

        if (!insideWorkspace)
        {
            return PolicyDecision.Deny;
        }

        if (action is ToolAction.FileRead or ToolAction.FileList or ToolAction.SearchText)
        {
            return PolicyDecision.Allow;
        }

        if (mode == OperationMode.Headless && !explicitPolicyPresent)
        {
            return PolicyDecision.Deny;
        }

        return PolicyDecision.Ask;
    }
}
