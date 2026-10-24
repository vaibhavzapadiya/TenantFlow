# ADR-0012: Module data ownership in PostgreSQL

- Status: Accepted
- Date: 2026-10-19
- Owners: TenantFlow tech lead (decision approved by the user on 2026-10-19)
- Related decisions: ADR-0002, ADR-0011
- Closes: Open Decision 16 (pulled forward from M3 to M2)

## Context
ADR-0002 deferred the mechanism for module data ownership to M3. M2 needs persistence earlier: the
Tenants and IdentityAccess modules must store tenants, memberships, and role-change audit records, and
the M2 gate requires real isolation tests, which mocked stores cannot provide (blueprint section 15:
do not mock EF Core query behaviour). The user therefore moved the roadmap slice "EF Core and
migrations run through the Migrator" from M3 into M2.

## Decision
- **One PostgreSQL database, one schema per module** (for example `tenants`, `identity_access`). Each
  module has its own `DbContext`, mapped only to its own schema, and its own migrations history table
  inside that schema.
- **No cross-schema foreign keys or joins between modules.** A module refers to another module's data
  by identifier and reads it through the owner's `Contracts` (ADR-0002).
- **Migrations run only through a Migrator host** (`src/Hosts/TenantFlow.Migrator`), as a separate
  owner role. The API and Worker never apply migrations at startup and connect as a non-owner
  application role with only the privileges they need (prepares ADR-0011).
- **Database tests in M2 run against the Compose PostgreSQL** on `127.0.0.1:55432`, creating a uniquely
  named database per test run and dropping it afterwards, in a separate test category that needs
  `dev.ps1 up`. When Docker is unavailable those tests report as blocked, not skipped silently.
  Testcontainers is decided with Open Decision 21 in M3.

## Options considered
- **Schema per module (chosen).** Ownership is visible in the database, permissions can be granted per
  schema, and extraction later is a schema move. Small cost: schema-qualified names and one migrations
  history per module.
- **Table prefixes in one schema.** Simplest, but ownership is only a naming convention, and
  permissions cannot be scoped per module.
- **Database per module.** Strongest separation, but cross-module transactions and operations cost
  more, with no evidence it is needed.

## Consequences
- Easier: architecture tests in M3 can check that each `DbContext` maps only to its own schema.
- Harder: a use case that needs data from two modules goes through contracts, not a join.
- The shared Compose database becomes a test dependency until Open Decision 21 is closed; test runs
  must not leave databases behind.

## Validation
- A test proves each module's migrations create tables only in its own schema.
- A test proves the application role cannot run DDL and does not own module tables.
- The Migrator applies all migrations to an empty database and is idempotent on a second run.

## Revisit triggers
A module is extracted (M14 lab); a module needs a different storage engine; Open Decision 21 changes
how database tests run.
