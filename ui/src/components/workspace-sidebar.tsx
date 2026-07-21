import {
  Archive,
  ArchiveIcon,
  Bot,
  CirclePlus,
  Ellipsis,
  Folder,
  PanelLeftClose,
  Pin,
  Puzzle,
  Settings,
  ShieldAlert,
  SquareTerminal,
  Trash2,
} from "lucide-react";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";

import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { ScrollArea } from "@/components/ui/scroll-area";
import { Separator } from "@/components/ui/separator";
import { cn } from "@/lib/utils";
import { useWorkspaceUiStore, type WorkspaceView } from "@/store/workspace-ui-store";
import type { SessionSummary } from "@/types/application-service";

interface WorkspaceSidebarProps {
  readonly onRequestClose?: () => void;
  readonly onCreateSession: () => void;
  readonly onOpenView: (view: Exclude<WorkspaceView, "conversation">) => void;
  readonly onSelectSession: (sessionId: string) => void;
  readonly onArchiveSession: (session: SessionSummary) => void;
  readonly onDeleteSession: (session: SessionSummary) => void;
  readonly navigationDisabled?: boolean;
  readonly canArchiveSessions: boolean;
  readonly canDeleteSessions: boolean;
  readonly busySessionIds: ReadonlySet<string>;
  readonly sessions: readonly SessionSummary[];
  readonly workspaceName: string;
}

interface SessionNavigationItemProps {
  readonly session: SessionSummary;
  readonly selected: boolean;
  readonly navigationDisabled: boolean;
  readonly actionsDisabled: boolean;
  readonly canArchive: boolean;
  readonly canDelete: boolean;
  readonly onSelect: () => void;
  readonly onArchive: () => void;
  readonly onDelete: () => void;
}

