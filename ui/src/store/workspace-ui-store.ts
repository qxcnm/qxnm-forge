import { create } from "zustand";

import type { BackendKind } from "@/types/application-service";

/** 工作台中央区域可切换的一级视图。 */
export type WorkspaceView = "conversation" | "agents" | "plugins" | "settings" | "archive";

/** 输入器支持的发送按键模式。 */
export type ComposerSubmitMode = "enter" | "mod-enter";

/** 桌面项目导航支持的稳定宽度。 */
export type SidebarWidth = "compact" | "standard";

interface StoredUiPreferences {
  readonly backend: BackendKind;
  readonly composerSubmitMode: ComposerSubmitMode;
  readonly sidebarWidth: SidebarWidth;
  readonly reduceMotion: boolean;
}

const UI_PREFERENCES_STORAGE_KEY = "agent-client.ui-preferences.v1";

const DEFAULT_UI_PREFERENCES: StoredUiPreferences = {
  backend: "rust",
  composerSubmitMode: "enter",
  sidebarWidth: "standard",
  reduceMotion: false,
};

/**
 * 从同源浏览器存储读取非敏感界面偏好，并对未知或损坏字段回退默认值。
 *
 * 输出：仅包含后端选择、输入快捷键和视觉布局偏好。
 * 不变量：不得读取或迁移 credential、会话、审批、工具授权或 Agent Profile。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function readStoredUiPreferences(): StoredUiPreferences {
  if (typeof window === "undefined") {
    return DEFAULT_UI_PREFERENCES;
  }
  try {
    const source = window.localStorage.getItem(UI_PREFERENCES_STORAGE_KEY);
    if (!source) {
      return DEFAULT_UI_PREFERENCES;
    }
    const value: unknown = JSON.parse(source);
    if (!value || typeof value !== "object" || Array.isArray(value)) {
      return DEFAULT_UI_PREFERENCES;
    }
    const candidate = value as Record<string, unknown>;
    return {
      backend:
        candidate.backend === "rust" || candidate.backend === "dotnet"
          ? candidate.backend
          : DEFAULT_UI_PREFERENCES.backend,
      composerSubmitMode:
        candidate.composerSubmitMode === "enter" ||
        candidate.composerSubmitMode === "mod-enter"
          ? candidate.composerSubmitMode
          : DEFAULT_UI_PREFERENCES.composerSubmitMode,
      sidebarWidth:
        candidate.sidebarWidth === "compact" || candidate.sidebarWidth === "standard"
          ? candidate.sidebarWidth
          : DEFAULT_UI_PREFERENCES.sidebarWidth,
      reduceMotion:
        typeof candidate.reduceMotion === "boolean"
          ? candidate.reduceMotion
          : DEFAULT_UI_PREFERENCES.reduceMotion,
    };
  } catch {
    return DEFAULT_UI_PREFERENCES;
  }
}

/**
 * 原子替换同源浏览器中的非敏感界面偏好快照。
 *
 * 输入：闭合且已经过类型约束的偏好对象。
 * 不变量：写入失败不影响当前内存界面状态，且绝不包含 secret 或业务数据。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function writeStoredUiPreferences(preferences: StoredUiPreferences): void {
  if (typeof window === "undefined") {
    return;
  }
  try {
    window.localStorage.setItem(UI_PREFERENCES_STORAGE_KEY, JSON.stringify(preferences));
  } catch {
    // 浏览器禁用存储时，当前进程中的设置仍然有效。
  }
}

const storedUiPreferences = readStoredUiPreferences();

interface WorkspaceUiState {
  backend: BackendKind;
  activeSessionId: string;
  activeView: WorkspaceView;
  activeAgentProfileId: string | null;
  mobileSidebarOpen: boolean;
  reviewOpen: boolean;
  composerSubmitMode: ComposerSubmitMode;
  sidebarWidth: SidebarWidth;
  reduceMotion: boolean;

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
   * 切换会话、智能体、插件、设置或归档管理视图。
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

  /**
   * 设置 Enter 或组合键发送，并保存非敏感界面偏好。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  setComposerSubmitMode: (mode: ComposerSubmitMode) => void;

  /**
   * 设置桌面项目导航宽度，并保存非敏感界面偏好。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  setSidebarWidth: (width: SidebarWidth) => void;

  /**
   * 切换减少动态效果偏好，并保存非敏感界面偏好。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  setReduceMotion: (reduceMotion: boolean) => void;
}

/**
 * 保存工作台视图状态，并仅持久化闭合的非敏感界面偏好。
 *
 * 不变量：不得持久化 transcript、Provider 配置或凭据、审批、工具授权和 Agent Profile。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export const useWorkspaceUiStore = create<WorkspaceUiState>((set) => ({
  backend: storedUiPreferences.backend,
  activeSessionId: "desktop-shell",
  activeView: "conversation",
  activeAgentProfileId: null,
  mobileSidebarOpen: false,
  reviewOpen: false,
  composerSubmitMode: storedUiPreferences.composerSubmitMode,
  sidebarWidth: storedUiPreferences.sidebarWidth,
  reduceMotion: storedUiPreferences.reduceMotion,
  setBackend: (backend) =>
    set((state) => {
      writeStoredUiPreferences({
        backend,
        composerSubmitMode: state.composerSubmitMode,
        sidebarWidth: state.sidebarWidth,
        reduceMotion: state.reduceMotion,
      });
      return { backend };
    }),
  setActiveSessionId: (activeSessionId) => set({ activeSessionId }),
  setActiveView: (activeView) => set({ activeView }),
  setActiveAgentProfileId: (activeAgentProfileId) => set({ activeAgentProfileId }),
  setMobileSidebarOpen: (mobileSidebarOpen) => set({ mobileSidebarOpen }),
  setReviewOpen: (reviewOpen) => set({ reviewOpen }),
  setComposerSubmitMode: (composerSubmitMode) =>
    set((state) => {
      writeStoredUiPreferences({
        backend: state.backend,
        composerSubmitMode,
        sidebarWidth: state.sidebarWidth,
        reduceMotion: state.reduceMotion,
      });
      return { composerSubmitMode };
    }),
  setSidebarWidth: (sidebarWidth) =>
    set((state) => {
      writeStoredUiPreferences({
        backend: state.backend,
        composerSubmitMode: state.composerSubmitMode,
        sidebarWidth,
        reduceMotion: state.reduceMotion,
      });
      return { sidebarWidth };
    }),
  setReduceMotion: (reduceMotion) =>
    set((state) => {
      writeStoredUiPreferences({
        backend: state.backend,
        composerSubmitMode: state.composerSubmitMode,
        sidebarWidth: state.sidebarWidth,
        reduceMotion,
      });
      return { reduceMotion };
    }),
}));
