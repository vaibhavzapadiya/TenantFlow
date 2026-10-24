# SPEC-0005: Messaging foundation

- Status: Implemented (approved 2026-10-21 by the user)
- Milestone: M5
- Owner: TenantFlow tech lead (user)
- Related: blueprint sections 6 (Notifications), 11 (outbox, consumer correctness, broker), 14 (failure), 16 (observability); ADR-0002, ADR-0008, ADR-0010, ADR-0011, ADR-0012, ADR-0019, ADR-0020; ADR-0021 (Accepted); SPEC-0004

## Outcome
When an order is reserved or cancelled, the rest of the system learns about it reliably, without the Orders request
waiting on a broker. A new Notifications module records a notification for the order's owner exactly once per event,
in the event's tenant, even though messages may be published and delivered more than once. Operators can see how
many messages wait to be published and how old the oldest one is, and poison messages land in a dead-letter queue
instead of blocking the queue. No message is actually delivered to a person in M5.

## Acceptance criteria
**Outbox (Orders)**
1. Given a `PendingReservation` order whose stock is reserved, when it moves to `PendingPayment`, then the same
   transaction writes one `orders.outbox_messages` row of type `order.reserved.v1` (version 1) with the order's tenant id.
   When an order moves to `Cancelled` (from `Draft` or `PendingPayment`), one `order.cancelled.v1` row is written.
2. Given a transition that loses its conditional update (a racing request already moved the order), then no outbox row
   is written; given the transaction rolls back after the update, then neither the state change nor the row persists.
3. Given any outbox row, then its payload holds identifiers, status and amounts only (no name, email or free text),
   and its envelope has message id, type, version, tenant id, occurred-at (UTC), correlation id and W3C `traceparent`.

**Publisher (Worker)**
4. Given 120 pending rows, when the publisher runs, then it claims at most 50 per batch (oldest first), publishes each
   to the `tenantflow.events` exchange with routing key = type and the message id as `message_id`, waits for the
   broker's confirm, and only then marks the row published. No database transaction is open while it publishes.
5. Given two publishers running at once, then a row is claimed by one of them only, until its lease expires.
6. Given a row that was published but not marked (crash before the mark), when its lease expires, then it is published
   again with the same message id (duplicate publication is expected and safe; see 9).
7. Given the broker is down or does not confirm, then the row stays pending, is retried on a later poll, and the
   Worker's readiness check reports the broker unhealthy. Given published rows older than 7 days, then they are deleted
   in bounded batches; pending rows are never deleted.
8. Given pending rows, then the metrics `tenantflow.outbox.pending` (count) and `tenantflow.outbox.oldest_pending_age`
   (seconds) report them per module, and drop to 0 once all are published (asserted with `MeterCapture`).

**Consumer (Notifications)**
9. Given an `order.reserved.v1` message, when Notifications consumes it, then one notification row (kind, order id,
   recipient membership id) and one `processed_messages` row keyed `(consumer, message_id)` are written in one
   transaction, then the message is acknowledged. Given the same message id delivered again, then nothing new is
   written and the message is acknowledged.
10. Given a message for tenant B, then the consumer acts in tenant B's context only (fresh per message, never a default
    or the previous message's tenant): the row is visible under B and invisible under A; RLS is forced on both tables.
11. Given a message with a missing or malformed tenant id, an unknown tenant, an unknown type or version, or an
    unreadable payload, then it goes straight to `notifications.order-events.dlq` with the reason, and nothing is written.
12. Given a transient failure (database unavailable), then the message is retried after 1 s, 5 s, 30 s, 2 min and 10 min
    (first delivery plus 5 retries); after the last retry fails it goes to the dead-letter queue. A retry that succeeds
    records the notification once.
13. Given a message published from an API request, then the consumer's span has the API request's trace as parent.
14. Then counters `tenantflow.messaging.consumed{consumer, outcome}` count `processed`, `duplicate`, `retried` and
    `dead_lettered`, and the dead-letter replay procedure is documented in `docs/operations/` (no automatic redrive).

**Contracts and structure**
15. Then event types live in a new `Orders.Contracts`; Notifications references Orders only through it; the
    architecture, schema-ownership, role-privilege and RLS tests include Notifications; RabbitMQ runs in Compose with a
    health check via `dev.ps1`, and broker settings come from configuration (password never committed, ADR-0008).

