# OpenCoWork M11 Runtime 1.0 Closure 设计规格

## 文档状态

- 状态：已确认，独立实施计划已落盘
- 日期：2026-08-02
- 修订：2026-08-02，按用户决策将真实 DeepSeek 与 OS Secret 重验移至未来客户端阶段
- 所属里程碑：OpenCoWork Runtime 1.0 / M11
- 当前基线：`dev` 的 `b2eef7fb595eb0fa9d2b50ea8d37cc1f2ff5ef39`
- 目标版本：`1.0.0-rc.1` 至 `1.0.0`
- 正式平台：`win-x64`、`osx-arm64`
- 本文只冻结 M11 设计；实施与提交遵循独立计划和当前会话授权，推送、发布或修改
  用户级状态仍须满足对应权限边界。

继续工作前必须先读取：

- [Runtime 1.0 路线规格](2026-07-25-open-cowork-runtime-1-0-roadmap.md)
- [M0 Contract Freeze](2026-07-25-open-cowork-m0-contract-freeze-design.md)
- [M0 能力台账](2026-07-25-open-cowork-m0-capability-ledger.md)
- [M0-M11 验收目录](2026-07-25-open-cowork-m0-acceptance-catalog.md)
- [双平台真机发布验证台账](../../platform-release-validation-ledger.md)
- [Provider 支持与真实兼容性台账](../../provider-validation-backlog.md)
- [M10 Gateway and Operations 交付归档](../archives/2026-08/2026-08-01-open-cowork-m10-gateway-operations-archives.md)
- [DotCraft 核心运行时复刻规范](../../../DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md)
- [M11 独立实施计划](../plans/2026-08-02-open-cowork-m11-runtime-1-0-closure-implementation-plan.md)

## 1. 目标

M11 不增加大型子系统。它把 M1-M10 已交付的开发基线收敛为可安装、可升级、
可恢复、可诊断并具有双平台真实证据的 OpenCoWork 1.0。

完成时必须同时满足：

- `M10-ACC-001..012` 全部为 `Passed`；
- CAP-001 至 CAP-078 均有通过证据或已冻结的 Removed/Deferred 结论；
- 公共 Wire、DTO、错误码、配置 Schema、默认值和 Plugin Manifest 按 SemVer 冻结；
- 迁移、恢复、安全、兼容、性能、Soak、安装和卸载均有可复现证据；
- `win-x64`、`osx-arm64` 在同一最终提交的各自 RID 发布包上独立通过；
- 没有开放的 P0/P1 缺陷、Secret 命中或无法解释的资源残留。

M11 不再把最终 RC 上的真实 `deepseek-v4-flash` 或用户 OS Secret 交互作为关闭门禁；
M9/M10 的既有真实证据继续有效，客户端具备对应交互后再建立新的真实验收记录。

## 2. 已确认边界

### 2.1 包含

- 能力与验收台账关闭；
- 1.0 公共契约 Golden Snapshot 与 Breaking Change 审查；
- SQLite 旧 Schema 迁移、备份恢复和诊断；
- ThreadJournal 真实历史 Corpus 回放、Checksum 和投影重建；
- Archive/Delete/Fork/Rollback 组合故障恢复；
- Secret、路径、Plugin、Hook、MCP/LSP、工具、媒体和 Worktree 安全矩阵；
- CLI、OpenCoWork Wire、ACP v1、Webhook Gateway、Hub/Operations 与 Fake Provider
  的发布目录 E2E，以及 DeepSeek Responses 的离线契约矩阵；
- 固定负载、双平台性能基线和每平台两小时 Soak；
- 未签名自包含发布包、用户级安装/升级/卸载、Release Notes、SBOM、SHA-256、
  用户文档、协议文档和插件开发文档。

### 2.2 不包含

- 新生产程序集、新 Session/Store/Tool/Hosted Service 框架或平行 AppServer；
- Slack、Teams 等厂商 Channel、真实第三方 Webhook 或远程管理面；
- `deepseek-v4-pro`、其他 Provider 或通用 OpenAI-compatible 路径；
- Linux、Intel macOS、桌面 UI、Web UI、Marketplace UI；
- Windows 代码签名、Apple Developer ID 签名或 macOS Notarization；
- GitHub Release、商店分发、官网托管或自动发布；
- 最终 RC 上的真实 DeepSeek、Keychain 或 Credential Manager 重验；这些交互待客户端
  具备可操作入口后另行激活；
