use std::collections::{BTreeMap, BTreeSet, VecDeque};
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

use chrono::{DateTime, Utc};
use futures_util::StreamExt;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use tokio::sync::{Mutex, mpsc, oneshot};
use tokio_util::sync::CancellationToken;
use uuid::Uuid;

use crate::agent_profile::AgentProfileRunSnapshot;
use crate::domain::{
    ArtifactRef, ContentBlock, EventEnvelope, FinishReason, Message, ProviderEvent, ProviderImage,
    Role, ToolEffect, ToolOutput, Usage, artifact_content_type,
};
use crate::error::{AgentError, ErrorCode};
use crate::policy::PolicyDecision;
use crate::protocol::{
    ApprovalDecision, SessionBranchSelectParams, SessionCompactParams, parse_strict_value,
};
use crate::provider::{Provider, ProviderRequest, ProviderResolvedImage};
use crate::session::{
    BranchSelectionResult, ContextCompactionInput, ContextCompactionResult, SessionLease,
    SessionSnapshot, SessionStore, session_busy_error,
};
use crate::tools::ToolRegistry;

const MAX_TOOL_TURNS: usize = 32;
const MAX_QUEUED_INPUTS: usize = 1_024;
const MAX_PROVIDER_INPUT_IMAGES: usize = 8;
const MAX_CUSTOM_INPUT_IMAGE_BYTES: usize = 524_288;
const MAX_CUSTOM_TOTAL_INPUT_IMAGE_BYTES: u64 = 4_194_304;
const MAX_OPENROUTER_INPUT_IMAGE_BYTES: usize = 16_777_216;
const DEFAULT_APPROVAL_TIMEOUT: Duration = Duration::from_secs(300);
const MAX_APPROVAL_TIMEOUT: Duration = Duration::from_secs(3_600);

#[derive(Debug, Clone)]
pub struct RunRequest {
    pub session_id: String,
    pub run_id: String,
    pub prompt: String,
    pub provider_id: String,
    pub api_family: Option<String>,
    pub model: String,
    pub interactive_approvals: bool,
}

struct RunControl {
    session_id: String,
    cancellation: CancellationToken,
    _lease: SessionLease,
    task_active: AtomicBool,
    usage: Mutex<Usage>,
    queues: Mutex<RunQueues>,
    lifecycle: Mutex<RunLifecycle>,
    approvals: Mutex<BTreeMap<String, PendingApproval>>,
    agent_profile: Option<AgentProfileRunSnapshot>,
}

#[derive(Default)]
struct RunLifecycle {
    cancellation_requested: bool,
    terminal: bool,
}

struct PendingApproval {
    sender: oneshot::Sender<ApprovalWaitOutcome>,
    deadline: tokio::time::Instant,
}

struct ApprovalResolution {
    approval_id: String,
    decision: Value,
    resolution_source: &'static str,
}

enum ApprovalWaitOutcome {
    Released(ApprovalResolution),
    DeliveryFailed(ApprovalResolution),
}

/// 已 durable 接受但必须等 RPC 响应 flush 后才能唤醒 Agent 的审批动作。
pub(crate) struct ApprovalResume {
    sender: Option<oneshot::Sender<ApprovalWaitOutcome>>,
    resolution: Option<ApprovalResolution>,
}

impl ApprovalResume {
    /// 功能：在 `approval/respond` 成功响应已经写出并刷新后恢复审批等待者。
    ///
    /// 输入：拥有所有权的单次恢复句柄。
    /// 输出：无；等待者已退出时安全忽略发送失败。
    /// 不变量：每个句柄最多消费一次，且构造前 `approval.resolved` 已 durable。
    /// 失败：本方法不返回错误；接收端提前关闭不会撤销已经接受的审批决定。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn resume(mut self) {
        if let (Some(sender), Some(resolution)) = (self.sender.take(), self.resolution.take()) {
            let _ = sender.send(ApprovalWaitOutcome::Released(resolution));
        }
    }
}

impl Drop for ApprovalResume {
    /// 功能：成功响应未释放审批屏障时向 Agent waiter 注入不可执行的交付失败结果。
    ///
    /// 输入：仍持有 sender/resolution 的未消费恢复句柄。
    /// 输出：最多发送一次 `DeliveryFailed`；正常 `resume` 后无动作。
    /// 不变量：不追加第二条审批决议、不把 durable `allow_once` 转成执行授权。
    /// 失败：接收端已结束时安全忽略发送失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn drop(&mut self) {
        if let (Some(sender), Some(resolution)) = (self.sender.take(), self.resolution.take()) {
            let _ = sender.send(ApprovalWaitOutcome::DeliveryFailed(resolution));
        }
    }
}

struct RunQueues {
    accepting: bool,
    steering: VecDeque<QueuedInput>,
    follow_up: VecDeque<QueuedInput>,
}

#[derive(Debug, Clone)]
struct QueuedInput {
    queue_item_id: String,
    text: String,
}

#[derive(Debug, Clone, Copy)]
pub enum QueueKind {
    Steering,
    FollowUp,
}

/// Agent 只依赖规范化 Provider 与工具接口，不感知具体 HTTP family。
pub struct Agent {
    providers: BTreeMap<String, Arc<dyn Provider>>,
    tools: Arc<ToolRegistry>,
    sessions: SessionStore,
    workspace: PathBuf,
    active: Mutex<BTreeMap<String, Arc<RunControl>>>,
    acceptance: Mutex<()>,
    event_sequences: Mutex<BTreeMap<String, u64>>,
    approval_timeout: Duration,
    approval_responder_connected: AtomicBool,
}

