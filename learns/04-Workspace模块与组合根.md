# 第 4 课：Workspace、模块与组合根

## 这一课解决什么问题

DI 注册完成不等于系统已经可以工作。模块有依赖顺序，资源可能启动失败，停止也可能超时；多个入口还需要决定谁是当前进程的 Primary Host。

这一课学习 OpenCoWork 怎样把分散的模块声明变成一个可启动、可回滚、可降级、可停止的 Workspace Runtime，以及为什么这些具体选择必须集中在 Composition Root。

## 先说结论

OpenCoWork 的 Workspace 启动不是“一堆 `AddSingleton` 加几个 `IHostedService`”，而是一条受控管线：

```text
Module Attribute
→ 编译期 RuntimeCatalog
→ ModuleRegistry 校验与拓扑排序
→ App 选择 Primary Host
→ Module.ConfigureServices
→ 唯一 ModuleLifecycleCoordinator
→ 按拓扑 StartAsync
→ 原子发布 WorkspaceRuntimeStartedState
```

停止则沿相反方向：

```text
先撤销对外可用状态
→ 按启动顺序的严格逆序 StopAsync
→ 一个模块失败也继续清理其他模块
→ 聚合错误并保留可重试清理状态
```

这里守住的核心不变量是：外部只能看见“完整启动后的 Workspace”，不能拿到半装好的服务世界。

## 模块声明表达什么

模块通过 `OpenCoWorkModuleAttribute` 声明静态元数据：

```csharp
[OpenCoWorkModule(
    "automations",
    Dependencies = ["session"])]
public sealed class AutomationsModule : IOpenCoWorkModule
```

| 字段 | 含义 |
| --- | --- |
| `Id` | 稳定、唯一的 lower-kebab-case 模块身份 |
| `Dependencies` | 本模块启动前必须已经成功启动的模块 |
| `Priority` | 未显式指定 Primary Host 时的选择优先级 |
| `CanBePrimaryHost` | 模块是否有资格成为当前进程入口宿主 |

`Dependencies` 表达的是运行时启动拓扑，不是 `ProjectReference`，也不代表对象所有权。Automation 依赖 Session 就绪，但它仍通过 `ISessionService` 协作，不需要引用 Core 实现。

## IOpenCoWorkModule 的三段职责

```csharp
public interface IOpenCoWorkModule
{
    void ConfigureServices(IServiceCollection services);

    ValueTask StartAsync(
        IServiceProvider services,
        CancellationToken cancellationToken);

    ValueTask StopAsync(
        IServiceProvider services,
        CancellationToken cancellationToken);
}
```

三段不能混用：

- `ConfigureServices` 描述对象图，不能假设 ServiceProvider 已经构建，也不应启动后台资源；
- `StartAsync` 在依赖模块已经就绪后激活连接、恢复任务和后台循环；
- `StopAsync` 停止本模块拥有的资源，允许协调器按依赖逆序清理。

这种拆分把“对象如何构造”和“资源何时变成活状态”分开。只注册 Service 不代表它已经恢复完成，也不代表可以接收工作。

## 编译期 Catalog 为什么在这里有价值

模块声明分散在 App、Automations、Teams 等程序集。如果手写总注册表，很容易漏模块、拼错 ID 或忘记依赖。Source Generator 在 App 拥有完整编译依赖闭包时收集 Attribute，生成 `RuntimeCatalog.Modules`。

编译期会提前报告非法 ID、重复 ID、缺失依赖和依赖环；运行时 `ModuleRegistry` 仍会再次校验。两层检查不是纯重复：

- Generator 让正常开发尽早在编译期失败；
- Registry 防御非生成 Catalog、测试构造和未来其他宿主传入的非法数据。

这是“编译期便利 + 运行时边界验证”，不能因为有 Generator 就删除运行时防线。

## ModuleRegistry 怎样建立确定性启动顺序

`ModuleRegistry` 先检查：

1. Catalog 不能为空；
2. ID 和依赖 ID 必须是 lower-kebab-case；
3. ID 不能重复；
4. 每个依赖必须存在；
5. 依赖图不能有环。

然后使用拓扑排序建立 `StartupOrder`。当多个模块同时满足启动条件时，使用按 ID 排序的 `SortedSet` 选择下一个模块，因此结果不依赖 Attribute 扫描顺序、程序集枚举顺序或 Dictionary 的偶然状态。

确定性很重要：同一份 Catalog 在不同机器和不同构建中必须得到同样的注册、启动和停止顺序，否则故障只能靠运气复现。

## Primary Host 解决什么问题

一个 Workspace 可以包含 Session、Teams、Automations、Gateway 等多个模块，但一个进程需要明确“当前由哪个入口形态负责宿主行为”。这就是 Primary Host。

