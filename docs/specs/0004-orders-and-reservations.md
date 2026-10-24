# SPEC-0004: Orders and inventory reservations

- Status: Implemented (approved 2026-10-21 by the user)
- Milestone: M4
- Owner: TenantFlow tech lead (user)
- Related: blueprint sections 6 (ownership), 7 (state transitions, order lifecycle), 10 (concurrency), 11 (sagas), 14 (failure); ADR-0002, ADR-0011, ADR-0012, ADR-0016, ADR-0019, ADR-0020 (Accepted); SPEC-0003

## Outcome
A customer places an order for the tenant's products. Submitting it reserves stock, so two customers can never be
promised the same last unit; an order the stock cannot cover is rejected. Order lines keep the price, currency and
name they were ordered at. Customers see only their own orders; tenant administrators see and can cancel all of them.
Payment (M6) will move a reserved order on; M4 stops at `PendingPayment`.

## Acceptance criteria
Tenant routes are `/v1/tenants/{tenantId}/...`; tenant and caller come from the membership (SPEC-0002).

**Reservations (Inventory)**
1. Given on-hand 5 and nothing reserved, when an order reserves 3, then the stock read shows `onHand 5, reserved 3, available 2`.
2. Given an order with two lines where one line exceeds available stock, when it is reserved, then it fails with insufficient stock and nothing is reserved for either line (all-or-nothing).
3. Given an order already reserved, when reserve is called again for it, then nothing changes; when release is called twice, then the units are given back once.
4. Given on-hand 5 and reserved 3, when an admin adjusts by -3, then 409 `inventory.insufficient_stock` and nothing changes; -2 succeeds (stock never drops below what is reserved).
5. Given on-hand 1, when 20 orders for 1 unit are submitted concurrently, then exactly one ends `PendingPayment`, 19 end `Rejected`, and reserved is 1 (real PostgreSQL; the M4 gate).

**Orders**
6. Given a customer, when they create an order with lines `{productId, quantity}`, then 201 `Draft` with `Location` and `ETag`; each line holds the product's sku, name, unit price and currency at that moment, a line total, and the order total. A later catalog price change does not change the order.
7. Given invalid lines (none, more than 50, quantity < 1 or > 10 000, a product twice), then 400 with field errors; an unknown or other-tenant product gives 404 `catalog.product_not_found`; an archived product 409 `orders.product_unavailable`; products in different currencies 409 `orders.mixed_currency`. Nothing is written.
8. Given a `Draft` order, when its owner replaces the lines with the current `ETag` in `If-Match`, then 200 and a new ETag; a stale ETag gives 412 `orders.version_conflict`, a missing one 400 `orders.if_match_required`.
9. Given a `Draft` order and enough stock, when its owner submits it, then it is `PendingPayment` and its quantities are reserved. Without enough stock, it is `Rejected` with `rejectionReason: insufficient_stock` and nothing is reserved.
10. Given a `Draft` order, when it is cancelled, then it is `Cancelled`; given a `PendingPayment` order, when it is cancelled, then it is `Cancelled` and its reservation is released (available restored).
11. Given any state, when a transition not in the state table is requested (submit a `PendingPayment`, `Rejected` or `Cancelled` order; cancel a `Rejected` or `PendingReservation` order; edit lines after `Draft`), then 409 `orders.invalid_transition` and the order and stock are unchanged.
12. Given an order left in `PendingReservation` after its stock was reserved (the last step failed), when it is submitted again, then it becomes `PendingPayment` without reserving twice; given a `Cancelled` order whose release did not run, when it is cancelled again, then the units are released and the order stays `Cancelled`.
13. Given customer X's order, when customer Y of the same tenant gets, edits, submits or cancels it, then 404 `orders.order_not_found` and nothing changes. A tenant admin can get and cancel any order of the tenant, but edits and submits only their own.
14. Given tenant B's order, when tenant A's admin uses its id under tenant A, then 404, no data, no change; RLS is forced on every new table.

