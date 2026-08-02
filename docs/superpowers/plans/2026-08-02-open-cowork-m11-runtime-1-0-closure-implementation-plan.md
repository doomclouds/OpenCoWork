# OpenCoWork M11 Runtime 1.0 Closure 实施计划

**Status:** Approved；Gate 0 由本计划基线提交完成，Outcome 1–9 待执行。

**Goal:** 不增加大型子系统，把 M1–M10 已交付能力收敛为可安装、可升级、可恢复、
可诊断的 OpenCoWork 1.0，并以同一发布源提交上的 `win-x64`、`osx-arm64` 未签名
自包含包真机证据关闭 Runtime 1.0。

**Why planning is required:** M11 同时冻结公共契约和版本，复验历史 SQLite/Journal、
组合恢复、安全与扩展兼容，建立固定负载和两小时 Soak，并交付双平台安装包。若顺序
错误，会把开发目录通过误报为发布包通过、把交叉发布误报为真机证据、在证据完成前
冻结错误版本，或让安装/卸载触碰用户数据与系统安全设置。

**Acceptance:** `M10-ACC-001..012` 全部具有可复现证据；公共 Wire、配置和 Plugin
契约完成 Golden Snapshot；State v7/v8 与两个真实历史 ThreadJournal v1 Corpus 可安全
迁移/回放/恢复；安全、Provider/Plugin/MCP/LSP、固定负载与每平台两小时 Soak 通过；
`opencowork-1.0.0-win-x64.zip` 与 `opencowork-1.0.0-osx-arm64.tar.gz` 在对应真机完成
安装、升级、E2E 和卸载；SBOM、SHA-256、文档、台账与归档闭合。任一真机缺失时
M11 保持 `In Progress`。

对应设计：
[M11 Runtime 1.0 Closure 设计](../specs/2026-08-02-open-cowork-m11-runtime-1-0-closure-design.md)

路线与验收：

- [Runtime 1.0 路线规格](../specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md)
- [M0-M11 验收目录](../specs/2026-07-25-open-cowork-m0-acceptance-catalog.md)
- [Runtime 1.0 Milestone](../../milestones/2026-07/open-cowork-runtime-1-0/README.md)
- [双平台发布验证台账](../../platform-release-validation-ledger.md)
- [Provider 兼容性台账](../../provider-validation-backlog.md)

## 当前实现基线

- 计划基线为 `dev` 的 `b2eef7fb595eb0fa9d2b50ea8d37cc1f2ff5ef39`；开始
  Outcome 1 前必须确认 Gate 0 已提交、工作区干净且未覆盖用户改动；
- 当前生产版本为 `0.1.0`，Workspace State 为 v9，OpenCoWork Wire 为 1.4，生产
  程序集冻结为七个；M11 不新增生产工程；
- `ThreadJournal` 的真实 Schema 只有 v1；未知 Schema 已稳定拒绝，不创建虚构 v2/v3
  或无调用者 Upcaster；
- 已有 `StateMigrationV7Tests`、`StateMigrationV8Tests`、
  `StateMigrationV9Tests`、`ThreadJournalTests`、Protocol TestClient 与 Integration
  Runner，优先扩展现有入口；
- 已有 `AutomationLoadTests` 的 1,000 Definition / 10,000 Run 负载，以及
  `GatewayOperationsLoadTests` 的 25,600 Inbound / 10,000 Outbox / 100,000 Trace Span
  负载；M11 只冻结输入、输出和预算，不增加 Benchmark 框架；
- M1–M10 平台证据是开发基线，不能替代 M11 的最终 RC 与 `1.0.0` 发布包重跑。

## 最小变更图

