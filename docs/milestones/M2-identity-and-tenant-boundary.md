# M2 — Identity and tenant boundary

**Goal:** authenticated users act only inside tenants where they hold an active membership, with the
permissions their role grants, and tenant-owned code fails closed when no tenant is known.

**Gate (blueprint section 20):** positive and negative tenant isolation tests pass for representative
resources. Spec: [SPEC-0002](../specs/0002-identity-and-tenant-boundary.md). Threat model:
[0001-tenant-boundary](../threat-models/0001-tenant-boundary.md).

## 1. What was built

```text
src/
├── BuildingBlocks/Abstractions/        AppError/AppException, ActorIdentity, IdentityLimits
├── Hosts/
│   ├── Api/                            Extensions/ Middleware/ Configuration/ ErrorHandling/ Health/ Telemetry/
│   │   └── Endpoints/V1/               Me/  Platform/Tenants/  Tenants/   (one folder per feature)
│   ├── Migrator/                       applies every module's migrations, as the owner role
│   └── Worker/
└── Modules/
    ├── Tenants/                        Domain, Application, Infrastructure, Contracts
    └── IdentityAccess/                 Domain, Application, Infrastructure, Contracts
deploy/compose/                         PostgreSQL 17 + roles, Keycloak 26 with two synthetic realms
tests/                                  IdentityAccess.Tests, Tenants.Tests, Api.Tests, Database.Tests, Worker.Tests
```

| Slice | Commit | Delivered |
|---|---|---|
| S1 | `b57d979` | EF Core, Migrator, schema per module, owner and app roles, database test category |
| S2 | `2d63177` | JWT validation, deny-by-default, `GET /v1/me` |
| S3 | `63d53d1` | Keycloak in Compose, realm import, smoke check |
| S4 | `4d130d6` | Tenants module and the platform boundary (separate issuer) |
| Pre-S5 review | `82fea3d` to `ff136ef` | feature folders, `/v1`, error contract, validation, hardening, host composition, domain layer, conventions |
| S5a | `649a9c0` | memberships, roles to permissions, membership audit, last-administrator rule, `GET /v1/me/memberships` |
| S5b | `467a8b1` | first tenant administrator through `/v1/platform/tenants/{id}/admins` |
| S6 | `c003712` | `ITenantContext`, membership resolver, `RequireTenantPermission`, tenant and member routes |
| S7 | `c899cc5` | exact route table, denial telemetry, isolation matrix |

## 2. Architectural reasoning

**Authority comes from the database, never from the token.** A token says who the caller is (issuer
plus subject, ADR-0009). Which tenant they may act in, and with which role, is read from
`identity_access.memberships` on every request (ADR-0010). A removed member is refused on the very next
call with the same token; a role change takes effect immediately. The price is two small reads per
tenant request; caching is a later option with a documented revocation delay.

**The route only selects; the filter decides.** `/v1/tenants/{tenantId}/...` names a tenant. The
`TenantAccessFilter` resolves the caller's membership, then the tenant's status, then the role's
permission, in that order, because each denial must reveal as little as possible: a non-member sees
`404 tenant.not_found` whatever the tenant's state, identical to an unknown or malformed id; only
members ever see `403 membership.tenant_suspended` or `403 membership.permission_denied`.

**Fail closed by construction.** `ITenantContext` is a read-only contract in `IdentityAccess.Contracts`.
Its only implementation is the resolver, which sets it once, after every check passed. Tenant-owned use
cases (`TenantMembersService`) take no tenant id and throw `TenantContextNotSetException` before touching
the store. Handlers act on the context's tenant, never on the route's text.

**Modules talk through Contracts only (ADR-0002).** IdentityAccess learns whether a tenant is usable
through `Tenants.Contracts.ITenantDirectory`; it never reads the `tenants` schema. "No such tenant" and
"tenant suspended" are defined once in `TenantContractErrors` and reused by both modules' catalogs.

**One serialized path for membership changes (ADR-0018).** The last-administrator rule reads a
count and then changes a row, so every change takes `pg_advisory_xact_lock` on the tenant id inside its
transaction. The spec's "tenant-level row lock" had no row to lock: the tenant row belongs to another
module (ADR-0012).

**Strongly typed ids inside IdentityAccess.** `TenantId` and `MembershipId` cannot be swapped by mistake;
the cross-tenant bug SPEC-0002 criterion 17 tests for is the compile error it would otherwise be.

**Two boundaries, structurally separate.** Tenant and platform tokens use different schemes, issuers and
audiences, and the host refuses to start if they overlap. No scheme authenticates by default, and the
fallback and default policies deny, so a route that forgets its policy answers 401.

## 3. Security considerations

- Threat model abuse cases TB-01 to TB-14 are mitigated and each maps to a SPEC-0002 criterion with a
  runtime proof; TB-15 (worker tenant context) is M5.
- Audit rows are append-only for the application role (`UPDATE`, `DELETE`, `TRUNCATE` revoked) and are
  written in the same transaction as the change; the actor, both roles and the clock's time are recorded.
- Denials are counted in `tenantflow.auth.denials` with only `scheme` and `reason` labels, and logged
  without the token, its parts, the subject, or the `Authorization` header.
