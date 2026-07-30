# OpenCoWork M8 Automations and Scheduler 实施计划

**Status:** Design + Plan 已冻结；M8 实现未开始，Outcome 1 待用户单独授权。

**Goal:** 在现有 Workspace、Session、Agent、Tool、Capability、Managed Worktree、
SQLite State 和 Wire 边界上交付安全、可版本控制、可恢复的无人值守 Automation
运行闭环。

**Why planning is required:** M8 同时修改 State v7、M7 Project Writer 互斥、
YAML/Fluid/Cron 信任边界、Session 与 Worktree 副作用、无人值守权限、并发和 Lease、
人工恢复、模块健康以及 Wire 1.3 公共契约；错误顺序会产生旧定义继续运行、重复
Thread/Turn、跨根写入、无人值守提权或非幂等副作用自动重放。

**Acceptance:** `M8-ACC-001` 至 `M8-ACC-009` 都有可复现证据；29 项冻结设计决策
全部映射到实现；Wire 1.0/1.1/1.2 无回归且 1.3 通过黑盒 TestClient；`win-x64`
与 `osx-arm64` 发布目录分别完成 M8 真机验证。

对应规格：
[M8 Automations and Scheduler 详细设计](../specs/2026-07-30-open-cowork-m8-automations-scheduler-design.md)

验收目录：
[M0-M10 验收目录](../specs/2026-07-25-open-cowork-m0-acceptance-catalog.md)

双平台证据：
[双平台发布验证台账](../../platform-release-validation-ledger.md)

## 当前实现基线

- `dev` 当前代码基线为 `a7a97d3`；
- M7 Outcome 1-9 已完成，Workspace State Schema 当前为 v6，Wire 最新版本为 1.2；
- M7 的 `win-x64` 真机验收仍待执行，M8 不得关闭或替代该证据；
- `src/OpenCoWork.Automations/` 当前只有 M0 冻结的占位工程，没有运行时代码；
- `OpenCoWork.Automations` 已由 App 引用，并保持
  Automations → Abstractions/Protocol、Automations ↛ Core/Teams 的项目边界；
- State 继续由 `StateRuntime`、`StateWriteCoordinator` 和
  `IWorkspaceStateMigrationContributor` 统一迁移和串行写入；
- Thread、Turn、Interaction、Archive 与恢复继续由 `ISessionService` 和
  `ThreadJournal` 权威管理；
- Worktree 继续复用 Core `IManagedWorktreeService`，工具副作用继续只经过
  `ToolInvocationPipeline`；
- 模块生命周期继续复用 `OpenCoWorkModule`、`WorkspaceRuntime`、
  `ReportDegraded` / `ClearDegraded`，不注册独立 `IHostedService`；
- M0 已冻结测试项目集合；M8 测试进入现有 Architecture、Core、Integration、
  Protocol Tests 和 Protocol TestClient，不创建新测试程序集；
- M8 不增加真实 Provider 声明，调度、恢复和发布验收使用确定性 Fake Agent/Tool。

实施前必须先把 M8 Design + Plan 作为独立、已验证的文档基线提交；本计划的提交
不授权自动进入 Outcome 1。

## 执行规则

- 只在 `dev` 分支按 Outcome 顺序实施；
- Outcome 1 前重新读取当前 Milestone README/CHECKLIST、M8 Design、路线规格、
  `DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md` 和双平台验证台账；
- 每个 Outcome 严格执行：
  Red Test → 最小实现 → focused tests → 全量 Release 回归 → 独立 Commit；
- 上一个 Outcome 未通过全量回归或工作区仍有未提交实现，不开始下一个 Outcome；
- 不新增生产项目或测试项目；只在 M0 占位工程和现有测试项目中实现；
- 只新增设计冻结的 `YamlDotNet`、`Fluid.Core` 和 `Cronos` 依赖，并通过 Central
  Package Management 固定兼容版本；不得为解析、时钟或调度再引入替代框架；
