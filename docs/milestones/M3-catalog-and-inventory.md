# M3 — First vertical slice: catalog and inventory

**Goal:** tenant-scoped catalog and inventory reads and writes, end to end, against real PostgreSQL, with row-level
security as a database backstop and stock that can never go negative.

**Gate (roadmap):** API contract, database constraints, integration tests, and OpenAPI docs pass.
Spec: [SPEC-0003](../specs/0003-catalog-and-inventory.md). Decisions: [ADR-0019](../architecture/decisions/0019-m3-conventions.md) (Accepted 2026-10-21).

## 1. What was built

```text
src/
├── BuildingBlocks/Persistence/         TenantScopedTransaction: set_config('app.tenant_id', ..., true) per transaction
├── Hosts/Api/Endpoints/V1/
│   ├── TenantAccess/                   RequireTenantPermission, TenantAccessFilter, permission metadata
│   ├── Tenants/Members/ Products/ Stock/   each feature: *Endpoints.cs + Schemas/ (requests, responses)
│   └── Extensions/OpenApiExtensions.cs /openapi/v1.json when ApiDocumentation:Enabled
└── Modules/
    ├── Catalog/      Domain, Application (Commands/ Validators/ Dtos/ Services/), Infrastructure, Contracts (IProductCatalog)
    └── Inventory/    Domain, Application, Infrastructure
docs/api/openapi-v1.json                the reviewed API contract (snapshot)
tests/Architecture/                     module boundaries, layer direction, one schema per DbContext
tests/Catalog/, tests/Inventory/        domain rules and validators
tests/Database/{Catalog,Inventory}/     endpoint tests on real PostgreSQL; Support/ shared tenant harness and container fixture
```

| Slice | Commit | Delivered |
|---|---|---|
| S1 | `b4865db` | tenant-scoped transaction, `catalog.products` with forced RLS, RLS tests, ADR-0019 drafted |
| S2 | `01ac870` | products: create, get, list (keyset), update (ETag), SKU unique per tenant, catalog permissions, 412 error kind |
| Cleanup | `db139f7` | one type per file, permission names in `IdentityAccess.Contracts`, layered folders, architecture tests |
| S3 | `4012deb` | Inventory: balances and append-only adjustments, atomic non-negative update, Catalog contract lookup |
| S4 | `1d2c97a` | OpenAPI document and snapshot contract test, Problem Details per route, Testcontainers CI path |

## 2. Architectural reasoning

**Two independent controls for tenant isolation.** The application filters every query by the tenant context's id;
PostgreSQL row-level security filters again. The gate proof removes each alone (tests still pass) and both together
(the cross-tenant tests fail). That is what "defence in depth" means in a test, not in a slide.

**Context per transaction, never per session.** `TenantScopedTransaction` runs `set_config(..., is_local => true)` as
the first statement, so the value dies at commit and a pooled connection cannot carry it to the next request. The
policies use `nullif(current_setting('app.tenant_id', true), '')::uuid`: after one tenant transaction a pooled
connection reads `''`, not null, and without `nullif` it would error instead of failing closed. The pooled-leak test
caught exactly this.

**Stock is changed by one conditional statement.** `UPDATE ... SET on_hand = on_hand + @d WHERE ... AND on_hand + @d >= 0`.
Concurrent adjustments queue on the row lock and each re-checks the condition against the committed value, so 20
concurrent decrements of 10 units succeed exactly 10 times. A `CHECK (on_hand >= 0)` backs it up: without the
condition the database still refuses, but the caller gets a 500 instead of a 409, which is why both exist.

**Modules meet only through contracts, and a test says so.** Inventory asks `Catalog.Contracts.IProductCatalog`
whether a product exists in the current tenant; it never reads `catalog.products`, and there is no cross-schema
foreign key. `tests/Architecture` reads the compiled assemblies' references, so a stray `using` fails the build.

**Optimistic concurrency on products uses PostgreSQL's `xmin`.** The ETag is the row version; `If-Match` is compared
when the row is read and again by the UPDATE itself, so an edit between the read and the write still loses (412).

**The contract is a file.** The OpenAPI document is generated from the endpoints and compared with
`docs/api/openapi-v1.json`. Any difference fails; accepting it is a deliberate snapshot update reviewed in the diff.
Tenant routes document their 401/403/404 once, in `RequireTenantPermission`.

## 3. Security considerations

- New tenant-owned tables (`catalog.products`, `inventory.stock_balances`, `inventory.stock_adjustments`) all have
  `ENABLE` + `FORCE ROW LEVEL SECURITY` with `USING` and `WITH CHECK`; a test lists every table with a `tenant_id`
  in a module schema and fails if any lacks a policy.
