import { Check, Circle, LoaderCircle, RefreshCw, TerminalSquare } from "lucide-react";
import { useTranslation } from "react-i18next";

import { Button } from "@/components/ui/button";
import { ScrollArea } from "@/components/ui/scroll-area";
import {
  ACTIVITY_FIXTURES,
  CHANGED_FILES,
  COMMAND_LOG_FIXTURES,
} from "@/data/workspace-fixtures";
import { ApprovalRequestCard } from "@/features/approvals/approval-request-card";
import { getApprovalIdentityKey } from "@/features/approvals/pending-approvals";
import type {
  ApprovalChoice,
  PendingApproval,
} from "@/types/application-service";

export interface SubmittedMessage {
  readonly id: string;
  readonly role: "user" | "assistant" | "status";
  readonly content: string;
  readonly translation?: {
    readonly key: string;
    readonly values?: Readonly<Record<string, string | number>>;
  };
  readonly images?: readonly SubmittedMessageImage[];
}

/** Session 消息中经过 artifact 边界验证的图片展示状态。 */
export interface SubmittedMessageImage {
  readonly artifactId: string;
  readonly alt: string;
  readonly status: "loading" | "ready" | "error";
  readonly dataUrl?: string;
}

/**
 * 在最终渲染边界再次限制图片 data URL 的 MIME 与纯 Base64 形态。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function isVerifiedImageDataUrl(value: string | undefined): value is string {
  return value !== undefined &&
    /^data:image\/(?:png|jpeg|webp|gif);base64,(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(
      value,
    );
}

interface ConversationProps {
  readonly backendLabel: string;
  readonly messages: readonly SubmittedMessage[];
  readonly busy: boolean;
  readonly showFixture: boolean;
  readonly historyLoading: boolean;
  readonly historyError: boolean;
  readonly onRetryHistory: () => void;
  readonly pendingApprovals: readonly PendingApproval[];
  readonly approvalResponseAvailable: boolean;
  readonly approvalSnapshotReady: boolean;
  readonly approvalInteractionLocked: boolean;
  readonly approvalSubmission: {
    readonly sessionId: string;
    readonly runId: string;
    readonly approvalId: string;
    readonly choice: ApprovalChoice;
  } | null;
  readonly approvalError: {
    readonly sessionId: string;
    readonly runId: string;
    readonly approvalId: string;
    readonly message: string;
  } | null;
  readonly approvalConfirmations: readonly {
    readonly sessionId: string;
    readonly approvalId: string;
    readonly runId: string;
    readonly choice: ApprovalChoice;
    readonly refreshFailed: boolean;
  }[];
  readonly onRespondToApproval: (
    approval: PendingApproval,
    choice: ApprovalChoice,
  ) => void;
  readonly onRetryApprovalRefresh: (approval: PendingApproval) => void;
}

/**
 * 呈现由服务快照和事件投影而来的会话内容。
 *
 * 当前静态活动来自 faux fixture，不读取 Session journal 或宿主文件。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function Conversation({
  backendLabel,
  messages,
  busy,
  showFixture,
  historyLoading,
  historyError,
  onRetryHistory,
  pendingApprovals,
  approvalResponseAvailable,
  approvalSnapshotReady,
  approvalInteractionLocked,
  approvalSubmission,
  approvalError,
  approvalConfirmations,
  onRespondToApproval,
  onRetryApprovalRefresh,
}: ConversationProps) {
  const { t } = useTranslation();

  return (
    <>
      <ScrollArea
        className="min-h-0 flex-1 bg-background"
        data-testid="conversation-transcript"
      >
        <div className="mx-auto w-full max-w-[760px] px-5 pb-8 pt-8 sm:px-8 sm:pt-12">
          {showFixture ? (
            <>
              <section aria-labelledby="task-heading">
                <div className="mb-7 flex items-start gap-3">
                  <div className="mt-0.5 flex size-7 shrink-0 items-center justify-center rounded-md bg-primary text-primary-foreground">
                    <TerminalSquare className="size-3.5" aria-hidden="true" />
                  </div>
                  <div className="min-w-0">
                    <h2
                      id="task-heading"
                      className="text-[13px] font-semibold leading-5 text-foreground"
                    >
                      {t("conversation.title")}
                    </h2>
                    <p className="mt-0.5 text-[11px] leading-4 text-muted-foreground">
                      React · shadcn/ui · Tauri 2
                    </p>
                  </div>
                </div>

                <div className="mb-6 space-y-2" aria-label={t("conversation.readOnlyLog")}>
                  {COMMAND_LOG_FIXTURES.map((command, index) => (
                    <div
                      key={command}
                      className="flex min-w-0 items-center gap-2 text-[10px] leading-4 text-muted-foreground"
                    >
                      <TerminalSquare
                        className="size-3 shrink-0 text-muted-foreground/60"
                        aria-hidden="true"
                      />
                      <span className="truncate font-mono">
                        {t(`conversation.commandLog.${index}`, { defaultValue: command })}
                      </span>
                    </div>
                  ))}
                </div>

                <p className="text-[13px] leading-5 text-foreground/90">
                  {t("conversation.intro")}
                </p>

                <div className="mt-6 space-y-4" aria-label={t("conversation.activity")}>
                  {ACTIVITY_FIXTURES.map((activity) => {
                    const ActivityIcon = activity.icon;
                    return (
                      <div
                        key={activity.id}
                        className="flex items-start gap-3 text-[11px] leading-4"
                      >
                        <div className="flex size-5 shrink-0 items-center justify-center">
                          {activity.state === "completed" ? (
                            <Check
                              className="size-3.5 text-emerald-600"
                              aria-hidden="true"
                            />
                          ) : activity.state === "running" ? (
                            <LoaderCircle
                              className="size-3.5 animate-spin text-sky-600"
                              aria-hidden="true"
                            />
                          ) : (
                            <Circle className="size-2.5 text-muted-foreground/50" aria-hidden="true" />
                          )}
                        </div>
                        <ActivityIcon
                          className="mt-0.5 size-3.5 shrink-0 text-muted-foreground"
                          aria-hidden="true"
                        />
                        <div className="min-w-0 flex-1">
                          <p className="font-medium text-foreground/80">
                            {t(`conversation.activities.${activity.id}.label`, { defaultValue: activity.label })}
                          </p>
                          <p className="mt-0.5 truncate text-muted-foreground">
                            {t(`conversation.activities.${activity.id}.detail`, { defaultValue: activity.detail })}
                          </p>
                        </div>
                      </div>
                    );
                  })}
                </div>

                <div className="my-7 flex items-center gap-3 text-[10px] text-muted-foreground">
                  <span className="h-px flex-1 bg-border" />
                  <span>{t("conversation.completedCheck")}</span>
                  <span className="h-px flex-1 bg-border" />
                </div>

                <div className="space-y-3 text-[13px] leading-5 text-foreground/90">
                  <p>{t("conversation.summary")}</p>
                  <p>{t("conversation.currentBackend", { backend: backendLabel })}</p>
                  <p>
                    {t("conversation.capabilityRule")}{" "}
                    <code className="font-mono text-[11px] text-sky-700 dark:text-sky-400">capabilities</code>
                  </p>
                </div>
              </section>

              <section
                className="mt-8 overflow-hidden rounded-lg bg-muted/70"
                aria-label={t("conversation.changeSummary")}
              >
                <div className="flex h-9 items-center border-b px-3 text-[11px] text-muted-foreground">
                  <span>{t("conversation.changedFiles", { count: CHANGED_FILES.length })}</span>
                </div>
                {CHANGED_FILES.map((file) => {
                  const FileIcon = file.icon;
                  return (
                    <div
                      key={file.path}
                      className="flex h-9 items-center gap-2 px-3 text-[11px] text-foreground/80"
                    >
                      <FileIcon className="size-3.5 text-muted-foreground" aria-hidden="true" />
                      <span className="min-w-0 flex-1 truncate font-mono text-[10px]">
                        {file.path}
                      </span>
                      <span className="text-emerald-600">+{file.additions}</span>
                      <span className="text-rose-500">-{file.deletions}</span>
                    </div>
                  );
                })}
              </section>
            </>
          ) : null}

          {messages.length > 0 ? (
            <section className="mt-8 space-y-5" aria-label={t("conversation.newMessages")}>
              {messages.map((message) => {
                const text = message.translation
                  ? t(message.translation.key, message.translation.values)
                  : message.content;
                return (
                  <div
                    key={message.id}
                    className={
                      message.role === "user"
                        ? "ml-auto max-w-[86%] rounded-lg bg-muted px-3 py-2.5 text-[13px] leading-5 text-foreground"
                        : message.role === "status"
                          ? "text-[11px] text-muted-foreground"
                          : "text-[13px] leading-5 text-foreground/90"
                    }
                  >
                    {text ? <p className="whitespace-pre-wrap">{text}</p> : null}
                    {message.images && message.images.length > 0 ? (
                      <div className={text ? "mt-2 space-y-2" : "space-y-2"}>
                        {message.images.map((image) => {
                          const ready =
                            image.status === "ready" &&
                            isVerifiedImageDataUrl(image.dataUrl);
                          return ready ? (
                            <figure key={image.artifactId} className="overflow-hidden rounded-md border bg-background/70">
                              <img
                                src={image.dataUrl}
                                alt={image.alt}
                                className="max-h-[28rem] w-full object-contain"
                              />
                            </figure>
                          ) : (
                            <div
                              key={image.artifactId}
                              className="rounded-md border border-dashed px-3 py-4 text-[11px] text-muted-foreground"
                              role="status"
                            >
                              {image.status === "loading"
                                ? t("conversation.imageLoading")
                                : t("conversation.imageLoadFailed")}
                            </div>
                          );
                        })}
                      </div>
                    ) : null}
                  </div>
                );
              })}
            </section>
          ) : null}

          {historyLoading ? (
            <div className="flex items-center gap-2 py-8 text-[11px] text-muted-foreground" role="status">
              <LoaderCircle className="size-3.5 animate-spin" aria-hidden="true" />
              {t("conversation.loadingHistory")}
            </div>
          ) : historyError ? (
            <div className="flex items-center gap-3 py-8" role="alert">
              <p className="min-w-0 flex-1 text-[11px] text-red-600">
                {t("conversation.loadFailed")}
              </p>
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="h-8 shrink-0 gap-1.5 text-[10px] shadow-none"
                onClick={onRetryHistory}
              >
                <RefreshCw className="size-3" aria-hidden="true" />
                {t("conversation.retryHistory")}
              </Button>
            </div>
          ) : !showFixture && messages.length === 0 && !busy ? (
            <p className="py-8 text-[11px] text-muted-foreground">{t("conversation.empty")}</p>
          ) : null}

          {busy ? (
            <div className="mt-6 flex items-center gap-2 text-[11px] text-muted-foreground" role="status">
              <LoaderCircle className="size-3.5 animate-spin" aria-hidden="true" />
              {t("conversation.waiting")}
            </div>
          ) : null}

          <div className="h-6 sm:h-10" />
        </div>
      </ScrollArea>

      {pendingApprovals.length > 0 ? (
        <aside
          className="max-h-[46dvh] shrink-0 overflow-y-auto overscroll-contain border-t bg-background/95 px-3 py-3 shadow-[0_-8px_24px_-20px_rgba(0,0,0,0.45)] sm:max-h-[min(46vh,30rem)] sm:px-8"
          aria-label={t("approval.title")}
          data-testid="pending-approval-dock"
        >
          <div className="mx-auto w-full max-w-[760px] space-y-3">
            {pendingApprovals.map((approval) => {
              const approvalKey = getApprovalIdentityKey(
                approval.sessionId,
                approval.runId,
                approval.request.approvalId,
              );
              const confirmation = approvalConfirmations.find(
                (candidate) =>
                  getApprovalIdentityKey(
                    candidate.sessionId,
                    candidate.runId,
                    candidate.approvalId,
                  ) === approvalKey,
              );
              return (
                <ApprovalRequestCard
                  key={approvalKey}
                  approval={approval}
                  responseAvailable={approvalResponseAvailable}
                  snapshotReady={approvalSnapshotReady}
                  interactionLocked={approvalInteractionLocked}
                  submittingChoice={
                    approvalSubmission &&
                    getApprovalIdentityKey(
                      approvalSubmission.sessionId,
                      approvalSubmission.runId,
                      approvalSubmission.approvalId,
                    ) === approvalKey
                      ? approvalSubmission.choice
                      : null
                  }
                  decisionAccepted={confirmation !== undefined}
                  refreshFailed={confirmation?.refreshFailed ?? false}
                  error={
                    approvalError &&
                    getApprovalIdentityKey(
                      approvalError.sessionId,
                      approvalError.runId,
                      approvalError.approvalId,
                    ) === approvalKey
                      ? approvalError.message
                      : null
                  }
                  onRespond={(choice) => onRespondToApproval(approval, choice)}
                  onRetryRefresh={() => onRetryApprovalRefresh(approval)}
                />
              );
            })}
          </div>
        </aside>
      ) : null}
    </>
  );
}
