#!/usr/bin/env python3
"""仅用于第二批 Provider runner 编排自测的确定性无外网 daemon。

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
from typing import Any
from urllib.parse import urlencode, urlsplit


CONFORMANCE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(CONFORMANCE))

import runner  # noqa: E402


FAMILY_CONFIG = {
    "mistral-conversations": {
        "providerId": "mistral",
        "modelId": "mock-mistral-v1",
        "endpointEnv": "QXNM_FORGE_MISTRAL_CONVERSATIONS_ENDPOINT",
        "credentialEnv": "MISTRAL_API_KEY",
        "pathSuffix": "/v1/chat/completions",
        "query": {},
    },
    "azure-openai-responses": {
        "providerId": "azure-openai-responses",
        "modelId": "mock-azure-responses-v1",
        "endpointEnv": "QXNM_FORGE_AZURE_OPENAI_RESPONSES_ENDPOINT",
        "credentialEnv": "AZURE_OPENAI_API_KEY",
        "pathSuffix": "/responses",
        "query": {"api-version": "v1"},
    },
    "google-generative-ai": {
        "providerId": "google",
        "modelId": "mock-google-v1",
        "endpointEnv": "QXNM_FORGE_GOOGLE_GENERATIVE_AI_ENDPOINT",
        "credentialEnv": "GEMINI_API_KEY",
        "pathSuffix": "/models/mock-google-v1:streamGenerateContent",
        "query": {"alt": "sse"},
    },
}


def emit(value: dict[str, Any]) -> None:
    """功能：把一个 JSON-RPC 对象作为紧凑 UTF-8 NDJSON 帧写入 stdout。

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
    """功能：构造第二批 Provider runner 所需的公共事件 notification。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    params: dict[str, Any] = {
        "sessionId": "provider-conformance-session-1",
        "runId": run_id,
        "seq": seq,
        "time": "2026-07-14T01:00:00Z",
        "type": event_type,
        "data": data,
    }
    if turn:
        params["turnId"] = "provider-batch2-test-turn-1"
    return {"jsonrpc": "2.0", "method": "event", "params": params}


def selected_family() -> tuple[str, dict[str, Any], str, str]:
    """功能：从清洁子环境中选择恰好一个第二批 family 及其运行时值。

    输入：只读取三个规定 endpoint 环境名及其对应 credential 环境名。
    输出：family ID、公开契约、精确 endpoint 和仅用于最终 header 的 credential。
    不变量：不得枚举或序列化整个环境；必须且只能存在一个 endpoint。
    失败：缺少、重复 endpoint 或缺少凭据时抛出 RuntimeError，且不回显值。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    selected: list[tuple[str, dict[str, Any], str, str]] = []
    for family_id, config in FAMILY_CONFIG.items():
        endpoint = os.environ.get(config["endpointEnv"])
        if endpoint is None:
            continue
        credential = os.environ.get(config["credentialEnv"])
        if not credential:
            raise RuntimeError("missing synthetic credential")
        selected.append((family_id, config, endpoint, credential))
    if len(selected) != 1:
        raise RuntimeError("expected exactly one batch2 conformance endpoint")
    return selected[0]


def tool_schema() -> dict[str, Any]:
    """功能：返回三个原生请求共享的受限 file.read 参数 JSON Schema。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "type": "object",
        "properties": {"path": {"type": "string"}},
        "required": ["path"],
        "additionalProperties": False,
    }


def request_document(family_id: str) -> dict[str, Any]:
    """功能：按 PI 快照提取的 family wire 构造带非空工具定义的请求体。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    schema = tool_schema()
    if family_id == "mistral-conversations":
        return {
            "model": "mock-mistral-v1",
            "messages": [{"role": "user", "content": "fixture input"}],
            "stream": True,
            "tools": [
                {
                    "type": "function",
                    "function": {
                        "name": "file.read",
                        "description": "Read one file.",
                        "parameters": schema,
                        "strict": False,
                    },
                }
            ],
        }
    if family_id == "azure-openai-responses":
        return {
            "model": "mock-azure-responses-v1",
            "input": [{"role": "user", "content": "fixture input"}],
            "stream": True,
            "store": False,
            "tools": [
                {
                    "type": "function",
                    "name": "file.read",
                    "description": "Read one file.",
                    "parameters": schema,
                    "strict": False,
                }
            ],
        }
    if family_id == "google-generative-ai":
        return {
            "contents": [{"role": "user", "parts": [{"text": "fixture input"}]}],
            "tools": [
                {
                    "functionDeclarations": [
                        {
                            "name": "file.read",
                            "description": "Read one file.",
                            "parametersJsonSchema": schema,
                        }
                    ]
                }
            ],
        }
    raise RuntimeError("unknown batch2 family")


