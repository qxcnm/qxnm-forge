use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::Arc;

use clap::{Parser, Subcommand, ValueEnum};
use qxnm_forge::agent::{Agent, RunRequest};
use qxnm_forge::agent_profile::AgentProfileService;
use qxnm_forge::commercial_state::{InstalledSponsoredRouteStore, ProviderCredentialStore};
use qxnm_forge::daemon::Daemon;
use qxnm_forge::domain::EventEnvelope;
use qxnm_forge::error::{AgentError, ErrorCode};
use qxnm_forge::hard_sandbox::HardSandboxState;
use qxnm_forge::policy::{ToolPolicy, WorkspaceGuard};
use qxnm_forge::provider::{
    AnthropicMessagesProvider, AzureOpenAiResponsesProvider, BedrockConverseStreamProvider,
    FauxProvider, GoogleGenerativeAiProvider, GoogleVertexProvider, MistralConversationsProvider,
    OpenAiChatProvider, OpenAiCodexResponsesProvider, OpenAiResponsesProvider,
    OpenRouterImagesProvider, Provider,
};
use qxnm_forge::provider_identity::ProviderIdentityAdvertisement;
use qxnm_forge::provider_route::ProviderRouteSnapshot;
use qxnm_forge::session::SessionStore;
use qxnm_forge::session::pi_v3_import::{PiV3ImportOptions, import_pi_v3};
use qxnm_forge::sponsored_catalog::{
    SponsoredCatalogService, generate_catalog_keypair, sign_catalog_file, verify_catalog_file,
};
use qxnm_forge::storage::{DatabaseConfiguration, connect_application_database};
use qxnm_forge::tools::ToolRegistry;
use tokio::sync::mpsc;
use uuid::Uuid;

#[derive(Debug, Parser)]
#[command(name = "qxnm-forge", author = "高宏顺 <18272669457@163.com>")]
#[command(about = "qxnm-forge 独立 Rust Agent 底座")]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Debug, Subcommand)]
enum Command {
    /// 通过 stdin/stdout 运行 UTF-8 NDJSON JSON-RPC daemon。
    #[command(alias = "rpc")]
    Daemon {
        #[arg(long, default_value = ".")]
        workspace: PathBuf,
        #[arg(long)]
        state_dir: Option<PathBuf>,
        #[arg(long)]
        conformance: bool,
    },
    /// 使用确定性 faux provider 运行一次纯文本 Agent。
    Run {
        prompt: String,
        #[arg(long, default_value = ".")]
        workspace: PathBuf,
        #[arg(long)]
        state_dir: Option<PathBuf>,
        #[arg(long, default_value = "faux")]
        provider: String,
        #[arg(long, default_value = "faux-v1")]
        model: String,
        #[arg(long)]
        api_family: Option<String>,
        #[arg(long)]
        session: Option<String>,
    },
    /// 管理 portable Session，包括一次性 PI v3 clean-room 导入。
    Session {
        #[command(subcommand)]
        command: SessionCommand,
    },
    /// 配置、发布和查看签名远程推广 Provider 目录。
    Sponsors {
        #[command(subcommand)]
        command: SponsorsCommand,
    },
    /// 管理工作区外的 Provider API key；secret 只从 stdin 读取。
    Auth {
        #[command(subcommand)]
        command: AuthCommand,
    },
}

#[derive(Debug, Subcommand)]
enum SessionCommand {
    /// 把一个只读 PI Session v3 JSONL 导入为新的 portable Session。
    ImportPiV3 {
        #[arg(long)]
        source: PathBuf,
        #[arg(long)]
        workspace: PathBuf,
        #[arg(long)]
        state_dir: PathBuf,
        #[arg(long)]
        session: Option<String>,
        #[arg(long, value_enum, default_value_t = OutputFormat::Text)]
        format: OutputFormat,
        #[arg(long)]
        conformance: bool,
    },
}

