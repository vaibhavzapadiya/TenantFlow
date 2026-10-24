# ADR-0025: Compliance goals, data classification, and retention

- Status: Proposed
- Date: 2026-10-22
- Owners: TenantFlow tech lead (the user decides)
- Related decisions: ADR-0009, ADR-0011, ADR-0012, ADR-0021, ADR-0024, ADR-0026
- Closes (when accepted): Open Decision 8 (compliance goals)

## Context
Blueprint section 2.8 says compliance goals do not mean certification: build privacy, audit, access control, retention
and evidence practices, and claim nothing without a formal programme. Section 13 asks for data classification, purpose
limitation, retention schedules, deletion or anonymisation processes, and a backup expiry policy.

The facts at the end of M8:
- TenantFlow stores no names, emails or IP addresses. The personal data it holds is the OIDC issuer and subject in
  memberships and in two audit tables, membership ids in nine tables and in outbox payloads, payment-method tokens, and
  three free-text reasons.
- Only published outbox rows (7 days) and stored files (7 days) expire. Processed messages, webhook events, idempotency
  keys, timer rows, job rows, audit rows and removed memberships are kept forever.
- Most tables have forced RLS, and the app role has no `BYPASSRLS` (ADR-0011). A single query cannot delete across
  tenants, and that is intentional.

## Decision (proposed)
1. **Compliance stance.** TenantFlow makes no certification claim (SOC 2, ISO 27001, PCI DSS, GDPR compliance). It
   produces evidence: threat models with tracked actions, an append-only audit log, an access model, a retention
   schedule with automated deletion, and scan results. `docs/security/` holds `security-baseline.md`,
   `access-model.md` and `privacy-and-retention.md` (blueprint section 22). Each control links to the test that proves it.
2. **Classification.** Every table gets one of four classes: `public-product` (catalog data a tenant publishes),
   `tenant-confidential` (orders, stock, jobs), `personal-pseudonymous` (OIDC subjects, membership ids, free-text
   reasons) and `security-evidence` (audit, webhook events). Each table also gets a purpose and a retention rule. A
   database test compares `information_schema` with the document, so a new table without a rule fails the build.
3. **Retention schedule** (owner to confirm, SPEC-0009 Q5):

   | Data | Keep | Then |
   |---|---|---|
   | Processed messages | 30 days | delete |
   | Webhook events | 90 days | delete |
   | Finished idempotency keys | 30 days | delete (a later replay is treated as a new request) |
   | Completed / dead workflow timers | 30 / 90 days | delete |
   | Finished import and export jobs, row errors, batches, file rows | 90 days after the end | delete |
   | Published outbox rows, stored files | 7 days (unchanged) | delete |
   | Removed memberships | 30 days after removal | pseudonymise issuer and subject; keep the row |
   | Audit records | 400 days | delete through `audit.purge_expired()` |
   | Orders, payments, refunds, subscriptions, products, stock | life of the tenant | (offboarding is out of scope) |
   | Local telemetry (Loki, Tempo, Prometheus) | 7 days (target) | not configured today, so image defaults apply; set in Compose with M10, deployed values with M12 (threat model 0008, DP-A12) |
   | Backups | none exist yet | policy due with M13 |

4. **Mechanism: per tenant, under RLS.** Each module owns a Quartz cron task (the `ScheduledTask` pattern, ADR-0023). It
   gets tenant ids page by page from a new `Tenants.Contracts` query, then deletes that module's expired rows per tenant
   inside a `TenantScopedTransaction`, at most 500 rows per batch. Tables outside RLS (timers, outbox, processed
   messages where unforced) are deleted directly in batches. No role gains `BYPASSRLS`.
5. **Pseudonymisation, not erasure on request.** A person's identity in TenantFlow is the (issuer, subject) pair on a
   membership. Audit records reference the membership id, not the subject (ADR-0026). Replacing the pair with
   `erased:{membershipId}` once the removal is 30 days old therefore pseudonymises the person everywhere, and keeps every
   reference intact. An on-request erasure operation and tenant offboarding are later work (SPEC-0009 Q6).

## Options considered
- **Retention by a privileged role that bypasses RLS.** One `DELETE ... WHERE created_at < cutoff` per table is simple and
  fast. But it adds a role that crosses tenants, and a bug in it deletes for everyone. Rejected.
- **An RLS policy with a "retention mode" setting.** Any code path could switch the setting on, so this weakens RLS for
  all deletes. Rejected.
- **Per-tenant deletion under RLS (chosen).** It keeps the ADR-0011 guarantee. It costs one transaction per tenant per
  table per run, which is acceptable at this scale; measure it in M11.
- **Due rows per record** (like file expiry). This is exact, but it doubles the writes for high-volume tables
  (processed messages, idempotency keys). Kept where it already exists (files), not extended.
- **Hard delete of removed memberships.** Orders, payments and audit records reference them, so it would break
  references or need cascades. Rejected in favour of pseudonymisation.

## Consequences
- Every new table must state its class and retention, or the build fails.
- An idempotency key replayed after 30 days creates a new operation. That is acceptable: providers use 24 hours.
- Audit history is limited to 400 days, which covers a year plus a month of review.
- Retention runs read the tenant list. Suspended tenants are included, so their data still expires.

## Validation
Database tests per retention rule: a row just outside its window is deleted, a row just inside is kept, and another
tenant's expired rows are also deleted, in a separate transaction. A pseudonymisation test, a document-completeness
test, and a break-it proof for each, run at runtime.

## Revisit triggers
A real tenant with a contract or a regulator that sets retention; backups (M13); a production identity provider that
offers account deletion events (M12); a measured purge cost (M11); a request for tenant offboarding.
