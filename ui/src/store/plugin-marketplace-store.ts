import { create } from "zustand";

import { PLUGIN_CATALOG } from "@/features/plugins/plugin-catalog";

export interface PluginLocalPreference {
  readonly installed: boolean;
  readonly enabled: boolean;
}

type PluginLocalPreferences = Readonly<Record<string, PluginLocalPreference>>;

interface StoredPluginMarketplacePreferences {
  readonly plugins: PluginLocalPreferences;
}

interface PluginMarketplaceState {
  readonly plugins: PluginLocalPreferences;

  /**
   * 在当前设备记录插件已安装，但不授予或启用任何工具能力。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  installPlugin: (pluginId: string) => void;

  /**
   * 从当前设备移除插件偏好，并同时清除其启用状态。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  uninstallPlugin: (pluginId: string) => void;

  /**
   * 更新已安装插件的本地启用偏好，不改变服务端 capability 广告。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  setPluginEnabled: (pluginId: string, enabled: boolean) => void;
}

export const PLUGIN_MARKETPLACE_STORAGE_KEY =
  "agent-client.plugin-marketplace-preferences.v1";

const EMPTY_PLUGIN_PREFERENCES: PluginLocalPreferences = {};
const MAX_PLUGIN_ID_LENGTH = 64;
const KNOWN_PLUGIN_IDS: ReadonlySet<string> = new Set(
  PLUGIN_CATALOG.map((plugin) => plugin.pluginId),
);

/**
 * 判断插件 ID 是否属于当前构建内固定且有界的目录。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function isKnownPluginId(pluginId: string): boolean {
  return (
    pluginId.length > 0 &&
    pluginId.length <= MAX_PLUGIN_ID_LENGTH &&
    KNOWN_PLUGIN_IDS.has(pluginId)
  );
}

/**
 * 判断未知值是否为闭合的插件本地偏好记录。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function isPluginLocalPreference(value: unknown): value is PluginLocalPreference {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return false;
  }
  const candidate = value as Record<string, unknown>;
  return (
    candidate.installed === true && typeof candidate.enabled === "boolean"
  );
}

/**
 * 从同源存储读取经过字段校验的设备本地插件偏好。
 *
 * 输出：只含插件目录 ID、安装状态和启用状态；损坏记录会被丢弃。
 * 不变量：不读取 credential、工具授权、会话、插件代码或 Provider 配置。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function readStoredPluginPreferences(): PluginLocalPreferences {
  if (typeof window === "undefined") {
    return EMPTY_PLUGIN_PREFERENCES;
  }
  try {
    const source = window.localStorage.getItem(PLUGIN_MARKETPLACE_STORAGE_KEY);
    if (!source) {
      return EMPTY_PLUGIN_PREFERENCES;
    }
    const value: unknown = JSON.parse(source);
    if (!value || typeof value !== "object" || Array.isArray(value)) {
      return EMPTY_PLUGIN_PREFERENCES;
    }
    const plugins = (value as Record<string, unknown>).plugins;
    if (!plugins || typeof plugins !== "object" || Array.isArray(plugins)) {
      return EMPTY_PLUGIN_PREFERENCES;
    }

    return Object.fromEntries(
      Object.entries(plugins)
        .filter(
          ([pluginId, preference]) =>
            isKnownPluginId(pluginId) && isPluginLocalPreference(preference),
        )
        .slice(0, PLUGIN_CATALOG.length)
        .map(([pluginId, preference]) => [
          pluginId,
          {
            installed: true,
            enabled: (preference as PluginLocalPreference).enabled,
          },
        ]),
    );
  } catch {
    return EMPTY_PLUGIN_PREFERENCES;
  }
}

/**
 * 原子替换同源浏览器中的非敏感插件市场偏好快照。
 *
 * 输入：已经过类型约束的本地偏好；失败时保留当前内存状态。
 * 不变量：不得写入 secret、工具授权、插件包内容或 application service 数据。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function writeStoredPluginPreferences(plugins: PluginLocalPreferences): void {
  if (typeof window === "undefined") {
    return;
  }
  try {
    const preferences: StoredPluginMarketplacePreferences = { plugins };
    window.localStorage.setItem(
      PLUGIN_MARKETPLACE_STORAGE_KEY,
      JSON.stringify(preferences),
    );
  } catch {
    // 存储不可用时，本次进程内的插件偏好仍然有效。
  }
}

const storedPluginPreferences = readStoredPluginPreferences();

/**
 * 保存设备本地插件安装与启用偏好，不承载实际插件实现或 capability。
 *
 * 不变量：启用偏好本身不能扩大 initialize 广告的工具集合。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export const usePluginMarketplaceStore = create<PluginMarketplaceState>((set) => ({
  plugins: storedPluginPreferences,
  installPlugin: (pluginId) =>
    set((state) => {
      if (!isKnownPluginId(pluginId)) {
        return state;
      }
      const plugins = {
        ...state.plugins,
        [pluginId]: { installed: true, enabled: false },
      };
      writeStoredPluginPreferences(plugins);
      return { plugins };
    }),
  uninstallPlugin: (pluginId) =>
    set((state) => {
      if (!isKnownPluginId(pluginId)) {
        return state;
      }
      const plugins = { ...state.plugins };
      delete plugins[pluginId];
      writeStoredPluginPreferences(plugins);
      return { plugins };
    }),
  setPluginEnabled: (pluginId, enabled) =>
    set((state) => {
      if (!isKnownPluginId(pluginId) || !state.plugins[pluginId]?.installed) {
        return state;
      }
      const plugins = {
        ...state.plugins,
        [pluginId]: { installed: true, enabled },
      };
      writeStoredPluginPreferences(plugins);
      return { plugins };
    }),
}));

/**
 * 清空插件市场内存状态，供隔离测试和显式设备偏好重置使用。
 *
 * 不变量：不会操作 application service 或删除其他 localStorage 项。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function resetPluginMarketplacePreferences(): void {
  usePluginMarketplaceStore.setState({ plugins: EMPTY_PLUGIN_PREFERENCES });
}
