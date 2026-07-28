# Windows 验证暴露隐藏的平台测试假设

- Date: `2026-07-29`
- Topic slug: `windows-cross-platform-test-assumptions`
- Status: `Captured`
- Scope: `Test`
- Tags: `windows`, `path`, `junction`, `file-sharing`, `protocol`, `hang`

## Symptom

Windows 全量验证同时出现三类表象：ACP approval 用例无界等待、外部链接边界测试因
创建 Symlink 权限失败、原子写并发读取偶发返回 `tool.outcomeUnknown`。这些失败会
掩盖真正的平台产品回归，并让测试进程残留。

## Trigger / Context

- 首次在 `win-x64` 真机运行 M4/M5 全量 Release 测试。
- 测试把 Unix 绝对路径、无特权 Symlink 和 Unix 文件替换共享语义当成跨平台前提。

## Root Cause

ACP 测试硬编码 `/workspace`，Windows 上
`Path.IsPathFullyQualified("/workspace")` 为 false，`session/new` 先返回
`protocol.workspaceMismatch`，随后测试仍无限等待 permission request。文件边界
测试直接创建目录 Symlink，需要 Windows 开发者模式或管理员权限。原子写读取器只
允许 `FileShare.Read`，阻止 `File.Replace` 删除/替换目标文件，从而把测试自身的
共享冲突误判为写入结果不明。

## Fix

- ACP 测试改用 `Path.GetTempPath()` 生成平台绝对路径，并让 permission wait 与
  prompt 结果竞速且受 5 秒上限约束，失败时报告已观察协议消息。
- Windows 外部目录链接复用仓库已有的 `cmd.exe /d /c mklink /J` Junction 模式，
  非 Windows 继续使用原生 Symlink，并在递归清理前显式删除链接。
- 并发读取器使用 `FileShare.ReadWrite | FileShare.Delete`，仅对替换窗口中的瞬时
  `IOException` 重试。

## Why This Fix

生产路径的路径校验、链接逃逸拒绝和原子替换语义都是正确安全边界；放宽生产实现会
制造真实漏洞。修正测试夹具并增加有界失败诊断，既保留产品契约，也让 Windows 与
macOS 验证使用各自真实的平台原语。

## Recognition Clues

- ACP 测试停在 permission request，先检查更早的 `session/new` 响应是否为
  `protocol.workspaceMismatch`，不要先怀疑审批状态机。
- Windows 出现 “A required privilege is not held by the client” 时，检查测试是否
  把普通开发机当成已启用 Symlink 权限。
- 原子替换只在并发读测试失败且错误为 `tool.outcomeUnknown` 时，先检查读取端的
  `FileShare.Delete`，不要给生产写入增加盲目重试。

## Applicability / Non-Applicability

### Applies When

- 跨平台测试涉及绝对路径、链接、文件替换共享或等待外部协议事件。
- Windows 真机失败而相同生产安全语义已被独立测试证明正确。

### Does Not Apply When

- 生产路径本身接受了根外路径、泄漏 Secret、遗留进程或破坏原子写；这些必须修产品。
- 测试需要验证原生 Symlink ACL/UAC 本身；此时不能用 Junction 替代目标场景。

## Related Artifacts

- Spec: [M4 Tool Runtime Alpha](../../specs/2026-07-28-open-cowork-m4-tool-runtime-alpha-design.md)
- Plan: [M4 Tool Runtime Alpha 实施计划](../../plans/2026-07-28-open-cowork-m4-tool-runtime-alpha-implementation-plan.md)
- Archive: [M4 Tool Runtime Alpha 交付归档](../../archives/2026-07/2026-07-28-open-cowork-m4-tool-runtime-alpha-archives.md)
- Archive: [M5 Wire Alpha 交付归档](../../archives/2026-07/2026-07-28-open-cowork-m5-wire-alpha-archives.md)
- Related Problems:
  - None.
- Code or Test:
  - [CoreToolTests.cs](../../../../tests/OpenCoWork.Core.Tests/CoreToolTests.cs)
  - [AcpConnectionTests.cs](../../../../tests/OpenCoWork.Protocol.Tests/AcpConnectionTests.cs)
