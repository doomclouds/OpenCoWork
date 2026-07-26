# OpenCoWork M2 Durable Session Core 实施计划

**Status:** Completed；Outcome 1-8 已完成。

**Goal:** 在不接入真实 Provider、AgentFactory 和工具的前提下，交付以
`ThreadJournal` 为权威事实源、可并发、可恢复的 Thread-Turn-Item Session Core。

**Why planning is required:** M2 同时修改公共 Session 契约、SQLite Schema、
Journal 持久化、并发与幂等、故障恢复、路径安全和 WorkspaceRuntime 健康状态，
属于跨模块、数据迁移和安全敏感工作，必须按依赖闭包推进并保留可恢复检查点。

**Acceptance:** `M2-ACC-001` 至 `M2-ACC-010` 全部具备自动化证据；Windows 真机
完成 Journal、SQLite、并发、崩溃恢复、Archive/Delete 和路径逃逸验证；
`osx-arm64` 交叉发布成功且 Mac 真机 Pending 项已登记；M2 最终只形成一份交付归档。

## Source Documents

- [M2 Durable Session Core 设计规格](../specs/2026-07-26-open-cowork-m2-durable-session-core-design.md)
- [M0 Contract Freeze](../specs/2026-07-25-open-cowork-m0-contract-freeze-design.md)
- [OpenCoWork Runtime 1.0 路线规格](../specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md)
- [M0 能力台账](../specs/2026-07-25-open-cowork-m0-capability-ledger.md)
- [M0-M10 验收目录](../specs/2026-07-25-open-cowork-m0-acceptance-catalog.md)
- 本机证据基线：`DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md`

本计划中的 Outcome 是一个 M2 内部的依赖结果，不是独立 Slice。Outcome 可以作为
提交边界，但不得创建独立规格、独立归档或提前把 M2 标记为 Done。

## Change Map

优先复用现有项目和基础设施，不新增项目、NuGet 包、通用 Store 接口或第二套模块
发现机制。

### 计划新增

| 路径 | 职责 |
| --- | --- |
| `src/OpenCoWork.Abstractions/SessionContracts.cs` | `ISessionService`、`ISessionExecutor`、不可变请求/快照、结果、错误和枚举。 |
| `src/OpenCoWork.Core/Configuration/SessionConfig.cs` | 三个已冻结 Session 配置项。 |
| `src/OpenCoWork.Core/Sessions/SessionDomain.cs` | Thread、Turn、Item、Queue 和 Interaction 的内部状态与转移。 |
| `src/OpenCoWork.Core/Sessions/ThreadJournal.cs` | 固定 JSONL 编码、Checksum、Flush、回放和损坏分类。 |
| `src/OpenCoWork.Core/Sessions/SessionProjection.cs` | Schema v2 Session 表的应用、查询、重建和水位。 |
| `src/OpenCoWork.Core/Sessions/SessionEventChannel.cs` | Snapshot/Resume 订阅和慢消费者隔离。 |
| `src/OpenCoWork.Core/Sessions/SessionExecution.cs` | Executor 意图、流式合并、等待、Resolution 和取消协调。 |
| `src/OpenCoWork.Core/Sessions/SessionRecovery.cs` | 启动扫描、Archive/Delete 协调、尾部修复和恢复。 |
| `src/OpenCoWork.Core/Sessions/SessionService.cs` | 公共门面、幂等、Gate、提交顺序、查询和调度。 |
| `src/OpenCoWork.Core/Sessions/SessionRuntime.cs` | Session DI 注册、启动恢复和停止生命周期，由 App 模块调用。 |

实现时可以在不改变上述职责边界的前提下合并过短文件；不得为单一实现额外创建
Repository、Factory 或一层转发接口。

### 计划修改

| 路径 | 修改目的 |
| --- | --- |
| `src/OpenCoWork.Core/State/StateRuntime.cs` | 生产 Schema v2 迁移、Session 表和 `synchronous = FULL`。 |
| `src/OpenCoWork.Core/Workspaces/WorkspacePaths.cs` | active/archived/deleting/recovery 的受控路径。 |
| `src/OpenCoWork.Core/Hosting/WorkspaceRuntime.cs` | Starting 阶段健康上报及 Running/Degraded 汇总。 |
| `src/OpenCoWork.App/Program.cs` | 组合根声明 `session`，令 `cli` 依赖它。 |
| `tests/OpenCoWork.Core.Tests/` | 领域、Journal、投影、Service、执行和恢复测试。 |
| `tests/OpenCoWork.IntegrationTests/` | 真实 SQLite/文件系统、进程中断和宿主集成测试。 |
| `tests/OpenCoWork.ArchitectureTests/ProjectGraphTests.cs` | 守卫 Session 契约归属和既有 13 项目边界。 |

