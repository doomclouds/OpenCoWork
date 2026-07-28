# OpenCoWork M3 Agent Runtime Alpha 实施计划

**Status:** Completed；Outcome 1-8 已完成。

**Goal:** 在 M2 Durable Session Core 上交付 Provider 中立、无真实工具、支持
真实多轮对话、流式响应、重试、Token 预算和可恢复压缩的 Agent Runtime。

**Why planning is required:** M3 同时修改公共 Agent/Session 契约、SQLite Schema、
ThreadJournal 事实、配置与 Secret 边界、Tokenizer 资产、HTTP/SSE 协议、流式提交、
重试、上下文压缩、CLI 和真实 Provider 发布验证，属于跨模块、数据迁移、
公共契约和安全敏感工作，必须按依赖闭包推进。

**Acceptance:** `M3-ACC-001` 至 `M3-ACC-008` 全部具备自动化或真实平台证据；
默认测试完全离线；DeepSeek 官方的两条模型路径在 `osx-arm64` 通过真实短请求；
Secret 不进入 Journal、SQLite、Session Event、日志、stdout、stderr 或测试产物；
其他 Provider 和 `win-x64` 真实兼容性进入独立待验证清单；最终只生成一份 M3
交付归档。

## Source Documents

- [M3 Agent Runtime Alpha 设计规格](../specs/2026-07-27-open-cowork-m3-agent-runtime-alpha-design.md)
- [M2 Durable Session Core 设计规格](../specs/2026-07-26-open-cowork-m2-durable-session-core-design.md)
- [M0 Contract Freeze](../specs/2026-07-25-open-cowork-m0-contract-freeze-design.md)
- [OpenCoWork Runtime 1.0 路线规格](../specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md)
- [M0 能力台账](../specs/2026-07-25-open-cowork-m0-capability-ledger.md)
- [M0-M10 验收目录](../specs/2026-07-25-open-cowork-m0-acceptance-catalog.md)
- 本机证据基线：`DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md`

本计划中的 Outcome 是 M3 内部依赖结果，不是独立 Slice。Outcome 可以作为提交
边界，但不得创建独立规格、独立归档或提前把 M3 标记为 Done。

## Change Map

优先复用 M1 配置/日志/生命周期与 M2 Session/Journal/Projection/Execution。
不新增项目、Agent 模块、Provider SDK、模板引擎、HTTP 工厂、ORM、第二套事件总线
或第二套会话状态机。

### 计划新增

| 路径 | 职责 |
| --- | --- |
| `src/OpenCoWork.Abstractions/AgentContracts.cs` | 最小 `IChatCompletionClient`、请求/事件/异常、Finish Reason、Agent Mode、Invocation/Usage/Compaction 快照和 M3 稳定错误码。 |
| `src/OpenCoWork.Core/Configuration/ModelsConfig.cs` | `models` 配置节、Provider/Model/Tokenizer 引用和 Generated Schema 约束。 |
| `src/OpenCoWork.Core/Agents/AgentRuntime.cs` | `ProviderRegistry`、同步 `AgentFactory`、`AgentRuntimeExecutor` 和最小 DI 注册。 |
| `src/OpenCoWork.Core/Agents/AgentPrompts.cs` | 两个版本化 Prompt、Workspace 指令安全读取、确定性组装和 SHA-256。 |
| `src/OpenCoWork.Core/Agents/TokenizerProfiles.cs` | `Tiktoken` 适配、内置 Profile、Chat Template 计数、资产 SHA-256 与预算。 |
| `src/OpenCoWork.Core/Agents/OpenAiCompatibleChatClient.cs` | 共享 `HttpClient`、Chat Completions 请求、严格 SSE、错误信封、传输上限和错误归一化。 |
| `src/OpenCoWork.Core/Agents/Tokenizers/` | M3 精确模型对应的版本化 `tokenizer.json` 资产；来源、版本和 SHA-256 由 `TokenizerProfiles` 固定。 |
| `tests/OpenCoWork.Core.Tests/AgentContractTests.cs` | 公共 DTO、稳定错误码、序列化和架构边界测试。 |
| `tests/OpenCoWork.Core.Tests/AgentFactoryTests.cs` | Prompt Golden、Workspace 指令、模型历史、Snapshot、Tokenizer 和预算测试。 |
| `tests/OpenCoWork.Core.Tests/ChatCompletionClientTests.cs` | BCL Loopback Server、SSE、HTTP、TLS、上限和错误分类测试。 |
| `tests/OpenCoWork.Core.Tests/AgentRuntimeExecutorTests.cs` | 流式 Item、重试、Usage、取消、Deadline 和终态测试。 |
| `tests/OpenCoWork.Core.Tests/CompactionTests.cs` | Micro/Partial、Checkpoint、prompt-too-long 和恢复测试。 |
| `tests/OpenCoWork.IntegrationTests/ChatCliIntegrationTests.cs` | `opencowork chat`、重启恢复、模式、重定向输入和 Ctrl+C 集成测试。 |
| `tests/OpenCoWork.IntegrationTests/ProviderReleaseValidationTests.cs` | 显式启用的 DeepSeek 官方两条真实短冒烟和 Secret Canary 发布 Runner。 |

