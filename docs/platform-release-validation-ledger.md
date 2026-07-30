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
| M4 Tool Runtime Alpha | `win-x64` | Passed | `9cf7e1e366d04fd63ac55906924ea0dde630321d` + Source/Test Patch SHA-256 `848ec5c02b1ef9be5afc7d9e1ffeccfa74539d3d2978b09fa9aa6f96438b1725` | Windows 11 Home `10.0.26200` x64；.NET SDK `10.0.302`；Runtime `10.0.10` | 280 项离线测试、Release build 0/0、App `win-x64` publish、发布目录真实 PTY 审批链、File 原子写、`powershell.exe` 回退、Web 私网拒绝、输出超限/取消进程树清理与全表面 Secret Canary | [M4 交付归档](superpowers/archives/2026-07/2026-07-28-open-cowork-m4-tool-runtime-alpha-archives.md) |
| M4 Tool Runtime Alpha | `osx-arm64` | Passed | 产品基线 `d236f29` | Apple Silicon macOS 26.5.2；.NET SDK `10.0.302`；Runtime `10.0.10` | 259 项离线测试、Release build 0/0、Mach-O arm64 发布目录真实 CLI 审批链、File 原子写、`/bin/zsh`、Web 私网拒绝、进程树清理与全表面 Secret Canary | [M4 交付归档](superpowers/archives/2026-07/2026-07-28-open-cowork-m4-tool-runtime-alpha-archives.md) |
| M5 OpenCoWork Wire Alpha | `win-x64` | Passed | `9cf7e1e366d04fd63ac55906924ea0dde630321d` + Source/Test Patch SHA-256 `848ec5c02b1ef9be5afc7d9e1ffeccfa74539d3d2978b09fa9aa6f96438b1725` | Windows 11 Home `10.0.26200` x64；.NET SDK `10.0.302`；Runtime `10.0.10` | 280 项离线测试、Release build 0/0、App/TestClient `win-x64` 发布目录真实运行；Wire stdio、ACP v1、loopback WebSocket Bearer Header、重连去重、慢读端、业务取消、Secret Canary 与子进程回收全部通过 | [M5 交付归档](superpowers/archives/2026-07/2026-07-28-open-cowork-m5-wire-alpha-archives.md) |
| M5 OpenCoWork Wire Alpha | `osx-arm64` | Passed | 产品基线 `882efd9c22e2323060d23938501191dcc409b981` | Apple Silicon macOS 26.5.2 (`25F84`)；.NET SDK `10.0.302`；Runtime `10.0.10` | 280 项离线测试、Release build 0/0、App/TestClient Mach-O arm64 发布目录真实运行；Wire stdio、ACP v1、loopback WebSocket Bearer Header、重连去重、慢读端、业务取消、Secret Canary 与子进程回收全部通过 | [M5 实施计划 Outcome 6](superpowers/plans/2026-07-28-open-cowork-m5-wire-alpha-implementation-plan.md) |
| M6 Capability Ecosystem | `win-x64` | Passed | `b25d2153805c5df158c3dde0d512f31107abdaa5` + Source/Test Patch SHA-256 `40c1dce1bacda69817b725086694c0ca34052924fbd04de5eb386a4edb55d7cb` | Windows 11 Home `10.0.26200` x64；.NET SDK `10.0.302`；Runtime `10.0.10`；Git `2.50.1.windows.1` | 373 项离线测试、Release build 0/0、App/TestClient `win-x64` 发布目录真实运行；Wire 1.0/1.1、ACP、Credential Manager、动态工具、Git、Memory、隐藏 Terminal、WebSocket、进程树与 Secret Canary 通过 | [M6 交付归档](superpowers/archives/2026-07/2026-07-29-open-cowork-m6-capability-ecosystem-archives.md) |
| M6 Capability Ecosystem | `osx-arm64` | Passed | `16768f490077585285a288e2fab01a425416ff51` | Apple Silicon macOS 26.5.2 (`25F84`)；.NET SDK `10.0.302`；Runtime `10.0.10` | 373 项离线测试、Release build 0/0、App/TestClient Mach-O arm64 发布目录真实运行；Wire 1.0/1.1、ACP、Keychain、动态工具、Git、Memory、Terminal、WebSocket、进程树与 Secret Canary 通过 | [M6 交付归档](superpowers/archives/2026-07/2026-07-29-open-cowork-m6-capability-ecosystem-archives.md) |
| M7 Multi-Agent CoWork | `win-x64` | Pending | `c30f168a7c01a39915662453799427e749c8eacf`；Cross-publish Passed | 从 macOS 26.5.2 交叉发布；Windows 真机环境待登记 | App/TestClient `win-x64` 独立 restore/publish 与 PE32+ x64 摘要通过；Wire 1.2、Reparse/Junction、Worktree、恢复和残留仍待 Windows 真机 | [M7 实施计划 Outcome 10](superpowers/plans/2026-07-30-open-cowork-m7-multi-agent-cowork-implementation-plan.md) |
| M7 Multi-Agent CoWork | `osx-arm64` | Passed | `c30f168a7c01a39915662453799427e749c8eacf` | Apple Silicon macOS 26.5.2 (`25F84`)；.NET SDK `10.0.302`；Runtime `10.0.10`；Git `2.50.1` | 446 项回归、Release build 0/0、App/TestClient Mach-O arm64 发布目录 Wire 1.0/1.1/1.2、ACP、WebSocket、DAG、Mailbox、Artifact、Symlink、Worktree、恢复、Secret Canary、进程树与 Dirty Retention | [M7 实施计划 Outcome 10](superpowers/plans/2026-07-30-open-cowork-m7-multi-agent-cowork-implementation-plan.md) |
| M10 OpenCoWork 1.0 Closure | `win-x64` | Pending | 最终发布候选待定 | 待登记 | 安装、升级、迁移、恢复、安全、性能、签名和完整发布候选验收 | [Runtime 1.0 里程碑](milestones/2026-07/open-cowork-runtime-1-0/README.md) |
| M10 OpenCoWork 1.0 Closure | `osx-arm64` | Pending | 最终发布候选待定 | 待登记 | 安装、升级、迁移、恢复、安全、性能、签名/公证和完整发布候选验收 | [Runtime 1.0 里程碑](milestones/2026-07/open-cowork-runtime-1-0/README.md) |

