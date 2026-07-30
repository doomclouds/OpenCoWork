# OpenCoWork M0-M10 验收目录

## 文档状态

- 状态：已冻结
- 日期：2026-07-25
- 修订：2026-07-28，按 M5 Desktop-first 方案细化既有九项验收，并回填
  `8 Passed / 1 Planned` 的实现证据；不新增或重排 Acceptance ID
- 修订：2026-07-29，补齐 M4/M5 Windows 真机证据；M4、M5 验收全部
  `Passed`，不新增或重排 Acceptance ID
- 修订：2026-07-29，补齐 M6 Windows 真机与发布目录证据；M6 十项验收全部
  `Passed`，不新增或重排 Acceptance ID
- 所属里程碑：OpenCoWork Runtime 1.0
- 契约规格：
  [OpenCoWork M0 Contract Freeze](2026-07-25-open-cowork-m0-contract-freeze-design.md)
- 能力台账：
  [OpenCoWork M0 能力台账](2026-07-25-open-cowork-m0-capability-ledger.md)

## 1. 证据规则

Acceptance ID 是稳定标识，创建后永不重排。废弃验收只能将 Status 改为
`Superseded` 并填写 `SupersededBy`，不能复用原 ID。

允许的 EvidenceType：

- `AutomatedTest`
- `ContractSnapshot`
- `FaultInjection`
- `MigrationTest`
- `SecurityTest`
- `PerformanceTest`
- `ManualValidation`
- `RealPlatformValidation`

Platform 标签：

- `All`：平台无关契约；
- `win-x64`：Windows x64 真实环境；
- `osx-arm64`：Apple Silicon macOS 真实环境；
- `DualPlatform`：同一验收必须同时具备 win-x64 与 osx-arm64 独立证据。

执行规则：

- 后续 Slice 必须把 ExpectedEvidence 替换或补充为实际文件、命令、测试报告、
  日志、快照或发布产物链接；
- “存在一个测试文件”不等于通过，必须有可复现的执行结果；
- `DualPlatform` 不接受在同一平台模拟另一平台；
- 每个 Slice 的证据必须包含此前已完成 Slice 的累计回归；
- 任一 P0/P1 缺陷、失败验收或未分类的缺失硬证据都会阻止 Slice 标记 Done。

Status 使用 `Passed`、`Planned`、`Failed`、`Deferred`、`Superseded`。
`Deferred` 只允许用于用户明确接受的 Slice 边界变更，必须链接后续台账；它不代表
通过，也不能用于绕过 M10 发布候选关闭门禁。

2026-07-25 用户确认 M1 先以 `win-x64` 正式证据关闭；M1 的 macOS 真机项不作
伪通过，统一滚动登记到仓库 `AGENTS.md`，并在 M10 / OpenCoWork 1.0 发布前清零。
该调整只改变 M1 的收口时点，不改变 1.0 对 `osx-arm64` 真实平台验收的承诺。

2026-07-28 用户确认 M3 以 DeepSeek 官方作为首个真实 Provider，并接受
`osx-arm64` 的两条真实模型证据关闭本 Slice。千问 Token Plan、其他 Provider 和
`win-x64` 真实 Provider 兼容性统一进入
[`docs/provider-validation-backlog.md`](../../provider-validation-backlog.md)，
并由 M10 的双平台兼容矩阵继续约束；未验证项不得对外宣称支持。

2026-07-28 用户确认关闭 M4 功能需求，并将 `M4-ACC-006`、`M4-ACC-009` 缺少的
`win-x64` 真机维度标记为 `Deferred`，统一进入
[`docs/platform-release-validation-ledger.md`](../../platform-release-validation-ledger.md)
后续集中补验。平台状态保持 `Pending`，不得对外宣称 Windows 已通过，且 M10
关闭前必须补齐。

2026-07-29 已在 Windows 11 x64 真机完成 M4/M5 补验：
`M4-ACC-006`、`M4-ACC-009` 由 `Deferred` 改为 `Passed`，
`M5-ACC-002` 由 `Planned` 改为 `Passed`。验证基线、Source/Test Patch、
环境、测试计数、发布物摘要和发布目录场景见
[`docs/platform-release-validation-ledger.md`](../../platform-release-validation-ledger.md)；
M10 仍须在最终发布候选上重跑双平台验收。

2026-07-29 已在 Windows 11 x64 真机完成 M6 补验：
`M6-ACC-002`、`M6-ACC-004`、`M6-ACC-005`、`M6-ACC-006`、
`M6-ACC-009` 由 `Planned` 改为 `Passed`。Windows 基线、Source/Test Patch、
Credential Manager、Git、Memory、隐藏 Terminal、Wire 1.1、进程树与 Secret
Canary 证据见
[`docs/platform-release-validation-ledger.md`](../../platform-release-validation-ledger.md)；
M10 仍须在最终发布候选上重跑完整双平台验收。

## 2. M0 - Contract Freeze（8）

