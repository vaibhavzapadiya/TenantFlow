# TenantFlow implementation roadmap

This is the prioritized execution plan derived from `PROJECT_BLUEPRINT.md`, which remains the
source of truth. Milestones run strictly in order. Each one ends at a gate, and the next starts
only after the gate is demonstrated and reviewed.

**Primary path:** modular monolith → tenant/security boundary → reliable transactional
workflows → outbox and idempotent messaging → production observability → measured performance
and resilience → safe cloud delivery → proven recovery → selective labs.

## Status

| Milestone | Priority | Status |
|-----------|----------|--------|
| M0 Engineering foundation | P0 | Gate verified; on .NET 10 since 2026-10-24 (SPEC-0015); waiting for your review |
| M1 Local platform | P0 | Gate met 2026-10-19; waiting for your review ([guide](milestones/M1-local-platform.md)) |
| M2 Identity and tenant boundary | P0 | Gate met 2026-10-21; waiting for your review ([guide](milestones/M2-identity-and-tenant-boundary.md)) |
| M3 First vertical slice: catalog and inventory | P1 | Gate met 2026-10-21 (ADR-0019 accepted); waiting for your review ([guide](milestones/M3-catalog-and-inventory.md)) |
| M4 Order and inventory consistency | P1 | Gate met 2026-10-21 (ADR-0020 accepted); waiting for your review ([guide](milestones/M4-order-and-inventory-consistency.md)) |
| M5 Messaging foundation | P1 | Gate met 2026-10-21 (ADR-0021 accepted); waiting for your review ([guide](milestones/M5-messaging-foundation.md)) |
| M6 Payment workflow | P1 | Done (gate met 2026-10-21; merged) ([guide](milestones/M6-payment-workflow.md)) |
| M7 Subscriptions and refunds | P2 | Gate met 2026-10-21 (ADR-0023 accepted); waiting for your review ([guide](milestones/M7-subscriptions-and-refunds.md)) |
| M8 Async files and reporting | P2/P3 | Done (gate met 2026-10-22) |
| M9 Security and privacy | P2 | Gate met 2026-10-22 (ADR-0025 to 0028 Proposed); merged ([guide](milestones/M9-security-and-privacy.md)) |
| M10 Production observability | P3 | Gate met 2026-10-22 (ADR-0029 to 0031 Proposed); waiting for your review ([guide](milestones/M10-production-observability.md)) |
| M11 Performance and resilience | P3 | Planned; deferred until after M12 by your decision (sequencing note 5) |
| M12 Cloud deployment | P3 | In progress (runs before M11; sequencing note 5) |
| M13 Disaster recovery | P3 | Planned |
| M14 Advanced labs | Labs | Planned |

## Milestones

Each milestone lists its slices in implementation order, the decisions that must be closed
first, and the gate copied from blueprint section 20.

### M0 — Engineering foundation (P0)
- **Slices:** pinned SDK and central packages → analyzers and formatting → API host shell with a
  host-composition test → CI skeleton and local verify script → ADR process, template, and first ADRs.
- **Decisions closed:** Open Decision 10 (ADR-0003). ADR-0001, 0002, and 0004 were recorded.
  ADR-0005 runs the build on .NET 9 until the .NET 10 SDK is installed (deadline 2026-11-10).
- **Gate:** a clean checkout builds; formatting and analyzer checks and a sample test pass.
- **Guide:** [milestones/M0-engineering-foundation.md](milestones/M0-engineering-foundation.md)

### M1 — Local platform (P0)
- **Slices:**
  1. Docker Compose with PostgreSQL, one start command, one reset command, health checks, and a
     git-ignored `.env`.
  2. Worker host sharing the build policy (the Migrator host moves to M3).
  3. Liveness and readiness endpoints, plus options validation that fails startup on bad configuration.
     A separate startup endpoint arrives in M3, once asynchronous initialization exists.
  4. Structured logging and OpenTelemetry traces, metrics, and logs to an OpenTelemetry Collector.
  5. Opt-in observability profile with Prometheus, Grafana, Loki, and Tempo, with log-to-trace
     correlation demonstrated for both hosts.
- **Decided:** Open Decisions 13 (ADR-0007) and 14 (ADR-0008).
- **Gate:** one documented command starts the dependencies; API and worker emit correlated telemetry.
- **Gate interpretation (approved by the user, 2026-10-19):** M1 proves log-to-trace correlation within
  each host and shared resource attributes across both hosts. A trace that spans both processes arrives
  with the broker in M5, instead of an artificial worker-to-API call now.
- **Moved to M3 (approved):** the Migrator host, created together with EF Core; and Testcontainers,
  after Open Decision 21 settles how local tests reach Docker.
- **Prerequisite:** a Docker engine with Compose v2. On the current workstation, Docker 28.3 and
  Compose v2.39 run inside WSL (Ubuntu-22.04), so scripts must also work through `wsl docker compose`.

