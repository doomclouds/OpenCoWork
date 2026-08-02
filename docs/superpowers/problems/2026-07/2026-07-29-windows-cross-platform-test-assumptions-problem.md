# Windows 验证暴露隐藏的平台测试假设

- Date: `2026-07-29`
- Updated: `2026-08-02`
- Topic slug: `windows-cross-platform-test-assumptions`
- Status: `Captured`
- Scope: `Test`
- Tags: `windows`, `macos`, `path`, `junction`, `file-sharing`, `process`, `keychain`, `user-profile`, `cleanup`

## Symptom

Windows 全量验证同时出现三类表象：ACP approval 用例无界等待、外部链接边界测试因
创建 Symlink 权限失败、原子写并发读取偶发返回 `tool.outcomeUnknown`。这些失败会
掩盖真正的平台产品回归，并让测试进程残留。M6 补验又暴露 collectible ALC、
SQLite Pool、Git 只读文件清理失败，以及进程树测试弹出可见 `cmd.exe`。
M7-M10 关闭验证继续暴露媒体暂存文件共享方向、Workspace 发现越过用户目录边界，
以及真实 Provider Secret Canary 扫描活动 SQLite 时的 Windows 共享冲突。
M10 macOS Protocol TestClient 若通过临时 `HOME` 隔离用户 Profile，会弹出“找不到
钥匙串”并提供危险的“还原为默认”操作，随后 `auth/secret/set` 因等待系统交互超时。

## Trigger / Context

- 首次在 `win-x64` 真机运行 M4/M5 全量 Release 测试。
- 测试把 Unix 绝对路径、无特权 Symlink 和 Unix 文件替换共享语义当成跨平台前提。
- M6 在 Windows 真机执行 Plugin 卸载、SQLite 会话清理、临时 Git 仓库删除和
  `Start-Process` 子进程树测试。
- M7-M10 在 Windows 发布目录执行 Gateway 媒体校验、CoWork Git Worktree 清理、
  Workspace Memory 与真实 DeepSeek Provider Runner。
- M10 在 Apple Silicon macOS 发布目录执行 Protocol TestClient 的真实 Keychain
  Set/Use/Clear，并尝试用临时 `HOME` 隔离调用者用户级状态。

## Root Cause

ACP 测试硬编码 `/workspace`，Windows 上
`Path.IsPathFullyQualified("/workspace")` 为 false，`session/new` 先返回
`protocol.workspaceMismatch`，随后测试仍无限等待 permission request。文件边界
测试直接创建目录 Symlink，需要 Windows 开发者模式或管理员权限。原子写读取器只
允许 `FileShare.Read`，阻止 `File.Replace` 删除/替换目标文件，从而把测试自身的
共享冲突误判为写入结果不明。M6 测试还把“方法返回”等同于 ALC 已无栈根、把
`SqliteConnection.Dispose` 等同于全局 Pool 已释放、把 Git Object 当成普通可写文件；
进程树夹具用 `Start-Process` 派生 `cmd.exe` 时也没有显式隐藏窗口。
M10 媒体暂存写流允许其他读取者，却未允许读取者共享现有写入者；Workspace 发现忽略
用户级 `.opencowork` 后仍继续向用户目录之上搜索；测试清理再次漏掉 SQLite Pool 和
Git 只读属性。Provider Runner 的 Secret Canary 又用独占式 `File.ReadAllBytes` 扫描
仍由 SQLite 持有的 `state.db`，使六个真实远端场景均成功后被本地扫描误报失败。
macOS 上 `.NET Environment.SpecialFolder.UserProfile` 仍解析登录用户目录，而 Security
Framework 在临时 `HOME` 下无法定位该登录会话的默认 Keychain；同一进程因此形成
“真实 User Profile + 不存在的默认钥匙串”组合。系统弹窗中的“还原为默认”不是测试
授权，而是修改用户 Keychain 配置的恢复操作，不能作为自动化继续按钮。

## Fix

- ACP 测试改用 `Path.GetTempPath()` 生成平台绝对路径，并让 permission wait 与
  prompt 结果竞速且受 5 秒上限约束，失败时报告已观察协议消息。
