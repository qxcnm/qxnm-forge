use std::collections::{BTreeMap, BTreeSet, VecDeque};
use std::sync::{Arc, OnceLock};
use std::time::Duration;

use async_stream::try_stream;
use async_trait::async_trait;
use regex::Regex;
use serde::{Deserialize, Deserializer, Serialize, de};
use serde_json::{Map, Value, json};
use tokio::sync::Mutex;
use tokio_util::sync::CancellationToken;
use uuid::Uuid;

use super::{Provider, ProviderRequest, ProviderStream, last_user_text};
use crate::domain::{ContentBlock, FinishReason, Message, ProviderEvent, Role, Usage};
use crate::error::{AgentError, ErrorCode};

const TOOL_ARGUMENT_CHUNK_CHARACTERS: usize = 8;
const MAX_SAFE_INTEGER: u64 = 9_007_199_254_740_991;

static SCENARIO_NAME_PATTERN: OnceLock<Regex> = OnceLock::new();
static OPAQUE_ID_PATTERN: OnceLock<Regex> = OnceLock::new();
static TOOL_NAME_PATTERN: OnceLock<Regex> = OnceLock::new();
static ERROR_KIND_PATTERN: OnceLock<Regex> = OnceLock::new();
static PROTOCOL_VERSION_PATTERN: OnceLock<Regex> = OnceLock::new();
static CURRENCY_PATTERN: OnceLock<Regex> = OnceLock::new();
static MONEY_AMOUNT_PATTERN: OnceLock<Regex> = OnceLock::new();
static EXTENSION_NAME_PATTERN: OnceLock<Regex> = OnceLock::new();

/// 一次确定性的 Provider 调用脚本。
#[derive(Debug, Clone)]
pub struct FauxScript {
    actions: Vec<FauxAction>,
    expected_context: Option<Vec<Value>>,
}

impl FauxScript {
    /// 功能：把现有规范化 Provider 事件包装为不含延迟或故障的 faux 脚本。
    ///
    /// 输入：按产出顺序排列的 Provider 事件。
    /// 输出：可由 `FauxProvider` 单次消费的确定性脚本。
    /// 不变量：事件顺序保持不变，且不会访问网络或真实凭据。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn from_events(events: Vec<ProviderEvent>) -> Self {
        Self {
            actions: events.into_iter().map(FauxAction::Event).collect(),
            expected_context: None,
        }
    }
}

/// faux 脚本中可观察事件与控制动作的内部统一表示。
#[derive(Debug, Clone)]
enum FauxAction {
    Event(ProviderEvent),
    Delay(Duration),
    Error(Box<FauxError>),
    Disconnect(Option<String>),
}

/// `faux/configure` 使用的规范化确定性场景。
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct FauxScenario {
    pub schema_version: String,
    pub name: String,
    pub seed: u32,
    pub steps: Vec<FauxStep>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub expected_context: Option<Vec<Value>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub usage: Option<FauxUsage>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub continuations: Vec<FauxContinuation>,
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub extensions: BTreeMap<String, Value>,
}

/// faux scenario 的反序列化中间结构；字段形状严格拒绝未知属性。
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FauxScenarioWire {
    schema_version: String,
    name: String,
    seed: u32,
    steps: Vec<FauxStep>,
    #[serde(default)]
    expected_context: Option<Vec<Value>>,
    #[serde(default)]
    usage: Option<FauxUsage>,
    #[serde(default)]
    continuations: Vec<FauxContinuation>,
    #[serde(default)]
    extensions: BTreeMap<String, Value>,
}

impl<'de> Deserialize<'de> for FauxScenario {
    /// 功能：反序列化 faux scenario 并执行 JSON Schema 2020-12 等价约束校验。
    ///
    /// 输入：任意 Serde 数据源中的场景对象。
    /// 输出：字段与范围均符合 faux scenario v0.1 Schema 的场景。
    /// 不变量：未知字段、非法标识、越界数字、超长文本及非法扩展命名空间一律拒绝。
    /// 失败：输入不符合规范时返回数据源原生反序列化错误，且不产生部分场景。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        let wire = FauxScenarioWire::deserialize(deserializer)?;
        let scenario = Self {
            schema_version: wire.schema_version,
            name: wire.name,
            seed: wire.seed,
            steps: wire.steps,
            expected_context: wire.expected_context,
            usage: wire.usage,
            continuations: wire.continuations,
            extensions: wire.extensions,
        };
        validate_scenario(&scenario).map_err(de::Error::custom)?;
        Ok(scenario)
    }
}

/// faux 场景中按 FIFO 消费的后续 Provider turn 脚本。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct FauxContinuation {
    pub steps: Vec<FauxStep>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub expected_context: Option<Vec<Value>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub usage: Option<FauxUsage>,
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub extensions: BTreeMap<String, Value>,
}

/// faux 场景支持的确定性输出、故障和时序步骤。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case", deny_unknown_fields)]
pub enum FauxStep {
    Text {
        text: String,
    },
    ToolCall {
        #[serde(rename = "toolCallId")]
        tool_call_id: String,
        name: String,
        arguments: Map<String, Value>,
    },
    Error {
        error: Box<FauxError>,
    },
    Delay {
        #[serde(rename = "durationMs")]
        duration_ms: u64,
    },
    Disconnect {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        reason: Option<String>,
    },
}

/// faux 场景中的完整规范化用量；当前领域事件会投影其已支持字段。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct FauxUsage {
    pub input_tokens: u64,
    pub output_tokens: u64,
    pub total_tokens: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cached_input_tokens: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cache_write_tokens: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reasoning_tokens: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cost: Option<FauxMoney>,
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub extensions: BTreeMap<String, Value>,
}

impl FauxUsage {
    /// 功能：把场景用量投影为 Rust 当前统一 Provider 用量事件。
    ///
    /// 输入：符合 faux scenario Schema 的完整用量对象。
    /// 输出：包含输入、输出、总计及缓存输入 token 的统一领域用量。
    /// 不变量：不会猜测当前领域模型尚不能无损表示的币种、缓存写入或推理字段。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    fn into_domain(self) -> Usage {
        Usage {
            input_tokens: self.input_tokens,
            output_tokens: self.output_tokens,
            total_tokens: self.total_tokens,
            cached_input_tokens: self.cached_input_tokens.unwrap_or_default(),
            cost_micros: None,
        }
    }
}

/// faux 用量中保持精确十进制表示的货币值。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct FauxMoney {
    pub currency: String,
    pub amount: String,
}

/// faux error step 携带的语言中立结构化错误。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct FauxError {
    pub code: i64,
    pub message: String,
    pub retryable: bool,
    pub details: FauxErrorDetails,
}

impl FauxError {
    /// 功能：把 faux 线上错误转换为运行时统一结构化错误。
    ///
    /// 输入：数值 RPC 错误码、稳定详情分类、人类可读消息及重试标志。
    /// 输出：可由 Provider 流直接返回的 `AgentError`。
    /// 不变量：消息、重试标志和全部语言中立详情保持不变；数值码按详情分类消除复用码歧义。
    /// 失败：本方法不返回错误；未知分类保守映射为 Provider 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    fn into_agent_error(self) -> AgentError {
        let code = error_code_from_wire(self.code, &self.details.kind);
        let details = serde_json::to_value(self.details)
            .unwrap_or_else(|_| serde_json::json!({"kind":"provider_error"}));
        AgentError {
            code,
            message: self.message,
            retryable: self.retryable,
            details,
        }
    }
}

