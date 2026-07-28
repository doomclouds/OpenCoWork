# OpenCoWork M4 Tool Runtime Alpha 实施计划

**Status:** Completed；Outcome 1-9 已完成。

**Goal:** 在 M3 Agent Runtime 上交付 Provider 中立、可审计、可审批、可取消、
可恢复且不会重复已提交副作用的最小 Tool Runtime。

**Why planning is required:** M4 同时修改公共 Tool/Agent/Session 契约、SQLite
Schema v4、ThreadJournal 事实、Provider 流协议、模型历史与 Compaction、Authority
和审批、机密信息边界、跨平台文件/进程/网络执行以及崩溃恢复，属于跨模块、数据迁移、
公共契约和安全敏感工作，必须按依赖闭包推进。

**Acceptance:** `M4-ACC-001` 至 `M4-ACC-010` 均有可定位的自动化、故障注入、
真实平台证据或明确延期结论；默认测试完全离线；同一 Provider Tool Call、重试和
恢复不会重复已提交副作用；Secret 不进入 Journal、SQLite、Session Event、
Provider Tool Message、日志、stdout、stderr 或测试产物；最终只生成一份 M4
交付归档。

**Closeout boundary update（2026-07-28）:** 用户明确接受关闭 M4 功能需求，并将
`M4-ACC-006`、`M4-ACC-009` 的 `win-x64` 真机部分保留在
`docs/platform-release-validation-ledger.md` 后续集中补验。两项状态为
`Deferred`，Windows 平台行为仍为 `Pending`，不得解释为已通过；该决定只调整 M4
关闭时点，不改变工具安全契约，并且不豁免 M10 的双平台发布候选验收。

**Supplemental validation（2026-07-29）:** Windows 11 x64 真机补验已完成，
`M4-ACC-006`、`M4-ACC-009` 和平台行均改为 `Passed`；M10 最终发布候选复验
仍保留。

## Source Documents

- [M4 Tool Runtime Alpha 设计规格](../specs/2026-07-28-open-cowork-m4-tool-runtime-alpha-design.md)
- [M3 Agent Runtime Alpha 设计规格](../specs/2026-07-27-open-cowork-m3-agent-runtime-alpha-design.md)
- [M3 Agent Runtime Alpha 实施计划](2026-07-27-open-cowork-m3-agent-runtime-alpha-implementation-plan.md)
- [M2 Durable Session Core 设计规格](../specs/2026-07-26-open-cowork-m2-durable-session-core-design.md)
- [M0 Contract Freeze](../specs/2026-07-25-open-cowork-m0-contract-freeze-design.md)
- [OpenCoWork Runtime 1.0 路线规格](../specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md)
- [M0 能力台账](../specs/2026-07-25-open-cowork-m0-capability-ledger.md)
- [M0-M10 验收目录](../specs/2026-07-25-open-cowork-m0-acceptance-catalog.md)
- 本机证据基线：`DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md`

本计划中的 Outcome 是 M4 内部依赖结果，不是独立 Slice。Outcome 可以作为提交
边界，但不得创建独立规格、独立归档或提前把 M4 标记为 Done。

## Change Map

优先复用 M1 配置/日志/路径、M2 Session/Journal/Projection/Interaction 和 M3
AgentFactory/Provider/Compaction。不得新增项目、第二套 Session 状态机、Tool Store、
事件总线、后台执行框架或未来扩展脚手架。

### 计划新增

| 路径 | 职责 |
| --- | --- |
| `src/OpenCoWork.Abstractions/ToolContracts.cs` | Tool Definition/Binding/Registration 身份、Effect/Authority、EffectiveToolSnapshot、Invocation/Result、ReplaySafety、稳定错误码和最小公共 DTO。 |
| `src/OpenCoWork.Core/Configuration/ToolsConfig.cs` | 用户与工作区 Effect 策略、严格收窄规则和 Generated Schema 约束；不承载大小/超时配置。 |
| `src/OpenCoWork.Core/Tools/ToolRuntime.cs` | CoreNative Registration、Snapshot Builder、名称投影、Schema 编译、Authority 计算和进程内 Binding 查找。 |
| `src/OpenCoWork.Core/Tools/ToolInvocationPipeline.cs` | 固定安全阶段、审批续接、内部 Hook delegate、Attempt/Terminal 提交、去重、恢复和结果归一化。 |
| `src/OpenCoWork.Core/Tools/CoreTools.cs` | 五个 Core Binding 的最小实现；允许在文件过长时按 File/Shell/Web 拆分，但不得增加单实现接口或工厂。 |
| `tests/OpenCoWork.Core.Tests/ToolContractTests.cs` | 公共 DTO、稳定枚举/错误码、Canonical JSON、大小上限和序列化边界。 |
| `tests/OpenCoWork.Core.Tests/ToolSnapshotTests.cs` | 来源身份、名称碰撞、Schema、Authority、Agent/Plan 曝光和快照冻结。 |
| `tests/OpenCoWork.Core.Tests/ToolInvocationPipelineTests.cs` | 固定阶段、拒绝矩阵、审批、Hook、超时、结果归一化、去重和恢复故障注入。 |
| `tests/OpenCoWork.Core.Tests/CoreToolTests.cs` | File/Shell/Web 参数、路径、进程、网络、取消、脱敏和输出限制。 |
| `tests/OpenCoWork.IntegrationTests/ToolRuntimeIntegrationTests.cs` | 双平台真实文件/进程、完整 Provider Tool Loop、重启恢复和副作用唯一性证据。 |

