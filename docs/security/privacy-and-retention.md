# Privacy and retention

TenantFlow makes no certification claim (ADR-0025 decision 1). This document is the evidence for SPEC-0009 criteria 7 to
10: what every table holds, why, and how long. `PrivacyDocumentTests` fails when a table of a module schema is missing
here, or a row names a table that does not exist.

## Classes

| Class | Meaning |
|---|---|
| `public-product` | Catalog data a tenant publishes to its own customers |
| `tenant-confidential` | A tenant's business data and operational state: orders, stock, payments, jobs, timers, outboxes |
| `personal-pseudonymous` | Data that identifies a person only through an id or an identity provider subject: memberships, free-text reasons |
| `security-evidence` | Records kept to show what happened: audit records, webhook events |

## Tables

"Life of the tenant" means kept until tenant offboarding, which is later work (SPEC-0009 Q6). Every rule below runs in the
Worker's daily `retention` task, tenant by tenant under row-level security, at most 500 rows per transaction
(`TenantRetention`; each module's rules are in its `…Retention` class). Published outbox rows and stored files had their
own expiry before M9 and keep it.

| Table | Class | Purpose | Retention |
|---|---|---|---|
| `tenants.tenants` | tenant-confidential | Tenant name and status | Life of the tenant |
| `tenants.tenant_audit` | security-evidence | M2 lifecycle audit with the operator's issuer and subject | 400 days, then `tenants.purge_expired_audit()` |
| `tenants.outbox_messages` | tenant-confidential | Audit events waiting for the broker; ids and operator references only | 7 days after publication |
| `identity_access.memberships` | personal-pseudonymous | Who belongs to which tenant with which role: issuer, subject, role | Active: while a member. Removed: issuer and subject replaced 30 days after removal; the row stays for the life of the tenant |
| `identity_access.membership_audit` | security-evidence | M2 membership audit with the actor's issuer and subject | 400 days, then `identity_access.purge_expired_audit()` |
| `identity_access.outbox_messages` | tenant-confidential | Audit events waiting for the broker; membership ids only | 7 days after publication |
| `catalog.products` | public-product | The tenant's catalog | Life of the tenant |
| `catalog.import_batches` | tenant-confidential | Import batches applied exactly once, with their counts | 90 days after the batch was applied |
| `inventory.stock_balances` | tenant-confidential | Stock on hand and reserved per product | Life of the tenant |
| `inventory.stock_adjustments` | tenant-confidential | Append-only stock changes | Life of the tenant |
| `inventory.reservations` | tenant-confidential | Stock held for orders | Life of the tenant |
| `orders.orders` | personal-pseudonymous | Orders with the customer's membership id and a free-text cancellation reason | Life of the tenant |
| `orders.order_lines` | tenant-confidential | Order lines with product snapshots and prices | Life of the tenant |
| `orders.outbox_messages` | tenant-confidential | Order and audit events waiting for the broker | 7 days after publication |
| `orders.processed_messages` | tenant-confidential | Consumer de-duplication | 30 days |
| `orders.workflow_timers` | tenant-confidential | Due rows of order deadlines | Completed: 30 days. Dead: 90 days |
| `notifications.notifications` | personal-pseudonymous | Notifications recorded for a member | Life of the tenant (no delivery provider yet; revisit with one) |
| `notifications.processed_messages` | tenant-confidential | Consumer de-duplication | 30 days |
| `payments.payments` | personal-pseudonymous | Payments with the owner's membership id and the provider's token | Life of the tenant |
| `payments.refunds` | personal-pseudonymous | Refunds with the requester's membership id and a free-text reason | Life of the tenant |
| `payments.idempotency_keys` | tenant-confidential | Replays of create-payment and refund requests | 30 days after the request finished; unfinished keys stay |
| `payments.webhook_events` | security-evidence | Provider events already applied or ignored, for replay protection | 90 days |
| `payments.outbox_messages` | tenant-confidential | Payment and audit events waiting for the broker | 7 days after publication |
| `payments.workflow_timers` | tenant-confidential | Due rows of reconciliation and renewal charges | Completed: 30 days. Dead: 90 days |
| `subscriptions.plans` | public-product | The tenant's plans | Life of the tenant |
| `subscriptions.subscriptions` | personal-pseudonymous | Subscriptions with the subscriber's membership id | Life of the tenant |
| `subscriptions.workflow_timers` | tenant-confidential | Due rows of renewals | Completed: 30 days. Dead: 90 days |
| `imports_exports.stored_files` | tenant-confidential | Metadata of uploaded and exported files (key, size, checksum) | The file: 7 days. The row: 90 days, once no job names it |
| `imports_exports.import_jobs` | tenant-confidential | Import requests with the requester's membership id and their outcome | 90 days after the job ended |
| `imports_exports.import_row_errors` | tenant-confidential | Row, column and code of a rejected row, never its value | With its job: 90 days after the job ended |
| `imports_exports.export_jobs` | tenant-confidential | Export requests with the requester's membership id and their outcome | 90 days after the job ended |
| `imports_exports.outbox_messages` | tenant-confidential | Audit events waiting for the broker; membership ids only | 7 days after publication |
| `imports_exports.workflow_timers` | tenant-confidential | Due rows of job runs, abandoned uploads and file expiry | Completed: 30 days. Dead: 90 days |
| `audit.audit_records` | security-evidence | Who did what in a tenant, by reference only (SPEC-0009 criterion 2) | 400 days, then `audit.purge_expired()`, the only way a record leaves |
| `audit.processed_messages` | tenant-confidential | Consumer de-duplication | 30 days |
| `operations.support_grants` | security-evidence | Support access to a tenant: operator reference, ticket reference, window (ADR-0027) | Life of the tenant (the audit log keeps who used each grant for 400 days) |
| `operations.outbox_messages` | tenant-confidential | Support audit events waiting for the broker; references only | 7 days after publication |

## Pseudonymisation

A person's identity in TenantFlow is the issuer and subject on a membership. Thirty days after a membership is removed,
both are replaced (`erased`, `erased:{membership id}`), and the row stays because orders, payments and audit records
reference its id. The same person can be added again: the identity is unique among active memberships only. The two M2
audit tables keep the actor's issuer and subject until their 400-day purge (threat model 0008, DP-10).

## Notes

- The `scheduler` schema holds Quartz's own state (job and trigger names, no tenant data) and is outside the module
  schemas; Quartz manages its rows.
- Telemetry retention (Loki, Tempo, Prometheus) is set with M10, and backups do not exist yet (policy due with M13),
  as ADR-0025 records.
