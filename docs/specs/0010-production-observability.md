# SPEC-0010: Production observability

- Status: Implemented (2026-10-22; approved by the user at the start of M10)
- Milestone: M10
- Owner: the user (TenantFlow tech lead)
- Related: blueprint sections 14, 16, 19 (operational runbooks) and 22 (runbook template); ADR-0007, ADR-0021,
  ADR-0022, ADR-0023, ADR-0024, ADR-0025 (telemetry retention); Proposed ADR-0029, ADR-0030, ADR-0031;
  threat models 0001 to 0008 (rows due in M10); SPEC-0001 (telemetry pipeline), SPEC-0005, SPEC-0006, SPEC-0008

> Drafted on 2026-10-22 while M9 was still being finished. It reads the code and documents of
> `m9-security-privacy` at commit `7c1d12c` (M9-S6). Re-check the evidence below after the M9 gate, before approval.

## Outcome
An operator who is told "orders are slow", "a payment never resolved" or "exports stopped" can find the cause from
TenantFlow's own metrics, logs and traces, without attaching a debugger or reading the database by hand. Each key
user journey and worker workflow has a dashboard, a stated objective, alerts that fire on real harm and stay quiet
otherwise, and a runbook that says what to do next. Telemetry stays free of tenant ids in labels, secrets and
personal data.

## What exists today (evidence)
- Pipeline (SPEC-0001, ADR-0007): OTLP to the Collector, then Tempo, Prometheus (OTLP receiver) and Loki; Grafana with
  provisioned data sources only. No dashboards, no alert rules, no Alertmanager, no retention settings.
- Instrumentation: ASP.NET Core, HttpClient and runtime metrics; spans for HTTP, HttpClient, consumer handling
  (`TenantFlow.Messaging`, joined to the producer trace through the stored `traceparent`) and the worker heartbeat.
- Meters: `tenantflow.auth.denials` (scheme, reason), outbox pending and oldest age, `tenantflow.messaging.consumed`
  (outcome), imports/exports due, oldest due age, jobs, failed rows and duration, worker heartbeats.
- Missing against blueprint section 16: database client spans and metrics (Npgsql emits them; nothing subscribes),
  broker queue depth and dead-letter count, payment and refund outcomes, unknown results and reconciliation age,
  reservation conflicts, scheduled-job lateness and failures, dead timers, retention-task failures, support grants.
  The critical "refund reversed after success" and "timer is dead" events are logs only.

## Acceptance criteria

Signals
1. Given the API serves requests, when the golden-signal dashboard is opened, then it shows rate, errors (5xx and 4xx
   separately) and p50/p95/p99 duration per `http.route` and method, plus process saturation (CPU, GC, thread pool
   queue, Npgsql pool usage) for each host.
2. Given a request that reads the database, when its trace is opened in Tempo, then it has a child span per database
   command with the statement text and no parameter values.
3. Given the worker runs, when the worker dashboard is opened, then it shows per queue: depth, dead-letter count,
   consumed messages by outcome, and oldest outbox age per module; per scheduled job: runs by outcome, duration and
   lateness (actual minus scheduled fire time); and the count of dead timers.
4. Given payments run, when the business dashboard is opened, then it shows payment attempts by outcome, unknown-result
   count, oldest unresolved payment age, provider call latency, refunds by outcome, refund reversals, reservation
   conflicts by reason, and export/import queue age, duration and failed rows.

Guards (threat model 0008)
5. Given any TenantFlow meter, when a test records every instrument's tags, then the test fails if a tag key is
   outside the allow-list or a tag value is a GUID (DP-A05).
6. Given a request with a query string, when its server span is exported, then no query-string value appears in any
   attribute (DP-A25).
7. Given a sensitive value planted in an exception message and in a log scope, when the fault is logged, then the
   exported log record does not contain it (DP-A03; the mechanism is ADR-0031).
8. Given the observability profile starts, when Loki, Tempo and Prometheus configuration is read back, then each keeps
   7 days of data (ADR-0025, DP-A12).

SLOs and alerts
9. Given each journey in the SLO table (ADR-0030), when its SLO document is read, then it states the event, window,
   success and latency definition, eligible traffic and exclusions, target, error budget, alert policy and owner
   (blueprint section 16), and synchronous and asynchronous objectives are separate.
10. Given the alert rules, when their unit tests run with synthetic series, then each rule fires on its harm case and
    does not fire on the healthy case and on a short spike below its `for` duration.
