# SLO: Event delivery

- Kind: Asynchronous (worker)
- Status: **provisional** target, to be confirmed by the M11 baseline (ADR-0030, Open Decision 9)
- SLI recording rules: `tenantflow:slo_error_ratio:rate<window>{slo="event_delivery"}` in
  [`deploy/observability/prometheus/rules/slo.rules.yml`](../../../deploy/observability/prometheus/rules/slo.rules.yml)
- Owner: the TenantFlow tech lead (the user); the on-call role arrives with deployment (M12)

## User-visible event and window
A message a consumer finished with (`tenantflow.messaging.consumed`): processed, a duplicate, or dead-lettered.

Window: 28 days, rolling.

## Success and latency definition
The message was processed or recognised as a duplicate. Dead-lettered is bad. Retries are not events: a retried message is counted when it finally ends. Publication delay is guarded by the stale-outbox alert (`tenantflow.outbox.oldest_pending_age`), not by this objective, because the outbox gives an age, not a per-message delay.

Latency bound: none (see below).

## Eligible traffic and exclusions
Every consumer queue. Excluded: `retried` outcomes.

## Target and error budget
Target: **99.9%** good events (provisional). Error budget: 0.1% of eligible events over 28 days (the same share of time would be about 40 minutes).

## Alert policy
Multi-window burn rates on the recording rules (ADR-0030; rules in `alerts.rules.yml`):

| Severity | Burn rate | Long window | Short window | Budget spent before it fires |
|---|---|---|---|---|
| `page` | 14.4 | 1 h | 5 min | 2 % in 1 hour |
| `ticket` | 6 | 6 h | 30 min | 5 % in 6 hours |

Both windows must burn, so a short spike pages only while it lasts and a recovered service stops paging within minutes.
The alert's `runbook` annotation names the runbook to start from.

## Dependency and capacity assumptions
RabbitMQ, the Worker's consumers, and the modules they call. A dead letter needs an operator (`docs/operations/dead-letter-replay.md`).

## SLI queries
Error ratio over a window `W` = 1 - good / total:

```promql
# total
sum(increase(tenantflow_messaging_consumed_total{outcome=~"processed|duplicate|dead_lettered"}[W]))
# good
sum(increase(tenantflow_messaging_consumed_total{outcome=~"processed|duplicate"}[W]))
```
