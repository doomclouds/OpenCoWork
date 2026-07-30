# OpenCoWork M0 能力台账

## 文档状态

- 状态：已冻结
- 日期：2026-07-25
- 修订：2026-07-28，按 M5 Desktop-first 方案澄清 Wire 与 ACP 边界
- 修订：2026-07-29，复核 M6 实现映射；能力结论与契约边界无变更
- 修订：2026-07-30，复核 M7 实现映射；CAP-055..060 契约不变，
  双平台验收仍等待 `win-x64`
- 修订：2026-07-30，复核 M8 实现映射；CAP-061..066 契约不变，
  双平台验收仍等待 `win-x64`
- 所属里程碑：OpenCoWork Runtime 1.0 / M0
- 契约规格：
  [OpenCoWork M0 Contract Freeze](2026-07-25-open-cowork-m0-contract-freeze-design.md)
- 验收目录：
  [OpenCoWork M0-M10 验收目录](2026-07-25-open-cowork-m0-acceptance-catalog.md)

## 1. 使用规则

本台账按用户可观察能力和关键工程契约组织，不追求原规范类型、方法或配置项的
数字一致。每项能力只能使用以下结论：

- `PreserveSemantics`：保留原始行为语义，以 OpenCoWork 契约实现；
- `Redesign`：保留业务目的，但重新设计命名、边界或存储；
- `Deferred`：明确推迟到 OpenCoWork 1.x，1.0 不承诺；
- `Removed`：明确不实现，且不构成 1.0 缺口。

不存在 `TBD`。Acceptance ID 是关闭能力的唯一证据索引；测试文件存在本身不等于
验收通过。

## 2. 启动、模块与生成器

| CapabilityId | Capability | SourceEvidence | Decision | OpenCoWorkContract | OwnerMilestone | AcceptanceIds | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| CAP-001 | 统一应用入口与 Composition Root | 原规范的 Host/应用入口能力 | Redesign | `OpenCoWork.App` 组合所有模块并产出 `opencowork`；业务程序集不得反向引用 App。 | M1 | M0-ACC-002, M1-ACC-001 | 七程序集基线。 |
| CAP-002 | 程序集与二进制兼容 | 原规范程序集边界 | Removed | 不兼容 DotCraft 程序集、命名空间或二进制；只复用可验证的行为语义。 | M0 | M0-ACC-001, M0-ACC-008 | 非缺口。 |
| CAP-003 | ModuleRegistry 与确定性模块顺序 | 原规范模块注册与启动顺序 | PreserveSemantics | 模块显式注册、依赖拓扑排序、重复标识失败、逆序停止。 | M1 | M1-ACC-002 | 禁止靠目录枚举顺序。 |
| CAP-004 | HostBuilder 与主宿主选择 | 原规范多入口宿主能力 | Redesign | CLI/AppServer/Gateway 共享唯一 Composition Root；一次进程只选择一个主宿主。 | M1 | M1-ACC-003 | 后台服务挂靠 WorkspaceRuntime。 |
| CAP-005 | 模块、配置和 Wire Catalog 源生成 | 原规范生成器能力 | Redesign | `OpenCoWork.Generators` 为 Analyzer-only，生成注册、Schema 与 Wire Catalog，并产生可定位诊断。 | M1 | M1-ACC-004 | `netstandard2.0`。 |
| CAP-006 | 类型、方法和配置数量一比一 | 原规范的数量统计 | Removed | 不以数字一致衡量完成度，以能力台账和验收 ID 衡量。 | M0 | M0-ACC-006, M0-ACC-008 | 禁止“2320 类型/214 方法”式伪验收。 |

## 3. 配置、路径与平台

