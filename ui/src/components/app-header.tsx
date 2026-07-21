import {
  GitCommitHorizontal,
  Menu,
  PanelRight,
} from "lucide-react";

import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";

interface AppHeaderProps {
  readonly title: string;
  readonly projectName: string;
  readonly implementationLabel: string;
  readonly connected: boolean;
  readonly previewReviewAvailable: boolean;
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
  projectName,
  implementationLabel,
  connected,
  previewReviewAvailable,
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
        <span className="hidden max-w-48 truncate text-[11px] text-stone-400 sm:inline">
          {projectName}
        </span>
      </div>

      {previewReviewAvailable ? (
        <div className="flex shrink-0 items-center gap-1">
          <Button
            type="button"
            variant="outline"
            size="sm"
            className="hidden h-7 gap-1.5 rounded-md border-stone-200 px-2.5 text-[11px] font-normal shadow-none lg:inline-flex"
            onClick={onOpenReview}
          >
            <GitCommitHorizontal className="size-3" aria-hidden="true" />
            预览变更
          </Button>

          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                type="button"
                variant="ghost"
                size="icon"
                className="size-8 text-stone-500 lg:hidden"
                onClick={onOpenReview}
                aria-label="打开预览变更"
              >
                <PanelRight className="size-4" aria-hidden="true" />
              </Button>
            </TooltipTrigger>
            <TooltipContent>预览变更</TooltipContent>
          </Tooltip>
        </div>
      ) : null}
    </header>
  );
}