| AcceptanceId | Requirement | CapabilityIds | EvidenceType | Platforms | ExpectedEvidence | Status | SupersededBy |
| --- | --- | --- | --- | --- | --- | --- | --- |
| M0-ACC-001 | OpenCoWork 品牌、CLI、目录、插件清单和关键领域命名已冻结，明确不兼容 `.craft`。 | CAP-002, CAP-007, CAP-025, CAP-033 | ContractSnapshot | All | Contract Freeze §2 与命名检索结果。 | Passed | — |
| M0-ACC-002 | 七个生产程序集、六个测试项目及禁止依赖方向已冻结。 | CAP-001, CAP-003, CAP-005 | ContractSnapshot | All | Contract Freeze §3 与路线规格程序集基线。 | Passed | — |
| M0-ACC-003 | JSONC 覆盖、合并、双平面目录、路径发现和信任模型已冻结。 | CAP-008, CAP-009, CAP-010, CAP-013, CAP-018 | ContractSnapshot | All | Contract Freeze §4。 | Passed | — |
| M0-ACC-004 | OpenCoWork Wire 的握手、命名、核心方法、事件、订阅、幂等和 Transport 已冻结。 | CAP-043, CAP-044, CAP-045, CAP-046 | ContractSnapshot | All | Contract Freeze §5。 | Passed | — |
| M0-ACC-005 | 权威源、Journal 提交点、SQLite、故障恢复和核心状态机已冻结。 | CAP-017, CAP-020, CAP-021, CAP-033, CAP-035 | ContractSnapshot | All | Contract Freeze §6-§8。 | Passed | — |
| M0-ACC-006 | 能力台账包含 60-90 个可观察能力，所有 Decision 均非 TBD。 | CAP-001-CAP-078 | ContractSnapshot | All | 能力台账 78 项统计与自动校验。 | Passed | — |
| M0-ACC-007 | M1-M10 均具有稳定 Acceptance ID、证据类型、平台和预期证据。 | CAP-001-CAP-078 | ContractSnapshot | All | 本目录编号连续性与字段完整性校验。 | Passed | — |
| M0-ACC-008 | 文档不存在未解释的 DotCraft、`.craft`、旧程序集或数字一比一兼容承诺。 | CAP-002, CAP-006, CAP-007, CAP-012 | ContractSnapshot | All | 规格定向检索与例外清单。 | Passed | — |

## 3. M1 - Runtime Foundation（8）

| AcceptanceId | Requirement | CapabilityIds | EvidenceType | Platforms | ExpectedEvidence | Status | SupersededBy |
| --- | --- | --- | --- | --- | --- | --- | --- |
| M1-ACC-001 | Solution、七个生产项目和六个测试项目在 .NET 10 下干净构建。 | CAP-001, CAP-002 | AutomatedTest | win-x64 | [M1 实施计划 Outcome 7](../plans/2026-07-25-open-cowork-m1-runtime-foundation-implementation-plan.md)：Windows Release build 零警告零错误。 | Passed | — |
| M1-ACC-002 | ModuleRegistry 拒绝重复模块和依赖环，并按拓扑启动、逆序停止。 | CAP-003 | AutomatedTest | All | `ModuleRegistryTests` 与 `WorkspaceRuntimeTests`，见 M1 实施计划 Windows 完整测试记录。 | Passed | — |
| M1-ACC-003 | Composition Root 只选择一个主宿主，启动失败可回滚且停止有界。 | CAP-004, CAP-014, CAP-016 | FaultInjection | win-x64 | `RuntimeCompositionIntegrationTests` 与 `WorkspaceRuntimeTests`，Windows 完整回归通过。 | Passed | — |
| M1-ACC-004 | Generators 生成模块、配置 Schema 和 Wire Catalog，重复贡献产生稳定诊断。 | CAP-005 | ContractSnapshot | All | `RuntimeCatalogGeneratorTests`：14 passed，覆盖稳定源码与 `OCWGEN001`-`OCWGEN008`。 | Passed | — |
| M1-ACC-005 | 配置优先级、对象/集合/数组/null 合并和严格未知字段行为符合契约。 | CAP-008, CAP-015 | AutomatedTest | All | `ConfigurationPipelineTests` 与 `CliIntegrationTests.Strict_config_and_parser_failures_use_stable_exit_codes`。 | Passed | — |
| M1-ACC-006 | `opencowork init` 创建安全双平面目录，Workspace 发现和路径逃逸保护正确。 | CAP-009, CAP-010, CAP-013 | SecurityTest | win-x64 | Windows init、Junction/Reparse Point、原生文件及目录 Symlink、大小写、写前复检和 Git Ignore 测试通过。 | Passed | — |
| M1-ACC-007 | WorkspaceRuntime 状态、结构化日志和 Secret 脱敏在成功/失败路径一致。 | CAP-014, CAP-073, CAP-074 | FaultInjection | All | `WorkspaceRuntimeTests`、`StructuredLoggingTests` 和 `DataFoundationIntegrationTests`。 | Passed | — |
| M1-ACC-008 | `--version`、`init`、`doctor` 验证 SDK、配置、SQLite、信任和正式平台边界。 | CAP-011, CAP-018, CAP-078 | RealPlatformValidation | win-x64 | M1 实施计划记录 Windows 发布可执行文件实跑，Doctor 七项检查全部通过。 | Passed | — |

