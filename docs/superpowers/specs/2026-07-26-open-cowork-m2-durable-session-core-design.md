# OpenCoWork M2 Durable Session Core 设计规格

## 文档状态

- 状态：已完成
- 日期：2026-07-26
- 所属里程碑：OpenCoWork Runtime 1.0 / M2
- 当前已完成阶段：M2 Durable Session Core
- 目标框架：.NET 10
- 当前正式验证平台：`win-x64`
- macOS 真机验证：滚动登记于仓库 `AGENTS.md`，统一在 M11 / 1.0 发布前清零
- 原始能力参考：
  [DotCraft 核心运行时复刻规范](../../../DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md)
- M0 冻结契约：
  [OpenCoWork M0 Contract Freeze](2026-07-25-open-cowork-m0-contract-freeze-design.md)
- 路线规格：
  [OpenCoWork Runtime 1.0 路线规格](2026-07-25-open-cowork-runtime-1-0-roadmap.md)
- 能力台账：
  [OpenCoWork M0 能力台账](2026-07-25-open-cowork-m0-capability-ledger.md)
- 验收目录：
  [OpenCoWork M0-M11 验收目录](2026-07-25-open-cowork-m0-acceptance-catalog.md)

## 1. 目标

M2 在没有真实 Provider、AgentFactory 和工具执行的前提下，交付可持久化、可并发、
可恢复的 Thread-Turn-Item Session Core。

完成后的 Session Core 必须满足：

- `ThreadJournal` 是会话状态和模型可见历史的唯一权威事实源；
- 同一 Thread 的修改严格串行，不同 Thread 可以并行；
- SQLite 只保存可重建查询投影和明确声明的最小删除回执；
- Turn、Item、排队输入、交互等待和管理操作可以在进程中断后恢复或确定终态；
- 所有外部事件只在权威事实提交后发布，并保持单 Thread Sequence 顺序；
- Protocol、CLI 和后续宿主只能通过 `ISessionService` 使用会话能力。

M2 作为一个完整任务交付。本文中的 Outcome 只表达实施依赖顺序，不形成独立 Slice，
也不单独验收、归档或关闭。

## 2. 范围

### 2.1 纳入范围

- Thread 创建、读取、列表、搜索、统计、重命名、暂停、恢复、归档、反归档和删除；
- Turn 创建、运行、等待、恢复、完成、失败和取消；
- Item 创建、流式追加和终态；
- Approval 与 UserInput 等待、首次 Resolution 和恢复；
- 同 Thread 串行、不同 Thread 并行；
- 输入队列的追加、删除、重排和 Steer；
- 普通 Thread Fork 与 Rollback；
- `SessionEventChannel` 快照订阅与 Sequence 续订；
- `ThreadJournal` 写入、回放、尾部修复、损坏隔离和 SQLite 投影重建；
- 确定性的 `ISessionExecutor` 脚本化测试实现；
- Windows 真机验证和 macOS ARM64 待验证台账。

### 2.2 明确不包含

- 真实 Provider、Model、AgentFactory 或上下文压缩；
- 真实工具、`ToolInvocationPipeline`、工具审批或外部副作用执行；
- Worktree Fork；
- 完整 Goal、预算、SubAgent、Team、Mission 或 Mailbox；
- Attachment、Artifact 或 Scratchpad 完整子系统；
- OpenCoWork Wire、ACP、AppServer 或 Gateway；
- FTS5、模糊搜索、向量搜索或高级查询语言；
- Journal 中段自动修复工具；
- DotCraft `.craft`、程序集、持久化格式或私有实现兼容。

## 3. 领域词汇与所有权

M2 使用以下 OpenCoWork 词汇：

| 名称 | 职责 |
| --- | --- |
| `AgentSession` | 提供给 Executor 的单次运行上下文；不是第二个持久化聚合根。 |
| `Thread` | 用户可管理的持久会话边界。 |
| `Turn` | Thread 中一次被调度执行的工作。 |
| `SessionItem` | Turn 内的输入、输出、等待请求、响应或诊断事实。 |
| `QueuedTurnInput` | 尚未创建 Turn 的排队输入，不伪装成 Queued Turn。 |
| `ISessionService` | 对外唯一会话门面和状态修改入口。 |
| `SessionService` | Core 中的门面实现和提交协调者。 |
| `ThreadJournal` | Thread 的权威追加日志，替代 `Rollout` 语义。 |
| `SessionEventChannel` | 按 Thread Sequence 发布和续订核心事件。 |
| `ISessionExecutor` | 接收不可变执行上下文并产生执行意图。 |
| `ThreadWriteGate` | 单 Thread 的写入串行边界。 |

`ToolInvocationPipeline` 是 M4 术语，M2 不创建占位类型。M2 不使用
`ModelSession`、`Rollout`、`ToolDispatcher`、`Mailbox`、通用 `Manager` 或
通用 `Repository` 表达上述职责。

## 4. 总体架构

