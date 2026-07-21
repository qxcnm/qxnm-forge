/** 可选择的独立后端实现。 */
export type BackendKind = "rust" | "dotnet";

/** UI 可使用的运行位置。 */
export type RuntimeMode = "browser-preview" | "desktop-local" | "remote-service";

/** 后端初始化后返回的实现信息。 */
export interface ImplementationInfo {
  readonly name: string;
  readonly version: string;
  readonly language: "rust" | "dotnet";
}

/** 后端通过协议广告的能力集合。 */
export interface ServerCapabilities {
  readonly methods: readonly string[];
  readonly eventTypes: readonly string[];
  readonly tools: readonly string[];
}

/** 中立 application service 的初始化投影。 */
export interface InitializeResult {
  readonly implementation: ImplementationInfo;
  readonly capabilities: ServerCapabilities;
}

/** Provider 模型的稳定三元标识与展示信息。 */
export interface ModelDescriptor {
  readonly providerId: string;
  readonly modelId: string;
  readonly apiFamily: string;
  readonly displayName: string;
  readonly supportsReasoning: boolean;
  readonly supportsTools: boolean;
}

/** 启动一次运行所需的最小输入。 */
export interface RunStartInput {
  readonly sessionId: string;
  readonly prompt: string;
  readonly model: Pick<ModelDescriptor, "providerId" | "modelId" | "apiFamily">;
  readonly agentProfile?: {
    readonly profileId: string;
    readonly revision: number;
  };
}

/** 服务接受运行后的稳定引用。 */
export interface RunStartResult {
  readonly runId: string;
}

/** 桌面壳或浏览器所在平台的能力投影。 */
export interface RuntimeEnvironment {
  readonly platform: string;
  readonly mode: RuntimeMode;
  readonly supportsLocalDaemon: boolean;
}

/**
 * React 只能依赖此中立客户端，不得越过 application service 访问宿主状态。
 */
export interface ApplicationServiceClient extends AgentProfileService {
  /**
   * 建立协议会话并返回服务端能力。
   *
   * 输入：无，连接与凭据由实现边界预先配置。
   * 输出：实现身份以及服务实际广告的方法、事件与工具集合。
   * 不变量：前端只能按返回能力收窄 UI，不得伪造或补全 capability。
   * 失败：传输、认证、协议版本或响应校验失败时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  initialize(): Promise<InitializeResult>;

  /**
   * 读取服务端可用模型，不从前端配置中推断。
   *
   * 输入：无。
   * 输出：包含 Provider、modelId 与 API family 完整身份的只读列表。
   * 不变量：同名模型不能合并，前端不得从显示名重建路由。
   * 失败：传输、认证或响应校验失败时拒绝，不返回推断列表。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  listModels(): Promise<readonly ModelDescriptor[]>;

  /**
   * 请求后端开始一次 Agent 运行。
   *
   * 输入：稳定 Session、非空 prompt 与服务端模型三元标识。
   * 输出：服务已接受请求的 runId 和时间；不代表运行已完成。
   * 不变量：实现不得把未声明的 Agent Profile 字段塞入扩展或提升权限。
   * 失败：输入、模型、Session、策略、传输或服务并发限制不满足时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  startRun(input: RunStartInput): Promise<RunStartResult>;

  /**
   * 请求后端取消指定运行。
   *
   * 输入：服务签发的稳定 runId。
   * 输出：取消请求被服务处理后无返回值，不承诺进程已立即退出。
   * 不变量：不得取消其他连接或 Session 中未授权的运行。
   * 失败：runId 无效、运行已终结、无权限或传输失败时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  cancelRun(sessionId: string, runId: string): Promise<void>;
}
import type { AgentProfileService } from "@/types/agent-profile";
