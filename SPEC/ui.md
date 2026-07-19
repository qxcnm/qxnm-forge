# TypeScript + React UI 边界规范 v0.1

状态：Draft  
作者：高宏顺 `<18272669457@163.com>`

## 决策

未来 QXNM Forge Web/Desktop UI 使用 TypeScript + React。UI 是客户端，不是第三套 Agent runtime：它 MUST 通过品牌中立 JSON-RPC/HTTP application service 使用 Rust 或 .NET 后端，MUST NOT 直接读取 SQLite、portable Session、CredentialStore 或许可证文件。

Open UI 代码未来放在独立公开仓库或本发行的 `ui/`；Pro UI 可以增加收费页面和 entitlement 展示，但公共组件、wire DTO 与主题 token 保持兼容。当前 v0.1 只冻结边界，不包含可运行前端。

## Application service

UI transport 可以是 loopback HTTP + WebSocket，也可以桥接现有 NDJSON JSON-RPC，但必须复用同一方法和事件语义。后端负责：

- Agent/Session 状态机、Provider 调用和工具审批；
- credential、数据库和许可证访问；
- 路径边界、项目 trust、取消和进程清理；
- 把用户可见错误脱敏后映射为稳定 wire error。

UI 只维护可丢弃的视图状态。刷新或重连后必须能从 application service 重建 transcript、pending approval、queue、usage、retry、compaction 和 entitlement 投影。

## 高定制化

前端应以设计 token、主题包、布局槽位和 renderer registry 提供定制能力。自定义 renderer 只能处理经过 schema 验证的 view model；不得取得 secret、宿主文件句柄或任意后端对象。新增 command、tool 或 Provider 必须经过后端 extension 权限协议注册，不能由 React 插件绕开审批。

推荐技术基线：TypeScript strict mode、React、Vite、TanStack Query、Zustand（仅视图状态）、基于生成 schema type 的 API client。ORM 不属于前端依赖。

## Open/Pro 与部署

Community 后端返回公开 capability 集；Pro 后端在授权服务验证后追加 entitlement，不让 UI 自行计算试用时间或验证卡密。任何广告/推广位必须标注“推广/返佣”，远程目录只能改变候选内容，不能注入脚本、HTML 或自动启用 Provider。

浏览器部署必须增加 origin、CSRF、authentication 和 WebSocket session 绑定；在这些规则和共同 trace 未完成前，daemon 不得直接监听公网地址。

