# OpenCoWork M1 Runtime Foundation 实施计划

**Status:** Completed；Outcome 1-8 已完成。

**Goal:** 建立可构建、可初始化、可诊断并可安全启停的 .NET 10 OpenCoWork
运行时基础。

**Why planning is required:** M1 同时覆盖 Generator、配置、路径安全、SQLite、
日志、模块、宿主、WorkspaceRuntime、CLI 和正式平台验收，需要跨会话按依赖闭包
推进，但保持一个统一交付边界。

**Acceptance:** `M1-ACC-001` 至 `M1-ACC-008` 全部通过；`opencowork --version`、
`init` 和 `doctor` 可用；主宿主选择、配置优先级、SQLite 基础、路径安全和生命周期
验证通过；Windows PC 形成正式收口证据，M4 Mac mini 真机项登记到 `AGENTS.md`
并在 M10 / 1.0 发布前统一验证；只生成一份 M1 交付归档。

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

### Outcome 7: Windows 完成 M1 收口，macOS 真机项进入滚动台账

- Work:
  - 在 Windows PC 完成 restore、Release build、完整 test、`win-x64` publish
    和发布可执行文件实跑。
  - 在 Windows 交叉发布 `osx-arm64`，只证明产物可生成，不冒充 macOS 真机证据。
  - 将 M4 的构建、CLI、路径、权限、SQLite、日志脱敏、启动回滚和有界停止项登记
    到 `AGENTS.md`，后续换 Mac 时统一验证。
  - 更新 `M1-ACC-001` 至 `M1-ACC-008` 的状态与证据，随后同步里程碑
    `CHECKLIST.md` 和 `docs/milestones/INDEX.md`。
  - Windows 收口通过且 macOS 真机项已登记后生成唯一一份 M1 交付归档。
  - Windows 原生 Symlink 补充验收复用现有路径安全测试：仅显式设置验收环境
    变量时改用文件及目录 Symlink，并只对该专项测试发起一次 UAC 提权；完整回归
    继续以普通权限运行。
- Windows validation on 2026-07-25:
  - 基线提交为 `d721836`；Windows 11 `win-x64` 使用 .NET SDK `10.0.302`、
    Runtime `10.0.10`。
  - restore 与 Release build 通过，`0` warning / `0` error；完整测试为
    Architecture `3`、Core `41`、Generators `14`、Integration `12`，合计
    `70` passed / `0` failed / `0` skipped。
  - `win-x64` publish 通过；发布目录中的真实可执行文件完成 `--version`、
    `init` 和 `doctor --json`，七项 Doctor 检查均为 `Passed`，初始化文件使用
    LF 且 SQLite 状态库存在。
  - `osx-arm64` framework-dependent 产物可在 Windows 交叉发布，但不计入
    macOS 真机证据。
  - Windows Junction、大小写不敏感包含、Trust ACL 拒绝和只读 Warning 已由
    测试覆盖。
  - 2026-07-26 使用显式验收开关复现普通权限
    `ERROR_PRIVILEGE_NOT_HELD`，随后仅提权运行 `WorkspacePathTests`；
    原生文件及目录 Symlink 专项 `5` passed / `0` failed，普通权限完整回归仍为
    `70` passed / `0` failed / `0` skipped。
  - 2026-07-25 用户确认 M1 先按 Windows 证据关闭；全部 M4 项已进入
    `AGENTS.md` 的 macOS 真机验证台账，必须在 M10 / 1.0 发布前清零。
- Risks/open questions:
  - macOS 真机验证尚未执行，不得将 Windows 交叉发布描述为 `osx-arm64` 真机
    通过；任一 Secret Canary 命中仍阻止对应平台验收。
- Verify:
  - `dotnet restore OpenCoWork.slnx`
  - `dotnet build OpenCoWork.slnx -c Release --no-restore`
  - `dotnet test OpenCoWork.slnx -c Release --no-build`
  - 提权运行 `WorkspacePathTests`，并设置
    `OPENCOWORK_VALIDATE_WINDOWS_SYMLINKS=1`
  - `dotnet publish src/OpenCoWork.App/OpenCoWork.App.csproj -c Release -r win-x64 --self-contained false`
  - `dotnet publish src/OpenCoWork.App/OpenCoWork.App.csproj -c Release -r osx-arm64 --self-contained false`
  - `git diff --check`

### Outcome 8: macOS ARM64 完成 M1/M2 已登记的真机验证

- Work:
  - 在 Apple Silicon macOS 上复跑 restore、Release build、完整测试和
    `osx-arm64` framework-dependent publish，并真实运行发布目录中的 CLI。
  - 修复真机发现的 `/var` 与 `/private/var` 物理路径别名误判，但不得放宽根外
    Symlink 拒绝或写前复检。
  - 让基于 XML 的项目图守卫按 MSBuild 路径语义读取 Windows 风格分隔符，不修改
    已冻结项目图。
  - 将提交、平台、命令、测试计数和专项结果回填到既有 M1/M2 归档及
    `AGENTS.md` 台账，不创建第二份交付归档。
- Risks/open questions:
  - 根目录 DotCraft 本机证据基线当前缺失；本 Outcome 只依据已冻结的 OpenCoWork
    跨平台、安全和验收契约修复已复现回归，不推断原实现事实。
  - 当前 `dev` 提交只能关闭 M1/M2 已登记的真机缺口；M10 发布候选仍需按
    `M10-ACC-011` 重新执行最终安装、升级、卸载和真实模型冒烟。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --filter FullyQualifiedName~WorkspacePathTests`
  - `dotnet test tests/OpenCoWork.ArchitectureTests/OpenCoWork.ArchitectureTests.csproj -c Release --filter FullyQualifiedName~Repository_project_graph_matches_frozen_contract`
  - `dotnet restore OpenCoWork.slnx`
  - `dotnet build OpenCoWork.slnx -c Release --no-restore`
  - `dotnet test OpenCoWork.slnx -c Release --no-build`
  - `dotnet publish src/OpenCoWork.App/OpenCoWork.App.csproj -c Release -r osx-arm64 --self-contained false`
  - 发布目录中的 `opencowork --version`、`init` 和 `doctor --json`
  - `dotnet format OpenCoWork.slnx --verify-no-changes --no-restore`
  - `git diff --check`