### 计划修改

| 路径 | 修改目的 |
| --- | --- |
| `Directory.Packages.props` | 只增加冻结版本 `JsonSchema.Net 9.4.0`。 |
| `src/OpenCoWork.Core/OpenCoWork.Core.csproj` | 引用 `JsonSchema.Net`，不增加其他生产依赖。 |
| `src/OpenCoWork.Abstractions/AgentContracts.cs` | Tool Role、Assistant Tool Call、Tool Delta/Frame、请求工具定义和工具感知 Compaction Checkpoint v2。 |
| `src/OpenCoWork.Abstractions/SessionContracts.cs` | ToolCall/ToolResult Item、工具事件、Tool Invocation 执行意图和 Approval 关联。 |
| `src/OpenCoWork.Core/Configuration/ConfigLoader.cs` | 合并和校验用户/工作区 Tool Effect 策略，工作区只能收窄。 |
| `src/OpenCoWork.Core/Logging/StructuredLogging.cs` | 在现有 `SecretRedactor` 上增加 JSON 结构化遍历，不创建第二个脱敏器。 |
| `src/OpenCoWork.Core/Agents/AgentRuntime.cs` | AgentFactory 冻结工具快照，AgentRuntimeExecutor 执行串行 Tool Loop、Frame Preflight、恢复和 Context Budget。 |
| `src/OpenCoWork.Core/Agents/OpenAiCompatibleChatClient.cs` | 严格解析/发送 OpenAI-compatible Tool Calls 和 Tool Messages。 |
| `src/OpenCoWork.Core/Agents/CompactionCheckpointIntegrity.cs` | 工具消息组、Source SHA-256 和 v1/v2 Checkpoint 兼容边界。 |
| `src/OpenCoWork.Core/Sessions/SessionFacts.cs` | 三个 Tool Invocation Fact、ToolCall/ToolResult 单一载荷引用和 Approval 关联。 |
| `src/OpenCoWork.Core/Sessions/SessionDomain.cs` | Tool Item、Invocation 与唯一终态的内存聚合约束。 |
| `src/OpenCoWork.Core/Sessions/SessionExecution.cs` | 新 Intent 的 Journal→Aggregate→Projection→Event 提交、等待续接和历史回放。 |
| `src/OpenCoWork.Core/Sessions/SessionProjection.cs` | `tool_invocations` 查询投影、Schema v4 应用、重建、Fork 和 Rollback。 |
| `src/OpenCoWork.Core/Sessions/SessionRecovery.cs` | 未终态 Tool Invocation 的 Safe/Unsafe 恢复和补齐优先级。 |
| `src/OpenCoWork.Core/Sessions/SessionRuntime.cs` | Tool Runtime/Pipeline/Core Binding 的最小 DI 组合与停止顺序。 |
| `src/OpenCoWork.Core/State/StateRuntime.cs` | v3→v4 迁移、结构快照、索引/外键验证和失败恢复。 |
| `src/OpenCoWork.Core/Workspaces/WorkspacePaths.cs` | 仅补充 Core File/Shell 需要的路径复验与 Blacklist 复用入口。 |
| `src/OpenCoWork.App/ChatCommandRunner.cs` | 显示精确 Tool Approval 请求和脱敏 Tool 终态；仍只调用 `ISessionService`。 |
| `tests/OpenCoWork.Core.Tests/` | 扩展 Agent、Session、State、Configuration、Logging、WorkspacePath 和 Compaction 累计回归。 |
| `tests/OpenCoWork.IntegrationTests/` | 扩展宿主组合、崩溃恢复和双平台真机 Tool Runtime 验证。 |
| `tests/OpenCoWork.ArchitectureTests/ProjectGraphTests.cs` | 守卫唯一新增包、零新增项目、契约归属和禁止的扩展依赖。 |

## Execution Rules

