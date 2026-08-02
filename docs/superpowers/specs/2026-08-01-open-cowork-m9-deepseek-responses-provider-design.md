# OpenCoWork M9 DeepSeek Responses Provider 设计

## 文档状态

- 状态：已完成并归档；双平台真实 Provider 验收通过
- 日期：2026-08-01
- 所属里程碑：OpenCoWork Runtime 1.0 / M9
- 已确认决策：12 组
- 待确认主题：0 组
- 对应计划：
  [M9 DeepSeek Responses Provider 实施计划](../plans/2026-08-01-open-cowork-m9-deepseek-responses-provider-implementation-plan.md)；
  本文与计划均不授权实现或真实外部请求
- 对应归档：
  [M9 DeepSeek Responses Provider 交付归档](../archives/2026-08/2026-08-01-open-cowork-m9-deepseek-responses-provider-archives.md)
- 继续工作前必须先阅读：
  - [OpenCoWork Runtime 1.0 路线规格](2026-07-25-open-cowork-runtime-1-0-roadmap.md)
  - [M0-M11 验收目录](2026-07-25-open-cowork-m0-acceptance-catalog.md)
  - [M3 Agent Runtime Alpha 设计](2026-07-27-open-cowork-m3-agent-runtime-alpha-design.md)
  - [M4 Tool Runtime Alpha 设计](2026-07-28-open-cowork-m4-tool-runtime-alpha-design.md)
  - [M6 Capability Ecosystem 设计](2026-07-29-open-cowork-m6-capability-ecosystem-design.md)
  - `DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md`

本文保存本轮头脑风暴已确认的冻结边界。第 2 节是 M9 的权威设计基线；协议探针和
实施计划只能细化线格式与交付顺序，不能绕过本文扩大 Provider、模型、工具或状态面。
本文不授权实现。

## 1. 目标与依据

M9 用 DeepSeek 官方 Responses API 取代现有通用 OpenAI-compatible Chat
Completions 与千问 Token Plan 路径。首发只支持 `deepseek-v4-flash`；
`deepseek-v4-pro` 等官方正式支持且完成真实兼容性验证后再激活。

协议实现范围以 2026-08-01 查阅的 DeepSeek 官方资料为准：

