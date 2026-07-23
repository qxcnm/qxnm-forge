import { useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";

import { AppHeader } from "@/components/app-header";
import { Composer, type ComposerAttachment } from "@/components/composer";
import {
  Conversation,
  type SubmittedMessage,
  type SubmittedMessageImage,
} from "@/components/conversation";
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
import {
  getApprovalIdentityKey,
  projectPendingApprovals,
} from "@/features/approvals/pending-approvals";
import { PluginWorkspace } from "@/features/plugins/plugin-workspace";
import { ArchiveWorkspace } from "@/features/sessions/archive-workspace";
import { SettingsWorkspace } from "@/features/settings/settings-workspace";
import { useApplicationServiceEvents } from "@/lib/application-service-events";
import { createApplicationServiceClient } from "@/lib/mock-application-service";
import { findModelByRouteKey, getModelRouteKey } from "@/lib/model-route";
import { readNativeClipboardImage } from "@/lib/native-clipboard-image";
import { getRuntimeEnvironment } from "@/lib/runtime-environment";
import i18n from "@/i18n";
import {
  useWorkspaceUiStore,
  type WorkspaceView,
} from "@/store/workspace-ui-store";
import type {
  ApprovalChoice,
  ApplicationServiceClient,
  ArtifactReadResult,
  BackendKind,
  ModelDescriptor,
  PendingApproval,
  RunStartInput,
  SessionArtifactReference,
  SessionSnapshot,
  SessionSummary,
} from "@/types/application-service";

const EMPTY_TOOL_IDS: readonly string[] = [];
const EMPTY_MODELS = [] as const;
const EMPTY_AGENT_PROFILES = [] as const;
const EMPTY_SESSIONS: readonly SessionSummary[] = [];
const EMPTY_MESSAGES: readonly SubmittedMessage[] = [];
const EMPTY_SESSION_IMAGES: ReadonlyMap<string, LoadedSessionImage> = new Map();
const SUPPORTED_IMAGE_MEDIA_TYPES = new Set([
  "image/png",
  "image/jpeg",
  "image/webp",
  "image/gif",
]);
const MAX_SESSION_IMAGE_COUNT = 32;
const MAX_SESSION_IMAGE_BYTES = 64 * 1_024 * 1_024;
const MAX_IMAGE_BASE64_LENGTH = 44_739_244;

interface RunMutationRequest {
  readonly client: ApplicationServiceClient;
  readonly backend: BackendKind;
  readonly input: RunStartInput;
  readonly backendLabel: "Rust" | ".NET";
  readonly agentProfileName?: string;
  readonly supportsFollowUp: boolean;
  readonly supportsSessionGet: boolean;
  readonly submissionId: string;
  readonly acceptedUserMessage: SubmittedMessage;
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

interface ApprovalIdentity {
  readonly approvalId: string;
  readonly sessionId: string;
  readonly runId: string;
}

interface ApprovalConfirmation extends ApprovalIdentity {
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

interface LoadedSessionImage {
  readonly status: "ready" | "error";
  readonly dataUrl?: string;
  readonly retryable?: boolean;
}

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
  return getApprovalIdentityKey(
    approval.sessionId,
    approval.runId,
    approval.request.approvalId,
  );
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
function projectSessionSnapshotMessages(
  snapshot: SessionSnapshot,
  loadedImages: ReadonlyMap<string, LoadedSessionImage> = EMPTY_SESSION_IMAGES,
  artifactReadAvailable = false,
): readonly SubmittedMessage[] {
  const projectedMessages: Array<{
    readonly message: SubmittedMessage;
    readonly timestamp: number;
    readonly order: number;
  }> = snapshot.messages.map((message, index) => {
    const text = message.content
      .filter((content) => content.type === "text")
      .map((content) => content.text)
      .join("\n\n")
      .trim();
    const images: readonly SubmittedMessageImage[] = message.content
      .filter((content) => content.type === "image_ref")
      .map((content) => {
        const loaded = loadedImages.get(getArtifactProjectionKey(content.artifact));
        return {
          artifactId: content.artifact.artifactId,
          alt: content.alt ?? content.artifact.displayName ?? i18n.t("conversation.imageAlt"),
          status: loaded?.status ?? (artifactReadAvailable ? "loading" : "error"),
          ...(loaded?.dataUrl ? { dataUrl: loaded.dataUrl } : {}),
        };
      });
    let projection: SubmittedMessage;
    if (message.role === "user") {
      projection = {
        id: message.messageId,
        role: "user" as const,
        content: text || i18n.t("app.attachmentSent"),
        ...(images.length > 0 ? { images } : {}),
      };
    } else if (message.role === "tool") {
      projection = {
        id: message.messageId,
        role: "status" as const,
        content: text
          ? i18n.t("app.toolResult", { name: message.toolName, text })
          : i18n.t("app.toolCompleted", { name: message.toolName }),
      };
    } else {
      const toolNames = message.content
        .filter((content) => content.type === "tool_call")
        .map((content) => content.name);
      projection = {
        id: message.messageId,
        role: "assistant" as const,
        content:
          text ||
          message.error?.message ||
          (toolNames.length > 0
            ? i18n.t("app.requestedTools", {
                names: toolNames.join(i18n.resolvedLanguage === "en-US" ? ", " : "、"),
              })
            : images.length > 0
              ? i18n.t("app.imageResponse")
              : i18n.t("app.runCompleted")),
        ...(images.length > 0 ? { images } : {}),
      };
    }
    const timestamp = Date.parse(message.time);
    return {
      message: projection,
      timestamp: Number.isFinite(timestamp) ? timestamp : 0,
      order: index,
    };
  });

  const durableMessageIds = new Set(
    snapshot.messages.map((message) => message.messageId),
  );
  const streamedAssistants = new Map<
    string,
    { content: string; timestamp: number; order: number }
  >();
  for (const event of snapshot.events) {
    const eventTimestamp = Date.parse(event.time);
    const timestamp = Number.isFinite(eventTimestamp) ? eventTimestamp : 0;
    if (event.type === "message.started" || event.type === "message.delta") {
      const messageId = event.data.messageId;
      if (typeof messageId !== "string" || durableMessageIds.has(messageId)) {
        continue;
      }
      if (event.type === "message.started") {
        if (event.data.role === "assistant" && !streamedAssistants.has(messageId)) {
          streamedAssistants.set(messageId, { content: "", timestamp, order: event.seq });
        }
        continue;
      }
      const delta = event.data.delta;
      if (typeof delta !== "object" || delta === null || Array.isArray(delta)) {
        continue;
      }
      const deltaRecord = delta as Readonly<Record<string, unknown>>;
      if (deltaRecord.type !== "text" || typeof deltaRecord.text !== "string") {
        continue;
      }
      const current = streamedAssistants.get(messageId) ?? {
        content: "",
        timestamp,
        order: event.seq,
      };
      streamedAssistants.set(messageId, {
        ...current,
        content: `${current.content}${deltaRecord.text}`,
      });
      continue;
    }

    if (
      event.type !== "run.failed" &&
      event.type !== "run.cancelled" &&
      event.type !== "run.interrupted"
    ) {
      continue;
    }
    let translation: NonNullable<SubmittedMessage["translation"]>;
    if (event.type === "run.cancelled") {
      translation = {
        key: "app.runCancelled",
        values: {
          reason: typeof event.data.reason === "string" ? `：${event.data.reason}` : "",
        },
      };
    } else {
      const error =
        typeof event.data.error === "object" &&
        event.data.error !== null &&
        !Array.isArray(event.data.error)
          ? (event.data.error as Readonly<Record<string, unknown>>)
          : undefined;
      const details =
        typeof error?.details === "object" &&
        error.details !== null &&
        !Array.isArray(error.details)
          ? (error.details as Readonly<Record<string, unknown>>)
          : undefined;
      const httpStatus =
        typeof details?.httpStatus === "number" ? `（HTTP ${details.httpStatus}）` : "";
      translation = {
        key: event.type === "run.failed" ? "app.runFailed" : "app.runInterrupted",
        values: {
          message:
            typeof error?.message === "string"
              ? error.message
              : i18n.t("app.unknownRunError"),
          httpStatus,
        },
      };
    }
    projectedMessages.push({
      message: {
        id: `${event.runId}:${event.type}:${event.seq}`,
        role: "status",
        content: "",
        translation,
      },
      timestamp,
      order: snapshot.messages.length + event.seq,
    });
  }

  for (const [messageId, streamed] of streamedAssistants) {
    if (streamed.content.length === 0) {
      continue;
    }
    projectedMessages.push({
      message: { id: messageId, role: "assistant", content: streamed.content },
      timestamp: streamed.timestamp,
      order: snapshot.messages.length + streamed.order,
    });
  }

  return projectedMessages
    .sort((left, right) => left.timestamp - right.timestamp || left.order - right.order)
    .map((projection) => projection.message);
}

/**
 * 为同一 artifact 元数据生成不含路径和正文的前端投影键。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function getArtifactProjectionKey(artifact: SessionArtifactReference): string {
  return [
    artifact.artifactId,
    artifact.mediaType,
    artifact.byteLength.toString(10),
    artifact.sha256,
  ].join("\u0000");
}

/**
 * 从完整 Session 消息中收集唯一图片引用，不读取普通 artifact 或宿主路径。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function collectSessionImageReferences(
  snapshot: SessionSnapshot | undefined,
): readonly SessionArtifactReference[] {
  const references = new Map<string, SessionArtifactReference>();
  const messages = snapshot?.messages ?? [];
  for (let messageIndex = 0; messageIndex < messages.length; messageIndex += 1) {
    const message = messages[messageIndex];
    if (!message) {
      continue;
    }
    for (let contentIndex = 0; contentIndex < message.content.length; contentIndex += 1) {
      const content = message.content[contentIndex];
      if (content.type === "image_ref") {
        const key = getArtifactProjectionKey(content.artifact);
        if (!references.has(key)) {
          references.set(key, content.artifact);
        }
      }
    }
  }
  return [...references.values()];
}

/**
 * 校验 artifact 回读字节并构造仅含受支持图片 MIME 的 data URL。
 *
 * 输入：快照中的不可变引用与 application service 的严格回读结果。
 * 输出：元数据、长度、签名和摘要全部匹配时的 data URL。
 * 失败：任何身份、Base64、MIME、大小、签名或摘要差异均拒绝。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
async function createVerifiedImageDataUrl(
  reference: SessionArtifactReference,
  result: ArtifactReadResult,
): Promise<string> {
  if (
    result.artifact.artifactId !== reference.artifactId ||
    result.artifact.mediaType !== reference.mediaType ||
    result.artifact.byteLength !== reference.byteLength ||
    result.artifact.sha256 !== reference.sha256 ||
    result.dataBase64.length > MAX_IMAGE_BASE64_LENGTH ||
    !SUPPORTED_IMAGE_MEDIA_TYPES.has(reference.mediaType) ||
    !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(
      result.dataBase64,
    )
  ) {
    throw new Error("Artifact image response does not match the Session reference");
  }
  let binary: string;
  let bytes: Uint8Array;
  try {
    binary = atob(result.dataBase64);
    bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
  } catch {
    throw new Error("Artifact image response is not valid Base64");
  }
  if (
    bytes.length === 0 ||
    bytes.length > 32 * 1_024 * 1_024 ||
    bytes.length !== reference.byteLength ||
    btoa(binary) !== result.dataBase64
  ) {
    throw new Error("Artifact image response violates the byte boundary");
  }
  const prefix = String.fromCharCode(...bytes.slice(0, 12));
  const signatureMatches =
    (reference.mediaType === "image/png" &&
      [137, 80, 78, 71, 13, 10, 26, 10].every(
        (value, index) => bytes[index] === value,
      )) ||
    (reference.mediaType === "image/jpeg" &&
      bytes[0] === 0xff &&
      bytes[1] === 0xd8 &&
      bytes[2] === 0xff) ||
    (reference.mediaType === "image/gif" &&
      (prefix.startsWith("GIF87a") || prefix.startsWith("GIF89a"))) ||
    (reference.mediaType === "image/webp" &&
      prefix.startsWith("RIFF") &&
      prefix.slice(8) === "WEBP");
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
  const sha256 = [...digest]
    .map((value) => value.toString(16).padStart(2, "0"))
    .join("");
  if (!signatureMatches || sha256 !== reference.sha256) {
    throw new Error("Artifact image response failed integrity validation");
  }
  return `data:${reference.mediaType};base64,${result.dataBase64}`;
}

/**
 * 逐张读取 Session 图片；单张失败收敛为占位状态，不阻断其他消息或文本。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
async function loadSessionImages(
  client: ApplicationServiceClient,
  sessionId: string,
  references: readonly SessionArtifactReference[],
): Promise<ReadonlyMap<string, LoadedSessionImage>> {
  const images = new Map<string, LoadedSessionImage>();
  const eligibleReferences: SessionArtifactReference[] = [];
  let remainingBytes = MAX_SESSION_IMAGE_BYTES;
  for (let index = references.length - 1; index >= 0; index -= 1) {
    const reference = references[index];
    if (!reference) {
      continue;
    }
    const key = getArtifactProjectionKey(reference);
    images.set(key, { status: "error" });
    if (
      eligibleReferences.length >= MAX_SESSION_IMAGE_COUNT ||
      reference.byteLength <= 0 ||
      reference.byteLength > 32 * 1_024 * 1_024 ||
      reference.byteLength > remainingBytes
    ) {
      continue;
    }
    eligibleReferences.push(reference);
    remainingBytes -= reference.byteLength;
  }
  eligibleReferences.reverse();
  for (let index = 0; index < eligibleReferences.length; index += 2) {
    const batch = await Promise.all(
      eligibleReferences.slice(index, index + 2).map(async (reference) => {
        let result: ArtifactReadResult;
        try {
          result = await client.readArtifact({
            sessionId,
            artifactId: reference.artifactId,
          });
        } catch {
          return {
            key: getArtifactProjectionKey(reference),
            image: { status: "error", retryable: true } as const,
          };
        }
        try {
          return {
            key: getArtifactProjectionKey(reference),
            image: {
              status: "ready",
              dataUrl: await createVerifiedImageDataUrl(reference, result),
            } as const,
          };
        } catch {
          return {
            key: getArtifactProjectionKey(reference),
            image: { status: "error" } as const,
          };
        }
      }),
    );
    for (const entry of batch) {
      images.set(entry.key, entry.image);
    }
  }
  return images;
}

/**
 * 判断提交完成时附件是否仍是原始 ID 快照，避免清掉等待期间的新附件。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function haveSameAttachmentIds(
  left: readonly ComposerAttachment[],
  right: readonly ComposerAttachment[],
): boolean {
  return left.length === right.length &&
    left.every((attachment, index) => attachment.id === right[index]?.id);
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
  const [composerAttachments, setComposerAttachments] = useState<readonly ComposerAttachment[]>([]);
  const [composerSubmissionError, setComposerSubmissionError] = useState<string | null>(null);
  const [selectedModelRouteKey, setSelectedModelRouteKey] = useState("");
  const [messagesBySession, setMessagesBySession] = useState<SessionMessages>(new Map());
  const [previewSessions, setPreviewSessions] = useState<readonly SessionSummary[]>([]);
  const [busySessionKeys, setBusySessionKeys] = useState<ReadonlySet<string>>(new Set());
  const [sessionOperationKeys, setSessionOperationKeys] = useState<ReadonlySet<string>>(new Set());
  const [pendingSessionDelete, setPendingSessionDelete] = useState<SessionSummary | null>(null);
  const [sessionOperationError, setSessionOperationError] = useState<string | null>(null);
  const [approvalError, setApprovalError] = useState<ApprovalIdentity | null>(null);
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
    setComposerAttachments([]);
    setComposerSubmissionError(null);
    setSelectedModelRouteKey("");
    setActiveAgentProfileId(null);
    setPreviewSessions([]);
    setPendingSessionDelete(null);
    setSessionOperationError(null);
    setApprovalError(null);
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
      if (request.client.mode === "faux-preview") {
        appendSessionMessage(
          request.backend,
          request.input.sessionId,
          request.acceptedUserMessage,
        );
        const translation: NonNullable<SubmittedMessage["translation"]> = request.agentProfileName
          ? {
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
      }
      if (request.client.mode === "application-service") {
        serviceOwnedSessionKeys.current.add(
          getSessionScopeKey(request.backend, request.input.sessionId),
        );
        let durableSnapshotLoaded = false;
        const refreshSessionList = queryClient
          .invalidateQueries({
            queryKey: ["sessions", request.client.mode, request.backend],
          })
          .catch(() => undefined);
        const refreshSessionSnapshot = request.supportsSessionGet
          ? queryClient
              .fetchQuery({
                queryKey: [
                  "session-snapshot",
                  request.client.mode,
                  request.backend,
                  request.input.sessionId,
                ],
                queryFn: () => request.client.getSession(request.input.sessionId),
              })
              .then(() => {
                durableSnapshotLoaded = true;
              })
              .catch(() => undefined)
          : Promise.resolve();
        await Promise.all([refreshSessionList, refreshSessionSnapshot]);
        if (!durableSnapshotLoaded) {
          appendSessionMessage(
            request.backend,
            request.input.sessionId,
            request.acceptedUserMessage,
          );
        }
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
      setApprovalError(null);
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
        setApprovalError(null);
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
        setApprovalError({
          approvalId: request.approval.request.approvalId,
          sessionId: request.approval.sessionId,
          runId: request.approval.runId,
        });
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
  const sessionImageReferences = useMemo(
    () => collectSessionImageReferences(sessionSnapshotQuery.data),
    [sessionSnapshotQuery.data],
  );
  const artifactReadAvailable = serviceMethods.includes("artifacts/read");
  const sessionImagesQuery = useQuery({
    queryKey: [
      "session-image-artifacts",
      applicationService.mode,
      backend,
      activeSession.sessionId,
      ...sessionImageReferences.map(getArtifactProjectionKey),
    ],
    queryFn: () =>
      loadSessionImages(
        applicationService,
        activeSession.sessionId,
        sessionImageReferences,
      ),
    enabled:
      activeView === "conversation" &&
      artifactReadAvailable &&
      sessionImageReferences.length > 0,
    staleTime: 15_000,
    refetchInterval: (query) =>
      query.state.data && [...query.state.data.values()].some((image) => image.retryable)
        ? 3_000
        : false,
  });
  const localMessages =
    messagesBySession.get(getSessionScopeKey(backend, activeSession.sessionId)) ?? [];
  const snapshotMessages = sessionSnapshotQuery.data
    ? projectSessionSnapshotMessages(
        sessionSnapshotQuery.data,
        sessionImagesQuery.data ?? EMPTY_SESSION_IMAGES,
        artifactReadAvailable,
      )
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
  const approvalSnapshotReady =
    sessionSnapshotQuery.isSuccess && sessionSnapshotQuery.fetchStatus === "idle";
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
      !approvalSnapshotReady ||
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
   * 将用户消息提交到当前后端客户端，并仅在 run/start 真正接受后清空输入。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleSubmit = async () => {
    const submittedDraft = draft;
    const prompt = submittedDraft.trim();
    const pendingAttachments = composerAttachments;
    const hasImageAttachments = pendingAttachments.some(
      (attachment) => attachment.kind === "image",
    );
    const sessionScopeKey = getSessionScopeKey(backend, activeSession.sessionId);
    if (
      (prompt.length === 0 && pendingAttachments.length === 0) ||
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
    if (hasImageAttachments && !selectedModel.supportsImageInput) {
      setComposerSubmissionError(t("composer.modelImageInputUnsupported"));
      return;
    }
    if (hasImageAttachments && !serviceMethods.includes("artifacts/create")) {
      setComposerSubmissionError(t("composer.attachmentUploadUnsupported"));
      return;
    }

    const submissionId = crypto.randomUUID();
    activeRunSubmissions.current.set(sessionScopeKey, submissionId);
    setBusySessionKeys((currentSessionKeys) => new Set(currentSessionKeys).add(sessionScopeKey));
    setComposerSubmissionError(null);
    let runSubmitted = false;
    try {
      const runAttachments: NonNullable<RunStartInput["attachments"]>[number][] = [];
      const submittedImages: SubmittedMessageImage[] = [];
      for (const attachment of pendingAttachments) {
        if (attachment.kind === "text") {
          runAttachments.push({
            type: "text",
            text: `[File: ${attachment.name}]\n${attachment.text}`,
          });
        } else {
          const created = await applicationService.createArtifact({
            sessionId: activeSession.sessionId,
            mediaType: attachment.mediaType,
            dataBase64: attachment.dataBase64,
          });
          runAttachments.push({
            type: "image_ref",
            artifact: created.artifact,
            alt: attachment.name,
          });
          submittedImages.push({
            artifactId: created.artifact.artifactId,
            alt: attachment.name,
            status: "ready",
            dataUrl: `data:${attachment.mediaType};base64,${attachment.dataBase64}`,
          });
        }
      }
      const attachmentLabels = pendingAttachments.map((attachment) =>
        attachment.kind === "image"
          ? `[Image: ${attachment.name}]`
          : `[File: ${attachment.name}]`,
      );
      runSubmitted = true;
      await runMutation.mutateAsync({
        client: applicationService,
        backend,
        backendLabel: backend === "rust" ? "Rust" : ".NET",
        agentProfileName: activeAgentProfile?.displayName,
        supportsFollowUp: serviceMethods.includes("run/followUp"),
        supportsSessionGet: serviceMethods.includes("session/get"),
        submissionId,
        acceptedUserMessage: {
          id: crypto.randomUUID(),
          role: "user",
          content: [prompt, ...attachmentLabels].filter(Boolean).join("\n"),
          ...(submittedImages.length > 0 ? { images: submittedImages } : {}),
        },
        input: {
          sessionId: activeSession.sessionId,
          prompt,
          attachments: runAttachments,
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
      const currentWorkspace = useWorkspaceUiStore.getState();
      if (
        currentWorkspace.backend === backend &&
        currentWorkspace.activeSessionId === activeSession.sessionId
      ) {
        setDraft((currentDraft) => currentDraft === submittedDraft ? "" : currentDraft);
        setComposerAttachments((currentAttachments) =>
          haveSameAttachmentIds(currentAttachments, pendingAttachments)
            ? []
            : currentAttachments,
        );
        setComposerSubmissionError(null);
      }
    } catch {
      if (!runSubmitted) {
        activeRunSubmissions.current.delete(sessionScopeKey);
        setBusySessionKeys((currentSessionKeys) => {
          const nextSessionKeys = new Set(currentSessionKeys);
          nextSessionKeys.delete(sessionScopeKey);
          return nextSessionKeys;
        });
      }
      const currentWorkspace = useWorkspaceUiStore.getState();
      if (
        currentWorkspace.backend === backend &&
        currentWorkspace.activeSessionId === activeSession.sessionId
      ) {
        setComposerSubmissionError(
          t(runSubmitted ? "composer.runSubmissionFailed" : "composer.attachmentUploadFailed"),
        );
      }
    }
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
    setComposerAttachments([]);
    setComposerSubmissionError(null);
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
   * 采用 Provider 发现流程刚写入 models/list 的首个完整模型路由。
   *
   * 输入：发起发现的后端及结果中的 Provider、模型与 API family；输出：下一次输入器选择。
   * 不变量：迟到的非当前后端结果不得修改选择；不会把显示名当作路由，也不会保留模型不匹配的 Agent 绑定。
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleProviderModelReady = (
    sourceBackend: BackendKind,
    model: Pick<ModelDescriptor, "providerId" | "modelId" | "apiFamily">,
  ) => {
    if (useWorkspaceUiStore.getState().backend !== sourceBackend) {
      return;
    }
    setActiveAgentProfileId(null);
    setSelectedModelRouteKey(getModelRouteKey(model));
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
      setDraft("");
      setComposerAttachments([]);
      setComposerSubmissionError(null);
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
            onModelReady={handleProviderModelReady}
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
              approvalSnapshotReady={approvalSnapshotReady}
              approvalInteractionLocked={approvalMutation.isPending}
              approvalSubmission={
                approvalMutation.isPending && approvalMutation.variables
                  ? {
                      sessionId: approvalMutation.variables.approval.sessionId,
                      runId: approvalMutation.variables.approval.runId,
                      approvalId:
                        approvalMutation.variables.approval.request.approvalId,
                      choice: approvalMutation.variables.choice,
                    }
                  : null
              }
              approvalError={
                approvalError
                  ? {
                      ...approvalError,
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
                        sessionId: confirmation.sessionId,
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
              attachments={composerAttachments}
              submissionError={composerSubmissionError}
              selectedModelRouteKey={resolvedModelRouteKey}
              models={models}
              modelLoadState={modelLoadState}
              agentProfiles={selectableAgentProfiles}
              selectedAgentProfileId={activeAgentProfileId}
              runtimeEnvironment={runtimeEnvironmentQuery.data}
              readNativeClipboardImage={
                runtimeEnvironmentQuery.data?.mode === "desktop-local"
                  ? readNativeClipboardImage
                  : undefined
              }
              submitMode={composerSubmitMode}
              busy={activeSessionBusy}
              onValueChange={(value) => {
                setDraft(value);
                setComposerSubmissionError(null);
              }}
              onModelChange={handleComposerModelChange}
              onRetryModels={handleRetryModels}
              onAgentChange={handleAgentChange}
              onAttachmentsChange={(attachments) => {
                setComposerAttachments(attachments);
                setComposerSubmissionError(null);
              }}
              onSubmit={() => void handleSubmit()}
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
