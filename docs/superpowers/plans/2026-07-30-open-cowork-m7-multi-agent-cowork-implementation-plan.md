# OpenCoWork M7 Multi-Agent CoWork 实施计划

**Status:** Done；Outcome 1-10 与 `win-x64`、`osx-arm64` 真机验收全部通过，M7 已归档。

**Goal:** 在现有 Workspace、Session、Agent、Tool、Capability 和 Wire 边界上交付
可持久、可恢复、受预算和权限约束的 Direct SubAgent 与 Mission 协作闭环。

**Why planning is required:** M7 同时修改 SQLite Migration、Session Thread 路由、
Tool 执行根、Git Worktree、并发与 Token 预算、Secret 边界、模块生命周期和 Wire
公共契约；错误顺序会产生重复 AgentRun、跨根写入、预算超卖或 Origin 重复回传。

**Acceptance:** `M7-ACC-001` 至 `M7-ACC-010` 都有可复现证据；27 项冻结设计决策
全部映射到实现；Wire 1.0/1.1 无回归且 1.2 通过黑盒 TestClient；`win-x64` 与
`osx-arm64` 发布目录分别完成 M7 真机验证。

对应规格：
[M7 Multi-Agent CoWork 详细设计](../specs/2026-07-30-open-cowork-m7-multi-agent-cowork-design.md)

验收目录：
[M0-M10 验收目录](../specs/2026-07-25-open-cowork-m0-acceptance-catalog.md)

双平台证据：
[双平台发布验证台账](../../platform-release-validation-ledger.md)

## 当前实现基线

- `dev` 当前代码基线 `c30f168` 已完成 Outcome 1-9；
- Workspace State Schema 已原子升级到 v6，Teams、AgentRun、Mission、Mailbox、
  Artifact、Worktree、Lease、Budget 与 Command Receipt 均以 SQLite 为权威；
- OpenCoWork Wire 已支持 1.0/1.1/1.2，并保持 ACP v1；
- `OpenCoWork.Teams` 已交付 Direct SubAgent、Mission DAG、Mailbox、Artifact、
  Worktree、Review/Rework、Leader Synthesis、恢复与 Origin Once；
- M0 已冻结测试项目集合，不新增 `OpenCoWork.Teams.Tests`；领域测试放入现有
  `OpenCoWork.Core.Tests` 或 `OpenCoWork.IntegrationTests`；
- 当前没有 M7 Provider 兼容性声明，编排测试使用可控 Fake Provider；
- 2026-07-30 的 `osx-arm64` Release、专项/全量回归与发布目录 TestClient 已通过；
  2026-08-02 的 `win-x64` Release、全量非显式 Integration 串行回归和发布目录
  TestClient 已通过，双平台证据齐全。

实施前必须先把 M7 Design + Plan 作为独立、已验证的文档基线提交；没有用户授权不得
从本计划自动进入 Outcome 1。

## 执行规则

- 只在 `dev` 分支按 Outcome 顺序实施；
- 每个 Outcome 严格执行：
  Red Test → 最小实现 → focused tests → 全量 Release 回归 → 独立 Commit；
- 上一个 Outcome 未通过全量回归或工作区仍有未提交实现，不开始下一个 Outcome；
- 不新增生产项目、测试项目或 NuGet 依赖；
- 复用现有 `StateRuntime`、`StateWriteCoordinator`、`ISessionService`、
  `ThreadJournal`、`AgentRuntimeExecutor`、`ToolInvocationPipeline`、
  `WorkspaceCapabilityRuntime`、`WorkspaceRuntime` 和 Wire Adapter；
- 只增加跨程序集确实需要的窄契约，不增加 Repository、Unit of Work、通用 Scheduler、
  通用 Outbox、通用 RBAC 或 `execute(action, payload)`；
- 所有测试只使用临时 Workspace、临时 Git 仓库和临时用户目录，不读取、修改或清理
  真实 `~/.opencowork`；
- Secret 不进入 SQLite、Journal 通知、日志、Wire 通知、测试输出或快照；
- Cross-publish 只证明产物可生成，不能把目标平台标为 Passed；
- 任一设计硬约束无法满足时停止当前 Outcome，先修订设计与计划，不在代码中增加
  隐式兼容层。

