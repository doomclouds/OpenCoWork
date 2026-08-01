# OpenCoWork M9 DeepSeek Responses Provider 实施计划

**Status:** Planned；实施前真实协议探针 Gate 尚未执行，M9 仍为 Not Started。

**Goal:** 用 DeepSeek 官方 Responses API 的专用最小实现替换现有通用
OpenAI-compatible Chat Completions 与千问 Token Plan 路径；首发只支持
`deepseek-v4-flash`，并在既有 Agent、Session、Tool、Approval、Audit、Replay 和
State 边界内交付 Function、服务端 `web_search`、`custom/apply_patch`、Usage、
重试、压缩和崩溃恢复闭环。

**Why planning is required:** M9 同时替换 Provider 传输协议、配置/Auth、模型目录、
领域事件、工具投影、重试提交点、模型历史、SQLite State v8、旧配置迁移、发布 Runner
和双平台真实兼容性证据。错误顺序会造成旧 Provider 静默回退、服务端搜索重复计费、
本地副作用重放、Patch 部分提交被误报成功、Usage 对账漂移或旧 Thread 越过迁移门联网。

**Acceptance:** `M9-ACC-011` 至 `M9-ACC-019` 都有可复现证据；默认回归完全离线；
`deepseek-v4-flash` 从 `osx-arm64` 与 `win-x64` 各自发布目录完成 Text、Function、
`web_search`、`custom/apply_patch`、Usage 和 Secret Canary；交叉发布不替代真机；
最终不残留通用 Provider SPI、OpenAI SDK、Chat Completions 兼容层、模型侧
`file.write` 或本地 `web.fetch`。

对应规格：
[M9 DeepSeek Responses Provider 设计](../specs/2026-08-01-open-cowork-m9-deepseek-responses-provider-design.md)

验收目录：
[M0-M11 验收目录](../specs/2026-07-25-open-cowork-m0-acceptance-catalog.md)

Provider 证据：
[Provider 真实兼容性验证清单](../../provider-validation-backlog.md)

双平台证据：
[双平台发布验证台账](../../platform-release-validation-ledger.md)

官方协议依据：