### M2 — Identity and tenant boundary (P0)
- **Slices:** EF Core and migrations run through the Migrator (moved from M3, see below) → OIDC JWT
  validation (issuer, audience, signature, lifetime) → Keycloak for local development → Tenants module
  (tenant lifecycle and status) → IdentityAccess module (membership, roles mapped to permissions,
  minimal append-only audit of membership and role changes) → scoped tenant context that fails closed
  and policy-based authorization → negative cross-tenant tests. Final slicing: SPEC-0002.
- **Decide first:** Open Decisions 3, 11, 16 (pulled forward), and 20. The first threat model, for the
  tenant boundary, is written here because the blueprint requires one before the first tenant-scoped workflow.
- **Accepted deviation (2026-10-19):** M2 must persist tenants and memberships, and the gate needs real
  database evidence, so the persistence foundation and Open Decision 16 move from M3 to M2. M2 database
  tests use the Compose PostgreSQL; Testcontainers (Open Decision 21) stays in M3.
- **Gate:** positive and negative tenant isolation tests pass for representative resources.

### M3 — First vertical slice: catalog and inventory (P1)
- **Slices:** (EF Core and the Migrator arrive in M2) → row-level security on the first tenant-owned
  table (ADR-0011) → Catalog products with tenant-aware
  constraints → Inventory stock balances → REST and OpenAPI with Problem Details and pagination →
  Testcontainers PostgreSQL integration tests → architecture tests for module boundaries.
- **Decide first:** Open Decisions 21 (15 and 17 moved to M2-S4; the URL versioning scheme was decided in
  ADR-0015 during the pre-S5 review, leaving the compatibility policy and OpenAPI checks here). API conventions and compatibility policy documents.
- **Gate:** API contract, database constraints, integration tests, and OpenAPI docs pass.

### M4 — Order and inventory consistency (P1)
- **Slices:** order aggregate and explicit state machine → reservation with atomic conditional
  updates → concurrency token on edits → concurrent last-unit test. Delivered as three slices of
  SPEC-0004: reservations first (the riskiest part), then the Orders module with ETag edits, then submit
  and cancel with the concurrency tests.
- **Decided:** ADR-0020 (state table, reservation model, Orders-to-Inventory flow; accepted 2026-10-21).
- **Gate:** a concurrent last-item test proves no oversell; illegal state transitions are rejected.
- **Guide:** [milestones/M4-order-and-inventory-consistency.md](milestones/M4-order-and-inventory-consistency.md)

### M5 — Messaging foundation (P1)
- **Slices:** transactional outbox → publisher with bounded batches → RabbitMQ in Compose →
  idempotent consumer with a processed-message table → dead-letter path → backlog and oldest-message
  age metrics. The first consumer is the **Notifications** module (P1 in the blueprint), for example
  "order confirmed → notification". Delivered as four slices of SPEC-0005: outbox write, RabbitMQ and publisher with
  metrics, Notifications consumer, retries and dead-letter path.
- **Decided:** Open Decision 4 (ADR-0021, accepted 2026-10-21): RabbitMQ via RabbitMQ.Client, per-module outbox,
  idempotent consumers, TTL retries then a dead-letter queue. The worker's client-credentials identity (ADR-0010) is
  deferred to the first worker that calls an API.
- **Gate:** duplicate publication and consumption tests pass; backlog and oldest-message age are observable.
- **Guide:** [milestones/M5-messaging-foundation.md](milestones/M5-messaging-foundation.md)

### M6 — Payment workflow (P1)
- **Slices:** deterministic payment simulator → payment intent and attempt states → idempotency keys
  → signed webhook verification with replay protection → order saga / process manager →
  reconciliation job for unknown outcomes.
- **Decide first:** Open Decision 12.
- **Gate:** timeout/unknown outcomes and duplicate or out-of-order webhooks recover safely.
- **Delivered as (SPEC-0006):** Payments module, simulator, idempotent creation → signed webhooks → order saga →
  workflow timers, reconciliation and order recovery → Stripe test-mode adapter → review fixes. Open Decision 12 closed
  by ADR-0022.
- **Guide:** [milestones/M6-payment-workflow.md](milestones/M6-payment-workflow.md)

### M7 — Subscriptions and refunds (P2)
- **Slices:** subscription state machine → renewals on a persistent schedule → refund workflow
  with its own idempotency, authorization, and audit trail.
- **Gate:** duplicate renewal or refund attempts cannot create duplicate business effects.
- **Delivered as (SPEC-0007):** Subscriptions module → Quartz.NET over due rows, dead-lettering and renewals → refunds in
  Payments → order refund saga and cancelling a paid order → `requires_action` and ISK/UGX → review fixes. Open Decision 18
  (persistent scheduler) closed by ADR-0023, pulled forward from M8 by you.
- **Guide:** [milestones/M7-subscriptions-and-refunds.md](milestones/M7-subscriptions-and-refunds.md)

### M8 — Async files and reporting (P2/P3)
- **Slices:** PostgreSQL full-text search → object storage adapter → asynchronous import jobs with
  row-level validation → streamed exports with CSV formula-injection protection → persistent scheduler.
