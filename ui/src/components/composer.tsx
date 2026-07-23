import { useCallback, useEffect, useRef, useState, type ChangeEvent, type FormEvent, type KeyboardEvent } from "react";
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

interface DecodedImage {
  readonly source: CanvasImageSource;
  readonly width: number;
  readonly height: number;
  readonly release: () => void;
}

interface ClipboardFileCollection {
  readonly files: readonly File[];
  readonly confirmedImageIntent: boolean;
  readonly textOnly: boolean;
}

interface ClipboardImageReadResult {
  readonly file: File | null;
  readonly confirmedImageIntent: boolean;
}

/**
 * 以窄权限读取当前剪贴板图片，供浏览器标准 API 不可用时注入原生实现。
 *
 * 输入：无，不接受文本读取或剪贴板写入能力。
 * 输出：一份图片 File；剪贴板没有图片时返回 null。
 * 不变量：实现不得读取或返回剪贴板文本。
 * 失败条件：权限拒绝或平台读取失败时可以拒绝 Promise，Composer 会安全降级。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export type ComposerNativeClipboardImageReader = () => Promise<File | null>;

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
 * 规范化 MIME 并仅返回协议允许的图片类型。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function parseSupportedImageMediaType(value: string): SupportedImageMediaType | null {
  const normalized = value.split(";", 1)[0]?.trim().toLowerCase() ?? "";
  return isSupportedImageMediaType(normalized) ? normalized : null;
}

/**
 * 在剪贴板未提供 MIME 时，根据安全白名单扩展名推断图片类型。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function inferImageMediaTypeFromName(name: string): SupportedImageMediaType | null {
  const extension = name.trim().toLowerCase().match(/\.([^.]+)$/)?.[1];
  switch (extension) {
    case "png":
      return "image/png";
    case "jpg":
    case "jpeg":
      return "image/jpeg";
    case "webp":
      return "image/webp";
    case "gif":
      return "image/gif";
    default:
      return null;
  }
}

/**
 * 解析文件及剪贴板项声明的图片类型；只有两者均为空时才使用扩展名兜底。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function resolveImageMediaType(file: File, fallbackMediaType = ""): SupportedImageMediaType | null {
  const fileMediaType = file.type.trim();
  const fallback = fallbackMediaType.trim();
  const declared = parseSupportedImageMediaType(fileMediaType) ??
    parseSupportedImageMediaType(fallback);
  if (declared) {
    return declared;
  }
  return fileMediaType.length === 0 && fallback.length === 0
    ? inferImageMediaTypeFromName(file.name)
    : null;
}

/**
 * 为 MIME 缺失或不一致的受支持剪贴板图片补齐稳定文件名与类型。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function normalizeClipboardFile(file: File, fallbackMediaType = ""): File {
  const mediaType = resolveImageMediaType(file, fallbackMediaType);
  if (!mediaType) {
    return file;
  }
  const extension = mediaType === "image/jpeg" ? "jpg" : mediaType.slice("image/".length);
  const name = file.name || `pasted.${extension}`;
  if (file.type.length === 0 && fallbackMediaType.length === 0 && file.name.length > 0) {
    return file;
  }
  if (file.type === mediaType && file.name === name) {
    return file;
  }
  return new File([file], name, {
    type: mediaType,
    lastModified: file.lastModified,
  });
}

/**
 * 在读取字节前判断文件是否属于允许的 UTF-8 文本附件候选。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function isSupportedTextFile(file: File): boolean {
  return file.type.trim().toLowerCase().startsWith("text/") ||
    /\.(?:txt|md|markdown|json|jsonl|csv|tsv|xml|ya?ml|toml|log|rs|cs|ts|tsx|js|jsx|css|html|sql)$/i.test(file.name);
}

/**
 * 从标准 files 与 WebKitGTK 常用的 items 两条剪贴板路径收集文件并去重。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function collectClipboardFiles(clipboardData: DataTransfer | null): ClipboardFileCollection {
  const files: File[] = [];
  const seenObjects = new WeakSet<File>();
  const fileListSignatures = new Set<string>();
  let confirmedImageIntent = false;
  let hasFileIntent = false;
  let hasTextIntent = false;
  const append = (file: File, fallbackMediaType = "", fromItem = false) => {
    if (seenObjects.has(file)) {
      return;
    }
    const normalized = normalizeClipboardFile(file, fallbackMediaType);
    const mediaType = resolveImageMediaType(normalized, fallbackMediaType) ??
      (normalized.type.trim().toLowerCase() || fallbackMediaType.trim().toLowerCase());
    const signature = `${normalized.name}\0${mediaType}\0${normalized.size}\0${normalized.lastModified}`;
    if (fromItem && fileListSignatures.has(signature)) {
      seenObjects.add(file);
      return;
    }
    seenObjects.add(file);
    if (!fromItem) {
      fileListSignatures.add(signature);
    }
    files.push(normalized);
    hasFileIntent = true;
    if (resolveImageMediaType(normalized, fallbackMediaType)) {
      confirmedImageIntent = true;
    }
  };

  if (!clipboardData) {
    return { files, confirmedImageIntent, textOnly: false };
  }

  try {
    const clipboardFiles = clipboardData.files;
    for (let index = 0; index < (clipboardFiles?.length ?? 0); index += 1) {
      const file = clipboardFiles[index];
      if (file) {
        append(file);
      }
    }
  } catch {
    // 某些 WebKitGTK 版本会在 files 投影不可用时抛错，继续尝试 items。
  }

  try {
    const clipboardItems = clipboardData.items;
    for (let index = 0; index < (clipboardItems?.length ?? 0); index += 1) {
      const item = clipboardItems[index];
      if (!item) {
        continue;
      }
      if (item.kind === "string") {
        hasTextIntent = true;
        continue;
      }
      if (item.kind !== "file") {
        continue;
      }
      hasFileIntent = true;
      const itemMediaType = item.type.trim().toLowerCase();
      if (itemMediaType.startsWith("image/")) {
        confirmedImageIntent = true;
      }
      if (itemMediaType.length > 0 && !parseSupportedImageMediaType(itemMediaType)) {
        continue;
      }
      try {
        const file = item.getAsFile();
        if (file) {
          append(file, itemMediaType, true);
        }
      } catch {
        // getAsFile 在部分 WebKitGTK/Wayland 组合中会抛错，交由异步图片路径兜底。
      }
    }
  } catch {
    // items 投影本身不可用时继续使用 types 和异步图片路径。
  }

  try {
    const clipboardTypes = clipboardData.types;
    for (let index = 0; index < (clipboardTypes?.length ?? 0); index += 1) {
      const clipboardType = clipboardTypes[index]?.trim().toLowerCase() ?? "";
      if (clipboardType === "files") {
        hasFileIntent = true;
      } else if (clipboardType.startsWith("image/")) {
        hasFileIntent = true;
        confirmedImageIntent = true;
      } else if (clipboardType.startsWith("text/")) {
        hasTextIntent = true;
      }
    }
  } catch {
    // types 仅用于判定是否为纯文本粘贴，不影响图片兜底读取。
  }

  return {
    files,
    confirmedImageIntent,
    textOnly: hasTextIntent && !hasFileIntent && !confirmedImageIntent,
  };
}

/**
 * 通过 HTMLImageElement 与 object URL 解码图片，并提供确定性的资源释放回调。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function decodeImageElement(file: File): Promise<DecodedImage> {
  return new Promise((resolve, reject) => {
    if (typeof globalThis.Image !== "function" ||
      typeof URL.createObjectURL !== "function" ||
      typeof URL.revokeObjectURL !== "function") {
      reject(new Error("unsupported"));
      return;
    }
    const objectUrl = URL.createObjectURL(file);
    const image = new Image();
    let released = false;
    const release = () => {
      if (released) {
        return;
      }
      released = true;
      image.onload = null;
      image.onerror = null;
      image.src = "";
      URL.revokeObjectURL(objectUrl);
    };
    image.onload = () => {
      const width = image.naturalWidth;
      const height = image.naturalHeight;
      if (width === 0 || height === 0) {
        release();
        reject(new Error("unsupported"));
        return;
      }
      resolve({ source: image, width, height, release });
    };
    image.onerror = () => {
      release();
      reject(new Error("unsupported"));
    };
    image.src = objectUrl;
  });
}

/**
 * 优先使用 createImageBitmap 解码；不可用或失败时退回 WebKitGTK 兼容路径。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
async function decodeImage(file: File): Promise<DecodedImage> {
  if (typeof globalThis.createImageBitmap === "function") {
    try {
      const bitmap = await globalThis.createImageBitmap(file);
      return {
        source: bitmap,
        width: bitmap.width,
        height: bitmap.height,
        release: () => bitmap.close(),
      };
    } catch {
      // WebKitGTK 可能暴露 createImageBitmap 却无法解码特定图片，继续使用元素解码。
    }
  }
  return decodeImageElement(file);
}

/**
 * 从浏览器异步剪贴板 API 读取第一份受支持图片，不读取文字或未知二进制内容。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
async function readBrowserClipboardImage(): Promise<ClipboardImageReadResult> {
  let clipboard: Clipboard | undefined;
  try {
    clipboard = navigator.clipboard;
  } catch {
    return { file: null, confirmedImageIntent: false };
  }
  if (!clipboard || typeof clipboard.read !== "function") {
    return { file: null, confirmedImageIntent: false };
  }

  let items: readonly ClipboardItem[];
  try {
    items = await clipboard.read();
  } catch {
    return { file: null, confirmedImageIntent: false };
  }

  let confirmedImageIntent = false;
  for (const item of items) {
    let itemTypes: readonly string[] = [];
    try {
      itemTypes = [...item.types];
    } catch {
      continue;
    }
    if (itemTypes.some((type) => type.trim().toLowerCase().startsWith("image/"))) {
      confirmedImageIntent = true;
    }
    const mediaType = itemTypes
      .map((type) => parseSupportedImageMediaType(type))
      .find((type): type is SupportedImageMediaType => type !== null);
    if (!mediaType || typeof item.getType !== "function") {
      continue;
    }
    try {
      const blob = await item.getType(mediaType);
      const extension = mediaType === "image/jpeg" ? "jpg" : mediaType.slice("image/".length);
      return {
        file: new File([blob], `pasted.${extension}`, { type: mediaType }),
        confirmedImageIntent: true,
      };
    } catch {
      // 同一剪贴板可能暴露多个表示；当前表示失败时继续尝试下一项。
    }
  }
  return { file: null, confirmedImageIntent };
}

/**
 * 判断文档级 paste 是否属于当前 Composer，避免截获其他输入控件的粘贴。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function shouldHandlePasteTarget(target: EventTarget | null, composer: HTMLFormElement): boolean {
  if (target instanceof Node && composer.contains(target)) {
    return true;
  }
  if (!(target instanceof Element)) {
    return true;
  }
  return !target.closest(
    'input, textarea, [contenteditable]:not([contenteditable="false"]), [role="textbox"], [role="searchbox"]',
  );
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
 * 读取已验证 Blob 的字节；旧版 WebKit 缺少 arrayBuffer 时使用 FileReader 兼容。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function readBlobArrayBuffer(blob: Blob): Promise<ArrayBuffer> {
  if (typeof blob.arrayBuffer === "function") {
    return blob.arrayBuffer();
  }
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      if (reader.result instanceof ArrayBuffer) {
        resolve(reader.result);
      } else {
        reject(new Error("unsupported"));
      }
    };
    reader.onerror = () => reject(new Error("unsupported"));
    reader.readAsArrayBuffer(blob);
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
  const decoded = await decodeImage(file);
  try {
    if (decoded.width === 0 || decoded.height === 0) {
      throw new Error("unsupported");
    }
    const initialScale = Math.min(
      1,
      MAX_COMPRESSED_IMAGE_EDGE / Math.max(decoded.width, decoded.height),
    );
    let width = Math.max(1, Math.round(decoded.width * initialScale));
    let height = Math.max(1, Math.round(decoded.height * initialScale));
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
      context.drawImage(decoded.source, 0, 0, width, height);
      for (const quality of qualities) {
        const blob = await canvasToJpegBlob(canvas, quality);
        if (blob.size > 0 && blob.size <= MAX_INPUT_IMAGE_BYTES) {
          const bytes = new Uint8Array(await readBlobArrayBuffer(blob));
          const baseName = file.name.replace(/\.[^.]+$/, "") || "pasted";
          return { bytes, mediaType: "image/jpeg", name: `${baseName}.jpg` };
        }
      }
      width = Math.max(1, Math.round(width * 0.8));
      height = Math.max(1, Math.round(height * 0.8));
    }
    throw new Error("too-large");
  } finally {
    decoded.release();
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
  readonly readNativeClipboardImage?: ComposerNativeClipboardImageReader;
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
  readNativeClipboardImage,
  onValueChange,
  onModelChange,
  onRetryModels,
  onAgentChange,
  onAttachmentsChange,
  onSubmit,
}: ComposerProps) {
  const { t } = useTranslation();
  const formRef = useRef<HTMLFormElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const busyRef = useRef(busy);
  const attachmentsRef = useRef(attachments);
  const pasteInFlightRef = useRef(false);
  busyRef.current = busy;
  attachmentsRef.current = attachments;
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
  const addFiles = useCallback(async (files: readonly File[]): Promise<boolean> => {
    if (busyRef.current || files.length === 0) {
      return false;
    }
    setAttachmentError(null);
    if (attachmentsRef.current.length + files.length > 8) {
      setAttachmentError(t("composer.attachmentTooMany"));
      return false;
    }
    const added: ComposerAttachment[] = [];
    try {
      for (const sourceFile of files) {
        const file = normalizeClipboardFile(sourceFile);
        const imageMediaType = resolveImageMediaType(file);
        const isText = isSupportedTextFile(file);
        if (!imageMediaType && !isText) {
          throw new Error("unsupported");
        }
        if (imageMediaType &&
          (file.size === 0 || file.size > MAX_SOURCE_IMAGE_BYTES)) {
          throw new Error("too-large");
        }
        if (!imageMediaType && (file.size === 0 || file.size > 262_144)) {
          throw new Error("too-large");
        }
        const buffer = await readBlobArrayBuffer(file);
        if (busyRef.current) {
          return false;
        }
        if (imageMediaType) {
          if (buffer.byteLength === 0 || buffer.byteLength > MAX_SOURCE_IMAGE_BYTES) {
            throw new Error("too-large");
          }
          const prepared = buffer.byteLength <= MAX_INPUT_IMAGE_BYTES
            ? { bytes: new Uint8Array(buffer), mediaType: imageMediaType, name: file.name }
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
      if (busyRef.current) {
        return false;
      }
      onAttachmentsChange([...attachmentsRef.current, ...added]);
      return true;
    } catch (error) {
      setAttachmentError(
        error instanceof Error && error.message === "too-large"
          ? t("composer.attachmentTooLarge")
          : t("composer.attachmentUnsupported"),
      );
      return false;
    }
  }, [onAttachmentsChange, t]);

  /**
   * 串行尝试浏览器与窄权限原生图片读取器，确保同一次粘贴只添加一份图片。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const addClipboardImageFromFallback = useCallback(async (initiallyConfirmed: boolean) => {
    if (pasteInFlightRef.current || busyRef.current) {
      return;
    }
    pasteInFlightRef.current = true;
    let confirmedImageIntent = initiallyConfirmed;
    try {
      const browserResult = await readBrowserClipboardImage();
      confirmedImageIntent ||= browserResult.confirmedImageIntent;
      if (busyRef.current) {
        return;
      }
      let file = browserResult.file;
      if (!file && readNativeClipboardImage) {
        try {
          file = await readNativeClipboardImage();
        } catch {
          file = null;
        }
      }
      if (busyRef.current) {
        return;
      }
      if (file) {
        await addFiles([file]);
      } else if (confirmedImageIntent) {
        setAttachmentError(t("composer.clipboardImageUnavailable"));
      }
    } finally {
      pasteInFlightRef.current = false;
    }
  }, [addFiles, readNativeClipboardImage, t]);

  /**
   * 处理 Composer 内及非编辑区域的文档级粘贴；外部输入控件保持浏览器默认行为。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const handlePaste = useCallback((event: globalThis.ClipboardEvent) => {
    const composer = formRef.current;
    if (!composer || !shouldHandlePasteTarget(event.target, composer)) {
      return;
    }
    let clipboardData: DataTransfer | null = null;
    try {
      clipboardData = event.clipboardData;
    } catch {
      clipboardData = null;
    }
    const collection = collectClipboardFiles(clipboardData);
    if (collection.files.length > 0 || collection.confirmedImageIntent) {
      event.preventDefault();
    }
    if (busyRef.current) {
      return;
    }
    if (collection.files.length > 0) {
      void addFiles(collection.files);
      return;
    }
    if (collection.textOnly) {
      return;
    }
    void addClipboardImageFromFallback(collection.confirmedImageIntent);
  }, [addClipboardImageFromFallback, addFiles]);

  useEffect(() => {
    document.addEventListener("paste", handlePaste);
    return () => document.removeEventListener("paste", handlePaste);
  }, [handlePaste]);

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
        ref={formRef}
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
                  disabled={busy}
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
            disabled={busy}
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
                  {model.displayName} · {model.providerId}/{model.apiFamily} · {[
                    model.supportsImageInput ? t("composer.modelImageInput") : null,
                    model.supportsImageOutput ? t("composer.modelImageOutput") : null,
                  ].filter((label): label is string => label !== null).join(" / ") ||
                    t("composer.modelTextOnly")}
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
