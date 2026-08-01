# OpenCoWork M3 Agent Runtime Alpha 设计规格

## 文档状态

- 状态：已交付
- 日期：2026-07-27
- 验收边界修订：2026-07-28
- 所属里程碑：OpenCoWork Runtime 1.0 / M3
- 目标框架：.NET 10
- 路线规格：
  [OpenCoWork Runtime 1.0 路线规格](2026-07-25-open-cowork-runtime-1-0-roadmap.md)
- M0 冻结契约：
  [OpenCoWork M0 Contract Freeze](2026-07-25-open-cowork-m0-contract-freeze-design.md)
- M2 前置规格：
  [OpenCoWork M2 Durable Session Core](2026-07-26-open-cowork-m2-durable-session-core-design.md)
- 能力台账：
  [OpenCoWork M0 能力台账](2026-07-25-open-cowork-m0-capability-ledger.md)
- 验收目录：
  [OpenCoWork M0-M11 验收目录](2026-07-25-open-cowork-m0-acceptance-catalog.md)

本文已于 2026-07-27 读取并核对仓库根目录的
`DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md`。该文件只作为
能力边界、状态语义、安全顺序和验收场景的本机证据基线；本文不兼容 DotCraft
的 `.craft`、程序集、私有实现或 Provider 专用能力，所有差异均按 OpenCoWork
冻结契约明确标注。

本文是 M3 的冻结设计契约。实现计划必须遵守本文已经确认的边界、状态语义、安全
顺序和验收证据；后续若发现必须改变公共行为，应先修订本文，不得在实现中静默偏离。

2026-08-01 前向修订：M3 已交付的通用 OpenAI-compatible/Qwen Chat Completions
路径保留为历史证据，但将在 M9 由 DeepSeek-only Responses API 实现取代；M3 的
Agent、Journal、恢复、压缩和 Usage 语义继续有效。

## 1. 目标与边界

M3 在 M2 的 Durable Session Core 上接入不带真实工具的 Agent Runtime，使 CLI
能够完成真实多轮对话、重启恢复、流式输出、Token 预算和上下文压缩。

固定边界：

- 复用 `ISessionExecutor`、`AgentSession`、`ISessionExecutionSink` 和
  `SessionService` 已有执行路径；
- `ThreadJournal` 继续作为会话事实与模型可见历史的唯一权威来源；
- M3 不创建第二套 Thread、Turn、Item 或 Provider 会话状态机；
- M3 的 Tool Snapshot 为空，不执行真实工具副作用；
- AppServer、ACP、MCP、插件和动态工具不进入 M3。

## 2. 已确认设计

### 2.1 单一 Agent 执行路径

M3 将 Provider 调用、Prompt 组合、上下文预算和压缩接入 M2 的
`ISessionExecutor.ExecuteAsync` 边界。运行结果只通过 `ISessionExecutionSink`
形成执行意图，再由 `SessionService` 按 M2 的 Journal、Projection、Event 顺序提交。

`AgentFactory` 负责确定性组装一次执行所需的：

- Provider 与模型配置；
- 规范化模型历史；
- 系统提示和运行时上下文；
- Token 预算；
- 空 Tool Snapshot。

Provider 相关类型的归属和可见性固定为：

- `OpenCoWork.Abstractions` 只公开 `IChatCompletionClient` 及其最小请求和事件 DTO；
- `ProviderRegistry` 与 `AgentFactory` 都是 Core 内部具体类，不为其创建接口；
- Fake Provider 只存在于测试项目，通过实现同一个 `IChatCompletionClient` 按脚本
  产生增量、Usage 和错误，不建立测试专用执行路径；
- `ProviderRegistry` 只按稳定 Provider ID 精确解析；配置缺失或 ID 重复时以稳定
  配置错误失败，不做隐式回退、自动发现或能力协商；
- Provider 扩展、动态发现和插件能力属于 M6，不提前进入 M3 公共契约。

Provider 与模型选择的状态归属固定为：

- 有效工作区配置必须明确指定一组默认的稳定 Provider ID 和精确 Model ID，不得以
  配置数组第一项作为隐式默认值；
- 创建 Thread 时解析默认值或 CLI 显式覆盖，并将选中的 Provider ID、Model ID
  持久化到 `ThreadSnapshot`；
- Thread 只持久化这两个 ID，不复制 Base URL、Secret 引用、Tokenizer Profile
  或其他运行时配置；
- `AgentFactory` 只按 Thread 当前选择精确解析 Provider、Model 和 Tokenizer Profile；
  配置项被删除或失效时确定性失败，不自动切换到默认值或其他模型；
- M3 不实现 Provider / Model 自动发现、模糊匹配或持久化模型目录。

`IChatCompletionClient` 的最小公共契约固定为：

- `StreamAsync(ChatCompletionRequest, CancellationToken)` 返回
  `IAsyncEnumerable<ChatCompletionEvent>`；
- `ChatCompletionRequest` 只包含精确 Model ID、System / User / Assistant 消息、
  最大输出 Token、Invocation ID、Attempt Number 和 `Response | Compaction` 用途；
- `stream = true` 与 `stream_options.include_usage = true` 由适配器固定，不成为请求
  DTO 或配置开关；
- `ChatCompletionEvent` 只允许 Content Delta、Reasoning Delta、Usage 和 Completed
  四种事件；Completed 只携带归一化的
  `ChatCompletionFinishReason { Stop, Length, ContentFilter, ToolCall, Unknown }`，
  不向核心暴露 Provider 原始值；
- Provider 失败使用类型化 `ChatCompletionException`，携带足以执行 2.9 分类的错误
  类别、可空 HTTP 状态和可空 `Retry-After`；调用方取消继续使用
  `OperationCanceledException`；
- 请求、事件和异常均不得携带 Secret、Provider ID、原始 Provider JSON、扩展字典、
  Tool Message 或非流式分支。

`AgentFactory` 保持同步、确定性且无副作用，固定按以下顺序组装：

1. 校验 `AgentSession` 快照和 Checkpoint；
2. 通过 `ProviderRegistry` 精确解析 Provider 与 Model，并解析对应
   `TokenizerProfile`；
3. 从 `ModelHistory` 和 Checkpoint 重建、规范化模型历史，保证当前用户输入只出现
   一次；
4. 按 2.8 的固定顺序组装唯一 System Message，并创建空 Tool Snapshot；
5. 执行 Micro Compaction 和本地 Token 预算；
6. 返回不可变的内部 `AgentInvocationDraft`，结果只能是 `Ready` 或
   `CompactionRequired`。

`AgentFactory` 不调用 Provider、不写 Journal，也不持有 `ISessionExecutionSink`。
`ISessionExecutor` 继续负责 Partial Compaction、正式流式调用、重试和 Sink 提交；
Partial Compaction 成功持久化后，使用更新后的 Checkpoint 重新执行同一组装顺序。
相同输入必须产生字节一致的 Draft。M3 只使用私有辅助方法，不创建
`PromptBuilder`、`ContextBuilder` 或 `BudgetPlanner` 接口。

### 2.2 Provider 中立的 Chat Completions 子集

M3 只实现一个 `openai-compatible` Chat Completions 适配器，通过配置支持：

- OpenAI 兼容端点；
- 千问 Token Plan；
- DeepSeek。

上述“支持”只表示共享协议与配置入口，不等于已经取得真实兼容性证据。M3 的首个
真实 Provider 发布承诺收敛为 DeepSeek 官方；千问 Token Plan 和后续 Provider
必须先进入 `docs/provider-validation-backlog.md`，待显式激活后再增加对应真实
发布测试和支持声明。

核心请求仅依赖三方共同具备的最小字段：

- `model`；
- `messages`；
- `stream`；
- `stream_options.include_usage`；
- `max_tokens`。

