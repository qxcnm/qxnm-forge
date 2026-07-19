#!/usr/bin/env python3
"""仅用于工具/审批黑盒 runner 自测的确定性 portable daemon。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import threading
from typing import Any


SESSION_ID = "tool-conformance-session-1"
RUN_ID = "tool-conformance-run-1"
PROVIDER = {"id": "faux", "modelId": "faux-v1"}
TIME = "2026-07-11T04:00:00Z"


class FakeToolDaemon:
    """保存 fake daemon 的 journal、协议、审批等待器和确定性上下文。"""

    def __init__(self, *, late_tool_result: bool = False) -> None:
        """功能：从 runner 注入环境初始化临时路径、锁与未配置状态。

        输入：只读取 QXNM_FORGE_WORKSPACE/QXNM_FORGE_SESSION_ROOT 及测试负例开关。
        输出：可处理 initialize/configure/run/control 的 fake daemon。
        不变量：所有文件访问位于 runner 创建的临时根；不启动进程或网络。
        失败：环境缺失时抛出 RuntimeError 并由测试 runner 安全报告。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        workspace = os.environ.get("QXNM_FORGE_WORKSPACE")
        state_root = os.environ.get("QXNM_FORGE_SESSION_ROOT")
        if not workspace or not state_root:
            raise RuntimeError("missing tool conformance roots")
        self.workspace = Path(workspace).resolve(strict=True)
        self.state_root = Path(state_root).resolve(strict=True)
        self.journal_path = self.state_root / "sessions" / SESSION_ID / "journal.jsonl"
        self.output_lock = threading.Lock()
        self.journal_lock = threading.Lock()
        self.state_lock = threading.Lock()
        self.record_seq = 0
        self.event_seq = 0
        self.parent_id: str | None = None
        self.scenario: dict[str, Any] | None = None
        self.scenario_record_id: str | None = None
        self.interactive = False
        self.pending: dict[str, Any] | None = None
        self.worker: threading.Thread | None = None
        self.context: list[dict[str, Any]] = []
        self.cancellation_requested = False
        self.terminal = False
        self.late_tool_result = late_tool_result

    def emit(self, value: dict[str, Any]) -> None:
        """功能：以输出锁写一个紧凑 UTF-8 NDJSON 协议帧并立即刷新。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        encoded = json.dumps(value, ensure_ascii=False, separators=(",", ":"))
        with self.output_lock:
            sys.stdout.write(encoded + "\n")
            sys.stdout.flush()

    def ensure_journal(self) -> None:
        """功能：创建嵌套 Rust 式 Session 布局和 durable portable header。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with self.journal_lock:
            if self.journal_path.exists():
                return
            self.journal_path.parent.mkdir(parents=True, exist_ok=True)
            header = {
                "kind": "session",
                "schemaVersion": "0.1",
                "sessionId": SESSION_ID,
                "createdAt": TIME,
                "workspace": str(self.workspace),
                "createdBy": {
                    "name": "tool-conformance-fake-daemon",
                    "version": "0.1.0+test",
                    "language": "python",
                },
            }
            self._write_line_locked(header)

    def _write_line_locked(self, value: dict[str, Any]) -> None:
        """功能：在 journal 锁内追加一行并执行 flush/fsync durability primitive。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        data = (
            json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
            + b"\n"
        )
        with self.journal_path.open("ab") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())

    def append_record(self, kind: str, data: dict[str, Any]) -> dict[str, Any]:
        """功能：按连续 seq/parent 链 durable 追加一个 portable journal record。

        输入：公共 core kind 与符合对应 Schema 的 data 对象。
        输出：刚持久化的完整 record，供后续绑定引用。
        不变量：recordId 唯一，seq 连续，parentId 指向前一条记录。
        失败：序列化或 fsync 错误传播并终止 fake 测试。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.ensure_journal()
        with self.journal_lock:
            self.record_seq += 1
            record_id = f"tool-record-{self.record_seq:06d}"
            record = {
                "schemaVersion": "0.1",
                "kind": kind,
                "recordId": record_id,
                "sessionId": SESSION_ID,
                "seq": self.record_seq,
                "parentId": self.parent_id,
                "time": TIME,
                "data": data,
            }
            self._write_line_locked(record)
            self.parent_id = record_id
            return record

    def emit_event(
        self,
        event_type: str,
        data: dict[str, Any],
        *,
        turn_id: str | None,
    ) -> dict[str, Any]:
        """功能：先持久化 exact event.emitted，再把同一 envelope 写到 stdout。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.event_seq += 1
        event: dict[str, Any] = {
            "sessionId": SESSION_ID,
            "runId": RUN_ID,
            "seq": self.event_seq,
            "time": TIME,
            "type": event_type,
            "data": data,
        }
        if turn_id is not None:
            event["turnId"] = turn_id
        self.append_record("event.emitted", {"event": event})
        frame = {"jsonrpc": "2.0", "method": "event", "params": event}
        self.emit(frame)
        return frame

    def initialize(self, request: dict[str, Any]) -> None:
        """功能：响应 initialize 并诚实声明 fake runner 实际覆盖的能力。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        capabilities = request.get("params", {}).get("capabilities", {})
        self.interactive = capabilities.get("interactiveApprovals") is True
        self.emit(
            {
                "jsonrpc": "2.0",
                "id": request["id"],
                "result": {
                    "protocolVersion": "0.1",
                    "implementation": {
                        "name": "tool-conformance-fake-daemon",
                        "version": "0.1.0+test",
                        "language": "python",
                    },
                    "capabilities": {
                        "methods": [
                            "initialize",
                            "faux/configure",
                            "run/start",
                            "run/cancel",
                            "approval/respond",
                        ],
                        "eventTypes": [
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
                            "tool.completed",
                            "run.completed",
                            "run.failed",
                            "run.cancelled",
                            "run.interrupted",
                        ],
                        "providers": [{"id": "faux", "models": ["faux-v1"]}],
                        "tools": ["file.read", "file.write"],
                        "transports": ["stdio"],
                    },
                    "limits": {
                        "maxFrameBytes": 1_048_576,
                        "maxEventBytes": 262_144,
                        "maxArtifactBytes": 1_073_741_824,
                        "maxConcurrentRuns": 1,
                    },
                },
            }
        )

    def configure(self, request: dict[str, Any]) -> None:
        """功能：append-before-ack 保存受治理 faux 场景供下一 run 独占消费。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        scenario = request["params"]["scenario"]
        scenario_id = "faux:" + scenario["name"]
        record = self.append_record(
            "faux.configured", {"scenarioId": scenario_id, "scenario": scenario}
        )
        self.scenario = scenario
        self.scenario_record_id = record["recordId"]
        self.emit(
            {
                "jsonrpc": "2.0",
                "id": request["id"],
                "result": {"scenarioId": scenario_id},
            }
        )

    def start_run(self, request: dict[str, Any]) -> None:
        """功能：先持久化用户消息和 run.accepted、响应 runId，再启动 worker。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if self.scenario is None or self.scenario_record_id is None:
            raise RuntimeError("faux scenario is not configured")
        input_message = request["params"]["input"]
        user_message = {
            "messageId": "tool-user-message-1",
            "role": "user",
            "content": input_message["content"],
            "time": TIME,
        }
        self.append_record(
            "message.appended", {"message": user_message, "runId": RUN_ID}
        )
        self.append_record(
            "run.accepted",
            {
                "runId": RUN_ID,
                "inputMessageId": user_message["messageId"],
                "provider": PROVIDER,
                "fauxScenarioRecordId": self.scenario_record_id,
            },
        )
        self.context = [{"role": "user", "content": input_message["content"]}]
        self.emit({"jsonrpc": "2.0", "id": request["id"], "result": {"runId": RUN_ID}})
        self.worker = threading.Thread(
            target=self.run_worker,
            name="fake-tool-run-worker",
            daemon=True,
        )
        self.worker.start()

    def resolve_approval(self, request: dict[str, Any]) -> None:
        """功能：durable 记录 client decision、先响应 accepted，再唤醒工具 worker。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with self.state_lock:
            pending = self.pending
            if pending is None:
                raise RuntimeError("approval is not pending")
            if (
                request["params"].get("runId") != RUN_ID
                or request["params"].get("approvalId")
                != pending["approval"]["approvalId"]
            ):
                raise RuntimeError("approval identifiers differ")
            decision = request["params"]["decision"]
            resolution = {
                "runId": RUN_ID,
                "approvalId": pending["approval"]["approvalId"],
                "decision": decision,
                "resolutionSource": "client",
            }
            self.append_record("approval.resolved", resolution)
            pending["resolution"] = resolution
        self.emit({"jsonrpc": "2.0", "id": request["id"], "result": {"accepted": True}})
        pending["ready"].set()

    def cancel_run(self, request: dict[str, Any]) -> None:
        """功能：持久化一次取消 intent 和 pending approval deny 后先响应再唤醒 worker。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with self.state_lock:
            if self.cancellation_requested:
                state = "terminal" if self.terminal else "alreadyRequested"
                self.emit(
                    {
                        "jsonrpc": "2.0",
                        "id": request["id"],
                        "result": {"cancellationState": state},
                    }
                )
                return
            self.cancellation_requested = True
            pending = self.pending
            if pending is None:
                raise RuntimeError("run is not waiting for approval")
        self.append_record("run.cancellation_requested", {"runId": RUN_ID})
        with self.state_lock:
            resolution = {
                "runId": RUN_ID,
                "approvalId": pending["approval"]["approvalId"],
                "decision": {"choice": "deny"},
                "resolutionSource": "cancellation",
            }
            self.append_record("approval.resolved", resolution)
            pending["resolution"] = resolution
        self.emit(
            {
                "jsonrpc": "2.0",
                "id": request["id"],
                "result": {"cancellationState": "requested"},
            }
        )
        pending["ready"].set()

    def run_worker(self) -> None:
        """功能：执行首轮工具批次、可选审批及 faux continuation 并产生终态。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        assert self.scenario is not None
        scenario = self.scenario
        turn_id = "tool-turn-1"
        self.append_record("run.started", {"runId": RUN_ID})
        self.emit_event("run.started", {}, turn_id=None)
        self.append_record(
            "turn.started", {"runId": RUN_ID, "turnId": turn_id, "attempt": 1}
        )
        self.emit_event("turn.started", {}, turn_id=turn_id)
        assistant_message_id = "tool-assistant-message-1"
        self.emit_event(
            "message.started",
            {"messageId": assistant_message_id, "role": "assistant"},
            turn_id=turn_id,
        )
        tool_steps = [
            step for step in scenario["steps"] if step.get("type") == "tool_call"
        ]
        assistant_content = [dict(step) for step in tool_steps]
        usage = scenario.get("usage", self.zero_usage())
        assistant_message = {
            "messageId": assistant_message_id,
            "role": "assistant",
            "content": assistant_content,
            "provider": PROVIDER,
            "finishReason": "tool_use",
            "usage": usage,
            "time": TIME,
        }
        self.append_record(
            "message.appended",
            {"message": assistant_message, "runId": RUN_ID, "turnId": turn_id},
        )
        self.emit_event(
            "message.completed",
            {
                "messageId": assistant_message_id,
                "finishReason": "tool_use",
                "usage": usage,
            },
            turn_id=turn_id,
        )
        self.context.append({"role": "assistant", "content": assistant_content})

        cancelled = False
        for step in tool_steps:
            result, was_cancelled = self.process_tool(
                step,
                turn_id,
                force_cancelled=cancelled or self.cancellation_requested,
            )
            cancelled = cancelled or was_cancelled
            self.context.append(
                {
                    "role": "tool",
                    "toolCallId": step["toolCallId"],
                    "toolName": step["name"],
                    "isError": result["isError"],
                    "content": result["content"],
                }
            )
        finish_reason = "cancelled" if cancelled else "tool_use"
        self.append_record(
            "turn.completed",
            {"runId": RUN_ID, "turnId": turn_id, "finishReason": finish_reason},
        )
        self.emit_event(
            "turn.completed",
            {"finishReason": finish_reason, "toolResultCount": len(tool_steps)},
            turn_id=turn_id,
        )
        if cancelled:
            self.append_record("run.terminal", {"runId": RUN_ID, "status": "cancelled"})
            with self.state_lock:
                self.terminal = True
            self.emit_event("run.cancelled", {"status": "cancelled"}, turn_id=None)
            return

        total_usage = dict(usage)
        for index, continuation in enumerate(
            scenario.get("continuations", []), start=2
        ):
            total_usage = self.run_continuation(continuation, index, total_usage)
        self.append_record(
            "run.terminal",
            {"runId": RUN_ID, "status": "completed", "usage": total_usage},
        )
        with self.state_lock:
            self.terminal = True
        self.emit_event(
            "run.completed",
            {"status": "completed", "usage": total_usage},
            turn_id=None,
        )

    def process_tool(
        self,
        step: dict[str, Any],
        turn_id: str,
        *,
        force_cancelled: bool = False,
    ) -> tuple[dict[str, Any], bool]:
        """功能：按 lookup、参数、策略、审批顺序处理工具，并为批内取消调用收尾。

        输入：完整工具步骤、所属 turn，以及前序取消是否已成为 durable 事实。
        输出：portable 工具结果与当前调用是否确认取消。
        不变量：force_cancelled 时仍写唯一 intent/result/message/completed，但不创建审批或执行副作用。
        失败：fake journal、事件或受治理文件操作失败时抛出 RuntimeError。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        name = step["name"]
        arguments = step["arguments"]
        tool_call_id = step["toolCallId"]
        valid = self.valid_arguments(name, arguments)
        if force_cancelled:
            status = "denied"
        elif name not in ("file.read", "file.write"):
            status = "rejected"
        elif not valid:
            status = "rejected"
        elif name == "file.write" and self.interactive:
            status = "awaiting_approval"
        elif name == "file.write":
            status = "denied"
        else:
            status = "started"
        intent: dict[str, Any] = {
            "runId": RUN_ID,
            "turnId": turn_id,
            "toolCallId": tool_call_id,
            "name": name,
            "arguments": arguments,
            "idempotent": name == "file.read",
            "status": status,
        }
        operation_hash = self.operation_hash(name, arguments)
        if status == "awaiting_approval":
            intent["operationHash"] = operation_hash
        self.append_record("tool.intent", intent)
        self.emit_event(
            "tool.requested",
            {"toolCallId": tool_call_id, "name": name, "arguments": arguments},
            turn_id=turn_id,
        )

        cancelled = False
        if force_cancelled:
            cancelled = True
            result = self.error_result(
                name,
                "cancelled",
                "cancelled",
                -32007,
                "Tool was cancelled before execution.",
            )
        elif name not in ("file.read", "file.write"):
            result = self.error_result(
                name,
                "validation_error",
                "tool_not_found",
                -32602,
                "Unknown tool.",
            )
        elif not valid:
            result = self.error_result(
                name,
                "validation_error",
                "tool_arguments_invalid",
                -32602,
                "Tool arguments are invalid.",
            )
        elif name == "file.write" and not self.interactive:
            result = self.error_result(
                name,
                "denied",
                "permission_denied",
                -32003,
                "Headless policy denied the tool.",
            )
        elif name == "file.write":
            resolution = self.wait_for_approval(step, turn_id, operation_hash)
            self.emit_event(
                "approval.resolved",
                {
                    "approvalId": resolution["approvalId"],
                    "decision": resolution["decision"],
                    "resolutionSource": resolution["resolutionSource"],
                },
                turn_id=turn_id,
            )
            choice = resolution["decision"]["choice"]
            if resolution["resolutionSource"] == "cancellation":
                cancelled = True
                result = self.error_result(
                    name,
                    "cancelled",
                    "cancelled",
                    -32007,
                    "Tool was cancelled before execution.",
                )
            elif choice == "deny":
                result = self.error_result(
                    name,
                    "denied",
                    "permission_denied",
                    -32003,
                    "Approval denied the tool.",
                )
            else:
                self.emit_event(
                    "tool.started",
                    {"toolCallId": tool_call_id, "name": name},
                    turn_id=turn_id,
                )
                result = self.execute_file_tool(name, arguments)
        else:
            self.emit_event(
                "tool.started",
                {"toolCallId": tool_call_id, "name": name},
                turn_id=turn_id,
            )
            result = self.execute_file_tool(name, arguments)

        self.finalize_tool(step, turn_id, result)
        return result, cancelled

    def wait_for_approval(
        self, step: dict[str, Any], turn_id: str, operation_hash: str
    ) -> dict[str, Any]:
        """功能：持久化审批请求、发事件并等待主线程的 client/cancel resolution。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        approval_id = "tool-approval-1"
        approval = {
            "approvalId": approval_id,
            "toolCallId": step["toolCallId"],
            "operation": step["name"],
            "arguments": step["arguments"],
            "operationHash": operation_hash,
            "risk": "medium",
            "reason": "The tool would modify a workspace file.",
            "resources": [{"kind": "path", "value": step["arguments"]["path"]}],
            "choices": ["allow_once", "deny"],
            "expiresAt": "2037-10-21T07:28:00Z",
        }
        ready = threading.Event()
        pending = {"approval": approval, "ready": ready, "resolution": None}
        with self.state_lock:
            self.pending = pending
        self.append_record(
            "approval.requested", {"runId": RUN_ID, "approval": approval}
        )
        self.emit_event("approval.requested", {"approval": approval}, turn_id=turn_id)
        if not ready.wait(5.0):
            raise RuntimeError("fake approval response timed out")
        with self.state_lock:
            resolution = pending.get("resolution")
            self.pending = None
        if not isinstance(resolution, dict):
            raise RuntimeError("fake approval resolution is missing")
        return resolution

    def valid_arguments(self, name: str, arguments: Any) -> bool:
        """功能：执行 fake daemon 覆盖的 file.read/file.write 严格参数验证。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if not isinstance(arguments, dict):
            return False
        if name == "file.read":
            return set(arguments) == {"path"} and isinstance(arguments.get("path"), str)
        if name == "file.write":
            return (
                set(arguments) == {"path", "content"}
                and isinstance(arguments.get("path"), str)
                and isinstance(arguments.get("content"), str)
            )
        return False

    def operation_hash(self, name: str, arguments: dict[str, Any]) -> str:
        """功能：计算 fake 审批绑定使用的确定性小写 SHA-256 operation hash。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        normalized = json.dumps(
            {"operation": name, "arguments": arguments},
            ensure_ascii=False,
            sort_keys=True,
            separators=(",", ":"),
        ).encode("utf-8")
        return hashlib.sha256(normalized).hexdigest()

    def resolve_workspace_path(self, relative: str, *, for_write: bool) -> Path:
        """功能：解析 fake 文件工具路径并阻止绝对路径、穿越和符号链接逃逸。

        输入：模型相对路径及目标是否允许尚不存在。
        输出：确认位于 runner 工作区内的绝对 Path。
        不变量：不把这一检查描述成 hard sandbox，也不跟随目标符号链接写入。
        失败：路径越界、不存在或符号链接目标时抛出 RuntimeError。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        raw = Path(relative)
        if raw.is_absolute() or ".." in raw.parts:
            raise RuntimeError("unsafe fake workspace path")
        candidate = self.workspace / raw
        if for_write:
            parent = candidate.parent.resolve(strict=True)
            resolved = parent / candidate.name
            if candidate.is_symlink():
                raise RuntimeError("fake write target is a symlink")
        else:
            resolved = candidate.resolve(strict=True)
        try:
            resolved.relative_to(self.workspace)
        except ValueError as exc:
            raise RuntimeError("fake workspace path escaped") from exc
        return resolved

    def execute_file_tool(self, name: str, arguments: dict[str, Any]) -> dict[str, Any]:
        """功能：仅执行 runner fixture 允许的 bounded file.read 或 file.write。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if name == "file.read":
            path = self.resolve_workspace_path(arguments["path"], for_write=False)
            text = path.read_text(encoding="utf-8", errors="strict")
            return {"content": [{"type": "text", "text": text}], "isError": False}
        path = self.resolve_workspace_path(arguments["path"], for_write=True)
        content = arguments["content"]
        path.write_text(content, encoding="utf-8", newline="")
        size = len(content.encode("utf-8"))
        return {
            "content": [{"type": "text", "text": f"wrote {size} bytes"}],
            "isError": False,
        }

    def error_result(
        self,
        name: str,
        termination_reason: str,
        error_kind: str,
        code: int,
        message: str,
    ) -> dict[str, Any]:
        """功能：构造带稳定 terminationReason 和 portable error 的工具错误结果。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        return {
            "content": [{"type": "text", "text": message}],
            "isError": True,
            "terminationReason": termination_reason,
            "error": {
                "code": code,
                "message": message,
                "retryable": False,
                "details": {"kind": error_kind, "toolName": name},
            },
        }

    def finalize_tool(
        self, step: dict[str, Any], turn_id: str, result: dict[str, Any]
    ) -> None:
        """功能：依次持久化 tool.result、canonical tool message 后发 completed。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        tool_call_id = step["toolCallId"]
        if self.late_tool_result:
            self.emit_event(
                "tool.completed",
                {"toolCallId": tool_call_id, "result": result},
                turn_id=turn_id,
            )
        self.append_record(
            "tool.result",
            {
                "runId": RUN_ID,
                "turnId": turn_id,
                "toolCallId": tool_call_id,
                "result": result,
                "outcomeKnown": True,
            },
        )
        tool_message = {
            "messageId": f"tool-result-message-{tool_call_id}",
            "role": "tool",
            "toolCallId": tool_call_id,
            "toolName": step["name"],
            "content": result["content"],
            "isError": result["isError"],
            "time": TIME,
        }
        self.append_record(
            "message.appended",
            {"message": tool_message, "runId": RUN_ID, "turnId": turn_id},
        )
        if not self.late_tool_result:
            self.emit_event(
                "tool.completed",
                {"toolCallId": tool_call_id, "result": result},
                turn_id=turn_id,
            )

    def run_continuation(
        self,
        continuation: dict[str, Any],
        turn_number: int,
        accumulated_usage: dict[str, Any],
    ) -> dict[str, Any]:
        """功能：验证 expectedContext 并执行一个 FIFO faux 后续文本 turn。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        expected_context = continuation.get("expectedContext")
        if expected_context is not None and expected_context != self.context:
            raise RuntimeError("fake continuation context mismatch")
        turn_id = f"tool-turn-{turn_number}"
        self.append_record(
            "turn.started", {"runId": RUN_ID, "turnId": turn_id, "attempt": 1}
        )
        self.emit_event("turn.started", {}, turn_id=turn_id)
        message_id = f"tool-assistant-message-{turn_number}"
        self.emit_event(
            "message.started",
            {"messageId": message_id, "role": "assistant"},
            turn_id=turn_id,
        )
        content: list[dict[str, Any]] = []
        for step in continuation["steps"]:
            if step["type"] != "text":
                raise RuntimeError("fake continuation supports text only")
            block = {"type": "text", "text": step["text"]}
            content.append(block)
            if step["text"]:
                self.emit_event(
                    "message.delta",
                    {"messageId": message_id, "delta": block},
                    turn_id=turn_id,
                )
        usage = continuation.get("usage", self.zero_usage())
        message = {
            "messageId": message_id,
            "role": "assistant",
            "content": content,
            "provider": PROVIDER,
            "finishReason": "stop",
            "usage": usage,
            "time": TIME,
        }
        self.append_record(
            "message.appended",
            {"message": message, "runId": RUN_ID, "turnId": turn_id},
        )
        self.emit_event(
            "message.completed",
            {"messageId": message_id, "finishReason": "stop", "usage": usage},
            turn_id=turn_id,
        )
        self.context.append({"role": "assistant", "content": content})
        return self.add_usage(accumulated_usage, usage)

    def zero_usage(self) -> dict[str, int]:
        """功能：返回符合公共 Schema 的全零 normalized usage。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        return {"inputTokens": 0, "outputTokens": 0, "totalTokens": 0}

    def add_usage(self, left: dict[str, Any], right: dict[str, Any]) -> dict[str, int]:
        """功能：按字段相加两个本 fixture 使用的基础 token usage。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        return {
            "inputTokens": int(left.get("inputTokens", 0))
            + int(right.get("inputTokens", 0)),
            "outputTokens": int(left.get("outputTokens", 0))
            + int(right.get("outputTokens", 0)),
            "totalTokens": int(left.get("totalTokens", 0))
            + int(right.get("totalTokens", 0)),
        }


def parse_args() -> argparse.Namespace:
    """功能：解析仅用于触发动态 durability 负例的 fake daemon 参数。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser()
    parser.add_argument("--late-tool-result", action="store_true")
    return parser.parse_args()


def main() -> int:
    """功能：按 stdin 请求循环分派 fake initialize/configure/run/approval/cancel。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = parse_args()
    daemon = FakeToolDaemon(late_tool_result=args.late_tool_result)
    for raw_line in sys.stdin.buffer:
        request = json.loads(raw_line)
        method = request.get("method")
        if method == "initialize":
            daemon.initialize(request)
        elif method == "faux/configure":
            daemon.configure(request)
        elif method == "run/start":
            daemon.start_run(request)
        elif method == "approval/respond":
            daemon.resolve_approval(request)
        elif method == "run/cancel":
            daemon.cancel_run(request)
        else:
            raise RuntimeError("fake daemon received an unsupported method")
    if daemon.worker is not None:
        daemon.worker.join(timeout=1.0)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
