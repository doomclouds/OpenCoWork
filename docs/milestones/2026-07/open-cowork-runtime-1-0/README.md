# OpenCoWork Runtime 1.0 Milestone

## Background

以 DotCraft 核心运行时规范作为能力与行为参考，但不兼容 .craft、程序集或私有实现；OpenCoWork 使用自有品牌与最贴合语境的领域命名。

## Strategic Significance

将当前只有复刻规范的空壳仓库推进为拥有独立品牌、独立契约和完整代理协作运行时的 OpenCoWork 1.0。

## Milestone Goal

按依赖闭包交付从契约冻结、运行时基础、持久会话、Agent 与工具，到协议、扩展、多代理、自动化、网关及 1.0 收口的完整产品路线。

## Acceptance Statement

M0-M10 全部完成并具备对应交付证据；OpenCoWork 1.0 在 win-x64 与 osx-arm64 上通过构建、迁移、恢复、安全、故障注入和真实运行验收。

## Scope

OpenCoWork .NET 10 核心运行时、CLI、AppServer、ACP、扩展生态、多代理协作、自动化、外部渠道与 win-x64/osx-arm64 发布。

## Technical Context

- 目标框架为 .NET 10。
- 当前仓库仍处于设计起点，现有实现依据为根目录下的
  `DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md`。
- 原规范用于提取能力边界、状态语义、安全顺序和验收场景，不作为
  DotCraft 私有实现、程序集或持久化目录的兼容承诺。
- OpenCoWork 1.0 的正式发布目标为 `win-x64` 和 `osx-arm64`。
- 详细路线与已确认决策见
  [OpenCoWork Runtime 1.0 路线规格](../../../superpowers/specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md)。
- M0 冻结结果见
  [Contract Freeze](../../../superpowers/specs/2026-07-25-open-cowork-m0-contract-freeze-design.md)、
  [能力台账](../../../superpowers/specs/2026-07-25-open-cowork-m0-capability-ledger.md)
  和
  [验收目录](../../../superpowers/specs/2026-07-25-open-cowork-m0-acceptance-catalog.md)。

## Architecture Constraints

- 产品、程序集、命名空间、CLI 和持久化目录统一使用 OpenCoWork 品牌。
- 内部类型按职责和语义逐项判断，不进行机械式全量重命名。
- `OpenCoWork.Abstractions` 承载稳定跨程序集契约；Protocol 只通过该层调用
  Session Core，不直接依赖 Core 实现。
- `ToolDispatcher` 重命名为 `ToolInvocationPipeline`。
- `Rollout` 重命名为 `ThreadJournal`。
- `ThreadJournal` 是 Thread、Turn、Item 和模型可见历史的权威事实源；
  SQLite 中对应内容是可按 Journal Sequence 重建的查询投影。
- `OpenCoWork.Protocol` 必须保持独立程序集；协议适配器只能调用
  `ISessionService`，不得复制会话状态机或直接修改存储。
- `ToolInvocationPipeline` 必须保持解析、授权、校验、策略、Hook、审批、
  超时、执行、归一化与审计的固定顺序。
- Teams/Mission 的权威状态进入 SQLite，成员会话继续使用各自的
  `ThreadJournal`，Artifact 与 Scratchpad 使用文件存储。
- 插件采用“声明式或进程外优先；明确授信的 .NET 插件才允许进程内加载”
  的混合信任模型。
- Gateway 采用至少一次投递、幂等去重和 Outbox，不承诺 Exactly Once。
- 从 M1 开始隔离路径、Shell、进程、权限和服务生命周期的操作系统差异。

## Non-Goals

DotCraft .craft 兼容、DotCraft 二进制或私有实现兼容、Linux 与 Intel macOS 1.0 正式支持、桌面或 Web UI。

## Reference Signals

- [DotCraft 核心运行时复刻规范](../../../../DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md)
- [OpenCoWork Runtime 1.0 路线规格](../../../superpowers/specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md)
- [OpenCoWork M0 Contract Freeze](../../../superpowers/specs/2026-07-25-open-cowork-m0-contract-freeze-design.md)
- [OpenCoWork M0 能力台账](../../../superpowers/specs/2026-07-25-open-cowork-m0-capability-ledger.md)
- [OpenCoWork M0-M10 验收目录](../../../superpowers/specs/2026-07-25-open-cowork-m0-acceptance-catalog.md)
- 2026-07-25 用户逐项确认的 M0-M10 头脑风暴决策