impl Agent {
    /// 功能：创建 Agent 应用服务并注入独立 Provider、工具、Session 与工作区边界。
    ///
    /// 输入：按稳定 ID 索引的 Provider、工具注册表、Session store 和 canonical 工作区。
    /// 输出：尚无活动 run、采用固定五分钟审批 timeout 的 Agent 服务。
    /// 不变量：构造本身不读取 conformance 环境、不发起网络请求或执行工具。
    /// 失败：本方法不返回错误；调用方必须在注入前完成工作区与存储初始化。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn new(
        providers: BTreeMap<String, Arc<dyn Provider>>,
        tools: Arc<ToolRegistry>,
        sessions: SessionStore,
        workspace: PathBuf,
    ) -> Self {
        Self {
            providers,
            tools,
            sessions,
            workspace,
            active: Mutex::new(BTreeMap::new()),
            acceptance: Mutex::new(()),
            event_sequences: Mutex::new(BTreeMap::new()),
            approval_timeout: DEFAULT_APPROVAL_TIMEOUT,
            approval_responder_connected: AtomicBool::new(true),
        }
    }

    /// 功能：仅在 CLI 与环境 conformance 双门同时成立时创建可使用有界短审批 timeout 的 Agent。
    ///
    /// 输入：完整 Provider/工具/Session/工作区依赖，以及字面 `--conformance` 和
    /// `QXNM_FORGE_CONFORMANCE=1` 的可信宿主判定。
    /// 输出：双门成立且 timeout 文本合法时采用该值，否则采用固定五分钟默认值。
    /// 不变量：任一单门都不会读取 `QXNM_FORGE_APPROVAL_TIMEOUT_MS`；timeout 始终位于 1ms 至 1 小时；
    /// 本构造不发起网络请求或执行工具。
    /// 失败：本方法不返回错误；缺失、非严格十进制或越界配置安全回退默认值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn new_with_conformance_timeout(
        providers: BTreeMap<String, Arc<dyn Provider>>,
        tools: Arc<ToolRegistry>,
        sessions: SessionStore,
        workspace: PathBuf,
        cli_conformance: bool,
        environment_conformance: bool,
    ) -> Self {
        let mut agent = Self::new(providers, tools, sessions, workspace);
        agent.approval_timeout =
            approval_timeout_from_env(cli_conformance, environment_conformance);
        agent
    }

    /// 功能：向同 crate 的 application service 暴露只读克隆 SessionStore 句柄。
    ///
    /// 输入：当前 Agent 服务。
    /// 输出：共享同一 canonical state root 与限制的轻量 SessionStore 克隆。
    /// 不变量：不会暴露 state path 给协议客户端，也不会打开 journal 或 writer。
    /// 失败：本方法不执行 I/O 且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub(crate) fn session_store(&self) -> SessionStore {
        self.sessions.clone()
    }

    /// 功能：在同一 Session 中 durable 发布客户端输入图片，并返回 portable 引用。
    ///
    /// 输入：opaque Session ID、受支持图片 MIME 与最多 512 KiB 的完整字节。
    /// 输出：文件与 `artifact.created` 均 durable 后的强绑定引用。
    /// 不变量：与 run 接受共享串行门禁；活动 Session 拒绝上传；字节、Base64 和主机路径不进入 journal 或错误。
    /// 失败：Session busy、MIME/魔数/大小、路径、锁、发布或 journal 失败时返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn publish_input_image(
        &self,
        session_id: &str,
        media_type: &str,
        bytes: &[u8],
    ) -> Result<ArtifactRef, AgentError> {
        if bytes.is_empty() || bytes.len() > 524_288 {
            return Err(AgentError::new(
                ErrorCode::InvalidParams,
                "artifact image data is invalid",
            ));
        }
        let _acceptance = self.acceptance.lock().await;
        if self
            .active
            .lock()
            .await
            .values()
            .any(|control| control.session_id == session_id)
        {
            return Err(session_busy_error());
        }
        self.sessions
            .create_session(session_id, &self.workspace)
            .await?;
        let _lease = self.sessions.acquire_writer(session_id).await?;
        self.sessions
            .store_image_artifact(session_id, bytes, media_type)
            .await
    }

    /// 功能：返回当前进程真实注册、可接受 run/start 的 Provider capability 列表。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// faux 固定暴露离线模型；live family 仅在构建依赖图时满足凭据或显式 endpoint 条件后出现。
    #[must_use]
    pub fn provider_capabilities(&self) -> Vec<Value> {
        let mut providers = Vec::new();
        let mut provider_ids = self
            .providers
            .values()
            .filter(|provider| provider.is_available())
            .map(|provider| provider.id().to_owned())
            .collect::<BTreeSet<_>>();
        if provider_ids.remove("faux") {
            providers.push(json!({"id":"faux","models":["faux-v1"]}));
        }
        providers.extend(
            provider_ids
                .into_iter()
                .map(|provider_id| json!({"id":provider_id,"models":[]})),
        );
        providers
    }

    /// 功能：返回当前宿主真实注册并可进入审批状态机的工具名称。
    ///
    /// 输入：Agent 持有的原生 Rust ToolRegistry。
    /// 输出：按定义顺序排列的稳定工具名列表。
    /// 不变量：能力广告从实际 definitions 派生；未具备强进程 containment 的平台不会硬编码广告 process/shell。
    /// 失败：本方法不访问外部服务且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn tool_capabilities(&self) -> Vec<String> {
        self.tools
            .definitions()
            .into_iter()
            .map(|definition| definition.name)
            .collect()
    }

    /// 功能：返回当前注册表中只读 Profile 可继续请求的工具名称。
    ///
    /// 输入：Agent 持有的真实工具定义。
    /// 输出：仅 `ToolEffect::Read` 工具的稳定定义顺序列表。
    /// 不变量：Profile deny 模式不能把写入、进程、shell 或 terminal 工具带入有效集合。
    /// 失败：本方法不访问外部状态且不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn read_only_tool_capabilities(&self) -> Vec<String> {
        self.tools
            .definitions()
            .into_iter()
            .filter(|definition| definition.effect == ToolEffect::Read)
            .map(|definition| definition.name)
            .collect()
    }

    /// 功能：取得 startup self-test 证明的 hard-sandbox capability，并传播工具启动配置失败。
    ///
    /// 输入：Agent 持有的不可变工具注册表。
    /// 输出：未配置时 None，完整自检通过时返回 Schema capability。
    /// 不变量：本方法不重新运行 backend、不重读配置；任一显式失败必须使 initialize 失败而非省略后继续。
    /// 失败：路径 hook 配置无效返回 `-32603/conformance_configuration_invalid`；sandbox profile/自检失败返回 `-32603/sandbox_unavailable`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn hard_sandbox_capability(&self) -> Result<Option<Value>, AgentError> {
        self.tools.hard_sandbox_capability()
    }

    /// 功能：验证并持久化一次 run 的输入与接受状态，但暂不启动异步执行。
    ///
    /// 输入：opaque Session/run ID、用户文本、已注册 Provider/model 和协商后的审批能力。
    /// 输出：成功时输入消息与 `run.accepted` 已 durable，活动控制对象已建立但任务尚未启动。
    /// 不变量：同一 daemon 只允许一个活动 run；调用方必须先发送 RPC 响应再调用 `start_run`。
    /// 失败：空输入、Provider 未配置、活动冲突、Session 恢复/锁或持久化失败时返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn accept_run(&self, request: &RunRequest) -> Result<(), AgentError> {
        if request.prompt.is_empty() {
            return Err(AgentError::new(
                ErrorCode::InvalidParams,
                "prompt must not be empty",
            ));
        }
        self.accept_run_content(
            request,
            vec![ContentBlock::Text {
                text: request.prompt.clone(),
            }],
        )
        .await
    }

    /// 功能：验证 portable text/image_ref 输入并 durable 接受一次显式 family 路由的 run。
    ///
    /// 输入：opaque run 参数和保持源顺序的 user content blocks。
    /// 输出：成功时输入消息与 `run.accepted` 已 durable，活动 run 等待响应 flush 后启动。
    /// 不变量：OpenRouter Images 必须显式选择 `openrouter-images`；image_ref 在追加 user message 前完成同 Session no-follow/hash/length/media 复核。
    /// 失败：family/model、内容、artifact、活动冲突、Session lock/recovery 或持久化失败返回结构化错误，Provider 网络尚未触发。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn accept_run_content(
        &self,
        request: &RunRequest,
        content: Vec<ContentBlock>,
    ) -> Result<(), AgentError> {
        self.accept_run_content_with_profile(request, content, None)
            .await
    }

    /// 功能：验证 portable 输入并以可选的安全 Agent Profile 快照 durable 接受运行。
    ///
    /// 输入：已验证 Provider run、源顺序 content blocks 与接受前冻结的可选 Profile 快照。
    /// 输出：用户消息、`run.accepted` 和等待启动的 RunControl 已建立。
    /// 不变量：快照只写入 run.accepted，不写成 Session system message；后续 Provider/tool 使用同一不可变快照。
    /// 失败：Provider、内容、artifact、Session、并发或持久化失败时不启动 Provider/tool。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn accept_run_content_with_profile(
        &self,
        request: &RunRequest,
        content: Vec<ContentBlock>,
        agent_profile: Option<AgentProfileRunSnapshot>,
    ) -> Result<(), AgentError> {
        if content.is_empty()
            || content.iter().any(|block| {
                !matches!(
                    block,
                    ContentBlock::Text { .. } | ContentBlock::ImageRef { .. }
                )
            })
            || !content.iter().any(|block| match block {
                ContentBlock::Text { text } => !text.is_empty(),
                ContentBlock::ImageRef { .. } => true,
                _ => false,
            })
        {
            return Err(AgentError::new(
                ErrorCode::InvalidParams,
                "run input content is invalid",
            ));
        }
        let provider = self.provider_for(request)?;
        let _acceptance = self.acceptance.lock().await;
        self.sessions
            .create_session(&request.session_id, &self.workspace)
            .await?;
        {
            let active = self.active.lock().await;
            if active.contains_key(&request.run_id)
                || active
                    .values()
                    .any(|control| control.session_id == request.session_id)
                || !active.is_empty()
            {
                return Err(AgentError::new(
                    ErrorCode::RunConflict,
                    "the daemon or session already has an active run",
                ));
            }
        }
        let lease = self.sessions.acquire_writer(&request.session_id).await?;
        self.sessions
            .recover_interrupted(&request.session_id)
            .await?;
        let _ = self.sessions.context_messages(&request.session_id).await?;
        let image_count = content
            .iter()
            .filter(|block| matches!(block, ContentBlock::ImageRef { .. }))
            .count();
        if image_count > 0 && !provider.supports_image_input() {
            return Err(AgentError::new(
                ErrorCode::InvalidParams,
                "selected provider does not accept image_ref",
            )
            .with_details(json!({"kind":"invalid_params","field":"input.content"})));
        }
        if image_count > MAX_PROVIDER_INPUT_IMAGES {
            return Err(AgentError::new(
                ErrorCode::OutputLimitExceeded,
                "provider image input count exceeded the limit",
            ));
        }
        let (single_image_limit, total_image_limit) =
            provider_image_input_limits(provider.as_ref());
        let mut total_image_bytes = 0_u64;
        for block in &content {
            if let ContentBlock::ImageRef { artifact, .. } = block {
                total_image_bytes = total_image_bytes
                    .checked_add(artifact.byte_length)
                    .ok_or_else(|| {
                        AgentError::new(
                            ErrorCode::OutputLimitExceeded,
                            "provider image input exceeded the byte limit",
                        )
                    })?;
                if total_image_bytes > total_image_limit {
                    return Err(AgentError::new(
                        ErrorCode::OutputLimitExceeded,
                        "provider image input exceeded the byte limit",
                    ));
                }
                let _ = self
                    .sessions
                    .read_verified_image_artifact(&request.session_id, artifact, single_image_limit)
                    .await?;
            }
        }
        let input_message_id = format!("message-{}", Uuid::new_v4());
        let portable_content = content
            .iter()
            .map(portable_content_block)
            .collect::<Vec<_>>();
        self.sessions
            .append_record(
                &request.session_id,
                "message.appended",
                json!({
                    "runId":request.run_id,
                    "message":{
                        "messageId":input_message_id,
                        "role":"user",
                        "content":portable_content,
                        "time":Utc::now()
                    }
                }),
            )
            .await?;
        let mut accepted = json!({
            "runId": request.run_id,
            "inputMessageId":input_message_id,
            "provider":run_provider_selection(request, provider.id())
        });
        if let Some(snapshot) = &agent_profile {
            accepted["agentProfileSnapshot"] = serde_json::to_value(snapshot).map_err(|_| {
                AgentError::new(
                    ErrorCode::InternalError,
                    "agent profile snapshot serialization failed",
                )
            })?;
        }
        self.sessions
            .append_record(&request.session_id, "run.accepted", accepted)
            .await?;

        let cancellation = CancellationToken::new();
        self.active.lock().await.insert(
            request.run_id.clone(),
            Arc::new(RunControl {
                session_id: request.session_id.clone(),
                cancellation: cancellation.clone(),
                _lease: lease,
                task_active: AtomicBool::new(false),
                usage: Mutex::new(Usage::default()),
                queues: Mutex::new(RunQueues {
                    accepting: true,
                    steering: VecDeque::new(),
                    follow_up: VecDeque::new(),
                }),
                lifecycle: Mutex::new(RunLifecycle::default()),
                approvals: Mutex::new(BTreeMap::new()),
                agent_profile,
            }),
        );
        Ok(())
    }

    /// 功能：在 run durable barrier 前确认 Profile 模型仍有真实可执行 adapter。
    ///
    /// 输入：已与 run 三元身份匹配的 Profile 快照和待接受 RunRequest。
    /// 输出：Provider family/model/credential 当前可用时成功。
    /// 不变量：不发起网络、不读取 credential 值、不创建 Session；错误仅回显公开模型身份。
    /// 失败：route/model/credential 不可执行时返回 fixture 冻结的可重试 `agent_profile_model_unavailable`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn ensure_profile_model_available(
        &self,
        request: &RunRequest,
        snapshot: &AgentProfileRunSnapshot,
    ) -> Result<(), AgentError> {
        match self.provider_for(request) {
            Ok(_) => Ok(()),
            Err(error)
                if matches!(
                    error.code,
                    ErrorCode::InvalidParams | ErrorCode::ProviderUnavailable
                ) =>
            {
                Err(AgentError::new(
                    ErrorCode::ProviderUnavailable,
                    "agent profile model is unavailable",
                )
                .retryable(true)
                .with_details(json!({
                    "kind":"agent_profile_model_unavailable",
                    "providerId":snapshot.model.provider_id,
                    "modelId":snapshot.model.model_id,
                    "apiFamily":snapshot.model.api_family
                })))
            }
            Err(error) => Err(error),
        }
    }

    /// 功能：按 Provider ID、显式 API family 与冻结 modelId 选择唯一运行时 adapter。
    ///
    /// 输入：run 的品牌清单 ID、可选 family 和 modelId。
    /// 输出：三者与已注册 adapter 精确匹配时返回共享 Provider。
    /// 不变量：`openrouter-images` 永不由 modelId 猜测或在缺失 family 时回退；不执行网络请求。
    /// 失败：Provider 未配置、family 缺失/不匹配或 model 不在目录时返回 `InvalidParams`；
    /// route credential 在接受前失效时返回 `ProviderUnavailable`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn provider_for(&self, request: &RunRequest) -> Result<&Arc<dyn Provider>, AgentError> {
        let adapter_api_family =
            if request.provider_id == "faux" && request.api_family.as_deref() == Some("faux") {
                None
            } else {
                request.api_family.as_deref()
            };
        let mut matches = self.providers.values().filter(|provider| {
            provider.id() == request.provider_id && provider.api_family() == adapter_api_family
        });
        let provider = matches.next().ok_or_else(|| {
            AgentError::new(
                ErrorCode::InvalidParams,
                "provider API family does not match a configured route",
            )
            .with_details(json!({"kind":"invalid_params","field":"provider.apiFamily"}))
        })?;
        if matches.next().is_some() {
            return Err(AgentError::new(
                ErrorCode::InternalError,
                "provider route registration is ambiguous",
            )
            .with_details(json!({"kind":"ambiguous_provider_route"})));
        }
        if !provider.supports_model(&request.model) {
            return Err(AgentError::new(
                ErrorCode::InvalidParams,
                "provider model is not in the supported catalog",
            )
            .with_details(json!({"kind":"invalid_params","field":"provider.modelId"})));
        }
        if !provider.is_available() {
            return Err(AgentError::new(
                ErrorCode::ProviderUnavailable,
                "provider route is unavailable",
            )
            .with_details(json!({"kind":"provider_unavailable"})));
        }
        Ok(provider)
    }

    /// 功能：校验并 durable 接受客户端审批决定，返回必须在 RPC 响应后执行的恢复句柄。
    ///
    /// 输入：Session、活动 run、不透明 approval ID 以及客户端选择。
    /// 输出：成功时返回一次性 `ApprovalResume`；调用方必须先写出并 flush 成功响应再恢复。
    /// 不变量：审批决定只接受一次；`approval.resolved` 必须先持久化，且本方法绝不直接执行工具。
    /// 失败：run/Session 不匹配、审批不存在/过期/重复、选择不在请求 choices 内或持久化失败时返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) async fn accept_approval(
        &self,
        session_id: &str,
        run_id: &str,
        approval_id: &str,
        decision: ApprovalDecision,
    ) -> Result<ApprovalResume, AgentError> {
        if !matches!(decision.choice.as_str(), "allow_once" | "deny") {
            return Err(AgentError::new(
                ErrorCode::InvalidParams,
                "approval decision is not one of the offered choices",
            )
            .with_details(json!({"kind":"invalid_params","field":"decision.choice"})));
        }
        let active = self.active.lock().await;
        let control = active.get(run_id).cloned().ok_or_else(approval_conflict)?;
        drop(active);
        if control.session_id != session_id {
            return Err(approval_conflict());
        }
        let lifecycle = control.lifecycle.lock().await;
        if lifecycle.cancellation_requested || lifecycle.terminal {
            return Err(approval_conflict());
        }
        let decision = serde_json::to_value(decision)
            .map_err(|error| AgentError::new(ErrorCode::InternalError, error.to_string()))?;
        let mut approvals = control.approvals.lock().await;
        let deadline = approvals.get(approval_id).map(|pending| pending.deadline);
        let Some(deadline) = deadline else {
            return Err(approval_conflict());
        };
        if tokio::time::Instant::now() >= deadline {
            let timeout_decision = json!({"choice":"deny"});
            self.sessions
                .append_record(
                    session_id,
                    "approval.resolved",
                    json!({
                        "runId":run_id,
                        "approvalId":approval_id,
                        "decision":timeout_decision,
                        "resolutionSource":"timeout"
                    }),
                )
                .await?;
            let pending = approvals.remove(approval_id).ok_or_else(|| {
                AgentError::new(
                    ErrorCode::InternalError,
                    "expired pending approval disappeared",
                )
            })?;
            let _ = pending
                .sender
                .send(ApprovalWaitOutcome::Released(ApprovalResolution {
                    approval_id: approval_id.to_owned(),
                    decision: timeout_decision,
                    resolution_source: "timeout",
                }));
            return Err(approval_conflict());
        }
        self.sessions
            .append_record(
                session_id,
                "approval.resolved",
                json!({
                    "runId":run_id,
                    "approvalId":approval_id,
                    "decision":decision,
                    "resolutionSource":"client"
                }),
            )
            .await?;
        let pending = approvals.remove(approval_id).ok_or_else(|| {
            AgentError::new(ErrorCode::InternalError, "pending approval disappeared")
        })?;
        Ok(ApprovalResume {
            sender: Some(pending.sender),
            resolution: Some(ApprovalResolution {
                approval_id: approval_id.to_owned(),
                decision,
                resolution_source: "client",
            }),
        })
    }

    /// 功能：在接受响应已经写出后启动异步 Agent loop，并保证最终清理活动 run。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// 输入必须先通过 `accept_run`；若活动状态意外缺失，则持久化并发送失败终态。
    pub async fn start_run(
        self: &Arc<Self>,
        request: RunRequest,
        events: mpsc::Sender<EventEnvelope>,
    ) {
        let control = self.active.lock().await.get(&request.run_id).cloned();
        let Some(control) = control else {
            let error = AgentError::new(
                ErrorCode::RunNotFound,
                "accepted run is missing from active state",
            );
            let _ = self
                .emit(
                    &events,
                    &request.session_id,
                    &request.run_id,
                    None,
                    "run.failed",
                    json!({"status":"failed","error":portable_error(&error)}),
                )
                .await;
            return;
        };
        control.task_active.store(true, Ordering::Release);
        let agent = Arc::clone(self);
        tokio::spawn(async move {
            let result = agent
                .execute_run(&request, &events, Arc::clone(&control))
                .await;
            control.queues.lock().await.accepting = false;
            let _ = agent
                .finalize_run(&request, &control, &events, result.as_ref().err())
                .await;
            control.task_active.store(false, Ordering::Release);
            agent.active.lock().await.remove(&request.run_id);
        });
    }

    /// 功能：在取消与自然完成共用的原子仲裁下持久化并发送唯一 run 终态。
    ///
    /// 输入：run/control、事件通道和 Agent loop 的可选结构化错误。
    /// 输出：终态 canonical record 与事件已 durable；已有 winner 时无重复输出。
    /// 不变量：持有 lifecycle 锁直至终态事件落盘；已接受取消优先产生 cancelled，之后的 cancel 只能观察 terminal。
    /// 失败：Session I/O、序列化或事件持久化失败时返回结构化错误且不伪造第二终态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn finalize_run(
        &self,
        request: &RunRequest,
        control: &RunControl,
        events: &mpsc::Sender<EventEnvelope>,
        error: Option<&AgentError>,
    ) -> Result<(), AgentError> {
        let mut lifecycle = control.lifecycle.lock().await;
        if lifecycle.terminal {
            return Ok(());
        }
        let usage = control.usage.lock().await.clone();
        let cancellation_won = lifecycle.cancellation_requested
            || error.is_some_and(|error| error.code == ErrorCode::Cancelled);
        let (event_type, data) = if cancellation_won {
            (
                "run.cancelled",
                json!({"status":"cancelled","reason":"run cancelled","usage":usage}),
            )
        } else if let Some(error) = error {
            if error.code == ErrorCode::StreamInterrupted {
                (
                    "run.interrupted",
                    json!({"status":"interrupted","error":portable_error(error),"usage":usage}),
                )
            } else {
                (
                    "run.failed",
                    json!({"status":"failed","error":portable_error(error),"usage":usage}),
                )
            }
        } else {
            ("run.completed", json!({"status":"completed","usage":usage}))
        };
        self.emit(
            events,
            &request.session_id,
            &request.run_id,
            None,
            event_type,
            data,
        )
        .await?;
        lifecycle.terminal = true;
        Ok(())
    }

    /// 功能：幂等请求取消指定 run，并返回规范化取消状态。
    ///
    /// 输入：必须精确关联的 Session ID 与 run ID。
    /// 输出：活动 run 返回 `requested`/`alreadyRequested`，已终止 run 返回 `terminal`。
    /// 不变量：取消意图最多 durable 一次；等待中的审批先以 cancellation 来源持久化并唤醒，随后才触发 token；已接受取消后不会再启动工具。
    /// 失败：Session/run 不匹配、不存在、journal 加锁或持久化失败时返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn cancel(&self, session_id: &str, run_id: &str) -> Result<&'static str, AgentError> {
        let active = self.active.lock().await;
        if let Some(control) = active.get(run_id).cloned() {
            drop(active);
            if control.session_id != session_id {
                return Err(AgentError::new(
                    ErrorCode::RunNotFound,
                    "run does not belong to the supplied session",
                ));
            }
            let mut lifecycle = control.lifecycle.lock().await;
            if lifecycle.terminal {
                return Ok("terminal");
            }
            if lifecycle.cancellation_requested {
                return Ok("alreadyRequested");
            }
            let mut queues = control.queues.lock().await;
            self.sessions
                .append_record(
                    session_id,
                    "run.cancellation_requested",
                    json!({"runId":run_id}),
                )
                .await?;
            lifecycle.cancellation_requested = true;
            queues.accepting = false;
            drop(queues);

            let mut approvals = control.approvals.lock().await;
            let approval_ids = approvals.keys().cloned().collect::<Vec<_>>();
            let cancellation_decision = json!({"choice":"deny"});
            for approval_id in &approval_ids {
                self.sessions
                    .append_record(
                        session_id,
                        "approval.resolved",
                        json!({
                            "runId":run_id,
                            "approvalId":approval_id,
                            "decision":cancellation_decision,
                            "resolutionSource":"cancellation"
                        }),
                    )
                    .await?;
            }
            for approval_id in approval_ids {
                if let Some(pending) = approvals.remove(&approval_id) {
                    let _ =
                        pending
                            .sender
                            .send(ApprovalWaitOutcome::Released(ApprovalResolution {
                                approval_id,
                                decision: cancellation_decision.clone(),
                                resolution_source: "cancellation",
                            }));
                }
            }
            drop(approvals);
            control.cancellation.cancel();
            return Ok("requested");
        }
        drop(active);
        let records = self.sessions.load(session_id).await?;
        let accepted = records.iter().any(|record| {
            record.kind == "run.accepted" && record.data["runId"].as_str() == Some(run_id)
        });
        if accepted {
            Ok("terminal")
        } else {
            Err(AgentError::new(ErrorCode::RunNotFound, "run not found"))
        }
    }

    /// 功能：在 stdio 客户端断开时 durable 否决所有尚未决议的交互审批并唤醒 run。
    ///
    /// 输入：当前 Agent 内全部活动 run；调用方无需提供客户端可控 source。
    /// 输出：成功解析并发送的 pending approval 数量。
    /// 不变量：与客户端响应、超时、取消和工具启动共用 lifecycle/approval 锁；每个 approval 只追加一个 resolution，source 固定为 disconnect。
    /// 失败：任一 Session journal 追加失败时返回结构化错误；已经由其他 winner 移除的审批不会重复处理。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) async fn resolve_disconnected_approvals(&self) -> Result<usize, AgentError> {
        self.approval_responder_connected
            .store(false, Ordering::Release);
        let controls = self
            .active
            .lock()
            .await
            .iter()
            .map(|(run_id, control)| (run_id.clone(), Arc::clone(control)))
            .collect::<Vec<_>>();
        let mut resolved = 0;
        for (run_id, control) in controls {
            let lifecycle = control.lifecycle.lock().await;
            if lifecycle.terminal || lifecycle.cancellation_requested {
                continue;
            }
            let mut approvals = control.approvals.lock().await;
            let approval_ids = approvals.keys().cloned().collect::<Vec<_>>();
            let decision = json!({"choice":"deny"});
            for approval_id in &approval_ids {
                self.sessions
                    .append_record(
                        &control.session_id,
                        "approval.resolved",
                        json!({
                            "runId":run_id,
                            "approvalId":approval_id,
                            "decision":decision,
                            "resolutionSource":"disconnect"
                        }),
                    )
                    .await?;
            }
            for approval_id in approval_ids {
                if let Some(pending) = approvals.remove(&approval_id) {
                    let _ =
                        pending
                            .sender
                            .send(ApprovalWaitOutcome::Released(ApprovalResolution {
                                approval_id,
                                decision: decision.clone(),
                                resolution_source: "disconnect",
                            }));
                    resolved += 1;
                }
            }
        }
        Ok(resolved)
    }

    /// 功能：在 transport 故障收尾时有界等待已经启动的 Agent task 写完唯一终态。
    ///
    /// 输入：可信 daemon 提供的正等待上限。
    /// 输出：全部已启动 task 在期限内结束时为 true；超时为 false。
    /// 不变量：未经过 run/start success barrier、`task_active=false` 的 accepted run 不参与等待，
    /// 因而仍由下次 Session 恢复处理；本方法不取消、恢复或执行任何工具。
    /// 失败：本方法不返回错误；锁竞争或慢存储超过期限时保守返回 false。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) async fn wait_for_started_runs(&self, timeout: Duration) -> bool {
        tokio::time::timeout(timeout, async {
            loop {
                let running = self
                    .active
                    .lock()
                    .await
                    .values()
                    .any(|control| control.task_active.load(Ordering::Acquire));
                if !running {
                    return;
                }
                tokio::time::sleep(Duration::from_millis(5)).await;
            }
        })
        .await
        .is_ok()
    }

    /// 功能：恢复静止 Session 后返回完整消息快照、durable 事件增量和真实活动 task。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// `afterSeq` 只过滤事件；无本进程活动 task 时先独占恢复未终止 run 和歧义工具，
    /// 且恢复绝不调用 Provider 或工具。被另一进程持锁的 Session 返回可重试冲突。
    pub async fn session_get(
        &self,
        session_id: &str,
        after_seq: u64,
    ) -> Result<SessionSnapshot, AgentError> {
        let mut active_run_id = self.active_task_for_session(session_id).await;
        if active_run_id.is_none() {
            let _ = self.sessions.header(session_id).await?;
            let lease = self.sessions.acquire_writer(session_id).await?;
            self.sessions.recover_interrupted(session_id).await?;
            drop(lease);
            active_run_id = self.active_task_for_session(session_id).await;
        }
        self.sessions
            .snapshot(session_id, after_seq, active_run_id)
            .await
    }

    /// 功能：在静止 Session 上完成恢复后以 expected-head CAS durable 选择 earlier branch。
    ///
    /// 输入：严格解码的 Session、expected head 和 target record ID。
    /// 输出：target、selection record 和新 selected head 身份。
    /// 不变量：与 run acceptance 共用互斥；live run 返回 busy；writer lease 覆盖恢复、比较和追加。
    /// 失败：Session 不存在/锁定、busy、stale、target 不存在/非静止或 journal 损坏返回冻结错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn select_session_branch(
        &self,
        params: SessionBranchSelectParams,
    ) -> Result<BranchSelectionResult, AgentError> {
        let _acceptance = self.acceptance.lock().await;
        let _ = self.sessions.header(&params.session_id).await?;
        self.ensure_session_mutation_quiescent(&params.session_id)
            .await?;
        let lease = self.sessions.acquire_writer(&params.session_id).await?;
        self.sessions
            .recover_interrupted(&params.session_id)
            .await?;
        self.sessions
            .select_branch(
                &lease,
                &params.expected_head_record_id,
                &params.target_leaf_record_id,
            )
            .await
    }

    /// 功能：在静止 Session 上完成恢复后 durable 追加 summary 与 context.compacted。
    ///
    /// 输入：严格解码的 expected head、retained boundary、summary、Provider/usage 和 token 策略。
    /// 输出：summary message/record、compaction record 与新 selected head 身份。
    /// 不变量：与 run acceptance 共用互斥；writer lease 覆盖恢复、CAS、两次 flush 和成功返回。
    /// 失败：Session 不存在/锁定、busy、stale、参数/边界、journal 或 I/O 违规返回冻结错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn compact_session(
        &self,
        params: SessionCompactParams,
    ) -> Result<ContextCompactionResult, AgentError> {
        let provider = serde_json::to_value(&params.provider)
            .map_err(|error| AgentError::new(ErrorCode::InvalidParams, error.to_string()))?;
        let usage = serde_json::to_value(&params.usage)
            .map_err(|error| AgentError::new(ErrorCode::InvalidParams, error.to_string()))?;
        let _acceptance = self.acceptance.lock().await;
        let _ = self.sessions.header(&params.session_id).await?;
        self.ensure_session_mutation_quiescent(&params.session_id)
            .await?;
        let lease = self.sessions.acquire_writer(&params.session_id).await?;
        self.sessions
            .recover_interrupted(&params.session_id)
            .await?;
        self.sessions
            .compact_context(
                &lease,
                ContextCompactionInput {
                    expected_head_record_id: params.expected_head_record_id,
                    first_retained_record_id: params.first_retained_record_id,
                    summary_text: params.summary_text,
                    provider,
                    usage,
                    tokens_before: params.tokens_before,
                    tokens_after: params.tokens_after,
                    strategy: params.strategy,
                },
            )
            .await
    }

    /// 功能：拒绝当前进程仍持有 accepted/active run 的 Session mutation。
    ///
    /// 输入：目标 Session ID。
    /// 输出：没有本进程 live RunControl 时成功。
    /// 不变量：检查由 acceptance mutex 包围，新 run 不能在检查与 writer acquisition 间插入。
    /// 失败：存在任意该 Session RunControl 时返回 -32004/session_busy/retryable。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) async fn ensure_session_mutation_quiescent(
        &self,
        session_id: &str,
    ) -> Result<(), AgentError> {
        if self
            .active
            .lock()
            .await
            .values()
            .any(|control| control.session_id == session_id)
        {
            return Err(session_busy_error());
        }
        Ok(())
    }

    /// 功能：查找指定 Session 当前确已启动且尚未退出的本进程 Agent task。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// 仅凭 durable `run.accepted` 不会产生 activeRunId；任务启动前和退出后均返回空。
    async fn active_task_for_session(&self, session_id: &str) -> Option<String> {
        self.active
            .lock()
            .await
            .iter()
            .find(|(_, control)| {
                control.session_id == session_id && control.task_active.load(Ordering::Acquire)
            })
            .map(|(run_id, _)| run_id.clone())
    }

    /// 功能：持久化并按 FIFO 将 steering 或 follow-up 用户输入加入活动 run。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// 成功返回前 `queue.appended` 已同步；不存在、终止或 Session 不匹配的 run 被拒绝。
    pub async fn enqueue(
        &self,
        session_id: &str,
        run_id: &str,
        text: String,
        kind: QueueKind,
    ) -> Result<String, AgentError> {
        if text.is_empty() {
            return Err(AgentError::new(
                ErrorCode::InvalidParams,
                "queued input must not be empty",
            ));
        }
        let control = self
            .active
            .lock()
            .await
            .get(run_id)
            .cloned()
            .ok_or_else(|| AgentError::new(ErrorCode::RunNotFound, "active run not found"))?;
        if control.session_id != session_id {
            return Err(AgentError::new(
                ErrorCode::RunNotFound,
                "run does not belong to supplied session",
            ));
        }
        let queue_item_id = format!("queue-{}", Uuid::new_v4());
        let queue_name = match kind {
            QueueKind::Steering => "steering",
            QueueKind::FollowUp => "follow_up",
        };
        let mut queues = control.queues.lock().await;
        if !queues.accepting {
            return Err(AgentError::new(
                ErrorCode::RunNotFound,
                "active run no longer accepts queued input",
            ));
        }
        let queue_length = match kind {
            QueueKind::Steering => queues.steering.len(),
            QueueKind::FollowUp => queues.follow_up.len(),
        };
        if queue_length >= MAX_QUEUED_INPUTS {
            return Err(AgentError::new(
                ErrorCode::OutputLimitExceeded,
                "queued input count exceeds per-run limit",
            ));
        }
        self.sessions
            .append_record(
                session_id,
                "queue.appended",
                json!({
                    "runId":run_id,
                    "queueItemId":queue_item_id,
                    "queue":queue_name,
                    "input":{"role":"user","content":[{"type":"text","text":text}]}
                }),
            )
            .await?;
        let queued = QueuedInput {
            queue_item_id: queue_item_id.clone(),
            text,
        };
        match kind {
            QueueKind::Steering => queues.steering.push_back(queued),
            QueueKind::FollowUp => queues.follow_up.push_back(queued),
        }
        Ok(queue_item_id)
    }

    /// 功能：为 conformance faux 场景创建 Session，并在确认配置前持久化场景事实。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// 本方法只记录脱敏的确定性测试场景，不接受或持久化 Provider 密钥。
    pub async fn record_faux_configuration(
        &self,
        session_id: &str,
        scenario: &crate::provider::FauxScenario,
    ) -> Result<(), AgentError> {
        self.sessions
            .create_session(session_id, &self.workspace)
            .await?;
        let _lease = self.sessions.acquire_writer(session_id).await?;
        self.sessions
            .append_record(
                session_id,
                "faux.configured",
                json!({
                    "scenarioId":format!("faux:{}",scenario.name),
                    "scenario":serde_json::to_value(scenario).map_err(|error| {
                        AgentError::new(ErrorCode::InvalidParams, error.to_string())
                    })?
                }),
            )
            .await
            .map(|_| ())
    }

    /// 功能：从本轮 selected context 收集并重新复核 Provider 可接收的用户图片引用。
    ///
    /// 输入：当前 Session ID、有序上下文消息和已选择 route。
    /// 输出：按首次用户引用顺序排列、以 artifact ID 去重的请求期图片字节。
    /// 不变量：不支持图片的 route 返回空集合；assistant 输出 image_ref 只作为 Session 历史展示而不会回送为模型输入；
    /// 每个用户引用都来自同 Session selected chain，字节不进入 Session、事件或日志。
    /// 失败：数量、单图/总字节、重复引用元数据、durable 归属、hash、MIME、魔数或文件形状无效时失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn resolve_provider_images(
        &self,
        session_id: &str,
        messages: &[Message],
        provider: &dyn Provider,
    ) -> Result<Vec<ProviderResolvedImage>, AgentError> {
        if !provider.supports_image_input() {
            return Ok(Vec::new());
        }
        let (single_image_limit, total_image_limit) = provider_image_input_limits(provider);
        let mut references = Vec::<ArtifactRef>::new();
        let mut total_bytes = 0_u64;
        for artifact in messages
            .iter()
            .filter(|message| message.role == Role::User)
            .flat_map(|message| {
                message.content.iter().filter_map(|block| match block {
                    ContentBlock::ImageRef { artifact, .. } => Some(artifact),
                    _ => None,
                })
            })
        {
            if references.len() >= MAX_PROVIDER_INPUT_IMAGES {
                return Err(AgentError::new(
                    ErrorCode::OutputLimitExceeded,
                    "provider image input count exceeded the limit",
                ));
            }
            total_bytes = total_bytes
                .checked_add(artifact.byte_length)
                .ok_or_else(|| {
                    AgentError::new(
                        ErrorCode::OutputLimitExceeded,
                        "provider image input exceeded the byte limit",
                    )
                })?;
            if total_bytes > total_image_limit {
                return Err(AgentError::new(
                    ErrorCode::OutputLimitExceeded,
                    "provider image input exceeded the byte limit",
                ));
            }
            references.push(artifact.clone());
        }

        let mut seen = BTreeMap::<String, ArtifactRef>::new();
        let mut resolved = Vec::new();
        for reference in references {
            if let Some(existing) = seen.get(&reference.artifact_id) {
                if existing != &reference {
                    return Err(AgentError::new(
                        ErrorCode::ProviderError,
                        "provider image input artifact is invalid",
                    )
                    .with_details(json!({"kind":"provider_input_artifact_invalid"})));
                }
                continue;
            }
            let bytes = self
                .sessions
                .read_verified_image_artifact(session_id, &reference, single_image_limit)
                .await
                .map_err(|_| {
                    AgentError::new(
                        ErrorCode::ProviderError,
                        "provider image input artifact is invalid",
                    )
                    .with_details(json!({"kind":"provider_input_artifact_invalid"}))
                })?;
            seen.insert(reference.artifact_id.clone(), reference.clone());
            resolved.push(ProviderResolvedImage { reference, bytes });
        }
        Ok(resolved)
    }

    /// 功能：执行一个已接受 run 的完整 turn/tool 循环直至唯一终态。
    ///
    /// 输入：已 durable 接受的 run、事件通道和独占 `RunControl`。
    /// 输出：正常完成返回成功；失败/取消由调用方转换为唯一 run 终态事件。
    /// 不变量：所有可观察生命周期事件均先持久化；工具按 Provider 源顺序串行；取消在异步边界检查。
    /// 失败：Provider、Session、工具状态机、队列或取消错误以结构化 `AgentError` 返回。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn execute_run(
        &self,
        request: &RunRequest,
        events: &mpsc::Sender<EventEnvelope>,
        control: Arc<RunControl>,
    ) -> Result<(), AgentError> {
        let cancellation = control.cancellation.clone();
        let mut total_usage = Usage::default();
        self.emit(
            events,
            &request.session_id,
            &request.run_id,
            None,
            "run.started",
            json!({}),
        )
        .await?;
        let mut messages = self
            .sessions
            .context_messages(&request.session_id)
            .await?
            .iter()
            .map(provider_message_from_portable)
            .collect::<Result<Vec<_>, _>>()?;
        let system_instructions = control
            .agent_profile
            .as_ref()
            .map(AgentProfileRunSnapshot::system_message_text);
        let mut seen_tool_call_ids = BTreeSet::<String>::new();

        for _turn_index in 0..MAX_TOOL_TURNS {
            if cancellation.is_cancelled() {
                return Err(AgentError::new(ErrorCode::Cancelled, "run cancelled"));
            }
            let turn_id = format!("turn-{}", Uuid::new_v4());
            self.emit(
                events,
                &request.session_id,
                &request.run_id,
                Some(&turn_id),
                "turn.started",
                json!({}),
            )
            .await?;
            let provider = self.provider_for(request)?;
            let tools = if provider.supports_tools() {
                self.tools
                    .definitions()
                    .into_iter()
                    .filter(|definition| {
                        control
                            .agent_profile
                            .as_ref()
                            .is_none_or(|profile| profile.permits_tool(&definition.name))
                    })
                    .collect()
            } else {
                Vec::new()
            };
            let resolved_images = self
                .resolve_provider_images(&request.session_id, &messages, provider.as_ref())
                .await?;
            let provider_request = ProviderRequest {
                session_id: Some(request.session_id.clone()),
                run_id: Some(request.run_id.clone()),
                model: request.model.clone(),
                system_instructions: system_instructions.clone(),
                messages: messages.clone(),
                tools,
                max_output_tokens: None,
                resolved_images,
            };
            let mut attempt = 1_u32;
            let stream = loop {
                self.persist_provider_attempt(request, &turn_id, attempt, "started", None, None)
                    .await?;
                match provider
                    .stream(provider_request.clone(), cancellation.child_token())
                    .await
                {
                    Ok(stream) => break stream,
                    Err(error)
                        if attempt < provider_max_attempts()
                            && should_retry_provider_start(&error) =>
                    {
                        let next_attempt = attempt + 1;
                        let delay = provider_retry_delay(&error, attempt);
                        self.persist_provider_attempt(
                            request,
                            &turn_id,
                            attempt,
                            "failed",
                            Some(&error),
                            Some(delay),
                        )
                        .await?;
                        self.emit(
                            events,
                            &request.session_id,
                            &request.run_id,
                            Some(&turn_id),
                            "retry.scheduled",
                            json!({
                                "attempt":next_attempt,
                                "delayMs":u64::try_from(delay.as_millis()).unwrap_or(u64::MAX),
                                "reason":portable_error(&error)
                            }),
                        )
                        .await?;
                        tokio::select! {
                            biased;
                            () = cancellation.cancelled() => {
                                return Err(AgentError::new(ErrorCode::Cancelled, "run cancelled"));
                            }
                            () = tokio::time::sleep(delay) => {}
                        }
                        attempt = next_attempt;
                    }
                    Err(error) => {
                        self.persist_provider_attempt(
                            request,
                            &turn_id,
                            attempt,
                            provider_attempt_error_status(&error),
                            Some(&error),
                            None,
                        )
                        .await?;
                        return Err(error);
                    }
                }
            };
            let outcome_result = self
                .consume_provider_stream(
                    request,
                    events,
                    &turn_id,
                    stream,
                    &cancellation,
                    &seen_tool_call_ids,
                )
                .await;
            let outcome = match outcome_result {
                Ok(outcome) => {
                    self.persist_provider_attempt(
                        request,
                        &turn_id,
                        attempt,
                        "completed",
                        None,
                        None,
                    )
                    .await?;
                    outcome
                }
                Err(error) => {
                    self.persist_provider_attempt(
                        request,
                        &turn_id,
                        attempt,
                        provider_attempt_error_status(&error),
                        Some(&error),
                        None,
                    )
                    .await?;
                    return Err(error);
                }
            };
            for call in &outcome.tool_calls {
                seen_tool_call_ids.insert(call.call_id.clone());
            }
            total_usage.input_tokens = total_usage
                .input_tokens
                .saturating_add(outcome.usage.input_tokens);
            total_usage.output_tokens = total_usage
                .output_tokens
                .saturating_add(outcome.usage.output_tokens);
            total_usage.total_tokens = total_usage
                .total_tokens
                .saturating_add(outcome.usage.total_tokens);
            total_usage.cached_input_tokens = total_usage
                .cached_input_tokens
                .saturating_add(outcome.usage.cached_input_tokens);
            if let Some(cost_micros) = outcome.usage.cost_micros {
                total_usage.cost_micros = Some(
                    total_usage
                        .cost_micros
                        .unwrap_or_default()
                        .saturating_add(cost_micros),
                );
            }
            *control.usage.lock().await = total_usage.clone();
            messages.push(outcome.assistant);

            if outcome.finish_reason != FinishReason::ToolCalls {
                if self
                    .drain_completion_queue(request, &control, &turn_id, &mut messages)
                    .await?
                {
                    continue;
                }
                return Ok(());
            }

            // v0.1 规定按 Provider 完成顺序串行执行，确保不同语言生成相同轨迹。
            let mut batch_cancelled = false;
            for call in outcome.tool_calls {
                let tool_outcome = self
                    .execute_tool_call(request, &control, events, &turn_id, call)
                    .await?;
                messages.push(tool_outcome.message);
                batch_cancelled |= tool_outcome.cancelled;
            }
            let lifecycle = control.lifecycle.lock().await;
            batch_cancelled |= lifecycle.cancellation_requested;
            self.emit(
                events,
                &request.session_id,
                &request.run_id,
                Some(&turn_id),
                "turn.completed",
                json!({
                    "finishReason":if batch_cancelled {"cancelled"} else {"tool_use"}
                }),
            )
            .await?;
            drop(lifecycle);
            if batch_cancelled {
                return Err(AgentError::new(ErrorCode::Cancelled, "run cancelled"));
            }
            let _ = self
                .drain_queue(
                    request,
                    &control,
                    QueueKind::Steering,
                    &turn_id,
                    &mut messages,
                )
                .await?;
        }

        Err(AgentError::new(
            ErrorCode::ProviderError,
            "agent exceeded maximum tool turns",
        ))
    }

    /// 功能：按预检、策略、审批、执行和 durable 完成顺序处理单个工具调用。
    ///
    /// 输入：所属 run/control、事件通道、turn ID 与 Provider 已完整解析的工具调用。
    /// 输出：供下一 Provider turn 使用的 canonical 工具消息，以及是否因取消终止当前 run。
    /// 不变量：每次调用只有一条 immutable `tool.intent` 和一条 `tool.result`；任何可观察事件均后于其前置记录；拒绝、否决或取消绝不发 `tool.started`。
    /// 失败：Session I/O、事件持久化、审批通道异常或授权后的工具执行基础设施失败会转成结构化结果或 Agent 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn execute_tool_call(
        &self,
        request: &RunRequest,
        control: &RunControl,
        events: &mpsc::Sender<EventEnvelope>,
        turn_id: &str,
        call: CompleteToolCall,
    ) -> Result<ToolCallOutcome, AgentError> {
        let preflight = if control
            .agent_profile
            .as_ref()
            .is_some_and(|profile| !profile.permits_tool(&call.name))
        {
            Err(AgentError::new(
                ErrorCode::ToolNotFound,
                "tool is not enabled for this agent profile",
            )
            .with_details(json!({"kind":"tool_not_found","toolName":call.name})))
        } else {
            self.tools.preflight(&call.name, &call.arguments)
        };
        let cancelled_before_intent = {
            let lifecycle = control.lifecycle.lock().await;
            lifecycle.cancellation_requested || lifecycle.terminal
        };
        let (effect, decision, mut status) = match preflight.as_ref() {
            Ok(effect) => {
                let decision = self.tools.policy_decision(
                    *effect,
                    request.interactive_approvals
                        && self.approval_responder_connected.load(Ordering::Acquire),
                );
                let status = match decision {
                    PolicyDecision::Allow => "started",
                    PolicyDecision::RequireApproval => "awaiting_approval",
                    PolicyDecision::Deny => "denied",
                };
                (Some(*effect), Some(decision), status)
            }
            Err(_) => (None, None, "rejected"),
        };
        if cancelled_before_intent {
            status = "rejected";
        }
        let operation_hash = if decision == Some(PolicyDecision::RequireApproval) {
            Some(operation_hash(&call.name, &call.arguments)?)
        } else {
            None
        };
        let mut intent = json!({
            "runId":request.run_id,
            "turnId":turn_id,
            "toolCallId":call.call_id,
            "name":call.name,
            "arguments":call.arguments,
            "idempotent":self.tools.is_idempotent(&call.name),
            "status":status
        });
        if let Some(operation_hash) = operation_hash.as_ref() {
            intent["operationHash"] = Value::String(operation_hash.clone());
        }
        self.sessions
            .append_record(&request.session_id, "tool.intent", intent)
            .await?;
        self.emit(
            events,
            &request.session_id,
            &request.run_id,
            Some(turn_id),
            "tool.requested",
            json!({
                "toolCallId":call.call_id,
                "name":call.name,
                "arguments":call.arguments
            }),
        )
        .await?;

        let mut cancelled = false;
        let mut delivery_failure = None;
        let execution = if cancelled_before_intent {
            cancelled = true;
            Err(cancelled_tool_error(&call.name))
        } else {
            match (preflight, decision) {
                (Err(error), _) => Err(error),
                (Ok(_), Some(PolicyDecision::Deny)) => Err(AgentError::new(
                    ErrorCode::PermissionDenied,
                    format!("headless policy denied tool '{}'", call.name),
                )),
                (Ok(_), Some(PolicyDecision::Allow)) => {
                    let lifecycle = control.lifecycle.lock().await;
                    if lifecycle.cancellation_requested || lifecycle.terminal {
                        cancelled = true;
                        drop(lifecycle);
                        Err(cancelled_tool_error(&call.name))
                    } else {
                        self.emit(
                            events,
                            &request.session_id,
                            &request.run_id,
                            Some(turn_id),
                            "tool.started",
                            json!({"toolCallId":call.call_id,"name":call.name}),
                        )
                        .await?;
                        drop(lifecycle);
                        self.tools
                            .execute_authorized_for_call(
                                &request.session_id,
                                Some(&request.run_id),
                                &call.call_id,
                                &call.name,
                                &call.arguments,
                                control.cancellation.child_token(),
                            )
                            .await
                    }
                }
                (Ok(_), Some(PolicyDecision::RequireApproval)) => {
                    let approval_id = format!("approval-{}", Uuid::new_v4());
                    let approval = approval_request(
                        &approval_id,
                        &call,
                        effect.ok_or_else(|| {
                            AgentError::new(ErrorCode::InternalError, "approval effect is missing")
                        })?,
                        operation_hash.as_deref().ok_or_else(|| {
                            AgentError::new(
                                ErrorCode::InternalError,
                                "approval operation hash is missing",
                            )
                        })?,
                        self.approval_timeout,
                    );
                    let (sender, receiver) = oneshot::channel();
                    let deadline = tokio::time::Instant::now() + self.approval_timeout;
                    let lifecycle = control.lifecycle.lock().await;
                    let mut approvals = control.approvals.lock().await;
                    if lifecycle.cancellation_requested || lifecycle.terminal {
                        cancelled = true;
                        drop(approvals);
                        drop(lifecycle);
                        Err(cancelled_tool_error(&call.name))
                    } else {
                        self.sessions
                            .append_record(
                                &request.session_id,
                                "approval.requested",
                                json!({"runId":request.run_id,"approval":approval}),
                            )
                            .await?;
                        approvals.insert(approval_id.clone(), PendingApproval { sender, deadline });
                        if let Err(error) = self
                            .emit(
                                events,
                                &request.session_id,
                                &request.run_id,
                                Some(turn_id),
                                "approval.requested",
                                json!({"approval":approval}),
                            )
                            .await
                        {
                            approvals.remove(&approval_id);
                            return Err(error);
                        }
                        if !self.approval_responder_connected.load(Ordering::Acquire) {
                            let disconnect_decision = json!({"choice":"deny"});
                            self.sessions
                                .append_record(
                                    &request.session_id,
                                    "approval.resolved",
                                    json!({
                                        "runId":request.run_id,
                                        "approvalId":approval_id,
                                        "decision":disconnect_decision,
                                        "resolutionSource":"disconnect"
                                    }),
                                )
                                .await?;
                            if let Some(pending) = approvals.remove(&approval_id) {
                                let _ = pending.sender.send(ApprovalWaitOutcome::Released(
                                    ApprovalResolution {
                                        approval_id: approval_id.clone(),
                                        decision: disconnect_decision,
                                        resolution_source: "disconnect",
                                    },
                                ));
                            }
                        }
                        drop(approvals);
                        drop(lifecycle);
                        let outcome = self
                            .wait_for_approval_resolution(
                                request,
                                control,
                                &approval_id,
                                deadline,
                                receiver,
                            )
                            .await?;
                        match outcome {
                            ApprovalWaitOutcome::DeliveryFailed(resolution) => {
                                let _ = resolution;
                                let error = AgentError::new(
                                    ErrorCode::StreamInterrupted,
                                    "approval response delivery failed",
                                )
                                .with_details(json!({
                                    "kind":"approval_delivery_failed",
                                    "toolName":call.name
                                }));
                                delivery_failure = Some(error.clone());
                                Err(error)
                            }
                            ApprovalWaitOutcome::Released(resolution) => {
                                self.emit(
                                    events,
                                    &request.session_id,
                                    &request.run_id,
                                    Some(turn_id),
                                    "approval.resolved",
                                    json!({
                                        "approvalId":resolution.approval_id,
                                        "decision":resolution.decision,
                                        "resolutionSource":resolution.resolution_source
                                    }),
                                )
                                .await?;
                                let choice = resolution.decision["choice"].as_str();
                                if resolution.resolution_source == "cancellation" {
                                    cancelled = true;
                                    Err(cancelled_tool_error(&call.name))
                                } else if choice == Some("allow_once") {
                                    let lifecycle = control.lifecycle.lock().await;
                                    if lifecycle.cancellation_requested || lifecycle.terminal {
                                        cancelled = true;
                                        drop(lifecycle);
                                        Err(cancelled_tool_error(&call.name))
                                    } else {
                                        self.emit(
                                            events,
                                            &request.session_id,
                                            &request.run_id,
                                            Some(turn_id),
                                            "tool.started",
                                            json!({"toolCallId":call.call_id,"name":call.name}),
                                        )
                                        .await?;
                                        drop(lifecycle);
                                        self.tools
                                            .execute_authorized_for_call(
                                                &request.session_id,
                                                Some(&request.run_id),
                                                &call.call_id,
                                                &call.name,
                                                &call.arguments,
                                                control.cancellation.child_token(),
                                            )
                                            .await
                                    }
                                } else {
                                    Err(AgentError::new(
                                        ErrorCode::PermissionDenied,
                                        format!("approval denied tool '{}'", call.name),
                                    ))
                                }
                            }
                        }
                    }
                }
                _ => Err(AgentError::new(
                    ErrorCode::InternalError,
                    "tool preflight produced an invalid policy state",
                )),
            }
        };

        let (output, portable_result, is_error) = match execution {
            Ok(output) => {
                let is_error = tool_output_is_error(&output);
                if output.termination_reason.as_deref() == Some("cancelled") {
                    cancelled = true;
                }
                let result = portable_tool_result(&output, is_error);
                (output, result, is_error)
            }
            Err(error) => {
                if error.code == ErrorCode::Cancelled {
                    cancelled = true;
                }
                let output = tool_error_output(&error);
                let result = portable_tool_error_result(&call.name, &output, &error);
                (output, result, true)
            }
        };
        self.sessions
            .append_record(
                &request.session_id,
                "tool.result",
                json!({
                    "runId":request.run_id,
                    "turnId":turn_id,
                    "toolCallId":call.call_id,
                    "result":portable_result,
                    "outcomeKnown":true
                }),
            )
            .await?;
        let tool_message_id = format!("message-{}", Uuid::new_v4());
        let message_time = Utc::now();
        self.sessions
            .append_record(
                &request.session_id,
                "message.appended",
                json!({
                    "runId":request.run_id,
                    "turnId":turn_id,
                    "message":{
                        "messageId":tool_message_id,
                        "role":"tool",
                        "toolCallId":call.call_id,
                        "toolName":call.name,
                        "content":portable_result["content"],
                        "isError":is_error,
                        "time":message_time
                    }
                }),
            )
            .await?;
        self.emit(
            events,
            &request.session_id,
            &request.run_id,
            Some(turn_id),
            "tool.completed",
            json!({"toolCallId":call.call_id,"result":portable_result}),
        )
        .await?;
        if let Some(error) = delivery_failure {
            return Err(error);
        }
        Ok(ToolCallOutcome {
            message: Message {
                id: tool_message_id,
                role: Role::Tool,
                content: vec![ContentBlock::ToolResult {
                    call_id: call.call_id,
                    name: call.name,
                    output,
                    is_error,
                }],
                created_at: message_time,
            },
            cancelled,
        })
    }

    /// 功能：有界等待审批 winner，并在 deadline 到期时 durable 记录服务器 timeout 否决。
    ///
    /// 输入：所属 run/control、approval ID、与 `expiresAt` 同源的 monotonic deadline 和 pending sender 对应 receiver。
    /// 输出：客户端、取消、断连或 timeout 中唯一获胜的结构化 resolution。
    /// 不变量：timeout 与其他 resolver 共用 lifecycle→approvals 锁序；记录先于后续 `approval.resolved` 事件，绝不覆盖已 durable 客户端决定。
    /// 失败：journal 追加失败、sender 无合法 winner 消失或等待通道异常关闭时返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn wait_for_approval_resolution(
        &self,
        request: &RunRequest,
        control: &RunControl,
        approval_id: &str,
        deadline: tokio::time::Instant,
        mut receiver: oneshot::Receiver<ApprovalWaitOutcome>,
    ) -> Result<ApprovalWaitOutcome, AgentError> {
        match tokio::time::timeout_at(deadline, &mut receiver).await {
            Ok(resolution) => resolution.map_err(|_| {
                AgentError::new(
                    ErrorCode::InternalError,
                    "approval waiter closed without a durable resolution",
                )
            }),
            Err(_) => {
                let lifecycle = control.lifecycle.lock().await;
                let mut approvals = control.approvals.lock().await;
                if approvals.contains_key(approval_id) {
                    let decision = json!({"choice":"deny"});
                    self.sessions
                        .append_record(
                            &request.session_id,
                            "approval.resolved",
                            json!({
                                "runId":request.run_id,
                                "approvalId":approval_id,
                                "decision":decision,
                                "resolutionSource":"timeout"
                            }),
                        )
                        .await?;
                    let pending = approvals.remove(approval_id).ok_or_else(|| {
                        AgentError::new(
                            ErrorCode::InternalError,
                            "pending approval disappeared during timeout",
                        )
                    })?;
                    drop(pending);
                    drop(approvals);
                    drop(lifecycle);
                    Ok(ApprovalWaitOutcome::Released(ApprovalResolution {
                        approval_id: approval_id.to_owned(),
                        decision,
                        resolution_source: "timeout",
                    }))
                } else {
                    drop(approvals);
                    drop(lifecycle);
                    receiver.await.map_err(|_| {
                        AgentError::new(
                            ErrorCode::InternalError,
                            "resolved approval response did not resume its waiter",
                        )
                    })
                }
            }
        }
    }

    /// 功能：追加一次 Provider attempt 的 started 或唯一终止事实。
    ///
    /// 输入：所属 run/turn、从 1 开始的 attempt、Schema 状态、可选脱敏错误与重试等待。
    /// 输出：记录完成持久化后返回成功。
    /// 不变量：不写入请求正文、认证信息或 Provider 原始响应；可选字段缺失时不写 `null`。
    /// 失败：Session 锁、I/O、序列化或日志完整性失败时返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn persist_provider_attempt(
        &self,
        request: &RunRequest,
        turn_id: &str,
        attempt: u32,
        status: &str,
        error: Option<&AgentError>,
        retry_after: Option<Duration>,
    ) -> Result<(), AgentError> {
        let mut data = json!({
            "runId":request.run_id,
            "turnId":turn_id,
            "attempt":attempt,
            "status":status
        });
        if let Some(error) = error {
            data["error"] = portable_error(error);
        }
        if let Some(retry_after) = retry_after {
            data["retryAfterMs"] = Value::from(
                u64::try_from(retry_after.as_millis()).unwrap_or(9_007_199_254_740_991),
            );
        }
        self.sessions
            .append_record(&request.session_id, "provider.attempt", data)
            .await
            .map(|_| ())
    }

    /// 功能：在规范注入点原子取出一个队列的当前全部项目并加入模型上下文。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// FIFO 顺序保持不变；`queue.consumed` 在继续下一次 Provider 请求前持久化。
    async fn drain_queue(
        &self,
        request: &RunRequest,
        control: &RunControl,
        kind: QueueKind,
        turn_id: &str,
        messages: &mut Vec<Message>,
    ) -> Result<bool, AgentError> {
        let mut queues = control.queues.lock().await;
        let (queue_name, items) = match kind {
            QueueKind::Steering => (
                "steering",
                queues.steering.iter().cloned().collect::<Vec<_>>(),
            ),
            QueueKind::FollowUp => (
                "follow_up",
                queues.follow_up.iter().cloned().collect::<Vec<_>>(),
            ),
        };
        if items.is_empty() {
            return Ok(false);
        }
        self.persist_queue_consumption(request, queue_name, turn_id, &items)
            .await?;
        let injected = self
            .persist_queued_messages(request, turn_id, &items)
            .await?;
        match kind {
            QueueKind::Steering => {
                queues.steering.drain(..items.len());
            }
            QueueKind::FollowUp => {
                queues.follow_up.drain(..items.len());
            }
        }
        drop(queues);
        messages.extend(injected);
        Ok(true)
    }

    /// 功能：在自然完成边界原子选择 steering、follow-up 或关闭队列入口。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// steering 优先于 follow-up；选中项持久化后才从内存 FIFO 移除；两队列都为空时
    /// 在同一互斥区内停止接受新输入，避免已接受项目越过终态。
    async fn drain_completion_queue(
        &self,
        request: &RunRequest,
        control: &RunControl,
        turn_id: &str,
        messages: &mut Vec<Message>,
    ) -> Result<bool, AgentError> {
        let mut queues = control.queues.lock().await;
        let (kind, queue_name, items) = if !queues.steering.is_empty() {
            (
                QueueKind::Steering,
                "steering",
                queues.steering.iter().cloned().collect::<Vec<_>>(),
            )
        } else if !queues.follow_up.is_empty() {
            (
                QueueKind::FollowUp,
                "follow_up",
                queues.follow_up.iter().cloned().collect::<Vec<_>>(),
            )
        } else {
            queues.accepting = false;
            return Ok(false);
        };
        self.persist_queue_consumption(request, queue_name, turn_id, &items)
            .await?;
        let injected = self
            .persist_queued_messages(request, turn_id, &items)
            .await?;
        match kind {
            QueueKind::Steering => {
                queues.steering.drain(..items.len());
            }
            QueueKind::FollowUp => {
                queues.follow_up.drain(..items.len());
            }
        }
        drop(queues);
        messages.extend(injected);
        Ok(true)
    }

    /// 功能：在队列内存状态改变前同步写入一条 FIFO 消费记录。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// 调用方必须持有该 run 的统一队列锁；失败时不得从内存队列删除任何项目。
    async fn persist_queue_consumption(
        &self,
        request: &RunRequest,
        queue_name: &str,
        turn_id: &str,
        items: &[QueuedInput],
    ) -> Result<(), AgentError> {
        self.sessions
            .append_record(
                &request.session_id,
                "queue.consumed",
                json!({
                    "runId":request.run_id,
                    "queueItemIds":items.iter().map(|item|item.queue_item_id.as_str()).collect::<Vec<_>>(),
                    "queue":queue_name,
                    "turnId":turn_id
                }),
            )
            .await
            .map(|_| ())
    }

    /// 功能：把已 durable 消费的队列输入逐条追加为 portable user message 并返回同 ID 上下文。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// 每条消息在进入下一 Provider 请求前完成同步，保证后续 run 能从 journal 重建该输入。
    async fn persist_queued_messages(
        &self,
        request: &RunRequest,
        turn_id: &str,
        items: &[QueuedInput],
    ) -> Result<Vec<Message>, AgentError> {
        let mut messages = Vec::with_capacity(items.len());
        for item in items {
            let message = Message::text(
                format!("message-{}", Uuid::new_v4()),
                Role::User,
                item.text.clone(),
            );
            self.sessions
                .append_record(
                    &request.session_id,
                    "message.appended",
                    json!({
                        "runId":request.run_id,
                        "turnId":turn_id,
                        "message":{
                            "messageId":message.id,
                            "role":"user",
                            "content":[{"type":"text","text":item.text}],
                            "time":message.created_at
                        }
                    }),
                )
                .await?;
            messages.push(message);
        }
        Ok(messages)
    }

    /// 功能：消费并验证一个 Provider 流，组装消息与完整工具调用。
    ///
    /// 输入：run/turn、Provider 流、取消令牌和本 run 已 durable 接受的 tool-call ID 集合。
    /// 输出：仅在 finish reason、ID、名称、参数和批内/跨 turn 唯一性全部有效后持久化的 assistant turn。
    /// 不变量：任意 partial JSON 只在 `ToolCallEnd` 后解析；工具与图片均先完成整批/MessageEnd/finish 验证，图片 bytes 才进入 artifact 发布边界。
    /// 失败：不完整、乱序、重复/非法标识或 finish reason 不一致时返回结构化 Provider 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[allow(clippy::too_many_arguments)]
    async fn consume_provider_stream(
        &self,
        request: &RunRequest,
        events: &mpsc::Sender<EventEnvelope>,
        turn_id: &str,
        mut stream: crate::provider::ProviderStream,
        cancellation: &CancellationToken,
        seen_tool_call_ids: &BTreeSet<String>,
    ) -> Result<TurnOutcome, AgentError> {
        let message_id = format!("msg-{}", Uuid::new_v4());
        let mut content = Vec::new();
        let mut text = String::new();
        let mut reasoning = String::new();
        let mut pending = BTreeMap::<String, PendingToolCall>::new();
        let mut completed = Vec::new();
        let mut finish_reason = None;
        let mut message_started = false;
        let mut image_completion = None::<(Option<String>, Vec<ProviderImage>)>;
        let mut usage = Usage::default();

        while let Some(item) = tokio::select! {
            () = cancellation.cancelled() => return Err(AgentError::new(ErrorCode::Cancelled, "run cancelled")),
            item = stream.next() => item,
        } {
            match item? {
                ProviderEvent::MessageStart {
                    provider_message_id: _,
                } => {
                    if message_started {
                        return Err(AgentError::new(
                            ErrorCode::StreamInterrupted,
                            "provider emitted duplicate message start",
                        ));
                    }
                    message_started = true;
                    self.emit(
                        events,
                        &request.session_id,
                        &request.run_id,
                        Some(turn_id),
                        "message.started",
                        json!({"messageId":message_id,"role":"assistant"}),
                    )
                    .await?;
                }
                ProviderEvent::TextDelta { text: delta } => {
                    text.push_str(&delta);
                    self.emit(
                        events,
                        &request.session_id,
                        &request.run_id,
                        Some(turn_id),
                        "message.delta",
                        json!({"messageId":message_id,"delta":{"type":"text","text":delta}}),
                    )
                    .await?;
                }
                ProviderEvent::ReasoningDelta { text: delta } => {
                    reasoning.push_str(&delta);
                    self.emit(
                        events,
                        &request.session_id,
                        &request.run_id,
                        Some(turn_id),
                        "message.delta",
                        json!({"messageId":message_id,"delta":{"type":"reasoning","text":delta}}),
                    )
                    .await?;
                }
                ProviderEvent::ToolCallStart { call_id, name } => {
                    if !valid_opaque_id(&call_id) || !valid_tool_name(&name) {
                        return Err(AgentError::new(
                            ErrorCode::ProviderError,
                            "provider emitted an invalid tool-call identity",
                        )
                        .with_details(json!({"kind":"invalid_tool_call_identity"})));
                    }
                    if seen_tool_call_ids.contains(&call_id)
                        || pending.contains_key(&call_id)
                        || completed
                            .iter()
                            .any(|call: &CompleteToolCall| call.call_id == call_id)
                    {
                        return Err(AgentError::new(
                            ErrorCode::ProviderError,
                            "provider duplicated a tool-call start",
                        )
                        .with_details(json!({"kind":"duplicate_tool_call_id"})));
                    }
                    pending.insert(
                        call_id.clone(),
                        PendingToolCall {
                            call_id,
                            name,
                            raw_arguments: String::new(),
                        },
                    );
                }
                ProviderEvent::ToolCallArgumentsDelta { call_id, delta } => {
                    let call = pending.get_mut(&call_id).ok_or_else(|| {
                        AgentError::new(
                            ErrorCode::StreamInterrupted,
                            "tool arguments arrived before tool start",
                        )
                    })?;
                    call.raw_arguments.push_str(&delta);
                }
                ProviderEvent::ToolCallEnd { call_id } => {
                    let call = pending.remove(&call_id).ok_or_else(|| {
                        AgentError::new(
                            ErrorCode::StreamInterrupted,
                            "tool end arrived without tool start",
                        )
                    })?;
                    let arguments = parse_strict_value(&call.raw_arguments).map_err(|error| {
                        AgentError::new(
                            ErrorCode::ToolArgumentsInvalid,
                            format!("tool arguments are incomplete or invalid JSON: {error}"),
                        )
                    })?;
                    if !arguments.is_object() {
                        return Err(AgentError::new(
                            ErrorCode::ToolArgumentsInvalid,
                            "tool arguments must be a JSON object",
                        ));
                    }
                    completed.push(CompleteToolCall {
                        call_id: call.call_id,
                        name: call.name,
                        arguments,
                    });
                }
                ProviderEvent::ImageCompletion {
                    text: final_text,
                    images,
                } => {
                    if !message_started
                        || image_completion.is_some()
                        || !text.is_empty()
                        || !reasoning.is_empty()
                        || !pending.is_empty()
                        || !completed.is_empty()
                        || images.is_empty()
                    {
                        return Err(AgentError::new(
                            ErrorCode::ProviderError,
                            "provider emitted an invalid image completion sequence",
                        )
                        .with_details(json!({"kind":"provider_protocol_error"})));
                    }
                    if final_text.as_deref() == Some("") {
                        return Err(AgentError::new(
                            ErrorCode::ProviderError,
                            "provider emitted an empty image completion text",
                        )
                        .with_details(json!({"kind":"provider_protocol_error"})));
                    }
                    image_completion = Some((final_text, images));
                }
                ProviderEvent::Usage(delta) => {
                    usage.input_tokens = usage.input_tokens.saturating_add(delta.input_tokens);
                    usage.output_tokens = usage.output_tokens.saturating_add(delta.output_tokens);
                    usage.total_tokens = usage.total_tokens.saturating_add(delta.total_tokens);
                    usage.cached_input_tokens = usage
                        .cached_input_tokens
                        .saturating_add(delta.cached_input_tokens);
                    if let Some(cost_micros) = delta.cost_micros {
                        usage.cost_micros = Some(
                            usage
                                .cost_micros
                                .unwrap_or_default()
                                .saturating_add(cost_micros),
                        );
                    }
                }
                ProviderEvent::MessageEnd {
                    finish_reason: reason,
                } => {
                    if finish_reason.replace(reason).is_some() {
                        return Err(AgentError::new(
                            ErrorCode::ProviderError,
                            "provider emitted duplicate message end",
                        )
                        .with_details(json!({"kind":"provider_protocol_error"})));
                    }
                }
            }
        }

        if !pending.is_empty() {
            return Err(AgentError::new(
                ErrorCode::StreamInterrupted,
                "provider stream ended with an incomplete tool call",
            ));
        }
        let finish_reason = finish_reason.ok_or_else(|| {
            AgentError::new(
                ErrorCode::StreamInterrupted,
                "provider stream ended without message end",
            )
        })?;
        validate_complete_tool_batch(&completed, finish_reason, seen_tool_call_ids)?;
        let assistant_created_at = Utc::now();
        let mut image_completion_committed = false;
        if let Some((final_text, images)) = image_completion {
            if finish_reason != FinishReason::Stop {
                return Err(AgentError::new(
                    ErrorCode::ProviderError,
                    "provider image completion used an invalid finish reason",
                )
                .with_details(json!({"kind":"provider_protocol_error"})));
            }
            if cancellation.is_cancelled() {
                return Err(AgentError::new(
                    ErrorCode::Cancelled,
                    "run cancelled before image artifact publication",
                ));
            }
            let record_text = final_text.clone();
            let record_run_id = request.run_id.clone();
            let record_turn_id = turn_id.to_owned();
            let record_message_id = message_id.clone();
            let record_provider = run_provider_selection(request, &request.provider_id);
            let record_usage = usage.clone();
            let record_time = assistant_created_at;
            let artifacts = self
                .sessions
                .store_image_completion(&request.session_id, &images, move |artifacts| {
                    let mut portable_content =
                        Vec::with_capacity(artifacts.len() + usize::from(record_text.is_some()));
                    if let Some(text) = record_text {
                        portable_content.push(json!({"type":"text","text":text}));
                    }
                    portable_content.extend(
                        artifacts
                            .iter()
                            .map(|artifact| json!({"type":"image_ref","artifact":artifact})),
                    );
                    json!({
                        "runId":record_run_id,
                        "turnId":record_turn_id,
                        "message":{
                            "messageId":record_message_id,
                            "role":"assistant",
                            "content":portable_content,
                            "provider":record_provider,
                            "finishReason":finish_reason,
                            "usage":record_usage,
                            "time":record_time
                        }
                    })
                })
                .await?;
            image_completion_committed = true;
            if let Some(final_text) = final_text {
                content.push(ContentBlock::Text { text: final_text });
            }
            content.extend(
                artifacts
                    .into_iter()
                    .map(|artifact| ContentBlock::ImageRef {
                        artifact,
                        alt: None,
                    }),
            );
        } else {
            if !text.is_empty() {
                content.push(ContentBlock::Text { text });
            }
            if !reasoning.is_empty() {
                content.push(ContentBlock::Reasoning {
                    text: reasoning,
                    redacted: false,
                    signature: None,
                });
            }
        }
        for call in &completed {
            content.push(ContentBlock::ToolCall {
                call_id: call.call_id.clone(),
                name: call.name.clone(),
                arguments: call.arguments.clone(),
            });
        }
        let assistant = Message {
            id: message_id.clone(),
            role: Role::Assistant,
            content,
            created_at: assistant_created_at,
        };
        let portable_content = assistant
            .content
            .iter()
            .map(portable_content_block)
            .collect::<Vec<_>>();
        if !image_completion_committed {
            self.sessions
                .append_record(
                    &request.session_id,
                    "message.appended",
                    json!({
                        "runId":request.run_id,
                        "turnId":turn_id,
                        "message":{
                            "messageId":message_id,
                            "role":"assistant",
                            "content":portable_content,
                            "provider":run_provider_selection(request, &request.provider_id),
                            "finishReason":finish_reason,
                            "usage":usage,
                            "time":assistant.created_at
                        }
                    }),
                )
                .await?;
        }
        self.emit(
            events,
            &request.session_id,
            &request.run_id,
            Some(turn_id),
            "message.completed",
            json!({"messageId":message_id,"finishReason":finish_reason}),
        )
        .await?;
        Ok(TurnOutcome {
            assistant,
            tool_calls: completed,
            finish_reason,
            usage,
        })
    }

    /// 功能：为 session 分配单调事件序号，先持久化事件，再尽力通知监听器。
    ///
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    ///
    /// 监听器断开不会回滚已持久化状态，也不会使 Agent 状态机失败。
    async fn emit(
        &self,
        events: &mpsc::Sender<EventEnvelope>,
        session_id: &str,
        run_id: &str,
        turn_id: Option<&str>,
        event_type: &str,
        data: Value,
    ) -> Result<(), AgentError> {
        let records = self.sessions.load(session_id).await?;
        let event_seq = {
            let mut sequences = self.event_sequences.lock().await;
            let persisted_next = records
                .iter()
                .filter(|record| record.kind == "event.emitted")
                .filter_map(|record| record.data.pointer("/event/seq").and_then(Value::as_u64))
                .max()
                .unwrap_or(0)
                .saturating_add(1);
            let next = sequences
                .entry(session_id.to_owned())
                .or_insert(persisted_next);
            let value = *next;
            *next = next.saturating_add(1);
            value
        };
        let envelope = EventEnvelope {
            session_id: session_id.to_owned(),
            run_id: run_id.to_owned(),
            turn_id: turn_id.map(str::to_owned),
            seq: event_seq,
            time: Utc::now(),
            event_type: event_type.to_owned(),
            data,
        };
        self.persist_event_fact(&envelope).await?;
        self.sessions
            .append_record(
                session_id,
                "event.emitted",
                json!({
                    "event":serde_json::to_value(&envelope).map_err(|error| {
                        AgentError::new(ErrorCode::InternalError,error.to_string())
                    })?
                }),
            )
            .await?;
        // 监听器关闭不能回滚已持久化状态；daemon 退出后 journal 仍可恢复。
        let _ = events.send(envelope).await;
        Ok(())
    }

    /// 功能：把可观察事件映射为 Session Schema 的 canonical lifecycle/tool records。
    ///
    /// 输入：尚未写入 `event.emitted` 的完整事件 envelope。
    /// 输出：需要 canonical lifecycle 事实的事件已先追加对应 journal 记录。
    /// 不变量：工具 intent/result 由工具状态机显式写入，绝不在此重复；流式 delta 只进入 replay 记录。
    /// 失败：Session 锁、I/O 或 journal 完整性失败时返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn persist_event_fact(&self, event: &EventEnvelope) -> Result<(), AgentError> {
        let record = match event.event_type.as_str() {
            "run.started" => Some(("run.started", json!({"runId":event.run_id}))),
            "turn.started" => Some((
                "turn.started",
                json!({"runId":event.run_id,"turnId":event.turn_id,"attempt":1}),
            )),
            "turn.completed" => Some((
                "turn.completed",
                json!({
                    "runId":event.run_id,
                    "turnId":event.turn_id,
                    "finishReason":event.data["finishReason"]
                }),
            )),
            "run.completed" => Some((
                "run.terminal",
                json!({"runId":event.run_id,"status":"completed","usage":event.data["usage"]}),
            )),
            "run.failed" => {
                let mut data = json!({
                    "runId":event.run_id,"status":"failed","error":event.data["error"]
                });
                if let Some(usage) = event.data.get("usage") {
                    data["usage"] = usage.clone();
                }
                Some(("run.terminal", data))
            }
            "run.cancelled" => {
                let mut data = json!({"runId":event.run_id,"status":"cancelled"});
                if let Some(usage) = event.data.get("usage") {
                    data["usage"] = usage.clone();
                }
                Some(("run.terminal", data))
            }
            "run.interrupted" => {
                let mut data = json!({
                    "runId":event.run_id,"status":"interrupted","error":event.data["error"]
                });
                if let Some(usage) = event.data.get("usage") {
                    data["usage"] = usage.clone();
                }
                Some(("run.terminal", data))
            }
            _ => None,
        };
        if let Some((kind, data)) = record {
            self.sessions
                .append_record(&event.session_id, kind, data)
                .await?;
        }
        Ok(())
    }
}