- Windows 外部目录链接复用仓库已有的 `cmd.exe /d /c mklink /J` Junction 模式，
  非 Windows 继续使用原生 Symlink，并在递归清理前显式删除链接。
- 并发读取器使用 `FileShare.ReadWrite | FileShare.Delete`，仅对替换窗口中的瞬时
  `IOException` 重试。
- Plugin 卸载断言移入 `NoInlining` helper，退出栈帧后再做有界 GC；SQLite 清理前
  调用 `SqliteConnection.ClearAllPools()`；删除临时 Git 仓库前清除只读属性。
- 需要独立子进程的 Windows 测试为 `Start-Process` 显式传入
  `-WindowStyle Hidden`，避免 Windows Terminal 弹出代理窗口和
  `0x800700e8` 断连提示。
- Gateway 媒体类型校验的读取流使用 `FileShare.ReadWrite`，与既有暂存写流形成双向
  共享；Workspace 发现到达用户目录即停止，不再检查更高层祖先。
- CoWork Git 清理前清除文件只读属性；Workspace Memory 夹具删除目录前清空 SQLite
  Pool；Provider Secret Canary 使用 `FileShare.ReadWrite | FileShare.Delete` 逐块读取。
- macOS OS Secret 真机验收不再通过改写 `HOME` 伪造临时用户。获得用户明确授权后，
  使用登录用户 Profile 和随机 Workspace/Account 执行 Keychain Set/Use/Clear；运行前
  记录 Registry 哈希，运行后精确删除本轮临时 Workspace 项并要求哈希恢复。若出现
  “找不到钥匙串”，选择取消，绝不点击“还原为默认”；随后按精确 Account 确认没有
  Keychain 残留。

## Why This Fix

路径逃逸拒绝和原子替换语义继续保持严格。对测试专属 Pool、只读属性和扫描共享只修
夹具；对媒体暂存共享方向和 Workspace 用户目录边界则修生产根因。这样不会放宽路径、
Secret 或写入权限，也不会给每个调用者复制重试逻辑。
macOS Keychain 验收使用系统真实边界才能证明产品行为；通过随机账户、精确清理和前后
哈希保护用户数据，比改写 `HOME` 或修改默认 Keychain 配置更小、更可审计。

## Recognition Clues

- ACP 测试停在 permission request，先检查更早的 `session/new` 响应是否为
  `protocol.workspaceMismatch`，不要先怀疑审批状态机。
- Windows 出现 “A required privilege is not held by the client” 时，检查测试是否
  把普通开发机当成已启用 Symlink 权限。
- 原子替换只在并发读测试失败且错误为 `tool.outcomeUnknown` 时，先检查读取端的
  `FileShare.Delete`，不要给生产写入增加盲目重试。
- 清理失败集中在 Plugin DLL、`state.db` 或 `.git/objects` 时，分别先检查 ALC
  栈根、SQLite Pool 和只读属性；不要给所有目录删除统一加无限重试。
- 弹窗命令含 `cmd.exe /d /c ping` 时，先搜索测试里的 `Start-Process`，确认
  派生进程是否显式使用 `-WindowStyle Hidden`。
- 媒体校验在 `FileStream` 构造处报共享冲突时，同时核对已有写流和新读流双方的
  `FileShare`，Windows 共享许可必须双向兼容。
- 真实 Provider 所有远端场景成功、最后统一变成 `ExecutionFailed` 时，先单独检查
  Secret Canary 是否正在独占读取活动 `state.db`，不要误判 API Key 或 Provider。
- Workspace 发现返回真实用户目录时，检查祖先搜索是否在显式 User Profile 边界停止。
- macOS Protocol TestClient 停在 `auth/secret/set` 且弹窗包含“找不到钥匙串”或
  “还原为默认”时，先检查是否改写了 `HOME`；取消弹窗并确认精确 Account 不存在，
  不要把它误当成普通 Keychain Access 授权。

## Applicability / Non-Applicability

### Applies When