| CapabilityId | Capability | SourceEvidence | Decision | OpenCoWorkContract | OwnerMilestone | AcceptanceIds | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| CAP-007 | `.craft` 工作区兼容 | 原规范持久化目录 | Removed | OpenCoWork 只使用 `.opencowork`，不读取、不迁移 `.craft`。 | M0 | M0-ACC-001, M0-ACC-008 | 品牌硬边界。 |
| CAP-008 | JSONC 分层配置与确定性覆盖 | 原规范配置聚合能力 | Redesign | 默认、用户、工作区、本机、覆盖文件、环境变量、CLI 按冻结优先级合并。 | M1 | M0-ACC-003, M1-ACC-005 | 数组替换、对象递归。 |
| CAP-009 | 双平面工作区目录 | 原规范配置与运行数据混合目录 | Redesign | 可跟踪定义与 `.opencowork/runtime` 本机状态分离，`init` 生成 Git 忽略规则。 | M1 | M0-ACC-003, M1-ACC-006 | 避免误提交状态和 Secret。 |
| CAP-010 | OpenCoWorkPaths 与路径包含安全 | 原规范 CraftPath/路径服务 | Redesign | 规范化绝对路径、按声明文件解析相对路径，拒绝逃逸与符号链接越界。 | M1 | M1-ACC-006, M10-ACC-006 | Windows/macOS 行为一致。 |
| CAP-011 | 正式平台矩阵 | 原规范跨平台能力与用户设备边界 | Deferred | 1.0 仅正式支持 `win-x64`、`osx-arm64`；Linux 与 Intel macOS 推迟到 1.x。 | M10 | M1-ACC-008, M10-ACC-010, M10-ACC-011 | 代码应隔离平台差异。 |
| CAP-012 | Electron 与 `node-run-as-node` 配置 | 原规范桌面宿主遗留边界 | Removed | 1.0 不包含 Electron 桌面宿主，也不保留相应环境变量兼容。 | M0 | M0-ACC-008 | .NET 原生运行时。 |

## 4. Workspace 生命周期

| CapabilityId | Capability | SourceEvidence | Decision | OpenCoWorkContract | OwnerMilestone | AcceptanceIds | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| CAP-013 | Workspace 发现 | 原规范 Workspace 定位能力 | Redesign | `--workspace`、最近 `.opencowork`、Git Root、CWD 的固定顺序。 | M1 | M1-ACC-005, M1-ACC-006 | Hub 不依赖 CWD。 |
| CAP-014 | WorkspaceRuntime 状态机 | 原规范 Workspace 生命周期 | PreserveSemantics | `Stopped/Starting/Running/Degraded/Stopping/Faulted`，状态转换可诊断。 | M1 | M1-ACC-003, M1-ACC-007 | 服务统一归属。 |
| CAP-015 | 不可变有效配置快照 | 原规范启动期配置能力 | Redesign | WorkspaceRuntime 启动时冻结配置；1.0 不做隐式热重载。 | M1 | M1-ACC-005 | 显式重启才生效。 |
| CAP-016 | 后台服务统一启停 | 原规范 Hosted Service 生命周期 | PreserveSemantics | 启动顺序确定、失败回滚、停止逆序、取消和超时有界。 | M1 | M1-ACC-003, M9-ACC-009 | 不遗留进程和锁。 |
| CAP-017 | ProjectionDegraded 降级运行 | 原规范存储降级与恢复 | Redesign | Journal 已提交但 SQLite 投影失败时暂停新工作，重放修复后恢复。 | M2 | M2-ACC-004, M10-ACC-004 | 不伪装回滚。 |
| CAP-018 | 工作区原生能力授信 | 原规范插件/命令信任边界 | Redesign | 原生插件、Hook、MCP、LSP、Channel Command、Shell Extension 初始均为 `PendingTrust`。 | M6 | M1-ACC-008, M6-ACC-002, M10-ACC-006 | 绑定路径、来源、版本、摘要、范围。 |

## 5. Durable Session