struct PendingToolCall {
    call_id: String,
    name: String,
    raw_arguments: String,
}

#[derive(Clone)]
struct CompleteToolCall {
    call_id: String,
    name: String,
    arguments: Value,
}

struct TurnOutcome {
    assistant: Message,
    tool_calls: Vec<CompleteToolCall>,
    finish_reason: FinishReason,
    usage: Usage,
}

struct ToolCallOutcome {
    message: Message,
    cancelled: bool,
}

/// 功能：在 assistant 消息 durable 前验证完整工具批的 finish 语义和稳定标识。
///
/// 输入：按源顺序完成的工具调用、Provider finish reason 和本 run 已接受 ID 集合。
/// 输出：批可安全持久化并保证后续每个调用能唯一收尾时返回成功。
/// 不变量：tool-use finish 当且仅当批非空；ID 在批内及 run 内唯一；名称/ID 满足公共 Schema。
/// 失败：任一不一致返回不可重试 ProviderError，且调用方尚未写入 assistant tool-call 消息。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_complete_tool_batch(
    calls: &[CompleteToolCall],
    finish_reason: FinishReason,
    seen_tool_call_ids: &BTreeSet<String>,
) -> Result<(), AgentError> {
    if (finish_reason == FinishReason::ToolCalls) == calls.is_empty() {
        return Err(AgentError::new(
            ErrorCode::ProviderError,
            "provider finish reason does not match its complete tool-call batch",
        )
        .with_details(json!({"kind":"tool_finish_reason_mismatch"})));
    }
    let mut batch_ids = BTreeSet::new();
    for call in calls {
        if !valid_opaque_id(&call.call_id) {
            return Err(AgentError::new(
                ErrorCode::ProviderError,
                "provider emitted an invalid tool-call ID",
            )
            .with_details(json!({"kind":"invalid_tool_call_id"})));
        }
        if !valid_tool_name(&call.name) {
            return Err(AgentError::new(
                ErrorCode::ProviderError,
                "provider emitted an invalid tool name",
            )
            .with_details(json!({"kind":"invalid_tool_name"})));
        }
        if seen_tool_call_ids.contains(&call.call_id) || !batch_ids.insert(call.call_id.clone()) {
            return Err(AgentError::new(
                ErrorCode::ProviderError,
                "provider reused a tool-call ID within one run",
            )
            .with_details(json!({"kind":"duplicate_tool_call_id"})));
        }
    }
    Ok(())
}

