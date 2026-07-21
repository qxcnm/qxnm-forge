#!/usr/bin/env python3
"""用于自测跨运行时 Session lifecycle runner 的共享状态 fake daemon。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shutil
import sys
from typing import Any


JSONObject = dict[str, Any]
TIME = "2026-07-21T08:00:00Z"
UPDATED_TIME = "2026-07-21T08:00:05Z"


def parse_args() -> argparse.Namespace:
    """功能：解析 runner 追加的 Rust/.NET daemon argv 与测试注入开关。

    输出：运行时语言、workspace、state root、stdio/conformance 和泄漏开关。
    失败：缺少必需路径、语言或 daemon 子命令时由 argparse 终止。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", choices=("rust", "dotnet"), required=True)
    parser.add_argument("--leak-canary", action="store_true")
    parser.add_argument("command", choices=("daemon",))
    parser.add_argument("--stdio", action="store_true")
    parser.add_argument("--conformance", action="store_true")
    parser.add_argument("--workspace", type=Path, required=True)
    parser.add_argument("--state-dir", type=Path, required=True)
    return parser.parse_args()


def emit(value: JSONObject) -> None:
    """功能：把单个紧凑 UTF-8 JSON-RPC 对象写入 stdout NDJSON。

    输入：协议对象。
    输出：一帧并立即 flush。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    sys.stdout.write(
        json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n"
    )
    sys.stdout.flush()


def success(request: JSONObject, result: JSONObject) -> None:
    """功能：发送与 request ID 关联的 JSON-RPC 成功响应。

    输入：请求和 result 对象。
    输出：一个成功帧。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    emit({"jsonrpc": "2.0", "id": request["id"], "result": result})


def error(request: JSONObject, session_id: str) -> None:
    """功能：发送不含路径或 journal 内容的固定 Session 不存在错误。

    输入：请求与公开 Session ID。
    输出：`session_not_found` error response。
    不变量：只允许公开 ID 进入 resourceId。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    emit(
        {
            "jsonrpc": "2.0",
            "id": request["id"],
            "error": {
                "code": -32602,
                "message": "session was not found",
                "retryable": False,
                "details": {"kind": "session_not_found", "resourceId": session_id},
            },
        }
    )


def initialize_result(runtime: str) -> JSONObject:
    """功能：构造广告 lifecycle、faux 与 stdio 的严格 initialize result。

    输入：`rust` 或 `dotnet` 运行时语言。
    输出：满足公共协议 Schema 的能力对象。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "protocolVersion": "0.1",
        "implementation": {
            "name": "session-lifecycle-fake-" + runtime,
            "version": "0.1.0+test",
            "language": runtime,
        },
        "capabilities": {
            "methods": [
                "initialize",
                "faux/configure",
                "run/start",
                "session/list",
                "session/archive",
                "session/restore",
                "session/delete",
            ],
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
            "providers": [{"id": "faux", "models": ["faux-v1"]}],
            "tools": [],
            "transports": ["stdio"],
        },
        "limits": {
            "maxFrameBytes": 1_048_576,
            "maxEventBytes": 262_144,
            "maxArtifactBytes": 1_073_741_824,
            "maxConcurrentRuns": 1,
        },
    }