- 开始实现前确认分支仍为 `dev`，保留用户已有的规格和里程碑改动；
- 每个 Outcome 先建立能够失败的聚焦测试，再实现到该 Outcome 验收信号通过；
- 默认测试不访问公网、不读取真实凭据、不修改测试临时工作区以外的文件或外部状态；
- 使用现有 xUnit v3、BCL、`System.Text.Json`、`HttpClient`、`TimeProvider`、
  `Process`、SQLite 和唯一新增的 `JsonSchema.Net 9.4.0`；
- Provider Tool 协议测试复用现有 BCL Loopback Server；Web 成功路径使用内部
  DNS/连接测试缝，生产路径仍必须拒绝 Loopback/Private/Metadata 目标；
- Tool Registration、Snapshot Builder、AgentFactory、Pipeline 和 Core Binding
  保持具体内部实现；不得增加单实现 Registry/Factory、配置化 Limits 或公共 Hook；
- 每个 Turn 只使用已写入 `AgentInvocationSnapshot` 的 EffectiveToolSnapshot；
  Attempt、审批恢复和 Provider 下一轮不得重新读取注册、环境或配置；
- Journal 仍是 ToolCall、ToolResult、Invocation、审批和恢复的权威源；SQLite 只做
  可重建查询投影，安全参数和结果载荷分别只写入一个 Item；
- Journal Flush 后的副作用和终态不得因 Projection/Event 失败被当作未提交；
- 多 Tool Call Frame 必须先完整 Preflight，再按 Provider Index 串行执行；不得为
  “看起来只读”的 Shell、GET 或模型声明降低 Effect/ReplaySafety；
- 任一 Outcome 发现需要改变冻结的阶段顺序、错误码、默认 Authority、限制常量、
  ReplaySafety、单一载荷归属或验收矩阵，立即停止实现并先修订 M4 设计规格；
- 未通过当前 Outcome 聚焦测试和已有累计回归，不进入下一个 Outcome；
- M4 不实现 MCP、Plugin、动态/延迟工具、公共 Hook API、后台工具、Sandbox、
  Node REPL、AppServer、ACP、SourceControl 或永久授权。

### Outcome 1: Tool/Agent/Session 契约、配置和 Schema v4 形成可编译基线

- Work:
  - 在 `ToolContracts.cs` 定义冻结的 ToolSourceKind、ToolDefinitionId、
    RuntimeBindingId、Definition/Binding/Registration、Audience/Exposure、
    ToolEffect、Authority Decision、ReplaySafety、Invocation Status、
    EffectiveToolSnapshot、ToolResultSnapshot 和 `ToolErrorCodes`。
  - 扩展 `AgentContracts.cs` 的消息角色、Assistant Tool Call、Tool Message、
    Tool Delta/Completed 事件和请求工具定义；Provider 私有 JSON 不进入
    Abstractions。
  - 扩展 `SessionContracts.cs` 的 ToolCall/ToolResult Item Content、四个 Session
    Event、执行 Intent 和 Approval 的可空 Tool Invocation ID；不增加第二个 Sink。
  - 增加 `tools.effects` 最小配置：`NetworkRead`、`WorkspaceWrite`、
    `ProcessExecution` 可为 Allow/RequireApproval/Deny，`ExternalMutation` 只能
    RequireApproval/Deny；工作区配置不得放宽用户配置。
  - 在中央包管理和 Core 项目只加入 `JsonSchema.Net 9.4.0`。
  - 将 `StateMigrations` 推进到 v4，只新增 `tool_invocations` 表及冻结索引/外键；
    同一事务无损重建 `items.item_type` 闭集约束以加入 ToolCall/ToolResult，并
    同步重建 `pending_interactions` 以保持其 Item 外键与既有行；新库直接到 v4，
    v3→v4 继续使用现有备份、事务 DDL、结构校验和恢复。
  - 建立契约、配置、序列化、迁移和项目图测试，使其在没有 Tool Runtime 行为时先
    形成可编译基线。