- 为满足验收数量而制造不存在的 Journal Schema；
- 新 Benchmark 框架或独立性能子系统。

签名、公证属于后续分发增强，不是 1.0 `Done` 条件。发布文档必须明确产物未签名，
并说明 Windows SmartScreen 与 macOS Gatekeeper 可能出现的提示；不得关闭或全局绕过
操作系统安全机制。

## 3. 当前基线与真实缺口

- M1-M10 已在平台台账登记双平台真机证据；M10 macOS 基线为 `638 passed / 0
  failed`、Release build `0/0`、Protocol 8 场景和 Runner 13 项。
- 当前生产状态为 State v9、OpenCoWork Wire 1.4、七个冻结生产程序集。
- SQLite 已存在 v6-v9 迁移与故障注入测试，可作为旧 Corpus 收口基础。
- ThreadJournal 自 M2 起只有真实 `schemaVersion: 1`，未知 Schema 会被拒绝；不存在
  可诚实声称的两个旧 Schema。
- Automation 固定负载已有 1,000 个定义和 10,000 个 Run；Gateway/Operations 固定
  负载已有 25,600 条 Inbound、10,000 条 Outbox 和 100,000 个 Trace Span，但当前
  只输出指标，没有冻结发布预算。
- App、Protocol TestClient 和 Integration Runner 已支持 RID 发布目录验证，但没有
  统一的 1.0 打包、安装、升级、卸载、SBOM 和发布清单流程。
- 当前产品版本仍为 `0.1.0`，根 README 尚不足以承担 1.0 用户入口。
- M10 的十项 Gateway 验收已为 Passed；验收目录汇总必须先校正为
  `101 Passed / 12 Planned`。

## 4. 版本与契约冻结

### 4.1 版本流

- 首个集成发布候选为 `1.0.0-rc.1`；
- 只有门禁修复产生后续 `rc.2`、`rc.3`，不预建占位版本；
- 所有门禁在同一最终提交双平台通过后切换为 `1.0.0`；
- M11 只有 `1.0.0` 发布包通过后才可标记 Done；
- 版本、Commit SHA、RID、包摘要、SBOM 和验证报告必须能互相反向定位。

### 4.2 公共契约

1. 生成并审查 Wire 1.4 方法、通知、DTO、枚举、错误码和 Catalog Golden Snapshot。
2. 生成并审查配置 JSON Schema、默认值、Provider/Model 与 Plugin Manifest/Lock 契约。
3. 对比最后一个已交付基线，所有差异必须归类为兼容新增、缺陷修复或明确 Breaking
   Change；未解释差异阻止 RC。
4. 1.0 后的公开 Wire 和 Plugin 契约按 SemVer 管理；内部类型不因本次审查自动转为
   公共 API。

不增加通用兼容层，也不为未来协议版本预建抽象。

## 5. 迁移、Journal 与恢复

### 5.1 SQLite Corpus

- 至少固定 State v7、v8 两套去敏旧数据库 Corpus，迁移目标为当前 State v9；
- Corpus 必须记录来源提交、Schema、文件 SHA-256 和预期业务快照；
- 两平台从原始副本执行迁移，验证外键、完整性、Workspace 身份和查询投影；
- DDL 或 Commit 故障必须保留可诊断失败状态、恢复备份并允许安全重试；
- 测试不得修改仓库内 Corpus 原件。

### 5.2 ThreadJournal v1 冻结

1. 1.0 继续冻结 `schemaVersion: 1`。
2. 从 M2 交付期与后续功能期各固定至少一套去敏真实历史 Journal Corpus，覆盖 Payload
   随功能演进后的兼容读取。
3. 验证规范 JSONL、Sequence、Checksum、UUID、历史回放、投影删除后重建和模型可见
   历史快照。
4. 未知 Schema 继续返回稳定诊断并进入 RecoveryRequired，不猜测、不跳行、不重写。
5. 不制造 v2/v3，也不预建无实际调用者的 Upcaster 框架；未来第一次真实修改 Schema
   时，必须同时交付旧版本 Upcaster 和 Corpus。

### 5.3 组合恢复

Archive、Unarchive、Delete、Fork、Rollback 在以下边界组成最小故障矩阵：Journal
Flush 前后、SQLite Commit 前后、文件移动前后、进程强杀与重启 Reconciler。每个场景
必须证明权威事实、投影、目录、幂等键和外部副作用声明一致；无法自动证明时进入明确
诊断状态，禁止静默修复。