#[derive(Debug, Subcommand)]
enum SponsorsCommand {
    /// 离线生成 ECDSA P-256 目录签名密钥；私钥绝不打印。
    Keygen {
        #[arg(long)]
        key_id: String,
        #[arg(long)]
        private_key: PathBuf,
        #[arg(long)]
        public_key: PathBuf,
    },
    /// 使用离线私钥签署 catalog JSON，生成可静态托管的 envelope。
    Sign {
        #[arg(long)]
        catalog: PathBuf,
        #[arg(long)]
        private_key: PathBuf,
        #[arg(long)]
        public_key: PathBuf,
        #[arg(long)]
        output: PathBuf,
    },
    /// 在上传前离线验证 envelope、签名、时间和推广字段。
    Verify {
        #[arg(long)]
        envelope: PathBuf,
        #[arg(long)]
        public_key: PathBuf,
        #[arg(long, value_enum, default_value_t = OutputFormat::Text)]
        format: OutputFormat,
    },
    /// 显式安装一个 HTTPS 目录 URL 及固定公开验签密钥。
    Configure {
        #[arg(long)]
        catalog_url: String,
        #[arg(long)]
        public_key: PathBuf,
        #[arg(long)]
        state_dir: Option<PathBuf>,
    },
    /// 刷新并列出明确标注返佣关系的推广 Provider。
    List {
        #[arg(long)]
        state_dir: Option<PathBuf>,
        #[arg(long)]
        offline: bool,
        #[arg(long, value_enum, default_value_t = OutputFormat::Text)]
        format: OutputFormat,
    },
    /// 明确接受推广披露后，把一个 catalog 条目固定为本地可执行 route。
    Use {
        entry_id: String,
        #[arg(long)]
        model: String,
        #[arg(long)]
        accept_disclosure: bool,
        #[arg(long)]
        offline: bool,
        #[arg(long)]
        state_dir: Option<PathBuf>,
    },
    /// 列出不会被远程目录静默修改的本地 route 快照。
    Installed {
        #[arg(long)]
        state_dir: Option<PathBuf>,
        #[arg(long, value_enum, default_value_t = OutputFormat::Text)]
        format: OutputFormat,
    },
    /// 按远程 entry ID 移除一个本地 route；credential 独立保留。
    Remove {
        entry_id: String,
        #[arg(long)]
        state_dir: Option<PathBuf>,
    },
}

#[derive(Debug, Subcommand)]
enum AuthCommand {
    /// 从 stdin 安全读取并保存或轮换一个 Provider API key。
    Set {
        #[arg(long)]
        provider: String,
        #[arg(long, default_value = ".")]
        workspace: PathBuf,
        #[arg(long)]
        state_dir: Option<PathBuf>,
    },
    /// 只列出拥有 stored credential 的 Provider ID，不输出 key。
    List {
        #[arg(long, default_value = ".")]
        workspace: PathBuf,
        #[arg(long)]
        state_dir: Option<PathBuf>,
        #[arg(long, value_enum, default_value_t = OutputFormat::Text)]
        format: OutputFormat,
    },
    /// 删除一个 stored credential，不影响已安装 route。
    Remove {
        #[arg(long)]
        provider: String,
        #[arg(long, default_value = ".")]
        workspace: PathBuf,
        #[arg(long)]
        state_dir: Option<PathBuf>,
    },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, ValueEnum)]
enum OutputFormat {
    Text,
    Json,
}

/// 功能：解析命令行、运行 qxnm-forge，并将结构化错误安全写入 stderr。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[tokio::main]
async fn main() {
    let conformance = env_nonempty("QXNM_FORGE_CONFORMANCE").as_deref() == Some("1");
    let provider_conformance =
        env_nonempty("QXNM_FORGE_PROVIDER_CONFORMANCE").as_deref() == Some("1");
    let provider_route = ProviderRouteSnapshot::from_environment(conformance, provider_conformance);
    let result = match provider_route {
        Ok(Some(snapshot)) => run(Cli::parse(), None, Some(snapshot)).await,
        Ok(None) => match ProviderIdentityAdvertisement::from_environment(conformance) {
            Ok(Some(advertisement)) => run(Cli::parse(), Some(advertisement), None).await,
            Ok(None) => match ProviderRouteSnapshot::production() {
                Ok(snapshot) => run(Cli::parse(), None, Some(snapshot)).await,
                Err(error) => Err(error),
            },
            Err(error) => Err(error),
        },
        Err(error) => Err(error),
    };
    if let Err(error) = result {
        eprintln!("qxnm-forge: {:?}: {}", error.code, error.message);
        std::process::exit(exit_code(error.code));
    }
}