- The application role still owns nothing and has no `BYPASSRLS`; stock adjustments are append-only for it.
- Adjustments record the actor as a membership id, never a name or email.
- Customers read active products only; stock is visible to administrators only (inventory permissions).
- The OpenAPI document is not served in a deployed API unless explicitly enabled.
- Residual risk: list queries and the product-existence check are two transactions; there is no product deletion, so
  a product cannot disappear between them today. Revisit when deletion arrives.

## 4. Verification

Fresh evidence for the gate (2026-10-21, commit `1d2c97a`): see the exit checklist and `docs/progress/progress.json`.

| Command | Result |
|---|---|
| `./scripts/verify.ps1 -Database` (code of `1d2c97a`) | all checks passed; Api 125/125, IdentityAccess 67/67, Tenants 77/77, Catalog 7/7, Inventory 5/5, Architecture 5/5, Worker 12/12, Database 134/134; 0 warnings |
| `./scripts/progress.ps1 -Verify` (CI test command with coverage) | quality run passed for `1d2c97a`, 0 warnings; compose valid |
| `StockEndpointTests` (concurrency, timing-sensitive) | 3 runs in a row, 6/6 each |

**Break-it checks re-run for the gate** (fault injected, test fails at an assertion, fault restored):
- Cross-tenant: the product store's tenant filter **and** the RLS policy removed: 4 of 7 `ProductEndpointTests` fail,
  including `Another_tenants_products_cannot_be_read_listed_or_changed`. With only one of the two removed they pass (S2).
- No oversell: the conditional update **and** the CHECK constraint removed: the negative-stock and 20-way concurrency
  tests fail.
- Database constraint: `FORCE ROW LEVEL SECURITY` removed from the inventory tables: the "forced on every tenant-owned
  table" test fails.

Per-slice proofs are in the spec's Verification table and the tracker.

**Not verified:** the Testcontainers path runs only where Docker is reachable directly (CI on Linux); CI is on hold, so
it has never run (Open Decision 21, ADR-0019).

## 5. Trade-offs and assumptions

- Compose PostgreSQL locally, Testcontainers in CI (ADR-0019); the CI path is unproven until CI runs.
- Keyset pagination over `(sku, id)`: stable under inserts, no page numbers. The cursor format is now a contract.
- Endpoints and tests stay in the host and in `tests/`; moving them into each module is an M14 decision (one ADR).
- The tenant id stays in the URL (your decision, 2026-10-21): it only selects; authority is the membership.
- Validation stays in the Application layer (ADR-0014), not an HTTP filter, so every caller gets it.
- Currency is checked as three letters, not against the ISO 4217 list. Prices are `numeric(19,4)`.
- A missing `If-Match` is 400 `catalog.if_match_required`. Archived products can still have stock adjusted.
- The roadmap's "separate startup endpoint once asynchronous initialization exists" is not due: nothing initialises
  asynchronously yet.

## 6. Mentor exercises

1. Remove `nullif` from the policy and run `RowLevelSecurityTests`. Why does only the pooled-connection test notice?
2. Replace the conditional UPDATE with "read the balance, check, write". How many of the 20 concurrent decrements
   succeed? What does the CHECK constraint turn the bug into?
3. Add a `Reporting` module that needs product names. What goes into `Catalog.Contracts`, and which architecture test
   fails if you take a shortcut?
4. Change `ProductResponse` by adding a field, then by renaming one. Which is allowed in `/v1`, and what does the
   snapshot test make you do in each case?
5. Why does a non-manager get 404, not 403, for an archived product?

## 7. Interview talking points

- "How does RLS work with connection pooling?" Transaction-local `set_config`, `FORCE ROW LEVEL SECURITY`, a role
  without `BYPASSRLS`, and a test that runs the next transaction on the same physical connection.
- "How do you prevent overselling?" One conditional UPDATE under the row lock, a CHECK constraint behind it, and a real
  concurrency test that fails when the condition is removed.
- "How do you keep a modular monolith modular?" Contracts projects, schema per module, no cross-schema keys, and
  architecture tests over compiled references.
- "How do you version an API safely?" URL versioning, an additive-only policy for `/v1`, and a committed OpenAPI snapshot.

## 8. Exit checklist

- [x] API contract: `OpenApiContractTests` (snapshot and problem responses) pass
- [x] Database constraints: RLS, unique SKU per tenant, non-negative stock, append-only adjustments tested on PostgreSQL
- [x] Integration tests: Catalog and Inventory endpoint tests and the 11-route isolation matrix pass on real PostgreSQL
- [x] OpenAPI docs: generated and committed at `docs/api/openapi-v1.json`
- [x] SPEC-0003: all 20 criteria have verification rows
- [x] ADR-0019 accepted by you on 2026-10-21 (closes Open Decision 21 and the API compatibility item)
- [ ] **You:** review this guide and the M3 commits on branch `m3-catalog-inventory`
- [ ] **You:** push and open a pull request so CI runs the gate, including the Testcontainers path (on hold until you raise it)
- [ ] **You:** approve the start of M4 (order and inventory consistency)
