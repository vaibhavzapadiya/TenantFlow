# Threat model: supply chain and delivery

- Status: Draft (M9), written with SPEC-0009
- Date: 2026-10-22
- Owner: TenantFlow tech lead (the user)
- Scope: the source repository, NuGet dependencies, the SDK pin, the GitHub Actions workflow, third-party container
  images used locally, local secrets, database roles and the Migrator, schema migrations, feature flags and deployment.
  Our own container images, infrastructure as code and production deployment arrive in M12.
- Related: blueprint sections 5, 13, 15, 18 and 22; ADR-0003, ADR-0004, ADR-0005, ADR-0008, ADR-0028 (Proposed);
  [SPEC-0009](../specs/0009-security-and-privacy.md); [M0 guide](../milestones/M0-engineering-foundation.md),
  [M1 guide](../milestones/M1-local-platform.md); [threat model 0001](0001-tenant-boundary.md)

## Assets and data classification

| Asset | Classification | Why it matters |
|---|---|---|
| Source code and git history | Internal, integrity-critical | Everything built and deployed comes from it; history keeps anything ever committed |
| `Directory.Packages.props` and the `packages.lock.json` files | Integrity-critical | They decide which third-party code runs inside the hosts |
| `global.json`, `Directory.Build.props`, `.editorconfig` | Integrity-critical | They set the SDK, analyzers, warnings-as-errors and NuGet audit |
| `.github/workflows/ci.yml`, `.github/dependabot.yml` | Integrity-critical | A changed workflow can run arbitrary code with the repository token |
| Compose images (PostgreSQL, Keycloak, RabbitMQ, observability) | Integrity-critical | Third-party code holding local data and identities |
| Local secrets (`deploy/compose/.env`, .NET user-secrets) | Secret (local-only values) | Database, broker, IdP and Grafana passwords for the local stack |
| Database owner and app roles | Secret, integrity-critical | The owner role can change any schema; the app role reads every tenant's rows under RLS |
| Migrations | Integrity-critical | Run with the owner role; a bad one can lose data or break a running version |

Personal data in scope: none. The repository, fixtures and realm files hold synthetic data only (ADR-0008, SPEC-0002).

## Actors and trust boundaries

Actors: the owner (sole maintainer); coding agents working in the repository; package and image publishers; a
compromised upstream maintainer or registry; a typosquatter; a future outside contributor opening pull requests;
GitHub Actions runners and action authors.

| Boundary | Crossed by | Control |
|---|---|---|
| S1 nuget.org → restore | every restore | `nuget.config` clears feeds and maps `*` to nuget.org; lock files; NuGet audit |
| S2 Image registries → local Docker | `docker compose up` | images pinned by version and digest; `ComposeFileTests` refuse an unpinned image |
| S3 Pull request → CI runner | workflow runs (none yet) | `pull_request` trigger only, `permissions: contents: read`, `persist-credentials: false`, SHA-pinned actions |
| S4 Developer machine → repository | commits | `.gitignore` covers `.env*` and `secrets.json`; coding agents are denied reading `.env` in `.claude/settings.json` |
| S5 Configuration → host | host start | required keys validated at startup; errors name keys, never values |
| S6 Migrator → PostgreSQL | migrations | owner connection string used only by the Migrator; hosts use the non-owner app role |

## Entry points and data flows

1. Package flow: `Directory.Packages.props` (central versions, transitive pinning) → `dotnet restore --locked-mode`
   (`dotnet build`, CI) → NuGet audit (mode `all`, level `low`; high and critical fail, NU1901 and NU1902 warn).
2. Toolchain flow: `global.json` pins SDK 10.0.402 with `rollForward: latestPatch` (ADR-0003), so a patch inside the
   10.0.4xx band is taken automatically and an older SDK fails to resolve.
3. Update flow: Dependabot proposes NuGet, Actions, Compose image and SDK updates weekly with a 14-day cooldown. It only
   runs once a GitHub remote exists.
