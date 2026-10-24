# M7 — Subscriptions and refunds

**Goal:** a tenant sells recurring plans and charges them on a persistent schedule, and an order manager gives money
back, in part or in full, or cancels a paid order. Whatever is retried, duplicated or crashed, money moves at most once.

**Gate (roadmap):** duplicate renewal or refund attempts cannot create duplicate business effects.

Spec: [SPEC-0007](../specs/0007-subscriptions-and-refunds.md) (Implemented). Decision:
[ADR-0023](../architecture/decisions/0023-subscriptions-renewal-scheduler-and-refunds.md) (Accepted 2026-10-21, with
Quartz.NET as the scheduler; decision 6 added by the review fixes you chose). Open Decision 18 (persistent scheduler) is
closed by it, pulled forward from M8.

## 1. What was built

```text
src/
├── BuildingBlocks/Messaging/Timers         failures + dead_at on due rows (20 in a row = dead), backoff up to an hour,
│                                           a per-module RunDueAsync
├── BuildingBlocks/Scheduling (new)         Quartz.NET 3.22.4: clustered AdoJobStore in the scheduler schema (Quartz DDL as a
│                                           Migrator migration), one WorkflowTimerJob per module, SchedulerHostedService
│                                           (starts once PostgreSQL answers, retries, shuts down cleanly)
├── Hosts/Api/Endpoints/V1                  Tenants/Subscriptions (plans, subscriptions), Payments/RefundEndpoints,
│                                           Orders cancel with an optional reason and 202
├── Hosts/Worker                            Quartz instead of the M6 polling loop; Subscriptions module; membership directory
└── Modules/
    ├── Subscriptions (new)                 Plan, Subscription and its state table (trialing, incomplete, active, past_due,
    │                                       cancelled); PlanService, SubscriptionService, SubscriptionRenewer; store
    ├── Payments                            subject: order or (subscription, period, attempt); ISubscriptionCharges; Refund
    │                                       workflow (own key, state, audit, reconciliation, webhooks, events);
    │                                       RemainderRefunds; IOrderRefunds; Stripe refunds; requires_action; ISK/UGX
    ├── Orders                              partially_refunded, cancellation_pending, refunded; refund events in the saga;
    │                                       OrderCancellationRefunds job
    └── IdentityAccess                      subscriptions.manage / subscriptions.subscribe; IMembershipDirectory
```

Tables: `subscriptions.plans`, `subscriptions.subscriptions`, `payments.refunds` (forced RLS, 15 forced tables in all);
`subscriptions.workflow_timers` and the `scheduler.qrtz_*` tables (no tenant data under RLS by design).

## 2. Architectural reasoning

- **Duplicates stopped by data, not by timing.** A renewal charge is one payment per (subscription, period, attempt),
  enforced by a unique index; its id is the provider's idempotency key. A refund is its own row with its own
  `Idempotency-Key` and provider key; the captured amount is checked with the payment row locked, so concurrent refunds
  serialize. A period advances only by a conditional save on exactly the state the renewal read.
- **Quartz for "when", rows for "what".** Quartz cannot join an EF Core transaction, so business due dates stay in the
  module's `workflow_timers`, written with the state change that needs them. Quartz (clustered, persistent, misfire
  "fire now") runs one job per module that claims due rows with leases. A schedule therefore exists if and only if its
  state change committed, and two Worker instances never run a fire twice.
- **Polling a contract instead of a new consumer.** Subscriptions learns a charge's outcome by asking
  `ISubscriptionCharges` from its own due row; no queue, consumer or processed-messages table was added.
- **Compensation is a new business action.** A renewal charge that succeeds after its subscription ended is refunded by
  the renewal job (review fix R1); a cancelled paid order is refunded by a job in Orders through `IOrderRefunds`, which
  waits until every other refund settled (R4) and allows one cancellation refund that has not failed (R3).
- **Payments owns refunds.** The provider, the captured amount and the payment lock are there; Orders only reacts to
  `payment.refund_succeeded.v1` / `payment.refund_failed.v1` through its state table.
- **Background money work respects people and tenants.** Renewals stop for a removed member and wait while the tenant
  is suspended; after an outage they charge the current period only.

## 3. Security considerations

- Every new store method filters by the context tenant inside `TenantScopedTransaction`; RLS is forced on the three new
  tenant tables; the isolation matrix covers 25 routes. Timer and scheduler rows hold ids only (accepted, as in M6).
- Refund routes require `orders.manage`; cancelling a paid order needs `orders.manage` in the service (the route needs
  `orders.place`, accepted in ADR-0023 decision 6). Subscriptions are visible only to the subscriber and managers (404).
- Refund webhooks: signature over the raw bytes first, event-id dedupe, tenant from signed metadata, stored provider
  reference must match. No card data anywhere: `PaymentMethodTokens` is shared by Payments and Subscriptions.
- The scheduler schema gives the application role DML only (role privilege tests); the Migrator owns its DDL.

## 4. Verification

