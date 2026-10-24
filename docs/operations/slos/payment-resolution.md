# SLO: Payment outcome resolution

- Kind: Asynchronous (workflow)
- Status: **provisional** target, to be confirmed by the M11 baseline (ADR-0030, Open Decision 9)
- SLI recording rules: `tenantflow:slo_error_ratio:rate<window>{slo="payment_resolution"}` in
  [`deploy/observability/prometheus/rules/slo.rules.yml`](../../../deploy/observability/prometheus/rules/slo.rules.yml)
- Owner: the TenantFlow tech lead (the user); the on-call role arrives with deployment (M12)

## User-visible event and window
A payment reaches a final outcome (`succeeded` or `failed`), from the provider's answer, a webhook or reconciliation (SPEC-0006 criterion 12).

Window: 28 days, rolling.

## Success and latency definition
The payment reaches `succeeded` or `failed` without needing review. A payment that needs review (`requires_review`: five checks without a final answer) is a bad event, even if a late answer settles it later.

Latency bound: the reconciliation window (first check after 2 min, then 4 more a minute apart: about 7 min).

## Eligible traffic and exclusions
Every payment that reaches `succeeded`, `failed` or `requires_review` (`tenantflow.payments.attempts`). Excluded: `unknown`, which is an intermediate state. A payment settled after review counts twice (once bad, once good); with review rare, the error is small and errs towards a better ratio, which the M11 baseline must check.

## Target and error budget
Target: **99.0%** good events (provisional). Error budget: 1.0% of eligible events over 28 days (the same share of time would be about 403 minutes).

## Alert policy
Multi-window burn rates on the recording rules (ADR-0030; rules in `alerts.rules.yml`):

| Severity | Burn rate | Long window | Short window | Budget spent before it fires |
|---|---|---|---|---|
| `page` | 14.4 | 1 h | 5 min | 2 % in 1 hour |
| `ticket` | 6 | 6 h | 30 min | 5 % in 6 hours |

Both windows must burn, so a short spike pages only while it lasts and a recovered service stops paging within minutes.
The alert's `runbook` annotation names the runbook to start from.

## Dependency and capacity assumptions
The payment provider (simulator locally, Stripe test mode), the Worker's timer job (every minute) and PostgreSQL. `tenantflow.payments.oldest_unresolved_age` shows the oldest open one.

## SLI queries
Error ratio over a window `W` = 1 - good / total:

```promql
# total
sum(increase(tenantflow_payments_attempts_total{outcome=~"succeeded|failed|requires_review"}[W]))
# good
sum(increase(tenantflow_payments_attempts_total{outcome=~"succeeded|failed"}[W]))
```
