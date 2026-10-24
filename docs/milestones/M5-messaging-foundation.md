# M5 — Messaging foundation

**Goal:** modules learn about each other's state changes reliably, without a request waiting on a broker, and every
consumer is safe against the duplicates that reliable delivery brings.

**Gate (roadmap):** duplicate publication and consumption tests pass; backlog and oldest-message age are observable.

Spec: [SPEC-0005](../specs/0005-messaging-foundation.md) (Implemented). Decision:
[ADR-0021](../architecture/decisions/0021-messaging-broker-outbox-and-consumers.md) (Accepted 2026-10-21: RabbitMQ,
per-module outbox, idempotent consumers, retry and dead-letter policy, outbox outside RLS, worker token deferred).
Open Decision 4 (RabbitMQ versus Kafka) is closed by it.

## 1. What was built

```text
deploy/compose/compose.yaml             + rabbitmq 4.3.5 (management, health check, pinned digest)
src/
├── BuildingBlocks/Abstractions/Messaging   IIntegrationEvent, OutgoingMessage, IncomingMessage, MessageOutcome,
│                                           InvalidMessageException (no dependencies)
├── BuildingBlocks/Messaging (new)          OutboxMessage + mapping, OutboxStore (lease claim, mark, defer, cleanup,
│                                           backlog), OutboxPublisher, OutboxMetrics, ProcessedMessage + mapping
├── BuildingBlocks/Messaging.RabbitMq (new) RabbitMqConnection, RabbitMqMessageBus (confirms), RabbitMqConsumer
│                                           (retry queues, dead-letter queue, consumer span), MessageEnvelope
├── Hosts/Worker/Messaging                  OutboxPublisherService, NotificationsConsumerService, readiness checks
└── Modules/
    ├── Orders/Contracts (new)              order.reserved.v1, order.cancelled.v1
    ├── Orders/Infrastructure               orders.outbox_messages; TryTransitionAsync writes the event
    └── Notifications/ (new module)         OrderEventConsumer, NotificationStore, schema "notifications"
docs/operations/dead-letter-replay.md       inspection and manual replay runbook
tests/Database/Messaging/                   OutboxPublisherTests, NotificationConsumerTests, DeadLetterTests
tests/Database/Orders/OrderOutboxTests.cs, tests/Architecture/OutboxAccessTests.cs
```

| Slice | Commit | Delivered |
|---|---|---|
| S1 | `7431efb` | outbox row written in the transition's transaction, only when the conditional update won |
| S2 | `152b830` | RabbitMQ in Compose; Worker publisher with leases and confirms; cleanup; backlog and age metrics |
| S3 | `416cdad` | Notifications module, processed-message deduplication, per-message tenant, trace continuation |
| S4 | `7f47bc6` | retry queues (1 s .. 10 min), dead-letter queue with reasons, consumed counters, runbook |
| Review fixes | `b29ec81` | connection recovery kept, refused rows deferred with backoff, live age gauge, outbox-access test |
| Process | `32fab40` | your decision: filtered checks per slice, one full gate per milestone |

Flow: `OrderService` decides the event → `OrderStore.TryTransitionAsync` updates the order and inserts the outbox row in
one tenant-scoped transaction → the Worker claims rows, publishes to `tenantflow.events` (routing key = type), waits for
confirms, marks them → `notifications.order-events` → `OrderEventConsumer` records the notification and the processed
message in one transaction → ack.

## 2. Architectural reasoning

**The outbox removes the dual write; it does not remove duplicates.** Writing the event in the same transaction as the
state change means the event exists if and only if the change committed. Publishing happens later, outside any
transaction (CLAUDE.md rule 3). A publisher can die after the broker confirmed but before it marked the row, so the
same message is published again. That is accepted by design and handled at the consumer.

**Leases instead of long transactions.** The publisher claims rows with
`UPDATE ... SET locked_until = now + 30 s WHERE id IN (SELECT ... FOR UPDATE SKIP LOCKED LIMIT 50) RETURNING ...` and
commits at once. Two publishers split the work; a crashed one's rows come back when the lease passes. Holding
`FOR UPDATE` across the publish would have kept a transaction open during a remote call.

