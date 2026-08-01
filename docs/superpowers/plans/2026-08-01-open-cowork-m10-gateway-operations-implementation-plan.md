# OpenCoWork M10 Gateway and Operations 实施计划

**Status:** In Progress；Gate 0 和 Outcome 1–8 已完成。2026-08-01 用户已授权按本计划实施
并提交；公网访问、真实 Secret/第三方 Webhook、推送和非本机平台操作仍未授权。

**Goal:** 在现有七程序集、单 Workspace SQLite、唯一 Session Core 和
`WorkspaceRuntime` 生命周期内，交付内建 Webhook Gateway、可靠 Inbound/Outbox、
Hub 与 Operations 查询，并用 State v9、OpenCoWork Wire 1.4 和双平台发布目录证据关闭
OpenCoWork 1.0 的最后一个功能 Slice。

**Why planning is required:** M10 同时引入外部信任边界、HMAC 认证、有界媒体、跨系统
至少一次投递、持久去重与顺序、State v9、用户级 Workspace Registry、观测链路、
Wire 1.4 和双平台真机证据。实施顺序错误会造成未认证内容入库、重复 Session 提交、
出站静默丢失、跨 Channel 故障扩散、Secret/正文泄漏或迁移后无法恢复。

**Acceptance:** 冻结设计中的 `M9-ACC-001` 至 `M9-ACC-010` 全部有可复现证据；同一
Inbound 只提交一次、Outbox 在崩溃窗口不静默丢失、同 Conversation 保序、Channel
故障隔离、Hub 不依赖当前目录、Correlation/Usage/Trace/Heartbeat/Insight 安全可查；
Wire 1.0–1.3 完整回归、Wire 1.4 黑盒通过；`win-x64` 与 `osx-arm64` 分别从本机发布
目录通过 M10 TestClient。交叉发布不替代真机，任一平台缺证据时 M10 保持未完成。

对应设计：
[M10 Gateway and Operations 设计](../specs/2026-08-01-open-cowork-m10-gateway-operations-design.md)

路线与验收：

- [OpenCoWork Runtime 1.0 路线规格](../specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md)
- [M0-M11 验收目录](../specs/2026-07-25-open-cowork-m0-acceptance-catalog.md)
- [OpenCoWork Runtime 1.0 Milestone](../../milestones/2026-07/open-cowork-runtime-1-0/README.md)
- [双平台发布验证台账](../../platform-release-validation-ledger.md)

## 当前实现基线

- 计划基线为 `dev` 的 `97aa1690a62a`；执行前必须重新确认分支、HEAD、工作区和台账，
  不覆盖用户未提交改动；
- 生产程序集仍冻结为七个，M10 只原位扩展现有项目和测试程序集，不新增工程；
- Workspace State 当前为 v8，`StateMigrations` 与既有 Migration Contributor 是唯一
  升级链；M10 升级到 v9，不增加第二个数据库；
- OpenCoWork Wire 当前最高为 1.3，连接协商、Catalog、Cursor、Revision、通知和
  TestClient 已有入口；M10 只做 1.4 的加法；
- `ISessionService`、Session Journal/Queue/Projection/Recovery 是唯一 Thread/Turn
  权威；Channel 只能通过它创建 Thread 和排队输入；
- `WorkspaceRuntime`、`ModuleLifecycleCoordinator` 与生成模块目录是唯一生命周期和
  依赖顺序权威；Gateway 不增加第二个 Hosted Service 框架；
- `ProviderSecretStore.cs` 已有 macOS Keychain、Windows Credential Manager、Secret
  Lease 与 Redactor 原语；M10 只下沉最小通用 OS Secret Store，Provider 行为保持；
- M7、M8、M9 的 `win-x64` 证据仍以执行时的平台台账为准；它们不阻塞 M10 Outcome
  1–9 的离线实施，但所有相关回归和 M10 双平台证据未齐前不得关闭 M10。

## 最小变更图

优先原位扩展现有边界。文件可在职责不变时合并；不得为计划表机械制造一文件一类型。