| 路径 | M11 最小变更 |
| --- | --- |
| `Directory.Build.props` | 冻结 `1.0.0-rc.1`，最终门禁通过后切到 `1.0.0`；程序集版本保持 1.0。 |
| `tests/OpenCoWork.*Tests/` | 原位增加契约 Snapshot、历史 Corpus、组合恢复、安全和兼容测试。 |
| `tests/OpenCoWork.IntegrationTests/` | 复用可发布的 xUnit Runner，扩展 E2E、固定负载、资源采样与可配置 Soak；不新增 Runner 工程。 |
| `tests/OpenCoWork.Protocol.TestClient/` | 复用现有 stdio/loopback WebSocket/Wire 黑盒入口。 |
| `scripts/release/` | 使用 .NET SDK 与平台原生命令完成 publish、打包、校验和、SBOM、安装和卸载。 |
| `docs/`、`README.md` | 用户入口、协议/插件文档、Release Notes、证据、平台台账和最终归档。 |

文件可按现有职责合并；不得为计划表机械制造一文件一类型，也不得新增 NuGet、签名
服务、发布服务、Benchmark 框架或第三方安装器。

## 执行规则

- 只在 `dev` 分支按 Outcome 顺序执行；每个 Outcome 是一个且仅一个 Git Commit
  边界。Red 必须因目标能力缺失而失败，随后做最小实现、focused verification、全量
  Release 回归和独立 Commit；
- Gate 0 的设计、计划、路线、验收目录、平台台账与 Milestone 同步单独提交为
  `docs(m11): freeze runtime 1.0 closure plan`，提交成功后才开始产品改动；
- 测试默认使用临时 Workspace、临时安装根、Fake Provider、仓库内 MCP/LSP Fixture
  和 loopback；不得隐式读取真实 Secret、修改真实 PATH、访问公网或删除用户数据；
- `--purge` 只测试隔离目录与拒绝/确认语义，不对真实 `~/.opencowork` 执行；
- 真实 DeepSeek、Keychain/Credential Manager、真实用户级安装和 PATH 变更在平台 RC
  Gate 前做精确只读预检；出现系统授权弹窗、未知目标或无法恢复的状态变化时立即停下；
- Windows 必须由 `win-x64` 真机执行。没有 Windows Runner 时只完成 Mac 可执行
  Outcome，并把 Outcome 7 及后续保持 Pending；
- 交叉发布、旧 Commit、开发目录、脏工作树或另一平台的结果均不能改变目标平台状态；
- 发布包显式标记 `Unsigned`。不签名、不公证、不关闭 SmartScreen/Gatekeeper，
  不创建 GitHub Release、不推送、不托管，除非用户另行授权；
- Secret、Prompt、回答、原始 Provider 响应、绝对用户路径与原始异常不得写入 Corpus、
  Snapshot、日志、报告、SBOM 或发布包；
- RC 失败时只做与失败对应的最小修复并生成下一个实际 RC；不预建 `rc.2`，不改写
  已验证 Commit，不降低 Acceptance。

Gate 0 提交后、Outcome 1 首次 Red 前运行：

```bash
dotnet restore OpenCoWork.slnx
dotnet test OpenCoWork.slnx -c Release --no-restore
dotnet build OpenCoWork.slnx -c Release --no-restore
```

每个代码 Outcome 的 focused tests 通过后运行：

```bash
dotnet test OpenCoWork.slnx -c Release --no-restore
dotnet build OpenCoWork.slnx -c Release --no-restore
```

## Gate 0：冻结设计、计划与证据入口

- Work:
  - 校正 M10 验收汇总为 `101 Passed / 12 Planned`；
  - 同步 M11 Design、Roadmap、Acceptance Catalog、平台台账和 Milestone Checklist；
  - 落盘本计划，明确未签名、自包含、DeepSeek-only、真实 Journal v1、双平台两小时
    Soak 和 Windows 真机停止条件。
- Risks:
  - 只提交本计划列出的文档，不夹带 DotCraft 本机规范或用户改动；
  - Gate 0 只代表可施工，不得把 M11、RC 或平台状态改为 Passed。
- Verify:
  - `git diff --check`
  - `python3 <milestone-skill>/scripts/milestone_assets.py . check --json`
  - `python3 <technical-debt-skill>/scripts/technical_debt_assets.py . check --json`
  - `python3 <compound-skill>/scripts/check_indexes.py . --json`
  - 验收目录计数为 `101 Passed / 12 Planned`，所有新增相对链接存在。
