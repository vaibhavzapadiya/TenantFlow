# Runbook: Host down, telemetry missing, and scheduled jobs

## Symptoms and impact
- `ApiTelemetryMissing` / `WorkerTelemetryMissing` (cause): no runtime metrics from the API, or no heartbeat from the
  Worker, for 5 minutes. Either the host is down or the collector path is broken (then the host still serves, but
  nothing can be seen).
- `ScheduledJobFailing`: a job failed 3 times in 15 minutes. `ScheduledJobLate`: fires start over 2 minutes late.
- `RetentionRunFailed`: the daily retention run failed; expired personal data was not removed (DP-A12).
With the Worker down: no outbox publishing, no consumers, no timers, no retention. Nothing is lost; it all resumes.

## Severity and escalation
`page`. The telemetry alerts are causes and inhibit the symptoms they produce (stale outbox, job alerts). Escalate a
retention failure that repeats on the next day to the tech lead (privacy obligation).

## Preconditions / access required
Grafana; the host's process or container; `docker compose ps` locally.

## Dashboards and diagnostic queries
- Is the host alive? `curl http://localhost:5081/health/live` (Worker), `:5080` (API).
- Does the collector receive? (PromQL) `sum by (service_name) (rate(dotnet_process_cpu_time_seconds_total[5m]))`
- Worker heartbeats (PromQL): `sum(rate(tenantflow_worker_heartbeats_total[5m]))`
- Jobs by outcome and lateness (PromQL):
  `sum by (job, outcome) (increase(tenantflow_scheduling_runs_total[15m]))`,
  `histogram_quantile(0.95, sum by (le, job) (rate(tenantflow_scheduling_lateness_seconds_bucket[15m])))`
- The failure (LogQL): `{service_name="tenantflow-worker"} |= "failed; retrying on the next fire"` (events 5204, 5208
  name the job or module; the exception type and stack follow)
- Retention (LogQL): `{service_name="tenantflow-worker"} |= "Retention run"`

## Immediate safe mitigations
- Host down: start it (`dotnet run --project src/Hosts/TenantFlow.Worker` locally); several Worker instances are safe
  (clustered Quartz, leased timers).
- Collector down while hosts serve: `docker compose --profile observability up -d --wait`; telemetry for the gap is lost (SPEC-0001).
- A failing job: read the exception; a database or broker cause has its own runbook.

## Recovery procedure
Once the host runs, jobs fire on their next schedule; a fire missed while no Worker ran is made once, at once
(ADR-0023). A failed retention run is finished by the next one; to run it sooner, restart the Worker after 03:15 UTC
only if the next run would be too late for a deletion obligation.

## Verification
Heartbeats and runtime metrics return; *Runs by outcome* shows `succeeded`; lateness falls under the schedule; the
alerts resolve.

## Rollback / forward-fix
A release that crashes the host: roll it back. A job that fails after a release: forward-fix with a test of the job.

## Customer / stakeholder communication
Usually none; delayed exports or notifications are covered by their own runbooks.

## Follow-up actions
Repeated lateness: check Quartz's thread pool and job durations against the M11 baseline.