M3 使用 `HttpClient` 与 `System.Text.Json` 实现协议适配，不引入 OpenAI 官方 SDK。
不使用 OpenAI Responses API、`previous_response_id`、Provider Conversations、
Provider 托管历史或 Provider 原生压缩能力。

Provider 配置复用 M0 冻结的顶层 `models` 配置节和 M1 的
`EffectiveConfigSnapshot`，固定形状为：

- `defaultProvider` 和 `defaultModel` 表达工作区默认组合；
- `providers` 是按稳定 Provider ID 命名的具名对象，不在值中重复 ID；
- 每个 Provider 只包含 `baseUrl`、`apiKey.environment` 和按精确 Model ID 命名的
  `models` 对象；
- 每个模型项包含 Tokenizer Profile ID 与版本、上下文窗口和最大输出 Token；
- Provider、模型和 Profile ID 均精确匹配，不做大小写归一化或别名展开。

Base URL 是 API 前缀，适配器固定请求
`{baseUrl.TrimEnd('/')}/chat/completions`。它必须是无 UserInfo、Query 和 Fragment
的绝对 URI；仅允许 HTTPS，回环地址可使用 HTTP。M3 不支持自定义 Header、OAuth
或 Provider 专用认证参数。

M3 的 `apiKey` 只接受单字段环境变量引用，不接受明文字符串。环境变量名必须符合
`[A-Za-z_][A-Za-z0-9_]*`；Keychain、Credential Manager 和通用 Secret Provider
留到后续安全能力。Secret 原值不进入 `EffectiveConfigSnapshot`。

配置校验分为两层：

- WorkspaceRuntime 启动时由 M1 管线校验全部 `models` 配置的结构、默认引用、
  Base URL、唯一键、正数范围和 Profile 引用；
- `opencowork chat` 在创建新 Thread 或更新恢复 Thread 的模型选择前，只预检最终
  选中组合的环境变量非空、Tokenizer 资产存在且 SHA-256 正确、限制值与 Profile
  一致；未选中 Provider 缺少 Secret 不阻塞启动；
- 预检属于 Core 能力而非 CLI 私有逻辑；真实执行路径在 Provider 调用前仍执行相同
  校验，其他宿主不能绕过；
- 预检不发起网络请求，API Key 和模型的远端有效性仍由第一次 Provider 调用确认；
- 预检失败不得创建空 Thread，也不得修改恢复 Thread 已持久化的选择。

WorkspaceRuntime 启动时一次性解析所有当前存在的环境变量 Secret，保存到 Core
私有的不可变字典；缺失值保持缺失，不因此阻塞未使用 Provider。该字典与
`EffectiveConfigSnapshot` 中已有 Secret 一起用于构造现有 `SecretRedactor`，并且
必须发生在任何模块或 Provider 日志产生前。`chat` 预检和后续 Invocation 只读取该
冻结字典，不再次读取进程环境。配置文件、环境变量或 Secret 的变化都必须重启
WorkspaceRuntime 才能生效；M3 不增加可变 Secret 登记接口、文件监视器、显式
Reload 命令或第二套配置状态机。

SSE 响应解析固定为：

- 使用 `HttpCompletionOption.ResponseHeadersRead`；成功响应的 Content-Type 必须是
  `text/event-stream`，允许 Charset 等参数；
- 使用 BCL 按严格 UTF-8 增量解析标准 SSE Event；空行结束 Event，重复 `data:`
  行以换行连接，注释和 `event`、`id`、`retry` 等非 Data 字段忽略；
- 单个 SSE Event 解码后的 Data 上限为 `1 MiB`；超限以
  `provider.invalidStream` 失败，不尝试把整段响应缓存在内存；
- 每个 Provider Attempt 从响应头之后读取的、HTTP 解压后的 SSE 协议 Body
  总量上限为
  `16 MiB`，包含注释、字段名、空行和 Data；超限以
  `provider.outputTooLarge` 失败；
- 同一 Attempt 解码后的 Content 与 Reasoning 合计上限为 `4 MiB UTF-8`；
  适配器分别维护两类计数和合计值，但不为任一类型分配独立配额；
- Data 去除 SSE 语法空白后等于 `[DONE]` 时表示协议终止；其他 Data 必须是一个完整
  JSON Chunk；
- JSON Chunk 的 `choices` 只能为空，或只包含 `index = 0` 的唯一 Choice；空
  `choices` 必须携带非空 Usage；
- Choice 的 `delta.content` 和 `delta.reasoning_content` 只接受字符串或 null；
  Role-only 和全空 Delta 忽略；
- 非空 Usage 出现在任一合法 Chunk 时都立即按既有 Sink 路径归一化提交；Provider
  使用 `choices = []` 的最终 Usage Chunk 不创建空消息；
- 非空 Finish Reason 先按内部精确表归一化并保存，不立即产生 Completed；
  `stop`、`length`、`content_filter` 分别映射同名公共语义，`tool_calls` 和兼容旧值
  `function_call` 都映射 `ToolCall`；
- Finish Reason 使用 Ordinal 精确比较；相同归一化值重复时幂等忽略，不同归一化值
  冲突时以 `provider.invalidStream` 失败；
- `insufficient_system_resource` 是内部瞬态服务失败标记，不映射公共
  `ChatCompletionFinishReason`；适配器继续读取后续 Usage 和 `[DONE]`，然后抛出
  类型化 `provider.serverUnavailable`，不产生 Completed；
- 只有收到 `[DONE]` 后才能产生 Completed；提前 EOF、非法 UTF-8、畸形 JSON、
  非法 Choice 或超大 Event 都不得冒充成功；
- `[DONE]` 时仍无 Finish Reason，按 `Unknown` 进入 2.4 的失败规则；缺少 Usage
  则沿用本地估算规则；
- 原始 Body 或解码文本即将越界时停止读取并释放当前响应；导致文本越界的整个
  Content / Reasoning Delta 不提交，不截断 Delta；
- 协议或流解析错误统一以 `provider.invalidStream` 失败且永不重试；首个可见
  Content / Reasoning 提交边界只决定失败时是否保留已经提交的部分输出；
- 原始 SSE、原始 JSON 和 Tool Call 参数不得写入 Journal、日志或诊断。

HTTP 非成功响应解析固定为：

- 解压后的响应 Body 最多读取 `64 KiB`；达到上限后停止读取，分类仍以 HTTP 状态为
  基线，不因错误 Body 超限产生第二个协议错误；
- Body 是严格 UTF-8 JSON 对象时，同时接受
  `error.{code,type,param,message}` 和同名顶层字段；字段只接受字符串或 null，
  其他类型忽略；
- 适配器先按 HTTP 状态确定错误大类，再使用内部精确映射表细化；Body 缺失、超限、
  非法 UTF-8、畸形 JSON 或不受支持的形状时保持状态分类；
- `code`、`type`、`param` 和 `message` 只在当前 Attempt 内用于分类，原始值和原始
  Body 不进入 `ChatCompletionException`、Journal、日志或 CLI；
- `Retry-After` 只解析标准 HTTP 响应头，不从 JSON Body、错误消息或 Provider
  私有字段推断；
- 映射表是适配器内部常量，并由 Contract Fixture 固定；M3 不允许配置扩展、模糊
  匹配、运行时学习或把未知错误自动提升为可重试错误。

HTTP 生命周期和 Deadline 固定为：

- OpenAI-compatible 适配器在单个 WorkspaceRuntime 内复用一个长生命周期
  `HttpClient`；不按 Turn 创建 Client，也不为此引入 `IHttpClientFactory`；
- `SocketsHttpHandler.AllowAutoRedirect = false`；任何 `3xx` 都以不可重试的
  `provider.redirectNotAllowed` 失败，不向 Location 指向的地址转发 Authorization
  或请求 Body；
