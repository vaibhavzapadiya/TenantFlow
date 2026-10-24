# Engineering conventions

How code in TenantFlow is organised and written. This page states the conventions and links to the
decision behind each one; it does not repeat the reasoning. Rules that protect tenants, money, and
secrets live in [`AGENTS.md`](../AGENTS.md) ("Non-negotiable rules") and take precedence over this page.

**Status of the sources.** A convention that rests on a `Proposed` ADR is marked *(Proposed)*; none does today
(ADR-0013 to ADR-0019 were accepted on 2026-10-21). Conventions marked *(planned)*
describe where the code is going and do not exist yet. A change to a convention here needs an ADR when it
meets the [ADR-0001](architecture/decisions/0001-record-architecture-decisions.md) trigger (section 15).

## 1. Solution and project organisation

- One solution, `TenantFlow.sln`. Source is under `src/`, tests under `tests/`, never mixed. Local environment
  setup is `docker compose up -d --wait`, and the repository has no host scripts. Development-only projects that are
  never deployed live under `tools/` (`TenantFlow.Progress`) and reference no `src/` project
  ([ADR-0037](architecture/decisions/0037-local-setup-without-a-setup-tool.md), *(Proposed)*).
- `src/Hosts/` holds the executables (`Api`, `Migrator`, `Worker`). A host composes modules and owns
  transport concerns; it holds no business rules.
- `src/Modules/<Module>/` holds one business module, split into `Domain`, `Application`, and
  `Infrastructure` projects ([ADR-0002](architecture/decisions/0002-modular-monolith.md)). A `Contracts`
  project is added when a second module first needs something from this one (`Tenants.Contracts` is the first).
- `src/BuildingBlocks/` holds technical code shared by every module and host, and no business concepts.
  It stays small: `Abstractions` (error and identity types), `Observability`, and `Persistence` (the tenant-scoped
  transaction that feeds row-level security, ADR-0011 and ADR-0019).
- Test projects mirror source: `tests/<Module-or-Host>/TenantFlow.<Name>.Tests`, plus
  `tests/Database/` for tests that need PostgreSQL, `tests/Architecture/` for the module-boundary and schema rules
  (read from the compiled assemblies, so a stray reference fails the build), and `tests/Shared/TenantFlow.Testing` for
  shared helpers.
- Package versions live only in `Directory.Packages.props`; build rules in `Directory.Build.props` and
  `.editorconfig` ([ADR-0004](architecture/decisions/0004-build-quality-gates.md)).

## 2. Folder and namespace naming

- Project and assembly names are `TenantFlow.<Module>.<Layer>` (for example `TenantFlow.Tenants.Domain`),
  `TenantFlow.<Host>`, and `TenantFlow.BuildingBlocks.<Name>`.
- The namespace equals the root namespace plus the folder path. Fix a mismatch by moving the file or
  renaming the folder, not by editing the namespace alone.
- Folders are named for the feature or concept, in plural or noun form (`Tenants/`, `Lifecycle/`,
  `Auditing/`), never for the kind of file (`Models/`, `Helpers/`, `Utils/`, `Interfaces/`). One deliberate exception:
  an API feature folder keeps its request and response records in `Schemas/`, so the wire contract of a feature is one
  folder to review. Likewise an Application feature folder splits by kind one level down, always with these names:
  `Commands/` (use-case inputs), `Validators/` (FluentValidation, reusing domain rules), `Dtos/` (what use cases
  return), `Services/` (the use cases and their private helpers). Ports stay in the project's `Abstractions/`.

```text
TenantFlow.Catalog.Application/
  Abstractions/       ICatalogStore, ProductRecord, ProductCursor
  Products/
    Commands/         CreateProductCommand, UpdateProductCommand
    Validators/       CreateProductCommandValidator, UpdateProductCommandValidator, DomainRuleValidation
    Dtos/             ProductDetails, ProductPage
    Services/         ProductService, ProductCursorCodec
```
- One top-level type per file, and the file is named for the type. A small interface and its only
  implementation still get separate files.
- Type names say what the thing is: `TenantLifecycleService`, `CreateTenantCommandValidator`. Avoid
  `Manager`, `Helper`, and `Common`.

### Layout of the Api host