/// 功能：按 common Schema 的 ASCII opaque ID 规则校验 Provider 工具调用标识。
///
/// 输入：不受信任的 Provider 字符串。
/// 输出：首字符、字符集及 1..128 Unicode 标量长度均有效时为 true。
/// 不变量：不接受空值、非 ASCII 同形字符或协议分隔符之外的字符。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn valid_opaque_id(value: &str) -> bool {
    let mut characters = value.chars();
    let Some(first) = characters.next() else {
        return false;
    };
    if !first.is_ascii_alphanumeric() {
        return false;
    }
    let mut length = 1;
    for character in characters {
        length += 1;
        if length > 128
            || !(character.is_ascii_alphanumeric() || matches!(character, '.' | '_' | ':' | '-'))
        {
            return false;
        }
    }
    true
}

/// 功能：按 tool Schema 规则校验 Provider 提供的工具名称。
///
/// 输入：不受信任的 Provider 工具名。
/// 输出：以 ASCII 小写字母开头且后续仅含小写字母、数字、下划线、点或连字符时为 true。
/// 不变量：长度限制为 1..128，不接受大写、空白或 Unicode 同形字符。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn valid_tool_name(value: &str) -> bool {
    let mut characters = value.chars();
    let Some(first) = characters.next() else {
        return false;
    };
    if !first.is_ascii_lowercase() {
        return false;
    }
    let mut length = 1;
    for character in characters {
        length += 1;
        if length > 128
            || !(character.is_ascii_lowercase()
                || character.is_ascii_digit()
                || matches!(character, '_' | '.' | '-'))
        {
            return false;
        }
    }
    true
}