**Contracts and structure**
15. Then the OpenAPI snapshot includes the order routes and the additive stock fields; the isolation matrix covers the new routes; architecture tests include Orders, which references other modules only through `Catalog.Contracts`, `Inventory.Contracts` and `IdentityAccess.Contracts`.

## Invariants
- `0 <= reserved <= on_hand` at the database (CHECK) and in each conditional update; no read-then-write.
- An order holds at most one active reservation per product; reserve and release are idempotent by order id.
- A state change is one conditional update (`WHERE status = @expected`); two racing commands cannot both win.
- Money is `numeric(19,4)` plus currency; an order has one currency; totals are computed once, from the snapshot.
- No cross-schema foreign keys; Orders reaches Catalog and Inventory only through their `Contracts`.

## Contract
| Operation | Route | Rule |
|---|---|---|
| Create | `POST /v1/tenants/{t}/orders` `{lines:[{productId,quantity}]}` | `orders.place`; owner = caller's membership |
| Get | `GET /v1/tenants/{t}/orders/{id}` | owner with `orders.place`, or `orders.manage` |
| Replace lines | `PUT /v1/tenants/{t}/orders/{id}/lines` (`If-Match`) | owner, `Draft` only |
| Submit | `POST /v1/tenants/{t}/orders/{id}/submit` | owner |
| Cancel | `POST /v1/tenants/{t}/orders/{id}/cancel` | owner, or `orders.manage` |
| Get stock | `GET .../products/{id}/stock` | unchanged; adds `reserved`, `available` |

Permissions (in `TenantPermissions`): `orders.place` (Customer and TenantAdmin), `orders.manage` (TenantAdmin).
Order resource: `id, status, currency, total{amount,currency}, lines[{productId, sku, name, unitPrice{amount,currency}, quantity, lineTotal}], rejectionReason?, createdAt, etag`.
Submit and cancel return 200 with the order; a rejection is a business outcome (200, status `Rejected`), not an error.
New error codes: `orders.order_not_found`, `orders.invalid_transition`, `orders.version_conflict`, `orders.if_match_required`, `orders.product_unavailable`, `orders.mixed_currency`, and `orders.total_too_large` (409, added in S2: a total beyond `numeric(19,4)`); line field errors `orders.lines.*` and `orders.line.*`.

`Inventory.Contracts.IStockReservations`: `ReserveAsync(orderId, lines)` returns reserved or insufficient; `ReleaseAsync(orderId)`.
`Catalog.Contracts.IProductCatalog` gains a read of order snapshots (sku, name, price, currency, active) for a set of ids.

## Data
- `inventory.stock_balances`: add `reserved bigint not null default 0`, CHECK `reserved >= 0 AND reserved <= on_hand`. The adjustment condition becomes `on_hand + @d >= reserved`.
- `inventory.reservations(tenant_id, order_id, product_id, quantity bigint > 0, status reserved|released, created_at, released_at, pk(tenant_id, order_id, product_id))`; no foreign key to Orders.
- Schema `orders`: `orders(id pk, tenant_id, customer_membership_id, status, currency char(3), total_amount numeric(19,4), rejection_reason, created_at, updated_at, xmin as row version)`, index `(tenant_id, customer_membership_id)`; `order_lines(id identity pk, tenant_id, order_id fk, line_no, product_id, sku, name, unit_price numeric(19,4) >= 0, quantity int 1..10000, line_total numeric(19,4))`, index `(order_id)`: a surrogate key so replacing a draft's lines deletes and inserts rows without key collisions; `sku` and `name` are sized from `Catalog.Contracts.ProductLimits`.
- RLS on all new tables (`ENABLE` + `FORCE`, ADR-0019 policy). New additive migrations; the Migrator, role grants, and schema-ownership tests gain `orders`.

