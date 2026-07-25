# OpenCoWork M1 Runtime Foundation 设计规格

## 文档状态

- 状态：已确认，待按统一实施计划完成
- 日期：2026-07-25
- 所属里程碑：OpenCoWork Runtime 1.0 / M1
- 当前已完成阶段：Solution & Build Baseline
- 目标框架：.NET 10
- 当前实现平台：`win-x64`
- M1 正式验收平台：`win-x64`、`osx-arm64`
- 原始能力参考：
  [DotCraft 核心运行时复刻规范](../../../DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md)
- M0 冻结契约：
  [OpenCoWork M0 Contract Freeze](2026-07-25-open-cowork-m0-contract-freeze-design.md)
- 路线规格：
  [OpenCoWork Runtime 1.0 路线规格](2026-07-25-open-cowork-runtime-1-0-roadmap.md)
- 能力台账：
  [OpenCoWork M0 能力台账](2026-07-25-open-cowork-m0-capability-ledger.md)
- 验收目录：
  [OpenCoWork M0-M10 验收目录](2026-07-25-open-cowork-m0-acceptance-catalog.md)

## 1. 目标

M1 将当前以规格文档为主的仓库推进为可构建、可初始化、可诊断并可安全启停的
.NET 10 运行时基础。已完成的 Solution & Build Baseline 是后续模块生成、
配置、路径、SQLite、宿主和 Workspace 生命周期实现的共同编译边界。

M1 作为一个完整交付单元推进，不再创建按序号拆分的独立子任务；内部阶段只用于
表达依赖顺序，最终统一按 `M1-ACC-001` 至 `M1-ACC-008` 验收和归档。

## 2. 范围

### 2.1 纳入范围

- `OpenCoWork.slnx`；
- SDK 固定与滚动策略；
- 集中构建属性和集中 NuGet 版本管理；
- 七个生产项目和六个测试项目；
- M0 冻结的生产程序集依赖方向；
- 模块、配置 Schema 和 Wire Catalog 的 Analyzer-only Generator；
- xUnit v3 测试基线；
- 基于项目文件的最小架构守卫；
- `OpenCoWorkPaths`、Workspace 发现、路径安全和 `.opencowork` 双平面目录；
- JSONC 分层配置、确定性合并、严格校验和不可变有效配置快照；
- ModuleRegistry、主宿主选择和 WorkspaceRuntime 生命周期；
- SQLite 迁移基础、PRAGMA、备份恢复和 `state_info`；
- 结构化日志与敏感字段脱敏；
- `opencowork --version`、`init` 和 `doctor`；
- Windows/macOS 一致的文本、路径和生命周期行为；
- Windows 当前实现验证与 M1 收口时的 macOS M4 正式证据。

### 2.2 明确不包含

- Session、Agent、Tool 或 OpenCoWork Wire 业务代码；
- 未被 M1 实际消费的工具函数生成器；
- Session、Teams、Automations 等后续业务表；
- StyleCop、Roslynator、FluentAssertions、Coverlet 或架构测试框架。

## 3. 工程基线：仓库级构建文件

| 文件 | 冻结职责 |
| --- | --- |
| `OpenCoWork.slnx` | 使用 .NET 10 默认 XML Solution 格式收录全部 13 个项目。 |
| `global.json` | 固定 SDK `10.0.302`，`rollForward` 为 `latestPatch`，禁止预览 SDK。 |
| `Directory.Build.props` | 统一 TFM、Nullable、ImplicitUsings、零警告和确定性构建规则。 |
| `Directory.Packages.props` | 启用 Central Package Management，并精确固定 NuGet 版本。 |
| `.editorconfig` | 统一 UTF-8、四空格缩进和基础 C# 格式。 |
| `.gitattributes` | 将仓库文本规范化为 LF，避免 Windows/macOS 行尾漂移。 |

公共构建属性：

- 除 Generator 外，默认 `TargetFramework` 为 `net10.0`；
- `Nullable=enable`；
- `ImplicitUsings=enable`；
- `TreatWarningsAsErrors=true`；
- `Deterministic=true`；
- 不设置 `LangVersion=latest`，使用目标框架对应的稳定 C# 版本；
- 不增加全局 warning suppression；
- 测试项目统一 `IsPackable=false`。

Generator 在自身项目中覆盖为 `netstandard2.0`，并固定稳定的
`LangVersion=10.0`，避免该 TFM 默认 C# 7.3 与全局 Nullable 冲突。

## 4. 工程基线：生产项目

| 项目 | TFM | 输出 | 普通 ProjectReference |
| --- | --- | --- | --- |
| `OpenCoWork.Abstractions` | `net10.0` | Library | 无 |
| `OpenCoWork.Core` | `net10.0` | Library | Abstractions |
| `OpenCoWork.Protocol` | `net10.0` | Library | Abstractions |
| `OpenCoWork.Automations` | `net10.0` | Library | Abstractions、Protocol |
| `OpenCoWork.Teams` | `net10.0` | Library | Abstractions、Protocol |
| `OpenCoWork.App` | `net10.0` | Exe | Core、Protocol、Automations、Teams |
| `OpenCoWork.Generators` | `netstandard2.0` | Library / Analyzer | 无 |

硬约束：

