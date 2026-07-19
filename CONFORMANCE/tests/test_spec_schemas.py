from __future__ import annotations

import json
from copy import deepcopy
from pathlib import Path
import sys
import unittest


CONFORMANCE = Path(__file__).resolve().parents[1]
ROOT = CONFORMANCE.parent
SCHEMAS = ROOT / "SPEC/schemas"

try:
    import jsonschema
    from referencing import Registry, Resource
except ImportError:  # pragma: no cover - optional developer gate
    jsonschema = None


class SpecSchemaTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        """功能：加载全部 Draft 2020-12 Schema 并构建跨文件引用注册表。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if jsonschema is None:
            raise unittest.SkipTest("optional jsonschema package is unavailable")
        resources: list[tuple[str, Resource[object]]] = []
        cls.schemas: dict[str, dict[str, object]] = {}
        for path in sorted(SCHEMAS.rglob("*.schema.json")):
            schema = json.loads(path.read_text(encoding="utf-8"))
            cls.schemas[str(path.relative_to(SCHEMAS))] = schema
            resources.append((schema["$id"], Resource.from_contents(schema)))
        cls.registry = Registry().with_resources(resources)

    def validator(self, schema_name: str, ref: str) -> object:
        """功能：创建指向指定 Schema 定义且支持跨文件引用的验证器。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        schema = self.schemas[schema_name]
        target = {"$schema": schema["$schema"], "$ref": schema["$id"] + ref}
        return jsonschema.Draft202012Validator(target, registry=self.registry)

    def test_golden_requests_and_trace_match_protocol_schemas(self) -> None:
        """功能：按请求方法验证 golden 请求，并验证每个实际响应和事件。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        sys.path.insert(0, str(CONFORMANCE))
        import runner

        requests = runner.load_ndjson(
            CONFORMANCE / "fixtures/golden/hello.requests.ndjson"
        )
        trace = runner.load_ndjson(CONFORMANCE / "fixtures/golden/hello.trace.ndjson")
        request_defs = {
            "initialize": "#/$defs/initializeRequest",
            "faux/configure": "#/$defs/fauxConfigureRequest",
            "run/start": "#/$defs/runStartRequest",
        }
        for request in requests:
            with self.subTest(method=request["method"]):
                self.validator(
                    "protocol/jsonrpc.schema.json", request_defs[request["method"]]
                ).validate(request)

        trace[0]["result"]["implementation"] = {
            "name": "qxnm-forge-conformance",
            "version": "0.1.0",
            "language": "python",
        }
        self.validator(
            "protocol/jsonrpc.schema.json", "#/$defs/initializeResult"
        ).validate(trace[0]["result"])
        self.validator(
            "protocol/jsonrpc.schema.json", "#/$defs/fauxConfigureResult"
        ).validate(trace[1]["result"])
        self.validator(
            "protocol/jsonrpc.schema.json", "#/$defs/runStartResult"
        ).validate(trace[2]["result"])
        event_validator = self.validator(
            "protocol/event.schema.json", "#/$defs/notification"
        )
        for frame in trace[3:]:
            frame["params"]["time"] = "2026-07-10T08:00:00Z"
            with self.subTest(event=frame["params"]["type"]):
                event_validator.validate(frame)

        from schema_validation import validate_protocol_trace

        validate_protocol_trace(requests, trace, SCHEMAS)

    def test_faux_scenarios_match_spec_schema(self) -> None:
        """功能：验证独立 faux 场景和故障场景包装中的每个 scenario。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        validator = self.validator(
            "protocol/faux-scenario.schema.json", "#/$defs/scenario"
        )
        fixture_dir = CONFORMANCE / "fixtures/faux"
        for name in ("hello-v1.json", "tool-call-v1.json"):
            scenario = json.loads((fixture_dir / name).read_text(encoding="utf-8"))
            with self.subTest(fixture=name):
                validator.validate(scenario)
        failures = json.loads(
            (fixture_dir / "failures-v1.json").read_text(encoding="utf-8")
        )
        for case in failures["scenarios"]:
            with self.subTest(
                fixture="failures-v1.json", name=case["scenario"]["name"]
            ):
                validator.validate(case["scenario"])

        continuation = json.loads(
            (
                CONFORMANCE
                / "fixtures/session/portable-v0.1/continuation.scenario.json"
            ).read_text(encoding="utf-8")
        )
        validator.validate(continuation)

    def test_provider_mock_cases_match_spec_schema(self) -> None:
        """功能：验证首批与第二批各三类、各 21 个本机 mock 案例夹具。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        validator = self.validator("provider-mock-cases.schema.json", "")
        for name in ("mock-cases.json", "mock-cases-batch2.json"):
            fixture = json.loads(
                (CONFORMANCE / "fixtures/provider" / name).read_text(encoding="utf-8")
            )
            with self.subTest(fixture=name):
                validator.validate(fixture)

    def test_openrouter_images_fixture_and_portable_content_match_schema(self) -> None:
        """功能：验证图像 fixture、显式 family 路由和 assistant image_ref 公共 Schema。

        输入：仓库内 OpenRouter Images fixture 与构造的 portable DTO。
        输出：全部满足 Draft 2020-12 封闭对象约束时测试通过。
        不变量：测试 DTO 只含 artifact reference，不内联 base64、URL 或 host path。
        失败：fixture、apiFamily 或 assistantContent 漂移时由验证器抛 ValidationError。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        fixture = json.loads(
            (CONFORMANCE / "fixtures/provider/mock-cases-images.json").read_text(
                encoding="utf-8"
            )
        )
        self.validator("provider-image-mock-cases.schema.json", "").validate(fixture)
        selection = {
            "id": "openrouter",
            "modelId": "google/gemini-2.5-flash-image",
            "apiFamily": "openrouter-images",
        }
        self.validator(
            "domain/model.schema.json", "#/$defs/providerSelection"
        ).validate(selection)
        image_reference = {
            "type": "image_ref",
            "artifact": {
                "artifactId": "artifact-image-1",
                "mediaType": "image/png",
                "byteLength": 28,
                "sha256": "0" * 64,
            },
        }
        self.validator(
            "domain/content.schema.json", "#/$defs/assistantContent"
        ).validate(image_reference)

    def test_provider_route_spine_fixture_and_config_match_schema(self) -> None:
        """功能：验证 ADR 0018 六 route fixture 与单 route loopback 配置 Schema。

        输入：仓库 route fixture 与合成无凭据配置。输出：两个文档均通过验证。
        不变量：配置不含 credential、adapter、header 或 quirk。
        失败：fixture/count/descriptor 或 literal-loopback 约束漂移时测试失败。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        fixture = json.loads(
            (CONFORMANCE / "fixtures/provider-route/route-spine-cases.json").read_text(
                encoding="utf-8"
            )
        )
        self.validator("provider-route-cases.schema.json", "").validate(fixture)
        config = {
            "schemaVersion": "0.1",
            "providerId": "groq",
            "apiFamily": "openai-completions",
            "modelId": "llama-3.1-8b-instant",
            "endpointBase": "http://127.0.0.1:43123/provider-route",
        }
        self.validator("provider-route-config.schema.json", "").validate(config)
        config["endpointBase"] = "http://localhost:43123/provider-route"
        with self.assertRaises(jsonschema.ValidationError):
            self.validator("provider-route-config.schema.json", "").validate(config)

    def test_sponsored_provider_catalog_fixture_and_security_negatives(self) -> None:
        """功能：验证空推广目录 Schema，并拒绝未披露字段、非受限 family 与未知属性。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        fixture = json.loads(
            (
                CONFORMANCE / "fixtures/sponsored-provider-catalog/empty.catalog.json"
            ).read_text(encoding="utf-8")
        )
        validator = self.validator("sponsored-provider-catalog.schema.json", "")
        validator.validate(fixture)
        entry = {
            "id": "example-relay",
            "displayName": "示例中转站",
            "description": "推广说明",
            "apiFamily": "openai-completions",
            "apiBaseUrl": "https://relay.example/v1",
            "signupUrl": "https://relay.example/register?ref=test",
            "commissionDisclosure": "发行方可能获得佣金",
            "priority": 100,
        }
        fixture["entries"] = [entry]
        validator.validate(fixture)
        for field, invalid in (
            ("commissionDisclosure", None),
            ("apiFamily", "arbitrary-runtime"),
            ("secretHeader", "forbidden"),
        ):
            changed = deepcopy(fixture)
            if invalid is None:
                del changed["entries"][0][field]
            else:
                changed["entries"][0][field] = invalid
            with (
                self.subTest(field=field),
                self.assertRaises(jsonschema.ValidationError),
            ):
                validator.validate(changed)

    def test_sponsored_installation_and_credential_store_schemas(self) -> None:
        """功能：验证非敏感安装 fixture，并拒绝 route/credential 未知字段和危险 family。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        installed = json.loads(
            (
                CONFORMANCE
                / "fixtures/sponsored-provider-commercial/installed-routes.json"
            ).read_text(encoding="utf-8")
        )
        installation_validator = self.validator(
            "sponsored-provider-installation.schema.json", ""
        )
        installation_validator.validate(installed)
        changed = deepcopy(installed)
        changed["routes"][0]["apiFamily"] = "arbitrary-runtime"
        with self.assertRaises(jsonschema.ValidationError):
            installation_validator.validate(changed)
        changed = deepcopy(installed)
        changed["routes"][0]["apiKey"] = "forbidden"
        with self.assertRaises(jsonschema.ValidationError):
            installation_validator.validate(changed)

        credential_validator = self.validator(
            "provider-credential-store.schema.json", ""
        )
        credential_validator.validate(
            {
                "schemaVersion": "0.1",
                "credentials": [
                    {"providerId": "relay-example-relay", "apiKey": "x"}
                ],
            }
        )
        with self.assertRaises(jsonschema.ValidationError):
            credential_validator.validate(
                {
                    "schemaVersion": "0.1",
                    "credentials": [
                        {
                            "providerId": "relay-example-relay",
                            "apiKey": "x",
                            "environment": "FORBIDDEN",
                        }
                    ],
                }
            )

    def test_storage_configuration_schema_closes_provider_and_secret_fields(self) -> None:
        """功能：验证 SQLite/远程配置互斥，并拒绝内联连接串和未知 provider。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        validator = self.validator("storage-config.schema.json", "")
        validator.validate(
            {
                "schemaVersion": "0.1",
                "provider": "sqlite",
                "sqlitePath": "/var/lib/qxnm-forge/application.db",
                "maxConnections": 8,
            }
        )
        validator.validate(
            {
                "schemaVersion": "0.1",
                "provider": "postgresql",
                "connectionEnvironment": "QXNM_FORGE_DATABASE_URL",
                "maxConnections": 16,
            }
        )
        for invalid in (
            {
                "schemaVersion": "0.1",
                "provider": "sqlite",
                "sqlitePath": "/tmp/application.db",
                "connectionEnvironment": "QXNM_FORGE_DATABASE_URL",
                "maxConnections": 8,
            },
            {
                "schemaVersion": "0.1",
                "provider": "mysql",
                "sqlitePath": "/tmp/application.db",
                "maxConnections": 8,
            },
            {
                "schemaVersion": "0.1",
                "provider": "postgresql",
                "connectionEnvironment": "QXNM_FORGE_DATABASE_URL",
                "connectionString": "forbidden-secret",
                "maxConnections": 8,
            },
            {
                "schemaVersion": "0.1",
                "provider": "arbitrary-runtime",
                "maxConnections": 8,
            },
        ):
            with self.subTest(provider=invalid["provider"]):
                with self.assertRaises(jsonschema.ValidationError):
                    validator.validate(invalid)

    def test_provider_claims_are_exactly_route_scoped_and_evidence_backed(self) -> None:
        """功能：验证首批 Provider claims 精确落到双实现六 route 的 48 个 feature cells。

        输入：共享 capability matrix 与 Provider manifest。
        输出：Rust/.NET 各 24 条唯一 conformant claim，且所有 evidence 路径存在。
        不变量：claim identity 含 apiFamily；不声明 reasoning/live_smoke 或 OpenRouter
        streaming/tools，也不把 sibling route 合并为品牌级能力。
        失败：Schema、计数、route、feature、状态、notes 或 evidence 漂移时测试失败。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        matrix = json.loads(
            (ROOT / "SPEC/capabilities.json").read_text(encoding="utf-8")
        )
        self.validator("capability-matrix.schema.json", "").validate(matrix)
        manifest = json.loads(
            (ROOT / "SPEC/providers.v1.json").read_text(encoding="utf-8")
        )
        manifest_routes = {
            (provider["id"], route["apiFamily"])
            for provider in manifest["providers"]
            for route in provider["routes"]
        }
        route_features = {
            ("groq", "openai-completions"): {
                "authentication",
                "text",
                "streaming",
                "tools",
            },
            ("minimax", "anthropic-messages"): {
                "authentication",
                "text",
                "streaming",
                "tools",
            },
            ("mistral", "mistral-conversations"): {
                "authentication",
                "text",
                "streaming",
                "tools",
            },
            ("openai", "openai-responses"): {
                "authentication",
                "text",
                "streaming",
                "tools",
            },
            ("google", "google-generative-ai"): {
                "authentication",
                "text",
                "streaming",
                "tools",
            },
            ("openrouter", "openrouter-images"): {
                "authentication",
                "text",
                "image_input",
                "image_output",
            },
        }
        implementations = set(matrix["implementations"])
        expected = {
            (implementation, provider_id, api_family, feature)
            for implementation in implementations
            for (provider_id, api_family), features in route_features.items()
            for feature in features
        }
        claims = [
            claim for claim in matrix["claims"] if claim["target"]["kind"] == "provider"
        ]
        actual = {
            (
                claim["implementation"],
                claim["target"]["id"],
                claim["target"]["apiFamily"],
                claim["target"]["feature"],
            )
            for claim in claims
        }
        self.assertEqual(len(claims), len(expected))
        self.assertEqual(len(actual), len(expected))
        self.assertEqual(actual, expected)
        for claim in claims:
            target = claim["target"]
            route = (target["id"], target["apiFamily"])
            self.assertIn(route, manifest_routes)
            self.assertEqual(claim["status"], "conformant")
            self.assertIn("Linux offline loopback", claim["notes"])
            self.assertIn("live smoke", claim["notes"])
            for evidence in claim["evidence"]:
                self.assertTrue((ROOT / evidence).is_file(), evidence)

    def test_writer_lock_owner_schema_rejects_external_host(self) -> None:
        """功能：验证 portable writer owner 正例并拒绝外部 host 注入。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        fixture = json.loads(
            (CONFORMANCE / "fixtures/session/writer-lock-cases.json").read_text(
                encoding="utf-8"
            )
        )
        owner = deepcopy(fixture["cases"][0]["owner"])
        validator = self.validator("session/writer-lock.schema.json", "#/$defs/owner")
        validator.validate(owner)
        owner["endpoint"]["host"] = "203.0.113.9"
        with self.assertRaises(jsonschema.ValidationError):
            validator.validate(owner)

    def test_writer_lock_cases_match_schema_and_fixed_semantics(self) -> None:
        """功能：验证九个 writer lease 案例的固定顺序与安全决策。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        fixture = json.loads(
            (CONFORMANCE / "fixtures/session/writer-lock-cases.json").read_text(
                encoding="utf-8"
            )
        )
        self.validator("session/writer-lock-cases.schema.json", "").validate(fixture)
        decisions = [
            (case["name"], case["probe"], case["expected"]) for case in fixture["cases"]
        ]
        self.assertEqual(
            decisions,
            [
                ("live_owner", "connected", "locked"),
                (
                    "live_unresponsive_owner",
                    "connected_without_response",
                    "locked",
                ),
                ("ambiguous_probe_timeout", "timeout", "locked"),
                ("dead_owner", "connection_refused", "stale_candidate"),
                (
                    "metadata_initializing",
                    "metadata_missing_within_grace",
                    "wait",
                ),
                (
                    "metadata_abandoned",
                    "metadata_missing_after_grace",
                    "stale_candidate",
                ),
                ("external_host_injection", "invalid_external_host", "invalid"),
                ("stale_takeover_race_lost", "atomic_rename_lost", "retry"),
                ("release_token_changed", "release_token_mismatch", "preserve"),
            ],
        )

    def test_branch_and_compaction_rpc_match_protocol_schema(self) -> None:
        """功能：验证 branch select 与 compaction 请求/结果的封闭 wire DTO。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        branch_request = {
            "jsonrpc": "2.0",
            "id": "branch-select-1",
            "method": "session/branch/select",
            "params": {
                "sessionId": "session-branch",
                "expectedHeadRecordId": "record-current",
                "targetLeafRecordId": "record-target",
            },
        }
        compact_request = {
            "jsonrpc": "2.0",
            "id": "compact-1",
            "method": "session/compact",
            "params": {
                "sessionId": "session-branch",
                "expectedHeadRecordId": "record-current",
                "firstRetainedRecordId": "record-user",
                "summaryText": "Portable summary.",
                "provider": {"id": "faux", "modelId": "faux-v1"},
                "usage": {"inputTokens": 10, "outputTokens": 2, "totalTokens": 12},
                "tokensBefore": 100,
                "tokensAfter": 40,
                "strategy": "faux-summary-v1",
            },
        }
        self.validator(
            "protocol/jsonrpc.schema.json", "#/$defs/sessionBranchSelectRequest"
        ).validate(branch_request)
        self.validator(
            "protocol/jsonrpc.schema.json", "#/$defs/sessionCompactRequest"
        ).validate(compact_request)
        self.validator(
            "protocol/jsonrpc.schema.json", "#/$defs/sessionBranchSelectResult"
        ).validate(
            {
                "targetLeafRecordId": "record-target",
                "selectionRecordId": "record-selection",
                "selectedHeadRecordId": "record-selection",
            }
        )
        self.validator(
            "protocol/jsonrpc.schema.json", "#/$defs/sessionCompactResult"
        ).validate(
            {
                "summaryMessageId": "message-summary",
                "summaryRecordId": "record-summary",
                "compactionRecordId": "record-compaction",
                "selectedHeadRecordId": "record-compaction",
            }
        )

    def test_provider_manifest_rejects_duplicate_or_replaced_identity(self) -> None:
        """功能：确认 Schema 后语义门禁拒绝重复 ID 和被替换的冻结 Provider。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        from CONFORMANCE.repository_data_validation import (
            DataValidationError,
            validate_provider_manifest_semantics,
        )

        manifest = json.loads(
            (ROOT / "SPEC/providers.v1.json").read_text(encoding="utf-8")
        )
        validate_provider_manifest_semantics(manifest)
        duplicate = deepcopy(manifest)
        duplicate["providers"][1]["id"] = duplicate["providers"][0]["id"]
        with self.assertRaises(DataValidationError):
            validate_provider_manifest_semantics(duplicate)
        replaced = deepcopy(manifest)
        replaced["providers"][0]["id"] = "replacement-provider"
        with self.assertRaises(DataValidationError):
            validate_provider_manifest_semantics(replaced)

    def test_frozen_model_catalog_matches_schema_and_semantics(self) -> None:
        """功能：验证 1,041 条文本和 35 条图像模型的 Schema、来源与身份语义。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        from CONFORMANCE.repository_data_validation import (
            validate_model_catalog_semantics,
        )

        catalog = json.loads((ROOT / "SPEC/models.v1.json").read_text(encoding="utf-8"))
        self.validator("model-catalog.schema.json", "").validate(catalog)
        validate_model_catalog_semantics(catalog)
        self.assertEqual(catalog["source"]["observedCounts"]["textModels"], 1041)
        self.assertEqual(catalog["source"]["observedCounts"]["imageModels"], 35)
        self.assertEqual(len(catalog["models"]), 1076)

    def test_model_catalog_semantics_rejects_drift(self) -> None:
        """功能：确认目录门禁拒绝重复模型、HTTP、未知模板、来源计数及内容摘要漂移。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        from CONFORMANCE.repository_data_validation import (
            DataValidationError,
            validate_model_catalog_semantics,
        )

        catalog = json.loads((ROOT / "SPEC/models.v1.json").read_text(encoding="utf-8"))
        duplicate = deepcopy(catalog)
        duplicate["models"][1]["modelId"] = duplicate["models"][0]["modelId"]
        with self.assertRaises(DataValidationError):
            validate_model_catalog_semantics(duplicate)

        insecure_endpoint = deepcopy(catalog)
        insecure_endpoint["models"][0]["endpoint"]["baseUrl"] = insecure_endpoint[
            "models"
        ][0]["endpoint"]["baseUrl"].replace("https://", "http://", 1)
        with self.assertRaises(DataValidationError):
            validate_model_catalog_semantics(insecure_endpoint)

        unknown_template = deepcopy(catalog)
        templated_model = next(
            model
            for model in unknown_template["models"]
            if model["endpoint"]["strategy"] == "template"
        )
        old_variable = templated_model["endpoint"]["templateVariables"][0]
        templated_model["endpoint"]["baseUrl"] = templated_model["endpoint"][
            "baseUrl"
        ].replace("{" + old_variable + "}", "{UNDECLARED_ORIGIN}")
        templated_model["endpoint"]["templateVariables"][0] = "UNDECLARED_ORIGIN"
        with self.assertRaises(DataValidationError):
            validate_model_catalog_semantics(unknown_template)

        wrong_count = deepcopy(catalog)
        wrong_count["source"]["observedCounts"]["textModels"] += 1
        with self.assertRaises(DataValidationError):
            validate_model_catalog_semantics(wrong_count)

        changed_capability = deepcopy(catalog)
        changed_capability["models"][0]["capabilities"]["reasoning"] = not (
            changed_capability["models"][0]["capabilities"]["reasoning"]
        )
        with self.assertRaises(DataValidationError):
            validate_model_catalog_semantics(changed_capability)

    def test_provider_manifest_references_and_environment_are_closed(self) -> None:
        """功能：确认 manifest 拒绝悬空引用、非法环境、模板绑定及内容摘要漂移。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        from CONFORMANCE.repository_data_validation import (
            DataValidationError,
            validate_provider_manifest_semantics,
        )

        manifest = json.loads(
            (ROOT / "SPEC/providers.v1.json").read_text(encoding="utf-8")
        )
        catalog = json.loads((ROOT / "SPEC/models.v1.json").read_text(encoding="utf-8"))
        validate_provider_manifest_semantics(manifest, catalog=catalog)

        unknown_adapter = deepcopy(manifest)
        unknown_adapter["providers"][0]["routes"][0]["adapterId"] = "missing"
        with self.assertRaises(DataValidationError):
            validate_provider_manifest_semantics(unknown_adapter, catalog=catalog)

        unknown_catalog = deepcopy(manifest)
        unknown_catalog["providers"][0]["routes"][0]["modelCatalogId"] = "missing"
        with self.assertRaises(DataValidationError):
            validate_provider_manifest_semantics(unknown_catalog, catalog=catalog)

        unknown_header_policy = deepcopy(manifest)
        unknown_header_policy["providers"][0]["routes"][0]["headerPolicyId"] = "missing"
        with self.assertRaises(DataValidationError):
            validate_provider_manifest_semantics(unknown_header_policy, catalog=catalog)

        unknown_auth = deepcopy(manifest)
        unknown_auth["providers"][0]["routes"][0]["authProfileIds"] = ["missing"]
        with self.assertRaises(DataValidationError):
            validate_provider_manifest_semantics(unknown_auth, catalog=catalog)

        invalid_environment = deepcopy(manifest)
        invalid_environment["providers"][0]["environment"][0]["name"] = "aws secret"
        with self.assertRaises(DataValidationError):
            validate_provider_manifest_semantics(invalid_environment, catalog=catalog)

        changed_environment_role = deepcopy(manifest)
        changed_environment_role["providers"][0]["environment"][0]["role"] = (
            "configuration"
        )
        with self.assertRaises(DataValidationError):
            validate_provider_manifest_semantics(
                changed_environment_role, catalog=catalog
            )

        missing_binding = deepcopy(manifest)
        route_with_template = next(
            route
            for provider in missing_binding["providers"]
            for route in provider["routes"]
            if route["endpoint"]["templateBindings"]
        )
        route_with_template["endpoint"]["templateBindings"] = []
        with self.assertRaises(DataValidationError):
            validate_provider_manifest_semantics(missing_binding, catalog=catalog)

    def test_initialize_allows_honest_faux_only_capability(self) -> None:
        """功能：确认垂直切面可只声明真实可用 faux Provider 且 tools 为空。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        trace = json.loads(
            (CONFORMANCE / "fixtures/golden/hello.trace.ndjson")
            .read_text(encoding="utf-8")
            .splitlines()[0]
        )
        trace["result"]["implementation"] = {
            "name": "honest-minimal",
            "version": "0.1.0",
            "language": "dotnet",
        }
        self.validator(
            "protocol/jsonrpc.schema.json", "#/$defs/initializeResult"
        ).validate(trace["result"])


if __name__ == "__main__":
    unittest.main()
