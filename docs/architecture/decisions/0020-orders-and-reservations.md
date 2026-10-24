# ADR-0020: Order state machine, stock reservations, and the Orders-to-Inventory flow

- Status: Accepted (2026-10-21 by the user)
- Date: 2026-10-21
- Owners: TenantFlow tech lead (options recommended by Claude, chosen and accepted by the user on 2026-10-21)
- Related decisions: [ADR-0002](0002-modular-monolith.md), [ADR-0011](0011-postgresql-row-level-security.md), [ADR-0012](0012-module-data-ownership.md), [ADR-0019](0019-m3-conventions.md); [SPEC-0004](../../specs/0004-orders-and-reservations.md); blueprint sections 7, 10, 11

## Context
M4 adds orders that reserve stock. The blueprint's lifecycle (section 7) is illustrative and runs into payment states
that arrive in M6. Orders and Inventory own separate schemas and `DbContext`s (ADR-0012), so a submit cannot be one
transaction without coupling their persistence, and stock must never be promised twice (section 10).

## Decision
1. **State table for M4.** `Draft -submit-> PendingReservation -> PendingPayment | Rejected`;
   `Draft | PendingPayment -cancel-> Cancelled`. Everything else is rejected (409 `orders.invalid_transition`).
   Each change is one conditional `UPDATE ... WHERE status = @expected`. The owner (customer membership) creates, edits,
   submits and cancels; a tenant administrator reads and cancels any order. Payment states are added in M6.
2. **Reservation model.** `inventory.stock_balances.reserved`, changed by one conditional update
   (`on_hand - reserved >= @q`) and backed by `CHECK (0 <= reserved <= on_hand)`; plus `inventory.reservations` rows
   keyed `(tenant_id, order_id, product_id)`, so reserve and release are idempotent and release knows what to return.
   Stock adjustments may not take on-hand below reserved.
3. **Flow.** Orders calls `Inventory.Contracts.IStockReservations` (in-process). Submit is three transactions:
   Orders to `PendingReservation`; Inventory reserve (all lines or none, in product-id order); Orders to
   `PendingPayment` or `Rejected`. Cancel commits `Cancelled`, then releases. Recovery is by repeating the command:
   both contract calls are idempotent. An automatic recovery sweeper arrives with the M5/M6 saga.

## Options considered
- States: the M4 subset (chosen) vs the full blueprint diagram now (states with no driver yet) vs Draft/Reserved/Cancelled
  only (loses the explicit failure state).
- Reservations: column plus rows (chosen) vs column only (release not idempotent; a retry frees units twice) vs rows only
  (every check aggregates and needs an explicit lock).
- Flow: contracts port with three transactions (chosen) vs one shared transaction across both `DbContext`s (simplest
  atomicity, but couples the modules' persistence against ADR-0012).

## Consequences
- Easier: no oversell under concurrency, proven on PostgreSQL; modules stay separable; failures are visible states.
- Harder: an interrupted submit or cancel leaves `PendingReservation` or held units until the command is repeated; until
  the saga exists, that recovery is manual (by the caller).

## Validation
SPEC-0004 criteria 3, 5, 11 and 12: the 20-way concurrent last-item test, the illegal-transition test, and the
recovery-by-repetition tests, each proven to fail with its guard removed.

## Revisit triggers
M6 payment states; reservation expiry; the M5/M6 saga taking over recovery; contention on hot products.
