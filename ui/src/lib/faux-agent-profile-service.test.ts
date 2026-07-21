import { describe, expect, it } from "vitest";

import { createFauxAgentProfileService } from "@/lib/faux-agent-profile-service";
import type { AgentProfileInput } from "@/types/agent-profile";

const PROFILE_INPUT: AgentProfileInput = {
  displayName: "测试智能体",
  description: "只使用服务端广告能力",
  enabled: true,
  instructions: "先阅读约束，再检查目标文件。",
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
};

describe("FauxAgentProfileService", () => {
  /**
   * 验证创建、revision 更新和旧 revision 冲突拒绝。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("applies optimistic revisions to in-memory profiles", async () => {
    const service = createFauxAgentProfileService("rust", ["file.read"]);
    const created = await service.createProfile(PROFILE_INPUT);
    const updated = await service.updateProfile(created.profileId, created.revision, {
      ...PROFILE_INPUT,
      displayName: "更新后的智能体",
    });

    expect(updated.revision).toBe(2);
    expect(updated.displayName).toBe("更新后的智能体");
    await expect(
      service.updateProfile(created.profileId, created.revision, PROFILE_INPUT),
    ).rejects.toThrow("已在其他位置更新");
  });

  /**
   * 验证未被 initialize 广告的工具会 fail closed。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects tools outside the advertised capability set", async () => {
    const service = createFauxAgentProfileService("dotnet", []);

    await expect(service.createProfile(PROFILE_INPUT)).rejects.toThrow(
      "当前服务未广告",
    );
  });

  /**
   * 验证 deny 只作为运行期收紧意图，不会被预览层误解为新的授权规则。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("retains requested tools without treating deny mode as authorization", async () => {
    const service = createFauxAgentProfileService("rust", ["file.write"]);

    const created = await service.createProfile({
      ...PROFILE_INPUT,
      requestedToolIds: ["file.write"],
      dangerousActionMode: "deny",
    });

    expect(created.requestedToolIds).toEqual(["file.write"]);
    expect(created.dangerousActionMode).toBe("deny");
  });

  /**
   * 验证未来新增但尚无可信展示权限类的工具不会自动进入 allowlist。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("fails closed for unknown advertised tool ids", async () => {
    const service = createFauxAgentProfileService("rust", ["network.fetch"]);

    await expect(
      service.createProfile({
        ...PROFILE_INPUT,
        requestedToolIds: ["network.fetch"],
      }),
    ).rejects.toThrow("预览未支持");
  });

  /**
   * 验证绕过 TypeScript 的非法枚举与行为字段仍会在运行时 fail closed。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects forged approval and behavior values", async () => {
    const service = createFauxAgentProfileService("rust", ["file.read"]);

    await expect(
      service.createProfile({
        ...PROFILE_INPUT,
        dangerousActionMode: "allow",
      } as unknown as AgentProfileInput),
    ).rejects.toThrow("危险操作模式无效");
    await expect(
      service.createProfile({
        ...PROFILE_INPUT,
        behavior: { ...PROFILE_INPUT.behavior, responseStyle: "verbose" },
      } as unknown as AgentProfileInput),
    ).rejects.toThrow("回复风格无效");
    await expect(
      service.createProfile({
        ...PROFILE_INPUT,
        behavior: { ...PROFILE_INPUT.behavior, planFirst: "yes" },
      } as unknown as AgentProfileInput),
    ).rejects.toThrow("智能体输入格式无效");
    await expect(
      service.createProfile({
        ...PROFILE_INPUT,
        model: { ...PROFILE_INPUT.model, providerId: "faux " },
      }),
    ).rejects.toThrow("模型身份无效");
    await expect(
      service.createProfile({
        ...PROFILE_INPUT,
        requestedToolIds: ["file.read", "file.read"],
      }),
    ).rejects.toThrow("工具集合格式无效");
  });

  /**
   * 验证 Profile 及嵌套 DTO 的未知字段按共同 closed Schema 直接拒绝。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects unknown fields in closed profile objects", async () => {
    const service = createFauxAgentProfileService("rust", ["file.read"]);

    await expect(
      service.createProfile({
        ...PROFILE_INPUT,
        apiKey: "must-not-be-accepted",
      } as unknown as AgentProfileInput),
    ).rejects.toThrow("智能体输入格式无效");
    await expect(
      service.createProfile({
        ...PROFILE_INPUT,
        model: {
          ...PROFILE_INPUT.model,
          privateEndpoint: "https://private.invalid",
        },
      } as unknown as AgentProfileInput),
    ).rejects.toThrow("智能体输入格式无效");
    await expect(
      service.createProfile({
        ...PROFILE_INPUT,
        behavior: {
          ...PROFILE_INPUT.behavior,
          privateEndpoint: "https://private.invalid",
        },
      } as unknown as AgentProfileInput),
    ).rejects.toThrow("智能体输入格式无效");
  });
});
