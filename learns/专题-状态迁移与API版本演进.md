# 专题：状态迁移与 API 版本演进

## 先纠正一个术语

你看到的不是 `distribute`，而是 `Contributor`：

```csharp
public interface IWorkspaceStateMigrationContributor
```

它表达“某个模块向共享 Workspace State Schema 贡献迁移”，不是分布式数据库。

这个专题对应总索引中的 `STA-01..10` 与 `API-01..12`。

## 一、共享数据库迁移为什么要有 Contributor

OpenCoWork 是模块化单体：Core、Teams、Automations、Gateway 同进程部署，并共享一个工作区 SQLite 数据库，但每个模块应该拥有自己的表和约束。

冲突的两个目标是：

1. 全库必须只有一个确定 Schema Version 和原子迁移过程；
2. Core 不应该知道 Teams/Automations/Gateway 每张业务表的全部细节。

因此设计成：

```text
中央版本时间线：StateMigration(Version, CoreSql)
模块迁移贡献：IWorkspaceStateMigrationContributor
中央执行与恢复：StateRuntime
最终装配：OpenCoWork.App
```

这是一种“中央编排、模块自有 Schema”的扩展设计。

## 二、四层职责

### 1. Abstractions 定义贡献契约

`IWorkspaceStateMigrationContributor` 只有三个职责：

```text
TargetVersion：在哪个全局版本执行
ApplyAsync：在中央事务中应用模块 DDL
ValidateAsync：迁移后验证模块 Schema
```

接口使用 `DbConnection` / `DbTransaction`，使模块不依赖 Core 的 `StateRuntime` 具体实现，但仍被强制加入同一个事务。

### 2. Core 维护全局连续版本链

`StateMigrations` 按 1、2、3……连续保存版本。初始化时检查：

- 版本必须从 1 开始；
- 不允许缺号；
- 声明顺序必须与版本顺序一致；
- Contributor 的 `TargetVersion` 必须位于支持链内；
- 数据库版本高于当前代码时直接拒绝，不能拿旧程序猜新 Schema。

当前版本链到 v9。v6 的 Core SQL 是 `SELECT 1;`，这不是没事找事：v6 仍是全局演进点，Teams 可以在这个版本贡献自己的 Schema，而 Core 本身恰好没有 DDL。

### 3. 各模块拥有自己的迁移

当前贡献关系包括：

| 模块 | Contributor | 目标版本 |
| --- | --- | ---: |
| Teams | `TeamsStateMigrationContributor` | 6 |
| Teams | `TeamsProjectWriterMigrationContributor` | 7 |
| Teams | `TeamsCorrelationMigrationContributor` | 9 |
| Automations | `AutomationsStateMigrationContributor` | 7 |
| Automations | `AutomationsCorrelationMigrationContributor` | 9 |
| Gateway/Operations | `GatewayStateMigrationContributor` | 9 |

Contributor 自己知道需要哪些表、列、索引和约束，并在 `ValidateAsync` 中逐项验证。Core 只知道何时调用它，不知道其业务细节。

### 4. App 决定最终产品组合

`Program.StateContributors()` 汇总 Gateway、Teams、Automations 的 Contributor。这个决定放在 App，因为只有组合根知道当前产品包含哪些模块。

如果把汇总写进 Core，Core 会反向依赖外围模块；如果让模块启动后自己迁移，则无法保证全库原子版本。

## 三、迁移执行算法

### 新数据库

```text
创建随机临时数据库
→ 在同一事务中依次执行每个 Core Migration
→ 每个版本后执行对应 Contributors
→ 写 Completed 状态
→ Commit
→ Validate 全部 Schema
→ WAL Checkpoint
→ 原子移动到正式 state.db
```

好处是正式路径只会看到完整数据库，不会看到初始化到一半的库。

### 已有数据库

