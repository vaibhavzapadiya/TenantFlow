# SPEC-0009: Security and privacy

- Status: Implemented (2026-10-22; approved the same day by the user, with the recommendation for every open question)
- Milestone: M9
- Owner: TenantFlow tech lead (user)
- Related: blueprint sections 2.8, 2.11, 3 (actors), 6 (Audit, Operations), 8, 13, 15 (security tests, CI gates), 18
  ("GitHub Actions and supply chain"), 22 (`security/`, threat-model template); ADR-0010, ADR-0011, ADR-0012, ADR-0021,
  ADR-0023, ADR-0024; ADR-0025 to ADR-0028 (Proposed); threat model 0001; SPEC-0002, SPEC-0007 (R9, R11), SPEC-0008
  (deferred L10, L14); Open Decisions 8 and 11

## Outcome
Every exposed workflow has a threat model whose actions are tracked to a test, a later milestone, or an accepted risk.
Tenant administrators can see an append-only history of the sensitive things that happened in their tenant, including
every time support looked at it. Data that is no longer needed is deleted or pseudonymised on a published schedule.
Background jobs stop when the person who asked for them loses the right to. Every change is scanned for secrets,
vulnerable dependencies, vulnerable images and unsafe configuration. Platform administrators and support operators have
separate, least-privilege authority: support sees a tenant only with a recorded reason, for a limited time, read-only,
and audited. None of this is a certification claim (Open Decision 8).

## Acceptance criteria
**Threat models (docs and a check)**
1. Given the exposed workflows (catalog, inventory and orders; payments, webhooks, subscriptions and refunds; messaging
   and the Worker; imports, exports and storage; platform and support; supply chain and delivery), then each has a threat
   model in `docs/threat-models/` in the blueprint section 22 shape, and 0001 is reviewed. Every mitigation and gap is an
   action row in its model's mitigation table (id, abuse case, mitigation, owner, due milestone, status `done` |
   `planned` | `accepted`, evidence), indexed in `docs/threat-models/README.md`. The tracker check
   (`progress.ps1`) fails when a row lacks a field, a `done` row names a test that does not exist, or a `planned` row is
   due in a milestone that is already closed. (S1 amendment: the rows stay in each model instead of a separate
   `actions.md`, so they cannot drift apart.)

**Audit (new Audit module)**
2. Given an audited action commits (membership added, role changed, removed; first tenant administrator; tenant created,
   suspended, reactivated; import and export requested; export file downloaded; refund requested; paid order cancelled;
   support grant opened, used, ended), then exactly one record with that event id appears in `audit.audit_records`: tenant
   (none for platform-only events), actor kind and reference, action, target type and id, trace id, time. A rolled-back
   action records nothing; a redelivered event records nothing more. No token, name, email, OIDC subject of a tenant user,
   or free text is stored, except a support grant's ticket reference.
3. Given the app role, when it runs `UPDATE`, `DELETE` or `TRUNCATE` on audit records, then PostgreSQL refuses; a trigger
   also refuses `UPDATE` for every role, the owner included. Rows leave only through the retention function of
   criterion 10.
4. Given a holder of the new `audit.read` permission (tenant administrator), when they call `GET .../audit`, then their
   tenant's records are returned newest first with keyset paging and an optional `action` filter. A customer gets 403; a
   member of tenant B, a removed member, or a non-member gets 404, and tenant B's records never appear (filter and forced RLS).
5. Given a platform administrator, when they call `GET /v1/platform/audit?tenantId=&action=`, then records are returned,
   platform-only events included. A support operator gets 403; a tenant token gets 401.
6. Given the broker is down, when an audited change commits, then it still commits and its record arrives after recovery.

**Retention and deletion**
7. Given `docs/security/privacy-and-retention.md`, then every table in every module schema has a classification, a
   purpose and a retention rule. A database test fails when a table is missing from the document.
8. Given rows past their retention (Q5), when the module's retention task runs, then they are deleted tenant by tenant
   inside a tenant-scoped transaction, in bounded batches. A row one second inside its window stays.
9. Given a membership removed more than 30 days ago, when retention runs, then its issuer and subject are replaced by a
   non-reversible marker. The row stays, because orders, payments and audit records reference its id. The same person can
   be added again.