```text
调用方
→ ISessionService
→ IdempotencyKeyGate
→ ThreadWriteGate
→ 校验状态与 expectedSequence
→ 追加并 Flush ThreadJournal
→ 更新不可变内存聚合
→ SQLite 事务更新投影与 lastAppliedSequence
→ 发布 SessionEvent
→ 释放 ThreadWriteGate
```

反向恢复路径：

```text
ThreadJournal
→ 校验并回放权威事实
→ 重建不可变聚合
→ 重建或追平 SQLite 投影
→ 恢复等待检查点或确定中断终态
→ 开放查询、订阅和新工作
```

固定原则：

- 一个 `ThreadJournalEntry` 表达一个持久领域事实；
- 一个 Entry 占用一个唯一 Sequence；
- 一个已提交 Entry 对应一个核心 `SessionEvent`；
- 多阶段操作使用有序、可续跑的领域事实和 Reconciler，不把多个事实塞进
  `changes[]` 批次；
- SQLite 事务不能伪装成 Journal 与数据库之间的原子事务；
- Journal Flush 是唯一提交点。

## 5. 公共契约

### 5.1 门面边界

`OpenCoWork.Abstractions` 只公开：

- `ISessionService`；
- 不可变 Thread、Turn、Item、Queue、Interaction 和统计快照；
- 明确的请求 record；
- `SessionCommandResult<T>`、`SessionError` 和订阅结果；
- `ISessionExecutor` 所需的稳定上下文与意图契约。

Journal Writer、Projector、Reconciler、SQLite 行模型、Gate、活动 Turn 注册表和
故障注入器均留在 `OpenCoWork.Core` 内部。不得公开 Store 或 Repository 让调用方
绕过 `ISessionService`。

### 5.2 标识与时间

- Thread、Turn、Item、Queue Item、Entry、Interaction 和 Idempotency Key 均使用
  UUIDv7 `Guid`；
- Wire 或 JSON 表达统一为小写 UUID；
- 外部传入的 ID 必须校验版本和格式；
- Sequence 使用从 1 开始的 `long`；
- 创建 Thread 的 `expectedSequence` 为 `0`；
- 公共时间使用 UTC `DateTimeOffset`；
- SQLite 时间使用 Unix 毫秒；
- JSON 时间使用 RFC 3339 UTC；
- 不为每种 ID 创建只包一层 `Guid` 的 record struct。

### 5.3 修改结果

```text
SessionCommandStatus
├─ Rejected
├─ Committed
└─ CommittedPendingProjection
```

`SessionCommandResult<T>` 至少携带：

- `Status`；
- 成功时的 `Value`；
- 已提交时的 `Sequence`；
- 并发冲突时的 `CurrentSequence`；
- 失败或降级时的 `SessionError`。

业务拒绝使用稳定结果，不抛异常。参数编程错误可以抛
`ArgumentException`。`OperationCanceledException` 只允许发生在 Journal 提交前；
一旦 Flush 成功，调用必须返回已提交结果，不能伪装成取消或回滚。

### 5.4 修改请求

所有持久修改请求必须携带：

- `idempotencyKey`；
- `expectedSequence`；
- 操作所需的不可变参数。

`PrepareDelete` 只签发短期能力令牌，不修改 Thread 状态，因此不是持久修改；它必须
校验 `expectedSequence`，但不进入持久幂等日志。查询和订阅也不需要
`idempotencyKey`。

## 6. 状态模型

### 6.1 Thread

```text
Active ⇄ Paused
Active ─┐
Paused ─┴→ Archived → Active
```

- 新 Thread 默认为 `Active`；
- `Pause` 只允许在没有活动 Turn 时执行；
- Paused Thread 可以重命名和排队，但不得调度；
- `Resume` 回到 `Active` 并尝试调度队首输入；
- `Archive` 只允许在没有活动 Turn、没有维护操作且队列为空时执行；
- `Unarchive` 固定回到 `Active`；
- `Deleting` 是维护阶段，不是正常 Thread 状态；
- `RecoveryRequired` 是独立可用性状态，不改写原 Thread 状态。

### 6.2 Turn

```text
Running ⇄ WaitingApproval
Running ⇄ WaitingInput
Running → Completed | Failed | Cancelled
WaitingApproval → Failed | Cancelled
WaitingInput → Failed | Cancelled
```

- Turn 只在调度 `QueuedTurnInput` 时创建；
- Resolution 已提交但 Executor 尚未确认恢复时，Turn 仍保持 Waiting；
- 只有 `ExecutionResumed` 事实将 Waiting 状态转回 Running；
- `Completed`、`Failed` 和 `Cancelled` 都是不可逆终态；
- 同一 Thread 同时最多一个非终态 Turn。

### 6.3 Item

```text
Started → Streaming → Completed
Started → Completed
Started | Streaming → Failed | Cancelled
```

M2 支持：

