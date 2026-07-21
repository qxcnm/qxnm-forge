import { type ReactNode } from "react";
import {
  Database,
  Info,
  Keyboard,
  Palette,
  PanelLeft,
  Settings2,
  ShieldCheck,
  SlidersHorizontal,
  Waypoints,
} from "lucide-react";

import { BackendSwitcher } from "@/components/backend-switcher";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Separator } from "@/components/ui/separator";
import { Switch } from "@/components/ui/switch";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { WorkspacePageHeader } from "@/components/workspace-page-header";
import { ProviderSettings } from "@/features/settings/provider-settings";
import { useWorkspaceUiStore } from "@/store/workspace-ui-store";
import type {
  ApplicationServiceClient,
  BackendKind,
  InitializeResult,
  RuntimeEnvironment,
} from "@/types/application-service";

interface SettingsWorkspaceProps {
  readonly backend: BackendKind;
  readonly service: ApplicationServiceClient;
  readonly initializeResult?: InitializeResult;
  readonly runtimeEnvironment?: RuntimeEnvironment;
  readonly onOpenArchive: () => void;
  readonly onOpenAgentTools: () => void;
  readonly onOpenMobileSidebar: () => void;
}

interface SettingRowProps {
  readonly title: string;
  readonly description: string;
  readonly control: ReactNode;
}

