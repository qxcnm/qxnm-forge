#!/usr/bin/env python3
"""OpenRouter Images artifact-first 本机 mock 与 daemon 黑盒一致性 runner。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import argparse
import base64
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import queue
import secrets
import sys
import tempfile
import threading
import time
from typing import Any, Sequence

import runner

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_FIXTURE = ROOT / "CONFORMANCE/fixtures/provider/mock-cases-images.json"
DEFAULT_GOLDEN_REQUESTS = ROOT / "CONFORMANCE/fixtures/golden/openrouter-images.requests.ndjson"
DEFAULT_GOLDEN_TRACE = ROOT / "CONFORMANCE/fixtures/golden/openrouter-images.trace.ndjson"
JSONObject = dict[str, Any]
PNG = b"\x89PNG\r\n\x1a\nqxnm-forge-image-fixture"
JPEG = b"\xff\xd8\xff\xe0qxnm-forge-image-fixture"
WEBP = b"RIFF\x16\x00\x00\x00WEBPqxnm-forge-image"
GIF = b"GIF89aqxnm-forge-image-fixture"
MEDIA_BYTES = {"image/png": PNG, "image/jpeg": JPEG, "image/webp": WEBP, "image/gif": GIF}
TERMINAL_TYPES = {"run.completed", "run.failed", "run.cancelled", "run.interrupted"}
CREDENTIAL_ENV = (
    "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "AZURE_OPENAI_API_KEY", "AZURE_CLIENT_SECRET",
    "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN",
    "AWS_BEARER_TOKEN_BEDROCK", "AWS_PROFILE", "AWS_WEB_IDENTITY_TOKEN_FILE",
    "GOOGLE_API_KEY", "GEMINI_API_KEY", "GOOGLE_APPLICATION_CREDENTIALS",
    "MISTRAL_API_KEY", "OPENROUTER_API_KEY", "GITHUB_TOKEN", "GH_TOKEN",
    "QXNM_FORGE_GOOGLE_VERTEX_OAUTH_TOKEN", "QXNM_FORGE_OPENAI_CODEX_OAUTH_TOKEN",
)
ENDPOINT_ENV = (
    "QXNM_FORGE_OPENAI_COMPATIBLE_ENDPOINT", "QXNM_FORGE_OPENAI_RESPONSES_ENDPOINT",
    "QXNM_FORGE_ANTHROPIC_ENDPOINT", "QXNM_FORGE_MISTRAL_CONVERSATIONS_ENDPOINT",
    "QXNM_FORGE_AZURE_OPENAI_RESPONSES_ENDPOINT", "QXNM_FORGE_GOOGLE_GENERATIVE_AI_ENDPOINT",
    "QXNM_FORGE_GOOGLE_VERTEX_ENDPOINT", "QXNM_FORGE_BEDROCK_CONVERSE_STREAM_ENDPOINT",
    "QXNM_FORGE_OPENAI_CODEX_RESPONSES_ENDPOINT", "QXNM_FORGE_OPENROUTER_IMAGES_ENDPOINT",
)
PROXY_ENV = (
    "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
    "http_proxy", "https_proxy", "all_proxy", "no_proxy",
)


class ImageRunnerError(Exception):
    """表示图像 fixture、mock、协议、Session 或泄漏检查失败。"""


def strict_load(path: Path) -> JSONObject:
    """功能：严格加载图像 fixture 并拒绝重复键、非有限数和非对象根。

    输入：仓库内 JSON fixture 路径。
    输出：字符串键 JSON 对象。
    不变量：不执行 fixture，也不访问远程引用。
    失败：I/O、UTF-8、JSON、重复键或根类型非法时抛 ImageRunnerError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    def pairs(values: list[tuple[str, Any]]) -> JSONObject:
        """功能：把无重复属性对构造成对象。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        result: JSONObject = {}
        for key, value in values:
            if key in result:
                raise ImageRunnerError("image fixture contains a duplicate key")
            result[key] = value
        return result

    try:
        value = json.loads(
            path.read_text(encoding="utf-8"),
            object_pairs_hook=pairs,
            parse_constant=lambda _: (_ for _ in ()).throw(
                ImageRunnerError("image fixture contains a non-finite number")
            ),
        )
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ImageRunnerError("image fixture cannot be loaded") from exc
    if not isinstance(value, dict):
        raise ImageRunnerError("image fixture root must be an object")
    return value


def validate_schema(fixture: JSONObject, schema_root: Path) -> None:
    """功能：使用 bundled Draft 2020-12 Schema 验证图像 fixture。

    输入：已严格解码的 fixture 与 SPEC/schemas 根。
    输出：Schema 与实例有效时无返回值。
    不变量：仅注册本地 Schema，不解析网络引用。
    失败：缺依赖、Schema 或实例违规时抛 ImageRunnerError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        import jsonschema
        from referencing import Registry, Resource
    except ImportError as exc:
        raise ImageRunnerError("jsonschema/referencing is required") from exc
    resources: list[tuple[str, Resource[Any]]] = []
    schemas: dict[str, JSONObject] = {}
    for path in sorted(schema_root.rglob("*.schema.json")):
        document = json.loads(path.read_text(encoding="utf-8"))
        schemas[path.name] = document
        resources.append((document["$id"], Resource.from_contents(document)))
    validator = jsonschema.Draft202012Validator(
        schemas["provider-image-mock-cases.schema.json"],
        registry=Registry().with_resources(resources),
    )
    errors = list(validator.iter_errors(fixture))
    if errors:
        raise ImageRunnerError("image fixture violates its Schema")