- Commit: `docs(m11): freeze runtime 1.0 closure plan`

### Outcome 1：冻结 RC 身份与公共契约 Snapshot

- Red:
  - 扩展现有 Protocol/Core/Generators 测试，固定 Wire 1.4 方法、通知、DTO、枚举、
    错误码与 Catalog 的规范化 Snapshot；
  - 固定配置 JSON Schema、默认值、DeepSeek Provider/Model、Plugin Manifest/Lock 和
    七程序集边界；
  - 固定 `--version`、程序集版本与发布元数据，证明 RC 包可反向定位版本和 Commit。
- Work:
  - 将产品版本设置为 `1.0.0-rc.1`，程序集版本保持 `1.0.0.0`；
  - 用既有序列化器与确定性排序生成/校验 Golden Snapshot，不引入 Snapshot 库；
  - 审查每个基线差异，只接受兼容新增、已批准修复或明确的 Breaking Change 记录。
- Risks:
  - Snapshot 只覆盖公开契约，不把内部实现偶然冻结为 1.0 API；
  - 时间、路径、随机 ID 和 Secret 必须规范化，禁止用每次重写 Golden 掩盖漂移。
- Verify:
  - `dotnet test tests/OpenCoWork.Protocol.Tests/OpenCoWork.Protocol.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~Contract|FullyQualifiedName~Catalog|FullyQualifiedName~Wire'`
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~Configuration|FullyQualifiedName~Plugin|FullyQualifiedName~Provider'`
  - `dotnet test tests/OpenCoWork.ArchitectureTests/OpenCoWork.ArchitectureTests.csproj -c Release --no-restore`
- Acceptance contribution: `M10-ACC-001`、`M10-ACC-002`。
- Commit: `feat(m11): freeze runtime 1.0 contracts`

### Outcome 2：固定历史 SQLite/Journal Corpus 与组合恢复

- Red:
  - 从已交付 Git 历史提取并去敏 State v7、v8 数据库原件，记录来源 Commit、Schema、
    SHA-256 和期望业务快照，覆盖原件只读、v9 迁移、外键/完整性与重复执行；
  - 从 M2 交付期和后续 Payload 演进期各固定一套真实 ThreadJournal v1 Corpus，覆盖
    Checksum、Sequence、UUID、模型可见历史、投影删除重建和未知 Schema 稳定拒绝；
  - 在既有 Fault Injector 上组合 Archive/Unarchive/Delete/Fork/Rollback 的 Journal
    Flush、SQLite Commit、文件移动和重启边界。
- Work:
  - Corpus 只保存去敏最小数据与清单；测试复制到临时目录后运行，不修改仓库原件；
  - 优先补测试和恢复编排；只有现有行为无法满足冻结语义时才改生产代码；
  - 复用 State v9 Migration/Backup、ThreadJournal、Projection 与 Reconciler，不增加
    数据库、迁移器或 Upcaster。
