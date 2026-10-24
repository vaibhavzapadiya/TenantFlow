# Threat model: business workflows (Catalog, Inventory, Orders)

- Status: Draft (M9), written for SPEC-0009 criterion 1
- Date: 2026-10-22
- Owner: TenantFlow tech lead (the user)
- Scope: product create, read, update, list and search (Catalog); stock reads, adjustments and reservations
  (Inventory); order create, line edits with `ETag`, submit, cancel and customer ownership (Orders). Payments and
  refunds, imports and exports, and the Worker have their own threat models; they appear here only where they touch
  these flows.
- Related: blueprint sections 6, 8, 10, 15, 22; ADR-0011, ADR-0016, ADR-0019, ADR-0020;
  [SPEC-0003](../specs/0003-catalog-and-inventory.md), [SPEC-0004](../specs/0004-orders-and-reservations.md),
  [SPEC-0009](../specs/0009-security-and-privacy.md); [threat model 0001](0001-tenant-boundary.md)

## Assets and data classification

| Asset | Classification | Why it matters |
|---|---|---|
| Products (sku, name, price, status) | Tenant-confidential | Prices and assortment are business-confidential; archived products are hidden from customers |
| Stock balances and reservations | Tenant-confidential, integrity-critical | Wrong numbers mean overselling or blocked sales |
| Stock adjustments (delta, reason, actor membership id) | Tenant-confidential, append-only | Explains every stock change; the reason is free text (up to 200 characters) |
| Orders and order lines (owner membership id, price snapshot, totals, status) | Tenant-confidential, integrity-critical | Money and customer ownership; kept for the life of the tenant (ADR-0025) |
| Order cancellation reason (paid orders) | Tenant-confidential | Free text (up to 500 characters) written by an administrator |

Personal data in scope: membership ids (pseudonymous references) and any personal data a user types into a free-text
reason. TenantFlow asks for none there, but cannot prevent it.

## Actors and trust boundaries

Actors: customer user (`catalog.read`, `orders.place`); tenant administrator (adds `catalog.manage`, `inventory.read`,
`inventory.manage`, `orders.manage`); member or administrator of another tenant; anonymous or platform caller.

| Boundary | Crossed by | Control |
|---|---|---|
| B2 Authenticated user → tenant | every route below | active membership in an active tenant, per request (threat model 0001) |
| B4 Role → permission | each route | named permission on every route (`RequireTenantPermission`) |
| B8 Caller → order object | get, edit, submit, cancel | `OrderService`: owner, or `orders.manage` to read and cancel; anyone else gets 404 |
| B5 API → PostgreSQL | every store call | store filters on the tenant context inside a tenant-scoped transaction, plus forced RLS |
| B9 Orders → Catalog, Inventory | create, edit, submit, cancel | in-process `Catalog.Contracts` and `Inventory.Contracts` only, each in its own transaction (ADR-0020) |

## Entry points and data flows

1. `GET /v1/tenants/{t}/products?limit&cursor&status&q`, `GET .../products/{id}`: keyset page (max 100) or one product;
   customers see active products only. `q` becomes a `to_tsquery` text through `ProductSearch.ToQuery`.
2. `POST .../products`, `PUT .../products/{id}` (`If-Match`): admin writes; SKU is immutable.
3. `GET .../products/{id}/stock`, `POST .../stock/adjustments {delta, reason}`: one conditional `UPDATE` plus an
   append-only adjustment row in the same transaction.
4. `POST .../orders {lines:[{productId, quantity}]}`, `PUT .../orders/{id}/lines` (`If-Match`): Orders reads
   price, currency, sku and name from Catalog and stores a snapshot; the owner is the caller's membership.
5. `POST .../orders/{id}/submit`: `Draft → PendingReservation` (version-checked), reserve in Inventory, then
   `PendingPayment` or `Rejected`. `POST .../orders/{id}/cancel {reason?}`: release, or a refund for a paid order.

## Abuse cases / attack paths