def record(
    session_id: str,
    sequence: int,
    kind: str,
    data: JSONObject,
) -> JSONObject:
    """功能：构造 parent 连续的最小 portable journal record。

    输入：Session ID、1-based seq、kind 与符合对应 Schema 的 data。
    输出：稳定 record ID、时间和 parent 的 journal 对象。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return {
        "schemaVersion": "0.1",
        "kind": kind,
        "recordId": f"record-{session_id}-{sequence}",
        "sessionId": session_id,
        "seq": sequence,
        "parentId": None if sequence == 1 else f"record-{session_id}-{sequence - 1}",
        "time": UPDATED_TIME if sequence == 5 else TIME,
        "data": data,
    }


def journal_values(
    runtime: str,
    workspace: Path,
    session_id: str,
    prompt: str,
    response_text: str,
) -> list[JSONObject]:
    """功能：生成 schema-valid、已完成 faux run 的最小 portable journal。

    输入：创建语言、workspace、Session ID、用户文本与 assistant 文本。
    输出：header、输入、accepted、started、assistant、terminal 六行。
    不变量：run 已唯一 completed，不保留 pending 状态。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    run_id = "run-" + session_id
    user_message_id = "message-user-" + session_id
    assistant_message_id = "message-assistant-" + session_id
    return [
        {
            "kind": "session",
            "schemaVersion": "0.1",
            "sessionId": session_id,
            "createdAt": TIME,
            "workspace": str(workspace.resolve()),
            "createdBy": {
                "name": "session-lifecycle-fake",
                "version": "0.1.0",
                "language": runtime,
            },
        },
        record(
            session_id,
            1,
            "message.appended",
            {
                "message": {
                    "messageId": user_message_id,
                    "role": "user",
                    "content": [{"type": "text", "text": prompt}],
                    "time": TIME,
                },
                "runId": run_id,
            },
        ),
        record(
            session_id,
            2,
            "run.accepted",
            {
                "runId": run_id,
                "inputMessageId": user_message_id,
                "provider": {"id": "faux", "modelId": "faux-v1"},
            },
        ),
        record(session_id, 3, "run.started", {"runId": run_id}),
        record(
            session_id,
            4,
            "message.appended",
            {
                "message": {
                    "messageId": assistant_message_id,
                    "role": "assistant",
                    "content": [{"type": "text", "text": response_text}],
                    "provider": {"id": "faux", "modelId": "faux-v1"},
                    "finishReason": "stop",
                    "usage": {
                        "inputTokens": 0,
                        "outputTokens": 0,
                        "totalTokens": 0,
                    },
                    "time": TIME,
                },
                "runId": run_id,
                "turnId": "turn-" + session_id,
            },
        ),
        record(
            session_id,
            5,
            "run.terminal",
            {
                "runId": run_id,
                "status": "completed",
                "usage": {
                    "inputTokens": 0,
                    "outputTokens": 0,
                    "totalTokens": 0,
                },
            },
        ),
    ]


def create_journal(
    runtime: str,
    workspace: Path,
    state_root: Path,
    session_id: str,
    prompt: str,
    response_text: str,
) -> None:
    """功能：在固定 `stateRoot/sessions/<id>` 写入新的 fake portable journal。

    输入：运行时、路径、Session ID 与两条文本。
    输出：以 LF 结束的严格 UTF-8 journal。
    失败：目标已存在时抛出 FileExistsError，避免覆盖证据。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    directory = state_root / "sessions" / session_id
    directory.mkdir(parents=True, exist_ok=False)
    values = journal_values(runtime, workspace, session_id, prompt, response_text)
    payload = "".join(
        json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n"
        for value in values
    )
    (directory / "journal.jsonl").write_text(payload, encoding="utf-8")


def load_journal(state_root: Path, session_id: str) -> list[JSONObject]:
    """功能：读取 fake 自己生成的 journal 以投影生命周期摘要。

    输入：state root 与 Session ID。
    输出：逐行 JSON objects。
    失败：缺失、空行或非对象时传播文件/JSON 错误。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    path = state_root / "sessions" / session_id / "journal.jsonl"
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]


