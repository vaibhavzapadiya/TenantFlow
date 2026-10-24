# ADR-0017: HTTP pipeline, host composition, and configuration policy

- Status: Accepted (2026-10-21 by the user)
- Date: 2026-10-19
- Owners: TenantFlow tech lead (drafted during the pre-S5 review, items 2.7 to 2.9)
- Related decisions: ADR-0008, ADR-0010, ADR-0015, ADR-0016

## Context
The API's pipeline order, security headers, and configuration rules were scattered across `Program.cs`
(75 lines) and several files, and some were only correct by accident. Probing showed: routing was inserted
implicitly before the exception handler, so a fault in routing would escape as a raw exception; EF Core logged
every executed SQL command at Information in Production; the Development settings file held a Keycloak port that
`dev up` also writes, so the same value lived in two places; and responses carried no security headers although
blueprint section 13 requires them.

## Decision
**1. `Program.cs` only orchestrates.** Each concern is a named extension method that follows the existing
`AddTenantFlow*` convention: `ConfigureTenantFlowHost` (Kestrel limits, dependency-injection validation),
`AddTenantFlowTelemetry`, `AddTenantFlowPersistence` (database settings, modules, readiness check),
`AddTenantFlowAuthentication`, `AddTenantFlowProblemDetails`, `UseTenantFlowPipeline`, `MapTenantFlowApi`.
A method is created for a concern with its own rules, not for a single registration.

**2. The pipeline is defined once, in order, in `PipelineExtensions`.** Outermost first: security headers, exception
handler, status-code pages, routing (explicit, so it runs inside the exception handler), expected-failure
mapping, routing-error endpoints, authentication, authorization, endpoints.

**3. Baseline security headers on every response**, set as the response starts so error responses carry them:
`X-Content-Type-Options: nosniff`, `Cache-Control: no-store`, `Referrer-Policy: no-referrer`,
`Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`. An endpoint that is safe to cache must
say so explicitly and be reviewed.

**4. Deliberately not in the API pipeline**, each with its owner:

| Not here | Why | Owner |
|---|---|---|
| HTTPS redirection, HSTS | The edge terminates TLS; the API has no HTTPS binding | Edge, M12 |
| Forwarded headers | Never trusted without a known-proxy list (blueprint section 9) | M9 with rate limiting |
| CORS | No browser client exists; adding one needs a reviewed policy | When a browser client is planned |
| Rate limiting | Needs the trusted-proxy and per-tenant/identity dimensions decided | M9 |
| OpenAPI exposure | No document exists yet; it must not be public in Production by default | M3 |

**5. Configuration policy.**
- Behaviour comes from configuration, never from `IsDevelopment()` branches. The only use of the environment name
  is the telemetry resource attribute.
- Precedence is ASP.NET Core's default and is relied on, not reimplemented: `appsettings.json`, then
  `appsettings.{Environment}.json`, then user-secrets (Development only), then environment variables, then the
  command line. An unset environment is Production.
- Committed settings files hold no secret and no address that differs per machine or environment. Addresses and
  credentials come from user-secrets (written by `dev up` from `.env`), environment variables, or a secret store.
  Tests enforce this by key name.
- Required settings are validated at startup and fail the host naming the key, never the value.
- Logging levels in the base file keep Production quiet: `Microsoft.AspNetCore` and `Microsoft.EntityFrameworkCore`
  at Warning, so SQL text and framework chatter are not written. Development raises them for debugging.

## Options considered
- **Keep `Program.cs` as one block.** Readable at 75 lines, but each new module adds more, and the order of the
  pipeline stays implicit. Rejected: the order is a security property and should be one reviewed file.
- **One extension method per registration.** Over-fragmented; rejected in favour of one method per concern.
- **A shared hosting project for API and Worker.** The two hosts share about ten duplicated lines
  (server header, DI validation). Not extracted: a test in each host guards them, and a shared project would
  carry little else. Revisit when a third host appears.
- **HSTS and redirection in the API.** Harmless but dead code behind a TLS-terminating edge; rejected.

## Consequences
- Easier: reviewing the pipeline and the security headers in one place; adding a feature means one line in
  `MapTenantFlowApi`.
- Harder: the API cannot start in Development without `dev up` (no hard-coded authority), exactly like the
  database connection string already.
- A response that must be cacheable needs an explicit, reviewed override of `Cache-Control`.

## Validation
- `HttpPipelineTests`: headers on 200, 401, 405, and an unexpected 500; a fault in routing is answered with
  Problem Details 500.
- `ConfigurationFilesTests`: no secret or machine-specific key in committed settings; logging levels.
- `TenantLifecycleTests.Production_logs_hold_no_sql_and_no_tenant_names` against the real database.
- `dev.ps1 smoke` against the real host; the headers were also checked on Kestrel.

## Revisit triggers
A browser client (CORS, cookies, CSRF); a second public host; rate limiting is designed (M9); the .NET 10 move
(implicit middleware ordering and `Program` exposure may change); a cacheable endpoint is added.
