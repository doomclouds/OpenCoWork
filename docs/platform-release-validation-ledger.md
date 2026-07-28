# OpenCoWork 双平台真机发布验证台账

## 目的

本台账统一记录 OpenCoWork 在 `win-x64` 与 `osx-arm64` 上的真实发布验证状态。
里程碑规格、实施计划和交付归档仍是具体能力的权威证据；本文件只负责回答：

- 哪个提交或可重建源码状态在哪个平台真实运行过；
- 运行了哪些构建、测试、发布目录和安全场景；
- 哪些结果只是交叉发布，不能算真机通过；
- 当前哪个平台证据仍阻止 Slice 或 1.0 发布关闭。

Provider 的真实兼容性不在此重复维护，统一见
[Provider 真实兼容性待验证清单](provider-validation-backlog.md)。

## 判定规则

只有同时满足以下条件，某一平台行才能标记为 `Passed`：

1. 记录可定位的 Commit SHA；若工作树不干净，还必须记录 Source Patch SHA-256，
   不得只写分支名或“最新代码”。
2. 在目标平台真实运行要求的 Release build、离线测试和 RID 发布命令。
3. 从对应 RID 的发布目录实跑该 Slice 要求的 CLI、迁移、恢复、File、Shell、
   Web 或 Provider 场景，不能只运行源码目录测试。
4. 执行该 Slice 规定的进程残留、路径逃逸、Secret Canary、日志和持久化扫描。
5. 结果、环境和证据入口已写入本台账或对应唯一交付归档。

状态只使用：

- `Passed`：该平台对当前登记基线的要求全部满足；
- `Pending`：仍缺真实平台执行或存在未关闭失败；
- `Not Required`：所属 Slice 的冻结规格明确不要求该平台证据；
- `Superseded`：旧基线已被更新的真机结果替代，保留历史入口但不再作为当前结论。

`dotnet publish -r <RID>` 在另一操作系统成功时，只能登记为 `Cross-publish
Passed`，不得把目标平台状态改为 `Passed`。M10 必须在最终发布候选上重新执行两端
完整验收，早期里程碑结果不能直接沿用为 1.0 发布结论。

## 当前台账

