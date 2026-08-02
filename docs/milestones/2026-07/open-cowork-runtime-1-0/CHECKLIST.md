# OpenCoWork Runtime 1.0 Checklist

This file is the progress ledger for the 2026-07 OpenCoWork Runtime 1.0 milestone.

Milestone standard: [README.md](README.md)

## Progress Summary

- Status: In Progress
- Progress: 11/12
- Done: 11
- In progress: 1
- Not started: 0
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
- [x] 8. M7 - Multi-Agent CoWork
  - Status: Done
  - Related spec: docs/superpowers/specs/2026-07-30-open-cowork-m7-multi-agent-cowork-design.md
  - Related plan: docs/superpowers/plans/2026-07-30-open-cowork-m7-multi-agent-cowork-implementation-plan.md
  - Related archive: docs/superpowers/archives/2026-07/2026-07-30-open-cowork-m7-multi-agent-cowork-archives.md
  - Completion signal: Direct SubAgent、Mission DAG、Mailbox、Artifact、Managed Worktree 与 Wire 1.2 已通过 win-x64、osx-arm64 发布目录真机验收；M7-ACC-001 至 M7-ACC-010 全部 Passed，M11 仍须重跑最终发布候选。
- [x] 9. M8 - Automations and Scheduler
  - Status: Done
  - Related spec: docs/superpowers/specs/2026-07-30-open-cowork-m8-automations-scheduler-design.md
  - Related plan: docs/superpowers/plans/2026-07-30-open-cowork-m8-automations-scheduler-implementation-plan.md
  - Related archive: docs/superpowers/archives/2026-07/2026-07-30-open-cowork-m8-automations-scheduler-archives.md
  - Completion signal: 严格 YAML/Fluid/Cron、持久 Run/Lease/NeedsAttention 恢复、Managed Worktree 与 Wire 1.3 已通过 win-x64、osx-arm64 发布目录真机验收；M8-ACC-001 至 M8-ACC-009 全部 Passed，M11 仍须重跑最终发布候选。
- [x] 10. M9 - DeepSeek Responses Provider
  - Status: Done
  - Related spec: docs/superpowers/specs/2026-08-01-open-cowork-m9-deepseek-responses-provider-design.md
  - Related plan: docs/superpowers/plans/2026-08-01-open-cowork-m9-deepseek-responses-provider-implementation-plan.md
  - Related archive: docs/superpowers/archives/2026-08/2026-08-01-open-cowork-m9-deepseek-responses-provider-archives.md
  - Completion signal: DeepSeek-only Responses 实现已在 win-x64、osx-arm64 发布目录以真实 deepseek-v4-flash 完成 Text、Function、web_search、custom/apply_patch、Usage 与 Secret Canary 六场景；M9-ACC-011 至 M9-ACC-019 全部 Passed，M11 仍须重跑最终发布候选。
- [x] 11. M10 - Gateway and Operations
  - Status: Done
  - Related spec: docs/superpowers/specs/2026-08-01-open-cowork-m10-gateway-operations-design.md
  - Related plan: docs/superpowers/plans/2026-08-01-open-cowork-m10-gateway-operations-implementation-plan.md
  - Related archive: docs/superpowers/archives/2026-08/2026-08-01-open-cowork-m10-gateway-operations-archives.md
  - Completion signal: Gateway、Inbound/Outbox、Hub、Operations、Wire 1.4 与 State v9 已通过 win-x64、osx-arm64 发布目录 Protocol 8 场景、Runner 13 项、OS Secret、Secret Canary 和残留检查；M9-ACC-001 至 M9-ACC-010 全部 Passed，M11 仍须重跑最终发布候选。
- [ ] 12. M11 - OpenCoWork 1.0 Closure
  - Status: In Progress
  - Related spec: docs/superpowers/specs/2026-08-02-open-cowork-m11-runtime-1-0-closure-design.md
  - Related plan: docs/superpowers/plans/2026-08-02-open-cowork-m11-runtime-1-0-closure-implementation-plan.md
  - Related archive: None yet.
  - Completion signal: M11 设计与实施 Outcomes 1-5 已完成；`osx-arm64` rc.1 的离线回归、安装、固定负载和两小时 Soak 已通过，真实 DeepSeek/OS Secret 按用户决策移至未来客户端阶段，因此 macOS 平台已关闭；`win-x64` 只有交叉包，真机 RC、`1.0.0` 晋升和交付归档仍待完成。

## Update Rules

- Keep this file focused on milestone progress.
- Do not add task-level implementation steps here.
- Update the summary counts whenever any slice status changes.
- Count every allowed slice status: `Done`, `In Progress`, `Not Started`, `Deferred`, and `Split`.
- Update `docs/milestones/INDEX.md` whenever this milestone status or progress changes.
