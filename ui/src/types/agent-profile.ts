import type { ModelDescriptor } from "@/types/application-service";

/** 智能体选择的完整 Provider 路由身份。 */
export type AgentModelSelection = Pick<
  ModelDescriptor,
  "providerId" | "modelId" | "apiFamily"
>;

/** 危险操作偏好只能要求逐次审批或直接拒绝，不能授予权限。 */
export type DangerousActionMode = "ask" | "deny";

/** 仅用于组织回复表达的预览偏好。 */
export type ResponseStyle = "concise" | "balanced" | "detailed";

/** 可丢弃的智能体行为偏好。 */
export interface AgentBehavior {
  readonly responseStyle: ResponseStyle;
  readonly planFirst: boolean;
  readonly reviewChanges: boolean;
}

/** 用户可编辑的智能体配置输入。 */
export interface AgentProfileInput {
  readonly displayName: string;
  readonly description: string;
  readonly enabled: boolean;
  readonly instructions: string;
  readonly model: AgentModelSelection;
  readonly requestedToolIds: readonly string[];
  readonly dangerousActionMode: DangerousActionMode;
  readonly behavior: AgentBehavior;
}

/** application service 返回的带 revision 智能体投影。 */
export interface AgentProfile extends AgentProfileInput {
  readonly profileId: string;
  readonly revision: number;
  readonly createdAt: string;
  readonly updatedAt: string;
}

/** 前端用于展示固定工具注册表元数据的只读说明。 */
export interface AgentToolPresentation {
  readonly toolId: string;
  readonly displayName: string;
  readonly description: string;
  readonly permissionClass: "workspace_read" | "workspace_write" | "process" | "shell";
  readonly dangerous: boolean;
}

/** 智能体配置的中立服务边界。 */
export interface AgentProfileService {
  readonly mode: "faux-preview" | "application-service";

  /**
   * 列出当前服务生命周期内的智能体投影。
   *
   * 输入：无。
   * 输出：按服务定义顺序返回的独立只读投影。
   * 不变量：返回值不能提升工具能力，也不能暴露服务内部可变状态。
   * 失败：服务不可用或投影无法验证时拒绝，不返回部分可信结果。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  listProfiles(): Promise<readonly AgentProfile[]>;

  /**
   * 创建一份经过边界校验的智能体投影。
   *
   * 输入：包含完整模型路由、工具请求子集与行为偏好的不可信配置。
   * 输出：带稳定 profileId、revision 和时间戳的独立投影。
   * 不变量：配置只能收窄服务能力，不能授予权限或携带 secret。
   * 失败：字段、枚举、模型、工具集合或只读约束无效时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  createProfile(input: AgentProfileInput): Promise<AgentProfile>;

  /**
   * 按预期 revision 更新智能体投影，冲突时拒绝覆盖。
   *
   * 输入：稳定 profileId、CAS expectedRevision 与完整替换配置。
   * 输出：revision 单调递增且保留实体身份的独立投影。
   * 不变量：冲突、缺失或校验失败时不得修改当前实体。
   * 失败：实体不存在、revision 冲突或输入违反创建边界时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  updateProfile(
    profileId: string,
    expectedRevision: number,
    input: AgentProfileInput,
  ): Promise<AgentProfile>;

  /**
   * 按预期 revision 删除智能体投影，冲突时拒绝删除。
   *
   * 输入：稳定 profileId 与 CAS expectedRevision。
   * 输出：成功时无返回值。
   * 不变量：实体不存在或 revision 不匹配时不得删除其他状态。
   * 失败：实体不存在、revision 冲突或服务不可用时拒绝。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  deleteProfile(profileId: string, expectedRevision: number): Promise<void>;
}
