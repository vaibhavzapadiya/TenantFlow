# M10 — Production observability

**Goal:** an operator told "orders are slow", "a payment never resolved" or "exports stopped" finds the cause from
TenantFlow's own metrics, logs and traces. Each key journey and worker workflow has a dashboard, an objective, alerts
that fire on real harm and stay quiet otherwise, and a runbook that says what to do next. Telemetry carries no tenant
ids in labels, no secrets and no personal data.

**Gate (roadmap):** key user journeys and worker workflows can be diagnosed from metrics, logs, and traces.

Spec: [SPEC-0010](../specs/0010-production-observability.md) (Implemented). Decisions:
[ADR-0029](../architecture/decisions/0029-observability-as-code-dashboards-and-alerting.md) (dashboards and alerts as
code, Prometheus rules, Alertmanager), [ADR-0030](../architecture/decisions/0030-slos-error-budgets-and-burn-rate-alerting.md)
(SLOs with provisional targets, burn-rate alerts), [ADR-0031](../architecture/decisions/0031-log-redaction.md) (log
redaction), all **Proposed**: you accept them. No open decision was due at M10.

## 1. What was built

| Slice | Commit | Delivers |
|---|---|---|
| S1 | `35e9d1a` | Telemetry guards: `LogRedactionProcessor` (third-party exception messages redacted, scope allow-list), no `url.query` on server spans, the metric label guard over the Api, Worker and Database test runs, 7-day retention in Prometheus, Loki and Tempo |
| S2 | `027fe38` | Npgsql spans and metrics; scheduled-job runs, duration and lateness; dead-timer gauge; payment and refund outcomes (after commit), reversals, unresolved payments, provider latency; reservation conflicts; support grants; RabbitMQ per-queue scrape |
| S3 | `fb18a1f` | Three provisioned, read-only dashboards: golden signals, worker, business workflows |
| S4 | `be3b37e` | Five SLO documents (two synchronous, three asynchronous) with provisional targets; error-ratio recording rules over 5m, 30m, 1h, 6h; `dev.ps1 rules` (promtool) in `verify.ps1 -Database` |
| S5 | `3ac0d7d` | 26 alerts (cause, symptom, event), 22 promtool cases, `tenantflow.health.status` from the hosts' health checks, Alertmanager with inhibition |
| S6 | `6889f3f`, `93c16d7` | Eight runbooks in the section 22 template, `dev.ps1 drill <name>`, the accepted-row review |
| Gate | `bc32ecc` and this commit | Docker probe retry; drill script fix, drill records, runbook query fixes found by the drills, this guide |

## 2. Architectural reasoning

- **Everything as files with tests.** Dashboards, recording rules, alert rules and the Alertmanager configuration are
  in the repository; Grafana cannot save UI edits. Rules have promtool unit tests that run offline from the pinned
  Prometheus image, so an alert's harm, healthy and spike cases are reviewed like code (ADR-0029).
- **Alerts on metrics only.** A condition that was only a log (refund reversed, dead timer) got a counter or gauge
  first, so one rule engine covers everything and Loki's ruler stays unused.
- **Causes inhibit symptoms.** `DatabaseDown`, `BrokerDown` and the telemetry-missing alerts are causes; while one fires,
  Alertmanager suppresses stale-outbox, job, API-error, stuck-payment and SLO alerts. Events (reversal, dead timer, dead
  letters, failed retention, cross-tenant probing) are never suppressed: each needs a person.
- **SLIs now, targets later.** SLOs are defined per journey with provisional targets and alert on burn rate, not on the
  raw target. M11's baseline replaces the numbers (ADR-0030).
- **Counting at the right moment.** Payment and refund outcomes are counted after their transaction commits, so a
  rolled-back change is never counted; event alerts treat a counter's first sample as an event (`unless offset`), since
  `increase()` cannot see it.
- **No schema change.** Unresolved payments come from the timer table (timers that ran and were rescheduled); the
  creation time is decoded from the subject's version 7 UUID in SQL, because PostgreSQL 17's `uuid_extract_timestamp`
  reads version 1 only.

## 3. Security considerations

- Label allow-list and GUID check over every TenantFlow measurement the tests record (DP-A05); dashboards and alert
  annotations are tested to name no tenant, subject or id, and to template only closed label values.