- `OpenCoWork.App` 的 `AssemblyName` 为 `opencowork`；
- Protocol 不得引用 Core；
- Automations 与 Teams 不得互相引用；
- Abstractions 不得引用其他生产项目；
- Generators 不得引用任何生产项目；
- 生产项目不得反向引用 App；
- Generators 不得作为普通运行时引用进入任何生产项目。

## 5. 工程基线：Generator 编译边界

以下项目以 Analyzer 方式引用 `OpenCoWork.Generators`：

- `OpenCoWork.App`
- `OpenCoWork.Core`
- `OpenCoWork.Protocol`
- `OpenCoWork.Automations`
- `OpenCoWork.Teams`

每个 Analyzer ProjectReference 必须同时满足：

```xml
OutputItemType="Analyzer"
ReferenceOutputAssembly="false"
```

工程基线阶段只接通该编译关系。Generator 项目可以为空，不创建占位生成器，也不提前
定义模块、配置或 Wire Catalog 的生成契约。

## 6. 工程基线：测试项目

| 项目 | 类型 | 普通 ProjectReference |
| --- | --- | --- |
| `OpenCoWork.Core.Tests` | xUnit v3 | Core |
| `OpenCoWork.Protocol.Tests` | xUnit v3 | Protocol |
| `OpenCoWork.Generators.Tests` | xUnit v3 | Generators |
| `OpenCoWork.ArchitectureTests` | xUnit v3 | 无 |
| `OpenCoWork.IntegrationTests` | xUnit v3 | App |
| `OpenCoWork.Protocol.TestClient` | Console Exe | Protocol |

五个自动测试项目统一使用稳定版 xUnit v3，不使用预览包：

| Package | 固定版本 |
| --- | --- |
| `xunit.v3` | `3.2.2` |
| `xunit.runner.visualstudio` | `3.1.5` |
| `Microsoft.NET.Test.Sdk` | `18.8.1` |

`xunit.runner.visualstudio` 必须使用 `PrivateAssets=all`。测试项目使用
`[Fact]` 表达固定场景，使用 `[Theory]` 表达参数化场景。

工程基线阶段不创建永远通过的占位测试。除 ArchitectureTests 外，其余测试项目只需
证明项目结构、引用和 xUnit 运行入口可恢复；真实测试随对应功能任务加入。

## 7. 工程基线：最小源代码

- 删除模板生成的 `Class1.cs`；
- 删除模板生成的示例测试；
- 不创建空接口、空服务、占位 DTO 或 `NotImplementedException` 骨架；
- `OpenCoWork.App` 只保留能够编译并以成功状态退出的最小入口；
- `OpenCoWork.Protocol.TestClient` 只保留能够编译的最小入口；
- SDK 自动生成 AssemblyInfo，不新增手写 AssemblyInfo；
- 不为后续目录预建空文件夹。

## 8. 工程基线：ArchitectureTests

ArchitectureTests 使用 BCL 的 `System.Xml.Linq.XDocument` 读取仓库内
`.csproj`，不引入 NetArchTest、ArchUnitNET 或 MSBuild Workspace。

测试从 `AppContext.BaseDirectory` 向父目录查找 `OpenCoWork.slnx`，以该目录
作为仓库根。找不到根目录必须直接失败，不得依赖调用测试时的当前工作目录。

最小测试集：

1. Solution 收录恰好七个生产项目和六个测试项目；
2. 项目名称、相对路径、TFM 和输出类型符合本规格；
3. 普通 ProjectReference 集合与本规格完全一致；
4. Analyzer ProjectReference 只出现在允许项目中，并具有两个必要元数据；
5. Protocol→Core、Teams↔Automations、业务项目→App 等禁止依赖不存在；
6. Generator 不成为普通 ProjectReference，也不进入 App 的运行时依赖；
7. App 的程序集输出名为 `opencowork`；
8. `.slnx`、`.csproj`、`.props` 中不存在 DotCraft 品牌或 `.craft` 兼容项。

第 8 项只扫描工程与构建文件，不扫描作为本机证据基线的 DotCraft 参考文档。

## 9. 工程基线：失败行为

- 缺少兼容的 `10.0.3xx` SDK 时，命令应在 restore/build 前明确失败；
- 任一编译 warning 必须导致 build 失败；
- 项目缺失、重复、改名或引用方向漂移必须导致 ArchitectureTests 失败；
- Generator 被误设为普通运行时引用必须导致 ArchitectureTests 失败；
- 测试不得因为工作目录不同而找不到仓库根；
- restore、build 或 test 失败时不得将工程基线阶段标记为完成。

## 10. 工程基线：验证

Windows 当前验证命令：

```powershell
dotnet restore OpenCoWork.slnx
dotnet build OpenCoWork.slnx -c Release --no-restore
dotnet test OpenCoWork.slnx -c Release --no-build
```

附加检查：

```powershell
dotnet sln OpenCoWork.slnx list
git status --short
```

通过条件：

- 13 个项目均成功 restore 和 build；
- Release 构建零错误、零警告；
- 五个 xUnit 项目可由 `dotnet test` 统一发现和执行；
- ArchitectureTests 全部通过；
- `bin/`、`obj/`、TestResults 和用户级 IDE 文件均未被 Git 跟踪；
- 验证后没有未解释的生成文件。

## 11. 工程基线：能力与验收映射

