use std::sync::Arc;
use std::time::Duration;

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD as BASE64_STANDARD;
use serde::Deserialize;
use serde_json::{Value, json};
use tokio::io::{AsyncBufRead, AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::sync::{Mutex, mpsc};
use uuid::Uuid;

use crate::agent::{Agent, ApprovalResume, QueueKind, RunRequest};
use crate::agent_profile::{
    AgentProfileInput, AgentProfileReference, AgentProfileRunSnapshot, AgentProfileService,
    DangerousActionMode,
};
use crate::error::{AgentError, ErrorCode};
use crate::protocol::{
    AgentProfilesCreateParams, AgentProfilesDeleteParams, AgentProfilesListParams,
    AgentProfilesUpdateParams, ApprovalRespondParams, InitializeParams, InitializeResult,
    JsonRpcNotification, JsonRpcRequest, JsonRpcResponse, PROTOCOL_VERSION, PeerInfo, QueueParams,
    RunCancelParams, RunStartParams, SessionBranchSelectParams, SessionCompactParams,
    SessionGetParams, is_valid_request_id, parse_strict_request,
};
use crate::provider::{FauxProvider, FauxScenario};
use crate::provider_catalog::{ProviderCatalog, ProviderCatalogListParams};
use crate::provider_connection::{
    CustomProviderConnectionRuntime, ProviderConnectionsCreateParams,
    ProviderConnectionsDeleteParams, ProviderConnectionsDiscoverModelsParams,
    ProviderConnectionsListParams, ProviderConnectionsUpdateParams,
    ProviderCredentialsRemoveParams, ProviderCredentialsSetParams,
};
use crate::provider_identity::{
    AdvertisedModel, ProviderIdentityAdvertisement, default_models, is_provider_id, model_key,
};
use crate::provider_route::ProviderRouteSnapshot;
use crate::session_lifecycle::{SessionLifecycleService, lifecycle_busy, parse_session_cursor};
use crate::terminal::{TerminalAfterResponse, TerminalManager, TerminalNotification};

const MAX_FRAME_BYTES: usize = 1_048_576;
const MAX_EVENT_BYTES: usize = 262_144;
const MAX_ARTIFACT_BYTES: usize = 1_073_741_824;
const MAX_INPUT_IMAGE_BYTES: usize = 524_288;
const OUTBOUND_WRITE_TIMEOUT: Duration = Duration::from_secs(1);

/// stdio daemon 的一次请求处理结果；`after_response` 用于强制响应先于后续事件。
struct DispatchResult {
    response: JsonRpcResponse,
    after_response: Option<AfterResponse>,
    close_after_response: bool,
}

enum AfterResponse {
    StartRun(RunRequest),
    ResumeApproval(ApprovalResume),
    Terminal(TerminalAfterResponse),
}

enum InboundFrame {
    Data(Vec<u8>),
    Oversized,
}

/// `faux/configure` 的严格参数对象。
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FauxConfigureParams {
    session_id: String,
    scenario: FauxScenario,
}

/// `models/list` 的严格可选 Provider 过滤参数。
#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ModelsListParams {
    #[serde(default)]
    provider_id: Option<Value>,
}

/// `artifacts/create` 的严格图片发布参数。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ArtifactCreateParams {
    session_id: String,
    media_type: String,
    data_base64: String,
}

/// `session/list` 的严格可选 cursor 与 page limit 参数。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SessionListParams {
    #[serde(default)]
    cursor: Option<String>,
    #[serde(default)]
    limit: Option<usize>,
}

/// `session/archive|restore|delete` 的严格唯一 Session 引用参数。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SessionLifecycleMutationParams {
    session_id: String,
}

/// qxnm-forge Rust 的 UTF-8 NDJSON JSON-RPC daemon。
pub struct Daemon {
    agent: Arc<Agent>,
    faux: FauxProvider,
    agent_profiles: AgentProfileService,
    initialized: bool,
    interactive_approvals: bool,
    terminal_events: bool,
    conformance_mode: bool,
    provider_identity: Option<ProviderIdentityAdvertisement>,
    provider_route: Option<ProviderRouteSnapshot>,
    provider_catalog: ProviderCatalog,
    provider_connections: CustomProviderConnectionRuntime,
    session_lifecycle: SessionLifecycleService,
    terminal: TerminalManager,
    event_sender: mpsc::Sender<crate::domain::EventEnvelope>,
    event_receiver: Option<mpsc::Receiver<crate::domain::EventEnvelope>>,
    terminal_event_receiver: Option<mpsc::Receiver<TerminalNotification>>,
}