- [DeepSeek Responses API Guide](https://api-docs.deepseek.com/guides/responses_api/)
- [DeepSeek Error Codes](https://api-docs.deepseek.com/quick_start/error_codes/)
- [DeepSeek Codex Integration](https://api-docs.deepseek.com/quick_start/agent_integrations/codex/)

## 当前实现基线

- `dev` 当前 Git 基线为 `56351ce`；M9 Design 与 Acceptance Catalog 调整尚未形成
  实施基线提交；
- 当前产品仍由 `OpenAiCompatibleChatClient` 发送 `chat/completions`，以
  `ChatCompletionMessage`、独立 Usage Event 和 `[DONE]` 表达 Provider 传输；
- `ModelsConfig` 仍允许 `defaultProvider`、自定义 Provider/Base URL/API Key、
  Model/Tokenizer/Context 配置，`ProviderDeclarationCatalog` 仍读取
  `.opencowork/providers.json`；
- Tokenizer Catalog 仍登记 Qwen、GLM、DeepSeek Pro/Flash，M9 最终有效模型目录只能
  剩 `deepseek-v4-flash`；DeepSeek Tokenizer 资产继续复用，不下载运行时资产；
- `AgentRuntimeExecutor` 已具备三次 Attempt、`Retry-After` 上限、Invocation Budget、
  Content/Reasoning 流式 Item、Tool Loop、Compaction 和 Journal 恢复，应原位改造；
- `ToolRuntime`、`EffectiveToolSnapshot`、`ToolInvocationPipeline`、
  `WorkspacePathGuard`、Approval、Hook、Audit 和 Replay Protection 已是本地工具权威；
- Workspace State 当前为 v7；v8 只需重建 `items.item_type` 约束并允许
  `providerAction`，不新增表、索引或 Usage 列；
- `CoreFileTools` 已有严格 UTF-8、受保护路径、Hash 前置条件和单文件原子替换原语；
  `CoreWebTool` 与模型侧 `file.write` 仍在 Catalog；
- M0 已冻结项目集合；M9 只修改现有项目和测试程序集，不新增生产/测试工程，不新增
  NuGet 依赖。

## 计划变更图

优先删除旧路径并复用现有边界。文件可在不改变职责的前提下合并；不得为了计划表
机械制造一文件一类型。

### 主要新增或替换

| 路径 | 职责 |
| --- | --- |
| `src/OpenCoWork.Core/Agents/DeepSeekResponsesClient.cs` | 固定 DeepSeek `/responses` 请求、BCL SSE 状态机、官方错误映射和全部协议上限；替换并删除 `OpenAiCompatibleChatClient.cs`。 |
| `tests/OpenCoWork.Core.Tests/DeepSeekResponsesClientTests.cs` | BCL Loopback 合法/非法语义 SSE、三终态、HTTP、超时、Usage、工具事件和上限矩阵；替换旧 Chat Completion Client 测试。 |
| `tests/OpenCoWork.Core.Tests/StateMigrationV8Tests.cs` | v7→v8、`providerAction` 约束重建、兼容默认值、备份恢复和结构验证。 |
| `src/OpenCoWork.Core/Tools/CoreFileTools.cs` | 在既有文件安全原语上增加最小 `file.apply_patch` Binding；不增加 Shell、`git apply` 或第三方 Patch 库。 |

### 主要原位修改

| 路径 | 修改目的 |
| --- | --- |
| `src/OpenCoWork.Core/Configuration/ModelsConfig.cs` | 只保留 `defaultModel` 与 `reasoningEffort`；Provider、Endpoint、Tokenizer 和限制全部内建。 |
| `src/OpenCoWork.Core/Capabilities/ProviderCapabilities.cs` | 删除工作区 Provider 声明与 `openaiCompatible` 投影；保留非 Provider Auth 能力，增加内建 DeepSeek/Flash Catalog。 |
| `src/OpenCoWork.Core/Capabilities/ProviderSecretStore.cs` | 固定 `auth/deepseek`，按 `DEEPSEEK_API_KEY` → Workspace 隔离 OS Secret Store 取值。 |
| `src/OpenCoWork.Abstractions/AgentContracts.cs` | 仅保留跨程序集需要的 Invocation/Usage/错误契约；Purpose 改名、Usage 细分和 Snapshot 冻结 Reasoning Effort。 |
| `src/OpenCoWork.Abstractions/SessionContracts.cs` | 增加 `ProviderAction`、`ProviderCallKind` 和向后兼容内容模型。 |
| `src/OpenCoWork.Core/Agents/AgentRuntime.cs` | 无状态 Responses Input History、Text/Reasoning/Function/Custom/Web Search 语义事件、Attempt 提交点、Usage、错误和压缩。 |
| `src/OpenCoWork.Core/Sessions/` | 继续由 Journal/Execution/Projection/Recovery 权威持久化 Provider Action、Usage、工具调用和恢复事实。 |
| `src/OpenCoWork.Core/State/StateRuntime.cs` | State v8 原子迁移，只重建 `items` 约束。 |
| `src/OpenCoWork.Core/Tools/ToolRuntime.cs` | Function/Custom/Web Search 请求投影；发布 `file.apply_patch`，撤下模型侧 `file.write` 和本地 `web.fetch`。 |
| `src/OpenCoWork.App/`、`src/OpenCoWork.Protocol/`、`src/OpenCoWork.Automations/` | 固定 Provider ID 兼容、模型选择、恢复迁移门与现有 Wire/Automation 快照回归。 |
| `tests/OpenCoWork.IntegrationTests/ProviderReleaseValidationTests.cs` | 显式真实 Flash 六场景、Usage 对账、终态和全输出面 Secret Canary。 |
| `tests/OpenCoWork.Protocol.TestClient/Program.cs` | 发布目录离线回归与 M9 黑盒场景，不植入测试专用产品 Endpoint 配置。 |

## 执行规则

- 只在 `dev` 分支按 Outcome 顺序实施；本计划本身不授权真实外部请求；
- Outcome 1 前重新读取当前 Milestone README/CHECKLIST、M9 Design、路线规格、
  `DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md`、Provider Backlog 和
  双平台验证台账，并重新核对 DeepSeek 官方三份资料；
- 每个 Outcome 严格执行：Red Test → 确认因目标缺失而失败 → 最小实现 → focused
  tests → 全量 Release 回归 → 一个独立 Commit；
- 上一个 Outcome 未通过全量回归或仍有未提交实现，不开始下一个 Outcome；
- 复用现有 `HttpClient`、`Utf8JsonWriter`、有界 SSE 读取、`TimeProvider`、
  `ThreadJournal`、`AgentRuntimeExecutor`、`EffectiveToolSnapshot`、
  `ToolInvocationPipeline`、`WorkspacePathGuard` 和 State 迁移框架；
- 不增加 OpenAI SDK、Provider SPI/Factory、Responses DTO 镜像、第二套历史/状态机、
  第二条工具执行管线、Patch 依赖、HTTP 工厂、重试框架或测试工程；
- `DeepSeekResponsesClient` 是具体 Core 类型；Runtime 测试只用内部 Stream Delegate
  或注入 `HttpClient`/Handler，不把测试缝升级成公共 Provider 接口；
- Provider 固定为 `deepseek`，官方 Base URL 固定 `https://api.deepseek.com`，Client
  固定请求 `/responses`；只有测试构造可以注入回环 `HttpClient`，产品配置没有
  Endpoint 覆盖；
- `reasoningEffort` 只允许 `low` / `high` / `max`，默认 `high`；Response 与
  Compaction 使用同一 Invocation Snapshot 值；
- 默认测试不访问公网、不读取真实凭据；真实 Provider 只在用户显式激活、对应发布
  目录和临时 Workspace/User Profile 中执行；
- Secret、Prompt、完整响应、Reasoning 正文、Provider Call ID 和原始错误 Body 不得
  进入证据、日志、stdout/stderr、快照或测试产物；
- 旧 Thread/Journal 保持可读；旧 Provider/Model 的运行与崩溃恢复必须在联网和工具
  重放前失败，不自动改写成 Flash；
- Cross-publish 只证明产物可生成，不能把目标平台标为 Passed；
- 任一官方行为与 M9 Design 冲突时停止当前 Outcome，先更新 Provider Backlog、设计
  和计划，不增加“兼容猜测”分支。

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

## 实施前 Gate 0：真实协议探针

此 Gate 不单独形成 Outcome；证据与对应红测在 Outcome 1 的唯一 Commit 中落盘。

- 只有用户显式激活 `deepseek-v4-flash` 真实验证后，才从安全进程环境或既有 OS
  Secret Store 读取凭据并发起最小外部请求；
- 探针只冻结 DeepSeek 官方资料没有完整定义的两项：
  `custom/apply_patch` Input/Result 回注线格式，以及上下文过长 400 的精确响应签名；
- 对 Apply Patch 记录事件类型、字段名、顺序、必填关系、Call ID 关系和规范化后的
  SHA-256；不记录 Patch/Result 正文、响应 ID 或 Secret；
- 上下文 400 至少重复观察稳定结构后才能冻结精确匹配；无法取得或结构不稳定时，
  明确冻结为“不实现响应式 400 压缩”，继续依赖本地 Token 预算和主动压缩；
- 探针结果先更新 M9 Design 的证据注记和脱敏测试 Fixture；若结果改变冻结边界，
  停止 Outcome 1 并请求设计修订，不从 OpenAI/Qwen 行为推断。

### Outcome 1：冻结探针证据与 DeepSeek-only 配置/Auth/Catalog

- Red:
  - 扩展 `ConfigurationPipelineTests`、`ProviderAuthTests`、`AgentFactoryTests` 和
    `WorkspaceInitializerTests`，覆盖仅 `defaultModel`/`reasoningEffort`、默认
    `deepseek-v4-flash`、固定 `deepseek`/Endpoint/Tokenizer/限制和非法 Effort
    无网络；
  - 扩展 `CapabilityProviderIntegrationTests` 与 CLI 测试，证明
    `.opencowork/providers.json`、旧 `models.providers/defaultProvider`、Qwen、
    GLM、DeepSeek Pro、自定义 Base URL 和非 `deepseek` 的 `--provider` 都返回稳定
    迁移诊断且不创建 Thread、不触发 HTTP；
  - 覆盖 `DEEPSEEK_API_KEY` 优先、OS Secret Store 回退、Workspace 隔离、
    `auth/secret/set|clear` 与 Secret Redaction；`.opencowork/auth.json` 的 MCP OAuth
    行为保持不变。
- Work:
  - 将 `ModelsConfig` 收敛为内建 `deepseek-v4-flash` 的 `defaultModel` 和
    `reasoningEffort`，删除用户 Provider/Model/Tokenizer/Base URL/API Key 配置；
  - 将 Provider/Model Catalog 固定为 `deepseek` / `deepseek-v4-flash`，删除
    `openaiCompatible`、External Provider、Qwen/GLM/Pro 有效注册和未再使用的模型
    资产；共享的 DeepSeek Flash Tokenizer 资产继续固定 SHA-256；
  - 保留 Auth Profile 文件解析给 MCP/LSP；为 Provider 增加固定
    `auth/deepseek`，取值顺序为环境变量后 OS Secret Store；
  - CLI 保留 `--provider` 仅作 `deepseek` 精确兼容入口；Automation/Wire/Thread
    仍保存 Provider ID 和 Model ID，但新入口只接受固定组合；
  - 把 Gate 0 的脱敏协议证据写入设计注记和测试 Fixture；不提交真实响应正文或 ID。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConfigurationPipelineTests|FullyQualifiedName~ProviderAuthTests|FullyQualifiedName~AgentFactoryTests|FullyQualifiedName~WorkspaceInitializerTests|FullyQualifiedName~StructuredLoggingTests'`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~CapabilityProviderIntegrationTests|FullyQualifiedName~ChatCliIntegrationTests'`
- Acceptance contribution:
  - `M9-ACC-011`、`M9-ACC-014`、`M9-ACC-017`。
- Commit:
  - `feat(m9): freeze DeepSeek provider contract`

### Outcome 2：升级 Responses 领域契约、Journal Item 与 State v8

- Red:
  - 扩展 `AgentContractTests`、`SessionContractTests`、`SessionExecutionTests` 和
    `SessionProjectionTests`，覆盖 `ProviderInvocationPurpose` 的既有 JSON 值、
    `CachedPromptTokens` / `ReasoningCompletionTokens` 兼容默认值、Usage 约束、
    Snapshot Effort 冻结、`ProviderCallKind` 旧值默认 `Function` 和 Provider Action
    不可变状态序列；
  - 新增 `StateMigrationV8Tests`，覆盖新库直达 v8、v7→v8、`items` 数据原样复制、
    `providerAction` 约束、无新表/索引/列、备份、DDL/Commit 故障恢复和重复启动；
  - 扩展 Protocol/ACP 契约回归，证明 Provider Action 不破坏现有 Wire 版本和旧 Item
    读取。
- Work:
  - 将 `ChatCompletionInvocationPurpose` 改名为 `ProviderInvocationPurpose`，保持
    `response` / `compaction` 序列化值和 SQLite 主键语义不变；
  - 扩展 `ProviderUsageSnapshot` 与 `AgentInvocationSnapshot`，旧 Journal 缺失字段
    分别按 `0` 和 `high` 读取；Provider Usage 仍只写 `usage_json`；
  - 为 `ToolCallItemEntry` 增加向后兼容的 `ProviderCallKind`；
  - 增加 `ProviderAction` Session Item 和有界内容，只复用既有 Journal → Projection
    → Event 顺序，不增加 Provider Action 表或状态机；
  - 将 State 迁移链升到 v8，只按现有备份/事务/恢复路径重建 `items` 与依赖外键，
    允许 `providerAction` 后删除旧表。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~AgentContractTests|FullyQualifiedName~SessionContractTests|FullyQualifiedName~SessionExecutionTests|FullyQualifiedName~SessionProjectionTests|FullyQualifiedName~StateMigrationV8Tests|FullyQualifiedName~StateRuntimeTests'`
  - `dotnet test tests/OpenCoWork.Protocol.Tests/OpenCoWork.Protocol.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~OpenCoWorkJsonRpcTests|FullyQualifiedName~AcpConnectionTests'`
- Acceptance contribution:
  - `M9-ACC-014`、`M9-ACC-016`。
- Commit:
  - `feat(m9): add Responses state contracts v8`

### Outcome 3：实现严格、有界的 DeepSeek Responses 客户端

- Red:
  - 用测试内 BCL Loopback Server 建立官方合法 Fixture：`response.created` 首帧、
    非连续但严格递增 `sequence_number`、Text/Reasoning Delta+Done、Function
    Arguments、Custom Input、Web Search 状态和三类唯一终态；
  - 覆盖未知事件、重复/倒退序号、重复 Call ID、错误 `output_index + item.id`、
    Delta/Done 不一致、未关闭 Item、终态后事件、提前 EOF、`[DONE]`、无终态、非法
    UTF-8、压缩/帧/累计输出/完成搜索回放 Item/错误 Body 上限；
  - 覆盖固定 `/responses`、Bearer Auth、关闭 Redirect/Cookie、系统代理保留、
    `reasoning.effort`、无状态 Input Items、Response Header/Idle Timeout、TLS 和官方
    400/401/402/422/429/500/503 分类；其他状态默认稳定失败；
  - 覆盖终态 Usage 的 cached/reasoning/total 约束和 `response.failed` 有界脱敏 Detail。
- Work:
  - 新增具体 `DeepSeekResponsesClient`，复用/移动现有共享 `HttpClient` 与有界 SSE
    Reader；使用 `Utf8JsonWriter` 直接序列化已冻结子集，不复制官方完整 DTO；
  - Client 内部按 `output_index + item.id` 组装 Item，只向 Runtime 发 Text、完整
    Function/Custom、Web Search 和 Terminal 本地语义事件；Usage 只随 Terminal；
  - 固定请求不发送 `previous_response_id`、`conversation`、`store`、background 或
    产品未激活字段；
  - HTTP 仅 429/500/503 标记瞬态；Redirect、TLS、无效 SSE、未知事件和上限失败均
    不可重试；
  - 保留最少的内部测试 seam，不新增公共 `IResponsesClient`、Provider SPI 或 SDK。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~DeepSeekResponsesClientTests|FullyQualifiedName~StructuredLoggingTests'`
- Acceptance contribution:
  - `M9-ACC-012`、`M9-ACC-014`、`M9-ACC-015`。
- Commit:
  - `feat(m9): add DeepSeek Responses client`

### Outcome 4：切换 Agent Runtime 的 Text、Reasoning、Usage、终态与压缩

- Red:
  - 扩展 `AgentRuntimeExecutorTests`，覆盖 Text/Reasoning Item 流式提交、终态 Usage、
    `completed`、结构完整 `incomplete` 的部分输出 + `response.truncated`、`failed`
    的稳定错误和活动 Item 收敛；
  - 扩展 `CompactionTests`，覆盖 Response/Compaction 同一 Effort、配置变化只影响后续
    Invocation、主动压缩、本地 Token 预算和 Gate 0 冻结的精确 400 行为；未取得稳定
    签名时断言任何泛化 400 都不触发响应式压缩；
  - 覆盖三次 Attempt、`Retry-After` 30 秒上限、Invocation Budget，以及首个已持久化
    Output/Reasoning Delta 前可重试、之后不可重试；
  - 覆盖 Usage 只从 Terminal 对账，缺失时按现有本地估算规则记录且新增细分字段为
    `0`。
- Work:
  - 令 `AgentRuntimeExecutor` 直接消费 `DeepSeekResponsesClient` 的本地语义事件；
  - 将模型历史从 `ChatCompletionMessage` 改为官方支持的无状态 Input Items，Reasoning
    只回注当前活动 Turn 的工具循环/恢复，不回注已完成历史 Turn；
  - Response 与 Compaction 都发送 Snapshot 冻结的 `reasoning.effort`，保持现有
    80%/60%/50% Token 水位和 Invocation Attempt 预算；
  - 删除 Qwen 错误文案匹配；按 Gate 0 证据实现一个精确 Context 400 匹配，或完全
    不实现响应式 400 压缩；
  - 完成 Runtime 切换后删除 `OpenAiCompatibleChatClient`、Chat Completion 传输 DTO、
    `[DONE]` 和旧 Client 测试，不保留双协议分支。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~AgentRuntimeExecutorTests|FullyQualifiedName~CompactionTests|FullyQualifiedName~AgentFactoryTests|FullyQualifiedName~SessionExecutionTests'`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter FullyQualifiedName~SessionCrashRecoveryIntegrationTests`
- Acceptance contribution:
  - `M9-ACC-012`、`M9-ACC-014`、`M9-ACC-015`、`M9-ACC-016`。
- Commit:
  - `feat(m9): run agents on Responses API`

### Outcome 5：接通 Function 多轮工具循环与副作用提交边界

- Red:
  - 扩展 `AgentRuntimeExecutorTests` 与 `ToolRuntimeIntegrationTests`，覆盖多个 Function
    Call、跨帧 Arguments、Call ID/顺序、Function Output 回注、连续 Provider Round、
    Deferred Tool 激活和 64 Round 上限；
  - 覆盖重复 Call ID、名称/Arguments 冲突、未知工具、坏 JSON、输入 Schema、审批、
    拒绝、失败、取消和 Result Envelope 上限；
  - 在 Function 仅完成组装、写入 ToolCall Item、真正进入 `ToolInvocationPipeline`
    前后分别注入断流，证明只有本地工具实际尝试后 Attempt 才已提交；
  - 扩展崩溃恢复测试，证明恢复复用原 Tool Call/Result，不重复副作用，旧
    `ProviderCallKind` 缺失按 Function 读取。
- Work:
  - 将 `EffectiveToolSnapshot` 中可见本地工具投影为 Responses `function`，保持
    Canonical↔Provider Name、Deferred Activation 和 Snapshot SHA-256；
  - 完整 Function Call 经 Client 校验后记录同一 ToolCall Frame，继续只调用现有
    `ToolInvocationPipeline`；
  - 将规范 `function_call` / `function_call_output` Item 加入当前无状态请求历史；
  - 统一 Attempt 提交判定：协议组装不提交，本地工具实际进入执行尝试后提交；
  - 不在本 Outcome 实现 `custom/apply_patch` 或 `web_search`。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~AgentRuntimeExecutorTests|FullyQualifiedName~ToolSnapshotTests|FullyQualifiedName~ToolInvocationPipelineTests|FullyQualifiedName~DeferredToolTests'`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ToolRuntimeIntegrationTests|FullyQualifiedName~SessionCrashRecoveryIntegrationTests'`
- Acceptance contribution:
  - `M9-ACC-013`、`M9-ACC-015`、`M9-ACC-016`。
- Commit:
  - `feat(m9): route Responses function calls`

### Outcome 6：交付 `custom/apply_patch` 并撤下模型侧 `file.write`

- Red:
  - 扩展 `CoreToolTests`、`ToolSnapshotTests` 和 `ToolInvocationPipelineTests`，覆盖
    `file.apply_patch` 的 Canonical Tool 与固定 `custom/apply_patch` Provider 投影、
    Agent/Plan、Allow/RequireApproval/Deny、ReplaySafety.Unsafe 和 512 KiB 上限；
  - 按 Gate 0 Grammar Fixture 覆盖新增/修改/删除/移动、多个 Hunk、CRLF/LF、空文件、
    非 UTF-8、坏语法、重复路径、绝对/越界/符号链接/受保护路径、旧 Hash、Context、
    新文件冲突和删除/移动前置条件；
  - 在首个写入前的每个校验点注入失败，证明整包零副作用；在单文件临时写、Flush、
    Replace/Move 注入故障，证明单文件原子结果；在跨文件第 N 个提交点故障，证明返回
    `tool.outcomeUnknown`、已提交/未提交路径且禁止 Replay；
  - 证明 Result 只含操作、路径、前后 Hash 和状态，且模型 Catalog 不再出现
    `file.write`。
- Work:
  - 在现有 Core File Tool 边界实现 Gate 0 冻结的最小 Patch Parser/Applier，复用
    Workspace Path Guard、UTF-8、Hash 前置条件和临时文件原子替换；
  - 全包先解析和预检，再按稳定路径顺序提交；不启动 Shell、不调用 `git apply`、
    不引入依赖、不承诺跨文件原子事务；
  - 将完整自由格式输入包装成 `{"patch":"..."}` 后进入现有 Tool Snapshot、
    Approval、Hook、Audit 和 Replay Protection；
  - 将完整 Custom Result 按探针冻结格式回注 Provider，日志/Journal 只保留既有有界
    Tool Result Envelope；
  - 从模型 Catalog 移除 `file.write` Registration/Binding；底层安全写入原语只作为
    Patch 实现细节保留。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~CoreToolTests|FullyQualifiedName~ToolSnapshotTests|FullyQualifiedName~ToolInvocationPipelineTests'`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ToolRuntimeIntegrationTests|FullyQualifiedName~AutomationServiceTests'`
- Acceptance contribution:
  - `M9-ACC-013`、`M9-ACC-019`。
- Commit:
  - `feat(m9): add atomic apply patch tool`

### Outcome 7：交付受 Authority 约束的服务端 `web_search`

- Red:
  - 覆盖 `NetworkRead=Allow|RequireApproval|Deny` 请求 Snapshot，只有 Allow 包含
    `{"type":"web_search"}`；Plan/Automation/CoWork 的有效 Authority 继续取既有
    Snapshot 交集，不增加请求前审批状态；
  - 用官方 Fixture 覆盖 `inProgress` / `searching` / `completed` 三个不可变
    Provider Action、同一 Provider Call ID、仅 completed 保存规范化 ≤256 KiB 回放
    Item、后续请求只回注完成 Item；
  - 在首个 `in_progress` 前后断流，证明前者按白名单可重试、后者即使无文字也禁止
    重试；Provider Action 不进入 `ToolInvocationPipeline` 或本地 Tool Audit；
  - 覆盖压缩可丢弃旧搜索状态噪音、未压缩 completed Item 可恢复、进程重启不重复
    搜索；证明模型 Catalog 不再出现 `web.fetch` 或 CoreWebTool。
- Work:
  - 根据 `EffectiveToolSnapshot.Authority` 直接决定 Provider 请求是否投影
    `web_search`，RequireApproval/Deny 均省略；
  - 将 Web Search 生命周期写成不可变 `ProviderAction` Item，并把 completed
    官方回放 Item 加入无状态历史；
  - 首个 `in_progress` 的 Journal 提交回执成为 Attempt 已提交边界，不以网络读取
    时间代替持久化；
  - 从 Core Catalog 删除 `web.fetch` Registration/Binding 和 `CoreWebTool`，不保留
    本地搜索回退或双路径；
  - 保持 Provider Action 与本地 Tool Invocation 的审计类型分离。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~DeepSeekResponsesClientTests|FullyQualifiedName~AgentRuntimeExecutorTests|FullyQualifiedName~ToolSnapshotTests|FullyQualifiedName~SessionProjectionTests|FullyQualifiedName~SessionRecoveryTests'`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ToolRuntimeIntegrationTests|FullyQualifiedName~SessionCrashRecoveryIntegrationTests|FullyQualifiedName~AutomationRuntimeSnapshotTests'`
- Acceptance contribution:
  - `M9-ACC-012`、`M9-ACC-015`、`M9-ACC-016`、`M9-ACC-019`。
- Commit:
  - `feat(m9): use DeepSeek server web search`

### Outcome 8：关闭旧配置/历史恢复与全部产品入口

- Red:
  - 建立旧配置 Corpus，覆盖 `.opencowork/providers.json`、旧
    `models.defaultProvider/providers`、Qwen/GLM/Pro、自定义 Tokenizer/Base URL、
    旧环境变量和非 `deepseek` CLI；Config/Doctor/CLI/Capability 必须返回稳定迁移
    诊断且 HTTP 请求数为 0；
  - 扩展 `SessionRecoveryTests` 与崩溃恢复集成测试，证明旧 Provider/Model Thread
    可读但 Run/Resume 在联网和工具重放前失败；用户显式切到固定组合后只能开始新
    Turn，不能改写旧 Invocation；
  - 扩展 Automation/CoWork/Wire/ACP/Protocol Process 测试，覆盖冻结 Provider/Model
    快照、`--provider` 精确兼容、Provider Action 投影、旧 Wire 回归和无测试专用
    Endpoint 配置；
  - 扫描生产代码与发布内容，证明不存在 `chat/completions`、
    `openaiCompatible`、Qwen Token Plan、`[DONE]`、`web.fetch`、模型侧 `file.write`
    或可激活 Pro 路径。
- Work:
  - 将 Workspace Init、Doctor、Composition Root、Chat CLI、Protocol、ACP、Automation
    与 CoWork 的 Provider/Model 流统一到固定 DeepSeek/Flash 预检；
  - 迁移诊断在配置绑定、Thread Run/Resume 和恢复入口 fail closed，不静默忽略、别名
    映射或自动改写；
  - 更新现有 Fake/Loopback 测试 seam 和 Protocol TestClient，产品 Endpoint 仍固定；
  - 删除不再可达的 External Provider、旧 DTO、旧配置字段、旧模型资产、旧工具代码
    和测试夹具；只保留历史反序列化所需的最小兼容字段；
  - 完成 M9 全部离线 Fault/Recovery/Security/Migration Corpus，保持 Wire 1.0–1.3、
    M7/M8 Session/Automation 行为回归。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConfigurationPipelineTests|FullyQualifiedName~ProviderAuthTests|FullyQualifiedName~SessionRecoveryTests|FullyQualifiedName~CapabilityRuntimeTests|FullyQualifiedName~AutomationRuntimeSnapshotTests'`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ChatCliIntegrationTests|FullyQualifiedName~CapabilityProviderIntegrationTests|FullyQualifiedName~ProtocolProcessIntegrationTests|FullyQualifiedName~RuntimeCompositionIntegrationTests|FullyQualifiedName~SessionCrashRecoveryIntegrationTests'`
  - `dotnet test tests/OpenCoWork.Protocol.Tests/OpenCoWork.Protocol.Tests.csproj -c Release --no-restore`
  - `rg -n 'chat/completions|openaiCompatible|qwen|\[DONE\]|web\.fetch|file\.write' src tests/OpenCoWork.Protocol.TestClient`
- Acceptance contribution:
  - `M9-ACC-011`、`M9-ACC-016`、`M9-ACC-017`、`M9-ACC-019`，并形成
    `M9-ACC-012..015` 的跨入口回归。
- Commit:
  - `refactor(m9): remove legacy provider paths`

### Outcome 9：交付发布 Runner、离线关闭矩阵与 `osx-arm64` 真机证据

- Red:
  - 令 `ProviderReleaseValidationTests` 在未显式启用、Commit SHA 非法、Secret 缺失、
    任一场景 NotRun/Fail、Usage 不守恒、终态错误、Secret 命中或临时残留时无法产出
    Pass 证据；普通离线回归不访问网络；
  - 令 M9 Closeout 检查在九个 Acceptance、两平台、六场景、完整 Fault/Migration
    Corpus 任一缺失时保持 Planned/Pending；
  - M7/M8 的 Windows 待验收项保持独立，M9 证据不得覆盖或顺手关闭它们。
- Work:
  - 重写显式真实 Provider Runner，只接受固定 `DEEPSEEK_API_KEY`、
    `deepseek-v4-flash` 和官方 Endpoint；每次使用临时 Workspace/User Profile；
  - 真实矩阵分别执行 Text、Function、Web Search、Apply Patch、Usage 对账和 Secret
    Canary；证据只保存 Commit、RID、OS/runtime、模型/API、场景、终态、Token 数、
    时间和 Pass/Fail，不保存 Prompt、正文、Reasoning、Call/Response ID；
  - 运行全部离线 Release test/build/format、State v8 迁移/故障、SSE Fault Matrix、
    Authority/Patch/恢复 Corpus 和 Protocol TestClient；
  - 为 App 与 Protocol TestClient 按 `osx-arm64`、`win-x64` 分别独立 restore/publish，
    只把交叉发布记为产物生成；
  - 在 `osx-arm64` 发布目录执行完整六场景真机矩阵与残留扫描，并更新 Provider
    Backlog、Platform Ledger 和 Acceptance 的 macOS 证据；不提前关闭 M9。
- Verify:
  - `dotnet test OpenCoWork.slnx -c Release --no-restore`
  - `dotnet build OpenCoWork.slnx -c Release --no-restore`
  - `dotnet format OpenCoWork.slnx --verify-no-changes --no-restore`
  - 每个目标 RID 分别执行：

```bash
dotnet restore src/OpenCoWork.App/OpenCoWork.App.csproj -r <rid>
dotnet publish src/OpenCoWork.App/OpenCoWork.App.csproj -c Release -r <rid> --self-contained false --no-restore
dotnet restore tests/OpenCoWork.Protocol.TestClient/OpenCoWork.Protocol.TestClient.csproj -r <rid>
dotnet publish tests/OpenCoWork.Protocol.TestClient/OpenCoWork.Protocol.TestClient.csproj -c Release -r <rid> --self-contained false --no-restore
```

  - 在安全凭据、真实 Commit SHA 和 xUnit 显式测试开关下，从 `osx-arm64` 发布目录
    执行 `ProviderReleaseValidationTests` 六场景，并将脱敏结果写入台账。
- Acceptance contribution:
  - 为 `M9-ACC-011..019` 形成完整离线证据；为 `M9-ACC-018`、`019` 增加
    `osx-arm64` 真实证据，双平台状态仍保持 Planned/Pending。
- Commit:
  - `test(m9): validate Responses release on macOS`

### Outcome 10：完成 `win-x64` 真机验证并关闭 M9

- Red:
  - `win-x64` 发布目录任一 Text、Function、Web Search、Apply Patch、Usage、Secret
    Canary 场景缺失或失败时，`M9-ACC-018`/`019`、Milestone M9 和 Archive Gate 必须
    保持未完成；
  - Cross-publish、macOS 结果或其他 Provider 的历史通过结果都不能替代 Windows
    真机证据。
- Work:
  - 在 Windows 真机从对应 `win-x64` 发布目录运行同一固定六场景、State v8 旧库
    迁移、重启恢复、路径/符号链接、Patch 原子提交和 Secret/残留扫描；
  - 记录 Commit、RID、Windows/SDK/runtime、精确模型/API、场景、Usage、终态、时间、
    命令与脱敏结果；
  - 两平台证据齐全后，把 `M9-ACC-011..019` 更新为 Passed，同步 M9 Design/Plan
    状态、Provider Backlog、Platform Ledger、Milestone CHECKLIST/INDEX 和唯一 M9
    Delivery Archive；
  - 若 Windows 尚未执行，只更新实际获得的证据并保持 Outcome 10/M9 未完成，不创建
    “已交付”归档。
- Verify:
  - 在 `win-x64` 发布目录执行与 Outcome 9 完全相同的显式 Runner/TestClient 六场景；
  - `dotnet test OpenCoWork.slnx -c Release --no-restore`
  - `dotnet build OpenCoWork.slnx -c Release --no-restore`
  - `git diff --check`
- Acceptance contribution:
  - 双平台真实结果齐全时关闭 `M9-ACC-011` 至 `M9-ACC-019`；否则保持未满足项为
    Planned/Pending。
- Commit:
  - `docs(m9): close DeepSeek Responses delivery`

## 覆盖矩阵

| Outcome | 冻结设计组 | 验收编号 |
| ---: | --- | --- |
| 1 | 2.1、2.2、2.8、2.12 | M9-ACC-011、014、017 |
| 2 | 2.3、2.4、2.9、2.11、2.12 | M9-ACC-014、016 |
| 3 | 2.2、2.7、2.9、2.12 | M9-ACC-012、014、015 |
| 4 | 2.3、2.4、2.9、2.12 | M9-ACC-012、014、015、016 |
| 5 | 2.3、2.4、2.5、2.9、2.12 | M9-ACC-013、015、016 |
| 6 | 2.3、2.5、2.10、2.12 | M9-ACC-013、019 |
| 7 | 2.3、2.5、2.6、2.7、2.9、2.11、2.12 | M9-ACC-012、015、016、019 |
| 8 | 2.1、2.4、2.8、2.11、2.12 | M9-ACC-011、016、017、019 |
| 9 | 全部离线关闭条件与 macOS 发布证据 | M9-ACC-011..019 |
| 10 | 全部双平台关闭条件 | M9-ACC-011..019 |

12 组冻结设计和 9 个新增 Acceptance ID 都至少有一个主实现 Outcome 与最终关闭
Outcome；没有为 `deepseek-v4-pro`、其他 Provider 或官方未支持能力安排占位工作。

## 停止条件与恢复边界

- 未获用户显式真实 Provider 探针授权：不执行 Gate 0，不开始 Outcome 1；
- Apply Patch Input/Result 线格式无法由官方资料或真实探针确认：停止 Outcome 6，
  不按 OpenAI 行为猜测；
- 上下文 400 无稳定精确签名：不实现响应式 400 压缩，这不是阻塞项；
- State v8 Backup、DDL、Commit、恢复或完整性校验失败：Runtime Start 失败，不继续
  Provider 调用；
- SSE 出现未知事件、序号倒退、重复 Call ID、Delta/Done 不一致、未闭合 Item、
  多终态或终态后事件：Attempt 以不可重试 Invalid Stream 失败；
- 首个持久化 Output/Reasoning Delta、首个 Web Search `in_progress` 或本地工具实际
  尝试之后发生故障：不得自动重试当前 Attempt；
- Patch 全包预检不完整：零写入；跨文件部分提交无法证明：`OutcomeUnknown`、记录路径、
  禁止 Replay；
- 旧 Provider/Model Thread 的运行/恢复无法在联网和工具重放前拦截：阻塞 Outcome 8；
- Secret Canary 命中 Journal、SQLite、Session Event、日志、Wire、stdout/stderr、
  证据或测试产物：阻塞当前 Outcome；
- Wire 1.0–1.3、M7 Session/CoWork 或 M8 Automation 回归：阻塞 M9 关闭；
- 任一目标平台缺发布目录真机证据：对应平台保持 Pending，M9 不标 Done；
- 真实用户目录、真实 Provider、远端仓库或外部系统未获明确授权：只使用临时 Profile、
  BCL Loopback 和 Fake 边界。
