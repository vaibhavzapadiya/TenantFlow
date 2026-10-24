# TenantFlow --- Production-Grade .NET SaaS Backend Blueprint

> **Purpose:** A lead-level, portfolio-quality project that teaches and
> demonstrates production engineering through a realistic multi-tenant
> SaaS backend. This is a blueprint and implementation handbook, not a
> promise that every advanced technology belongs in the first release.

-   **Working name:** TenantFlow
-   **Repository model:** Monorepo
-   **Architecture trajectory:** Modular monolith first; extract
    services only when evidence and operational needs justify it
-   **Implementation style:** Vertical slices, gated milestones,
    architecture decision records (ADRs)
-   **Database:** PostgreSQL
-   **Observability:** OpenTelemetry, Prometheus, Grafana, Loki, Tempo
-   **Cloud:** Compare AWS and GCP; keep the initial choice provisional
    until a short decision exercise
-   **Testing:** xUnit, Testcontainers, contract, architecture,
    security, resilience, and performance tests
-   **Delivery:** Docker Compose locally, Terraform for infrastructure,
    GitHub Actions for CI/CD, progressive releases

------------------------------------------------------------------------

## 1. Consolidated requirements

This section consolidates all discovery answers. The requirements are
retained even when a feature belongs to a later milestone or an optional
lab.

  -----------------------------------------------------------------------
  Area                                Consolidated requirement
  ----------------------------------- -----------------------------------
  Project goals                       Learning, portfolio,
                                      production-grade implementation,
                                      technical-lead/architect practice,
                                      and interview preparation

  Domain                              Recommend a realistic domain;
                                      TenantFlow is a multi-tenant B2B
                                      SaaS operations platform

  Experience level                    Advanced .NET developer; target
                                      depth is senior engineer /
                                      technical lead / architect

  Learning style                      Hybrid: architecture,
                                      implementation, testing,
                                      operational practice, and failure
                                      simulations

  Scope                               Broad, including customer and
                                      tenant administration, orders,
                                      payments, inventory, notifications,
                                      refunds, reporting, subscriptions,
                                      workflows, audit, imports, search,
                                      and operations

  Architecture                        Modular monolith first, with
                                      selective later extraction of
                                      services

  Domain design                       Practical DDD, business-oriented
                                      module boundaries, explicit
                                      ownership, formal ADRs and
                                      architecture governance

  Patterns                            Transactional outbox, saga/process
                                      manager, idempotency, state
                                      machines, feature flags,
                                      architecture tests, selective event
                                      sourcing, plugin boundaries,
                                      strangler migration

  API and communication               REST, webhooks, messaging, gRPC,
                                      and realtime options; use each
                                      where it earns its complexity

  Persistence                         PostgreSQL; shared-schema
                                      multitenancy with `TenantId`; EF
                                      Core as the default and raw
                                      SQL/Dapper for proven query-heavy
                                      paths

  Data consistency                    Strong consistency for local
                                      invariants; eventual consistency
                                      for cross-module/distributed
                                      workflows

  Database operations                 Advanced transaction/concurrency
                                      handling, expand-contract
                                      migrations, Redis selectively,
                                      PostgreSQL full-text search, object
                                      storage, asynchronous imports and
                                      exports

  Messaging                           Compare RabbitMQ and Kafka;
                                      at-least-once delivery with
                                      idempotent consumers and
                                      deduplication

  Background work                     `BackgroundService`/message
                                      consumers plus persistent
                                      scheduling for delayed or recurring
                                      business work

  Identity and security               Compare identity approaches; hybrid
                                      authorization; defense-in-depth
                                      tenant isolation; formal threat
                                      modeling and privacy controls

  Integrations                        Realistic sandbox providers, signed
                                      webhooks, timeouts, retries,
                                      reconciliation, and failure
                                      simulation

  Resilience                          Comprehensive failure handling,
                                      SLOs, disaster recovery, automated
                                      resilience tests

  Testing                             xUnit, Testcontainers,
                                      boundary-focused mocks, contract
                                      tests, architecture tests,
                                      end-to-end tests, security tests,
                                      and k6 load tests

  Observability                       OpenTelemetry with Prometheus,
                                      Grafana, Loki, and Tempo; golden
                                      signals and production diagnostics

  Performance                         Establish targets from workload
                                      assumptions and measured evidence
                                      rather than arbitrary throughput
                                      claims

  Cloud and DevOps                    Compare AWS/GCP and hosting
                                      options; managed PostgreSQL, cloud
                                      secrets, cloud registry, Terraform,
                                      GitHub Actions, strict supply-chain
                                      controls

  Local development                   Docker Compose

  Release strategy                    Progressive releases,
                                      four-environment preference to be
                                      clarified, migration safety,
                                      quality gates

  Documentation                       A complete engineering handbook,
                                      Mermaid diagrams, templates,
                                      runbooks, and learning labs

  Execution                           Stepwise vertical slices, milestone
                                      gates, measurable definition of
                                      done, deep blueprint

  Advanced labs                       Include both comparative technology
                                      labs and failure/operations
                                      experiments
  -----------------------------------------------------------------------

## 2. Conflicts, ambiguities, and explicit assumptions

These are not reasons to drop requirements. They are decision points to
track in ADRs and resolve as the implementation gains evidence.

1.  **"Four environments" is ambiguous.** The proposed lifecycle is
    Local, CI, Development, Staging, and Production. Local and CI are
    not persistent deployed environments, leaving three deployed
    environments. If four persistent deployed environments are required,
    add a dedicated Pre-production or Performance environment and
    document its purpose and cost.
2.  **AWS versus GCP is unresolved.** The blueprint compares both. AWS
    is used as a provisional example for deployment sketches only; it is
    not a final commitment. A small decision record should compare team
    learning value, managed container ergonomics, PostgreSQL options,
    identity, observability, cost, and operational burden.
3.  **Identity provider is unresolved.** Compare managed OIDC providers
    with a self-hosted option. Keep application code behind standard
    OIDC/OAuth 2.0 boundaries. Do not invent a custom authentication
    protocol. A local identity implementation is for development/test
    convenience, not a shortcut around production security.
4.  **RabbitMQ versus Kafka is unresolved.** RabbitMQ is the provisional
    primary broker for task/work-queue and workflow messaging. Kafka is
    a comparison lab. Revisit if durable event replay, high-volume
    stream processing, or multiple independent stream consumers become
    real requirements.
5.  **Maximum scope conflicts with finishing a coherent product.**
    Preserve all requested capabilities in the roadmap, but deliver them
    through gated vertical slices. A feature is not "done" merely
    because its project or interface exists.
6.  **Event sourcing is explicitly selective.** Conventional relational
    persistence is the default. Implement a bounded experiment or one
    genuinely justified module; document the operational and modeling
    costs.
7.  **gRPC and realtime are options, not mandatory everywhere.** REST is
    the default external API. Add gRPC only for a clear internal
    contract/performance need; add SignalR/SSE when a user journey
    benefits from live updates.
8.  **Compliance goals do not mean certification.** Build privacy,
    audit, access control, retention, and evidence practices, but do not
    claim SOC 2, ISO 27001, PCI DSS, or other certification without a
    formal program and independent validation.
9.  **Performance targets must be evidence-based.** First define
    workload profiles, user journeys, data volumes, and error budgets;
    then measure and set targets. Avoid ungrounded "supports X requests
    per second" claims.
10. **".NET LTS" is time-dependent.** At kickoff, select the currently
    supported .NET LTS release, pin the SDK, lock package versions, and
    define a supported-version upgrade policy.
11. **"All users" needs separate trust boundaries.** Customer users,
    tenant administrators, platform administrators, support operators,
    and background workers must not share implicit authority.
    Administrative access requires explicit scope, audit, and
    authorization.
12. **"Realistic sandbox payments" is not real-money processing.** Use
    provider test modes or deterministic simulators. Never store raw
    card data; keep payment-provider secrets outside source control.

Maintain an **Open Decisions** section in the repository and close each
item with an ADR before the relevant milestone.

## 3. Product concept and scope

TenantFlow is a multi-tenant B2B SaaS platform for managing a business's
catalog, inventory, orders, payments, subscriptions, notifications,
imports/exports, reports, and audit history. It is intentionally rich
enough to exercise real backend concerns without requiring a frontend or
pretending to be a production payment processor.

### User and system actors

-   **Customer user:** browses permitted catalog data, places and tracks
    orders, manages their own profile and subscriptions.