impl Daemon {
    /// 功能：创建 stdio daemon，并建立 Agent/terminal 两条有界事件通道。
    ///
    /// 输入：完整 Agent、faux provider、Profile/Provider connection services、互斥 identity/route、
    /// 自定义 Provider 启动快照和 CLI 选定 workspace。
    /// 输出：初始化前的连接状态及冻结 Provider 配置模板；terminal 只有 conformance + fixture-only 双门控时可用。
    /// 不变量：identity 广告不注册 adapter；canonical/custom 广告与 Agent adapter 来自同一启动快照；模板不读取 credential 或扩大 registry；构造不启动 Provider、工具或 PTY；
    /// `QXNM_FORGE_CONFORMANCE=1` 单独不授权 terminal。
    /// 失败：terminal workspace 无法 canonicalize 或冻结 Provider 目录无效时返回脱敏结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn new(
        agent: Arc<Agent>,
        faux: FauxProvider,
        agent_profiles: AgentProfileService,
        provider_identity: Option<ProviderIdentityAdvertisement>,
        provider_route: Option<ProviderRouteSnapshot>,
        provider_connections: CustomProviderConnectionRuntime,
        workspace: impl AsRef<std::path::Path>,
    ) -> Result<Self, AgentError> {
        let conformance_mode = std::env::var("QXNM_FORGE_CONFORMANCE").as_deref() == Ok("1");
        let (event_sender, event_receiver) = mpsc::channel(256);
        let (terminal, terminal_event_receiver) =
            TerminalManager::new(workspace, conformance_mode)?;
        let session_lifecycle = SessionLifecycleService::new(agent.session_store())?;
        Ok(Self {
            agent,
            faux,
            agent_profiles,
            initialized: false,
            interactive_approvals: false,
            terminal_events: false,
            conformance_mode,
            provider_identity,
            provider_route,
            provider_catalog: ProviderCatalog::from_frozen()?,
            provider_connections,
            session_lifecycle,
            terminal,
            event_sender,
            event_receiver: Some(event_receiver),
            terminal_event_receiver: Some(terminal_event_receiver),
        })
    }

    /// 功能：通过 stdin/stdout 运行协议循环，并保持 stdout 仅包含 NDJSON 帧。
    ///
    /// 输入：进程 stdin 上的 UTF-8 NDJSON JSON-RPC 请求帧。
    /// 输出：stdout 上互斥写入并 flush 的响应/事件帧；诊断不进入协议流。
    /// 不变量：帧解析前受大小限制；terminal wire ack 只在 stdout flush 后推进；run/审批/terminal after-response 动作只能发生在对应成功响应后；fatal terminal delivery 优先终止 transport。
    /// 失败：输入/输出 I/O、terminal fatal watch、事件任务、协议或 Agent 基础设施失败时统一进入有界断连清理。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn run_stdio(mut self) -> Result<(), AgentError> {
        let stdout = Arc::new(Mutex::new(tokio::io::stdout()));
        let mut events = self.event_receiver.take().ok_or_else(|| {
            AgentError::new(ErrorCode::InternalError, "event receiver already consumed")
        })?;
        let mut terminal_events = self.terminal_event_receiver.take().ok_or_else(|| {
            AgentError::new(
                ErrorCode::InternalError,
                "terminal event receiver already consumed",
            )
        })?;
        let event_stdout = Arc::clone(&stdout);
        let terminal_delivery = self.terminal.clone();
        let mut terminal_failure = self.terminal.subscribe_delivery_failure();
        let mut event_task = tokio::spawn(async move {
            let mut agent_open = true;
            let mut terminal_open = true;
            while agent_open || terminal_open {
                tokio::select! {
                    biased;
                    failure = terminal_failure.changed(), if terminal_open => {
                        match failure {
                            Ok(()) if *terminal_failure.borrow_and_update() => {
                                return Err(AgentError::new(
                                    ErrorCode::InternalError,
                                    "terminal event delivery failed",
                                )
                                .with_details(json!({"kind":"terminal_io"})));
                            }
                            Ok(()) => {}
                            Err(_) => terminal_open = false,
                        }
                    },
                    event = events.recv(), if agent_open => match event {
                        Some(event) => {
                            write_frame(&event_stdout, &JsonRpcNotification::from(&event)).await?;
                        }
                        None => agent_open = false,
                    },
                    event = terminal_events.recv(), if terminal_open => match event {
                        Some(event) => {
                            write_frame(&event_stdout, &event).await?;
                            terminal_delivery.acknowledge_written(&event).await;
                        }
                        None => terminal_open = false,
                    },
                }
            }
            Ok::<(), AgentError>(())
        });

        let (protocol_result, early_event_result) = {
            let protocol = async {
                let mut input = BufReader::new(tokio::io::stdin());
                while let Some(frame) = read_bounded_frame(&mut input, MAX_FRAME_BYTES).await? {
                    let InboundFrame::Data(bytes) = frame else {
                        write_frame(
                            &stdout,
                            &JsonRpcResponse::failure(
                                Value::Null,
                                AgentError::new(
                                    ErrorCode::OutputLimitExceeded,
                                    "RPC frame exceeds limit",
                                ),
                            ),
                        )
                        .await?;
                        continue;
                    };
                    let line = match String::from_utf8(bytes) {
                        Ok(line) => line,
                        Err(error) => {
                            write_frame(
                                &stdout,
                                &JsonRpcResponse::failure(
                                    Value::Null,
                                    AgentError::new(ErrorCode::ParseError, error.to_string()),
                                ),
                            )
                            .await?;
                            continue;
                        }
                    };
                    if line.trim().is_empty() {
                        write_frame(
                            &stdout,
                            &JsonRpcResponse::failure(
                                Value::Null,
                                AgentError::new(
                                    ErrorCode::InvalidRequest,
                                    "blank RPC frame is invalid",
                                ),
                            ),
                        )
                        .await?;
                        continue;
                    }
                    let request: JsonRpcRequest = match parse_strict_request(&line) {
                        Ok(value) => value,
                        Err(error) => {
                            write_frame(
                                &stdout,
                                &JsonRpcResponse::failure(
                                    Value::Null,
                                    AgentError::new(ErrorCode::ParseError, error.to_string()),
                                ),
                            )
                            .await?;
                            continue;
                        }
                    };
                    let dispatched = self.handle(request).await;
                    write_frame(&stdout, &dispatched.response).await?;
                    if let Some(action) = dispatched.after_response {
                        match action {
                            AfterResponse::StartRun(request) => {
                                self.agent
                                    .start_run(request, self.event_sender.clone())
                                    .await;
                            }
                            AfterResponse::ResumeApproval(resume) => resume.resume(),
                            AfterResponse::Terminal(action) => {
                                self.terminal.after_response(action).await?;
                            }
                        }
                    }
                    if dispatched.close_after_response {
                        break;
                    }
                }
                Ok::<(), AgentError>(())
            };
            tokio::pin!(protocol);
            tokio::select! {
                result = &mut protocol => (result, None),
                result = &mut event_task => {
                    let result = result
                        .map_err(|error| AgentError::new(ErrorCode::InternalError, error.to_string()))
                        .and_then(|result| result);
                    (Ok(()), Some(result))
                }
            }
        };
        self.terminal.shutdown().await;
        let disconnect_result = self.agent.resolve_disconnected_approvals().await;
        let _ = self
            .agent
            .wait_for_started_runs(Duration::from_secs(5))
            .await;
        drop(self.event_sender);
        let event_result = if let Some(result) = early_event_result {
            result
        } else {
            match tokio::time::timeout(Duration::from_secs(1), &mut event_task).await {
                Ok(result) => result.map_err(|error| {
                    AgentError::new(ErrorCode::InternalError, error.to_string())
                })?,
                Err(_) => {
                    event_task.abort();
                    let _ = event_task.await;
                    Ok(())
                }
            }
        };
        protocol_result?;
        disconnect_result?;
        event_result
    }

    /// 功能：将一个已解析请求转换为响应以及可选的“响应后启动”动作。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn handle(&mut self, request: JsonRpcRequest) -> DispatchResult {
        let id = request.id.clone();
        let was_initialized = self.initialized;
        match self.dispatch(request).await {
            Ok((result, after_response)) => DispatchResult {
                response: JsonRpcResponse::success(id, result),
                after_response,
                close_after_response: false,
            },
            Err(error) => DispatchResult {
                response: JsonRpcResponse::failure(id, error),
                after_response: None,
                close_after_response: !was_initialized,
            },
        }
    }

    /// 功能：校验连接生命周期并分派一个 JSON-RPC 方法。
    ///
    /// 输入：已经 strict JSON 解码、但尚未执行方法级验证的请求。
    /// 输出：语言中立结果和可选响应后动作。
    /// 不变量：initialize 必须首发；所有 accepted mutation 返回前 durable；工具执行与审批恢复不在响应前发生。
    /// 失败：协议生命周期、ID、参数、未知方法或底层 Agent 操作失败时返回公共结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn dispatch(
        &mut self,
        request: JsonRpcRequest,
    ) -> Result<(Value, Option<AfterResponse>), AgentError> {
        if request.jsonrpc != "2.0" {
            return Err(AgentError::new(
                ErrorCode::InvalidRequest,
                "jsonrpc must equal '2.0'",
            ));
        }
        if !is_valid_request_id(&request.id) {
            return Err(AgentError::new(
                ErrorCode::InvalidRequest,
                "request id must be an opaque string or safe non-negative integer",
            ));
        }
        if !self.initialized && request.method != "initialize" {
            return Err(AgentError::new(
                ErrorCode::NotInitialized,
                "initialize must be the first request",
            ));
        }
        match request.method.as_str() {
            "initialize" => Ok((self.initialize(request.params)?, None)),
            "faux/configure" => {
                if !self.conformance_mode {
                    return Err(AgentError::new(
                        ErrorCode::MethodNotFound,
                        "faux/configure is available only in conformance mode",
                    ));
                }
                let params: FauxConfigureParams = parse_params(request.params)?;
                if params.scenario.schema_version != PROTOCOL_VERSION {
                    return Err(AgentError::new(
                        ErrorCode::InvalidParams,
                        "unsupported faux scenario version",
                    ));
                }
                let scenario_id = format!("faux:{}", params.scenario.name);
                self.agent
                    .record_faux_configuration(&params.session_id, &params.scenario)
                    .await?;
                let scripts = params.scenario.into_scripts();
                self.faux.configure(&params.session_id, scripts).await;
                Ok((json!({"scenarioId":scenario_id}), None))
            }
            "agentProfiles/list" => {
                let _: AgentProfilesListParams = parse_params(request.params)?;
                Ok((json!({"profiles":self.agent_profiles.list().await?}), None))
            }
            "agentProfiles/create" => {
                let params: AgentProfilesCreateParams = parse_agent_profile_params(request.params)?;
                self.validate_agent_profile_capabilities(&params.profile)?;
                Ok((
                    json!({"profile":self.agent_profiles.create(params.profile).await?}),
                    None,
                ))
            }
            "agentProfiles/update" => {
                let params: AgentProfilesUpdateParams = parse_agent_profile_params(request.params)?;
                self.validate_agent_profile_capabilities(&params.profile)?;
                let profile = self
                    .agent_profiles
                    .update(&params.profile_id, params.expected_revision, params.profile)
                    .await?;
                Ok((json!({"profile":profile}), None))
            }
            "agentProfiles/delete" => {
                let params: AgentProfilesDeleteParams = parse_agent_profile_params(request.params)?;
                self.agent_profiles
                    .delete(&params.profile_id, params.expected_revision)
                    .await?;
                Ok((json!({"deleted":true}), None))
            }
            "providerConnections/list" => {
                let _: ProviderConnectionsListParams = parse_params(request.params)?;
                Ok((
                    json!({"connections":self.provider_connections.service().list()?}),
                    None,
                ))
            }
            "providerCatalog/list" => {
                let _: ProviderCatalogListParams = parse_params(request.params)?;
                Ok((json!({"templates":self.provider_catalog.templates()}), None))
            }
            "providerConnections/create" => {
                let params: ProviderConnectionsCreateParams = parse_params(request.params)?;
                let connection = self
                    .provider_connections
                    .service()
                    .create(params.connection)?;
                Ok((
                    json!({"connection":connection,"restartRequired":true}),
                    None,
                ))
            }
            "providerConnections/update" => {
                let params: ProviderConnectionsUpdateParams = parse_params(request.params)?;
                let connection = self.provider_connections.service().update(
                    &params.connection_id,
                    params.expected_revision,
                    params.connection,
                )?;
                Ok((
                    json!({"connection":connection,"restartRequired":true}),
                    None,
                ))
            }
            "providerConnections/discoverModels" => {
                let params: ProviderConnectionsDiscoverModelsParams = parse_params(request.params)?;
                let connection = self
                    .provider_connections
                    .service()
                    .discover_models(&params.connection_id, params.expected_revision)
                    .await?;
                let discovered_count = connection.model_ids.len();
                Ok((
                    json!({
                        "connection":connection,
                        "discoveredCount":discovered_count,
                        "restartRequired":true
                    }),
                    None,
                ))
            }
            "providerConnections/delete" => {
                let params: ProviderConnectionsDeleteParams = parse_params(request.params)?;
                let _ = self
                    .provider_connections
                    .service()
                    .delete(&params.connection_id, params.expected_revision)?;
                Ok((json!({"deleted":true,"restartRequired":true}), None))
            }
            "providerCredentials/set" => {
                let params: ProviderCredentialsSetParams = parse_params(request.params)?;
                self.provider_connections.service().set_credential(
                    &params.provider_id,
                    &params.credential_kind,
                    &params.credential,
                )?;
                Ok((
                    json!({
                        "providerId":params.provider_id,
                        "credentialKind":params.credential_kind,
                        "credentialConfigured":true,
                        "restartRequired":true
                    }),
                    None,
                ))
            }
            "providerCredentials/remove" => {
                let params: ProviderCredentialsRemoveParams = parse_params(request.params)?;
                self.provider_connections
                    .service()
                    .remove_credential(&params.provider_id, &params.credential_kind)?;
                Ok((
                    json!({
                        "providerId":params.provider_id,
                        "credentialKind":params.credential_kind,
                        "credentialConfigured":false,
                        "restartRequired":true
                    }),
                    None,
                ))
            }
            "artifacts/create" => {
                let params: ArtifactCreateParams = parse_params(request.params)?;
                let bytes = decode_input_image(&params.data_base64)?;
                let artifact = self
                    .agent
                    .publish_input_image(&params.session_id, &params.media_type, &bytes)
                    .await?;
                Ok((json!({"artifact":artifact}), None))
            }
            "models/list" => {
                let params: ModelsListParams = parse_params(request.params)?;
                let provider_id = match params.provider_id {
                    None => None,
                    Some(Value::String(provider_id)) if is_provider_id(&provider_id) => {
                        Some(provider_id)
                    }
                    Some(_) => {
                        return Err(AgentError::new(
                            ErrorCode::InvalidParams,
                            "models/list providerId is invalid",
                        ));
                    }
                };
                let models = self.advertised_models(provider_id.as_deref());
                Ok((json!({"models":models}), None))
            }
            "run/start" => {
                let mut params = parse_run_start_params(request.params)?;
                if let Some(tool_execution) = params
                    .options
                    .as_ref()
                    .and_then(|options| options.tool_execution.as_deref())
                    && tool_execution != "sequential"
                {
                    return Err(AgentError::new(
                        ErrorCode::InvalidParams,
                        "this implementation currently supports only sequential tool execution",
                    )
                    .with_details(json!({
                        "kind":"invalid_params",
                        "field":"options.toolExecution"
                    })));
                }
                let agent_profile = match params.agent_profile.as_ref() {
                    Some(reference) => Some(
                        self.agent_profiles
                            .resolve_for_run(
                                reference,
                                &params.provider,
                                &self.agent.tool_capabilities(),
                                &self.agent.read_only_tool_capabilities(),
                            )
                            .await?,
                    ),
                    None => None,
                };
                if params.provider.id != "faux"
                    && let Some(advertisement) = &self.provider_identity
                {
                    let matching_routes = advertisement.matching_route_count(
                        &params.provider.id,
                        &params.provider.model_id,
                        params.provider.api_family.as_deref(),
                    );
                    if matching_routes > 1 && params.provider.api_family.is_none() {
                        return Err(AgentError::new(
                            ErrorCode::InvalidParams,
                            "provider selection requires an explicit API family",
                        )
                        .with_details(json!({
                            "kind":"invalid_params",
                            "field":"provider.apiFamily"
                        })));
                    }
                    if matching_routes == 1 {
                        if let Some(profile) = &agent_profile {
                            return Err(agent_profile_model_unavailable(profile));
                        }
                        return Err(AgentError::new(
                            ErrorCode::ProviderUnavailable,
                            "identity-only Provider route is not executable",
                        ));
                    }
                }
                if params.provider.id != "faux"
                    && let Some(snapshot) = &self.provider_route
                {
                    match snapshot.resolve(
                        &params.provider.id,
                        &params.provider.model_id,
                        params.provider.api_family.as_deref(),
                    ) {
                        Ok(Some(family)) => params.provider.api_family = Some(family),
                        Ok(None) => {}
                        Err(_) if agent_profile.is_some() => {
                            return Err(agent_profile_model_unavailable(
                                agent_profile.as_ref().ok_or_else(|| {
                                    AgentError::new(
                                        ErrorCode::InternalError,
                                        "agent profile resolution state is invalid",
                                    )
                                })?,
                            ));
                        }
                        Err(error) => return Err(error),
                    }
                }
                if let Some(family) = self.provider_connections.snapshot().resolve(
                    &params.provider.id,
                    &params.provider.model_id,
                    params.provider.api_family.as_deref(),
                ) {
                    params.provider.api_family = Some(family);
                }
                if params.provider.id != "faux"
                    && !self
                        .provider_connections
                        .snapshot()
                        .contains_provider(&params.provider.id)
                    && !matches!(
                        params.provider.id.as_str(),
                        "openai-compatible"
                            | "groq"
                            | "minimax"
                            | "openai"
                            | "openai-responses"
                            | "anthropic"
                            | "mistral"
                            | "azure-openai-responses"
                            | "google"
                            | "google-vertex"
                            | "amazon-bedrock"
                            | "openai-codex"
                            | "openrouter"
                    )
                {
                    if let Some(profile) = &agent_profile {
                        return Err(agent_profile_model_unavailable(profile));
                    }
                    return Err(AgentError::new(
                        ErrorCode::InvalidParams,
                        "unknown provider id",
                    ));
                }
                let content = params.input.content_blocks()?;
                let prompt = content
                    .iter()
                    .filter_map(|block| match block {
                        crate::domain::ContentBlock::Text { text } => Some(text.as_str()),
                        _ => None,
                    })
                    .collect::<Vec<_>>()
                    .join("\n");
                let run_id = format!("run-{}", Uuid::new_v4());
                let run = RunRequest {
                    session_id: params.session_id,
                    run_id: run_id.clone(),
                    prompt,
                    provider_id: params.provider.id,
                    api_family: params.provider.api_family,
                    model: params.provider.model_id,
                    interactive_approvals: self.interactive_approvals
                        && agent_profile.as_ref().is_none_or(|profile| {
                            profile.dangerous_action_mode != DangerousActionMode::Deny
                        }),
                };
                if let Some(profile) = &agent_profile {
                    self.agent.ensure_profile_model_available(&run, profile)?;
                }
                self.agent
                    .accept_run_content_with_profile(&run, content, agent_profile)
                    .await?;
                Ok((json!({"runId":run_id}), Some(AfterResponse::StartRun(run))))
            }
            "run/cancel" => {
                let params: RunCancelParams = parse_params(request.params)?;
                let cancellation_state = self
                    .agent
                    .cancel(&params.session_id, &params.run_id)
                    .await?;
                Ok((json!({"cancellationState":cancellation_state}), None))
            }
            "approval/respond" => {
                let params: ApprovalRespondParams = parse_params(request.params)?;
                let resume = self
                    .agent
                    .accept_approval(
                        &params.session_id,
                        &params.run_id,
                        &params.approval_id,
                        params.decision,
                    )
                    .await?;
                Ok((
                    json!({"accepted":true}),
                    Some(AfterResponse::ResumeApproval(resume)),
                ))
            }
            "run/steer" | "run/followUp" => {
                let method = request.method.clone();
                let params: QueueParams = parse_params(request.params)?;
                let text = params.input.text()?;
                let kind = if method == "run/steer" {
                    QueueKind::Steering
                } else {
                    QueueKind::FollowUp
                };
                let queue_item_id = self
                    .agent
                    .enqueue(&params.session_id, &params.run_id, text, kind)
                    .await?;
                Ok((json!({"accepted":true,"queueItemId":queue_item_id}), None))
            }
            "session/get" => {
                let params: SessionGetParams = parse_params(request.params)?;
                let snapshot = self
                    .agent
                    .session_get(&params.session_id, params.after_seq)
                    .await?;
                let value = serde_json::to_value(snapshot).map_err(|error| {
                    AgentError::new(ErrorCode::InternalError, error.to_string())
                })?;
                Ok((value, None))
            }
            "session/list" => {
                let params: SessionListParams = parse_params(request.params)?;
                let offset = parse_session_cursor(params.cursor.as_deref())?;
                let limit = params
                    .limit
                    .unwrap_or_else(SessionLifecycleService::default_page_limit);
                let summaries = self.session_lifecycle.list().await?;
                let page =
                    SessionLifecycleService::create_page(&summaries, offset, limit, &request.id)?;
                let value = serde_json::to_value(page).map_err(|_| {
                    AgentError::new(
                        ErrorCode::InternalError,
                        "session list response serialization failed",
                    )
                })?;
                Ok((value, None))
            }
            "session/archive" => {
                let params: SessionLifecycleMutationParams = parse_params(request.params)?;
                self.ensure_session_lifecycle_idle(&params.session_id)
                    .await?;
                let session = self.session_lifecycle.archive(&params.session_id).await?;
                Ok((json!({"session":session}), None))
            }
            "session/restore" => {
                let params: SessionLifecycleMutationParams = parse_params(request.params)?;
                self.ensure_session_lifecycle_idle(&params.session_id)
                    .await?;
                let session = self.session_lifecycle.restore(&params.session_id).await?;
                Ok((json!({"session":session}), None))
            }
            "session/delete" => {
                let params: SessionLifecycleMutationParams = parse_params(request.params)?;
                self.ensure_session_lifecycle_idle(&params.session_id)
                    .await?;
                self.session_lifecycle.delete(&params.session_id).await?;
                Ok((json!({"deleted":true}), None))
            }
            "session/branch/select" => {
                let params: SessionBranchSelectParams = parse_params(request.params)?;
                let result = self.agent.select_session_branch(params).await?;
                let value = serde_json::to_value(result).map_err(|error| {
                    AgentError::new(ErrorCode::InternalError, error.to_string())
                })?;
                Ok((value, None))
            }
            "session/compact" => {
                let params: SessionCompactParams = parse_params(request.params)?;
                let result = self.agent.compact_session(params).await?;
                let value = serde_json::to_value(result).map_err(|error| {
                    AgentError::new(ErrorCode::InternalError, error.to_string())
                })?;
                Ok((value, None))
            }
            "terminal/open" => {
                self.require_terminal_events()?;
                let (result, action) = self.terminal.open(request.params).await?;
                Ok((result, Some(AfterResponse::Terminal(action))))
            }
            "terminal/write" => {
                self.require_terminal_events()?;
                Ok((self.terminal.write(request.params).await?, None))
            }
            "terminal/resize" => {
                self.require_terminal_events()?;
                Ok((self.terminal.resize(request.params).await?, None))
            }
            "terminal/signal" => {
                self.require_terminal_events()?;
                Ok((self.terminal.signal(request.params).await?, None))
            }
            "terminal/attach" => {
                self.require_terminal_events()?;
                let (result, action) = self.terminal.attach(request.params).await?;
                Ok((result, Some(AfterResponse::Terminal(action))))
            }
            "terminal/close" => {
                self.require_terminal_events()?;
                let (result, action) = self.terminal.close(request.params).await?;
                Ok((result, action.map(AfterResponse::Terminal)))
            }
            _ => Err(AgentError::new(
                ErrorCode::MethodNotFound,
                format!("unknown RPC method: {}", request.method),
            )),
        }
    }

    /// 功能：在 archive、restore 或 delete 前拒绝本进程 active run 与 pending faux 配置。
    ///
    /// 输入：严格 DTO 解码后的 Session ID。
    /// 输出：Agent RunControl 和未绑定 faux 队列均不存在时成功。
    /// 不变量：daemon 串行 dispatch 保证检查到文件 mutation 返回间不会接受新的 run/configure 请求；外部 writer 由文件锁复核。
    /// 失败：任一内存工作仍存在时返回可重试 `session_busy`，且不读取或修改 journal。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn ensure_session_lifecycle_idle(&self, session_id: &str) -> Result<(), AgentError> {
        self.agent
            .ensure_session_mutation_quiescent(session_id)
            .await
            .map_err(|_| lifecycle_busy(session_id))?;
        if self.faux.has_pending_configuration(session_id).await {
            return Err(lifecycle_busy(session_id));
        }
        Ok(())
    }

    /// 功能：在分派 terminal RPC 前验证客户端事件协商与宿主 PTY 策略能力。
    ///
    /// 输入：当前连接 initialize 后保存的 terminalEvents 协商状态。
    /// 输出：客户端可接收通知且 fixture-only Unix PTY 可用时成功。
    /// 不变量：未广告的方法始终返回 method_not_found，不以 pipe 或无事件模式降级。
    /// 失败：任一门控缺失返回 `-32601/method_not_found`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn require_terminal_events(&self) -> Result<(), AgentError> {
        if self.terminal_events && self.terminal.enabled() {
            Ok(())
        } else {
            Err(AgentError::new(
                ErrorCode::MethodNotFound,
                "terminal methods are not enabled",
            ))
        }
    }

    /// 功能：协商协议版本并返回当前 daemon 的真实能力与限制。
    ///
    /// 输入：strict initialize 参数，包括候选版本、客户端交互审批和 terminal event 能力。
    /// 输出：选定版本、实现标识、实际 methods/events/providers/tools 与资源限制。
    /// 不变量：同一连接只成功初始化一次；terminal methods 只有客户端协商、真 PTY 与窄宿主策略同时存在时广告。
    /// 失败：重复初始化、参数/版本、路径 conformance 配置或 hard-sandbox 启动状态无效时返回结构化错误且不伪报能力。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn initialize(&mut self, params: Value) -> Result<Value, AgentError> {
        if self.initialized {
            return Err(AgentError::new(
                ErrorCode::AlreadyInitialized,
                "connection is already initialized",
            ));
        }
        let params: InitializeParams = parse_params(params)?;
        if !params
            .protocol_versions
            .iter()
            .any(|version| version == PROTOCOL_VERSION)
        {
            return Err(AgentError::new(
                ErrorCode::ProtocolVersionUnsupported,
                "no mutually supported protocol version",
            )
            .with_details(json!({"supported":[PROTOCOL_VERSION]})));
        }
        let hard_sandbox = self.agent.hard_sandbox_capability()?;
        self.interactive_approvals = params.capabilities.interactive_approvals;
        self.terminal_events = params.capabilities.terminal_events && self.terminal.enabled();
        self.initialized = true;
        let providers = if let Some(advertisement) = &self.provider_identity {
            advertisement.provider_capabilities()
        } else if let Some(snapshot) = &self.provider_route {
            snapshot.merged_provider_capabilities(self.agent.provider_capabilities())
        } else {
            self.agent.provider_capabilities()
        };
        let providers = self
            .provider_connections
            .snapshot()
            .merged_provider_capabilities(providers);
        let tools = self.agent.tool_capabilities();
        let mut methods = vec!["initialize"];
        if self.conformance_mode {
            methods.push("faux/configure");
        }
        methods.extend([
            "agentProfiles/list",
            "agentProfiles/create",
            "agentProfiles/update",
            "agentProfiles/delete",
            "providerConnections/list",
            "providerConnections/create",
            "providerConnections/update",
            "providerConnections/discoverModels",
            "providerConnections/delete",
            "providerCredentials/set",
            "providerCredentials/remove",
            "artifacts/create",
            "models/list",
            "providerCatalog/list",
            "run/start",
            "run/cancel",
            "run/steer",
            "run/followUp",
            "approval/respond",
            "session/get",
            "session/list",
            "session/archive",
            "session/restore",
            "session/delete",
            "session/branch/select",
            "session/compact",
        ]);
        let (_, terminal_event_types) = if self.terminal_events {
            let (terminal_methods, terminal_event_types) = self.terminal.capabilities();
            methods.extend(terminal_methods);
            (true, terminal_event_types)
        } else {
            (false, Vec::new())
        };
        let mut capabilities = json!({
            "methods":methods,
            "eventTypes":[
                "run.started","turn.started","turn.completed","message.started",
                "message.delta","message.completed","tool.requested","approval.requested",
                "approval.resolved","tool.started","tool.delta","tool.completed",
                "retry.scheduled","context.compacted","run.completed","run.failed",
                "run.cancelled","run.interrupted"
            ],
            "providers":providers,
            "tools":tools,
            "transports":["stdio"]
        });
        if let Some(hard_sandbox) = hard_sandbox
            && let Some(object) = capabilities.as_object_mut()
        {
            object.insert("hardSandbox".to_owned(), hard_sandbox);
        }
        if !terminal_event_types.is_empty()
            && let Some(object) = capabilities.as_object_mut()
        {
            object.insert(
                "terminalEventTypes".to_owned(),
                serde_json::to_value(terminal_event_types).map_err(|error| {
                    AgentError::new(ErrorCode::InternalError, error.to_string())
                })?,
            );
        }
        serde_json::to_value(InitializeResult {
            protocol_version: PROTOCOL_VERSION,
            implementation: PeerInfo {
                name: "qxnm-forge-rust".to_owned(),
                version: env!("CARGO_PKG_VERSION").to_owned(),
                language: "rust".to_owned(),
            },
            capabilities,
            limits: json!({
                "maxFrameBytes":MAX_FRAME_BYTES,
                "maxEventBytes":MAX_EVENT_BYTES,
                "maxArtifactBytes":MAX_ARTIFACT_BYTES,
                "maxConcurrentRuns":1
            }),
        })
        .map_err(|error| AgentError::new(ErrorCode::InternalError, error.to_string()))
    }

    /// 功能：验证待持久化 Profile 只引用当前 models/list 与 initialize 工具子集。
    ///
    /// 输入：已经 strict DTO 解码、尚未写数据库的 Profile input。
    /// 输出：模型三元 identity 和每个工具均由当前 daemon 广告时成功。
    /// 不变量：验证只读取启动期快照和真实 ToolRegistry，不读取 credential 值或扩大能力。
    /// 失败：模型或工具未广告时返回 fixture 冻结的 `agent_profile_invalid`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn validate_agent_profile_capabilities(
        &self,
        profile: &AgentProfileInput,
    ) -> Result<(), AgentError> {
        let models = self.advertised_models(None);
        let model_matches = serde_json::to_value(models)
            .ok()
            .and_then(|value| value.as_array().cloned())
            .is_some_and(|models| {
                models.iter().any(|model| {
                    model["providerId"].as_str() == Some(&profile.model.provider_id)
                        && model["modelId"].as_str() == Some(&profile.model.model_id)
                        && model["apiFamily"].as_str() == Some(&profile.model.api_family)
                })
            });
        let available_tools = self
            .agent
            .tool_capabilities()
            .into_iter()
            .collect::<std::collections::BTreeSet<_>>();
        if !model_matches
            || profile
                .requested_tool_ids
                .iter()
                .any(|tool_id| !available_tools.contains(tool_id))
        {
            return Err(agent_profile_invalid());
        }
        Ok(())
    }

    /// 功能：从 canonical 路由与自定义启动快照生成同源模型列表。
    ///
    /// 输入：可选 Provider ID 精确过滤。
    /// 输出：按 `(providerId, modelId, apiFamily)` 排序且与已注册 adapter 一致的 descriptor。
    /// 不变量：identity-only 分支的自定义快照为空；CRUD 后不会在本进程动态扩大能力。
    /// 失败：本方法不执行 I/O 且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn advertised_models(&self, provider_id: Option<&str>) -> Vec<AdvertisedModel> {
        let mut models = if let Some(advertisement) = &self.provider_identity {
            advertisement.models(provider_id)
        } else if let Some(snapshot) = &self.provider_route {
            snapshot.models(provider_id)
        } else {
            default_models(provider_id)
        };
        models.extend(self.provider_connections.snapshot().models(provider_id));
        models.sort_by_key(model_key);
        models
    }
}

