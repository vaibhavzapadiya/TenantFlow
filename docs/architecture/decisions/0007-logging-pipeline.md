# ADR-0007: Logging through Microsoft.Extensions.Logging and OpenTelemetry

- Status: Accepted
- Date: 2026-10-19
- Owners: TenantFlow tech lead (decision approved by the user on 2026-10-19)
- Related decisions: ADR-0002, ADR-0004
- Closes: Open Decision 13

## Context
The blueprint requires structured logging, OpenTelemetry, and Loki for logs, with logs, metrics, and
traces correlated through trace context (sections 16 and 26). Both the API and worker hosts need the
same pipeline. Two common .NET approaches exist: the built-in `Microsoft.Extensions.Logging` (MEL)
abstraction exported through OpenTelemetry, or Serilog with its own sinks and enrichers.

## Decision
- Application code logs through MEL `ILogger<T>` only, using compile-time `LoggerMessage` source
  generation for hot or frequently used messages.
- The OpenTelemetry logging provider exports log records over OTLP to the OpenTelemetry Collector,
  which forwards them to Loki. Trace and span IDs are attached by the provider when a span is active.
- Locally, the JSON console formatter with scopes enabled writes the same records to stdout, so
  logs stay readable without the observability stack running.
- The pipeline setup lives in `TenantFlow.BuildingBlocks.Observability` and is shared by all hosts.

Out of scope: log redaction and data classification tooling (M9), dashboards and alerts (M10).

## Options considered
- **MEL + OpenTelemetry (chosen).** One logging abstraction, first-party support, and automatic
  log-to-trace correlation. Exporter choice stays in configuration, so a managed backend can replace
  Loki later. Fewer sinks and enrichers than Serilog, and less mature message templating in older tooling.
- **Serilog.** Mature structured logging with rich sinks, enrichers, and request logging. It runs a
  second pipeline next to OpenTelemetry, adds packages and configuration, and trace correlation has to be
  wired explicitly.

## Consequences
- Easier: one place to configure all telemetry, and trace IDs in every log written inside a span.
- Harder: some Serilog conveniences (for example destructuring policies) need MEL equivalents.
- Follow-up: M9 adds redaction for sensitive fields; M10 builds Loki queries and alerts on these logs.

## Validation
- An automated test checks that a log written while handling a request carries the request span's trace ID.
- The M1 smoke check finds API and worker logs in Loki with trace IDs that resolve to traces in Tempo.

## Amendment 1: readable console in Development

- Status: Accepted (2026-10-23, approved by the user).
- Context: the JSON console, with Debug records from health checks and Quartz every few seconds, proved hard to read
  in the hosts' console windows, which is the reason the local console log exists.
- Change: in the `Development` environment the console uses the built-in simple formatter, one line per record
  (UTC time, level, category, message), without scopes. Every other environment, Compose and the tests included,
  keeps the JSON formatter with scopes. Development also logs `Microsoft.Extensions.Diagnostics.HealthChecks` at
  Warning and, in the worker, `Quartz` at Information.
- Consequences: trace and span IDs no longer appear on a developer's console; they still travel with every record
  over OTLP, so the Grafana log-to-trace links are unchanged. No new dependency.

## Revisit triggers
A required capability (for example a sink, redaction feature, or performance characteristic) is
missing from MEL and OpenTelemetry, with evidence from a real need.