11. Given a refund reversal, a dead timer, a growing dead-letter queue, a stale outbox, a late or failing scheduled
    job, or a failed retention run, when it happens, then a page-level alert fires (PY-A25, MW-A07, DP-A12 follow-up).
12. Given a spike of `tenantflow.auth.denials` with reason `not_a_member` (a user of tenant A calling tenant B's
    routes), when the spike crosses the threshold, then a security alert fires, and neither the alert nor the series
    carries a tenant id or subject (threat models 0001 and 0006).
13. Given a healthy stack under the smoke traffic, when the alert state is read, then no alert is firing.

Diagnosis (the milestone gate)
14. Given each fault drill (database stopped, broker stopped, provider simulator returning unknown results, worker
    stopped, poison message), when the drill runs, then the expected alert fires, its runbook link opens, and the
    runbook's queries lead from the alert to the failing trace and its logs. The drill record lists the time to each step.
15. Given each runbook required by blueprint section 19 that applies to M10 (API errors, database saturation and locks,
    queue backlog and poison messages, stuck payment and reconciliation, failed import/export, failed migration,
    suspected cross-tenant access), when it is read, then it follows the section 22 template and every query in it
    returns data against the drill stack.

## Invariants
- No metric label carries a tenant id, subject, email, order or payment id, or free text (blueprint section 16).
- No secret or personal data in spans, logs, baggage, dashboards, alert annotations or runbooks (CLAUDE.md rule 6).
- Telemetry never blocks or fails a request or a job: export stays asynchronous and bounded (SPEC-0001).
- Alert rules and dashboards are files in the repository; nothing is configured by hand in the Grafana UI.

## Contract
- New instruments follow the existing naming (`tenantflow.<area>.<thing>`, UCUM units) and closed label sets. Each new
  label value list is part of the telemetry contract, like the denial reasons today.
- Added in S2 (label values in brackets): `tenantflow.payments.attempts` (outcome: a payment status name),
  `tenantflow.payments.unresolved` and `tenantflow.payments.oldest_unresolved_age` (kind: `payment`, `refund`; payments
  and refunds reconciliation checked at least once and still open), `tenantflow.payments.provider_duration` (provider,
  operation: `create`, `status`, `refund`, `refund_status`; outcome: `answered`, `unavailable`, `error`),
  `tenantflow.refunds.completed` (outcome: a status name or `reversed`), `tenantflow.orders.reservation_conflicts`
  (reason: `insufficient_stock`), `tenantflow.scheduling.runs` (job, outcome: `succeeded`, `failed`),
  `tenantflow.scheduling.lateness` and `.duration` histograms (job: a task name or `timers.<schema>`),
  `tenantflow.messaging.dead_timers` (module), `tenantflow.platform.support_grants` (grants opened, no label), and
  Npgsql's own `Npgsql` spans and metrics. Retention runs are `tenantflow.scheduling.runs{job="retention"}` instead of a
  separate `tenantflow.privacy.retention_runs`: the same signal, one instrument fewer. Broker queues come from RabbitMQ's
  `rabbitmq_detailed_queue_messages{queue}` (decision 4).
- The label keys are the allow-list in `MeterTagGuard` (`tests/Shared/TenantFlow.Testing`).
- Alert annotations carry a summary, a severity (`page` or `ticket`) and a runbook path; never a value from a request.
- Who sees telemetry: only operators with Grafana access (local login today, DP-A11). Deployed access control is M12.

## Data
No application schema change and no migration. New files only: dashboards and alert rules under `deploy/compose/config`,
SLO documents under `docs/operations/slos/`, runbooks under `docs/operations/runbooks/`, and Compose changes for
retention and any component ADR-0029 adds.

## Failure modes
- Collector or backend down: hosts keep serving; telemetry is lost for that window (accepted, SPEC-0001). A
  "telemetry missing" alert (no heartbeat series for 5 minutes) makes the gap visible.
- Alert storm when a shared dependency fails: alerts group by dependency, and symptom alerts are inhibited while the
  cause alert fires (ADR-0029).
- Cardinality explosion from a new label: prevented by criterion 5 before merge.
- Gauges computed by queries (outbox age, unresolved payments): the query is bounded, runs on the existing metrics
  task cadence, and a failed query reports nothing rather than zero, so a stale panel is not mistaken for healthy.

