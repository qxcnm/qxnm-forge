import { describe, expect, it } from "vitest";

import { projectPendingApprovals } from "@/features/approvals/pending-approvals";
import type { SessionSnapshot } from "@/types/application-service";

const APPROVAL_REQUEST = {
  approvalId: "approval-1",
  toolCallId: "tool-call-1",
  operation: "file.write",
  arguments: { path: "notes.txt", content: "hello" },
  operationHash: "a".repeat(64),
  risk: "medium" as const,
  reason: "需要更新工作区文件",
  resources: [{ kind: "path" as const, value: "notes.txt" }],
  choices: ["allow_once" as const, "deny" as const],
  expiresAt: "2026-07-21T10:00:00Z",
};

describe("pending approval projection", () => {
  /**
   * 验证 durable 请求会保留服务端签发的 Session、run 与结构化操作。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("projects an unresolved approval request", () => {
    const snapshot: SessionSnapshot = {
      sessionId: "session-1",
      latestSeq: 1,
      activeRunId: "run-1",
      messages: [],
      events: [
        {
          sessionId: "session-1",
          runId: "run-1",
          turnId: "turn-1",
          seq: 1,
          time: "2026-07-21T09:59:00Z",
          type: "approval.requested",
          data: { approval: APPROVAL_REQUEST },
        },
      ],
    };

    expect(projectPendingApprovals(snapshot)).toEqual([
      {
        sessionId: "session-1",
        runId: "run-1",
        turnId: "turn-1",
        requestedAt: "2026-07-21T09:59:00Z",
        request: APPROVAL_REQUEST,
      },
    ]);
  });

  /**
   * 验证后续 resolution 会移除相同 ID，不能重复显示或再次提交。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("removes approvals that already have a durable resolution", () => {
    const snapshot: SessionSnapshot = {
      sessionId: "session-1",
      latestSeq: 2,
      activeRunId: null,
      messages: [],
      events: [
        {
          sessionId: "session-1",
          runId: "run-1",
          seq: 1,
          time: "2026-07-21T09:59:00Z",
          type: "approval.requested",
          data: { approval: APPROVAL_REQUEST },
        },
        {
          sessionId: "session-1",
          runId: "run-1",
          seq: 2,
          time: "2026-07-21T09:59:10Z",
          type: "approval.resolved",
          data: {
            approvalId: APPROVAL_REQUEST.approvalId,
            decision: { choice: "deny" },
            resolutionSource: "client",
          },
        },
      ],
    };

    expect(projectPendingApprovals(snapshot)).toEqual([]);
  });

  /**
   * 验证损坏或伪造的 event data 安全回退为空，不生成审批能力。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("ignores malformed approval request data", () => {
    const snapshot: SessionSnapshot = {
      sessionId: "session-1",
      latestSeq: 1,
      activeRunId: "run-1",
      messages: [],
      events: [
        {
          sessionId: "session-1",
          runId: "run-1",
          seq: 1,
          time: "2026-07-21T09:59:00Z",
          type: "approval.requested",
          data: {
            approval: {
              ...APPROVAL_REQUEST,
              choices: ["allow_once"],
              operationHash: "forged",
            },
          },
        },
      ],
    };

    expect(projectPendingApprovals(snapshot)).toEqual([]);
  });
});
