import type { FormEvent, KeyboardEvent } from "react";
import {
  ArrowUp,
  Bot,
  GitBranch,
  LoaderCircle,
  LockKeyhole,
  Paperclip,
  Server,
} from "lucide-react";

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

interface ComposerProps {
  readonly value: string;
  readonly selectedModelRouteKey: string;
  readonly models: readonly ModelDescriptor[];
  readonly agentProfiles: readonly AgentProfile[];
  readonly selectedAgentProfileId: string | null;
  readonly runtimeEnvironment?: RuntimeEnvironment;
  readonly submitMode: ComposerSubmitMode;
  readonly busy: boolean;
  readonly onValueChange: (value: string) => void;
  readonly onModelChange: (modelRouteKey: string) => void;
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
  agentProfiles,
  selectedAgentProfileId,
  runtimeEnvironment,
  submitMode,
  busy,
  onValueChange,
  onModelChange,
  onAgentChange,
  onSubmit,
}: ComposerProps) {
  const selectedAgent = agentProfiles.find(
    (profile) => profile.profileId === selectedAgentProfileId,
  );
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
    <div className="shrink-0 bg-white px-3 pb-2 pt-2 sm:px-6 sm:pb-3 sm:pt-0">
      <form
        onSubmit={handleSubmit}
        className="mx-auto w-full max-w-[760px] rounded-2xl border border-stone-200 bg-white p-2 shadow-[0_1px_4px_rgba(28,25,23,0.07)] focus-within:border-stone-300 focus-within:shadow-[0_2px_8px_rgba(28,25,23,0.09)]"
      >
        <Textarea
          value={value}
          onChange={(event) => onValueChange(event.target.value)}
          onKeyDown={handleKeyDown}
          placeholder="向 Forge 发送任务"
          aria-label="任务消息"
          className="min-h-[54px] resize-none border-0 px-2 py-1 text-[13px] leading-5 shadow-none placeholder:text-stone-400 focus-visible:ring-0"
        />

        <div className="flex h-9 items-center gap-1">
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                type="button"
                variant="ghost"
                size="icon"
                className="size-8 rounded-full text-stone-500"
                disabled
                aria-label="添加附件"
              >
                <Paperclip className="size-4" aria-hidden="true" />
              </Button>
            </TooltipTrigger>
            <TooltipContent>添加附件</TooltipContent>
          </Tooltip>

          <Select
            value={selectedAgentProfileId ?? "__default__"}
            onValueChange={(profileId) =>
              onAgentChange(profileId === "__default__" ? null : profileId)
            }
          >
            <SelectTrigger
              aria-label="选择智能体"
              className="h-8 w-auto min-w-[96px] max-w-[150px] gap-1 border-0 px-2 text-[11px] font-medium shadow-none focus:ring-0"
            >
              <Bot className="size-3.5 shrink-0 text-stone-400" aria-hidden="true" />
              <SelectValue placeholder="默认智能体" />
            </SelectTrigger>
            <SelectContent align="start">
              <SelectItem value="__default__">默认智能体</SelectItem>
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
            disabled={models.length === 0 || selectedAgent !== undefined}
          >
            <SelectTrigger
              aria-label="选择模型"
              className="h-8 w-auto min-w-[96px] max-w-[150px] gap-1 border-0 px-2 text-[11px] font-medium shadow-none focus:ring-0"
            >
              <SelectValue placeholder="加载模型" />
            </SelectTrigger>
            <SelectContent align="start">
              {models.map((model) => (
                <SelectItem key={getModelRouteKey(model)} value={getModelRouteKey(model)}>
                  {model.displayName} · {model.providerId}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>

          <div className="flex-1" />

          <Button
            type="submit"
            size="icon"
            className="size-8 rounded-full bg-stone-800 text-white shadow-none hover:bg-stone-700"
            disabled={busy || value.trim().length === 0 || models.length === 0}
            aria-label={busy ? "正在提交" : "发送任务"}
          >
            {busy ? (
              <LoaderCircle className="size-4 animate-spin" aria-hidden="true" />
            ) : (
              <ArrowUp className="size-4" aria-hidden="true" />
            )}
          </Button>
        </div>
      </form>

      <div className="mx-auto mt-1.5 flex h-7 w-full max-w-[760px] items-center gap-2 overflow-hidden px-1 text-[10px] text-stone-400">
        <BackendSwitcher compact />
        <div className="hidden min-w-0 items-center gap-1.5 sm:flex">
          <Server className="size-3 shrink-0" aria-hidden="true" />
          <span className="truncate">
            {runtimeEnvironment?.supportsLocalDaemon ? "本地服务" : "远程 / 预览"}
          </span>
        </div>
        <div className="hidden items-center gap-1.5 md:flex">
          <LockKeyhole className="size-3" aria-hidden="true" />
          {selectedAgent?.dangerousActionMode === "deny" ? "只读" : "标准审批"}
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
