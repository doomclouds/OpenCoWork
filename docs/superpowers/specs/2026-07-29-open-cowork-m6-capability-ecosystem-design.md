# OpenCoWork M6 Capability Ecosystem 详细设计

## 文档状态

- 状态：已实现并归档
- 日期：2026-07-29
- 所属里程碑：OpenCoWork Runtime 1.0 / M6
- 已确认决策：43 项
- 对应计划：
  [M6 Capability Ecosystem 实施计划](../plans/2026-07-29-open-cowork-m6-capability-ecosystem-implementation-plan.md)
- 对应归档：
  [M6 Capability Ecosystem 交付归档](../archives/2026-07/2026-07-29-open-cowork-m6-capability-ecosystem-archives.md)
- 继续工作前必须先阅读：
  - [OpenCoWork Runtime 1.0 路线规格](2026-07-25-open-cowork-runtime-1-0-roadmap.md)
  - [M0 Contract Freeze](2026-07-25-open-cowork-m0-contract-freeze-design.md)
  - [M0 能力台账](2026-07-25-open-cowork-m0-capability-ledger.md)
  - [M0-M11 验收目录](2026-07-25-open-cowork-m0-acceptance-catalog.md)
  - [M3 Agent Runtime Alpha 设计](2026-07-27-open-cowork-m3-agent-runtime-alpha-design.md)
  - [M4 Tool Runtime Alpha 设计](2026-07-28-open-cowork-m4-tool-runtime-alpha-design.md)
  - [M5 Wire Alpha 设计](2026-07-28-open-cowork-m5-wire-alpha-design.md)
  - `DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md`

本文冻结 M6 的产品边界、权威关系、安全顺序、外部契约、持久化模型和验收边界。
实现与双平台验证已按对应计划完成。

2026-08-01 前向修订：M6 已交付的 Provider/Model 声明式扩展证据保留，但 1.0
公共 Provider 支持面将在 M9 收敛为 DeepSeek-only Responses API；这不改变 Skills、
Plugins、MCP、LSP、Hooks、Trust 或 Binding Lease 的既有交付语义。

## 1. 目标、范围与既有不变量

M6 交付 Desktop-first 的工作区级 Capability Ecosystem，使 OpenCoWork 可以安全
发现、授信、安装、启动、组合、显式刷新和清理外部能力。

包含：

- Skills、显式 Variant、启停、Prompt 注入和按需加载；
- Plugin Manifest、内容寻址安装、精确 Lock、Trust 和可卸载执行器；
- Provider、Model 和 Auth 的声明式扩展目录；
- MCP Tool、Resource、OAuth、Status 和连接生命周期；
- LSP 只读语言服务；
- Runtime Dynamic Tools 与 Binding Lease；
- Deferred Tool Loading；
- `PreToolUse` 与 `ToolTerminal` Hooks；
- Git 只读 SourceControl；
- Thread 级 Background Terminal；
- Workspace Memory；
- OpenCoWork Wire 1.1 扩展。

M6 必须保持两个既有权威边界：

```text
Session 状态：
ISessionService -> SessionService -> ThreadJournal

工具副作用：
EffectiveToolSnapshot -> ToolInvocationPipeline -> RuntimeBinding
```

Wire 仍是 Protocol Adapter，不成为第二套状态机。Trust 只允许来源激活，不替代
Tool Authority、Policy、Approval、Hook 和结果审计。

## 2. WorkspaceCapabilityRuntime

M6 只增加一个工作区级能力控制面：

```text
WorkspaceRuntime
        │ owns start/stop
        ▼
WorkspaceCapabilityRuntime
  ├─ discovery + trust + overrides
  ├─ plugin installation + lock
  ├─ source lifecycle
  ├─ immutable candidate catalog
  ├─ monotonic revisions
  └─ live binding registry
        │
        ├───────────────► AgentFactory
        │                  ├─ EffectiveSkillSnapshot
        │                  └─ EffectiveToolSnapshot
        │
        └───────────────► ToolInvocationPipeline
                           └─ live Binding / Lease / Trust check
```

规则：

1. Plugin、Skill、Provider、MCP、LSP 和 Hook 来源只发布不可变
   `CapabilityContributionSet`；
2. Runtime 在旁路构建候选 Catalog，完整校验后原子发布；
3. Catalog Revision 单调递增；
4. 相同 Catalog 可以复用上次 Revision；
5. Binding Generation 改变必须生成新 Revision；
6. `AgentFactory` 在 Turn 首次执行时冻结该 Revision 的 Skill 和 Tool Snapshot；
7. 运行中 Turn 不重新扫描 Catalog；
8. Pipeline 每次执行都实时检查 Binding、Lease 和 Trust；
9. Trust 撤销、Lease 过期和断连立即使旧 Binding 失效；
10. M6 不创建第二套 Session、Tool 或 Workspace 生命周期状态机。

### 2.1 Runtime 生命周期

```text
Starting
  └─► Ready | Degraded | Faulted
                    └─► Stopping
                          └─► Stopped
```

- Core 能力和已解析声明先发布；
- Trusted Plugin、MCP 和 LSP 随后各自启动，不阻塞 Desktop 打开；
- 新 Turn 只冻结当时已经 `Ready` 的 Binding；
- `Starting`、`PendingTrust` 和 `Faulted` 项只在 Catalog 可见；
- 单个可选来源失败时只隔离该来源，Runtime 进入 `Degraded`；
- `Disabled` 与 `PendingTrust` 是预期状态，不算降级；
- 只有 Core Catalog 无法建立不变量时 Runtime 才进入 `Faulted` 并拒绝新 Turn；
- M6 不实现后台指数退避；恢复由 Refresh 或显式 Restart 触发。

停止顺序固定为：

```text
拒绝新 Turn 和 Catalog 变更
→ 撤销 Dynamic Binding
→ 取消活动 Capability 调用
→ 逆序停止 Terminal / MCP / LSP / Hook / Plugin
→ Stopped
```

子进程超时后终止整个进程树。无法退出的进程内 `.NET` Plugin 使 Runtime 进入
`Faulted`，由 Desktop 重启恢复。

