# OpenCoWork M6 Capability Ecosystem 实施计划

**Status:** In progress；Outcome 1-2 已完成，Outcome 3-10 未开始。

**Goal:** 在现有 Workspace、Agent、Tool、Journal、SQLite 和 Wire 边界上交付
Desktop-first 的工作区级 Capability Ecosystem，使受信来源能够被确定性发现、组合、
冻结、调用、撤销和清理。

**Why planning is required:** M6 同时修改工作区生命周期、Agent/Tool 快照、外部进程、
进程内插件、OS Secret Store、SQLite Schema、Wire 协议和双平台发布证据；错误的实施
顺序会产生第二状态机、失效的旧快照或无法回收的外部能力。

**Acceptance:** `M6-ACC-001` 至 `M6-ACC-010` 都有可复现证据；Catalog、Snapshot、
Trust 与 Live Binding 的权威关系符合设计；Wire 1.0 无回归且 Wire 1.1 通过黑盒
TestClient；macOS arm64 与 Windows x64 真机分别完成 M6 发布验证。

对应规格：
[M6 Capability Ecosystem 详细设计](../specs/2026-07-29-open-cowork-m6-capability-ecosystem-design.md)

验收目录：
[M0-M10 验收目录](../specs/2026-07-25-open-cowork-m0-acceptance-catalog.md)

## 执行规则

- 只在 `dev` 分支按 Outcome 顺序实施；每个 Outcome 结束时保持解决方案可构建、现有
  测试不退化；
- 不新增生产项目；公共契约进入 `OpenCoWork.Abstractions`，能力控制面进入
  `OpenCoWork.Core`，Wire 适配进入 `OpenCoWork.Protocol`，宿主组合留在
  `OpenCoWork.App`；
- 复用 `WorkspaceRuntime`、`AgentFactory`、`ToolRuntime`、
  `ToolInvocationPipeline`、`ThreadJournal`、`StateRuntime` 和现有配置校验/原子写
  模式，不建立平行生命周期、工具管线或数据库；
- 只创建跨程序集边界确实需要的 `ICapabilityService` 和设计已冻结的
  `IOpenCoWorkPlugin`；其余单实现逻辑优先使用具体类型；
- 新增依赖只允许 Outcome 7 的官方 `ModelContextProtocol.Core 2.0.0`。若最小包无法
  覆盖已冻结的客户端 transport/lifecycle，先停止并修订计划，不追加 Hosting、
  ASP.NET Core MCP 或其他包装库；
- M6 Provider 只使用 Fake OpenAI-compatible Server；没有用户重新激活的真实
  Provider、模型和平台，不新增兼容性声明；
- Secret 不进入配置、Journal、SQLite、日志、诊断、stdout/stderr、测试快照或
  Memory；所有边界测试都带 Secret Canary；
- Outcome 10 以前不关闭双平台 Acceptance；cross-publish 只能记录产物生成。

### Outcome 1：建立 Capability 基础契约与工作区控制面

- Work:
  - 在 `src/OpenCoWork.Abstractions/CapabilityContracts.cs` 定义冻结设计需要的
    Capability Kind、Status、Source、Contribution、Catalog Summary/Detail、
    Revision、Diagnostic、Snapshot Lease 和稳定业务错误；复用现有 Tool、Agent、
    Provider 和 Wire 契约，不复制其 DTO；
  - 在 `src/OpenCoWork.Core/Capabilities/` 实现唯一
    `WorkspaceCapabilityRuntime`：旁路构建候选、完整校验、确定性 hash、冲突隔离、
    单调 Revision、原子发布和 live binding registry；
  - Core 始终优先；外部来源之间发生相同 ID 冲突时全部隔离，不按扫描顺序覆盖；
  - 把 Runtime 生命周期接入现有 `WorkspaceRuntime`/Session 组合：接受 Turn 前能力
    Runtime 已启动，停止时先拒绝新 Turn，再按设计顺序撤销和清理；
  - 暂时只发布 Core Contribution，不为后续来源创建空 executor 或通用 source
    framework。
