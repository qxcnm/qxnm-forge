#!/usr/bin/env python3
"""仅用于测试 Provider 黑盒 runner 编排的最小确定性 daemon。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import http.client
import json
import os
from pathlib import Path
import sys
import threading
import time
from typing import Any
from urllib.parse import urlsplit


CONFORMANCE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(CONFORMANCE))

import runner  # noqa: E402


def emit(value: dict[str, Any]) -> None:
    """功能：把一个 JSON-RPC 对象输出为紧凑 UTF-8 NDJSON 帧。

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
    turn: bool,
) -> dict[str, Any]:
    """功能：构造 Provider runner 测试所需的公共事件 notification。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    params: dict[str, Any] = {
        "sessionId": "provider-conformance-session-1",
        "runId": run_id,
        "seq": seq,
        "time": "2026-07-11T03:00:00Z",
        "type": event_type,
        "data": data,
    }
    if turn:
        params["turnId"] = "provider-test-turn-1"
    return {"jsonrpc": "2.0", "method": "event", "params": params}


def call_openai_compatible_mock() -> tuple[list[str], dict[str, int]]:
    """功能：调用 runner 注入的精确回环 Chat endpoint 并解析文本及 usage。

    输入：从规定环境名读取 endpoint 与运行时合成凭据。
    输出：按流顺序的文本片段和 normalized usage。
    不变量：只允许字面 127.0.0.1，凭据仅进入最终 Authorization header。
    失败：缺少配置、非回环 endpoint、HTTP 错误或无 usage 时抛出 RuntimeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    endpoint = os.environ.get("QXNM_FORGE_OPENAI_COMPATIBLE_ENDPOINT")
    credential = os.environ.get("OPENAI_API_KEY")
    if not endpoint or not credential:
        raise RuntimeError("missing provider conformance environment")
    parsed = urlsplit(endpoint)
    if parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or parsed.port is None:
        raise RuntimeError("provider conformance endpoint is not exact IPv4 loopback")
    body = json.dumps(
        {
            "model": "mock-chat-v1",
            "messages": [{"role": "user", "content": "fixture input"}],
            "stream": True,
            "tools": [
                {
                    "type": "function",
                    "function": {
                        "name": "file.read",
                        "parameters": {
                            "type": "object",
                            "properties": {"path": {"type": "string"}},
                            "required": ["path"],
                            "additionalProperties": False,
                        },
                    },
                }
            ],
        }
    )
    connection = http.client.HTTPConnection(parsed.hostname, parsed.port, timeout=2)
    try:
        connection.request(
            "POST",
            parsed.path,
            body=body,
            headers={
                "Content-Type": "application/json",
                "Authorization": f"Bearer {credential}",
            },
        )
        response = connection.getresponse()
        wire = response.read()
        if response.status != 200:
            raise RuntimeError("synthetic provider returned an HTTP error")
    finally:
        connection.close()

    decoder = runner.SSEDecoder()
    decoded = decoder.feed(wire)
    decoded.extend(decoder.finish())
    text_parts: list[str] = []
    usage: dict[str, int] | None = None
    for sse_event in decoded:
        data = sse_event.get("data")
        if data == "[DONE]" or not isinstance(data, str):
            continue
        payload = json.loads(data)
        choices = payload.get("choices")
        if isinstance(choices, list) and choices:
            delta = choices[0].get("delta")
            text = delta.get("content") if isinstance(delta, dict) else None
            if isinstance(text, str):
                text_parts.append(text)
        raw_usage = payload.get("usage")
        if isinstance(raw_usage, dict):
            usage = {
                "inputTokens": raw_usage["prompt_tokens"],
                "outputTokens": raw_usage["completion_tokens"],
                "totalTokens": raw_usage["total_tokens"],
            }
    if usage is None:
        raise RuntimeError("synthetic provider stream omitted usage")
    return text_parts, usage