## 6. 安全与兼容矩阵

### 6.1 安全关闭

在既有安全原语上组合复验，不创建第二套扫描器：

- Secret：环境变量、Fake/隔离 Secret Fixture、日志、Journal、SQLite、事件、
  stdout/stderr、包目录和报告的 Canary；
- 路径：Traversal、Symlink、Junction/Reparse Point、大小写与媒体路径包含；
- 工具：Authority、模式、Policy、Hook、Approval、Timeout、Cancellation 与审计顺序；
- 扩展：Plugin 来源/摘要/信任、MCP/LSP 进程和 Binding Lease、断连失效；
- 协作：Artifact、Scratchpad、Managed Worktree、Dirty Retention；
- 安装：只写入精确用户级目标，不接触其他安装或用户数据。

M11 不读取或写入真实用户 Keychain/Credential Manager；OS Secret 的真实交互沿用
M10 已有证据，未来客户端验收不得倒推为本次 RC 的新证据。

### 6.2 支持矩阵

- Provider 只声明 DeepSeek `deepseek-v4-flash` Responses，并以离线官方协议 Fixture
  冻结契约；M11 不重复 M9 的真实 Provider 验收；
- Plugin 只承诺 OpenCoWork 1.0 Manifest、Lock、安装、升级、启停、卸载与故障隔离；
- MCP/LSP 使用仓库内真实子进程 Fixture 验证握手、调用、取消、断连、升级和进程树
  清理，不宣称兼容所有第三方 Server；
- AppServer 指现有 OpenCoWork Wire Host，不新增平行产品或状态机；
- ACP 固定稳定 v1；WebSocket 仍只允许 loopback 和 Bearer Token。

## 7. E2E、性能与 Soak

### 7.1 发布目录 E2E

每个平台从安装后的自包含包运行同一 Workspace 流程：

1. 安装、`--version`、`init`、`doctor --json`；
2. CLI Fake Provider 回合与重启恢复；
3. Wire stdio、loopback WebSocket、ACP v1；
4. Plugin、MCP、LSP、Automation、CoWork、Webhook Gateway、Hub/Operations；
5. 应用升级后复用同一 Workspace，状态、Secret 引用和 Journal 可恢复；
6. 卸载后程序、PATH 项、进程和临时文件清理，用户数据默认保留。

真实 DeepSeek 与 OS Secret 交互不属于 M11 最终包门禁；待客户端可用后，按届时支持面
独立验证 Text、Function、`web_search`、`custom/apply_patch`、Usage 与 Secret Canary。

### 7.2 发布预算

- 复用 Automation 与 Gateway/Operations 现有固定负载；
- 在相同机器、SDK、运行时、文件系统和负载上建立 `rc.1` 双平台基线；
- 后续 RC 的任一可比耗时不得超过冻结基线的 `2×`；正确性、SQLite Busy、超时、
  Secret、资源残留是硬失败，不因耗时达标而放宽；
- 不把单机结果写成对外 SLA。

### 7.3 两小时 Soak

`win-x64` 与 `osx-arm64` 各连续运行两小时：

- 循环 Session、Wire、Automation、CoWork、Gateway 和 MCP/LSP 启停；
- 主循环全程使用 Fake Provider，不读取真实 Provider 或用户 OS Secret；
- 记录阶段性内存、句柄/描述符、线程、子进程、SQLite WAL、错误和完成计数；
- 停止后资源必须回落，不得出现持续单调增长、僵尸进程、活动句柄、临时 Workspace
  或测试 Keychain/Credential 残留；
- 任一崩溃、挂起、数据不一致、P0/P1 或无法解释的增长使对应平台保持 Pending。

## 8. 未签名自包含发布包

### 8.1 产物

- `opencowork-<version>-win-x64.zip`
- `opencowork-<version>-osx-arm64.tar.gz`
- `SHA256SUMS`
- SPDX SBOM
- Release Notes、安装/升级/卸载和安全提示文档

App 使用 `dotnet publish --self-contained true` 生成目标 RID 产物。TestClient 与 Runner
是验证证据，不进入用户发布包。M11 可增加最小发布脚本和测试 Fixture，但不得新增
生产程序集或运行时依赖。

### 8.2 用户级安装语义

- Windows 默认安装到 `%LOCALAPPDATA%\OpenCoWork\bin`；
- macOS 默认安装到 `$HOME/.local/share/opencowork/bin`，命令入口位于用户级
  `$HOME/.local/bin`；
