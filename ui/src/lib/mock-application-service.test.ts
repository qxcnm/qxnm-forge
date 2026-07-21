import { describe, expect, it } from "vitest";

import { createApplicationServiceClient } from "@/lib/mock-application-service";

describe("MockApplicationServiceClient", () => {
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
});