/// 严格解码 `artifacts/create` 的 canonical Base64，并执行固定输入大小上限。
///
/// 输入：不含 data URL 前缀或空白的 Base64 文本。
/// 输出：最多 512 KiB 的原始图片字节。
/// 不变量：失败错误不包含输入、字节、路径或摘要；宽松 Base64 与非 canonical padding 均拒绝。
/// 失败：字符、padding、解码或大小边界不满足时返回 `invalid_params`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn decode_input_image(value: &str) -> Result<Vec<u8>, AgentError> {
    if value.is_empty()
        || value.len() > 699_052
        || value.bytes().any(|byte| byte.is_ascii_whitespace())
    {
        return Err(AgentError::new(
            ErrorCode::InvalidParams,
            "artifact image data is invalid",
        ));
    }
    let bytes = BASE64_STANDARD
        .decode(value)
        .map_err(|_| AgentError::new(ErrorCode::InvalidParams, "artifact image data is invalid"))?;
    if bytes.is_empty()
        || bytes.len() > MAX_INPUT_IMAGE_BYTES
        || BASE64_STANDARD.encode(&bytes) != value
    {
        return Err(AgentError::new(
            ErrorCode::InvalidParams,
            "artifact image data is invalid",
        ));
    }
    Ok(bytes)
}

