# ADR-0006: Project-specific Claude Code setup and a two-path, spec-driven workflow

- Status: Accepted
- Date: 2026-10-19
- Owners: TenantFlow tech lead
- Related decisions: ADR-0001, ADR-0004

## Context
TenantFlow is built with Claude Code across many sessions. A new session starts without earlier
conversations, so project rules (tenant isolation, module boundaries, the gate, the milestone
workflow) must be available every time. The source of truth, `PROJECT_BLUEPRINT.md`, lived outside
the repository, where other sessions, CI, and reviewers could not see it.

A general lifecycle plugin, vz-skills 0.2.1, was reviewed as an option. It is well engineered, but it
solves problems TenantFlow does not have: stack discovery, configurable rigor profiles, and routing
for unknown repositories. It also has no multi-tenancy guidance, only shallow .NET support, and
always-on hooks that add context and false-positive risk.

## Decision
1. **Move the blueprint into the repository** as `docs/PROJECT_BLUEPRINT.md`. Changes to it are
   reviewed like code.
2. **Keep a small, project-specific setup**, layered by enforcement strength:
   - Tests, analyzers, and CI enforce rules wherever possible (architecture tests from M3,
     cross-tenant denial tests from M2).
   - `.claude/settings.json` holds shared permission rules: allow the gate's commands, deny reading
     secret files and hand-editing lock files, and ask before pushing, opening PRs, or changing a database.
   - `CLAUDE.md` (under about 100 lines) holds only what cannot be enforced or cheaply inferred: sources
     of truth, the workflow, the gate, non-negotiable rules, and temporary toolchain notes.
   - Three skills hold the repeatable procedures: `spec`, `slice`, and `milestone-gate`.
3. **Two paths.** Work that hits a spec trigger (tenancy, authorization, money, audit, schema,
   contracts, cross-module or messaging work, state machines, concurrency, ADR-worthy, multi-slice)
   gets a one-to-two-page spec in `docs/specs/` that the user approves before implementation. Every
   acceptance criterion maps to a test. Everything else follows a simple path with a one-line
   acceptance criterion. A simple-path task that hits a trigger escalates.
4. **Borrowed from vz-skills, without adopting the plugin:** proving new tests fail without the
   change, evidence-based slice reports, explicit failure behaviour and validation scenarios in each
   skill, and each skill naming the next step.
5. **Reuse built-in Claude Code features** instead of rebuilding them: plan mode, `/code-review`,
   `/security-review`, and commit and PR handling.

Out of scope for now: hooks, custom agents, MCP servers, output styles, and a separate commands
directory. Each needs evidence of a gap before it is added.

## Options considered
- **No setup.** Zero maintenance, but every session re-learns the rules, and nothing reminds it of
  tenant-isolation and boundary rules.
- **Install vz-skills in full.** A mature lifecycle, but about 1,750 lines of generic skill text plus
  hooks that duplicate built-in features and inject context into every session. Rejected.
- **Build a vz-skills-like generic pipeline inside TenantFlow.** Duplicates a general product inside
  a project repository and pulls time away from the product. Rejected.
- **Small project-specific setup (chosen).** About 300 lines, each with a stated purpose.

## Consequences
- New sessions start with the same rules and workflow.
- Spec-path work costs roughly 15 to 30 extra minutes per feature for the spec and its review, in
  return for testable requirements where TenantFlow is most fragile.
- Instruction files can go stale. `/milestone-gate` reviews the setup at every milestone close, and
  unused or wrong parts are removed.

## Validation
- Each milestone review checks that spec-path work had approved specs with every criterion mapped to a test.
- Permission rules are checked by attempting a denied action once after setup.
- At each milestone close, any skill or rule that was not used is a candidate for deletion.

## Revisit triggers
Specs regularly go stale or get skipped. A recurring mistake is not caught by any layer, which
would justify a new rule, hook, skill, or agent. Built-in Claude Code features make a project skill
redundant.
