# M4 — Order and inventory consistency

**Goal:** customers place orders that reserve stock without ever promising the same unit twice, through a lifecycle whose
illegal steps are refused, across two modules that share no transaction.

**Gate (roadmap):** a concurrent last-item test proves no oversell; illegal state transitions are rejected.

Spec: [SPEC-0004](../specs/0004-orders-and-reservations.md) (Implemented). Decision: [ADR-0020](../architecture/decisions/0020-orders-and-reservations.md)
(Accepted 2026-10-21: your four choices of that day). No open decision was due by M4.

## 1. What was built

```text
src/
├── BuildingBlocks/Validation/          DomainRuleValidation: one domain-error-to-field-error mapping for every module
├── Hosts/Api/Endpoints/V1/
│   ├── RowVersionETag.cs               ETag / If-Match on PostgreSQL xmin, shared by products and orders
│   └── Tenants/Orders/                 create, get, replace lines, submit, cancel + Schemas/
└── Modules/
    ├── Catalog/Contracts               + ProductSnapshot, GetSnapshotsAsync, ProductLimits
    ├── Inventory/Contracts (new)       IStockReservations: ReserveAsync (all or nothing), ReleaseAsync; idempotent by order
    ├── Inventory/Infrastructure        stock_balances.reserved, inventory.reservations, StockReservations
    └── Orders/ (new module)            Order aggregate, OrderStateMachine, OrderService, OrderStore, schema "orders"
tests/Orders/                           state table and line rules (unit)
tests/Database/Inventory/StockReservationTests.cs, tests/Database/Orders/{OrderEndpointTests,OrderLifecycleTests}.cs
```

| Slice | Commit | Delivered |
|---|---|---|
| S1 | `d0783e2` | `reserved` column with CHECK, reservation rows with forced RLS, `Inventory.Contracts`, adjustments respect reserved |
| S2 | `46f6343` | Orders module: aggregate and state table, snapshot lines, ETag edits, `orders.place` / `orders.manage` |
| S3 | `bf84c89` | submit and cancel in three committed steps, recovery by repetition, 20-way last-item test |
| Review fixes | `70b1d3f` | repeat checks on reserve, version-guarded submit, shared limits and validation helper, ETag cleanup |

States in M4: `draft → pending_reservation → pending_payment | rejected`, and `draft | pending_payment → cancelled`.
Payment states arrive in M6.

## 2. Architectural reasoning

**Two counters would race; one conditional statement does not.** A reservation is
`UPDATE stock_balances SET reserved = reserved + q WHERE ... AND on_hand - reserved >= q`. Twenty submits for the last
unit queue on the balance row; each re-evaluates the condition against the committed value, so exactly one matches.
`CHECK (reserved >= 0 AND reserved <= on_hand)` stands behind it: without the condition the database still refuses,
but as a 500 instead of a clean rejection. Stock adjustments now use `on_hand + d >= reserved`, so a stock count can
never take away units already promised.

**Idempotency lives in a key, not in a flag.** Each reservation row is keyed `(tenant, order, product)` and inserted
before its balance changes. A repeated or concurrent reserve of the same order waits on that key, finds the row, rolls
back without touching a balance, and is answered from the committed rows: still held for exactly these lines means
"reserved"; anything else (released, or other lines) is a caller error. Release flips rows to `released`, so the second
release finds nothing to give back.

**Two modules, two transactions, no distributed transaction.** Orders and Inventory own separate schemas and
`DbContext`s (ADR-0012). Submit is: commit `draft → pending_reservation`; reserve in Inventory's own transaction;
commit `pending_payment` or `rejected`. A crash between steps leaves a visible state, and because both Inventory calls
are idempotent, **repeating the command finishes it**. That is the minimum honest answer before the M5/M6 saga takes
over recovery. Cancel commits `cancelled` first and then releases, for the same reason.

**Every transition is a compare-and-set.** `UPDATE orders SET status = @target WHERE id = @id AND status = @expected`.
Of a cancel and a submit racing on one draft, exactly one moves it; the other re-reads and either finishes the work
(a submit that finds `pending_reservation`) or answers 409. The submit's first step also requires the row version it
read, so a line edit that commits in between makes the transition miss: the order never reserves stale lines.

**The state table is data.** `OrderStateMachine` is one dictionary of `(state, trigger) → state`. A unit test compares
the whole table, and every use case asks it instead of checking states locally.

**Snapshots are copies, sized by contract.** Order lines copy SKU, name, price and currency from
`Catalog.Contracts.ProductSnapshot`, so a later catalog change cannot rewrite history. The columns are sized from
`ProductLimits`, and an architecture test fails if any module's model changes without a migration.

## 3. Security considerations

- New tenant-owned tables (`inventory.reservations`, `orders.orders`, `orders.order_lines`) are `ENABLE` + `FORCE` RLS
  with the fail-closed policy; the forced-RLS test now counts 6 tables. Every store method filters by the tenant
  context's id inside `TenantScopedTransaction`.