| 对象 | 工程基线结果 |
| --- | --- |
| CAP-001 | 建立统一 App 入口和七程序集工程边界。 |
| CAP-002 | 保持 OpenCoWork 独立品牌，不提供 DotCraft 二进制兼容。 |
| CAP-005 | 只建立 Generator Analyzer-only 项目边界，生成行为留给后续任务。 |
| M0-ACC-002 | 用项目清单和 ArchitectureTests 防止程序集边界回归。 |
| M0-ACC-008 | 用工程文件品牌检查防止 DotCraft/.craft 兼容承诺混入实现。 |
| M1-ACC-001 | 产生 Windows 构建证据；macOS M4 证据未完成前保持 Planned。 |

工程基线完成不代表 M1 完成，也不得提前将 `M1-ACC-001` 标记为 Passed。

## 12. 工程基线阶段完成证据

- 本规格中的仓库文件、项目清单和引用图全部落地；
- Windows 验证命令全部通过；
- ArchitectureTests 能主动拒绝至少一个临时构造的非法引用场景；
- 最终差异不包含业务实现、假测试或未被本任务消费的抽象；
- 工程基线已完成实现、验证和提交，作为 M1 最终交付证据的一部分；
- 工程基线不单独归档，M1 完整验收后统一形成一份交付归档。

## 13. 工程基线已确认决策

- Solution 使用 `.slnx`；
- SDK 固定 `10.0.302`，只允许同 feature band 补丁滚动；
- 测试使用 xUnit v3；
- Generator Analyzer 引用在工程基线阶段接通，但不实现生成逻辑；
- 全局启用 `TreatWarningsAsErrors`；
- 架构守卫优先使用 BCL，不新增架构测试依赖；
- Windows 先完成实现验证，M4 Mac mini 在 M1 收口时补正式平台证据。

当前没有影响工程基线结构的开放决策。

## 14. M1 已确认设计域

以下内容已在本规格内完成逐项确认，不创建新的 M1 子任务规格：

- 模块特性、ModuleRegistry、拓扑排序和稳定诊断；
- 配置节、模块注册与 Wire Catalog 的生成契约；
- JSONC 配置模型、合并、校验和不可变快照；
- Workspace 发现、`OpenCoWorkPaths`、初始化和路径安全；
- SQLite 基础、迁移状态、备份恢复与并发写入边界；
- 结构化日志、Secret Canary 和跨平台敏感信息脱敏；
- 主宿主选择、WorkspaceRuntime 状态机、失败回滚与有界停止；
- `--version`、`init`、`doctor` 的 CLI 契约；
- Windows 与 macOS M4 的完整 M1 验收。

当前没有影响 M1 实施的开放设计决策。实施过程中若发现必须改变公共契约、安全
顺序或验收边界的新事实，应先回到本规格确认，不以代码先行代替设计决策。

## 15. 模块发现与注册

### 15.1 已确认架构

模块系统采用“编译期生成 Catalog，运行时校验和排序”的单一路径：

1. 各生产程序集通过模块特性声明自身模块；
2. `OpenCoWork.Generators` 在编译期生成模块描述和聚合 Catalog；
3. `OpenCoWork.App` 是内置模块 Catalog 的唯一聚合入口；
4. `ModuleRegistry` 消费生成结果，检查重复模块、缺失依赖和依赖环；
5. 通过校验后按拓扑顺序启动，并按严格逆序停止。

Generator 负责发现和生成，不负责启动服务或持有运行时状态；
`ModuleRegistry` 负责运行时不变量，不重新扫描程序集。M1 不提供反射扫描、目录
枚举或手写备用注册表，避免两套发现机制产生不同结果。

### 15.2 模块身份与依赖

模块使用稳定字符串 ID，不使用 CLR 类型名作为跨模块身份：

```csharp
[OpenCoWorkModule(
    "automations",
    Dependencies = ["protocol"],
    Priority = 55)]
```

- ID 使用小写 kebab-case，例如 `core`、`protocol`、`app-server`；
- 依赖项使用模块 ID，声明顺序不作为启动顺序；
- Generator 在构建期检查 ID 格式、重复 ID 和缺失依赖；
- `ModuleRegistry` 对输入 Catalog 执行相同运行时校验；
- 模块类、命名空间或程序集内部重构不得隐式改变稳定模块 ID；
- 模块 ID 可用于配置、诊断和未来扩展契约，但不承诺 DotCraft 名称兼容。

### 15.3 失败行为

- 重复模块、缺失依赖和依赖环必须在构建期产生可定位的稳定诊断；
- `ModuleRegistry` 仍需对输入 Catalog 执行同样校验，拒绝绕过 Generator 的非法
  测试或未来扩展输入；
- Catalog 为空或没有可用主宿主时必须明确失败，不得静默回退到程序集扫描；
- 任一模块启动失败时，只逆序停止已经成功启动的模块。

### 15.4 验证

- Generator Snapshot 覆盖生成顺序与 Catalog 内容；
- Roslyn Diagnostic 测试覆盖重复、缺失和依赖环；
- `ModuleRegistry` 单元测试覆盖确定性拓扑排序与严格逆序停止；
- 集成测试证明运行时不依赖反射或目录枚举发现模块。

### 15.5 Generic Host 与模块生命周期

.NET Generic Host 负责进程生命周期、依赖注入、配置、日志和取消信号；
`ModuleRegistry` 与单一 `ModuleLifecycleCoordinator` 负责模块依赖图的运行时
生命周期：

