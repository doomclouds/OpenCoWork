# OpenCoWork M0 Contract Freeze 设计规格

## 文档状态

- 状态：已冻结
- 日期：2026-07-25
- 所属里程碑：OpenCoWork Runtime 1.0 / M0
- 目标框架：.NET 10
- 正式平台：`win-x64`、`osx-arm64`
- 原始能力参考：
  [DotCraft 核心运行时复刻规范](../../../DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md)
- 路线规格：
  [OpenCoWork Runtime 1.0 路线规格](2026-07-25-open-cowork-runtime-1-0-roadmap.md)
- 能力台账：
  [OpenCoWork M0 能力台账](2026-07-25-open-cowork-m0-capability-ledger.md)
- 验收目录：
  [OpenCoWork M0-M10 验收目录](2026-07-25-open-cowork-m0-acceptance-catalog.md)

## 1. 目的与约束级别

本文冻结 OpenCoWork 1.0 的品牌、领域术语、程序集依赖、配置与路径、
OpenCoWork Wire、状态机和存储一致性契约。M1-M10 的独立规格可以细化实现，
但不得无说明地改变本文的外部行为、安全顺序或权威数据边界。

约束级别：

- **必须**：后续实现和验收不可违反；
- **应当**：默认实现方式，变更时必须在对应 Slice 规格中给出等价性证明；
- **可以**：不影响冻结契约的实现自由。

M0 不创建 Solution、项目骨架或业务代码。其交付物就是本文、能力台账和验收
目录。

## 2. 品牌与领域语言

### 2.1 品牌边界

| 对象 | 冻结名称 |
| --- | --- |
| 产品与根命名空间 | `OpenCoWork` |
| CLI 可执行文件 | `opencowork` |
| 工作区数据目录 | `.opencowork` |
| Windows 用户目录 | `%USERPROFILE%\.opencowork` |
| macOS 用户目录 | `~/.opencowork` |
| 插件清单 | `.opencowork-plugin/plugin.json` |
| 协议名称 | `OpenCoWork Wire` |

OpenCoWork 不兼容 DotCraft 的 `.craft` 目录、程序集、命名空间、二进制或私有
实现。原规范只用于核对能力边界、状态语义、安全顺序和验收场景。

### 2.2 命名原则

命名以 OpenCoWork 的协作语境和职责准确性为准，不做机械式全量替换。以下
名称语义准确，必须保留：

- `WorkspaceRuntime`
- `SessionThread`
- `SessionTurn`
- `SessionItem`
- `AgentSession`
- `SessionService`
- `AgentFactory`
- `EffectiveToolSnapshot`
- `ModuleRegistry`
- `Teams`
- `Mission`

冻结的重命名：

| 参考名称 | OpenCoWork 名称 | 契约理由 |
| --- | --- | --- |
| `ToolDispatcher` | `ToolInvocationPipeline` | 职责是完整的安全调用管线，而非单纯分发。 |
| `Rollout` | `ThreadJournal` | 它是 Thread 的权威追加日志，承担提交、回放和恢复。 |
| `CraftPath` 概念 | `Workspace Data Root` | 表达数据根边界，不携带旧品牌。 |
| `CraftPath` 路径服务 | `OpenCoWorkPaths` | 集中负责用户、工作区和运行时路径解析。 |
| `Dreams` | `Workspace Insights` | 表达后台分析与改进建议，不拟人化持久化域。 |
| `DreamsService` | `WorkspaceInsightService` | 服务名与工作区洞察职责一致。 |

Workspace Insights 使用 `InsightRun` 表示一次分析运行，使用
`ImprovementProposal` 表示可审阅的改进建议。

### 2.3 协议领域术语

`OpenCoWork Wire` 是协议总称；会话域继续使用 `thread`、`turn`、`item`。
协议方法不加 `opencowork/` 冗余前缀。插件扩展使用
`extension/<publisher>.<pluginId>/<method>`，避免与内置域冲突。

## 3. 程序集与依赖方向

### 3.1 生产程序集