- The application role owns nothing, cannot run DDL, and never bypasses row-level security. RLS itself
  is decided (ADR-0011) and built in M3 with the first tenant-owned business table.
- Residual risks: membership and tenant status are separate reads (a suspension between them is seen on
  the next request); platform operator authority is an IdP role claim until the M9 registry; every
  denial is logged at Warning, which a flood of anonymous requests will make noisy.

## 4. Verification

Fresh evidence for the gate (2026-10-20, commit `c899cc5`):

| Command | Result |
|---|---|
| `./scripts/verify.ps1 -Database` | all checks passed; IdentityAccess 67/67, Tenants 77/77, Worker 12/12, API 123/123, Database 100/100; 0 warnings |
| `./scripts/progress.ps1 -Verify` (CI test command with coverage) | quality run passed for `c899cc5`; compose valid |
| `./scripts/dev.ps1 validate` | compose valid; 8 ports on 127.0.0.1; images pinned; realm synthetic |
| `./scripts/dev.ps1 smoke` (Migrator run, real API and worker, Keycloak) | 10 of 10 checks; no error-level log lines in either host |

**Break-it checks re-run for the gate** (fault injected, test fails at an assertion, fault removed):
resolving a membership without the tenant condition; removing the tenant access filter; removing the
tenant condition from a membership lookup; weakening the platform operator policy. Across the milestone
every guard was removed once; the per-slice lists are in `docs/progress/progress.json`.

Isolation matrix (`IsolationMatrixTests`): the tenant routes are read from the host's route table, so a
route added later is covered without anyone remembering to add it. Each is called by anonymous, platform
operator, non-member, removed member, administrator of an unknown tenant and of a malformed id, member of
a suspended tenant, and customer; denials answer exactly as the boundary says, change no row, are counted
once, and look the same for a real tenant, an unknown id, and a malformed id.

## 5. Trade-offs and assumptions

- Direct application services instead of a mediator (ADR-0013); FluentValidation in Application
  (ADR-0014). Both are followed in the code and need your decision.
- URL-segment versioning, hand-rolled (ADR-0015); Problem Details with stable codes (ADR-0016); one
  pipeline and host composition (ADR-0017). All accepted on 2026-10-21.
- The member issuer is the caller's own validated issuer (tenant routes) or the tenant scheme's configured
  authority (platform admins call). That assumes the authority equals the token's `iss`, true for the test
  issuer and Keycloak; revisit when the production provider is chosen (M12).
- Tenant names are unique ignoring case and surrounding whitespace (T-19, decided 2026-10-21; SPEC-0002 criterion 26):
  a duplicate is 409 `tenant.name_taken`, and a suspended tenant keeps its name. The index uses the database's `lower`, which
  folds ASCII always and other letters per the database locale. Membership removal is final; a returning user gets a new
  membership. A 201 for a new member carries no `Location` (a member has no route of its own).

## 6. Mentor exercises

1. Remove `RequireTenantPermission` from one route and predict which of the matrix, route-table and
   telemetry tests fail first; then run it.
2. Make two administrators demote each other at the same moment with the advisory lock removed. How many
   rounds of 20 does it take to lose the last administrator on your machine?
3. A customer's token is valid, but their membership was removed an hour ago. Trace exactly which line
   refuses the next request, and what an attacker holding the token sees.
4. Design the second module that needs `ITenantContext`. What would you add to `IdentityAccess.Contracts`,
   and what must it never expose?
5. Why is `404` right for a non-member and `403` right for a member of a suspended tenant? Argue the
   opposite and find the leak.

## 7. Interview talking points

- "How do you stop one tenant reading another's data?" The tenant id in a route only selects; authority is
  an active membership read per request, every lookup is by `(tenant, id)`, tenant-owned code throws with no
  context, and a matrix test walks every route as every kind of caller. RLS adds a second layer in M3.
- "Why 404 for non-members?" A denial must not confirm a tenant exists; the response body is identical to
  an unknown id, and a test compares them.
- "How do you keep the last admin?" A per-tenant advisory lock inside the transaction; a 20-round concurrent
  demotion test fails when the lock is removed.
- "How do you know your tests prove anything?" Each guard was removed once to watch its test fail.

## 8. Exit checklist

- [x] Positive and negative tenant isolation tests pass (`IsolationMatrixTests`, `TenantRoutesTests`)
- [x] SPEC-0002 implemented: all 25 criteria have verification rows
- [x] Fresh gate, coverage run, validate, and smoke green
- [x] Open Decisions 3, 11, 15, 16, 17, 20 closed by accepted ADRs (0009 to 0014)
- [x] ADR-0013 to ADR-0018 accepted by you on 2026-10-21; Open Decisions 15 and 17 closed
- [x] Tenant names are unique (T-19, decided 2026-10-21, criterion 26)
- [ ] **You:** review this guide and the M2 commits on branch `m2-identity-tenant-boundary`
- [ ] **You:** push and open a pull request so CI runs the gate (on hold until you raise it)
- [ ] **You:** approve the start of M3 (first vertical slice: catalog and inventory)