/// 功能：增量读取一个有界 LF/CRLF NDJSON 帧，超限后持续排空到下一帧边界。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
///
/// 任意 UTF-8 字节分片在完整帧后统一解码；EOF 时接受完整的无 LF 最后一帧。
async fn read_bounded_frame<R>(
    reader: &mut R,
    max_bytes: usize,
) -> Result<Option<InboundFrame>, AgentError>
where
    R: AsyncBufRead + Unpin,
{
    let mut output = Vec::new();
    let mut oversized = false;
    loop {
        let available = reader.fill_buf().await?;
        if available.is_empty() {
            if output.is_empty() && !oversized {
                return Ok(None);
            }
            return Ok(Some(if oversized {
                InboundFrame::Oversized
            } else {
                if output.last() == Some(&b'\r') {
                    output.pop();
                }
                InboundFrame::Data(output)
            }));
        }
        let newline = available.iter().position(|byte| *byte == b'\n');
        let take = newline.unwrap_or(available.len());
        if !oversized {
            if output.len().saturating_add(take) > max_bytes {
                oversized = true;
                output.clear();
            } else {
                output.extend_from_slice(&available[..take]);
            }
        }
        let consumed = take + usize::from(newline.is_some());
        reader.consume(consumed);
        if newline.is_some() {
            if oversized {
                return Ok(Some(InboundFrame::Oversized));
            }
            if output.last() == Some(&b'\r') {
                output.pop();
            }
            return Ok(Some(InboundFrame::Data(output)));
        }
    }
}