```text
src/
├── OpenCoWork.Abstractions
├── OpenCoWork.Core
├── OpenCoWork.Protocol
├── OpenCoWork.Automations
├── OpenCoWork.Teams
├── OpenCoWork.App
└── OpenCoWork.Generators
```

职责与依赖：

| 程序集 | 职责 | 允许依赖 |
| --- | --- | --- |
| `OpenCoWork.Abstractions` | 稳定跨程序集与插件契约 | .NET 10 BCL 为主，不依赖其他生产程序集 |
| `OpenCoWork.Core` | Workspace、Session、Agent、Tool、Context、State 实现 | `Abstractions` |
| `OpenCoWork.Protocol` | JSON-RPC、AppServer、ACP、DTO、Wire 扩展点 | `Abstractions` |
| `OpenCoWork.Automations` | 定义、调度、Run 编排 | `Abstractions`、Protocol 扩展点 |
| `OpenCoWork.Teams` | SubAgent、Team、Mission、Mailbox 编排 | `Abstractions`、Protocol 扩展点 |
| `OpenCoWork.App` | Composition Root 和 `opencowork` 可执行入口 | Core、Protocol、Automations、Teams |
| `OpenCoWork.Generators` | 模块、配置和契约目录的编译期生成 | `netstandard2.0` Analyzer-only |

硬约束：

- `OpenCoWork.Protocol` 不依赖 `OpenCoWork.Core`；
- Protocol Handler 只通过 `OpenCoWork.Abstractions` 中的
  `ISessionService` 等契约调用核心服务；
- `OpenCoWork.Automations` 与 `OpenCoWork.Teams` 不互相引用；
- `OpenCoWork.Generators` 不形成运行时依赖；
- `OpenCoWork.Abstractions` 不包含数据库 Provider、文件系统实现或宿主实现；
- 内置和插件扩展不得通过依赖方向绕过 Session Core 或
  `ToolInvocationPipeline`。

### 3.2 测试项目

```text
tests/
├── OpenCoWork.Core.Tests
├── OpenCoWork.Protocol.Tests
├── OpenCoWork.Generators.Tests
├── OpenCoWork.ArchitectureTests
├── OpenCoWork.IntegrationTests
└── OpenCoWork.Protocol.TestClient
```

`OpenCoWork.ArchitectureTests` 必须验证程序集引用方向和禁止依赖。
`OpenCoWork.Generators.Tests` 必须保存生成器快照并覆盖重复注册诊断。

## 4. 配置、路径与信任

### 4.1 双平面工作区目录

工作区采用“可版本控制定义”和“本机运行状态”双平面：

```text
.opencowork/
├── config.jsonc
├── config.local.jsonc
├── plugins.lock.json
├── skills/
├── automations/
│   └── definitions/
└── runtime/
    ├── state.db
    ├── threads/
    │   ├── active/
    │   └── archived/
    ├── attachments/
    ├── memory/
    ├── insights/
    ├── teams/
    │   └── missions/
    ├── logs/
    ├── worktrees/
    └── external-channel-media/
```

默认版本控制规则：

- 跟踪：`config.jsonc`、`plugins.lock.json`、`skills/`、
  `automations/definitions/`；
- 忽略：`config.local.jsonc`、`runtime/`、SQLite WAL/SHM、临时文件和锁文件；
- `opencowork init` 必须生成 `.opencowork/.gitignore`；
- 插件源仓库使用 `.opencowork-plugin/plugin.json`，不得混入工作区运行目录。

### 4.2 配置覆盖与合并

配置格式为 JSONC。优先级从低到高：

```text
内置默认值
< ~/.opencowork/config.jsonc
< .opencowork/config.jsonc
< .opencowork/config.local.jsonc
< --config 指定的覆盖文件
< OPENCOWORK__* 环境变量
< CLI 显式参数与 --set
```

合并规则：

- 对象递归合并；
- 具名集合按键合并；
- 数组整体替换；
- `null` 只允许赋给可空属性；
- 类型错误、范围错误或缺少必填项时，运行时必须在启动服务前失败；
- 未知字段默认产生警告，`opencowork doctor --strict-config` 必须失败；
- `WorkspaceRuntime` 启动时冻结不可变配置快照，1.0 不做隐式热重载。

