# 第 2 课：Runtime 架构角色与生命周期

## 这一课解决什么问题

不只看懂 OpenCoWork 做了什么，还要学会识别：为什么这样分层、为什么抽象放在这里、为什么某个对象叫 Runtime，以及这些设计如何迁移到自己的 .NET 项目。

## 先说 Runtime 的结论

`Runtime` 不是 GoF 设计模式，也不是 .NET 关键字。它是一种架构角色：

```text
Runtime
= 已解析的配置
+ 当前有效的实现与绑定
+ 运行中的状态和资源
+ 明确的生命周期
+ 故障、恢复与失效边界
```

一句人话：**定义描述“系统可以有什么”，Runtime 表示“此时此地真正装起来并正在工作的那一套东西”。**

例如插件清单只是声明；发现插件、校验信任、建立工具绑定并发布当前能力目录，才进入 Capability/Plugin Runtime 的职责。

## Runtime 与其他常见角色的区别

| 名称 | 主要职责 | 通常是否持有活资源 |
| --- | --- | --- |
| `Service` | 提供一个业务能力或用例操作 | 不一定 |
| `Store` | 保存、读取和查询数据 | 可能持有连接，但重点是持久化语义 |
| `Registry` | 注册、索引、解析某类定义或实现 | 通常不负责完整生命周期 |
| `Factory` | 根据输入创建对象或执行器 | 通常不长期拥有创建结果 |
| `Snapshot` | 冻结某个时点的一致视图 | 不持有活行为，应尽量不可变 |
| `Binding` | 把稳定定义连接到当前可执行实现 | 可能失效，但通常不是总协调者 |
| `Runtime` | 组合以上角色，在一个作用域内维持可执行状态 | 经常持有进程、连接、计时器、缓存、Channel 或生命周期状态 |

不要按名字机械判断。一个类如果只是做 CRUD，却叫 `XxxRuntime`，它仍然只是穿了 Runtime 马甲的 Service。

## OpenCoWork 为什么有很多 Runtime

因为这些对象的**作用域、资源所有权、故障域和权限域不同**。把它们塞进一个全局大对象，停止顺序、故障隔离和恢复会立刻变成玄学。

| Runtime | 作用域与真实职责 |
| --- | --- |
| `WorkspaceRuntime` | 工作区级组合根；统一激活模块，发布完全启动状态，并协调逆序停止 |
| `SessionRuntime` | Session 子系统生命周期；初始化 State/Terminal，恢复 Thread 执行，开放新工作，停止后 checkpoint |
| `StateRuntime` | 工作区 SQLite 运行边界；负责迁移、连接配置和统一写入协调 |
| `AgentRuntimeExecutor` | 单次 Turn 的模型执行引擎；把冻结配置、历史、Provider 和工具循环变成 Session Intent |
| `WorkspaceCapabilityRuntime` | 工作区能力的动态组合；发现、校验、重建并发布当前 Capability Catalog |
| `PluginRuntime` | 插件包发现、信任和贡献加载；把声明转成当前可用能力 |
| `ToolRuntime` | 当前 Tool Definition、Binding、Registration 和 Snapshot 的组合与动态失效 |
| `BackgroundTerminalRuntime` | 后台终端进程的资源所有者；维护活动 Session、输出和停止清理 |
| `WorkspaceMemoryRuntime` | 工作区记忆版本与持久化操作的运行边界 |
| `AutomationSourceRuntime` | Automation 文件源的监听、扫描、唤醒和发布循环 |
| `AutomationsModuleRuntime` | Automation 模块内 Service、Source、Dispatcher、Reconciler 的生命周期组合 |
| `CoWorkModuleRuntime` | CoWork 模块服务、Direct SubAgent 和 Mission Reconciler 的生命周期组合 |
| `GatewayChannelRuntime` | 单个外部 Channel 的尝试、断连和状态更新边界 |
| `OperationsRuntime` | Operations 查询、Tracing、Heartbeat、Workspace Registry 和健康信息的运行组合 |

这些 Runtime 不是平级的一排“万能服务”。它们存在嵌套关系：

```text
进程 Host
└─ WorkspaceRuntime
   ├─ StateRuntime
   ├─ SessionRuntime
   │  ├─ SessionService
   │  ├─ AgentRuntimeExecutor
   │  └─ BackgroundTerminalRuntime
   ├─ WorkspaceCapabilityRuntime
   │  ├─ PluginRuntime
   │  └─ ToolRuntime
   ├─ AutomationsModuleRuntime
   ├─ CoWorkModuleRuntime
   └─ Operations / Gateway runtimes
```