- HTTPS 完全使用操作系统证书库、主机名校验和平台默认 TLS 版本；M3 不设置自定义
  证书验证回调、不接受自签名绕过、不固定证书，也不加载自定义信任库；
- 2.2 已确认的回环地址 HTTP 例外保持不变，非回环地址仍必须使用 HTTPS；
- 代理沿用 .NET 与操作系统默认解析及标准环境变量；M3 不增加代理地址、绕过列表、
  `Proxy-Authorization` 或代理凭据配置；
- `UseCookies = false`、`UseDefaultCredentials = false`；Provider 认证只使用冻结的
  API Key，不建立 Cookie 会话或主机集成认证状态；
- `AutomaticDecompression` 只启用 BCL 的 gzip、deflate 和 Brotli；SSE Body 的
  `16 MiB` 上限和错误 Body 的 `64 KiB` 上限都按解压后字节计算；
- `HttpClient.Timeout` 设为 `Timeout.InfiniteTimeSpan`；连接、响应头、流空闲和
  Invocation Deadline 分别使用明确的 CancellationToken；
- `SocketsHttpHandler.ConnectTimeout` 固定为 `15s`，
  `ResponseDrainTimeout` 固定为 `2s`；
- 每次 Provider Attempt 等待响应头最多 `120s`；
- SSE 连续 `120s` 未读取到任何字节时以流空闲超时失败；合法注释心跳同样刷新该
  空闲计时；
- 整个 Agent Invocation 的总 Deadline 为 `30m`，覆盖 Partial Compaction、正式
  回答、全部重试和退避等待；后续 Attempt 只能使用剩余时间；
- 上述计时复用 M2 的 `TimeProvider`，测试不得真实等待；
- `ISessionExecutor` 区分用户取消、WorkspaceRuntime 停止和内部 Deadline：
  用户取消提交 `TurnCancelled`；Runtime 停止沿用 M2 停止顺序；内部 Deadline
  归一化为 `provider.timeout`；
- `provider.timeout` 只有在首个可见 Content / Reasoning 提交前、Invocation
  Deadline 仍有剩余且尚有调用预算时才按瞬态错误处理；否则直接形成 Terminal
  Failure，已提交的部分输出继续保留；
- 每个 Attempt 在 `finally` 中释放 `HttpRequestMessage`、
  `HttpResponseMessage` 和响应流；共享 `HttpClient` 只在 WorkspaceRuntime 停止时
  释放；
- 单次取消不得调用 `HttpClient.CancelPendingRequests()` 或销毁共享 Client，避免
  误伤其他 Thread 的并行调用；
- 本地测试服务器必须覆盖重定向拒绝、自签名证书失败和压缩后 Body 超限；
- M3 不为这些固定值增加配置项或网络依赖；真实 Provider 验证证明默认值不适用时
  再开放。

参考的当前公开协议边界：

