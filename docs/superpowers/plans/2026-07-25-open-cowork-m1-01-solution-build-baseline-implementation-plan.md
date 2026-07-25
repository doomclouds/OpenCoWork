# OpenCoWork M1-01 Solution & Build Baseline 实施计划

**Goal:** 建立可在 .NET 10 下恢复、构建和测试，并由自动化守卫程序集依赖方向的 OpenCoWork 工程基线。

**Why planning is required:** 本任务同时固定 13 个项目、Analyzer 编译边界、集中包版本、跨平台文本规则和后续所有 Slice 依赖的项目图，需要可跨会话恢复的依赖顺序。

**Acceptance:** Windows 上 `restore`、Release `build` 和 `test` 全部零错误、零警告；项目清单、普通引用、Analyzer 引用和品牌边界与规格完全一致；不包含 M1-01 范围外的运行时实现；macOS 证据保持待办。

## Source Documents

- [M1-01 设计规格](../specs/2026-07-25-open-cowork-m1-01-solution-build-baseline-design.md)
- [M0 Contract Freeze](../specs/2026-07-25-open-cowork-m0-contract-freeze-design.md)
- [OpenCoWork Runtime 1.0 路线规格](../specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md)
- [M0 能力台账](../specs/2026-07-25-open-cowork-m0-capability-ledger.md)
- [M0-M10 验收目录](../specs/2026-07-25-open-cowork-m0-acceptance-catalog.md)
- 本机证据基线：`DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md`

### Outcome 1: 仓库构建策略可恢复

- Work:
  - 新增 `OpenCoWork.slnx`、`global.json`、`Directory.Build.props`、
    `Directory.Packages.props`、`.editorconfig` 和 `.gitattributes`。
  - `global.json` 固定 SDK `10.0.302`、`latestPatch`，并禁止预览 SDK。
  - 集中启用 Nullable、ImplicitUsings、确定性构建和
    `TreatWarningsAsErrors`；不启用浮动 C# 语言版本。
  - 集中固定 `xunit.v3 3.2.2`、`xunit.runner.visualstudio 3.1.5` 和
    `Microsoft.NET.Test.Sdk 18.8.1`。
  - 扩充 `.gitignore`，排除 TestResults、Visual Studio、Rider 和用户级
    MSBuild 文件；保留 DotCraft 本机证据忽略规则。
  - `.gitattributes` 只建立后续文本规范，不在本任务执行全仓库
    `git add --renormalize`。
- Risks/open questions:
  - 若稳定包在 `net10.0` 下无法恢复或运行，停止并修订规格；不得静默改用
    prerelease 包。
  - 若机器没有兼容的 `10.0.3xx` SDK，停止构建并报告缺失版本，不放宽
    `global.json`。
- Verify:
  - `dotnet --version`
  - `dotnet restore OpenCoWork.slnx`
  - `git diff --check`

### Outcome 2: 十三个项目和依赖图落地

- Work:
  - 在 `src/` 创建 Abstractions、Core、Protocol、Automations、Teams、App
    和 Generators 七个项目。
  - 在 `tests/` 创建 Core.Tests、Protocol.Tests、Generators.Tests、
    ArchitectureTests、IntegrationTests 和 Protocol.TestClient 六个项目。
  - 除 Generators 为 `netstandard2.0` 且固定 `LangVersion=10.0` 外，其余
    项目使用 `net10.0`。
  - App 和 Protocol.TestClient 为最小控制台入口；App 的 AssemblyName 为
    `opencowork`；其他生产项目不添加占位类型。
  - 按设计规格建立普通 ProjectReference；不增加冗余的传递引用。
  - App、Core、Protocol、Automations 和 Teams 以 Analyzer 元数据引用
    Generators；Generators 不进入运行时引用图。
  - 删除模板 `Class1.cs`、示例测试和未被消费的占位文件。
- Risks/open questions:
  - `dotnet new` 模板可能附带未冻结的包或属性；以设计规格和集中构建文件为
    准，删除模板噪音。
- Verify:
  - `dotnet sln OpenCoWork.slnx list`
  - `dotnet build OpenCoWork.slnx -c Release --no-restore`

### Outcome 3: xUnit 与项目图守卫能够发现回归

- Work:
  - 五个自动测试项目使用 xUnit v3，runner 依赖保持私有；不引入
    FluentAssertions、Coverlet 或架构测试包。
  - 在 `tests/OpenCoWork.ArchitectureTests/` 实现仓库根定位和基于
    `XDocument` 的项目模型读取。
  - 验证项目数量、名称、路径、TFM、输出类型、普通引用和 Analyzer 引用。
  - 验证 Protocol→Core、Teams↔Automations、业务项目→App 等禁止依赖。
  - 验证 Generators 不进入 App 运行时依赖，App 输出名为 `opencowork`。
  - 只扫描工程/构建清单中的 DotCraft 与 `.craft` 品牌泄漏，不扫描本机参考
    规范。
  - 至少使用一个内存构造的非法项目图证明守卫会失败，不修改真实项目后再回滚。
- Risks/open questions:
  - 仓库根定位必须从 `AppContext.BaseDirectory` 向上搜索 Solution，不能依赖
    PowerShell 当前目录。
- Verify:
  - `dotnet test tests/OpenCoWork.ArchitectureTests/OpenCoWork.ArchitectureTests.csproj -c Release --no-build`
  - `dotnet test OpenCoWork.slnx -c Release --no-build`

### Outcome 4: Windows 交付证据完整且范围没有漂移

- Work:
  - 从已清理的 `bin/`、`obj/` 和 TestResults 状态执行完整验证。
  - 检查 App 运行时产物不包含 `OpenCoWork.Generators`。
  - 检查 Git 只显示 M1-01 规格、计划、里程碑链接、构建清单、项目文件、最小
    入口和 ArchitectureTests。
  - 保持 `M1-ACC-001` 为 Planned，并记录 macOS M4 证据将在 M1 收口时补齐。
  - 完成实现后创建 M1-01 交付归档，再决定是否提交。
- Risks/open questions:
  - Windows 单平台通过不能升级为 DualPlatform 验收完成。
- Verify:
  - `dotnet restore OpenCoWork.slnx`
  - `dotnet build OpenCoWork.slnx -c Release --no-restore`
  - `dotnet test OpenCoWork.slnx -c Release --no-build`
  - `git diff --check`
  - `git status --short`
