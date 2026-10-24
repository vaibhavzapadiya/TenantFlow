# M9 — Security and privacy

**Goal:** every exposed workflow has a threat model with tracked actions; sensitive actions leave an append-only audit
trail the tenant can read; data that is no longer needed is deleted or pseudonymised on a published schedule; background
jobs stop when their requester loses the right; every change can be scanned for secrets, vulnerable dependencies and
images and unsafe configuration; platform administrators and support operators have separate, least-privilege authority.
None of this is a certification claim (Open Decision 8, ADR-0025).

**Gate (roadmap):** threat-model actions are tracked; high-risk security tests and pipeline checks pass.

Spec: [SPEC-0009](../specs/0009-security-and-privacy.md) (Implemented). Decisions:
[ADR-0025](../architecture/decisions/0025-compliance-goals-data-classification-and-retention.md),
[ADR-0026](../architecture/decisions/0026-audit-module-and-append-only-audit-log.md),
[ADR-0027](../architecture/decisions/0027-platform-admin-and-support-operator-boundary.md) and
[ADR-0028](../architecture/decisions/0028-security-scanning-in-the-pipeline.md), all **Proposed**: you accept them.
Open Decision 8 closes when you accept ADR-0025.

## 1. What was built

| Slice | Commit | Delivers |
|---|---|---|
| S1 | `a383eab` | Threat models 0002–0008 and a reviewed 0001; 184 tracked actions; the `progress.ps1` action check |
| S2 | `2e0f19d` | Audit module: `audit.audit_records` (append-only, forced RLS), outbox write path from Tenants and IdentityAccess, idempotent consumer, operator HMAC reference |
| S3 | `98a89e5` | Audited imports, exports, downloads, refunds, paid cancellations; `GET /v1/tenants/{id}/audit` (`audit.read`) and `GET /v1/platform/audit?tenantId=` |
| S4 | `37d3976` | `docs/security/privacy-and-retention.md` (every table classified, checked by a test); per-tenant retention run; pseudonymisation of removed members; three audit purge functions |
| S5 | `658ddfd` | Jobs stop when the tenant is suspended or the requester loses `catalog.manage` (M8 L10); storage `0600`/`0700`, flush, temp sweep (L14); Worker EF log level |
| S6 | `7c1d12c` | `scripts/security-scan.ps1` (gitleaks, NuGet audit, Trivy images, config checks), dated exceptions, CI `security` job; leaner slice workflow |
| Images | `cd55b11` | PostgreSQL 17.11, Keycloak 26.8.0, RabbitMQ 4.3.6; 58 dated image exceptions (expire 2026-11-21) |
| S7 | `787a9c6` | `platform-admin` / `support-operator` split; Operations module with support grants; read-only, audited support views; platform route matrix |

## 2. Architectural reasoning

- **Audit through the outbox, not a shared table.** Each module writes `audit.recorded.v1` in the transaction of its
  change; the Audit consumer records one row per event id. An audited change and its record commit together or not at
  all, and no module touches Audit's tables (ADR-0002, ADR-0026).
- **Retention under RLS, tenant by tenant.** Modules declare rules; one runner executes them per tenant in 500-row
  transactions as the application role. No role bypasses RLS; audit rows leave only through `SECURITY DEFINER` purge
  functions with a fixed 400-day cutoff (ADR-0025).
- **Pseudonymise, don't delete, people.** A removed membership keeps its id (orders, payments, audit reference it) and
  loses its issuer and subject after 30 days.
- **Support is a grant, not a role with standing access.** Time-limited, ticket-referenced, read-only, audited before
  every read and visible to the tenant (ADR-0027).
- **Scans are one script, locally and in CI** (ADR-0028); Trivy could not read Compose or workflows, so those checks
  live in the script.

## 3. Security considerations

- Tenant isolation: audit reads are scoped by the membership's tenant and forced RLS; retention deletes filter by
  tenant even in tables outside RLS (proven); support views need the caller's own active grant (proven).
- Personal data: audit stores membership ids and keyed operator references only; the support member view has no
  issuer or subject; scan output is redacted.
- Residual risks: the operator HMAC key is a local placeholder in Development (S2-L6); 58 image CVEs are excepted
  until 2026-11-21; Unix file modes are tested on Linux only; CI has never run (on hold).

## 4. Verification

Fresh evidence for the gate (2026-10-22), branch `m9-security-privacy` after merging the image fixes:

| Command | Result |
|---|---|
| `./scripts/verify.ps1 -Database -Security` | format clean; Release build 0 warnings; IdentityAccess 68, Tenants 77, Catalog 9, Architecture 10, Inventory 5, Orders 6, Payments 14, Subscriptions 7, ImportsExports 27 (+1 Unix-only skip), Worker 16, Api 132; Database 325/325. Security scan: dependencies, images and configuration pass; secrets failed once on the spec quoting a test placeholder, reworded plus one history exception, then pass |
| `./scripts/progress.ps1` | 184 threat-model actions in 8 models (124 done, 26 planned, 34 accepted); action check passes |
| `./scripts/dev.ps1 up -Observability` | stack on PostgreSQL 17.11, Keycloak 26.8.0, RabbitMQ 4.3.6; fixed a PowerShell 5.1 bug that the second synthetic platform user exposed |

**Break-it proofs** (each failed at an assertion, then restored): no forced RLS on audit tables; no deduplication on the
event id; an audit event committed before a refusal; an event saved apart from its change; another tenant as target;
audit reads without tenant filter and RLS; a retention delete without its tenant filter; jobs without the requester
check; a privileged Compose service; a support access check that ignores the grant's operator.

**Not verified:** the three-agent milestone review (skipped at your request); `progress.ps1 -Verify` (coverage run) and
re-proofs of the guards the slices deferred (skipped for speed, your instruction); Unix file modes (Linux only);
CI (on hold); `dev.ps1 smoke` (needs the API and Worker started by hand).

## 5. Trade-offs and assumptions

- One composed retention task instead of one per module; a Worker test pins that every module's rules are included.
- `support_grants` is platform-owned and not under RLS, like `tenants.tenants`.
- The platform audit read needs a `tenantId`: no audited action is platform-only.
- Review agents ran per slice for S2 only; the milestone review was skipped at your request (2026-10-22).

## 6. Mentor exercises

1. Remove the tenant filter from a retention rule on `workflow_timers`. Which test fails, and why does RLS not save you?
2. Move the support access record after the read. Construct the incident where a read leaves no trace.
3. Make the audit consumer use a fresh id instead of the message id. What does a redelivery do?

## 7. Interview talking points

- "How do you make an audit log trustworthy?" Same-transaction outbox intent, idempotent consumer, append-only by
  privileges and a trigger, deletion only through a fixed-cutoff definer function.
- "How do you give support access without a backdoor?" Separate role, per-tenant time-boxed grant with a ticket, every
  read audited first and visible to the customer, read-only contracts.

## 8. Exit checklist

- [x] Threat-model actions tracked and checked (`progress.ps1`)
- [x] SPEC-0009 criteria 1–19 implemented with verification rows
- [ ] **You:** accept or amend ADR-0025 to ADR-0028 (closes Open Decision 8)
- [ ] **You:** review the 58 image exceptions (expire 2026-11-21) and the S2 open findings
- [ ] **You:** approve the start of M10 (its spec draft is on branch `claude/sad-faraday-079933`)