| 路径 | M10 变更 |
| --- | --- |
| `src/OpenCoWork.Abstractions/` | 增加 Channel、Operations、Hub 与 Wire 1.4 共享契约；Session Queue Fact 只增加可选 `correlationId`。 |
| `src/OpenCoWork.Core/State/StateRuntime.cs` | 升级 State v9，创建 M10 十表和 Core 所属 Correlation 列。 |
| `src/OpenCoWork.Automations/AutomationsState.cs`、`src/OpenCoWork.Teams/TeamsState.cs` | 各自贡献现有表的可选 Correlation 列，不越权创建 M10 表。 |
| `src/OpenCoWork.Core/Gateway/` | 放置 Gateway 领域入口、媒体、Inbound/Outbox、Reconciler、Hub 与 Operations 实现；实现时取最少文件。 |
| `src/OpenCoWork.Protocol/` | 放置 loopback Kestrel Webhook Adapter、Webhook Sender 和 Wire 1.4 映射；不承载领域状态。 |
| `src/OpenCoWork.App/Program.cs` | 组合 `gateway` 主宿主、模块注册和 `channel`/`hub`/`ops` CLI。 |
| `tests/` | 在现有 Core、Protocol、Integration、Generators 与 TestClient 工程内补证据，不新增测试工程。 |

## 执行规则

- 只在 `dev` 分支按 Outcome 顺序执行；首次实施前重新读取 Milestone README/CHECKLIST、
  M10 Design、路线规格、根目录 DotCraft 规范和平台台账；
- 每个 Outcome 是一个且仅一个 Git Commit 边界：Red → 确认因目标缺失而失败 → 最小
  实现 → focused tests → 全量 Release test/build → 独立 Commit；未获得提交授权时停在
  验证通过的工作区并报告建议 Commit；
- 上一个 Outcome 未通过全量回归或遗留未提交实现，不开始下一个 Outcome；
- 默认测试使用临时 Workspace/User Profile、`TimeProvider`、BCL Loopback、Fake
  Session/Sender 和故障注入；不读取真实凭据、不监听非 loopback、不访问公网；
- 复用现有 SQLite 迁移、Session、Trust、Secret Redactor、`HttpClient`、Kestrel、
  `ActivitySource`、Keyset Cursor、Revision、模块生成器和 TestClient；不新增 NuGet、
  消息代理、状态机、Scheduler、Telemetry SDK、HTTP/重试框架或通用 Channel Factory；
- Test Channel 只存在于测试代码；产品 Catalog 仅包含 Webhook；
- Secret、签名、正文、媒体内容、Callback URL、第三方 Header/Body、绝对路径和原始
  Exception 不得进入 Journal、SQLite 非必要列、日志、Trace、Wire、证据或测试产物；
- 任何公共 Wire 1.4、State v9 权威表、投递语义或安全顺序需要改变时，先停止并修订
  Design/Plan，不用兼容分支掩盖偏差。

首次执行 Outcome 1 前运行：

```bash
dotnet restore OpenCoWork.slnx
dotnet test OpenCoWork.slnx -c Release --no-restore
dotnet build OpenCoWork.slnx -c Release --no-restore
```

每个 Outcome 的 focused tests 通过后运行：

```bash
dotnet test OpenCoWork.slnx -c Release --no-restore
dotnet build OpenCoWork.slnx -c Release --no-restore
```

## 实施前 Gate 0：基线与权限

Gate 0 不形成独立 Commit，也不改变 Milestone 状态。

- 确认 `dev`、目标 HEAD、工作区改动归属、M7–M9 当前实现与平台台账；
- 确认 `DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md` 仍存在且被忽略，
  只作本机证据，不进入提交；
- 用 CodeGraph 重新核对 Runtime Start/Stop、State Contributor、Session Queue、Wire
  Catalog、Secret Store 和 TestClient 当前调用路径；
- 明确本次授权范围。若只授权离线实现，则 Outcome 1–9 使用临时状态和 Loopback；
  Outcome 10 的 OS Secret 写入、发布目录真机与任何外部入口另行确认；
- 若基线已有行为与 Design Freeze 冲突，先修订设计，不开始 Outcome 1。

### Outcome 1：冻结契约、Config、Trust/Secret 与 Gateway 主宿主

- Red:
  - 扩展 Abstractions、Config、Module Generator、Workspace 初始化和架构测试，覆盖
    `gateway` 配置边界、稳定 Module ID/依赖、七程序集和依赖方向；
  - 扩展 Trust/Auth/Logging 测试，覆盖 `ExternalChannel`、配置摘要变化重授信、
    Environment/OS Store 取值、Lease/Redaction 和 Provider Secret 回归；
  - 扩展 CLI/Host 测试，证明 `gateway` 只能显式成为主宿主，`app-server` 不启动
    Webhook Intake，`cli`/`acp` 不启动长期循环。
