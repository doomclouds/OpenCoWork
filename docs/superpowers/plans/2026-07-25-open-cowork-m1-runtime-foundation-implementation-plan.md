# OpenCoWork M1 Runtime Foundation 实施计划

**Status:** In progress；Outcome 1-6 已完成。

**Goal:** 建立可构建、可初始化、可诊断并可安全启停的 .NET 10 OpenCoWork
运行时基础。

**Why planning is required:** M1 同时覆盖 Generator、配置、路径安全、SQLite、
日志、模块、宿主、WorkspaceRuntime、CLI 和双平台验收，需要跨会话按依赖闭包推进，
但保持一个统一交付边界。

**Acceptance:** `M1-ACC-001` 至 `M1-ACC-008` 全部通过；`opencowork --version`、
`init` 和 `doctor` 可用；主宿主选择、配置优先级、SQLite 基础、路径安全和生命周期
验证通过；Windows PC 与 M4 Mac mini 形成正式证据；只生成一份 M1 交付归档。

## Source Documents

- [M1 Runtime Foundation 设计规格](../specs/2026-07-25-open-cowork-m1-runtime-foundation-design.md)
- [M0 Contract Freeze](../specs/2026-07-25-open-cowork-m0-contract-freeze-design.md)
- [OpenCoWork Runtime 1.0 路线规格](../specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md)
- [M0 能力台账](../specs/2026-07-25-open-cowork-m0-capability-ledger.md)
- [M0-M10 验收目录](../specs/2026-07-25-open-cowork-m0-acceptance-catalog.md)
- 本机证据基线：`DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md`

本计划中的 Outcome 是 M1 内部依赖结果，不是独立 Slice。不得据此创建编号式 M1
子任务、独立规格或阶段归档。

### Outcome 1: 已完成的工程基线保持可恢复

- Work:
  - 复用现有 Solution、十三个项目、集中构建配置、Analyzer 引用和项目图守卫。
  - 保持七个生产项目、六个测试项目和 M0 冻结的依赖方向。
  - 不重复搭建脚手架，不新增未被后续 Outcome 消费的占位类型。
- Verify:
  - `dotnet restore OpenCoWork.slnx`
  - `dotnet build OpenCoWork.slnx -c Release --no-restore`
  - `dotnet test tests/OpenCoWork.ArchitectureTests/OpenCoWork.ArchitectureTests.csproj -c Release --no-build`

### Outcome 2: 运行时稳定契约足够支撑 M1

- Work:
  - 在实际拥有者程序集定义模块、配置节、Secret、诊断和结果契约。
  - 模块 ID 使用稳定 lower kebab-case 字符串；配置路径使用 Schema 中的规范
    lowerCamel 名称。
  - 配置节保持公开、不可变、可默认构造，并用属性初始化器和 DataAnnotations
    表达 M1 默认值与基础约束。
  - 只增加 Generator、Core 和 App 当前消费的契约，不预建 Session、Agent、
    Tool、插件或 Wire 业务抽象。
- Verify:
  - `dotnet build OpenCoWork.slnx -c Release --no-restore`
  - `dotnet test tests/OpenCoWork.ArchitectureTests/OpenCoWork.ArchitectureTests.csproj -c Release --no-build`

### Outcome 3: Generator 产生唯一且稳定的运行时目录

- Work:
  - 普通生产项目只校验当前编译中的 OpenCoWork 声明，App 单独启用聚合生成。
  - 生成 `OpenCoWork.Generated.RuntimeCatalog` 的 Modules、Config Schema 和
    Wire Methods 三个目录；M1 生产 Wire Methods 允许为空。
  - 对重复、无效 ID、缺失依赖和无法解析的声明产生稳定 `OCWGENxxx` 诊断。
  - 生成源码、目录顺序和诊断顺序对相同输入保持字节稳定。
- Risks/open questions:
  - Generator 不得引入运行时反射、目录扫描或第二份手写目录。
- Verify:
  - `dotnet test tests/OpenCoWork.Generators.Tests/OpenCoWork.Generators.Tests.csproj -c Release`
  - `dotnet build OpenCoWork.slnx -c Release --no-restore`

### Outcome 4: 配置、路径、SQLite 和日志形成数据基础

- Work:
  - 使用 `JsonNode` 完成 JSONC 分层读取、确定性合并、Generated Schema 校验、
    强类型绑定和不可变 `EffectiveConfigSnapshot`。
  - 接通环境变量、`--set` 与专用 CLI 参数的统一值解析和来源追踪，不记录 Secret
    原值。
  - 完成 Workspace 发现、`OpenCoWorkPaths`、原子且幂等的 `init`，并在写入前
    复核 Symlink、Junction 和 Reparse Point 的实际包含关系。
  - 使用 `Microsoft.Data.Sqlite` 完成 `state_info`、PRAGMA、单 Workspace 写协调、
    代码迁移、备份恢复和失败阻断。
  - 使用 `Microsoft.Extensions.Logging` 与最小 JSONL Provider 完成文件诊断；
    所有 Provider 前统一执行 Secret 脱敏。