| Id | Attack | Attacker goal | Path |
|---|---|---|---|
| BW-01 | Cross-tenant BOLA | Read or change tenant B's products, stock or orders | Tenant A route with B's object id |
| BW-02 | Cross-customer BOLA | Read, edit, submit or cancel another customer's order | Same tenant, another customer's order id |
| BW-03 | Privilege escalation customer → manager | Change prices, stock, or cancel and refund paid orders | Call admin routes or admin-only transitions as a customer |
| BW-04 | Mass assignment / over-posting | Set owner, status, tenant, totals or row version | Extra JSON members in create or edit bodies |
| BW-05 | Price tampering | Buy below the catalog price | Send a price in order lines; mix currencies; overflow a total |
| BW-06 | Oversell by race | Two customers get the same last unit; stock below zero | Concurrent submits, adjustments, or repeated reserve calls |
| BW-07 | State-machine race | Cancelled order that still reserves, or a double release | Submit and cancel at once; replay an interrupted command |
| BW-08 | Lost update | Overwrite another admin's product edit or a draft's lines | Edit with a stale or missing `ETag` |
| BW-09 | Search injection | Break or widen the query, read across tenants | `to_tsquery` operators or SQL in `q` |
| BW-10 | Enumeration via 404 versus 403 | Learn which tenant, product or order ids exist | Compare answers for unknown, foreign and not-own ids |
| BW-11 | Expensive queries / paging abuse | Slow the shared database for every tenant | Huge `limit`, one-letter prefix search, 50-line orders in a loop |
| BW-12 | Personal data in free text | Store names or contact details where no retention applies | Stock adjustment reason or cancellation reason |
| BW-13 | Untraceable stock change | Hide who moved stock | Update or delete adjustment rows |
| BW-14 | Unaudited administrator action | Cancel a customer's order with no trace of who did it | Administrator cancels an order they do not own |
| BW-15 | Trusted in-process caller | Reserve or release under another order id | Inventory contract trusts the order id it is given |

## Existing controls

- Every store method filters by the tenant context; every tenant-owned table here has `ENABLE` + `FORCE` RLS, and the
  app role is not owner and has no `BYPASSRLS`. Foreign ids answer 404, as unknown ones do (BW-01, BW-10).
- `OrderService.FindVisibleAsync` and `FindOwnAsync` check ownership per order; only the owner edits and submits;
  `orders.manage` reads and cancels. A paid order needs `orders.manage` and a reason (BW-02, BW-03).
- Request records hold only client-settable fields: `OrderLineRequest(ProductId, Quantity)`,
  `UpdateProductRequest(Name, Price, Status)`, `AdjustStockRequest(Delta, Reason)`. Owner and tenant come from the
  tenant context; prices come from `IProductCatalog.GetSnapshotsAsync` (BW-04, BW-05).
- Stock moves by one conditional `UPDATE` with `CHECK (0 <= reserved <= on_hand)`; reservations are keyed
  `(tenant, order, product)` and idempotent; transitions are compare-and-set on the expected state (BW-06, BW-07).
- `If-Match` is required on product and line edits; the version is compared again in the conditional write (BW-08).
- `ProductSearch.ToQuery` keeps letters and digits only, joins words with `&`, adds a prefix only from 2 characters,
  and passes the result as a parameter; `q` longer than 100 characters is 400 (BW-09, BW-11).
- Limits: page size 1 to 100, request body 64 KiB, 1 to 50 lines, quantity 1 to 10 000, delta up to 1 000 000,
  reasons trimmed, length-limited, control and direction-override characters refused (BW-11, BW-12).
- Stock adjustments are append-only for the app role and record the actor as a membership id (BW-13).

## Gaps and residual risk

| Gap | Residual risk | Tracked as |
|---|---|---|
| No rate limiting | A member can loop searches, pages or order creation and load the shared database | BW-A15 |
| No test sends extra members (owner, price, status) | A future request field could bind something the client must not set | BW-A07 |
| `If-Match: *` accepts any version | A client that sends it can overwrite a concurrent edit | BW-A11 |
| Free-text reasons are not yet classified with a retention rule | Personal data typed into a reason has no published rule | BW-A16, BW-A17 |
| Paid order cancellation not yet in the Audit module | Cancellation and refund trail exists only in Orders and Payments rows | BW-A19 |
| Unpaid cancellations by an administrator and product price changes are not audited | No record of which administrator acted | BW-A20 |
| Inventory trusts the order id from its in-process caller | A bug in Orders could reserve under a wrong order id (tenant still enforced) | BW-A21 |

## Mitigations with owner and due date