## 4. M2 - Durable Session Core（10）

| AcceptanceId | Requirement | CapabilityIds | EvidenceType | Platforms | ExpectedEvidence | Status | SupersededBy |
| --- | --- | --- | --- | --- | --- | --- | --- |
| M2-ACC-001 | Thread、Turn、Item 的创建、状态转换和终态不变量由唯一 Session Core 执行。 | CAP-019 | AutomatedTest | All | `SessionDomainTests`、`SessionContractTests` 与 [M2 交付归档](../archives/2026-07/2026-07-26-open-cowork-m2-durable-session-core-archives.md)。 | Passed | — |
| M2-ACC-002 | ThreadJournal Entry 严格递增、校验完整，Flush 前不产生可见提交。 | CAP-020 | FaultInjection | DualPlatform | `ThreadJournalTests` 写入故障矩阵与 `SessionCrashRecoveryIntegrationTests` 真实子进程终止。 | Passed | — |
| M2-ACC-003 | 删除 SQLite Session 投影后可由 Journal 完整重建列表、历史和统计。 | CAP-021 | AutomatedTest | All | `SessionProjectionTests.Full_rebuild_removes_orphans_preserves_delete_receipts_and_matches_snapshot`。 | Passed | — |
| M2-ACC-004 | 同 Thread 写入串行、不同 Thread 并行；投影失败进入并恢复 ProjectionDegraded。 | CAP-017, CAP-022 | FaultInjection | All | `SessionServiceTests` 的并发、Sequence 冲突、投影降级与追平场景。 | Passed | — |
| M2-ACC-005 | Queue 的追加、删除、重排和 Steer 在重启后保持确定顺序。 | CAP-023 | AutomatedTest | All | `SessionQueueTests` 的重放、随机序列、Steer、自动标题和 128 项边界。 | Passed | — |
| M2-ACC-006 | Approval、UserInput、Cancel 的等待和首次 Resolution 语义可恢复且幂等。 | CAP-023 | FaultInjection | All | `SessionExecutionTests` 的等待、Resolution、Cancel 竞态、Checkpoint 与重启恢复。 | Passed | — |
| M2-ACC-007 | Archive/Unarchive 的 Journal、文件移动、投影与事件顺序可在崩溃后协调。 | CAP-024 | FaultInjection | DualPlatform | `SessionRecoveryTests` 覆盖 Archive/Unarchive 各三个已提交阶段及 Reconciler。 | Passed | — |
| M2-ACC-008 | 永久删除需要 prepare token，失败可续跑，且不删除外部文件或 Dirty Worktree。 | CAP-024 | SecurityTest | DualPlatform | `SessionRecoveryTests` 覆盖 Token、八个删除故障点、Junction/Reparse 逃逸和外部文件保护；M2 不创建 Worktree 绑定。 | Passed | — |
| M2-ACC-009 | Fork 自包含，Rollback 只追加历史并报告外部副作用未撤销。 | CAP-024 | AutomatedTest | All | `SessionRecoveryTests.Fork_survives_source_delete_and_rollback_replaces_model_history`。 | Passed | — |
| M2-ACC-010 | 尾部损坏可安全截断，中段损坏进入 RecoveryRequired 且不阻塞其他 Thread。 | CAP-020, CAP-021 | FaultInjection | DualPlatform | `ThreadJournalTests` 损坏 Corpus 与 `SessionRuntimeTests.Startup_isolates_corrupt_thread_and_recovers_interrupted_turn`。 | Passed | — |

## 5. M3 - Agent Runtime Alpha（8）

