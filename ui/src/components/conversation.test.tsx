import { act, fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { Conversation } from "@/components/conversation";
import i18n from "@/i18n";

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
});
