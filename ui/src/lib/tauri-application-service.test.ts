import { beforeEach, describe, expect, it, vi } from "vitest";

const invokeMock = vi.hoisted(() => vi.fn());

vi.mock("@tauri-apps/api/core", () => ({
  invoke: invokeMock,
}));

import { TauriApplicationServiceClient } from "@/lib/tauri-application-service";

const PROFILE = {
  profileId: "profile-1",
  revision: 3,
  displayName: "仓库助手",
  description: "审阅并实现仓库任务",
  enabled: true,
  instructions: "先读取约束，再完成实现。",
  model: {
    providerId: "faux",
    modelId: "faux-v1",
    apiFamily: "faux",
  },
  requestedToolIds: ["file.read"],
  dangerousActionMode: "ask",
  behavior: {
    responseStyle: "balanced",
    planFirst: true,
    reviewChanges: true,
  },
  createdAt: "2026-07-21T00:00:00Z",
  updatedAt: "2026-07-21T00:01:00Z",
} as const;

describe("TauriApplicationServiceClient", () => {
  beforeEach(() => {
    invokeMock.mockReset();
  });

  /**
   * 验证桌面 client 使用生产 Profile RPC，并把精确 revision 绑定到 run/start。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("uses profile RPC and binds the selected revision to run/start", async () => {
    invokeMock.mockImplementation((_command: string, arguments_: Record<string, unknown>) => {
      if (arguments_.method === "agentProfiles/list") {
        return Promise.resolve({ profiles: [PROFILE] });
      }
      if (arguments_.method === "run/start") {
        return Promise.resolve({ runId: "run-1" });
      }
      throw new Error("unexpected command");
    });
    const client = new TauriApplicationServiceClient("rust");

    expect(await client.listProfiles()).toEqual([PROFILE]);
    await client.startRun({
      sessionId: "session-1",
      prompt: "检查实现",
      model: PROFILE.model,
      agentProfile: { profileId: PROFILE.profileId, revision: PROFILE.revision },
    });

    expect(invokeMock).toHaveBeenLastCalledWith("application_service_request", {
      backend: "rust",
      method: "run/start",
      params: {
        sessionId: "session-1",
        input: { role: "user", content: [{ type: "text", text: "检查实现" }] },
        provider: { id: "faux", modelId: "faux-v1", apiFamily: "faux" },
        agentProfile: { profileId: "profile-1", revision: 3 },
      },
    });
  });

  /**
   * 验证 host 即使返回额外 secret 字段，前端严格 schema 仍拒绝该投影。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects profile projections containing unknown secret fields", async () => {
    invokeMock.mockResolvedValue({
      profiles: [{ ...PROFILE, apiKey: "must-not-enter-ui" }],
    });
    const client = new TauriApplicationServiceClient("dotnet");

    await expect(client.listProfiles()).rejects.toThrow();
  });

  /**
   * 验证桌面边界执行共同 Schema 的工具唯一性与 UTC 时间格式。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects profile projections outside the shared schema", async () => {
    invokeMock.mockResolvedValue({
      profiles: [
        {
          ...PROFILE,
          requestedToolIds: ["file.read", "file.read"],
          updatedAt: "2026-07-21T08:01:00+08:00",
        },
      ],
    });
    const client = new TauriApplicationServiceClient("rust");

    await expect(client.listProfiles()).rejects.toThrow();
  });
});
