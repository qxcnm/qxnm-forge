//! 品牌中立的 Agent Profile 实体、校验与持久化服务。
//!
//! 作者：高宏顺 <18272669457@163.com>

use std::collections::{BTreeMap, BTreeSet};

use chrono::{SecondsFormat, Utc};
use sea_orm::entity::prelude::*;
use sea_orm::{
    ActiveModelTrait, ColumnTrait, ConnectionTrait, DatabaseConnection, EntityTrait, QueryFilter,
    QueryOrder, Set, TransactionTrait,
};
use serde::{Deserialize, Serialize};
use serde_json::json;
use uuid::Uuid;

use crate::error::{AgentError, ErrorCode};
use crate::protocol::ProviderSelection;

const MAX_SAFE_INTEGER: u64 = 9_007_199_254_740_991;

/// Agent Profile 使用的完整 Provider 路由身份。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct AgentProfileModel {
    pub provider_id: String,
    pub model_id: String,
    pub api_family: String,
}

/// Profile 对危险工具的收紧模式。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum DangerousActionMode {
    Ask,
    Deny,
}

/// Profile 对响应篇幅的请求级偏好。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ResponseStyle {
    Concise,
    Balanced,
    Detailed,
}

impl ResponseStyle {
    /// 功能：返回写入 request-local system message 的稳定协议文本。
    ///
    /// 输出：与 JSON 枚举值相同的 ASCII 字符串。
    /// 不变量：不本地化、不读取配置，跨实现可逐字比较。
    /// 失败：本方法不失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Concise => "concise",
            Self::Balanced => "balanced",
            Self::Detailed => "detailed",
        }
    }
}

/// 可丢弃但会在单次运行内冻结的 Agent 行为偏好。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct AgentProfileBehavior {
    pub response_style: ResponseStyle,
    pub plan_first: bool,
    pub review_changes: bool,
}

/// 创建或完整替换 Agent Profile 的严格输入。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct AgentProfileInput {
    pub display_name: String,
    pub description: String,
    pub enabled: bool,
    pub instructions: String,
    pub model: AgentProfileModel,
    pub requested_tool_ids: Vec<String>,
    pub dangerous_action_mode: DangerousActionMode,
    pub behavior: AgentProfileBehavior,
}

impl AgentProfileInput {
    /// 功能：按公共 schema 校验并规范化不可信 Profile 输入。
    ///
    /// 输入：RPC 解码后的闭合对象。
    /// 输出：文本边缘空白已移除、其余语义保持不变的可持久化输入。
    /// 不变量：工具 ID 必须唯一；Provider/family/tool ID 仅接受受限 ASCII；不接受 secret、endpoint 等额外字段。
    /// 失败：长度、枚举、身份格式、工具数量或重复项无效时返回 `invalid_params`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn validate_and_normalize(mut self) -> Result<Self, AgentError> {
        require_char_count(&self.display_name, 0, 48, "profile.displayName")?;
        require_char_count(&self.description, 0, 160, "profile.description")?;
        require_char_count(&self.instructions, 0, 12_000, "profile.instructions")?;
        self.display_name = self.display_name.trim().to_owned();
        self.description = self.description.trim().to_owned();
        self.instructions = self.instructions.trim().to_owned();

        require_char_count(&self.display_name, 1, 48, "profile.displayName")?;
        require_char_count(&self.description, 0, 160, "profile.description")?;
        require_char_count(&self.instructions, 1, 12_000, "profile.instructions")?;
        require_ascii_id(
            &self.model.provider_id,
            128,
            true,
            "profile.model.providerId",
        )?;
        require_model_id(&self.model.model_id)?;
        require_ascii_id(
            &self.model.api_family,
            128,
            false,
            "profile.model.apiFamily",
        )?;
        if self.requested_tool_ids.len() > 256 {
            return Err(invalid_profile_field("profile.requestedToolIds"));
        }
        let mut unique = BTreeSet::new();
        for tool_id in &self.requested_tool_ids {
            require_tool_id(tool_id)?;
            if !unique.insert(tool_id.clone()) {
                return Err(invalid_profile_field("profile.requestedToolIds"));
            }
        }
        self.requested_tool_ids.sort();
        Ok(self)
    }
}

/// 功能：验证 modelId 原始值的 schema 长度并拒绝边缘空白。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn require_model_id(value: &str) -> Result<(), AgentError> {
    require_char_count(value, 1, 256, "profile.model.modelId")?;
    if value == value.trim() {
        Ok(())
    } else {
        Err(invalid_profile_field("profile.model.modelId"))
    }
}

/// application service 返回的完整 Agent Profile 投影。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct AgentProfile {
    pub profile_id: String,
    pub revision: u64,
    pub display_name: String,
    pub description: String,
    pub enabled: bool,
    pub instructions: String,
    pub model: AgentProfileModel,
    pub requested_tool_ids: Vec<String>,
    pub dangerous_action_mode: DangerousActionMode,
    pub behavior: AgentProfileBehavior,
    pub created_at: String,
    pub updated_at: String,
}

/// `run/start` 引用的稳定 Profile identity 与 CAS revision。
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct AgentProfileReference {
    pub profile_id: String,
    pub revision: u64,
}

impl AgentProfileReference {
    /// 功能：验证运行引用满足公共 opaque ID 与正安全整数边界。
    ///
    /// 输出：引用可用于数据库查询时成功。
    /// 不变量：不把引用解释为路径、命令或产品品牌标识。
    /// 失败：ID 或 revision 无效时返回 `invalid_params`。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub fn validate(&self) -> Result<(), AgentError> {
        require_profile_id(&self.profile_id)?;
        require_revision(self.revision, "agentProfile.revision")
    }
}

