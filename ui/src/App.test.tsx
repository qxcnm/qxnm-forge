import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";

import App from "@/App";
import { TooltipProvider } from "@/components/ui/tooltip";
import { useWorkspaceUiStore } from "@/store/workspace-ui-store";

/**
 * 使用隔离的 QueryClient 渲染完整工作台。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function renderApp() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <TooltipProvider>
        <App />
      </TooltipProvider>
    </QueryClientProvider>,
  );
}

describe("QXNM Forge workspace", () => {
  beforeEach(() => {
    useWorkspaceUiStore.setState({
      backend: "rust",
      activeSessionId: "desktop-shell",
      activeView: "conversation",
      activeAgentProfileId: null,
      mobileSidebarOpen: false,
      reviewOpen: false,
    });
  });

  /**
   * 验证分段控件会重新初始化所选 .NET 服务画像。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("switches the backend capability profile", async () => {
    renderApp();

    fireEvent.click(screen.getByLabelText("使用 .NET 后端"));

    expect(await screen.findByText("qxnm-forge-dotnet")).toBeInTheDocument();
  });

  /**
   * 验证主输入流程可以完成 faux 运行接受回执。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("submits a faux run from the composer", async () => {
    renderApp();

    fireEvent.change(screen.getByLabelText("任务消息"), {
      target: { value: "检查桌面打包配置" },
    });
    await waitFor(() => {
      expect(screen.getByLabelText("发送任务")).toBeEnabled();
    });
    fireEvent.click(screen.getByLabelText("发送任务"));

    expect(await screen.findByText("检查桌面打包配置")).toBeInTheDocument();
    expect(
      await screen.findByText(/运行已由 Rust capability 画像接受/),
    ).toBeInTheDocument();
  });

  /**
   * 验证变更审阅抽屉可打开并返回主会话。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("opens and closes the review sheet", async () => {
    renderApp();

    fireEvent.click(screen.getByLabelText("打开变更审阅"));
    expect(await screen.findByRole("heading", { name: "变更审阅" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "返回会话" }));
    expect(screen.queryByRole("heading", { name: "变更审阅" })).not.toBeInTheDocument();
  });

  /**
   * 验证智能体编辑器可创建并保存一份内存预览投影。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("creates an in-memory agent profile", async () => {
    renderApp();

    fireEvent.click(screen.getByRole("button", { name: "智能体" }));
    expect(await screen.findByRole("heading", { name: "智能体" })).toBeInTheDocument();
    const codingAgentButton = await screen.findByRole("button", { name: /编码助手/ });

    const createButton = screen.getByRole("button", { name: "新建" });
    await waitFor(() => expect(createButton).toBeEnabled());
    fireEvent.click(createButton);
    fireEvent.change(screen.getByLabelText("名称"), {
      target: { value: "测试智能体" },
    });
    fireEvent.click(codingAgentButton);
    expect(
      await screen.findByRole("heading", { name: "放弃未保存修改？" }),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "继续编辑" }));
    expect(screen.getByLabelText("名称")).toHaveValue("测试智能体");
    fireEvent.click(screen.getByRole("button", { name: "保存" }));

    expect(await screen.findByText("已保存到本次预览")).toBeInTheDocument();
    expect(screen.getAllByText("测试智能体").length).toBeGreaterThanOrEqual(1);

    fireEvent.click(screen.getByRole("button", { name: "复制智能体" }));
    expect(await screen.findByText("测试智能体 副本")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "删除智能体" }));
    expect(
      await screen.findByRole("heading", { name: "删除“测试智能体 副本”？" }),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /^删除$/ }));
    expect(await screen.findByText("智能体已从本次预览删除")).toBeInTheDocument();

    act(() => useWorkspaceUiStore.getState().setBackend("dotnet"));
    expect(await screen.findByText(/面向 \.NET 画像的风险检查/)).toBeInTheDocument();
    act(() => useWorkspaceUiStore.getState().setBackend("rust"));
    await waitFor(() => {
      expect(screen.getAllByText("测试智能体").length).toBeGreaterThanOrEqual(1);
    });
  });

  /**
   * 验证 Composer 可选择启用的智能体并保持预览边界提示。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("selects an agent profile for a faux run", async () => {
    renderApp();

    await waitFor(() => expect(screen.getByLabelText("选择模型")).toBeEnabled());
    const agentSelect = screen.getByLabelText("选择智能体");
    fireEvent.click(agentSelect);
    fireEvent.click(await screen.findByRole("option", { name: "编码助手" }));
    fireEvent.change(screen.getByLabelText("任务消息"), {
      target: { value: "使用智能体检查改动" },
    });
    fireEvent.click(screen.getByLabelText("发送任务"));
    act(() => useWorkspaceUiStore.getState().setBackend("dotnet"));

    expect(
      await screen.findByText(/运行已由 Rust capability 画像接受.*当前选择“编码助手”内存预览/),
    ).toBeInTheDocument();
  });

  /**
   * 验证窄屏列表可以打开默认选中项，并在确认放弃后恢复服务投影。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("reopens the selected agent from the mobile list", async () => {
    renderApp();

    fireEvent.click(screen.getByRole("button", { name: "智能体" }));
    const nameInput = await screen.findByLabelText("名称");
    const editor = screen.getByRole("region", { name: "智能体编辑器" });
    const codingAgentButton = screen.getByRole("button", { name: /编码助手/ });

    expect(editor).toHaveClass("hidden");
    fireEvent.click(codingAgentButton);
    expect(editor).toHaveClass("flex");
    fireEvent.change(nameInput, { target: { value: "尚未保存的名称" } });
    fireEvent.click(screen.getByRole("button", { name: "返回智能体列表" }));
    fireEvent.click(
      await screen.findByRole("button", { name: "放弃修改" }),
    );

    expect(editor).toHaveClass("hidden");
    await waitFor(() => expect(codingAgentButton).toHaveFocus());
    fireEvent.click(codingAgentButton);
    expect(screen.getByLabelText("名称")).toHaveValue("编码助手");

    const createButton = screen.getByRole("button", { name: "新建" });
    fireEvent.click(createButton);
    fireEvent.click(screen.getByRole("button", { name: "返回智能体列表" }));
    fireEvent.click(await screen.findByRole("button", { name: "放弃修改" }));
    await waitFor(() => expect(createButton).toHaveFocus());

    fireEvent.click(codingAgentButton);
    fireEvent.click(screen.getByRole("button", { name: "删除智能体" }));
    fireEvent.click(await screen.findByRole("button", { name: /^删除$/ }));
    expect(await screen.findByText("智能体已从本次预览删除")).toBeInTheDocument();
  });

  /**
   * 验证侧栏离开智能体管理时同样保护未保存草稿。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("guards dirty agent drafts from sidebar navigation", async () => {
    renderApp();

    fireEvent.click(screen.getByRole("button", { name: "智能体" }));
    const nameInput = await screen.findByLabelText("名称");
    fireEvent.change(nameInput, { target: { value: "保留这个草稿" } });
    fireEvent.click(screen.getByRole("button", { name: "新建任务" }));

    expect(
      await screen.findByRole("heading", { name: "放弃未保存修改？" }),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "继续编辑" }));
    expect(screen.getByLabelText("名称")).toHaveValue("保留这个草稿");

    fireEvent.click(screen.getByRole("button", { name: "新建任务" }));
    fireEvent.click(await screen.findByRole("button", { name: "放弃修改" }));
    expect(await screen.findByLabelText("任务消息")).toBeInTheDocument();
  });

  /**
   * 验证提交消息和异步接受回执始终归属原 Session。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("routes run receipts to the submitted session", async () => {
    renderApp();

    await waitFor(() => expect(screen.getByLabelText("选择模型")).toBeEnabled());
    fireEvent.change(screen.getByLabelText("任务消息"), {
      target: { value: "只属于桌面 Session 的消息" },
    });
    fireEvent.click(screen.getByLabelText("发送任务"));
    fireEvent.click(
      screen.getAllByRole("button", { name: /统一后端能力协议/ })[0],
    );

    expect(screen.queryByText("只属于桌面 Session 的消息")).not.toBeInTheDocument();
    fireEvent.click(
      screen.getAllByRole("button", { name: /实现跨平台桌面端/ })[0],
    );
    expect(screen.getByText("只属于桌面 Session 的消息")).toBeInTheDocument();
    expect(
      await screen.findByText(/运行已由 Rust capability 画像接受/, {}, { timeout: 2_000 }),
    ).toBeInTheDocument();

    fireEvent.click(
      screen.getAllByRole("button", { name: /统一后端能力协议/ })[0],
    );
    expect(screen.queryByText("只属于桌面 Session 的消息")).not.toBeInTheDocument();
    expect(screen.queryByText(/运行已由 Rust capability 画像接受/)).not.toBeInTheDocument();
  });

  /**
   * 验证不同 Session 可以各自等待接受回执，且不会共享忙碌锁。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("tracks concurrent faux submissions per session", async () => {
    renderApp();

    await waitFor(() => expect(screen.getByLabelText("选择模型")).toBeEnabled());
    fireEvent.change(screen.getByLabelText("任务消息"), {
      target: { value: "Session A 并行消息" },
    });
    fireEvent.click(screen.getByLabelText("发送任务"));
    fireEvent.click(
      screen.getAllByRole("button", { name: /统一后端能力协议/ })[0],
    );
    fireEvent.change(screen.getByLabelText("任务消息"), {
      target: { value: "Session B 并行消息" },
    });

    expect(screen.getByLabelText("发送任务")).toBeEnabled();
    fireEvent.click(screen.getByLabelText("发送任务"));
    expect(await screen.findByText("Session B 并行消息")).toBeInTheDocument();
    expect(
      await screen.findByText(/运行已由 Rust capability 画像接受/, {}, { timeout: 2_000 }),
    ).toBeInTheDocument();

    fireEvent.click(
      screen.getAllByRole("button", { name: /实现跨平台桌面端/ })[0],
    );
    expect(screen.getByText("Session A 并行消息")).toBeInTheDocument();
    expect(screen.getByText(/运行已由 Rust capability 画像接受/)).toBeInTheDocument();
  });

  /**
   * 验证每次新建任务都会分配独立且不复用 fixture 的预览 Session 标识。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("creates unique preview sessions", () => {
    renderApp();

    const originalSessionId = useWorkspaceUiStore.getState().activeSessionId;
    fireEvent.click(screen.getByRole("button", { name: "新建任务" }));
    const firstPreviewSessionId = useWorkspaceUiStore.getState().activeSessionId;
    expect(
      screen.queryByRole("heading", { name: "构建 QXNM Forge 跨平台工作台" }),
    ).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "新建任务" }));
    const secondPreviewSessionId = useWorkspaceUiStore.getState().activeSessionId;

    expect(firstPreviewSessionId).toMatch(/^preview-/);
    expect(firstPreviewSessionId).not.toBe(originalSessionId);
    expect(secondPreviewSessionId).toMatch(/^preview-/);
    expect(secondPreviewSessionId).not.toBe(firstPreviewSessionId);
  });
});