## Failure modes
- **Submit is three transactions** (ADR-0020): (1) Orders `Draft -> PendingReservation`, commit; (2) Inventory reserve, its own transaction, lines in product-id order to avoid deadlocks; (3) Orders `-> PendingPayment` or `Rejected`. A crash after (1) or (2) leaves `PendingReservation`; submitting again repeats (2) idempotently and finishes (3) (criterion 12). An automatic sweeper waits for the M5/M6 saga.
- **Cancel** of `PendingPayment`: Orders `-> Cancelled`, commit, then release. A crash in between leaves units reserved; cancelling again releases them.
- **A line edit racing a submit**: the submit's `Draft -> PendingReservation` update also requires the row version it read, so it reserves exactly the committed lines; a lost race re-reads the order.
- **Concurrent commands on one order**: every state change is conditional on the expected state, so one wins. A concurrent submit that finds `PendingReservation` helps finish it idempotently; one that finds a later state gets 409. A cancel that answered 200 always sticks (tested over 20 racing rounds).
- **Concurrent lines edit**: `xmin` compare-and-set, loser 412. **Database down**: 503 (ADR-0016), no partial write within a transaction.
- No remote calls inside a transaction (rule 3); the Inventory call is in-process but in its own transaction.

## Out of scope
Payment and the states after `PendingPayment` (M6); committing a reservation into an on-hand decrement (M6); reservation expiry and the recovery sweeper (M5/M6 saga); order listing and search; re-pricing at submit; outbox events (M5).

## Open questions
1. Snapshot timing: prices are taken when lines are set (create/replace) and not re-checked at submit, so a product archived after the draft still reserves (decided 2026-10-21).
2. A rejected order is final; the customer creates a new order rather than editing and resubmitting (decided).
3. Order list endpoint deferred; get by id only in M4 (decided).
4. **Needs ADR:** state table, reservation model, the three-transaction flow and its recovery by repetition: drafted as [ADR-0020](../architecture/decisions/0020-orders-and-reservations.md) (Accepted 2026-10-21), from your choices of that day.

## Slices
| Slice | Delivers | Criteria |
|-------|----------|----------|
| S1 | Inventory reservations: `reserved` column, `inventory.reservations`, `Inventory.Contracts.IStockReservations`, adjustments respect reserved, stock fields; concurrent last-unit test at the contract level (riskiest assumption first) | 1-4, 14 (reservations table) |
| S2 | Orders module: schema with RLS, order aggregate and state table, create/get/replace lines with snapshot and ETag, permissions, Catalog snapshot contract | 6-8, 11 (edit), 13-15 |
| S3 | Submit and cancel through the three-step flow, recovery by repetition, illegal transitions, 20-way concurrent last-item HTTP test | 5, 9-12, 13 (submit/cancel), 15 |

## Verification
Filled in during implementation.

