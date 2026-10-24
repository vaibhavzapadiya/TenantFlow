# SPEC-0003: Catalog and inventory (first vertical slice)

- Status: Implemented
- Milestone: M3
- Owner: TenantFlow tech lead (user)
- Related: blueprint sections 5 (modules), 8 (tenancy), 9 (API), 15 (testing); ADR-0002, ADR-0010, ADR-0011, ADR-0012, ADR-0015, ADR-0016; Open Decision 21; threat model 0001; SPEC-0002

## Outcome
A tenant administrator manages the tenant's products (Catalog) and their stock (Inventory) over a
versioned REST API; customers read the catalog. Each tenant sees only its own rows, enforced by the
application and, as a backstop, by PostgreSQL row-level security. Stock can never go negative, even
under concurrent adjustments.

## Acceptance criteria
Tenant routes are `/v1/tenants/{tenantId}/...`; the tenant comes from the membership (SPEC-0002), never from the route text.

**RLS foundation**
1. Given a tenant-owned table, when a raw query runs with `app.tenant_id` = A, then tenant B's rows are neither readable nor writable (update/delete touch 0 rows, insert for B is rejected).
2. Given no tenant set, then a select returns no rows and an insert is rejected.
3. Given a transaction for A committed on a pooled connection, when the next transaction runs without a tenant on that connection, then it sees no rows.
4. Then the application role has no `BYPASSRLS`, is not superuser, and owns none of the module tables (`pg_roles`, `pg_tables`).

**Catalog**
5. Given an admin, when they create a product (sku, name, price amount + currency), then 201 with `Location`; the product is readable by id.
6. Given sku `ABC` exists in the tenant, when an admin creates `abc` (case-insensitive), then 409 `catalog.sku_taken`; the same sku in another tenant succeeds.
7. Given invalid input (blank sku/name, negative price, currency not a 3-letter ISO code, price with >4 decimals), then 400 Problem Details with field errors and no row written.
8. Given a product, when an admin updates name/price/status (active/archived) with the current `ETag` in `If-Match`, then 200 and a new ETag; with a stale ETag, 412 `catalog.version_conflict`. SKU is immutable.
9. Given a customer, when they list or get products, then only `active` products are returned; when they create/update, then 403 `membership.permission_denied`.
10. Given tenant A's admin, when they get/update/list tenant B's product id (route tenant A), then 404 `catalog.product_not_found`, no data, no change; listing never includes B's rows.

**Inventory**
11. Given a product, when an admin adjusts stock by a signed delta with a reason, then the balance changes by exactly that delta and the new balance is returned; reading a product without a balance row yields on-hand 0.
12. Given on-hand 3, when an adjustment of -4 is submitted, then 409 `inventory.insufficient_stock` and the balance stays 3.
13. Given on-hand 10, when 20 concurrent adjustments of -1 run, then exactly 10 succeed, the rest are 409, and the balance is 0 (real PostgreSQL).
14. Given a product id that does not exist in the caller's tenant (unknown, or another tenant's), when stock is adjusted or read, then 404 `catalog.product_not_found` and no balance row is created.
15. Given a customer, when they read or adjust stock, then 403.
16. Given tenant B's balance, when tenant A's admin reads or adjusts it by B's product id, then 404 and B's balance is unchanged.

**API and contracts**
17. Given a list request, then results are keyset-paged (`limit` default 20, max 100, `cursor` opaque), ordered by `(sku, id)`; following `nextCursor` yields every product once, including when rows are inserted between pages; a malformed cursor or limit >100 gives 400.
18. Then the generated OpenAPI document matches the committed `docs/api/openapi-v1.json` snapshot (a breaking diff fails the test; an additive diff requires updating the snapshot deliberately), and every M3 operation documents its Problem Details responses.
19. Then architecture tests prove: modules reference each other only through `*.Contracts`; no module's Domain/Application references Infrastructure of another; each `DbContext` maps only its own schema; Catalog and Inventory have no project reference to each other's non-Contracts projects.
20. Given anonymous or platform-operator callers, then the new routes answer as for all tenant routes (401/403 per SPEC-0002; covered by the existing route-table isolation matrix extended to the new routes).

