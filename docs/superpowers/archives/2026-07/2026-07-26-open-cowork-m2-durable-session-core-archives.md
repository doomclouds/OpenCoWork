# OpenCoWork M2 Durable Session Core

- Date: `2026-07-26`
- Topic slug: `open-cowork-m2-durable-session-core`
- Status: `Archived`
- Scope: `Feature`
- Tags: `session`, `journal`, `SQLite`, `recovery`, `lifecycle`
- Implementation commit: `a99f8aa61bd541eee3a0b386b9b398ac15ddbb91`

## Summary

M2 交付了以 `ThreadJournal` 为唯一权威事实源、SQLite 为可重建查询投影的
Thread-Turn-Item Session Core，并把它作为独立 `session` 模块接入
`WorkspaceRuntime`。同一 Thread 串行、不同 Thread 并行，等待、队列、管理操作和
进程中断都有确定的持久化与恢复结果。

## Delivered Scope

- 完成 `ISessionService`、Thread/Turn/Item 状态机、全局幂等、Sequence Gate、查询和
  `SessionEventChannel`。
- 完成 canonical JSONL Journal、每次提交 Flush、Checksum/Sequence、尾部修复意图、
  中段损坏隔离和 SQLite Schema v2 投影重建/追平。
- 完成 Executor 流式 Delta、等待 Checkpoint、Resolution、Cancel、Queue、Steer、
  首条文本自动标题及重启恢复。
- 完成 Archive、Unarchive、Delete Token、可续跑删除、Fork、Rollback 和路径安全
  Reconciler。
- 完成 `session → cli` 模块顺序、Starting 阶段健康上报、停止拒绝新工作、活动执行
  Flush/终态化，以及 State Schema 失败进入 Faulted。

## Acceptance Evidence

| Acceptance | Automated evidence |
| --- | --- |
| `M2-ACC-001` | `SessionDomainTests`、`SessionContractTests`。 |
| `M2-ACC-002` | `ThreadJournalTests` 写入故障矩阵；`SessionCrashRecoveryIntegrationTests` 在 Turn Flush 后终止真实子进程并由父进程恢复。 |
| `M2-ACC-003` | `SessionProjectionTests.Full_rebuild_removes_orphans_preserves_delete_receipts_and_matches_snapshot`。 |
| `M2-ACC-004` | `SessionServiceTests` 并发、Sequence 冲突、Projection Degraded 与追平。 |
| `M2-ACC-005` | `SessionQueueTests` 追加、删除、重排、Steer、重放、容量和自动标题。 |
| `M2-ACC-006` | `SessionExecutionTests` 等待、首次 Resolution、Cancel 竞态、Checkpoint 与重启续接。 |
| `M2-ACC-007` | `SessionRecoveryTests` 的 Archive/Unarchive 三阶段故障矩阵。 |
| `M2-ACC-008` | `SessionRecoveryTests` 的 Token、八阶段 Delete、Junction/Reparse 逃逸和外部文件保护。 |
| `M2-ACC-009` | `SessionRecoveryTests.Fork_survives_source_delete_and_rollback_replaces_model_history`。 |
| `M2-ACC-010` | `ThreadJournalTests` 损坏 Corpus 与 `SessionRuntimeTests` 单 Thread 隔离。 |

## Verification Snapshot

- Windows 11 `win-x64`、.NET SDK `10.0.302`、Runtime `10.0.10`。
- `dotnet restore OpenCoWork.slnx` 成功；Release build 为 `0` warning /
  `0` error。
- 完整测试为 `139` passed / `0` failed / `0` skipped：
  Core `107`、Integration `15`、Generators `14`、Architecture `3`。
- `OpenCoWork.Protocol.Tests` 在 M5 前仍是无可发现测试的冻结项目壳；测试运行器明确
  报告该状态，不计作 skipped，也没有隐藏失败。
- 真实子进程在 `TurnStarted` 已 Flush 后以退出码 `73` 终止；父测试在 15 秒有界
  等待内重启同一 Workspace，并断言 Turn 进入
  `runtime.interrupted` 持久终态，输出未泄露 Workspace 绝对路径。
- Windows 普通权限完成 Journal Flush、SQLite FULL/WAL、并发、Archive/Delete
  故障矩阵与 Junction/Reparse Point 外部逃逸保护；外部文件保持不变。
- `win-x64` 与 `osx-arm64` framework-dependent publish 均成功；后者仅为交叉发布。
- `win-x64` 发布目录真实运行提交 `a99f8aa` 的 `--version`、`init` 和
  `doctor --json`，Doctor 七项全部 Passed，Schema v2 与 `win-x64` 平台识别正确。
- `dotnet format --verify-no-changes` 与 `git diff --check` 通过。
- 2026-07-27 在 Apple Silicon macOS 26.5.2、`osx-arm64`、.NET SDK
  `10.0.302`、Runtime `10.0.10` 上补验提交
  `7ae53f2de59f4959b2097f1837e28a95d6db81ae` 加 Source Patch SHA-256
  `c2d3a54e9455d16f90db1f5fb21f8923dbb2a120101e773ed54f54335b761010`。
- macOS Release build 为 `0` warning / `0` error；完整测试仍为
  Core `107`、Integration `15`、Generators `14`、Architecture `3`，合计
  `139` passed / `0` failed / `0` skipped。
- 同一完整测试集先在默认 APFS 运行，再将 `TMPDIR` 切到临时 Case-sensitive
  APFS sparse image 重跑；两次均为 `139` passed / `0` failed / `0` skipped，
  镜像验证后已卸载。
- 真机完整回归覆盖 Journal Flush、半行尾部修复、Checksum/Sequence、中段损坏
  隔离、真实子进程中断、active/archived/deleting 恢复矩阵、Symlink 根外逃逸、
  SQLite WAL/FULL、同 Thread 串行、不同 Thread 并行、慢订阅者隔离、投影重建/
  追平、Waiting Checkpoint、Resolution/Cancel 竞态和 Delete Reconciler。
- `osx-arm64` framework-dependent 发布物为 Mach-O arm64；发布目录实跑
  `doctor --json` 正确识别 Schema v2 和 `osx-arm64`，七项检查全部 `Passed`。

## Out of Scope

- 真实 Provider、AgentFactory、工具执行和 Worktree Fork。
- Protocol/AppServer/Gateway 业务入口；进入后续里程碑。
- M10 最终发布候选的安装、升级、卸载、签名、公证和真实模型冒烟。

## Source Documents

- Spec: [M2 Durable Session Core 设计规格](../../specs/2026-07-26-open-cowork-m2-durable-session-core-design.md)
- Plan: [M2 Durable Session Core 实施计划](../../plans/2026-07-26-open-cowork-m2-durable-session-core-implementation-plan.md)
- Roadmap: [OpenCoWork Runtime 1.0 路线规格](../../specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md)

## Related Problems

None.

## Notes

- M2 作为一个整体任务关闭，只生成本归档；Outcome 1-8 仅作为依赖与提交边界。
- 普通 solution restore 不生成 RID-specific assets；正式发布命令按 RID 自行还原，
  未为此增加 `RuntimeIdentifiers` 或改变项目框架契约。
- 2026-07-27 M2 当前开发基线的 macOS 真机台账已清零；M10 仍须对最终发布候选
  重新执行完整 `osx-arm64` 发布验收。
