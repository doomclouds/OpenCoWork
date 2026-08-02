# OpenCoWork Runtime 1.0 集成说明

## CLI 与本地运行边界

`opencowork` 是唯一用户入口。`init` 初始化 Workspace，`doctor --json` 提供稳定诊断
结果，`chat` 运行会话，`gateway`/`channel`/`hub`/`ops` 提供本地 Gateway 与 Operations
能力。所有写操作仍经过既有权限、Policy、Approval、Hook、超时与审计顺序。

## Wire 1.4

`opencowork app-server` 提供本地 Wire 1.4：

- stdio 是默认传输；
- WebSocket 只监听 loopback，必须使用环境注入的 Bearer Token；
- Wire 覆盖 Session、Catalog、CoWork、Automation、Gateway、Hub 和 Operations；
- 帧大小、并发、分页和慢读端均使用冻结上限；协议客户端不能绕过工具授权链。

发布包不携带 Protocol TestClient。台账里的 TestClient 结果是发布验证证据，不是公开
SDK 或兼容性承诺。

## ACP v1

`opencowork acp` 通过 stdio 提供 ACP v1 Bridge。它复用同一 Session/Journal 和工具
Pipeline，不创建平行状态或授权系统。ACP v1 以外的协议扩展不属于 Runtime 1.0。

## Plugin 1.0

Plugin 包使用 ZIP 和根清单 `opencowork.plugin.json`。清单冻结为 `schemaVersion: 1`、
`hostApiVersion: 1`，并声明稳定 ID、版本和 contributions。程序集入口必须实现
`OpenCoWork.Abstractions.IOpenCoWorkPlugin`。安装、升级、启用、禁用和删除都会刷新
Catalog；新摘要必须重新经过 Trust 决策。

Runtime 不加载 DotCraft `.craft`、私有程序集或兼容别名。Plugin 不得把 Trust、Hook、
Policy 或 Approval 当作可选步骤。

## Workspace MCP

Workspace MCP 配置位于 `.opencowork/mcp.json`。最小结构是：

```json
{
  "schemaVersion": 1,
  "servers": [{
    "id": "workspace/example",
    "enabled": true,
    "transport": {
      "kind": "streamableHttp",
      "url": "https://example.invalid/mcp",
      "authProfileId": null
    }
  }]
}
```

进程型/网络型 MCP 必须满足对应 Trust 与网络安全策略。Runtime 1.0 的发布声明只覆盖仓库
Fixture 和台账中明确登记的真实兼容性，不能从共享协议推导第三方 Server 必然兼容。

## Workspace LSP

Workspace LSP 配置位于 `.opencowork/lsp.json`。最小结构是：

```json
{
  "schemaVersion": 1,
  "servers": [{
    "id": "workspace/csharp",
    "enabled": true,
    "selectors": [{ "languageId": "csharp", "extensions": [".cs"] }],
    "command": "your-language-server",
    "arguments": [],
    "workingDirectory": "workspace",
    "environment": {}
  }]
}
```

LSP 进程受工作区边界、Trust、请求 Allowlist、输出上限、取消和重启代际约束。Runtime
拒绝工作区外 URI；发布声明同样只覆盖仓库 Fixture 和台账中的明确证据。
