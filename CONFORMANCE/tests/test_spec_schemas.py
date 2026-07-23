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

    @staticmethod
    def local_display_allowed(value: object, contract: dict[str, object]) -> bool:
        """功能：按 ADR 0032 的保守语法判断合成 DISPLAY 是否明确指向本地 Unix display。

        输入：待验证值与 fixture 冻结的长度、display number 和 screen number 上限。
        输出：仅 :N[.S] 或 unix/:N[.S] 的有界 ASCII 十进制形式返回 true。
        不变量：不解析宿主 socket、不连接 X11，也不接受 hostname、TCP 或 SSH forwarding。
        失败：缺失、非字符串、非 ASCII、空数字段、多余点或数值越界均返回 false。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        if (
            not isinstance(value, str)
            or not value.isascii()
            or len(value) == 0
            or len(value) > contract["maxLength"]
        ):
            return False
        if value.startswith("unix/:"):
            numeric = value[len("unix/:") :]
        elif value.startswith(":"):
            numeric = value[1:]
        else:
            return False
        parts = numeric.split(".")
        if len(parts) not in (1, 2):
            return False
        if any(
            not part or any(character < "0" or character > "9" for character in part)
            for part in parts
        ):
            return False
        if int(parts[0]) > contract["maxDisplayNumber"]:
            return False
        return len(parts) == 1 or int(parts[1]) <= contract["maxScreenNumber"]

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

    def test_provider_template_catalog_fixture_protocol_and_security_negatives(
        self,
    ) -> None:
        """功能：验证品牌中立 Provider 配置模板目录及其只读 RPC 契约。

        输入：20 项冻结派生与 3 项受控官方兼容模板 fixture、空参数请求、能力矩阵及安全负例。
        输出：合法目录、RPC 和双实现声明通过，未知参数与敏感或能力声明字段被拒绝。
        不变量：模板仅是本地配置建议，不携带凭据且不声称可执行或已经远程验证。
        失败：目录字段、顺序、协议引用或封闭对象约束漂移时测试失败。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        fixture = json.loads(
            (
                CONFORMANCE / "fixtures/provider-catalog/configuration-templates.json"
            ).read_text(encoding="utf-8")
        )
        catalog_validator = self.validator(
            "provider-catalog.schema.json", "#/$defs/listResult"
        )
        catalog_validator.validate(fixture)
        template_ids = [template["templateId"] for template in fixture["templates"]]
        self.assertEqual(
            [
                "ant-ling",
                "anthropic",
                "cerebras",
                "deepseek",
                "fireworks",
                "google",
                "groq",
                "huggingface",
                "moonshotai",
                "moonshotai-cn",
                "nvidia",
                "openai",
                "opencode",
                "opencode-go",
                "openrouter",
                "together",
                "xai",
                "xiaomi",
                "xiaomi-token-plan-ams",
                "xiaomi-token-plan-cn",
                "xiaomi-token-plan-sgp",
                "zai",
                "zai-coding-cn",
            ],
            template_ids,
        )
        self.assertEqual(sorted(template_ids), template_ids)
        official_compatibility_templates = {
            template["templateId"]: {
                "displayName": template["displayName"],
                "suggestedProviderId": template["suggestedProviderId"],
                "apiFamily": template["apiFamily"],
                "defaultBaseUrl": template["defaultBaseUrl"],
                "modelDiscovery": template["modelDiscovery"],
                "logoAssetId": template["logoAssetId"],
            }
            for template in fixture["templates"]
            if template["templateId"] in {"anthropic", "google", "openai"}
        }
        self.assertEqual(
            {
                "anthropic": {
                    "displayName": "Anthropic Claude",
                    "suggestedProviderId": "custom-anthropic",
                    "apiFamily": "openai-completions",
                    "defaultBaseUrl": "https://api.anthropic.com/v1",
                    "modelDiscovery": "openai-models",
                    "logoAssetId": None,
                },
                "google": {
                    "displayName": "Google Gemini",
                    "suggestedProviderId": "custom-google",
                    "apiFamily": "openai-completions",
                    "defaultBaseUrl": (
                        "https://generativelanguage.googleapis.com/v1beta/openai"
                    ),
                    "modelDiscovery": "openai-models",
                    "logoAssetId": None,
                },
                "openai": {
                    "displayName": "OpenAI",
                    "suggestedProviderId": "custom-openai",
                    "apiFamily": "openai-completions",
                    "defaultBaseUrl": "https://api.openai.com/v1",
                    "modelDiscovery": "openai-models",
                    "logoAssetId": None,
                },
            },
            official_compatibility_templates,
        )

        request = {
            "jsonrpc": "2.0",
            "id": "provider-catalog",
            "method": "providerCatalog/list",
            "params": {},
        }
        self.validator(
            "protocol/jsonrpc.schema.json", "#/$defs/providerCatalogListRequest"
        ).validate(request)
        self.validator(
            "protocol/jsonrpc.schema.json", "#/$defs/providerCatalogListResult"
        ).validate(fixture)

        invalid_request = deepcopy(request)
        invalid_request["params"]["refresh"] = True
        with self.assertRaises(jsonschema.ValidationError):
            self.validator(
                "protocol/jsonrpc.schema.json", "#/$defs/providerCatalogListRequest"
            ).validate(invalid_request)

        for field, value in (
            ("credential", "forbidden"),
            ("apiKey", "forbidden"),
            ("executable", True),
            ("verified", True),
        ):
            invalid_catalog = deepcopy(fixture)
            invalid_catalog["templates"][0][field] = value
            with (
                self.subTest(template_negative=field),
                self.assertRaises(jsonschema.ValidationError),
            ):
                catalog_validator.validate(invalid_catalog)

        matrix = json.loads(
            (ROOT / "SPEC/capabilities.json").read_text(encoding="utf-8")
        )
        catalog_claims = [
            claim
            for claim in matrix["claims"]
            if claim["target"]
            == {"kind": "foundation", "id": "provider.template_catalog"}
        ]
        self.assertEqual(
            {"rust", "dotnet"},
            {claim["implementation"] for claim in catalog_claims},
        )
        self.assertEqual(2, len(catalog_claims))
        for claim in catalog_claims:
            self.assertEqual("implemented", claim["status"])
            for evidence in claim["evidence"]:
                self.assertTrue((ROOT / evidence).is_file(), evidence)

    def test_sponsored_installation_and_credential_store_schemas(self) -> None:
        """功能：验证非敏感安装 fixture 与仅供迁移的 legacy credential schema。

        输入：v0.1 推广 route、v0.1 聚合 credential migration input 与 v0.2 规范文本。
        输出：旧输入仍可升级，未知字段、危险 family 及伪造 v0.2 JSON 文档被拒绝。
        不变量：当前 CredentialStore 是逐叶目录，旧 schema 不得冒充现行存储格式。
        失败：schema ID/用途或目录、叶名、字节/权限、presence 边界说明漂移时测试失败。

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

        credential_schema = json.loads(
            (SCHEMAS / "provider-credential-store.schema.json").read_text(
                encoding="utf-8"
            )
        )
        self.assertEqual(
            "https://schemas.agentprotocol.local/spec/0.1/schemas/provider-credential-store.schema.json",
            credential_schema["$id"],
        )
        self.assertIn("Legacy v0.1", credential_schema["title"])
        self.assertIn("migration input only", credential_schema["$comment"])
        credential_validator = self.validator(
            "provider-credential-store.schema.json", ""
        )
        credential_validator.validate(
            {
                "schemaVersion": "0.1",
                "credentials": [{"providerId": "relay-example-relay", "apiKey": "x"}],
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
        with self.assertRaises(jsonschema.ValidationError):
            credential_validator.validate(
                {"schemaVersion": "0.2", "credentials": []}
            )

        commercial_contract = (
            ROOT / "SPEC/provider-commercial-state.md"
        ).read_text(encoding="utf-8")
        for required_text in (
            "CredentialStore v0.2",
            "`credentials/provider-credentials.d/`",
            "base64url_no_padding(UTF8(providerId))",
            "`1..16384` bytes",
            "最多 128 条",
            "no-follow 元数据",
            "最终 HTTP 认证 header",
            "`0700`",
            "`0600`",
            "`credentials/.provider-credentials.d.migrating/`",
            "legacy v0.1 migration input",
            "唯一迁移例外",
        ):
            self.assertIn(required_text, commercial_contract)

    def test_storage_configuration_schema_closes_provider_and_secret_fields(
        self,
    ) -> None:
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

    def test_custom_provider_connection_schema_never_accepts_credentials(self) -> None:
        """功能：验证自定义连接 DTO 与 CredentialStore 保持严格分离。

        输入：一个合法 HTTPS OpenAI-compatible connection 及安全负例。
        输出：合法及待发现空目录实体通过，secret、危险 URL、重复模型和未知字段全部拒绝。
        不变量：可读取的 connection 投影永远只有两个 credential presence 状态。
        失败：ADR 0029 的字段或封闭对象约束漂移时测试失败。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        validator = self.validator(
            "custom-provider-connection.schema.json", "#/$defs/connection"
        )
        connection = {
            "connectionId": "connection-example",
            "revision": 1,
            "displayName": "示例 New API",
            "providerId": "custom-example",
            "apiFamily": "openai-completions",
            "baseUrl": "https://api.example/v1",
            "modelsUrl": "https://api.example/v1/models",
            "modelIds": ["gpt-5"],
            "supportsTools": True,
            "supportsImageInput": True,
            "supportsImageOutput": True,
            "logoAssetId": "new-api",
            "enabled": True,
            "credentialConfigured": False,
            "imageCredentialConfigured": False,
            "createdAt": "2026-07-21T00:00:00Z",
            "updatedAt": "2026-07-21T00:00:00Z",
        }
        validator.validate(connection)
        for endpoint in (
            "https://api.example:65535/v1/%E4%B8%AD",
            "https://[2001:db8::1]:443/v1",
        ):
            bounded_endpoint = deepcopy(connection)
            bounded_endpoint["baseUrl"] = endpoint
            bounded_endpoint["modelsUrl"] = endpoint
            validator.validate(bounded_endpoint)
        pending_discovery = deepcopy(connection)
        pending_discovery["modelIds"] = []
        validator.validate(pending_discovery)
        configuration = {
            key: value
            for key, value in connection.items()
            if key not in {"credentialConfigured", "imageCredentialConfigured"}
        }
        self.validator(
            "custom-provider-connection.schema.json", "#/$defs/document"
        ).validate({"schemaVersion": "0.3", "connections": [configuration]})
        projected_fields = {
            "connectionId",
            "revision",
            "credentialConfigured",
            "imageCredentialConfigured",
            "createdAt",
            "updatedAt",
        }
        connection_input = {
            key: value
            for key, value in connection.items()
            if key not in projected_fields
        }
        protocol_cases = (
            ("providerConnections/list", {}, "providerConnectionsListRequest"),
            (
                "providerConnections/create",
                {"connection": connection_input},
                "providerConnectionsCreateRequest",
            ),
            (
                "providerConnections/update",
                {
                    "connectionId": connection["connectionId"],
                    "expectedRevision": 1,
                    "connection": connection_input,
                },
                "providerConnectionsUpdateRequest",
            ),
            (
                "providerConnections/delete",
                {"connectionId": connection["connectionId"], "expectedRevision": 1},
                "providerConnectionsDeleteRequest",
            ),
            (
                "providerConnections/discoverModels",
                {"connectionId": connection["connectionId"], "expectedRevision": 1},
                "providerConnectionsDiscoverModelsRequest",
            ),
            (
                "providerCredentials/set",
                {
                    "providerId": "custom-example",
                    "credentialKind": "responses",
                    "credential": "test-only-secret",
                },
                "providerCredentialsSetRequest",
            ),
            (
                "providerCredentials/remove",
                {"providerId": "custom-example", "credentialKind": "image"},
                "providerCredentialsRemoveRequest",
            ),
        )
        for method, params, definition in protocol_cases:
            request = {
                "jsonrpc": "2.0",
                "id": method,
                "method": method,
                "params": params,
            }
            with self.subTest(method=method):
                self.validator(
                    "protocol/jsonrpc.schema.json", f"#/$defs/{definition}"
                ).validate(request)
        discovery_result = {
            "connection": connection,
            "discoveredCount": 1,
            "restartRequired": True,
        }
        self.validator(
            "custom-provider-connection.schema.json", "#/$defs/discoverModelsResult"
        ).validate(discovery_result)
        self.validator(
            "protocol/jsonrpc.schema.json", "#/$defs/successResponse"
        ).validate(
            {
                "jsonrpc": "2.0",
                "id": "providerConnections/discoverModels",
                "result": discovery_result,
            }
        )
        for field, value in (
            ("credential", "forbidden"),
            ("apiKey", "forbidden"),
            ("baseUrl", "http://api.example/v1"),
            ("baseUrl", "https://user@example.test/v1"),
            ("baseUrl", "https://api.example/v1?token=x"),
            ("modelIds", ["gpt-5", "gpt-5"]),
        ):
            invalid = deepcopy(connection)
            invalid[field] = value
            with self.subTest(field=field, value=value):
                with self.assertRaises(jsonschema.ValidationError):
                    validator.validate(invalid)

        unsafe_https_urls = (
            "https://api.example:abc/v1",
            "https://api.example:65536/v1",
            "https://api.example:99999/v1",
            "https://api example/v1",
            "https://api..example/v1",
            "https://api.example/%zz",
            "https://api.example/%2",
        )
        for field in ("baseUrl", "modelsUrl"):
            for value in unsafe_https_urls:
                invalid = deepcopy(connection)
                invalid[field] = value
                with self.subTest(field=field, value=value):
                    with self.assertRaises(jsonschema.ValidationError):
                        validator.validate(invalid)

    def test_artifact_read_contract_is_chunked_and_path_free(self) -> None:
        """功能：验证 Session 图片 artifact 只能通过有界、连续分块 RPC 回读。

        输入：opaque Session/artifact ID、显式 offset 与不超过 512 KiB 的规范 Base64 分块。
        输出：合法请求和响应通过，负 offset、空分块、路径及未知字段全部拒绝。
        不变量：wire 只携带不可变 artifact 元数据和字节，不暴露宿主路径或远程 URL。
        失败：artifacts/read 方法、分块边界或封闭对象约束漂移时测试失败。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        request = {
            "jsonrpc": "2.0",
            "id": "artifact-read-1",
            "method": "artifacts/read",
            "params": {
                "sessionId": "session-example",
                "artifactId": "image-example",
                "offset": 0,
            },
        }
        self.validator(
            "protocol/jsonrpc.schema.json", "#/$defs/artifactReadRequest"
        ).validate(request)
        artifact = {
            "artifactId": "image-example",
            "mediaType": "image/png",
            "byteLength": 16,
            "sha256": "a" * 64,
        }
        result = {
            "artifact": artifact,
            "offset": 0,
            "dataBase64": "iVBORw0KGgo=",
            "nextOffset": 8,
        }
        result_validator = self.validator(
            "protocol/jsonrpc.schema.json", "#/$defs/artifactReadResult"
        )
        result_validator.validate(result)
        self.validator(
            "protocol/jsonrpc.schema.json", "#/$defs/successResponse"
        ).validate(
            {
                "jsonrpc": "2.0",
                "id": "artifact-read-1",
                "result": result,
            }
        )
        for invalid in (
            {**request, "params": {**request["params"], "offset": -1}},
            {**request, "params": {**request["params"], "offset": 33_554_432}},
            {**request, "params": {**request["params"], "path": "/tmp/image.png"}},
        ):
            with self.assertRaises(jsonschema.ValidationError):
                self.validator(
                    "protocol/jsonrpc.schema.json", "#/$defs/artifactReadRequest"
                ).validate(invalid)
        for invalid in (
            {**result, "dataBase64": ""},
            {**result, "dataBase64": "AB=="},
            {**result, "artifact": {**artifact, "mediaType": "application/pdf"}},
            {**result, "artifact": {**artifact, "byteLength": 33_554_433}},
            {**result, "nextOffset": 33_554_433},
            {**result, "path": "/tmp/image.png"},
            {**result, "remoteUrl": "https://example.test/image.png"},
        ):
            with self.assertRaises(jsonschema.ValidationError):
                result_validator.validate(invalid)

    def test_custom_image_and_artifact_read_claims_remain_implemented(self) -> None:
        """功能：验证图片闭环能力只登记已有双实现证据，不越级声明共同符合性。

        输入：共享 capability matrix、两种自定义 OpenAI-compatible family 与 UI/foundation claims。
        输出：双实现 artifact read、八个 family/实现图片 cell、连接和 UI 证据均完整且状态为 implemented。
        不变量：没有共同动态 runner 时不把新增图片能力登记为 conformant。
        失败：claim 缺失、重复、状态升级、证据路径失效或陈旧 UI 说明回归时测试失败。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        matrix = json.loads(
            (ROOT / "SPEC/capabilities.json").read_text(encoding="utf-8")
        )
        self.validator("capability-matrix.schema.json", "").validate(matrix)
        identities = [
            (claim["implementation"], tuple(sorted(claim["target"].items())))
            for claim in matrix["claims"]
        ]
        self.assertEqual(len(identities), len(set(identities)))

        artifact_claims = [
            claim
            for claim in matrix["claims"]
            if claim["target"] == {"kind": "foundation", "id": "session.artifact_read"}
        ]
        self.assertEqual(
            {"rust", "dotnet"},
            {claim["implementation"] for claim in artifact_claims},
        )
        self.assertEqual(2, len(artifact_claims))

        image_claims = [
            claim
            for claim in matrix["claims"]
            if claim["target"]["kind"] == "api_family"
            and claim["target"]["id"] in {"openai-responses", "openai-completions"}
            and claim["target"]["feature"] in {"image_input", "image_output"}
        ]
        expected_image_cells = {
            (implementation, family, feature)
            for implementation in ("rust", "dotnet")
            for family in ("openai-responses", "openai-completions")
            for feature in ("image_input", "image_output")
        }
        self.assertEqual(
            expected_image_cells,
            {
                (
                    claim["implementation"],
                    claim["target"]["id"],
                    claim["target"]["feature"],
                )
                for claim in image_claims
            },
        )
        for claim in artifact_claims + image_claims:
            self.assertEqual("implemented", claim["status"])
            self.assertIn("共同动态", claim["notes"])
            for evidence in claim["evidence"]:
                self.assertTrue((ROOT / evidence).is_file(), evidence)

        for feature in ("provider.custom_connection", "ui.headless_contract"):
            claims = [
                claim
                for claim in matrix["claims"]
                if claim["target"] == {"kind": "foundation", "id": feature}
            ]
            self.assertEqual(2, len(claims))
            self.assertTrue(all(claim["status"] == "implemented" for claim in claims))
            for claim in claims:
                for evidence in claim["evidence"]:
                    self.assertTrue((ROOT / evidence).is_file(), evidence)
        ui_notes = " ".join(
            claim["notes"]
            for claim in matrix["claims"]
            if claim["target"] == {"kind": "foundation", "id": "ui.headless_contract"}
        )
        self.assertIn("视觉闭环已完成", ui_notes)
        self.assertNotIn("没有 artifact 读取/渲染视觉闭环", ui_notes)

    def test_session_lifecycle_contract_is_closed_and_redacted(self) -> None:
        """功能：验证 Session 生命周期分页、摘要、mutation 与归档文档的共同契约。

        输入：合法脱敏摘要、四类 RPC、分页结果和工作区外归档状态。
        输出：合法对象通过，状态矛盾、越界文本、secret/路径字段与重复归档 ID 均拒绝。
        不变量：生命周期 wire 不包含 transcript、journal、宿主路径或 Provider credential。
        失败：ADR 0030 的封闭字段、分页关系或资源上限漂移时测试失败。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        summary = {
            "sessionId": "session-example",
            "title": "实现桌面会话管理",
            "project": "AI-Code",
            "updatedAt": "2026-07-21T03:04:05Z",
            "archived": False,
        }
        lifecycle_schema = "session/lifecycle.schema.json"
        self.validator(lifecycle_schema, "#/$defs/summary").validate(summary)
        self.validator(lifecycle_schema, "#/$defs/archiveDocument").validate(
            {
                "schemaVersion": "0.1",
                "archivedSessionIds": ["session-a", "session-example"],
            }
        )

        protocol_cases = (
            (
                "session/list",
                {"limit": 64},
                "sessionListRequest",
                {
                    "sessions": [summary],
                    "nextCursor": "v1:1",
                    "hasMore": True,
                },
                "sessionListResult",
            ),
            (
                "session/archive",
                {"sessionId": "session-example"},
                "sessionArchiveRequest",
                {"session": {**summary, "archived": True}},
                "sessionSummaryResult",
            ),
            (
                "session/restore",
                {"sessionId": "session-example"},
                "sessionRestoreRequest",
                {"session": summary},
                "sessionSummaryResult",
            ),
            (
                "session/delete",
                {"sessionId": "session-example"},
                "sessionDeleteRequest",
                {"deleted": True},
                "sessionDeleteResult",
            ),
        )
        for method, params, request_definition, result, result_definition in protocol_cases:
            with self.subTest(method=method):
                self.validator(
                    "protocol/jsonrpc.schema.json",
                    f"#/$defs/{request_definition}",
                ).validate(
                    {
                        "jsonrpc": "2.0",
                        "id": method,
                        "method": method,
                        "params": params,
                    }
                )
                self.validator(
                    "protocol/jsonrpc.schema.json",
                    f"#/$defs/{result_definition}",
                ).validate(result)

        summary_validator = self.validator(lifecycle_schema, "#/$defs/summary")
        for field, value in (
            ("title", "x" * 97),
            ("project", "x" * 129),
            ("transcript", []),
            ("workspacePath", "/private/workspace"),
            ("apiKey", "forbidden"),
        ):
            invalid = deepcopy(summary)
            invalid[field] = value
            with (
                self.subTest(summary_negative=field),
                self.assertRaises(jsonschema.ValidationError),
            ):
                summary_validator.validate(invalid)

        list_validator = self.validator(lifecycle_schema, "#/$defs/listResult")
        for invalid in (
            {"sessions": [], "nextCursor": "v1:1", "hasMore": False},
            {"sessions": [summary], "nextCursor": None, "hasMore": True},
            {
                "sessions": [summary],
                "nextCursor": None,
                "hasMore": False,
                "journal": "forbidden",
            },
        ):
            with self.assertRaises(jsonschema.ValidationError):
                list_validator.validate(invalid)

        with self.assertRaises(jsonschema.ValidationError):
            self.validator(lifecycle_schema, "#/$defs/archiveDocument").validate(
                {
                    "schemaVersion": "0.1",
                    "archivedSessionIds": ["session-example", "session-example"],
                }
            )

    def test_agent_profile_v02_contract_and_security_negatives(self) -> None:
        """功能：验证 Agent Profile v0.2 的封闭 DTO、RPC、快照与安全负例。

        输入：共同 profile fixture 以及由其派生的边界和未知字段负例。
        输出：合法 CRUD/run wire 与 journal 快照通过，所有越界输入稳定拒绝。
        不变量：Profile 不携带 secret/endpoint，模型身份完整，工具无重复且快照只收窄能力。
        失败：字段长度、枚举、未知属性、引用或共同错误表发生漂移时测试失败。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        fixture = json.loads(
            (CONFORMANCE / "fixtures/agent-profile/profile-cases.json").read_text(
                encoding="utf-8"
            )
        )
        self.validator("agent-profile-cases.schema.json", "").validate(fixture)

        profile_validator = self.validator(
            "agent-profile.schema.json", "#/$defs/profileInput"
        )
        invalid_profiles = []
        for field, value in (
            ("displayName", ""),
            ("displayName", "x" * 49),
            ("description", "x" * 161),
            ("instructions", ""),
            ("instructions", "x" * 12001),
            ("dangerousActionMode", "allow"),
        ):
            changed = deepcopy(fixture["profileInput"])
            changed[field] = value
            invalid_profiles.append((field, changed))

        missing_family = deepcopy(fixture["profileInput"])
        del missing_family["model"]["apiFamily"]
        invalid_profiles.append(("model.apiFamily", missing_family))
        duplicate_tools = deepcopy(fixture["profileInput"])
        duplicate_tools["requestedToolIds"].append(
            duplicate_tools["requestedToolIds"][0]
        )
        invalid_profiles.append(("requestedToolIds", duplicate_tools))
        invalid_behavior = deepcopy(fixture["profileInput"])
        invalid_behavior["behavior"]["responseStyle"] = "unbounded"
        invalid_profiles.append(("behavior.responseStyle", invalid_behavior))
        for forbidden_field in ("apiKey", "secret", "endpoint", "extensions"):
            changed = deepcopy(fixture["profileInput"])
            changed[forbidden_field] = "forbidden-field"
            invalid_profiles.append((forbidden_field, changed))

        for name, invalid in invalid_profiles:
            with (
                self.subTest(profile_negative=name),
                self.assertRaises(jsonschema.ValidationError),
            ):
                profile_validator.validate(invalid)

        request_validator = self.validator(
            "protocol/jsonrpc.schema.json", "#/$defs/request"
        )
        for request in fixture["wire"].values():
            if isinstance(request, dict) and "method" in request:
                request_validator.validate(request)

        invalid_list = deepcopy(fixture["wire"]["listRequest"])
        invalid_list["params"]["includeDisabled"] = True
        invalid_create = deepcopy(fixture["wire"]["createRequest"])
        invalid_create["params"]["credential"] = "forbidden-field"
        invalid_run = deepcopy(fixture["wire"]["runStartRequest"])
        invalid_run["params"]["agentProfile"]["latest"] = True
        for name, invalid in (
            ("list unknown param", invalid_list),
            ("create secret param", invalid_create),
            ("run profile unknown param", invalid_run),
        ):
            with (
                self.subTest(request_negative=name),
                self.assertRaises(jsonschema.ValidationError),
            ):
                request_validator.validate(invalid)

        snapshot = fixture["runSnapshot"]
        self.assertTrue(
            set(snapshot["effectiveToolIds"]).issubset(snapshot["requestedToolIds"])
        )
        self.assertEqual(
            fixture["wire"]["runStartRequest"]["params"]["provider"],
            {
                "id": snapshot["model"]["providerId"],
                "modelId": snapshot["model"]["modelId"],
                "apiFamily": snapshot["model"]["apiFamily"],
            },
        )
        self.validator(
            "session/journal.schema.json", "#/$defs/runAcceptedData"
        ).validate(
            {
                "runId": "run-profile-1",
                "inputMessageId": "message-profile-1",
                "provider": {"id": "faux", "modelId": "faux-v1", "apiFamily": "faux"},
                "agentProfileSnapshot": snapshot,
            }
        )
        self.assertEqual(
            fixture["advertisementExpectations"]["productionForbiddenMethods"],
            ["faux/configure"],
        )
        self.assertIn(
            {
                "implementation": "rust",
                "mode": "production",
                "method": "faux/configure",
            },
            fixture["advertisementExpectations"]["knownRegressionTargets"],
        )
        self.assertEqual(fixture["createdProfile"]["revision"], 1)
        self.assertEqual(fixture["updatedProfile"]["revision"], 2)
        self.assertEqual(
            fixture["createdProfile"]["profileId"],
            fixture["updatedProfile"]["profileId"],
        )
        expected_error_contract = {
            "profileInvalid": (-32602, False, "agent_profile_invalid"),
            "profileNotFound": (-32602, False, "agent_profile_not_found"),
            "crudRevisionConflict": (-32010, True, "stale_agent_profile_revision"),
            "revisionExhausted": (
                -32009,
                False,
                "agent_profile_revision_exhausted",
            ),
            "runRevisionConflict": (-32010, False, "stale_agent_profile_revision"),
            "profileDisabled": (-32003, False, "agent_profile_disabled"),
            "profileModelMismatch": (-32602, False, "agent_profile_model_mismatch"),
            "profileModelUnavailable": (
                -32005,
                True,
                "agent_profile_model_unavailable",
            ),
        }
        self.assertEqual(
            {
                name: (error["code"], error["retryable"], error["details"]["kind"])
                for name, error in fixture["expectedErrors"].items()
            },
            expected_error_contract,
        )
        self.assertEqual(
            fixture["normalizationExpectations"],
            {
                "lengthUnit": "unicode_scalar_value",
                "rawLengthBeforeTrim": True,
                "trimmedTextFields": [
                    "displayName",
                    "description",
                    "instructions",
                ],
                "identityFields": [
                    "model.providerId",
                    "model.modelId",
                    "model.apiFamily",
                    "requestedToolIds[]",
                ],
                "identityEdgeWhitespace": "reject",
                "whitespaceOnlyRequiredText": "reject",
            },
        )
        self.assertEqual(
            fixture["migrationExpectations"],
            {
                "logicalVersion": "0.2",
                "metadataTable": "application_metadata",
                "legacyMetadataTable": "forge_metadata",
                "profileTables": ["agent_profiles", "agent_profile_tools"],
                "cases": {
                    "fresh": "create_v02",
                    "legacyV01": "migrate_transactionally",
                    "currentV02": "validate_and_noop",
                    "unversionedNonEmpty": "reject_without_modification",
                    "missingVersion": "reject_without_modification",
                    "bothMetadata": "reject_without_modification",
                    "newer": "reject_without_modification",
                    "incompleteV02": "reject_without_modification",
                },
            },
        )
        matrix = json.loads(
            (ROOT / "SPEC/capabilities.json").read_text(encoding="utf-8")
        )
        profile_claims = [
            claim
            for claim in matrix["claims"]
            if claim["target"]["kind"] == "foundation"
            and claim["target"]["id"]
            in {"agent.profile_crud", "agent.profile_run_binding"}
        ]
        self.assertEqual(len(profile_claims), 4)
        self.assertEqual(
            {
                (claim["implementation"], claim["target"]["id"])
                for claim in profile_claims
            },
            {
                (implementation, feature)
                for implementation in ("rust", "dotnet")
                for feature in ("agent.profile_crud", "agent.profile_run_binding")
            },
        )
        for claim in profile_claims:
            self.assertEqual(claim["status"], "implemented")
            self.assertIn("共同动态", claim["notes"])
            self.assertIn("conformant", claim["notes"])
            for evidence in claim["evidence"]:
                self.assertTrue((ROOT / evidence).is_file(), evidence)

    def test_desktop_computer_contract_fixture_and_security_negatives(self) -> None:
        """功能：验证实验性桌面工具的静态合同、敏感 artifact extension 与安全负例。

        输入：只含合成环境/参数的 computer fixture、公共 Schema 与 capability matrix。
        输出：原生 X11/本地 DISPLAY 门禁、合法参数、限制、审批资源和 Session 数据通过，扩权输入稳定拒绝。
        不变量：测试不探测 display、不执行桌面动作、不读取像素，并拒绝 text 与持久化宿主显示标识。
        失败：双实现广告门、公共合同、限制、extension、journal 或 implemented claim 漂移时失败。
        作者：高宏顺
        邮箱：18272669457@163.com
        """

        fixture = json.loads(
            (CONFORMANCE / "fixtures/computer/computer-cases.json").read_text(
                encoding="utf-8"
            )
        )
        self.validator("computer-cases.schema.json", "").validate(fixture)

        platform_gate = fixture["platformGate"]
        self.assertEqual(
            {
                "AGENT_CLIENT_DESKTOP_COMPUTER": "1",
                "AGENT_CLIENT_EXPERIMENTAL_DESKTOP_COMPUTER": "1",
            },
            platform_gate["requiredExactGates"],
        )
        self.assertEqual(
            {
                "environment": "XDG_SESSION_TYPE",
                "normalization": "trim_ascii_lowercase",
                "requiredValue": "x11",
            },
            platform_gate["sessionType"],
        )
        self.assertEqual(
            {
                "environment": "WAYLAND_DISPLAY",
                "allowedStates": ["missing", "empty"],
            },
            platform_gate["waylandDisplay"],
        )
        self.assertEqual(
            {
                "environment": "DISPLAY",
                "allowedSyntax": [":N[.S]", "unix/:N[.S]"],
                "connectionNormalization": "unix/:N[.S]",
                "maxLength": 64,
                "maxDisplayNumber": 65535,
                "maxScreenNumber": 65535,
            },
            platform_gate["localDisplay"],
        )
        gate_cases = platform_gate["cases"]
        self.assertEqual(
            [
                "native_x11_wayland_missing",
                "native_x11_normalized_wayland_empty",
                "desktop_gate_missing",
                "experimental_gate_missing",
                "desktop_gate_not_exact",
                "experimental_gate_not_exact",
                "session_type_missing",
                "session_type_empty",
                "session_type_non_ascii_whitespace",
                "wayland_session",
                "xwayland_detected",
                "wayland_display_whitespace",
                "unknown_session_type",
                "windows_platform",
                "macos_platform",
                "unknown_platform",
                "display_missing",
                "display_empty",
                "display_remote_hostname",
                "display_ssh_forwarded",
                "display_legacy_unix_hostname",
                "display_number_out_of_range",
                "display_screen_out_of_range",
                "display_whitespace",
                "display_multiple_dots",
                "display_non_ascii_digits",
            ],
            [case["name"] for case in gate_cases],
        )
        ascii_lowercase = str.maketrans(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZ", "abcdefghijklmnopqrstuvwxyz"
        )
        for case in gate_cases:
            environment = case["environment"]
            normalized_session_type = (
                environment.get(platform_gate["sessionType"]["environment"], "")
                .strip(" \t\n\r\v\f")
                .translate(ascii_lowercase)
            )
            should_advertise = (
                case["platform"] == "linux"
                and all(
                    environment.get(name) == value
                    for name, value in platform_gate["requiredExactGates"].items()
                )
                and normalized_session_type
                == platform_gate["sessionType"]["requiredValue"]
                and environment.get(platform_gate["waylandDisplay"]["environment"])
                in (None, "")
                and self.local_display_allowed(
                    environment.get(platform_gate["localDisplay"]["environment"]),
                    platform_gate["localDisplay"],
                )
            )
            with self.subTest(platform_gate_case=case["name"]):
                self.assertEqual(case["advertise"], should_advertise)

        invalid_platform_fixture = deepcopy(fixture)
        invalid_platform_fixture["platformGate"]["cases"][0]["environment"][
            "XAUTHORITY"
        ] = "/forbidden/host/path"
        with self.assertRaises(jsonschema.ValidationError):
            self.validator("computer-cases.schema.json", "").validate(
                invalid_platform_fixture
            )
        overlong_display_fixture = deepcopy(fixture)
        overlong_display_fixture["platformGate"]["cases"][0]["environment"][
            "DISPLAY"
        ] = ":" + ("1" * 64)
        with self.assertRaises(jsonschema.ValidationError):
            self.validator("computer-cases.schema.json", "").validate(
                overlong_display_fixture
            )

        cases = fixture["cases"]
        self.assertEqual(
            [
                "computer.observe",
                "computer.screenshot",
                "computer.interact",
                "computer.interact",
                "computer.interact",
                "computer.interact",
            ],
            [case["tool"] for case in cases],
        )
        self.assertEqual(
            ["move", "click", "scroll", "key"],
            [case["arguments"]["action"] for case in cases[2:]],
        )
        self.assertEqual(
            [
                "desktop:screen",
                "desktop:screen",
                "desktop:move",
                "desktop:click",
                "desktop:scroll",
                "desktop:key",
            ],
            [case["approval"]["resource"]["value"] for case in cases],
        )
        self.assertEqual(
            ["high", "high"],
            [case["approval"]["risk"] for case in cases[:2]],
        )
        self.assertTrue(
            all(case["approval"]["risk"] == "critical" for case in cases[2:])
        )
        self.assertTrue(
            all(
                case["approval"]["choices"] == ["allow_once", "deny"]
                for case in cases
            )
        )

        measurement = fixture["capture"]["measurement"]
        self.assertEqual(
            measurement["pixelCount"], measurement["width"] * measurement["height"]
        )
        self.assertEqual(
            measurement["rawByteLength"], measurement["pixelCount"] * 4
        )
        artifact = fixture["capture"]["result"]["content"][1]["artifact"]
        artifact_created = fixture["capture"]["artifactCreatedData"]
        self.assertEqual(measurement["pngByteLength"], artifact["byteLength"])
        self.assertEqual(artifact, artifact_created["artifact"])
        extension = artifact["extensions"]["org.agentprotocol.computer"]
        self.assertEqual(
            {
                "source": "desktop_capture",
                "runId": "run-computer-synthetic-1",
                "sensitivity": "desktop_sensitive",
                "retention": "session_lifecycle",
            },
            extension,
        )
        self.assertEqual(
            extension,
            artifact_created["extensions"]["org.agentprotocol.computer"],
        )
        self.validator(
            "session/journal.schema.json", "#/$defs/artifactCreatedData"
        ).validate(artifact_created)

        serialized = json.dumps(fixture, ensure_ascii=False)
        for forbidden in (
            '"backend"',
            '"displayId"',
            '"windowTitle"',
            '"hostPath"',
            '"credential"',
            '"apiKey"',
            '"action": "text"',
        ):
            self.assertNotIn(forbidden, serialized)

        observe_validator = self.validator(
            "domain/computer.schema.json", "#/$defs/observeArguments"
        )
        interact_validator = self.validator(
            "domain/computer.schema.json", "#/$defs/interactArguments"
        )
        measurement_validator = self.validator(
            "domain/computer.schema.json", "#/$defs/captureMeasurement"
        )
        extension_validator = self.validator(
            "domain/computer.schema.json", "#/$defs/computerExtensionValue"
        )
        artifact_validator = self.validator(
            "domain/computer.schema.json", "#/$defs/captureArtifactReference"
        )

        with self.assertRaises(jsonschema.ValidationError):
            observe_validator.validate({"display": 0})

        interact_validator.validate(
            {"action": "scroll", "deltaX": 0, "deltaY": 0}
        )

        invalid_arguments = (
            {"action": "move", "y": 1},
            {"action": "move", "x": 16384, "y": 0},
            {
                "action": "click",
                "x": 1,
                "y": 1,
                "button": "left",
                "clicks": 4,
            },
            {"action": "key", "key": "a", "modifiers": []},
            {
                "action": "key",
                "key": "escape",
                "modifiers": ["shift", "shift"],
            },
            {"action": "text", "text": "forbidden"},
        )
        for invalid in invalid_arguments:
            with (
                self.subTest(arguments=invalid),
                self.assertRaises(jsonschema.ValidationError),
            ):
                interact_validator.validate(invalid)

        for field, value in (
            ("width", 16385),
            ("height", 16385),
            ("pixelCount", 16777217),
            ("rawByteLength", 67108865),
            ("pngByteLength", 33554433),
        ):
            invalid = deepcopy(measurement)
            invalid[field] = value
            with (
                self.subTest(measurement_negative=field),
                self.assertRaises(jsonschema.ValidationError),
            ):
                measurement_validator.validate(invalid)

        for field in ("backend", "displayId", "windowTitle", "hostPath"):
            invalid = deepcopy(extension)
            invalid[field] = "forbidden"
            with (
                self.subTest(extension_negative=field),
                self.assertRaises(jsonschema.ValidationError),
            ):
                extension_validator.validate(invalid)

        invalid_artifact = deepcopy(artifact)
        invalid_artifact["mediaType"] = "image/jpeg"
        with self.assertRaises(jsonschema.ValidationError):
            artifact_validator.validate(invalid_artifact)
        invalid_artifact = deepcopy(artifact)
        invalid_artifact["byteLength"] = 33554433
        with self.assertRaises(jsonschema.ValidationError):
            artifact_validator.validate(invalid_artifact)

        invalid_record_data = deepcopy(artifact_created)
        invalid_record_data["runId"] = "run-core-field-forbidden"
        with self.assertRaises(jsonschema.ValidationError):
            self.validator(
                "session/journal.schema.json", "#/$defs/artifactCreatedData"
            ).validate(invalid_record_data)

        invalid_case_fixture = deepcopy(fixture)
        invalid_case_fixture["cases"][2]["approval"]["resource"]["value"] = (
            "desktop:click"
        )
        with self.assertRaises(jsonschema.ValidationError):
            self.validator("computer-cases.schema.json", "").validate(
                invalid_case_fixture
            )

        matrix = json.loads(
            (ROOT / "SPEC/capabilities.json").read_text(encoding="utf-8")
        )
        computer_features = {"tool.computer_observe", "tool.computer_interact"}
        self.assertTrue(computer_features.issubset(matrix["foundationFeatures"]))
        computer_claims = [
            claim
            for claim in matrix["claims"]
            if claim["target"]["kind"] == "foundation"
            and claim["target"]["id"] in computer_features
        ]
        self.assertEqual(
            {
                (implementation, feature)
                for implementation in ("rust", "dotnet")
                for feature in computer_features
            },
            {
                (claim["implementation"], claim["target"]["id"])
                for claim in computer_claims
            },
        )
        self.assertEqual(4, len(computer_claims))
        for claim in computer_claims:
            self.assertEqual("implemented", claim["status"])
            self.assertIn("不构成公开支持", claim["notes"])
            self.assertIn("Provider image_ref 续接", claim["notes"])
            for evidence in claim["evidence"]:
                self.assertTrue((ROOT / evidence).is_file(), evidence)

        route_features: dict[tuple[str, str, str], set[str]] = {}
        for claim in matrix["claims"]:
            if claim["target"]["kind"] != "provider" or claim["status"] not in {
                "conformant",
                "live-verified",
            }:
                continue
            target = claim["target"]
            route_features.setdefault(
                (
                    claim["implementation"],
                    target["id"],
                    target["apiFamily"],
                ),
                set(),
            ).add(target["feature"])
        self.assertFalse(
            any(
                {"tools", "image_input"}.issubset(features)
                for features in route_features.values()
            )
        )

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
