import { useMemo, useState } from "react";
import type { TFunction } from "i18next";
import type { LucideIcon } from "lucide-react";
import {
  BarChart3,
  BookOpenText,
  Check,
  ChevronRight,
  CircleAlert,
  Github,
  MonitorCog,
  Palette,
  Plus,
  Search,
  Settings2,
  ShieldCheck,
  Trash2,
} from "lucide-react";
import { useTranslation } from "react-i18next";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { ScrollArea } from "@/components/ui/scroll-area";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetTitle,
} from "@/components/ui/sheet";
import { Switch } from "@/components/ui/switch";
import {
  ToggleGroup,
  ToggleGroupItem,
} from "@/components/ui/toggle-group";
import { WorkspacePageHeader } from "@/components/workspace-page-header";
import { AGENT_TOOL_PRESENTATIONS } from "@/data/agent-tools";
import {
  filterPluginCatalog,
  getPluginCapabilityStatus,
  PLUGIN_CATALOG,
  PLUGIN_CATEGORY_TRANSLATION_KEYS,
  type PluginCapabilityStatus,
  type PluginCatalogEntry,
  type PluginCategory,
} from "@/features/plugins/plugin-catalog";
import { cn } from "@/lib/utils";
import {
  usePluginMarketplaceStore,
  type PluginLocalPreference,
} from "@/store/plugin-marketplace-store";

interface PluginWorkspaceProps {
  readonly supportedToolIds: readonly string[];
  readonly supportedMethodIds: readonly string[];
  readonly supportedEventTypes: readonly string[];
  readonly loading: boolean;
  readonly onOpenAgentTools: () => void;
  readonly onOpenMobileSidebar: () => void;
}

interface PluginVisual {
  readonly icon: LucideIcon;
  readonly className: string;
}

interface PluginCardProps {
  readonly plugin: PluginCatalogEntry;
  readonly capability: PluginCapabilityStatus;
  readonly preference: PluginLocalPreference | undefined;
  readonly loading: boolean;
  readonly onInstall: () => void;
  readonly onUninstall: () => void;
  readonly onEnabledChange: (enabled: boolean) => void;
  readonly onOpenDetails: () => void;
}

interface PluginDetailsProps {
  readonly plugin: PluginCatalogEntry | null;
  readonly capability: PluginCapabilityStatus | null;
  readonly preference: PluginLocalPreference | undefined;
  readonly loading: boolean;
  readonly onClose: () => void;
  readonly onInstall: () => void;
  readonly onUninstall: () => void;
  readonly onEnabledChange: (enabled: boolean) => void;
}

const PLUGIN_VISUALS: Readonly<Record<string, PluginVisual>> = {
  "product-design": {
    icon: Palette,
    className: "bg-rose-50 text-rose-700 dark:bg-rose-950/50 dark:text-rose-300",
  },
  "computer-use": {
    icon: MonitorCog,
    className: "bg-foreground text-background",
  },
  "openai-docs": {
    icon: BookOpenText,
    className: "bg-emerald-50 text-emerald-700 dark:bg-emerald-950/50 dark:text-emerald-300",
  },
  "data-analytics": {
    icon: BarChart3,
    className: "bg-sky-50 text-sky-700 dark:bg-sky-950/50 dark:text-sky-300",
  },
  "github-workflow": {
    icon: Github,
    className: "bg-violet-50 text-violet-700 dark:bg-violet-950/50 dark:text-violet-300",
  },
};

const DEFAULT_PLUGIN_VISUAL: PluginVisual = {
  icon: ShieldCheck,
  className: "bg-muted text-muted-foreground",
};

