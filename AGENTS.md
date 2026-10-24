# TenantFlow: instructions for coding agents

A multi-tenant B2B SaaS backend in .NET, built as a modular monolith. It is a learning and portfolio project:
explain architectural reasoning, and leave design decisions to the user rather than making them silently.

## How much process

Risk picks the tier, not size.

- **Just do it.** Bug fixes, small and medium changes, refactors, renames, docs, configuration. Edit, build,
  run the tests you touched, commit. No skill, no spec, no checklist, no report.
- **`/slice`** when the change touches a non-negotiable rule below — tenant isolation or authorization, money,
  a schema or migration, a public API or event contract, the outbox or messaging. A one-line change to a
  tenant-scoped query still belongs here.
- **`/spec`** when the work is large or hard to reverse: more than a few slices, or it needs an ADR. Stop for
  the user's approval before writing code.
- **`/milestone-gate`** to close a milestone, then stop for approval.

One commit per change, when it is done (allowed without asking, 2026-10-22). Never push, tag or open a PR
without asking. A decision under ADR-0001 is drafted `Proposed`; only the user accepts it. Never change a
blueprint requirement silently — raise it as a recommendation.

## The gate

`dotnet test TenantFlow.sln -c Release` — Release build with warnings and formatting as errors, then the
unit, API and architecture tests. This is what CI runs.

The **full gate** adds, with the local stack up:
`dotnet run --project tests/Database/TenantFlow.Database.Tests -c Release` and
`docker compose --profile checks run --rm rule-tests`.

Per change, run the build plus the test projects you touched — once, after the last edit. The full gate,
break-it proofs and reviews run once per milestone and before any push, never per change. Background the
slow ones.

Local stack: `docker compose up -d --wait` (`wsl docker compose` here, `--profile observability` for
telemetry). Smoke tests, drills and security scans live under `tests/` and run at the gate. There are no host
scripts, and nothing reads `deploy/compose/.env` but the tests.

Package versions live only in `Directory.Packages.props`; `dotnet restore TenantFlow.sln` after a change.
No `packages.lock.json` (removed 2026-10-23). An analyzer suppression goes in `.editorconfig` with a reason.

## Non-negotiable rules

1. **Tenant isolation.** Tenant context comes from the authenticated identity and an active membership. A
   tenant id in a route or body selects a tenant; it never grants access. Tenant-owned code fails closed when
   no tenant is set. EF Core query filters are a convenience, not the control. Every tenant-scoped feature
   has a cross-tenant denial test.
2. **Module boundaries.** A module uses another only through its `Contracts` project — never its Domain,
   Application or Infrastructure code, its `DbContext`, or its tables (ADR-0002).
3. **No remote calls inside a database transaction.** Persist the intent, commit, call out, reconcile.
4. **Money** is a decimal amount plus a currency, or documented minor units. Never `float` or `double`.
5. **Async** all the way with `CancellationToken` passed through; no sync-over-async. Instants are UTC and
   time-dependent logic uses `TimeProvider`.
6. **No secrets or personal data** in code, configuration, fixtures, logs, traces or test data.
7. **Schema changes are expand-contract.** Never edit an applied migration; add a new one.
8. **Earn complexity.** No speculative abstractions, no generic repositories, no new dependency or
   infrastructure component without a stated reason, and an ADR when it is stateful.

Architecture, row-level-security, route-table and isolation-matrix tests catch most violations. Never weaken,
skip or delete a failing test to go green — report it.

## Toolchain

.NET 10 SDK 10.0.402 (runtime 10.0.13), pinned in `global.json` with `rollForward: latestPatch` (ADR-0003).
Tests run through VSTest: `IsTestingPlatformApplication` is `false` because the .NET 10 SDK refuses the VSTest
target for a Microsoft.Testing.Platform app (ADR-0038 *Proposed*). xUnit v3 (`xunit.v3` 4.x), with
`using Xunit` declared per test project.

## Where things live

| | |
|---|---|
| Requirements | `docs/PROJECT_BLUEPRINT.md` |
| Milestones, gates, status | `docs/roadmap.md`, `docs/milestones/` |
| Decisions | `docs/architecture/decisions/`, `docs/architecture/open-decisions.md` |
| Specs | `docs/specs/` |
| How code is written | `docs/engineering-conventions.md` |
| Test conventions | `.agents/rules/tests.md` (read before touching `tests/`) |
| Progress record | `docs/progress/progress.json`, built by `tools/TenantFlow.Progress` |
| Skills | `.agents/skills/{slice,spec,milestone-gate}/SKILL.md` |

Skills are in the open [Agent Skills](https://agentskills.io) format. Claude Code exposes them as slash
commands through thin wrappers in `.claude/skills/`; other tools open the files directly. On Windows, Docker
runs inside WSL and fails transiently now and then — retry once before believing it.
