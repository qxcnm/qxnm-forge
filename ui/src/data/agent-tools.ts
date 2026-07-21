import type { AgentToolPresentation } from "@/types/agent-profile";

/** 固定注册表工具的展示副本，不含 schema、executor 或授权结论。 */
export const AGENT_TOOL_PRESENTATIONS: readonly AgentToolPresentation[] = [
  {
    toolId: "file.read",
    displayName: "读取文件",
    description: "读取工作区内的 UTF-8 文本",
    permissionClass: "workspace_read",
    dangerous: false,
  },
  {
    toolId: "search.text",
    displayName: "搜索文本",
    description: "在工作区文本中执行正则搜索",
    permissionClass: "workspace_read",
    dangerous: false,
  },
  {
    toolId: "file.write",
    displayName: "写入文件",
    description: "在工作区内原子写入文本",
    permissionClass: "workspace_write",
    dangerous: true,
  },
  {
    toolId: "file.edit",
    displayName: "编辑文件",
    description: "唯一匹配并替换工作区文本",
    permissionClass: "workspace_write",
    dangerous: true,
  },
  {
    toolId: "process.exec",
    displayName: "运行进程",
    description: "通过 executable 与 argv 运行进程",
    permissionClass: "process",
    dangerous: true,
  },
  {
    toolId: "shell.exec",
    displayName: "运行 Shell",
    description: "通过显式非交互 Shell 执行脚本",
    permissionClass: "shell",
    dangerous: true,
  },
  {
    toolId: "computer.observe",
    displayName: "查看屏幕",
    description: "读取当前桌面的可见状态",
    permissionClass: "computer_observe",
    dangerous: false,
  },
  {
    toolId: "computer.screenshot",
    displayName: "截取屏幕",
    description: "生成当前桌面的屏幕快照",
    permissionClass: "computer_observe",
    dangerous: false,
  },
  {
    toolId: "computer.interact",
    displayName: "操作电脑",
    description: "在审批范围内控制鼠标与键盘",
    permissionClass: "computer_interact",
    dangerous: true,
  },
] as const;