- Risks/open questions:
  - Snapshot、Item、Fact、Event、错误和配置不得包含 Secret、原始异常、绝对路径、
    命令、URL、Header 或未经脱敏的参数/结果。
  - v3→v4 任一步失败必须恢复 v3 备份并阻断启动，不能留下半张表、孤立索引或部分
    `user_version`。
  - 不为 M5/M6 增加 Dynamic、Deferred、MCP、Plugin 或公共 Hook 占位类型。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter "FullyQualifiedName~ToolContractTests|FullyQualifiedName~ConfigurationPipelineTests|FullyQualifiedName~SessionContractTests|FullyQualifiedName~StateRuntimeTests"`
  - `dotnet test tests/OpenCoWork.ArchitectureTests/OpenCoWork.ArchitectureTests.csproj -c Release`
  - `dotnet build OpenCoWork.slnx -c Release`
- Acceptance contribution:
  - `M4-ACC-001`
  - `M4-ACC-003`
  - `M4-ACC-004`

### Outcome 2: Core Registration 与 EffectiveToolSnapshot 确定性冻结

- Work:
  - 在 `ToolRuntime.cs` 建立五个固定 CoreNative Definition/Binding/Registration，
    使用稳定 Source/Definition/Binding ID；此 Outcome 的 Binding 可以返回确定性
    测试结果，不提前实现真实 File/Shell/Web。
  - 使用 `JsonSchema.Net` 按 Draft 2020-12 编译 Definition Schema，只允许本地
    Fragment `$ref`；注册时隔离无效 Schema、未知 Vocabulary、外部引用和冲突定义。
  - 实现 canonical name 校验、`namespace__name` Provider 投影、确定性 hash 后缀、
    双向名称映射和冲突隔离；不增加 Provider 专用命名表。
  - Snapshot Builder 按 Source、Audience、Exposure、Effect、AgentMode 和 Authority
    上限生成 Canonical EffectiveToolSnapshot，并执行 64 KiB/1 MiB 固定边界。
  - AgentFactory 将 Snapshot 嵌入 AgentInvocationSnapshot；Response 使用冻结定义，
    Compaction 始终使用空工具集；进行中的 Turn 不读取后续注册变化。
  - Plan 只保留 `None`、`WorkspaceRead`、`NetworkRead` 子集，禁止
    WorkspaceWrite/ProcessExecution/ExternalMutation；只读 Web 仍遵守 Authority。
- Risks/open questions:
  - Provider 名称冲突必须隔离定义或使用冻结 hash 投影，不能后注册覆盖先注册。
  - `ExternalMutation` 永远不能提升为 Allow；Shell Definition 从首次暴露起就声明
    全部潜在 Effect。
  - Snapshot 构建失败必须在首次 Provider 请求前终止，不能延迟到调用时补救。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter "FullyQualifiedName~ToolSnapshotTests|FullyQualifiedName~AgentFactoryTests|FullyQualifiedName~ToolContractTests"`
  - `dotnet test tests/OpenCoWork.ArchitectureTests/OpenCoWork.ArchitectureTests.csproj -c Release`
- Acceptance contribution:
  - `M4-ACC-001`
  - `M4-ACC-002`
  - `M4-ACC-008`

### Outcome 3: Tool Journal、单一载荷 Item 和 v4 投影可重建

- Work:
  - 在 `SessionFacts.cs` 增加 Started、AttemptStarted、Terminal 三个 Invocation
    Fact；Started 只引用 ToolCall Item ID/Call Index，Terminal 只引用 Result Item
    ID 和摘要，不复制参数或 ToolResultSnapshot。
  - 增加完整 ToolCall Frame 的完成态 Item 提交，以及 Terminal 与 ToolResult Item
    同一 Journal Entry 的原子提交；安全参数只在 ToolCall Item，结果只在
    ToolResult Item。
  - 扩展 SessionExecution/Domain/Event，使每个 Invocation 恰有一个 Started、
    零至两个 Attempt 和至多一个 Terminal，并维持
    Journal→Aggregate→Projection→Event 顺序。
  - 投影 `tool_invocations` 的身份、状态、Attempt Count、Result Item、Error 和时间；
    Provider Call ID 冲突保留独立审计行，不建立错误的唯一约束。
  - Projection Rebuild 从 Journal 恢复完全相同的查询表；Fork/Rollback 只复制完成
    ToolCall/ToolResult 历史，不复制未终态 Invocation、Approval 或 Attempt。
  - 扩展 History/Event 读取和结构校验；缺失引用、重复/越序 Result、非法 Attempt
    或双 Terminal 使 Thread 进入 RecoveryRequired。
- Risks/open questions:
  - 同一 Journal Entry 内的 Terminal Fact 和 ToolResult Item 必须共享一个结果载荷
    所有者，不能为了投影便利再次序列化结果。
  - 投影失败不能回滚已 Flush 的 Journal；恢复后必须先补投影，再接受新工作。
  - v4 Rebuild、Fork 和 Rollback 必须保持 Provider Message 组顺序与 Item ID 引用。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter "FullyQualifiedName~SessionExecutionTests|FullyQualifiedName~SessionProjectionTests|FullyQualifiedName~SessionRecoveryTests|FullyQualifiedName~StateRuntimeTests"`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter FullyQualifiedName~SessionCrashRecoveryIntegrationTests`
- Acceptance contribution:
  - `M4-ACC-003`
  - `M4-ACC-005`
  - `M4-ACC-010`

### Outcome 4: ToolInvocationPipeline 固定安全阶段与恢复规则闭环