- **Decide first:** Open Decision 19 (Open Decision 18 was closed in M7 by ADR-0023: Quartz.NET; the persistent scheduler slice becomes plain Quartz cron jobs).
- **Gate:** large jobs are asynchronous, tenant-scoped, bounded, resumable where needed, and observable.
- **Delivered as (SPEC-0008):** full-text search → ImportsExports module and storage port → import jobs → export jobs →
  file expiry, Quartz metrics task and job counters → review fixes. Open Decision 19 closed by ADR-0024.
- **Guide:** [milestones/M8-async-files-and-reporting.md](milestones/M8-async-files-and-reporting.md)

### M9 — Security and privacy (P2)
- **Slices:** threat models for every exposed workflow → append-only audit module → retention and
  deletion processes → secret, dependency, container, and IaC scanning in CI → platform-admin and
  support-operator boundary.
- **Gate:** threat-model actions are tracked; high-risk security tests and pipeline checks pass.
- **Delivered as (SPEC-0009):** threat models and action register → Audit module → audited actions and reads → retention
  and pseudonymisation → job re-checks and storage hardening → security scanning and CI job → platform-admin and
  support-operator boundary. Open Decision 8 closes when you accept ADR-0025. CI stays on hold (decision 1).
- **Guide:** [milestones/M9-security-and-privacy.md](milestones/M9-security-and-privacy.md)

### M10 — Production observability (P3)
- **Slices:** golden-signal and business dashboards → alerts → SLO definitions per journey → runbooks.
- **Gate:** key user journeys and worker workflows can be diagnosed from metrics, logs, and traces.
- **Delivered as (SPEC-0010):** telemetry guards (redaction, label allow-list, retention) → missing signals → dashboards
  as code → SLO documents and recording rules → alert rules with promtool tests and Alertmanager → runbooks and
  scripted fault drills.
- **Guide:** [milestones/M10-production-observability.md](milestones/M10-production-observability.md)

### M11 — Performance and resilience (P3)
- **Slices:** workload model → k6 scenarios → baseline report → fault-injection suite.
- **Decide first:** Open Decision 9.
- **Gate:** a repeatable baseline and failure reports exist; capacity assumptions and bottlenecks are documented.

### M12 — Cloud deployment (P3)
- **Slices:** AWS versus GCP comparison → Terraform with remote state → registry, managed PostgreSQL,
  and secrets → CI/CD with OIDC federation, image build, SBOM, and promotion → smoke checks and rollback rehearsal.
- **Decide first:** Open Decisions 1 and 2.
- **Gate:** reproducible deployment, smoke checks, and a tested application rollback path.

### M13 — Disaster recovery (P3)
- **Gate:** a restore drill verifies integrity and records measured recovery time against approved objectives.

### M14 — Advanced labs
- RabbitMQ vs Kafka, event sourcing, service extraction, EF Core vs Dapper, cache vs no cache,
  AWS vs GCP, managed containers vs Kubernetes, identity approaches, REST vs gRPC, polling vs
  SignalR/SSE, PostgreSQL search vs a dedicated engine, and failure drills.
- **Gate:** each lab has a hypothesis, method, results, trade-offs, and an ADR or an explicit
  "not adopted" decision.

## Sequencing notes for review

These are recommendations, not silent changes. Each needs your agreement before it takes effect.

**Status (2026-10-19): all four accepted at the start of M2.** The owner delegated the choice and
the recommendations were adopted as written. M2 therefore includes the tenant-boundary threat model
and a minimal append-only audit record for membership and role changes.

1. **Notifications** is P1 in the blueprint but has no milestone of its own. The plan places it in
   M5 as the first idempotent consumer.
2. **Audit** is assigned to M9, but role changes appear in M2. Recommendation: introduce a minimal
   append-only audit write path in M2 for membership and role changes, then complete the module in M9.
3. **Threat modeling** starts in M2, not M9, because the blueprint requires a threat model before
   the first tenant-scoped workflow is exposed.
4. **Architecture tests** start in M3, when there are two modules to separate. Before that they
   would only assert trivia.
5. **M12 runs before M11 (owner decision, 2026-10-22).** This is a deviation from "milestones run strictly in
   order". M11 stays Planned and comes back after M12. What M12 loses without M11:
   - No workload model, baseline or capacity numbers, so instance sizes, database tiers, connection-pool limits
     and autoscaling bounds in M12 are provisional and are revisited from the M11 baseline.
   - SLO targets stay provisional (ADR-0030); deployed alerts use the M10 raw targets.
   - postgres_exporter (server-side database metrics) stays out; the deployed dashboards keep the M10 stand-ins.
   - Rate limiting (TB-A21, BW-A15, PY-A23, IE-A10, PS-A21, MW-A12) stays in M11, so the first deployed
     environment has no per-tenant rate limits and must not take untrusted public traffic until M11 delivers them.
   - SC-A13 (.NET 10 move) and DP-A26 (ADR-0025 acceptance), which were parked on M11 and are yours, move to M12
     so they are not left behind on a later milestone. SC-A13 was done on 2026-10-24 by SPEC-0015.
