# Threat model: data protection and telemetry (personal data, logs, traces, metrics, errors, retention)

- Status: Draft (M9), written with SPEC-0009
- Date: 2026-10-22
- Owner: TenantFlow tech lead (the user)
- Scope: personal and sensitive data across every module (OIDC issuer and subject, membership ids, payment-method
  tokens, free-text reasons, stored files); telemetry from the API and Worker through the OpenTelemetry Collector to
  Loki, Tempo and Prometheus, read in Grafana (`deploy/compose/config`); Problem Details error responses; response
  security headers; retention, backups, erasure and offboarding. Tenant isolation itself is threat model 0001.
- Related: blueprint sections 2.8, 13, 16; ADR-0007, ADR-0016, ADR-0017, ADR-0025 (Proposed), ADR-0026 (Proposed);
  [SPEC-0009](../specs/0009-security-and-privacy.md) criteria 2, 7 to 12; engineering conventions sections 6, 8, 13;
  [threat model 0001](0001-tenant-boundary.md)

## Assets and data classification

Classes follow ADR-0025 (`public-product`, `tenant-confidential`, `personal-pseudonymous`, `security-evidence`).
TenantFlow stores no names, emails, passwords or IP addresses (ADR-0009, ADR-0025).

| Asset | Where | Class | Why it matters |
|---|---|---|---|
| OIDC issuer and subject | `memberships`; `actor_issuer`/`actor_subject` in `membership_audit` and `tenant_audit` | personal-pseudonymous | Links a TenantFlow record to a person at the identity provider |
| Membership ids | orders, payments, refunds, subscriptions, jobs and other tables; outbox payloads; broker messages; URL paths | personal-pseudonymous | Pseudonymous until joined with the membership row |
| Payment-method tokens | `payments.payment_method`, `subscriptions.payment_method` | tenant-confidential | A provider token, never a card number (PCI scope must stay zero) |
| Free-text reasons | refund `reason`, order `cancellation_reason`, stock adjustment `reason` | personal-pseudonymous | A user can type personal data into them |
| Idempotency response bodies | `idempotency_keys.response_body` | tenant-confidential | Replayed responses of payment and refund calls |
| Uploaded and exported files | storage root, `ImportsExports` file rows | tenant-confidential | Whole catalogs; an upload can hold anything a user put in a CSV |
| Logs, traces, metrics | Loki, Tempo, Prometheus volumes; JSON console | Internal | Can leak tokens, subjects, SQL, bodies or tenant data if careless |
| Error responses | every non-2xx answer | Public to the caller | Can leak internals or echo input back |

## Actors and trust boundaries

Actors: anonymous caller; tenant member, administrator and customer; platform administrator; support operator
(SPEC-0009 S7); Worker; developer or operator with access to the database, broker, telemetry stack or file storage.

| Boundary | Crossed by | Control |
|---|---|---|
| Caller to API | requests and error responses | Problem Details contract, security headers, input validation |
| Hosts to Collector (OTLP) | log records, spans, metrics | Only what the code emits; the Collector adds no processors that redact |
| Telemetry stack to a person | Grafana, Loki, Tempo, Prometheus HTTP | Loopback-only ports and Grafana login (local); deployed access is M12 |
| Modules to broker | outbox payloads with membership ids | Loopback-only broker locally; payloads hold ids, not subjects |
| Application to storage | files under the storage root | Tenant-named keys, 7-day expiry; file modes are SPEC-0009 criterion 12 |
| Live data to time | rows that outlive their purpose | Retention schedule (SPEC-0009 criteria 7 to 10) |

## Entry points and data flows

1. A request carries a bearer token, route ids, query strings and a body. ASP.NET Core instrumentation records a
   span (health probes excluded); log records inside it carry its trace id. The `traceId` is returned in Problem Details.
2. `ILogger<T>` writes JSON to the console and OTLP to the Collector, which batches and forwards to Loki. Spans go to
   Tempo and metrics to Prometheus. Grafana reads all three and links traces to logs by trace id.