/// 功能：构造绑定到规范化工具意图的语言中立审批请求。
///
/// 输入：opaque approval ID、完整工具调用、副作用等级、canonical operation hash 和有界 timeout。
/// 输出：符合 approval v0.1 Schema 的请求对象。
/// 不变量：choices 始终包含 `allow_once` 与 `deny`；受影响路径保持模型原始相对值，不写入 canonical 主机路径。
/// 失败：本方法不返回错误；缺少专用资源字段时保守使用 operation 资源。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn approval_request(
    approval_id: &str,
    call: &CompleteToolCall,
    effect: ToolEffect,
    operation_hash: &str,
    timeout: Duration,
) -> Value {
    let (risk, reason) = match effect {
        ToolEffect::Write => ("medium", "工具将修改工作区内容。"),
        ToolEffect::Process | ToolEffect::Shell => ("high", "工具将启动本机进程。"),
        ToolEffect::Terminal => ("high", "工具将打开持续终端任务。"),
        ToolEffect::ComputerObserve => (
            "high",
            "工具将读取完整可见桌面并在 Session 生命周期内持久化敏感截图。",
        ),
        ToolEffect::ComputerInteract => ("critical", "工具将控制当前桌面的鼠标或键盘。"),
        ToolEffect::Read => ("low", "工具将读取工作区资源。"),
    };
    let resources = approval_resources(call, effect);
    json!({
        "approvalId":approval_id,
        "toolCallId":call.call_id,
        "operation":call.name,
        "arguments":call.arguments,
        "operationHash":operation_hash,
        "risk":risk,
        "reason":reason,
        "resources":resources,
        "choices":["allow_once","deny"],
        "expiresAt":Utc::now()
            + chrono::Duration::from_std(timeout).unwrap_or_else(|_| chrono::Duration::hours(1))
    })
}

/// 功能：从工具参数提取不泄露 canonical 主机路径的审批资源列表。
///
/// 输入：完整工具调用和预检后的副作用等级。
/// 输出：至少一个符合 approval resource Schema 的资源描述。
/// 不变量：优先保留原始 `path` 或 `executable`；不会解析凭据或读取文件内容。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn approval_resources(call: &CompleteToolCall, effect: ToolEffect) -> Vec<Value> {
    if effect == ToolEffect::ComputerObserve {
        return vec![json!({"kind":"other","value":"desktop:screen"})];
    }
    if effect == ToolEffect::ComputerInteract {
        let action = call
            .arguments
            .get("action")
            .and_then(Value::as_str)
            .unwrap_or("unknown");
        return vec![json!({"kind":"other","value":format!("desktop:{action}")})];
    }
    if let Some(path) = call.arguments.get("path").and_then(Value::as_str) {
        return vec![json!({"kind":"path","value":path})];
    }
    if let Some(executable) = call.arguments.get("executable").and_then(Value::as_str) {
        return vec![json!({"kind":"executable","value":executable})];
    }
    let kind = match effect {
        ToolEffect::Process | ToolEffect::Shell | ToolEffect::Terminal => "process",
        _ => "other",
    };
    vec![json!({"kind":kind,"value":call.name})]
}

/// 功能：计算审批绑定使用的递归键排序 compact JSON SHA-256。
///
/// 输入：工具 operation 名和语言中立参数对象。
/// 输出：64 位小写十六进制 SHA-256。
/// 不变量：对象键递归排序、数组顺序保持不变且 UTF-8 不转义为语言专属表示。
/// 失败：内存中的 `serde_json::Value` 无法序列化时返回内部结构化错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn operation_hash(name: &str, arguments: &Value) -> Result<String, AgentError> {
    let normalized = canonical_json(&json!({"operation":name,"arguments":arguments}));
    let bytes = serde_json::to_vec(&normalized)
        .map_err(|error| AgentError::new(ErrorCode::InternalError, error.to_string()))?;
    Ok(format!("{:x}", Sha256::digest(bytes)))
}

/// 功能：递归复制 JSON 并按 Unicode 键序规范化所有对象。
///
/// 输入：任意无语言专属值的 JSON 树。
/// 输出：语义等价、对象键稳定排序的 JSON 树。
/// 不变量：数组顺序与数值/字符串表示保持 serde_json 语义不变。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn canonical_json(value: &Value) -> Value {
    match value {
        Value::Object(object) => {
            let sorted = object
                .iter()
                .map(|(key, value)| (key.clone(), canonical_json(value)))
                .collect::<BTreeMap<_, _>>();
            Value::Object(sorted.into_iter().collect())
        }
        Value::Array(values) => Value::Array(values.iter().map(canonical_json).collect()),
        _ => value.clone(),
    }
}

/// 功能：创建“审批等待期间已取消”的稳定工具错误。
///
/// 输入：被取消的工具名。
/// 输出：错误码、详情分类及 operation 均可移植的取消错误。
/// 不变量：不会把取消伪装成权限拒绝或未知执行结果。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn cancelled_tool_error(name: &str) -> AgentError {
    AgentError::new(ErrorCode::Cancelled, "tool was cancelled before execution")
        .with_details(json!({"kind":"cancelled","toolName":name}))
}

/// 功能：为未知、过期、已超时、重复或 run 已结束的审批响应创建统一不可重试 conflict。
///
/// 输入：无客户端可控错误分类。
/// 输出：固定 JSON-RPC `-32010` 及 `approval_conflict` 详情的结构化错误。
/// 不变量：不同审批可用状态不会通过错误码泄露，也不会恢复或修改工具执行。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn approval_conflict() -> AgentError {
    AgentError::new(
        ErrorCode::Conflict,
        "approval is unavailable or already resolved",
    )
    .with_details(json!({"kind":"approval_conflict","field":"approvalId"}))
}

/// 功能：为已声明图片输入的 route 选择单图与总字节硬上限。
///
/// 输入：已完成 route 选择的 Provider。
/// 输出：`(单图上限, 总上限)`；OpenRouter 保持既有 16 MiB 合同，自定义 OpenAI route 收窄为单图 512 KiB、总计 4 MiB。
/// 不变量：调用方仅在 `supports_image_input` 为 true 时使用；未知 route 使用更严格的自定义上限。
/// 失败：本函数不执行 I/O 且不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn provider_image_input_limits(provider: &dyn Provider) -> (usize, u64) {
    if provider.api_family() == Some("openrouter-images") {
        (
            MAX_OPENROUTER_INPUT_IMAGE_BYTES,
            MAX_OPENROUTER_INPUT_IMAGE_BYTES as u64,
        )
    } else {
        (
            MAX_CUSTOM_INPUT_IMAGE_BYTES,
            MAX_CUSTOM_TOTAL_INPUT_IMAGE_BYTES,
        )
    }
}

/// 功能：把 Provider attempt 的结构化错误映射为 Session Schema 终止状态。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn provider_attempt_error_status(error: &AgentError) -> &'static str {
    match error.code {
        ErrorCode::Cancelled => "cancelled",
        ErrorCode::StreamInterrupted => "interrupted",
        _ => "failed",
    }
}

/// 功能：读取 Provider 初始请求允许的最大尝试次数并应用安全上限。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn provider_max_attempts() -> u32 {
    std::env::var("QXNM_FORGE_PROVIDER_MAX_ATTEMPTS")
        .ok()
        .and_then(|value| value.parse::<u32>().ok())
        .filter(|value| (1..=100).contains(value))
        .unwrap_or(3)
}

/// 功能：仅在 CLI 与环境 conformance 双门成立时读取审批等待上限。
///
/// 输入：字面 `--conformance` 判定与 `QXNM_FORGE_CONFORMANCE=1` 判定。
/// 输出：双门及环境值都合法时返回有界持续时间，否则返回默认五分钟。
/// 不变量：任一单门都不会读取 timeout 环境；模型、工具参数和协议客户端不能改变该限制；
/// 永不返回零或无界持续时间。
/// 失败：本方法不返回错误，非法文本或越界值安全回退默认值。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn approval_timeout_from_env(cli_conformance: bool, environment_conformance: bool) -> Duration {
    if !(cli_conformance && environment_conformance) {
        return DEFAULT_APPROVAL_TIMEOUT;
    }
    let value = std::env::var("QXNM_FORGE_APPROVAL_TIMEOUT_MS").ok();
    approval_timeout_from_text(cli_conformance, environment_conformance, value.as_deref())
}

/// 功能：在两个 conformance 门成立后把可选严格十进制毫秒文本解析为有界审批等待时间。
///
/// 输入：CLI/environment conformance 判定与可选环境文本。
/// 输出：双门成立且文本位于 1 至 3,600,000 毫秒时返回该持续时间，否则返回默认五分钟。
/// 不变量：任一单门以及符号、空白、非 ASCII 数字、零、溢出和超过一小时的值均被拒绝。
/// 失败：本方法不返回错误，所有非法输入安全回退默认值。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn approval_timeout_from_text(
    cli_conformance: bool,
    environment_conformance: bool,
    value: Option<&str>,
) -> Duration {
    if !(cli_conformance && environment_conformance) {
        return DEFAULT_APPROVAL_TIMEOUT;
    }
    value
        .filter(|value| !value.is_empty() && value.bytes().all(|byte| byte.is_ascii_digit()))
        .and_then(|value| value.parse::<u64>().ok())
        .map(Duration::from_millis)
        .filter(|duration| !duration.is_zero() && *duration <= MAX_APPROVAL_TIMEOUT)
        .unwrap_or(DEFAULT_APPROVAL_TIMEOUT)
}

/// 功能：判断 Provider 尚未返回流时的错误是否属于规范允许自动重试的类型。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
///
/// 仅重试明确标记 retryable 的 408/429/500/502/503/504 或无 HTTP 状态的传输不可用。
fn should_retry_provider_start(error: &AgentError) -> bool {
    if !error.retryable {
        return false;
    }
    error
        .details
        .get("httpStatus")
        .and_then(Value::as_u64)
        .map_or(error.code == ErrorCode::ProviderUnavailable, |status| {
            matches!(status, 408 | 429 | 500 | 502 | 503 | 504)
        })
}

/// 功能：计算受 Retry-After 与 conformance 最大延迟共同约束的重试等待。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
///
/// 没有服务端提示时采用 250ms 起始的有界指数等待；测试可把最大延迟显式设为零。
fn provider_retry_delay(error: &AgentError, attempt: u32) -> Duration {
    let maximum_ms = std::env::var("QXNM_FORGE_PROVIDER_RETRY_MAX_DELAY_MS")
        .ok()
        .and_then(|value| value.parse::<u64>().ok())
        .unwrap_or(5_000)
        .min(60_000);
    let hinted_ms = error.details.get("retryAfterMs").and_then(Value::as_u64);
    let fallback_ms = 250_u64.saturating_mul(1_u64 << attempt.saturating_sub(1).min(8));
    Duration::from_millis(hinted_ms.unwrap_or(fallback_ms).min(maximum_ms))
}

/// 功能：把内部结构化错误转换为公共 Schema 的数值错误对象。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn portable_error(error: &AgentError) -> Value {
    json!({
        "code":error.code.rpc_code(),
        "message":error.message,
        "retryable":error.retryable,
        "details":error.details
    })
}

/// 功能：构造 journal 使用的品牌中立 Provider selection，并仅在存在时写入 apiFamily。
///
/// 输入：已接受 run 与 adapter 的 canonical Provider ID。
/// 输出：含 id/modelId/可选 apiFamily 的 portable JSON 对象。
/// 不变量：字段不含 endpoint、credential 或实现语言；图像路由保留显式 family。
/// 失败：本函数只构造受控 JSON 且不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn run_provider_selection(request: &RunRequest, provider_id: &str) -> Value {
    let mut selection = json!({"id":provider_id,"modelId":request.model});
    if let Some(api_family) = &request.api_family {
        selection["apiFamily"] = Value::String(api_family.clone());
    }
    selection
}

/// 功能：把内部消息内容块转换为语言中立的 snake_case wire content。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn portable_content_block(block: &ContentBlock) -> Value {
    match block {
        ContentBlock::Text { text } => json!({"type":"text","text":text}),
        ContentBlock::Reasoning {
            text,
            redacted,
            signature,
        } => {
            let mut value = json!({"type":"reasoning","text":text});
            if *redacted {
                value["redacted"] = Value::Bool(true);
            }
            if let Some(signature) = signature {
                value["signature"] = Value::String(signature.clone());
            }
            value
        }
        ContentBlock::ImageRef { artifact, alt } => {
            let mut value = json!({"type":"image_ref","artifact":artifact});
            if let Some(alt) = alt {
                value["alt"] = Value::String(alt.clone());
            }
            value
        }
        ContentBlock::ArtifactRef { artifact } => {
            json!({"type":"artifact_ref","artifact":artifact})
        }
        ContentBlock::ToolCall {
            call_id,
            name,
            arguments,
        } => json!({
            "type":"tool_call","toolCallId":call_id,"name":name,"arguments":arguments
        }),
        ContentBlock::ToolResult { .. } => {
            json!({"type":"text","text":"unsupported nested tool result"})
        }
    }
}