## 3. Capability Catalog

Catalog 使用分页摘要和单项详情：

```text
CapabilityCatalog
  SchemaVersion
  Revision
  CatalogSha256
  RuntimeState
  Items[]
```

每个摘要项只包含：

```text
Kind
Id
DisplayName
Description
SourceKind
SourceId
SourceVersion
SourceSha256
Status
RequiredTrustScopes
Generation
DiagnosticCodes
```

`Kind` 固定为：

- Plugin
- Skill
- Tool
- Provider
- Model
- AuthProfile
- McpServer
- LspServer
- Hook

`Status` 固定为：

- Ready
- Disabled
- PendingTrust
- Starting
- Authenticating
- Unavailable
- Disconnected
- Faulted
- Conflict

规则：

- `capability/catalog` 分页返回摘要，Cursor 绑定 Revision；
- 翻页期间 Revision 改变时返回 `capability.revisionConflict`；
- `capability/read` 返回单项安全详情；
- Skill Body 只通过 `skill/read` 获取；
- MCP Resource 只通过 `mcp/resource/list|read` 获取；
- Terminal、Memory 和 SourceControl 是 Core 服务，不伪装成外部 Catalog Item；
- 同 Kind/ID 冲突时生成一个不可执行的 Conflict 项并列出冲突来源摘要；
- Catalog Hash 不包含 Secret、时间戳或临时路径；
- Agent 使用内部完整快照，Wire 使用脱敏投影，二者共享同一 Revision。

### 3.1 冲突与候选发布

- Package 或 Manifest 结构错误：整个新插件版本不激活；
- 单个 Skill、Tool、Hook、MCP、LSP、Provider Model 声明无效：只隔离该项；
- 外部项与内置身份或规范名称冲突：保留内置项，隔离外部项；
- 外部来源之间冲突：隔离全部冲突项；
- 不按发现、安装、注册或扫描顺序选择赢家；
- 候选发布失败时保留旧 Catalog 与旧 Revision；
- 发布成功只影响下一 Turn。

## 4. 权威、持久化与恢复

| 能力事实 | 权威 |
| --- | --- |
| Plugin、Skill、Provider、MCP、LSP 定义 | 配置、Manifest、Lock、内容寻址文件 |
| Trust | `~/.opencowork/trust/decisions.json` |
| User/Workspace 启停与 Skill Variant | Capability Override 文件 |
| Turn Skill/Tool Snapshot | ThreadJournal，SQLite 投影 |
| Deferred Tool 激活 | ThreadJournal，SQLite 投影 |
| MCP/LSP/Dynamic Binding、Generation、Lease | 仅内存 |
| Secret | 环境变量或 OS Secret Store |

启动时必须重新校验 Lock、Digest、Trust、Manifest 和配置，然后重新握手所有实时来源。
禁止从 SQLite 恢复 Executor、Delegate、Process、Token、Lease 或 `Ready` 状态。

候选 Catalog 的发布顺序为：

```text
构建候选
→ 提交 Revision 收据
→ 原子发布内存 Catalog
```

提交收据后、内存发布前崩溃只会留下 Revision Gap，不得使用旧可执行 Catalog。
Lock、Digest 或 Trust 不匹配时进入 `PendingTrust` 或 `Faulted`。Memory 的未引用 Blob
只产生诊断，M6 不自动删除。

## 5. Trust 与 Capability Override

### 5.1 Trust Subject

未授信来源可以解析有界声明、计算摘要并展示诊断，但不能：

- 注入 Prompt；
- 启动进程；
- 加载程序集；
- 注册可执行 Tool；
- 执行 Hook；
- 发布实时 Binding。

Trust Subject 绑定：

- 解析符号链接后的规范化 Workspace 绝对路径；
- Source Kind 与 Source ID；
- 精确 Source Version，非版本化文件允许为 `null`；
- 内容 SHA-256；
- 授权 Scope。

Scope 固定为：

- `promptContribution`
- `outOfProcess`
- `inProcessCode`
- `trustedHook`

任一绑定字段变化后旧决定不再匹配。`.NET` 进程内代码仍拥有宿主进程权限；
`AssemblyLoadContext` 不是安全沙箱，Trust UI 必须明确提示。

### 5.2 Trust 文件

固定路径：

```text
~/.opencowork/trust/decisions.json
```

最小结构：

```json
{
  "schemaVersion": 1,
  "decisions": [
    {
      "workspacePath": "/Users/x/project",
      "sourceKind": "plugin",
      "sourceId": "acme/git-tools",
      "sourceVersion": "1.2.3",
      "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "allowedScopes": ["promptContribution"],
      "deniedScopes": ["inProcessCode", "trustedHook"]
    }
  ]
}
```

- `allowedScopes` 与 `deniedScopes` 不得重叠；
- 未出现在两者中的 Scope 为 `PendingTrust`；
- 同一来源允许部分授权；
- Revoke 把 Scope 从允许移动到拒绝并立即撤销 Binding；
- 文件严格解析并原子替换；
- 损坏或不可读时全部外部来源 Fail Closed，Runtime 为 `Degraded`；
- Unix 权限限制为当前用户，Windows 使用当前用户 ACL；
- 不保存 Secret、执行参数或自动删除旧 Subject。

### 5.3 Capability Override

固定路径：

```text
Workspace: .opencowork/capabilities.json
User:      ~/.opencowork/capabilities.json
```

```json
{
  "schemaVersion": 1,
  "disabled": [
    {
      "kind": "skill",
      "id": "acme/review"
    }
  ],
  "skillVariants": [
    {
      "baseId": "acme/review",
      "variantId": "acme/review-strict"
    }
  ]
}
```

- Override 只保存覆盖项，不复制 Catalog；
- 来源自身 `enabled: false` 是基础禁用；
- Override 只能进一步禁用，启用操作等价于删除 Disable 项；
- User Disable 是安全下限，Workspace 不能重新启用；
- Skill Variant 优先级为 Thread Journal、Workspace、User、Base；
- Variant 缺失或不可用时回退下一层并产生诊断；
- Plugin 的 Workspace 启停写入 Lock，User 整体禁用可以写入 Override；
- 未知 Capability ID 可以保留但不生效；
- `capability/setEnabled` 只修改 Override，不是通用执行入口。