def validate_semantics(fixture: JSONObject) -> tuple[JSONObject, list[JSONObject]]:
    """功能：冻结图像 family、限制与十三个案例的精确语义。

    输入：已通过基础 JSON/Schema 的 fixture。
    输出：family 与按 fixture 顺序的案例。
    不变量：无 remote endpoint、任意命令、凭据值或真实图像文件进入夹具。
    失败：标识、计数、模型、环境、限制或案例集合漂移时抛 ImageRunnerError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    family = fixture.get("family")
    cases = fixture.get("cases")
    limits = fixture.get("limits")
    if not isinstance(family, dict) or not isinstance(cases, list) or not isinstance(limits, dict):
        raise ImageRunnerError("image fixture sections are invalid")
    if (
        fixture.get("suiteId") != "openrouter-images-artifact-v1"
        or family.get("providerId") != "openrouter"
        or family.get("apiFamily") != "openrouter-images"
        or family.get("modelId") != "google/gemini-2.5-flash-image"
        or family.get("endpointEnv") != "QXNM_FORGE_OPENROUTER_IMAGES_ENDPOINT"
        or family.get("credentialEnv") != "OPENROUTER_API_KEY"
        or limits
        != {
            "maxInputImages": 8,
            "maxOutputImages": 8,
            "maxInputImageBytes": 16_777_216,
            "maxOutputImageBytes": 33_554_432,
            "maxResponseBytes": 50_331_648,
            "maxTextBytes": 262_144,
        }
    ):
        raise ImageRunnerError("image fixture frozen contract drifted")
    names = [case.get("name") for case in cases if isinstance(case, dict)]
    expected_names = [
        "success_png_text", "success_jpeg", "success_webp", "success_gif",
        "remote_url_rejected", "invalid_base64_rejected", "mime_mismatch_rejected",
        "svg_rejected", "rate_limit_retry", "server_error_retry", "disconnect",
        "idle_timeout", "cancellation",
    ]
    if names != expected_names:
        raise ImageRunnerError("image fixture cases are incomplete or reordered")
    return family, cases


def validate_golden(request_path: Path, trace_path: Path) -> None:
    """功能：验证图像 golden 冻结了品牌中立 route 与 artifact-first 混合轨迹。

    输入：请求 NDJSON 与规范化 event/journal trace NDJSON。
    输出：请求、事件和 durable 顺序满足 ADR 0015 时无返回值。
    不变量：golden 不含真实 credential、base64、data URL、host path 或真实 opaque ID。
    失败：结构、顺序、路由或占位符漂移时抛 ImageRunnerError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    requests = runner.load_ndjson(request_path)
    trace = runner.load_ndjson(trace_path)
    if (
        len(requests) != 2
        or requests[0].get("method") != "initialize"
        or requests[1].get("method") != "run/start"
        or requests[1].get("params", {}).get("provider", {}).get("apiFamily")
        != "openrouter-images"
    ):
        raise ImageRunnerError("image golden requests are invalid")
    kinds = [item.get("journalKind") or item.get("params", {}).get("type") for item in trace]
    expected = [
        None,
        None,
        "run.started",
        "turn.started",
        "message.started",
        "artifact.created",
        "message.appended",
        "message.completed",
        "run.completed",
    ]
    if kinds != expected:
        raise ImageRunnerError("image golden durable/event order drifted")
    raw = request_path.read_bytes() + trace_path.read_bytes()
    if b"data:image" in raw or b";base64," in raw or b"OPENROUTER_API_KEY" in raw:
        raise ImageRunnerError("image golden contains forbidden material")