-   **Tenant administrator:** manages tenant settings, members, roles,
    catalog, inventory, reports, and business operations within their
    tenant.
-   **Platform administrator:** manages tenant lifecycle and
    platform-level configuration through a separate, tightly controlled
    administrative boundary.
-   **Support/operator:** investigates incidents and customer issues
    with least privilege, explicit reason/context, and audited access.
-   **Background worker:** processes messages, scheduled work,
    imports/exports, notifications, reconciliation, and long-running
    workflows using explicit service identity and tenant context.

### Capability priorities

  -----------------------------------------------------------------------
  Priority                Capabilities            Exit expectation
  ----------------------- ----------------------- -----------------------
  P0 --- foundation       Repository, standards,  A clean clone can
                          local stack, CI, tenant build, test, and run
                          context, identity       predictably
                          boundary, PostgreSQL,   
                          health/telemetry        
                          foundation              

  P1 --- core business    Catalog, inventory,     A tenant-scoped order
                          orders, order           can complete through a
                          lifecycle, sandbox      reliable simulated
                          payment, outbox,        workflow
                          idempotency,            
                          notifications           

  P2 --- SaaS breadth     Tenant/member           Tenant administration
                          administration,         and core workflows are
                          permissions,            secure and operable
                          subscriptions, refunds, 
                          audit, search, reports  

  P3 --- operational      Imports/exports, object Operators can diagnose,
  breadth                 storage, scheduled      recover, and evolve the
                          jobs, admin operations, platform
                          advanced observability, 
                          progressive deployment, 
                          DR                      

  Labs                    Broker comparison,      Each lab records a
                          event sourcing, service hypothesis, method,
                          extraction, data-access results, and decision
                          comparison,             
                          cloud/hosting           
                          comparison, realtime    
                          options                 
  -----------------------------------------------------------------------

### Non-goals and guardrails

-   Do not store cardholder data or process real-money payments in the
    learning project.
-   Do not create one microservice per module as a starting point.
-   Do not require Kubernetes for the first production-shaped
    deployment.
-   Do not apply event sourcing or CQRS to every feature.
-   Do not promise exactly-once message delivery. Aim for **exactly-once
    business effects where practical** through idempotency and
    transactional boundaries.
-   Do not add every technology to the runtime simply to list it on a
    résumé.
-   Do not make a frontend a prerequisite for demonstrating backend
    quality.
-   Do not claim regulatory certification or security guarantees that
    have not been independently verified.

## 4. Architectural principles

1.  **Start with a modular monolith.** Keep a single deployable
    application initially, with strong module boundaries and
    independently testable business capabilities.
2.  **Let business modules own their rules and data.** Other modules use
    public contracts or application interfaces, not another module's
    internal domain objects or tables.
3.  **Make dependencies explicit.** Use architecture tests to prevent
    forbidden references and cycles.
4.  **Prefer local ACID transactions.** Use eventual consistency for
    workflows spanning modules, brokers, or providers.
5.  **Assume messages can be duplicated, delayed, reordered, or lost
    before acknowledgment.** Design consumers to be idempotent and
    workflows to be recoverable.
6.  **Treat tenant context as security-critical.** Resolve it from
    trusted identity and membership; never trust an arbitrary
    client-supplied tenant identifier as proof of access.
7.  **Design observability and security in, not as a final sprint.**
8.  **Use evidence to justify optimization and complexity.** Establish a
    baseline, measure, then change one variable at a time.
9.  **Use backward-compatible schema evolution.** Application rollback
    must not depend on a destructive database rollback.
10. **Keep external calls outside database transactions.** Persist
    intent, commit, then perform remote work and reconcile outcomes.
11. **Make important state transitions explicit.** Use state machines or
    clearly modeled transition rules for orders, payments,
    subscriptions, and long-running workflows.
12. **Complexity must earn its place.** Record why a pattern or
    infrastructure component is needed and what simpler option was
    rejected.

### Architecture at a glance

``` mermaid
flowchart TB
    Clients[Clients and tenant users] --> Edge[Ingress / TLS / rate limits]
    Edge --> API[TenantFlow API]
    API --> IAM[Identity and tenant context]
    API --> Modules[Business modules]
    Modules --> DB[(PostgreSQL)]
    Modules --> Outbox[Transactional outbox]
    Outbox --> Relay[Outbox publisher]
    Relay --> Broker[(Message broker)]
    Broker --> Worker[Background workers]
    Worker --> Modules
    Worker --> Providers[Sandbox payment / email providers]
    Worker --> Storage[(Object storage)]
    API --> Cache[(Selective Redis)]
    API --> Telemetry[OpenTelemetry]
    Worker --> Telemetry
    Telemetry --> Metrics[Prometheus]
    Telemetry --> Logs[Loki]
    Telemetry --> Traces[Tempo]
    Metrics --> Dashboards[Grafana]
    Logs --> Dashboards
    Traces --> Dashboards
```

The diagram is conceptual. The first implementation may run API and
worker processes from the same repository and share a PostgreSQL cluster
while preserving module ownership. Avoid a distributed deployment until
there is a demonstrated reason.

## 5. Repository layout

Use a monorepo organized around deployable hosts, shared technical
building blocks, and business modules. Keep shared code small; do not
let a `Common` project become a dumping ground.

``` text
tenantflow/
├── TenantFlow.sln
├── global.json
├── Directory.Build.props
├── Directory.Packages.props
├── .editorconfig
├── .config/
│   └── dotnet-tools.json
├── src/
│   ├── Hosts/
│   │   ├── TenantFlow.Api/
│   │   ├── TenantFlow.Worker/
│   │   └── TenantFlow.Migrator/
│   ├── BuildingBlocks/
│   │   ├── Abstractions/
│   │   ├── Observability/
│   │   ├── Messaging/
│   │   └── Security/
│   └── Modules/
│       ├── IdentityAccess/
│       ├── Tenants/
│       ├── Catalog/
│       ├── Inventory/
│       ├── Orders/
│       ├── Payments/
│       ├── Subscriptions/
│       ├── Notifications/
│       ├── ImportsExports/
│       ├── Reporting/
│       ├── Audit/
│       └── Operations/
├── tests/
│   ├── Unit/
│   ├── Integration/
│   ├── Api/
│   ├── Architecture/
│   ├── Contracts/
│   ├── EndToEnd/
│   ├── Security/
│   └── Resilience/
├── tests-performance/
│   └── k6/
├── deploy/
│   ├── compose/
│   ├── docker/
│   ├── terraform/
│   ├── dashboards/
│   └── kubernetes-lab/
├── scripts/
└── docs/
    ├── architecture/
    ├── domain/
    ├── api/
    ├── security/
    ├── threat-models/
    ├── testing/
    ├── operations/
    ├── performance/
    ├── delivery/
    └── learning-labs/
```

### Module internal structure

Use a consistent structure where it improves discoverability, but do not
force empty projects into every module.

``` text
Modules/Orders/
├── Orders.Domain/
├── Orders.Application/
├── Orders.Infrastructure/
├── Orders.Contracts/
└── Orders.Tests/
```

-   **Domain:** business entities/value objects, invariants, domain
    policies, state transitions. No HTTP, broker, or database-specific
    dependencies.
-   **Application:** use cases, transaction boundaries, orchestration,
    authorization requirements, and ports.
-   **Infrastructure:** EF Core mappings, SQL, repositories where
    useful, provider adapters, messaging, and persistence
    implementations.
-   **Contracts:** public module/API/event contracts only; keep them
    versioned and intentionally small.
-   **Tests:** unit and module-level tests. Cross-module integration
    tests live in the top-level test projects where appropriate.

### Coding and repository standards

-   Enable nullable reference types and analyzers; treat important
    warnings as errors.
-   Pin the SDK in `global.json`; centrally manage package versions.
-   Use `.editorconfig`, consistent naming, formatting checks, and
    documented analyzer suppressions.
-   Use asynchronous I/O, pass cancellation tokens through request and
    background paths, and avoid sync-over-async.
-   Avoid static service locators and hidden global state.
-   Use explicit options validation at startup for required
    configuration.
-   Keep secrets out of source, fixtures, logs, and committed `.env`
    files.
-   Keep public APIs and event contracts documented and
    compatibility-tested.
-   Prefer small, reviewable pull requests with tests and an ADR when a
    change alters an architectural boundary.