10. Given audit records older than the audit retention (Q5), when audit retention runs, then they are deleted by
    `audit.purge_expired()`, which can delete nothing younger. Younger records stay. The same holds for
    `identity_access.membership_audit` and `tenants.tenant_audit`, each through a purge function in its own schema
    (decision 15).

**Background jobs and storage (M8 L10, L14)**
11. Given a queued or running import or export, when its tenant is suspended, or its requester is removed or no longer
    holds `catalog.manage`, then the next run (or the next batch) ends the job with `tenant_suspended` or
    `requester_not_authorized` before any further write. Batches already applied stay.
12. Given storage on a Unix host, when a file or directory is created, then it has mode `0600` or `0700`. The final file
    is flushed before the rename. A `.tmp` file older than one hour is deleted by a cron task.
19. Given the API and the Worker running outside Development, then Entity Framework Core logs at `Warning` or above
    only, so SQL text never reaches their logs (decision 14). A test asserts the effective level in both hosts.

**Scanning (scripts and CI)**
13. Given `./scripts/security-scan.ps1` (also `verify.ps1 -Security`), then it runs a secret scan over the git history
    and the working tree, a dependency audit, an image scan of every Compose image and a configuration scan of Compose
    and the workflows. Each tool is pinned by digest. It fails on a planted fake secret, a vulnerable package version,
    an image with a fixed HIGH or CRITICAL vulnerability, or a HIGH misconfiguration. An exception lives only in a
    reviewed ignore file with a reason and an expiry, and an expired exception fails the scan.
14. Given `ci.yml`, then a `security` job runs the same script with read-only permissions and SHA-pinned actions (Q1).

**Platform administrator and support operator (new Operations module)**
15. Given the roles `platform-admin` and `support-operator` (replacing `platform-operator`), then lifecycle, first
    administrator and platform audit require `platform-admin`, and support routes require `support-operator`. Each
    gets 403 on the other's routes; a tenant token gets 401 on both. A new test walks every `/platform` route as each
    kind of caller.
16. Given a support operator, when they `POST /v1/platform/support/grants` with a tenant, a ticket reference and a
    duration of at most 240 minutes, then 201 with a grant and an audit record visible to that tenant. An unknown tenant
    is 404, a bad reference or duration is 400, and a second active grant for the same operator and tenant is 409.
17. Given an active grant, when its operator reads `GET /v1/platform/support/tenants/{tenantId}/members` or `.../jobs`,
    then the read-only view is returned, and a `support.tenant_accessed` record is committed before any data is read.
    With no grant, an expired, ended or revoked grant, another operator's grant, or a grant for another tenant, the answer
    is 404 and nothing is read. No support route writes tenant data.
18. Given an active grant, when its operator ends it or a platform administrator revokes it, then the next access is 404
    and the end is audited.

## Invariants
- Audit records are never changed. They leave only through the retention function, and only after retention.
- An audited change and its audit intent commit together or not at all.
- Support never reads a tenant without an active grant and a committed access record, and never writes tenant data.
- No retention run deletes across tenants in one transaction or uses a role that bypasses RLS.
- No module reads another module's tables. The Audit and Operations modules use `Contracts` only (ADR-0002).

## Contract
| Operation | Authorization | Answer |
|---|---|---|
| `GET /v1/tenants/{tenantId}/audit?action=&cursor=` | `audit.read` (tenant administrator) | `{items: [AuditRecord], nextCursor}` |
| `GET /v1/platform/audit?tenantId=&action=&cursor=` | `platform-admin` | same |
| `POST /v1/platform/support/grants` `{tenantId, ticket, minutes}` | `support-operator` | 201 `SupportGrant` |
| `DELETE /v1/platform/support/grants/{grantId}` | grant owner, or `platform-admin` | 204 |
| `GET /v1/platform/support/tenants/{tenantId}/members`, `.../jobs` | `support-operator` + active own grant | read-only views |

`AuditRecord`: id, occurredAt, actorKind (`member` | `platform_admin` | `support_operator` | `service`), actorRef
(membership id, or an operator reference: Q9), action (`area.verb`, a closed list), targetType, targetId, traceId.
`SupportGrant`: id, tenantId, ticket, startsAt, expiresAt, endedAt. Member view: membership id, role, status, created
(no issuer or subject). Job view: import and export ids, status, counts, failure code. Event: `audit.recorded.v1`.