def parse_command_json(value: str) -> list[str]:
    """功能：把 CLI JSON 安全解析为不经过 shell 的非空 daemon argv。

    输入：JSON 数组字符串。
    输出：最多 128 个无 NUL 的非空字符串。
    不变量：错误不回显命令内容。
    失败：结构或边界无效时抛 argparse.ArgumentTypeError。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        parsed = json.loads(value)
    except json.JSONDecodeError as exc:
        raise argparse.ArgumentTypeError("daemon command must be JSON argv") from exc
    if (
        not isinstance(parsed, list)
        or not parsed
        or len(parsed) > 128
        or not all(isinstance(item, str) and item and "\x00" not in item for item in parsed)
    ):
        raise argparse.ArgumentTypeError("daemon command must be bounded JSON argv")
    return parsed


class MockState:
    """保存线程安全的每案例请求计数与脱敏观察。"""

    def __init__(self, fixture: JSONObject, canary: str) -> None:
        """功能：初始化 fixture/canary 与空观察列表。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.fixture = fixture
        self.canary = canary
        self.lock = threading.Lock()
        self.counts: dict[str, int] = {}
        self.observed: list[JSONObject] = []

    def case(self, name: str) -> JSONObject:
        """功能：按固定名称取得唯一图像案例。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        cases = self.fixture["cases"]
        match = [item for item in cases if isinstance(item, dict) and item.get("name") == name]
        if len(match) != 1:
            raise ImageRunnerError("mock received an unknown image case")
        return match[0]

    def record(self, name: str, valid: bool) -> int:
        """功能：原子增加案例请求计数并只保存认证/请求形状布尔值。

        输入：案例名与请求是否完整符合契约。
        输出：该案例的一基请求序号。
        不变量：不保存 header 值、prompt、body、base64 或 credential。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with self.lock:
            count = self.counts.get(name, 0) + 1
            self.counts[name] = count
            self.observed.append({"case": name, "request": count, "valid": valid})
            return count


