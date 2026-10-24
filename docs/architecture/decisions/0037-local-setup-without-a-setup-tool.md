# ADR-0037: No host scripts: Compose and dotnet run the local environment and its checks

- Status: Proposed
- Date: 2026-10-23
- Owners: TenantFlow tech lead (direction chosen by the user on 2026-10-23: "Go script-free", then "move the check
  scripts in this same change", then "remove each and every script that is OS dependent, build native solution";
  drafted as Proposed, only the user accepts it)
- Related decisions: amends ADR-0008; supersedes ADR-0036 (the C# setup tool and the PowerShell scripts) and, in part,
  ADR-0028 decision 1 (one PowerShell script runs the scans); ADR-0004, ADR-0009, ADR-0011, ADR-0012, ADR-0029; SPEC-0013

## Context
After ADR-0036, working on TenantFlow went through PowerShell: a dispatcher (`scripts/dev.ps1`) with nine command
scripts, a helper module, a Docker-or-WSL module, `verify.ps1` and `verify.sh`, `security-scan.ps1`, `progress.ps1`, and
a C# setup console with its own tests. They needed Windows PowerShell or PowerShell 7, worked around its quirks (a
byte-order mark on piped input, stderr turned into errors, flaky `wsl.exe` launches), and copied committed values from
one place to another. The user asked for the simplest setup: clone, one command, ready, and no OS-dependent scripts.

What the scripts did, looked at closely, is mostly done by tools the project already depends on:

- **Local values.** The local-only passwords are committed in `.env.example` already; copying them into `.env` and then
  into user-secrets protected nothing.
- **Database roles.** PostgreSQL's image runs `/docker-entrypoint-initdb.d` scripts when it initializes a volume, and
  psql's `\getenv` reads passwords from the environment, as the deployed provisioning task does.
