import { Braces, Boxes } from "lucide-react";
import { useTranslation } from "react-i18next";

import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { useWorkspaceUiStore } from "@/store/workspace-ui-store";
import type { BackendKind } from "@/types/application-service";

interface BackendSwitcherProps {
  readonly compact?: boolean;
}

/**
 * 以紧凑分段控件切换 Rust 与 .NET 独立后端画像。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function BackendSwitcher({ compact = false }: BackendSwitcherProps) {
  const { t } = useTranslation();
  const backend = useWorkspaceUiStore((state) => state.backend);
  const setBackend = useWorkspaceUiStore((state) => state.setBackend);

  /**
   * 忽略 Radix 单选组清空事件，只接受合法后端值。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleValueChange = (value: string) => {
    if (value === "rust" || value === "dotnet") {
      setBackend(value satisfies BackendKind);
    }
  };

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <ToggleGroup
          type="single"
          value={backend}
          onValueChange={handleValueChange}
          aria-label={t("backend.choose")}
          className={cn(
            "gap-0 rounded-md border bg-background p-0.5 shadow-sm",
            compact ? "h-7" : "h-8",
          )}
        >
          <ToggleGroupItem
            value="rust"
            aria-label={t("backend.useRust")}
            className={cn(
              "gap-1.5 rounded px-2 text-[11px] font-medium text-muted-foreground shadow-none data-[state=on]:bg-muted data-[state=on]:text-foreground data-[state=on]:shadow-none",
              compact ? "h-6" : "h-7",
            )}
          >
            <Braces className="size-3" aria-hidden="true" />
            Rust
          </ToggleGroupItem>
          <ToggleGroupItem
            value="dotnet"
            aria-label={t("backend.useDotnet")}
            className={cn(
              "gap-1.5 rounded px-2 text-[11px] font-medium text-muted-foreground shadow-none data-[state=on]:bg-muted data-[state=on]:text-foreground data-[state=on]:shadow-none",
              compact ? "h-6" : "h-7",
            )}
          >
            <Boxes className="size-3" aria-hidden="true" />
            .NET
          </ToggleGroupItem>
        </ToggleGroup>
      </TooltipTrigger>
      <TooltipContent side="top">{t("backend.switchHint")}</TooltipContent>
    </Tooltip>
  );
}
