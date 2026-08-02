# OpenCoWork Runtime 1.0 Release Notes

## 1.0.0-rc.1

这是 Runtime 1.0 的关闭候选，不增加新的主要子系统。它冻结并组合验证：

- 持久 Session/ThreadJournal、恢复、Archive/Fork/Rollback 和 State v7/v8 到 v9 迁移；
- DeepSeek `deepseek-v4-flash` Responses、工具调用、Web Search 与 Usage 边界；
- Wire 1.4、ACP v1、Plugin 1.0、Workspace MCP/LSP；
- Automations、Multi-Agent CoWork、Gateway、Hub 与 Operations；
- 固定负载、可配置 Soak、资源/残留报告、历史 Corpus 和安全兼容矩阵；
- 未签名自包含 ZIP/tar.gz、用户级安装/升级/卸载、SPDX SBOM 与 SHA-256。

### 发布边界

- 包是 Unsigned：没有 Windows 代码签名、Apple Developer ID 或公证。
- Windows 与 macOS 必须分别在目标真机完成最终包安装、运行、卸载和两小时 Soak；
  Cross-publish 只证明产物可生成。
- 默认测试使用 Fake Provider、仓库 MCP/LSP Fixture、临时 Workspace 和 loopback。
- 真实 Provider、OS Secret、真实用户级安装/PATH 和 purge 均要求显式门禁；出现授权弹窗
  或拒绝时不绕过。
- Runtime 1.0 不承诺第三方 MCP/LSP 普遍兼容，不支持其他 DeepSeek 模型，也不兼容
  DotCraft `.craft` 或私有程序集。

最终 `1.0.0` 只有在同一发布源 Commit 的 macOS 与 Windows 门禁都闭合后才会冻结；在此
之前平台台账中的 M11 状态保持 Pending。
