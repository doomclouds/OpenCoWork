# OpenCoWork Runtime 1.0 路线规格

## 文档状态

- 状态：已确认
- 日期：2026-07-25
- 修订：2026-07-28，按 M5 头脑风暴确认 Desktop-first Wire 与稳定 ACP v1 边界
- 修订：2026-08-01，新增 M9 DeepSeek Responses Provider，原 Gateway 与
  1.0 Closure 顺延为 M10/M11
- 目标版本：OpenCoWork 1.0
- 目标框架：.NET 10
- 正式平台：`win-x64`、`osx-arm64`
- 原始能力参考：
  [DotCraft 核心运行时复刻规范](../../../DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md)
- 进度台账：
  [OpenCoWork Runtime 1.0 Milestone](../../milestones/2026-07/open-cowork-runtime-1-0/README.md)
- M0 冻结契约：
  [OpenCoWork M0 Contract Freeze](2026-07-25-open-cowork-m0-contract-freeze-design.md)

## 1. 产品目标

OpenCoWork 是一个拥有独立品牌、独立领域模型和独立持久化边界的 .NET 10
代理协作运行时。项目参考原规范中的模块组合、Workspace 生命周期、Session
状态机、工具安全顺序、上下文维护、协议适配、多代理协作和无人值守运行能力，
但不以恢复 DotCraft 的私有类型、程序集、目录或二进制兼容为目标。

OpenCoWork 1.0 的完成标准不是“模型能够回复消息”，而是：

- 重启后状态与模型历史可恢复；
- 并发不会破坏 Thread、Turn、Item 和任务状态；
- 模型工具调用不能绕过权限、审批、超时和审计；
- CLI、AppServer、ACP 与 Gateway 共享唯一 Session Core；
- Skills、Plugins、MCP 和 LSP 能按信任边界组合；
- Teams 和 Automations 能在故障后协调恢复；
- Windows x64 和 Apple Silicon macOS 具备真实发布证据。

## 2. 明确边界

### 2.1 纳入 1.0

- OpenCoWork .NET 10 核心运行时；
- CLI、AppServer、ACP、Gateway 与 Hub；
- Thread-Turn-Item Session Core；
- Agent、上下文、压缩、记忆和工具运行时；
- Skills、Plugins、MCP、LSP、Hooks 与 SourceControl；
- SubAgent、Teams、Mission、Mailbox、Artifact 与 Worktree；
- Automations、Cron、Heartbeat、后台反思/改进建议能力；
- `win-x64` 与 `osx-arm64` 的构建、测试和发布。

### 2.2 不纳入 1.0

- DotCraft `.craft` 目录兼容；
- DotCraft 程序集、命名空间、二进制或私有实现兼容；
- Linux 和 Intel macOS 的正式支持承诺；
- 桌面 UI 或 Web UI；
- 为追求数字一致而机械复制原规范中的方法、配置或表结构数量。

## 3. 品牌、命名与术语

### 3.1 品牌边界

- 产品名：`OpenCoWork`
- 根命名空间：`OpenCoWork`
- CLI：`opencowork`
- 工作区数据目录：`.opencowork`
- 用户级目录：`%USERPROFILE%\.opencowork` 或 macOS 对应的用户主目录路径
- 插件清单目录：`.opencowork-plugin`

### 3.2 命名原则

旧品牌标识必须替换；领域术语按职责和语义逐项判断，不进行全仓库机械式
重命名。标准且准确的术语应保留，例如：

- `WorkspaceRuntime`
- `SessionThread`
- `SessionTurn`
- `SessionItem`
- `AgentSession`
- `SessionService`
- `AgentFactory`
- `EffectiveToolSnapshot`
- `Teams`
- `Mission`

已确认重命名：

| 原名称 | OpenCoWork 名称 | 原因 |
| --- | --- | --- |
| `ToolDispatcher` | `ToolInvocationPipeline` | 实际职责覆盖调用解析、安全检查、审批、执行、归一化与审计，而非单纯分发。 |
| `Rollout` | `ThreadJournal` | 本质是 Thread 的权威追加日志，支持回放和恢复。 |

配套类型应保持一致，例如：

- `IToolInvocationPipeline`
- `ToolInvocationContext`
- `ToolInvocationResult`
- `ThreadJournalStore`
- `ThreadJournalWriteGate`
- `OrderedThreadJournalWriter`
- `ThreadJournalEntry`
- `ThreadJournalReplayer`