- Risks:
  - 不用当前构造器伪造“历史”Corpus；找不到可验证来源时该 Corpus 保持 Pending；
  - 故障后禁止静默重写权威事实，必须安全恢复或进入稳定诊断状态。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~StateMigrationV7Tests|FullyQualifiedName~StateMigrationV8Tests|FullyQualifiedName~StateMigrationV9Tests|FullyQualifiedName~ThreadJournal|FullyQualifiedName~ThreadProjection|FullyQualifiedName~ThreadArchive|FullyQualifiedName~ThreadDelete|FullyQualifiedName~ThreadFork|FullyQualifiedName~Rollback'`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~Migration|FullyQualifiedName~Recovery|FullyQualifiedName~Archive|FullyQualifiedName~Fork|FullyQualifiedName~Rollback'`
- Acceptance contribution: `M10-ACC-003`、`M10-ACC-004`、`M10-ACC-005`。
- Commit: `test(m11): add historical recovery corpus`

### Outcome 3：关闭安全与最小兼容矩阵

- Red:
  - 组合现有 Secret Canary、路径 Corpus、Tool Pipeline、Hook/Approval、Plugin Trust、
    Worktree Retention 与媒体安全测试，覆盖日志、Journal、SQLite、事件、stdout/stderr、
    报告和发布目录；
  - 覆盖 Plugin 1.0 Manifest/Lock 的安装、升级、启停、卸载和故障隔离；
  - 使用仓库内真实 MCP/LSP 子进程 Fixture 覆盖握手、调用、取消、断连、升级与进程树
    清理；复验 Wire Host、ACP v1、loopback WebSocket/Bearer；
  - Provider 只激活 Fake 和显式 DeepSeek `deepseek-v4-flash` 路径，不增加其他模型。
- Work:
  - 将分散的现有测试组合为确定性矩阵与机器可读摘要；只补缺失的边界用例；
  - 所有扫描复用 Redactor、路径包含与已有 Corpus，不创建第二套安全扫描器；
  - 修复只能落在共享根因边界，不能给单个测试入口加旁路。
- Risks:
  - 默认验证禁止访问真实 Provider、用户 Secret、非 loopback 地址和第三方进程；
  - MCP/LSP 通过只声明仓库 Fixture，不外推为第三方兼容承诺。
- Verify:
  - `dotnet test tests/OpenCoWork.Core.Tests/OpenCoWork.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~Security|FullyQualifiedName~Secret|FullyQualifiedName~Path|FullyQualifiedName~Tool|FullyQualifiedName~Plugin|FullyQualifiedName~Mcp|FullyQualifiedName~Lsp|FullyQualifiedName~Worktree'`
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~Security|FullyQualifiedName~Plugin|FullyQualifiedName~Mcp|FullyQualifiedName~Lsp|FullyQualifiedName~Acp|FullyQualifiedName~WebSocket'`
- Acceptance contribution: `M10-ACC-006`、`M10-ACC-008`。
- Commit: `test(m11): close security compatibility matrix`

### Outcome 4：扩展发布候选 E2E、固定负载与 Soak Runner

- Red:
  - 扩展现有 Integration Runner，使同一入口可运行 CLI、Wire stdio/WS、ACP v1、
    Plugin、MCP/LSP、Automation、CoWork、Gateway、Hub/Operations、重启恢复与清理；
  - 固定 Automation 和 Gateway/Operations 负载输入，输出环境、Commit、阶段耗时、
    完成计数、SQLite Busy、错误、内存、句柄/描述符、线程、子进程和 WAL 的 JSON；
  - 增加可配置 Duration 的 Soak，短时测试证明循环、周期采样、取消、超时和最终清理；
  - 增加 RC 基线比较，后续可比耗时超过 `2×` 失败，正确性/资源/Secret 失败始终硬失败。
- Work:
  - 原位扩展 Integration Runner 和现有 LoadTests；使用 `TimeProvider`、
    `System.Diagnostics.Process`、`Stopwatch` 与平台原生进程指标，不加 Benchmark SDK；
  - 主循环使用 Fake Provider；真实 DeepSeek 只由平台 Gate 在开头和结尾显式调用；
  - 报告只保留统计与摘要，不保存 Prompt、回答、响应正文或绝对用户路径。
- Risks:
  - 短时 CI 测试只验证 Runner，不替代平台两小时 Soak；
  - 首个 `rc.1` 结果是基线而非公开 SLA，跨机器结果不能直接作倍率比较。
- Verify:
  - `dotnet test tests/OpenCoWork.IntegrationTests/OpenCoWork.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~AutomationLoadTests|FullyQualifiedName~GatewayOperationsLoadTests|FullyQualifiedName~ReleaseCandidate|FullyQualifiedName~Soak'`
  - 发布 Integration Runner 后执行最短离线冒烟，并验证 JSON Schema、退出码与残留清理。
- Acceptance contribution: `M10-ACC-007`、`M10-ACC-009`。
- Commit: `test(m11): add release candidate validation runner`

### Outcome 5：交付未签名自包含包、安装器、SBOM 与文档

