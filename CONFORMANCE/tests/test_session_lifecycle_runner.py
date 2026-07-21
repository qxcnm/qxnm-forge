from __future__ import annotations

import ast
from contextlib import redirect_stdout
from copy import deepcopy
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest


CONFORMANCE = Path(__file__).resolve().parents[1]
ROOT = CONFORMANCE.parent
SCHEMAS = ROOT / "SPEC/schemas"
FAKE_DAEMON = CONFORMANCE / "tests/fake_session_lifecycle_daemon.py"
sys.path.insert(0, str(CONFORMANCE))

import session_lifecycle_runner as lifecycle_runner  # noqa: E402


class SessionLifecycleRunnerTests(unittest.TestCase):
    def valid_summary(self) -> dict[str, object]:
        """功能：构造供字段、分页和错误单测复用的合法摘要。

        输出：严格五字段 Session summary。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        return {
            "sessionId": "session-unit-1",
            "title": "Unit lifecycle title",
            "project": "workspace",
            "updatedAt": "2026-07-21T08:00:05Z",
            "archived": False,
        }

    def test_command_parser_and_runtime_daemon_arguments_are_closed(self) -> None:
        """功能：验证 JSON argv 不经 shell且 Rust/.NET stdio 参数保持独立形状。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        command = lifecycle_runner.parse_command('["binary","arg"]', "unit")
        self.assertEqual(["binary", "arg"], command)
        workspace = Path("/tmp/workspace")
        state = Path("/tmp/state")
        rust = lifecycle_runner.daemon_command(command, "rust", workspace, state)
        dotnet = lifecycle_runner.daemon_command(command, "dotnet", workspace, state)
        self.assertNotIn("--stdio", rust)
        self.assertIn("--stdio", dotnet)
        with self.assertRaises(lifecycle_runner.SessionLifecycleConformanceError):
            lifecycle_runner.parse_command('["ok",""]', "unit")

    def test_new_python_functions_have_required_documentation(self) -> None:
        """功能：审计本门禁全部 Python 函数含中文功能、作者与邮箱注释。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        paths = [Path(lifecycle_runner.__file__), FAKE_DAEMON, Path(__file__)]
        missing: list[str] = []
        for path in paths:
            tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
            for node in ast.walk(tree):
                if not isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
                    continue
                documentation = ast.get_docstring(node) or ""
                if not all(
                    marker in documentation
                    for marker in (
                        "功能：",
                        "作者：高宏顺",
                        "邮箱：18272669457@163.com",
                    )
                ):
                    missing.append(f"{path.name}:{node.lineno}:{node.name}")
        self.assertEqual([], missing)

    def test_summary_rejects_transcript_path_and_secret_extensions(self) -> None:
        """功能：验证摘要只允许 ADR 0030 五字段与可选 status。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.assertEqual(
            self.valid_summary(),
            lifecycle_runner.validate_summary(self.valid_summary()),
        )
        for field in ("transcript", "path", "credential", "provider", "messages"):
            invalid = deepcopy(self.valid_summary())
            invalid[field] = "private"
            with (
                self.subTest(field=field),
                self.assertRaises(lifecycle_runner.SessionLifecycleConformanceError),
            ):
                lifecycle_runner.validate_summary(invalid)

    def test_pagination_requires_three_fields_progress_and_null_terminal(self) -> None:
        """功能：验证分页三字段、非终页进度与终页显式 null。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        summary = self.valid_summary()
        first = {
            "sessions": [summary],
            "nextCursor": "v1:1",
            "hasMore": True,
        }
        terminal = {"sessions": [], "nextCursor": None, "hasMore": False}
        self.assertEqual([summary], lifecycle_runner.validate_list_result(first, 1))
        self.assertEqual([], lifecycle_runner.validate_list_result(terminal, 1))
        for invalid in (
            {"sessions": [], "nextCursor": "v1:0", "hasMore": True},
            {"sessions": [], "nextCursor": "v1:0", "hasMore": False},
            {"sessions": [], "hasMore": False},
        ):
            with (
                self.subTest(invalid=invalid),
                self.assertRaises(lifecycle_runner.SessionLifecycleConformanceError),
            ):
                lifecycle_runner.validate_list_result(invalid, 1)

    def test_missing_error_allows_only_public_resource_id(self) -> None:
        """功能：验证不存在错误允许公开 ID，但拒绝路径、journal 与 secret 文本。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        response = {
            "jsonrpc": "2.0",
            "id": "missing",
            "error": {
                "code": -32602,
                "message": "session was not found",
                "retryable": False,
                "details": {
                    "kind": "session_not_found",
                    "resourceId": "session-public-1",
                },
            },
        }
        lifecycle_runner.validate_redacted_error(
            response,
            "missing",
            "session-public-1",
            ["synthetic-private"],
        )
        for private in ("/private/state", "journal.jsonl", "synthetic-private"):
            invalid = deepcopy(response)
            invalid["error"]["message"] = private
            with (
                self.subTest(private=private),
                self.assertRaises(lifecycle_runner.SessionLifecycleConformanceError),
            ):
                lifecycle_runner.validate_redacted_error(
                    invalid,
                    "missing",
                    "session-public-1",
                    ["synthetic-private", "/private/state"],
                )

    def test_archive_document_is_closed_sorted_and_unique(self) -> None:
        """功能：验证 archive-state 两字段文档拒绝乱序、重复与扩展字段。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with tempfile.TemporaryDirectory(prefix="lifecycle-archive-unit-") as temporary:
            state = Path(temporary)
            directory = state / "session-lifecycle"
            directory.mkdir()
            path = directory / "archive-state.json"
            path.write_text(
                json.dumps(
                    {
                        "schemaVersion": "0.1",
                        "archivedSessionIds": ["session-a", "session-b"],
                    }
                ),
                encoding="utf-8",
            )
            self.assertEqual(
                ["session-a", "session-b"],
                lifecycle_runner.archive_ids(state, required=True),
            )
            path.write_text(
                json.dumps(
                    {
                        "schemaVersion": "0.1",
                        "archivedSessionIds": ["session-b", "session-a"],
                    }
                ),
                encoding="utf-8",
            )
            with self.assertRaises(lifecycle_runner.SessionLifecycleConformanceError):
                lifecycle_runner.archive_ids(state, required=True)

    def test_canary_leak_from_fake_stderr_fails_without_echoing_value(self) -> None:
        """功能：验证 stderr canary 泄漏使门禁失败且异常不复述 secret。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with tempfile.TemporaryDirectory(prefix="lifecycle-leak-unit-") as temporary:
            root = Path(temporary)
            workspace = root / "workspace"
            state = root / "state"
            environment = root / "environment"
            workspace.mkdir()
            state.mkdir()
            environment.mkdir()
            command = [
                sys.executable,
                str(FAKE_DAEMON),
                "--runtime",
                "rust",
                "--leak-canary",
            ]
            canary = "synthetic-unit-canary-value"
            with self.assertRaises(
                lifecycle_runner.SessionLifecycleConformanceError
            ) as caught:
                lifecycle_runner.execute_requests(
                    command,
                    "rust",
                    workspace,
                    state,
                    [lifecycle_runner.initialize_request("leak-initialize")],
                    SCHEMAS,
                    environment,
                    canary,
                    5.0,
                )
            self.assertNotIn(canary, str(caught.exception))

    def test_full_fake_cross_runtime_suite_passes(self) -> None:
        """功能：用两个独立 fake runtime 完成双向创建、归档、恢复、分页与删除。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        rust = json.dumps([sys.executable, str(FAKE_DAEMON), "--runtime", "rust"])
        dotnet = json.dumps([sys.executable, str(FAKE_DAEMON), "--runtime", "dotnet"])
        stdout = io.StringIO()
        with redirect_stdout(stdout):
            code = lifecycle_runner.main(
                [
                    "--rust-command-json",
                    rust,
                    "--dotnet-command-json",
                    dotnet,
                    "--schema-root",
                    str(SCHEMAS),
                    "--timeout",
                    "8",
                ]
            )
        self.assertEqual(0, code, stdout.getvalue())
        result = json.loads(stdout.getvalue())
        self.assertEqual("passed", result["status"])
        self.assertEqual(16, result["cases"])
        self.assertEqual(2, result["crossRuntimeDirections"])
        self.assertEqual(2, result["paginatedRuntimes"])
        self.assertIs(False, result["providerHttpConfigured"])
        self.assertNotIn("networkRequests", result)


if __name__ == "__main__":
    unittest.main()
