import { useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { AppHeader } from "@/components/app-header";
import { Composer } from "@/components/composer";
import { Conversation, type SubmittedMessage } from "@/components/conversation";
import { ReviewSheet } from "@/components/review-sheet";
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
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetTitle,
} from "@/components/ui/sheet";
import { WorkspaceSidebar } from "@/components/workspace-sidebar";
import {
  AgentWorkspace,
  type AgentWorkspaceNavigationState,
} from "@/features/agents/agent-workspace";
import { PluginWorkspace } from "@/features/plugins/plugin-workspace";
import { ArchiveWorkspace } from "@/features/sessions/archive-workspace";
import { SettingsWorkspace } from "@/features/settings/settings-workspace";
import { createApplicationServiceClient } from "@/lib/mock-application-service";
import { findModelByRouteKey, getModelRouteKey } from "@/lib/model-route";
import { getRuntimeEnvironment } from "@/lib/runtime-environment";
import {
  useWorkspaceUiStore,
  type WorkspaceView,
} from "@/store/workspace-ui-store";
import type {
  ApplicationServiceClient,
  BackendKind,
  RunStartInput,
  SessionSnapshot,
  SessionSummary,
} from "@/types/application-service";

const EMPTY_TOOL_IDS: readonly string[] = [];
const EMPTY_SESSIONS: readonly SessionSummary[] = [];
const EMPTY_MESSAGES: readonly SubmittedMessage[] = [];

interface RunMutationRequest {
  readonly client: ApplicationServiceClient;
  readonly backend: BackendKind;
  readonly input: RunStartInput;
  readonly backendLabel: "Rust" | ".NET";
  readonly agentProfileName?: string;
  readonly supportsFollowUp: boolean;
  readonly supportsSessionGet: boolean;
  readonly submissionId: string;
}

type PendingWorkspaceNavigation =
  | { readonly kind: "create" }
  | { readonly kind: "session"; readonly sessionId: string }
  | { readonly kind: "view"; readonly view: Exclude<WorkspaceView, "conversation"> };

type SessionMessages = ReadonlyMap<string, readonly SubmittedMessage[]>;

const CLEAN_AGENT_NAVIGATION_STATE: AgentWorkspaceNavigationState = {
  dirty: false,
  locked: false,
};

