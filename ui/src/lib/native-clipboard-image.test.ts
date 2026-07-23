import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { readImageMock } = vi.hoisted(() => ({
  readImageMock: vi.fn(),
}));

vi.mock("@tauri-apps/plugin-clipboard-manager", () => ({
  readImage: readImageMock,
}));

import { readNativeClipboardImage } from "@/lib/native-clipboard-image";

/**
 * 构造只暴露图片读取所需方法的原生资源替身。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function createNativeImage(
  width: number,
  height: number,
  rgba: Uint8Array,
) {
  return {
    size: vi.fn().mockResolvedValue({ width, height }),
    rgba: vi.fn().mockResolvedValue(rgba),
    close: vi.fn().mockResolvedValue(undefined),
  };
}

describe("native clipboard image reader", () => {
  const originalCreateElement = document.createElement.bind(document);
  const putImageData = vi.fn();

  beforeEach(() => {
    readImageMock.mockReset();
    putImageData.mockReset();
    vi.stubGlobal(
      "ImageData",
      class ImageDataStub {
        /** 像素宽度。 */
        public readonly width: number;
        /** 像素高度。 */
        public readonly height: number;

        /**
         * 保存测试编码器传入的尺寸。
         *
         * 作者：高宏顺
         * 邮箱：18272669457@163.com
         */
        public constructor(
          _pixels: Uint8ClampedArray,
          width: number,
          height: number,
        ) {
          this.width = width;
          this.height = height;
        }
      },
    );
    vi.spyOn(document, "createElement").mockImplementation((tagName, options) => {
      if (tagName !== "canvas") {
        return originalCreateElement(tagName, options);
      }
      return {
        width: 0,
        height: 0,
        getContext: vi.fn().mockReturnValue({ putImageData }),
        toBlob: (callback: BlobCallback) =>
          callback(new Blob([new Uint8Array([137, 80, 78, 71])], { type: "image/png" })),
      } as unknown as HTMLCanvasElement;
    });
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  /**
   * 验证剪贴板没有图片时安静返回且不尝试读取文本。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("returns null when the native clipboard has no image", async () => {
    readImageMock.mockRejectedValue(new Error("unavailable"));

    await expect(readNativeClipboardImage()).resolves.toBeNull();
    expect(readImageMock).toHaveBeenCalledOnce();
  });

  /**
   * 验证合法 RGBA 被编码为 PNG 且原生资源在成功路径关闭。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("encodes rgba as png and closes the native resource", async () => {
    const image = createNativeImage(1, 1, new Uint8Array([1, 2, 3, 255]));
    readImageMock.mockResolvedValue(image);

    const file = await readNativeClipboardImage();

    expect(file).toBeInstanceOf(File);
    expect(file?.type).toBe("image/png");
    expect(file?.name).toMatch(/^clipboard-\d+\.png$/);
    expect(putImageData).toHaveBeenCalledOnce();
    expect(image.close).toHaveBeenCalledOnce();
  });

  /**
   * 验证像素边界在读取大块 RGBA 前拒绝并仍释放资源。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects oversized dimensions before reading rgba", async () => {
    const image = createNativeImage(16_385, 1, new Uint8Array());
    readImageMock.mockResolvedValue(image);

    await expect(readNativeClipboardImage()).rejects.toThrow(
      "native clipboard image is outside the supported boundary",
    );
    expect(image.rgba).not.toHaveBeenCalled();
    expect(image.close).toHaveBeenCalledOnce();
  });
});
