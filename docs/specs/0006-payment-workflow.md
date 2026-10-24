# SPEC-0006: Payment workflow

- Status: Implemented (approved 2026-10-21 by the user; gate 2026-10-21)
- Milestone: M6
- Owner: TenantFlow tech lead (user)
- Related: blueprint sections 7 (order lifecycle), 11 (sagas, background work), 12 (payments), 13, 14 (failure table);
  ADR-0002, ADR-0008, ADR-0010, ADR-0011, ADR-0020, ADR-0021; ADR-0022 (Accepted); SPEC-0004, SPEC-0005;
  Open Decision 12

## Outcome
A customer pays for a reserved order. TenantFlow charges at most once per order whatever the client, the network or
the provider does: a repeated request, a timeout with an unknown result, duplicate or out-of-order webhooks, or a crash
between steps. A paid order becomes `Paid`; a declined one becomes `PaymentFailed` and its stock is released; one whose
outcome stays unknown goes to `PaymentReview` instead of being charged again. The provider is a deterministic simulator
in tests and development, with a Stripe test-mode adapter behind the same port. No card data enters TenantFlow.

## Acceptance criteria
**Create a payment (API, Payments module)**
1. Given the owner's `PendingPayment` order, when they `POST .../orders/{orderId}/payments` with an `Idempotency-Key`
   and a provider payment-method token, then one payment is stored for the order with the order's amount and currency
   (taken from Orders, never from the request), the provider is called after the commit with the payment id as its
   idempotency key, and the response is 201 with the payment's status (`succeeded`, `failed` or `processing`).
2. Given the same key and the same body again, then the stored response is replayed (same status code and body) and
   the provider is not called again. Given the same key with a different body, then 422 `payments.idempotency_key_reused`.
   Given a request while the first one with that key is still running, then 409 `payments.request_in_progress`.
   Given no or a malformed key, then 400. Keys are scoped to the tenant: tenant B may reuse tenant A's key.
3. Given an order that already has a payment, when a request with a new key arrives, then 409 `payments.already_exists`
   and no second charge. Given an order not in `PendingPayment`, then 409 `orders.invalid_transition`.
4. Given a user of tenant A, when they pay tenant B's order (or another customer's order in their own tenant), then 404
   and nothing is stored. A caller without `orders.place` gets 403.
5. Given the provider times out or returns a 5xx, then the payment is `unknown` (no failure assumed, nothing retried in
   the request), the response is 201 with status `processing`, and reconciliation (criterion 12) settles it.
6. Given an order with a payment begun, when its owner or an admin cancels it, then 409 `orders.payment_in_progress`
   and the order stays as it is (cancel and payment cannot race: one conditional update wins).

**Webhooks**
7. Given a webhook whose HMAC-SHA256 signature over `timestamp.body` matches the provider's secret and whose timestamp
   is within 5 minutes, when it reports a payment outcome, then the payment moves by its state table and, on a change,
   one event is written to `payments.outbox_messages` in the same transaction. The response is 200.
8. Given a bad or missing signature, a timestamp outside the tolerance, or an unparseable body, then 401 (signature,
   timestamp) or 400, and nothing is stored.
9. Given the same provider event id again, then 200 and nothing changes. Given an event that is illegal for the
   payment's current state (a late `processing` after `succeeded`, a `failed` after `succeeded`), then 200, it is recorded
   as ignored, and the state does not change.
10. Given a webhook naming a tenant and payment that do not match a stored payment with that provider reference, then
    it acts in no tenant (404 to the provider, nothing written); a webhook can never change another tenant's payment.

**Order saga (Orders consumer)**
11. Given `payment.succeeded.v1`, then the order moves `PendingPayment | PaymentReview -> Paid`. Given
    `payment.failed.v1`, then `-> PaymentFailed` and the reservation is released (released again on redelivery, so a
    crash between the two still releases). Given `payment.review_required.v1`, then `PendingPayment -> PaymentReview`.
    Each message has one effect (`orders.processed_messages`), acts in the event's tenant only, and an event the order's
    state does not allow (review after paid) is acknowledged as ignored.

**Reconciliation and recovery (Worker)**
12. Given a payment `processing` or `unknown` for 2 minutes, then every minute the Worker asks the provider for its
    status (no transaction open during the call) and applies it like a webhook. After 5 checks without a final answer,
    the payment becomes `requires_review` and `payment.review_required.v1` is published; checks continue every 10 minutes
    and a later success or failure still settles it.
13. Given an order in `PendingReservation` for 5 minutes, then the Worker finishes its submit (reserve, then
    `PendingPayment` or `Rejected`). Given a `PendingPayment` order with no payment begun 30 minutes after reservation,
    then it is cancelled (`order.cancelled.v1`) and its stock released. A begun payment is never expired.

