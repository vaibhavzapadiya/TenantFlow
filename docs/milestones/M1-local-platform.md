# M1 — Local platform

> **Historical guide.** The `scripts/` entry points this guide describes were removed (ADR-0037). For the current
> commands see [docs/operations/local-environment.md](../operations/local-environment.md) and `AGENTS.md`.

**Goal:** one documented command starts TenantFlow's local dependencies. The API and worker run
against them with health endpoints, validated configuration, and logs, metrics, and traces that are
correlated and visible in one place.

**Gate (blueprint section 20):** one documented command starts dependencies; the API and worker emit
correlated telemetry. Interpretation approved on 2026-10-19: correlation is proven **within each host**
(every log written inside an operation carries that operation's trace and span IDs), and both hosts
report shared resource attributes. A single trace spanning both processes arrives with the broker in M5.

Specification: [SPEC-0001](../specs/0001-local-platform.md), 17 acceptance criteria, all verified.

## 1. What was built

```text
deploy/compose/
├── compose.yaml                PostgreSQL 17; "observability" profile: collector, Prometheus, Loki, Tempo, Grafana
├── .env.example                local-only defaults (ports, local passwords); .env is git-ignored
└── config/                     collector pipeline, Prometheus OTLP settings, Tempo, Grafana data sources
scripts/dev.ps1                 up [-Observability] | down | reset | status | validate | smoke
src/BuildingBlocks/TenantFlow.BuildingBlocks.Observability/
├── HealthEndpoints.cs          /health/live (no checks), /health/ready (checks tagged "ready"), status word only
└── TelemetrySetup.cs           OpenTelemetry traces, metrics, logs over OTLP; JSON console logs
src/Hosts/TenantFlow.Api/       options validation, PostgreSQL readiness check, user-secrets for local runs
src/Hosts/TenantFlow.Worker/    health-only web host; heartbeat (PeriodicTimer on TimeProvider)
tests/Shared/TenantFlow.Testing/  telemetry capture for host tests
tests/Worker/TenantFlow.Worker.Tests/
docs/architecture/decisions/    ADR-0007 (logging pipeline), ADR-0008 (local configuration and secrets)
docs/operations/local-environment.md
```

Deliberately **not** in M1: the Migrator host and EF Core (M3), Testcontainers (M3, after Open
Decision 21), container images (M12), dashboards, alerts, and SLOs (M10), log redaction (M9), and a
cross-process trace (M5).

## 2. Architectural reasoning

### One script in front of Compose
`scripts/dev.ps1` is the only documented entry point, and CI runs its `validate` command. Developers
never memorize Compose flags, and policy checks such as loopback-only ports run the same way in CI and
locally. It works on Windows PowerShell 5.1 and PowerShell 7, and with Docker on the host or inside WSL.
It never starts system services: when Docker is down it explains the fix instead of escalating privileges.

### Liveness and readiness mean different things
- **Liveness** runs no checks. A database outage must not make the platform restart a healthy process;
  restarts would not fix the database and would add load during recovery.
- **Readiness** runs one `SELECT 1` with a 2-second bound and no retries. Probes run every few seconds,
  so retrying inside a probe multiplies load on a database that is already struggling.
- Both return only the status word. Exception text, hosts, and dependency names stay in logs.
- A separate startup endpoint waits for M3: today startup validation fails the process before it serves
  anything, so an extra endpoint would always say healthy and prove nothing.

### Configuration fails fast and never echoes values
Required options are validated at startup (`ValidateOnStart`). The error names the key
(`ConnectionStrings:TenantFlow`) and never the value. Credentials never live in `appsettings`: Compose
reads a git-ignored `.env`, and `dev.ps1 up` writes the API's connection string into .NET user-secrets
(ADR-0008). Deployed environments use the same keys from the environment or a secret manager.

### Telemetry: one pipeline, correlation for free
`Microsoft.Extensions.Logging` plus OpenTelemetry (ADR-0007) means a log written inside a span carries
that span's IDs automatically. Both hosts set `service.name`, `service.namespace`, `service.version`,
and `deployment.environment.name`. Export is asynchronous, so a missing collector costs telemetry data,
never requests. Health probes are not traced: they would bury real requests.

The collector is the only thing the hosts know about. It forwards traces to Tempo, metrics to
Prometheus's native OTLP receiver, and logs to Loki's native OTLP endpoint. Swapping a backend for a
managed service later changes collector configuration, not application code.

### Time is injected
The heartbeat runs on `PeriodicTimer` driven by `TimeProvider`. Tests advance a fake clock instead of
sleeping, which keeps them fast and deterministic (blueprint section 15).

## 3. Security considerations

| Concern | Control |
|---------|---------|
| Local services reachable from the network | Every published port binds to `127.0.0.1`; `dev.ps1 validate` enforces it in CI for every profile |
| Credentials in tracked files | `.env` git-ignored; user-secrets for hosts; `.env.example` holds only local-only values |
| Password visible to other processes | User-secret written through standard input from a BOM-free temp file, never on a command line, never printed |
| Health endpoints leaking internals | Status word only; tested |
| Startup errors leaking configuration | Validation messages name keys, never values; tested |
| Grafana exposed without login | Anonymous access off (smoke checks for 401); admin password from `.env` |
| Supply chain of local images | Images pinned by version and digest, 14-day cooldown, Dependabot `docker-compose` updates |
| Test telemetry leaking to a developer's collector | Test hosts export to a closed port |

Residual risk: OTLP between hosts and collector is unencrypted, acceptable only on loopback. Deployed
environments need TLS or a private network (M12). User-secrets are unencrypted in the user profile,
which is acceptable for local-only passwords only.

## 4. Verification

### 4.1 Run it yourself

```bash
./scripts/dev.ps1 up -Observability
```

Then start the hosts one after the other (see 4.4), and run the smoke check:

```bash
./scripts/dev.ps1 smoke
```

Grafana is at `http://127.0.0.1:3000` (user `admin`, password from `deploy/compose/.env`).

### 4.2 Gate evidence (2026-10-19, fresh run)

| Exit criterion | Evidence | Result |
|----------------|----------|--------|
| One documented command starts dependencies | `./scripts/dev.ps1 up -Observability`: 6 containers up and healthy | Met |
| API and worker emit correlated telemetry | `dev.ps1 smoke` 7/7; after a fresh restart Loki held 3 API and 13 worker log lines with trace IDs, Tempo held a new API trace, and Prometheus the heartbeat counter; tests prove log and span IDs match in both hosts | Met |
| Quality gate | `verify.ps1`: 0 warnings, 20/20 tests; CI coverage command produced Cobertura (93.5% API tests, 89.4% worker tests) | Met |

### 4.3 Break-it checks

| Change | Expected failure | Re-proved at the gate |
|--------|------------------|-----------------------|
| Publish Grafana on all interfaces | `dev.ps1 validate` exits 1 | Yes |
| Log the heartbeat outside its span | Worker telemetry test fails | Yes |
| Liveness runs dependency checks | Live test fails (503) | During S2 |
| Readiness check without the `ready` tag | Ready test fails (200) | During S2 |
| Remove `ValidateOnStart` | Configuration test fails | During S2 |
| Health response includes check details | Ready test fails | During S2 |
| Heartbeat recorded twice per tick, not caught, or not cancellable | The matching heartbeat test fails | During S3 |
| Wrong service namespace or environment attribute | API telemetry test fails | During S4 |

### 4.4 Lessons from verification

- Starting both hosts with `dotnet run` at the same instant can fail: both build the shared building
  block and one hits a file lock. Build first (`dotnet build TenantFlow.sln`) or start them one after the other.
- `WebApplicationFactory` with top-level `Program` files intercepts the next host built in the process,
  so parallel host tests can cross-wire settings. Host tests run sequentially.
- Open: in tests, an unrouted request without middleware sometimes exported no span (about 1 run in 5).
  With a middleware in front it was captured every time. Production is not affected; see `TelemetryTests`.

## 5. Trade-offs and assumptions

- **Hosts run on the machine, dependencies in Compose.** This is a faster edit-run loop than containers,
  at the cost of the user-secrets step (ADR-0008).
- **Collector without a health check.** The contrib image has no shell to run one. `dev smoke` checks
  the whole pipeline end to end instead.
- **Tempo 3.x** with a minimal configuration, verified by running it, not from memory.
- **PostgreSQL 17**, chosen by the user to match the local installation.
- **Docker inside WSL does not start by itself** on this machine (built-in WSL, no systemd); see the
  local-environment guide.

## 6. Mentor exercises

1. Stop PostgreSQL while watching `/health/live` and `/health/ready`. Explain to an interviewer why
   liveness must not follow the database.
2. In Grafana Explore, open an API trace in Tempo, jump to its logs, and back. Then write the LogQL
   query that finds every log line of one trace.
3. Change `Worker__HeartbeatInterval` to `00:00:00` and start the worker. Read the error and explain why
   failing at startup beats failing on the first tick.
4. Remove `127.0.0.1:` from one port in `compose.yaml` and run `./scripts/dev.ps1 validate`. Then
   explain why "localhost-only" is a security control even on a laptop.

## 7. Interview talking points

- "Why is your readiness check not retried?" Probes are already periodic retries; retrying inside one
  multiplies load during an outage and delays the signal.
- "How do you correlate logs with traces?" One logging abstraction exported through OpenTelemetry, with
  logs written inside spans carrying the span's IDs. It is proven by a test, not just configured.
- "How do you keep secrets out of a repository that needs local credentials?" A git-ignored `.env` for
  infrastructure and user-secrets for the app, the same configuration keys as production, and an agent
  permission rule that denies reading `.env`.
- "How do you know your test proves anything?" Every guard was removed once to watch its test fail.

## 8. Exit checklist

- [x] One documented command starts dependencies (`dev.ps1 up [-Observability]`)
- [x] API and worker health endpoints, validated configuration, PostgreSQL readiness
- [x] Traces, metrics, and logs from both hosts in Tempo, Prometheus, and Loki, correlated per host
- [x] SPEC-0001 implemented, all 17 criteria verified; ADR-0007 and ADR-0008 accepted
- [ ] **You:** review this guide and the M1 commits on branch `m1-local-platform`
- [ ] **You:** push and open a pull request so CI runs the gate and the new compose validation
- [ ] **You:** approve the start of M2 (identity and tenant boundary)