| CapabilityId | Capability | SourceEvidence | Decision | OpenCoWorkContract | OwnerMilestone | AcceptanceIds | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| CAP-019 | Thread-Turn-Item 聚合 | 原规范 Session 模型 | PreserveSemantics | 唯一 Session Core 管理 Thread、Turn、Item；所有入口复用 `ISessionService`。 | M2 | M2-ACC-001, M5-ACC-009 | 不建立第二状态机。 |
| CAP-020 | ThreadJournal 权威历史 | 原规范 Rollout 与回放 | Redesign | `ThreadJournal` 是模型历史和会话状态的权威追加日志。 | M2 | M0-ACC-005, M2-ACC-002 | Rollout 政名。 |
| CAP-021 | SQLite 查询投影与重建 | 原规范 Session 数据库能力 | Redesign | 列表、搜索、统计为可从 Journal Sequence 重建的投影。 | M2 | M2-ACC-003, M10-ACC-004 | `lastAppliedSequence`。 |
| CAP-022 | 同 Thread 串行与跨 Thread 并行 | 原规范会话并发协调 | PreserveSemantics | `ThreadWriteGate` 保护单 Thread 提交，不建立全局串行瓶颈。 | M2 | M2-ACC-004 | 支持 expectedSequence。 |
| CAP-023 | Queue、Steer、Cancel 与等待恢复 | 原规范 Turn 队列和交互等待 | PreserveSemantics | 排队输入独立于 Turn；审批、输入和取消均为持久业务状态。 | M2 | M2-ACC-005, M2-ACC-006 | Resolution 首次有效。 |
| CAP-024 | Archive、Delete、Fork 与 Rollback | 原规范会话管理操作 | Redesign | Archive/删除采用可恢复顺序；Fork 自包含；Rollback 追加且不声称撤销外部副作用。 | M2 | M2-ACC-007, M2-ACC-008, M10-ACC-005 | 删除需 prepare token。 |

## 6. Agent 与 Provider

| CapabilityId | Capability | SourceEvidence | Decision | OpenCoWorkContract | OwnerMilestone | AcceptanceIds | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| CAP-025 | AgentSession 与 AgentFactory | 原规范 ModelSession/Agent 创建能力 | Redesign | 使用 `AgentSession` 表达代理会话，由 `AgentFactory` 组装 Provider、Context 与 Tool Snapshot。 | M3 | M3-ACC-001 | 语境优先，不机械改名。 |
| CAP-026 | Provider Registry | 原规范模型 Provider 体系 | PreserveSemantics | Provider 按稳定 ID 注册，能力、限制和认证需求可查询。 | M3 | M3-ACC-002, M6-ACC-001 | 不泄漏 Provider 私有实现。 |
| CAP-027 | Provider-neutral Auth | 原规范多 Provider 认证 | Redesign | 公共认证契约与 Provider 无关，Secret 由 OS Store/环境引用解析。 | M3 | M3-ACC-002, M10-ACC-006 | 禁止明文落盘。 |
| CAP-028 | 流式 Item、Reasoning 与 Usage | 原规范流式模型事件 | PreserveSemantics | 所有流片段进入 Item/Journal；Usage 可累积并可恢复。 | M3 | M3-ACC-003, M3-ACC-007 | 协议通过 item/delta 暴露。 |
| CAP-029 | 瞬态重试边界 | 原规范模型调用恢复 | PreserveSemantics | 首 Token 前可按策略重试；首 Token 后不得造成重复可见输出。 | M3 | M3-ACC-004 | 保留相关 Correlation。 |
| CAP-030 | Agent/Plan 模式 | 原规范模式切换与工具范围 | PreserveSemantics | 模式是持久状态；Plan 模式使用受限系统提示和只读工具曝光。 | M4 | M3-ACC-008, M4-ACC-008 | 不靠 Prompt 自觉约束。 |

## 7. Tool 与安全