- `UserMessage`
- `AgentMessage`
- `Reasoning`
- `ApprovalRequest`
- `ApprovalResponse`
- `UserInputRequest`
- `UserInputResponse`
- `Error`
- `SystemNotice`

流式类型只有 `AgentMessage` 和 `Reasoning`。其余类型不流式。M2 输入只接受非空
文本，不提前创建 Tool、Image、Command 等空类型。

模型可见性由 Core 根据 Item 类型决定，不允许调用方传入任意 `isModelVisible`：

- `UserMessage`、`AgentMessage`、`ApprovalResponse` 和 `UserInputResponse`
  进入模型可见历史；
- `Reasoning`、请求类 Item、`Error` 和 `SystemNotice` 不进入模型可见历史。

Thread 保留 `HistoryMode` 契约，但 M2 只接受 `Server`；`Client` 留给 M5。

## 7. ThreadJournal

### 7.1 物理布局

```text
.opencowork/runtime/threads/
├─ active/{threadId}.jsonl
├─ archived/{threadId}.jsonl
└─ deleting/{threadId}.jsonl
```

- 文件名是小写 UUIDv7；
- 路径完全由 `OpenCoWorkPaths` 和 Thread ID 生成；
- 调用方不能提供 Journal 路径；
- 文本编码为 UTF-8 无 BOM；
- 行尾固定为 LF；
- 每个 Entry 恰好占一行。

### 7.2 Entry 格式

属性顺序固定为：

```text
schemaVersion
threadId
sequence
entryId
timestamp
entryType
idempotencyKey
payload
checksum
```

约束：

- `schemaVersion` 在 M2 从 1 开始；
- `entryId` 和维护操作生成的 `idempotencyKey` 使用 UUIDv7；
- `sequence` 从 1 开始且严格递增；
- `timestamp` 使用 RFC 3339 UTC；
- `entryType` 使用稳定 lowerCamel 文本；
- `payload` 使用强类型序列化；
- Dictionary 按 Ordinal Key 排序；
- 编码后的单行 JSON 不含 LF 时最大 `1 MiB`；
- `checksum` 是小写 SHA-256。

校验和计算对象是按固定顺序序列化、但完全不含 `checksum` 属性的 JSON 对象原始
UTF-8 字节。最终写入时在其余属性后追加 `checksum`，不得重新格式化或使用缩进。

### 7.3 写入

每次提交：

1. 以 `FileShare.Read` 打开目标 Journal；
2. 定位到文件末尾；
3. 追加完整 Entry 与 LF；
4. 调用持久化 Flush；
5. 关闭文件。

M2 不实现常驻句柄池。未完成写入或未完成 Flush 的字节不构成提交，不能更新内存、
SQLite 或外部事件。

故障注入点至少包括：

- 打开或写入前；
- 写到半行；
- 完整写入但 Flush 前；
- Flush 后、内存更新前；
- 内存更新后、SQLite 投影前；
- SQLite 提交后、事件发布前。

## 8. 幂等与并发

### 8.1 幂等

幂等范围是 Workspace 全局，而不是单 Thread：

- 相同 Key、相同操作、相同目标和相同请求指纹返回第一次结果；
- 不重复追加 Journal、更新投影或发布事件；
- 相同 Key 被不同操作、Thread 或请求内容使用时返回
  `session.idempotencyConflict`；
- 请求仍在执行时，重复调用附着到同一进行中结果；
- Journal 已提交但投影未完成时，重复调用返回同一
  `CommittedPendingProjection` 结果；
- 启动恢复可从 Journal 重建普通幂等索引；
- CreateThread 的 Key 通过全局 SQLite 索引快速定位，索引丢失时通过 Journal
  扫描重建；
- 永久删除后的重复结果由最小删除回执提供。

请求指纹由稳定操作名、目标 Thread、`expectedSequence` 和规范化请求载荷计算，
不包含 Idempotency Key 自身。

### 8.2 锁顺序

唯一允许的顺序：

```text
IdempotencyKeyGate
→ ThreadWriteGate
→ StateWriteCoordinator
```

- Idempotency Gate 按 Key 分片；
- Thread Gate 每 Thread 一个；
- SQLite 写入继续复用 M1 `StateWriteCoordinator`；
- 不同 Key、不同 Thread 的 Journal 写入可以并行；
- 不得持有 SQLite 写锁再获取 Thread Gate；
- Fork 不同时持有源和目标两个 Thread Gate；
- Projector 遵守相同锁顺序；
- 查询读取不可变快照，不获取写锁；
- 订阅只在建立原子水位时短暂进入 Thread Gate；
- `ActiveTurnRegistry` 只是运行时索引，不是权威状态；
- Thread Gate 在 Thread 存续期间保留，删除完成后移除；M2 不实现复杂闲置淘汰。

`expectedSequence` 必须在 Thread Gate 内校验。不匹配时返回
`session.sequenceConflict`，不写 Journal。

## 9. SQLite Schema v2

M2 将 M1 State Schema 从 v1 迁移为 v2，并将：

```text
synchronous = NORMAL
```

