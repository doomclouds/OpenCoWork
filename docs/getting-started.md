# OpenCoWork Runtime 1.0 安装与安全

## 发布包边界

Runtime 1.0 发布候选提供：

- `opencowork-<version>-osx-arm64.tar.gz`
- `opencowork-<version>-win-x64.zip`
- 每个平台对应的 `.spdx` SBOM
- `SHA256SUMS`

这些包是自包含、未签名（Unsigned）的 CLI 包，不包含 TestClient、验证 Runner、PDB、
用户 Workspace、Secret 或用户状态。包内 `release-manifest.json` 记录版本、完整 Git
Commit、RID 和 `unsigned: true`；包内外 SBOM 必须一致。

## 下载后先核验

macOS：

```zsh
expected=$(awk '$2 == "opencowork-1.0.0-rc.1-osx-arm64.tar.gz" {print $1}' SHA256SUMS)
actual=$(shasum -a 256 opencowork-1.0.0-rc.1-osx-arm64.tar.gz | awk '{print $1}')
test -n "$expected" && test "$actual" = "$expected"
```

Windows PowerShell：

```powershell
$expected = (Select-String -Path .\SHA256SUMS -Pattern '  opencowork-1.0.0-rc.1-win-x64.zip$').Line.Split(' ')[0]
$actual = (Get-FileHash .\opencowork-1.0.0-rc.1-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not $expected -or $actual -ne $expected) { throw 'SHA-256 mismatch' }
```

摘要不一致时不要解压或运行。

## macOS 安装、升级与卸载

仅支持 Apple Silicon。解压后在包目录执行：

```zsh
./install.zsh
```

脚本安装到 `$HOME/.local/share/opencowork/bin`，并在 `$HOME/.local/bin/opencowork`
创建精确用户级入口；它不会修改 shell 启动文件。若 `$HOME/.local/bin` 不在 `PATH`，
按脚本提示由用户自行加入。再次运行同一脚本即原位升级；失败会恢复旧安装。

默认卸载：

```zsh
$HOME/.local/share/opencowork/bin/uninstall.zsh
```

默认保留 `$HOME/.opencowork` 和所有 Workspace 数据。只在确实要删除用户数据时运行：

```zsh
$HOME/.local/share/opencowork/bin/uninstall.zsh --purge --confirm-purge
```

脚本会先列出精确目标；缺少二次确认时不做任何删除。不要对真实用户目录做试验性 purge。

## Windows 安装、升级与卸载

解压后在符合本机 PowerShell 策略的终端执行：

```powershell
.\install.ps1
```

脚本安装到 `%LOCALAPPDATA%\OpenCoWork\bin`，只在用户 `PATH` 尚无该精确目录时加入，
并记录该项是否由 OpenCoWork 创建。重复运行完成升级；失败会恢复旧安装和原用户
`PATH`。默认卸载：

```powershell
& "$env:LOCALAPPDATA\OpenCoWork\bin\uninstall.ps1"
```

显式删除 `%USERPROFILE%\.opencowork`：

```powershell
& "$env:LOCALAPPDATA\OpenCoWork\bin\uninstall.ps1" -Purge -ConfirmPurge
```

默认卸载不会删除用户数据或 Workspace。脚本不写管理员目录、不安装服务，也不修改机器
级 `PATH`。

## 未签名安全提示

- Windows 可能显示未知发布者或 SmartScreen 提示；先核验 SHA-256，再由用户对这个精确
  包作本地决定。
- macOS Gatekeeper/Quarantine 可能拦截未签名 CLI；先核验 SHA-256，再由用户对这个
  精确文件作本地决定。
- 本项目不提供关闭 SmartScreen、关闭 Gatekeeper、修改全局安全策略或批量清除隔离
  属性的命令。
- 安装脚本遇到 Reparse Point/Symlink 边界、无关入口、摘要错误或未知目标会拒绝继续。

## 首次运行

```text
opencowork --version
opencowork doctor --json
opencowork init
opencowork chat --provider deepseek --model deepseek-v4-flash
```

真实 DeepSeek 只支持台账中已验证的 `deepseek-v4-flash` Responses 路径。Secret 应通过
受支持的环境/OS Secret 配置进入进程；不要写入 Workspace、命令历史、日志或发布报告。
