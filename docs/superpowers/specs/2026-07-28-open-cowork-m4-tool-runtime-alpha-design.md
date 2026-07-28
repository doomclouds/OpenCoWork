# OpenCoWork M4 Tool Runtime Alpha 设计规格

## 文档状态

- 状态：设计已冻结
- 日期：2026-07-28
- 所属里程碑：OpenCoWork Runtime 1.0 / M4
- 目标框架：.NET 10
- 路线规格：
  [OpenCoWork Runtime 1.0 路线规格](2026-07-25-open-cowork-runtime-1-0-roadmap.md)
- M0 冻结契约：
  [OpenCoWork M0 Contract Freeze](2026-07-25-open-cowork-m0-contract-freeze-design.md)
- M3 前置规格：
  [OpenCoWork M3 Agent Runtime Alpha](2026-07-27-open-cowork-m3-agent-runtime-alpha-design.md)
- 能力台账：
  [OpenCoWork M0 能力台账](2026-07-25-open-cowork-m0-capability-ledger.md)
- 验收目录：
  [OpenCoWork M0-M10 验收目录](2026-07-25-open-cowork-m0-acceptance-catalog.md)

本文已于 2026-07-28 读取并核对仓库根目录的
`DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md`。该文件只作为
能力边界、状态语义、安全顺序和验收场景的本机证据基线；本文不兼容 DotCraft
的 `.craft`、程序集或私有实现。

本文已按头脑风暴逐项确认并完成全文一致性审查。本文中的目标、设计、数据流、
证据核对和验收映射共同构成 M4 冻结设计；实现计划不得重新解释已确认边界。

## 1. 目标与边界

M4 在 M3 Agent Runtime 上加入模型工具调用，使模型能够通过固定安全管线产生
受控、可审批、可取消、可恢复且不会被重复提交的副作用。

继承边界：

- 继续复用 `ISessionExecutor → SessionService → ThreadJournal` 唯一执行与提交链；
- `AgentFactory` 按 `TurnSnapshot.EffectiveAgentMode` 为当前 Turn 冻结工具视图；
- 审批继续复用 M2 已有的等待、持久化、恢复、超时和取消机制；
- `ThreadJournal` 继续作为 Tool Call、Tool Result、审批和调用终态的权威事实源；
- Plan 模式的只读限制必须由工具快照和调用管线强制执行，不能依赖 Prompt 自觉；
- M4 不创建第二套 Thread、Turn、Item、审批或工具调用状态机。

M4 包含：

- Tool Definition、Runtime Binding、Registration 与来源身份；
- `EffectiveToolSnapshot` 和 Provider 工具名双向映射；
- 固定顺序的 `ToolInvocationPipeline`；
- Authority、Schema、Policy、Hook、Approval、Timeout、Cancellation、结果归一化和审计；
- 稳定工具错误码、结果不明保护和副作用幂等；
- Agent / Plan 工具曝光差异；
- 最小 File、Shell、Web 与确定性测试工具。

M4 不包含：

- MCP、插件、动态工具和 Deferred Loading；
- AppServer、ACP 和外部客户端工具注册；
- 新增生产程序集或独立工具状态存储；
- 内置 Node REPL、后台终端、Sandbox、LSP 或 SourceControl 工具。

完成信号沿用 `M4-ACC-001` 至 `M4-ACC-010`，不得以契约存在代替固定管线、
故障注入、双平台真实工具或副作用不重复证据。

## 2. 已确认设计

### 2.1 单一工具执行路径与契约归属

M4 只扩展现有 Agent / Session 链，不建立平行工具运行时：

```text
AgentFactory 冻结 EffectiveToolSnapshot
→ Provider 产生 Tool Call
→ ToolInvocationPipeline 执行固定安全管线
→ ISessionExecutionSink 提交执行意图
→ SessionService 按 Journal / Projection / Event 顺序提交
→ 归一化 Tool Result 回注同一 Turn 的 Provider 对话
```

程序集职责固定为：

- `OpenCoWork.Abstractions` 只承载确实需要跨程序集使用的稳定工具契约：
  工具身份、Definition / Registration 元数据、不可变工具快照、调用/结果/错误 DTO
  和 `IToolInvocationPipeline`；
- `OpenCoWork.Core` 承载 Snapshot Builder、Provider 名称投影、Authority、Policy、
  Approval、Hook、固定管线实现和 File / Shell / Web 内置 Runtime Binding；
- M4 不新增 `OpenCoWork.Tools` 程序集；
- Builder、Projector、Authority、Policy 和内置 Binding 不因单一实现预建公共接口；
- M4 不为 M6 的插件、MCP 或动态工具预建注册宿主、热更新状态机或兼容适配层。

`OpenCoWork.Abstractions` 中的契约只表达跨程序集必须共享的身份、不可变输入输出和
调用边界，不包含文件系统、进程、HTTP、数据库、DI 容器或 Core 实现类型。
`OpenCoWork.Core` 中的所有调用入口最终都必须经过同一个
`IToolInvocationPipeline`，内置工具不得以直接调用 Runtime Binding 的方式旁路。

本决策不提前冻结具体字段、枚举全集或 JSON Schema 表示；这些内容在后续对应设计
决策确认后补入本文。

### 2.2 工具身份与名称分离

工具的权威身份、规范名称和 Provider 可见名称是三个不同概念：

- `ToolDefinitionId` 由 `SourceKind`、`SourceId` 和 `SourceToolId` 组成，唯一标识
  一个语义工具定义；
- `ToolName` 由 `namespace` 和 `name` 组成，是 OpenCoWork 内部使用的规范名称；
- Provider 可见扁平名只存在于当前 Turn 的 `EffectiveToolSnapshot`，不是工具身份。

固定语义：

- 工具改名不改变 `ToolDefinitionId`，也不得创建第二份语义身份；
- 不允许使用规范名称、Provider 扁平名或集合下标代替 `ToolDefinitionId`；
- Snapshot 同时保存规范名称到 Provider 扁平名、Provider 扁平名到规范名称的
  双向映射；调用时不得通过拆分字符串反推名称；
- 重复 `ToolDefinitionId`、重复规范名称或 Provider 名称冲突都不得采用
  “后注册覆盖前注册”；所有冲突项均从有效工具集中隔离并产生确定性诊断；
- 冲突判断和快照结果不得依赖工具源枚举顺序或注册顺序。

M4 只允许 `CoreNative` 来源产生可执行注册。`PluginNative`、`Mcp` 和
`RuntimeDynamic` 只作为已冻结的来源身份值保留；保留枚举值不代表 M4 可以加载、
连接或执行这些来源。M6 必须复用相同身份模型接入对应生命周期，不能创建另一套
插件或 MCP 工具身份。

本决策尚未冻结 `ToolName` 字符约束、Provider 扁平名投影算法、长度上限或冲突
后缀算法。

### 2.3 Definition、Binding 与 Registration 职责

工具语义、当前执行能力和有效注册严格分离：

- `ToolDefinition` 是不可变语义，包含权威身份、规范名称、描述、输入 Schema 和
  副作用分类，不包含执行委托或运行时可用性；
- `ToolRuntimeBinding` 是 Core 内存中的当前执行能力，包含独立
  `RuntimeBindingId`、Executor、Availability、Lease 和默认超时；
- `ToolRegistration` 是 Definition 与 `RuntimeBindingId` 的不可变关联，并声明
  Exposure 和 Audience；Registration 不复制或包装 Executor；
- `EffectiveToolSnapshot` 冻结 Definition、Registration 元数据和
  `RuntimeBindingId`，不保存可执行委托。

