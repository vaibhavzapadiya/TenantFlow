# Runbook: Database down, saturation and locks

## Symptoms and impact
`DatabaseDown`: a host's `postgresql` health check fails (`tenantflow_health_status{check="postgresql"} == 0`) for a
minute. Everything that reads or writes stops: API requests answer 503/500, the outbox and timers stop, and Quartz
(whose job store is in PostgreSQL) stops firing jobs. Nothing is lost: committed work is in the database, and the
outbox and timers resume. Saturation and lock waits (no server-side metrics until M11, SPEC-0010 decision 5) show as
slow database spans and a full Npgsql pool before anything fails.

## Severity and escalation
`page`, `category="cause"`: it inhibits stale-outbox, job, API-error and SLO alerts. Escalate to whoever owns the
database (locally: you; deployed: M12) if it is not back within 15 minutes.

## Preconditions / access required
Grafana; locally `docker compose ps`. Database credentials only for the recovery steps, never pasted anywhere.

## Dashboards and diagnostic queries
- Is it reachable? (PromQL) `min by (service_name, check) (tenantflow_health_status)`
- Pool pressure (PromQL): `sum by (service_name, state) (db_client_connections_usage)` against `db_client_connections_max`
- Slow commands (PromQL): `histogram_quantile(0.95, sum by (le, service_name) (rate(db_client_commands_duration_seconds_bucket[5m])))`
- Slow or failing commands with their statements (TraceQL; statement text, never parameter values):
  `{ span.db.system = "postgresql" && (status = error || duration > 1s) }`
- Connection errors in logs (LogQL): `{service_name=~"tenantflow-api|tenantflow-worker"} |= "Npgsql"`
- Locally: `docker compose ps` shows whether the `postgres` container runs and is healthy.

## Immediate safe mitigations
- Container stopped: `docker compose up -d --wait` (locally). Deployed: the managed database's console (M12).
- Pool exhausted with the database healthy: find the slow statement in Tempo; restarting a host frees its pool but
  does not fix the query.
- A lock held for long: identify it with `pg_stat_activity` / `pg_locks` as the owner role, end only the blocking
  session, and record its query text (no parameter values) in the incident notes.

## Recovery procedure
Start or restore PostgreSQL; the hosts' readiness returns by itself. Then watch the outbox drain (*Oldest pending
outbox row age*) and the timer jobs resume (*Runs by outcome*).

## Verification
`DatabaseDown` resolves; `tenantflow_health_status{check="postgresql"}` is 1 for both hosts; outbox age falls below a
minute; the 5xx rate is zero.

## Rollback / forward-fix
A migration that caused the locks: see [failed migration](failed-migration.md). A slow query: forward-fix with an index
or a query change, tested against real PostgreSQL.

## Customer / stakeholder communication
Report the outage window. Orders submitted during it were either rejected with an error or finished by recovery;
payments were not charged twice (idempotency, ADR-0022).

## Follow-up actions
Server-side metrics (lock waits, deadlocks) are M11 (`postgres_exporter`); record whether their absence slowed this
diagnosis. Review pool size against the M11 baseline.