1. 所有启用模块按拓扑顺序完成服务注册；
2. Generic Host 构建完成后，由 Coordinator 按同一顺序启动模块；
3. 启动失败时，Coordinator 只逆序停止已经成功启动的模块；
4. 正常停止时，Coordinator 按严格逆拓扑顺序停止全部已启动模块；
5. 所有停止异常继续聚合，前一个失败不得跳过后续清理。

各模块不得直接注册为互相独立的 `IHostedService` 来表达模块生命周期，否则实际
顺序会退化为 DI 注册顺序。M1 只向 Generic Host 注册一个模块生命周期
Coordinator；模块内部服务仍通过标准 DI 获取，不另造容器。

### 15.6 主宿主选择

一次进程只允许一个主宿主，选择规则如下：

1. 内部调用提供 `preferredModuleId` 时，必须选择该模块；
2. 首选模块必须已注册、已启用且 `CanBePrimaryHost=true`，否则明确失败；
3. 未提供首选模块时，从所有已启用主宿主候选中选择最高优先级；
4. 最高优先级存在并列时失败，不使用模块 ID 或注册顺序静默决胜；
5. 没有候选时返回包含首选值和可用模块摘要的稳定诊断。

M1 生产代码只实现真正可用的 `cli` 主宿主，不创建 AppServer、ACP 或 Gateway
空壳模块。主宿主切换、并列、启动失败和回滚通过测试模块验收；真实 AppServer、
ACP 和 Gateway 分别留在其所属里程碑。

M1 不公开通用 `--host` 参数。后续命令模式由 App 组合根在内部传入首选模块 ID，
避免把运行时模块选择细节过早固化成用户 CLI 契约。

## 16. Generator 聚合契约

### 16.1 生成范围

`OpenCoWork.Generators` 继续以 Analyzer 方式接入各生产项目：

- 普通生产项目只校验当前编译中的 OpenCoWork 声明；
- 只有 `OpenCoWork.App` 设置
  `<OpenCoWorkGenerateCatalog>true</OpenCoWorkGenerateCatalog>`；
- App 编译时聚合当前源码和引用程序集中的显式 OpenCoWork 特性；
- 聚合只认特性契约，不按 `OpenCoWork.*` 程序集名称过滤；
- 生成过程不使用运行时反射或目录扫描。

### 16.2 生成结果

App 生成唯一的 `internal` 入口 `OpenCoWork.Generated.RuntimeCatalog`，其中包含：

- Modules：模块描述、依赖、优先级与主宿主资格；
- Config Schema：配置节、属性、类型、必填项和验证元数据；
- Wire Methods：Wire 方法元数据。

生成类型不构成公共 API。M1 尚无真实 Wire 业务方法时，生产 Wire Catalog 允许
为空；Generator 测试使用独立测试源码验证非空生成、排序和重复贡献，不创建
生产占位方法。

### 16.3 诊断与验证

- Generator Diagnostic ID 使用稳定的 `OCWGENxxx` 命名；
- 相同输入必须产生字节稳定的生成源码和诊断顺序；
- 重复模块、配置节或 Wire 方法必须在构建期失败；
- 无效 ID、缺失依赖和无法解析的声明必须给出可定位源码位置；
- Snapshot 测试覆盖 Modules、Config Schema 和 Wire Methods 三类输出；
- App 集成测试证明只有一个聚合 Catalog，其他项目不会各自产生聚合入口。

M1 将稳定诊断具体冻结为：

- `OCWGEN001`：无效模块 ID 或依赖 ID；
- `OCWGEN002`：重复模块 ID；
- `OCWGEN003`：缺失模块依赖；
- `OCWGEN004`：模块依赖环；
- `OCWGEN005`：无效配置节名称；
- `OCWGEN006`：重复配置节；
- `OCWGEN007`：重复 Wire 方法；
- `OCWGEN008`：无法解析、不可访问或不满足不可变/默认构造要求的目录声明。

Generator 使用与固定 SDK 编译器一致的 `Microsoft.CodeAnalysis.CSharp 5.6.0`。
三份生成源码统一输出 LF，确保 Windows 与 macOS 对相同输入产生字节一致的结果。

## 17. 配置加载与有效快照

### 17.1 配置管线

配置引擎直接使用 BCL `System.Text.Json.Nodes.JsonNode`，不使用
`Microsoft.Extensions.Configuration` 重新解释已经冻结的合并语义：

```text
读取 JSONC
→ 按来源优先级合并 JsonNode
→ Generated Config Schema 校验
→ 绑定强类型配置节
→ 冻结 EffectiveConfigSnapshot
```

JSONC 解析允许注释和尾随逗号。配置来源继续严格遵守 M0 从低到高的顺序：

1. 内置默认值；
2. `~/.opencowork/config.jsonc`；
3. `.opencowork/config.jsonc`；
4. `.opencowork/config.local.jsonc`；
5. `--config` 指定覆盖文件；
6. `OPENCOWORK__*` 环境变量；
7. CLI 显式参数与 `--set`。

### 17.2 合并、校验与绑定