## Execution Rules

- 每个 Outcome 先建立能够失败的聚焦测试，再实现到该 Outcome 的验收信号通过；
- 使用现有 xUnit v3、BCL、`Microsoft.Data.Sqlite`、`TimeProvider` 和
  `System.Threading.Channels`，不增加测试、序列化、事件总线或 ORM 依赖；
- 测试并发时使用 `Barrier`、`TaskCompletionSource` 或受控 Fault Point，不使用
  `Thread.Sleep` 猜时序；
- 一旦 Journal Flush 成功，后续错误只能返回已提交或待投影结果，不能报告回滚；
- 任一 Outcome 发现需要改变公共契约、权威数据源、提交顺序、安全顺序或验收语义，
  立即停止实现，先更新 M2 设计规格并确认；
- 未通过当前 Outcome 的聚焦测试和已有回归，不进入下一个 Outcome；
- 不在 M2 中补真实 Provider、Tool、Worktree、Wire 或后续里程碑占位实现。

### Outcome 1: Session 契约、状态机和 Schema v2 形成可编译基线

- Work:
  - 在 `SessionContracts.cs` 一次性定义 M2 真正消费的公共契约：UUIDv7 ID、
    Sequence、Thread/Turn/Item/Queue/Interaction 快照、修改请求、
    `SessionCommandResult<T>`、稳定 `SessionError`、订阅模式及
    `ISessionService`/`ISessionExecutor`。
  - 公共快照保持不可变；不公开 Journal、SQLite Row、Gate、Repository 或活动
    Executor 内部状态。
  - 在 `SessionDomain.cs` 实现唯一 Thread-Turn-Item 状态机，拒绝非法转换和终态
    回退；`AgentSession` 只作为 Executor 运行上下文，不成为第二聚合根。
  - 在 `SessionConfig.cs` 只公开
    `eventBufferCapacity=256`、`streamFlushInterval=50ms`、
    `streamFlushBytes=8192`，复用现有 Generated Config Schema。
  - 将 `StateMigrations` 的生产链推进至 v2，创建 `threads`、`turns`、`items`、
    `turn_queue`、`pending_interactions`、`session_idempotency` 和
    `session_operation_receipts`，同时添加外键、唯一约束、查询排序和幂等定位所需
    索引。
  - 让新库和 v1 升级都落到 v2；继续复用现有 WAL Checkpoint、Backup API、恢复和
    `StateWriteCoordinator`，连接策略改为 `synchronous = FULL`。
  - 为 `OpenCoWorkPaths` 增加受控 Session Runtime 路径，不接受调用方路径。
- Risks/open questions:
  - v1→v2 任一步失败必须恢复 v1 备份并阻断启动，不能留下部分 Session 表。
  - 枚举文本、时间、UUID 和 JSON 属性顺序必须与设计规格一致。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release`
  - `dotnet test tests/OpenCoWork.ArchitectureTests/OpenCoWork.ArchitectureTests.csproj -c Release`
  - `dotnet build OpenCoWork.slnx -c Release --no-restore`
- Acceptance contribution:
  - `M2-ACC-001`
  - `M2-ACC-003`

### Outcome 2: ThreadJournal 能确定提交、回放和隔离损坏

- Work:
  - 在 `ThreadJournal.cs` 使用 `System.Text.Json.Utf8JsonWriter` 产生固定属性顺序、
    UTF-8 无 BOM、LF、lowerCamel 类型和 Ordinal Dictionary 排序的单行 JSON。
  - Checksum 对不含 `checksum` 属性的规范 JSON 原始字节计算小写 SHA-256；
    Reader 同时校验 Schema、文件名/Thread ID、UUIDv7、Sequence 和 Checksum。
  - Writer 每次提交只打开一个目标文件，使用 `FileShare.Read` 追加完整 Entry 与
    LF，持久化 Flush 后关闭；不实现句柄池。
  - 实现 1 MiB Entry、256 KiB 文本/Checkpoint 的写前限制，超限不占 Sequence。
  - 提供写前、半行、Flush 前、Flush 后等内部 Fault Point，Fault Point 只供
    `InternalsVisibleTo` 测试使用，不进入公共 API 或配置。
  - 回放只自动接受可证明安全的尾部截断：先写 recovery 备份和恢复意图，再
    `SetLength`、Flush、追加 `ThreadJournalRecovered` 并重建。
  - LF 终止的坏 Checksum、中段坏行、Sequence 缺口/重复、未知 Schema 和 ID
    不一致保持原文件不动，并把该 Thread 标记为 `RecoveryRequired`。
- Risks/open questions:
  - 不能因为 JSON 可解析就跳过 Checksum 或 Sequence 校验。
  - 自动截断范围不确定时必须拒绝修复，不能“尽量读取”。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release`
  - `git diff --check`
