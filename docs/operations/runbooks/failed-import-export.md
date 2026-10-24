# Runbook: Failed import or export and storage problems

## Symptoms and impact
`ImportExportBacklog` (a due job waited more than 10 minutes), `ImportExportFailures` (jobs failed), or
`SloExportCompletion*`. Tenants see jobs stuck in `queued`/`running` or ending `failed`; an export file is not ready.

## Severity and escalation
`ticket`; the SLO fast burn pages. Escalate storage problems (disk full, permissions) to whoever runs the host.

## Preconditions / access required
Grafana; on the Worker host, read access to the storage root (`Storage:Root`, ADR-0024; files are mode 0600).

## Dashboards and diagnostic queries
- Dashboard **TenantFlow / Business workflows**: *Oldest due job age*, *Due jobs*, *Job duration p95*, *Jobs and failed rows*.
- Failures by kind (PromQL): `sum by (kind, outcome) (increase(tenantflow_imports_exports_jobs_total[1h]))`
- Is the timer job running? (PromQL): `sum by (outcome) (increase(tenantflow_scheduling_runs_total{job="timers.imports_exports"}[15m]))`
- Runner errors (LogQL): `{service_name="tenantflow-worker"} |= "export" | severity_text=~"Error|Critical"`
- The job's trace (TraceQL): `{ resource.service.name = "tenantflow-worker" && span.db.system.name = "postgresql" && span.db.query.text =~ ".*imports_exports.*" }`

## Immediate safe mitigations
- Worker or timer job down: see [hosts and scheduled jobs](hosts-and-scheduled-jobs.md); due jobs run when it returns.
- Storage full: free space by deleting expired files only (the retention run and `imports_exports.storage_sweep` do
  this); never delete a file of an active job.

## Recovery procedure
Fix the cause; due jobs are retried by their timers; a failed job stays failed and the tenant submits a new one
(an idempotency key returns the old one, so a new key is needed).

## Verification
*Oldest due job age* falls under a minute; new exports succeed within the SLO's 250 s; the alerts resolve.

## Rollback / forward-fix
A runner bug: roll back the Worker; imports already applied are not undone (rows are per-line results).

## Customer / stakeholder communication
Tell the tenant which jobs failed and that they can submit them again.

## Follow-up actions
Check failed-row counts for a data-shape problem; feed large-catalog durations into the M11 workload model.