/// faux error details；字段集合与公共 error Schema 保持一致。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct FauxErrorDetails {
    pub kind: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub http_status: Option<u16>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub retry_after_ms: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub field: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub operation: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub provider_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub model_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub tool_name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub supported_versions: Option<Vec<String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub limit: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub observed: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub exit_code: Option<i64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub signal: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub resource_id: Option<String>,
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub extensions: BTreeMap<String, Value>,
}

/// 功能：校验完整 faux scenario 是否满足公共 JSON Schema 的值域和格式约束。
///
/// 输入：已经通过严格字段反序列化的场景。
/// 输出：全部约束满足时返回成功。
/// 不变量：校验不修改场景，也不执行其中任何动作。
/// 失败：首个违反 Schema 的字段以稳定字段路径说明原因。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_scenario(scenario: &FauxScenario) -> Result<(), String> {
    if scenario.schema_version != "0.1" {
        return Err("schemaVersion must equal '0.1'".to_owned());
    }
    if !pattern_matches(
        &SCENARIO_NAME_PATTERN,
        r"^[A-Za-z0-9][A-Za-z0-9._-]*$",
        &scenario.name,
    ) || !(1..=128).contains(&scenario.name.chars().count())
    {
        return Err("name does not match the faux scenario identifier format".to_owned());
    }
    if scenario.steps.len() > 10_000 {
        return Err("steps exceeds the 10000 item limit".to_owned());
    }
    if scenario
        .expected_context
        .as_ref()
        .is_some_and(|messages| messages.len() > 10_000)
    {
        return Err("expectedContext exceeds the 10000 item limit".to_owned());
    }
    if scenario.continuations.len() > 100 {
        return Err("continuations exceeds the 100 item limit".to_owned());
    }
    validate_extensions("extensions", &scenario.extensions)?;
    if let Some(usage) = scenario.usage.as_ref() {
        validate_usage(usage)?;
    }
    for (index, step) in scenario.steps.iter().enumerate() {
        validate_step(index, step)?;
    }
    if let Some(messages) = scenario.expected_context.as_ref() {
        for (index, message) in messages.iter().enumerate() {
            validate_context_message(index, message)?;
        }
    }
    for (index, continuation) in scenario.continuations.iter().enumerate() {
        validate_continuation(index, continuation)?;
    }
    Ok(())
}

/// 功能：校验一个 faux continuation 的步骤、上下文、用量和扩展边界。
///
/// 输入：continuations 数组索引及只读 continuation。
/// 输出：全部字段符合 faux scenario continuation Schema 时返回成功。
/// 不变量：只验证结构和值域，不消费脚本或执行 Provider 动作。
/// 失败：步骤、上下文、用量、扩展或数量超限时返回稳定字段原因。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_continuation(index: usize, continuation: &FauxContinuation) -> Result<(), String> {
    if continuation.steps.len() > 10_000 {
        return Err(format!(
            "continuations[{index}].steps exceeds the 10000 item limit"
        ));
    }
    if continuation
        .expected_context
        .as_ref()
        .is_some_and(|messages| messages.len() > 10_000)
    {
        return Err(format!(
            "continuations[{index}].expectedContext exceeds the 10000 item limit"
        ));
    }
    for (step_index, step) in continuation.steps.iter().enumerate() {
        validate_step(step_index, step)?;
    }
    if let Some(messages) = continuation.expected_context.as_ref() {
        for (message_index, message) in messages.iter().enumerate() {
            validate_context_message(message_index, message)?;
        }
    }
    if let Some(usage) = continuation.usage.as_ref() {
        validate_usage(usage)?;
    }
    validate_extensions(
        &format!("continuations[{index}].extensions"),
        &continuation.extensions,
    )
}

/// 功能：校验 expectedContext 中一条去标识化 Provider 上下文消息。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_context_message(index: usize, value: &Value) -> Result<(), String> {
    let prefix = format!("expectedContext[{index}]");
    let object = value
        .as_object()
        .ok_or_else(|| format!("{prefix} must be an object"))?;
    ensure_json_keys(
        &prefix,
        object,
        &["role", "content", "toolCallId", "toolName", "isError"],
    )?;
    let role = object
        .get("role")
        .and_then(Value::as_str)
        .ok_or_else(|| format!("{prefix}.role must be a string"))?;
    if !matches!(role, "user" | "assistant" | "tool") {
        return Err(format!("{prefix}.role is invalid"));
    }
    let content = object
        .get("content")
        .and_then(Value::as_array)
        .ok_or_else(|| format!("{prefix}.content must be an array"))?;
    if content.len() > 1_024 {
        return Err(format!("{prefix}.content exceeds 1024 items"));
    }
    for (content_index, block) in content.iter().enumerate() {
        validate_context_content(&format!("{prefix}.content[{content_index}]"), block)?;
    }
    if role == "tool" {
        validate_opaque_value(&format!("{prefix}.toolCallId"), object.get("toolCallId"))?;
        let name = object
            .get("toolName")
            .and_then(Value::as_str)
            .ok_or_else(|| format!("{prefix}.toolName must be a string"))?;
        if !pattern_matches(&TOOL_NAME_PATTERN, r"^[a-z][a-z0-9_.-]*$", name)
            || name.chars().count() > 128
        {
            return Err(format!("{prefix}.toolName is invalid"));
        }
        if !object.get("isError").is_some_and(Value::is_boolean) {
            return Err(format!("{prefix}.isError must be boolean"));
        }
    } else if object.contains_key("toolCallId")
        || object.contains_key("toolName")
        || object.contains_key("isError")
    {
        return Err(format!("{prefix} non-tool message has tool-only fields"));
    }
    Ok(())
}

/// 功能：校验 expectedContext 的一个 portable content tagged union 分支。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_context_content(field: &str, value: &Value) -> Result<(), String> {
    let object = value
        .as_object()
        .ok_or_else(|| format!("{field} must be an object"))?;
    let content_type = object
        .get("type")
        .and_then(Value::as_str)
        .ok_or_else(|| format!("{field}.type must be a string"))?;
    match content_type {
        "text" => {
            ensure_json_keys(field, object, &["type", "text", "extensions"])?;
            if !object.get("text").is_some_and(Value::is_string) {
                return Err(format!("{field}.text must be a string"));
            }
        }
        "reasoning" => {
            ensure_json_keys(
                field,
                object,
                &["type", "text", "redacted", "signature", "extensions"],
            )?;
            if !object.get("text").is_some_and(Value::is_string)
                || object
                    .get("redacted")
                    .is_some_and(|item| !item.is_boolean())
                || object.get("signature").is_some_and(|item| {
                    item.as_str()
                        .is_none_or(|text| text.chars().count() > 1_048_576)
                })
            {
                return Err(format!("{field} reasoning fields are invalid"));
            }
        }
        "image_ref" => {
            ensure_json_keys(field, object, &["type", "artifact", "alt", "extensions"])?;
            validate_context_artifact(field, object.get("artifact"))?;
            if object.get("alt").is_some_and(|item| {
                item.as_str()
                    .is_none_or(|text| text.chars().count() > 4_096)
            }) {
                return Err(format!("{field}.alt is invalid"));
            }
        }
        "artifact_ref" => {
            ensure_json_keys(field, object, &["type", "artifact", "extensions"])?;
            validate_context_artifact(field, object.get("artifact"))?;
        }
        "tool_call" => {
            ensure_json_keys(
                field,
                object,
                &["type", "toolCallId", "name", "arguments", "extensions"],
            )?;
            validate_opaque_value(&format!("{field}.toolCallId"), object.get("toolCallId"))?;
            let name = object
                .get("name")
                .and_then(Value::as_str)
                .ok_or_else(|| format!("{field}.name must be a string"))?;
            if !pattern_matches(&TOOL_NAME_PATTERN, r"^[a-z][a-z0-9_.-]*$", name)
                || name.chars().count() > 128
                || !object.get("arguments").is_some_and(Value::is_object)
            {
                return Err(format!("{field} tool call fields are invalid"));
            }
        }
        _ => return Err(format!("{field}.type is unsupported")),
    }
    if let Some(extensions) = object.get("extensions") {
        validate_extension_value(&format!("{field}.extensions"), extensions)?;
    }
    Ok(())
}

