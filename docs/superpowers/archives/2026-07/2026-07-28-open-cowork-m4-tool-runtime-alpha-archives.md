# OpenCoWork M4 Tool Runtime Alpha

- Date: `2026-07-28`
- Topic slug: `open-cowork-m4-tool-runtime-alpha`
- Status: `Archived`
- Scope: `Feature`
- Tags: `tool-runtime`, `approval`, `journal`, `file`, `shell`, `web`

## Summary

M4 在 M3 Agent Runtime 和既有 Session/Journal 执行链上交付了最小 Tool
Runtime：每个 Turn 冻结工具身份与权限快照，Provider Tool Call 经过固定安全管线、
审批、超时、结果归一化和审计后回注同一对话。File、Shell、Web 共五个 Core 工具
共享稳定错误、Secret 脱敏、取消和副作用不重复保护，没有引入第二套状态机或工具
存储。

## Delivered Scope

- 完成 Tool Definition、Binding、Registration、Provider 名称映射、
  `EffectiveToolSnapshot` 和 Agent/Plan 曝光边界。
- 完成 `ToolInvocationPipeline` 的固定阶段、Authority 交集、Schema、内部 Hook、
  Approval/Resume、Attempt/Terminal 审计和 Safe/Unsafe 恢复。
- 完成 ToolCall/ToolResult Journal 事实、SQLite v4 投影、Provider 工具协议、
  工具消息历史和 Compaction Checkpoint v2。
- 完成原子文本 File、PowerShell/zsh Shell、受 SSRF 与输出上限保护的 Web 工具，
  并接入生产 DI 和 CLI 审批链。

## Out of Scope

- MCP、Plugin、动态或延迟工具、公共 Hook API、后台工具、Sandbox、Node REPL、
  AppServer、ACP、SourceControl 和独立 Tool Store。
- `win-x64` 真机 PowerShell、发布目录 File/Shell/Web、进程树残留和 Secret
  Canary；按用户确认的关闭边界保留在
  [双平台真机发布验证台账](../../../platform-release-validation-ledger.md)，状态为
  `Pending`，对应 `M4-ACC-006`、`M4-ACC-009` 为 `Deferred`。
- M10 最终双平台安装、升级、迁移、恢复、安全、性能和发布候选验收。

## Acceptance Evidence

| Acceptance | Evidence |
| --- | --- |
| `M4-ACC-001` | `ToolContractTests`、`ToolSnapshotTests` 和架构测试。 |
| `M4-ACC-002` | 工具快照冻结、热更新竞态、名称限制与冲突隔离测试。 |
| `M4-ACC-003` | `ToolInvocationPipelineTests` 的逐阶段 Trace、顺序和旁路防护。 |
| `M4-ACC-004` | Audience、Exposure、Mode、Lease、Authority、Schema、Policy 拒绝矩阵和稳定错误测试。 |
| `M4-ACC-005` | Authority 交集、恶意 Hook、重复审批及 CLI Approval/Resume 测试。 |
| `M4-ACC-006` | 自动化超时/取消矩阵与 `osx-arm64` 进程树残留通过；`win-x64` 真机部分 `Deferred`。 |
| `M4-ACC-007` | Safe/Unsafe 恢复、提交窗口、`tool.outcomeUnknown` 和副作用计数测试。 |
| `M4-ACC-008` | Agent/Plan、Effect、Authority 和 Provider 名称组合矩阵。 |
| `M4-ACC-009` | `osx-arm64` 发布目录 File/zsh/Web 实跑通过；Windows PowerShell 真机部分 `Deferred`。 |
| `M4-ACC-010` | 重复 Call ID、Journal 重放、Checkpoint 恢复和副作用唯一性故障注入。 |

## Verification Snapshot

- 产品实现基线为提交 `d236f29`；Apple Silicon macOS 26.5.2、
  `osx-arm64`、.NET SDK `10.0.302`、Runtime `10.0.10`。
- Release build 为 `0` warning / `0` error。
- 完整离线回归为 Core `218`、Integration `22`、Generators `14`、
  Architecture `5`，合计 `259` passed / `0` failed；显式真实 Provider Runner
  按设计跳过。
- `osx-arm64` framework-dependent 发布物确认为 Mach-O arm64；发布目录通过本地
  Fake Provider 和真实 CLI 审批链完成 `file.write`、`shell.run`、
  `web.fetch` 私网拒绝和 Tool Result 回注。
- Shell 实际宿主为 `/bin/zsh`，取消后进程树无残留；Secret Canary 未命中
  Journal、SQLite、Session Event、Provider 请求、日志、stdout/stderr 或测试目录。
- `win-x64` 仅完成交叉发布，不能作为 Windows 真机证据；后续清单以双平台台账为准。

## Source Documents

- Spec: [M4 Tool Runtime Alpha 设计规格](../../specs/2026-07-28-open-cowork-m4-tool-runtime-alpha-design.md)
- Visual: None found for this topic.
- Plan: [M4 Tool Runtime Alpha 实施计划](../../plans/2026-07-28-open-cowork-m4-tool-runtime-alpha-implementation-plan.md)
- Roadmap: [OpenCoWork Runtime 1.0 路线规格](../../specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md)

## Related Problems

None.

## Notes

- 2026-07-28 用户确认关闭 M4 功能需求，并将 Windows 真机验证留在统一台账后续
  集中处理；延期不等于通过，也不豁免 M10 双平台发布门禁。
