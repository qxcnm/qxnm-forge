import type { AgentProfileService } from "@/types/agent-profile";

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

/** Provider 连接允许保存的非敏感配置输入。 */
export interface ProviderConnectionInput {
  readonly displayName: string;
  readonly providerId: string;
  readonly baseUrl: string;
  readonly apiFamily: "openai-completions";
  readonly modelIds: readonly string[];
  readonly logoAssetId: string | null;
  readonly enabled: boolean;
}

/** Provider 连接的脱敏投影，永不包含凭据正文。 */
export interface ProviderConnection extends ProviderConnectionInput {
  readonly connectionId: string;
  readonly revision: number;
  readonly credentialConfigured: boolean;
  readonly createdAt: string;
  readonly updatedAt: string;
}

/** Provider 连接创建或更新后的严格响应。 */
export interface ProviderConnectionMutationResult {
  readonly connection: ProviderConnection;
  readonly restartRequired: true;
}

/** Provider 连接删除后的严格响应。 */
export interface ProviderConnectionDeleteResult {
  readonly deleted: true;
  readonly restartRequired: true;
}

/** Provider 凭据写入或移除后的最小状态投影。 */
export interface ProviderCredentialStatus {
  readonly providerId: string;
  readonly credentialConfigured: boolean;
  readonly restartRequired: true;
}

/** 协议扩展字段的只读 JSON 对象投影。 */
export type ProtocolExtensions = Readonly<Record<string, unknown>>;

/** Session 消息引用的不可变 artifact 元数据。 */
export interface SessionArtifactReference {
  readonly artifactId: string;
  readonly mediaType: string;
  readonly byteLength: number;
  readonly sha256: string;
  readonly displayName?: string;
  readonly extensions?: ProtocolExtensions;
}

/** Session 消息中的文本内容块。 */
export interface SessionTextContent {
  readonly type: "text";
  readonly text: string;
  readonly extensions?: ProtocolExtensions;
}

/** Session assistant 消息中的 reasoning 内容块。 */
export interface SessionReasoningContent {
  readonly type: "reasoning";
  readonly text: string;
  readonly redacted?: boolean;
  readonly signature?: string;
  readonly extensions?: ProtocolExtensions;
}

/** Session 消息中的图片 artifact 引用。 */
export interface SessionImageContent {
  readonly type: "image_ref";
  readonly artifact: SessionArtifactReference;
  readonly alt?: string;
  readonly extensions?: ProtocolExtensions;
}

/** Session user/tool 消息中的普通 artifact 引用。 */
export interface SessionArtifactContent {
  readonly type: "artifact_ref";
  readonly artifact: SessionArtifactReference;
  readonly extensions?: ProtocolExtensions;
}

/** Session assistant 消息中的结构化工具调用。 */
export interface SessionToolCallContent {
  readonly type: "tool_call";
  readonly toolCallId: string;
  readonly name: string;
  readonly arguments: Readonly<Record<string, unknown>>;
  readonly extensions?: ProtocolExtensions;
}

/** user 消息允许的 portable 内容块。 */
export type SessionInputContent =
  | SessionTextContent
  | SessionImageContent
  | SessionArtifactContent;

/** assistant 消息允许的 portable 内容块。 */
export type SessionAssistantContent =
  | SessionTextContent
  | SessionReasoningContent
  | SessionImageContent
  | SessionToolCallContent;

/** Session 消息记录的完整 Provider 路由身份。 */
export interface SessionProviderSelection {
  readonly id: string;
  readonly modelId: string;
  readonly apiFamily?: string;
  readonly extensions?: ProtocolExtensions;
}

/** Session assistant 消息记录的规范化 token 与费用用量。 */
export interface SessionUsage {
  readonly inputTokens: number;
  readonly outputTokens: number;
  readonly totalTokens: number;
  readonly cachedInputTokens?: number;
  readonly cacheWriteTokens?: number;
  readonly reasoningTokens?: number;
  readonly cost?: {
    readonly currency: string;
    readonly amount: string;
  };
  readonly extensions?: ProtocolExtensions;
}