| AcceptanceId | Requirement | CapabilityIds | EvidenceType | Platforms | ExpectedEvidence | Status | SupersededBy |
| --- | --- | --- | --- | --- | --- | --- | --- |
| M3-ACC-001 | AgentFactory 确定性组装 AgentSession、系统提示、Context 和空 Tool Snapshot。 | CAP-025, CAP-037 | ContractSnapshot | All | `AgentFactoryTests`、Prompt Golden、`AgentContractTests` 和 [M3 交付归档](../archives/2026-07/2026-07-27-open-cowork-m3-agent-runtime-alpha-archives.md)。 | Passed | — |
| M3-ACC-002 | Fake Provider 和首个真实 Provider 共享中立契约，认证 Secret 不落盘。 | CAP-026, CAP-027 | SecurityTest | osx-arm64 | `ChatCompletionClientTests`、`StructuredLoggingTests`、Secret Canary，以及提交 `3da2e47` 上 DeepSeek Pro/Flash 两条真实冒烟。 | Passed | — |
| M3-ACC-003 | 流式响应、Reasoning 和 Item 终态按 Journal 顺序持久化并可恢复。 | CAP-028 | AutomatedTest | All | `AgentRuntimeExecutorTests`、`SessionExecutionTests` 和 `SessionCrashRecoveryIntegrationTests`。 | Passed | — |
| M3-ACC-004 | 首 Token 前瞬态错误可重试，首 Token 后中断不重复已显示内容。 | CAP-029 | FaultInjection | All | `AgentRuntimeExecutorTests` 与 `ChatCompletionClientTests` 的两阶段流中断、协议错误和 Invocation 计数。 | Passed | — |
| M3-ACC-005 | Token 预算、Micro/Partial Compaction 和 Checkpoint 在重启后保持一致。 | CAP-038, CAP-039 | AutomatedTest | All | `AgentFactoryTests`、`CompactionTests`、`SessionProjectionTests` 和 `SessionRecoveryTests`。 | Passed | — |
| M3-ACC-006 | Prompt-too-long 触发有界响应式压缩，当前 Turn 不重复。 | CAP-040 | FaultInjection | All | `CompactionTests` 的精确错误信封、三次调用预算、唯一当前 Turn 和失败边界。 | Passed | — |
| M3-ACC-007 | Usage 在流式、重试、压缩和恢复后无重复累计。 | CAP-028, CAP-075 | AutomatedTest | All | `AgentRuntimeExecutorTests`、`CompactionTests` 和 Usage 投影/重放对账。 | Passed | — |
| M3-ACC-008 | Agent/Plan 模式持久化，并为 M4 提供不同的工具曝光策略输入。 | CAP-030 | ContractSnapshot | All | `ChatCliIntegrationTests`、`SessionContractTests` 和 Queue/重启模式冻结测试。 | Passed | — |

## 6. M4 - Tool Runtime Alpha（10）

| AcceptanceId | Requirement | CapabilityIds | EvidenceType | Platforms | ExpectedEvidence | Status | SupersededBy |
| --- | --- | --- | --- | --- | --- | --- | --- |
| M4-ACC-001 | Tool Definition、Binding、Registration 与来源标识彼此独立。 | CAP-031 | ContractSnapshot | All | `ToolContractTests`、`ToolSnapshotTests` 和架构测试。 | Passed | — |
| M4-ACC-002 | 每个 Turn 冻结 EffectiveToolSnapshot 和 Provider 名称双向映射。 | CAP-032 | AutomatedTest | All | `ToolSnapshotTests`、热更新竞态、名称限制和碰撞隔离测试。 | Passed | — |
| M4-ACC-003 | ToolInvocationPipeline 的阶段顺序不可旁路，所有阶段可观测。 | CAP-033 | AutomatedTest | All | `ToolInvocationPipelineTests` 的逐阶段 Trace、顺序与旁路防护。 | Passed | — |
| M4-ACC-004 | Audience、Exposure、Lease、Authority、Schema、Policy 拒绝均有稳定错误码。 | CAP-033, CAP-034 | SecurityTest | All | 拒绝矩阵、Schema/Policy 失败和稳定错误契约测试。 | Passed | — |
| M4-ACC-005 | Hook 和 Approval 不能扩大权限，拒绝调用同样产生 Started/Terminal 审计。 | CAP-034 | SecurityTest | All | Authority 交集、恶意 Hook、重复审批和 CLI Approval/Resume 测试。 | Passed | — |
| M4-ACC-006 | Timeout 与 Cancellation 贯穿 Provider、Tool 和子进程并进入单一终态。 | CAP-035 | FaultInjection | DualPlatform | 全阶段故障注入与双平台真实进程树残留通过；Windows 输出上限/取消专项见双平台台账。 | Passed | — |
| M4-ACC-007 | 非幂等副作用在结果不明时不自动重试，并产生 `tool.outcomeUnknown`。 | CAP-035 | FaultInjection | All | Safe/Unsafe 恢复、提交窗口和副作用唯一性故障注入。 | Passed | — |
| M4-ACC-008 | Plan 模式不能调用写入、执行或网络副作用工具。 | CAP-030 | SecurityTest | All | `ToolSnapshotTests` 的 Mode、Effect、Authority 和 Provider 名称矩阵。 | Passed | — |
| M4-ACC-009 | 最小 File、Shell、Web 工具在双平台遵守路径、进程、权限和输出限制。 | CAP-036 | RealPlatformValidation | DualPlatform | `osx-arm64` File/zsh/Web 与 `win-x64` File/PowerShell/Web 发布目录真实 CLI 审批链通过。 | Passed | — |
| M4-ACC-010 | 模型重试、恢复和重复 Tool Call ID 不会重复已提交副作用。 | CAP-032, CAP-033, CAP-035 | FaultInjection | All | 重复 Call ID、Journal 重放、Checkpoint 恢复和副作用计数测试。 | Passed | — |

## 7. M5 - OpenCoWork Wire Alpha（9）