每个 Outcome 的 focused tests 通过后，还必须运行：

```bash
dotnet test OpenCoWork.slnx -c Release --no-restore
dotnet build OpenCoWork.slnx -c Release --no-restore
```

全量回归失败时不得提交该 Outcome。

### Outcome 1：冻结 CoWork 跨程序集契约与 Teams 模块边界

- Red:
  - 在 `tests/OpenCoWork.ArchitectureTests/ProjectGraphTests.cs` 增加 Teams 不得引用
    Core/Automations、Protocol 不得引用 Teams 的失败用例；
  - 在 `tests/OpenCoWork.Core.Tests/CoWorkContractTests.cs` 增加 Actor、Revision、
    Execution Workspace、状态枚举、稳定错误和限制默认值契约测试；
  - 在 `tests/OpenCoWork.Core.Tests/WorkspaceRuntimeTests.cs` 增加 Teams 非 Primary、
    Binding 在 Start 前不可用和停止顺序测试。
- Work:
  - 在 `src/OpenCoWork.Abstractions/CoWorkContracts.cs` 定义 Profile、Team、Mission、
    Task、AgentRun、Mailbox、Artifact、Worktree、Budget、Intent、Actor Context、
    `ICoWorkService` 和 `cowork.*` 错误；
  - 在 `src/OpenCoWork.Abstractions/WorkspaceStateContracts.cs` 定义
    `IWorkspaceStateStore`、`IWorkspaceStateMigrationContributor`，回调只暴露
    `DbConnection` / `DbTransaction`；
  - 在 Abstractions 增加仅跨程序集需要的 `WorkspaceRuntimeDescriptor`、
    `ExecutionWorkspaceDescriptor`、`IManagedWorktreeService` 和
    `ISensitiveDataService`；不把 Core 路径、SQLite Provider 或 Redactor 实现上移；
  - 在现有 `src/OpenCoWork.Teams/` 增加 `TeamsModule` 和配置/生命周期注册入口；
  - 给现有 `OpenCoWork.Core.Tests` 增加仅测试用途的 Teams ProjectReference，复用
    M0 冻结测试项目，不创建新的 Teams 测试程序集；
  - 保留 Teams 对 Protocol 扩展点的现有允许依赖，不增加 Core 引用；
  - 本 Outcome 不预注册尚不可执行的 M7 Tool Definition；Outcome 9 成对发布
    Definition 与 Binding，且 Binding 只在 Teams Start 成功后 Available。
- Verify:
  - `dotnet test tests/OpenCoWork.ArchitectureTests/OpenCoWork.ArchitectureTests.csproj -c Release --filter 'FullyQualifiedName~ProjectGraphTests'`
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~CoWorkContractTests|FullyQualifiedName~WorkspaceRuntimeTests|FullyQualifiedName~RuntimeContractTests'`
- Acceptance contribution:
  - `M7-ACC-002` 的程序集、权威和生命周期基线。
- Commit:
  - `feat(m7): define cowork contracts and teams module`

### Outcome 2：升级 SQLite v6、配置和持久化权威层

- Red:
  - 在 `tests/OpenCoWork.Core.Tests/StateMigrationV6Tests.cs` 覆盖 v5→v6、14 表、
    索引/约束、备份、DDL/Commit 故障和重复启动；
  - 在 `tests/OpenCoWork.Core.Tests/CoWorkPersistenceTests.cs` 覆盖 Revision CAS、
    Command Receipt、Intent/Lease、Token 预留和部分唯一索引竞态；
  - 扩展 `ConfigurationPipelineTests` 与 `WorkspacePathTests`，覆盖默认值、范围和 M0
    运行时目录。