**Confirms decide "published".** A row is marked only after the broker's confirm for that message. A batch is written
in order and its confirms are awaited together, so a batch costs one round trip, not fifty. A row the broker refuses is
deferred with exponential backoff (2 s doubling to 5 minutes), so it never heads every later batch.

**Idempotency is a key in the consumer's own transaction.** `processed_messages (consumer, message_id)` is inserted first
with `ON CONFLICT DO NOTHING`; only the delivery that inserted it writes the notification, and both commit together.
Two concurrent deliveries of one message produce one notification, proven on real PostgreSQL.

**The tenant comes from the envelope, and nothing else.** The worker has no user and no membership. The event's tenant was
taken from the authenticated request when the row was written; the consumer validates it (well-formed, the tenant
exists) and passes it explicitly to a store that runs under forced RLS. Each message is handled in its own DI scope, so
nothing of one message's tenant can reach the next (ADR-0010 service boundary).

**Retries survive restarts.** Delays are TTL queues that dead-letter back to the main queue, not in-process sleeps. A
copy is republished with confirms before the original is acknowledged: a crash duplicates, never loses. Invalid messages
skip the retries. Replay is manual and documented, because blind redrive repeats the failure.

**Bind by name, not by pattern.** The Notifications queue binds `order.reserved.v1` and `order.cancelled.v1` explicitly.
A wildcard would have sent every future Orders event into its dead-letter queue as "unknown type".

**Why RabbitMQ.** Work queues, acknowledgements, TTL and dead-lettering are native; replay and partitions (Kafka's
strengths) are not needed yet. Kafka is the M14 comparison lab. `RabbitMQ.Client` directly, without a framework, keeps
the outbox, retry and dead-letter mechanics visible.

## 3. Security considerations

- `notifications.notifications` and `notifications.processed_messages` are `ENABLE` + `FORCE` RLS; the forced-RLS test
  counts 8 tables. The consumer writes only inside `TenantScopedTransaction` for the message's tenant.
- `orders.outbox_messages` is deliberately **not** under RLS (ADR-0021 decision 7): the publisher reads every tenant's
  rows. `OutboxAccessTests` fails if any module or host code other than `OrderStore` and the Orders migrations names
  the table. Payloads carry identifiers, statuses and amounts only.
- Envelope validation fails closed: a missing, malformed or empty tenant id, an unknown tenant, type or version, or an
  unreadable payload is dead-lettered and writes nothing. Dead-letter reasons never contain payload text.
- Broker credentials come from `.env` through user-secrets (ADR-0008); the port binds to 127.0.0.1.
- **Residual risks (deferred, your choice):** the broker user is shared by publisher and consumer, so anyone with it can
  publish a message for any existing tenant; message ids are not tenant-scoped in `processed_messages`, so a forged
  message reusing another tenant's id is dropped as a duplicate (review finding 6). Both belong with per-service broker
  users and the worker identity in M9. No client-credentials token for the worker yet: it calls no API (ADR-0021).

## 4. Verification

Fresh evidence for the gate (2026-10-21): the full gate after `32fab40`, the quality run at `6eb3e98` (a test-message-only change):

| Command | Result |
|---|---|
| `./scripts/verify.ps1 -Database` | all checks passed; Api 125/125, IdentityAccess 67/67, Tenants 77/77, Catalog 7/7, Inventory 5/5, Orders 6/6, Architecture 7/7, Worker 13/13, Database 183/183; 0 warnings |
| `./scripts/progress.ps1 -Verify` (CI test command with coverage) | quality run passed for `6eb3e98`, 0 warnings, 307/307 non-database tests; compose valid. Line coverage of the new messaging code is low here (BuildingBlocks.Messaging 44%, RabbitMq 19%, Notifications 4-38%) because the coverage run excludes the database tests, which are where it is exercised |
| Timing-sensitive classes, 3 runs in a row | OutboxPublisherTests 7/7, NotificationConsumerTests 5/5, DeadLetterTests 3/3 |

**Break-it checks re-run for the gate** (fault injected, test fails at an assertion, fault restored, rebuilt):

- Duplicate publication: the lease never expires: the republish-after-lease test fails (with the claim and backoff tests).
- Duplicate consumption: the processed-message check bypassed: the duplicate-through-broker and concurrent-delivery
  tests fail.
- Cross-tenant: the first message's tenant reused for later ones: the cross-tenant consumer test fails.
- Transactional outbox: the outbox insert moved after the commit: the rollback test fails.

