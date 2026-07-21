import { invoke } from "@tauri-apps/api/core";
import { z } from "zod";

import type {
  ApprovalDecision,
  ApplicationServiceClient,
  BackendKind,
  InitializeResult,
  ModelDescriptor,
  ProviderConnection,
  ProviderConnectionDeleteResult,
  ProviderConnectionInput,
  ProviderConnectionMutationResult,
  ProviderCredentialStatus,
  ProviderModelDiscoveryResult,
  RunStartInput,
  RunStartResult,
  SessionSnapshot,
  SessionSummary,
} from "@/types/application-service";
import type { AgentProfile, AgentProfileInput } from "@/types/agent-profile";

const PROVIDER_ID_PATTERN = /^[a-z0-9][a-z0-9.-]*$/;
const API_FAMILY_PATTERN = /^[a-z0-9][a-z0-9-]*$/;
const TOOL_ID_PATTERN = /^[a-z][a-z0-9_.-]*$/;
const OPAQUE_ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._:-]*$/;
const SESSION_LIST_PAGE_LIMIT = 128;
const MAX_SESSION_LIST_PAGES = 256;

/**
 * 判断 wire 数组是否不存在重复值，用于执行共同 Schema 的 uniqueItems 约束。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function hasUniqueValues(values: readonly string[]): boolean {
  return new Set(values).size === values.length;
}

/**
 * 校验 Provider baseUrl 只包含 HTTPS origin/path，不携带认证、查询或片段。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function isSafeProviderBaseUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return (
      url.protocol === "https:" &&
      url.username === "" &&
      url.password === "" &&
      url.search === "" &&
      url.hash === ""
    );
  } catch {
    return false;
  }
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

const providerConnectionInputSchema = z
  .object({
    displayName: z.string().min(1).max(64),
    providerId: z.string().max(128).regex(PROVIDER_ID_PATTERN),
    baseUrl: z.string().min(1).max(2_048).refine(isSafeProviderBaseUrl),
    apiFamily: z.literal("openai-completions"),
    modelIds: z.array(z.string().min(1).max(256)).max(512).refine(hasUniqueValues),
    logoAssetId: z.string().min(1).max(128).regex(PROVIDER_ID_PATTERN).nullable(),
    enabled: z.boolean(),
  })
  .strict();

const providerConnectionSchema = providerConnectionInputSchema
  .extend({
    connectionId: z.string().max(128).regex(OPAQUE_ID_PATTERN),
    revision: z.number().int().positive().max(Number.MAX_SAFE_INTEGER),
    credentialConfigured: z.boolean(),
    createdAt: z.iso.datetime({ offset: false }),
    updatedAt: z.iso.datetime({ offset: false }),
  })
  .strict();

const providerConnectionResultSchema = z
  .object({ connection: providerConnectionSchema, restartRequired: z.literal(true) })
  .strict();
const providerConnectionListResultSchema = z
  .object({ connections: z.array(providerConnectionSchema) })
  .strict();
const providerModelDiscoveryResultSchema = z
  .object({
    connection: providerConnectionSchema,
    discoveredCount: z.number().int().min(1).max(512),
    restartRequired: z.literal(true),
  })
  .strict()
  .refine(
    (result) => result.discoveredCount === result.connection.modelIds.length,
    { message: "Provider model discovery count does not match the connection snapshot" },
  );
const providerCredentialStatusSchema = z
  .object({
    providerId: z.string().max(128).regex(PROVIDER_ID_PATTERN),
    credentialConfigured: z.boolean(),
    restartRequired: z.literal(true),
  })
  .strict();
const providerConnectionDeleteResultSchema = z
  .object({ deleted: z.literal(true), restartRequired: z.literal(true) })
  .strict();

const nonNegativeSafeIntegerSchema = z.number().int().min(0).max(Number.MAX_SAFE_INTEGER);
const positiveSafeIntegerSchema = z.number().int().positive().max(Number.MAX_SAFE_INTEGER);
const opaqueIdSchema = z.string().min(1).max(128).regex(OPAQUE_ID_PATTERN);
const utcTimeSchema = z.iso.datetime({ offset: false });
const extensionsSchema = z.record(
  z.string().regex(/^[a-z0-9]+(?:[.-][a-z0-9-]+)+$/),
  z.unknown(),
);
const jsonObjectSchema = z.record(z.string(), z.unknown());
const optionalExtensions = { extensions: extensionsSchema.optional() } as const;

const artifactReferenceSchema = z
  .object({
    artifactId: opaqueIdSchema,
    mediaType: z.string().min(3).max(128).regex(/^[A-Za-z0-9!#$&^_.+-]+\/[A-Za-z0-9!#$&^_.+-]+$/),
    byteLength: nonNegativeSafeIntegerSchema,
    sha256: z.string().regex(/^[a-f0-9]{64}$/),
    displayName: z.string().min(1).max(256).optional(),
    ...optionalExtensions,
  })
  .strict();
const textContentSchema = z
  .object({ type: z.literal("text"), text: z.string(), ...optionalExtensions })
  .strict();
const reasoningContentSchema = z
  .object({
    type: z.literal("reasoning"),
    text: z.string(),
    redacted: z.boolean().optional(),
    signature: z.string().max(1_048_576).optional(),
    ...optionalExtensions,
  })
  .strict();
const imageContentSchema = z
  .object({
    type: z.literal("image_ref"),
    artifact: artifactReferenceSchema,
    alt: z.string().max(4_096).optional(),
    ...optionalExtensions,
  })
  .strict();
const artifactContentSchema = z
  .object({
    type: z.literal("artifact_ref"),
    artifact: artifactReferenceSchema,
    ...optionalExtensions,
  })
  .strict();
const toolCallContentSchema = z
  .object({
    type: z.literal("tool_call"),
    toolCallId: opaqueIdSchema,
    name: z.string().max(128).regex(TOOL_ID_PATTERN),
    arguments: jsonObjectSchema,
    ...optionalExtensions,
  })
  .strict();
const inputContentSchema = z.union([
  textContentSchema,
  imageContentSchema,
  artifactContentSchema,
]);
const assistantContentSchema = z.union([
  textContentSchema,
  reasoningContentSchema,
  imageContentSchema,
  toolCallContentSchema,
]);
const providerSelectionSchema = z
  .object({
    id: z.string().max(128).regex(PROVIDER_ID_PATTERN),
    modelId: z.string().min(1).max(256),
    apiFamily: z.string().max(128).regex(API_FAMILY_PATTERN).optional(),
    ...optionalExtensions,
  })
  .strict();
const moneySchema = z
  .object({
    currency: z.string().regex(/^[A-Z]{3}$/),
    amount: z.string().regex(/^(0|[1-9][0-9]*)(\.[0-9]{1,12})?$/),
  })
  .strict();
const usageSchema = z
  .object({
    inputTokens: nonNegativeSafeIntegerSchema,
    outputTokens: nonNegativeSafeIntegerSchema,
    totalTokens: nonNegativeSafeIntegerSchema,
    cachedInputTokens: nonNegativeSafeIntegerSchema.optional(),
    cacheWriteTokens: nonNegativeSafeIntegerSchema.optional(),
    reasoningTokens: nonNegativeSafeIntegerSchema.optional(),
    cost: moneySchema.optional(),
    ...optionalExtensions,
  })
  .strict();
const errorDetailsSchema = z
  .object({
    kind: z.string().min(1).max(96).regex(/^[a-z][a-z0-9_]*$/),
    httpStatus: z.number().int().min(100).max(599).optional(),
    retryAfterMs: nonNegativeSafeIntegerSchema.optional(),
    field: z.string().max(256).optional(),
    operation: z.string().max(128).optional(),
    providerId: z.string().max(128).optional(),
    modelId: z.string().max(256).optional(),
    apiFamily: z.string().max(128).optional(),
    expectedRevision: positiveSafeIntegerSchema.optional(),
    currentRevision: positiveSafeIntegerSchema.optional(),
    toolName: z.string().max(128).optional(),
    path: z.string().max(4_096).optional(),
    supportedVersions: z
      .array(z.string().max(16).regex(/^[0-9]+\.[0-9]+$/))
      .max(32)
      .refine(hasUniqueValues)
      .optional(),
    limit: nonNegativeSafeIntegerSchema.optional(),
    observed: nonNegativeSafeIntegerSchema.optional(),
    exitCode: z.number().int().nullable().optional(),
    signal: z.string().max(128).optional(),
    resourceId: opaqueIdSchema.optional(),
    ...optionalExtensions,
  })
  .strict();
const portableErrorSchema = z
  .object({
    code: z.number().int().min(-32_768).max(-1),
    message: z.string().min(1).max(4_096),
    retryable: z.boolean(),
    details: errorDetailsSchema,
  })
  .strict();
const finishReasonSchema = z.enum([
  "stop",
  "length",
  "tool_use",
  "error",
  "cancelled",
  "interrupted",
]);
const userMessageSchema = z
  .object({
    messageId: opaqueIdSchema,
    role: z.literal("user"),
    content: z.array(inputContentSchema).min(1).max(128),
    time: utcTimeSchema,
    ...optionalExtensions,
  })
  .strict();
const assistantMessageSchema = z
  .object({
    messageId: opaqueIdSchema,
    role: z.literal("assistant"),
    content: z.array(assistantContentSchema).max(1_024),
    provider: providerSelectionSchema,
    responseId: z.string().min(1).max(512).optional(),
    finishReason: finishReasonSchema,
    usage: usageSchema,
    error: portableErrorSchema.optional(),
    time: utcTimeSchema,
    ...optionalExtensions,
  })
  .strict();
const toolResultMessageSchema = z
  .object({
    messageId: opaqueIdSchema,
    role: z.literal("tool"),
    toolCallId: opaqueIdSchema,
    toolName: z.string().max(128).regex(TOOL_ID_PATTERN),
    content: z.array(inputContentSchema).min(1).max(128),
    isError: z.boolean(),
    time: utcTimeSchema,
    ...optionalExtensions,
  })
  .strict();
const sessionMessageSchema = z.discriminatedUnion("role", [
  userMessageSchema,
  assistantMessageSchema,
  toolResultMessageSchema,
]);

const streamCaptureSchema = z
  .object({
    encoding: z.literal("utf-8-replacement"),
    text: z.string().max(1_048_576),
    capturedBytes: nonNegativeSafeIntegerSchema,
    totalBytes: nonNegativeSafeIntegerSchema,
    omittedBytes: nonNegativeSafeIntegerSchema,
    truncated: z.boolean(),
    artifact: artifactReferenceSchema.optional(),
    ...optionalExtensions,
  })
  .strict();
const executionResultSchema = z
  .object({
    exitCode: z.number().int().min(-2_147_483_648).max(4_294_967_295).nullable(),
    signal: z.string().max(64).regex(/^[A-Z][A-Z0-9_]{0,63}$/).nullable(),
    terminationReason: z.enum(["exit", "signal", "timeout", "cancelled", "output_limit"]),
    durationMs: nonNegativeSafeIntegerSchema,
    containment: z.enum([
      "unix_process_group",
      "unix_process_group_descendant_guard",
      "windows_job_suspended_bind",
      "os_isolation",
      "direct_process",
    ]),
    stdout: streamCaptureSchema,
    stderr: streamCaptureSchema,
    ...optionalExtensions,
  })
  .strict();
const toolResultSchema = z
  .object({
    content: z.array(inputContentSchema).min(1).max(128),
    isError: z.boolean(),
    terminationReason: z
      .enum([
        "exit",
        "signal",
        "timeout",
        "cancelled",
        "output_limit",
        "denied",
        "validation_error",
        "internal_error",
      ])
      .optional(),
    execution: executionResultSchema.optional(),
    error: portableErrorSchema.optional(),
    ...optionalExtensions,
  })
  .strict();
const approvalDecisionSchema = z
  .object({
    choice: z.enum(["allow_once", "allow_session", "deny"]),
    reason: z.string().max(4_096).optional(),
    ...optionalExtensions,
  })
  .strict();
const approvalResourceSchema = z
  .object({
    kind: z.enum(["path", "executable", "origin", "credential", "process", "other"]),
    value: z.string().min(1).max(4_096),
  })
  .strict();
const approvalChoicesSchema = z
  .array(z.enum(["allow_once", "allow_session", "deny"]))
  .min(2)
  .refine(hasUniqueValues)
  .refine((choices) => choices.includes("deny"));
const approvalRequestSchema = z
  .object({
    approvalId: opaqueIdSchema,
    toolCallId: opaqueIdSchema,
    operation: z.string().max(128).regex(TOOL_ID_PATTERN),
    arguments: jsonObjectSchema,
    operationHash: z.string().regex(/^[a-f0-9]{64}$/),
    risk: z.enum(["low", "medium", "high", "critical"]),
    reason: z.string().max(4_096).optional(),
    resources: z.array(approvalResourceSchema).max(128),
    choices: approvalChoicesSchema,
    expiresAt: utcTimeSchema,
    ...optionalExtensions,
  })
  .strict();
const approvalRespondParamsSchema = z
  .object({
    sessionId: opaqueIdSchema,
    runId: opaqueIdSchema,
    approvalId: opaqueIdSchema,
    decision: approvalDecisionSchema,
  })
  .strict();
const approvalRespondResultSchema = z.object({ accepted: z.literal(true) }).strict();
const messageDeltaSchema = z.union([
  z.object({ type: z.literal("text"), text: z.string().min(1) }).strict(),
  z.object({ type: z.literal("reasoning"), text: z.string().min(1) }).strict(),
  z
    .object({
      type: z.literal("tool_arguments"),
      toolCallId: opaqueIdSchema,
      jsonFragment: z.string().min(1),
    })
    .strict(),
]);

const eventDataSchemas = {
  "run.started": z.object({}).strict(),
  "turn.started": z.object({}).strict(),
  "turn.completed": z
    .object({
      finishReason: finishReasonSchema.optional(),
      toolResultCount: nonNegativeSafeIntegerSchema.optional(),
      ...optionalExtensions,
    })
    .strict(),
  "message.started": z
    .object({
      messageId: opaqueIdSchema,
      role: z.enum(["user", "assistant", "tool"]),
      ...optionalExtensions,
    })
    .strict(),
  "message.delta": z
    .object({ messageId: opaqueIdSchema, delta: messageDeltaSchema, ...optionalExtensions })
    .strict(),
  "message.completed": z
    .object({
      messageId: opaqueIdSchema,
      finishReason: finishReasonSchema,
      usage: usageSchema.optional(),
      ...optionalExtensions,
    })
    .strict(),
  "tool.requested": z
    .object({
      toolCallId: opaqueIdSchema,
      name: z.string().max(128).regex(TOOL_ID_PATTERN),
      arguments: jsonObjectSchema,
      ...optionalExtensions,
    })
    .strict(),
  "approval.requested": z
    .object({ approval: approvalRequestSchema, ...optionalExtensions })
    .strict(),
  "approval.resolved": z
    .object({
      approvalId: opaqueIdSchema,
      decision: approvalDecisionSchema,
      resolutionSource: z.enum(["client", "policy", "timeout", "cancellation", "disconnect"]),
      ...optionalExtensions,
    })
    .strict(),
  "tool.started": z
    .object({
      toolCallId: opaqueIdSchema,
      name: z.string().max(128).regex(TOOL_ID_PATTERN),
      ...optionalExtensions,
    })
    .strict(),
  "tool.delta": z
    .object({
      toolCallId: opaqueIdSchema,
      stream: z.enum(["stdout", "stderr", "status"]),
      text: z.string(),
      ...optionalExtensions,
    })
    .strict(),
  "tool.completed": z
    .object({ toolCallId: opaqueIdSchema, result: toolResultSchema, ...optionalExtensions })
    .strict(),
  "retry.scheduled": z
    .object({
      attempt: z.number().int().min(2).max(100),
      delayMs: nonNegativeSafeIntegerSchema,
      reason: portableErrorSchema,
      ...optionalExtensions,
    })
    .strict(),
  "context.compacted": z
    .object({
      recordId: opaqueIdSchema,
      tokensBefore: nonNegativeSafeIntegerSchema,
      tokensAfter: nonNegativeSafeIntegerSchema.optional(),
      ...optionalExtensions,
    })
    .strict(),
  "run.completed": z
    .object({ status: z.literal("completed"), usage: usageSchema, ...optionalExtensions })
    .strict(),
  "run.failed": z
    .object({
      status: z.literal("failed"),
      error: portableErrorSchema,
      usage: usageSchema.optional(),
      ...optionalExtensions,
    })
    .strict(),
  "run.cancelled": z
    .object({
      status: z.literal("cancelled"),
      reason: z.string().max(4_096).optional(),
      usage: usageSchema.optional(),
      ...optionalExtensions,
    })
    .strict(),
  "run.interrupted": z
    .object({
      status: z.literal("interrupted"),
      error: portableErrorSchema,
      usage: usageSchema.optional(),
      ...optionalExtensions,
    })
    .strict(),
} as const;
const sessionEventTypeSchema = z.enum([
  "run.started",
  "turn.started",
  "turn.completed",
  "message.started",
  "message.delta",
  "message.completed",
  "tool.requested",
  "approval.requested",
  "approval.resolved",
  "tool.started",
  "tool.delta",
  "tool.completed",
  "retry.scheduled",
  "context.compacted",
  "run.completed",
  "run.failed",
  "run.cancelled",
  "run.interrupted",
]);
const runLevelEventTypes = new Set([
  "run.started",
  "run.completed",
  "run.failed",
  "run.cancelled",
  "run.interrupted",
]);
const sessionReplayEventSchema = z
  .object({
    sessionId: opaqueIdSchema,
    runId: opaqueIdSchema,
    turnId: opaqueIdSchema.optional(),
    seq: positiveSafeIntegerSchema,
    time: utcTimeSchema,
    type: sessionEventTypeSchema,
    data: jsonObjectSchema,
    ...optionalExtensions,
  })
  .strict()
  .superRefine((event, context) => {
    const dataResult = eventDataSchemas[event.type].safeParse(event.data);
    if (!dataResult.success) {
      context.addIssue({ code: "custom", path: ["data"], message: "事件 data 不符合类型 Schema" });
    }
    const runLevel = runLevelEventTypes.has(event.type);
    if ((runLevel && event.turnId !== undefined) || (!runLevel && event.turnId === undefined)) {
      context.addIssue({ code: "custom", path: ["turnId"], message: "事件 turnId 与类型不一致" });
    }
  });
const sessionGetParamsSchema = z
  .object({
    sessionId: opaqueIdSchema,
    afterSeq: nonNegativeSafeIntegerSchema.optional(),
  })
  .strict();
const sessionGetResultSchema = z
  .object({
    sessionId: opaqueIdSchema,
    latestSeq: nonNegativeSafeIntegerSchema,
    activeRunId: opaqueIdSchema.nullable(),
    messages: z.array(sessionMessageSchema),
    events: z.array(sessionReplayEventSchema).optional().default([]),
    selectedHeadRecordId: opaqueIdSchema.optional(),
    compactionRecordId: opaqueIdSchema.optional(),
    ...optionalExtensions,
  })
  .strict();

const sessionSummarySchema = z
  .object({
    sessionId: z.string().max(128).regex(OPAQUE_ID_PATTERN),
    title: z.string().min(1).max(96),
    project: z.string().min(1).max(128),
    updatedAt: z.iso.datetime({ offset: false }),
    archived: z.boolean(),
    status: z.enum(["active", "approval"]).optional(),
  })
  .strict();
const sessionResultSchema = z.object({ session: sessionSummarySchema }).strict();
const sessionListCursorSchema = z.string().min(1).max(64).regex(/^[A-Za-z0-9._:-]+$/);
const sessionListParamsSchema = z
  .object({
    cursor: sessionListCursorSchema.optional(),
    limit: z.number().int().min(1).max(SESSION_LIST_PAGE_LIMIT).optional(),
  })
  .strict();
const sessionListResultSchema = z
  .object({
    sessions: z.array(sessionSummarySchema).max(SESSION_LIST_PAGE_LIMIT),
    nextCursor: sessionListCursorSchema.nullable(),
    hasMore: z.boolean(),
  })
  .strict()
  .superRefine((page, context) => {
    if (page.hasMore === (page.nextCursor === null)) {
      context.addIssue({
        code: "custom",
        path: ["nextCursor"],
        message: "nextCursor 与 hasMore 不一致",
      });
    }
    if (page.hasMore && page.sessions.length === 0) {
      context.addIssue({
        code: "custom",
        path: ["sessions"],
        message: "存在下一页时当前页不能为空",
      });
    }
  });

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
   * 将用户选择绑定到服务端签发的精确审批请求，并等待 durable 接受回执。
   *
   * 输入：同一 Session/run 的 opaque approval ID 与受支持决定。
   * 输出：服务确认 accepted=true 后无正文。
   * 不变量：客户端不发送 resolutionSource，也不修改审批操作或 operation hash。
   * 失败：输入 Schema、过期/重复审批、服务冲突或传输失败时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async respondToApproval(
    sessionId: string,
    runId: string,
    approvalId: string,
    decision: ApprovalDecision,
  ): Promise<void> {
    const params = approvalRespondParamsSchema.parse({
      sessionId,
      runId,
      approvalId,
      decision,
    });
    approvalRespondResultSchema.parse(
      await this.#request("approval/respond", params),
    );
  }

  /**
   * 从 application service 读取严格脱敏的 Provider 连接列表。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async listProviderConnections(): Promise<readonly ProviderConnection[]> {
    return providerConnectionListResultSchema.parse(
      await this.#request("providerConnections/list", {}),
    ).connections;
  }

  /**
   * 创建不含凭据的 Provider 连接并验证返回投影。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async createProviderConnection(
    input: ProviderConnectionInput,
  ): Promise<ProviderConnectionMutationResult> {
    const connection = providerConnectionInputSchema.parse(input);
    return providerConnectionResultSchema.parse(
      await this.#request("providerConnections/create", { connection }),
    );
  }

  /**
   * 以 revision CAS 更新 Provider 非敏感配置。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async updateProviderConnection(
    connectionId: string,
    expectedRevision: number,
    input: ProviderConnectionInput,
  ): Promise<ProviderConnectionMutationResult> {
    const connection = providerConnectionInputSchema.parse(input);
    return providerConnectionResultSchema.parse(
      await this.#request("providerConnections/update", {
        connectionId,
        expectedRevision,
        connection,
      }),
    );
  }

  /**
   * 通过受限 application-service RPC 显式发现并持久化远端模型目录。
   *
   * 输入：服务签发的连接 ID 与最近 revision；输出：严格脱敏的更新连接。
   * 不变量：请求不携带 credential、endpoint 或模型猜测，计数必须与返回 allowlist 一致。
   * 失败：输入、远端发现、CAS、传输或响应 Schema 无效时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async discoverProviderModels(
    connectionId: string,
    expectedRevision: number,
  ): Promise<ProviderModelDiscoveryResult> {
    return providerModelDiscoveryResultSchema.parse(
      await this.#request("providerConnections/discoverModels", {
        connectionId,
        expectedRevision,
      }),
    );
  }

  /**
   * 删除 Provider 连接并要求服务确认删除结果。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async deleteProviderConnection(
    connectionId: string,
    expectedRevision: number,
  ): Promise<ProviderConnectionDeleteResult> {
    return providerConnectionDeleteResultSchema.parse(
      await this.#request("providerConnections/delete", {
        connectionId,
        expectedRevision,
      }),
    );
  }

  /**
   * 将 Provider 凭据直接交给 host 最终使用边界，并只接受脱敏状态。
   *
   * 输入：连接标识与非空 secret。
   * 输出：不含 secret 的配置状态。
   * 不变量：响应出现任意未知字段时拒绝，防止凭据进入 React。
   * 失败：输入、host、CredentialStore 或响应校验失败时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async setProviderCredential(
    providerId: string,
    credential: string,
  ): Promise<ProviderCredentialStatus> {
    const validatedCredential = z.string().min(1).max(16_384).parse(credential);
    return providerCredentialStatusSchema.parse(
      await invoke<unknown>("provider_credential_set", {
        backend: this.#backend,
        providerId,
        credential: validatedCredential,
      }),
    );
  }

  /**
   * 移除 Provider 凭据并只接受 configured=false 的脱敏状态。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async removeProviderCredential(
    providerId: string,
  ): Promise<ProviderCredentialStatus> {
    const credential = providerCredentialStatusSchema.parse(
      await invoke<unknown>("provider_credential_remove", {
        backend: this.#backend,
        providerId,
      }),
    );
    if (credential.credentialConfigured) {
      throw new Error("Provider credential removal was not confirmed");
    }
    return credential;
  }

  /**
   * 读取 selected branch 的完整 portable 消息，并严格验证可选事件增量。
   *
   * 输入：稳定 Session ID 与可选非负 durable event 下界。
   * 输出：完整消息快照、active run、最新事件序号和严格递增的事件增量。
   * 不变量：响应 Session ID 必须匹配请求，events 必须同 Session 且满足 afterSeq。
   * 失败：输入、传输、消息/事件 Schema 或序号语义不满足时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async getSession(sessionId: string, afterSeq?: number): Promise<SessionSnapshot> {
    const params = sessionGetParamsSchema.parse({
      sessionId,
      ...(afterSeq === undefined ? {} : { afterSeq }),
    });
    const snapshot = sessionGetResultSchema.parse(
      await this.#request("session/get", params),
    );
    if (snapshot.sessionId !== params.sessionId) {
      throw new Error("Session snapshot identity does not match the request");
    }

    let previousSequence = params.afterSeq ?? 0;
    for (const event of snapshot.events) {
      if (
        event.sessionId !== params.sessionId ||
        event.seq <= previousSequence ||
        event.seq > snapshot.latestSeq
      ) {
        throw new Error("Session replay events violate the requested sequence boundary");
      }
      previousSequence = event.seq;
    }
    return snapshot;
  }

  /**
   * 分页读取不含 transcript 与宿主路径的完整 Session 摘要列表。
   *
   * 输入：无；客户端使用协议允许的最大页大小遍历 opaque cursor。
   * 输出：按首次出现顺序以 Session ID 去重的完整脱敏摘要数组。
   * 不变量：每页严格校验，cursor 不得重复，非终页必须产生进度，且读取页数有固定上界。
   * 失败：响应字段、分页状态、cursor 或页数不满足共同契约时拒绝且不返回部分列表。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
  */
  public async listSessions(): Promise<readonly SessionSummary[]> {
    const sessionsById = new Map<string, SessionSummary>();
    const seenCursors = new Set<string>();
    let cursor: string | undefined;

    for (let pageIndex = 0; pageIndex < MAX_SESSION_LIST_PAGES; pageIndex += 1) {
      const params = sessionListParamsSchema.parse({
        limit: SESSION_LIST_PAGE_LIMIT,
        ...(cursor === undefined ? {} : { cursor }),
      });
      const page = sessionListResultSchema.parse(
        await this.#request("session/list", params),
      );
      let addedSessionCount = 0;
      for (const session of page.sessions) {
        if (!sessionsById.has(session.sessionId)) {
          sessionsById.set(session.sessionId, session);
          addedSessionCount += 1;
        }
      }
      if (!page.hasMore) {
        return [...sessionsById.values()];
      }

      const nextCursor = page.nextCursor;
      if (nextCursor === null || seenCursors.has(nextCursor)) {
        throw new Error("Session list pagination cursor did not advance");
      }
      if (addedSessionCount === 0) {
        throw new Error("Session list pagination did not add any sessions");
      }
      seenCursors.add(nextCursor);
      cursor = nextCursor;
    }

    throw new Error("Session list pagination exceeded the client page limit");
  }

  /**
   * 归档指定 Session 并验证返回状态。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async archiveSession(sessionId: string): Promise<SessionSummary> {
    const session = sessionResultSchema.parse(
      await this.#request("session/archive", { sessionId }),
    ).session;
    if (!session.archived) {
      throw new Error("Session archive was not confirmed");
    }
    return session;
  }

  /**
   * 恢复指定 Session 并验证返回状态。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async restoreSession(sessionId: string): Promise<SessionSummary> {
    const session = sessionResultSchema.parse(
      await this.#request("session/restore", { sessionId }),
    ).session;
    if (session.archived) {
      throw new Error("Session restore was not confirmed");
    }
    return session;
  }

  /**
   * 永久删除指定 Session 并要求服务确认结果。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async deleteSession(sessionId: string): Promise<void> {
    deleteResultSchema.parse(await this.#request("session/delete", { sessionId }));
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
