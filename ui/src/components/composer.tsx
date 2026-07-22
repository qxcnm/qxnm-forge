import { useRef, useState, type ChangeEvent, type ClipboardEvent, type FormEvent, type KeyboardEvent } from "react";
import {
  ArrowUp,
  Bot,
  GitBranch,
  LoaderCircle,
  LockKeyhole,
  Paperclip,
  RefreshCw,
  Server,
  X,
} from "lucide-react";
import { useTranslation } from "react-i18next";

import { BackendSwitcher } from "@/components/backend-switcher";
import { Button } from "@/components/ui/button";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { getModelRouteKey } from "@/lib/model-route";
import type { ComposerSubmitMode } from "@/store/workspace-ui-store";
import type { ModelDescriptor, RuntimeEnvironment } from "@/types/application-service";
import type { AgentProfile } from "@/types/agent-profile";

const MAX_INPUT_IMAGE_BYTES = 524_288;
const MAX_SOURCE_IMAGE_BYTES = 20 * 1_024 * 1_024;
const MAX_COMPRESSED_IMAGE_EDGE = 2_048;
const SUPPORTED_IMAGE_MEDIA_TYPES = [
  "image/png",
  "image/jpeg",
  "image/webp",
  "image/gif",
] as const;

type SupportedImageMediaType = (typeof SUPPORTED_IMAGE_MEDIA_TYPES)[number];

interface PreparedImage {
  readonly bytes: Uint8Array;
  readonly mediaType: SupportedImageMediaType;
  readonly name: string;
}

