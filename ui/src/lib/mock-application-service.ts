import type {
  ApplicationServiceClient,
  BackendKind,
  InitializeResult,
  ModelDescriptor,
  RunStartInput,
  RunStartResult,
} from "@/types/application-service";
import { createFauxAgentProfileService } from "@/lib/faux-agent-profile-service";
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
  "session/branch/select",
  "session/compact",
  "agentProfiles/list",
  "agentProfiles/create",
  "agentProfiles/update",
  "agentProfiles/delete",
] as const;

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

  /**
   * 创建指定实现画像的预览客户端。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public constructor(backend: BackendKind) {
    this.#backend = backend;
    this.#profiles = createFauxAgentProfileService(backend, []);
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
    const rustOnlyMethods = this.#backend === "rust" ? ["run/steer", "run/followUp"] : [];

    return {
      implementation: {
        name: this.#backend === "rust" ? "qxnm-forge-rust" : "qxnm-forge-dotnet",
        version: "0.1.0-preview",
        language: this.#backend,
      },
      capabilities: {
        methods: [...SHARED_METHODS, ...rustOnlyMethods],
        eventTypes: ["run.started", "message.delta", "message.completed", "run.completed"],
        tools: [],
      },
    };
  }

  /**
   * 返回公开 faux Provider 的确定性模型。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async listModels(): Promise<readonly ModelDescriptor[]> {
    await wait(120);
    return [
      {
        providerId: "faux",
        modelId: "faux-v1",
        apiFamily: "faux",
        displayName: "Faux v1",
        supportsReasoning: false,
        supportsTools: true,
      },
    ];
  }

  /**
   * 接受一次预览运行并返回临时引用。
   *
   * 输入：非空 prompt 与服务端模型三元标识。
   * 输出：仅用于当前预览生命周期的 run 引用。
   * 失败：prompt 为空时拒绝请求。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public async startRun(input: RunStartInput): Promise<RunStartResult> {
    if (input.prompt.trim().length === 0) {
      throw new Error("prompt must not be empty");
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