3. Entity Framework logs SQL commands under `Microsoft.EntityFrameworkCore.Database.Command`.
4. Unhandled exceptions are logged with the exception by the exception handler and by the RabbitMQ consumer.
5. Domain events leave through the outbox to RabbitMQ; failed messages go to retry and dead-letter queues.
6. Imports upload a CSV; exports write a file that is downloaded later.
7. Rows stay in PostgreSQL until a retention rule removes them; today only published outbox rows and files expire.

## Abuse cases / attack paths

| Id | Attack or failure | Goal or harm | Path |
|---|---|---|---|
| DP-01 | Token, `Authorization` header, credential or body written to a log or span | Replay a token; read personal data from telemetry | Careless log call, scope, or exception message in a log |
| DP-02 | Tenant id, subject or free text used as a metric label | Unbounded series take Prometheus down; identifiers leak to dashboards | A new counter tags caller input |
| DP-03 | SQL text or parameter values in production logs | Reveal schema, filters or row values | EF command logging above `Warning` in a deployed host |
| DP-04 | Error response echoes input, an exception message, a stack trace or SQL | Reconnaissance; reflected data | Development-only detail, or a handler that copies `exception.Message` |
| DP-05 | A card number submitted as a payment-method token | Bring card data into scope; store or echo it | `paymentMethod` field on payment or subscription calls |
| DP-06 | Import row errors copy cell values | Personal or confidential values persisted in error rows | Row-level error reporting |
| DP-07 | Unauthorised reading of the telemetry stack | Read logs, traces and metrics of every tenant | Grafana without login, or a port bound to a public interface |
| DP-08 | Telemetry and rows kept forever | Data outlives its purpose; a breach exposes more | No retention configured in Loki, Tempo, Prometheus; no purge tasks |
| DP-09 | A removed member's issuer and subject kept forever | Personal data kept after the relationship ended | `memberships` rows are never pseudonymised today |
| DP-10 | Actor subjects in `membership_audit` and `tenant_audit` kept forever | Same as DP-09, for administrators and operators | SPEC-0009 criterion 9 covers `memberships` only |
| DP-11 | Personal data typed into a free-text reason | Personal data held for the life of the tenant | Refund, cancellation and stock adjustment reasons |
| DP-12 | Exported or uploaded files read after they are needed | Bulk tenant data exposure from storage | Files left on disk; loose file modes; orphaned `.tmp` files |
| DP-13 | Dead-lettered messages kept indefinitely | Membership ids and payloads outlive retention | Dead-letter queues have no expiry |
| DP-14 | Backup restores deleted or pseudonymised data | Retention undone by a restore | No backup policy exists yet |
| DP-15 | Erasure request or tenant leaving cannot be served | Data kept with no lawful purpose | No erasure operation, no offboarding |
| DP-16 | A response is cached or sniffed by a browser or shared cache | Tenant data stored outside TenantFlow; content rendered as HTML | Missing response headers |
| DP-17 | Search terms or cursors in a query string stored in spans | Caller text in Tempo | `url.query` on request spans |
| DP-18 | Compliance overclaim | Users rely on a certification that does not exist | Documentation claims SOC 2, ISO 27001, PCI DSS or GDPR compliance |

## Existing controls

- Denials are counted in `tenantflow.auth.denials` with only `scheme` and `reason`, and logged without the token or the
  `Authorization` header (`AuthDenialRecorder`; `AuthDenialTelemetryTests`). JWT error details are off
  (`IncludeErrorDetails = false`), so a 401 reveals nothing (`AuthenticationTests`).
- A host that refuses to start names the configuration key, never its value (`ConfigurationValidationTests`).
- Metric labels in code are closed sets: `scheme`, `reason`, `consumer`, `outcome`, `kind`, `module`. No tenant id or
  subject is a label (verified by reading every `CreateCounter`/`CreateObservableGauge` call).
- The API sets `Microsoft.EntityFrameworkCore` to `Warning` in `appsettings.json`; Development raises EF commands to
  `Information`. The Migrator turns EF command logging off. No `EnableSensitiveDataLogging` exists in `src/`.
- Every failure is Problem Details with fixed titles; a fault is a generic 500 `server.unexpected` with a trace id, the
  same in Development and Production, and malformed input is never echoed (`ErrorContractTests`).
