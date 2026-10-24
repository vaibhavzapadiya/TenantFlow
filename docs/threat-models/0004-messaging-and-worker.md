# Threat model: messaging and the Worker

- Status: Draft (M9), written with SPEC-0009 S1
- Date: 2026-10-22
- Owner: TenantFlow tech lead (the user)
- Scope: the per-module transactional outboxes (`orders`, `payments`), the Worker's outbox publisher, RabbitMQ (exchange
  `tenantflow.events`, consumer queues, TTL retry queues, dead-letter queues, credentials), the idempotent consumers
  (`notifications.order-events`, `orders.payment-events`), workflow timer due rows (`workflow_timers` in Orders,
  Payments, Subscriptions, ImportsExports), the Quartz.NET clustered scheduler, `ServiceTenantContext`, and the Worker's
  health endpoints. The audit event path of SPEC-0009 is in scope as planned work.
- Related: blueprint sections 6, 11, 14, 16; ADR-0010, ADR-0021, ADR-0023, ADR-0025, ADR-0026;
  [SPEC-0005](../specs/0005-messaging-foundation.md); [SPEC-0009](../specs/0009-security-and-privacy.md) (AC 2, 3, 6, 7,
  8, 11, 13); [M5 guide](../milestones/M5-messaging-foundation.md) (residual risks, review finding 6);
  [dead-letter runbook](../operations/dead-letter-replay.md); [threat model 0001](0001-tenant-boundary.md) (TB-15)

## Assets and data classification

| Asset | Classification | Why it matters |
|---|---|---|
| Outbox rows (`orders.outbox_messages`, `payments.outbox_messages`) | Tenant-confidential, integrity-critical | Every tenant's pending events in one table outside RLS; the tenant id on a row decides where its effect lands |
| Messages in transit and in queues (envelope + JSON payload) | Tenant-confidential | Order, payment and refund ids, statuses, amounts and the owner's membership id |
| `processed_messages` (Notifications, Orders) | Internal, integrity-critical | The deduplication key; losing it repeats business effects |
| `workflow_timers` due rows | Internal, integrity-critical | Tenant id, kind, subject id, due time; outside RLS and read across tenants |
| Quartz job store (`scheduler` schema) | Internal | Triggers for the per-module timer jobs and the recurring tasks |
| Broker credentials (one user, from `deploy/compose/.env`) | Secret | Publish, consume, declare and purge on every queue |
| Database app role credentials (shared with the API) | Secret | Reads and writes every module schema, under RLS where forced |
| Dead-letter queues | Tenant-confidential | Hold failed messages with their full payload until a human decides |

Personal data: no names, emails or free text in payloads (SPEC-0005 AC 3). Membership ids (`ownerMembershipId`) are
`personal-pseudonymous` (ADR-0025) and appear in Orders event payloads, notifications and dead letters.

## Actors and trust boundaries

Actors: API request (writes outbox rows and timers inside its tenant transaction); Worker (publisher, consumers, timer
runner, scheduler); anyone holding the broker credentials (developer, operator, or an attacker who obtained them);
operator using the management UI for dead-letter replay; developer with database access.

| Boundary | Crossed by | Control |
|---|---|---|
| M1 API transaction → outbox / timer row | state change | row written in the same transaction as the change, tenant id from the request's tenant context |
| M2 Outbox → broker | publisher (Worker) | lease claim, publish with confirms, mark after confirm; no transaction open during the publish |
| M3 Broker → consumer | every delivery | envelope validation, known tenant, fresh DI scope and `ServiceTenantContext` per message, forced RLS |
| M4 Due row → handler | timer runner via Quartz | lease claim, fresh scope entered for the row's tenant, stores filter by tenant again under RLS |
| M5 Human → broker | management UI on `127.0.0.1:15672` | the same broker user as the Worker; runbook rules only |
| M6 Worker → PostgreSQL | all Worker work | app role (no owner, no `BYPASSRLS`); no service token (ADR-0021 decision 8) |

## Entry points and data flows

1. Order or payment state change → outbox row (same transaction) → publisher claims ≤ 50 rows with a 30 s lease →
   publish to `tenantflow.events` (routing key = type, `message_id` = row id) → confirm → mark published → delete after 7 days.
