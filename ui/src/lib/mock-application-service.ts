import type {
  ApprovalDecision,
  ApplicationServiceClient,
  BackendKind,
  InitializeResult,
  ModelDescriptor,
  ProviderCatalogEntry,
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
import {
  getApprovalIdentityKey,
  projectPendingApprovals,
} from "@/features/approvals/pending-approvals";
import { createFauxAgentProfileService } from "@/lib/faux-agent-profile-service";
import { getModelRouteKey } from "@/lib/model-route";
import type {
  AgentProfile,
  AgentProfileInput,
  AgentProfileService,
} from "@/types/agent-profile";
import { TauriApplicationServiceClient } from "@/lib/tauri-application-service";

const SHARED_METHODS = [
  "initialize",
  "models/list",
  "run/start",
  "run/cancel",
  "approval/respond",
  "session/get",
  "session/list",
  "session/archive",
  "session/restore",
  "session/delete",
  "agentProfiles/list",
  "agentProfiles/create",
  "agentProfiles/update",
  "agentProfiles/delete",
  "providerCatalog/list",
  "providerConnections/list",
  "providerConnections/create",
  "providerConnections/update",
  "providerConnections/delete",
  "providerCredentials/set",
  "providerCredentials/remove",
] as const;

const FAUX_TOOL_IDS = [
  "file.read",
  "search.text",
  "file.write",
  "file.edit",
  "process.exec",
  "shell.exec",
] as const;

const FAUX_MODEL_DESCRIPTOR: ModelDescriptor = {
  providerId: "faux",
  modelId: "faux-v1",
  apiFamily: "faux",
  displayName: "Faux v1",
  supportsReasoning: false,
  supportsTools: true,
};

const PREVIEW_PROVIDER_CATALOG: readonly ProviderCatalogEntry[] = [
  ["ant-ling", "Ant Ling", "https://api.ant-ling.com/v1"],
  ["cerebras", "Cerebras", "https://api.cerebras.ai/v1"],
  ["deepseek", "DeepSeek", "https://api.deepseek.com"],
  ["fireworks", "Fireworks AI", "https://api.fireworks.ai/inference/v1"],
  ["groq", "Groq", "https://api.groq.com/openai/v1"],
  ["huggingface", "Hugging Face", "https://router.huggingface.co/v1"],
  ["moonshotai", "Moonshot AI", "https://api.moonshot.ai/v1"],
  ["moonshotai-cn", "Moonshot AI (China)", "https://api.moonshot.cn/v1"],
  ["nvidia", "NVIDIA NIM", "https://integrate.api.nvidia.com/v1"],
  ["opencode", "OpenCode", "https://opencode.ai/zen/v1"],
  ["opencode-go", "OpenCode Go", "https://opencode.ai/zen/go/v1"],
  ["openrouter", "OpenRouter", "https://openrouter.ai/api/v1"],
  ["together", "Together AI", "https://api.together.ai/v1"],
  ["xai", "xAI", "https://api.x.ai/v1"],
  ["xiaomi", "Xiaomi MiMo", "https://api.xiaomimimo.com/v1"],
  ["xiaomi-token-plan-ams", "Xiaomi MiMo Token Plan (Amsterdam)", "https://token-plan-ams.xiaomimimo.com/v1"],
  ["xiaomi-token-plan-cn", "Xiaomi MiMo Token Plan (China)", "https://token-plan-cn.xiaomimimo.com/v1"],
  ["xiaomi-token-plan-sgp", "Xiaomi MiMo Token Plan (Singapore)", "https://token-plan-sgp.xiaomimimo.com/v1"],
  ["zai", "Z.AI", "https://api.z.ai/api/coding/paas/v4"],
  ["zai-coding-cn", "Z.AI Coding (China)", "https://open.bigmodel.cn/api/coding/paas/v4"],
].map(([providerId, displayName, defaultBaseUrl]) => ({
  templateId: providerId,
  displayName,
  suggestedProviderId: `custom-${providerId}`,
  apiFamily: "openai-completions" as const,
  defaultBaseUrl,
  modelDiscovery: "openai-models" as const,
  logoAssetId: null,
}));

const DEFAULT_SESSIONS: readonly SessionSummary[] = [
  {
    sessionId: "desktop-shell",
    title: "实现跨平台桌面端",
    project: "AI-Code",
    updatedAt: "2026-07-21T08:00:00Z",
    archived: false,
    status: "active",
  },
  {
    sessionId: "backend-contract",
    title: "统一后端能力协议",
    project: "AI-Code",
    updatedAt: "2026-07-21T07:48:00Z",
    archived: false,
  },
  {
    sessionId: "approval-flow",
    title: "检查工具审批流程",
    project: "QXNM Forge",
    updatedAt: "2026-07-21T07:00:00Z",
    archived: false,
    status: "approval",
  },
  {
    sessionId: "mobile-layout",
    title: "适配移动端工作区",
    project: "QXNM Forge",
    updatedAt: "2026-07-20T08:00:00Z",
    archived: false,
  },
];

interface MockServiceState {
  readonly providerConnections: Map<string, ProviderConnection>;
  readonly sessions: Map<string, SessionSummary>;
  readonly sessionSnapshots: Map<string, SessionSnapshot>;
}

/** desktop-shell faux 会话的脱敏 portable 消息投影。 */
const DEFAULT_DESKTOP_MESSAGES: SessionSnapshot["messages"] = [
  {
    messageId: "preview-user-1",
    role: "user",
    content: [{ type: "text", text: "检查桌面端打包与响应式工作台" }],
    time: "2026-07-21T07:59:58Z",
  },
  {
    messageId: "preview-assistant-1",
    role: "assistant",
    content: [{ type: "text", text: "已完成基础桌面壳与 application service 边界检查。" }],
    provider: { id: "faux", modelId: "faux-v1", apiFamily: "faux" },
    finishReason: "stop",
    usage: { inputTokens: 8, outputTokens: 12, totalTokens: 20 },
    time: "2026-07-21T08:00:00Z",
  },
];

/** approval-flow faux 会话中等待用户确认的结构化写入请求。 */
const DEFAULT_APPROVAL_SNAPSHOT: SessionSnapshot = {
  sessionId: "approval-flow",
  latestSeq: 1,
  activeRunId: "preview-run-approval",
  messages: [
    {
      messageId: "preview-approval-user",
      role: "user",
      content: [{ type: "text", text: "在工作区写入审批演示文件" }],
      time: "2026-07-21T06:59:58Z",
    },
    {
      messageId: "preview-approval-assistant",
      role: "assistant",
      content: [
        {
          type: "tool_call",
          toolCallId: "preview-tool-call-approval",
          name: "file.write",
          arguments: { path: "approval-preview.txt", content: "preview" },
        },
      ],
      provider: { id: "faux", modelId: "faux-v1", apiFamily: "faux" },
      finishReason: "tool_use",
      usage: { inputTokens: 8, outputTokens: 6, totalTokens: 14 },
      time: "2026-07-21T07:00:00Z",
    },
  ],
  events: [
    {
      sessionId: "approval-flow",
      runId: "preview-run-approval",
      turnId: "preview-turn-approval",
      seq: 1,
      time: "2026-07-21T07:00:01Z",
      type: "approval.requested",
      data: {
        approval: {
          approvalId: "preview-approval-1",
          toolCallId: "preview-tool-call-approval",
          operation: "file.write",
          arguments: { path: "approval-preview.txt", content: "preview" },
          operationHash: "a".repeat(64),
          risk: "medium",
          reason: "此写入会修改工作区内容，需要明确批准。",
          resources: [{ kind: "path", value: "approval-preview.txt" }],
          choices: ["allow_once", "deny"],
          expiresAt: "2099-07-21T08:00:00Z",
        },
      },
    },
  ],
  selectedHeadRecordId: "preview-approval-record-1",
};

/**
 * 为默认 Session 建立不含事件、凭据或宿主路径的确定性消息快照。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function createDefaultSessionSnapshot(sessionId: string): SessionSnapshot {
  if (sessionId === DEFAULT_APPROVAL_SNAPSHOT.sessionId) {
    return structuredClone(DEFAULT_APPROVAL_SNAPSHOT);
  }
  const messages = sessionId === "desktop-shell" ? DEFAULT_DESKTOP_MESSAGES : [];
  return {
    sessionId,
    latestSeq: 0,
    activeRunId: null,
    messages,
    events: [],
    ...(messages.length > 0 ? { selectedHeadRecordId: "preview-record-1" } : {}),
  };
}

/**
 * 创建一个刷新即丢弃的浏览器服务状态，不保存任何凭据正文。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function createMockServiceState(): MockServiceState {
  return {
    providerConnections: new Map(),
    sessions: new Map(DEFAULT_SESSIONS.map((session) => [session.sessionId, session])),
    sessionSnapshots: new Map(
      DEFAULT_SESSIONS.map((session) => [
        session.sessionId,
        createDefaultSessionSnapshot(session.sessionId),
      ]),
    ),
  };
}

const MOCK_SERVICE_STATES: Record<BackendKind, MockServiceState> = {
  rust: createMockServiceState(),
  dotnet: createMockServiceState(),
};

const RESERVED_PROVIDER_IDS = new Set([
  "amazon-bedrock",
  "ant-ling",
  "anthropic",
  "azure-openai-responses",
  "cerebras",
  "cloudflare-ai-gateway",
  "cloudflare-workers-ai",
  "deepseek",
  "faux",
  "fireworks",
  "github-copilot",
  "google",
  "google-vertex",
  "groq",
  "huggingface",
  "kimi-coding",
  "minimax",
  "minimax-cn",
  "mistral",
  "moonshotai",
  "moonshotai-cn",
  "nvidia",
  "openai",
  "openai-codex",
  "opencode",
  "opencode-go",
  "openrouter",
  "together",
  "vercel-ai-gateway",
  "xai",
  "xiaomi",
  "xiaomi-token-plan-ams",
  "xiaomi-token-plan-cn",
  "xiaomi-token-plan-sgp",
  "zai",
  "zai-coding-cn",
]);

/**
 * 将浏览器 faux application service 恢复到确定性初始状态，供隔离测试使用。
 *
 * 不变量：重置后 Provider 列表为空，Session 仅含脱敏 fixture，且不存在凭据正文。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function resetMockApplicationServiceState(): void {
  for (const state of Object.values(MOCK_SERVICE_STATES)) {
    state.providerConnections.clear();
    state.sessions.clear();
    state.sessionSnapshots.clear();
    for (const session of DEFAULT_SESSIONS) {
      state.sessions.set(session.sessionId, session);
      state.sessionSnapshots.set(
        session.sessionId,
        createDefaultSessionSnapshot(session.sessionId),
      );
    }
  }
}

/**
 * 复制 Provider 脱敏投影，隔离调用方数组修改。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function cloneProviderConnection(connection: ProviderConnection): ProviderConnection {
  return { ...connection, modelIds: [...connection.modelIds] };
}

/**
 * 从浏览器预览状态构造与真实 daemon 规则一致的可用模型快照。
 *
 * 输入：当前后端隔离的 Provider 连接状态。
 * 输出：faux 路由以及已启用、已配置凭据的自定义 Provider 三元路由。
 * 不变量：不读取凭据正文，不向未配置连接广告模型，并按完整身份稳定排序。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function createMockModelSnapshot(state: MockServiceState): readonly ModelDescriptor[] {
  const models: ModelDescriptor[] = [{ ...FAUX_MODEL_DESCRIPTOR }];
  for (const connection of state.providerConnections.values()) {
    if (!connection.enabled || !connection.credentialConfigured) {
      continue;
    }
    for (const modelId of connection.modelIds) {
      models.push({
        providerId: connection.providerId,
        modelId,
        apiFamily: connection.apiFamily,
        displayName: modelId,
        supportsReasoning: false,
        supportsTools: false,
      });
    }
  }
  return models.sort((left, right) => {
    const leftKey = getModelRouteKey(left);
    const rightKey = getModelRouteKey(right);
    return leftKey < rightKey ? -1 : leftKey > rightKey ? 1 : 0;
  });
}

/**
 * 深复制 faux Session 快照，阻止调用方修改共享的消息与事件对象。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function cloneSessionSnapshot(snapshot: SessionSnapshot): SessionSnapshot {
  return structuredClone(snapshot);
}

/**
 * 规范化 Provider 表单输入并拒绝凭据型未知字段。
 *
 * 输入：来自 UI 的完整非敏感连接配置。
 * 输出：去除首尾空白、模型去重后的配置。
 * 失败：字段缺失、URL 非 HTTPS/loopback HTTP 或模型为空时拒绝。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function normalizeProviderInput(input: ProviderConnectionInput): ProviderConnectionInput {
  const displayName = input.displayName.trim();
  const providerId = input.providerId.trim();
  const apiFamily = input.apiFamily;
  const modelIds = [...new Set(input.modelIds.map((modelId) => modelId.trim()).filter(Boolean))];
  let baseUrl: URL;
  try {
    baseUrl = new URL(input.baseUrl.trim());
  } catch {
    throw new Error("Provider URL 无效");
  }
  if (
    !displayName ||
    [...displayName].length > 64 ||
    !/^[a-z0-9][a-z0-9.-]*$/.test(providerId) ||
    RESERVED_PROVIDER_IDS.has(providerId) ||
    providerId.startsWith("relay-") ||
    apiFamily !== "openai-completions" ||
    modelIds.length > 512 ||
    baseUrl.protocol !== "https:" ||
    baseUrl.username !== "" ||
    baseUrl.password !== "" ||
    baseUrl.search !== "" ||
    baseUrl.hash !== "" ||
    (input.logoAssetId !== null &&
      input.logoAssetId.trim() !== "" &&
      !/^[a-z0-9][a-z0-9.-]*$/.test(input.logoAssetId.trim()))
  ) {
    throw new Error("Provider 配置无效");
  }
  return {
    displayName,
    providerId,
    baseUrl: baseUrl.toString().replace(/\/$/, ""),
    apiFamily,
    modelIds,
    logoAssetId: input.logoAssetId?.trim() || null,
    enabled: input.enabled,
  };
}

/**
 * 产生确定性的短延迟，供零网络预览模拟 application service 边界。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function wait(milliseconds: number): Promise<void> {
  return new Promise((resolve) => window.setTimeout(resolve, milliseconds));
}

/**
 * 默认只使用 faux 数据的本地预览客户端，不会调用任何付费 Provider。
 *
 * 不变量：返回的数据不含凭据、宿主路径或 Pro 权益信息。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
class MockApplicationServiceClient implements ApplicationServiceClient {
  public readonly mode = "faux-preview" as const;
  readonly #backend: BackendKind;
  readonly #profiles: AgentProfileService;
  readonly #state: MockServiceState;

  /**
   * 创建指定实现画像的预览客户端。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public constructor(backend: BackendKind) {
    this.#backend = backend;
    this.#state = MOCK_SERVICE_STATES[backend];
    this.#profiles = createFauxAgentProfileService(
      backend,
      FAUX_TOOL_IDS,
      () => createMockModelSnapshot(this.#state),
    );
  }

  /**
   * 列出当前浏览器预览生命周期内的智能体。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public listProfiles(): Promise<readonly AgentProfile[]> {
    return this.#profiles.listProfiles();
  }

  /**
   * 在 faux application service 投影中创建智能体。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public createProfile(input: AgentProfileInput): Promise<AgentProfile> {
    return this.#profiles.createProfile(input);
  }

  /**
   * 在 faux application service 投影中按 revision 更新智能体。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public updateProfile(
    profileId: string,
    expectedRevision: number,
    input: AgentProfileInput,
  ): Promise<AgentProfile> {
    return this.#profiles.updateProfile(profileId, expectedRevision, input);
  }

  /**
   * 在 faux application service 投影中按 revision 删除智能体。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public deleteProfile(profileId: string, expectedRevision: number): Promise<void> {
    return this.#profiles.deleteProfile(profileId, expectedRevision);
  }

  /**
   * 返回符合所选实现差异的初始化能力。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async initialize(): Promise<InitializeResult> {
    await wait(240);

    return {
      implementation: {
        name: this.#backend === "rust" ? "qxnm-forge-rust" : "qxnm-forge-dotnet",
        version: "0.1.0-preview",
        language: this.#backend,
      },
      capabilities: {
        methods: SHARED_METHODS,
        eventTypes: [
          "run.started",
          "message.delta",
          "message.completed",
          "approval.requested",
          "approval.resolved",
          "run.completed",
        ],
        tools: FAUX_TOOL_IDS,
      },
    };
  }

  /**
   * 返回公开 faux 与当前已配置自定义 Provider 的确定性模型快照。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async listModels(): Promise<readonly ModelDescriptor[]> {
    await wait(120);
    return createMockModelSnapshot(this.#state);
  }

  /**
   * 返回与冻结后端目录同形但不表示真实连通性的浏览器预览模板。
   *
   * 不变量：这里只复制非敏感配置建议，不广告模型或执行可用性。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async listProviderCatalog(): Promise<readonly ProviderCatalogEntry[]> {
    await wait(40);
    return PREVIEW_PROVIDER_CATALOG.map((provider) => ({ ...provider }));
  }

  /**
   * 接受一次预览运行并返回临时引用。
   *
   * 输入：非空 prompt 与服务端模型三元标识。
   * 输出：仅用于当前预览生命周期的 run 引用。
   * 失败：prompt 为空或模型三元路由不在当前可用快照时拒绝请求。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async startRun(input: RunStartInput): Promise<RunStartResult> {
    if (input.prompt.trim().length === 0) {
      throw new Error("prompt must not be empty");
    }
    if (
      !createMockModelSnapshot(this.#state).some(
        (model) => getModelRouteKey(model) === getModelRouteKey(input.model),
      )
    ) {
      throw new Error("model route is not available");
    }

    await wait(760);
    return {
      runId: crypto.randomUUID(),
    };
  }

  /**
   * 模拟取消当前预览运行。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async cancelRun(sessionId: string, runId: string): Promise<void> {
    void sessionId;
    void runId;
    await wait(80);
  }

  /**
   * 在浏览器 faux 状态中解决一个确定性审批请求。
   *
   * 输入：精确 Session/run/approval ID 与服务端已提供的 choice。
   * 输出：决议事件写入内存快照后无正文。
   * 不变量：未知、重复或不在 choices 内的决定被拒绝；不会执行真实工具或写入宿主。
   * 失败：标识不匹配、审批已解决或过期、choice 未提供时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async respondToApproval(
    sessionId: string,
    runId: string,
    approvalId: string,
    decision: ApprovalDecision,
  ): Promise<void> {
    const snapshot = this.#state.sessionSnapshots.get(sessionId);
    const requestedEvent = snapshot?.events.find(
      (event) =>
        event.type === "approval.requested" &&
        event.runId === runId &&
        (event.data.approval as { approvalId?: unknown } | undefined)?.approvalId ===
          approvalId,
    );
    const alreadyResolved = snapshot?.events.some(
      (event) =>
        event.type === "approval.resolved" &&
        typeof event.data.approvalId === "string" &&
        getApprovalIdentityKey(
          event.sessionId,
          event.runId,
          event.data.approvalId,
        ) === getApprovalIdentityKey(sessionId, runId, approvalId),
    );
    const offeredChoices = (
      requestedEvent?.data.approval as { choices?: unknown } | undefined
    )?.choices;
    const expiresAt = (
      requestedEvent?.data.approval as { expiresAt?: unknown } | undefined
    )?.expiresAt;
    const expiresAtTimestamp =
      typeof expiresAt === "string" ? Date.parse(expiresAt) : Number.NaN;
    if (
      !snapshot ||
      !requestedEvent ||
      alreadyResolved ||
      !Array.isArray(offeredChoices) ||
      !offeredChoices.includes(decision.choice) ||
      !Number.isFinite(expiresAtTimestamp) ||
      expiresAtTimestamp <= Date.now()
    ) {
      throw new Error("approval is unavailable or the decision was not offered");
    }

    const nextSequence = snapshot.latestSeq + 1;
    const resolvedSnapshot: SessionSnapshot = {
      ...snapshot,
      latestSeq: nextSequence,
      events: [
        ...snapshot.events,
        {
          sessionId,
          runId,
          ...(requestedEvent.turnId ? { turnId: requestedEvent.turnId } : {}),
          seq: nextSequence,
          time: new Date().toISOString(),
          type: "approval.resolved",
          data: { approvalId, decision, resolutionSource: "client" },
        },
      ],
    };
    const remainingApprovals = projectPendingApprovals(resolvedSnapshot);
    const nextSnapshot = {
      ...resolvedSnapshot,
      activeRunId: remainingApprovals[0]?.runId ?? null,
    };
    this.#state.sessionSnapshots.set(sessionId, nextSnapshot);
    const session = this.#state.sessions.get(sessionId);
    if (session) {
      this.#state.sessions.set(sessionId, {
        sessionId: session.sessionId,
        title: session.title,
        project: session.project,
        updatedAt: new Date().toISOString(),
        archived: session.archived,
        ...(remainingApprovals.length > 0 ? { status: "approval" as const } : {}),
      });
    }
    await wait(80);
  }

  /**
   * 返回当前后端预览状态中的 Provider 脱敏列表。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async listProviderConnections(): Promise<readonly ProviderConnection[]> {
    await wait(40);
    return [...this.#state.providerConnections.values()].map(cloneProviderConnection);
  }

  /**
   * 创建不含凭据的浏览器内存 Provider 连接。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async createProviderConnection(
    input: ProviderConnectionInput,
  ): Promise<ProviderConnectionMutationResult> {
    const normalized = normalizeProviderInput(input);
    if (
      [...this.#state.providerConnections.values()].some(
        (connection) => connection.providerId === normalized.providerId,
      )
    ) {
      throw new Error("Provider ID 已存在");
    }
    const timestamp = new Date().toISOString();
    const connection: ProviderConnection = {
      ...normalized,
      connectionId: crypto.randomUUID(),
      revision: 1,
      credentialConfigured: false,
      createdAt: timestamp,
      updatedAt: timestamp,
    };
    this.#state.providerConnections.set(connection.connectionId, connection);
    await wait(60);
    return { connection: cloneProviderConnection(connection), restartRequired: true };
  }

  /**
   * 以 revision CAS 更新浏览器内存 Provider 配置。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async updateProviderConnection(
    connectionId: string,
    expectedRevision: number,
    input: ProviderConnectionInput,
  ): Promise<ProviderConnectionMutationResult> {
    const current = this.#state.providerConnections.get(connectionId);
    if (!current || current.revision !== expectedRevision) {
      throw new Error("Provider 不存在或已在其他位置更新");
    }
    const normalized = normalizeProviderInput(input);
    if (
      [...this.#state.providerConnections.values()].some(
        (connection) =>
          connection.connectionId !== connectionId &&
          connection.providerId === normalized.providerId,
      )
    ) {
      throw new Error("Provider ID 已存在");
    }
    const connection: ProviderConnection = {
      ...normalized,
      connectionId,
      revision: current.revision + 1,
      credentialConfigured: current.credentialConfigured,
      createdAt: current.createdAt,
      updatedAt: new Date().toISOString(),
    };
    this.#state.providerConnections.set(connectionId, connection);
    await wait(60);
    return { connection: cloneProviderConnection(connection), restartRequired: true };
  }

  /**
   * 拒绝在普通浏览器 faux 预览中访问用户配置的远端模型目录。
   *
   * 输入：连接标识与 revision；输出：本模式无成功结果。
   * 不变量：浏览器预览不读取 credential，也不向 Provider 发起网络请求。
   * 失败：始终拒绝；调用方应仅在 capability 广告本方法后调用。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public discoverProviderModels(
    connectionId: string,
    expectedRevision: number,
  ): Promise<ProviderModelDiscoveryResult> {
    void connectionId;
    void expectedRevision;
    return Promise.reject(new Error("Provider model discovery is unavailable in browser preview"));
  }

  /**
   * 删除浏览器内存 Provider 连接及脱敏凭据状态。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async deleteProviderConnection(
    connectionId: string,
    expectedRevision: number,
  ): Promise<ProviderConnectionDeleteResult> {
    const current = this.#state.providerConnections.get(connectionId);
    if (!current || current.revision !== expectedRevision) {
      throw new Error("Provider 不存在或已在其他位置更新");
    }
    this.#state.providerConnections.delete(connectionId);
    await wait(50);
    return { deleted: true, restartRequired: true };
  }

  /**
   * 只记录凭据已配置状态，不在浏览器预览中保留 secret 正文。
   *
   * 输入：连接标识与瞬时非空凭据。
   * 输出：configured=true 的脱敏状态。
   * 不变量：credential 在方法返回前不会写入任何状态对象。
   * 失败：连接不存在或凭据为空时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async setProviderCredential(
    providerId: string,
    credential: string,
  ): Promise<ProviderCredentialStatus> {
    const connection = [...this.#state.providerConnections.values()].find(
      (candidate) => candidate.providerId === providerId,
    );
    if (!connection || credential.trim().length === 0) {
      throw new Error("Provider 不存在或凭据为空");
    }
    this.#state.providerConnections.set(connection.connectionId, {
      ...connection,
      credentialConfigured: true,
    });
    await wait(40);
    return { providerId, credentialConfigured: true, restartRequired: true };
  }

  /**
   * 清除浏览器内存中的 Provider 凭据配置状态。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async removeProviderCredential(
    providerId: string,
  ): Promise<ProviderCredentialStatus> {
    const connection = [...this.#state.providerConnections.values()].find(
      (candidate) => candidate.providerId === providerId,
    );
    if (!connection) {
      throw new Error("Provider 不存在");
    }
    this.#state.providerConnections.set(connection.connectionId, {
      ...connection,
      credentialConfigured: false,
    });
    await wait(40);
    return { providerId, credentialConfigured: false, restartRequired: true };
  }

  /**
   * 返回浏览器 faux Session 的完整脱敏 portable 消息快照。
   *
   * 输入：已存在的 Session ID 与可选事件序号下界。
   * 输出：独立深复制的完整消息；faux fixture 当前不产生 durable replay events。
   * 不变量：afterSeq 不过滤 messages，返回对象不能修改共享 mock 状态。
   * 失败：Session 不存在或 afterSeq 不是非负安全整数时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async getSession(sessionId: string, afterSeq?: number): Promise<SessionSnapshot> {
    if (
      (afterSeq !== undefined &&
        (!Number.isSafeInteger(afterSeq) || afterSeq < 0)) ||
      !this.#state.sessions.has(sessionId)
    ) {
      throw new Error("Session 不存在或 afterSeq 无效");
    }
    const snapshot = this.#state.sessionSnapshots.get(sessionId);
    if (!snapshot) {
      throw new Error("Session 消息快照不存在");
    }
    await wait(40);
    return cloneSessionSnapshot(snapshot);
  }

  /**
   * 列出浏览器预览中的活动与归档 Session 摘要。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async listSessions(): Promise<readonly SessionSummary[]> {
    await wait(40);
    return [...this.#state.sessions.values()].map((session) => ({ ...session }));
  }

  /**
   * 将预览 Session 移入归档区。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async archiveSession(sessionId: string): Promise<SessionSummary> {
    const current = this.#state.sessions.get(sessionId);
    if (!current) {
      throw new Error("Session 不存在");
    }
    const session = { ...current, archived: true, status: undefined };
    this.#state.sessions.set(sessionId, session);
    await wait(50);
    return { ...session };
  }

  /**
   * 将预览 Session 从归档区恢复。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async restoreSession(sessionId: string): Promise<SessionSummary> {
    const current = this.#state.sessions.get(sessionId);
    if (!current) {
      throw new Error("Session 不存在");
    }
    const session = { ...current, archived: false };
    this.#state.sessions.set(sessionId, session);
    await wait(50);
    return { ...session };
  }

  /**
   * 从预览状态永久删除 Session 摘要。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async deleteSession(sessionId: string): Promise<void> {
    if (!this.#state.sessions.delete(sessionId)) {
      throw new Error("Session 不存在");
    }
    this.#state.sessionSnapshots.delete(sessionId);
    await wait(50);
  }
}

/**
 * 按后端选择创建 application service 客户端。
 *
 * 当前输出为零网络 faux 预览；后续桌面 NDJSON bridge 与远程 HTTP/WS
 * 实现必须继续满足同一接口和 schema 校验边界。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function createApplicationServiceClient(backend: BackendKind): ApplicationServiceClient {
  return isTauri()
    ? new TauriApplicationServiceClient(backend)
    : new MockApplicationServiceClient(backend);
}
import { isTauri } from "@tauri-apps/api/core";