4. CI flow: `ci.yml` runs `docker compose config`, locked restore, format, Release build, tests and database tests on
   Testcontainers. It has never run: there is no remote, and CI is on hold (SPEC-0009 decision 1).
5. Local secrets flow (ADR-0037, Proposed): Compose and the hosts' `appsettings.Development.json` hold the same
   `-local-only` values; `.env` and user-secrets only override them. No script reads or writes them. Hosts read
   configuration keys only, and the images never contain Development settings.
6. Migration flow: the Migrator connects with `ConnectionStrings:TenantFlowOwner` and applies each module's EF Core
   migrations into its own schema. Schema changes follow expand-contract (blueprint, "Migration strategy").
7. Feature flags: none exist. Deployment: none exists until M12; the only environments are local and test containers.

## Abuse cases / attack paths

| Id | Attack | Attacker goal | Path |
|---|---|---|---|
| SC-01 | Compromised or typosquatted package | Run code in the API or Worker | Malicious release of a used package, a look-alike id, or a private-feed name substitution |
| SC-02 | Vulnerable transitive dependency | Exploit a known flaw | An advisory lands on a package we never named directly |
| SC-03 | Lock-file tampering | Slip in an unreviewed version | Edit a lock file or regenerate it with another SDK so the diff hides a change |
| SC-04 | Mutable image tag | Replace a local service image | Re-push a tag upstream so the next pull gets different content |
| SC-05 | Vulnerable third-party image | Exploit a known flaw in a dependency service | Keep running an image with a fixed HIGH or CRITICAL vulnerability |
| SC-06 | Malicious or hijacked GitHub Action | Steal the token or poison the build | A moved tag or a compromised action repository |
| SC-07 | Over-broad workflow permissions or pull-request secrets | Write to the repository or read secrets from a fork | `pull_request_target`, write permissions, persisted checkout credentials, secrets in PR jobs |
| SC-08 | Secret committed to git | Reuse a credential | `.env`, a key or a connection string committed; it stays in history after deletion |
| SC-09 | Secrets in logs or configuration | Harvest a credential | Startup errors, Migrator logs or `appsettings*.json` echoing values; a live payment key used locally |
| SC-10 | App connects as owner | Change schema or bypass RLS | Hosts given the owner connection string, or the app role granted ownership |
| SC-11 | Unsafe migration | Lose data or break the running version | Editing an applied migration, a missing migration, a destructive change in one step, DDL outside the module schema |
| SC-12 | Unsupported or drifting toolchain | Build with an unpatched SDK or runtime | .NET 9 leaves support on 2026-11-10; `latestFeature` lets machines differ |
| SC-13 | Unverifiable artifacts | Ship a tampered image | No SBOM, provenance or image signature exists to check (no images are built yet) |
| SC-14 | Insecure infrastructure configuration | Expose a service or run it privileged | Compose or future IaC misconfiguration that nothing scans |
| SC-15 | Code vulnerability beyond the analyzers | Exploit an injection or unsafe API | No SAST such as CodeQL runs |
| SC-16 | CI controls never exercised | False confidence | Workflow, Dependabot and SHA pins were written but never run against a remote |

## Existing controls

- Central package management with transitive pinning (`Directory.Packages.props`); 56 committed lock files; locked
  restore in every build (`RestoreLockedMode` in `Directory.Build.props`) and in `ci.yml`. ADR-0004 records a lock drift failing restore (`NU1004`) in M0.
- `nuget.config` clears machine feeds and maps every package to nuget.org, which blocks dependency confusion through
  a second feed. NuGet audit covers transitive packages; high and critical fail the build.
- Every Compose image is pinned by version and `sha256` digest; `ComposeFileTests` fail every build on an unpinned
  image, a port not bound to `127.0.0.1`, a setting without a default, or a realm file with a credential other than the
  placeholder or a non-synthetic user. The database test container uses the same PostgreSQL digest as Compose.
- `ci.yml` uses `pull_request` (not `pull_request_target`), `permissions: contents: read`, `persist-credentials:
  false`, no repository secrets, a pinned runner image and SHA-pinned actions. It has not run.
