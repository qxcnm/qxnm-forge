import { describe, expect, it } from "vitest";

import {
  filterPluginCatalog,
  getPluginCapabilityStatus,
  PLUGIN_CATALOG,
} from "@/features/plugins/plugin-catalog";

describe("plugin catalog capability projection", () => {
  /**
   * 验证 Computer Use 不会在后端未广告 computer.* 时伪造可用能力。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("keeps Computer Use unavailable without advertised tools", () => {
    const plugin = PLUGIN_CATALOG.find(
      (entry) => entry.pluginId === "computer-use",
    );

    expect(plugin).toBeDefined();
    expect(getPluginCapabilityStatus(plugin!, ["file.read", "process.exec"])).toEqual({
      available: false,
      availableToolIds: [],
      missingToolIds: [
        "computer.observe",
        "computer.screenshot",
        "computer.interact",
      ],
      availableMethodIds: [],
      missingMethodIds: ["approval/respond"],
      availableEventTypes: [],
      missingEventTypes: ["approval.requested", "approval.resolved"],
    });
  });

  /**
   * 验证 Computer Use 即使全部声明能力已广告也因实验性视觉闭环缺口保持不可用。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("keeps a complete Computer Use advertisement experimental and unavailable", () => {
    const plugin = PLUGIN_CATALOG.find(
      (entry) => entry.pluginId === "computer-use",
    );

    expect(plugin).toBeDefined();
    expect(plugin!.readinessPolicy).toBe("experimental_unavailable");
    expect(
      getPluginCapabilityStatus(plugin!, ["computer.screenshot"]),
    ).toEqual({
      available: false,
      availableToolIds: ["computer.screenshot"],
      missingToolIds: ["computer.observe", "computer.interact"],
      availableMethodIds: [],
      missingMethodIds: ["approval/respond"],
      availableEventTypes: [],
      missingEventTypes: ["approval.requested", "approval.resolved"],
    });
    expect(
      getPluginCapabilityStatus(
        plugin!,
        ["computer.observe", "computer.screenshot", "computer.interact"],
        ["approval/respond"],
        ["approval.requested", "approval.resolved"],
      ),
    ).toEqual({
      available: false,
      availableToolIds: [
        "computer.observe",
        "computer.screenshot",
        "computer.interact",
      ],
      missingToolIds: [],
      availableMethodIds: ["approval/respond"],
      missingMethodIds: [],
      availableEventTypes: ["approval.requested", "approval.resolved"],
      missingEventTypes: [],
    });
  });

  /**
   * 验证桌面工具齐全但缺少 durable 交互审批时仍保持不可启用。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("requires interactive approval capabilities for Computer Use", () => {
    const plugin = PLUGIN_CATALOG.find(
      (entry) => entry.pluginId === "computer-use",
    );

    expect(plugin).toBeDefined();
    expect(
      getPluginCapabilityStatus(plugin!, [
        "computer.observe",
        "computer.screenshot",
        "computer.interact",
      ]),
    ).toMatchObject({
      available: false,
      missingToolIds: [],
      missingMethodIds: ["approval/respond"],
      missingEventTypes: ["approval.requested", "approval.resolved"],
    });
  });

  /**
   * 验证市场筛选同时遵守安装页签、分类和搜索词。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("filters installed plugins by category and query", () => {
    const installedPluginIds = new Set(["product-design", "github-workflow"]);

    expect(
      filterPluginCatalog(installedPluginIds, "installed", "developer", "github", (key) => key),
    ).toHaveLength(1);
    expect(
      filterPluginCatalog(installedPluginIds, "installed", "design", "github", (key) => key),
    ).toHaveLength(0);
  });
});