| AcceptanceId | Requirement | CapabilityIds | EvidenceType | Platforms | ExpectedEvidence | Status | SupersededBy |
| --- | --- | --- | --- | --- | --- | --- | --- |
| M5-ACC-001 | 未 initialize 的业务请求被拒绝，协商结果固定版本、真实能力、限制和进程绑定 Workspace。 | CAP-043 | ContractSnapshot | All | `OpenCoWorkJsonRpcTests` 的 initialize 正反例、Workspace 边界与快照；基线 `882efd9`。 | Passed | — |
| M5-ACC-002 | stdio JSONL 满足 stdout/stderr 隔离；loopback WebSocket 只接受 Bearer Header Token；两者遵守 UTF-8、上限和慢客户端规则。 | CAP-044 | SecurityTest | DualPlatform | `ProtocolServerTests`、`ProtocolProcessIntegrationTests` 与双平台发布目录 Protocol TestClient 均通过，见[双平台台账](../../platform-release-validation-ledger.md)。 | Passed | — |
| M5-ACC-003 | M5 核心 method/event（含 history/model/mode/delete prepare）全部存在且 Generated Wire Catalog 无重复。 | CAP-045 | ContractSnapshot | All | `RuntimeCatalogGeneratorTests`、`OpenCoWorkJsonRpcTests` 与 Generated Wire Catalog 快照。 | Passed | — |
| M5-ACC-004 | Desktop 主路径从 create/history/subscribe/start 到 Turn 终态、Approval、Input、Queue、Steer 一致，start 提交即返回。 | CAP-019, CAP-045 | AutomatedTest | All | `OpenCoWorkJsonRpcTests` 全方法矩阵与 Protocol TestClient 发布目录进程场景。 | Passed | — |
| M5-ACC-005 | subscribe 原子返回快照+Sequence，afterSequence 重连不丢失且可去重。 | CAP-046 | FaultInjection | All | `OpenCoWorkJsonRpcTests` 断连/追赶边界与 TestClient `resumeAfterSequence(0)` 严格递增、零重复检查。 | Passed | — |
| M5-ACC-006 | Request ID、idempotencyKey、expectedSequence 和业务 Cancel 各自语义独立。 | CAP-023, CAP-046 | ContractSnapshot | All | `OpenCoWorkJsonRpcTests` 的请求取消、幂等与 sequence 冲突矩阵；TestClient Wire/ACP 业务取消。 | Passed | — |
| M5-ACC-007 | ACP 稳定 v1 initialize/new/load/prompt/cancel/set_mode 正确映射且历史不重复；通用 UserInput 明确失败并取消。 | CAP-047 | AutomatedTest | All | `AcpConnectionTests` 6 项兼容性用例、进程测试与 TestClient new/prompt/cancel/reconnect/load。 | Passed | — |
| M5-ACC-008 | 稳定错误响应不包含堆栈、Secret、内部绝对路径或不透明异常文本。 | CAP-044, CAP-046 | SecurityTest | All | Wire/ACP Error Snapshot、进程 stdout/stderr 隔离与 TestClient 对协议、日志、Journal、SQLite、配置的 Secret Canary 扫描。 | Passed | — |
| M5-ACC-009 | Protocol/ACP 只调用 ISessionService，不直接写 Store，不建立第二套 Thread/Turn 状态机。 | CAP-019, CAP-047 | AutomatedTest | All | `ArchitectureTests` 5 项通过；`OpenCoWork.Protocol` 生产路径只依赖 Abstractions/`ISessionService`。 | Passed | — |

## 8. M6 - Capability Ecosystem（10）

| AcceptanceId | Requirement | CapabilityIds | EvidenceType | Platforms | ExpectedEvidence | Status | SupersededBy |
| --- | --- | --- | --- | --- | --- | --- | --- |
| M6-ACC-001 | Provider、Model 与 Auth 扩展域使用稳定 ID 和中立认证契约。 | CAP-026, CAP-027 | ContractSnapshot | All | `ProviderAuthTests`、`CapabilityProviderIntegrationTests` 与 Wire 1.1 Catalog Snapshot。 | Passed | — |
| M6-ACC-002 | 未授信工作区或插件不能加载原生工具、可信 Hook 或外部命令。 | CAP-018, CAP-050 | SecurityTest | DualPlatform | Trust/Plugin/Hook 拒绝与重授信自动化测试，以及 macOS Keychain、Windows Credential Manager 发布目录场景。 | Passed | — |
| M6-ACC-003 | Skill 来源、优先级、变体和提示注入确定，冲突不靠扫描顺序覆盖。 | CAP-049 | AutomatedTest | All | `SkillCatalogTests`、`ToolSnapshotTests` 与 Wire Catalog 分页/Override 黑盒。 | Passed | — |
| M6-ACC-004 | Plugin Manifest/Lock 固定来源、版本、摘要和贡献，安装失败可回滚。 | CAP-050, CAP-051 | SecurityTest | DualPlatform | `PluginPackageTests`、`PluginRuntimeTests`、双平台发布目录回滚与 collectible ALC 清理。 | Passed | — |
| M6-ACC-005 | MCP Tool/Resource/OAuth/Status 生命周期、取消和故障隔离符合契约。 | CAP-052, CAP-054 | FaultInjection | DualPlatform | `McpCapabilityTests`、`McpCapabilityIntegrationTests`、双平台发布目录 Wire 1.1 生命周期与清理。 | Passed | — |
| M6-ACC-006 | LSP 启动、请求路由、断连和进程树终止在双平台无残留。 | CAP-052 | RealPlatformValidation | DualPlatform | `LspCapabilityTests`、`LspCapabilityIntegrationTests` 与双平台外部进程终止/残留检查。 | Passed | — |
| M6-ACC-007 | Dynamic Binding Lease 过期或来源断连后旧 Binding 立即失效。 | CAP-032, CAP-053 | FaultInjection | All | `DynamicToolTests`、`DeferredToolTests`、Wire server-request cancel/disconnect 与真实动态回调黑盒。 | Passed | — |
| M6-ACC-008 | 工具、Hook、协议扩展冲突隔离单个贡献，不能覆盖内置能力。 | CAP-005, CAP-050 | SecurityTest | All | `CapabilityRuntimeTests`、`CapabilityHookTests`、Plugin 冲突与 Wire 1.0/1.1 Catalog Snapshot。 | Passed | — |
| M6-ACC-009 | Workspace Memory、SourceControl 与 Terminal 均经过统一路径、权限和工具管线。 | CAP-041, CAP-033 | SecurityTest | DualPlatform | 对应 Core 测试与双平台发布目录 Git/Memory/隐藏 Terminal/进程树/Secret Canary。 | Passed | — |
| M6-ACC-010 | 插件卸载或能力热更新只影响下一 Turn，故障插件不阻止运行时清理。 | CAP-032, CAP-050, CAP-052 | FaultInjection | All | Catalog Revision、Turn Snapshot、Plugin/MCP/LSP 故障与停止清理测试。 | Passed | — |