- Base and Development settings hold no secret (`ConfigurationFilesTests`). Hosts refuse to start without required
  keys and name only the key; a non-test Stripe key is refused (`ConfigurationValidationTests`). The Migrator fails
  without echoing the owner connection string or password (`MigratorFailureTests`).
- The app role has no elevated attributes, owns nothing and cannot run DDL (`RolePrivilegeTests`); migrations stay in
  their module schema with no cross-schema foreign key (`SchemaOwnershipTests`, `ModuleSchemaTests`); the Migrator
  applies everything to an empty database and is idempotent (`MigratorTests`).
- A model change without a migration fails a test (`ModuleSchemaTests`). The host fails at start when a registration
  cannot be built (`HostCompositionTests`), so a broken composition does not start.
- Rules in `AGENTS.md` and `AGENTS.md`: versions only in `Directory.Packages.props`, never hand-edit a lock file, never
  edit an applied migration. These are review rules, not automated checks.

## Gaps and residual risk

| Gap | Residual risk | Tracked by |
|---|---|---|
| No secret scan over history or tree | A committed secret is found only by review | SC-A07, SC-A08 |
| Dependency audit fails only at high in the build | Moderate advisories stay warnings | SC-A02 |
| No image or configuration scan | Known image flaws and Compose or workflow misconfiguration go unseen | SC-A04, SC-A05 |
| A coordinated change to props and lock files passes locked restore | Relies on reviewing the diff | SC-A03 |
| No package signature or trusted-signer policy | A malicious release on nuget.org restores after the cooldown | SC-A01 |
| No automated old-code/new-schema compatibility test; no check that applied migrations are unchanged | Expand-contract is a policy enforced by review | SC-A12 |
| No SBOM, provenance, signing, IaC or IaC scan | Nothing to verify until images and infrastructure exist | SC-A14, SC-A15 |
| No CodeQL | Code flaws outside analyzer rules | SC-A16 |
| CI and Dependabot have never run | The pins and permissions are unproven on GitHub | SC-A17 |

## Mitigations with owner and due date