## 6. Plugin Package、Manifest 与 Lock

### 6.1 Store 与 Package

用户级内容寻址 Store：

```text
~/.opencowork/plugins/store/<content-sha256>/
  package/
    opencowork.plugin.json
    ...
```

Package 使用 ZIP 容器。HTTPS/Marketplace 先校验下载制品 SHA-256，再解包并计算
规范化内容 SHA-256。Local Install 复制到同卷暂存目录并计算相同摘要。

内容摘要按排序后的以下内容计算：

- NFC 规范化相对路径；
- 可执行位；
- 文件长度；
- 文件内容。

Package 安全限制：

- 压缩包最大 50 MiB；
- 解压后最大 200 MiB；
- 最多 4096 个文件；
- 单文件最大 64 MiB；
- 只允许普通文件和目录；
- 拒绝 Symlink、Hardlink、设备文件和 Reparse Point；
- 拒绝绝对路径、盘符、`..`、反斜杠、NUL、重复 Entry；
- 拒绝 Unicode 和大小写碰撞；
- 暂存与 Store 必须同卷；
- 完整校验后使用原子目录移动；
- 目标摘要已存在时重新校验后复用，不覆盖；
- 启动加载时重新计算 Store 内容摘要；
- 未被 Lock 引用的 Store 内容不自动清理。

HTTPS 重定向只能继续使用 HTTPS；跨 Origin 重定向必须重新确认安装来源。

### 6.2 Manifest

包根目录 Manifest 固定为：

```text
opencowork.plugin.json
```

```json
{
  "schemaVersion": 1,
  "hostApiVersion": 1,
  "id": "acme/git-tools",
  "version": "1.2.3",
  "displayName": "Git Tools",
  "entryPoint": {
    "assembly": "lib/net10.0/Acme.GitTools.dll",
    "type": "Acme.GitTools.Plugin"
  },
  "contributions": {
    "skills": ["skills/review/SKILL.md"],
    "providers": [],
    "authProfiles": [],
    "mcpServers": [],
    "lspServers": [],
    "tools": ["tools/status.json"],
    "hooks": ["hooks/pre-tool.json"]
  }
}
```

- Plugin ID 使用小写 `namespace/name`，`opencowork/*` 保留；
- Version 是精确 SemVer；
- Host API Version 只做整数精确匹配；
- Entry Point 只在贡献 Tool 或 Hook Executor 时存在；
- Contribution 必须显式列出相对路径；
- 禁止通配符、目录扫描、绝对路径、`..` 和 Symlink；
- Trust Scope 由 Core 根据 Contribution 推导；
- Digest 不进入 Manifest；
- 未知字段必须提升 `schemaVersion` 后才能使用；
- 不支持依赖、任意 Wire 扩展、HostedService 或根 DI 配置。

### 6.3 Lock

Workspace Lock 固定为：

```text
.opencowork/plugins.lock.json
```

```json
{
  "schemaVersion": 1,
  "plugins": [
    {
      "id": "acme/git-tools",
      "version": "1.2.3",
      "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "enabled": true
    }
  ]
}
```

- Lock 只记录 Workspace 期望状态；
- 不保存本机源路径、下载地址、时间、展示信息或 Trust；
- 条目按 ID 排序；
- 重复 ID、未知字段或非精确版本拒绝整个新 Lock；
- 安装顺序为暂存、校验、Store、Lock、Catalog；
- 删除 Plugin 只移除 Lock 引用；
- Store 缺失或摘要不符时 Plugin 为 `Faulted`；
- 不静默重新下载或回退其他版本。

### 6.4 进程内 `.NET` Plugin

- Manifest 指定唯一 Entry Type；
- Entry Type 实现窄接口 `IOpenCoWorkPlugin`；
- Plugin 只能绑定 Manifest 已声明的 Tool 和 Hook Executor；
- Plugin 不能修改根 `IServiceCollection`、获取根 `IServiceProvider`、
  注册 HostedService、成为 Primary Module 或注册 Wire Handler；
- Skill、MCP、LSP、Provider 和 Auth 仍为声明式、Core-owned；
- 每个 `pluginId + version + digest` 使用 Collectible `AssemblyLoadContext`；
- `OpenCoWork.Abstractions` 从 Default ALC 共享；
- Plugin 依赖和 Native Library 只能位于 Package Store；
- Turn 持有 `CapabilitySnapshotLease`；
- Update/Remove 后不再发放旧 Revision Lease；
- 旧 Snapshot Lease 与 Active Call 都归零后才停止并卸载旧 ALC；
- 同一 Plugin 不允许两个版本同时激活，短暂能力空窗可接受；
- Trust Revoke 立即 Deny/Cancel；
- 忽略取消的进程内代码无法安全杀线程，Runtime 进入 `Faulted` 并要求 Desktop 重启。

## 7. Skills

### 7.1 布局与格式

```text
Workspace: .opencowork/skills/<folder>/SKILL.md
User:      ~/.opencowork/skills/<folder>/SKILL.md
Plugin:    Manifest 显式列出的包内相对路径
```

```markdown
---
id: acme/review
name: Code Review
description: Review changes for correctness and maintainability.
variantOf: acme/review-base
---

这里是实际 Skill 指令。
```

Frontmatter 只允许单行标量：

- `id`
- `name`
- `description`
- 可选 `variantOf`

规则：

- 不引入完整 YAML 解析器；
- 不支持数组、嵌套、锚点、多行值、模板或 Include；
- Skill ID 使用小写 `namespace/name`，`opencowork/*` 保留；
- 相同 Skill ID 的多个定义全部隔离；
- Variant 只能直接指向非 Variant Base；
- 禁止 Variant Chain 和 Cycle；
- 未知或重复 Frontmatter 字段隔离该 Skill；
- Markdown Body 原样作为 Skill 内容；
- 不递归读取附件或 Supporting Files；
- UTF-8 严格解码，去除 BOM，并把换行规范化为 LF 后计算摘要；
- Standalone Skill 的 Trust Version 为 `null`，Trust Digest 为内容摘要；
- Plugin Skill 继承整个 Plugin Package 的 Trust Subject。

