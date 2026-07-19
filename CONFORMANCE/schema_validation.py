"""qxnm-forge conformance 的可选 Draft 2020-12 Schema 验证桥。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any, Sequence


class SchemaValidationError(Exception):
    """Schema 加载、依赖或实例验证失败。"""


def _load_validator(schema_root: Path) -> Any:
    """功能：加载全部规范 Schema 并创建支持跨文件引用的根验证器。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    try:
        import jsonschema
        from referencing import Registry, Resource
    except ImportError as exc:
        raise SchemaValidationError(
            "--schema-root requires the optional jsonschema and referencing packages"
        ) from exc

    resources: list[tuple[str, Any]] = []
    schemas: dict[str, dict[str, Any]] = {}
    for path in sorted(schema_root.rglob("*.schema.json")):
        try:
            schema = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise SchemaValidationError(f"cannot load schema {path}: {exc}") from exc
        try:
            jsonschema.Draft202012Validator.check_schema(schema)
            schema_id = schema["$id"]
        except (KeyError, jsonschema.SchemaError) as exc:
            raise SchemaValidationError(
                f"invalid Draft 2020-12 schema {path}: {exc}"
            ) from exc
        schemas[str(path.relative_to(schema_root))] = schema
        resources.append((schema_id, Resource.from_contents(schema)))

    root_name = "protocol/jsonrpc.schema.json"
    if root_name not in schemas:
        raise SchemaValidationError(
            f"missing root protocol schema: {schema_root / root_name}"
        )
    registry = Registry().with_resources(resources)
    return jsonschema.Draft202012Validator(schemas[root_name], registry=registry)


def _format_error(error: Any, frame_kind: str, index: int) -> str:
    """功能：把 jsonschema 错误转换为稳定且可定位的诊断文本。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    instance_path = "/".join(str(part) for part in error.absolute_path) or "<root>"
    return (
        f"{frame_kind} {index + 1} schema violation at {instance_path}: {error.message}"
    )


def validate_protocol_trace(
    requests: Sequence[dict[str, Any]],
    frames: Sequence[dict[str, Any]],
    schema_root: Path,
) -> None:
    """功能：按规范根 Schema 验证所有请求、响应和事件通知。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    validator = _load_validator(schema_root)
    for frame_kind, messages in (("request", requests), ("server frame", frames)):
        for index, message in enumerate(messages):
            errors = sorted(
                validator.iter_errors(message),
                key=lambda error: (
                    tuple(str(part) for part in error.absolute_path),
                    error.message,
                ),
            )
            if errors:
                raise SchemaValidationError(_format_error(errors[0], frame_kind, index))