| CapabilityId | Capability | SourceEvidence | Decision | OpenCoWorkContract | OwnerMilestone | AcceptanceIds | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| CAP-031 | 工具定义、Binding 与 Registration 分离 | 原规范工具注册模型 | PreserveSemantics | 模型可见定义、运行时绑定和来源注册是独立对象。 | M4 | M4-ACC-001 | 支持动态失效。 |
| CAP-032 | EffectiveToolSnapshot | 原规范每回合工具快照 | PreserveSemantics | Turn 开始冻结有效工具、权限和名称映射，进行中的 Turn 不受热更新影响。 | M4 | M4-ACC-002, M6-ACC-007 | 下一 Turn 使用新快照。 |
| CAP-033 | ToolInvocationPipeline 固定顺序 | 原规范 ToolDispatcher 安全链 | Redesign | 按 Snapshot、审计、Exposure、Lease、Authority、Schema、Policy、Hook、Approval、Timeout、Invoke、Normalize、Terminal 执行。 | M4 | M0-ACC-005, M4-ACC-003 | 禁止旁路。 |
| CAP-034 | Authority 与审批 | 原规范权限和 Approval | PreserveSemantics | 有效权限取交集；拒绝与审批同样产出 Started/Terminal 审计。 | M4 | M4-ACC-004, M4-ACC-005, M10-ACC-006 | Workspace 不可提权。 |
| CAP-035 | Timeout、Cancellation 与 OutcomeUnknown | 原规范工具超时/取消 | Redesign | 结果不明的非幂等副作用不自动重试，交互失败、无人值守转人工处理。 | M4 | M4-ACC-006, M4-ACC-007, M8-ACC-006 | 稳定错误码。 |
| CAP-036 | 内置 Node REPL | 原规范 Node 执行工具 | Deferred | 1.0 不承诺内置 Node REPL；File/Shell/Web 提供最小跨平台工具面。 | M4 | M4-ACC-009 | 可在 1.x 作为授信扩展。 |

## 8. Context、记忆与 Insights

| CapabilityId | Capability | SourceEvidence | Decision | OpenCoWorkContract | OwnerMilestone | AcceptanceIds | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| CAP-037 | 系统提示与运行时上下文组合 | 原规范 Prompt 组装 | PreserveSemantics | 按确定顺序组装产品、Workspace、Mode、Skill 和动态上下文。 | M3 | M3-ACC-001 | 记录版本/摘要。 |
| CAP-038 | Token 预算与窗口跟踪 | 原规范 Context Window | PreserveSemantics | Provider 限制、已用 Token 和预留输出共同决定可用预算。 | M3 | M3-ACC-005 | 预算可诊断。 |
| CAP-039 | Micro/Partial Compaction | 原规范上下文压缩 | PreserveSemantics | 压缩产物持久化并可重放，不修改既有 Journal Entry。 | M3 | M3-ACC-005, M10-ACC-004 | 支持 Upcaster。 |
| CAP-040 | Prompt-too-long 响应式压缩 | 原规范溢出恢复 | PreserveSemantics | 压缩后重试不得重复当前 Turn 或已显示流片段。 | M3 | M3-ACC-006 | 重试次数有界。 |
| CAP-041 | Workspace Memory | 原规范长期记忆能力 | Redesign | 文件保存内容、SQLite 保存元数据与摘要，写入受 Authority 与路径约束。 | M6 | M6-ACC-009, M10-ACC-006 | 不等同全局用户记忆。 |
| CAP-042 | Dreams/后台反思 | 原规范 DreamsService | Redesign | 使用 `WorkspaceInsightService`、`InsightRun`、`ImprovementProposal`；只产出可审阅建议，不直接改代码。 | M9 | M9-ACC-008 | Wire 域为 insight。 |

## 9. Wire、ACP 与 CLI

