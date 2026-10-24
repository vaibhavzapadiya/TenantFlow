# ADR-0022: Payments module, provider port with a simulator and Stripe test mode, and the order payment saga

- Status: Accepted (2026-10-21 by the user; decision 3 amended 2026-10-21 by the user)
- Date: 2026-10-21
- Owners: TenantFlow tech lead (options recommended by Claude; sandbox, module and defaults chosen by the user on 2026-10-21; every SPEC-0006 open question accepted with the recommendation)
- Related decisions: [ADR-0002](0002-modular-monolith.md), [ADR-0008](0008-local-configuration-and-secrets.md), [ADR-0010](0010-trust-boundaries-between-user-types.md), [ADR-0011](0011-postgresql-row-level-security.md), [ADR-0020](0020-orders-and-reservations.md), [ADR-0021](0021-messaging-broker-outbox-and-consumers.md); [SPEC-0006](../../specs/0006-payment-workflow.md); blueprint sections 7, 11, 12, 14
- Closes: Open Decision 12 (sandbox payments)

## Context
M6 charges for reserved orders. A provider call is remote and can time out with an unknown result, webhooks arrive
duplicated and out of order, and the order must move on without a request waiting for it. Charging twice or losing an
order are the failures that matter. Tests must reproduce every failure mode without a network or an account.

## Decision
1. **Sandbox (Open Decision 12): both.** A provider port `IPaymentProvider` (create with idempotency key, get status,
   verify webhook) with two adapters: a deterministic in-process **simulator** (outcome scripted by the payment-method
   token; status a pure function of token and payment id, so API and Worker agree without shared memory) used by all
   automated tests and development, and a **Stripe test-mode** adapter over `HttpClient` (no SDK) tested with recorded
   fixtures. The host refuses a Stripe key that does not start with `sk_test_`. Secrets come from user-secrets or the
   environment (ADR-0008). Only provider tokens are accepted; no card data is accepted, stored or logged.
2. **Payments module** (schema `payments`) owns payments, idempotency keys, webhook events and its outbox. One payment
   per order in M6 (one row covers intent and attempt); states `processing -> succeeded | failed | unknown`,
   `unknown -> succeeded | failed | requires_review`, `requires_review -> succeeded | failed`. Amount is
   `numeric(19,4)` plus ISO currency, taken from Orders.
3. **Creation is committed steps** (as ADR-0020; step order amended by the user on 2026-10-21 after the M6 review):
   (1) Orders answers what to charge (`GetPayableAsync`, a read: owner and `pending_payment` checked, nothing written;
   a zero total is refused); (2) Payments commits the idempotency key `in_progress`, the payment and its reconciliation
   timer in one transaction; (3) Orders marks the payment begun (`BeginPaymentAsync`, conditional update; cancel and
   expiry require it unset), and if the order is no longer payable the payment fails with `order_not_payable` and
   nothing is charged; (4) Payments calls the provider with the payment id as the provider idempotency key, outside any
   transaction, then commits the outcome, the stored response and any event. A begun mark therefore always has a payment
   that reconciliation settles. After step 2 the steps ignore the client's abort. A timeout or 5xx is `unknown`, never a
   failure, and is never retried in the request.
4. **Idempotency-Key** is required, scoped `(tenant, operation, key)`, stored with a SHA-256 hash of the canonical
   request; same hash replays the stored response, a different hash is 422, an unfinished one is 409.
5. **Webhooks**: HMAC-SHA256 over `timestamp.body` (Stripe's scheme, also used by the simulator), 5-minute tolerance,
   constant-time compare, dedupe by `(provider, event_id)` in the same transaction as the effect. The tenant comes from
   our own metadata in the signed event, and the stored payment's provider reference must match, so a valid signature
   for one payment can never act on another tenant's data. Illegal transitions are recorded and ignored.
6. **Order saga as a process manager in Orders**: new states `Paid`, `PaymentFailed`, `PaymentReview`; an Orders
   consumer of the payment events (ADR-0021 pattern, `orders.processed_messages`) applies them through the state table
   and releases the reservation on failure (also on redelivery). Saga state is the order row plus its timers.
7. **Workflow timers** per module (`payments.workflow_timers`, `orders.workflow_timers`): ids, tenant, kind, due time,
   written in the state change's transaction, claimed by the Worker with leases like the outbox, not under RLS (ids
   only); the work runs in the timer's tenant under RLS. Reconciliation: after 2 minutes, every minute, 5 checks, then
   `requires_review` and every 10 minutes. Recovery: `PendingReservation` after 5 minutes is resumed; `PendingPayment`
   with no payment begun after 30 minutes is cancelled and released.

## Options considered
- Sandbox: simulator only (no real-provider shape check) vs Stripe test mode only (account, network, failure modes not
  reproducible) vs both (chosen by the user; the simulator carries the tests, Stripe proves the port fits a real API).
- Stripe client: `HttpClient` (chosen, two endpoints, no dependency) vs `Stripe.net` (more surface, new dependency).
- Workflow: process manager in Orders driven by events (chosen) vs Payments calling Orders synchronously after each
  outcome (couples modules, no recovery when the call fails) vs an orchestrator table of its own (more state to keep).
- Due work: timer tables outside RLS (chosen) vs per-tenant scans under RLS (no table, O(tenants) and a new contract).
- Attempts: one row now (chosen, one attempt per order) vs intent and attempt tables (no second attempt until M7).

## Consequences
- Easier: no double charge by construction (one provider key per payment); every failure mode has a scripted test;
  the order recovers from crashes, outages and lost requests without an operator in the common cases.
- Harder: three more forced-RLS tables and two timer tables; a second consumer in the Worker; an anonymous route that
  must stay signature-only; the simulator must keep matching the Stripe adapter's semantics.
- Follow-up: refunds and a second attempt (M7); manual resolution of `PaymentReview`; circuit breaker and rate limits
  (M11); idempotency-key and webhook-event retention; live Stripe run in CI when CI resumes.

## Validation
SPEC-0006 criteria with tests; break-it proofs for idempotency, webhook dedupe and signature checks, duplicate consumption,
the cancel/payment race guard and cross-tenant denial.

## Revisit triggers
A second payment attempt per order; refunds; a provider without idempotency keys; timer volume that needs a real
scheduler; webhook volume that needs an inbox queue.
