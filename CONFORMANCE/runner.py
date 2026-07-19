#!/usr/bin/env python3
"""qxnm-forge 语言中立 daemon 一致性 runner，仅使用 Python 标准库。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
import codecs
import copy
import datetime as dt
import difflib
import json
import os
from pathlib import Path
import queue
import re
import signal
import subprocess
import sys
import threading
import time
from typing import Any, BinaryIO, Callable, Iterable, Mapping, Sequence


JSONValue = (
    type(None) | bool | int | float | str | list["JSONValue"] | dict[str, "JSONValue"]
)
JSONObject = dict[str, JSONValue]

TERMINAL_EVENTS = frozenset(
    {"run.completed", "run.failed", "run.cancelled", "run.interrupted"}
)

NORMALIZED_SERVER_CAPABILITIES: JSONObject = {
    "methods": ["initialize", "faux/configure", "run/start"],
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
}


class ConformanceError(Exception):
    """A deterministic fixture, transport, protocol, or trace failure."""


class ProtocolViolation(ConformanceError):
    """The daemon emitted a frame that violates the common protocol."""


class GoldenMismatch(ConformanceError):
    """The normalized actual trace differs from the checked-in golden trace."""

    def __init__(self, message: str, actual: Sequence[JSONObject]) -> None:
        """功能：保存 golden 差异诊断及可选择落盘的规范化实际轨迹。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        super().__init__(message)
        self.actual = list(actual)


def _reject_duplicate_keys(pairs: list[tuple[str, JSONValue]]) -> JSONObject:
    """功能：构造 JSON 对象并拒绝重复键。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    result: JSONObject = {}
    for key, value in pairs:
        if key in result:
            raise ProtocolViolation(f"duplicate JSON object key: {key!r}")
        result[key] = value
    return result


def _reject_constant(value: str) -> JSONValue:
    """功能：拒绝 JSON 标准之外的 NaN 和 Infinity 常量。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    raise ProtocolViolation(f"non-finite JSON number: {value}")


STRICT_DECODER = json.JSONDecoder(
    object_pairs_hook=_reject_duplicate_keys,
    parse_constant=_reject_constant,
)


def strict_json_loads(text: str) -> JSONValue:
    """功能：按 RFC 8259 严格解析 JSON，拒绝重复键与非有限数值。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        value, end = STRICT_DECODER.raw_decode(text)
    except json.JSONDecodeError as exc:
        raise ProtocolViolation(f"invalid JSON: {exc.msg}") from exc
    if text[end:].strip():
        raise ProtocolViolation("invalid JSON: trailing data")
    return value


class NDJSONDecoder:
    """严格且有帧大小限制的增量 UTF-8 NDJSON 解码器。"""

    def __init__(
        self,
        max_frame_bytes: int = 1_048_576,
        frame_bytes_callback: Callable[[JSONObject, int], None] | None = None,
    ) -> None:
        """功能：初始化 NDJSON 缓冲区、最大帧限制和可选实际字节观察器。

        输入：正最大 payload 字节数，以及每个成功对象帧的可选 `(frame, wireBytes)` callback。
        输出：可增量解析 UTF-8 NDJSON 的 decoder。
        不变量：wireBytes 是 LF 之前的实际 payload 长度并包含可选 CR/JSON whitespace。
        失败：限制非正时抛出 ValueError；callback 异常由 feed/finish 传播。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if max_frame_bytes <= 0:
            raise ValueError("max_frame_bytes must be positive")
        self.max_frame_bytes = max_frame_bytes
        self.frame_bytes_callback = frame_bytes_callback
        self._buffer = bytearray()

    def feed(self, chunk: bytes) -> list[JSONObject]:
        """功能：接收任意拆分的字节块并返回完整 NDJSON 对象帧。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if not isinstance(chunk, bytes):
            raise TypeError("NDJSON chunks must be bytes")
        self._buffer.extend(chunk)
        frames: list[JSONObject] = []
        while True:
            newline = self._buffer.find(b"\n")
            if newline < 0:
                if len(self._buffer) > self.max_frame_bytes:
                    raise ProtocolViolation("NDJSON frame exceeds maxFrameBytes")
                return frames
            if newline > self.max_frame_bytes:
                raise ProtocolViolation("NDJSON frame exceeds maxFrameBytes")
            wire_bytes = newline
            raw = bytes(self._buffer[:newline])
            del self._buffer[: newline + 1]
            if raw.endswith(b"\r"):
                raw = raw[:-1]
            if not raw:
                raise ProtocolViolation("blank NDJSON frame")
            try:
                text = raw.decode("utf-8", errors="strict")
            except UnicodeDecodeError as exc:
                raise ProtocolViolation("invalid UTF-8 in NDJSON frame") from exc
            value = strict_json_loads(text)
            if not isinstance(value, dict):
                raise ProtocolViolation("NDJSON frame must be a JSON object")
            if self.frame_bytes_callback is not None:
                self.frame_bytes_callback(value, wire_bytes)
            frames.append(value)

    def finish(self) -> list[JSONObject]:
        """功能：在流结束时解析规范允许的不带 LF 的完整末帧。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if not self._buffer:
            return []
        if len(self._buffer) > self.max_frame_bytes:
            raise ProtocolViolation("NDJSON frame exceeds maxFrameBytes")
        wire_bytes = len(self._buffer)
        raw = bytes(self._buffer)
        self._buffer.clear()
        if raw.endswith(b"\r"):
            raw = raw[:-1]
        if not raw:
            raise ProtocolViolation("blank NDJSON frame")
        try:
            text = raw.decode("utf-8", errors="strict")
        except UnicodeDecodeError as exc:
            raise ProtocolViolation("invalid UTF-8 in NDJSON frame") from exc
        try:
            value = strict_json_loads(text)
        except ProtocolViolation as exc:
            raise ProtocolViolation("unterminated NDJSON frame at EOF") from exc
        if not isinstance(value, dict):
            raise ProtocolViolation("NDJSON frame must be a JSON object")
        if self.frame_bytes_callback is not None:
            self.frame_bytes_callback(value, wire_bytes)
        return [value]


