# ADR-0030: Service level objectives per journey, with provisional targets and burn-rate alerts

- Status: Proposed
- Date: 2026-10-22
- Owners: TenantFlow tech lead (option chosen by the user on 2026-10-22; acceptance pending)
- Related decisions: ADR-0029; Open Decision 9 (performance targets, M11); SPEC-0010 decision 2

## Context
Blueprint section 16 asks for SLOs on representative journeys (authenticated catalog reads, order submission, payment
outcome resolution, background export completion), each with an event, window, success and latency definition,
eligible traffic, target, error budget, alert policy and owner, and keeps synchronous and asynchronous objectives
apart. It also says: "Do not invent SLOs before workload and user journeys are understood." The workload model and
measured performance targets are M11 (Open Decision 9), one milestone later.

## Decision
- One SLO document per journey in `docs/operations/slos/`, using the section 16 fields. Starting set:
  | Journey | Kind | SLI |
  |---|---|---|
  | Catalog read | sync | share of `GET` catalog requests answered non-5xx within a latency threshold |
  | Order submission | sync | share of order `POST` requests answered non-5xx within a latency threshold |
  | Payment outcome resolution | async | share of payments reaching a final state within a time bound of creation |
  | Export completion | async | share of exports finished within a time bound of submission |
  | Event delivery | async (worker) | share of outbox rows published within a time bound, and consumed without dead-lettering |
- 4xx answers are excluded from errors; health probes and platform routes are not eligible traffic.
- Targets set in M10 are labelled **provisional** and replaced by measured values in M11. Windows are 28 days.
- Alerts use multi-window burn rates (fast burn pages, slow burn opens a ticket), computed from recording rules.

Out of scope: per-tenant SLOs, contractual SLAs, and capacity planning.

## Options considered
- **SLIs now, provisional targets, burn-rate alerts (chosen).** The gate needs objectives to diagnose against, and SLIs
  do not depend on load. Risk: a provisional number is treated as final; the label and the M11 revisit guard against it.
- **SLIs and dashboards only; targets and SLO alerts in M11.** Follows the blueprint wording strictly. M10 then alerts
  only on causes (outbox age, dead letters), and the gate's "diagnosed" has no user-facing objective to measure from.
- **Fixed targets now.** Simple, but invents numbers the blueprint warns against.

## Consequences
- Easier: one consistent definition per journey shared by dashboards, alerts and the M11 baseline.
- Harder: asynchronous SLIs need completion-time histograms that do not exist yet (SPEC-0010 slice S2).
- Follow-up: M11 replaces every provisional target and records the change in each SLO document.

## Validation
- Recording rules for each SLI have promtool tests (ADR-0029).
- Each SLO document has every section 16 field; a document test checks the headings.

## Revisit triggers
The M11 baseline; a journey whose SLI does not match what users notice; alert fatigue from burn-rate rules.