/// 功能：解析 Agent Profile mutation 参数并映射为 fixture 冻结的闭合输入错误。
///
/// 输入：尚未进行方法级 DTO 解码的 JSON params。
/// 输出：没有未知/缺失/错误类型字段的目标参数。
/// 不变量：不把 serde 诊断或输入值回显给客户端。
/// 失败：任一解码失败返回 `agent_profile_invalid`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_agent_profile_params<T: serde::de::DeserializeOwned>(
    value: Value,
) -> Result<T, AgentError> {
    serde_json::from_value(value).map_err(|_| agent_profile_invalid())
}

/// 功能：严格解析 run/start，并把 Agent Profile 引用自身的结构错误映射为冻结错误。
///
/// 输入：尚未进行方法级 DTO 解码的 JSON params。
/// 输出：Profile 引用和其余 run 字段均通过各自闭合 DTO 校验的参数。
/// 不变量：只重映射 `agentProfile` 子对象的错误；其他 run 字段仍使用通用 `invalid_params`。
/// 失败：Profile 引用缺字段、类型错误、null 或未知字段时返回 `agent_profile_invalid`；其余解码错误返回通用参数错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_run_start_params(value: Value) -> Result<RunStartParams, AgentError> {
    if let Some(agent_profile) = value
        .as_object()
        .and_then(|object| object.get("agentProfile"))
    {
        serde_json::from_value::<AgentProfileReference>(agent_profile.clone())
            .map_err(|_| agent_profile_invalid())?;
    }
    parse_params(value)
}

