# OpenCoWork Runtime 1.0 Checklist

This file is the progress ledger for the 2026-07 OpenCoWork Runtime 1.0 milestone.

Milestone standard: [README.md](README.md)

## Progress Summary

- Status: In Progress
- Progress: 1/11
- Done: 1
- In progress: 1
- Not started: 9
- Deferred: 0
- Split: 0

## Checklist

- [x] 1. M0 - Contract Freeze
  - Status: Done
  - Related spec: docs/superpowers/specs/2026-07-25-open-cowork-m0-contract-freeze-design.md
  - Related plan: Not applicable: M0 is a contract-only slice with no implementation work.
  - Related archive: Not applicable: the three frozen M0 specs are the authoritative delivery evidence.
  - Completion signal: 品牌、命名、程序集、配置、OpenCoWork Wire、存储与状态契约已冻结；78 项能力均有确定去向，M0-M10 共 104 个验收编号已建立。
- [ ] 2. M1 - Runtime Foundation
  - Status: In Progress
  - Related spec: docs/superpowers/specs/2026-07-25-open-cowork-m1-01-solution-build-baseline-design.md
  - Related plan: docs/superpowers/plans/2026-07-25-open-cowork-m1-01-solution-build-baseline-implementation-plan.md
  - Related archive: docs/superpowers/archives/2026-07/2026-07-25-open-cowork-m1-01-solution-build-baseline-archives.md
  - Completion signal: 运行时骨架、初始化、诊断、宿主选择和生命周期验收通过。
- [ ] 3. M2 - Durable Session Core
  - Status: Not Started
  - Related spec: docs/superpowers/specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md
  - Related plan: None yet.
  - Related archive: None yet.
  - Completion signal: Journal 重放、投影重建、并发、等待、取消和恢复验收通过。
- [ ] 4. M3 - Agent Runtime Alpha
  - Status: Not Started
  - Related spec: docs/superpowers/specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md
  - Related plan: None yet.
  - Related archive: None yet.
  - Completion signal: 真实多轮对话、重启恢复、流重试和上下文压缩验收通过。
- [ ] 5. M4 - Tool Runtime Alpha
  - Status: Not Started
  - Related spec: docs/superpowers/specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md
  - Related plan: None yet.
  - Related archive: None yet.
  - Completion signal: 固定工具管线、稳定错误、审批、模式限制和副作用保护验收通过。
- [ ] 6. M5 - OpenCoWork Wire Alpha
  - Status: Not Started
  - Related spec: docs/superpowers/specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md
  - Related plan: None yet.
  - Related archive: None yet.
  - Completion signal: 核心 JSON-RPC 与 ACP 端到端契约验收通过。
- [ ] 7. M6 - Capability Ecosystem
  - Status: Not Started
  - Related spec: docs/superpowers/specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md
  - Related plan: None yet.
  - Related archive: None yet.
  - Completion signal: 插件信任、MCP/LSP 生命周期、动态工具和冲突隔离验收通过。
- [ ] 8. M7 - Multi-Agent CoWork
  - Status: Not Started
  - Related spec: docs/superpowers/specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md
  - Related plan: None yet.
  - Related archive: None yet.
  - Completion signal: Mission DAG、Mailbox、Worktree、恢复、综合和单次回传验收通过。
- [ ] 9. M8 - Automations and Scheduler
  - Status: Not Started
  - Related spec: docs/superpowers/specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md
  - Related plan: None yet.
  - Related archive: None yet.
  - Completion signal: 去重调度、时区、权限、NeedsAttention 和崩溃恢复验收通过。
- [ ] 10. M9 - Gateway and Operations
  - Status: Not Started
  - Related spec: docs/superpowers/specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md
  - Related plan: None yet.
  - Related archive: None yet.
  - Completion signal: 入站去重、Outbox、Channel 隔离、Hub 和后台生命周期验收通过。
- [ ] 11. M10 - OpenCoWork 1.0 Closure
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
