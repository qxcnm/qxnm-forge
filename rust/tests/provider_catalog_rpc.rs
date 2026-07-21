use std::collections::BTreeSet;
use std::io::Write;
use std::process::{Command, Stdio};

use serde_json::{Value, json};

/// 功能：把 Provider catalog RPC 请求发送给真实 Rust stdio daemon 并解析响应。
///
/// 输入：隔离 workspace/state 与按顺序发送的严格 JSON-RPC 请求。
/// 输出：daemon EOF 正常退出后按帧顺序返回 JSON 值。
/// 不变量：测试只使用本地冻结目录，不配置或调用任何付费 Provider。
/// 失败：进程、编码、协议或退出状态异常时使测试失败且不回显 credential。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn invoke_provider_catalog_daemon(
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
        ])
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
    String::from_utf8(output.stdout)
        .expect("UTF-8 daemon stdout")
        .lines()
        .map(|line| serde_json::from_str(line).expect("strict response JSON"))
        .collect()
}

/// 功能：验证 daemon 广告并执行只读 Provider 模板目录，同时严格拒绝额外参数。
///
/// 不变量：模板存在不等于 Provider 已配置；initialize 不得因此广告 DeepSeek 可执行 route。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[test]
fn stdio_daemon_exposes_strict_provider_catalog_contract() {
    let workspace = tempfile::tempdir().expect("workspace");
    let state = tempfile::tempdir().expect("state root");
    let responses = invoke_provider_catalog_daemon(
        workspace.path(),
        state.path(),
        &[
            json!({
                "jsonrpc":"2.0",
                "id":"init",
                "method":"initialize",
                "params":{
                    "protocolVersions":["0.1"],
                    "client":{"name":"rust-provider-catalog-test","version":"0.1"},
                    "capabilities":{"interactiveApprovals":false,"terminalEvents":false}
                }
            }),
            json!({"jsonrpc":"2.0","id":"catalog","method":"providerCatalog/list","params":{}}),
            json!({"jsonrpc":"2.0","id":"bad-catalog","method":"providerCatalog/list","params":{"configured":true}}),
        ],
    );
    assert_eq!(responses.len(), 3);
    let methods = responses[0]["result"]["capabilities"]["methods"]
        .as_array()
        .expect("initialize methods");
    assert!(
        methods
            .iter()
            .any(|method| method == "providerCatalog/list")
    );
    let providers = responses[0]["result"]["capabilities"]["providers"]
        .as_array()
        .expect("initialize providers");
    assert!(
        providers
            .iter()
            .all(|provider| provider["id"] != "deepseek")
    );

    let result = responses[1]["result"].as_object().expect("catalog result");
    assert_eq!(
        result.keys().map(String::as_str).collect::<BTreeSet<_>>(),
        BTreeSet::from(["templates"])
    );
    let templates = result["templates"].as_array().expect("catalog templates");
    assert_eq!(templates.len(), 20);
    assert_eq!(templates[0]["templateId"], "ant-ling");
    assert_eq!(templates[8]["templateId"], "nvidia");
    assert_eq!(templates[8]["displayName"], "NVIDIA NIM");
    assert_eq!(templates[19]["templateId"], "zai-coding-cn");
    let keys = templates[0]
        .as_object()
        .expect("template object")
        .keys()
        .map(String::as_str)
        .collect::<BTreeSet<_>>();
    assert_eq!(
        keys,
        BTreeSet::from([
            "apiFamily",
            "defaultBaseUrl",
            "displayName",
            "logoAssetId",
            "modelDiscovery",
            "suggestedProviderId",
            "templateId",
        ])
    );
    assert_eq!(responses[2]["error"]["code"], -32602);
}
