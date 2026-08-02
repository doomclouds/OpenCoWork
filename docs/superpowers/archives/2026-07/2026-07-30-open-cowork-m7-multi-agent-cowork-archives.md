# OpenCoWork M7 Multi-Agent CoWork

- Date: `2026-07-30`
- Topic slug: `open-cowork-m7-multi-agent-cowork`
- Status: `Archived`
- Scope: `Feature`
- Tags: `multi-agent`, `mission`, `mailbox`, `worktree`, `wire`

## Summary

M7 在既有 Session、Agent、Tool、Capability 与 Wire 权威边界上交付了可持久、
可恢复、受预算与权限约束的 Direct SubAgent 和 Mission 协作闭环。

## Delivered Scope

- 交付 Agent Profile、Team、MissionTask DAG、Review/Rework、Leader Synthesis 与
  Origin 单次回传。
- 交付持久 Mailbox、不可变 Artifact、私有 Scratchpad、预算/并发/成员互斥与崩溃恢复。
- 交付 Managed Git Worktree、Dirty Retention 与 OpenCoWork Wire 1.2。
- `win-x64` 与 `osx-arm64` 发布目录均通过 M7 真机验收。

## Out of Scope

- Automation、Gateway、通用消息总线和新的 Provider 兼容性声明。
- 未纳入 M7 冻结边界的共享 Scratchpad、任意命令编排或跨 Workspace Mission。

## Verification Snapshot

- Apple Silicon macOS 基线 `c30f168a7c01a39915662453799427e749c8eacf`：
  Release build `0` warning / `0` error，`446` 项回归及发布目录 Wire 1.2、
  DAG、Mailbox、Artifact、Symlink、Worktree、恢复和 Secret Canary 通过。
- Windows 11 Home `10.0.26200` x64 基线
  `2d966400e61e8d17c8a513299e8a9b420591d865` 加 Source/Test Patch SHA-256
  `516c263191620d8b9f41eb5bbce0436aac41ee04aef6be73af5c5514783e90cd`：
  Release build `0` warning / `0` error，全量非显式 Integration 串行回归和发布目录
  TestClient 的 Wire 1.2、Git/Worktree、Secret Canary 与残留检查通过。
- `M7-ACC-001` 至 `M7-ACC-010` 全部为 `Passed`；M11 仍须在最终发布候选上复验。

## Source Documents

- Spec: [M7 Multi-Agent CoWork 详细设计](../../specs/2026-07-30-open-cowork-m7-multi-agent-cowork-design.md)
- Visual: None found for this topic.
- Plan: [M7 Multi-Agent CoWork 实施计划](../../plans/2026-07-30-open-cowork-m7-multi-agent-cowork-implementation-plan.md)

## Related Problems

- [Windows 验证暴露隐藏的平台测试假设](../../problems/2026-07/2026-07-29-windows-cross-platform-test-assumptions-problem.md)

## Notes

- 双平台环境、发布物摘要与发布目录场景见
  [平台发布验证台账](../../../platform-release-validation-ledger.md)。
