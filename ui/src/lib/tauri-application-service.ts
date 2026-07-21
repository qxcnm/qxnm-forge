import { invoke } from "@tauri-apps/api/core";
import { z } from "zod";

import type {
  ApplicationServiceClient,
  BackendKind,
  InitializeResult,
  ModelDescriptor,
  RunStartInput,
  RunStartResult,
} from "@/types/application-service";
import type { AgentProfile, AgentProfileInput } from "@/types/agent-profile";

const PROVIDER_ID_PATTERN = /^[a-z0-9][a-z0-9.-]*$/;
const API_FAMILY_PATTERN = /^[a-z0-9][a-z0-9-]*$/;
const TOOL_ID_PATTERN = /^[a-z][a-z0-9_.-]*$/;
const OPAQUE_ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._:-]*$/;

/**
 * 判断 wire 数组是否不存在重复值，用于执行共同 Schema 的 uniqueItems 约束。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function hasUniqueValues(values: readonly string[]): boolean {
  return new Set(values).size === values.length;
}

const modelSelectionSchema = z
  .object({
    providerId: z.string().max(128).regex(PROVIDER_ID_PATTERN),
    modelId: z.string().min(1).max(256),
    apiFamily: z.string().max(128).regex(API_FAMILY_PATTERN),
  })
  .strict();

const toolIdsSchema = z
  .array(z.string().max(128).regex(TOOL_ID_PATTERN))
  .max(256)
  .refine(hasUniqueValues);

const behaviorSchema = z
  .object({
    responseStyle: z.enum(["concise", "balanced", "detailed"]),
    planFirst: z.boolean(),
    reviewChanges: z.boolean(),
  })
  .strict();

const profileInputSchema = z
  .object({
    displayName: z.string().min(1).max(48),
    description: z.string().max(160),
    enabled: z.boolean(),
    instructions: z.string().min(1).max(12_000),
    model: modelSelectionSchema,
    requestedToolIds: toolIdsSchema,
    dangerousActionMode: z.enum(["ask", "deny"]),
    behavior: behaviorSchema,
  })
  .strict();

const profileSchema = profileInputSchema
  .extend({
    profileId: z.string().max(128).regex(OPAQUE_ID_PATTERN),
    revision: z.number().int().positive().max(Number.MAX_SAFE_INTEGER),
    createdAt: z.iso.datetime({ offset: false }),
    updatedAt: z.iso.datetime({ offset: false }),
  })
  .strict();

const initializeSchema = z.object({
  implementation: z.object({
    name: z.string().min(1).max(128),
    version: z.string().min(1).max(64),
    language: z.enum(["rust", "dotnet"]),
  }),
  capabilities: z.object({
    methods: z.array(z.string()),
    eventTypes: z.array(z.string()),
    tools: z.array(z.string()),
  }),
});

const modelsListSchema = z.object({
  models: z.array(
    z.object({
      providerId: z.string().min(1).max(128),
      modelId: z.string().min(1).max(256),
      apiFamily: z.string().min(1).max(128),
      displayName: z.string().min(1).max(256),
      capabilities: z.object({
        reasoning: z.boolean(),
        tools: z.boolean(),
      }),
    }),
  ),
});

const profileResultSchema = z.object({ profile: profileSchema }).strict();
const profileListResultSchema = z
  .object({ profiles: z.array(profileSchema) })
  .strict();
const runStartResultSchema = z.object({ runId: z.string().min(1).max(128) }).strict();
const deleteResultSchema = z.object({ deleted: z.literal(true) }).strict();

/** 通过 Tauri host 的受限 NDJSON bridge 调用本地 application service。 */
export class TauriApplicationServiceClient implements ApplicationServiceClient {
  public readonly mode = "application-service" as const;
  readonly #backend: BackendKind;

  /**
   * 绑定用户选择的独立 Rust 或 .NET daemon。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public constructor(backend: BackendKind) {
    this.#backend = backend;
  }

  /**
   * 让 host 启动并协商 daemon，随后验证前端需要的 capability 投影。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async initialize(): Promise<InitializeResult> {
    const result = initializeSchema.parse(
      await invoke<unknown>("application_service_initialize", {
        backend: this.#backend,
      }),
    );
    return result;
  }

  /**
   * 从 daemon 读取完整 Provider route identity，并转换为界面模型投影。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async listModels(): Promise<readonly ModelDescriptor[]> {
    const result = modelsListSchema.parse(await this.#request("models/list", {}));
    return result.models.map((model) => ({
      providerId: model.providerId,
      modelId: model.modelId,
      apiFamily: model.apiFamily,
      displayName: model.displayName,
      supportsReasoning: model.capabilities.reasoning,
      supportsTools: model.capabilities.tools,
    }));
  }

  /**
   * 读取 application.db 中的智能体投影。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async listProfiles(): Promise<readonly AgentProfile[]> {
    return profileListResultSchema.parse(
      await this.#request("agentProfiles/list", {}),
    ).profiles;
  }

  /**
   * 通过严格 profile DTO 创建持久化智能体。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async createProfile(input: AgentProfileInput): Promise<AgentProfile> {
    const profile = profileInputSchema.parse(input);
    return profileResultSchema.parse(
      await this.#request("agentProfiles/create", { profile }),
    ).profile;
  }

  /**
   * 以 revision CAS 更新持久化智能体。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async updateProfile(
    profileId: string,
    expectedRevision: number,
    input: AgentProfileInput,
  ): Promise<AgentProfile> {
    const profile = profileInputSchema.parse(input);
    return profileResultSchema.parse(
      await this.#request("agentProfiles/update", {
        profileId,
        expectedRevision,
        profile,
      }),
    ).profile;
  }

  /**
   * 以 revision CAS 删除持久化智能体。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async deleteProfile(
    profileId: string,
    expectedRevision: number,
  ): Promise<void> {
    deleteResultSchema.parse(
      await this.#request("agentProfiles/delete", { profileId, expectedRevision }),
    );
  }

  /**
   * 启动一次可选绑定精确 Agent Profile revision 的运行。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async startRun(input: RunStartInput): Promise<RunStartResult> {
    const prompt = input.prompt.trim();
    if (prompt.length === 0) {
      throw new Error("prompt must not be empty");
    }
    const result = runStartResultSchema.parse(
      await this.#request("run/start", {
        sessionId: input.sessionId,
        input: { role: "user", content: [{ type: "text", text: prompt }] },
        provider: {
          id: input.model.providerId,
          modelId: input.model.modelId,
          apiFamily: input.model.apiFamily,
        },
        ...(input.agentProfile ? { agentProfile: input.agentProfile } : {}),
      }),
    );
    return result;
  }

  /**
   * 请求取消当前 Session 中的精确 run。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async cancelRun(sessionId: string, runId: string): Promise<void> {
    await this.#request("run/cancel", { sessionId, runId });
  }

  /**
   * 调用 host allowlist 内的方法；WebView 不能传入可执行文件、参数或 shell。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  async #request(method: string, params: Record<string, unknown>): Promise<unknown> {
    return invoke<unknown>("application_service_request", {
      backend: this.#backend,
      method,
      params,
    });
  }
}
