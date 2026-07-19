"""验证 Linux Bubblewrap hard-sandbox runner 的安全边界与失败关闭行为。

作者：高宏顺
邮箱：18272669457@163.com
"""

from __future__ import annotations

from copy import deepcopy
import json
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


CONFORMANCE = Path(__file__).resolve().parents[1]
ROOT = CONFORMANCE.parent
SCHEMAS = ROOT / "SPEC/schemas"
sys.path.insert(0, str(CONFORMANCE))

import hard_sandbox_runner  # noqa: E402


class HardSandboxRunnerTests(unittest.TestCase):
    """覆盖 hard-sandbox 静态契约、确定性探针与执行监督。"""

    def test_fixture_schema_rejects_missing_dynamic_evidence(self) -> None:
        """功能：确认 fixture 缺少真实网络/后代门禁字段时被封闭 Schema 拒绝。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        fixture = hard_sandbox_runner.strict_load(hard_sandbox_runner.FIXTURE)
        hard_sandbox_runner.validate_schema(fixture, SCHEMAS)
        hard_sandbox_runner.validate_semantics(fixture)
        invalid = deepcopy(fixture)
        del invalid["cases"][0]["expected"]["networkProbeExecuted"]
        with self.assertRaises(hard_sandbox_runner.HardSandboxError):
            hard_sandbox_runner.validate_schema(invalid, SCHEMAS)

    def test_capability_requires_bounds_cleanup_and_no_fallback(self) -> None:
        """功能：确认公共 capability 必须公开执行边界、setsid 清理和失败模式。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        try:
            import jsonschema
            from referencing import Registry, Resource
        except ImportError as exc:  # pragma: no cover - developer dependency gate
            self.skipTest(f"jsonschema/referencing unavailable: {exc}")
        resources: list[tuple[str, Resource[object]]] = []
        schemas: dict[str, dict[str, object]] = {}
        for path in sorted(SCHEMAS.rglob("*.schema.json")):
            document = json.loads(path.read_text(encoding="utf-8"))
            schemas[path.name] = document
            resources.append((document["$id"], Resource.from_contents(document)))
        schema = schemas["hard-sandbox.schema.json"]
        validator = jsonschema.Draft202012Validator(
            {"$ref": schema["$id"] + "#/$defs/capability"},
            registry=Registry().with_resources(resources),
        )
        capability = {
            "profile": "linux-bwrap-v1",
            "platform": "linux",
            "filesystem": "mount_namespace_allowlist",
            "network": "network_namespace_isolated",
            "process": "pid_namespace_die_with_parent",
            "credentials": "empty_environment",
            "execution": {
                "timeout": "bounded",
                "output": "bounded",
                "descendants": "fork_setsid_terminated_on_cancel_or_parent_exit",
            },
            "failureMode": "sandbox_unavailable_no_host_fallback",
            "selfTested": True,
        }
        validator.validate(capability)
        del capability["failureMode"]
        with self.assertRaises(jsonschema.ValidationError):
            validator.validate(capability)

    def test_daemon_contract_freezes_dual_gate_and_process_tool(self) -> None:
        """功能：确认动态 fixture 冻结 process.exec、固定生产 backend 与双 conformance 门。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        fixture = hard_sandbox_runner.strict_load(hard_sandbox_runner.FIXTURE)
        hard_sandbox_runner.validate_schema(fixture, SCHEMAS)
        daemon = hard_sandbox_runner.validate_daemon_semantics(fixture)
        self.assertEqual(daemon["tool"], "process.exec")
        self.assertEqual(daemon["productionBackend"], "/usr/bin/bwrap")
        self.assertEqual(
            daemon["conformanceGate"],
            {
                "cliArgument": "--conformance",
                "environment": {"name": "QXNM_FORGE_CONFORMANCE", "value": "1"},
            },
        )
        invalid = deepcopy(fixture)
        invalid["daemon"]["profileEnvironment"] = "UNSAFE_PROFILE"
        with self.assertRaises(hard_sandbox_runner.HardSandboxError):
            hard_sandbox_runner.validate_schema(invalid, SCHEMAS)

    def test_daemon_tool_arguments_reuse_optional_sandbox_member(self) -> None:
        """功能：确认动态案例不新增 RPC/工具名且 sandbox 参数不含宿主 workspace 路径。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        case = {
            "name": "daemon_read_only_isolation",
            "kind": "isolation",
            "workspaceAccess": "read_only",
        }
        arguments = hard_sandbox_runner.tool_arguments(
            case, "hard_sandbox_helper.py", listener_port=12345
        )
        self.assertEqual(arguments["executable"], "/usr/bin/python3")
        self.assertEqual(
            arguments["sandbox"],
            {
                "profile": "linux-bwrap-v1",
                "workspaceAccess": "read_only",
                "network": "isolated",
            },
        )
        self.assertNotIn("/tmp/", json.dumps(arguments))
        request = hard_sandbox_runner.faux_configure_request(
            1, "sandbox-session", case, arguments
        )
        tool_call = request["params"]["scenario"]["steps"][0]
        self.assertEqual(tool_call["name"], "process.exec")
        self.assertEqual(tool_call["arguments"], arguments)

    def test_production_command_removes_only_exact_conformance_gate(self) -> None:
        """功能：确认生产 argv 只删除精确 `--conformance`，不重写相似参数。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        command = ["daemon", "--conformance", "--conformance-mode", "value"]
        self.assertEqual(
            hard_sandbox_runner.production_daemon_command(command),
            ["daemon", "--conformance-mode", "value"],
        )

    def test_helper_marker_stays_in_current_workspace(self) -> None:
        """功能：确认固定 helper 的 fallback marker 动作只写临时 cwd 单一文件。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with tempfile.TemporaryDirectory(
            prefix="qxnm-forge-hard-sandbox-helper-test-"
        ) as temporary:
            root = Path(temporary)
            completed = subprocess.run(
                [
                    sys.executable,
                    str(hard_sandbox_runner.DAEMON_HELPER),
                    "marker",
                    "started.marker",
                ],
                cwd=root,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                timeout=2,
                check=False,
            )
            self.assertEqual(completed.returncode, 0, completed.stderr)
            self.assertEqual(
                (root / "started.marker").read_text(encoding="ascii"), "started"
            )

    def test_non_sandbox_backend_identity_is_not_self_test_evidence(self) -> None:
        """功能：确认 root-owned `/usr/bin/true` 只能作为 setup self-test 负例候选。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        candidate = hard_sandbox_runner.setup_failure_backend()
        hard_sandbox_runner.validate_backend(candidate)
        command = hard_sandbox_runner.bwrap_command(
            candidate, Path("/tmp/workspace"), "read_only", "exit 73"
        )
        self.assertEqual(command[0], str(candidate))
        self.assertIn("--unshare-net", command)

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux-only socket gate")
    def test_network_probe_rejects_reachable_listener_under_dash(self) -> None:
        """功能：证明 `/bin/sh` 为 dash 时外部 Python socket 探针仍真实执行并发现连接。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            listener.bind(("127.0.0.1", 0))
            listener.listen(1)
            command = hard_sandbox_runner.network_probe_command(
                listener.getsockname()[1]
            )
            self.assertNotIn("/dev/tcp", command)
            completed = subprocess.run(
                ["/bin/sh", "-c", command],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                timeout=2,
                check=False,
            )
        finally:
            listener.close()
        self.assertEqual(completed.returncode, 44, completed.stderr)

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux-only procfs gate")
    def test_bounded_output_is_capped_and_process_is_reaped(self) -> None:
        """功能：确认 supervisor 达到合并输出硬上限即终止并只保留固定字节数。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        limit = 16_384
        result = hard_sandbox_runner.run_bounded(
            [
                sys.executable,
                "-c",
                "import os,time;os.write(1,b'x'*65536);time.sleep(10)",
            ],
            env={"PATH": "/usr/bin:/bin"},
            timeout_seconds=2,
            max_output_bytes=limit,
        )
        self.assertTrue(result.output_limited)
        self.assertFalse(result.timed_out)
        self.assertEqual(len(result.stdout) + len(result.stderr), limit)
        self.assertFalse(
            any(
                hard_sandbox_runner.identity_is_alive(identity)
                for identity in result.observed
            )
        )

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux-only procfs gate")
    def test_timeout_cleans_fork_setsid_descendant(self) -> None:
        """功能：确认 timeout 路径跟踪并清理逃离原 process group 的 setsid 后代。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        code = (
            "import os,sys,time\n"
            "child=os.fork()\n"
            "if child == 0:\n"
            "    os.setsid()\n"
            "    with open(sys.argv[1],'w',encoding='ascii') as stream:\n"
            "        stream.write(str(os.getpid()))\n"
            "    while True: time.sleep(0.05)\n"
            "while True: time.sleep(0.05)\n"
        )
        with tempfile.TemporaryDirectory(
            prefix="qxnm-forge-hard-sandbox-test-"
        ) as temporary:
            marker = Path(temporary) / "setsid.ready"
            result = hard_sandbox_runner.run_bounded(
                [sys.executable, "-c", code, str(marker)],
                env={"PATH": "/usr/bin:/bin"},
                timeout_seconds=0.5,
                max_output_bytes=4_096,
            )
            descendant = hard_sandbox_runner.verify_setsid_identity(
                marker, result.observed
            )
        self.assertTrue(result.timed_out)
        self.assertFalse(result.output_limited)
        self.assertFalse(hard_sandbox_runner.identity_is_alive(descendant))

    def test_missing_backend_fails_before_any_execution(self) -> None:
        """功能：确认正常 profile 入口先拒绝缺失 backend，绝不调用执行或 host fallback。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        with tempfile.TemporaryDirectory(
            prefix="qxnm-forge-hard-sandbox-test-"
        ) as temporary:
            root = Path(temporary)
            with mock.patch.object(hard_sandbox_runner, "run_bounded") as execution:
                with self.assertRaisesRegex(
                    hard_sandbox_runner.HardSandboxError,
                    "sandbox_unavailable",
                ):
                    hard_sandbox_runner.run_profile_command(
                        root / "missing-bwrap",
                        root,
                        "read_write",
                        "touch /workspace/must-not-exist",
                    )
            execution.assert_not_called()
            self.assertFalse((root / "must-not-exist").exists())

    def test_bwrap_argv_has_no_host_root_or_fallback_command(self) -> None:
        """功能：确认固定 argv 启用全部 namespace 且不 bind 宿主根或附带兜底命令。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        command = hard_sandbox_runner.bwrap_command(
            Path("/usr/bin/bwrap"), Path("/tmp/workspace"), "read_only", "true"
        )
        self.assertEqual(command[0], "/usr/bin/bwrap")
        for option in (
            "--unshare-user",
            "--unshare-pid",
            "--unshare-net",
            "--die-with-parent",
            "--new-session",
        ):
            self.assertIn(option, command)
        self.assertNotIn(
            ["--bind", "/", "/"],
            [command[index : index + 3] for index in range(len(command) - 2)],
        )
        self.assertNotIn("fallback", command)


if __name__ == "__main__":
    unittest.main()
