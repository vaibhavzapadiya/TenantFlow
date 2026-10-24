# ADR-0036: Local environment tooling: Compose, small scripts and a C# setup tool, with Aspire deferred

- Status: Accepted
- Date: 2026-10-23
- Owners: TenantFlow tech lead (direction chosen by the user on 2026-10-23; accepted by the user on 2026-10-23,
  after the SPEC-0013 slices landed)
- Related decisions: ADR-0003, ADR-0005, ADR-0008, ADR-0011, ADR-0012, ADR-0029; SPEC-0012 open question 12; SPEC-0013
- Note (2026-10-23): ADR-0037 (Proposed) replaces the C# setup tool and every PowerShell script with Compose, test
  projects and one dashboard tool; Compose and the Aspire deferral stand.

## Context
`scripts/dev.ps1` has grown to 797 lines and nine commands. About 350 lines run the local environment, about 300
are milestone-gate tooling (`smoke`, `drill`, `rules`) and about 40 run the database tests. The user finds it hard
to read and asked whether .NET's own tooling could replace it.

The .NET-native answer is .NET Aspire: an AppHost project that runs the dependencies as resources and passes
configuration to the hosts. Two facts block it on the current workstation (checked 2026-10-23):

- **Aspire 13 needs the .NET 10 SDK.** That blocker lifted on 2026-10-24 when SPEC-0015 moved the build to SDK
  10.0.402; adopting Aspire remains its own decision, unchanged by this ADR.
  Aspire 9.x runs on SDK 9 but is the superseded line, so adopting it now means migrating it again within weeks.
- **Aspire does not support a custom Docker host.** It needs a `docker` or `podman` CLI on the Windows PATH;
  dotnet/aspire#1650 was closed "not planned" on 2025-11-17. Docker runs only inside WSL here (old WSL without
  systemd, Windows build 22000 without mirrored networking). The known workaround exposes the Docker daemon on
  unauthenticated TCP, which contradicts the loopback-only rule.

Even with Aspire, Prometheus, Loki, Tempo, Alertmanager and Grafana stay in Compose, because they mirror the
deployed observability stack (ADR-0029). `deploy/compose/postgres/provision-roles.sql` is also run by Terraform,
so it must stay a psql script.

## Decision
- **Docker Compose stays** the way local dependencies run. `compose.yaml`, its pinned digests and its loopback-only
  ports do not change.
- **`scripts/dev.ps1` becomes a short dispatcher** that keeps every command name and parameter. Each command moves
  to its own file under `scripts/dev/`. The Docker-or-WSL selection moves to one shared module, also used by
  `scripts/security-scan.ps1`.
- **Setup work moves to a C# console tool, `tools/TenantFlow.DevSetup/`.** It creates and extends `.env`, runs
  `provision-roles.sql` through psql in the container with the SQL on standard input, writes the hosts'
  user-secrets through `Microsoft.Extensions.Configuration.UserSecrets`, sets the Keycloak dev passwords through
  the admin API, and runs the database tests. `validate` stays a PowerShell script, because CI runs it before the
  .NET SDK is set up.
- **Layout deviation:** the blueprint's repository layout (section 5) has no `tools/` folder. Developer-only
  projects that are never deployed go there, so `src/` keeps only shipped code. It is development-only, never deployed, and references no TenantFlow project. It uses only
  packages already in `Directory.Packages.props`, so no new dependency.
- **ADR-0008 stands.** "The dev script writes user-secrets from `.env`" now means the tool, called by `dev.ps1 up`.
- **Aspire is deferred** to a time-boxed spike after the ADR-0005 migration, with the go/no-go criteria under
  Revisit triggers. Out of scope here: `dev.sh`, and any change to images, realms, smoke checks, drills or rule tests.

## Options considered
- **Aspire AppHost now.** Benefits: C# for the whole local topology, a built-in dashboard, and parameters that
  replace `.env`. Costs: blocked by the SDK version and the WSL-only engine. Working around it means running the
  AppHost inside WSL (hosts then run on Linux; slow `/mnt/d` file access), adding Podman, or installing Docker
  Desktop. It also adds Aspire.Hosting packages (Keycloak is a preview package), requires rewriting ADR-0008, and
  still needs Compose for observability, so two orchestrators.
- **Compose with the script split into small PowerShell files.** Benefits: no new project, no behaviour change,
  smallest diff. Costs: the trickiest code (psql quoting, secrets through standard input, Keycloak admin calls)
  stays in untyped, untested PowerShell, with Windows PowerShell 5.1 quirks.
- **Compose, small scripts and a C# setup tool (chosen).** Benefits: the hard parts become typed and unit-tested,
  in the language the project is written in. Works with Docker inside WSL. No new packages, and the stateful pieces
  are unchanged. Costs: one more project in the solution, and a `dotnet run` startup on each `up` (a few seconds).
  A small Docker-or-WSL helper exists in both PowerShell and C#.

## Consequences
- Easier: reading `dev.ps1`, testing the setup logic, changing a user-secret key in one typed place.
- Harder: `up` now needs the .NET SDK; today it only warned and skipped the user-secrets.
- Follow-up: update `docs/operations/local-environment.md`, the M1 guide, ADR-0012's wording, the test messages
  that name `./scripts/dev.ps1 up`, and `AGENTS.md` and `CLAUDE.md` if paths change.

## Validation
SPEC-0013's acceptance criteria. In particular: `up` then a host `dotnet run` works on a fresh clone; the same
user-secret keys exist; passwords with special characters round-trip; no secret appears in output or command lines;
`validate` still guards CI; `verify.ps1 -Database` passes.

## Revisit triggers
- After the ADR-0005 migration to .NET 10: run an Aspire spike (at most two days). Go only if it reaches a
  supported container runtime on the workstation without opening the Docker daemon on TCP, keeps pinned digests and
  loopback-only ports provably, and runs `provision-roles.sql` unchanged. Otherwise this ADR stays.
- Docker Desktop, Podman or a CLI that reaches the WSL engine becomes available on the workstation.
- Aspire adds support for custom Docker hosts or Docker contexts.

## Addendum (2026-10-23, asked for by the user)
Starting work took too many steps, so `up` now ends by running the Migrator. Applied migrations are skipped, which
makes running it on every `up` safe. The Migrator is still the only process that changes the schema (ADR-0012).
`TenantFlow.slnLaunch` holds a shared Visual Studio startup profile, **API + Worker**, so after `up` one F5 starts
both hosts. Other IDEs run the hosts' existing `launchSettings.json` profiles. Command names and parameters do not
change. Aspire stays deferred as decided above.
