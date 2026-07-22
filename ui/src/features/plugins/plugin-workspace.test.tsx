import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { PluginWorkspace } from "@/features/plugins/plugin-workspace";
import i18n from "@/i18n";
import {
  PLUGIN_MARKETPLACE_STORAGE_KEY,
  resetPluginMarketplacePreferences,
  usePluginMarketplaceStore,
} from "@/store/plugin-marketplace-store";

const WORKSPACE_TOOL_IDS = [
  "file.read",
  "search.text",
  "file.write",
  "file.edit",
  "process.exec",
  "shell.exec",
] as const;

const COMPUTER_TOOL_IDS = [
  "computer.observe",
  "computer.screenshot",
  "computer.interact",
] as const;

const APPROVAL_METHOD_IDS = ["approval/respond"] as const;
const APPROVAL_EVENT_TYPES = ["approval.requested", "approval.resolved"] as const;

/**
 * 使用固定的 faux capability 广告渲染插件市场。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function renderPluginWorkspace(
  supportedToolIds: readonly string[] = WORKSPACE_TOOL_IDS,
  supportedMethodIds: readonly string[] = APPROVAL_METHOD_IDS,
  supportedEventTypes: readonly string[] = APPROVAL_EVENT_TYPES,
) {
  return render(
    <PluginWorkspace
      supportedToolIds={supportedToolIds}
      supportedMethodIds={supportedMethodIds}
      supportedEventTypes={supportedEventTypes}
      loading={false}
      onOpenAgentTools={vi.fn()}
      onOpenMobileSidebar={vi.fn()}
    />,
  );
}

describe("PluginWorkspace", () => {
  beforeEach(async () => {
    window.localStorage.clear();
    resetPluginMarketplacePreferences();
    await i18n.changeLanguage("zh-CN");
  });

  /**
   * 验证市场可以搜索目录并打开精确 capability 详情。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("searches catalog entries and opens capability details", () => {
    renderPluginWorkspace();

    fireEvent.change(screen.getByLabelText("搜索插件"), {
      target: { value: "GitHub" },
    });
    expect(
      screen.getByRole("heading", { name: "GitHub Workflow" }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("heading", { name: "Product Design" }),
    ).not.toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("搜索插件"), {
      target: { value: "" },
    });
    fireEvent.click(screen.getByRole("button", { name: "查看 Product Design 详情" }));
    expect(
      screen.getByRole("heading", { name: "Product Design" }),
    ).toBeInTheDocument();
    expect(screen.getByText("file.read")).toBeInTheDocument();
    expect(
      screen.getByText(/安装与启用是本设备上的非敏感界面偏好/),
    ).toBeInTheDocument();
  });

  /**
   * 验证市场视图使用有标签的分段按钮，而不是缺失 tabpanel 的页签语义。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("exposes browse and installed views as an accessible toggle group", () => {
    renderPluginWorkspace();

    expect(
      screen.getByRole("radiogroup", { name: "选择插件市场视图" }),
    ).toBeInTheDocument();
    const browseButton = screen.getByRole("radio", { name: "浏览市场" });
    expect(browseButton).toHaveAttribute("aria-checked", "true");
    expect(screen.queryByRole("tab")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("radio", { name: "已安装" }));
    expect(screen.getByText("没有匹配的插件")).toBeInTheDocument();
  });

  /**
   * 验证安装、启用、停用和卸载只更新设备本地偏好。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("persists local install and enable preferences", () => {
    renderPluginWorkspace();

    fireEvent.click(screen.getByRole("button", { name: "安装 Product Design" }));
    const enabledSwitch = screen.getByRole("switch", {
      name: "启用 Product Design 插件",
    });
    expect(enabledSwitch).not.toBeChecked();
    fireEvent.click(enabledSwitch);
    expect(enabledSwitch).toBeChecked();
    expect(usePluginMarketplaceStore.getState().plugins["product-design"]).toEqual({
      installed: true,
      enabled: true,
    });
    expect(
      JSON.parse(
        window.localStorage.getItem(PLUGIN_MARKETPLACE_STORAGE_KEY) ?? "{}",
      ),
    ).toEqual({
      plugins: {
        "product-design": { installed: true, enabled: true },
      },
    });

    fireEvent.click(enabledSwitch);
    expect(enabledSwitch).not.toBeChecked();
    fireEvent.click(
      screen.getByRole("button", { name: "卸载 Product Design" }),
    );
    expect(
      screen.getByRole("button", { name: "安装 Product Design" }),
    ).toBeInTheDocument();
  });

  /**
   * 验证未广告的 Computer Use 可以记录安装偏好，但不可启用或扩大工具集合。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("disables Computer Use when computer capabilities are absent", () => {
    renderPluginWorkspace();

    expect(screen.getByText("后端未安装")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "安装 Computer Use" }));
    const computerSwitch = screen.getByRole("switch", {
      name: "启用 Computer Use 插件",
    });
    expect(computerSwitch).toBeDisabled();
    fireEvent.click(computerSwitch);
    expect(usePluginMarketplaceStore.getState().plugins["computer-use"]).toEqual({
      installed: true,
      enabled: false,
    });

    fireEvent.click(screen.getByRole("button", { name: "查看 Computer Use 详情" }));
    expect(
      screen.getByText(/当前连接未广告任何/),
    ).toBeInTheDocument();
    expect(screen.getAllByText("computer.*").length).toBeGreaterThan(0);
  });

  /**
   * 验证部分 computer.* 只展示真实交集，且不足以启用 Computer Use。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("keeps Computer Use disabled when only part of its tools are advertised", () => {
    renderPluginWorkspace([...WORKSPACE_TOOL_IDS, "computer.screenshot"]);

    expect(screen.getByText("1/3 项可用")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "安装 Computer Use" }));
    expect(
      screen.getByRole("switch", { name: "启用 Computer Use 插件" }),
    ).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "查看 Computer Use 详情" }));
    expect(screen.getByText(/当前连接只广告了部分/)).toBeInTheDocument();
    expect(screen.queryByText(/当前连接未广告任何/)).not.toBeInTheDocument();
  });

  /**
   * 验证 Computer 工具齐全但缺少交互审批能力时仍不可启用。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("keeps Computer Use disabled without approval capabilities", () => {
    renderPluginWorkspace(
      [...WORKSPACE_TOOL_IDS, ...COMPUTER_TOOL_IDS],
      [],
      [],
    );

    expect(screen.getByText("审批能力未接入")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "安装 Computer Use" }));
    expect(
      screen.getByRole("switch", { name: "启用 Computer Use 插件" }),
    ).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "查看 Computer Use 详情" }));
    expect(screen.getByText("approval/respond")).toBeInTheDocument();
    expect(screen.getByText("approval.requested")).toBeInTheDocument();
    expect(screen.getByText(/当前连接缺少交互审批方法/)).toBeInTheDocument();
  });

  /**
   * 验证 Computer 全部广告齐全时仍显示真实交集，并因视觉闭环缺口保持实验性未就绪。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("keeps fully advertised Computer Use experimental and disabled", () => {
    renderPluginWorkspace([
      ...WORKSPACE_TOOL_IDS,
      ...COMPUTER_TOOL_IDS,
    ]);

    expect(screen.getByText("实验性未就绪")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "安装 Computer Use" }));
    const computerSwitch = screen.getByRole("switch", {
      name: "启用 Computer Use 插件",
    });
    expect(computerSwitch).toBeDisabled();
    expect(computerSwitch).not.toBeChecked();
    expect(usePluginMarketplaceStore.getState().plugins["computer-use"]).toEqual({
      installed: true,
      enabled: false,
    });

    fireEvent.click(screen.getByRole("button", { name: "查看 Computer Use 详情" }));
    expect(screen.getByText("computer.observe")).toBeInTheDocument();
    expect(screen.getByText("computer.screenshot")).toBeInTheDocument();
    expect(screen.getByText("computer.interact")).toBeInTheDocument();
    expect(screen.getByText("approval/respond")).toBeInTheDocument();
    expect(screen.getByText("approval.requested")).toBeInTheDocument();
    expect(screen.getByText("approval.resolved")).toBeInTheDocument();
    expect(
      screen.getByText(/尚无经过边界验证的截图 artifact 读取\/渲染/),
    ).toBeInTheDocument();
    expect(
      screen.getAllByText(/tools \+ image_input Provider 视觉闭环/),
    ).toHaveLength(2);
  });

  /**
   * 验证历史已启用偏好只能被用户收紧，实验性冻结状态下不可重新启用。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("allows disabling a legacy enabled Computer preference", () => {
    usePluginMarketplaceStore.getState().installPlugin("computer-use");
    usePluginMarketplaceStore.getState().setPluginEnabled("computer-use", true);
    renderPluginWorkspace([...WORKSPACE_TOOL_IDS, ...COMPUTER_TOOL_IDS]);

    const computerSwitch = screen.getByRole("switch", {
      name: "启用 Computer Use 插件",
    });
    expect(computerSwitch).toBeChecked();
    expect(computerSwitch).not.toBeDisabled();
    expect(screen.getByText("已启用，当前后端不生效")).toBeInTheDocument();
    fireEvent.click(computerSwitch);
    expect(computerSwitch).not.toBeChecked();
    expect(computerSwitch).toBeDisabled();
    expect(usePluginMarketplaceStore.getState().plugins["computer-use"]).toEqual({
      installed: true,
      enabled: false,
    });
  });

  /**
   * 验证 store 拒绝固定目录外的任意插件 ID。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects plugin ids outside the fixed catalog", () => {
    usePluginMarketplaceStore.getState().installPlugin("unknown-plugin");
    usePluginMarketplaceStore
      .getState()
      .installPlugin("x".repeat(128));

    expect(usePluginMarketplaceStore.getState().plugins).toEqual({});
    expect(window.localStorage.getItem(PLUGIN_MARKETPLACE_STORAGE_KEY)).toBeNull();
  });

  /**
   * 验证 English 模式覆盖市场目录、分类、状态、操作与详情文案。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("renders the marketplace catalog and details in English", async () => {
    await i18n.changeLanguage("en-US");
    renderPluginWorkspace();

    expect(
      screen.getByRole("heading", { name: "Plugin marketplace" }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/Turn product requirements into implementable/),
    ).toBeInTheDocument();
    expect(screen.getByText("Design")).toBeInTheDocument();
    expect(screen.getAllByText("Backend ready").length).toBeGreaterThan(0);
    fireEvent.click(
      screen.getByRole("button", { name: "View Product Design details" }),
    );
    expect(screen.getByText("Backend capabilities")).toBeInTheDocument();
    expect(screen.getByText("Read files")).toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/[\u4e00-\u9fff]/u);
  });

  /**
   * 验证 English 模式明确解释广告齐全的 Computer Use 仍缺少 artifact 与 Provider 视觉闭环。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("explains the fully advertised Computer Use gap in English", async () => {
    await i18n.changeLanguage("en-US");
    renderPluginWorkspace([...WORKSPACE_TOOL_IDS, ...COMPUTER_TOOL_IDS]);

    expect(screen.getByText("Experimental—not ready")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Install Computer Use" }));
    expect(
      screen.getByRole("switch", { name: "Enable Computer Use plugin" }),
    ).toBeDisabled();
    fireEvent.click(
      screen.getByRole("button", { name: "View Computer Use details" }),
    );
    expect(
      screen.getByText(/no boundary-validated screenshot artifact reader or renderer/),
    ).toBeInTheDocument();
    expect(screen.getByText(/both tools and image_input/)).toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/[\u4e00-\u9fff]/u);
  });
});