```text
Program.cs          the composition, as a short list of steps
Extensions/         the entry points Program calls, one file per concern (Add... / Use... / Map...)
Middleware/         every custom middleware, in one place
Configuration/      options classes, fixed limits, and the startup checks on them
ErrorHandling/      the error contract: code table and exception-to-problem mapping
Health/             health checks
Endpoints/V1/
  TenantAccess/     how a route becomes a tenant route: RequireTenantPermission, the access filter, its metadata
  Schemas/          request and response records shared by several features (MembershipResponse)
  <Feature>/        one folder per feature (Me/, Platform/Tenants/, Tenants/, Tenants/Members/, Tenants/Products/):
                    *Endpoints.cs, route-value parsing (ProductRouteId), header helpers (ProductETag)
    Schemas/        that feature's request and response records, one per file
```

An extension class registers; it does not decide. Logic that grows beyond a registration moves to a type in
its concern's folder. A new version adds `Endpoints/V2/` beside `V1/`.

## 3. Feature-based organisation and boundaries

- Inside a project, group by feature first and technical kind second. Tenant lifecycle code sits in
  `Lifecycle/` (then `Commands/`, `Validators/`, `Dtos/`, `Services/`) of the Application project; the HTTP surface for it sits in
  `Endpoints/V1/Platform/Tenants/` of the Api host.
- A module is a business boundary. It reaches another module only through that module's `Contracts`
  project, never its Domain, Application, Infrastructure, `DbContext`, or tables
  ([ADR-0002](architecture/decisions/0002-modular-monolith.md),
  [ADR-0012](architecture/decisions/0012-module-data-ownership.md)).
- Each module owns one PostgreSQL schema and one `DbContext`. Only the Migrator applies DDL.
- Do not add a shared "common" project to avoid a boundary. Duplicating a small type is cheaper than a
  hidden dependency; move it to `BuildingBlocks` only when two modules need the same technical concept.

## 4. Layer responsibilities

| Layer | Holds | Must not hold |
|---|---|---|
| Domain | Entities, value objects, invariants, state transitions, domain errors, the audit records a change produces | EF Core, HTTP, configuration, clocks (take a `TimeProvider` value in) |
| Application | Use cases as direct services ([ADR-0013](architecture/decisions/0013-use-case-dispatch.md)), commands, validators, ports (`ITenantStore`) | HTTP types, EF Core types, SQL |
| Infrastructure | EF Core mappings, stores, migrations, module registration | Business rules |
| Host (Api, Worker, Migrator) | Routing, authentication, request and response shapes, error mapping, composition | Business rules, direct `DbContext` use |

- The domain decides what a change means; the application service keeps the pieces together
  (load, call the domain, save). An audit record is built by the domain, never assembled by the caller.
- A rule lives in one place. The tenant-name rule is in `Tenant.CheckName`; the validator reuses it
  ([ADR-0014](architecture/decisions/0014-input-validation.md)).
- No generic repositories, no mediator, no speculative interfaces ([`AGENTS.md`](../AGENTS.md), rule 8).

## 5. API versioning

([ADR-0015](architecture/decisions/0015-api-versioning.md))

- The version is a URL segment: `/v1/...`. Every business route is registered under `ApiVersions.MapV1()`.
- `/health/*` is unversioned and outside the version group.
- A new version is added by a new group beside the old one. A breaking change to a contract needs a new
  version; adding an optional field does not.
- A route-table test lists every endpoint, so an unversioned route fails the build.
- The OpenAPI document is generated from the endpoints and compared with `docs/api/openapi-v1.json`; a contract
  change is a reviewed snapshot update in the same change ([ADR-0019](architecture/decisions/0019-m3-conventions.md)).

## 6. Response and error contracts

([ADR-0016](architecture/decisions/0016-error-model-and-problem-details-contract.md))

- Success returns the resource, or a small response record, as JSON. A response type is a record in the
  host (`TenantResponse`), never a domain entity.
- Every failure is RFC 9457 Problem Details with `type` (`urn:tenantflow:problem:{code}`), `title`,
  `status`, a stable `code`, and a bare 32-hex `traceId`. Validation failures add
  `errors: [{ field, code, message }]`.
- Titles and messages are fixed sentences. Exception messages, stack traces, SQL, and user input are never
  copied into a response.
- A created resource returns `201` with a relative `Location` built from the route name.
- A route that does not exist is `404`; a known route with the wrong method is `405`; a wrong content type
  is `415`; a body over the limit is `413`. Do not collapse them into `400`.

## 7. Error-code naming and validation

- A code is `area.reason` in lower snake case, with optional extra segments (`tenant.name.too_long`).
  The pattern is `AppError.CodePattern`.
- A module declares its codes once, in a catalog class in its Domain project (`TenantErrors`), as
  `AppError(Code, Kind, Message)`. `ErrorKind` decides the HTTP status in exactly one place, `ProblemMapper`.