/// 功能：构造 CRUD 输入统一使用的冻结 Agent Profile 校验错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn agent_profile_invalid() -> AgentError {
    AgentError::new(ErrorCode::InvalidParams, "agent profile is invalid")
        .with_details(json!({"kind":"agent_profile_invalid","field":"profile"}))
}

/// 功能：为已匹配但当前不可执行的 Profile model 构造稳定错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn agent_profile_model_unavailable(profile: &AgentProfileRunSnapshot) -> AgentError {
    AgentError::new(
        ErrorCode::ProviderUnavailable,
        "agent profile model is unavailable",
    )
    .retryable(true)
    .with_details(json!({
        "kind":"agent_profile_model_unavailable",
        "providerId":profile.model.provider_id,
        "modelId":profile.model.model_id,
        "apiFamily":profile.model.api_family
    }))
}

/// 功能：将 JSON 参数严格反序列化为方法 DTO，并统一映射参数错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn parse_params<T: serde::de::DeserializeOwned>(value: Value) -> Result<T, AgentError> {
    serde_json::from_value(value)
        .map_err(|error| AgentError::new(ErrorCode::InvalidParams, error.to_string()))
}

/// 功能：原子序列化并写出一个 NDJSON 帧，禁止并发写入交错。
///
/// 输入：共享 stdout 锁与可序列化协议值。
/// 输出：完整 JSON、换行和 flush 在一秒边界内完成后返回。
/// 不变量：序列化后的 JSON 不超过帧上限；同一帧的 write/flush 持有唯一 stdout 锁。
/// 失败：序列化、超限、stdout I/O 或锁等待/写入超时返回结构化错误；超时后不继续阻塞 daemon shutdown。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn write_frame(
    stdout: &Arc<Mutex<tokio::io::Stdout>>,
    value: &impl serde::Serialize,
) -> Result<(), AgentError> {
    let mut encoded = serde_json::to_vec(value)
        .map_err(|error| AgentError::new(ErrorCode::InternalError, error.to_string()))?;
    if encoded.len() > MAX_FRAME_BYTES {
        return Err(AgentError::new(
            ErrorCode::OutputLimitExceeded,
            "outbound RPC frame exceeds limit",
        ));
    }
    encoded.push(b'\n');
    tokio::time::timeout(OUTBOUND_WRITE_TIMEOUT, async {
        let mut stdout = stdout.lock().await;
        stdout.write_all(&encoded).await?;
        stdout.flush().await?;
        Ok::<(), AgentError>(())
    })
    .await
    .map_err(|_| AgentError::new(ErrorCode::InternalError, "outbound RPC write timed out"))?
}

