import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ComponentProps } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { ProviderSettings } from "@/features/settings/provider-settings";
import i18n from "@/i18n";
import {
  createApplicationServiceClient,
  resetMockApplicationServiceState,
} from "@/lib/mock-application-service";
import type { ApplicationServiceClient } from "@/types/application-service";

const PROVIDER_METHODS = [
  "providerCatalog/list",
  "providerConnections/list",
  "providerConnections/create",
  "providerConnections/update",
  "providerConnections/delete",
  "providerCredentials/set",
  "providerCredentials/remove",
] as const;

/**
 * 使用隔离 QueryClient 和 faux application service 渲染 Provider 设置。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function renderProviderSettings(
  options: {
    readonly service?: ApplicationServiceClient;
    readonly supportedMethods?: readonly string[];
    readonly onModelReady?: ComponentProps<typeof ProviderSettings>["onModelReady"];
  } = {},
) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
  const service = options.service ?? createApplicationServiceClient("rust");
  const rendered = render(
    <QueryClientProvider client={queryClient}>
      <ProviderSettings
        backend="rust"
        service={service}
        supportedMethods={options.supportedMethods ?? PROVIDER_METHODS}
        onModelReady={options.onModelReady}
      />
    </QueryClientProvider>,
  );
  return { ...rendered, queryClient, service };
}

describe("ProviderSettings secret import boundary", () => {
  beforeEach(async () => {
    window.localStorage.clear();
    resetMockApplicationServiceState();
    await i18n.changeLanguage("zh-CN");
  });

  /**
   * 验证兼容模板来自 application service 目录，而不是设置组件内的固定数组。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("renders the provider catalog returned by the backend", async () => {
    const service = createApplicationServiceClient("rust");
    const listProviderCatalog = vi
      .spyOn(service, "listProviderCatalog")
      .mockResolvedValue([
        {
          templateId: "backend-only-openai-completions",
          displayName: "Backend Only",
          suggestedProviderId: "custom-backend-only",
          apiFamily: "openai-completions",
          defaultBaseUrl: "https://api.example.invalid/v1",
          modelDiscovery: "openai-models",
          logoAssetId: null,
        },
      ]);

    renderProviderSettings({ service });

    fireEvent.click(
      await screen.findByRole("button", { name: "配置 Backend Only" }),
    );
    expect(listProviderCatalog).toHaveBeenCalledTimes(1);
    expect(screen.getByLabelText("名称")).toHaveValue("Backend Only");
    expect(screen.getByLabelText("Provider ID")).toHaveValue("custom-backend-only");
    expect(screen.getByLabelText("Base URL")).toHaveValue(
      "https://api.example.invalid/v1",
    );
    expect(
      screen.getByText(
        "图片输出需单独保存 Image Key；即使与上方 Key 相同，也请在这里重新输入。",
      ),
    ).toBeInTheDocument();
  });

  /**
   * 验证浏览器预览也展示后端审计过的三家主流官方兼容入口。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("groups mainstream official provider presets ahead of compatible services", async () => {
    renderProviderSettings();

    expect(await screen.findByText("官方提供商")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "配置 OpenAI" })).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "配置 Anthropic Claude" }),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "配置 Google Gemini" }));

    expect(screen.getByLabelText("名称")).toHaveValue("Google Gemini");
    expect(screen.getByLabelText("Provider ID")).toHaveValue("custom-google");
    expect(screen.getByLabelText("Base URL")).toHaveValue(
      "https://generativelanguage.googleapis.com/v1beta/openai",
    );
  });

  /**
   * 验证导入原文只停留在遮罩 DOM 输入，失败和成功都会清空且 secret 不进入持久化或 Query cache。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("masks, clears, and isolates imported New API secrets", async () => {
    const { queryClient } = renderProviderSettings();
    await screen.findByRole("heading", { name: "新建提供商" });
    const importInput = screen.getByLabelText("导入 New API 连接 JSON");
    const importButton = screen.getByRole("button", { name: "导入" });
    const failedSecret = "failed-import-secret";
    const importedSecret = "successful-import-secret";

    expect(importInput).toHaveAttribute("type", "password");
    fireEvent.change(importInput, {
      target: {
        value: `{"_type":"unsupported","key":"${failedSecret}"}`,
      },
    });
    fireEvent.click(importButton);

    expect(importInput).toHaveValue("");
    expect(screen.getByLabelText("Responses / Chat API Key")).toHaveValue("");
    expect(
      screen.getByText("无法解析连接 JSON，请检查 _type、url 和 key 字段"),
    ).toBeInTheDocument();
    expect(
      JSON.stringify(
        queryClient
          .getQueryCache()
          .getAll()
          .map((query) => query.state.data),
      ),
    ).not.toContain(failedSecret);

    fireEvent.change(importInput, {
      target: {
        value: `{"_type":"newapi_channel_conn","key":"${importedSecret}","url":"https://api.example.invalid"}`,
      },
    });
    fireEvent.click(importButton);

    expect(importInput).toHaveValue("");
    expect(screen.getByLabelText("Responses / Chat API Key")).toHaveAttribute("type", "password");
    expect(screen.getByLabelText("Responses / Chat API Key")).toHaveValue(importedSecret);
    fireEvent.change(screen.getByLabelText("模型 ID"), {
      target: { value: "manual-preview-model" },
    });
    expect(
      JSON.stringify(
        queryClient
          .getQueryCache()
          .getAll()
          .map((query) => query.state.data),
      ),
    ).not.toMatch(/failed-import-secret|successful-import-secret/);

    fireEvent.click(screen.getByRole("button", { name: "保存" }));
    expect(screen.getByLabelText("Responses / Chat API Key")).toHaveValue("");
    expect(await screen.findByText("已保存，预览已更新")).toBeInTheDocument();
    expect(
      screen.getByRole("button", {
        name: "编辑提供商 星思研 New API · 凭据已配置",
      }),
    ).toBeInTheDocument();

    await waitFor(() =>
      expect(
        JSON.stringify(
          queryClient
            .getQueryCache()
            .getAll()
            .map((query) => query.state.data),
        ),
      ).not.toMatch(/failed-import-secret|successful-import-secret/),
    );
    const storedPreferenceValues = Array.from(
      { length: window.localStorage.length },
      (_, index) => {
        const key = window.localStorage.key(index);
        return key === null ? null : window.localStorage.getItem(key);
      },
    ).join("\n");
    expect(storedPreferenceValues).not.toMatch(
      /failed-import-secret|successful-import-secret/,
    );
    expect(window.location.search).not.toMatch(
      /failed-import-secret|successful-import-secret/,
    );
  });

  /**
   * 验证 Responses 与 Image Key 从两个独立密码框提交，不会互相复制或回退。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("submits responses and image credentials independently", async () => {
    const service = createApplicationServiceClient("rust");
    const setProviderCredential = vi.spyOn(service, "setProviderCredential");
    renderProviderSettings({ service });

    fireEvent.click(await screen.findByRole("button", { name: "配置 OpenAI" }));
    fireEvent.change(screen.getByLabelText("模型 ID"), {
      target: { value: "image-capable-model" },
    });
    fireEvent.change(screen.getByLabelText("Responses / Chat API Key"), {
      target: { value: "responses-canary" },
    });
    fireEvent.change(screen.getByLabelText("Image API Key"), {
      target: { value: "image-canary" },
    });
    fireEvent.click(screen.getByRole("button", { name: "保存" }));

    await waitFor(() => expect(setProviderCredential).toHaveBeenCalledTimes(2));
    expect(setProviderCredential).toHaveBeenNthCalledWith(
      1,
      "custom-openai",
      "responses",
      "responses-canary",
    );
    expect(setProviderCredential).toHaveBeenNthCalledWith(
      2,
      "custom-openai",
      "image",
      "image-canary",
    );
    expect(screen.getByLabelText("Responses / Chat API Key")).toHaveValue("");
    expect(screen.getByLabelText("Image API Key")).toHaveValue("");
  });

  /**
   * 验证桌面 capability 可把空模型连接、瞬时凭据与显式发现串成一次保存流程。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("discovers models after saving a credential-backed connection", async () => {
    const service = createApplicationServiceClient("rust");
    const discoverModels = vi
      .spyOn(service, "discoverProviderModels")
      .mockImplementation((connectionId, expectedRevision) =>
        Promise.resolve({
          connection: {
            connectionId,
            revision: expectedRevision + 1,
            displayName: "星思研 New API",
            providerId: "newapi-gzxsy",
            baseUrl: "https://api.example.invalid/v1",
            modelsUrl: "https://api.example.invalid/v1/models",
            apiFamily: "openai-completions",
            modelIds: ["model-a", "model-b"],
            supportsTools: false,
            supportsImageInput: false,
            supportsImageOutput: false,
            logoAssetId: "newapi-gzxsy",
            enabled: true,
            credentialConfigured: true,
            imageCredentialConfigured: false,
            createdAt: "2026-07-22T00:00:00Z",
            updatedAt: "2026-07-22T00:00:01Z",
          },
          discoveredCount: 2,
          restartRequired: true,
        }),
      );
    const onModelReady = vi.fn();
    renderProviderSettings({
      service,
      supportedMethods: [
        ...PROVIDER_METHODS,
        "providerConnections/discoverModels",
      ],
      onModelReady,
    });
    const importInput = await screen.findByLabelText("导入 New API 连接 JSON");

    fireEvent.change(importInput, {
      target: {
        value:
          '{"_type":"newapi_channel_conn","key":"discovery-secret","url":"https://api.example.invalid"}',
      },
    });
    fireEvent.click(screen.getByRole("button", { name: "导入" }));
    expect(screen.getByLabelText("模型 ID")).toHaveValue("");
    fireEvent.click(
      screen.getByRole("button", { name: "保存并获取模型" }),
    );

    expect(
      await screen.findByText("已获取并保存 2 个模型，模型选择器已刷新"),
    ).toBeInTheDocument();
    expect(discoverModels).toHaveBeenCalledWith(expect.any(String), 1);
    expect(screen.getByLabelText("模型 ID")).toHaveValue("model-a\nmodel-b");
    expect(screen.getByLabelText("Responses / Chat API Key")).toHaveValue("");
    expect(onModelReady).toHaveBeenCalledWith("rust", {
      providerId: "newapi-gzxsy",
      modelId: "model-a",
      apiFamily: "openai-completions",
    });
  });

  /**
   * 验证 mutation 响应丢失但状态已落盘时会重读连接，而不是把旧 Query 快照继续留在界面。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("reconciles provider state after an ambiguous mutation failure", async () => {
    const service = createApplicationServiceClient("rust");
    const createConnection = service.createProviderConnection.bind(service);
    const listConnections = vi.spyOn(service, "listProviderConnections");
    vi.spyOn(service, "createProviderConnection").mockImplementation(async (input) => {
      await createConnection(input);
      throw new Error("response lost after commit");
    });
    renderProviderSettings({ service });
    await waitFor(() => expect(listConnections).toHaveBeenCalledTimes(1));

    fireEvent.change(screen.getByLabelText("导入 New API 连接 JSON"), {
      target: {
        value:
          '{"_type":"newapi_channel_conn","url":"https://api.example.invalid"}',
      },
    });
    fireEvent.click(screen.getByRole("button", { name: "导入" }));
    fireEvent.change(screen.getByLabelText("模型 ID"), {
      target: { value: "reconciled-model" },
    });
    fireEvent.click(screen.getByRole("button", { name: "保存" }));

    expect(
      await screen.findByText(
        "连接保存结果未确认；已重新加载最新状态，请核对后再试",
      ),
    ).toBeInTheDocument();
    await waitFor(() => expect(listConnections.mock.calls.length).toBeGreaterThan(1));
    expect(
      await screen.findByRole("button", {
        name: /编辑提供商 星思研 New API · 凭据未配置/,
      }),
    ).toBeInTheDocument();
  });

  /**
   * 验证 mutation 结果和后续重读同时失败时不会误报“已重新加载”。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("reports when an ambiguous mutation cannot be reconciled", async () => {
    const service = createApplicationServiceClient("rust");
    const createConnection = service.createProviderConnection.bind(service);
    vi.spyOn(service, "createProviderConnection").mockImplementation(async (input) => {
      await createConnection(input);
      throw new Error("response lost after commit");
    });
    const { queryClient } = renderProviderSettings({ service });
    await waitFor(() =>
      expect(
        queryClient.getQueryState([
          "provider-connections",
          "faux-preview",
          "rust",
        ])?.status,
      ).toBe("success"),
    );
    vi.spyOn(queryClient, "invalidateQueries").mockRejectedValue(
      new Error("refresh failed"),
    );

    fireEvent.change(screen.getByLabelText("导入 New API 连接 JSON"), {
      target: {
        value:
          '{"_type":"newapi_channel_conn","url":"https://api.example.invalid"}',
      },
    });
    fireEvent.click(screen.getByRole("button", { name: "导入" }));
    fireEvent.change(screen.getByLabelText("模型 ID"), {
      target: { value: "unconfirmed-model" },
    });
    fireEvent.click(screen.getByRole("button", { name: "保存" }));

    expect(
      await screen.findByText(
        "操作结果未确认，且无法重新加载最新 Provider 状态；请重新打开设置后核对",
      ),
    ).toBeInTheDocument();
    expect(
      screen.queryByText(
        "连接保存结果未确认；已重新加载最新状态，请核对后再试",
      ),
    ).not.toBeInTheDocument();
  });

  /**
   * 验证 Provider 保存先等待新 initialize 代际，再失效 models 查询。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("refreshes models only after initialize succeeds", async () => {
    const { queryClient } = renderProviderSettings();
    await waitFor(() =>
      expect(
        queryClient.getQueryState([
          "provider-connections",
          "faux-preview",
          "rust",
        ])?.status,
      ).toBe("success"),
    );
    let resolveInitialize!: () => void;
    const pendingInitialize = new Promise<void>((resolve) => {
      resolveInitialize = resolve;
    });
    const invalidationSpy = vi
      .spyOn(queryClient, "invalidateQueries")
      .mockImplementation((filters) => {
        const serializedKey = JSON.stringify(filters?.queryKey);
        return serializedKey ===
          JSON.stringify(["application-service", "rust", "initialize"])
          ? pendingInitialize
          : Promise.resolve();
      });
    const importInput = screen.getByLabelText("导入 New API 连接 JSON");

    fireEvent.change(importInput, {
      target: {
        value:
          '{"_type":"newapi_channel_conn","url":"https://api.example.invalid"}',
      },
    });
    fireEvent.click(screen.getByRole("button", { name: "导入" }));
    fireEvent.change(screen.getByLabelText("模型 ID"), {
      target: { value: "manual-preview-model" },
    });
    fireEvent.click(screen.getByRole("button", { name: "保存" }));

    await waitFor(() => expect(invalidationSpy).toHaveBeenCalledTimes(4));
    expect(invalidationSpy).toHaveBeenNthCalledWith(
      1,
      {
        queryKey: ["application-service", "rust", "initialize"],
        exact: true,
        refetchType: "active",
      },
      { throwOnError: true },
    );
    expect(invalidationSpy).toHaveBeenNthCalledWith(
      2,
      {
        queryKey: ["provider-connections", "faux-preview", "rust"],
        exact: true,
        refetchType: "active",
      },
      { throwOnError: true },
    );
    expect(
      invalidationSpy.mock.calls.some(
        ([filters]) =>
          JSON.stringify(filters?.queryKey) ===
          JSON.stringify(["application-service", "rust", "models"]),
      ),
    ).toBe(false);

    resolveInitialize();
    expect(await screen.findByText("已保存，预览已更新")).toBeInTheDocument();
    expect(invalidationSpy).toHaveBeenCalledWith(
      {
        queryKey: ["application-service", "rust", "models"],
        refetchType: "active",
      },
      { throwOnError: true },
    );
  });

  /**
   * 验证共享 Provider 状态变更会让另一套后端的握手与连接缓存立即过期。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("invalidates inactive backend provider snapshots after a mutation", async () => {
    const { queryClient } = renderProviderSettings();
    await waitFor(() =>
      expect(
        queryClient.getQueryState([
          "provider-connections",
          "faux-preview",
          "rust",
        ])?.status,
      ).toBe("success"),
    );
    const peerKeys = [
      ["application-service", "dotnet", "initialize"],
      ["provider-connections", "faux-preview", "dotnet"],
    ] as const;
    for (const peerKey of peerKeys) {
      queryClient.setQueryData(peerKey, []);
    }

    fireEvent.change(screen.getByLabelText("导入 New API 连接 JSON"), {
      target: {
        value:
          '{"_type":"newapi_channel_conn","url":"https://api.example.invalid"}',
      },
    });
    fireEvent.click(screen.getByRole("button", { name: "导入" }));
    fireEvent.change(screen.getByLabelText("模型 ID"), {
      target: { value: "cross-backend-model" },
    });
    fireEvent.click(screen.getByRole("button", { name: "保存" }));

    expect(await screen.findByText("已保存，预览已更新")).toBeInTheDocument();
    for (const peerKey of peerKeys) {
      expect(queryClient.getQueryState(peerKey)?.isInvalidated).toBe(true);
    }
  });

  /**
   * 验证 initialize refetch 失败会关闭刷新流程且不继续读取 models。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("surfaces initialize refresh failures without refetching models", async () => {
    const { queryClient } = renderProviderSettings();
    await waitFor(() =>
      expect(
        queryClient.getQueryState([
          "provider-connections",
          "faux-preview",
          "rust",
        ])?.status,
      ).toBe("success"),
    );
    const invalidationSpy = vi
      .spyOn(queryClient, "invalidateQueries")
      .mockImplementation((filters) => {
        const serializedKey = JSON.stringify(filters?.queryKey);
        return serializedKey ===
          JSON.stringify(["application-service", "rust", "initialize"])
          ? Promise.reject(new Error("initialize failed"))
          : Promise.resolve();
      });
    const importInput = screen.getByLabelText("导入 New API 连接 JSON");

    fireEvent.change(importInput, {
      target: {
        value:
          '{"_type":"newapi_channel_conn","url":"https://api.example.invalid"}',
      },
    });
    fireEvent.click(screen.getByRole("button", { name: "导入" }));
    fireEvent.change(screen.getByLabelText("模型 ID"), {
      target: { value: "manual-preview-model" },
    });
    fireEvent.click(screen.getByRole("button", { name: "保存" }));

    expect(
      await screen.findByText(
        "操作已被服务接受，但最新 Provider 状态刷新失败；请重新打开设置",
      ),
    ).toBeInTheDocument();
    expect(
      invalidationSpy.mock.calls.some(
        ([filters]) => filters?.queryKey?.[2] === "models",
      ),
    ).toBe(false);
  });
});