- Codes are public contract. Never rename or reuse one; add a new code and retire the old.
- An error that other modules must answer with (such as "no such tenant") is defined once in the owning module's
  `Contracts` project and reused by that module's catalog (`TenantContractErrors`), so there is one code and one message.
- Messages are one sentence, end with a full stop, and contain no placeholders. A test enforces the
  pattern, the uniqueness of codes, and the message rules for every catalog.
- Validation happens at two depths. The domain protects its invariants. A FluentValidation validator in
  Application checks input shape and reuses domain rules. Give every rule an explicit code.
- A malformed identifier in a route is a `404` for that resource, not a `400`
  (`TenantRouteId.Parse`), so a probe cannot tell "malformed" from "absent".

## 8. Exception handling and middleware order

- Expected failures are `AppException` (carrying an `AppError`) or `ValidationException`. They are mapped
  to `4xx` by `ExpectedFailureMiddleware` and are never logged as errors.
- Anything else is a fault: the exception handler logs it with its trace and returns a generic `500`
  with code `server.unexpected`.
- Do not catch an exception only to return a status code. Do not use exceptions for control flow that a
  return value expresses clearly.
- Pipeline order, as built in `UseTenantFlowPipeline`: security headers, exception handler, status-code
  pages, routing, expected-failure mapping, routing-error endpoints, authentication, authorization.
  Change the order only with a test that observes the difference (one step, the security headers, cannot be
  observed today, so its position is kept by convention)
  ([ADR-0017](architecture/decisions/0017-http-pipeline-host-composition-and-configuration.md)).
- Every response carries `X-Content-Type-Options: nosniff`, `Cache-Control: no-store`, a referrer policy,
  and a restrictive content-security policy.

## 9. Authentication and authorization

([ADR-0009](architecture/decisions/0009-identity-provider-and-oidc-boundary.md),
[ADR-0010](architecture/decisions/0010-trust-boundaries-between-user-types.md))

- The API validates standard OIDC JWTs only: signature, issuer, audience, lifetime, and algorithm.
  Nothing in the code depends on a particular identity provider.
- Tenant users and platform operators use separate authentication schemes, issuers, and audiences. The
  startup check `IssuerSeparation` refuses a configuration where they overlap.
- Platform tokens live at most one hour. Token `sub` and issuer lengths are capped by `IdentityLimits`.
- There is no default authentication scheme. The fallback and default policies deny and name no scheme, so
  an endpoint that forgets to declare a policy is closed, and `/health` never reads a token.
- Authorize by policy, not by role names scattered in handlers. Tenant authorization comes from the
  authenticated identity and an active membership; a tenant id in a route or body selects a tenant and
  never grants access *(membership policies: S5 and S6)*.
- Every protected endpoint has a test for no token, a wrong-audience token, and a cross-tenant request.
- Permission names are defined once, in `IdentityAccess.Contracts.TenantPermissions`; which role grants which
  permission is IdentityAccess's own rule (`RolePermissions`). Modules and the host use the contract's constants.
- A tenant route is mapped with `RequireTenantPermission(permission)`, never with a bare policy. The route's `{tenantId}`
  only selects a tenant; the filter resolves the membership, and handlers read the tenant from `ITenantContext`. Tenant-owned
  use cases take no tenant id and throw when no context is set.

## 10. Environment configuration and secrets

([ADR-0008](architecture/decisions/0008-local-configuration-and-secrets.md),
[ADR-0017](architecture/decisions/0017-http-pipeline-host-composition-and-configuration.md))

- Configuration is read through typed options with startup validation. A missing or invalid required
  setting stops the host and names the key, never the value.
- `appsettings.json` holds safe defaults. `appsettings.Development.json` holds no addresses and no
  credentials; Development settings hold only the local stack's `-local-only` values (ADR-0037 *(Proposed)*); other local values go to user-secrets. Deployed environments supply the
  same keys from environment variables or a secret manager.
- No credential, token, or personal data in code, configuration, fixtures, logs, traces, or test data.
  Test values are placeholders (`Password=placeholder`) and synthetic identifiers.
- Do not read `deploy/compose/.env`; the dev script does.
- Production-only behaviour must not depend on a Development-only default. Tests run hosts in `Production`.

## 11. Service registration and extension methods

- `Program.cs` stays short and reads as a list of steps. Each step is an extension method named for what
  it composes: `AddTenantFlowTelemetry`, `AddTenantFlowPersistence`, `AddTenantFlowAuthentication`,
  `AddTenantFlowProblemDetails`, `UseTenantFlowPipeline`, `MapTenantFlowApi`.
