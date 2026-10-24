# Feature specs

A spec says **what** a feature must do and how that will be proven. An ADR says **why** a decision
was made. Specs link to ADRs and never repeat them.

## When a spec is required
Only for work that is large or hard to reverse: more than a few slices, or it needs an ADR
([`AGENTS.md`](../../AGENTS.md), "How much process"). Riskier work that is still small — tenant
isolation, money, a schema, a public contract, messaging — goes through `/slice` and its checklist
without a spec. Bug fixes and small to medium changes need neither.

## Lifecycle
1. **Draft:** written from [`0000-spec-template.md`](0000-spec-template.md), with open questions listed.
2. **Approved:** the owner has reviewed it and no open question is blocking. Implementation starts only now.
3. **Implemented:** every acceptance criterion maps to a passing test in the Verification table.

After implementation the tests are the living specification. The document changes only when behaviour
changes, and then in the same pull request as the code.

## Index

| Spec | Title | Milestone | Status |
|------|-------|-----------|--------|
| [0001](0001-local-platform.md) | Local platform and host foundation | M1 | Implemented |
| [0002](0002-identity-and-tenant-boundary.md) | Identity and tenant boundary | M2 | Implemented |
| [0003](0003-catalog-and-inventory.md) | Catalog and inventory | M3 | Implemented |
| [0004](0004-orders-and-reservations.md) | Orders and inventory reservations | M4 | Implemented |
| [0005](0005-messaging-foundation.md) | Messaging foundation | M5 | Implemented |
| [0006](0006-payment-workflow.md) | Payment workflow | M6 | Implemented |
| [0007](0007-subscriptions-and-refunds.md) | Subscriptions and refunds | M7 | Implemented |
| [0008](0008-async-files-and-reporting.md) | Async files and reporting | M8 | Implemented |
| [0009](0009-security-and-privacy.md) | Security and privacy | M9 | Implemented |
| [0010](0010-production-observability.md) | Production observability | M10 | Implemented |
| [0012](0012-cloud-deployment.md) | Cloud deployment | M12 | Approved (amendment 1, S3c storage: approved 2026-10-23) |
| [0013](0013-local-environment-tooling.md) | Local environment tooling | none (after M12) | Implemented |
| [0014](0014-api-reference-sign-in.md) | Sign-in from the local API reference | none (local tooling) | Implemented |
| [0015](0015-dotnet-10-toolchain-migration.md) | .NET 10 toolchain migration | none (toolchain) | Approved (2026-10-24) |
