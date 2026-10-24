# ADR-0018: Serialized per-tenant changes with PostgreSQL advisory locks

- Status: Accepted (2026-10-21 by the user)
- Date: 2026-10-19
- Owners: Project owner (decision and acceptance), Claude Code (draft)
- Related decisions: [ADR-0010](0010-trust-boundaries-between-user-types.md), [ADR-0012](0012-module-data-ownership.md), [ADR-0013](0013-use-case-dispatch.md); SPEC-0002 criterion 20 and decision 7

## Context
A tenant that has had an administrator must always keep one (SPEC-0002 criterion 20). The rule reads a count
("how many active administrators?") and then changes a row, so two administrators who demote each other at the same
moment would both pass the check and leave the tenant with none. SPEC-0002 said "a tenant-level row lock", but
IdentityAccess owns no tenant row: the tenant row lives in the Tenants module's schema, and reaching into it breaks
[ADR-0012](0012-module-data-ownership.md).

## Decision
- Every membership change runs in one database transaction that first takes `pg_advisory_xact_lock` on a key derived
  from the tenant id (`hashtextextended(tenant_id::text, 0)`). Changes to one tenant queue; changes to different tenants
  do not wait. The lock belongs to the transaction, so it is released on commit or rollback and can never leak through a
  pooled connection.
- The Application layer sees this only as `IMembershipStore.BeginChangeAsync(tenantId)`, which returns a change object
  scoped to that tenant. It cannot read or write another tenant's memberships, and the count and the change happen inside
  the same lock.
- The unique index on active memberships and the database check constraints stay as a backstop, not as the mechanism.
- Out of scope: other modules' changes. A module that needs the same guarantee adopts the same pattern behind its own
  store, with its own lock key space.

## Options considered
- **Advisory lock per tenant (chosen).** No extra table, no cross-module access, scoped to the transaction. Costs: a lock
  key is a hash, so two tenants can rarely share a queue (harmless, only slower); the lock is invisible in the schema, so it
  is documented here and proven by a test that fails without it.
- **A per-tenant row in `identity_access`.** Explicit and visible, lockable with `SELECT ... FOR UPDATE`. Costs: a table and
  a migration, and the row must exist before the first administrator is added.
- **Lock the administrator rows.** No new object. Does not protect a tenant that has no administrator yet from two
  concurrent first additions, and it locks only the rows that happen to exist.
- **Serializable isolation with retry.** Correct without explicit locks, but needs a retry policy and surfaces conflicts as
  failures the caller must handle; heavier than the need.

## Consequences
- All membership writes go through the store's change object. Adding a write path that skips it would reopen the race, so
  the store exposes no other way to write.
- A long transaction on one tenant delays that tenant's other membership changes. Changes are short and make no remote call
  ([`CLAUDE.md`](../../../CLAUDE.md), rule 3).
- Tests that prove it must be able to fail: the 20-round concurrent-demotion test fails when the lock is removed.

## Validation
`MembershipServiceTests.Two_administrators_demoting_each_other_at_once_leave_exactly_one_winner_every_time` (20 rounds),
and `MembershipAtomicityTests.A_failed_change_releases_the_tenant_lock_so_the_next_change_succeeds`.

## Revisit triggers
Membership changes become frequent enough that per-tenant queuing shows up in latency; a second module needs the same
guarantee across tables; or the platform moves off PostgreSQL for this module.
