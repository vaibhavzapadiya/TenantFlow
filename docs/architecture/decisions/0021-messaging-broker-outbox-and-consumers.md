# ADR-0021: Messaging with RabbitMQ, a per-module outbox, and idempotent consumers

- Status: Accepted (2026-10-21 by the user)
- Date: 2026-10-21
- Owners: TenantFlow tech lead (options recommended by Claude, chosen and accepted by the user on 2026-10-21)
- Related decisions: [ADR-0002](0002-modular-monolith.md), [ADR-0010](0010-trust-boundaries-between-user-types.md), [ADR-0011](0011-postgresql-row-level-security.md), [ADR-0012](0012-module-data-ownership.md), [ADR-0020](0020-orders-and-reservations.md); [SPEC-0005](../../specs/0005-messaging-foundation.md); blueprint section 11
- Closes: Open Decision 4 (RabbitMQ versus Kafka)

## Context
M5 adds asynchronous integration: Orders announces reservations and cancellations, and a new Notifications module
reacts. A state change and a broker publish cannot share one transaction (the dual-write problem), and CLAUDE.md
rule 3 forbids remote calls inside a database transaction. Delivery is therefore at least once, and every consumer
must be idempotent. The broker is new stateful infrastructure (ADR-0001 trigger).

## Decision
1. **Broker: RabbitMQ**, through `RabbitMQ.Client` directly (no MassTransit or other framework), running in Compose
   with a health check. One durable topic exchange `tenantflow.events`; routing key = event type (`order.reserved.v1`).
   Kafka stays a comparison lab (M14).
2. **Outbox per module schema** (`orders.outbox_messages`), written by the module's own store in the same transaction
   as the state change, so schema ownership (ADR-0012) holds. Shared code (envelope, outbox row mapping, publisher loop,
   metrics) lives in `BuildingBlocks.Messaging`; each module registers its outbox with the publisher.
3. **Publisher in the Worker host.** It claims up to 50 oldest pending rows with a lease
   (`UPDATE ... SET locked_until = now + 30 s WHERE id IN (SELECT ... FOR UPDATE SKIP LOCKED LIMIT 50) RETURNING`), commits,
   publishes each with publisher confirms, then marks confirmed rows published in a new transaction. An expired lease
   makes a row claimable again, so a crash causes duplicate publication, never loss. Published rows are deleted after
   7 days in bounded batches.
4. **Envelope:** message id (= outbox row id), type, version, tenant id, occurred-at (UTC), correlation id, W3C
   `traceparent`; payloads carry identifiers, statuses and amounts only, never personal data.
5. **Idempotent consumers.** A consumer writes its business effect and a `processed_messages (consumer, message_id)` row
   in one tenant-scoped transaction, then acknowledges. A duplicate hits the unique key and is acknowledged with no effect.
   The tenant comes from the envelope, is validated (well-formed, known tenant), and is set fresh per message.
6. **Retries and dead letters.** Transient failures are retried through TTL retry queues after 1 s, 5 s, 30 s, 2 min and
   10 min (first delivery plus 5 retries), then dead-lettered. Invalid messages (bad envelope, unknown tenant, type or
   version, unreadable payload) are dead-lettered at once. Replay is a documented manual procedure, never automatic.
7. **Outbox is not under RLS**: it is messaging infrastructure read across tenants by the publisher; rows carry
   `tenant_id`, and only the owning store and the publisher touch it. Consumer tables are under forced RLS as usual.
8. **No worker token yet.** The Worker calls no HTTP API, so the client-credentials service identity from ADR-0010 is
   deferred to the first worker that does. The Worker's authority is its broker user and the application database role.
9. **Processed-message rows are kept** in M5; cleanup with a measured retention comes later.

## Options considered
- Broker: RabbitMQ (chosen: queues, acks, dead-lettering fit workflow messaging) vs Kafka (replay and partitions,
  heavier operations, weaker fit for work queues) vs MassTransit over RabbitMQ (less plumbing, but hides the outbox,
  retry and dead-letter mechanics this project exists to learn; v9 is commercial).
- Outbox: per module (chosen) vs one shared `messaging` schema (simpler publisher, but every module writes a table it
  does not own, against ADR-0012).
- Publisher claim: lease then publish (chosen, no transaction open during the remote call) vs holding `FOR UPDATE` while
  publishing (breaks rule 3, long transactions) vs PostgreSQL logical replication / CDC (new infrastructure).
- Retries: TTL retry queues (chosen, delays survive a Worker restart) vs in-process delays (lost on restart, block
  the consumer) vs the delayed-message plugin (extra broker plugin).

## Consequences
- Easier: modules integrate without waiting on each other; a broker outage delays messages but loses none; duplicates
  are harmless by construction; backlog, age and dead letters are visible.
- Harder: every consumer must keep a processed-message table; the Worker now needs the database and the broker;
  queue topology must be declared and kept in sync with code; one more Compose service to run and secure.
- Follow-up: a Testcontainers RabbitMQ path when CI resumes; outbox for other modules when they publish; a schema
  compatibility check for integration events; processed-message retention.

## Validation
SPEC-0005: the duplicate-publication test (crash between publish and mark), the duplicate-consumption test, the
cross-tenant consumer test, the dead-letter tests, and backlog and oldest-age metrics asserted with `MeterCapture`,
each guarantee proven to fail with its guard removed.

## Revisit triggers
Event replay or stream processing becoming central (Kafka); outbox polling load or latency becoming measurable; a
second broker consumer host; a module needing ordered processing per aggregate.