def call_cancellation_mock(cancellation: threading.Event) -> None:
    """功能：延迟建立回环请求并保持流，直到测试 runner 发出取消。

    输入：由主 stdin 循环设置的线程安全取消事件。
    输出：无返回值；请求建立后等待取消并关闭连接。
    不变量：固定延迟让测试可证明 runner 不会在首个 HTTP 请求前抢先取消。
    失败：本测试辅助线程吞掉本机连接错误，主协议断言会给出确定性失败。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    time.sleep(0.15)
    endpoint = os.environ.get("QXNM_FORGE_OPENAI_COMPATIBLE_ENDPOINT")
    credential = os.environ.get("OPENAI_API_KEY")
    if not endpoint or not credential:
        return
    parsed = urlsplit(endpoint)
    if parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or parsed.port is None:
        return
    connection = http.client.HTTPConnection(parsed.hostname, parsed.port, timeout=1)
    try:
        connection.request(
            "POST",
            parsed.path,
            body=json.dumps(
                {
                    "model": "mock-chat-v1",
                    "messages": [{"role": "user", "content": "fixture input"}],
                    "stream": True,
                    "tools": [
                        {
                            "type": "function",
                            "function": {
                                "name": "file.read",
                                "parameters": {
                                    "type": "object",
                                    "properties": {"path": {"type": "string"}},
                                    "required": ["path"],
                                    "additionalProperties": False,
                                },
                            },
                        }
                    ],
                }
            ),
            headers={
                "Content-Type": "application/json",
                "Authorization": f"Bearer {credential}",
            },
        )
        response = connection.getresponse()
        if response.status != 200:
            return
        response.read(1)
        cancellation.wait(2.0)
    except (OSError, TimeoutError):
        return
    finally:
        connection.close()


def emit_initialize(request_id: str) -> None:
    """功能：响应 initialize 并诚实声明测试 daemon 的最小能力。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    emit(
        {
            "jsonrpc": "2.0",
            "id": request_id,
            "result": {
                "protocolVersion": "0.1",
                "implementation": {
                    "name": "provider-runner-test-daemon",
                    "version": "0.1.0+test",
                    "language": "python",
                },
                "capabilities": {
                    "methods": ["initialize", "run/start", "run/cancel"],
                    "eventTypes": [
                        "run.started",
                        "turn.started",
                        "message.started",
                        "message.delta",
                        "message.completed",
                        "run.completed",
                        "run.failed",
                        "run.cancelled",
                        "run.interrupted",
                    ],
                    "providers": [
                        {"id": "faux", "models": ["faux-v1"]},
                        {"id": "openai-compatible", "models": ["mock-chat-v1"]},
                    ],
                    "tools": [],
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


def emit_run(request_id: str, cancellation: threading.Event) -> None:
    """功能：接受 run 后按 endpoint 案例发起可取消请求或完整文本轨迹。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    run_id = "provider-test-run-1"
    emit({"jsonrpc": "2.0", "id": request_id, "result": {"runId": run_id}})
    endpoint = os.environ.get("QXNM_FORGE_OPENAI_COMPATIBLE_ENDPOINT", "")
    if urlsplit(endpoint).path.rstrip("/").endswith("/cancellation"):
        emit(event(run_id, 1, "run.started", {}, turn=False))
        threading.Thread(
            target=call_cancellation_mock,
            args=(cancellation,),
            name="fake-provider-cancellation-request",
            daemon=True,
        ).start()
        return
    text_parts, usage = call_openai_compatible_mock()
    frames = [
        event(run_id, 1, "run.started", {}, turn=False),
        event(run_id, 2, "turn.started", {}, turn=True),
        event(
            run_id,
            3,
            "message.started",
            {"messageId": "provider-test-message-1", "role": "assistant"},
            turn=True,
        ),
    ]
    seq = 4
    for text in text_parts:
        frames.append(
            event(
                run_id,
                seq,
                "message.delta",
                {
                    "messageId": "provider-test-message-1",
                    "delta": {"type": "text", "text": text},
                },
                turn=True,
            )
        )
        seq += 1
    frames.extend(
        [
            event(
                run_id,
                seq,
                "message.completed",
                {
                    "messageId": "provider-test-message-1",
                    "finishReason": "stop",
                    "usage": usage,
                },
                turn=True,
            ),
            event(
                run_id,
                seq + 1,
                "run.completed",
                {"status": "completed", "usage": usage},
                turn=False,
            ),
        ]
    )
    for frame in frames:
        emit(frame)


def emit_cancel(request_id: str, cancellation: threading.Event) -> None:
    """功能：确认可取消请求已建立后响应 run/cancel 并发出唯一取消终态。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    cancellation.set()
    emit(
        {
            "jsonrpc": "2.0",
            "id": request_id,
            "result": {"cancellationState": "requested"},
        }
    )
    emit(
        event(
            "provider-test-run-1",
            2,
            "run.cancelled",
            {"status": "cancelled", "reason": "test cancellation"},
            turn=False,
        )
    )


def main() -> int:
    """功能：处理 Provider runner 的 initialize 与单次 run/start 请求。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    cancellation = threading.Event()
    for raw_line in sys.stdin.buffer:
        request = json.loads(raw_line)
        method = request.get("method")
        request_id = request.get("id")
        if not isinstance(request_id, str):
            continue
        if method == "initialize":
            emit_initialize(request_id)
        elif method == "run/start":
            emit_run(request_id, cancellation)
        elif method == "run/cancel":
            emit_cancel(request_id, cancellation)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