实现时可以在不改变职责边界的前提下合并过短文件或测试 Fixture；不得为单一实现
创建 `IProviderRegistry`、`IAgentFactory`、`IPromptBuilder`、`ITokenBudgetPlanner`
或通用 Pipeline 抽象。

### 计划修改

| 路径 | 修改目的 |
| --- | --- |
| `Directory.Packages.props` | 只增加冻结版本 `Tiktoken 3.1.5`。 |
| `src/OpenCoWork.Core/OpenCoWork.Core.csproj` | 引用 `Tiktoken` 并将版本化 Tokenizer 资产纳入发布产物。 |
| `src/OpenCoWork.Abstractions/SessionContracts.cs` | Thread/Turn/Queue 模式与模型选择、两个 Session 命令及 Agent 持久化意图。 |
| `src/OpenCoWork.Core/Sessions/SessionFacts.cs` | 模型/模式切换、Invocation Snapshot、Usage 和 Compaction Checkpoint Journal 事实。 |
| `src/OpenCoWork.Core/Sessions/SessionDomain.cs` | 模型/模式状态转移及冻结语义。 |
| `src/OpenCoWork.Core/Sessions/SessionService.cs` | `SetThreadModel`、`SetAgentMode`、幂等提交和快照查询。 |
| `src/OpenCoWork.Core/Sessions/SessionQueue.cs` | Queue Item 冻结 Agent Mode，调度时读取 Thread 当前模型。 |
| `src/OpenCoWork.Core/Sessions/SessionExecution.cs` | 新执行意图的 Journal/Projection/Event 顺序、Usage 去重和可见增量提交边界。 |
| `src/OpenCoWork.Core/Sessions/SessionProjection.cs` | Schema v3 的模型、模式、Snapshot、Usage 和 Compaction 投影与重建。 |
| `src/OpenCoWork.Core/Sessions/SessionRecovery.cs` | M3 新事实回放、未终态 Provider 调用失败和 Checkpoint 恢复。 |
| `src/OpenCoWork.Core/Sessions/SessionRuntime.cs` | 解析已注册的真实 Agent Executor，并在宿主释放共享 HTTP 资源前停止全部 Session 执行。 |
| `src/OpenCoWork.Core/State/StateRuntime.cs` | 生产 Schema v3 迁移、结构快照和旧 v2 升级。 |
| `src/OpenCoWork.App/Program.cs` | 有效配置加载、Agent 注册和 `opencowork chat` 入口；CLI 仍只调用 `ISessionService`。 |
| `tests/OpenCoWork.Core.Tests/` | 扩展现有 Session/State/Configuration/Logging 回归，覆盖 v3 重建和 Secret 脱敏。 |
| `tests/OpenCoWork.IntegrationTests/` | 扩展宿主、崩溃恢复、真实文件系统和发布验证。 |
| `tests/OpenCoWork.ArchitectureTests/ProjectGraphTests.cs` | 守卫唯一新增包、零新增项目和 Agent 契约归属。 |

## Execution Rules