**Simulator, Stripe and structure**
14. Given the simulator, then each scripted method token produces its outcome deterministically: `sim_success`,
    `sim_decline`, `sim_timeout_then_success`, `sim_timeout_then_decline`, `sim_timeout_unknown`, `sim_server_error`; and
    test helpers produce signed duplicate, delayed and out-of-order webhooks. Given the Stripe adapter, then it maps
    create, status and `Stripe-Signature` verification against Stripe's documented test-mode shapes (recorded fixtures),
    and the host refuses to start with a key that is not a test key (`sk_test_`).
15. Then payments live in a new `Payments` module with its own schema, outbox, Worker registration and entries in the
    module, schema, role, RLS (forced count 8 -> 12: payments, idempotency_keys, webhook_events, orders.processed_messages) and outbox-access tests; no card number, CVC or personal data is
    accepted, stored or logged (a 12-19 digit token is rejected with 400).

## Invariants
- At most one payment per order; the provider receives one idempotency key per payment, so a retry cannot charge twice.
- A payment and an order change state only through their state tables, with conditional updates; arrival order of
  webhooks, reconciliation answers and events never decides the result.
- No database transaction is open during a provider call; intent is committed before the call, outcome after it.
- Every tenant-owned read and write runs in the tenant taken from the identity (API), the verified webhook's stored
  payment (webhook) or the envelope (consumer); never a default.

## Contract
- `POST /v1/tenants/{tenantId}/orders/{orderId}/payments` (permission `orders.place`, owner only; header
  `Idempotency-Key`, 1-255 visible ASCII). Body `{ "paymentMethod": "sim_success" }` (provider token, 3-100 chars
  `[A-Za-z0-9_]`). 201 `{ id, orderId, status, amount, currency, createdAt }` + `Location`.
- `GET /v1/tenants/{tenantId}/orders/{orderId}/payments/current`: owner or `orders.manage`; 404 otherwise.
- `POST /v1/payment-webhooks/{provider}` (`simulator` | `stripe`): anonymous; authenticated by signature only;
  excluded from the tenant route filter. Errors are Problem Details (ADR-0016).
- Events (Payments.Contracts): `payment.succeeded.v1`, `payment.failed.v1`, `payment.review_required.v1`, payload
  `paymentId, orderId, amount{amount, currency}` (+ `failureCode` on failed). Orders queue `orders.payment-events`.
- Worker: queue `orders.payment-events` (bound by name to the three payment events). Each message runs in its own DI
  scope whose `ServiceTenantContext` (IdentityAccess) is entered from the validated envelope: one tenant, no membership,
  no permission, so owner and permission checks still fail closed.
- Orders.Contracts gains `IOrderPayments.BeginPaymentAsync(orderId)`: checks owner and `PendingPayment`, marks the
  payment begun with a conditional update, returns amount and currency.

## Data
- `payments.payments(tenant_id, id, order_id UNIQUE per tenant, amount numeric(19,4), currency char(3), status,
  provider, provider_payment_id, payment_method, failure_code, status_checks, next_check_at, created_at, updated_at,
  xmin)`; `payments.idempotency_keys(tenant_id, key, operation, request_hash, payment_id,
  response_body jsonb (the payment as first answered; every create answers 201), created_at, completed_at, PK (tenant_id, operation, key))`; `payments.webhook_events(provider, event_id PK,
  tenant_id, payment_id, outcome, received_at)`; `payments.outbox_messages` (ADR-0021 shape);
  `orders.processed_messages`; `orders.orders` gains `payment_started_at`, `payment_due_at` (nullable, expand only).
- New tenant tables get `ENABLE` + `FORCE ROW LEVEL SECURITY` with the standard policy. Due-work tables: open question 1.

## Failure modes (blueprint 14)
- Timeout or 5xx on create: `unknown`, reconciled; never re-charged with a new key (5, 12).
- Crash after commit, before the provider call: the payment stays `processing`; reconciliation finds "not found" and
  counts it as unknown until review (12). Crash after the provider answers, before the outcome commits: the webhook or
  reconciliation settles it (7, 12).
- Duplicate or out-of-order webhooks and events: deduplicated by event or message id; the state tables decide (9, 11).
- Broker down: outcome committed with its outbox row; the saga catches up later (ADR-0021).
- Provider down for long: payments go to `requires_review`; orders to `PaymentReview`; nothing is lost.