- Work:
  - 在 `OpenCoWork.Abstractions` 增加最小 Channel/Operations 服务契约和稳定错误码；
  - 增加严格 `GatewayConfig`，只保留 Design Freeze 已批准的字段与上限；
  - 将 Provider 专用 OS Secret Store 最小改名/下沉为 Core 内部共享原语，复用现有
    Keychain/Credential Manager、Lease 和 Redactor，不改变 DeepSeek Auth 行为；
  - 在 `OpenCoWork.App/Program.cs` 和模块生成路径接入 `gateway` 主宿主骨架；此
    Outcome 不打开监听、不创建 M10 状态、不发送请求。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ConfigurationPipelineTests|FullyQualifiedName~ProviderAuthTests|FullyQualifiedName~StructuredLoggingTests|FullyQualifiedName~WorkspaceInitializerTests'`
  - `dotnet test tests/OpenCoWork.Generators.Tests/OpenCoWork.Generators.Tests.csproj -c Release --no-restore`
  - `dotnet test tests/OpenCoWork.ArchitectureTests/OpenCoWork.ArchitectureTests.csproj -c Release --no-restore`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~CliIntegrationTests|FullyQualifiedName~RuntimeCompositionIntegrationTests'`
- Acceptance contribution: `M9-ACC-001`、`M9-ACC-007`、`M9-ACC-009`。
- Commit: `feat(m10): freeze gateway contracts and host`

### Outcome 2：升级 State v9、Workspace ID 与 M10 路径

- Red:
  - 新增 `StateMigrationV9Tests`，覆盖新库直达 v9、v8→v9、十表/索引/约束、三组
    Correlation 列、备份、DDL/Commit 故障、完整性校验、重复启动和 v8 恢复；
  - 扩展 Workspace Path 测试，覆盖媒体根、临时文件、不同 CWD、路径包含、Symlink、
    Junction/Reparse Point 和不跟随链接；
  - 覆盖稳定 `workspace_id` 首建、重启、并发初始化与损坏状态失败。