调用时，`ToolInvocationPipeline` 必须使用 Snapshot 中的
`RuntimeBindingId` 精确解析 Binding，并在执行前重新检查 Availability 和 Lease。
如果 Binding 不存在、不可用或 Lease 失效，则返回对应稳定错误；不得回退到同名、
同 Definition 或后来注册的新 Binding。

`RuntimeBindingId` 不得被不同执行能力复用。M4 内置 Binding 使用确定性 ID，
进程重启后只允许重建同一个内置执行能力；实现、来源或权限语义改变时必须使用新的
Binding ID。后续 M6 的动态生命周期不得借用内置确定性 ID。

M4 不为这些 Core 内部对象增加 Repository、Provider、Factory 或事件总线。
内置工具在 WorkspaceRuntime 启动时完成一次注册；后续是否支持来源热更新、外部
Executor 注册和断连回收由 M6 设计，不在 M4 预建。

本决策尚未冻结副作用分类、Exposure、Audience、Availability、Lease 的具体枚举和
状态转换，也未冻结 Snapshot 的持久化格式。

### 2.4 EffectiveToolSnapshot 的冻结与恢复

`AgentFactory` 在 Turn 首次执行时只构建一次 `EffectiveToolSnapshot`。Snapshot
至少包含：

- `SchemaVersion` 和 `EffectiveAgentMode`；
- 按确定顺序排列的 Definition、Registration 元数据和 `RuntimeBindingId`；
- 规范名称与 Provider 扁平名的双向映射；
- 被隔离或排除工具的确定性诊断；
- `SnapshotSha256`。

`SnapshotSha256` 是 Snapshot 规范化 JSON 的小写 SHA-256；计算输入不包含
`SnapshotSha256` 自身。规范化必须固定属性顺序、集合顺序和 UTF-8 编码，相同输入
必须得到字节一致的 Snapshot 和摘要。

M4 直接扩展现有 `AgentInvocationSnapshot` 保存完整
`EffectiveToolSnapshot`，并继续通过现有 `RecordAgentInvocationSnapshotIntent`
在任何 Provider 调用前提交到 `ThreadJournal`。不新增独立 Snapshot Journal
事件、Snapshot 仓库、文件缓存或 SQLite 权威表。

Compaction Provider 调用继续使用空工具集；正式回答和后续工具循环始终使用
`AgentInvocationSnapshot` 中冻结的同一工具快照。

如果 Turn 因审批等待、取消竞争或进程重启而恢复，Executor 必须从 Journal 中的
`AgentInvocationSnapshot` 恢复原 Snapshot，禁止根据当前注册集合重新构建。当前
运行时仍需精确解析原 `RuntimeBindingId` 并执行实时 Lease 与安全检查：

- 原 Binding 缺失或失效时拒绝调用，不得换绑；
- 当前 Authority、Policy 或 Workspace 安全边界比快照更严格时，只允许收窄；
- 当前配置比快照更宽松时，原 Snapshot 继续作为权限上限，不得提权。

工具注册、模式或配置变化只影响后续 Turn。进行中的 Turn、等待恢复的 Turn 和同一
Turn 内的全部 Provider 工具循环都继续使用原 Snapshot。

完整 Snapshot 的大小限制、单工具 Schema 限制和 Journal Schema 版本在后续数据
边界决策中冻结。

### 2.5 规范名称与 Provider 名称投影

`ToolName.Namespace` 和 `ToolName.Name` 必须分别匹配
`^[a-z][a-z0-9_]{0,63}$`。工具注册时直接拒绝 Unicode、空值、大小写别名、连字符、
点号和其他字符，不执行大小写转换、Unicode 规范化或字符替换。

OpenCoWork 规范名称固定为：

```text
namespace.name
```

名称比较使用 Ordinal。Provider 基础名固定为：

```text
namespace__name
```

投影算法固定为：

1. 先隔离重复规范名称；
2. 基础名不超过 64 ASCII 字节且在候选集合中唯一时，直接使用基础名；
3. 基础名超长或多个不同规范名称产生相同基础名时，所有相关项统一使用：

   ```text
   基础名的前 min(30, length) 个字符
   + "__"
   + SHA-256(规范名称 UTF-8) 的前 32 个小写十六进制字符
   ```

4. Hash 投影后仍冲突时，隔离全部相关项并产生确定性诊断。

由于输入只允许 ASCII，字符长度与 UTF-8 字节长度一致。Hash 只用于确定性缩短和
消歧，不改变 `ToolDefinitionId`，也不为冲突项建立优先级。算法不动态增加后缀、
不读取注册顺序，也不提供配置开关。

调用反向映射只能查询 `EffectiveToolSnapshot` 中冻结的字典；任何代码都不得通过
拆分双下划线、重新运行投影算法或猜测 Hash 前缀来恢复规范名称。

### 2.6 Provider Tool Call 串行循环

M4 扩展 `ChatCompletionRequest`，仅在 `Purpose=Response` 时携带
`EffectiveToolSnapshot` 中投影后的工具定义；`Purpose=Compaction` 始终使用空
工具集。Provider 消息契约同步增加 Assistant Tool Call 和 Tool Result 角色数据，
不把工具结果伪装成普通 User / Assistant 文本。

Provider 流新增 Tool Call Delta，至少携带 Provider Call Index、Tool Call ID、
Provider 工具名和 Arguments Delta。Executor 按 Index 与 Tool Call ID 累积调用，
并遵守以下提交边界：

- 必须完整读取到 `[DONE]`；
- Finish Reason 必须为 `ToolCall`；
- 每个 Tool Call ID、工具名和完整 JSON Arguments 必须存在且通过 Frame
  Preflight；
- 只有上述条件全部满足后才能进入 `ToolInvocationPipeline`；
- EOF、取消、解析失败、参数超限或不完整 Tool Call 都不得执行工具。

Frame Preflight 在执行任何工具前遍历完整 Frame，统一完成 JSON 语法、大小、深度、
Canonical JSON 和机密扫描。JSON 损坏、超限或 Frame 不完整时，整个 Provider
步骤以 `provider.invalidStream` 失败，不提交 ToolCall Item 或 Tool Invocation。
机密命中不保留原值：对应参数先替换为统一脱敏标记，并携带
`SensitiveInputDetected=true` 进入 Pipeline；只有受影响的调用在 Input Schema
阶段被拒绝，Frame 中其余调用仍按顺序处理。

同一 Provider 步骤返回多个 Tool Call 时，严格按 Provider Index 顺序串行执行。
M4 不并行执行副作用。全部 Tool Result 按原 Tool Call ID 回注后，再使用同一个
`EffectiveToolSnapshot` 调用 Provider，直到得到正常终答。

每个 Provider 步骤沿用 M3 的首个可见增量重试边界和最多两次重试。整个 Turn
继续受 30 分钟 Invocation Deadline 约束。一旦当前步骤已经提交 Content、
Reasoning 或 Tool Call，或者当前 Turn 已提交任何工具副作用，对应步骤不得通过
重试重复可见输出或副作用。

M4 不支持 Provider 并行工具执行、后台工具、跨 Turn 工具任务或脱离
`AgentRuntimeExecutor` 的工具循环。

不采用每 Turn Tool Call 总数上限。同一 Provider 响应可以批量返回多个 Tool
Call，仍逐个审计和串行执行。每 Turn 最多允许 64 个已完成的 Tool Call 轮次；
一个轮次定义为一次以 `ToolCall` Finish Reason 完整结束的 Provider 步骤，不按
该步骤包含的 Tool Call 数量重复计数。

