import { ArchiveRestore, Trash2 } from "lucide-react";
import { useTranslation } from "react-i18next";

import { Button } from "@/components/ui/button";
import { ScrollArea } from "@/components/ui/scroll-area";
import { WorkspacePageHeader } from "@/components/workspace-page-header";
import type { SessionSummary } from "@/types/application-service";

interface ArchiveWorkspaceProps {
  readonly sessions: readonly SessionSummary[];
  readonly busySessionIds: ReadonlySet<string>;
  readonly canRestore: boolean;
  readonly canDelete: boolean;
  readonly onRestore: (sessionId: string) => void;
  readonly onDelete: (session: SessionSummary) => void;
  readonly onOpenMobileSidebar: () => void;
}

/**
 * 将 Session UTC 更新时间转换为紧凑本地时间。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function formatSessionTime(updatedAt: string, language: string): string {
  return new Intl.DateTimeFormat(language, {
    month: "numeric",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  }).format(new Date(updatedAt));
}

/**
 * 展示可恢复或永久删除的归档 Session 摘要。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function ArchiveWorkspace({
  sessions,
  busySessionIds,
  canRestore,
  canDelete,
  onRestore,
  onDelete,
  onOpenMobileSidebar,
}: ArchiveWorkspaceProps) {
  const { t, i18n } = useTranslation();

  return (
    <div className="flex min-h-0 flex-1 flex-col bg-background">
      <WorkspacePageHeader
        title={t("archive.title")}
        description={t("archive.description")}
        onOpenMobileSidebar={onOpenMobileSidebar}
      />
      <ScrollArea className="min-h-0 flex-1">
        <div className="mx-auto w-full max-w-4xl px-4 py-6 sm:px-8">
          {sessions.length === 0 ? (
            <div className="border-y py-12 text-center">
              <ArchiveRestore className="mx-auto size-5 text-muted-foreground/50" aria-hidden="true" />
              <h2 className="mt-3 text-[13px] font-medium text-foreground/80">{t("archive.emptyTitle")}</h2>
              <p className="mt-1 text-[11px] text-muted-foreground">{t("archive.emptyDescription")}</p>
            </div>
          ) : (
            <div className="divide-y border-y">
              {sessions.map((session) => {
                const busy = busySessionIds.has(session.sessionId);
                return (
                  <article key={session.sessionId} className="flex min-h-16 items-center gap-3 py-3">
                    <div className="min-w-0 flex-1">
                      <h2 className="truncate text-[12px] font-medium text-foreground">{session.title}</h2>
                      <p className="mt-1 truncate text-[10px] text-muted-foreground">
                        {session.project} · {formatSessionTime(session.updatedAt, i18n.resolvedLanguage ?? "zh-CN")}
                      </p>
                    </div>
                    {canRestore ? <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      className="h-7 gap-1.5 px-2 text-[10px] shadow-none"
                      onClick={() => onRestore(session.sessionId)}
                      disabled={busy}
                    >
                      <ArchiveRestore className="size-3" aria-hidden="true" />
                      {t("archive.restore")}
                    </Button> : null}
                    {canDelete ? <Button
                      type="button"
                      variant="ghost"
                      size="icon"
                      className="size-7 text-muted-foreground hover:text-red-600"
                      onClick={() => onDelete(session)}
                      disabled={busy}
                      aria-label={t("archive.permanentlyDelete", { title: session.title })}
                    >
                      <Trash2 className="size-3.5" aria-hidden="true" />
                    </Button> : null}
                  </article>
                );
              })}
            </div>
          )}
        </div>
      </ScrollArea>
    </div>
  );
}
