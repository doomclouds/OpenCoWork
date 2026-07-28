# OpenCoWork M5 Wire Alpha

- Date: `2026-07-28`
- Topic slug: `open-cowork-m5-wire-alpha`
- Status: `Archived`
- Scope: `Feature`
- Tags: `wire`, `json-rpc`, `websocket`, `acp`, `desktop`, `transport`

## Summary

M5 交付了面向 OpenCoWork Desktop 的本地 OpenCoWork Wire，以及消费同一
Session Core 的 ACP 稳定 v1 Bridge。stdio JSONL、loopback WebSocket 和 ACP
只做协议与传输适配，不直接写 Store，也不复制 Thread/Turn 状态机。

## Delivered Scope

- 完成强制 initialize、稳定 JSON-RPC 错误、Generated Wire Catalog 和核心
  Thread/Turn/Item 方法与语义事件投影。
- 完成 Desktop 子进程 stdio 主通道和仅限 loopback、Bearer Header 鉴权的
  WebSocket 次通道，并实施 UTF-8、消息上限与慢读端隔离。
- 完成 ACP v1 initialize/new/load/prompt/cancel/set_mode 映射、历史与实时去重、
  Approval permission request 和不支持 UserInput 的明确失败。
- 完成发布目录 Protocol TestClient，覆盖重连、业务取消、Secret Canary 与子进程
  回收，并取得 `win-x64`、`osx-arm64` 双平台真机证据。

## Out of Scope

- 正式 Desktop Client SDK、Desktop UI、daemon、远程监听、TLS、浏览器 Origin、
  端口发现和长期驻留服务。
- ACP v2 草案、v1 可选扩展、JSON-RPC batch，以及 Skills、MCP、Teams、
  Automations 和 Gateway 方法。
- 真实 Provider 兼容性和 M10 最终安装、升级、迁移、性能及发布候选复验。

## Verification Snapshot

- `osx-arm64` 产品基线为
  `882efd9c22e2323060d23938501191dcc409b981`；Apple Silicon macOS
  26.5.2 真机已完成 280 项离线测试、Release build 和 App/TestClient 发布目录
  全场景运行。
- `win-x64` 验证基线为
  `9cf7e1e366d04fd63ac55906924ea0dde630321d`，验证时 Source/Test Patch
  SHA-256 为
  `848ec5c02b1ef9be5afc7d9e1ffeccfa74539d3d2978b09fa9aa6f96438b1725`；
  Windows 11 Home `10.0.26200` x64、.NET SDK `10.0.302`、Runtime
  `10.0.10`。
- Windows Release 全量回归为 Core `221`、Integration `24`、Generators `15`、
  Protocol `15`、Architecture `5`，合计 `280` passed / `0` failed /
  `0` skipped；Release build 为 `0` warning / `0` error。
- `win-x64` App 与 Protocol TestClient 发布目录真实运行通过。App SHA-256 为
  `11F7F38EED98FC44C3845A3728AF522B8F497D1F83CEA75D3D58F527EC8C6AAA`，
  TestClient SHA-256 为
  `F329FE294C0C348C4B7C040015ACA23E5BFA8E7AD97C2C05AA7AAB14B62F43E8`。
- TestClient 在 5.202 秒内通过 Wire stdio、ACP v1、loopback WebSocket
  Bearer Header、重连去重、慢读端、业务取消和 Secret Canary 场景；退出后无
  `opencowork` 子进程残留。
- `M5-ACC-001` 至 `M5-ACC-009` 全部为 `Passed`；双平台明细见
  [真机发布验证台账](../../../platform-release-validation-ledger.md)。

## Source Documents

- Spec: [M5 Wire Alpha 详细设计](../../specs/2026-07-28-open-cowork-m5-wire-alpha-design.md)
- Visual: None found for this topic.
- Plan: [M5 Wire Alpha 实施计划](../../plans/2026-07-28-open-cowork-m5-wire-alpha-implementation-plan.md)
- Roadmap: [OpenCoWork Runtime 1.0 路线规格](../../specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md)

## Related Problems

- [Windows 验证暴露隐藏的平台测试假设](../../problems/2026-07/2026-07-29-windows-cross-platform-test-assumptions-problem.md)

## Notes

- M5 的双平台开发基线已经关闭，但 M10 仍须在最终发布候选提交上重跑完整双平台
  验收；本归档不能直接替代 1.0 发布结论。
