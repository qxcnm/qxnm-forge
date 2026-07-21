import {
  ChevronDown,
  GitCommitHorizontal,
  Menu,
  MoreHorizontal,
  PanelRight,
  Play,
  Share2,
} from "lucide-react";

import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";

interface AppHeaderProps {
  readonly title: string;
  readonly implementationLabel: string;
  readonly connected: boolean;
  readonly onOpenMobileSidebar: () => void;
  readonly onOpenReview: () => void;
}

/**
 * 展示当前任务标题、连接状态和紧凑工作区命令。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function AppHeader({
  title,
  implementationLabel,
  connected,
  onOpenMobileSidebar,
  onOpenReview,
}: AppHeaderProps) {
  return (
    <header className="flex h-[52px] shrink-0 items-center border-b border-stone-100 bg-white px-2.5 sm:px-4">
      <Button
        type="button"
        variant="ghost"
        size="icon"
        className="mr-1 size-8 md:hidden"
        onClick={onOpenMobileSidebar}
        aria-label="打开项目导航"
      >
        <Menu className="size-4" aria-hidden="true" />
      </Button>

      <div className="flex min-w-0 flex-1 items-center gap-2">
        <div className="min-w-0">
          <div className="flex min-w-0 items-center gap-2">
            <h1 className="truncate text-[13px] font-semibold text-stone-900">{title}</h1>
            <span
              className={`size-1.5 shrink-0 rounded-full ${connected ? "bg-emerald-500" : "bg-amber-400"}`}
              aria-label={connected ? "服务已初始化" : "服务初始化中"}
            />
          </div>
          <p className="truncate text-[10px] text-stone-400 sm:hidden">{implementationLabel}</p>
        </div>
        <span className="hidden text-[11px] text-stone-400 sm:inline">AI-Code</span>
        <Button
          type="button"
          variant="ghost"
          size="icon"
          className="hidden size-7 text-stone-400 sm:inline-flex"
          aria-label="更多任务操作"
        >
          <MoreHorizontal className="size-4" aria-hidden="true" />
        </Button>
      </div>

      <div className="flex shrink-0 items-center gap-1">
        <Tooltip>
          <TooltipTrigger asChild>
            <Button
              type="button"
              variant="ghost"
              size="icon"
              className="size-8 text-stone-500"
              aria-label="运行当前任务"
            >
              <Play className="size-3.5" aria-hidden="true" />
            </Button>
          </TooltipTrigger>
          <TooltipContent>运行任务</TooltipContent>
        </Tooltip>

        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="hidden h-7 gap-1.5 rounded-md border-stone-200 px-2.5 text-[11px] font-normal shadow-none sm:inline-flex"
            >
              打开
              <ChevronDown className="size-3 text-stone-400" aria-hidden="true" />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end" className="w-44">
            <DropdownMenuItem>在编辑器中打开</DropdownMenuItem>
            <DropdownMenuItem>打开工作区终端</DropdownMenuItem>
            <DropdownMenuSeparator />
            <DropdownMenuItem>复制工作区路径</DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>

        <Button
          type="button"
          variant="outline"
          size="sm"
          className="hidden h-7 gap-1.5 rounded-md border-stone-200 px-2.5 text-[11px] font-normal shadow-none lg:inline-flex"
        >
          <Share2 className="size-3" aria-hidden="true" />
          交接
        </Button>

        <Button
          type="button"
          variant="outline"
          size="sm"
          className="hidden h-7 gap-1.5 rounded-md border-stone-200 px-2.5 text-[11px] font-normal shadow-none lg:inline-flex"
          onClick={onOpenReview}
        >
          <GitCommitHorizontal className="size-3" aria-hidden="true" />
          变更
          <span className="text-emerald-600">+253</span>
          <span className="text-rose-500">-0</span>
        </Button>

        <Tooltip>
          <TooltipTrigger asChild>
            <Button
              type="button"
              variant="ghost"
              size="icon"
              className="size-8 text-stone-500 lg:hidden"
              onClick={onOpenReview}
              aria-label="打开变更审阅"
            >
              <PanelRight className="size-4" aria-hidden="true" />
            </Button>
          </TooltipTrigger>
          <TooltipContent>变更审阅</TooltipContent>
        </Tooltip>
      </div>
    </header>
  );
}
