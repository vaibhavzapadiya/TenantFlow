# SLO: Export completion

- Kind: Asynchronous (workflow)
- Status: **provisional** target, to be confirmed by the M11 baseline (ADR-0030, Open Decision 9)
- SLI recording rules: `tenantflow:slo_error_ratio:rate<window>{slo="export_completion"}` in
  [`deploy/observability/prometheus/rules/slo.rules.yml`](../../../deploy/observability/prometheus/rules/slo.rules.yml)
- Owner: the TenantFlow tech lead (the user); the on-call role arrives with deployment (M12)

## User-visible event and window
A product export accepted by the API (`POST /v1/tenants/{tenantId}/exports/products`, 202) ends with its file ready, measured from submission to the end of the job (`tenantflow.imports_exports.job_duration`).

Window: 28 days, rolling.

## Success and latency definition
The export succeeded **and** it ended at most 250 s after it was submitted (the `le="250"` bucket). The 202 of the request is not success: a request accepted into a queue is not a completed workflow.

Latency bound: 250 s.

## Eligible traffic and exclusions
Every export that ended. Excluded: imports (their duration depends on the uploaded file), and exports still queued or running (they appear in the oldest-due-age panel and the queue alert).

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
The Worker's export runner (timer job every minute), PostgreSQL and local object storage (ADR-0024). Large catalogs take longer: the M11 workload model sets the real size.

## SLI queries
Error ratio over a window `W` = 1 - good / total:

```promql
# total
sum(increase(tenantflow_imports_exports_job_duration_seconds_count{kind="export"}[W]))
# good
sum(increase(tenantflow_imports_exports_job_duration_seconds_bucket{kind="export", outcome="succeeded", le=~"250(\\.0)?"}[W]))
```
