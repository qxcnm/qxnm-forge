import type { BackendKind } from "@/types/application-service";
import { AGENT_TOOL_PRESENTATIONS } from "@/data/agent-tools";
import type {
  AgentProfile,
  AgentProfileInput,
  AgentProfileService,
} from "@/types/agent-profile";

const DEFAULT_MODEL = {
  providerId: "faux",
  modelId: "faux-v1",
  apiFamily: "faux",
} as const;
const PRESENTED_TOOL_IDS = new Set(
  AGENT_TOOL_PRESENTATIONS.map((tool) => tool.toolId),
);
const DANGEROUS_ACTION_MODES = new Set(["ask", "deny"]);
const RESPONSE_STYLES = new Set(["concise", "balanced", "detailed"]);
const PROVIDER_ID_PATTERN = /^[a-z0-9][a-z0-9.-]*$/;
const API_FAMILY_PATTERN = /^[a-z0-9][a-z0-9-]*$/;
const TOOL_ID_PATTERN = /^[a-z][a-z0-9_.-]*$/;
const PROFILE_INPUT_KEYS = new Set([
  "displayName",
  "description",
  "enabled",
  "instructions",
  "model",
  "requestedToolIds",
  "dangerousActionMode",
  "behavior",
]);
const MODEL_KEYS = new Set(["providerId", "modelId", "apiFamily"]);
const BEHAVIOR_KEYS = new Set(["responseStyle", "planFirst", "reviewChanges"]);