达到 64 轮后以 `tool.iterationLimitExceeded` 结束当前 Turn，不创建伪造的 Tool
Call 或 ToolResult；已完成的副作用、Tool Call 和 Tool Result 继续保留在 Journal，
用户可以在下一 Turn 继续。M4 不为该上限增加配置项、Thread 覆盖或 Provider
覆盖；30 分钟 Invocation Deadline、上下文窗口和各层输出大小限制继续共同约束
单个 Turn。

### 2.7 副作用分类与 Agent / Plan 工具面

`ToolDefinition` 使用可组合的 `ToolEffect` 标记工具可能触及的最大能力：

- `None`：确定性纯计算，不读取或修改外部状态；
- `WorkspaceRead`：只读取受工作区边界保护的本地内容；
- `WorkspaceWrite`：可能创建、修改、移动或删除工作区内容；
- `ProcessExecution`：启动或控制本机进程；
- `NetworkRead`：只执行受限的网络读取；
- `ExternalMutation`：可能修改工作区之外的系统或远端状态。

Effect 是静态权限上限，不是运行后的行为描述。Definition 不得根据参数、Prompt
或模型声明把高风险工具临时降级。一个工具同时具有多种能力时必须声明全部能力；
可以安全拆分时优先拆成独立工具，例如 `file.read_text` 与 `file.write_text`。

Shell 无论命令文本看起来是否只读，至少声明 `ProcessExecution`，并按其可能触及的
文件、网络和外部状态使用保守 Effect 集合；M4 不解析 Shell 文本以推断更低风险。
File 写入工具声明 `WorkspaceWrite`。M4 Web 只允许无认证、无请求体的 GET / HEAD，
声明 `NetworkRead`；POST、PUT、PATCH、DELETE、上传和自定义认证不进入 M4。

Snapshot Builder 按 `EffectiveAgentMode` 形成工具视图：

- Agent 模式可以包含当前 Authority 允许的全部 M4 工具；
- Plan 模式只允许 Effect 集合是 `None`、`WorkspaceRead` 和 `NetworkRead` 子集的
  工具；
- `WorkspaceWrite`、`ProcessExecution` 或 `ExternalMutation` 任一存在时，Plan
  模式必须从 Snapshot 排除该工具。

Plan 模式允许只读 Web 是为了支持方案调研，但 `NetworkRead` 仍需经过 Authority、
Policy 和可能的 Approval，不代表自动放行。`ToolInvocationPipeline` 在执行前再次
校验 Snapshot 中的 `EffectiveAgentMode` 与 Definition Effect；Provider 名称、
模型内容、Hook 和 Approval 都不能扩大模式上限。

M4-ACC-008 中的“网络副作用工具”按静态 Effect 映射为
`ExternalMutation`，不包含只读的 `NetworkRead`。需要认证、请求体、GET / HEAD
以外方法或可能修改远端状态的工具必须声明 `ExternalMutation`，因此不能进入 Plan
Snapshot；M4 `web.fetch` 不具备这些输入能力。

### 2.8 固定调用管线与审计提交

`IToolInvocationPipeline` 的最小调用边界固定为：

```text
InvokeAsync(
    ToolInvocationContext context,
    ISessionExecutionSink sink,
    CancellationToken cancellationToken)
```

M4 直接复用 `ISessionExecutionSink` 提交工具执行意图，不新增
ToolInvocationRecorder、工具事件总线或第二套 Session Sink。Pipeline 是
Agent Runtime 内唯一允许提交 Tool Invocation Started、WaitingApproval 和
Terminal 意图的组件。

安全阶段顺序固定为：

```text
Snapshot Lookup
→ ToolInvocation Started
→ Audience / Exposure / Mode
→ Binding Availability / Lease
→ Authority
→ Input Schema
→ Policy
→ PreToolUse Hook
→ Approval
→ Timeout-linked Invoke
→ Result Normalize
→ ToolInvocation Terminal
→ Terminal Hook
```

任何阶段都不得旁路、重排或合并到 Runtime Binding。Snapshot Lookup 即使失败，
也必须使用原 Provider Tool Call ID 和 Provider 工具名提交 Started，随后提交
NotFound Terminal；此时解析后的 Definition / Binding 身份为空。Audience、
Exposure、Mode、Lease、Authority、Schema、Policy、Hook 和 Approval 的前置拒绝
同样必须形成 Started 与单一 Terminal。

Tool Invocation 终态固定为：

- `Completed`
- `Rejected`
- `Failed`
- `Cancelled`
- `TimedOut`
- `OutcomeUnknown`

Started、WaitingApproval 和 Terminal 均由 `SessionService` 按现有
ThreadJournal → 内存聚合 → SQLite 投影 → SessionEvent 顺序提交。Journal Flush
继续是提交点；SQLite 只保存可重建查询投影，不成为工具审计事实源。

PreToolUse Hook 只能拒绝、要求审批或收窄超时，不得替换工具、修改参数、扩大
Audience / Effect / Authority 或取消已有审批要求。没有 Hook 时该阶段是确定性
No-op。Terminal Hook 只在 Terminal 已提交后运行；异常只形成脱敏诊断，不得改写、
删除或追加第二个工具终态。

Approval 直接复用 M2 的 `WaitForInteractionIntent`、Pending Interaction、短期
Checkpoint、超时、取消和恢复机制。Pipeline 不创建独立审批存储或等待任务注册表。

### 2.9 Authority 交集与默认审批策略

每种 `ToolEffect` 的策略只有三个有序值：

```text
Deny < RequireApproval < Allow
```

有效决策取以下来源中最严格的值：

```text
内置默认
∩ 用户配置
∩ 工作区请求
∩ Thread Authority
∩ EffectiveAgentMode
∩ EffectiveToolSnapshot 权限上限
∩ 当前运行时安全限制
```

工作区配置只能收窄用户配置。Thread、Snapshot、Hook、Approval、模型输出或
Provider Tool Call 都不能扩大用户级上限。恢复旧 Turn 时，冻结 Snapshot 与当前
运行时限制继续取交集，只允许收窄。

M4 内置默认固定为：

| Effect | 默认策略 |
| --- | --- |
| `None` | `Allow` |
| `WorkspaceRead` | `Allow` |
| `NetworkRead` | `RequireApproval` |
| `WorkspaceWrite` | `RequireApproval` |
| `ProcessExecution` | `RequireApproval` |
| `ExternalMutation` | `RequireApproval` |

用户可以在用户级配置中显式将 `NetworkRead`、`WorkspaceWrite` 或
`ProcessExecution` 调整为 `Allow` 或 `Deny`。工作区配置只能维持或收窄该选择，
不能把用户的 `Deny` 或 `RequireApproval` 提升为 `Allow`。M4 不增加按工具名、
命令文本、域名或路径表达的策略语言。

`ExternalMutation` 在 M4 只能由用户配置维持为 `RequireApproval` 或收窄为
`Deny`，不得提升为 `Allow`。因此任何声明该 Effect 的工具都必须逐次审批；
Shell 因无法在无沙箱条件下排除外部副作用，必须声明 `ExternalMutation`，并绑定
完整命令和参数发起审批。M4 不解析 Shell 文本来猜测副作用。

Approval 只处理 `RequireApproval`，不能覆盖 `Deny`。审批请求绑定精确
Thread ID、Turn ID、Tool Invocation ID、Tool Definition ID、Snapshot SHA-256
和参数摘要；批准只对该次 Tool Invocation 有效。M4 不支持永久授权、整 Turn
授权、同名工具授权或参数通配授权。

模型调用还必须同时满足 Registration 的 `Audience=Model` 和
`Exposure=Direct`。`Hidden`、Host 或 App 工具即使 Provider 名称匹配也必须拒绝。
M4 不提供 Host / App 调用入口。

