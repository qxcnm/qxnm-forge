#!/usr/bin/env python3
"""仅用于第三批 Provider runner 编排自测的确定性无外网 daemon。

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

import provider_mock  # noqa: E402
import runner  # noqa: E402


FAMILY_CONFIG = {
    "google-vertex": {
        "providerId": "google-vertex",
        "modelId": "mock-vertex-v1",
        "endpointEnv": "QXNM_FORGE_GOOGLE_VERTEX_ENDPOINT",
        "credentialEnv": "QXNM_FORGE_GOOGLE_VERTEX_OAUTH_TOKEN",
        "pathSuffix": (
            "/v1/projects/mock-project/locations/us-central1/publishers/google/"
            "models/mock-vertex-v1:streamGenerateContent"
        ),
        "query": {"alt": "sse"},
    },
    "bedrock-converse-stream": {
        "providerId": "amazon-bedrock",
        "modelId": "mock-bedrock-v1",
        "endpointEnv": "QXNM_FORGE_BEDROCK_CONVERSE_STREAM_ENDPOINT",
        "credentialEnv": "AWS_ACCESS_KEY_ID",
        "pathSuffix": "/model/mock-bedrock-v1/converse-stream",
        "query": {},
    },
    "openai-codex-responses": {
        "providerId": "openai-codex",
        "modelId": "mock-codex-v1",
        "endpointEnv": "QXNM_FORGE_OPENAI_CODEX_RESPONSES_ENDPOINT",
        "credentialEnv": "QXNM_FORGE_OPENAI_CODEX_OAUTH_TOKEN",
        "pathSuffix": "/codex/responses",
        "query": {},
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
    """功能：构造第三批 Provider runner 所需的公共事件 notification。

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
        params["turnId"] = "provider-batch3-test-turn-1"
    return {"jsonrpc": "2.0", "method": "event", "params": params}


