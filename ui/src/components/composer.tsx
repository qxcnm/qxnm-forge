import type { FormEvent, KeyboardEvent } from "react";
import {
  ArrowUp,
  Bot,
  GitBranch,
  LoaderCircle,
  LockKeyhole,
  Paperclip,
  RefreshCw,
  Server,
} from "lucide-react";
import { useTranslation } from "react-i18next";

import { BackendSwitcher } from "@/components/backend-switcher";
import { Button } from "@/components/ui/button";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { getModelRouteKey } from "@/lib/model-route";
import type { ComposerSubmitMode } from "@/store/workspace-ui-store";
import type { ModelDescriptor, RuntimeEnvironment } from "@/types/application-service";
import type { AgentProfile } from "@/types/agent-profile";

export type ComposerModelLoadState =
  | "loading"
  | "error"
  | "empty"
  | "unsupported"
  | "ready";

interface ComposerProps {
  readonly value: string;
  readonly selectedModelRouteKey: string;
  readonly models: readonly ModelDescriptor[];
  readonly modelLoadState: ComposerModelLoadState;
  readonly agentProfiles: readonly AgentProfile[];
  readonly selectedAgentProfileId: string | null;
  readonly runtimeEnvironment?: RuntimeEnvironment;
  readonly submitMode: ComposerSubmitMode;
  readonly busy: boolean;
  readonly onValueChange: (value: string) => void;
  readonly onModelChange: (modelRouteKey: string) => void;
  readonly onRetryModels: () => void;
  readonly onAgentChange: (profileId: string | null) => void;
  readonly onSubmit: () => void;
}