M4 只有 `CoreNative` 来源，不实现 `PendingTrust` 或
`~/.opencowork/trust/decisions.json`。该 Trust Store 只在 M6 接入 Plugin、Hook、
MCP、LSP 和外部命令来源时实现，并继续作为 Authority 的收窄输入。

### 2.10 JSON Schema 与参数校验

M4 使用 `JsonSchema.Net` 执行工具输入校验，不手写 JSON Schema 子集，也不增加
OpenCoWork 自有 Schema 抽象或单实现包装接口。它是 M4 唯一新增的 Schema
依赖，具体稳定版本在实现时通过中央包管理固定。

工具输入 Schema 统一采用 JSON Schema Draft 2020-12：

- Schema 未声明 `$schema` 时仍按 Draft 2020-12 构建；
- Schema 声明其他 Dialect、未知 Vocabulary 或自定义 Keyword 时注册失败；
- 只允许当前 Schema 文档内的 Fragment `$ref`，例如 `#/$defs/path`；
- 外部 URI、文件、网络和全局 Registry 引用一律不解析；
- 不注册自定义 Format，也不启用 Format Assertion；`format` 不能作为安全判断；
- 根 Schema 必须声明 `type: "object"`；
- Core 内置工具必须显式声明 `additionalProperties: false`。

Definition 进入 EffectiveToolSnapshot 前必须完成 Schema 构建与校验。无效、
不受支持或无法解析引用的 Schema 使对应 Registration 被隔离，并形成确定性
诊断；它不得暴露给 Provider，也不得等到首次调用时才失败。

Provider Tool Call 的参数先由 2.6 Frame Preflight 使用 `System.Text.Json`
解析、规范化和脱敏，再由固定 Pipeline 的 Input Schema 阶段一次性校验。
`SensitiveInputDetected=true` 必须优先提交
`Rejected(tool.sensitiveInputRejected)`；其余 Schema 校验失败提交
`Rejected(tool.inputInvalid)`。两者均不得进入 Policy、Hook、Approval 或
Invoke。校验输出使用最小 Flag 结果；Journal 只保存稳定错误码、脱敏后的实例位置
与 Schema 位置，不持久化完整验证树。

EffectiveToolSnapshot 摘要和审批参数摘要继续复用现有 Canonical JSON
写入规则与 SHA-256，不引入第二套 JSON DOM、序列化器或规范化依赖。

### 2.11 审批恢复、去重与重放安全

`ToolDefinition` 增加 `ReplaySafety`，取值仅为 `Unsafe` 或 `Safe`，默认
`Unsafe`。它只回答“结果未知后是否允许使用完全相同的参数再调用一次”，不能由
Runtime Binding、Registration、Hook、Approval 或 Provider 临时提升。

每个完整的 Provider Tool Call 首次进入 Pipeline 时生成一个内部
Tool Invocation ID，并在任何 Binding 调用前将 Started 写入 Journal。Started
固定记录 Turn ID、Provider Tool Call ID、Provider 工具名、解析后的 Definition /
Binding ID、EffectiveToolSnapshot SHA-256 和 Canonical 参数摘要。

Provider Tool Call ID 在同一 Turn 内执行以下确定性去重：

- ID、Provider 工具名和参数摘要均相同且已有 Terminal：直接回放已记录的规范化
  Tool Result，不创建第二次副作用；
- 三者相同但仍处于 WaitingApproval：继续引用原 Interaction，不创建第二个审批；
- 三者相同且存在未完成调用：进入下述恢复规则，不并发调用；
- ID 相同但工具名或参数摘要不同：以新的内部 Tool Invocation ID 记录 Started 和
  `Rejected(tool.callIdConflict)`，原映射与原终态保持不变。

Pipeline 每次把调用权交给 Runtime Binding 前，先在同一 Tool Invocation 下持久化
递增的 Attempt Started。它不是第二套状态机，只用于区分“尚未调用”与“可能已经
产生副作用”，并为崩溃恢复提供重放次数证据。

需要审批时继续使用 M2 的 `WaitForInteractionIntent` 与
`SessionExecutionCheckpoint`。M4 Checkpoint 只保存版本化恢复游标：
Agent Invocation ID、EffectiveToolSnapshot SHA-256、Provider Round、Tool Call
顺序、Tool Invocation ID、参数摘要和下一 Pipeline 阶段。参数、Definition、
审批请求和已完成 Tool Result 仍以 Journal 事实为准，不复制进 Checkpoint。

审批响应必须先由 `ResolveInteractionAsync` 持久化，再恢复同一个 Tool
Invocation。批准后从 Approval 边界继续，并在 Binding 调用前重新检查当前
Availability、Lease 与只能收窄的运行时安全限制；拒绝则提交单一 Rejected
Terminal。重复提交同一审批响应继续复用 M2 的 Command Idempotency，不增加审批
存储或等待任务表。

进程恢复或宿主中断时按 Journal 中最后一个确定状态处理：

- 已有 Terminal：只回放结果；
- WaitingApproval：按 M2 Checkpoint 等待或恢复；
- Started 但没有 Attempt Started：从未完成的安全阶段继续；
- Attempt Started 但没有 Terminal，且 `ReplaySafety=Safe`：使用同一 Tool
  Invocation ID、同一 Snapshot 和同一参数自动重放一次；
- Attempt Started 但没有 Terminal，且 `ReplaySafety=Unsafe`：直接提交
  `OutcomeUnknown(tool.outcomeUnknown)`，绝不自动调用；
- Safe 重放再次中断：同样提交 OutcomeUnknown，不循环重试。

没有有效工具恢复游标的 Running Turn 继续沿用 M2 的
`RuntimeInterrupted` 失败语义。M4 不增加独立去重数据库、恢复队列、后台重放器或
可配置重试策略。

### 2.12 结果规范化与机密信息边界

Runtime Binding 只返回一个最小 `ToolBindingResult`：

```text
Success(JsonElement Output)
或
Failure(SessionError Error)
```

可预期的文件、进程、网络或业务失败必须返回 Failure，不用异常表达正常控制流。
逃逸异常由 Pipeline 捕获并规范化为
`Failed(tool.executionFailed)`；异常类型、堆栈、原始系统错误和未脱敏消息不进入
Journal、SessionEvent 或 Provider 上下文。

Pipeline 是唯一结果规范化入口，并生成权威 `ToolResultSnapshot`。Snapshot 至少
固定包含：

- Tool Invocation ID 与 Provider Tool Call ID；
- Terminal Status；
- Completed 时的结构化 JSON Output，或非 Completed 时的稳定 `SessionError`；
- `IsTruncated`、规范化前字节数与规范化结果 SHA-256；
- Attempt Count。

Output 与 Error 必须先经过 Secret Redaction，再执行大小限制和 Canonical JSON
写入。SHA-256 针对“已脱敏、截断前”的 Canonical JSON 计算；这样既能验证截断
来源，又不会为原始机密内容建立额外持久化指纹。原始 Binding 输出只允许在
Pipeline 当前调用栈内短暂存在。

M4 复用现有 `SecretRedactor` 的已知值与敏感字段规则，只为它补充 JSON
结构化遍历能力；不得创建第二个 Tool 专用脱敏器。以下所有消费者使用同一个
ToolResultSnapshot，不得二次解释或各自重新格式化：

```text
ThreadJournal
→ SQLite Projection
→ SessionEvent
→ Provider Tool Message
→ Duplicate / Recovery Replay
```

Provider Tool Message 使用真实的 `role=tool` 和原 Provider Tool Call ID，
Content 是从 ToolResultSnapshot 生成的 Canonical JSON Envelope。相同终态的重复
回放必须产生逐字节相同的 Content。

