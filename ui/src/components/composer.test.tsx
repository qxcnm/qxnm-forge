import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ComponentProps } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { Composer, type ComposerAttachment } from "@/components/composer";
import { TooltipProvider } from "@/components/ui/tooltip";

const MODEL = {
  providerId: "faux",
  modelId: "faux-v1",
  apiFamily: "faux",
  displayName: "Faux",
  capabilities: { input: ["text", "image"], output: ["text"] },
  supportsReasoning: false,
  supportsTools: true,
  supportsImageInput: true,
  supportsImageOutput: false,
} as const;

const originalClipboardDescriptor = Object.getOwnPropertyDescriptor(navigator, "clipboard");

/**
 * 使用完整默认属性渲染 Composer，并允许测试仅覆盖关注的输入。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function renderComposer(overrides: Partial<ComponentProps<typeof Composer>> = {}) {
  const properties: ComponentProps<typeof Composer> = {
    value: "",
    attachments: [],
    selectedModelRouteKey: "faux\0faux-v1\0faux",
    models: [MODEL],
    modelLoadState: "ready",
    agentProfiles: [],
    selectedAgentProfileId: null,
    submitMode: "enter",
    busy: false,
    submissionError: null,
    onValueChange: vi.fn(),
    onModelChange: vi.fn(),
    onRetryModels: vi.fn(),
    onAgentChange: vi.fn(),
    onAttachmentsChange: vi.fn(),
    onSubmit: vi.fn(),
    ...overrides,
  };
  return {
    ...render(<TooltipProvider><Composer {...properties} /></TooltipProvider>),
    properties,
  };
}

/**
 * 为单测注入只暴露图片 read 能力的浏览器剪贴板桩。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function installClipboardRead(read: Clipboard["read"] | undefined): void {
  Object.defineProperty(navigator, "clipboard", {
    configurable: true,
    value: read ? { read } : undefined,
  });
}

afterEach(() => {
  if (originalClipboardDescriptor) {
    Object.defineProperty(navigator, "clipboard", originalClipboardDescriptor);
  } else {
    Reflect.deleteProperty(navigator, "clipboard");
  }
});

describe("Composer attachments", () => {
  /**
   * 验证剪贴板图片会成为可移除附件，并允许无文字提交。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("accepts pasted images and enables attachment-only submission", async () => {
    const bytes = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]);
    const file = new File([bytes], "pasted.png", { type: "image/png" });
    Object.defineProperty(file, "arrayBuffer", {
      value: () => Promise.resolve(bytes.buffer),
    });
    const onAttachmentsChange = vi.fn();
    const onSubmit = vi.fn();
    const properties = {
      value: "",
      selectedModelRouteKey: "faux\0faux-v1\0faux",
      models: [MODEL],
      modelLoadState: "ready" as const,
      agentProfiles: [],
      selectedAgentProfileId: null,
      submitMode: "enter" as const,
      busy: false,
      submissionError: null,
      onValueChange: vi.fn(),
      onModelChange: vi.fn(),
      onRetryModels: vi.fn(),
      onAgentChange: vi.fn(),
      onAttachmentsChange,
      onSubmit,
    };
    const { rerender } = render(
      <TooltipProvider><Composer {...properties} attachments={[]} /></TooltipProvider>,
    );

    fireEvent.paste(screen.getByLabelText("任务消息"), {
      clipboardData: { files: [file] },
    });
    await waitFor(() => expect(onAttachmentsChange).toHaveBeenCalledTimes(1));
    const attachments = onAttachmentsChange.mock.calls[0]?.[0] as readonly ComposerAttachment[];
    expect(attachments[0]).toMatchObject({
      kind: "image",
      name: "pasted.png",
      mediaType: "image/png",
      byteLength: bytes.length,
    });

    rerender(
      <TooltipProvider><Composer {...properties} attachments={attachments} /></TooltipProvider>,
    );
    fireEvent.click(screen.getByRole("button", { name: "发送任务" }));
    expect(onSubmit).toHaveBeenCalledTimes(1);
    expect(screen.getByLabelText("移除附件 pasted.png")).toBeInTheDocument();
    expect(screen.getByRole("img", { name: "pasted.png" })).toHaveAttribute(
      "src",
      `data:image/png;base64,${btoa(String.fromCharCode(...bytes))}`,
    );
  });

  /**
   * 验证 WebKitGTK 仅通过 DataTransferItem 暴露截图时仍可创建图片附件。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("accepts WebKit clipboard image items when files is empty", async () => {
    const bytes = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]);
    const file = new File([bytes], "webkit-paste.png", { type: "image/png" });
    Object.defineProperty(file, "arrayBuffer", {
      value: () => Promise.resolve(bytes.buffer),
    });
    const getAsFile = vi.fn(() => file);
    const onAttachmentsChange = vi.fn();
    render(
      <TooltipProvider>
        <Composer
          value=""
          attachments={[]}
          selectedModelRouteKey="faux\0faux-v1\0faux"
          models={[MODEL]}
          modelLoadState="ready"
          agentProfiles={[]}
          selectedAgentProfileId={null}
          submitMode="enter"
          busy={false}
          onValueChange={vi.fn()}
          onModelChange={vi.fn()}
          onRetryModels={vi.fn()}
          onAgentChange={vi.fn()}
          onAttachmentsChange={onAttachmentsChange}
          onSubmit={vi.fn()}
        />
      </TooltipProvider>,
    );

    fireEvent.paste(screen.getByLabelText("任务消息"), {
      clipboardData: {
        files: { length: 0 },
        items: {
          0: { kind: "file", type: "image/png", getAsFile },
          length: 1,
        },
      },
    });

    await waitFor(() => expect(onAttachmentsChange).toHaveBeenCalledTimes(1));
    expect(getAsFile).toHaveBeenCalledTimes(1);
    const attachments = onAttachmentsChange.mock.calls[0]?.[0] as readonly ComposerAttachment[];
    expect(attachments[0]).toMatchObject({
      kind: "image",
      name: "webkit-paste.png",
      mediaType: "image/png",
      byteLength: bytes.length,
    });
  });

  /**
   * 验证同步 files/items 均为空时会使用浏览器异步图片 API，且不会再调用原生兜底。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("uses the browser image fallback for genuinely empty clipboard lists", async () => {
    const bytes = new Uint8Array([137, 80, 78, 71, 1, 2, 3, 4]);
    const getType = vi.fn(() => Promise.resolve(new Blob([bytes], { type: "image/png" })));
    const read = vi.fn(() => Promise.resolve([{
      types: ["image/png"],
      getType,
    } as unknown as ClipboardItem]));
    installClipboardRead(read);
    const readNativeClipboardImage = vi.fn(() => Promise.resolve(null));
    const onAttachmentsChange = vi.fn();
    renderComposer({ onAttachmentsChange, readNativeClipboardImage });

    fireEvent.paste(screen.getByLabelText("任务消息"), {
      clipboardData: { files: [], items: [], types: [] },
    });

    await waitFor(() => expect(onAttachmentsChange).toHaveBeenCalledTimes(1));
    expect(read).toHaveBeenCalledTimes(1);
    expect(getType).toHaveBeenCalledWith("image/png");
    expect(readNativeClipboardImage).not.toHaveBeenCalled();
    const attachments = onAttachmentsChange.mock.calls[0]?.[0] as readonly ComposerAttachment[];
    expect(attachments[0]).toMatchObject({
      kind: "image",
      name: "pasted.png",
      mediaType: "image/png",
      byteLength: bytes.length,
    });
  });

  /**
   * 验证 clipboardData 为 null 时浏览器空结果会继续调用窄权限原生图片读取器。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("uses the native image fallback when clipboardData is null", async () => {
    const bytes = new Uint8Array([255, 216, 255, 224, 1, 2, 3, 4]);
    const read = vi.fn(() => Promise.resolve([] as ClipboardItem[]));
    installClipboardRead(read);
    const file = new File([bytes], "native-paste.jpg", { type: "image/jpeg" });
    const readNativeClipboardImage = vi.fn(() => Promise.resolve(file));
    const onAttachmentsChange = vi.fn();
    renderComposer({ onAttachmentsChange, readNativeClipboardImage });

    fireEvent.paste(screen.getByLabelText("任务消息"), { clipboardData: null });

    await waitFor(() => expect(onAttachmentsChange).toHaveBeenCalledTimes(1));
    expect(read).toHaveBeenCalledTimes(1);
    expect(readNativeClipboardImage).toHaveBeenCalledTimes(1);
    const attachments = onAttachmentsChange.mock.calls[0]?.[0] as readonly ComposerAttachment[];
    expect(attachments[0]).toMatchObject({
      kind: "image",
      name: "native-paste.jpg",
      mediaType: "image/jpeg",
      byteLength: bytes.length,
    });
  });

  /**
   * 验证空 MIME 图片可由白名单扩展名识别，不会退回异步路径或丢失附件。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("accepts a clipboard image with an empty MIME and safe extension", async () => {
    const bytes = new Uint8Array([137, 80, 78, 71, 5, 6, 7, 8]);
    const file = new File([bytes], "empty-mime.png", { type: "" });
    const read = vi.fn(() => Promise.resolve([] as ClipboardItem[]));
    installClipboardRead(read);
    const onAttachmentsChange = vi.fn();
    renderComposer({ onAttachmentsChange });

    fireEvent.paste(screen.getByLabelText("任务消息"), {
      clipboardData: { files: [file], items: null, types: ["Files"] },
    });

    await waitFor(() => expect(onAttachmentsChange).toHaveBeenCalledTimes(1));
    expect(read).not.toHaveBeenCalled();
    const attachments = onAttachmentsChange.mock.calls[0]?.[0] as readonly ComposerAttachment[];
    expect(attachments[0]).toMatchObject({
      kind: "image",
      name: "empty-mime.png",
      mediaType: "image/png",
      byteLength: bytes.length,
    });
  });

  /**
   * 验证 files 与 items 对同一空 MIME 图片的两份投影只会生成一个附件。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("deduplicates the same empty-MIME image across files and items", async () => {
    const bytes = new Uint8Array([137, 80, 78, 71, 9, 10, 11, 12]);
    const metadata = { type: "", lastModified: 1234 };
    const fileProjection = new File([bytes], "duplicate.png", metadata);
    const itemProjection = new File([bytes], "duplicate.png", metadata);
    const getAsFile = vi.fn(() => itemProjection);
    const onAttachmentsChange = vi.fn();
    renderComposer({ onAttachmentsChange });

    fireEvent.paste(screen.getByLabelText("任务消息"), {
      clipboardData: {
        files: [fileProjection],
        items: [{ kind: "file", type: "image/png", getAsFile }],
        types: ["Files", "image/png"],
      },
    });

    await waitFor(() => expect(onAttachmentsChange).toHaveBeenCalledTimes(1));
    const attachments = onAttachmentsChange.mock.calls[0]?.[0] as readonly ComposerAttachment[];
    expect(attachments).toHaveLength(1);
    expect(getAsFile).toHaveBeenCalledTimes(1);
  });

  /**
   * 验证 files 列表中元数据相同但对象不同的文件不会被误判为同一附件。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("keeps distinct file-list entries with matching metadata", async () => {
    const metadata = { type: "image/png", lastModified: 5678 };
    const first = new File([new Uint8Array([1, 2, 3])], "same.png", metadata);
    const second = new File([new Uint8Array([4, 5, 6])], "same.png", metadata);
    const onAttachmentsChange = vi.fn();
    renderComposer({ onAttachmentsChange });

    fireEvent.paste(screen.getByLabelText("任务消息"), {
      clipboardData: { files: [first, second], items: [], types: ["Files"] },
    });

    await waitFor(() => expect(onAttachmentsChange).toHaveBeenCalledTimes(1));
    const attachments = onAttachmentsChange.mock.calls[0]?.[0] as readonly ComposerAttachment[];
    expect(attachments).toHaveLength(2);
  });

  /**
   * 验证图片项读取抛错时安全转入兜底，并在已确认图片意图后显示明确错误。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("handles a throwing image item and reports an unavailable image", async () => {
    const getAsFile = vi.fn(() => {
      throw new DOMException("unavailable", "NotReadableError");
    });
    installClipboardRead(vi.fn(() => Promise.resolve([] as ClipboardItem[])));
    const readNativeClipboardImage = vi.fn(() => Promise.resolve(null));
    renderComposer({ readNativeClipboardImage });

    fireEvent.paste(screen.getByLabelText("任务消息"), {
      clipboardData: {
        files: null,
        items: [{ kind: "file", type: "image/png", getAsFile }],
        types: ["image/png"],
      },
    });

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "未能从系统剪贴板读取图片；请重试或使用回形针选择图片",
    );
    expect(getAsFile).toHaveBeenCalledTimes(1);
    expect(readNativeClipboardImage).toHaveBeenCalledTimes(1);
  });

  /**
   * 验证纯文本粘贴不请求图片权限、不调用原生读取器，也不产生附件错误。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("leaves ordinary text paste to the textarea", async () => {
    const read = vi.fn(() => Promise.resolve([] as ClipboardItem[]));
    installClipboardRead(read);
    const readNativeClipboardImage = vi.fn(() => Promise.resolve(null));
    const onAttachmentsChange = vi.fn();
    renderComposer({ onAttachmentsChange, readNativeClipboardImage });

    fireEvent.paste(screen.getByLabelText("任务消息"), {
      clipboardData: {
        files: [],
        items: [{ kind: "string", type: "text/plain" }],
        types: ["text/plain"],
      },
    });
    await Promise.resolve();

    expect(read).not.toHaveBeenCalled();
    expect(readNativeClipboardImage).not.toHaveBeenCalled();
    expect(onAttachmentsChange).not.toHaveBeenCalled();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  /**
   * 验证文档级监听不会截获 Composer 外部输入框中的粘贴事件。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("does not capture paste from another editable input", async () => {
    const read = vi.fn(() => Promise.resolve([] as ClipboardItem[]));
    installClipboardRead(read);
    renderComposer();
    const externalInput = document.createElement("input");
    document.body.append(externalInput);

    fireEvent.paste(externalInput, {
      clipboardData: { files: [], items: [], types: [] },
    });
    await Promise.resolve();

    expect(read).not.toHaveBeenCalled();
    externalInput.remove();
  });

  /**
   * 验证 busy 状态与附件按钮一致，不从同步或异步剪贴板新增附件。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("does not add clipboard attachments while busy", async () => {
    const file = new File([new Uint8Array([1, 2, 3])], "busy.png", { type: "image/png" });
    const read = vi.fn(() => Promise.resolve([] as ClipboardItem[]));
    installClipboardRead(read);
    const onAttachmentsChange = vi.fn();
    renderComposer({ busy: true, onAttachmentsChange });

    fireEvent.paste(screen.getByLabelText("任务消息"), {
      clipboardData: { files: [file], items: [], types: ["Files"] },
    });
    await Promise.resolve();

    expect(onAttachmentsChange).not.toHaveBeenCalled();
    expect(read).not.toHaveBeenCalled();
    expect(screen.getByLabelText("选择图片或文本文件")).toBeDisabled();
  });

  /**
   * 验证剪贴板 UTF-8 文本文件会保留文件名、MIME、字节数与完整文本。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("accepts pasted UTF-8 text files", async () => {
    const bytes = new TextEncoder().encode("第一行\nsecond line");
    const file = new File([bytes], "notes.md", { type: "text/markdown" });
    Object.defineProperty(file, "arrayBuffer", {
      value: () => Promise.resolve(bytes.buffer),
    });
    const onAttachmentsChange = vi.fn();
    render(
      <TooltipProvider>
        <Composer
          value=""
          attachments={[]}
          selectedModelRouteKey="faux\0faux-v1\0faux"
          models={[MODEL]}
          modelLoadState="ready"
          agentProfiles={[]}
          selectedAgentProfileId={null}
          submitMode="enter"
          busy={false}
          onValueChange={vi.fn()}
          onModelChange={vi.fn()}
          onRetryModels={vi.fn()}
          onAgentChange={vi.fn()}
          onAttachmentsChange={onAttachmentsChange}
          onSubmit={vi.fn()}
        />
      </TooltipProvider>,
    );

    fireEvent.paste(screen.getByLabelText("任务消息"), {
      clipboardData: { files: [file] },
    });

    await waitFor(() => expect(onAttachmentsChange).toHaveBeenCalledTimes(1));
    const attachments = onAttachmentsChange.mock.calls[0]?.[0] as readonly ComposerAttachment[];
    expect(attachments[0]).toMatchObject({
      kind: "text",
      name: "notes.md",
      mediaType: "text/markdown",
      byteLength: bytes.length,
      text: "第一行\nsecond line",
    });
  });

  /**
   * 验证任意二进制文件不会被静默当作文本附件。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects unsupported binary files", async () => {
    const file = new File([new Uint8Array([1, 2, 3])], "payload.bin", {
      type: "application/octet-stream",
    });
    const arrayBuffer = vi.fn(() => Promise.resolve(new Uint8Array([1, 2, 3]).buffer));
    Object.defineProperty(file, "arrayBuffer", { value: arrayBuffer });
    render(
      <TooltipProvider>
        <Composer
          value=""
          attachments={[]}
          selectedModelRouteKey="faux\0faux-v1\0faux"
          models={[MODEL]}
          modelLoadState="ready"
          agentProfiles={[]}
          selectedAgentProfileId={null}
          submitMode="enter"
          busy={false}
          onValueChange={vi.fn()}
          onModelChange={vi.fn()}
          onRetryModels={vi.fn()}
          onAgentChange={vi.fn()}
          onAttachmentsChange={vi.fn()}
          onSubmit={vi.fn()}
        />
      </TooltipProvider>,
    );

    fireEvent.change(screen.getByLabelText("选择图片或文本文件"), {
      target: { files: [file] },
    });
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "仅支持 PNG、JPEG、WebP、GIF 和 UTF-8 文本文件",
    );
    expect(arrayBuffer).not.toHaveBeenCalled();
  });

  /**
   * 验证受支持图片也会先检查 File.size，超出原图上限时不读取内容。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects an oversized image before reading its bytes", async () => {
    const file = new File([new Uint8Array([1])], "oversized.png", { type: "image/png" });
    Object.defineProperty(file, "size", { value: 20 * 1_024 * 1_024 + 1 });
    const arrayBuffer = vi.fn(() => Promise.resolve(new Uint8Array([1]).buffer));
    Object.defineProperty(file, "arrayBuffer", { value: arrayBuffer });
    renderComposer();

    fireEvent.change(screen.getByLabelText("选择图片或文本文件"), {
      target: { files: [file] },
    });

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "图片原文件最大 20 MB（会自动压缩），文本文件最大 256 KB",
    );
    expect(arrayBuffer).not.toHaveBeenCalled();
  });
});