/// 功能：在启动期 presence 已处理后分派纯文本 CLI 或 stdio RPC daemon 子命令。
///
/// 输入：严格 clap 命令，以及在 CLI 解析前生成且互斥的 identity-only 或 executable route 快照。
/// 输出：所选命令完成时成功；CLI 模式仅打印文本 delta，RPC stdout 只输出协议帧。
/// 不变量：本函数不再读取 presence 路径；identity 广告不能注册 adapter，route 广告与执行同源。
/// 失败：命令配置、Provider、Session、工具或协议失败时返回结构化错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn run(
    cli: Cli,
    provider_identity: Option<ProviderIdentityAdvertisement>,
    provider_route: Option<ProviderRouteSnapshot>,
) -> Result<(), AgentError> {
    match cli.command {
        Command::Daemon {
            workspace,
            state_dir,
            conformance,
        } => {
            let state_dir = std::path::absolute(resolve_state_dir(state_dir))?;
            let (agent, faux) = build_agent(
                &workspace,
                &state_dir,
                provider_identity.is_some(),
                provider_route.as_ref(),
                conformance,
            )
            .await?;
            let database =
                connect_application_database(&DatabaseConfiguration::sqlite_default(&state_dir))
                    .await
                    .map_err(|_| {
                        AgentError::new(
                            ErrorCode::InternalError,
                            "application database initialization failed",
                        )
                    })?;
            let agent_profiles = AgentProfileService::new(database);
            Daemon::new(
                agent,
                faux,
                agent_profiles,
                provider_identity,
                provider_route,
                &workspace,
            )?
            .run_stdio()
            .await
        }
        Command::Run {
            prompt,
            workspace,
            state_dir,
            provider,
            model,
            api_family,
            session,
        } => {
            let state_dir = resolve_state_dir(state_dir);
            let (agent, _faux) = build_agent(
                &workspace,
                &state_dir,
                provider_identity.is_some(),
                provider_route.as_ref(),
                false,
            )
            .await?;
            let api_family = if provider != "faux" {
                if let Some(snapshot) = &provider_route {
                    let canonical = snapshot.resolve(&provider, &model, api_family.as_deref())?;
                    if canonical.is_some() {
                        canonical
                    } else if api_family.is_some() {
                        api_family
                    } else {
                        InstalledSponsoredRouteStore::new(&state_dir)
                            .resolve_family(&provider, &model)?
                    }
                } else {
                    api_family.or(InstalledSponsoredRouteStore::new(&state_dir)
                        .resolve_family(&provider, &model)?)
                }
            } else {
                api_family
            };
            let (sender, mut receiver) = mpsc::channel::<EventEnvelope>(256);
            let request = RunRequest {
                session_id: session.unwrap_or_else(|| format!("session-{}", Uuid::new_v4())),
                run_id: format!("run-{}", Uuid::new_v4()),
                prompt,
                provider_id: provider,
                api_family,
                model,
                interactive_approvals: false,
            };
            agent.accept_run(&request).await?;
            agent.start_run(request, sender).await;
            while let Some(event) = receiver.recv().await {
                match event.event_type.as_str() {
                    "message.delta" if event.data.pointer("/delta/type") == Some(&json_text()) => {
                        if let Some(delta) =
                            event.data.pointer("/delta/text").and_then(|v| v.as_str())
                        {
                            print!("{delta}");
                        }
                    }
                    "run.completed" => {
                        println!();
                        return Ok(());
                    }
                    "run.failed" | "run.cancelled" | "run.interrupted" => {
                        return Err(AgentError::new(
                            ErrorCode::ProviderError,
                            event.data.to_string(),
                        ));
                    }
                    _ => {}
                }
            }
            Err(AgentError::new(
                ErrorCode::StreamInterrupted,
                "agent event stream closed before terminal event",
            ))
        }
        Command::Session {
            command:
                SessionCommand::ImportPiV3 {
                    source,
                    workspace,
                    state_dir,
                    session,
                    format,
                    conformance,
                },
        } => {
            let outcome = import_pi_v3(PiV3ImportOptions {
                source,
                workspace,
                state_root: state_dir,
                session_id: session,
                conformance,
            })
            .await?;
            match format {
                OutputFormat::Json => println!(
                    "{}",
                    serde_json::to_string(&outcome).map_err(|_| AgentError::new(
                        ErrorCode::InternalError,
                        "PI v3 import result serialization failed"
                    ))?
                ),
                OutputFormat::Text => {
                    println!("status: {}", outcome.status);
                    println!("sessionId: {}", outcome.session_id);
                    println!("reportArtifactId: {}", outcome.report_artifact_id);
                }
            }
            Ok(())
        }
        Command::Sponsors { command } => match command {
            SponsorsCommand::Keygen {
                key_id,
                private_key,
                public_key,
            } => {
                generate_catalog_keypair(&key_id, &private_key, &public_key)?;
                println!("推广目录签名密钥已创建；请离线保管私钥且只分发公钥。");
                Ok(())
            }
            SponsorsCommand::Sign {
                catalog,
                private_key,
                public_key,
                output,
            } => {
                sign_catalog_file(&catalog, &private_key, &public_key, &output)?;
                println!("推广目录 envelope 已签名，可上传到已配置的 HTTPS 地址。");
                Ok(())
            }
            SponsorsCommand::Verify {
                envelope,
                public_key,
                format,
            } => {
                let catalog = verify_catalog_file(&envelope, &public_key)?;
                match format {
                    OutputFormat::Json => println!(
                        "{}",
                        serde_json::to_string(&catalog).map_err(|_| AgentError::new(
                            ErrorCode::InternalError,
                            "sponsored catalog output serialization failed"
                        ))?
                    ),
                    OutputFormat::Text => println!(
                        "推广目录验证通过：version={} entries={}",
                        catalog.catalog_version,
                        catalog.entries.len()
                    ),
                }
                Ok(())
            }
            SponsorsCommand::Configure {
                catalog_url,
                public_key,
                state_dir,
            } => {
                let service = SponsoredCatalogService::new(resolve_state_dir(state_dir))?;
                service.configure(&catalog_url, &public_key)?;
                println!("推广目录来源已配置；远端不能更换本地验签公钥。");
                Ok(())
            }
            SponsorsCommand::List {
                state_dir,
                offline,
                format,
            } => {
                let service = SponsoredCatalogService::new(resolve_state_dir(state_dir))?;
                let loaded = service.load(offline).await?;
                if let Some(warning) = &loaded.warning {
                    eprintln!("警告：{warning}");
                }
                match format {
                    OutputFormat::Json => println!(
                        "{}",
                        serde_json::to_string(&loaded).map_err(|_| AgentError::new(
                            ErrorCode::InternalError,
                            "sponsored catalog output serialization failed"
                        ))?
                    ),
                    OutputFormat::Text => {
                        let Some(catalog) = loaded.catalog else {
                            println!("尚未配置推广 Provider 目录。");
                            return Ok(());
                        };
                        if catalog.entries.is_empty() {
                            println!("当前没有推广 Provider。");
                        }
                        for entry in catalog.entries {
                            println!("[推广] {} ({})", entry.display_name, entry.api_family);
                            println!("  {}", entry.description);
                            println!("  API: {}", entry.api_base_url);
                            println!("  注册: {}", entry.signup_url);
                            println!("  披露: {}", entry.commission_disclosure);
                        }
                    }
                }
                Ok(())
            }
            SponsorsCommand::Use {
                entry_id,
                model,
                accept_disclosure,
                offline,
                state_dir,
            } => {
                if !accept_disclosure {
                    return Err(AgentError::new(
                        ErrorCode::InvalidParams,
                        "安装推广 Provider 必须显式提供 --accept-disclosure",
                    ));
                }
                let state_dir = resolve_state_dir(state_dir);
                let service = SponsoredCatalogService::new(&state_dir)?;
                let loaded = service.load(offline).await?;
                let catalog = loaded.catalog.ok_or_else(|| {
                    AgentError::new(ErrorCode::InvalidRequest, "尚未配置推广 Provider 目录")
                })?;
                let entry = catalog
                    .entries
                    .iter()
                    .find(|entry| entry.id == entry_id)
                    .ok_or_else(|| {
                        AgentError::new(ErrorCode::InvalidParams, "推广目录中没有该条目")
                    })?;
                println!("[推广] {} ({})", entry.display_name, entry.api_family);
                println!("披露: {}", entry.commission_disclosure);
                let route = InstalledSponsoredRouteStore::new(&state_dir)
                    .install(&catalog, &entry_id, &model)?;
                println!(
                    "已安装 Provider：{}；请继续执行 auth set。",
                    route.provider_id
                );
                Ok(())
            }
            SponsorsCommand::Installed { state_dir, format } => {
                let routes =
                    InstalledSponsoredRouteStore::new(resolve_state_dir(state_dir)).list()?;
                match format {
                    OutputFormat::Json => println!(
                        "{}",
                        serde_json::to_string(&serde_json::json!({
                            "schemaVersion":"0.1",
                            "routes":routes
                        }))
                        .map_err(|_| AgentError::new(
                            ErrorCode::InternalError,
                            "installed sponsored routes output serialization failed"
                        ))?
                    ),
                    OutputFormat::Text => {
                        if routes.is_empty() {
                            println!("当前没有已安装的推广 Provider。");
                        }
                        for route in routes {
                            println!("[推广] {} => {}", route.display_name, route.provider_id);
                            println!("  family: {}", route.api_family);
                            println!("  models: {}", route.models.join(", "));
                            println!("  披露: {}", route.commission_disclosure);
                        }
                    }
                }
                Ok(())
            }
            SponsorsCommand::Remove {
                entry_id,
                state_dir,
            } => {
                let removed = InstalledSponsoredRouteStore::new(resolve_state_dir(state_dir))
                    .remove(&entry_id)?;
                println!(
                    "{}",
                    if removed {
                        "已移除本地推广 Provider route。"
                    } else {
                        "本地没有该推广 Provider route。"
                    }
                );
                Ok(())
            }
        },
        Command::Auth { command } => match command {
            AuthCommand::Set {
                provider,
                workspace,
                state_dir,
            } => {
                let store = ProviderCredentialStore::new(resolve_state_dir(state_dir), &workspace)?;
                let secret = read_secret_from_stdin()?;
                store.set(&provider, &secret)?;
                drop(secret);
                println!("Provider credential 已保存；不会写入 workspace 或 Session。");
                Ok(())
            }
            AuthCommand::List {
                workspace,
                state_dir,
                format,
            } => {
                let ids = ProviderCredentialStore::new(resolve_state_dir(state_dir), &workspace)?
                    .list()?;
                match format {
                    OutputFormat::Json => println!(
                        "{}",
                        serde_json::to_string(&serde_json::json!({"providers":ids})).map_err(
                            |_| AgentError::new(
                                ErrorCode::InternalError,
                                "credential status serialization failed"
                            )
                        )?
                    ),
                    OutputFormat::Text => {
                        if ids.is_empty() {
                            println!("当前没有 stored Provider credential。");
                        }
                        for id in ids {
                            println!("{id}: configured");
                        }
                    }
                }
                Ok(())
            }
            AuthCommand::Remove {
                provider,
                workspace,
                state_dir,
            } => {
                let removed =
                    ProviderCredentialStore::new(resolve_state_dir(state_dir), &workspace)?
                        .remove(&provider)?;
                println!(
                    "{}",
                    if removed {
                        "Provider credential 已移除。"
                    } else {
                        "Provider credential 不存在。"
                    }
                );
                Ok(())
            }
        },
    }
}