## Data
New schema `audit`: `audit_records` (id = event id, tenant_id null for platform events, occurred_at, actor_kind,
actor_ref, action, target_type, target_id, trace_id; index (tenant_id, occurred_at desc, id); forced RLS, where
tenant-less rows are visible only on the platform path; INSERT and SELECT for the app role; a trigger refusing UPDATE),
`processed_messages`, and `purge_expired()` (SECURITY DEFINER, fixed cutoff). (S2 amendment: S2 creates `tenant_id`
NOT NULL, because every S2 event has a tenant and the outbox envelope requires one; a tenant target must equal the
record's tenant. S3 amendment: no action in criterion 2 is platform-only (support grants in S7 name their tenant too), so
`tenant_id` stays NOT NULL with no platform-path policy, and the platform read of criterion 5 requires `tenantId` and reads
that one tenant under its own row-level security scope: 400 `audit.tenant_required` without it, 404 for an unknown tenant.
S4 amendment: instead of one cron task per module, each module declares its rules (`…Retention` classes) and the Worker
runs them all in one daily `retention` task, tenant by tenant; a Worker test fails if a module's rules are left out.
S6 amendment: Trivy's config scan does not read Compose files or GitHub workflows, so the configuration checks are in
the scan script (privileged, host namespaces, Docker socket, added capabilities, digest pins, loopback ports; actions
pinned by commit, no write permissions, no persisted checkout credentials). Trivy gets the Docker host's CA bundle and
uses ghcr.io for its databases, so a TLS-inspecting proxy the host trusts works. A secret exception names `rule:path`
or one fingerprint.
S7 amendment: support grants are platform-owned (not under row-level security, like `tenants.tenants`); the views take
the tenant id from the route after the grant check and read through `ISupportMemberView` (IdentityAccess) and
`ISupportJobView` (the new `ImportsExports.Contracts`); ending an already ended or expired grant is 404
`support.grant_not_found`; grant expiry is not audited separately (the expiry time is in the opening event).
Removed memberships age from `updated_at` (removal is their last change), and the M2 audit tables and `audit_records`
are purged by `SECURITY DEFINER` functions with a fixed 400-day cutoff, one tenant and 500 rows per call.) New schema `operations`: `support_grants`
(id, tenant_id, operator_ref, ticket, starts_at, expires_at, ended_at, ended_by; at most one open grant per (operator,
tenant)), `outbox_messages`. Outbox tables are added to Tenants, IdentityAccess, ImportsExports and Subscriptions where
missing. Every change is additive. The existing `membership_audit` and `tenant_audit` tables follow Q3.

## Failure modes
- Broker down: audit intents wait in each module's outbox (ADR-0021). A poison audit event dead-letters with an alert path.
- The Audit consumer is redelivered: deduplicated on the event id (unique key plus processed messages).
- Retention fails mid-run: each batch commits alone, the next run continues, and nothing is half-deleted.
- A grant expires during a request: the check reads the grant and writes the access record in one transaction, before
  the read. The read that follows completes.
- A scanner registry or feed is unavailable: the scan fails. It never passes silently; a retry is manual.

## Out of scope
Tenant offboarding and deleting a whole tenant (Q6); on-request erasure through an API (Q6); tenant consent before
support access (Q8); support write actions and impersonation; an operator registry (Q9, unless chosen); rate limiting
(Q12); a hash-chained or WORM audit store (Q4); building our own container images and SBOMs (M12); CodeQL (Q10).

## Decisions (approved by the user, 2026-10-22)
The recommendation was taken for every question below:
1. CI stays on hold. "Pipeline checks pass" means the local scan script passes with fresh evidence and the `security`
   job is defined but has not run.
2. Audit intents go through each module's outbox and the broker to an Audit consumer (ADR-0026).
3. `membership_audit` and `tenant_audit` keep being written. Nothing is backfilled, and they are retired later.
4. Tamper evidence comes from privileges and an update trigger. A hash chain is a revisit trigger.
5. Retention follows the ADR-0025 table.
6. Time-based pseudonymisation only. On-request erasure and tenant offboarding are later work.
7. L10: the job ends (`tenant_suspended`, `requester_not_authorized`). `IMembershipDirectory` gains a permission query.
8. Support: a grant with a ticket reference, at most 240 minutes, read-only `members` and `jobs` views, every access
   audited and visible to the tenant, no tenant consent.