顶层配置按模块职责分组：

```text
runtime, models, sessions, context, agents, tools, extensions,
security, insights, automations, teams, protocol, operations, cli
```

配置节使用 `[ConfigSection]` 声明，由生成器产生聚合模型和 Schema；重复节名必须
在构建时失败。插件私有配置位于
`extensions.plugins.config.<pluginId>`。JSON 属性与枚举值使用 lowerCamel；
持续时间使用 `"30s"`、`"10m"` 等带单位字符串。

### 4.3 工作区发现与路径安全

工作区发现顺序：

```text
--workspace
> 从当前目录向上找到最近的 .opencowork
> Git 根目录
> 当前工作目录
```

`OpenCoWorkPaths` 必须：

- 规范化并返回绝对路径；
- 将相对路径解析到声明它的配置文件所在目录；
- 拒绝越出允许根目录的 `..`、符号链接和重解析点逃逸；
- 隔离 Windows 与 macOS 的路径、权限和大小写差异；
- 不依赖进程当前目录来定位 Hub 或后台工作区。

### 4.4 Secret 与工作区信任

Secret 不得以明文进入被跟踪配置、日志、Journal 或事件。允许通过环境变量引用，
或存入 Windows Credential Manager / macOS Keychain。

有效权限取以下集合的交集：

```text
用户策略 ∩ 工作区请求 ∩ 当前运行模式 ∩ Thread/Automation 权限
```

工作区配置不得提升用户策略。工作区声明的原生插件、Hook、MCP、LSP、外部渠道
命令和 Shell 扩展首次均为 `PendingTrust`。

信任决定存放在 `~/.opencowork/trust/decisions.json`，并绑定：

- 规范化工作区路径；
- 能力来源；
- 版本；
- 内容摘要；
- 授权范围。

任何绑定字段变化都必须重新授信。

## 5. OpenCoWork Wire 契约

### 5.1 基础协议与握手

OpenCoWork Wire 使用 JSON-RPC 2.0。每个连接必须先调用 `initialize`，随后发送
`initialized`，未完成握手不得调用业务方法。

`initialize` 请求至少包含：

- 客户端名称与版本；
- 支持的 Wire 版本；
- 客户端能力；
- 工作区请求；
- 认证信息或认证方式。

响应至少包含：

- 服务端名称与版本；
- 协商后的 Wire 版本；
- 服务端能力；
- 限制与配额；
- 规范化工作区信息。

目标 `wireVersion` 为 `"1.0"`。M10 正式发布前协议允许调整；1.0 发布后公共协议
按 SemVer 管理。

### 5.2 命名与方法元数据

- 方法名使用 `domain/action`；
- 事件名使用已发生语态；
- 属性与枚举值使用 lowerCamel；
- ID 使用小写 UUIDv7；
- 时间戳使用 RFC 3339 UTC；
- 分页使用不透明 Cursor。

每个 Wire 方法描述符必须包含：

`method`、`direction`、`owner`、`since`、`request`、`response`、
`authority`、`mutates`、`idempotency`。

生成器必须输出可测试的 Wire Catalog。内置方法重复在构建或启动时失败；插件
重复贡献必须隔离该贡献，不能按加载顺序覆盖。

### 5.3 M5 核心方法

```text
thread/create          thread/get             thread/list
thread/rename          thread/pause           thread/resume
thread/archive         thread/unarchive       thread/delete
thread/fork            thread/rollback        thread/subscribe
thread/unsubscribe

turn/start             turn/enqueue           turn/queue/remove
turn/queue/reorder     turn/steer             turn/cancel

item/approval/resolve  item/input/resolve
```

核心事件：

```text
thread/created         thread/updated          thread/deleted
thread/statusChanged   thread/queueUpdated
turn/started           turn/completed          turn/failed
turn/cancelled
item/started           item/delta              item/completed
item/approval/requested   item/approval/resolved
item/input/requested      item/input/resolved
system/event
```

