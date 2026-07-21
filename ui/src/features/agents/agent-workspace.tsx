import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useMutation, useQueryClient, type QueryKey } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import {
  ArrowLeft,
  Bot,
  Copy,
  LoaderCircle,
  Menu,
  Plus,
  Save,
  Search,
  ShieldAlert,
  Trash2,
} from "lucide-react";

import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { ScrollArea } from "@/components/ui/scroll-area";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { AGENT_TOOL_PRESENTATIONS } from "@/data/agent-tools";
import { findModelByRouteKey, getModelRouteKey } from "@/lib/model-route";
import { cn } from "@/lib/utils";
import type { ModelDescriptor } from "@/types/application-service";
import type {
  AgentProfile,
  AgentProfileInput,
  AgentProfileService,
  AgentToolPresentation,
  DangerousActionMode,
  ResponseStyle,
} from "@/types/agent-profile";

interface AgentWorkspaceProps {
  readonly service: AgentProfileService;
  readonly profiles: readonly AgentProfile[];
  readonly profilesLoading: boolean;
  readonly profileQueryKey: QueryKey;
  readonly models: readonly ModelDescriptor[];
  readonly supportedToolIds: readonly string[];
  readonly canCreate: boolean;
  readonly canUpdate: boolean;
  readonly canDelete: boolean;
  readonly initialEditorTab?: "profile" | "tools";
  readonly onOpenMobileSidebar: () => void;
  readonly onNavigationStateChange: (state: AgentWorkspaceNavigationState) => void;
}

export interface AgentWorkspaceNavigationState {
  readonly dirty: boolean;
  readonly locked: boolean;
}

type SaveRequest =
  | { readonly kind: "create"; readonly input: AgentProfileInput }
  | {
      readonly kind: "update";
      readonly profileId: string;
      readonly expectedRevision: number;
      readonly input: AgentProfileInput;
    };

type PendingNavigation =
  | { readonly kind: "profile"; readonly profile: AgentProfile; readonly trigger: HTMLButtonElement }
  | { readonly kind: "create" }
  | { readonly kind: "back" };

const PERMISSION_LABEL_KEYS = {
  workspace_read: "agents.permissions.workspaceRead",
  workspace_write: "agents.permissions.workspaceWrite",
  process: "agents.permissions.process",
  shell: "agents.permissions.shell",
  computer_observe: "agents.permissions.computerObserve",
  computer_interact: "agents.permissions.computerInteract",
  extension: "agents.permissions.extension",
} as const;

const TOOL_TRANSLATION_KEYS: Readonly<
  Record<string, Readonly<{ displayName: string; description: string }>>
> = {
  "file.read": {
    displayName: "tools.file_read.name",
    description: "tools.file_read.description",
  },
  "search.text": {
    displayName: "tools.search_text.name",
    description: "tools.search_text.description",
  },
  "file.write": {
    displayName: "tools.file_write.name",
    description: "tools.file_write.description",
  },
  "file.edit": {
    displayName: "tools.file_edit.name",
    description: "tools.file_edit.description",
  },
  "process.exec": {
    displayName: "tools.process_exec.name",
    description: "tools.process_exec.description",
  },
  "shell.exec": {
    displayName: "tools.shell_exec.name",
    description: "tools.shell_exec.description",
  },
  "computer.observe": {
    displayName: "tools.computer_observe.name",
    description: "tools.computer_observe.description",
  },
  "computer.screenshot": {
    displayName: "tools.computer_screenshot.name",
    description: "tools.computer_screenshot.description",
  },
  "computer.interact": {
    displayName: "tools.computer_interact.name",
    description: "tools.computer_interact.description",
  },
};

