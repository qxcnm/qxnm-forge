import {
  useCallback,
  useEffect,
  useState,
  type ReactNode,
} from "react";
import { ThemeProvider, useTheme as useNextTheme } from "next-themes";
import { useTranslation } from "react-i18next";

import {
  DEFAULT_INTERFACE_THEME,
  INTERFACE_THEME_STORAGE_KEY,
  InterfaceThemeContext,
  isInterfaceTheme,
  readStoredInterfaceTheme,
  writeStoredInterfaceTheme,
  type InterfaceTheme,
} from "@/components/interface-theme";

interface InterfaceProvidersProps {
  readonly children: ReactNode;
}

interface InterfaceThemeSynchronizerProps {
  readonly children: ReactNode;
}

/**
 * 将当前 i18next 语言同步到文档根节点，供辅助技术与原生壳正确识别。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function DocumentLanguageSynchronizer() {
  const { i18n } = useTranslation();

  useEffect(() => {
    document.documentElement.lang = i18n.resolvedLanguage ?? "zh-CN";
  }, [i18n.resolvedLanguage]);

  return null;
}

/**
 * 提供可持久化的系统主题解析与界面语言文档同步边界。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function InterfaceProviders({ children }: InterfaceProvidersProps) {
  const [initialTheme] = useState<InterfaceTheme>(readStoredInterfaceTheme);

  return (
    <ThemeProvider
      attribute="class"
      defaultTheme={initialTheme}
      enableSystem
      disableTransitionOnChange
      storageKey={INTERFACE_THEME_STORAGE_KEY}
    >
      <InterfaceThemeSynchronizer>
        <DocumentLanguageSynchronizer />
        {children}
      </InterfaceThemeSynchronizer>
    </ThemeProvider>
  );
}

/**
 * 将白名单主题偏好桥接到 next-themes，并收敛跨窗口的损坏存储事件。
 *
 * 不变量：system 由 next-themes 解析并持续监听操作系统配色，不使用 forcedTheme。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function InterfaceThemeSynchronizer({
  children,
}: InterfaceThemeSynchronizerProps) {
  const { theme: managedTheme, setTheme: setManagedTheme } = useNextTheme();
  const theme = isInterfaceTheme(managedTheme)
    ? managedTheme
    : DEFAULT_INTERFACE_THEME;

  /**
   * 更新当前主题，并让 next-themes 写入品牌中立的版本化偏好 key。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  const setTheme = useCallback((nextTheme: InterfaceTheme) => {
    writeStoredInterfaceTheme(nextTheme);
    setManagedTheme(nextTheme);
  }, [setManagedTheme]);

  useEffect(() => {
    /**
     * 接收其他窗口的主题变化，并在 next-themes 处理前阻止损坏值到达文档 class。
     *
     * 作者：高宏顺
     * 邮箱：18272669457@163.com
     */
    const handleThemeStorage = (event: StorageEvent) => {
      if (
        event.key !== null &&
        event.key !== INTERFACE_THEME_STORAGE_KEY
      ) {
        return;
      }
      const nextTheme = isInterfaceTheme(event.newValue)
        ? event.newValue
        : DEFAULT_INTERFACE_THEME;
      if (event.newValue !== null && !isInterfaceTheme(event.newValue)) {
        event.stopImmediatePropagation();
      }
      if (event.newValue !== null && !isInterfaceTheme(event.newValue)) {
        writeStoredInterfaceTheme(DEFAULT_INTERFACE_THEME);
      }
      setManagedTheme(nextTheme);
    };

    window.addEventListener("storage", handleThemeStorage, { capture: true });
    return () =>
      window.removeEventListener("storage", handleThemeStorage, { capture: true });
  }, [setManagedTheme]);

  return (
    <InterfaceThemeContext.Provider value={{ theme, setTheme }}>
      {children}
    </InterfaceThemeContext.Provider>
  );
}