- Every response, including 405 and 500, carries `nosniff`, `Cache-Control: no-store`, `Referrer-Policy: no-referrer`
  and `default-src 'none'; frame-ancestors 'none'` (`SecurityHeadersMiddleware`; `HttpPipelineTests`).
- The RabbitMQ consumer marks failed spans and dead-letter reasons with the exception type only, not its message.
- Card-shaped payment-method values are refused, never echoed and never stored (`CreatePaymentCommandValidatorTests`,
  `PaymentEndpointTests`, `SubscriptionEndpointTests`). Webhook events store ids and an outcome, not the raw payload.
- Import row errors are reported without the cell values (`ImportJobTests`).
- Published outbox rows and stored files are deleted after 7 days (`OutboxPublisherTests`, `FileLifecycleTests`).
- Local telemetry: every Compose port binds to `127.0.0.1`; Grafana has anonymous access off, an admin password from
  the environment, analytics and plugin downloads off; the smoke tests (`tests/Smoke`) check that Grafana answers 401
  anonymously. The Collector bounds its memory.

## Gaps and residual risk

- **Worker EF logging.** The Worker's `appsettings.json` does not lower `Microsoft.EntityFrameworkCore`, so EF command
  text is logged at `Information` in Production. Parameter values are not logged (no sensitive-data logging), but the
  convention in section 13 is not met. No test checks either host's EF log level.
- **Fault logs carry exceptions.** Faults are logged with the exception, so its message and stack trace reach Loki.
  ADR-0007 deferred redaction to M9; the owner moved it to M10 (SPEC-0009 decision 16).
- **No metric-label guard.** Labels are closed by review only; a new counter could tag a tenant id.
- **Query strings in spans.** The instrumentation's default query redaction is relied on without a test.
- **Telemetry retention.** ADR-0025 says local telemetry is kept 7 days and configured in Compose; the Compose files set
  no retention for Loki, Tempo or Prometheus, so image defaults apply. Deployed retention and access are M10 and M12.
- **Rows kept forever** (processed messages, webhook events, idempotency keys with response bodies, timers, job rows,
  audit rows, removed memberships) until SPEC-0009 criteria 7 to 10 land.
- **Old audit tables.** `membership_audit` and `tenant_audit` keep actor subjects; criterion 9 pseudonymises
  `memberships` only, and Decision 3 keeps both tables in use.
- Free-text reasons, dead-letter queues, backups, erasure and offboarding: see the accepted rows below.

## Mitigations with owner and due date