Frame Preflight 必须检测 Canonical 参数是否包含 Effective Config 或冻结 Provider
Credential 中的已知机密值，或使用敏感字段承载值。命中时只持久化替换为统一标记
后的 Canonical 参数、参数摘要和 `SensitiveInputDetected` 标志；参数 SHA-256 同样
针对脱敏后的 Canonical JSON 计算，不为原始机密建立持久化指纹。受影响的调用仍
形成 Started 和 `Rejected(tool.sensitiveInputRejected)`，但不调用 Binding；
未命中的调用不受同一 Frame 中其他调用影响。

`OutcomeUnknown`、`Rejected`、`Failed`、`Cancelled` 和 `TimedOut` 不伪造
Output，只返回稳定 Error Envelope。尤其 `OutcomeUnknown` 明确表示副作用可能
已经发生，Provider 与恢复逻辑都不得把它解释为“尚未执行”。

### 2.13 大小、超时与取消边界

M4 使用固定常量，不增加 `ToolLimitsConfig` 或按工具可调的限制层：

| 边界 | M4 固定值 |
| --- | ---: |
| 单个 Tool Schema UTF-8 大小 | 64 KiB |
| EffectiveToolSnapshot Canonical JSON 总量 | 1 MiB |
| Tool 参数 Canonical JSON | 512 KiB |
| JSON 最大深度 | 64 |
| Binding 脱敏后 Canonical JSON 结果 | 1 MiB |
| Journal / Provider Tool Result Envelope | 256 KiB |
| Turn 累计活动执行预算 | 30 分钟 |
| File 默认 Tool Timeout | 30 秒 |
| Shell 默认 Tool Timeout | 10 分钟 |
| Web 默认 Tool Timeout | 2 分钟 |

大小统一按 UTF-8 字节计算。超过 Schema、Snapshot 或参数边界时，在分配不受控
对象或调用 Binding 前稳定拒绝。M4 Core Binding 也必须边读边执行硬限制，不能先
把无界文件、stdout、stderr 或 HTTP Body 全量缓冲后再交给 Pipeline 检查。

脱敏后的完整 Canonical Tool Output 不超过 256 KiB 时原样进入
ToolResultSnapshot。大于 256 KiB 且不超过 1 MiB 时：

- 对完整脱敏结果计算 SHA-256 并记录原 UTF-8 字节数；
- 使用 UTF-8 安全的头尾预览替换 Output；
- 头尾按可用预览预算约 `3:1` 分配，并根据最终 Envelope 的实际序列化大小收缩；
- 设置 `IsTruncated=true`，保证完整 Envelope 最终不超过 256 KiB。

超过 1 MiB 时，File 停止读取、Web 中止响应、Shell 终止进程树，并提交
`Failed(tool.outputLimitExceeded)`。M4 不写 Tool Result Spill 文件，不把超限
内容转移到临时目录或旁路审计路径。

Agent Runtime 继续沿用 30 分钟总活动执行预算。该预算在 Provider、Compaction、
Pipeline 和所有 Tool Invocation 间共享，并在 Checkpoint / Resume 后继承剩余额度，
不得因恢复而重置。WaitingApproval 的等待时间不消耗活动执行预算，由现有
Interaction `TimeoutAt` 独立约束。

每次工具调用的有效取消令牌固定为：

```text
Turn Cancellation
+ Turn Remaining Budget
+ Binding Default Timeout
+ Hook Narrowed Timeout
→ Linked CancellationToken
```

显式 Turn Cancellation 形成 `Cancelled`；Tool 或 Turn Deadline 形成
`TimedOut(tool.timeout)`。如果取消与超时同时可见，显式 Turn Cancellation 优先。
两者都不承诺回滚 Binding 已经完成的部分副作用，且 `ReplaySafety=Unsafe` 时不得
自动重试。

File 与 Web 使用 .NET 原生异步取消。Shell 必须持续异步排空 stdout / stderr，
并在取消、超时或输出超限时首先使用
`Process.Kill(entireProcessTree: true)` 后等待退出。只有双平台残留进程测试证明
该原生路径不足时，才增加 Windows Job Object 或 macOS Process Group 实现。

### 2.14 M4 Core 内置工具集

M4 只注册以下五个 `CoreNative` 工具，均为
`Audience=Model`、`Exposure=Direct`：

| Canonical Tool Name | Effect | ReplaySafety | Agent | Plan |
| --- | --- | --- | --- | --- |
| `file.list` | `WorkspaceRead` | `Safe` | 是 | 是 |
| `file.read` | `WorkspaceRead` | `Safe` | 是 | 是 |
| `file.write` | `WorkspaceRead + WorkspaceWrite` | `Unsafe` | 是 | 否 |
| `shell.run` | `WorkspaceRead + WorkspaceWrite + ProcessExecution + NetworkRead + ExternalMutation` | `Unsafe` | 是 | 否 |
| `web.fetch` | `NetworkRead` | `Unsafe` | 是 | 是 |

Tool Definition ID 分别使用固定的 `CoreNative / opencowork.core /
<canonical-name>` 三元组；Runtime Binding ID 同样为稳定常量，进程重启后不得变化。

#### 2.14.1 File

所有 File 参数只接受以 Workspace Root 为基准的相对路径。路径先规范化为内部绝对
路径，再复用现有 `WorkspacePathGuard.ResolveContained` 检查逻辑路径、物理路径、
现存父级和符号链接；写入前再次调用 `RevalidateForWrite`。根路径、绝对路径、父级
逃逸和指向 Workspace 外部的链接稳定拒绝。

固定 Path Blacklist 为：

| 路径 | Read / List | Write |
| --- | --- | --- |
| `.git` 及其后代 | 拒绝 | 拒绝 |
| `.opencowork/runtime` 及其后代 | 拒绝 | 拒绝 |
| `.opencowork/config.local.jsonc` | 拒绝 | 拒绝 |
| `.opencowork` 其余内容 | 允许读取 | 拒绝 |

Blacklist 使用规范化后的 Workspace 相对路径和平台正确的大小写比较；被拒绝的条目
不会通过 `file.list` 泄漏名称。

`file.list` 只枚举一个目录层级，按规范化相对路径 Ordinal 排序，返回名称、类型、
字节数和最后修改时间。它不递归、不展开链接、不解释 `.gitignore`。

`file.read` 只接受严格有效的 UTF-8 文本，支持 `startLine` 与 `lineCount`，返回实际
行范围、`hasMore`、内容和完整文件 SHA-256。二进制、无效 UTF-8、目录和超限结果
形成稳定 Failure，不自动转 Base64。

`file.write` 只执行 UTF-8 整文件原子写入。覆盖现有文件必须提供由最近一次
`file.read` 返回的 `expectedSha256`，不匹配则
`Failed(tool.preconditionFailed)`；创建新文件时目标必须不存在。父目录必须已存在。
Binding 在同目录创建临时文件、Flush 后重新检查物理路径，再以原子 Rename /
Replace 提交并清理临时文件。M4 不提供 Append、Delete、Move、Mkdir、局部 Replace
或 Patch。

#### 2.14.2 Shell

`shell.run` 只接受 `command` 和可选 Workspace 相对 `workingDirectory`。M4 不接受
stdin、自定义环境变量、交互终端、后台执行、会话复用或 Shell 选择。

宿主固定为：

| 平台 | Shell Host |
| --- | --- |
| macOS | `/bin/zsh -lc` |
| Windows | 优先 `pwsh -NoLogo -NoProfile -NonInteractive`，不存在时回退 `powershell.exe -NoLogo -NoProfile -NonInteractive` |

Tool Result 记录实际宿主、退出码、stdout、stderr 和持续时间。非零退出码表示命令
已确定结束，仍为 Completed，不转换成 Pipeline Failed。

