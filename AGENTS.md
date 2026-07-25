# AGENTS

## OpenCoWork 项目上下文

- 当前活动里程碑：
  `docs/milestones/2026-07/open-cowork-runtime-1-0/`
- 里程碑总路线规格与里程碑放在一起：
  `docs/milestones/2026-07/open-cowork-runtime-1-0/specs/`
- 各 Slice 的独立实现规格、计划和交付归档仍分别进入
  `docs/superpowers/specs/`、`docs/superpowers/plans/` 和
  `docs/superpowers/archives/`。
- 继续开发前先读取当前里程碑的 `README.md`、`CHECKLIST.md` 和路线规格，
  不得绕过已确认边界直接推进后续 Slice。
- 规划或实现 M0-M10 任一里程碑前，必须读取仓库根目录的
  `DotCraft_Core_核心代码详细设计与一比一复刻规范_v1.0.md`，并结合对应
  Slice 规格核对原始能力边界、状态语义、安全顺序和验收场景。
- 该 DotCraft 规范只作为本机实现证据基线，不属于 OpenCoWork 版本库内容，
  已由 `.gitignore` 排除，任何提交都不得包含它。
- OpenCoWork 不兼容 DotCraft 的 `.craft`、程序集或私有实现；参考规范时应
  保持已确认的 OpenCoWork 品牌、命名和架构决策。如果该规范缺失，先确认
  材料来源，不得凭空补造原实现事实。

<!-- asset-compounding-guidance:start -->
<!-- asset-compounding-guidance:version=0.3.1 -->
## Asset Compounding Retrieval Guide

This repository uses hook-assisted asset compounding from the `superpowers-asset-compounding` plugin. Keep this `AGENTS.md` block as repository-specific retrieval anchors only; generic routing, plan-boundary checkpoints, closeout reminders, and `asset_gate` nudges belong to the plugin hooks and skills.

If the plugin was just installed or upgraded, review and trust the bundled hooks with `/hooks` before relying on lifecycle automation.

### Repository Context Guidance

Keep repository-owned context outside this managed block. Project goals, tech stack, repository boundaries, language rules, runtime commands, validation commands, and the current active milestone belong in the hand-maintained project guidance above or below this block.

This managed block only provides asset retrieval anchors. Guidance scripts may replace the content between the managed markers when the block version is stale, but they must not overwrite project-owned context outside the markers.

### Asset Directories

- Specs: `docs/superpowers/specs/`
- Plans: `docs/superpowers/plans/`
- Archives: `docs/superpowers/archives/`
- Problems: `docs/superpowers/problems/`
- Inbox: `docs/superpowers/inbox/`
- Milestones: `docs/milestones/`
- Technical debt: `docs/technical-debt/`

If one of these directories does not exist, do not assume there is no asset. Search the existing directories first, then inspect current code and tests before guessing.

### Milestone Navigation

Use `docs/milestones/INDEX.md` as the project-level milestone ledger. Milestone documents track target stages, strategic significance, slice boundaries, acceptance signals, progress, and links to completed evidence.

Read the current active milestone before choosing the next slice in a tracked phase. Read completed milestones when reconstructing historical phase evidence. If a task does not belong to the current milestone, decide whether it should become a future milestone slice, a technical-debt record, or ordinary Superpowers spec/plan/archive work before editing the checklist.

After completing, deferring, or splitting a milestone slice, update the milestone `CHECKLIST.md` status/progress and `docs/milestones/INDEX.md` before closeout. Prefer `compound-development-asset/scripts/milestone_assets.py` for script-owned status and progress updates.

`docs/milestones/` does not replace Superpowers specs, plans, archives, problems, or inbox notes. Use milestones to understand roadmap and progress; use `docs/superpowers/` assets for slice design, implementation plans, delivery evidence, and reusable lessons.

### Technical Debt Navigation

Use `docs/technical-debt/INDEX.md` as the project-level technical-debt ledger. Technical-debt records explain why debt exists, how it was discovered, current impact, revisit triggers, resolution criteria, and closure evidence.

Technical debt should inform milestone and slice planning when it affects acceptance, maintainability, or architecture clarity. If debt affects the active milestone's acceptance boundary, use it as slice-selection or spec input instead of mixing the debt record into the milestone checklist.

After resolving, closing, superseding, or intentionally keeping a debt item, update the debt record status, closure/replacement rationale, and `docs/technical-debt/INDEX.md` before closeout. Prefer `compound-development-asset/scripts/technical_debt_assets.py` for script-owned status and index updates.

Do not duplicate reusable failure-mode narratives that belong in `docs/superpowers/problems/`. Link problem assets when debt emerged from a failure, but keep technical-debt records focused on the engineering liability, revisit trigger, and repayment criteria.

### Retrieval Order

When continuing feature work, explaining prior decisions, or checking whether a requirement is already delivered:

1. Search `docs/superpowers/specs/` and `docs/superpowers/plans/` for the intended behavior and implementation plan.
2. Search `docs/superpowers/archives/` for completed delivery history.
3. Search `docs/superpowers/problems/` for stable reusable failure modes, root causes, and recovery rules.
4. Search `docs/superpowers/inbox/` for uncertain but possibly reusable signals.
5. Search `docs/milestones/` for current target stages, slice boundaries, acceptance signals, and progress evidence.
6. Search `docs/technical-debt/` for unresolved engineering liabilities that may affect the next slice or refactor.
7. If no asset answers the question, inspect current code and tests before guessing.

Preferred keyword search:

```powershell
rg -n "<topic-keyword>" docs/superpowers/specs docs/superpowers/plans docs/superpowers/archives docs/superpowers/problems docs/superpowers/inbox docs/milestones docs/technical-debt
```

### Hook-Owned Workflow

- `SessionStart` injects a short asset protocol when `docs/superpowers/` exists.
- `PostToolUse` records compact signals from edits, verification, git closeout commands, and main-agent plan updates.
- `Stop` may request one more pass when meaningful work lacks an `asset_gate`.
- `PreCompact` / `PostCompact` preserve pending asset signals across compaction.

Subagent lifecycle hooks are intentionally not used for asset compounding. The main agent owns final route decisions and repository asset writes. Use the plugin skills and scripts when the hook-provided context indicates an archive, problem, inbox, or update is needed.
<!-- asset-compounding-guidance:end -->