/// 接受运行时冻结并写入 `run.accepted` 的安全 Profile 快照。
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentProfileRunSnapshot {
    pub profile_id: String,
    pub revision: u64,
    pub display_name: String,
    pub instructions: String,
    pub model: AgentProfileModel,
    pub requested_tool_ids: Vec<String>,
    pub effective_tool_ids: Vec<String>,
    pub dangerous_action_mode: DangerousActionMode,
    pub behavior: AgentProfileBehavior,
}

impl AgentProfileRunSnapshot {
    /// 功能：生成只在当前 Provider 请求中使用的稳定 system message 文本。
    ///
    /// 输出：Profile instructions 与三个行为偏好的品牌中立纯文本组合。
    /// 不变量：不包含 Profile 描述、时间戳、secret 或宿主状态，也不写入 Session message 历史。
    /// 失败：本方法不失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn system_message_text(&self) -> String {
        format!(
            "{}\n\nBehavior preferences:\nresponseStyle={}\nplanFirst={}\nreviewChanges={}",
            self.instructions,
            self.behavior.response_style.as_str(),
            self.behavior.plan_first,
            self.behavior.review_changes
        )
    }

    /// 功能：判断指定工具是否属于运行接受时冻结的有效工具集。
    ///
    /// 输入：Provider 返回的不可信工具名。
    /// 输出：工具名精确存在于快照时为 true。
    /// 不变量：不重新解释 wildcard、前缀或大小写。
    /// 失败：本方法不失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn permits_tool(&self, tool_name: &str) -> bool {
        self.effective_tool_ids
            .iter()
            .any(|candidate| candidate == tool_name)
    }
}

/// SeaORM 对品牌中立 `agent_profiles` 表的实体映射。
pub mod entity {
    use sea_orm::entity::prelude::*;

    /// Agent Profile 的 SQLite 主行模型；工具集合保存在规范化子表。
    #[derive(Clone, Debug, PartialEq, DeriveEntityModel)]
    #[sea_orm(table_name = "agent_profiles")]
    pub struct Model {
        #[sea_orm(primary_key, auto_increment = false)]
        pub profile_id: String,
        pub revision: i64,
        pub display_name: String,
        pub description: String,
        pub enabled: bool,
        pub instructions: String,
        pub provider_id: String,
        pub model_id: String,
        pub api_family: String,
        pub dangerous_action_mode: String,
        pub response_style: String,
        pub plan_first: bool,
        pub review_changes: bool,
        pub created_at: String,
        pub updated_at: String,
    }

    /// Agent Profile 当前没有外键关系。
    #[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
    pub enum Relation {}

    impl ActiveModelBehavior for ActiveModel {}
}

/// SeaORM 对规范化 `agent_profile_tools` 工具集合表的实体映射。
pub mod tool_entity {
    use sea_orm::entity::prelude::*;

    /// 一个 Profile 请求的单个稳定工具 ID。
    #[derive(Clone, Debug, PartialEq, DeriveEntityModel)]
    #[sea_orm(table_name = "agent_profile_tools")]
    pub struct Model {
        #[sea_orm(primary_key, auto_increment = false)]
        pub profile_id: String,
        #[sea_orm(primary_key, auto_increment = false)]
        pub tool_id: String,
    }

    /// 工具行的外键由共同 DDL 管理，ORM 不执行级联图遍历。
    #[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
    pub enum Relation {}

    impl ActiveModelBehavior for ActiveModel {}
}

/// 基于共享 SeaORM 连接的 Agent Profile CRUD 与运行绑定服务。
#[derive(Clone, Debug)]
pub struct AgentProfileService {
    database: DatabaseConnection,
}

impl AgentProfileService {
    /// 功能：从已完成 schema 0.2 迁移的连接创建 Profile 服务。
    ///
    /// 输入：应用数据库共享连接池句柄。
    /// 输出：不立即执行查询的轻量服务。
    /// 不变量：服务不读取 Session journal、CredentialStore 或 workspace 文件。
    /// 失败：本方法不失败；连接错误由具体操作映射为脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[must_use]
    pub fn new(database: DatabaseConnection) -> Self {
        Self { database }
    }