- Work:
  - 实现唯一 `IToolInvocationPipeline.InvokeAsync`，严格执行 Snapshot Lookup、
    Started、Audience/Exposure/Mode、Binding/Lease、Authority、Input Schema、
    Policy、PreToolUse、Approval、Attempt、Timeout Invoke、Normalize、Terminal 和
    Terminal Hook 顺序。
  - 所有前置拒绝均提交 Started 与单一 Rejected Terminal；未找到 Definition 时
    保留 Provider Tool Call ID/Name，身份字段为空。
  - Effective Authority 取内置默认、用户、工作区、Thread/Mode、Snapshot 和运行时
    限制的最严格交集；Approval 只能处理 RequireApproval，且绑定精确 Invocation、
    Snapshot SHA-256 和脱敏参数摘要。
  - 复用 M2 WaitForInteraction/Checkpoint 完成审批等待与续接；Checkpoint 只保存
    冻结引用和阶段游标，Waiting 时间不消耗 30 分钟活动预算。
  - 只提供两个可空 internal delegate：PreToolUse 可 Deny/RequireApproval/
    TimeoutCap，Terminal 只在提交后观察；产品组合均为 null。
  - Frame Preflight 后优先拒绝 SensitiveInputDetected，再执行 JSON Schema；
    Pipeline 统一处理异常、SecretRedactor JSON 遍历、Canonical Result、256 KiB
    头尾收缩、1 MiB 硬失败和稳定 Terminal 映射。
  - 持久化 Attempt 后才把控制权交给 Binding；Safe 未终态重放一次，Unsafe 直接
    OutcomeUnknown，Safe 再次中断同样 OutcomeUnknown。
  - 同一 Provider Call ID/Name/参数摘要重复时回放原终态；内容冲突形成新的
    Rejected 调用，不覆盖原映射。
- Risks/open questions:
  - Hook、Approval、模型或恢复路径都不能扩大 Effect/Authority、跳过阶段或修改
    已提交结果。
  - 显式取消、Deadline、Binding 已返回和无法确认结果的竞态必须只选择一个终态。
  - 恢复必须使用 ToolCall Item 中的安全参数和冻结 Snapshot，不能重新读取当前
    Registration、配置或原始机密。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter "FullyQualifiedName~ToolInvocationPipelineTests|FullyQualifiedName~SessionExecutionTests|FullyQualifiedName~StructuredLoggingTests"`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter FullyQualifiedName~SessionCrashRecoveryIntegrationTests`
- Acceptance contribution:
  - `M4-ACC-003`
  - `M4-ACC-004`
  - `M4-ACC-005`
  - `M4-ACC-006`
  - `M4-ACC-007`
  - `M4-ACC-010`

### Outcome 5: Provider Tool 协议、消息历史和工具感知 Compaction 严格有界

- Work:
  - 扩展 `ChatCompletionRequest` 和 OpenAI-compatible 请求 JSON，只在 Response
    携带冻结 Tool Definitions；Compaction 不携带工具，也不发送 Provider 私有扩展。
  - 严格解析 Tool Call Delta 的 Index、ID、Name 和 Arguments；保持 M3 SSE/UTF-8/
    Body 上限，只有完整 `[DONE]`、ToolCall Finish Reason 和完整 Frame 才交给
    AgentRuntimeExecutor。
  - 支持 Assistant Tool Call 与 `role=tool/tool_call_id` 历史；被 ToolCall Item
    引用的 AgentMessage 不再生成第二条 Assistant Message。
  - 每个 Response Round 使用独立 AgentMessage/Reasoning/ToolCall Item，且
    AgentMessage+ToolCall+全部 ToolResult 构成不可拆分消息组。
  - Token 预算覆盖 Definition、Provider Name、Call ID、参数、Result Envelope 和
   协议开销；结果在 Terminal 提交前收缩到下一轮请求可接受的唯一 Snapshot。
  - Compaction Checkpoint 升级到 v2，Source SHA-256 覆盖完整工具消息组；v1 只在
    Source Range 无 ToolCall/ToolResult 时接受，未完成组不得进入 Source。
  - 扩展 BCL Loopback Server 测试分片 Arguments、多 Call、未知/重复 Index、
    不完整 Frame、Tool Role 历史、重定向/错误 Body Canary 和工具定义上限。
- Risks/open questions:
  - Provider 已产生 Content/Reasoning 或当前 Turn 已提交工具副作用后，任何协议或
    网络错误都不能重试当前步骤。
  - Frame 损坏/超限必须在执行任何工具前整体失败；机密命中只拒绝受影响调用，并
    持久化脱敏后的完整 Frame。
  - 最小 Result Envelope 仍超窗时必须先提交真实 Tool Terminal，再以
    `context.inputTooLarge` 失败 Turn。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter "FullyQualifiedName~ChatCompletionClientTests|FullyQualifiedName~AgentFactoryTests|FullyQualifiedName~CompactionTests|FullyQualifiedName~ToolContractTests"`
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter FullyQualifiedName~StructuredLoggingTests`
- Acceptance contribution:
  - `M4-ACC-002`
  - `M4-ACC-006`
  - `M4-ACC-010`

