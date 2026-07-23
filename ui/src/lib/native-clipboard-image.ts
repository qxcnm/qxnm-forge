import { readImage } from "@tauri-apps/plugin-clipboard-manager";

const MAX_CLIPBOARD_IMAGE_DIMENSION = 16_384;
const MAX_CLIPBOARD_IMAGE_PIXELS = 33_554_432;

/**
 * 将桌面壳只读剪贴板返回的 RGBA 图片编码为受控 PNG 文件。
 *
 * 输入：Tauri 图片资源报告的尺寸与逐行 RGBA 字节。
 * 输出：可复用现有附件校验和压缩流程的 PNG `File`。
 * 不变量：尺寸、像素数与 RGBA 长度必须完全一致，且不会读取剪贴板文本。
 * 失败：尺寸越界、资源数据不完整或浏览器无法编码 PNG 时拒绝。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
async function encodeClipboardRgbaAsPng(
  width: number,
  height: number,
  rgba: Uint8Array,
): Promise<File> {
  if (
    !Number.isSafeInteger(width) ||
    !Number.isSafeInteger(height) ||
    width <= 0 ||
    height <= 0 ||
    width > MAX_CLIPBOARD_IMAGE_DIMENSION ||
    height > MAX_CLIPBOARD_IMAGE_DIMENSION ||
    width * height > MAX_CLIPBOARD_IMAGE_PIXELS ||
    rgba.byteLength !== width * height * 4
  ) {
    throw new Error("native clipboard image is outside the supported boundary");
  }

  const canvas = document.createElement("canvas");
  canvas.width = width;
  canvas.height = height;
  const context = canvas.getContext("2d");
  if (!context) {
    throw new Error("native clipboard image encoder is unavailable");
  }
  const pixels = new Uint8ClampedArray(
    rgba.buffer,
    rgba.byteOffset,
    rgba.byteLength,
  );
  context.putImageData(new ImageData(pixels, width, height), 0, 0);

  const blob = await new Promise<Blob | null>((resolve) => {
    canvas.toBlob(resolve, "image/png");
  });
  if (!blob || blob.size === 0) {
    throw new Error("native clipboard image could not be encoded");
  }

  const capturedAt = Date.now();
  return new File([blob], `clipboard-${capturedAt}.png`, {
    type: "image/png",
    lastModified: capturedAt,
  });
}

/**
 * 通过 Tauri 剪贴板插件只读取当前图片，并始终释放原生图片资源。
 *
 * 输入：无；权限限定为 `clipboard-manager:allow-read-image`。
 * 输出：剪贴板图片的 PNG 文件；剪贴板没有可读图片时返回 `null`。
 * 不变量：不调用文本读取、写入或清空剪贴板 API，也不回显底层错误正文。
 * 失败：取得图片后若尺寸、RGBA 或 PNG 编码校验失败则以固定错误拒绝。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export async function readNativeClipboardImage(): Promise<File | null> {
  let image: Awaited<ReturnType<typeof readImage>>;
  try {
    image = await readImage();
  } catch {
    return null;
  }

  try {
    const { width, height } = await image.size();
    if (
      !Number.isSafeInteger(width) ||
      !Number.isSafeInteger(height) ||
      width <= 0 ||
      height <= 0 ||
      width > MAX_CLIPBOARD_IMAGE_DIMENSION ||
      height > MAX_CLIPBOARD_IMAGE_DIMENSION ||
      width * height > MAX_CLIPBOARD_IMAGE_PIXELS
    ) {
      throw new Error("native clipboard image is outside the supported boundary");
    }
    return await encodeClipboardRgbaAsPng(width, height, await image.rgba());
  } finally {
    try {
      await image.close();
    } catch {
      // 资源释放失败不覆盖图片读取结果，也不暴露平台错误正文。
    }
  }
}
