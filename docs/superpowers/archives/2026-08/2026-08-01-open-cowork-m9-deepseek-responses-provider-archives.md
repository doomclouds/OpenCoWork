# OpenCoWork M9 DeepSeek Responses Provider

- Date: `2026-08-01`
- Topic slug: `open-cowork-m9-deepseek-responses-provider`
- Status: `Archived`
- Scope: `Feature`
- Tags: `deepseek`, `responses-api`, `provider`, `usage`, `security`

## Summary

M9 用 DeepSeek 官方 Responses API 的专用最小实现替换通用 OpenAI-compatible
Chat Completions 路径，并完成 `deepseek-v4-flash` 双平台真实兼容性验收。

## Delivered Scope

- 交付文本、Reasoning、Function 多轮循环、服务端 `web_search`、
  `custom/apply_patch`、Usage 与三类终态。
- 保持 ThreadJournal、Tool Pipeline、Approval、Authority、审计和无状态恢复为本地权威。
- 删除千问 Token Plan、通用 Provider SPI、本地 `web.fetch` 与模型侧 `file.write`。
- `win-x64` 与 `osx-arm64` 发布目录均通过真实 DeepSeek 六场景。

## Out of Scope

- `deepseek-v4-pro`、其他 Provider、Chat Completions、结构化输出和服务端持久会话。
- 未经独立官方支持确认与双平台验证的兼容性推断。

## Verification Snapshot

- Apple Silicon macOS 基线 `058b505174602653385c51cb35fb654dd0b31262`：
  `577` 项离线回归、Release build `0` warning / `0` error、Protocol TestClient 和
  `deepseek-v4-flash` `/v1/responses` 六场景通过。
- Windows 11 Home `10.0.26200` x64 基线
  `2d966400e61e8d17c8a513299e8a9b420591d865` 加 Source/Test Patch SHA-256
  `516c263191620d8b9f41eb5bbce0436aac41ee04aef6be73af5c5514783e90cd`：
  发布目录 Runner 以用户级真实 API Key 执行 Text、Function、`web_search`、
  `custom/apply_patch`、Usage 与 Secret Canary，六场景均 `completed`；证据时间
  `2026-08-02T01:59:52.7843277+00:00`。
- Windows 六场景 Usage（Input/Cached/Output/Reasoning/Total）依次为 Text
  `1956/0/18/16/1974`、Function `4146/1920/82/33/4228`、Web Search
  `5606/2304/141/77/5747`、Apply Patch `6609/3200/97/29/6706`、Usage
  `1949/0/14/11/1963`、Secret Canary `1957/0/24/18/1981`。
- `M9-ACC-011` 至 `M9-ACC-019` 全部为 `Passed`；M11 仍须在最终发布候选上复验。

## Source Documents

- Spec: [M9 DeepSeek Responses Provider 设计](../../specs/2026-08-01-open-cowork-m9-deepseek-responses-provider-design.md)
- Visual: None found for this topic.
- Plan: [M9 DeepSeek Responses Provider 实施计划](../../plans/2026-08-01-open-cowork-m9-deepseek-responses-provider-implementation-plan.md)

## Related Problems

- [Windows 验证暴露隐藏的平台测试假设](../../problems/2026-07/2026-07-29-windows-cross-platform-test-assumptions-problem.md)

## Notes

- Provider 支持声明见 [Provider 真实兼容性台账](../../../provider-validation-backlog.md)，
  双平台发布证据见 [平台发布验证台账](../../../platform-release-validation-ledger.md)。