## M4 Windows 验证结果（2026-07-29）

- 环境：Windows 11 Home `10.0.26200` x64；.NET SDK `10.0.302`；
  Runtime `10.0.10`；`pwsh` 不存在，实际 Shell Host 为系统
  `powershell.exe`。
- 基线：
  `9cf7e1e366d04fd63ac55906924ea0dde630321d`；验证时 Source/Test Patch
  SHA-256：
  `848ec5c02b1ef9be5afc7d9e1ffeccfa74539d3d2978b09fa9aa6f96438b1725`。
- `dotnet test OpenCoWork.slnx -c Release --no-restore
  --blame-hang-timeout 90s --blame-hang-dump-type none`：22.440 秒，
  Core `221`、Integration `24`、Generators `15`、Protocol `15`、
  Architecture `5`，合计 `280` passed / `0` failed / `0` skipped。
- `dotnet build OpenCoWork.slnx -c Release --no-restore`：2.756 秒，
  `0` warning / `0` error。
- App `win-x64` framework-dependent publish 成功；发布物
  `opencowork.exe` SHA-256 为
  `11F7F38EED98FC44C3845A3728AF522B8F497D1F83CEA75D3D58F527EC8C6AAA`，
  发布目录 `--version` 返回 `opencowork 0.1.0`。
- `ToolRuntimeIntegrationTests.Chat_cli_resolves_shell_approval_and_resumes_the_turn`
  为 `1` passed；Shell Host、Credential 移除、输出上限和取消进程树专项为
  `4` passed。
- 发布目录真实 PTY 在 31.333 秒内完成三次 CLI Approval/Resume、File 写入、
  `powershell.exe` Shell、Web 私网拒绝和 Tool Result 回注。Shell 证据为
  `Desktop|shell-ok|True`，Web 为 `tool.networkTargetDenied`。
