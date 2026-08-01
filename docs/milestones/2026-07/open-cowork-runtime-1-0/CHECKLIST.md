# OpenCoWork Runtime 1.0 Checklist

This file is the progress ledger for the 2026-07 OpenCoWork Runtime 1.0 milestone.

Milestone standard: [README.md](README.md)

## Progress Summary

- Status: In Progress
- Progress: 7/12
- Done: 7
- In progress: 4
- Not started: 1
- Deferred: 0
- Split: 0

## Checklist

- [x] 1. M0 - Contract Freeze
  - Status: Done
  - Related spec: docs/superpowers/specs/2026-07-25-open-cowork-m0-contract-freeze-design.md
  - Related plan: Not applicable: M0 is a contract-only slice with no implementation work.
  - Related archive: Not applicable: the three frozen M0 specs are the authoritative delivery evidence.
  - Completion signal: 品牌、命名、程序集、配置、OpenCoWork Wire、存储与状态契约已冻结；78 项能力均有确定去向；2026-08-01 已按用户决策追加 M9 Provider 验收且未重排既有 Acceptance ID。
- [x] 2. M1 - Runtime Foundation
  - Status: Done
  - Related spec: docs/superpowers/specs/2026-07-25-open-cowork-m1-runtime-foundation-design.md
  - Related plan: docs/superpowers/plans/2026-07-25-open-cowork-m1-runtime-foundation-implementation-plan.md
  - Related archive: docs/superpowers/archives/2026-07/2026-07-25-open-cowork-m1-runtime-foundation-archives.md
  - Completion signal: 运行时骨架、初始化、诊断、宿主选择和生命周期已通过 Windows 与 Apple Silicon macOS 开发基线验收；M11 仍须重跑最终发布候选。
- [x] 3. M2 - Durable Session Core
  - Status: Done
  - Related spec: docs/superpowers/specs/2026-07-26-open-cowork-m2-durable-session-core-design.md
  - Related plan: docs/superpowers/plans/2026-07-26-open-cowork-m2-durable-session-core-implementation-plan.md
  - Related archive: docs/superpowers/archives/2026-07/2026-07-26-open-cowork-m2-durable-session-core-archives.md
  - Completion signal: Journal 重放、投影重建、并发、等待、取消、队列和管理恢复已通过 Windows 与 Apple Silicon macOS 开发基线验收；M11 仍须重跑最终发布候选。
- [x] 4. M3 - Agent Runtime Alpha
  - Status: Done
  - Related spec: docs/superpowers/specs/2026-07-27-open-cowork-m3-agent-runtime-alpha-design.md
  - Related plan: docs/superpowers/plans/2026-07-27-open-cowork-m3-agent-runtime-alpha-implementation-plan.md
  - Related archive: docs/superpowers/archives/2026-07/2026-07-27-open-cowork-m3-agent-runtime-alpha-archives.md
  - Completion signal: 真实多轮对话、重启恢复、流式重试和上下文压缩通过完整离线回归；DeepSeek 官方 Pro/Flash 在 osx-arm64 通过真实冒烟，其他 Provider 已登记延期。
- [x] 5. M4 - Tool Runtime Alpha
  - Status: Done
  - Related spec: docs/superpowers/specs/2026-07-28-open-cowork-m4-tool-runtime-alpha-design.md
  - Related plan: docs/superpowers/plans/2026-07-28-open-cowork-m4-tool-runtime-alpha-implementation-plan.md
  - Related archive: docs/superpowers/archives/2026-07/2026-07-28-open-cowork-m4-tool-runtime-alpha-archives.md
  - Completion signal: 固定工具管线、稳定错误、审批恢复、模式限制和副作用保护已完成；win-x64、osx-arm64 真机 File/Shell/Web 与进程树证据均已通过，M11 仍须重跑最终发布候选。
- [x] 6. M5 - OpenCoWork Wire Alpha
  - Status: Done
  - Related spec: docs/superpowers/specs/2026-07-28-open-cowork-m5-wire-alpha-design.md
  - Related plan: docs/superpowers/plans/2026-07-28-open-cowork-m5-wire-alpha-implementation-plan.md
  - Related archive: docs/superpowers/archives/2026-07/2026-07-28-open-cowork-m5-wire-alpha-archives.md
  - Completion signal: Desktop-first stdio/loopback WebSocket Wire、核心会话流与稳定 ACP v1 已通过 win-x64、osx-arm64 发布目录 TestClient 真机验收；M5-ACC-001 至 M5-ACC-009 全部 Passed，M11 仍须重跑最终发布候选。