- Work:
  - 由 Core Contributor 创建 `operations_state`、`channels`、
    `channel_thread_mappings`、`channel_inbound_messages`、`channel_media`、
    `channel_outbox`、`workspace_heartbeat`、`trace_spans`、`insight_runs` 和
    `improvement_proposals`；
  - Core、Automations、Teams Contributor 分别为自己拥有的 `turns`、
    `automation_runs`、`agent_runs` 增加 nullable `correlation_id`；
  - 复用 State v8 原子迁移/备份/恢复框架和 `WorkspacePaths`，增加
    `.opencowork/runtime/external-channel-media`；不增加数据库或并行迁移器。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~StateMigrationV9Tests|FullyQualifiedName~StateRuntimeTests|FullyQualifiedName~WorkspacePath'`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~State|FullyQualifiedName~Workspace'`
- Acceptance contribution: `M9-ACC-002`、`M9-ACC-005`、`M9-ACC-006`、
  `M9-ACC-010`。
- Commit: `feat(m10): migrate workspace state to v9`

### Outcome 3：实现 Webhook HMAC、严格 Envelope 与媒体安全边界

- Red:
  - 新增 Protocol Loopback 测试，覆盖 loopback-only、Body 上限、Timestamp 五分钟窗、
    HMAC-SHA256 原始字节、固定时间比较、统一 401、Strict JSON、重复/未知字段、
    Callback Redirect 禁止和稳定 HTTP 错误；
  - 新增媒体 Corpus，覆盖数量/单项/总量/Base64 上限、允许类型与魔数、摘要、恶意
    文件名、绝对路径、`..`、Symlink、Junction/Reparse、类型伪造、篡改和孤儿文件；
  - 证明任何认证或 Schema 失败都不写 SQLite、文件或 Session。
- Work:
  - 在 Protocol 内用 Kestrel 与 BCL Crypto/JSON 实现 Webhook v1 Adapter，严格按
    Body → Timestamp → Ready → Secret → HMAC → JSON → Schema → Media 顺序校验；
  - 在 Core 实现 SHA-256 内容寻址媒体存储、临时文件原子提交和 State 元数据；
  - 只保存安全媒体引用，不把二进制送入 M9 Provider；不增加 MIME/上传依赖。
- Verify:
  - `dotnet test tests/OpenCoWork.Protocol.Tests/OpenCoWork.Protocol.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~Webhook|FullyQualifiedName~ChannelMedia'`
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~GatewayMedia|FullyQualifiedName~WorkspacePath'`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~Webhook|FullyQualifiedName~ChannelMedia'`
- Acceptance contribution: `M9-ACC-001`、`M9-ACC-005`、`M9-ACC-007`。
- Commit: `feat(m10): secure webhook intake and media`

### Outcome 4：交付 Inbound 去重、Thread 映射、Session 幂等提交与顺序

- Red:
  - 覆盖相同/冲突 `(channelId, externalMessageId)` 的顺序、并发与重启重放；相同摘要
    返回 202，冲突返回稳定错误且不提交 Session；
  - 对 Thread Create、Mapping Commit、Queue Commit、Delivered Commit 各崩溃窗口
    注入故障，证明持久 Idempotency Key 只产生一个 Thread/Turn；
  - 覆盖单 Conversation 100 条保序、32 Conversation 并行、单分区毒消息不阻塞其他
    分区，以及媒体引用进入 Session 文本摘要但不进入 Provider 二进制输入。
- Work:
  - 实现 `GatewayService` 的 Inbound 事务、Body 摘要冲突、UUIDv7 Idempotency Key、
    Conversation→Thread 持久映射和 `partitionSequence`；
  - 只通过现有 `ISessionService` 创建 Thread、排队输入和读取终态；给 Queue Fact/
    Projection 增加向后兼容的可选 `correlationId`；
  - 在唯一 `GatewayReconciler` 中按 Conversation 串行、跨分区有界并行地恢复
    `pending`/`dispatching`，不增加第二个队列或 Scheduler。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~GatewayInbound|FullyQualifiedName~SessionQueue|FullyQualifiedName~SessionProjection'`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~GatewayInbound|FullyQualifiedName~SessionCrashRecovery'`
- Acceptance contribution: `M9-ACC-002`、`M9-ACC-004`、`M9-ACC-010`。
- Commit: `feat(m10): dispatch inbound messages exactly once locally`

### Outcome 5：交付 Outbox、Sender 隔离、重试与崩溃恢复

- Red:
  - 覆盖 Turn 终态→Outbox Insert、Claim、HTTP Send、Sent Commit 每个故障窗口；发送后
    崩溃必须以相同 `deliveryId` 重试，禁止声称远端 Exactly Once；
  - 覆盖两分钟 Lease、过期回收、`Retry-After` 上限、1s/5s/30s/2m/10m 延迟、五次
    Dead Letter、手动 Retry Idempotency 和未知结果；
  - 覆盖同 Conversation 串行、每 Channel 并发/最小间隔、跨 Channel 的 429、超时、
    凭据失败、断连与毒消息隔离。
- Work:
  - 在 Core 实现终态 Outbox 原子写入、Claim/Lease/Retry/Dead Letter 和唯一
    `GatewayReconciler` 恢复路径；
  - 在 Protocol 实现具体 Webhook Sender，固定严格 Outbound Envelope、HTTPS、禁
    Redirect、有界响应读取和相同 Delivery ID；
  - 每 Channel 使用独立有界发送状态和 Secret Lease；停止时先停 Intake、再 Drain
    有界工作、归还 Lease 并释放 HttpClient/Kestrel/Secret。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~GatewayOutbox|FullyQualifiedName~GatewayReconciler'`
  - `dotnet test tests/OpenCoWork.Protocol.Tests/OpenCoWork.Protocol.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~WebhookSender'`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~GatewayOutbox|FullyQualifiedName~GatewayRecovery|FullyQualifiedName~ChannelIsolation'`
- Acceptance contribution: `M9-ACC-001`、`M9-ACC-003`、`M9-ACC-004`、
  `M9-ACC-007`、`M9-ACC-009`。
- Commit: `feat(m10): deliver reliable channel outbox`

### Outcome 6：接通 Usage、Trace、Correlation 与安全结构化日志