改为：

```text
synchronous = FULL
```

其余 M0 PRAGMA、WAL、Foreign Key、Busy Timeout、备份、恢复和迁移状态规则保持
不变。

### 9.1 最小表面

| 表 | 最小职责 |
| --- | --- |
| `threads` | Thread 状态、可用性、标题、Sequence、水位、活动 Turn 和搜索字段。 |
| `turns` | Turn 状态、起止时间、错误和所属 Thread。 |
| `items` | Item 类型、状态、顺序、物化内容和摘要。 |
| `turn_queue` | 排队输入、最终位置和创建时间。 |
| `pending_interactions` | 等待请求、Resolution、超时和恢复检查点。 |
| `session_idempotency` | Workspace 全局请求指纹、状态、结果和提交 Sequence。 |
| `session_operation_receipts` | 永久删除后的最小幂等回执。 |

所有外键启用级联删除。枚举使用 lowerCamel 文本，时间使用 Unix 毫秒，复杂载荷使用
属性顺序确定的 JSON。

除 `session_operation_receipts` 外，上述数据均可从 Journal 重建。
`session_operation_receipts` 只保存：

- Thread ID 的 SHA-256；
- Idempotency Key 的 SHA-256；
- 完成时间；
- 最小结果；
- 到期时间。

它不保存用户内容、显示名称、文件路径、原始 ID 或 Token，默认保留 7 天。

### 9.2 投影水位

- 每个 Thread 保存 `currentSequence` 和 `lastAppliedSequence`；
- Projector 只按 Sequence 升序应用；
- 重复 Entry 必须幂等跳过；
- Sequence 缺口不得跨越；
- 完整重建前清空可重建 Session 表，再从各 Journal 回放；
- 重建前后列表、历史、搜索和统计的规范化结果必须一致；
- 找不到对应 Journal 的孤立投影必须删除并产生高优先级诊断。

## 10. 投影降级

Journal Flush 成功而 SQLite 投影失败时：

1. 返回 `CommittedPendingProjection`；
2. 结果携带已提交 Sequence 和 `session.projectionUnavailable`；
3. WorkspaceRuntime 进入 `Degraded`；
4. 停止接收新工作；
5. 允许维护写入将已开始 Turn 取消或确定终态；
6. 暂存尚未发布的外部事件；
7. Projector 从 `lastAppliedSequence` 追平；
8. 追平后按 Sequence 发布暂存事件；
9. 清除降级并重新开放工作。

不得把已 Flush 的事实报告为回滚。单 Thread Journal 损坏只影响该 Thread，不使整个
Workspace 进入 Projection Degraded。

## 11. SessionEventChannel

核心事件按 Thread 提供至少一次、有序投递。消费者使用 `(threadId, sequence)` 去重。

### 11.1 SnapshotThenLive

1. 在 Thread Gate 内捕获不可变快照和水位 `H`；
2. 注册只接收 `sequence > H` 的 Live 订阅；
3. 返回快照、`H` 和订阅；
4. 释放 Gate。

### 11.2 ResumeAfterSequence

1. 注册 Live 订阅并捕获水位 `H`；
2. 从 Journal 回放 `K + 1` 至 `H`；
3. 再投递 Live 中 `H + 1` 之后的事件。

Resume 模式不重复发送当前快照。无效、超前或无法连续回放的 Cursor 返回
`ResetRequired` 和当前快照。

### 11.3 慢订阅者

- 每个订阅者使用有界 Channel；
- 默认容量为 256；
- Thread Gate 内只使用非阻塞写入；
- Channel 满时只断开该订阅者，并返回 `session.subscriberLagged`；
- 慢订阅者不能阻塞 Journal 提交、其他订阅者或其他 Thread。

## 12. Executor 与流式 Item

`ISessionExecutor` 只负责：

```text
ExecuteAsync(context, sink, cancellationToken)
```

- `context` 是不可变 `AgentSession` 执行上下文；
- `sink` 只提交执行意图；
- Executor 不直接修改聚合、Journal、SQLite 或事件；
- `SessionService` 校验意图并完成全部持久化和状态迁移；
- M2 只实现可脚本化、可暂停、可故障注入的确定性 Executor；
- M3 在同一边界接入真实 Agent Runtime。

### 12.1 流式批处理

Executor 产生的小 Delta 由 Session Core 合并，满足任一条件即提交：

- 距离上次提交达到 50 ms；
- 累积 UTF-8 内容达到 8 KiB。

每个批次写入一个 `ItemDeltaAppended` Entry，占用一个 Sequence，并只在 Flush 后
发布对应事件。

以下情况必须先强制提交已缓冲 Delta：

- Item 或 Turn 进入终态；
- Turn 进入等待；
- 取消；
- 正常停止。

`ItemCompleted` 只保存最终长度、摘要和状态，不在 Journal 中重复完整内容。SQLite
投影可以物化内容。进程中断时，已提交 Delta 保留，未提交 Delta 丢弃；Item 和 Turn
以 `runtime.interrupted` 进入失败终态。