- [x] 7. M6 - Capability Ecosystem
  - Status: Done
  - Related spec: docs/superpowers/specs/2026-07-29-open-cowork-m6-capability-ecosystem-design.md
  - Related plan: docs/superpowers/plans/2026-07-29-open-cowork-m6-capability-ecosystem-implementation-plan.md
  - Related archive: docs/superpowers/archives/2026-07/2026-07-29-open-cowork-m6-capability-ecosystem-archives.md
  - Completion signal: Capability Catalog、插件与 Skill、Provider/Auth、MCP/LSP、动态与延迟工具、Hook、Git/Terminal/Memory 和 Wire 1.1 已通过 win-x64、osx-arm64 发布目录真机验收；M6-ACC-001 至 M6-ACC-010 全部 Passed；通用 Provider 声明路径将在 M9 被 DeepSeek-only 契约取代，M11 仍须重跑最终发布候选。
- [ ] 8. M7 - Multi-Agent CoWork
  - Status: In Progress
  - Related spec: docs/superpowers/specs/2026-07-30-open-cowork-m7-multi-agent-cowork-design.md
  - Related plan: docs/superpowers/plans/2026-07-30-open-cowork-m7-multi-agent-cowork-implementation-plan.md
  - Related archive: None yet.
  - Completion signal: Outcome 1-9 已完成，Outcome 10 的自动化、双 RID 交叉发布和 `osx-arm64` 真机验收已通过；`M7-ACC-001..005`、`008..010` 为 Passed，`006..007` 与完整 M7 等待 `win-x64` 真机。
- [ ] 9. M8 - Automations and Scheduler
  - Status: In Progress
  - Related spec: docs/superpowers/specs/2026-07-30-open-cowork-m8-automations-scheduler-design.md
  - Related plan: docs/superpowers/plans/2026-07-30-open-cowork-m8-automations-scheduler-implementation-plan.md
  - Related archive: None yet.
  - Completion signal: Outcome 1-9 与 Outcome 10 自动化、双 RID 交叉发布、osx-arm64 真机验收已通过；M8-ACC-001..002、004..007、009 为 Passed，003、008 与完整 M8 等待 win-x64 真机。
- [ ] 10. M9 - DeepSeek Responses Provider
  - Status: In Progress
  - Related spec: docs/superpowers/specs/2026-08-01-open-cowork-m9-deepseek-responses-provider-design.md (Design Freeze; 2026-08-01 用户确认)
  - Related plan: docs/superpowers/plans/2026-08-01-open-cowork-m9-deepseek-responses-provider-implementation-plan.md (In Progress; Gate 0 and Outcomes 1-9 complete; Outcome 10 pending)
  - Related archive: None yet.
  - Completion signal: 通用 OpenAI-compatible Chat Completions 与千问 Token Plan 路径已由 DeepSeek-only 实现取代；`M9-ACC-011..017` 已通过离线验收，`osx-arm64` 发布目录 Protocol TestClient 与真实 DeepSeek 六场景已在 `058b505` 通过；`M9-ACC-018..019` 及完整 M9 仍等待 `win-x64` 真机关闭；`deepseek-v4-pro` 等官方支持后再激活。
- [ ] 11. M10 - Gateway and Operations
  - Status: In Progress
  - Related spec: docs/superpowers/specs/2026-08-01-open-cowork-m10-gateway-operations-design.md (Design Freeze; 2026-08-01 用户确认)
  - Related plan: docs/superpowers/plans/2026-08-01-open-cowork-m10-gateway-operations-implementation-plan.md (In Progress; Gate 0 and Outcomes 1-9 complete)
  - Related archive: None yet.
  - Completion signal: Outcomes 1-9 已完成 Gateway 契约/主宿主、State v9、Loopback Webhook HMAC、严格 Envelope、内容寻址媒体、Inbound/Outbox、持久 Correlation、Operations/Hub/Insight、Wire 1.4、CLI/TestClient，以及 25,600 Inbound、10,000 Outbox、100,000 Span、10,000 Usage、1,000 Proposal 的固定负载分页与全部离线回归；双平台发布目录真机验收待 Outcome 10。
- [ ] 12. M11 - OpenCoWork 1.0 Closure
  - Status: Not Started
  - Related spec: docs/superpowers/specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md
  - Related plan: None yet.
  - Related archive: None yet.
  - Completion signal: 能力台账清零且 win-x64、osx-arm64 发布候选通过完整验收。

## Update Rules

- Keep this file focused on milestone progress.
- Do not add task-level implementation steps here.
- Update the summary counts whenever any slice status changes.
- Count every allowed slice status: `Done`, `In Progress`, `Not Started`, `Deferred`, and `Split`.
- Update `docs/milestones/INDEX.md` whenever this milestone status or progress changes.