/// 功能：从非交互 stdin 有界读取单个 API key，避免 secret 出现在 argv 或终端回显。
///
/// 输入：重定向的标准输入，允许一个结尾 LF 或 CRLF。
/// 输出：长度 1..16384 的 key 字符串。
/// 不变量：TTY 输入直接拒绝；错误不包含任何输入字节。
/// 失败：stdin 未重定向、非 UTF-8、空值、多行、NUL 或超限时返回 InvalidParams。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn read_secret_from_stdin() -> Result<String, AgentError> {
    use std::io::{IsTerminal as _, Read as _};

    let stdin = std::io::stdin();
    if stdin.is_terminal() {
        return Err(AgentError::new(
            ErrorCode::InvalidParams,
            "auth set 必须从重定向 stdin 读取 credential，以避免终端回显",
        ));
    }
    let mut bytes = Vec::new();
    stdin
        .take(16_387)
        .read_to_end(&mut bytes)
        .map_err(|_| AgentError::new(ErrorCode::IoError, "credential stdin 读取失败"))?;
    if bytes.len() > 16_386 {
        return Err(AgentError::new(
            ErrorCode::InvalidParams,
            "credential 输入无效",
        ));
    }
    if bytes.ends_with(b"\r\n") {
        bytes.truncate(bytes.len() - 2);
    } else if bytes.ends_with(b"\n") {
        bytes.truncate(bytes.len() - 1);
    }
    let secret = String::from_utf8(bytes)
        .map_err(|_| AgentError::new(ErrorCode::InvalidParams, "credential 输入无效"))?;
    if secret.is_empty()
        || secret.len() > 16_384
        || secret.contains('\r')
        || secret.contains('\n')
        || secret.contains('\0')
    {
        return Err(AgentError::new(
            ErrorCode::InvalidParams,
            "credential 输入无效",
        ));
    }
    Ok(secret)
}