- 对象递归合并；
- 具名集合按键合并；
- 数组整体替换；
- 原始树完成合并后统一校验 null、类型、范围、必填项和未知字段；
- 未知字段默认产生警告，Strict 模式将同一问题升级为错误；
- Schema 校验通过后才绑定各强类型配置节；
- 配置节使用不可变 record，运行时只暴露 `EffectiveConfigSnapshot`；
- 任何错误都必须在模块服务注册和 WorkspaceRuntime 启动前失败。

M1 不做隐式配置热重载。WorkspaceRuntime 启动时固定一个有效配置快照，文件或
环境变量变化必须通过显式重启才能生效。

### 17.3 验证

- 表驱动测试覆盖每一层优先级和跨层覆盖；
- 对象、具名集合、数组和 null 分别覆盖成功与失败场景；
- 普通模式与 Strict 模式对未知字段使用同一诊断，只改变严重级别；
- 快照发布后不存在可从外部修改的集合或可写属性；
- 验证失败时证明没有模块或后台服务被启动。

### 17.4 环境变量与 CLI 覆盖

环境变量使用双下划线分隔配置路径：

```text
OPENCOWORK__runtime__state__busyTimeout=30s
```

`--set` 使用点路径：

```text
opencowork ... --set runtime.state.busyTimeout=30s
```

覆盖规则如下：

- `OPENCOWORK__` 前缀固定，后续路径必须匹配 Generated Config Schema；
- 配置路径使用规范 lowerCamel 名称，大小写或字段拼写错误直接失败；
- 重复 `--set` 从左到右覆盖；
- 专用 CLI 参数在所有 `--set` 之后应用，具有 CLI 层内最高优先级；
- 环境变量和 `--set` 共用同一个值解析器。

值解析器按以下顺序工作：

1. `true`、`false`、`null` 和数字解析为 JSON 标量；
2. 以 `{`、`[` 或 `"` 开头的值必须是合法 JSON，否则失败；
3. 其他值直接作为字符串，因此 `30s` 不要求额外引号。

`EffectiveConfigSnapshot` 为最终叶子值保留来源种类及来源标识，包括文件路径、
环境变量名或 CLI 参数位置。诊断可以报告来源和配置路径，但不得输出 Secret
原值。

### 17.5 配置节所有权

配置节由实际消费它的程序集声明，不建立随所有模块膨胀的中央 `AppConfig`：

```csharp
[ConfigSection("runtime")]
public sealed record RuntimeConfig
{
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
```

- 配置节类型必须公开、不可变且可默认构造；
- 缺省值来自属性初始化器，Generator 生成默认实例工厂；
- 必填和范围等基础约束复用 DataAnnotations；
- Secret 字段使用 OpenCoWork `[Secret]` 标记；
- Generator 输出节描述、默认值工厂和内嵌 JSON Schema；
- Runtime 使用生成描述校验，不引入 JSON Schema 运行库；
- `EffectiveConfigSnapshot.GetRequiredSection<T>()` 提供强类型读取。

新增模块只贡献自己的配置节和生成元数据，不要求修改中央根配置类型。

### 17.6 M1 配置节与默认值

M1 只定义当前真实消费的两个配置节：

```jsonc
{
  "runtime": {
    "stopTimeout": "30s",
    "state": {
      "busyTimeout": "5s"
    }
  },
  "operations": {
    "minimumLogLevel": "information"
  }
}
```

- `runtime.stopTimeout` 默认 `30s`；
- `runtime.state.busyTimeout` 默认 `5s`；
- `operations.minimumLogLevel` 默认 `information`；
- 持续时间只接受带 `ms`、`s`、`m` 或 `h` 单位的字符串，拒绝裸数字；
- 其他 M0 顶层配置节在实际模块消费时再加入，不创建空 record。

`init` 生成的 `config.jsonc` 只包含简短说明和空对象 `{}`，不复制整套默认值。
Doctor 从 `EffectiveConfigSnapshot` 展示最终值和来源，因此默认值仍可诊断。

## 18. Workspace 发现与路径

### 18.1 Workspace 发现

Workspace 发现只在 App 边界执行一次，顺序固定为：

1. `--workspace <path>`；
2. 从启动时 CWD 向上找到最近的 `.opencowork`；
3. 从启动时 CWD 向上找到最近的 `.git` 文件或目录；
4. 启动时 CWD。

`--workspace` 只接受 Workspace 根目录，不接受直接指向 `.opencowork`。相对路径
在 CLI 边界以启动时 CWD 为基准解析一次，随后立即规范化为绝对路径。普通运行
命令要求根目录已经存在；创建行为只由 `opencowork init` 执行。

Git Root 检测只检查 `.git` 文件或目录，不启动外部 `git` 进程。发现结果只确定
Workspace 位置，不代表工作区、配置或原生能力已经受信任。

`OpenCoWorkPaths` 构造后保存规范化的绝对根路径，后续路径解析不得再次读取进程
CWD。Hub、后台服务和测试因此不会因调用期间的工作目录变化而漂移。

### 18.2 发现验证

- 表驱动测试覆盖显式路径、最近 `.opencowork`、Git Root 和 CWD 回退；
- 嵌套 Workspace 必须选择距离启动目录最近的 `.opencowork`；
- `.git` 文件与目录都可标识 Git Root；
- 显式路径不存在、不是目录或直接指向 `.opencowork` 时给出稳定错误；
- 修改进程 CWD 不得改变已经生成的 `OpenCoWorkPaths`。

### 18.3 Workspace 初始化