- Work:
  - 让 Core `StateRuntime` 在一个全局 v6 Migration 中组合 Teams Contributor；
  - 由 App Composition Root 把同一 Contributor 集合同时交给 Workspace Init/Doctor
    和 Runtime Start，不能让新建工作区与运行时升级使用不同 Migration 列表；
  - 通过现有 `StateWriteCoordinator` 实现 `IWorkspaceStateStore`，增加有返回值的窄
    事务回调，不公开 `SqliteConnection` 类型；
  - 创建设计冻结的 14 张表和必要唯一/部分索引，不增加依赖边、Digest、Event、
    Revision History 或 Outbox 表；
  - 增加 Teams 配置：深度、全局/Mission 并发，以及设计冻结的成员、Task、消息、
    Artifact、总存储、Lease 和 Attempt 硬限制；
  - 扩展 `OpenCoWorkPaths` 和 `WorkspaceRuntimeDescriptor`，只声明 M0/M7 冻结目录，
    保持按需创建；
  - Migration、Schema 或完整性失败时阻止 Teams Start，旧 v5 数据库保持可恢复。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~StateMigrationV6Tests|FullyQualifiedName~CoWorkPersistenceTests|FullyQualifiedName~StateRuntimeTests|FullyQualifiedName~WorkspaceInitializerTests|FullyQualifiedName~ConfigurationPipelineTests|FullyQualifiedName~WorkspacePathTests'`
- Acceptance contribution:
  - `M7-ACC-002`、`M7-ACC-005`、`M7-ACC-009` 的持久化与竞态基础。
- Commit:
  - `feat(m7): add cowork state schema v6`

### Outcome 3：实现 Profile、Team、Mission Planning 与 DAG 命令面

- Red:
  - 在 `tests/OpenCoWork.IntegrationTests/CoWorkServiceTests.cs` 覆盖 Profile/Team
    原子 Upsert、冻结快照、权限、Revision 冲突和 Command 重放；
  - 在 `tests/OpenCoWork.IntegrationTests/MissionDagPropertyTests.cs` 覆盖随机 DAG、
    环、缺失依赖、256 Task、Active 边不可变和 Ready 确定性；
  - 在 `tests/OpenCoWork.IntegrationTests/CoWorkStateMachineTests.cs` 覆盖设计列出的
    所有合法/非法状态边；
  - 使用 Secret Canary 覆盖 Profile、Task、Mailbox 输入拒绝。
- Work:
  - 在 `src/OpenCoWork.Teams/` 实现具体 `CoWorkService`，所有写命令统一经过
    Actor、Permission、Revision、状态和硬限制校验；
  - 实现 AgentProfile、Team/Member、Mission Create、Task Planning CRUD 和
    Activate；
  - Profile/Team 被引用后只允许 SetEnabled，不物理删除；Team Upsert 原子校验唯一
    Leader、Alias 和可解析 Profile；
  - Create 记录 Planning Team Revision 和 Leader Planning Intent；Activate 对
    Team/Profile 漂移、Alias、预算、成员、Task 和 DAG 做单事务校验；
  - 激活时冻结 Member/Profile/Provider/Model/Instructions/Allowlist、Workspace
    模式、Base SHA 和根 Budget；
  - 实现 Required/Optional、Review、Block、Retry、Reassign、Waive 的纯领域转换；
  - 事务只写状态、Receipt 与 Intent，不在事务内调用 Session、Git 或文件系统；
  - 通过 `ISensitiveDataService` 拒绝或脱敏持久文本。
