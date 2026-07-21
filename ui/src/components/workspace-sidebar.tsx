import {
  Bot,
  ChevronDown,
  CirclePlus,
  Clock3,
  Folder,
  FolderPlus,
  PanelLeftClose,
  Pin,
  Settings,
  Sparkles,
  SquareTerminal,
} from "lucide-react";

import { Button } from "@/components/ui/button";
import { ScrollArea } from "@/components/ui/scroll-area";
import { Separator } from "@/components/ui/separator";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import type { SessionFixture } from "@/data/workspace-fixtures";
import { cn } from "@/lib/utils";
import { useWorkspaceUiStore } from "@/store/workspace-ui-store";

interface WorkspaceSidebarProps {
  readonly onRequestClose?: () => void;
  readonly onCreateSession: () => void;
  readonly onOpenAgents: () => void;
  readonly onSelectSession: (sessionId: string) => void;
  readonly navigationDisabled?: boolean;
  readonly sessions: readonly SessionFixture[];
}

/**
 * 展示项目导航与仅用于预览的最近 Session 列表。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function WorkspaceSidebar({
  onRequestClose,
  onCreateSession,
  onOpenAgents,
  onSelectSession,
  navigationDisabled = false,
  sessions,
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
          onClick={onOpenAgents}
        >
          <Bot className="size-4 text-stone-500" aria-hidden="true" />
          智能体
        </Button>
        <Button
          type="button"
          variant="ghost"
          className="h-8 w-full justify-start gap-2 px-2 text-[12px] font-normal text-stone-700 hover:bg-stone-200/75"
        >
          <Clock3 className="size-4 text-stone-500" aria-hidden="true" />
          自动任务
        </Button>
        <Button
          type="button"
          variant="ghost"
          className="h-8 w-full justify-start gap-2 px-2 text-[12px] font-normal text-stone-700 hover:bg-stone-200/75"
        >
          <Sparkles className="size-4 text-stone-500" aria-hidden="true" />
          技能
        </Button>
      </div>

      <Separator className="bg-stone-200/80" />

      <ScrollArea className="min-h-0 flex-1">
        <div className="px-2 py-3">
          <div className="mb-1 flex h-7 items-center px-2 text-[11px] font-medium text-stone-500">
            最近任务
          </div>
          <div className="space-y-0.5">
            {sessions.slice(0, 3).map((session) => (
              <button
                key={session.id}
                type="button"
                onClick={() => onSelectSession(session.id)}
                disabled={navigationDisabled}
                className={cn(
                  "group flex h-8 w-full min-w-0 items-center gap-2 rounded-md px-2 text-left text-[12px] transition-colors hover:bg-stone-200/80 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-stone-400 disabled:pointer-events-none disabled:opacity-50",
                  activeView === "conversation" &&
                    activeSessionId === session.id &&
                    "bg-[#e7e7e5] text-stone-950",
                )}
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
                  <span className="shrink-0 text-[10px] text-stone-400 group-hover:text-stone-500">
                    {session.age}
                  </span>
                )}
              </button>
            ))}
          </div>

          <div className="mb-1 mt-4 flex h-7 items-center justify-between px-2 text-[11px] font-medium text-stone-500">
            <span>工作区</span>
            <Tooltip>
              <TooltipTrigger asChild>
                <Button
                  type="button"
                  variant="ghost"
                  size="icon"
                  className="size-6 text-stone-400 hover:text-stone-700"
                  aria-label="添加工作区"
                >
                  <FolderPlus className="size-3.5" aria-hidden="true" />
                </Button>
              </TooltipTrigger>
              <TooltipContent side="right">添加工作区</TooltipContent>
            </Tooltip>
          </div>

          <button
            type="button"
            className="flex h-8 w-full items-center gap-2 rounded-md px-2 text-left text-[12px] text-stone-700 hover:bg-stone-200/80"
          >
            <Folder className="size-3.5 text-stone-500" aria-hidden="true" />
            <span className="min-w-0 flex-1 truncate">AI-Code</span>
            <ChevronDown className="size-3 text-stone-400" aria-hidden="true" />
          </button>

          <div className="ml-3 mt-0.5 space-y-0.5 border-l border-stone-200 pl-2">
            {sessions.slice(0, 4).map((session) => (
              <button
                key={`workspace-${session.id}`}
                type="button"
                onClick={() => onSelectSession(session.id)}
                disabled={navigationDisabled}
                className={cn(
                  "flex h-8 w-full min-w-0 items-center gap-2 rounded-md px-2 text-left text-[12px] hover:bg-stone-200/80 disabled:pointer-events-none disabled:opacity-50",
                  activeView === "conversation" &&
                    activeSessionId === session.id &&
                    "bg-[#e7e7e5] text-stone-950",
                )}
              >
                <Bot className="size-3.5 shrink-0 text-stone-400" aria-hidden="true" />
                <span className="min-w-0 flex-1 truncate">{session.title}</span>
              </button>
            ))}
          </div>
        </div>
      </ScrollArea>

      <div className="shrink-0 px-2 pb-2 pt-1">
        <Button
          type="button"
          variant="ghost"
          className="h-8 w-full justify-start gap-2 px-2 text-[12px] font-normal text-stone-700 hover:bg-stone-200/75"
        >
          <Settings className="size-4 text-stone-500" aria-hidden="true" />
          设置
        </Button>
      </div>
    </div>
  );
}