- [千问 Token Plan](https://platform.qianwenai.com/pricing/token-plan)
- [Token Plan 快速接入](https://help.aliyun.com/en/model-studio/token-plan-personal-quick-start)
- [千问流式输出](https://help.aliyun.com/en/model-studio/stream)
- [DeepSeek Chat Completions](https://api-docs.deepseek.com/api/create-chat-completion)

### 2.3 本地历史是唯一会话状态

每次模型调用都从 `ThreadJournal` 重放出的规范化本地历史重新构造 `messages`。
Provider 返回的会话 ID、服务端历史或缓存标识都不能成为恢复前提。

这保证同一 Thread 可以更换兼容 Provider，并在进程重启后从本地事实恢复。

### 2.4 流式响应、Reasoning 与 Usage

普通内容增量、可选 `reasoning_content` 和最终 Usage 统一归一化后进入现有
Item / Journal 提交路径。

流式 Item 与恢复规则固定为：

- M3 不新增流缓冲层，直接复用 M2 已实现的 `50ms` 或 `8KiB UTF-8` Delta
  批处理；
- Content 和 Reasoning 首次出现非空 Delta 时，才分别创建 `AgentMessage` 和
  `Reasoning` Item，不产生空 Item；
- Provider Delta 只能先转换为 `SessionExecutionIntent`；CLI 和后续协议只能消费
  Journal 提交后发布的 Session Event，不得旁路显示 Provider 原始流；
- 正常完成顺序为：刷新全部 Delta，完成 Reasoning / AgentMessage Item，记录
  Usage，最后提交 `TurnCompleted`；
- 失败或取消先刷新已接收 Delta，再终结活动 Item，最后提交 Turn 终态；
- 进程中断时，已提交 Delta 保留，未提交 Delta 允许丢失；Item 和 Turn 沿用 M2
  的 `runtime.interrupted` 恢复语义；
- 失败或取消 Turn 的部分 Content / Reasoning 保留用于审计和展示，但
  `AgentFactory` 只把已完成 Turn 中 Completed 的模型可见 Item 送入后续历史；
- M3 不创建 Stream Store、Chunk 表或 Provider 流缓存。

Reasoning 的固定规则：

- Provider 返回 `reasoning_content` 时，归一化为 `Reasoning` Item；
- Provider 不返回 Reasoning 时，Turn 仍是完整有效的；
- 核心不得依赖 Reasoning 才能完成 Turn；
- 请求中的最大输出 Token 是 Content 与 Reasoning 的共享预算；M3 不增加
  Provider 专用的 Thinking 参数、独立 Reasoning 配额或预算拆分；
- 历史 Reasoning 默认不回传到后续请求，避免依赖千问与 DeepSeek 不一致的
  Reasoning 保留规则。

Finish Reason 的固定规则：

- `Stop` 且至少存在一个非空 Content 时正常完成 Turn；
- `Length` 且存在非空 Content 时完成 Turn，将内容纳入后续模型历史，并追加不进入
  模型历史的 `SystemNotice(response.truncated)`；CLI 在 stderr 输出脱敏截断警告；
- `ContentFilter` 使 Turn 以 `provider.contentFiltered` 失败；已提交的部分
  Content / Reasoning 只保留用于审计，不进入后续模型历史；
- `ToolCall` 使 Turn 以 `provider.unsupportedToolCall` 失败；M3 不解析或执行工具调用；
- `provider.serverUnavailable` 在首个可见 Content / Reasoning 提交前按 2.9 的
  瞬态错误和三次调用总预算处理；提交后保留部分输出并形成失败终态，不自动重试；
- `insufficient_system_resource` 到达 `[DONE]` 前若流中断，仍以
  `provider.invalidStream` 处理，不能把不完整流冒充已确认的服务资源错误；
- Finish Reason 缺失或为 `Unknown` 时，以 Provider 协议错误结束 Turn；
- 未产生任何非空 Content 时，无论是否只有 Reasoning，都以
  `provider.emptyResponse` 失败；已提交 Reasoning 只保留用于审计。

Usage 的固定规则：

- 调用前由本地真实 Tokenizer 做预算；
- 调用后以 Provider 返回的 Usage 为该次调用的权威计量；
- `ISessionExecutor` 通过同一个 Sink 提交最小 `RecordProviderUsageIntent`，
  `SessionService` 在 Turn 终态前形成可恢复的 Usage 事实，不创建第二套 Usage
  状态机；
- 每次调用以 `(InvocationId, AttemptNumber, Purpose)` 作为唯一 Usage 键；正式回答、
  压缩和重试分别记录，再按 Invocation 聚合；
- 同一键、相同内容的重复 Usage 幂等忽略；同一键出现冲突内容时返回 Provider
  协议错误，不覆盖已提交事实；
- Provider 成功返回 Usage 时，本地 Tokenizer 计数只用于对账；
- 成功回答未返回 Usage 时，Turn 仍可完成，但必须保存本地计数并标记
  `Source=LocalEstimate`、`IsEstimate=true`，同时产生脱敏警告，不得写入伪造的零值；
- 流因 `provider.outputTooLarge` 失败时，已提交的 Provider Usage 事实继续保留；
  未收到真实 Usage 时不为失败流合成本地 Usage；
- `provider.serverUnavailable` 前已经提交的真实 Usage 同样保留，并以原
  `(InvocationId, AttemptNumber, Purpose)` 键参与实际调用统计；
- M3 内置 Provider / Model Profile 的发布冒烟必须取得真实 Usage；缺失时该
  Profile 不得通过发布验收；
- M3 只统计 Token，不计算价格、Token Plan 套餐余额或人民币成本；
- 流式、重试、压缩和恢复不得重复累计同一次调用的 Usage。

`provider.outputTooLarge` 是不可重试的终态错误。`ISessionExecutor` 先刷新并终结
已经提交的 Content / Reasoning Item，再提交失败终态；部分输出只用于审计和展示，
不进入后续模型历史。`16 MiB`、`4 MiB`、请求 Token 预算和 30 分钟 Invocation
Deadline 构成 M3 的固定多层边界；M3 不增加相关配置项、全量响应缓冲或增量
Tokenizer。

### 2.5 重试安全边界

- 首个 Content 或 Reasoning 增量提交前，满足策略的瞬态失败允许重试；
- 首个 Content 或 Reasoning 增量提交后，自动重试不得造成重复输出；
- prompt-too-long 只允许在尚未产生可见输出时触发响应式压缩和有界重试；
- 所有尝试必须可关联，具体错误分类、次数和退避规则见 2.9。

### 2.6 Micro / Partial Compaction

M3 采用本地权威的混合压缩：

- Micro Compaction 是确定性、本地、无模型调用的无损规范化；
- Partial Compaction 使用同一个 Provider 中立 Chat Completions 通道生成纯文本摘要；
- 压缩产物连同来源 Sequence 范围和 Checkpoint 持久化；
- 既有 Journal Entry 不修改、不删除；
- 恢复时从 Checkpoint、压缩摘要和未压缩尾部历史重建模型上下文；
- 当前 Turn 只能出现一次。

M3 不使用 OpenAI `/responses/compact`、加密压缩 Item 或其他 Provider 私有压缩协议。

压缩水位固定为：

- 可用输入预算等于上下文窗口 Token 减去预留输出 Token；
- 每次请求在 Token 预算前执行 Micro Compaction；
- Micro 后输入超过可用输入预算的 80% 时执行 Partial Compaction；
- Partial Compaction 将输入压到不超过可用输入预算的 60%；
- Provider 在首个 Content 或 Reasoning 增量提交前返回 prompt-too-long 时，执行一次
  响应式压缩，将输入压到不超过可用输入预算的 50%，并最多重试一次；
- 响应式重试计入同一个 Invocation 的三次调用上限；
- 无法达到目标水位或响应式重试后仍超限时，确定性失败，不继续压缩循环。

Partial Compaction 的历史选择规则固定为：

- 只压缩已经完整结束的 Turn；
- 从最旧的连续历史前缀开始选择；
- 只选择达到目标水位所需的最小前缀，尽量保留更多近期原文；
- 当前 Turn 永不参与压缩；
- 不硬编码保留最近若干 Turn，保留范围完全由 Token 水位决定。

Partial Compaction 摘要 Prompt 固定为：

- 首次压缩输入为本次选中的完整历史前缀；后续压缩输入为上一份权威摘要和本次
  新增纳入的完整 Turn，不重复发送已经摘要的旧原文；
- 摘要输入只包含对应 Turn 中模型可见的 User / Assistant 内容，不包含历史
  Reasoning；M3 不向摘要调用提供工具，也不允许其产生工具调用；
- 输出为纯文本，并且只能按顺序各包含一次以下五个 Markdown 二级标题：
  `## 目标与上下文`、`## 已确认的决策与约束`、`## 已完成结果`、
  `## 关键标识、路径与错误`、`## 待办与下一步`；
- 每个区段正文必须非空；没有内容时固定写 `- None.`，不得省略标题或增加其他
  Markdown 二级标题；
- 摘要不得补造信息，并应原样保留关键标识、路径、错误和未解决事项；
- 摘要输出 Token 上限为
  `min(8192, ceil(usableInputBudgetTokens × 0.10))`。

摘要持久化和重放规则固定为：

- Core 使用一个内部 `CompactionCheckpoint` 记录 Schema Version、Summary Text、
  Summary SHA-256、闭区间 Source Start / End Sequence、规范化来源消息 SHA-256
  和 Summary Prompt Version，不为其创建公共接口；
- Source Start 从第一个被选 Turn 的首条 Journal 事实开始，Source End 到最后一个
  被选 Turn 的终态事实结束；来源消息哈希只覆盖该范围内实际送入摘要的规范化
  User / Assistant Role 与 Content；
- 首次压缩创建权威 Checkpoint；后续压缩保留原 Source Start、扩展 Source End，
  并用上一份权威摘要与新增 Turn 生成一个新的权威 Checkpoint；
- 最新 Checkpoint 取代旧摘要参与模型上下文；旧 Checkpoint 和全部原始 Journal
  事实继续保留用于审计，不修改、不删除；
- 重放时只把最新有效摘要构造成一个不向用户展示的合成 Assistant Message，内容以
  `Conversation summary of earlier turns:` 和换行开头，再接未压缩历史尾部；不得
  注入摘要链或第二个 System Message；
- Summary、来源范围和 Checkpoint 必须作为一个 Journal 提交原子持久化；哈希或
  Sequence 边界不一致时按 Journal 损坏处理，不静默回退到旧摘要。

Partial Compaction 的调用计数和失败语义固定为：

- 单个用户 Turn 对应一次 Agent Invocation；摘要、正式回答及其所有重试共享最多
  三次 Provider 调用；
- 摘要流的中间内容只保留在内存中；仅当摘要完整、格式有效且达到目标水位时，
  才将摘要、来源 Sequence 范围和 Checkpoint 作为同一个提交持久化；
- 摘要失败时丢弃中间内容，既有摘要和 Checkpoint 保持不变；
- 摘要持久化前发生 2.9 定义的瞬态错误时，只要仍有调用额度即可按相同退避规则
  重试；摘要截断、格式无效或仍无法达到目标水位时确定性失败；
- 摘要截断、格式无效、无法达到目标水位，或响应式压缩重试后仍然
  prompt-too-long，统一以 `context.compactionFailed` 结束当前 Turn；
- 格式校验只检查 LF 规范化、五个标题的唯一性和顺序、非空正文、`Stop` Finish
  Reason、哈希和 Token 水位；M3 不评价摘要质量，也不额外调用模型修复；
- 摘要调用的 Provider Usage 按 2.4 正常计量；同一 Agent Invocation 内的每次
  Provider 调用使用不同的 Attempt Number。

### 2.7 Tokenizer 与预算

M3 必须使用真实 Tokenizer，不能只用字符数、字节数或固定比例估算。

调用前预算至少包含：

- 每条消息的内容 Token；
- Role、消息边界和 Chat Template 开销；
- 系统提示与动态上下文；
- 已持久化压缩摘要；
- 为模型输出预留的 Token。

本地预算用于决定是否压缩和是否允许发起请求；Provider 返回的 Usage 用于调用后
校准与持久统计。

Tokenizer 实现路线固定为：

- 使用托管包
  [`Tiktoken` 3.1.5](https://www.nuget.org/packages/Tiktoken/)，M3 不并存第二套
  Tokenizer 引擎；
- 内置 Tiktoken 编码可直接使用库内编码；千问、DeepSeek 等模型使用经过兼容性
  验证的 HuggingFace `tokenizer.json`；
- 每个精确模型必须匹配一个版本化 `TokenizerProfile`；
- 找不到匹配 Profile 时，在发起 Provider 请求前返回稳定配置错误，不退化为字符估算；
- Provider Usage 只负责调用后权威计量，不替代调用前 Token 预算。

`TokenizerProfile` 至少记录：

- 稳定 Profile ID 与版本；
- 精确模型 ID 列表；
- 内置编码名，或 `tokenizer.json` 来源与 SHA-256；
- Chat Template ID 与版本；
- 上下文窗口 Token；
- 最大输出 Token。

Tokenizer 资产分发固定为：

- M3 支持的精确模型随程序提供 Profile 和对应词表资产；
- 启动模型调用前校验资产 SHA-256，不匹配时返回稳定配置错误；
- 自定义模型只接受显式配置的本地 `tokenizer.json` 与预期 SHA-256；
- Agent Runtime 不在运行时自动下载词表，也不依赖 HuggingFace 等外部目录可用；
- 远程 Profile 目录、下载、更新和信任策略留到 M6 Capability Ecosystem。

M3 首批内置 Profile 固定为：

- `qwen3.8-max-preview`，对应千问 Token Plan 的当前精确模型 ID；
- `glm-5.2`，对应千问 Token Plan 的当前精确模型 ID；
- `deepseek-v4-pro`，对应 DeepSeek 官方与千问 Token Plan 的精确模型 ID；
- `deepseek-v4-flash`，对应 DeepSeek 官方与千问 Token Plan 团队版的精确模型 ID。

`qwen3.8-max-preview` 当前固定启用 Thinking，但 M3 仍只依赖 Provider 中立的
Chat Completions 公共子集，不依赖 Token Plan 的 Responses API、Harness Tool 或
厂商私有 Thinking 参数。

当前模型清单依据：

- [千问 Token Plan](https://platform.qianwenai.com/pricing/token-plan)
- [阿里云 Qwen Code 接入说明](https://help.aliyun.com/en/model-studio/qwen-code)
- [DeepSeek Models & Pricing](https://api-docs.deepseek.com/quick_start/pricing)

具体模型 Profile 冻结前必须执行 Token 对账，语料至少覆盖中英文、代码、Reasoning
和多轮消息。对账采用非对称验收：

- 原始文本的 Token ID 序列必须与模型参考 Tokenizer 完全一致，零差异；
- 完整 Prompt 的本地计数不得低于 Provider 返回的 Prompt Usage；
- 允许的保守高估上限为
  `max(32, ceil(providerPromptTokens × 0.005))`；
- 任一语料不满足上述条件时，该 Profile 不得随 M3 发布。

### 2.8 Prompt 组装与记录

M3 只定义两个用途明确、Provider 中立的 Prompt 版本：

- `opencowork.response.v1` 的唯一 System Message 按固定顺序包含 OpenCoWork
  身份与安全边界、当前 Agent / Plan 模式、可选 Workspace 指令和最小运行时事实；
- `opencowork.compaction.v1` 的唯一 System Message 只包含内置摘要职责、安全边界和
  2.6 冻结的五段输出格式，不包含 Agent / Plan 模式、Workspace 指令或运行时事实。

两种用途的每次 Provider 调用都只能生成一个 System Message，不使用 OpenAI
专用 Role、Provider 私有 Prompt 字段或第二条 System Message。

内置 Prompt 的实现和语义固定为：

- 内置文本使用 Core 私有常量和确定性字符串拼接，源码只使用 LF；M3 不引入
  Razor、Liquid、Scriban、资源加载器或可配置 Prompt 模板；
- 生成 System Message 后按 UTF-8 无 BOM 计算 SHA-256；固定文本、空白、区段顺序
  或边界标记发生任何字节变化，都必须升级对应 Prompt 版本；
- 共同身份块声明模型是 OpenCoWork 的 AI 助手，只能依据当前 System Message、
  本地模型历史和用户输入回答；
- 共同能力边界声明 M3 没有文件、命令、网络或其他工具，不能声称已经执行操作、
  修改文件或取得未提供的外部事实；
- Agent 模式要求直接帮助解决当前问题；Plan 模式只允许分析、必要澄清和提供计划，
  不得声称已经实施变更；
- Workspace 指令只能补充工作区级回答约束，不能覆盖共同身份、能力边界、
  `EffectiveAgentMode` 或运行时强制策略。

Workspace 指令来源和规范化规则固定为：

- 只读取工作区根目录精确名称的可选 `AGENTS.md`；M3 不读取用户级或全局指令，
  不搜索目录层级，不支持 `AGENT.md` 别名、Include、Import 或自动发现；
- 路径解析复用 M1 的安全工作区边界；`AGENTS.md` 可以是符号链接，但解析后的目标
  必须仍在工作区内；
- 原始文件最大为 `64 KiB`，按严格 UTF-8 解码；允许并移除 UTF-8 BOM，拒绝 NUL；
- 将 CRLF 和 CR 规范化为 LF，移除多余尾部空行，并使非空内容以一个 LF 结尾；
- 文件不存在时完全省略 Workspace 指令区段，不生成空标题或占位文本；
- 超限、非法 UTF-8、NUL、越界符号链接或读取失败都必须在 Provider 调用前以
  `context.instructionsInvalid` 结束 Turn，不静默忽略。

Workspace 指令在输入真正创建 Turn 时读取一次，并在
`AgentInvocationSnapshot` 提交前冻结；排队输入不在入队时复制文件内容。
当前 Turn 的 Partial Compaction、正式回答和全部重试使用同一份内存内容；文件变化
只影响之后创建的 Turn。Workspace 指令属于每 Turn 的调用输入，不属于 M1
`EffectiveConfigSnapshot`，M3 不为其增加文件监视器、热重载状态机或显式 Reload
命令。

Workspace 指令放在内置身份与安全边界之后，并使用固定边界标记包裹。其内容不能
修改 `EffectiveAgentMode`、运行时强制策略或安全边界；M3 没有真实工具，M4 仍必须
通过 Tool Snapshot 和 Dispatcher 执行权限约束，不能依赖 Prompt 自觉。
开始和结束标记分别固定为
`<workspace_instructions source="AGENTS.md">` 和 `</workspace_instructions>`；
标记只用于清晰分区，不作为安全解析器或授权边界。

`opencowork.response.v1` 的最小运行时事实只包含工作区显示名称。不得向 Provider
发送工作区绝对路径、主机用户名、环境变量、Secret 或墙上时钟时间。

固定规则：

- 压缩摘要、历史消息和当前用户输入保持普通消息，不并入 System Message；
- M3 不为尚未进入范围的 Skill 或 Tool 创建空白 Prompt 区段；
- 相同输入必须生成字节一致的 System Message；
- Journal 只记录 Prompt 用途、版本、完整 System Message SHA-256、按顺序排列的
  来源列表和 Token 数；
- 可选 Workspace 指令来源只记录工作区相对路径、规范化内容 SHA-256、原始字节数
  和 Token 数，不持久化文件全文；
- 不在每个 Turn 重复持久化完整 System Message。

`M3-ACC-001` 的 Prompt Snapshot 验收固定为五个 Golden Snapshot：

1. Agent 模式且无 `AGENTS.md`；
2. Plan 模式且无 `AGENTS.md`；
3. Agent 模式且有固定 `AGENTS.md` Fixture；
4. Plan 模式且有同一 Fixture；
5. `opencowork.compaction.v1`。

每个 Snapshot 断言完整 System Message 的 UTF-8 字节、SHA-256、Prompt 版本和
有序来源列表。BOM、CRLF / CR、尾部空行、`64 KiB` 边界、NUL、非法 UTF-8 和
符号链接边界使用独立聚焦测试，不扩张为组合矩阵。M3 不对真实模型回答措辞建立
Golden Snapshot、质量分数或非确定性断言。

### 2.9 Provider 错误分类与重试

一次 Agent Invocation 对应一个用户 Turn，最多发起三次 Provider 调用，包含摘要、
正式回答和所有重试。两次重试的默认退避分别为 `250ms` 和 `1s`；响应携带有效
`Retry-After` 时优先采用，但单次等待最多 30 秒。

重试安全分界是首个 Content 或 Reasoning 增量成功提交到 Journal。仅在该分界之前，
以下瞬态错误只有在 Invocation Deadline 仍有剩余且尚有调用预算时允许重试：

- 连接失败、连接重置或 Attempt 请求超时；
- HTTP `408`、`429`、`500`、`502`、`503`、`504`；
- 已确认完整读取到 `[DONE]` 的内部 `provider.serverUnavailable` 标记。

以下情况不得进入瞬态重试：

- HTTP `400`、`401`、`402`、`403`、`404`、`413`、`422`；
- Provider、模型、认证或 Tokenizer 配置错误；
- TLS 证书或主机名验证错误；
- 响应协议或流解析错误，包括发生在首个可见增量之前的错误；
- 调用方取消；
- prompt-too-long；该错误只进入独立的响应式压缩路径。

远端 prompt-too-long 识别固定为：

- 本地真实 Tokenizer 预算仍是主要防线；远端识别只处理 Provider 实际限制与本地
  Profile 不一致的情况；
- 只有 HTTP `400`、`413` 或 `422` 加内部映射表中的精确 Code / Type，或经过真实
  Provider 冒烟固化的精确消息前缀，才能归一化为 prompt-too-long；
- 首批千问 Token Plan 映射接受 `code = InvalidParameter` 且消息以前缀
  `Range of input length should be [` 或
  `Total message token length exceed model limit (` 开始；
- DeepSeek 在真实冒烟取得稳定 Fixture 前不增加消息规则；未识别的
  `400` / `413` / `422` 保持普通不可重试请求错误，不触发 Partial Compaction；
- Code、Type 和消息前缀都使用 Ordinal 精确比较，不做大小写归一化、包含匹配、
  本地化猜测或正则兜底；
- 新增或修改映射必须同时提供官方文档或脱敏真实响应证据和 Contract Fixture，
  不修改公共 `IChatCompletionClient` 契约。

一旦 Content 或 Reasoning 增量成功提交，后续任何错误都以保留已提交部分输出的
Terminal Failure 结束，不自动重试。所有尝试共享同一个 Invocation Correlation，
同时具有不同的 Attempt Number。

### 2.10 Agent / Plan 模式状态

模式状态归属固定为：

- `OpenCoWork.Abstractions` 定义 `AgentMode { Agent, Plan }`，默认值为 `Agent`；
- `ThreadSnapshot` 持久化当前 `AgentMode`；
- 创建 Turn 时将 Thread 当前模式复制为不可变的
  `TurnSnapshot.EffectiveAgentMode`；Turn 恢复始终使用该值，不受后续模式切换影响；
- `AgentFactory` 只读取 `EffectiveAgentMode`，并将它写入唯一 System Message 和
  `AgentInvocationDraft`；
- M3 的 Tool Snapshot 仍为空；M4 必须直接使用 `EffectiveAgentMode` 生成工具视图，
  Plan 模式只允许只读能力，不能仅靠 Prompt 自觉约束；
- Provider 输出和模型内容都不能修改模式。

模式切换与并发语义固定为：

- `ISessionService` 新增 `SetAgentModeAsync(SetAgentModeRequest)`；请求只包含 Thread ID、
  Idempotency Key、Expected Sequence 和目标 `AgentMode`；
- 成功切换追加 `ThreadModeChanged` Journal Event，并沿用现有顺序更新 Projection 和
  Subscription Event；
- 切换命令沿用现有 Thread Gate、Expected Sequence 和幂等语义；
- 活跃 Turn 不受切换影响，切换只作用于之后创建的 Turn；
- `QueuedTurnInputSnapshot` 在入队时冻结 `EffectiveAgentMode`；后续切换、重排或移除
  不修改既有队列项；
- 切换后新入队的输入，以及空闲时直接创建的 Turn，使用新的 Thread 当前模式；
- CLI 使用 `/mode agent | plan` 调用同一 Session 命令；M5 Wire 只复用该命令，
  不复制模式状态机。

### 2.11 CLI 入口与 Thread 身份

M3 新增 `opencowork chat` 作为真实多轮对话入口：

- 不带参数时创建新 Thread；
- `opencowork chat --thread <threadId>` 只按精确 Thread ID 恢复，不自动选择
  “最近 Thread”，也不做模糊匹配；
- `--provider <providerId>` 与 `--model <modelId>` 只能成对出现；创建新 Thread 时
  省略两者则使用工作区默认值，恢复 Thread 时省略两者则沿用 Thread 持久化选择；
- 创建新 Thread 前必须验证最终 Provider / Model 组合；配置无效时不得留下空
  Thread；
- 恢复 Thread 时，显式 Provider / Model 组合通过
  `SetThreadModelAsync(SetThreadModelRequest)` 原子更新；请求携带 Thread ID、
  Idempotency Key、Expected Sequence、Provider ID 和 Model ID，并沿用 Thread Gate；
- 更新成功追加 `ThreadModelChanged` Journal Event；本地可验证的配置错误使请求失败，
  Thread 原选择保持不变；
- 活跃 Turn 使用已经冻结的 Invocation Snapshot，不受选择更新影响；排队输入仍在
  真正创建 Turn 时读取 Thread 当前选择；
- 启动后先在 `stderr` 显示 Thread ID、名称、模式、Provider 和模型，保持
  `stdout` 可被脚本安全消费；
- 交互循环忽略空输入，每次输入必须等待当前 Turn 进入终态后才接受下一条；
- 重定向标准输入时沿用同一循环，不创建单独的批处理执行路径；
- `/exit` 或 EOF 只退出 CLI，不归档、不暂停、不删除 Thread；
- 交互模式下，Turn 运行时第一次 `Ctrl+C` 调用 `CancelTurnAsync`，继续消费事件直到
  `TurnCancelled` 后返回输入提示；
- 取消等待期间重复 `Ctrl+C` 只保持同一次幂等取消，并且只显示一次
  `Cancelling...`，不得强杀进程；
- CLI 空闲时 `Ctrl+C` 等同 `/exit`；重定向标准输入时收到 `Ctrl+C`，取消当前
  Turn 并等待终态后退出；
- M3 不提供“再次按 `Ctrl+C` 强制退出”的旁路，避免留下本可正常提交的
  Running Turn；
- M3 不实现 TUI、Thread 选择器、历史搜索、`/provider` 或 `/model` 交互命令；
- 创建、恢复、提交输入、模式切换和退出前状态读取都通过 `ISessionService`，
  CLI 不维护第二套会话状态。

用户输入与斜杠命令边界固定为：

- 交互模式和重定向标准输入都以一个物理文本行作为一次用户输入；交互模式下一行
  对应一个 Turn，M3 不实现多行编辑器、粘贴模式或续行协议；
- 空白行忽略；非命令消息保留读取到的原始首尾空白，不由 CLI Trim 后再提交；
- 只有交互模式解析 Trim 后精确匹配的 `/exit`、`/mode agent` 和
  `/mode plan`；命令不创建 `UserMessage`，`/mode` 只通过 2.10 的 Session 命令
  持久化，`/exit` 不写 Journal；
- 交互模式下 Trim 后以 `//` 开头的输入删除第一个 `/` 后作为普通消息提交，
  因此 `//exit` 可发送字面 `/exit`；其他未知斜杠输入也作为普通消息；
- 重定向标准输入不解析或转义斜杠命令，所有非空行都作为普通消息，EOF 仍按既有
  规则退出；
- CLI 使用 BCL 实现有界行读取，在构造完整超大字符串前限制单行
  `256 KiB UTF-8`；提交时继续复用 M2 的 Core 权威校验，不创建第二套上限；
- NUL、非法 Unicode 或超过 `256 KiB UTF-8` 的输入在创建 Turn 前拒绝，不占用
  Sequence、不写 Journal；NUL 或非法 Unicode 使用 `context.inputInvalid`，超限
  使用 `context.inputTooLarge`，交互模式继续接受下一条输入。

当前用户输入不参与 Micro / Partial Compaction。若 System Message、当前
`UserMessage`、消息边界和预留输出已经超过可用上下文窗口，则以
`context.inputTooLarge` 结束已创建的 Turn：保留已提交的用户输入和终态用于审计，
但不截断、不拆分、不调用 Provider、不记录 Usage，也不把失败 Turn 纳入后续模型
历史。历史压缩仍可能解决“旧历史加当前输入”超窗，但不得伪装成压缩了当前输入。

流式显示固定为：

- Content Delta 原样写入 `stdout`；
- Reasoning Delta 写入 `stderr`，交互式 TTY 只增加一次 `thinking>` 前缀和弱化颜色；
- 非 TTY 或设置 `NO_COLOR` 时不得输出 ANSI 控制码；重定向时 `stdout` 只保留
  Content，不被 Reasoning 或诊断信息污染；
- M3 不实现 Markdown 渲染、Spinner 或其他 TUI 状态。

失败诊断固定为：

- 使用 `error[stable_code]: safe message` 和下一行 Invocation ID 的稳定短格式；
- 已输出 Content 保留，错误在换行后显示，不覆盖、不重放；
- Turn 级 Provider 或流错误在交互模式中返回输入提示，Thread 保持可用；
- 可由本地确认的 Provider、Secret 引用、模型和 Tokenizer 配置错误在进入输入循环前
  失败并返回非零退出码；Provider 请求后才确认的认证错误按 Turn 级错误处理；
- 重定向模式任一 Turn 失败时，等待 Journal 终态后返回非零退出码；
- 用户取消只显示 `cancelled`，不按系统错误处理；
- 默认不得打印堆栈、Prompt、原始 Provider 响应或 Secret；详细异常只进入脱敏日志。

### 2.12 Agent Invocation 配置快照

每个 Turn 只创建一个不可变 `AgentInvocationSnapshot`。它由 `AgentFactory` 在
Turn 执行开始后的首个组装步骤产生，并且必须在任何 Partial Compaction 或 Provider
调用前通过 Session Sink 提交；该提交点构成 Turn 的配置冻结边界。

Snapshot 只记录：

- 稳定 Provider ID 和精确 Model ID；
- Tokenizer Profile ID 与版本；
- Response / Compaction Prompt 版本及各自 System Message SHA-256；
- 可选 Workspace 指令的相对路径、规范化内容 SHA-256、原始字节数和 Token 数；
- 上下文窗口、最大输出 Token；
- 排除 Secret 值后的配置指纹。

固定语义：

- Partial Compaction、正式回答和全部重试使用同一个 Snapshot；
- WorkspaceRuntime 生命周期内只使用同一个 `EffectiveConfigSnapshot`；配置文件和
  环境变量变化不影响当前进程中的任何 Turn；
- 排队输入在真正创建 Turn 时读取 Thread 当前 Provider / Model 选择，再从冻结的
  有效配置中解析，不在入队时复制；
- Invocation 只取得预检后保存在运行时内存中的 Secret 值；Snapshot、Journal、
  日志和错误都不得包含 Secret；
- Secret 轮换必须重启 WorkspaceRuntime；同一进程和 Invocation 的不同 Attempt
  不得切换凭据；
- 进程中断后当前 Turn 沿用 M2 规则失败，不恢复半截 Provider 请求；
- 若 Snapshot 因配置无法构造，则保留最具体的 `OCWCFGxxx` 诊断；若 Snapshot
  无法提交，则复用 M2 对应的 Session、Journal 或 Runtime 错误，不把持久化故障
  冒充配置错误。

### 2.13 真实 Provider 与发布验证

M3 的首个真实 Provider 发布矩阵固定为两条：

| Provider 路径 | 精确 Model ID |
| --- | --- |
| DeepSeek 官方 | `deepseek-v4-pro` |
| DeepSeek 官方 | `deepseek-v4-flash` |

每条真实路径只验证：

- 流式请求到达 `[DONE]`；
- 至少产生一个非空 Content；
- Finish Reason 可按 2.4 归一化；
- 返回真实、非空 Usage；
- 本地 Tokenizer 与 Provider Prompt Usage 满足 2.7 的对账阈值。

Reasoning 真实冒烟使用 `deepseek-v4-flash` 作为代表。测试只断言 Reasoning
流可选出现时能被正确归一化和提交，不比较思考内容或回答措辞。

真实测试固定为显式发布任务，不进入默认 `dotnet test`，也不在缺少安全凭据的普通
CI 或开发机上自动联网。缺少某条 DeepSeek 路径的凭据时结果是 `NotRun`，不能记为
通过；任一对外宣称支持的模型路径未通过，都阻止 M3 发布。

`M3-ACC-002` 的真实平台证据固定为：两条 DeepSeek 官方路径在 `osx-arm64`
各执行一次。本地 Fake Provider 契约、安全和故障注入套件继续证明 Provider 中立
行为；`win-x64` 真实 Provider、千问 Token Plan 和后续 Provider 的兼容性证据延期到
`docs/provider-validation-backlog.md`，未激活和通过前不得对外宣称支持。

Secret Canary 固定为两层：

- 本地 Fake Server 使用唯一人工 Canary API Key，覆盖成功、认证失败、重试、
  非成功 Body 和流中断路径；
- 安全发布 Runner 使用实际凭据完成真实冒烟，但只在进程内检查其原值是否出现在
  Journal、SQLite、Session Event、结构化日志、stdout、stderr 或测试产物中，只
  输出通过或失败，不打印或保存 Secret 原值。

重试、首个可见 Delta 后断流、非法 SSE、prompt-too-long 错误信封、`Retry-After`、
重定向、自签名证书和压缩后超限全部使用本地 Fake Server 做确定性故障注入，不用
真实 Provider 消耗 Token。真实响应不建立 Golden Snapshot，也不保存 Prompt 或
回答正文。

发布证据只记录 Commit SHA、Provider 路径、精确 Model ID、UTC 时间、Usage、
Finish Reason 和通过 / 失败 / `NotRun` 结果。证据不得包含 Base URL 中的敏感部分、
Prompt、模型正文、原始响应、环境变量值或 Secret。

### 2.14 稳定诊断目录

M3 不创建第二套通用错误对象。公开 Turn 失败继续使用 M2 的
`SessionError(Code, Message, IsRetryable)`，CLI 只负责按 2.11 渲染。配置加载、
绑定和预检直接复用现有 `OCWCFGxxx` 诊断；Session、Journal 和 Runtime 故障直接
复用 M2 的 `session.*`、`journal.*` 和 `runtime.*` 错误码。

M3 新增错误码仅限：

| Code | 含义 | 自动重试 |
| --- | --- | --- |
| `provider.authenticationFailed` | Provider 拒绝凭据，对应 HTTP `401`。 | 否 |
| `provider.quotaExceeded` | 余额、套餐或配额不足，对应 HTTP `402`。 | 否 |
| `provider.permissionDenied` | 凭据无权访问目标资源，对应 HTTP `403`。 | 否 |
| `provider.notFound` | Provider 端点或精确 Model ID 不存在，对应 HTTP `404`。 | 否 |
| `provider.invalidRequest` | 未识别为 prompt-too-long 的请求错误或其他不可重试 `4xx`。 | 否 |
| `provider.rateLimited` | Provider 限流，对应 HTTP `429`。 | 可见增量前且预算与 Deadline 允许 |
| `provider.timeout` | HTTP `408`、Attempt Deadline 或 Invocation Deadline。 | 可见增量前且 Deadline 未耗尽 |
| `provider.serverUnavailable` | 连接失败、连接重置、`5xx` 或完整服务资源不足标记。 | 仅 2.9 白名单且预算与 Deadline 允许 |
| `provider.tlsFailure` | TLS 证书链或主机名验证失败。 | 否 |
| `provider.redirectNotAllowed` | Provider 返回 `3xx`，适配器拒绝携带认证重定向。 | 否 |
| `provider.invalidStream` | Content-Type、SSE、JSON、Choice、Finish Reason 或 Usage 协议无效。 | 否 |
| `provider.outputTooLarge` | 解压后协议 Body 或 Content / Reasoning 超过固定上限。 | 否 |
| `provider.contentFiltered` | Finish Reason 为 `ContentFilter`。 | 否 |
| `provider.unsupportedToolCall` | Provider 返回 M3 不支持的 Tool Call。 | 否 |
| `provider.emptyResponse` | 完整响应没有非空 Content。 | 否 |
| `context.inputInvalid` | CLI 用户输入包含 NUL 或非法 Unicode。 | 否 |
| `context.inputTooLarge` | CLI 单行超限，或当前输入自身无法放入模型上下文。 | 否 |
| `context.instructionsInvalid` | Workspace 指令无法安全读取或规范化。 | 否 |
| `context.compactionFailed` | 压缩无效、超限、格式错误或响应式压缩后仍超窗。 | 否 |

HTTP `500`、`502`、`503`、`504` 以 `provider.serverUnavailable` 表达并允许按 2.9
重试；其他 `5xx` 使用同一错误码但不自动重试。错误码不替代
`ChatCompletionException` 的内部分类，调用方不得仅凭 Code 绕过首个可见增量和
三次调用总预算。

`response.truncated` 是成功 Turn 上的 `SystemNotice`，不是错误码；用户取消也不是
系统错误。所有安全消息、日志与事件继续遵守 M0/M2 的脱敏边界，不能包含原始
Provider Body、异常文本、绝对路径或 Secret。

## 3. 最小数据流

```text
ThreadJournal
→ 重放 AgentSession 与模型可见历史
→ AgentFactory 选择 Provider / Model 并组合 Prompt
→ 真实 Tokenizer 计算输入预算
→ 必要时执行 Micro / Partial Compaction
→ openai-compatible Chat Completions 流
→ Content / Reasoning / Usage 归一化
→ ISessionExecutionSink
→ SessionService 提交 Journal / Projection / Event
```

## 4. DotCraft 证据基线核对

| 设计面 | DotCraft 证据 | M3 处理 |
| --- | --- | --- |
| 唯一 Session Core、Thread/Turn/Item 与同 Thread 串行 | §5.1-§5.5 | 保持语义，复用 M2 的 `ISessionService`、Thread Gate 和 Journal 提交路径。 |
| AgentFactory 冻结 Provider、Context、Mode 与 Tool Snapshot | §6.1-§6.3 | 保持职责；M3 将其收窄为同步、确定性、无副作用的内部组装器，Tool Snapshot 为空。 |
| 多 Provider ChatClient 责任链 | §6.2、§6.4、§14.7 | OpenCoWork 重设计为单个 Provider 中立 `IChatCompletionClient` 和 OpenAI-compatible Chat Completions 最小子集；不引入 `Microsoft.Agents.AI` 或厂商 SDK。 |
| Agent / Plan 持久模式与 Plan 只读工具视图 | §5.2、§6.3 | 保持模式语义；M3 冻结到 Turn，M4 再按该值生成只读工具快照。 |
| Token 跟踪、上下文窗口和 Usage | §8.1-§8.2、§9.3、§13.4 | 保持预算与用量分离；真实 Tokenizer、版本化 Profile 和对账阈值是 OpenCoWork 补强，不冒充 DotCraft 原实现。 |
| Micro / Partial Compaction、Checkpoint 与审计历史不删除 | §8.2、§9.4、§14.7 | 保持可恢复语义；具体 80% / 60% / 50% 水位、摘要格式和三次调用预算是 OpenCoWork 冻结参数。 |
| prompt-too-long 响应式压缩 | §5.4、§8.3、`CTX-02` | 明确重设计：DotCraft 记录为当前 Turn 失败并提示下一 Turn 重发；M3 只在尚无可见输出时，于同一 Invocation 内压缩并最多重试一次。两者都禁止重复当前 Turn 或已显示片段。 |
| CLI 宿主 | §2.1、§10.1 | 明确重设计：DotCraft 记录为一次性 CLI 会话；M3 提供 `opencowork chat` 交互式多轮循环与精确 Thread 恢复。 |
| 工具、AppServer、ACP 与扩展能力 | §7、§10 | 按路线延期到 M4-M6；M3 不提前复制其抽象或 Provider 私有能力。 |

核对结论：M3 保留 DotCraft 规范中可观察的 Session、模式、上下文和恢复不变量，
但不以复刻其 SDK 组合、私有类型或交互流程为目标。上表列出的重设计均受 M0
能力台账和 M3 验收目录约束，不得在实现计划中重新解释为 DotCraft 兼容。

## 5. 验收映射

| 验收编号 | 冻结设计覆盖 | 预期证据 |
| --- | --- | --- |
| `M3-ACC-001` | §2.1、§2.8、§2.12：单一执行路径、确定性 AgentFactory、唯一 System Message 和空 Tool Snapshot。 | AgentFactory 组装顺序测试、五份 Prompt Golden Snapshot、配置快照重放断言。 |
| `M3-ACC-002` | §2.1、§2.2、§2.13、§2.14：Fake 与首个真实 Provider 共用中立契约，Secret 不进入持久化与输出面。 | Provider Contract/Security Tests、`osx-arm64` 两条 DeepSeek 官方短冒烟、Secret Canary 全输出面扫描。 |
| `M3-ACC-003` | §2.3、§2.4：Content、Reasoning、Usage 和终态只经 Session Sink 按 Journal 顺序提交并恢复。 | Chunk/Reasoning/Usage/Terminal 顺序测试、Journal 重放与投影重建快照。 |
| `M3-ACC-004` | §2.5、§2.9：首个可见增量前只重试白名单瞬态错误，提交后保留部分输出且不重试。 | Fake Server 两阶段断流、协议错误和 Invocation/Attempt 计数故障注入。 |
| `M3-ACC-005` | §2.6、§2.7、§2.12：真实 Token 预算、80%/60%/50% 水位和权威 Checkpoint 可重放。 | Token 边界测试、Micro/Partial Compaction 测试、崩溃后 Checkpoint 重放。 |
| `M3-ACC-006` | §2.6、§2.9：prompt-too-long 只在无可见输出时触发一次响应式压缩并共享三次调用预算。 | 精确错误信封 Fixture、当前 Turn 唯一性断言、压缩后仍超窗的确定性失败测试。 |
| `M3-ACC-007` | §2.4：Provider Usage 权威，估算值显式标记，同一调用键只累计一次。 | 流式、重试、压缩和恢复后的 Usage Ledger 对账测试。 |
| `M3-ACC-008` | §2.8、§2.10、§2.12：Agent/Plan 持久化到 Thread、冻结到 Turn，并作为 M4 工具策略输入。 | 模式切换、队列冻结、重启恢复和 M4 输入快照测试。 |

## 6. 冻结结论

M3 设计于 2026-07-27 完成全文一致性审查并冻结，并于 2026-07-28 按用户确认将
真实发布承诺收敛为 `osx-arm64` 上的 DeepSeek 官方首个 Provider。实现计划不得
提前引入 M4+ 的工具、Provider 插件、动态能力协商或厂商专用接口；后续 Provider
必须按待验证清单显式激活后再增加发布测试和支持声明。