- Verify:
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter 'FullyQualifiedName~CoWorkServiceTests|FullyQualifiedName~MissionDagPropertyTests|FullyQualifiedName~CoWorkStateMachineTests'`
- Acceptance contribution:
  - `M7-ACC-002`、`M7-ACC-003`、`M7-ACC-008`。
- Commit:
  - `feat(m7): implement cowork domain commands`

### Outcome 4：把 Thread 执行空间、Scratchpad 和 Managed Worktree 接入 Core

- Red:
  - 扩展 `SessionContractTests`、`ThreadJournalTests` 和 `SessionRecoveryTests`，
    证明 Execution Workspace 与 CoWork Provenance 创建后不可变且可重放；
  - 扩展 `AgentRuntimeExecutorTests`，证明 Workspace Instructions 从调用 Thread
    的 Project/Worktree Root 读取；
  - 扩展 `CoreToolTests`、`SourceControlToolTests`、`BackgroundTerminalTests`，
    证明 File/Shell/SourceControl/Terminal 不再越过调用者执行根；
  - 在临时 Git 仓库覆盖 Detached Worktree、Dirty Origin、Base SHA 固定、
    Symlink/Reparse/Junction 和 Dirty Retention。
- Work:
  - 扩展 Session Thread Created Fact、Snapshot、AgentSession 和 Journal 投影，
    保存不可变 `ExecutionWorkspaceDescriptor` 与 CoWork Provenance；
  - 既有 Host/Wire 创建的普通 Thread 默认绑定 Project Root；外部
    `thread/create` 不接受任意路径；
  - `AgentRuntimeExecutor`、ToolInvocationContext、File、Shell、SourceControl 和
    Terminal 从 Thread Descriptor 解析 Root；
  - File Tool 仅为 CoWork Thread 增加 `area=workspace|scratchpad`，Shell/Terminal/
    SourceControl 仍只能使用 Workspace Root；
  - 在 Core 实现窄 `IManagedWorktreeService`，复用 M6 Git 进程、Trust、环境、
    Secret 和进程树处理；
  - 使用 `git worktree add --detach`，清理不加 `--force`；Dirty Worktree 返回
    `RetainedDirty`；
  - Handoff 只返回路径、Base SHA、状态和 Artifact 引用，不自动 Commit、Merge、
    Rebase、Cherry-pick 或生成/应用 Patch；
  - Teams 只持久化 Worktree 生命周期和 Trust Snapshot，不直接依赖 Core 类型。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~SessionContractTests|FullyQualifiedName~ThreadJournalTests|FullyQualifiedName~SessionRecoveryTests|FullyQualifiedName~AgentRuntimeExecutorTests|FullyQualifiedName~CoreToolTests|FullyQualifiedName~SourceControlToolTests|FullyQualifiedName~BackgroundTerminalTests'`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter 'FullyQualifiedName~CoWorkWorkspaceIntegrationTests'`
- Acceptance contribution:
  - `M7-ACC-006`、`M7-ACC-007` 的执行根和 Worktree 基础。
- Commit:
  - `feat(m7): bind agent threads to execution workspaces`

### Outcome 5：实现 Direct SubAgent、预算、并发与取消传播

- Red:
  - 在 `tests/OpenCoWork.IntegrationTests/DirectSubAgentTests.cs` 覆盖 Spawn、
    Children/List、Send、Follow-up、同 Thread 多 AgentRun 和递归 Cancel；
  - 在 `CoWorkBudgetRaceTests.cs` 以 16 个并发 AgentRun 覆盖 Token 预留/结算、
    深度、全局并发、根预算共享和崩溃恢复；
  - 在 `CoWorkDispatchFaultTests.cs` 覆盖 Thread/Turn 提交前后崩溃、过期 Lease、
    Unknown Outcome 探测和 Intent Dead Letter。
- Work:
  - 在 Teams 实现每 Workspace 单例 Reconciler、SQLite 权威队列、Channel 唤醒、
    Lease 领取/续约和固定处理顺序；
  - Direct `spawn` 创建 Child Thread、首个 AgentRun 和根 BudgetScope，不创建隐藏
    Team/Mission/Task；
  - Follow-up 在相同 Child Thread 上创建新 AgentRun/Turn，复用冻结 Profile、
    Execution Workspace 和根预算；
  - Send 使用持久 Direct Message；Active Run 在安全输入边界接收，无 Active Run
    时等待下一 Follow-up；
  - 取消沿持久 Thread Lineage 递归传播，并通过 Session 取消回收工具进程树；
  - 在一个 State 事务中完成深度、并发、预算预留和 Intent 创建；Semaphore/Channel
    只减少唤醒；
  - Provider Usage 成功时按实际值结算并释放余量；Usage 未知时按完整预留结算；
  - 自动重试只覆盖设计列出的瞬时基础设施错误，不自动重跑模型或工具失败。
  - Active AgentRun 引用的 Thread 禁止删除；取消与清理必须保留可恢复关系。
- Verify:
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter 'FullyQualifiedName~DirectSubAgentTests|FullyQualifiedName~CoWorkBudgetRaceTests|FullyQualifiedName~CoWorkDispatchFaultTests'`
- Acceptance contribution:
  - `M7-ACC-001`、`M7-ACC-005`、`M7-ACC-009`。
- Commit:
  - `feat(m7): implement durable direct subagents`

### Outcome 6：实现 Mission 调度、Member 执行、Review 与 Rework

