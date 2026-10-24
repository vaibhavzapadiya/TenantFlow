# M6 — Payment workflow

**Goal:** a customer pays for a reserved order, and TenantFlow charges at most once whatever the client, the network or
the provider does; every unknown outcome is settled later, and no order is lost or stuck.

**Gate (roadmap):** timeout/unknown outcomes and duplicate or out-of-order webhooks recover safely.

Spec: [SPEC-0006](../specs/0006-payment-workflow.md) (Implemented). Decision:
[ADR-0022](../architecture/decisions/0022-payments-provider-port-and-order-payment-saga.md) (Accepted 2026-10-21:
simulator plus Stripe test mode behind one port, a Payments module, idempotency keys, signed webhooks, an order process
manager, workflow timers). Open Decision 12 (sandbox payments) is closed by it.

## 1. What was built

```text
src/
├── BuildingBlocks/Abstractions/Workflow    TimerDecision
├── BuildingBlocks/Messaging/Timers (new)   WorkflowTimer + mapping, WorkflowTimerSql (schedule in the state change's
│                                           transaction), WorkflowTimerStore (lease claim, complete, reschedule),
│                                           WorkflowTimerRunner (scope per timer, tenant entered, backoff, lease skip)
├── Hosts/Api/Endpoints/V1                  Tenants/Payments (POST .../orders/{id}/payments, GET .../payments/current),
│                                           Payments/PaymentWebhookEndpoints (anonymous, signature only)
├── Hosts/Worker/Messaging                  OrderPaymentEventsConsumerService, WorkflowTimerService
└── Modules/
    ├── Payments (new)                      Domain: Payment, state table, errors. Contracts: payment.succeeded/failed/
    │                                       review_required.v1. Application: PaymentService, PaymentWebhookService,
    │                                       PaymentReconciler, provider port and directory. Infrastructure: PaymentStore,
    │                                       simulator, Stripe adapter (HttpClient, no SDK), webhook readers, migrations
    ├── Orders                              paid / payment_failed / payment_review states; IOrderPayments contract;
    │                                       PaymentEventConsumer (the saga); OrderRecovery; OrderReservationStep;
    │                                       payment_started_at, processed_messages, workflow_timers
    └── IdentityAccess                      ServiceTenantContext (the Worker's per-message or per-timer tenant)
```

Tables: `payments.payments`, `payments.idempotency_keys`, `payments.webhook_events`, `orders.processed_messages`
(forced RLS, 12 forced tables in all); `payments.outbox_messages`, `payments.workflow_timers`, `orders.workflow_timers`
(ids only, outside RLS, like the M5 outbox).

## 2. Architectural reasoning

- **One payment per order, one provider key per payment.** The payment id is the provider's idempotency key, and a
  unique `(tenant_id, order_id)` index refuses a second payment. "No double charge" is a property of the data, not of
  careful code.
- **Unknown is a state, not an error.** A timeout or 5xx is `unknown` (shown as `processing`). Retrying the charge is
  never the fix: reconciliation asks the provider, and after 5 checks the payment needs review. Charging twice is worse
  than waiting.
- **Committed steps, no remote call in a transaction (rule 3).** First Orders is read. Then the key, the payment and
  its reconciliation timer commit together. Then Orders marks the payment begun, so cancel and expiry lose. Only then
  is the provider called. The review moved the begin mark after the payment commit, so a marked order always has a
  payment that reconciliation settles.
- **Arrival order decides nothing.** Webhooks, the create call's answer, reconciliation and events all go through two
  state tables (payment and order). Late, duplicate or contradicting news is recorded and ignored.
- **A process manager, not an orchestrator table.** The order row is the saga's state, and payment events move it
  through the order's state table. Deadlines live in workflow timers written in the same transaction as the state they
  guard.
- **Timers outside RLS, work inside it.** A timer holds only ids, the tenant and a due time, and is read across
  tenants with a lease. Its handler runs in a scope entered for that tenant, and every store filters by tenant again
  under RLS. A forged timer tenant finds nothing (tested).