- Red:
  - 覆盖 Gateway→Session→Provider→Tool、Automation、CoWork、Outbox 的同一
    `correlationId`，旧记录缺列时安全为空；
  - 覆盖 BCL `ActivitySource`/`ActivityListener` Span、容量饱和 Drop 计数、持久分页、
    重启查询和不保存正文/Secret/URL/绝对路径；
  - 覆盖 `provider_usage` 按 Channel/Model/时间桶聚合，实际 Usage 与 Estimate 分离，
    无重复 Usage 表或双写对账源；
  - Secret Canary 扫描 State、Journal、Trace、日志、Wire、stdout/stderr 和测试产物。
- Work:
  - 在 Inbound 首次持久化生成安全 Correlation，并沿现有 Session Fact、Provider、Tool、
    Automation、Teams 与 Outbox 边界传播；
  - 用 BCL `ActivitySource`/`ActivityListener` 和有界队列写 `trace_spans`，不增加
    OpenTelemetry 包或第二套日志管线；
  - `IOperationsQueryService` 直接查询既有 `provider_usage` 与 M10 Trace，保持单一
    Usage Authority；Structured Logging 只增加安全 Scope。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~Correlation|FullyQualifiedName~Trace|FullyQualifiedName~Usage|FullyQualifiedName~StructuredLogging'`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~AutomationCorrelation|FullyQualifiedName~CoWorkCorrelation|FullyQualifiedName~GatewayCorrelation'`
- Acceptance contribution: `M9-ACC-008`、`M9-ACC-010`。
- Commit: `feat(m10): correlate gateway operations`

### Outcome 7：交付 Heartbeat、Hub、Dashboard 与 Workspace Insights

- Red:
  - 覆盖 30 秒 Heartbeat、90 秒 Stale、时钟回拨/跳跃、进程重启和 Stop 后不再续写；
  - 覆盖用户级 `~/.opencowork/workspaces.json` 原子 Upsert、并发、不同 CWD、缺失/
    损坏单项隔离、显式注册与不自动删除；
  - 覆盖 Dashboard 只读聚合、离线 Workspace 状态、跨 Workspace 查询不启动 Runtime；
  - 覆盖 Insight 确定性规则、Fingerprint 去重、Revision、Archive、分页、重启和绝不
    调用 Agent/Provider/Tool 或自动 Apply。
- Work:
  - 在 `OperationsRuntime` 中用现有模块生命周期管理 Heartbeat、Trace Collector 和
    Insight 周期；不注册独立 `IHostedService`；
  - 实现稳定 Workspace ID 驱动的用户级 Registry 和只读 Hub 查询；原子替换失败保留
    旧文件，不因暂时离线删除记录；
  - Dashboard 只组合 Channel/Queue/Usage/Trace/Heartbeat 数据；Insight 只运行设计
    冻结的本地确定性规则并持久化 Proposal。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~Heartbeat|FullyQualifiedName~HubRegistry|FullyQualifiedName~Dashboard|FullyQualifiedName~WorkspaceInsight'`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~Hub|FullyQualifiedName~Heartbeat|FullyQualifiedName~Insight|FullyQualifiedName~RuntimeLifecycle'`
- Acceptance contribution: `M9-ACC-006`、`M9-ACC-008`、`M9-ACC-009`。
- Commit: `feat(m10): add workspace operations hub`

### Outcome 8：交付 Wire 1.4、通知、CLI 与版本隐藏

- Red:
  - 扩展握手/Catalog 测试，证明 1.0–1.3 看不到 1.4 方法/通知/错误，1.4 可协商且
    旧契约逐字节兼容；
  - 覆盖 `channel/*`、`hub/*`、`usage/query`、`trace/*`、`heartbeat/get`、
    `insight/*` 的合法/非法参数、Keyset Cursor、Revision、Idempotency、分页和
    `media/read` 每块不超过 256 KiB；
  - 覆盖 `channel/changed`、`heartbeat/changed`、`insight/changed` 的订阅过滤、慢
    客户端、重连和通知丢失后靠查询恢复；
  - 扩展 CLI 与 TestClient，证明命令只调用稳定服务，不绕过 Wire/State Authority。