class ImageHandler(BaseHTTPRequestHandler):
    """处理 loopback OpenRouter Images mock 请求且不记录默认访问日志。"""

    server: "ImageServer"

    def log_message(self, format: str, *args: object) -> None:
        """功能：禁用可能记录 path/header 的 BaseHTTPRequestHandler 日志。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        del format, args

    def do_POST(self) -> None:
        """功能：验证一个图像请求并按案例返回有界 JSON/故障响应。

        输入：仅接受 `/case/<name>/chat/completions`。
        输出：确定性分片响应、HTTP retry、断流或 idle。
        不变量：认证值仅 constant-time 比较后丢弃；观察中没有 secret/body/base64。
        失败：非法请求返回 400 且不会回显输入。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        parts = self.path.split("/")
        name = parts[2] if len(parts) == 5 and parts[1] == "case" else ""
        try:
            length = int(self.headers.get("Content-Length", "-1"))
        except ValueError:
            length = -1
        body = self.rfile.read(length) if 0 <= length <= 67_108_864 else b""
        valid = self._request_valid(name, body)
        count = self.server.state.record(name, valid) if name else 0
        if not valid:
            self.send_error(400)
            return
        response = self.server.state.case(name)["response"]
        kind = response["kind"]
        if kind == "http_retry" and count == 1:
            self.send_response(int(response["status"]))
            if "retryAfter" in response:
                self.send_header("Retry-After", str(response["retryAfter"]))
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        if kind == "disconnect":
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", "128")
            self.end_headers()
            self.wfile.write(b"{")
            self.wfile.flush()
            self.connection.shutdown(1)
            self.connection.close()
            return
        if kind in ("idle", "cancel"):
            time.sleep(int(response["idleMs"]) / 1000)
            return
        payload = self._payload(response if kind != "http_retry" else {"kind": "success", "mediaType": response["thenMediaType"]})
        wire = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(wire)))
        self.end_headers()
        sizes = response.get("chunkSizes", [3])
        offset = 0
        index = 0
        while offset < len(wire):
            size = int(sizes[index % len(sizes)])
            self.wfile.write(wire[offset : offset + size])
            self.wfile.flush()
            offset += size
            index += 1

    def _request_valid(self, name: str, body: bytes) -> bool:
        """功能：验证认证、path 与 OpenRouter 非流式 body 的精确公共形状。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        try:
            value = json.loads(body.decode("utf-8"))
        except (UnicodeError, json.JSONDecodeError):
            return False
        content = value.get("messages", [{}])[0].get("content") if isinstance(value, dict) else None
        return (
            bool(name)
            and self.path == f"/case/{name}/chat/completions"
            and self.headers.get("Authorization") == "Bearer " + self.server.state.canary
            and self.headers.get("Content-Type", "").lower().startswith("application/json")
            and value.get("model") == "google/gemini-2.5-flash-image"
            and value.get("stream") is False
            and value.get("modalities") == ["image", "text"]
            and content == [{"type": "text", "text": "生成一张确定性本地图像。"}]
        )

    def _payload(self, response: JSONObject) -> JSONObject:
        """功能：按案例构造 OpenRouter image response，不从请求复制任何值。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        kind = response["kind"]
        if kind == "remote_url":
            url = "https://invalid.example/image.png"
        elif kind == "invalid_base64":
            url = "data:image/png;base64,!!!!"
        elif kind == "mime_mismatch":
            url = "data:image/jpeg;base64," + base64.b64encode(PNG).decode("ascii")
        elif kind == "svg":
            url = "data:image/svg+xml;base64," + base64.b64encode(b"<svg/>").decode("ascii")
        else:
            media = str(response["mediaType"])
            url = f"data:{media};base64," + base64.b64encode(MEDIA_BYTES[media]).decode("ascii")
        message: JSONObject = {"images": [{"image_url": {"url": url}}]}
        if isinstance(response.get("text"), str):
            message["content"] = response["text"]
        result: JSONObject = {"id": "image-response-1", "choices": [{"message": message}]}
        if isinstance(response.get("usage"), dict):
            result["usage"] = response["usage"]
        return result


class ImageServer(ThreadingHTTPServer):
    """绑定 literal loopback 并携带 MockState 的线程 HTTP server。"""

    def __init__(self, state: MockState) -> None:
        """功能：在 127.0.0.1 系统分配端口初始化 mock server。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.state = state
        super().__init__(("127.0.0.1", 0), ImageHandler)


def await_response(process: runner.DaemonProcess, request_id: str, frames: list[JSONObject], timeout: float) -> JSONObject:
    """功能：在 timeout 内收集目标响应，允许此前已有 run events。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            frame = process.next_frame(max(0.01, deadline - time.monotonic()))
        except queue.Empty as exc:
            raise ImageRunnerError("daemon response timed out") from exc
        frames.append(frame)
        if frame.get("id") != request_id:
            if frame.get("method") == "event":
                continue
            raise ImageRunnerError("daemon returned an unexpected response")
        if "error" in frame:
            raise ImageRunnerError("daemon returned a JSON-RPC error")
        return frame
    raise ImageRunnerError("daemon response timed out")