| Id | Abuse case | Mitigation | Owner | Due | Status | Evidence |
|---|---|---|---|---|---|---|
| SC-A01 | SC-01 | Keep source mapping to nuget.org, central versions and the 14-day cooldown; no trusted-signer list for now | user | M12 | accepted | Source mapping and the cooldown reduce typosquatting and fresh-compromise risk; signer pinning is reviewed with production delivery |
| SC-A02 | SC-02 | Dependency audit including transitive packages that fails at moderate or above in the scan script | Claude | M9 | done | `SecurityScanTests.Every_scanner_image_is_pinned_by_digest`, `SecurityScanTests.Every_exception_names_its_tool_id_reason_and_owner_and_has_not_expired` |
| SC-A03 | SC-03 | Locked restore detects drift; lock files are regenerated only by restore and reviewed in the diff | user | M12 | accepted | Single maintainer reviews every lock diff; revisit when outside contributors or CI arrive |
| SC-A04 | SC-04, SC-05 | Keep digest pins enforced (ComposeFileTests since ADR-0037; dev.ps1 validate before) and add a Trivy image scan of every Compose image failing on fixed HIGH or CRITICAL | Claude | M9 | done | `SecurityScanTests.Every_scanner_image_is_pinned_by_digest`, `SecurityScanTests.Every_exception_names_its_tool_id_reason_and_owner_and_has_not_expired` |
| SC-A05 | SC-07, SC-14 | Trivy configuration scan of deploy/compose and .github/workflows failing on HIGH misconfigurations | Claude | M9 | done | `SecurityScanTests.Every_scanner_image_is_pinned_by_digest`, `SecurityScanTests.Every_exception_names_its_tool_id_reason_and_owner_and_has_not_expired` |
| SC-A06 | SC-06, SC-07 | CI security job running the scan script with read-only permissions and SHA-pinned actions | Claude | M9 | done | `SecurityScanTests.The_ci_security_job_runs_the_scan_read_only_with_actions_pinned_by_commit` |
| SC-A07 | SC-08 | gitleaks secret scan over git history and working tree, with expiring reviewed exceptions | Claude | M9 | done | `SecurityScanTests.Every_scanner_image_is_pinned_by_digest`, `SecurityScanTests.Every_exception_names_its_tool_id_reason_and_owner_and_has_not_expired` |
| SC-A08 | SC-08 | Enable GitHub secret scanning and push protection when the remote is created | user | M12 | planned | Owner task when the GitHub remote is added (M0 guide) |
| SC-A09 | SC-09 | No secret in appsettings files; startup and Migrator errors name keys, never values; non-test payment keys refused | Claude | M2 | done | `ConfigurationFilesTests`, `ConfigurationValidationTests.Host_refuses_to_start_without_the_connection_string_and_names_only_the_key`, `ConfigurationValidationTests.Host_refuses_to_start_with_a_stripe_key_that_is_not_a_test_key_and_names_only_the_key`, `MigratorFailureTests` |
| SC-A10 | SC-10 | Hosts use a non-owner app role without elevated attributes or DDL rights; only the Migrator uses the owner role | Claude | M2 | done | `RolePrivilegeTests`, `MigratorFailureTests.Missing_owner_connection_string_exits_one_and_names_only_the_key`, `MigratorTests` |
| SC-A11 | SC-11 | Every model change has a migration, migrations stay in their module schema, reruns apply nothing | Claude | M3 | done | `ModuleSchemaTests.Each_module_has_a_migration_for_every_change_to_its_model`, `SchemaOwnershipTests.Module_migrations_create_objects_only_in_the_module_schema`, `MigratorTests.Second_run_applies_nothing_and_exits_zero` |
| SC-A12 | SC-11 | Automated check that the previous app version runs on the new schema and that applied migrations are unchanged | Claude | M12 | planned | M12 deployment pipeline |
| SC-A13 | SC-12 | Move to the .NET 10 SDK and runtime and restore the latestPatch pin in global.json | user | M12 | done | SPEC-0015, 2026-10-24: SDK 10.0.402 with `latestPatch`, `net10.0`, runtime-aligned packages on 10.0.13, images on the 10.0 bases. ADR-0005 superseded |
| SC-A14 | SC-13 | SBOM, build provenance and signed images for our own images | Claude | M12 | planned | M12 container images and delivery |
| SC-A15 | SC-14 | Infrastructure as code with a configuration scan (Trivy or Checkov) in the scan script | Claude | M12 | planned | M12 infrastructure as code |
| SC-A16 | SC-15 | Rely on .NET analyzers with warnings as errors; add CodeQL when CI runs | user | M12 | accepted | CodeQL cannot run meaningfully without CI (SPEC-0009 decision 10); analyzers cover common rules meanwhile |
| SC-A17 | SC-16 | Keep CI on hold; record the first real CI and Dependabot run when the owner adds a remote | user | M12 | accepted | Owner decision to keep CI on hold (SPEC-0009 decision 1); local scripts give the gate evidence meanwhile |

## Security tests and monitoring

- Tests: the classes cited above, plus the break-it proofs of ADR-0028 for the scan script (a planted fake secret, a
  package with a known advisory, an expired exception, a privileged Compose service), each followed by a revert.
- Gate: the full gate (`AGENTS.md`, "The gate") per milestone; the scans in `tests/Security` (SPEC-0009 AC 13) per milestone
  once it exists, with fresh evidence recorded in the milestone guide.
- Monitoring: none for delivery yet. Dependabot pull requests and GitHub alerts start when the remote exists.
  Deployment monitoring is M12.

## Review date and change triggers

Review at the M9 gate, then at M12 (own images, IaC, production identity and deployment). Also review when the
owner adds a GitHub remote or switches CI on, a new package feed or registry is added, the SDK moves to .NET 10, an
outside contributor joins, a workflow gains a secret or a write permission, a feature flag is introduced, or a
scanner exception is about to expire.
