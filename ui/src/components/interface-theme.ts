import { createContext, useContext } from "react";

export type InterfaceTheme = "light" | "dark" | "system";

export interface InterfaceThemeContextValue {
  readonly theme: InterfaceTheme;
  readonly setTheme: (theme: InterfaceTheme) => void;
}

export const INTERFACE_THEME_STORAGE_KEY =
  "agent-client.interface-theme.v1";
export const DEFAULT_INTERFACE_THEME: InterfaceTheme = "system";
export const InterfaceThemeContext =
  createContext<InterfaceThemeContextValue | null>(null);

/**
 * 判断未知值是否为界面支持的闭合主题值。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function isInterfaceTheme(value: unknown): value is InterfaceTheme {
  return value === "light" || value === "dark" || value === "system";
}

/**
 * 从同源存储读取经过白名单校验的主题，损坏值会立即回退并被替换。
 *
 * 输出：只可能是 light、dark 或 system；存储不可用时返回 system。
 * 不变量：未经校验的值不会传入 next-themes 或成为文档 class。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function readStoredInterfaceTheme(): InterfaceTheme {
  if (typeof window === "undefined") {
    return DEFAULT_INTERFACE_THEME;
  }
  try {
    const value = window.localStorage.getItem(INTERFACE_THEME_STORAGE_KEY);
    if (isInterfaceTheme(value)) {
      return value;
    }
    if (value !== null) {
      window.localStorage.setItem(
        INTERFACE_THEME_STORAGE_KEY,
        DEFAULT_INTERFACE_THEME,
      );
    }
  } catch {
    // 存储不可用时仍以安全默认主题继续初始化界面。
  }
  return DEFAULT_INTERFACE_THEME;
}

/**
 * 保存经过类型约束的非敏感主题偏好。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function writeStoredInterfaceTheme(theme: InterfaceTheme): void {
  if (typeof window === "undefined") {
    return;
  }
  try {
    window.localStorage.setItem(INTERFACE_THEME_STORAGE_KEY, theme);
  } catch {
    // 存储不可用不影响当前进程内主题。
  }
}

/**
 * 返回经过白名单约束的界面主题状态与更新入口。
 *
 * 失败：只能在 InterfaceProviders 子树中调用，否则抛出开发期配置错误。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function useInterfaceTheme(): InterfaceThemeContextValue {
  const context = useContext(InterfaceThemeContext);
  if (!context) {
    throw new Error("useInterfaceTheme must be used within InterfaceProviders");
  }
  return context;
}
