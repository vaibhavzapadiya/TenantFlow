# SLO: Order submission

- Kind: Synchronous (API)
- Status: **provisional** target, to be confirmed by the M11 baseline (ADR-0030, Open Decision 9)
- SLI recording rules: `tenantflow:slo_error_ratio:rate<window>{slo="order_submission"}` in
  [`deploy/observability/prometheus/rules/slo.rules.yml`](../../../deploy/observability/prometheus/rules/slo.rules.yml)
- Owner: the TenantFlow tech lead (the user); the on-call role arrives with deployment (M12)

## User-visible event and window
A customer submits an order (`POST /v1/tenants/{tenantId}/orders/{orderId}/submit`): the API reserves stock in Inventory and moves the order to `pending_payment` or `rejected`.

Window: 28 days, rolling.

## Success and latency definition
The answer is not a 5xx **and** it took at most 1 s (the `le="1"` bucket). A rejection for insufficient stock is a correct answer (it is counted by `tenantflow.orders.reservation_conflicts`, not here).

Latency bound: 1 s.

## Eligible traffic and exclusions
Every submit request. Excluded: order creation and line edits (drafts), which are cheap and not the journey.

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
PostgreSQL (orders and inventory schemas, one transaction each), the catalog snapshot read. An interrupted submit is finished by the Worker's recovery (ADR-0020), so a 5xx here can still end in a correct order.

## SLI queries
Error ratio over a window `W` = 1 - good / total:

```promql
# total
sum(rate(http_server_request_duration_seconds_count{service_name="tenantflow-api", http_request_method="POST", http_route=~"/v1/tenants/\\{[a-zA-Z]+(:[a-z]+)?\\}/orders/\\{[a-zA-Z]+(:[a-z]+)?\\}/submit/?"}[W]))
# good
sum(rate(http_server_request_duration_seconds_bucket{service_name="tenantflow-api", http_request_method="POST", http_route=~"/v1/tenants/\\{[a-zA-Z]+(:[a-z]+)?\\}/orders/\\{[a-zA-Z]+(:[a-z]+)?\\}/submit/?", le=~"1(\\.0)?", http_response_status_code!~"5.."}[W]))
```
