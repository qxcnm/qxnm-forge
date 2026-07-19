# QXNM Forge UI（架构预留）

这里预留未来的 TypeScript + React Community UI。目前不包含可运行前端，也没有恢复旧 TypeScript Agent 实现。

计划基线：TypeScript strict mode、React、Vite、TanStack Query，以及只保存可丢弃视图状态的 Zustand。UI 使用生成的品牌中立 JSON-RPC/HTTP client 连接 Rust 或 .NET application service，不直接访问 SQLite、Session journal、CredentialStore 或工具执行器。

定制入口将包括设计 token、浅色/深色主题、布局槽位、消息/工具 renderer registry、命令面板和国际化。自定义前端代码不获得 Provider secret、host path 或审批绕过能力。

正式实现前需先完成 [`SPEC/ui.md`](../SPEC/ui.md) 中的 loopback HTTP/WebSocket authentication、origin、CSRF、重连和 event replay 规范。