Shell 子进程继承宿主正常开发环境，但必须移除冻结 Provider Credential 的来源环境
变量，以及名称命中 Password、Token、Secret、API Key、Credential 或
Authorization 的变量。工具参数不能补回被移除变量。stdout / stderr 继续经过
2.12 的统一脱敏和 2.13 的输出上限。

由于 M4 没有跨平台文件系统或网络沙箱，`shell.run` 必须声明全部潜在 Effect，并
始终对完整命令逐次审批。审批是明确的人类授权，不把 Shell 文本解析包装成虚假的
能力隔离。

#### 2.14.3 Web

`web.fetch` 只接受 URL 和 `GET` / `HEAD` 方法，不接受 Body、自定义 Header、
Cookie、Credential、代理设置或认证信息。URL 只允许 `http` 与 `https`，且
UserInfo 必须为空。

每个初始请求和重定向目标都必须重新解析并拒绝 Loopback、Private、Link-local、
Multicast、Unspecified、文档保留地址和云 Metadata 地址；最多跟随五次重定向。
连接必须绑定已经校验的解析结果，不能在校验后让默认连接逻辑再次解析到不同地址。

GET 只返回文本、HTML、JSON、XML 与 JavaScript 类媒体内容；HEAD 不读取 Body。
压缩后的响应必须按解压后字节数执行 2.13 的硬限制。M4 不缓存响应、不落下载文件，
也不支持 Web Search。

M4 明确不实现 `file.search`、局部 Patch、后台 Shell、二进制 File / Web 内容和
搜索 Provider。只有真实 M4 使用或后续里程碑验收证明五工具闭环不足时，才新增独立
工具，而不是扩张这五个工具的参数模式。

### 2.15 Journal、Item、Event 与 SQLite v4 投影

M4 继续使用 Session 的 Thread–Turn–Item 聚合和 ThreadJournal 单一事实源，不新增
Tool Store。工具循环的持久化顺序固定为：

```text
Provider Tool Call Frame
→ Tool Invocation Started
→ WaitingApproval / InteractionResolved（如需）
→ Tool Invocation Attempt Started
→ Tool Invocation Terminal + ToolResult Item
```

#### 2.15.1 Session Item

`SessionItemType` 只新增 `ToolCall` 与 `ToolResult`：

- `ToolCallItemContent` 保存 Provider Round、可选的已完成 AgentMessage Item ID 和
  Provider 返回的有序 Tool Call 列表；
- 每个 Call 保存 Provider Tool Call ID、Provider 工具名、Preflight 后的安全参数、
  参数摘要和 `SensitiveInputDetected`；
- Preflight 后的安全参数只由 ToolCall Item 持有，Invocation Fact 不再复制；
- 完整 Frame 在执行第一个 Tool Call 前作为一个 Completed Item 提交；
- `ToolResultItemContent` 直接承载 2.12 的 `ToolResultSnapshot`；
- `ToolResultSnapshot` 只由 ToolResult Item 持有，Terminal Fact 不再复制；
- Tool Result Item 与 Tool Invocation Terminal 在同一 Journal Entry 中原子提交。

Frame 不完整、参数 JSON 无效或超限时，不提交 Item，也不执行其中任何工具。命中
机密输入时提交脱敏后的完整 Frame；受影响调用形成拒绝结果，其余调用按 Provider
顺序串行处理。每个 Tool Result Item 紧随对应 Invocation Terminal 提交。

#### 2.15.2 Journal Facts 与 Session Events

M4 只新增三个 Tool Invocation Fact：

- `ToolInvocationStartedFact`
- `ToolInvocationAttemptStartedFact`
- `ToolInvocationTerminalFact`

Started Fact 保存身份、冻结 Snapshot 摘要、参数摘要、ToolCall Item ID 和 Call
Index；恢复参数从该 Item 的对应 Call 读取，它不声称参数已经通过 Definition
Schema。只有 Schema 及其后的全部前置阶段通过后才能提交 Attempt Started。
Attempt Started Fact 保存递增 Attempt Number 与时间；Terminal Fact 只保存
Terminal Status、Error Code、Result SHA-256 和 Result Item ID。对同一 Tool
Invocation，Journal 回放必须得到恰好一个 Started、零至两个 Attempt Started 和
至多一个 Terminal；违反约束则 Thread 进入 RecoveryRequired。

Provider Tool Call Frame 使用一个专用完成态 Item Fact 提交，不再模拟
StartItem + CompleteItem 两次写入。Approval 继续使用现有 `TurnWaitingFact`、
`InteractionResolvedFact` 和 `TurnExecutionResumedFact`，只扩展可空
Tool Invocation ID 关联；非工具审批保持原格式和语义。

`SessionEventType` 只新增：

- `ToolCallRecorded`
- `ToolInvocationStarted`
- `ToolInvocationAttemptStarted`
- `ToolInvocationTerminal`

Terminal Event Payload 从同一 Journal Entry 的 Terminal Fact 与 ToolResult Item
组装 Tool Invocation Snapshot 和唯一 ToolResultSnapshot，不再追加第二个 Result
Event。现有 `TurnWaitingApproval`、`InteractionResolved`、Item 查询和 History
订阅继续工作。

#### 2.15.3 SQLite Schema v4

State Migration v4 只新增一张 `tool_invocations` 查询投影表，每个内部 Tool
Invocation ID 一行，至少包含：

```text
tool_invocation_id       PRIMARY KEY
thread_id
turn_id
provider_tool_call_id
provider_tool_name
tool_definition_id       NULL
runtime_binding_id       NULL
snapshot_sha256
arguments_sha256
status
attempt_count
result_item_id            NULL
error_code                NULL
started_at
updated_at
completed_at              NULL
```

表使用现有 Thread / Turn 外键与级联删除策略，并为
`(thread_id, turn_id, provider_tool_call_id)` 和 `(thread_id, status)` 建查询索引，
但不对 Provider Tool Call ID 建唯一约束，以保留 2.11 的冲突审计记录。原始参数和
完整 Result 不重复写入该表；安全参数只在 ToolCall Item，Result 内容只在
ToolResult Item。

M4 不创建 `tool_definitions`、`tool_registrations`、`tool_attempts`、
`tool_results` 或 Tool Snapshot 表。EffectiveToolSnapshot 继续嵌入已有
AgentInvocationSnapshot Journal Fact，Attempt 明细继续由 Journal 回放。

Migration v4 沿用现有 Checkpoint、Backup、事务 DDL、Schema Validation 和失败恢复
流程。由于 M2 的 `items.item_type` 使用闭集 `CHECK`，v4 必须在同一事务内无损
重建 `items` 表约束以加入 `toolCall` 与 `toolResult`，并同步重建唯一引用它的
`pending_interactions` 表，避免 SQLite 表重命名把外键永久改指向临时表；两表既有
行和索引必须原样保留。最终 Schema 仍只新增 `tool_invocations` 一张业务表。旧
Thread 不回填工具记录；
Projection Rebuild 删除并从所有 Journal 重建 `tool_invocations`。Fork / Rollback
的完成历史从 ToolCall 与 ToolResult Item 恢复 Provider 消息和终态查询投影，不
复制 WaitingApproval、未终态 Invocation 或独立 Attempt 历史。

### 2.16 Provider 消息历史、Token 预算与 Compaction

M4 扩展统一 Chat Completion 契约：

- `ChatCompletionMessageRole` 新增 `Tool`；
- Assistant Message 可携带有序 `ToolCalls`；
- Tool Message 必须携带原 Provider Tool Call ID；
- Response 请求携带 EffectiveToolSnapshot 中的 Provider Tool Definitions；
- Compaction 请求的 Tools 始终为空。