def await_terminal(process: runner.DaemonProcess, run_id: str, frames: list[JSONObject], timeout: float) -> str:
    """功能：收集指定 run 直到唯一 terminal event 并返回其类型。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            frame = process.next_frame(max(0.01, deadline - time.monotonic()))
        except queue.Empty as exc:
            raise ImageRunnerError("image run terminal timed out") from exc
        frames.append(frame)
        params = frame.get("params")
        if isinstance(params, dict) and params.get("runId") == run_id and params.get("type") in TERMINAL_TYPES:
            return str(params["type"])
    raise ImageRunnerError("image run terminal timed out")


def journal_records(session_root: Path) -> tuple[list[JSONObject], Path]:
    """功能：从临时状态根定位唯一 Session journal 并严格加载 records。

    输出：不含 header 的 records 与 Session 目录。
    不变量：只读取 runner 自建临时根；拒绝多个或缺失 journal。
    作者：高宏顺
    邮箱：18272669457@163.com
    """

    paths = list(session_root.rglob("journal.jsonl"))
    if len(paths) != 1:
        raise ImageRunnerError("image probe did not create exactly one journal")
    values = runner.load_ndjson(paths[0])
    return values[1:], paths[0].parent


def verify_case(case: JSONObject, frames: list[JSONObject], session_root: Path, canary: str, request_count: int) -> None:
    """功能：验证终态、无 delta、artifact-first、hash/bytes 与全树不泄漏。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    expected = case["expected"]
    events = [frame["params"] for frame in frames if frame.get("method") == "event" and isinstance(frame.get("params"), dict)]
    terminals = [event for event in events if event.get("type") in TERMINAL_TYPES]
    if len(terminals) != 1 or terminals[0]["type"] != "run." + expected["terminal"]:
        raise ImageRunnerError("image terminal did not match fixture")
    if any(event.get("type") == "message.delta" for event in events):
        raise ImageRunnerError("non-streaming image run emitted message.delta")
    if request_count < int(expected.get("minRequests", 1)):
        raise ImageRunnerError("image retry request count is too small")
    records, session_dir = journal_records(session_root)
    artifacts = [record for record in records if record.get("kind") == "artifact.created"]
    if len(artifacts) != expected["artifactCount"]:
        raise ImageRunnerError("image artifact count did not match fixture")
    if artifacts:
        assistant_indexes = [i for i, record in enumerate(records) if record.get("kind") == "message.appended" and isinstance(record.get("data"), dict) and isinstance(record["data"].get("message"), dict) and record["data"]["message"].get("role") == "assistant"]
        completed_indexes = [i for i, record in enumerate(records) if record.get("kind") == "event.emitted" and isinstance(record.get("data"), dict) and isinstance(record["data"].get("event"), dict) and record["data"]["event"].get("type") == "message.completed"]
        if len(assistant_indexes) != 1 or len(completed_indexes) != 1 or max(records.index(item) for item in artifacts) >= assistant_indexes[0] or assistant_indexes[0] >= completed_indexes[0]:
            raise ImageRunnerError("artifact-first durable ordering is invalid")
        content = records[assistant_indexes[0]]["data"]["message"]["content"]
        refs = [block["artifact"] for block in content if isinstance(block, dict) and block.get("type") == "image_ref"]
        if len(refs) != len(artifacts):
            raise ImageRunnerError("assistant image_ref count is invalid")
        import hashlib
        for reference in refs:
            target = session_dir / "artifacts" / reference["artifactId"]
            data = target.read_bytes()
            if len(data) != reference["byteLength"] or hashlib.sha256(data).hexdigest() != reference["sha256"]:
                raise ImageRunnerError("artifact bytes do not match reference")
    forbidden = (canary.encode(), b"data:image", base64.b64encode(PNG), base64.b64encode(JPEG), base64.b64encode(WEBP), base64.b64encode(GIF))
    frame_bytes = json.dumps(frames, ensure_ascii=False).encode()
    for token in forbidden:
        if token in frame_bytes:
            raise ImageRunnerError("protocol leaked image or credential material")
    for path in session_root.rglob("*"):
        if path.is_file() and path.parent.name != "artifacts":
            data = path.read_bytes()
            if any(token in data for token in forbidden):
                raise ImageRunnerError("Session metadata leaked image or credential material")


