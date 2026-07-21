import { type ReactNode } from "react";
import {
  Database,
  Info,
  Keyboard,
  Languages,
  Monitor,
  Moon,
  Palette,
  PanelLeft,
  Settings2,
  ShieldCheck,
  SlidersHorizontal,
  Sun,
  Waypoints,
} from "lucide-react";
import { useTranslation } from "react-i18next";

import { BackendSwitcher } from "@/components/backend-switcher";
import { useInterfaceTheme } from "@/components/interface-theme";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
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
  ModelDescriptor,
  RuntimeEnvironment,
} from "@/types/application-service";

interface SettingsWorkspaceProps {
  readonly backend: BackendKind;
  readonly service: ApplicationServiceClient;
  readonly initializeResult?: InitializeResult;
  readonly runtimeEnvironment?: RuntimeEnvironment;
  readonly onOpenArchive: () => void;
  readonly onOpenAgentTools: () => void;
  readonly onModelReady: (
    backend: BackendKind,
    model: Pick<ModelDescriptor, "providerId" | "modelId" | "apiFamily">,
  ) => void;
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
        <h3 className="text-[11px] font-medium text-foreground">{title}</h3>
        <p className="mt-0.5 text-[10px] leading-4 text-muted-foreground">{description}</p>
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
  onModelReady,
  onOpenMobileSidebar,
}: SettingsWorkspaceProps) {
  const { t, i18n } = useTranslation();
  const { theme, setTheme } = useInterfaceTheme();
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
    <div className="flex min-h-0 flex-1 flex-col bg-background">
      <WorkspacePageHeader
        title={t("settings.title")}
        description={t("settings.description")}
        onOpenMobileSidebar={onOpenMobileSidebar}
      />
      <Tabs defaultValue="general" className="flex min-h-0 flex-1 flex-col lg:flex-row">
        <TabsList className="scrollbar-none h-11 w-full shrink-0 justify-start gap-1 overflow-x-auto rounded-none border-b bg-muted/60 px-3 py-1 text-muted-foreground lg:h-full lg:w-48 lg:flex-col lg:items-stretch lg:justify-start lg:border-b-0 lg:border-r lg:px-2 lg:py-4">
          <TabsTrigger value="general" className="h-8 justify-start gap-2 px-2 text-[11px] shadow-none data-[state=active]:bg-background lg:w-full">
            <Settings2 className="size-3.5" aria-hidden="true" />
            {t("settings.tabs.general")}
          </TabsTrigger>
          <TabsTrigger value="providers" className="h-8 justify-start gap-2 px-2 text-[11px] shadow-none data-[state=active]:bg-background lg:w-full">
            <Waypoints className="size-3.5" aria-hidden="true" />
            {t("settings.tabs.providers")}
          </TabsTrigger>
          <TabsTrigger value="appearance" className="h-8 justify-start gap-2 px-2 text-[11px] shadow-none data-[state=active]:bg-background lg:w-full">
            <Palette className="size-3.5" aria-hidden="true" />
            {t("settings.tabs.appearance")}
          </TabsTrigger>
          <TabsTrigger value="permissions" className="h-8 justify-start gap-2 px-2 text-[11px] shadow-none data-[state=active]:bg-background lg:w-full">
            <ShieldCheck className="size-3.5" aria-hidden="true" />
            {t("settings.tabs.permissions")}
          </TabsTrigger>
          <TabsTrigger value="data" className="h-8 justify-start gap-2 px-2 text-[11px] shadow-none data-[state=active]:bg-background lg:w-full">
            <Database className="size-3.5" aria-hidden="true" />
            {t("settings.tabs.data")}
          </TabsTrigger>
          <TabsTrigger value="about" className="h-8 justify-start gap-2 px-2 text-[11px] shadow-none data-[state=active]:bg-background lg:w-full">
            <Info className="size-3.5" aria-hidden="true" />
            {t("settings.tabs.about")}
          </TabsTrigger>
        </TabsList>

        <div className="min-h-0 min-w-0 flex-1 overflow-y-auto">
          <TabsContent value="general" className="mx-auto mt-0 w-full max-w-3xl px-4 py-6 sm:px-8">
            <h2 className="text-[14px] font-semibold text-foreground">{t("settings.tabs.general")}</h2>
            <p className="mt-1 text-[10px] text-muted-foreground">{t("settings.general.description")}</p>
            <Separator className="mt-5" />
            <SettingRow
              title={t("settings.general.backendTitle")}
              description={t("settings.general.backendDescription")}
              control={<BackendSwitcher />}
            />
            <Separator />
            <SettingRow
              title={t("settings.general.currentServiceTitle")}
              description={initializeResult?.implementation.name ?? t("settings.general.handshaking")}
              control={(
                <Badge variant="secondary" className="font-mono text-[9px] font-normal">
                  {initializeResult?.implementation.version ?? backend}
                </Badge>
              )}
            />
            <Separator />
            <SettingRow
              title={t("settings.general.sendTitle")}
              description={t("settings.general.sendDescription")}
              control={(
                <ToggleGroup
                  type="single"
                  value={composerSubmitMode}
                  onValueChange={(value) => {
                    if (value === "enter" || value === "mod-enter") {
                      setComposerSubmitMode(value);
                    }
                  }}
                  aria-label={t("settings.general.sendShortcut")}
                  className="rounded-md bg-muted p-0.5"
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
              <h2 className="text-[14px] font-semibold text-foreground">{t("settings.tabs.providers")}</h2>
              <p className="mt-1 text-[10px] text-muted-foreground">{t("settings.providers.description")}</p>
            </div>
            <div className={initializeResult ? undefined : "hidden"}>
              <ProviderSettings
                key={backend}
                backend={backend}
                service={service}
                supportedMethods={methods}
                onModelReady={onModelReady}
              />
            </div>
            {!initializeResult ? (
              <p className="border-y py-10 text-center text-[11px] text-muted-foreground">
                {t("settings.providers.loadingCapability")}
              </p>
            ) : null}
          </TabsContent>

          <TabsContent value="appearance" className="mx-auto mt-0 w-full max-w-3xl px-4 py-6 sm:px-8">
            <h2 className="text-[14px] font-semibold text-foreground">{t("settings.tabs.appearance")}</h2>
            <p className="mt-1 text-[10px] text-muted-foreground">{t("settings.appearance.description")}</p>
            <Separator className="mt-5" />
            <SettingRow
              title={t("settings.appearance.themeTitle")}
              description={t("settings.appearance.themeDescription")}
              control={(
                <ToggleGroup
                  type="single"
                  value={theme}
                  onValueChange={(value) => {
                    if (value === "light" || value === "dark" || value === "system") {
                      setTheme(value);
                    }
                  }}
                  aria-label={t("settings.appearance.themeLabel")}
                  className="rounded-md bg-muted p-0.5"
                >
                  <ToggleGroupItem value="system" className="h-7 gap-1 px-2 text-[9px]">
                    <Monitor className="size-3" aria-hidden="true" />
                    {t("settings.appearance.themeSystem")}
                  </ToggleGroupItem>
                  <ToggleGroupItem value="light" className="h-7 gap-1 px-2 text-[9px]">
                    <Sun className="size-3" aria-hidden="true" />
                    {t("settings.appearance.themeLight")}
                  </ToggleGroupItem>
                  <ToggleGroupItem value="dark" className="h-7 gap-1 px-2 text-[9px]">
                    <Moon className="size-3" aria-hidden="true" />
                    {t("settings.appearance.themeDark")}
                  </ToggleGroupItem>
                </ToggleGroup>
              )}
            />
            <Separator />
            <SettingRow
              title={t("settings.appearance.languageTitle")}
              description={t("settings.appearance.languageDescription")}
              control={(
                <Select
                  value={i18n.resolvedLanguage === "en-US" ? "en-US" : "zh-CN"}
                  onValueChange={(value) => void i18n.changeLanguage(value)}
                >
                  <SelectTrigger
                    className="h-8 w-36 gap-2 text-[10px] shadow-none"
                    aria-label={t("settings.appearance.languageLabel")}
                  >
                    <Languages className="size-3.5 text-muted-foreground" aria-hidden="true" />
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="zh-CN">{t("settings.appearance.languageChinese")}</SelectItem>
                    <SelectItem value="en-US">{t("settings.appearance.languageEnglish")}</SelectItem>
                  </SelectContent>
                </Select>
              )}
            />
            <Separator />
            <SettingRow
              title={t("settings.appearance.sidebarTitle")}
              description={t("settings.appearance.sidebarDescription")}
              control={(
                <ToggleGroup
                  type="single"
                  value={sidebarWidth}
                  onValueChange={(value) => {
                    if (value === "compact" || value === "standard") {
                      setSidebarWidth(value);
                    }
                  }}
                  aria-label={t("settings.appearance.sidebarLabel")}
                  className="rounded-md bg-muted p-0.5"
                >
                  <ToggleGroupItem value="compact" className="h-7 gap-1 px-2 text-[9px]">
                    <PanelLeft className="size-3" aria-hidden="true" />
                    {t("settings.appearance.compact")}
                  </ToggleGroupItem>
                  <ToggleGroupItem value="standard" className="h-7 gap-1 px-2 text-[9px]">
                    <PanelLeft className="size-3.5" aria-hidden="true" />
                    {t("settings.appearance.standard")}
                  </ToggleGroupItem>
                </ToggleGroup>
              )}
            />
            <Separator />
            <SettingRow
              title={t("settings.appearance.reduceMotionTitle")}
              description={t("settings.appearance.reduceMotionDescription")}
              control={(
                <Switch
                  checked={reduceMotion}
                  onCheckedChange={setReduceMotion}
                  aria-label={t("settings.appearance.reduceMotionLabel")}
                />
              )}
            />
          </TabsContent>

          <TabsContent value="permissions" className="mx-auto mt-0 w-full max-w-3xl px-4 py-6 sm:px-8">
            <h2 className="text-[14px] font-semibold text-foreground">{t("settings.tabs.permissions")}</h2>
            <p className="mt-1 text-[10px] text-muted-foreground">{t("settings.permissions.description")}</p>
            <Separator className="mt-5" />
            <SettingRow
              title={t("settings.permissions.workspaceWriteTitle")}
              description={t("settings.permissions.workspaceWriteDescription")}
              control={(
                <Badge variant="outline" className="text-[9px] font-normal">
                  {writeToolsAvailable ? t("settings.permissions.backendApproval") : t("settings.permissions.notAdvertised")}
                </Badge>
              )}
            />
            <Separator />
            <SettingRow
              title={t("settings.permissions.processTitle")}
              description={t("settings.permissions.processDescription")}
              control={(
                <Badge variant="outline" className="text-[9px] font-normal">
                  {processToolsAvailable ? t("settings.permissions.backendApproval") : t("settings.permissions.notAdvertised")}
                </Badge>
              )}
            />
            <Separator />
            <SettingRow
              title={t("settings.permissions.agentToolsTitle")}
              description={t("settings.permissions.agentToolsDescription")}
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
                  {t("settings.permissions.configureTools")}
                </Button>
              )}
            />
            <Separator />
            <div className="py-4">
              <h3 className="text-[11px] font-medium text-foreground">{t("settings.permissions.advertisedTools")}</h3>
              <div className="mt-3 flex flex-wrap gap-1.5">
                {tools.length > 0 ? tools.map((toolId) => (
                  <Badge key={toolId} variant="secondary" className="font-mono text-[9px] font-normal text-secondary-foreground">
                    {toolId}
                  </Badge>
                )) : <span className="text-[10px] text-muted-foreground">{t("settings.permissions.noAdvertisedTools")}</span>}
              </div>
            </div>
          </TabsContent>

          <TabsContent value="data" className="mx-auto mt-0 w-full max-w-3xl px-4 py-6 sm:px-8">
            <h2 className="text-[14px] font-semibold text-foreground">{t("settings.tabs.data")}</h2>
            <p className="mt-1 text-[10px] text-muted-foreground">{t("settings.data.description")}</p>
            <Separator className="mt-5" />
            <SettingRow
              title={t("settings.data.archiveTitle")}
              description={t("settings.data.archiveDescription")}
              control={(
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  className="h-7 text-[10px] shadow-none"
                  onClick={onOpenArchive}
                  disabled={!methods.includes("session/list")}
                >
                  {t("settings.data.viewArchive")}
                </Button>
              )}
            />
            <Separator />
            <SettingRow
              title={t("settings.data.runtimeTitle")}
              description={runtimeEnvironment?.supportsLocalDaemon ? t("settings.data.localDaemon") : t("settings.data.remoteBoundary")}
              control={<Badge variant="secondary" className="text-[9px]">{runtimeEnvironment?.mode ?? t("settings.data.detecting")}</Badge>}
            />
          </TabsContent>

          <TabsContent value="about" className="mx-auto mt-0 w-full max-w-3xl px-4 py-6 sm:px-8">
            <div className="flex items-center gap-3">
              <div className="flex size-10 items-center justify-center rounded-md bg-primary text-primary-foreground">
                <SlidersHorizontal className="size-4" aria-hidden="true" />
              </div>
              <div>
                <h2 className="text-[14px] font-semibold text-foreground">QXNM Forge</h2>
                <p className="mt-0.5 text-[10px] text-muted-foreground">
                  {initializeResult?.implementation.name ?? t("settings.about.serviceInitializing")}
                  {initializeResult ? ` · ${initializeResult.implementation.version}` : ""}
                </p>
              </div>
            </div>
            <Separator className="my-5" />
            <SettingRow title={t("settings.about.author")} description={t("settings.about.authorName")} control={<span className="text-[10px] text-muted-foreground">18272669457@163.com</span>} />
            <Separator />
            <SettingRow title={t("settings.about.implementation")} description={t("settings.about.implementationDescription")} control={<Badge variant="outline" className="text-[9px]">{t("common.community")}</Badge>} />
          </TabsContent>
        </div>
      </Tabs>
    </div>
  );
}