流式内容统一通过带类型 Payload 的 `item/delta` 传递，不为每种 Item 创建独立
Delta 方法。

### 5.4 事件、订阅与并发

事件信封至少包含：

`eventId`、`threadId`、`turnId`、`itemId`、`sequence`、`timestamp`、
`payload`。

`sequence` 等于 ThreadJournal Sequence。订阅必须原子返回当前快照与
`currentSequence`，随后只推送更大 Sequence 的事件。客户端通过
`afterSequence` 续订。

交付语义：

- 重连为至少一次交付；
- 单 Thread 内按 Sequence 有序；
- 客户端用 `eventId + sequence` 去重；
- 慢客户端使用有界队列，溢出后断开并要求通过 Cursor 重连；
- 断连释放连接级订阅，但不取消已提交的 Turn。

修改状态的请求必须提供独立于 JSON-RPC Request ID 的 `idempotencyKey`。
并发修改必须支持 `expectedSequence`。Approval 和 UserInput 的第一次有效
Resolution 生效，重复请求返回同一结果。

`$/cancelRequest` 只取消 RPC 等待；`turn/cancel` 才是持久业务取消。客户端不得
用 Notification 发起有副作用操作。

### 5.5 Transport、错误与扩展域

stdio：

- UTF-8 JSONL；
- stdout 只输出协议对象；
- 日志只写 stderr。

WebSocket：

- 一个 UTF-8 Text Frame 对应一个 JSON 对象；
- Token 通过 Header 或 initialize 传递，不放 Query String；
- 使用有界发送队列和重连 Cursor。

稳定错误响应包含 JSON-RPC 数字错误码，以及：

`data.errorCode`、`data.retryable`、`data.correlationId`。
不得暴露堆栈、Secret 或内部路径。

按 Slice 开放的域：

| Slice | Wire 域 |
| --- | --- |
| M5 | `initialize`、`workspace`、`thread`、`turn`、`item`、`system` |
| M6 | `provider`、`model`、`auth`、`tool`、`skill`、`plugin`、`marketplace`、`mcp`、`lsp`、`hook`、`sourceControl`、`terminal` |
| M7 | `agent`、`subagent`、`team`、`mission`、`mailbox`、`artifact`、`worktree` |
| M8 | `automation`、`schedule`、`automationRun` |
| M9 | `gateway`、`channel`、`hub`、`usage`、`trace`、`heartbeat`、`insight` |

原 `dreams` 语义进入 `insight`，原 `cron` 进入 `schedule`，原
`externalChannel` 进入 `channel`，MCP App 与 Server Status 归入 `mcp`。
`ext` 不是公共域；Dashboard 只提供查询，不承诺 Wire UI。

ACP Bridge 只做协议转换，不持有独立状态机。

## 6. 权威状态与存储

### 6.1 权威源矩阵

| 数据 | 权威源 | 补充存储 |
| --- | --- | --- |
| Thread、Turn、Item、模型历史、Rollback、Compaction | `ThreadJournal` | SQLite 可重建查询投影 |
| Session 列表、搜索、统计 | SQLite 投影 | 从 Journal 重建 |
| Mission、Task、Member、Mailbox | SQLite | Thread 内容仍在各自 Journal |
| Automation Schedule、Run、Lease | SQLite | 定义文件冻结快照 |
| Gateway 映射、去重、Outbox、Dead Letter | SQLite | 媒体使用文件存储 |
| 配置、Skill、Automation 定义、Plugin Lock | 文件 | 生成的有效快照可缓存 |
| Attachment、Artifact、Scratchpad | 文件内容 | SQLite 保存元数据与摘要 |
| Secret | OS Credential Store | 配置只保存引用 |

### 6.2 ThreadJournal 格式与提交点

每个 Entry 至少包含：

`schemaVersion`、`threadId`、`sequence`、`entryId`、`timestamp`、
`entryType`、`idempotencyKey`、`payload`、`checksum`。

约束：