/**
 * 以扫描友好的行布局展示单项设置与对应控件。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function SettingRow({ title, description, control }: SettingRowProps) {
  return (
    <div className="flex min-h-14 items-center gap-4 py-2.5">
      <div className="min-w-0 flex-1">
        <h3 className="text-[11px] font-medium text-stone-800">{title}</h3>
        <p className="mt-0.5 text-[10px] leading-4 text-stone-400">{description}</p>
      </div>
      <div className="shrink-0">{control}</div>
    </div>
  );
}

/**
 * 汇总 QXNM Forge 的常规、Provider、权限、数据与版本设置。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function SettingsWorkspace({
  backend,
  service,
  initializeResult,
  runtimeEnvironment,
  onOpenArchive,
  onOpenAgentTools,
  onOpenMobileSidebar,
}: SettingsWorkspaceProps) {
  const composerSubmitMode = useWorkspaceUiStore((state) => state.composerSubmitMode);
  const setComposerSubmitMode = useWorkspaceUiStore(
    (state) => state.setComposerSubmitMode,
  );
  const sidebarWidth = useWorkspaceUiStore((state) => state.sidebarWidth);
  const setSidebarWidth = useWorkspaceUiStore((state) => state.setSidebarWidth);
  const reduceMotion = useWorkspaceUiStore((state) => state.reduceMotion);
  const setReduceMotion = useWorkspaceUiStore((state) => state.setReduceMotion);
  const methods = initializeResult?.capabilities.methods ?? [];
  const tools = initializeResult?.capabilities.tools ?? [];
  const writeToolsAvailable = tools.some(
    (toolId) => toolId === "file.write" || toolId === "file.edit",
  );
  const processToolsAvailable = tools.some(
    (toolId) => toolId === "process.exec" || toolId === "shell.exec",
  );

  return (
    <div className="flex min-h-0 flex-1 flex-col bg-white">
      <WorkspacePageHeader
        title="设置"
        description="应用、连接与数据边界"
        onOpenMobileSidebar={onOpenMobileSidebar}
      />
      <Tabs defaultValue="general" className="flex min-h-0 flex-1 flex-col lg:flex-row">
        <TabsList className="scrollbar-none h-11 w-full shrink-0 justify-start gap-1 overflow-x-auto rounded-none border-b border-stone-100 bg-stone-50/70 px-3 py-1 text-stone-500 lg:h-full lg:w-48 lg:flex-col lg:items-stretch lg:justify-start lg:border-b-0 lg:border-r lg:px-2 lg:py-4">
          <TabsTrigger value="general" className="h-8 justify-start gap-2 px-2 text-[11px] shadow-none data-[state=active]:bg-white lg:w-full">
            <Settings2 className="size-3.5" aria-hidden="true" />
            常规
          </TabsTrigger>
          <TabsTrigger value="providers" className="h-8 justify-start gap-2 px-2 text-[11px] shadow-none data-[state=active]:bg-white lg:w-full">
            <Waypoints className="size-3.5" aria-hidden="true" />
            提供商
          </TabsTrigger>
          <TabsTrigger value="appearance" className="h-8 justify-start gap-2 px-2 text-[11px] shadow-none data-[state=active]:bg-white lg:w-full">
            <Palette className="size-3.5" aria-hidden="true" />
            外观
          </TabsTrigger>
          <TabsTrigger value="permissions" className="h-8 justify-start gap-2 px-2 text-[11px] shadow-none data-[state=active]:bg-white lg:w-full">
            <ShieldCheck className="size-3.5" aria-hidden="true" />
            权限
          </TabsTrigger>
          <TabsTrigger value="data" className="h-8 justify-start gap-2 px-2 text-[11px] shadow-none data-[state=active]:bg-white lg:w-full">
            <Database className="size-3.5" aria-hidden="true" />
            数据
          </TabsTrigger>
          <TabsTrigger value="about" className="h-8 justify-start gap-2 px-2 text-[11px] shadow-none data-[state=active]:bg-white lg:w-full">
            <Info className="size-3.5" aria-hidden="true" />
            关于
          </TabsTrigger>
        </TabsList>

        <div className="min-h-0 min-w-0 flex-1 overflow-y-auto">
          <TabsContent value="general" className="mx-auto mt-0 w-full max-w-3xl px-4 py-6 sm:px-8">
            <h2 className="text-[14px] font-semibold text-stone-900">常规</h2>
            <p className="mt-1 text-[10px] text-stone-400">非敏感界面偏好保存在当前设备。</p>
            <Separator className="mt-5" />
            <SettingRow
              title="后端实现"
              description="Rust 与 .NET 均通过相同的品牌中立 application service 协议连接。"
              control={<BackendSwitcher />}
            />
            <Separator />
            <SettingRow
              title="当前服务"
              description={initializeResult?.implementation.name ?? "正在与 application service 握手"}
              control={(
                <Badge variant="secondary" className="font-mono text-[9px] font-normal">
                  {initializeResult?.implementation.version ?? backend}
                </Badge>
              )}
            />
            <Separator />
            <SettingRow
              title="发送消息"
              description="选择输入器提交消息的按键。"
              control={(
                <ToggleGroup
                  type="single"
                  value={composerSubmitMode}
                  onValueChange={(value) => {
                    if (value === "enter" || value === "mod-enter") {
                      setComposerSubmitMode(value);
                    }
                  }}
                  aria-label="发送消息快捷键"
                  className="rounded-md bg-stone-100 p-0.5"
                >
                  <ToggleGroupItem value="enter" className="h-7 px-2 text-[9px]">
                    Enter
                  </ToggleGroupItem>
                  <ToggleGroupItem value="mod-enter" className="h-7 px-2 text-[9px]">
                    Ctrl / ⌘ Enter
                  </ToggleGroupItem>
                </ToggleGroup>
              )}
            />
          </TabsContent>

          <TabsContent value="providers" className="mx-auto mt-0 w-full max-w-5xl px-4 py-6 sm:px-8">
            <div className="mb-5">
              <h2 className="text-[14px] font-semibold text-stone-900">提供商</h2>
              <p className="mt-1 text-[10px] text-stone-400">连接配置与 CredentialStore 凭据分开保存。</p>
            </div>
            {initializeResult ? (
              <ProviderSettings
                key={backend}
                backend={backend}
                service={service}
                supportedMethods={methods}
              />
            ) : (
              <p className="border-y border-stone-100 py-10 text-center text-[11px] text-stone-400">
                正在读取 Provider capability...
              </p>
            )}
          </TabsContent>

          <TabsContent value="appearance" className="mx-auto mt-0 w-full max-w-3xl px-4 py-6 sm:px-8">
            <h2 className="text-[14px] font-semibold text-stone-900">外观</h2>
            <p className="mt-1 text-[10px] text-stone-400">调整当前设备上的工作台布局与动态效果。</p>
            <Separator className="mt-5" />
            <SettingRow
              title="项目导航宽度"
              description="紧凑模式为会话区留出更多横向空间。"
              control={(
                <ToggleGroup
                  type="single"
                  value={sidebarWidth}
                  onValueChange={(value) => {
                    if (value === "compact" || value === "standard") {
                      setSidebarWidth(value);
                    }
                  }}
                  aria-label="项目导航宽度"
                  className="rounded-md bg-stone-100 p-0.5"
                >
                  <ToggleGroupItem value="compact" className="h-7 gap-1 px-2 text-[9px]">
                    <PanelLeft className="size-3" aria-hidden="true" />
                    紧凑
                  </ToggleGroupItem>
                  <ToggleGroupItem value="standard" className="h-7 gap-1 px-2 text-[9px]">
                    <PanelLeft className="size-3.5" aria-hidden="true" />
                    标准
                  </ToggleGroupItem>
                </ToggleGroup>
              )}
            />
            <Separator />
            <SettingRow
              title="减少动态效果"
              description="关闭非必要的过渡与循环动画。"
              control={(
                <Switch
                  checked={reduceMotion}
                  onCheckedChange={setReduceMotion}
                  aria-label="减少动态效果"
                />
              )}
            />
          </TabsContent>

          <TabsContent value="permissions" className="mx-auto mt-0 w-full max-w-3xl px-4 py-6 sm:px-8">
            <h2 className="text-[14px] font-semibold text-stone-900">权限</h2>
            <p className="mt-1 text-[10px] text-stone-400">审批只会收窄后端广告的工具能力。</p>
            <Separator className="mt-5" />
            <SettingRow
              title="工作区写入审批"
              description="file.write 与 file.edit 继续由后端策略和逐次审批裁决。"
              control={(
                <Badge variant="outline" className="text-[9px] font-normal">
                  {writeToolsAvailable ? "后端审批" : "未广告"}
                </Badge>
              )}
            />
            <Separator />
            <SettingRow
              title="进程与 Shell 审批"
              description="process.exec 与 shell.exec 继续由后端策略和逐次审批裁决。"
              control={(
                <Badge variant="outline" className="text-[9px] font-normal">
                  {processToolsAvailable ? "后端审批" : "未广告"}
                </Badge>
              )}
            />
            <Separator />
            <SettingRow
              title="智能体工具子集"
              description="为每个智能体选择当前服务已广告的工具。"
              control={(
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  className="h-7 gap-1.5 text-[10px] shadow-none"
                  onClick={onOpenAgentTools}
                  disabled={!methods.includes("agentProfiles/list")}
                >
                  <Keyboard className="size-3" aria-hidden="true" />
                  配置工具
                </Button>
              )}
            />
            <Separator />
            <div className="py-4">
              <h3 className="text-[11px] font-medium text-stone-800">当前广告工具</h3>
              <div className="mt-3 flex flex-wrap gap-1.5">
                {tools.length > 0 ? tools.map((toolId) => (
                  <Badge key={toolId} variant="secondary" className="font-mono text-[9px] font-normal text-stone-600">
                    {toolId}
                  </Badge>
                )) : <span className="text-[10px] text-stone-400">当前服务未广告工具</span>}
              </div>
            </div>
          </TabsContent>

          <TabsContent value="data" className="mx-auto mt-0 w-full max-w-3xl px-4 py-6 sm:px-8">
            <h2 className="text-[14px] font-semibold text-stone-900">数据</h2>
            <p className="mt-1 text-[10px] text-stone-400">会话数据由 application service 管理。</p>
            <Separator className="mt-5" />
            <SettingRow
              title="已归档会话"
              description="恢复会话或执行经过确认的永久删除。"
              control={(
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  className="h-7 text-[10px] shadow-none"
                  onClick={onOpenArchive}
                  disabled={!methods.includes("session/list")}
                >
                  查看归档
                </Button>
              )}
            />
            <Separator />
            <SettingRow
              title="运行位置"
              description={runtimeEnvironment?.supportsLocalDaemon ? "使用桌面本地 daemon" : "使用远程或浏览器预览边界"}
              control={<Badge variant="secondary" className="text-[9px]">{runtimeEnvironment?.mode ?? "检测中"}</Badge>}
            />
          </TabsContent>

          <TabsContent value="about" className="mx-auto mt-0 w-full max-w-3xl px-4 py-6 sm:px-8">
            <div className="flex items-center gap-3">
              <div className="flex size-10 items-center justify-center rounded-md bg-stone-900 text-white">
                <SlidersHorizontal className="size-4" aria-hidden="true" />
              </div>
              <div>
                <h2 className="text-[14px] font-semibold text-stone-900">QXNM Forge</h2>
                <p className="mt-0.5 text-[10px] text-stone-400">
                  {initializeResult?.implementation.name ?? "application service 初始化中"}
                  {initializeResult ? ` · ${initializeResult.implementation.version}` : ""}
                </p>
              </div>
            </div>
            <Separator className="my-5" />
            <SettingRow title="作者" description="高宏顺" control={<span className="text-[10px] text-stone-500">18272669457@163.com</span>} />
            <Separator />
            <SettingRow title="实现" description="React + Tauri；Rust 与 .NET 独立后端" control={<Badge variant="outline" className="text-[9px]">Community</Badge>} />
          </TabsContent>
        </div>
      </Tabs>
    </div>
  );
}