- **Two adapters, one port.** The simulator scripts every failure mode deterministically. Its outcome is a function of
  the token and the payment id, so the API and the Worker agree without shared memory. The Stripe adapter proves the
  port fits a real API. Both send Stripe-shaped, Stripe-signed webhooks through one parser.

## 3. Security considerations

- **No card data:** only provider tokens are accepted. Card-shaped digit runs are refused, even split by underscores,
  and never echoed. Nothing about a payment method appears in logs or responses.
- **Webhooks:** the anonymous route is authenticated by the signature only: HMAC-SHA256 over the raw bytes,
  constant-time compare, ±5-minute tolerance. With no secret configured, every webhook is refused. Nothing is read or
  written before verification. The tenant comes from TenantFlow's own signed metadata, and the stored payment must
  match on provider and provider reference. An unknown tenant and an unknown payment get the same 404.
- **Idempotency keys:** keys are scoped to the tenant and the hash binds the caller's membership, so another member can
  never replay your response (residual: they can learn that a key exists, see §5).
- **Test mode only:** a Stripe key that is not `sk_test_` stops the host at start, naming the key, never the value. The
  simulator takes payments only where `Payments:Simulator:Enabled` is set.
- **Worker identity:** `ServiceTenantContext` holds one tenant, no membership and no permission, so owner and
  permission checks still fail closed in background work.

## 4. Verification

Fresh evidence for the gate (2026-10-21) at `81fc3a7`:

| Command | Result |
|---|---|
| `./scripts/verify.ps1 -Database` | all checks passed; Tenants 77/77, IdentityAccess 68/68, Catalog 7/7, Architecture 7/7, Orders 6/6, Inventory 5/5, Payments 10/10, Worker 13/13, Api 127/127, Database 217/217; 0 warnings |
| `./scripts/progress.ps1 -Verify` (CI test command with coverage) | quality run passed for `81fc3a7`: 0 warnings, 320/320 non-database tests, Compose valid. Line coverage of the new code is low here (Payments.Application 14%, Payments.Infrastructure 33%, BuildingBlocks.Messaging 40%) because the coverage run excludes the database tests, where it is exercised; Payments.Domain 81% |

**Break-it checks re-run for the gate.** In each one a fault was injected, the test failed at an assertion, and the
fault was restored and the code rebuilt:

- **Unknown outcome:** treating a timeout as a decline makes the timeout test fail. Reconciliation never escalating
  makes the five-checks review test fail.
- **Duplicate webhooks:** without event-id dedupe, the duplicate and out-of-order test fails.
- **Cross-customer:** skipping Orders' ownership check makes the cross-tenant and cross-customer payment test fail.
- **Deferred proofs from S1, S5 and the review fixes:**
  - a card number split by underscores slips through: the validator test fails;
  - a signed foreign event is refused: the metadata test fails;
  - a live Stripe key is accepted: the host-start test fails;
  - the simulator is allowed everywhere: the host-start test fails;
  - the signature skips a byte-order mark: the Stripe reader test fails.

Not proven by a fault, with the reason:
- *Reconciling with the stored provider:* the fault would make a real call to `api.stripe.com`. The test asserts the
  behaviour instead.
- *Ignoring the client's abort after commit:* there is no deterministic seam for an aborted request mid-call.

Per-slice proofs (28) are in the spec's Verification table and the tracker. They cover idempotency replay, hash, caller
binding and in-progress keys, one payment per order, the cancel guard, ownership, RLS on every new table, webhook
signature, timestamp, dedupe and reference match, event only on change, saga dedupe and release rules, timer leases,
review escalation, unfinished keys answered, release after a crashed expiry, and the Stripe idempotency header.

**A flaky test, reported honestly:** `WorkerHostTests.Host_refuses_to_start_without_a_broker_password_and_names_the_key`
failed in the first full gate run of M6. It passed in the rerun and 3 times in a row on its own. The first run's output
was redirected with `*>`, which turned the test's stderr into a terminating PowerShell error, so its diagnostic text
was lost. That is the same trap as with `dev.ps1`, and why the pending `progress.ps1` fix matters. It still fails only
inside full, parallel runs.

