import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { Conversation } from "@/components/conversation";
import i18n from "@/i18n";
import type { PendingApproval } from "@/types/application-service";

const PENDING_APPROVAL: PendingApproval = {
  sessionId: "session-approval",
  runId: "run-approval",
  requestedAt: "2026-07-22T01:00:00.000Z",
  request: {
    approvalId: "approval-visible",
    toolCallId: "tool-call-visible",
    operation: "file.write",
    arguments: { path: "notes.txt" },
    operationHash: "a".repeat(64),
    risk: "medium",
    reason: "写入工作区文件",
    resources: [{ kind: "path", value: "notes.txt" }],
    choices: ["allow_once", "deny"],
    expiresAt: "2099-07-22T02:00:00.000Z",
  },
};

const COLLIDING_APPROVAL: PendingApproval = {
  ...PENDING_APPROVAL,
  runId: "run-colliding",
  request: {
    ...PENDING_APPROVAL.request,
    toolCallId: "tool-call-colliding",
    operation: "process.exec",
    arguments: { executable: "formatter" },
    operationHash: "b".repeat(64),
  },
};

describe("Conversation", () => {
  beforeEach(async () => {
    await i18n.changeLanguage("zh-CN");
  });

  /**
   * 验证语义化系统状态会随界面语言重译，而用户内容始终保持原文。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("retranslates status messages without translating user content", async () => {
    render(
      <Conversation
        backendLabel="Rust"
        messages={[
          {
            id: "user-1",
            role: "user",
            content: "用户原文保持不变",
          },
          {
            id: "status-1",
            role: "status",
            content: "",
            translation: {
              key: "app.runAccepted",
              values: { backend: "Rust" },
            },
          },
        ]}
        busy={false}
        showFixture={false}
        historyLoading={false}
        historyError={false}
        onRetryHistory={vi.fn()}
        pendingApprovals={[]}
        approvalResponseAvailable
        approvalSnapshotReady
        approvalInteractionLocked={false}
        approvalSubmission={null}
        approvalError={null}
        approvalConfirmations={[]}
        onRespondToApproval={vi.fn()}
        onRetryApprovalRefresh={vi.fn()}
      />,
    );

    expect(screen.getByText(/运行已由 Rust capability 画像接受/)).toBeInTheDocument();
    await act(async () => i18n.changeLanguage("en-US"));

    expect(
      screen.getByText(/The Rust capability profile accepted the run/),
    ).toBeInTheDocument();
    expect(screen.getByText("用户原文保持不变")).toBeInTheDocument();
  });

  /**
   * 验证会话读取失败时提供明确重试操作并调用只读刷新边界。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("retries a failed session history read", () => {
    const onRetryHistory = vi.fn();
    render(
      <Conversation
        backendLabel="Rust"
        messages={[]}
        busy={false}
        showFixture={false}
        historyLoading={false}
        historyError
        onRetryHistory={onRetryHistory}
        pendingApprovals={[]}
        approvalResponseAvailable
        approvalSnapshotReady
        approvalInteractionLocked={false}
        approvalSubmission={null}
        approvalError={null}
        approvalConfirmations={[]}
        onRespondToApproval={vi.fn()}
        onRetryApprovalRefresh={vi.fn()}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "重试读取" }));

    expect(onRetryHistory).toHaveBeenCalledTimes(1);
  });

  /**
   * 验证长会话只在 transcript 内滚动，审批操作 dock 始终位于滚动容器外并保留有限决定按钮。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("keeps approval actions outside the scrolling transcript", () => {
    render(
      <Conversation
        backendLabel="Rust"
        messages={Array.from({ length: 80 }, (_, index) => ({
          id: `message-${index}`,
          role: "assistant" as const,
          content: `历史消息 ${index}`,
        }))}
        busy={false}
        showFixture={false}
        historyLoading={false}
        historyError={false}
        onRetryHistory={vi.fn()}
        pendingApprovals={[PENDING_APPROVAL]}
        approvalResponseAvailable
        approvalSnapshotReady
        approvalInteractionLocked={false}
        approvalSubmission={null}
        approvalError={null}
        approvalConfirmations={[]}
        onRespondToApproval={vi.fn()}
        onRetryApprovalRefresh={vi.fn()}
      />,
    );

    const transcript = screen.getByTestId("conversation-transcript");
    const approvalDock = screen.getByTestId("pending-approval-dock");

    expect(transcript).not.toContainElement(approvalDock);
    expect(transcript.nextElementSibling).toBe(approvalDock);
    expect(within(approvalDock).getByRole("button", { name: /允许一次/ })).toBeVisible();
    expect(within(approvalDock).getByRole("button", { name: /拒绝/ })).toBeVisible();
  });

  /**
   * 验证相同 approvalId 的不同 run 只在目标卡片显示提交进度和失败信息。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("scopes approval submission and errors to the full identity", () => {
    render(
      <Conversation
        backendLabel="Rust"
        messages={[]}
        busy={false}
        showFixture={false}
        historyLoading={false}
        historyError={false}
        onRetryHistory={vi.fn()}
        pendingApprovals={[PENDING_APPROVAL, COLLIDING_APPROVAL]}
        approvalResponseAvailable
        approvalSnapshotReady
        approvalInteractionLocked={false}
        approvalSubmission={{
          sessionId: PENDING_APPROVAL.sessionId,
          runId: PENDING_APPROVAL.runId,
          approvalId: PENDING_APPROVAL.request.approvalId,
          choice: "allow_once",
        }}
        approvalError={{
          sessionId: COLLIDING_APPROVAL.sessionId,
          runId: COLLIDING_APPROVAL.runId,
          approvalId: COLLIDING_APPROVAL.request.approvalId,
          message: "second request failed",
        }}
        approvalConfirmations={[]}
        onRespondToApproval={vi.fn()}
        onRetryApprovalRefresh={vi.fn()}
      />,
    );

    const allowButtons = screen.getAllByRole("button", { name: /允许一次/ });
    expect(allowButtons).toHaveLength(2);
    expect(allowButtons[0]?.querySelector(".animate-spin")).not.toBeNull();
    expect(allowButtons[1]?.querySelector(".animate-spin")).toBeNull();
    const error = screen.getByRole("alert");
    expect(error).toHaveTextContent("second request failed");
    expect(error.closest("section")).toHaveTextContent("process.exec");
  });
});