/// 功能：把内部 ToolOutput 转换为公共 toolResult content 数组。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn portable_tool_result(output: &ToolOutput, is_error: bool) -> Value {
    let mut content = Vec::new();
    if let Some(text) = &output.text {
        content.push(json!({"type":"text","text":text}));
    }
    if let Some(artifact) = &output.artifact {
        content.push(json!({"type":artifact_content_type(artifact),"artifact":artifact}));
    }
    if content.is_empty() {
        content.push(json!({"type":"text","text":""}));
    }
    let mut result = json!({"content":content,"isError":is_error});
    if let Some(termination_reason) = &output.termination_reason {
        result["terminationReason"] = Value::String(termination_reason.clone());
    }
    if let Some(execution) = &output.execution {
        result["execution"] = execution.clone();
    }
    result
}

/// 功能：依据结构化终止原因和 exitCode 决定 portable ToolResult 的 isError。
///
/// 输入：已授权工具返回的内部 ToolOutput。
/// 输出：普通非执行工具与 exitCode=0 返回 false；非零、signal、timeout、cancelled、output_limit 或畸形 execution 返回 true。
/// 不变量：不解析展示文本；非零退出仍是 tool.completed，只通过 isError 标记。
/// 失败：本方法不返回错误，缺失必要 execution 时保守返回 true。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn tool_output_is_error(output: &ToolOutput) -> bool {
    match output.termination_reason.as_deref() {
        None => false,
        Some("exit") => {
            output
                .execution
                .as_ref()
                .and_then(|execution| execution.get("exitCode"))
                .and_then(Value::as_i64)
                != Some(0)
        }
        Some(_) => true,
    }
}

/// 功能：把工具执行错误转换为 Provider 上下文使用的最小文本输出。
///
/// 输入：已分类且不含秘密的结构化工具错误。
/// 输出：以人类可读消息为文本、无 artifact 的 `ToolOutput`。
/// 不变量：结构化错误只进入 portable result 的显式 `error` 字段，不塞入任意 metadata。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn tool_error_output(error: &AgentError) -> ToolOutput {
    ToolOutput {
        text: Some(error.message.clone()),
        artifact: None,
        termination_reason: None,
        execution: None,
        metadata: BTreeMap::new(),
    }
}

/// 功能：生成同时包含内容、终止原因和公共错误对象的 portable 工具失败结果。
///
/// 输入：工具名、上下文输出和已分类结构化错误。
/// 输出：符合 toolResult/error v0.1 Schema 的 `isError=true` 对象。
/// 不变量：`details.kind` 保持稳定且附加 `toolName`；客户端无需解析错误文本。
/// 失败：本方法不返回错误；非对象详情会安全退化为错误码对应的 kind。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn portable_tool_error_result(name: &str, output: &ToolOutput, error: &AgentError) -> Value {
    let mut result = portable_tool_result(output, true);
    result["terminationReason"] = Value::String(tool_termination_reason(error).to_owned());
    let mut details = error
        .details
        .as_object()
        .cloned()
        .unwrap_or_else(serde_json::Map::new);
    details
        .entry("kind".to_owned())
        .or_insert_with(|| Value::String(error.code.detail_kind().to_owned()));
    details.insert("toolName".to_owned(), Value::String(name.to_owned()));
    result["error"] = json!({
        "code":error.code.rpc_code(),
        "message":error.message,
        "retryable":error.retryable,
        "details":details
    });
    result
}

/// 功能：将稳定错误码映射为公共工具终止原因枚举。
///
/// 输入：工具边界产生的结构化错误。
/// 输出：toolResult Schema 允许的 terminationReason。
/// 不变量：取消、超时、输出上限、权限和参数验证保持可机读区分；未知基础设施失败保守归为 internal_error。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn tool_termination_reason(error: &AgentError) -> &'static str {
    match error.code {
        ErrorCode::Cancelled => "cancelled",
        ErrorCode::Timeout => "timeout",
        ErrorCode::OutputLimitExceeded => "output_limit",
        ErrorCode::ApprovalRequired
        | ErrorCode::PermissionDenied
        | ErrorCode::PathOutsideWorkspace => "denied",
        ErrorCode::ToolNotFound | ErrorCode::ToolArgumentsInvalid | ErrorCode::InvalidParams => {
            "validation_error"
        }
        _ => "internal_error",
    }
}

/// 功能：把 portable `message.appended.data.message` 无语言依赖地重建为 Provider 消息。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
///
/// 支持 user、assistant、tool 以及文本、推理、图像/artifact 引用和工具调用；不能安全
/// 表示的消息返回 `journal_incompatible`，绝不以当前 prompt 替代或跳过历史。
fn provider_message_from_portable(value: &Value) -> Result<Message, AgentError> {
    let object = value.as_object().ok_or_else(|| {
        AgentError::new(
            ErrorCode::JournalCorrupt,
            "portable message must be a JSON object",
        )
    })?;
    let message_id = portable_string(object.get("messageId"), "message.messageId")?;
    let role = portable_string(object.get("role"), "message.role")?;
    let created_at: DateTime<Utc> =
        serde_json::from_value(object.get("time").cloned().ok_or_else(|| {
            AgentError::new(ErrorCode::JournalCorrupt, "message.time is missing")
        })?)
        .map_err(|_| AgentError::new(ErrorCode::JournalCorrupt, "message.time is invalid"))?;
    let content = object
        .get("content")
        .and_then(Value::as_array)
        .ok_or_else(|| {
            AgentError::new(
                ErrorCode::JournalCorrupt,
                "portable message content must be an array",
            )
        })?;

    let (role, content) = match role {
        "user" => (
            Role::User,
            content
                .iter()
                .map(portable_content_to_internal)
                .collect::<Result<Vec<_>, _>>()?,
        ),
        "assistant" => (
            Role::Assistant,
            content
                .iter()
                .map(portable_content_to_internal)
                .collect::<Result<Vec<_>, _>>()?,
        ),
        "tool" => {
            let call_id = portable_string(object.get("toolCallId"), "message.toolCallId")?;
            let name = portable_string(object.get("toolName"), "message.toolName")?;
            let is_error = object
                .get("isError")
                .and_then(Value::as_bool)
                .ok_or_else(|| {
                    AgentError::new(
                        ErrorCode::JournalCorrupt,
                        "tool message isError must be boolean",
                    )
                })?;
            let mut text = None;
            let mut artifact = None;
            for block in content {
                match block.get("type").and_then(Value::as_str) {
                    Some("text") if text.is_none() => {
                        text = Some(
                            block
                                .get("text")
                                .and_then(Value::as_str)
                                .ok_or_else(|| {
                                    AgentError::new(
                                        ErrorCode::JournalCorrupt,
                                        "tool result text block is invalid",
                                    )
                                })?
                                .to_owned(),
                        );
                    }
                    Some("artifact_ref" | "image_ref") if artifact.is_none() => {
                        artifact = Some(artifact_from_portable(block.get("artifact"))?);
                    }
                    _ => return Err(incompatible_history_error()),
                }
            }
            (
                Role::Tool,
                vec![ContentBlock::ToolResult {
                    call_id: call_id.to_owned(),
                    name: name.to_owned(),
                    output: ToolOutput {
                        text,
                        artifact,
                        termination_reason: None,
                        execution: None,
                        metadata: BTreeMap::new(),
                    },
                    is_error,
                }],
            )
        }
        _ => {
            return Err(AgentError::new(
                ErrorCode::JournalCorrupt,
                "portable message role is invalid",
            ));
        }
    };
    Ok(Message {
        id: message_id.to_owned(),
        role,
        content,
        created_at,
    })
}

/// 功能：把 portable user/assistant 内容块转换为 Provider 领域内容块。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn portable_content_to_internal(value: &Value) -> Result<ContentBlock, AgentError> {
    let content_type = value.get("type").and_then(Value::as_str).ok_or_else(|| {
        AgentError::new(
            ErrorCode::JournalCorrupt,
            "portable content type is missing",
        )
    })?;
    match content_type {
        "text" => Ok(ContentBlock::Text {
            text: portable_string(value.get("text"), "content.text")?.to_owned(),
        }),
        "reasoning" => Ok(ContentBlock::Reasoning {
            text: portable_string(value.get("text"), "content.text")?.to_owned(),
            redacted: match value.get("redacted") {
                Some(redacted) => redacted.as_bool().ok_or_else(|| {
                    AgentError::new(
                        ErrorCode::JournalCorrupt,
                        "content.redacted must be boolean",
                    )
                })?,
                None => false,
            },
            signature: value
                .get("signature")
                .map(|signature| portable_string(Some(signature), "content.signature"))
                .transpose()?
                .map(str::to_owned),
        }),
        "image_ref" => Ok(ContentBlock::ImageRef {
            artifact: artifact_from_portable(value.get("artifact"))?,
            alt: value
                .get("alt")
                .map(|alt| portable_string(Some(alt), "content.alt"))
                .transpose()?
                .map(str::to_owned),
        }),
        "artifact_ref" => Ok(ContentBlock::ArtifactRef {
            artifact: artifact_from_portable(value.get("artifact"))?,
        }),
        "tool_call" => {
            let arguments = value.get("arguments").cloned().ok_or_else(|| {
                AgentError::new(ErrorCode::JournalCorrupt, "tool call arguments are missing")
            })?;
            if !arguments.is_object() {
                return Err(AgentError::new(
                    ErrorCode::JournalCorrupt,
                    "tool call arguments must be an object",
                ));
            }
            Ok(ContentBlock::ToolCall {
                call_id: portable_string(value.get("toolCallId"), "content.toolCallId")?.to_owned(),
                name: portable_string(value.get("name"), "content.name")?.to_owned(),
                arguments,
            })
        }
        _ => Err(incompatible_history_error()),
    }
}

/// 功能：严格反序列化 portable artifact reference，保留显示名和扩展命名空间。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn artifact_from_portable(value: Option<&Value>) -> Result<crate::domain::ArtifactRef, AgentError> {
    serde_json::from_value(value.cloned().ok_or_else(|| {
        AgentError::new(
            ErrorCode::JournalCorrupt,
            "portable artifact reference is missing",
        )
    })?)
    .map_err(|_| {
        AgentError::new(
            ErrorCode::JournalCorrupt,
            "portable artifact reference is invalid",
        )
    })
}

/// 功能：读取 portable JSON 对象中的必需字符串字段。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn portable_string<'a>(value: Option<&'a Value>, field: &str) -> Result<&'a str, AgentError> {
    value.and_then(Value::as_str).ok_or_else(|| {
        AgentError::new(
            ErrorCode::JournalCorrupt,
            format!("{field} must be a string"),
        )
    })
}

/// 功能：创建“不安全历史投影不可用”的稳定结构化错误。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn incompatible_history_error() -> AgentError {
    AgentError::new(
        ErrorCode::JournalCorrupt,
        "portable history contains content this implementation cannot project safely",
    )
    .with_details(json!({"kind":"journal_incompatible"}))
}

#[cfg(test)]
mod tests {
    use std::collections::{BTreeMap, BTreeSet};
    use std::fs;
    use std::path::Path;
    use std::sync::Arc;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::time::Duration;

    use async_trait::async_trait;
    use futures_util::stream;
    use serde_json::{Value, json};
    use tempfile::tempdir;
    use tokio::sync::mpsc;
    use tokio_util::sync::CancellationToken;

    use super::{
        Agent, CompleteToolCall, DEFAULT_APPROVAL_TIMEOUT, QueueKind, RunRequest, approval_request,
        approval_timeout_from_text, operation_hash, portable_tool_result,
        validate_complete_tool_batch,
    };
    use crate::agent_profile::{
        AgentProfileBehavior, AgentProfileModel, AgentProfileRunSnapshot, DangerousActionMode,
        ResponseStyle,
    };
    use crate::domain::{
        ArtifactRef, EventEnvelope, FinishReason, ProviderEvent, ProviderImage, ToolEffect,
        ToolOutput,
    };
    use crate::error::{AgentError, ErrorCode};
    use crate::policy::{ToolPolicy, WorkspaceGuard};
    use crate::protocol::{
        ApprovalDecision, ProviderSelection, SessionBranchSelectParams, SessionCompactParams,
        SessionCompactUsage,
    };
    use crate::provider::{
        FauxProvider, FauxScenario, FauxScript, OpenRouterImagesProvider, Provider,
        ProviderRequest, ProviderStream,
    };
    use crate::session::SessionStore;
    use crate::tools::ToolRegistry;

