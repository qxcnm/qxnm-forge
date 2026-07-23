import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import App from "@/App";
import { InterfaceProviders } from "@/components/interface-providers";
import { TooltipProvider } from "@/components/ui/tooltip";
import { createApplicationServiceClient } from "@/lib/mock-application-service";
import { useWorkspaceUiStore } from "@/store/workspace-ui-store";
import type {
  ApplicationServiceClient,
  BackendKind,
  InitializeResult,
  ModelDescriptor,
  SessionSnapshot,
  SessionSummary,
} from "@/types/application-service";
import type { AgentProfile } from "@/types/agent-profile";

const MODEL: ModelDescriptor = {
  providerId: "faux",
  modelId: "faux-v1",
  apiFamily: "faux",
  displayName: "Faux v1",
  capabilities: { input: ["text"], output: ["text"] },
  supportsReasoning: false,
  supportsTools: true,
  supportsImageInput: false,
  supportsImageOutput: false,
};

const REPLACEMENT_MODEL: ModelDescriptor = {
  providerId: "replacement",
  modelId: "replacement-v1",
  apiFamily: "faux",
  displayName: "Replacement v1",
  capabilities: { input: ["text"], output: ["text"] },
  supportsReasoning: false,
  supportsTools: false,
  supportsImageInput: false,
  supportsImageOutput: false,
};

const DISCOVERED_MODEL: ModelDescriptor = {
  providerId: "newapi-gzxsy",
  modelId: "discovered-model",
  apiFamily: "openai-completions",
  displayName: "Discovered model",
  capabilities: { input: ["text"], output: ["text"] },
  supportsReasoning: false,
  supportsTools: true,
  supportsImageInput: false,
  supportsImageOutput: false,
};

const IMAGE_MODEL: ModelDescriptor = {
  providerId: "image-provider",
  modelId: "image-v1",
  apiFamily: "openai-responses",
  displayName: "Image v1",
  capabilities: { input: ["text", "image"], output: ["text", "image"] },
  supportsReasoning: false,
  supportsTools: false,
  supportsImageInput: true,
  supportsImageOutput: true,
};

const TEST_IMAGE_BYTES = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]);
const TEST_IMAGE_BASE64 = "iVBORw0KGgo=";
const TEST_IMAGE_SHA256 =
  "4c4b6a3be1314ab86138bef4314dde022e600960d8689a2c8f8631802d20dab6";

const METHODS = [
  "initialize",
  "models/list",
  "run/start",
  "approval/respond",
  "session/get",
  "session/list",
  "agentProfiles/list",
] as const;

const INITIALIZE_RESULT: InitializeResult = {
  implementation: {
    name: "test-rust-service",
    version: "0.1.0-test",
    language: "rust",
  },
  capabilities: {
    methods: METHODS,
    eventTypes: ["approval.requested", "approval.resolved"],
    tools: ["file.write", "process.exec"],
  },
};

const PROFILE: AgentProfile = {
  profileId: "coding-agent",
  revision: 1,
  displayName: "编码助手",
  description: "测试画像",
  enabled: true,
  instructions: "检查工作区改动。",
  model: MODEL,
  requestedToolIds: [],
  dangerousActionMode: "ask",
  behavior: {
    responseStyle: "balanced",
    planFirst: false,
    reviewChanges: true,
  },
  createdAt: "2026-07-21T00:00:00Z",
  updatedAt: "2026-07-21T00:00:00Z",
};

const REPLACEMENT_PROFILE: AgentProfile = {
  ...PROFILE,
  model: REPLACEMENT_MODEL,
};

interface Deferred<T> {
  readonly promise: Promise<T>;
  readonly resolve: (value: T) => void;
  readonly reject: (reason: unknown) => void;
}

