# AGENTS

## OpenCoWork 项目上下文

- 当前活动里程碑：
  `docs/milestones/2026-07/open-cowork-runtime-1-0/`
- 里程碑路线规格与 M0 冻结规格统一放在：
  `docs/superpowers/specs/`
- 各 Slice 的独立实现规格、计划和交付归档分别进入
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

## macOS 真机验证台账

- 当前没有可用 Mac；各里程碑允许先依据 Windows 实机、自动测试和
  `osx-arm64` 交叉发布结果收口。交叉发布不记作 macOS 真机证据。
- 后续任何需要 M4 Mac mini 的构建、运行、权限、Symlink、进程、服务、安装或
  性能验证，都必须追加到本章节，不得散落在聊天记录里。
- 每条记录至少写明来源里程碑、状态、目标提交、RID、命令、预期结果和最终证据
  路径。拿到 Mac 后按里程碑顺序批量执行并回填结果。
- 所有 `Pending` 项必须在 M10 / OpenCoWork 1.0 正式发布前清零；未清零时不得
  声称 `osx-arm64` 已通过真实平台发布验收。

### M1 - Runtime Foundation

- Status: `Pending`
- Target: 与最终验证时选定的发布候选提交一致，RID 为 `osx-arm64`。
- Build: `dotnet --info`、restore、Release build、完整 test、framework-dependent
  publish；确认零警告、零错误、零失败和零跳过。
- CLI: 真实运行发布目录中的 `opencowork --version`、`init` 和
  `doctor --json`，验证 SDK/Runtime/RID、带空格路径、用户目录、LF、退出码及
  Doctor 七项检查。
- Filesystem: 验证目录与文件 Symlink 的根内通过、根外拒绝和写前复检。
- Trust: 验证 group/other 可写为 `Failed`、仅可读为 `Warning`。
- Runtime: 验证 SQLite 只读诊断不改写、Secret Canary 不出现在 stdout、
  stderr 或日志中、启动失败逆序回滚、停止超时有界且没有残留进程。
- Evidence: `Pending`；完成后写回对应验收目录或交付归档。

### 后续里程碑

- 新增 macOS 真机验证需求时，在这里按 `### M<N> - <Name>` 新建小节并沿用
  `Status / Target / Checks / Evidence` 结构。

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
