import { ArchiveRestore, Trash2 } from "lucide-react";

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
function formatSessionTime(updatedAt: string): string {
  return new Intl.DateTimeFormat("zh-CN", {
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
  return (
    <div className="flex min-h-0 flex-1 flex-col bg-white">
      <WorkspacePageHeader
        title="已归档"
        description="归档会话不会出现在最近任务中"
        onOpenMobileSidebar={onOpenMobileSidebar}
      />
      <ScrollArea className="min-h-0 flex-1">
        <div className="mx-auto w-full max-w-4xl px-4 py-6 sm:px-8">
          {sessions.length === 0 ? (
            <div className="border-y border-stone-100 py-12 text-center">
              <ArchiveRestore className="mx-auto size-5 text-stone-300" aria-hidden="true" />
              <h2 className="mt-3 text-[13px] font-medium text-stone-700">暂无已归档会话</h2>
              <p className="mt-1 text-[11px] text-stone-400">从任务菜单归档的会话会显示在这里。</p>
            </div>
          ) : (
            <div className="divide-y divide-stone-100 border-y border-stone-100">
              {sessions.map((session) => {
                const busy = busySessionIds.has(session.sessionId);
                return (
                  <article key={session.sessionId} className="flex min-h-16 items-center gap-3 py-3">
                    <div className="min-w-0 flex-1">
                      <h2 className="truncate text-[12px] font-medium text-stone-800">{session.title}</h2>
                      <p className="mt-1 truncate text-[10px] text-stone-400">
                        {session.project} · {formatSessionTime(session.updatedAt)}
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
                      恢复
                    </Button> : null}
                    {canDelete ? <Button
                      type="button"
                      variant="ghost"
                      size="icon"
                      className="size-7 text-stone-400 hover:text-red-600"
                      onClick={() => onDelete(session)}
                      disabled={busy}
                      aria-label={`永久删除 ${session.title}`}
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