def request_headers(family_id: str, credential: str) -> dict[str, str]:
    """功能：把合成凭据放入 family 固定的最终认证 header。

    输入：第二批 family ID 和内存 canary credential。
    输出：Content-Type 与唯一认证 header；调用者不得记录返回值。
    不变量：Mistral 使用 Bearer，Azure 使用 api-key，Google 使用 x-goog-api-key。
    失败：未知 family 时抛出 RuntimeError，不回显 credential。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    headers = {"Content-Type": "application/json"}
    if family_id == "mistral-conversations":
        headers["Authorization"] = f"Bearer {credential}"
    elif family_id == "azure-openai-responses":
        headers["api-key"] = credential
    elif family_id == "google-generative-ai":
        headers["x-goog-api-key"] = credential
    else:
        raise RuntimeError("unknown batch2 family")
    return headers


def parse_native_stream(
    family_id: str,
    wire: bytes,
) -> tuple[list[str], dict[str, int]]:
    """功能：解析三类原生 SSE payload 并归一化文本及 usage。

    输入：family ID 和本机 mock 返回的任意分片重组字节。
    输出：文本 delta 列表及公共 input/output/total token 对象。
    不变量：只消费原生 JSON 字段，不读取认证值或错误 message。
    失败：SSE/JSON 缺失文本或 usage 时抛出 RuntimeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

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
        if family_id == "mistral-conversations":
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
        elif family_id == "azure-openai-responses":
            if payload.get("type") == "response.output_text.delta":
                text = payload.get("delta")
                if isinstance(text, str):
                    text_parts.append(text)
            if payload.get("type") == "response.completed":
                response = payload.get("response")
                raw_usage = (
                    response.get("usage") if isinstance(response, dict) else None
                )
                if isinstance(raw_usage, dict):
                    usage = {
                        "inputTokens": raw_usage["input_tokens"],
                        "outputTokens": raw_usage["output_tokens"],
                        "totalTokens": raw_usage["total_tokens"],
                    }
        else:
            candidates = payload.get("candidates")
            if isinstance(candidates, list) and candidates:
                content = candidates[0].get("content")
                parts = content.get("parts") if isinstance(content, dict) else None
                if isinstance(parts, list):
                    for part in parts:
                        text = part.get("text") if isinstance(part, dict) else None
                        if isinstance(text, str):
                            text_parts.append(text)
            raw_usage = payload.get("usageMetadata")
            if isinstance(raw_usage, dict):
                usage = {
                    "inputTokens": raw_usage["promptTokenCount"],
                    "outputTokens": raw_usage["candidatesTokenCount"],
                    "totalTokens": raw_usage["totalTokenCount"],
                }
    if not text_parts or usage is None:
        raise RuntimeError("synthetic native stream omitted text or usage")
    return text_parts, usage