- Risks/open questions:
  - 路径安全必须覆盖缺失目标的最近现存父目录和写入前重检。
  - 迁移失败必须恢复或阻断启动，不能留下被误认成可用的数据库。
  - Canary 不得出现在 stdout、stderr、日志属性、Scope 或异常文本中。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release`

### Outcome 5: 模块组合与 Workspace 生命周期只有一个权威顺序

- Work:
  - `ModuleRegistry` 校验重复、缺失依赖和依赖环，并产生确定性拓扑顺序。
  - Composition Root 只选择一个主宿主；M1 生产代码只提供真实 `cli` 候选，
    其他切换场景使用测试模块。
  - Generic Host 只注册一个 `ModuleLifecycleCoordinator`，按拓扑启动并按严格
    逆序停止，启动失败只回滚已成功模块。
  - `WorkspaceRuntime` 实现已冻结状态机、原子 StartedState、显式 Degraded、
    启动取消回滚、有界停止、错误聚合和 Faulted 清理门槛。
- Risks/open questions:
  - 调用方取消不得跳过必要清理；停止超时后仍需继续清理剩余模块并聚合失败。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release`

### Outcome 6: CLI 在进入宿主前完成版本、初始化和只读诊断

- Work:
  - 只在 App 使用稳定版 `System.CommandLine 2.0.10`，Parser 负责语法与参数映射，
    业务逻辑留在 Core 用例。
  - `--version` 不发现 Workspace、不读配置、不连接 SQLite，只输出稳定产品版本。
  - `init` 不启动 Generic Host，通过 WorkspaceInitializer 完成安全初始化。
  - `doctor` 按固定顺序检查运行时、平台、Workspace、路径、配置、SQLite 和 Trust；
    依赖项失败时标记 Skipped，独立检查继续。
  - 文本与 `--json` 共用结果模型；Doctor 保持只读，并使用稳定退出码与统一脱敏。
- Risks/open questions:
  - Doctor 不得创建目录、迁移数据库、启动模块或写日志文件。
- Verify:
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release`
  - `dotnet run --project src/OpenCoWork.App/OpenCoWork.App.csproj -- --version`
  - `dotnet run --project src/OpenCoWork.App/OpenCoWork.App.csproj -- doctor --json`
- Implementation result:
  - App 已接入 `System.CommandLine 2.0.10`；产品版本由集中构建属性提供，入口在构建
    Generic Host 前分流 `--version`、`init`、`doctor` 和无命令帮助。
  - `DiagnosticRunner` 以单一结果模型按固定顺序检查 Runtime、平台、Workspace、
    路径、配置、SQLite 与 Trust，并映射稳定退出码。
  - SQLite 诊断使用无连接池的 immutable 只读连接，并拒绝未 checkpoint 的活动
    Journal；集成测试通过文件 SHA-256 证明诊断前后无新增或改写。
  - CLI 集成测试覆盖两种选项风格、带空格路径、重复 `--set`、JSON 值、严格配置、
    未初始化 Workspace、损坏 Trust 和无效选项。

### Outcome 7: Windows 与 macOS M4 完成同一套 M1 收口

- Work:
  - 在 Windows PC 与 M4 Mac mini 分别完成 restore、Release build、完整 test 和
    对应 RID publish。
  - 两平台真实运行 `--version`、`init`、`doctor`，验证路径、行尾、权限、链接、
    SQLite、日志脱敏、启动回滚和有界停止。
  - 更新 `M1-ACC-001` 至 `M1-ACC-008` 的状态与证据，随后同步里程碑
    `CHECKLIST.md` 和 `docs/milestones/INDEX.md`。
  - 只有全部验收通过后生成一份 M1 交付归档；任何单平台通过都不能将 M1 标记为
    Done。
- Risks/open questions:
  - 缺少 M4 实机证据、存在未解释平台差异或任一 Secret Canary 命中时停止收口。
- Verify:
  - `dotnet restore OpenCoWork.slnx`
  - `dotnet build OpenCoWork.slnx -c Release --no-restore`
  - `dotnet test OpenCoWork.slnx -c Release --no-build`
  - `dotnet publish src/OpenCoWork.App/OpenCoWork.App.csproj -c Release -r win-x64 --self-contained false`
  - `dotnet publish src/OpenCoWork.App/OpenCoWork.App.csproj -c Release -r osx-arm64 --self-contained false`
  - `git diff --check`