/**
 * 将 Session 更新时间显示为稳定的紧凑标签。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function getSessionAge(updatedAt: string, t: TFunction): string {
  const elapsedMinutes = Math.max(0, Math.floor((Date.now() - Date.parse(updatedAt)) / 60_000));
  if (elapsedMinutes < 1) {
    return t("navigation.justNow");
  }
  if (elapsedMinutes < 60) {
    return t("navigation.minutes", { count: elapsedMinutes });
  }
  if (elapsedMinutes < 1_440) {
    return t("navigation.hours", { count: Math.floor(elapsedMinutes / 60) });
  }
  return t("navigation.days", { count: Math.floor(elapsedMinutes / 1_440) });
}

/**
 * 展示单条 Session 导航与归档、删除操作菜单。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function SessionNavigationItem({
  session,
  selected,
  navigationDisabled,
  actionsDisabled,
  canArchive,
  canDelete,
  onSelect,
  onArchive,
  onDelete,
}: SessionNavigationItemProps) {
  const { t } = useTranslation();
  const sessionStatusLabel =
    session.status === "approval"
      ? t("navigation.pendingApproval")
      : session.status === "active"
        ? t("navigation.sessionInProgress")
        : null;

  return (
    <div
      className={cn(
        "group flex h-8 min-w-0 items-center rounded-md pr-1 transition-colors hover:bg-accent",
        selected && "bg-accent text-accent-foreground",
      )}
    >
      <button
        type="button"
        onClick={onSelect}
        disabled={navigationDisabled}
        className="flex h-full min-w-0 flex-1 items-center gap-2 rounded-md px-2 text-left text-[12px] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:pointer-events-none disabled:opacity-50"
        aria-label={
          sessionStatusLabel
            ? `${session.title}, ${sessionStatusLabel}`
            : session.title
        }
      >
        {session.status === "active" ? (
          <span className="size-1.5 shrink-0 rounded-full bg-sky-500" aria-label={t("navigation.sessionInProgress")} />
        ) : (
          <Pin className="size-3 shrink-0 text-muted-foreground" aria-hidden="true" />
        )}
        <span className="min-w-0 flex-1 truncate">{session.title}</span>
        {session.status === "approval" ? (
          <span className="shrink-0 rounded bg-emerald-100 px-1.5 py-0.5 text-[9px] font-medium text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300">
            {t("navigation.pendingApproval")}
          </span>
        ) : (
          <span className="shrink-0 text-[9px] text-muted-foreground group-hover:hidden">
            {getSessionAge(session.updatedAt, t)}
          </span>
        )}
      </button>
      {canArchive || canDelete ? <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button
            type="button"
            variant="ghost"
            size="icon"
            className="size-6 shrink-0 text-muted-foreground opacity-100 data-[state=open]:opacity-100 focus-visible:opacity-100 sm:opacity-0 sm:group-hover:opacity-100"
            disabled={actionsDisabled}
            aria-label={t("navigation.moreActions", { title: session.title })}
          >
            <Ellipsis className="size-3.5" aria-hidden="true" />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start" side="right" className="w-36">
          {canArchive ? <DropdownMenuItem className="text-[11px]" onSelect={onArchive}>
            <ArchiveIcon className="size-3.5" aria-hidden="true" />
            {t("navigation.archiveAction")}
          </DropdownMenuItem> : null}
          {canArchive && canDelete ? <DropdownMenuSeparator /> : null}
          {canDelete ? <DropdownMenuItem className="text-[11px] text-red-600 focus:text-red-600" onSelect={onDelete}>
            <Trash2 className="size-3.5" aria-hidden="true" />
            {t("navigation.deleteAction")}
          </DropdownMenuItem> : null}
        </DropdownMenuContent>
      </DropdownMenu> : null}
    </div>
  );
}

/**
 * 展示项目导航、真实一级页面入口与可管理的最近 Session 列表。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function WorkspaceSidebar({
  onRequestClose,
  onCreateSession,
  onOpenView,
  onSelectSession,
  onArchiveSession,
  onDeleteSession,
  navigationDisabled = false,
  canArchiveSessions,
  canDeleteSessions,
  busySessionIds,
  sessions,
  workspaceName,
}: WorkspaceSidebarProps) {
  const { t } = useTranslation();
  const activeSessionId = useWorkspaceUiStore((state) => state.activeSessionId);
  const activeView = useWorkspaceUiStore((state) => state.activeView);
  const approvalSessions = sessions.filter((session) => session.status === "approval");

  return (
    <div className="flex h-full min-h-0 flex-col bg-muted/70 text-foreground/80">
      <div className="flex h-12 shrink-0 items-center gap-2 px-3">
        <div className="flex size-6 items-center justify-center rounded-md bg-primary text-primary-foreground">
          <SquareTerminal className="size-3.5" aria-hidden="true" />
        </div>
        <span className="min-w-0 flex-1 truncate text-[13px] font-semibold text-foreground">
          QXNM Forge
        </span>
        {onRequestClose ? (
          <Button
            type="button"
            variant="ghost"
            size="icon"
            className="size-7 text-muted-foreground"
            onClick={onRequestClose}
            aria-label={t("navigation.closeSidebar")}
          >
            <PanelLeftClose className="size-4" aria-hidden="true" />
          </Button>
        ) : null}
      </div>

      <div className="shrink-0 space-y-0.5 px-2 pb-2">
        <Button
          type="button"
          variant="ghost"
          className="h-8 w-full justify-start gap-2 px-2 text-[12px] font-normal text-foreground/80 hover:bg-accent"
          onClick={onCreateSession}
          disabled={navigationDisabled}
        >
          <CirclePlus className="size-4 text-muted-foreground" aria-hidden="true" />
          {t("navigation.newTask")}
        </Button>
        {approvalSessions.length > 0 ? (
          <Button
            type="button"
            variant="ghost"
            className={cn(
              "h-8 w-full justify-start gap-2 px-2 text-[12px] font-normal text-foreground/80 hover:bg-accent",
              activeView === "conversation" &&
                approvalSessions.some((session) => session.sessionId === activeSessionId) &&
                "bg-accent text-accent-foreground",
            )}
            onClick={() => onSelectSession(approvalSessions[0].sessionId)}
            disabled={navigationDisabled}
          >
            <ShieldAlert className="size-4 text-amber-600" aria-hidden="true" />
            <span className="min-w-0 flex-1 text-left">{t("navigation.pendingApprovals")}</span>
            <span className="flex size-5 shrink-0 items-center justify-center rounded-full bg-amber-100 text-[9px] font-semibold text-amber-800 dark:bg-amber-950 dark:text-amber-200">
              {approvalSessions.length}
            </span>
          </Button>
        ) : null}
        <Button
          type="button"
          variant="ghost"
          className={cn(
            "h-8 w-full justify-start gap-2 px-2 text-[12px] font-normal text-foreground/80 hover:bg-accent",
            activeView === "agents" && "bg-accent text-accent-foreground",
          )}
          onClick={() => onOpenView("agents")}
        >
          <Bot className="size-4 text-muted-foreground" aria-hidden="true" />
          {t("navigation.agents")}
        </Button>
        <Button
          type="button"
          variant="ghost"
          className={cn(
            "h-8 w-full justify-start gap-2 px-2 text-[12px] font-normal text-foreground/80 hover:bg-accent",
            activeView === "plugins" && "bg-accent text-accent-foreground",
          )}
          onClick={() => onOpenView("plugins")}
        >
          <Puzzle className="size-4 text-muted-foreground" aria-hidden="true" />
          {t("navigation.plugins")}
        </Button>
        <Button
          type="button"
          variant="ghost"
          className={cn(
            "h-8 w-full justify-start gap-2 px-2 text-[12px] font-normal text-foreground/80 hover:bg-accent",
            activeView === "archive" && "bg-accent text-accent-foreground",
          )}
          onClick={() => onOpenView("archive")}
        >
          <Archive className="size-4 text-muted-foreground" aria-hidden="true" />
          {t("navigation.archive")}
        </Button>
      </div>

      <Separator />

      <ScrollArea className="min-h-0 flex-1">
        <div className="px-2 py-3">
          <div className="mb-1 flex h-7 items-center px-2 text-[11px] font-medium text-muted-foreground">
            {t("navigation.recentTasks")}
          </div>
          <div className="space-y-0.5">
            {sessions.map((session) => (
              <SessionNavigationItem
                key={session.sessionId}
                session={session}
                selected={activeView === "conversation" && activeSessionId === session.sessionId}
                navigationDisabled={navigationDisabled}
                actionsDisabled={navigationDisabled || busySessionIds.has(session.sessionId)}
                canArchive={canArchiveSessions}
                canDelete={canDeleteSessions}
                onSelect={() => onSelectSession(session.sessionId)}
                onArchive={() => onArchiveSession(session)}
                onDelete={() => onDeleteSession(session)}
              />
            ))}
          </div>

          <div className="mb-1 mt-4 flex h-7 items-center px-2 text-[11px] font-medium text-muted-foreground">
            {t("navigation.workspace")}
          </div>

          <div className="flex h-8 w-full items-center gap-2 rounded-md px-2 text-left text-[12px] text-foreground/80">
            <Folder className="size-3.5 text-muted-foreground" aria-hidden="true" />
            <span className="min-w-0 flex-1 truncate">{workspaceName}</span>
          </div>
        </div>
      </ScrollArea>

      <div className="shrink-0 px-2 pb-2 pt-1">
        <Button
          type="button"
          variant="ghost"
          className={cn(
            "h-8 w-full justify-start gap-2 px-2 text-[12px] font-normal text-foreground/80 hover:bg-accent",
            activeView === "settings" && "bg-accent text-accent-foreground",
          )}
          onClick={() => onOpenView("settings")}
        >
          <Settings className="size-4 text-muted-foreground" aria-hidden="true" />
          {t("navigation.settings")}
        </Button>
      </div>
    </div>
  );
}