## Slice Boundaries

| Slice | 边界与阶段结果 | 本 Slice 明确不包含 | 完成信号 |
| --- | --- | --- | --- |
| M0 - Contract Freeze | 冻结品牌、术语、程序集、配置、协议、存储和能力台账，形成后续实现唯一设计基线。 | 工程骨架和业务代码。 | 所有能力均标记为保持语义、OpenCoWork 重设计或延期，且不存在影响项目结构的开放决策。 |
| M1 - Runtime Foundation | 建立可构建、可初始化、可诊断、可安全启停的 .NET 10 运行时骨架。 | Session、Agent、工具和业务协议。 | `opencowork init/doctor/--version` 可用，主宿主选择、配置优先级、SQLite 基础和生命周期测试通过。 |
| M2 - Durable Session Core | 交付可持久化、可并发、可恢复的 Thread-Turn-Item 会话核心。 | 真实 Provider、AgentFactory、真实工具和 Worktree Fork。 | Journal 重放、SQLite 投影重建、并发、等待、取消、队列、归档和恢复验收通过。 |
| M3 - Agent Runtime Alpha | 交付无工具但支持真实多轮对话、流式响应、重试和上下文压缩的 Agent Runtime。 | 真实工具副作用和外部协议。 | CLI 可完成并恢复真实多轮对话，流中断与压缩故障注入通过。 |
| M4 - Tool Runtime Alpha | 交付模型可安全产生副作用的工具身份、快照、审批和固定执行管线。 | MCP、插件、动态工具和 AppServer。 | 管线逐阶段测试、稳定错误码、Plan 模式限制和副作用不重复验收通过。 |
| M5 - OpenCoWork Wire Alpha | 交付 Desktop-first 的本地子进程 Wire、语义事件同步和稳定 ACP v1 Bridge。 | 正式 Desktop SDK、daemon、远程监听、ACP 草案和扩展域。 | stdio/loopback WebSocket、核心会话流与 ACP v1 通过端到端契约和双平台真机验收。 |
| M6 - Capability Ecosystem | 交付 Skills、Plugins、MCP、LSP、Hooks、SourceControl 与动态/延迟工具生态。 | Teams、Automations 和 Gateway 编排。 | 插件信任、Binding Lease、断连失效、能力热更新和冲突隔离验收通过。 |
| M7 - Multi-Agent CoWork | 交付 SubAgent、Teams、Mission DAG、Mailbox、Artifact 和 Worktree 协作闭环。 | 周期自动化和外部渠道。 | DAG、成员互斥、预算、恢复、Leader 综合和 Origin 单次回传验收通过。 |
| M8 - Automations and Scheduler | 交付可版本控制定义、可恢复运行和严格权限边界的无人值守调度。 | Gateway、Hub 和外部渠道交付。 | 去重调度、时区、并发、Worktree、定义快照、NeedsAttention 和崩溃恢复验收通过。 |
| M9 - Gateway and Operations | 交付外部渠道、可靠消息、Hub、Heartbeat、Tracing 和后台服务运行能力。 | 新增大型核心子系统和桌面/Web UI。 | 入站去重、Outbox、断连隔离、顺序、媒体安全和后台生命周期验收通过。 |
| M10 - OpenCoWork 1.0 Closure | 关闭契约缺口，完成迁移、恢复、安全、性能、安装和双平台发布证据。 | 临门新增大型功能、Linux/Intel macOS 正式支持。 | M0 能力台账清零，`win-x64` 与 `osx-arm64` 发布候选通过完整验收。 |

## Update Rules

- Keep `CHECKLIST.md` at slice granularity.
- Link this milestone's detailed design docs under `specs/`.
- Link implementation plans under `docs/superpowers/plans/`.
- Link completed delivery archives under `docs/superpowers/archives/`.
- Recompute progress whenever any slice status changes.
- Update `docs/milestones/INDEX.md` whenever milestone status or progress changes.