## 9. M7 - Multi-Agent CoWork（10）

| AcceptanceId | Requirement | CapabilityIds | EvidenceType | Platforms | ExpectedEvidence | Status | SupersededBy |
| --- | --- | --- | --- | --- | --- | --- | --- |
| M7-ACC-001 | SubAgent 父子关系、深度、并发、预算和取消传播持久且可恢复。 | CAP-055 | AutomatedTest | All | `DirectSubAgentTests`、`CoWorkBudgetRaceTests`、`MissionRecoveryTests`。 | Passed | — |
| M7-ACC-002 | Team、Member、Mission 使用 SQLite 权威状态，成员会话使用独立 Journal。 | CAP-056 | AutomatedTest | All | `CoWorkPersistenceTests`、`CoWorkServiceTests`、`CoWorkLifecycleTests`。 | Passed | — |
| M7-ACC-003 | MissionTask DAG 拒绝环，依赖满足后才进入 Ready，并持久化 Blocked 原因。 | CAP-057 | AutomatedTest | All | `MissionDagPropertyTests`、`MissionReconcilerTests`。 | Passed | — |
| M7-ACC-004 | Mailbox 支持交接、阻塞、审查、返工和 Artifact 引用，投递/确认幂等。 | CAP-058 | FaultInjection | All | `CoWorkMailboxTests`、`CoWorkDispatchFaultTests`。 | Passed | — |
| M7-ACC-005 | 同一 Member 互斥、全局并发和 Mission 预算在竞态下不超限。 | CAP-055 | PerformanceTest | All | `CoWorkBudgetRaceTests` 的 16 Run 竞态与预算不变量。 | Passed | — |
| M7-ACC-006 | Artifact/Scratchpad 的路径、摘要、权限和孤儿清理不会越出运行时根。 | CAP-059 | SecurityTest | DualPlatform | macOS Symlink、篡改、Secret 与孤儿恢复已通过；Windows Reparse/Junction 真机待验。 | Planned | — |
| M7-ACC-007 | Project/Worktree 隔离正确，Dirty Worktree 不被自动删除或复用。 | CAP-060 | RealPlatformValidation | DualPlatform | macOS Git Worktree/Dirty Retention 已通过；Windows 真机待验。 | Planned | — |
| M7-ACC-008 | Leader 仅在必需任务完成后综合，Review/返工不会提前结束 Mission。 | CAP-057, CAP-060 | AutomatedTest | All | `MissionReviewTests`、`MissionSynthesisTests`。 | Passed | — |
| M7-ACC-009 | Mission 运行中崩溃后可由 Lease/Reconciler 恢复且无重复任务。 | CAP-056, CAP-057, CAP-058 | FaultInjection | All | 256 Task 恢复与完整 `CoWorkDispatchFaultTests` 矩阵。 | Passed | — |
| M7-ACC-010 | 完成通知丢失或重复时，Origin 只接收一次最终结果。 | CAP-060 | FaultInjection | All | `OriginDeliveryTests` 与完成前后故障注入。 | Passed | — |

## 10. M8 - Automations and Scheduler（9）