- 复用现有 `StateRuntime`、`IWorkspaceStateStore`、`ISessionService`、
  `ThreadJournal`、`AgentRuntimeExecutor`、`ToolInvocationPipeline`、
  `WorkspaceCapabilityRuntime`、`IManagedWorktreeService`、`WorkspaceRuntime`
  和 Wire Adapter；
- 只增加跨程序集确实需要的窄契约，不增加通用 Scheduler、Workflow、Outbox、
  Repository、Unit of Work、Resource Lock、监控或重试框架；
- YAML 只缩小 Trust、Unattended Policy 与冻结 Catalog 权限，不能扩大权限；
- 事务只写状态、Receipt、Lease 和 Intent；Git、Session、文件系统与通知副作用
  必须在事务外执行并可探测；
- 所有测试只使用临时 Workspace、临时 Git 仓库和临时用户目录，不读取、修改或
  清理真实 `~/.opencowork`；
- Secret 不进入 SQLite、Journal 通知、日志、Wire、stdout/stderr、测试输出或快照；
- Cross-publish 只证明产物可生成，不能把目标平台标为 Passed；
- 任一设计硬约束无法满足时停止当前 Outcome，先修订设计与计划，不在代码中增加
  隐式兼容层。

第一次执行 Outcome 1 前先运行：

```bash
dotnet restore OpenCoWork.slnx
dotnet test OpenCoWork.slnx -c Release --no-restore
dotnet build OpenCoWork.slnx -c Release --no-restore
```

每个 Outcome 的 focused tests 通过后，还必须运行：

```bash
dotnet test OpenCoWork.slnx -c Release --no-restore
dotnet build OpenCoWork.slnx -c Release --no-restore
```

全量回归失败时不得提交该 Outcome。

### Outcome 1：冻结 Automation 契约、Config、模块和依赖边界

- Red:
  - 在 `tests/OpenCoWork.ArchitectureTests/ProjectGraphTests.cs` 增加
    Automations 不得引用 Core/Teams、Protocol 不得引用 Automations 的失败用例；
  - 在 `tests/OpenCoWork.Core.Tests/AutomationContractTests.cs` 覆盖 Actor、
    Definition/Schedule/Run 状态、Revision、分页、结果、稳定错误和固定限制；
  - 扩展 `ModuleRegistryTests`、`WorkspaceRuntimeTests` 与
    `ConfigurationPipelineTests`，覆盖非 Primary、默认关闭、配置范围、Start 前
    不可用以及停止顺序。
- Work:
  - 在 `src/OpenCoWork.Abstractions/AutomationContracts.cs` 定义仅跨程序集需要的
    Snapshot、Request、Actor、`AutomationResult<T>`、`AutomationPage<T>`、
    `IAutomationService` 和 `automation.*` 错误；
  - 在 `src/OpenCoWork.Automations/` 增加 `AutomationsModule`、
    `AutomationsConfig` 与最小 `AutomationsModuleRuntime`；
  - Config 只暴露 `enabled`、`maxConcurrentRuns`、`maximumRunTimeout` 和
    `maximumAttentionTimeout`，固定参数不变成配置；
  - 通过 `Directory.Packages.props` 与 Automations 工程引用冻结的 YamlDotNet、
    Fluid.Core、Cronos，不新增第二套解析或计时依赖；
  - 给 `OpenCoWork.Core.Tests` 增加仅测试用途的 Automations ProjectReference，
    不创建 `OpenCoWork.Automations.Tests`；
  - 本 Outcome 不实现 Definition、State、Reconciler 或 Wire 方法，也不发布
    尚不可执行的 Binding。
- Verify:
  - `dotnet test tests/OpenCoWork.ArchitectureTests/OpenCoWork.ArchitectureTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ProjectGraphTests'`
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~AutomationContractTests|FullyQualifiedName~ModuleRegistryTests|FullyQualifiedName~WorkspaceRuntimeTests|FullyQualifiedName~ConfigurationPipelineTests'`
- Acceptance contribution:
  - `M8-ACC-004`、`M8-ACC-007` 的契约与生命周期基础。
- Commit:
  - `feat(m8): define automation contracts and module`

