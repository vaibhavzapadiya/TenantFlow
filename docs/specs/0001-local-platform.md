# SPEC-0001: Local platform and host foundation

- Status: Implemented (2026-10-19; approved by the user the same day)
- Milestone: M1
- Owner: TenantFlow tech lead (the user)
- Related: blueprint sections 5, 14, 16, 18, 20, 26; ADR-0002, ADR-0007, ADR-0008; roadmap M1 gate interpretation

## Outcome
A developer can start TenantFlow's local dependencies with one documented command, run the API and
worker hosts, and see whether each host is alive and ready. Both hosts emit logs, metrics, and traces
in which every log written inside an operation links to that operation's trace. This is the
operational foundation every later milestone builds on.

## Acceptance criteria

**Local stack** (`scripts/dev.ps1` and `scripts/dev.sh`)
1. Given Docker is reachable, when `dev up` runs, then PostgreSQL reports healthy within 60 seconds and
   listens on `127.0.0.1` at the port set in `.env` (default 55432).
2. Given no `deploy/compose/.env`, when `dev up` runs, then the file is created from `.env.example` and
   `git check-ignore` confirms it is ignored.
3. Given the Docker daemon is unreachable, when `dev up` runs, then it exits non-zero, prints the fix
   from `docs/operations/local-environment.md`, and starts nothing.
4. Given the stack holds data, when `dev reset` runs and the user confirms, then the volumes are removed
   and the next `up` starts with an empty database. Without confirmation, nothing is removed.
5. Every port published by Compose binds to `127.0.0.1`, and Grafana has anonymous access disabled.

**Health** (both hosts)
6. Given PostgreSQL is unreachable, when `/health/live` is called on the API, then it returns 200; when
   `/health/ready` is called, then it returns 503.
7. Given PostgreSQL is reachable, when `/health/ready` is called on the API, then it returns 200.
8. Health responses contain only the status word. They never include exception text, host names,
   connection details, or dependency names.
9. Given `ConnectionStrings:TenantFlow` is missing, when the API starts, then startup fails with an
   error that names the key and contains no configuration values.
10. The worker serves `/health/live` and `/health/ready` with 200, and any other path returns 404.

**Worker heartbeat**
11. Given a heartbeat interval, when that interval elapses on the clock, then the worker records exactly
    one heartbeat: one span, one log line, and one increment of `tenantflow.worker.heartbeats`.
12. Given the worker is stopping, when cancellation is signalled, then the heartbeat loop ends within
    5 seconds without an unhandled exception.
13. Given a single heartbeat tick throws, then the error is logged and the next tick still runs.

**Telemetry**
14. Given a request to the API, then its exported span carries `service.name=tenantflow-api`,
    `service.namespace=tenantflow`, `service.version`, and `deployment.environment.name`. The worker
    reports the same attributes, with `service.name=tenantflow-worker`.
15. Given a log written while handling a request, then the log record's trace and span IDs equal the
    request span's. The same holds for a heartbeat log and its heartbeat span.
16. Given the collector is unreachable, when requests are sent to the API, then they succeed with their
    normal status codes.
17. Given the observability profile is running and both hosts have run, when `dev smoke` runs, then it
    finds API HTTP metrics and the worker heartbeat counter in Prometheus, an API trace in Tempo, and
    logs with trace IDs from both services in Loki. Otherwise it exits non-zero and lists what is missing.

## Invariants
- No credential is ever stored in a tracked file (ADR-0008).
- Liveness never depends on an external dependency.
- A telemetry outage never changes the result of a request or a job.
- Health endpoints never reveal internal details.

## Contract

| Host | Endpoint | Responses | Authorization |
|------|----------|-----------|---------------|
| API (`http://localhost:5080`) | `GET /health/live`, `GET /health/ready` | 200 or 503, `text/plain` status word | Anonymous: platform probes need it, and only the status word is exposed. Detailed diagnostics are protected later (blueprint section 16; M9/M10) |
| Worker (`http://localhost:5081`) | Same | Same | Same |