- Risks/open questions:
  - 相同候选 Catalog 复用 Revision，但 binding generation 变化必须产生新
    Revision；
  - 可选来源失败只能使 Runtime `Degraded`；只有 Core Catalog 不变量失败才进入
    `Faulted` 并拒绝新 Turn。
- Verify:
  `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~CapabilityCatalogTests|FullyQualifiedName~WorkspaceCapabilityRuntimeTests|FullyQualifiedName~RuntimeContractTests'`
  与
  `dotnet test tests/OpenCoWork.ArchitectureTests/OpenCoWork.ArchitectureTests.csproj -c Release`。
- Acceptance contribution:
  `M6-ACC-008`、`M6-ACC-010` 的 Catalog、冲突、生命周期和清理基线。

### Outcome 2：落地持久化权威、严格配置与恢复

- Work:
  - 在现有 `StateRuntime` migration 链增加 SQLite v5，只创建
    `capability_catalog_state`、`deferred_tool_activations`、
    `workspace_memories`、`workspace_memory_versions` 和 `terminal_sessions`
    及必要索引；
  - 复用现有备份、事务和 fault injection；不创建 Plugin、Trust、MCP、LSP、
    Auth、Binding、Lease 或 Executor 表；
  - 扩展 `WorkspacePaths` 和现有配置加载/原子写边界；本 Outcome 完整落地
    `.opencowork/plugins.lock.json`、Workspace/User Capability Override 与 Trust
    Schema，各领域配置 Schema 跟随其实际消费者在 Outcome 4、7、8 落地；
  - 实现 `~/.opencowork/trust/decisions.json` 的 scoped allow/deny、digest
    失效和用户 disable floor；Trust 仍不代替 Tool Authority；
  - 所有可写 JSON 使用同目录临时文件、flush 和原子替换；启动时从文件、SQLite
    receipt 和内容存储重建内存状态，不把内存 Catalog 当恢复权威；
  - 只实现 persistence primitives；Package 安装、Memory 内容和 Terminal 进程分别
    留给 Outcome 5、9。
- Risks/open questions:
  - 文件系统移动成功但 receipt/lock 提交失败时，恢复必须得到旧状态或完整新状态，
    不能发布半套 Catalog；
  - macOS 默认大小写不敏感不能替代显式 Unicode/Case Collision 检查。
- Verify:
  `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~StateRuntimeTests|FullyQualifiedName~StateMigrationV5Tests|FullyQualifiedName~CapabilityPersistenceTests|FullyQualifiedName~ConfigurationPipelineTests'`。
- Acceptance contribution:
  `M6-ACC-002`、`M6-ACC-004`、`M6-ACC-009` 的持久化、授信和恢复基线。

### Outcome 3：把 Capability Revision 接入 Turn 冻结与实时 Binding

- Work:
  - 扩展 `AgentInvocationSnapshot`，保存 `CapabilityRevision`、
    `EffectiveSkillSnapshot` 和现有 `EffectiveToolSnapshot`；继续持久化在
    `agent_invocations.snapshot_json`；
  - 修改 `AgentFactory`，只在 Turn 首次执行时从已发布 Catalog 冻结 Skill/Tool
    Snapshot；恢复执行只使用 Journal 中的原始快照，不重新扫描当前 Catalog；
  - 扩展现有 `ToolRuntime` 支持已冻结的 Tool Source Kind，并把冻结定义与实时
    binding resolution 分离；本 Outcome 保持现有 Direct exposure，Deferred 留给
    Outcome 6；
  - 保持 `EffectiveToolSnapshot -> ToolInvocationPipeline -> RuntimeBinding`
    路径；Pipeline 每次调用实时校验 binding generation、lease 和 trust，旧快照
    不等于旧 binding 永远可执行；
  - Response Prompt 注入 Skill Snapshot；Compaction Prompt 永不注入 Skill；
  - 更新 snapshot hash、Journal 序列化、重启恢复和 mismatch 诊断。