- 每个 Outcome 先建立能够失败的聚焦测试，再实现到该 Outcome 验收信号通过；
- 默认测试不访问公网、不读取真实凭据；真实 Provider 只由显式发布 Runner 执行；
- 使用现有 xUnit v3、BCL、`System.Text.Json`、`HttpClient`、`TimeProvider`、
  `System.Threading.Channels` 和唯一新增的 `Tiktoken 3.1.5`；
- HTTP/SSE 故障测试复用一个测试内 BCL Loopback Server，不新增 Harness 项目或
  ASP.NET Core/TestServer 依赖；
- 所有 Provider 调用只消费 `AgentInvocationSnapshot` 冻结后的配置、Prompt、
  Tokenizer 和 Secret；Attempt 不重新读取文件、环境或配置；
- Journal 仍是 Thread、Turn、Item、模型历史、Invocation、Usage 和 Compaction
  的权威源；SQLite 只做可重建投影；
- 任一 Journal 事实 Flush 后，投影或 Event 失败不得把结果冒充为未提交；
- 任一 Outcome 发现需要改变冻结的公共契约、错误语义、首个可见增量边界、三次
  调用预算、安全边界或验收矩阵，立即停止实现并先修订 M3 设计规格；
- 未通过当前 Outcome 聚焦测试和已有累计回归，不进入下一个 Outcome；
- M3 不实现真实工具、Tool Call 执行、AppServer、ACP、插件 Provider、动态模型
  发现、Responses API、Provider 托管历史或运行时 Tokenizer 下载。

### Outcome 1: Agent/Session 契约、Journal 事实和 Schema v3 形成可编译基线（已完成）

- Work:
  - 在 `AgentContracts.cs` 定义冻结的最小公共契约：
    `IChatCompletionClient.StreamAsync`、请求、Content/Reasoning/Usage/Completed
    事件、`ChatCompletionException`、Finish Reason、Invocation Purpose、
    `AgentMode`、Invocation/Usage/Compaction 快照和 M3 稳定错误码。
  - 保持 `ChatCompletionRequest` 无 Provider ID、Secret、原始 JSON、扩展字典、
    Tool Message 或非流式分支；Provider 私有错误和 prompt-too-long 映射只留在 Core。
  - 扩展 `ThreadSnapshot` 持久化 Provider ID、精确 Model ID 和当前 Agent Mode；
    `TurnSnapshot` 与 `QueuedTurnInputSnapshot` 持久化
    `EffectiveAgentMode`，既有数据默认 `Agent`。
  - 扩展 `CreateThreadRequest`，新增 `SetThreadModelRequest`、
    `SetAgentModeRequest` 与对应 `ISessionService` 方法；复用 Thread Gate、
    `expectedSequence` 和 Workspace 全局幂等。
  - 增加 `ThreadModelChanged`、`ThreadModeChanged`、
    `AgentInvocationSnapshotRecorded`、`ProviderUsageRecorded` 和
    `CompactionCheckpointRecorded` 事实及 Session Event；Secret、完整 Prompt 和
    Workspace 指令正文不得进入 Payload。
  - 增加最小执行意图，使 Executor 能原子提交 Invocation Snapshot、唯一 Usage
    事实和摘要+来源范围+Checkpoint；继续由 SessionService 执行
    Journal→内存→Projection→Event。
  - 将 `StateMigrations` 推进到 v3：为 Thread/Turn/Queue 增加模型和模式字段，
    保存 Turn Invocation Snapshot、最新 Compaction Checkpoint，并增加按
    `(invocationId, attemptNumber, purpose)` 唯一的 Provider Usage 投影。
  - v2→v3 迁移使用现有备份、事务、结构校验和恢复；新库直接到 v3，投影重建可从
    Journal 得到字节一致的规范快照。