- Exported logs keep exception type and stack but not third-party messages, and only allow-listed scope keys (DP-A03).
  The local JSON console log is unchanged.
- Server spans carry no query string (DP-A25); database spans carry statement text, never parameter values (decision 6).
- 7-day retention for all three backends (DP-A12); a failed retention run pages.
- `CrossTenantProbing` pages on sustained `not_a_member` denials without carrying who or which tenant (threat models
  0001, 0006); the runbook finds them in logs and traces.
- New component: Alertmanager v0.34.1, pinned by digest, loopback port, null receiver. It is not yet in an image scan
  run (`security-scan.ps1` was not run in M10).

## 4. Verification

See SPEC-0010's Verification table. Gate evidence (2026-10-22):

- `./scripts/verify.ps1 -Database` at `93c16d7`: locked restore, format clean, Release build 0 warnings, all unit and
  host test projects pass (Api 141, Worker 16, Architecture 19, module projects), Database 328/328 with the label guard
  quiet. Its last step (promtool) failed on the flaky Docker probe; after the probe fix (`bc32ecc`) `dev.ps1 rules`
  passed (promtool check and 29 test groups, amtool).
- `./scripts/dev.ps1 smoke`: 14/14, including 7-day retention, the broker scrape, the three dashboards, rules loaded and
  no alert firing (criterion 13).
- Break-it checks: S1 redaction, S1 retention read-back, S2 Npgsql source, S3 dashboard query, S4 SLI 5xx filter, S5
  first-reversal clause, S6 runbook template; at the gate again: the `url.query` scrub (criterion 6) and the label
  guard fixture with a planted `tenant_id` (criterion 5) both fail their tests and were restored.

Drills ([record](../operations/drills/2026-10-22-m10-gate.md)): poison +21 s, probe +263 s, database about 2 minutes,
broker +80 s, provider +465 s, worker +300 s; each alert named its runbook, and the runbook's queries led to the failing
trace and its logs. The drills fixed five runbook queries and the drill script itself.

## 5. Trade-offs and assumptions

- Retention runs are `tenantflow.scheduling.runs{job="retention"}`, not a separate `retention_runs` counter.
- The unresolved-payment gauge counts only payments reconciliation already checked (after 2 minutes); younger unknown
  ones show in *Payment outcomes* as `unknown`.
- `tenantflow.health.status` runs the readiness checks every 15 s in each host (one extra `SELECT 1` and broker check).
- No server-side database metrics (lock waits, deadlocks) until M11; slow spans and the pool panel stand in.
- Event alerts can fire once after Prometheus starts with an empty TSDB while counters already exist (the
  `unless offset` clause sees no history). Prometheus data persists in its volume, so this needs a reset.
- Reviews were not run for this milestone (the task said to skip them unless you ask); the full security scan and
  `progress.ps1 -Verify` (coverage) were not run either.

## 6. Mentor exercises

1. Remove the `unless offset` clause from `RefundReversed`. Which promtool case fails, and what would production miss?
2. Make `ApiServerErrors` use `for: 5m`. Construct the one-minute spike that now pages, and explain why `for` must be
   longer than the rate window.
3. Drop the PostgreSQL 17 decoding and use `uuid_extract_timestamp`. Which test fails, and what would the gauge show?

## 7. Interview talking points

- "How do you keep alerts from paging on noise?" Multi-window burn rates for user-facing objectives, `for` longer than
  the rate window for symptoms, cause alerts that inhibit their symptoms, and unit tests for harm, healthy and spike.
- "How do you keep telemetry from leaking data?" A tested label allow-list, redaction in the exporter pipeline with a
  planted-value test, no query strings on spans, statement text without parameters.

## 8. Exit checklist

- [x] SPEC-0010 criteria 1–15 implemented with verification rows
- [x] Full gate `verify.ps1 -Database` (includes `dev.ps1 rules`)
- [x] Drills run and recorded
- [ ] **You:** accept or amend ADR-0029 to ADR-0031
- [ ] **You:** the five accepted threat rows reviewed in S6 stay accepted (recommendation)
- [ ] **You:** still from M9: ADR-0025 to ADR-0028, the 58 image exceptions (expire 2026-11-21); SC-A13 (.NET 10, due
      2026-11-10) and DP-A26 were moved from M10 to M11 at the gate so the tracker check passes; both stay yours