2. Delivery on a consumer queue → `MessageEnvelope.Read` (message id, type, tenant, version, UTF-8 payload) → new scope →
   `ServiceTenantContext.Enter(tenant)` → consumer checks type, version, payload and tenant existence → effect plus
   `processed_messages (consumer, message_id)` in one tenant-scoped transaction → ack.
3. Failure → copy republished to `<queue>.retry.n` (1 s, 5 s, 30 s, 2 min, 10 min) → back to the queue; after 5 retries,
   or at once for an invalid message, to `<queue>.dlq` with a reason header.
4. Quartz (clustered AdoJobStore, `DisallowConcurrentExecution`, every minute) → per-module timer job → claim ≤ 10 due rows
   with a 2 min lease → one scope per row entered for its tenant → handler → complete, reschedule or count a failure
   (dead after 20).
5. Manual dead-letter inspection and replay through the management UI (runbook).
6. Worker HTTP: liveness and readiness only; every other path is 404.

## Abuse cases / attack paths

| Id | Attack | Attacker goal | Path |
|---|---|---|---|
| MW-01 | Message forgery | Drive a saga without the real event, e.g. mark a pending order paid | Publish `payment.succeeded.v1` with a real tenant and order id using the broker credentials; messages are not signed |
| MW-02 | Replay and duplicates | Repeat an effect (stock release, refund state, notification) | Redeliver, republish after a crash, or replay a dead letter |
| MW-03 | Poison message or failing timer | Block a queue or a module's timers | A message or due row that always throws |
| MW-04 | Overprivileged broker user | Read every tenant's payloads, purge queues, rebind topology | One RabbitMQ default user (administrator tag) shared by the Worker and the management UI |
| MW-05 | Tenant inheritance between messages or jobs (TB-15) | Process tenant B's message in tenant A's context | Scoped state or a pooled connection reused across deliveries |
| MW-06 | Cross-tenant due row or outbox row | Act on tenant A's subject through a row that names tenant B | Row outside RLS with a wrong or tampered tenant id |
| MW-07 | Unscoped table misuse | Read every tenant's outbox or timers from module code | New code querying `outbox_messages` or `workflow_timers` directly |
| MW-08 | Queue flood / noisy tenant | Starve other tenants' events and timers | One tenant generating many events or due rows; no per-tenant fairness |
| MW-09 | Dead-letter replay misuse | Replay into another consumer, edit the tenant header, or purge evidence | Management UI with the shared broker user |
| MW-10 | Personal data in messages and logs | Harvest identities from payloads, reasons or logs | Payload fields, exception text in reasons, logged bodies |
| MW-11 | Unbounded retention | Keep membership ids and dedup rows forever | `processed_messages`, completed and dead timers never deleted |
| MW-12 | Eavesdropping or tampering on the wire | Read or alter messages | Plain AMQP and HTTP management UI (no TLS) |
| MW-13 | Worker acting for a removed requester or suspended tenant | Keep using rights that were withdrawn | Job, renewal or saga continues after removal or suspension |
| MW-14 | Missing Worker service identity | Worker authority that is broader than its jobs | Shared app DB role and broker user; no client-credentials token |
| MW-15 | Scheduler job store tampering | Stop or duplicate recurring work | Stale or edited Quartz triggers in the shared job store |
| MW-16 | Audit event lost or doubled | Hide or inflate an audited action | Broker outage, redelivery, or a rolled-back action that still emits |

## Existing controls

- Outbox rows are written only inside the state change's transaction, and a lost conditional update writes none
  (`OrderOutboxTests`). Only `OrderStore`, `PaymentStore` and migrations name `outbox_messages` or `workflow_timers`
  (`OutboxAccessTests`).
- The publisher claims with leases and marks a row only after a broker confirm; a crash before the mark republishes the
  same message id; a refused row backs off without blocking newer ones (`OutboxPublisherTests`).
- Consumers validate the envelope and dead-letter invalid messages at once, with fixed reasons that never quote payload
  text; transient failures get five retries (`DeadLetterTests`, `RabbitMqConsumer.cs`).
