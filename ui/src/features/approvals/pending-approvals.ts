import { z } from "zod";

import type {
  ApprovalRequest,
  PendingApproval,
  SessionSnapshot,
} from "@/types/application-service";

const OPAQUE_ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._:-]*$/;
const TOOL_ID_PATTERN = /^[a-z][a-z0-9_.-]*$/;

const approvalRequestSchema = z
  .object({
    approvalId: z.string().max(128).regex(OPAQUE_ID_PATTERN),
    toolCallId: z.string().max(128).regex(OPAQUE_ID_PATTERN),
    operation: z.string().max(128).regex(TOOL_ID_PATTERN),
    arguments: z.record(z.string(), z.unknown()),
    operationHash: z.string().regex(/^[a-f0-9]{64}$/),
    risk: z.enum(["low", "medium", "high", "critical"]),
    reason: z.string().max(4_096).optional(),
    resources: z
      .array(
        z
          .object({
            kind: z.enum([
              "path",
              "executable",
              "origin",
              "credential",
              "process",
              "other",
            ]),
            value: z.string().min(1).max(4_096),
          })
          .strict(),
      )
      .max(128),
    choices: z
      .array(z.enum(["allow_once", "deny"]))
      .min(2)
      .refine((choices) => new Set(choices).size === choices.length)
      .refine((choices) => choices.includes("deny")),
    expiresAt: z.iso.datetime({ offset: false }),
    extensions: z.record(z.string(), z.unknown()).optional(),
  })
  .strict();

/**
 * 生成审批在 Session 与 run 范围内的无歧义身份键。
 *
 * 输入：已经通过 wire schema 校验的 Session、run 与 approval ID；输出：仅用于客户端内存索引的复合键。
 * 不变量：三个协议 ID 都不允许 NUL，因此不同三元组不会发生拼接碰撞；键不进入协议或持久化。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function getApprovalIdentityKey(
  sessionId: string,
  runId: string,
  approvalId: string,
): string {
  return `${sessionId}\u0000${runId}\u0000${approvalId}`;
}

/**
 * 从已经校验的 durable Session 事件中重建尚未决议的审批集合。
 *
 * 输入：`session/get` 返回的完整事件快照；输出：按请求顺序排列的未决审批。
 * 不变量：只有结构完整的 `approval.requested` 才进入视图，后续相同
 * `(sessionId,runId,approvalId)` 的 `approval.resolved` 才会移除它；本函数不根据可见文案推断权限。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function projectPendingApprovals(
  snapshot: SessionSnapshot,
): readonly PendingApproval[] {
  const pending = new Map<string, PendingApproval>();

  for (const event of snapshot.events) {
    if (event.type === "approval.requested") {
      const parsed = approvalRequestSchema.safeParse(event.data.approval);
      if (!parsed.success) {
        continue;
      }
      const request: ApprovalRequest = parsed.data;
      pending.set(getApprovalIdentityKey(event.sessionId, event.runId, request.approvalId), {
        sessionId: event.sessionId,
        runId: event.runId,
        ...(event.turnId ? { turnId: event.turnId } : {}),
        requestedAt: event.time,
        request,
      });
      continue;
    }

    if (event.type === "approval.resolved") {
      const approvalId = event.data.approvalId;
      if (typeof approvalId === "string") {
        pending.delete(getApprovalIdentityKey(event.sessionId, event.runId, approvalId));
      }
    }
  }

  return [...pending.values()];
}
