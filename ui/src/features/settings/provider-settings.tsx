import { type FormEvent, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
  CheckCircle2,
  KeyRound,
  Plus,
  RefreshCw,
  Server,
  Trash2,
  Upload,
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
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import { cn } from "@/lib/utils";
import type {
  ApplicationServiceClient,
  BackendKind,
  ProviderConnection,
  ProviderConnectionInput,
} from "@/types/application-service";

interface ProviderSettingsProps {
  readonly backend: BackendKind;
  readonly service: ApplicationServiceClient;
  readonly supportedMethods: readonly string[];
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
    throw new Error("连接 JSON 必须是对象");
  }
  const value = parsed as Record<string, unknown>;
  if (
    value._type !== "newapi_channel_conn" ||
    typeof value.url !== "string" ||
    (value.key !== undefined && typeof value.key !== "string")
  ) {
    throw new Error("仅支持 newapi_channel_conn 连接 JSON");
  }
  const endpoint = new URL(value.url);
  if (
    endpoint.protocol !== "https:" ||
    endpoint.username !== "" ||
    endpoint.password !== "" ||
    endpoint.search !== "" ||
    endpoint.hash !== ""
  ) {
    throw new Error("Provider URL 必须使用 HTTPS");
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
      modelIdsText: "gpt-5",
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
}: ProviderSettingsProps) {
  const queryClient = useQueryClient();
  const queryKey = ["provider-connections", service.mode, backend] as const;
  const canList = supportedMethods.includes("providerConnections/list");
  const canCreate = supportedMethods.includes("providerConnections/create");
  const canUpdate = supportedMethods.includes("providerConnections/update");
  const canDelete = supportedMethods.includes("providerConnections/delete");
  const canSetCredential = supportedMethods.includes("providerCredentials/set");
  const canRemoveCredential = supportedMethods.includes("providerCredentials/remove");
  const connectionsQuery = useQuery({
    queryKey,
    queryFn: () => service.listProviderConnections(),
    enabled: canList,
  });
  const connections = connectionsQuery.data ?? [];
  const [selectedConnectionId, setSelectedConnectionId] = useState<string | null>(null);
  const [editingRevision, setEditingRevision] = useState<number | null>(null);
  const selectedConnection = connections.find(
    (connection) => connection.connectionId === selectedConnectionId,
  );
  const [draft, setDraft] = useState<ProviderDraft>(EMPTY_PROVIDER_DRAFT);
  const [credential, setCredential] = useState("");
  const [importSource, setImportSource] = useState("");
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [failedLogoAssetIds, setFailedLogoAssetIds] = useState<ReadonlySet<string>>(new Set());
  const [deleteConnection, setDeleteConnection] = useState<ProviderConnection | null>(null);
  const draftLogoAssetId = draft.logoAssetId.trim();
  const draftLogoUrl = getProviderLogoUrl(draftLogoAssetId || null);
  const draftLogoAvailable =
    draftLogoUrl !== null && !failedLogoAssetIds.has(draftLogoAssetId);

  /**
   * 在 Provider 变更后刷新连接、初始化能力与模型快照。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const refreshProviderQueries = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey }),
      queryClient.invalidateQueries({ queryKey: ["application-service", backend] }),
    ]);
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
    setImportSource("");
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
    setImportSource("");
    setNotice(null);
    setErrorMessage(null);
  };

  /**
   * 从用户粘贴的 New API JSON 填充草稿与瞬时密码输入。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleImport = () => {
    try {
      const imported = parseConnectionImport(importSource);
      setSelectedConnectionId(null);
      setEditingRevision(null);
      setDraft(imported.draft);
      setCredential(canSetCredential ? imported.credential : "");
      setImportSource("");
      setNotice(
        canSetCredential
          ? "连接信息已导入，请确认模型和凭据后保存"
          : "连接信息已导入；当前服务不支持写入凭据",
      );
      setErrorMessage(
        imported.credential && !canSetCredential
          ? "导入内容中的 Key 未被保留，请切换到支持凭据管理的服务"
          : null,
      );
    } catch {
      setErrorMessage("无法解析连接 JSON，请检查 _type、url 和 key 字段");
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
            setErrorMessage("Provider revision 不可用，请重新选择连接");
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
        setErrorMessage("连接配置保存失败，请检查字段或重新加载最新 revision");
        return;
      }

      setSelectedConnectionId(result.connection.connectionId);
      setEditingRevision(result.connection.revision);
      setDraft(connectionToDraft(result.connection));
      if (pendingCredential.trim()) {
        try {
          await service.setProviderCredential(result.connection.providerId, pendingCredential);
        } catch {
          await refreshProviderQueries().catch(() => undefined);
          setErrorMessage("连接配置已保存，但 API Key 写入失败；请重新输入 Key 后再次保存");
          return;
        }
      }
      try {
        await refreshProviderQueries();
      } catch {
        setErrorMessage("连接已保存，但最新 Provider 状态刷新失败；请重新打开设置");
        return;
      }
      setNotice(
        result.restartRequired && service.mode === "application-service"
          ? "已保存并重新连接"
          : "已保存，预览已更新",
      );
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
      await refreshProviderQueries();
      setNotice(
        status.restartRequired && service.mode === "application-service"
          ? "凭据已移除并重新连接"
          : "凭据已移除，预览已更新",
      );
    } catch {
      setErrorMessage("无法移除凭据");
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
      await refreshProviderQueries();
      setNotice(
        service.mode === "application-service"
          ? "Provider 连接已删除并重新连接"
          : "Provider 连接已从预览删除",
      );
      setErrorMessage(null);
    } catch {
      setErrorMessage("删除失败，请重新加载后再试");
    } finally {
      setSaving(false);
    }
  };

  if (!canList) {
    return (
      <div className="border-y border-stone-100 py-10 text-center">
        <Server className="mx-auto size-5 text-stone-300" aria-hidden="true" />
        <h2 className="mt-3 text-[13px] font-medium text-stone-700">当前服务不支持 Provider 管理</h2>
        <p className="mt-1 text-[11px] text-stone-400">请升级或切换 application service 实现。</p>
      </div>
    );
  }

  if (connectionsQuery.isError) {
    return (
      <div className="border-y border-stone-100 py-10 text-center" role="alert">
        <Server className="mx-auto size-5 text-red-300" aria-hidden="true" />
        <h2 className="mt-3 text-[13px] font-medium text-stone-700">无法读取 Provider 配置</h2>
        <Button
          type="button"
          variant="outline"
          size="sm"
          className="mt-4 h-8 gap-1.5 text-[10px] shadow-none"
          onClick={() => void connectionsQuery.refetch()}
        >
          <RefreshCw className="size-3" aria-hidden="true" />
          重试
        </Button>
      </div>
    );
  }

  return (
    <div className="grid min-h-[540px] grid-cols-1 border-y border-stone-100 lg:grid-cols-[220px_minmax(0,1fr)]">
      <aside className="border-b border-stone-100 py-3 lg:border-b-0 lg:border-r">
        <div className="flex items-center justify-between px-3 pb-2">
          <span className="text-[11px] font-semibold text-stone-700">提供商</span>
          <Button
            type="button"
            variant="ghost"
            size="icon"
            className="size-7"
            onClick={beginCreate}
            disabled={!canCreate || saving}
            aria-label="新建提供商"
          >
            <Plus className="size-3.5" aria-hidden="true" />
          </Button>
        </div>
        {connectionsQuery.isLoading ? (
          <p className="px-3 py-4 text-[11px] text-stone-400">正在读取提供商...</p>
        ) : (
          <div className="space-y-0.5 px-2">
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
                    "flex h-12 w-full min-w-0 items-center gap-2 rounded-md px-2 text-left hover:bg-stone-50",
                    selectedConnectionId === connection.connectionId && "bg-stone-100",
                  )}
                  aria-label={`编辑提供商 ${connection.displayName}`}
                >
                  {logoAvailable ? (
                    <img
                      src={logoUrl}
                      alt=""
                      className="size-8 rounded border border-stone-100 bg-white object-contain"
                      onError={() => {
                        setFailedLogoAssetIds((currentIds) =>
                          new Set(currentIds).add(logoAssetId),
                        );
                      }}
                    />
                  ) : (
                    <span className="flex size-8 items-center justify-center rounded bg-stone-100 text-[9px] font-semibold text-stone-500">
                      {connection.displayName.slice(0, 2).toUpperCase()}
                    </span>
                  )}
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-[11px] font-medium text-stone-700">
                      {connection.displayName}
                    </span>
                    <span className="block truncate text-[9px] text-stone-400">{connection.providerId}</span>
                  </span>
                  <span
                    className={cn(
                      "size-1.5 rounded-full",
                      connection.credentialConfigured ? "bg-emerald-500" : "bg-stone-300",
                    )}
                    aria-label={connection.credentialConfigured ? "凭据已配置" : "凭据未配置"}
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
        <div className="mb-4 flex items-center justify-between gap-2">
          <div className="flex min-w-0 items-center gap-3">
            {draftLogoAvailable ? (
              <img
                src={draftLogoUrl}
                alt="提供商 Logo 预览"
                className="size-10 shrink-0 rounded border border-stone-100 bg-white object-contain"
                onError={() => {
                  setFailedLogoAssetIds((currentIds) =>
                    new Set(currentIds).add(draftLogoAssetId),
                  );
                }}
              />
            ) : null}
            <div className="min-w-0">
              <h2 className="truncate text-[13px] font-semibold text-stone-900">
                {selectedConnection ? selectedConnection.displayName : "新建提供商"}
              </h2>
              <p className="mt-0.5 truncate text-[10px] text-stone-400">OpenAI-compatible Chat 连接</p>
            </div>
          </div>
          {selectedConnection && canDelete ? (
            <Button
              type="button"
              variant="ghost"
              size="icon"
              className="size-7 text-stone-400 hover:text-red-600"
              onClick={() => setDeleteConnection(selectedConnection)}
              disabled={saving}
              aria-label="删除提供商"
            >
              <Trash2 className="size-3.5" aria-hidden="true" />
            </Button>
          ) : null}
        </div>

        <fieldset disabled={saving} className="contents">
        {!selectedConnection ? (
          <div className="mb-5 rounded-md border border-dashed border-stone-200 bg-stone-50/60 p-3">
            <Label htmlFor="provider-import" className="text-[10px] text-stone-600">
              导入 New API 连接 JSON
            </Label>
            <div className="mt-2 flex gap-2">
              <Input
                id="provider-import"
                value={importSource}
                onChange={(event) => setImportSource(event.target.value)}
                placeholder={'{"_type":"newapi_channel_conn","key":"sk-...","url":"https://..."}'}
                className="h-8 min-w-0 text-[10px]"
                autoComplete="off"
              />
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="h-8 gap-1.5 px-2 text-[10px] shadow-none"
                onClick={handleImport}
                disabled={!importSource.trim() || !canCreate}
              >
                <Upload className="size-3" aria-hidden="true" />
                导入
              </Button>
            </div>
          </div>
        ) : null}

        <div className="grid grid-cols-1 gap-x-4 gap-y-3 sm:grid-cols-2">
          <div className="space-y-1.5">
            <Label htmlFor="provider-display-name" className="text-[10px]">名称</Label>
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
            <Label htmlFor="provider-id" className="text-[10px]">Provider ID</Label>
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
            <Label htmlFor="provider-base-url" className="text-[10px]">Base URL</Label>
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
            <Label htmlFor="provider-api-family" className="text-[10px]">API family</Label>
            <Input
              id="provider-api-family"
              value={draft.apiFamily}
              className="h-8 font-mono text-[10px]"
              readOnly
            />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="provider-logo" className="text-[10px]">Logo 资源 ID</Label>
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
            <Label htmlFor="provider-models" className="text-[10px]">模型 ID</Label>
            <Textarea
              id="provider-models"
              value={draft.modelIdsText}
              onChange={(event) => setDraft({ ...draft, modelIdsText: event.target.value })}
              className="min-h-16 resize-y font-mono text-[10px]"
              placeholder="每行一个模型 ID"
              required
            />
          </div>
          <div className="space-y-1.5 sm:col-span-2">
            <div className="flex items-center justify-between gap-2">
              <Label htmlFor="provider-key" className="text-[10px]">API Key</Label>
              {selectedConnection?.credentialConfigured ? (
                <Badge variant="secondary" className="gap-1 bg-emerald-50 text-[9px] text-emerald-700">
                  <CheckCircle2 className="size-2.5" aria-hidden="true" />
                  已配置
                </Badge>
              ) : null}
            </div>
            <div className="flex gap-2">
              <div className="relative min-w-0 flex-1">
                <KeyRound className="pointer-events-none absolute left-2.5 top-2 size-3 text-stone-400" aria-hidden="true" />
                <Input
                  id="provider-key"
                  type="password"
                  value={credential}
                  onChange={(event) => setCredential(event.target.value)}
                  className="h-8 pl-8 text-[11px]"
                  placeholder={selectedConnection?.credentialConfigured ? "输入新 Key 以替换" : "输入 API Key"}
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
                  移除
                </Button>
              ) : null}
            </div>
          </div>
        </div>

        <div className="mt-4 flex items-center gap-2 border-t border-stone-100 pt-4">
          <Switch
            id="provider-enabled"
            checked={draft.enabled}
            onCheckedChange={(enabled) => setDraft({ ...draft, enabled })}
          />
          <Label htmlFor="provider-enabled" className="text-[11px] font-normal">启用此提供商</Label>
          <div className="flex-1" />
          <Button
            type="submit"
            size="sm"
            className="h-8 px-3 text-[10px]"
            disabled={saving || (selectedConnectionId ? !canUpdate : !canCreate)}
          >
            {saving ? "保存中..." : "保存"}
          </Button>
        </div>
        </fieldset>
        {notice ? <p className="mt-3 text-[10px] text-emerald-700" role="status">{notice}</p> : null}
        {errorMessage ? <p className="mt-3 text-[10px] text-red-600" role="alert">{errorMessage}</p> : null}
      </form>

      <AlertDialog open={deleteConnection !== null} onOpenChange={(open) => !open && setDeleteConnection(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>删除“{deleteConnection?.displayName}”？</AlertDialogTitle>
            <AlertDialogDescription>
              连接配置与关联凭据会被永久移除，此操作无法撤销。
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>取消</AlertDialogCancel>
            <AlertDialogAction
              className="bg-red-600 hover:bg-red-700"
              onClick={() => void confirmDelete()}
            >
              删除
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