### Outcome 6: AgentRuntimeExecutor 串行 Tool Loop、Checkpoint 和副作用去重闭环

- Work:
  - 在 `AgentRuntimeExecutor` 中按 Provider Round 累积完整 Tool Frame，执行全帧
    Preflight，先提交关联 AgentMessage/ToolCall Item，再按 Index 串行调用 Pipeline。
  - 全部 ToolResult 按原 Provider Call ID 回注后，使用同一
    AgentInvocationSnapshot/EffectiveToolSnapshot 继续 Provider Response，直至正常
    终答或 64 个 ToolCall Round。
  - 同一 Turn 不设 Tool Call 数量上限，但 Round 64 后以
    `tool.iterationLimitExceeded` 失败；既有 Item、终态和副作用完整保留。
  - 将 Provider、Compaction、Pipeline 和 Tool Invocation 纳入同一 30 分钟活动
    预算；审批等待暂停活动预算，恢复后继承剩余值。
  - Checkpoint 恢复先完成 Waiting Approval、Safe Replay 或 OutcomeUnknown，再发起
    下一次 Provider 请求；无有效工具游标继续使用既有 RuntimeInterrupted。
  - Duplicate Tool Call 只回放持久化 ToolResultSnapshot；冲突 ID、崩溃点和
    Projection 延迟均不得再次调用 Unsafe Binding。
  - 使用 Fake Client、TimeProvider、可控 Binding 和副作用计数器覆盖多 Round、
    多 Call、取消/超时、重试边界、重启和唯一 Turn 终态。
- Risks/open questions:
  - 每个 Round 的流式 Item ID、内容缓冲和 Usage 必须独立，不能跨轮追加或重复计数。
  - ToolCall Frame 已提交后即属于可见历史，即使 Binding 尚未开始也不能重试
    Provider 步骤。
  - 恢复补齐期间不得并发调度队列中的下一 Turn。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter "FullyQualifiedName~AgentRuntimeExecutorTests|FullyQualifiedName~ToolInvocationPipelineTests|FullyQualifiedName~SessionExecutionTests"`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~SessionCrashRecoveryIntegrationTests|FullyQualifiedName~ToolRuntimeIntegrationTests"`
- Acceptance contribution:
  - `M4-ACC-006`
  - `M4-ACC-007`
  - `M4-ACC-010`

### Outcome 7: File Core Tools 在工作区边界内原子且可重放

- Work:
  - 实现 `file.list`、`file.read` 和 `file.write` 的固定 Schema、CoreNative ID、
    ReplaySafety、Effect 和 30 秒默认 Timeout，并接入 Outcome 2 Registration。
  - 所有路径只接受 Workspace 相对路径，复用 `WorkspacePathGuard` 的逻辑/物理/
    现存父级/符号链接检查和写前复验；实现冻结的 `.git`、`.opencowork/runtime`、
    local config 和 `.opencowork` Blacklist。
  - `file.list` 只枚举一层、Ordinal 排序、不展开链接，并隐藏被 Blacklist 拒绝的
    名称。
  - `file.read` 严格 UTF-8，支持行窗口，返回完整文件 SHA-256；拒绝二进制、目录、
    无效 UTF-8 和超限内容。
  - `file.write` 只做 UTF-8 整文件创建/覆盖；覆盖要求 expectedSha256，创建要求目标
    不存在，使用同目录临时文件、Flush、路径复验和原子 Replace/Rename。
  - 使用测试临时工作区覆盖软链接逃逸、大小写、并发修改、Precondition、原子性、
    取消、清理和崩溃前后副作用计数。
- Risks/open questions:
  - Path Blacklist 和路径比较必须使用平台正确的大小写语义，且错误不能泄漏绝对路径。
  - Temp 写入失败或取消必须清理；无法确认 Rename 是否提交时由 Pipeline 标记
    OutcomeUnknown，不能自动重试。
  - 不增加 Append/Delete/Move/Mkdir/Patch/Base64 或递归遍历。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter "FullyQualifiedName~CoreToolTests|FullyQualifiedName~WorkspacePathTests|FullyQualifiedName~ToolInvocationPipelineTests"`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter FullyQualifiedName~ToolRuntimeIntegrationTests`
- Acceptance contribution:
  - `M4-ACC-006`
  - `M4-ACC-007`
  - `M4-ACC-008`
  - `M4-ACC-009`
  - `M4-ACC-010`

### Outcome 8: Shell/Web Core Tools 与生产组合满足跨平台安全边界