/** Session 中可持久化 portable error 的闭合 details 投影。 */
export interface SessionErrorDetails {
  readonly kind: string;
  readonly httpStatus?: number;
  readonly retryAfterMs?: number;
  readonly field?: string;
  readonly operation?: string;
  readonly providerId?: string;
  readonly modelId?: string;
  readonly apiFamily?: string;
  readonly expectedRevision?: number;
  readonly currentRevision?: number;
  readonly toolName?: string;
  readonly path?: string;
  readonly supportedVersions?: readonly string[];
  readonly limit?: number;
  readonly observed?: number;
  readonly exitCode?: number | null;
  readonly signal?: string;
  readonly resourceId?: string;
  readonly extensions?: ProtocolExtensions;
}

/** Session assistant 消息可携带的 portable error。 */
export interface SessionPortableError {
  readonly code: number;
  readonly message: string;
  readonly retryable: boolean;
  readonly details: SessionErrorDetails;
}

/** session/get 返回的 portable user 消息。 */
export interface SessionUserMessage {
  readonly messageId: string;
  readonly role: "user";
  readonly content: readonly SessionInputContent[];
  readonly time: string;
  readonly extensions?: ProtocolExtensions;
}

/** session/get 返回的 portable assistant 消息。 */
export interface SessionAssistantMessage {
  readonly messageId: string;
  readonly role: "assistant";
  readonly content: readonly SessionAssistantContent[];
  readonly provider: SessionProviderSelection;
  readonly responseId?: string;
  readonly finishReason: "stop" | "length" | "tool_use" | "error" | "cancelled" | "interrupted";
  readonly usage: SessionUsage;
  readonly error?: SessionPortableError;
  readonly time: string;
  readonly extensions?: ProtocolExtensions;
}

/** session/get 返回的 portable tool result 消息。 */
export interface SessionToolResultMessage {
  readonly messageId: string;
  readonly role: "tool";
  readonly toolCallId: string;
  readonly toolName: string;
  readonly content: readonly SessionInputContent[];
  readonly isError: boolean;
  readonly time: string;
  readonly extensions?: ProtocolExtensions;
}

/** session/get 完整 selected-branch 消息投影。 */
export type SessionMessage =
  | SessionUserMessage
  | SessionAssistantMessage
  | SessionToolResultMessage;

/** session/get 可选返回的 durable event envelope。 */
export interface SessionReplayEvent {
  readonly sessionId: string;
  readonly runId: string;
  readonly turnId?: string;
  readonly seq: number;
  readonly time: string;
  readonly type:
    | "run.started"
    | "turn.started"
    | "turn.completed"
    | "message.started"
    | "message.delta"
    | "message.completed"
    | "tool.requested"
    | "approval.requested"
    | "approval.resolved"
    | "tool.started"
    | "tool.delta"
    | "tool.completed"
    | "retry.scheduled"
    | "context.compacted"
    | "run.completed"
    | "run.failed"
    | "run.cancelled"
    | "run.interrupted";
  readonly data: Readonly<Record<string, unknown>>;
  readonly extensions?: ProtocolExtensions;
}

/** session/get 返回的完整消息快照与可选事件增量。 */
export interface SessionSnapshot {
  readonly sessionId: string;
  readonly latestSeq: number;
  readonly activeRunId: string | null;
  readonly messages: readonly SessionMessage[];
  readonly events: readonly SessionReplayEvent[];
  readonly selectedHeadRecordId?: string;
  readonly compactionRecordId?: string;
  readonly extensions?: ProtocolExtensions;
}

/** application service 暴露给侧栏的 Session 摘要。 */
export interface SessionSummary {
  readonly sessionId: string;
  readonly title: string;
  readonly project: string;
  readonly updatedAt: string;
  readonly archived: boolean;
  readonly status?: "active" | "approval";
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