## Invariants
- An outbox row exists if and only if its state change committed; publication never happens inside a DB transaction.
- A row is marked published only after a broker confirm; a published message id never changes.
- A message id produces at most one business effect per consumer (unique `(consumer, message_id)`, same transaction).
- A consumer never runs tenant-owned work without a tenant taken from the validated envelope.
- No ordering is assumed between messages; Notifications needs none.

## Contract
Exchange `tenantflow.events` (topic, durable). Messages are persistent JSON; AMQP `message_id`, `type`, `timestamp`,
`correlation_id`, and headers `tenant_id`, `version`, `traceparent`. Envelope = these fields; body = event payload.

| Event | Payload (v1) |
|---|---|
| `order.reserved.v1` | `orderId, ownerMembershipId, total{amount, currency}, lineCount` |
| `order.cancelled.v1` | `orderId, ownerMembershipId, previousStatus` |

Queues: `notifications.order-events` (bound to `order.reserved.v1` and `order.cancelled.v1` by name, so a new Orders event never lands in its dead-letter queue as an unknown type), retry queues with per-queue TTL dead-lettering back to it,
and `notifications.order-events.dlq`. No new HTTP routes; the OpenAPI snapshot does not change.
Authorization: the Worker holds no user token. Its authority is its broker user and the application database role;
the tenant comes from the envelope written inside the tenant-scoped transaction (ADR-0010 service boundary).

## Data
- `orders.outbox_messages(id uuid PK = message id, tenant_id, type, version, payload jsonb, correlation_id, traceparent,
  occurred_at, attempts, locked_until, published_at, last_error)`; partial index on `(occurred_at) WHERE published_at IS NULL`.
  Not under RLS: it is read across tenants by the publisher (decision 1 below).
- `notifications.notifications(tenant_id, id, kind, order_id, recipient_membership_id, message_id, created_at)` and
  `notifications.processed_messages(tenant_id, consumer, message_id, processed_at, PK (consumer, message_id))`:
  `ENABLE` + `FORCE ROW LEVEL SECURITY`, ADR-0019 policy. New migrations only (expand); nothing is altered.

## Failure modes
- Publisher crash after publish, before mark: republished after the lease (30 s); the consumer deduplicates (criterion 6, 9).
- Broker down: rows accumulate, backlog and age metrics rise, readiness fails; publication resumes without loss (7, 8).
- Consumer crash before ack: redelivered; the processed-message row makes the repeat a no-op (9).
- Poison message: dead-lettered at once, never retried (11). Transient failure: bounded retries, then DLQ (12).
- Two publishers: leases split the work; an expired lease may cause one duplicate publication, which is safe (5, 6).

## Out of scope
Real delivery (email, SMS), templates and preferences; the order saga and automatic recovery of stuck orders (M6);
events from other modules; outbox for other modules (added when they publish); schema registry and automated
compatibility checks; automatic dead-letter redrive; Kafka (M14 lab); Testcontainers RabbitMQ for CI (CI is on hold).

## Open questions
All three were answered with the recommendation by the user on 2026-10-21 (recorded in ADR-0021).

1. **Outbox and RLS (needs ADR, in ADR-0021).** The publisher reads every tenant's rows. Recommended: the outbox is
   messaging infrastructure, not business data, so it is not under RLS; rows carry `tenant_id`; only the store's
   transition method and the publisher touch it (architecture test). Alternative: RLS plus a `BYPASSRLS` publisher role
   (ADR-0011 reserves that for audited platform operations; adds a third role and connection string).
2. **Worker service identity (ADR-0010 deviation).** ADR-0010 places the client-credentials implementation "with the
   first worker that handles tenant data, M5". Recommended: defer it to the first worker that calls an HTTP API; in M5
   the Worker calls no API, so a token would authorize nothing. The roadmap records the deferral.
3. **Processed-message retention.** Recommended: keep all rows in M5 (deduplication must outlive the 13-minute retry
   window and lease expiry); add cleanup with a measured retention later.

## Slices
| Slice | Delivers | Criteria |
|-------|----------|----------|
| S1 | `BuildingBlocks.Messaging` (envelope, outbox row), `Orders.Contracts` events, `orders.outbox_messages` migration, outbox write in `OrderStore.TryTransitionAsync` | 1, 2, 3 |
| S2 | RabbitMQ in Compose and `dev.ps1`; Worker database and broker wiring; publisher with leases, confirms, cleanup; readiness; backlog and age metrics; duplicate-publication test | 4, 5, 6, 7, 8 |
| S3 | Notifications module, topology, idempotent consumer, per-message tenant context, trace propagation, duplicate-consumption and cross-tenant tests | 9, 10, 13, 15 |
| S4 | Retry queues and dead-letter path, consumed counters, replay runbook | 11, 12, 14 |