### 7.2 EffectiveSkillSnapshot

Turn 开始时，`AgentFactory` 将以下内容写入 `AgentInvocationSnapshot`：

- 所有已启用 Skill 的 ID、来源和描述；
- 完整 Markdown Body；
- 内容摘要；
- 激活状态与选中 Variant；
- Capability Revision。

限制：

- 单个 Skill 最大 64 KiB；
- 单 Turn Skill Snapshot 最大 1 MiB；
- 超限项隔离，不截断。

Response Prompt 顺序固定为：

```text
OpenCoWork 内置提示
→ Agent Mode
→ AGENTS.md
→ 已激活 Skills（按 SkillId 排序）
→ Skill Catalog（仅 ID 与描述）
→ Runtime Facts
```

未注入 Body 的已启用 Skill 通过只读 Core Tool `skill.load` 从当前 Turn 冻结 Snapshot
按需读取。Compaction Prompt 不注入 Skills。M6 不支持 Skill 依赖或自动语义匹配。

## 8. Tool Ecosystem

### 8.1 Plugin Tool 声明

M6 在现有 `ToolExposure` 增加 `Deferred`。

```json
{
  "id": "status",
  "description": "Read the current repository status.",
  "inputSchema": {
    "type": "object",
    "properties": {},
    "additionalProperties": false
  },
  "effects": ["workspaceRead", "processExecution"],
  "replaySafety": "safe",
  "exposure": "deferred",
  "audience": ["model", "host"],
  "defaultTimeoutMs": 30000,
  "executor": "status"
}
```

- `ToolDefinitionId` 由 Core 生成；
- Tool Namespace 由 Plugin ID 派生，Plugin 不能冒充其他来源；
- Schema、Arguments 和 Result 大小复用 M4 限制；
- Effect 只接受 Workspace Read/Write、Process Execution、Network Read、
  External Mutation；
- Replay Safety 必须显式声明；
- Exposure 只接受 Direct、Deferred、Hidden；
- Audience 复用 Model、Host、App；
- Timeout 仍受 Hook 与 Turn Budget 收紧；
- Executor 必须由唯一 Entry Point 绑定；
- Effect 是审批契约，不是进程内安全沙箱。

### 8.2 Deferred Tools

`EffectiveToolSnapshot` 冻结全部有效 Direct 和 Deferred Definition。Provider 首轮
只收到 Direct Tools 和 Core Tool `tool.search`。

`tool.search`：

- 只搜索当前 Turn 的冻结 Snapshot；
- 使用名称、Namespace 和 Description 的确定性文本匹配；
- 每次最多激活 8 个 Tool；
- 单 Turn 最多累计激活 32 个 Tool；
- 不使用 Embedding、网络搜索或 Provider 私有 API。

成功激活后写入 `DeferredToolsActivated` Journal Fact。下一 Provider Round 使用：

```text
Direct Tools
+ 当前 Turn 已激活的 Deferred Tools
+ tool.search
```

激活集合在 Turn 内只增不减，崩溃恢复从 Journal 重建。激活不跳过实时 Binding、
Lease、Trust、Authority 或 Approval。

### 8.3 Runtime Dynamic Tools

Wire 方法：

- `tool/dynamic/register`
- `tool/dynamic/renew`
- `tool/dynamic/unregister`
- Server-to-client `tool/invoke`

注册绑定：

- 当前 Wire Connection；
- Thread ID；
- 客户端 UUIDv7 Registration ID；
- Tool Definition Digest；
- Runtime Binding ID；
- Lease。

规则：

- Dynamic Trust 只存在内存并绑定当前连接；
- 首次注册为 `PendingTrust`；
- 用户允许当前连接的 `outOfProcess` Scope 后才可进入 Snapshot；
- 同连接、同 Registration ID、同摘要可以安全重试；
- 摘要变化必须使用新 Registration ID；
- Lease 默认 30 秒，最大 5 分钟；
- 续租不能改变 Definition；
- 每个连接每个 Thread 最多 64 个 Dynamic Tool；
- Namespace 由 Core 派生；
- 断线、到期、注销或 Trust Revoke 立即使 Binding 失效；
- 重连必须重新注册、重新信任并获得新 Runtime Binding ID；
- 不持久化 Executor、Lease、连接身份或注册记录。

旧 Snapshot 调用失效 Binding 时返回 `dynamicTool.disconnected` 或
`dynamicTool.leaseExpired`。

### 8.4 Hooks

公共 Hook 仅有：

- `preToolUse`
- `toolTerminal`

工作区文件固定为：

```text
.opencowork/hooks.json
```

Process Hook 示例：

```json
{
  "id": "protect-main",
  "event": "preToolUse",
  "execution": {
    "kind": "process",
    "command": "dotnet",
    "arguments": ["hooks/ProtectMain.dll"],
    "workingDirectory": "workspace",
    "environment": {}
  },
  "timeoutMs": 2000
}
```

Plugin Hook 示例：

```json
{
  "id": "audit",
  "event": "toolTerminal",
  "execution": {
    "kind": "plugin",
    "executor": "audit"
  }
}
```

- Process Hook 每次事件启动一次；
- stdin 输入一个有界 JSON 对象；
- stdout 只读取一个有界 JSON 对象，然后进程退出；
- Pre Hook 可以返回 Allow、Deny、RequireApproval 和可选 Timeout Cap；
- 多 Hook 按稳定 ID 排序并严格求交；
- 不支持 Priority、Dependency 或覆盖；
- Process Hook 需要 `outOfProcess + trustedHook`；
- Plugin Hook 需要 `inProcessCode + trustedHook`；
- Plugin Hook 只能观察同 Plugin Source 的 Tool；
- 只有 Workspace Hook 可以覆盖整个 Workspace；
- Pre 默认 2 秒、最大 10 秒，失败或超时 Fail Closed；
- ToolTerminal 在 Journal Terminal Fact 提交后运行；
- Terminal Hook 失败只产生诊断，不修改已提交结果；
- Hook 可以读取当前 Tool 的有界输入和结果，Trust UI 必须明确数据可见性。

