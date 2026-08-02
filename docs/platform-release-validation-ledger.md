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
Passed`，不得把目标平台状态改为 `Passed`。M11 必须在最终发布候选上重新执行两端
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
| M7 Multi-Agent CoWork | `win-x64` | Passed | `2d966400e61e8d17c8a513299e8a9b420591d865` + Source/Test Patch SHA-256 `516c263191620d8b9f41eb5bbce0436aac41ee04aef6be73af5c5514783e90cd` | Windows 11 Home `10.0.26200` x64；.NET SDK `10.0.302`；Runtime `10.0.10`；Git `2.50.1.windows.1` | Release build 0/0、全量非显式 Integration 串行回归、App/TestClient `win-x64` 发布目录 Wire 1.2、Reparse/Junction、Worktree、恢复、Secret Canary 与残留检查通过 | [M7 交付归档](superpowers/archives/2026-07/2026-07-30-open-cowork-m7-multi-agent-cowork-archives.md) |
| M7 Multi-Agent CoWork | `osx-arm64` | Passed | `c30f168a7c01a39915662453799427e749c8eacf` | Apple Silicon macOS 26.5.2 (`25F84`)；.NET SDK `10.0.302`；Runtime `10.0.10`；Git `2.50.1` | 446 项回归、Release build 0/0、App/TestClient Mach-O arm64 发布目录 Wire 1.0/1.1/1.2、ACP、WebSocket、DAG、Mailbox、Artifact、Symlink、Worktree、恢复、Secret Canary、进程树与 Dirty Retention | [M7 实施计划 Outcome 10](superpowers/plans/2026-07-30-open-cowork-m7-multi-agent-cowork-implementation-plan.md) |
| M8 Automations and Scheduler | `win-x64` | Passed | `2d966400e61e8d17c8a513299e8a9b420591d865` + Source/Test Patch SHA-256 `516c263191620d8b9f41eb5bbce0436aac41ee04aef6be73af5c5514783e90cd` | Windows 11 Home `10.0.26200` x64；.NET SDK `10.0.302`；Runtime `10.0.10`；Git `2.50.1.windows.1` | Release build 0/0、全量非显式 Integration 串行回归、App/TestClient `win-x64` 发布目录 Wire 1.3、Cron/DST、强杀恢复、Reparse/Junction、Worktree、Secret Canary 与残留检查通过 | [M8 交付归档](superpowers/archives/2026-07/2026-07-30-open-cowork-m8-automations-scheduler-archives.md) |
| M8 Automations and Scheduler | `osx-arm64` | Passed | `a710866ec2f812dce3bb03a72d5723ac72e68427` | Apple Silicon macOS 26.5.2 (`25F84`)；.NET SDK `10.0.302`；Runtime `10.0.10`；Git `2.50.1` | 536 项回归、100 项 M8 专项、固定负载、Release build 0/0、App/TestClient Mach-O arm64 发布目录 Wire 1.0–1.3、DST、热更新、恢复、Symlink、Worktree、取消、Secret Canary 与残留检查 | [M8 实施计划 Outcome 10](superpowers/plans/2026-07-30-open-cowork-m8-automations-scheduler-implementation-plan.md) |
| M9 DeepSeek Responses Provider | `win-x64` | Passed | `2d966400e61e8d17c8a513299e8a9b420591d865` + Source/Test Patch SHA-256 `516c263191620d8b9f41eb5bbce0436aac41ee04aef6be73af5c5514783e90cd` | Windows 11 Home `10.0.26200` x64；.NET SDK `10.0.302`；Runtime `10.0.10` | App/TestClient/Runner `win-x64` 发布目录通过 Protocol 场景及真实 `deepseek-v4-flash` Text、Function、Web Search、Apply Patch、Usage、Secret Canary 六场景 | [M9 交付归档](superpowers/archives/2026-08/2026-08-01-open-cowork-m9-deepseek-responses-provider-archives.md) |
| M9 DeepSeek Responses Provider | `osx-arm64` | Passed | `058b505174602653385c51cb35fb654dd0b31262` | Apple Silicon macOS 26.5.2 (`25F84`)；.NET SDK `10.0.302`；Runtime `10.0.10` | 577 项离线回归、Release build 0/0、App/TestClient/Runner Mach-O arm64 发布目录真实运行；Protocol TestClient 7 场景和 DeepSeek Flash Responses 六场景、Usage 容差、Secret Canary、残留扫描全部通过 | [Provider 台账](provider-validation-backlog.md) |
| M10 Gateway and Operations | `win-x64` | Passed | `2d966400e61e8d17c8a513299e8a9b420591d865` + Source/Test Patch SHA-256 `516c263191620d8b9f41eb5bbce0436aac41ee04aef6be73af5c5514783e90cd` | Windows 11 Home `10.0.26200` x64；.NET SDK `10.0.302`；Runtime `10.0.10` | App/TestClient/Runner `win-x64` 发布目录通过 8 个 Protocol 场景、13 项 Gateway/Outbox/Operations/Runtime Composition、Junction/Reparse Point、Credential Manager、Wire 1.4、Secret Canary 与残留检查 | [M10 实施计划 Outcome 10](superpowers/plans/2026-08-01-open-cowork-m10-gateway-operations-implementation-plan.md) |
| M10 Gateway and Operations | `osx-arm64` | Passed | `050b85c1c42ca2e3bd2abd5eb0943232895081d7` | Apple Silicon macOS 26.5.2 (`25F84`)；.NET SDK `10.0.302`；Runtime `10.0.10` | 638 项离线回归、Release build 0/0、App/TestClient/Runner Mach-O arm64 发布目录通过 Protocol 8 场景、Gateway/Outbox/Operations/Runtime Composition 13 项、Symlink、Keychain、Wire 1.4、Secret Canary 与残留检查 | [M10 交付归档](superpowers/archives/2026-08/2026-08-01-open-cowork-m10-gateway-operations-archives.md) |
| M11 OpenCoWork 1.0 Closure | `win-x64` | Pending | 最终发布候选待定 | 待登记 | 未签名自包含 ZIP、安装/升级/卸载、迁移、恢复、安全、固定负载、两小时 Soak、SBOM、校验和与完整发布候选验收 | [M11 设计](superpowers/specs/2026-08-02-open-cowork-m11-runtime-1-0-closure-design.md) / [计划](superpowers/plans/2026-08-02-open-cowork-m11-runtime-1-0-closure-implementation-plan.md) |
| M11 OpenCoWork 1.0 Closure | `osx-arm64` | Pending | 最终发布候选待定 | 待登记 | 未签名自包含 tar.gz、安装/升级/卸载、迁移、恢复、安全、固定负载、两小时 Soak、SBOM、校验和与完整发布候选验收 | [M11 设计](superpowers/specs/2026-08-02-open-cowork-m11-runtime-1-0-closure-design.md) / [计划](superpowers/plans/2026-08-02-open-cowork-m11-runtime-1-0-closure-implementation-plan.md) |

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
- `M4-ACC-006`、`M4-ACC-009` 已由 `Deferred` 改为 `Passed`。M11 仍须在最终
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
  `M6-ACC-010` 已全部 `Passed`，M11 仍须重跑最终发布候选。

## M7 双平台验证结果（macOS 2026-07-30；Windows 2026-08-02）

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
  该交叉发布结果本身不是 Windows 真机证据。
- Windows 于 2026-08-02 在 `2d966400e61e8d17c8a513299e8a9b420591d865`
  加 Source/Test Patch SHA-256
  `516c263191620d8b9f41eb5bbce0436aac41ee04aef6be73af5c5514783e90cd`
  上完成 Release build、全量非显式 Integration 串行回归和发布目录 TestClient；
  `wire-12-cowork-idempotency-notification`、Git/Worktree、Reparse/Junction、
  Secret Canary 与残留检查通过。`M7-ACC-006`、`007` 和完整 M7 已关闭。

## M8 双平台验证结果（macOS 2026-07-30；Windows 2026-08-02）

- 代码基线为 `a710866ec2f812dce3bb03a72d5723ac72e68427`；验证前工作树干净。
- `dotnet test OpenCoWork.slnx -c Release --no-build --no-restore` 为
  Architecture `8`、Core `357`、Generators `15`、Integration `123`、
  Protocol `33`，合计 `536` passed / `0` failed；真实 Provider 未激活，
  未新增兼容性声明。
- M8 专项为 Core `31`、Integration `45`、Protocol `24`，合计 `100`；
  固定负载为 1,000 Definition / 100 Faulted、64 Start / 并发上限 16、
  10,000 Run / 每页 100。一次样本记录 Scan `476 ms`、Schedule Lag
  `60,627 ms`、Reconcile `1` 轮 / `66 ms`、Seed `141 ms`、分页
  `414 ms`、SQLite Busy `0`，不据此设置产品 SLA。
- 首次整解运行仅在 Automation fixture 回收时遇到一次 macOS
  `state.db-wal` / `state.db-shm` 的瞬时 `Directory not empty`；目标用例连续
  10 次、Integration 全量与整解全量原样复跑均通过，业务断言无失败，临时残留已
  单独定位并清理。
- `dotnet build OpenCoWork.slnx -c Release --no-restore` 为
  `0` warning / `0` error。App 与 Protocol TestClient 分别为
  `osx-arm64`、`win-x64` 独立 restore/publish。
- `osx-arm64` 发布目录 TestClient 返回 `passed: true`，通过
  `wire-stdio-reconnect-cancel`、
  `wire-11-catalog-dynamic-memory-git-terminal-cleanup`、
  `wire-12-cowork-idempotency-notification`、
  `wire-13-automation-catalog-schedule-runs-notifications`、
  `acp-v1-reconnect-cancel`、`wire-websocket-auth-slow-reader` 与
  `secret-canary`；退出后无 App/TestClient 进程或临时工作区残留。
- `osx-arm64` App host/DLL SHA-256 为
  `162a965f8f20ad7e6b78e03d2d76c396a6e7ba193c9bb1b69ab06944f36f0212` /
  `cf618e11385870bbdd4c6907d6b25d2b77d155bbfe183de9e1fdf3e64b222011`；
  TestClient host/DLL 为
  `bd55da6603c19010bf549dab4cd6decc03e1d9d67ed7f45b2a54169150927306` /
  `3b40f1d1e59f27874e2bb171151d9ec4b6530579850ad45a5d234c2f691556f6`。
- `win-x64` 交叉发布生成 PE32+ x64 App/TestClient；App host/DLL SHA-256 为
  `8d06d88a485c089007f888de976cfe8646e1972a6848e89463720cb5f3f7c7c0` /
  `797674c0642269e2653c427f8f6e82edceaecec2a9453d1b0c6f2a626155035e`，
  TestClient host/DLL 为
  `2db352fef2e27c40a34a14e2f2682840dd80b4b3356aedc5787db18e4b08dbab` /
  `b2cbd020fb2d1ad35da39bcdd485ab15b8de466c09104c5ed41f39b7970b73c6`。
  该交叉发布结果本身不是 Windows 真机证据。
- Windows 于 2026-08-02 在与 M7 相同的新基线和 Source/Test Patch 上完成
  Release build、全量非显式 Integration 串行回归和发布目录 TestClient；
  `wire-13-automation-catalog-schedule-runs-notifications`、Cron/DST、恢复、Worktree、
  Secret Canary 与残留检查通过。`M8-ACC-003`、`008` 和完整 M8 已关闭。

## M9 双平台验证结果（macOS 2026-08-01；Windows 2026-08-02）

- 代码基线为 `058b505174602653385c51cb35fb654dd0b31262`；验证前工作树干净。
- `dotnet test OpenCoWork.slnx -c Release --no-restore` 为 Architecture `8`、
  Core `391`、Generators `15`、Integration `130`、Protocol `33`，合计
  `577` passed / `0` failed；真实 Provider 显式用例按设计跳过。
- `dotnet build OpenCoWork.slnx -c Release --no-restore` 为 `0` warning /
  `0` error；format 与 diff 门禁通过。该提交的 App、Protocol TestClient 与真实
  Provider Runner 均按 `osx-arm64` 独立 restore/publish；既有 `win-x64` 交叉发布
  仍只算产物生成。
- `osx-arm64` 发布目录 TestClient 返回 `passed: true`，通过
  `wire-stdio-reconnect`、`wire-11-catalog-dynamic-memory-git-terminal-cleanup`、
  `wire-12-cowork-idempotency-notification`、
  `wire-13-automation-catalog-schedule-runs-notifications`、
  `acp-v1-session-load`、`wire-websocket-auth-slow-reader` 与 `secret-canary`。
- `osx-arm64` App host/DLL SHA-256 为
  `162a965f8f20ad7e6b78e03d2d76c396a6e7ba193c9bb1b69ab06944f36f0212` /
  `62aaa95983746f47e68c200cbc585a27f8c348e3a4db85a1d64b0b395dab6b75`；
  TestClient host/DLL 为
  `bd55da6603c19010bf549dab4cd6decc03e1d9d67ed7f45b2a54169150927306` /
  `c9ff53bd78781abad673b9bbbb2131dab4b65f4bf8fd5641226a01186661eb92`；
  Runner host/DLL 为
  `006ccbe2ac0b7576d8034f10d0192a787cb1e8e260f4d84ac2deab361ed9f3fa` /
  `1d0b0a2def9e1a346a79177ed1c7a1ab775ac71261872a808013c66353bafd98`。
- `win-x64` 交叉发布生成 PE32+ x64 App/TestClient；App host/DLL SHA-256 为
  `1fbf2743dea49d121e190a507ad9395fdbd8dc5c88976adff56f87e0f703c5c4` /
  `4a67b0b34a3c96acf0cf7b948a14f3ee751216407432ce1113581b01ba46dbb6`，
  TestClient host/DLL 为
  `2c010be85638a9c6288943bd666504392d24fd38adc3820c068295efad45d26b` /
  `9efb1cc6bf3f173d8df60b943c3c599b6ee574a7d941e4170029b4579bf0fb35`。
- 从 Runner 的 `osx-arm64` 发布目录，以进程级临时凭据和精确 Commit SHA 执行
  `deepseek-v4-flash` 官方 `/v1/responses`。Text、Function、服务端 Web Search、
  `custom/apply_patch`、Usage 与 Secret Canary 六场景全部 `Pass`，终态均为
  `response.completed`；总证据时间为 `2026-08-01T09:33:19.229679Z`。
- 六场景聚合 Usage（Input / Cached / Output / Reasoning / Total）依次为：Text
  `1954/0/21/19/1975`、Function `4144/1920/81/32/4225`、Web Search
  `6207/2304/224/149/6431`、Apply Patch `6613/3200/103/35/6716`、Usage
  `1953/0/16/13/1969`、Secret Canary `1957/0/34/28/1991`。
- Prompt Usage 对账不修改生产 Tokenizer：普通/Function/Apply Patch 使用
  `max(1536, ceil(Provider Prompt × 0.5%))`，服务端搜索使用
  `max(8192, ceil(Provider Prompt × 0.5%))`；边界测试和真实六场景均通过。
- Runner 内部完成全输出面 Secret 扫描与临时 Workspace/User Profile 删除；外部复查
  未发现残留测试目录或 `OpenCoWork.IntegrationTests` / `opencowork` 进程。
  该 macOS 证据已由 Windows 同等场景补齐。
- Windows 发布目录 Runner 以用户级真实 API Key 调用官方 `/v1/responses`；Text、
  Function、服务端 Web Search、`custom/apply_patch`、Usage 与 Secret Canary 六场景
  均为 `completed`，证据时间 `2026-08-02T01:59:52.7843277+00:00`。API Key
  只注入验证子进程，未输出或落盘；`M9-ACC-018`、`019` 和完整 M9 已关闭。

## M10 双平台验证结果（macOS 2026-08-02；Windows 2026-08-02）

- 代码基线为 `9b714bcb7dc0c526a3f7bce1b47f4e6b12d0360f`；验证前工作树干净。
  Outcome 9 全量门禁为 Core `429`、Integration `142`、Protocol `44`、Generators
  `15`、Architecture `8`，合计 `638` passed / `0` failed；Release build 为
  `0` warning / `0` error，format 门禁通过。
- App、Protocol TestClient 与 Integration Runner 已按 `osx-arm64`、`win-x64`
  分别独立 restore/publish。三套 macOS Host 均为 Mach-O arm64，三套 Windows Host
  均为 PE32+ x64；Windows 结果不是 Windows 真机证据。
- 从 Runner 的 `osx-arm64` 发布目录执行 `GatewayOperationsLoadTests`、
  `GatewayOutboxIntegrationTests`、`OperationsCliIntegrationTests` 和
  `RuntimeCompositionIntegrationTests`，共 `13 passed / 0 failed / 0 skipped`，
  xUnit 记录时间 `22.636s`。发布目录 App `--version` 返回 `opencowork 0.1.0`。
- `osx-arm64` App Host/DLL SHA-256 为
  `162a965f8f20ad7e6b78e03d2d76c396a6e7ba193c9bb1b69ab06944f36f0212` /
  `92b7208d61b0124da4e5de5574f965c146be79ca9b6c2655eb241910bf3bff1b`；
  TestClient Host/DLL 为
  `bd55da6603c19010bf549dab4cd6decc03e1d9d67ed7f45b2a54169150927306` /
  `f025087319205b57620a8fadad80d93dfee499d558af41bb09d05e3ddf16c6bf`；
  Runner Host/DLL 为
  `006ccbe2ac0b7576d8034f10d0192a787cb1e8e260f4d84ac2deab361ed9f3fa` /
  `7b293771f82287230b836dfa469b37cab3e2ba7ff404a6ee37fb34e00bb97c77`。
- `win-x64` App Host/DLL SHA-256 为
  `4a023005b69a60b520a74069059aec39edfb88e66fc8a4c7a505b0a6b0861afe` /
  `767885f7f6a5a18b36f153e33ff3ceef80928db70b42f996b3c22cf33ceeb86d`；
  TestClient Host/DLL 为
  `12c3c373d1fd58c2480fb8fefc5f4eb45ad8afcf39c38ce6172984923cde5e72` /
  `2f87113f5dab1850e2fdcd54e47e745a2176f00fa81a799d2f1759c4f6e3b07a`；
  Runner Host/DLL 为
  `d0e5d86e439a9b37fdfa4a4a9cc9eb495234769e1d65e6be158e3449e5d347fe` /
  `6dc386e18e0b60ac7cb1a4b4af33929b9ab0fcaf3ccfdd7e6be7f14633087d88`。
- Runner 使用临时 Workspace/User Profile；测试后对应临时目录为零，调用者用户级
  Workspace Registry SHA-256 前后均为
  `3b984c42b554f73befa318006f1f179712a6f47da78a920de19dbf6d555c0fec`。
  当前 Protocol TestClient 会真实执行 Keychain Set/Clear 并使用调用者用户级 Profile，
  本轮未获授权，故 macOS 未执行。
- Windows 发布目录 TestClient 返回 `passed: true`，通过 Wire 1.0–1.4、ACP v1、
  WebSocket、Credential Manager 与 Secret Canary 共 8 个场景；Runner 的
  Gateway/Outbox/Operations CLI/Runtime Composition 共 `13 passed`。App、TestClient、
  Runner EXE SHA-256 分别为
  `eae4cbaaec249bb7ecea44ebb2f3858e6b5cd4eeb9c26f594ca1bff769d9ce14`、
  `28e3b70ae877dab3a376e337d4f5a9e39c869a85713466a66e2d3dcb921a8d81`、
  `17fa9e53d2c0e3038233856e97efc41c98ea8c13f80396e752d44106bc14febb`。
- macOS 于 2026-08-02 在干净提交
  `050b85c1c42ca2e3bd2abd5eb0943232895081d7` 上复跑 Outcome 10：Core `429`、
  Integration `142`、Protocol `44`、Generators `15`、Architecture `8`，合计
  `638 passed / 0 failed`；Release build 为 `0` warning / `0` error，format 门禁
  通过且未修改文件。
- App、Protocol TestClient 与 Integration Runner 分别独立 restore/publish 为
  `osx-arm64`，三套 Host 均为 Mach-O arm64。App Host/DLL SHA-256 为
  `162a965f8f20ad7e6b78e03d2d76c396a6e7ba193c9bb1b69ab06944f36f0212` /
  `272910bb1efef07332f4eb0b39182d510ad9cad5e1eff7af9786ca366e905c5b`；
  TestClient Host/DLL 为
  `bd55da6603c19010bf549dab4cd6decc03e1d9d67ed7f45b2a54169150927306` /
  `41c3d34a4576f53d8a4930160193ddb2b2641fcc33e79f3690ea95c62bb46433`；
  Runner Host/DLL 为
  `006ccbe2ac0b7576d8034f10d0192a787cb1e8e260f4d84ac2deab361ed9f3fa` /
  `92ad3dc9d0ecb8c291e5590efe5fe275bd9430237567f8592d30b5247d93d079`。
- macOS 发布目录 TestClient 返回 `passed: true`，通过 Wire stdio/reconnect、Wire
  1.1 Capability/Keychain、Wire 1.2 CoWork、Wire 1.3 Automations、Wire 1.4
  Operations/Hub、ACP v1、WebSocket 与 Secret Canary 共 8 个场景；Runner 的
  Gateway/Outbox/Operations CLI/Runtime Composition 为
  `13 passed / 0 failed / 0 skipped`，xUnit 时间 `22.371s`。
- Keychain Set/Clear 使用用户明确授权的真实用户 Profile；TestClient 结束后精确随机
  Account 不存在，临时 Workspace 已删除。调用者 Workspace Registry 清除本轮精确
  临时项后，SHA-256 从验证前到验证后均为
  `f25b0e5dea6023f24eff8a4cbbdbb1e7a958c0d30c5ff10796bc9645b6e43e11`；无
  OpenCoWork 进程、打开句柄或发布验证目录残留。
- `win-x64`、`osx-arm64` 均为 Passed，`M9-ACC-001..010` 已关闭，M10 标记 Done 并
  创建唯一交付归档；M11 仍须在最终发布候选上重跑完整双平台验收。

## 更新规则

- 每次新增真机结果时只追加或替换对应 Slice/平台行，不复制完整测试日志。
- 详细测试映射进入 Slice 计划或唯一交付归档，本台账保留结论、基线和入口。
- 失败必须保留为 `Pending` 并写明失败场景；不得删除失败、降低 Acceptance 或改写
  为交叉发布成功。
- 若验证使用脏工作树，必须同时保存 `git diff --binary` 的 SHA-256；关闭 Slice 前
  应在实际提交 SHA 上复跑关键发布场景。
- 更新本台账后检查 `AGENTS.md` 引用仍有效；只有 Slice 状态变化时才同步里程碑
  CHECKLIST/INDEX。
