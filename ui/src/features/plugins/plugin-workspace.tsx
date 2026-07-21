import { useMemo } from "react";
import { Check, MonitorCog, Settings2, ShieldCheck, Wrench } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { ScrollArea } from "@/components/ui/scroll-area";
import { WorkspacePageHeader } from "@/components/workspace-page-header";
import { AGENT_TOOL_PRESENTATIONS } from "@/data/agent-tools";

interface PluginWorkspaceProps {
  readonly supportedToolIds: readonly string[];
  readonly loading: boolean;
  readonly onOpenAgentTools: () => void;
  readonly onOpenMobileSidebar: () => void;
}

/**
 * 将 Computer 工具 ID 转换为面向用户的权限名称。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function getComputerPermissionLabel(toolId: string): string {
  const labels: Readonly<Record<string, string>> = {
    "computer.observe": "查看屏幕",
    "computer.screenshot": "截取屏幕",
    "computer.interact": "控制鼠标与键盘",
  };
  return labels[toolId] ?? toolId;
}

/**
 * 将普通工具 ID 转换为固定注册表中的展示名称。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function getToolLabel(toolId: string): string {
  return (
    AGENT_TOOL_PRESENTATIONS.find((tool) => tool.toolId === toolId)?.displayName ?? toolId
  );
}

/**
 * 展示服务真实广告的插件能力，并在能力缺失时保持控件不可用。
 *
 * 不变量：页面不会补全 initialize 未广告的 computer.* 工具。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function PluginWorkspace({
  supportedToolIds,
  loading,
  onOpenAgentTools,
  onOpenMobileSidebar,
}: PluginWorkspaceProps) {
  const computerToolIds = useMemo(
    () => supportedToolIds.filter((toolId) => toolId.startsWith("computer.")),
    [supportedToolIds],
  );
  const workspaceToolIds = useMemo(
    () => supportedToolIds.filter((toolId) => !toolId.startsWith("computer.")),
    [supportedToolIds],
  );
  const computerAvailable = computerToolIds.length > 0;

  return (
    <div className="flex min-h-0 flex-1 flex-col bg-white">
      <WorkspacePageHeader
        title="插件"
        description="管理服务已安装并明确广告的扩展能力"
        onOpenMobileSidebar={onOpenMobileSidebar}
      />
      <ScrollArea className="min-h-0 flex-1">
        <div className="mx-auto w-full max-w-4xl px-4 py-6 sm:px-8">
          <div className="mb-5 flex items-end justify-between gap-3">
            <div>
              <h2 className="text-[14px] font-semibold text-stone-900">能力目录</h2>
              <p className="mt-1 text-[11px] leading-5 text-stone-500">
                当前 application service 广告的内置工具与宿主扩展。
              </p>
            </div>
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="h-8 shrink-0 gap-1.5 px-2.5 text-[10px] shadow-none"
              onClick={onOpenAgentTools}
            >
              <Settings2 className="size-3" aria-hidden="true" />
              配置工具
            </Button>
          </div>

          <div className="space-y-3">
          <section className="rounded-lg border border-stone-200 bg-white" aria-labelledby="workspace-tools-title">
            <div className="flex items-start gap-3 p-4">
              <div className="flex size-9 shrink-0 items-center justify-center rounded-md bg-stone-100 text-stone-700">
                <Wrench className="size-4" aria-hidden="true" />
              </div>
              <div className="min-w-0 flex-1">
                <div className="flex min-w-0 flex-wrap items-center gap-2">
                  <h2 id="workspace-tools-title" className="text-[13px] font-semibold text-stone-900">
                    工作区工具
                  </h2>
                  <Badge variant="secondary" className="text-stone-600">
                    {loading ? "检测中" : `${workspaceToolIds.length} 项可用`}
                  </Badge>
                </div>
                <p className="mt-1 max-w-xl text-[11px] leading-5 text-stone-500">
                  文件、搜索、进程与 Shell 能力由所选后端注册，并继续受工作区边界和审批策略约束。
                </p>
              </div>
            </div>
            <div className="border-t border-stone-100 px-4 py-3">
              {workspaceToolIds.length > 0 ? (
                <div className="grid gap-x-6 gap-y-1 sm:grid-cols-2">
                  {workspaceToolIds.map((toolId) => (
                    <div key={toolId} className="flex h-8 min-w-0 items-center gap-2">
                      <span className="flex size-4 shrink-0 items-center justify-center rounded bg-emerald-50 text-emerald-700">
                        <Check className="size-3" aria-hidden="true" />
                      </span>
                      <span className="min-w-0 flex-1 truncate text-[11px] text-stone-700">
                        {getToolLabel(toolId)}
                      </span>
                      <code className="max-w-28 truncate text-[9px] text-stone-400">{toolId}</code>
                    </div>
                  ))}
                </div>
              ) : (
                <p className="text-[11px] leading-5 text-stone-400">当前服务未广告工作区工具。</p>
              )}
            </div>
          </section>

          <section className="rounded-lg border border-stone-200 bg-white" aria-labelledby="computer-plugin-title">
            <div className="flex items-start gap-3 p-4">
              <div className="flex size-9 shrink-0 items-center justify-center rounded-md bg-stone-900 text-white">
                <MonitorCog className="size-4" aria-hidden="true" />
              </div>
              <div className="min-w-0 flex-1">
                <div className="flex min-w-0 flex-wrap items-center gap-2">
                  <h2 id="computer-plugin-title" className="text-[13px] font-semibold text-stone-900">
                    Computer
                  </h2>
                  <Badge
                    variant="secondary"
                    className={computerAvailable ? "bg-emerald-50 text-emerald-700" : "text-stone-500"}
                  >
                    {loading ? "检测中" : computerAvailable ? "后端可用" : "后端未安装"}
                  </Badge>
                </div>
                <p className="mt-1 max-w-xl text-[11px] leading-5 text-stone-500">
                  允许智能体在审批与宿主策略范围内观察或操作桌面。实际权限取决于当前 application service。
                </p>
              </div>
            </div>

            <div className="border-t border-stone-100 px-4 py-3">
              <div className="mb-2 flex items-center gap-2 text-[11px] font-medium text-stone-700">
                <ShieldCheck className="size-3.5 text-stone-400" aria-hidden="true" />
                权限范围
              </div>
              {computerAvailable ? (
                <div className="space-y-1">
                  {computerToolIds.map((toolId) => (
                    <div key={toolId} className="flex h-8 items-center gap-2">
                      <span className="flex size-4 shrink-0 items-center justify-center rounded bg-emerald-50 text-emerald-700">
                        <Check className="size-3" aria-hidden="true" />
                      </span>
                      <span className="min-w-0 flex-1 text-[11px] font-normal text-stone-700">
                        {getComputerPermissionLabel(toolId)}
                      </span>
                      <code className="truncate text-[9px] text-stone-400">{toolId}</code>
                    </div>
                  ))}
                </div>
              ) : (
                <p className="text-[11px] leading-5 text-stone-400">
                  当前连接未广告任何 <code>computer.*</code> 工具，因此无法启用或选择权限。
                </p>
              )}
            </div>
          </section>
          </div>
        </div>
      </ScrollArea>
    </div>
  );
}