补充冻结命名：

- `CraftPath` 概念使用 `Workspace Data Root`；
- 路径服务使用 `OpenCoWorkPaths`；
- `Dreams` 使用 `Workspace Insights`；
- `DreamsService` 使用 `WorkspaceInsightService`；
- 一次洞察运行使用 `InsightRun`；
- 可审阅改进建议使用 `ImprovementProposal`。

## 4. 程序集与依赖方向

生产程序集基线：

```text
src/
├── OpenCoWork.Abstractions
├── OpenCoWork.App
├── OpenCoWork.Core
├── OpenCoWork.Protocol
├── OpenCoWork.Automations
├── OpenCoWork.Teams
└── OpenCoWork.Generators
```

测试基线：

```text
tests/
├── OpenCoWork.Core.Tests
├── OpenCoWork.Protocol.Tests
├── OpenCoWork.Generators.Tests
├── OpenCoWork.ArchitectureTests
├── OpenCoWork.IntegrationTests
└── OpenCoWork.Protocol.TestClient
```

约束：

- `OpenCoWork.Abstractions` 承载稳定跨程序集与插件契约，不包含存储和宿主实现；
- `OpenCoWork.App` 是入口、宿主组合层与 `opencowork` 可执行项目；
- `OpenCoWork.Core` 依赖 Abstractions，实现 Workspace、Session、Agent、Tool、State；
- `OpenCoWork.Protocol` 只依赖 Abstractions，独立承载 JSON-RPC、AppServer、ACP 和协议 DTO；
- `OpenCoWork.Automations` 与 `OpenCoWork.Teams` 依赖 Abstractions 和 Protocol 扩展点，
  彼此不得互相引用；
- `OpenCoWork.Generators` 是 `netstandard2.0` Analyzer-only 编译期程序集；
- Protocol Handler 不得直接修改 ThreadStore 或建立第二套状态机。

## 5. 稳定架构中心

OpenCoWork 的实现围绕五个稳定中心推进：

1. `ModuleRegistry` 与 HostBuilder 决定进程组合；
2. `WorkspaceRuntime` 决定工作区生命周期；
3. `ISessionService` 决定所有对外可见会话状态；
4. `AgentFactory` 决定模型、上下文和工具视图；
5. `ToolInvocationPipeline` 决定模型副作用的安全边界。

CLI、AppServer、ACP、Gateway、Teams 与 Automations 都是这些中心的适配或
编排，不得各自复制 Session 或 Tool 状态机。

## 6. 持久化与一致性

### 6.1 Session 权威模型

`ThreadJournal` 是以下内容的权威事实源：

- SessionThread；
- SessionTurn；
- SessionItem；
- 模型可见历史；
- 回滚和压缩检查点。

SQLite 为这些内容提供可重建查询投影。每个 Thread Journal Entry 至少包含：

- `schemaVersion`
- `threadId`
- `sequence`
- `entryId`
- `timestamp`
- `entryType`
- `idempotencyKey`
- `payload`
- `checksum`

SQLite 保存最后应用的 Journal Sequence。启动发现投影落后时，必须从
Journal 补齐；删除 Session 查询投影后，应能够完整重建。

状态提交顺序为：

```text
获取线程协调锁
→ 校验状态
→ 追加并确认 ThreadJournal
→ 更新内存聚合
→ 更新 SQLite 投影
→ 发布 SessionEvent
```

不得在权威记录成功前发布外部可见状态。

### 6.2 Teams 与文件存储

- Mission、Task、Member、Mailbox 和调度状态以 SQLite 为权威；
- 成员对话继续使用每个 Thread 自己的 `ThreadJournal`；
- Scratchpad 和 Artifact 使用文件系统；
- 通过 MissionId、TaskId、MemberId 与 ThreadId 建立关联；
- 不复刻单个 `teams/state.json` 作为并发写入热点。

### 6.3 Automation

- YAML 是 Automation 定义的事实源；
- SQLite 是 Schedule 与 Automation Run 状态的事实源；
- 每个 Run 冻结定义版本、权限、插件和工具快照；
- 活动 Run 不受定义文件中途修改影响。

### 6.4 Gateway

- 入站消息先持久化，再交给 Session Core；
- 出站消息先写 Outbox，再发送；
- 采用至少一次投递与幂等去重；
- 不承诺跨外部系统的 Exactly Once；
- 只保证单个外部会话内有序，不保证全局顺序。

