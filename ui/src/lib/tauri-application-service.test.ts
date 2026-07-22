import { beforeEach, describe, expect, it, vi } from "vitest";

const invokeMock = vi.hoisted(() => vi.fn());

vi.mock("@tauri-apps/api/core", () => ({
  invoke: invokeMock,
}));

import { TauriApplicationServiceClient } from "@/lib/tauri-application-service";

const PROFILE = {
  profileId: "profile-1",
  revision: 3,
  displayName: "仓库助手",
  description: "审阅并实现仓库任务",
  enabled: true,
  instructions: "先读取约束，再完成实现。",
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
  createdAt: "2026-07-21T00:00:00Z",
  updatedAt: "2026-07-21T00:01:00Z",
} as const;

describe("TauriApplicationServiceClient", () => {
  beforeEach(() => {
    invokeMock.mockReset();
  });

  /**
   * 验证桌面 client 使用生产 Profile RPC，并把精确 revision 绑定到 run/start。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("uses profile RPC and binds the selected revision to run/start", async () => {
    invokeMock.mockImplementation((_command: string, arguments_: Record<string, unknown>) => {
      if (arguments_.method === "agentProfiles/list") {
        return Promise.resolve({ profiles: [PROFILE] });
      }
      if (arguments_.method === "run/start") {
        return Promise.resolve({ runId: "run-1" });
      }
      throw new Error("unexpected command");
    });
    const client = new TauriApplicationServiceClient("rust");

    expect(await client.listProfiles()).toEqual([PROFILE]);
    await client.startRun({
      sessionId: "session-1",
      prompt: "检查实现",
      model: PROFILE.model,
      agentProfile: { profileId: PROFILE.profileId, revision: PROFILE.revision },
    });

    expect(invokeMock).toHaveBeenLastCalledWith("application_service_request", {
      backend: "rust",
      method: "run/start",
      params: {
        sessionId: "session-1",
        input: { role: "user", content: [{ type: "text", text: "检查实现" }] },
        provider: { id: "faux", modelId: "faux-v1", apiFamily: "faux" },
        agentProfile: { profileId: "profile-1", revision: 3 },
      },
    });
  });

  /**
   * 验证输入图片只通过品牌中立 artifacts/create 发布，并严格解析脱敏引用。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("publishes input images through artifacts/create", async () => {
    const artifact = {
      artifactId: "artifact-input-1",
      mediaType: "image/png",
      byteLength: 8,
      sha256: "0".repeat(64),
    };
    invokeMock.mockResolvedValue({ artifact });
    const client = new TauriApplicationServiceClient("dotnet");

    await expect(
      client.createArtifact({
        sessionId: "session-input-1",
        mediaType: "image/png",
        dataBase64: "iVBORw0KGgo=",
      }),
    ).resolves.toEqual({ artifact });
    expect(invokeMock).toHaveBeenCalledWith("application_service_request", {
      backend: "dotnet",
      method: "artifacts/create",
      params: {
        sessionId: "session-input-1",
        mediaType: "image/png",
        dataBase64: "iVBORw0KGgo=",
      },
    });
  });

  /**
   * 验证审批决定只通过 allowlist 内的 approval/respond 提交，并要求 accepted 回执。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("submits a bounded approval decision through the application service", async () => {
    invokeMock.mockResolvedValue({ accepted: true });
    const client = new TauriApplicationServiceClient("dotnet");

    await client.respondToApproval(
      "session-1",
      "run-1",
      "approval-1",
      { choice: "allow_once" },
    );

    expect(invokeMock).toHaveBeenCalledWith("application_service_request", {
      backend: "dotnet",
      method: "approval/respond",
      params: {
        sessionId: "session-1",
        runId: "run-1",
        approvalId: "approval-1",
        decision: { choice: "allow_once" },
      },
    });
  });

  /**
   * 验证 Provider 模板只从后端目录 RPC 读取，并保持模板与可执行模型语义分离。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("reads provider templates from the backend catalog", async () => {
    invokeMock.mockResolvedValue({
      templates: [
        {
          templateId: "deepseek-openai-completions",
          displayName: "DeepSeek",
          suggestedProviderId: "custom-deepseek",
          apiFamily: "openai-completions",
          defaultBaseUrl: "https://api.deepseek.com/v1",
          modelDiscovery: "openai-models",
          logoAssetId: null,
        },
      ],
    });
    const client = new TauriApplicationServiceClient("rust");

    await expect(client.listProviderCatalog()).resolves.toHaveLength(1);
    expect(invokeMock).toHaveBeenCalledWith("application_service_request", {
      backend: "rust",
      method: "providerCatalog/list",
      params: {},
    });
  });

  /**
   * 验证桌面传输边界按共同 Schema 接受包含点号的 Logo 资源 ID。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("transports dotted provider logo asset identifiers", async () => {
    const connectionInput = {
      displayName: "Example Provider",
      providerId: "custom.example",
      baseUrl: "https://api.example.invalid/v1",
      modelsUrl: "https://api.example.invalid/catalog/models",
      apiFamily: "openai-completions",
      modelIds: ["model-a"],
      supportsTools: true,
      logoAssetId: "custom.brand.dark",
      enabled: true,
    } as const;
    invokeMock.mockResolvedValue({
      connection: {
        connectionId: "connection-1",
        revision: 1,
        ...connectionInput,
        credentialConfigured: false,
        imageCredentialConfigured: false,
        createdAt: "2026-07-21T00:00:00Z",
        updatedAt: "2026-07-21T00:00:00Z",
      },
      restartRequired: true,
    });
    const client = new TauriApplicationServiceClient("rust");

    const result = await client.createProviderConnection(connectionInput);

    expect(result.connection.logoAssetId).toBe("custom.brand.dark");
    expect(invokeMock).toHaveBeenCalledWith("application_service_request", {
      backend: "rust",
      method: "providerConnections/create",
      params: { connection: connectionInput },
    });
  });

  /**
   * 验证模型发现只发送连接 CAS 标识，并严格校验返回计数与脱敏 allowlist。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("discovers provider models without sending credentials from React", async () => {
    invokeMock.mockResolvedValue({
      connection: {
        connectionId: "connection-1",
        revision: 2,
        displayName: "Example Provider",
        providerId: "custom.example",
        baseUrl: "https://api.example.invalid/v1",
        modelsUrl: "https://api.example.invalid/catalog/models",
        apiFamily: "openai-completions",
        modelIds: ["model-a", "model-b"],
        supportsTools: false,
        logoAssetId: null,
        enabled: true,
        credentialConfigured: true,
        imageCredentialConfigured: false,
        createdAt: "2026-07-21T00:00:00Z",
        updatedAt: "2026-07-22T00:00:00Z",
      },
      discoveredCount: 2,
      restartRequired: true,
    });
    const client = new TauriApplicationServiceClient("dotnet");

    const result = await client.discoverProviderModels("connection-1", 1);

    expect(result.discoveredCount).toBe(2);
    expect(invokeMock).toHaveBeenCalledWith("application_service_request", {
      backend: "dotnet",
      method: "providerConnections/discoverModels",
      params: { connectionId: "connection-1", expectedRevision: 1 },
    });
    expect(JSON.stringify(invokeMock.mock.calls)).not.toMatch(/credential|apiKey|secret/i);
  });

  /**
   * 验证 host 即使返回额外 secret 字段，前端严格 schema 仍拒绝该投影。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects profile projections containing unknown secret fields", async () => {
    invokeMock.mockResolvedValue({
      profiles: [{ ...PROFILE, apiKey: "must-not-enter-ui" }],
    });
    const client = new TauriApplicationServiceClient("dotnet");

    await expect(client.listProfiles()).rejects.toThrow();
  });

  /**
   * 验证桌面边界执行共同 Schema 的工具唯一性与 UTC 时间格式。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects profile projections outside the shared schema", async () => {
    invokeMock.mockResolvedValue({
      profiles: [
        {
          ...PROFILE,
          requestedToolIds: ["file.read", "file.read"],
          updatedAt: "2026-07-21T08:01:00+08:00",
        },
      ],
    });
    const client = new TauriApplicationServiceClient("rust");

    await expect(client.listProfiles()).rejects.toThrow();
  });

  /**
   * 验证 Provider secret 只通过专用 Tauri command 进入 host 边界。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("uses dedicated host commands for provider credentials", async () => {
    invokeMock.mockImplementation((command: string, arguments_?: unknown) =>
      Promise.resolve({
        providerId: "newapi",
        credentialKind: (arguments_ as { credentialKind: string }).credentialKind,
        credentialConfigured: command === "provider_credential_set",
        restartRequired: true,
      }),
    );
    const client = new TauriApplicationServiceClient("rust");

    await client.setProviderCredential("newapi", "responses", "test-secret");
    await client.removeProviderCredential("newapi", "image");

    expect(invokeMock).toHaveBeenNthCalledWith(1, "provider_credential_set", {
      backend: "rust",
      providerId: "newapi",
      credentialKind: "responses",
      credential: "test-secret",
    });
    expect(invokeMock).toHaveBeenNthCalledWith(2, "provider_credential_remove", {
      backend: "rust",
      providerId: "newapi",
      credentialKind: "image",
    });
  });

  /**
   * 验证桌面 client 接受协议允许的 16 KiB credential，并在调用 host 前拒绝超限值。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("enforces the shared provider credential length boundary", async () => {
    invokeMock.mockResolvedValue({
      providerId: "newapi",
      credentialKind: "responses",
      credentialConfigured: true,
      restartRequired: true,
    });
    const client = new TauriApplicationServiceClient("rust");
    const maximumCredential = "x".repeat(16_384);

    await client.setProviderCredential("newapi", "responses", maximumCredential);
    const arguments_ = invokeMock.mock.calls[0]?.[1] as
      | { credential?: string }
      | undefined;
    expect(arguments_?.credential).toHaveLength(16_384);
    await expect(
      client.setProviderCredential("newapi", "responses", "x".repeat(16_385)),
    ).rejects.toThrow();
    expect(invokeMock).toHaveBeenCalledTimes(1);
  });

  /**
   * 验证 session/get 使用可选 afterSeq，并解析三种 portable 消息与严格事件增量。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("parses strict session message snapshots and replay events", async () => {
    invokeMock.mockResolvedValue({
      sessionId: "session-1",
      latestSeq: 5,
      activeRunId: null,
      selectedHeadRecordId: "record-3",
      messages: [
        {
          messageId: "message-user",
          role: "user",
          content: [{ type: "text", text: "检查消息恢复" }],
          time: "2026-07-21T01:00:00Z",
        },
        {
          messageId: "message-assistant",
          role: "assistant",
          content: [
            { type: "reasoning", text: "读取严格快照", redacted: false },
            {
              type: "tool_call",
              toolCallId: "call-1",
              name: "file.read",
              arguments: { path: "README.md" },
            },
          ],
          provider: { id: "faux", modelId: "faux-v1", apiFamily: "faux" },
          finishReason: "tool_use",
          usage: { inputTokens: 3, outputTokens: 2, totalTokens: 5 },
          time: "2026-07-21T01:00:01Z",
        },
        {
          messageId: "message-tool",
          role: "tool",
          toolCallId: "call-1",
          toolName: "file.read",
          content: [{ type: "text", text: "文件内容" }],
          isError: false,
          time: "2026-07-21T01:00:02Z",
        },
      ],
      events: [
        {
          sessionId: "session-1",
          runId: "run-1",
          seq: 5,
          time: "2026-07-21T01:00:03Z",
          type: "run.completed",
          data: {
            status: "completed",
            usage: { inputTokens: 3, outputTokens: 2, totalTokens: 5 },
          },
        },
      ],
    });
    const client = new TauriApplicationServiceClient("dotnet");

    const snapshot = await client.getSession("session-1", 4);

    expect(snapshot.messages.map((message) => message.role)).toEqual([
      "user",
      "assistant",
      "tool",
    ]);
    expect(snapshot.events.map((event) => event.seq)).toEqual([5]);
    expect(invokeMock).toHaveBeenCalledWith("application_service_request", {
      backend: "dotnet",
      method: "session/get",
      params: { sessionId: "session-1", afterSeq: 4 },
    });
  });

  /**
   * 验证 session/get 不允许消息、事件或顶层投影夹带共同 Schema 未声明字段。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects session snapshots containing unknown message fields", async () => {
    invokeMock.mockResolvedValue({
      sessionId: "session-1",
      latestSeq: 0,
      activeRunId: null,
      messages: [
        {
          messageId: "message-user",
          role: "user",
          content: [{ type: "text", text: "消息", apiKey: "must-not-enter-ui" }],
          time: "2026-07-21T01:00:00Z",
        },
      ],
      events: [],
    });
    const client = new TauriApplicationServiceClient("rust");

    await expect(client.getSession("session-1")).rejects.toThrow();
  });

  /**
   * 验证 session/get 会拒绝事件 data 未声明字段和不满足 afterSeq 的重放序号。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects invalid session replay event projections", async () => {
    invokeMock.mockResolvedValue({
      sessionId: "session-1",
      latestSeq: 4,
      activeRunId: null,
      messages: [],
      events: [
        {
          sessionId: "session-1",
          runId: "run-1",
          seq: 4,
          time: "2026-07-21T01:00:03Z",
          type: "run.completed",
          data: {
            status: "completed",
            usage: { inputTokens: 1, outputTokens: 1, totalTokens: 2 },
            apiKey: "must-not-enter-ui",
          },
        },
      ],
    });
    const client = new TauriApplicationServiceClient("rust");

    await expect(client.getSession("session-1", 4)).rejects.toThrow();
  });

  /**
   * 验证 session/list 严格遍历 opaque cursor，并保持服务端分页顺序。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("loads every strict session list page", async () => {
    invokeMock
      .mockResolvedValueOnce({
        sessions: [
          {
            sessionId: "session-1",
            title: "第一条会话",
            project: "AI-Code",
            updatedAt: "2026-07-21T01:00:00Z",
            archived: false,
          },
        ],
        nextCursor: "v1:1",
        hasMore: true,
      })
      .mockResolvedValueOnce({
        sessions: [
          {
            sessionId: "session-1",
            title: "第一条会话的重复投影",
            project: "AI-Code",
            updatedAt: "2026-07-21T01:02:00Z",
            archived: false,
          },
          {
            sessionId: "session-2",
            title: "第二条会话",
            project: "AI-Code",
            updatedAt: "2026-07-21T01:01:00Z",
            archived: true,
          },
        ],
        nextCursor: null,
        hasMore: false,
      });
    const client = new TauriApplicationServiceClient("dotnet");

    const sessions = await client.listSessions();

    expect(sessions.map((session) => session.sessionId)).toEqual(["session-1", "session-2"]);
    expect(sessions[0]?.title).toBe("第一条会话");
    expect(invokeMock).toHaveBeenNthCalledWith(1, "application_service_request", {
      backend: "dotnet",
      method: "session/list",
      params: { limit: 128 },
    });
    expect(invokeMock).toHaveBeenNthCalledWith(2, "application_service_request", {
      backend: "dotnet",
      method: "session/list",
      params: { limit: 128, cursor: "v1:1" },
    });
  });

  /**
   * 验证 session/list 拒绝互相矛盾的终页状态。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects inconsistent session list page state", async () => {
    invokeMock.mockResolvedValue({
      sessions: [],
      nextCursor: "v1:1",
      hasMore: false,
    });
    const client = new TauriApplicationServiceClient("rust");

    await expect(client.listSessions()).rejects.toThrow();
  });

  /**
   * 验证 session/list 严格拒绝后端夹带的未知敏感字段。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects unknown session list page fields", async () => {
    invokeMock.mockResolvedValue({
      sessions: [],
      nextCursor: null,
      hasMore: false,
      credential: "must-not-enter-ui",
    });
    const client = new TauriApplicationServiceClient("rust");

    await expect(client.listSessions()).rejects.toThrow();
  });

  /**
   * 验证 session/list cursor 只接受共同契约允许的有界 opaque 字符。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects invalid session list cursors", async () => {
    invokeMock.mockResolvedValue({
      sessions: [
        {
          sessionId: "session-1",
          title: "会话",
          project: "AI-Code",
          updatedAt: "2026-07-21T01:00:00Z",
          archived: false,
        },
      ],
      nextCursor: "cursor with spaces",
      hasMore: true,
    });
    const client = new TauriApplicationServiceClient("rust");

    await expect(client.listSessions()).rejects.toThrow();
    expect(invokeMock).toHaveBeenCalledTimes(1);
  });

  /**
   * 验证 session/list 会拒绝超出共同摘要长度边界的展示字段。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects oversized session summaries", async () => {
    invokeMock.mockResolvedValue({
      sessions: [
        {
          sessionId: "session-1",
          title: "会".repeat(97),
          project: "AI-Code",
          updatedAt: "2026-07-21T01:00:00Z",
          archived: false,
        },
      ],
      nextCursor: null,
      hasMore: false,
    });
    const client = new TauriApplicationServiceClient("dotnet");

    await expect(client.listSessions()).rejects.toThrow();
  });

  /**
   * 验证 session/list 在服务重复 cursor 时失败关闭，不会无限读取。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects repeated session list cursors", async () => {
    invokeMock.mockResolvedValue({
      sessions: [
        {
          sessionId: "session-1",
          title: "重复页",
          project: "AI-Code",
          updatedAt: "2026-07-21T01:00:00Z",
          archived: false,
        },
      ],
      nextCursor: "v1:1",
      hasMore: true,
    });
    const client = new TauriApplicationServiceClient("dotnet");

    await expect(client.listSessions()).rejects.toThrow(
      "Session list pagination cursor did not advance",
    );
    expect(invokeMock).toHaveBeenCalledTimes(2);
  });

  /**
   * 验证 session/list 拒绝只有重复 Session 的非终页，避免 live view 空转。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects session list pages without unique progress", async () => {
    invokeMock
      .mockResolvedValueOnce({
        sessions: [
          {
            sessionId: "session-1",
            title: "会话",
            project: "AI-Code",
            updatedAt: "2026-07-21T01:00:00Z",
            archived: false,
          },
        ],
        nextCursor: "v1:1",
        hasMore: true,
      })
      .mockResolvedValueOnce({
        sessions: [
          {
            sessionId: "session-1",
            title: "重复会话",
            project: "AI-Code",
            updatedAt: "2026-07-21T01:01:00Z",
            archived: false,
          },
        ],
        nextCursor: "v1:2",
        hasMore: true,
      });
    const client = new TauriApplicationServiceClient("rust");

    await expect(client.listSessions()).rejects.toThrow(
      "Session list pagination did not add any sessions",
    );
    expect(invokeMock).toHaveBeenCalledTimes(2);
  });

  /**
   * 验证 session/list 在服务持续声明下一页时受固定页数上限约束。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects session lists exceeding the client page limit", async () => {
    invokeMock.mockImplementation(
      (_command: string, arguments_: { params?: { cursor?: string } }) => {
        const currentOffset = Number(arguments_.params?.cursor?.slice(3) ?? "0");
        const nextOffset = currentOffset + 1;
        return Promise.resolve({
          sessions: [
            {
              sessionId: `session-${nextOffset}`,
              title: `会话 ${nextOffset}`,
              project: "AI-Code",
              updatedAt: "2026-07-21T01:00:00Z",
              archived: false,
            },
          ],
          nextCursor: `v1:${nextOffset}`,
          hasMore: true,
        });
      },
    );
    const client = new TauriApplicationServiceClient("dotnet");

    await expect(client.listSessions()).rejects.toThrow(
      "Session list pagination exceeded the client page limit",
    );
    expect(invokeMock).toHaveBeenCalledTimes(256);
  });
});
