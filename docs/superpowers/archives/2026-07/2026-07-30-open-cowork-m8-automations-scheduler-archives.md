# OpenCoWork M8 Automations and Scheduler

- Date: `2026-07-30`
- Topic slug: `open-cowork-m8-automations-scheduler`
- Status: `Archived`
- Scope: `Feature`
- Tags: `automation`, `scheduler`, `cron`, `recovery`, `wire`

## Summary

M8 在既有 Session、Tool、Capability、Worktree 与 State 权威边界上交付了安全、
可版本控制、可恢复的无人值守 Automation 运行闭环。

## Delivered Scope

- 交付严格 YAML、受限 Fluid 输入、Manual/Cron、IANA 时区、DST 与稳定定义版本。
- 交付冻结 Run Snapshot、并发/互斥/Lease、NeedsAttention、取消、超时与崩溃恢复。
- 交付 Prepared Turn、Managed Worktree、State v7 与 OpenCoWork Wire 1.3。
- `win-x64` 与 `osx-arm64` 发布目录均通过 M8 真机验收。

## Out of Scope

- Gateway、通用工作流引擎、任意脚本调度和每个 Automation 独立 Provider 配置。
- 绕过 Workspace Trust、Unattended Policy 或 Tool Authority 的后台审批。

## Verification Snapshot

- Apple Silicon macOS 基线 `a710866ec2f812dce3bb03a72d5723ac72e68427`：
  Release build `0` warning / `0` error，`536` 项回归、`100` 项 M8 专项、固定负载及
  发布目录 Wire 1.3、DST、热更新、恢复、Worktree 和 Secret Canary 通过。
- Windows 11 Home `10.0.26200` x64 基线
  `2d966400e61e8d17c8a513299e8a9b420591d865` 加 Source/Test Patch SHA-256
  `516c263191620d8b9f41eb5bbce0436aac41ee04aef6be73af5c5514783e90cd`：
  Release build `0` warning / `0` error，全量非显式 Integration 串行回归和发布目录
  TestClient 的 Wire 1.3、Schedule/Run/Notification、恢复、Secret Canary 与残留检查通过。
- `M8-ACC-001` 至 `M8-ACC-009` 全部为 `Passed`；M11 仍须在最终发布候选上复验。

## Source Documents

- Spec: [M8 Automations and Scheduler 详细设计](../../specs/2026-07-30-open-cowork-m8-automations-scheduler-design.md)
- Visual: None found for this topic.
- Plan: [M8 Automations and Scheduler 实施计划](../../plans/2026-07-30-open-cowork-m8-automations-scheduler-implementation-plan.md)

## Related Problems

- [Windows 验证暴露隐藏的平台测试假设](../../problems/2026-07/2026-07-29-windows-cross-platform-test-assumptions-problem.md)

## Notes

- 双平台环境、发布物摘要与发布目录场景见
  [平台发布验证台账](../../../platform-release-validation-ledger.md)。