- 安装不要求管理员权限；PATH 变更必须精确、可逆并在日志中说明；
- 升级使用同一用户级目录替换程序文件，不覆盖 `~/.opencowork`、Workspace 数据或
  OS Secret；
- 卸载默认只移除程序与本次精确 PATH/入口项，保留用户数据；
- 删除用户数据必须使用显式 `--purge`，列出精确目标并单独确认；
- 安装/卸载脚本必须支持带空格路径、重复执行和中途失败恢复。

### 8.3 未签名安全提示

- Windows 文档说明未知发布者/SmartScreen 提示及包 SHA-256 核验方式；
- macOS 文档说明 Gatekeeper/Quarantine 可能拦截未签名 CLI，只允许用户对精确下载
  产物作显式本地决定；
- 不提供关闭 SmartScreen、关闭 Gatekeeper、全局修改安全策略或批量清除隔离属性的
  命令；
- 每个包和文档显式标记 `Unsigned`，不得使用 Signed/Notarized 文案。

## 9. 关闭顺序

M11 的实施计划必须按依赖排序，但不在本文写施工步骤：

1. Gate 0：校正台账、冻结 RC 身份和证据目录；
2. 契约 Golden Snapshot；
3. SQLite/Journal Corpus 与组合恢复；
4. 安全和离线 Provider 契约、Plugin/MCP/LSP 兼容矩阵；
5. E2E、固定负载和 Soak Runner；
6. 自包含包、安装/升级/卸载、SBOM、校验和与文档；
7. `rc.1` 双平台真机，按失败形成最小修复循环；
8. `1.0.0` 同一最终提交双平台重跑与交付归档。

后一步不能用前一步的交叉发布、旧提交或脏工作树证据替代。任何真实平台失败都必须
保留为 Pending，不能降低 Acceptance。

## 10. 验收映射

| Acceptance | 本设计关闭面 |
| --- | --- |
| `M10-ACC-001` | 能力台账关闭报告、证据反链和 P0/P1 清零。 |
| `M10-ACC-002` | Wire/DTO/Error/Config/Plugin Golden Snapshot 与 SemVer 审查。 |
| `M10-ACC-003` | State v7/v8 Corpus 到 v9、备份恢复和双平台日志。 |
| `M10-ACC-004` | 两个真实历史 Journal v1 Corpus、Checksum、回放和投影重建。 |
| `M10-ACC-005` | Archive/Delete/Fork/Rollback 组合故障矩阵。 |
| `M10-ACC-006` | 双平台 Threat Matrix、Canary、越界 Corpus 和资源清理。 |
| `M10-ACC-007` | 安装后 CLI/Wire/ACP/Gateway/Fake Provider E2E 与重启恢复。 |
| `M10-ACC-008` | DeepSeek-only 离线契约、Plugin 1.0、仓库 MCP/LSP Fixture 兼容矩阵。 |
| `M10-ACC-009` | 现有固定负载、基线 `2×` 上限和两平台各两小时 Soak。 |
| `M10-ACC-010` | Windows 未签名自包含 ZIP 的安装、升级、离线 E2E、卸载和清理。 |
| `M10-ACC-011` | macOS 未签名自包含 tar.gz 的安装、升级、离线 E2E、卸载和清理。 |
| `M10-ACC-012` | 用户/协议/插件文档、Release Notes、SPDX SBOM 和 SHA-256。 |

## 11. 交付与证据规则

- 按已确认的独立、可恢复 M11 实施计划逐 Outcome 执行；
- M11 测试不得访问真实 Provider、真实第三方服务或用户 OS Secret；
- 未来客户端真实验收必须单独显式激活，Secret 只进入验证进程且不得输出或落盘；
- 用户级 Registry、Keychain/Credential 与 Workspace 的前后状态必须可精确核对；
- 最终报告保留 Commit、环境、命令、摘要、计数、失败与清理结果，不保存 Prompt、
  回答、原始 Provider 响应或 Secret；
- 发布、推送、创建 GitHub Release 或删除用户数据仍需用户单独授权。

## 12. 设计完成条件

本文、路线规格、验收目录、平台台账和里程碑 Checklist 对 M11 边界一致；不存在签名/
公证仍为 1.0 硬门禁、虚构 Journal Schema、额外 Provider/Channel 或新大型子系统的
承诺。达到该条件只代表设计可进入实施计划，不代表 M11 已实现或可发布。
