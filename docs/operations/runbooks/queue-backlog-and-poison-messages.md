# Runbook: Queue backlog, broker outage, poison messages and dead timers

## Symptoms and impact
- `BrokerDown` (cause): the Worker's `rabbitmq` check fails. Outbox rows wait (no events are lost: they are in the
  database), consumers stop, notifications and audit records arrive late.
- `OutboxStale`: a module's oldest unpublished row is older than 5 minutes.
- `DeadLettersGrowing`: a consumer dead-lettered messages; each needs a decision ([dead-letter replay](../dead-letter-replay.md)).
- `DeadTimer`: a workflow timer failed 20 times in a row and never runs again until cleared (reconciliation, order
  deadline, renewal or import/export step stuck for that one subject).
- `SloEventDelivery*`: the share of dead-lettered messages burns the event-delivery budget.

## Severity and escalation
All `page`. `BrokerDown` is a cause and inhibits `OutboxStale`. `DeadLettersGrowing` and `DeadTimer` are events: they
are never inhibited. Escalate dead letters caused by a producer bug to the owning module's developer.

## Preconditions / access required
Grafana; RabbitMQ management UI (`http://localhost:15672`, credentials from `deploy/compose/.env`, read by you only);
for a dead timer, the owner database role.

## Dashboards and diagnostic queries
- Dashboard **TenantFlow / Worker**: *Queue depth*, *Dead-letter queues*, *Consumed by outcome*, *Oldest pending outbox
  row age*, *Dead timers*.
- Broker reachable? (PromQL) `min by (service_name) (tenantflow_health_status{check="rabbitmq"})`, and `up{job="rabbitmq"}`
- Dead letters per consumer since the Worker started (PromQL): `sum by (consumer) (tenantflow_messaging_consumed_total{outcome="dead_lettered"})`
  (`increase()` reads 0 for a counter whose first sample is its first dead letter; the alert handles that case)
- Dead-letter queue depth (PromQL): `sum by (queue) (rabbitmq_detailed_queue_messages{queue=~".*\\.dlq"})`
- Why it was dead-lettered (LogQL): `{service_name="tenantflow-worker"} |~ "(?i)dead-letter"` (events 5101 and 5104 carry
  the message id, queue and reason, and the line's `trace_id` opens the consumer span in Tempo)
- The consumer span of a message and the request that produced it (TraceQL):
  `{ resource.service.name = "tenantflow-worker" && span.messaging.destination.name = "notifications.order-events" }`;
  dead-lettered ones only: `{ span.tenantflow.messaging.outcome = "dead_lettered" }`
- Dead timers per module (PromQL): `max by (module) (tenantflow_messaging_dead_timers)`; the failing handler (LogQL):
  `{service_name="tenantflow-worker"} |= "is dead"` (event 5206 names the timer id, kind and module).

## Immediate safe mitigations
- Broker down: start it (`docker compose up -d --wait` locally). The Worker reconnects; the outbox publishes what waited.
- Poison message: leave it in the dead-letter queue; it harms nothing there. Never purge without reading the reason.
- Dead timer: fix the cause first (the handler's exception in the logs), then clear `dead_at` for that timer only.

## Recovery procedure
1. Broker: once healthy, watch *Oldest pending outbox row age* fall to under a minute.
2. Dead letters: follow [dead-letter replay](../dead-letter-replay.md): decide per reason, replay only fixed causes.
3. Dead timer, as the owner role, in the module's schema:
   `UPDATE <schema>.workflow_timers SET dead_at = NULL, failures = 0, due_at = now() WHERE id = '<timer id>';`
   The next timer job (within a minute) runs it.

## Verification
`BrokerDown`, `OutboxStale` and `DeadTimer` resolve; the dead-letter queue is empty or every remaining message is
recorded with a decision; *Consumed by outcome* shows `processed` again.

## Rollback / forward-fix
A producer emitting malformed messages: roll it back or fix it; a consumer that cannot read a new version: deploy the
consumer first, then replay.

## Customer / stakeholder communication
Delayed notifications or audit records are delivered late, not lost; say so with the delay window.

## Follow-up actions
Record replayed or purged message ids; add a contract test for the message that failed; review whether threat model
0004's accepted rows (MW-A14) still hold.