def run_case(command: Sequence[str], fixture: JSONObject, case: JSONObject, timeout: float) -> None:
    """功能：在清洁环境运行一个真实 daemon 图像黑盒案例。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    canary = "image-canary-" + secrets.token_urlsafe(32)
    state = MockState(fixture, canary)
    server = ImageServer(state)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    process: runner.DaemonProcess | None = None
    try:
        with tempfile.TemporaryDirectory(prefix="qxnm-forge-image-") as temporary:
            session_root = Path(temporary)
            name = str(case["name"])
            endpoint = f"http://127.0.0.1:{server.server_address[1]}/case/{name}"
            environment = {
                "QXNM_FORGE_PROVIDER_CONFORMANCE": "1",
                "QXNM_FORGE_CONFORMANCE": "1",
                "QXNM_FORGE_OPENROUTER_IMAGES_ENDPOINT": endpoint,
                "OPENROUTER_API_KEY": canary,
                "QXNM_FORGE_SESSION_ROOT": str(session_root),
                "QXNM_FORGE_PROVIDER_CONNECT_TIMEOUT_MS": "150",
                "QXNM_FORGE_PROVIDER_IDLE_TIMEOUT_MS": "150",
                "QXNM_FORGE_PROVIDER_TOTAL_TIMEOUT_MS": "1500",
                "QXNM_FORGE_PROVIDER_MAX_ATTEMPTS": "2",
                "QXNM_FORGE_PROVIDER_RETRY_MAX_DELAY_MS": "50",
                "HTTP_PROXY": "http://127.0.0.1:9",
                "HTTPS_PROXY": "http://127.0.0.1:9",
                "ALL_PROXY": "http://127.0.0.1:9",
                "NO_PROXY": "127.0.0.1",
                "http_proxy": "http://127.0.0.1:9",
                "https_proxy": "http://127.0.0.1:9",
                "all_proxy": "http://127.0.0.1:9",
                "no_proxy": "127.0.0.1",
            }
            process = runner.DaemonProcess(
                command,
                timeout=timeout,
                max_frame_bytes=1_048_576,
                extra_env=environment,
                removed_env=(*CREDENTIAL_ENV, *ENDPOINT_ENV, *PROXY_ENV),
            )
            frames: list[JSONObject] = []
            process.send_request({"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersions":["0.1"],"client":{"name":"image-runner","version":"0.1.0"},"capabilities":{}}})
            init = await_response(process, "init", frames, timeout)
            providers = init.get("result", {}).get("capabilities", {}).get("providers", [])
            if not any(isinstance(item, dict) and item.get("id") == "openrouter" for item in providers):
                raise ImageRunnerError("daemon did not advertise configured openrouter")
            process.send_request({"jsonrpc":"2.0","id":"start","method":"run/start","params":{"sessionId":"image-session","input":{"role":"user","content":[{"type":"text","text":"生成一张确定性本地图像。"}]},"provider":{"id":"openrouter","modelId":"google/gemini-2.5-flash-image","apiFamily":"openrouter-images"}}})
            start = await_response(process, "start", frames, timeout)
            run_id = start.get("result", {}).get("runId")
            if not isinstance(run_id, str):
                raise ImageRunnerError("image run/start returned no runId")
            if case["name"] == "cancellation":
                deadline = time.monotonic() + timeout
                while state.counts.get(str(case["name"]), 0) < 1 and time.monotonic() < deadline:
                    time.sleep(0.01)
                process.send_request({"jsonrpc":"2.0","id":"cancel","method":"run/cancel","params":{"sessionId":"image-session","runId":run_id}})
                await_response(process, "cancel", frames, timeout)
            await_terminal(process, run_id, frames, timeout)
            stderr = process.stderr_text(limit=131_072).encode()
            if canary.encode() in stderr or b"data:image" in stderr:
                raise ImageRunnerError("stderr leaked image or credential material")
            verify_case(case, frames, session_root, canary, state.counts.get(str(case["name"]), 0))
    finally:
        if process is not None:
            process.close()
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)


def build_parser() -> argparse.ArgumentParser:
    """功能：构建图像静态门禁和可选 daemon 黑盒 CLI。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    parser = argparse.ArgumentParser(description="OpenRouter Images artifact-first conformance")
    parser.add_argument("--fixture", type=Path, default=DEFAULT_FIXTURE)
    parser.add_argument("--schema-root", type=Path)
    parser.add_argument("--golden-requests", type=Path, default=DEFAULT_GOLDEN_REQUESTS)
    parser.add_argument("--golden-trace", type=Path, default=DEFAULT_GOLDEN_TRACE)
    parser.add_argument("--daemon-command-json", type=parse_command_json)
    parser.add_argument("--timeout", type=float, default=8.0)
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    """功能：运行十三个图像案例并输出稳定 PASS/FAIL 摘要。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    args = build_parser().parse_args(argv)
    try:
        fixture = strict_load(args.fixture)
        if args.schema_root is not None:
            validate_schema(fixture, args.schema_root)
        _, cases = validate_semantics(fixture)
        validate_golden(args.golden_requests, args.golden_trace)
        if args.daemon_command_json is None:
            print(f"PASS: OpenRouter Images static {len(cases)}/{len(cases)}")
            return 0
        for case in cases:
            try:
                run_case(args.daemon_command_json, fixture, case, args.timeout)
            except (ImageRunnerError, runner.ConformanceError) as exc:
                raise ImageRunnerError(f"{case.get('name', '<unknown>')}: {exc}") from exc
        print(f"PASS: OpenRouter Images daemon {len(cases)}/{len(cases)}")
        return 0
    except (ImageRunnerError, runner.ConformanceError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