9. No operator registry in M9. Audit stores a keyed operator reference.
10. gitleaks and Trivy as pinned images. Packages fail at moderate or above, images at fixed HIGH or CRITICAL, and
    exceptions expire. CodeQL waits for CI.
11. Threat-model actions live in a Markdown register checked by `progress.ps1`.
12. Rate limiting goes to M11, tracked as a threat-model action.
13. One spec.

S1 follow-ups (approved by the user, 2026-10-22, from the threat models):
14. The Worker's Entity Framework logging moves to `Warning` outside Development, like the API's (criterion 19, S5).
15. The M2 tables `membership_audit` and `tenant_audit` lose rows older than the audit retention through their own purge
    functions (criterion 10, S4).
16. Per-service broker users and tenant-scoped processed-message ids (M5 finding 6) move to M12 with the production
    broker. Unsigned broker messages are accepted until then. Log redaction moves to M10.

## Open questions (resolved; kept for the record)
1. (B) **CI in M9.** Switch CI on (needs a remote and a push by you), or keep it on hold and read "pipeline checks
   pass" as the local scan script passing plus a defined but unrun `security` job.
2. (B) **Audit write path** (ADR-0026): outbox and broker into an Audit consumer; an in-process outbox relay;
   enlisting in the caller's transaction; or per-module tables with a merged read.
3. (B) **Existing `membership_audit` and `tenant_audit`**: keep writing them and backfill nothing; backfill into Audit;
   or stop writing them (a contract step later).
4. **Tamper evidence**: privileges plus a trigger only, or a per-tenant hash chain too.
5. (B) **Retention periods** (ADR-0025 table): processed messages 30 d, webhook events 90 d, finished idempotency keys
   30 d, completed timers 30 d, dead timers 90 d, finished job rows with errors and batches 90 d, removed-member
   subjects 30 d, audit 400 d; orders, payments, refunds and subscriptions kept for the life of the tenant.
6. **Erasure**: time-based pseudonymisation only (recommended), or an on-request erasure operation too. Tenant
   offboarding moves to a later milestone.
7. (B) **L10**: end the job (recommended) or pause it like renewals. The permission re-check needs a new
   `IMembershipDirectory` query.
8. (B) **Support model** (ADR-0027): reason, time limit and audit with no tenant consent, or tenant-admin approval;
   which read-only views; ticket format; maximum duration.
9. **Operator registry and operator reference**: keep IdP-claim authority and store an operator reference (recommended),
   or add a TenantFlow-side operator registry now.
10. (B) **Scanning tools and thresholds** (ADR-0028): gitleaks and Trivy as pinned images; fail on moderate and above
    for packages and on fixed HIGH/CRITICAL for images; CodeQL only once CI runs.
11. **Tracking threat-model actions**: a Markdown register checked by `progress.ps1 -Verify` (recommended), or entries
    in `progress.json`.
12. **Rate limiting** (a gap in threat model 0001): M11 with the workload model (recommended), or a minimal limiter
    in M9.
13. **One spec or two**: this spec has 18 criteria and 7 slices. Keep it whole, or split scanning and threat models
    into their own spec.

## Slices
| Slice | Delivers | Criteria |
|-------|----------|----------|
| S1 | Threat models, action register, tracker check | 1 |
| S2 | Audit module: schema, append-only guards, consumer, events from Tenants and IdentityAccess (riskiest: the write path) | 2, 3, 6 |
| S3 | More audited actions (imports, exports, downloads, refunds, cancellations), tenant and platform audit reads | 2, 4, 5 |
| S4 | Retention: privacy document, table check, per-module purge tasks, pseudonymisation, audit retention | 7, 8, 9, 10 |
| S5 | Background-job re-checks (L10), storage hardening (L14), Worker EF log level | 11, 12, 19 |
| S6 | Security scanning script and CI job | 13, 14 |
| S7 | Role split, Operations module, support grants and views, platform route matrix | 15, 16, 17, 18 |

## Verification
Filled in during implementation.

