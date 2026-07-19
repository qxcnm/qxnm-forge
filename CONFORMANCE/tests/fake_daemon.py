#!/usr/bin/env python3
"""仅用于测试黑盒 runner 的确定性 daemon。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
import json
import sys
from typing import Any


def emit(value: dict[str, Any]) -> None:
    """功能：把一个 JSON-RPC 对象作为紧凑 UTF-8 NDJSON 帧输出。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    sys.stdout.write(
        json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n"
    )
    sys.stdout.flush()


def event(
    run_id: str,
    seq: int,
    event_type: str,
    data: dict[str, Any],
    *,
    turn: bool = False,
) -> dict[str, Any]:
    """功能：构造带公共 envelope 的确定性事件 notification。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    params: dict[str, Any] = {
        "sessionId": "conformance-session-1",
        "runId": run_id,
        "seq": seq,
        "time": "2026-07-10T08:00:00.123456Z",
        "type": event_type,
        "data": data,
    }
    if turn:
        params["turnId"] = "fake-turn-99"
    return {"jsonrpc": "2.0", "method": "event", "params": params}


def parse_args() -> argparse.Namespace:
    """功能：解析用于触发 runner 负向测试的假 daemon 开关。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser()
    parser.add_argument("--bad-seq", action="store_true")
    parser.add_argument("--stdout-log", action="store_true")
    parser.add_argument("--event-after-terminal", action="store_true")
    parser.add_argument("--diagnostic-token")
    return parser.parse_args()


def main() -> int:
    """功能：处理 initialize、faux/configure 和 run/start 测试请求。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = parse_args()
    scenario: dict[str, Any] | None = None
    if args.stdout_log:
        print("safe stderr marker for invalid stdout", file=sys.stderr, flush=True)
        print("this diagnostic illegally went to stdout", flush=True)
    for raw_line in sys.stdin.buffer:
        request = json.loads(raw_line)
        method = request["method"]
        if method == "initialize":
            emit(
                {
                    "jsonrpc": "2.0",
                    "id": request["id"],
                    "result": {
                        "protocolVersion": "0.1",
                        "implementation": {
                            "name": "qxnm-forge-conformance",
                            "version": "0.1.0+test",
                            "language": "python",
                        },
                        "capabilities": {
                            "methods": [
                                "initialize",
                                "faux/configure",
                                "run/start",
                                "run/cancel",
                                "run/steer",
                                "run/followUp",
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
                                "tool.delta",
                                "tool.completed",
                                "retry.scheduled",
                                "context.compacted",
                                "run.completed",
                                "run.failed",
                                "run.cancelled",
                                "run.interrupted",
                            ],
                            "providers": [
                                {"id": "faux", "models": ["faux-v1"]},
                                {"id": "openai-compatible", "models": []},
                                {"id": "openai-responses", "models": []},
                                {"id": "anthropic", "models": []},
                            ],
                            "tools": [
                                "file.read",
                                "file.write",
                                "file.edit",
                                "search.text",
                                "process.exec",
                                "shell.exec",
                            ],
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
        elif method == "faux/configure":
            scenario = request["params"]["scenario"]
            emit(
                {
                    "jsonrpc": "2.0",
                    "id": request["id"],
                    "result": {"scenarioId": f"faux:{scenario['name']}"},
                }
            )
        elif method == "run/start":
            assert scenario is not None
            run_id = "fake-run-42"
            emit({"jsonrpc": "2.0", "id": request["id"], "result": {"runId": run_id}})
            text = "".join(
                step["text"] for step in scenario["steps"] if step["type"] == "text"
            )
            seqs = [1, 2, 3, 4, 5, 6]
            if args.bad_seq:
                seqs[4] = seqs[3]
            message_id = "fake-message-123"
            emitted = [
                event(run_id, seqs[0], "run.started", {}),
                event(run_id, seqs[1], "turn.started", {}, turn=True),
                event(
                    run_id,
                    seqs[2],
                    "message.started",
                    {"messageId": message_id, "role": "assistant"},
                    turn=True,
                ),
                event(
                    run_id,
                    seqs[3],
                    "message.delta",
                    {"messageId": message_id, "delta": {"type": "text", "text": text}},
                    turn=True,
                ),
                event(
                    run_id,
                    seqs[4],
                    "message.completed",
                    {"messageId": message_id, "finishReason": "stop"},
                    turn=True,
                ),
                event(
                    run_id,
                    seqs[5],
                    "run.completed",
                    {"status": "completed", "usage": scenario["usage"]},
                ),
            ]
            for frame in emitted:
                emit(frame)
            if args.event_after_terminal:
                emit(event(run_id, 7, "message.delta", {"text": "late"}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