## 13. 等待、Resolution 与取消

等待事实必须包含：

- Interaction ID；
- 请求 Item；
- 交互类型；
- 可选超时；
- `SessionExecutionCheckpoint`。

Checkpoint 固定包含：

- `executorKind`；
- `schemaVersion`；
- `payload`；
- `checksum`。

规则：

- Checkpoint 缺失、不支持或损坏时使用 `runtime.continuationMissing`；
- 第一次有效 Resolution 被持久化并成为唯一结果；
- 相同请求重放返回第一次结果；
- Resolution 提交后 Turn 仍保持 Waiting；
- Executor 真正接受续接后写入 `ExecutionResumed`，Turn 才回到 Running；
- Resolution 与 Cancel 在同一 Thread Gate 内按 Sequence 决胜；
- Cancel 先提交时，后续 Resolution 被拒绝；
- Resolution 先提交但尚未恢复时，Cancel 仍可按下一 Sequence 取消；
- 崩溃发生在 Resolution 与 Resume 之间时，启动恢复从 Checkpoint 继续；
- 没有可恢复 Checkpoint 的 Running Turn 在重启时以
  `runtime.interrupted` 失败。

超时由 `TimeProvider` 驱动并转换为普通持久化失败或取消事实，测试不得等待真实时间。

## 14. 队列与 Steer

### 14.1 QueuedTurnInput

- 排队输入是独立持久对象，不提前创建 Turn；
- Enqueue 总是先写 Journal；
- Thread 空闲且 Active 时，提交后调度队首并创建 Turn；
- Thread 忙碌或 Paused 时保持排队；
- 每个 Thread 最多 128 项；
- 只能删除尚未调度的项；
- 重排请求提交完整 Queue Item ID 列表；
- 重排在 Thread Gate 内校验 `expectedSequence`、成员完整性和唯一性；
- Journal 保存重排后的最终顺序，不保存脆弱的移动指令。

### 14.2 Steer

Steer 必须同时满足：

- Thread 有预期的活动 Turn；
- `expectedTurnId` 匹配；
- 目标 Queue Item 仍未调度；
- `expectedSequence` 匹配。

一次 `TurnSteered` 领域事实同时表达：

- 从队列移除目标输入；
- 向当前 Turn 追加模型可见 `UserMessage`。

提交后再通知 Executor。若 Executor 已丢失或拒绝 Steer：

- 已提交的 UserMessage 保留在历史；
- 不重新入队；
- 当前 Turn 以稳定错误失败；
- Session Core 再尝试调度下一项。

## 15. Thread 生命周期

### 15.1 标题

- 第一个纯文本用户输入可生成自动标题；
- 最多保留 50 个 Unicode 文本元素；
- 手工 Rename 后永久关闭该 Thread 的自动标题覆盖；
- 标题修改是普通 Journal 事实。

### 15.2 Pause 与 Resume

- Pause 遇到活动 Turn 返回 `session.threadBusy`；
- Pause 不隐式 Cancel；
- Resume 不创建假 Turn，只调度已经提交的队首输入。

### 15.3 Archive 与 Unarchive

Archive 固定顺序：

```text
追加 ThreadArchived 并 Flush
→ active Journal 移到 archived
→ 更新 SQLite 投影
→ 发布事件
```

Unarchive 对称执行：

```text
追加 ThreadUnarchived 并 Flush
→ archived Journal 移到 active
→ 更新 SQLite 投影
→ 发布事件
```

移动必须保持同一 Runtime Data Root 内的路径约束。任一步骤崩溃后，启动 Reconciler
根据 Journal 最后事实和实际目录完成移动、投影或事件恢复。

## 16. 永久删除

### 16.1 Prepare

只允许：

- Thread 为 Archived；
- 没有活动 Turn 或维护操作；
- Queue 为空；
- `expectedSequence` 匹配；
- 没有 Worktree 或未来外部资源绑定。

Prepare 使用密码学安全随机数生成 256-bit、一次性、2 分钟有效的 Token。Token
绑定 Workspace、Thread 和 Sequence，只存在内存中，不写日志、SQLite 或文件。

### 16.2 Delete

Token 校验和消费后按固定顺序执行：

```text
追加 ThreadDeletionRequested 并 Flush
→ Journal 移到 deleting
→ SQLite 标记 Deleting
→ 清理 Runtime Data Root 内由 Session Core 明确拥有的文件
→ 删除可重建 Session 投影
→ 删除 deleting Journal
→ 写入最小删除回执
```

每个删除目标在实际删除前必须重新进行：

- 绝对路径解析；
- Runtime Data Root 包含关系校验；
- Symlink、Junction 和 Reparse Point 逃逸校验；
- 所有权校验。

M2 不删除 Worktree。发现任何当前或未来 Worktree 绑定时必须拒绝删除。不得删除
`.opencowork/runtime` 之外的用户文件。