| Id | Abuse case | Mitigation | Owner | Due | Status | Evidence |
|---|---|---|---|---|---|---|
| DP-A01 | DP-01 | Denial logs and 401 answers carry no token, header or token detail | Claude | M2 | done | `AuthDenialTelemetryTests.Logs_describe_the_denial_but_never_carry_the_token_or_the_authorization_header`, `AuthenticationTests.Invalid_token_is_rejected_with_401_and_reveals_nothing` |
| DP-A02 | DP-01 | Startup validation names the configuration key, never the value | Claude | M6 | done | `ConfigurationValidationTests.Host_refuses_to_start_without_the_connection_string_and_names_only_the_key`, `ConfigurationValidationTests.Host_refuses_to_start_with_a_stripe_key_that_is_not_a_test_key_and_names_only_the_key` |
| DP-A03 | DP-01 | Decide on log redaction for exception messages and scopes (ADR-0007 follow-up), with a test that a planted sensitive value never reaches a log record | user | M10 | done | ADR-0031 (Proposed); `TelemetryRedactionTests.A_planted_value_in_a_third_party_exception_or_an_unlisted_scope_is_not_exported` |
| DP-A04 | DP-02 | Metric labels are closed sets: scheme and reason, kind and outcome, module | Claude | M8 | done | `AuthDenialTelemetryTests.A_missing_token_is_counted_once_with_the_scheme_and_reason_only`, `FileLifecycleTests.The_metrics_task_reports_due_jobs_per_kind_and_the_runners_count_each_job_that_ends`, `OutboxPublisherTests.Backlog_metrics_report_the_pending_count_and_oldest_age_per_module_and_drop_to_zero_when_published` |
| DP-A05 | DP-02 | A test that fails when any TenantFlow meter emits a tag outside an allow-list | Claude | M10 | done | `MeterTagGuardTests.An_unlisted_tag_key_and_a_guid_tag_value_are_reported`; the guard runs as an assembly fixture of the Api, Worker and Database tests |
| DP-A06 | DP-03 | Set `Microsoft.EntityFrameworkCore` to `Warning` in the Worker and test the effective level in both hosts | Claude | M9 | done | `WorkerHostTests.Outside_development_entity_framework_core_logs_warnings_and_above_only`, `ConfigurationFilesTests.Outside_development_entity_framework_core_logs_warnings_and_above_only` |
| DP-A07 | DP-04 | Generic 500 with a trace id, no echo of input, same answer in every environment | Claude | M2 | done | `ErrorContractTests.Unexpected_fault_is_a_generic_500_that_leaks_nothing_and_is_logged_as_an_error`, `ErrorContractTests.Malformed_or_empty_body_is_400_request_malformed_and_never_echoes_the_input`, `ErrorContractTests.Malformed_body_gets_the_same_answer_in_Development_as_in_Production` |
| DP-A08 | DP-16 | Baseline security headers on every response, faults included | Claude | M2 | done | `HttpPipelineTests.Every_response_carries_the_baseline_security_headers`, `HttpPipelineTests.An_unexpected_fault_still_carries_the_baseline_security_headers` |
| DP-A09 | DP-05 | Card-shaped payment-method values are refused, never echoed or stored | Claude | M7 | done | `CreatePaymentCommandValidatorTests.Card_numbers_and_malformed_keys_are_refused_with_their_codes`, `PaymentEndpointTests.A_card_number_is_refused_never_echoed_and_nothing_is_stored`, `SubscriptionEndpointTests.A_card_number_is_refused_never_echoed_and_nothing_is_stored` |
| DP-A10 | DP-06 | Import row errors hold a row number and a code, not the values | Claude | M8 | done | `ImportJobTests.An_import_creates_and_updates_products_by_sku_and_reports_bad_rows_without_their_values` |
| DP-A11 | DP-07 | Telemetry stack is local only: loopback ports, Grafana login, anonymous access off and smoke-checked | user | M12 | accepted | Local development stack with synthetic data only; deployed telemetry access control is designed with M12 |
| DP-A12 | DP-08 | Configure 7-day retention in Loki, Tempo and Prometheus as ADR-0025 states, and the deployed values | Claude | M10 | done | Local values: `ObservabilityConfigTests.Loki_tempo_and_prometheus_are_configured_to_keep_7_days`, read back from the running stack by the smoke tests (tests/Smoke); deployed values with M12 |
| DP-A13 | DP-08 | Every table has a class, purpose and retention rule; a database test fails on a missing table | Claude | M9 | done | `PrivacyDocumentTests.Every_table_in_every_module_schema_has_a_class_a_purpose_and_a_retention_rule` |
| DP-A14 | DP-08 | Per-tenant retention tasks delete expired rows in bounded batches | Claude | M9 | done | `RetentionTests.Expired_rows_go_a_row_one_second_inside_its_window_stays_and_another_tenant_waits_for_its_own_run`, `RetentionTests.More_expired_rows_than_one_batch_are_all_deleted_in_bounded_batches` |
| DP-A15 | DP-09 | Pseudonymise issuer and subject of memberships removed more than 30 days ago | Claude | M9 | done | `RetentionTests.A_membership_removed_thirty_days_ago_loses_its_identity_keeps_its_row_and_the_person_can_return` |
| DP-A16 | DP-08 | Audit records deleted after 400 days only through `audit.purge_expired()` | Claude | M9 | done | `RetentionTests.Audit_rows_older_than_four_hundred_days_leave_through_their_purge_functions_and_younger_ones_stay`, `AuditLogTests.The_application_role_can_only_add_and_read_audit_records_and_no_role_can_change_one` |
| DP-A17 | DP-10 | Delete `membership_audit` and `tenant_audit` rows older than the audit retention through a purge function in each schema (owner decision, 2026-10-22) | Claude | M9 | done | `RetentionTests.Audit_rows_older_than_four_hundred_days_leave_through_their_purge_functions_and_younger_ones_stay` |
| DP-A18 | DP-01 | The new audit log stores no token, name, email, tenant-user subject or free text | Claude | M9 | done | `AuditLogTests.Each_platform_and_member_change_writes_one_audit_event_naming_its_actor_by_reference_only`, `AuditLogTests.An_event_that_can_never_be_recorded_is_refused_for_the_dead_letter_queue_and_records_nothing` |
| DP-A19 | DP-12 | Published outbox rows and stored files are deleted after 7 days | Claude | M8 | done | `OutboxPublisherTests.Cleanup_deletes_only_rows_published_longer_ago_than_the_retention`, `FileLifecycleTests.An_exports_file_and_a_finished_imports_upload_are_deleted_seven_days_later_and_not_before` |
| DP-A20 | DP-12 | Files and directories created `0600`/`0700`; stale `.tmp` files swept hourly | Claude | M9 | done | `StorageTests.The_sweep_deletes_only_temporary_files_left_for_more_than_an_hour`, `StorageTests.On_unix_stored_files_are_readable_by_their_owner_only` |
| DP-A21 | DP-11 | Free-text reasons stay bounded in length and tenant-scoped, kept for the life of the tenant | user | M13 | accepted | Short bounded fields under forced RLS; no content scanning; reviewed with tenant offboarding |
| DP-A22 | DP-13 | Dead-letter queues keep payloads until handled by an operator | user | M10 | accepted | Payloads carry ids, not subjects; local broker only. Reviewed in M10-S6 (2026-10-22): the queue runbook sets handling (decide per reason, record ids); no expiry is set, revisit with the deployed broker in M12; stays accepted (recommendation) |
| DP-A23 | DP-14 | Backup policy with expiry that honours retention and pseudonymisation | user | M13 | accepted | No backups exist yet; policy is due with M13 disaster recovery |
| DP-A24 | DP-15 | On-request erasure and tenant offboarding | user | M13 | accepted | Deferred by SPEC-0009 Decision 6; time-based pseudonymisation only until then |
| DP-A25 | DP-17 | A test that request spans carry no query-string values | Claude | M10 | done | `TelemetryRedactionTests.A_query_string_value_or_key_never_reaches_the_server_span` |
| DP-A26 | DP-18 | No certification claim; evidence only (ADR-0025 stance) | user | M12 | planned | Your acceptance of ADR-0025 (closes Open Decision 8); moved from the M9 gate, the M10 gate, and from M11 to M12 because M12 runs first, 2026-10-22 |

