use std::io::Write;
use std::process::{Command, Stdio};

use serde_json::{Value, json};

/// 功能：运行真实 Rust CLI 创建一个由 faux provider 完成的 portable Session。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn create_session(workspace: &std::path::Path, state: &std::path::Path) {
    let output = Command::new(env!("CARGO_BIN_EXE_qxnm-forge"))
        .args([
            "run",
            "RPC lifecycle integration",
            "--workspace",
            workspace.to_str().expect("UTF-8 test workspace"),
            "--state-dir",
            state.to_str().expect("UTF-8 test state root"),
            "--session",
            "session-rpc-integration",
        ])
        .output()
        .expect("run command starts");
    assert!(
        output.status.success(),
        "run failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
}

/// 功能：把一组 JSON-RPC 请求发送给真实 stdio daemon 并严格解析全部响应帧。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn invoke_daemon(
    workspace: &std::path::Path,
    state: &std::path::Path,
    requests: &[Value],
) -> Vec<Value> {
    let mut child = Command::new(env!("CARGO_BIN_EXE_qxnm-forge"))
        .args([
            "daemon",
            "--workspace",
            workspace.to_str().expect("UTF-8 test workspace"),
            "--state-dir",
            state.to_str().expect("UTF-8 test state root"),
            "--conformance",
        ])
        .env("QXNM_FORGE_CONFORMANCE", "1")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("daemon starts");
    {
        let stdin = child.stdin.as_mut().expect("daemon stdin");
        for request in requests {
            serde_json::to_writer(&mut *stdin, request).expect("request serialization");
            stdin.write_all(b"\n").expect("request frame write");
        }
    }
    drop(child.stdin.take());
    let output = child.wait_with_output().expect("daemon exits after EOF");
    assert!(
        output.status.success(),
        "daemon failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    let stdout = String::from_utf8(output.stdout).expect("UTF-8 daemon stdout");
    stdout
        .lines()
        .map(|line| {
            assert!(
                line.len() <= 1_048_576,
                "response frame exceeds maxFrameBytes"
            );
            serde_json::from_str(line).expect("strict response JSON")
        })
        .collect()
}

/// 功能：验证真实 daemon 广告并执行列表、严格参数、归档、恢复和永久删除契约。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[test]
fn stdio_daemon_exposes_complete_session_lifecycle_contract() {
    let workspace = tempfile::tempdir().expect("workspace");
    let state = tempfile::tempdir().expect("state root");
    create_session(workspace.path(), state.path());
    let responses = invoke_daemon(
        workspace.path(),
        state.path(),
        &[
            json!({
                "jsonrpc":"2.0",
                "id":"init",
                "method":"initialize",
                "params":{
                    "protocolVersions":["0.1"],
                    "client":{"name":"rust-lifecycle-test","version":"0.1"},
                    "capabilities":{"interactiveApprovals":false,"terminalEvents":false}
                }
            }),
            json!({
                "jsonrpc":"2.0",
                "id":"configure-pending",
                "method":"faux/configure",
                "params":{
                    "sessionId":"session-pending-rpc",
                    "scenario":{
                        "schemaVersion":"0.1",
                        "name":"pending-lifecycle",
                        "seed":1,
                        "steps":[{"type":"text","text":"pending"}]
                    }
                }
            }),
            json!({"jsonrpc":"2.0","id":"pending-busy","method":"session/archive","params":{"sessionId":"session-pending-rpc"}}),
            json!({"jsonrpc":"2.0","id":"bad-list","method":"session/list","params":{"limit":0}}),
            json!({"jsonrpc":"2.0","id":"list","method":"session/list","params":{"limit":1}}),
            json!({"jsonrpc":"2.0","id":"bad-archive","method":"session/archive","params":{"sessionId":"session-rpc-integration","extra":true}}),
            json!({"jsonrpc":"2.0","id":"archive","method":"session/archive","params":{"sessionId":"session-rpc-integration"}}),
            json!({"jsonrpc":"2.0","id":"restore","method":"session/restore","params":{"sessionId":"session-rpc-integration"}}),
            json!({"jsonrpc":"2.0","id":"delete","method":"session/delete","params":{"sessionId":"session-rpc-integration"}}),
            json!({"jsonrpc":"2.0","id":"empty","method":"session/list","params":{}}),
        ],
    );
    assert_eq!(responses.len(), 10);
    let methods = responses[0]["result"]["capabilities"]["methods"]
        .as_array()
        .expect("initialize methods");
    for method in [
        "session/list",
        "session/archive",
        "session/restore",
        "session/delete",
    ] {
        assert!(methods.iter().any(|value| value == method));
    }
    assert_eq!(
        responses[1]["result"]["scenarioId"],
        "faux:pending-lifecycle"
    );
    assert_eq!(responses[2]["error"]["code"], -32004);
    assert_eq!(responses[2]["error"]["details"]["kind"], "session_busy");
    assert_eq!(responses[3]["error"]["code"], -32602);
    assert_eq!(responses[3]["error"]["details"]["field"], "limit");
    assert_eq!(responses[4]["result"]["hasMore"], true);
    assert_eq!(responses[4]["result"]["nextCursor"], "v1:1");
    assert_eq!(
        responses[4]["result"]["sessions"].as_array().map(Vec::len),
        Some(1)
    );
    assert_eq!(responses[5]["error"]["code"], -32602);
    assert_eq!(responses[6]["result"]["session"]["archived"], true);
    assert_eq!(responses[7]["result"]["session"]["archived"], false);
    assert_eq!(responses[8]["result"]["deleted"], true);
    assert!(responses[9]["result"]["nextCursor"].is_null());
    assert_eq!(
        responses[9]["result"]["sessions"].as_array().map(Vec::len),
        Some(1)
    );
}