/// 功能：校验 expectedContext 内嵌 artifact reference 的必需字段和值域。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_context_artifact(field: &str, value: Option<&Value>) -> Result<(), String> {
    let field = format!("{field}.artifact");
    let object = value
        .and_then(Value::as_object)
        .ok_or_else(|| format!("{field} must be an object"))?;
    ensure_json_keys(
        &field,
        object,
        &[
            "artifactId",
            "mediaType",
            "byteLength",
            "sha256",
            "displayName",
            "extensions",
        ],
    )?;
    validate_opaque_value(&format!("{field}.artifactId"), object.get("artifactId"))?;
    let media_type = object
        .get("mediaType")
        .and_then(Value::as_str)
        .ok_or_else(|| format!("{field}.mediaType must be a string"))?;
    if !(3..=128).contains(&media_type.chars().count()) || !media_type.contains('/') {
        return Err(format!("{field}.mediaType is invalid"));
    }
    let byte_length = object
        .get("byteLength")
        .and_then(Value::as_u64)
        .ok_or_else(|| format!("{field}.byteLength must be a non-negative integer"))?;
    if byte_length > MAX_SAFE_INTEGER {
        return Err(format!(
            "{field}.byteLength exceeds the safe integer maximum"
        ));
    }
    let sha256 = object
        .get("sha256")
        .and_then(Value::as_str)
        .ok_or_else(|| format!("{field}.sha256 must be a string"))?;
    if sha256.len() != 64
        || !sha256
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return Err(format!("{field}.sha256 is invalid"));
    }
    if object.get("displayName").is_some_and(|item| {
        item.as_str()
            .is_none_or(|text| !(1..=256).contains(&text.chars().count()))
    }) {
        return Err(format!("{field}.displayName is invalid"));
    }
    if let Some(extensions) = object.get("extensions") {
        validate_extension_value(&format!("{field}.extensions"), extensions)?;
    }
    Ok(())
}

/// 功能：校验 JSON 对象不存在 Schema 未声明字段。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn ensure_json_keys(
    field: &str,
    object: &Map<String, Value>,
    allowed: &[&str],
) -> Result<(), String> {
    if let Some(key) = object.keys().find(|key| !allowed.contains(&key.as_str())) {
        Err(format!("{field} contains unknown field '{key}'"))
    } else {
        Ok(())
    }
}

/// 功能：校验 JSON 值是公共 opaqueId。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_opaque_value(field: &str, value: Option<&Value>) -> Result<(), String> {
    let value = value
        .and_then(Value::as_str)
        .ok_or_else(|| format!("{field} must be a string"))?;
    if pattern_matches(&OPAQUE_ID_PATTERN, r"^[A-Za-z0-9][A-Za-z0-9._:-]*$", value)
        && (1..=128).contains(&value.chars().count())
    {
        Ok(())
    } else {
        Err(format!("{field} is not a valid opaque ID"))
    }
}

/// 功能：校验动态 JSON extensions 对象的反向域名命名空间。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_extension_value(field: &str, value: &Value) -> Result<(), String> {
    let extensions = value
        .as_object()
        .ok_or_else(|| format!("{field} must be an object"))?;
    if let Some(key) = extensions.keys().find(|key| {
        !pattern_matches(
            &EXTENSION_NAME_PATTERN,
            r"^[a-z0-9]+(?:[.-][a-z0-9-]+)+$",
            key,
        )
    }) {
        Err(format!("{field} contains invalid namespace '{key}'"))
    } else {
        Ok(())
    }
}

/// 功能：校验单个 faux step 的标识、数值和文本边界。
///
/// 输入：步骤索引及只读步骤。
/// 输出：步骤符合对应 tagged union 分支约束时返回成功。
/// 不变量：工具参数必须已经是 JSON object；错误详情交由公共错误校验处理。
/// 失败：字段格式或范围不符时返回包含步骤索引的原因。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_step(index: usize, step: &FauxStep) -> Result<(), String> {
    match step {
        FauxStep::Text { .. } => Ok(()),
        FauxStep::ToolCall {
            tool_call_id, name, ..
        } => {
            if !pattern_matches(
                &OPAQUE_ID_PATTERN,
                r"^[A-Za-z0-9][A-Za-z0-9._:-]*$",
                tool_call_id,
            ) || !(1..=128).contains(&tool_call_id.chars().count())
            {
                return Err(format!(
                    "steps[{index}].toolCallId is not a valid opaque ID"
                ));
            }
            if !pattern_matches(&TOOL_NAME_PATTERN, r"^[a-z][a-z0-9_.-]*$", name)
                || name.chars().count() > 128
            {
                return Err(format!("steps[{index}].name is not a valid tool name"));
            }
            Ok(())
        }
        FauxStep::Error { error } => validate_faux_error(index, error),
        FauxStep::Delay { duration_ms } => {
            if *duration_ms > 60_000 {
                Err(format!("steps[{index}].durationMs exceeds 60000"))
            } else {
                Ok(())
            }
        }
        FauxStep::Disconnect { reason } => {
            validate_optional_text(&format!("steps[{index}].reason"), reason, 4_096)
        }
    }
}

/// 功能：校验 faux error step 的数值 RPC 码和公共错误详情对象。
///
/// 输入：步骤索引及只读 faux 错误。
/// 输出：错误对象满足 domain/error Schema 时返回成功。
/// 不变量：错误消息必须非空；扩展数据只能位于显式 extensions 命名空间。
/// 失败：错误码、文本、详情范围或命名空间不合法时返回字段路径原因。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_faux_error(index: usize, error: &FauxError) -> Result<(), String> {
    let prefix = format!("steps[{index}].error");
    if !(-32_768..=-1).contains(&error.code) {
        return Err(format!("{prefix}.code must be between -32768 and -1"));
    }
    let message_length = error.message.chars().count();
    if !(1..=4_096).contains(&message_length) {
        return Err(format!(
            "{prefix}.message must contain 1 to 4096 characters"
        ));
    }
    let details = &error.details;
    if !pattern_matches(&ERROR_KIND_PATTERN, r"^[a-z][a-z0-9_]*$", &details.kind)
        || !(1..=96).contains(&details.kind.chars().count())
    {
        return Err(format!("{prefix}.details.kind is invalid"));
    }
    if details
        .http_status
        .is_some_and(|status| !(100..=599).contains(&status))
    {
        return Err(format!("{prefix}.details.httpStatus is outside 100..599"));
    }
    validate_safe_integer(
        &format!("{prefix}.details.retryAfterMs"),
        details.retry_after_ms,
    )?;
    validate_optional_text(&format!("{prefix}.details.field"), &details.field, 256)?;
    validate_optional_text(
        &format!("{prefix}.details.operation"),
        &details.operation,
        128,
    )?;
    validate_optional_text(
        &format!("{prefix}.details.providerId"),
        &details.provider_id,
        128,
    )?;
    validate_optional_text(&format!("{prefix}.details.modelId"), &details.model_id, 256)?;
    validate_optional_text(
        &format!("{prefix}.details.toolName"),
        &details.tool_name,
        128,
    )?;
    validate_optional_text(&format!("{prefix}.details.path"), &details.path, 4_096)?;
    if let Some(versions) = details.supported_versions.as_ref() {
        if versions.len() > 32 {
            return Err(format!(
                "{prefix}.details.supportedVersions exceeds 32 items"
            ));
        }
        let mut unique = BTreeSet::new();
        for version in versions {
            if version.chars().count() > 16
                || !pattern_matches(&PROTOCOL_VERSION_PATTERN, r"^[0-9]+\.[0-9]+$", version)
            {
                return Err(format!(
                    "{prefix}.details.supportedVersions contains an invalid version"
                ));
            }
            if !unique.insert(version) {
                return Err(format!(
                    "{prefix}.details.supportedVersions contains duplicates"
                ));
            }
        }
    }
    validate_safe_integer(&format!("{prefix}.details.limit"), details.limit)?;
    validate_safe_integer(&format!("{prefix}.details.observed"), details.observed)?;
    validate_optional_text(&format!("{prefix}.details.signal"), &details.signal, 128)?;
    if let Some(resource_id) = details.resource_id.as_ref()
        && (!pattern_matches(
            &OPAQUE_ID_PATTERN,
            r"^[A-Za-z0-9][A-Za-z0-9._:-]*$",
            resource_id,
        ) || !(1..=128).contains(&resource_id.chars().count()))
    {
        return Err(format!("{prefix}.details.resourceId is invalid"));
    }
    validate_extensions(&format!("{prefix}.details.extensions"), &details.extensions)
}

