# ADR-0002: Start as a modular monolith with business-owned modules

- Status: Accepted
- Date: 2026-10-19
- Owners: TenantFlow tech lead
- Related decisions: ADR-0001

## Context
TenantFlow covers many business capabilities: catalog, inventory, orders, payments,
subscriptions, notifications, imports/exports, reporting, audit, and operations. There is one
team, no production traffic yet, and no evidence about which parts need independent scaling or
deployment. Distributing early would add network failure modes, distributed consistency
problems, deployment coordination, and observability cost before any benefit is measurable.

## Decision
- Build **one codebase and one application** organized into **business modules** under
  `src/Modules/<Module>/`. Separate host processes (API, Worker, Migrator) share that codebase.
- Each module **owns its rules and its data**. Other modules interact only through the owner's
  public `Contracts` project: use-case interfaces, query contracts, and integration events.
- No module may reference another module's `Domain`, `Application`, or `Infrastructure`
  project, its `DbContext`, or its tables.
- Module internals follow `Domain` / `Application` / `Infrastructure` / `Contracts`, but empty
  projects are not created just to complete the pattern.
- Shared code lives in small, purpose-named `BuildingBlocks` projects. A generic `Common`
  project is not allowed.
- Modules may share one PostgreSQL cluster at first, with ownership kept explicit. The exact
  mechanism, for example one schema per module, is decided in the persistence ADR in Milestone 3.

Out of scope: extracting any service. Extraction is a Milestone 14 lab and requires measured
evidence of a need for deployment independence, scaling, fault isolation, or separate team ownership.

## Options considered
- **Layered monolith** with one Domain/Application/Infrastructure for everything. Simple to
  start, but boundaries erode quickly and later extraction becomes very expensive.
- **Microservices from day one.** Independent deployment and scaling, but high operational
  cost, distributed consistency problems, and no evidence that it is needed.
- **Modular monolith (chosen).** Local ACID transactions and simple operations now, with
  boundaries strong enough that extraction stays possible.

## Consequences
- Easier: local transactions, debugging, refactoring across modules, and a single pipeline.
- Harder: boundaries are only as strong as their enforcement. A shared process makes it
  tempting to reach into another module's internals.
- Follow-up: architecture tests enforce reference rules once the first two modules exist
  (Milestone 3), and `docs/architecture/dependency-rules.md` documents those rules.

## Validation
- Architecture tests fail the build on forbidden references and on cycles.
- Cross-module workflows use contracts or integration events, which is visible in code review.
- The service-extraction lab in Milestone 14 measures what extracting one module really costs.

## Revisit triggers
A module needs a different scaling profile, release cadence, availability target, or owning
team, backed by measurements rather than preference.