- Deduplication on `(consumer, message_id)` in the effect's transaction, proven with concurrent deliveries
  (`NotificationConsumerTests`, `OrderPaymentSagaTests`). Messages for an unknown tenant or order are dead-lettered.
- A fresh DI scope per message and per timer; `ServiceTenantContext` can be entered once, holds no membership and no
  permission, and fails closed when unentered (`ServiceTenantContextTests`). `PaymentEventConsumer` also checks that the
  context matches the envelope's tenant.
- Timer handlers re-filter by tenant under RLS: a timer naming the wrong tenant finds nothing
  (`PaymentReconciliationTests`, `JobRecoveryTests`). Dead after 20 failures, with backoff up to an hour (`WorkflowTimerTests`).
- Quartz uses a clustered persistent store; tasks removed from code are removed from the store (`SchedulerTests`).
- Broker ports bind to `127.0.0.1`; the image is pinned by digest; the Worker refuses to start without a broker password
  and names only the key (`deploy/compose/compose.yaml`, `WorkerHostTests`). Only the Worker connects to the broker.
- Renewals wait while the tenant is suspended and end for a removed member (`SubscriptionRenewalTests`).

## Gaps and residual risk

| Gap | Residual risk | Until |
|---|---|---|
| One broker user (default administrator) for the Worker and humans; no message signing | Anyone with it can forge events for existing tenants and orders, read payloads, or purge queues | M12 |
| `processed_messages` key is not tenant-scoped (M5 finding 6) | A forged message reusing another tenant's message id is dropped as a duplicate | M12 |
| Outbox and timer tables outside RLS | A future query bug could read across tenants; only the architecture test guards it | M12 review |
| No TLS on AMQP or the management UI | Readable on the host network; acceptable only on loopback | M12 |
| No per-tenant fairness or queue limits | One tenant's volume delays others | M11 |
| No alerting on dead letters or dead timers | Failures are visible only in metrics and logs | M10 |
| `processed_messages` and timers kept forever | Membership-linked rows outlive their purpose | M9 (SPEC-0009 AC 8) |
| Imports and exports do not re-check the requester | A removed manager's job finishes | M9 (SPEC-0009 AC 11) |
| No Worker service identity | Worker authority equals the app role and broker user | M12 |

## Mitigations with owner and due date

