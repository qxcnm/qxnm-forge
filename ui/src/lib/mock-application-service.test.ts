import { beforeEach, describe, expect, it, vi } from "vitest";

import {
  createApplicationServiceClient,
  resetMockApplicationServiceState,
} from "@/lib/mock-application-service";
import { projectPendingApprovals } from "@/features/approvals/pending-approvals";

describe("MockApplicationServiceClient", () => {
  beforeEach(() => {
    resetMockApplicationServiceState();
  });

  /**
   * 验证 faux 客户端不会广告尚未实现的 Session 与运行控制方法。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("does not advertise unimplemented application service methods", async () => {
    const rustClient = createApplicationServiceClient("rust");
    const dotnetClient = createApplicationServiceClient("dotnet");

    const [rustResult, dotnetResult] = await Promise.all([
      rustClient.initialize(),
      dotnetClient.initialize(),
    ]);

    for (const result of [rustResult, dotnetResult]) {
      for (const method of [
        "session/branch/select",
        "session/compact",
        "run/steer",
        "run/followUp",
      ]) {
        expect(result.capabilities.methods).not.toContain(method);
      }
    }
  });

  /**
   * 验证 faux 能力投影广告其可持久化与解决的审批事件。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("advertises the approval events produced by the faux service", async () => {
    const result = await createApplicationServiceClient("rust").initialize();

    expect(result.capabilities.eventTypes).toEqual(
      expect.arrayContaining(["approval.requested", "approval.resolved"]),
    );
  });

  /**
   * 验证预览客户端不会接受空白任务。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects an empty prompt", async () => {
    const client = createApplicationServiceClient("rust");

    await expect(
      client.startRun({
        sessionId: "test-session",
        prompt: "   ",
        model: {
          providerId: "faux",
          modelId: "faux-v1",
          apiFamily: "faux",
        },
      }),
    ).rejects.toThrow("run input must not be empty");
  });

  /**
   * 验证 faux 初始化广告 Agent 编辑器支持的真实工具 ID 子集。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("advertises the supported workspace tool registry", async () => {
    const result = await createApplicationServiceClient("rust").initialize();

    expect(result.capabilities.tools).toEqual([
      "file.read",
      "search.text",
      "file.write",
      "file.edit",
      "process.exec",
      "shell.exec",
    ]);
  });

  /**
   * 验证 Provider 凭据正文不会进入连接投影或方法响应。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("never returns provider credential contents", async () => {
    const client = createApplicationServiceClient("rust");
    const created = await client.createProviderConnection({
      displayName: "测试 New API",
      providerId: "test-newapi",
      baseUrl: "https://example.invalid/v1",
      modelsUrl: "https://example.invalid/v1/models",
      apiFamily: "openai-completions",
      modelIds: ["gpt-5"],
      supportsTools: false,
      supportsImageInput: false,
      supportsImageOutput: false,
      logoAssetId: "newapi-gzxsy",
      enabled: true,
    });
    const secret = "test-secret-must-not-return";
    const status = await client.setProviderCredential(
      created.connection.providerId,
      "responses",
      secret,
    );
    const connections = await client.listProviderConnections();

    expect(status).toEqual({
      providerId: "test-newapi",
      credentialKind: "responses",
      credentialConfigured: true,
      restartRequired: true,
    });
    expect(connections[0]?.credentialConfigured).toBe(true);
    expect(JSON.stringify({ status, connections })).not.toContain(secret);
    expect(JSON.stringify(connections)).not.toContain("credential\"");
  });

  /**
   * 验证浏览器预览与真实 daemon 使用相同的 Provider 命名空间边界。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("accepts dotted custom ids and rejects reserved provider namespaces", async () => {
    const client = createApplicationServiceClient("rust");
    const input = {
      displayName: "自定义 Provider",
      providerId: "custom.example",
      baseUrl: "https://example.invalid/v1",
      modelsUrl: "https://example.invalid/v1/models",
      apiFamily: "openai-completions" as const,
      modelIds: ["model-v1"],
      supportsTools: false,
      supportsImageInput: false,
      supportsImageOutput: false,
      logoAssetId: "custom.logo",
      enabled: true,
    };

    await expect(client.createProviderConnection(input)).resolves.toMatchObject({
      connection: { providerId: "custom.example", logoAssetId: "custom.logo" },
    });
    for (const providerId of ["faux", "relay-example", "anthropic", "deepseek", "openai"]) {
      await expect(client.createProviderConnection({ ...input, providerId })).rejects.toThrow(
        "Provider 配置无效",
      );
    }
  });

  /**
   * 验证预览模型目录只投影当前后端已启用且已配置凭据的完整 Provider 路由。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("projects configured provider models with backend-isolated route identity", async () => {
    const rustClient = createApplicationServiceClient("rust");
    const dotnetClient = createApplicationServiceClient("dotnet");
    const created = await rustClient.createProviderConnection({
      displayName: "Route Provider",
      providerId: "custom.routes",
      baseUrl: "https://example.invalid/v1",
      modelsUrl: "https://example.invalid/v1/models",
      apiFamily: "openai-completions",
      modelIds: ["shared-model", "second-model"],
      supportsTools: true,
      supportsImageInput: true,
      supportsImageOutput: true,
      logoAssetId: null,
      enabled: true,
    });

    expect(await rustClient.listModels()).toHaveLength(1);
    await rustClient.setProviderCredential(
      created.connection.providerId,
      "responses",
      "test-only-secret",
    );

    expect(await rustClient.listModels()).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          providerId: "custom.routes",
          modelId: "shared-model",
          apiFamily: "openai-completions",
          capabilities: {
            input: ["text", "image"],
            output: ["text", "image"],
          },
          supportsTools: true,
          supportsImageInput: true,
          supportsImageOutput: true,
        }),
        expect.objectContaining({
          providerId: "custom.routes",
          modelId: "second-model",
          apiFamily: "openai-completions",
        }),
      ]),
    );
    expect(await dotnetClient.listModels()).toEqual([
      expect.objectContaining({
        providerId: "faux",
        modelId: "faux-v1",
        apiFamily: "faux",
      }),
    ]);
    await expect(
      rustClient.startRun({
        sessionId: "route-identity",
        prompt: "verify route",
        model: {
          providerId: "custom.routes",
          modelId: "shared-model",
          apiFamily: "wrong-family",
        },
      }),
    ).rejects.toThrow("model route is not available");

    await rustClient.removeProviderCredential(created.connection.providerId, "responses");
    expect(await rustClient.listModels()).toHaveLength(1);
  });

  /**
   * 验证 faux Session 可完成归档、恢复与永久删除生命周期。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("archives restores and deletes sessions", async () => {
    const client = createApplicationServiceClient("dotnet");

    expect((await client.archiveSession("backend-contract")).archived).toBe(true);
    expect((await client.restoreSession("backend-contract")).archived).toBe(false);
    await client.deleteSession("backend-contract");

    expect((await client.listSessions()).some(
      (session) => session.sessionId === "backend-contract",
    )).toBe(false);
  });

  /**
   * 验证 faux session/get 返回完整 portable 消息且每次调用得到独立深复制。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("returns isolated portable session message snapshots", async () => {
    const client = createApplicationServiceClient("rust");

    const first = await client.getSession("desktop-shell", 0);
    const second = await client.getSession("desktop-shell");

    expect(first.sessionId).toBe("desktop-shell");
    expect(first.messages.map((message) => message.role)).toEqual(["user", "assistant"]);
    expect(first.messages).not.toBe(second.messages);
    expect(second).toEqual(first);
    await expect(client.getSession("desktop-shell", -1)).rejects.toThrow();
    await expect(client.getSession("missing-session")).rejects.toThrow();
  });

  /**
   * 验证浏览器预览提供可操作审批，并拒绝未提供或重复的 choice。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("resolves the deterministic approval flow exactly once", async () => {
    const client = createApplicationServiceClient("rust");
    const initial = await client.getSession("approval-flow");
    const [approval] = projectPendingApprovals(initial);

    expect(approval?.request.operation).toBe("file.write");
    await client.respondToApproval(
      "approval-flow",
      "preview-run-approval",
      "preview-approval-1",
      { choice: "deny" },
    );

    const resolved = await client.getSession("approval-flow");
    expect(projectPendingApprovals(resolved)).toEqual([]);
    expect(resolved.activeRunId).toBeNull();
    expect(
      (await client.listSessions()).find(
        (session) => session.sessionId === "approval-flow",
      ),
    ).not.toHaveProperty("status");
    expect(resolved.events.at(-1)).toMatchObject({
      type: "approval.resolved",
      data: {
        approvalId: "preview-approval-1",
        decision: { choice: "deny" },
        resolutionSource: "client",
      },
    });
    await expect(
      client.respondToApproval(
        "approval-flow",
        "preview-run-approval",
        "preview-approval-1",
        { choice: "deny" },
      ),
    ).rejects.toThrow();
  });

  /**
   * 验证 faux 服务拒绝已过期审批且不会追加伪造的 resolution。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects an expired approval without recording a resolution", async () => {
    const client = createApplicationServiceClient("rust");
    const before = await client.getSession("approval-flow");
    const now = vi.spyOn(Date, "now").mockReturnValue(Date.parse("2100-01-01T00:00:00Z"));

    await expect(
      client.respondToApproval(
        "approval-flow",
        "preview-run-approval",
        "preview-approval-1",
        { choice: "deny" },
      ),
    ).rejects.toThrow();
    now.mockRestore();

    const after = await client.getSession("approval-flow");
    expect(after).toEqual(before);
    expect(after.events).not.toEqual(
      expect.arrayContaining([expect.objectContaining({ type: "approval.resolved" })]),
    );
  });
});