## Security tests and monitoring

- Tests: `AuthDenialTelemetryTests`, `AuthenticationTests`, `ConfigurationValidationTests`, `ErrorContractTests`,
  `HttpPipelineTests`, `TelemetryTests` and `WorkerTelemetryTests` (trace correlation), `CreatePaymentCommandValidatorTests`,
  `PaymentEndpointTests`, `SubscriptionEndpointTests`, `ImportJobTests`, `OutboxPublisherTests`, `FileLifecycleTests`.
  SPEC-0009 adds the table-completeness, retention, pseudonymisation and audit-retention database tests.
- Smoke: the smoke tests (`tests/Smoke`) check Grafana refuses anonymous access and that traces and logs correlate.
- Monitoring today: `tenantflow.auth.denials`, outbox backlog gauges, job counters. M10 adds alerts on retention task
  failures and dead-letter growth (DP-A03, DP-A05, DP-A22).

## Review date and change triggers

Review by the end of M10, and again at M12 and M13. Review earlier when: a new table, metric, log message with caller
data, or free-text field is added; a new telemetry backend or exporter is configured; telemetry leaves the developer
machine; a real tenant or regulator sets retention; backups are introduced; the identity provider sends account
deletion events; a request for erasure or offboarding arrives; ADR-0025 or ADR-0026 changes status.