| CapabilityId | Capability | SourceEvidence | Decision | OpenCoWorkContract | OwnerMilestone | AcceptanceIds | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| CAP-043 | JSON-RPC initialize 与能力协商 | 原规范 AppServer 协议 | Redesign | 强制 initialize/initialized，协商 Wire 版本、能力、限制与进程绑定 Workspace；第一客户端为 OpenCoWork Desktop。 | M5 | M0-ACC-004, M5-ACC-001 | 1.0 后按 SemVer。 |
| CAP-044 | stdio JSONL 与 WebSocket | 原规范多 Transport | PreserveSemantics | Desktop 子进程默认 stdio；WS 仅 loopback 且只接受环境注入 Token 对应的 Bearer Header。 | M5 | M5-ACC-002 | stdout/stderr 隔离，慢客户端有界。 |
| CAP-045 | Thread/Turn/Item 方法与事件 | 原规范会话 RPC | Redesign | 使用 `domain/action`、分页 history 和统一 `item/delta`；补齐 model/mode，不复制 214 方法。 | M5 | M5-ACC-003, M5-ACC-004 | Generated Catalog。 |
| CAP-046 | 订阅、Sequence 与重连 | 原规范事件订阅 | Redesign | 原子快照+Cursor、Thread 内有序、至少一次、eventId+sequence 去重。 | M5 | M5-ACC-005, M5-ACC-008 | Sequence 来自 Journal。 |
| CAP-047 | ACP Bridge | 原规范 ACP 适配 | PreserveSemantics | 固定稳定 ACP v1，只转换 initialize/new/load/prompt/cancel/set_mode，不维护独立 Session 状态；通用 UserInput 明确失败并取消。 | M5 | M5-ACC-007, M5-ACC-009 | stdio only，历史回放不重复。 |
| CAP-048 | 桌面/Web Dashboard 与内嵌交互 Host | 原规范 UI/Widget/Welcome 能力 | Deferred | 1.0 只提供 CLI、协议、查询和可引用 Visualization Artifact；不提供嵌入式 UI Host、Welcome Suggestion 或 Widget State。 | M9 | M9-ACC-008 | UI 推迟到 1.x；遗留 UI 状态不兼容。 |

## 10. Skills、Plugins、MCP、LSP 与 Hooks

| CapabilityId | Capability | SourceEvidence | Decision | OpenCoWorkContract | OwnerMilestone | AcceptanceIds | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| CAP-049 | Skills 发现、变体与提示注入 | 原规范 Skill 系统 | PreserveSemantics | Workspace/User/Plugin 来源可追踪，冲突显式，Turn 冻结有效 Skill Snapshot。 | M6 | M6-ACC-003 | 受信任和优先级约束。 |
| CAP-050 | Plugin Manifest、安装与 Lock | 原规范插件生态 | Redesign | `.opencowork-plugin/plugin.json` 声明贡献；`plugins.lock.json` 固定来源、版本与摘要。 | M6 | M6-ACC-002, M6-ACC-004 | 无 `.craft` 兼容。 |
| CAP-051 | Marketplace | 原规范 Marketplace 能力 | Deferred | 1.0 支持来源与安装契约/协议域；图形化 Marketplace UI 推迟到 1.x。 | M6 | M6-ACC-004 | 供应链证据必须可追踪。 |
| CAP-052 | MCP 与 LSP 进程生命周期 | 原规范 MCP/LSP 管理 | PreserveSemantics | 子进程启动、握手、健康、取消、断连、进程树终止统一归 WorkspaceRuntime。 | M6 | M6-ACC-005, M6-ACC-006 | 双平台真实测试。 |
| CAP-053 | 私有 App Binding | 原规范 Legacy App Binding | Redesign | 替换为 Dynamic Tool Binding + Lease；来源断连或 Lease 过期后旧 Binding 立即失效。 | M6 | M6-ACC-007 | 不暴露私有协议。 |
| CAP-054 | MCP Apps 内嵌 UI | 原规范 MCP App Host | Deferred | 1.0 支持 MCP Tool/Resource/OAuth/Status，不承诺 MCP Apps 嵌入式 UI。 | M6 | M6-ACC-005 | 1.x 再评估 Host。 |

## 11. Multi-Agent CoWork