/**
 * 创建可由测试精确推进的 Promise，用于观察请求进行中的失效关闭状态。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function createDeferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

/**
 * 在保留 faux 客户端完整方法面的同时覆盖故障注入方法。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function createTestService(
  overrides: Partial<ApplicationServiceClient>,
): ApplicationServiceClient {
  const base = createApplicationServiceClient("rust");
  return {
    mode: overrides.mode ?? base.mode,
    initialize: overrides.initialize ?? base.initialize.bind(base),
    listModels: overrides.listModels ?? base.listModels.bind(base),
    createArtifact: overrides.createArtifact ?? base.createArtifact.bind(base),
    readArtifact: overrides.readArtifact ?? base.readArtifact.bind(base),
    startRun: overrides.startRun ?? base.startRun.bind(base),
    cancelRun: overrides.cancelRun ?? base.cancelRun.bind(base),
    respondToApproval:
      overrides.respondToApproval ?? base.respondToApproval.bind(base),
    listProviderCatalog:
      overrides.listProviderCatalog ?? base.listProviderCatalog.bind(base),
    listProviderConnections:
      overrides.listProviderConnections ?? base.listProviderConnections.bind(base),
    createProviderConnection:
      overrides.createProviderConnection ?? base.createProviderConnection.bind(base),
    updateProviderConnection:
      overrides.updateProviderConnection ?? base.updateProviderConnection.bind(base),
    discoverProviderModels:
      overrides.discoverProviderModels ?? base.discoverProviderModels.bind(base),
    deleteProviderConnection:
      overrides.deleteProviderConnection ?? base.deleteProviderConnection.bind(base),
    setProviderCredential:
      overrides.setProviderCredential ?? base.setProviderCredential.bind(base),
    removeProviderCredential:
      overrides.removeProviderCredential ?? base.removeProviderCredential.bind(base),
    getSession: overrides.getSession ?? base.getSession.bind(base),
    listSessions: overrides.listSessions ?? base.listSessions.bind(base),
    archiveSession: overrides.archiveSession ?? base.archiveSession.bind(base),
    restoreSession: overrides.restoreSession ?? base.restoreSession.bind(base),
    deleteSession: overrides.deleteSession ?? base.deleteSession.bind(base),
    listProfiles: overrides.listProfiles ?? base.listProfiles.bind(base),
    createProfile: overrides.createProfile ?? base.createProfile.bind(base),
    updateProfile: overrides.updateProfile ?? base.updateProfile.bind(base),
    deleteProfile: overrides.deleteProfile ?? base.deleteProfile.bind(base),
  };
}

/**
 * 使用隔离 QueryClient 和可注入 application service 或后端工厂渲染工作台。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function renderTestApp(
  serviceOrFactory:
    | ApplicationServiceClient
    | ((backend: BackendKind) => ApplicationServiceClient),
  staleTime = 0,
) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime },
      mutations: { retry: false },
    },
  });
  const serviceFactory =
    typeof serviceOrFactory === "function"
      ? serviceOrFactory
      : () => serviceOrFactory;
  const view = render(
    <QueryClientProvider client={queryClient}>
      <InterfaceProviders>
        <TooltipProvider>
          <App serviceFactory={serviceFactory} />
        </TooltipProvider>
      </InterfaceProviders>
    </QueryClientProvider>,
  );
  return { ...view, queryClient };
}

/**
 * 构造一个符合 durable 事件 schema 的待审批事件。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function createApprovalEvent(
  approvalId: string,
  operation: "file.write" | "process.exec",
  sequence: number,
) {
  return {
    sessionId: "approval-session",
    runId: "approval-run",
    seq: sequence,
    time: "2099-07-21T09:59:00Z",
    type: "approval.requested" as const,
    data: {
      approval: {
        approvalId,
        toolCallId: `tool-${sequence}`,
        operation,
        arguments: { target: `${operation}-${sequence}` },
        operationHash: String(sequence).repeat(64),
        risk: "medium",
        reason: "测试审批",
        resources: [{ kind: "other", value: `${operation}-${sequence}` }],
        choices: ["allow_once", "deny"],
        expiresAt: "2099-07-21T10:00:00Z",
      },
    },
  };
}

const APPROVAL_SESSION: SessionSummary = {
  sessionId: "approval-session",
  title: "审批状态机",
  project: "测试工作区",
  updatedAt: "2099-07-21T09:59:00Z",
  archived: false,
  status: "approval",
};

const PENDING_APPROVAL_SNAPSHOT: SessionSnapshot = {
  sessionId: APPROVAL_SESSION.sessionId,
  latestSeq: 2,
  activeRunId: null,
  messages: [],
  events: [
    createApprovalEvent("approval-one", "file.write", 1),
    createApprovalEvent("approval-two", "process.exec", 2),
  ],
};

const FIRST_APPROVAL_RESOLVED_SNAPSHOT: SessionSnapshot = {
  ...PENDING_APPROVAL_SNAPSHOT,
  latestSeq: 3,
  events: [
    ...PENDING_APPROVAL_SNAPSHOT.events,
    {
      sessionId: APPROVAL_SESSION.sessionId,
      runId: "approval-run",
      seq: 3,
      time: "2099-07-21T09:59:10Z",
      type: "approval.resolved",
      data: {
        approvalId: "approval-one",
        decision: { choice: "allow_once" },
        resolutionSource: "client",
      },
    },
  ],
};

const LIVE_CHAT_SESSION: SessionSummary = {
  sessionId: "live-chat-session",
  title: "真实聊天闭环",
  project: "测试工作区",
  updatedAt: "2026-07-22T06:00:00Z",
  archived: false,
};

const EMPTY_LIVE_CHAT_SNAPSHOT: SessionSnapshot = {
  sessionId: LIVE_CHAT_SESSION.sessionId,
  latestSeq: 0,
  activeRunId: null,
  messages: [],
  events: [],
};

const IMAGE_OUTPUT_SESSION: SessionSummary = {
  sessionId: "image-output-session",
  title: "图片输出闭环",
  project: "测试工作区",
  updatedAt: "2026-07-22T07:00:00Z",
  archived: false,
};

const VALID_OUTPUT_ARTIFACT = {
  artifactId: "artifact-output-valid",
  mediaType: "image/png",
  byteLength: TEST_IMAGE_BYTES.length,
  sha256: TEST_IMAGE_SHA256,
  displayName: "模型生成图片.png",
} as const;

const BROKEN_OUTPUT_ARTIFACT = {
  ...VALID_OUTPUT_ARTIFACT,
  artifactId: "artifact-output-broken",
  displayName: "损坏图片.png",
} as const;

const IMAGE_OUTPUT_SNAPSHOT: SessionSnapshot = {
  sessionId: IMAGE_OUTPUT_SESSION.sessionId,
  latestSeq: 0,
  activeRunId: null,
  messages: [
    {
      messageId: "image-assistant",
      role: "assistant",
      content: [
        { type: "text", text: "图片结果文本仍然可见" },
        { type: "image_ref", artifact: VALID_OUTPUT_ARTIFACT, alt: "模型生成图片" },
        { type: "image_ref", artifact: BROKEN_OUTPUT_ARTIFACT, alt: "损坏图片" },
      ],
      provider: {
        id: IMAGE_MODEL.providerId,
        modelId: IMAGE_MODEL.modelId,
        apiFamily: IMAGE_MODEL.apiFamily,
      },
      finishReason: "stop",
      usage: { inputTokens: 1, outputTokens: 1, totalTokens: 2 },
      time: "2026-07-22T07:00:01Z",
    },
  ],
  events: [],
};

const STREAMING_LIVE_CHAT_SNAPSHOT: SessionSnapshot = {
  sessionId: LIVE_CHAT_SESSION.sessionId,
  latestSeq: 3,
  activeRunId: "live-run",
  messages: [
    {
      messageId: "live-user",
      role: "user",
      content: [{ type: "text", text: "验证真实回复" }],
      time: "2026-07-22T06:00:01Z",
    },
  ],
  events: [
    {
      sessionId: LIVE_CHAT_SESSION.sessionId,
      runId: "live-run",
      seq: 1,
      time: "2026-07-22T06:00:01Z",
      type: "run.started",
      data: {},
    },
    {
      sessionId: LIVE_CHAT_SESSION.sessionId,
      runId: "live-run",
      turnId: "live-turn",
      seq: 2,
      time: "2026-07-22T06:00:02Z",
      type: "message.started",
      data: { messageId: "live-assistant", role: "assistant" },
    },
    {
      sessionId: LIVE_CHAT_SESSION.sessionId,
      runId: "live-run",
      turnId: "live-turn",
      seq: 3,
      time: "2026-07-22T06:00:03Z",
      type: "message.delta",
      data: {
        messageId: "live-assistant",
        delta: { type: "text", text: "真实流式回复" },
      },
    },
  ],
};

const COMPLETED_LIVE_CHAT_SNAPSHOT: SessionSnapshot = {
  ...STREAMING_LIVE_CHAT_SNAPSHOT,
  latestSeq: 5,
  activeRunId: null,
  messages: [
    ...STREAMING_LIVE_CHAT_SNAPSHOT.messages,
    {
      messageId: "live-assistant",
      role: "assistant",
      content: [{ type: "text", text: "真实流式回复已完成" }],
      provider: { id: "faux", modelId: "faux-v1", apiFamily: "faux" },
      finishReason: "stop",
      usage: { inputTokens: 1, outputTokens: 1, totalTokens: 2 },
      time: "2026-07-22T06:00:04Z",
    },
  ],
  events: [
    ...STREAMING_LIVE_CHAT_SNAPSHOT.events,
    {
      sessionId: LIVE_CHAT_SESSION.sessionId,
      runId: "live-run",
      turnId: "live-turn",
      seq: 4,
      time: "2026-07-22T06:00:04Z",
      type: "message.completed",
      data: { messageId: "live-assistant", finishReason: "stop" },
    },
    {
      sessionId: LIVE_CHAT_SESSION.sessionId,
      runId: "live-run",
      seq: 5,
      time: "2026-07-22T06:00:05Z",
      type: "run.completed",
      data: {
        status: "completed",
        usage: { inputTokens: 1, outputTokens: 1, totalTokens: 2 },
      },
    },
  ],
};

const FAILED_LIVE_CHAT_SNAPSHOT: SessionSnapshot = {
  sessionId: LIVE_CHAT_SESSION.sessionId,
  latestSeq: 1,
  activeRunId: null,
  messages: [
    {
      messageId: "failed-user",
      role: "user",
      content: [{ type: "text", text: "触发失败" }],
      time: "2026-07-22T06:01:00Z",
    },
  ],
  events: [
    {
      sessionId: LIVE_CHAT_SESSION.sessionId,
      runId: "failed-run",
      seq: 1,
      time: "2026-07-22T06:01:01Z",
      type: "run.failed",
      data: {
        status: "failed",
        error: {
          code: -32005,
          message: "provider HTTP request failed with status 400",
          retryable: false,
          details: { kind: "provider_error", httpStatus: 400 },
        },
      },
    },
  ],
};

describe("App fail-closed boundaries", () => {
  beforeEach(() => {
    window.localStorage.clear();
    useWorkspaceUiStore.setState({
      backend: "rust",
      activeSessionId: "desktop-shell",
      activeView: "conversation",
      activeAgentProfileId: null,
      mobileSidebarOpen: false,
      reviewOpen: false,
      composerSubmitMode: "enter",
      sidebarWidth: "standard",
      reduceMotion: false,
    });
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  /**
   * 验证正式 application service 从事件增量显示流式文本，并在终态切换为 durable assistant 消息。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rebuilds live and completed assistant messages without an acceptance placeholder", async () => {
    let started = false;
    let postStartReads = 0;
    const getSession = vi.fn<ApplicationServiceClient["getSession"]>(() => {
      if (!started) {
        return Promise.resolve(EMPTY_LIVE_CHAT_SNAPSHOT);
      }
      postStartReads += 1;
      return Promise.resolve(
        postStartReads === 1
          ? STREAMING_LIVE_CHAT_SNAPSHOT
          : COMPLETED_LIVE_CHAT_SNAPSHOT,
      );
    });
    const service = createTestService({
      mode: "application-service",
      initialize: vi.fn().mockResolvedValue({
        ...INITIALIZE_RESULT,
        capabilities: {
          ...INITIALIZE_RESULT.capabilities,
          eventTypes: [
            "run.started",
            "message.started",
            "message.delta",
            "message.completed",
            "run.completed",
            "run.failed",
          ],
        },
      }),
      listModels: vi.fn().mockResolvedValue([MODEL]),
      listProfiles: vi.fn().mockResolvedValue([]),
      listSessions: vi.fn().mockResolvedValue([LIVE_CHAT_SESSION]),
      getSession,
      startRun: vi.fn().mockImplementation(() => {
        started = true;
        return Promise.resolve({ runId: "live-run" });
      }),
    });
    useWorkspaceUiStore.setState({ activeSessionId: LIVE_CHAT_SESSION.sessionId });
    renderTestApp(service);

    await waitFor(() => expect(screen.getByLabelText("发送任务")).toBeDisabled());
    fireEvent.change(screen.getByLabelText("任务消息"), {
      target: { value: "验证真实回复" },
    });
    await waitFor(() => expect(screen.getByLabelText("发送任务")).toBeEnabled());
    fireEvent.click(screen.getByLabelText("发送任务"));

    expect(await screen.findByText("真实流式回复")).toBeInTheDocument();
    expect(screen.queryByText(/capability 画像接受/)).not.toBeInTheDocument();
    expect(
      await screen.findByText("真实流式回复已完成", {}, { timeout: 2_000 }),
    ).toBeInTheDocument();
    expect(screen.queryByText("真实流式回复")).not.toBeInTheDocument();
  });

  /**
   * 验证正式运行的 durable run.failed 会显示脱敏错误与 HTTP 状态，而不是停留在接受占位。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("renders durable provider failures in the conversation", async () => {
    let snapshot = EMPTY_LIVE_CHAT_SNAPSHOT;
    const service = createTestService({
      mode: "application-service",
      initialize: vi.fn().mockResolvedValue({
        ...INITIALIZE_RESULT,
        capabilities: {
          ...INITIALIZE_RESULT.capabilities,
          eventTypes: ["run.failed"],
        },
      }),
      listModels: vi.fn().mockResolvedValue([MODEL]),
      listProfiles: vi.fn().mockResolvedValue([]),
      listSessions: vi.fn().mockResolvedValue([LIVE_CHAT_SESSION]),
      getSession: vi.fn().mockImplementation(() => Promise.resolve(snapshot)),
      startRun: vi.fn().mockImplementation(() => {
        snapshot = FAILED_LIVE_CHAT_SNAPSHOT;
        return Promise.resolve({ runId: "failed-run" });
      }),
    });
    useWorkspaceUiStore.setState({ activeSessionId: LIVE_CHAT_SESSION.sessionId });
    renderTestApp(service);

    fireEvent.change(await screen.findByLabelText("任务消息"), {
      target: { value: "触发失败" },
    });
    await waitFor(() => expect(screen.getByLabelText("发送任务")).toBeEnabled());
    fireEvent.click(screen.getByLabelText("发送任务"));

    expect(
      await screen.findByText(
        "运行失败：provider HTTP request failed with status 400（HTTP 400）",
      ),
    ).toBeInTheDocument();
    expect(screen.queryByText(/capability 画像接受/)).not.toBeInTheDocument();
  });

  /**
   * 验证仅文本模型在发布 artifact 前拒绝图片，并在切换 Session 时清理附件和错误。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects images before upload for a text-only model", async () => {
    const createArtifact = vi.fn<ApplicationServiceClient["createArtifact"]>();
    const startRun = vi.fn<ApplicationServiceClient["startRun"]>();
    const service = createTestService({
      initialize: vi.fn().mockResolvedValue({
        ...INITIALIZE_RESULT,
        capabilities: {
          ...INITIALIZE_RESULT.capabilities,
          methods: [...METHODS, "artifacts/create"],
        },
      }),
      listModels: vi.fn().mockResolvedValue([MODEL]),
      listProfiles: vi.fn().mockResolvedValue([]),
      createArtifact,
      startRun,
    });
    renderTestApp(service);

    await waitFor(() => expect(screen.getByLabelText("选择模型")).toBeEnabled());
    const file = new File([TEST_IMAGE_BYTES], "text-only.png", {
      type: "image/png",
    });
    Object.defineProperty(file, "arrayBuffer", {
      value: () => Promise.resolve(TEST_IMAGE_BYTES.slice().buffer),
    });
    fireEvent.change(screen.getByLabelText("选择图片或文本文件"), {
      target: { files: [file] },
    });
    expect(
      await screen.findByLabelText("移除附件 text-only.png"),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByLabelText("发送任务"));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "所选模型不支持图片输入",
    );
    expect(createArtifact).not.toHaveBeenCalled();
    expect(startRun).not.toHaveBeenCalled();
    expect(screen.getByLabelText("移除附件 text-only.png")).toBeInTheDocument();

    fireEvent.click(
      (await screen.findAllByRole("button", { name: /统一后端能力协议/ }))[0],
    );
    await waitFor(() => {
      expect(screen.queryByLabelText("移除附件 text-only.png")).not.toBeInTheDocument();
      expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    });
  });

  /**
   * 验证图片模型只在 run/start 接受后清空草稿与附件，拒绝时保留并允许重试。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("uploads image references and preserves the composer until run acceptance", async () => {
    const firstRun = createDeferred<
      Awaited<ReturnType<ApplicationServiceClient["startRun"]>>
    >();
    const createArtifact = vi
      .fn<ApplicationServiceClient["createArtifact"]>()
      .mockResolvedValue({ artifact: VALID_OUTPUT_ARTIFACT });
    const startRun = vi
      .fn<ApplicationServiceClient["startRun"]>()
      .mockImplementationOnce(() => firstRun.promise)
      .mockResolvedValue({ runId: "accepted-image-run" });
    const service = createTestService({
      initialize: vi.fn().mockResolvedValue({
        ...INITIALIZE_RESULT,
        capabilities: {
          ...INITIALIZE_RESULT.capabilities,
          methods: [...METHODS, "artifacts/create"],
        },
      }),
      listModels: vi.fn().mockResolvedValue([IMAGE_MODEL]),
      listProfiles: vi.fn().mockResolvedValue([]),
      createArtifact,
      startRun,
    });
    renderTestApp(service);

    await waitFor(() => expect(screen.getByLabelText("选择模型")).toBeEnabled());
    const file = new File([TEST_IMAGE_BYTES], "input.png", { type: "image/png" });
    Object.defineProperty(file, "arrayBuffer", {
      value: () => Promise.resolve(TEST_IMAGE_BYTES.slice().buffer),
    });
    fireEvent.change(screen.getByLabelText("选择图片或文本文件"), {
      target: { files: [file] },
    });
    expect(await screen.findByLabelText("移除附件 input.png")).toBeInTheDocument();
    const composer = screen.getByLabelText("任务消息");
    fireEvent.change(composer, { target: { value: "分析这张图片" } });
    fireEvent.click(screen.getByLabelText("发送任务"));

    await waitFor(() => expect(startRun).toHaveBeenCalledTimes(1));
    expect(createArtifact).toHaveBeenCalledWith({
      sessionId: "desktop-shell",
      mediaType: "image/png",
      dataBase64: TEST_IMAGE_BASE64,
    });
    expect(startRun).toHaveBeenCalledWith(
      expect.objectContaining({
        prompt: "分析这张图片",
        attachments: [
          {
            type: "image_ref",
            artifact: VALID_OUTPUT_ARTIFACT,
            alt: "input.png",
          },
        ],
      }),
    );
    expect(composer).toHaveValue("分析这张图片");
    expect(screen.getByLabelText("移除附件 input.png")).toBeInTheDocument();

    act(() => firstRun.reject(new Error("run rejected")));
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "任务未被服务接受；草稿与附件已保留",
    );
    expect(composer).toHaveValue("分析这张图片");
    expect(screen.getByLabelText("移除附件 input.png")).toBeInTheDocument();

    await waitFor(() => expect(screen.getByLabelText("发送任务")).toBeEnabled());
    fireEvent.click(screen.getByLabelText("发送任务"));
    expect(await screen.findByText(/分析这张图片/)).toBeInTheDocument();
    await waitFor(() => {
      expect(composer).toHaveValue("");
      expect(screen.queryByLabelText("移除附件 input.png")).not.toBeInTheDocument();
    });
    expect(createArtifact).toHaveBeenCalledTimes(2);
    expect(startRun).toHaveBeenCalledTimes(2);
  });

  /**
   * 验证 durable assistant 图片只在 MIME、签名和摘要通过时显示，失败图片不遮蔽文本。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("renders verified assistant images and isolates artifact read failures", async () => {
    const readArtifact = vi
      .fn<ApplicationServiceClient["readArtifact"]>()
      .mockImplementation((input) =>
        input.artifactId === VALID_OUTPUT_ARTIFACT.artifactId
          ? Promise.resolve({
              artifact: VALID_OUTPUT_ARTIFACT,
              dataBase64: TEST_IMAGE_BASE64,
            })
          : Promise.reject(new Error("artifact unavailable")),
      );
    const service = createTestService({
      mode: "application-service",
      initialize: vi.fn().mockResolvedValue({
        ...INITIALIZE_RESULT,
        capabilities: {
          ...INITIALIZE_RESULT.capabilities,
          methods: [...METHODS, "artifacts/read"],
        },
      }),
      listModels: vi.fn().mockResolvedValue([IMAGE_MODEL]),
      listProfiles: vi.fn().mockResolvedValue([]),
      listSessions: vi.fn().mockResolvedValue([IMAGE_OUTPUT_SESSION]),
      getSession: vi.fn().mockResolvedValue(IMAGE_OUTPUT_SNAPSHOT),
      readArtifact,
    });
    useWorkspaceUiStore.setState({ activeSessionId: IMAGE_OUTPUT_SESSION.sessionId });
    renderTestApp(service);

    expect(await screen.findByText("图片结果文本仍然可见")).toBeInTheDocument();
    expect(await screen.findByRole("img", { name: "模型生成图片" })).toHaveAttribute(
      "src",
      `data:image/png;base64,${TEST_IMAGE_BASE64}`,
    );
    expect(
      await screen.findByText("图片无法安全读取，文本内容仍可查看"),
    ).toBeInTheDocument();
    expect(screen.getByText("图片结果文本仍然可见")).toBeInTheDocument();
    expect(screen.queryByRole("img", { name: "损坏图片" })).not.toBeInTheDocument();
    expect(readArtifact).toHaveBeenCalledTimes(2);
  });

  /**
   * 验证 Session 图片预算从最新消息向前分配，旧图片不会抢占最新输出额度。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("prioritizes the newest assistant images within the session byte budget", async () => {
    const budgetSession: SessionSummary = {
      ...IMAGE_OUTPUT_SESSION,
      sessionId: "image-budget-session",
      title: "图片预算",
    };
    const imageByteLength = 32 * 1_024 * 1_024;
    const budgetSnapshot: SessionSnapshot = {
      sessionId: budgetSession.sessionId,
      latestSeq: 0,
      activeRunId: null,
      messages: [
        {
          messageId: "image-old",
          role: "assistant",
          content: [{
            type: "image_ref",
            artifact: {
              artifactId: "artifact-old",
              mediaType: "image/png",
              byteLength: imageByteLength,
              sha256: "1".repeat(64),
            },
          }],
          provider: { id: "faux", modelId: "faux-v1", apiFamily: "faux" },
          finishReason: "stop",
          usage: { inputTokens: 1, outputTokens: 1, totalTokens: 2 },
          time: "2026-07-22T08:00:01Z",
        },
        {
          messageId: "image-middle",
          role: "assistant",
          content: [{
            type: "image_ref",
            artifact: {
              artifactId: "artifact-middle",
              mediaType: "image/png",
              byteLength: imageByteLength,
              sha256: "2".repeat(64),
            },
          }],
          provider: { id: "faux", modelId: "faux-v1", apiFamily: "faux" },
          finishReason: "stop",
          usage: { inputTokens: 1, outputTokens: 1, totalTokens: 2 },
          time: "2026-07-22T08:00:02Z",
        },
        {
          messageId: "image-new",
          role: "assistant",
          content: [{
            type: "image_ref",
            artifact: {
              artifactId: "artifact-new",
              mediaType: "image/png",
              byteLength: imageByteLength,
              sha256: "3".repeat(64),
            },
          }],
          provider: { id: "faux", modelId: "faux-v1", apiFamily: "faux" },
          finishReason: "stop",
          usage: { inputTokens: 1, outputTokens: 1, totalTokens: 2 },
          time: "2026-07-22T08:00:03Z",
        },
      ],
      events: [],
    };
    const readArtifact = vi
      .fn<ApplicationServiceClient["readArtifact"]>()
      .mockRejectedValue(new Error("skip payload in budget test"));
    const service = createTestService({
      mode: "application-service",
      initialize: vi.fn().mockResolvedValue({
        ...INITIALIZE_RESULT,
        capabilities: {
          ...INITIALIZE_RESULT.capabilities,
          methods: [...METHODS, "artifacts/read"],
        },
      }),
      listModels: vi.fn().mockResolvedValue([IMAGE_MODEL]),
      listProfiles: vi.fn().mockResolvedValue([]),
      listSessions: vi.fn().mockResolvedValue([budgetSession]),
      getSession: vi.fn().mockResolvedValue(budgetSnapshot),
      readArtifact,
    });
    useWorkspaceUiStore.setState({ activeSessionId: budgetSession.sessionId });
    renderTestApp(service);

    await waitFor(() => expect(readArtifact).toHaveBeenCalledTimes(2));
    const readArtifactIds = readArtifact.mock.calls.map(
      ([input]) => input.artifactId,
    );
    expect(readArtifactIds).toEqual(["artifact-middle", "artifact-new"]);
    expect(readArtifactIds).not.toContain("artifact-old");
  });

  /**
   * 验证重新握手进行中和失败后不会继续使用旧模型或运行能力，并可显式重试。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("drops stale capabilities and models when reinitialize rejects", async () => {
    const secondInitialize = createDeferred<InitializeResult>();
    const initialize = vi
      .fn<ApplicationServiceClient["initialize"]>()
      .mockResolvedValueOnce(INITIALIZE_RESULT)
      .mockImplementationOnce(() => secondInitialize.promise)
      .mockResolvedValue(INITIALIZE_RESULT);
    const service = createTestService({
      initialize,
      listModels: vi.fn().mockResolvedValue([MODEL]),
      listProfiles: vi.fn().mockResolvedValue([]),
      startRun: vi.fn().mockResolvedValue({ runId: "run-test" }),
    });
    const { queryClient } = renderTestApp(service);

    await waitFor(() => expect(screen.getByLabelText("选择模型")).toBeEnabled());
    act(() => {
      void queryClient.invalidateQueries({
        queryKey: ["application-service", "rust", "initialize"],
        exact: true,
      });
    });
    await waitFor(() => expect(screen.getByLabelText("选择模型")).toBeDisabled());

    act(() => secondInitialize.reject(new Error("reinitialize failed")));
    expect(await screen.findByLabelText("重试加载模型")).toBeInTheDocument();
    expect(screen.getByLabelText("选择模型")).toHaveTextContent("模型加载失败");

    fireEvent.click(screen.getByLabelText("重试加载模型"));
    await waitFor(() => expect(screen.getByLabelText("选择模型")).toBeEnabled());
    expect(initialize).toHaveBeenCalledTimes(3);
  });

  /**
   * 验证新握手撤销 models/list 与 run/start 后旧路由不能提交。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("removes model and run controls when methods are withdrawn", async () => {
    const initialize = vi
      .fn<ApplicationServiceClient["initialize"]>()
      .mockResolvedValueOnce(INITIALIZE_RESULT)
      .mockResolvedValueOnce({
        ...INITIALIZE_RESULT,
        capabilities: {
          ...INITIALIZE_RESULT.capabilities,
          methods: ["initialize"],
        },
      });
    const startRun = vi.fn().mockResolvedValue({ runId: "must-not-run" });
    const service = createTestService({
      initialize,
      listModels: vi.fn().mockResolvedValue([MODEL]),
      listProfiles: vi.fn().mockResolvedValue([]),
      startRun,
    });
    const { queryClient } = renderTestApp(service);

    await waitFor(() => expect(screen.getByLabelText("选择模型")).toBeEnabled());
    await act(async () => {
      await queryClient.invalidateQueries({
        queryKey: ["application-service", "rust", "initialize"],
        exact: true,
      });
    });

    await waitFor(() => {
      expect(screen.getByLabelText("选择模型")).toBeDisabled();
      expect(screen.getByLabelText("选择模型")).toHaveTextContent(
        "服务不支持模型列表",
      );
    });
    fireEvent.change(screen.getByLabelText("任务消息"), {
      target: { value: "不应提交" },
    });
    expect(screen.getByLabelText("发送任务")).toBeDisabled();
    expect(startRun).not.toHaveBeenCalled();
  });

  /**
   * 验证新代际模型读取失败时不会回退旧快照，重试后只显示新快照。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("shows model errors and retries the current generation", async () => {
    const listModels = vi
      .fn<ApplicationServiceClient["listModels"]>()
      .mockResolvedValueOnce([MODEL])
      .mockRejectedValueOnce(new Error("models failed"))
      .mockResolvedValueOnce([REPLACEMENT_MODEL]);
    const service = createTestService({
      initialize: vi.fn().mockResolvedValue(INITIALIZE_RESULT),
      listModels,
      listProfiles: vi.fn().mockResolvedValue([]),
    });
    const { queryClient } = renderTestApp(service);

    await waitFor(() => expect(screen.getByLabelText("选择模型")).toBeEnabled());
    await act(async () => {
      await queryClient.invalidateQueries({
        queryKey: ["application-service", "rust", "initialize"],
        exact: true,
      });
    });

    expect(await screen.findByLabelText("重试加载模型")).toBeInTheDocument();
    expect(screen.getByLabelText("选择模型")).toHaveTextContent("模型加载失败");
    fireEvent.click(screen.getByLabelText("重试加载模型"));

    await waitFor(() => expect(screen.getByLabelText("选择模型")).toBeEnabled());
    expect(screen.getByLabelText("选择模型")).toHaveTextContent("Replacement v1");
    expect(listModels).toHaveBeenCalledTimes(3);
  });

  /**
   * 验证设置页完成后端模型发现后，主输入器采用新 Provider 路由而不是继续停留在 Faux。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("selects the first discovered provider model in the composer", async () => {
    const timestamp = "2026-07-22T00:00:00Z";
    const createdConnection = {
      connectionId: "connection-discovered",
      revision: 1,
      displayName: "星思研 New API",
      providerId: "newapi-gzxsy",
      baseUrl: "https://api.example.invalid/v1",
      modelsUrl: "https://api.example.invalid/v1/models",
      apiFamily: "openai-completions" as const,
      modelIds: [] as readonly string[],
      supportsTools: false,
      supportsImageInput: false,
      supportsImageOutput: false,
      logoAssetId: "newapi-gzxsy",
      enabled: true,
      credentialConfigured: false,
      imageCredentialConfigured: false,
      createdAt: timestamp,
      updatedAt: timestamp,
    };
    const discoveredConnection = {
      ...createdConnection,
      revision: 2,
      modelIds: [DISCOVERED_MODEL.modelId],
      credentialConfigured: true,
    };
    const providerMethods = [
      ...METHODS,
      "providerCatalog/list",
      "providerConnections/list",
      "providerConnections/create",
      "providerConnections/update",
      "providerConnections/delete",
      "providerConnections/discoverModels",
      "providerCredentials/set",
      "providerCredentials/remove",
    ];
    const service = createTestService({
      initialize: vi.fn().mockResolvedValue({
        ...INITIALIZE_RESULT,
        capabilities: {
          ...INITIALIZE_RESULT.capabilities,
          methods: providerMethods,
        },
      }),
      listModels: vi
        .fn()
        .mockResolvedValueOnce([MODEL])
        .mockResolvedValue([MODEL, DISCOVERED_MODEL]),
      listProfiles: vi.fn().mockResolvedValue([]),
      listProviderCatalog: vi.fn().mockResolvedValue([]),
      listProviderConnections: vi.fn().mockResolvedValue([]),
      createProviderConnection: vi.fn().mockResolvedValue({
        connection: createdConnection,
        restartRequired: true,
      }),
      setProviderCredential: vi.fn().mockResolvedValue({
        providerId: createdConnection.providerId,
        credentialConfigured: true,
        restartRequired: true,
      }),
      discoverProviderModels: vi.fn().mockResolvedValue({
        connection: discoveredConnection,
        discoveredCount: 1,
        restartRequired: true,
      }),
    });
    renderTestApp(service);

    await waitFor(() => expect(screen.getByLabelText("选择模型")).toHaveTextContent("Faux v1"));
    fireEvent.click(screen.getByRole("button", { name: "设置" }));
    const providersTab = await screen.findByRole("tab", { name: "提供商" });
    fireEvent.mouseDown(providersTab, { button: 0, ctrlKey: false });
    fireEvent.click(providersTab);
    const importInput = await screen.findByLabelText("导入 New API 连接 JSON");
    fireEvent.change(importInput, {
      target: {
        value:
          '{"_type":"newapi_channel_conn","key":"test-discovery-secret","url":"https://api.example.invalid"}',
      },
    });
    fireEvent.click(screen.getByRole("button", { name: "导入" }));
    fireEvent.click(screen.getByRole("button", { name: "保存并获取模型" }));

    expect(
      await screen.findByText("已获取并保存 1 个模型，模型选择器已刷新"),
    ).toBeInTheDocument();
    fireEvent.click(
      (await screen.findAllByRole("button", { name: /实现跨平台桌面端/ }))[0],
    );
    await waitFor(() =>
      expect(screen.getByLabelText("选择模型")).toHaveTextContent("Discovered model"),
    );
  });

  /**
   * 验证 Provider 保存期间切换后端仍会重握手当前后端，不复用无限新鲜的旧模型快照。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("reinitializes the backend activated during a provider mutation", async () => {
    const timestamp = "2026-07-22T00:00:00Z";
    const providerMethods = [
      ...METHODS,
      "providerCatalog/list",
      "providerConnections/list",
      "providerConnections/create",
    ];
    const providerInitialize: InitializeResult = {
      ...INITIALIZE_RESULT,
      capabilities: {
        ...INITIALIZE_RESULT.capabilities,
        methods: providerMethods,
      },
    };
    const dotnetInitializeResult: InitializeResult = {
      ...INITIALIZE_RESULT,
      implementation: {
        ...INITIALIZE_RESULT.implementation,
        name: "test-dotnet-service",
        language: "dotnet",
      },
    };
    const dotnetReinitialize = createDeferred<InitializeResult>();
    const dotnetInitialize = vi
      .fn<ApplicationServiceClient["initialize"]>()
      .mockResolvedValueOnce(dotnetInitializeResult)
      .mockImplementationOnce(() => dotnetReinitialize.promise);
    const dotnetModels = vi
      .fn<ApplicationServiceClient["listModels"]>()
      .mockResolvedValueOnce([MODEL])
      .mockResolvedValue([REPLACEMENT_MODEL]);
    const pendingCreate = createDeferred<
      Awaited<ReturnType<ApplicationServiceClient["createProviderConnection"]>>
    >();
    const rustInitialize = vi
      .fn<ApplicationServiceClient["initialize"]>()
      .mockResolvedValue(providerInitialize);
    const createProviderConnection = vi.fn(() => pendingCreate.promise);
    const rustService = createTestService({
      initialize: rustInitialize,
      listModels: vi.fn().mockResolvedValue([MODEL]),
      listProfiles: vi.fn().mockResolvedValue([]),
      listProviderCatalog: vi.fn().mockResolvedValue([]),
      listProviderConnections: vi.fn().mockResolvedValue([]),
      createProviderConnection,
    });
    const dotnetService = createTestService({
      initialize: dotnetInitialize,
      listModels: dotnetModels,
      listProfiles: vi.fn().mockResolvedValue([]),
    });
    useWorkspaceUiStore.setState({ backend: "dotnet" });
    renderTestApp(
      (candidateBackend) =>
        candidateBackend === "rust" ? rustService : dotnetService,
      Number.POSITIVE_INFINITY,
    );

    await waitFor(() => {
      expect(screen.getByLabelText("选择模型")).toBeEnabled();
      expect(screen.getByLabelText("选择模型")).toHaveTextContent("Faux v1");
    });
    expect(dotnetInitialize).toHaveBeenCalledTimes(1);
    expect(dotnetModels).toHaveBeenCalledTimes(1);

    act(() => useWorkspaceUiStore.getState().setBackend("rust"));
    await waitFor(() => expect(rustInitialize).toHaveBeenCalledTimes(1));
    fireEvent.click(screen.getByRole("button", { name: "设置" }));
    const providersTab = await screen.findByRole("tab", { name: "提供商" });
    fireEvent.mouseDown(providersTab, { button: 0, ctrlKey: false });
    fireEvent.click(providersTab);
    const importInput = await screen.findByLabelText("导入 New API 连接 JSON");
    fireEvent.change(importInput, {
      target: {
        value:
          '{"_type":"newapi_channel_conn","url":"https://api.example.invalid"}',
      },
    });
    fireEvent.click(screen.getByRole("button", { name: "导入" }));
    fireEvent.change(screen.getByLabelText("模型 ID"), {
      target: { value: "manual-rust-model" },
    });
    fireEvent.click(screen.getByRole("button", { name: "保存" }));
    await waitFor(() =>
      expect(createProviderConnection).toHaveBeenCalledTimes(1),
    );

    act(() => {
      useWorkspaceUiStore.setState({
        backend: "dotnet",
        activeView: "conversation",
      });
    });
    await waitFor(() => expect(screen.getByLabelText("选择模型")).toBeEnabled());
    expect(dotnetInitialize).toHaveBeenCalledTimes(1);
    expect(dotnetModels).toHaveBeenCalledTimes(1);

    act(() =>
      pendingCreate.resolve({
        connection: {
          connectionId: "connection-race",
          revision: 1,
          displayName: "星思研 New API",
          providerId: "newapi-gzxsy",
          baseUrl: "https://api.example.invalid/v1",
          modelsUrl: "https://api.example.invalid/v1/models",
          apiFamily: "openai-completions",
          modelIds: ["manual-rust-model"],
          supportsTools: false,
          supportsImageInput: false,
          supportsImageOutput: false,
          logoAssetId: "newapi-gzxsy",
          enabled: true,
          credentialConfigured: false,
          imageCredentialConfigured: false,
          createdAt: timestamp,
          updatedAt: timestamp,
        },
        restartRequired: true,
      }),
    );
    await waitFor(() => {
      expect(dotnetInitialize).toHaveBeenCalledTimes(2);
      expect(screen.getByLabelText("选择模型")).toBeDisabled();
    });

    act(() => dotnetReinitialize.resolve(dotnetInitializeResult));
    await waitFor(() => {
      expect(dotnetModels).toHaveBeenCalledTimes(2);
      expect(screen.getByLabelText("选择模型")).toBeEnabled();
      expect(screen.getByLabelText("选择模型")).toHaveTextContent(
        "Replacement v1",
      );
    });
  });

  /**
   * 验证迟到的 Rust 模型发现结果不能覆盖已经切换到 .NET 的 Agent 与模型选择。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("ignores late provider discovery after switching backends", async () => {
    const timestamp = "2026-07-22T00:00:00Z";
    const providerMethods = [
      ...METHODS,
      "providerCatalog/list",
      "providerConnections/list",
      "providerConnections/create",
      "providerConnections/discoverModels",
      "providerCredentials/set",
    ];
    const createdConnection = {
      connectionId: "connection-late-discovery",
      revision: 1,
      displayName: "星思研 New API",
      providerId: "newapi-gzxsy",
      baseUrl: "https://api.example.invalid/v1",
      modelsUrl: "https://api.example.invalid/v1/models",
      apiFamily: "openai-completions" as const,
      modelIds: [] as readonly string[],
      supportsTools: false,
      supportsImageInput: false,
      supportsImageOutput: false,
      logoAssetId: "newapi-gzxsy",
      enabled: true,
      credentialConfigured: false,
      imageCredentialConfigured: false,
      createdAt: timestamp,
      updatedAt: timestamp,
    };
    const discoveredConnection = {
      ...createdConnection,
      revision: 2,
      modelIds: [DISCOVERED_MODEL.modelId],
      credentialConfigured: true,
    };
    const providerInitialize: InitializeResult = {
      ...INITIALIZE_RESULT,
      capabilities: {
        ...INITIALIZE_RESULT.capabilities,
        methods: providerMethods,
      },
    };
    const dotnetInitializeResult: InitializeResult = {
      ...INITIALIZE_RESULT,
      implementation: {
        ...INITIALIZE_RESULT.implementation,
        name: "test-dotnet-service",
        language: "dotnet",
      },
    };
    const pendingDiscovery = createDeferred<
      Awaited<ReturnType<ApplicationServiceClient["discoverProviderModels"]>>
    >();
    const rustInitialize = vi
      .fn<ApplicationServiceClient["initialize"]>()
      .mockResolvedValue(providerInitialize);
    const rustDiscovery = vi
      .fn<ApplicationServiceClient["discoverProviderModels"]>()
      .mockImplementation(() => pendingDiscovery.promise);
    const dotnetInitialize = vi
      .fn<ApplicationServiceClient["initialize"]>()
      .mockResolvedValue(dotnetInitializeResult);
    const dotnetModels = vi
      .fn<ApplicationServiceClient["listModels"]>()
      .mockResolvedValue([MODEL, REPLACEMENT_MODEL]);
    const dotnetProfiles = vi
      .fn<ApplicationServiceClient["listProfiles"]>()
      .mockResolvedValue([REPLACEMENT_PROFILE]);
    const rustService = createTestService({
      initialize: rustInitialize,
      listModels: vi.fn().mockResolvedValue([MODEL]),
      listProfiles: vi.fn().mockResolvedValue([]),
      listProviderCatalog: vi.fn().mockResolvedValue([]),
      listProviderConnections: vi.fn().mockResolvedValue([]),
      createProviderConnection: vi.fn().mockResolvedValue({
        connection: createdConnection,
        restartRequired: true,
      }),
      setProviderCredential: vi.fn().mockResolvedValue({
        providerId: createdConnection.providerId,
        credentialConfigured: true,
        restartRequired: true,
      }),
      discoverProviderModels: rustDiscovery,
    });
    const dotnetService = createTestService({
      initialize: dotnetInitialize,
      listModels: dotnetModels,
      listProfiles: dotnetProfiles,
    });
    useWorkspaceUiStore.setState({ backend: "dotnet" });
    const { queryClient } = renderTestApp(
      (candidateBackend) =>
        candidateBackend === "rust" ? rustService : dotnetService,
      Number.POSITIVE_INFINITY,
    );

    await waitFor(() => expect(screen.getByLabelText("选择模型")).toBeEnabled());
    act(() => useWorkspaceUiStore.getState().setBackend("rust"));
    await waitFor(() => expect(rustInitialize).toHaveBeenCalledTimes(1));
    fireEvent.click(screen.getByRole("button", { name: "设置" }));
    const providersTab = await screen.findByRole("tab", { name: "提供商" });
    fireEvent.mouseDown(providersTab, { button: 0, ctrlKey: false });
    fireEvent.click(providersTab);
    const importInput = await screen.findByLabelText("导入 New API 连接 JSON");
    fireEvent.change(importInput, {
      target: {
        value:
          '{"_type":"newapi_channel_conn","key":"late-discovery-secret","url":"https://api.example.invalid"}',
      },
    });
    fireEvent.click(screen.getByRole("button", { name: "导入" }));
    fireEvent.click(screen.getByRole("button", { name: "保存并获取模型" }));
    await waitFor(() => expect(rustDiscovery).toHaveBeenCalledTimes(1));

    act(() => {
      useWorkspaceUiStore.setState({
        backend: "dotnet",
        activeView: "conversation",
      });
    });
    await waitFor(() => expect(screen.getByLabelText("选择模型")).toBeEnabled());
    fireEvent.click(screen.getByLabelText("选择智能体"));
    fireEvent.click(await screen.findByRole("option", { name: "编码助手" }));
    expect(useWorkspaceUiStore.getState().activeAgentProfileId).toBe(
      REPLACEMENT_PROFILE.profileId,
    );
    expect(screen.getByLabelText("选择模型")).toHaveTextContent("Replacement v1");
    const profilesBeforeDiscovery = dotnetProfiles.mock.calls.length;

    act(() =>
      pendingDiscovery.resolve({
        connection: discoveredConnection,
        discoveredCount: 1,
        restartRequired: true,
      }),
    );
    await waitFor(() => {
      expect(dotnetInitialize).toHaveBeenCalledTimes(2);
      expect(dotnetModels).toHaveBeenCalledTimes(2);
      expect(dotnetProfiles).toHaveBeenCalledTimes(profilesBeforeDiscovery + 1);
      expect(queryClient.isFetching()).toBe(0);
    });
    await act(async () => Promise.resolve());

    expect(useWorkspaceUiStore.getState().activeAgentProfileId).toBe(
      REPLACEMENT_PROFILE.profileId,
    );
    expect(screen.getByLabelText("选择模型")).toHaveTextContent("Replacement v1");
  });

  /**
   * 验证重新握手和新代际查询期间保留非首模型与 Agent 偏好，稳定快照未撤销时恢复原选择。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("preserves a valid non-default model and agent across reinitialize", async () => {
    const secondInitialize = createDeferred<InitializeResult>();
    const secondModels = createDeferred<readonly ModelDescriptor[]>();
    const secondProfiles = createDeferred<readonly AgentProfile[]>();
    const initialize = vi
      .fn<ApplicationServiceClient["initialize"]>()
      .mockResolvedValueOnce(INITIALIZE_RESULT)
      .mockImplementationOnce(() => secondInitialize.promise);
    const listModels = vi
      .fn<ApplicationServiceClient["listModels"]>()
      .mockResolvedValueOnce([MODEL, REPLACEMENT_MODEL])
      .mockImplementationOnce(() => secondModels.promise);
    const listProfiles = vi
      .fn<ApplicationServiceClient["listProfiles"]>()
      .mockResolvedValueOnce([REPLACEMENT_PROFILE])
      .mockImplementationOnce(() => secondProfiles.promise);
    const service = createTestService({ initialize, listModels, listProfiles });
    const { queryClient } = renderTestApp(service);

    await waitFor(() => expect(screen.getByLabelText("选择模型")).toBeEnabled());
    fireEvent.click(screen.getByLabelText("选择智能体"));
    fireEvent.click(await screen.findByRole("option", { name: "编码助手" }));
    expect(screen.getByLabelText("选择模型")).toHaveTextContent("Replacement v1");
    expect(useWorkspaceUiStore.getState().activeAgentProfileId).toBe(
      REPLACEMENT_PROFILE.profileId,
    );

    act(() => {
      void queryClient.invalidateQueries({
        queryKey: ["application-service", "rust", "initialize"],
        exact: true,
      });
    });
    await waitFor(() => expect(screen.getByLabelText("选择模型")).toBeDisabled());
    expect(useWorkspaceUiStore.getState().activeAgentProfileId).toBe(
      REPLACEMENT_PROFILE.profileId,
    );

    act(() => secondInitialize.resolve(INITIALIZE_RESULT));
    await waitFor(() => {
      expect(listModels).toHaveBeenCalledTimes(2);
      expect(listProfiles).toHaveBeenCalledTimes(2);
    });
    expect(useWorkspaceUiStore.getState().activeAgentProfileId).toBe(
      REPLACEMENT_PROFILE.profileId,
    );

    act(() => secondModels.resolve([MODEL, REPLACEMENT_MODEL]));
    await waitFor(() => expect(screen.getByLabelText("选择模型")).toBeEnabled());
    expect(screen.getByLabelText("选择模型")).toHaveTextContent("Replacement v1");
    expect(useWorkspaceUiStore.getState().activeAgentProfileId).toBe(
      REPLACEMENT_PROFILE.profileId,
    );

    act(() => secondProfiles.resolve([REPLACEMENT_PROFILE]));
    await waitFor(() =>
      expect(
        queryClient
          .getQueriesData<readonly AgentProfile[]>({
            queryKey: ["agent-profiles", service.mode, "rust"],
          })
          .some(([, profiles]) => profiles?.[0]?.profileId === REPLACEMENT_PROFILE.profileId),
      ).toBe(true),
    );
    expect(useWorkspaceUiStore.getState().activeAgentProfileId).toBe(
      REPLACEMENT_PROFILE.profileId,
    );
    await waitFor(() => {
      expect(useWorkspaceUiStore.getState().activeAgentProfileId).toBe(
        REPLACEMENT_PROFILE.profileId,
      );
      expect(screen.getByLabelText("选择智能体")).toHaveTextContent("编码助手");
    });
    expect(useWorkspaceUiStore.getState().activeAgentProfileId).toBe(
      REPLACEMENT_PROFILE.profileId,
    );
  });

  /**
   * 验证 Agent 模型失效会清除真实绑定，模型恢复后也不会静默重绑。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("does not silently restore an agent after its model route disappears", async () => {
    const listModels = vi
      .fn<ApplicationServiceClient["listModels"]>()
      .mockResolvedValueOnce([MODEL])
      .mockResolvedValueOnce([REPLACEMENT_MODEL])
      .mockResolvedValueOnce([MODEL]);
    const service = createTestService({
      initialize: vi.fn().mockResolvedValue(INITIALIZE_RESULT),
      listModels,
      listProfiles: vi.fn().mockResolvedValue([PROFILE]),
    });
    const { queryClient } = renderTestApp(service);

    await waitFor(() => expect(screen.getByLabelText("选择模型")).toBeEnabled());
    fireEvent.click(screen.getByLabelText("选择智能体"));
    fireEvent.click(await screen.findByRole("option", { name: "编码助手" }));
    expect(useWorkspaceUiStore.getState().activeAgentProfileId).toBe(PROFILE.profileId);

    await act(async () => {
      await queryClient.invalidateQueries({
        queryKey: ["application-service", "rust", "initialize"],
        exact: true,
      });
    });
    await waitFor(() => {
      expect(useWorkspaceUiStore.getState().activeAgentProfileId).toBeNull();
    });
    expect(screen.getByLabelText("选择智能体")).toHaveTextContent("默认智能体");

    await act(async () => {
      await queryClient.invalidateQueries({
        queryKey: ["application-service", "rust", "initialize"],
        exact: true,
      });
    });
    await waitFor(() => expect(screen.getByLabelText("选择模型")).toBeEnabled());
    expect(screen.getByLabelText("选择智能体")).toHaveTextContent("默认智能体");
    expect(useWorkspaceUiStore.getState().activeAgentProfileId).toBeNull();
  });

  /**
   * 验证审批提交会锁住全部卡，接受后的快照失败只允许刷新而不会重复决定。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("locks multiple approvals and retries a failed durable refresh", async () => {
    const response = createDeferred<void>();
    const getSession = vi
      .fn<ApplicationServiceClient["getSession"]>()
      .mockResolvedValueOnce(PENDING_APPROVAL_SNAPSHOT)
      .mockRejectedValueOnce(new Error("snapshot failed"))
      .mockResolvedValueOnce(FIRST_APPROVAL_RESOLVED_SNAPSHOT);
    const respondToApproval = vi.fn(() => response.promise);
    const service = createTestService({
      initialize: vi.fn().mockResolvedValue(INITIALIZE_RESULT),
      listModels: vi.fn().mockResolvedValue([MODEL]),
      listProfiles: vi.fn().mockResolvedValue([]),
      listSessions: vi.fn().mockResolvedValue([APPROVAL_SESSION]),
      getSession,
      respondToApproval,
    });
    useWorkspaceUiStore.setState({ activeSessionId: APPROVAL_SESSION.sessionId });
    renderTestApp(service);

    expect(await screen.findAllByRole("heading", { name: "需要审批" })).toHaveLength(2);
    fireEvent.click(
      screen.getByRole("button", { name: "对 file.write 选择允许一次" }),
    );
    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: "对 process.exec 选择允许一次" }),
      ).toBeDisabled();
    });

    act(() => response.resolve());
    expect(await screen.findByText(/决定已被接受，但无法刷新审批状态/)).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "对 file.write 选择允许一次" }),
    ).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "对 process.exec 选择允许一次" }),
    ).toBeDisabled();
    expect(respondToApproval).toHaveBeenCalledTimes(1);

    fireEvent.click(
      screen.getByRole("button", { name: "刷新 file.write 的审批状态" }),
    );
    await waitFor(() => expect(screen.queryByText("file.write")).not.toBeInTheDocument());
    expect(screen.getByText("process.exec")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "对 process.exec 选择允许一次" }),
    ).toBeEnabled();
    expect(respondToApproval).toHaveBeenCalledTimes(1);
  });

  /**
   * 验证待审批 Session 首次读取失败后可显式重试并恢复允许与拒绝按钮。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("retries a failed approval session snapshot", async () => {
    const getSession = vi
      .fn<ApplicationServiceClient["getSession"]>()
      .mockRejectedValueOnce(new Error("snapshot unavailable"))
      .mockResolvedValueOnce(PENDING_APPROVAL_SNAPSHOT);
    const service = createTestService({
      initialize: vi.fn().mockResolvedValue(INITIALIZE_RESULT),
      listModels: vi.fn().mockResolvedValue([MODEL]),
      listProfiles: vi.fn().mockResolvedValue([]),
      listSessions: vi.fn().mockResolvedValue([APPROVAL_SESSION]),
      getSession,
    });
    useWorkspaceUiStore.setState({ activeSessionId: APPROVAL_SESSION.sessionId });
    renderTestApp(service);

    fireEvent.click(await screen.findByRole("button", { name: "重试读取" }));

    expect(await screen.findAllByRole("heading", { name: "需要审批" })).toHaveLength(2);
    expect(
      screen.getByRole("button", { name: "对 file.write 选择允许一次" }),
    ).toBeEnabled();
    expect(
      screen.getByRole("button", { name: "对 file.write 选择拒绝" }),
    ).toBeEnabled();
    expect(getSession).toHaveBeenCalledTimes(2);
  });

  /**
   * 验证即使审批卡尚未收到计时器重渲染，提交边界仍会二次拒绝已过期请求。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rechecks approval expiry at the response boundary", async () => {
    const respondToApproval = vi.fn().mockResolvedValue(undefined);
    const service = createTestService({
      initialize: vi.fn().mockResolvedValue(INITIALIZE_RESULT),
      listModels: vi.fn().mockResolvedValue([MODEL]),
      listProfiles: vi.fn().mockResolvedValue([]),
      listSessions: vi.fn().mockResolvedValue([APPROVAL_SESSION]),
      getSession: vi.fn().mockResolvedValue(PENDING_APPROVAL_SNAPSHOT),
      respondToApproval,
    });
    useWorkspaceUiStore.setState({ activeSessionId: APPROVAL_SESSION.sessionId });
    renderTestApp(service);

    const allowButton = await screen.findByRole("button", {
      name: "对 file.write 选择允许一次",
    });
    vi.spyOn(Date, "now").mockReturnValue(Date.parse("2100-01-01T00:00:00Z"));
    fireEvent.click(allowButton);

    expect(respondToApproval).not.toHaveBeenCalled();
  });
});
