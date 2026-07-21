import type { ReactNode } from "react";
import { Menu } from "lucide-react";

import { Button } from "@/components/ui/button";

interface WorkspacePageHeaderProps {
  readonly title: string;
  readonly description: string;
  readonly onOpenMobileSidebar: () => void;
  readonly actions?: ReactNode;
}

/**
 * 为管理型一级视图提供一致的紧凑标题栏与移动导航入口。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function WorkspacePageHeader({
  title,
  description,
  onOpenMobileSidebar,
  actions,
}: WorkspacePageHeaderProps) {
  return (
    <header className="flex h-[52px] shrink-0 items-center gap-2 border-b border-stone-100 bg-white px-2.5 sm:px-4">
      <Button
        type="button"
        variant="ghost"
        size="icon"
        className="size-8 md:hidden"
        onClick={onOpenMobileSidebar}
        aria-label="打开项目导航"
      >
        <Menu className="size-4" aria-hidden="true" />
      </Button>
      <div className="min-w-0 flex-1">
        <h1 className="truncate text-[13px] font-semibold text-stone-900">{title}</h1>
        <p className="truncate text-[10px] text-stone-400">{description}</p>
      </div>
      {actions ? <div className="flex shrink-0 items-center gap-2">{actions}</div> : null}
    </header>
  );
}