- [Responses API Guide](https://api-docs.deepseek.com/guides/responses_api/)
- [Error Codes](https://api-docs.deepseek.com/quick_start/error_codes/)
- [Codex Integration](https://api-docs.deepseek.com/quick_start/agent_integrations/codex/)

### 1.1 实施前真实协议探针（2026-08-01）

用户显式授权后，在 macOS 从安全进程环境读取凭据，对官方
`https://api.deepseek.com/responses` 和精确模型 `deepseek-v4-flash` 执行了最小
两轮探针。探针未保存 Prompt、正文、Reasoning、Response/Call ID 或 Secret。

- 请求工具为 `{"type":"custom","name":"apply_patch"}`；对 Custom Tool 发送
  `tool_choice:"required"` 会得到 400，因此产品不得发送该组合；
- 调用输出为 `custom_tool_call`，字段为 `call_id`、`id`、`input`、`name`、
  `status`、`type`；自由格式 Patch 通过
  `response.custom_tool_call_input.delta/done` 组装；
- 结果回注 Item 精确为 `custom_tool_call_output`，只需 `call_id`、`output`、
  `type`；将第一轮完整 Output Items 与该结果按无状态 Input 回注后，第二轮 HTTP 200、
  `response.completed`，且正常返回 Message、不重复调用 Patch；
- 规范化线结构 SHA-256 为
  `c2354907bba0cd7358c7d8ddaf3e52d700c7ddbc85f8872a46f579f1a7923ca2`；脱敏结构见
  `tests/OpenCoWork.Core.Tests/Fixtures/DeepSeekResponses/apply-patch-wire-shape.json`；
- 官方仅保证超上下文请求返回 400，未保证稳定错误体。M9 不发送百万 Token 的高成本
  合成探针，也不实现泛化 400 响应式压缩；继续依赖本地 Token 预算与主动压缩。

当前实现锚点：

- `src/OpenCoWork.Core/Agents/OpenAiCompatibleChatClient.cs`
- `src/OpenCoWork.Core/Agents/AgentRuntime.cs`
- `src/OpenCoWork.Core/Capabilities/ProviderCapabilities.cs`
- `src/OpenCoWork.Core/Capabilities/ProviderSecretStore.cs`
- `src/OpenCoWork.Core/Tools/ToolRuntime.cs`
- `src/OpenCoWork.Core/Tools/ToolInvocationPipeline.cs`
- `src/OpenCoWork.Core/Tools/CoreWebTool.cs`

## 2. 已确认设计基线

### 2.1 产品边界

1. M9 只实现 DeepSeek Responses Provider，首发模型只有
   `deepseek-v4-flash`。
2. 不保留千问 Token Plan、其他 Provider 或通用 OpenAI-compatible Chat
   Completions 运行路径。
3. `deepseek-v4-pro` 不做占位实现；官方支持和真实验证完成时再增加精确模型项。

### 2.2 最小客户端

新增专用的最小 `DeepSeekResponsesClient`，复用 BCL `HttpClient`、
`Utf8JsonWriter` 和有界 SSE 读取方式。不引入 OpenAI SDK，不建立通用 Provider
SPI、工厂或“以后可能用到”的适配层。

### 2.3 既有权威边界保持不变

以下组件继续作为唯一权威边界，不建立平行状态机：

- `ThreadJournal` 与 `AgentRuntime`：Turn、Item、模型历史和恢复；
- Invocation/Attempt、首个可见增量边界和响应式压缩：重试语义；
- `EffectiveToolSnapshot`：本 Turn 可见工具集合；
- `ToolInvocationPipeline`：所有本地模型工具副作用；
- 既有 Approval、Audit 和 Replay Protection：授权、审计和幂等。

### 2.4 无状态恢复

DeepSeek 请求按无状态方式重建。不得依赖官方当前明确不支持的
`previous_response_id`、`conversation`、`store` 或 background；进程恢复仍以
本地 Journal 和 Checkpoint 为准。

### 2.5 工具分工

- File Read、Shell、MCP、Git 等现有本地工具通过官方 `function` 类型暴露，执行
  仍经过 `ToolInvocationPipeline`。
- 文件修改改用官方 `custom/apply_patch` 的模型协议，落地时仍进入本地权限、路径、
  审批、审计和原子提交边界；模型侧整文件 `file.write` 退出 Catalog，底层安全文件
  原语可以保留。
- 联网搜索使用 DeepSeek 服务端 `web_search`；模型侧本地
  `web.fetch/CoreWebTool` 退出 Catalog。

### 2.6 `web_search` 授权规则

这是本轮已明确确认的安全决策：

| 有效 `NetworkRead` Authority | 请求是否包含 `web_search` |
| --- | --- |
| `Allow` | 是 |
| `RequireApproval` | 否 |
| `Deny` | 否 |

原因是 `web_search` 由 Provider 在本地收到工具调用前执行，无法套用现有的调用后
审批。M9 不新增请求前审批状态机。服务端搜索生命周期必须进入本地 Journal/Audit，
但不能伪装成由 `ToolInvocationPipeline` 执行的本地工具。

### 2.7 官方 SSE 子集

实现语义 SSE，而不是沿用 Chat Completions 的 `[DONE]` 哨兵：

- 校验单调递增的 `sequence_number`；
- 以 `response.completed`、`response.incomplete` 或 `response.failed` 唯一结束；
- 映射 Content、Reasoning、Function Arguments、Custom Tool Input、Web Search
  状态和 Usage；
- 保持事件、单帧、累计响应和输出大小有界；
- 未进入产品范围的官方能力不发送，不能因为服务端可能静默忽略就假装已支持。

### 2.8 Provider / Model / Auth 配置

- Provider 是固定协议实现，不是用户扩展点：内建 ID 为 `deepseek`，Base URL 固定
  为 `https://api.deepseek.com`。测试可以注入本地 `HttpClient`，产品配置不能
  改写 Endpoint；代理继续使用系统网络栈和标准代理环境变量。
- Model 是唯一 Provider 选择维度。`models` 配置只保留精确的 `defaultModel` 和生成
  参数 `reasoningEffort`；首发默认且只允许 `deepseek-v4-flash`。Tokenizer、Context
  Window 和 Output Limit 来自内建模型目录，不允许工作区覆盖。未来增加 Pro 时只
  扩充内建白名单。
- 内建固定 `auth/deepseek` Profile，不要求在 `.opencowork/auth.json` 中声明。
  凭据先读取显式进程环境 `DEEPSEEK_API_KEY`，缺失时再读取现有 Workspace 隔离的
  OS Secret Store；现有 `auth/secret/set` / `clear` 继续管理后者。
- `.opencowork/auth.json` 继续服务 MCP OAuth 等非 Provider Auth，不因 M9 删除。
- Thread、Invocation、Automation 和 Wire 继续保存 `ProviderId` / `ModelId`，用于
  历史恢复、证据和协议兼容；所有入口只接受 `ProviderId=deepseek` 和内建精确模型。
  CLI 暂时保留 `--provider` 作为精确值兼容入口，不恢复通用 Provider 选择能力。
- `.opencowork/providers.json`、旧 `models.providers`、Qwen 模型、自定义 Base URL
  等旧输入必须在任何网络调用前返回稳定迁移诊断，不能静默忽略或回退。

### 2.9 Responses 领域契约与 SSE 状态机

- `DeepSeekResponsesClient` 独占官方 SSE 协议状态机；Runtime 只接收 Text、
  Tool Call、Web Search 和 Terminal 四类本地语义事件，不复制完整 OpenAI
  Responses 对象模型，也不把测试缝扩成 Provider SPI。
- Text 事件按 Output / Reasoning 分类并携带稳定 Item Key、Delta 和 Complete；
  Function / Apply Patch 只在完整调用组装并校验后进入 Runtime；Web Search 保存状态
  和完成后的有界回放 Item；Terminal 唯一表达 Completed / Incomplete / Failed，并
  携带 Usage、原因或错误。
- 每个输出 Item 按 `output_index + item.id` 独立组装。Text、Reasoning、Function
  Arguments 和 Custom Tool Input 在 Delta/Done 同时存在时必须逐字一致；Call ID
  必须唯一。
- `response.created` 必须是首事件；`sequence_number` 必须严格递增但不要求连续；
  只能存在一个终态且终态之后不能再有事件。未知事件、重复 Call ID、未关闭 Item、
  Delta/Done 不一致和缺少终态均作为无效流失败；SSE keep-alive 注释正常忽略。
- 请求历史使用官方已支持的 Message、Reasoning、Function Call、Function Output 和
  Web Search Call Input Item；Session/Journal 负责投影，不再以
  `ChatCompletionMessage` 作为 Provider 传输契约。
- Usage 只从终态携带的完整 Response 对账，不再建立顺序不确定的独立 Usage Event。
- 收到首个 `response.web_search_call.in_progress` 后，本 Attempt 立即进入“已提交”
  状态，即使尚无可见文字也禁止自动重试，避免重复服务端搜索与计费。本地 Function
  Call 在真正进入 `ToolInvocationPipeline` 前不构成副作用提交。

### 2.10 `custom/apply_patch`

- Core Catalog 增加规范工具 `file.apply_patch`，Provider 特殊投影固定为
  `{"type":"custom","name":"apply_patch"}`；它不是普通 Function，也不建立
  第二条执行管线。
- 完整自由格式输入在进入 Runtime 后包装为 `{"patch":"..."}`，继续经过现有
  `EffectiveToolSnapshot`、`ToolInvocationPipeline`、Workspace Write Authority、
  Approval、Hook、Audit 和 Replay Protection。
- `file.apply_patch` 标记为 `WorkspaceRead | WorkspaceWrite` 和
  `ReplaySafety.Unsafe`。`RequireApproval` 时仍可向 Provider 暴露并在本地执行前审批；
  Plan Mode 或有效 Authority 为 `Deny` 时不进入请求。
- 模型侧 `file.write` 退出 Catalog；既有 Workspace Path Guard、严格 UTF-8、Hash
  前置条件和单文件原子替换保留为 Patch Binding 的底层原语。
- Patch Input 复用 512 KiB Tool Arguments 上限。任何写入前必须完成全部语法、操作、
  重复路径、相对路径、符号链接、受保护路径、旧文件 Hash、Hunk Context、新文件冲突
  和删除/移动前置条件检查。
- 不启动 Shell，不调用 `git apply`，不增加第三方 Patch 依赖；用 BCL 实现真实协议
  探针确认过的最小 Grammar。DeepSeek 官方资料未完整定义 Custom Tool Result 回注
  Item 和 Patch Grammar，探针通过前不得凭 OpenAI 行为冻结线协议。
- Mutation 开始前保证整包无副作用；每个目标文件使用原子替换。文件系统不提供跨文件
  原子事务，因此跨文件中途失败必须返回 `tool.outcomeUnknown`，记录已提交与未提交路径，
  并禁止自动重放；不得宣称整个 Patch 具备崩溃原子性。
- Tool Result 只返回操作、路径、前后 Hash 和状态，不回传完整文件内容，并继续受现有
  Result Envelope 上限约束。

### 2.11 Journal Item、Usage 与 State v8

- Usage 继续写入现有 `ProviderUsageRecorded` 和 SQLite
  `provider_usage.usage_json`，不增加 Usage 表或列。`ProviderUsageSnapshot` 在保留
  旧字段的基础上增加 `CachedPromptTokens` 和 `ReasoningCompletionTokens`；旧记录与
  本地估算缺失时按 `0` 读取。
- DeepSeek Usage 必须满足 Cached Prompt 不大于 Prompt、Reasoning Completion 不大于
  Completion，且 Total 等于 Prompt 与 Completion 之和。`ChatCompletionInvocationPurpose`
  重命名为 `ProviderInvocationPurpose`，现有 `response` / `compaction` JSON 值不变。
- 本地 Tokenizer 继续负责调用前预算，Provider 终态 Usage 继续负责调用后权威计量；
  不允许用服务端模板反推常量修改生产计数。真实发布对账只允许
  `max(1536, ceil(providerPromptTokens × 0.005))` 的普通/Function/Apply Patch 偏差；
  服务端 `web_search` 因包含本地无法预知的检索上下文，允许
  `max(8192, ceil(providerPromptTokens × 0.005))`。本轮 macOS 探针观察到的最大
  非搜索偏差为 `1183`、搜索偏差约为 `3605`；任一偏差越界必须失败并重新探针，
  不得继续扩大容差或写入新的模型校准常量。
- `ToolCallItemEntry` 增加向后兼容的 `ProviderCallKind`：旧 Journal 缺失时解释为
  `Function`，Apply Patch 使用 `CustomApplyPatch`。Reasoning 继续使用现有 Item 类型：
  当前活动 Turn 的工具循环和恢复需要回注，已完成的历史 Turn 不回注。
- 服务端 Web Search 不写成本地 Tool Call。新增 `ProviderAction` Session Item，每个
  `inProgress` / `searching` / `completed` 状态追加一条不可变 Item，并共享同一个
  Provider Call ID；客户端可以折叠显示，但 Journal 不原地改写。
- 只有 `completed` Provider Action 保存已校验、规范化且不超过 256 KiB 的
  `web_search_call` 回放 Item。后续请求只按官方要求回注这个完成 Item，不保存或回注
  整个原始 Provider Response。
- State v8 只重建 `items.item_type` 约束以允许 `providerAction`，不增加表、索引或第二
  套持久化。压缩可以丢弃旧搜索状态噪音，由摘要保留结果叙事；未压缩的完成搜索 Item
  仍属于 Provider History。

### 2.12 Reasoning、终态、错误、重试、迁移与验证

- `models.reasoningEffort` 只允许 DeepSeek 官方当前列出的 `low`、`high`、`max`，默认
  `high`；非法值必须在联网前失败。该值映射为请求 `reasoning.effort`，同时用于 Response
  和 Compaction，并冻结到 `AgentInvocationSnapshot`；配置变化只影响后续 Invocation。
  M9 不增加 Thread/Wire 级覆盖入口。
- HTTP 只将官方明确列出的 429、500、503 视为瞬态；400、401、402、422 为稳定失败，
  分别映射既有 Invalid Request、Authentication、Quota 语义。其他 HTTP 状态、Redirect、
  TLS、无效 SSE、未知事件和大小越界默认不可重试，不根据错误文案猜测瞬态性。
- `response.completed` 正常结束；结构完整的 `response.incomplete` 保留部分输出并追加
  `response.truncated` System Notice，不自动重试；存在未闭合 Item 或 Tool Call 时仍按
  Invalid Stream 失败。`response.failed` 使用稳定本地码 `provider.responseFailed`，只
  保存有界、脱敏的官方 Error Detail，默认不可重试。
- 现有每个 Provider Round 最多三次 Attempt、`Retry-After`、30 秒单次等待上限和
  Invocation 总预算保持不变。只有尚未提交的 Transport、Response Header/Idle Timeout、
  429、500、503 才能重试。
- Attempt 提交边界统一扩展为：首个持久化的 Output/Reasoning Delta、首个
  `response.web_search_call.in_progress`，或任一本地工具实际进入执行尝试。越过任一
  边界后禁止自动重试；完成 Function/Custom Call 的协议组装本身不构成本地副作用。
- 本地 Token 预算与主动压缩仍是主要上下文保护。删除旧千问错误文案匹配；DeepSeek
  官方只明确超窗返回 400，未定义稳定响应体签名，因此只有真实探针冻结的精确签名才能
  在首轮、未提交、无工具尝试时触发一次既有响应式压缩。若无法获得稳定签名，M9 不实现
  泛化 400 压缩。
- State v8 复用现有备份、事务迁移和失败恢复路径。旧 Journal 缺失新增字段时使用第
  2.11 节的兼容默认值；旧 Provider/Model Thread 保持可读，但运行或崩溃恢复必须在
  联网前以稳定迁移诊断失败，不自动改写为 Flash，也不重放工具。用户显式选择受支持模型
  后才能开始新 Turn。
- 自动验证分为 BCL Loopback 协议/故障矩阵和 v7→v8/旧配置迁移 Corpus。真实验证必须
  从 `win-x64` 与 `osx-arm64` 各自发布目录独立执行 Flash Text、Function、Web Search、
  Apply Patch、Usage 对账和 Secret Canary；交叉发布不能替代真机证据。

## 3. 实施前置条件

1. M9 实施计划必须把第 5 节 Acceptance ID 映射成可独立验证的交付 Outcome，并把
   未知官方行为保留为显式 Gate，不能在计划中假装已经取得证据。
2. 开始实现前先由用户显式激活真实 `deepseek-v4-flash` 探针，冻结
   `custom/apply_patch` 的完整 Input/Result 回注线格式，以及 DeepSeek 上下文过长 400
   的可识别签名；未取得的行为不得从 OpenAI 或旧千问实现推断。

## 4. 变更控制

后续若 DeepSeek 官方文档变化，先更新 Provider Backlog 和本文的协议证据，再修改计划。
新增模型、Provider、工具或持久化状态均需重新确认，不能作为实现细节顺手加入。

## 5. 验收映射

| 验收范围 | Acceptance ID |
| --- | --- |
| DeepSeek-only Catalog、配置、Auth | `M9-ACC-011`, `M9-ACC-017` |
| Responses SSE、Reasoning、Usage、终态 | `M9-ACC-012`, `M9-ACC-014` |
| Function 与 `custom/apply_patch` | `M9-ACC-013` |
| 错误、重试、压缩 | `M9-ACC-015` |
| 无状态恢复 | `M9-ACC-016` |
| 双平台真实 Provider 验证 | `M9-ACC-018` |
| `web_search` Authority 与旧工具退出 | `M9-ACC-019` |