### Outcome 2：升级 State v7 并统一 M7/M8 Project Writer Lease

- Red:
  - 在 `tests/OpenCoWork.Core.Tests/StateMigrationV7Tests.cs` 覆盖 v6→v7、七张新表、
    索引/约束、备份恢复、DDL/Commit 故障、新建工作区和重复启动；
  - 在 `ProjectWriterLeaseTests.cs` 覆盖 Acquire/Renew/Release、`leaseId` CAS、
    过期接管、错误 Owner、丢失 Lease 和并发唯一获胜；
  - 扩展 `CoWorkPersistenceTests`、`MissionReconcilerTests` 和
    `DataFoundationIntegrationTests`，证明 M7 Project Writer 保留原部分唯一索引，
    同时取得与 M8 共用的 Core Lease。
- Work:
  - 把 Core State migration chain 升到 v7，并由同一 Composition Root 把 Contributor
    集合交给 Workspace Init/Doctor 与 Runtime Start；
  - 在 `WorkspaceStateContracts.cs` 增加单用途 `IProjectWriterLeaseService` 和
    `coWorkAgentRun` / `automationRun` Owner，不抽象成通用 Resource Lock；
  - 在 Core 实现 `project_writer_lease` 单例表及事务 CAS 服务，Lease 固定 2 分钟、
    每 30 秒续约；
  - 由 Automations Contributor 创建 `automation_state`、`automation_definitions`、
    `automation_schedules`、`automation_runs`、`automation_dispatch_intents` 和
    `automation_command_receipts`；
  - 实现 JSON、UUIDv7、部分唯一索引、周期幂等键、Thread/Worktree/Intent 唯一性、
    UTC、外键和单例约束；
  - 修改 M7 Project Writer 领取路径：保留 `ix_agent_runs_project_writer`，在启动
    Writer Turn 前额外取得共享 Lease，丢失后停止新工具调用；
  - Migration、Schema 或完整性失败时阻止 Runtime Start，旧 v6 数据库保持可恢复。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~StateMigrationV7Tests|FullyQualifiedName~ProjectWriterLeaseTests|FullyQualifiedName~StateRuntimeTests|FullyQualifiedName~WorkspaceInitializerTests|FullyQualifiedName~CoWorkPersistenceTests'`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~DataFoundationIntegrationTests|FullyQualifiedName~MissionReconcilerTests'`
- Acceptance contribution:
  - `M8-ACC-005`、`M8-ACC-008` 的持久化、互斥与恢复基础。
- Commit:
  - `feat(m8): add automation state schema v7`

### Outcome 3：实现严格 YAML、Inputs、Fluid 与版本摘要

- Red:
  - 在 `tests/OpenCoWork.Core.Tests/AutomationDefinitionTests.cs` 覆盖 valid/invalid
    Corpus、未知字段、重复键、Anchor/Alias/Tag、深度/节点/大小、ID/文件名和安全
    上限；
  - 在 `AutomationTemplateTests.cs` 覆盖 JSON Schema Inputs、Defaults、四根上下文、
    Strict Variables、对象逃逸、2 秒 Deadline、256 KiB 输出和 Secret Canary；
  - 用 YAML 注释、缩进和键顺序变化证明 canonical SHA-256 不变，用每个语义字段
    变化证明 `definitionVersion` 改变。
- Work:
  - 在 `src/OpenCoWork.Automations/` 实现严格 `DefinitionLoader`，只读取
    `definitions` 直属 `.yaml` 文件；
  - 按 YamlDotNet → 已知模型 → JSON Schema → 业务校验 → Fluid 预解析 →
    canonical JSON → SHA-256 的固定顺序处理；
  - 实现 Schema v1、lower-kebab-case ID、显式 Project/Worktree、至多一个 Schedule、
    固定字段和全部硬限制；
  - Inputs 只允许 JSON 值；Fluid 只暴露 `automation`、`inputs`、`workspace`、
    `trigger` 四个安全根，不开放对象反射、文件读取或环境变量；
  - 诊断最多 32 条，先脱敏，只保存稳定 code/severity/message/path；
  - 本 Outcome 只产出候选模型与纯校验结果，不发布投影、不创建 Run。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~AutomationDefinitionTests|FullyQualifiedName~AutomationTemplateTests'`
