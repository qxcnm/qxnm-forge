import "@testing-library/jest-dom/vitest";
import { cleanup } from "@testing-library/react";
import { afterEach, vi } from "vitest";

/**
 * 为 jsdom 补充 Radix Switch 计算隐藏输入尺寸所需的只读观察器。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
class ResizeObserverStub implements ResizeObserver {
  /**
   * jsdom 不产生布局变化，因此无需释放观察状态。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public disconnect(): void {}

  /**
   * 接受观察请求但不产生虚构尺寸回调。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public observe(): void {}

  /**
   * 接受取消观察请求但不维护额外状态。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  public unobserve(): void {}
}

globalThis.ResizeObserver = ResizeObserverStub;

Object.defineProperty(Element.prototype, "scrollIntoView", {
  configurable: true,
  value: vi.fn(),
});

afterEach(cleanup);