| Slice | 平台 | 状态 | 验证基线 | 环境 | 已验证范围 | 权威证据 |
| --- | --- | --- | --- | --- | --- | --- |
| M1 Runtime Foundation | `win-x64` | Passed | `30977e0cdd727d0f32a03c47a64bd5e3c88e6f00` | Windows 11；.NET SDK `10.0.302`；Runtime `10.0.10` | Release build、70 项测试、`win-x64` publish、发布目录 `--version`/`init`/`doctor --json`、原生 Symlink/UAC 专项、Secret 与残留检查 | [M1 交付归档](superpowers/archives/2026-07/2026-07-25-open-cowork-m1-runtime-foundation-archives.md) |
| M1 Runtime Foundation | `osx-arm64` | Passed | `7ae53f2de59f4959b2097f1837e28a95d6db81ae` + Source Patch SHA-256 `c2d3a54e9455d16f90db1f5fb21f8923dbb2a120101e773ed54f54335b761010` | Apple Silicon macOS 26.5.2；.NET SDK `10.0.302`；Runtime `10.0.10` | Release build、139 项测试、Mach-O arm64 publish、发布目录 `--version`/带空格路径 `init`/`doctor --json`、Trust/Secret/回滚/残留检查 | [M1 交付归档](superpowers/archives/2026-07/2026-07-25-open-cowork-m1-runtime-foundation-archives.md) |
| M2 Durable Session Core | `win-x64` | Passed | `a99f8aa61bd541eee3a0b386b9b398ac15ddbb91` | Windows 11；.NET SDK `10.0.302`；Runtime `10.0.10` | Release build、139 项测试、Journal/SQLite/并发/故障恢复/Junction 边界、`win-x64` 发布目录 `--version`/`init`/`doctor --json` | [M2 交付归档](superpowers/archives/2026-07/2026-07-26-open-cowork-m2-durable-session-core-archives.md) |
| M2 Durable Session Core | `osx-arm64` | Passed | `7ae53f2de59f4959b2097f1837e28a95d6db81ae` + Source Patch SHA-256 `c2d3a54e9455d16f90db1f5fb21f8923dbb2a120101e773ed54f54335b761010` | Apple Silicon macOS 26.5.2；.NET SDK `10.0.302`；Runtime `10.0.10` | 默认与大小写敏感 APFS 各 139 项测试、Journal/SQLite/并发/恢复/Symlink、Mach-O arm64 发布目录 `doctor --json` | [M2 交付归档](superpowers/archives/2026-07/2026-07-26-open-cowork-m2-durable-session-core-archives.md) |
| M3 Agent Runtime Alpha | `win-x64` | Not Required | — | — | M3 冻结边界只要求首个真实 Provider；Windows Provider 兼容性进入独立待验证清单 | [M3 交付归档](superpowers/archives/2026-07/2026-07-27-open-cowork-m3-agent-runtime-alpha-archives.md) |
| M3 Agent Runtime Alpha | `osx-arm64` | Passed | `3da2e47f1a917529e3264535b7f9efed66d1b2bb` | Apple Silicon macOS；.NET SDK `10.0.302`；Runtime `10.0.10` | 183 项离线测试、`osx-arm64` publish、DeepSeek Pro/Flash 真实冒烟、Usage 对账与 Secret Canary | [M3 交付归档](superpowers/archives/2026-07/2026-07-27-open-cowork-m3-agent-runtime-alpha-archives.md) |
| M4 Tool Runtime Alpha | `win-x64` | Pending | 产品基线 `d236f29`；交叉发布已通过 | 真实环境待登记 | M4 已按用户确认的延期边界归档；仍待完整离线回归、PowerShell Host、File/Shell/Web 发布目录 Smoke、输出超限/取消后的进程树残留和 Secret Canary，交叉发布不计真机通过 | [M4 交付归档](superpowers/archives/2026-07/2026-07-28-open-cowork-m4-tool-runtime-alpha-archives.md) |
| M4 Tool Runtime Alpha | `osx-arm64` | Passed | 产品基线 `d236f29` | Apple Silicon macOS 26.5.2；.NET SDK `10.0.302`；Runtime `10.0.10` | 259 项离线测试、Release build 0/0、Mach-O arm64 发布目录真实 CLI 审批链、File 原子写、`/bin/zsh`、Web 私网拒绝、进程树清理与全表面 Secret Canary | [M4 交付归档](superpowers/archives/2026-07/2026-07-28-open-cowork-m4-tool-runtime-alpha-archives.md) |
| M5 OpenCoWork Wire Alpha | `win-x64` | Pending | 产品基线 `882efd9c22e2323060d23938501191dcc409b981`；App/TestClient 交叉发布已通过 | 真实环境待登记 | App 与 Protocol TestClient 已生成 PE32+ x86-64 产物；仍待 Windows 真机 Release 回归、stdio/ACP/WebSocket、Bearer Header、重连、慢读端、取消、Secret Canary 与进程残留验证，交叉发布不计真机通过 | [M5 实施计划 Outcome 6](superpowers/plans/2026-07-28-open-cowork-m5-wire-alpha-implementation-plan.md) |
| M5 OpenCoWork Wire Alpha | `osx-arm64` | Passed | 产品基线 `882efd9c22e2323060d23938501191dcc409b981` | Apple Silicon macOS 26.5.2 (`25F84`)；.NET SDK `10.0.302`；Runtime `10.0.10` | 280 项离线测试、Release build 0/0、App/TestClient Mach-O arm64 发布目录真实运行；Wire stdio、ACP v1、loopback WebSocket Bearer Header、重连去重、慢读端、业务取消、Secret Canary 与子进程回收全部通过 | [M5 实施计划 Outcome 6](superpowers/plans/2026-07-28-open-cowork-m5-wire-alpha-implementation-plan.md) |
| M10 OpenCoWork 1.0 Closure | `win-x64` | Pending | 最终发布候选待定 | 待登记 | 安装、升级、迁移、恢复、安全、性能、签名和完整发布候选验收 | [Runtime 1.0 里程碑](milestones/2026-07/open-cowork-runtime-1-0/README.md) |
| M10 OpenCoWork 1.0 Closure | `osx-arm64` | Pending | 最终发布候选待定 | 待登记 | 安装、升级、迁移、恢复、安全、性能、签名/公证和完整发布候选验收 | [Runtime 1.0 里程碑](milestones/2026-07/open-cowork-runtime-1-0/README.md) |