`opencowork init [path]` 要求目标 Workspace 根目录已经存在，不负责创建项目目录。
首次初始化只创建 M1 实际消费的内容：

```text
.opencowork/
├── config.jsonc
├── .gitignore
└── runtime/
    └── state.db
```

M1 不提前创建 `plugins.lock.json`、Skills、Automations 或其他尚未消费的空目录与
占位文件。

首次初始化必须：

1. 在 Workspace 根目录下创建同级临时初始化目录；
2. 写入默认配置和包含 OpenCoWork 管理块的 `.gitignore`；
3. 创建、迁移并验证 `runtime/state.db`；
4. 全部成功后将临时目录重命名为 `.opencowork`；
5. 任一步失败时清理临时目录，不留下可被误认为有效 Workspace 的半成品。

重复执行必须幂等：不得覆盖用户配置或重建数据库，只补齐缺失的 M1 文件、执行
必要迁移并更新 `.gitignore` 的 OpenCoWork 管理块。管理块之外的用户 Git Ignore
内容必须原样保留。

M1 不提供 `--force`。`config.local.jsonc` 与整个 `runtime/` 默认忽略；覆盖用户
配置需要用户明确编辑或删除对应文件。

### 18.4 初始化验证

- 首次成功、重复执行和部分文件缺失均有测试；
- 配置、Git Ignore 或 SQLite 任一点故障均不得留下首次初始化半成品；
- 重跑前后用户配置、数据库内容和管理块外文本保持不变；
- `.opencowork/.gitignore` 使用 UTF-8 与 LF；
- 已存在非 OpenCoWork 的 `.opencowork` 目录时必须失败并给出恢复提示。

### 18.5 路径包含安全

路径安全采用“允许根内链接、拒绝根外逃逸”的物理路径包含规则：

1. 相对路径以声明它的配置文件目录为基准解析；
2. 使用 `Path.GetFullPath` 完成词法规范化并检查 `..` 越界；
3. 对现存路径逐段解析 Symlink、Windows Junction 和其他 Reparse Point；
4. 最终物理路径必须位于声明允许的根目录内；
5. 目标不存在时，解析最近的现存父目录后再校验剩余路径；
6. 写入或创建前重新校验，缩小检查与使用之间的竞态窗口。

指向允许根内部的链接可以使用；任何中间段或最终目标逃出允许根都必须拒绝。
Windows 使用大小写不敏感比较；macOS 使用实际解析后的路径判断。

诊断可以报告逻辑路径、解析后的物理路径和允许根，但不得包含 Secret。M1 提供
路径包含保护，不宣称具备操作系统级文件系统沙箱；进程与文件系统强隔离属于后续
安全能力。

### 18.6 路径安全验证

- Windows 覆盖 Symlink、Junction、普通 Reparse Point 和大小写差异；
- macOS M4 覆盖文件及目录 Symlink；
- 根内链接通过，直接 `..`、中间链接逃逸和最终目标逃逸均失败；
- 不存在的目标通过最近现存父目录完成校验；
- 检查后替换链接的故障注入必须在写入前复检时失败。

## 19. SQLite 状态基础

### 19.1 Provider 与职责

M1 使用 `Microsoft.Data.Sqlite 10.0.10`，不引入 EF Core 或其他 ORM。SQLite
原生依赖显式锁定 `SQLitePCLRaw.bundle_e_sqlite3 3.0.4`，避免退回存在已知高危
漏洞的旧传递版本。
`StateRuntime` 只负责：

- 解析并验证 `OpenCoWorkPaths.StateDatabasePath`；
- 创建读写与只读连接；
- 应用连接策略和 PRAGMA；
- 执行 Schema 检查、迁移、备份与恢复；
- 提供 WAL Checkpoint 等数据库维护入口。

M1 只创建 `state_info`，不提前加入 Session、Teams、Automations 或其他后续业务
表。

### 19.2 连接与写入协调

所有写入统一经过每 Workspace 一个 `StateWriteCoordinator`：

- 进程内使用 `SemaphoreSlim` 串行化写事务；
- 需要抢占写锁的事务使用 `BEGIN IMMEDIATE`；
- Repository 不得各自实现重试或绕过 Coordinator；
- 跨进程竞争交给 SQLite 锁和显式 `busy_timeout` 处理。

数据库初始化时设置 `journal_mode=WAL`。所有普通连接显式应用
`synchronous=FULL`、`foreign_keys=ON`、`secure_delete=ON` 和配置的
`busy_timeout`；只读连接额外启用 `query_only=ON`。

### 19.3 迁移与恢复

迁移使用按 Schema 版本排序的代码迁移：

1. 获取 `StateWriteCoordinator`；
2. 验证当前 Schema 与迁移链连续；
3. 执行 WAL Checkpoint；
4. 使用 SQLite Backup API 创建迁移前备份；
5. 在 `state_info` 记录 `Started` 和目标版本；
6. 在事务内执行迁移并记录 `Completed`；
7. 失败时恢复备份、记录 `Failed`，并阻止 WorkspaceRuntime 启动。

迁移必须幂等。初次创建数据库时使用临时数据库完成 Schema 和 PRAGMA 验证，再
原子移动为 `state.db`；空数据库不制造无意义的迁移前备份。

### 19.4 验证