## Out of scope
Deployed telemetry backends, access control and retention (M12); load-based SLO targets and capacity (M11, Open
Decision 9); restore and disaster-recovery runbooks (M13); cost per workload; tenant-level dashboards for customers.
Threat model 0007's SC-A13 is due in M10 under ADR-0005 but is a toolchain task, not part of this spec.

## Threat-model rows due in M10
Planned and covered here: PY-A25 (criterion 11), MW-A07 (11), DP-A03 (7), DP-A05 (5), DP-A12 (8, 11), DP-A25 (6).
Accepted rows to revisit with the runbooks: BW-A07, BW-A11, BW-A20, MW-A14, DP-A22. Monitoring follow-ups named in
threat models 0001, 0002, 0005 and 0006 are criteria 4, 11 and 12.

## Decisions
The owner adopted every recommendation below on 2026-10-22 ("follow the recommended approach"). No question is open.
ADR-0029, ADR-0030 and ADR-0031 record the chosen options and stay `Proposed` until the owner accepts them. The owner
approved the spec on 2026-10-22 after the M9 gate, when M10 started.

1. **Where alert rules live and how they are tested** (needs ADR-0029). Prometheus rule files with `promtool test
   rules` plus Alertmanager, or Grafana-managed alerting provisioned from files, or both. *Decided:* Prometheus
   rules and Alertmanager: rules are unit-testable (criterion 10) and portable to managed Prometheus in M12.
2. **SLO targets before the M11 workload model** (needs ADR-0030). The blueprint says "Do not invent SLOs before
   workload and user journeys are understood", and Open Decision 9 puts performance targets in M11, after M10.
   *Decided:* define the SLIs and journeys now with provisional targets marked "to be confirmed by the M11
   baseline"; alert on burn rate, not on the raw target. (Not chosen: SLIs and dashboards only, targets in M11.)
3. **Log redaction mechanism** (DP-A03, needs ADR-0031). Keep exception messages; drop them in an OpenTelemetry log
   processor; or adopt `Microsoft.Extensions.Compliance.Redaction` with data classification. *Decided:* an
   allow-list processor in `TenantFlow.BuildingBlocks.Observability` that keeps exception type and stack but replaces
   the message of non-TenantFlow exceptions, plus the planted-value test; no new package.
4. **Broker queue depth and dead-letter count.** Scrape RabbitMQ's built-in Prometheus endpoint (Compose and
   Prometheus config change, no new component), or have the worker poll queue counts. *Decided:* the broker
   endpoint; polling duplicates what the broker already measures.
5. **Database server signals** (lock waits, deadlocks, slow queries). Npgsql client metrics and spans only, or also a
   `postgres_exporter` container. *Decided:* client side in M10; server-side exporter in M11, where saturation
   is measured. This leaves lock waits visible only through slow spans until then, which criterion 14's database
   drill must show is enough.
6. **Statement text in database spans.** Npgsql spans carry SQL text without parameters. *Decided:* keep it;
   values never appear and the text is what makes a slow span diagnosable. Confirm with criterion 2's test.
7. **Where alerts go locally.** No real receiver exists. *Decided:* Alertmanager with a null receiver; tests
   read `/api/v2/alerts`. A webhook to a chat tool waits for M12.
8. **Are fault drills automated?** *Decided:* scripted in `scripts/dev.ps1 drill <name>` and run by hand at the
   gate with a recorded result; not part of `verify.ps1`, because they stop shared dependencies.
9. **Accepted threat rows.** Do BW-A07, BW-A11, BW-A20, MW-A14 and DP-A22 stay accepted after the runbooks are
   written? *Decided:* review each in S6 and record the answer in its threat model.

## Slices

| Slice | Delivers | Criteria |
|-------|----------|----------|
| S1 | Telemetry guards: tag allow-list test, query-string test, log redaction (ADR-0031) and its planted-value test, 7-day retention in Compose. Riskiest first: the redaction choice touches every host. | 5, 6, 7, 8 |
| S2 | Missing signals: Npgsql spans and metrics, scheduled-job listener, payment, refund, reservation, dead-timer, retention and support-grant instruments, broker queue metrics | 2, 3, 4 |
| S3 | Dashboards as code: golden signals per host, worker, business | 1, 3, 4 |
| S4 | SLO documents and recording rules (ADR-0030) | 9 |
| S5 | Alert rules, Alertmanager, rule unit tests (ADR-0029) | 10, 11, 12, 13 |
| S6 | Runbooks, fault drills, drill records; accepted-row review | 14, 15 |

