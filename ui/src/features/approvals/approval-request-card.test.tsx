import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ApprovalRequestCard } from "@/features/approvals/approval-request-card";
import i18n from "@/i18n";
import type { PendingApproval } from "@/types/application-service";

const PENDING_APPROVAL: PendingApproval = {
  sessionId: "session-1",
  runId: "run-1",
  requestedAt: "2099-07-21T09:59:00Z",
  request: {
    approvalId: "approval-1",
    toolCallId: "tool-call-1",
    operation: "file.write",
    arguments: { path: "notes.txt", content: "hello" },
    operationHash: "a".repeat(64),
    risk: "medium",
    reason: "需要更新工作区文件",
    resources: [{ kind: "path", value: "notes.txt" }],
    choices: ["allow_once", "allow_session", "deny"],
    expiresAt: "2099-07-21T10:00:00Z",
  },
};

describe("ApprovalRequestCard", () => {
  beforeEach(async () => {
    await i18n.changeLanguage("zh-CN");
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  /**
   * 验证界面只呈现服务端 choices，并把精确 choice 交给响应边界。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("renders structured details and submits the selected server choice", () => {
    const onRespond = vi.fn();
    render(
      <ApprovalRequestCard
        approval={PENDING_APPROVAL}
        responseAvailable
        interactionLocked={false}
        submittingChoice={null}
        decisionAccepted={false}
        refreshFailed={false}
        error={null}
        onRespond={onRespond}
        onRetryRefresh={vi.fn()}
      />,
    );

    expect(screen.getByRole("heading", { name: "需要审批" })).toBeInTheDocument();
    expect(screen.getByText("file.write")).toBeInTheDocument();
    expect(screen.getByText("notes.txt")).toBeInTheDocument();
    fireEvent.click(
      screen.getByRole("button", { name: "对 file.write 选择允许一次" }),
    );

    expect(onRespond).toHaveBeenCalledWith("allow_once");
  });

  /**
   * 验证另一张卡提交过程中所有授权与拒绝按钮都被锁定，避免并行决议。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("locks every choice while any response is pending", () => {
    render(
      <ApprovalRequestCard
        approval={PENDING_APPROVAL}
        responseAvailable
        interactionLocked
        submittingChoice={null}
        decisionAccepted={false}
        refreshFailed={false}
        error={null}
        onRespond={vi.fn()}
        onRetryRefresh={vi.fn()}
      />,
    );

    expect(screen.getByRole("button", { name: /允许一次/ })).toBeDisabled();
    expect(screen.getByRole("button", { name: /本会话允许/ })).toBeDisabled();
    expect(screen.getByRole("button", { name: /拒绝/ })).toBeDisabled();
  });

  /**
   * 验证审批在绝对过期点自动重渲染并关闭全部决定按钮。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("disables choices when the approval expires without another render", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2099-07-21T09:59:59Z"));
    render(
      <ApprovalRequestCard
        approval={PENDING_APPROVAL}
        responseAvailable
        interactionLocked={false}
        submittingChoice={null}
        decisionAccepted={false}
        refreshFailed={false}
        error={null}
        onRespond={vi.fn()}
        onRetryRefresh={vi.fn()}
      />,
    );

    expect(screen.getByRole("button", { name: /允许一次/ })).toBeEnabled();
    await act(() => vi.advanceTimersByTimeAsync(1_001));

    expect(screen.getByText("审批已过期，正在等待服务刷新状态")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /允许一次/ })).toBeDisabled();
    expect(screen.getByRole("button", { name: /拒绝/ })).toBeDisabled();
  });
});