## Verification
| Criterion | Test | Proven to fail without the change |
|-----------|------|------------------------------------|
| 1 | `OrderOutboxTests.Reserving_and_cancelling_write_one_event_each_in_the_orders_tenant_and_a_rejection_writes_none` | Yes: event written on rejection too, the test fails |
| 2 | `OrderOutboxTests.A_transition_that_loses_its_conditional_update_writes_no_event`, `When_the_outbox_write_fails_the_state_change_rolls_back_with_it` | Yes: insert without the `changed == 1` guard, and insert after the commit, each fail their test |
| 3 | `OrderOutboxTests.Reserving_and_cancelling_...` (exact payload keys, `traceparent`, correlation id) | Asserted; not separately broken (no guard of its own) |
| 4 | `OutboxPublisherTests.Publishes_the_oldest_rows_in_batches_of_50_with_the_whole_envelope_and_marks_them_published` | Yes: claim limit widened, the test fails |
| 5 | `OutboxPublisherTests.A_second_publisher_cannot_claim_rows_another_holds_until_the_lease_passes` | Yes: lease condition removed, the test fails |
| 6 | `OutboxPublisherTests.A_message_published_but_never_marked_is_published_again_with_the_same_id_once_its_lease_passes` | Yes: lease never expiring, or ignored, each fail the test |
| 7 | `OutboxPublisherTests.While_the_broker_is_down_...`, `Cleanup_deletes_only_rows_published_longer_ago_than_the_retention`; `WorkerHostTests.Readiness_is_503_while_the_broker_is_unreachable`; `A_message_the_broker_keeps_refusing_is_deferred_with_growing_backoff_and_never_holds_back_newer_ones` (review fix) | Yes: unconfirmed message marked, pending rows deleted, broker check removed: each fails its test; failed row released at once: the backoff and broker-down tests fail |
| 8 | `OutboxPublisherTests.Backlog_metrics_report_the_pending_count_and_oldest_age_per_module_and_drop_to_zero_when_published` (`MeterCapture`); age keeps rising without a refresh (review fix) | Asserted with exact fake-clock values; not separately broken; age frozen at refresh: the metrics test fails |
| 9 | `NotificationConsumerTests.A_message_delivered_twice_through_the_broker_records_one_notification`, `Two_concurrent_deliveries_of_one_message_record_one_notification`; `The_consumer_keeps_consuming_after_the_broker_drops_its_connection` (review fix) | Yes: processed-message check bypassed, both fail; recovering connection replaced: the reconnection test fails |
| 10 | `NotificationConsumerTests.Each_message_is_recorded_in_its_own_tenant_only_never_in_the_previous_ones`; `RowLevelSecurityTests` (8 forced tables) | Yes: first tenant reused for later messages, the test fails |
| 11 | `DeadLetterTests.Invalid_messages_go_straight_to_the_dead_letter_queue_with_the_reason_and_write_nothing` | Yes: invalid messages treated as transient, the test fails |
| 12 | `DeadLetterTests.A_transient_failure_is_retried_and_the_retry_that_succeeds_records_the_notification_once`, `A_message_that_keeps_failing_is_dead_lettered_after_five_retries` | Yes: retry limit off by one, the dead-letter test fails |
| 13 | `NotificationConsumerTests.The_consumer_span_continues_the_trace_of_the_request_that_published_the_message` (with criterion 3's stored `traceparent`) | Yes: parent context dropped, the test fails |
| 14 | `DeadLetterTests` assert `tenantflow.messaging.consumed` by outcome (`MeterCapture`); runbook `docs/operations/dead-letter-replay.md` | Counters asserted in the tests above; runbook reviewed, not executable |
| 15 | `ModuleBoundaryTests`, `ModuleSchemaTests`, `SchemaOwnershipTests`, `RolePrivilegeTests`, `RowLevelSecurityTests` include Notifications; Compose RabbitMQ with health check (S2); `OutboxAccessTests` (only OrderStore and the Orders migrations name the outbox; open question 1, review fix) | Architecture and schema tests fail on any missing module entry (checklist); a planted outbox query, and the store not allowed, each fail it |