一旦 `ThreadDeletionRequested` Flush 成功，Token 不再需要；启动 Reconciler 必须
自动续跑删除。删除回执用于 7 天内重放原 Idempotency Key 的完成结果。

## 17. Fork 与 Rollback

### 17.1 稳定边界

Fork 和 Rollback 只接受：

- Thread 创建后的边界；
- Turn 终态后的 Sequence。

不得以 Streaming Item、Waiting 中间步骤或非终态 Turn 为目标。

### 17.2 Fork

1. 在源 Thread Gate 内捕获指定稳定 Sequence 的不可变快照；
2. 释放源 Gate；
3. 创建目标 Thread；
4. 目标 Sequence 1 写入 `ThreadForked`，Payload 包含源 Thread ID、源 Sequence
   和完整 `HistoryCheckpoint`。

目标 Thread：

- 状态为 Active；
- Queue 为空；
- 没有活动 Turn；
- 只复制模型可见历史和 Thread 配置；
- 不复制等待、Checkpoint、附件所有权或 Worktree；
- 不依赖源 Thread 后续存在。

源 Thread 可以是 Active、Paused 或 Archived，也可以存在更晚的活动 Turn，只要目标
Sequence 是稳定边界。

### 17.3 Rollback

- 只允许 Active 或 Paused、空闲、Queue 为空且无维护操作的 Thread；
- Archived Thread 必须先 Unarchive；
- 追加 `ThreadRolledBack` 和替换后的完整 `HistoryCheckpoint`；
- 旧事实继续保留用于审计；
- 投影和后续模型历史只显示 Rollback 后的有效历史；
- 不恢复旧 Queue、等待或 Checkpoint；
- 不自动创建 Turn；
- 返回 `externalSideEffectsReverted = false`。

## 18. 损坏、修复与隔离

### 18.1 可自动修复

仅以下尾部可以自动截断：

- 最后一个有效 LF 之后的半行；
- 未以 LF 结束的非 JSON 或不完整 JSON；
- 所有更早 Entry 均通过 Schema、ID、Sequence 和 Checksum 校验。

修复顺序：

```text
在 runtime/recovery 创建原文件备份和恢复意图元数据
→ 记录原始长度与 SHA-256
→ SetLength 到最后有效偏移
→ Flush
→ 追加 ThreadJournalRecovered
→ 重建投影
```

恢复意图元数据使进程在截断后、Recovery Entry 前崩溃时仍可续跑。

### 18.2 不自动修复

- 以 LF 结束但 Checksum 错误的 Entry；
- 中间非法行；
- Sequence 重复或缺口；
- 未知且无法 Upcast 的 Schema；
- Thread ID、Entry ID 或文件名不一致；
- 无法证明只影响尾部的任何损坏。

原文件保持不变并创建备份，该 Thread 的可用性进入 `RecoveryRequired`：

- GetThread 和列表可以显示诊断摘要；
- History 和所有修改被拒绝；
- 其他 Thread 继续工作；
- M2 不提供跳行、中段拼接或人工重写命令。

## 19. 查询投影

M2 只提供以下查询面：

| 查询 | 行为 |
| --- | --- |
| `GetThread` | 返回不可变聚合快照、当前 Sequence、活动 Turn、Queue 摘要和投影状态。 |
| `ListThreads` | 按 `updatedAt DESC, threadId DESC` 游标分页，单页最多 100。 |
| `ReadHistory` | 按 Journal Sequence 升序游标读取，不使用 Offset。 |
| `GetSessionStatistics` | 返回 Thread、Turn、Item 和状态计数。 |
| `SearchThreads` | 仅搜索 DisplayName 和 FirstUserMessage。 |

搜索投影：

```text
Unicode Form C
→ ToUpperInvariant
→ SQLite instr
```

M2 不使用 FTS5、区域性 Collation、模糊匹配或相关性排序。

降级行为：

- `GetThread` 可从不可变内存聚合返回，并标记
  `ProjectionState = Degraded`；
- `ReadHistory` 在投影缺失时直接从 Journal 读取并触发修复；
- `ListThreads`、`SearchThreads` 和 `GetSessionStatistics` 在全局投影不可用时返回
  `session.projectionUnavailable`。

## 20. 错误契约

```text
SessionError
├─ Code
├─ Message
└─ IsRetryable
```

- `Code` 是稳定契约；
- `Message` 只供人阅读，不参与程序分支；
- 不提供通用 `Dictionary<string, object>` 详情袋；
- 必要的结构化数据放入对应结果类型；
- 公开结果不包含堆栈、绝对路径、SQLite 文本、Journal 原始行、Secret 或内部异常；
- 完整异常和安全脱敏后的路径只进入结构化日志。

M2 初始错误码：