/// 功能：按显式 CLI、conformance Session 根、内置默认值顺序选择状态目录。
///
/// 输入：可选显式 `--state-dir` 和可信进程环境。
/// 输出：显式目录优先，其次 QXNM_FORGE_SESSION_ROOT，最终为工作区外的平台用户级状态目录。
/// 不变量：默认值不依赖当前工作目录，避免 Session 被工作区工具直接访问。
/// 失败：本方法不访问文件系统且不返回错误；目录创建失败由 SessionStore 初始化报告。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn resolve_state_dir(explicit: Option<PathBuf>) -> PathBuf {
    explicit
        .or_else(|| env_nonempty("QXNM_FORGE_SESSION_ROOT").map(PathBuf::from))
        .unwrap_or_else(default_state_dir)
}

/// 功能：选择默认位于工作区之外的用户级状态目录，避免 Agent 文件工具触达 Session。
///
/// 输入：可信进程环境中的平台状态目录变量。
/// 输出：Windows 优先 LOCALAPPDATA，其他平台优先 XDG_STATE_HOME/HOME，最后使用系统临时目录。
/// 不变量：不根据当前工作目录构造默认值；显式 CLI 和 QXNM_FORGE_SESSION_ROOT 的优先级由调用方处理。
/// 失败：本方法不返回错误；缺少平台目录时退化到系统临时目录下的独立子目录。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn default_state_dir() -> PathBuf {
    #[cfg(windows)]
    if let Some(local_app_data) = env_nonempty("LOCALAPPDATA") {
        return PathBuf::from(local_app_data)
            .join("qxnm-forge")
            .join("state");
    }
    if let Some(xdg_state_home) = env_nonempty("XDG_STATE_HOME") {
        return PathBuf::from(xdg_state_home).join("qxnm-forge");
    }
    if let Some(home) = env_nonempty("HOME") {
        return PathBuf::from(home)
            .join(".local")
            .join("state")
            .join("qxnm-forge");
    }
    std::env::temp_dir().join("qxnm-forge-state")
}

