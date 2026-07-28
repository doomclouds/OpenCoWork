# OpenCoWork M5 Wire Alpha 实施计划

**Goal:** 交付以 OpenCoWork Desktop 为第一客户端的原生 Wire 和共享 Session
Core 的 ACP 稳定 v1 Bridge。

**Why planning is required:** M5 修改公共协议、进程边界、认证与跨平台 transport，
并跨越 Abstractions、Generator、Core、Protocol、App 和测试项目。

**Acceptance:** M5-ACC-001 至 M5-ACC-009 有可复现证据；Protocol 不直接写
Store、不建立第二状态机；stdio、loopback WebSocket 与 ACP v1 通过黑盒测试；
win-x64 与 osx-arm64 真机证据进入平台台账。

对应规格：
[M5 Wire Alpha 详细设计](../specs/2026-07-28-open-cowork-m5-wire-alpha-design.md)

### Outcome 1：冻结 Wire 契约与生成目录

- Work:
  - 在 `OpenCoWork.Abstractions` 定义 Wire method attribute、九字段 descriptor、
    initialize/error/event DTO 和 M5 请求响应 DTO；
  - 扩展 `OpenCoWorkGenerator`，生成唯一 catalog 并诊断重复或不完整方法；
  - catalog 覆盖 M0 方法、`thread/delete/prepare`、history、model 和 mode 修订；
  - App 继续作为唯一 catalog 聚合点。
- Verify:
  `dotnet test tests/OpenCoWork.Generators.Tests/OpenCoWork.Generators.Tests.csproj -c Release`
  与
  `dotnet test tests/OpenCoWork.ArchitectureTests/OpenCoWork.ArchitectureTests.csproj -c Release`。

### Outcome 2：补齐 Session Core 的统一入口约束

- Work:
  - 给现有 `EnqueueInputRequest` 增加 `StartOnly | QueueIfBusy` admission；
  - 默认保持 CLI 现有排队行为；
  - 让 create thread 的 provider/model 校验在 Session Core 生产组合中统一执行；
  - 保持所有幂等、sequence、delete prepare 和 interaction 决议在现有权威边界；
  - 不新增平行 Session service 或只有一个实现的公共 interface。
- Verify:
  `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~SessionQueueTests|FullyQualifiedName~SessionServiceTests'`。

### Outcome 3：实现 OpenCoWork Wire

- Work:
  - 在 `OpenCoWork.Protocol` 用 `System.Text.Json` 实现单消息 JSON-RPC connection；
  - 实现 initialize、方法 dispatch、标准/业务错误与 `$/cancelRequest`；
  - 所有 handler 只调用 `ISessionService`；
  - 显式映射稳定 Wire DTO，不序列化 Core 类型或异常；
  - `turn/start` 提交即返回，执行结果只走事件；
  - `thread/history/read` 与 `thread/subscribe` 分离；
  - 每个 Journal sequence 投影一个专用事件或脱敏 `system/event`。
- Verify:
  `dotnet test tests/OpenCoWork.Protocol.Tests/OpenCoWork.Protocol.Tests.csproj -c Release --filter 'FullyQualifiedName~JsonRpc|FullyQualifiedName~OpenCoWorkWire|FullyQualifiedName~Subscription'`。

### Outcome 4：接入 Desktop 子进程 transport 与 App 生命周期

- Work:
  - 新增 `app-server` primary command，一进程绑定一个 Workspace；
  - stdio 使用严格 UTF-8 JSONL，stdout 仅协议，stderr 仅日志；
  - 使用 ASP.NET Core shared framework 提供 loopback WebSocket；
  - WebSocket 只接受环境注入 token 对应的 bearer header；
  - 使用固定消息和有界队列上限；
  - 命令显式选择 primary module，现有 `cli` 默认行为不变；
  - 不实现正式 Desktop SDK、daemon 或远程 listener。
- Verify:
  `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --filter FullyQualifiedName~ProtocolProcessIntegrationTests`。

### Outcome 5：实现 ACP 稳定 v1 Bridge

- Work:
  - 新增 `acp` stdio command；
  - 固定 `protocolVersion: 1`；
  - 实现 initialize/new/load/prompt/cancel/set_mode；
  - session ID 直接使用 opaque Thread ID；
  - load 使用同一个 Journal catch-up/live 流并按 sequence 去重；
  - Approval 映射 permission request；
  - 通用 UserInput 返回 capability error 并取消当前 Turn；
  - 不声明 v2、optional 或 draft 能力，不新增 ACP 状态表。
- Verify:
  `dotnet test tests/OpenCoWork.Protocol.Tests/OpenCoWork.Protocol.Tests.csproj -c Release --filter FullyQualifiedName~Acp`
  与进程级 ACP harness。

### Outcome 6：黑盒验收与双平台证据

**执行状态（2026-07-28，基线
`882efd9c22e2323060d23938501191dcc409b981`）：**

- Protocol TestClient 已在发布目录覆盖 Wire stdio、ACP v1、loopback WebSocket、
  重连去重、慢读端、业务取消、Bearer Header 拒绝矩阵和 Secret Canary；
- Release 全量回归为 280 passed，Release build 为 0 warning / 0 error；
- `osx-arm64` App/TestClient 均确认是 Mach-O arm64，并在 Apple Silicon
  macOS 26.5.2 真机通过全部 TestClient 场景；
- `win-x64` App/TestClient 均交叉发布为 PE32+ x86-64，但缺 Windows 真机运行，
  因此平台状态保持 `Pending`，`M5-ACC-002` 保持 `Planned`；
- 其余八项 M5 Acceptance 已回填为 `Passed`；能力台账契约无需修订，M5
  保持 `In Progress`，不创建完成归档。

- Work:
  - 完成 `OpenCoWork.Protocol.TestClient` 的 stdio、WebSocket、ACP、重连、慢连接、
    cancel 和敏感信息扫描场景；
  - 逐项关闭 M5-ACC-001 至 M5-ACC-009；
  - 运行全量 build/test；
  - 分别在 osx-arm64 与 win-x64 真机运行发布产物和 TestClient；
  - 更新 acceptance catalog、capability ledger、platform ledger、milestone 和
    M5 delivery archive。
- Risks/open questions:
  - win-x64 真机证据不可由 macOS cross-publish 替代；未取得时 M5 保持
    Pending/Deferred；
  - 真实 Provider 只验证用户显式激活的 provider/model/platform，其他项目不进入
    M5 完成声明。
- Verify:
  `dotnet test OpenCoWork.slnx -c Release --no-restore` 与
  `dotnet build OpenCoWork.slnx -c Release --no-restore`；
  对每个 RID 单独 restore/publish 后运行 Protocol TestClient，并把命令、平台、
  产物摘要和结果写入双平台台账。