```text
读取 currentVersion / migrationStatus
→ 拒绝未来版本或非 Completed 状态
→ WAL Checkpoint
→ 生成 vCurrent-to-vTarget 备份
→ 标记 Started + targetVersion
→ 在中央 WriteCoordinator 事务中：
   逐版本执行 Core SQL
   + 执行同版本 Contributors
→ 写 Completed
→ 校验完整 Schema
→ 删除备份
```

### 失败恢复

```text
迁移异常
→ 若备份存在则恢复备份
→ 标记 Failed、原版本、目标版本和错误类型
→ 删除临时备份
→ 抛出稳定 StateMigrationException
```

恢复本身再失败时，原异常和恢复异常用 `AggregateException` 一起保留，不能拿第二个错误覆盖第一个根因。

## 四、这套迁移设计真正守住了什么

- **原子性**：Core 和模块 Schema 要么一起到目标版本，要么一起恢复；
- **所有权**：模块维护自己的表，Core 维护全局演进时间线；
- **可诊断性**：数据库能说明当前版本、目标版本和失败状态；
- **可验证性**：执行 DDL 不等于成功，必须检查最终表、列、索引、外键；
- **前向安全**：旧代码不打开更新的数据库；
- **可重试性**：失败恢复后可以再次执行迁移测试。

## 五、它的代价和适用边界

代价：

- 所有模块发布节奏仍受同一全局版本约束；
- Contributor 顺序和目标版本需要集中审查；
- 模块缺失可能导致其 Schema 无法验证；
- 不适合让不受信插件直接提交任意数据库 DDL。

适合：

- 模块同进程、同版本发布；
- 需要跨模块事务或统一查询；
- 共享数据库生命周期与 Workspace 一致。

不适合：

- 模块必须独立部署和回滚；
- 模块来自不受信第三方；
- 不同模块要求不同数据库引擎或可用性边界。

这些场景更适合独立数据库、独立 Migration Host 或服务级 API，而不是共享 Contributor。

## 六、最小复刻骨架

复刻这套设计时，最小结构不是复制 SQL，而是保留职责：

```csharp
public interface IMigrationContributor
{
    int TargetVersion { get; }

    ValueTask ApplyAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken);

    ValueTask ValidateAsync(
        DbConnection connection,
        CancellationToken cancellationToken);
}
```

中央 Runner 至少需要：连续链校验、当前状态表、同事务执行、迁移前备份、失败恢复、最终验证和未来版本拒绝。少一个都不是同一可靠性等级。

## 七、API 版本和数据库版本不是一回事

OpenCoWork 同时存在多种版本：

| 版本 | 管什么 | 演进方式 |
| --- | --- | --- |
| Wire Version | 当前连接能调用哪些协议方法 | 握手协商、方法门控 |
| SQLite Schema Version | 持久数据库物理结构 | 有序迁移、备份恢复 |
| ThreadJournal Schema Version | 单条权威事实如何解析 | Reader 校验或 Upcaster |
| Tool Snapshot Schema Version | 冻结工具快照的数据形状 | 兼容读取或升级 |
| Automation Definition Schema Version | 用户定义文件的格式 | 严格解析、稳定错误 |
| Product SemVer | 对外发布兼容承诺 | 1.0 后按公开契约治理 |

这些版本不能共用一个数字。它们的消费者、生命周期和兼容策略都不同。

## 八、Wire 版本管理如何工作

当前 Wire 显式支持：

```text
1.0 Session
1.1 Capability
1.2 CoWork
1.3 Automation
1.4 Operations
```

### 1. 方法声明自己的首次版本

每个 Client-to-server 方法用 `OpenCoWorkWireMethodAttribute` 声明：

```text
Method
Direction
Owner
Since
Request / Response DTO
Authority
Mutates
Idempotency
```

版本、权限和幂等要求与方法实现同址，生成器再汇总为 Catalog。这样协议文档不是另一份容易忘记更新的手写清单。

### 2. 初始化时协商连接版本

客户端提交 `wireVersions`，服务端结合当前实际装配能力选择双方支持的最高版本。连接随后固定 `_wireVersion`，不会请求到一半自动升级。

如果双方没有共同版本，返回稳定的 `protocol.versionUnsupported`。

