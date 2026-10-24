# SLO: Catalog read

- Kind: Synchronous (API)
- Status: **provisional** target, to be confirmed by the M11 baseline (ADR-0030, Open Decision 9)
- SLI recording rules: `tenantflow:slo_error_ratio:rate<window>{slo="catalog_read"}` in
  [`deploy/observability/prometheus/rules/slo.rules.yml`](../../../deploy/observability/prometheus/rules/slo.rules.yml)
- Owner: the TenantFlow tech lead (the user); the on-call role arrives with deployment (M12)

## User-visible event and window
An authenticated `GET` of a tenant's product list, search or one product (`/v1/tenants/{tenantId}/products` and `/v1/tenants/{tenantId}/products/{productId}`), answered by the API.

Window: 28 days, rolling.

## Success and latency definition
The answer is not a 5xx **and** it took at most 0.5 s (the `le="0.5"` bucket of `http.server.request.duration`). A 4xx is the caller's (bad token, unknown product) and counts as good when fast.

Latency bound: 500 ms.

## Eligible traffic and exclusions
Every request to those two routes. Excluded: health probes and platform routes (other routes), and requests that never reached routing (no `http_route`).

## Target and error budget
Target: **99.5%** good events (provisional). Error budget: 0.5% of eligible events over 28 days (the same share of time would be about 202 minutes).

## Alert policy
Multi-window burn rates on the recording rules (ADR-0030; rules in `alerts.rules.yml`):

| Severity | Burn rate | Long window | Short window | Budget spent before it fires |
|---|---|---|---|---|
| `page` | 14.4 | 1 h | 5 min | 2 % in 1 hour |
| `ticket` | 6 | 6 h | 30 min | 5 % in 6 hours |

Both windows must burn, so a short spike pages only while it lasts and a recovered service stops paging within minutes.
The alert's `runbook` annotation names the runbook to start from.

## Dependency and capacity assumptions
PostgreSQL (catalog schema, row-level security per request), the identity provider's signing keys (cached). No capacity model exists yet: the M11 baseline measures one.

## SLI queries
Error ratio over a window `W` = 1 - good / total:

```promql
# total
sum(rate(http_server_request_duration_seconds_count{service_name="tenantflow-api", http_request_method="GET", http_route=~"/v1/tenants/\\{[a-zA-Z]+(:[a-z]+)?\\}/products/?(\\{[a-zA-Z]+(:[a-z]+)?\\})?"}[W]))
# good
sum(rate(http_server_request_duration_seconds_bucket{service_name="tenantflow-api", http_request_method="GET", http_route=~"/v1/tenants/\\{[a-zA-Z]+(:[a-z]+)?\\}/products/?(\\{[a-zA-Z]+(:[a-z]+)?\\})?", le=~"0.5(\\.0)?", http_response_status_code!~"5.."}[W]))
```
