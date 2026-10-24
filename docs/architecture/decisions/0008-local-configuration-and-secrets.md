# ADR-0008: Local configuration and secrets

- Status: Accepted
- Date: 2026-10-19
- Owners: TenantFlow tech lead (decision approved by the user on 2026-10-19)
- Related decisions: ADR-0006
- Closes: Open Decision 14

## Context
Local development runs dependencies in Docker Compose (inside WSL on the current workstation) and
runs the .NET hosts directly with `dotnet run` on Windows. Both need the same values, such as the
PostgreSQL password and ports. The blueprint requires that secrets stay out of source, fixtures, logs,
and committed `.env` files, and that local stacks use non-production credentials (sections 5 and 18).

## Decision
- `deploy/compose/.env.example` is committed. It holds non-secret local defaults (ports, database
  name, user) and a clearly local-only password. It must never contain a real or shared credential.
- `deploy/compose/.env` is git-ignored. The dev script creates it from the example when it is missing,
  and Compose reads it.
- The .NET hosts read credentials from configuration keys only (for example `ConnectionStrings:TenantFlow`).
  In Development, the dev script writes the connection string into .NET user-secrets, built from `.env`.
  In deployed environments the same keys come from environment variables or a secret manager (M12).
- `appsettings*.json` never contains a credential. Required settings are validated at startup, and
  validation errors name the missing key, never its value.
- Claude Code is denied from reading `.env` files by `.claude/settings.json`; scripts read them, not the agent.
- **Proposed amendment (2026-10-23, ADR-0037, not yet accepted):** the hosts' `appsettings.Development.json` holds
  the local-only connection strings and credentials, equal to the Compose defaults, and every committed password ends
  in `-local-only`; `.env` becomes an optional override file and user-secrets the way to override a host setting. No
  script writes user-secrets any more. Real and deployed credentials are unchanged: never in a file, always from the
  secret manager. Until the user accepts ADR-0037, the rest of this ADR describes the previous rule.

## Options considered
- **User-secrets fed from `.env` (chosen).** One source of local values, no credentials in tracked
  files, and the same configuration keys as production. Needs a small script step.
- **Local-only password in `appsettings.Development.json`.** Simplest, but it normalizes credentials in
  the files where production credentials usually leak.
- **Run the hosts in Compose so they read `.env` directly.** Consistent, but needs Dockerfiles now and
  slows the edit-run loop.

## Consequences
- Easier: rotating or changing the local password means editing `.env` and re-running the dev script.
- Harder: a fresh clone needs `scripts/dev.ps1 up` (or `dev.sh up`) once before `dotnet run` works.
- User-secrets are stored unencrypted in the user profile. That is acceptable for a local-only password,
  and not acceptable for anything shared.

## Validation
- A test proves the host refuses to start without the connection string, and that the error does not
  contain configuration values.
- `git ls-files` contains no `.env` file, and the repository has no credential in `appsettings*.json`.

## Revisit triggers
The hosts move into containers for local development, or a shared development environment needs
centrally managed secrets.