- Secret Canary 未命中 Journal、SQLite、Session Event、Provider Tool Message、
  日志、stdout/stderr 或验证目录；退出后无 `opencowork`/PowerShell 残留。
- `M4-ACC-006`、`M4-ACC-009` 已由 `Deferred` 改为 `Passed`。M10 仍须在最终
  发布候选上重跑完整双平台验收。

## M5 Windows 验证结果（2026-07-29）

- 复用上述 Windows 环境、基线、280 项全量回归和 Release build 结果。
- App 与 Protocol TestClient 均完成 `win-x64` framework-dependent publish；
  TestClient `OpenCoWork.Protocol.TestClient.exe` SHA-256 为
  `F329FE294C0C348C4B7C040015ACA23E5BFA8E7AD97C2C05AA7AAB14B62F43E8`。
- 从 TestClient 发布目录运行真实 `opencowork.exe`，5.202 秒内通过
  `wire-stdio-reconnect-cancel`、`acp-v1-reconnect-cancel`、
  `wire-websocket-auth-slow-reader` 和 `secret-canary`。
- Protocol/ACP/stdout/stderr/日志/Journal/SQLite/配置 Secret Canary 零命中；
  TestClient 退出后无新 `opencowork` 子进程。
- `M5-ACC-002` 已由 `Planned` 改为 `Passed`，M5 为
  `9 Passed / 0 Planned`，交付归档和里程碑 Slice 已关闭。

## M6 双平台验证结果（Windows 2026-07-29；macOS 复验 2026-07-30）

- macOS 已于 2026-07-30 在包含 Windows 修正的提交
  `16768f490077585285a288e2fab01a425416ff51` 上重新验证；Windows 基线为
  `b25d2153805c5df158c3dde0d512f31107abdaa5` 加 Source/Test Patch SHA-256
  `40c1dce1bacda69817b725086694c0ca34052924fbd04de5eb386a4edb55d7cb`。
- macOS `dotnet build OpenCoWork.slnx -c Release --no-restore` 为
  `0` warning / `0` error；Architecture `5`、Core `298`、Generators `15`、
  Integration `32`、Protocol `23`，合计 `373` passed / `0` failed。
- Windows `dotnet build OpenCoWork.slnx -c Release --no-restore -m:1` 为
  `0` warning / `0` error；Architecture `5`、Core `298`、Generators `15`、
  Integration `32`、Protocol `23`，合计 `373` passed / `0` failed。真实
  Provider 未激活，未新增 Provider 兼容性声明。
- App 与 Protocol TestClient 分别按目标 RID restore/publish，并从发布目录运行
  真实 App；Wire 1.0、Wire 1.1 Catalog/Revision/Dynamic Callback、
  Memory/Git/Terminal、ACP v1、WebSocket 慢读端和 Secret Canary 全部通过。
- TestClient 通过 Wire 1.1 对 macOS Keychain 执行随机 Secret 的 set/clear；
  退出后 Terminal 子进程树已回收，协议、日志和工作区 Secret 扫描零命中。
- `osx-arm64` App DLL SHA-256 为
  `bcf6a70b27541dc0fd2d10ed0c0269c232ec72fd136e45c0334c2e0116c03456`，
  TestClient DLL 为
  `e144ddac63c978adcb3cb448d6f057ebc57bb4106c79ecabebd0e56bee190781`；
  TestClient 退出后未发现该发布目录的 App 残留进程。
- Windows 发布目录 TestClient 在 `11.247` 秒内返回
  `passed: true`，通过 `wire-stdio-reconnect-cancel`、
  `wire-11-catalog-dynamic-memory-git-terminal-cleanup`、
  `acp-v1-reconnect-cancel`、`wire-websocket-auth-slow-reader` 和
  `secret-canary`；Credential Manager 决策已撤销，用户目录无残留信任状态。