- Red:
  - 在 `MissionReconcilerTests.cs` 覆盖 Planning Intent、Activate、Ready、依赖、
    Member 互斥、Project Writer 互斥与 Worktree 并行；
  - 在 `MissionReviewTests.cs` 覆盖 Required/Optional 默认值、Review Accept、
    Rework 新 Attempt、Reassign、Optional Waive 和提前综合拒绝；
  - 在 `MissionRecoveryTests.cs` 覆盖 256 Task、Session 终态通知丢失、重复唤醒和
    Reconciler 重启。
- Work:
  - Reconciler 处理 Leader Planning Intent，Leader Thread 只使用编排工具并计入
    Mission 根预算/并发；
  - 事务内计算 WaitingDependencies/Ready，原子获取 Member、Project Writer、
    Mission/全局并发、Token 和 Dispatch Lease；
  - Worktree 模式为每个 Member AgentRun 创建独立 Thread/Worktree；Project 模式按
    Effective Tool Snapshot 保守推导 ReadOnly/ReadWrite；
  - Member 完成只写 Redacted OutputSummary、Usage、状态和 Artifact 引用，完整历史
    留在独立 ThreadJournal；
  - Task Retry/Rework 创建新 AgentRun/Attempt，不复活旧 Run；
  - Required 失败/取消阻止综合；Optional 失败进入 Leader 输入但不自动失败 Mission；
  - 单 Task 故障隔离，不把 Workspace 标为 Degraded。
- Verify:
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter 'FullyQualifiedName~MissionReconcilerTests|FullyQualifiedName~MissionReviewTests|FullyQualifiedName~MissionRecoveryTests'`
- Acceptance contribution:
  - `M7-ACC-003`、`M7-ACC-005`、`M7-ACC-008`、`M7-ACC-009`。
- Commit:
  - `feat(m7): implement mission scheduling and review`

### Outcome 7：实现 Mailbox、Artifact 与安全文件生命周期

- Red:
  - 在 `MissionMailboxTests.cs` 覆盖六种消息、64 KiB、at-least-once、批量注入、
    显式 Ack、重复投递、重试和 Dead Letter；
  - 在 `CoWorkArtifactTests.cs` 覆盖 64 MiB/512 MiB、SHA-256、Mission 去重、
    Promote、缺失文件、摘要篡改和孤儿恢复；
  - 在双平台条件测试中覆盖 Symlink、Junction、Reparse Point、路径逃逸和
    Secret Canary。
- Work:
  - 实现 Leader↔Member Mission Mailbox，Message ID 作为 Session 幂等键；
  - Digest 只做查询投影，不增加表；
  - Direct Message 复用投递底座但不暴露为 Mission Mailbox；
  - Scratchpad 保持 AgentRun 私有可变；Artifact 使用临时文件、流式大小/Secret
    扫描、SHA-256 和原子移动；
  - Artifact 默认 Mission 可见，只有 Leader 可 Promote 到 Origin；
  - 文件缺失标为 Unavailable；Dirty/未知/根外文件不自动删除；
  - 孤儿清理延迟执行，只处理可证明归属、未引用且超过保留门槛的 Clean 内容。
- Verify:
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter 'FullyQualifiedName~MissionMailboxTests|FullyQualifiedName~CoWorkArtifactTests|FullyQualifiedName~CoWorkFileSecurityTests'`
- Acceptance contribution:
  - `M7-ACC-004`、`M7-ACC-006`。
- Commit:
  - `feat(m7): add mailbox and artifact workflows`

### Outcome 8：实现 Leader 综合、Origin 单次回传与完整恢复

- Red:
  - 在 `MissionSynthesisTests.cs` 覆盖确定性输入、Optional Failure、Artifact
    Metadata、Review/Rework 门槛和仅一次最终综合；
  - 在 `OriginDeliveryTests.cs` 覆盖 Origin Busy、Archived、断连、提交前后崩溃、
    重复通知和稳定 OriginDeliveryId；
  - 在 `CoWorkLifecycleTests.cs` 覆盖 Teams Start/Stop、Lease 恢复、Binding
    可用性、Degraded/Faulted 边界和正常停止不失败 Mission。