- PRAGMA 和读写连接行为通过真实 SQLite 文件测试；
- 并发写入证明同 Workspace 串行、不同 Workspace 互不阻塞；
- 迁移链缺口、重复版本和未知新版本均明确失败；
- 在 Checkpoint、Backup、DDL 和提交点分别进行故障注入；
- 任一迁移失败后，原数据库内容与 Schema 可由备份恢复；
- M1 Schema 快照只包含 `state_info`。

## 20. 结构化日志与 Secret 脱敏

### 20.1 日志边界

M1 统一使用 `Microsoft.Extensions.Logging 10.0.10` 抽象，由 Core 提供最小
`JsonLinesFileLoggerProvider`。每个进程写入独立文件：

```text
.opencowork/runtime/logs/opencowork-<UTC>-<PID>.jsonl
```

正常 CLI 结果写 stdout；诊断和详细日志只写 stderr 或日志文件，确保后续
Wire/stdio 模式不会被普通日志污染。

M1 不实现日志轮转、压缩、上传、多 Sink 路由和复杂保留策略。长驻服务运维在 M9
基于真实需求评估，不提前引入 Serilog。

### 20.2 统一脱敏

所有日志记录在进入任何 Provider 前经过 `SecretRedactor`。脱敏范围包括：

- Generated Config Schema 标记的 Secret 字段；
- 名称匹配 `password`、`token`、`secret`、`apiKey` 等敏感键的属性；
- 当前有效配置解析出的已知 Secret 原值；
- Message、Scope、结构化属性和 Exception。

脱敏发生在格式化和 Sink 分流之前，Provider 不得各自维护一套规则。配置来源
诊断可以显示字段路径和来源，但不得显示 Secret 原值。

### 20.3 验证

- 日志使用逐行独立 JSON，并包含 UTC 时间、级别、类别、事件 ID、消息和异常；
- 并发写入不得产生交错或损坏 JSON；
- 进程正常停止时必须刷新并关闭文件；
- 测试注入唯一 Secret Canary，结束后扫描 stdout、stderr 和日志文件；
- Message、Scope、属性或 Exception 中任何 Canary 命中都使测试失败；
- Wire 兼容测试证明 stdout 中不存在日志记录。

## 21. WorkspaceRuntime 生命周期

### 21.1 状态机

```text
Stopped → Starting → Running ⇄ Degraded → Stopping → Stopped
                \                     /
                 └──────→ Faulted ←──┘
```

一个异步生命周期锁串行化 `StartAsync`、`StopAsync` 和 `DisposeAsync`。只有配置、
SQLite、依赖注入和所有必要模块都成功启动后，才原子发布 `StartedState`。

`Running` 前访问运行时服务必须返回稳定状态错误，不得暴露半初始化对象。
`Degraded` 只能由明确的模块健康信号触发，信号清除后回到 `Running`。

### 21.2 启动失败与取消

- 启动失败时逆序回滚已经成功启动的模块；
- 回滚完成后记录脱敏故障并进入 `Faulted`；
- 用户主动取消启动时仍需完成回滚；
- 主动取消且回滚完整时回到 `Stopped`，不标记系统故障；
- `Faulted` 禁止直接重启，必须先完成清理并回到 `Stopped`。

### 21.3 停止与释放

停止开始时先摘除 `StartedState`，阻止新工作进入，再严格逆序清理。调用方取消不能
跳过已经开始的清理；Coordinator 使用内部 `runtime.stopTimeout` 作为硬截止。

单个模块停止超时或抛错后继续清理其余模块。结束时聚合全部异常；存在未完成清理
或停止错误时进入 `Faulted`，全部成功时进入 `Stopped`。

### 21.4 验证

- 每个合法和非法状态转换都有表驱动测试；
- 运行时服务只在 `Running` 或 `Degraded` 可访问；
- 每个启动阶段都有失败与主动取消注入；
- 停止覆盖异常、忽略取消、超时和多异常聚合；
- Faulted 直接重启失败，完成清理后可以重新启动；
- 并发 Start/Stop 不产生重复模块实例或双重释放。

## 22. M1 CLI 契约

### 22.1 主宿主前命令

M1 的入口路由顺序为：

```text
解析参数
├── --version → 直接输出版本
├── init      → WorkspaceInitializer
├── doctor    → DiagnosticRunner
└── 其他      → 主宿主选择与 WorkspaceRuntime
```

- `--version` 不发现 Workspace、不加载配置、不打开 SQLite；
- `init` 只调用路径、配置模板和 StateRuntime 初始化能力，不构建 Generic Host；
- `doctor` 默认严格只读，不创建目录、不修改配置、不执行迁移、不启动模块，也不
  创建日志文件；
- 无命令时显示帮助并成功退出，不启动空壳交互 CLI；
- 主宿主选择和 WorkspaceRuntime 通过独立集成测试验收；
- 真正的交互 CLI 在 M3 接入 `cli` 主宿主。

这些命令可以复用 Core 服务，但不得为了构造服务而隐式启动 WorkspaceRuntime。

### 22.2 路由验证

- `--version` 在无 Workspace 和损坏 Workspace 下结果一致；
- `doctor` 执行前后文件系统与数据库哈希不变；
- `init` 不创建 Generic Host 或启动模块；
- 无命令只输出帮助，不创建 `.opencowork`；
- stdout 只包含命令结果，内部诊断不混入结果流。