选择规则是：

1. 调用方显式指定模块 ID时，该模块必须存在且 `CanBePrimaryHost=true`；
2. 没有显式指定时，从候选中选择最高 Priority；
3. 最高 Priority 并列时拒绝猜测，直接报告稳定错误；
4. 没有候选同样启动失败。

Primary Host 不是“最先启动的模块”，也不是“拥有所有其他模块的上帝对象”。它只表示当前进程采用哪个宿主入口和相应行为，依赖排序仍由 Module Graph 决定。

## Composition Root 实际做了什么

`OpenCoWorkCompositionRoot.Build` 是具体知识的汇合点：

1. 用生成的 Module Catalog 建立 `ModuleRegistry`；
2. 选择 `cli` 或调用方要求的 Primary Host；
3. 创建 .NET Generic Host；
4. 注册 Workspace、RuntimeConfig、SessionConfig、GatewayConfig；
5. 调用 `AddOpenCoWorkRuntime` 激活模块并收集服务注册；
6. 构建最终 Host。

Composition Root 知道具体模块、具体配置和具体实现是正常的。它的边界是只负责选择与组装，不把 Session 状态转换、Automation 调度等业务规则搬进来。

## 为什么模块不能各自注册 IHostedService

`AddOpenCoWorkRuntime` 会在每次 `module.ConfigureServices` 前后统计 `IHostedService` 数量；模块如果私自新增 Hosted Service，立即以 `OCWMOD009` 拒绝。

原因不是讨厌 .NET Hosting，而是必须保留一个生命周期权威。如果每个模块都注册自己的 Hosted Service：

- Generic Host 的启动顺序不再等于 Module 依赖拓扑；
- 中途失败时没人知道哪些模块已经完成；
- 无法统一逆序回滚；
- 停止超时和异常可能跳过后续清理；
- Workspace 可能在部分 Hosted Service 未就绪时被误判为 Running。

因此系统只注册一个 `ModuleLifecycleCoordinator` 作为 `IHostedService`，模块内部资源都服从它的编排。

## 启动状态机

`WorkspaceRuntime` 的状态是：

```text
Stopped
→ Starting
→ Running / Degraded
→ Stopping
→ Stopped

任何未完成清理的失败
→ Faulted
```

启动过程由 `_lifecycleLock` 串行化，避免并发启动两次：

1. 只允许从 `Stopped` 开始；
2. 清空旧 Degraded 原因并进入 `Starting`；
3. Coordinator 按拓扑逐个调用模块 `StartAsync`；
4. 每个模块成功返回后才加入 `_started`；
5. 所有模块启动完成后创建 `WorkspaceRuntimeStartedState`；
6. 在同一状态临界区发布 StartedState，并进入 `Running` 或 `Degraded`。

在 `Starting` 期间访问 `StartedState` 会收到稳定错误。DI 容器里可能已经有对象，但 Workspace 对外仍不可用；只有完整依赖闭包就绪后才发布运行状态。

## 启动失败怎样回滚

假设启动顺序是 `A → B → C`，C 启动失败：

```text
C.StartAsync 失败
→ 只回滚已成功登记的 B、A
→ 顺序为 B.StopAsync、A.StopAsync
```

C 没有被加入 `_started`，所以 C 必须在自己的 `StartAsync` 内清理“启动到一半”的资源。`SessionModule` 就采用这种分层责任：它先启动 Capability Runtime，再启动 Session Runtime；如果后者失败，模块自己停止前者，然后把原始异常重新抛给上层协调器。

如果上层回滚全部成功：

- 普通启动异常使 Workspace 进入 `Faulted`；
- 启动取消且没有遗留清理时回到 `Stopped`。

如果回滚自身也失败，系统聚合原始启动错误和清理错误，并保留 `_started` 中尚未清理的模块。此时必须停在 `Faulted`，禁止再次启动，直到后续 Stop/Dispose 重试完成清理。

## Degraded 与 Faulted 不是一回事

`Degraded` 表示完整启动已完成，但某些模块报告能力不完整，例如恢复或外部连接尚未完全健康。此时 `StartedState` 仍然可用，系统可以提供受限能力，并通过健康查询暴露具体模块原因。

`Faulted` 表示启动或停止没有形成可信的完整边界，可能仍有待清理资源。此时 StartedState 被撤销，不能继续把 Workspace 当成可服务状态。

```text
Degraded = 已完整装好，但部分能力降级
Faulted  = 生命周期边界没有完整闭合
```

## 停止为什么必须逆序且继续清理

如果 B 依赖 A，停止时必须先停 B，再停 A；否则 B 可能在退出过程中继续访问已经被 A 释放的资源。因此 Coordinator 对 `_started` 使用严格逆序。