| AcceptanceId | Requirement | CapabilityIds | EvidenceType | Platforms | ExpectedEvidence | Status | SupersededBy |
| --- | --- | --- | --- | --- | --- | --- | --- |
| M8-ACC-001 | YAML Automation 定义通过 Schema 验证并生成稳定定义版本。 | CAP-061 | ContractSnapshot | All | `AutomationDefinitionTests`、`AutomationSourceTests` 与 1,000 Definition Corpus。 | Passed | — |
| M8-ACC-002 | Fluid 模板在受限上下文执行，失败不创建半成品 Run 或泄漏 Secret。 | CAP-062 | SecurityTest | All | `AutomationTemplateTests`、`AutomationServiceTests` 与 Secret Canary。 | Passed | — |
| M8-ACC-003 | Manual/Cron、时区、DST 和 next-run 计算确定，重启不重复派发。 | CAP-063 | AutomatedTest | DualPlatform | macOS Cron/DST、去重与发布目录已通过；Windows 真机待验。 | Planned | — |
| M8-ACC-004 | Run 冻结定义、权限、Plugin、Skill 和 Tool Snapshot，热更新只影响后续 Run。 | CAP-064 | AutomatedTest | All | `AutomationRuntimeSnapshotTests`、`AutomationServiceTests` 与发布目录热更新。 | Passed | — |
| M8-ACC-005 | 最大并发、单任务互斥、Lease 和 Worktree 分配在竞态下正确。 | CAP-065 | PerformanceTest | All | 64 Start/16 上限、10,000 Run 分页、Lease/Worktree 竞态，SQLite Busy 为 0。 | Passed | — |
| M8-ACC-006 | 结果不明的非幂等工具使 Run 进入 NeedsAttention，不自动重试。 | CAP-035, CAP-066 | FaultInjection | All | `AutomationDispatchTests`、`AutomationInteractionTests` 全副作用窗口。 | Passed | — |
| M8-ACC-007 | 无人值守权限不扩大，Approval 不能通过 Console 自动放行。 | CAP-066 | SecurityTest | All | 权限交集、Approval/UserInput 与无人终端安全测试。 | Passed | — |
| M8-ACC-008 | Running/Pending/Lease 各状态崩溃后恢复且不重复创建 Turn 或 Worktree。 | CAP-065 | FaultInjection | DualPlatform | macOS Dispatch/Reconciler/路径恢复已通过；Windows 强杀与 Reparse/Junction 真机待验。 | Planned | — |
| M8-ACC-009 | NeedsAttention 可由协议恢复，也能按策略取消或超时并重排下一周期。 | CAP-066 | AutomatedTest | All | `AutomationInteractionTests`、`AutomationReconcilerTests` 与 Wire 1.3。 | Passed | — |

## 11. M9 - Gateway and Operations（10）

| AcceptanceId | Requirement | CapabilityIds | EvidenceType | Platforms | ExpectedEvidence | Status | SupersededBy |
| --- | --- | --- | --- | --- | --- | --- | --- |
| M9-ACC-001 | 多 Channel 并发运行且凭据、故障、重连和速率限制互相隔离。 | CAP-067 | FaultInjection | All | Test/Webhook Channel 隔离矩阵。 | Planned | — |
| M9-ACC-002 | 重复入站消息只创建一个 Turn，持久化前不交给 Session Core。 | CAP-068 | FaultInjection | All | 重放/并发重复 Message ID 测试。 | Planned | — |
| M9-ACC-003 | 出站消息发送前进入 Outbox，发送前后崩溃均不静默丢失。 | CAP-069 | FaultInjection | All | Outbox 状态窗口和重试报告。 | Planned | — |
| M9-ACC-004 | 单外部会话消息有序，跨会话可并行，Dead Letter 不阻塞其他分区。 | CAP-069, CAP-070 | PerformanceTest | All | 分区并发与毒消息测试。 | Planned | — |
| M9-ACC-005 | 附件/媒体校验类型、大小、摘要和路径，不信任远端文件名。 | CAP-071 | SecurityTest | DualPlatform | 恶意文件名、Symlink、超限与篡改 Corpus。 | Planned | — |
| M9-ACC-006 | Hub 从用户级注册发现 Workspace，不依赖当前工作目录。 | CAP-013, CAP-072 | RealPlatformValidation | DualPlatform | 两平台不同 CWD 启动与 Workspace 映射。 | Planned | — |
| M9-ACC-007 | Channel Adapter 的启动、停止、断连和重连不遗留后台任务或进程。 | CAP-067 | FaultInjection | DualPlatform | 生命周期与资源泄漏检查。 | Planned | — |
| M9-ACC-008 | Usage/Trace/Insight/Dashboard 查询可用，但不依赖桌面或 Web UI。 | CAP-042, CAP-048, CAP-072 | ContractSnapshot | All | Wire Catalog、CLI 查询和 ImprovementProposal 快照。 | Planned | — |
| M9-ACC-009 | Heartbeat 和所有后台服务随 WorkspaceRuntime 完整注册与清理。 | CAP-016, CAP-077 | FaultInjection | DualPlatform | Stop Timeout、崩溃与残留资源报告。 | Planned | — |
| M9-ACC-010 | 日志、Usage、Tracing 跨 Gateway 到 Session/Tool 可关联且不泄漏 Secret。 | CAP-073, CAP-074, CAP-075, CAP-076 | SecurityTest | All | 端到端 Correlation 与敏感信息扫描。 | Planned | — |