- Risks/open questions:
  - 不能复用 `SessionExecutionCheckpoint` 表达 Compaction；前者只属于
    Waiting Interaction 的 Executor 续接。
  - Queue 只冻结 Agent Mode；Provider/Model 必须在真正创建 Turn 时读取 Thread
    当前选择。
  - v2→v3 任一步失败必须恢复 v2 备份并阻断启动，不能留下部分列或半张 Usage 表。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter "FullyQualifiedName~AgentContractTests|FullyQualifiedName~SessionContractTests|FullyQualifiedName~StateRuntimeTests"`
  - `dotnet test tests/OpenCoWork.ArchitectureTests/OpenCoWork.ArchitectureTests.csproj -c Release`
  - `dotnet build OpenCoWork.slnx -c Release --no-restore`
- Acceptance contribution:
  - `M3-ACC-001`
  - `M3-ACC-003`
  - `M3-ACC-007`
  - `M3-ACC-008`

### Outcome 2: 有效模型配置、Secret 冻结和真实 Tokenizer 预算可离线验证（已完成）

- Work:
  - 在 `ModelsConfig.cs` 实现 `models.defaultProvider`、
    `models.defaultModel`、具名 Providers、精确 Models 和显式 Tokenizer Profile
    引用；所有 ID 用 Ordinal 精确匹配。
  - 配置管线校验 Base URL、默认引用、范围、环境变量名、Profile 引用和自定义本地
    `tokenizer.json` 路径/SHA-256；仅允许 HTTPS，回环地址可用 HTTP。
  - `opencowork chat` 启动路径复用 `ConfigLoader` 产生唯一
    `EffectiveConfigSnapshot`；结构错误继续使用 `OCWCFGxxx`，不得另建配置系统。
  - WorkspaceRuntime 启动时一次读取当前存在的 API Key 环境变量，形成 Core 私有
    不可变凭据表并先扩充现有 `SecretRedactor`；未选 Provider 的缺失 Secret
    不阻止启动，选中组合预检失败不得创建或修改 Thread。
  - 在 `Directory.Packages.props` 和 Core 项目只加入 `Tiktoken 3.1.5`；
    内置编码直接复用库，其他模型加载随程序分发、固定 SHA-256 的
    `tokenizer.json`，运行时不下载。
  - 实现四个精确模型的版本化 `TokenizerProfile` 与 Chat Template 开销：
    `qwen3.8-max-preview`、`glm-5.2`、`deepseek-v4-pro`、
    `deepseek-v4-flash`。
  - Tokenizer Corpus 覆盖中英文、代码、Reasoning 和多轮消息；原始 Token ID 与
    参考实现零差异，完整 Prompt 计数不得低于 Provider Usage，保守高估不超过
    `max(32, ceil(providerPromptTokens × 0.005))`。
- Risks/open questions:
  - Tokenizer 资产来源、版本、许可与 SHA-256 必须在代码注释和发布证据中可追溯；
    任一资产不匹配都以配置错误失败，不能退化为字符估算。
  - Secret 原值不能进入 `EffectiveConfigSnapshot`、异常、结构化日志或测试快照。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter "FullyQualifiedName~ConfigurationPipelineTests|FullyQualifiedName~AgentFactoryTests|FullyQualifiedName~StructuredLoggingTests"`
  - `dotnet publish src/OpenCoWork.App/OpenCoWork.App.csproj -c Release -r osx-arm64 --self-contained false`
  - 从发布目录离线加载全部内置 Tokenizer 资产并校验 SHA-256
- Acceptance contribution:
  - `M3-ACC-001`
  - `M3-ACC-002`
  - `M3-ACC-005`

### Outcome 3: Prompt、Workspace 指令和 AgentFactory 产生确定性 Invocation（已完成）

- Work:
  - 在 `AgentPrompts.cs` 以 Core 私有 LF 常量实现
    `opencowork.response.v1` 和 `opencowork.compaction.v1`；不引入模板引擎、
    Provider 专用 Role 或第二条 System Message。
  - 只读取工作区根 `AGENTS.md`，复用 `WorkspacePathGuard` 校验物理边界；严格
    UTF-8、最大 64 KiB、移除 BOM、拒绝 NUL、规范化换行和尾部空行。
  - Prompt 只包含身份/安全边界、Effective Agent Mode、可选 Workspace 指令和
    工作区显示名称；不包含绝对路径、用户名、时钟、环境变量或 Secret。
  - 在 `AgentRuntime.cs` 实现具体 `ProviderRegistry` 和同步、确定性、无副作用的
    `AgentFactory`：解析精确配置、规范化 Model History、组装唯一 System Message、
    计算真实预算并返回内部 `Ready | CompactionRequired` Draft。
  - Tool Snapshot 只表达为空的不可变集合；不创建 M4 工具接口或 Provider 能力协商。
  - 每个 Turn 在任何压缩或 Provider 调用前提交一次
    `AgentInvocationSnapshot`，记录版本、哈希、来源、Token、限制和无 Secret
    配置指纹；相同输入必须字节一致。
  - 保存五份纯文本 Golden Snapshot，并对 Prompt 字节、SHA-256、版本和有序来源
    做精确断言；边界错误使用独立聚焦测试，不做组合爆炸。