## Verification

| Criterion | Test | Proven to fail without the change |
|-----------|------|------------------------------------|
| 1 | `ObservabilityConfigTests.Each_dashboard_is_provisioned_from_the_repository_with_the_queries_its_criterion_needs("tenantflow-golden-signals")`, `Grafana_loads_the_dashboards_folder_read_only`; `dev.ps1 smoke` finds the three dashboards in Grafana; panels with data observed at the gate | Yes (S3): without the 4xx query the dashboard test fails (`Assert.Contains`) |
| 2 | `DatabaseTelemetryTests.Database_command_spans_carry_the_statement_but_no_parameter_value` | Yes (S2): without the `Npgsql` source no database span exists (`Assert.Contains` fails); parameter values are absent by Npgsql's default, not proven by a break |
| 3 (dashboard) | `ObservabilityConfigTests.Each_dashboard_is_provisioned_from_the_repository_with_the_queries_its_criterion_needs("tenantflow-worker")` | Deferred to gate |
| 3 (signals) | `SchedulerTests.Quartz_runs_each_modules_due_rows_from_a_clustered_persistent_job_store` (runs, lateness, dead-timer gauge), `WorkflowTimerTests` (dead count); broker queues by the RabbitMQ scrape job (observed at the gate) | Deferred to gate |
| 4 (dashboard) | `ObservabilityConfigTests.Each_dashboard_is_provisioned_from_the_repository_with_the_queries_its_criterion_needs("tenantflow-business")` | Deferred to gate |
| 4 (signals) | `PaymentEndpointTests.A_reserved_order_is_charged_once_for_its_own_amount_and_announces_the_outcome` (attempts), `RefundEndpointTests.A_refund_failed_after_it_succeeded_is_counted_as_reversed`, `WorkflowTimerTests.A_rescheduled_timer_counts_as_unresolved_since_its_subject_was_created` | Deferred to gate |
| 5 | `MeterTagGuardTests` (both); `MeterTagGuardFixture` checks every `TenantFlow.*` measurement of the Api, Worker and Database test runs | Deferred to gate |
| 6 | `TelemetryRedactionTests.A_query_string_value_or_key_never_reaches_the_server_span` | Deferred to gate |
| 7 | `TelemetryRedactionTests.A_planted_value_in_a_third_party_exception_or_an_unlisted_scope_is_not_exported` | Yes (S1): without `LogRedactionProcessor` the planted exception message is exported (`Assert.DoesNotContain` fails) |
| 8 | `ObservabilityConfigTests.Loki_tempo_and_prometheus_are_configured_to_keep_7_days`; `dev.ps1 smoke` reads the running values back | Yes (S1): the read-back against the stack before the change gave 15d, 0s and no 168h block retention |
| 9 | `ObservabilityConfigTests.Every_slo_document_has_the_blueprint_fields_and_a_recording_rule_for_each_window`; SLI recording rules unit-tested by `slo.rules.test.yml` (`dev.ps1 rules`) | Yes (S4): without the 5xx filter in the catalog SLI, promtool's test fails |
| 10 | `alerts.rules.test.yml` (22 promtool cases: every rule's harm, healthy and short-spike case; SLO burn per journey), run by `dev.ps1 rules`; `ObservabilityConfigTests.Every_alert_has_a_severity_a_category_and_a_runbook_and_quotes_no_value` | Yes (S5): `RefundReversed` without its new-series clause misses the first reversal (promtool test fails) |
| 11 | `RefundReversed`, `DeadTimer`, `DeadLettersGrowing`, `OutboxStale`, `ScheduledJobFailing`, `ScheduledJobLate`, `RetentionRunFailed` (all `page`) with their promtool cases; `HealthStatusTelemetryTests` for the dependency gauge | Deferred to gate (drills) |
| 12 | `CrossTenantProbing` (`page`): fires on sustained `not_a_member` denials, quiet on a burst and on other reasons; the series and annotations carry no tenant or subject | Deferred to gate |
| 13 | `dev.ps1 smoke`: "Prometheus loads the rules and no alert is firing" | Observed at the gate |
| 14 | `dev.ps1 drill` (database, broker, provider, worker, poison, probe); records in `docs/operations/drills` | Observed at the gate |
| 15 | `RunbookTests` (section 22 template, the section 19 set, every alert's runbook exists); queries run against the drill stack at the gate | Yes (S6): a renamed heading fails the template test |