- `entryId` 使用 UUIDv7；
- Sequence 从 1 开始且严格递增；
- 一个 Entry 是不可分割的逻辑提交；
- 大型内容外置为 Attachment，并在 Entry 中保存引用与摘要；
- Rollback 通过追加补偿事实表达，不改写历史。

固定提交顺序：

```text
ThreadWriteGate
→ 校验状态、权限和 expectedSequence
→ 构造不可变 Entry
→ 追加并 Flush ThreadJournal
→ 更新内存聚合
→ SQLite 事务更新投影与 lastAppliedSequence
→ 发布 SessionEvent
→ 释放 ThreadWriteGate
```

Journal Flush 是提交点。投影失败不回滚已提交事实；运行时进入
`ProjectionDegraded`，暂停新的 Turn、Mission 和 Automation，允许在途操作进入
终态或取消。Projector 必须从 `lastAppliedSequence` 重放，恢复后再开放写入。
外部事件只在投影完成后发布；重连期间可直接从 Journal 恢复事件。

### 6.3 损坏与恢复

- 尾部不完整 Entry：允许截断到最后一个校验通过且以换行结束的 Entry，并记录
  诊断；
- 中间校验失败、Sequence 缺口或非法 Schema：该 Thread 进入
  `RecoveryRequired`，不得静默跳过；
- 单 Thread 损坏不得阻止其他 Thread 加载；
- 修复前必须创建备份，自动修复只处理可证明安全的尾部损坏。

崩溃恢复：

- 未终态 Turn 变为 `Failed`，错误码 `runtime.interrupted`；
- 只有具有完整持久化 Continuation 的等待状态可以恢复；
- 缺失 Continuation 使用 `runtime.continuationMissing`；
- Lease 由 Reconciler 按过期规则接管；
- 外部副作用结果不明时进入 `NeedsAttention` 或 `Blocked`，不得自动重试。

### 6.4 SQLite 契约

启动 PRAGMA：

```text
journal_mode = WAL
synchronous = FULL
foreign_keys = ON
secure_delete = ON
busy_timeout = 显式配置值
```

所有写入经 `StateWriteCoordinator`，需要抢占写锁的事务使用
`BEGIN IMMEDIATE`。时间戳以 Unix 毫秒存储；枚举以小写文本存储。
`state_info` 保存 Schema、应用版本和迁移状态。

迁移必须幂等，并在迁移前执行 WAL Checkpoint 与 SQLite Backup API 备份。
迁移状态显式记录 Started、Completed、Failed；失败时恢复旧数据库。

ThreadJournal 通过 Upcaster 读取旧 Schema，正常启动不得原地重写历史。需要物理
升级时，写入新目录、完整校验后原子切换，并保留备份。M10 至少覆盖两个旧版本
Schema 的数据库迁移和 Journal 回放。

## 7. 冻结状态机

### 7.1 核心状态

| 聚合 | 状态 |
| --- | --- |
| WorkspaceRuntime | `Stopped`、`Starting`、`Running`、`Degraded`、`Stopping`、`Faulted` |
| Thread | `Active`、`Paused`、`Archived` |
| Turn | `Running`、`WaitingApproval`、`WaitingInput`、`Completed`、`Failed`、`Cancelled` |
| Item | `Started`、`Streaming`、`Completed`、`Failed`、`Cancelled` |
| Goal | `Active`、`Paused`、`Blocked`、`UsageLimited`、`BudgetLimited`、`Completed` |
| AgentMode | `Agent`、`Plan` |

排队输入使用独立 `QueuedTurnInput`，不得伪造一个“Queued Turn”。

### 7.2 工具与编排状态

```text
ToolInvocation:
Started
→ WaitingApproval
→ Running
→ Completed | Rejected | Failed | Cancelled | TimedOut | OutcomeUnknown
```

非幂等外部操作结果不明时不得自动重试。交互 Turn 以
`tool.outcomeUnknown` 失败；Automation 进入 `NeedsAttention`。

