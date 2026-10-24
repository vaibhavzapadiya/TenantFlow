# ADR-0014: Input validation with FluentValidation in the Application layer

- Status: Accepted (2026-10-21 by the user)
- Date: 2026-10-19
- Owners: TenantFlow tech lead (option chosen by the user on 2026-10-19)
- Related decisions: ADR-0003, ADR-0005, ADR-0013
- Closes: Open Decision 17 (triggered by the first write endpoint, M2-S4)

## Context
The first write endpoint (`POST /platform/tenants`) needs input validation with a consistent error
contract. Open Decision 17 compared FluentValidation with built-in minimal API validation. The
built-in option (`AddValidation`) arrives with .NET 10. When this was decided the build ran on the .NET 9 SDK
(ADR-0005), so it could not be used or evaluated. The build moved to .NET 10 on 2026-10-24 (SPEC-0015), so
`AddValidation` can now be evaluated; this ADR's choice of FluentValidation stands until it is.

## Decision
- Request rules are written as **FluentValidation validators for Application commands** (for example
  `CreateTenantCommandValidator`) in the module's Application project, next to the use case.
- The application service validates before doing any work (ADR-0013), so every caller is validated,
  not only HTTP.
- Validators are registered explicitly per module; no assembly scanning package.
- A validation failure becomes **400 Problem Details** (`ValidationProblemDetails`) with errors keyed by
  property name, mapped in one place in the API host. Messages describe the rule, never echo input.
  **Amended by ADR-0016:** the body is Problem Details with `code: request.validation_failed` and an `errors`
  array of `{ field, code, message }`; every rule sets an error code from its module's catalog.
- **Invariants stay in the domain.** Validators check request shape (required, length, format); domain
  methods enforce state rules (for example legal status transitions) and are not duplicated in validators.
- **A rule that is also an invariant is written once, in the domain** (pre-S5 review 2.4). The tenant name
  rule is `Tenant.CheckName`: `Tenant.Create` enforces it, so no caller can create an invalid tenant, and the
  validator only reports the same answer as a field error. A test feeds both the same inputs and fails if
  they ever disagree. A value object is introduced only when a value gains behaviour beyond its rule.
- **Input limits are part of validation.** Request bodies are limited to 64 KiB by default (413
  `request.payload_too_large`); identity claims are limited to the size of the columns that store them;
  text that will be shown to people rejects control and text-direction override characters.
- **A route id that cannot name a resource answers like an unknown resource** (404), never 400 and never 401.

## Options considered
- **FluentValidation now (chosen).** Mature, expressive, testable, works on .NET 9 and .NET 10.
  Adds a third-party dependency (14-day cooldown applies: 12.1.1 adopted, 12.2.0 too new).
- **Built-in minimal API validation.** No dependency and attribute-based, but .NET 10 only, and it ties
  validation to the HTTP edge rather than the use case.
- **Hand-written checks in the domain or endpoint.** No dependency, but rules and error shapes drift.

## Consequences
- Easier: one error contract for all write endpoints; rules unit-testable without a host.
- Harder: two places for rules (validators for shape, domain for invariants) need the boundary above.
- Follow-up: compare with .NET 10 built-in validation during the .NET 10 move; this ADR may be superseded.

## Validation
- API tests: invalid input returns 400 `application/problem+json` with property-keyed errors and
  writes nothing.

## Revisit triggers
The .NET 10 move (built-in validation becomes available); FluentValidation licensing or maintenance
changes; validation needs that cross module boundaries.