- Risks/open questions:
  - Catalog refresh 只影响下一 Turn；当前 Turn 的定义不变，但被撤销、断连或过期的
    live binding 必须立即失败；
  - 不增加第二个 ToolRuntime 或冻结 runtime binding 的字典副本。
- Verify:
  `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~AgentFactoryTests|FullyQualifiedName~ToolSnapshotTests|FullyQualifiedName~ToolInvocationPipelineTests|FullyQualifiedName~SessionRecoveryTests|FullyQualifiedName~CompactionTests'`。
- Acceptance contribution:
  `M6-ACC-003`、`M6-ACC-007`、`M6-ACC-010` 的旧 Turn/新 Turn 语义。

### Outcome 4：实现 Skills 与声明式 Provider/Auth

- Work:
  - 在 `src/OpenCoWork.Core/Capabilities/` 实现 Skill 发现、严格简化
    frontmatter parser、稳定 ID、显式 Variant、Thread > Workspace > User > base
    优先级、用户 disable floor 和确定性顺序；
  - 强制单 Skill 64 KiB、单 Turn 总计 1 MiB；超限或格式错误只隔离来源并产生脱敏
    Diagnostic；
  - 实现内置 `skill.load`，继续走现有 Tool Snapshot、Authority 和 Pipeline；
  - 把 `.opencowork/providers.json` 的 OpenAI-compatible Provider/Model 声明映射到
    现有 `ProviderRegistry`/HTTP client，不添加 native provider adapter；
  - 实现 Auth Profile 与每 Turn secret lease；macOS 使用 Keychain Services，
    Windows 使用 Credential Manager，测试使用内存 fake，不允许 shell 或明文
    fallback；
  - 移除启动期 `FrozenProviderCredentials` 权威，改为 AgentFactory 创建 Turn 时解析
    auth reference、短时持有并在 client materialization 后释放；
  - 用 Fake OpenAI-compatible Server 覆盖 streaming、tool calls、usage 和全链路
    redaction。
- Risks/open questions:
  - frontmatter 只支持设计列出的简单键值和列表，不引入 YAML 依赖、include 或模板；
  - OS Secret Store 的真机互操作只能在对应平台验收，另一平台的 fake 通过不能替代。
- Verify:
  `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~SkillCatalogTests|FullyQualifiedName~ProviderAuthTests|FullyQualifiedName~AgentFactoryTests|FullyQualifiedName~ChatCompletionClientTests'`
  与
  `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter 'FullyQualifiedName~CapabilityProviderIntegrationTests'`。
- Acceptance contribution:
  `M6-ACC-001`、`M6-ACC-003`。

### Outcome 5：实现 Plugin Package Store 与进程内 Executor

- Work:
  - 使用 BCL ZIP、`HttpClient` 和现有路径守卫实现本地/HTTPS package 解析、严格
    archive limits、Zip Slip/symlink/Unicode/Case Collision 防护、canonical content
    digest、内容寻址 store 和精确 lock；
  - 严格解析根目录 `opencowork.plugin.json`，只接受 `schemaVersion: 1`、
    `hostApiVersion: 1` 和显式 contribution paths；Marketplace 只解析到 artifact，
    不实现浏览、搜索、依赖求解、签名、自动更新、GC 或文件监听；
  - 实现最小 `IOpenCoWorkPlugin` 以及 collectible `AssemblyLoadContext` executor；
    禁止 root DI、HostedService、任意 Wire handler 和 native provider adapter；
  - Plugin Tool 直接映射现有 `ToolDefinition`、`ToolRuntimeBinding` 和
    `ToolRegistration`；不创建第二套 Plugin Tool 模型；
  - 每个已冻结 Snapshot 持有版本 lease；旧版本 lease 清零后才卸载，单 Plugin
    不同时运行两个版本；
  - 安装、lock、Catalog publish 任一步失败都保留旧版本；Trust digest 变化后新包
    只能进入 `PendingTrust`。