- Risks/open questions:
  - Workspace 指令只影响当前 Turn；排队时不复制，后续 Attempt 也不得重新读取。
  - Prompt 文本任一字节变化都必须先升级版本并审查 Golden Diff。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter FullyQualifiedName~AgentFactoryTests`
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter FullyQualifiedName~SessionExecutionTests`
  - `git diff --check`
- Acceptance contribution:
  - `M3-ACC-001`
  - `M3-ACC-005`
  - `M3-ACC-008`

### Outcome 4: OpenAI-compatible Chat Completions 适配器严格、有界且可脱机故障注入（已完成）

- Work:
  - 在 `OpenAiCompatibleChatClient.cs` 使用一个 WorkspaceRuntime 级共享
    `HttpClient` 和 `SocketsHttpHandler`；关闭自动重定向、Cookie、默认凭据和
    `HttpClient.Timeout`，保留平台默认代理、TLS 和证书验证。
  - 每个 Attempt 构造独立 `HttpRequestMessage`，只发送冻结的
    `model/messages/stream/include_usage/max_tokens` 和 Bearer Secret；释放请求、
    响应和流，不调用全局 `CancelPendingRequests()`。
  - 使用 `ResponseHeadersRead` 和严格 UTF-8 增量 SSE 解析；实现 1 MiB Event、
    16 MiB 解压后 Body、4 MiB Content+Reasoning 和整 Delta 拒绝边界。
  - 只接受单 Choice `index=0`、Content/Reasoning 字符串/null、合法 Usage、
    精确 Finish Reason 和 `[DONE]`；协议错误统一
    `provider.invalidStream` 且永不重试。
  - 非成功 Body 最多解压后 64 KiB，状态优先、精确解析
    `error.{code,type,param,message}`；实现冻结的 HTTP、Qwen prompt-too-long、
    Retry-After、TLS、重定向和稳定错误码映射。
  - 使用测试内 BCL Loopback Server 覆盖分片 UTF-8、多 `data:`、心跳、提前 EOF、
    畸形 JSON、Usage 冲突、压缩超限、重定向、自签名证书和错误 Body Canary。
  - 测试只断言归一化事件、错误、字节上限和资源释放，不保存原始 Prompt、响应正文
    或真实 Secret。
- Risks/open questions:
  - `insufficient_system_resource` 只有在完整读取 Usage 和 `[DONE]` 后才映射
    `provider.serverUnavailable`；中途断流仍是 `provider.invalidStream`。
  - 解压后限制必须在文本或 JSON 全量缓冲前生效。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter FullyQualifiedName~ChatCompletionClientTests`
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter FullyQualifiedName~StructuredLoggingTests`
- Acceptance contribution:
  - `M3-ACC-002`
  - `M3-ACC-003`
  - `M3-ACC-004`

### Outcome 5: AgentRuntimeExecutor 完成流式 Item、Usage、重试、取消和唯一终态（已完成）