## 9. MCP

### 9.1 Transport 与生命周期

M6 只支持：

- stdio；
- Streamable HTTP。

不支持 Legacy SSE。一个 MCP 定义对应一个工作区级 `McpServerSession`：

```text
PendingTrust
→ Starting
→ Authenticating
→ Ready
→ Disconnected / Faulted
→ Stopped
```

只有 Initialize 和 Capability Discovery 成功后才能发布 Tool 与 Resource。每次连接
生成新的 Connection Generation 与 Runtime Binding ID；断连立即撤销全部 Binding。
旧 Turn 调用返回 `mcp.disconnected`。

`tools/listChanged` 与 `resources/listChanged` 触发候选 Catalog 重建。M6 不后台重试；
重连由 `mcp/restart` 显式触发。

### 9.2 MCP 配置

工作区文件固定为：

```text
.opencowork/mcp.json
```

```json
{
  "schemaVersion": 1,
  "servers": [
    {
      "id": "workspace/local-tools",
      "enabled": true,
      "transport": {
        "kind": "stdio",
        "command": "dotnet",
        "arguments": ["tools/Server.dll"],
        "workingDirectory": "workspace",
        "environment": {
          "LOG_LEVEL": {
            "literal": "warning"
          },
          "API_KEY": {
            "secretRef": "auth/mcp-local"
          }
        }
      }
    },
    {
      "id": "workspace/remote-tools",
      "enabled": true,
      "transport": {
        "kind": "streamableHttp",
        "url": "https://example.com/mcp",
        "authProfileId": "auth/remote-tools"
      }
    }
  ]
}
```

- ID 使用小写 `namespace/name`；
- Stdio 使用 `ProcessStartInfo.ArgumentList`，禁止 Shell；
- Working Directory 只允许 Workspace 或 Plugin Package；
- 含路径分隔符的 Command 按来源根解析，裸命令从受控 PATH 解析；
- Trust 绑定最终解析的可执行文件路径、版本和摘要；
- Environment Value 只能是 Literal 或 Secret Reference；
- HTTP 默认只允许 HTTPS，受信任 Loopback 可以使用 HTTP；
- HTTP Auth 只引用 Auth Profile；
- 配置不能写任意 Authorization Header；
- Timeout 使用 Core 固定上下限；
- Plugin 文件使用单个 Server 对象格式。

M6 只交付 MCP Tool、Resource、OAuth 和 Status；不包含 Prompt、Sampling、Elicitation
或 MCP Apps。

## 10. LSP

一个 LSP 定义对应一个工作区级进程：

```text
PendingTrust
→ Starting
→ Initializing
→ Running
→ Stopping / Faulted
```

工作区配置固定为：

```text
.opencowork/lsp.json
```

```json
{
  "schemaVersion": 1,
  "servers": [
    {
      "id": "workspace/csharp",
      "enabled": true,
      "selectors": [
        {
          "languageId": "csharp",
          "extensions": [".cs", ".csx"]
        }
      ],
      "command": "csharp-ls",
      "arguments": [],
      "workingDirectory": "workspace",
      "environment": {}
    }
  ]
}
```

规则：

- M6 只支持 Stdio LSP；
- Command、Environment、Working Directory 和 Trust 复用 MCP 进程规则；
- Selector 只允许明确的 Language ID 与 Extension；
- 不支持 Glob、Regex 或 Root Discovery；
- Root 固定为当前 Workspace；
- `lsp/request` 必须显式指定 Server ID；
- Selector 只负责文件类型校验和 Catalog 展示；
- 文档内容由 Core 从磁盘读取；
- 不接受编辑器未保存缓冲区；
- Core 使用固定 Client Capability 和白名单；
- 配置不能扩大请求权限；
- 不开放 Initialization Options 或任意 JSON 透传；
- Plugin 文件使用单个 Server 对象格式。

白名单固定为：

- hover
- definition
- references
- documentSymbol
- workspaceSymbol
- diagnostic

禁止 Execute Command、Rename、Formatting、Apply Edit 和其他写操作。所有 File URI
必须经过 Workspace Physical Path Guard。M6 不把 LSP 方法包装成内置模型 Tool。

## 11. Provider、Model、Auth 与 Secret

### 11.1 Auth Profile

工作区配置固定为：

```text
.opencowork/auth.json
```

```json
{
  "schemaVersion": 1,
  "profiles": [
    {
      "id": "auth/deepseek",
      "kind": "apiKey",
      "source": {
        "kind": "environment",
        "name": "DEEPSEEK_API_KEY"
      },
      "placement": {
        "kind": "bearer"
      }
    },
    {
      "id": "auth/private-mcp",
      "kind": "oauth",
      "scopes": ["tools.read"]
    }
  ]
}
```

Auth Kind 固定为：

- None；
- API Key；
- OAuth。

规则：

- API Key 只来自 Environment 或 OS Secret Store；
- Placement 只允许 Bearer 或单个显式 Header；
- OAuth 在 M6 只用于 MCP；
- OAuth Endpoint 由 MCP 协议发现；
- OAuth Token 只进入 OS Secret Store；
- OpenAI-compatible Provider 只使用 None 或 API Key；
- Catalog 只返回 Profile ID、Kind、Source Kind 和 Available；
- `auth/secret/set|clear` 只操作 OS Store；
- Environment 来源只读；
- Secret 在 Turn 执行时解析，不进入 Snapshot。

### 11.2 Provider 与 Model

工作区配置固定为：

```text
.opencowork/providers.json
```

