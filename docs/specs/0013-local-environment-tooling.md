# SPEC-0013: Local environment tooling

- Status: Implemented (2026-10-23; approved by the user the same day with the recommended answers to all open questions)
- Milestone: none (local tooling). Planned for after the M12 gate; the user started it on 2026-10-23, alongside M12
- Owner: the user
- Related: blueprint section 5 (repository layout) and "Docker Compose local environment"; ADR-0008, ADR-0011,
  ADR-0012, ADR-0029; SPEC-0001; SPEC-0012 open question 12; ADR-0036

## Outcome
A developer can read and change the local environment tooling without reading one 800-line PowerShell script.
The work that is really application setup (database roles, user-secrets, Keycloak dev passwords) is in typed,
unit-tested C#. Docker Compose still runs the dependencies, so the Docker-inside-WSL workstation keeps working.
Every command and guarantee that exists today still holds. Aspire is evaluated later, after the .NET 10 migration
(ADR-0036 explains why not now).

## Acceptance criteria
1. Given a fresh clone with Docker reachable, when the developer runs `./scripts/dev.ps1 up`, then the stack is
   healthy, both database roles exist with the ADR-0011 attributes, and the API, Worker and Migrator start with
   `dotnet run` and no further step. This is still the one documented command (blueprint, "Docker Compose local
   environment").
2. Given `up` ran, then the user-secrets of the three hosts hold the same keys `dev.ps1 up` writes today:
   `ConnectionStrings:TenantFlow` (Api, Worker; app role), `ConnectionStrings:TenantFlowOwner` (Migrator),
   `Messaging:RabbitMq:*` (Worker), `Authentication:Tenant:Authority`, `Authentication:Platform:Authority` and
   `Audit:OperatorReferenceKey` (Api). Other user-secret keys are left as they were.
3. Given a `.env` with a required value empty (`TENANTFLOW_APP_PASSWORD`, `TENANTFLOW_OWNER_PASSWORD` or
   `KEYCLOAK_DEV_USER_PASSWORD`), when `up` runs, then it exits non-zero and names the setting, never a value.
4. Given a password containing `'`, `\`, `;`, `=` and a space, when `up` runs, then the role, the connection string
   and the user-secret all hold it exactly (unit test on the connection-string and SQL-variable builders, plus one
   database test).
5. Given any step fails (psql, user-secrets, Keycloak admin API), then the output names the step and never contains a
   password, connection string or token. No secret appears on a command line (secrets go through standard input or
   files in the user profile).
6. Given `up` ran, then every `dev-*` user of both realms can obtain a token with `KEYCLOAK_DEV_USER_PASSWORD`, and
   `dev.ps1 smoke` passes as before.
7. `dev.ps1` keeps every command name and parameter it has today (`up [-Observability]`, `down`, `reset [-Yes]`,
   `status`, `validate`, `db-tests [-Filter]`, `rules`, `drill <name>`, `smoke`), so `verify.ps1`, CI and the
   docs keep working. Each command lives in its own file. `dev.ps1` only parses arguments and dispatches; it is short
   enough to read in one screen (target: under 120 lines).
8. Given a Compose file with an image that has no digest, a port published on an address other than 127.0.0.1, or a
   realm user that is not synthetic, when `validate` runs (locally or in CI), then it fails and names the service,
   port or user, as today.
9. Given `db-tests` runs, then it provisions the roles, runs the database tests, and fails if a
   `tenantflow_test_<run>_%` database is left behind, as today.
10. The Docker-or-WSL selection exists once, in a shared module that `dev.ps1`'s commands and `security-scan.ps1`
    both use. The setup tool does not choose again: the scripts pass their choice (`--wsl`) when it runs psql
    (decision 3).
11. The setup tool references no module, building block or host project (architecture test), is never deployed,
    and is built, format-checked and tested by `verify.ps1` like the rest of the solution.

## Invariants
- The application role never owns tables and never bypasses RLS (ADR-0011, ADR-0012).
- `deploy/compose/postgres/provision-roles.sql` stays the single role script, run unchanged locally and by
  Terraform (`deploy/terraform/environment/provisioning.tf`). The tool runs it; it does not re-implement it.
- The hosts never read `.env` (ADR-0008). Only the tooling reads it.
- Every published port is on 127.0.0.1; every image is pinned to a version and digest (ADR-0003).
- No secret in tracked files, command lines, logs or error messages (CLAUDE.md rule 6).

## Contract
The setup tool is a console app, `tools/TenantFlow.DevSetup/`, run as
`dotnet run --project tools/TenantFlow.DevSetup -- <command>`. The scripts call it; developers do not run it directly.

| Command | Does | Replaces in dev.ps1 |
|---|---|---|
| `env` | Create `.env` from `.env.example`, append keys added since; never change existing values | `Initialize-EnvFile` |
| `setup` | Provision roles (psql in the container, SQL on stdin), write user-secrets, set Keycloak dev passwords | `Invoke-ProvisionRoles`, `Set-*Secret*`, `Set-KeycloakDevPasswords` |
| `db-tests [--filter]` | Provision roles, run the database tests with the `TENANTFLOW_TEST_*` variables, check for leftover databases | `Invoke-DbTests` |

Exit code 0 on success, 1 on failure with a one-line message naming the step. User-secrets are written through
`Microsoft.Extensions.Configuration.UserSecrets` (the secrets path for each host's `UserSecretsId`), merging keys.

## Data
No schema change. Local files only: `deploy/compose/.env` (git-ignored) and each host's user-secrets JSON.

## Failure modes
- Docker engine down: the script stops before the tool runs, with today's fix instructions.
- Keycloak not ready yet: `up --wait` already waits for health; one admin-API failure fails `up` with the step named.
- `dotnet` missing: today `up` skips the user-secrets with a warning. With a C# tool, `up` fails with an instruction.
  The SDK is a prerequisite anyway.
- psql error: output is hidden (it can echo a password), the step is named, as today.

## Out of scope
- .NET Aspire. A spike after the ADR-0005 migration, with the criteria in ADR-0036.
- Changing the Compose stack, images, ports, realms, smoke checks, drills or rule tests.
- `dev.sh`. A POSIX twin is not part of this spec.

## Open questions
None blocking. Decided by the user on 2026-10-23 (the recommended answers):
1. **Tool location:** `tools/TenantFlow.DevSetup/`, with its unit tests in `tests/DevSetup/TenantFlow.DevSetup.Tests`
   (the repository's `tests/<Area>/` convention; the draft said `tests/Unit/`, which does not exist). The new top-level
   `tools/` folder deviates from the blueprint layout (section 5); ADR-0036 records it.
2. **`validate`** stays a small PowerShell script (`scripts/dev/validate.ps1`). CI runs it before the .NET SDK is
   set up.
3. **psql:** the tool runs `docker compose exec -T postgres psql` with the SQL on standard input, choosing between
   `docker` and `wsl.exe -e docker` itself, so `provision-roles.sql` stays the one role script.
4. **`db-tests`** moves into the tool.
5. **Spec number:** 0013. M13 takes the next free number.

## Slices
| Slice | Delivers | Criteria |
|-------|----------|----------|
| S1 | Shared Docker module; `dev.ps1` becomes a dispatcher; each command in `scripts/dev/<command>.ps1` with no behaviour change; `security-scan.ps1` uses the module | 7, 8, 10 |
| S2 | Setup tool with `env` and user-secrets writing (riskiest: merging user-secrets without losing keys; secret-free errors) | 2, 3, 5, 11 |
| S3 | Role provisioning and Keycloak dev passwords in the tool; `up` calls it; PowerShell versions removed | 1, 4, 5, 6 |
| S4 | `db-tests` in the tool; docs, ADR-0008/0012 wording, test messages updated | 9 |

## Verification
| Criterion | Test | Proven to fail without the change |
|-----------|------|------------------------------------|
| 7 | `LocalToolingTests.Dev_ps1_is_a_short_dispatcher_with_one_script_per_command`; `dev.ps1 drill` without a name exits 1 to its caller, `validate` exits 0 | yes: a stray `scripts/dev/proof.ps1` fails it |
| 8 | `dev.ps1 validate` passes on the committed file (code moved unchanged to `scripts/dev/validate.ps1`) | deferred to gate: planting a non-loopback port was refused by the agent's safety check; to be shown by hand |
| 10 | `LocalToolingTests.The_docker_or_wsl_choice_is_written_once_and_shared_with_the_security_scan`; `security-scan.ps1 -Only config` runs through the module (its 5 workflow findings also fail on the previous commit) | yes: the same stray script fails it |
| 2 | `HostSecretsTests.Each_host_gets_exactly_the_keys_it_reads_in_development`; `UserSecretsFileTests.Keys_are_merged_and_other_keys_are_kept`, `A_nested_or_differently_cased_existing_key_is_replaced_not_duplicated`; the built tool run end to end in a scratch copy (throwaway `UserSecretsId`s) wrote exactly these keys | yes: reading no existing file fails the merge tests |
| 3 | `EnvFileTests.An_empty_required_setting_is_named_without_any_value`; `HostSecretsTests.An_empty_database_password_stops_before_anything_is_written`; end to end: exit 1, setting named, no secrets file written | deferred to gate |
| 4 (in part) | `UserSecretsFileTests.A_value_with_awkward_characters_round_trips`; `HostSecretsTests.Hosts_connect_as_their_own_role_with_any_password_intact` (read back with Npgsql) | deferred to gate |
| 5 (in part) | `UserSecretsFileTests.An_unreadable_file_is_named_without_its_contents`; secrets go to files, never a command line | deferred to gate |
| 11 | `LocalToolingTests.The_setup_tool_references_no_tenantflow_project_and_is_built_with_the_solution` | deferred to gate |
| 1 | `HostSecretsTests.Hosts_connect_as_their_own_role_with_any_password_intact`; `DatabaseRolesTests.The_committed_role_script_keeps_both_roles_without_superuser_or_bypassrls`; end to end against an isolated Compose project (own name, ports, volumes): `setup --wsl` provisioned both roles without superuser, BYPASSRLS, CREATEDB or CREATEROLE, wrote all user-secrets, and ran again cleanly. From the main checkout after the merge, with the real `.env`: `dev.ps1 up -Observability` exited 0, the Migrator applied all migrations, and the API and Worker reported live with `dotnet run` and nothing else | deferred to gate |
| 4 | `DatabaseRolesTests.Psql_values_are_quoted_so_they_survive_intact`; end to end: the app password `p'a\s;s=w o` set through psql and user-secrets logged in over the published port, a wrong one was refused | yes: without doubling backslashes the quoting tests fail |
| 5 | `KeycloakDevPasswordsTests.A_failed_request_names_the_step_and_status_without_any_secret`; `DatabaseRolesTests.A_line_break_is_refused_and_the_setting_named_without_its_value`; psql output and errors are discarded | deferred to gate |
| 6 | `KeycloakDevPasswordsTests.Every_dev_user_of_both_realms_gets_the_env_password_and_nobody_else`; end to end: tokens issued for `dev-user-a` and `dev-operator` with the .env password; `dev.ps1 smoke` from the main checkout passed 14 of 14 checks | deferred to gate |
| 9 | `DatabaseTestRunTests.The_tool_passes_exactly_the_settings_the_database_tests_read`, `The_leftover_check_counts_only_this_runs_databases`; end to end: `dev.ps1 db-tests` in a temporary worktree against an isolated Compose project ran all 328 database tests green and found no leftover database; without a database it exited 1 naming the step | yes: dropping one variable fails the drift test |

Not shown: `validate` rejecting a planted non-loopback port (criterion 8). The agent's safety check refused to plant one; the
rule's code moved unchanged from the old `dev.ps1`. The user marked the spec `Implemented` on 2026-10-23 with this gap open.
