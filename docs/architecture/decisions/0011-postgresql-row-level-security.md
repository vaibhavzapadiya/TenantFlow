# ADR-0011: PostgreSQL row-level security as defence in depth

- Status: Accepted
- Date: 2026-10-19
- Owners: TenantFlow tech lead (decision approved by the user on 2026-10-19)
- Related decisions: ADR-0010, ADR-0012
- Closes: Open Decision 20

## Context
TenantFlow uses a shared database and shared schema with a non-null `TenantId` on tenant-owned rows
(blueprint section 8). The primary controls are in the application: tenant context from an active
membership, tenant-scoped queries, and cross-tenant denial tests. EF Core query filters are a
convenience. The blueprint asks us to consider row-level security (RLS) as a database backstop, and,
if enabled, to document session and transaction context, connection pooling and reset behaviour,
migration ownership, and privileged roles, and to test pooled-connection leakage and fail-closed
behaviour.

M2's own tables fit RLS poorly: the membership lookup runs *before* a tenant context exists ("which
tenants does this user belong to?"). RLS fits tenant-owned business tables, the first of which arrives
in M3 (Catalog).

## Decision
Adopt RLS on tenant-owned business tables, implemented with the first one in M3, with these rules:
- **Context per transaction, not per session.** The application sets the tenant with
  `SELECT set_config('app.tenant_id', @tenantId, true)` inside each transaction (`is_local = true`), so
  the value disappears at commit or rollback and cannot leak to the next user of a pooled connection.
  A session-level `SET` is not used.
- **Fail closed.** Policies compare `tenant_id` with `current_setting('app.tenant_id', true)`. When the
  setting is absent, the comparison is null and no rows are visible or writable. Policies have both
  `USING` and `WITH CHECK`.
- **`FORCE ROW LEVEL SECURITY`** on each protected table, so even the table owner is subject to it.
- **Roles.** The application connects as a role that does not own the tables and has neither
  `BYPASSRLS` nor superuser. Migrations run as a separate owner role through the Migrator (ADR-0012).
  Any role with `BYPASSRLS` is reserved for documented platform operations and is audited.
- **Application controls stay primary.** RLS is a backstop for a missed filter or a raw query, not a
  replacement for authorization or cross-tenant tests.

Out of scope for M2: creating policies. M2 delivers the tenant context that will feed RLS and the
role separation in the Migrator (ADR-0012).

## Options considered
- **Adopt, implement in M3 (chosen).** Decision and design now; proof on the table where RLS pays off.
- **Adopt and implement in M2** on M2's tenant-owned tables (for example audit records). Earlier
  proof, but it grows M2, and the membership tables do not fit RLS well.
- **Do not adopt.** Least complexity, but no database backstop when an application bug slips through.

## Consequences
- Easier: a missed `WHERE tenant_id = ...` returns no rows instead of another tenant's rows.
- Harder: every tenant-scoped unit of work must run inside a transaction that sets the context;
  reporting and platform-wide queries need an explicit, audited path; query plans must be checked
  because policies add predicates.
- Follow-up in M3: an EF Core interceptor or unit-of-work step that sets the context, and the tests below.

## Validation (M3)
- With the tenant set to A, a raw query cannot read or write tenant B's rows.
- With no tenant set, a query returns no rows and an insert is rejected.
- Pooled-connection leakage: after a transaction for tenant A commits, the next transaction on the
  same pooled connection without a tenant sees no rows.
- The application role has no `BYPASSRLS` and does not own the tables (checked by a test query on
  `pg_roles` and `pg_tables`).

## Revisit triggers
Measured query-plan regressions from policies; a move to database-per-tenant or schema-per-tenant;
a need for cross-tenant reporting that cannot use an audited privileged path.
