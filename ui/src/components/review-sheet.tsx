import { CheckCircle2, FileCode2, GitCommitHorizontal } from "lucide-react";
import { useTranslation } from "react-i18next";

import { Button } from "@/components/ui/button";
import { ScrollArea } from "@/components/ui/scroll-area";
import { Separator } from "@/components/ui/separator";
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet";
import { CHANGED_FILES } from "@/data/workspace-fixtures";

interface ReviewSheetProps {
  readonly open: boolean;
  readonly onOpenChange: (open: boolean) => void;
}

/**
 * 在不改变主会话布局的情况下展示变更审阅投影。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function ReviewSheet({ open, onOpenChange }: ReviewSheetProps) {
  const { t } = useTranslation();

  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent className="flex w-[360px] max-w-[92vw] flex-col gap-0 p-0 sm:max-w-[400px]">
        <SheetHeader className="shrink-0 px-5 pb-4 pt-5 text-left">
          <SheetTitle className="text-[14px]">{t("review.title")}</SheetTitle>
          <SheetDescription className="text-[11px]">{t("review.description")}</SheetDescription>
        </SheetHeader>
        <Separator />
        <ScrollArea className="min-h-0 flex-1">
          <div className="p-4">
            <div className="mb-4 flex items-center gap-2 text-[12px] text-foreground/80">
              <CheckCircle2 className="size-4 text-emerald-600" aria-hidden="true" />
              <span>{t("review.completed")}</span>
            </div>
            <div className="space-y-1">
              {CHANGED_FILES.map((file) => (
                <div
                  key={file.path}
                  className="flex w-full items-center gap-2 rounded-md px-2 py-2.5 text-left text-[11px]"
                >
                  <FileCode2 className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
                  <span className="min-w-0 flex-1 truncate font-mono text-[10px] text-foreground/80">
                    {file.path}
                  </span>
                  <span className="text-emerald-600">+{file.additions}</span>
                </div>
              ))}
            </div>
          </div>
        </ScrollArea>
        <div className="shrink-0 border-t p-4">
          <Button type="button" className="h-9 w-full gap-2 text-[12px]" onClick={() => onOpenChange(false)}>
            <GitCommitHorizontal className="size-4" aria-hidden="true" />
            {t("review.back")}
          </Button>
        </div>
      </SheetContent>
    </Sheet>
  );
}