/// 功能：返回用于比较 JSON 文本类型的静态值，避免 CLI 解析非文本 delta。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn json_text() -> serde_json::Value {
    serde_json::Value::String("text".to_owned())
}

/// 功能：构造完全原生的 Rust Agent 依赖图及各批 Provider。
///
/// 输入：workspace、Session state 根、identity-only 分支标记、可选 executable route 快照和 CLI conformance 门。
/// 输出：共享 Agent 与 faux Provider。
/// 不变量：identity-only 分支只注册 faux；canonical adapter 只保存 credential 环境名称；hard-sandbox override 同时要求 CLI 与环境 conformance 门；
/// route model allowlist 与协议广告来自同一快照；credential 值不进入日志或 Session。
/// 失败：workspace、Session、工具或普通 Provider 配置初始化失败时返回结构化错误；sandbox 自检失败保留到 initialize 失败关闭。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn build_agent(
    workspace: &PathBuf,
    state_dir: &PathBuf,
    identity_only: bool,
    provider_route: Option<&ProviderRouteSnapshot>,
    cli_conformance: bool,
) -> Result<(Arc<Agent>, FauxProvider), AgentError> {
    let environment_conformance = env_nonempty("QXNM_FORGE_CONFORMANCE").as_deref() == Some("1");
    let guard =
        WorkspaceGuard::new_with_conformance(workspace, cli_conformance, environment_conformance)?;
    let sessions = SessionStore::new(state_dir, 1024 * 1024).await?;
    let hard_sandbox =
        HardSandboxState::from_environment(guard.root(), cli_conformance, environment_conformance)
            .await;
    let tools = Arc::new(ToolRegistry::new_with_hard_sandbox(
        guard.clone(),
        ToolPolicy::headless_default(),
        sessions.clone(),
        hard_sandbox,
    ));
    let faux = FauxProvider::new();
    let mut providers = BTreeMap::<String, Arc<dyn Provider>>::new();
    providers.insert("faux".to_owned(), Arc::new(faux.clone()));
    if identity_only {
        return Ok((
            Arc::new(Agent::new_with_conformance_timeout(
                providers,
                tools,
                sessions,
                guard.root().to_path_buf(),
                cli_conformance,
                environment_conformance,
            )),
            faux,
        ));
    }
    if let Some(snapshot) = provider_route {
        providers.extend(snapshot.build_providers(&sessions)?);
    }
    let credential_store = ProviderCredentialStore::new(state_dir, guard.root())?;
    providers
        .extend(InstalledSponsoredRouteStore::new(state_dir).build_providers(&credential_store)?);
    let provider_conformance =
        env_nonempty("QXNM_FORGE_PROVIDER_CONFORMANCE").as_deref() == Some("1");
    let openai_credential_present = env_present("OPENAI_API_KEY");
    let compatible_endpoint = provider_conformance
        .then(|| env_nonempty("QXNM_FORGE_OPENAI_COMPATIBLE_ENDPOINT"))
        .flatten();
    if openai_credential_present && let Some(endpoint) = compatible_endpoint {
        providers.insert(
            "openai-compatible".to_owned(),
            Arc::new(OpenAiChatProvider::new(
                "openai-compatible",
                endpoint,
                Some("OPENAI_API_KEY".to_owned()),
            )),
        );
    }
    let responses_endpoint = provider_conformance
        .then(|| env_nonempty("QXNM_FORGE_OPENAI_RESPONSES_ENDPOINT"))
        .flatten();
    if openai_credential_present && let Some(endpoint) = responses_endpoint {
        providers.insert(
            "openai-responses".to_owned(),
            Arc::new(OpenAiResponsesProvider::new(
                "openai-responses",
                endpoint,
                Some("OPENAI_API_KEY".to_owned()),
            )),
        );
    }
    let anthropic_endpoint = provider_conformance
        .then(|| env_nonempty("QXNM_FORGE_ANTHROPIC_ENDPOINT"))
        .flatten();
    if env_present("ANTHROPIC_API_KEY")
        && let Some(endpoint) = anthropic_endpoint
    {
        providers.insert(
            "anthropic".to_owned(),
            Arc::new(AnthropicMessagesProvider::new(
                "anthropic",
                endpoint,
                Some("ANTHROPIC_API_KEY".to_owned()),
            )),
        );
    }
    let mistral_endpoint = provider_conformance
        .then(|| env_nonempty("QXNM_FORGE_MISTRAL_CONVERSATIONS_ENDPOINT"))
        .flatten();
    if env_present("MISTRAL_API_KEY")
        && let Some(endpoint) = mistral_endpoint
    {
        providers.insert(
            "legacy-mistral".to_owned(),
            Arc::new(MistralConversationsProvider::new(
                "mistral",
                endpoint,
                Some("MISTRAL_API_KEY".to_owned()),
            )),
        );
    }
    register_legacy_azure_provider(
        &mut providers,
        provider_conformance,
        || env_nonempty("QXNM_FORGE_AZURE_OPENAI_RESPONSES_ENDPOINT"),
        || env_nonempty("AZURE_OPENAI_API_KEY"),
    );
    let google_endpoint = provider_conformance
        .then(|| env_nonempty("QXNM_FORGE_GOOGLE_GENERATIVE_AI_ENDPOINT"))
        .flatten();
    if env_present("GEMINI_API_KEY")
        && let Some(endpoint) = google_endpoint
    {
        providers.insert(
            "legacy-google".to_owned(),
            Arc::new(GoogleGenerativeAiProvider::new(
                "google",
                endpoint,
                Some("GEMINI_API_KEY".to_owned()),
            )),
        );
    }
    let vertex_endpoint = provider_conformance
        .then(|| env_nonempty("QXNM_FORGE_GOOGLE_VERTEX_ENDPOINT"))
        .flatten();
    let vertex_token = provider_conformance
        .then(|| env_nonempty("QXNM_FORGE_GOOGLE_VERTEX_OAUTH_TOKEN"))
        .flatten();
    if let (Some(endpoint), Some(token), Some(project), Some(location)) = (
        vertex_endpoint,
        vertex_token,
        env_nonempty("GOOGLE_CLOUD_PROJECT").or_else(|| env_nonempty("GCLOUD_PROJECT")),
        env_nonempty("GOOGLE_CLOUD_LOCATION"),
    ) {
        providers.insert(
            "google-vertex".to_owned(),
            Arc::new(GoogleVertexProvider::new(
                "google-vertex",
                endpoint,
                Some(token),
                project,
                location,
            )),
        );
    }
    let bedrock_endpoint = provider_conformance
        .then(|| env_nonempty("QXNM_FORGE_BEDROCK_CONVERSE_STREAM_ENDPOINT"))
        .flatten();
    if let (Some(endpoint), Some(region)) = (
        bedrock_endpoint,
        env_nonempty("AWS_REGION").or_else(|| env_nonempty("AWS_DEFAULT_REGION")),
    ) {
        providers.insert(
            "amazon-bedrock".to_owned(),
            Arc::new(BedrockConverseStreamProvider::new(
                "amazon-bedrock",
                endpoint,
                region,
            )),
        );
    }
    let codex_endpoint = provider_conformance
        .then(|| env_nonempty("QXNM_FORGE_OPENAI_CODEX_RESPONSES_ENDPOINT"))
        .flatten();
    let codex_token = provider_conformance
        .then(|| env_nonempty("QXNM_FORGE_OPENAI_CODEX_OAUTH_TOKEN"))
        .flatten();
    if let (Some(endpoint), Some(token)) = (codex_endpoint, codex_token) {
        providers.insert(
            "openai-codex".to_owned(),
            Arc::new(OpenAiCodexResponsesProvider::new(
                "openai-codex",
                endpoint,
                Some(token),
            )),
        );
    }
    let openrouter_credential_present = env_present("OPENROUTER_API_KEY");
    let openrouter_endpoint = provider_conformance
        .then(|| env_nonempty("QXNM_FORGE_OPENROUTER_IMAGES_ENDPOINT"))
        .flatten();
    if openrouter_credential_present && let Some(endpoint) = openrouter_endpoint {
        providers.insert(
            "legacy-openrouter".to_owned(),
            Arc::new(OpenRouterImagesProvider::new(endpoint, sessions.clone())),
        );
    }
    Ok((
        Arc::new(Agent::new_with_conformance_timeout(
            providers,
            tools,
            sessions,
            guard.root().to_path_buf(),
            cli_conformance,
            environment_conformance,
        )),
        faux,
    ))
}