## Review fixes (M6 gate, chosen by the user on 2026-10-21)
- Reconciliation asks the provider that took the payment (`payment.Provider`), never the one configured now.
- Creation order changed: Orders is read first (no write), then the key, payment and timer commit, then Orders marks the
  payment begun, then the provider is called. A begun mark always has a payment behind it; an order cancelled or expired
  in between gets a failed payment (`order_not_payable`) and no charge. A zero total is `409 payments.nothing_to_pay`.
  ADR-0022 decision 3 is amended to this order (confirmed by the user on 2026-10-21).
- After the payment commits, the steps ignore the client's abort; the provider call keeps its own timeout.
- The simulator takes payments only where `Payments:Simulator:Enabled` is true (Development, tests).
- Timers: batches of 10, and a timer whose lease passed while it waited in the batch is skipped.
- Card numbers split by underscores are refused; a signed event without TenantFlow metadata is acknowledged (200) and
  ignored; a refusal without a provider intent stores no provider reference; signatures cover the raw body bytes.

## Out of scope
Refunds and partial refunds, subscriptions (M7); a second attempt with another method after a decline; manual review
tooling (an admin resolves `PaymentReview` in M7 or later); real card capture, 3-D Secure, client-side Stripe.js; circuit
breakers and provider rate limits (M11); idempotency-key cleanup; order fulfilment states after `Paid`.

## Open questions
All six were answered with the recommendation by the user on 2026-10-21 (recorded in ADR-0022).

1. **Due work across tenants (needs ADR, in ADR-0022).** Reconciliation and recovery must find due rows in every tenant.
   Recommended: per-module `workflow_timers(id, tenant_id, kind, subject_id, due_at, attempts, locked_until)` written in
   the same transaction as the state change, ids only, not under RLS (like the outbox), claimed with leases; the work
   itself runs in the row's tenant under RLS. Alternative: list tenants and scan each under RLS (no new table, but
   O(tenants) queries a minute and a new Tenants contract).
2. **One table for intent and attempt.** Recommended: one `payments` row per order whose status covers the attempt,
   since M6 allows exactly one attempt; an `attempts` table comes with retries in M7. Alternative: both tables now.
3. **Unpaid order expiry.** Recommended: 30 minutes after reservation, `PendingPayment -> Cancelled`, configurable.
   (Implemented as constants in `OrderDeadlines` and `PaymentReconciliation`; configuration comes when a tenant or
   environment needs another value.)
   Alternative: no expiry in M6 (stock stays held until the customer cancels).
4. **Webhook tenant resolution (in ADR-0022).** Recommended: we put tenant and payment id in the provider metadata at
   creation; after the signature checks, the handler sets that tenant and requires the stored provider reference to
   match. Alternative: a cross-tenant lookup table by provider reference.
5. **Stripe adapter without the SDK.** Recommended: `HttpClient` against the two endpoints used plus our own
   `Stripe-Signature` check (no new dependency); tests use recorded fixtures; a live test-mode run is a manual,
   opt-in check with a test key in user-secrets. Alternative: `Stripe.net` (new dependency, more surface).
6. **Order state names.** Recommended: `Paid`, `PaymentFailed`, `PaymentReview` (blueprint `Confirmed`/`Rejected`
   renamed so the reason is visible in the state; fulfilment states later).

## Slices
| Slice | Delivers | Criteria |
|-------|----------|----------|
| S1 | Payments module (new-module checklist), payment state table, provider port + simulator, `BeginPaymentAsync` and the cancel guard, create endpoint with idempotency keys, unknown on timeout, outbox | 1, 2, 3, 4, 5, 6, 14 (simulator create), 15 |
| S2 | Webhook endpoint: signature, timestamp, dedupe, tenant resolution, state-table transitions, signed test helpers | 7, 8, 9, 10, 14 (webhooks) |
| S3 | Order saga: payment states in Orders, `orders.processed_messages`, payment-events consumer in the Worker, release on failure | 11 |
| S4 | Workflow timers, reconciliation job, review escalation, stuck-submit recovery, unpaid expiry | 12, 13 |
| S5 | Stripe test-mode adapter, fixtures, test-key guard, live-run note | 14 (Stripe) |

## Verification
Filled in during implementation.