## 6. Business modules and ownership

  -----------------------------------------------------------------------
  Module                  Owns                    Key interactions
  ----------------------- ----------------------- -----------------------
  IdentityAccess          Identity integration,   Tenants and all
                          membership,             business modules
                          role/permission grants, 
                          authorization policy    

  Tenants                 Tenant lifecycle,       IdentityAccess,
                          status, configuration,  Operations
                          plan/limits             

  Catalog                 Products, variants,     Inventory, Orders,
                          pricing rules,          Search
                          publication state       

  Inventory               Stock balances,         Orders, Operations
                          reservations,           
                          adjustments,            
                          release/commit rules    

  Orders                  Order aggregate, line   Catalog, Inventory,
                          items, totals snapshot, Payments, Notifications
                          lifecycle               

  Payments                Payment                 Orders, provider
                          intents/attempts,       adapters
                          provider references,    
                          refunds, reconciliation 

  Subscriptions           Plans, tenant           Payments, Tenants,
                          subscriptions,          Notifications
                          renewals, entitlements  

  Notifications           Notification            Orders, Payments,
                          preferences, delivery   Subscriptions
                          attempts, templates,    
                          provider adapters       

  ImportsExports          Import jobs, validation Catalog, Reporting,
                          results, export jobs,   Operations
                          object references       

  Reporting               Read models and report  Other modules through
                          generation              explicit query
                                                  contracts or events

  Audit                   Security-sensitive and  All modules via narrow
                          business audit records, append-only interface
                          retention metadata      

  Operations              Platform support        Platform boundary and
                          workflows, feature      modules
                          flags,                  
                          health/diagnostics,     
                          controlled admin        
                          operations              
  -----------------------------------------------------------------------

**Ownership rule:** a module may expose a use-case interface, query
contract, or integration event. Other modules must not reach into its
private EF Core `DbContext`, internal entity classes, or tables to
shortcut the boundary.

## 7. Domain modeling, CQRS, and state transitions

Use practical DDD: model important business invariants and ubiquitous
language, but do not add aggregates, repositories, domain events, or
factories merely to satisfy a pattern checklist.

-   Use aggregates to protect invariants that must be consistent within
    one transaction.
-   Use value objects for meaningful concepts such as `Money`,
    `TenantId`, `OrderId`, and validated email/address values where
    useful.
-   Store money as a decimal/numeric amount plus currency, or use a
    documented minor-unit representation; never use binary floating
    point for money.
-   Define order, payment, subscription, and inventory-reservation
    transitions explicitly and reject invalid transitions.
-   Snapshot price, currency, and relevant product description on order
    lines so historical orders do not change when catalog data changes.
-   Use a stable clock abstraction for time-dependent logic and tests.
-   Use domain events for meaningful in-process domain facts;
    integration events are separate, versioned contracts published after
    commit.
-   Use CQRS selectively: separate read/write models when query
    complexity, scaling, or ownership justifies it. Do not create a
    mediator handler for every trivial line of code by default.
-   MediatR is optional; compare its benefits against direct application
    services and the added indirection.
-   Keep event sourcing as a bounded lab or an explicitly justified
    module with replay, projection rebuild, schema evolution, audit, and
    operational costs documented.

### Example order lifecycle

``` mermaid
stateDiagram-v2
    [*] --> Draft
    Draft --> PendingReservation: Submit
    PendingReservation --> PendingPayment: Inventory reserved
    PendingReservation --> Rejected: Reservation failed
    PendingPayment --> Confirmed: Payment succeeded
    PendingPayment --> PaymentReview: Outcome unknown
    PendingPayment --> Rejected: Payment definitively failed
    PaymentReview --> Confirmed: Reconciliation confirms success
    PaymentReview --> Rejected: Reconciliation confirms failure
    Confirmed --> FulfillmentPending
    FulfillmentPending --> Completed
    Confirmed --> CancellationPending: Cancellation requested
    CancellationPending --> Refunded: Refund completed
    Rejected --> [*]
    Completed --> [*]
    Refunded --> [*]
```

This is illustrative, not a final business contract. Refine transitions,
timeout policy, cancellation semantics, and compensation behavior in a
domain ADR and tests.

## 8. Multitenancy and authorization

### Tenant model

Start with **shared database, shared schema, tenant-scoped rows** using
a non-null `TenantId` for tenant-owned data. This is economical and
operationally straightforward for the learning project, but the design
must explicitly defend against cross-tenant access.

-   Resolve tenant context from authenticated identity, validated
    membership, and route/resource context. A route tenant ID selects
    context; it does not grant access.
-   Use a scoped tenant context that fails closed if a tenant-owned use
    case executes without a valid tenant.
-   Apply EF Core global query filters as a convenience, **not** as the
    only security control.
-   Enforce tenant scoping in application authorization and critical
    SQL/query paths.
-   Use tenant-aware unique constraints and indexes where appropriate,
    for example `(TenantId, ExternalReference)`.
-   Include `TenantId` in relevant foreign-key relationships and
    uniqueness rules to prevent accidental cross-tenant references.
-   Consider PostgreSQL row-level security (RLS) as defense in depth. If
    enabled, document session/transaction context, connection pooling,
    reset behavior, migration ownership, and privileged roles. Test
    pooled-connection leakage and fail-closed behavior.
-   Background jobs and message consumers must establish tenant context
    from trusted persisted job/message metadata and validate it. Never
    inherit tenant state from a previous job.
-   Platform-wide operations must use a distinct, explicit authorization
    path and produce audit records.
-   Add automated negative tests for tenant A attempting to read,
    update, export, or reference tenant B's resources.
-   Ensure caches, object-storage keys, search documents, exports,
    idempotency records, and telemetry dimensions cannot leak tenant
    data.
-   Tenant identifiers in logs/metrics should be carefully bounded and
    privacy-reviewed; avoid unbounded metric labels.

### Authorization model

Use hybrid authorization:

1.  **Authentication:** external OIDC/OAuth 2.0 provider or carefully
    bounded local development setup.
2.  **Tenant membership:** active membership and tenant status checked
    on sensitive operations.
3.  **Role/permission:** coarse role assignments mapped to explicit
    permissions.
4.  **Resource authorization:** ownership, tenant, and state-based rules
    evaluated at the resource/use-case boundary.
5.  **Platform authority:** separate platform-admin policies, stronger
    authentication where available, limited support access, and audit.
6.  **Service identity:** workers and internal callers receive only
    required capabilities; do not reuse a human's permissions.

Avoid relying on a single broad `Admin` role. Define permissions in
business language, test denial paths, and document how permission
changes take effect.

### Identity decision exercise

Compare managed OIDC providers and a self-hosted identity option
against:

-   OIDC/OAuth 2.0 support and standards conformance
-   MFA/passkeys and account recovery
-   Tenant federation/enterprise SSO needs
-   Local development and automated test ergonomics
-   User lifecycle and provisioning
-   Auditability, availability, portability, and lock-in
-   Cost and operational ownership

Do not build password storage, token signing, MFA, or account recovery
from scratch. Validate issuer, audience, signature, expiry, and relevant
claims. Never treat an unvalidated JWT payload as identity.

## 9. API design and external boundaries

REST/OpenAPI is the default public interface. Version contracts
deliberately and make errors consistent.

-   Use consistent resource naming, HTTP status codes, validation,
    pagination, filtering, sorting, and Problem Details responses.
-   Define API compatibility policy and automated OpenAPI/contract
    checks.
-   Use UTC for instants and explicit timezone identifiers for
    user-local schedules. Do not assume every business day is UTC.
-   Make money, currency, rounding, and tax assumptions explicit.
-   Set request body, upload, page-size, timeout, and export limits.
-   Use asynchronous jobs for long-running imports/exports; return a job
    resource and status rather than holding a request open.
-   Apply rate limits by appropriate dimensions (identity, tenant, IP,
    endpoint, and provider constraints) without allowing a single tenant
    to starve others.
-   Use ETags or concurrency tokens for updates where helpful; return a
    conflict when the client operates on stale state.
-   Add CSRF protections when cookie-based authentication uses ambient
    credentials. CSRF is not a blanket substitute for understanding the
    authentication model.
-   Validate webhook signatures over the exact raw request bytes where
    the provider requires it, enforce timestamp/replay policy, persist
    delivery IDs, and make handlers idempotent.
-   Do not trust forwarded headers unless set by known proxies.
-   Prevent SSRF in any URL-fetching/import feature with allowlists,
    private/link-local address blocking, redirect validation, DNS
    rebinding considerations, and outbound network controls.