/**
 * 判断 MIME 是否属于协议允许的输入图片类型。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function isSupportedImageMediaType(value: string): value is SupportedImageMediaType {
  return SUPPORTED_IMAGE_MEDIA_TYPES.some((mediaType) => mediaType === value);
}

/**
 * 从标准 files 与 WebKitGTK 常用的 items 两条剪贴板路径收集文件并去重。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function collectClipboardFiles(clipboardData: DataTransfer): File[] {
  const files: File[] = [];
  const seen = new Set<string>();
  const append = (file: File, fallbackMediaType = "") => {
    const mediaType = file.type || fallbackMediaType;
    const signature = `${file.name}\0${mediaType}\0${file.size}\0${file.lastModified}`;
    if (seen.has(signature)) {
      return;
    }
    seen.add(signature);
    if (file.type || !isSupportedImageMediaType(mediaType)) {
      files.push(file);
      return;
    }
    const extension = mediaType === "image/jpeg" ? "jpg" : mediaType.slice("image/".length);
    files.push(new File([file], file.name || `pasted.${extension}`, {
      type: mediaType,
      lastModified: file.lastModified,
    }));
  };

  const clipboardFiles = clipboardData.files;
  for (let index = 0; index < (clipboardFiles?.length ?? 0); index += 1) {
    const file = clipboardFiles[index];
    if (file) {
      append(file);
    }
  }
  const clipboardItems = clipboardData.items;
  for (let index = 0; index < (clipboardItems?.length ?? 0); index += 1) {
    const item = clipboardItems[index];
    if (!item) {
      continue;
    }
    if (item.kind !== "file") {
      continue;
    }
    const file = item.getAsFile();
    if (file) {
      append(file, item.type);
    }
  }
  return files;
}

/**
 * 将画布编码为 JPEG Blob；浏览器编码失败时拒绝 Promise。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function canvasToJpegBlob(canvas: HTMLCanvasElement, quality: number): Promise<Blob> {
  return new Promise((resolve, reject) => {
    canvas.toBlob((blob) => {
      if (blob) {
        resolve(blob);
      } else {
        reject(new Error("unsupported"));
      }
    }, "image/jpeg", quality);
  });
}

/**
 * 解码超过协议上限的静态图片并压缩到 512 KiB 内。
 *
 * 输入：受支持且不超过 20 MiB 的浏览器 File。
 * 输出：可直接发布为 artifact 的 JPEG 字节；保持宽高比且最长边不超过 2048 像素。
 * 不变量：返回字节非空且不超过协议图片上限。
 * 失败条件：图片无法解码、画布不可用，或多轮降质缩放后仍超过上限。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
async function compressImage(file: File): Promise<PreparedImage> {
  if (file.size === 0 || file.size > MAX_SOURCE_IMAGE_BYTES || file.type === "image/gif") {
    throw new Error("too-large");
  }
  const bitmap = await createImageBitmap(file);
  try {
    if (bitmap.width === 0 || bitmap.height === 0) {
      throw new Error("unsupported");
    }
    const initialScale = Math.min(
      1,
      MAX_COMPRESSED_IMAGE_EDGE / Math.max(bitmap.width, bitmap.height),
    );
    let width = Math.max(1, Math.round(bitmap.width * initialScale));
    let height = Math.max(1, Math.round(bitmap.height * initialScale));
    const canvas = document.createElement("canvas");
    const context = canvas.getContext("2d", { alpha: false });
    if (!context) {
      throw new Error("unsupported");
    }
    const qualities = [0.88, 0.78, 0.68, 0.58, 0.48];
    for (let scaleAttempt = 0; scaleAttempt < 5; scaleAttempt += 1) {
      canvas.width = width;
      canvas.height = height;
      context.fillStyle = "#ffffff";
      context.fillRect(0, 0, width, height);
      context.drawImage(bitmap, 0, 0, width, height);
      for (const quality of qualities) {
        const blob = await canvasToJpegBlob(canvas, quality);
        if (blob.size > 0 && blob.size <= MAX_INPUT_IMAGE_BYTES) {
          const bytes = new Uint8Array(await blob.arrayBuffer());
          const baseName = file.name.replace(/\.[^.]+$/, "") || "pasted";
          return { bytes, mediaType: "image/jpeg", name: `${baseName}.jpg` };
        }
      }
      width = Math.max(1, Math.round(width * 0.8));
      height = Math.max(1, Math.round(height * 0.8));
    }
    throw new Error("too-large");
  } finally {
    bitmap.close();
  }
}

export type ComposerModelLoadState =
  | "loading"
  | "error"
  | "empty"
  | "unsupported"
  | "ready";

export type ComposerAttachment =
  | {
      readonly id: string;
      readonly kind: "image";
      readonly name: string;
      readonly mediaType: "image/png" | "image/jpeg" | "image/webp" | "image/gif";
      readonly byteLength: number;
      readonly dataBase64: string;
    }
  | {
      readonly id: string;
      readonly kind: "text";
      readonly name: string;
      readonly mediaType: string;
      readonly byteLength: number;
      readonly text: string;
    };

interface ComposerProps {
  readonly value: string;
  readonly selectedModelRouteKey: string;
  readonly models: readonly ModelDescriptor[];
  readonly modelLoadState: ComposerModelLoadState;
  readonly agentProfiles: readonly AgentProfile[];
  readonly selectedAgentProfileId: string | null;
  readonly runtimeEnvironment?: RuntimeEnvironment;
  readonly submitMode: ComposerSubmitMode;
  readonly busy: boolean;
  readonly attachments: readonly ComposerAttachment[];
  readonly submissionError?: string | null;
  readonly onValueChange: (value: string) => void;
  readonly onModelChange: (modelRouteKey: string) => void;
  readonly onRetryModels: () => void;
  readonly onAgentChange: (profileId: string | null) => void;
  readonly onAttachmentsChange: (attachments: readonly ComposerAttachment[]) => void;
  readonly onSubmit: () => void;
}

/**
 * 提供消息输入、模型选择和后端选择的主任务入口。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function Composer({
  value,
  selectedModelRouteKey,
  models,
  modelLoadState,
  agentProfiles,
  selectedAgentProfileId,
  runtimeEnvironment,
  submitMode,
  busy,
  attachments,
  submissionError,
  onValueChange,
  onModelChange,
  onRetryModels,
  onAgentChange,
  onAttachmentsChange,
  onSubmit,
}: ComposerProps) {
  const { t } = useTranslation();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [attachmentError, setAttachmentError] = useState<string | null>(null);
  const selectedAgent = agentProfiles.find(
    (profile) => profile.profileId === selectedAgentProfileId,
  );
  const modelStatusLabel =
    modelLoadState === "error"
      ? t("composer.modelLoadFailed")
      : modelLoadState === "empty"
        ? t("composer.noModels")
        : modelLoadState === "unsupported"
          ? t("composer.modelsUnsupported")
          : t("composer.loadingModels");
  /**
   * 提交非空消息并阻止浏览器刷新。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleSubmit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!busy && (value.trim().length > 0 || attachments.length > 0)) {
      onSubmit();
    }
  };

  /**
   * 按当前界面偏好处理 Enter 或组合键发送。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    const shouldSubmit =
      event.key === "Enter" &&
      (submitMode === "enter"
        ? !event.shiftKey && !event.metaKey && !event.ctrlKey
        : (event.metaKey || event.ctrlKey) && !event.shiftKey);
    if (shouldSubmit) {
      event.preventDefault();
      if (!busy && (value.trim().length > 0 || attachments.length > 0)) {
        onSubmit();
      }
    }
  };

  /**
   * 读取文件选择或剪贴板附件，严格限制类型、数量、大小与 UTF-8 文本。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const addFiles = async (files: readonly File[]) => {
    setAttachmentError(null);
    if (attachments.length + files.length > 8) {
      setAttachmentError(t("composer.attachmentTooMany"));
      return;
    }
    const added: ComposerAttachment[] = [];
    try {
      for (const file of files) {
        const buffer = await file.arrayBuffer();
        if (isSupportedImageMediaType(file.type)) {
          if (buffer.byteLength === 0 || buffer.byteLength > MAX_SOURCE_IMAGE_BYTES) {
            throw new Error("too-large");
          }
          const prepared = buffer.byteLength <= MAX_INPUT_IMAGE_BYTES
            ? { bytes: new Uint8Array(buffer), mediaType: file.type, name: file.name }
            : await compressImage(file);
          const { bytes } = prepared;
          let binary = "";
          for (let offset = 0; offset < bytes.length; offset += 8_192) {
            binary += String.fromCharCode(...bytes.subarray(offset, offset + 8_192));
          }
          added.push({
            id: crypto.randomUUID(),
            kind: "image",
            name: prepared.name || t("composer.pastedImage"),
            mediaType: prepared.mediaType,
            byteLength: bytes.length,
            dataBase64: btoa(binary),
          });
          continue;
        }
        const isText = file.type.startsWith("text/") ||
          /\.(?:txt|md|markdown|json|jsonl|csv|tsv|xml|ya?ml|toml|log|rs|cs|ts|tsx|js|jsx|css|html|sql)$/i.test(file.name);
        if (!isText) {
          throw new Error("unsupported");
        }
        if (buffer.byteLength === 0 || buffer.byteLength > 262_144) {
          throw new Error("too-large");
        }
        const text = new TextDecoder("utf-8", { fatal: true }).decode(buffer);
        if (text.includes("\0")) {
          throw new Error("unsupported");
        }
        added.push({
          id: crypto.randomUUID(),
          kind: "text",
          name: file.name || t("composer.pastedFile"),
          mediaType: file.type || "text/plain",
          byteLength: buffer.byteLength,
          text,
        });
      }
      onAttachmentsChange([...attachments, ...added]);
    } catch (error) {
      setAttachmentError(
        error instanceof Error && error.message === "too-large"
          ? t("composer.attachmentTooLarge")
          : t("composer.attachmentUnsupported"),
      );
    }
  };

  /**
   * 优先消费剪贴板文件项；纯文本粘贴继续交给 Textarea 默认行为。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handlePaste = (event: ClipboardEvent<HTMLTextAreaElement>) => {
    const files = collectClipboardFiles(event.clipboardData);
    if (files.length > 0) {
      event.preventDefault();
      void addFiles(files);
    }
  };

  /**
   * 消费隐藏文件输入并允许再次选择同一文件。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handleFileSelection = (event: ChangeEvent<HTMLInputElement>) => {
    void addFiles([...(event.currentTarget.files ?? [])]);
    event.currentTarget.value = "";
  };

  return (
    <div className="shrink-0 bg-background px-3 pb-2 pt-2 sm:px-6 sm:pb-3 sm:pt-0">
      <form
        onSubmit={handleSubmit}
        className="mx-auto w-full max-w-[760px] rounded-2xl border bg-background p-2 shadow-sm focus-within:border-ring focus-within:ring-1 focus-within:ring-ring/20"
      >
        {attachments.length > 0 ? (
          <div className="flex flex-wrap gap-2 px-1 pb-2">
            {attachments.map((attachment) => (
              <span key={attachment.id} className="inline-flex max-w-60 items-center gap-2 rounded-lg border bg-muted/40 p-1 text-[10px] text-foreground/75">
                {attachment.kind === "image" ? (
                  <img
                    src={`data:${attachment.mediaType};base64,${attachment.dataBase64}`}
                    alt={attachment.name}
                    className="size-12 shrink-0 rounded-md bg-muted object-cover"
                    decoding="async"
                  />
                ) : null}
                <span className="min-w-0">
                  <span className="block max-w-36 truncate">{attachment.name}</span>
                  <span className="block text-muted-foreground">{Math.ceil(attachment.byteLength / 1024)} KB</span>
                </span>
                <button
                  type="button"
                  className="self-start rounded p-0.5 text-muted-foreground hover:text-foreground"
                  onClick={() => onAttachmentsChange(attachments.filter((item) => item.id !== attachment.id))}
                  aria-label={t("composer.removeAttachment", { name: attachment.name })}
                >
                  <X className="size-3" aria-hidden="true" />
                </button>
              </span>
            ))}
          </div>
        ) : null}
        <Textarea
          value={value}
          onChange={(event) => onValueChange(event.target.value)}
          onKeyDown={handleKeyDown}
          onPaste={handlePaste}
          placeholder={t("composer.placeholder")}
          aria-label={t("composer.messageLabel")}
          className="min-h-[54px] resize-none border-0 px-2 py-1 text-[13px] leading-5 shadow-none placeholder:text-muted-foreground focus-visible:ring-0"
        />

        <div className="flex h-9 items-center gap-1">
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                type="button"
                variant="ghost"
                size="icon"
                className="size-8 rounded-full text-muted-foreground"
                onClick={() => fileInputRef.current?.click()}
                disabled={busy}
                aria-label={t("composer.addAttachment")}
              >
                <Paperclip className="size-4" aria-hidden="true" />
              </Button>
            </TooltipTrigger>
            <TooltipContent>{t("composer.addAttachment")}</TooltipContent>
          </Tooltip>
          <input
            ref={fileInputRef}
            type="file"
            multiple
            className="hidden"
            accept="image/png,image/jpeg,image/webp,image/gif,text/*,.md,.markdown,.json,.jsonl,.csv,.tsv,.xml,.yaml,.yml,.toml,.log,.rs,.cs,.ts,.tsx,.js,.jsx,.css,.html,.sql"
            onChange={handleFileSelection}
            aria-label={t("composer.attachmentInput")}
          />

          <Select
            value={selectedAgentProfileId ?? "__default__"}
            onValueChange={(profileId) =>
              onAgentChange(profileId === "__default__" ? null : profileId)
            }
          >
            <SelectTrigger
              aria-label={t("composer.chooseAgent")}
              className="h-8 w-auto min-w-[96px] max-w-[150px] gap-1 border-0 px-2 text-[11px] font-medium shadow-none focus:ring-0"
            >
              <Bot className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
              <SelectValue>
                {selectedAgent?.displayName ?? t("composer.defaultAgent")}
              </SelectValue>
            </SelectTrigger>
            <SelectContent align="start">
              <SelectItem value="__default__">{t("composer.defaultAgent")}</SelectItem>
              {agentProfiles
                .filter((profile) => profile.enabled)
                .map((profile) => (
                  <SelectItem key={profile.profileId} value={profile.profileId}>
                    {profile.displayName}
                  </SelectItem>
                ))}
            </SelectContent>
          </Select>

          <Select
            value={selectedModelRouteKey}
            onValueChange={onModelChange}
            disabled={modelLoadState !== "ready"}
          >
            <SelectTrigger
              aria-label={t("composer.chooseModel")}
              className="h-8 w-auto min-w-[96px] max-w-[150px] gap-1 border-0 px-2 text-[11px] font-medium shadow-none focus:ring-0"
            >
              <SelectValue placeholder={modelStatusLabel} />
            </SelectTrigger>
            <SelectContent align="start">
              {models.map((model) => (
                <SelectItem key={getModelRouteKey(model)} value={getModelRouteKey(model)}>
                  {model.displayName} · {model.providerId}/{model.apiFamily}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>

          {modelLoadState === "error" ? (
            <Tooltip>
              <TooltipTrigger asChild>
                <Button
                  type="button"
                  variant="ghost"
                  size="icon"
                  className="size-8 shrink-0 rounded-full text-muted-foreground"
                  onClick={onRetryModels}
                  aria-label={t("composer.retryModels")}
                >
                  <RefreshCw className="size-3.5" aria-hidden="true" />
                </Button>
              </TooltipTrigger>
              <TooltipContent>{t("composer.retryModels")}</TooltipContent>
            </Tooltip>
          ) : null}

          <div className="flex-1" />

          <Button
            type="submit"
            size="icon"
            className="size-8 rounded-full bg-primary text-primary-foreground shadow-none hover:bg-primary/90"
            disabled={busy || (value.trim().length === 0 && attachments.length === 0) || modelLoadState !== "ready"}
            aria-label={busy ? t("composer.submitting") : t("composer.send")}
          >
            {busy ? (
              <LoaderCircle className="size-4 animate-spin" aria-hidden="true" />
            ) : (
              <ArrowUp className="size-4" aria-hidden="true" />
            )}
          </Button>
        </div>
      </form>

      {attachmentError || submissionError ? (
        <p className="mx-auto mt-1 w-full max-w-[760px] px-2 text-[10px] text-red-600" role="alert">
          {attachmentError ?? submissionError}
        </p>
      ) : null}

      <div className="mx-auto mt-1.5 flex h-7 w-full max-w-[760px] items-center gap-2 overflow-hidden px-1 text-[10px] text-muted-foreground">
        <BackendSwitcher compact />
        <div className="hidden min-w-0 items-center gap-1.5 sm:flex">
          <Server className="size-3 shrink-0" aria-hidden="true" />
          <span className="truncate">
            {runtimeEnvironment?.supportsLocalDaemon ? t("composer.localService") : t("composer.remotePreview")}
          </span>
        </div>
        <div className="hidden items-center gap-1.5 md:flex">
          <LockKeyhole className="size-3" aria-hidden="true" />
          {selectedAgent?.dangerousActionMode === "deny" ? t("composer.readOnly") : t("composer.standardApproval")}
        </div>
        <div className="flex-1" />
        <div className="flex min-w-0 items-center gap-1.5">
          <GitBranch className="size-3 shrink-0" aria-hidden="true" />
          <span className="max-w-28 truncate">main</span>
        </div>
      </div>
    </div>
  );
}
