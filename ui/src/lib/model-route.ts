import type { ModelDescriptor } from "@/types/application-service";
import type { AgentModelSelection } from "@/types/agent-profile";

/**
 * 将 Provider、model 与 API family 组合为前端选择器的无歧义 key。
 *
 * 输入：服务端返回的完整模型路由身份。
 * 输出：仅用于当前 UI 生命周期的稳定 JSON key。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function getModelRouteKey(model: AgentModelSelection): string {
  return JSON.stringify([model.providerId, model.modelId, model.apiFamily]);
}

/**
 * 通过完整路由 key 查找模型，避免不同 Provider 或 API family 的同名模型碰撞。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function findModelByRouteKey(
  models: readonly ModelDescriptor[],
  routeKey: string,
): ModelDescriptor | undefined {
  return models.find((model) => getModelRouteKey(model) === routeKey);
}