- Work:
  - 按 Task 创建顺序构造 Leader Synthesis 输入，不读取或复制完整 Member 历史；
  - Synthesis 成功后先持久化 Final Summary/Provenance，再标记 Mission Completed；
  - 为 `ISessionService` 增加窄、幂等的 Completed Agent Turn 追加命令，Origin 不
    触发第二次模型调用；
  - Origin Busy 时等待，Archived 时恢复 Active；Client 断连不取消 Mission；
  - Reconciler 以 OriginDeliveryId 探测/重放，Journal 中最终结果只出现一次；
  - 按设计顺序完成 Teams Start/Stop、Session 订阅和 Runtime Degraded 报告；
  - 对取消、Session 终态、Mailbox、Synthesis、Origin 回传的所有前后故障点运行
    Fault Injection。
- Verify:
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter 'FullyQualifiedName~MissionSynthesisTests|FullyQualifiedName~OriginDeliveryTests|FullyQualifiedName~CoWorkLifecycleTests|FullyQualifiedName~CoWorkDispatchFaultTests'`
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~SessionServiceTests|FullyQualifiedName~SessionRecoveryTests|FullyQualifiedName~WorkspaceRuntimeTests'`
- Acceptance contribution:
  - `M7-ACC-008`、`M7-ACC-009`、`M7-ACC-010`。
- Commit:
  - `feat(m7): complete mission synthesis and recovery`

### Outcome 9：交付 Wire 1.2 与角色化 Deferred Tools

- Red:
  - 在 `tests/OpenCoWork.Protocol.Tests/CoWorkWireTests.cs` 覆盖 1.2 方法、七域
    Changed 通知、Revision、Command Idempotency、权限和错误投影；
  - 扩展 `OpenCoWorkJsonRpcTests`，证明 1.0/1.1 客户端看不到 1.2 方法/事件；
  - 在 `CoWorkToolExposureTests.cs` 覆盖 Host、Leader、Member、Direct Parent 的
    Tool 子集和越权；
  - 扩展 `ProtocolProcessIntegrationTests` 和 TestClient，覆盖 stdio/WebSocket
    重连、慢读端、取消、通知重复和 Secret Canary。
- Work:
  - 在 `WireContracts.cs` 增加 Latest 1.2、M7 DTO、七个既有公共域的方法和单
    Changed 通知；
  - `OpenCoWorkJsonRpcConnection` 只作为 Adapter 调用 `ICoWorkService`，Host Actor
    来自已认证连接，不接受负载伪造身份；
  - 保持 Wire 1.0/1.1、ACP v1 和 server-to-client request 边界不变；
  - 在 Teams 注册 Leader、Member、Direct Parent 的最小 Deferred Tools，继续经过
    M6 Catalog、Snapshot、Authority、Policy、Approval、Hook 和 Binding Lease；
  - 不增加 `cowork` 公共域，不增加通用 action dispatcher；
  - 扩展 Protocol TestClient 形成 M7 黑盒场景，不把进程内测试冒充 Wire 证据。
