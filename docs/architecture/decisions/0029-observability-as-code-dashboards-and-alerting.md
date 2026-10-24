# ADR-0029: Dashboards and alert rules as code, with Prometheus rules and Alertmanager

- Status: Proposed
- Date: 2026-10-22
- Owners: TenantFlow tech lead (option chosen by the user on 2026-10-22; acceptance pending)
- Related decisions: ADR-0007, ADR-0021, ADR-0025, ADR-0030; SPEC-0010 decisions 1 and 7

## Context
Blueprint section 16 names Grafana for dashboards and alerting and requires alerts for the critical workflows
(section 21). Today Grafana has provisioned data sources only; there are no dashboards, no alert rules, and nothing
to deliver an alert. Threat models 0003, 0004 and 0008 have planned M10 actions that are alerts (PY-A25, MW-A07).
SPEC-0010 criterion 10 requires every rule to be tested: it fires on its harm case and stays quiet on a healthy one.
M12 may replace the local backends with managed services, so rules should survive that move.

## Decision
- Dashboards are JSON files provisioned into Grafana from `deploy/compose/config/grafana/dashboards`, read-only in the UI.
- Alert and recording rules are Prometheus rule files in `deploy/compose/config/prometheus/rules`, evaluated by
  Prometheus. Each rule file has a `promtool test rules` file beside it; the test runs from the `prom/prometheus`
  image already pinned in Compose, under `verify.ps1 -Database` (it needs Docker, like the database tests).
- Alerts are metric-based only. An event that is a log today (refund reversal, dead timer) gets a counter, so one
  rule engine covers everything and Loki ruler stays unused.
- Alertmanager joins the observability profile (pinned by digest, loopback port), with a null receiver locally,
  grouping by dependency and inhibition of symptom alerts while a cause alert fires.
- Every alert carries `severity` (`page` or `ticket`) and a `runbook` annotation with a repository path.

Out of scope: deployed alert routing and on-call (M12), SLO targets (ADR-0030).

## Options considered
- **Prometheus rules, promtool tests, Alertmanager (chosen).** Rules are plain files with a first-party unit-test tool;
  portable to managed Prometheus-compatible services. Costs one more container and a second place (Alertmanager) to
  configure routing. Alertmanager keeps only silences and notification state, which may be lost locally.
- **Grafana-managed alerting provisioned from files.** No new container, and alerts can query Loki and Tempo too. No
  offline unit-test tool for rules, so criterion 10 would need a running stack and timing-dependent tests; provisioned
  Grafana alerts are tied to Grafana.
- **Both.** Most flexibility, two engines to learn and to keep consistent. Not earned yet.

## Consequences
- Easier: alert logic is reviewed in diffs and proven by fast tests; dashboards survive a Grafana volume reset.
- Harder: log-only conditions need a metric first; Alertmanager is a new component to pin, update and scan (ADR-0028).
- Follow-up: M12 chooses a real receiver and adapts rules to the managed backend.

## Validation
- `promtool check rules` and `promtool test rules` pass for every rule file; a rule without a test fails the check.
- The M10 fault drills show each expected alert firing in Alertmanager and no alert firing on a healthy stack.

## Revisit triggers
A required alert cannot be expressed on metrics; the M12 platform offers only Grafana-managed alerting; Alertmanager
operation costs more than the tests save.