- Work:
  - 实现 `shell.run` 的固定 Schema、10 分钟默认 Timeout、Unsafe ReplaySafety 和
    全部潜在 Effect；始终以完整命令逐次审批，不解析命令推断更低权限。
  - macOS 使用 `/bin/zsh -lc`；Windows 优先 `pwsh`，缺失时回退
    `powershell.exe`；只接受相对 workingDirectory，不接受 stdin、PTY、后台、
    Shell 选择、自定义环境或会话复用。
  - 子进程移除冻结 Provider Credential 和敏感名称环境变量，异步排空 stdout/
    stderr；取消、超时或输出超限时先
    `Process.Kill(entireProcessTree: true)` 并等待退出。
  - 实现 `web.fetch` 的无认证 GET/HEAD 和 2 分钟默认 Timeout；拒绝 Body、Header、
    Cookie、Credential、UserInfo 和非 HTTP(S)，默认 RequireApproval。
  - 每次请求和最多五次重定向都重新验证目标；拒绝 Loopback、Private、Link-local、
    Multicast、Unspecified、文档保留和 Metadata 地址，并把连接绑定到已校验解析
    结果以防 DNS Rebinding。
  - Web 只返回冻结文本媒体类型，按流式硬限制、统一脱敏和 ToolResultSnapshot 上限
    处理；HTTP 4xx/5xx 是 Completed 结构化结果。
  - 在 SessionRuntime/AgentRuntime 的现有 DI 扩展中组合 Tool Runtime、Pipeline、
    五个 Binding 和 null Hook；Chat CLI 复用 M2 Approval，不新增工具命令入口。
  - 单元测试使用受控 DNS/连接 seam 和本地服务模拟成功/重定向/慢流/大 Body；真实
    Loopback 地址仍必须被生产校验拒绝。
- Risks/open questions:
  - Approval 不是 Sandbox；Shell 测试只能在临时工作区执行受控命令，不得宣称进程
    获得 OS 级文件系统隔离。
  - `Process.Kill(entireProcessTree: true)` 只有在双平台残留测试失败时才允许增加
    Job Object/Process Group，不能预先实现平台进程管理层。
  - Web 校验后不能让默认连接逻辑再次解析 Host；TLS SNI/Host 仍必须保持原目标。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter "FullyQualifiedName~CoreToolTests|FullyQualifiedName~ToolInvocationPipelineTests|FullyQualifiedName~StructuredLoggingTests"`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter FullyQualifiedName~ToolRuntimeIntegrationTests`
  - 在 `osx-arm64` 和 `win-x64` 分别确认 Shell Host、取消后进程树、Web SSRF/重定向和输出上限证据
- Acceptance contribution:
  - `M4-ACC-005`
  - `M4-ACC-006`
  - `M4-ACC-008`
  - `M4-ACC-009`

### Outcome 9: M4 验收矩阵、双平台证据和统一交付收口

- Work:
  - 建立 `M4-ACC-001` 至 `M4-ACC-010` 的测试到证据映射，确认每项至少有冻结规格
    要求的 Contract、Security、FaultInjection 或 RealPlatform 证据。
  - 完整回归 v3→v4 迁移、Projection Rebuild、Fork/Rollback、重复 ID、审批恢复、
    Safe/Unsafe 断连、64 Round、Context Budget 和副作用计数。
  - 在 Apple Silicon macOS 完成完整离线测试、ToolRuntimeIntegrationTests 和发布
    目录 File/Shell/Web 实跑；`win-x64` 真机部分按关闭边界变更登记为后续集中补验，
    不以 cross-publish 代替真实平台证据。
  - 对 Journal、SQLite、Session Event、Provider Tool Message、日志、stdout、
    stderr 和测试临时目录执行 Secret Canary 扫描；命中即阻断交付。
  - 运行架构测试确认零新增项目、只有 `JsonSchema.Net 9.4.0` 一个 M4 新依赖，且
    没有 MCP/Plugin/动态工具/公共 Hook/Tool Store 等延期能力。
  - M4 不要求新增真实 Provider 支持证据；只有用户按
    `docs/provider-validation-backlog.md` 显式激活 Provider/Model/平台时，才增加
    独立真实 Tool Call 发布验证。
  - 自动化和 macOS 证据满足、Windows 延期已明确登记后，创建唯一 M4 交付归档并
    更新 Milestone CHECKLIST/INDEX；未执行的真实平台证据必须保持
    `Deferred` / `Pending`。