- Acceptance contribution:
  - `M8-ACC-001`、`M8-ACC-002`、`M8-ACC-004`、`M8-ACC-007`。
- Commit:
  - `feat(m8): parse automation definitions safely`

### Outcome 4：交付 Source 投影、热更新与 Cron 时钟语义

- Red:
  - 在 `tests/OpenCoWork.IntegrationTests/AutomationSourceTests.cs` 覆盖首次全量扫描、
    250ms 合并、原子替换、重复/乱序/丢失 watcher 事件、重命名、删除、恢复和相同
    canonical 内容 Revision 去噪；
  - 在 `AutomationScheduleTests.cs` 覆盖 5 段 Cron、显式 IANA、无效时区、春季跳时、
    秋季回拨、停机合并、下一次运行与周期幂等键；
  - 覆盖 Ready/Faulted/Missing、Faulted 不回退旧有效版本、现有 Run 冻结不受影响。
- Work:
  - 实现 watcher 仅唤醒、每次完整扫描、候选完整校验后单事务发布的 Source Runtime；
  - 实现 Ready/Faulted/Missing tombstone、`sourceSha256`、诊断、实体 Revision 与
    全局 `automationRevision`；
  - 当前文件无效时清空可运行 Definition/Schedule 投影并停止新 Run，不继续旧版本；
  - 删除后隐藏 Definition/Schedule 查询但保留历史 Run；同 ID 恢复复用 tombstone；
  - 使用 Cronos 的 5 段/IANA/DST 语义计算 `nextRunAtUtc`，停机只合并到最新错过点；
  - 使用 `TimeProvider` 驱动测试与下一次唤醒，不增加轮询配置或第二个 Cron 服务。
- Verify:
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~AutomationSourceTests|FullyQualifiedName~AutomationScheduleTests'`
- Acceptance contribution:
  - `M8-ACC-001`、`M8-ACC-003`、`M8-ACC-008`。
- Commit:
  - `feat(m8): project automation sources and schedules`

### Outcome 5：实现查询、激活门、Manual Start 与冻结 Run

- Red:
  - 在 `tests/OpenCoWork.IntegrationTests/AutomationServiceTests.cs` 覆盖 Definition、
    Schedule、Run 的 List/Get、keyset cursor、分页边界、过滤和稳定排序；
  - 覆盖 `enabled` 全局开关、Workspace Trust、YAML enabled 三重激活，以及
    Trust ∩ Unattended Policy ∩ YAML allow ∩ Catalog 的权限交集；
  - 覆盖 Manual Inputs/Render 在创建前失败、Definition Revision CAS、
    `commandId` 重放/冲突、单 Automation 非终态冲突和 Secret Canary；
  - 运行中修改 Definition、Plugin、Skill、Tool Binding，证明 Run 的 Definition、
    Input、Permission、Provider/Model 与能力摘要不漂移。
- Work:
  - 实现 `IAutomationService` 的查询、分页、Manual Start 与 Command Receipt；
  - List/Get 只读取 SQLite 投影，YAML 文件仍是定义事实源，Service 不提供 CRUD；
  - Manual Start 在一个事务中校验 Host Actor、Definition Revision、三重激活门、
    输入、权限交集、单实例和固定上限；
  - Run 创建时冻结规范化 Definition、Inputs 摘要、Rendered Prompt、Provider/Model、
    Trust/Policy/YAML/Catalog 交集和 Plugin/Skill/Tool 身份/摘要；
  - SQLite 不保存 Inputs、Rendered Prompt 或 Secret；完整内容只在提交 Thread 时
    进入现有 Journal 安全边界；
  - `AutomationResult<T>` 只承载 value、automationRevision、isReplay、error，不复制
    Wire 错误包络。
- Verify:
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~AutomationServiceTests|FullyQualifiedName~AutomationSecurityTests'`
- Acceptance contribution:
  - `M8-ACC-004`、`M8-ACC-007`。
