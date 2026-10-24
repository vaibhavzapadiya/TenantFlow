# Runbooks

One runbook per incident type of blueprint section 19 that applies to M10, each in the section 22 template
(SPEC-0010 criterion 15). Every alert's `runbook` annotation points at one of them (ADR-0029). Queries are written
for the local observability profile (`docker compose --profile observability up -d --wait`): PromQL in Grafana's Explore on
**Prometheus**, LogQL on **Loki**, TraceQL on **Tempo**. Dashboards: **TenantFlow / Golden signals**, **Worker**,
**Business workflows**.

| Runbook | Alerts |
|---|---|
| [API errors](api-errors.md) | `ApiServerErrors`, `SloCatalogRead*`, `SloOrderSubmission*` |
| [Database saturation and locks](database-saturation-and-locks.md) | `DatabaseDown` |
| [Queue backlog and poison messages](queue-backlog-and-poison-messages.md) | `BrokerDown`, `DeadLettersGrowing`, `DeadTimer`, `OutboxStale`, `SloEventDelivery*` |
| [Stuck payment and reconciliation](stuck-payment-and-reconciliation.md) | `PaymentsStuck`, `RefundReversed`, `SloPaymentResolution*` |
| [Failed import or export](failed-import-export.md) | `ImportExportBacklog`, `ImportExportFailures`, `SloExportCompletion*` |
| [Failed migration](failed-migration.md) | none (the Migrator is a one-shot job: its exit code is the signal) |
| [Suspected cross-tenant access](suspected-cross-tenant-access.md) | `CrossTenantProbing` |
| [Hosts and scheduled jobs](hosts-and-scheduled-jobs.md) | `ApiTelemetryMissing`, `WorkerTelemetryMissing`, `ScheduledJobFailing`, `ScheduledJobLate`, `RetentionRunFailed` |

Severity: `page` means act now; `ticket` means within a working day. Locally nothing is delivered (Alertmanager's
receiver is null); read firing alerts at `http://127.0.0.1:9093` or `GET /api/v2/alerts`. While a cause alert
(`category="cause"`) fires, its symptom alerts are inhibited: start from the cause.

Not here yet: secret rotation, cost spikes, restore and disaster recovery (M12, M13), feature flags (none exist).
Rehearsals are recorded in [`../drills`](../drills).
