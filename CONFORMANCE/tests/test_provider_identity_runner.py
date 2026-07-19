from __future__ import annotations

import ast
import io
import json
import os
import secrets
import sys
import tempfile
import time
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest.mock import patch

CONFORMANCE = Path(__file__).resolve().parents[1]
ROOT = CONFORMANCE.parent
SCHEMAS = ROOT / "SPEC/schemas"
FAKE_DAEMON = CONFORMANCE / "tests/fake_provider_identity_daemon.py"
FIXTURE_DIR = CONFORMANCE / "fixtures/provider-identity"

sys.path.insert(0, str(CONFORMANCE))
import provider_identity_runner as identity_runner  # noqa: E402


class ProviderIdentityRunnerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        """功能：一次加载严格 fixture/manifest/catalog 并建立共享索引。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        cls.fixture = identity_runner.load_json(identity_runner.DEFAULT_FIXTURE)
        cls.manifest = identity_runner.load_json(identity_runner.DEFAULT_MANIFEST)
        cls.catalog = identity_runner.load_json(identity_runner.DEFAULT_CATALOG)
        cls.route_index, cls.provider_index, cls.catalog_index = (
            identity_runner.validate_static_suite(
                cls.fixture,
                cls.manifest,
                cls.catalog,
                schema_root=SCHEMAS,
            )
        )

    def case(self, name: str) -> identity_runner.JSONObject:
        """功能：按稳定名称取得一个 Provider 身份 fixture 案例。

        输入：案例 name。
        输出：匹配的案例 object。
        失败：名称缺失时让 next 抛 StopIteration，从而使测试失败。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        return next(item for item in self.fixture["cases"] if item["name"] == name)

    def test_static_suite_covers_full_manifest_and_ambiguity(self) -> None:
        """功能：验证静态门禁覆盖 35 身份、45 路由、1,076 模型和两个歧义对。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        self.assertEqual(len(self.provider_index), 35)
        self.assertEqual(len(self.route_index), 45)
        self.assertEqual(sum(len(items) for items in self.catalog_index.values()), 1076)
        config = identity_runner.build_presence_config(
            self.case("openrouter_dual_family"), self.route_index
        )
        routes = identity_runner.usable_route_keys(self.route_index, self.catalog_index, config)
        models = identity_runner.expected_models(routes, self.catalog_index, config)
        pairs = [
            (model["providerId"], model["modelId"])
            for model in models
            if model["modelId"] in {"google/gemini-3-pro-image", "openrouter/auto"}
        ]
        self.assertEqual(len(pairs), 4)
        self.assertEqual(len({model["modelId"] for model in models}), 300)

    def test_presence_config_contains_no_values_and_projects_capabilities(self) -> None:
        """功能：验证临时配置仅表达 presence，并把未获准能力降为 false。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        case = self.case("capability_projection")
        config = identity_runner.build_presence_config(case, self.route_index)
        self.assertEqual(
            set(config),
            {
                "schemaVersion",
                "implementedAdapterIds",
                "capabilityAllowances",
                "usableAuthProfiles",
                "configuredEnvironmentNames",
            },
        )
        allowance = config["capabilityAllowances"][0]
        self.assertEqual(allowance["features"], ["authentication", "text"])
        routes = identity_runner.usable_route_keys(self.route_index, self.catalog_index, config)
        models = identity_runner.expected_models(routes, self.catalog_index, config)
        self.assertEqual(len(models), 2)
        for model in models:
            self.assertFalse(model["capabilities"]["streaming"])
            self.assertFalse(model["capabilities"]["tools"])
            self.assertFalse(model["capabilities"]["reasoning"])

    def test_duplicate_json_keys_fail_closed(self) -> None:
        """功能：验证 runner 自身在任意层级拒绝重复 JSON 键。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with self.assertRaises(identity_runner.ProviderIdentityRunnerError):
            identity_runner.parse_json_bytes(b'{"outer":{"key":1,"key":2}}', "test")

    def test_child_environment_removes_unrelated_secret_shaped_values(self) -> None:
        """功能：验证 runner 不把 manifest 外的继承 secret 环境传给 daemon。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        config = identity_runner.build_presence_config(
            self.case("default_closed"), self.route_index
        )
        with tempfile.TemporaryDirectory(prefix="provider-identity-env-") as temporary:
            temp_root = Path(temporary)
            inherited = {
                "UNRELATED_API_TOKEN": "must-not-cross",
                "HTTP_PROXY": "http://real-proxy.invalid:8080",
                "NO_PROXY": "*",
            }
            with patch.dict(os.environ, inherited):
                environment = identity_runner.child_environment(
                    self.manifest,
                    config,
                    None,
                    temp_root,
                    "synthetic-canary",
                )
        self.assertNotIn("UNRELATED_API_TOKEN", environment)
        self.assertEqual(environment[identity_runner.CANARY_ENV], "synthetic-canary")
        for name in (
            "HTTP_PROXY",
            "HTTPS_PROXY",
            "ALL_PROXY",
            "http_proxy",
            "https_proxy",
            "all_proxy",
        ):
            self.assertEqual(environment[name], identity_runner.DEAD_PROXY_URL)
        self.assertEqual(environment["NO_PROXY"], identity_runner.LOOPBACK_NO_PROXY)
        self.assertEqual(environment["no_proxy"], identity_runner.LOOPBACK_NO_PROXY)

    def test_daemon_command_argv_count_and_item_length_are_bounded(self) -> None:
        """功能：验证 daemon JSON argv 具有与共同 Provider runner 同级的硬边界。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        valid = ["x"] * identity_runner.MAX_ARGV_ITEMS
        self.assertEqual(identity_runner.parse_daemon_command(json.dumps(valid)), valid)
        invalid_values = [
            ["x"] * (identity_runner.MAX_ARGV_ITEMS + 1),
            ["x" * (identity_runner.MAX_ARG_CHARS + 1)],
        ]
        for value in invalid_values:
            with self.subTest(size=len(value)):
                with self.assertRaises(identity_runner.ProviderIdentityRunnerError):
                    identity_runner.parse_daemon_command(json.dumps(value))

    def test_incremental_output_limit_terminates_flooding_daemon(self) -> None:
        """功能：验证持续 reader 在小型测试上限一越界就终止洪泛 daemon。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        config = identity_runner.build_presence_config(
            self.case("default_closed"), self.route_index
        )
        self.assertEqual(identity_runner.MAX_PROCESS_OUTPUT_BYTES, 16 * 1024 * 1024)
        self.assertEqual(identity_runner.MAX_PROCESS_STDERR_BYTES, 1024 * 1024)
        with tempfile.TemporaryDirectory(prefix="provider-identity-output-") as temporary:
            temp_root = Path(temporary)
            environment = identity_runner.child_environment(
                self.manifest, config, None, temp_root, "synthetic-canary"
            )
            started = time.monotonic()
            with self.assertRaisesRegex(
                identity_runner.ProviderIdentityRunnerError,
                "output exceeded limit",
            ):
                identity_runner.run_process(
                    [
                        sys.executable,
                        str(FAKE_DAEMON),
                        "--flood-output-bytes",
                        str(512 * 1024),
                    ],
                    [identity_runner.encode_request(identity_runner.initialize_request())],
                    environment,
                    5.0,
                    max_output_bytes=64 * 1024,
                    max_stderr_bytes=32 * 1024,
                )
        self.assertLess(time.monotonic() - started, 3.0)

    @unittest.skipUnless(os.name == "posix", "POSIX process-group cleanup required")
    def test_timeout_kills_descendant_before_delayed_marker(self) -> None:
        """功能：验证 timeout 通过独立进程组清理 daemon 后代且不会延迟写 marker。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        config = identity_runner.build_presence_config(
            self.case("default_closed"), self.route_index
        )
        with tempfile.TemporaryDirectory(prefix="provider-identity-timeout-") as temporary:
            temp_root = Path(temporary)
            marker = temp_root / "descendant-marker"
            environment = identity_runner.child_environment(
                self.manifest, config, None, temp_root, "synthetic-canary"
            )
            with self.assertRaisesRegex(
                identity_runner.ProviderIdentityRunnerError,
                "exceeded deadline",
            ):
                identity_runner.run_process(
                    [
                        sys.executable,
                        str(FAKE_DAEMON),
                        "--spawn-descendant-marker",
                        str(marker),
                        "--hang",
                    ],
                    [identity_runner.encode_request(identity_runner.initialize_request())],
                    environment,
                    0.2,
                )
            time.sleep(1.0)
            self.assertFalse(marker.exists())

    def test_total_deadline_applies_when_daemon_does_not_read_large_stdin(self) -> None:
        """功能：验证候选 daemon 拒读大 stdin 时 writer 线程不阻塞总 deadline。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        config = identity_runner.build_presence_config(
            self.case("default_closed"), self.route_index
        )
        with tempfile.TemporaryDirectory(prefix="provider-identity-stdin-") as temporary:
            temp_root = Path(temporary)
            environment = identity_runner.child_environment(
                self.manifest, config, None, temp_root, "synthetic-canary"
            )
            started = time.monotonic()
            with self.assertRaisesRegex(
                identity_runner.ProviderIdentityRunnerError,
                "exceeded deadline",
            ):
                identity_runner.run_process(
                    [sys.executable, str(FAKE_DAEMON), "--hang"],
                    [b"{" + b"x" * (512 * 1024)],
                    environment,
                    0.2,
                )
        self.assertLess(time.monotonic() - started, 3.0)

    def test_full_fake_daemon_black_box_suite_passes(self) -> None:
        """功能：运行 14 个正例与四个 strict 负例的完整离线黑盒套件。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        command = f'["{sys.executable}","{FAKE_DAEMON}"]'
        stdout = io.StringIO()
        stderr = io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            code = identity_runner.main(
                [
                    "--schema-root",
                    str(SCHEMAS),
                    "--daemon-command-json",
                    command,
                ]
            )
        self.assertEqual(code, 0, stderr.getvalue())
        self.assertIn("35 providers / 45 routes / 1076 models", stdout.getvalue())

    def test_runner_rejects_canary_leak_and_unknown_route(self) -> None:
        """功能：证明 runner 会拒绝 credential canary 泄漏和 manifest 外广告。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        case = self.case("single_route_golden")
        base = [sys.executable, str(FAKE_DAEMON)]
        for flag in ("--leak-canary", "--advertise-unknown-route"):
            with self.subTest(flag=flag):
                with self.assertRaises(identity_runner.ProviderIdentityRunnerError):
                    identity_runner.run_positive_case(
                        [*base, flag],
                        case,
                        self.manifest,
                        self.route_index,
                        self.provider_index,
                        self.catalog_index,
                        SCHEMAS,
                        15.0,
                    )

    def test_dedicated_golden_trace_matches_fake_daemon_and_schema(self) -> None:
        """功能：验证单路由 golden trace 与 fake daemon 输出及协议 Schema 完全一致。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        raw_requests = (FIXTURE_DIR / "deepseek.requests.ndjson").read_bytes().splitlines()
        requests = [
            identity_runner.parse_json_bytes(raw, f"golden request {index}")
            for index, raw in enumerate(raw_requests, start=1)
        ]
        expected_frames = [
            identity_runner.parse_json_bytes(raw, f"golden frame {index}")
            for index, raw in enumerate(
                (FIXTURE_DIR / "deepseek.trace.ndjson").read_bytes().splitlines(),
                start=1,
            )
        ]
        config = identity_runner.build_presence_config(
            self.case("single_route_golden"), self.route_index
        )
        canary = "provider-identity-canary-" + secrets.token_hex(24)
        with tempfile.TemporaryDirectory(prefix="provider-identity-golden-") as temporary:
            temp_root = Path(temporary)
            (temp_root / "home").mkdir()
            config_path = temp_root / "presence.json"
            config_path.write_text(
                json.dumps(config, separators=(",", ":")),
                encoding="utf-8",
            )
            environment = identity_runner.child_environment(
                self.manifest, config, config_path, temp_root, canary
            )
            completed, actual_frames = identity_runner.run_process(
                [sys.executable, str(FAKE_DAEMON)],
                raw_requests,
                environment,
                15.0,
            )
        self.assertEqual(completed.returncode, 0)
        self.assertNotIn(canary.encode(), completed.stdout)
        self.assertEqual(actual_frames, expected_frames)
        identity_runner.validate_protocol_frames(requests, actual_frames, SCHEMAS)

    def test_new_python_functions_have_required_documentation(self) -> None:
        """功能：审计本门禁新增 Python 函数均含中文功能、作者和邮箱注释。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        for path in (
            CONFORMANCE / "provider_identity_runner.py",
            FAKE_DAEMON,
            Path(__file__),
        ):
            tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
            for node in ast.walk(tree):
                if not isinstance(node, ast.FunctionDef | ast.AsyncFunctionDef):
                    continue
                doc = ast.get_docstring(node) or ""
                with self.subTest(file=path.name, function=node.name):
                    self.assertIn("功能：", doc)
                    self.assertIn("作者：高宏顺", doc)
                    self.assertIn("邮箱：18272669457@163.com", doc)


if __name__ == "__main__":
    unittest.main()