## 7. 安全与信任

### 7.1 工具调用

`ToolInvocationPipeline` 的固定顺序为：

```text
Snapshot 查找
→ Started 审计
→ Audience / Exposure
→ Binding Availability / Lease
→ Authority
→ Input Schema
→ Policy
→ PreToolUse Hook
→ Approval
→ Timeout-linked Invocation
→ Result Normalization
→ Terminal 审计与 Hook
```

任何前置拒绝也必须产生稳定错误码和 Terminal 记录。Agent 与 Plan 模式必须
使用不同的工具曝光范围。

### 7.2 插件

插件采用混合边界：

1. Skills、Prompt、MCP 和外部命令默认使用声明式或进程外能力；
2. 只有用户明确授信的 .NET 原生插件可以进程内加载；
3. 进程内插件使用独立 `AssemblyLoadContext` 管理依赖与卸载；
4. `AssemblyLoadContext` 不视为安全沙箱；
5. 未授信代码不得注册原生工具或可信 Hook。

### 7.3 无人值守

Automation 使用比普通交互线程更严格的工具、路径和网络权限。需要人工审批时：

```text
Running → NeedsAttention → Running
                         ↘ Cancelled / TimedOut
```

不得依靠 Console 输入解除等待，也不得因为无人值守而自动放行。

## 8. 平台边界

1.0 正式支持：

- Windows x64：`win-x64`
- Apple Silicon macOS：`osx-arm64`

1.0 不承诺：

- Linux
- Intel macOS：`osx-x64`

从 M1 开始建立平台抽象，至少覆盖：

- 路径与用户级数据目录；
- Shell 解析；
- 进程启动和进程树终止；
- 文件权限与符号链接；
- Git 和 Worktree；
- MCP/LSP 子进程；
- 后台服务生命周期；
- SQLite native bundle。

两个正式平台都必须使用真实机器或可信 CI 完成发布验收。

## 9. 里程碑路线

### M0 - Contract Freeze

目标：把当前已确认方向转化为后续实现的唯一契约基线。

包含：

- 品牌、术语和重命名台账；
- 程序集与依赖方向；
- 配置、路径、协议、状态和存储目录；
- 原规范能力映射；
- 每项能力的保持语义、OpenCoWork 重设计或延期结论；
- M1-M11 的验收编号。

交付规格：

- [OpenCoWork M0 Contract Freeze](2026-07-25-open-cowork-m0-contract-freeze-design.md)
- [OpenCoWork M0 能力台账](2026-07-25-open-cowork-m0-capability-ledger.md)
- [OpenCoWork M0-M11 验收目录](2026-07-25-open-cowork-m0-acceptance-catalog.md)

不包含：

- Solution 和项目骨架；
- 业务代码和运行时代码。

完成信号：

- 不存在影响工程结构的开放决策；
- 所有参考能力均有明确去向；
- 后续 Slice 可以只引用本规格与各自详细规格开始规划。

### M1 - Runtime Foundation

目标：建立能构建、初始化、诊断和安全启停的 .NET 10 运行时骨架。

包含：

- Solution、生产程序集和测试程序集；
- `global.json`、集中包版本与统一构建规则；
- 模块与配置节源生成器；
- `OpenCoWorkPaths` 与 `.opencowork` 目录体系；
- 默认、用户、工作区与 CLI 配置覆盖；
- ModuleRegistry、HostBuilder、WorkspaceRuntime；
- SQLite 迁移基础、PRAGMA 与 `state_info`；
- 日志与敏感字段脱敏；
- `opencowork --version`、`init`、`doctor`。

不包含：

- Session、Agent、工具和业务协议；
- 未被实际消费的工具函数生成器；
- 后续业务表。

完成信号：

- 全部项目干净构建；
- 生成器快照、配置优先级、主宿主选择和生命周期测试通过；
- `init` 能生成 `.opencowork`，`doctor` 能验证 SDK、路径、配置和 SQLite。

### M2 - Durable Session Core

目标：在没有真实模型和工具时交付可持久化、可并发、可恢复的 Session Core。

包含：