### 3. 旧客户端看不见新方法

连接建立 `method → since` 映射。每次派发前：

```text
VersionRank(connectionVersion) < VersionRank(method.Since)
→ Method Not Found
```

这里选择“不可观察”而不是返回“请升级”，可以让旧客户端继续把未知方法当成协议中不存在，避免意外依赖未来契约。

### 4. Version 与 Capability 分开

版本说明“协议可能包含什么”；Capability 说明“当前连接双方实际启用了什么”。

例如同为较新 Wire，某个可选模块未装配或客户端未声明 `serverRequests`，相关能力仍不能使用。只检查版本会把编译期支持误当成运行时可用。

### 5. 当前实现的取舍

`VersionRank` 是显式 switch，不是通用 SemVer 比较器。这适合目前只有五个冻结版本的协议：规则简单、未知值明确失败。

当协议进入公开长期演进后，如果出现并行 Major、预发布版本或版本范围，就需要专门的版本对象和兼容矩阵，不能继续把 switch 无限加长。

## 九、API 演进的复刻规则

1. 已发布方法语义尽量保持不变；新能力优先增加新方法或新版本域；
2. 每个方法必须声明 `Since`、权限、是否修改状态和幂等要求；
3. 握手选择一个连接级版本，执行中不漂移；
4. Version 和 Capability 分开协商；
5. 老版本调用新方法时必须有确定行为；
6. DTO 新字段优先可选且有明确缺省语义；
7. Breaking Change 进入新 Major，必要时并存 Handler/DTO；
8. Catalog、方法数量、版本隔离和协商结果必须由测试冻结。

## 十、数据库迁移与 API 版本的共同哲学

它们表面一个管表、一个管协议，核心思想相同：

```text
把“变化”当作正式领域问题
→ 显式记录当前版本和目标版本
→ 在边界处协商或迁移
→ 禁止消费者猜测未知未来
→ 用验证证明转换完整
→ 失败时保持旧状态可恢复
```

架构能力的关键不是设计一个漂亮的 v1，而是让 v2 到来时，系统知道自己正在变化什么、谁负责变化、失败后退回哪里。

## 十一、代码与测试锚点

- `src/OpenCoWork.Abstractions/WorkspaceStateContracts.cs`
- `src/OpenCoWork.Core/State/StateRuntime.cs`
- `src/OpenCoWork.Core/State/GatewayState.cs`
- `src/OpenCoWork.Teams/TeamsState.cs`
- `src/OpenCoWork.Automations/AutomationsState.cs`
- `src/OpenCoWork.Abstractions/WireContracts.cs`
- `src/OpenCoWork.Protocol/OpenCoWorkJsonRpcConnection.cs`
- `src/OpenCoWork.Generators/OpenCoWorkGenerator.cs`
- `tests/OpenCoWork.Core.Tests/StateRuntimeTests.cs`
- `tests/OpenCoWork.Protocol.Tests/OpenCoWorkJsonRpcTests.cs`
- `tests/OpenCoWork.Protocol.Tests/CapabilityWireTests.cs`
- `tests/OpenCoWork.Protocol.Tests/CoWorkWireTests.cs`
- `tests/OpenCoWork.Protocol.Tests/AutomationWireTests.cs`
- `tests/OpenCoWork.Protocol.Tests/OperationsWireTests.cs`

## 十二、复刻练习

设计一个“库存核心 + 采购模块 + 销售模块”的 .NET 模块化单体：

1. 共享一个 SQLite 数据库；
2. Core 维护 v1-v3 时间线；
3. 采购在 v2 贡献表，销售在 v3 贡献表；
4. v2 迁移提交后模拟崩溃，证明能够恢复和重试；
5. 暴露 API 1.0/1.1，采购方法从 1.1 开始；
6. 1.0 客户端必须看不见采购方法；
7. 写测试冻结迁移链、Contributor 验证、版本协商和方法门控。

完成这份练习，才算真正复刻了思路，而不是只看懂 OpenCoWork 的实现。