- Windows App EXE/DLL SHA-256 分别为
  `98286E8CE7EE9927F1F367F2613504600DF838F9BA8334F6F95C49C6EE79E2B7` /
  `09D45089BE8DAD4D2CFCAB651ADE04BD2CD5B19DA1CF0C339760B2B44FAB1F36`；
  TestClient EXE/DLL 分别为
  `F036FA905B881EF61001323FCD9471ECEDA4D5B9746D35D2476EB2EFB717FB54` /
  `13C8590F8CE58B1258554A56A096D9515942EB9E2343994DD5D6EBF6D75BB096`。
- Windows Terminal/进程树夹具显式隐藏派生控制台，退出后工作区已清理；
  `opencowork`、`ping` 和终端父进程均无持久残留。`M6-ACC-001` 至
  `M6-ACC-010` 已全部 `Passed`，M10 仍须重跑最终发布候选。

## M7 当前验证结果（macOS 2026-07-30；Windows 待验）

- 代码基线为 `c30f168a7c01a39915662453799427e749c8eacf`；工作树在验证前干净。
- `dotnet test OpenCoWork.slnx -c Release --no-restore` 为 Architecture `6`、
  Core `319`、Generators `15`、Integration `78`、Protocol `28`，合计
  `446` passed / `0` failed；真实 Provider 未激活，未新增兼容性声明。
- Outcome 10 专项为 Integration `29`、Protocol `19`；包含 16 个并发 Run、
  256 Task 恢复、DAG/权限/预算、Mailbox/Artifact、Review/Synthesis、
  Dispatch Fault、Origin Once、Symlink 与 Managed Worktree。
- `dotnet build OpenCoWork.slnx -c Release --no-restore` 为
  `0` warning / `0` error。App 与 Protocol TestClient 分别为
  `osx-arm64`、`win-x64` 独立 restore/publish。
- `osx-arm64` 发布目录 TestClient 返回 `passed: true`，通过
  `wire-stdio-reconnect-cancel`、
  `wire-11-catalog-dynamic-memory-git-terminal-cleanup`、
  `wire-12-cowork-idempotency-notification`、`acp-v1-reconnect-cancel`、
  `wire-websocket-auth-slow-reader` 与 `secret-canary`；退出后无 App/TestClient
  进程或临时工作区残留。
- `osx-arm64` App host/DLL SHA-256 为
  `162a965f8f20ad7e6b78e03d2d76c396a6e7ba193c9bb1b69ab06944f36f0212` /
  `c85b08b082dff05b850607e2b952f9092dccbcd5549d9dc7fc407c5d92490644`；
  TestClient host/DLL 为
  `bd55da6603c19010bf549dab4cd6decc03e1d9d67ed7f45b2a54169150927306` /
  `af1bbadc61b5a72c31b12ca6ad9281a46aee321cec36fb1981afc3501a9ab8ab`。
- `win-x64` 交叉发布生成 PE32+ x64 App/TestClient；App host/DLL SHA-256 为
  `82130432738d9b9f42ca841bc7d3b827caf9ff140d03fbad4060422150304798` /
  `8110feaef5f0b566a306c2a93dbb086539f2fbf03b0a95d97dfd66349aecf980`，
  TestClient host/DLL 为
  `43ac322f59b556f570583495691970b4393fc8be96818feb3647d2ba5bb28146` /
  `35e2cf3ba43796ce56b98bb994171bc0d04be1ef146ca1dcebba70382f8d9542`。
  该结果不是 Windows 真机证据，`M7-ACC-006`、`M7-ACC-007` 和完整 M7
  继续保持未关闭。

## 更新规则

- 每次新增真机结果时只追加或替换对应 Slice/平台行，不复制完整测试日志。
- 详细测试映射进入 Slice 计划或唯一交付归档，本台账保留结论、基线和入口。
- 失败必须保留为 `Pending` 并写明失败场景；不得删除失败、降低 Acceptance 或改写
  为交叉发布成功。
- 若验证使用脏工作树，必须同时保存 `git diff --binary` 的 SHA-256；关闭 Slice 前
  应在实际提交 SHA 上复跑关键发布场景。
- 更新本台账后检查 `AGENTS.md` 引用仍有效；只有 Slice 状态变化时才同步里程碑
  CHECKLIST/INDEX。