def selected_family() -> tuple[str, dict[str, Any], str, str]:
    """功能：从清洁子环境选择恰好一个第三批 family 及其运行时值。

    输入：只读取三个规定 endpoint 名及各自 primary credential 名。
    输出：family ID、公开契约、精确回环 endpoint 和内存 credential。
    不变量：不得枚举或序列化整个环境；必须且只能存在一个 endpoint。
    失败：缺少、重复 endpoint 或缺少凭据时抛出不回显实例值的 RuntimeError。
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
        raise RuntimeError("expected exactly one batch3 conformance endpoint")
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
    """功能：按第三批 family 原生 wire 构造带非空工具定义的请求体。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    schema = tool_schema()
    if family_id == "google-vertex":
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
    if family_id == "bedrock-converse-stream":
        return {
            "messages": [{"role": "user", "content": [{"text": "fixture input"}]}],
            "toolConfig": {
                "tools": [
                    {
                        "toolSpec": {
                            "name": "file.read",
                            "description": "Read one file.",
                            "inputSchema": {"json": schema},
                        }
                    }
                ]
            },
        }
    if family_id == "openai-codex-responses":
        return {
            "model": "mock-codex-v1",
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
    raise RuntimeError("unknown batch3 family")


def request_headers(family_id: str, credential: str) -> dict[str, str]:
    """功能：把合成凭据与必要固定 header 放到第三批最终 HTTP 请求。

    输入：第三批 family ID 和仅驻留内存的 primary credential。
    输出：供回环 mock 使用的 header；调用者不得记录返回值。
    不变量：Vertex/Codex 使用 Bearer；Bedrock 仅生成 SigV4 形状，不声称校验签名。
    失败：缺少 AWS companion secret/session 或未知 family 时抛出脱敏 RuntimeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    headers = {"Content-Type": "application/json"}
    if family_id == "google-vertex":
        headers["Authorization"] = f"Bearer {credential}"
    elif family_id == "bedrock-converse-stream":
        secret = os.environ.get("AWS_SECRET_ACCESS_KEY")
        session_token = os.environ.get("AWS_SESSION_TOKEN")
        if not secret or not session_token:
            raise RuntimeError("missing synthetic AWS companion credential")
        del secret
        headers.update(
            {
                "Accept": "application/vnd.amazon.eventstream",
                "Authorization": (
                    "AWS4-HMAC-SHA256 Credential="
                    f"{credential}/20260714/us-east-1/bedrock/aws4_request, "
                    "SignedHeaders=content-type;host;x-amz-content-sha256;"
                    "x-amz-date;x-amz-security-token, Signature=synthetic"
                ),
                "x-amz-content-sha256": "synthetic-payload-digest",
                "x-amz-date": "20260714T010000Z",
                "x-amz-security-token": session_token,
            }
        )
    elif family_id == "openai-codex-responses":
        headers.update(
            {
                "Authorization": f"Bearer {credential}",
                "OpenAI-Beta": "responses=experimental",
                "originator": "qxnm-forge",
            }
        )
    else:
        raise RuntimeError("unknown batch3 family")
    return headers


def parse_sse_stream(
    family_id: str,
    wire: bytes,
) -> tuple[list[str], dict[str, int]]:
    """功能：解析 Vertex 或 Codex 原生 SSE 并归一化文本及 usage。

    输入：SSE family ID 和本机 mock 完整响应字节。
    输出：文本 delta 列表及公共 token usage。
    不变量：只消费原生事件字段，不读取或复制认证值。
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
        if not isinstance(data, str):
            continue
        payload = json.loads(data)
        if family_id == "google-vertex":
            candidates = payload.get("candidates")
            if isinstance(candidates, list) and candidates:
                content = candidates[0].get("content")
                parts = content.get("parts") if isinstance(content, dict) else None
                if isinstance(parts, list):
                    for part in parts:
                        text_value = (
                            part.get("text") if isinstance(part, dict) else None
                        )
                        if isinstance(text_value, str):
                            text_parts.append(text_value)
            raw_usage = payload.get("usageMetadata")
            if isinstance(raw_usage, dict):
                usage = {
                    "inputTokens": raw_usage["promptTokenCount"],
                    "outputTokens": raw_usage["candidatesTokenCount"],
                    "totalTokens": raw_usage["totalTokenCount"],
                }
        elif payload.get("type") == "response.output_text.delta":
            text_value = payload.get("delta")
            if isinstance(text_value, str):
                text_parts.append(text_value)
        if (
            family_id == "openai-codex-responses"
            and payload.get("type") == "response.completed"
        ):
            response = payload.get("response")
            raw_usage = response.get("usage") if isinstance(response, dict) else None
            if isinstance(raw_usage, dict):
                usage = {
                    "inputTokens": raw_usage["input_tokens"],
                    "outputTokens": raw_usage["output_tokens"],
                    "totalTokens": raw_usage["total_tokens"],
                }
    if not text_parts or usage is None:
        raise RuntimeError("synthetic SSE omitted text or usage")
    return text_parts, usage


def parse_bedrock_stream(wire: bytes) -> tuple[list[str], dict[str, int]]:
    """功能：严格解析 Bedrock AWS EventStream 并归一化文本与 usage。

    输入：本机 mock 返回的完整 EventStream 字节。
    输出：按 contentBlockDelta 顺序排列的文本和 metadata usage。
    不变量：委托公共测试解码器验证长度、typed header 与两个 CRC。
    失败：事件 header/JSON/usage 缺失或 CRC 不符时抛出 RuntimeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    text_parts: list[str] = []
    usage: dict[str, int] | None = None
    for headers, payload in provider_mock.decode_aws_event_stream(wire):
        event_type = headers.get(":event-type")
        if event_type == "contentBlockDelta":
            delta = payload.get("delta")
            text_value = delta.get("text") if isinstance(delta, dict) else None
            if isinstance(text_value, str):
                text_parts.append(text_value)
        elif event_type == "metadata":
            raw_usage = payload.get("usage")
            if isinstance(raw_usage, dict):
                usage = {
                    "inputTokens": raw_usage["inputTokens"],
                    "outputTokens": raw_usage["outputTokens"],
                    "totalTokens": raw_usage["totalTokens"],
                }
    if not text_parts or usage is None:
        raise RuntimeError("synthetic Bedrock stream omitted text or usage")
    return text_parts, usage


def native_target(endpoint: str, config: dict[str, Any]) -> tuple[Any, str]:
    """功能：验证精确回环 base 并追加第三批 family 固定 path/query。

    输入：runner 注入的 endpoint 和公开 family 配置。
    输出：已解析 URL 对象与供 HTTPConnection 使用的 request-target。
    不变量：scheme 为 http、host 为字面 127.0.0.1 且端口由 runner 分配。
    失败：目标越界时抛出不回显 endpoint 的 RuntimeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parsed = urlsplit(endpoint)
    if parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or parsed.port is None:
        raise RuntimeError("batch3 conformance endpoint is not exact IPv4 loopback")
    query = urlencode(config["query"])
    target = parsed.path.rstrip("/") + config["pathSuffix"]
    if query:
        target += "?" + query
    return parsed, target


def call_mock() -> tuple[list[str], dict[str, int]]:
    """功能：调用精确回环第三批 endpoint 并解析成功文本流。

    输入：只从选中 family 的规定环境读取 endpoint 与内存凭据。
    输出：文本 delta 与 normalized usage。
    不变量：不跟随重定向；Bedrock 二进制和两个 SSE family 分开解析。
    失败：配置、HTTP、CRC、SSE 或原生事件语义不符时抛出脱敏 RuntimeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    family_id, config, endpoint, credential = selected_family()
    parsed, target = native_target(endpoint, config)
    connection = http.client.HTTPConnection(parsed.hostname, parsed.port, timeout=2)
    try:
        connection.request(
            "POST",
            target,
            body=json.dumps(request_document(family_id), separators=(",", ":")),
            headers=request_headers(family_id, credential),
        )
        response = connection.getresponse()
        wire = response.read()
        if response.status != 200:
            raise RuntimeError("synthetic batch3 provider returned an HTTP error")
    finally:
        connection.close()
    if family_id == "bedrock-converse-stream":
        return parse_bedrock_stream(wire)
    return parse_sse_stream(family_id, wire)


def call_cancellation_mock(cancellation: threading.Event) -> None:
    """功能：建立第三批原生请求并保持到 runner 发出 run/cancel。

    输入：由 stdin 主循环设置的线程安全取消事件。
    输出：无返回值；读取首字节证明请求到达后等待取消并关闭连接。
    不变量：只请求精确回环目标；凭据仅进入最终 header；线程不记录值。
    失败：测试辅助线程吞掉本机连接异常，由主 runner 的观测提供断言。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        family_id, config, endpoint, credential = selected_family()
        parsed, target = native_target(endpoint, config)
        connection = http.client.HTTPConnection(parsed.hostname, parsed.port, timeout=1)
        try:
            connection.request(
                "POST",
                target,
                body=json.dumps(request_document(family_id), separators=(",", ":")),
                headers=request_headers(family_id, credential),
            )
            response = connection.getresponse()
            if response.status != 200:
                return
            response.read(1)
            cancellation.wait(2.0)
        finally:
            connection.close()
    except (OSError, TimeoutError, RuntimeError, provider_mock.ProviderMockError):
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
                    "name": "provider-batch3-runner-test-daemon",
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
    """功能：接受 run/start，调用本机第三批 mock 并输出 normalized 文本轨迹。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    run_id = "provider-batch3-test-run-1"
    emit({"jsonrpc": "2.0", "id": request_id, "result": {"runId": run_id}})
    _, _, endpoint, _ = selected_family()
    if urlsplit(endpoint).path.rstrip("/").endswith("/cancellation"):
        emit(event(run_id, 1, "run.started", {}, turn=False))
        threading.Thread(
            target=call_cancellation_mock,
            args=(cancellation,),
            name="fake-provider-batch3-cancellation-request",
            daemon=True,
        ).start()
        return
    text_parts, usage = call_mock()
    frames = [
        event(run_id, 1, "run.started", {}, turn=False),
        event(run_id, 2, "turn.started", {}, turn=True),
        event(
            run_id,
            3,
            "message.started",
            {"messageId": "provider-batch3-test-message-1", "role": "assistant"},
            turn=True,
        ),
    ]
    seq = 4
    for text_value in text_parts:
        frames.append(
            event(
                run_id,
                seq,
                "message.delta",
                {
                    "messageId": "provider-batch3-test-message-1",
                    "delta": {"type": "text", "text": text_value},
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
                    "messageId": "provider-batch3-test-message-1",
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
    """功能：确认第三批请求到达后响应取消并发出唯一 run.cancelled。

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
            "provider-batch3-test-run-1",
            2,
            "run.cancelled",
            {"status": "cancelled", "reason": "test cancellation"},
            turn=False,
        )
    )


def main() -> int:
    """功能：处理第三批 Provider runner 的 initialize 与单次 run/start。

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
