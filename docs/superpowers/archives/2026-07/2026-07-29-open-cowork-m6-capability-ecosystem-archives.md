# OpenCoWork M6 Capability Ecosystem

- Date: `2026-07-29`
- Topic slug: `open-cowork-m6-capability-ecosystem`
- Status: `Archived`
- Scope: `Feature`
- Tags: `capability`, `plugin`, `mcp`, `lsp`, `wire`

## Summary

M6 在既有 Workspace、Session、Tool Pipeline 与 Wire 权威边界上交付了
Desktop-first Capability Ecosystem，使 Skills、Plugins、Provider/Auth、MCP、
LSP、Hooks 与动态能力可以被确定性发现、授信、冻结、调用、撤销和清理。

## Delivered Scope

- 建立单一 Workspace Capability Runtime、不可变 Catalog、单调 Revision、
  Trust/Override、内容寻址 Plugin Store 与精确 Lock。
- 交付 Skills/Variant、Provider/Auth、MCP/LSP、Deferred/Dynamic Tools、
  Hooks、Git SourceControl、Thread Background Terminal 与 Workspace Memory。
- Wire 1.1 提供能力目录、刷新和动态回调，同时保持 Wire 1.0 与 ACP v1 回归兼容。
- `win-x64` 与 `osx-arm64` 发布目录分别通过 Secret Store、Git、Terminal、
  Memory、Wire 和进程树清理真机验收。

## Out of Scope

- Teams、Automations、Gateway 及 M7-M9 编排能力。
- 新增真实 Provider 兼容性声明、完整 Marketplace、签名、自动更新与 Store GC。
- PTY/Terminal 重连、可写 LSP、MCP Apps/Sampling/Elicitation 等 M6 外能力。

## Verification Snapshot

- Windows 基线 `b25d2153805c5df158c3dde0d512f31107abdaa5` 加
  Source/Test Patch SHA-256
  `40c1dce1bacda69817b725086694c0ca34052924fbd04de5eb386a4edb55d7cb`：
  Release build `0` warning / `0` error，`373` 项离线测试通过。
- App/TestClient `win-x64` framework-dependent 发布目录通过 Wire
  stdio/1.1、ACP v1、WebSocket、Credential Manager、Git、Memory、隐藏终端、
  动态工具、Secret Canary 与持久进程残留检查。
- Apple Silicon macOS 26.5.2 于 2026-07-30 在包含 Windows 修正的提交
  `16768f490077585285a288e2fab01a425416ff51` 上完成 Release build
  `0` warning / `0` error、`373` 项离线测试及 App/TestClient `osx-arm64`
  发布目录 Keychain、Git、Memory、Terminal、Wire 与进程树复验。
- `M6-ACC-001` 至 `M6-ACC-010` 全部为 `Passed`；M10 仍须在最终发布候选上
  重跑完整双平台验收。

## Source Documents

- Spec: [M6 Capability Ecosystem 详细设计](../../specs/2026-07-29-open-cowork-m6-capability-ecosystem-design.md)
- Visual: None found for this topic.
- Plan: [M6 Capability Ecosystem 实施计划](../../plans/2026-07-29-open-cowork-m6-capability-ecosystem-implementation-plan.md)

## Related Problems

- [Windows 验证暴露隐藏的平台测试假设](../../problems/2026-07/2026-07-29-windows-cross-platform-test-assumptions-problem.md)

## Notes

- 双平台环境、产物摘要与发布目录场景见
  [平台发布验证台账](../../../platform-release-validation-ledger.md)。
- Provider 仍只使用 Fake OpenAI-compatible Server；未新增真实 Provider 支持声明。