**Not verified:** a live Stripe test-mode run (manual and opt-in, see `docs/operations/local-environment.md`; the
adapter is tested against Stripe's documented shapes). The Worker processes end to end through the broker (the saga and
timers are tested the way the Worker wires them). Multi-instance Workers (lease logic is tested, two processes are
not). CI (on hold).

## 5. Trade-offs and assumptions

- **One attempt per order:** a decline fails the order, and a second attempt with another method is M7 work.
- **`requires_review` has no tool yet:** a payment that stays unknown keeps being checked every 10 minutes, and its
  order sits in `payment_review` until an admin tool (M7 or later) resolves it.
- **A crash right after the payment commits:** if the begin mark or the provider call never happens, the payment
  reaches review. It is never charged blindly, and the order may still expire.
- **Durations are constants** (`OrderDeadlines`, `PaymentReconciliation`). They become configuration when a tenant or
  environment needs another value.
- **Deferred by your choice at the gate:**
  - idempotency keys per member (a schema and spec change);
  - an amount check on webhook and search results;
  - a tenant check on the Stripe metadata search;
  - `requires_action` (3-D Secure) mapped to failed;
  - UGX/ISK minor units (check Stripe's docs);
  - a rate limit on the anonymous webhook route (M9).
- Idempotency keys and webhook events are kept for now; retention comes later.

## 6. Mentor exercises

1. Move the reconciliation timer insert out of `PaymentStore.TryStartAsync` into a second transaction. What does a
   crash between the two leave, and which test would notice?
2. Make reconciliation call `CreateAsync` again when the provider has no record of a payment. Why is that tempting,
   when is it safe, and what does Stripe's idempotency key change about the answer?
3. Remove `orders.processed_messages` and rely on the state table alone to ignore duplicates. Which duplicate is still
   harmful? (Hint: the release on `payment_failed`.)
4. Run two Workers with a 50-timer batch and a provider that takes 10 s. Draw what happens to timer 40.
5. Add a second payment attempt per order. Which unique index, state, event and saga transition change?

## 7. Interview talking points

- "How do you never charge twice?" One payment per order (unique index), its id as the provider's idempotency key,
  and a timeout treated as unknown and reconciled, never retried with a new key.
- "How do you handle webhooks?" Signature over the raw bytes, a timestamp window, dedupe by event id in the effect's
  transaction, the tenant from your own signed metadata cross-checked against stored data, and state tables so arrival
  order does not matter.
- "How does a saga recover?" Persisted state (the order row) plus persisted deadlines (timers written with the state
  change), run by leased workers in the tenant's scope, every step idempotent.
- "How do you test payment failure modes?" A deterministic simulator whose tokens script success, decline, timeout
  with each outcome, unknown and 5xx, plus signed duplicate, stale, forged and out-of-order webhooks on demand.

## 8. Exit checklist

- [x] Timeout and unknown outcomes recover: settled by reconciliation or escalated to review, never charged again (`PaymentReconciliationTests`, proof re-run)
- [x] Duplicate and out-of-order webhooks recover safely (`PaymentWebhookTests`, proof re-run)
- [x] Every state reachable only through the state tables (`PaymentStateMachineTests`, `OrderStateMachineTests`, saga and webhook tests)
- [x] SPEC-0006 Implemented: all 15 criteria have verification rows
- [x] Reviews: tenant isolation (2 low), code (2 high, 2 medium, 4 low), security (2 medium, 6 low); findings 1 to 9 fixed in `81fc3a7` by your choice, the rest deferred (§5)
- [x] ADR-0022 accepted by you on 2026-10-21; Open Decision 12 closed
- [x] ADR-0022 decision 3 amended to the reviewed step order (begin mark after the payment commit), confirmed by you on 2026-10-21
- [ ] **You:** review this guide and the M6 commits on branch `m6-payments`, and merge into `main` when satisfied
- [ ] **You:** approve the start of M7 (subscriptions and refunds)
