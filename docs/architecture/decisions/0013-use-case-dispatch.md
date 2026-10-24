# ADR-0013: Use-case dispatch through direct application services

- Status: Accepted (2026-10-21 by the user)
- Date: 2026-10-19
- Owners: TenantFlow tech lead (option chosen by the user on 2026-10-19)
- Related decisions: ADR-0002, ADR-0014
- Closes: Open Decision 15 (pulled forward from M3 to M2-S4, the first use case)

## Context
M2-S4 adds the first use cases (tenant lifecycle). Whatever shape it takes becomes the pattern every
module copies. The blueprint lists CQRS and use-case orchestration as Application-layer concerns
(section 7) and asks to earn complexity (`CLAUDE.md`, rule 8). The provisional position was direct
application services unless pipeline behaviours prove their value.

## Decision
- Each module's Application project exposes **plain application services**, for example
  `TenantLifecycleService`, with one method per use case. Endpoints and consumers call them directly
  through dependency injection.
- A use case method owns its transaction boundary, validation (ADR-0014), authorization requirements
  passed in by the caller, and audit, explicitly in its body.
- Application services reach persistence through **narrow, module-specific ports** (for example
  `ITenantStore`), implemented in Infrastructure. No generic repository.
- Expected failures (a missing resource, a state conflict, a broken rule) are raised as `AppException` with a
  catalog error, never as HTTP results or null returns (ADR-0016).
- **The domain decides what a change means; the service keeps the pieces together.** An audit record is built by
  the domain through factories (`TenantAuditRecord.Suspended(...)`), never assembled by hand from an action string
  and two statuses, so it cannot contradict the change it describes. The actor is an `ActorIdentity` value (issuer
  plus subject, never blank, never larger than the columns that store it), shared by every module that audits.
- Read-only queries may be separate methods or classes on the same service; a separate query model is
  introduced only when a read needs it.

Out of scope: a mediator library, pipeline behaviours, and generic command buses.

## Options considered
- **Direct application services (chosen).** Easy to read and debug, no indirection, no dependency.
  Cross-cutting steps are repeated in each use case, so they stay visible but can drift.
- **MediatR with pipeline behaviours.** Uniform hooks for logging, validation, and transactions, but it
  adds indirection, a dependency whose licence changed with version 13, and little value with few use cases.
- **A home-grown command dispatcher.** Same indirection as a mediator, plus code to own.

## Consequences
- Easier: following a request from endpoint to database; testing a use case with real ports.
- Harder: keeping cross-cutting steps consistent; reviews check that each write use case validates,
  audits, and handles concurrency.
- If the same cross-cutting step is repeated in many use cases, extract a small helper first, not a bus.

## Validation
- Code review of each write use case: validation, audit, and concurrency handling are present.
- Architecture tests in M3 check that endpoints depend on Application services, not on Infrastructure.

## Revisit triggers
More than a handful of cross-cutting steps repeated across modules; a need for per-use-case policies
(retries, idempotency) that a pipeline would centralise; measured duplication bugs.