| Code | 含义 |
| --- | --- |
| `session.notFound` | Thread 或目标对象不存在。 |
| `session.invalidState` | 当前状态不允许该操作。 |
| `session.threadBusy` | 活动 Turn 或维护操作阻止请求。 |
| `session.sequenceConflict` | `expectedSequence` 不匹配。 |
| `session.idempotencyConflict` | Workspace 全局 Key 被不同请求复用。 |
| `session.queueFull` | Queue 达到 128 项。 |
| `session.queueItemNotFound` | Queue Item 已不存在或已调度。 |
| `session.interactionAlreadyResolved` | Resolution 与已提交结果冲突。 |
| `session.subscriberLagged` | 订阅者消费速度不足。 |
| `session.projectionUnavailable` | SQLite Session 投影不可用。 |
| `session.recoveryRequired` | Thread Journal 需要人工恢复。 |
| `session.invalidCursor` | 查询或订阅游标非法。 |
| `session.deleteTokenInvalid` | 删除 Token 不匹配或已消费。 |
| `session.deleteTokenExpired` | 删除 Token 已过期。 |
| `session.unsupportedHistoryMode` | M2 收到非 Server HistoryMode。 |
| `journal.corrupt` | Journal 不满足完整性约束。 |
| `journal.entryTooLarge` | Entry 超过 1 MiB。 |
| `journal.unsupportedSchema` | Entry Schema 无可用 Upcaster。 |
| `runtime.interrupted` | 非可续接执行被进程中断。 |
| `runtime.continuationMissing` | 等待状态缺少有效 Checkpoint。 |
| `runtime.shuttingDown` | Runtime 已停止接收新工作。 |
| `runtime.executorUnavailable` | Executor 丢失或拒绝已提交的执行意图。 |

新增稳定错误码必须先更新本规格或后续公共契约规格，不能把内部异常文本临时当错误码。

## 21. SessionModule 生命周期

M2 新增独立 `session` 模块：

- 模块声明进入 `OpenCoWork.App` 组合清单；
- 实现和依赖注入位于 `OpenCoWork.Core`；
- `cli` 和未来宿主依赖 `session`；
- `session` 不具备主宿主资格；
- 不新增生产程序集。

启动顺序：

```text
State Schema v2 迁移
→ 创建 Session Runtime 目录
→ 扫描 active / archived / deleting
→ Archive / Delete Reconciler
→ Journal 校验与内存聚合重建
→ SQLite 投影重建或追平
→ 恢复等待或写入 interrupted 终态
→ Session Ready
→ CLI 开放业务入口
```

失败边界：

- SQLite Schema 迁移失败：模块启动失败，WorkspaceRuntime 进入 Faulted；
- 全局投影追平失败：SessionModule 启动完成但报告 Degraded，不开放新工作；
- 单 Thread Journal 损坏：仅该 Thread 进入 RecoveryRequired；
- M2 调整 WorkspaceRuntime，使模块可在 Starting 阶段报告健康状态，并在全部模块
  启动后根据模块健康汇总为 Running 或 Degraded。

停止顺序：

1. 停止接收新工作；
2. 取消活动 Executor；
3. 关闭等待和订阅；
4. 强制提交已缓冲 Delta；
5. 将无法续接的 Turn 确定终态；
6. 停止 Projector；
7. 释放文件和数据库资源。

## 22. 容量与配置

只开放三个运行时配置：

| 配置 | 默认值 |
| --- | ---: |
| `session.eventBufferCapacity` | 256 |
| `session.streamFlushInterval` | 50 ms |
| `session.streamFlushBytes` | 8192 |

固定安全边界：

| 对象 | 边界 |
| --- | ---: |
| Journal Entry | 1 MiB UTF-8，不含 LF |
| 单次文本输入 | 256 KiB UTF-8 |
| Execution Checkpoint | 256 KiB UTF-8 |
| Queue | 128 项 / Thread |
| 查询页 | 100 项 |
| 自动标题 | 50 个 Unicode 文本元素 |
| 删除 Token | 2 分钟 |
| 删除回执 | 7 天 |

超限请求在 Journal 写入前拒绝，不占用 Sequence。

## 23. 实施 Outcomes

M2 只建立一份整体实施计划，按以下依赖顺序推进：

1. 领域契约、状态机与 SQLite Schema v2；
2. `ThreadJournal` 写入、回放与损坏识别；
3. 投影重建、追平、降级和恢复；
4. `ISessionService`、幂等、并发、查询和事件订阅；
5. Executor、流式 Item、等待、取消和重启续接；
6. Queue、重排和 Steer；
7. Fork、Rollback、Archive、Delete 和 Reconciler；
8. 主机集成、故障注入、Windows 验证、macOS 台账和文档收口。

Outcome 可以形成阶段提交，但任一 Outcome 完成不代表 M2 已验收。

## 24. 验证策略

### 24.1 测试基础