Per-slice and review proofs (lost update writes no event, bounded batch, unconfirmed not marked, cleanup keeps pending
rows, broker readiness, trace parent, invalid messages not retried, retry limit, connection recovery, deferral backoff,
live age, outbox access) are in the spec's Verification table and the tracker.

**A flaky test, reported honestly:** `WorkerHostTests.Host_refuses_to_start_without_a_broker_password_and_names_the_key` failed twice inside `progress.ps1 -Verify` and passed in 16 other runs (alone, full suite, and the same nested invocation). The log kept no assertion text, so the cause is unknown; `6eb3e98` makes a failure print what the host did. Watch it in M6.

**Not verified:** the Testcontainers path for PostgreSQL and any CI path for RabbitMQ (CI is on hold); a real broker
restart (the reconnection test makes the broker close the connection through its management API instead); the
cross-process trace in Tempo (the trace continuation is asserted in tests, not looked at in Grafana).

## 5. Trade-offs and assumptions

- Duplicates are normal; every future consumer needs its own `processed_messages` row in its own transaction.
- Processed-message rows are kept forever in M5 (your choice); retention comes with a measured window.
- No ordering between messages; Notifications needs none. A consumer that does will need a per-aggregate sequence.
- Published outbox rows are kept 7 days for diagnosis, then deleted in batches of 500.
- Changing retry delays needs the retry queues deleted while the worker is stopped (runbook).
- The Worker references Orders.Infrastructure for its schema name and Tenants and Notifications to run them; it is a
  host, so that is composition, not a module boundary crossing.
- Notifications records only; no delivery, templates or preferences.

## 6. Mentor exercises

1. Move the outbox insert in `OrderStore.TryTransitionAsync` after `CommitAsync`. Which test fails, and what would a
   crash between the two statements leave behind in production?
2. Remove `FOR UPDATE SKIP LOCKED` but keep the lease condition. Run two publishers at once: what can happen, and why
   does the sequential lease test not catch it?
3. Make `processed_messages` keyed by `message_id` alone. What breaks when a second consumer (say Reporting) reads the
   same events?
4. Replace the TTL retry queues with `Task.Delay` inside the consumer. What happens to a message waiting out its 10-minute
   delay when the Worker restarts, and to the messages behind it?
5. Add `order.rejected.v1`. Which files change in Orders, which in Notifications, and what happens to rejected-order
   events before Notifications binds the new key?

## 7. Interview talking points

- "How do you publish events reliably?" Transactional outbox in the module's own schema, written with the state change;
  a lease-based publisher with publisher confirms; duplicates expected and removed at the consumer.
- "How do you make a consumer idempotent?" A processed-message key inserted in the same transaction as the effect, so a
  redelivery hits the key and changes nothing; proven with two concurrent deliveries.
- "How does a background worker know the tenant?" From the validated envelope written inside the authenticated
  request's transaction, set fresh per message, enforced again by row-level security.
- "What do you do with poison messages?" Invalid ones go straight to a dead-letter queue with a reason; transient
  failures get bounded, restart-safe retries; replay is a documented human decision.

## 8. Exit checklist

- [x] Duplicate publication test passes (`A_message_published_but_never_marked_is_published_again_with_the_same_id_once_its_lease_passes`; proof re-run)
- [x] Duplicate consumption tests pass (`A_message_delivered_twice_through_the_broker_records_one_notification`, `Two_concurrent_deliveries_of_one_message_record_one_notification`; proof re-run)
- [x] Backlog and oldest-message age observable (`tenantflow.outbox.pending`, `tenantflow.outbox.oldest_pending_age`, asserted with `MeterCapture`, including a rising age without refresh)
- [x] SPEC-0005 Implemented: all 15 criteria have verification rows
- [x] Tenant isolation review: no critical or high findings; code review: 10 findings, 9 fixed in `b29ec81`, 1 deferred (finding 6, M9)
- [x] ADR-0021 accepted by you on 2026-10-21; Open Decision 4 closed
- [ ] **You:** review this guide and the M5 commits on branch `m5-messaging`, and merge into `main` when satisfied
- [ ] **You:** approve the start of M6 (payment workflow; Open Decision 12, sandbox payments, is due first)
