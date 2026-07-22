import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { Composer, type ComposerAttachment } from "@/components/composer";
import { TooltipProvider } from "@/components/ui/tooltip";

const MODEL = {
  providerId: "faux",
  modelId: "faux-v1",
  apiFamily: "faux",
  displayName: "Faux",
  supportsReasoning: false,
  supportsTools: true,
} as const;

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
    Object.defineProperty(file, "arrayBuffer", {
      value: () => Promise.resolve(new Uint8Array([1, 2, 3]).buffer),
    });
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
  });
});