## 12. M10 - OpenCoWork 1.0 Closure（12）

| AcceptanceId | Requirement | CapabilityIds | EvidenceType | Platforms | ExpectedEvidence | Status | SupersededBy |
| --- | --- | --- | --- | --- | --- | --- | --- |
| M10-ACC-001 | CAP-001 至 CAP-078 均有通过证据或明确 Deferred/Removed 结论，无开放缺口。 | CAP-001-CAP-078 | ContractSnapshot | All | 能力台账关闭报告与证据反向链接。 | Planned | — |
| M10-ACC-002 | 公共 Wire 方法、DTO、错误码、配置 Schema 和默认值完成冻结审查。 | CAP-005, CAP-008, CAP-043, CAP-045 | ContractSnapshot | All | 1.0 Golden Snapshot 与 Breaking Change 审查。 | Planned | — |
| M10-ACC-003 | SQLite 至少两个旧 Schema 可迁移，失败会恢复备份且状态可诊断。 | CAP-017, CAP-021 | MigrationTest | DualPlatform | 旧数据库 Corpus、Backup/Restore 与两平台日志。 | Planned | — |
| M10-ACC-004 | ThreadJournal 至少两个旧 Schema 可 Upcast/回放，投影重建与升级原子切换正确。 | CAP-017, CAP-020, CAP-021, CAP-039 | MigrationTest | DualPlatform | 旧 Journal Corpus、Checksum 与重建快照。 | Planned | — |
| M10-ACC-005 | Archive/Delete/Fork/Rollback 在崩溃、升级和恢复组合下保持契约。 | CAP-024 | FaultInjection | DualPlatform | 组合故障矩阵与 Reconciler 结果。 | Planned | — |
| M10-ACC-006 | Secret、路径、插件、Hook、MCP、工具、媒体和 Worktree 通过完整安全审计。 | CAP-010, CAP-018, CAP-027, CAP-034, CAP-041, CAP-059, CAP-071, CAP-074 | SecurityTest | DualPlatform | Threat Matrix、Canary、越界 Corpus 和修复证据。 | Planned | — |
| M10-ACC-007 | CLI、AppServer、ACP、Gateway 从初始化到恢复完成端到端真实模型验收。 | CAP-019, CAP-025, CAP-043, CAP-047, CAP-067 | RealPlatformValidation | DualPlatform | Windows PC 与 M4 Mac mini 的 E2E 记录。 | Planned | — |
| M10-ACC-008 | Provider、Plugin、MCP 和 LSP 兼容矩阵覆盖支持版本、失败隔离和升级。 | CAP-026, CAP-050, CAP-052 | RealPlatformValidation | DualPlatform | 兼容矩阵报告与供应链摘要。 | Planned | — |
| M10-ACC-009 | 性能、并发、长时间运行和资源清理满足发布预算且无 P0/P1。 | CAP-022, CAP-055, CAP-065, CAP-070, CAP-077 | PerformanceTest | DualPlatform | Soak、并发、内存/句柄/进程报告。 | Planned | — |
| M10-ACC-010 | `win-x64` 在干净 Windows 机器完成安装、升级、卸载和真实模型冒烟。 | CAP-011 | RealPlatformValidation | win-x64 | 签名产物、安装日志、卸载残留与冒烟报告。 | Planned | — |
| M10-ACC-011 | `osx-arm64` 在 M4 Mac mini 完成安装、升级、卸载和真实模型冒烟。 | CAP-011 | RealPlatformValidation | osx-arm64 | 签名/Notarization 产物、安装日志和冒烟报告。 | Planned | — |
| M10-ACC-012 | 用户、协议、插件文档、Release Notes、SBOM、校验和与诊断说明齐全。 | CAP-051, CAP-072, CAP-078 | ManualValidation | All | 发布包清单、文档链接、SBOM 与校验和验证。 | Planned | — |

## 13. 数量与关闭规则

| Slice | Acceptance 数量 | 当前状态 |
| --- | ---: | --- |
| M0 | 8 | Passed |
| M1 | 8 | Passed |
| M2 | 10 | Passed |
| M3 | 8 | Passed |
| M4 | 10 | Passed |
| M5 | 9 | Passed |
| M6 | 10 | Passed |
| M7 | 10 | 8 Passed / 2 Planned |
| M8 | 9 | 7 Passed / 2 Planned |
| M9 | 10 | Planned |
| M10 | 12 | Planned |
| **Total** | **104** | **78 Passed / 26 Planned** |

每个 Slice 标记 Done 前必须：

1. 将该 Slice 的全部 Planned 改为 Passed，或以用户明确接受并有台账的 Deferred、
   Superseded 明确处理；
2. 链接实际证据；
3. 执行此前 Slice 的累计回归；
4. 确认没有 P0/P1 缺陷；
5. 对 `DualPlatform` 项提供两台真实平台的独立证据，或明确记录已接受的延期平台；
   M10 不允许保留该延期；
6. 更新能力台账、里程碑 Checklist 和交付归档。
