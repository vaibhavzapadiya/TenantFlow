# Service level objectives

One document per journey (ADR-0030, SPEC-0010 criterion 9), each with the blueprint section 16 fields. Synchronous API
objectives and asynchronous workflow objectives are kept apart: a request accepted into a queue is not a completed
workflow. Every target is **provisional** until the M11 baseline replaces it.

| SLO | Kind | Target | Latency bound |
|---|---|---|---|
| [Catalog read](catalog-read.md) | Synchronous (API) | 99.5% | 500 ms |
| [Order submission](order-submission.md) | Synchronous (API) | 99.0% | 1 s |
| [Payment outcome resolution](payment-resolution.md) | Asynchronous (workflow) | 99.0% | the reconciliation window |
| [Export completion](export-completion.md) | Asynchronous (workflow) | 99.0% | 250 s |
| [Event delivery](event-delivery.md) | Asynchronous (worker) | 99.9% | none |

The SLIs are Prometheus recording rules (`deploy/observability/prometheus/rules/slo.rules.yml`), tested with
`promtool test rules` (`slo.rules.test.yml`). Alerts use burn rates on them; see `alerts.rules.yml`.