- Commit:
  - `feat(m8): create frozen automation runs`

### Outcome 6：派发 Project/Worktree、一个 Thread 与一个 Turn

- Red:
  - 在 `tests/OpenCoWork.IntegrationTests/AutomationDispatchTests.cs` 覆盖 Project
    ReadOnly/Writer、Worktree、Dirty Origin、冻结 Base SHA、路径逃逸和每 Run 独立
    Detached Worktree；
  - 覆盖 Worktree Create、Thread Create、Turn Submit 在副作用前后崩溃、相同
    幂等键探测、重复唤醒和 Intent 最大五次；
  - 证明每个 Run 只创建一个 Unattended Thread 和一个 Turn，不启动 Team/Mission，
    不绕过 `ToolInvocationPipeline`；
  - 覆盖共享 Project Writer Lease 与 M7 Writer 的竞态，ReadOnly Project 和
    Worktree Run 不领取该 Lease。
- Work:
  - 实现持久 `automation_dispatch_intents` 的领取、续约、结果提交和探测；
  - 在事务外按 Worktree → Thread → Turn 顺序执行副作用，每一步使用稳定幂等键；
  - Project 直接绑定 Workspace Root；含 `WorkspaceWrite` 的有效快照先领取 Core
    Project Writer Lease；
  - Worktree 通过 `IManagedWorktreeService` 从冻结 Base Commit 创建每 Run 独立
    Detached Worktree，不复制 Dirty Origin 内容；
  - 通过 `ISessionService` 创建带 Automation Provenance 的 Unattended Thread，
    再提交唯一 Turn；只增加必要的窄 Session 契约，不复制 Session 状态机；
  - 外部副作用成功但结果未提交时先探测；无法证明的非幂等 Tool 结果不自动重放；
  - 不自动 Commit、Merge、Rebase、Cherry-pick 或 Patch。
- Verify:
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~AutomationDispatchTests|FullyQualifiedName~AutomationWorkspaceIntegrationTests|FullyQualifiedName~AutomationFaultTests'`
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~SessionContractTests|FullyQualifiedName~SessionRecoveryTests|FullyQualifiedName~AgentRuntimeExecutorTests|FullyQualifiedName~CoreToolTests|FullyQualifiedName~SourceControlToolTests|FullyQualifiedName~ProjectWriterLeaseTests'`
- Acceptance contribution:
  - `M8-ACC-004`、`M8-ACC-005`、`M8-ACC-008`。
- Commit:
  - `feat(m8): dispatch automation runs durably`

### Outcome 7：实现 Cron Claim、并发、Lease、恢复与 Degraded

- Red:
  - 在 `tests/OpenCoWork.IntegrationTests/AutomationReconcilerTests.cs` 覆盖固定
    Reconcile 顺序、Cron Claim、单 Automation 互斥、全局并发、Lease 过期接管、
    Session 终态恢复和通知丢失/重复；
  - 以 64 个并发 Start、`maxConcurrentRuns = 16` 覆盖容量不超卖、手动/周期冲突、
    跨 M7/M8 Writer 互斥和多个 Reconciler 只有一个获胜；
  - 在 `AutomationLifecycleTests.cs` 覆盖初始不安全基线 Start 失败、运行期共享控制面
    失效进入 Degraded、实体故障隔离、恢复后 ClearDegraded 和正常 Stop 保留 Run；
  - 覆盖 NeedsAttention 占用 Automation/Worktree/Writer Lease 但释放全局并发槽。
- Work:
  - 实现每 Workspace 单例 `AutomationReconciler`，SQLite 是权威，Channel 只合并
    唤醒；
  - 按设计固定顺序处理 Cancel/Deadline、Session 恢复、未知 Intent、过期 Lease、
    Cron、Run Claim、副作用、终态 Archive 和 Worktree 清理；
  - 在事务内完成周期幂等、单 Automation、全局并发和 Dispatch Lease 领取；
  - 全局并发默认 3、范围 1–16；Dispatch Lease 固定 2 分钟、每 30 秒续约；
  - 自动重试只覆盖五次幂等基础设施 Intent；不重跑整个 Run、模型或 Tool；
  - 复用 `WorkspaceRuntime.ReportDegraded("automations", reason)`，不新增健康状态表；
  - 模块启动/停止遵循冻结顺序，Start 前和 Stop 开始后 Binding 均不可用。
