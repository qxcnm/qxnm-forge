# ADR 0001：Rust/.NET 两个 active 原生实现

- Status: Accepted
- Date: 2026-07-10
- Project: qxnm-forge
- Author: 高宏顺 `<18272669457@163.com>`

## Context

The project needs equivalent active Agent products for .NET and Rust. Reusing one language's runtime through FFI, subprocesses, or a
shared executable would simplify early delivery but make installation,
cancellation, security, debugging, and ecosystem APIs depend on a foreign
runtime. It would also turn reference implementation accidents into the
standard.

## Decision

Build two complete active native implementations. They share only language-neutral
specifications, schemas, manifests, fixtures, golden traces, and black-box
conformance tooling. Each owns native provider transports/authentication,
agent/session/tool/executor behavior, daemon, and CLI. Rust is the first
reference and .NET the second independent challenge, but neither outranks the
specification.

Go、TypeScript、Python 自 2026-07-18 起冻结为 legacy snapshots，不再新增功能，不进入
vNext 默认能力矩阵、构建或发布门禁。五种历史 `createdBy.language` 和五种目标项目语言
profile 继续兼容；这不等于三套 legacy runtime 获得 vNext 支持承诺。

Every new source method follows the documented method-level comment rule with
functional description, author 高宏顺, and email `18272669457@163.com`.

## Consequences

- 双实现仍需要独立实现和共同黑盒门禁，但产品交付面显著收窄。
- Each package installs and runs without PI or another qxnm-forge runtime.
- Platform-native process, cancellation, async, and credential behavior can be
  implemented correctly.
- A behavior found in one implementation must be specified before portability
  is claimed.
