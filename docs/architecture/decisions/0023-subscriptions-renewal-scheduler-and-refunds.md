# ADR-0023: Subscriptions, the persistent scheduler for renewals, and refund workflows

- Status: Accepted (2026-10-21 by the user, with Quartz.NET as the scheduler; amended 2026-10-21 by the M7 review fixes the
  user chose, "fix all": decision 6)
- Date: 2026-10-21
- Owners: TenantFlow tech lead (scope, scheduler and refund rules chosen by the user on 2026-10-21; every SPEC-0007
  open question accepted with the recommendation)
- Related decisions: [ADR-0002](0002-modular-monolith.md), [ADR-0011](0011-postgresql-row-level-security.md),
  [ADR-0012](0012-module-data-ownership.md), [ADR-0021](0021-messaging-broker-outbox-and-consumers.md),
  [ADR-0022](0022-payments-provider-port-and-order-payment-saga.md); [SPEC-0007](../../specs/0007-subscriptions-and-refunds.md);
  blueprint sections 6, 7, 11, 12
- Closes: Open Decision 18 (persistent scheduler), pulled forward from M8 by the user

## Context
M7 charges subscriptions every period and refunds paid orders. Renewals are delayed business work that must survive
restarts, run once per period across several Worker instances, and never charge twice (blueprint section 11: no
in-memory timers for business-critical work). Refunds are separate workflows with their own idempotency,
authorization, state and audit (section 12). ADR-0022 left both open and named "timer volume that needs a real
scheduler" as a revisit trigger. The user chose to close Open Decision 18 now rather than defer it to M8.

## Decision
1. **Scheduler (Open Decision 18): Quartz.NET** (chosen by the user), in the Worker only, with the clustered
   AdoJobStore on PostgreSQL in its own `scheduler` schema (DDL applied by the Migrator, ADR-0012), so a recurring job
   fires once across Worker instances and a missed fire runs once after a restart (misfire: fire now). Quartz cannot
   join an EF Core transaction, so business due dates stay in the owning module's `workflow_timers` rows, written in
   the transaction of the state change that needs them (a due date exists if and only if that change committed). Quartz
   runs one recurring job per module (`DisallowConcurrentExecution`, every minute) that claims that module's due rows
   with leases and runs them per tenant under RLS; this replaces the Worker's M6 polling loop. M7 adds poison handling
   to the rows: a `failures` counter (handler exceptions in a row) and `dead_at` after 20 failures; a dead row is
   logged as an error and no longer claimed (re-armed by clearing `dead_at`). M8's recurring jobs (reports, clean-ups)
   are plain Quartz cron jobs.
2. **Subscriptions module** (schema `subscriptions`): plans and subscriptions, states `trialing`, `incomplete`,
   `active`, `past_due`, `cancelled` in a state table. No outbox and no consumer in M7.
3. **Renewal charges go through Payments**: `ISubscriptionCharges` in `Payments.Contracts`, called by the renewal job.
   A payment has exactly one subject, an order or a (subscription, period, attempt); a unique index on the latter makes
   a repeated job find the existing payment instead of charging again. Subscriptions learns the outcome by polling the
   same contract from its job, not by events, so no new queue or consumer is needed. The period advances by a
   conditional update on (period, attempt).
4. **Refunds live in Payments** (`payments.refunds`), only for order payments. Each refund has its own row, state table
   (as payments), `Idempotency-Key` (operation `create_refund`) and provider idempotency key (the refund id). The
   captured-amount check runs with the payment row locked (`FOR UPDATE`) in the transaction that inserts the refund.
   The port gains `RefundAsync` and `GetRefundStatusAsync`; the refund uses the provider stored on the payment.
   Audit fields (who, reason, when) live on the refund row until the M9 audit module.
5. **Order refund saga** in Orders: states `PartiallyRefunded`, `CancellationPending`, `Refunded`; the existing Orders
   consumer applies `payment.refund_succeeded.v1` / `payment.refund_failed.v1`. Cancelling a paid order commits
   `CancellationPending` and an Orders job, which asks Payments for the one cancellation refund of the remainder.

6. **Review amendments (M7-R1, SPEC-0007 "Review fixes")**: money already taken for a subscription that ended is given
   back by the renewal job (a cancellation refund of the subscription charge); a renewal charge in review does not grant
   its period (`past_due`); renewals pause while the tenant is suspended and end for a removed member
   (`IMembershipDirectory` in `IdentityAccess.Contracts`); after an outage only the current period is charged; a
   cancellation refund waits for every other refund of its payment to settle; due rows back off up to an hour (20
   failures span about 15 hours). Accepted risks: due-row tables stay outside RLS (ids only, M6 pattern), and the
   paid-order cancel route checks `orders.place` with `orders.manage` enforced in the service.

## Options considered
- Scheduler: **Quartz.NET** with the clustered ADO.NET store (chosen by the user: mature cron, misfire policies and
  clustering; its own `qrtz_*` tables and connection, and no tenant awareness) vs a **custom PostgreSQL** scheduler
  (Claude's first recommendation: no dependency, but we would own misfire, clustering and cron) vs **Hangfire** with
  community PostgreSQL storage (dashboard and retries; LGPL core, storage package outside the vendor).
- Quartz use (chosen by the user): recurring per-module jobs over transactional due rows vs one Quartz trigger per
  renewal scheduled after the commit (a crash between commit and scheduling loses the trigger, so a cross-tenant sweep
  would have to re-create it).
- Renewal outcome: polling through the contract (chosen) vs payment events consumed by Subscriptions (a queue,
  consumer and processed-messages table more) vs Payments calling Subscriptions (couples Payments to a consumer).
- Refund ownership: Payments (chosen, the provider and the captured amount are there) vs Orders (would need the
  captured amount and provider across the boundary).
- Payment status after refunds: `succeeded` plus `refunded_amount` (recommended) vs new payment states.

## Consequences
- Easier: one scheduling mechanism for M6 to M8 with clustering and misfire handling from a mature library; duplicate
  renewal and refund attempts are stopped by unique indexes and a row lock, not by timing.
- Harder: a stateful dependency (Quartz packages, the `scheduler` schema and its role grants); two layers to read when
  diagnosing a late job (the Quartz trigger and the due row); dead rows need an operator query until M10; payments get
  a second subject; three more forced-RLS tables; the provider adapters grow refunds.
- Follow-up: scheduler metrics and alerts (M10); refunding renewal charges; plan changes and proration; job and
  idempotency-key retention.

## Validation
SPEC-0007 criteria with tests; break-it proofs for the duplicate renewal job, the per-attempt unique index, concurrent
refunds above the captured amount, refund idempotency, refund webhook dedupe, the cancellation-refund retry, dead-job
handling and cross-tenant denial.

## Revisit triggers
Job volume or latency the timer tables cannot carry; a need for cron expressions or a scheduler UI; a provider without
refund idempotency keys; refunds of renewal charges.