| Id | Abuse case | Mitigation | Owner | Due | Status | Evidence |
|---|---|---|---|---|---|---|
| MW-A01 | MW-01 | Envelope validation; unknown tenant, order, type or version is dead-lettered and writes nothing | Claude | M5 | done | `DeadLetterTests.Invalid_messages_go_straight_to_the_dead_letter_queue_with_the_reason_and_write_nothing`, `OrderPaymentSagaTests.A_message_acts_in_its_own_tenant_only_and_an_unknown_tenant_or_order_is_dead_lettered` |
| MW-A02 | MW-01, MW-04, MW-09 | Least-privilege broker users: an app user without the administrator tag, limited to TenantFlow resources; a separate operator account for the management UI; no default user | user | M12 | planned | M12 production broker and secrets (moved from M9 by the owner, SPEC-0009 decision 16) |
| MW-A03 | MW-01 | No message signing; a forger needs the broker credentials, which only the Worker holds and which bind to loopback locally | user | M12 | accepted | Broker credentials are the trust anchor; signing is reconsidered with the M12 broker design |
| MW-A04 | MW-02 | Dedup on `(consumer, message_id)` in the effect's transaction; republication keeps the message id | Claude | M5 | done | `NotificationConsumerTests.A_message_delivered_twice_through_the_broker_records_one_notification`, `NotificationConsumerTests.Two_concurrent_deliveries_of_one_message_record_one_notification`, `OrderPaymentSagaTests.A_duplicate_has_one_effect_and_a_redelivery_after_a_crash_before_the_release_still_releases`, `OutboxPublisherTests.A_message_published_but_never_marked_is_published_again_with_the_same_id_once_its_lease_passes` |
| MW-A05 | MW-02 | Message ids are not tenant-scoped in `processed_messages` (M5 finding 6) | user | M12 | accepted | A collision needs broker access and a known message id, and drops a message rather than writing across tenants; moved from M9 by the owner (SPEC-0009 decision 16) |
| MW-A06 | MW-03 | Invalid messages dead-lettered at once, transient ones after five retries; refused outbox rows back off; timers dead after 20 failures | Claude | M7 | done | `DeadLetterTests.A_message_that_keeps_failing_is_dead_lettered_after_five_retries`, `OutboxPublisherTests.A_message_the_broker_keeps_refusing_is_deferred_with_growing_backoff_and_never_holds_back_newer_ones`, `WorkflowTimerTests.A_timer_whose_handler_fails_twenty_times_in_a_row_is_dead_and_the_others_keep_running` |
| MW-A07 | MW-03, MW-09 | Alerts on `dead_lettered` outcomes, dead timers and outbox age | Claude | M10 | done | DeadLettersGrowing, DeadTimer and OutboxStale page alerts, tested in `deploy/compose/config/prometheus/rules/alerts.rules.test.yml`; `ObservabilityConfigTests.Every_alert_has_a_severity_a_category_and_a_runbook_and_quotes_no_value`, `WorkflowTimerTests.A_timer_whose_handler_fails_twenty_times_in_a_row_is_dead_and_the_others_keep_running` |
| MW-A08 | MW-05 | Fresh scope per message and timer; single-entry `ServiceTenantContext` with no membership or permission; pooled connections reset the tenant | Claude | M6 | done | `ServiceTenantContextTests.Unentered_it_fails_closed_and_entered_it_grants_no_permission_or_membership`, `NotificationConsumerTests.Each_message_is_recorded_in_its_own_tenant_only_never_in_the_previous_ones`, `RowLevelSecurityTests.A_pooled_connection_does_not_keep_the_previous_tenant` |
| MW-A09 | MW-06 | Handlers re-filter by tenant under RLS; a row naming another tenant acts on nothing | Claude | M8 | done | `PaymentReconciliationTests.A_timer_runs_in_its_own_tenant_only`, `JobRecoveryTests.A_due_row_of_another_tenant_never_acts_on_this_tenants_job_or_file`, `WorkflowTimerTests.Scheduling_a_kind_and_subject_again_moves_the_timer_only_within_its_own_tenant` |
| MW-A10 | MW-07 | Only the owning stores and migrations may name `outbox_messages` and `workflow_timers` | Claude | M6 | done | `OutboxAccessTests.Only_the_owning_store_and_migrations_name_the_outbox_table` |
| MW-A11 | MW-06, MW-07 | Outbox and timer tables stay outside RLS (ADR-0021 decision 7, ADR-0023 decision 6) | user | M12 | accepted | Rows hold ids, tenant and timing only; access is limited by the architecture test; reviewed with production roles |
| MW-A12 | MW-08 | Workload model, per-tenant fairness or rate limits for events and due rows, queue length limits | Claude | M11 | planned | M11 workload model |
| MW-A13 | MW-09 | Manual replay only, into the message's own queue, after reading the reason; replay of processed ids is a no-op | Claude | M5 | done | `DeadLetterTests.A_transient_failure_is_retried_and_the_retry_that_succeeds_records_the_notification_once`, `NotificationConsumerTests.A_message_delivered_twice_through_the_broker_records_one_notification`, `docs/operations/dead-letter-replay.md` |
| MW-A14 | MW-09 | Replays and purges are recorded only in incident notes, not in the audit log | user | M10 | accepted | Needs broker access already covered by MW-A02. Reviewed in M10-S6 (2026-10-22): the queue runbook requires recording replayed and purged message ids in the incident notes; stays accepted (recommendation) |
| MW-A15 | MW-10 | Payloads carry ids, statuses and amounts only; dead-letter reasons never quote payload text | Claude | M5 | done | `OrderOutboxTests.Reserving_and_cancelling_write_one_event_each_in_the_orders_tenant_and_a_rejection_writes_none`, `DeadLetterTests.Invalid_messages_go_straight_to_the_dead_letter_queue_with_the_reason_and_write_nothing` |
| MW-A16 | MW-10, MW-11 | Classify outbox, processed-message, notification and timer tables (membership ids as personal-pseudonymous) | Claude | M9 | done | `PrivacyDocumentTests.Every_table_in_every_module_schema_has_a_class_a_purpose_and_a_retention_rule` |
| MW-A17 | MW-11 | Published outbox rows deleted after 7 days; pending rows never | Claude | M5 | done | `OutboxPublisherTests.Cleanup_deletes_only_rows_published_longer_ago_than_the_retention` |
| MW-A18 | MW-11 | Processed messages after 30 days, completed and dead timers after 30 and 90 days, deleted in bounded batches | Claude | M9 | done | `RetentionTests.Expired_rows_go_a_row_one_second_inside_its_window_stays_and_another_tenant_waits_for_its_own_run` |
| MW-A19 | MW-12 | TLS for AMQP and the management UI | Claude | M12 | planned | M12 production broker and secrets |
| MW-A20 | MW-13 | Renewals wait for a suspended tenant and end for a removed member | Claude | M7 | done | `SubscriptionRenewalTests.A_removed_members_subscription_ends_at_its_next_renewal_without_a_charge`, `SubscriptionRenewalTests.Renewals_of_a_suspended_tenant_wait_and_charge_nothing` |
| MW-A21 | MW-13 | Imports and exports end with `tenant_suspended` or `requester_not_authorized` before further writes | Claude | M9 | done | `ImportJobTests.An_import_stops_before_its_next_batch_once_its_requester_is_demoted_and_keeps_the_batches_applied`, `ExportJobTests.An_export_ends_unwritten_when_its_requester_is_demoted_or_removed_or_its_tenant_suspended` |
| MW-A22 | MW-13 | In-flight sagas (payment and refund events, cancellation refunds, notifications) complete after suspension or removal | user | M13 | accepted | Finishing returns money and stock; reviewed with tenant offboarding |
| MW-A23 | MW-14 | No client-credentials identity; the Worker calls no HTTP API (ADR-0021 decision 8) | user | M12 | accepted | A token would authorize nothing today; reviewed with the M12 production IdP |
| MW-A24 | MW-15 | Clustered persistent job store; tasks no longer in code are removed at start | Claude | M8 | done | `SchedulerTests.Quartz_runs_each_modules_due_rows_from_a_clustered_persistent_job_store`, `SchedulerTests.A_task_no_longer_in_the_code_is_removed_from_the_shared_job_store_when_a_worker_starts` |
| MW-A25 | MW-16 | Audit intents through each module's outbox to an idempotent Audit consumer; survives a broker outage | Claude | M9 | done | `AuditLogTests.Every_audit_event_made_while_the_broker_was_down_is_recorded_once_after_it_recovers`, `AuditLogTests.A_message_delivered_twice_and_concurrently_makes_one_record_with_the_message_id_and_trace`, `AuditLogTests.A_refused_change_writes_no_audit_event` |
| MW-A26 | MW-04 | Image and dependency scan covers the RabbitMQ image and the client package | Claude | M9 | done | `SecurityScanTests.Every_scanner_image_is_pinned_by_digest`, `SecurityScanTests.Every_exception_names_its_tool_id_reason_and_owner_and_has_not_expired` |

## Security tests and monitoring

- Tests: the classes cited above, each guarantee proven to fail with its guard removed (SPEC-0005, SPEC-0006,
  SPEC-0007 verification tables). New consumers must add a duplicate, a cross-tenant and an invalid-message test.
- Monitoring today: `tenantflow.outbox.pending` and `tenantflow.outbox.oldest_pending_age` per module;
  `tenantflow.messaging.consumed{consumer, outcome}`; log events 5101 and 5104 (dead letters) and 5206 (dead timer).
  Labels never carry a tenant id. Alerts arrive in M10 (MW-A07).

## Review date and change triggers

Review at the M9 gate, then at M10 (alerts), M11 (workload and fairness) and M12 (broker users, TLS, service identity).
Also review when a new consumer, event type or publishing module is added, when the Worker starts calling an HTTP API,
when a second consumer host or broker is introduced, or when a table outside RLS is proposed.