    /// 功能：按更新时间倒序、ID 正序列出全部 Agent Profile。
    ///
    /// 输出：数据库当前一致性视图中的完整 Profile 投影。
    /// 不变量：排序稳定；损坏的 JSON/枚举不会作为部分可信结果返回。
    /// 失败：查询或行解码失败时返回脱敏内部错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn list(&self) -> Result<Vec<AgentProfile>, AgentError> {
        let transaction = self.database.begin().await.map_err(storage_error)?;
        let rows = entity::Entity::find()
            .order_by_desc(entity::Column::UpdatedAt)
            .order_by_asc(entity::Column::ProfileId)
            .all(&transaction)
            .await
            .map_err(storage_error)?;
        let tools = tool_entity::Entity::find()
            .order_by_asc(tool_entity::Column::ProfileId)
            .order_by_asc(tool_entity::Column::ToolId)
            .all(&transaction)
            .await
            .map_err(storage_error)?;
        let mut tools_by_profile = BTreeMap::<String, Vec<String>>::new();
        for tool in tools {
            tools_by_profile
                .entry(tool.profile_id)
                .or_default()
                .push(tool.tool_id);
        }
        let profiles = rows
            .into_iter()
            .map(|row| {
                let requested_tool_ids =
                    tools_by_profile.remove(&row.profile_id).unwrap_or_default();
                profile_from_row(row, requested_tool_ids)
            })
            .collect::<Result<Vec<_>, _>>()?;
        if !tools_by_profile.is_empty() {
            transaction.rollback().await.map_err(storage_error)?;
            return Err(corrupted_profile());
        }
        transaction.commit().await.map_err(storage_error)?;
        Ok(profiles)
    }

    /// 功能：创建 revision 1 且带稳定 UTC 时间的 Agent Profile。
    ///
    /// 输入：严格闭合但尚未完成语义校验的 Profile 输入。
    /// 输出：已提交数据库的完整 Profile。
    /// 不变量：新 ID 为品牌中立 opaque ID；输入无效或写入失败时不返回伪成功。
    /// 失败：输入违规返回 `invalid_params`；数据库失败返回脱敏内部错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn create(&self, input: AgentProfileInput) -> Result<AgentProfile, AgentError> {
        let input = input.validate_and_normalize()?;
        let timestamp = utc_now();
        let profile = AgentProfile {
            profile_id: format!("agent-{}", Uuid::new_v4()),
            revision: 1,
            display_name: input.display_name,
            description: input.description,
            enabled: input.enabled,
            instructions: input.instructions,
            model: input.model,
            requested_tool_ids: input.requested_tool_ids,
            dangerous_action_mode: input.dangerous_action_mode,
            behavior: input.behavior,
            created_at: timestamp.clone(),
            updated_at: timestamp,
        };
        let transaction = self.database.begin().await.map_err(storage_error)?;
        entity::ActiveModel::from(profile_to_row(&profile)?)
            .insert(&transaction)
            .await
            .map_err(storage_error)?;
        insert_tools(
            &transaction,
            &profile.profile_id,
            &profile.requested_tool_ids,
        )
        .await?;
        transaction.commit().await.map_err(storage_error)?;
        Ok(profile)
    }

    /// 功能：以 expected revision 原子完整替换 Agent Profile。
    ///
    /// 输入：稳定 Profile ID、正安全整数 revision 与完整新输入。
    /// 输出：保留 ID/createdAt、revision 增加 1 的已提交投影。
    /// 不变量：校验、缺失或 CAS 冲突时不修改任何字段；更新时间使用 UTC。
    /// 失败：输入无效、Profile 缺失、stale CAS 或数据库失败时返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn update(
        &self,
        profile_id: &str,
        expected_revision: u64,
        input: AgentProfileInput,
    ) -> Result<AgentProfile, AgentError> {
        require_profile_id(profile_id)?;
        require_revision(expected_revision, "expectedRevision")?;
        let input = input.validate_and_normalize()?;
        if expected_revision == MAX_SAFE_INTEGER {
            let transaction = self.database.begin().await.map_err(storage_error)?;
            let current = entity::Entity::find_by_id(profile_id)
                .one(&transaction)
                .await
                .map_err(storage_error)?;
            transaction.rollback().await.map_err(storage_error)?;
            let Some(current) = current else {
                return Err(profile_not_found(profile_id));
            };
            let actual_revision = u64::try_from(current.revision)
                .ok()
                .filter(|revision| (1..=MAX_SAFE_INTEGER).contains(revision))
                .ok_or_else(corrupted_profile)?;
            if actual_revision != expected_revision {
                return Err(stale_profile(
                    profile_id,
                    actual_revision,
                    expected_revision,
                    true,
                ));
            }
            return Err(revision_exhausted(profile_id));
        }
        let current_revision = revision_to_i64(expected_revision)?;
        let next_revision = expected_revision
            .checked_add(1)
            .filter(|revision| *revision <= MAX_SAFE_INTEGER)
            .ok_or_else(|| invalid_profile_field("expectedRevision"))?;
        let next_revision = revision_to_i64(next_revision)?;
        let timestamp = utc_now();
        let requested_tool_ids = input.requested_tool_ids.clone();
        let transaction = self.database.begin().await.map_err(storage_error)?;
        let result = entity::Entity::update_many()
            .col_expr(entity::Column::Revision, Expr::value(next_revision))
            .col_expr(entity::Column::DisplayName, Expr::value(input.display_name))
            .col_expr(entity::Column::Description, Expr::value(input.description))
            .col_expr(entity::Column::Enabled, Expr::value(input.enabled))
            .col_expr(
                entity::Column::Instructions,
                Expr::value(input.instructions),
            )
            .col_expr(
                entity::Column::ProviderId,
                Expr::value(input.model.provider_id),
            )
            .col_expr(entity::Column::ModelId, Expr::value(input.model.model_id))
            .col_expr(
                entity::Column::ApiFamily,
                Expr::value(input.model.api_family),
            )
            .col_expr(
                entity::Column::DangerousActionMode,
                Expr::value(dangerous_mode_text(input.dangerous_action_mode)),
            )
            .col_expr(
                entity::Column::ResponseStyle,
                Expr::value(input.behavior.response_style.as_str()),
            )
            .col_expr(
                entity::Column::PlanFirst,
                Expr::value(input.behavior.plan_first),
            )
            .col_expr(
                entity::Column::ReviewChanges,
                Expr::value(input.behavior.review_changes),
            )
            .col_expr(entity::Column::UpdatedAt, Expr::value(timestamp))
            .filter(entity::Column::ProfileId.eq(profile_id))
            .filter(entity::Column::Revision.eq(current_revision))
            .exec(&transaction)
            .await
            .map_err(storage_error)?;
        if result.rows_affected != 1 {
            let current = entity::Entity::find_by_id(profile_id)
                .one(&transaction)
                .await
                .map_err(storage_error)?;
            transaction.rollback().await.map_err(storage_error)?;
            return Err(profile_conflict(
                profile_id,
                current.as_ref(),
                expected_revision,
                true,
            ));
        }
        tool_entity::Entity::delete_many()
            .filter(tool_entity::Column::ProfileId.eq(profile_id))
            .exec(&transaction)
            .await
            .map_err(storage_error)?;
        insert_tools(&transaction, profile_id, &requested_tool_ids).await?;
        let updated = entity::Entity::find_by_id(profile_id)
            .one(&transaction)
            .await
            .map_err(storage_error)?
            .ok_or_else(|| storage_error(sea_orm::DbErr::RecordNotFound("profile".to_owned())))?;
        transaction.commit().await.map_err(storage_error)?;
        profile_from_row(updated, requested_tool_ids)
    }

    /// 功能：以 expected revision 原子删除 Agent Profile。
    ///
    /// 输入：稳定 Profile ID 与正安全整数 CAS revision。
    /// 输出：精确删除一行时成功。
    /// 不变量：缺失或 stale 时不删除任何 Profile；不级联到 Session journal。
    /// 失败：ID/revision、Profile 缺失、CAS 冲突或数据库失败时返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn delete(&self, profile_id: &str, expected_revision: u64) -> Result<(), AgentError> {
        require_profile_id(profile_id)?;
        require_revision(expected_revision, "expectedRevision")?;
        let transaction = self.database.begin().await.map_err(storage_error)?;
        let result = entity::Entity::delete_many()
            .filter(entity::Column::ProfileId.eq(profile_id))
            .filter(entity::Column::Revision.eq(revision_to_i64(expected_revision)?))
            .exec(&transaction)
            .await
            .map_err(storage_error)?;
        if result.rows_affected != 1 {
            let current = entity::Entity::find_by_id(profile_id)
                .one(&transaction)
                .await
                .map_err(storage_error)?;
            transaction.rollback().await.map_err(storage_error)?;
            return Err(profile_conflict(
                profile_id,
                current.as_ref(),
                expected_revision,
                true,
            ));
        }
        tool_entity::Entity::delete_many()
            .filter(tool_entity::Column::ProfileId.eq(profile_id))
            .exec(&transaction)
            .await
            .map_err(storage_error)?;
        transaction.commit().await.map_err(storage_error)?;
        Ok(())
    }

    /// 功能：在运行产生 durable 副作用前解析并冻结 Profile 引用。
    ///
    /// 输入：客户端 Profile 引用、规范化 Provider 选择与当前真实工具名称。
    /// 输出：模型三元身份完全匹配、enabled 且 revision 一致的安全运行快照。
    /// 不变量：有效工具只取 requested 与当前注册表的精确交集；读取不修改 Profile。
    /// 失败：引用无效、缺失、stale、disabled、模型不匹配或存储损坏时返回结构化错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub async fn resolve_for_run(
        &self,
        reference: &AgentProfileReference,
        provider: &ProviderSelection,
        available_tools: &[String],
        read_only_tools: &[String],
    ) -> Result<AgentProfileRunSnapshot, AgentError> {
        reference.validate()?;
        let transaction = self.database.begin().await.map_err(storage_error)?;
        let row = entity::Entity::find_by_id(&reference.profile_id)
            .one(&transaction)
            .await
            .map_err(storage_error)?
            .ok_or_else(|| profile_not_found(&reference.profile_id))?;
        let requested_tool_ids = tool_entity::Entity::find()
            .filter(tool_entity::Column::ProfileId.eq(&reference.profile_id))
            .order_by_asc(tool_entity::Column::ToolId)
            .all(&transaction)
            .await
            .map_err(storage_error)?
            .into_iter()
            .map(|tool| tool.tool_id)
            .collect();
        let profile = profile_from_row(row, requested_tool_ids)?;
        transaction.commit().await.map_err(storage_error)?;
        if profile.revision != reference.revision {
            return Err(stale_profile(
                &reference.profile_id,
                profile.revision,
                reference.revision,
                false,
            ));
        }
        if !profile.enabled {
            return Err(
                AgentError::new(ErrorCode::PermissionDenied, "agent profile is disabled")
                    .with_details(json!({
                        "kind":"agent_profile_disabled",
                        "resourceId":reference.profile_id
                    })),
            );
        }
        let selected_family = provider.api_family.as_deref();
        if profile.model.provider_id != provider.id
            || profile.model.model_id != provider.model_id
            || Some(profile.model.api_family.as_str()) != selected_family
        {
            return Err(AgentError::new(
                ErrorCode::InvalidParams,
                "run model does not match agent profile",
            )
            .with_details(json!({
                "kind":"agent_profile_model_mismatch",
                "providerId":profile.model.provider_id,
                "modelId":profile.model.model_id,
                "apiFamily":profile.model.api_family
            })));
        }
        let available = available_tools
            .iter()
            .map(String::as_str)
            .collect::<BTreeSet<_>>();
        let read_only = read_only_tools
            .iter()
            .map(String::as_str)
            .collect::<BTreeSet<_>>();
        let effective_tool_ids = profile
            .requested_tool_ids
            .iter()
            .filter(|tool_id| {
                available.contains(tool_id.as_str())
                    && (profile.dangerous_action_mode == DangerousActionMode::Ask
                        || read_only.contains(tool_id.as_str()))
            })
            .cloned()
            .collect();
        Ok(AgentProfileRunSnapshot {
            profile_id: profile.profile_id,
            revision: profile.revision,
            display_name: profile.display_name,
            instructions: profile.instructions,
            model: profile.model,
            requested_tool_ids: profile.requested_tool_ids,
            effective_tool_ids,
            dangerous_action_mode: profile.dangerous_action_mode,
            behavior: profile.behavior,
        })
    }
}