- Current validation evidence（2026-07-28）:
  - 产品实现基线为提交 `d236f29`；Apple Silicon macOS 26.5.2、
    `osx-arm64`、.NET SDK `10.0.302`、Runtime `10.0.10`。
  - 完整离线回归为 Core `218`、Integration `22`、Generators `14`、
    Architecture `5`，合计 `259` passed / `0` failed；显式真实 Provider Runner
    按既有设计跳过，Protocol 测试项目仍无可发现测试。
  - Release build 为 `0` warning / `0` error；`osx-arm64` 与 `win-x64`
    framework-dependent publish 均成功，后者仅是交叉发布证据。
  - `osx-arm64` 发布物确认为 Mach-O arm64；发布目录经本地 Fake Provider 和真实
    CLI 审批链依次完成 `file.write`、`shell.run` 与 `web.fetch` 私网拒绝。
    File 原子创建内容为 `file-ok`；Shell 宿主为 `/bin/zsh`、退出码为 `0`、
    stdout 为 `missing|shell-ok`；Web 返回稳定
    `tool.networkTargetDenied`，三项结果均进入后续 Provider Tool Message。
  - Secret Canary 未命中该发布 Smoke 的 Journal、SQLite、Session Event、Provider
    请求体、日志、stdout/stderr 或测试目录；Shell 进程确认未继承冻结 Credential。
  - `win-x64` 真机完整回归、PowerShell Host、输出超限/取消后的进程树残留和发布
    目录 File/Shell/Web Smoke 尚未执行；用户已接受将 `M4-ACC-006`、
    `M4-ACC-009` 标记为 `Deferred` 并保留平台行 `Pending`，因此 M4 功能需求关闭，
    但不得宣称 Windows 真机通过。
- Supplemental validation（2026-07-29）:
  - Windows 11 Home `10.0.26200` x64、.NET SDK `10.0.302`、Runtime
    `10.0.10`；基线
    `9cf7e1e366d04fd63ac55906924ea0dde630321d`，Source/Test Patch SHA-256
    `848ec5c02b1ef9be5afc7d9e1ffeccfa74539d3d2978b09fa9aa6f96438b1725`。
  - Release 全量回归 `280` passed / `0` failed / `0` skipped，build
    `0` warning / `0` error；Shell/进程树专项 `4` passed，CLI
    Approval/Resume 专项 `1` passed。
  - `win-x64` 发布目录真实 PTY 审批链完成 File、`powershell.exe` Shell、Web 私网
    拒绝、Credential 移除、Secret Canary 和进程残留检查。
  - `M4-ACC-006`、`M4-ACC-009` 与 Windows 平台行已改为 `Passed`。
- Risks/open questions:
  - 单一平台通过、Fake Provider 通过或 cross-publish 成功都不能替代双平台真实工具
    证据。
  - 任一默认测试依赖公网、真实 Secret、机器全局 Shell 状态或非临时路径都必须先
    修正再收口。
  - 不得通过修改 Acceptance、降低限制或把失败改成 NotRun 来制造完成状态。
- Verify:
  - `dotnet test OpenCoWork.slnx -c Release`
  - `dotnet build OpenCoWork.slnx -c Release --no-restore`
  - `dotnet publish src/OpenCoWork.App/OpenCoWork.App.csproj -c Release -r osx-arm64 --self-contained false`
  - `dotnet publish src/OpenCoWork.App/OpenCoWork.App.csproj -c Release -r win-x64 --self-contained false`
  - 在对应真实平台运行 `ToolRuntimeIntegrationTests`，并使用对应发布目录完成
    File/Shell/Web 端到端冒烟与进程树残留检查
  - `git diff --check`
- Acceptance contribution:
  - `M4-ACC-001`
  - `M4-ACC-002`
  - `M4-ACC-003`
  - `M4-ACC-004`
  - `M4-ACC-005`
  - `M4-ACC-006`
  - `M4-ACC-007`
  - `M4-ACC-008`
  - `M4-ACC-009`
  - `M4-ACC-010`

## M4 Completion Gate

按 2026-07-28 用户确认的关闭边界，以下条件同时满足时，M4 从
`In Progress` 改为 `Done`：

- Outcome 1-9 全部完成，默认测试、架构测试和 Release Build 全绿；
- `M4-ACC-001` 至 `M4-ACC-010` 均有可定位证据或明确延期结论；
- `osx-arm64` 真机 File/Shell/Web 验证和进程树残留检查通过；
- `win-x64` 真机 File/Shell/Web、进程树与 Secret 证据在双平台台账标记为
  `Passed`；
- v3→v4、Journal 重放、Projection Rebuild、Approval/Checkpoint、重复 Call ID 和
  Unsafe OutcomeUnknown 故障注入通过；
- Secret Canary 在所有持久化、事件、Provider、日志、进程输出和测试产物表面为零；
- 没有未归档的 M4 内部交付分支、临时 Tool Store、公共 Hook 或延期能力；
- 单一 M4 交付归档已创建，里程碑 CHECKLIST/INDEX 已同步，工作区无意外改动。

2026-07-29 补验后，上述 Windows 延期条件已由双平台台账中的 `Passed` 结果替代；
本 Gate 的其他条件及 M10 复验要求不变。