Session Item 到 Provider Message 的映射固定为：

```text
UserMessage Item
→ role=user

未关联 ToolCall 的 AgentMessage Item
→ role=assistant, content

ToolCall Item
→ role=assistant, optional linked AgentMessage content + ordered tool_calls

ToolResult Item
→ role=tool, tool_call_id + canonical Tool Result Envelope
```

被 ToolCall Item 引用的 AgentMessage 不再单独生成第二条 Provider Message。该
AgentMessage、ToolCall Frame 与全部 ToolResult 共同构成不可拆分消息组；没有
Assistant Content 时组从 ToolCall Item 开始。组内 Result 必须与 Frame 中的 Call
一一对应、Provider Tool Call ID 唯一且顺序一致；缺失、重复、越序或未知 ID 在
Journal 回放时使 Thread 进入 RecoveryRequired。当前未完成组只用于同 Turn
Checkpoint 恢复，补齐缺失结果前不得发起下一次 Provider 请求。

每个 Provider Response Round 使用独立的 AgentMessage、Reasoning 和 ToolCall
Item；ToolCall Item 只能引用同一 Round 的已完成 AgentMessage，不能复用前一轮
Item 或跨轮追加 Delta。

AgentFactory 的 Token 预算必须覆盖实际出站材料，而不再只计算角色与文本：

- System / User / Assistant / Tool Message；
- Tool Call ID、Provider Name 和 Canonical 参数；
- Canonical Tool Result Envelope；
- 每个 Response 请求重复携带的冻结 Tool Definitions；
- Provider Profile 已定义的消息与工具协议开销。

同一 Turn 的每个 Response Round 使用同一 EffectiveToolSnapshot 和同一 Provider
Name 映射。每次把 Tool Results 追加到消息历史后，必须重新计算可用输入预算，再
决定继续 Response、触发 Compaction 或稳定失败。

2.13 的 256 KiB 是持久化硬上限，不是保证可以塞入任意模型窗口。Pipeline 在提交
Tool Invocation Terminal 前，使用当前 Provider Tokenizer 计算下一次 Response 的
剩余输入预算；如果规范化结果仍过大，则继续收缩同一份已脱敏结果的头尾预览，直到
ToolResultSnapshot 与下一次请求都可接受。最终写入 Journal 的 Snapshot 就是发给
Provider 和后续重复回放的唯一版本，不允许 Provider Adapter 临时再截一份。

如果最小 Error / Truncation Envelope 加上 System Prompt、当前 User Message、
Tool Definitions 和当前不可拆分 Tool 组仍超过窗口，Pipeline 仍先提交真实 Tool
Terminal 与最小 ToolResult Item，随后复用现有 `context.inputTooLarge` 终止当前
Turn；不得丢弃已发生的工具结果、伪造失败或更换 Snapshot。

Compaction 继续复用 M3 管线，但按完整消息组选择边界：

- 关联 AgentMessage、ToolCall Frame 与对应的全部 ToolResult 不能被拆开；
- 当前未完成 Tool 组不能进入 Compaction Source；
- `SourceMessagesSha256` 覆盖关联 Assistant Content、Canonical ToolCall 与
  ToolResult 内容；
- Summary 可以概括较早的完整 Tool 组，但不能被当作可重放工具结果；
- Reactive Compaction 只能移除或概括已完成组，不能修改当前 Tool Call ID。

M4 写入 Compaction Checkpoint Schema v2。v2 使用工具感知的分组和摘要算法；M3
Schema v1 只在 Source Range 不含 ToolCall / ToolResult Item 时继续接受。M4 不增加
Tool 专用 Tokenizer、第二套 Compaction Store 或 Provider 侧隐式历史。

### 2.17 稳定错误码与终态映射

M4 在 Abstractions 中新增独立 `ToolErrorCodes`，不把工具阶段错误混入
`AgentErrorCodes` 或 `SessionErrorCodes`。

稳定错误码固定为：

| 阶段 | Error Codes |
| --- | --- |
| Definition / Snapshot | `tool.definitionInvalid`, `tool.nameConflict`, `tool.snapshotTooLarge` |
| Turn Loop | `tool.iterationLimitExceeded` |
| Lookup / Duplicate | `tool.notFound`, `tool.callIdConflict` |
| Audience / Exposure / Mode | `tool.audienceDenied`, `tool.exposureDenied`, `tool.modeDenied` |
| Binding / Lease | `tool.bindingUnavailable`, `tool.leaseExpired` |
| Authority | `tool.authorityDenied` |
| Schema / Input | `tool.inputInvalid`, `tool.inputTooLarge`, `tool.sensitiveInputRejected` |
| Policy / Hook | `tool.policyDenied`, `tool.hookDenied`, `tool.hookFailed` |
| Approval | `tool.approvalDenied` |
| Invoke / Result | `tool.executionFailed`, `tool.resultInvalid`, `tool.outputLimitExceeded` |
| Control | `tool.timeout`, `tool.cancelled`, `tool.outcomeUnknown` |
| Core Binding | `tool.pathDenied`, `tool.pathNotFound`, `tool.contentUnsupported`, `tool.preconditionFailed`, `tool.networkTargetDenied` |

Definition / Snapshot 代码既可用于隔离诊断，也可在整个 Snapshot 无法构建时终止
Turn。`tool.iterationLimitExceeded` 是 Tool Loop 级 Turn Error，不创建
ToolResultSnapshot；其余代码用于 ToolResultSnapshot。调用方必须按 Code 和
Terminal Status 分支，Message 只提供简短、脱敏且非契约性的说明。

Terminal Status 映射固定为：

| 场景 | Terminal Status |
| --- | --- |
| Lookup、Audience、Exposure、Mode、Binding、Lease、Authority、Schema、Policy、Hook 或 Approval 前置拒绝 | `Rejected` |
| Binding 的确定性 Failure、逃逸异常、结果无效或输出超限 | `Failed` |
| 显式 Turn Cancellation | `Cancelled` |
| Tool 或 Turn Deadline | `TimedOut` |
| Attempt 已交付 Binding，但无法确认最终结果 | `OutcomeUnknown` |
| Binding 返回 Success，包括 Shell 非零退出码与 HTTP 4xx / 5xx | `Completed` |

M4 不自动重试任何已有 Terminal 的工具错误；Tool Result Error 的
`SessionError.IsRetryable` 固定为 `false`。`ReplaySafety` 只控制没有 Terminal 的
崩溃恢复，不能覆盖 Rejected、Failed、Cancelled、TimedOut 或 OutcomeUnknown。

通用 Error Message 不得包含原始异常类型、堆栈、绝对路径、Shell 命令、URL、
Header、响应正文或未脱敏系统错误。模型确实需要且已被授权的上下文只放在对应工具
的脱敏结构化 Output 或 Approval Request 中，不拼进错误消息。

### 2.18 M4 Hook 测试缝与 M6 边界

M4 不发布公共 Hook API，也不实现 Hook 来源或生命周期。`ToolInvocationPipeline`
构造函数只接受两个可空的 internal delegate：`PreToolUse` 与 `Terminal`。正式
WorkspaceRuntime 组合两者均传 `null`，对应管线阶段为确定性 No-op；Core 测试可
注入 delegate 覆盖 M4-ACC-005。

`PreToolUse` 的输入是不可变的调用上下文，只能返回以下收窄决定：

- `Deny`；
- `RequireApproval`；
- `TimeoutCap`。

