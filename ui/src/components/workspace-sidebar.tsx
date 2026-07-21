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
  SquareTerminal,
  Trash2,
} from "lucide-react";

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
function getSessionAge(updatedAt: string): string {
  const elapsedMinutes = Math.max(0, Math.floor((Date.now() - Date.parse(updatedAt)) / 60_000));
  if (elapsedMinutes < 1) {
    return "刚刚";
  }
  if (elapsedMinutes < 60) {
    return `${elapsedMinutes} 分钟`;
  }
  if (elapsedMinutes < 1_440) {
    return `${Math.floor(elapsedMinutes / 60)} 小时`;
  }
  return `${Math.floor(elapsedMinutes / 1_440)} 天`;
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
  return (
    <div
      className={cn(
        "group flex h-8 min-w-0 items-center rounded-md pr-1 transition-colors hover:bg-stone-200/80",
        selected && "bg-[#e7e7e5] text-stone-950",
      )}
    >
      <button
        type="button"
        onClick={onSelect}
        disabled={navigationDisabled}
        className="flex h-full min-w-0 flex-1 items-center gap-2 rounded-md px-2 text-left text-[12px] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-stone-400 disabled:pointer-events-none disabled:opacity-50"
        aria-label={session.title}
      >
        {session.status === "active" ? (
          <span className="size-1.5 shrink-0 rounded-full bg-sky-500" aria-label="正在进行" />
        ) : (
          <Pin className="size-3 shrink-0 text-stone-400" aria-hidden="true" />
        )}
        <span className="min-w-0 flex-1 truncate">{session.title}</span>
        {session.status === "approval" ? (
          <span className="shrink-0 rounded bg-emerald-100 px-1.5 py-0.5 text-[9px] font-medium text-emerald-700">
            待审批
          </span>
        ) : (
          <span className="shrink-0 text-[9px] text-stone-400 group-hover:hidden">
            {getSessionAge(session.updatedAt)}
          </span>
        )}
      </button>
      {canArchive || canDelete ? <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button
            type="button"
            variant="ghost"
            size="icon"
            className="size-6 shrink-0 text-stone-400 opacity-100 data-[state=open]:opacity-100 focus-visible:opacity-100 sm:opacity-0 sm:group-hover:opacity-100"
            disabled={actionsDisabled}
            aria-label={`${session.title} 更多操作`}
          >
            <Ellipsis className="size-3.5" aria-hidden="true" />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start" side="right" className="w-36">
          {canArchive ? <DropdownMenuItem className="text-[11px]" onSelect={onArchive}>
            <ArchiveIcon className="size-3.5" aria-hidden="true" />
            归档
          </DropdownMenuItem> : null}
          {canArchive && canDelete ? <DropdownMenuSeparator /> : null}
          {canDelete ? <DropdownMenuItem className="text-[11px] text-red-600 focus:text-red-600" onSelect={onDelete}>
            <Trash2 className="size-3.5" aria-hidden="true" />
            删除
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
  const activeSessionId = useWorkspaceUiStore((state) => state.activeSessionId);
  const activeView = useWorkspaceUiStore((state) => state.activeView);

  return (
    <div className="flex h-full min-h-0 flex-col bg-[#f3f3f2] text-stone-700">
      <div className="flex h-12 shrink-0 items-center gap-2 px-3">
        <div className="flex size-6 items-center justify-center rounded-md bg-stone-900 text-white">
          <SquareTerminal className="size-3.5" aria-hidden="true" />
        </div>
        <span className="min-w-0 flex-1 truncate text-[13px] font-semibold text-stone-900">
          QXNM Forge
        </span>
        {onRequestClose ? (
          <Button
            type="button"
            variant="ghost"
            size="icon"
            className="size-7 text-stone-500"
            onClick={onRequestClose}
            aria-label="关闭项目导航"
          >
            <PanelLeftClose className="size-4" aria-hidden="true" />
          </Button>
        ) : null}
      </div>

      <div className="shrink-0 space-y-0.5 px-2 pb-2">
        <Button
          type="button"
          variant="ghost"
          className="h-8 w-full justify-start gap-2 px-2 text-[12px] font-normal text-stone-700 hover:bg-stone-200/75"
          onClick={onCreateSession}
          disabled={navigationDisabled}
        >
          <CirclePlus className="size-4 text-stone-500" aria-hidden="true" />
          新建任务
        </Button>
        <Button
          type="button"
          variant="ghost"
          className={cn(
            "h-8 w-full justify-start gap-2 px-2 text-[12px] font-normal text-stone-700 hover:bg-stone-200/75",
            activeView === "agents" && "bg-[#e7e7e5] text-stone-950",
          )}
          onClick={() => onOpenView("agents")}
        >
          <Bot className="size-4 text-stone-500" aria-hidden="true" />
          智能体
        </Button>
        <Button
          type="button"
          variant="ghost"
          className={cn(
            "h-8 w-full justify-start gap-2 px-2 text-[12px] font-normal text-stone-700 hover:bg-stone-200/75",
            activeView === "plugins" && "bg-[#e7e7e5] text-stone-950",
          )}
          onClick={() => onOpenView("plugins")}
        >
          <Puzzle className="size-4 text-stone-500" aria-hidden="true" />
          插件
        </Button>
        <Button
          type="button"
          variant="ghost"
          className={cn(
            "h-8 w-full justify-start gap-2 px-2 text-[12px] font-normal text-stone-700 hover:bg-stone-200/75",
            activeView === "archive" && "bg-[#e7e7e5] text-stone-950",
          )}
          onClick={() => onOpenView("archive")}
        >
          <Archive className="size-4 text-stone-500" aria-hidden="true" />
          已归档
        </Button>
      </div>

      <Separator className="bg-stone-200/80" />

      <ScrollArea className="min-h-0 flex-1">
        <div className="px-2 py-3">
          <div className="mb-1 flex h-7 items-center px-2 text-[11px] font-medium text-stone-500">
            最近任务
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

          <div className="mb-1 mt-4 flex h-7 items-center px-2 text-[11px] font-medium text-stone-500">
            工作区
          </div>

          <div className="flex h-8 w-full items-center gap-2 rounded-md px-2 text-left text-[12px] text-stone-700">
            <Folder className="size-3.5 text-stone-500" aria-hidden="true" />
            <span className="min-w-0 flex-1 truncate">{workspaceName}</span>
          </div>
        </div>
      </ScrollArea>

      <div className="shrink-0 px-2 pb-2 pt-1">
        <Button
          type="button"
          variant="ghost"
          className={cn(
            "h-8 w-full justify-start gap-2 px-2 text-[12px] font-normal text-stone-700 hover:bg-stone-200/75",
            activeView === "settings" && "bg-[#e7e7e5] text-stone-950",
          )}
          onClick={() => onOpenView("settings")}
        >
          <Settings className="size-4 text-stone-500" aria-hidden="true" />
          设置
        </Button>
      </div>
    </div>
  );
}
