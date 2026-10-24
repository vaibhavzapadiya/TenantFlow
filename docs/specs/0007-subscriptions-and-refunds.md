# SPEC-0007: Subscriptions and refunds

- Status: Implemented (approved 2026-10-21 by the user, with Quartz.NET as the scheduler; gate 2026-10-21)
- Milestone: M7
- Owner: TenantFlow tech lead (user)
- Related: blueprint sections 6 (Subscriptions module), 7 (order lifecycle, subscription transitions), 11 (sagas,
  persistent scheduler), 12 (refunds), 14; ADR-0002, ADR-0011, ADR-0012, ADR-0020, ADR-0021, ADR-0022;
  ADR-0023 (Accepted); SPEC-0006; Open Decision 18

## Outcome
A tenant sells recurring plans: a tenant admin defines plans, a member subscribes with a provider payment token, and
TenantFlow charges each billing period on a persistent schedule, retries a failed renewal, and ends the subscription
when payment keeps failing or the member cancels. An admin refunds a paid order in part or in full, or cancels a paid
order, which refunds what is left. Whatever is retried, duplicated or crashed, a period is charged at most once per
attempt, a refund is sent at most once, and refunds never add up to more than was captured (the M7 gate).

## Acceptance criteria
**Plans and subscriptions (API, new Subscriptions module)**
1. Given a holder of `subscriptions.manage`, when they `POST .../plans` with name, amount, ISO currency, interval
   (`month` or `year`) and trial days (0 to 90), then 201 and the plan is listed by `GET .../plans`. A caller without
   the permission gets 403; an amount <= 0 or an unknown currency or interval gets 400.
2. Given a holder of `subscriptions.subscribe` and an active plan, when they `POST .../subscriptions` with the plan id
   and a provider token, then 201: with trial days the subscription is `trialing` until the trial ends; without, it is
   `incomplete` and its first charge is due at once. Its renewal timer is written in the same transaction. A second
   live subscription of the same member to the same plan is 409 `subscriptions.already_subscribed`. A card-number-shaped
   token is 400 (SPEC-0006 criterion 15 rule).
3. Given a subscription, when its subscriber or a holder of `subscriptions.manage` reads or cancels it, then 200;
   anyone else in the tenant gets 404. Cancelling `trialing`, `active` sets cancel-at-period-end (the status stays until
   the period ends, then `cancelled`, never charged again); cancelling `incomplete` or `past_due` cancels at once.
   Cancelling a `cancelled` subscription is 409 `subscriptions.invalid_transition`.
4. Given a user of tenant A, when they read, cancel or subscribe to tenant B's plan or subscription, then 404 and
   nothing is written.

**Renewals (Worker, persistent schedule)**
5. Given a subscription whose period ends (or an `incomplete` one), when its timer runs, then the Worker asks Payments
   (through `Payments.Contracts`) to charge period *n*, attempt *k*, with the plan's amount and currency; Payments stores
   at most one payment per (subscription, period, attempt) and calls the provider outside any transaction with the
   payment id as its idempotency key. On success the subscription moves to `active`, the period advances by one interval
   from the anchor date (Jan 31 -> Feb 28 -> Mar 31), and the timer is moved to the new period end.
6. Given the timer runs again for the same period and attempt (lost lease, crash, duplicate run), then no second
   payment is created, the provider is not charged again, and the period advances once (conditional update on the
   period and attempt).
7. Given the charge fails, then `trialing | active -> past_due` and attempt *k+1* is due after 1 day; after 3 failed
   attempts the subscription is `cancelled` (reason `payment_failed`). A failed first charge of an `incomplete`
   subscription cancels it at once. Given the charge is still pending or unknown, then the timer checks it again
   every 5 minutes and creates no new payment.
8. Given Quartz.NET runs each module's due-row job every minute in the Worker (clustered, so one Worker instance runs
   it at a time), when a due row's handler throws 20 times in a row, then the row is marked dead, logged as an error
   and no longer claimed; other rows keep running (Open Decision 18, ADR-0023).

**Refunds (API and Worker, Payments module)**
9. Given a holder of `orders.manage` and an order whose payment `succeeded`, when they `POST
   .../orders/{orderId}/payments/current/refunds` with an `Idempotency-Key`, an amount in the payment's currency and a
   reason, then 201 with the refund (`succeeded`, `failed` or `processing`); the refund, its key and its reconciliation
   timer are committed before the provider is called with the refund id as its idempotency key. The refund row keeps
   who asked, why and when (audit). A customer gets 403; a missing reason or amount <= 0 is 400.
10. Given refunds of a payment that are not `failed` already add up to *r*, when a refund of *a* > captured - *r* is
    asked (also by two concurrent requests), then 409 `payments.refund_exceeds_captured` and nothing is stored: the
    check runs with the payment row locked.