class PartialJSONDecoder:
    """解析在任意字节位置拆分且以空白分隔的连续 JSON 值。"""

    def __init__(self) -> None:
        """功能：初始化 UTF-8 增量解码器和文本缓冲区。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self._utf8 = codecs.getincrementaldecoder("utf-8")(errors="strict")
        self._text = ""

    def feed(self, chunk: bytes) -> list[JSONValue]:
        """功能：接收 partial JSON 字节块并返回所有已完整的值。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        try:
            self._text += self._utf8.decode(chunk, final=False)
        except UnicodeDecodeError as exc:
            raise ProtocolViolation("invalid UTF-8 in JSON stream") from exc
        return self._drain(final=False)

    def finish(self) -> list[JSONValue]:
        """功能：结束 partial JSON 流并拒绝残缺值或残缺 UTF-8。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        try:
            self._text += self._utf8.decode(b"", final=True)
        except UnicodeDecodeError as exc:
            raise ProtocolViolation("invalid UTF-8 in JSON stream") from exc
        return self._drain(final=True)

    def _drain(self, *, final: bool) -> list[JSONValue]:
        """功能：从内部文本缓冲区提取尽可能多的完整 JSON 值。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        values: list[JSONValue] = []
        while True:
            stripped = self._text.lstrip()
            self._text = stripped
            if not stripped:
                return values
            try:
                value, end = STRICT_DECODER.raw_decode(stripped)
            except json.JSONDecodeError as exc:
                if final:
                    raise ProtocolViolation("incomplete JSON value at EOF") from exc
                return values
            values.append(value)
            self._text = stripped[end:]


class SSEDecoder:
    """供 Provider 夹具使用的最小增量 WHATWG event-stream 解码器。"""

    def __init__(self) -> None:
        """功能：初始化 SSE 的 UTF-8、行和事件字段状态。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self._utf8 = codecs.getincrementaldecoder("utf-8")(errors="strict")
        self._text = ""
        self._event = ""
        self._event_id: str | None = None
        self._data: list[str] = []

    def feed(self, chunk: bytes) -> list[JSONObject]:
        """功能：接收任意拆分的 SSE 字节块并返回完整事件。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        try:
            self._text += self._utf8.decode(chunk, final=False)
        except UnicodeDecodeError as exc:
            raise ProtocolViolation("invalid UTF-8 in SSE stream") from exc
        return self._drain_lines(final=False)

    def finish(self) -> list[JSONObject]:
        """功能：结束 SSE 流，提交字段行完整的尾事件并丢弃残缺字段行。

        输入：此前 feed 缓存的 UTF-8、行与事件状态。
        输出：空行已提交的事件，以及 EOF 前以 CR/LF 结束的一个尾事件。
        不变量：无字段行终止符的 EOF 残片永不提交，避免把断流 JSON 当作完整事件。
        失败：EOF 处残缺 UTF-8 时抛出 ProtocolViolation。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        try:
            self._text += self._utf8.decode(b"", final=True)
        except UnicodeDecodeError as exc:
            raise ProtocolViolation("invalid UTF-8 in SSE stream") from exc
        events = self._drain_lines(final=True)
        if self._data:
            self._process_line("", events)
        self._event = ""
        self._event_id = None
        self._data = []
        return events

    def _drain_lines(self, *, final: bool) -> list[JSONObject]:
        """功能：按 CR、LF 或 CRLF 从 SSE 缓冲区消费完整行。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        events: list[JSONObject] = []
        while self._text:
            positions = [
                index
                for index in (self._text.find("\r"), self._text.find("\n"))
                if index >= 0
            ]
            if not positions:
                if final:
                    # 没有 CR/LF 的末尾字段行属于断流残片，不参与 EOF 提交。
                    self._text = ""
                return events
            end = min(positions)
            if self._text[end] == "\r" and end + 1 == len(self._text) and not final:
                return events
            consume = 2 if self._text[end : end + 2] == "\r\n" else 1
            line = self._text[:end]
            self._text = self._text[end + consume :]
            self._process_line(line, events)
        return events

    def _process_line(self, line: str, events: list[JSONObject]) -> None:
        """功能：应用 SSE 字段语义并在空行处提交事件。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if not line:
            if self._data:
                event: JSONObject = {
                    "event": self._event or "message",
                    "data": "\n".join(self._data),
                }
                if self._event_id is not None:
                    event["id"] = self._event_id
                events.append(event)
            self._event = ""
            self._data = []
            return
        if line.startswith(":"):
            return
        field, separator, value = line.partition(":")
        if separator and value.startswith(" "):
            value = value[1:]
        if field == "event":
            self._event = value
        elif field == "data":
            self._data.append(value)
        elif field == "id" and "\x00" not in value:
            self._event_id = value


def decode_ndjson_bytes(
    data: bytes, max_frame_bytes: int = 1_048_576
) -> list[JSONObject]:
    """功能：一次性严格解析一段 NDJSON 字节数据。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    decoder = NDJSONDecoder(max_frame_bytes=max_frame_bytes)
    frames = decoder.feed(data)
    frames.extend(decoder.finish())
    return frames


def load_ndjson(path: Path) -> list[JSONObject]:
    """功能：从文件读取并严格解析 NDJSON 对象帧。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        return decode_ndjson_bytes(path.read_bytes())
    except OSError as exc:
        raise ConformanceError(f"cannot read {path}: {exc}") from exc


def _is_rpc_id(value: JSONValue) -> bool:
    """功能：判断值是否为协议允许的非布尔字符串或整数 ID。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return isinstance(value, (str, int)) and not isinstance(value, bool)


def _require_object(value: JSONValue, context: str) -> JSONObject:
    """功能：要求协议字段为 JSON 对象并返回其窄化类型。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, dict):
        raise ProtocolViolation(f"{context} must be a JSON object")
    return value


def _require_nonempty_string(value: JSONValue, context: str) -> str:
    """功能：要求协议字段为非空字符串并返回其窄化类型。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, str) or not value:
        raise ProtocolViolation(f"{context} must be a non-empty string")
    return value