停止还有几条重要规则：

1. 先清空 StartedState 并进入 `Stopping`，阻止新工作；
2. 使用统一 `runtime.stopTimeout` 控制清理预算；
3. 一个模块同步抛错、异步失败或超时，都记录错误但继续处理剩余模块；
4. 所有错误最终以 `AggregateException` 报告；
5. 未成功停止的模块继续留在 `_started`，允许从 `Faulted` 再次调用 Stop；
6. 超时但仍在运行的 Stop Task 会被保留，重试时等待原任务，不重复调用 `StopAsync`；
7. 调用方的 CancellationToken 只控制等待生命周期锁，一旦停止开始，不允许外部取消跳过清理。

这套设计选择“尽最大努力释放所有资源并保留重试线索”，而不是第一个异常出现就撒手不管。

## 常见错误

- 把 `ConfigureServices` 当成启动阶段，在里面连接数据库或创建后台任务；
- 用目录或 Attribute 扫描顺序代替显式依赖图；
- 多个模块 Priority 并列时静默选择一个 Primary Host；
- 模块各自注册 `IHostedService`，让生命周期失去统一权威；
- 第一个 Stop 失败就退出，导致其他资源永远不清理；
- 启动到一半的模块把自清理责任全部推给上层；
- 在 `Starting` 时提前暴露 ServiceProvider 作为“已经可用”；
- 把 `Degraded` 和 `Faulted` 都粗暴理解成失败；
- Stop 超时后重新调用同一个非幂等清理操作，造成二次释放。

## 迁移到其他项目的判断套路

为一个模块化 .NET 服务或桌面宿主设计生命周期时，按顺序回答：

1. 模块身份和依赖如何声明？
2. 谁校验缺失依赖、重复 ID 和依赖环？
3. 多个可启动模块如何得到确定性顺序？
4. 谁是唯一生命周期协调者？
5. “注册完成”和“真正可用”之间怎样区分？
6. 当前模块启动到一半失败时，谁清理局部资源？
7. 上层怎样逆序回滚已经成功的模块？
8. 停止失败后怎样继续清理和再次重试？
9. 哪些故障允许 Degraded，哪些必须 Faulted？
10. 对外可用状态何时原子发布和撤销？

如果这些问题没有答案，增加再多 `IHostedService` 也只是把不确定性藏进框架回调。

## 源码核对锚点

- `src/OpenCoWork.Abstractions/RuntimeContracts.cs`
- `src/OpenCoWork.Core/Hosting/ModuleRegistry.cs`
- `src/OpenCoWork.Core/Hosting/WorkspaceRuntime.cs`
- `src/OpenCoWork.App/Program.cs`
- `src/OpenCoWork.Automations/AutomationsModule.cs`
- `src/OpenCoWork.Teams/TeamsModule.cs`
- `tests/OpenCoWork.Core.Tests/ModuleRegistryTests.cs`
- `tests/OpenCoWork.Core.Tests/WorkspaceRuntimeTests.cs`
- `tests/OpenCoWork.Generators.Tests/RuntimeCatalogGeneratorTests.cs`

## 本课作业

暂时不要看答案，先从不变量推导。

1. 从 `[OpenCoWorkModule]` 开始，按顺序写出一个模块最终进入 `WorkspaceRuntimeStartedState` 之前经过的阶段，并说明每一阶段排除什么错误。
2. 假设有 `session`、`teams -> session`、`automations -> session`、`gateway -> session + automations` 四个模块。按当前确定性拓扑算法写出启动顺序和停止顺序。
3. 为什么 DI 容器已经构建、所有 Service 也能解析时，`WorkspaceRuntime.StartedState` 在 `Starting` 阶段仍必须拒绝访问？
4. 模块按 `A → B → C` 启动，C 的 `StartAsync` 中途失败。Coordinator 应停止谁？C 已经创建但尚未登记的局部资源由谁清理？如果 B 停止也失败，Workspace 应进入什么状态？
5. Primary Host 与依赖图中的根节点有什么区别？为什么最高 Priority 并列时不能随便选一个？
6. 为什么禁止模块自行注册 `IHostedService`？如果确实需要一个长期后台循环，应该由谁启动和停止？
7. `Degraded` 与 `Faulted` 分别允许哪些对外行为？为什么前者可以读取 StartedState，后者不可以？
8. 为一个包含本地数据库、串口设备、后台同步和 WPF UI 的应用设计最小模块图，给出启动顺序、逆序停止顺序，以及每个模块启动失败时的局部清理责任。

## 本课掌握记录

- 状态：学习中（2026-08-02）
- 已掌握：待填写
- 仍有疑问：待填写
- 可迁移案例：待填写
- 纠偏记录：待填写