- Red:
  - 对发布脚本做离线 Fixture 测试，覆盖精确 RID/版本/Commit、缺文件、重复执行、带
    空格路径、摘要不匹配和产物清单；
  - 对 macOS/Windows 用户级安装、升级、卸载做隔离 Profile 测试，覆盖精确入口/PATH、
    中途失败恢复、默认保留数据、`--purge` 二次确认与越界拒绝；
  - 验证包不包含 TestClient/Runner、PDB、Secret、Workspace、临时文件或 Signed/
    Notarized 误导文案；SBOM 为 SPDX，`SHA256SUMS` 可独立复算。
- Work:
  - 以 `dotnet publish --self-contained true` 生成 `win-x64` ZIP 与 `osx-arm64` tar.gz；
  - 用平台原生 PowerShell、zsh/tar 与 .NET SDK 完成最小构建、安装和卸载脚本；
  - 补齐根 README、Release Notes、安装/升级/卸载、安全、CLI/Wire/ACP、Plugin/MCP/LSP
    文档；明确 `Unsigned`、SmartScreen/Gatekeeper 精确提示与 SHA-256 核验；
  - 不关闭系统安全、不改全局策略、不安装服务、不写管理员目录。
- Risks:
  - 交叉生成包只证明产物生成；安装和运行仍必须在目标 RID 真机完成；
  - 默认卸载不得触碰 `~/.opencowork` 或 Workspace；真实 `--purge` 不在 M11 执行。
- Verify:
  - 运行发布脚本自身检查和隔离 Profile 安装/升级/卸载测试；
  - `dotnet publish src/OpenCoWork.App/OpenCoWork.App.csproj -c Release -r osx-arm64 --self-contained true`
  - `dotnet publish src/OpenCoWork.App/OpenCoWork.App.csproj -c Release -r win-x64 --self-contained true`
  - 对两个包复算 SHA-256，校验 SPDX、文件清单、版本、Commit 和 `Unsigned` 文案。
- Acceptance contribution: `M10-ACC-010`、`M10-ACC-011`、`M10-ACC-012`。
- Commit: `feat(m11): package unsigned runtime 1.0 candidates`

### Outcome 6：执行 `osx-arm64` rc.1 真机验收

- Work:
  - 在 Apple Silicon Mac 从干净提交构建 `opencowork-1.0.0-rc.1-osx-arm64.tar.gz`；
  - 在隔离用户级目标验证安装、`--version`、`init`、`doctor --json`、升级、重启恢复、
    默认卸载保留数据和残留清理；
  - 从安装目录执行完整 E2E、State/Journal Corpus、安全/兼容矩阵、固定负载；
  - 显式激活真实 `deepseek-v4-flash` 开始/结束冒烟并运行连续两小时 Soak；
  - 记录环境、Commit、包 SHA-256、SBOM 摘要、命令、计数、预算、资源曲线和清理结果。
- Risks:
  - 真实 Secret 只进入验证进程；Keychain 弹窗或授权拒绝不得绕过；
  - 任何崩溃、挂起、SQLite Busy、数据不一致、Secret 命中、P0/P1 或无法解释的资源
    增长使 macOS 保持 Pending。
- Verify:
  - 对报告中的 Commit 与当前干净 HEAD、包内版本、SHA-256 和 SBOM 做反向核对；
  - 平台台账只在所有 macOS 门禁通过后更新为 `Passed`。
- Acceptance contribution: `M10-ACC-003..012` 的 `osx-arm64` 证据。
- Commit: `docs(m11): record macOS rc validation`

### Outcome 7：执行 `win-x64` rc.1 真机验收

- Work:
  - 在 Windows x64 真机从与 Outcome 6 相同的干净源码 Commit 构建并验证
    `opencowork-1.0.0-rc.1-win-x64.zip`；
  - 验证用户级安装、精确 PATH、升级、E2E、State/Journal Corpus、安全/兼容矩阵、
    固定负载、真实 DeepSeek 开始/结束冒烟、两小时 Soak、卸载和残留清理；
  - 记录 OS/SDK/Runtime、Commit、包摘要、SBOM、命令、计数、预算与清理结果。
