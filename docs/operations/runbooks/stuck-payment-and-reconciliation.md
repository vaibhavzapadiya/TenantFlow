# Runbook: Stuck payment, reconciliation and reversed refunds

## Symptoms and impact
- `PaymentsStuck`: a payment or refund that reconciliation already checked is still unresolved 5 minutes after it was
  created (`tenantflow_payments_oldest_unresolved_age_seconds`). The customer sees `processing`; the order waits in
  `pending_payment`. After five checks the payment is `requires_review` and checks continue every 10 minutes.
- `SloPaymentResolution*`: too many payments need review.
- `RefundReversed`: a provider failed a refund after reporting it succeeded (PY-A25). The customer was **not**
  refunded, but TenantFlow shows the refund as succeeded (the state table keeps success final).

## Severity and escalation
`page`. A reversal always needs a person and is never inhibited. Escalate provider-side problems to the provider's
support with the provider reference (`sim_pi_...` / `pi_...`), never with card data (TenantFlow holds none).

## Preconditions / access required
Grafana; the provider's dashboard (Stripe test mode) when the provider is Stripe; tenant-scoped read access through the
API, or the owner role for the queries below.

## Dashboards and diagnostic queries
- Dashboard **TenantFlow / Business workflows**: *Payment outcomes*, *Unresolved after a reconciliation check*, *Oldest
  unresolved age*, *Provider call latency p95*, *Provider calls by outcome*, *Refunds reversed after success*.
- Is the provider answering? (PromQL) `sum by (provider, operation, outcome) (rate(tenantflow_payments_provider_duration_seconds_count[5m]))`
- Unknown outcomes (PromQL): `sum(increase(tenantflow_payments_attempts_total{outcome="unknown"}[30m]))`
- Reconciliation runs (TraceQL): `{ resource.service.name = "tenantflow-worker" && span.db.system = "postgresql" && span.db.statement =~ ".*payments.*" }`
- The payment's logs (LogQL): `{service_name=~"tenantflow-api|tenantflow-worker"} |~ "(?i)payment"`
- A reversal (LogQL): `{service_name="tenantflow-api"} |= "after it succeeded"` (event 5301 names the refund and payment ids)
- Which payments (SQL, owner role, ids only):
  `SELECT id, status, provider, created_at FROM payments.payments WHERE status IN ('unknown', 'requires_review') ORDER BY created_at;`

## Immediate safe mitigations
- Never charge again: a new payment for the same order is refused (`payments.already_exists`) and must stay refused.
- Provider down: nothing to do in TenantFlow; reconciliation keeps checking and settles each payment once the provider
  answers. If the provider is down for long, tell customers their payment is being confirmed.
- Reversal: refund the customer through the provider's dashboard, then record the manual action in the incident notes.

## Recovery procedure
1. Ask the provider for the payment's state with its provider reference.
2. If final, either wait for reconciliation (it runs every minute, then every 10 minutes under review) or deliver the
   provider's signed webhook again from its dashboard; both settle it exactly once.
3. A reversal: after the manual refund, document it on the order; there is no in-app transition back (ADR-0022).

## Verification
*Unresolved after a reconciliation check* returns to 0; `PaymentsStuck` resolves; the order leaves `pending_payment`;
one `payment.succeeded.v1` or `payment.failed.v1` event per payment in the outbox.

## Rollback / forward-fix
A provider adapter change that broke status reads: roll it back; payments created meanwhile settle once it is fixed.

## Customer / stakeholder communication
Tell the affected tenant's administrator which orders are pending and that no card was charged twice. For a reversal,
confirm the manual refund to the customer through the tenant.

## Follow-up actions
Count the payments that needed review; compare with the provisional SLO (ADR-0030). Revisit threat model 0003's
accepted rows with what was learned.