- **Keycloak dev passwords.** The realm import replaces `${ENV_VAR}` placeholders (checked 2026-10-23 on the pinned
  26.8.0; the value is inserted as is, so `"` and `\` break it).
- **Migrations.** `deploy/docker/Dockerfile` has a `migrator` target; `docker compose up --wait` treats a one-shot
  service that exits 0 as done and fails when it does not (Compose 2.39).
- **The gate.** `EnforceCodeStyleInBuild` and warnings as errors were already on; with IDE0055 at warning the build
  reports the same formatting defects as `dotnet format --verify-no-changes` (shown with planted defects), and
  `RestoreLockedMode` makes every restore a locked one.
- **Checks and scans.** Test projects can talk to the running stack, run containers and read files on any OS.

## Decision
- **Setup is one command:** `docker compose up -d --wait` from the repository root (a root `compose.yaml` includes
  `deploy/compose/compose.yaml`). Every variable has a local-only default, so `.env` is an optional override file. The
  roles are provisioned at volume initialization (`provision-roles.sh`, `provision-roles.sql` unchanged). The Migrator is
  a Compose service built from the Dockerfile's `migrator` target. Each realm user's only credential is the
  `${KEYCLOAK_DEV_USER_PASSWORD}` placeholder.
- **ADR-0008 is amended:** the hosts' `appsettings.Development.json` holds the local-only connection strings, broker
  credentials, Keycloak authorities and audit key, equal to the Compose defaults; every committed password ends in
  `-local-only`. User-secrets remain the way to override a value. Real and deployed credentials are unchanged: never in a
  file, always from the secret manager; the images never contain Development settings.
- **The gate is `dotnet test TenantFlow.sln -c Release`** (locked restore, Release build with warnings and formatting as
  errors, unit, API and architecture tests). The full gate adds, with the stack running, the database tests
  (`dotnet run --project tests/Database/TenantFlow.Database.Tests -c Release`, which read the Compose settings and check
  that no test database is left) and the rule tests (`docker compose --profile checks run --rm rule-tests`).
- **Every former script has a native home:**

  | Was | Now |
  |---|---|
  | `dev.ps1 up / down / reset / status` | `docker compose up -d --wait / down / down -v / ps` |
  | `dev.ps1 validate` | `ComposeFileTests` (architecture tests, every build); CI runs `docker compose config --quiet` |
  | `dev.ps1 db-tests` | `dotnet run --project tests/Database/...` (`LocalStack`, leftover check in the assembly fixture) |
  | `dev.ps1 rules` | Compose `rule-tests` and `alertmanager-check` services (profile `checks`); the rule-test pairing is an architecture test |
  | `dev.ps1 smoke` | `tests/Smoke` (14 checks, one test each) |
  | `dev.ps1 drill` | `tests/Drills` (`FaultDrills`, one test per fault; the Worker drill stops and restarts the process itself) |
  | `verify.ps1`, `verify.sh` | `dotnet test TenantFlow.sln -c Release` |
  | `security-scan.ps1` | `tests/Security` (gitleaks, `dotnet list package --vulnerable`, Trivy images and IaC, configuration rules), same register |
  | `progress.ps1` | `tools/TenantFlow.Progress` (same dashboard and evidence) |
  | `lib/Docker.psm1` | `DockerCli` in `tests/Shared/TenantFlow.Testing`, the one place that knows about Docker inside WSL |

  The test projects that need the stack, Docker or the network (`Database`, `Smoke`, `Security`, `Drills`) have
  `IsTestProject` false, so the solution-wide `dotnet test` (and CI's unit-test step) builds them but does not run them.
- **Shell scripts stay only where they run inside a Linux container or on CI's Linux runner:**
  `deploy/compose/postgres/provision-roles.sh`, `deploy/terraform/environment/provisioning/*.sh` and
  `deploy/pipeline/release.sh`. They behave the same whatever the developer's OS. `LocalToolingTests` fails the build on
  any other script.
- **Layout deviation (raised for the user):** the blueprint's repository layout (section 5) has `scripts/`, which no
  longer exists, and no `tools/`, which holds the dashboard generator, a development-only project that references no
  TenantFlow project.
- Compose stays; Aspire stays deferred with ADR-0036's revisit triggers.

## Options considered
- **Keep ADR-0036.** Typed setup code, but a .NET project, its tests and a dozen scripts to copy committed values, and
  every check tied to PowerShell.
- **One script without the C# tool** (built first, 2026-10-23, and tested from empty volumes). About 400 lines of
  PowerShell that reimplement user-secrets merging around Windows PowerShell quirks.
- **Script-free setup, checks still in scripts** (the second step of 2026-10-23). Setup became Compose alone, but the
  gate and the milestone checks still needed PowerShell.
- **No host scripts (chosen).** One Compose command to start, `dotnet` for everything else, on every OS. Costs: ADR-0008's
  rule changes; role passwords apply only at volume initialization; the first start builds the Migrator image; four
  test projects and one tool replace scripts, which is more C# than the PowerShell it replaces, but typed, analyzed and
  run the same everywhere.

## Consequences
- Easier: a new developer runs `docker compose up -d --wait`, then F5. PowerShell is no longer a prerequisite.
- Harder: overriding a local password or port means an `.env` override for Compose and a user-secrets override for the
  host that uses it; a role password change needs `docker compose down -v`. Running one drill or one scan needs the
  xUnit filter (`-- -method "*Name*"`). Updating a package version needs `dotnet restore TenantFlow.sln
  -p:RestoreLockedMode=false`.
- The first start builds the Migrator image (about 3 minutes here, cached afterwards); after new migrations,
  `docker compose up -d --wait --build`.
- Found while porting, not caused by it: the security scan fails on five write permissions in `deploy.yml` and
  `release.yml` (`id-token: write`, `attestations: write`) and on CVE-2026-114406 in the Keycloak image. Both fail the
  old script identically; they need the user's review, as new exceptions or fixes.
- Developer machines keep the user-secrets that the old tool wrote; they hold the same values and do no harm.

## Validation
Run on 2026-10-23: from no volumes, no `.env` and no Migrator image, `docker compose up -d --wait` built the image,
provisioned the roles and applied every migration; the hosts, with their user-secrets folder pointed at an empty
directory, started from `appsettings.Development.json` alone; the smoke checks passed 14 of 14. The gate, the database
tests (329) and the rule tests pass. The new security scans and dashboard were compared with the old scripts on the
same tree: the same findings and identical dashboard evidence.

## Revisit triggers
- A local value must become secret (a shared development environment, a real third-party test key).
- Keycloak changes how it replaces placeholders during import, or Compose changes how `--wait` treats one-shot services.
- ADR-0036's Aspire triggers.