| 聚合 | 状态 |
| --- | --- |
| Mission | `Planning`、`Active`、`AwaitingLeaderReview`、`Completed`、`Failed`、`Cancelled` |
| MissionTask | `Pending`、`WaitingDependencies`、`Ready`、`Running`、`Blocked`、`Review`、`Completed`、`Failed`、`Cancelled` |
| Mailbox | `Pending`、`Delivered`、`Acknowledged`、`DeadLettered` |
| AutomationRun | `Pending`、`Running`、`NeedsAttention`、`Completed`、`Failed`、`Cancelled`、`TimedOut` |
| Outbox | `Pending`、`Sending`、`Sent`、`Failed`、`DeadLettered` |

所有成功终态统一使用 `Completed`，不得在不同子系统混用 `Succeeded`。

## 8. Archive、Delete、Rollback 与文件提交

### 8.1 Archive

固定顺序：

```text
追加 ThreadArchived 并 Flush
→ 原子移动 active Journal 到 archived
→ 更新 SQLite 投影
→ 发布事件
```

启动 Reconciler 以 Journal 状态为准修复移动中断。

### 8.2 永久删除

永久删除只允许 Archived、无活动操作且 `expectedSequence` 匹配的 Thread。
客户端先调用 `thread/delete/prepare` 获取短期 Token，再执行删除：

```text
追加 ThreadDeletionRequested
→ Journal 移入 runtime/threads/deleting
→ SQLite 标记 Deleting
→ 清理安全范围内的运行时文件和 Worktree
→ 删除查询投影
→ 最后删除 Journal
```

Reconciler 必须继续未完成删除。任何失败都保留 `Deleting` 状态和可诊断证据。
不得删除 `.opencowork/runtime` 之外的用户文件，不得删除 Dirty Worktree。

### 8.3 Rollback 与 Fork

Rollback 只追加恢复检查点，不擦除历史，也不声称撤销已经发生的外部副作用。
Wire 响应必须明确 `externalSideEffectsReverted: false`。

普通 Fork 创建独立 Thread：写入 `ThreadForked`、源 Thread ID、源 Sequence 和
完整 `HistoryCheckpoint`。Fork 不依赖源 Thread 后续存在。Worktree Fork 是独立
能力，不与会话 Fork 隐式绑定。

### 8.4 文件型数据

文件提交顺序：

```text
校验目标包含关系与符号链接
→ 同目录临时文件
→ 写入并 Flush
→ 计算摘要
→ 原子 Rename
→ SQLite 写入相对路径、摘要和元数据
```

可以按摘要去重并维护引用计数；必须有孤儿文件清理器，且清理前重新验证根目录
包含关系。

## 9. 能力、验收与变更控制

能力去向以
[能力台账](2026-07-25-open-cowork-m0-capability-ledger.md)
为准，只允许：

- `PreserveSemantics`
- `Redesign`
- `Deferred`
- `Removed`

不得保留 `TBD`。M1-M10 的完成证据以
[验收目录](2026-07-25-open-cowork-m0-acceptance-catalog.md)
中的稳定 Acceptance ID 为索引；ID 永不重排，废弃项使用 `Superseded` 并指向
替代 ID。

以下变更必须先修订 M0 契约并记录影响：

- 增删生产程序集或反转依赖方向；
- 改变权威数据源或 Journal 提交点；
- 改变工具安全顺序或信任交集；
- 改变公共 Wire 命名、幂等或订阅语义；
- 改变正式平台或兼容承诺；
- 将 Deferred/Removed 能力重新纳入 1.0。

普通内部类型、私有方法、表索引和可替换实现不属于 M0 变更，只要没有改变上述
可观察契约。

## 10. M0 完成结论

- 品牌与关键领域命名已冻结；
- 七个生产程序集与依赖方向已冻结；
- JSONC 配置、双平面路径和信任模型已冻结；
- OpenCoWork Wire 的握手、核心域、事件与幂等语义已冻结；
- Journal、SQLite、文件和 Secret 的权威边界与故障恢复已冻结；
- 状态机、Archive/Delete/Rollback/Fork 语义已冻结；
- 原始能力均已进入能力台账且无 `TBD`；
- M0-M10 均已有稳定验收编号。

M1 可以直接引用本文开始 Runtime Foundation 的独立规格与实施计划。