/**
 * 将中立工具 ID 转换为固定注册表中的展示名称。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function getToolLabel(toolId: string, t: TFunction): string {
  const presentation = AGENT_TOOL_PRESENTATIONS.find((tool) => tool.toolId === toolId);
  return presentation
    ? t(`tools.${toolId.replaceAll(".", "_")}.name`, {
        defaultValue: presentation.displayName,
      })
    : toolId;
}

/**
 * 为插件 capability 交集生成紧凑且不夸大的状态标签。
 *
 * 输入：目录项、真实交集状态和 initialize 加载状态。
 * 输出：仅描述当前后端实际广告结果的本地化标签。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function getCapabilityLabel(
  plugin: PluginCatalogEntry,
  capability: PluginCapabilityStatus,
  loading: boolean,
  t: TFunction,
): string {
  if (loading) {
    return t("marketplace.status.detecting");
  }
  if (capability.available && capability.missingToolIds.length === 0) {
    return t("marketplace.status.backendReady");
  }
  if (
    capability.missingToolIds.length > 0 &&
    capability.availableToolIds.length > 0
  ) {
    return t("marketplace.status.availableCount", {
      available: capability.availableToolIds.length,
      total: plugin.requiredToolIds.length,
    });
  }
  if (
    capability.missingMethodIds.length > 0 ||
    capability.missingEventTypes.length > 0
  ) {
    return t("marketplace.status.approvalUnavailable");
  }
  return plugin.pluginId === "computer-use"
    ? t("marketplace.status.backendNotInstalled")
    : t("marketplace.status.capabilityUnavailable");
}

/**
 * 显示插件目录中的本地 Lucide 标识。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function PluginIcon({ pluginId, size = "normal" }: { readonly pluginId: string; readonly size?: "normal" | "large" }) {
  const visual = PLUGIN_VISUALS[pluginId] ?? DEFAULT_PLUGIN_VISUAL;
  const Icon = visual.icon;

  return (
    <span
      className={cn(
        "flex shrink-0 items-center justify-center rounded-md",
        size === "large" ? "size-12" : "size-10",
        visual.className,
      )}
      aria-hidden="true"
    >
      <Icon className={size === "large" ? "size-5" : "size-4"} />
    </span>
  );
}

/**
 * 展示单个市场目录项及其设备本地安装、启用操作。
 *
 * 不变量：启用开关只在 capability 交集可用时可操作。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function PluginCard({
  plugin,
  capability,
  preference,
  loading,
  onInstall,
  onUninstall,
  onEnabledChange,
  onOpenDetails,
}: PluginCardProps) {
  const { t } = useTranslation();
  const installed = preference?.installed === true;
  const enabled = installed && preference.enabled;
  const capabilityReady = enabled && capability.available && !loading;

  return (
    <article className="flex min-h-[208px] flex-col rounded-lg border border-border bg-card p-4 transition-colors hover:border-foreground/20">
      <div className="flex min-w-0 items-start gap-3">
        <PluginIcon pluginId={plugin.pluginId} />
        <div className="min-w-0 flex-1">
          <div className="flex min-w-0 flex-wrap items-center gap-1.5">
            <h3 className="truncate text-[13px] font-semibold text-foreground">
              {plugin.name}
            </h3>
            <Badge variant="secondary" className="h-5 px-1.5 text-[9px] font-medium text-muted-foreground">
              {t(PLUGIN_CATEGORY_TRANSLATION_KEYS[plugin.category])}
            </Badge>
          </div>
          <p className="mt-0.5 text-[10px] text-muted-foreground/70">
            {plugin.publisher} · v{plugin.version}
          </p>
        </div>
        {installed ? (
          <div className="flex shrink-0 items-center gap-1.5">
            <Switch
              checked={enabled}
              onCheckedChange={onEnabledChange}
              disabled={!enabled && (loading || !capability.available)}
              aria-label={t("plugins.enabled", { name: plugin.name })}
            />
            <Button
              type="button"
              variant="ghost"
              size="icon"
              className="size-7 text-muted-foreground hover:text-destructive"
              onClick={onUninstall}
              aria-label={t("marketplace.uninstallNamed", { name: plugin.name })}
            >
              <Trash2 className="size-3.5" aria-hidden="true" />
            </Button>
          </div>
        ) : (
          <Button
            type="button"
            variant="outline"
            size="sm"
            className="h-8 shrink-0 gap-1.5 px-2.5 text-[10px] shadow-none"
            onClick={onInstall}
            aria-label={t("marketplace.installNamed", { name: plugin.name })}
          >
            <Plus className="size-3" aria-hidden="true" />
            {t("marketplace.install")}
          </Button>
        )}
      </div>

      <p className="mt-3 line-clamp-2 text-[11px] leading-5 text-muted-foreground">
        {t(plugin.summaryKey)}
      </p>

      <div className="mt-3 flex flex-wrap gap-1.5">
        {plugin.tagKeys.map((tagKey) => (
          <span key={tagKey} className="rounded bg-muted px-1.5 py-0.5 text-[9px] text-muted-foreground">
            {t(tagKey)}
          </span>
        ))}
      </div>

      <div className="mt-auto flex min-w-0 items-end justify-between gap-3 pt-4">
        <div className="min-w-0">
          <div className="flex items-center gap-1.5">
            <span
              className={cn(
                "size-1.5 shrink-0 rounded-full",
                capability.available && !loading ? "bg-emerald-500" : "bg-muted-foreground/30",
              )}
              aria-hidden="true"
            />
            <span className="truncate text-[10px] font-medium text-muted-foreground">
              {getCapabilityLabel(plugin, capability, loading, t)}
            </span>
          </div>
          {installed ? (
            <p className={cn("mt-1 text-[9px]", capabilityReady ? "text-emerald-700 dark:text-emerald-300" : "text-muted-foreground/70")}>
              {capabilityReady
                ? t("marketplace.status.enabledReady")
                : enabled && !capability.available
                  ? t("marketplace.status.enabledUnavailable")
                  : t("marketplace.status.installedDisabled")}
            </p>
          ) : null}
        </div>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          className="h-7 shrink-0 gap-1 px-2 text-[10px] font-normal text-muted-foreground"
          onClick={onOpenDetails}
          aria-label={t("marketplace.details", { name: plugin.name })}
        >
          {t("marketplace.detailAction")}
          <ChevronRight className="size-3" aria-hidden="true" />
        </Button>
      </div>
    </article>
  );
}

/**
 * 在侧边详情面板中解释插件来源、能力交集和本地状态。
 *
 * 不变量：面板只展示 initialize 返回的 capability 交集，不提供额外工具授权。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function PluginDetails({
  plugin,
  capability,
  preference,
  loading,
  onClose,
  onInstall,
  onUninstall,
  onEnabledChange,
}: PluginDetailsProps) {
  const { t } = useTranslation();
  const installed = preference?.installed === true;
  const enabled = installed && preference.enabled;

  return (
    <Sheet open={plugin !== null} onOpenChange={(open) => !open && onClose()}>
      <SheetContent side="right" className="w-full overflow-y-auto p-0 sm:max-w-md">
        {plugin && capability ? (
          <div className="flex min-h-full flex-col">
            <div className="border-b border-border px-5 pb-5 pt-6 pr-12">
              <div className="flex items-start gap-3">
                <PluginIcon pluginId={plugin.pluginId} size="large" />
                <div className="min-w-0 flex-1">
                  <SheetTitle className="text-[15px] text-foreground">
                    {plugin.name}
                  </SheetTitle>
                  <SheetDescription className="mt-1 text-[10px] text-muted-foreground/70">
                    {plugin.publisher} · v{plugin.version} · {t(PLUGIN_CATEGORY_TRANSLATION_KEYS[plugin.category])}
                  </SheetDescription>
                </div>
              </div>
              <p className="mt-4 text-[11px] leading-5 text-muted-foreground">
                {t(plugin.descriptionKey)}
              </p>
            </div>

            <div className="space-y-6 px-5 py-5">
              <section aria-labelledby="plugin-capability-title">
                <div className="flex items-center justify-between gap-3">
                  <h3 id="plugin-capability-title" className="text-[11px] font-semibold text-foreground">
                    {t("marketplace.backendCapabilities")}
                  </h3>
                  <Badge
                    variant="secondary"
                    className={cn(
                      "text-[9px] font-medium",
                      capability.available && !loading
                        ? "bg-emerald-50 text-emerald-700 dark:bg-emerald-950/50 dark:text-emerald-300"
                        : "text-muted-foreground",
                    )}
                  >
                    {getCapabilityLabel(plugin, capability, loading, t)}
                  </Badge>
                </div>
                <div className="mt-3 space-y-1">
                  {plugin.requiredToolIds.map((toolId) => {
                    const advertised = capability.availableToolIds.includes(toolId);
                    return (
                      <div key={toolId} className="flex min-h-8 items-center gap-2 rounded-md bg-muted/50 px-2.5 py-1.5">
                        <span
                          className={cn(
                            "flex size-4 shrink-0 items-center justify-center rounded",
                            advertised
                              ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-950/60 dark:text-emerald-300"
                              : "bg-muted text-muted-foreground",
                          )}
                        >
                          {advertised ? (
                            <Check className="size-3" aria-hidden="true" />
                          ) : (
                            <CircleAlert className="size-3" aria-hidden="true" />
                          )}
                        </span>
                        <span className="min-w-0 flex-1 truncate text-[10px] text-foreground/80">
                          {getToolLabel(toolId, t)}
                        </span>
                        <code className="max-w-36 truncate text-[9px] text-muted-foreground/70">
                          {toolId}
                        </code>
                      </div>
                    );
                  })}
                  {plugin.requiredMethodIds.map((methodId) => {
                    const advertised = capability.availableMethodIds.includes(methodId);
                    return (
                      <div key={methodId} className="flex min-h-8 items-center gap-2 rounded-md bg-muted/50 px-2.5 py-1.5">
                        <span
                          className={cn(
                            "flex size-4 shrink-0 items-center justify-center rounded",
                            advertised
                              ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-950/60 dark:text-emerald-300"
                              : "bg-muted text-muted-foreground",
                          )}
                        >
                          {advertised ? (
                            <Check className="size-3" aria-hidden="true" />
                          ) : (
                            <CircleAlert className="size-3" aria-hidden="true" />
                          )}
                        </span>
                        <span className="min-w-0 flex-1 truncate text-[10px] text-foreground/80">
                          {t("marketplace.methodRequirement")}
                        </span>
                        <code className="max-w-44 truncate text-[9px] text-muted-foreground/70">
                          {methodId}
                        </code>
                      </div>
                    );
                  })}
                  {plugin.requiredEventTypes.map((eventType) => {
                    const advertised = capability.availableEventTypes.includes(eventType);
                    return (
                      <div key={eventType} className="flex min-h-8 items-center gap-2 rounded-md bg-muted/50 px-2.5 py-1.5">
                        <span
                          className={cn(
                            "flex size-4 shrink-0 items-center justify-center rounded",
                            advertised
                              ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-950/60 dark:text-emerald-300"
                              : "bg-muted text-muted-foreground",
                          )}
                        >
                          {advertised ? (
                            <Check className="size-3" aria-hidden="true" />
                          ) : (
                            <CircleAlert className="size-3" aria-hidden="true" />
                          )}
                        </span>
                        <span className="min-w-0 flex-1 truncate text-[10px] text-foreground/80">
                          {t("marketplace.eventRequirement")}
                        </span>
                        <code className="max-w-44 truncate text-[9px] text-muted-foreground/70">
                          {eventType}
                        </code>
                      </div>
                    );
                  })}
                </div>
              </section>

              <section className="border-t border-border pt-5" aria-labelledby="plugin-safety-title">
                <div className="flex items-center gap-2">
                  <ShieldCheck className="size-4 text-muted-foreground" aria-hidden="true" />
                  <h3 id="plugin-safety-title" className="text-[11px] font-semibold text-foreground">
                    {t("marketplace.runtimeBoundary")}
                  </h3>
                </div>
                <p className="mt-2 text-[10px] leading-5 text-muted-foreground">
                  {t("marketplace.runtimeDescription")}
                </p>
                {!capability.available && plugin.pluginId === "computer-use" ? (
                  <p className="mt-2 rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-[10px] leading-5 text-amber-800 dark:border-amber-900 dark:bg-amber-950/40 dark:text-amber-200">
                    {capability.missingMethodIds.length > 0 ||
                    capability.missingEventTypes.length > 0
                      ? t("marketplace.computerApprovalUnavailable")
                      : (
                          <>
                            {t(
                              capability.availableToolIds.length > 0
                                ? "marketplace.computerPartialBefore"
                                : "marketplace.computerUnavailableBefore",
                            )}{" "}
                            <code>computer.*</code>{" "}
                            {t(
                              capability.availableToolIds.length > 0
                                ? "marketplace.computerPartialAfter"
                                : "marketplace.computerUnavailableAfter",
                            )}
                          </>
                        )}
                  </p>
                ) : null}
              </section>
            </div>

            <div className="mt-auto flex items-center justify-end gap-2 border-t border-border px-5 py-4">
              {installed ? (
                <>
                  <div className="mr-auto flex items-center gap-2">
                    <Switch
                      checked={enabled}
                      onCheckedChange={onEnabledChange}
                      disabled={!enabled && (loading || !capability.available)}
                      aria-label={t("marketplace.enableInDetails", { name: plugin.name })}
                    />
                    <span className="text-[10px] text-muted-foreground">
                      {enabled ? t("common.enabled") : t("common.disabled")}
                    </span>
                  </div>
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    className="h-8 gap-1.5 text-[10px] text-destructive shadow-none"
                    onClick={onUninstall}
                  >
                    <Trash2 className="size-3" aria-hidden="true" />
                    {t("marketplace.uninstall")}
                  </Button>
                </>
              ) : (
                <Button type="button" size="sm" className="h-8 gap-1.5 text-[10px]" onClick={onInstall}>
                  <Plus className="size-3" aria-hidden="true" />
                  {t("marketplace.installPlugin")}
                </Button>
              )}
            </div>
          </div>
        ) : null}
      </SheetContent>
    </Sheet>
  );
}

/**
 * 展示可搜索、筛选和设备本地管理的插件市场。
 *
 * 输入：当前 application service 的真实工具、方法与事件广告；输出：市场与本地偏好视图。
 * 不变量：页面不会补全 initialize 未广告的能力，且不会执行插件代码。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function PluginWorkspace({
  supportedToolIds,
  supportedMethodIds,
  supportedEventTypes,
  loading,
  onOpenAgentTools,
  onOpenMobileSidebar,
}: PluginWorkspaceProps) {
  const { t } = useTranslation();
  const preferences = usePluginMarketplaceStore((state) => state.plugins);
  const installPlugin = usePluginMarketplaceStore((state) => state.installPlugin);
  const uninstallPlugin = usePluginMarketplaceStore((state) => state.uninstallPlugin);
  const setPluginEnabled = usePluginMarketplaceStore(
    (state) => state.setPluginEnabled,
  );
  const [view, setView] = useState<"browse" | "installed">("browse");
  const [category, setCategory] = useState<PluginCategory | "all">("all");
  const [query, setQuery] = useState("");
  const [selectedPluginId, setSelectedPluginId] = useState<string | null>(null);

  const installedPluginIds = useMemo(
    () =>
      new Set(
        Object.entries(preferences)
          .filter(([, preference]) => preference.installed)
          .map(([pluginId]) => pluginId),
      ),
    [preferences],
  );
  const filteredPlugins = useMemo(
    () => filterPluginCatalog(installedPluginIds, view, category, query, (key) => t(key)),
    [category, installedPluginIds, query, t, view],
  );
  const capabilityByPluginId = useMemo(
    () =>
      Object.fromEntries(
        PLUGIN_CATALOG.map((plugin) => [
          plugin.pluginId,
          getPluginCapabilityStatus(
            plugin,
            supportedToolIds,
            supportedMethodIds,
            supportedEventTypes,
          ),
        ]),
      ) as Readonly<Record<string, PluginCapabilityStatus>>,
    [supportedEventTypes, supportedMethodIds, supportedToolIds],
  );
  const selectedPlugin =
    PLUGIN_CATALOG.find((plugin) => plugin.pluginId === selectedPluginId) ?? null;
  const readyPluginCount = PLUGIN_CATALOG.filter((plugin) => {
    const preference = preferences[plugin.pluginId];
    return (
      preference?.installed &&
      preference.enabled &&
      capabilityByPluginId[plugin.pluginId]?.available
    );
  }).length;

  return (
    <div className="flex min-h-0 flex-1 flex-col bg-background">
      <WorkspacePageHeader
        title={t("plugins.title")}
        description={t("plugins.description")}
        onOpenMobileSidebar={onOpenMobileSidebar}
        actions={
          <Button
            type="button"
            variant="outline"
            size="sm"
            className="h-8 gap-1.5 px-2.5 text-[10px] shadow-none"
            onClick={onOpenAgentTools}
          >
            <Settings2 className="size-3" aria-hidden="true" />
            {t("plugins.configureTools")}
          </Button>
        }
      />

      <ScrollArea className="min-h-0 flex-1">
        <main className="mx-auto w-full max-w-6xl px-4 py-6 sm:px-8">
          <div className="mb-5 flex flex-wrap items-end justify-between gap-3">
            <div>
              <h2 className="text-[14px] font-semibold text-foreground">{t("marketplace.title")}</h2>
              <p className="mt-1 text-[11px] leading-5 text-muted-foreground">
                {t("marketplace.description")}
              </p>
              <p className="mt-0.5 text-[9px] leading-4 text-muted-foreground/70">
                {t("marketplace.localOnlyDescription")}
              </p>
            </div>
            <div className="flex items-center gap-2">
              <Badge variant="outline" className="text-[9px] font-medium text-muted-foreground">
                {t("marketplace.installedCount", { count: installedPluginIds.size })}
              </Badge>
              <Badge variant="outline" className="text-[9px] font-medium text-muted-foreground">
                {t("marketplace.readyCount", { count: readyPluginCount })}
              </Badge>
            </div>
          </div>

          <div className="mb-5 flex flex-col gap-3 border-b border-border pb-4 sm:flex-row sm:items-center">
            <ToggleGroup
              type="single"
              value={view}
              onValueChange={(value) => {
                if (value === "browse" || value === "installed") {
                  setView(value);
                }
              }}
              aria-label={t("marketplace.viewLabel")}
              className="h-8 shrink-0 rounded-md bg-muted p-0.5"
            >
              <ToggleGroupItem
                value="browse"
                className="h-7 rounded px-3 text-[10px] shadow-none data-[state=on]:bg-background data-[state=on]:text-foreground"
              >
                {t("marketplace.browse")}
              </ToggleGroupItem>
              <ToggleGroupItem
                value="installed"
                className="h-7 rounded px-3 text-[10px] shadow-none data-[state=on]:bg-background data-[state=on]:text-foreground"
              >
                {t("marketplace.installed")}
              </ToggleGroupItem>
            </ToggleGroup>

            <div className="relative min-w-0 flex-1 sm:max-w-sm">
              <Search className="pointer-events-none absolute left-2.5 top-1/2 size-3.5 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
              <Input
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                placeholder={t("marketplace.searchPlaceholder")}
                aria-label={t("marketplace.searchLabel")}
                className="h-8 pl-8 text-[10px] shadow-none"
              />
            </div>

            <Select
              value={category}
              onValueChange={(value) => setCategory(value as PluginCategory | "all")}
            >
              <SelectTrigger className="h-8 w-full text-[10px] shadow-none sm:w-32" aria-label={t("marketplace.filterCategory") }>
                <SelectValue placeholder={t("marketplace.allCategories")} />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="all" className="text-[10px]">{t("marketplace.allCategories")}</SelectItem>
                {(Object.keys(PLUGIN_CATEGORY_TRANSLATION_KEYS) as PluginCategory[]).map((value) => (
                  <SelectItem key={value} value={value} className="text-[10px]">
                    {t(PLUGIN_CATEGORY_TRANSLATION_KEYS[value])}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          {filteredPlugins.length > 0 ? (
            <div className="grid gap-3 lg:grid-cols-2">
              {filteredPlugins.map((plugin) => (
                <PluginCard
                  key={plugin.pluginId}
                  plugin={plugin}
                  capability={capabilityByPluginId[plugin.pluginId]}
                  preference={preferences[plugin.pluginId]}
                  loading={loading}
                  onInstall={() => installPlugin(plugin.pluginId)}
                  onUninstall={() => uninstallPlugin(plugin.pluginId)}
                  onEnabledChange={(enabled) => {
                    if (
                      !enabled ||
                      (!loading && capabilityByPluginId[plugin.pluginId].available)
                    ) {
                      setPluginEnabled(plugin.pluginId, enabled);
                    }
                  }}
                  onOpenDetails={() => setSelectedPluginId(plugin.pluginId)}
                />
              ))}
            </div>
          ) : (
            <div className="flex min-h-56 flex-col items-center justify-center border-y border-border text-center">
              <Search className="size-5 text-muted-foreground/40" aria-hidden="true" />
              <p className="mt-3 text-[11px] font-medium text-foreground/80">{t("marketplace.noResults")}</p>
              <p className="mt-1 text-[10px] text-muted-foreground/70">{t("marketplace.noResultsHelp")}</p>
            </div>
          )}
        </main>
      </ScrollArea>

      <PluginDetails
        plugin={selectedPlugin}
        capability={
          selectedPlugin ? capabilityByPluginId[selectedPlugin.pluginId] : null
        }
        preference={selectedPlugin ? preferences[selectedPlugin.pluginId] : undefined}
        loading={loading}
        onClose={() => setSelectedPluginId(null)}
        onInstall={() => selectedPlugin && installPlugin(selectedPlugin.pluginId)}
        onUninstall={() => selectedPlugin && uninstallPlugin(selectedPlugin.pluginId)}
        onEnabledChange={(enabled) => {
          if (
            selectedPlugin &&
            (!enabled ||
              (!loading &&
                capabilityByPluginId[selectedPlugin.pluginId].available))
          ) {
            setPluginEnabled(selectedPlugin.pluginId, enabled);
          }
        }}
      />
    </div>
  );
}