| CapabilityId | Capability | SourceEvidence | Decision | OpenCoWorkContract | OwnerMilestone | AcceptanceIds | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| CAP-055 | SubAgent 父子关系与深度限制 | 原规范子代理能力 | PreserveSemantics | 父子关系持久化，限制深度、并发、预算，取消可传播。 | M7 | M7-ACC-001, M7-ACC-005 | 不靠内存引用。 |
| CAP-056 | Team、Member 与 Mission | 原规范 Teams | Redesign | Teams 元数据和 Mission 权威状态进入 SQLite，成员会话各用独立 ThreadJournal。 | M7 | M7-ACC-002, M7-ACC-009 | 避免 state.json 热点。 |
| CAP-057 | MissionTask DAG | 原规范任务依赖 | PreserveSemantics | 校验无环，只有依赖满足的 Ready Task 可调度，阻塞原因持久化。 | M7 | M7-ACC-003 | 支持 Review。 |
| CAP-058 | Mailbox | 原规范 Agent 间消息 | PreserveSemantics | 持久异步消息用于补充、交接、阻塞、审查、返工和 Artifact 引用；不替代 Task/Thread。 | M7 | M7-ACC-004 | Pending 到 DeadLettered。 |
| CAP-059 | Artifact 与 Scratchpad | 原规范协作文件 | Redesign | 文件保存内容，SQLite 保存相对路径、摘要、来源和权限；路径必须包含于运行时根。 | M7 | M7-ACC-006, M10-ACC-006 | 可按摘要去重。 |
| CAP-060 | Worktree、Leader 综合与 Origin 回传 | 原规范隔离执行和汇总 | PreserveSemantics | Worktree 独立授信和清理；Leader 在必需任务终态后综合，Origin 最终结果幂等回传一次。 | M7 | M7-ACC-007, M7-ACC-008, M7-ACC-010 | Dirty Worktree 不自动删除。 |

## 12. Automations

| CapabilityId | Capability | SourceEvidence | Decision | OpenCoWorkContract | OwnerMilestone | AcceptanceIds | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| CAP-061 | 可版本控制 Automation 定义 | 原规范任务定义 | Redesign | YAML 定义是事实源，Schema 校验后生成不可变定义版本。 | M8 | M8-ACC-001 | 存放 automations/definitions。 |
| CAP-062 | Fluid 模板与输入解析 | 原规范模板能力 | PreserveSemantics | 模板在受限上下文求值，失败不创建半成品 Run。 | M8 | M8-ACC-002 | 禁止任意代码执行。 |
| CAP-063 | Manual/Cron、时区与下一次运行 | 原规范 Cron Scheduler | Redesign | Wire 域命名为 schedule；显式 IANA/系统时区，持久化下次触发并去重派发。 | M8 | M8-ACC-003 | DST 有测试。 |
| CAP-064 | Run 定义、权限、插件和工具快照 | 原规范无人值守执行 | PreserveSemantics | Run 创建时冻结全部有效快照，定义中途变更只影响后续 Run。 | M8 | M8-ACC-004 | 可审计。 |
| CAP-065 | 并发、互斥、Lease 与崩溃恢复 | 原规范调度并发 | PreserveSemantics | SQLite 权威状态+Lease+Reconciler，重启不重复派发。 | M8 | M8-ACC-005, M8-ACC-008 | BEGIN IMMEDIATE。 |
| CAP-066 | NeedsAttention 与无人值守权限 | 原规范审批等待 | Redesign | 权限不自动扩大；需要人工或结果不明时进入 NeedsAttention，可恢复、取消或超时。 | M8 | M8-ACC-006, M8-ACC-007, M8-ACC-009 | 不读 Console。 |

## 13. Gateway、Hub 与可靠消息

| CapabilityId | Capability | SourceEvidence | Decision | OpenCoWorkContract | OwnerMilestone | AcceptanceIds | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| CAP-067 | Channel Adapter 与多渠道隔离 | 原规范 External Channel | Redesign | Wire 域为 channel；每个 Channel 独立生命周期、凭据和故障边界。 | M9 | M9-ACC-001, M9-ACC-007 | 先交付 Test/Webhook。 |
| CAP-068 | Inbound 去重与 Thread 映射 | 原规范 Message Router | PreserveSemantics | 入站先持久化，外部消息 ID 幂等映射到单个 Turn。 | M9 | M9-ACC-002 | 至少一次。 |
| CAP-069 | Outbox 与 Dead Letter | 原规范可靠出站 | PreserveSemantics | 发送前写 Outbox；状态 Pending/Sending/Sent/Failed/DeadLettered，可恢复重试。 | M9 | M9-ACC-003 | 不承诺 Exactly Once。 |
| CAP-070 | 单外部会话顺序 | 原规范 Channel 顺序 | PreserveSemantics | 同一外部会话串行，跨会话可并行，不保证全局顺序。 | M9 | M9-ACC-004 | 分区键稳定。 |
| CAP-071 | 附件与外部媒体安全 | 原规范媒体缓存 | Redesign | 文件内容进入 runtime/external-channel-media，校验大小、类型、摘要和路径包含。 | M9 | M9-ACC-005, M10-ACC-006 | 不信任远端文件名。 |
| CAP-072 | Hub 与 Dashboard 查询 | 原规范 Hub/Dashboard | Redesign | Hub 使用用户级注册发现 Workspace；只提供 Usage/Trace/Insight 查询，不交付桌面/Web UI。 | M9 | M9-ACC-006, M9-ACC-008 | UI 见 CAP-048。 |