/// 功能：校验 faux usage 的安全整数、货币格式及扩展命名空间。
///
/// 输入：只读场景用量。
/// 输出：全部字段符合 domain/usage Schema 时返回成功。
/// 不变量：不推断 token 合计关系；Schema 仅分别约束每个计数的值域。
/// 失败：超出 JavaScript 安全整数、货币或扩展格式非法时返回原因。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_usage(usage: &FauxUsage) -> Result<(), String> {
    validate_safe_integer("usage.inputTokens", Some(usage.input_tokens))?;
    validate_safe_integer("usage.outputTokens", Some(usage.output_tokens))?;
    validate_safe_integer("usage.totalTokens", Some(usage.total_tokens))?;
    validate_safe_integer("usage.cachedInputTokens", usage.cached_input_tokens)?;
    validate_safe_integer("usage.cacheWriteTokens", usage.cache_write_tokens)?;
    validate_safe_integer("usage.reasoningTokens", usage.reasoning_tokens)?;
    if let Some(cost) = usage.cost.as_ref() {
        if !pattern_matches(&CURRENCY_PATTERN, r"^[A-Z]{3}$", &cost.currency) {
            return Err("usage.cost.currency must be a three-letter uppercase code".to_owned());
        }
        if !pattern_matches(
            &MONEY_AMOUNT_PATTERN,
            r"^(0|[1-9][0-9]*)(\.[0-9]{1,12})?$",
            &cost.amount,
        ) {
            return Err("usage.cost.amount is not a canonical decimal amount".to_owned());
        }
    }
    validate_extensions("usage.extensions", &usage.extensions)
}

/// 功能：校验可选无符号计数不超过公共协议安全整数上限。
///
/// 输入：字段路径及可选计数。
/// 输出：字段缺省或处于合法范围时返回成功。
/// 不变量：零是合法值；不改变原始计数。
/// 失败：计数大于 2^53-1 时返回字段路径原因。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_safe_integer(field: &str, value: Option<u64>) -> Result<(), String> {
    if value.is_some_and(|number| number > MAX_SAFE_INTEGER) {
        Err(format!("{field} exceeds the safe integer maximum"))
    } else {
        Ok(())
    }
}

/// 功能：校验可选字符串的 Unicode 字符数量上限。
///
/// 输入：字段路径、可选字符串及最大字符数。
/// 输出：字段缺省或长度不超限时返回成功。
/// 不变量：按 Unicode 标量计数，不按 UTF-8 字节数误拒绝中文或 emoji。
/// 失败：文本超过上限时返回字段路径原因。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_optional_text(
    field: &str,
    value: &Option<String>,
    maximum: usize,
) -> Result<(), String> {
    if value
        .as_ref()
        .is_some_and(|text| text.chars().count() > maximum)
    {
        Err(format!("{field} exceeds {maximum} characters"))
    } else {
        Ok(())
    }
}

/// 功能：校验显式 extensions 对象的所有顶层键均为反向域名式命名空间。
///
/// 输入：字段路径及扩展键值映射。
/// 输出：所有命名空间键符合 common Schema 时返回成功。
/// 不变量：扩展值保持任意 JSON；扩展不得逸出显式对象。
/// 失败：任一键不符合命名规则时返回包含该键的原因。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_extensions(field: &str, extensions: &BTreeMap<String, Value>) -> Result<(), String> {
    for key in extensions.keys() {
        if !pattern_matches(
            &EXTENSION_NAME_PATTERN,
            r"^[a-z0-9]+(?:[.-][a-z0-9-]+)+$",
            key,
        ) {
            return Err(format!("{field} contains invalid namespace '{key}'"));
        }
    }
    Ok(())
}

/// 功能：使用进程级缓存正则校验公共 Schema 中的固定字符串格式。
///
/// 输入：专用缓存、编译期固定正则文本及待校验值。
/// 输出：字符串是否完整匹配对应格式。
/// 不变量：同一缓存只配合同一固定模式使用；正则最多编译一次。
/// 失败：内置正则若因编程错误无法编译则立即终止，外部输入不会触发该分支。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn pattern_matches(cache: &OnceLock<Regex>, pattern: &str, value: &str) -> bool {
    let expression = cache.get_or_init(|| match Regex::new(pattern) {
        Ok(expression) => expression,
        Err(error) => panic!("invalid built-in faux schema regex: {error}"),
    });
    expression.is_match(value)
}

impl FauxScenario {
    /// 功能：把规范化确定性场景转换为单次 faux Provider 异步动作脚本。
    ///
    /// 输入：文本、工具调用、错误、延迟、断流步骤及可选固定用量。
    /// 输出：包含开始事件、按序动作、可选用量和结束事件的脚本。
    /// 不变量：转换完全离线且确定性；工具参数按 UTF-8 字符边界稳定分片。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn into_script(self) -> FauxScript {
        script_from_parts(self.seed, self.steps, self.expected_context, self.usage)
    }

    /// 功能：把首轮和 continuations 展开为按 Provider turn 消费的 FIFO faux 脚本。
    ///
    /// 输入：完整且已验证的 faux scenario。
    /// 输出：首轮位于索引 0、后续 continuation 保持声明顺序的非空脚本数组。
    /// 不变量：每个 continuation 恰好对应一次后续 Provider 调用；上下文断言不跨脚本移动。
    /// 失败：本方法不返回错误；场景值域已在反序列化阶段验证。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn into_scripts(self) -> Vec<FauxScript> {
        let mut scripts = vec![script_from_parts(
            self.seed,
            self.steps,
            self.expected_context,
            self.usage,
        )];
        scripts.extend(
            self.continuations
                .into_iter()
                .enumerate()
                .map(|(index, continuation)| {
                    script_from_parts(
                        self.seed.wrapping_add(index as u32).wrapping_add(1),
                        continuation.steps,
                        continuation.expected_context,
                        continuation.usage,
                    )
                }),
        );
        scripts
    }
}