```json
{
  "schemaVersion": 1,
  "providers": [
    {
      "id": "workspace/deepseek",
      "protocol": "openaiCompatible",
      "baseUrl": "https://api.deepseek.com",
      "authProfileId": "auth/deepseek",
      "timeouts": {
        "responseHeaderMs": 30000,
        "streamIdleMs": 60000
      },
      "models": [
        {
          "id": "deepseek-chat",
          "capabilities": ["streaming", "toolCalls", "usage"],
          "tokenizerProfileId": "deepseek-chat",
          "tokenizerProfileVersion": "1",
          "contextWindowTokens": 64000,
          "maxOutputTokens": 8192,
          "tokenizerPath": null,
          "tokenizerSha256": null
        }
      ]
    }
  ]
}
```

- M6 唯一 Provider Protocol 为 `openaiCompatible`；
- 复用 M3 Base URL、Tokenizer、Context Window 和 Output Limit 语义；
- 内置 Provider ID 保持兼容；
- 新 External Provider ID 使用小写 `namespace/name`；
- Model ID 保持供应商精确名称；
- Base URL 只允许 HTTPS，受信任 Loopback 可以使用 HTTP；
- Capability 只接受 Streaming、Tool Calls 和 Usage；
- Custom Tokenizer 必须同时提供相对路径和 SHA-256；
- Timeout 受 Core 上下限约束；
- Invalid Model 单独隔离；
- Provider 无有效 Model 时整体 Faulted；
- Secret 缺失时 Provider 保留在 Catalog，但为 Unavailable；
- 内置 M3 配置优先，External Source 不能覆盖；
- Default Provider/Model 仍由 Core 配置决定；
- Plugin Provider 文件使用单个 Provider 对象格式。

M6 只使用 Fake OpenAI-compatible Provider 验证契约，不新增真实供应商兼容声明。

### 11.3 OS Secret Store

- macOS 使用 Keychain Services；
- Windows 使用 Credential Manager；
- 使用最小 Native Interop；
- 不启动 `security`、PowerShell 或 `cmdkey`；
- Service 固定为 `OpenCoWork`；
- Account 由 Workspace Path Hash 与 Auth Profile ID 组成；
- Secret 只在 Set、Turn 首次使用或 MCP 建立连接时进入内存；
- 同一 Turn 可以持有短生命周期 Secret Lease；
- Turn 结束立即释放；
- 活跃 Secret 值注册到 Secret Redactor；
- Store 失败时 Fail Closed，不创建明文备用文件；
- Linux OS Store 在 M6 为 Unavailable，Environment 仍可使用；
- Plugin、Hook 和 MCP 进程不能直接访问 Store API，只接受声明允许的 Secret 注入。

## 12. SourceControl、Terminal 与 Workspace Memory

### 12.1 SourceControl

只支持 Git：

- status
- diff
- log
- show

不支持 Stage、Commit、Checkout、Reset、Merge、Pull、Push 或 Worktree。

- 使用 `ProcessStartInfo.ArgumentList`；
- 不拼 Shell 命令，不新增 Git SDK；
- Git Executable 首次使用进入 `PendingTrust`；
- Trust 绑定路径、版本和摘要；
- Repository Root 必须等于 Workspace Root；
- Path 参数经过 Path Guard，并使用 Git `--` 分隔；
- 输出、Timeout、Environment 和 Process Tree 复用 Shell 安全约束；
- Model 和 Host 都经过 `ToolInvocationPipeline`；
- Dirty Repository 是正常结果。

### 12.2 Background Terminal

现有 `shell.run` 继续处理一次性命令。M6 新增 Thread-scoped Background Terminal：

- `terminal/start`
- `terminal/list`
- `terminal/read`
- `terminal/write`
- `terminal/stop`
- `terminal/release`

规则：

- 每个 Thread 最多 4 个；
- 每个 Workspace 最多 16 个；
- Start 使用客户端 UUIDv7 Terminal Session ID；
- 同 ID、同 Request Hash 可以重试；
- 同 ID、不同参数返回冲突；
- 每个 Session 使用 1 MiB Ring Buffer 与单调 Offset；
- Reader 落后时返回 Reset Required；
- Start 必须声明最大时长并经过 External Mutation Approval；
- Write 需要独立 Approval；
- Thread Delete/Archive、Runtime Stop 或 Expiry 终止整个进程树；
- Runtime Restart 后旧 Session 标记为 Lost；
- 不按 PID 猜测恢复；
- Release 只清理已经 Stopped、Lost 或自然退出的 Session Metadata，不能脱管
  Running Process；
- 不持久化 Input、Arguments、Environment 或 Output Ring。

M6 不实现 PTY、Resize、ANSI Renderer、Shell History 或跨重启重连。

### 12.3 Workspace Memory

内容使用不可变 Markdown Blob：

```text
.opencowork/runtime/memory/content/<sha256>.md
```

提交顺序：

```text
写临时文件
→ fsync
→ 原子 Rename
→ SQLite 事务更新索引与版本
```

SQLite 失败时保留未引用 Blob，不得先提交指向不存在文件的记录。

方法：

- `memory/list`
- `memory/search`
- `memory/read`
- `memory/write`
- `memory/archive`

规则：

- 不提供物理删除；
- Write 使用 Expected Version；
- Write 经过 Workspace Write Authority、Approval、Secret Detection 和大小限制；
- Body 最大 64 KiB；
- Summary 最大 2 KiB；
- 超限不截断；
- 搜索只匹配规范化 Title、Summary 和 Tags；
- 不使用 FTS、Embedding 或 Vector Database；
- Memory 不自动注入 Prompt；
- 不包含 User-global Memory、Auto Summary 或 Background Reflection。

## 13. Wire 1.1

### 13.1 Protocol 与 Core 边界

Protocol 只依赖 Abstractions 中的窄接口 `ICapabilityService`，由 Core 实现。Protocol
不得直接访问 Plugin Store、SQLite 或 `WorkspaceCapabilityRuntime` 具体实现，也不得
把 Workspace Capability 塞入 `ISessionService`。

Wire Method 由 Core 固定定义。Plugin 不能注册 Wire Method。

统一目录方法：