| Criterion | Test | Proven to fail without the change |
|-----------|------|------------------------------------|
| 1 | `progress.ps1` threat-model action check over 8 models (184 actions: 83 done, 67 planned, 34 accepted), every done reference resolved to an existing test | Yes: a missing test method (P1), a planned action due in a closed milestone (P2), an invalid owner (P3) each made the check fail with exit 1 |
| 2 (S2: memberships and tenant lifecycle) | `AuditLogTests.Each_platform_and_member_change_writes_one_audit_event_naming_its_actor_by_reference_only`, `AuditLogTests.A_refused_change_writes_no_audit_event`, `AuditLogTests.A_message_delivered_twice_and_concurrently_makes_one_record_with_the_message_id_and_trace`, `AuditLogTests.Each_message_is_recorded_in_its_own_tenant_and_another_tenant_never_sees_it`, `AuditLogTests.An_event_that_can_never_be_recorded_is_refused_for_the_dead_letter_queue_and_records_nothing` (9 shapes, including another tenant as target, a missing and a non-UTC time), `AuditLogTests.When_the_change_fails_at_commit_its_audit_event_rolls_back_with_it`, `ConfigurationValidationTests.Host_refuses_to_start_without_a_usable_operator_reference_key_and_names_only_the_key` | Yes: no forced RLS on the audit tables (S2-P1, cross-tenant test fails); no deduplication on the event id (S2-P2, duplicate test fails); the event committed before the last-administrator refusal (S2-P3, refused-change test fails); the event saved apart from its change (S2-P4, commit-failure test fails); a tenant target of another tenant accepted (S2-P5, that shape fails; the check constraint still refuses the write). Other field validation and the key check: deferred to gate |
| 2 (S3: imports, exports, downloads, refunds, paid cancellations) | `ImportJobTests.An_import_creates_and_updates_products_by_sku_and_reports_bad_rows_without_their_values`, `ExportJobTests.An_export_writes_every_product_with_formulas_neutralized_and_its_file_imports_back_unchanged`, `RefundEndpointTests.An_order_manager_refunds_part_of_a_paid_order_with_an_audited_reason_and_a_customer_cannot`, `OrderRefundSagaTests.Cancelling_a_paid_order_refunds_what_is_left_once_and_its_event_refunds_the_order` (one outbox event each, naming the requesting member) | Deferred to gate (same outbox write as S2-P3 and S2-P4) |
| 3 | `AuditLogTests.The_application_role_can_only_add_and_read_audit_records_and_no_role_can_change_one` | Deferred to gate (privileges and the update trigger) |
| 4 | `AuditReadTests.An_administrator_reads_their_tenants_log_newest_first_page_by_page_and_never_another_tenants`, `AuditReadTests.A_bad_page_request_is_refused_with_its_code` (4 shapes), `IsolationMatrixTests` (32 routes: customer 403, other tenant, removed member and non-member 404), `RouteTableTests` | Yes: no tenant predicate and no forced RLS on reads (S3-P1, both read tests return other tenants' records) |
| 5 | `AuditReadTests.A_platform_operator_reads_one_named_tenants_log_and_a_tenant_token_is_not_accepted` (support operator 403 comes with the role split in S7) | Yes: S3-P1 |
| 7 | `PrivacyDocumentTests.Every_table_in_every_module_schema_has_a_class_a_purpose_and_a_retention_rule` (35 tables, four classes; names every missing or unknown table) | Deferred to gate |
| 8 | `RetentionTests.Expired_rows_go_a_row_one_second_inside_its_window_stays_and_another_tenant_waits_for_its_own_run` (every rule kind, one second either side of its window, another tenant untouched until its own run), `RetentionTests.More_expired_rows_than_one_batch_are_all_deleted_in_bounded_batches`, `RetentionTests.Every_tenant_is_listed_once_page_by_page_suspended_ones_included`, `RetentionTaskTests.The_retention_run_applies_every_rule_of_every_module_once`, `WorkerHostTests.The_scheduler_runs_the_metrics_task_the_storage_sweep_and_retention_on_their_schedules` | Yes: no tenant filter in a retention delete (S4-P1: tenant B's expired timer, a table outside RLS, goes in tenant A's run) |
| 9 | `RetentionTests.A_membership_removed_thirty_days_ago_loses_its_identity_keeps_its_row_and_the_person_can_return` | Deferred to gate |
| 10 | `RetentionTests.Audit_rows_older_than_four_hundred_days_leave_through_their_purge_functions_and_younger_ones_stay` (audit, tenant and membership audit tables), `AuditLogTests.The_application_role_can_only_add_and_read_audit_records_and_no_role_can_change_one` | Deferred to gate |
| 11 | `ImportJobTests.An_import_stops_before_its_next_batch_once_its_requester_is_demoted_and_keeps_the_batches_applied`, `ExportJobTests.An_export_ends_unwritten_when_its_requester_is_demoted_or_removed_or_its_tenant_suspended`, `ImportJobTests.A_queued_import_ends_unapplied_when_its_requester_is_removed` | Yes: no requester check (S5-P1, all three job tests fail: the jobs succeed) |
| 12 | `StorageTests.The_sweep_deletes_only_temporary_files_left_for_more_than_an_hour`, `StorageTests.On_unix_stored_files_are_readable_by_their_owner_only` (file modes: Linux only, skipped on the Windows workstation, so not yet run; CI is on hold), `WorkerHostTests.The_scheduler_runs_the_metrics_task_the_storage_sweep_and_retention_on_their_schedules` | Deferred to gate (sweep); flush before rename has no test (a crash between the two cannot be staged in a test) |
| 19 | `WorkerHostTests.Outside_development_entity_framework_core_logs_warnings_and_above_only`, `ConfigurationFilesTests.Outside_development_entity_framework_core_logs_warnings_and_above_only` | Deferred to gate |
| 13 | `./scripts/security-scan.ps1` run 2026-10-22: secrets pass (3 reviewed exceptions: the local audit key and two Stripe-shaped placeholder strings in tests), dependencies pass, configuration pass; images FAIL (all 8 third-party Compose images have fixed HIGH or CRITICAL CVEs; decision pending with the user). `SecurityScanTests.Every_scanner_image_is_pinned_by_digest`, `SecurityScanTests.Every_exception_names_its_tool_id_reason_and_owner_and_has_not_expired` | Yes: a privileged Compose service fails the configuration scan (S6-P1). Planted secret, vulnerable package and expired exception: deferred to gate |
| 14 | `SecurityScanTests.The_ci_security_job_runs_the_scan_read_only_with_actions_pinned_by_commit` (the job is defined; CI is on hold, decision 1) | Deferred to gate |
| 15 | `SupportAccessTests.Every_platform_route_answers_each_kind_of_caller_as_its_role_says` (10 platform routes x 5 callers), `RouteTableTests` | Deferred to gate |
| 16 | `SupportAccessTests.A_support_operator_opens_one_audited_grant_with_a_ticket_and_a_bounded_duration` | Deferred to gate |
| 17 | `SupportAccessTests.The_views_need_the_callers_own_active_grant_for_that_tenant_and_record_each_access_before_reading` | Yes: an access check that ignores the grant's operator lets a colleague read the tenant (S7-P1, views test fails) |
| 18 | `SupportAccessTests.An_ended_revoked_or_expired_grant_answers_404_from_the_next_access_and_each_end_is_audited` (ended, revoked by a platform administrator, expired under a fake clock) | Deferred to gate |
| 6 | `AuditLogTests.Every_audit_event_made_while_the_broker_was_down_is_recorded_once_after_it_recovers` | Deferred to gate (outbox path; atomicity is S2-P3 and S2-P4) |

Open S2 review findings (the user chose to fix the four medium ones, 2026-10-22; these stay open for the M9 review):
- S2-L1 Domain projects of Tenants and IdentityAccess reference `Audit.Contracts`; engineering conventions section 12
  allows only the module's own Contracts, the architecture test allows any (precedent: Subscriptions.Domain to
  Payments.Contracts). Decide: amend the conventions or move the event factories to Application.
- S2-L2 A malformed trace id makes the whole event invalid instead of dropping the trace id.
- S2-L3 A redelivery whose processed-message row is gone hits the record's key and is dead-lettered, not counted as a
  duplicate (matters once S4 purges processed messages).
- S2-L4 The `service` actor kind accepts any short lowercase text as its reference.
- S2-L5 Default privileges in the `audit` schema would grant UPDATE and DELETE on a future table.
- S2-L6 The published local operator-reference key would be accepted outside Development.
- S2-L7 Any module with broker access can publish `audit.recorded.v1` (broker trusted, ADR-0021); no threat-model row yet.
- S2-L8 Two assertions in the broker-down test cannot fail; S2-L9 no domain-separation prefix in the operator HMAC.