- Ownership is per order: the owner reads, edits, submits and cancels; `orders.manage` (administrators) reads and
  cancels any order. Anyone else gets 404 `orders.order_not_found`, the same answer as an unknown or malformed id.
- `orders.manage` is not a route permission; the module checks it per order. The route-table test says so explicitly.
- Reservations cannot be deleted by the application role (history of what an order held).
- Orders reaches Catalog and Inventory only through `Contracts`; the architecture tests read compiled references.
- Residual risk: Inventory trusts the order id its in-process caller passes (tenant still enforced by filter and RLS).
  Revisit when the saga owns recovery.

## 4. Verification

Fresh evidence for the gate (2026-10-21, after the review fixes `70b1d3f`): see the exit checklist and
`docs/progress/progress.json`.

| Command | Result |
|---|---|
| `./scripts/verify.ps1 -Database` | all checks passed; Api 125/125, IdentityAccess 67/67, Tenants 77/77, Catalog 7/7, Inventory 5/5, Orders 6/6, Architecture 6/6, Worker 12/12, Database 162/162; 0 warnings |
| `./scripts/progress.ps1 -Verify` (CI test command with coverage) | quality run passed for `70b1d3f`, 0 warnings; compose valid |
| `OrderLifecycleTests`, `StockReservationTests` (concurrency) | 3 runs in a row each: 9/9 and 8/8 |

**Break-it checks re-run for the gate** (fault injected, test fails at an assertion, fault restored, rebuilt):

- No oversell: the reservation condition **and** the CHECK removed: the 20-way last-item, submit, and
  illegal-transition tests fail.
- Illegal transitions: cancel allowed from any state: the illegal-transition test fails.
- Cross-tenant: the order store's tenant filter **and** the RLS policy removed: the cross-tenant order test fails.

Per-slice proofs (ownership, ETag, release, idempotency, version guard, pending model changes) are in the spec's
Verification table and the tracker. One honest note: removing only the service's explicit submit check leaves the
illegal-transition test passing, because the domain's `NextOrThrow` refuses too (two layers). A 20-round race test for
the version guard could not hit the window, so a deterministic store-level test replaced it.

**Not verified:** the Testcontainers path still runs only in CI, which is on hold.

## 5. Trade-offs and assumptions

- Recovery is by repeating the command, not automatic. An order stuck in `pending_reservation` stays there until its
  owner submits again (a cancel is refused in that state). The M5/M6 saga adds a sweeper.
- A rejected order is final; the customer creates a new one. Prices are frozen when lines are set and not re-checked at
  submit, so a product archived after the draft still reserves (your decisions, 2026-10-21).
- No order list endpoint yet; get by id only.
- Submit and cancel take no `If-Match`: each transition is already a compare-and-set on the state.
- `orders.total_too_large` (409) was added for a total beyond `numeric(19,4)`.
- The Catalog and Inventory InitialSchema Down migrations still do not revoke grants (known, from M3); the new Orders
  InitialSchema Down does.

## 6. Mentor exercises

1. Remove `AND status = {expectedName}` from `OrderStore.TryTransitionAsync`. Which test catches it, and why does the
   race need a cancel and a submit rather than two submits?
2. Replace the reservation rows with "check the order's status in Orders first, then reserve". What breaks when two
   submits of the same order run at once?
3. Cancel commits before it releases. Swap the two steps. What does a crash between them leave behind, and can the
   owner still recover it by repeating a command?
4. Add `paid` after `pending_payment`. Which single file changes for the state rules, and which tests fail first?
5. Why does `ReserveAsync` take lines in product-id order? Construct the deadlock without it.

## 7. Interview talking points

- "How do you prevent overselling?" One conditional UPDATE on the balance row, a CHECK behind it, and a 20-way
  concurrent test on real PostgreSQL that fails when the condition is removed.
- "How do you keep two modules consistent without a distributed transaction?" Commit intent, call the other module in
  its own transaction, record the outcome; make each call idempotent so the command can be repeated.
- "How do you enforce a state machine?" A transition table in the domain and a compare-and-set on the stored state, so
  racing commands cannot both win.
- "What is idempotency here, concretely?" A natural key on the reservation row, not a request id or a flag.

## 8. Exit checklist

- [x] Concurrent last-item test proves no oversell (`Twenty_concurrent_submits_for_the_last_unit_reserve_it_exactly_once`, 3 runs; proof re-run)
- [x] Illegal state transitions are rejected (`A_transition_the_state_table_does_not_allow_is_a_409_and_changes_nothing`, the cancel-vs-submit race test, `OrderStateMachineTests`; proof re-run)
- [x] SPEC-0004 Implemented: all 15 criteria have verification rows
- [x] Tenant isolation review: no critical/high/medium findings; code review: 7 findings, all fixed in `70b1d3f`
- [x] ADR-0020 accepted by you on 2026-10-21
- [ ] **You:** review this guide and the M4 commits on branch `m4-orders`, and merge into `main` when satisfied
- [ ] **You:** approve the start of M5 (messaging foundation; Open Decision 4, RabbitMQ versus Kafka, is due first)