/// 功能：把单个 faux turn 的步骤、上下文断言和用量组装为异步动作脚本。
///
/// 输入：确定性消息 seed、步骤、可选 expectedContext 与可选 usage。
/// 输出：带 message start、规范化增量、用量和唯一 message end 的脚本。
/// 不变量：工具参数按 UTF-8 字符边界分片；finish reason 由是否存在工具调用唯一决定。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn script_from_parts(
    seed: u32,
    steps: Vec<FauxStep>,
    expected_context: Option<Vec<Value>>,
    usage: Option<FauxUsage>,
) -> FauxScript {
    let mut actions = vec![FauxAction::Event(ProviderEvent::MessageStart {
        provider_message_id: format!("faux-message-{seed}"),
    })];
    let mut contains_tool_call = false;
    for step in steps {
        match step {
            FauxStep::Text { text } => {
                actions.push(FauxAction::Event(ProviderEvent::TextDelta { text }));
            }
            FauxStep::ToolCall {
                tool_call_id,
                name,
                arguments,
            } => {
                contains_tool_call = true;
                actions.push(FauxAction::Event(ProviderEvent::ToolCallStart {
                    call_id: tool_call_id.clone(),
                    name,
                }));
                actions.extend(argument_deltas(arguments).into_iter().map(|delta| {
                    FauxAction::Event(ProviderEvent::ToolCallArgumentsDelta {
                        call_id: tool_call_id.clone(),
                        delta,
                    })
                }));
                actions.push(FauxAction::Event(ProviderEvent::ToolCallEnd {
                    call_id: tool_call_id,
                }));
            }
            FauxStep::Error { error } => actions.push(FauxAction::Error(error)),
            FauxStep::Delay { duration_ms } => {
                actions.push(FauxAction::Delay(Duration::from_millis(duration_ms)));
            }
            FauxStep::Disconnect { reason } => actions.push(FauxAction::Disconnect(reason)),
        }
    }
    if let Some(usage) = usage {
        actions.push(FauxAction::Event(ProviderEvent::Usage(usage.into_domain())));
    }
    actions.push(FauxAction::Event(ProviderEvent::MessageEnd {
        finish_reason: if contains_tool_call {
            FinishReason::ToolCalls
        } else {
            FinishReason::Stop
        },
    }));
    FauxScript {
        actions,
        expected_context,
    }
}

/// 功能：将工具参数对象序列化并按固定 UTF-8 字符数切分为确定性增量。
///
/// 输入：符合工具参数 Schema 的 JSON 对象。
/// 输出：顺序拼接后等于完整 JSON 对象文本的一个或多个增量。
/// 不变量：不会切断多字节 Unicode 标量；相同对象产生相同分片。
/// 失败：`serde_json::Value` 理论上始终可序列化；异常时安全退化为空对象文本。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn argument_deltas(arguments: Map<String, Value>) -> Vec<String> {
    let serialized =
        serde_json::to_string(&Value::Object(arguments)).unwrap_or_else(|_| "{}".into());
    let characters: Vec<char> = serialized.chars().collect();
    characters
        .chunks(TOOL_ARGUMENT_CHUNK_CHARACTERS)
        .map(|chunk| chunk.iter().collect())
        .collect()
}

/// 功能：依据详情分类和复用的数值 RPC 码选择运行时稳定错误枚举。
///
/// 输入：faux error step 的数值错误码与 `details.kind`。
/// 输出：Rust 运行时统一的 `ErrorCode`。
/// 不变量：优先使用语义明确的详情分类；未知 Provider 故障不会伪装成成功或客户端错误。
/// 失败：本方法不返回错误，未知值保守映射为 `ProviderError`。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn error_code_from_wire(code: i64, kind: &str) -> ErrorCode {
    match kind {
        "invalid_request" => ErrorCode::InvalidRequest,
        "not_initialized" => ErrorCode::NotInitialized,
        "already_initialized" => ErrorCode::AlreadyInitialized,
        "method_not_found" => ErrorCode::MethodNotFound,
        "invalid_params" => ErrorCode::InvalidParams,
        "protocol_version_unsupported" => ErrorCode::ProtocolVersionUnsupported,
        "session_not_found" => ErrorCode::SessionNotFound,
        "run_not_found" => ErrorCode::RunNotFound,
        "run_conflict" => ErrorCode::RunConflict,
        "cancelled" => ErrorCode::Cancelled,
        "provider_rate_limit" | "provider_rate_limited" => ErrorCode::ProviderRateLimited,
        "provider_unavailable" => ErrorCode::ProviderUnavailable,
        "stream_interrupted" => ErrorCode::StreamInterrupted,
        "tool_not_found" => ErrorCode::ToolNotFound,
        "tool_arguments_invalid" => ErrorCode::ToolArgumentsInvalid,
        "approval_required" => ErrorCode::ApprovalRequired,
        "permission_denied" => ErrorCode::PermissionDenied,
        "path_outside_workspace" => ErrorCode::PathOutsideWorkspace,
        "output_limit_exceeded" => ErrorCode::OutputLimitExceeded,
        "timeout" => ErrorCode::Timeout,
        "journal_corrupt" => ErrorCode::JournalCorrupt,
        "io_error" => ErrorCode::IoError,
        "internal_error" => ErrorCode::InternalError,
        "provider_error" => ErrorCode::ProviderError,
        _ => match code {
            -32601 => ErrorCode::MethodNotFound,
            -32602 => ErrorCode::InvalidParams,
            -32603 => ErrorCode::InternalError,
            -32001 => ErrorCode::ProtocolVersionUnsupported,
            -32003 => ErrorCode::PermissionDenied,
            -32004 => ErrorCode::RunConflict,
            -32006 => ErrorCode::Timeout,
            -32007 => ErrorCode::Cancelled,
            -32008 => ErrorCode::JournalCorrupt,
            -32009 => ErrorCode::OutputLimitExceeded,
            -32010 => ErrorCode::RunNotFound,
            _ => ErrorCode::ProviderError,
        },
    }
}

/// 功能：创建 Provider 流取消时使用的统一结构化错误。
///
/// 输入：无。
/// 输出：错误码为 `Cancelled` 且不可重试的错误。
/// 不变量：不包含凭据、路径或其他敏感数据。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn cancelled_error() -> AgentError {
    AgentError::new(ErrorCode::Cancelled, "provider invocation cancelled")
}

/// 功能：创建 faux 主动断流时使用的统一结构化错误。
///
/// 输入：场景中可选、长度受 Schema 限制的断流原因。
/// 输出：错误码为 `StreamInterrupted` 的不可重试结构化错误。
/// 不变量：断流永不产生正常结束事件；详情使用公共稳定分类。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn disconnect_error(reason: Option<String>) -> AgentError {
    let message = reason.unwrap_or_else(|| "synthetic provider disconnect".to_owned());
    AgentError::new(ErrorCode::StreamInterrupted, message).with_details(serde_json::json!({
        "kind": "stream_interrupted",
        "operation": "provider.stream"
    }))
}

/// 功能：创建同一 run 需要额外 faux turn 但 continuation 已耗尽的稳定错误。
///
/// 输入：无。
/// 输出：不可重试且带 `faux_configuration_exhausted` 分类的结构化错误。
/// 不变量：不会退回默认文本脚本掩盖缺失的受治理 continuation。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn configuration_exhausted_error() -> AgentError {
    AgentError::new(
        ErrorCode::InvalidParams,
        "faux scenario has no continuation for the required provider turn",
    )
    .with_details(serde_json::json!({
        "kind":"faux_configuration_exhausted",
        "providerId":"faux"
    }))
}

#[derive(Debug, Default)]
struct ConfiguredScripts {
    scripts: VecDeque<FauxScript>,
    bound_run_id: Option<String>,
}