- Verify:
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~AutomationReconcilerTests|FullyQualifiedName~AutomationLifecycleTests|FullyQualifiedName~AutomationConcurrencyTests|FullyQualifiedName~AutomationFaultTests'`
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~WorkspaceRuntimeTests|FullyQualifiedName~StateRuntimeTests|FullyQualifiedName~ProjectWriterLeaseTests'`
- Acceptance contribution:
  - `M8-ACC-003`、`M8-ACC-005`、`M8-ACC-008`。
- Commit:
  - `feat(m8): reconcile automation schedules and runs`

### Outcome 8：关闭 Attention、Cancel、Timeout 与资源保留

- Red:
  - 在 `tests/OpenCoWork.IntegrationTests/AutomationInteractionTests.cs` 覆盖 Approval
    Approve/Reject、UserInput ProvideInput、错误 Attention Kind/Resolution 和同
    Session 恢复；
  - 覆盖 Unsafe Tool 提交副作用后中断，Run 进入
    `OutcomeUnknown -> NeedsAttention`，只能由 Host Fail/Cancel，不能自动或同 Run
    重试；
  - 覆盖 Run Timeout、Attention Timeout、显式 Cancel、Lease Lost、请求取消和
    Session 终态的唯一获胜竞态；
  - 覆盖 Thread Archive Intent 前后崩溃、Clean Unchanged Worktree 自动清理、
    Dirty/HEAD 漂移保留和进程树残留。
- Work:
  - 把 Session Waiting Interaction 投影为 Run NeedsAttention，并保存稳定
    `attentionId`、Kind 与 Deadline；
  - Host Resolution 先校验 Run Revision、Attention ID、Kind 和允许动作，再使用
    稳定幂等键调用 `ISessionService.ResolveInteractionAsync`；
  - OutcomeUnknown 不调用 Session Resume，只把 Run 推进为 Failed 或 Cancelled；
  - Cancel/Timeout 先持久化意图，再取消 Turn/工具进程树并收敛唯一终态；
  - Project Writer Lease 在 NeedsAttention 期间续约，丢失时按可证明结果选择
    `automation.leaseLost` 或 OutcomeUnknown；
  - Run 终态后以 Intent 幂等 Archive Thread，不自动删除 Thread；
  - 只有 Worktree 干净且 `HEAD == BaseCommit` 才自动清理，其余现场保留并返回引用；
  - Run 只保存最多 16 KiB 的脱敏安全摘要，完整历史继续留在 ThreadJournal。
- Verify:
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~AutomationInteractionTests|FullyQualifiedName~AutomationCancellationTests|FullyQualifiedName~AutomationRetentionTests|FullyQualifiedName~AutomationFaultTests'`
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~SessionExecutionTests|FullyQualifiedName~SessionRecoveryTests|FullyQualifiedName~BackgroundTerminalTests|FullyQualifiedName~ProjectWriterLeaseTests'`
- Acceptance contribution:
  - `M8-ACC-006`、`M8-ACC-008`、`M8-ACC-009`。
- Commit:
  - `feat(m8): resolve automation attention safely`

### Outcome 9：交付 Wire 1.3 与 TestClient 黑盒

- Red:
  - 在 `tests/OpenCoWork.Protocol.Tests/AutomationWireTests.cs` 覆盖九个方法、三个
    Changed 通知、DTO、keyset cursor、Revision、Command 重放和稳定错误映射；
  - 扩展 `OpenCoWorkJsonRpcTests`，证明 1.0/1.1/1.2 客户端看不到 1.3 方法/事件，
    1.3 初始化协商与旧版本回归不变；
  - 扩展 `ProtocolProcessIntegrationTests` 和 TestClient，覆盖 stdio/WebSocket
    重连、慢读端、通知重复、取消、Attention 恢复和 Secret Canary；
  - 证明 Wire 不能构造 Scheduler Actor，Automation 没有 YAML CRUD、retry、ACP
    或模型管理工具。
