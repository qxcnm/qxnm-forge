import { describe, expect, it } from "vitest";

import { findModelByRouteKey, getModelRouteKey } from "@/lib/model-route";
import type { ModelDescriptor } from "@/types/application-service";

const ROUTES: readonly ModelDescriptor[] = [
  {
    providerId: "same-provider",
    modelId: "same-model",
    apiFamily: "openai-completions",
    displayName: "Same model",
    supportsReasoning: false,
    supportsTools: true,
  },
  {
    providerId: "same-provider",
    modelId: "same-model",
    apiFamily: "openrouter-images",
    displayName: "Same model",
    supportsReasoning: false,
    supportsTools: false,
  },
];

describe("model route identity", () => {
  /**
   * 验证相同 Provider/model 下的不同 API family 不会被前端 key 合并。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("keeps API families distinct in selector keys", () => {
    expect(getModelRouteKey(ROUTES[0])).not.toBe(getModelRouteKey(ROUTES[1]));
  });

  /**
   * 验证选择器只能通过完整三元 key 找回精确路由。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("finds the exact route selected by its complete identity", () => {
    const selected = findModelByRouteKey(ROUTES, getModelRouteKey(ROUTES[1]));

    expect(selected?.apiFamily).toBe("openrouter-images");
  });
});