def _require_unique_string_array(value: JSONValue, context: str) -> list[str]:
    """功能：验证能力字段是元素非空且无重复的字符串数组。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not isinstance(value, list) or not all(
        isinstance(item, str) and item for item in value
    ):
        raise ProtocolViolation(f"{context} must be a string array")
    if len(value) != len(set(value)):
        raise ProtocolViolation(f"{context} must not contain duplicates")
    return value


def _parse_utc_time(value: JSONValue) -> dt.datetime:
    """功能：解析并验证带尾随 Z 的 RFC 3339 UTC 事件时间。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    text = _require_nonempty_string(value, "event time")
    if not re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z", text):
        raise ProtocolViolation(
            f"event time is not an RFC 3339 UTC timestamp: {text!r}"
        )
    candidate = text[:-1] + "+00:00" if text.endswith("Z") else text
    try:
        timestamp = dt.datetime.fromisoformat(candidate)
    except ValueError as exc:
        raise ProtocolViolation(f"event time is not RFC 3339: {text!r}") from exc
    if timestamp.tzinfo is None or timestamp.utcoffset() != dt.timedelta(0):
        raise ProtocolViolation("event time must include an explicit UTC offset")
    return timestamp


class TraceValidator:
    """Validate cross-message JSON-RPC and asynchronous run invariants."""

    def __init__(self, requests: Sequence[JSONObject]) -> None:
        """功能：保存请求夹具并建立请求 ID 索引。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if not requests:
            raise ProtocolViolation("request fixture is empty")
        self.requests = list(requests)
        self.request_by_id: dict[str | int, JSONObject] = {}
        self.request_index: dict[str | int, int] = {}
        self.declared_event_types: set[str] = set()
        self._validate_requests()

    def _validate_requests(self) -> None:
        """功能：验证请求帧结构、ID 唯一性和 initialize 首帧约束。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        for index, request in enumerate(self.requests):
            if request.get("jsonrpc") != "2.0":
                raise ProtocolViolation(
                    f"request {index + 1} has invalid jsonrpc version"
                )
            request_id = request.get("id")
            if not _is_rpc_id(request_id):
                raise ProtocolViolation(
                    f"request {index + 1} must have an opaque string/integer id"
                )
            if request_id in self.request_by_id:
                raise ProtocolViolation(f"duplicate request id: {request_id!r}")
            _require_nonempty_string(
                request.get("method"), f"request {request_id!r} method"
            )
            if "params" in request:
                _require_object(request["params"], f"request {request_id!r} params")
            self.request_by_id[request_id] = request
            self.request_index[request_id] = index
        if self.requests[0].get("method") != "initialize":
            raise ProtocolViolation("the first request must be initialize")

    def validate(self, frames: Sequence[JSONObject]) -> None:
        """功能：验证响应关联、协商结果、事件顺序和唯一终态。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        responses: dict[str | int, tuple[int, JSONObject]] = {}
        accepted_runs: dict[str, tuple[str, int]] = {}
        terminal_runs: set[str] = set()
        last_seq_by_session: dict[str, int] = {}
        initialized_at: int | None = None

        for position, frame in enumerate(frames):
            if frame.get("jsonrpc") != "2.0":
                raise ProtocolViolation(
                    f"frame {position + 1} has invalid jsonrpc version"
                )
            if "id" in frame:
                response_id = frame["id"]
                if not _is_rpc_id(response_id):
                    raise ProtocolViolation(
                        f"response {position + 1} has an invalid id"
                    )
                if response_id not in self.request_by_id:
                    raise ProtocolViolation(
                        f"response for unknown request id: {response_id!r}"
                    )
                if response_id in responses:
                    raise ProtocolViolation(
                        f"duplicate response for request id: {response_id!r}"
                    )
                if "method" in frame:
                    raise ProtocolViolation(
                        "a JSON-RPC response must not contain method"
                    )
                has_result = "result" in frame
                has_error = "error" in frame
                if has_result == has_error:
                    raise ProtocolViolation(
                        "a response must contain exactly one of result or error"
                    )
                if has_error:
                    self._validate_error(frame["error"], f"response {response_id!r}")
                responses[response_id] = (position, frame)
                method = self.request_by_id[response_id]["method"]
                if method == "initialize" and has_result:
                    self._validate_initialize_result(
                        self.request_by_id[response_id], frame["result"]
                    )
                    initialized_at = position
                elif method == "faux/configure" and has_result:
                    result = _require_object(frame["result"], "faux/configure result")
                    scenario_id = _require_nonempty_string(
                        result.get("scenarioId"), "scenarioId"
                    )
                    params = _require_object(
                        self.request_by_id[response_id].get("params"),
                        "faux/configure params",
                    )
                    scenario = _require_object(
                        params.get("scenario"), "faux/configure scenario"
                    )
                    scenario_name = _require_nonempty_string(
                        scenario.get("name"), "faux/configure scenario.name"
                    )
                    if scenario_id != f"faux:{scenario_name}":
                        raise ProtocolViolation(
                            "scenarioId must equal 'faux:' plus the scenario name"
                        )
                elif method == "run/start" and has_result:
                    result = _require_object(frame["result"], "run/start result")
                    run_id = _require_nonempty_string(result.get("runId"), "runId")
                    if run_id in accepted_runs:
                        raise ProtocolViolation(f"runId was reused: {run_id!r}")
                    params = _require_object(
                        self.request_by_id[response_id].get("params"),
                        "run/start params",
                    )
                    session_id = _require_nonempty_string(
                        params.get("sessionId"), "run/start sessionId"
                    )
                    accepted_runs[run_id] = (session_id, position)
                continue

            if frame.get("method") != "event":
                raise ProtocolViolation(
                    f"server notification {position + 1} must use method 'event'"
                )
            if initialized_at is None:
                raise ProtocolViolation(
                    "event emitted before successful initialization"
                )
            params = _require_object(frame.get("params"), "event params")
            session_id = _require_nonempty_string(
                params.get("sessionId"), "event sessionId"
            )
            run_id = _require_nonempty_string(params.get("runId"), "event runId")
            event_type = _require_nonempty_string(params.get("type"), "event type")
            if event_type not in self.declared_event_types:
                raise ProtocolViolation(
                    f"event type {event_type!r} was not declared by initialize"
                )
            _require_object(params.get("data"), "event data")
            if "turnId" in params:
                _require_nonempty_string(params["turnId"], "event turnId")
            seq = params.get("seq")
            if (
                not isinstance(seq, int)
                or isinstance(seq, bool)
                or seq < 1
                or seq > 9_007_199_254_740_991
            ):
                raise ProtocolViolation("event seq must be a positive safe integer")
            previous_seq = last_seq_by_session.get(session_id)
            if previous_seq is not None and seq <= previous_seq:
                raise ProtocolViolation(
                    f"event seq for session {session_id!r} is not strictly increasing"
                )
            last_seq_by_session[session_id] = seq
            _parse_utc_time(params.get("time"))
            if run_id not in accepted_runs:
                raise ProtocolViolation(
                    f"event references unaccepted runId: {run_id!r}"
                )
            expected_session, accepted_at = accepted_runs[run_id]
            if session_id != expected_session:
                raise ProtocolViolation(
                    f"event sessionId does not match accepted run {run_id!r}"
                )
            if position <= accepted_at:
                raise ProtocolViolation(
                    f"event for {run_id!r} preceded run/start response"
                )
            if run_id in terminal_runs:
                raise ProtocolViolation(
                    f"event emitted after terminal event for {run_id!r}"
                )
            if event_type in TERMINAL_EVENTS:
                terminal_runs.add(run_id)

        missing_responses = [
            request_id
            for request_id in self.request_by_id
            if request_id not in responses
        ]
        if missing_responses:
            raise ProtocolViolation(
                f"missing responses for request ids: {missing_responses!r}"
            )
        initialize_id = self.requests[0]["id"]
        initialize_response = responses[initialize_id][1]
        if "result" not in initialize_response:
            raise ProtocolViolation("initialize did not complete successfully")
        missing_terminals = sorted(set(accepted_runs) - terminal_runs)
        if missing_terminals:
            raise ProtocolViolation(
                f"accepted runs missing terminal events: {missing_terminals!r}"
            )

    @staticmethod
    def _validate_error(value: JSONValue, context: str) -> None:
        """功能：验证可移植 JSON-RPC 结构化错误的必需字段。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        error = _require_object(value, f"{context} error")
        code = error.get("code")
        if not isinstance(code, int) or isinstance(code, bool):
            raise ProtocolViolation(f"{context} error code must be an integer")
        _require_nonempty_string(error.get("message"), f"{context} error message")
        if not isinstance(error.get("retryable"), bool):
            raise ProtocolViolation(f"{context} error retryable must be boolean")
        _require_object(error.get("details"), f"{context} error details")

    def _validate_initialize_result(
        self, request: JSONObject, value: JSONValue
    ) -> None:
        """功能：验证协议协商及当前测试实际依赖的诚实能力子集。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        result = _require_object(value, "initialize result")
        selected = _require_nonempty_string(
            result.get("protocolVersion"), "initialize protocolVersion"
        )
        params = _require_object(request.get("params"), "initialize params")
        offered = params.get("protocolVersions")
        if (
            not isinstance(offered, list)
            or not offered
            or not all(isinstance(item, str) and item for item in offered)
        ):
            raise ProtocolViolation(
                "initialize protocolVersions must be a non-empty string array"
            )
        if selected not in offered:
            raise ProtocolViolation(
                f"daemon selected unoffered protocol version: {selected!r}"
            )
        implementation = _require_object(
            result.get("implementation"), "initialize implementation"
        )
        for field in ("name", "version", "language"):
            _require_nonempty_string(
                implementation.get(field), f"initialize implementation.{field}"
            )
        capabilities = _require_object(
            result.get("capabilities"), "initialize capabilities"
        )
        methods = set(
            _require_unique_string_array(
                capabilities.get("methods"), "initialize capabilities.methods"
            )
        )
        required_methods = {
            str(item["method"])
            for item in self.requests
            if isinstance(item.get("method"), str)
        }
        missing_methods = sorted(required_methods - methods)
        if missing_methods:
            raise ProtocolViolation(
                f"initialize omitted exercised methods: {missing_methods!r}"
            )

        event_types = set(
            _require_unique_string_array(
                capabilities.get("eventTypes"),
                "initialize capabilities.eventTypes",
            )
        )
        missing_terminals = sorted(TERMINAL_EVENTS - event_types)
        if missing_terminals:
            raise ProtocolViolation(
                f"initialize omitted terminal event types: {missing_terminals!r}"
            )
        self.declared_event_types = event_types

        providers = capabilities.get("providers")
        if not isinstance(providers, list) or not providers:
            raise ProtocolViolation(
                "initialize capabilities.providers must be non-empty"
            )
        provider_ids: set[str] = set()
        faux_models: list[str] | None = None
        for index, raw_provider in enumerate(providers):
            provider = _require_object(
                raw_provider, f"initialize capabilities.providers[{index}]"
            )
            provider_id = _require_nonempty_string(
                provider.get("id"),
                f"initialize capabilities.providers[{index}].id",
            )
            if provider_id in provider_ids:
                raise ProtocolViolation(
                    f"initialize capabilities.providers duplicated id {provider_id!r}"
                )
            provider_ids.add(provider_id)
            models = _require_unique_string_array(
                provider.get("models"),
                f"initialize capabilities.providers[{index}].models",
            )
            if provider_id == "faux":
                faux_models = models
        if faux_models is None or "faux-v1" not in faux_models:
            raise ProtocolViolation(
                "initialize must advertise the exercised faux/faux-v1 provider"
            )

        _require_unique_string_array(
            capabilities.get("tools"), "initialize capabilities.tools"
        )
        transports = _require_unique_string_array(
            capabilities.get("transports"), "initialize capabilities.transports"
        )
        if "stdio" not in transports:
            raise ProtocolViolation(
                "initialize must advertise exercised stdio transport"
            )
        _require_object(result.get("limits"), "initialize limits")


class TraceNormalizer:
    """Replace nondeterministic wire values while preserving public semantics."""

    ID_PREFIXES: Mapping[str, str] = {
        "runId": "run",
        "turnId": "turn",
        "messageId": "message",
        "callId": "call",
        "toolCallId": "tool-call",
        "artifactId": "artifact",
        "approvalId": "approval",
    }

    def __init__(self) -> None:
        """功能：初始化每一类 opaque ID 的确定性映射表。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self._ids: dict[str, dict[str, str]] = {field: {} for field in self.ID_PREFIXES}

    def normalize(self, frames: Sequence[JSONObject]) -> list[JSONObject]:
        """功能：规范化 opaque ID、事件时间和实现身份以便差分。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        normalized: list[JSONObject] = []
        for raw_frame in frames:
            frame = copy.deepcopy(raw_frame)
            self._normalize_value(frame)
            if frame.get("method") == "event":
                params = frame.get("params")
                if isinstance(params, dict) and "time" in params:
                    params["time"] = "$TIME"
            result = frame.get("result")
            if isinstance(result, dict):
                implementation = result.get("implementation")
                if isinstance(implementation, dict):
                    implementation["name"] = "$IMPLEMENTATION"
                    implementation["version"] = "$VERSION"
                    implementation["language"] = "$LANGUAGE"
                if isinstance(result.get("capabilities"), dict):
                    result["capabilities"] = copy.deepcopy(
                        NORMALIZED_SERVER_CAPABILITIES
                    )
            normalized.append(frame)
        return normalized

    def _normalize_value(self, value: JSONValue) -> None:
        """功能：递归规范化嵌套对象中的已命名 opaque ID 字段。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if isinstance(value, list):
            for item in value:
                self._normalize_value(item)
            return
        if not isinstance(value, dict):
            return
        for key, item in list(value.items()):
            if key in self.ID_PREFIXES and isinstance(item, str):
                mapping = self._ids[key]
                if item not in mapping:
                    mapping[item] = f"{self.ID_PREFIXES[key]}-{len(mapping) + 1}"
                value[key] = mapping[item]
            else:
                self._normalize_value(item)


