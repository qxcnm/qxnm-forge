import { create } from "zustand";

import type { BackendKind } from "@/types/application-service";

/** 工作台中央区域可切换的一级视图。 */
export type WorkspaceView = "conversation" | "agents";

interface WorkspaceUiState {
  backend: BackendKind;
  activeSessionId: string;
  activeView: WorkspaceView;
  activeAgentProfileId: string | null;
  mobileSidebarOpen: boolean;
  reviewOpen: boolean;

  /**
   * 切换下一次连接使用的独立后端实现。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  setBackend: (backend: BackendKind) => void;

  /**
   * 更新当前仅用于视图定位的 Session 引用。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  setActiveSessionId: (sessionId: string) => void;

  /**
   * 切换会话与智能体管理视图。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  setActiveView: (view: WorkspaceView) => void;

  /**
   * 选择新会话输入器使用的智能体预览投影。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  setActiveAgentProfileId: (profileId: string | null) => void;

  /**
   * 控制移动端项目导航抽屉。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  setMobileSidebarOpen: (open: boolean) => void;

  /**
   * 控制变更审阅面板。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  setReviewOpen: (open: boolean) => void;
}

/**
 * 保存可丢弃的工作台视图状态，不持久化 transcript 或审批状态。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export const useWorkspaceUiStore = create<WorkspaceUiState>((set) => ({
  backend: "rust",
  activeSessionId: "desktop-shell",
  activeView: "conversation",
  activeAgentProfileId: null,
  mobileSidebarOpen: false,
  reviewOpen: false,
  setBackend: (backend) => set({ backend }),
  setActiveSessionId: (activeSessionId) => set({ activeSessionId }),
  setActiveView: (activeView) => set({ activeView }),
  setActiveAgentProfileId: (activeAgentProfileId) => set({ activeAgentProfileId }),
  setMobileSidebarOpen: (mobileSidebarOpen) => set({ mobileSidebarOpen }),
  setReviewOpen: (reviewOpen) => set({ reviewOpen }),
}));