-   Defend file uploads with size limits, content-type/content
    inspection, safe filenames, malware scanning where appropriate, and
    isolated storage.
-   Prevent CSV formula injection in generated exports and validate
    spreadsheet import content.
-   Never include secrets, access tokens, payment data, or sensitive
    personal data in error bodies.

## 10. PostgreSQL, persistence, concurrency, and migrations

### Persistence strategy

-   PostgreSQL is the system of record for transactional business state.
-   EF Core is the default for domain-oriented writes and routine
    queries.
-   Use raw SQL or Dapper only for a measured need: complex reporting
    queries, carefully tuned bulk operations, or a query that is
    materially clearer and faster in SQL.
-   Keep SQL parameterized; review query plans and indexes with
    representative data.
-   Enforce business-critical invariants in database constraints where
    possible, not only in application validation.
-   Avoid generic repositories that hide EF Core's useful
    query/transaction semantics without a clear payoff.
-   Keep each module's mappings and data access behind its ownership
    boundary. Initially the modules may share one PostgreSQL cluster,
    but ownership should remain explicit.

### Concurrency patterns

Choose the smallest mechanism that correctly protects the invariant:

-   Optimistic concurrency tokens for ordinary edits.
-   Atomic conditional updates for stock counters and state transitions.
-   Unique constraints for duplicate business identifiers.
-   Short, carefully scoped row locks for high-contention invariants
    when needed.
-   Serializable transactions only when the invariant requires them and
    the retry behavior is tested.
-   Bounded retries for known transient database errors; never blindly
    retry non-idempotent operations.
-   Consistent lock ordering and short transactions to reduce deadlocks.
-   Explicit cancellation and timeout behavior for queries and
    transactions.

Inventory reservation is a prime lab: demonstrate two concurrent orders
competing for the last unit and prove stock never becomes negative or is
reserved twice.

### Migration strategy: expand-contract

1.  Add new nullable columns/tables/indexes in a backward-compatible
    change.
2.  Deploy code that can read old and new representations and, if
    necessary, dual-write safely.
3.  Backfill in bounded, observable batches.
4.  Verify data and switch reads/writes.
5.  Remove old schema only after all deployed versions no longer need
    it.
6.  Keep the rollback plan explicit; destructive schema rollback is not
    the default recovery mechanism.

Run migrations through a controlled migration job or deployment stage
with appropriate privileges, not independently from every application
replica. Test migrations against realistic prior schema/data, and ensure
a failed migration has an actionable recovery path.

### Redis and caching

Redis is selective, not mandatory for every read. Candidate uses include
short-lived caching, rate limiting, ephemeral coordination, or
distributed locks only when the correctness model is understood.

-   Document cache ownership, TTL, invalidation, stampede behavior, and
    failure mode.
-   Treat cache as disposable; PostgreSQL remains authoritative.
-   Never rely on a cache lock alone to enforce a business invariant
    that the database can protect.
-   Avoid caching authorization decisions or tenant-sensitive data
    without a clear key, invalidation, and isolation strategy.
-   Test cache outage and stale values. Prefer graceful fallback only
    where the database can safely absorb the load.

### Search, files, and exports

-   Start with PostgreSQL full-text search and appropriate indexes; add
    a dedicated search engine only after measured needs emerge.
-   Store large files and exports in object storage, not PostgreSQL
    blobs by default.
-   Persist object metadata, tenant ownership, content type, size,
    checksum, retention, and authorization metadata in the database.
-   Use short-lived, scoped signed URLs when suitable; avoid public
    buckets.
-   Generate large exports asynchronously, stream data, cap concurrency,
    and clean up expired objects.
-   Imports should have explicit states, row-level validation results,
    idempotent submission where possible, bounded batches, and
    resumability.
-   Make CSV/Excel output safe against formula injection and protect
    imported data against malicious content.

## 11. Messaging, outbox, sagas, and background work

### Broker decision: RabbitMQ versus Kafka

  -----------------------------------------------------------------------
  Dimension               RabbitMQ                Kafka
  ----------------------- ----------------------- -----------------------
  Core strength           Work queues, routing,   Durable partitioned
                          acknowledgments,        event streams, replay,
                          task/workflow messaging high-throughput stream
                                                  consumption

  Typical model           Queue-oriented delivery Append-only log with
                          and routing             consumer offsets

  Replay                  Possible with           First-class within
                          retained/re-published   retention and offset
                          messages, but not its   model
                          primary model           

  Operational concerns    Queue topology,         Partitions, consumer
                          acknowledgments,        groups, retention,
                          redelivery,             ordering per partition,
                          dead-lettering,         lag
                          capacity                

  TenantFlow initial fit  Provisional choice for  Comparison lab; revisit
                          background jobs and     if event replay/stream
                          workflow                processing becomes
                          commands/events         central
  -----------------------------------------------------------------------

This is a provisional choice, not a universal rule. Record throughput,
routing, ordering, replay, operational ownership, managed offerings,
cost, and developer ergonomics in the broker ADR.

### Transactional outbox

The outbox addresses the dual-write problem between a database
transaction and broker publication:

1.  In one database transaction, persist the business state change and
    an outbox record.
2.  A publisher reads pending outbox records in bounded batches.
3.  Publish with a stable message/event ID and relevant metadata.
4.  Mark records published only after the broker confirms according to
    the selected client/broker semantics.
5.  Retry safely; monitor age, backlog, retries, and poison records.
6.  Retain and clean up published rows according to a documented policy.

The publisher can crash after broker publication but before marking the
row. Therefore, duplicate publication is expected and consumers must be
idempotent. Outbox cleanup must not race with publication or remove
records needed for recovery.

### Consumer correctness

-   Assume at-least-once delivery.
-   Persist a processed-message/deduplication record in the same
    transaction as the consumer's business effect when feasible.
-   Use stable message IDs, schema versions, correlation/causation IDs,
    and timestamps.
-   Acknowledge only after durable processing.
-   Use bounded retries with backoff; distinguish transient failures
    from permanent invalid messages.
-   Send poison messages to a dead-letter/quarantine path with alerting
    and a documented replay procedure.
-   Make replay safe and observable; do not blindly redrive all dead
    letters into production.
-   Define ordering requirements per workflow. Do not assume global
    ordering.
-   Use schema compatibility checks for integration events.

### Sagas and workflow state

Use a saga/process manager for multi-step workflows such as order →
inventory reservation → payment → confirmation. Persist workflow state,
deadlines, retries, and compensation steps. A compensation is a new
business action, not a magical rollback of an external side effect.

For example: - If inventory reservation fails, reject the order without
attempting payment. - If payment definitively fails, release the
reservation and reject the order. - If payment times out with an unknown
result, move to a review/reconciliation state rather than assuming
failure and charging again. - If payment succeeds but later work fails,
use a defined refund/compensation policy and retain the audit trail.

### Background processing

Use `BackgroundService` for bounded worker loops and broker consumers.
Use a persistent scheduler for delayed or recurring business jobs where
missed execution, restart recovery, and visibility matter. The scheduler
itself must be monitored and idempotent.

Avoid in-memory timers for business-critical scheduled work. Define
concurrency limits, per-tenant fairness, cancellation, graceful
shutdown, and poison-job handling.

### gRPC and realtime

-   Keep REST as the default public API.
-   Use gRPC only where typed internal contracts or measured
    latency/throughput needs justify it.
-   Use SignalR or SSE for meaningful live progress/status updates;
    ordinary polling may be simpler and more resilient.
-   Treat realtime connections as another resource to authenticate,
    authorize, rate-limit, observe, and scale.

## 12. Payment and third-party integrations

Use provider sandbox/test mode or a deterministic simulator. The payment
adapter should isolate provider-specific request/response models from
the domain.

-   Model payment intent/attempt states explicitly: created, pending,
    succeeded, failed, unknown/requires reconciliation,
    refunded/partially refunded.
-   Use idempotency keys for create/refund operations where supported,
    scoped to the tenant and business operation.
-   Persist provider request identifiers and safe diagnostic metadata,
    but never log secrets or sensitive payment details.
-   Handle timeouts as potentially ambiguous outcomes; reconcile with
    provider status or verified webhooks before retrying a charge.
-   Verify webhook signatures, timestamps, and replay IDs; accept
    duplicate delivery safely.
-   Expect webhook and API responses to arrive out of order. Apply
    state-transition rules and reconciliation rather than trusting
    arrival order.