- Acceptance contribution:
  - `M2-ACC-002`
  - `M2-ACC-010`

### Outcome 3: SQLite 投影可以重建、追平和从降级恢复

- Work:
  - 在 `SessionProjection.cs` 通过现有 `StateWriteCoordinator` 按 Entry Sequence
    应用 v2 投影，并原子更新 `lastAppliedSequence`。
  - 投影应用保持幂等：已应用 Sequence 可跳过，缺口和内容冲突必须停止；普通
    Session 表可以从 Journal 全量重建。
  - 实现 threads/turns/items/queue/interactions/idempotency 的规范化快照对比，
    以及仅用于删除重放的 7 天最小回执。
  - 删除投影后按 active、archived、deleting 的稳定顺序回放；没有 Journal 的孤立
    投影被删除并记录高优先级诊断。
  - Journal 已 Flush 而投影失败时，将命令结果固定为
    `CommittedPendingProjection`，阻止新工作、保留待发事件，并从水位追平。
  - 追平完成后按 Sequence 释放事件并清除 Degraded；单 Thread
    `RecoveryRequired` 不升级为全 Workspace 投影降级。
  - 为投影事务前后增加确定性 Fault Point，测试失败重启和重复应用。
- Risks/open questions:
  - 投影恢复不能重写 Journal，也不能把投影失败解释成 Journal 回滚。
  - `session_operation_receipts` 是唯一不可从 Journal 重建的 Session 表面。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release`
- Acceptance contribution:
  - `M2-ACC-003`
  - `M2-ACC-004`

### Outcome 4: ISessionService 统一提交、查询、幂等、并发和事件

- Work:
  - 在 `SessionService.cs` 实现唯一公共门面；Create、Rename、Pause、Resume 等
    状态修改统一经过 Idempotency Key Gate、Thread Gate、Journal、内存、投影、
    Event 的固定顺序。
  - 幂等范围为 Workspace 全局：相同请求返回第一次结果，不同操作/Thread/指纹
    复用相同 Key 返回 `session.idempotencyConflict`；CreateThread 的 Key 可由
    SQLite 索引定位并从 Journal 重建。
  - 每个 Thread 保留一个 `ThreadWriteGate`，不同 Thread 不共享 Journal 锁；
    SQLite 写入仍复用全 Workspace `StateWriteCoordinator`，严格禁止反向拿锁。
  - 维护不可变聚合快照和最小 Active Turn 运行时索引；查询不拿写锁。
  - 实现 `GetThread`、`ListThreads`、`ReadHistory`、
    `GetSessionStatistics`、DisplayName/FirstUserMessage 最小搜索，以及
    Degraded/RecoveryRequired 查询边界。
  - 在 `SessionEventChannel.cs` 实现 SnapshotThenLive、
    ResumeAfterSequence、ResetRequired、单订阅者有界 Channel 和
    `session.subscriberLagged`。
  - 并发测试用 Barrier 精确证明同 Thread 串行、不同 Thread 同时进入 Journal
    写阶段、Sequence 冲突无写入、慢订阅者不阻塞提交。
- Risks/open questions:
  - Query 不能把 SQLite 投影重新升级为权威源。
  - 订阅建立的快照与水位必须在同一 Thread Gate 临界区取得。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release`
- Acceptance contribution:
  - `M2-ACC-001`
  - `M2-ACC-004`

### Outcome 5: Executor、流式 Item、等待和取消可以确定恢复

