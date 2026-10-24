# Runbook: API errors and elevated error rates

## Symptoms and impact
`ApiServerErrors` (more than 5% of requests answer 5xx for 10 minutes) or an SLO burn alert for catalog reads or order
submission. Users see `500`/`503` problem details; order submissions may be interrupted (the Worker's recovery finishes
them, ADR-0020). A 4xx rise is not this runbook: 401/403/404 spikes are denials (see
[suspected cross-tenant access](suspected-cross-tenant-access.md)).

## Severity and escalation
`page`. If `DatabaseDown` or `BrokerDown` also fires, follow that runbook first: these alerts are then inhibited.
Escalate to the tech lead if the cause is not found within 30 minutes.

## Preconditions / access required
Grafana (local `admin`), Prometheus, Loki and Tempo through Grafana's Explore. No database access is needed to diagnose.

## Dashboards and diagnostic queries
- Dashboard **TenantFlow / Golden signals**: *Server errors (5xx) by route*, *Error ratio*, *Duration p95*, saturation row.
- Which routes fail (PromQL):
  `sum by (http_route, http_request_method, http_response_status_code) (rate(http_server_request_duration_seconds_count{service_name="tenantflow-api", http_response_status_code=~"5.."}[5m]))`
- Failing requests and their traces (TraceQL):
  `{ resource.service.name = "tenantflow-api" && span.http.response.status_code >= 500 }`
- Error logs with their trace ids (LogQL): `{service_name="tenantflow-api"} | severity_text=~"Error|Critical"`
- Slow or failing database commands inside a failing trace (TraceQL):
  `{ resource.service.name = "tenantflow-api" && span.db.system = "postgresql" && (status = error || duration > 500ms) }`

Open a failing trace from Tempo; the **Logs for this span** link shows its logs. Third-party exception messages are
redacted in exported logs (ADR-0031): the type and stack identify the fault; reproduce locally for the text.

## Immediate safe mitigations
- A dependency is down: follow its runbook.
- One route fails after a release: roll back the release (M12 path) or disable the client calling it.
- Saturation (CPU, thread-pool queue, Npgsql pool at max): restart one API instance at a time; scale out where deployed.

## Recovery procedure
Fix the cause the trace shows (dependency, bug, configuration), deploy, and watch the 5xx rate fall.

## Verification
*Server errors (5xx) by route* returns to zero; `ApiServerErrors` and the SLO burn alerts resolve; a request to the
failing route answers 2xx.

## Rollback / forward-fix
Roll back the API image (M12 pipeline) when a release caused it; otherwise forward-fix with a test that reproduces the
failing request.

## Customer / stakeholder communication
If the SLO budget burned (fast burn paged), tell affected tenants' administrators the window and the affected routes;
never name other tenants.

## Follow-up actions
A blameless review for any page (blueprint section 19); add a test for the failing path; check the SLO's provisional
target against what happened (ADR-0030).