def read_archive_ids(state_root: Path) -> list[str]:
    """功能：读取共享 archive-state；缺失表示空集合。

    输入：state root。
    输出：排序 archived Session IDs。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    path = state_root / "session-lifecycle" / "archive-state.json"
    if not path.exists():
        return []
    document = json.loads(path.read_text(encoding="utf-8"))
    return list(document["archivedSessionIds"])


def write_archive_ids(state_root: Path, values: list[str]) -> None:
    """功能：以同目录替换发布排序唯一的共享 archive-state 文档。

    输入：state root 与 archived IDs。
    输出：Schema 0.1 两字段 JSON 文档。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    directory = state_root / "session-lifecycle"
    directory.mkdir(parents=True, exist_ok=True)
    target = directory / "archive-state.json"
    temporary = directory / "archive-state.json.tmp"
    temporary.write_text(
        json.dumps(
            {
                "schemaVersion": "0.1",
                "archivedSessionIds": sorted(set(values)),
            },
            ensure_ascii=False,
            separators=(",", ":"),
        )
        + "\n",
        encoding="utf-8",
    )
    os.replace(temporary, target)


def summary(state_root: Path, workspace: Path, session_id: str) -> JSONObject:
    """功能：从 portable journal 首条 user 文本构造封闭 Session 摘要。

    输入：state root、workspace 与 Session ID。
    输出：五字段 lifecycle summary。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    values = load_journal(state_root, session_id)
    message = values[1]["data"]["message"]
    return {
        "sessionId": session_id,
        "title": message["content"][0]["text"],
        "project": workspace.name,
        "updatedAt": values[-1]["time"],
        "archived": session_id in set(read_archive_ids(state_root)),
    }


def list_summaries(state_root: Path, workspace: Path) -> list[JSONObject]:
    """功能：只扫描固定 sessions 根并按 ADR 0030 顺序列出摘要。

    输入：state root 与 workspace。
    输出：updatedAt 降序、Session ID ASCII 升序的摘要列表。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    sessions_root = state_root / "sessions"
    if not sessions_root.exists():
        return []
    result = [
        summary(state_root, workspace, item.name)
        for item in sessions_root.iterdir()
        if item.is_dir()
        and not item.name.startswith(".")
        and (item / "journal.jsonl").is_file()
    ]
    result.sort(key=lambda item: item["sessionId"])
    result.sort(key=lambda item: item["updatedAt"], reverse=True)
    return result


def list_result(request: JSONObject, state_root: Path, workspace: Path) -> JSONObject:
    """功能：按 v1 offset cursor 和 1..128 limit 构造严格三字段分页结果。

    输入：session/list 请求、state root 与 workspace。
    输出：sessions、nextCursor、hasMore。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    params = request["params"]
    limit = int(params.get("limit", 64))
    cursor = params.get("cursor")
    offset = int(cursor.removeprefix("v1:")) if isinstance(cursor, str) else 0
    values = list_summaries(state_root, workspace)
    page = values[offset : offset + limit]
    has_more = offset + len(page) < len(values)
    return {
        "sessions": page,
        "nextCursor": f"v1:{offset + len(page)}" if has_more else None,
        "hasMore": has_more,
    }


def event(
    session_id: str,
    run_id: str,
    sequence: int,
    event_type: str,
    data: JSONObject,
    *,
    turn_id: str | None = None,
) -> JSONObject:
    """功能：构造符合公共 envelope 的确定性 faux run event notification。

    输入：Session/run、seq、类型、data 与可选 turn ID。
    输出：JSON-RPC event notification。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    params: JSONObject = {
        "sessionId": session_id,
        "runId": run_id,
        "seq": sequence,
        "time": TIME,
        "type": event_type,
        "data": data,
    }
    if turn_id is not None:
        params["turnId"] = turn_id
    return {"jsonrpc": "2.0", "method": "event", "params": params}


