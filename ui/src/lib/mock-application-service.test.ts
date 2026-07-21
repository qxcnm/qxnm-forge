import { beforeEach, describe, expect, it } from "vitest";

import {
  createApplicationServiceClient,
  resetMockApplicationServiceState,
} from "@/lib/mock-application-service";

describe("MockApplicationServiceClient", () => {
  beforeEach(() => {
    resetMockApplicationServiceState();
  });

  /**
   * 验证 Rust 画像只额外广告服务端实际具备的队列方法。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("advertises Rust-only follow-up methods", async () => {
    const rustClient = createApplicationServiceClient("rust");
    const dotnetClient = createApplicationServiceClient("dotnet");

    const [rustResult, dotnetResult] = await Promise.all([
      rustClient.initialize(),
      dotnetClient.initialize(),
    ]);

    expect(rustResult.capabilities.methods).toContain("run/followUp");
    expect(dotnetResult.capabilities.methods).not.toContain("run/followUp");
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
    ).rejects.toThrow("prompt must not be empty");
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
      apiFamily: "openai-completions",
      modelIds: ["gpt-5"],
      logoAssetId: "newapi-gzxsy",
      enabled: true,
    });
    const secret = "test-secret-must-not-return";
    const status = await client.setProviderCredential(
      created.connection.providerId,
      secret,
    );
    const connections = await client.listProviderConnections();

    expect(status).toEqual({
      providerId: "test-newapi",
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
      apiFamily: "openai-completions" as const,
      modelIds: ["model-v1"],
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
});