    /// 功能：验证审批 operation hash 对递归键序和 UTF-8 使用确定性 compact JSON SHA-256。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn operation_hash_is_canonical_and_stable() -> Result<(), AgentError> {
        let arguments = json!({"path":"a.txt","content":"值"});
        assert_eq!(
            operation_hash("file.write", &arguments)?,
            "9f0e665711f1eeb35858845b61f10f2b703237a18290df088d4c9e41207e539d"
        );
        Ok(())
    }

    /// 功能：验证 computer 审批风险与资源精确绑定完整屏幕或单个动作。
    ///
    /// 输入：observe 及 move/click/scroll/key 四个 synthetic 完整工具调用。
    /// 输出：observe=high/desktop:screen，interact=critical/desktop:<action>，选择仅 allow_once/deny。
    /// 不变量：测试不执行工具、不访问 display，也不从先前决定推导持续授权。
    /// 失败：风险、资源或选择集合漂移时断言失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn computer_approvals_are_per_action_and_never_cached() {
        let observe = CompleteToolCall {
            call_id: "call-observe".to_owned(),
            name: "computer.screenshot".to_owned(),
            arguments: json!({}),
        };
        let observe_approval = approval_request(
            "approval-observe",
            &observe,
            ToolEffect::ComputerObserve,
            &"a".repeat(64),
            Duration::from_secs(30),
        );
        assert_eq!(observe_approval["risk"], "high");
        assert_eq!(
            observe_approval["resources"],
            json!([{"kind":"other","value":"desktop:screen"}])
        );
        assert_eq!(observe_approval["choices"], json!(["allow_once", "deny"]));

        for action in ["move", "click", "scroll", "key"] {
            let call = CompleteToolCall {
                call_id: format!("call-{action}"),
                name: "computer.interact".to_owned(),
                arguments: json!({"action":action}),
            };
            let approval = approval_request(
                &format!("approval-{action}"),
                &call,
                ToolEffect::ComputerInteract,
                &"b".repeat(64),
                Duration::from_secs(30),
            );
            assert_eq!(approval["risk"], "critical");
            assert_eq!(
                approval["resources"],
                json!([{"kind":"other","value":format!("desktop:{action}")}])
            );
            assert_eq!(approval["choices"], json!(["allow_once", "deny"]));
        }
    }

    /// 功能：验证 portable ToolResult 对 image MIME 使用 image_ref，普通 artifact 保持 artifact_ref。
    ///
    /// 输入：同一 synthetic ArtifactRef 分别使用 image/png 与 text/plain。
    /// 输出：图像 content 类型为 image_ref，非图像为 artifact_ref。
    /// 不变量：不内联 bytes、base64 或主机路径，artifact 引用字段原样保留。
    /// 失败：MIME 路由或 portable 内容形状漂移时断言失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn portable_tool_result_uses_image_ref_for_image_mime() {
        let mut artifact = ArtifactRef {
            artifact_id: "artifact-image".to_owned(),
            media_type: "image/png".to_owned(),
            byte_length: 8,
            sha256: "a".repeat(64),
            display_name: None,
            extensions: BTreeMap::new(),
        };
        let image = portable_tool_result(
            &ToolOutput {
                text: Some("desktop captured".to_owned()),
                artifact: Some(artifact.clone()),
                termination_reason: None,
                execution: None,
                metadata: BTreeMap::new(),
            },
            false,
        );
        assert_eq!(image["content"][1]["type"], "image_ref");
        assert_eq!(image["content"][1]["artifact"], json!(artifact));

        artifact.media_type = "text/plain".to_owned();
        let ordinary = portable_tool_result(
            &ToolOutput {
                text: None,
                artifact: Some(artifact),
                termination_reason: None,
                execution: None,
                metadata: BTreeMap::new(),
            },
            false,
        );
        assert_eq!(ordinary["content"][0]["type"], "artifact_ref");
    }

    /// 功能：验证客户端输入图片返回前已同时发布文件与 artifact.created，journal 不包含原始字节或路径。
    ///
    /// 不变量：测试只使用本地 synthetic PNG，不访问 Provider 或任何 credential。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn publishes_durable_input_image_artifact() -> Result<(), AgentError> {
        let directory = tempdir()?;
        let state = directory.path().join("state");
        let guard = WorkspaceGuard::new(directory.path())?;
        let sessions = SessionStore::new(&state, 1024 * 1024).await?;
        let tools = Arc::new(ToolRegistry::new(
            guard.clone(),
            ToolPolicy::headless_default(),
            sessions.clone(),
        ));
        let agent = Agent::new(
            BTreeMap::new(),
            tools,
            sessions.clone(),
            guard.root().to_path_buf(),
        );
        let png = b"\x89PNG\r\n\x1a\nfixture";

        let artifact = agent
            .publish_input_image("session-input-image", "image/png", png)
            .await?;

        let records = sessions.load("session-input-image").await?;
        let record = records.first().ok_or_else(|| {
            AgentError::new(ErrorCode::InternalError, "missing artifact.created record")
        })?;
        assert_eq!(records.len(), 1);
        assert_eq!(record.kind, "artifact.created");
        assert_eq!(record.data["artifact"]["artifactId"], artifact.artifact_id);
        let artifact_path = state
            .join("sessions/session-input-image/artifacts")
            .join(&artifact.artifact_id);
        assert_eq!(fs::read(&artifact_path)?, png);
        let journal = fs::read_to_string(state.join("sessions/session-input-image/journal.jsonl"))?;
        assert!(!journal.contains("fixture"));
        assert!(!journal.contains(artifact_path.to_string_lossy().as_ref()));
        Ok(())
    }

    /// 功能：验证图片完成在 MessageEnd/stop 校验失败时不会发布 artifact 或 assistant 假成功。
    ///
    /// 不变量：测试只注入内存 faux 图片事件，不访问网络或 credential；错误结束后的 artifact 目录和 `artifact.created` 均为空。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn rejects_image_finish_before_any_artifact_publication() -> Result<(), AgentError> {
        let directory = tempdir()?;
        let state = directory.path().join("state");
        let (agent, faux, sessions) = build_test_agent(directory.path(), &state).await?;
        let session_id = "session-invalid-image-finish";
        faux.configure(
            session_id,
            vec![FauxScript::from_events(vec![
                ProviderEvent::MessageStart {
                    provider_message_id: "faux-image-invalid".to_owned(),
                },
                ProviderEvent::ImageCompletion {
                    text: Some("不应持久化".to_owned()),
                    images: vec![ProviderImage {
                        media_type: "image/png".to_owned(),
                        bytes: b"\x89PNG\r\n\x1a\nsynthetic-invalid-finish".to_vec(),
                    }],
                },
                ProviderEvent::MessageEnd {
                    finish_reason: FinishReason::Length,
                },
            ])],
        )
        .await;
        let request = RunRequest {
            session_id: session_id.to_owned(),
            run_id: "run-invalid-image-finish".to_owned(),
            prompt: "generate".to_owned(),
            provider_id: "faux".to_owned(),
            api_family: None,
            model: "faux-v1".to_owned(),
            interactive_approvals: false,
        };
        agent.accept_run(&request).await?;
        let (sender, mut receiver) = mpsc::channel(256);
        agent.start_run(request.clone(), sender).await;
        let terminal = loop {
            let event = receive_event(&mut receiver).await?;
            if matches!(
                event.event_type.as_str(),
                "run.completed" | "run.failed" | "run.cancelled" | "run.interrupted"
            ) {
                break event;
            }
        };
        assert_eq!(terminal.event_type, "run.failed");
        assert_eq!(
            terminal.data.pointer("/error/details/kind"),
            Some(&json!("provider_protocol_error"))
        );
        let records = sessions.load(session_id).await?;
        assert_eq!(
            records
                .iter()
                .filter(|record| record.kind == "artifact.created")
                .count(),
            0
        );
        assert!(!records.iter().any(|record| {
            record.kind == "message.appended"
                && record.data.pointer("/message/role").and_then(Value::as_str) == Some("assistant")
        }));
        let artifact_count =
            fs::read_dir(state.join("sessions").join(session_id).join("artifacts"))?.count();
        assert_eq!(artifact_count, 0);
        Ok(())
    }

    /// 功能：验证图片完成后的流尾缺少 MessageEnd 时仍保持零 artifact、零 assistant 消息。
    ///
    /// 不变量：测试只注入内存 faux 断尾，不访问网络或 credential；图片字节始终停留在内存候选中。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn rejects_unterminated_image_stream_before_any_artifact_publication()
    -> Result<(), AgentError> {
        let directory = tempdir()?;
        let state = directory.path().join("state");
        let (agent, faux, sessions) = build_test_agent(directory.path(), &state).await?;
        let session_id = "session-unterminated-image";
        faux.configure(
            session_id,
            vec![FauxScript::from_events(vec![
                ProviderEvent::MessageStart {
                    provider_message_id: "faux-image-unterminated".to_owned(),
                },
                ProviderEvent::ImageCompletion {
                    text: None,
                    images: vec![ProviderImage {
                        media_type: "image/png".to_owned(),
                        bytes: b"\x89PNG\r\n\x1a\nsynthetic-unterminated".to_vec(),
                    }],
                },
            ])],
        )
        .await;
        let request = RunRequest {
            session_id: session_id.to_owned(),
            run_id: "run-unterminated-image".to_owned(),
            prompt: "generate".to_owned(),
            provider_id: "faux".to_owned(),
            api_family: None,
            model: "faux-v1".to_owned(),
            interactive_approvals: false,
        };
        agent.accept_run(&request).await?;
        let (sender, mut receiver) = mpsc::channel(256);
        agent.start_run(request, sender).await;
        let terminal = loop {
            let event = receive_event(&mut receiver).await?;
            if matches!(
                event.event_type.as_str(),
                "run.completed" | "run.failed" | "run.cancelled" | "run.interrupted"
            ) {
                break event;
            }
        };
        assert_eq!(terminal.event_type, "run.interrupted");
        let records = sessions.load(session_id).await?;
        assert_eq!(
            records
                .iter()
                .filter(|record| record.kind == "artifact.created")
                .count(),
            0
        );
        assert!(!records.iter().any(|record| {
            record.kind == "message.appended"
                && record.data.pointer("/message/role").and_then(Value::as_str) == Some("assistant")
        }));
        assert_eq!(
            fs::read_dir(state.join("sessions").join(session_id).join("artifacts"))?.count(),
            0
        );
        Ok(())
    }

    /// 功能：验证审批短 timeout 只接受 conformance 双门后的严格有界十进制文本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn approval_timeout_override_requires_both_gates_and_strict_text() {
        assert_eq!(
            approval_timeout_from_text(true, true, Some("100")),
            Duration::from_millis(100)
        );
        for (cli, environment) in [(false, false), (true, false), (false, true)] {
            assert_eq!(
                approval_timeout_from_text(cli, environment, Some("100")),
                DEFAULT_APPROVAL_TIMEOUT
            );
        }
        for value in [
            None,
            Some(""),
            Some("0"),
            Some("+1"),
            Some(" 1"),
            Some("1 "),
            Some("1_000"),
            Some("3600001"),
            Some("999999999999999999999999999"),
        ] {
            assert_eq!(
                approval_timeout_from_text(true, true, value),
                DEFAULT_APPROVAL_TIMEOUT
            );
        }
    }

    /// 功能：验证 finish、工具名和 run 级重复 ID 在 assistant 消息 durable 前被拒绝。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn rejects_invalid_complete_tool_batch_before_persistence() {
        let valid = CompleteToolCall {
            call_id: "call-valid-1".to_owned(),
            name: "file.read".to_owned(),
            arguments: json!({"path":"x"}),
        };
        assert!(
            validate_complete_tool_batch(
                std::slice::from_ref(&valid),
                FinishReason::Stop,
                &BTreeSet::new()
            )
            .is_err()
        );
        let invalid_name = CompleteToolCall {
            name: "File.Read".to_owned(),
            ..valid.clone()
        };
        assert!(
            validate_complete_tool_batch(
                &[invalid_name],
                FinishReason::ToolCalls,
                &BTreeSet::new()
            )
            .is_err()
        );
        assert!(
            validate_complete_tool_batch(
                &[valid],
                FinishReason::ToolCalls,
                &BTreeSet::from(["call-valid-1".to_owned()])
            )
            .is_err()
        );
    }

    /// 功能：验证图像 Provider 必须由精确 apiFamily 路由，缺失 family 和动态模型均拒绝。
    ///
    /// 输入：未发起网络的真实 OpenRouter Images adapter 与三种 RunRequest selection。
    /// 输出：仅显式 `openrouter-images` 加冻结 modelId 能被选择。
    /// 不变量：测试不设置 credential、不连接 endpoint，也不根据 modelId 猜测 family。
    /// 失败：路由接受/拒绝语义漂移使测试失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn requires_explicit_image_api_family() -> Result<(), AgentError> {
        let directory = tempdir()?;
        let guard = WorkspaceGuard::new(directory.path())?;
        let sessions = SessionStore::new(directory.path().join("state"), 1024).await?;
        let tools = Arc::new(ToolRegistry::new(
            guard.clone(),
            ToolPolicy::headless_default(),
            sessions.clone(),
        ));
        let providers = BTreeMap::from([(
            "openrouter-images-route".to_owned(),
            Arc::new(OpenRouterImagesProvider::new(
                "https://openrouter.ai/api/v1",
                sessions.clone(),
            )) as Arc<dyn Provider>,
        )]);
        let agent = Agent::new(providers, tools, sessions, guard.root().to_path_buf());
        let mut request = RunRequest {
            session_id: "session-image-route".to_owned(),
            run_id: "run-image-route".to_owned(),
            prompt: "draw".to_owned(),
            provider_id: "openrouter".to_owned(),
            api_family: None,
            model: "google/gemini-2.5-flash-image".to_owned(),
            interactive_approvals: false,
        };
        assert!(agent.provider_for(&request).is_err());
        request.api_family = Some("openrouter-images".to_owned());
        assert!(agent.provider_for(&request).is_ok());
        request.model = "dynamic/image-model".to_owned();
        assert!(agent.provider_for(&request).is_err());
        Ok(())
    }

    /// 功能：验证显式 faux Profile 的完整模型三元组原样写入 durable run.accepted。
    ///
    /// 输入：逻辑 `apiFamily=faux`、兼容旧注册方式的 Faux adapter 与冻结 Profile 快照。
    /// 输出：provider selection 和 snapshot model 均逐字段保留相同三元组。
    /// 不变量：adapter 内部兼容不得把 wire/journal 的 apiFamily 归一为空。
    /// 失败：路由无法选择或任一 durable 字段漂移时测试失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn faux_profile_preserves_exact_provider_triple_in_run_accepted() -> Result<(), AgentError>
    {
        let directory = tempdir()?;
        let state = directory.path().join("state");
        let (agent, _, sessions) = build_test_agent(directory.path(), &state).await?;
        let request = RunRequest {
            session_id: "session-faux-profile-triple".to_owned(),
            run_id: "run-faux-profile-triple".to_owned(),
            prompt: "review".to_owned(),
            provider_id: "faux".to_owned(),
            api_family: Some("faux".to_owned()),
            model: "faux-v1".to_owned(),
            interactive_approvals: false,
        };
        let snapshot = AgentProfileRunSnapshot {
            profile_id: "agent-faux-profile-triple".to_owned(),
            revision: 3,
            display_name: "Reviewer".to_owned(),
            instructions: "Review the pending change.".to_owned(),
            model: AgentProfileModel {
                provider_id: "faux".to_owned(),
                model_id: "faux-v1".to_owned(),
                api_family: "faux".to_owned(),
            },
            requested_tool_ids: vec!["file.read".to_owned()],
            effective_tool_ids: vec!["file.read".to_owned()],
            dangerous_action_mode: DangerousActionMode::Deny,
            behavior: AgentProfileBehavior {
                response_style: ResponseStyle::Balanced,
                plan_first: true,
                review_changes: true,
            },
        };
        agent
            .accept_run_content_with_profile(
                &request,
                vec![crate::domain::ContentBlock::Text {
                    text: "review".to_owned(),
                }],
                Some(snapshot),
            )
            .await?;
        let records = sessions.load(&request.session_id).await?;
        let accepted = records
            .iter()
            .find(|record| record.kind == "run.accepted")
            .ok_or_else(|| AgentError::new(ErrorCode::InternalError, "missing run.accepted"))?;
        let expected_triple = json!({
            "id":"faux",
            "modelId":"faux-v1",
            "apiFamily":"faux"
        });
        assert_eq!(accepted.data["provider"], expected_triple);
        assert_eq!(
            accepted.data["agentProfileSnapshot"]["model"],
            json!({
                "providerId":"faux",
                "modelId":"faux-v1",
                "apiFamily":"faux"
            })
        );
        Ok(())
    }

    /// 功能：为审批 timeout/disconnect 测试配置一轮写工具和一轮正常 continuation。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn configure_write_approval_scenario(
        faux: &FauxProvider,
        session_id: &str,
    ) -> Result<(), AgentError> {
        let scenario: FauxScenario = serde_json::from_value(json!({
            "schemaVersion":"0.1",
            "name":"approval-resolution-test",
            "seed":801,
            "steps":[{
                "type":"tool_call",
                "toolCallId":"approval-call-1",
                "name":"file.write",
                "arguments":{"path":"approval.txt","content":"must not be written"}
            }],
            "continuations":[{
                "steps":[{"type":"text","text":"denial observed"}]
            }]
        }))?;
        faux.configure(session_id, scenario.into_scripts()).await;
        Ok(())
    }

    /// 功能：验证无人响应的 pending approval 在 deadline 后 durable timeout deny 且 run 不挂起。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn approval_timeout_denies_and_resumes_run() -> Result<(), AgentError> {
        let directory = tempdir()?;
        let state = directory.path().join("state");
        let (agent, faux, sessions) =
            build_test_agent_with_timeout(directory.path(), &state, Duration::from_millis(20))
                .await?;
        configure_write_approval_scenario(&faux, "session-timeout").await?;
        let request = RunRequest {
            session_id: "session-timeout".to_owned(),
            run_id: "run-timeout".to_owned(),
            prompt: "request write".to_owned(),
            provider_id: "faux".to_owned(),
            api_family: None,
            model: "faux-v1".to_owned(),
            interactive_approvals: true,
        };
        agent.accept_run(&request).await?;
        let (sender, mut receiver) = mpsc::channel(256);
        agent.start_run(request.clone(), sender).await;
        let mut events = Vec::new();
        loop {
            let event = receive_event(&mut receiver).await?;
            let terminal = event.event_type == "run.completed";
            events.push(event);
            if terminal {
                break;
            }
        }
        assert!(events.iter().any(|event| {
            event.event_type == "approval.resolved" && event.data["resolutionSource"] == "timeout"
        }));
        assert!(
            !events
                .iter()
                .any(|event| event.event_type == "tool.started")
        );
        assert!(!directory.path().join("approval.txt").exists());
        let records = sessions.load(&request.session_id).await?;
        assert!(records.iter().any(|record| {
            record.kind == "approval.resolved" && record.data["resolutionSource"] == "timeout"
        }));
        Ok(())
    }

    /// 功能：验证客户端断连 durable disconnect deny pending approval 并解除 Agent waiter。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn approval_disconnect_denies_and_resumes_run() -> Result<(), AgentError> {
        let directory = tempdir()?;
        let state = directory.path().join("state");
        let (agent, faux, sessions) = build_test_agent(directory.path(), &state).await?;
        configure_write_approval_scenario(&faux, "session-disconnect").await?;
        let request = RunRequest {
            session_id: "session-disconnect".to_owned(),
            run_id: "run-disconnect".to_owned(),
            prompt: "request write".to_owned(),
            provider_id: "faux".to_owned(),
            api_family: None,
            model: "faux-v1".to_owned(),
            interactive_approvals: true,
        };
        agent.accept_run(&request).await?;
        let (sender, mut receiver) = mpsc::channel(256);
        agent.start_run(request.clone(), sender).await;
        loop {
            let event = receive_event(&mut receiver).await?;
            if event.event_type == "approval.requested" {
                break;
            }
        }
        assert_eq!(agent.resolve_disconnected_approvals().await?, 1);
        let mut saw_disconnect = false;
        loop {
            let event = receive_event(&mut receiver).await?;
            saw_disconnect |= event.event_type == "approval.resolved"
                && event.data["resolutionSource"] == "disconnect";
            if event.event_type == "run.completed" {
                break;
            }
        }
        assert!(saw_disconnect);
        assert!(!directory.path().join("approval.txt").exists());
        let records = sessions.load(&request.session_id).await?;
        assert_eq!(
            records
                .iter()
                .filter(|record| record.kind == "approval.resolved")
                .count(),
            1
        );
        Ok(())
    }

    /// 功能：验证重复/迟到审批固定 conflict，且 success 未交付时只审计一次 client allow 并零执行收尾。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn approval_delivery_failure_persists_tool_error_without_releasing_allow()
    -> Result<(), AgentError> {
        let directory = tempdir()?;
        let state = directory.path().join("state");
        let (agent, faux, sessions) = build_test_agent(directory.path(), &state).await?;
        configure_write_approval_scenario(&faux, "session-delivery-failure").await?;
        let request = RunRequest {
            session_id: "session-delivery-failure".to_owned(),
            run_id: "run-delivery-failure".to_owned(),
            prompt: "request write".to_owned(),
            provider_id: "faux".to_owned(),
            api_family: None,
            model: "faux-v1".to_owned(),
            interactive_approvals: true,
        };
        agent.accept_run(&request).await?;
        let (sender, mut receiver) = mpsc::channel(256);
        agent.start_run(request.clone(), sender).await;
        let approval_id = loop {
            let event = receive_event(&mut receiver).await?;
            if event.event_type == "approval.requested" {
                break event.data["approval"]["approvalId"]
                    .as_str()
                    .ok_or_else(|| {
                        AgentError::new(
                            ErrorCode::InternalError,
                            "approval test event omitted approvalId",
                        )
                    })?
                    .to_owned();
            }
        };
        let decision = ApprovalDecision {
            choice: "allow_once".to_owned(),
            reason: None,
            extensions: BTreeMap::new(),
        };
        let resume = agent
            .accept_approval(
                &request.session_id,
                &request.run_id,
                &approval_id,
                decision.clone(),
            )
            .await?;
        let duplicate = match agent
            .accept_approval(
                &request.session_id,
                &request.run_id,
                &approval_id,
                decision.clone(),
            )
            .await
        {
            Ok(_) => panic!("duplicate approval must conflict"),
            Err(error) => error,
        };
        assert_eq!(duplicate.code, ErrorCode::Conflict);
        assert_eq!(duplicate.code.rpc_code(), -32010);
        assert!(!duplicate.retryable);

        drop(resume);
        let mut event_types = Vec::new();
        loop {
            let event = receive_event(&mut receiver).await?;
            let terminal = matches!(
                event.event_type.as_str(),
                "run.completed" | "run.failed" | "run.interrupted" | "run.cancelled"
            );
            event_types.push(event.event_type);
            if terminal {
                break;
            }
        }
        assert!(!event_types.iter().any(|kind| kind == "approval.resolved"));
        assert!(!event_types.iter().any(|kind| kind == "tool.started"));
        assert!(event_types.iter().any(|kind| kind == "tool.completed"));
        assert_eq!(
            event_types.last().map(String::as_str),
            Some("run.interrupted")
        );
        assert!(!directory.path().join("approval.txt").exists());

        let late = match agent
            .accept_approval(&request.session_id, &request.run_id, &approval_id, decision)
            .await
        {
            Ok(_) => panic!("late approval must conflict"),
            Err(error) => error,
        };
        assert_eq!(late.code, ErrorCode::Conflict);
        assert_eq!(late.code.rpc_code(), -32010);
        assert!(!late.retryable);

        let records = sessions.load(&request.session_id).await?;
        assert_eq!(
            records
                .iter()
                .filter(|record| record.kind == "approval.resolved")
                .count(),
            1
        );
        assert_eq!(
            records
                .iter()
                .filter(|record| record.kind == "tool.result")
                .count(),
            1
        );
        let terminals = records
            .iter()
            .filter(|record| record.kind == "run.terminal")
            .collect::<Vec<_>>();
        assert_eq!(terminals.len(), 1);
        assert_eq!(terminals[0].data["status"], "interrupted");
        Ok(())
    }

    /// 功能：验证自然终态与取消共享 winner 仲裁且不会追加相互矛盾的 durable 事实。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn terminal_and_cancellation_have_one_durable_winner() -> Result<(), AgentError> {
        let completed_directory = tempdir()?;
        let completed_state = completed_directory.path().join("state");
        let (completed_agent, _, completed_sessions) =
            build_test_agent(completed_directory.path(), &completed_state).await?;
        let completed_request = RunRequest {
            session_id: "session-natural-winner".to_owned(),
            run_id: "run-natural-winner".to_owned(),
            prompt: "complete".to_owned(),
            provider_id: "faux".to_owned(),
            api_family: None,
            model: "faux-v1".to_owned(),
            interactive_approvals: false,
        };
        completed_agent.accept_run(&completed_request).await?;
        let completed_control = completed_agent
            .active
            .lock()
            .await
            .get(&completed_request.run_id)
            .cloned()
            .ok_or_else(|| AgentError::new(ErrorCode::InternalError, "control missing"))?;
        let (completed_sender, _) = mpsc::channel(8);
        completed_agent
            .finalize_run(
                &completed_request,
                &completed_control,
                &completed_sender,
                None,
            )
            .await?;
        assert_eq!(
            completed_agent
                .cancel(&completed_request.session_id, &completed_request.run_id)
                .await?,
            "terminal"
        );
        let completed_records = completed_sessions
            .load(&completed_request.session_id)
            .await?;
        assert_eq!(
            completed_records
                .iter()
                .filter(|record| record.kind == "run.terminal")
                .count(),
            1
        );
        assert!(
            !completed_records
                .iter()
                .any(|record| record.kind == "run.cancellation_requested")
        );

        let cancelled_directory = tempdir()?;
        let cancelled_state = cancelled_directory.path().join("state");
        let (cancelled_agent, _, cancelled_sessions) =
            build_test_agent(cancelled_directory.path(), &cancelled_state).await?;
        let cancelled_request = RunRequest {
            session_id: "session-cancel-winner".to_owned(),
            run_id: "run-cancel-winner".to_owned(),
            prompt: "cancel".to_owned(),
            provider_id: "faux".to_owned(),
            api_family: None,
            model: "faux-v1".to_owned(),
            interactive_approvals: false,
        };
        cancelled_agent.accept_run(&cancelled_request).await?;
        let cancelled_control = cancelled_agent
            .active
            .lock()
            .await
            .get(&cancelled_request.run_id)
            .cloned()
            .ok_or_else(|| AgentError::new(ErrorCode::InternalError, "control missing"))?;
        assert_eq!(
            cancelled_agent
                .cancel(&cancelled_request.session_id, &cancelled_request.run_id)
                .await?,
            "requested"
        );
        let (cancelled_sender, mut cancelled_receiver) = mpsc::channel(8);
        cancelled_agent
            .finalize_run(
                &cancelled_request,
                &cancelled_control,
                &cancelled_sender,
                None,
            )
            .await?;
        let terminal = receive_event(&mut cancelled_receiver).await?;
        assert_eq!(terminal.event_type, "run.cancelled");
        let cancelled_records = cancelled_sessions
            .load(&cancelled_request.session_id)
            .await?;
        assert_eq!(
            cancelled_records
                .iter()
                .filter(|record| record.kind == "run.terminal")
                .count(),
            1
        );
        assert_eq!(
            cancelled_records
                .iter()
                .filter(|record| record.kind == "run.cancellation_requested")
                .count(),
            1
        );
        Ok(())
    }

    /// 功能：为 Provider attempt 日志测试提供一次 429 后成功的纯内存 Provider。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    struct RetryOnceProvider {
        calls: AtomicUsize,
    }

    #[async_trait]
    impl Provider for RetryOnceProvider {
        /// 功能：返回测试 Provider 的稳定注册标识。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        fn id(&self) -> &str {
            "retry-once"
        }

        /// 功能：第一次调用返回可重试 429，第二次返回确定性完成流。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        async fn stream(
            &self,
            _request: ProviderRequest,
            _cancellation: CancellationToken,
        ) -> Result<ProviderStream, AgentError> {
            if self.calls.fetch_add(1, Ordering::SeqCst) == 0 {
                return Err(AgentError::new(
                    ErrorCode::ProviderRateLimited,
                    "synthetic rate limit",
                )
                .retryable(true)
                .with_details(json!({
                    "kind":"provider_rate_limited",
                    "httpStatus":429,
                    "retryAfterMs":0
                })));
            }
            Ok(Box::pin(stream::iter(vec![
                Ok(ProviderEvent::MessageStart {
                    provider_message_id: "retry-success".to_owned(),
                }),
                Ok(ProviderEvent::TextDelta {
                    text: "done".to_owned(),
                }),
                Ok(ProviderEvent::Usage(crate::domain::Usage {
                    input_tokens: 7,
                    output_tokens: 5,
                    total_tokens: 12,
                    ..crate::domain::Usage::default()
                })),
                Ok(ProviderEvent::MessageEnd {
                    finish_reason: FinishReason::Stop,
                }),
            ])))
        }
    }

    /// 功能：为 Agent 测试构造完全离线的 faux 依赖图和 portable Session 存储。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn build_test_agent(
        workspace: &Path,
        state: &Path,
    ) -> Result<(Arc<Agent>, FauxProvider, SessionStore), AgentError> {
        build_test_agent_with_timeout(workspace, state, DEFAULT_APPROVAL_TIMEOUT).await
    }

    /// 功能：为审批 deadline 测试构造可注入短超时的完全离线 Agent。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn build_test_agent_with_timeout(
        workspace: &Path,
        state: &Path,
        approval_timeout: Duration,
    ) -> Result<(Arc<Agent>, FauxProvider, SessionStore), AgentError> {
        let guard = WorkspaceGuard::new(workspace)?;
        let sessions = SessionStore::new(state, 1024 * 1024).await?;
        let tools = Arc::new(ToolRegistry::new(
            guard.clone(),
            ToolPolicy::headless_default(),
            sessions.clone(),
        ));
        let faux = FauxProvider::new();
        let providers = BTreeMap::from([(
            "faux".to_owned(),
            Arc::new(faux.clone()) as Arc<dyn Provider>,
        )]);
        let mut agent = Agent::new(
            providers,
            tools,
            sessions.clone(),
            guard.root().to_path_buf(),
        );
        agent.approval_timeout = approval_timeout;
        Ok((Arc::new(agent), faux, sessions))
    }

    /// 功能：验证已接受但尚未结束的 run 会优先拒绝 branch/compaction mutation，且失败不追加日志。
    ///
    /// 输入：同一 Session 上一个 durable accepted run，以及故意 stale/无效的 mutation 参数。
    /// 输出：两项 mutation 均返回 `-32004/session_busy/retryable`，journal 字节保持完全不变。
    /// 不变量：busy 检查先于 expected-head、target、boundary 与 token 校验，避免并发 mutation 观察半完成生命周期。
    /// 失败：错误映射、校验优先级或零追加保证漂移时测试失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn active_run_rejects_session_mutations_without_appending() -> Result<(), AgentError> {
        let directory = tempdir()?;
        let state = directory.path().join("state");
        let (agent, _, _) = build_test_agent(directory.path(), &state).await?;
        let request = RunRequest {
            session_id: "session-mutation-busy".to_owned(),
            run_id: "run-mutation-busy".to_owned(),
            prompt: "keep this run accepted".to_owned(),
            provider_id: "faux".to_owned(),
            api_family: None,
            model: "faux-v1".to_owned(),
            interactive_approvals: false,
        };
        agent.accept_run(&request).await?;
        let journal_path = state
            .join("sessions")
            .join(&request.session_id)
            .join("journal.jsonl");
        let before = fs::read(&journal_path)?;

        let branch_error = agent
            .select_session_branch(SessionBranchSelectParams {
                session_id: request.session_id.clone(),
                expected_head_record_id: "record-stale-head".to_owned(),
                target_leaf_record_id: "record-missing-target".to_owned(),
            })
            .await
            .expect_err("active run must reject branch selection");
        assert_eq!(branch_error.code, ErrorCode::RunConflict);
        assert!(branch_error.retryable);
        assert_eq!(branch_error.details, json!({"kind":"session_busy"}));
        assert_eq!(fs::read(&journal_path)?, before);

        let compact_error = agent
            .compact_session(SessionCompactParams {
                session_id: request.session_id,
                expected_head_record_id: "record-stale-head".to_owned(),
                first_retained_record_id: "record-invalid-boundary".to_owned(),
                summary_text: String::new(),
                provider: ProviderSelection {
                    id: "faux".to_owned(),
                    model_id: "faux-v1".to_owned(),
                    api_family: None,
                    extensions: BTreeMap::new(),
                },
                usage: SessionCompactUsage {
                    input_tokens: 1,
                    output_tokens: 1,
                    total_tokens: 2,
                    cached_input_tokens: None,
                    cache_write_tokens: None,
                    reasoning_tokens: None,
                    cost: None,
                    extensions: BTreeMap::new(),
                },
                tokens_before: 1,
                tokens_after: 2,
                strategy: String::new(),
            })
            .await
            .expect_err("active run must reject context compaction");
        assert_eq!(compact_error.code, ErrorCode::RunConflict);
        assert!(compact_error.retryable);
        assert_eq!(compact_error.details, json!({"kind":"session_busy"}));
        assert_eq!(fs::read(journal_path)?, before);
        Ok(())
    }

    /// 功能：在固定期限内接收下一个 Agent 事件，并把关闭或超时转换为结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn receive_event(
        receiver: &mut mpsc::Receiver<EventEnvelope>,
    ) -> Result<EventEnvelope, AgentError> {
        tokio::time::timeout(Duration::from_secs(5), receiver.recv())
            .await
            .map_err(|_| AgentError::new(ErrorCode::Timeout, "test event receive timed out"))?
            .ok_or_else(|| {
                AgentError::new(
                    ErrorCode::StreamInterrupted,
                    "test event channel closed before terminal event",
                )
            })
    }

    /// 功能：安装跨实现 portable Session fixture 到 Rust 临时状态根。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn install_session_fixture(
        state: &Path,
        session_id: &str,
        content: &str,
    ) -> Result<(), std::io::Error> {
        let directory = state.join("sessions").join(session_id);
        fs::create_dir_all(directory.join("artifacts"))?;
        fs::write(directory.join("journal.jsonl"), content)
    }

    /// 功能：验证真实 retry 决策为每次 Provider attempt 持久化完整且有序的状态事实。
    ///
    /// 输入：纯内存 Provider 第一次返回带零等待的 429，第二次返回成功流。
    /// 输出：run 完成，日志依次包含 attempt 1 started/failed 与 attempt 2 started/completed。
    /// 不变量：失败记录保留结构化错误和 `retryAfterMs`，不记录请求正文或凭据。
    /// 失败：任何事件、重试或 Session 日志语义偏离预期都会使测试失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn journals_each_provider_attempt_in_retry_order() -> Result<(), AgentError> {
        let directory = tempdir()?;
        let state = directory.path().join("state");
        let guard = WorkspaceGuard::new(directory.path())?;
        let sessions = SessionStore::new(&state, 1024 * 1024).await?;
        let tools = Arc::new(ToolRegistry::new(
            guard.clone(),
            ToolPolicy::headless_default(),
            sessions.clone(),
        ));
        let providers = BTreeMap::from([(
            "retry-once".to_owned(),
            Arc::new(RetryOnceProvider {
                calls: AtomicUsize::new(0),
            }) as Arc<dyn Provider>,
        )]);
        let agent = Arc::new(Agent::new(
            providers,
            tools,
            sessions.clone(),
            guard.root().to_path_buf(),
        ));
        let request = RunRequest {
            session_id: "session-provider-attempt".to_owned(),
            run_id: "run-provider-attempt".to_owned(),
            prompt: "retry once".to_owned(),
            provider_id: "retry-once".to_owned(),
            api_family: None,
            model: "retry-model".to_owned(),
            interactive_approvals: false,
        };
        agent.accept_run(&request).await?;
        let (sender, mut receiver) = mpsc::channel(256);
        agent.start_run(request.clone(), sender).await;
        let mut event_types = Vec::new();
        loop {
            let event = receive_event(&mut receiver).await?;
            let terminal = event.event_type == "run.completed";
            event_types.push(event.event_type);
            if terminal {
                break;
            }
        }
        assert_eq!(
            event_types
                .iter()
                .filter(|event_type| event_type.as_str() == "retry.scheduled")
                .count(),
            1
        );

        let records = sessions.load(&request.session_id).await?;
        let attempts = records
            .iter()
            .filter(|record| record.kind == "provider.attempt")
            .map(|record| record.data.clone())
            .collect::<Vec<_>>();
        assert_eq!(attempts.len(), 4);
        assert_eq!(attempts[0]["attempt"], 1);
        assert_eq!(attempts[0]["status"], "started");
        assert_eq!(attempts[1]["attempt"], 1);
        assert_eq!(attempts[1]["status"], "failed");
        assert_eq!(attempts[1]["retryAfterMs"], 0);
        assert_eq!(attempts[1]["error"]["code"], -32005);
        assert_eq!(
            attempts[1]["error"]["details"]["kind"],
            "provider_rate_limited"
        );
        assert_eq!(attempts[2]["attempt"], 2);
        assert_eq!(attempts[2]["status"], "started");
        assert_eq!(attempts[3]["attempt"], 2);
        assert_eq!(attempts[3]["status"], "completed");
        assert!(attempts[0].get("error").is_none());
        assert!(attempts[2].get("error").is_none());
        assert!(attempts[3].get("error").is_none());
        Ok(())
    }

    /// 功能：验证 steering 优先、follow-up FIFO、消费先落盘以及终态后拒绝排队。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn consumes_durable_queues_in_fifo_order() -> Result<(), AgentError> {
        let directory = tempdir()?;
        let state = directory.path().join("state");
        let (agent, faux, sessions) = build_test_agent(directory.path(), &state).await?;
        let queue_scripts = ["first", "faux: steer-one", "faux: follow-two"]
            .into_iter()
            .enumerate()
            .map(|(index, text)| {
                FauxScript::from_events(vec![
                    ProviderEvent::MessageStart {
                        provider_message_id: format!("faux-queue-{index}"),
                    },
                    ProviderEvent::TextDelta {
                        text: text.to_owned(),
                    },
                    ProviderEvent::MessageEnd {
                        finish_reason: FinishReason::Stop,
                    },
                ])
            })
            .collect();
        faux.configure("session-queue", queue_scripts).await;
        let request = RunRequest {
            session_id: "session-queue".to_owned(),
            run_id: "run-queue".to_owned(),
            prompt: "initial".to_owned(),
            provider_id: "faux".to_owned(),
            api_family: None,
            model: "faux-v1".to_owned(),
            interactive_approvals: false,
        };
        agent.accept_run(&request).await?;
        let steering_id = agent
            .enqueue(
                &request.session_id,
                &request.run_id,
                "steer-one".to_owned(),
                QueueKind::Steering,
            )
            .await?;
        let follow_one_id = agent
            .enqueue(
                &request.session_id,
                &request.run_id,
                "follow-one".to_owned(),
                QueueKind::FollowUp,
            )
            .await?;
        let follow_two_id = agent
            .enqueue(
                &request.session_id,
                &request.run_id,
                "follow-two".to_owned(),
                QueueKind::FollowUp,
            )
            .await?;

        let (sender, mut receiver) = mpsc::channel(256);
        agent.start_run(request.clone(), sender).await;
        let mut events = Vec::new();
        loop {
            let event = receive_event(&mut receiver).await?;
            let terminal = event.event_type == "run.completed";
            events.push(event);
            if terminal {
                break;
            }
        }

        let text_deltas = events
            .iter()
            .filter(|event| event.event_type == "message.delta")
            .filter_map(|event| event.data.pointer("/delta/text").and_then(Value::as_str))
            .collect::<Vec<_>>();
        assert_eq!(
            text_deltas,
            ["first", "faux: steer-one", "faux: follow-two"]
        );

        let records = sessions.load(&request.session_id).await?;
        let consumed = records
            .iter()
            .filter(|record| record.kind == "queue.consumed")
            .collect::<Vec<_>>();
        assert_eq!(consumed.len(), 2);
        assert_eq!(consumed[0].data["queue"], "steering");
        assert_eq!(consumed[0].data["queueItemIds"], json!([steering_id]));
        assert_eq!(consumed[1].data["queue"], "follow_up");
        assert_eq!(
            consumed[1].data["queueItemIds"],
            json!([follow_one_id, follow_two_id])
        );

        let persisted_events = records
            .iter()
            .filter(|record| record.kind == "event.emitted")
            .map(|record| record.data["event"].clone())
            .collect::<Vec<_>>();
        let observed_events = events
            .iter()
            .map(serde_json::to_value)
            .collect::<Result<Vec<_>, _>>()?;
        assert_eq!(persisted_events, observed_events);
        assert!(!records.iter().any(|record| {
            record.kind == "extension" && record.data["namespace"] == "org.agentprotocol.event"
        }));

        let error = agent
            .enqueue(
                &request.session_id,
                &request.run_id,
                "too-late".to_owned(),
                QueueKind::FollowUp,
            )
            .await
            .expect_err("terminal run must reject queued input");
        assert_eq!(error.code, ErrorCode::RunNotFound);
        Ok(())
    }

    /// 功能：验证重复工具调用标识不会二次执行，且日志只保存一条含完整参数的 canonical intent。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn rejects_duplicate_tool_call_id_after_single_canonical_intent() -> Result<(), AgentError>
    {
        let directory = tempdir()?;
        fs::write(
            directory.path().join("README.md"),
            "canonical tool intent fixture",
        )?;
        let state = directory.path().join("state");
        let (agent, faux, sessions) = build_test_agent(directory.path(), &state).await?;
        let tool_script = || {
            FauxScript::from_events(vec![
                ProviderEvent::MessageStart {
                    provider_message_id: "faux-tool-message".to_owned(),
                },
                ProviderEvent::ToolCallStart {
                    call_id: "call-canonical-1".to_owned(),
                    name: "file.read".to_owned(),
                },
                ProviderEvent::ToolCallArgumentsDelta {
                    call_id: "call-canonical-1".to_owned(),
                    delta: r#"{"path":"README.md"}"#.to_owned(),
                },
                ProviderEvent::ToolCallEnd {
                    call_id: "call-canonical-1".to_owned(),
                },
                ProviderEvent::MessageEnd {
                    finish_reason: FinishReason::ToolCalls,
                },
            ])
        };
        faux.configure(
            "session-canonical-intent",
            vec![tool_script(), tool_script()],
        )
        .await;
        let request = RunRequest {
            session_id: "session-canonical-intent".to_owned(),
            run_id: "run-canonical-intent".to_owned(),
            prompt: "read the fixture".to_owned(),
            provider_id: "faux".to_owned(),
            api_family: None,
            model: "faux-v1".to_owned(),
            interactive_approvals: false,
        };
        agent.accept_run(&request).await?;
        let (sender, mut receiver) = mpsc::channel(256);
        agent.start_run(request.clone(), sender).await;

        let mut events = Vec::new();
        loop {
            let event = receive_event(&mut receiver).await?;
            let terminal = matches!(
                event.event_type.as_str(),
                "run.completed" | "run.failed" | "run.cancelled" | "run.interrupted"
            );
            events.push(event);
            if terminal {
                break;
            }
        }
        assert_eq!(
            events
                .iter()
                .filter(|event| event.event_type == "tool.requested")
                .count(),
            1
        );
        assert_eq!(
            events.last().map(|event| event.event_type.as_str()),
            Some("run.failed")
        );
        assert_eq!(
            events
                .last()
                .and_then(|event| event.data.pointer("/error/details/kind"))
                .and_then(Value::as_str),
            Some("duplicate_tool_call_id")
        );

        let records = sessions.load(&request.session_id).await?;
        let intents = records
            .iter()
            .filter(|record| record.kind == "tool.intent")
            .collect::<Vec<_>>();
        assert_eq!(intents.len(), 1);
        assert_eq!(intents[0].data["toolCallId"], "call-canonical-1");
        assert_eq!(intents[0].data["name"], "file.read");
        assert_eq!(intents[0].data["arguments"], json!({"path":"README.md"}));
        assert_eq!(intents[0].data["status"], "started");
        let results = records
            .iter()
            .filter(|record| record.kind == "tool.result")
            .collect::<Vec<_>>();
        assert_eq!(results.len(), 1);
        assert_eq!(results[0].data["toolCallId"], "call-canonical-1");
        Ok(())
    }

    /// 功能：验证共享崩溃 fixture 恢复、afterSeq、真实 active task、历史注入和事件序号续接。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn recovers_shared_session_and_injects_complete_history() -> Result<(), AgentError> {
        let directory = tempdir()?;
        let state = directory.path().join("state");
        fs::create_dir_all(state.join("sessions"))?;
        install_session_fixture(
            &state,
            "session-portable-1",
            include_str!(
                "../../CONFORMANCE/fixtures/session/portable-v0.1/journal.before-recovery.jsonl"
            ),
        )?;
        let (agent, faux, sessions) = build_test_agent(directory.path(), &state).await?;

        let recovered = agent.session_get("session-portable-1", 4).await?;
        assert_eq!(recovered.latest_seq, 9);
        assert_eq!(recovered.active_run_id, None);
        assert_eq!(recovered.messages.len(), 5);
        assert_eq!(
            recovered
                .events
                .iter()
                .filter_map(|event| event["seq"].as_u64())
                .collect::<Vec<_>>(),
            [5, 6, 7, 8, 9]
        );
        assert_eq!(recovered.messages[4]["role"], "tool");
        assert_eq!(recovered.messages[4]["isError"], true);

        let mut scenario_value: Value = serde_json::from_str(include_str!(
            "../../CONFORMANCE/fixtures/session/portable-v0.1/continuation.scenario.json"
        ))?;
        scenario_value["steps"]
            .as_array_mut()
            .ok_or_else(|| AgentError::new(ErrorCode::InternalError, "scenario steps missing"))?
            .insert(0, json!({"type":"delay","durationMs":100}));
        let scenario: FauxScenario = serde_json::from_value(scenario_value)?;
        faux.configure("session-portable-1", vec![scenario.into_script()])
            .await;
        let request = RunRequest {
            session_id: "session-portable-1".to_owned(),
            run_id: "run-continuation-rust".to_owned(),
            prompt: "What token did I ask you to remember?".to_owned(),
            provider_id: "faux".to_owned(),
            api_family: None,
            model: "faux-v1".to_owned(),
            interactive_approvals: false,
        };
        agent.accept_run(&request).await?;
        let (sender, mut receiver) = mpsc::channel(256);
        agent.start_run(request.clone(), sender).await;
        let first = receive_event(&mut receiver).await?;
        assert_eq!(first.event_type, "run.started");
        assert_eq!(first.seq, 10);
        let active = agent.session_get("session-portable-1", 9).await?;
        assert_eq!(
            active.active_run_id.as_deref(),
            Some(request.run_id.as_str())
        );

        let mut events = vec![first];
        loop {
            let event = receive_event(&mut receiver).await?;
            let terminal = event.event_type == "run.completed";
            events.push(event);
            if terminal {
                break;
            }
        }
        assert!(events.iter().any(|event| {
            event.event_type == "message.delta"
                && event.data.pointer("/delta/text").and_then(Value::as_str)
                    == Some("The recovered token is ALPHA.")
        }));
        let completed = agent.session_get("session-portable-1", 9).await?;
        assert_eq!(completed.active_run_id, None);
        assert_eq!(completed.latest_seq, 15);
        assert_eq!(completed.messages.len(), 7);
        assert!(
            sessions
                .load("session-portable-1")
                .await?
                .iter()
                .any(|record| {
                    record.kind == "tool.result"
                        && record.data["outcomeKnown"] == Value::Bool(false)
                })
        );
        Ok(())
    }
}