/**
 * 判断对象是否只包含闭合 DTO 明确允许的字段。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function hasExactKeys(value: object, allowedKeys: ReadonlySet<string>): boolean {
  const keys = Object.keys(value);
  return keys.length === allowedKeys.size && keys.every((key) => allowedKeys.has(key));
}

/**
 * 按 Unicode scalar value 计算共同 Schema 使用的字符串长度。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function unicodeScalarCount(value: string): number {
  return [...value].length;
}

/**
 * 产生短延迟以保留真实 application service 的异步交互形态。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function previewWait(milliseconds: number): Promise<void> {
  return new Promise((resolve) => globalThis.setTimeout(resolve, milliseconds));
}

/**
 * 深复制智能体投影，避免调用方修改 service 内部数组。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function cloneProfile(profile: AgentProfile): AgentProfile {
  return {
    ...profile,
    model: { ...profile.model },
    requestedToolIds: [...profile.requestedToolIds],
    behavior: { ...profile.behavior },
  };
}

/**
 * 校验并规范化用户可编辑字段，拒绝未知工具与危险模式越权组合。
 *
 * 输入：未受信任的表单投影和服务端已广告工具集合。
 * 输出：去除首尾空白且数组去重后的配置。
 * 失败：名称、说明、指令、模型或工具越界时抛出固定错误。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function normalizeInput(
  input: AgentProfileInput,
  supportedToolIds: ReadonlySet<string>,
): AgentProfileInput {
  if (
    !input ||
    typeof input !== "object" ||
    typeof input.displayName !== "string" ||
    typeof input.description !== "string" ||
    typeof input.enabled !== "boolean" ||
    typeof input.instructions !== "string" ||
    !hasExactKeys(input, PROFILE_INPUT_KEYS) ||
    !input.model ||
    typeof input.model !== "object" ||
    !hasExactKeys(input.model, MODEL_KEYS) ||
    typeof input.model.providerId !== "string" ||
    typeof input.model.modelId !== "string" ||
    typeof input.model.apiFamily !== "string" ||
    !Array.isArray(input.requestedToolIds) ||
    !input.behavior ||
    typeof input.behavior !== "object" ||
    !hasExactKeys(input.behavior, BEHAVIOR_KEYS) ||
    typeof input.behavior.planFirst !== "boolean" ||
    typeof input.behavior.reviewChanges !== "boolean"
  ) {
    throw new Error("智能体输入格式无效");
  }
  if (!DANGEROUS_ACTION_MODES.has(input.dangerousActionMode)) {
    throw new Error("危险操作模式无效");
  }
  if (!RESPONSE_STYLES.has(input.behavior.responseStyle)) {
    throw new Error("回复风格无效");
  }

  const displayName = input.displayName.trim();
  const description = input.description.trim();
  const instructions = input.instructions.trim();
  const requestedToolIdSet = new Set<string>();
  for (const toolId of input.requestedToolIds as readonly unknown[]) {
    if (typeof toolId !== "string") {
      throw new Error("智能体输入格式无效");
    }
    requestedToolIdSet.add(toolId);
  }
  const requestedToolIds = [...requestedToolIdSet];

  if (
    unicodeScalarCount(input.displayName) > 48 ||
    unicodeScalarCount(displayName) === 0
  ) {
    throw new Error("智能体名称需为 1 至 48 个字符");
  }
  if (unicodeScalarCount(input.description) > 160) {
    throw new Error("智能体说明不能超过 160 个字符");
  }
  if (
    unicodeScalarCount(input.instructions) > 12_000 ||
    unicodeScalarCount(instructions) === 0
  ) {
    throw new Error("系统指令需为 1 至 12000 个字符");
  }
  if (
    unicodeScalarCount(input.model.providerId) > 128 ||
    !PROVIDER_ID_PATTERN.test(input.model.providerId) ||
    unicodeScalarCount(input.model.modelId) === 0 ||
    unicodeScalarCount(input.model.modelId) > 256 ||
    input.model.modelId !== input.model.modelId.trim() ||
    unicodeScalarCount(input.model.apiFamily) > 128 ||
    !API_FAMILY_PATTERN.test(input.model.apiFamily) ||
    input.model.providerId !== DEFAULT_MODEL.providerId ||
    input.model.modelId !== DEFAULT_MODEL.modelId ||
    input.model.apiFamily !== DEFAULT_MODEL.apiFamily
  ) {
    throw new Error("模型身份无效或未被当前预览服务广告");
  }
  if (
    input.requestedToolIds.length > 256 ||
    requestedToolIds.length !== input.requestedToolIds.length ||
    requestedToolIds.some(
      (toolId) => unicodeScalarCount(toolId) > 128 || !TOOL_ID_PATTERN.test(toolId),
    )
  ) {
    throw new Error("工具集合格式无效");
  }
  if (requestedToolIds.some((toolId) => !supportedToolIds.has(toolId))) {
    throw new Error("工具集合包含当前服务未广告或预览未支持的能力");
  }

  return {
    displayName,
    description,
    enabled: input.enabled,
    instructions,
    model: {
      providerId: input.model.providerId,
      modelId: input.model.modelId,
      apiFamily: input.model.apiFamily,
    },
    requestedToolIds,
    dangerousActionMode: input.dangerousActionMode,
    behavior: {
      responseStyle: input.behavior.responseStyle,
      planFirst: input.behavior.planFirst,
      reviewChanges: input.behavior.reviewChanges,
    },
  };
}

/**
 * 创建当前后端画像的确定性默认智能体。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function createDefaultProfiles(backend: BackendKind): AgentProfile[] {
  const timestamp = new Date().toISOString();
  return [
    {
      profileId: "coding-agent",
      revision: 1,
      displayName: "编码助手",
      description: "负责仓库实现、测试与变更审阅",
      enabled: true,
      instructions: "先阅读项目约束，再进行范围明确的实现。完成后运行与改动风险相匹配的检查。",
      model: DEFAULT_MODEL,
      requestedToolIds: [],
      dangerousActionMode: "ask",
      behavior: {
        responseStyle: "balanced",
        planFirst: true,
        reviewChanges: true,
      },
      createdAt: timestamp,
      updatedAt: timestamp,
    },
    {
      profileId: "review-agent",
      revision: 1,
      displayName: "只读审阅",
      description: `面向 ${backend === "rust" ? "Rust" : ".NET"} 画像的风险检查`,
      enabled: true,
      instructions: "优先查找行为回归、安全边界与缺失测试，只报告有证据支持的问题。",
      model: DEFAULT_MODEL,
      requestedToolIds: [],
      dangerousActionMode: "deny",
      behavior: {
        responseStyle: "concise",
        planFirst: false,
        reviewChanges: true,
      },
      createdAt: timestamp,
      updatedAt: timestamp,
    },
  ];
}

/**
 * 仅在浏览器内存中模拟 Agent Profile CRUD，不实现或广告生产 RPC。
 *
 * 不变量：配置不会持久化，不读取 secret，不扩大服务端工具能力。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
class FauxAgentProfileService implements AgentProfileService {
  public readonly mode = "faux-preview" as const;
  readonly #profiles = new Map<string, AgentProfile>();
  readonly #supportedToolIds: ReadonlySet<string>;

  /**
   * 以当前 initialize 广告的工具上限建立内存服务。
   *
   * 输入：后端展示画像与服务实际广告的工具 ID。
   * 输出：隔离于其他后端画像的进程内预览服务。
   * 不变量：未知展示工具不会进入允许集合，且不读取网络或凭据。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public constructor(backend: BackendKind, supportedToolIds: readonly string[]) {
    this.#supportedToolIds = new Set(
      supportedToolIds.filter((toolId) => PRESENTED_TOOL_IDS.has(toolId)),
    );
    for (const profile of createDefaultProfiles(backend)) {
      this.#profiles.set(profile.profileId, profile);
    }
  }

  /**
   * 返回按更新时间倒序排列的独立投影。
   *
   * 输出：字段、数组与嵌套对象均已复制的只读快照。
   * 不变量：调用方修改返回值不会改变服务内部状态。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async listProfiles(): Promise<readonly AgentProfile[]> {
    await previewWait(80);
    return [...this.#profiles.values()]
      .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt))
      .map(cloneProfile);
  }

  /**
   * 创建 revision 为 1 的内存投影。
   *
   * 输入：未受信任的用户可编辑配置。
   * 输出：带新 profileId、时间戳和 revision 1 的独立投影。
   * 不变量：工具集合不会超过构造时广告且受支持的子集。
   * 失败：字段格式、枚举、模型身份、工具集合或只读约束无效时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async createProfile(input: AgentProfileInput): Promise<AgentProfile> {
    const normalized = normalizeInput(input, this.#supportedToolIds);
    const timestamp = new Date().toISOString();
    const profile: AgentProfile = {
      ...normalized,
      profileId: crypto.randomUUID(),
      revision: 1,
      createdAt: timestamp,
      updatedAt: timestamp,
    };
    this.#profiles.set(profile.profileId, profile);
    await previewWait(110);
    return cloneProfile(profile);
  }

  /**
   * 仅在 expectedRevision 匹配时更新并递增 revision。
   *
   * 输入：稳定 profileId、调用方已读取的 revision 与完整替换配置。
   * 输出：revision 递增 1 且保留 createdAt 的独立投影。
   * 不变量：不存在或冲突时不覆盖当前状态，工具能力只会收窄。
   * 失败：实体不存在、revision 冲突或输入边界校验失败时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async updateProfile(
    profileId: string,
    expectedRevision: number,
    input: AgentProfileInput,
  ): Promise<AgentProfile> {
    const current = this.#profiles.get(profileId);
    if (!current) {
      throw new Error("智能体不存在或已被删除");
    }
    if (current.revision !== expectedRevision) {
      throw new Error("智能体已在其他位置更新，请重新加载");
    }

    const normalized = normalizeInput(input, this.#supportedToolIds);
    const profile: AgentProfile = {
      ...normalized,
      profileId,
      revision: current.revision + 1,
      createdAt: current.createdAt,
      updatedAt: new Date().toISOString(),
    };
    this.#profiles.set(profileId, profile);
    await previewWait(110);
    return cloneProfile(profile);
  }

  /**
   * 仅在 expectedRevision 匹配时删除投影。
   *
   * 输入：稳定 profileId 与调用方已读取的 revision。
   * 输出：成功时无返回值。
   * 不变量：不存在或冲突时不删除任何实体。
   * 失败：实体不存在或 revision 冲突时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async deleteProfile(profileId: string, expectedRevision: number): Promise<void> {
    const current = this.#profiles.get(profileId);
    if (!current) {
      throw new Error("智能体不存在或已被删除");
    }
    if (current.revision !== expectedRevision) {
      throw new Error("智能体已在其他位置更新，请重新加载");
    }
    this.#profiles.delete(profileId);
    await previewWait(90);
  }
}

/**
 * 创建与后端画像隔离的 Faux Agent Profile 内存服务。
 *
 * 输入：后端展示画像与 initialize 实际广告的工具 ID。
 * 输出：刷新即丢弃且不会产生网络请求的 Draft 服务。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function createFauxAgentProfileService(
  backend: BackendKind,
  supportedToolIds: readonly string[],
): AgentProfileService {
  return new FauxAgentProfileService(backend, supportedToolIds);
}