/**
 * 提供消息输入、模型选择和后端选择的主任务入口。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function Composer({
  value,
  selectedModelRouteKey,
  models,
  modelLoadState,
  agentProfiles,
  selectedAgentProfileId,
  runtimeEnvironment,
  submitMode,
  busy,
  onValueChange,
  onModelChange,
  onRetryModels,
  onAgentChange,
  onSubmit,
}: ComposerProps) {
  const { t } = useTranslation();
  const selectedAgent = agentProfiles.find(
    (profile) => profile.profileId === selectedAgentProfileId,
  );
  const modelStatusLabel =
    modelLoadState === "error"
      ? t("composer.modelLoadFailed")
      : modelLoadState === "empty"
        ? t("composer.noModels")
        : modelLoadState === "unsupported"
          ? t("composer.modelsUnsupported")
          : t("composer.loadingModels");
  /**
   * 提交非空消息并阻止浏览器刷新。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleSubmit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!busy && value.trim().length > 0) {
      onSubmit();
    }
  };

  /**
   * 按当前界面偏好处理 Enter 或组合键发送。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    const shouldSubmit =
      event.key === "Enter" &&
      (submitMode === "enter"
        ? !event.shiftKey && !event.metaKey && !event.ctrlKey
        : (event.metaKey || event.ctrlKey) && !event.shiftKey);
    if (shouldSubmit) {
      event.preventDefault();
      if (!busy && value.trim().length > 0) {
        onSubmit();
      }
    }
  };

  return (
    <div className="shrink-0 bg-background px-3 pb-2 pt-2 sm:px-6 sm:pb-3 sm:pt-0">
      <form
        onSubmit={handleSubmit}
        className="mx-auto w-full max-w-[760px] rounded-2xl border bg-background p-2 shadow-sm focus-within:border-ring focus-within:ring-1 focus-within:ring-ring/20"
      >
        <Textarea
          value={value}
          onChange={(event) => onValueChange(event.target.value)}
          onKeyDown={handleKeyDown}
          placeholder={t("composer.placeholder")}
          aria-label={t("composer.messageLabel")}
          className="min-h-[54px] resize-none border-0 px-2 py-1 text-[13px] leading-5 shadow-none placeholder:text-muted-foreground focus-visible:ring-0"
        />

        <div className="flex h-9 items-center gap-1">
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                type="button"
                variant="ghost"
                size="icon"
                className="size-8 rounded-full text-muted-foreground"
                disabled
                aria-label={t("composer.addAttachment")}
              >
                <Paperclip className="size-4" aria-hidden="true" />
              </Button>
            </TooltipTrigger>
            <TooltipContent>{t("composer.addAttachment")}</TooltipContent>
          </Tooltip>

          <Select
            value={selectedAgentProfileId ?? "__default__"}
            onValueChange={(profileId) =>
              onAgentChange(profileId === "__default__" ? null : profileId)
            }
          >
            <SelectTrigger
              aria-label={t("composer.chooseAgent")}
              className="h-8 w-auto min-w-[96px] max-w-[150px] gap-1 border-0 px-2 text-[11px] font-medium shadow-none focus:ring-0"
            >
              <Bot className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
              <SelectValue>
                {selectedAgent?.displayName ?? t("composer.defaultAgent")}
              </SelectValue>
            </SelectTrigger>
            <SelectContent align="start">
              <SelectItem value="__default__">{t("composer.defaultAgent")}</SelectItem>
              {agentProfiles
                .filter((profile) => profile.enabled)
                .map((profile) => (
                  <SelectItem key={profile.profileId} value={profile.profileId}>
                    {profile.displayName}
                  </SelectItem>
                ))}
            </SelectContent>
          </Select>

          <Select
            value={selectedModelRouteKey}
            onValueChange={onModelChange}
            disabled={modelLoadState !== "ready"}
          >
            <SelectTrigger
              aria-label={t("composer.chooseModel")}
              className="h-8 w-auto min-w-[96px] max-w-[150px] gap-1 border-0 px-2 text-[11px] font-medium shadow-none focus:ring-0"
            >
              <SelectValue placeholder={modelStatusLabel} />
            </SelectTrigger>
            <SelectContent align="start">
              {models.map((model) => (
                <SelectItem key={getModelRouteKey(model)} value={getModelRouteKey(model)}>
                  {model.displayName} · {model.providerId}/{model.apiFamily}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>

          {modelLoadState === "error" ? (
            <Tooltip>
              <TooltipTrigger asChild>
                <Button
                  type="button"
                  variant="ghost"
                  size="icon"
                  className="size-8 shrink-0 rounded-full text-muted-foreground"
                  onClick={onRetryModels}
                  aria-label={t("composer.retryModels")}
                >
                  <RefreshCw className="size-3.5" aria-hidden="true" />
                </Button>
              </TooltipTrigger>
              <TooltipContent>{t("composer.retryModels")}</TooltipContent>
            </Tooltip>
          ) : null}

          <div className="flex-1" />

          <Button
            type="submit"
            size="icon"
            className="size-8 rounded-full bg-primary text-primary-foreground shadow-none hover:bg-primary/90"
            disabled={busy || value.trim().length === 0 || modelLoadState !== "ready"}
            aria-label={busy ? t("composer.submitting") : t("composer.send")}
          >
            {busy ? (
              <LoaderCircle className="size-4 animate-spin" aria-hidden="true" />
            ) : (
              <ArrowUp className="size-4" aria-hidden="true" />
            )}
          </Button>
        </div>
      </form>

      <div className="mx-auto mt-1.5 flex h-7 w-full max-w-[760px] items-center gap-2 overflow-hidden px-1 text-[10px] text-muted-foreground">
        <BackendSwitcher compact />
        <div className="hidden min-w-0 items-center gap-1.5 sm:flex">
          <Server className="size-3 shrink-0" aria-hidden="true" />
          <span className="truncate">
            {runtimeEnvironment?.supportsLocalDaemon ? t("composer.localService") : t("composer.remotePreview")}
          </span>
        </div>
        <div className="hidden items-center gap-1.5 md:flex">
          <LockKeyhole className="size-3" aria-hidden="true" />
          {selectedAgent?.dangerousActionMode === "deny" ? t("composer.readOnly") : t("composer.standardApproval")}
        </div>
        <div className="flex-1" />
        <div className="flex min-w-0 items-center gap-1.5">
          <GitBranch className="size-3 shrink-0" aria-hidden="true" />
          <span className="max-w-28 truncate">main</span>
        </div>
      </div>
    </div>
  );
}