def emit_run_events(session_id: str, response_text: str) -> None:
    """功能：在 run/start 响应后发出单一 completed 终态的 faux 事件序列。

    输入：Session ID 与 scenario text。
    输出：六个严格递增事件。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    run_id = "run-" + session_id
    turn_id = "turn-" + session_id
    message_id = "event-message-" + session_id
    frames = [
        event(session_id, run_id, 1, "run.started", {}),
        event(session_id, run_id, 2, "turn.started", {}, turn_id=turn_id),
        event(
            session_id,
            run_id,
            3,
            "message.started",
            {"messageId": message_id, "role": "assistant"},
            turn_id=turn_id,
        ),
        event(
            session_id,
            run_id,
            4,
            "message.delta",
            {"messageId": message_id, "delta": {"type": "text", "text": response_text}},
            turn_id=turn_id,
        ),
        event(
            session_id,
            run_id,
            5,
            "message.completed",
            {"messageId": message_id, "finishReason": "stop"},
            turn_id=turn_id,
        ),
        event(
            session_id,
            run_id,
            6,
            "run.completed",
            {
                "status": "completed",
                "usage": {
                    "inputTokens": 0,
                    "outputTokens": 0,
                    "totalTokens": 0,
                },
            },
        ),
    ]
    for frame in frames:
        emit(frame)


def mutate(
    request: JSONObject,
    method: str,
    state_root: Path,
    workspace: Path,
) -> None:
    """功能：执行 fake archive、restore 或 delete 并发送协议响应。

    输入：请求、method、共享 state root 与 workspace。
    输出：成功摘要/delete 回执，或脱敏 not-found error。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    session_id = request["params"]["sessionId"]
    session_directory = state_root / "sessions" / session_id
    if not session_directory.is_dir():
        error(request, session_id)
        return
    archived = read_archive_ids(state_root)
    if method == "session/archive":
        write_archive_ids(state_root, [*archived, session_id])
        success(request, {"session": summary(state_root, workspace, session_id)})
    elif method == "session/restore":
        write_archive_ids(state_root, [item for item in archived if item != session_id])
        success(request, {"session": summary(state_root, workspace, session_id)})
    else:
        shutil.rmtree(session_directory)
        write_archive_ids(state_root, [item for item in archived if item != session_id])
        success(request, {"deleted": True})


def main() -> int:
    """功能：处理 initialize、faux run、分页与生命周期 mutation 直到 clean EOF。

    输出：daemon 正常 EOF 为 0。
    不变量：所有 durable 状态只位于 runner 提供的 state root；不访问网络。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = parse_args()
    args.state_dir.mkdir(parents=True, exist_ok=True)
    scenario: JSONObject | None = None
    initialized = False
    if args.leak_canary:
        print(
            os.environ.get("QXNM_FORGE_SESSION_LIFECYCLE_SYNTHETIC_SECRET", ""),
            file=sys.stderr,
            flush=True,
        )
    for raw_line in sys.stdin.buffer:
        request = json.loads(raw_line)
        method = request["method"]
        if method == "initialize":
            initialized = True
            success(request, initialize_result(args.runtime))
        elif not initialized:
            error(request, "uninitialized")
        elif method == "faux/configure":
            scenario = request["params"]["scenario"]
            success(request, {"scenarioId": "faux:" + scenario["name"]})
        elif method == "run/start":
            assert scenario is not None
            session_id = request["params"]["sessionId"]
            prompt = request["params"]["input"]["content"][0]["text"]
            response_text = "".join(
                step["text"] for step in scenario["steps"] if step["type"] == "text"
            )
            create_journal(
                args.runtime,
                args.workspace,
                args.state_dir,
                session_id,
                prompt,
                response_text,
            )
            success(request, {"runId": "run-" + session_id})
            emit_run_events(session_id, response_text)
        elif method == "session/list":
            success(request, list_result(request, args.state_dir, args.workspace))
        elif method in {"session/archive", "session/restore", "session/delete"}:
            mutate(request, method, args.state_dir, args.workspace)
        else:
            emit(
                {
                    "jsonrpc": "2.0",
                    "id": request["id"],
                    "error": {
                        "code": -32601,
                        "message": "method not found",
                        "retryable": False,
                        "details": {"kind": "method_not_found"},
                    },
                }
            )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
