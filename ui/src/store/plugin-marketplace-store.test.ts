import { beforeEach, describe, expect, it, vi } from "vitest";

const STORAGE_KEY = "agent-client.plugin-marketplace-preferences.v1";

describe("plugin marketplace preference hydration", () => {
  beforeEach(() => {
    window.localStorage.clear();
    vi.resetModules();
  });

  /**
   * 验证水合只投影固定目录 ID 与安装、启用布尔值，不保留额外字段。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("projects stored preferences into the closed local shape", async () => {
    window.localStorage.setItem(
      STORAGE_KEY,
      JSON.stringify({
        plugins: {
          "product-design": {
            installed: true,
            enabled: true,
            credential: "must-not-survive",
          },
          "unknown-plugin": { installed: true, enabled: true },
          "computer-use": { installed: false, enabled: false },
        },
        endpoint: "must-not-survive",
      }),
    );

    const { usePluginMarketplaceStore } = await import(
      "@/store/plugin-marketplace-store"
    );

    expect(usePluginMarketplaceStore.getState().plugins).toEqual({
      "product-design": { installed: true, enabled: true },
    });

    usePluginMarketplaceStore.getState().installPlugin("github-workflow");
    expect(JSON.parse(window.localStorage.getItem(STORAGE_KEY) ?? "{}")).toEqual({
      plugins: {
        "product-design": { installed: true, enabled: true },
        "github-workflow": { installed: true, enabled: false },
      },
    });
  });
});