enum ConfiguredSelection {
    Script(FauxScript),
    Exhausted,
    Unconfigured,
}

#[derive(Debug, Clone, Default)]
pub struct FauxProvider {
    scripts_by_session: Arc<Mutex<BTreeMap<String, ConfiguredScripts>>>,
}

impl FauxProvider {
    /// 功能：创建尚未配置脚本的离线确定性 Provider。
    ///
    /// 输入：无。
    /// 输出：共享线程安全脚本队列为空的实例。
    /// 不变量：实例不会访问网络或读取真实凭据。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// 功能：原子替换指定会话后续调用按顺序消费的 faux 动作脚本队列。
    ///
    /// 输入：不透明会话标识及按调用顺序排列的完整动作脚本。
    /// 输出：该会话队列替换完成后返回。
    /// 不变量：不同会话队列相互隔离；每次 Provider 调用最多消费一个脚本；配置不访问网络。
    /// 失败：本方法不返回业务错误；等待异步互斥锁期间可被任务取消。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn configure(&self, session_id: &str, scripts: Vec<FauxScript>) {
        self.scripts_by_session.lock().await.insert(
            session_id.to_owned(),
            ConfiguredScripts {
                scripts: scripts.into(),
                bound_run_id: None,
            },
        );
    }

    /// 功能：在脚本队列为空时根据最近用户文本生成默认离线事件脚本。
    ///
    /// 输入：只读 Provider 请求。
    /// 输出：含开始、文本、用量及正常结束事件的确定性内存脚本。
    /// 不变量：不读取非用户提示，不访问网络或真实凭据。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn default_script(request: &ProviderRequest) -> FauxScript {
        let prompt = last_user_text(request);
        let text = if prompt.is_empty() {
            "Faux provider received an empty prompt.".to_owned()
        } else {
            format!("faux: {prompt}")
        };
        FauxScript::from_events(vec![
            ProviderEvent::MessageStart {
                provider_message_id: format!("faux-{}", Uuid::new_v4()),
            },
            ProviderEvent::TextDelta { text },
            ProviderEvent::Usage(Usage {
                input_tokens: prompt.split_whitespace().count() as u64,
                output_tokens: 1,
                total_tokens: prompt.split_whitespace().count() as u64 + 1,
                ..Usage::default()
            }),
            ProviderEvent::MessageEnd {
                finish_reason: FinishReason::Stop,
            },
        ])
    }
}

/// 功能：把完整 Provider 消息历史归一为 faux expectedContext 的去标识化 JSON 形状。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
///
/// 消息顺序、内容数组和 tool 关联保持不变；无法安全表示的内部组合返回结构化错误。
fn normalize_provider_context(messages: &[Message]) -> Result<Vec<Value>, AgentError> {
    messages
        .iter()
        .map(|message| {
            let role = match message.role {
                Role::User => "user",
                Role::Assistant => "assistant",
                Role::Tool => "tool",
                Role::System => return Err(faux_context_shape_error()),
            };
            if message.role == Role::Tool {
                let [
                    ContentBlock::ToolResult {
                        call_id,
                        name,
                        output,
                        is_error,
                    },
                ] = message.content.as_slice()
                else {
                    return Err(faux_context_shape_error());
                };
                let mut content = Vec::new();
                if let Some(text) = output.text.as_ref() {
                    content.push(json!({"type":"text","text":text}));
                }
                if let Some(artifact) = output.artifact.as_ref() {
                    content.push(json!({"type":"artifact_ref","artifact":artifact}));
                }
                return Ok(json!({
                    "role":role,
                    "content":content,
                    "toolCallId":call_id,
                    "toolName":name,
                    "isError":is_error
                }));
            }
            let content = message
                .content
                .iter()
                .map(normalize_context_content)
                .collect::<Result<Vec<_>, _>>()?;
            Ok(json!({"role":role,"content":content}))
        })
        .collect()
}

/// 功能：将一个内部 user/assistant 内容块规范化为 expectedContext portable content。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn normalize_context_content(content: &ContentBlock) -> Result<Value, AgentError> {
    match content {
        ContentBlock::Text { text } => Ok(json!({"type":"text","text":text})),
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
            Ok(value)
        }
        ContentBlock::ImageRef { artifact, alt } => {
            let mut value = json!({"type":"image_ref","artifact":artifact});
            if let Some(alt) = alt {
                value["alt"] = Value::String(alt.clone());
            }
            Ok(value)
        }
        ContentBlock::ArtifactRef { artifact } => {
            Ok(json!({"type":"artifact_ref","artifact":artifact}))
        }
        ContentBlock::ToolCall {
            call_id,
            name,
            arguments,
        } => Ok(json!({
            "type":"tool_call","toolCallId":call_id,"name":name,"arguments":arguments
        })),
        ContentBlock::ToolResult { .. } => Err(faux_context_shape_error()),
    }
}

/// 功能：创建不泄露上下文正文的 faux 上下文形状错误。
///
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn faux_context_shape_error() -> AgentError {
    AgentError::new(
        ErrorCode::InvalidParams,
        "faux provider context could not be normalized safely",
    )
    .with_details(serde_json::json!({
        "kind":"faux_context_mismatch",
        "providerId":"faux"
    }))
}

#[async_trait]
impl Provider for FauxProvider {
    /// 功能：返回内置离线 Provider 的稳定标识 `faux`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn id(&self) -> &str {
        "faux"
    }

    /// 功能：消费一个已配置动作脚本或生成默认脚本，并返回可取消的内存事件流。
    ///
    /// 输入：Provider 请求和运行级取消令牌。
    /// 输出：按脚本顺序执行事件、延迟与故障且不访问网络的规范化流。
    /// 不变量：脚本在请求所属会话内按配置顺序仅消费一次；延迟可取消；故障或断流后不产生正常事件。
    /// 失败：取消返回 `Cancelled`；error step 返回指定结构化错误；disconnect 返回 `StreamInterrupted`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn stream(
        &self,
        request: ProviderRequest,
        cancellation: CancellationToken,
    ) -> Result<ProviderStream, AgentError> {
        let configured = if let Some(session_id) = request.session_id.as_deref() {
            let mut configurations = self.scripts_by_session.lock().await;
            let selection = if let Some(configuration) = configurations.get_mut(session_id) {
                let same_run = match (
                    configuration.bound_run_id.as_deref(),
                    request.run_id.as_deref(),
                ) {
                    (None, Some(run_id)) => {
                        configuration.bound_run_id = Some(run_id.to_owned());
                        true
                    }
                    (None, None) => true,
                    (Some(bound), Some(run_id)) => bound == run_id,
                    (Some(_), None) => false,
                };
                if same_run {
                    configuration
                        .scripts
                        .pop_front()
                        .map_or(ConfiguredSelection::Exhausted, ConfiguredSelection::Script)
                } else {
                    ConfiguredSelection::Unconfigured
                }
            } else {
                ConfiguredSelection::Unconfigured
            };
            if matches!(selection, ConfiguredSelection::Unconfigured) {
                configurations.remove(session_id);
            }
            selection
        } else {
            ConfiguredSelection::Unconfigured
        };
        let script = match configured {
            ConfiguredSelection::Script(script) => script,
            ConfiguredSelection::Exhausted => return Err(configuration_exhausted_error()),
            ConfiguredSelection::Unconfigured => Self::default_script(&request),
        };
        if let Some(expected_context) = script.expected_context.as_ref() {
            let actual_context = normalize_provider_context(&request.messages)?;
            if &actual_context != expected_context {
                return Err(AgentError::new(
                    ErrorCode::InvalidParams,
                    "faux provider context did not match expectedContext",
                )
                .with_details(serde_json::json!({
                    "kind":"faux_context_mismatch",
                    "providerId":"faux"
                })));
            }
        }
        let output = try_stream! {
            for action in script.actions {
                if cancellation.is_cancelled() {
                    Err(cancelled_error())?;
                }
                match action {
                    FauxAction::Event(event) => yield event,
                    FauxAction::Delay(duration) => {
                        let was_cancelled = tokio::select! {
                            biased;
                            () = cancellation.cancelled() => true,
                            () = tokio::time::sleep(duration) => false,
                        };
                        if was_cancelled {
                            Err(cancelled_error())?;
                        }
                    }
                    FauxAction::Error(error) => Err((*error).into_agent_error())?,
                    FauxAction::Disconnect(reason) => Err(disconnect_error(reason))?,
                }
            }
        };
        Ok(Box::pin(output))
    }
}