### 22.3 Doctor 检查与结果

`doctor` 按稳定顺序执行：

1. .NET Runtime 与 `10.0.3xx` SDK；
2. 正式平台边界；
3. Workspace 发现；
4. 路径规范化与逃逸检查；
5. 配置来源、合并、Schema 和未知字段；
6. SQLite 可读性、PRAGMA、`state_info` 和迁移状态；
7. 用户信任存储与当前 Workspace 信任状态。

每项结果统一为 `Passed`、`Warning`、`Failed` 或 `Skipped`。前置失败后，依赖该
结果的检查标记 `Skipped`；其他独立检查继续执行。

Trust Store 不存在表示尚无授权，不算失败；损坏或不可读才失败。所有结果和异常
统一经过 Secret 脱敏。

### 22.4 Doctor 输出与退出码

默认输出适合人读的文本表。`doctor --json` 输出带 `schemaVersion: 1` 的稳定
JSON，stdout 不得混入日志或其他说明。

`--strict-config` 将未知配置字段从 `Warning` 升级为 `Failed`，不改变其他检查的
严重级别。

退出码固定为：

```text
0  没有 Failed
1  至少一个检查 Failed
2  CLI 用法错误
3  Doctor 自身发生未建模故障
```

文本与 JSON 输出必须来自同一个结果模型，不能维护两套检查逻辑。

### 22.5 CLI Parser

M1 固定使用稳定版 `System.CommandLine 2.0.10`，不使用
`3.0.0-preview`，也不引用 `System.CommandLine.Hosting`。

命令、参数、选项、Help 和语法验证只在 `OpenCoWork.App` 定义。Command Handler
只负责将解析结果转换为 Core 用例输入并映射退出码，不承载 Workspace、配置、
SQLite 或生命周期业务逻辑。

Parser 验证错误统一映射到退出码 `2`。测试覆盖 Windows/POSIX 参数形式、带空格
路径、重复 `--set`、JSON 值和无效选项，避免测试只能从 `Program.Main` 黑盒触发
业务用例。

### 22.6 版本契约

M1 产品版本固定为 `0.1.0`，M10 正式收口时进入 `1.0.0`。

```text
opencowork --version
opencowork 0.1.0
```

`--version` 只输出一行稳定 SemVer，不包含日期、平台或 Git 工作区状态。版本从
集中构建属性读取，不在命令处理器中写死。

构建时能够获得 Commit SHA 时，将其写入 `InformationalVersion`。Doctor 可以
显示 `productVersion`、`informationalVersion`、`commit`、`runtimeVersion` 和
`platform`；没有 Git 元数据时 `commit` 为 `null`，不导致构建或运行失败。

## 23. M1 Trust 诊断边界

用户级 Trust Store 固定为：

```text
~/.opencowork/trust/decisions.json
```

M1 不主动创建 Trust Store，也不实现 trust/add/remove 命令或空壳
`TrustService`。文件不存在时 Doctor 返回 `Passed`，含义是当前没有任何授权。

文件存在时，Doctor 只读检查：

- 路径位于用户级 `.opencowork/trust` 内；
- Symlink 或 Reparse Point 没有逃逸；
- JSON 可以解析并具有支持的 `schemaVersion`；
- `decisions` 是数组；
- Windows 普通 Users/Everyone 不具有写权限；
- macOS group/other 不可写；仅可读时产生 `Warning`。

Windows 广泛写权限或 macOS group/other 可写均为 `Failed`。M1 不解释或执行具体
授权决定；能力来源绑定、版本、摘要、范围、重新授信和决策执行统一留到 M6。

## 24. M1 统一实施顺序

M1 是一个整体交付任务。以下阶段只表达依赖顺序，不是独立 Slice，不创建编号式
M1 子任务、独立规格或阶段归档：

1. **工程基线检查点**：复用已完成并提交的 Solution、项目图、集中构建配置和
   ArchitectureTests，不重复搭建脚手架；
2. **稳定契约**：只定义 Generator、Core 和 App 当前会消费的模块、配置、诊断与
   结果契约，不为 M2 以后预建空接口；
3. **Generator 闭环**：完成生产程序集本地校验、App 聚合
   `RuntimeCatalog`、稳定诊断和 Snapshot 验证；
4. **数据基础**：依次接通配置快照、Workspace 发现与安全初始化、SQLite 状态基础、
   结构化日志和 Secret 脱敏；
5. **组合与生命周期**：完成 `ModuleRegistry`、主宿主选择、Generic Host
   Coordinator 和 `WorkspaceRuntime` 状态机及失败回滚；
6. **CLI 闭环**：接入 `System.CommandLine`，完成 `--version`、`init` 和只读
   `doctor` 的文本/JSON 输出与稳定退出码；
7. **双平台收口**：在 Windows PC 与 M4 Mac mini 上完成构建、测试、真实 CLI、
   路径链接和故障注入验证。

每个阶段必须留下能够阻止该阶段回归的最小可运行验证，后续阶段只在其依赖验证通过
后开始。实现可以使用小提交作为恢复检查点，但提交边界不改变 M1 的统一交付边界。

只有 `M1-ACC-001` 至 `M1-ACC-008` 全部通过、Windows 与 macOS M4 正式证据齐全，
且里程碑台账同步后，M1 才能标记为 Done。届时只生成一份 M1 交付归档。