- Risks:
  - macOS、Cross Publish、PE32+ 检查或旧 Windows 证据均不能替代本 Outcome；
  - Credential Manager、Registry/PATH 和进程树必须在测试前后精确核对；拒绝授权时保留
    Pending，不改系统策略。
- Verify:
  - Windows 报告、包、Commit、SHA-256 和 SBOM 相互可追溯；
  - 平台台账只在所有 Windows 门禁通过后更新为 `Passed`。
- Acceptance contribution: `M10-ACC-003..012` 的 `win-x64` 证据。
- Commit: `docs(m11): record Windows rc validation`

### Outcome 8：晋升 `1.0.0` 并在同一发布源提交双平台重跑

- Work:
  - 只有 rc.1 两平台全部 Passed 且无 P0/P1 时，把版本从 `1.0.0-rc.1` 切到
    `1.0.0`，更新 Release Notes 和确定性发布元数据；
  - 先完成本机全量回归和包结构检查，再形成不可改写的发布源 Commit；
  - `win-x64`、`osx-arm64` 均从该准确 Commit 重新构建 1.0.0 包并重跑安装、升级、
    发布目录 E2E、真实 DeepSeek、固定负载、两小时 Soak、卸载和残留检查；
  - 任何失败进入新 RC/最小修复循环，禁止在失败提交上关闭 1.0。
- Risks:
  - 为确保两个平台能引用同一 SHA，本 Outcome 的发布源 Commit 在真机重跑前形成；
    后续证据 Commit 只能改台账/归档，不得改变包输入；
  - 不打 Tag、不推送、不建 GitHub Release，除非用户另行授权。
- Verify:
  - 两个平台报告的 `release_source_commit` 完全一致；
  - 两个 1.0.0 包、SHA-256、SBOM、版本输出和 Release Notes 相互可追溯；
  - 全量 Release test/build 及两个平台完整门禁均通过。
- Acceptance contribution: `M10-ACC-001..012` 最终发布源证据。
- Commit: `chore(release): promote runtime 1.0.0`

### Outcome 9：关闭台账、Milestone 与交付归档

- Work:
  - 将 `M10-ACC-001..012` 更新为 `Passed` 并附最终发布源 Commit 与证据反链；
  - 同步平台台账、Provider 台账、Milestone Checklist/README、Milestone Index 与路线；
  - 创建唯一 M11 交付归档，记录版本、Commit、包摘要、SBOM、命令、计数、预算、
    已知限制、未签名边界和清理结果；
  - 确认 CAP-001..078 均有 Passed 或冻结 Removed/Deferred 结论，无开放 P0/P1。
- Risks:
  - Outcome 6–8 任一平台或最终版本证据缺失时不得执行本 Outcome；
  - 归档不能包含 Secret、原始 Provider 内容、用户路径或未公开环境信息。
- Verify:
  - `git diff --check`
  - Milestone、Technical Debt、Superpowers Index 与 Completion Gate 全部通过；
  - 验收计数、Checklist 进度、平台状态、归档链接和发布摘要相互一致；
  - `git status --short --branch` 只显示预期关闭文档。
- Acceptance contribution: `M10-ACC-001..012`、CAP-001..078、M11 Done。
- Commit: `docs(m11): close runtime 1.0 delivery`

## 停止条件

- 当前分支不是 `dev`、工作区出现归属不明改动或 DotCraft 本机规范进入暂存区；
- 需要扩大 Provider/Channel/平台范围，新增生产程序集或改变已冻结公共语义；
- 需要签名、公证、推送、GitHub Release、管理员权限、全局安全策略或删除真实用户数据；
- 真实 Provider、OS Secret、安装/PATH 操作出现未预期授权弹窗或无法证明可恢复；
- 缺少 Windows 真机、RC 两平台失败、最终发布源 Commit 不一致或存在开放 P0/P1。

触发停止条件时保留已验证 Commit 和真实 Pending 状态，报告最小阻塞项，不制造替代
证据。M11 只有 Outcome 9 完成后才可标记 Done。