- Work:
  - 令 `AgentRuntimeExecutor` 成为唯一生产 `ISessionExecutor`；复用 M2 的
    `SessionExecution` 和 Sink，不接触 Journal、Projection 或可变 Session Aggregate。
  - 以首个非空 Delta 创建 Content/Reasoning Item，逐 Delta 提交；Finish Reason、
    空响应、内容过滤、Tool Call、输出超限和错误均按冻结错误码终结活动 Item/Turn。
  - `Length` 完成 Turn 并追加 `response.truncated` SystemNotice；Failed/Cancelled
    Turn 的部分输出只供审计，不进入之后的 Model History。
  - 每个用户 Turn 只有一个 Invocation，摘要、正式回答和重试共享三次 Provider
    调用；Attempt Number 唯一，退避为 250 ms/1 s，并受 Retry-After 30 s、
    Attempt Deadline、流空闲 120 s 和 Invocation Deadline 30 m 共同约束。
  - 重试分界以首个 Content/Reasoning Delta 的 Journal 提交完成为准；此前只重试
    2.9 白名单瞬态错误，此后任何错误只保留部分输出并失败。
  - 每次调用以 `(InvocationId, AttemptNumber, Purpose)` 原子记录 Usage；真实值
    权威，成功缺失时才写显式本地估算，失败缺失时不合成；重复相同值幂等、冲突值
    以协议错误失败。
  - 使用 `TimeProvider`、受控事件和 Fake Client 测试全部时序，不使用
    `Thread.Sleep`；用户取消、Runtime 停止和内部 Deadline 形成各自正确终态。
- Risks/open questions:
  - Invocation Deadline 耗尽时不得因错误仍属“瞬态”而启动新 Attempt。
  - 首个可见 Delta 的提交回执必须来自 Session Sink，不能以“已从网络读取”替代。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter FullyQualifiedName~AgentRuntimeExecutorTests`
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter FullyQualifiedName~SessionExecutionTests`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter FullyQualifiedName~SessionCrashRecoveryIntegrationTests`
- Acceptance contribution:
  - `M3-ACC-003`
  - `M3-ACC-004`
  - `M3-ACC-007`

### Outcome 6: Micro/Partial Compaction 与响应式压缩在重启后保持相同历史（已完成）

- Work:
  - 在本地预算达到 80% 时先执行确定性 Micro Compaction，只清理已冻结的安全
    低价值内容；降到 60% 以下则直接继续，不调用 Provider。
  - Micro 后仍超限时，用 `opencowork.compaction.v1` 对可压缩的最旧完整 Turn
    范围做 Partial Compaction，目标不超过可用输入预算 60%，保留足够的最近历史。
  - 摘要只接受五个固定标题、唯一顺序、非空正文、`Stop`、有效哈希和目标水位；
    中间流只在内存中，失败时丢弃且不改变既有 Checkpoint。
  - 摘要、Source Sequence Start/End、来源消息哈希、Prompt/Tokenizer 版本和替换
    Model History 作为单个 Journal 事实提交；重放只使用最新有效 Checkpoint，
    原始历史与旧 Checkpoint 保留审计。
  - Provider 返回精确 prompt-too-long 且尚无可见输出时，只执行一次响应式
    Partial Compaction，把输入压到不超过 50%，随后在同一三次调用预算内重试；
    当前 UserMessage 始终只出现一次。
  - 当前输入自身超窗以 `context.inputTooLarge` 失败；摘要无效、达不到水位或重试
    后仍超窗以 `context.compactionFailed` 失败，不截断输入、不进入无限循环。
  - 测试覆盖 80%/60%/50% 边界、预算恰好相等、三次调用组合、旧 Checkpoint 扩展、
    崩溃重放、哈希破坏和 Usage 去重。
- Risks/open questions:
  - Compaction 不能删除或重写 Journal 原始事实，也不能把摘要链作为多条历史注入。
  - 摘要调用失败不得消耗之外的第四次“修复摘要”调用。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter FullyQualifiedName~CompactionTests`
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter "FullyQualifiedName~SessionProjectionTests|FullyQualifiedName~SessionRecoveryTests"`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter FullyQualifiedName~SessionCrashRecoveryIntegrationTests`
- Acceptance contribution:
  - `M3-ACC-005`
  - `M3-ACC-006`
  - `M3-ACC-007`

### Outcome 7: `opencowork chat` 只经 ISessionService 完成多轮、恢复和模式切换（已完成）