| Criterion | Test | Proven to fail without the change |
|-----------|------|------------------------------------|
| 1 | `StockReservationTests.A_reservation_holds_units_and_the_stock_read_shows_reserved_and_available` | no separate proof (read model) |
| 2 | `StockReservationTests.A_reservation_is_every_line_or_none` | yes: reservation condition and CHECK removed fails it |
| 3 | `StockReservationTests.Reserving_or_releasing_an_order_twice_counts_once`, `...The_same_order_reserved_concurrently_holds_its_units_once` | yes: idempotency guard removed fails both; release not limited to held rows fails the first (CHECK violation at runtime) |
| 4 | `StockReservationTests.An_adjustment_cannot_take_stock_below_what_is_reserved` | yes: adjustment condition back to `>= 0` fails it (CHECK turns it into a 500) |
| 5 (contract level) | `StockReservationTests.Twenty_concurrent_orders_for_the_last_unit_reserve_it_exactly_once` | yes: reservation condition and CHECK removed fails it |
| 14 (reservations) | `StockReservationTests.Another_tenants_stock_cannot_be_reserved_or_released`; `RowLevelSecurityTests` (forced on 4 tables; reservations not deletable) | no separate proof (same two controls as M3) |
| 6 | `OrderEndpointTests.An_order_snapshots_its_products_and_a_later_price_change_does_not_touch_it`; `OrderRuleTests.A_draft_snapshots_its_lines_and_computes_the_total_once` | no separate proof (snapshot) |
| 7 | `OrderEndpointTests.Invalid_lines_are_refused_and_nothing_is_written`; `OrderRuleTests` (validator, currency, total) | no separate proof (validation) |
| 8 | `OrderEndpointTests.Lines_change_only_with_the_current_etag` | yes: version compare removed in the service and the store fails it |
| 11 (edits) | `OrderEndpointTests.An_order_past_the_draft_stage_cannot_be_edited`; `OrderStateMachineTests` (whole table) | yes: the EditLines state check removed fails it |
| 13 (read, edit) | `OrderEndpointTests.Another_customers_order_does_not_exist_for_them_and_an_admin_reads_but_does_not_edit_it` | yes: ownership removed from the read check, or from the edit check, fails it |
| 14 (orders) | `OrderEndpointTests.Another_tenants_order_cannot_be_read_or_changed`; `RowLevelSecurityTests` (forced on 6 tables); `IsolationMatrixTests` (14 routes) | yes: store tenant filter and RLS policy both removed fails it |
| 15 (S2 part) | `OpenApiContractTests` (snapshot updated, additive), `RouteTableTests`, `ModuleBoundaryTests` (Orders added), `SchemaOwnershipTests`, `RolePrivilegeTests` | covered by existing guards (proven in M3) |
| 5 (the M4 gate) | `OrderLifecycleTests.Twenty_concurrent_submits_for_the_last_unit_reserve_it_exactly_once` (3 runs in a row) | yes: reservation condition and CHECK removed fails it |
| 9 | `OrderLifecycleTests.Submitting_reserves_the_stock_or_rejects_the_order_without_reserving_anything` | yes: reservation condition and CHECK removed fails it |
| 10 | `OrderLifecycleTests.Cancelling_a_draft_or_a_reserved_order_cancels_it_and_gives_the_stock_back` | yes: release removed from cancel fails it |
| 11 (the M4 gate) | `OrderLifecycleTests.A_transition_the_state_table_does_not_allow_is_a_409_and_changes_nothing`, `...A_draft_cancelled_and_submitted_at_once_ends_in_one_consistent_state` (20 racing rounds); `OrderStateMachineTests` | yes: cancel allowed from any state fails the first; the transition's state condition removed fails the race test. The explicit submit check alone removed: the illegal-transition test still passes because the domain's `NextOrThrow` also refuses (two layers) |
| 12 | `OrderLifecycleTests.Repeating_an_interrupted_submit_or_cancel_finishes_it_without_counting_twice` | yes: release removed fails it; reservation idempotency proven in S1 |
| 13 (submit, cancel) | `OrderLifecycleTests.Only_the_owner_submits_and_an_administrator_may_cancel_any_order` | yes: release removed fails it; ownership checks proven in S2 |
| 14 (submit, cancel) | `OrderLifecycleTests.Another_tenants_order_cannot_be_submitted_or_cancelled`; `IsolationMatrixTests` (16 routes) | same two controls as S2 (proven there) |
| 15 | `OpenApiContractTests` (snapshot updated, additive), `RouteTableTests` | covered by existing guards |
| 3 (review fix) | `StockReservationTests.A_repeat_for_other_lines_or_after_a_release_fails_and_holds_nothing_more` | yes: answering every repeat "reserved" fails it |
| 11 (review fix) | `OrderLifecycleTests.A_submit_moves_only_the_draft_version_it_read_so_a_concurrent_line_edit_is_never_reserved_stale` | yes: the version condition on the Draft transition removed fails it (a 20-round race test could not hit the window, so it was replaced by this deterministic one) |
| Data (review fix) | `ModuleSchemaTests.Each_module_has_a_migration_for_every_change_to_its_model` | yes: widening `ProductLimits.NameMaxLength` without migrations fails it |
