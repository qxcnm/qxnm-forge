import { type FormEvent, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
  CheckCircle2,
  KeyRound,
  Plus,
  RefreshCw,
  Search,
  Server,
  Trash2,
  Upload,
} from "lucide-react";
import { useTranslation } from "react-i18next";

import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import i18n from "@/i18n";
import { cn } from "@/lib/utils";
import type {
  ApplicationServiceClient,
  BackendKind,
  ModelDescriptor,
  ProviderCatalogEntry,
  ProviderConnection,
  ProviderConnectionInput,
} from "@/types/application-service";

interface ProviderSettingsProps {
  readonly backend: BackendKind;
  readonly service: ApplicationServiceClient;
  readonly supportedMethods: readonly string[];
  readonly onModelReady?: (
    backend: BackendKind,
    model: Pick<ModelDescriptor, "providerId" | "modelId" | "apiFamily">,
  ) => void;
}

interface ProviderDraft {
  readonly displayName: string;
  readonly providerId: string;
  readonly baseUrl: string;
  readonly apiFamily: "openai-completions";
  readonly modelIdsText: string;
  readonly logoAssetId: string;
  readonly enabled: boolean;
}

interface ImportedConnection {
  readonly draft: ProviderDraft;
  readonly credential: string;
}

const EMPTY_PROVIDER_DRAFT: ProviderDraft = {
  displayName: "",
  providerId: "",
  baseUrl: "",
  apiFamily: "openai-completions",
  modelIdsText: "",
  logoAssetId: "newapi-gzxsy",
  enabled: true,
};

const OFFICIAL_PROVIDER_TEMPLATE_IDS = new Set(["anthropic", "google", "openai"]);