- Work:
  - 在 Abstractions 和 Protocol 的现有版本协商/Catalog/Connection 分片上增加 Wire
    1.4 DTO、方法、错误映射与通知；
  - 实现设计冻结的全部方法，不增加远程管理、Secret 读回或任意文件读取；
  - 在 `OpenCoWork.App/Program.cs` 增加 `channel`、`hub`、`ops` 命令和稳定 JSON/
    人类输出；本机 Secret set/clear 与只读查询保持权限分离；
  - 扩展 Protocol TestClient 的 M10 场景，Test Channel 仍只在测试组合中出现。
- Verify:
  - `dotnet test tests/OpenCoWork.Protocol.Tests/OpenCoWork.Protocol.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~OperationsWire|FullyQualifiedName~WireHandshake|FullyQualifiedName~WireVersion'`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ChannelCli|FullyQualifiedName~HubCli|FullyQualifiedName~OperationsCli'`
  - `dotnet build tests/OpenCoWork.Protocol.TestClient/OpenCoWork.Protocol.TestClient.csproj -c Release --no-restore`
- Acceptance contribution: `M9-ACC-005`、`M9-ACC-006`、`M9-ACC-008`、
  `M9-ACC-010`。
- Commit: `feat(m10): expose operations over wire 1.4`

### Outcome 9：关闭故障、安全、固定负载与离线回归

- Red:
  - 把设计 §17.1 的全部崩溃窗口、安全 Corpus、Registry/Trace/Insight 故障和 Wire
    版本矩阵固化为自动化回归；
  - 增加固定负载 Runner：8 Channel × 32 Conversation × 100 条、10,000 混合消息、
    100,000 Trace、10,000 Usage、1,000 Proposal；分页全遍历无重复/遗漏；
  - 增加全输出面 Secret Canary、句柄/端口/Timer/Lease/后台 Task 清理和重复
    Start/Stop 测试。
- Work:
  - 只修复自动化揭示的根因，优先修改共享权威路径；不为负载样本增加缓存、队列框架、
    公共调参或未冻结 SLA；
  - 记录 Intake/Dispatch/Outbox/Reconcile/SQLite Busy/Trace Drop/内存/句柄基线，
    结果只作为 M11 发布预算输入；
  - 完成 Wire 1.0–1.4、M7 CoWork、M8 Automations、M9 Provider 的离线全回归。
- Verify:
  - `dotnet test OpenCoWork.slnx -c Release --no-restore --blame-hang-timeout 90s --blame-hang-dump-type none`
  - `dotnet build OpenCoWork.slnx -c Release --no-restore`
  - `dotnet format OpenCoWork.slnx --verify-no-changes --no-restore`
- Acceptance contribution: `M9-ACC-001..010` 的离线证据。
- Commit: `test(m10): close gateway reliability matrix`

### Outcome 10：完成双 RID 发布、真机台账与交付归档

- Red:
  - 发布 Runner/TestClient 必须在目标发布目录缺能力时失败，并显式区分
    `osx-arm64`、`win-x64`、交叉发布和真机证据；
  - 平台矩阵覆盖 Gateway/HMAC/Reverse Proxy 模拟、多 Channel、强杀恢复、同
    Delivery ID、Dead Letter、媒体链接 Corpus、OS Secret、不同 CWD Hub、Wire
    1.0–1.4、Secret Canary 和清理残留。
- Work:
  - 从当前 `dev` 固定修订发布 `osx-arm64` 与 `win-x64`，先证明两套产物可生成；
  - 分别在 macOS 与 Windows 真机的发布目录执行相同 TestClient/Runner；OS Secret
    Set/Use/Clear 只在用户明确授权的临时 Profile 中进行并清理，记录可复现脱敏证据；
  - 只把实际执行的平台结果写入 `docs/platform-release-validation-ledger.md`；交叉发布
    或另一平台缺证据时保持该项 Pending，不伪造 Passed；
  - 两平台全部 Passed 后，同步 M10 Design/Plan、Acceptance/Capability/Platform
    Ledger、Milestone CHECKLIST/INDEX，创建 M10 Archive；任一平台 Pending 时不创建
    “已交付”归档、不把 M10 标 Done。
- Verify:
  - `dotnet test OpenCoWork.slnx -c Release --no-restore`
  - `dotnet build OpenCoWork.slnx -c Release --no-restore`
  - `dotnet format OpenCoWork.slnx --verify-no-changes --no-restore`
  - 分别从 `osx-arm64`、`win-x64` 发布目录执行 M10 Protocol TestClient/Release
    Runner，并按平台台账记录真实命令、修订和结果。