  /**
   * 列出 Provider 连接的脱敏配置。
   *
   * 输入：无。
   * 输出：仅含非敏感连接字段与 credentialConfigured 布尔状态。
   * 不变量：不得返回 API key、token、认证 header 或可还原凭据的派生值。
   * 失败：传输、认证或响应校验失败时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  listProviderConnections(): Promise<readonly ProviderConnection[]>;

  /**
   * 创建一条不含凭据的 Provider 连接。
   *
   * 输入：显示名、稳定 Provider ID、基础 URL、API family、模型与 Logo 资源标识。
   * 输出：新连接的脱敏投影与 restartRequired=true 的严格响应。
   * 不变量：凭据必须通过独立凭据方法写入，配置输入不得夹带 secret。
   * 失败：字段无效、标识冲突、策略拒绝或持久化失败时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  createProviderConnection(
    input: ProviderConnectionInput,
  ): Promise<ProviderConnectionMutationResult>;

  /**
   * 以 revision CAS 更新 Provider 非敏感配置。
   *
   * 输入：连接标识、期望 revision 与完整非敏感配置。
   * 输出：更新后的脱敏投影与 restartRequired=true 的严格响应。
   * 不变量：更新不能读取、替换或回显现有凭据。
   * 失败：连接不存在、revision 冲突、字段无效或持久化失败时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  updateProviderConnection(
    connectionId: string,
    expectedRevision: number,
    input: ProviderConnectionInput,
  ): Promise<ProviderConnectionMutationResult>;

  /**
   * 删除 Provider 连接及其关联凭据。
   *
   * 输入：连接标识与期望 revision。
   * 输出：deleted=true、restartRequired=true 的严格响应。
   * 不变量：删除必须同时使关联凭据不可再被使用。
   * 失败：连接不存在、revision 冲突或持久化失败时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  deleteProviderConnection(
    connectionId: string,
    expectedRevision: number,
  ): Promise<ProviderConnectionDeleteResult>;

  /**
   * 在最终凭据边界写入 Provider secret。
   *
   * 输入：稳定 Provider ID 与非空明文凭据。
   * 输出：只含 Provider ID、credentialConfigured 与 restartRequired，不回显凭据。
   * 不变量：实现不得把凭据写入 Query cache、配置投影、日志或 Session。
   * 失败：连接不存在、凭据为空、CredentialStore 不可用或写入失败时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  setProviderCredential(
    providerId: string,
    credential: string,
  ): Promise<ProviderCredentialStatus>;

  /**
   * 移除 Provider secret，但保留连接配置。
   *
   * 输入：稳定 Provider ID。
   * 输出：credentialConfigured 必须为 false 的状态投影。
   * 不变量：响应不得携带已移除凭据或其摘要。
   * 失败：连接不存在或 CredentialStore 删除失败时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  removeProviderCredential(providerId: string): Promise<ProviderCredentialStatus>;

  /**
   * 读取指定 Session 当前 selected branch 的完整 portable 消息快照。
   *
   * 输入：稳定 Session 标识与可选 durable event 序号下界。
   * 输出：完整消息、真实 active run、最新事件序号及严格大于 afterSeq 的事件增量。
   * 不变量：afterSeq 只过滤 events，不能过滤 messages 或改变 latestSeq。
   * 失败：Session 不存在、输入无效、存储/传输失败或响应违反共同 Schema 时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  getSession(sessionId: string, afterSeq?: number): Promise<SessionSnapshot>;

  /**
   * 列出活动与已归档 Session 摘要。
   *
   * 输入：无。
   * 输出：不含 transcript、journal 或私有文件路径的摘要列表。
   * 不变量：UI 只能依据此投影管理 Session，不直接读取持久化文件。
   * 失败：授权、存储、传输或响应校验失败时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  listSessions(): Promise<readonly SessionSummary[]>;

  /**
   * 将活动 Session 标记为已归档。
   *
   * 输入：稳定 Session 标识。
   * 输出：更新后的摘要。
   * 不变量：归档不等同于删除，数据仍可恢复。
   * 失败：Session 不存在、正在运行、无权限或持久化失败时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  archiveSession(sessionId: string): Promise<SessionSummary>;

  /**
   * 恢复已归档 Session。
   *
   * 输入：稳定 Session 标识。
   * 输出：恢复后的活动摘要。
   * 不变量：恢复不会重写 transcript 或 Session 标识。
   * 失败：Session 不存在、未归档、无权限或持久化失败时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  restoreSession(sessionId: string): Promise<SessionSummary>;

  /**
   * 永久删除 Session。
   *
   * 输入：稳定 Session 标识。
   * 输出：完成后无正文。
   * 不变量：调用方必须先完成显式用户确认；删除后不得继续返回该摘要。
   * 失败：Session 不存在、正在运行、无权限或删除失败时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  deleteSession(sessionId: string): Promise<void>;
}