## 14. 日志、Tracing 与 Doctor

| CapabilityId | Capability | SourceEvidence | Decision | OpenCoWorkContract | OwnerMilestone | AcceptanceIds | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| CAP-073 | 结构化日志与 Correlation | 原规范日志能力 | PreserveSemantics | Workspace/Thread/Turn/Tool/Run 共享可传播 Correlation，日志写入结构化 Sink。 | M1 | M1-ACC-007, M9-ACC-010 | stdio 日志只到 stderr。 |
| CAP-074 | Secret 与敏感字段脱敏 | 原规范安全日志 | PreserveSemantics | API Key、Token、Authorization、Secret 配置和值不得进入日志、Journal、事件。 | M1 | M1-ACC-007, M10-ACC-006 | 失败路径同样脱敏。 |
| CAP-075 | Usage 统计 | 原规范 Token/成本统计 | PreserveSemantics | 从已提交事件/投影聚合 Token、模型、任务与 Channel Usage，可重建。 | M9 | M3-ACC-007, M9-ACC-010 | 不作为计费系统承诺。 |
| CAP-076 | Tracing | 原规范调用追踪 | PreserveSemantics | 跨 Session、Tool、MCP、Mission、Automation、Gateway 传播 Trace/Correlation。 | M9 | M9-ACC-010 | 不记录 Secret Payload。 |
| CAP-077 | Heartbeat 与后台健康 | 原规范后台心跳 | PreserveSemantics | Heartbeat 反映 WorkspaceRuntime 和关键服务状态，停止时完整注销。 | M9 | M9-ACC-009 | 不能伪装业务成功。 |
| CAP-078 | Doctor 与静态分析证据边界 | 原规范诊断与源分析 | Redesign | `doctor` 验证 SDK、路径、配置、SQLite、进程与平台；静态分析只作参考，不得宣称为运行时行为证据。 | M1 | M1-ACC-008, M10-ACC-012 | 严格配置模式可失败。 |

## 15. 决策汇总

| Decision | 数量 | 结论 |
| --- | ---: | --- |
| PreserveSemantics | 36 | 保留行为语义，以 OpenCoWork 边界实现。 |
| Redesign | 33 | 保留目标，冻结新的命名、存储或协议设计。 |
| Deferred | 5 | 明确推迟到 1.x，不阻塞 1.0。 |
| Removed | 4 | 明确不兼容或不实现，不属于缺口。 |
| **Total** | **78** | **全部有确定去向，无 TBD。** |

Deferred 项：

- Linux 与 Intel macOS 正式支持；
- 内置 Node REPL；
- 桌面/Web Dashboard 与内嵌交互 Host；
- 图形化 Marketplace；
- MCP Apps 内嵌 UI。

`CAP-048` 同时包含多个同一边界下的 UI 延期项，因此表中 Decision 数量按
Capability 计数，不按子项计数。

Removed 项：

- DotCraft 程序集、命名空间和二进制兼容；
- 类型、方法和配置数量一比一；
- `.craft` 工作区兼容；
- Electron / `node-run-as-node` 遗留配置。

能力台账只能通过带 Acceptance ID 和替代契约的修订更新，不能以“实现时再说”
重新引入开放结论。