/// 功能：把公共 Profile 投影编码为 SeaORM 行。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn profile_to_row(profile: &AgentProfile) -> Result<entity::Model, AgentError> {
    Ok(entity::Model {
        profile_id: profile.profile_id.clone(),
        revision: revision_to_i64(profile.revision)?,
        display_name: profile.display_name.clone(),
        description: profile.description.clone(),
        enabled: profile.enabled,
        instructions: profile.instructions.clone(),
        provider_id: profile.model.provider_id.clone(),
        model_id: profile.model.model_id.clone(),
        api_family: profile.model.api_family.clone(),
        dangerous_action_mode: dangerous_mode_text(profile.dangerous_action_mode).to_owned(),
        response_style: profile.behavior.response_style.as_str().to_owned(),
        plan_first: profile.behavior.plan_first,
        review_changes: profile.behavior.review_changes,
        created_at: profile.created_at.clone(),
        updated_at: profile.updated_at.clone(),
    })
}

/// 功能：严格解码数据库行并拒绝损坏或未知枚举。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn profile_from_row(
    row: entity::Model,
    requested_tool_ids: Vec<String>,
) -> Result<AgentProfile, AgentError> {
    require_profile_id(&row.profile_id).map_err(|_| corrupted_profile())?;
    validate_utc_timestamp(&row.created_at)?;
    validate_utc_timestamp(&row.updated_at)?;
    let revision = u64::try_from(row.revision).map_err(|_| corrupted_profile())?;
    require_revision(revision, "revision").map_err(|_| corrupted_profile())?;
    let dangerous_action_mode = match row.dangerous_action_mode.as_str() {
        "ask" => DangerousActionMode::Ask,
        "deny" => DangerousActionMode::Deny,
        _ => return Err(corrupted_profile()),
    };
    let response_style = match row.response_style.as_str() {
        "concise" => ResponseStyle::Concise,
        "balanced" => ResponseStyle::Balanced,
        "detailed" => ResponseStyle::Detailed,
        _ => return Err(corrupted_profile()),
    };
    let input = AgentProfileInput {
        display_name: row.display_name,
        description: row.description,
        enabled: row.enabled,
        instructions: row.instructions,
        model: AgentProfileModel {
            provider_id: row.provider_id,
            model_id: row.model_id,
            api_family: row.api_family,
        },
        requested_tool_ids,
        dangerous_action_mode,
        behavior: AgentProfileBehavior {
            response_style,
            plan_first: row.plan_first,
            review_changes: row.review_changes,
        },
    }
    .validate_and_normalize()
    .map_err(|_| corrupted_profile())?;
    Ok(AgentProfile {
        profile_id: row.profile_id,
        revision,
        display_name: input.display_name,
        description: input.description,
        enabled: input.enabled,
        instructions: input.instructions,
        model: input.model,
        requested_tool_ids: input.requested_tool_ids,
        dangerous_action_mode: input.dangerous_action_mode,
        behavior: input.behavior,
        created_at: row.created_at,
        updated_at: row.updated_at,
    })
}

