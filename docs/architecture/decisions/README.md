# Architecture decision records

How and when to write one: [ADR-0001](0001-record-architecture-decisions.md).
Template: [0000-adr-template.md](0000-adr-template.md).

| ADR | Title | Status |
|-----|-------|--------|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | Accepted |
| [0002](0002-modular-monolith.md) | Start as a modular monolith with business-owned modules | Accepted |
| [0003](0003-dotnet-lts-and-dependency-policy.md) | .NET 10 LTS, pinned SDK, and dependency policy | Accepted (adopted 2026-10-24; amended by 0038) |
| [0004](0004-build-quality-gates.md) | Build quality gates (analyzers, formatting, and the CI skeleton) | Accepted |
| [0005](0005-temporary-dotnet-9-toolchain.md) | Temporary .NET 9 toolchain until the .NET 10 SDK is installed | Superseded by 0003 (2026-10-24) |
| [0006](0006-claude-code-setup-and-spec-driven-workflow.md) | Project-specific Claude Code setup and a two-path, spec-driven workflow | Accepted |
| [0007](0007-logging-pipeline.md) | Logging through Microsoft.Extensions.Logging and OpenTelemetry | Accepted |
| [0008](0008-local-configuration-and-secrets.md) | Local configuration and secrets | Accepted |
| [0009](0009-identity-provider-and-oidc-boundary.md) | Identity provider and the OIDC boundary | Accepted |
| [0010](0010-trust-boundaries-between-user-types.md) | Trust boundaries between user types | Accepted |
| [0011](0011-postgresql-row-level-security.md) | PostgreSQL row-level security as defence in depth | Accepted |
| [0012](0012-module-data-ownership.md) | Module data ownership in PostgreSQL | Accepted |
| [0013](0013-use-case-dispatch.md) | Use-case dispatch through direct application services | Accepted |
| [0014](0014-input-validation.md) | Input validation with FluentValidation in the Application layer | Accepted |
| [0015](0015-api-versioning.md) | API versioning with a URL segment | Accepted |
| [0016](0016-error-model-and-problem-details-contract.md) | Error model and the Problem Details contract | Accepted |
| [0017](0017-http-pipeline-host-composition-and-configuration.md) | HTTP pipeline, host composition, and configuration policy | Accepted |
| [0018](0018-serialized-tenant-changes-with-advisory-locks.md) | Serialized per-tenant changes with PostgreSQL advisory locks | Accepted |
| [0019](0019-m3-conventions.md) | M3 conventions: RLS wiring, database test infrastructure, pagination, OpenAPI, and API compatibility | Accepted |
| [0020](0020-orders-and-reservations.md) | Order state machine, stock reservations, and the Orders-to-Inventory flow | Accepted |
| [0021](0021-messaging-broker-outbox-and-consumers.md) | Messaging with RabbitMQ, a per-module outbox, and idempotent consumers | Accepted |
| [0022](0022-payments-provider-port-and-order-payment-saga.md) | Payments module, provider port with a simulator and Stripe test mode, and the order payment saga | Accepted |
| [0023](0023-subscriptions-renewal-scheduler-and-refunds.md) | Subscriptions, the persistent scheduler for renewals, and refund workflows | Accepted |
| [0024](0024-object-storage-and-async-import-export-jobs.md) | Object storage port with a filesystem adapter, and asynchronous import and export jobs | Accepted |
| [0025](0025-compliance-goals-data-classification-and-retention.md) | Compliance goals, data classification, and retention | Proposed |
| [0026](0026-audit-module-and-append-only-audit-log.md) | Audit module and the append-only audit log | Proposed |
| [0027](0027-platform-admin-and-support-operator-boundary.md) | Platform-administrator and support-operator boundary | Proposed |
| [0028](0028-security-scanning-in-the-pipeline.md) | Secret, dependency, container, and configuration scanning in the pipeline | Proposed |
| [0029](0029-observability-as-code-dashboards-and-alerting.md) | Dashboards and alert rules as code, with Prometheus rules and Alertmanager | Proposed |
| [0030](0030-slos-error-budgets-and-burn-rate-alerting.md) | Service level objectives per journey, with provisional targets and burn-rate alerts | Proposed |
| [0031](0031-log-redaction.md) | Log redaction for exception messages and scopes | Proposed |
| [0032](0032-aws-and-managed-container-hosting.md) | AWS with ECS Fargate, RDS for PostgreSQL and Amazon MQ for RabbitMQ | Proposed |
| [0033](0033-terraform-layout-remote-state-and-secrets.md) | Terraform layout, remote state, and secrets kept out of state | Proposed |
| [0034](0034-deployed-environments-configuration-parity-and-budget.md) | Deployed environments, configuration parity, and the budget | Proposed |
| [0035](0035-production-identity-provider-hosted-keycloak.md) | Production identity provider: hosted Keycloak with operator MFA | Proposed |
| [0036](0036-local-environment-tooling-compose-and-a-csharp-setup-tool.md) | Local environment tooling: Compose, small scripts and a C# setup tool, with Aspire deferred | Accepted (superseded by 0037 once accepted) |
| [0037](0037-local-setup-without-a-setup-tool.md) | No host scripts: Compose and dotnet run the local environment and its checks | Proposed (amends 0008, 0028) |
| [0038](0038-test-runner-on-the-dotnet-10-sdk.md) | Tests keep running through VSTest on the .NET 10 SDK | Proposed (amends 0003) |
