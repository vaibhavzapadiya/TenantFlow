# ADR-0001: Record architecture decisions

- Status: Accepted
- Date: 2026-10-19
- Owners: TenantFlow tech lead
- Related decisions: none

## Context
TenantFlow deliberately defers several large choices (identity provider, broker, cloud,
environment count) until evidence exists. Without a written record, the reasoning behind
both made and deferred decisions is lost, and reviewers cannot tell an intentional
trade-off from an accident.

## Decision
- Architecturally significant decisions are recorded as ADRs in `docs/architecture/decisions/`,
  using `0000-adr-template.md`.
- Files are numbered sequentially (`NNNN-kebab-title.md`) and never renumbered.
- An accepted ADR is immutable except for its status. A changed decision gets a new ADR that
  supersedes the old one, and the two link to each other.
- An ADR is **required** when a change alters a module boundary or dependency rule, adds a
  stateful infrastructure component, changes a public API or event contract policy, changes a
  security or tenancy control, or closes an item in `docs/architecture/open-decisions.md`.
- An ADR is **not** required for routine library patches, refactors inside one module, or
  choices that are cheap to reverse.

Out of scope: heavyweight review boards. The pull request that implements a decision carries
its ADR, and review happens there.

## Options considered
- **No formal records (wiki or chat).** Cheapest, but decisions become unsearchable and drift
  away from the code.
- **ADRs in the repository (chosen).** Versioned with the code, reviewed in the same pull
  request, and readable offline.
- **External architecture tool.** Richer workflow, but adds a system to maintain and separates
  decisions from code history.

## Consequences
Every significant pull request takes a few extra minutes. In exchange, onboarding, interviews,
and future reversals have a clear trail of context, options, and evidence.

## Validation
The pull request template asks whether an ADR is needed. Each milestone review checks that
the open decisions due for that milestone have been closed by an ADR.

## Revisit triggers
ADRs routinely go unread, or the process slows small changes without improving outcomes.