def canonical_ndjson(frames: Iterable[JSONObject]) -> str:
    """功能：把消息序列化为稳定键序的紧凑 UTF-8 NDJSON 文本。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    return "".join(
        json.dumps(frame, ensure_ascii=False, separators=(",", ":"), sort_keys=True)
        + "\n"
        for frame in frames
    )


def compare_golden(
    actual: Sequence[JSONObject], expected: Sequence[JSONObject]
) -> None:
    """功能：精确比较规范化轨迹，并生成可读 unified diff。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if list(actual) == list(expected):
        return
    actual_text = canonical_ndjson(actual).splitlines(keepends=True)
    expected_text = canonical_ndjson(expected).splitlines(keepends=True)
    diff = "".join(
        difflib.unified_diff(
            expected_text,
            actual_text,
            fromfile="golden",
            tofile="actual-normalized",
        )
    )
    raise GoldenMismatch("normalized trace differs from golden:\n" + diff, actual)


def _safe_executable_label(command: Sequence[str]) -> str:
    """功能：从 daemon argv 仅提取有界、无控制字符的可执行文件标签。

    输入：不可信且可能在后续参数中含敏感值的 argv。
    输出：最多 256 个字符的 argv[0] 文件名标签，不包含其他参数。
    不变量：绝不拼接 argv[1:] 或环境变量。
    失败：空 argv 返回固定占位符，不抛出异常。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    if not command:
        return "<missing>"
    raw = command[0]
    label = Path(raw).name or raw
    sanitized = "".join(
        character if character.isprintable() and character not in "\r\n\t" else "?"
        for character in label
    )
    return (sanitized or "<unnamed>")[:256]


class DaemonProcess:
    """Run one daemon command and collect strict protocol frames from stdout."""

    def __init__(
        self,
        command: Sequence[str],
        *,
        timeout: float,
        max_frame_bytes: int,
        stderr_limit: int = 131_072,
        extra_env: Mapping[str, str] | None = None,
        removed_env: Sequence[str] | None = None,
        pause_stdout_after: Callable[[JSONObject], bool] | None = None,
    ) -> None:
        """功能：以可清理环境启动 daemon 并建立 stdout/stderr 收集线程。

        输入：安全 argv、超时/帧/诊断上限、可选环境删除/注入项和 stdout 帧后暂停谓词。
        输出：可逐帧驱动并最终关闭的 daemon 进程包装器。
        不变量：启动失败诊断只含 argv[0] 标签，绝不输出完整 argv 或环境。
        失败：空命令、非法限制或进程无法创建时抛出 ConformanceError。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if not command:
            raise ConformanceError("daemon command is empty")
        if timeout <= 0 or max_frame_bytes <= 0 or stderr_limit <= 0:
            raise ConformanceError("daemon process limits must be positive")
        self.command = list(command)
        self.executable_label = _safe_executable_label(command)
        self.timeout = timeout
        self.max_frame_bytes = max_frame_bytes
        self.stderr_limit = stderr_limit
        self._items: queue.Queue[tuple[str, Any]] = queue.Queue()
        self._stderr = bytearray()
        self._stderr_lock = threading.Lock()
        self._stderr_updated = threading.Event()
        self._frame_wire_sizes: dict[int, int] = {}
        self._frame_wire_sizes_lock = threading.Lock()
        self._tracked_frame_count = 0
        self._tracked_frame_bytes = 0
        self._pause_stdout_after = pause_stdout_after
        self._stdout_paused = threading.Event()
        self._stdout_resume = threading.Event()
        env = os.environ.copy()
        if removed_env is not None:
            for name in removed_env:
                env.pop(name, None)
        env.setdefault("QXNM_FORGE_CONFORMANCE", "1")
        if extra_env is not None:
            env.update(extra_env)
        process_options: dict[str, Any] = {}
        if os.name == "posix":
            process_options["start_new_session"] = True
        elif os.name == "nt":
            process_options["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
        try:
            self.process = subprocess.Popen(
                self.command,
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                env=env,
                **process_options,
            )
        except OSError as exc:
            errno_text = f", errno={exc.errno}" if exc.errno is not None else ""
            raise ConformanceError(
                "cannot launch daemon "
                f"executable={self.executable_label!r} "
                f"({type(exc).__name__}{errno_text})"
            ) from exc
        assert self.process.stdout is not None
        assert self.process.stderr is not None
        self._stdout_thread = threading.Thread(
            target=self._read_stdout,
            args=(self.process.stdout,),
            name="conformance-stdout",
            daemon=True,
        )
        self._stderr_thread = threading.Thread(
            target=self._read_stderr,
            args=(self.process.stderr,),
            name="conformance-stderr",
            daemon=True,
        )
        self._stdout_thread.start()
        self._stderr_thread.start()

    def run(
        self, requests: Sequence[JSONObject], settle_seconds: float
    ) -> list[JSONObject]:
        """功能：逐请求驱动 daemon，收集至所有 run 出现唯一终态。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        frames: list[JSONObject] = []
        accepted_runs: set[str] = set()
        try:
            for request in requests:
                request_id = request["id"]
                if any(frame.get("id") == request_id for frame in frames):
                    raise ProtocolViolation(
                        f"daemon responded before request was sent: {request_id!r}"
                    )
                self._send(request)
                while not any(frame.get("id") == request_id for frame in frames):
                    frame = self._next_frame(self.timeout)
                    if "id" in frame and frame.get("id") != request_id:
                        raise ProtocolViolation(
                            f"unexpected response while awaiting {request_id!r}: "
                            f"{frame.get('id')!r}"
                        )
                    frames.append(frame)
                response = next(
                    frame for frame in reversed(frames) if frame.get("id") == request_id
                )
                if request.get("method") == "run/start" and isinstance(
                    response.get("result"), dict
                ):
                    run_id = response["result"].get("runId")
                    if isinstance(run_id, str) and run_id:
                        accepted_runs.add(run_id)

            deadline = time.monotonic() + self.timeout
            while accepted_runs - self._terminal_run_ids(frames):
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    missing = sorted(accepted_runs - self._terminal_run_ids(frames))
                    raise ConformanceError(
                        f"timed out waiting for terminal events: {missing!r}"
                    )
                frames.append(self._next_frame(remaining))

            # 终态后短暂排空 stdout，确保紧随其后的非法事件不会逃过检查。
            quiet_deadline = time.monotonic() + settle_seconds
            while True:
                remaining = quiet_deadline - time.monotonic()
                if remaining <= 0:
                    break
                try:
                    frames.append(self._next_frame(remaining))
                    quiet_deadline = time.monotonic() + settle_seconds
                except queue.Empty:
                    break
            return frames
        finally:
            self.close()

    def _send(self, request: JSONObject) -> None:
        """功能：把单个请求编码为 UTF-8 NDJSON 并刷新到 daemon stdin。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if self.process.poll() is not None:
            raise ConformanceError(
                self._early_exit_message("before request could be sent")
            )
        assert self.process.stdin is not None
        data = (
            json.dumps(request, ensure_ascii=False, separators=(",", ":")) + "\n"
        ).encode("utf-8")
        try:
            self.process.stdin.write(data)
            self.process.stdin.flush()
        except (BrokenPipeError, OSError) as exc:
            raise ConformanceError(
                self._early_exit_message("while writing request")
            ) from exc

    def send_request(self, request: JSONObject) -> None:
        """功能：供动态 conformance 驱动器发送一个严格 JSON-RPC 请求帧。

        输入：不含任意 Python 对象的 JSON-RPC 请求对象。
        输出：成功写入并刷新时无返回值。
        不变量：始终使用 UTF-8 NDJSON，stdout 仍只由读取线程消费。
        失败：daemon 已退出或管道写入失败时抛出不含完整 argv/环境的错误。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self._send(request)

    def _next_frame(self, timeout: float) -> JSONObject:
        """功能：在超时内取得下一协议帧或传播读取线程错误。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        try:
            kind, value = self._items.get(timeout=timeout)
        except queue.Empty:
            if self.process.poll() is not None:
                raise ConformanceError(
                    self._early_exit_message("before trace completed")
                )
            raise
        if kind == "frame":
            return value
        if kind == "error":
            if isinstance(value, ProtocolViolation):
                raise ProtocolViolation(
                    f"{value}\n{self._diagnostic_context()}"
                ) from value
            raise ConformanceError(
                "daemon stdout reader failed\n" + self._diagnostic_context()
            ) from value
        if kind == "eof":
            raise ConformanceError(
                self._early_exit_message("closed stdout before trace completed")
            )
        raise AssertionError(f"unknown queue item: {kind}")

    def next_frame(self, timeout: float) -> JSONObject:
        """功能：供动态 conformance 驱动器读取一个严格协议帧。

        输入：正数等待秒数。
        输出：下一个严格解码的 JSON 对象帧。
        不变量：普通 stdout 日志必然成为协议错误并附安全有界诊断。
        失败：超时、EOF、无效 JSON/UTF-8 或读取线程异常时传播确定性错误。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if timeout <= 0:
            raise ValueError("frame timeout must be positive")
        return self._next_frame(timeout)

    def _read_stdout(self, stream: BinaryIO) -> None:
        """功能：后台严格解析协议专用 stdout 并传递消息或错误。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        decoder = NDJSONDecoder(
            max_frame_bytes=self.max_frame_bytes,
            frame_bytes_callback=self._record_frame_wire_bytes,
        )
        try:
            while True:
                chunk = (
                    stream.read1(4096)
                    if hasattr(stream, "read1")
                    else stream.read(4096)
                )
                if not chunk:
                    break
                # RPC 模式 stdout 只能包含协议帧；普通日志也会被判为失败。
                for frame in decoder.feed(chunk):
                    self._items.put(("frame", frame))
                    if (
                        self._pause_stdout_after is not None
                        and self._pause_stdout_after(frame)
                    ):
                        self._stdout_paused.set()
                        self._stdout_resume.wait()
            for frame in decoder.finish():
                self._items.put(("frame", frame))
        except BaseException as exc:
            self._items.put(("error", exc))
        finally:
            self._items.put(("eof", None))

    def _record_frame_wire_bytes(self, frame: JSONObject, wire_bytes: int) -> None:
        """功能：在线程安全的有界账本中登记一个已严格解析帧的实际 wire 字节数。

        输入：decoder 刚创建的对象帧及其 LF 前 payload 字节数。
        输出：账本增加一条 `id(frame) -> wireBytes` 记录。
        不变量：最多登记 65536 帧/64 MiB payload；不复制或序列化帧正文，不记录 LF 后内容。
        失败：帧数或累计字节超过监督预算时抛出 ProtocolViolation，使 stdout reader 失败关闭。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if wire_bytes <= 0:
            raise ProtocolViolation("NDJSON decoder reported an invalid wire frame size")
        with self._frame_wire_sizes_lock:
            self._tracked_frame_count += 1
            self._tracked_frame_bytes += wire_bytes
            if (
                self._tracked_frame_count > 65_536
                or self._tracked_frame_bytes > 64 * 1024 * 1024
            ):
                raise ProtocolViolation("daemon exceeded bounded wire-size frame tracking")
            self._frame_wire_sizes[id(frame)] = wire_bytes

    def frame_wire_bytes(self, frame: JSONObject) -> int:
        """功能：查询 `next_frame` 返回对象在 stdout 上的实际 LF 前字节数。

        输入：由当前 DaemonProcess 解码并返回、身份未改变的 frame 对象。
        输出：包含 JSON whitespace/escaping/可选 CR 的正 payload 字节数。
        不变量：查询不重新序列化动态内容，因而可证明实际 maxFrame/maxEvent 边界。
        失败：外来、复制或未登记对象抛出 ConformanceError，不猜测 canonical 大小。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with self._frame_wire_sizes_lock:
            size = self._frame_wire_sizes.get(id(frame))
        if size is None:
            raise ConformanceError("wire size is unavailable for the supplied daemon frame")
        return size

    def _read_stderr(self, stream: BinaryIO) -> None:
        """功能：后台有界收集 daemon 诊断输出，避免阻塞进程管道。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        while True:
            chunk = (
                stream.read1(4096) if hasattr(stream, "read1") else stream.read(4096)
            )
            if not chunk:
                return
            with self._stderr_lock:
                self._stderr.extend(chunk)
                if len(self._stderr) > self.stderr_limit:
                    del self._stderr[: len(self._stderr) - self.stderr_limit]
            self._stderr_updated.set()

    @staticmethod
    def _terminal_run_ids(frames: Sequence[JSONObject]) -> set[str]:
        """功能：从事件轨迹提取已出现终态的 run ID 集合。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        result: set[str] = set()
        for frame in frames:
            if frame.get("method") != "event" or not isinstance(
                frame.get("params"), dict
            ):
                continue
            params = frame["params"]
            if params.get("type") in TERMINAL_EVENTS and isinstance(
                params.get("runId"), str
            ):
                result.add(params["runId"])
        return result

    def stderr_text(self, limit: int = 4096) -> str:
        """功能：把 stderr 最后若干字节安全解码为进一步有界的诊断文本。

        输入：正整数尾部字节上限，默认 4096。
        输出：替换无效 UTF-8 后的 stderr 尾部文本。
        不变量：返回内容不超过内部存储上限，且从不包含环境或 argv 注入信息。
        失败：非正上限抛出 ValueError。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if limit <= 0:
            raise ValueError("stderr diagnostic limit must be positive")
        with self._stderr_lock:
            return bytes(self._stderr[-limit:]).decode("utf-8", errors="replace")

    def _diagnostic_context(self) -> str:
        """功能：组合安全可执行文件标签与最多 4096 字节 stderr 尾部。

        输入：无显式输入，读取当前 daemon 的有界诊断状态。
        输出：不含完整 argv 和环境的多行诊断上下文。
        不变量：只显示 argv[0] 的文件名标签；stderr 已由存储及返回上限双重约束。
        失败：无 stderr 时仍返回稳定的 executable 上下文。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if not self._stderr:
            self._stderr_updated.wait(0.05)
        stderr = self.stderr_text().strip()
        context = f"executable={self.executable_label!r}"
        return context + (f"\nstderr (tail):\n{stderr}" if stderr else "")

    def _early_exit_message(self, context: str) -> str:
        """功能：组合 daemon 提前退出状态与安全有界诊断上下文。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        return_code = self.process.poll()
        message = f"daemon {context} (exit={return_code})"
        return message + "\n" + self._diagnostic_context()

    def close(self) -> None:
        """功能：关闭管道并在需要时终止 daemon 的整个进程树。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self._stdout_resume.set()
        self.close_stdin()
        try:
            self.process.wait(timeout=0.5)
        except subprocess.TimeoutExpired:
            self._terminate_tree()
            try:
                self.process.wait(timeout=1.0)
            except subprocess.TimeoutExpired:
                self._kill_tree()
                self.process.wait(timeout=1.0)
        self._stdout_thread.join(timeout=0.5)
        self._stderr_thread.join(timeout=0.5)
        if self.process.stdout is not None:
            self.process.stdout.close()
        if self.process.stderr is not None:
            self.process.stderr.close()

    def close_stdin(self) -> None:
        """功能：只关闭 daemon stdin 以注入 clean EOF，同时保留 stdout/stderr 观察能力。

        输入：当前子进程的父端 stdin pipe。
        输出：关闭或已经关闭时无返回值。
        不变量：不向进程发送 signal，也不关闭 stdout，因而 daemon 仍可完成 fail-closed 事件与 journal。
        失败：pipe 已由并发退出关闭时安全忽略底层 OSError。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if self.process.stdin is not None and not self.process.stdin.closed:
            try:
                self.process.stdin.close()
            except OSError:
                pass

    def break_stdout_delivery(self, timeout: float) -> None:
        """功能：在预配置帧后暂停点关闭 stdout 读端，确定性注入 response delivery failure。

        输入：等待 reader 到达暂停点的正秒数。
        输出：父端 stdout 已关闭且 reader 获准退出时无返回值。
        不变量：只有构造时的暂停谓词命中后才能调用；关闭发生在下一 response 写入前。
        失败：未配置/未到达暂停点、非法 timeout 或 pipe 状态异常时抛出 ConformanceError。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if timeout <= 0:
            raise ValueError("stdout delivery break timeout must be positive")
        if self._pause_stdout_after is None:
            raise ConformanceError("stdout delivery break requires a pause predicate")
        if not self._stdout_paused.wait(timeout):
            raise ConformanceError("stdout reader did not reach the delivery pause point")
        if self.process.stdout is None or self.process.stdout.closed:
            raise ConformanceError("daemon stdout was already closed")
        try:
            self.process.stdout.close()
        except OSError as exc:
            raise ConformanceError("cannot close daemon stdout read end") from exc
        finally:
            self._stdout_resume.set()

    def force_kill(self, timeout: float) -> int:
        """功能：强制终止 runner 自建 daemon 进程组并有界等待退出。

        输入：强制 signal 后的正等待秒数。
        输出：子进程退出码。
        不变量：POSIX 使用独立进程组 SIGKILL，其他平台使用 Popen.kill；不影响 runner 外进程。
        失败：timeout 非正或进程树在期限内未退出时抛出 ConformanceError。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if timeout <= 0:
            raise ValueError("force-kill timeout must be positive")
        self._stdout_resume.set()
        self._kill_tree()
        return self.wait_for_exit(timeout)

    def wait_for_exit(self, timeout: float) -> int:
        """功能：有界等待 daemon 自然退出而不主动发送终止信号。

        输入：正等待秒数。
        输出：子进程退出码。
        不变量：本方法不关闭任何 pipe、不修改环境，也不杀进程。
        失败：timeout 非正或期限内未退出时抛出 ConformanceError。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if timeout <= 0:
            raise ValueError("process exit timeout must be positive")
        try:
            return self.process.wait(timeout=timeout)
        except subprocess.TimeoutExpired as exc:
            raise ConformanceError("daemon did not exit within the fault deadline") from exc

    def _terminate_tree(self) -> None:
        """功能：向 daemon 进程组发送温和终止请求。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        try:
            if os.name == "posix":
                os.killpg(self.process.pid, signal.SIGTERM)
            else:
                self.process.terminate()
        except (OSError, ProcessLookupError):
            pass

    def _kill_tree(self) -> None:
        """功能：温和终止超时后强制杀死 daemon 进程组。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        try:
            if os.name == "posix":
                os.killpg(self.process.pid, signal.SIGKILL)
            else:
                self.process.kill()
        except (OSError, ProcessLookupError):
            pass


def run_conformance(
    command: Sequence[str],
    requests_path: Path,
    golden_path: Path,
    *,
    timeout: float = 5.0,
    settle_seconds: float = 0.05,
    max_frame_bytes: int = 1_048_576,
    schema_root: Path | None = None,
) -> list[JSONObject]:
    """功能：执行一次完整黑盒运行、语义校验、规范化和 golden 比较。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    requests = load_ndjson(requests_path)
    golden = load_ndjson(golden_path)
    process = DaemonProcess(
        command,
        timeout=timeout,
        max_frame_bytes=max_frame_bytes,
    )
    try:
        frames = process.run(requests, settle_seconds=settle_seconds)
    except queue.Empty as exc:
        raise ConformanceError(
            f"daemon did not produce the expected response within {timeout:.3f}s"
        ) from exc
    TraceValidator(requests).validate(frames)
    if schema_root is not None:
        from schema_validation import SchemaValidationError, validate_protocol_trace

        try:
            validate_protocol_trace(requests, frames, schema_root)
        except SchemaValidationError as exc:
            raise ProtocolViolation(str(exc)) from exc
    normalized = TraceNormalizer().normalize(frames)
    compare_golden(normalized, golden)
    return normalized


def build_argument_parser() -> argparse.ArgumentParser:
    """功能：构建黑盒 runner 的命令行参数解析器。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser(
        description="Launch a qxnm-forge daemon and validate a normalized JSON-RPC trace."
    )
    parser.add_argument(
        "--requests", required=True, type=Path, help="input NDJSON requests"
    )
    parser.add_argument(
        "--golden", required=True, type=Path, help="normalized golden NDJSON"
    )
    parser.add_argument(
        "--actual", type=Path, help="write normalized actual trace here"
    )
    parser.add_argument(
        "--timeout", type=float, default=5.0, help="response/run timeout seconds"
    )
    parser.add_argument(
        "--settle-seconds",
        type=float,
        default=0.05,
        help="quiet window after terminal events",
    )
    parser.add_argument(
        "--max-frame-bytes", type=int, default=1_048_576, help="stdout frame limit"
    )
    parser.add_argument(
        "--schema-root",
        type=Path,
        help="optionally validate actual frames against SPEC Draft 2020-12 schemas",
    )
    parser.add_argument(
        "command", nargs=argparse.REMAINDER, help="-- daemon command and args"
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    """功能：执行 CLI 并把一致性结果映射为稳定退出码。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = build_argument_parser().parse_args(argv)
    command = list(args.command)
    if command and command[0] == "--":
        command = command[1:]
    if not command:
        print("conformance: a daemon command is required after --", file=sys.stderr)
        return 2
    if args.timeout <= 0 or args.settle_seconds < 0 or args.max_frame_bytes <= 0:
        print("conformance: timeout/frame options are out of range", file=sys.stderr)
        return 2
    try:
        normalized = run_conformance(
            command,
            args.requests,
            args.golden,
            timeout=args.timeout,
            settle_seconds=args.settle_seconds,
            max_frame_bytes=args.max_frame_bytes,
            schema_root=args.schema_root,
        )
        if args.actual:
            args.actual.parent.mkdir(parents=True, exist_ok=True)
            args.actual.write_text(canonical_ndjson(normalized), encoding="utf-8")
    except GoldenMismatch as exc:
        if args.actual:
            args.actual.parent.mkdir(parents=True, exist_ok=True)
            args.actual.write_text(canonical_ndjson(exc.actual), encoding="utf-8")
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1
    except ConformanceError as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1
    print(f"PASS: {len(normalized)} frames conform")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