**Configuration**

| Key | Host | Rule |
|-----|------|------|
| `ConnectionStrings:TenantFlow` | API | Required. User-secrets in Development, environment or secret manager elsewhere |
| `Worker:HeartbeatInterval` | Worker | Default 30 seconds. Validated between 1 second and 1 hour |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Both | Standard OpenTelemetry variable. Default `http://localhost:4317` |

**Telemetry names:** activity source `TenantFlow.Worker`; meter `TenantFlow.Worker`; counter
`tenantflow.worker.heartbeats` (unit `{heartbeat}`). Metric labels stay low-cardinality, with no tenant
IDs (blueprint section 16).

## Data
No application schema yet. Compose creates database `tenantflow`, user `tenantflow`, and the named
volume for PostgreSQL data. Separate migration and application database roles (least privilege,
blueprint section 13) arrive in M3 with the first migration.

## Failure modes

| Failure | Expected behaviour |
|---------|--------------------|
| PostgreSQL down | API readiness returns 503 and liveness returns 200. The readiness check runs one `SELECT 1` with a 2-second timeout and no retries, so probes cannot cause a retry storm (blueprint section 14) |
| Collector down | Exporters drop data and log warnings; requests and jobs are unaffected |
| Docker daemon down | `dev up` fails fast with the fix (criterion 3) |
| Port already in use | Compose fails; the script shows the conflicting port and points to `.env` |
| Missing or invalid configuration | The host fails at startup (criterion 9) |
| Heartbeat error | Logged; the loop continues (criterion 13) |
| Shutdown | The heartbeat loop stops within 5 seconds (criterion 12) |

## Out of scope
Migrator host, EF Core, and Testcontainers (M3). Dockerfiles and container images (M12). Dashboards,
alerts, and SLOs (M10). Log redaction (M9). A trace spanning both processes (M5).

## Resolved questions
1. **PostgreSQL major version:** 17, chosen by the user on 2026-10-19. It matches the PostgreSQL
   already installed on the development machine. Revisit in M3 if native `uuidv7()` (PostgreSQL 18)
   becomes the preferred way to generate time-ordered IDs.
2. **Heartbeat interval default:** 30 seconds, confirmed by the user on 2026-10-19.

## Slices

| Slice | Delivers | Criteria |
|-------|----------|----------|
| S1 | Compose core, `.env.example`, dev scripts (`up`, `down`, `reset`, `status`), CI compose validation, local-environment doc | 1–5 |
| S2 | Observability building block (health part), API live/ready, options validation, PostgreSQL readiness check, user-secrets wiring | 6–9 |
| S3 | Worker host, heartbeat service, worker health endpoints | 10–13 |
| S4 | OpenTelemetry tracing, metrics, and logs in both hosts; JSON console logs | 14–16 |
| S5 | Observability profile (collector, Prometheus, Loki, Tempo, Grafana) and `dev smoke` | 5, 17 |

S1 goes first because it carries the riskiest assumption: the Docker engine in WSL, port forwarding
to Windows, and the port clash with the local PostgreSQL service.

## Verification
Filled in during implementation.

