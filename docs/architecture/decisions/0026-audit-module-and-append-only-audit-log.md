# ADR-0026: Audit module and the append-only audit log

- Status: Proposed
- Date: 2026-10-22
- Owners: TenantFlow tech lead (the user decides)
- Related decisions: ADR-0002, ADR-0010, ADR-0011, ADR-0012, ADR-0021, ADR-0025, ADR-0027

## Context
Blueprint section 6 lists an **Audit** module that holds "security-sensitive and business audit records, retention
metadata", which every module reaches "via a narrow append-only interface". Section 13 lists the actions to audit: role
changes, tenant suspension, data export, refunds, support access, configuration changes, and privileged operations.

M2 built two module-local audit tables, `identity_access.membership_audit` and `tenants.tenant_audit`. Each is written
in the same transaction as its change, and kept append-only by revoking `UPDATE`, `DELETE` and `TRUNCATE`. They have no
RLS and no read API. Both store the actor's OIDC issuer and subject. Exports, downloads, refunds and support access are
not audited.

Each module has its own `DbContext` and connection (ADR-0012). A central table therefore cannot simply join a module's
transaction.

## Decision (proposed)
1. **A new Audit module** with schema `audit`, a single table `audit_records`, and an `Audit.Contracts` project that
   defines the event `audit.recorded.v1` and the closed list of action codes.
2. **Write path: the transactional outbox (ADR-0021).** A module that performs an audited action writes the audit
   event to its own outbox, in the same transaction as the change. The Worker publishes it, and the Audit consumer
   inserts it, deduplicated on the event id. Tenants, IdentityAccess, ImportsExports and Subscriptions gain an outbox
   where they lack one. The change and its audit intent commit together. The record appears shortly afterwards, and a
   broker outage delays it but never loses it.
3. **Who the actor is, without personal data.** `actor_kind` is `member`, `platform_admin`, `support_operator` or
   `service`. `actor_ref` is the membership id for members. For operators it is an opaque reference: SHA-256 of
   issuer and subject under a configured key (SPEC-0009 Q9). No OIDC subject of a tenant user, no names, and no free text
   are stored. The single exception is a support grant's ticket reference, whose pattern is fixed.
4. **Append-only by three controls.**
   - The app role has `INSERT` and `SELECT` only.
   - A `BEFORE UPDATE` trigger refuses every role, the owner included.
   - `DELETE` happens only through `audit.purge_expired()`, a `SECURITY DEFINER` function whose cutoff (400 days,
     ADR-0025) is fixed in the migration. It is the only grant the app role has that removes rows.
5. **Reads.** Forced RLS on `tenant_id`, with tenant-less (platform) rows visible only on the platform path.
   - Tenant administrators read their tenant through `audit.read`.
   - Platform administrators read across tenants by selecting a tenant (`tenantId`), one tenant-scoped transaction at a
     time, or the platform-only rows.
   - Support access events are visible to the tenant.
6. **Existing tables (SPEC-0009 Q3, recommended).** Keep writing `membership_audit` and `tenant_audit` unchanged in M9.
   Add the outbox event beside them, and backfill nothing. Retiring them is a later contract step with its own
   migration.

## Options considered
- **Outbox → broker → Audit consumer (chosen).** It reuses the outbox, the publisher, deduplication, retries and the
  dead-letter path that M5 proved. Atomic with the change, and the module boundary stays clean. Cost: eventual
  visibility (seconds), more outbox tables, and a dependency on the broker for freshness.
- **An in-process outbox relay.** A per-module relay calls `IAuditIngest.AppendAsync` directly, with no broker. Fewer
  moving parts at run time, but it adds a second delivery mechanism beside ADR-0021 and needs its own retries and
  monitoring.
- **Enlisting in the caller's transaction.** The Audit `DbContext` shares the module's `DbConnection` and
  `DbTransaction`. It is immediate and atomic, but connection and transaction types leak into a contract, and every module
  becomes coupled to Audit's schema being reachable in its own transaction.
- **Module-local tables with a merged read.** Each module keeps its own audit table, and the Audit API fans out and
  merges. Writes are simplest, but reads, paging and retention are spread across N modules, and blueprint section 6's
  single owner is lost.
- **A hash chain per tenant** (SPEC-0009 Q4). Each record stores the hash of the previous one, so a deletion or an edit
  by the database owner becomes detectable. Not chosen for M9: it needs serialised writes per tenant and a verifier
  job. Privileges and the trigger cover the threat from the app role; the owner and DBA threat is recorded as residual risk.

## Consequences
- An audited action costs one extra outbox row. The audit record is eventually consistent, and tests wait for it.
- Every new audited action must add an action code to `Audit.Contracts` (closed list, tested).
- An operator reference depends on a configured key. Losing the key loses the link to an operator, not the record.
- The two M2 tables duplicate part of the log until they are retired.

## Validation
Database tests:
- exactly one record per committed action, none for a rolled-back one, and none extra for a redelivery;
- the app role is refused `UPDATE`, `DELETE` and `TRUNCATE`, the owner is refused `UPDATE`, and the purge function
  deletes nothing younger than the cutoff;
- the cross-tenant read is 404 and is filtered under forced RLS;
- the broker is down at commit and the record arrives later.

Each guard has a break-it proof.

## Revisit triggers
A requirement for tamper evidence against the database owner (hash chain or a WORM store); audit volume that measurably
loads the broker; a need for synchronous audit (for example a regulator's "no action without a record").
