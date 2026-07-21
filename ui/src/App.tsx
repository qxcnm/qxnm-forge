import { useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";

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
import { projectPendingApprovals } from "@/features/approvals/pending-approvals";
import { PluginWorkspace } from "@/features/plugins/plugin-workspace";
import { ArchiveWorkspace } from "@/features/sessions/archive-workspace";
import { SettingsWorkspace } from "@/features/settings/settings-workspace";
import { useApplicationServiceEvents } from "@/lib/application-service-events";
import { createApplicationServiceClient } from "@/lib/mock-application-service";
import { findModelByRouteKey, getModelRouteKey } from "@/lib/model-route";
import { getRuntimeEnvironment } from "@/lib/runtime-environment";
import i18n from "@/i18n";
import {
  useWorkspaceUiStore,
  type WorkspaceView,
} from "@/store/workspace-ui-store";
import type {
  ApprovalChoice,
  ApplicationServiceClient,
  BackendKind,
  PendingApproval,
  RunStartInput,
  SessionSnapshot,
  SessionSummary,
} from "@/types/application-service";

const EMPTY_TOOL_IDS: readonly string[] = [];
const EMPTY_MODELS = [] as const;
const EMPTY_AGENT_PROFILES = [] as const;
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

interface ApprovalMutationRequest {
  readonly client: ApplicationServiceClient;
  readonly backend: BackendKind;
  readonly approval: PendingApproval;
  readonly choice: ApprovalChoice;
}

interface InitializedServiceSnapshot {
  readonly result: Awaited<ReturnType<ApplicationServiceClient["initialize"]>>;
  readonly generation: string;
}

interface ApprovalConfirmation {
  readonly approvalId: string;
  readonly sessionId: string;
  readonly runId: string;
  readonly choice: ApprovalChoice;
  readonly status: "submitting" | "awaiting_snapshot" | "refresh_error";
}

interface AppProps {
  readonly serviceFactory?: (backend: BackendKind) => ApplicationServiceClient;
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
 * 初始化 application service，并为这次成功握手签发仅用于前端查询隔离的代际。
 *
 * 输入：当前后端的品牌中立客户端；输出：服务初始化结果与不可复用的本地代际。
 * 不变量：代际不进入协议、Session 或持久化，只阻止旧模型快照跨握手复用。
 * 失败：初始化失败时原样拒绝，不产生可用代际。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
async function initializeApplicationService(
  client: ApplicationServiceClient,
): Promise<InitializedServiceSnapshot> {
  const result = await client.initialize();
  return { result, generation: crypto.randomUUID() };
}

/**
 * 生成审批在当前 Session 与 run 内的稳定前端状态键。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function getApprovalStateKey(approval: PendingApproval): string {
  return `${approval.sessionId}\u0000${approval.runId}\u0000${approval.request.approvalId}`;
}

/**
 * 判断审批是否已到服务声明的绝对过期时间。
 *
 * 输入：服务端已经通过 schema 校验的审批请求；输出：无效或已到期时为 true。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function isApprovalExpired(approval: PendingApproval): boolean {
  const expiresAt = Date.parse(approval.request.expiresAt);
  return !Number.isFinite(expiresAt) || expiresAt <= Date.now();
}

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
        content: text || i18n.t("app.attachmentSent"),
      };
    }
    if (message.role === "tool") {
      return {
        id: message.messageId,
        role: "status" as const,
        content: text
          ? i18n.t("app.toolResult", { name: message.toolName, text })
          : i18n.t("app.toolCompleted", { name: message.toolName }),
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
        (toolNames.length > 0
          ? i18n.t("app.requestedTools", {
              names: toolNames.join(i18n.resolvedLanguage === "en-US" ? ", " : "、"),
            })
          : i18n.t("app.runCompleted")),
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
export default function App({
  serviceFactory = createApplicationServiceClient,
}: AppProps = {}) {
  const { t } = useTranslation();
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
  const [approvalErrorId, setApprovalErrorId] = useState<string | null>(null);
  const [approvalConfirmations, setApprovalConfirmations] = useState<
    ReadonlyMap<string, ApprovalConfirmation>
  >(new Map());
  const [agentNavigationState, setAgentNavigationState] =
    useState<AgentWorkspaceNavigationState>(CLEAN_AGENT_NAVIGATION_STATE);
  const [agentInitialEditorTab, setAgentInitialEditorTab] =
    useState<"profile" | "tools">("profile");
  const [pendingWorkspaceNavigation, setPendingWorkspaceNavigation] =
    useState<PendingWorkspaceNavigation | null>(null);
  const activeRunSubmissions = useRef(new Map<string, string>());
  const serviceOwnedSessionKeys = useRef(new Set<string>());
  const previousBackend = useRef(backend);
  const serviceCache = useRef({
    factory: serviceFactory,
    clients: new Map<BackendKind, ApplicationServiceClient>(),
  });

  const applicationService = useMemo(
    () => {
      if (serviceCache.current.factory !== serviceFactory) {
        serviceCache.current = {
          factory: serviceFactory,
          clients: new Map<BackendKind, ApplicationServiceClient>(),
        };
      }
      const cachedService = serviceCache.current.clients.get(backend);
      if (cachedService) {
        return cachedService;
      }
      const service = serviceFactory(backend);
      serviceCache.current.clients.set(backend, service);
      return service;
    },
    [backend, serviceFactory],
  );

  useEffect(() => {
    if (previousBackend.current === backend) {
      return;
    }
    previousBackend.current = backend;
    setDraft("");
    setSelectedModelRouteKey("");
    setActiveAgentProfileId(null);
    setPreviewSessions([]);
    setPendingSessionDelete(null);
    setSessionOperationError(null);
    setApprovalErrorId(null);
    setApprovalConfirmations(new Map());
  }, [backend, setActiveAgentProfileId]);

  const initializeQuery = useQuery({
    queryKey: ["application-service", backend, "initialize"],
    queryFn: () => initializeApplicationService(applicationService),
  });

  const initializeReady =
    initializeQuery.isSuccess && initializeQuery.fetchStatus === "idle";
  const initializeSnapshot = initializeReady ? initializeQuery.data : undefined;
  const serviceMethods = initializeSnapshot?.result.capabilities.methods ?? EMPTY_TOOL_IDS;
  const supportedToolIds = initializeSnapshot?.result.capabilities.tools ?? EMPTY_TOOL_IDS;
  const serviceEventTypes =
    initializeSnapshot?.result.capabilities.eventTypes ?? EMPTY_TOOL_IDS;
  const initializeGeneration = initializeSnapshot?.generation ?? "uninitialized";
  const advertisesModels = serviceMethods.includes("models/list");
  const advertisesAgentProfiles = serviceMethods.includes("agentProfiles/list");

  const modelsQuery = useQuery({
    queryKey: [
      "application-service",
      backend,
      "models",
      initializeGeneration,
    ],
    queryFn: () => applicationService.listModels(),
    enabled: initializeReady && advertisesModels,
  });

  const toolCapabilitySignature = [...supportedToolIds].sort().join("|");
  const profileQueryKey = [
    "agent-profiles",
    applicationService.mode,
    backend,
    initializeGeneration,
  ] as const;
  const agentProfilesQuery = useQuery({
    queryKey: profileQueryKey,
    queryFn: () => applicationService.listProfiles(),
    enabled: initializeReady && advertisesAgentProfiles,
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
    enabled: initializeReady && serviceMethods.includes("session/list"),
    staleTime: 0,
  });
  useApplicationServiceEvents(backend, (event) => {
    if (
      applicationService.mode !== "application-service" ||
      !serviceEventTypes.includes(event.type)
    ) {
      return;
    }
    void queryClient.invalidateQueries({
      queryKey: [
        "session-snapshot",
        applicationService.mode,
        backend,
        event.sessionId,
      ],
      exact: true,
    });
    void queryClient.invalidateQueries({ queryKey: sessionQueryKey, exact: true });
  });
  const canArchiveSessions = serviceMethods.includes("session/archive");
  const canRestoreSessions = serviceMethods.includes("session/restore");
  const canDeleteSessions = serviceMethods.includes("session/delete");
  const canCreateProfiles = serviceMethods.includes("agentProfiles/create");
  const canUpdateProfiles = serviceMethods.includes("agentProfiles/update");
  const canDeleteProfiles = serviceMethods.includes("agentProfiles/delete");

  const agentProfilesReady =
    initializeReady &&
    advertisesAgentProfiles &&
    agentProfilesQuery.isSuccess &&
    agentProfilesQuery.fetchStatus === "idle";
  const agentProfiles = agentProfilesReady
    ? agentProfilesQuery.data
    : EMPTY_AGENT_PROFILES;
  const modelsReady =
    initializeReady &&
    advertisesModels &&
    modelsQuery.isSuccess &&
    modelsQuery.fetchStatus === "idle";
  const models = modelsReady ? modelsQuery.data : EMPTY_MODELS;
  useEffect(() => {
    if (!initializeReady) {
      return;
    }
    if (!advertisesModels) {
      setSelectedModelRouteKey("");
      return;
    }
    if (!modelsReady) {
      return;
    }
    setSelectedModelRouteKey((currentRouteKey) => {
      if (findModelByRouteKey(models, currentRouteKey)) {
        return currentRouteKey;
      }
      return models[0] ? getModelRouteKey(models[0]) : "";
    });
  }, [advertisesModels, initializeReady, models, modelsReady]);
  const selectableAgentProfiles = useMemo(
    () =>
      agentProfiles.filter(
        (profile) =>
          profile.enabled &&
          findModelByRouteKey(models, getModelRouteKey(profile.model)) !== undefined,
      ),
    [agentProfiles, models],
  );
  const activeAgentProfile = selectableAgentProfiles.find(
    (profile) => profile.profileId === activeAgentProfileId,
  );
  useEffect(() => {
    if (activeAgentProfileId === null || !initializeReady) {
      return;
    }
    if (!advertisesModels || !advertisesAgentProfiles) {
      setActiveAgentProfileId(null);
      return;
    }
    if (!modelsReady || !agentProfilesReady) {
      return;
    }
    const selectedProfile = agentProfiles.find(
      (profile) => profile.profileId === activeAgentProfileId,
    );
    if (
      !selectedProfile?.enabled ||
      !findModelByRouteKey(models, getModelRouteKey(selectedProfile.model))
    ) {
      setActiveAgentProfileId(null);
    }
  }, [
    activeAgentProfileId,
    advertisesAgentProfiles,
    advertisesModels,
    agentProfiles,
    agentProfilesReady,
    initializeReady,
    models,
    modelsReady,
    setActiveAgentProfileId,
  ]);

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
      const translation: NonNullable<SubmittedMessage["translation"]> = request.agentProfileName
        ? request.client.mode === "application-service"
          ? {
              key: "app.runAcceptedProfile",
              values: {
                backend: request.backendLabel,
                profile: request.agentProfileName,
              },
            }
          : {
              key: "app.runAcceptedPreviewProfile",
              values: {
                backend: request.backendLabel,
                profile: request.agentProfileName,
              },
            }
        : request.supportsFollowUp
          ? {
              key: "app.runAcceptedFollowUp",
              values: { backend: request.backendLabel },
            }
          : {
              key: "app.runAccepted",
              values: { backend: request.backendLabel },
            };
      appendSessionMessage(request.backend, request.input.sessionId, {
        id: result.runId,
        role: "status",
        content: "",
        translation,
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
        content: "",
        translation: { key: "app.runRejected" },
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

  const approvalMutation = useMutation({
    mutationFn: (request: ApprovalMutationRequest) =>
      request.client.respondToApproval(
        request.approval.sessionId,
        request.approval.runId,
        request.approval.request.approvalId,
        { choice: request.choice },
      ),
    onMutate: (request) => {
      setApprovalErrorId(null);
      if (useWorkspaceUiStore.getState().backend !== request.backend) {
        return;
      }
      const approvalKey = getApprovalStateKey(request.approval);
      setApprovalConfirmations((currentConfirmations) => {
        const nextConfirmations = new Map(currentConfirmations);
        nextConfirmations.set(approvalKey, {
          approvalId: request.approval.request.approvalId,
          sessionId: request.approval.sessionId,
          runId: request.approval.runId,
          choice: request.choice,
          status: "submitting",
        });
        return nextConfirmations;
      });
    },
    onSuccess: async (_result, request) => {
      const approvalKey = getApprovalStateKey(request.approval);
      if (useWorkspaceUiStore.getState().backend === request.backend) {
        setApprovalErrorId(null);
        setApprovalConfirmations((currentConfirmations) => {
          const nextConfirmations = new Map(currentConfirmations);
          nextConfirmations.set(approvalKey, {
            approvalId: request.approval.request.approvalId,
            sessionId: request.approval.sessionId,
            runId: request.approval.runId,
            choice: request.choice,
            status: "awaiting_snapshot",
          });
          return nextConfirmations;
        });
      }

      await queryClient.invalidateQueries({
        queryKey: ["sessions", request.client.mode, request.backend],
      }).catch(() => undefined);

      try {
        const snapshot = await queryClient.fetchQuery({
          queryKey: [
            "session-snapshot",
            request.client.mode,
            request.backend,
            request.approval.sessionId,
          ],
          queryFn: () => request.client.getSession(request.approval.sessionId),
          staleTime: 0,
        });
        const stillPending = projectPendingApprovals(snapshot).some(
          (candidate) => getApprovalStateKey(candidate) === approvalKey,
        );
        if (useWorkspaceUiStore.getState().backend === request.backend) {
          setApprovalConfirmations((currentConfirmations) => {
            const nextConfirmations = new Map(currentConfirmations);
            if (stillPending) {
              nextConfirmations.set(approvalKey, {
                approvalId: request.approval.request.approvalId,
                sessionId: request.approval.sessionId,
                runId: request.approval.runId,
                choice: request.choice,
                status: "awaiting_snapshot",
              });
            } else {
              nextConfirmations.delete(approvalKey);
            }
            return nextConfirmations;
          });
        }
      } catch {
        if (useWorkspaceUiStore.getState().backend === request.backend) {
          setApprovalConfirmations((currentConfirmations) => {
            const nextConfirmations = new Map(currentConfirmations);
            nextConfirmations.set(approvalKey, {
              approvalId: request.approval.request.approvalId,
              sessionId: request.approval.sessionId,
              runId: request.approval.runId,
              choice: request.choice,
              status: "refresh_error",
            });
            return nextConfirmations;
          });
        }
      }
    },
    onError: (_error, request) => {
      if (useWorkspaceUiStore.getState().backend === request.backend) {
        const approvalKey = getApprovalStateKey(request.approval);
        setApprovalConfirmations((currentConfirmations) => {
          const nextConfirmations = new Map(currentConfirmations);
          nextConfirmations.delete(approvalKey);
          return nextConfirmations;
        });
        setApprovalErrorId(request.approval.request.approvalId);
      }
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
      title: t("app.newTask"),
      project: allSessions[0]?.project ?? t("app.localWorkspace"),
      updatedAt: new Date().toISOString(),
      archived: false,
    };
  const activeSessionIsServiceOwned =
    serviceOwnedSessionKeys.current.has(getSessionScopeKey(backend, activeSession.sessionId)) ||
    serviceSessions.some((session) => session.sessionId === activeSession.sessionId);
  const canReadActiveSession =
    initializeReady &&
    serviceMethods.includes("session/get") &&
    activeSessionIsServiceOwned;
  const sessionSnapshotQuery = useQuery({
    queryKey: [
      "session-snapshot",
      applicationService.mode,
      backend,
      activeSession.sessionId,
    ],
    queryFn: () => applicationService.getSession(activeSession.sessionId),
    enabled: activeView === "conversation" && canReadActiveSession,
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
  const pendingApprovals = useMemo(
    () =>
      sessionSnapshotQuery.data
        ? projectPendingApprovals(sessionSnapshotQuery.data)
        : [],
    [sessionSnapshotQuery.data],
  );
  useEffect(() => {
    if (
      !sessionSnapshotQuery.data ||
      !sessionSnapshotQuery.isSuccess ||
      sessionSnapshotQuery.fetchStatus !== "idle"
    ) {
      return;
    }
    const pendingApprovalKeys = new Set(
      pendingApprovals.map(getApprovalStateKey),
    );
    setApprovalConfirmations((currentConfirmations) => {
      let changed = false;
      const nextConfirmations = new Map(currentConfirmations);
      for (const [approvalKey, confirmation] of currentConfirmations) {
        if (
          confirmation.sessionId === activeSession.sessionId &&
          !pendingApprovalKeys.has(approvalKey)
        ) {
          nextConfirmations.delete(approvalKey);
          changed = true;
        }
      }
      return changed ? nextConfirmations : currentConfirmations;
    });
  }, [activeSession.sessionId, pendingApprovals, sessionSnapshotQuery.data, sessionSnapshotQuery.fetchStatus, sessionSnapshotQuery.isSuccess]);
  const canRespondToApprovals = serviceMethods.includes("approval/respond");
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
  const modelLoadState =
    initializeQuery.isError || (initializeReady && advertisesModels && modelsQuery.isError)
      ? "error"
      : !initializeReady ||
          (advertisesModels &&
            (!modelsQuery.isSuccess || modelsQuery.fetchStatus !== "idle"))
        ? "loading"
        : !advertisesModels
          ? "unsupported"
          : models.length === 0
            ? "empty"
            : "ready";
  const implementationLabel =
    initializeSnapshot?.result.implementation.name ??
    (initializeQuery.isError ? t("app.initializationFailed") : t("app.initializing"));

  /**
   * 重试当前失败的初始化握手或模型快照读取。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleRetryModels = () => {
    if (!initializeReady) {
      void initializeQuery.refetch();
      return;
    }
    void modelsQuery.refetch();
  };

  /**
   * 重新读取已接受审批的 durable Session 快照，而不重复提交决定。
   *
   * 不变量：只有先前已被响应接口接受的请求可以进入此路径；刷新只读 Session。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleRetryApprovalRefresh = async (approval: PendingApproval) => {
    const approvalKey = getApprovalStateKey(approval);
    const confirmation = approvalConfirmations.get(approvalKey);
    if (!confirmation || approvalMutation.isPending) {
      return;
    }
    setApprovalConfirmations((currentConfirmations) => {
      const current = currentConfirmations.get(approvalKey);
      if (!current) {
        return currentConfirmations;
      }
      const nextConfirmations = new Map(currentConfirmations);
      nextConfirmations.set(approvalKey, {
        ...current,
        status: "awaiting_snapshot",
      });
      return nextConfirmations;
    });
    try {
      const snapshot = await queryClient.fetchQuery({
        queryKey: [
          "session-snapshot",
          applicationService.mode,
          backend,
          approval.sessionId,
        ],
        queryFn: () => applicationService.getSession(approval.sessionId),
        staleTime: 0,
      });
      const stillPending = projectPendingApprovals(snapshot).some(
        (candidate) => getApprovalStateKey(candidate) === approvalKey,
      );
      setApprovalConfirmations((currentConfirmations) => {
        const nextConfirmations = new Map(currentConfirmations);
        if (stillPending) {
          const current = currentConfirmations.get(approvalKey);
          if (current) {
            nextConfirmations.set(approvalKey, {
              ...current,
              status: "awaiting_snapshot",
            });
          }
        } else {
          nextConfirmations.delete(approvalKey);
        }
        return nextConfirmations;
      });
    } catch {
      setApprovalConfirmations((currentConfirmations) => {
        const current = currentConfirmations.get(approvalKey);
        if (!current) {
          return currentConfirmations;
        }
        const nextConfirmations = new Map(currentConfirmations);
        nextConfirmations.set(approvalKey, {
          ...current,
          status: "refresh_error",
        });
        return nextConfirmations;
      });
    }
  };

  /**
   * 将审批卡上的有限 choice 提交给生成该请求的同一 application service。
   *
   * 不变量：只接受当前 durable 请求明确提供的 choice，且同一时间只提交一个决定。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleApprovalResponse = (
    approval: PendingApproval,
    choice: ApprovalChoice,
  ) => {
    if (
      approvalMutation.isPending ||
      !canRespondToApprovals ||
      !approval.request.choices.includes(choice) ||
      isApprovalExpired(approval) ||
      approvalConfirmations.has(getApprovalStateKey(approval))
    ) {
      return;
    }
    approvalMutation.mutate({
      client: applicationService,
      backend,
      approval,
      choice,
    });
  };

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
      modelLoadState !== "ready" ||
      !serviceMethods.includes("models/list") ||
      !serviceMethods.includes("run/start") ||
      !findModelByRouteKey(models, getModelRouteKey(selectedModel)) ||
      (activeAgentProfileId !== null && !activeAgentProfile) ||
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
      supportsFollowUp: serviceMethods.includes("run/followUp"),
      supportsSessionGet: serviceMethods.includes("session/get"),
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
        title: t("app.newTask"),
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
   * 选择完整模型路由，并在模型与当前 Agent 默认路由不同时解除 Agent 绑定。
   *
   * 输入：由 Radix Select 返回的三元路由 key。
   * 不变量：只有当前 models/list 中存在的路由可以进入提交状态，不能产生 Profile 模型不匹配。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleComposerModelChange = (modelRouteKey: string) => {
    const model = findModelByRouteKey(models, modelRouteKey);
    if (!model) {
      return;
    }
    const nextModelRouteKey = getModelRouteKey(model);
    setSelectedModelRouteKey(nextModelRouteKey);
    if (activeAgentProfile && nextModelRouteKey !== agentModelRouteKey) {
      setActiveAgentProfileId(null);
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
      setSessionOperationError(t("app.archiveUnsupported"));
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
        setSessionOperationError(t("app.archiveFailed"));
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
      setSessionOperationError(t("app.restoreUnsupported"));
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
        setSessionOperationError(t("app.restoreFailed"));
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
      setSessionOperationError(t("app.deleteUnsupported"));
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
        setSessionOperationError(t("app.deleteFailed"));
      }
    } finally {
      setSessionOperation(session.sessionId, false);
    }
  };

  return (
    <div
      className="flex h-dvh w-full overflow-hidden bg-background text-foreground"
      data-reduce-motion={reduceMotion ? "true" : "false"}
    >
      <aside
        className={`hidden h-full shrink-0 border-r md:block ${
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
          <SheetTitle className="sr-only">{t("app.sidebarTitle")}</SheetTitle>
          <SheetDescription className="sr-only">{t("app.sidebarDescription")}</SheetDescription>
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

      <main className="flex min-w-0 flex-1 flex-col bg-background">
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
            supportedMethodIds={serviceMethods}
            supportedEventTypes={serviceEventTypes}
            loading={!initializeReady && !initializeQuery.isError}
            onOpenAgentTools={handleOpenAgentTools}
            onOpenMobileSidebar={() => setMobileSidebarOpen(true)}
          />
        ) : activeView === "settings" ? (
          <SettingsWorkspace
            backend={backend}
            service={applicationService}
            initializeResult={initializeSnapshot?.result}
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
              connected={initializeReady}
              previewReviewAvailable={applicationService.mode === "faux-preview"}
              onOpenMobileSidebar={() => setMobileSidebarOpen(true)}
              onOpenReview={() => setReviewOpen(true)}
            />
            <Conversation
              backendLabel={backend === "rust" ? "Rust" : ".NET"}
              messages={messages}
              busy={activeSessionBusy}
              historyLoading={sessionSnapshotQuery.isLoading}
              historyError={sessionSnapshotQuery.isError && canReadActiveSession}
              onRetryHistory={() => void sessionSnapshotQuery.refetch()}
              pendingApprovals={pendingApprovals}
              approvalResponseAvailable={canRespondToApprovals}
              approvalInteractionLocked={approvalMutation.isPending}
              approvalSubmission={
                approvalMutation.isPending && approvalMutation.variables
                  ? {
                      approvalId:
                        approvalMutation.variables.approval.request.approvalId,
                      choice: approvalMutation.variables.choice,
                    }
                  : null
              }
              approvalError={
                approvalErrorId
                  ? {
                      approvalId: approvalErrorId,
                      message: t("approval.responseFailed"),
                    }
                  : null
              }
              approvalConfirmations={pendingApprovals.flatMap((approval) => {
                const confirmation = approvalConfirmations.get(
                  getApprovalStateKey(approval),
                );
                return confirmation
                  ? [
                      {
                        approvalId: confirmation.approvalId,
                        runId: confirmation.runId,
                        choice: confirmation.choice,
                        refreshFailed: confirmation.status === "refresh_error",
                      },
                    ]
                  : [];
              })}
              onRespondToApproval={handleApprovalResponse}
              onRetryApprovalRefresh={(approval) =>
                void handleRetryApprovalRefresh(approval)
              }
              showFixture={
                applicationService.mode === "faux-preview" &&
                activeSession.sessionId === "desktop-shell" &&
                previewSessions.every(
                  (session) => session.sessionId !== activeSession.sessionId,
                )
              }
            />
            <Composer
              value={draft}
              selectedModelRouteKey={resolvedModelRouteKey}
              models={models}
              modelLoadState={modelLoadState}
              agentProfiles={selectableAgentProfiles}
              selectedAgentProfileId={activeAgentProfileId}
              runtimeEnvironment={runtimeEnvironmentQuery.data}
              submitMode={composerSubmitMode}
              busy={activeSessionBusy}
              onValueChange={setDraft}
              onModelChange={handleComposerModelChange}
              onRetryModels={handleRetryModels}
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
            <AlertDialogTitle>{t("app.discardTitle")}</AlertDialogTitle>
            <AlertDialogDescription>
              {t("app.discardDescription")}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>{t("app.keepEditing")}</AlertDialogCancel>
            <AlertDialogAction onClick={confirmWorkspaceNavigation}>{t("app.discard")}</AlertDialogAction>
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
            <AlertDialogTitle>{t("app.deleteSessionTitle", { title: pendingSessionDelete?.title })}</AlertDialogTitle>
            <AlertDialogDescription>
              {t("app.deleteSessionDescription")}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>{t("common.cancel")}</AlertDialogCancel>
            <AlertDialogAction
              className="bg-red-600 hover:bg-red-700"
              onClick={() => void confirmDeleteSession()}
            >
              {t("app.permanentlyDelete")}
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
            <AlertDialogTitle>{t("app.operationFailedTitle")}</AlertDialogTitle>
            <AlertDialogDescription>{sessionOperationError}</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogAction onClick={() => setSessionOperationError(null)}>
              {t("app.acknowledge")}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