-   Add circuit breakers and provider-specific rate limits, but do not
    let a circuit breaker silently lose business intent.
-   Define retryable versus terminal provider errors.
-   Provide a reconciliation job for stuck/unknown payments and
    mismatched records.
-   Simulate provider latency, 429s, 5xx errors, timeouts, malformed
    responses, duplicate webhooks, and delayed/out-of-order
    notifications.
-   Refunds are separate workflows with their own idempotency,
    authorization, state, and audit trail.

## 13. Security, privacy, and threat modeling

Treat security as an ongoing engineering process. Build a formal threat
model before exposing the first tenant-scoped workflow and revisit it as
features are added.

### Threat-model coverage

At minimum, document assets, trust boundaries, attacker goals, attack
paths, mitigations, and residual risks for:

-   Cross-tenant data access and tenant-context confusion
-   Broken object-level authorization and privilege escalation
-   Credential theft, token validation errors, session misuse, account
    recovery abuse
-   Platform-admin and support-operator access
-   Payment callback spoofing, replay, and duplicate processing
-   Message forgery, replay, poison messages, and overprivileged
    consumers
-   SSRF, unsafe uploads, CSV injection, and malicious imports
-   Secret exposure, dependency/supply-chain compromise, container image
    risks
-   Denial of service, noisy-neighbor tenants, expensive reports, queue
    floods
-   Sensitive data in logs, traces, exports, backups, and retained
    objects
-   Unsafe deployment, migration, or feature-flag changes

### Required controls

-   Least privilege for database roles, cloud identities, CI jobs,
    service identities, and support operators.
-   Managed secret storage and workload identity where available; rotate
    secrets and avoid long-lived credentials.
-   Dependency, container, static analysis, secret scanning, and
    infrastructure policy scanning in CI.
-   Secure headers, TLS, input validation, output encoding where
    relevant, request limits, and safe error handling.
-   Rate limiting and abuse protection on public and expensive
    endpoints.
-   CSRF protection for applicable cookie-auth flows; strict CORS
    policy.
-   Signed webhooks, replay prevention, idempotency, and reconciliation.
-   Tenant-aware authorization on every data access path, including
    exports and object storage.
-   Audit sensitive actions: role changes, tenant suspension, data
    export, refunds, support access, configuration changes, and
    privileged operations.
-   Redact or avoid personal data, tokens, secrets, and payment details
    in logs/traces. Define retention and access controls for telemetry.
-   Data classification, purpose limitation, retention schedules,
    deletion/anonymization processes, and backup expiry policy.
-   Safe handling of user-supplied URLs and files; avoid SSRF and
    malicious spreadsheet formulas.
-   Explicit security review for new dependencies and third-party
    integrations.

Do not use logs as an unrestricted secondary database. Do not state that
a control "guarantees security"; document threat assumptions and
residual risk.

## 14. Resilience and failure behavior

Use timeouts, cancellation, bounded retries with exponential backoff and
jitter, circuit breakers, bulkheads, load shedding, and graceful
degradation where appropriate. Every retry policy must consider
idempotency and downstream capacity.

  --------------------------------------------------------------------------
  Failure                  Expected behavior        Evidence/test
  ------------------------ ------------------------ ------------------------
  PostgreSQL unavailable   Fail readiness; return   Integration/resilience
                           controlled errors; avoid test
                           retry storms; recover    
                           without duplicate        
                           business effects         

  Redis unavailable        Fall back only where     Fault injection
                           safe; protect the        
                           database from sudden     
                           load amplification       

  Broker unavailable       Commit business state    Outbox outage test
                           plus outbox; backlog     
                           becomes visible; publish 
                           resumes later            

  Duplicate message        Business effect occurs   Consumer deduplication
                           once; duplicate is       test
                           measurable but not fatal 

  Poison message           Bounded retry then       Automated poison-message
                           quarantine/dead-letter   test
                           with alert and replay    
                           procedure                

  Payment request timeout  Mark outcome unknown;    Provider simulator test
                           reconcile before         
                           retrying charge          

  Duplicate/out-of-order   Verify and dedupe;       Contract and integration
  webhook                  enforce legal state      tests
                           transitions              

  Notification provider    Business transaction     Worker resilience test
  outage                   remains committed;       
                           delivery retries         
                           independently            

  Slow report/export       Asynchronous job,        Load and cancellation
                           resource limits,         test
                           cancellation, tenant     
                           fairness                 

  Bad application release  Smoke/health gates;      Deployment rehearsal
                           progressive rollout      
                           stops; rollback path     
                           documented               

  Incompatible schema      Expand-contract protects Migration compatibility
  change                   old and new versions     test
                           during rollout           

  Missing tenant context   Fail closed before       Security test
                           tenant-owned access      

  Noisy tenant             Per-tenant limits and    Multi-tenant load test
                           bounded queues prevent   
                           starvation               

  Secret rotation          Services recover without Operational exercise
                           logging credentials or   
                           prolonged outage         
  --------------------------------------------------------------------------

### Retry policy rules

-   Retry only transient failures and only a bounded number of times.
-   Use exponential backoff and jitter; honor provider retry hints where
    appropriate.
-   Set an overall deadline and propagate cancellation.
-   Never retry a non-idempotent operation blindly.
-   Avoid multiplying retries across API, client, worker, and provider
    layers.
-   Monitor retries, timeouts, circuit state, and exhausted budgets.
-   Provide a manual/reconciliatory path for uncertain business
    outcomes.

## 15. Testing strategy and quality gates

Use xUnit for the .NET test suite and Testcontainers for real
infrastructure dependencies where feasible. Mock at external boundaries;
prefer real PostgreSQL behavior for persistence and concurrency tests.

### Test layers

1.  **Unit tests:** domain invariants, state transitions, calculation
    rules, authorization policy logic.
2.  **Module/application tests:** use-case orchestration, transaction
    boundaries, validation, and module contracts.
3.  **Integration tests:** PostgreSQL, migrations, unique constraints,
    row locking, concurrency, outbox, Redis, broker, object-storage
    adapters.
4.  **API tests:** routing, authentication, tenant scoping, validation,
    error contracts, pagination, rate limits.
5.  **Contract tests:** OpenAPI compatibility, event schemas, provider
    adapters, webhook signatures, consumer/provider expectations.
6.  **Architecture tests:** module dependency rules, no cycles, no
    direct cross-module persistence access, permitted building-block
    references.
7.  **End-to-end tests:** tenant onboarding → catalog/inventory → order
    → payment simulation → notification/report.
8.  **Security tests:** cross-tenant denial, object-level authorization,
    privilege escalation, webhook replay, unsafe file/URL inputs,
    secrets/log redaction.
9.  **Resilience tests:** dependency outage, duplicates, poison
    messages, retries, recovery, cancellation, reconciliation.
10. **Performance tests:** k6 workload scenarios and database/query
    profiling.
11. **Migration tests:** upgrade from prior schema with representative
    data; verify backward compatibility and recovery steps.

### Testing philosophy

-   Prefer deterministic tests and injected clocks over arbitrary
    sleeps.
-   Avoid mocking EF Core query behavior as a substitute for database
    tests.
-   Mock external providers at their adapter boundary; also run contract
    tests against provider simulators or sandboxes where practical.
-   Use unique test data and isolate containers/state to prevent
    cross-test contamination.
-   Test both allowed and denied paths. A successful request alone does
    not establish tenant isolation.
-   Include concurrent tests for inventory, idempotency keys, duplicate
    message handling, and refunds.
-   Keep flaky tests visible; do not normalize retries around flaky CI
    as a substitute for fixing them.

### CI quality gates

A pull request should at minimum: - Restore/build with the pinned SDK. -
Format-check and run analyzers. - Run unit and fast architecture
tests. - Run integration tests for changed modules and critical shared
infrastructure. - Validate API/event contracts. - Scan dependencies,
source, secrets, containers, and IaC according to the repository
policy. - Build immutable versioned container images. - Publish test and
coverage artifacts with clear failure summaries.

Run heavier end-to-end, fault-injection, full security, and performance
suites on an appropriate schedule and before high-risk releases. Set
coverage expectations by risk and behavior; avoid a single global
percentage as the only quality signal.

## 16. Observability and production diagnostics

Instrument APIs, workers, database operations, messaging, provider
calls, and scheduled jobs with OpenTelemetry. Use:

-   **Metrics:** Prometheus
-   **Dashboards and alerting:** Grafana
-   **Logs:** Loki
-   **Traces:** Tempo

Keep instrumentation portable so managed cloud services can be
substituted where appropriate.

### Golden signals and business signals

Track latency, traffic, errors, and saturation for API and worker
workloads. Add domain/operational signals:

-   Request duration and error rate by route/operation (avoid
    high-cardinality labels).
-   Queue depth, consumer lag where relevant, retry counts, dead-letter
    count, and oldest-message age.
-   Outbox pending count, oldest pending age, publication failures, and
    throughput.
-   Payment attempts by outcome, unknown-result count, reconciliation
    age, and provider latency.
-   Inventory reservation conflicts and failures.
-   Import/export queue age, job duration, failed rows, cancellations,
    and object-storage errors.
-   Database pool usage, slow queries, lock waits, deadlocks, connection
    errors, and migration status.
-   Authentication/authorization denials and suspicious cross-tenant
    access attempts, without leaking sensitive data.
-   Scheduled job lateness, failure count, and missed execution.
-   Resource usage and estimated cost per major workload where
    practical.

Use trace context and correlation/causation IDs across HTTP, messages,
and workflow steps. Do not put secrets or personal data in baggage. Keep
tenant IDs out of metric labels unless cardinality and privacy have been
explicitly evaluated.

### Health and diagnostics

-   **Liveness:** process can continue; should not fail solely because
    an optional downstream dependency is unavailable.
-   **Readiness:** instance can safely receive the relevant traffic;
    define dependency policy carefully.
-   **Startup checks:** required configuration and essential
    initialization completed.
-   Expose protected diagnostics for dependency status and version/build
    metadata without revealing secrets.
-   Sample traces intentionally; keep enough data for debugging without
    unbounded cost.
-   Define telemetry access controls and retention.

### SLO development

Do not invent SLOs before workload and user journeys are understood.
Define objectives for representative journeys such as authenticated
catalog reads, order submission, payment outcome resolution, and
background export completion.

For each SLO, specify: - User-visible event and measurement window -
Success and latency definition - Eligible traffic and exclusions -
Target and error budget - Alerting policy and response owner -
Dependency and capacity assumptions

Keep synchronous API objectives separate from asynchronous workflow
completion objectives. A request accepted into a queue is not equivalent
to the business workflow being completed.

## 17. Performance engineering with k6

Performance work begins with a workload model, not a target picked for a
résumé.

### Establish the baseline

Document expected tenant count, active users, read/write mix, request
size, data distribution, peak patterns, report/export volume, provider
latency, and acceptable cost. These are test assumptions until
validated.

Use k6 to model: - Common authenticated API reads and writes - Inventory
contention for scarce stock - Order submission with payment simulator
latency - Search/filter/pagination - Concurrent tenant activity and
noisy-neighbor behavior - Large imports and asynchronous exports -
Broker slowdown and consumer backlog - Provider 429/5xx/timeout
patterns - Sustained load, ramp-up, spike, stress-to-failure, and soak
tests

### Report evidence

For every run, capture: - Workload version and data shape - Environment
and resource sizing - Throughput, p50/p95/p99 latency, error rate, and
timeouts - CPU, memory, GC, thread-pool behavior, connection pools -
PostgreSQL query plans, slow queries, locks, and I/O - Queue depth/age
and consumer throughput - Provider latency and retry amplification -
Cost estimate and known confounders

Use a repeatable loop: **hypothesis → baseline → one change → same
workload → compare → decision**. Avoid optimizing from local
microbenchmarks alone. Record results in `docs/performance/` and set
release gates only after repeatable evidence exists.

## 18. Cloud and deployment strategy

### AWS versus GCP

Compare on a small representative deployment rather than debating every
service in the abstract.

  -----------------------------------------------------------------------
  Concern                 AWS direction to        GCP direction to
                          evaluate                evaluate
  ----------------------- ----------------------- -----------------------
  Managed container       ECS/Fargate as a        Cloud Run or a managed
  hosting                 Kubernetes-free first   container option suited
                          option                  to workload needs

  PostgreSQL              Managed PostgreSQL      Cloud SQL for
                          service                 PostgreSQL

  Object storage          S3                      Cloud Storage

  Secrets and keys        Secrets Manager / KMS / Secret Manager / Cloud
                          workload identity       KMS / service identity
                          options                 options

  Container registry      ECR                     Artifact Registry

  Telemetry               OpenTelemetry to chosen OpenTelemetry to chosen
                          backend and/or managed  backend and/or managed
                          services                services

  Infrastructure          Terraform               Terraform

  Decision factors        Team familiarity,       Same factors, including
                          networking, IAM,        service ergonomics and
                          managed-service fit,    portability
                          price, operational      
                          burden                  
  -----------------------------------------------------------------------

These are evaluation candidates, not a finalized bill of materials.
Validate current product features, regional availability, pricing,
quotas, and operational requirements before committing.

### Hosting evolution

1.  Start with containers and a managed container platform; avoid
    Kubernetes as a prerequisite.
2.  Use managed PostgreSQL with backups, point-in-time recovery,
    patching policy, monitoring, and connection management.
3.  Use object storage for exports and uploaded files.
4.  Use cloud secret management and workload identity rather than
    long-lived static credentials.
5.  Keep the API and worker as separate deployable processes even if
    they share a codebase.
6.  Introduce service extraction or orchestration complexity only when
    deployment independence, scaling, fault isolation, or team ownership
    provides measurable value.
7.  Keep Kubernetes as a lab: compare its capabilities and operational
    costs with the managed-container baseline.

### Docker Compose local environment

Provide one documented command to start the required local services. A
representative Compose stack includes:

-   TenantFlow API and worker (optional containerized profiles)
-   PostgreSQL
-   RabbitMQ (provisional primary broker)
-   Redis (optional profile)
-   OpenTelemetry Collector
-   Prometheus and Grafana
-   Loki and Tempo
-   Object-storage emulator or local adapter
-   Provider simulators

Make optional services opt-in when they are not needed for a slice.
Include health checks, persistent development volumes where appropriate,
non-production credentials, and a reliable reset command. Never use
production secrets locally.

### Terraform

-   Separate reusable modules from environment-specific configuration.
-   Use remote state with access controls, encryption, and locking.
-   Pin Terraform/provider versions and review upgrades.
-   Keep secrets out of state where possible; treat state as sensitive
    regardless.
-   Use least-privilege deployment identities and short-lived
    credentials.
-   Run format, validation, policy, and plan checks in CI; require
    review for destructive changes.
-   Detect drift and document how to reconcile it.
-   Avoid creating a generic Terraform abstraction before there is a
    second real use case.

### GitHub Actions and supply chain

-   Use short-lived cloud federation/identity instead of long-lived
    cloud keys.
-   Pin third-party actions to immutable revisions according to the
    organization's policy.
-   Apply least privilege to workflow permissions and isolate untrusted
    pull-request execution from release secrets.
-   Scan source, dependencies, container images, and IaC; generate an
    SBOM and provenance where practical.
-   Build once and promote the same immutable artifact through
    environments.
-   Protect release environments with approvals and required checks.
-   Store deployment metadata and release notes.
-   Test rollback and forward-fix procedures.

### Progressive release

-   Deploy backward-compatible schema changes before code that depends
    on them.
-   Use feature flags for risky behavior, with owners, expiry/review
    dates, and secure defaults.
-   Use canary or staged traffic shifts where the platform supports
    them.
-   Gate progression on smoke tests, error/latency signals, and workflow
    health.
-   Define when to stop rollout and how to roll back the application.
-   Do not roll back schema destructively as a reflex; use forward fixes
    or the documented expand-contract recovery path.

### Environment proposal

Proposed lifecycle: - **Local:** developer workstation, Compose-based. -
**CI:** ephemeral or isolated build/test resources. - **Development:**
shared integration environment. - **Staging:** production-like
validation and release rehearsal. - **Production:** customer traffic.

This is five lifecycle contexts but only three persistent deployed
environments if CI is ephemeral and Local is not deployed. If the
requirement is exactly four persistent deployed environments, introduce
**Pre-production** or **Performance** and define why it exists, what
data it uses, how it differs from Staging, and its budget.

## 19. Reliability, disaster recovery, and operations

Define recovery objectives from business impact rather than choosing
arbitrary RPO/RTO numbers. Document approved targets per environment and
data class.

### Backup and recovery