- Work:
  - 在 `SessionExecution.cs` 让 `ISessionExecutor` 只接收不可变
    `AgentSession` Context 并向 Sink 产生意图；所有意图回到
    `SessionService` 校验和提交。
  - M2 生产代码不注册假 Provider 或 Echo Agent；没有注入 Executor 时以
    `runtime.executorUnavailable` 拒绝开始执行。测试通过内部构造入口注入确定性
    Scripted Executor。
  - 使用 `TimeProvider` 和 UTF-8 字节计数实现 50 ms / 8 KiB Delta 合并；等待、
    Cancel、Item/Turn 终态和正常停止前强制 Flush。
  - `ItemCompleted` 只写长度、摘要和状态；回放聚合已提交 Delta，未提交缓冲不
    可见。
  - Approval/UserInput 等待事实保存请求、超时和带 Schema/Checksum 的
    `SessionExecutionCheckpoint`；第一次有效 Resolution 持久化后保持 Waiting，
    直到 Executor 接受并提交 `ExecutionResumed`。
  - Resolution 与 Cancel 在同一 Thread Gate 按 Sequence 决胜；重启时恢复有效
    Checkpoint，没有 Checkpoint 的活动 Turn 以 `runtime.interrupted` 失败。
  - 测试覆盖 Resolution 后 Resume 前中断、重复 Resolution、超时、Cancel 竞态、
    Executor 丢失、流式终态前强制 Flush 和已提交内容保留。
- Risks/open questions:
  - Executor 不得拿到 Journal、Projection 或 Aggregate 的可变引用。
  - Journal 已提交后的调用方 Cancellation 不得抹掉提交结果。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release`
- Acceptance contribution:
  - `M2-ACC-001`
  - `M2-ACC-006`

### Outcome 6: Queue、调度、重排和 Steer 在重启后保持确定顺序

- Work:
  - 所有输入先成为 `QueuedTurnInput`；只有 Active 且空闲的 Thread 在 Enqueue
    提交后调度队首并创建 Turn，Paused/Busy Thread 只持久排队。
  - Queue 上限固定为 128；Remove 只接受未调度项；Reorder 必须提交当前完整、
    无重复的 Queue Item ID 列表并持久化最终顺序。
  - 调度、Remove、Reorder 全部复用 Thread Gate、幂等和
    `expectedSequence`，重启只依赖 Journal 恢复，不依赖内存队列。
  - Steer 同时校验 `expectedTurnId`、Queue Item 和 Sequence；单个
    `TurnSteered` 事实移除队列项并向当前 Turn 追加模型可见 UserMessage。
  - Steer 提交后再通知 Executor；Executor 丢失或拒绝时保留历史、不重新入队，
    当前 Turn 失败后尝试调度下一项。
  - 测试覆盖随机操作序列重放、重复幂等请求、重排非法集合、Steer/Turn 终态竞态
    和重启后的最终顺序。
- Risks/open questions:
  - 不创建 `Queued` Turn 状态，也不让调度通知先于 Queue Journal 提交。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release`
- Acceptance contribution:
  - `M2-ACC-005`

### Outcome 7: Archive、Delete、Fork、Rollback 和 Reconciler 闭合管理语义

- Work:
  - Archive/Unarchive 只允许空闲、空 Queue、无维护操作的 Thread，并按
    Journal Flush、目录移动、投影、事件顺序执行；Reconciler 从任一阶段继续。
  - `PrepareDelete` 校验 Archived、Sequence 和空闲状态，使用 BCL
    `RandomNumberGenerator` 产生内存-only、一次性、256-bit、2 分钟 Token。
  - Delete 消费 Token 后先 Flush `ThreadDeletionRequested`，再移动至 deleting、
    标记投影、清理 Session Core 明确拥有的 Runtime 文件、删除投影/Journal 并写
    7 天最小回执。
  - 每次删除前复用并加强现有 `WorkspacePathGuard` 做绝对路径、根包含、Symlink、
    Junction 和 Reparse Point 写前复检；M2 不创建 Worktree 绑定接口，也从不删除
    Worktree 或 Runtime Root 外文件。
  - Fork 只在稳定 Sequence 捕获源快照，释放源 Gate 后创建独立目标；目标首个
    `ThreadForked` Entry 包含完整模型历史 Checkpoint，不复制 Queue、等待、
    Runtime 所有权或未来 Worktree。
  - Rollback 只允许空闲 Active/Paused Thread，追加 `ThreadRolledBack` 和替换
    Checkpoint，保留审计事实并返回 `externalSideEffectsReverted=false`。
  - 在 `SessionRecovery.cs` 统一协调 active/archived/deleting、尾部恢复意图、
    孤立投影和中断 Turn；不另建多个后台 Reconciler 框架。
  - 测试覆盖 Archive/Delete 每个故障点、过期/重复/错 Thread Token、链接逃逸、
    删除源后的 Fork 回放、Rollback 有效历史和 RecoveryRequired 对其他 Thread
    的隔离。