/// 功能：仅在旧 Provider family conformance 中惰性注册 Azure Responses mock adapter。
///
/// 输入：运行时 registry、显式 Provider conformance 门，以及延迟读取 endpoint/credential 的闭包。
/// 输出：双值均存在时新增一个 legacy adapter；否则 registry 保持原样。
/// 不变量：门关闭时两个 loader 均不得执行，普通生产因此不读取、复制或长期保存 Azure
/// credential，也不会通过 `AZURE_OPENAI_BASE_URL` 创建 allowlist 外 route；版本固定为 mock `v1`。
/// 失败：本函数不发起网络且不返回错误；adapter 的 endpoint/transport 错误在请求边界脱敏报告。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn register_legacy_azure_provider(
    providers: &mut BTreeMap<String, Arc<dyn Provider>>,
    provider_conformance: bool,
    endpoint_loader: impl FnOnce() -> Option<String>,
    credential_loader: impl FnOnce() -> Option<String>,
) {
    if !provider_conformance {
        return;
    }
    let Some(endpoint) = endpoint_loader() else {
        return;
    };
    let Some(credential) = credential_loader() else {
        return;
    };
    providers.insert(
        "azure-openai-responses".to_owned(),
        Arc::new(AzureOpenAiResponsesProvider::new(
            "azure-openai-responses",
            endpoint,
            "v1",
            Some(credential),
        )),
    );
}