11. Given the same key and body again, then the stored response is replayed and the provider is not called; a
    different body is 422, an unfinished one 409 (SPEC-0006 criterion 2 rules, operation `create_refund`).
12. Given a refund timeout or 5xx, then the refund is `unknown` and is reconciled like a payment (2 minutes, every
    minute, 5 checks, then `requires_review`). Signed refund webhooks (`refund.updated`, `refund.failed`) settle it with
    the same dedupe and tenant-match rules as SPEC-0006 criteria 7 to 10.
13. Given a refund succeeds, then `payment.refund_succeeded.v1` is published and the order moves `Paid ->
    PartiallyRefunded` or `Paid | PartiallyRefunded -> Refunded` when the refunds reach the captured amount; one effect per
    message, in the event's tenant only.
14. Given a holder of `orders.manage` cancels a `Paid` or `PartiallyRefunded` order with a reason, then 202 and the
    order is `CancellationPending`; the Worker asks Payments for one cancellation refund of the remaining amount (at
    most one per payment), and its success moves the order to `Refunded`. Its failure returns the order to its previous
    state (`payment.refund_failed.v1`). A customer cancelling a paid order gets 403.
15. Given a user of tenant A, when they refund or list refunds of tenant B's order, then 404 and nothing is written.

**Folded M6 findings and structure**
16. Given Stripe answers `requires_action`, then the payment (and a renewal charge) is `failed` with code
    `requires_action`, not pending. Given UGX or ISK, then Stripe receives the amount times 100 and an amount that is
    not a whole unit is refused (Stripe's special cases).
17. Then Subscriptions is a module with its own schema, Worker timer registration and entries in the module, schema,
    role, RLS (forced 12 -> 15: plans, subscriptions, payments.refunds), isolation matrix (18 -> 25 routes) and Migrator
    tests; no card data is accepted, stored or logged.

## Invariants
- At most one payment per (subscription, period, attempt) and one provider idempotency key per payment or refund.
- The sum of a payment's refunds that are not `failed` never exceeds its captured amount; at most one cancellation
  refund per payment.
- Subscriptions, orders, payments and refunds change only through their state tables, with conditional updates.
- No transaction is open during a provider call. Every tenant-owned read and write runs in the identity's, the
  timer's or the verified event's tenant.

## Contract
Routes are under `/v1/tenants/{tenantId}`. Errors are Problem Details (ADR-0016).
| Operation | Permission and resource rule |
|---|---|
| `POST /plans`, `GET /plans` | `subscriptions.manage`; list also `subscriptions.subscribe` (active plans only) |
| `POST /subscriptions` `{planId, paymentMethod}` | `subscriptions.subscribe`; subscriber is the caller's membership |
| `GET /subscriptions/{id}`, `POST /subscriptions/{id}/cancel` | subscriber or `subscriptions.manage`, else 404 |
| `POST /orders/{id}/payments/current/refunds` `{amount, reason}` + `Idempotency-Key` | `orders.manage` |
| `GET /orders/{id}/payments/current/refunds` | `orders.manage` |
| `POST /orders/{id}/cancel` `{reason}` on a paid order | `orders.manage` (unchanged for unpaid orders) |

New permissions: `subscriptions.manage` (tenant admin), `subscriptions.subscribe` (tenant admin, customer).
Events (additive): `payment.refund_succeeded.v1` `{orderId, paymentId, refundId, amount, currency, refundedTotal,
fullyRefunded, cancellation}`, `payment.refund_failed.v1` `{orderId, paymentId, refundId, cancellation}`. Payments of
subscriptions publish no order events. Contract `ISubscriptionCharges` in `Payments.Contracts` (charge, get status).

## Data
- `subscriptions.plans` (tenant_id, id, name, amount numeric(19,4), currency, interval, trial_days, active);
  `subscriptions.subscriptions` (tenant_id, id, plan_id, subscriber_membership_id, status, payment_method, anchor_at,
  period, period_end, failed_attempts, cancel_at_period_end, cancel_reason); unique live subscription per
  (tenant, member, plan); `subscriptions.workflow_timers`. Both tenant tables `ENABLE` + `FORCE` RLS.
- `payments.payments` (expand): `order_id` nullable; `subscription_id`, `billing_period`, `charge_attempt`; check that
  exactly one subject is set; unique (subscription_id, billing_period, charge_attempt). `payments.refunds` (tenant_id,
  id, payment_id, amount, currency, status, kind `refund|cancellation`, reason, requested_by_membership_id,
  provider_refund_id, failure_code, timestamps); partial unique (payment_id) where kind = `cancellation`; forced RLS.
- Every `workflow_timers` table gets `failures` and `dead_at` (ADR-0023). Orders state check adds
  `partially_refunded`, `cancellation_pending`, `refunded`. All migrations are additive.

## Failure modes
- Crash after a renewal payment commits and before the provider call: Payments' reconciliation settles it as in M6;
  the renewal timer polls it and never creates a second payment for the attempt.
- Provider outage during renewals: charges are `unknown`, reconciled; the subscription waits, never double-charges.
- Concurrent refunds: serialized by the payment row lock; the loser gets 409.
- Cancellation-refund request lost between the order commit and Payments: the Orders timer retries it; Payments'
  one-per-payment cancellation refund makes the retry harmless.

## Out of scope
Plan changes, proration, entitlements, invoices, dunning e-mails, refunding renewal charges, a second payment attempt
for orders, resolving `requires_review` by hand, the M9 audit module (refund rows carry the audit fields until then).

## Decisions (approved by the user, 2026-10-21)
1. Scheduler (Open Decision 18): Quartz.NET (clustered AdoJobStore, `scheduler` schema, Worker only) runs one
   recurring job per module over that module's transactional `workflow_timers` due rows (ADR-0023 decision 1).
2. Subscribe idempotency: the natural key (one live subscription per member and plan; a repeat is 409).
3. `incomplete` status for a subscription without a trial until its first charge succeeds.
4. A refunded payment stays `succeeded` with `refunded_amount`; refund state lives on refund rows.
5. A failed cancellation refund returns the order to `Paid` / `PartiallyRefunded`.
6. Constants: 3 renewal attempts 1 day apart; a due row is dead after 20 handler failures in a row.

## Review fixes (the user chose "fix all", 2026-10-21)
The M7 review (tenant isolation, correctness, payments/security) found 13 issues; all were fixed in M7-R1:
- R1: a renewal charge that succeeds after its subscription ended (cancelled at once, or at its period end while the
  charge was pending) is refunded once by the renewal job (a cancellation refund of a subscription charge; no event).
- R2: a renewal stores its outcome only if the cancel flag is still as it read it.
- R3: a failed cancellation refund no longer blocks the next one; a "not refundable" cancellation returns the order.
- R4: a cancellation refund waits until every other refund of the payment has settled, then refunds exactly the rest.
- R5: a renewal charge in review makes the subscription `past_due` (the period is not granted) and is polled every 10
  minutes, never charged again.
- R6: a refund event that arrives before its payment's event is retried, not recorded.
- R7: Stripe `charge_already_refunded` / `amount_too_large` on a refund is an unknown outcome.
- R8: cancelling an order in a state with no cancel is `orders.invalid_transition`.
- R9: at its next renewal the subscription of a removed member is cancelled (`member_removed`) and never charged.
- R10: a Stripe intent that requires action is cancelled before the payment is reported failed.
- R11: renewals of a suspended tenant wait (checked hourly) and charge nothing.
- R12: a refund reversed by the provider after success is logged as critical; Stripe refund lookup reads every page;
  after an outage an active subscription is charged for the current period only (missed whole periods are skipped, not
  charged); due-row backoff doubles up to an hour, so 20 failures span about 15 hours.
- R13 (no change): the paid-order cancel route requires `orders.place` and the service requires `orders.manage`; timer
  tables stay outside RLS (ids only), as in M6. Both recorded as accepted in ADR-0023.

## Slices
| Slice | Delivers | Criteria |
|-------|----------|----------|
| S1 | Subscriptions module, plans, subscribe, read, cancel, state table, permissions | 1-4, 17 |
| S2 | Quartz.NET scheduler over due rows with dead-lettering, Payments subject expand, `ISubscriptionCharges`, renewals, retries | 5-8 |
| S3 | Refunds in Payments: port + simulator + Stripe, API, captured-amount lock, reconciliation, webhooks, events | 9-12, 15 |
| S4 | Order refund states, consumer, cancelling a paid order with the cancellation refund | 13, 14 |
| S5 | `requires_action` -> failed, UGX/ISK minor units | 16 |

## Verification
| Criterion | Test | Proven to fail without the change |
|-----------|------|------------------------------------|
| 1 | `SubscriptionEndpointTests.An_admin_creates_plans_that_members_list_and_a_customer_cannot_create_one`, `CreatePlanCommandValidatorTests` | Deferred to gate |
| 2 | `SubscriptionEndpointTests.Subscribing_starts_a_trial_or_an_incomplete_subscription_with_its_renewal_row_and_a_second_live_one_is_refused`, `SubscriptionEndpointTests.Two_concurrent_subscribes_of_one_member_to_one_plan_leave_exactly_one_subscription_and_one_renewal_row`, `SubscriptionEndpointTests.A_card_number_is_refused_never_echoed_and_nothing_is_stored`, `SubscriptionStateMachineTests` | Yes: no live unique index (P4), renewal row not written (P5) |
| 3 | `SubscriptionEndpointTests.Only_the_subscriber_and_a_manager_see_or_cancel_a_subscription_and_cancel_follows_the_period_rules`, `SubscriptionStateMachineTests` | Yes: subscriber not checked (P2) |
| 4 | `SubscriptionEndpointTests.Another_tenant_can_neither_see_nor_cancel_nor_subscribe_to_this_tenants_plans_and_subscriptions`, `IsolationMatrixTests` (23 routes) | Yes: no RLS and no tenant filter (P3) |
| 5 | `SubscriptionRenewalTests.A_trial_is_charged_when_it_ends_and_each_period_after_counted_from_the_anchor`, `SubscriptionStateMachineTests` | Deferred to gate |
| 6 | `SubscriptionRenewalTests.A_renewal_that_runs_again_after_charging_or_twice_at_once_charges_once_and_advances_once`, `SubscriptionRenewalTests.One_attempt_holds_one_payment_and_a_stale_renewal_cannot_move_a_subscription_back` | Yes: no unique payment per attempt (Q1), save ignores the state read (Q2) |
| 7 | `SubscriptionRenewalTests.A_declined_renewal_is_past_due_retried_daily_and_cancelled_after_the_third_attempt`, `SubscriptionRenewalTests.A_declined_first_charge_ends_the_subscription_and_a_cancelled_trial_is_never_charged`, `SubscriptionRenewalTests.A_charge_with_an_unknown_outcome_is_asked_about_again_and_never_charged_again` | Deferred to gate |
| 8 | `WorkflowTimerTests.A_timer_whose_handler_fails_twenty_times_in_a_row_is_dead_and_the_others_keep_running`, `SchedulerTests` (3 runs) | Yes: never marked dead (Q3), dead rows still claimed (Q4) |
| 9 | `RefundEndpointTests.An_order_manager_refunds_part_of_a_paid_order_with_an_audited_reason_and_a_customer_cannot`, `StripePaymentProviderTests.A_refund_is_one_refund_of_the_intent_in_minor_units_keyed_by_the_refund_id` | Deferred to gate |
| 10 | `RefundEndpointTests.Refunds_never_add_up_to_more_than_was_captured_even_when_sent_at_once` | Yes: captured amount not checked (R1), payment not locked (R2, a race: one run) |
| 11 | `RefundEndpointTests.A_repeated_refund_key_replays_another_body_is_refused_and_an_unfinished_one_is_in_progress` | Yes: key never claimed (R3), hash not compared (R4) |
| 12 | `RefundEndpointTests.A_refund_with_an_unknown_outcome_is_reconciled_and_a_signed_refund_webhook_settles_another_once`, `StripePaymentProviderTests.A_refused_refund_is_failed_an_ambiguous_one_is_unknown_and_its_status_is_found_by_id_or_metadata` | Deferred to gate (webhook dedupe is the M6 guard) |
| 13 | `OrderRefundSagaTests.A_partial_refund_leaves_the_order_partially_refunded_and_the_rest_refunds_it_once_per_message`, `OrderStateMachineTests` | Deferred to gate (message dedupe is the M6 guard) |
| 14 | `OrderRefundSagaTests.Cancelling_a_paid_order_refunds_what_is_left_once_and_its_event_refunds_the_order`, `OrderRefundSagaTests.A_failed_cancellation_refund_returns_the_order_to_paid` | Yes: repeated job re-sends the refund (O1), job not scheduled with the cancellation (O2), failed refund leaves the order stuck (O3) |
| 15 | `RefundEndpointTests.Another_tenant_can_neither_refund_nor_list_refunds_of_this_tenants_order`, `IsolationMatrixTests` (25 routes), `RowLevelSecurityTests` (15 forced) | Yes: refunds not under RLS (R5) |
| R1-R12 | `SubscriptionRenewalTests` (R1, R2, R5, R9, R11, R12), `OrderRefundSagaTests` (R3, R4, R6, R8), `StripePaymentProviderTests` (R7, R10, R12), `WorkflowTimerTests` (R12) | Yes: refund of a late charge dropped (F1), save ignores the cancel flag (F2), failed refund blocks the next (F3), no wait for unsettled refunds (F4), early refund event recorded (F5); R12 reversal log has no test (deferred to gate) |
| 16 | `StripePaymentProviderTests.An_intent_that_requires_action_is_a_failed_payment_not_a_pending_one`, `StripePaymentProviderTests.Isk_and_ugx_are_whole_units_sent_in_hundredths_and_a_fraction_is_refused` | Deferred to gate |
| 17 | `ModuleBoundaryTests`, `ModuleSchemaTests`, `OutboxAccessTests`, `SchemaOwnershipTests`, `RolePrivilegeTests`, `RowLevelSecurityTests` (14 forced), `MigratorTests`, `RouteTableTests` | Yes: subscriptions not under RLS (P1) |