- Work:
  - 在 `Program.cs` 增加 `chat` 命令及
    `--workspace/--config/--set/--strict-config/--thread/--provider/--model`；
    Provider 与 Model 只能成对出现，恢复只接受精确 Thread ID。
  - Chat 路径加载唯一 Effective Config、冻结 Secret、注册 Agent Runtime 后再复用
    现有 SessionModule；不创建 `AgentModule`，因为 Agent 没有独立生命周期或宿主。
  - 新 Thread 在创建前完成最终 Provider/Model/Secret/Tokenizer 预检；恢复 Thread
    的显式模型切换通过 `SetThreadModelAsync` 原子提交，失败保持原选择。
  - 交互与重定向输入共用一个有界 UTF-8 单行读取器；256 KiB 前停止构造字符串，
    正确处理空行、NUL、非法 Unicode、`//` 转义和仅交互模式的
    `/exit`、`/mode agent`、`/mode plan`。
  - CLI 只订阅 Journal 提交后发布的 Session Event：Content 写 stdout，
    Reasoning/状态/安全错误写 stderr；非 TTY 与 `NO_COLOR` 不输出 ANSI。
  - 第一次 Ctrl+C 幂等取消当前 Turn 并等待终态；空闲时退出；重定向模式取消后以
    非零码退出，不提供强杀 Running Turn 的第二次 Ctrl+C 旁路。
  - 集成测试使用 Fake Provider 完成新 Thread、多轮、精确恢复、模型/模式切换、
    Queue Mode 冻结、输出隔离、重定向失败、重启恢复和取消；不访问真实网络。
- Risks/open questions:
  - CLI 不得直接读取 Journal、Projection 或维护第二份 Thread/Turn 状态。
  - stdout 只能包含 Content；Thread 信息、Reasoning、诊断和 Secret-safe 状态都在
    stderr。
- Verify:
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter FullyQualifiedName~ChatCliIntegrationTests`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter FullyQualifiedName~RuntimeCompositionIntegrationTests`
  - `dotnet run --project src/OpenCoWork.App/OpenCoWork.App.csproj -- chat --help`
- Acceptance contribution:
  - `M3-ACC-001`
  - `M3-ACC-002`
  - `M3-ACC-003`
  - `M3-ACC-008`

### Outcome 8: DeepSeek 官方真实验证和 M3 统一收口（已完成）

- Work:
  - 默认离线回归执行 Provider Contract、SSE、重试、Compaction、Secret、安全和
    CLI 测试，不访问公网或真实凭据。
  - 真实发布 Runner 保持显式启用并只在进程内读取安全凭据；缺失任一路径时记录
    `NotRun` 且阻止对外宣称支持，不把普通开发/CI 标记为失败。
  - 在 `osx-arm64` 运行 DeepSeek 官方的 `deepseek-v4-pro` 和
    `deepseek-v4-flash` 两条短冒烟。
  - 每条真实路径只断言 `[DONE]`、非空 Content、归一化 Finish Reason、真实 Usage
    和 Tokenizer 对账；Reasoning 代表只用 `deepseek-v4-flash`，不快照回答或
    思考正文。
  - Fake Canary 覆盖成功、认证、重试、错误 Body 和流中断；真实 Runner 扫描
    Journal、SQLite、Session Event、结构化日志、stdout、stderr 和测试产物，
    只输出命中/未命中，不保存 Secret 原值。
  - 发布证据只保留 Commit SHA、RID、Provider 路径、精确 Model ID、UTC 时间、
    Usage、Finish Reason 和 Pass/Fail/NotRun；不保留 Base URL 敏感部分、Prompt、
    回答正文或原始响应。
  - Apple Silicon macOS 完成 restore、Release build、完整 test、
    framework-dependent publish 和发布目录 CLI 实跑；解释所有
    skipped/explicit/not-run 项及临时资源清理结果。
  - 千问 Token Plan、其他 Provider 和 `win-x64` 真实兼容性进入
    `docs/provider-validation-backlog.md`，激活时再编写对应发布测试。
  - 用实际测试类、命令和结果把 `M3-ACC-001` 至 `M3-ACC-008` 从 Planned 更新为
    Passed；随后同步里程碑 CHECKLIST/INDEX，并生成唯一 M3 交付归档。