| Id | Abuse case | Mitigation | Owner | Due | Status | Evidence |
|---|---|---|---|---|---|---|
| BW-A01 | BW-01 | Tenant-filtered lookups by id; a foreign id is 404 with no data and no change | Claude | M4 | done | `ProductEndpointTests.Another_tenants_products_cannot_be_read_listed_or_changed`, `StockEndpointTests.Another_tenants_stock_cannot_be_read_or_changed`, `StockReservationTests.Another_tenants_stock_cannot_be_reserved_or_released`, `OrderEndpointTests.Another_tenants_order_cannot_be_read_or_changed`, `OrderLifecycleTests.Another_tenants_order_cannot_be_submitted_or_cancelled`, `IsolationMatrixTests` |
| BW-A02 | BW-01 | Forced RLS on every tenant-owned table as a second control | Claude | M4 | done | `RowLevelSecurityTests.Row_level_security_is_forced_on_every_tenant_owned_table`, `RowLevelSecurityTests.A_tenant_scope_reads_and_writes_only_its_own_rows`, `RowLevelSecurityTests.Without_a_tenant_no_row_is_visible_and_no_row_can_be_written` |
| BW-A03 | BW-02 | Per-order ownership check; not-own orders are 404; admins read and cancel but never edit or submit | Claude | M4 | done | `OrderEndpointTests.Another_customers_order_does_not_exist_for_them_and_an_admin_reads_but_does_not_edit_it`, `OrderLifecycleTests.Only_the_owner_submits_and_an_administrator_may_cancel_any_order` |
| BW-A04 | BW-03 | Named permission per route; the customer role holds no manage permission | Claude | M4 | done | `RolePermissionsTests.Customer_can_read_the_tenant_and_the_catalog_place_orders_and_subscribe_and_nothing_else`, `RouteTableTests.Every_tenant_route_names_a_permission_that_exists_and_every_permission_is_required_somewhere`, `ProductEndpointTests.A_customer_sees_active_products_only_and_cannot_write`, `StockEndpointTests.A_customer_can_neither_read_nor_adjust_stock` |
| BW-A05 | BW-03 | Cancelling a paid order needs `orders.manage` and a valid reason | Claude | M7 | done | `OrderRefundSagaTests.Cancelling_a_paid_order_refunds_what_is_left_once_and_its_event_refunds_the_order` |
| BW-A06 | BW-05 | Lines carry only product id and quantity; price, currency and name are a Catalog snapshot; one currency per order; total bounded | Claude | M4 | done | `OrderEndpointTests.An_order_snapshots_its_products_and_a_later_price_change_does_not_touch_it`, `OrderEndpointTests.Invalid_lines_are_refused_and_nothing_is_written`, `OrderRuleTests.A_draft_snapshots_its_lines_and_computes_the_total_once`, `OrderRuleTests.Lines_in_different_currencies_or_beyond_the_largest_total_are_refused` |
| BW-A07 | BW-04 | Request records declare only client-settable fields; no explicit over-posting test | user | M10 | accepted | Extra JSON members have no property to bind to, owner and tenant come from the tenant context, and price comes from the Catalog snapshot; revisit if a request record gains a server-owned field. Reviewed in M10-S6 (2026-10-22): no request record gained one; stays accepted (recommendation, the owner decides) |
| BW-A08 | BW-06 | One conditional stock update plus CHECK; idempotent reservations keyed by order | Claude | M4 | done | `StockEndpointTests.Twenty_concurrent_decrements_of_ten_units_succeed_exactly_ten_times`, `StockReservationTests.Twenty_concurrent_orders_for_the_last_unit_reserve_it_exactly_once`, `OrderLifecycleTests.Twenty_concurrent_submits_for_the_last_unit_reserve_it_exactly_once`, `StockReservationTests.An_adjustment_cannot_take_stock_below_what_is_reserved`, `StockReservationTests.Reserving_or_releasing_an_order_twice_counts_once` |
| BW-A09 | BW-07 | Compare-and-set transitions from a state table; repeating an interrupted command finishes it once | Claude | M4 | done | `OrderLifecycleTests.A_draft_cancelled_and_submitted_at_once_ends_in_one_consistent_state`, `OrderLifecycleTests.A_transition_the_state_table_does_not_allow_is_a_409_and_changes_nothing`, `OrderLifecycleTests.Repeating_an_interrupted_submit_or_cancel_finishes_it_without_counting_twice`, `OrderStateMachineTests` |
| BW-A10 | BW-08 | `If-Match` required; stale version is 412; submit moves only the draft version it read | Claude | M4 | done | `ProductEndpointTests.An_edit_needs_the_current_etag_and_a_stale_one_changes_nothing`, `OrderEndpointTests.Lines_change_only_with_the_current_etag`, `OrderLifecycleTests.A_submit_moves_only_the_draft_version_it_read_so_a_concurrent_line_edit_is_never_reserved_stale` |
| BW-A11 | BW-08 | `If-Match: *` stays allowed as an explicit overwrite | user | M10 | accepted | Standard HTTP semantics chosen by a caller who already holds write permission on that object; it never bypasses tenant or ownership checks. Reviewed in M10-S6 (2026-10-22): no runbook depends on it; stays accepted (recommendation) |
| BW-A12 | BW-09 | Search text reduced to letters and digits joined by `&`, passed as a parameter, tenant filter kept | Claude | M8 | done | `ProductSearchTests.Words_are_joined_the_last_one_is_a_prefix_and_operators_are_dropped`, `ProductEndpointTests.Search_finds_every_word_in_name_or_sku_with_a_prefix_last_word_inside_the_tenant_only` |
| BW-A13 | BW-10 | Unknown, malformed, foreign and not-own ids give the same 404; SKU uniqueness is per tenant only | Claude | M4 | done | `TenantRoutesTests.A_non_member_reading_another_tenant_gets_the_same_404_as_an_unknown_or_malformed_id`, `StockEndpointTests.Stock_of_an_unknown_product_is_404_and_creates_no_balance`, `ProductEndpointTests.A_sku_is_unique_within_a_tenant_ignoring_case_but_not_across_tenants`, `OrderEndpointTests.Another_customers_order_does_not_exist_for_them_and_an_admin_reads_but_does_not_edit_it` |
| BW-A14 | BW-11 | Bounded work per request: page size, search length and prefix, body size, line count and quantity | Claude | M8 | done | `ProductEndpointTests.Paging_by_cursor_returns_every_product_once_even_when_rows_arrive_between_pages`, `ProductEndpointTests.Search_finds_every_word_in_name_or_sku_with_a_prefix_last_word_inside_the_tenant_only`, `InputValidationTests.Request_bodies_are_limited_to_64_KiB`, `OrderRuleTests.The_validator_reports_each_broken_line_rule_as_a_field_error` |
| BW-A15 | BW-11 | Per-tenant and per-member rate limits on tenant routes | Claude | M11 | planned | M11 workload model (SPEC-0009 decision 12) |
| BW-A16 | BW-12 | Classify `inventory.stock_adjustments` and `orders.orders` (cancellation reason) with purpose and retention rule | Claude | M9 | done | `PrivacyDocumentTests.Every_table_in_every_module_schema_has_a_class_a_purpose_and_a_retention_rule` |
| BW-A17 | BW-12 | Free-text reasons stay for the life of the tenant; no on-request erasure | user | M13 | accepted | Reasons are short, length-limited and stripped of control characters; on-request erasure and tenant offboarding are deferred by SPEC-0009 decision 6 |
| BW-A18 | BW-13 | Adjustments append-only for the app role, actor stored as a membership id | Claude | M3 | done | `RowLevelSecurityTests.Stock_adjustments_are_append_only_for_the_application_role`, `StockAdjustmentRuleTests.An_invalid_adjustment_is_rejected_by_the_domain_and_reported_by_the_validator` |
| BW-A19 | BW-14 | Paid order cancellation recorded in the Audit module and readable by tenant administrators | Claude | M9 | done | `OrderRefundSagaTests.Cancelling_a_paid_order_refunds_what_is_left_once_and_its_event_refunds_the_order`, `AuditReadTests.An_administrator_reads_their_tenants_log_newest_first_page_by_page_and_never_another_tenants` |
| BW-A20 | BW-14 | Unpaid cancellations by an administrator and catalog price changes stay outside the audited list | user | M10 | accepted | No money moves on an unpaid cancellation and the order keeps its price snapshot; revisit if tenants need an actor trail for these actions. Reviewed in M10-S6 (2026-10-22): the cross-tenant runbook uses the audit log for platform actions only; stays accepted (recommendation) |
| BW-A21 | BW-15 | Inventory trusts the order id from its in-process Orders caller | user | M14 | accepted | The call is in-process through the Inventory contract only, tenant is still enforced by filter and RLS; review with the service-extraction lab |

## Security tests and monitoring

- Tests: the denial and concurrency tests cited above, each proven to fail without its guard (SPEC-0003 and SPEC-0004
  verification tables), plus `IsolationMatrixTests` and `RouteTableTests`, which pick up every new tenant route.
- Monitoring: authorization denials are counted with bounded labels (threat model 0001). Business alerts on 409
  spikes (insufficient stock, version conflicts) and slow search queries are M10 work; load baselines are M11.

## Review date and change triggers

Review at the M9 gate, then at M11 (rate limiting and load baseline) and M13 (erasure and offboarding). Also review
when a new order or product field, an order list or search endpoint, product deletion, a new free-text field,
re-pricing at submit, or a new caller of `IStockReservations` is proposed.
