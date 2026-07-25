# OpenCoWork M1-01 Solution & Build Baseline

- Date: `2026-07-25`
- Topic slug: `open-cowork-m1-01-solution-build-baseline`
- Status: `Archived`
- Scope: `Feature`
- Tags: `.NET 10`, `solution`, `architecture-tests`

## Summary

M1-01 将文档阶段的 OpenCoWork 推进为可恢复、可构建且依赖方向受自动化保护的
.NET 10 工程基线，为 M1 后续运行时基础任务提供稳定编译边界。

## Delivered Scope

- 建立包含七个生产项目和六个测试项目的 `OpenCoWork.slnx`。
- 统一 SDK、构建属性、NuGet 版本及 UTF-8/LF 文本规则。
- 按冻结项目图建立普通引用与 Analyzer-only 引用。
- 使用 xUnit v3 和 BCL `XDocument` 守卫项目结构、依赖及品牌边界。

## Out of Scope

- ModuleRegistry、配置、SQLite、宿主与 Workspace 生命周期等运行时业务实现。
- Generator 的实际扫描、生成和诊断逻辑。
- macOS M4 正式构建证据。

## Verification Snapshot

- `dotnet --version`：`10.0.302`。
- `dotnet restore OpenCoWork.slnx`：通过。
- `dotnet build OpenCoWork.slnx -c Release --no-restore`：0 warnings，0 errors。
- `dotnet test OpenCoWork.slnx -c Release --no-build`：ArchitectureTests 2/2
  通过。
- App Release 产物和 `opencowork.deps.json` 均不包含
  `OpenCoWork.Generators`。

## Source Documents

- Spec:
  [M1-01 设计规格](../../specs/2026-07-25-open-cowork-m1-01-solution-build-baseline-design.md)
- Visual: None found for this topic.
- Plan:
  [M1-01 实施计划](../../plans/2026-07-25-open-cowork-m1-01-solution-build-baseline-implementation-plan.md)

## Related Problems

- None.

## Notes

- `OpenCoWork.Generators` 保持 `netstandard2.0`，其余项目使用 `net10.0`。
- `M1-ACC-001` 继续保持 Planned，macOS M4 证据在 M1 收口时补齐。
