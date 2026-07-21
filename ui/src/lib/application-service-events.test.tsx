import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { isTauriMock, listenMock } = vi.hoisted(() => ({
  isTauriMock: vi.fn(),
  listenMock: vi.fn(),
}));

vi.mock("@tauri-apps/api/core", () => ({
  isTauri: isTauriMock,
}));

vi.mock("@tauri-apps/api/event", () => ({
  listen: listenMock,
}));

import {
  subscribeToApplicationServiceEvents,
  useApplicationServiceEvents,
} from "@/lib/application-service-events";
import type { BackendKind, SessionReplayEvent } from "@/types/application-service";

interface TauriEventProjection {
  readonly payload: unknown;
}

const RUN_STARTED_EVENT = {
  sessionId: "session-1",
  runId: "run-1",
  seq: 1,
  time: "2026-07-21T00:00:00Z",
  type: "run.started",
  data: {},
} as const satisfies SessionReplayEvent;

/**
 * 生成 Tauri application-service-event 的合法 payload。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function createPayload(
  backend: BackendKind,
  event: SessionReplayEvent = RUN_STARTED_EVENT,
): unknown {
  return {
    backend,
    notification: {
      jsonrpc: "2.0",
      method: "event",
      params: event,
    },
  };
}

describe("application service event bridge", () => {
  beforeEach(() => {
    isTauriMock.mockReset();
    listenMock.mockReset();
    isTauriMock.mockReturnValue(true);
  });

  /**
   * 验证浏览器预览不会尝试注册 Tauri 监听。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("does not register a listener outside Tauri", async () => {
    isTauriMock.mockReturnValue(false);
    const onEvent = vi.fn();

    const unlisten = await subscribeToApplicationServiceEvents("rust", onEvent);
    unlisten();

    expect(listenMock).not.toHaveBeenCalled();
    expect(onEvent).not.toHaveBeenCalled();
  });

  /**
   * 验证事件桥只把匹配当前后端且通过严格 Schema 的事件交给调用方。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("delivers only valid events for the selected backend", async () => {
    let listener: ((event: TauriEventProjection) => void) | undefined;
    const hostUnlisten = vi.fn();
    listenMock.mockImplementation(
      (_eventName: string, registeredListener: (event: TauriEventProjection) => void) => {
        listener = registeredListener;
        return Promise.resolve(hostUnlisten);
      },
    );
    const onEvent = vi.fn();

    const unlisten = await subscribeToApplicationServiceEvents("rust", onEvent);
    expect(listenMock).toHaveBeenCalledWith("application-service-event", expect.any(Function));

    listener?.({ payload: createPayload("dotnet") });
    listener?.({ payload: createPayload("rust") });

    expect(onEvent).toHaveBeenCalledOnce();
    expect(onEvent).toHaveBeenCalledWith(RUN_STARTED_EVENT);

    unlisten();
    expect(hostUnlisten).toHaveBeenCalledOnce();
  });

  /**
   * 验证伪造 notification、未知字段和事件不变量错误均被丢弃。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("rejects malformed envelopes and replay events", async () => {
    let listener: ((event: TauriEventProjection) => void) | undefined;
    listenMock.mockImplementation(
      (_eventName: string, registeredListener: (event: TauriEventProjection) => void) => {
        listener = registeredListener;
        return Promise.resolve(vi.fn());
      },
    );
    const onEvent = vi.fn();
    await subscribeToApplicationServiceEvents("rust", onEvent);

    listener?.({
      payload: {
        ...createPayload("rust") as Record<string, unknown>,
        injected: true,
      },
    });
    listener?.({
      payload: {
        backend: "rust",
        notification: { jsonrpc: "1.0", method: "event", params: RUN_STARTED_EVENT },
      },
    });
    listener?.({
      payload: createPayload("rust", {
        ...RUN_STARTED_EVENT,
        seq: 0,
      }),
    });
    listener?.({
      payload: createPayload("rust", {
        ...RUN_STARTED_EVENT,
        turnId: "turn-not-allowed-on-run-events",
      }),
    });

    expect(onEvent).not.toHaveBeenCalled();
  });

  /**
   * 验证宿主拒绝监听注册时以空清理器降级而不向调用方抛错。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("fails closed when Tauri listener registration rejects", async () => {
    listenMock.mockRejectedValue(new Error("host unavailable"));

    await expect(
      subscribeToApplicationServiceEvents("dotnet", vi.fn()),
    ).resolves.toEqual(expect.any(Function));
  });

  /**
   * 验证 hook 卸载发生在异步注册完成前时仍会释放随后得到的监听器。
   *
   * 作者：高宏顺
   * 邮箱：18272669457@163.com
   */
  it("unlistens a late registration after hook unmount", async () => {
    const hostUnlisten = vi.fn();
    let resolveListen: ((unlisten: () => void) => void) | undefined;
    listenMock.mockReturnValue(
      new Promise<() => void>((resolve) => {
        resolveListen = resolve;
      }),
    );
    const { unmount } = renderHook(() => useApplicationServiceEvents("rust", vi.fn()));

    expect(listenMock).toHaveBeenCalledOnce();
    unmount();
    act(() => {
      resolveListen?.(hostUnlisten);
    });

    await waitFor(() => expect(hostUnlisten).toHaveBeenCalledOnce());
  });
});