- 继续使用现有 xUnit v3；
- 固定场景使用 `[Fact]`，参数矩阵使用 `[Theory]`；
- 不新增测试框架、断言框架或 Mock 框架；
- 单元测试覆盖纯状态机、编码、Checksum、幂等和 Cursor；
- 集成测试使用真实 SQLite 和真实临时文件系统；
- 并发测试使用 `Barrier`、`TaskCompletionSource` 等确定性同步原语；
- 禁止使用 `Thread.Sleep` 或概率性竞态；
- 时间相关测试使用 .NET `TimeProvider`；
- 进程中断使用可控子进程和故障注入点，不把普通 Cancellation 当断电证据。

### 24.2 Windows 真机

M2 关闭前必须在当前 Windows 真机验证：

- Journal 持久化 Flush、半行恢复和进程中断；
- SQLite Schema v2、投影删除与完整重建；
- 同 Thread 串行和不同 Thread 并行；
- Archive/Unarchive 的目录移动和崩溃续跑；
- Delete Token、路径包含、Junction/Reparse Point/Symlink 逃逸防护；
- 等待 Resolution、取消竞态和重启续接；
- Release build、完整 test 和 `win-x64` 运行证据。

需要原生 Windows Symlink 时使用临时专项提权测试，不要求 OpenCoWork 日常以管理员
身份运行，也不把开发者模式作为产品前置条件。

### 24.3 macOS ARM64

当前执行 `osx-arm64` 交叉构建或发布，但不得称为 macOS 真机证据。需要 M4 Mac mini
验证的 Journal Flush、原子移动、文件锁、并发、崩溃恢复和 Symlink 安全场景统一
记录在仓库 `AGENTS.md`。这些 Pending 项不阻塞当前 M2 的 Windows 阶段关闭，但必须
在 M11 / OpenCoWork 1.0 正式发布前清零。

### 24.4 验收映射

| Acceptance | M2 证据 |
| --- | --- |
| `M2-ACC-001` | Thread、Turn、Item 状态转换和非法转换测试。 |
| `M2-ACC-002` | Sequence、Checksum、Flush 前后和进程终止故障注入。 |
| `M2-ACC-003` | 删除 Session 投影后的完整重建与规范化快照对比。 |
| `M2-ACC-004` | Gate 并发、Sequence 冲突、投影降级与追平。 |
| `M2-ACC-005` | Queue 追加、删除、重排、Steer 和重启回放。 |
| `M2-ACC-006` | Waiting、首次 Resolution、Cancel 竞态和 Checkpoint 恢复。 |
| `M2-ACC-007` | Archive/Unarchive 各阶段崩溃与 Reconciler。 |
| `M2-ACC-008` | Delete Token、路径安全、续跑和外部文件保护。 |
| `M2-ACC-009` | 源删除后的 Fork 回放和 Rollback 副作用声明。 |
| `M2-ACC-010` | 尾部修复 Corpus、中段损坏隔离和备份。 |

M2 只有在 `M2-ACC-001` 至 `M2-ACC-010` 均有自动化证据、Windows 真机验证通过、
macOS 待验证项已登记后才能关闭。

## 25. 已确认决策

- `ThreadJournal` 是唯一事实源，SQLite 是可重建查询投影；
- 一个 Entry 是一个领域事实、一个 Sequence 和一个核心事件；
- `ISessionService` 是唯一公共修改门面；
- `ISessionExecutor` 只产生执行意图；
- 修改请求使用 Workspace 全局幂等和 `expectedSequence`；
- 订阅支持 SnapshotThenLive 与 ResumeAfterSequence；
- Journal 使用固定 JSONL、SHA-256、LF 和每次提交持久化 Flush；
- 投影失败返回 `CommittedPendingProjection`，不伪装回滚；
- 等待状态通过版本化、带校验的 Checkpoint 恢复；
- Queue Input 独立于 Turn，Steer 不创建新 Turn；
- Pause、Archive 和 Delete 不隐式清队列或取消活动工作；
- Delete 使用内存短期 Token 和可续跑删除事实；
- Fork 自包含，Rollback 只追加且不声称撤销外部副作用；
- 只自动修复可证明安全的尾部损坏；
- SQLite Schema v2 保持最小 Session 表面并启用 `synchronous = FULL`；
- 流式 Item 按 50 ms 或 8 KiB 合并提交；
- 锁顺序固定为 Idempotency Key、Thread、SQLite；
- `session` 是独立非宿主模块；
- 公共 ID 使用 UUIDv7 `Guid`，结果区分三种提交状态；
- M2 Item 类型保持最小文本与交互集合；
- 查询面只覆盖 Get、List、History、Statistics 和最小 Search；
- 错误使用稳定 Code，不泄露内部实现；
- 只有三个运行时调节配置，其余是固定安全边界；
- M2 以 xUnit、真实 SQLite/文件系统、确定性故障注入和 Windows 真机证据收口；
- M2 是一个整体任务，只形成一份设计、一份计划和一份完成归档。

当前没有影响 M2 实施的开放设计决策。实施中若发现必须改变公共契约、权威数据边界、
提交顺序、安全顺序或验收语义的新事实，必须先更新本规格并确认，不能以代码先行
代替设计决策。