- Risks/open questions:
  - `ThreadDeletionRequested` Flush 后必须无需 Token 自动续跑。
  - 路径或所有权复检失败必须停止删除，不得“清理剩余内容”。
  - 无法证明安全的 Journal 中段损坏不得由 Reconciler 自动修复。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release`
- Acceptance contribution:
  - `M2-ACC-007`
  - `M2-ACC-008`
  - `M2-ACC-009`
  - `M2-ACC-010`

### Outcome 8: SessionModule、真实故障验证和 M2 统一收口

- Work:
  - 在 Core 的 `SessionRuntime.cs` 注册 Session Config、State v2、Service、
    Projection、Recovery 和生命周期。
  - 在 App 组合根声明实现 `IOpenCoWorkModule` 的 `SessionModule`，模块 ID 为
    `session` 且不具备主宿主资格；令现有 `cli` 依赖 `session`。
  - `SessionModule` 只调用 Core 的注册与生命周期入口，不复制 Session 状态；
    保持 App 是唯一 Generated Runtime Catalog 聚合入口，不新增项目或手写 Catalog。
  - 调整 `WorkspaceRuntime`：模块可在 Starting 阶段报告 Degraded，全部模块启动
    后按健康汇总为 Running/Degraded；Schema 迁移失败仍进入 Faulted。
  - 集成测试验证启动顺序为 State v2、目录扫描、管理协调、Journal 回放、投影追平、
    等待恢复、Ready、CLI；停止按设计顺序拒绝新工作、取消执行、Flush 和释放资源。
  - 进程中断测试复用 `OpenCoWork.IntegrationTests` 的 xUnit v3 可执行程序集：
    父测试通过环境开关启动同一测试程序集的指定 Child 场景，Child 在 Fault Point
    终止，父测试随后重新打开 Workspace 并断言恢复；不新增 Harness 项目，也不向
    产品 CLI 暴露测试命令。
  - 在普通权限 Windows 完成 restore、Release build、完整 test、`win-x64`
    publish 和 Session Runtime 集成场景；原生 Symlink 场景只在需要时使用现有临时
    专项提权方式。
  - 交叉发布 `osx-arm64`，只证明产物生成；确认 `AGENTS.md` 的 M2 Mac 真机台账
    包含 Journal、文件锁、原子移动、并发、恢复和链接安全。
  - 用实际测试类、命令和结果更新 `M2-ACC-001` 至 `M2-ACC-010`；全部通过后才把
    M2 标记 Done、同步 `docs/milestones/INDEX.md` 并生成唯一 M2 交付归档。
- Risks/open questions:
  - xUnit Child 场景必须使用独立临时 Workspace，父进程必须设置超时并清理残留子
    进程；测试输出不得泄露绝对路径或 Journal 内容。
  - Windows 交叉发布不能写成 macOS 真机通过；Mac Pending 必须保留到实际回填。
- Verify:
  - `dotnet restore OpenCoWork.slnx`
  - `dotnet build OpenCoWork.slnx -c Release --no-restore`
  - `dotnet test OpenCoWork.slnx -c Release --no-build`
  - `dotnet publish src/OpenCoWork.App/OpenCoWork.App.csproj -c Release -r win-x64 --self-contained false`
  - `dotnet publish src/OpenCoWork.App/OpenCoWork.App.csproj -c Release -r osx-arm64 --self-contained false`
  - 必要时以临时专项权限运行 Session Delete Symlink 安全测试
  - `git diff --check`
- Acceptance contribution:
  - `M2-ACC-001` 至 `M2-ACC-010`

## M2 Completion Gate

只有同时满足以下条件才能关闭 M2：

- 八个 Outcome 的聚焦测试和完整回归全部通过；
- `M2-ACC-001` 至 `M2-ACC-010` 均从 Planned 更新为 Passed，并链接实际证据；
- Windows 真机 Journal、SQLite、并发、故障恢复和路径安全验证完成；
- `win-x64` 发布产物可运行，`osx-arm64` 交叉发布成功；
- `AGENTS.md` 中 M2 macOS 真机项完整且保持 Pending；
- 没有未解释的 skipped test、生成文件、Journal、SQLite、子进程或临时目录残留；
- M2 交付归档、里程碑 CHECKLIST 和 INDEX 同步完成；
- 根目录 DotCraft 证据文档仍被忽略，未进入任何提交。
