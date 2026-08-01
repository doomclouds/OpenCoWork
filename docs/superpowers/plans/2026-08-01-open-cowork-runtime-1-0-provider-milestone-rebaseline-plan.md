# OpenCoWork Runtime 1.0 Provider 里程碑重排计划

**Goal:** 在不重写既有交付历史和稳定 Acceptance ID 的前提下，新增 M9 DeepSeek Responses Provider，并将 Gateway 与 1.0 Closure 顺延到 M10/M11。

**Why planning is required:** 本次变更会调整 1.0 公共 Provider 支持边界、已冻结路线编号和后续发布验收归属。

**Acceptance:** 里程碑、路线、能力台账、验收目录和 Provider 待验证台账一致表达 DeepSeek-only 目标；M9 首发仅支持 `deepseek-v4-flash`，实现范围以 [DeepSeek 官方 Responses API 指南](https://api-docs.deepseek.com/guides/responses_api/) 的明确支持项为上界，并只取 OpenCoWork 当前需要的最小子集；服务端 `web_search` 和 `custom/apply_patch` 取代本地 `web.fetch` 与模型侧 `file.write`，但本地安全/审计边界保留；`deepseek-v4-pro` 等官方支持后再激活；既有 Acceptance ID 不重排；里程碑校验与文档一致性检查通过。

### Outcome 1: 冻结并同步 M9-M11 新边界

- Work: 更新 Runtime 1.0 README/CHECKLIST/INDEX、路线规格、M0 冻结契约/能力台账/M0-M11 验收目录、受影响 Slice 规格中的 Closure 前向引用、双平台台账和 Provider 待验证台账；保留 M3/M4/M6 历史交付证据，明确 M9 将用 DeepSeek Responses、服务端 `web_search` 和 `custom/apply_patch` 取代通用 Provider、本地 `web.fetch` 与模型侧 `file.write`，但保留本地安全、审批、审计和 Journal 边界；本计划不修改生产代码。
- Risks/open questions: M9 独立设计规格实施前须重新核对 DeepSeek 官方指南与错误码；只冻结当时明确支持且产品需要的字段、事件与工具，不从 OpenAI Responses API 推导未记录能力。Pro 继续以官方支持声明和双平台真实请求为激活门槛。
- Verify: `python /Users/palink/.codex/plugins/cache/codex-plugin/superpowers-asset-compounding/0.5.3/skills/compound-development-asset/scripts/milestone_assets.py . check --json && git diff --check && rg -n "M0-M10|M1-M10|M10 - OpenCoWork 1.0 Closure|M9 - Gateway and Operations" docs/milestones/2026-07/open-cowork-runtime-1-0 docs/superpowers/specs/2026-07-25-open-cowork-runtime-1-0-roadmap.md docs/superpowers/specs/2026-07-25-open-cowork-m0-capability-ledger.md docs/superpowers/specs/2026-07-25-open-cowork-m0-acceptance-catalog.md docs/provider-validation-backlog.md`
- Commit: `docs(milestone): add DeepSeek Responses provider slice`
