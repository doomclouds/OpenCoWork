# OpenCoWork M1 Runtime Foundation

- Date: `2026-07-25`
- Topic slug: `open-cowork-m1-runtime-foundation`
- Status: `Archived`
- Scope: `Feature`
- Tags: `runtime`, `CLI`, `SQLite`, `lifecycle`

## Summary

M1 将 OpenCoWork 从契约与工程骨架推进为可构建、可初始化、可诊断并可安全启停的
.NET 10 运行时基础，形成后续 Session、Agent 与 Tool Runtime 可以复用的模块、
配置、状态、路径和生命周期边界。

## Delivered Scope

- 建立七个生产项目、六个测试项目、集中构建规则和 Analyzer-only Generator。
- 完成模块目录、配置快照、Workspace 安全初始化、SQLite 状态基础与结构化日志。
- 完成单一主宿主、失败回滚、有界停止和 `WorkspaceRuntime` 状态机。
- 交付 `opencowork --version`、`init`、只读 `doctor` 及稳定输出和退出码。

## Out of Scope

- Session、Agent、Tool 和 OpenCoWork Wire 业务能力。
- M10 发布候选的安装、升级、卸载、签名、公证和真实模型冒烟。

## Verification Snapshot

- Windows 11 `win-x64`、.NET SDK `10.0.302`、Runtime `10.0.10`。
- Release build 为 `0` warning / `0` error；完整测试 `70` passed /
  `0` failed / `0` skipped。
- `win-x64` 与 `osx-arm64` framework-dependent publish 均成功；后者仅为交叉
  发布证据。
- Windows 发布可执行文件实跑 `--version`、`init`、`doctor --json`，Doctor
  七项检查全部通过，初始化文件使用 LF，SQLite 状态库存在且诊断只读。
- Windows 原生文件及目录 Symlink 使用显式验收开关和临时 UAC 提权专项验证，
  `WorkspacePathTests` 为 `5` passed / `0` failed；同一开关在普通权限下稳定
  返回 `ERROR_PRIVILEGE_NOT_HELD`，证明专项没有退回 Junction。
- Symlink 专项后以普通权限复跑 Release build 与完整测试，结果仍为
  `0` warning / `0` error、`70` passed / `0` failed / `0` skipped。
- `dotnet format --verify-no-changes` 通过，NuGet 全项目传递依赖扫描未发现已知漏洞。
- 2026-07-27 在 Apple Silicon macOS 26.5.2、`osx-arm64`、.NET SDK
  `10.0.302`、Runtime `10.0.10` 上补验当前开发基线。目标为提交
  `7ae53f2de59f4959b2097f1837e28a95d6db81ae` 加 Source Patch SHA-256
  `c2d3a54e9455d16f90db1f5fb21f8923dbb2a120101e773ed54f54335b761010`。
- macOS restore、Release build、完整测试与 framework-dependent publish 均通过；
  构建 `0` warning / `0` error，测试 Core `107`、Integration `15`、
  Generators `14`、Architecture `3`，合计 `139` passed / `0` failed /
  `0` skipped。
- 真机首次暴露两类跨平台回归：Symlink 目标把已解析的 `/private/var` 再引入为
  `/var`，以及 ArchitectureTests 按当前 OS 解释 MSBuild 的反斜杠路径。修复后
  `WorkspacePathTests` 为 `5/5`，项目图聚焦测试为 `1/1`，根外文件/目录链接和
  写前替换链接仍被拒绝。
- `osx-arm64` 发布物确认为 Mach-O arm64，并真实运行 `--version`、带空格路径
  `init` 和 `doctor --json`；Doctor 七项全部 `Passed`，初始化文本为 LF，
  诊断前后 `state.db` SHA-256 一致且未创建日志。
- macOS Trust 权限、Secret Canary、启动回滚、有界停止和无残留进程由本次完整
  Core/Integration 回归及进程检查覆盖。

## Source Documents

- Spec: [M1 Runtime Foundation 设计规格](../../specs/2026-07-25-open-cowork-m1-runtime-foundation-design.md)
- Visual: None found for this topic.
- Plan: [M1 Runtime Foundation 实施计划](../../plans/2026-07-25-open-cowork-m1-runtime-foundation-implementation-plan.md)

## Related Problems

- [SQLite Native Bundle 漏洞依赖](../../problems/2026-07/2026-07-25-sqlite-native-bundle-vulnerability-problem.md)

## Notes

- 2026-07-25 用户确认 M1 先按 Windows 正式证据关闭；未执行的 macOS 真机项不
  计作通过，并统一进入跨里程碑真机验证台账。
- 2026-07-27 M1 当前开发基线的 macOS 真机台账已清零；M10 仍须对最终发布候选
  重新执行完整 `osx-arm64` 发布验收。
