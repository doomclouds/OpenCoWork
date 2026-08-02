# OpenCoWork

OpenCoWork 是面向本地工作区的开源 Agent 协作运行时。Runtime 1.0 提供持久会话、
工具授权链、Plugin/MCP/LSP 能力、自动化、多 Agent CoWork、本地 Gateway/Operations，
以及 Wire 1.4 和 ACP v1 接口。

当前发布候选是 `1.0.0-rc.1`。发布包为自包含、未签名（Unsigned）的
`win-x64` ZIP 与 `osx-arm64` tar.gz；不需要预装 .NET Runtime，也不包含 TestClient、
验证 Runner、签名或公证声明。

## 快速开始

1. 从发布产物中选择与系统一致的包，并按 `SHA256SUMS` 核对 SHA-256。
2. 阅读 [安装、升级、卸载与安全说明](docs/getting-started.md)。
3. 安装后运行 `opencowork --version` 和 `opencowork doctor --json`。
4. 在工作区运行 `opencowork init`，再按需使用 `chat`、`app-server`、`acp`、
   `gateway`、`channel`、`hub` 或 `ops`。

默认卸载保留 `~/.opencowork` 和 Workspace 数据。删除用户数据必须使用安装包中
卸载脚本的显式 purge + 二次确认参数；不要把普通卸载当作数据清理。

## 文档

- [安装与安全](docs/getting-started.md)
- [Wire、ACP、Plugin、MCP 与 LSP](docs/integration-guide.md)
- [Runtime 1.0 Release Notes](docs/release-notes.md)
- [双平台发布验证台账](docs/platform-release-validation-ledger.md)
- [Provider 真实兼容性台账](docs/provider-validation-backlog.md)

许可证见 [LICENSE](LICENSE)。
