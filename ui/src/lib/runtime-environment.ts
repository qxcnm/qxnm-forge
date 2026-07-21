import { invoke, isTauri } from "@tauri-apps/api/core";

import type { RuntimeEnvironment } from "@/types/application-service";

/**
 * 查询当前宿主是否可以运行本地 daemon。
 *
 * 输出：浏览器预览、桌面本地或移动远程模式的中立能力投影。
 * 失败：Tauri 命令失败时向调用方传播异常，由查询层展示降级状态。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export async function getRuntimeEnvironment(): Promise<RuntimeEnvironment> {
  if (!isTauri()) {
    return {
      platform: "browser",
      mode: "browser-preview",
      supportsLocalDaemon: false,
    };
  }

  return invoke<RuntimeEnvironment>("runtime_environment");
}
