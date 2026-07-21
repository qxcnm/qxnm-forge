import type { LucideIcon } from "lucide-react";
import { AppWindow, Boxes, Code2, FileCode2, ShieldCheck } from "lucide-react";

export interface SessionFixture {
  readonly id: string;
  readonly title: string;
  readonly project: string;
  readonly age: string;
  readonly status?: "active" | "approval";
}

export interface ActivityFixture {
  readonly id: string;
  readonly label: string;
  readonly detail: string;
  readonly icon: LucideIcon;
  readonly state: "completed" | "running" | "pending";
}

export const SESSION_FIXTURES: readonly SessionFixture[] = [
  {
    id: "desktop-shell",
    title: "实现跨平台桌面端",
    project: "AI-Code",
    age: "刚刚",
    status: "active",
  },
  {
    id: "backend-contract",
    title: "统一后端能力协议",
    project: "AI-Code",
    age: "12 分钟",
  },
  {
    id: "approval-flow",
    title: "检查工具审批流程",
    project: "QXNM Forge",
    age: "1 小时",
    status: "approval",
  },
  {
    id: "mobile-layout",
    title: "适配移动端工作区",
    project: "QXNM Forge",
    age: "昨天",
  },
];

export const ACTIVITY_FIXTURES: readonly ActivityFixture[] = [
  {
    id: "inspect",
    label: "读取项目边界",
    detail: "已确认 React application-service 约束",
    icon: ShieldCheck,
    state: "completed",
  },
  {
    id: "frontend",
    label: "构建工作台",
    detail: "React + shadcn/ui + TanStack Query",
    icon: Code2,
    state: "completed",
  },
  {
    id: "desktop",
    label: "配置桌面壳",
    detail: "Tauri 2 · Windows / macOS",
    icon: AppWindow,
    state: "running",
  },
  {
    id: "android",
    label: "移动端边界",
    detail: "Android 使用远程 application service",
    icon: Boxes,
    state: "pending",
  },
];

export const COMMAND_LOG_FIXTURES = [
  "读取 open/SPEC/ui.md 与 protocol schema",
  "检查 open/ui 与 pro/ui 的发行边界",
  "解析 initialize.capabilities.methods",
  "校验 Rust daemon 启动参数",
  "校验 .NET daemon --stdio 启动参数",
  "生成 React application-service 适配器",
] as const;

export const CHANGED_FILES = [
  { path: "ui/src/App.tsx", additions: 214, deletions: 0, icon: FileCode2 },
  { path: "ui/src-tauri/tauri.conf.json", additions: 39, deletions: 0, icon: FileCode2 },
] as const;