def native_target(
    endpoint: str,
    config: dict[str, Any],
) -> tuple[Any, str]:
    """功能：验证精确回环 base 并追加 family 固定 path/query。

    输入：runner 注入的 endpoint 和公开 family 配置。
    输出：已解析 URL 对象与发送给 HTTPConnection 的原生 request-target。
    不变量：scheme 必须为 http 且 host 必须是字面 127.0.0.1 ephemeral 端口。
    失败：目标越界时抛出不回显 endpoint 的 RuntimeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parsed = urlsplit(endpoint)
    if parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or parsed.port is None:
        raise RuntimeError("batch2 conformance endpoint is not exact IPv4 loopback")
    query = urlencode(config["query"])
    target = parsed.path.rstrip("/") + config["pathSuffix"]
    if query:
        target += "?" + query
    return parsed, target


def call_mock() -> tuple[str, dict[str, Any], list[str], dict[str, int]]:
    """功能：调用 runner 注入的精确回环原生 endpoint 并解析成功文本流。

    输入：仅从选中 family 的规定环境名读取 endpoint 与内存 canary。
    输出：family、公开配置、文本 delta 与 normalized usage。
    不变量：只允许字面 127.0.0.1；不跟随重定向；凭据仅进入最终 header。
    失败：配置、目标、HTTP 或流语义不符时抛出不含敏感值的 RuntimeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    family_id, config, endpoint, credential = selected_family()
    parsed, target = native_target(endpoint, config)
    body = json.dumps(request_document(family_id), separators=(",", ":"))
    connection = http.client.HTTPConnection(parsed.hostname, parsed.port, timeout=2)
    try:
        connection.request(
            "POST",
            target,
            body=body,
            headers=request_headers(family_id, credential),
        )
        response = connection.getresponse()
        wire = response.read()
        if response.status != 200:
            raise RuntimeError("synthetic batch2 provider returned an HTTP error")
    finally:
        connection.close()
    text_parts, usage = parse_native_stream(family_id, wire)
    return family_id, config, text_parts, usage


def call_cancellation_mock(cancellation: threading.Event) -> None:
    """功能：建立第二批原生 SSE 请求并保持到 runner 发出 run/cancel。

    输入：由 stdin 主循环设置的线程安全取消事件。
    输出：无返回值；读取首字节证明请求到达后等待取消并关闭连接。
    不变量：仅请求精确回环目标；凭据只进入最终 header；线程不输出诊断值。
    失败：本测试辅助线程吞掉本机连接异常，主 runner 观测提供确定性断言。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        family_id, config, endpoint, credential = selected_family()
        parsed, target = native_target(endpoint, config)
        connection = http.client.HTTPConnection(
            parsed.hostname,
            parsed.port,
            timeout=1,
        )
        try:
            connection.request(
                "POST",
                target,
                body=json.dumps(
                    request_document(family_id),
                    separators=(",", ":"),
                ),
                headers=request_headers(family_id, credential),
            )
            response = connection.getresponse()
            if response.status != 200:
                return
            response.read(1)
            cancellation.wait(2.0)
        finally:
            connection.close()
    except (OSError, TimeoutError, RuntimeError):
        return


def emit_initialize(request_id: str) -> None:
    """功能：响应 initialize 并只声明当前测试进程选中的 Provider。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    _, config, _, _ = selected_family()
    emit(
        {
            "jsonrpc": "2.0",
            "id": request_id,
            "result": {
                "protocolVersion": "0.1",
                "implementation": {
                    "name": "provider-batch2-runner-test-daemon",
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
                        {"id": config["providerId"], "models": [config["modelId"]]},
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
    """功能：接受 run/start，调用本机第二批 mock 并输出完整 normalized 文本轨迹。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    run_id = "provider-batch2-test-run-1"
    emit({"jsonrpc": "2.0", "id": request_id, "result": {"runId": run_id}})
    _, _, endpoint, _ = selected_family()
    if urlsplit(endpoint).path.rstrip("/").endswith("/cancellation"):
        emit(event(run_id, 1, "run.started", {}, turn=False))
        threading.Thread(
            target=call_cancellation_mock,
            args=(cancellation,),
            name="fake-provider-batch2-cancellation-request",
            daemon=True,
        ).start()
        return
    _, _, text_parts, usage = call_mock()
    frames = [
        event(run_id, 1, "run.started", {}, turn=False),
        event(run_id, 2, "turn.started", {}, turn=True),
        event(
            run_id,
            3,
            "message.started",
            {"messageId": "provider-batch2-test-message-1", "role": "assistant"},
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
                    "messageId": "provider-batch2-test-message-1",
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
                    "messageId": "provider-batch2-test-message-1",
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
    """功能：确认第二批本机请求到达后响应取消并发出唯一 run.cancelled。

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
            "provider-batch2-test-run-1",
            2,
            "run.cancelled",
            {"status": "cancelled", "reason": "test cancellation"},
            turn=False,
        )
    )


def main() -> int:
    """功能：处理第二批 Provider runner 的 initialize 与单次 run/start。

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