Fresh evidence for the gate (2026-10-21) at `3b1bfb8`:

| Command | Result |
|---|---|
| `./scripts/verify.ps1 -Database` | all checks passed; Tenants 77/77, IdentityAccess 68/68, Catalog 7/7, Architecture 7/7, Inventory 5/5, Orders 6/6, Payments 14/14, Subscriptions 7/7, Worker 13/13, Api 127/127, Database 254/254; 0 warnings |
| `./scripts/progress.ps1 -Verify` (CI test command with coverage) | quality run passed for `3b1bfb8`: 0 warnings, 331/331 non-database tests, Compose valid. Line coverage of the new code is low here (Payments.Application 9%, Subscriptions.Application 10%, Scheduling 34%) because the coverage run excludes the database tests, where it is exercised; Subscriptions.Domain 73% |

**Break-it checks re-run for the gate** (fault injected, test failed at an assertion, fault restored, rebuilt):
- **Gate:** no unique payment per renewal attempt (renewal test fails); refund key never claimed (replay test fails);
  refunds above the captured amount (over-refund test fails).
- **Deferred proofs:** customers may create plans; the period end does not advance; one attempt too many; customers may
  refund; refund reconciliation never settles; a full refund counted as partial; ISK/UGX sent as whole units;
  `requires_action` pending again. Each made its test fail.

Per-slice and review-fix proofs (34) are in the spec's Verification table and the tracker. Not proven by a fault: the
critical log for a refund reversed by the provider (R12) has no test; it is asserted only by reading.

**Environment notes:** Docker in WSL dropped out briefly about one run in five during this milestone (role provisioning,
"engine not reachable"); every affected run passed on retry. `WorkerHostTests` passed in both full runs.

**Not verified:** a live Stripe test-mode run (refunds tested against documented shapes); two Worker processes against
one Quartz cluster (clustering is configured and the store is shown clustered, not run twice); CI (on hold).

## 5. Trade-offs and assumptions

- **Quartz 3.22, not 4.x:** 4.0 shipped 2026-09-03 and changes every few days; 3.22.4 is past the 14-day cooldown.
- **No plan changes, proration, invoices or entitlements;** the price is copied at subscribe time.
- **Renewal charges are server-side Stripe confirms** with a stored token; 3-D Secure fails the attempt (and cancels
  the intent).
- **Skipped periods after an outage are not charged** (R12): a business choice made while fixing the review; change it
  in ADR-0023 if back-billing is wanted.
- **Suspended tenants pause renewals** (R11); refunds already requested still complete.
- **A charge crashed between its commit and the provider call** is still reconciled into review, not re-sent; the
  subscription becomes `past_due` meanwhile (R5).
- Durations are constants (`SubscriptionRenewals`, `OrderCancellationRefunds.WaitForRefunds`).

## 6. Mentor exercises

1. Replace the per-attempt unique index with a check in `SubscriptionCharges` only. Write the interleaving that charges
   twice.
2. Move the cancellation refund into the cancel request itself. What does a crash between the order commit and the
   Payments call leave, and why does the job exist?
3. Schedule one Quartz trigger per subscription instead of due rows. Where does a crash lose a schedule?
4. Let a cancellation refund start while a manual refund is pending. Construct the two failure endings R4 prevents.
5. Back-bill the skipped periods after an outage instead. Which customer-facing rule and which test change?

## 7. Interview talking points

- "How do you stop double renewals?" One payment per (subscription, period, attempt) by a unique index, its id as the
  provider key, and a conditional save on the exact state read; any run can repeat.
- "How do refunds stay within the captured amount?" Lock the payment row, sum the refunds that have not failed, insert
  in the same transaction; pending refunds hold their amount.
- "Why Quartz and still a timers table?" Quartz can't join your transaction; due rows written with the state change
  make schedules exactly as durable as the change, Quartz adds clustering and misfire handling.
- "What is compensation here?" Money taken after a subscription ended is refunded by a new action, never by undoing
  the charge.

## 8. Exit checklist

- [x] Duplicate renewal attempts create one payment and one period advance (`SubscriptionRenewalTests`, proof re-run)
- [x] Duplicate refund attempts replay, never refund twice, and never exceed the captured amount (`RefundEndpointTests`, proofs re-run)
- [x] Cancelling a paid order refunds the rest once (`OrderRefundSagaTests`)
- [x] SPEC-0007 Implemented: criteria 1-17 and the review fixes have verification rows
- [x] Reviews: tenant isolation (1 medium, 3 low), correctness (3 high, 3 medium, 4 low), payments/security (1 high, 3
      medium, 3 low); all fixed in `3b1bfb8` by your choice ("fix all"), R13 recorded as accepted
- [x] ADR-0023 accepted by you on 2026-10-21; Open Decision 18 closed; decision 6 added by the review fixes
- [ ] **You:** review this guide and the M7 commits on branch `m7-subscriptions`, and merge into `main` when satisfied
- [ ] **You:** approve the start of M8 (async files and reporting)
