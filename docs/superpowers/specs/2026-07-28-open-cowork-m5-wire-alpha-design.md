# OpenCoWork M5 Wire Alpha 详细设计

## 文档状态

- 状态：已完成并归档
- 日期：2026-07-28
- 所属里程碑：OpenCoWork Runtime 1.0 / M5
- 第一真实客户端：OpenCoWork Desktop
- 对应计划：
  [M5 Wire Alpha 实施计划](../plans/2026-07-28-open-cowork-m5-wire-alpha-implementation-plan.md)
- 对应归档：
  [M5 Wire Alpha 交付归档](../archives/2026-07/2026-07-28-open-cowork-m5-wire-alpha-archives.md)
- 官方 ACP 基线：
  [ACP v1 Overview](https://github.com/agentclientprotocol/agent-client-protocol/blob/main/docs/protocol/v1/overview.mdx)

## 1. 目标

M5 通过独立 `OpenCoWork.Protocol` 程序集，把唯一
`ISessionService -> SessionService -> ThreadJournal` 暴露为：

1. 面向 OpenCoWork Desktop 的原生 OpenCoWork Wire；
2. 面向外部编辑器的 ACP 稳定 v1 Bridge。

OpenCoWork Wire 是主协议。ACP 是同一 Session Core 上的兼容适配，不能反向决定
Wire 的命名、事件和状态模型。

## 2. 已确认边界

### 2.1 Desktop 与进程

- Desktop 启动并管理本地 `opencowork app-server` 子进程；
- stdio UTF-8 JSONL 是 Desktop 默认通道；
- Desktop 退出时子进程结束，已提交事实仍由 Journal 保证恢复；
- 一个 app-server 进程只绑定一个 Workspace；
- 多 Workspace 由 Desktop 管理多个子进程；
- M5 不交付正式 Desktop Client SDK，只交付黑盒 Protocol TestClient。

### 2.2 Transport

- OpenCoWork Wire：stdio 为主，loopback WebSocket 为次；
- stdio 信任启动它的 Desktop 父进程，不增加 token；
- WebSocket 只监听 loopback，必须使用临时 bearer token；
- 启动方通过环境变量把 token 交给 app-server，客户端通过
  `Authorization` header 发送；
- 不接受 query token 或 initialize token；
- ACP 只使用 stdio；
- 远程绑定、TLS、浏览器 Origin、端口发现和长期驻留服务属于 M9。

### 2.3 不包含

- DotCraft AppServer 的 214 方法复制；
- Desktop UI 或生产 Client SDK；
- JSON-RPC batch；
- ACP v2 草案和 v1 可选扩展；
- Skills、MCP、Teams、Automations 与 Gateway 方法；
- Protocol 专属数据库、事件存储或恢复状态机；
- 第三方 JSON-RPC、WebSocket 或 ACP 框架。

## 3. 架构与所有权

```text
OpenCoWork Desktop
        │ stdio JSONL
        ▼
opencowork app-server ── loopback WebSocket（次要）
        │
        ▼
OpenCoWork.Protocol
  ├─ JSON-RPC connection
  ├─ OpenCoWork Wire adapter
  ├─ semantic event projection
  └─ ACP v1 bridge
        │
        ▼
ISessionService
        │
        ▼
SessionService -> ThreadJournal -> SQLite
```

依赖保持：

```text
OpenCoWork.Protocol -> OpenCoWork.Abstractions
OpenCoWork.App      -> OpenCoWork.Protocol + OpenCoWork.Core
```

Protocol 只能保存连接、请求等待、订阅句柄和增量投影偏移。这些数据可丢弃、不可
落库，也不是 Thread/Turn 状态。

## 4. OpenCoWork Wire

### 4.1 JSON-RPC 与握手

- 协议为 JSON-RPC 2.0；
- request ID 接受字符串或整数并原样回写；
- 每个连接必须先调用 `initialize`；
- initialize 校验客户端请求的 Workspace 与进程绑定 Workspace 相同；
- 成功响应后服务端发送 `initialized` notification；
- 重复 initialize 和初始化前业务请求返回稳定错误；
- M5 拒绝 batch。

`initialize` 返回：

- `wireVersion: "1.0"`；
- 服务端名称、版本和真实能力；
- 绑定 Workspace；
- transport 与限制；
- correlation ID。

### 4.2 方法面

M5 保留 M0 的 Thread/Turn/Item 方法，并根据 Desktop 真实需求做四项最小修订：

1. 增加 `thread/history/read`，历史分页与实时订阅分离；
2. 增加 `thread/model/set`；
3. 增加 `thread/mode/set`；
4. 明确 `thread/delete/prepare`，补齐 M0 已冻结的删除预检。

完整方法目录：

```text
thread/create          thread/get             thread/list
thread/history/read    thread/rename          thread/model/set
thread/mode/set        thread/pause           thread/resume
thread/archive         thread/unarchive       thread/delete/prepare
thread/delete          thread/fork            thread/rollback
thread/subscribe       thread/unsubscribe

turn/start             turn/enqueue           turn/queue/remove
turn/queue/reorder     turn/steer             turn/cancel

item/approval/resolve  item/input/resolve
```

`thread/create` 接受初始 provider、model 和 mode；所有入口都在 Session Core
统一校验 provider/model，Protocol 不复制 CLI 预检逻辑。

`thread/get` 只返回当前 Thread 状态与元数据。
`thread/history/read` 使用 opaque cursor 分页返回稳定 Item 历史。
`thread/subscribe` 只负责实时同步和断线补齐。

### 4.3 Turn 提交

`turn/start` 提交成功后立即返回：

```text
threadId, turnId, acceptedSequence
```

它不等待模型或工具执行完成。`item/started`、`item/delta` 和 Turn 终态通过订阅
发送。

- `turn/start`：Thread 忙时返回 `thread.busy`，不隐式排队；
- `turn/enqueue`：明确创建队列项，由 Session Core 调度；
- `$/cancelRequest`：只取消当前 RPC 等待；
- `turn/cancel`：调用 Session Core，产生持久业务取消。

为避免 Protocol 先查 Busy 再提交的竞态，现有 `EnqueueInputRequest` 增加
`StartOnly | QueueIfBusy` admission，并由 Session Core 原子执行。默认值保持
现有 CLI 行为。

### 4.4 修改与幂等

- mutation 请求携带独立于 JSON-RPC ID 的 `idempotencyKey`；
- 依赖当前 Thread 投影的 mutation 携带 `expectedSequence`；
- 并发判断与事实提交由 Session Core 在同一权威边界完成；
- Approval/UserInput 的首个有效 resolution 胜出；
- 重复幂等请求返回已有结果，不重复事实或副作用。

### 4.5 状态同步

Desktop 使用“命令 + 原子快照 + 语义事件流”：

1. `thread/subscribe` 原子返回 snapshot 与 `currentSequence`；
2. 随后只发送更大 sequence 的事件；
3. 重连携带 `afterSequence`，先补齐再接 live；
4. Desktop 仅维护可丢弃的内存投影。

事件 envelope 保持：

```text
eventId, threadId, turnId, itemId, sequence, timestamp, payload
```

`eventId` 使用 Journal entry ID，`sequence` 使用 ThreadJournal sequence。
交付为单 Thread 有序、at-least-once，客户端用 `eventId + sequence` 去重。

事件目录：

```text
thread/created         thread/updated          thread/deleted
thread/statusChanged   thread/queueUpdated
turn/started           turn/completed          turn/failed
turn/cancelled
item/started           item/delta              item/completed
item/approval/requested   item/approval/resolved
item/input/requested      item/input/resolved
system/event
```

Protocol 不暴露原始 Journal fact。领域事实映射为专用事件；其余已提交 sequence
映射为脱敏 `system/event`，不能因丢弃内部 fact 造成 cursor 空洞。

Core 的流式内容是累计文本时，Protocol 只在订阅内保存已发送长度并生成真正
`item/delta`；偏移不进入 Journal 或全局缓存。

### 4.6 错误

标准 JSON-RPC 错误使用 `-32700`、`-32600`、`-32601`、`-32602`、
`-32603`。业务错误使用稳定 `-320xx` 范围，`error.data` 至少包含：

```text
errorCode, retryable, correlationId
```

并发冲突可以增加 `currentSequence`。响应、stdout、日志和契约快照不能包含：

- Secret 或 bearer token；
- provider 原始 payload；
- 堆栈；
- 内部绝对路径；
- SQLite 或内部异常文本。

## 5. Catalog 与 DTO

生产 `OpenCoWorkWireMethodAttribute` 和生成的 `WireMethodDescriptor` 必须包含
M0 九字段：

```text
method, direction, owner, since, request, response,
authority, mutates, idempotency
```

Source Generator 在编译期拒绝重复方法和缺失元数据。App 是唯一全局目录聚合点；
Protocol 不运行时扫描程序集，也不维护第二份手写 catalog。

Protocol 定义稳定 Wire DTO，并显式映射 Abstractions 契约。不得直接序列化 Core
类型、异常或 provider payload。

## 6. Transport 细节

### 6.1 stdio

- stdin/stdout 使用严格 UTF-8 JSONL；
- 一行一个完整 JSON 对象；
- stdout 只写协议；
- 日志只写 stderr；
- EOF 关闭连接并释放订阅；
- 单消息最大 1 MiB；
- Session 文本继续使用现有 256 KiB 上限；
- 出站队列固定 256 条，溢出后关闭慢连接。

### 6.2 WebSocket

- App 使用 .NET 自带 ASP.NET Core/Kestrel shared framework；
- 一个完整 UTF-8 text message 对应一个 JSON 对象；
- 只监听 loopback；
- handshake 必须有 bearer header；
- binary、无效 UTF-8、超限消息、query token 和非 loopback 绑定均拒绝。

固定上限不增加配置项；出现真实容量需求后再开放配置。

## 7. ACP 稳定 v1 Bridge

### 7.1 版本与方法

M5 固定 `protocolVersion: 1`，只声明：

```text
initialize
session/new
session/load
session/prompt
session/cancel
session/set_mode
```

ACP 官方 v2 migration 已明确移除 `session/load` 和 `session/set_mode`；M5 不追随
草案，也不同时实现两套版本：
[ACP v2 Migration](https://github.com/agentclientprotocol/agent-client-protocol/blob/main/docs/protocol/v2/migration.mdx)。

不声明 resume、list、config options、image/audio、MCP server 或 draft
elicitation。

### 7.2 映射

| ACP v1 | Session Core |
| --- | --- |
| `session/new` | `CreateThreadAsync` |
| `session/load` | 读取 Thread/历史并建立一次 catch-up + live 订阅 |
| `session/prompt` | `EnqueueInputAsync(StartOnly)`，等待终态后返回 stop reason |
| `session/cancel` | 取消该 session 当前 active Turn |
| `session/set_mode` | `SetAgentModeAsync(agent\|plan)` |

- ACP `sessionId` 直接使用 opaque Thread ID，不建立映射表；
- `cwd` 必须等于进程绑定 Workspace；
- M5 拒绝非空 `mcpServers`；
- prompt 只接受 text block；
- `session/load` 按 Journal sequence 去重，历史 replay 与 live update 不重复；
- ACP 连接断开不删除 Thread，不创建 ACP 专属持久状态。

终态只映射有 Core 事实支撑的 stop reason：

| Core 终态 | ACP stop reason |
| --- | --- |
| 正常完成 | `end_turn` |
| 响应被 token 上限截断 | `max_tokens` |
| 内容过滤 | `refusal` |
| 已取消 | `cancelled` |
| 其他失败 | 脱敏 JSON-RPC error |

### 7.3 Approval 与 UserInput

- Approval 映射 ACP v1 permission request，再调用
  `ResolveInteractionAsync`；
- 通用 UserInput 没有稳定 v1 等价能力；
- 遇到通用 UserInput 时返回 `capability_not_supported` 并取消当前 Turn；
- 不伪装成 permission，不启用 draft elicitation，也不无限等待。

原生 Wire 完整支持 Approval 与 UserInput，不受 ACP 限制。

## 8. App 生命周期

App 新增 `app-server` 与 `acp` primary command，现有 `cli` 仍是默认入口。
组合根由命令明确选择 primary module。

Module 初始化不能自动启动 listener；命令处理器启动共享 Host 后，才运行所选协议
host。进程关闭时：

1. 停止接收新请求；
2. 取消连接级 RPC waits；
3. 释放订阅与 transport；
4. 停止共享 Host。

关闭连接不主动写业务 cancel；进程中断后的恢复由现有 Session Core 规则负责。

## 9. 验收

| 验收项 | M5 证据 |
| --- | --- |
| M5-ACC-001 | initialize 前置、版本、真实能力与绑定 Workspace snapshot |
| M5-ACC-002 | stdio/WS UTF-8、stdout/stderr、bearer、上限与慢连接双平台测试 |
| M5-ACC-003 | 完整 method/event generated catalog 与重复诊断 |
| M5-ACC-004 | Desktop 主路径：create/history/subscribe/start/terminal/interaction/queue/steer |
| M5-ACC-005 | 原子 snapshot、afterSequence、断连窗口、去重与 ResetRequired |
| M5-ACC-006 | request ID、idempotency、expectedSequence 与两类 cancel 独立 |
| M5-ACC-007 | ACP v1 六方法、history/live 不重复、UserInput 明确失败 |
| M5-ACC-008 | 错误、transcript 与日志敏感信息扫描 |
| M5-ACC-009 | Protocol/ACP 只调用 ISessionService，无 Store 写入与第二状态机 |

`OpenCoWork.Protocol.TestClient` 作为进程级黑盒工具覆盖 stdio、WebSocket、ACP、
重连、慢客户端、取消和脱敏 transcript。它不是生产 Desktop SDK。

M5-ACC-002 需要 `osx-arm64` 与 `win-x64` 真机证据。两端发布目录 TestClient
均已真实运行通过，状态为 `Passed`；交叉 publish 仍不能替代后续 M10 最终发布
候选复验。

## 10. 冻结结论

M5 的最小正确交付是：

1. 一个基于 .NET 标准库的 JSON-RPC connection；
2. 一个为 OpenCoWork Desktop 服务的原生 Wire adapter；
3. 一个消费同一 Session Core 的 ACP 稳定 v1 adapter；
4. 一个进程级 TestClient；
5. 两个平台各自的真实 transport 证据。

除此之外不为未来客户端预造 SDK、daemon、远程认证或协议扩展。
