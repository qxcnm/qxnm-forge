import { Check, Circle, LoaderCircle, TerminalSquare } from "lucide-react";

import { ScrollArea } from "@/components/ui/scroll-area";
import {
  ACTIVITY_FIXTURES,
  CHANGED_FILES,
  COMMAND_LOG_FIXTURES,
} from "@/data/workspace-fixtures";

export interface SubmittedMessage {
  readonly id: string;
  readonly role: "user" | "assistant" | "status";
  readonly content: string;
}

interface ConversationProps {
  readonly backendLabel: string;
  readonly messages: readonly SubmittedMessage[];
  readonly busy: boolean;
  readonly showFixture: boolean;
  readonly historyLoading: boolean;
  readonly historyError: boolean;
}

/**
 * 呈现由服务快照和事件投影而来的会话内容。
 *
 * 当前静态活动来自 faux fixture，不读取 Session journal 或宿主文件。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function Conversation({
  backendLabel,
  messages,
  busy,
  showFixture,
  historyLoading,
  historyError,
}: ConversationProps) {
  return (
    <ScrollArea className="min-h-0 flex-1 bg-white">
      <div className="mx-auto w-full max-w-[760px] px-5 pb-8 pt-8 sm:px-8 sm:pt-12">
        {showFixture ? (
          <>
            <section aria-labelledby="task-heading">
              <div className="mb-7 flex items-start gap-3">
                <div className="mt-0.5 flex size-7 shrink-0 items-center justify-center rounded-md bg-stone-900 text-white">
                  <TerminalSquare className="size-3.5" aria-hidden="true" />
                </div>
                <div className="min-w-0">
                  <h2
                    id="task-heading"
                    className="text-[13px] font-semibold leading-5 text-stone-900"
                  >
                    构建 QXNM Forge 跨平台工作台
                  </h2>
                  <p className="mt-0.5 text-[11px] leading-4 text-stone-400">
                    React · shadcn/ui · Tauri 2
                  </p>
                </div>
              </div>

              <div className="mb-6 space-y-2" aria-label="只读执行日志">
                {COMMAND_LOG_FIXTURES.map((command) => (
                  <div
                    key={command}
                    className="flex min-w-0 items-center gap-2 text-[10px] leading-4 text-stone-400"
                  >
                    <TerminalSquare
                      className="size-3 shrink-0 text-stone-300"
                      aria-hidden="true"
                    />
                    <span className="truncate font-mono">{command}</span>
                  </div>
                ))}
              </div>

              <p className="text-[13px] leading-5 text-stone-800">
                我会先确认现有协议与 Open/Pro 边界，再建立可切换 Rust 或 .NET 的中立客户端。
                桌面端由 Tauri 壳承载，Android 保留同一套响应式界面和远程服务边界。
              </p>

              <div className="mt-6 space-y-4" aria-label="执行活动">
                {ACTIVITY_FIXTURES.map((activity) => {
                  const ActivityIcon = activity.icon;
                  return (
                    <div
                      key={activity.id}
                      className="flex items-start gap-3 text-[11px] leading-4"
                    >
                      <div className="flex size-5 shrink-0 items-center justify-center">
                        {activity.state === "completed" ? (
                          <Check
                            className="size-3.5 text-emerald-600"
                            aria-hidden="true"
                          />
                        ) : activity.state === "running" ? (
                          <LoaderCircle
                            className="size-3.5 animate-spin text-sky-600"
                            aria-hidden="true"
                          />
                        ) : (
                          <Circle className="size-2.5 text-stone-300" aria-hidden="true" />
                        )}
                      </div>
                      <ActivityIcon
                        className="mt-0.5 size-3.5 shrink-0 text-stone-400"
                        aria-hidden="true"
                      />
                      <div className="min-w-0 flex-1">
                        <p className="font-medium text-stone-700">{activity.label}</p>
                        <p className="mt-0.5 truncate text-stone-400">{activity.detail}</p>
                      </div>
                    </div>
                  );
                })}
              </div>

              <div className="my-7 flex items-center gap-3 text-[10px] text-stone-400">
                <span className="h-px flex-1 bg-stone-200" />
                <span>完成基础界面检查</span>
                <span className="h-px flex-1 bg-stone-200" />
              </div>

              <div className="space-y-3 text-[13px] leading-5 text-stone-800">
                <p>
                  工作台已经按 Codex 的信息密度组织：项目导航固定在左侧，任务记录保持窄行宽，
                  输入区贴近底部。后端切换只改变下一次 application service 初始化，不接触数据库或凭据。
                </p>
                <p>
                  当前连接画像为 <span className="font-medium text-stone-950">{backendLabel}</span>。
                  控件是否出现继续以服务端返回的{" "}
                  <code className="font-mono text-[11px] text-sky-700">capabilities</code> 为准。
                </p>
              </div>
            </section>

            <section
              className="mt-8 overflow-hidden rounded-lg bg-[#f6f6f5]"
              aria-label="变更摘要"
            >
              <div className="flex h-9 items-center border-b border-stone-200/70 px-3 text-[11px] text-stone-600">
                <span>{CHANGED_FILES.length} 个文件已变更</span>
              </div>
              {CHANGED_FILES.map((file) => {
                const FileIcon = file.icon;
                return (
                  <div
                    key={file.path}
                    className="flex h-9 items-center gap-2 px-3 text-[11px] text-stone-700"
                  >
                    <FileIcon className="size-3.5 text-stone-400" aria-hidden="true" />
                    <span className="min-w-0 flex-1 truncate font-mono text-[10px]">
                      {file.path}
                    </span>
                    <span className="text-emerald-600">+{file.additions}</span>
                    <span className="text-rose-500">-{file.deletions}</span>
                  </div>
                );
              })}
            </section>
          </>
        ) : null}

        {messages.length > 0 ? (
          <section className="mt-8 space-y-5" aria-label="新消息">
            {messages.map((message) => (
              <div
                key={message.id}
                className={
                  message.role === "user"
                    ? "ml-auto max-w-[86%] rounded-lg bg-stone-100 px-3 py-2.5 text-[13px] leading-5 text-stone-800"
                    : message.role === "status"
                      ? "text-[11px] text-stone-400"
                      : "text-[13px] leading-5 text-stone-800"
                }
              >
                {message.content}
              </div>
            ))}
          </section>
        ) : null}

        {historyLoading ? (
          <div className="flex items-center gap-2 py-8 text-[11px] text-stone-400" role="status">
            <LoaderCircle className="size-3.5 animate-spin" aria-hidden="true" />
            正在读取会话记录
          </div>
        ) : historyError ? (
          <p className="py-8 text-[11px] text-red-600" role="alert">
            无法读取会话记录
          </p>
        ) : !showFixture && messages.length === 0 && !busy ? (
          <p className="py-8 text-[11px] text-stone-400">暂无会话消息</p>
        ) : null}

        {busy ? (
          <div className="mt-6 flex items-center gap-2 text-[11px] text-stone-400" role="status">
            <LoaderCircle className="size-3.5 animate-spin" aria-hidden="true" />
            正在等待 application service 接受运行
          </div>
        ) : null}

        <div className="h-6 sm:h-10" />
      </div>
    </ScrollArea>
  );
}