| Criterion | Test | Proven to fail without the change |
|-----------|------|------------------------------------|
| 1 | `PaymentEndpointTests.A_reserved_order_is_charged_once_for_its_own_amount_and_announces_the_outcome` | Asserted (amount from Orders, one provider call, one event each, payload keys); event-on-change only: deferred to S2 (duplicate webhook) |
| 2 | `PaymentEndpointTests.The_same_key_and_body_replay_the_first_response_without_charging_again`, `A_repeat_while_the_first_request_is_still_charging_is_409_and_starts_nothing` (deterministic gate inside the provider call); `CreatePaymentCommandValidatorTests` | Yes: stored key never consulted, hash not compared, hash without the caller, unfinished key not refused: each fails its test |
| 3 | `PaymentEndpointTests.An_order_with_a_payment_is_not_charged_again_under_a_new_key_and_a_draft_cannot_be_paid` | Yes: unique (tenant, order) index and its ON CONFLICT removed: the test fails (second payment stored) |
| 4 | `PaymentEndpointTests.Another_tenants_or_another_customers_order_cannot_be_paid_or_read_and_nothing_is_stored`; `RowLevelSecurityTests` (10 forced tables) | Yes: ownership check skipped in `OrderPayments`, and payments left out of RLS: each fails its test |
| 5 | `PaymentEndpointTests.A_timeout_or_server_error_records_an_unknown_outcome_shown_as_processing_with_no_event` | Asserted; deferred to gate |
| 6 | `PaymentEndpointTests.Once_a_payment_has_begun_neither_the_owner_nor_an_admin_can_cancel_the_order`, `A_cancel_that_read_the_order_before_the_payment_began_loses_at_the_store` (seam) | Yes: the store's `payment_started_at IS NULL` condition removed: the seam test fails |
| 7 | `PaymentWebhookTests.A_signed_webhook_settles_an_unknown_payment_and_announces_it_once` | Asserted; event-on-change: yes (event written without a status change: the duplicate test fails) |
| 8 | `PaymentWebhookTests.Forged_stale_tampered_or_malformed_webhooks_are_refused_and_change_nothing`, `Without_a_configured_secret_every_webhook_is_refused` | Yes: signature not compared, and timestamp tolerance ignored: each fails the test |
| 9 | `PaymentWebhookTests.Duplicate_and_out_of_order_webhooks_change_nothing_and_announce_nothing_twice`; `PaymentStateMachineTests` | Yes: event id not deduplicated: the test fails |
| 10 | `PaymentWebhookTests.A_correctly_signed_webhook_cannot_act_on_another_tenants_payment_or_a_mismatched_one`; `RowLevelSecurityTests` (11 forced tables) | Yes: provider reference not compared, and webhook events left out of RLS: each fails its test. The store's tenant filter and RLS are two layers: removing one alone does not fail the cross-tenant test |
| 11 | `OrderPaymentSagaTests` (4: every outcome by the state table with stock released on failure; duplicate and crash-before-release redelivery; disallowed events ignored without releasing a paid order's stock; own tenant only, unknown tenant or order dead-lettered); `OrderStateMachineTests`; `ServiceTenantContextTests`. 3 runs in a row: 4/4 each | Yes: message id not deduplicated, no release on redelivery, release on any failure event, processed messages left out of RLS: each fails its test |
| 12 | `PaymentReconciliationTests` (timed-out charge settled once its first check is due; review after five checks, then settled by a late webhook; a payment the provider never saw goes to review and is never charged again; an interrupted request's key is answered once settled; a timer acts in its own tenant only); `WorkflowTimerTests` | Yes: claim ignores the lease, a stale claim finishes the timer, review one check late, unfinished keys never answered: each fails its test |
| 13 | `OrderRecoveryTests` (interrupted submit finished after 5 minutes; unpaid order cancelled with stock released and `order.cancelled.v1` after 30 minutes while a begun payment's order is kept; a crashed expiry still releases; own tenant only) | Yes: no release after a crashed expiry: two tests fail. The begun-payment guard is the store's (proven in S1) |
| 14 (webhooks) | `Support/SimulatorWebhooks` signs duplicate, delayed, out-of-order, forged and stale events for the tests above | Exercised by criteria 7 to 10 |
| 14 (Stripe) | `StripePaymentProviderTests` (6: one confirmed intent in minor units keyed by the payment id; decline and processing mapped; every ambiguous answer unknown and sent once; unrepresentable amounts refused before sending; status by id or metadata search; `Stripe-Signature` verified, wrong or missing secret refused); `ConfigurationValidationTests.Host_refuses_to_start_with_a_stripe_key_that_is_not_a_test_key_and_names_only_the_key`. Fixtures follow Stripe's documented shapes; no live call is automated | Yes: Stripe `Idempotency-Key` header dropped: the charge test fails. Others asserted; deferred to gate |
| 15 (S1 part) | `PaymentEndpointTests.A_card_number_is_refused_never_echoed_and_nothing_is_stored`; `ModuleBoundaryTests`, `ModuleSchemaTests`, `OutboxAccessTests`, `SchemaOwnershipTests`, `RolePrivilegeTests`, `IsolationMatrixTests` (18 routes) include Payments | Checklist tests fail on any missing entry; card rule asserted, deferred to gate |