/**
 * 将 portable Session 快照收敛为当前会话组件可展示的文本投影。
 *
 * 输入：已经通过 application-service wire schema 校验的完整快照。
 * 输出：保留 user/assistant 文本和工具结果摘要的稳定消息列表。
 * 不变量：不会把 reasoning、工具参数、artifact 元数据或扩展对象拼进可见文本。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function projectSessionSnapshotMessages(snapshot: SessionSnapshot): readonly SubmittedMessage[] {
  return snapshot.messages.map((message) => {
    const text = message.content
      .filter((content) => content.type === "text")
      .map((content) => content.text)
      .join("\n\n")
      .trim();
    if (message.role === "user") {
      return {
        id: message.messageId,
        role: "user" as const,
        content: text || "已发送附件",
      };
    }
    if (message.role === "tool") {
      return {
        id: message.messageId,
        role: "status" as const,
        content: text ? `${message.toolName}：${text}` : `${message.toolName} 已完成`,
      };
    }
    const toolNames = message.content
      .filter((content) => content.type === "tool_call")
      .map((content) => content.name);
    return {
      id: message.messageId,
      role: "assistant" as const,
      content:
        text ||
        message.error?.message ||
        (toolNames.length > 0 ? `请求工具：${toolNames.join("、")}` : "运行已完成"),
    };
  });
}

/**
 * 生成只用于前端并发与所有权跟踪的后端分区 Session key。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function getSessionScopeKey(backend: BackendKind, sessionId: string): string {
  return `${backend}\u0000${sessionId}`;
}

/**
 * 组合 QXNM Forge 的桌面与移动响应式 Agent 工作台。
 *
 * 不变量：组件只调用中立 application service client，不读取宿主持久化数据。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export default function App() {
  const queryClient = useQueryClient();
  const backend = useWorkspaceUiStore((state) => state.backend);
  const activeSessionId = useWorkspaceUiStore((state) => state.activeSessionId);
  const setActiveSessionId = useWorkspaceUiStore((state) => state.setActiveSessionId);
  const activeView = useWorkspaceUiStore((state) => state.activeView);
  const setActiveView = useWorkspaceUiStore((state) => state.setActiveView);
  const activeAgentProfileId = useWorkspaceUiStore((state) => state.activeAgentProfileId);
  const setActiveAgentProfileId = useWorkspaceUiStore(
    (state) => state.setActiveAgentProfileId,
  );
  const mobileSidebarOpen = useWorkspaceUiStore((state) => state.mobileSidebarOpen);
  const setMobileSidebarOpen = useWorkspaceUiStore((state) => state.setMobileSidebarOpen);
  const reviewOpen = useWorkspaceUiStore((state) => state.reviewOpen);
  const setReviewOpen = useWorkspaceUiStore((state) => state.setReviewOpen);
  const composerSubmitMode = useWorkspaceUiStore((state) => state.composerSubmitMode);
  const sidebarWidth = useWorkspaceUiStore((state) => state.sidebarWidth);
  const reduceMotion = useWorkspaceUiStore((state) => state.reduceMotion);

  const [draft, setDraft] = useState("");
  const [selectedModelRouteKey, setSelectedModelRouteKey] = useState("");
  const [messagesBySession, setMessagesBySession] = useState<SessionMessages>(new Map());
  const [previewSessions, setPreviewSessions] = useState<readonly SessionSummary[]>([]);
  const [busySessionKeys, setBusySessionKeys] = useState<ReadonlySet<string>>(new Set());
  const [sessionOperationKeys, setSessionOperationKeys] = useState<ReadonlySet<string>>(new Set());
  const [pendingSessionDelete, setPendingSessionDelete] = useState<SessionSummary | null>(null);
  const [sessionOperationError, setSessionOperationError] = useState<string | null>(null);
  const [agentNavigationState, setAgentNavigationState] =
    useState<AgentWorkspaceNavigationState>(CLEAN_AGENT_NAVIGATION_STATE);
  const [agentInitialEditorTab, setAgentInitialEditorTab] =
    useState<"profile" | "tools">("profile");
  const [pendingWorkspaceNavigation, setPendingWorkspaceNavigation] =
    useState<PendingWorkspaceNavigation | null>(null);
  const activeRunSubmissions = useRef(new Map<string, string>());
  const serviceOwnedSessionKeys = useRef(new Set<string>());
  const previousBackend = useRef(backend);

  const applicationService = useMemo(() => createApplicationServiceClient(backend), [backend]);

  useEffect(() => {
    if (previousBackend.current === backend) {
      return;
    }
    previousBackend.current = backend;
    setDraft("");
    setPreviewSessions([]);
    setPendingSessionDelete(null);
    setSessionOperationError(null);
  }, [backend]);

  const initializeQuery = useQuery({
    queryKey: ["application-service", backend, "initialize"],
    queryFn: () => applicationService.initialize(),
  });

  const modelsQuery = useQuery({
    queryKey: ["application-service", backend, "models"],
    queryFn: () => applicationService.listModels(),
  });

  const supportedToolIds = initializeQuery.data?.capabilities.tools ?? EMPTY_TOOL_IDS;
  const toolCapabilitySignature = [...supportedToolIds].sort().join("|");
  const profileQueryKey = [
    "agent-profiles",
    applicationService.mode,
    backend,
  ] as const;
  const agentProfilesQuery = useQuery({
    queryKey: profileQueryKey,
    queryFn: () => applicationService.listProfiles(),
    enabled:
      initializeQuery.isSuccess &&
      initializeQuery.data.capabilities.methods.includes("agentProfiles/list"),
    staleTime: 0,
    refetchOnMount: "always",
  });

  const runtimeEnvironmentQuery = useQuery({
    queryKey: ["runtime-environment"],
    queryFn: getRuntimeEnvironment,
    staleTime: Number.POSITIVE_INFINITY,
  });

  const sessionQueryKey = ["sessions", applicationService.mode, backend] as const;
  const sessionsQuery = useQuery({
    queryKey: sessionQueryKey,
    queryFn: () => applicationService.listSessions(),
    enabled:
      applicationService.mode === "faux-preview" ||
      (initializeQuery.isSuccess &&
        initializeQuery.data.capabilities.methods.includes("session/list")),
    staleTime: 0,
  });
  const serviceMethods = initializeQuery.data?.capabilities.methods ?? EMPTY_TOOL_IDS;
  const canArchiveSessions =
    applicationService.mode === "faux-preview" || serviceMethods.includes("session/archive");
  const canRestoreSessions =
    applicationService.mode === "faux-preview" || serviceMethods.includes("session/restore");
  const canDeleteSessions =
    applicationService.mode === "faux-preview" || serviceMethods.includes("session/delete");
  const canCreateProfiles =
    applicationService.mode === "faux-preview" || serviceMethods.includes("agentProfiles/create");
  const canUpdateProfiles =
    applicationService.mode === "faux-preview" || serviceMethods.includes("agentProfiles/update");
  const canDeleteProfiles =
    applicationService.mode === "faux-preview" || serviceMethods.includes("agentProfiles/delete");

  const agentProfiles = agentProfilesQuery.data ?? [];
  const models = modelsQuery.data ?? [];
  const selectableAgentProfiles = agentProfiles.filter(
    (profile) =>
      profile.enabled &&
      findModelByRouteKey(models, getModelRouteKey(profile.model)) !== undefined,
  );
  const activeAgentProfile = selectableAgentProfiles.find(
    (profile) => profile.profileId === activeAgentProfileId,
  );

  /**
   * 将一条消息追加到提交时的 Session，不读取回调执行时的当前视图。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const appendSessionMessage = (
    targetBackend: BackendKind,
    sessionId: string,
    message: SubmittedMessage,
  ) => {
    const sessionScopeKey = getSessionScopeKey(targetBackend, sessionId);
    setMessagesBySession((currentMessages) => {
      const nextMessages = new Map(currentMessages);
      nextMessages.set(
        sessionScopeKey,
        [...(currentMessages.get(sessionScopeKey) ?? []), message],
      );
      return nextMessages;
    });
  };

  const runMutation = useMutation({
    mutationFn: (request: RunMutationRequest) => request.client.startRun(request.input),
    onSuccess: async (result, request) => {
      appendSessionMessage(request.backend, request.input.sessionId, {
        id: result.runId,
        role: "status",
        content: request.agentProfileName
          ? request.client.mode === "application-service"
            ? `运行已由 ${request.backendLabel} application service 接受，并绑定智能体“${request.agentProfileName}”的精确 revision。`
            : `运行已由 ${request.backendLabel} capability 画像接受。当前选择“${request.agentProfileName}”内存预览。`
          : request.supportsFollowUp
            ? `运行已由 ${request.backendLabel} capability 画像接受。此连接广告了 follow-up；正式连接后仍由服务事件重建进度和结果。`
            : `运行已由 ${request.backendLabel} capability 画像接受。正式连接后仍由服务事件重建进度和结果。`,
      });
      if (request.client.mode === "application-service") {
        serviceOwnedSessionKeys.current.add(
          getSessionScopeKey(request.backend, request.input.sessionId),
        );
        await Promise.all([
          queryClient.invalidateQueries({
            queryKey: ["sessions", request.client.mode, request.backend],
          }),
          request.supportsSessionGet
            ? queryClient.fetchQuery({
                queryKey: [
                  "session-snapshot",
                  request.client.mode,
                  request.backend,
                  request.input.sessionId,
                ],
                queryFn: () => request.client.getSession(request.input.sessionId),
              })
            : Promise.resolve(),
        ]).catch(() => undefined);
        if (useWorkspaceUiStore.getState().backend === request.backend) {
          setPreviewSessions((currentSessions) =>
            currentSessions.filter((session) => session.sessionId !== request.input.sessionId),
          );
        }
      }
    },
    onError: (_error, request) => {
      appendSessionMessage(request.backend, request.input.sessionId, {
        id: crypto.randomUUID(),
        role: "status",
        content: "运行请求未被服务接受。",
      });
    },
    onSettled: (_result, _error, request) => {
      const sessionScopeKey = getSessionScopeKey(request.backend, request.input.sessionId);
      if (activeRunSubmissions.current.get(sessionScopeKey) !== request.submissionId) {
        return;
      }
      activeRunSubmissions.current.delete(sessionScopeKey);
      setBusySessionKeys((currentSessionKeys) => {
        const nextSessionKeys = new Set(currentSessionKeys);
        nextSessionKeys.delete(sessionScopeKey);
        return nextSessionKeys;
      });
    },
  });

  const serviceSessions = sessionsQuery.data ?? EMPTY_SESSIONS;
  const allSessions = useMemo(
    () => {
      const sessions = new Map<string, SessionSummary>();
      for (const session of previewSessions) {
        if (!session.archived) {
          sessions.set(session.sessionId, session);
        }
      }
      for (const session of serviceSessions) {
        if (!session.archived) {
          sessions.set(session.sessionId, session);
        }
      }
      return [...sessions.values()];
    },
    [previewSessions, serviceSessions],
  );
  const archivedSessions = useMemo(
    () => {
      const sessions = new Map<string, SessionSummary>();
      for (const session of previewSessions) {
        if (session.archived) {
          sessions.set(session.sessionId, session);
        }
      }
      for (const session of serviceSessions) {
        if (session.archived) {
          sessions.set(session.sessionId, session);
        }
      }
      return [...sessions.values()];
    },
    [previewSessions, serviceSessions],
  );
  const allBusySessionIds = useMemo(
    () => {
      const busyIds = new Set<string>();
      for (const session of [...allSessions, ...archivedSessions]) {
        const sessionScopeKey = getSessionScopeKey(backend, session.sessionId);
        if (
          busySessionKeys.has(sessionScopeKey) ||
          sessionOperationKeys.has(sessionScopeKey)
        ) {
          busyIds.add(session.sessionId);
        }
      }
      return busyIds;
    },
    [allSessions, archivedSessions, backend, busySessionKeys, sessionOperationKeys],
  );
  const activeSession =
    allSessions.find((session) => session.sessionId === activeSessionId) ?? {
      sessionId: activeSessionId,
      title: "新任务",
      project: allSessions[0]?.project ?? "本地工作区",
      updatedAt: new Date().toISOString(),
      archived: false,
    };
  const activeSessionIsServiceOwned =
    serviceOwnedSessionKeys.current.has(getSessionScopeKey(backend, activeSession.sessionId)) ||
    serviceSessions.some((session) => session.sessionId === activeSession.sessionId);
  const sessionSnapshotQuery = useQuery({
    queryKey: [
      "session-snapshot",
      applicationService.mode,
      backend,
      activeSession.sessionId,
    ],
    queryFn: () => applicationService.getSession(activeSession.sessionId),
    enabled:
      activeView === "conversation" &&
      applicationService.mode === "application-service" &&
      serviceMethods.includes("session/get") &&
      activeSessionIsServiceOwned,
    staleTime: 0,
    refetchInterval: (query) => query.state.data?.activeRunId ? 750 : false,
  });
  const localMessages =
    messagesBySession.get(getSessionScopeKey(backend, activeSession.sessionId)) ?? [];
  const snapshotMessages = sessionSnapshotQuery.data
    ? projectSessionSnapshotMessages(sessionSnapshotQuery.data)
    : EMPTY_MESSAGES;
  const activeRequestPending = busySessionKeys.has(
    getSessionScopeKey(backend, activeSession.sessionId),
  );
  const snapshotUserContent = new Set(
    snapshotMessages
      .filter((message) => message.role === "user")
      .map((message) => message.content),
  );
  const messages = sessionSnapshotQuery.data
    ? [
        ...snapshotMessages,
        ...localMessages.filter(
          (message) =>
            message.role === "status" ||
            (message.role === "user" &&
              (activeRequestPending || !snapshotUserContent.has(message.content))),
        ),
      ]
    : localMessages;
  const activeSessionBusy =
    activeRequestPending ||
    (sessionSnapshotQuery.data?.activeRunId !== null &&
      sessionSnapshotQuery.data?.activeRunId !== undefined);
  const agentModelRouteKey = activeAgentProfile
    ? getModelRouteKey(activeAgentProfile.model)
    : "";
  const preferredModelRouteKey = agentModelRouteKey || selectedModelRouteKey;
  const selectedModel =
    findModelByRouteKey(models, preferredModelRouteKey) ?? models[0];
  const resolvedModelRouteKey = selectedModel ? getModelRouteKey(selectedModel) : "";
  const implementationLabel =
    initializeQuery.data?.implementation.name ?? (initializeQuery.isError ? "初始化失败" : "正在初始化");

  /**
   * 将用户消息提交到当前后端客户端并清空输入框。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleSubmit = () => {
    const prompt = draft.trim();
    const sessionScopeKey = getSessionScopeKey(backend, activeSession.sessionId);
    if (
      prompt.length === 0 ||
      !selectedModel ||
      activeRunSubmissions.current.has(sessionScopeKey)
    ) {
      return;
    }

    const submissionId = crypto.randomUUID();
    activeRunSubmissions.current.set(sessionScopeKey, submissionId);
    setBusySessionKeys((currentSessionKeys) => new Set(currentSessionKeys).add(sessionScopeKey));
    appendSessionMessage(backend, activeSession.sessionId, {
      id: crypto.randomUUID(),
      role: "user",
      content: prompt,
    });
    setDraft("");
    runMutation.mutate({
      client: applicationService,
      backend,
      backendLabel: backend === "rust" ? "Rust" : ".NET",
      agentProfileName: activeAgentProfile?.displayName,
      supportsFollowUp:
        initializeQuery.data?.capabilities.methods.includes("run/followUp") ?? false,
      supportsSessionGet:
        initializeQuery.data?.capabilities.methods.includes("session/get") ?? false,
      submissionId,
      input: {
        sessionId: activeSession.sessionId,
        prompt,
        model: {
          providerId: selectedModel.providerId,
          modelId: selectedModel.modelId,
          apiFamily: selectedModel.apiFamily,
        },
        ...(activeAgentProfile
          ? {
              agentProfile: {
                profileId: activeAgentProfile.profileId,
                revision: activeAgentProfile.revision,
              },
            }
          : {}),
      },
    });
  };

  /**
   * 执行已经通过未保存检查的 Session 导航。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const applyWorkspaceNavigation = (navigation: PendingWorkspaceNavigation) => {
    if (navigation.kind === "view") {
      setActiveView(navigation.view);
      setMobileSidebarOpen(false);
      return;
    }
    const sessionId =
      navigation.kind === "create"
        ? `ui-preview:${crypto.randomUUID()}`
        : navigation.sessionId;
    if (navigation.kind === "create") {
      const session: SessionSummary = {
        sessionId,
        title: "新任务",
        project: activeSession.project,
        updatedAt: new Date().toISOString(),
        archived: false,
        status: "active",
      };
      setPreviewSessions((currentSessions) => [session, ...currentSessions]);
    }
    setDraft("");
    setActiveSessionId(sessionId);
    setActiveView("conversation");
    setMobileSidebarOpen(false);
  };

  /**
   * 请求离开智能体编辑器；未保存草稿必须先由用户确认。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const requestWorkspaceNavigation = (navigation: PendingWorkspaceNavigation) => {
    setMobileSidebarOpen(false);
    if (activeView === "agents" && agentNavigationState.locked) {
      return;
    }
    if (activeView === "agents" && agentNavigationState.dirty) {
      setPendingWorkspaceNavigation(navigation);
      return;
    }
    applyWorkspaceNavigation(navigation);
  };

  /**
   * 创建具有独立标识的本地预览 Session。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleCreateSession = () => {
    requestWorkspaceNavigation({ kind: "create" });
  };

  /**
   * 请求打开指定预览 Session。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleSelectSession = (sessionId: string) => {
    requestWorkspaceNavigation({ kind: "session", sessionId });
  };

  /**
   * 打开指定管理视图，并沿用智能体未保存草稿保护。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleOpenView = (view: Exclude<WorkspaceView, "conversation">) => {
    if (view === "agents") {
      setAgentInitialEditorTab("profile");
    }
    requestWorkspaceNavigation({ kind: "view", view });
  };

  /**
   * 从设置或插件目录直接打开当前智能体的工具请求子集。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleOpenAgentTools = () => {
    setAgentInitialEditorTab("tools");
    requestWorkspaceNavigation({ kind: "view", view: "agents" });
  };

  /**
   * 放弃智能体草稿并执行等待中的外层导航。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const confirmWorkspaceNavigation = () => {
    const navigation = pendingWorkspaceNavigation;
    setPendingWorkspaceNavigation(null);
    if (navigation) {
      applyWorkspaceNavigation(navigation);
    }
  };

  /**
   * 选择输入器的智能体预览，并采用该投影的完整默认模型路由。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleAgentChange = (profileId: string | null) => {
    setActiveAgentProfileId(profileId);
    const profile = selectableAgentProfiles.find(
      (candidate) => candidate.profileId === profileId,
    );
    if (profile) {
      setSelectedModelRouteKey(getModelRouteKey(profile.model));
    }
  };

  /**
   * 在 Session 管理请求期间更新侧栏与归档列表的操作锁。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const setSessionOperation = (sessionId: string, busy: boolean) => {
    const sessionScopeKey = getSessionScopeKey(backend, sessionId);
    setSessionOperationKeys((currentKeys) => {
      const nextKeys = new Set(currentKeys);
      if (busy) {
        nextKeys.add(sessionScopeKey);
      } else {
        nextKeys.delete(sessionScopeKey);
      }
      return nextKeys;
    });
  };

  /**
   * 当前 Session 离开活动列表后选择稳定后备项，必要时创建本地空白任务。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const selectSessionFallback = (removedSessionId: string) => {
    if (activeSessionId !== removedSessionId) {
      return;
    }
    const fallback = allSessions.find(
      (session) => session.sessionId !== removedSessionId,
    );
    if (fallback) {
      setActiveSessionId(fallback.sessionId);
      return;
    }
    applyWorkspaceNavigation({ kind: "create" });
  };

  /**
   * 归档服务 Session 或尚未持久化的本地预览 Session。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleArchiveSession = async (session: SessionSummary) => {
    if (!canArchiveSessions) {
      setSessionOperationError("当前 application service 不支持归档会话");
      return;
    }
    setSessionOperation(session.sessionId, true);
    try {
      const serviceOwnsSession =
        serviceOwnedSessionKeys.current.has(getSessionScopeKey(backend, session.sessionId)) ||
        serviceSessions.some((candidate) => candidate.sessionId === session.sessionId);
      if (
        !serviceOwnsSession &&
        previewSessions.some((candidate) => candidate.sessionId === session.sessionId)
      ) {
        setPreviewSessions((currentSessions) =>
          currentSessions.map((currentSession) =>
            currentSession.sessionId === session.sessionId
              ? {
                  sessionId: currentSession.sessionId,
                  title: currentSession.title,
                  project: currentSession.project,
                  updatedAt: currentSession.updatedAt,
                  archived: true,
                }
              : currentSession,
          ),
        );
      } else {
        await applicationService.archiveSession(session.sessionId);
        if (useWorkspaceUiStore.getState().backend === backend) {
          setPreviewSessions((currentSessions) =>
            currentSessions.filter((candidate) => candidate.sessionId !== session.sessionId),
          );
        }
        await queryClient.invalidateQueries({ queryKey: sessionQueryKey });
      }
      if (useWorkspaceUiStore.getState().backend === backend) {
        selectSessionFallback(session.sessionId);
      }
    } catch {
      if (useWorkspaceUiStore.getState().backend === backend) {
        setSessionOperationError("归档失败，会话仍保留在最近任务中");
      }
    } finally {
      setSessionOperation(session.sessionId, false);
    }
  };

  /**
   * 将归档 Session 恢复到最近任务列表。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleRestoreSession = async (sessionId: string) => {
    if (!canRestoreSessions) {
      setSessionOperationError("当前 application service 不支持恢复会话");
      return;
    }
    setSessionOperation(sessionId, true);
    try {
      const serviceOwnsSession =
        serviceOwnedSessionKeys.current.has(getSessionScopeKey(backend, sessionId)) ||
        serviceSessions.some((candidate) => candidate.sessionId === sessionId);
      if (
        !serviceOwnsSession &&
        previewSessions.some((candidate) => candidate.sessionId === sessionId)
      ) {
        setPreviewSessions((currentSessions) =>
          currentSessions.map((session) =>
            session.sessionId === sessionId ? { ...session, archived: false } : session,
          ),
        );
      } else {
        await applicationService.restoreSession(sessionId);
        await queryClient.invalidateQueries({ queryKey: sessionQueryKey });
      }
    } catch {
      if (useWorkspaceUiStore.getState().backend === backend) {
        setSessionOperationError("恢复失败，会话仍保留在归档中");
      }
    } finally {
      setSessionOperation(sessionId, false);
    }
  };

  /**
   * 在用户确认后永久删除 Session，并清除对应的前端消息投影。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const confirmDeleteSession = async () => {
    const session = pendingSessionDelete;
    setPendingSessionDelete(null);
    if (!session) {
      return;
    }
    if (!canDeleteSessions) {
      setSessionOperationError("当前 application service 不支持永久删除会话");
      return;
    }
    setSessionOperation(session.sessionId, true);
    try {
      const serviceOwnsSession =
        serviceOwnedSessionKeys.current.has(getSessionScopeKey(backend, session.sessionId)) ||
        serviceSessions.some((candidate) => candidate.sessionId === session.sessionId);
      if (
        !serviceOwnsSession &&
        previewSessions.some((candidate) => candidate.sessionId === session.sessionId)
      ) {
        setPreviewSessions((currentSessions) =>
          currentSessions.filter(
            (currentSession) => currentSession.sessionId !== session.sessionId,
          ),
        );
      } else {
        await applicationService.deleteSession(session.sessionId);
        serviceOwnedSessionKeys.current.delete(getSessionScopeKey(backend, session.sessionId));
        if (useWorkspaceUiStore.getState().backend === backend) {
          setPreviewSessions((currentSessions) =>
            currentSessions.filter((candidate) => candidate.sessionId !== session.sessionId),
          );
        }
        await queryClient.invalidateQueries({ queryKey: sessionQueryKey });
      }
      setMessagesBySession((currentMessages) => {
        const nextMessages = new Map(currentMessages);
        nextMessages.delete(getSessionScopeKey(backend, session.sessionId));
        return nextMessages;
      });
      if (useWorkspaceUiStore.getState().backend === backend) {
        selectSessionFallback(session.sessionId);
      }
    } catch {
      if (useWorkspaceUiStore.getState().backend === backend) {
        setSessionOperationError("删除失败，会话及其记录未被移除");
      }
    } finally {
      setSessionOperation(session.sessionId, false);
    }
  };

  return (
    <div
      className="flex h-dvh w-full overflow-hidden bg-white text-stone-900"
      data-reduce-motion={reduceMotion ? "true" : "false"}
    >
      <aside
        className={`hidden h-full shrink-0 border-r border-stone-200/70 md:block ${
          sidebarWidth === "compact" ? "w-[260px]" : "w-[300px]"
        }`}
      >
        <WorkspaceSidebar
          onCreateSession={handleCreateSession}
          onOpenView={handleOpenView}
          onSelectSession={handleSelectSession}
          onArchiveSession={(session) => void handleArchiveSession(session)}
          onDeleteSession={setPendingSessionDelete}
          navigationDisabled={activeView === "agents" && agentNavigationState.locked}
          canArchiveSessions={canArchiveSessions}
          canDeleteSessions={canDeleteSessions}
          busySessionIds={allBusySessionIds}
          sessions={allSessions}
          workspaceName={activeSession.project}
        />
      </aside>

      <Sheet open={mobileSidebarOpen} onOpenChange={setMobileSidebarOpen}>
        <SheetContent side="left" className="w-[300px] max-w-[88vw] p-0 [&>button]:hidden">
          <SheetTitle className="sr-only">项目导航</SheetTitle>
          <SheetDescription className="sr-only">选择工作区和最近任务</SheetDescription>
          <WorkspaceSidebar
            onCreateSession={handleCreateSession}
            onOpenView={handleOpenView}
            onSelectSession={handleSelectSession}
            onArchiveSession={(session) => void handleArchiveSession(session)}
            onDeleteSession={setPendingSessionDelete}
            navigationDisabled={activeView === "agents" && agentNavigationState.locked}
            canArchiveSessions={canArchiveSessions}
            canDeleteSessions={canDeleteSessions}
            onRequestClose={() => setMobileSidebarOpen(false)}
            busySessionIds={allBusySessionIds}
            sessions={allSessions}
            workspaceName={activeSession.project}
          />
        </SheetContent>
      </Sheet>

      <main className="flex min-w-0 flex-1 flex-col bg-white">
        {activeView === "agents" ? (
          <AgentWorkspace
            key={`${backend}:${toolCapabilitySignature}:${agentInitialEditorTab}`}
            service={applicationService}
            profiles={agentProfiles}
            profilesLoading={agentProfilesQuery.isLoading}
            profileQueryKey={profileQueryKey}
            models={models}
            supportedToolIds={supportedToolIds}
            canCreate={canCreateProfiles}
            canUpdate={canUpdateProfiles}
            canDelete={canDeleteProfiles}
            initialEditorTab={agentInitialEditorTab}
            onOpenMobileSidebar={() => setMobileSidebarOpen(true)}
            onNavigationStateChange={setAgentNavigationState}
          />
        ) : activeView === "plugins" ? (
          <PluginWorkspace
            key={toolCapabilitySignature}
            supportedToolIds={supportedToolIds}
            loading={initializeQuery.isLoading}
            onOpenAgentTools={handleOpenAgentTools}
            onOpenMobileSidebar={() => setMobileSidebarOpen(true)}
          />
        ) : activeView === "settings" ? (
          <SettingsWorkspace
            backend={backend}
            service={applicationService}
            initializeResult={initializeQuery.data}
            runtimeEnvironment={runtimeEnvironmentQuery.data}
            onOpenArchive={() => setActiveView("archive")}
            onOpenAgentTools={handleOpenAgentTools}
            onOpenMobileSidebar={() => setMobileSidebarOpen(true)}
          />
        ) : activeView === "archive" ? (
          <ArchiveWorkspace
            sessions={archivedSessions}
            busySessionIds={allBusySessionIds}
            canRestore={canRestoreSessions}
            canDelete={canDeleteSessions}
            onRestore={(sessionId) => void handleRestoreSession(sessionId)}
            onDelete={setPendingSessionDelete}
            onOpenMobileSidebar={() => setMobileSidebarOpen(true)}
          />
        ) : (
          <>
            <AppHeader
              title={activeSession.title}
              projectName={activeSession.project}
              implementationLabel={implementationLabel}
              connected={initializeQuery.isSuccess}
              previewReviewAvailable={applicationService.mode === "faux-preview"}
              onOpenMobileSidebar={() => setMobileSidebarOpen(true)}
              onOpenReview={() => setReviewOpen(true)}
            />
            <Conversation
              backendLabel={backend === "rust" ? "Rust" : ".NET"}
              messages={messages}
              busy={activeSessionBusy}
              historyLoading={sessionSnapshotQuery.isLoading}
              historyError={sessionSnapshotQuery.isError}
              showFixture={
                applicationService.mode === "faux-preview" &&
                previewSessions.every(
                  (session) => session.sessionId !== activeSession.sessionId,
                )
              }
            />
            <Composer
              value={draft}
              selectedModelRouteKey={resolvedModelRouteKey}
              models={models}
              agentProfiles={selectableAgentProfiles}
              selectedAgentProfileId={activeAgentProfile?.profileId ?? null}
              runtimeEnvironment={runtimeEnvironmentQuery.data}
              submitMode={composerSubmitMode}
              busy={activeSessionBusy}
              onValueChange={setDraft}
              onModelChange={setSelectedModelRouteKey}
              onAgentChange={handleAgentChange}
              onSubmit={handleSubmit}
            />
          </>
        )}
      </main>

      {applicationService.mode === "faux-preview" ? (
        <ReviewSheet open={reviewOpen} onOpenChange={setReviewOpen} />
      ) : null}
      <AlertDialog
        open={pendingWorkspaceNavigation !== null}
        onOpenChange={(open) => {
          if (!open) {
            setPendingWorkspaceNavigation(null);
          }
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>放弃未保存修改？</AlertDialogTitle>
            <AlertDialogDescription>
              当前智能体草稿尚未保存，继续后这些修改会被丢弃。
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>继续编辑</AlertDialogCancel>
            <AlertDialogAction onClick={confirmWorkspaceNavigation}>放弃修改</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
      <AlertDialog
        open={pendingSessionDelete !== null}
        onOpenChange={(open) => {
          if (!open) {
            setPendingSessionDelete(null);
          }
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>永久删除“{pendingSessionDelete?.title}”？</AlertDialogTitle>
            <AlertDialogDescription>
              该会话及其记录会被永久删除，此操作无法撤销。
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>取消</AlertDialogCancel>
            <AlertDialogAction
              className="bg-red-600 hover:bg-red-700"
              onClick={() => void confirmDeleteSession()}
            >
              永久删除
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
      <AlertDialog
        open={sessionOperationError !== null}
        onOpenChange={(open) => {
          if (!open) {
            setSessionOperationError(null);
          }
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>会话操作未完成</AlertDialogTitle>
            <AlertDialogDescription>{sessionOperationError}</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogAction onClick={() => setSessionOperationError(null)}>
              知道了
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
