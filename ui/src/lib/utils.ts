import { clsx, type ClassValue } from "clsx";
import { twMerge } from "tailwind-merge";

/**
 * 合并条件类名并消解 Tailwind 冲突。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function cn(...inputs: ClassValue[]): string {
  return twMerge(clsx(inputs));
}
