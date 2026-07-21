import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import App from "@/App";
import { InterfaceProviders } from "@/components/interface-providers";
import { INTERFACE_THEME_STORAGE_KEY } from "@/components/interface-theme";
import { TooltipProvider } from "@/components/ui/tooltip";
import i18n, { INTERFACE_LANGUAGE_STORAGE_KEY } from "@/i18n";
import { resetMockApplicationServiceState } from "@/lib/mock-application-service";
import { useWorkspaceUiStore } from "@/store/workspace-ui-store";

/**
 * 使用真实界面 Provider 与隔离 QueryClient 渲染完整工作台。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function renderThemedApp() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <InterfaceProviders>
        <TooltipProvider>
          <App />
        </TooltipProvider>
      </InterfaceProviders>
    </QueryClientProvider>,
  );
}

describe("interface theme and language preferences", () => {
  beforeEach(async () => {
    window.localStorage.clear();
    document.documentElement.className = "";
    resetMockApplicationServiceState();
    await i18n.changeLanguage("zh-CN");
    useWorkspaceUiStore.setState({
      backend: "rust",
      activeSessionId: "desktop-shell",
      activeView: "conversation",
      activeAgentProfileId: null,
      mobileSidebarOpen: false,
      reviewOpen: false,
      composerSubmitMode: "enter",
      sidebarWidth: "standard",
      reduceMotion: false,
    });
  });

  /**
   * 验证主题与语言设置即时应用，并由成熟 Provider 保存到设备存储。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("applies and persists dark theme and English", async () => {
    renderThemedApp();

    fireEvent.click(screen.getByRole("button", { name: "设置" }));
    await screen.findByRole("heading", { name: "设置" });
    const appearanceTab = screen.getByRole("tab", { name: "外观" });
    fireEvent.mouseDown(appearanceTab, { button: 0, ctrlKey: false });
    fireEvent.click(appearanceTab);

    fireEvent.click(screen.getByText("深色"));
    await waitFor(() => expect(document.documentElement).toHaveClass("dark"));
    expect(window.localStorage.getItem(INTERFACE_THEME_STORAGE_KEY)).toBe("dark");

    fireEvent.click(screen.getByLabelText("界面语言"));
    fireEvent.click(await screen.findByRole("option", { name: "English" }));

    expect(await screen.findByRole("heading", { name: "Settings" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "New task" })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Providers" })).toBeInTheDocument();
    expect(document.documentElement.lang).toBe("en-US");
    expect(window.localStorage.getItem(INTERFACE_LANGUAGE_STORAGE_KEY)).toBe("en-US");

    fireEvent.click(screen.getByRole("button", { name: "Agents" }));
    expect(await screen.findByRole("heading", { name: "Agents" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "New" })).toBeInTheDocument();
    expect(await screen.findByLabelText("Name")).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Profile" })).toBeInTheDocument();
    const toolsTab = screen.getByRole("tab", { name: "Tools" });
    fireEvent.mouseDown(toolsTab, { button: 0, ctrlKey: false });
    fireEvent.click(toolsTab);
    expect(await screen.findByText("Requested tools")).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: /Read files/ })).toBeInTheDocument();
  });

  /**
   * 验证损坏的持久化主题在 next-themes 挂载前回退，且不会卸载应用根节点。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("replaces an invalid stored theme with the safe default", async () => {
    window.localStorage.setItem(INTERFACE_THEME_STORAGE_KEY, "bad value");

    render(
      <InterfaceProviders>
        <p>theme harness</p>
      </InterfaceProviders>,
    );

    expect(screen.getByText("theme harness")).toBeInTheDocument();
    await waitFor(() =>
      expect(window.localStorage.getItem(INTERFACE_THEME_STORAGE_KEY)).toBe(
        "system",
      ),
    );
    expect(document.documentElement).not.toHaveClass("bad value");
  });

  /**
   * 验证 system 偏好持续跟随操作系统配色变化，而不是被 forcedTheme 固定。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("keeps the system theme synchronized with operating system changes", async () => {
    const originalMatchMedia = window.matchMedia;
    let prefersDark = false;
    const mediaListeners = new Set<(event: MediaQueryListEvent) => void>();
    const mediaQueryList = {
      get matches() {
        return prefersDark;
      },
      media: "(prefers-color-scheme: dark)",
      onchange: null,
      addListener: vi.fn((listener: (event: MediaQueryListEvent) => void) => {
        mediaListeners.add(listener);
      }),
      removeListener: vi.fn((listener: (event: MediaQueryListEvent) => void) => {
        mediaListeners.delete(listener);
      }),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    } as unknown as MediaQueryList;

    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      value: vi.fn(() => mediaQueryList),
    });
    window.localStorage.setItem(INTERFACE_THEME_STORAGE_KEY, "system");

    try {
      render(
        <InterfaceProviders>
          <p>system theme harness</p>
        </InterfaceProviders>,
      );
      expect(screen.getByText("system theme harness")).toBeInTheDocument();
      await waitFor(() => expect(document.documentElement).not.toHaveClass("dark"));

      act(() => {
        prefersDark = true;
        const event = { matches: true } as MediaQueryListEvent;
        mediaListeners.forEach((listener) => listener(event));
      });
      await waitFor(() => expect(document.documentElement).toHaveClass("dark"));

      act(() => {
        prefersDark = false;
        const event = { matches: false } as MediaQueryListEvent;
        mediaListeners.forEach((listener) => listener(event));
      });
      await waitFor(() => expect(document.documentElement).toHaveClass("light"));
    } finally {
      Object.defineProperty(window, "matchMedia", {
        configurable: true,
        value: originalMatchMedia,
      });
    }
  });

  /**
   * 验证其他窗口写入的非法主题不会到达文档 class，并立即恢复 system。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("sanitizes invalid theme storage events", async () => {
    render(
      <InterfaceProviders>
        <p>theme harness</p>
      </InterfaceProviders>,
    );
    expect(screen.getByText("theme harness")).toBeInTheDocument();

    window.localStorage.setItem(INTERFACE_THEME_STORAGE_KEY, "dark");
    fireEvent(
      window,
      new StorageEvent("storage", {
        key: INTERFACE_THEME_STORAGE_KEY,
        newValue: "dark",
        storageArea: window.localStorage,
      }),
    );
    await waitFor(() => expect(document.documentElement).toHaveClass("dark"));

    window.localStorage.setItem(INTERFACE_THEME_STORAGE_KEY, "bad value");
    fireEvent(
      window,
      new StorageEvent("storage", {
        key: INTERFACE_THEME_STORAGE_KEY,
        newValue: "bad value",
        storageArea: window.localStorage,
      }),
    );

    await waitFor(() => {
      expect(window.localStorage.getItem(INTERFACE_THEME_STORAGE_KEY)).toBe(
        "system",
      );
      expect(document.documentElement).not.toHaveClass("dark");
    });
    expect(screen.getByText("theme harness")).toBeInTheDocument();
  });
});