- `capability/catalog`
- `capability/read`
- `capability/refresh`
- `capability/setEnabled`
- Notification `capability/changed`

状态变化只通知新 Revision，客户端随后重新读取 Catalog。M6 不保存 Capability Event
Stream，也不补发历史事件。

领域操作只保留必要方法：

- `plugin/install|remove|setEnabled`
- `skill/read|selectVariant`
- `trust/decide|revoke`
- `mcp/resource/list|read`
- `mcp/restart`
- `lsp/request|restart`
- `auth/secret/set|clear`
- Dynamic Tool 注册、续租、注销与 `tool/invoke`
- SourceControl、Terminal 与 Memory 的业务方法

M6 不提供万能 `capability/invoke`，也不提供 Marketplace 浏览协议。

### 13.2 并发与重试

- 改变 Catalog 的操作必须携带 Expected Revision；
- 冲突返回 `capability.revisionConflict` 与 Current Revision；
- 操作参数表达最终状态；
- 重复达到相同目标时返回 No-op；
- `capability/refresh` 合并并发刷新；
- MCP/LSP Restart 使用 Expected Generation；
- Terminal 使用客户端 Session ID 与 Request Hash；
- Memory 使用自身 Expected Version；
- Dynamic Tool 使用客户端 Registration ID；
- M6 不增加通用 Capability Idempotency Receipt 表。

### 13.3 Server-to-client Request

M6 只为 Dynamic Tool 增加一个 Server-to-client Method：

```text
tool/invoke
```

- Client 必须在 Initialize 声明 `dynamicToolExecution`；
- Server 使用独立 Request ID；
- Client 返回标准 JSON-RPC Result 或 Error；
- Cancel 沿用 `$/cancelRequest`；
- Disconnect 立即失败全部 Pending Request；
- 不重试，不等待重连；
- Plugin 不能增加其他 Server Request；
- `capability/changed` 等状态变化仍使用 Notification。

### 13.4 Version Negotiation

- Server 支持 Wire `1.1` 与 `1.0`；
- 选择双方共同支持的最高版本；
- Wire 1.0 行为保持不变；
- 1.0 不暴露 M6 Method 或 Server Request；
- M6 Method 的 `Since` 为 `1.1`；
- `tool/invoke` 还要求双方协商 `serverRequests` 与 `dynamicToolExecution`；
- Wire Error 的 Revision/Generation/Version 字段只在 1.1 投影；
- ACP 是独立协议，本次不扩展。

## 14. Error、Diagnostic 与 Redaction

JSON-RPC 标准错误只处理 Protocol Envelope。业务错误继续使用 `WireErrorData`：

- Stable Error Code；
- Core-owned Safe Message；
- Retryable；
- Correlation ID；
- 可选 Current Revision；
- 可选 Current Generation；
- 可选 Current Version。

不开放任意 Details。稳定 Error Code 使用点分命名，例如：

- `capability.revisionConflict`
- `trust.required`
- `plugin.digestMismatch`
- `mcp.disconnected`
- `lsp.unsupportedRequest`
- `dynamicTool.leaseExpired`
- `hook.denied`
- `terminal.lost`
- `memory.versionConflict`

第三方 Exception、Stack Trace、stderr 和原始 Process Error 不直接穿透 Wire。

Redaction 规则：

- 不记录原始 JSON Request；
- Secret、Authorization、API Key、Terminal Input 和 Tool Arguments 默认不落日志；
- 普通路径显示为 `<workspace>`、`<plugin-store>` 和 `<temp>`；
- 只有显式 Trust Subject 可以展示规范化绝对路径；
- Environment Name 可以展示，Value 不可展示；
- Known Secret Value 进入日志前统一脱敏；
- Catalog、Snapshot、Wire、Journal、SQLite 和测试输出不得包含明文 Secret。

## 15. SQLite v5

在现有 v4 迁移框架上只新增五张表：

```text
capability_catalog_state
deferred_tool_activations
workspace_memories
workspace_memory_versions
terminal_sessions
```

### 15.1 capability_catalog_state

固定单行 `id = 1`：

- Last Revision；
- Catalog SHA-256；
- Updated UTC。

只保存发布收据，不保存 Catalog JSON。

### 15.2 deferred_tool_activations

- Thread ID；
- Turn ID；
- Tool Definition ID；
- Activated Sequence；
- Activated UTC；
- Primary Key 为 Turn ID 与 Tool Definition ID。

Journal Fact 是权威，表只是投影。

### 15.3 workspace_memories

- Memory ID；
- Current Version；
- Title；
- Summary；
- Tags；
- Status；
- Normalized Search Text；
- Created/Updated UTC。

### 15.4 workspace_memory_versions

- Memory ID；
- Version；
- Content SHA-256；
- Content Length；
- Created UTC。

保留全部历史 Blob 引用，不物理删除。

### 15.5 terminal_sessions

- Terminal Session ID；
- Thread ID；
- Request SHA-256；
- Status；
- Started/Updated/Ended UTC；
- Exit Code。

不保存 Input、Arguments、Environment 或 Output Ring。启动时把残留 Running Session
原子改为 Lost。

Migration v5 只执行 Create Table/Index，复用现有 Backup、Transaction 和 Fault
Injection。Agent Skill/Tool Snapshot 继续写入 `agent_invocations.snapshot_json`。
不新增 Plugin、Trust、MCP、LSP、Auth、Binding、Lease 或 Executor 表。

## 16. 实施 Outcome

M6 实施拆为 10 个可独立验收的 Outcome：

1. **Capability 基础契约**：Catalog、Revision、状态、Source Contribution、
   Snapshot Lease。
2. **持久化权威**：SQLite v5、Lock、Trust、Override、严格 Schema 和原子写。
3. **Turn 冻结集成**：Agent Skill/Tool Snapshot 与实时 Binding 解析。
4. **Skills 与 Provider/Auth**：Skill、Variant、Provider Catalog、Secret Store
   和 M3 适配。
5. **Plugin Package 与进程内 Executor**：Install、Store、Manifest、
   Collectible ALC、Tool Binding 和 Unload Lease。