/// 功能：验证数据库时间为可解析且以 `Z` 结尾的 RFC3339 UTC 字符串。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_utc_timestamp(value: &str) -> Result<(), AgentError> {
    if value.ends_with('Z') && chrono::DateTime::parse_from_rfc3339(value).is_ok() {
        Ok(())
    } else {
        Err(corrupted_profile())
    }
}

/// 功能：在同一事务内写入规范排序且已验证的 Profile 工具集合。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
async fn insert_tools<C>(
    connection: &C,
    profile_id: &str,
    tool_ids: &[String],
) -> Result<(), AgentError>
where
    C: ConnectionTrait,
{
    for tool_id in tool_ids {
        tool_entity::ActiveModel {
            profile_id: Set(profile_id.to_owned()),
            tool_id: Set(tool_id.clone()),
        }
        .insert(connection)
        .await
        .map_err(storage_error)?;
    }
    Ok(())
}

/// 功能：把危险操作枚举映射为稳定数据库文本。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
const fn dangerous_mode_text(mode: DangerousActionMode) -> &'static str {
    match mode {
        DangerousActionMode::Ask => "ask",
        DangerousActionMode::Deny => "deny",
    }
}

/// 功能：生成带毫秒精度和 `Z` 后缀的稳定 UTC 时间。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn utc_now() -> String {
    Utc::now().to_rfc3339_opts(SecondsFormat::Millis, true)
}

/// 功能：验证 Unicode 字符数量位于闭区间。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn require_char_count(
    value: &str,
    minimum: usize,
    maximum: usize,
    field: &'static str,
) -> Result<(), AgentError> {
    let count = value.chars().count();
    if (minimum..=maximum).contains(&count) {
        Ok(())
    } else {
        Err(invalid_profile_field(field))
    }
}

/// 功能：验证 Provider 或 API family 的受限小写 ASCII 标识。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn require_ascii_id(
    value: &str,
    maximum: usize,
    allow_dot: bool,
    field: &'static str,
) -> Result<(), AgentError> {
    let valid = !value.is_empty()
        && value.len() <= maximum
        && (value.as_bytes()[0].is_ascii_lowercase() || value.as_bytes()[0].is_ascii_digit())
        && value.bytes().all(|byte| {
            byte.is_ascii_lowercase()
                || byte.is_ascii_digit()
                || byte == b'-'
                || (allow_dot && byte == b'.')
        });
    if valid {
        Ok(())
    } else {
        Err(invalid_profile_field(field))
    }
}

/// 功能：验证工具名称符合公共 schema 的闭合 ASCII 模式。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn require_tool_id(value: &str) -> Result<(), AgentError> {
    let valid = !value.is_empty()
        && value.len() <= 128
        && value.as_bytes()[0].is_ascii_lowercase()
        && value.bytes().all(|byte| {
            byte.is_ascii_lowercase() || byte.is_ascii_digit() || matches!(byte, b'_' | b'.' | b'-')
        });
    if valid {
        Ok(())
    } else {
        Err(invalid_profile_field("profile.requestedToolIds"))
    }
}

/// 功能：验证 Profile opaque ID 与长度边界。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn require_profile_id(value: &str) -> Result<(), AgentError> {
    let valid = !value.is_empty()
        && value.len() <= 128
        && value.as_bytes()[0].is_ascii_alphanumeric()
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b':' | b'-'));
    if valid {
        Ok(())
    } else {
        Err(invalid_profile_field("profileId"))
    }
}

