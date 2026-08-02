# OpenCoWork M10 Gateway and Operations

- Date: `2026-08-01`
- Topic slug: `open-cowork-m10-gateway-operations`
- Status: `Archived`
- Scope: `Feature`
- Tags: `gateway`, `outbox`, `operations`, `wire-1.4`, `dual-platform`

## Summary

M10 在唯一 WorkspaceRuntime、Session Core 与 SQLite 边界内交付 loopback Webhook
Gateway、可靠 Inbound/Outbox、Hub 和 Operations 查询，使外部渠道任务具备持久去重、
顺序、恢复、隔离和可观测闭环。

## Delivered Scope

- 交付 HMAC Webhook、严格 Envelope、有界媒体、Inbound 幂等、Outbox/Dead Letter 与
  持久分区顺序。
- 交付用户级 Workspace Registry、Hub、Usage、Trace、Heartbeat、Insight 与 CLI/Wire
  1.4 查询。
- 保持 State v9 迁移、WorkspaceRuntime 生命周期、`ISessionService` 和既有工具授权链
  为唯一权威边界。
- `win-x64` 与 `osx-arm64` 发布目录均通过 Protocol TestClient、M10 Runner、OS Secret、
  Secret Canary 和残留检查。

## Out of Scope

- Slack、Teams 等厂商 SDK、桌面/Web UI、公网监听、内建 TLS 与远程管理面。
- 多模态 Provider、Exactly Once、安装/升级、签名/公证和最终 1.0 发布候选验收。

## Verification Snapshot

- macOS 基线 `050b85c1c42ca2e3bd2abd5eb0943232895081d7`：`638` 项离线回归、
  Release build `0` warning / `0` error、三套 Mach-O arm64 发布目录、Protocol
  TestClient 8 场景与 Gateway/Outbox/Operations/Runtime Composition Runner
  `13 passed / 0 failed / 0 skipped`。
- macOS Keychain Set/Clear、Secret Canary、临时 Workspace、用户级 Registry、进程与
  句柄检查通过；Registry 清理后恢复验证前 SHA-256
  `f25b0e5dea6023f24eff8a4cbbdbb1e7a958c0d30c5ff10796bc9645b6e43e11`。
- Windows 基线 `2d966400e61e8d17c8a513299e8a9b420591d865` 加 Source/Test Patch
  SHA-256 `516c263191620d8b9f41eb5bbce0436aac41ee04aef6be73af5c5514783e90cd`：
  发布目录 Protocol 8 场景、Runner 13 项、Credential Manager、Junction/Reparse、
  Secret Canary 与残留检查通过。
- `M9-ACC-001` 至 `M9-ACC-010` 全部为 `Passed`；M11 仍须在最终发布候选上复验。

## Source Documents

- Spec: [M10 Gateway and Operations 设计](../../specs/2026-08-01-open-cowork-m10-gateway-operations-design.md)
- Visual: None found for this topic.
- Plan: [M10 Gateway and Operations 实施计划](../../plans/2026-08-01-open-cowork-m10-gateway-operations-implementation-plan.md)

## Related Problems

- [Windows 验证暴露隐藏的平台测试假设](../../problems/2026-07/2026-07-29-windows-cross-platform-test-assumptions-problem.md)

## Notes

- 双平台证据见 [平台发布验证台账](../../../platform-release-validation-ledger.md)。
- M10 是 Runtime 1.0 最后一个功能 Slice；M11 只做契约、迁移、恢复、安全、性能、安装
  与最终发布收口。
