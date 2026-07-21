import { useEffect, useId, useState } from "react";
import {
  Check,
  Clock3,
  LoaderCircle,
  RefreshCw,
  ShieldAlert,
  X,
} from "lucide-react";
import { useTranslation } from "react-i18next";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import type {
  ApprovalChoice,
  PendingApproval,
} from "@/types/application-service";

interface ApprovalRequestCardProps {
  readonly approval: PendingApproval;
  readonly responseAvailable: boolean;
  readonly snapshotReady: boolean;
  readonly interactionLocked: boolean;
  readonly submittingChoice: ApprovalChoice | null;
  readonly decisionAccepted: boolean;
  readonly refreshFailed: boolean;
  readonly error: string | null;
  readonly onRespond: (choice: ApprovalChoice) => void;
  readonly onRetryRefresh: () => void;
}

/**
 * 将审批风险映射为不夸大安全状态的视觉样式。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function getRiskClassName(risk: PendingApproval["request"]["risk"]): string {
  if (risk === "critical" || risk === "high") {
    return "border-destructive/30 bg-destructive/10 text-destructive";
  }
  if (risk === "medium") {
    return "border-amber-300 bg-amber-50 text-amber-800 dark:border-amber-900 dark:bg-amber-950/40 dark:text-amber-200";
  }
  return "border-border bg-muted text-muted-foreground";
}

/**
 * 展示 application service 签发的结构化工具审批及其有限 choices。
 *
 * 输入：从 durable Session 事件重建的审批与当前提交状态。
 * 输出：只提交服务端明确给出的 choice，不修改参数或扩大授权范围。
 * 不变量：过期请求和提交中的请求均不可重复操作。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function ApprovalRequestCard({
  approval,
  responseAvailable,
  snapshotReady,
  interactionLocked,
  submittingChoice,
  decisionAccepted,
  refreshFailed,
  error,
  onRespond,
  onRetryRefresh,
}: ApprovalRequestCardProps) {
  const { t, i18n } = useTranslation();
  const titleId = useId();
  const { request } = approval;
  const expiresAt = new Date(request.expiresAt);
  const expiresAtTimestamp = expiresAt.getTime();
  const [now, setNow] = useState(() => Date.now());
  const expired = Number.isNaN(expiresAtTimestamp) || expiresAtTimestamp <= now;
  const argumentsText = JSON.stringify(request.arguments, null, 2);

  useEffect(() => {
    if (!Number.isFinite(expiresAtTimestamp) || expiresAtTimestamp <= now) {
      return;
    }
    const delay = Math.min(expiresAtTimestamp - now + 1, 2_147_483_647);
    const timer = window.setTimeout(() => setNow(Date.now()), delay);
    return () => window.clearTimeout(timer);
  }, [expiresAtTimestamp, now]);

  return (
    <section
      className="rounded-lg border border-amber-300/80 bg-amber-50/60 p-4 shadow-sm dark:border-amber-900 dark:bg-amber-950/20"
      aria-labelledby={titleId}
    >
      <div className="flex min-w-0 items-start gap-3">
        <span className="flex size-8 shrink-0 items-center justify-center rounded-md bg-amber-100 text-amber-800 dark:bg-amber-950 dark:text-amber-200">
          <ShieldAlert className="size-4" aria-hidden="true" />
        </span>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <h3 id={titleId} className="text-[13px] font-semibold text-foreground">
              {t("approval.title")}
            </h3>
            <Badge
              variant="outline"
              className={cn("h-5 px-1.5 text-[10px] font-medium", getRiskClassName(request.risk))}
            >
              {t(`approval.risk.${request.risk}`)}
            </Badge>
          </div>
          <p className="mt-1 break-all font-mono text-[12px] font-medium text-foreground/90">
            {request.operation}
          </p>
          <p className="mt-2 text-[12px] leading-5 text-muted-foreground">
            {request.reason || t("approval.reasonFallback")}
          </p>
        </div>
      </div>

      {request.resources.length > 0 ? (
        <div className="mt-3 flex flex-wrap gap-1.5" aria-label={t("approval.resources")}>
          {request.resources.map((resource, index) => (
            <span
              key={`${resource.kind}:${resource.value}:${index}`}
              className="max-w-full rounded-md border bg-background/80 px-2 py-1 text-[10px] text-foreground/80"
            >
              <span className="mr-1 text-muted-foreground">
                {t(`approval.resourceKind.${resource.kind}`)}
              </span>
              <code className="break-all">{resource.value}</code>
            </span>
          ))}
        </div>
      ) : null}

      <details className="mt-3 rounded-md border bg-background/70 px-3 py-2">
        <summary className="cursor-pointer select-none text-[11px] font-medium text-foreground/80">
          {t("approval.requestDetails")}
        </summary>
        <div className="mt-3 space-y-3 text-[10px] text-muted-foreground">
          <div>
            <p className="mb-1 font-medium text-foreground/70">{t("approval.arguments")}</p>
            <pre className="max-h-48 overflow-auto whitespace-pre-wrap break-all rounded bg-muted p-2 font-mono leading-4 text-foreground/80">
              {argumentsText}
            </pre>
          </div>
          <div className="grid gap-1 sm:grid-cols-[96px_minmax(0,1fr)]">
            <span>{t("approval.operationHash")}</span>
            <code className="break-all text-foreground/70">{request.operationHash}</code>
            <span>{t("approval.approvalId")}</span>
            <code className="break-all text-foreground/70">{request.approvalId}</code>
          </div>
        </div>
      </details>

      <div className="mt-3 flex flex-col gap-3 border-t border-amber-300/60 pt-3 dark:border-amber-900 sm:flex-row sm:items-center">
        <div className="flex min-w-0 flex-1 items-center gap-1.5 text-[10px] text-muted-foreground">
          <Clock3 className="size-3.5 shrink-0" aria-hidden="true" />
          <span className="truncate">
            {!responseAvailable
              ? t("approval.responseUnavailable")
              : decisionAccepted
                ? t("approval.decisionAccepted")
              : !snapshotReady
                ? t("approval.snapshotUnavailable")
              : expired
              ? t("approval.expired")
              : t("approval.expiresAt", {
                  time: new Intl.DateTimeFormat(i18n.resolvedLanguage ?? "zh-CN", {
                    hour: "2-digit",
                    minute: "2-digit",
                    second: "2-digit",
                  }).format(expiresAt),
                })}
          </span>
        </div>
        <div className="flex flex-wrap justify-end gap-2">
          {request.choices.map((choice) => (
            <Button
              key={choice}
              type="button"
              variant={
                choice === "deny"
                  ? "outline"
                  : "default"
              }
              size="sm"
              className={cn(
                "h-8 text-[11px] shadow-none",
                choice === "deny" && "border-destructive/30 text-destructive hover:bg-destructive/10 hover:text-destructive",
              )}
              disabled={
                !responseAvailable ||
                !snapshotReady ||
                expired ||
                interactionLocked ||
                decisionAccepted ||
                submittingChoice !== null
              }
              onClick={() => onRespond(choice)}
              aria-label={t("approval.respondWith", {
                choice: t(`approval.choice.${choice}`),
                operation: request.operation,
              })}
            >
              {submittingChoice === choice ? (
                <LoaderCircle className="size-3.5 animate-spin" aria-hidden="true" />
              ) : choice === "deny" ? (
                <X className="size-3.5" aria-hidden="true" />
              ) : (
                <Check className="size-3.5" aria-hidden="true" />
              )}
              {t(`approval.choice.${choice}`)}
            </Button>
          ))}
          {decisionAccepted ? (
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="h-8 text-[11px] shadow-none"
              disabled={interactionLocked}
              onClick={onRetryRefresh}
              aria-label={t("approval.retryRefreshFor", {
                operation: request.operation,
              })}
            >
              <RefreshCw className="size-3.5" aria-hidden="true" />
              {t("approval.retryRefresh")}
            </Button>
          ) : null}
        </div>
      </div>

      {error ? (
        <p className="mt-3 text-[11px] text-destructive" role="alert">
          {error}
        </p>
      ) : null}
      {refreshFailed ? (
        <p className="mt-3 text-[11px] text-destructive" role="alert">
          {t("approval.refreshFailed")}
        </p>
      ) : null}
    </section>
  );
}