- A module exposes one registration method from its Infrastructure project (`AddTenantsModule`); a host
  never registers a module's internals one by one.
- An extension method registers one concern and does not read another concern's configuration.
- Registration code has no business logic and no conditional behaviour that tests cannot reach.

## 12. Dependency injection and dependency direction

- Dependencies point inward: Host → Infrastructure → Application → Domain. Domain references nothing of
  ours except `BuildingBlocks.Abstractions` and its own module's `Contracts`. `tests/Architecture` enforces this.
- Constructor injection only. No service locator, no static state, no `IServiceProvider` passed into
  business code.
- Inject `TimeProvider` for time, and `IMeterFactory`/`ILogger<T>` for telemetry. Never `DateTime.UtcNow`.
- Lifetimes: a `DbContext` and stores are scoped; application services are scoped; options and the
  `TimeProvider` are singletons. A singleton never captures a scoped service.
- A new package or infrastructure component needs a stated reason, and an ADR when it holds state
  ([`AGENTS.md`](../AGENTS.md), rule 8).

## 13. Logging, tracing, and correlation

([ADR-0007](architecture/decisions/0007-logging-pipeline.md))

- Log through `ILogger<T>` with `LoggerMessage` source generation. Each message has a unique `EventId`, numbered in a block per host (1xxx worker, 3xxx migrator).
- Never log a token, a credential, a request body, or personal data. Log identifiers, counts, and codes.
- Every authentication or authorization denial is counted by `AuthDenialRecorder` in `tenantflow.auth.denials` with only
  the labels `scheme` and `reason`, both from a closed list, and logged with those two values alone. Never put a token,
  subject, tenant id, or any caller text in a label or a denial log line.
- The pipeline is built once, in `TenantFlow.BuildingBlocks.Observability`, and shared by every host. A
  log record written inside a span carries its trace id; the same id is returned as `traceId` in a
  Problem Details body, so a client report can be matched to a server trace.
- Expected failures (`4xx` produced by the error contract) are not logged as errors. A fault is logged
  once, where it is handled.
- Entity Framework logging is at `Warning` outside Development, so SQL text and parameters do not reach
  production logs.
- Name `ActivitySource`, `Meter`, and instrument strings once, as constants (for example `WorkerTelemetry`).

## 14. Testing and quality gates

([ADR-0004](architecture/decisions/0004-build-quality-gates.md), [`.agents/rules/tests.md`](../.agents/rules/tests.md))

- The gate is `dotnet test TenantFlow.sln -c Release`: locked restore, Release build with warnings (formatting
  included) as errors, and tests. A slice that touches persistence also passes the database tests (`dotnet run --project tests/Database/TenantFlow.Database.Tests -c Release`).
  Nothing is done until the gate passes after the last edit.
- Domain and Application rules get host-free unit tests. Host tests use the shared factories and run in
  `Production`, sequentially. Persistence tests use the real database, never a mock of EF Core.
- Every tenant-scoped feature has a cross-tenant denial test. Every guard has a test that fails when the
  guard is removed (a break-it proof, run at runtime, not only a compile error).
- A host-starting or timing-sensitive test must pass three runs in a row before it counts.
- An analyzer suppression goes in `.editorconfig` with a one-line reason. A failing test is fixed or
  reported, never disabled.
- Package changes are followed by `dotnet restore TenantFlow.sln`. There are no `packages.lock.json` files
  (removed by the user, 2026-10-23).

## 15. ADR and documentation requirements

([ADR-0001](architecture/decisions/0001-record-architecture-decisions.md),
[ADR-0006](architecture/decisions/0006-claude-code-setup-and-spec-driven-workflow.md))

- A decision needs an ADR when it is hard to reverse, crosses a module boundary, adds a stateful
  component or a dependency, changes a public contract, or sets a convention others must follow. Use the
  [template](architecture/decisions/0000-adr-template.md).
- A new ADR is `Proposed`. Only the owner makes it `Accepted`. Superseding an ADR adds a new one and
  marks the old one `Superseded`; it does not rewrite history.
- A feature on the spec path has an approved spec in `docs/specs/` before code, with a Verification table
  filled in when its slices land.
- A change that makes a document wrong updates that document in the same commit. Link to the source of
  truth instead of copying it.
- `docs/progress/progress.json` records verified work only; run `dotnet run --project tools/TenantFlow.Progress -- --verify`.
- One commit per slice or review item, only when the owner asks. Never push, tag, or open a PR unasked.