#[cfg(test)]
mod tests {
    use serde_json::json;
    use tokio::io::BufReader;

    use super::{InboundFrame, decode_input_image, parse_run_start_params, read_bounded_frame};

    /// 功能：验证输入图片只接受 canonical Base64，并在错误中不回显正文。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn input_image_base64_is_strict_and_redacted() {
        assert_eq!(
            decode_input_image("iVBORw0KGgo=").expect("canonical PNG prefix should decode"),
            [137, 80, 78, 71, 13, 10, 26, 10]
        );
        for invalid in ["iVBORw0KGgo", "iVBORw0K Ggo=", "!!!!"] {
            let error = decode_input_image(invalid).expect_err("invalid base64 must fail");
            assert!(!error.message.contains(invalid));
        }
    }

    /// 功能：验证 run/start 的非法 Profile 引用使用冻结错误且不扩大到其他字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn run_start_profile_reference_rejects_unknown_latest_field() {
        let error = parse_run_start_params(json!({
            "sessionId":"session-profile-invalid",
            "input":{"role":"user","content":[{"type":"text","text":"review"}]},
            "provider":{"id":"faux","modelId":"faux-v1","apiFamily":"faux"},
            "agentProfile":{"profileId":"agent-profile-invalid","revision":1,"latest":true}
        }))
        .expect_err("unknown Profile reference fields must be rejected");
        assert_eq!(error.code, crate::error::ErrorCode::InvalidParams);
        assert!(!error.retryable);
        assert_eq!(
            error.details,
            json!({"kind":"agent_profile_invalid","field":"profile"})
        );

        let other_error = parse_run_start_params(json!({
            "sessionId":7,
            "input":{"role":"user","content":[{"type":"text","text":"review"}]},
            "provider":{"id":"faux","modelId":"faux-v1","apiFamily":"faux"}
        }))
        .expect_err("non-Profile run field errors must remain generic");
        assert_ne!(other_error.details["kind"], "agent_profile_invalid");
    }

    /// 功能：验证有界读取器接受 CRLF 与无 LF 的最终完整帧。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn bounded_reader_handles_crlf_and_final_frame() -> Result<(), crate::error::AgentError> {
        let input = b"{\"a\":1}\r\n{\"b\":2}";
        let mut reader = BufReader::new(input.as_slice());
        let Some(InboundFrame::Data(first)) = read_bounded_frame(&mut reader, 64).await? else {
            panic!("first frame must be data");
        };
        let Some(InboundFrame::Data(second)) = read_bounded_frame(&mut reader, 64).await? else {
            panic!("second frame must be data");
        };
        assert_eq!(first, br#"{"a":1}"#);
        assert_eq!(second, br#"{"b":2}"#);
        assert!(read_bounded_frame(&mut reader, 64).await?.is_none());
        Ok(())
    }

    /// 功能：验证超限帧被完整排空且不会吞掉下一条合法帧。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn oversized_frame_resynchronizes_at_lf() -> Result<(), crate::error::AgentError> {
        let input = b"123456789\nok\n";
        let mut reader = BufReader::new(input.as_slice());
        assert!(matches!(
            read_bounded_frame(&mut reader, 4).await?,
            Some(InboundFrame::Oversized)
        ));
        let Some(InboundFrame::Data(next)) = read_bounded_frame(&mut reader, 4).await? else {
            panic!("next frame must remain available");
        };
        assert_eq!(next, b"ok");
        Ok(())
    }
}
