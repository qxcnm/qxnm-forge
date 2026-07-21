import { useMemo, useRef, useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";

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
import { SESSION_FIXTURES, type SessionFixture } from "@/data/workspace-fixtures";
import {
  AgentWorkspace,
  type AgentWorkspaceNavigationState,
} from "@/features/agents/agent-workspace";
import { createApplicationServiceClient } from "@/lib/mock-application-service";
import { findModelByRouteKey, getModelRouteKey } from "@/lib/model-route";
import { getRuntimeEnvironment } from "@/lib/runtime-environment";
import { useWorkspaceUiStore } from "@/store/workspace-ui-store";
import type {
  ApplicationServiceClient,
  RunStartInput,
} from "@/types/application-service";

const EMPTY_TOOL_IDS: readonly string[] = [];

interface RunMutationRequest {
  readonly client: ApplicationServiceClient;
  readonly input: RunStartInput;
  readonly backendLabel: "Rust" | ".NET";
  readonly agentProfileName?: string;
  readonly supportsFollowUp: boolean;
  readonly submissionId: string;
}

type PendingWorkspaceNavigation =
  | { readonly kind: "create" }
  | { readonly kind: "session"; readonly sessionId: string };

type SessionMessages = ReadonlyMap<string, readonly SubmittedMessage[]>;

const CLEAN_AGENT_NAVIGATION_STATE: AgentWorkspaceNavigationState = {
  dirty: false,
  locked: false,
};

/**
 * 组合 QXNM Forge 的桌面与移动响应式 Agent 工作台。
 *
 * 不变量：组件只调用中立 application service client，不读取宿主持久化数据。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export default function App() {
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

  const [draft, setDraft] = useState("");
  const [selectedModelRouteKey, setSelectedModelRouteKey] = useState("");
  const [messagesBySession, setMessagesBySession] = useState<SessionMessages>(new Map());
  const [previewSessions, setPreviewSessions] = useState<readonly SessionFixture[]>([]);
  const [busySessionIds, setBusySessionIds] = useState<ReadonlySet<string>>(new Set());
  const [agentNavigationState, setAgentNavigationState] =
    useState<AgentWorkspaceNavigationState>(CLEAN_AGENT_NAVIGATION_STATE);
  const [pendingWorkspaceNavigation, setPendingWorkspaceNavigation] =
    useState<PendingWorkspaceNavigation | null>(null);
  const activeRunSubmissions = useRef(new Map<string, string>());

  const applicationService = useMemo(() => createApplicationServiceClient(backend), [backend]);

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
  const appendSessionMessage = (sessionId: string, message: SubmittedMessage) => {
    setMessagesBySession((currentMessages) => {
      const nextMessages = new Map(currentMessages);
      nextMessages.set(sessionId, [...(currentMessages.get(sessionId) ?? []), message]);
      return nextMessages;
    });
  };

  const runMutation = useMutation({
    mutationFn: (request: RunMutationRequest) => request.client.startRun(request.input),
    onSuccess: (result, request) => {
      appendSessionMessage(request.input.sessionId, {
        id: result.runId,
        role: "assistant",
        content: request.agentProfileName
          ? request.client.mode === "application-service"
            ? `运行已由 ${request.backendLabel} application service 接受，并绑定智能体“${request.agentProfileName}”的精确 revision。`
            : `运行已由 ${request.backendLabel} capability 画像接受。当前选择“${request.agentProfileName}”内存预览。`
          : request.supportsFollowUp
            ? `运行已由 ${request.backendLabel} capability 画像接受。此连接广告了 follow-up；正式连接后仍由服务事件重建进度和结果。`
            : `运行已由 ${request.backendLabel} capability 画像接受。正式连接后仍由服务事件重建进度和结果。`,
      });
    },
    onError: (_error, request) => {
      appendSessionMessage(request.input.sessionId, {
        id: crypto.randomUUID(),
        role: "status",
        content: "运行请求未被服务接受。",
      });
    },
    onSettled: (_result, _error, request) => {
      if (activeRunSubmissions.current.get(request.input.sessionId) !== request.submissionId) {
        return;
      }
      activeRunSubmissions.current.delete(request.input.sessionId);
      setBusySessionIds((currentSessionIds) => {
        const nextSessionIds = new Set(currentSessionIds);
        nextSessionIds.delete(request.input.sessionId);
        return nextSessionIds;
      });
    },
  });

  const allSessions = useMemo(
    () => [...previewSessions, ...SESSION_FIXTURES],
    [previewSessions],
  );
  const activeSession =
    allSessions.find((session) => session.id === activeSessionId) ?? {
      id: activeSessionId,
      title: "新任务",
      project: "AI-Code",
      age: "刚刚",
    };
  const messages = messagesBySession.get(activeSession.id) ?? [];
  const activeSessionBusy = busySessionIds.has(activeSession.id);
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
    if (
      prompt.length === 0 ||
      !selectedModel ||
      activeRunSubmissions.current.has(activeSession.id)
    ) {
      return;
    }

    const submissionId = crypto.randomUUID();
    activeRunSubmissions.current.set(activeSession.id, submissionId);
    setBusySessionIds((currentSessionIds) => new Set(currentSessionIds).add(activeSession.id));
    appendSessionMessage(activeSession.id, {
      id: crypto.randomUUID(),
      role: "user",
      content: prompt,
    });
    setDraft("");
    runMutation.mutate({
      client: applicationService,
      backendLabel: backend === "rust" ? "Rust" : ".NET",
      agentProfileName: activeAgentProfile?.displayName,
      supportsFollowUp:
        initializeQuery.data?.capabilities.methods.includes("run/followUp") ?? false,
      submissionId,
      input: {
        sessionId: activeSession.id,
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
    const sessionId =
      navigation.kind === "create"
        ? `preview-${crypto.randomUUID()}`
        : navigation.sessionId;
    if (navigation.kind === "create") {
      const session: SessionFixture = {
        id: sessionId,
        title: "新任务",
        project: "AI-Code",
        age: "刚刚",
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
   * 打开智能体管理视图并收起移动导航。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleOpenAgents = () => {
    setActiveView("agents");
    setMobileSidebarOpen(false);
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

  return (
    <div className="flex h-dvh w-full overflow-hidden bg-white text-stone-900">
      <aside className="hidden h-full w-[300px] shrink-0 border-r border-stone-200/70 md:block">
        <WorkspaceSidebar
          onCreateSession={handleCreateSession}
          onOpenAgents={handleOpenAgents}
          onSelectSession={handleSelectSession}
          navigationDisabled={activeView === "agents" && agentNavigationState.locked}
          sessions={allSessions}
        />
      </aside>

      <Sheet open={mobileSidebarOpen} onOpenChange={setMobileSidebarOpen}>
        <SheetContent side="left" className="w-[300px] max-w-[88vw] p-0 [&>button]:hidden">
          <SheetTitle className="sr-only">项目导航</SheetTitle>
          <SheetDescription className="sr-only">选择工作区和最近任务</SheetDescription>
          <WorkspaceSidebar
            onCreateSession={handleCreateSession}
            onOpenAgents={handleOpenAgents}
            onSelectSession={handleSelectSession}
            navigationDisabled={activeView === "agents" && agentNavigationState.locked}
            onRequestClose={() => setMobileSidebarOpen(false)}
            sessions={allSessions}
          />
        </SheetContent>
      </Sheet>

      <main className="flex min-w-0 flex-1 flex-col bg-white">
        {activeView === "agents" ? (
          <AgentWorkspace
            key={`${backend}:${toolCapabilitySignature}`}
            service={applicationService}
            profiles={agentProfiles}
            profilesLoading={agentProfilesQuery.isLoading}
            profileQueryKey={profileQueryKey}
            models={models}
            supportedToolIds={supportedToolIds}
            onOpenMobileSidebar={() => setMobileSidebarOpen(true)}
            onNavigationStateChange={setAgentNavigationState}
          />
        ) : (
          <>
            <AppHeader
              title={activeSession.title}
              implementationLabel={implementationLabel}
              connected={initializeQuery.isSuccess}
              onOpenMobileSidebar={() => setMobileSidebarOpen(true)}
              onOpenReview={() => setReviewOpen(true)}
            />
            <Conversation
              backendLabel={backend === "rust" ? "Rust" : ".NET"}
              messages={messages}
              busy={activeSessionBusy}
              showFixture={previewSessions.every((session) => session.id !== activeSession.id)}
            />
            <Composer
              value={draft}
              selectedModelRouteKey={resolvedModelRouteKey}
              models={models}
              agentProfiles={selectableAgentProfiles}
              selectedAgentProfileId={activeAgentProfile?.profileId ?? null}
              runtimeEnvironment={runtimeEnvironmentQuery.data}
              busy={activeSessionBusy}
              onValueChange={setDraft}
              onModelChange={setSelectedModelRouteKey}
              onAgentChange={handleAgentChange}
              onSubmit={handleSubmit}
            />
          </>
        )}
      </main>

      <ReviewSheet open={reviewOpen} onOpenChange={setReviewOpen} />
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
    </div>
  );
}