- Thread 创建、读取、重命名、暂停、恢复、归档和删除；
- Turn 创建、执行、完成、失败和取消；
- Item Started、Streaming 和 Completed；
- WaitingApproval 与 WaitingInput 状态；
- 同 Thread 串行、不同 Thread 并行；
- 队列、删除、重排和 Steer；
- 普通 Fork 与 Rollback；
- SessionEventChannel；
- ThreadJournal、回放、损坏尾部识别和 SQLite 投影；
- 确定性的 `ISessionExecutor` 测试实现。

不包含：

- 真实 Provider、AgentFactory 和真实工具；
- Worktree Fork；
- 完整 Goal 预算和 SubAgent。

完成信号：

- Journal 重放和 SQLite 投影重建一致；
- 并发、等待、取消、队列、归档和损坏尾部测试通过；
- 所有外部事件都在权威状态提交后发布。

### M3 - Agent Runtime Alpha

目标：交付没有真实工具但可以稳定多轮对话、恢复和压缩的 Agent Runtime。

包含：

- AgentSession、AgentFactory；
- Provider Registry、认证配置、Fake Provider 和首个真实 Provider；
- 流式响应、Reasoning、Usage 与允许的瞬态重试；
- 系统提示和运行时上下文组合；
- Token 跟踪与上下文窗口；
- Micro/Partial Compaction 与 Checkpoint；
- prompt-too-long 响应式压缩；
- Agent/Plan 模式基础状态。

不包含：

- 真实工具副作用；
- AppServer 和 ACP。

完成信号：

- CLI 可完成并恢复真实多轮对话；
- 首 Token 前后流中断具有不同且正确的重试行为；
- 压缩后历史可恢复，响应式压缩不重复当前 Turn；
- API Key 不进入日志、Journal 或事件。

### M4 - Tool Runtime Alpha

目标：让模型在稳定 Agent Runtime 之上安全产生副作用。

包含：

- Tool Definition、Runtime Binding、Registration；
- EffectiveToolSnapshot；
- Provider 工具名投影和反向映射；
- ToolInvocationPipeline；
- Authority、Schema、Policy、Hook、Approval；
- Timeout、Cancellation、结果归一化和审计；
- 稳定工具错误码；
- Agent/Plan 工具曝光差异；
- 最小 File、Shell、Web 与确定性测试工具。

不包含：

- MCP、插件与动态工具；
- AppServer 和 ACP。

完成信号：

- 管线每个阶段均有允许、拒绝、取消和异常测试；
- 拒绝调用也有 Started 与 Terminal 记录；
- Plan 模式不能调用写入或执行类工具；
- 模型重试不会重复工具副作用。

### M5 - OpenCoWork Wire Alpha

目标：通过独立 Protocol 程序集，为 OpenCoWork Desktop 暴露唯一 Session
Core，并同时交付稳定 ACP v1 兼容入口。

包含：

- JSON-RPC 2.0 基础设施；
- initialize、initialized 和 Capability 协商；
- Desktop 管理本地子进程，一进程绑定一个 Workspace；
- stdio JSONL 默认 Transport 与 loopback WebSocket 次要 Transport；
- `thread/*`、`turn/*`、`item/*` 核心方法和通知；
- history 分页、model/mode 切换和生成式 Wire Catalog；
- 原子快照、语义事件流与 afterSequence 重连；
- Approval、UserInput、Cancel、Queue 与 Steer；
- 连接级订阅和断连清理；
- ACP 稳定 v1 的 initialize、new、load、prompt、cancel 和 mode；
- Protocol TestClient 与契约快照测试。

不包含：

- 一次性复制原规范全部 214 个方法；
- 正式 Desktop Client SDK、daemon、远程监听与浏览器接入；
- ACP v2 草案、可选扩展和 draft elicitation；
- Skills、MCP、Teams 和 Automations 扩展域。

完成信号：

- Desktop 子进程从 initialize、history、subscribe、Turn 提交到终态的完整协议流
  通过；
- ACP 历史回放无重复；
- RPC cancel 与业务 Turn cancel 语义独立；
- 协议层没有第二套 Thread/Turn 状态。

### M6 - Capability Ecosystem

目标：安全加载和热更新外部能力，同时所有工具调用仍经过统一安全管线。

包含：

- Skills、变体、启停和提示注入；
- Plugin Manifest、安装、版本、来源和信任；
- 插件贡献的 Skills、Hooks、MCP、工具与协议扩展；
- MCP 生命周期、工具、资源、OAuth 和状态；
- LSP 生命周期与请求路由；
- Deferred Tool Loading；
- Runtime Dynamic Tools、Binding Lease 和断连失效；
- SourceControl 基础能力；
- 对应 Wire 扩展。

