# Local environment

TenantFlow's local dependencies run in Docker Compose. The .NET hosts run directly from an IDE or with `dotnet run`.
No setup script is involved (ADR-0037): Compose starts and prepares everything, and each host's
`appsettings.Development.json` already holds the matching local-only settings.

## Setting up a fresh clone

Prerequisites: the .NET SDK named in `global.json`, and Docker with Compose v2 (Docker Desktop, or Docker Engine inside
WSL; see [Docker setup](#docker-setup)). Then, from the repository root:

```bash
docker compose up -d --wait
```

With Docker inside WSL, prefix it with `wsl` (`wsl docker compose up -d --wait`); the same holds for every
`docker compose` command below. The command returns when everything is ready, and fails if any step fails:

1. Starts PostgreSQL, Keycloak and RabbitMQ and waits until they are healthy. Every setting has a local-only default in
   `deploy/compose/compose.yaml`, so no `.env` file is needed.
2. Runs the provisioner (`TenantFlow.Migrator provision`), which creates or re-asserts the database roles and exits.
   Keycloak imports both realms with the dev-user password.
3. Runs the Migrator, which applies the migrations and exits. The first start builds its image (a few
   minutes); later starts reuse it.

Then start the API and the Worker ([Running the hosts](#running-the-hosts)). Run the same command after a reboot. After
pulling new migrations, add `--build` so the Migrator image is rebuilt.

## Commands

| Command | What it does |
|---------|--------------|
| `docker compose up -d --wait` | Starts and prepares everything, as described above |
| `docker compose --profile observability up -d --wait` | Same, plus the collector, Prometheus, Loki, Tempo, Alertmanager, and Grafana (with the TenantFlow dashboards) |
| `docker compose up -d --wait --build` | Same, rebuilding the Migrator image (after pulling new migrations) |
| `docker compose ps` | Shows each container's state, health, and ports |
| `docker compose down` | Stops everything. Data is kept |
| `docker compose down -v` | Stops everything and **deletes the data volumes** |

Checks and tests (no script; see also [README, Running the tests](../../README.md#running-the-tests)):

| Command | What it does |
|---------|--------------|
| `dotnet test TenantFlow.sln -c Release` | The gate, as in CI: locked restore, Release build with warnings and formatting as errors, and the unit, API and architecture tests. The architecture tests also check the Compose file: pinned images, loopback-only ports, a default for every setting, synthetic realms |
| `dotnet run --project tests/Database/TenantFlow.Database.Tests -c Release` | The database and messaging tests against the running stack. Add `-- -class "*SomeTestClass"` for the matching classes only (about 2 seconds for one class against about 45 for all). The run fails if a test database is left behind |
| `docker compose --profile checks run --rm rule-tests` | Checks and unit-tests the Prometheus recording and alert rules with promtool, after checking the Alertmanager configuration with amtool, from the pinned images (ADR-0029). Needs Docker, not the running stack |
| `dotnet run --project tests/Smoke/TenantFlow.Smoke.Tests -c Release` | With the observability profile and both hosts running: checks that real Keycloak tokens are accepted, that metrics, traces, and logs from both hosts arrived, that Prometheus, Loki and Tempo keep 7 days (ADR-0025), that the broker is scraped, that Grafana has the TenantFlow dashboards, that the rules are loaded and no alert fires, and that Grafana refuses anonymous access |
| `dotnet run --project tests/Drills/TenantFlow.Drills.Tests -c Release -- -method "*Database_down*"` | Milestone gate only: injects one fault and times its alert (SPEC-0010, [drills](drills/README.md)) |
| `dotnet run --project tests/Security/TenantFlow.Security.Tests -c Release` | The secret, dependency, image, configuration and infrastructure scans (ADR-0028); needs Docker and the network |
| `dotnet run --project tools/TenantFlow.Progress -- --verify --open` | The progress dashboard ([progress/README.md](../progress/README.md)) |

The database and smoke tests read the stack's settings the way Compose does (`.env`, else the defaults in
`compose.yaml`), so they need no setup. The root `compose.yaml` includes `deploy/compose/compose.yaml`, where the
services are defined next to their configuration.

## Services and ports

Every published port binds to `127.0.0.1`, so nothing is reachable from other machines on the network.

| Service | Host address | Override in `.env` |
|---------|--------------|----------------------|
| PostgreSQL 17 | `127.0.0.1:55432` | `POSTGRES_PORT`, `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` |
| Keycloak 26 (local OIDC provider) | `http://localhost:8080`, admin console `/admin`, user `admin` | `KEYCLOAK_PORT`, `KEYCLOAK_ADMIN_PASSWORD`, `KEYCLOAK_DEV_USER_PASSWORD` |
| RabbitMQ 4.3 (message broker, ADR-0021) | AMQP `127.0.0.1:5672`; management UI `http://localhost:15672`, user from `RABBITMQ_USER` | `RABBITMQ_PORT`, `RABBITMQ_MANAGEMENT_PORT`, `RABBITMQ_USER`, `RABBITMQ_PASSWORD` |
| OpenTelemetry Collector (observability profile) | `127.0.0.1:4317` (gRPC), `:4318` (HTTP) | `OTLP_GRPC_PORT`, `OTLP_HTTP_PORT` |
| Grafana | `http://127.0.0.1:3000`, user `admin` | `GRAFANA_PORT`, `GRAFANA_ADMIN_PASSWORD` |
| Prometheus | `http://127.0.0.1:9090` | `PROMETHEUS_PORT` |
| Loki | `http://127.0.0.1:3100` | `LOKI_PORT` |
| Tempo | `http://127.0.0.1:3200` | `TEMPO_PORT` |
| Alertmanager | `http://127.0.0.1:9093` (firing alerts: `/api/v2/alerts`; the receiver is null locally) | `ALERTMANAGER_PORT` |

The host port is 55432, not 5432, because a natively installed PostgreSQL often holds 5432.

## Telemetry (ADR-0007)

Both hosts export traces, metrics, and logs over OTLP to `http://localhost:4317` (override with
`OTEL_EXPORTER_OTLP_ENDPOINT`). Without the observability profile, export fails quietly and the hosts
keep working. Locally, metrics are exported every 10 seconds (`OTEL_METRIC_EXPORT_INTERVAL` in the
launch profiles). Health probes are not traced.

In Grafana, open **Explore**, choose **Tempo**, and search by service name. A span's **Logs for this
span** link opens the matching log lines in Loki; a log line's **TraceID** link opens the trace.

In Development, console logs are one readable line per record (ADR-0007 amendment 1); trace IDs are in
Loki, not on the console. In other environments, console logs are JSON, and each line carries `TraceId` and `SpanId`
in its scopes.

## Configuration and secrets (ADR-0008, ADR-0037)

- Every local value is a throwaway default, and every password ends in `-local-only`. The Compose defaults are in
  `deploy/compose/compose.yaml`; the hosts' copies are in `src/Hosts/*/appsettings.Development.json` (connection
  strings, broker credentials, Keycloak authorities, and the audit key `Audit:OperatorReferenceKey`, ADR-0026). An
  architecture test keeps the two equal. Real credentials never go in a file: deployed environments read them from
  the secret manager.
- To change a value locally, copy `deploy/compose/.env.example` to `deploy/compose/.env` (git-ignored) and uncomment
  the setting. If a host uses it (a port or password of PostgreSQL, RabbitMQ or Keycloak), override the same key in
  that host's user-secrets too, which take precedence over `appsettings.Development.json`:
  `dotnet user-secrets set ConnectionStrings:TenantFlow "<connection string>" --project src/Hosts/TenantFlow.Api`.
- Role passwords are set when PostgreSQL initializes its volume; after changing one, run `docker compose down -v`.
  Grafana likewise stores its admin password on first start.
- Keycloak inserts `KEYCLOAK_DEV_USER_PASSWORD` into the realm JSON as it is, so a value of your own must not contain
  `"` or `\`.
- User-secrets written by the old setup tool (before ADR-0037) hold the same values and do no harm. To remove them:
  `dotnet user-secrets clear --project src/Hosts/TenantFlow.Api` (likewise for the Worker and the Migrator).

## Database roles and migrations (ADR-0011, ADR-0012)

- The `provisioner` service runs `TenantFlow.Migrator provision` as the Compose superuser before the Migrator, on every
  start, the same command the deployed provisioning task runs (`DatabaseProvisioner`). It is idempotent and creates or
  re-asserts two roles:
  `tenantflow_owner` (used only by the Migrator; owns module schemas) and `tenantflow_app` (used by the
  API; owns nothing, cannot run DDL, never bypasses row-level security). Their passwords are
  `TENANTFLOW_OWNER_PASSWORD` and `TENANTFLOW_APP_PASSWORD` (Compose defaults, `.env` overrides).
- Every environment must provide roles with **exactly these names**: module migrations grant
  `tenantflow_app` access to each schema. Deployed environments provision them with infrastructure code (M12).
- Each module owns one schema (`tenants`, `identity_access`) with its own migrations history table.
- The `migrator` Compose service applies the migrations on every `docker compose up`, as the owner role; applied ones
  are skipped. After pulling new migrations, use `docker compose up -d --wait --build`, or run the Migrator from the
  IDE or with `dotnet run --project src/Hosts/TenantFlow.Migrator` (exit code 0 on success, 1 on failure).
  The API and worker never migrate.
- Database tests: `dotnet run --project tests/Database/TenantFlow.Database.Tests -c Release` runs the tests in `tests/Database` against this PostgreSQL. Each test uses its own
  `tenantflow_test_*` database, and the run fails if any is left behind. The solution-wide `dotnet test` (and CI's
  unit-test step) does not run them; CI runs them against a Testcontainers PostgreSQL.
- New migration (local tool from `.config/dotnet-tools.json`, `dotnet tool restore` once):
  `dotnet tool run dotnet-ef migrations add <Name> --project <module Infrastructure project> --startup-project <same> --output-dir Persistence/Migrations`.
  Never edit an applied migration; add a new one (`AGENTS.md`, rule 7).

## Payments (ADR-0022)

- The API charges through the deterministic simulator, which `appsettings.Development.json` switches on
  (`Payments:Simulator:Enabled`); a host configured for it anywhere else refuses to start. The payment-method token scripts the outcome
  (`sim_success`, `sim_decline`, `sim_timeout_then_success`, `sim_timeout_then_decline`, `sim_timeout_unknown`,
  `sim_server_error`). No card data is ever accepted.
- Simulator webhooks (`POST /v1/payment-webhooks/simulator`, header `TenantFlow-Simulator-Signature`, Stripe's
  `t=...,v1=...` scheme) are refused until the API has a secret. Set one of your own choosing in its user-secrets:
  `dotnet user-secrets set Payments:Simulator:WebhookSecret <value> --project src/Hosts/TenantFlow.Api`.
  Never commit it (ADR-0008).
- Stripe test mode (optional, manual): set `Payments:Provider` to `stripe` and `Payments:Stripe:SecretKey` to a
  `sk_test_...` key in the API's and the Worker's user-secrets; the hosts refuse to start with any other key. Pay with a
  Stripe test payment method (`pm_card_visa`, `pm_card_chargeDeclined`). For webhooks, run
  `stripe listen --forward-to localhost:<api port>/v1/payment-webhooks/stripe` and put the printed `whsec_...` in
  `Payments:Stripe:WebhookSecret`. Automated tests never call Stripe; they use documented response shapes.

## Authentication (ADR-0009, ADR-0010)

- The API validates bearer tokens from the issuer in `Authentication:Tenant:Authority` for the audience
  in `Authentication:Tenant:Audience`; the host refuses to start without either. Plain-HTTP metadata
  (`RequireHttpsMetadata: false`) is accepted only for a loopback authority.
- Every response carries `X-Content-Type-Options`, `Cache-Control: no-store`, `Referrer-Policy` and a
  `Content-Security-Policy` (ADR-0017). The Production log level for EF Core is Warning, so SQL is not logged;
  Development logs commands.
- Development trusts the local Keycloak realms `tenantflow` and `tenantflow-platform` on port 8080
  (`Authentication:*:Authority` in `appsettings.Development.json`). Tests use an in-process test issuer.
- The realm is imported from `deploy/identity/realms/local/tenantflow-realm.json` on every new container. It
  holds only synthetic users (`dev-*`, `@tenantflow.test`), whose only credential is the placeholder
  `${KEYCLOAK_DEV_USER_PASSWORD}`: Keycloak fills it in from the container's environment during the import.
  `ComposeFileTests` (every build) rejects any other credential value, secrets, or real-looking users.
- Get a local token (development only; the client allows the password grant for scripts, real clients
  use the authorization code flow with PKCE):
  `POST http://localhost:8080/realms/tenantflow/protocol/openid-connect/token` with
  `grant_type=password`, `client_id=tenantflow-dev-cli`, `username=dev-user-a`, `password=tenantflow-dev-user-local-only`
  (or your `.env` override).
  The smoke tests do this and call `GET /me`.
- Each realm also has a browser client for the API reference's sign-in (SPEC-0014): `tenantflow-api-docs` and
  `tenantflow-platform-api-docs`, public, authorization code with PKCE S256 only, redirects and web origin only
  `http://localhost:5080`. A changed API port loses the reference's sign-in, nothing else.
- Platform operators come from a separate realm, `tenantflow-platform` (ADR-0010), with the synthetic
  user `dev-operator` holding the `platform-operator` role, issued as the `tenantflow_roles` claim. Its
  dev client is `tenantflow-platform-dev-cli`; the API trusts it as `Authentication:Platform`.
- All business routes are under the version segment `/v1` (ADR-0015); only `/health/*` is unversioned.
- Request bodies are limited to 64 KiB (`RequestLimits`); a larger body is a 413 `request.payload_too_large`.
  The smoke tests check this against the real host, because the in-memory test host does not enforce it.
- Platform tenant lifecycle: `POST /v1/platform/tenants` `{ "name": "..." }`, `GET /v1/platform/tenants/{id}`,
  `POST /v1/platform/tenants/{id}/suspend`, `POST /v1/platform/tenants/{id}/reactivate`. Run the Migrator
  first; the smoke tests check that an operator token reaches `/platform` and not `/me`.
- After editing a realm file, recreate the container so it is re-imported:
  `docker compose up -d --wait --force-recreate keycloak`.
- Every endpoint without an explicit policy is denied with 401 for everyone, whether or not a token is sent: the
  fallback policy names no scheme, so it never reads a token. This also
  covers unknown paths. Only the health endpoints are anonymous.

## Running the hosts

**Visual Studio:** choose the startup profile **API + Worker** in the toolbar and press F5. It starts both hosts with
the debugger; the profile is committed in `TenantFlow.slnLaunch` (Visual Studio 2022 17.10 or later). Any other IDE
runs the same launch profiles from each host's `Properties/launchSettings.json`, or use the commands below.

**Smaller solutions:** `solution-filters/` holds Visual Studio solution filters (`.slnf`) that load only part of the
68 projects: `Hosts` (the three hosts and what they reference), `Api` (the API, its tests and the architecture tests),
and one per module (its projects, its tests and what they reference). Open one with File → Open → Project/Solution;
`dotnet build solution-filters/Catalog.slnf` works too. The gate still builds `TenantFlow.sln`. After adding a project,
add it to the filters that should show it.

| Host | Command | Address |
|------|---------|---------|
| Migrator (run by `docker compose up`) | `dotnet run --project src/Hosts/TenantFlow.Migrator` | none (runs and exits) |
| API | `dotnet run --project src/Hosts/TenantFlow.Api` | `http://localhost:5080` |
| Worker | `dotnet run --project src/Hosts/TenantFlow.Worker` | `http://localhost:5081` (health endpoints only) |

Start the hosts one after the other, or run `dotnet build TenantFlow.sln` first: two `dotnet run`
commands started at the same instant both build the shared building block, and one fails on a file lock.

- **Requests from the IDE:** [`TenantFlow.Api.http`](../../src/Hosts/TenantFlow.Api/TenantFlow.Api.http) walks the whole
  API (operator and user tokens, a tenant, a product, an order, a payment, a refund, a subscription, an export) in the
  Visual Studio or Rider HTTP client. Choose the `dev` environment and send the requests top to bottom; each one reads
  its ids and token from the responses above it.
- **API reference (Development only):** `http://localhost:5080/scalar/v1` serves an interactive reference (Scalar)
  built from the OpenAPI document at `/openapi/v1.json`. Both are anonymous. To try a protected route, choose
  **Tenant user** or **Platform operator** under Authentication, click **Authorize** and sign in at the local Keycloak
  (`dev-user-a`, `dev-operator`, `dev-support`; SPEC-0014); every request then carries the token, which lives in the page
  only. A Production host does not map the reference.

The worker publishes the module outboxes to RabbitMQ (ADR-0021): it polls every second, in batches of 50, and
deletes published rows after 7 days (`Messaging:Outbox:*`). The `tenantflow.outbox.pending` and
`tenantflow.outbox.oldest_pending_age` metrics show the backlog per module. It also runs the Notifications consumer
on the queue `notifications.order-events`; failed messages are retried, then dead-lettered
([dead-letter-replay.md](dead-letter-replay.md)).

Imports and exports (SPEC-0008) keep their files under `Storage:Root`, which the API and the worker must share
(`.storage/` in Development). Locally `Storage:Provider` is `filesystem`, switched on by `Storage:Filesystem:Enabled`;
a host without that switch, or without the keys its provider needs, refuses to start. Deployed environments use `s3`
(ADR-0024 amendment 1); its tests run against Adobe S3Mock in the database test run, never in this stack. The worker runs their jobs from due rows and deletes files 7 days after a job ends. Every
30 seconds a Quartz task refreshes `tenantflow.imports_exports.due_jobs` and `tenantflow.imports_exports.oldest_due_age`
per kind; `tenantflow.imports_exports.jobs`, `.failed_rows` and `.job_duration` count the jobs that end.

The worker logs a heartbeat every 30 seconds. For a faster local check, set
`Worker__HeartbeatInterval=00:00:02` in the environment (allowed range: 1 second to 1 hour).

## Health endpoints

| Endpoint | Meaning |
|----------|---------|
| `/health/live` | 200 while the process can serve requests. Never checks other systems |
| `/health/ready` | API: 200 when PostgreSQL answers, 503 otherwise. Worker: 200 when PostgreSQL and RabbitMQ answer, 503 otherwise |

Both hosts expose both endpoints. They return only the status word (`Healthy` or `Unhealthy`).

## Docker setup

With Docker Desktop, or Docker installed directly on Linux or macOS, run `docker compose` as shown. With Docker Engine
inside WSL and no `docker` on the Windows PATH, run the same commands as `wsl docker compose ...` from the repository
folder in Windows; WSL starts in that folder. That uses the **default** WSL distribution; if Docker lives in another
one, make it the default with `wsl --set-default <distribution>`. The drills and the security scans find either setup
themselves (`DockerCli` in `tests/Shared/TenantFlow.Testing`).

Through `wsl.exe`, about one Docker call in ten fails at once with no output. If a command ends immediately without
saying anything, run it again; the tests retry such a call themselves.

### "The Docker engine is not reachable"

| Setup | Fix |
|-------|-----|
| Docker Engine inside WSL, on the built-in (older) WSL | The built-in WSL cannot boot systemd, so the enabled Docker service never starts. Run `wsl --update`, then `wsl --shutdown`, and open a new terminal. The current WSL runs systemd, so Docker then starts automatically |
| Same, as a one-off for this session | `wsl -u root service docker start` |
| Docker Desktop | Start Docker Desktop and wait until it reports it is running |

Check the WSL version with `wsl --version`. The built-in WSL rejects that option as invalid.

### "Port is already allocated"

Another process holds the port. Change it in `deploy/compose/.env` (for example `POSTGRES_PORT`), override the host
setting that uses it in user-secrets (see [Configuration and secrets](#configuration-and-secrets-adr-0008-adr-0037)),
then run `docker compose up -d --wait` again. On Windows, `netstat -ano | findstr :55432` shows the owning process ID.

### Starting from a clean state

```bash
docker compose down -v
```

```bash
docker compose up -d --wait
```