#[cfg(test)]
mod tests {
    use std::time::Duration;

    use futures_util::TryStreamExt;
    use serde_json::{Value, json};
    use tokio_util::sync::CancellationToken;

    use super::{FauxAction, FauxProvider, FauxScenario, FauxScript};
    use crate::domain::{FinishReason, ProviderEvent};
    use crate::error::ErrorCode;
    use crate::provider::{Provider, ProviderRequest};

    /// 功能：创建 faux Provider 单元测试使用的最小语言中立请求。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn request_for_session(session_id: &str) -> ProviderRequest {
        ProviderRequest {
            model: "faux-1".to_owned(),
            system_instructions: Some("profile guidance".to_owned()),
            messages: Vec::new(),
            tools: Vec::new(),
            max_output_tokens: None,
            session_id: Some(session_id.to_owned()),
            run_id: Some("run-test".to_owned()),
        }
    }

    /// 功能：解析 JSON 场景并将其配置为 Provider 下一次调用的唯一脚本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    async fn configure_scenario(
        provider: &FauxProvider,
        session_id: &str,
        value: Value,
    ) -> Result<(), serde_json::Error> {
        let scenario: FauxScenario = serde_json::from_value(value)?;
        provider
            .configure(session_id, vec![scenario.into_script()])
            .await;
        Ok(())
    }

    /// 功能：验证配置的 faux 脚本严格按队列顺序被单次调用消费。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn consumes_configured_scripts_in_order() -> Result<(), crate::error::AgentError> {
        let provider = FauxProvider::new();
        provider
            .configure(
                "session-order",
                vec![FauxScript::from_events(vec![ProviderEvent::MessageEnd {
                    finish_reason: FinishReason::Length,
                }])],
            )
            .await;
        let stream = provider
            .stream(
                request_for_session("session-order"),
                CancellationToken::new(),
            )
            .await?;
        let events: Vec<_> = stream.try_collect().await?;
        assert!(matches!(
            events.as_slice(),
            [ProviderEvent::MessageEnd {
                finish_reason: FinishReason::Length
            }]
        ));
        Ok(())
    }

    /// 功能：验证两个会话的 faux 队列彼此隔离且不受调用交错顺序影响。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn isolates_script_queues_by_session() -> Result<(), crate::error::AgentError> {
        let provider = FauxProvider::new();
        provider
            .configure(
                "session-a",
                vec![FauxScript::from_events(vec![ProviderEvent::TextDelta {
                    text: "from-a".to_owned(),
                }])],
            )
            .await;
        provider
            .configure(
                "session-b",
                vec![FauxScript::from_events(vec![ProviderEvent::TextDelta {
                    text: "from-b".to_owned(),
                }])],
            )
            .await;

        let events_b: Vec<_> = provider
            .stream(request_for_session("session-b"), CancellationToken::new())
            .await?
            .try_collect()
            .await?;
        let events_a: Vec<_> = provider
            .stream(request_for_session("session-a"), CancellationToken::new())
            .await?
            .try_collect()
            .await?;
        assert!(matches!(
            events_b.as_slice(),
            [ProviderEvent::TextDelta { text }] if text == "from-b"
        ));
        assert!(matches!(
            events_a.as_slice(),
            [ProviderEvent::TextDelta { text }] if text == "from-a"
        ));
        Ok(())
    }

    /// 功能：验证 tool_call 参数以多个 UTF-8 安全增量发出并可无损拼装。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn emits_tool_call_with_partial_argument_assembly()
    -> Result<(), Box<dyn std::error::Error>> {
        let provider = FauxProvider::new();
        configure_scenario(
            &provider,
            "session-tool",
            json!({
                "schemaVersion":"0.1",
                "name":"tool-call-v1",
                "seed":7,
                "steps":[{
                    "type":"tool_call",
                    "toolCallId":"faux-call-1",
                    "name":"file.read",
                    "arguments":{"path":"资料/🙂.txt"}
                }],
                "usage":{"inputTokens":4,"outputTokens":11,"totalTokens":15},
                "extensions":{"org.agentprotocol.test":{"case":"tool"}}
            }),
        )
        .await?;

        let events: Vec<_> = provider
            .stream(
                request_for_session("session-tool"),
                CancellationToken::new(),
            )
            .await?
            .try_collect()
            .await?;
        let deltas: Vec<&str> = events
            .iter()
            .filter_map(|event| match event {
                ProviderEvent::ToolCallArgumentsDelta { call_id, delta }
                    if call_id == "faux-call-1" =>
                {
                    Some(delta.as_str())
                }
                _ => None,
            })
            .collect();
        assert!(deltas.len() > 1);
        assert_eq!(deltas.concat(), r#"{"path":"资料/🙂.txt"}"#);
        assert!(events.iter().any(|event| matches!(
            event,
            ProviderEvent::ToolCallStart { call_id, name }
                if call_id == "faux-call-1" && name == "file.read"
        )));
        assert!(events.iter().any(|event| matches!(
            event,
            ProviderEvent::ToolCallEnd { call_id } if call_id == "faux-call-1"
        )));
        assert!(matches!(
            events.last(),
            Some(ProviderEvent::MessageEnd {
                finish_reason: FinishReason::ToolCalls
            })
        ));
        Ok(())
    }

    /// 功能：验证取消令牌能立即中断 delay step 且后续文本不会发出。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn cancellation_interrupts_delay() -> Result<(), Box<dyn std::error::Error>> {
        let provider = FauxProvider::new();
        configure_scenario(
            &provider,
            "session-cancel",
            json!({
                "schemaVersion":"0.1",
                "name":"cancel-during-delay",
                "seed":499,
                "steps":[
                    {"type":"delay","durationMs":1000},
                    {"type":"text","text":"must not be emitted"}
                ]
            }),
        )
        .await?;
        let cancellation = CancellationToken::new();
        let mut stream = provider
            .stream(request_for_session("session-cancel"), cancellation.clone())
            .await?;
        assert!(matches!(
            stream.try_next().await?,
            Some(ProviderEvent::MessageStart { .. })
        ));
        let canceller = tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(10)).await;
            cancellation.cancel();
        });
        let error = match stream.try_next().await {
            Ok(value) => panic!("delay cancellation unexpectedly emitted {value:?}"),
            Err(error) => error,
        };
        canceller.await?;
        assert_eq!(error.code, ErrorCode::Cancelled);
        Ok(())
    }

    /// 功能：验证 error step 保留重试标志和结构化错误详情并终止流。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn emits_configured_structured_error() -> Result<(), Box<dyn std::error::Error>> {
        let provider = FauxProvider::new();
        configure_scenario(
            &provider,
            "session-error",
            json!({
                "schemaVersion":"0.1",
                "name":"retryable-429",
                "seed":429,
                "steps":[{
                    "type":"error",
                    "error":{
                        "code":-32005,
                        "message":"Synthetic rate limit",
                        "retryable":true,
                        "details":{
                            "kind":"provider_rate_limit",
                            "httpStatus":429,
                            "retryAfterMs":25
                        }
                    }
                }]
            }),
        )
        .await?;
        let mut stream = provider
            .stream(
                request_for_session("session-error"),
                CancellationToken::new(),
            )
            .await?;
        assert!(matches!(
            stream.try_next().await?,
            Some(ProviderEvent::MessageStart { .. })
        ));
        let error = match stream.try_next().await {
            Ok(value) => panic!("error step unexpectedly emitted {value:?}"),
            Err(error) => error,
        };
        assert_eq!(error.code, ErrorCode::ProviderRateLimited);
        assert_eq!(error.message, "Synthetic rate limit");
        assert!(error.retryable);
        assert_eq!(error.details["httpStatus"], 429);
        assert_eq!(error.details["retryAfterMs"], 25);
        Ok(())
    }

    /// 功能：验证 disconnect step 在已发出的 partial 文本后返回结构化断流错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn emits_disconnect_after_partial_output() -> Result<(), Box<dyn std::error::Error>> {
        let provider = FauxProvider::new();
        configure_scenario(
            &provider,
            "session-disconnect",
            json!({
                "schemaVersion":"0.1",
                "name":"disconnect-after-partial",
                "seed":503,
                "steps":[
                    {"type":"text","text":"partial"},
                    {"type":"disconnect","reason":"Synthetic provider disconnect"}
                ],
                "extensions":{}
            }),
        )
        .await?;
        let mut stream = provider
            .stream(
                request_for_session("session-disconnect"),
                CancellationToken::new(),
            )
            .await?;
        assert!(matches!(
            stream.try_next().await?,
            Some(ProviderEvent::MessageStart { .. })
        ));
        assert!(matches!(
            stream.try_next().await?,
            Some(ProviderEvent::TextDelta { text }) if text == "partial"
        ));
        let error = match stream.try_next().await {
            Ok(value) => panic!("disconnect unexpectedly emitted {value:?}"),
            Err(error) => error,
        };
        assert_eq!(error.code, ErrorCode::StreamInterrupted);
        assert_eq!(error.message, "Synthetic provider disconnect");
        assert_eq!(error.details["kind"], "stream_interrupted");
        Ok(())
    }

    /// 功能：验证可选 usage/extensions 线上字段能够严格反序列化并无损重新序列化。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn preserves_optional_usage_and_extensions() -> Result<(), serde_json::Error> {
        let scenario: FauxScenario = serde_json::from_value(json!({
            "schemaVersion":"0.1",
            "name":"optional-fields",
            "seed":1,
            "steps":[],
            "usage":{
                "inputTokens":1,
                "outputTokens":2,
                "totalTokens":3,
                "cachedInputTokens":1,
                "cacheWriteTokens":2,
                "reasoningTokens":1,
                "cost":{"currency":"USD","amount":"0.000001"},
                "extensions":{"org.agentprotocol.test":{"usage":true}}
            },
            "extensions":{"org.agentprotocol.test":{"scenario":true}}
        }))?;
        let encoded = serde_json::to_value(scenario)?;
        assert_eq!(encoded["usage"]["cacheWriteTokens"], 2);
        assert_eq!(encoded["usage"]["cost"]["amount"], "0.000001");
        assert_eq!(
            encoded["extensions"]["org.agentprotocol.test"]["scenario"],
            true
        );
        Ok(())
    }

    /// 功能：验证 scenario continuations 按声明 FIFO 展开且各轮 expectedContext 不串位。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn expands_continuations_in_fifo_order() -> Result<(), serde_json::Error> {
        let scenario: FauxScenario = serde_json::from_value(json!({
            "schemaVersion":"0.1",
            "name":"continuation-order",
            "seed":71,
            "expectedContext":[{"role":"user","content":[{"type":"text","text":"u"}]}],
            "steps":[{"type":"text","text":"first"}],
            "continuations":[
                {
                    "expectedContext":[{"role":"assistant","content":[{"type":"text","text":"a"}]}],
                    "steps":[{"type":"text","text":"second"}]
                },
                {
                    "expectedContext":[{"role":"tool","content":[{"type":"text","text":"r"}],"toolCallId":"c1","toolName":"file.read","isError":false}],
                    "steps":[{"type":"text","text":"third"}]
                }
            ]
        }))?;
        let scripts = scenario.into_scripts();
        assert_eq!(scripts.len(), 3);
        assert_eq!(scripts[0].expected_context.as_ref().map(Vec::len), Some(1));
        assert_eq!(scripts[1].expected_context.as_ref().map(Vec::len), Some(1));
        assert_eq!(scripts[2].expected_context.as_ref().map(Vec::len), Some(1));
        assert!(matches!(
            &scripts[0].actions[1],
            FauxAction::Event(ProviderEvent::TextDelta { text }) if text == "first"
        ));
        assert!(matches!(
            &scripts[1].actions[1],
            FauxAction::Event(ProviderEvent::TextDelta { text }) if text == "second"
        ));
        assert!(matches!(
            &scripts[2].actions[1],
            FauxAction::Event(ProviderEvent::TextDelta { text }) if text == "third"
        ));
        Ok(())
    }

    /// 功能：验证同一 run continuation 耗尽时失败，且已消费配置不会污染下一 run。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn fails_closed_when_same_run_exhausts_continuations()
    -> Result<(), Box<dyn std::error::Error>> {
        let provider = FauxProvider::new();
        let scenario: FauxScenario = serde_json::from_value(json!({
            "schemaVersion":"0.1",
            "name":"exhausted-continuation",
            "seed":72,
            "steps":[{"type":"tool_call","toolCallId":"call-1","name":"file.read","arguments":{"path":"a.txt"}}]
        }))?;
        provider
            .configure("session-exhausted", scenario.into_scripts())
            .await;
        let request = request_for_session("session-exhausted");
        let first = provider
            .stream(request.clone(), CancellationToken::new())
            .await?;
        let _: Vec<_> = first.try_collect().await?;
        let error = match provider.stream(request, CancellationToken::new()).await {
            Ok(_) => panic!("exhausted configured run unexpectedly received a default script"),
            Err(error) => error,
        };
        assert_eq!(error.details["kind"], "faux_configuration_exhausted");
        assert!(!error.retryable);

        let mut next_run = request_for_session("session-exhausted");
        next_run.run_id = Some("run-next".to_owned());
        let next = provider.stream(next_run, CancellationToken::new()).await?;
        let next_events: Vec<_> = next.try_collect().await?;
        assert!(next_events.iter().any(|event| matches!(
            event,
            ProviderEvent::TextDelta { text }
                if text == "Faux provider received an empty prompt."
        )));
        Ok(())
    }

    /// 功能：验证 faux scenario 反序列化拒绝超过 Schema 上限的 delay 与非法扩展键。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn rejects_values_outside_faux_schema() {
        let excessive_delay = serde_json::from_value::<FauxScenario>(json!({
            "schemaVersion":"0.1",
            "name":"invalid-delay",
            "seed":1,
            "steps":[{"type":"delay","durationMs":60001}]
        }));
        assert!(excessive_delay.is_err());

        let invalid_extension = serde_json::from_value::<FauxScenario>(json!({
            "schemaVersion":"0.1",
            "name":"invalid-extension",
            "seed":1,
            "steps":[],
            "extensions":{"not_namespaced":true}
        }));
        assert!(invalid_extension.is_err());
    }
}
