import { isTauri } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useEffect, useRef } from "react";
import { z } from "zod";

import type { BackendKind, SessionReplayEvent } from "@/types/application-service";

const APPLICATION_SERVICE_EVENT_NAME = "application-service-event";
const OPAQUE_ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._:-]*$/;
const RUN_LEVEL_EVENT_TYPES = new Set<SessionReplayEvent["type"]>([
  "run.started",
  "run.completed",
  "run.failed",
  "run.cancelled",
  "run.interrupted",
]);

const opaqueIdSchema = z.string().max(128).regex(OPAQUE_ID_PATTERN);
const jsonObjectSchema = z.record(z.string(), z.unknown());
const replayEventSchema = z
  .object({
    sessionId: opaqueIdSchema,
    runId: opaqueIdSchema,
    turnId: opaqueIdSchema.optional(),
    seq: z.number().int().positive().max(Number.MAX_SAFE_INTEGER),
    time: z.iso.datetime({ offset: false }),
    type: z.enum([
      "run.started",
      "turn.started",
      "turn.completed",
      "message.started",
      "message.delta",
      "message.completed",
      "tool.requested",
      "approval.requested",
      "approval.resolved",
      "tool.started",
      "tool.delta",
      "tool.completed",
      "retry.scheduled",
      "context.compacted",
      "run.completed",
      "run.failed",
      "run.cancelled",
      "run.interrupted",
    ]),
    data: jsonObjectSchema,
    extensions: jsonObjectSchema.optional(),
  })
  .strict()
  .superRefine((event, context) => {
    const isRunLevel = RUN_LEVEL_EVENT_TYPES.has(event.type);
    if ((isRunLevel && event.turnId !== undefined) || (!isRunLevel && event.turnId === undefined)) {
      context.addIssue({
        code: "custom",
        path: ["turnId"],
        message: "事件 turnId 与类型不一致",
      });
    }
  });
const applicationServiceEventPayloadSchema = z
  .object({
    backend: z.enum(["rust", "dotnet"]),
    notification: z
      .object({
        jsonrpc: z.literal("2.0"),
        method: z.literal("event"),
        params: replayEventSchema,
      })
      .strict(),
  })
  .strict();

/**
 * 提供浏览器预览或监听失败时可安全调用的空清理器。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
function noopUnlisten(): void {}

/**
 * 订阅 Tauri 壳转发的 application service durable 事件。
 *
 * 输入：当前后端以及仅接收该后端合法 SessionReplayEvent 的回调。
 * 输出：可重复调用语义由 Tauri 管理的监听清理器；非 Tauri 或注册失败时为空清理器。
 * 不变量：只接受精确的 JSON-RPC event notification，且不会把另一后端或未通过 Schema 的数据交给回调。
 * 失败：宿主监听注册失败时安全降级，不抛出错误且不创建虚构事件。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export async function subscribeToApplicationServiceEvents(
  backend: BackendKind,
  onEvent: (event: SessionReplayEvent) => void,
): Promise<() => void> {
  if (!isTauri()) {
    return noopUnlisten;
  }

  try {
    return await listen<unknown>(APPLICATION_SERVICE_EVENT_NAME, ({ payload }) => {
      const result = applicationServiceEventPayloadSchema.safeParse(payload);
      if (!result.success || result.data.backend !== backend) {
        return;
      }

      onEvent(result.data.notification.params);
    });
  } catch {
    return noopUnlisten;
  }
}

/**
 * 在 React 生命周期内订阅当前后端的 application service durable 事件。
 *
 * 输入：当前后端和事件消费回调；回调更新不会重复注册宿主监听。
 * 输出：无；组件卸载或后端切换时自动调用 unlisten。
 * 不变量：异步注册晚于清理完成时会立即撤销该监听，不留下跨后端订阅。
 * 失败：非 Tauri 环境或监听失败时保持无事件状态，不影响组件渲染。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function useApplicationServiceEvents(
  backend: BackendKind,
  onEvent: (event: SessionReplayEvent) => void,
): void {
  const onEventRef = useRef(onEvent);

  useEffect(() => {
    onEventRef.current = onEvent;
  }, [onEvent]);

  useEffect(() => {
    let active = true;
    let unlisten = noopUnlisten;

    void subscribeToApplicationServiceEvents(backend, (event) => {
      onEventRef.current(event);
    }).then((registeredUnlisten) => {
      if (!active) {
        registeredUnlisten();
        return;
      }

      unlisten = registeredUnlisten;
    });

    return () => {
      active = false;
      unlisten();
    };
  }, [backend]);
}