这里表达的是所有权和生命周期，不是程序集依赖图。

## Runtime 背后的五条设计哲学

### 1. 声明与执行分离

稳定定义不应直接携带临时执行器。Tool 体系把它拆成：

```text
Definition → Binding → Registration → Snapshot → Invocation
```

- Definition 回答“它是什么”；
- Binding 回答“现在由谁执行、是否可用”；
- Registration 回答“这两者怎样组合并向谁曝光”；
- Snapshot 回答“本次执行看到哪一个冻结版本”；
- Runtime 负责维持这些活绑定和版本变化。

这套套路可以迁移到支付渠道、消息消费者、设备驱动、插件命令和第三方 Provider。

### 2. 冻结必须一致的，保持必须动态的

配置、插件和连接可以热变化，但一个已经开始的 Turn 不能执行到一半突然换工具定义。因此系统让 Runtime 保持动态，让 Invocation Snapshot 保持不可变。

这不是“多创建几个 record”，而是在解决并发下的时间一致性。

### 3. 生命周期必须跟资源所有权对齐

谁创建进程、计时器、Channel、连接或后台循环，谁就必须负责停止、等待和清理。Runtime 的 `StartAsync` / `StopAsync` 不是仪式代码，而是资源所有权契约。

### 4. 故障域必须可隔离、可降级

Capability、Gateway、Automation 或某个 Module 的失败，不应让 Session 权威状态变得不可恢复。运行时边界让系统可以表达 `Starting`、`Running`、`Degraded`、`Stopping` 等状态，并决定是拒绝新工作还是继续提供降级查询。

### 5. 外围编排不能复制核心状态机

Teams、Automations 和 Gateway 都可以拥有自己的 Runtime 和权威业务表，但最终对话执行仍进入 `ISessionService`。Runtime 可以组合核心，不能偷偷再造一套 Thread/Turn 状态机。

## 什么时候值得设计一个 Runtime

满足下面大部分条件时，`Runtime` 才可能是合适命名：

1. 它表示某个明确作用域内“当前有效”的执行环境；
2. 它组合配置、实现、状态与外部资源，而不只是一个算法；
3. 它有真实的 Start、Stop、Refresh、Recover 或失效语义；
4. 它拥有连接、进程、计时器、Channel、缓存或动态 Binding；
5. 它的失败可以被单独观察、隔离或降级；
6. 它的生命周期不同于创建它的 Host 或调用它的 Service。

如果只满足“类比较重要”，不要叫 Runtime。名字不是爵位。

## Runtime 最容易写坏的方式

- 把 DI 容器塞进去，到处 `GetService`，变成 Service Locator；
- 把配置、存储、业务规则、协议和后台任务全塞进一个 God Object；
- 有 `StartAsync` 没有对称清理，后台任务和进程泄漏；
- Runtime 之间循环持有，没人说得清谁先停止；
- 热更新直接修改执行中的对象，没有 Snapshot/Revision；
- 每个外围 Runtime 都复制一份核心状态机；
- 用 `Runtime` 掩盖模糊职责，实际上说不出它拥有哪种资源。

以后看到任意 `XxxRuntime`，先问七个问题：

1. 它的作用域是什么？
2. 谁创建它，谁停止它？
3. 它拥有哪些活状态和资源？
4. 它依赖哪些稳定契约？
5. 它向外暴露 Service、Snapshot 还是事件？
6. 启动失败、运行故障和停止失败分别怎样处理？
7. 重启后哪些状态恢复，哪些状态重新发现？

答不出来，说明还没真正看懂这个 Runtime。

## 源码核对锚点

这节课只核对 Runtime 角色，不展开程序集依赖、状态迁移或代码生成：

- `src/OpenCoWork.Core/Hosting/WorkspaceRuntime.cs`
- `src/OpenCoWork.Core/Sessions/SessionRuntime.cs`
- `src/OpenCoWork.Core/Tools/ToolRuntime.cs`
- `src/OpenCoWork.Core/Tools/BackgroundTerminalRuntime.cs`
- `src/OpenCoWork.Core/Agents/AgentRuntime.cs`
- `tests/OpenCoWork.Core.Tests/SessionRuntimeTests.cs`
- `tests/OpenCoWork.Core.Tests/ToolSnapshotTests.cs`
- `tests/OpenCoWork.Core.Tests/DynamicToolTests.cs`

