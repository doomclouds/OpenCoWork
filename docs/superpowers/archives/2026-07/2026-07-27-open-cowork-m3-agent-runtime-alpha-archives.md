# OpenCoWork M3 Agent Runtime Alpha

- Date: `2026-07-27`
- Topic slug: `open-cowork-m3-agent-runtime-alpha`
- Status: `Archived`
- Scope: `Feature`
- Tags: `agent`, `provider`, `streaming`, `tokenizer`, `compaction`, `CLI`

## Summary

M3 在 M2 的唯一 Session/Journal 执行链上交付了无真实工具的 Agent Runtime：
Provider、模型、Prompt、Tokenizer 和上下文预算在 Invocation 前冻结，流式内容、
Reasoning、Usage、重试和压缩只通过 `ISessionExecutionSink` 进入既有事实链。
`opencowork chat` 可以完成真实多轮对话和精确 Thread 恢复，首个真实 Provider
以 DeepSeek 官方在 Apple Silicon macOS 上验收。

## Delivered Scope

- 完成最小 `IChatCompletionClient` 契约、Provider Registry、确定性 AgentFactory、
  Invocation Snapshot、Usage 事实和 State Schema v3。
- 完成有界 OpenAI-compatible Chat Completions/SSE 客户端、Secret 脱敏、稳定错误、
  首个可见增量前重试和提交后部分输出保护。
- 完成版本化 Tokenizer Profile、真实 Token 预算、Micro/Partial Compaction、
  prompt-too-long 响应式压缩和可恢复 Checkpoint。
- 完成 `opencowork chat` 多轮、恢复、模型选择、Agent/Plan 模式、取消和
  stdout/stderr 隔离。
- 完成 `osx-arm64` 上 DeepSeek 官方 `deepseek-v4-pro` 与
  `deepseek-v4-flash` 的真实短冒烟和 Secret Canary。

## Out of Scope

- 真实工具副作用、Tool Call 执行、AppServer、ACP、MCP、插件 Provider 和动态模型
  发现；分别进入 M4-M6。
- 千问 Token Plan、其他 OpenAI-compatible Provider 和 `win-x64` 真实 Provider
  兼容性；统一登记在
  [Provider 真实兼容性待验证清单](../../../provider-validation-backlog.md)。
- M10 最终双平台安装、升级、长期运行和完整 Provider 兼容矩阵。

## Acceptance Evidence

| Acceptance | Evidence |
| --- | --- |
| `M3-ACC-001` | `AgentFactoryTests`、Prompt Golden、`AgentContractTests`。 |
| `M3-ACC-002` | `ChatCompletionClientTests`、`StructuredLoggingTests`、Secret Canary；提交 `3da2e47` 的两条 `osx-arm64` DeepSeek 真实冒烟。 |
| `M3-ACC-003` | `AgentRuntimeExecutorTests`、`SessionExecutionTests`、`SessionCrashRecoveryIntegrationTests`。 |
| `M3-ACC-004` | 首个可见 Delta 前后断流、Retry-After、Deadline、协议错误和调用计数故障注入。 |
| `M3-ACC-005` | `AgentFactoryTests`、`CompactionTests`、Projection/Recovery Checkpoint 重放。 |
| `M3-ACC-006` | 精确 prompt-too-long Fixture、三次调用预算、唯一当前 Turn 和压缩失败边界。 |
| `M3-ACC-007` | 流式、重试、压缩和恢复后的 Usage 唯一键与投影对账。 |
| `M3-ACC-008` | `ChatCliIntegrationTests`、模式切换、Queue 冻结和重启恢复。 |

## Verification Snapshot

- Apple Silicon macOS、`osx-arm64`、.NET SDK `10.0.302`、Runtime `10.0.10`。
- Release build 为 `0` warning / `0` error。
- 完整离线回归为 Core `147`、Integration `18`、Generators `14`、
  Architecture `4`，合计 `183` passed / `0` failed；显式真实 Provider Runner
  按设计跳过。
- `OpenCoWork.Protocol.Tests` 仍是 M5 前无可发现测试的冻结项目壳。
- `osx-arm64` framework-dependent publish 成功；RID 专用 restore 后不再出现
  `NETSDK1047`。
- 提交 `3da2e47f1a917529e3264535b7f9efed66d1b2bb` 的 DeepSeek 真实证据：
  `deepseek-v4-pro` 为 `142 / 18 / 160` tokens、`stop`、Pass；
  `deepseek-v4-flash` 为 `144 / 26 / 170` tokens、`stop`、Pass。
- DeepSeek-only Runner 在强制清空凭据时仅报告两条 `NotRun`，不包含 Token Plan
  占位且未访问网络。
- `scripts/setup-deepseek-env-macos.zsh` 通过 `zsh -n`、临时设置和 `--clear`
  无 Secret 模拟；脚本不修改 shell 配置，重启后环境失效。

## Source Documents

- Spec: [M3 Agent Runtime Alpha 设计规格](../../specs/2026-07-27-open-cowork-m3-agent-runtime-alpha-design.md)
- Visual: None found for this topic.
- Plan: [M3 Agent Runtime Alpha 实施计划](../../plans/2026-07-27-open-cowork-m3-agent-runtime-alpha-implementation-plan.md)
- Roadmap: [OpenCoWork Runtime 1.0 路线规格](../../specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md)

## Related Problems

None.

## Notes

- 2026-07-28 用户确认 M3 回到 M0 的“首个真实 Provider”边界，以 DeepSeek 官方
  关闭本 Slice；共享协议适配不替代后续 Provider 的真实证据。
- macOS 临时环境使用 `launchctl setenv`，只对之后启动的应用生效；操作者可运行
  `scripts/setup-deepseek-env-macos.zsh --clear` 提前清除。
