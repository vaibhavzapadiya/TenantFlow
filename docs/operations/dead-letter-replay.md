# Dead-letter queue: inspection and replay

Runbook for the dead-letter path of ADR-0021 and SPEC-0005 criteria 11, 12 and 14. Replay is a deliberate,
manual decision: nothing redrives dead letters automatically (blueprint section 11).

## How a message gets there

Each consumer queue `<queue>` (today `notifications.order-events`) has:

| Queue | Purpose |
|-------|---------|
| `<queue>.retry.1` … `.retry.5` | A failed message waits here 1 s, 5 s, 30 s, 2 min, 10 min, then returns to `<queue>` |
| `<queue>.dlq` | Messages that will not be processed without a human decision |

- **Invalid** messages go to the dead-letter queue at once, never retried: a missing or malformed message id,
  tenant id or version, an unknown tenant, an unknown type or version, or an unreadable payload.
- **Transient** failures (for example the database is down) are retried five times, then dead-lettered with the
  reason `Retries exhausted: <exception type>`.

A dead letter keeps the original envelope (message id, type, `tenant_id`, `version`, `traceparent`) and adds
`x-tenantflow-dead-letter-reason` and `x-tenantflow-retry` (retries made). Reasons never contain payload text.

## Notice

- The counter `tenantflow.messaging.consumed{consumer, outcome="dead_lettered"}` rises. Alerting on it arrives in M10.
- The worker logs event 5101 (invalid) or 5104 (retries exhausted) with the message id and queue.
- The trace of the message (from `traceparent`) shows the request that caused it.

## Inspect

Locally: open the management UI at `http://localhost:15672` (user and password from `deploy/compose/.env`, read
by you, never pasted anywhere), open **Queues → `<queue>.dlq` → Get messages** with **Ack mode: Nack message requeue
true**, so inspecting does not remove anything. Elsewhere use the same UI or `rabbitmqadmin` with an operator account.

Decide per reason:

| Reason | Action |
|--------|--------|
| `Unknown tenant.` | Check the tenant id in Tenants. A tenant that never existed: purge the message. One created after the event: replay. |
| `Unknown message type.` / `Unsupported message version.` | A producer is ahead of this consumer. Deploy the consumer that understands it, then replay. |
| `Missing or malformed …` / `Unreadable payload.` | A producer bug. Fix the producer; the message itself cannot be repaired. Purge after recording the message id. |
| `Retries exhausted: …` | Find the cause in the logs and traces (database, a bug). Once fixed, replay. |

## Replay

Replay is safe because every consumer is idempotent: a message id it already processed is acknowledged with no
effect (`processed_messages`). Replay only messages whose cause is fixed, and only into their own queue.

1. In the management UI, on `<queue>.dlq`, use **Move messages** (the shovel plugin) with destination queue `<queue>`.
   Without the plugin, use **Get messages** with **Ack mode: Automatic ack**, then **Publish message** to the
   default exchange with routing key `<queue>`, copying properties and headers. Remove `x-tenantflow-retry` so the
   message gets its five retries again.
2. Watch `tenantflow.messaging.consumed` for `processed` (or `duplicate`) and confirm the dead-letter queue is empty.
3. Record what was replayed or purged, by message id, in the incident notes.

Never move dead letters to another consumer's queue, and never replay a whole queue without reading the reasons.

## Changing the retry delays

Retry queues are declared with their delay (`x-message-ttl`); RabbitMQ refuses to redeclare a queue with a different
value. To change the delays: stop the worker, wait until the retry queues are empty, delete them, deploy, and start
the worker, which declares them again.