/// 功能：读取并仅保留非空环境变量，用于判断 Provider 是否真实配置。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn env_nonempty(name: &str) -> Option<String> {
    std::env::var(name).ok().filter(|value| !value.is_empty())
}

/// 功能：只判断固定环境变量的 opaque OS 值是否存在且非空。
///
/// 输入：代码或 manifest 固定的环境变量名称。
/// 输出：存在至少一个字节时为 true。
/// 不变量：不把值转换为协议、日志、Session 或长期 Provider 字段。
/// 失败：缺失或空值安全返回 false。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn env_present(name: &str) -> bool {
    std::env::var_os(name).is_some_and(|value| !value.is_empty())
}

/// 功能：把稳定 AgentError 分类映射为公共 CLI 退出码。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn exit_code(code: ErrorCode) -> i32 {
    match code {
        ErrorCode::InvalidRequest
        | ErrorCode::InvalidParams
        | ErrorCode::ProtocolVersionUnsupported => 2,
        ErrorCode::ApprovalRequired
        | ErrorCode::PermissionDenied
        | ErrorCode::PathOutsideWorkspace => 3,
        ErrorCode::ProviderError
        | ErrorCode::ProviderRateLimited
        | ErrorCode::ProviderUnavailable => 4,
        ErrorCode::Cancelled | ErrorCode::StreamInterrupted => 6,
        ErrorCode::JournalCorrupt => 7,
        ErrorCode::IoError | ErrorCode::InternalError => 8,
        _ => 5,
    }
}

#[cfg(test)]
mod tests {
    use std::cell::Cell;
    use std::collections::BTreeMap;
    use std::path::PathBuf;
    use std::sync::Arc;

    use qxnm_forge::provider::{FauxProvider, Provider};

    use super::{register_legacy_azure_provider, resolve_state_dir};

    /// 功能：验证显式 CLI state-dir 不会被任何 conformance 环境默认值覆盖。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn explicit_state_directory_has_highest_precedence() {
        let explicit = PathBuf::from("explicit-state");
        assert_eq!(resolve_state_dir(Some(explicit.clone())), explicit);
    }

    /// 功能：证明普通生产即使宿主存在 Azure 配置，也不读取 endpoint/secret 且 registry 仍仅 faux。
    ///
    /// 输入：关闭的 Provider conformance 门和若被调用就标记失败的 Azure 配置 loader。
    /// 输出：两个 loader 均未执行，Provider registry 只保留初始 faux。
    /// 不变量：测试不修改进程环境、不保存 canary，也不构造 Azure adapter。
    /// 失败：生产分支读取任一 loader 或注册额外 Provider 时测试失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn production_azure_configuration_is_not_read_or_registered() {
        let faux = FauxProvider::new();
        let mut providers = BTreeMap::<String, Arc<dyn Provider>>::from([(
            "faux".to_owned(),
            Arc::new(faux) as Arc<dyn Provider>,
        )]);
        let endpoint_read = Cell::new(false);
        let credential_read = Cell::new(false);
        register_legacy_azure_provider(
            &mut providers,
            false,
            || {
                endpoint_read.set(true);
                panic!("production read Azure endpoint configuration")
            },
            || {
                credential_read.set(true);
                panic!("production read Azure credential")
            },
        );
        assert!(!endpoint_read.get());
        assert!(!credential_read.get());
        assert_eq!(
            providers.keys().map(String::as_str).collect::<Vec<_>>(),
            ["faux"]
        );
    }
}