6. **Deferred/Dynamic Tools 与 Hooks**：Deferred Exposure、Tool Search、
   Journal Activation、Dynamic Lease 和 Hook。
7. **MCP**：Stdio、Streamable HTTP、OAuth、Tool、Resource 和 Connection
   Generation。
8. **LSP 与 SourceControl**：Read-only LSP Allowlist 与 Git High-level Read。
9. **Background Terminal 与 Workspace Memory**：Bounded Process Session、
   Lost Recovery、Immutable Blob 与 Version Conflict。
10. **Wire 与验收**：Wire 1.1、Server Request、TestClient、Fault Injection
    和双平台证据。

约束：

- 每个 Outcome 都必须保持 `dev` 可构建且现有测试不退化；
- 后一个 Outcome 不提前为未来接口搭空壳；
- Wire 最后连接已稳定的 Core 服务；
- 双平台真机证据只在 Outcome 10 关闭；
- Cross-publish 不能替代目标平台真机。

## 17. 测试、Fault Injection 与发布证据

必须覆盖：

- Catalog Deterministic Hash、Revision、Conflict 和 Concurrent Refresh；
- Package Zip Slip、Symlink、Unicode/Case Collision、Digest Tamper 和 Size Limits；
- Trust Partial Grant、User Disable Floor、Digest Change 和 Immediate Revoke；
- Old Turn Snapshot、Plugin ALC Lease 和 New Turn Binding；
- Skill Order、Variant Fallback 和 Size Limits；
- Deferred Activation Replay、Dynamic Lease 和 Mid-call Disconnect；
- Hook Stable Order、Strict Intersection、Pre Fail Closed 和 Terminal Immutability；
- Fake MCP/LSP Handshake、Capability Change、Reconnect Generation 和 Process Cleanup；
- Fake OpenAI-compatible Provider、Secret Store Fake 和 End-to-end Redaction；
- Temporary Real Git Repository 与 Dirty Workspace；
- Terminal Offset、Limit、Stop 和 Lost；
- Memory Expected Version、Atomic Blob、Orphan Blob、Archive 和 Search；
- Wire 1.0 Regression、1.1 Full Capability、Negotiation、Cancel 和 Disconnect。

固定 Fault Injection：

- Store 已移动但 Lock 未提交；
- Catalog Receipt 已提交但 Memory 未发布；
- Deferred Fact 已写 Journal 但 Projection 未更新；
- Memory Blob 已 Rename 但 SQLite 未提交；
- MCP/Dynamic Tool 在调用中断线；
- Terminal 已启动但 Metadata 提交失败；
- Plugin Trust 在执行中撤销。

双平台发布边界：

- macOS arm64 与 Windows x64 真机均验证 OS Secret Store、Process Tree Cleanup、
  Git Argument Boundary、Terminal、Stdio 和 WebSocket Wire；
- Cross-publish 只记录 Artifact Generation；
- 既有 Windows 台账状态不能自动继承为 M6 Passed；
- M6 必须增加独立 Evidence Row；
- Provider 只做 Fake Protocol Test；
- 除非用户再次显式激活，不新增真实 Provider 验证。

## 18. 明确延期

M6 不包含：

- Plugin Dependency Solver、Auto Update、Signature、Store GC 和 File Hot Reload；
- Marketplace Browse、Rating、Search 和 Recommendation；
- Plugin Root DI、HostedService、Arbitrary Wire Method 和 Native Provider Adapter；
- MCP Legacy SSE、Prompt、Sampling、Elicitation 和 Apps；
- LSP Write、Unsaved Buffer、TCP Transport 和 Built-in Model LSP Tool；
- Hook Long-running Host、Custom Event、Prompt Rewrite 和 Priority System；
- SourceControl Commit、Pull、Push、Checkout、Reset 和 Merge；
- Terminal PTY、Resize、Reconnect 和 Permanent History；
- Memory Auto Injection、User-global Memory、Embedding 和 Physical Delete；
- New Real Provider Claim；
- Teams/Multi-Agent，它属于 M7；
- Linux Release Guarantee；
- ACP Capability Extension；
- DotCraft `.craft`、Plugin Package 或 Private Protocol Compatibility。

## 19. Acceptance 映射与完成定义

| Acceptance ID | 冻结设计输入 |
| --- | --- |
| M6-ACC-001 | §11 Provider/Model/Auth ID、Schema、Secret Store 与脱敏 |
| M6-ACC-002 | §5 Scoped Trust、Trust Subject、Override 与 Revoke |
| M6-ACC-003 | §7 Skill Layout、Variant、Prompt Order 与 Snapshot |
| M6-ACC-004 | §6 Package、Store、Manifest、Lock 与 ALC Unload |
| M6-ACC-005 | §9 MCP Transport、OAuth、Resource 与 Binding Generation |
| M6-ACC-006 | §10 LSP Stdio、Allowlist、Disk Fact 与 Process Cleanup |
| M6-ACC-007 | §8 Deferred/Dynamic Tool、Lease、Journal 与 Disconnect |
| M6-ACC-008 | §3 Conflict、§8 Hook、§14 Stable Error 与 Diagnostic |
| M6-ACC-009 | §12 SourceControl、Terminal 与 Workspace Memory |
| M6-ACC-010 | §2 Atomic Revision、Lifecycle、Degraded 与 Stop |

所有 Acceptance 在实现和证据完成前保持 `Planned`。

M6 完成必须同时满足：

- 10 个 Outcome 全部交付；
- 对应 Fault Injection 通过；
- Wire 1.0 Regression 与 1.1 Black-box TestClient 通过；
- macOS 与 Windows 真机台账都有 M6 独立证据；
- Secret 未进入配置、Journal、SQLite、日志或测试输出；
- Catalog、Snapshot、Trust 和 Live Binding 权威与本文一致；
- Design、Plan、Archive 与 Milestone Ledger 已同步。

若 Windows 证据被用户显式延期，可以归档已经完成的实现，但不得把 Windows 或完整
M6 验收标记为 Passed。
