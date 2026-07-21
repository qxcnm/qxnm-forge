use serde::Serialize;

mod application_service_bridge;

use application_service_bridge::{
    ApplicationServiceBridge, application_service_initialize, application_service_request,
    provider_credential_remove, provider_credential_set,
};

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct RuntimeEnvironment {
    platform: &'static str,
    mode: &'static str,
    supports_local_daemon: bool,
}

/// 返回当前壳的平台能力，不暴露任意进程启动或宿主文件访问能力。
///
/// 输入：无。
/// 输出：稳定的展示投影；移动平台始终禁用本地 daemon。
/// 不变量：不得把 shell execute/spawn 权限直接授予 `WebView`。
/// 失败：本函数不执行 I/O，因此不会产生运行时失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[tauri::command]
fn runtime_environment() -> RuntimeEnvironment {
    let is_mobile = cfg!(any(target_os = "android", target_os = "ios"));

    RuntimeEnvironment {
        platform: std::env::consts::OS,
        mode: if is_mobile {
            "remote-service"
        } else {
            "desktop-local"
        },
        supports_local_daemon: !is_mobile,
    }
}

/// 启动共享的 Tauri 应用构建器。
///
/// 输入：由 Tauri 生成的编译期上下文。
/// 输出：应用事件循环，正常退出前持续运行。
/// 不变量：仅注册窄的只读平台能力命令。
/// 失败：平台 `WebView` 或运行时初始化失败时终止进程并给出固定错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
///
/// # Panics
///
/// 当平台 `WebView` 或 Tauri 事件循环无法初始化时触发 panic。
#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .manage(ApplicationServiceBridge::default())
        .invoke_handler(tauri::generate_handler![
            runtime_environment,
            application_service_initialize,
            application_service_request,
            provider_credential_set,
            provider_credential_remove
        ])
        .run(tauri::generate_context!())
        .expect("failed to run agent client shell");
}

#[cfg(test)]
mod tests {
    use super::runtime_environment;

    /// 验证桌面测试目标不会被错误识别为移动端。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn desktop_target_supports_local_daemon() {
        let environment = runtime_environment();

        assert!(environment.supports_local_daemon);
        assert_eq!(environment.mode, "desktop-local");
    }
}