- Risks/open questions:
  - ALC unload 测试用 `WeakReference` 和有界 GC 验证；生产逻辑不能依赖一次 GC
    立即完成；
  - 无法卸载的进程内 Plugin 使 Runtime `Faulted`，恢复路径是 Desktop 重启。
- Verify:
  `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~PluginPackageTests|FullyQualifiedName~PluginRuntimeTests|FullyQualifiedName~CapabilityCatalogTests'`。
- Acceptance contribution:
  `M6-ACC-002`、`M6-ACC-004`、`M6-ACC-008`、`M6-ACC-010`。

### Outcome 6：实现 Deferred/Dynamic Tools 与 Hooks

- Work:
  - 给现有 `ToolExposure` 增加 `Deferred`，让 Agent Snapshot 同时冻结 Direct 与
    Deferred 定义，但 Provider 首次只看到 Direct；
  - 实现内置 `tool.search`：每次最多返回 8 个、每 Turn 最多激活 32 个；激活写入
    `DeferredToolsActivated` Journal fact，再由既有 projection/replay 路径恢复；
  - 实现 Thread + Wire connection scoped Dynamic Tool Registry：每连接每 Thread
    最多 64 个，默认 lease 30 秒、最大 5 分钟，断连立即撤销；
  - Dynamic Tool 先使用 Core 内的窄调用委托完成 Pipeline/lease 语义测试；Wire
    `tool/invoke` 适配延后到 Outcome 10；
  - 在现有 `PreToolUse` 与 `ToolTerminal` pipeline stage 接入 Hook：稳定来源顺序、
    Effect/Trust 严格交集、Pre fail closed、Terminal 只观察不可篡改结果；
  - Process Hook 每事件启动一个进程并通过 JSON stdio 交互；进程内 `.NET` Hook
    复用 Plugin executor 和 trust scope，不实现长期 Hook host、优先级或自定义事件。
- Risks/open questions:
  - Deferred activation 已写 Journal 但 projection 未更新时，恢复必须由 Journal
    重放得出相同集合；
  - Dynamic Tool 调用中断线时不能等待 lease 到期才失效，也不能把注册写入磁盘。
- Verify:
  `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~DeferredToolTests|FullyQualifiedName~DynamicToolTests|FullyQualifiedName~CapabilityHookTests|FullyQualifiedName~ToolInvocationPipelineTests|FullyQualifiedName~SessionRecoveryTests'`。
- Acceptance contribution:
  `M6-ACC-007`、`M6-ACC-008`。

### Outcome 7：实现 MCP 客户端来源

- Work:
  - 在 `Directory.Packages.props` 和 `OpenCoWork.Core.csproj` 只加入官方
    `ModelContextProtocol.Core 2.0.0`；先用 focused contract test 验证客户端 API、
    stdio、Streamable HTTP、取消和通知，失败则停止并修订计划；
  - 严格读取 `.opencowork/mcp.json`，实现一个 Workspace 一个 Server Session、
    stdio/Streamable HTTP transport、初始化、状态、取消和进程树清理；
  - 只发布 Tool、Resource、OAuth 和 Status；不声明 legacy SSE、Prompt、
    Sampling、Elicitation、Apps 或 server-side hosting；
  - MCP Tool 映射到现有 Tool Contract/Pipeline，Resource 使用少量显式 domain
    operation；OAuth credential 只通过 Outcome 4 的 Auth Profile/Secret Store；
  - 重连产生新的 connection/binding generation 和 Catalog Revision；M6 不做后台
    指数退避，恢复只由显式 Refresh/Restart 驱动；
  - 使用进程内 fake HTTP server 和可发布 fake stdio server 覆盖 handshake、
    list-changed、断连、取消、超时、半帧、恶意错误和清理。
- Risks/open questions:
  - 官方 SDK 只负责协议细节，Workspace 生命周期、Trust、Catalog、Lease 和
    redaction 仍由 OpenCoWork 控制；
  - HTTP OAuth redirect、token 和错误正文都必须通过统一 redactor 后才能进入
    Diagnostic。
