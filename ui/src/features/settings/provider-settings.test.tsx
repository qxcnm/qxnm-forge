import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { ProviderSettings } from "@/features/settings/provider-settings";
import i18n from "@/i18n";
import {
  createApplicationServiceClient,
  resetMockApplicationServiceState,
} from "@/lib/mock-application-service";
import type { ApplicationServiceClient } from "@/types/application-service";

const PROVIDER_METHODS = [
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
    expect(screen.getByLabelText("API Key")).toHaveValue("");
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
    expect(screen.getByLabelText("API Key")).toHaveAttribute("type", "password");
    expect(screen.getByLabelText("API Key")).toHaveValue(importedSecret);
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
    expect(screen.getByLabelText("API Key")).toHaveValue("");
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
            apiFamily: "openai-completions",
            modelIds: ["model-a", "model-b"],
            logoAssetId: "newapi-gzxsy",
            enabled: true,
            credentialConfigured: true,
            createdAt: "2026-07-22T00:00:00Z",
            updatedAt: "2026-07-22T00:00:01Z",
          },
          discoveredCount: 2,
          restartRequired: true,
        }),
      );
    renderProviderSettings({
      service,
      supportedMethods: [
        ...PROVIDER_METHODS,
        "providerConnections/discoverModels",
      ],
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
    expect(screen.getByLabelText("API Key")).toHaveValue("");
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

    await waitFor(() => expect(invalidationSpy).toHaveBeenCalledTimes(2));
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
        ([filters]) => filters?.queryKey?.[2] === "models",
      ),
    ).toBe(false);

    resolveInitialize();
    expect(await screen.findByText("已保存，预览已更新")).toBeInTheDocument();
    expect(invalidationSpy).toHaveBeenNthCalledWith(
      3,
      {
        queryKey: ["application-service", "rust", "models"],
        refetchType: "active",
      },
      { throwOnError: true },
    );
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
        "连接已保存，但最新 Provider 状态刷新失败；请重新打开设置",
      ),
    ).toBeInTheDocument();
    expect(
      invalidationSpy.mock.calls.some(
        ([filters]) => filters?.queryKey?.[2] === "models",
      ),
    ).toBe(false);
  });
});