/**
 * 从服务投影提取用户可编辑字段的独立副本。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function profileToInput(profile: AgentProfile): AgentProfileInput {
  return {
    displayName: profile.displayName,
    description: profile.description,
    enabled: profile.enabled,
    instructions: profile.instructions,
    model: { ...profile.model },
    requestedToolIds: [...profile.requestedToolIds],
    dangerousActionMode: profile.dangerousActionMode,
    behavior: { ...profile.behavior },
  };
}

/**
 * 使用首个服务端模型建立一份新智能体草稿。
 *
 * 失败：没有可用模型时返回 undefined，由页面禁用新建入口。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function createEmptyProfile(
  models: readonly ModelDescriptor[],
  defaultDisplayName: string,
  defaultInstructions: string,
): AgentProfileInput | undefined {
  const model = models[0];
  if (!model) {
    return undefined;
  }
  return {
    displayName: defaultDisplayName,
    description: "",
    enabled: true,
    instructions: defaultInstructions,
    model: {
      providerId: model.providerId,
      modelId: model.modelId,
      apiFamily: model.apiFamily,
    },
    requestedToolIds: [],
    dangerousActionMode: "ask",
    behavior: {
      responseStyle: "balanced",
      planFirst: true,
      reviewChanges: true,
    },
  };
}

/**
 * 将未知异常转换为可展示的脱敏消息。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function getErrorMessage(error: unknown, fallbackMessage: string): string {
  return error instanceof Error ? error.message : fallbackMessage;
}

/**
 * 提供响应式智能体列表与配置编辑器。
 *
 * 不变量：页面只操作 Faux AgentProfileService 投影；工具只能选择 initialize 已广告子集。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function AgentWorkspace({
  service,
  profiles,
  profilesLoading,
  profileQueryKey,
  models,
  supportedToolIds,
  canCreate,
  canUpdate,
  canDelete,
  initialEditorTab = "profile",
  onOpenMobileSidebar,
  onNavigationStateChange,
}: AgentWorkspaceProps) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [search, setSearch] = useState("");
  const [selectedProfileId, setSelectedProfileId] = useState<string | null>(null);
  const [editingRevision, setEditingRevision] = useState<number | null>(null);
  const [draft, setDraft] = useState<AgentProfileInput | null>(null);
  const [mobileEditorOpen, setMobileEditorOpen] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [pendingNavigation, setPendingNavigation] = useState<PendingNavigation | null>(null);
  const editorHeadingRef = useRef<HTMLHeadingElement>(null);
  const createButtonRef = useRef<HTMLButtonElement>(null);
  const lastProfileTriggerRef = useRef<HTMLButtonElement | null>(null);
  const previousMobileEditorOpenRef = useRef(false);

  const supportedToolIdSet = useMemo(
    () => new Set(supportedToolIds),
    [supportedToolIds],
  );
  const toolPresentations = useMemo(() => {
    const knownToolIds = new Set(AGENT_TOOL_PRESENTATIONS.map((tool) => tool.toolId));
    const knownTools = AGENT_TOOL_PRESENTATIONS.map((tool) => {
      const translationKeys = TOOL_TRANSLATION_KEYS[tool.toolId];
      return translationKeys
        ? {
            ...tool,
            displayName: t(translationKeys.displayName),
            description: t(translationKeys.description),
          }
        : tool;
    });
    const dynamicToolIds = new Set([
      ...supportedToolIds,
      ...(draft?.requestedToolIds ?? []),
    ]);
    const dynamicTools: AgentToolPresentation[] = [...dynamicToolIds]
      .filter((toolId) => !knownToolIds.has(toolId))
      .sort()
      .map((toolId) => ({
        toolId,
        displayName: toolId,
        description: t("agents.tools.dynamicDescription"),
        permissionClass: "extension",
        dangerous: true,
      }));
    return [...knownTools, ...dynamicTools];
  }, [draft?.requestedToolIds, supportedToolIds, t]);
  const selectedProfile = profiles.find((profile) => profile.profileId === selectedProfileId);
  const currentInput = selectedProfile ? profileToInput(selectedProfile) : null;
  const isNewProfile = draft !== null && selectedProfileId === null;
  const editorReadOnly = isNewProfile ? !canCreate : !canUpdate;
  const isDirty =
    draft !== null &&
    (isNewProfile || JSON.stringify(draft) !== JSON.stringify(currentInput));
  const normalizedSearch = search.trim().toLocaleLowerCase();
  const filteredProfiles = profiles.filter((profile) =>
    `${profile.displayName} ${profile.description}`.toLocaleLowerCase().includes(normalizedSearch),
  );

  /**
   * 把服务投影加载为当前编辑草稿。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const selectProfile = (profile: AgentProfile, openMobileEditor = true) => {
    setSelectedProfileId(profile.profileId);
    setEditingRevision(profile.revision);
    setDraft(profileToInput(profile));
    setMobileEditorOpen(openMobileEditor);
    setNotice(null);
    setErrorMessage(null);
  };

  useEffect(() => {
    if (draft === null && profiles[0]) {
      selectProfile(profiles[0], false);
    }
  }, [draft, profiles]);

  useEffect(() => {
    const wasMobileEditorOpen = previousMobileEditorOpenRef.current;
    previousMobileEditorOpenRef.current = mobileEditorOpen;
    if (mobileEditorOpen || !wasMobileEditorOpen) {
      return;
    }
    const profileTrigger = lastProfileTriggerRef.current;
    const focusTarget =
      profileTrigger?.isConnected && !profileTrigger.disabled
        ? profileTrigger
        : createButtonRef.current;
    const focusTimer = window.setTimeout(() => focusTarget?.focus(), 250);
    return () => window.clearTimeout(focusTimer);
  }, [mobileEditorOpen]);

  useEffect(() => {
    if (mobileEditorOpen) {
      editorHeadingRef.current?.focus();
    }
  }, [isNewProfile, mobileEditorOpen, selectedProfileId]);

  const saveMutation = useMutation({
    mutationFn: (request: SaveRequest) =>
      request.kind === "create"
        ? service.createProfile(request.input)
        : service.updateProfile(
            request.profileId,
            request.expectedRevision,
            request.input,
          ),
    onSuccess: (savedProfile) => {
      queryClient.setQueryData<readonly AgentProfile[]>(profileQueryKey, (current = []) => [
        savedProfile,
        ...current.filter((profile) => profile.profileId !== savedProfile.profileId),
      ]);
      setSelectedProfileId(savedProfile.profileId);
      setEditingRevision(savedProfile.revision);
      setDraft(profileToInput(savedProfile));
      setNotice(
        service.mode === "application-service"
          ? t("agents.notices.savedService")
          : t("agents.notices.savedPreview"),
      );
      setErrorMessage(null);
    },
    onError: (error) => {
      setNotice(null);
      setErrorMessage(getErrorMessage(error, t("agents.errors.requestFailed")));
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (profile: AgentProfile) =>
      service.deleteProfile(profile.profileId, profile.revision),
    onSuccess: (_result, deletedProfile) => {
      const remaining = profiles.filter(
        (profile) => profile.profileId !== deletedProfile.profileId,
      );
      queryClient.setQueryData<readonly AgentProfile[]>(profileQueryKey, remaining);
      const nextProfile = remaining[0];
      if (nextProfile) {
        selectProfile(nextProfile, false);
      } else {
        setSelectedProfileId(null);
        setEditingRevision(null);
        setDraft(null);
        setMobileEditorOpen(false);
      }
      setNotice(
        service.mode === "application-service"
          ? t("agents.notices.deletedService")
          : t("agents.notices.deletedPreview"),
      );
      setErrorMessage(null);
    },
    onError: (error) => {
      setNotice(null);
      setErrorMessage(getErrorMessage(error, t("agents.errors.requestFailed")));
    },
  });
  const navigationLocked = saveMutation.isPending || deleteMutation.isPending;

  useEffect(() => {
    onNavigationStateChange({ dirty: isDirty, locked: navigationLocked });
  }, [isDirty, navigationLocked, onNavigationStateChange]);

  useEffect(
    () => () => onNavigationStateChange({ dirty: false, locked: false }),
    [onNavigationStateChange],
  );

  /**
   * 建立不含持久化标识的新智能体草稿。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const beginCreate = () => {
    if (!canCreate) {
      return;
    }
    const emptyProfile = createEmptyProfile(
      models,
      t("agents.draft.defaultName"),
      t("agents.draft.defaultInstructions"),
    );
    if (!emptyProfile) {
      setErrorMessage(t("agents.errors.noModels"));
      return;
    }
    lastProfileTriggerRef.current = null;
    setSelectedProfileId(null);
    setEditingRevision(null);
    setDraft(emptyProfile);
    setMobileEditorOpen(true);
    setNotice(null);
    setErrorMessage(null);
  };

  /**
   * 在存在未保存修改时请求确认，否则直接进入新建草稿。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleCreate = () => {
    if (navigationLocked) {
      return;
    }
    if (isDirty) {
      setPendingNavigation({ kind: "create" });
      return;
    }
    beginCreate();
  };

  /**
   * 在切换智能体前保护未保存草稿并保留焦点返回目标。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleProfileRequest = (profile: AgentProfile, trigger: HTMLButtonElement) => {
    if (navigationLocked) {
      return;
    }
    lastProfileTriggerRef.current = trigger;
    if (profile.profileId === selectedProfileId) {
      setMobileEditorOpen(true);
      return;
    }
    if (isDirty) {
      setPendingNavigation({ kind: "profile", profile, trigger });
      return;
    }
    selectProfile(profile);
  };

  /**
   * 在移动端返回列表前保护未保存草稿。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleBackRequest = () => {
    if (navigationLocked) {
      return;
    }
    if (isDirty) {
      setPendingNavigation({ kind: "back" });
      return;
    }
    setMobileEditorOpen(false);
  };

  /**
   * 放弃当前草稿并执行用户已确认的导航动作。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const confirmDiscard = () => {
    const navigation = pendingNavigation;
    setPendingNavigation(null);
    if (!navigation) {
      return;
    }
    if (navigation.kind === "profile") {
      lastProfileTriggerRef.current = navigation.trigger;
      selectProfile(navigation.profile);
      return;
    }
    if (navigation.kind === "create") {
      beginCreate();
      return;
    }
    if (selectedProfile) {
      setEditingRevision(selectedProfile.revision);
      setDraft(profileToInput(selectedProfile));
    } else if (profiles[0]) {
      selectProfile(profiles[0], false);
      return;
    } else {
      setEditingRevision(null);
      setDraft(null);
    }
    setMobileEditorOpen(false);
  };

  /**
   * 保存新建草稿或按原 revision 更新当前投影。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleSave = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!draft || navigationLocked || editorReadOnly) {
      return;
    }
    if (selectedProfileId && editingRevision !== null) {
      saveMutation.mutate({
        kind: "update",
        profileId: selectedProfileId,
        expectedRevision: editingRevision,
        input: draft,
      });
      return;
    }
    saveMutation.mutate({ kind: "create", input: draft });
  };

  /**
   * 将当前投影复制为 revision 1 的独立草稿实体。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleDuplicate = () => {
    if (!draft || navigationLocked || !canCreate) {
      return;
    }
    saveMutation.mutate({
      kind: "create",
      input: {
        ...draft,
        displayName: t("agents.draft.copyName", { name: draft.displayName }).slice(0, 48),
        model: { ...draft.model },
        requestedToolIds: [...draft.requestedToolIds],
        behavior: { ...draft.behavior },
      },
    });
  };

  /**
   * 更新指定表单字段并清除上一条请求反馈。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const updateDraft = (nextDraft: AgentProfileInput) => {
    setDraft(nextDraft);
    setNotice(null);
    setErrorMessage(null);
  };

  /**
   * 通过完整模型路由 key 更新草稿模型。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleModelChange = (routeKey: string) => {
    const model = findModelByRouteKey(models, routeKey);
    if (!draft || !model) {
      return;
    }
    updateDraft({
      ...draft,
      model: {
        providerId: model.providerId,
        modelId: model.modelId,
        apiFamily: model.apiFamily,
      },
    });
  };

  /**
   * 切换工具请求子集，不生成工具定义或授权结论。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleToolChange = (toolId: string, checked: boolean) => {
    if (!draft || (checked && !supportedToolIdSet.has(toolId))) {
      return;
    }
    updateDraft({
      ...draft,
      requestedToolIds: checked
        ? [...new Set([...draft.requestedToolIds, toolId])]
        : draft.requestedToolIds.filter((currentToolId) => currentToolId !== toolId),
    });
  };

  /**
   * 切换危险操作策略；只读模式会立即移除所有危险工具请求。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleDangerousActionModeChange = (mode: DangerousActionMode) => {
    if (!draft) {
      return;
    }
    const dangerousToolIds = new Set(
      toolPresentations.filter((tool) => tool.dangerous).map((tool) => tool.toolId),
    );
    updateDraft({
      ...draft,
      dangerousActionMode: mode,
      requestedToolIds:
        mode === "deny"
          ? draft.requestedToolIds.filter((toolId) => !dangerousToolIds.has(toolId))
          : draft.requestedToolIds,
    });
  };

  return (
    <div className="flex h-full min-h-0 flex-col bg-background">
      <header className="flex h-[52px] shrink-0 items-center border-b px-2.5 sm:px-4">
        <Button
          type="button"
          variant="ghost"
          size="icon"
          className="mr-1 size-8 md:hidden"
          onClick={onOpenMobileSidebar}
          aria-label={t("agents.header.openNavigation")}
        >
          <Menu className="size-4" aria-hidden="true" />
        </Button>
        <div className="flex min-w-0 flex-1 items-center gap-2">
          <Bot className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
          <h1 className="truncate text-[13px] font-semibold text-foreground">
            {t("agents.header.title")}
          </h1>
          <Badge variant="secondary" className="h-5 rounded px-1.5 text-[9px] font-medium">
            {service.mode === "application-service"
              ? t("agents.header.persisted")
              : t("agents.header.preview")}
          </Badge>
        </div>
        <Button
          ref={createButtonRef}
          type="button"
          size="sm"
          className="h-8 gap-1.5 rounded-md px-2.5 text-[11px] shadow-none"
          onClick={handleCreate}
          disabled={models.length === 0 || navigationLocked || !canCreate}
        >
          <Plus className="size-3.5" aria-hidden="true" />
          {t("agents.header.create")}
        </Button>
      </header>

      <div className="flex min-h-0 flex-1">
        <aside
          aria-label={t("agents.list.ariaLabel")}
          className={cn(
            "flex w-full min-w-0 flex-col border-r bg-muted/30 lg:w-[272px] lg:shrink-0",
            mobileEditorOpen && "hidden lg:flex",
          )}
        >
          <div className="shrink-0 border-b p-3">
            <div className="relative">
              <Search
                className="pointer-events-none absolute left-2.5 top-1/2 size-3.5 -translate-y-1/2 text-muted-foreground"
                aria-hidden="true"
              />
              <Input
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                placeholder={t("agents.list.searchPlaceholder")}
                aria-label={t("agents.list.searchAria")}
                className="h-8 bg-background pl-8 text-[11px] shadow-none"
              />
            </div>
          </div>
          <ScrollArea className="min-h-0 flex-1">
            <div className="space-y-1 p-2">
              {profilesLoading ? (
                <div className="flex h-20 items-center justify-center gap-2 text-[11px] text-muted-foreground">
                  <LoaderCircle className="size-3.5 animate-spin" aria-hidden="true" />
                  {t("agents.list.loading")}
                </div>
              ) : filteredProfiles.length === 0 ? (
                <div className="px-3 py-8 text-center text-[11px] leading-5 text-muted-foreground">
                  {t("agents.list.empty")}
                </div>
              ) : (
                filteredProfiles.map((profile) => (
                  <button
                    key={profile.profileId}
                    type="button"
                    onClick={(event) => handleProfileRequest(profile, event.currentTarget)}
                    disabled={navigationLocked}
                    aria-current={selectedProfileId === profile.profileId ? "true" : undefined}
                    className={cn(
                      "flex min-h-16 w-full items-start gap-2.5 rounded-md px-2.5 py-2 text-left transition-colors hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:pointer-events-none disabled:opacity-60",
                      selectedProfileId === profile.profileId && "bg-accent",
                    )}
                  >
                    <span className="mt-0.5 flex size-7 shrink-0 items-center justify-center rounded-md bg-primary text-primary-foreground">
                      <Bot className="size-3.5" aria-hidden="true" />
                    </span>
                    <span className="min-w-0 flex-1">
                      <span className="flex items-center gap-1.5 text-[12px] font-medium text-foreground">
                        <span className="truncate">{profile.displayName}</span>
                        {!profile.enabled ? (
                          <span className="shrink-0 text-[9px] font-normal text-muted-foreground">
                            {t("agents.list.disabled")}
                          </span>
                        ) : null}
                      </span>
                      <span className="mt-0.5 line-clamp-2 text-[10px] leading-4 text-muted-foreground">
                        {profile.description || t("agents.list.noDescription")}
                      </span>
                    </span>
                    <span className="mt-0.5 shrink-0 text-[9px] text-muted-foreground">
                      {t("agents.list.version", { revision: profile.revision })}
                    </span>
                  </button>
                ))
              )}
            </div>
          </ScrollArea>
          <div className="shrink-0 border-t px-3 py-2 text-[9px] leading-4 text-muted-foreground">
            {service.mode === "application-service"
              ? t("agents.list.serviceFooter")
              : t("agents.list.previewFooter")}
          </div>
        </aside>

        <section
          aria-label={t("agents.editor.ariaLabel")}
          className={cn(
            "min-w-0 flex-1 flex-col bg-background",
            mobileEditorOpen ? "flex" : "hidden lg:flex",
          )}
        >
          {draft ? (
            <form
              onSubmit={handleSave}
              className="flex min-h-0 flex-1 flex-col"
              aria-busy={navigationLocked}
            >
              <div className="flex h-12 shrink-0 items-center gap-2 border-b px-3 sm:px-5">
                <Button
                  type="button"
                  variant="ghost"
                  size="icon"
                  className="size-8 lg:hidden"
                  onClick={handleBackRequest}
                  disabled={navigationLocked}
                  aria-label={t("agents.editor.back")}
                >
                  <ArrowLeft className="size-4" aria-hidden="true" />
                </Button>
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2">
                    <h2
                      ref={editorHeadingRef}
                      tabIndex={-1}
                      className="truncate text-[12px] font-semibold text-foreground outline-none"
                    >
                      {isNewProfile ? t("agents.editor.createTitle") : draft.displayName}
                    </h2>
                    {isDirty ? (
                      <span
                        className="size-1.5 rounded-full bg-amber-400"
                        aria-label={t("agents.editor.unsavedChanges")}
                      />
                    ) : null}
                  </div>
                  <p className="truncate text-[9px] text-muted-foreground">
                    {isNewProfile
                      ? t("agents.editor.unsaved")
                      : t("agents.editor.revision", { revision: editingRevision ?? 1 })}
                  </p>
                </div>
                {selectedProfile && canCreate ? (
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon"
                    className="size-8 text-muted-foreground"
                    onClick={handleDuplicate}
                    disabled={navigationLocked}
                    aria-label={t("agents.editor.duplicate")}
                  >
                    <Copy className="size-3.5" aria-hidden="true" />
                  </Button>
                ) : null}
                {selectedProfile && canDelete ? (
                  <AlertDialog>
                    <AlertDialogTrigger asChild>
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon"
                        className="size-8 text-muted-foreground hover:text-rose-600"
                        disabled={navigationLocked}
                        aria-label={t("agents.editor.delete")}
                      >
                        <Trash2 className="size-3.5" aria-hidden="true" />
                      </Button>
                    </AlertDialogTrigger>
                    <AlertDialogContent>
                      <AlertDialogHeader>
                        <AlertDialogTitle>
                          {t("agents.deleteDialog.title", {
                            name: selectedProfile.displayName,
                          })}
                        </AlertDialogTitle>
                        <AlertDialogDescription>
                          {service.mode === "application-service"
                            ? t("agents.deleteDialog.serviceDescription")
                            : t("agents.deleteDialog.previewDescription")}
                        </AlertDialogDescription>
                      </AlertDialogHeader>
                      <AlertDialogFooter>
                        <AlertDialogCancel>{t("agents.deleteDialog.cancel")}</AlertDialogCancel>
                        <AlertDialogAction
                          className="bg-rose-600 text-white hover:bg-rose-700 dark:bg-rose-700 dark:hover:bg-rose-600"
                          onClick={() => deleteMutation.mutate(selectedProfile)}
                        >
                          {t("agents.deleteDialog.confirm")}
                        </AlertDialogAction>
                      </AlertDialogFooter>
                    </AlertDialogContent>
                  </AlertDialog>
                ) : null}
                <Button
                  type="submit"
                  size="sm"
                  className="h-8 gap-1.5 rounded-md px-2.5 text-[11px] shadow-none"
                  disabled={!isDirty || navigationLocked || editorReadOnly}
                >
                  {saveMutation.isPending ? (
                    <LoaderCircle className="size-3.5 animate-spin" aria-hidden="true" />
                  ) : (
                    <Save className="size-3.5" aria-hidden="true" />
                  )}
                  {t("agents.editor.save")}
                </Button>
              </div>

              <fieldset disabled={navigationLocked || editorReadOnly} className="contents">
              <ScrollArea className="min-h-0 flex-1">
                <Tabs defaultValue={initialEditorTab} className="mx-auto w-full max-w-[820px] px-4 pb-8 pt-4 sm:px-7 sm:pt-6">
                  <TabsList className="grid h-9 w-full grid-cols-4 rounded-md bg-muted p-1">
                    <TabsTrigger value="profile" className="text-[11px]">
                      {t("agents.tabs.profile")}
                    </TabsTrigger>
                    <TabsTrigger value="instructions" className="text-[11px]">
                      {t("agents.tabs.instructions")}
                    </TabsTrigger>
                    <TabsTrigger value="tools" className="text-[11px]">
                      {t("agents.tabs.tools")}
                    </TabsTrigger>
                    <TabsTrigger value="behavior" className="text-[11px]">
                      {t("agents.tabs.behavior")}
                    </TabsTrigger>
                  </TabsList>

                  <div className="mt-5 min-h-5 text-[10px]" aria-live="polite">
                    {errorMessage ? <p className="text-rose-600 dark:text-rose-400">{errorMessage}</p> : null}
                    {!errorMessage && notice ? <p className="text-emerald-600 dark:text-emerald-400">{notice}</p> : null}
                  </div>

                  <TabsContent value="profile" className="mt-1 space-y-7">
                    <section className="space-y-4" aria-labelledby="agent-basic-heading">
                      <div>
                        <h3 id="agent-basic-heading" className="text-[12px] font-semibold text-foreground">
                          {t("agents.profile.basicTitle")}
                        </h3>
                        <p className="mt-1 text-[10px] leading-4 text-muted-foreground">
                          {t("agents.profile.basicDescription")}
                        </p>
                      </div>
                      <div className="grid gap-4 sm:grid-cols-[minmax(0,1fr)_150px]">
                        <div className="space-y-1.5">
                          <Label htmlFor="agent-name" className="text-[11px]">
                            {t("agents.profile.name")}
                          </Label>
                          <Input
                            id="agent-name"
                            value={draft.displayName}
                            maxLength={48}
                            onChange={(event) => updateDraft({ ...draft, displayName: event.target.value })}
                            className="h-9 text-[12px] shadow-none"
                          />
                        </div>
                        <div className="flex h-9 items-center justify-between self-end rounded-md border px-3">
                          <Label htmlFor="agent-enabled" className="text-[11px] font-normal">
                            {t("agents.profile.enabled")}
                          </Label>
                          <Switch
                            id="agent-enabled"
                            checked={draft.enabled}
                            onCheckedChange={(enabled) => updateDraft({ ...draft, enabled })}
                          />
                        </div>
                      </div>
                      <div className="space-y-1.5">
                        <Label htmlFor="agent-description" className="text-[11px]">
                          {t("agents.profile.description")}
                        </Label>
                        <Input
                          id="agent-description"
                          value={draft.description}
                          maxLength={160}
                          onChange={(event) => updateDraft({ ...draft, description: event.target.value })}
                          placeholder={t("agents.profile.descriptionPlaceholder")}
                          className="h-9 text-[12px] shadow-none"
                        />
                      </div>
                    </section>

                    <section className="space-y-4 border-t pt-6" aria-labelledby="agent-model-heading">
                      <div>
                        <h3 id="agent-model-heading" className="text-[12px] font-semibold text-foreground">
                          {t("agents.model.title")}
                        </h3>
                        <p className="mt-1 text-[10px] leading-4 text-muted-foreground">
                          {t("agents.model.description")}
                        </p>
                      </div>
                      <Select value={getModelRouteKey(draft.model)} onValueChange={handleModelChange}>
                        <SelectTrigger
                          aria-label={t("agents.model.ariaLabel")}
                          className="h-9 w-full text-[11px] shadow-none"
                        >
                          <SelectValue placeholder={t("agents.model.placeholder")} />
                        </SelectTrigger>
                        <SelectContent>
                          {models.map((model) => (
                            <SelectItem key={getModelRouteKey(model)} value={getModelRouteKey(model)}>
                              {model.displayName} · {model.providerId}/{model.apiFamily}
                            </SelectItem>
                          ))}
                        </SelectContent>
                      </Select>
                    </section>

                    <section className="space-y-4 border-t pt-6" aria-labelledby="agent-approval-heading">
                      <div>
                        <h3 id="agent-approval-heading" className="text-[12px] font-semibold text-foreground">
                          {t("agents.dangerous.title")}
                        </h3>
                        <p className="mt-1 text-[10px] leading-4 text-muted-foreground">
                          {t("agents.dangerous.description")}
                        </p>
                      </div>
                      <ToggleGroup
                        type="single"
                        value={draft.dangerousActionMode}
                        onValueChange={(value) => {
                          if (value === "ask" || value === "deny") {
                            handleDangerousActionModeChange(value);
                          }
                        }}
                        className="grid grid-cols-2 rounded-md bg-muted p-1"
                      >
                        <ToggleGroupItem value="ask" className="h-8 text-[11px]">
                          {t("agents.dangerous.ask")}
                        </ToggleGroupItem>
                        <ToggleGroupItem value="deny" className="h-8 text-[11px]">
                          {t("agents.dangerous.deny")}
                        </ToggleGroupItem>
                      </ToggleGroup>
                    </section>
                  </TabsContent>

                  <TabsContent value="instructions" className="mt-1 space-y-3">
                    <div>
                      <h3 className="text-[12px] font-semibold text-foreground">
                        {t("agents.instructions.title")}
                      </h3>
                      <p className="mt-1 text-[10px] leading-4 text-muted-foreground">
                        {t("agents.instructions.description")}
                      </p>
                    </div>
                    <Textarea
                      value={draft.instructions}
                      maxLength={12_000}
                      onChange={(event) => updateDraft({ ...draft, instructions: event.target.value })}
                      aria-label={t("agents.instructions.ariaLabel")}
                      className="min-h-[320px] resize-y text-[12px] leading-5 shadow-none"
                    />
                    <p className="text-right text-[9px] text-muted-foreground">
                      {t("agents.instructions.characterCount", {
                        count: draft.instructions.length,
                        max: 12_000,
                      })}
                    </p>
                  </TabsContent>

                  <TabsContent value="tools" className="mt-1 space-y-4">
                    <div>
                      <h3 className="text-[12px] font-semibold text-foreground">
                        {t("agents.tools.title")}
                      </h3>
                      <p className="mt-1 text-[10px] leading-4 text-muted-foreground">
                        {t("agents.tools.description")}
                      </p>
                    </div>
                    <div className="divide-y border-y">
                      {toolPresentations.map((tool) => {
                        const advertised = supportedToolIdSet.has(tool.toolId);
                        const deniedByMode = tool.dangerous && draft.dangerousActionMode === "deny";
                        const selected = draft.requestedToolIds.includes(tool.toolId);
                        const disabled =
                          (!advertised && !selected) || (deniedByMode && !selected);
                        return (
                          <div key={tool.toolId} className="flex min-h-16 items-center gap-3 py-2.5">
                            <Checkbox
                              id={`agent-tool-${tool.toolId}`}
                              checked={selected}
                              disabled={disabled}
                              onCheckedChange={(checked) => handleToolChange(tool.toolId, checked === true)}
                            />
                            <Label htmlFor={`agent-tool-${tool.toolId}`} className={cn("min-w-0 flex-1 cursor-pointer", disabled && "cursor-not-allowed opacity-55")}>
                              <span className="flex items-center gap-2 text-[11px] font-medium text-foreground">
                                {tool.displayName}
                                <span className="rounded bg-muted px-1.5 py-0.5 text-[8px] font-normal text-muted-foreground">
                                  {t(PERMISSION_LABEL_KEYS[tool.permissionClass])}
                                </span>
                              </span>
                              <span className="mt-0.5 block text-[10px] leading-4 text-muted-foreground">
                                {advertised
                                  ? tool.description
                                  : selected
                                    ? t("agents.tools.unavailableSelected")
                                    : t("agents.tools.unavailable")}
                              </span>
                            </Label>
                          </div>
                        );
                      })}
                    </div>
                    <div className="flex items-start gap-2 rounded-md bg-amber-50 px-3 py-2.5 text-[10px] leading-4 text-amber-800 dark:bg-amber-950/70 dark:text-amber-200">
                      <ShieldAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
                      {t("agents.tools.warning")}
                    </div>
                  </TabsContent>

                  <TabsContent value="behavior" className="mt-1 space-y-7">
                    <section className="space-y-4">
                      <div>
                        <h3 className="text-[12px] font-semibold text-foreground">
                          {t("agents.behavior.title")}
                        </h3>
                        <p className="mt-1 text-[10px] leading-4 text-muted-foreground">
                          {t("agents.behavior.description")}
                        </p>
                      </div>
                      <ToggleGroup
                        type="single"
                        value={draft.behavior.responseStyle}
                        onValueChange={(value) => {
                          if (["concise", "balanced", "detailed"].includes(value)) {
                            updateDraft({
                              ...draft,
                              behavior: { ...draft.behavior, responseStyle: value as ResponseStyle },
                            });
                          }
                        }}
                        className="grid grid-cols-3 rounded-md bg-muted p-1"
                      >
                        <ToggleGroupItem value="concise" className="h-8 text-[11px]">
                          {t("agents.behavior.concise")}
                        </ToggleGroupItem>
                        <ToggleGroupItem value="balanced" className="h-8 text-[11px]">
                          {t("agents.behavior.balanced")}
                        </ToggleGroupItem>
                        <ToggleGroupItem value="detailed" className="h-8 text-[11px]">
                          {t("agents.behavior.detailed")}
                        </ToggleGroupItem>
                      </ToggleGroup>
                    </section>

                    <section className="divide-y border-y">
                      <div className="flex min-h-16 items-center gap-4 py-3">
                        <div className="min-w-0 flex-1">
                          <Label htmlFor="agent-plan-first" className="text-[11px] font-medium">
                            {t("agents.behavior.planFirstTitle")}
                          </Label>
                          <p className="mt-0.5 text-[10px] leading-4 text-muted-foreground">
                            {t("agents.behavior.planFirstDescription")}
                          </p>
                        </div>
                        <Switch
                          id="agent-plan-first"
                          checked={draft.behavior.planFirst}
                          onCheckedChange={(planFirst) => updateDraft({ ...draft, behavior: { ...draft.behavior, planFirst } })}
                        />
                      </div>
                      <div className="flex min-h-16 items-center gap-4 py-3">
                        <div className="min-w-0 flex-1">
                          <Label htmlFor="agent-review-changes" className="text-[11px] font-medium">
                            {t("agents.behavior.reviewChangesTitle")}
                          </Label>
                          <p className="mt-0.5 text-[10px] leading-4 text-muted-foreground">
                            {t("agents.behavior.reviewChangesDescription")}
                          </p>
                        </div>
                        <Switch
                          id="agent-review-changes"
                          checked={draft.behavior.reviewChanges}
                          onCheckedChange={(reviewChanges) => updateDraft({ ...draft, behavior: { ...draft.behavior, reviewChanges } })}
                        />
                      </div>
                    </section>
                  </TabsContent>
                </Tabs>
                <div className="[height:env(safe-area-inset-bottom)]" />
              </ScrollArea>
              </fieldset>
            </form>
          ) : (
            <div className="flex min-h-0 flex-1 items-center justify-center px-6 text-center">
              <div>
                <Bot className="mx-auto size-7 text-muted-foreground/50" aria-hidden="true" />
                <p className="mt-3 text-[12px] font-medium text-foreground/80">
                  {t("agents.empty.title")}
                </p>
                <p className="mt-1 text-[10px] text-muted-foreground">
                  {service.mode === "application-service"
                    ? t("agents.empty.serviceDescription")
                    : t("agents.empty.previewDescription")}
                </p>
              </div>
            </div>
          )}
        </section>
      </div>
      <AlertDialog
        open={pendingNavigation !== null}
        onOpenChange={(open) => {
          if (!open) {
            setPendingNavigation(null);
          }
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{t("agents.discardDialog.title")}</AlertDialogTitle>
            <AlertDialogDescription>
              {t("agents.discardDialog.description")}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>{t("agents.discardDialog.continueEditing")}</AlertDialogCancel>
            <AlertDialogAction onClick={confirmDiscard}>
              {t("agents.discardDialog.confirm")}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