不包含：

- Teams、Automations 和 Gateway 编排。

完成信号：

- 未授信插件不能注册原生工具或可信 Hook；
- MCP 与动态工具断连后旧 Binding 立即失效；
- 工具冲突被隔离而不是按加载顺序覆盖；
- 插件卸载后下一回合使用新工具快照；
- 故障插件不阻止其他能力和运行时清理。

### M7 - Multi-Agent CoWork

目标：通过持久会话、任务 DAG、Mailbox、Artifact 和 Worktree 完成可恢复的
多 Agent 协作。

包含：

- SubAgent 父子关系和生命周期；
- Agent Profile、Team、Member 和 Mission；
- Leader Thread 与 Member Thread；
- MissionTask DAG、DependsOn、BlockedOn 和 Ready；
- Mailbox、Digest 和 Artifact；
- 并发、深度、预算和成员忙碌限制；
- Project 与 Worktree 执行空间；
- Leader 综合、Origin 回传和完成恢复；
- Teams Wire 扩展。

Mailbox 是多 Agent 内部的持久异步消息机制，用于补充要求、交接、阻塞、
审查、返工和 Artifact 引用。它不是 MissionTask、Thread 对话或 Artifact
存储的替代品。

不包含：

- 周期自动化；
- 外部渠道。

完成信号：

- DAG、成员互斥、预算和 Worktree 隔离正确；
- 完成通知丢失后 Reconciler 能恢复；
- Leader 只在必需任务完成后综合；
- Origin 只收到一次最终结果。

### M8 - Automations and Scheduler

目标：在无人守着终端时安全调度、执行和恢复任务。

包含：

- YAML 任务定义与 Schema；
- Fluid 模板；
- 手动与 Cron 触发；
- 明确时区和下一次运行；
- 定义文件监听；
- Pending、Running、NeedsAttention、Completed、Failed、Cancelled、TimedOut；
- 最大并发与单任务互斥；
- Project/Worktree 执行；
- Unattended Agent 规划范围；
- 运行超时、取消、崩溃恢复和周期重排；
- Automation Wire 扩展。

不包含：

- Gateway、Hub 和外部渠道。

完成信号：

- 重启不重复派发；
- 时区、并发、Worktree 和定义快照行为确定；
- 权限不会因无人值守而自动扩大；
- NeedsAttention 可经外部客户端恢复，也能按期限取消或超时。

### M9 - DeepSeek Responses Provider

目标：把既有通用 OpenAI-compatible Chat Completions Provider 收敛为
DeepSeek 专用 Responses API 实现，并形成 1.0 唯一真实 Provider 路径。

包含：