- 跨平台测试涉及绝对路径、链接、文件替换共享或等待外部协议事件。
- Windows 测试涉及 collectible ALC、SQLite Pool、Git 临时仓库或派生控制台进程。
- Windows 验证涉及活动 SQLite 的 Secret 扫描、写入中的媒体暂存或用户目录祖先搜索。
- Windows 真机失败而相同生产安全语义已被独立测试证明正确。
- macOS 真机验证需要同时触达登录 Keychain 与用户级 Workspace Registry。

### Does Not Apply When

- 生产路径本身接受了根外路径、泄漏 Secret、遗留进程或破坏原子写；这些必须修产品。
- 生产子进程继承了不应共享的 stdin，或用户级状态被错误识别为 Workspace；
  这些属于产品隔离错误，不能归咎于测试夹具。
- 测试需要验证原生 Symlink ACL/UAC 本身；此时不能用 Junction 替代目标场景。
- 已为验证创建独立 macOS 用户和专用 Keychain，并能证明 Search List、默认 Keychain
  与清理边界完全隔离；此时无需复用登录用户 Profile。

## Related Artifacts

- Spec: [M4 Tool Runtime Alpha](../../specs/2026-07-28-open-cowork-m4-tool-runtime-alpha-design.md)
- Plan: [M4 Tool Runtime Alpha 实施计划](../../plans/2026-07-28-open-cowork-m4-tool-runtime-alpha-implementation-plan.md)
- Archive: [M4 Tool Runtime Alpha 交付归档](../../archives/2026-07/2026-07-28-open-cowork-m4-tool-runtime-alpha-archives.md)
- Archive: [M5 Wire Alpha 交付归档](../../archives/2026-07/2026-07-28-open-cowork-m5-wire-alpha-archives.md)
- Archive: [M6 Capability Ecosystem 交付归档](../../archives/2026-07/2026-07-29-open-cowork-m6-capability-ecosystem-archives.md)
- Archive: [M7 Multi-Agent CoWork 交付归档](../../archives/2026-07/2026-07-30-open-cowork-m7-multi-agent-cowork-archives.md)
- Archive: [M8 Automations and Scheduler 交付归档](../../archives/2026-07/2026-07-30-open-cowork-m8-automations-scheduler-archives.md)
- Archive: [M9 DeepSeek Responses Provider 交付归档](../../archives/2026-08/2026-08-01-open-cowork-m9-deepseek-responses-provider-archives.md)
- Archive: [M10 Gateway and Operations 交付归档](../../archives/2026-08/2026-08-01-open-cowork-m10-gateway-operations-archives.md)
- Related Problems:
  - None.
- Code or Test:
  - [CoreToolTests.cs](../../../../tests/OpenCoWork.Core.Tests/CoreToolTests.cs)
  - [AcpConnectionTests.cs](../../../../tests/OpenCoWork.Protocol.Tests/AcpConnectionTests.cs)
  - [CoreToolTests.ShellWeb.cs](../../../../tests/OpenCoWork.Core.Tests/CoreToolTests.ShellWeb.cs)
  - [PluginRuntimeTests.cs](../../../../tests/OpenCoWork.Core.Tests/PluginRuntimeTests.cs)
  - [BackgroundTerminalTests.cs](../../../../tests/OpenCoWork.Core.Tests/BackgroundTerminalTests.cs)
  - [GatewayMediaStore.cs](../../../../src/OpenCoWork.Core/Gateway/GatewayMediaStore.cs)
  - [WorkspacePaths.cs](../../../../src/OpenCoWork.Core/Workspaces/WorkspacePaths.cs)
  - [WorkspaceMemoryTests.cs](../../../../tests/OpenCoWork.Core.Tests/WorkspaceMemoryTests.cs)
  - [CoWorkWorkspaceIntegrationTests.cs](../../../../tests/OpenCoWork.IntegrationTests/CoWorkWorkspaceIntegrationTests.cs)
  - [ProviderReleaseValidationTests.cs](../../../../tests/OpenCoWork.IntegrationTests/ProviderReleaseValidationTests.cs)
  - [OpenCoWork.Protocol.TestClient/Program.cs](../../../../tests/OpenCoWork.Protocol.TestClient/Program.cs)
  - [ProviderSecretStore.cs](../../../../src/OpenCoWork.Core/Capabilities/ProviderSecretStore.cs)