## 本课作业

1. 在设备控制系统中，`Definition`、`Binding`、`Registration`、`Snapshot` 和 `Runtime` 分别应该表示什么？
2. 为什么 Tool Runtime 热更新后，已经开始的 Turn 仍应使用旧 Snapshot？
3. 一个只封装 `HttpClient.SendAsync` 的无状态类，为什么通常应叫 Client 或 Service，而不是 Runtime？
4. `WorkspaceRuntime` 和 `ToolRuntime` 形态差异很大，为什么它们仍都适合使用 Runtime 命名？
5. Runtime 刷新新配置失败时，为什么通常应该保留上一份可用 Snapshot，而不是立即清空当前状态？
6. 看到一个新类名 `XxxRuntime` 时，至少应追问哪四类问题，才能判断这个命名是否合理？

## 本课掌握记录

- 状态：已完成（2026-08-02）
- 已掌握：Runtime 是架构角色而非设计模式；能够区分 Runtime、Service、Store、Registry、Factory、Binding 与 Snapshot；理解声明和执行分离、动态 Runtime 与冻结 Snapshot、生命周期所有权及故障域
- 仍有疑问：未记录；源码解剖阶段遇到理解断点时回填
- 可迁移案例：设备控制系统中的 DeviceDefinition、DeviceBinding、DeviceSnapshot 与 DeviceRuntime
- 纠偏记录：重要或复杂的类不等于 Runtime；没有活资源、独立生命周期和失效恢复语义时，不应滥用 Runtime 命名

## 本课作业参考答案

### 1. Definition、Binding、Registration、Snapshot 与 Runtime

- `Definition`：稳定描述设备是什么、支持哪些命令、参数 Schema 和能力，不持有当前连接；
- `Binding`：把某个 Definition 连接到当前串口、网络连接或本地驱动，带有可用性、Generation 和超时；
- `Registration`：说明某个 Definition 使用哪个 Binding、向哪些调用者暴露，以及采用什么策略；
- `Snapshot`：冻结一次任务实际看到的 Definition、Binding Generation、权限和配置版本；
- `Runtime`：负责发现设备、建立和失效 Binding、发布 Registration、生成 Snapshot，并管理启动、刷新、重连和停止。

### 2. 为什么正在执行的 Turn 继续使用旧 Snapshot

一次 Turn 的工具 Schema、权限判断、审批内容和最终执行对象必须来自同一个一致版本。如果执行中途切到新 Definition 或 Binding，就会出现“按旧规则审批、按新规则执行”的时间竞争，既不可复现，也可能越权。因此 Runtime 保持动态，已经开始的 Invocation 使用冻结 Snapshot；新版本只影响之后开始的 Turn。

### 3. 为什么无状态 HTTP 封装不叫 Runtime

只转发 `HttpClient.SendAsync` 的类表达的是访问外部服务的能力，通常没有独立的启动、停止、刷新、恢复和故障域，也不拥有一套当前有效的运行状态。它更准确的角色是 Client；如果还包含业务用例编排，可以叫 Service。把它叫 Runtime 只会掩盖职责。

### 4. 为什么 WorkspaceRuntime 和 ToolRuntime 都是 Runtime

`WorkspaceRuntime` 主要拥有模块启动状态、主宿主和逆序停止过程；`ToolRuntime` 主要拥有动态 Definition、Binding、Registration、Revision 与 Snapshot。它们管理的资源不同，但都表示某个作用域内“当前真正可工作的执行环境”，都有活状态、生命周期、失效语义和独立故障边界，所以共享 Runtime 这一架构角色。

### 5. 为什么刷新失败要保留上一份可用 Snapshot

刷新过程产生的是候选状态，候选只有在完整校验和组合成功后才能原子发布。失败时保留旧版本，可以让正在执行和新进入的工作继续使用最后已知可用状态，同时把 Runtime 标记为 Degraded 并报告诊断；立即清空会把一次局部配置错误扩大成系统级不可用。

### 6. 怎样审问一个 XxxRuntime

至少要问：它的作用域是什么；谁创建、启动、停止和释放它；它拥有哪些连接、进程、Channel、计时器、缓存或动态 Binding；启动失败、运行故障、刷新失败和停止失败分别怎样处理。进一步还要确认它向外发布的是 Service、事件还是 Snapshot，以及重启后哪些状态恢复、哪些重新发现。
