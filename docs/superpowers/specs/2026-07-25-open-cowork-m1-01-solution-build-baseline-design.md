# OpenCoWork M1-01 Solution & Build Baseline 设计规格

## 文档状态

- 状态：已确认
- 日期：2026-07-25
- 所属里程碑：OpenCoWork Runtime 1.0 / M1
- 任务：M1-01 Solution & Build Baseline
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

M1-01 把当前以规格文档为主的仓库推进为可恢复、可构建、依赖方向受保护的
.NET 10 工程基线。该基线是 M1 后续模块生成、配置、路径、SQLite、宿主和
Workspace 生命周期实现的共同编译边界。

本任务只建立工程结构与构建契约，不以提前交付可用 CLI 或运行时功能为目标。

## 2. 范围

### 2.1 纳入范围

- `OpenCoWork.slnx`；
- SDK 固定与滚动策略；
- 集中构建属性和集中 NuGet 版本管理；
- 七个生产项目和六个测试项目；
- M0 冻结的生产程序集依赖方向；
- Generator 的 Analyzer-only 编译引用；
- xUnit v3 测试基线；
- 基于项目文件的最小架构守卫；
- Windows 下的干净 restore、build 和 test 证据；
- Windows/macOS 一致的 UTF-8 与 LF 文本规则。

### 2.2 明确不包含

- ModuleRegistry、模块排序和主宿主选择；
- Roslyn Generator 的扫描、生成或诊断逻辑；
- HostBuilder 和 WorkspaceRuntime；
- 配置加载、Workspace 发现和 `OpenCoWorkPaths`；
- SQLite、日志和 Secret 脱敏；
- `opencowork --version`、`init`、`doctor` 的实际行为；
- Session、Agent、Tool 或 OpenCoWork Wire 业务代码；
- macOS M4 的最终构建证据；
- StyleCop、Roslynator、FluentAssertions、Coverlet 或架构测试框架。

## 3. 仓库级构建文件

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

Generator 在自身项目中覆盖为 `netstandard2.0`。

## 4. 生产项目

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

## 5. Generator 编译边界

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

M1-01 只接通该编译关系。Generator 项目可以为空，不创建占位生成器，也不提前
定义模块、配置或 Wire Catalog 的生成契约。

## 6. 测试项目

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

M1-01 不创建永远通过的占位测试。除 ArchitectureTests 外，其余测试项目只需
证明项目结构、引用和 xUnit 运行入口可恢复；真实测试随对应功能任务加入。

## 7. 最小源代码

- 删除模板生成的 `Class1.cs`；
- 删除模板生成的示例测试；
- 不创建空接口、空服务、占位 DTO 或 `NotImplementedException` 骨架；
- `OpenCoWork.App` 只保留能够编译并以成功状态退出的最小入口；
- `OpenCoWork.Protocol.TestClient` 只保留能够编译的最小入口；
- SDK 自动生成 AssemblyInfo，不新增手写 AssemblyInfo；
- 不为后续目录预建空文件夹。

## 8. ArchitectureTests

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

## 9. 失败行为

- 缺少兼容的 `10.0.3xx` SDK 时，命令应在 restore/build 前明确失败；
- 任一编译 warning 必须导致 build 失败；
- 项目缺失、重复、改名或引用方向漂移必须导致 ArchitectureTests 失败；
- Generator 被误设为普通运行时引用必须导致 ArchitectureTests 失败；
- 测试不得因为工作目录不同而找不到仓库根；
- restore、build 或 test 失败时不得将 M1-01 标记为完成。

## 10. 验证

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

## 11. 能力与验收映射

| 对象 | M1-01 结果 |
| --- | --- |
| CAP-001 | 建立统一 App 入口和七程序集工程边界。 |
| CAP-002 | 保持 OpenCoWork 独立品牌，不提供 DotCraft 二进制兼容。 |
| CAP-005 | 只建立 Generator Analyzer-only 项目边界，生成行为留给后续任务。 |
| M0-ACC-002 | 用项目清单和 ArchitectureTests 防止程序集边界回归。 |
| M0-ACC-008 | 用工程文件品牌检查防止 DotCraft/.craft 兼容承诺混入实现。 |
| M1-ACC-001 | 产生 Windows 构建证据；macOS M4 证据未完成前保持 Planned。 |

M1-01 完成不代表 M1 完成，也不得提前将 `M1-ACC-001` 标记为 Passed。

## 12. 完成条件

- 本规格中的仓库文件、项目清单和引用图全部落地；
- Windows 验证命令全部通过；
- ArchitectureTests 能主动拒绝至少一个临时构造的非法引用场景；
- 最终差异不包含业务实现、假测试或未被本任务消费的抽象；
- 形成对应实施计划和交付归档后，M1-01 才视为完整关闭。

## 13. 已确认决策

- Solution 使用 `.slnx`；
- SDK 固定 `10.0.302`，只允许同 feature band 补丁滚动；
- 测试使用 xUnit v3；
- Generator Analyzer 引用在 M1-01 接通，但不实现生成逻辑；
- 全局启用 `TreatWarningsAsErrors`；
- 架构守卫优先使用 BCL，不新增架构测试依赖；
- Windows 先完成实现验证，M4 Mac mini 在 M1 收口时补正式平台证据。

当前没有影响 M1-01 工程结构的开放决策。