- Real-provider evidence（2026-07-27）:

  | Commit SHA | RID | Provider 路径 | 精确 Model ID | UTC 时间 | Usage（Prompt / Completion / Total） | Finish Reason | 结果 |
  | --- | --- | --- | --- | --- | --- | --- | --- |
  | `3da2e47f1a917529e3264535b7f9efed66d1b2bb` | `osx-arm64` | `deepseek-official` | `deepseek-v4-pro` | `2026-07-27T15:18:05.857672+00:00` | `142 / 18 / 160` | `stop` | Pass |
  | `3da2e47f1a917529e3264535b7f9efed66d1b2bb` | `osx-arm64` | `deepseek-official` | `deepseek-v4-flash` | `2026-07-27T15:18:07.291915+00:00` | `144 / 26 / 170` | `stop` | Pass |

  2026-07-28 用户确认 DeepSeek 官方是 M3 的首个真实 Provider；千问 Token Plan
  和 `win-x64` 真实兼容性不再阻止 M3，统一进入独立待验证清单。
- Completion evidence（2026-07-28）:
  - Release build 为 `0` warning / `0` error。
  - 完整离线回归为 Core `147`、Integration `18`、Generators `14`、
    Architecture `4`，合计 `183` passed / `0` failed；显式真实 Provider Runner
    按设计跳过。
  - `OpenCoWork.Protocol.Tests` 仍是无可发现测试的冻结项目壳，不计作通过或跳过。
  - `osx-arm64` framework-dependent publish 成功。
  - DeepSeek-only Runner 在强制清空凭据时只产生 Pro/Flash 两条 `NotRun`，没有
    Token Plan 占位，也没有发起网络请求。
  - `scripts/setup-deepseek-env-macos.zsh` 的语法、临时设置和 `--clear` 分支均通过
    无 Secret 的模拟验证。
- Risks/open questions:
  - 真实 Provider 请求属于显式发布操作；只有操作者提供对应凭据和执行授权时运行。
  - 任一 DeepSeek 官方路径 `NotRun`、Secret Canary 命中或 Tokenizer 对账失败都
    阻止 M3 标记 Done。
- Verify:
  - `dotnet restore OpenCoWork.slnx`
  - `dotnet build OpenCoWork.slnx -c Release --no-restore`
  - `dotnet test OpenCoWork.slnx -c Release --no-build`
  - `dotnet format OpenCoWork.slnx --verify-no-changes --no-restore`
  - `dotnet publish src/OpenCoWork.App/OpenCoWork.App.csproj -c Release -r win-x64 --self-contained false`
  - `dotnet publish src/OpenCoWork.App/OpenCoWork.App.csproj -c Release -r osx-arm64 --self-contained false`
  - 在安全凭据和显式发布开关下运行
    `ProviderReleaseValidationTests`，生成两条 DeepSeek 官方结果
  - 发布目录中的 `opencowork --version`、`doctor --json`、`chat --help` 和
    Fake Provider 多轮恢复场景
  - `git diff --check`
- Acceptance contribution:
  - `M3-ACC-001` 至 `M3-ACC-008`

## M3 Completion Gate（已满足）

只有同时满足以下条件才能关闭 M3：

- 八个 Outcome 的聚焦测试和完整累计回归全部通过；
- `M3-ACC-001` 至 `M3-ACC-008` 均从 Planned 更新为 Passed，并链接实际证据；
- 默认测试无公网依赖，所有真实 Provider 测试只能显式运行；
- `osx-arm64` 完成两条 DeepSeek 官方真实短冒烟，无 `NotRun`；
- 内置 Tokenizer Profile 的 Token ID 和资产 SHA-256 离线校验通过，DeepSeek
  两个 Profile 的 Prompt Usage 真实对账通过；
- Secret Canary 未命中 Journal、SQLite、Session Event、日志、stdout、stderr
  或测试产物；
- Prompt Golden、SSE/HTTP 故障矩阵、首个可见增量重试边界、Usage 去重、
  80%/60%/50% Compaction 和重启恢复均通过；
- 没有未解释的 skipped test、后台任务、HTTP 连接、临时证书、测试服务器、
  Journal、SQLite 或临时目录残留；
- M3 交付归档、里程碑 CHECKLIST 和 INDEX 同步完成；
- 千问 Token Plan、其他 Provider 和 `win-x64` 真实兼容性已进入
  `docs/provider-validation-backlog.md`；
- 根目录 DotCraft 证据文档仍被忽略，未进入任何提交。