/// 功能：验证 revision 为 JavaScript 安全范围内的正整数。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn require_revision(revision: u64, field: &'static str) -> Result<(), AgentError> {
    if (1..=MAX_SAFE_INTEGER).contains(&revision) {
        Ok(())
    } else {
        Err(invalid_profile_field(field))
    }
}

/// 功能：把已验证 revision 转为 SQLite INTEGER。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn revision_to_i64(revision: u64) -> Result<i64, AgentError> {
    require_revision(revision, "revision")?;
    i64::try_from(revision).map_err(|_| invalid_profile_field("revision"))
}

/// 功能：构造不包含输入值的 Profile 字段错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn invalid_profile_field(field: &'static str) -> AgentError {
    let _ = field;
    AgentError::new(ErrorCode::InvalidParams, "agent profile is invalid")
        .with_details(json!({"kind":"agent_profile_invalid","field":"profile"}))
}

/// 功能：根据 CAS 查询结果构造缺失或 stale 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn profile_conflict(
    profile_id: &str,
    current: Option<&entity::Model>,
    expected_revision: u64,
    retryable: bool,
) -> AgentError {
    match current {
        Some(current) => match u64::try_from(current.revision) {
            Ok(current_revision) if (1..=MAX_SAFE_INTEGER).contains(&current_revision) => {
                stale_profile(profile_id, current_revision, expected_revision, retryable)
            }
            _ => corrupted_profile(),
        },
        None => profile_not_found(profile_id),
    }
}

/// 功能：构造稳定且不可重试的 Profile 缺失错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn profile_not_found(profile_id: &str) -> AgentError {
    AgentError::new(ErrorCode::InvalidParams, "agent profile was not found").with_details(json!({
        "kind":"agent_profile_not_found",
        "resourceId":profile_id
    }))
}

/// 功能：构造最大安全 revision 已耗尽的冻结资源上限错误。
///
/// 输入：已验证且当前 revision 与最大 expected revision 匹配的 Profile ID。
/// 输出：固定 `-32009`、不可重试且不包含数据库内容的 portable 错误。
/// 不变量：调用方必须在任何 update 写入前返回此错误。
/// 失败：本函数只构造错误，不执行 I/O。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn revision_exhausted(profile_id: &str) -> AgentError {
    AgentError::new(
        ErrorCode::OutputLimitExceeded,
        "agent profile revision is exhausted",
    )
    .with_details(json!({
        "kind":"agent_profile_revision_exhausted",
        "resourceId":profile_id
    }))
}

/// 功能：构造携带当前与预期 revision 的稳定 CAS 冲突错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn stale_profile(
    profile_id: &str,
    current_revision: u64,
    expected_revision: u64,
    retryable: bool,
) -> AgentError {
    AgentError::new(ErrorCode::Conflict, "agent profile revision is stale")
        .retryable(retryable)
        .with_details(json!({
            "kind":"stale_agent_profile_revision",
            "resourceId":profile_id,
            "expectedRevision":expected_revision,
            "currentRevision":current_revision
        }))
}

/// 功能：把 ORM 错误映射为不泄漏 SQL、路径或数据库内容的内部错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn storage_error(_error: sea_orm::DbErr) -> AgentError {
    AgentError::new(
        ErrorCode::InternalError,
        "agent profile storage operation failed",
    )
    .with_details(json!({"kind":"agent_profile_storage_error"}))
}

/// 功能：构造数据库 Profile 行损坏的脱敏内部错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn corrupted_profile() -> AgentError {
    AgentError::new(ErrorCode::InternalError, "agent profile storage is corrupt")
        .with_details(json!({"kind":"agent_profile_storage_corrupt"}))
}

#[cfg(test)]
mod tests {
    use sea_orm::{ConnectionTrait as _, DatabaseBackend, Statement};
    use tempfile::tempdir;

    use crate::storage::{DatabaseConfiguration, connect_application_database};

    use super::{
        AgentProfileBehavior, AgentProfileInput, AgentProfileModel, AgentProfileReference,
        AgentProfileService, DangerousActionMode, MAX_SAFE_INTEGER, ResponseStyle,
        corrupted_profile,
    };
    use crate::error::AgentError;
    use crate::protocol::ProviderSelection;