- Work:
  - 把 `OpenCoWorkWire.LatestVersion` 升到 1.3，在 `WireContracts.cs` 增加 M8 DTO 与
    `WireAutomationResponse<T>`；
  - 在 Protocol 增加 Automation Wire Catalog、Handler 和通知投影，Adapter 只调用
    `IAutomationService`；
  - Host Actor 来自已认证 ConnectionAuthority，忽略负载中的伪造身份；
  - 实现 `automation/list|get`、`schedule/list|get`、
    `automationRun/start|list|get|cancel|resolveAttention`；
  - Domain Error 映射到现有 `-32000` 至 `-32005` 和 `WireErrorData`，不增加数字码或
    第二套错误包络；
  - 通知只携带全局 Revision、Kind 和 Entity ID，重放命令不重复通知；
  - 扩展 `tests/OpenCoWork.Protocol.TestClient/Program.cs` 形成 M8 发布目录黑盒场景。
- Verify:
  - `dotnet test tests/OpenCoWork.Protocol.Tests/OpenCoWork.Protocol.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~AutomationWireTests|FullyQualifiedName~OpenCoWorkJsonRpcTests|FullyQualifiedName~AcpConnectionTests|FullyQualifiedName~CoWorkWireTests'`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ProtocolProcessIntegrationTests|FullyQualifiedName~AutomationSecurityTests'`
- Acceptance contribution:
  - 为 `M8-ACC-001` 至 `M8-ACC-009` 增加 Wire 黑盒证据。
- Commit:
  - `feat(m8): expose automations over wire 1.3`

### Outcome 10：关闭故障、安全、性能、双平台与交付资产

- Red:
  - 先让 Acceptance Catalog 的 M8 行保持 `Planned`，平台台账保持 `Pending`；
  - 缺任一 focused/full、Fault Injection、Wire 黑盒、Secret/残留、性能负载或真机
    证据时，Closeout Check 必须失败；
  - 缺 M7 Windows 真机证据时，M7 继续保持未关闭，M8 结果不得覆盖它。
- Work:
  - 运行全量 Release build/test、定义/Cron/DST Corpus、权限交集、状态/竞态性质测试
    和全部副作用故障点；
  - 执行固定性能负载：1,000 Definition/10% Faulted、64 Start/并发上限 16、
    10,000 Run/每页 100，并记录耗时、Schedule Lag、Reconcile 数和 SQLite Busy；
  - 分别为 App 与 Protocol TestClient 按 RID 独立 restore/publish，避免复用错误的
    RID assets；
  - 在 `win-x64` 与 `osx-arm64` 发布目录真机验证 DST、热更新、强杀恢复、
    Worktree/路径、取消进程树、Wire 1.0–1.3 和 Secret Canary；
  - 记录 Commit、平台、OS、SDK/runtime、Git、产物摘要、测试数量、命令和结果；
  - 不新增真实 Provider 声明；Provider Backlog 只在实际激活并验证后更新；
  - 同步 M8 Design/Plan 状态、M0 Capability Ledger、Acceptance Catalog、Platform
    Ledger、Milestone Checklist/INDEX 和 M8 Delivery Archive；
  - `M8-ACC-001..009` 全部 Passed 且两平台独立证据齐全后，才能把 M8 标为 Done
    并创建交付归档。
- Verify:
  - `dotnet test OpenCoWork.slnx -c Release --no-restore`
  - `dotnet build OpenCoWork.slnx -c Release --no-restore`
  - 每个目标平台分别执行：

