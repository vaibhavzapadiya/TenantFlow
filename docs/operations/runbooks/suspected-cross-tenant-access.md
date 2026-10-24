# Runbook: Suspected cross-tenant access

## Symptoms and impact
`CrossTenantProbing`: more than 10 requests in 2 minutes were refused with reason `not_a_member` (a signed-in user
named a tenant they do not belong to), sustained for 3 minutes. The refusals worked: this alert is about the attempt.
A real exposure would show as data of one tenant in another tenant's response, which tenant isolation (RLS, query
filters, cross-tenant tests) is built to prevent (AGENTS.md rule 1, threat models 0001 and 0006).

## Severity and escalation
`page`, never inhibited. Escalate to the tech lead as a security incident if any denied request was later answered
2xx for the same caller and tenant, or if the probing continues after the account is disabled.

## Preconditions / access required
Grafana (Loki, Tempo); the identity provider's admin console to disable an account; the audit log (`GET
/v1/platform/audit`) as a platform administrator.

## Dashboards and diagnostic queries
- Dashboard **TenantFlow / Business workflows**: *Authentication and authorization denials*.
- The spike (PromQL; scheme and reason only, never a tenant or subject):
  `sum by (scheme, reason) (increase(tenantflow_auth_denials_total[10m]))`
- Which routes and when: the denial logs (LogQL) `{service_name="tenantflow-api"} |= "Access denied" |= "not_a_member"`;
  each line's `trace_id` opens the request (route, status 404, time). By design neither the log, the trace nor the
  metric names the caller or the tenant (DP-A01): identify the caller from the identity provider's login and token
  events for the same time window, and the tenants from the request paths in the API's own access log where one is
  kept (none locally).
- Any success for the same caller (TraceQL), by the trace attributes found above:
  `{ resource.service.name = "tenantflow-api" && span.http.response.status_code < 400 && span.http.route =~ "/v1/tenants/.*" }`
- Platform activity in the same window: `GET /v1/platform/audit` (support grants opened, tenant changes).

## Immediate safe mitigations
Disable the caller's account in the identity provider (tokens expire within their lifetime); revoke any support grant
opened in the window. Do not delete logs or traces: they are the evidence.

## Recovery procedure
Confirm from the traces that every cross-tenant request was refused. If any was not, treat it as a data exposure:
preserve evidence, identify the data returned, and follow the privacy obligations in `docs/privacy`.

## Verification
The denial rate returns to its background; no 2xx for the caller on another tenant's routes; the alert resolves.

## Rollback / forward-fix
A real bypass: roll back the release that introduced it, then forward-fix with a cross-tenant denial test that fails
first.

## Customer / stakeholder communication
Probing that was refused: no customer notice. A confirmed exposure: notify the affected tenant through the agreed
channel, naming only their data.

## Follow-up actions
Review threat models 0001 and 0006 (accepted rows BW-A07, BW-A11, BW-A20) against what happened; consider rate limiting
per subject (M11/M12).