/**
 * 将脱敏 Provider 投影复制为可编辑表单状态。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function connectionToDraft(connection: ProviderConnection): ProviderDraft {
  return {
    displayName: connection.displayName,
    providerId: connection.providerId,
    baseUrl: connection.baseUrl,
    apiFamily: connection.apiFamily,
    modelIdsText: connection.modelIds.join("\n"),
    logoAssetId: connection.logoAssetId ?? "",
    enabled: connection.enabled,
  };
}

/**
 * 将表单状态转换为不含凭据的 application service 输入。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function draftToInput(draft: ProviderDraft): ProviderConnectionInput {
  return {
    displayName: draft.displayName,
    providerId: draft.providerId,
    baseUrl: draft.baseUrl,
    apiFamily: draft.apiFamily,
    modelIds: draft.modelIdsText
      .split(/[\n,]/)
      .map((modelId) => modelId.trim())
      .filter(Boolean),
    logoAssetId: draft.logoAssetId.trim() || null,
    enabled: draft.enabled,
  };
}

/**
 * 解析 New API 连接 JSON，并把凭据与非敏感配置保持分离。
 *
 * 输入：用户粘贴的未受信任 JSON 文本。
 * 输出：Provider 表单草稿与仅供密码输入状态使用的瞬时凭据。
 * 不变量：调用方不得把 credential 放入 Query、Zustand 或浏览器持久化。
 * 失败：JSON、类型、URL 或 key 字段格式不符合支持的连接对象时拒绝。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function parseConnectionImport(source: string): ImportedConnection {
  const parsed: unknown = JSON.parse(source);
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new Error(i18n.t("provider.importErrors.object"));
  }
  const value = parsed as Record<string, unknown>;
  if (
    value._type !== "newapi_channel_conn" ||
    typeof value.url !== "string" ||
    (value.key !== undefined && typeof value.key !== "string")
  ) {
    throw new Error(i18n.t("provider.importErrors.type"));
  }
  const endpoint = new URL(value.url);
  if (
    endpoint.protocol !== "https:" ||
    endpoint.username !== "" ||
    endpoint.password !== "" ||
    endpoint.search !== "" ||
    endpoint.hash !== ""
  ) {
    throw new Error(i18n.t("provider.importErrors.https"));
  }
  if (endpoint.pathname === "/") {
    endpoint.pathname = "/v1";
  }
  return {
    draft: {
      displayName: "星思研 New API",
      providerId: "newapi-gzxsy",
      baseUrl: endpoint.toString().replace(/\/$/, ""),
      apiFamily: "openai-completions",
      modelIdsText: "",
      logoAssetId: "newapi-gzxsy",
      enabled: true,
    },
    credential: typeof value.key === "string" ? value.key : "",
  };
}

/**
 * 将受控 Logo 资源标识解析为同源静态资源路径。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function getProviderLogoUrl(logoAssetId: string | null): string | null {
  return logoAssetId && /^[a-z0-9][a-z0-9.-]*$/.test(logoAssetId)
    ? `/providers/${logoAssetId}.jpg`
    : null;
}

/**
 * 提供 Provider 连接 CRUD、New API 导入与独立凭据管理界面。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function ProviderSettings({
  backend,
  service,
  supportedMethods,
  onModelReady,
}: ProviderSettingsProps) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const queryKey = ["provider-connections", service.mode, backend] as const;
  const canList = supportedMethods.includes("providerConnections/list");
  const canListCatalog = supportedMethods.includes("providerCatalog/list");
  const canCreate = supportedMethods.includes("providerConnections/create");
  const canUpdate = supportedMethods.includes("providerConnections/update");
  const canDelete = supportedMethods.includes("providerConnections/delete");
  const canDiscover = supportedMethods.includes("providerConnections/discoverModels");
  const canSetCredential = supportedMethods.includes("providerCredentials/set");
  const canRemoveCredential = supportedMethods.includes("providerCredentials/remove");
  const connectionsQuery = useQuery({
    queryKey,
    queryFn: () => service.listProviderConnections(),
    enabled: canList,
  });
  const catalogQuery = useQuery({
    queryKey: ["provider-catalog", service.mode, backend],
    queryFn: () => service.listProviderCatalog(),
    enabled: canListCatalog,
    staleTime: Number.POSITIVE_INFINITY,
  });
  const connections = connectionsQuery.data ?? [];
  const catalog = catalogQuery.data ?? [];
  const [selectedConnectionId, setSelectedConnectionId] = useState<string | null>(null);
  const [editingRevision, setEditingRevision] = useState<number | null>(null);
  const selectedConnection = connections.find(
    (connection) => connection.connectionId === selectedConnectionId,
  );
  const [draft, setDraft] = useState<ProviderDraft>(EMPTY_PROVIDER_DRAFT);
  const [credential, setCredential] = useState("");
  const importSourceRef = useRef<HTMLInputElement>(null);
  const [hasImportSource, setHasImportSource] = useState(false);
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [catalogFilter, setCatalogFilter] = useState("");
  const [failedLogoAssetIds, setFailedLogoAssetIds] = useState<ReadonlySet<string>>(new Set());
  const [deleteConnection, setDeleteConnection] = useState<ProviderConnection | null>(null);
  const draftLogoAssetId = draft.logoAssetId.trim();
  const draftLogoUrl = getProviderLogoUrl(draftLogoAssetId || null);
  const draftLogoAvailable =
    draftLogoUrl !== null && !failedLogoAssetIds.has(draftLogoAssetId);
  const normalizedCatalogFilter = catalogFilter.trim().toLocaleLowerCase();
  const visibleCatalog = normalizedCatalogFilter
    ? catalog.filter((provider) =>
        `${provider.displayName} ${provider.suggestedProviderId}`
          .toLocaleLowerCase()
          .includes(normalizedCatalogFilter),
      )
    : catalog;
  const officialCatalog = visibleCatalog.filter((provider) =>
    OFFICIAL_PROVIDER_TEMPLATE_IDS.has(provider.templateId),
  );
  const compatibleCatalog = visibleCatalog.filter(
    (provider) => !OFFICIAL_PROVIDER_TEMPLATE_IDS.has(provider.templateId),
  );

  /**
   * 在 Provider 变更后先刷新初始化代际，再刷新对应模型快照。
   *
   * 不变量：连接列表可与 initialize 并行，但 models 只能在 initialize 成功后 refetch。
   * 输出：连接、initialize 与 models 活动查询是否全部刷新成功。
   * 失败：任一活动查询失败时返回 false，并且不会在 initialize 失败后继续刷新 models。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const refreshProviderQueries = async (): Promise<boolean> => {
    try {
      const backends: readonly BackendKind[] = ["rust", "dotnet"];
      const modelsQueryKey = ["application-service", backend, "models"] as const;
      await Promise.all(
        backends.flatMap((candidateBackend) => [
          queryClient.invalidateQueries(
            {
              queryKey: ["application-service", candidateBackend, "initialize"],
              exact: true,
              refetchType: "active",
            },
            { throwOnError: true },
          ),
          queryClient.invalidateQueries(
            {
              queryKey: [
                "provider-connections",
                service.mode,
                candidateBackend,
              ],
              exact: true,
              refetchType: "active",
            },
            { throwOnError: true },
          ),
        ]),
      );
      await queryClient.invalidateQueries(
        { queryKey: modelsQueryKey, refetchType: "active" },
        { throwOnError: true },
      );
      return true;
    } catch {
      return false;
    }
  };

  /**
   * 选择一条 Provider 连接，并确保密码输入不继承上一条连接内容。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const selectConnection = (connection: ProviderConnection) => {
    if (saving) {
      return;
    }
    setSelectedConnectionId(connection.connectionId);
    setEditingRevision(connection.revision);
    setDraft(connectionToDraft(connection));
    setCredential("");
    if (importSourceRef.current) {
      importSourceRef.current.value = "";
    }
    setHasImportSource(false);
    setNotice(null);
    setErrorMessage(null);
  };

  /**
   * 打开空白 Provider 编辑器。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const beginCreate = () => {
    if (saving) {
      return;
    }
    setSelectedConnectionId(null);
    setEditingRevision(null);
    setDraft(EMPTY_PROVIDER_DRAFT);
    setCredential("");
    if (importSourceRef.current) {
      importSourceRef.current.value = "";
    }
    setHasImportSource(false);
    setNotice(null);
    setErrorMessage(null);
  };

  /**
   * 从后端冻结目录填充 OpenAI-compatible 连接草稿，或打开已经保存的同 ID 连接。
   *
   * 输入：application service 返回的非敏感模板；输出：不含 credential 和猜测模型的表单草稿。
   * 不变量：模板只选择现有通用 adapter，不把自定义 endpoint 冒充 canonical Provider。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const selectPreset = (preset: ProviderCatalogEntry) => {
    const existingConnection = connections.find(
      (connection) => connection.providerId === preset.suggestedProviderId,
    );
    if (existingConnection) {
      selectConnection(existingConnection);
      return;
    }
    if (saving) {
      return;
    }
    setSelectedConnectionId(null);
    setEditingRevision(null);
    setDraft({
      displayName: preset.displayName,
      providerId: preset.suggestedProviderId,
      baseUrl: preset.defaultBaseUrl,
      apiFamily: preset.apiFamily,
      modelIdsText: "",
      logoAssetId: preset.logoAssetId ?? "",
      enabled: true,
    });
    setCredential("");
    setNotice(null);
    setErrorMessage(null);
  };

  /**
   * 从未受控密码输入同步读取 New API JSON，并填充草稿与瞬时密码输入。
   *
   * 不变量：完整导入原文不进入 React state，且成功或失败后都会立即从 DOM 清空。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleImport = () => {
    const importInput = importSourceRef.current;
    const importSource = importInput?.value ?? "";
    setNotice(null);
    try {
      const imported = parseConnectionImport(importSource);
      setSelectedConnectionId(null);
      setEditingRevision(null);
      setDraft(imported.draft);
      setCredential(canSetCredential ? imported.credential : "");
      setNotice(
        canSetCredential
          ? t("provider.imported")
          : t("provider.importedWithoutCredential"),
      );
      setErrorMessage(
        imported.credential && !canSetCredential
          ? t("provider.keyDiscarded")
          : null,
      );
    } catch {
      setErrorMessage(t("provider.importErrors.invalid"));
    } finally {
      if (importInput) {
        importInput.value = "";
      }
      setHasImportSource(false);
    }
  };

  /**
   * 保存 Provider 非敏感配置，并直接在最终边界提交瞬时凭据。
   *
   * 不变量：credential 在发起服务调用前从组件输入状态清空，且不进入 mutation cache。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleSave = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const connectionId = selectedConnectionId;
    const expectedRevision = editingRevision;
    if (
      saving ||
      (connectionId ? !canUpdate : !canCreate) ||
      (connectionId !== null && expectedRevision === null) ||
      (credential.trim().length > 0 && !canSetCredential)
    ) {
      return;
    }
    const pendingCredential = credential;
    setCredential("");
    setSaving(true);
    setNotice(null);
    setErrorMessage(null);
    try {
      let result;
      try {
        if (connectionId !== null) {
          if (expectedRevision === null) {
            setErrorMessage(t("provider.revisionUnavailable"));
            return;
          }
          result = await service.updateProviderConnection(
              connectionId,
              expectedRevision,
              draftToInput(draft),
            );
        } else {
          result = await service.createProviderConnection(draftToInput(draft));
        }
      } catch {
        const refreshed = await refreshProviderQueries();
        setErrorMessage(
          t(refreshed ? "provider.saveFailed" : "provider.reconciliationFailed"),
        );
        return;
      }

      if (pendingCredential.trim()) {
        try {
          await service.setProviderCredential(result.connection.providerId, pendingCredential);
        } catch {
          const refreshed = await refreshProviderQueries();
          setErrorMessage(
            t(refreshed ? "provider.keySaveFailed" : "provider.reconciliationFailed"),
          );
          return;
        }
      }
      let savedConnection = result.connection;
      let discoveredCount: number | null = null;
      let discoveryFailed = false;
      if (
        canDiscover &&
        (pendingCredential.trim().length > 0 || result.connection.credentialConfigured)
      ) {
        try {
          const discovery = await service.discoverProviderModels(
            result.connection.connectionId,
            result.connection.revision,
          );
          savedConnection = discovery.connection;
          discoveredCount = discovery.discoveredCount;
        } catch {
          discoveryFailed = true;
        }
      }
      setSelectedConnectionId(savedConnection.connectionId);
      setEditingRevision(savedConnection.revision);
      setDraft(connectionToDraft(savedConnection));
      if (!(await refreshProviderQueries())) {
        setErrorMessage(t("provider.refreshFailed"));
        return;
      }
      if (discoveredCount !== null && savedConnection.modelIds[0]) {
        onModelReady?.(backend, {
          providerId: savedConnection.providerId,
          modelId: savedConnection.modelIds[0],
          apiFamily: savedConnection.apiFamily,
        });
      }
      setNotice(
        discoveredCount !== null
          ? t("provider.modelsDiscovered", { count: discoveredCount })
          : result.restartRequired && service.mode === "application-service"
          ? t("provider.savedAndReconnected")
          : t("provider.savedPreview"),
      );
      if (discoveryFailed) {
        setErrorMessage(t("provider.discoveryFailed"));
      }
    } finally {
      setSaving(false);
    }
  };

  /**
   * 移除选中 Provider 的 CredentialStore 凭据并刷新脱敏状态。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleRemoveCredential = async () => {
    if (!selectedConnection || saving || !canRemoveCredential) {
      return;
    }
    setCredential("");
    setSaving(true);
    setNotice(null);
    setErrorMessage(null);
    try {
      const status = await service.removeProviderCredential(selectedConnection.providerId);
      if (!(await refreshProviderQueries())) {
        setErrorMessage(t("provider.refreshFailed"));
        return;
      }
      setNotice(
        status.restartRequired && service.mode === "application-service"
          ? t("provider.credentialRemovedAndReconnected")
          : t("provider.credentialRemovedPreview"),
      );
    } catch {
      const refreshed = await refreshProviderQueries();
      setErrorMessage(
        t(
          refreshed
            ? "provider.credentialRemoveFailed"
            : "provider.reconciliationFailed",
        ),
      );
    } finally {
      setSaving(false);
    }
  };

  /**
   * 永久删除用户已经确认的 Provider 连接。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const confirmDelete = async () => {
    const connection = deleteConnection;
    setDeleteConnection(null);
    if (!connection || !canDelete) {
      return;
    }
    setSaving(true);
    try {
      await service.deleteProviderConnection(connection.connectionId, connection.revision);
      setSelectedConnectionId(null);
      setEditingRevision(null);
      setDraft(EMPTY_PROVIDER_DRAFT);
      setCredential("");
      if (!(await refreshProviderQueries())) {
        setErrorMessage(t("provider.refreshFailed"));
        return;
      }
      setNotice(
        service.mode === "application-service"
          ? t("provider.deletedAndReconnected")
          : t("provider.deletedPreview"),
      );
      setErrorMessage(null);
    } catch {
      const refreshed = await refreshProviderQueries();
      setErrorMessage(
        t(refreshed ? "provider.deleteFailed" : "provider.reconciliationFailed"),
      );
    } finally {
      setSaving(false);
    }
  };

  if (!canList) {
    return (
      <div className="border-y py-10 text-center">
        <Server className="mx-auto size-5 text-muted-foreground/50" aria-hidden="true" />
        <h2 className="mt-3 text-[13px] font-medium text-foreground/80">{t("provider.unsupportedTitle")}</h2>
        <p className="mt-1 text-[11px] text-muted-foreground">{t("provider.unsupportedDescription")}</p>
      </div>
    );
  }

  if (connectionsQuery.isError) {
    return (
      <div className="border-y py-10 text-center" role="alert">
        <Server className="mx-auto size-5 text-red-300" aria-hidden="true" />
        <h2 className="mt-3 text-[13px] font-medium text-foreground/80">{t("provider.loadFailed")}</h2>
        <Button
          type="button"
          variant="outline"
          size="sm"
          className="mt-4 h-8 gap-1.5 text-[10px] shadow-none"
          onClick={() => void connectionsQuery.refetch()}
        >
          <RefreshCw className="size-3" aria-hidden="true" />
          {t("common.retry")}
        </Button>
      </div>
    );
  }

  return (
    <div className="grid min-h-[540px] grid-cols-1 border-y lg:grid-cols-[220px_minmax(0,1fr)]">
      <aside className="border-b bg-muted/20 py-3 lg:border-b-0 lg:border-r">
        <div className="flex items-center justify-between px-3 pb-2">
          <span className="text-[11px] font-semibold text-foreground/80">{t("provider.title")}</span>
          <Button
            type="button"
            variant="ghost"
            size="icon"
            className="size-7"
            onClick={beginCreate}
            disabled={!canCreate || saving}
            aria-label={t("provider.create")}
          >
            <Plus className="size-3.5" aria-hidden="true" />
          </Button>
        </div>
        {connectionsQuery.isLoading ? (
          <p className="px-3 py-4 text-[11px] text-muted-foreground">{t("provider.loading")}</p>
        ) : (
          <div className="space-y-0.5 px-2">
            <p className="px-2 pb-1 pt-2 text-[9px] font-medium uppercase text-muted-foreground">
              {t("provider.compatibleCatalog")}
            </p>
            {canListCatalog ? (
              <div className="relative mx-1 mb-2">
                <Search
                  className="pointer-events-none absolute left-2 top-2 size-3 text-muted-foreground"
                  aria-hidden="true"
                />
                <Input
                  value={catalogFilter}
                  onChange={(event) => setCatalogFilter(event.target.value)}
                  placeholder={t("provider.catalogSearch")}
                  aria-label={t("provider.catalogSearch")}
                  className="h-7 pl-7 text-[9px]"
                />
              </div>
            ) : null}
            {catalogQuery.isLoading ? (
              <p className="px-2 py-3 text-[10px] text-muted-foreground">
                {t("provider.catalogLoading")}
              </p>
            ) : catalogQuery.isError ? (
              <div className="px-2 py-2" role="alert">
                <p className="text-[10px] text-red-600">{t("provider.catalogLoadFailed")}</p>
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  className="mt-1 h-7 gap-1 px-1.5 text-[9px]"
                  onClick={() => void catalogQuery.refetch()}
                >
                  <RefreshCw className="size-3" aria-hidden="true" />
                  {t("common.retry")}
                </Button>
              </div>
            ) : !canListCatalog ? (
              <p className="px-2 py-3 text-[10px] leading-4 text-muted-foreground">
                {t("provider.catalogUnavailable")}
              </p>
            ) : visibleCatalog.length === 0 ? (
              <p className="px-2 py-3 text-[10px] text-muted-foreground">
                {t("provider.catalogEmpty")}
              </p>
            ) : (
              <div className="mb-3 space-y-2">
                {officialCatalog.length > 0 ? (
                  <div>
                    <p className="px-2 pb-1 text-[9px] font-medium text-muted-foreground">
                      {t("provider.officialProviders")}
                    </p>
                    <div className="space-y-0.5">
                      {officialCatalog.map((preset) => (
                        <button
                          key={preset.templateId}
                          type="button"
                          onClick={() => selectPreset(preset)}
                          disabled={saving || !canCreate}
                          className="flex h-9 w-full min-w-0 items-center gap-2 rounded-md px-2 text-left text-[10px] text-foreground/80 hover:bg-accent disabled:pointer-events-none disabled:opacity-50"
                          aria-label={t("provider.usePreset", { name: preset.displayName })}
                        >
                          <span className="flex size-5 shrink-0 items-center justify-center rounded border bg-background text-[8px] font-semibold text-foreground/70">
                            {preset.displayName.slice(0, 2).toUpperCase()}
                          </span>
                          <span className="min-w-0 flex-1 truncate">{preset.displayName}</span>
                          <Badge variant="outline" className="h-4 shrink-0 px-1 text-[8px] font-normal">
                            {t("provider.officialBadge")}
                          </Badge>
                        </button>
                      ))}
                    </div>
                  </div>
                ) : null}
                {compatibleCatalog.length > 0 ? (
                  <div>
                    <p className="px-2 pb-1 text-[9px] font-medium text-muted-foreground">
                      {t("provider.compatibleProviders")}
                    </p>
                    <div className="grid grid-cols-2 gap-1">
                      {compatibleCatalog.map((preset) => (
                        <button
                          key={preset.templateId}
                          type="button"
                          onClick={() => selectPreset(preset)}
                          disabled={saving || !canCreate}
                          className="flex h-9 min-w-0 items-center gap-2 rounded-md px-2 text-left text-[10px] text-foreground/75 hover:bg-accent disabled:pointer-events-none disabled:opacity-50"
                          aria-label={t("provider.usePreset", { name: preset.displayName })}
                        >
                          {preset.logoAssetId &&
                          !failedLogoAssetIds.has(preset.logoAssetId) ? (
                            <img
                              src={getProviderLogoUrl(preset.logoAssetId) ?? undefined}
                              alt=""
                              className="size-5 shrink-0 rounded border bg-white object-contain"
                              onError={() => {
                                setFailedLogoAssetIds((currentIds) =>
                                  new Set(currentIds).add(preset.logoAssetId ?? ""),
                                );
                              }}
                            />
                          ) : (
                            <span className="flex size-5 shrink-0 items-center justify-center rounded border bg-background text-[8px] font-semibold text-muted-foreground">
                              {preset.displayName.slice(0, 2).toUpperCase()}
                            </span>
                          )}
                          <span className="truncate">{preset.displayName}</span>
                        </button>
                      ))}
                    </div>
                  </div>
                ) : null}
              </div>
            )}
            <p className="px-2 pb-3 text-[9px] leading-4 text-muted-foreground">
              {t("provider.catalogDisclaimer")}
            </p>
            {connections.length > 0 ? (
              <p className="px-2 pb-1 text-[9px] font-medium uppercase text-muted-foreground">
                {t("provider.savedConnections")}
              </p>
            ) : null}
            {connections.map((connection) => {
              const logoAssetId = connection.logoAssetId;
              const logoUrl = getProviderLogoUrl(logoAssetId);
              const logoAvailable =
                logoUrl !== null &&
                logoAssetId !== null &&
                !failedLogoAssetIds.has(logoAssetId);
              return (
                <button
                  key={connection.connectionId}
                  type="button"
                  onClick={() => selectConnection(connection)}
                  disabled={saving}
                  className={cn(
                    "flex h-12 w-full min-w-0 items-center gap-2 rounded-md px-2 text-left hover:bg-accent",
                    selectedConnectionId === connection.connectionId && "bg-accent",
                  )}
                  aria-label={`${t("provider.edit", { name: connection.displayName })} · ${
                    connection.credentialConfigured
                      ? t("provider.credentialConfigured")
                      : t("provider.credentialNotConfigured")
                  }`}
                >
                  {logoAvailable ? (
                    <img
                      src={logoUrl}
                      alt=""
                      className="size-8 rounded border bg-white object-contain"
                      onError={() => {
                        setFailedLogoAssetIds((currentIds) =>
                          new Set(currentIds).add(logoAssetId),
                        );
                      }}
                    />
                  ) : (
                    <span className="flex size-8 items-center justify-center rounded bg-muted text-[9px] font-semibold text-muted-foreground">
                      {connection.displayName.slice(0, 2).toUpperCase()}
                    </span>
                  )}
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-[11px] font-medium text-foreground/80">
                      {connection.displayName}
                    </span>
                    <span className="block truncate text-[9px] text-muted-foreground">{connection.providerId}</span>
                  </span>
                  <span
                    className={cn(
                      "size-1.5 rounded-full",
                      connection.credentialConfigured ? "bg-emerald-500" : "bg-muted-foreground/40",
                    )}
                    aria-hidden="true"
                  />
                </button>
              );
            })}
          </div>
        )}
      </aside>

      <form
        className="min-w-0 px-4 py-4 sm:px-6"
        onSubmit={(event) => void handleSave(event)}
        aria-busy={saving}
      >
        {service.mode === "faux-preview" ? (
          <div className="mb-4 rounded-md border border-amber-300 bg-amber-50 px-3 py-2 text-[10px] leading-4 text-amber-900 dark:border-amber-900 dark:bg-amber-950/30 dark:text-amber-100">
            {t("provider.previewBoundary")}
          </div>
        ) : null}
        <div className="mb-4 flex items-center justify-between gap-2">
          <div className="flex min-w-0 items-center gap-3">
            {draftLogoAvailable ? (
              <img
                src={draftLogoUrl}
                alt={t("provider.logoPreview")}
                className="size-10 shrink-0 rounded border bg-white object-contain"
                onError={() => {
                  setFailedLogoAssetIds((currentIds) =>
                    new Set(currentIds).add(draftLogoAssetId),
                  );
                }}
              />
            ) : null}
            <div className="min-w-0">
              <h2 className="truncate text-[13px] font-semibold text-foreground">
                {selectedConnection ? selectedConnection.displayName : t("provider.create")}
              </h2>
              <p className="mt-0.5 truncate text-[10px] text-muted-foreground">{t("provider.connectionDescription")}</p>
            </div>
          </div>
          {selectedConnection && canDelete ? (
            <Button
              type="button"
              variant="ghost"
              size="icon"
              className="size-7 text-muted-foreground hover:text-red-600"
              onClick={() => setDeleteConnection(selectedConnection)}
              disabled={saving}
              aria-label={t("provider.deleteProvider")}
            >
              <Trash2 className="size-3.5" aria-hidden="true" />
            </Button>
          ) : null}
        </div>

        <fieldset disabled={saving} className="contents">
        {!selectedConnection ? (
          <div className="mb-5 rounded-md border border-dashed bg-muted/30 p-3">
            <Label htmlFor="provider-import" className="text-[10px] text-foreground/70">
              {t("provider.importLabel")}
            </Label>
            <div className="mt-2 flex gap-2">
              <Input
                id="provider-import"
                ref={importSourceRef}
                type="password"
                onChange={(event) =>
                  setHasImportSource(event.currentTarget.value.trim().length > 0)
                }
                placeholder={'{"_type":"newapi_channel_conn","key":"sk-...","url":"https://..."}'}
                className="h-8 min-w-0 text-[10px]"
                autoComplete="new-password"
                autoCapitalize="none"
                autoCorrect="off"
                spellCheck={false}
              />
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="h-8 gap-1.5 px-2 text-[10px] shadow-none"
                onClick={handleImport}
                disabled={!hasImportSource || !canCreate}
              >
                <Upload className="size-3" aria-hidden="true" />
                {t("common.import")}
              </Button>
            </div>
          </div>
        ) : null}

        <div className="grid grid-cols-1 gap-x-4 gap-y-3 sm:grid-cols-2">
          <div className="space-y-1.5">
            <Label htmlFor="provider-display-name" className="text-[10px]">{t("provider.fields.name")}</Label>
            <Input
              id="provider-display-name"
              value={draft.displayName}
              onChange={(event) => setDraft({ ...draft, displayName: event.target.value })}
              className="h-8 text-[11px]"
              maxLength={64}
              required
            />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="provider-id" className="text-[10px]">{t("provider.fields.providerId")}</Label>
            <Input
              id="provider-id"
              value={draft.providerId}
              onChange={(event) => setDraft({ ...draft, providerId: event.target.value })}
              className="h-8 font-mono text-[10px]"
              pattern="[a-z0-9][a-z0-9.\-]*"
              required
            />
          </div>
          <div className="space-y-1.5 sm:col-span-2">
            <Label htmlFor="provider-base-url" className="text-[10px]">{t("provider.fields.baseUrl")}</Label>
            <Input
              id="provider-base-url"
              type="url"
              value={draft.baseUrl}
              onChange={(event) => setDraft({ ...draft, baseUrl: event.target.value })}
              className="h-8 font-mono text-[10px]"
              required
            />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="provider-api-family" className="text-[10px]">{t("provider.fields.apiFamily")}</Label>
            <Input
              id="provider-api-family"
              value={draft.apiFamily}
              className="h-8 font-mono text-[10px]"
              readOnly
            />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="provider-logo" className="text-[10px]">{t("provider.fields.logoAssetId")}</Label>
            <Input
              id="provider-logo"
              value={draft.logoAssetId}
              onChange={(event) => setDraft({ ...draft, logoAssetId: event.target.value })}
              className="h-8 font-mono text-[10px]"
              placeholder="newapi-gzxsy"
              pattern="[a-z0-9][a-z0-9.\-]*"
              maxLength={128}
            />
          </div>
          <div className="space-y-1.5 sm:col-span-2">
            <Label htmlFor="provider-models" className="text-[10px]">{t("provider.fields.modelIds")}</Label>
            <Textarea
              id="provider-models"
              value={draft.modelIdsText}
              onChange={(event) => setDraft({ ...draft, modelIdsText: event.target.value })}
              className="min-h-16 resize-y font-mono text-[10px]"
              placeholder={t("provider.fields.modelIdsPlaceholder")}
              required={!canDiscover}
            />
            <p className="text-[9px] leading-4 text-muted-foreground">
              {canDiscover
                ? t("provider.fields.modelIdsDiscoveryHint")
                : t("provider.fields.modelIdsManualHint")}
            </p>
          </div>
          <div className="space-y-1.5 sm:col-span-2">
            <div className="flex items-center justify-between gap-2">
              <Label htmlFor="provider-key" className="text-[10px]">{t("provider.fields.apiKey")}</Label>
              {selectedConnection?.credentialConfigured ? (
                <Badge variant="secondary" className="gap-1 bg-emerald-50 text-[9px] text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300">
                  <CheckCircle2 className="size-2.5" aria-hidden="true" />
                  {t("common.configured")}
                </Badge>
              ) : null}
            </div>
            <div className="flex gap-2">
              <div className="relative min-w-0 flex-1">
                <KeyRound className="pointer-events-none absolute left-2.5 top-2 size-3 text-muted-foreground" aria-hidden="true" />
                <Input
                  id="provider-key"
                  type="password"
                  value={credential}
                  onChange={(event) => setCredential(event.target.value)}
                  className="h-8 pl-8 text-[11px]"
                  placeholder={selectedConnection?.credentialConfigured ? t("provider.fields.replacementKey") : t("provider.fields.apiKeyPlaceholder")}
                  autoComplete="new-password"
                  disabled={!canSetCredential}
                />
              </div>
              {selectedConnection?.credentialConfigured && canRemoveCredential ? (
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  className="h-8 px-2 text-[10px] shadow-none"
                  onClick={() => void handleRemoveCredential()}
                  disabled={saving}
                >
                  {t("common.remove")}
                </Button>
              ) : null}
            </div>
          </div>
        </div>

        <div className="mt-4 flex items-center gap-2 border-t pt-4">
          <Switch
            id="provider-enabled"
            checked={draft.enabled}
            onCheckedChange={(enabled) => setDraft({ ...draft, enabled })}
          />
          <Label htmlFor="provider-enabled" className="text-[11px] font-normal">{t("provider.fields.enable")}</Label>
          <div className="flex-1" />
          <Button
            type="submit"
            size="sm"
            className="h-8 px-3 text-[10px]"
            disabled={saving || (selectedConnectionId ? !canUpdate : !canCreate)}
          >
            {saving
              ? t("provider.savingAndDiscovering")
              : canDiscover &&
                  (credential.trim().length > 0 || selectedConnection?.credentialConfigured)
                ? t("provider.saveAndDiscover")
                : t("common.save")}
          </Button>
        </div>
        </fieldset>
        {notice ? <p className="mt-3 text-[10px] text-emerald-700" role="status">{notice}</p> : null}
        {errorMessage ? <p className="mt-3 text-[10px] text-red-600" role="alert">{errorMessage}</p> : null}
      </form>

      <AlertDialog open={deleteConnection !== null} onOpenChange={(open) => !open && setDeleteConnection(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{t("provider.deleteTitle", { name: deleteConnection?.displayName })}</AlertDialogTitle>
            <AlertDialogDescription>
              {t("provider.deleteDescription")}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>{t("common.cancel")}</AlertDialogCancel>
            <AlertDialogAction
              className="bg-red-600 hover:bg-red-700"
              onClick={() => void confirmDelete()}
            >
              {t("common.delete")}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