- Verify:
  `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~McpCapabilityTests'`
  与
  `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter 'FullyQualifiedName~McpCapabilityIntegrationTests'`。
- Acceptance contribution:
  `M6-ACC-005`、`M6-ACC-010`。

### Outcome 8：实现只读 LSP 与 Git SourceControl

- Work:
  - 使用 `System.Text.Json`、`Process` 和现有有界 transport 模式实现 Workspace
    stdio LSP session；严格读取 `.opencowork/lsp.json`，按显式语言 selector 启动；
  - 只允许 design 中冻结的 initialize、documentSymbol、definition、references、
    hover、workspace/symbol 和 shutdown/exit 路径；文档事实始终来自磁盘，不支持
    unsaved buffer、写操作、TCP 或 model-wrapped LSP tool；
  - 断连或 refresh 产生新 generation；stop timeout 后终止进程树；
  - 使用 `ProcessStartInfo.ArgumentList` 调用受信 Git executable，实现
    status/diff/log/show 的高层只读 SourceControl 操作；不经过 shell，不引入 Git
    SDK，不实现 commit/pull/push/checkout/reset/merge；
  - Git 与 LSP 输出都使用 Workspace path guard、大小上限、超时、取消和统一
    redaction；
  - 使用 fake LSP server 和临时真实 Git repository 覆盖协议、dirty workspace、
    参数边界、恶意路径和进程回收。
- Risks/open questions:
  - LSP server 声明超出 allowlist 的能力只记录状态，不自动启用；
  - Git 参数必须保持参数数组边界，任何输入都不能拼成命令字符串。
- Verify:
  `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~LspCapabilityTests|FullyQualifiedName~SourceControlToolTests|FullyQualifiedName~WorkspacePathTests'`
  与对应进程级 IntegrationTests。
- Acceptance contribution:
  `M6-ACC-006`、`M6-ACC-009`。

### Outcome 9：实现 Background Terminal 与 Workspace Memory

- Work:
  - 在现有 Core Tool/Process 边界实现 Thread scoped
    `terminal.start/list/read/write/stop/release`；使用参数数组，不经过 shell 拼接，
    不支持 PTY、resize、重连或永久输出历史；
  - 强制每 Thread/Workspace session 数量、输入输出、运行时间、offset 和 ring
    buffer 上限；stop 终止进程树，release 只清理已停止/lost/exited metadata，不能
    遗弃运行进程；
  - 进程启动后 metadata 提交失败时立即终止进程；启动恢复把残留 Running 原子标记
    Lost；
  - 实现 Workspace Memory immutable blob + SQLite metadata/version，写入使用临时
    blob、fsync/rename 和 `expectedVersion` 乐观并发；
  - archive 只改变可见状态，不物理删除 blob；搜索使用确定性 metadata/text
    matching，不做自动 Prompt 注入、用户全局 Memory 或 embedding；
  - Terminal 与 Memory 都通过现有 Tool Snapshot、Authority、Policy、Approval、
    Hook 和 Journal 路径暴露，不增加旁路调用。
- Risks/open questions:
  - blob 已 rename 但 SQLite 未提交时保留 orphan，并由恢复/诊断识别；不能伪造已
    提交 Memory；
  - ring buffer 丢弃旧数据后 read 必须返回稳定 offset/error，不能静默伪装完整。
- Verify:
  `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~BackgroundTerminalTests|FullyQualifiedName~WorkspaceMemoryTests|FullyQualifiedName~ToolInvocationPipelineTests|FullyQualifiedName~StateRuntimeTests'`
  与
  `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter 'FullyQualifiedName~CapabilityProcessIntegrationTests|FullyQualifiedName~SessionCrashRecoveryIntegrationTests'`。
- Acceptance contribution:
  `M6-ACC-009`、`M6-ACC-010`。

### Outcome 10：接入 Wire 1.1 并完成黑盒与双平台验收