-   Enable managed PostgreSQL backups and point-in-time recovery where
    supported.
-   Define backup retention, encryption, access control, and deletion
    policy.
-   Back up infrastructure/configuration artifacts and document which
    state is reconstructible.
-   Treat object-storage versioning/retention according to data
    sensitivity and deletion requirements.
-   Run restore drills into an isolated environment; verify data
    integrity and application compatibility.
-   Measure actual recovery duration and compare it with the approved
    RTO.
-   Test that restored services do not accidentally send real external
    notifications or payments.
-   Include secrets/key recovery and rotation dependencies in the plan.

### Operational runbooks

Create and rehearse runbooks for: - API outage and elevated error
rates - Database saturation, lock contention, failover, and recovery -
Queue backlog, consumer outage, poison messages, and replay - Stuck
payment and reconciliation - Failed import/export and object-storage
access issues - Bad release and failed migration - Tenant data exposure
or suspected authorization bypass - Secret rotation and credential
compromise - Resource/cost spike and noisy-neighbor incident - Backup
restore and disaster recovery - Feature-flag emergency disablement

Each runbook should contain symptoms, impact, prerequisites, diagnostic
queries/dashboards, safe mitigations, escalation, verification,
rollback/forward-fix steps, and post-incident follow-up.

### Incident learning

For significant failures, write a blameless review with timeline,
customer impact, contributing factors, detection gaps, recovery, and
owned action items. Feed learnings back into tests, dashboards,
runbooks, and architecture decisions.

## 20. Milestones and implementation gates

The roadmap is intentionally stepwise. Do not start every module at
once.

  -------------------------------------------------------------------------
  Milestone               Deliverable             Gate / measurable exit
                                                  criteria
  ----------------------- ----------------------- -------------------------
  M0 --- Engineering      Solution, SDK/package   Clean checkout builds;
  foundation              pinning, analyzers,     formatting/analyzer
                          formatting, ADR         checks and a sample test
                          template, CI skeleton   pass

  M1 --- Local platform   Compose, PostgreSQL,    One documented command
                          API/worker hosts,       starts dependencies; API
                          health endpoints, OTel  and worker emit
                          pipeline                correlated telemetry

  M2 --- Identity and     OIDC boundary, tenant   Positive and negative
  tenant boundary         membership/context,     tenant isolation tests
                          authorization policies  pass for representative
                                                  resources

  M3 --- First vertical   Tenant-scoped catalog   API contract, database
  slice                   and inventory           constraints, integration
                          read/write              tests, and OpenAPI docs
                                                  pass

  M4 --- Order and        Order lifecycle,        Concurrent last-item test
  inventory consistency   reservation,            proves no oversell;
                          concurrency controls    illegal state transitions
                                                  are rejected

  M5 --- Messaging        Outbox publisher,       Duplicate
  foundation              broker, idempotent      publication/consumption
                          consumer, DLQ path      tests pass; backlog and
                                                  oldest-message age are
                                                  observable

  M6 --- Payment workflow Sandbox adapter,        Timeout/unknown outcome
                          idempotency, webhook    and
                          verification,           duplicate/out-of-order
                          saga/reconciliation     webhook scenarios recover
                                                  safely

  M7 --- Subscriptions    Subscription state      Duplicate renewal/refund
  and refunds             machine, renewals,      attempts cannot create
                          refund workflow         duplicate business
                                                  effects

  M8 --- Async files and  Search,                 Large jobs are
  reporting               imports/exports, object asynchronous,
                          storage, scheduled jobs tenant-scoped, bounded,
                                                  resumable where needed,
                                                  and observable

  M9 --- Security and     Threat models, audit,   Threat-model actions are
  privacy                 retention/deletion,     tracked; high-risk
                          scanning, admin         security tests and
                          boundary                pipeline checks pass

  M10 --- Production      Dashboards, alerts, SLO Key user journeys and
  observability           definitions, runbooks   worker workflows can be
                                                  diagnosed from
                                                  metrics/logs/traces

  M11 --- Performance and k6 workload suite,      Repeatable baseline and
  resilience              fault injection,        failure reports exist;
                          capacity baseline       capacity assumptions and
                                                  bottlenecks are
                                                  documented

  M12 --- Cloud           Terraform, registry,    Reproducible deployment,
  deployment              managed database,       smoke checks, and tested
                          secrets, CI/CD          application rollback path

  M13 --- Disaster        Backup/restore and      Restore drill verifies
  recovery                recovery exercise       integrity and records
                                                  measured recovery time
                                                  against approved
                                                  objectives

  M14 --- Advanced labs   Broker, event-sourcing, Each lab includes
                          service-boundary,       hypothesis, methodology,
                          data-access,            results, trade-offs, and
                          hosting/cloud           an ADR or explicit "not
                          comparisons             adopted" decision
  -------------------------------------------------------------------------

### Vertical-slice implementation template

For every feature, follow this sequence:

1.  Describe the user/business outcome and failure modes.
2.  Write acceptance criteria and invariants.
3.  Define the API/event contract and authorization requirements.
4.  Model state and persistence constraints.
5.  Implement domain/application behavior.
6.  Add infrastructure adapters and transaction boundaries.
7.  Add telemetry, audit, and operational signals.
8.  Add unit, integration, contract, security, and relevant failure
    tests.
9.  Update OpenAPI, module docs, Mermaid diagrams, and ADRs where
    needed.
10. Demonstrate the happy path and at least one important failure path.
11. Verify migration compatibility and rollout/rollback behavior.
12. Mark the slice complete only when the definition of done is met.

## 21. Definition of done

### Feature-level checklist

-   [ ] Business outcome and acceptance criteria are explicit.
-   [ ] Domain invariants and valid state transitions are enforced.
-   [ ] Authentication, authorization, tenant isolation, and denial
    paths are tested.
-   [ ] Validation and consistent error responses are implemented.
-   [ ] Database constraints, indexes, concurrency, and migration
    behavior are considered.
-   [ ] External calls are bounded by timeouts/cancellation and safe
    retry/idempotency behavior.
-   [ ] Messages and background jobs are duplicate-safe and observable.
-   [ ] Sensitive actions produce appropriate audit records.
-   [ ] Logs, metrics, and traces support diagnosis without exposing
    secrets or personal data.
-   [ ] Unit/integration/contract/architecture tests cover relevant
    behavior.
-   [ ] API/event contracts and user/operator documentation are updated.
-   [ ] Feature flags, rollout, recovery, and operational ownership are
    defined where relevant.

### Release-level checklist

-   [ ] CI quality and security gates pass.
-   [ ] Container image and dependencies are versioned/scanned; release
    artifact is immutable.
-   [ ] Database migration is backward-compatible for the planned
    rollout.
-   [ ] Health/readiness and smoke tests pass.
-   [ ] Dashboards, alerts, and runbooks exist for critical workflows.
-   [ ] Rollout stop conditions and application rollback/forward-fix
    plan are explicit.
-   [ ] Backup status and recovery implications are understood.
-   [ ] No unresolved critical/high risk is accepted without an owner
    and documented rationale.
-   [ ] Release notes and operational changes are communicated.
-   [ ] Post-deployment verification checks both technical health and
    business workflow completion.

## 22. Engineering handbook and supporting artifacts

Create and maintain these files as the project evolves:

``` text
docs/
├── README.md
├── architecture/
│   ├── overview.md
│   ├── module-boundaries.md
│   ├── decisions/
│   ├── diagrams/
│   └── dependency-rules.md
├── domain/
│   ├── glossary.md
│   ├── order-lifecycle.md
│   ├── inventory-invariants.md
│   ├── payment-workflow.md
│   └── subscription-lifecycle.md
├── api/
│   ├── conventions.md
│   └── compatibility-policy.md
├── security/
│   ├── security-baseline.md
│   ├── privacy-and-retention.md
│   └── access-model.md
├── threat-models/
├── testing/
│   ├── strategy.md
│   └── test-data.md
├── operations/
│   ├── dashboards.md
│   ├── alerts.md
│   ├── runbooks/
│   ├── incident-review-template.md
│   └── disaster-recovery.md
├── performance/
│   ├── workload-model.md
│   ├── baseline.md
│   └── experiments/
├── delivery/
│   ├── environments.md
│   ├── release-process.md
│   └── migration-policy.md
└── learning-labs/
```

### ADR template