## M4 Windows 后续验证项

M4 功能需求已按用户确认的延期边界关闭，`win-x64` 真机状态仍为 `Pending`。
后续集中验证必须至少包含：

1. `dotnet test OpenCoWork.slnx -c Release`
2. `dotnet build OpenCoWork.slnx -c Release --no-restore`
3. `dotnet publish src/OpenCoWork.App/OpenCoWork.App.csproj -c Release -r win-x64 --self-contained false`
4. `ToolRuntimeIntegrationTests` 全部通过；
5. 发布目录真实完成 File、Shell、Web 和 CLI Approval/Resume Smoke；
6. 确认 Shell 实际宿主优先为 `pwsh`、缺失时为 `powershell.exe`；
7. 确认输出超限与取消后父子进程均无残留；
8. 对 Journal、SQLite、Session Event、Provider Tool Message、日志、stdout、
   stderr 和测试目录执行 Secret Canary 零命中扫描；
9. 记录 Windows 版本、CPU 架构、SDK/Runtime、Commit SHA、测试计数和执行时间。

完成后才能把 `M4-ACC-006`、`M4-ACC-009` 从 `Deferred` 改为 `Passed`，并回写
M4 交付归档和本台账。该缺口不再阻止 M4 功能需求归档，但在补验前不得声明
Windows 真机通过，也不能关闭 M10 双平台发布候选。

## M5 Windows 后续验证项

M5 当前为 `8 Passed / 1 Planned`，`win-x64` 真机状态为 `Pending`，未取得用户对
该缺口的延期确认，因此 M5 保持 `In Progress` 且不创建交付归档。后续必须在
Windows x64 真机对基线提交或更新后的干净提交执行：

1. `dotnet test OpenCoWork.slnx -c Release --no-restore`；
2. `dotnet build OpenCoWork.slnx -c Release --no-restore`；
3. 分别发布 `OpenCoWork.App` 与 `OpenCoWork.Protocol.TestClient` 的
   `win-x64` framework-dependent 产物；
4. 从发布目录运行 TestClient，覆盖 Wire stdio、ACP v1、loopback WebSocket、
   Bearer Header 拒绝、重连去重、慢读端和业务取消；
5. 对协议、stdout、stderr、日志、Journal、SQLite 与配置执行 Secret Canary
   零命中扫描，并确认所有子进程均已退出；
6. 记录 Windows 版本、架构、SDK/Runtime、Commit SHA、测试计数与运行时间。

完成后才能把 `M5-ACC-002` 从 `Planned` 改为 `Passed`、把 M5 的 Windows 行改为
`Passed`，并创建 M5 交付归档、关闭里程碑 Slice。

## 更新规则

- 每次新增真机结果时只追加或替换对应 Slice/平台行，不复制完整测试日志。
- 详细测试映射进入 Slice 计划或唯一交付归档，本台账保留结论、基线和入口。
- 失败必须保留为 `Pending` 并写明失败场景；不得删除失败、降低 Acceptance 或改写
  为交叉发布成功。
- 若验证使用脏工作树，必须同时保存 `git diff --binary` 的 SHA-256；关闭 Slice 前
  应在实际提交 SHA 上复跑关键发布场景。
- 更新本台账后检查 `AGENTS.md` 引用仍有效；只有 Slice 状态变化时才同步里程碑
  CHECKLIST/INDEX。