- Work:
  - 在 `WireContracts.cs` 增加 1.1 capability DTO、分页 Catalog、单项 read、少量
    domain operations、`expectedRevision` 和稳定 dotted error；保留 Wire 1.0
    descriptor、行为和测试；
  - 扩展 `OpenCoWorkJsonRpcConnection` 只依赖 `ISessionService` 与
    `ICapabilityService`，不直接读取文件、SQLite、Catalog 内存对象或 executor；
  - 复用 ACP connection 已有的 request correlation 模式实现 Wire 1.1 唯一
    server-to-client `tool/invoke`，只服务 Dynamic Tools；ACP v1 不增加能力；
  - initialize 协商双方最高共同版本；1.0 客户端看不到 1.1 方法和通知，1.1
    disconnect/cancel 会立即完成并撤销相关 Dynamic Binding；
  - 在 `OpenCoWork.App` 完成 Capability Runtime、Session Runtime、Protocol 和进程
    来源的启动/停止组合；
  - 扩展 `OpenCoWork.Protocol.TestClient`，覆盖 Wire 1.0 regression、1.1 catalog、
    revision conflict、refresh、dynamic callback、disconnect、cancel、慢读端、fault
    injection、Secret Canary 和子进程树回收；
  - 运行全量 build/test；分别发布 App 与 TestClient，并在 macOS arm64、Windows
    x64 真机验证 Secret Store、Git 参数边界、Terminal、stdio/WebSocket Wire 和
    Process Tree Cleanup；
  - 逐项回填 `M6-ACC-001` 至 `M6-ACC-010`，更新 capability/provider/platform
    ledgers、M6 delivery archive、Milestone Checklist 和 INDEX；未取得某平台真机
    证据时保持对应 Acceptance `Planned` 或显式 `Deferred`。
- Risks/open questions:
  - Wire 通知只报告 Catalog revision/status 变化，不镜像 Core 内部状态机；
  - 既有 M5 Windows 证据不能继承为 M6 Passed；每个平台必须新增独立 M6 evidence
    row；
  - Cross-publish、Fake Secret Store 或共享 Provider fixture 都不能代替对应真机或
    真实 Provider 证据。
- Verify:
  `dotnet test tests/OpenCoWork.Protocol.Tests/OpenCoWork.Protocol.Tests.csproj -c Release --filter 'FullyQualifiedName~OpenCoWorkJsonRpcTests|FullyQualifiedName~CapabilityWire'`；
  `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter 'FullyQualifiedName~ProtocolProcessIntegrationTests|FullyQualifiedName~Capability'`；
  `dotnet test OpenCoWork.slnx -c Release --no-restore`；
  `dotnet build OpenCoWork.slnx -c Release --no-restore`。
  对每个 RID 单独 restore/publish App 与 Protocol TestClient，再在目标真机运行完整
  M6 TestClient 矩阵，并把 commit、平台、SDK/runtime、产物摘要、命令和结果写入
  [双平台发布验证台账](../../platform-release-validation-ledger.md)。
- Acceptance contribution:
  关闭 `M6-ACC-001` 至 `M6-ACC-010`，或明确记录仍未满足的证据边界。

## 完成定义

M6 只有在以下条件同时满足后才能标记 Done：

- 10 个 Outcome 的实现、focused tests、fault injection 和全量回归全部通过；
- Wire 1.0 regression 与 Wire 1.1 黑盒场景全部通过；
- macOS arm64 与 Windows x64 真机都有独立 M6 evidence；
- `M6-ACC-001` 至 `M6-ACC-010` 均为 `Passed`；
- Secret 扫描无泄漏，所有外部进程和 collectible Plugin 都可按契约清理；
- Design、Plan、Acceptance Catalog、Capability/Provider/Platform Ledger、Archive、
  Milestone Checklist 与 INDEX 已同步。

若用户显式延期 Windows 真机证据，可以归档已完成实现并记录延期，但不得把 Windows
或完整 M6 验收标记为 `Passed`。
