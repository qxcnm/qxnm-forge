//! qxnm-forge 的独立 Rust Agent 底座实现。
//!
//! 作者：高宏顺 <18272669457@163.com>

pub mod agent;
pub mod commercial_state;
pub mod daemon;
pub mod domain;
pub mod error;
pub mod executor;
pub mod hard_sandbox;
mod path_boundary;
pub mod policy;
pub mod protocol;
pub mod provider;
pub mod provider_identity;
pub mod provider_route;
pub mod session;
pub mod sponsored_catalog;
pub mod storage;
pub mod terminal;
pub mod tools;

pub use agent::{Agent, RunRequest};
pub use error::{AgentError, ErrorCode};