## Invariants
- A tenant-owned row is never visible or writable outside its tenant: application filter AND RLS.
- `on_hand >= 0` at the database (CHECK) and in the conditional update; no read-then-write stock logic.
- `(tenant_id, lower(sku))` is unique; SKU never changes.
- Money is `numeric(19,4)` plus an ISO-4217 currency; never float/double. No currency conversion or arithmetic across currencies.
- No cross-schema foreign keys; Inventory refers to a product by id and checks existence through `Catalog.Contracts`.
- Tenant-owned use cases take no tenant id and throw when no tenant context is set (SPEC-0002).

## Contract
| Operation | Route | Permission |
|---|---|---|
| List products | `GET /v1/tenants/{t}/products?limit&cursor` | `catalog.read` (customers see active only; admins see all, optional `status` filter) |
| Get product | `GET /v1/tenants/{t}/products/{id}` | `catalog.read` |
| Create | `POST /v1/tenants/{t}/products` | `catalog.manage` |
| Update | `PUT /v1/tenants/{t}/products/{id}` (`If-Match`) | `catalog.manage` |
| Get stock | `GET /v1/tenants/{t}/products/{id}/stock` | `inventory.read` |
| Adjust stock | `POST /v1/tenants/{t}/products/{id}/stock/adjustments` `{delta, reason}` | `inventory.manage` |

Permissions are added to `Permissions`/`RolePermissions`: TenantAdmin gets all four new ones, Customer only `catalog.read`.
Errors follow ADR-0016; new codes: `catalog.sku_taken`, `catalog.product_not_found`, `catalog.version_conflict`, `inventory.insufficient_stock`. Product resource: `id, sku, name, price{amount,currency}, status, etag`. Stock: `productId, onHand`.

## Data
- Schema `catalog`: `products(id uuid pk, tenant_id uuid not null, sku text, name text, price_amount numeric(19,4), price_currency char(3), status text, xmin as the row version, created_at, updated_at)`; unique index `(tenant_id, lower(sku))`; index `(tenant_id, sku, id)` for the cursor; CHECK price >= 0.
- Schema `inventory`: `stock_balances(tenant_id uuid, product_id uuid, on_hand bigint check (on_hand >= 0) (widened from integer after the M3 code review, migration WidenStockOnHand), updated_at, pk(tenant_id, product_id))`; `stock_adjustments(id, tenant_id, product_id, delta, reason, actor, created_at)` append-only for the app role (same pattern as membership audit), written in the same transaction as the balance change.
- RLS (ADR-0011) on all three tables: `ENABLE` + `FORCE`, policy `USING`/`WITH CHECK` on `tenant_id = current_setting('app.tenant_id', true)::uuid`. A shared unit-of-work step in BuildingBlocks begins the transaction and runs `set_config('app.tenant_id', @t, true)` first.
- Migrations: new, additive, applied by the Migrator; no applied migration is edited.

## Failure modes
- Concurrent adjustments: single conditional `UPDATE ... SET on_hand = on_hand + @d WHERE on_hand + @d >= 0`; zero rows updated means 409 (first adjustment upserts the balance row inside the same statement path).
- Concurrent product updates: `row_version` compare-and-set; loser gets 412.
- Duplicate SKU race: unique index is the arbiter; violation maps to 409.
- Database unavailable: 503 per ADR-0016; no partial writes (balance and adjustment row share a transaction).
- No remote calls occur inside these transactions (rule 3).

## Out of scope
Variants, pricing rules, reservations/release/commit (M4), product deletion, outbox events, search, multi-warehouse stock, import/export, per-tenant currency policy.

## Open questions
1. Delete: archive-only (proposed, no hard delete in M3). OK?
2. Adjusting stock of an archived product: allowed (proposed) or 409?
3. Stock read for customers: hidden (proposed, 403); a later "in stock" boolean is additive.
4. Needs ADR (Proposed, one combined "M3 conventions" ADR-0019): Open Decision 21 (Compose locally, Testcontainers in CI), RLS wiring, keyset pagination, OpenAPI snapshot, additive-only v1 compatibility policy. Drafted as ADR-0019 (Proposed) in S1.

