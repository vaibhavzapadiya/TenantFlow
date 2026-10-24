## What and why

<!-- The business or engineering outcome. Link the issue (Fixes #123), and the spec or ADR if there is one. -->

## How it was verified

- [ ] `dotnet test TenantFlow.sln -c Release` passes locally
- [ ] The database tests (`dotnet run --project tests/Database/TenantFlow.Database.Tests -c Release`) pass, or the change does not touch the database, messaging or `tests/Database`
- [ ] New or changed behaviour has tests, including at least one failure or denial path
- [ ] Tenant-scoped behaviour has a test showing another tenant is denied, or not applicable

## Architecture and risk

- [ ] No change to a module boundary, dependency rule, or public contract, **or** an ADR is added/updated
- [ ] Spec-path changes (tenancy, authorization, money, audit, schema, contracts, messaging, concurrency) have an approved spec
- [ ] No secrets, tokens, or personal data in code, config, fixtures, or logs
- [ ] Database changes are backward-compatible (expand-contract), or not applicable
- [ ] Docs updated where behaviour or operations changed

## Licensing

- [ ] I have the right to submit this contribution, and I agree it is licensed under the project's MIT License (`LICENSE`)