- DeepSeek-only Provider、Model 与 Auth 配置边界；
- 以 [DeepSeek 官方 Responses API 指南](https://api-docs.deepseek.com/guides/responses_api/)
  为协议权威，只实现现有运行时需要且官方明确支持的最小子集；
- 用 Responses API 替换 `OpenAiCompatibleChatClient` 的文本请求、SSE 流事件和
  错误映射；
- Content、Reasoning、Usage、Function Call/Output 与
  `response.completed` / `response.incomplete` / `response.failed` 终态；
- DeepSeek 服务端 `web_search` 与 `custom/apply_patch`，其中 Provider 工具同样
  受 EffectiveToolSnapshot、Authority、审计和 Journal 约束；
- 本地 `ThreadJournal` 权威历史、重启恢复、压缩和重试边界；
- 删除千问 Token Plan、其他 Provider 和通用 `openaiCompatible` 协议承诺；
- 删除模型侧本地 `web.fetch/CoreWebTool` 和整文件 `file.write` 注册；保留可复用的
  路径安全、原子提交、Approval 与 ToolInvocationPipeline；
- 首发模型 `deepseek-v4-flash` 的离线 Fixture、真实发布冒烟和 Secret Canary。

不包含：

- OpenAI、千问 Token Plan 或其他 OpenAI-compatible Provider；
- Chat Completions 或 Anthropic Messages 兼容层；
- DeepSeek 官方未记录或标记为不支持的 Responses API 能力，以及尚未被
  OpenCoWork 产品需求激活的结构化输出和其他 Tool Type；
- 尚未获得 DeepSeek 官方 Responses API 支持并完成真实验证的
  `deepseek-v4-pro`；
- Provider 托管历史成为本地恢复前提。

完成信号：

- 配置与 Capability Catalog 只声明 DeepSeek 和 `deepseek-v4-flash`；
- 官方 Responses 子集的流、Reasoning、Usage、Function、`web_search`、
  `custom/apply_patch`、三类终态、错误和重试通过离线故障注入；
- 本地 `web.fetch` 与模型侧 `file.write` 已退出 Catalog，且 Provider 工具没有
  绕过既有 Authority、Approval、审计或 Journal；
- 进程重启只依赖本地 Journal，可重建同一模型可见历史且不重复工具副作用；
- `deepseek-v4-flash` 在目标发布目录通过真实短冒烟、Usage 对账和 Secret
  Canary；
- 被移除的旧 Provider/协议配置产生稳定、可诊断的迁移错误。

### M10 - Gateway and Operations

目标：通过外部渠道长期接收任务、可靠投递结果，并提供用户级协调与后台运维。

包含：

- Gateway 主宿主与 Channel Adapter；
- 多 Channel 并发；
- Inbound 去重、Message Router 与 Outbound Outbox；
- 单外部会话顺序和 Dead Letter；
- 附件与外部媒体缓存；
- Channel 与 Thread 映射；
- Webhook/Test Channel；
- Hub、工作区发现和用户级配置；
- Heartbeat、Usage、Tracing 和 Dashboard 查询；
- 后台反思/改进建议能力；
- 后台服务统一生命周期。

不包含：

- 桌面或 Web UI；
- 新的大型核心子系统。

完成信号：

- 重复外部消息只创建一个 Turn；
- 发送前后崩溃不静默丢失消息；
- Channel 断连隔离、顺序、媒体路径和凭据保护正确；
- Hub 不依赖当前工作目录；
- 后台服务可按 WorkspaceRuntime 生命周期完整清理。

### M11 - OpenCoWork 1.0 Closure

目标：不再增加大型子系统，关闭契约缺口并形成可发布产品。

独立设计：
[M11 Runtime 1.0 Closure 设计规格](2026-08-02-open-cowork-m11-runtime-1-0-closure-design.md)。
施工顺序与 Commit 边界见
[M11 Runtime 1.0 Closure 实施计划](../plans/2026-08-02-open-cowork-m11-runtime-1-0-closure-implementation-plan.md)。

包含：

- M0 能力台账逐项关闭；
- Protocol 方法、DTO、配置 Schema 和默认值审查；
- SQLite Migration 和 ThreadJournal Schema v1 真实历史 Corpus 回放；
- DeepSeek-only Provider、Plugin、MCP/LSP 最小兼容矩阵；
- CLI、AppServer、ACP、Gateway 端到端测试；
- 安全、故障注入、现有固定负载和每平台两小时 Soak；
- 未签名自包含包的安装、升级、卸载与双平台发布；
- 用户、协议和插件开发文档；
- Release Notes、SBOM 与校验和。

不包含：

- 临门新增大型功能；
- Linux 或 Intel macOS 正式支持。
- Windows/macOS 代码签名与 macOS Notarization。

完成信号：

- 没有未解释的契约缺口和 P0/P1 缺陷；
- 所有迁移、恢复和故障场景有自动化证据；
- `win-x64` 与 `osx-arm64` 在干净机器通过未签名自包含包安装和
  `deepseek-v4-flash` 真实模型冒烟；
- 公开协议和插件契约开始遵守 SemVer。

## 10. 发布阶段

| Slice | 对外阶段 |
| --- | --- |
| M1 | Foundation Preview |
| M2 | Developer Preview |
| M3 | CLI Alpha |
| M4 | Agentic Alpha |
| M5 | Protocol Alpha |
| M6 | Extension Beta |
| M7 | CoWork Beta |
| M8 | Unattended Beta |
| M9 | Provider Beta |
| M10 | Release Candidate |
| M11 | OpenCoWork 1.0 |

## 11. 后续工作规则

- 每个 M1-M11 在实施前必须有独立规格和可恢复计划；
- 每个 Slice 只在完成硬验收并具备交付证据后标记 Done；
- CHECKLIST 只维护 Slice 状态和资产链接，不写任务级施工步骤；
- 新发现的能力不能直接塞进当前 Slice，必须判断属于当前边界、未来 Slice、
  技术债还是 1.x 后续；
- M11 只允许收口，不接受新大型子系统。
