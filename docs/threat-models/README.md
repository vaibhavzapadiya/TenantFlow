# Threat models

One model per exposed workflow, in the blueprint section 22 shape. Each model ends with its **actions**: every
mitigation and every gap is one row of its "Mitigations with owner and due date" table. That table is the action
register; no other list exists, so nothing can drift (SPEC-0009 criterion 1).

## Index

| Model | Scope | Action prefix |
|---|---|---|
| [0001](0001-tenant-boundary.md) | Authentication, tenant and platform route boundaries, tenant context, memberships, RLS | TB |
| [0002](0002-catalog-inventory-orders.md) | Catalog and search, inventory adjustments and reservations, orders | BW |
| [0003](0003-payments-subscriptions-refunds.md) | Payments, webhooks, reconciliation, sagas, subscriptions and renewals, refunds | PY |
| [0004](0004-messaging-and-worker.md) | Outboxes, publisher, RabbitMQ, consumers, due rows, Quartz, the Worker | MW |
| [0005](0005-imports-exports-and-storage.md) | CSV imports, exports and downloads, object storage, file expiry | IE |
| [0006](0006-platform-and-support-access.md) | Platform administrators and support operators | PS |
| [0007](0007-supply-chain-and-delivery.md) | Dependencies, images, CI, local secrets, database roles, migrations | SC |
| [0008](0008-data-protection-and-telemetry.md) | Personal data, logs, traces, metrics, errors, retention, backups | DP |

## Blueprint section 13 coverage

| Threat area (blueprint section 13) | Model |
|---|---|
| Cross-tenant data access and tenant-context confusion | 0001, 0002, 0004 |
| Broken object-level authorization and privilege escalation | 0001, 0002, 0003 |
| Credential theft, token validation errors, session misuse, account recovery abuse | 0001 (account recovery belongs to the IdP, ADR-0009) |
| Platform-admin and support-operator access | 0006 |
| Payment callback spoofing, replay, and duplicate processing | 0003 |
| Message forgery, replay, poison messages, and overprivileged consumers | 0004 |
| SSRF, unsafe uploads, CSV injection, and malicious imports | 0005 |
| Secret exposure, dependency and supply-chain compromise, container image risks | 0007 |
| Denial of service, noisy-neighbour tenants, expensive reports, queue floods | 0002, 0004, 0005 (rate limiting: M11) |
| Sensitive data in logs, traces, exports, backups, and retained objects | 0008, 0005 |
| Unsafe deployment, migration, or feature-flag changes | 0007 |

## Action rows

`| Id | Abuse case | Mitigation | Owner | Due | Status | Evidence |`

- **Id:** `<prefix>-A<nn>`, unique across all models.
- **Owner:** `Claude` (implementation) or `user` (a decision or a manual task).
- **Due:** a milestone, `M0` to `M14`.
- **Status:** one of three values.
  - `done`: the evidence cites at least one test in backticks, as `TestClass.TestMethod` or `TestClass`, and may add
    repository paths. Every reference must exist.
  - `planned`: the evidence says where it will be delivered (for example "SPEC-0009 AC 11"). The due milestone must
    not be closed.
  - `accepted`: the evidence gives the reason for the residual risk. The due milestone is when it is reviewed again.

`dotnet run --project tools/TenantFlow.Progress` checks every row on each run. It prints each error, adds it to the dashboard's Data health
tab, and exits with code 1.

## When to update

When a new entry point, actor, cross-tenant feature or authorization cache is proposed, review the model that covers it,
or add a new one. When a planned action is delivered, mark it `done` with its test in the same change.