- Acceptance contribution: 双平台齐全时关闭 `M9-ACC-001..010`；否则保持对应项
  Planned/Pending。
- Commit: `docs(m10): close gateway operations delivery`

## 覆盖矩阵

| Outcome | 冻结设计主边界 | 验收编号 |
| ---: | --- | --- |
| 1 | 架构、Config、Trust/Secret、主宿主 | M9-ACC-001、007、009 |
| 2 | State v9、Workspace ID、媒体路径 | M9-ACC-002、005、006、010 |
| 3 | Webhook v1、HMAC、严格 Envelope、媒体 | M9-ACC-001、005、007 |
| 4 | Inbound、映射、Session 提交、顺序 | M9-ACC-002、004、010 |
| 5 | Outbox、Sender、重试、Dead Letter、恢复 | M9-ACC-001、003、004、007、009 |
| 6 | Usage、Trace、Correlation、日志 | M9-ACC-008、010 |
| 7 | Heartbeat、Hub、Dashboard、Insights | M9-ACC-006、008、009 |
| 8 | Wire 1.4、通知、CLI、版本隐藏 | M9-ACC-005、006、008、010 |
| 9 | 故障、安全、固定负载、全回归 | M9-ACC-001..010 |
| 10 | 双平台真机、台账、归档 | M9-ACC-001..010 |

十个冻结验收编号都有主实现 Outcome、离线关闭 Outcome 和双平台关闭 Outcome；没有为
厂商 Channel、多模态 Provider、UI、远程管理或 M11 发布工作安排占位实现。

## 停止条件与恢复边界

- State v9 Backup、Migration、Contributor 顺序或完整性校验失败：Runtime Start 失败，
  不打开监听、不派发、不发送；
- Channel Trust、Secret、配置摘要或 Ready 状态不确定：该 Channel 保持不可用，不用
  默认凭据或旧摘要继续；
- HMAC、Timestamp、Schema、媒体路径/类型/摘要任一不确定：请求在持久化前失败；
- Thread Create、Queue Commit、Outbox Send 等副作用已发生但结果不可判定：依靠持久
  Idempotency/Delivery ID 恢复；不能证明时进入稳定 Failed/Dead Letter，不盲目重放；
- 同 Conversation 出现 Sequence 越序、重复提交或两个活跃 Sender：停止该分区并
  报告 Degraded，不用内存锁掩盖数据库错误；
- Secret Canary 命中 HTTP、State、Journal、Trace、日志、Wire、stdout/stderr、证据
  或测试产物：阻塞当前 Outcome；
- Registry 损坏只隔离坏项；无法原子写时保留旧文件并阻塞注册更新，不清空注册表；
- Insight 触发 Agent/Provider/Tool、文件写入或配置修改：阻塞 Outcome 7；
- Wire 1.0–1.3、M7 Session/CoWork、M8 Automation、M9 Provider 回归：阻塞 M10 关闭；
- 任一目标平台缺发布目录真机证据：对应平台保持 Pending，M10 不标 Done；
- 真实用户目录、OS Secret、外部系统、非 loopback 网络或远端仓库未获明确授权：只用
  临时 Profile、BCL Loopback、Fake 边界，不执行外部动作。

## 完成定义

M10 只有在以下条件同时满足后才能标记 Done：

- 十个 Outcome 各自按 Red → Minimal → Focused → Full → Independent Commit 完成；
- `M9-ACC-001` 至 `M9-ACC-010` 全部 Passed；
- State v9 迁移/恢复、Inbound/Outbox 崩溃窗口、Channel 隔离和媒体安全通过；
- Wire 1.0–1.3 回归与 Wire 1.4 黑盒全部通过；
- `win-x64`、`osx-arm64` 发布目录真机证据独立完成，交叉发布未冒充真机；
- Secret、正文、路径、句柄、端口、Timer、Lease、后台任务和子进程检查通过；
- Design、Plan、Acceptance/Capability/Platform Ledger、Archive、Milestone
  CHECKLIST/INDEX 已同步。

缺少任一目标平台真机证据时，可以提交已验证的 Outcome，但不得创建 M10 完成交付
归档、不得把对应平台或整个 M10 标为 Passed/Done。