``` markdown
# ADR-NNNN: <Decision title>

- Status: Proposed | Accepted | Superseded
- Date:
- Owners:
- Related decisions:

## Context
What problem and constraints require a decision?

## Decision
What are we choosing, and what is explicitly out of scope?

## Options considered
For each option, describe benefits, costs, risks, and operational burden.

## Consequences
What becomes easier or harder? What migration or follow-up work is required?

## Validation
What evidence, tests, measurements, or review will validate the decision?

## Revisit triggers
What new evidence would justify reconsideration?
```

### Threat-model template

``` markdown
# Threat model: <feature/system>

## Assets and data classification
## Actors and trust boundaries
## Entry points and data flows
## Abuse cases / attack paths
## Existing controls
## Gaps and residual risk
## Mitigations with owner and due date
## Security tests and monitoring
## Review date and change triggers
```

### Runbook template

``` markdown
# Runbook: <incident>

## Symptoms and impact
## Severity and escalation
## Preconditions / access required
## Dashboards and diagnostic queries
## Immediate safe mitigations
## Recovery procedure
## Verification
## Rollback / forward-fix
## Customer / stakeholder communication
## Follow-up actions
```

### Performance experiment template

``` markdown
# Experiment: <hypothesis>

- Date / commit:
- Environment and resource sizing:
- Workload version:
- Data shape:
- Baseline:
- Change under test:

## Hypothesis
## Method and controls
## Results: throughput, p50/p95/p99, errors, resources, DB, queues, cost
## Confounders and limitations
## Decision
## Follow-up
```

## 23. Advanced labs

Each lab should be time-boxed and produce a short written report. Do not
merge experimental complexity into the mainline until it proves useful.

1.  **RabbitMQ vs Kafka:** implement a comparable workflow, measure
    throughput/latency and operational behavior, inspect replay and
    ordering needs, document the winner for the current workload.
2.  **Conventional persistence vs event sourcing:** model a bounded
    domain; compare audit/history, projection rebuilds, schema
    evolution, operational complexity, and query needs.
3.  **Modular monolith vs service extraction:** extract one module
    behind a versioned contract in a branch or lab environment; measure
    deployment independence, latency, failure modes, and operational
    overhead.
4.  **EF Core vs Dapper/raw SQL:** compare representative query plans
    and maintainability using realistic data; adopt raw SQL only for a
    demonstrated improvement.
5.  **Cache vs no cache:** measure read patterns, invalidation
    complexity, stale-data risk, and database load under failure.
6.  **AWS vs GCP:** deploy the same minimal
    API/worker/database/object-storage slice or build a comparable
    cost/operations worksheet; record assumptions and current pricing
    sources.
7.  **Managed containers vs Kubernetes:** compare scaling, rollout,
    networking, observability, security, upgrades, and operator effort.
    Keep Kubernetes optional.
8.  **Identity approaches:** compare managed OIDC and self-hosted
    alternatives using the same authorization requirements.
9.  **REST vs gRPC:** compare only across a real internal boundary;
    measure end-to-end behavior, not serialization microbenchmarks
    alone.
10. **Polling vs SignalR/SSE:** compare user experience,
    connection/resource costs, reconnect behavior, authorization, and
    deployment requirements.
11. **PostgreSQL search vs dedicated search:** use realistic query and
    indexing workloads before adding another stateful service.
12. **Failure drills:** disable the broker, delay provider responses,
    duplicate messages, rotate a secret, and restore a database backup.
    Record detection and recovery time.

For every lab, publish: question, hypothesis, setup, method, results,
trade-offs, decision, and what would cause the decision to change.

## 24. Technical-lead and architecture interview guide

Use TenantFlow as a source of concrete examples rather than memorized
definitions.

-   Why begin with a modular monolith, and what evidence would justify
    extracting a service?
-   How are module boundaries enforced in code and CI?
-   How does shared-schema multitenancy fail, and what layers prevent
    cross-tenant access?
-   Why is an EF Core global filter insufficient as the only tenant
    security control?
-   How does the outbox solve dual writes, and why can it still publish
    duplicates?
-   How do idempotency keys differ from message deduplication?
-   What does at-least-once delivery mean for a payment workflow?
-   How do you handle a payment timeout where the provider may have
    succeeded?
-   When is a saga appropriate, and why is compensation not a database
    rollback?
-   When would CQRS or event sourcing be justified, and what are their
    costs?
-   How do you safely deploy a schema change while old and new
    application versions coexist?
-   What isolation/concurrency strategy protects inventory from
    overselling?
-   How do you avoid retry storms and retry multiplication?
-   What are the golden signals for a synchronous API versus an
    asynchronous workflow?
-   How are SLOs tied to user outcomes and error budgets?
-   How do you design a load test that reflects real tenant/data
    behavior?
-   What operational evidence supports a move from managed containers to
    Kubernetes?
-   How would you compare RabbitMQ and Kafka for a specific workload?
-   How do you prove a restore procedure works, and how do you measure
    RPO/RTO?
-   How do supply-chain controls reduce risk in CI/CD?
-   How do you design support access without creating a platform-wide
    bypass?
-   How do you respond to suspected cross-tenant data exposure?
-   What are the consequences of high-cardinality labels in metrics?
-   How do you make imports and exports safe, resumable, and
    tenant-scoped?

For each answer, explain context, constraints, options, decision,
trade-offs, failure modes, and evidence. Be candid about what was
measured versus assumed.

## 25. Risk register

  -----------------------------------------------------------------------
  Risk                                Mitigation
  ----------------------------------- -----------------------------------
  Scope expands faster than delivery  Gated milestones, vertical slices,
                                      explicit P0/P1/P2/P3 priorities

  Premature microservices             Modular monolith and measurable
                                      extraction criteria

  Tenant data leak                    Defense in depth, tenant-aware
                                      constraints, negative tests, audit,
                                      optional RLS

  Duplicate or lost workflow effects  Outbox, idempotency, dedupe,
                                      reconciliation, backlog monitoring

  Ambiguous payment outcomes          Unknown state, provider
                                      reconciliation, no blind retries

  Technology collection without value Every advanced component requires a
                                      use case, ADR, and operational
                                      owner

  False performance claims            Workload model, repeatable k6
                                      baselines, transparent assumptions

  Fragile deployments                 Expand-contract migrations,
                                      immutable artifacts, smoke gates,
                                      progressive rollout

  Cloud choice becomes premature      Short comparison exercise and
                                      provisional decision with revisit
                                      triggers

  Security/compliance overclaim       Document controls and residual
                                      risks; do not claim certification

  Operational gaps hidden by          Fault injection, runbooks, restore
  happy-path tests                    drills, alert validation

  Documentation becomes stale         Update docs as part of
                                      feature/release definition of done
  -----------------------------------------------------------------------

## 26. Immediate next actions

Start with the smallest coherent foundation:

1.  Create the monorepo, pin the currently supported .NET LTS SDK,
    centralize package versions, and enable analyzers/formatting.
2.  Write ADR-0001 for modular monolith boundaries and ADRs for tenancy,
    identity, and cloud/broker decisions as they become actionable.
3.  Build the API, worker, and migration hosts with structured logging,
    OpenTelemetry, health checks, and configuration validation.
4.  Add Docker Compose for PostgreSQL and the minimum local
    dependencies.
5.  Implement tenant context and authorization before adding business
    data.
6.  Build the first vertical slice: tenant-scoped catalog → inventory
    reservation → order creation, with real PostgreSQL integration
    tests.
7.  Prove tenant isolation and concurrency failure cases before adding
    messaging.
8.  Add the outbox and broker, then implement the sandbox payment
    workflow with idempotency and reconciliation.
9.  Establish dashboards, workload assumptions, and the first k6
    baseline before performance tuning.
10. Add cloud infrastructure and progressive deployment only after the
    local path and CI are repeatable.

## 27. Final architectural position

TenantFlow should demonstrate **disciplined evolution**, not maximum
technology count. The strongest portfolio result is a coherent system
whose boundaries, data invariants, tenant isolation, asynchronous
workflows, security controls, tests, telemetry, deployment, and recovery
procedures are all explainable and evidenced.

The primary path is:

**Modular monolith → strong tenant/security boundaries → reliable
transactional workflows → outbox and idempotent messaging → production
observability → measured performance and resilience → safe cloud
delivery → proven recovery → selective advanced labs.**

Every requested capability remains on the roadmap. The implementation
sequence protects the project from becoming a collection of unfinished
subsystems, while the ADRs and labs preserve the learning value of
comparing alternatives.