## Slices
| Slice | Delivers | Criteria |
|-------|----------|----------|
| S1 | Unit-of-work tenant transaction + RLS on `catalog.products` (first table, minimal Catalog domain/migration), role checks, ADR-0019 draft | 1-4, plus 10 (storage level) |
| S2 | Catalog module: create/get/list/update use cases and their endpoints, SKU uniqueness, ETag, keyset paging, permissions | 5-10, 17 |
| S3 | Inventory module: balances, adjustments, atomic non-negative rule, contract call to Catalog | 11-16 |
| S4 | OpenAPI snapshot, Testcontainers CI fixture path (architecture tests landed with the S2 structure cleanup) | 18, 20 (the route matrix grows with each slice's routes) |

## Verification
Filled in during implementation.

| Criterion | Test | Proven to fail without the change |
|-----------|------|------------------------------------|
| 1, 10 (storage) | `RowLevelSecurityTests.A_tenant_scope_reads_and_writes_only_its_own_rows` | yes: policy `USING (true)` fails it |
| 2 | `RowLevelSecurityTests.Without_a_tenant_no_row_is_visible_and_no_row_can_be_written` | yes: policy `USING (true)` and no-`nullif` fail it |
| 3 | `RowLevelSecurityTests.A_pooled_connection_does_not_keep_the_previous_tenant` | yes: without `nullif` the pooled read errors; `USING (true)` fails it |
| 5 | `ProductEndpointTests.An_administrator_creates_a_product_and_reads_it_back` | yes: with RLS context unset every request fails closed |
| 6 | `ProductEndpointTests.A_sku_is_unique_within_a_tenant_ignoring_case_but_not_across_tenants` | yes: unique index removed fails it |
| 7 | `ProductRuleTests` (rules, 7 cases) and `ProductEndpointTests.Invalid_input_is_a_400_with_field_errors_and_writes_nothing` | no separate proof (validation only) |
| 8 | `ProductEndpointTests.An_edit_needs_the_current_etag_and_a_stale_one_changes_nothing` | yes: version compare removed fails it |
| 9 | `ProductEndpointTests.A_customer_sees_active_products_only_and_cannot_write` | yes: archived made visible fails it |
| 10 | `ProductEndpointTests.Another_tenants_products_cannot_be_read_listed_or_changed`; `IsolationMatrixTests` (9 tenant routes) | yes: passes with only the application filter removed or only the policy removed (two controls), fails with both removed |
| 17 | `ProductEndpointTests.Paging_by_cursor_returns_every_product_once_even_when_rows_arrive_between_pages` | no separate proof (paging) |
| 11 | `StockEndpointTests.Stock_starts_at_zero_and_moves_by_exactly_each_signed_delta`; `StockAdjustmentRuleTests` (rules) | no separate proof (arithmetic) |
| 12 | `StockEndpointTests.An_adjustment_that_would_go_negative_is_a_409_and_changes_nothing` | yes: conditional update removed (CHECK kept: 500, not 409) and both removed each fail it |
| 13 | `StockEndpointTests.Twenty_concurrent_decrements_of_ten_units_succeed_exactly_ten_times` (3 runs in a row) | yes: conditional update and CHECK removed fails it |
| 14 | `StockEndpointTests.Stock_of_an_unknown_product_is_404_and_creates_no_balance` | yes: product existence check removed fails it |
| 15 | `StockEndpointTests.A_customer_can_neither_read_nor_adjust_stock`; `IsolationMatrixTests` | covered by the route boundary (proven in M2) |
| 16 | `StockEndpointTests.Another_tenants_stock_cannot_be_read_or_changed`; `IsolationMatrixTests` (11 routes); RLS forced on both inventory tables (`RowLevelSecurityTests`) | yes: product existence check removed fails it |
| 18 | `OpenApiContractTests` (snapshot, problem responses) | yes: renaming `nextCursor` fails the snapshot; removing the boundary problem metadata fails both |
| 20 | `IsolationMatrixTests` (all 11 tenant routes read from the route table), `RouteTableTests` | yes in M2 (matrix); the new routes are picked up automatically |
| 19 | `ModuleBoundaryTests` (4 tests), `ModuleSchemaTests` | yes: a Catalog.Infrastructure reference to Tenants.Domain fails `A_module_uses_another_module_only_through_its_contracts` |
| 4 | `RolePrivilegeTests` (now includes `catalog`), `RowLevelSecurityTests.Row_level_security_is_forced_on_the_table` | yes: `FORCE` removed fails the flag test |
