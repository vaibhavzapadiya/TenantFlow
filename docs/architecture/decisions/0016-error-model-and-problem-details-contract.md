# ADR-0016: Error model and the Problem Details contract

- Status: Accepted (2026-10-21 by the user)
- Date: 2026-10-19
- Owners: TenantFlow tech lead (options chosen by the user on 2026-10-19)
- Related decisions: ADR-0002, ADR-0010, ADR-0013, ADR-0014, ADR-0015

## Context
Before this decision the API's failures had no common shape. Probing it showed: a malformed body was a
400 with no body in Production but a 500 in Development; a 404 had an empty body; the only identifiers in
an error were the HTTP status and the framework's default `type` link; two mechanisms handled exceptions,
one of which each route group had to remember to add; and a wrong method or content type answered 401 or
403 because the deny-by-default policy also guarded the framework's own 405/415 endpoints. Clients would
have had to parse messages, which they must never do, and the blueprint requires consistent errors that
never expose internals (section 9, section 13).

## Decision
**1. One error body: RFC 9457 Problem Details with extension members.** Every failure, from every source,
is `application/problem+json` with:

| Member | Meaning |
|---|---|
| `status` | HTTP status code |
| `code` | Stable machine-readable code, e.g. `tenant.already_suspended`. Clients branch on this and nothing else |
| `type` | `urn:tenantflow:problem:{code}`, a stable non-resolvable identifier derived from the code |
| `title` | Fixed human-readable sentence for the code; never contains user input or internals |
| `traceId` | The request's bare 32-character trace id, for Tempo and Loki lookups |
| `errors` | Only for validation: an array of `{ field, code, message }` |

No success envelope: successful responses are the resource itself.

**2. Code convention.** Lowercase `area.reason` segments of `[a-z0-9_]` joined by dots
(`AppError.CodePattern`). Examples: `request.validation_failed`, `tenant.not_found`, `tenant.name.too_long`,
`authentication.required`, `authorization.forbidden`, `server.unexpected`. Codes are a public contract:
never renamed or reused; messages may change wording. A test enforces the pattern and uniqueness for every
module catalog.

**3. Transport-agnostic error model.** A new project `TenantFlow.BuildingBlocks.Abstractions` holds
`AppError(Code, Kind, Message)`, `ErrorKind` (Validation, NotFound, Conflict, Forbidden) and
`AppException`. Domain and Application raise `AppException` for expected failures and never mention HTTP.
Each module owns a static catalog (`TenantErrors`); validators take their codes and messages from it.

**4. One place maps to HTTP.** `ExpectedFailureMiddleware` converts `AppException`, FluentValidation's
`ValidationException`, and `BadHttpRequestException` into Problem Details. `ProblemMapper` is the only code
that turns an `ErrorKind` into a status. Exception messages and request input are never copied into a response.

**5. Pipeline order** (outermost first): `UseExceptionHandler` (unexpected faults: logged at Error with the
exception, answered with a generic `server.unexpected` 500), `UseStatusCodePages` (a Problem Details body
for any bare 4xx/5xx, including the authentication challenge), `ExpectedFailureMiddleware` (caller mistakes,
never logged as errors), `RoutingErrorEndpointsMiddleware`, authentication, authorization.

**6. One customization point.** `ProblemDetailsExtensions` fills `code`, `type`, and `traceId` for every problem,
including ones written by the framework, so no source can omit them. A status with no module error gets a
code from a small table (`HttpProblemCodes`).

**7. Honest 405 and 415.** Routing's synthetic 405/415 endpoints are let through the fallback policy, so a
caller gets the real status and an `Allow` header. Paths that match nothing are still denied (401/403).
The endpoints are recognised by display name, which the framework does not guarantee; tests fail loudly if a
framework upgrade changes it (check at the .NET 10 move).

**7a. Malformed route ids are 404.** A tenant id that is not in canonical form cannot name a tenant, so it
answers exactly like an unknown one (`tenant.not_found`). A `:guid` route constraint is not used: a route that
does not match falls through to the deny-by-default policy, which told a signed-in operator who mistyped an id
to authenticate.

**8. Same behaviour in every environment.** `RouteHandlerOptions.ThrowOnBadRequest` is set explicitly, so
Production and Development use one code path; a Development parity test guards this.

Out of scope: localisation of messages, per-field codes for nested collections beyond dotted paths,
rate-limit (429) and request-timeout details (codes reserved in `HttpProblemCodes`), and OpenAPI
documentation of the contract (M3).

## Options considered
- **Problem Details with `code` and an `errors` array (chosen).** Standard tooling understands it, one
  shape for all failures, per-field codes. Cost: it replaces ASP.NET's `errors` dictionary
  (property to messages), so clients written against the framework default would need the array.
- **Problem Details with `code`, keeping the `errors` dictionary.** Familiar to ASP.NET clients, but
  per-field codes would need a second parallel member.
- **A custom envelope** (`{ success, data, error }`). Uniform, but fights HTTP semantics and Problem Details
  tooling, and wraps every success.
- **Shared model in a new project (chosen) versus per-module only.** A tiny project is cheap and keeps all
  modules on one convention; per-module exceptions would put mapping code for every module into the host.
- **Filter on each route group** (the first version). Rejected: a forgotten filter turned a 409 into a
  logged 500. **`IExceptionHandler`** was rejected because ASP.NET Core 9 logs every exception that reaches the
  exception middleware at Error level, so each normal 409 would look like an incident.

## Consequences
- Easier: clients handle every error the same way; adding a module means adding a catalog, not mapping code.
- Harder: every new expected failure needs a catalog entry and a test; changing a code is a breaking change.
- `Kind` is deliberately small. Add a value only with its status mapping and a test.
- ADR-0014 is amended: a validation failure is `request.validation_failed` with an `errors` array.

## Validation
- `ErrorContractTests`: validation, malformed and empty bodies, 405, 415, 401, 403, unexpected 500 (no
  internals, logged as Error), expected failures not logged as errors, `traceId` equals the request trace id,
  Development and Production behave alike.
- `ErrorCodeConventionTests`: every catalog entry matches the pattern, is unique, and uses the right kind.
- `TenantLifecycleTests`: `tenant.not_found`, `tenant.already_suspended`, `tenant.not_suspended`,
  `tenant.concurrent_update`.

## Revisit triggers
The .NET 10 move (`SuppressDiagnosticsCallback` on the exception handler could replace the middleware split;
synthetic endpoint names may change); a client needs localisation; an API gateway starts producing errors;
more than one `Kind` is needed per status.