```bash
dotnet restore src/OpenCoWork.App/OpenCoWork.App.csproj -r <rid>
dotnet publish src/OpenCoWork.App/OpenCoWork.App.csproj -c Release -r <rid> --self-contained false --no-restore
dotnet restore tests/OpenCoWork.Protocol.TestClient/OpenCoWork.Protocol.TestClient.csproj -r <rid>
dotnet publish tests/OpenCoWork.Protocol.TestClient/OpenCoWork.Protocol.TestClient.csproj -c Release -r <rid> --self-contained false --no-restore
```

  - 在对应真机运行发布目录 M8 TestClient 矩阵和残留检查，并把结果写入
    [双平台发布验证台账](../../platform-release-validation-ledger.md)。
- Acceptance contribution:
  - 关闭 `M8-ACC-001` 至 `M8-ACC-009`，或保持未满足项和对应平台为
    `Planned` / `Pending`。
- Commit:
  - `docs(m8): close automations scheduler delivery`

## 覆盖矩阵

| Outcome | 冻结决策 | 验收编号 |
| ---: | --- | --- |
| 1 | 1、12、23、24、26、27、29 | M8-ACC-004、007 |
| 2 | 7、12、18、19、22、27 | M8-ACC-005、008 |
| 3 | 2、10、11、16、17、20、23、26 | M8-ACC-001、002、004、007 |
| 4 | 3、4、9、11、18、20、27 | M8-ACC-001、003、008 |
| 5 | 4、5、7、10、14、15、18、23、24、25、26 | M8-ACC-004、007 |
| 6 | 1、4、5、8、12、13、21、22、23、26 | M8-ACC-004、005、008 |
| 7 | 3、7、12、13、18、22、27 | M8-ACC-003、005、008 |
| 8 | 6、8、13、18、21、22、24、26、27 | M8-ACC-006、008、009 |
| 9 | 14、18、24、25、26 | M8-ACC-001..009 Wire 证据 |
| 10 | 28、29 与全部关闭条件 | M8-ACC-001..009 |

29 项决策和 9 个验收编号都至少有一个主实现 Outcome 与最终关闭 Outcome。

## 停止条件与恢复边界

- State Backup、Migration 或完整性校验失败：Runtime Start 失败，不继续 Outcome 3；
- Project Writer Lease 出现双 Owner、错误续约或 M7/M8 互斥失效：停止 Writer
  调度，不用内存锁掩盖数据库错误；
- Definition Source 新鲜度无法确认：停止新 Run 并报告 Degraded，不回退旧版本；
- YAML、Inputs、Rendered Prompt、权限或能力快照不确定：不创建 Run；
- Thread/Turn/Worktree Intent 无法探测且副作用可能已发生：不自动重放，按
  OutcomeUnknown 进入 NeedsAttention；
- Worktree 路径、Trust、摘要或 Dirty 状态不确定：保留目录，不自动删除或复用；
- Secret Canary 出现在任何持久层、日志、通知、stdout/stderr 或测试输出：阻塞
  当前 Outcome；
- Wire 1.0/1.1/1.2 回归：阻塞 Wire 1.3 和 M8 Closeout；
- 任一目标平台缺少真机发布目录证据：对应平台保持 Pending，M8 不标 Done；
- 真实用户目录或真实 Provider 未获明确授权：只使用临时 Profile/Fake Agent/Tool。

## 完成定义

M8 只有在以下条件同时满足后才能标记 Done：

- 10 个 Outcome 都按 Red → Minimal → Focused → Full → Independent Commit 完成；
- 29 项冻结设计决策均有实现和验证证据；
- `M8-ACC-001` 至 `M8-ACC-009` 全部 Passed；
- Wire 1.0/1.1/1.2 回归与 Wire 1.3 黑盒全部通过；
- `win-x64` 与 `osx-arm64` 发布目录真机证据独立完成；
- Secret、路径逃逸、进程树、Dirty Worktree、Lease 与 OutcomeUnknown 检查通过；
- Design、Plan、Capability/Acceptance/Platform Ledger、Archive、Milestone
  Checklist 与 INDEX 已同步。

若用户显式延期任一平台真机证据，可以归档已完成实现并登记延期，但不得把该平台或
完整 M8 标为 Passed/Done；缺少双平台证据时不得创建 M8 交付归档。