决定类型不表达工具替换、参数修改、Effect 或 Authority 扩张，也不能跳过
Approval。多个限制按最严格交集生效：`Deny` 直接以
`Rejected(tool.hookDenied)` 结束；`RequireApproval` 只能增加审批；`TimeoutCap`
只能缩短现有 Deadline。delegate 抛出的异常 Fail Closed，以
`Rejected(tool.hookFailed)` 结束。生效的 Hook 决定摘要进入 Tool Invocation 审计
结果，但不保存任意 Hook 私有状态。

`Terminal` delegate 只在 Terminal Journal 提交成功后运行。它只能读取已脱敏、
不可变的 Terminal Snapshot，不返回值，也不能修改或追加第二个 Terminal。其异常
只写入脱敏诊断日志，不能回滚已经提交的工具结果或改变 Provider 可见历史。

M4 不增加 `IToolHook`、Hook Registry、配置节、动态发现、持久化表或独立进程宿主。
M6 在拥有真实 Hook 来源、生命周期和兼容性需求后，再把验证过的阶段语义提升为
公共契约。

## 3. 最小数据流

```text
ThreadJournal
→ 重放 Thread / Turn / Item 与 AgentInvocationSnapshot
→ AgentFactory 冻结 EffectiveToolSnapshot 和 Provider 名称映射
→ Provider 流式返回完整 Tool Call Frame
→ SessionService 提交 ToolCall Item
→ ToolInvocationPipeline 按固定安全阶段串行调度
→ Core Runtime Binding 执行 File / Shell / Web
→ 结果归一化、脱敏与大小收缩
→ SessionService 原子提交 Terminal + ToolResult Item
→ Agent Runtime 追加 Assistant Tool Call / Tool Message
→ 使用同一 Snapshot 继续下一次 Provider Response
```

崩溃恢复只从 ThreadJournal 重建未完成调用；SQLite v4 仅提供查询投影。恢复必须先
按 2.11 补齐安全重放或 `OutcomeUnknown`，再允许发起下一次 Provider 请求。

## 4. DotCraft 证据基线核对

| 设计面 | DotCraft 证据 | M4 处理 |
| --- | --- | --- |
| 唯一 Session Core、审批、取消和 Thread–Turn–Item 状态 | §5.1-§5.7 | 保持语义，复用 M2/M3 的 `SessionService`、ThreadJournal、Interaction 和执行 Sink，不创建 Tool Store。 |
| AgentFactory 冻结模式、模型和工具视图 | §6.1、§6.3 | 保持每 Turn 冻结语义；M4 将 M3 的空工具快照扩展为 `EffectiveToolSnapshot`，进行中 Turn 不受注册变化影响。 |
| 流式函数调用与结果回注 | §6.2 | 按 OpenCoWork Provider 中立契约重设计为完整 Tool Call Frame、串行调用和标准 Assistant Tool Call / Tool Message 历史。 |
| Definition、Binding、Registration、来源与名称映射 | §7.1-§7.3 | 保持职责分离、冲突隔离和双向名称映射；M4 只实现 `CoreNative`，不提前实现动态来源。 |
| ToolDispatcher 安全顺序与稳定错误 | §7.4-§7.5、§13.1-§13.3 | 保持连续收窄和前置拒绝也审计的语义；按 M0 冻结管线补入 Snapshot Lookup、Attempt 提交、统一结果快照和 `OutcomeUnknown`。 |
| Journal / SQLite 双层持久化与回放 | §9.1、§9.3-§9.4 | 保持 Journal 权威、SQLite 可重建；M4 只增加一个 `tool_invocations` 投影表和 Compaction Checkpoint v2。 |
| Deferred、MCP、Plugin 与动态工具 | §7.6、§8.5 | 延期到 M5/M6；M4 不为未来来源预建 Registry、公共 Hook 或独立宿主。 |
| 工具验收与故障注入 | §15.2-§15.3 | 以 M4-ACC-001 至 M4-ACC-010 重写为可自动化、双平台和副作用计数证据，不沿用 DotCraft 私有测试设施。 |

核对结论：M4 保留 DotCraft 规范中可观察的工具身份、快照冻结、安全阶段、审批、
超时、审计与恢复不变量，但不复刻其程序集、私有 ToolDispatcher 类型、动态来源或
SDK 组合。OpenCoWork 新增的固定限制、错误码、ReplaySafety 和 Provider 历史格式
受 M0 验收目录约束。

## 5. 验收映射

| 验收编号 | 冻结设计覆盖 | 预期证据 |
| --- | --- | --- |
| `M4-ACC-001` | §2.1-§2.3：Definition、Binding、Registration 和来源身份独立，生产路径仍进入现有 Session 执行链。 | 契约快照、重复来源与 Binding 失效测试。 |
| `M4-ACC-002` | §2.4-§2.5：每 Turn 冻结工具、Authority 上限和 Provider 名称双向映射，冲突定义隔离。 | 热更新竞态、名称限制/碰撞和恢复后映射一致性测试。 |
| `M4-ACC-003` | §2.8、§2.15：Pipeline 是唯一审计提交者，固定阶段均形成可观测 Trace 和 Journal 事实。 | 逐阶段 Trace Snapshot、顺序断言和旁路防护测试。 |
| `M4-ACC-004` | §2.8-§2.10、§2.17：Audience、Exposure、Mode、Lease、Authority、Schema 和 Policy 拒绝均映射稳定代码。 | 完整拒绝矩阵、错误契约快照和原始异常泄漏扫描。 |
| `M4-ACC-005` | §2.8-§2.9、§2.18：Hook 与 Approval 只能收窄，前置拒绝同样提交 Started 和单一 Terminal。 | Authority 交集、恶意 delegate、重复审批和 Hook 异常测试。 |
| `M4-ACC-006` | §2.6、§2.13-§2.14：Turn、Provider、Tool 和子进程共享取消链与唯一 Deadline，超时后清理进程树。 | 全阶段超时/取消故障注入、Windows/macOS 真实进程树残留检查。 |
| `M4-ACC-007` | §2.11、§2.17：已交付 Binding 而结果不明时，Unsafe 调用不重试并提交 `tool.outcomeUnknown`。 | 提交前后崩溃/断连、Safe/Unsafe 对照和副作用计数测试。 |
| `M4-ACC-008` | §2.7、§2.9、§2.14：Plan 只曝光纯计算、工作区读取和无副作用网络读取，禁止写入、进程与外部修改。 | Agent/Plan、Effect、Authority 和 Provider 名称组合矩阵。 |
| `M4-ACC-009` | §2.10、§2.12-§2.14：五个 Core 工具执行统一 Schema、路径/网络边界、脱敏、超时和输出限制。 | Windows PowerShell 与 macOS zsh/File/Web 实跑、安全边界和输出上限证据。 |
| `M4-ACC-010` | §2.6、§2.11、§2.15-§2.16：Provider 重试、恢复和重复 Call ID 复用冻结结果，不重复已提交副作用。 | Call ID 冲突、Journal 重放、Checkpoint 恢复和副作用唯一性故障注入。 |

## 6. 冻结结论

M4 设计于 2026-07-28 经逐项确认、M0 验收反查和独立一致性审查后冻结。实现必须继续
复用 `ISessionExecutor → SessionService → ThreadJournal` 单一路径，以
EffectiveToolSnapshot 冻结每 Turn 工具视图，并严格执行本文定义的固定安全管线、
单一载荷归属、恢复规则和五个 Core 工具边界。

实现计划不得提前引入 MCP、Plugin、动态工具、公共 Hook API、后台工具、Sandbox、
Node REPL、第二套工具状态机或独立 Tool Store。只有 `M4-ACC-001` 至
`M4-ACC-010` 的自动化、故障注入和 Windows/macOS 真实平台证据全部满足后，M4
Slice 才能标记完成。
