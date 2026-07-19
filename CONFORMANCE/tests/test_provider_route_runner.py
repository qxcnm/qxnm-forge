from __future__ import annotations

from copy import deepcopy
import io
from contextlib import redirect_stderr, redirect_stdout
import os
from pathlib import Path
import socket
import sys
import tempfile
import time
import unittest
from unittest.mock import patch


CONFORMANCE = Path(__file__).resolve().parents[1]
ROOT = CONFORMANCE.parent
SCHEMAS = ROOT / "SPEC/schemas"
sys.path.insert(0, str(CONFORMANCE))

import provider_route_runner as route_runner  # noqa: E402


class ProviderRouteRunnerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        """功能：一次严格加载 ADR 0018 fixture、manifest 与 catalog。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        cls.fixture = route_runner.load_json(route_runner.DEFAULT_FIXTURE)
        cls.manifest = route_runner.load_json(route_runner.DEFAULT_MANIFEST)
        cls.catalog = route_runner.load_json(route_runner.DEFAULT_CATALOG)

    def test_static_suite_freezes_six_routes_and_133_models(self) -> None:
        """功能：验证完整静态门禁冻结六 route、133 模型和九个负例。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        fixture_index, _, catalog_index = route_runner.validate_static_suite(
            self.fixture,
            self.manifest,
            self.catalog,
            schema_root=SCHEMAS,
        )
        self.assertEqual(tuple(sorted(fixture_index)), route_runner.SPINE_KEYS)
        self.assertEqual(
            sum(len(catalog_index[key]) for key in route_runner.SPINE_KEYS),
            133,
        )

    def test_groq_golden_freezes_route_identity_descriptor_and_terminal_trace(
        self,
    ) -> None:
        """功能：验证 Groq dedicated golden 与 route fixture、协议生命周期保持一致。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        route = next(
            item for item in self.fixture["routes"] if item["providerId"] == "groq"
        )
        route_runner.validate_groq_golden(route, schema_root=SCHEMAS)

    def test_production_projection_freezes_seven_providers_and_134_model_ids(
        self,
    ) -> None:
        """功能：验证生产投影为六 route 的 133 descriptors 加唯一 faux 模型。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        fixture_index, _, catalog_index = route_runner.validate_static_suite(
            self.fixture,
            self.manifest,
            self.catalog,
            schema_root=None,
        )
        models = route_runner.expected_production_models(fixture_index, catalog_index)
        requests = route_runner.production_models_requests(
            sorted({key[0] for key in route_runner.SPINE_KEYS})
        )
        providers = route_runner.identity_runner.expected_provider_capabilities(models)
        frames = [
            {
                "id": "route-initialize",
                "result": {"capabilities": {"providers": providers}},
            }
        ]
        for index, provider_id in enumerate(
            sorted({str(model["providerId"]) for model in models}), start=1
        ):
            frames.append(
                {
                    "id": f"production-models-{index}",
                    "result": {
                        "models": [
                            model
                            for model in models
                            if model["providerId"] == provider_id
                        ]
                    },
                }
            )
        route_runner.assert_production_advertisement(requests, frames, models)
        self.assertEqual(len(models), 133)
        self.assertEqual(len(providers), 7)
        self.assertEqual(sum(len(item["models"]) for item in providers), 134)

    def test_production_environment_removes_both_presence_and_conformance(self) -> None:
        """功能：验证无配置生产环境不会继承 route/identity presence 或真实 credential。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        inherited = {
            route_runner.CONFIG_ENV: "/private/route.json",
            route_runner.identity_runner.CONFIG_ENV: "/private/identity.json",
            "QXNM_FORGE_CONFORMANCE": "1",
            "QXNM_FORGE_PROVIDER_CONFORMANCE": "1",
            "GROQ_API_KEY": "real-secret",
        }
        with (
            tempfile.TemporaryDirectory() as temporary,
            patch.dict(os.environ, inherited, clear=False),
        ):
            environment = route_runner.sanitized_environment(
                self.manifest,
                Path(temporary),
                None,
                credential_name=None,
                credential_value="unused",
                conformance=False,
                proxy_url="http://127.0.0.1:43123",
            )
        for name in (
            route_runner.CONFIG_ENV,
            route_runner.identity_runner.CONFIG_ENV,
            "QXNM_FORGE_CONFORMANCE",
            "QXNM_FORGE_PROVIDER_CONFORMANCE",
            "GROQ_API_KEY",
        ):
            self.assertNotIn(name, environment)
        self.assertEqual(environment["HTTPS_PROXY"], "http://127.0.0.1:43123")

    def test_network_tripwire_starts_at_zero_and_counts_connections(self) -> None:
        """功能：验证 production gate 的 loopback tripwire 能证明零网络并捕获连接。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with route_runner.NetworkTripwire() as tripwire:
            self.assertEqual(tripwire.server.connection_count(), 0)
            parsed = route_runner.urlsplit(tripwire.origin)
            with socket.create_connection((str(parsed.hostname), int(parsed.port))):
                pass
            for _ in range(100):
                if tripwire.server.connection_count() == 1:
                    break
                time.sleep(0.001)
            self.assertEqual(tripwire.server.connection_count(), 1)

    def test_static_gate_rejects_auth_quirk_endpoint_descriptor_and_count_drift(
        self,
    ) -> None:
        """功能：验证五类独立 route spine 漂移均 fail closed。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        mutations = []
        manifest_quirk = deepcopy(self.manifest)
        groq = next(
            item for item in manifest_quirk["providers"] if item["id"] == "groq"
        )
        groq["routes"][0]["quirks"] = ["synthetic"]
        mutations.append((self.fixture, manifest_quirk, self.catalog))

        manifest_auth = deepcopy(self.manifest)
        groq = next(item for item in manifest_auth["providers"] if item["id"] == "groq")
        groq["authProfiles"][0]["kind"] = "oauth"
        mutations.append((self.fixture, manifest_auth, self.catalog))

        catalog_endpoint = deepcopy(self.catalog)
        model = next(
            item
            for item in catalog_endpoint["models"]
            if item["providerId"] == "groq"
            and item["apiFamily"] == "openai-completions"
        )
        model["endpoint"]["baseUrl"] = "https://api.invalid"
        mutations.append((self.fixture, self.manifest, catalog_endpoint))

        fixture_descriptor = deepcopy(self.fixture)
        fixture_descriptor["routes"][0]["expectedDescriptor"]["displayName"] = "drift"
        mutations.append((fixture_descriptor, self.manifest, self.catalog))

        fixture_count = deepcopy(self.fixture)
        fixture_count["routes"][0]["productionModelCount"] += 1
        mutations.append((fixture_count, self.manifest, self.catalog))

        for index, (fixture, manifest, catalog) in enumerate(mutations):
            with self.subTest(index=index):
                with self.assertRaises(route_runner.ProviderRouteRunnerError):
                    route_runner.validate_static_suite(
                        fixture,
                        manifest,
                        catalog,
                        schema_root=None,
                    )

    def test_route_config_schema_accepts_literal_loopback_and_rejects_expansion(
        self,
    ) -> None:
        """功能：验证 strict config 只承载单 route/model 与 literal loopback base。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        route = self.fixture["routes"][0]
        valid = route_runner.route_config(
            route, "http://127.0.0.1:43123/provider-route"
        )
        route_runner.validate_local_schema(
            valid, "provider-route-config.schema.json", SCHEMAS
        )
        for invalid in (
            {**valid, "endpointBase": "http://localhost:43123/provider-route"},
            {**valid, "credential": "forbidden"},
        ):
            with self.assertRaises(route_runner.ProviderRouteRunnerError):
                route_runner.validate_local_schema(
                    invalid, "provider-route-config.schema.json", SCHEMAS
                )

    def test_projected_mock_reuses_family_parser_with_canonical_model_and_target(
        self,
    ) -> None:
        """功能：验证 text mock 投影只替换 canonical model/target 身份。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        route = next(
            item for item in self.fixture["routes"] if item["providerId"] == "groq"
        )
        _, family, case = route_runner.projected_text_fixture(route)
        self.assertEqual(family["request"]["equals"]["/model"], route["modelId"])
        self.assertEqual(family["requestTarget"], route["requestTarget"])
        self.assertEqual(case["name"], "text_success")

    def test_run_start_always_carries_explicit_api_family(self) -> None:
        """功能：验证正例与 family 负例均通过三字段 provider selection 路由。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        route = self.fixture["routes"][0]
        request = route_runner.run_start_request(route)
        self.assertEqual(
            request["params"]["provider"],
            {
                "id": route["providerId"],
                "modelId": route["modelId"],
                "apiFamily": route["apiFamily"],
            },
        )

    def test_fixture_freezes_all_required_negative_kinds(self) -> None:
        """功能：验证九个安全负例无缺失、重复或重排。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        kinds = tuple(item["kind"] for item in self.fixture["negativeCases"])
        self.assertEqual(kinds, route_runner.NEGATIVE_KINDS)

    def test_static_cli_passes_without_daemon_or_network(self) -> None:
        """功能：验证默认 CLI 仅运行静态 gate 并输出稳定 census。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        stdout = io.StringIO()
        stderr = io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            code = route_runner.main(["--schema-root", str(SCHEMAS)])
        self.assertEqual(code, 0, stderr.getvalue())
        self.assertIn("6 routes / 133 models / 9 negatives", stdout.getvalue())


if __name__ == "__main__":
    unittest.main()