| Criterion | Test or check | Proven to fail without the change |
|-----------|---------------|------------------------------------|
| 1 | `dev.ps1 up` waits on the PostgreSQL health check (`--wait --wait-timeout 90`); `compose port` shows `127.0.0.1:55432`; a TCP connect from Windows succeeds | Observed 2026-10-19: healthy after 43 s including the first image pull |
| 2 | `dev.ps1 up` creates `.env` from the example; `git status --ignored` lists it as ignored, matched by `.gitignore:30` | Observed 2026-10-19 |
| 3 | `dev.ps1 up` with the engine down exits 1, prints the fix, creates no container and no `.env` | Observed 2026-10-19 (engine down on this machine) |
| 4 | Marker table created; `reset` answered `no` keeps it (1 row); `reset -Yes` removes the volume; after `up` the table is absent | Observed 2026-10-19 |
| 5 | `dev.ps1 validate` (CI step): every published port binds to `127.0.0.1` (7 ports with the observability profile); `dev.ps1 smoke`: Grafana answers 401 without credentials | Yes: failed with a port published on all interfaces, passed after binding to 127.0.0.1; also fails for a service behind a profile (temporary probe service) |
| 6 | `HealthEndpointTests.Live_returns_200_with_only_the_status_word_while_PostgreSQL_is_unreachable`, `Ready_returns_503_with_only_the_status_word_while_PostgreSQL_is_unreachable`; also observed against the real database: ready went 200 → 503 → 200 as PostgreSQL stopped and started, live stayed 200 | Yes: liveness running all checks fails the live test; the check without the `ready` tag fails the ready test |
| 7 | Observed 2026-10-19: `dotnet run` the API after `dev up`; `/health/ready` returns 200 `Healthy`. Automated with Testcontainers in M3 (Open Decision 21) | Manual check only |
| 8 | Both health tests assert the body is exactly the status word, as `text/plain` | Yes: a writer that appends check descriptions fails the ready test |
| 9 | `ConfigurationValidationTests.Host_refuses_to_start_without_the_connection_string_and_names_only_the_key`; also observed when the API started without its user-secret | Yes: removing `ValidateOnStart` fails the test |
| 10 | `WorkerHostTests.Health_endpoints_return_200_with_only_the_status_word` (live, ready), `Every_other_path_returns_404` (`/`, `/api`, `/health`); real worker on 5081 observed the same | Yes: mapping an extra `/` endpoint fails the 404 test |
| 11 | `PlatformHeartbeatServiceTests.Records_exactly_one_heartbeat_per_elapsed_interval` (fake clock); `HeartbeatRecorderTests.One_heartbeat_emits_one_span_one_log_line_and_one_counter_increment` | Yes: recording twice per tick, not incrementing the counter, and not logging each fail their test |
| 12 | `PlatformHeartbeatServiceTests.Stops_within_five_seconds_without_an_unhandled_exception` | Yes: not handling shutdown cancellation fails it |
| 13 | `PlatformHeartbeatServiceTests.A_failing_tick_is_logged_and_the_next_tick_still_runs` | Yes: not catching the failure fails it |
| Config | `WorkerHostTests.Host_refuses_to_start_with_a_heartbeat_interval_outside_one_second_to_one_hour` (0 s, 1 h 0 m 1 s) | Yes: widening the range fails both cases |
| 14 | API: `TelemetryTests.Request_span_has_the_service_resource_and_its_log_carries_the_span_ids`; worker: `WorkerTelemetryTests.Heartbeat_log_carries_the_heartbeat_span_ids_and_the_worker_resource` | Yes: a wrong service namespace and a renamed environment attribute each fail the API test |
| 15 | Same two tests (log trace and span IDs equal the span's); live worker JSON logs carry `TraceId` and `SpanId` scopes | Yes: logging the heartbeat outside its span fails the worker test; removing OpenTelemetry logging fails the API test. Open: an unrouted test request without middleware sometimes exported no span (test-host behaviour, noted in `TelemetryTests`) |
| 16 | `TelemetryTests.Requests_succeed_while_the_collector_is_unreachable` (20 requests, exporter on a closed port); live worker served health with no collector on 4317 | Guard test: export failure isolation is OpenTelemetry behaviour, so there is no production rule to remove |
| 17 | `dev.ps1 smoke` passed 7 of 7 checks on 2026-10-19 (Prometheus: API HTTP metrics and worker heartbeat counter; Tempo: API trace; Loki: API and worker logs with trace IDs; Grafana: 401 anonymous, three data sources with trace-to-logs links). An API trace from Tempo had 3 matching log lines in Loki | Yes: with the worker stopped, smoke exits 1 and names the worker; a script bug (PowerShell 5.1 JSON array handling) made one check fail until fixed |