    /// 功能：创建测试用的完整 Profile 输入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn profile_input(display_name: &str) -> AgentProfileInput {
        AgentProfileInput {
            display_name: display_name.to_owned(),
            description: "测试 Profile".to_owned(),
            enabled: true,
            instructions: "只执行有证据支持的变更。".to_owned(),
            model: AgentProfileModel {
                provider_id: "faux".to_owned(),
                model_id: "faux-v1".to_owned(),
                api_family: "faux".to_owned(),
            },
            requested_tool_ids: vec!["file.read".to_owned()],
            dangerous_action_mode: DangerousActionMode::Ask,
            behavior: AgentProfileBehavior {
                response_style: ResponseStyle::Balanced,
                plan_first: true,
                review_changes: true,
            },
        }
    }

    /// 功能：验证 CRUD、CAS 失败不变更及 SQLite 重开持久化。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn crud_cas_and_reopen_are_durable() -> Result<(), Box<dyn std::error::Error>> {
        let directory = tempdir()?;
        let configuration = DatabaseConfiguration::sqlite_default(directory.path());
        let database = connect_application_database(&configuration).await?;
        let service = AgentProfileService::new(database.clone());
        let created = service.create(profile_input("  编码助手  ")).await?;
        assert_eq!(created.display_name, "编码助手");
        assert_eq!(created.revision, 1);

        let stale = service
            .update(&created.profile_id, 2, profile_input("错误覆盖"))
            .await
            .expect_err("stale update must fail");
        assert_eq!(stale.details["kind"], "stale_agent_profile_revision");
        assert!(stale.retryable);
        let updated = service
            .update(&created.profile_id, 1, profile_input("审阅助手"))
            .await?;
        assert_eq!(updated.revision, 2);
        assert_eq!(updated.created_at, created.created_at);
        drop(service);
        database.close().await?;

        let reopened = connect_application_database(&configuration).await?;
        let service = AgentProfileService::new(reopened);
        let profiles = service.list().await?;
        assert_eq!(profiles, vec![updated.clone()]);
        let stale_delete = service
            .delete(&updated.profile_id, 1)
            .await
            .expect_err("stale delete must fail");
        assert_eq!(stale_delete.details["kind"], "stale_agent_profile_revision");
        service.delete(&updated.profile_id, 2).await?;
        assert!(service.list().await?.is_empty());
        Ok(())
    }

    /// 功能：验证并发完整替换时 list 不会拼接不同 revision 的主行与工具集合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn concurrent_list_observes_atomic_profile_and_tools()
    -> Result<(), Box<dyn std::error::Error>> {
        let directory = tempdir()?;
        let configuration = DatabaseConfiguration::sqlite_default(directory.path());
        let database = connect_application_database(&configuration).await?;
        let service = AgentProfileService::new(database);
        let created = service.create(profile_input("read-profile")).await?;
        let profile_id = created.profile_id.clone();
        let writer = service.clone();
        let reader = service.clone();
        let write_task = async move {
            let mut revision = 1_u64;
            for index in 0..64 {
                let read_profile = index % 2 == 0;
                let mut input = profile_input(if read_profile {
                    "read-profile"
                } else {
                    "write-profile"
                });
                input.requested_tool_ids = vec![if read_profile {
                    "file.read".to_owned()
                } else {
                    "file.write".to_owned()
                }];
                let updated = writer.update(&profile_id, revision, input).await?;
                revision = updated.revision;
                tokio::task::yield_now().await;
            }
            Ok::<(), AgentError>(())
        };
        let read_task = async move {
            for _ in 0..256 {
                let profiles = reader.list().await?;
                let profile = profiles.first().ok_or_else(corrupted_profile)?;
                match profile.display_name.as_str() {
                    "read-profile" => assert_eq!(profile.requested_tool_ids, ["file.read"]),
                    "write-profile" => assert_eq!(profile.requested_tool_ids, ["file.write"]),
                    _ => return Err(corrupted_profile()),
                }
                tokio::task::yield_now().await;
            }
            Ok::<(), AgentError>(())
        };
        let (written, read) = tokio::join!(write_task, read_task);
        written?;
        read?;
        Ok(())
    }

    /// 功能：验证 Profile run 必须显式携带完整 apiFamily，faux 也不隐式补全。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn run_binding_rejects_missing_api_family() -> Result<(), Box<dyn std::error::Error>> {
        let directory = tempdir()?;
        let configuration = DatabaseConfiguration::sqlite_default(directory.path());
        let service = AgentProfileService::new(connect_application_database(&configuration).await?);
        let profile = service.create(profile_input("reviewer")).await?;
        let error = service
            .resolve_for_run(
                &AgentProfileReference {
                    profile_id: profile.profile_id,
                    revision: profile.revision,
                },
                &ProviderSelection {
                    id: "faux".to_owned(),
                    model_id: "faux-v1".to_owned(),
                    api_family: None,
                    extensions: Default::default(),
                },
                &["file.read".to_owned()],
                &["file.read".to_owned()],
            )
            .await
            .expect_err("apiFamily must be explicit for Profile binding");
        assert_eq!(error.details["kind"], "agent_profile_model_mismatch");
        Ok(())
    }

    /// 功能：验证损坏的非 UTC 数据库时间不会投影到 application-service wire。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn list_rejects_non_utc_persisted_timestamp() -> Result<(), Box<dyn std::error::Error>> {
        let directory = tempdir()?;
        let configuration = DatabaseConfiguration::sqlite_default(directory.path());
        let database = connect_application_database(&configuration).await?;
        let service = AgentProfileService::new(database.clone());
        let profile = service.create(profile_input("reviewer")).await?;
        database
            .execute(Statement::from_sql_and_values(
                DatabaseBackend::Sqlite,
                "UPDATE agent_profiles SET updated_at=? WHERE profile_id=?",
                [
                    "2026-07-21T08:00:00+08:00".into(),
                    profile.profile_id.into(),
                ],
            ))
            .await?;
        let error = service
            .list()
            .await
            .expect_err("non-Z timestamp must be rejected");
        assert_eq!(error.details["kind"], "agent_profile_storage_corrupt");
        Ok(())
    }

    /// 功能：验证最大安全 revision 的 update 在写入前失败且数据库完全不变。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn update_rejects_revision_overflow_without_modification()
    -> Result<(), Box<dyn std::error::Error>> {
        let directory = tempdir()?;
        let configuration = DatabaseConfiguration::sqlite_default(directory.path());
        let database = connect_application_database(&configuration).await?;
        let service = AgentProfileService::new(database.clone());
        let profile = service.create(profile_input("boundary")).await?;
        database
            .execute(Statement::from_sql_and_values(
                DatabaseBackend::Sqlite,
                "UPDATE agent_profiles SET revision=? WHERE profile_id=?",
                [
                    i64::try_from(MAX_SAFE_INTEGER)?.into(),
                    profile.profile_id.clone().into(),
                ],
            ))
            .await?;
        let before = service.list().await?;
        let mut replacement = profile_input("must-not-write");
        replacement.requested_tool_ids = vec!["file.write".to_owned()];
        let error = service
            .update(&profile.profile_id, MAX_SAFE_INTEGER, replacement)
            .await
            .expect_err("next revision outside the safe integer range must fail");
        assert_eq!(error.code, crate::error::ErrorCode::OutputLimitExceeded);
        assert_eq!(error.code.rpc_code(), -32009);
        assert_eq!(error.message, "agent profile revision is exhausted");
        assert!(!error.retryable);
        assert_eq!(
            error.details,
            serde_json::json!({
                "kind":"agent_profile_revision_exhausted",
                "resourceId":profile.profile_id
            })
        );
        assert_eq!(service.list().await?, before);
        assert_eq!(before[0].revision, MAX_SAFE_INTEGER);
        assert_eq!(before[0].requested_tool_ids, ["file.read"]);
        Ok(())
    }

    /// 功能：验证被篡改的持久化 Profile ID 不会投影到 application-service wire。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn list_rejects_invalid_persisted_profile_id() -> Result<(), Box<dyn std::error::Error>> {
        let directory = tempdir()?;
        let configuration = DatabaseConfiguration::sqlite_default(directory.path());
        let database = connect_application_database(&configuration).await?;
        let service = AgentProfileService::new(database.clone());
        let mut input = profile_input("reviewer");
        input.requested_tool_ids.clear();
        let profile = service.create(input).await?;
        database
            .execute(Statement::from_sql_and_values(
                DatabaseBackend::Sqlite,
                "UPDATE agent_profiles SET profile_id=? WHERE profile_id=?",
                ["../escaped".into(), profile.profile_id.into()],
            ))
            .await?;
        let error = service
            .list()
            .await
            .expect_err("invalid persisted Profile ID must be rejected");
        assert_eq!(error.details["kind"], "agent_profile_storage_corrupt");
        Ok(())
    }

    /// 功能：验证普通 CAS miss 遇到损坏 revision 时不会生成越界 stale wire 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn cas_miss_rejects_corrupt_persisted_revision() -> Result<(), Box<dyn std::error::Error>>
    {
        let directory = tempdir()?;
        let configuration = DatabaseConfiguration::sqlite_default(directory.path());
        let database = connect_application_database(&configuration).await?;
        let service = AgentProfileService::new(database.clone());
        let profile = service.create(profile_input("corrupt-revision")).await?;
        let pool = database.get_sqlite_connection_pool();
        let mut connection = pool.acquire().await?;
        sea_orm::sqlx::query("PRAGMA ignore_check_constraints=ON")
            .execute(&mut *connection)
            .await?;
        sea_orm::sqlx::query("UPDATE agent_profiles SET revision=0 WHERE profile_id=?")
            .bind(&profile.profile_id)
            .execute(&mut *connection)
            .await?;
        sea_orm::sqlx::query("PRAGMA ignore_check_constraints=OFF")
            .execute(&mut *connection)
            .await?;
        drop(connection);

        let update_error = service
            .update(&profile.profile_id, 1, profile_input("replacement"))
            .await
            .expect_err("corrupt current revision must not be reported as stale");
        assert_eq!(
            update_error.details["kind"],
            "agent_profile_storage_corrupt"
        );
        let delete_error = service
            .delete(&profile.profile_id, 1)
            .await
            .expect_err("delete CAS miss must reject corrupt current revision");
        assert_eq!(
            delete_error.details["kind"],
            "agent_profile_storage_corrupt"
        );
        Ok(())
    }

    /// 功能：验证 list 不会静默忽略绕过外键写入的孤儿 Profile 工具行。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[tokio::test]
    async fn list_rejects_orphan_persisted_tool_row() -> Result<(), Box<dyn std::error::Error>> {
        let directory = tempdir()?;
        let configuration = DatabaseConfiguration::sqlite_default(directory.path());
        let database = connect_application_database(&configuration).await?;
        let service = AgentProfileService::new(database.clone());
        let _ = service.create(profile_input("reviewer")).await?;
        let pool = database.get_sqlite_connection_pool();
        let mut connection = pool.acquire().await?;
        sea_orm::sqlx::query("PRAGMA foreign_keys=OFF")
            .execute(&mut *connection)
            .await?;
        sea_orm::sqlx::query(
            "INSERT INTO agent_profile_tools(profile_id,tool_id) VALUES('agent-orphan','file.read')",
        )
        .execute(&mut *connection)
        .await?;
        sea_orm::sqlx::query("PRAGMA foreign_keys=ON")
            .execute(&mut *connection)
            .await?;
        drop(connection);

        let error = service
            .list()
            .await
            .expect_err("orphan tool rows must be treated as storage corruption");
        assert_eq!(error.details["kind"], "agent_profile_storage_corrupt");
        Ok(())
    }

    /// 功能：验证重复工具和未知闭合字段在进入数据库前失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn validation_rejects_duplicate_tools_and_unknown_fields() {
        let mut input = profile_input("测试");
        input.requested_tool_ids.push("file.read".to_owned());
        assert!(input.validate_and_normalize().is_err());
        assert!(
            serde_json::from_value::<AgentProfileInput>(serde_json::json!({
                "displayName":"测试",
                "description":"",
                "enabled":true,
                "instructions":"test",
                "model":{"providerId":"faux","modelId":"faux-v1","apiFamily":"faux"},
                "requestedToolIds":[],
                "dangerousActionMode":"ask",
                "behavior":{"responseStyle":"balanced","planFirst":true,"reviewChanges":true},
                "secret":"must-not-be-accepted"
            }))
            .is_err()
        );

        let mut digit_prefixed_identity = profile_input("数字路由");
        digit_prefixed_identity.model.provider_id = "1provider".to_owned();
        digit_prefixed_identity.model.api_family = "2family".to_owned();
        assert!(digit_prefixed_identity.validate_and_normalize().is_ok());
    }
}