- Verify:
  - `dotnet test tests/OpenCoWork.Protocol.Tests/OpenCoWork.Protocol.Tests.csproj -c Release --filter 'FullyQualifiedName~CoWorkWireTests|FullyQualifiedName~OpenCoWorkJsonRpcTests|FullyQualifiedName~AcpConnectionTests'`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter 'FullyQualifiedName~CoWorkToolExposureTests|FullyQualifiedName~ProtocolProcessIntegrationTests'`
- Acceptance contribution:
  - 为 `M7-ACC-001` 至 `M7-ACC-010` 增加 Wire/Tool 黑盒证据。
- Commit:
  - `feat(m7): expose cowork over wire 1.2`

### Outcome 10：关闭全量回归、双平台真机证据与交付资产

- Red:
  - 先让 Acceptance Catalog 的 M7 行保持 `Planned`，平台台账保持 `Pending`；
  - 缺任一 focused/full、Fault Injection、Wire 黑盒、Secret/残留或真机证据时，
    Closeout Check 必须失败。
- Work:
  - 运行全量 Release build/test、DAG/权限/状态性质测试、16 Run/256 Task 竞态和
    完整故障矩阵；
  - 分别为 App 与 Protocol TestClient 按 RID 独立 restore/publish，避免复用错误的
    RID assets；
  - 在 `win-x64` 与 `osx-arm64` 发布目录真机验证 Wire 1.0/1.1/1.2、Project、
    Worktree、Symlink/Junction/Reparse、Artifact、Secret Canary、取消进程树、
    Dirty Retention、恢复和 Origin Once；
  - 记录 Commit、平台、OS、SDK/runtime、Git、产物摘要、测试数量、命令和结果；
  - 不新增真实 Provider 声明；Provider Backlog 只在实际激活并验证后更新；
  - 同步 M7 Design/Plan 状态、M0 Capability Ledger、Acceptance Catalog、Platform
    Ledger、Milestone Checklist/INDEX 和 M7 Delivery Archive；
  - M7-ACC-001..010 全部 Passed 且两平台独立证据齐全后，才能把 M7 标为 Done。
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

  - 在对应真机运行发布目录 M7 TestClient 矩阵和残留检查，并把结果写入
    [双平台发布验证台账](../../platform-release-validation-ledger.md)。
- Acceptance contribution:
  - 关闭 `M7-ACC-001` 至 `M7-ACC-010`，或保持未满足项和对应平台为
    `Planned` / `Pending`。
- Commit:
  - `docs(m7): close multi-agent cowork delivery`

## 覆盖矩阵

| Outcome | 冻结决策 | 验收编号 |
| ---: | --- | --- |
| 1 | 2、10、14、15、16、18、21、22、24、25 | M7-ACC-002 |
| 2 | 2、8、16、17、18、19、24 | M7-ACC-002、005、009 |
| 3 | 3、4、12、14、15、16、20、22、26 | M7-ACC-002、003、008 |
| 4 | 5、7、18、19、23、25、26 | M7-ACC-006、007 |
| 5 | 1、8、9、11、16、17、21、22、25 | M7-ACC-001、005、009 |
| 6 | 3、4、5、8、11、12、13、15、20、22、23、25 | M7-ACC-003、005、008、009 |
| 7 | 6、7、14、16、17、19、21、26 | M7-ACC-004、006 |
| 8 | 3、9、11、13、15、16、24、26 | M7-ACC-008、009、010 |
| 9 | 10、14、16、21、24、26 | M7-ACC-001..010 Wire/Tool 证据 |
| 10 | 27 与全部关闭条件 | M7-ACC-001..010 |

27 项决策和 10 个验收编号都至少有一个主实现 Outcome 与一个最终关闭 Outcome。

## 停止条件与恢复边界

- Schema Backup、Migration 或完整性校验失败：不启动 Teams，不继续 Outcome 3；
- Command Receipt、Lease、Budget 或成员互斥出现重复/超卖：停止调度，不用内存锁
  掩盖数据库错误；
- Thread Descriptor 与 Teams 路由不一致：失败关闭，不回退全局 Workspace Root；
- Worktree 路径、Trust、摘要或 Dirty 状态不确定：保留目录，不自动删除或复用；
- Secret Canary 出现在任何持久层、日志、通知或测试输出：阻塞当前 Outcome；
- Wire 1.0/1.1 回归：阻塞 Wire 1.2 和 M7 Closeout；
- 任一目标平台缺少真机发布目录证据：对应平台保持 Pending，M7 不标 Done；
- 真实用户目录或真实 Provider 未获明确授权：只使用临时 Profile/Fake Provider。

## 完成定义

M7 只有在以下条件同时满足后才能标记 Done：

- 10 个 Outcome 都按 Red → Minimal → Focused → Full → Independent Commit 完成；
- 27 项冻结设计决策均有实现和验证证据；
- `M7-ACC-001` 至 `M7-ACC-010` 全部 Passed；
- Wire 1.0/1.1 回归与 Wire 1.2 黑盒全部通过；
- `win-x64` 与 `osx-arm64` 发布目录真机证据独立完成；
- Secret、路径逃逸、进程树、Dirty Worktree 和 Origin Once 检查通过；
- Design、Plan、Capability/Acceptance/Platform Ledger、Archive、Milestone
  Checklist 与 INDEX 已同步。

若用户显式延期任一平台真机证据，可以归档已完成实现并登记延期，但不得把该平台或
完整 M7 标为 Passed/Done。
