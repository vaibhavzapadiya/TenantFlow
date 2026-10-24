# SPEC-0002: Identity and tenant boundary

- Status: Implemented (approved 2026-10-19 by the user; all 25 criteria verified 2026-10-20)
- Milestone: M2
- Owner: TenantFlow tech lead (the user)
- Related: blueprint sections 2.3, 2.11, 8, 13, 14, 15; ADR-0002, ADR-0008, ADR-0009, ADR-0010,
  ADR-0011, ADR-0012; threat model [0001-tenant-boundary](../threat-models/0001-tenant-boundary.md)

## Outcome
A signed-in user can act only inside tenants where they hold an active membership in an active tenant,
and only with the permissions their role grants. Tenant administrators manage members and roles, and
every such change is recorded. Platform administrators manage tenant lifecycle through a separate
boundary. Every denial path is proven by tests, so later milestones build tenant-owned features on a
boundary that is known to hold.

## Acceptance criteria

**Persistence foundation** (ADR-0012)
1. Given an empty database, when the Migrator runs, then schemas `tenants` and `identity_access` exist
   with all migrations applied and it exits 0; when it runs again, it applies nothing and exits 0.
2. Each module's migrations create objects only in its own schema, and no foreign key crosses schemas.
3. The application role does not own module tables and is neither superuser nor `BYPASSRLS`. Given the
   application role, when it runs DDL, or an `UPDATE`/`DELETE` on an audit table, then PostgreSQL denies it.
4. Given the Compose PostgreSQL is unreachable, when the database test category runs, then it fails with
   a message naming `dev.ps1 up` (blocked, never silently skipped). After any run, no test database remains.

**Authentication** (ADR-0009)
5. Given a valid tenant-audience token, when `GET /v1/me` is called, then it returns 200 with `issuer` and `subject`.
6. Given a token with the wrong issuer, wrong audience, an unknown signing key, `alg: none`, a symmetric
   algorithm, an expired lifetime (beyond the allowed skew), a future `nbf`, or no token, when any
   protected endpoint is called, then it returns 401 with `WWW-Authenticate: Bearer` and a Problem
   Details body that does not echo the token.
7. Given the authority or audience setting is missing, when the API starts, then startup fails with an
   error naming the key and containing no configuration values.
8. Given an endpoint with no explicit policy, when it is called anonymously, then it returns 401 (fallback
   policy). Health endpoints stay anonymous and keep SPEC-0001 behaviour.
9. Given a tenant-audience token, when any `/platform` endpoint is called, then it returns 401; given a
   platform-audience token, when `/me` or any `/tenants` endpoint is called, then it returns 401.

**Local identity provider** (ADR-0009)
10. Given `dev up`, then Keycloak is healthy on `127.0.0.1` with the TenantFlow realm imported; when
    `dev smoke` runs, it obtains a token for a synthetic user and `GET /v1/me` returns 200.
11. The committed realm file contains only synthetic users and no credential values; local passwords
    come from `.env` through the dev script; the image is pinned by tag and digest.

**Tenant boundary** (ADR-0010); all with users U (member of A only) and V (member of B)
12. When U calls `GET /v1/tenants/{B}`, then the response is 404 with a body identical to the response for a
    tenant id that does not exist, or a `tenantId` that is not a valid id.
13. When U calls any other tenant route for B (list, add, change role, remove members), then it returns
    404, B's data is unchanged, and no audit row is written.
14. Given U's membership in A is removed, when U next calls a route for A with the same token, then it
    returns 404. Given A is suspended, then U's calls for A return 403 (`tenant-suspended`), while V's
    calls for A still return 404 (membership is checked before tenant status, so suspension leaks nothing).
15. Given a tenant-owned use case runs with no tenant context, then it throws before touching data. The
    tenant context is set only from a resolved membership, once per request.
16. When U calls `GET /v1/me/memberships`, then only U's active memberships in active tenants are returned;
    a membership with the same `subject` from a different issuer is never matched.
17. Given U is an administrator of A, when U changes the role of a membership id that belongs to B
    through A's route, then it returns 404 and B's membership is unchanged.
18. Given U is a customer in A, when U adds, removes, or changes the role of any member (including
    their own), then it returns 403 and nothing changes.
19. Given U is an administrator of A, when U adds a member by subject with a role, then it returns 201;
    adding a subject that already has an active membership in A returns 409.
20. Given A has one active administrator, when that administrator is demoted or removed, then it returns
    409. Given two administrators demote each other concurrently, then exactly one succeeds and A keeps
    at least one administrator (repeated 20 times).
21. Each add, role change, and removal writes exactly one audit row in the same transaction (actor
    issuer and subject, tenant, membership, action, old and new role, UTC time from `TimeProvider`);
    a rejected operation writes none.

**Cross-cutting**
22. A route-table test fails if any endpoint other than health lacks a tenant, platform, or `/me` policy.
23. Authentication and authorization failures are logged without the token or `Authorization` header,
    and counted in `tenantflow.auth.denials` with only `scheme` and `reason` labels.

**Platform boundary** (ADR-0010)
24. Given a platform operator token (an IdP role claim, Q2), when `POST /v1/platform/tenants` is called with a name, then
    it returns 201 with an active tenant; `POST /v1/platform/tenants/{id}/admins` with a subject creates an
    administrator membership. A platform token without the operator authority gets 403. Both write audit rows.
25. `POST /v1/platform/tenants/{id}/suspend` and `/reactivate` move between `active` and `suspended`;
    an illegal transition returns 409; each change writes an audit row.
26. **Unique tenant names (amendment 2026-10-21, T-19).** Given a tenant exists, when `POST /v1/platform/tenants` is
    called with a name that equals its name ignoring case and surrounding whitespace, then it returns 409
    `tenant.name_taken` and writes no tenant and no audit row. A suspended tenant keeps its name. Given two creations
    of the same name at once, exactly one succeeds.

## Invariants
- No request ever reads or changes another tenant's data, and denials never reveal that a tenant exists.
- Authority comes from memberships and tenant status in the database, never from token claims (ADR-0010).
- A tenant that has had an administrator always keeps at least one active administrator.
- Audit rows are never updated or deleted by the application.

## Contract
Errors use Problem Details. Tenant routes resolve membership before any handler runs.

**Amendment (2026-10-19, pre-S5 review 2.1, ADR-0015):** every path below is under the `/v1` version
segment, as agreed by the owner. `/health/live` and `/health/ready` stay unversioned. Paths that are not a
versioned endpoint (`/me`, `/v2/me`) are denied by the fallback policy (criterion 8).

**Amendment (2026-10-19, pre-S5 review 2.2/2.3/2.5, ADR-0016):** every error response is
`application/problem+json` with `code`, `traceId`, and, for validation, `errors: [{ field, code, message }]`.
Criterion 6's Problem Details body therefore carries `authentication.required`; "a body identical to an unknown
tenant" (criterion 12) means the same `tenant.not_found` code and title. 405 and 415 are answered honestly.
**Authentication hardening (pre-S5 review 2.6, ADR-0010):** criterion 8 means 401 for every caller on a path with no
policy, signed in or not; the tenant and platform schemes must use different issuers and audiences or the host
does not start; platform tokens live at most one hour; `/health/*` never reads a token.
**Input rules (pre-S5 review 2.4):** a tenant id that is not in canonical form is a `tenant.not_found` 404 on
every route that takes one; tenant names reject control and text-direction override characters
(`tenant.name.invalid_characters`); request bodies are limited to 64 KiB; a token `sub` over 255 characters is refused (401).

| Operation | Scheme | Authorization |
|---|---|---|
| `GET /v1/me`, `GET /v1/me/memberships` | tenant | authenticated |
| `GET /v1/tenants/{tenantId}` | tenant | membership, permission `tenant.read` |
| `GET /v1/tenants/{tenantId}/members` | tenant | `members.read` |
| `POST /v1/tenants/{tenantId}/members` `{ subject, role }` | tenant | `members.manage` |
| `PUT /v1/tenants/{tenantId}/members/{membershipId}/role` `{ role }` | tenant | `members.manage` |
| `DELETE /v1/tenants/{tenantId}/members/{membershipId}` | tenant | `members.manage` |
| `POST /v1/platform/tenants` `{ name }`, `/{id}/admins`, `/{id}/suspend`, `/{id}/reactivate` | platform | platform operator (Q2) |

Roles: `tenant-admin` → `tenant.read`, `members.read`, `members.manage`; `customer` → `tenant.read`.
Membership identity is the token issuer plus `subject` (ADR-0009).

## Data
- `tenants.tenants`: `id uuid` PK, `name`, `status` (`active`|`suspended`, check), `created_at`, `updated_at`.
  Unique index on `lower(name)` (amendment 2026-10-21): the name is stored trimmed, so this makes names unique ignoring
  case and surrounding whitespace. It compares with the database's own `lower`, which folds ASCII letters always and
  other letters according to the database locale.
- `tenants.tenant_audit`: append-only lifecycle records.
- `identity_access.memberships`: `id uuid` PK, `tenant_id` (no cross-schema FK), `issuer`, `subject`, `role`
  (check), `status` (`active`|`removed`), timestamps. Unique `(tenant_id, issuer, subject)` where active;
  unique `(tenant_id, id)`; index `(issuer, subject)`.
- `identity_access.membership_audit`: `id bigint` identity PK, `tenant_id`, `membership_id`, `action`,
  actor issuer and subject, `old_role`, `new_role`, `occurred_at timestamptz`. App role: `INSERT, SELECT` only.
- Tenant status is read by IdentityAccess through `Tenants.Contracts`, not by querying `tenants` tables.
- All new tables; no expand-contract step is needed yet. Roles: owner (Migrator) and app (API, Worker).

## Failure modes
- PostgreSQL down: tenant routes return 503 through Problem Details; never fall back to "allow".
- IdP metadata unreachable at first request: 401 for new tokens and an error log; health stays as in SPEC-0001.
- Concurrent role changes in one tenant: serialized by a tenant-level row lock (criterion 20).
- The existing `dev smoke` probe of an unrouted path will get 401 instead of 404; the smoke check is updated.

## Out of scope
Invitations and email, user profiles, MFA policy, SSO federation, support access, the worker service
identity (M5), row-level security policies (M3, ADR-0011), rate limiting (M9/M11), caching memberships.

## Open questions
All resolved by the user on 2026-10-19:
1. **Tenant creation:** platform endpoints (criteria 24, 25); the first administrator is a separate call,
   so no transaction spans two modules.
2. **Platform operator authority:** a role claim from the IdP on the platform audience for M2; a database
   operator registry arrives with support access in M9 (residual risk in the threat model).
3. **Denial status codes:** 404 for non-members and unknown tenants; 403 only for members who lack a
   permission or whose tenant is suspended.
4. **Spec size:** kept as one spec; S1 exists only to serve this boundary.
5. **ADRs 0009 to 0012:** Accepted.

**S6 decisions (owner, 2026-10-20):**
10. **Tenant context:** `ITenantContext` is a read-only contract in `IdentityAccess.Contracts`. Its only implementation is
    the membership resolver in `IdentityAccess.Application`, which sets it once per request, after every check passed.
11. **Enforcement:** each tenant route is mapped with `RequireTenantPermission(permission)`: the tenant-user policy,
    permission metadata (read by the criterion 22 route-table test), and an endpoint filter that resolves the membership
    and throws `AppException` (ADR-0016) on denial.
12. **Codes (mine, within the spec):** a non-member, an unknown tenant, and a malformed id are `tenant.not_found` (404);
    a member of a suspended tenant is `membership.tenant_suspended` (403, the spec's "tenant-suspended"); a member
    whose role lacks the permission is `membership.permission_denied` (403); an unknown or foreign membership id is
    `membership.not_found` (404). A member added through `/members` gets the caller's own (validated) issuer, which closes T-29.

**Amendment (owner, 2026-10-21, closes T-19):** tenant names are unique, compared case-insensitively after
trimming; a duplicate is 409 `tenant.name_taken`; suspended tenants keep their names (criterion 26).

**S5 decisions (owner, 2026-10-19):**
6. **Typed ids in IdentityAccess:** `TenantId` and `MembershipId` are value types inside IdentityAccess, converted from
   `Guid` at the module boundary. Tenants keeps `Guid` (T-26).
7. **Last-administrator rule (criterion 20):** IdentityAccess owns no tenant row, so every membership change runs in
   one transaction that first takes `pg_advisory_xact_lock` keyed on the tenant id. The spec text "tenant-level row
   lock" is read as this lock.
8. **S5 is split:** S5a is memberships, roles to permissions, membership audit, the last-administrator rule, and
   `GET /v1/me/memberships` (with a minimal `Tenants.Contracts` status query, which criterion 16 needs).
   S5b is `POST /v1/platform/tenants/{id}/admins`.
9. **Suspended tenants:** the first-administrator call for a suspended tenant returns 409 `tenant.suspended`
   (S5b); the operator reactivates first.

## Slices
S1 proves the riskiest assumption: database tests against Compose PostgreSQL from Windows, with role separation.

| Slice | Delivers | Criteria |
|-------|----------|----------|
| S1 | EF Core + Npgsql, Migrator host, module schemas, owner and app roles, database test category | 1, 2, 3 (except audit), 4 |
| S2 | JWT bearer validation with the test issuer, fallback policy, `GET /v1/me` | 5–8 |
| S3 | Keycloak in Compose, realm import, smoke update | 10, 11 |
| S4 | Tenants module, platform scheme (separate issuer), tenant lifecycle and audit | 9, 24 (create), 25 |
| S5a | IdentityAccess memberships, roles to permissions, membership audit, last-administrator rule, `Tenants.Contracts` status query, `GET /v1/me/memberships` | 3 (audit part), 16, 19–21 (as application behaviour; the HTTP routes are S6) |
| S5b | First administrator via `POST /v1/platform/tenants/{id}/admins` | 24 (admins) |
| S6 | Tenant context that fails closed, membership resolution, tenant-route policies | 12–15, 17, 18 |
| S7 | Isolation matrix over every tenant route, route-table test, denial telemetry (M2 gate) | 22, 23 |

## Verification
Filled in during implementation.

| Criterion | Test | Proven to fail without the change |
|-----------|------|------------------------------------|
| 1 | `MigratorTests.Migrator_applies_every_module_migration_to_an_empty_database_and_exits_zero`, `MigratorTests.Second_run_applies_nothing_and_exits_zero`; `MigratorFailureTests` (missing setting, unreachable database) | First run: yes (Migrator without IdentityAccess fails at the schema assertion). Second run: no proof; idempotency comes from EF Core's history table, and there is no TenantFlow guard to remove. Failure tests: yes (unlogged key, password in log) |
| 2 | `SchemaOwnershipTests.Module_migrations_create_objects_only_in_the_module_schema` (per module), `SchemaOwnershipTests.No_foreign_key_crosses_module_schemas` | Yes: an object in another schema, and a cross-schema foreign key, each fail at an assertion |
| 3 (S1 part) | `RolePrivilegeTests`: no elevated attributes, owns nothing, cannot run DDL (4 statements), can use schemas and gets default table privileges | Yes for attributes (BYPASSRLS), DDL (CREATE granted on schema), and default privileges (removed). Owns nothing: no runtime proof, because the owner role cannot hand ownership to the app role without a role-membership change. Audit `UPDATE`/`DELETE` denial: moved to S5 |
| 4 | `DatabaseTestSettingsTests` (missing settings, unreachable server, run id validation); leftover check in `dev.ps1 db-tests` | Yes: instructions removed, connection error not translated, run id unchecked; each fails at an assertion. The leftover check ran on every run (0 left) |
| 5 | `AuthenticationTests.Valid_token_reaches_me_with_its_issuer_and_subject` | Yes: failed at the status assertion before `/me` was wired; returning the issuer as subject fails |
| 6 | `AuthenticationTests.Invalid_token_is_rejected_with_401_and_reveals_nothing` (11 cases: missing, wrong issuer, wrong audience, unknown key, `alg: none`, HS256, public key as HMAC secret, RS512 not allowed, expired, not yet valid, no subject); `Rejection_keeps_its_status_when_the_caller_does_not_accept_json` | Yes: disabling issuer, audience, lifetime, signed-token, subject, allow-list, and signature checks each fail their cases; the default challenge fails the Problem Details assertion; `WriteAsync` instead of `TryWriteAsync` returns 500 |
| 7 | `AuthenticationTests.Host_refuses_to_start_without_a_tenant_authentication_setting`, `Host_refuses_http_metadata_for_a_non_loopback_authority`, `Host_allows_http_metadata_for_a_loopback_authority` | Yes: without `ValidateOnStart` the host starts; without the loopback rule HTTP metadata is accepted |
| 8 | `AuthenticationTests.Endpoint_without_a_policy_is_denied_to_anonymous_and_signed_in_callers`, `Health_endpoints_stay_anonymous`, `HostCompositionTests.Host_starts_and_denies_unmapped_routes_to_anonymous_callers` | Yes: a permissive fallback fails both denial tests; health without `AllowAnonymous` returns 401 |
| 9 | `PlatformBoundaryTests`: tenant token on `/platform` (401), tenant-issuer token with the platform audience (401), platform token on `/me` (401), platform token without the operator role (403), anonymous (401), missing platform settings stop startup; smoke check with a real Keycloak operator token | Yes: platform policy on the tenant scheme fails the tenant-token test; `/me` also accepting the platform scheme fails the cross-route test; no role requirement fails the 403 test |
| 10 | `dev.ps1 up` (Keycloak healthy on 127.0.0.1:8080, realm imported, dev-user password set); `dev.ps1 smoke` check "Keycloak issues a token that the API accepts on /me" (observed, 3 runs) | Yes: without the audience mapper the smoke check fails (1 of 8) |
| Amendment: `/v1` paths (ADR-0015) | `ApiVersioningTests` (route table, unversioned and `/v2` paths denied, `/v1/me` served); `TenantLifecycleTests.Operator_creates_an_active_tenant_and_the_change_is_audited` (Location header); smoke on `/v1` routes | Yes: an extra unversioned endpoint, `/me` mapped outside the group, and a hand-built Location without the version each fail |
| 11 | `dev.ps1 validate` (also in CI): every image pinned by version and digest; realm without credential properties; users `dev-*` with `@tenantflow.test` | Yes: an unpinned image, a `credentials` entry, and a real-looking email each fail validate |
| 24 (create part) | `TenantLifecycleTests.Operator_creates_an_active_tenant_and_the_change_is_audited`, `Invalid_name_is_rejected_with_400_and_writes_nothing` (4 cases), `Platform_token_without_the_operator_role_creates_nothing`, `Rejected_input_and_illegal_transitions_are_not_logged_as_errors` | Yes: skipping the validator fails the 400 cases; removing the endpoint filter returns 500 and logs errors. The `/admins` part moved to S5 |
| 25 | `TenantLifecycleTests.Suspend_and_reactivate_follow_the_lifecycle_and_each_change_is_audited_once`, `Unknown_tenant_returns_404` (3), `Concurrent_suspensions_have_exactly_one_winner` (10 rounds), `Application_role_cannot_change_tenant_audit_records` (2) | Yes: no transition guard, no audit append, no concurrency token, and no REVOKE each fail their tests |
| 3 (audit part) | `MembershipSchemaTests.Application_role_cannot_change_or_remove_membership_audit` (UPDATE, DELETE, TRUNCATE), `Application_role_can_read_and_append_membership_audit` | Yes: removing the REVOKE from the migration fails the UPDATE and DELETE cases |
| 16 | `MembershipServiceTests.Listing_returns_only_the_callers_own_active_memberships_in_active_tenants`, `Listing_matches_issuer_exactly_...`; over HTTP `MyMembershipsEndpointTests` (own memberships only, other issuer, suspended tenant and removed membership, 401 for no token or a platform token) | Yes: ignoring tenant status, ignoring the issuer, and listing removed memberships each fail their tests |
| 17 (service level) | `MembershipServiceTests.Changing_the_role_through_another_tenants_route_is_not_found_and_changes_nothing`, `An_unknown_membership_or_an_empty_id_is_not_found`. The HTTP route is S6 | Yes: removing the tenant condition from the membership lookup fails it |
| 19 (service level) | `MembershipServiceTests`: add creates an active membership; a second active add is `membership.already_exists`; the same subject from another issuer, or in another tenant, is a different member; invalid input writes nothing (6 cases). `MembershipSchemaTests`: the database refuses a second active row and an unknown role or status. The 201 over HTTP is S6 | Yes: dropping the unique filter, the role check, the issuer length check, the subject control-character rule, and case-insensitive role names each fail their tests |
| 20 | `AdministratorRuleTests`; `MembershipServiceTests.The_only_administrator_cannot_be_demoted_or_removed_and_nothing_is_written`, `With_two_administrators_...`, `Two_administrators_demoting_each_other_at_once_leave_exactly_one_winner_every_time` (20 rounds) | Yes: removing the rule from either path fails; removing the advisory lock fails the 20-round test (ADR-0018) |
| 21 | `MembershipServiceTests` (one audit row per add, role change, and removal, with actor, both roles, and clock time; none for a rejected or no-op change), `MembershipAuditRecordTests`, `MembershipAtomicityTests` (a refused audit row leaves the membership unchanged) | Yes: dropping either audit append, taking the time from `DateTimeOffset.UtcNow`, and committing the audit separately each fail |
| 24 (admins part) | `TenantAdministratorEndpointTests`: operator creates an administrator (201, audit row with the operator as actor, the user then sees the membership with their own token); a second call is 409; unknown, malformed, and empty tenant ids are 404 `tenant.not_found`; a suspended tenant is 409 `tenant.suspended`; invalid subjects are 400 on `subject` (4 cases); tenant tokens and role-less platform tokens are refused; one tenant's administrator is not in another | Yes: removing the tenant check, allowing suspended tenants, the wrong not-found error, weakening the policy, a forged issuer, the wrong actor, and the wrong role each fail their tests |
| 12 | `TenantRoutesTests.A_non_member_reading_another_tenant_gets_the_same_404_as_an_unknown_or_malformed_id` (status, code, title, type, and content type equal for another tenant, an unknown id, a malformed id, and an empty id) | Yes: resolving without the tenant condition, and removing the access filter, each fail it |
| 13 | `TenantRoutesTests.Every_route_of_another_tenant_is_404_and_changes_and_audits_nothing`, `A_valid_body_does_not_make_a_non_member_a_member` | Yes: same faults fail them |
| 14 | `TenantRoutesTests.A_removed_member_is_refused_with_the_same_token_on_the_next_call`, `A_suspended_tenant_is_403_to_its_members_and_404_to_everyone_else`; `TenantAccessResolverTests` and `MembershipServiceTests.Resolving_...` (order of checks) | Yes: reporting a suspended tenant as not found, or ignoring the issuer or tenant in the lookup, each fail |
| 15 | `TenantAccessResolverTests` (set once, every member throws when unset), `TenantMembersServiceFailClosedTests` (four use cases throw `TenantContextNotSetException` before any store or directory call) | Yes: a context that returns empty values when unset fails 5 tests; allowing a second resolution fails its test |
| 17 | `TenantRoutesTests.An_administrator_cannot_change_or_remove_another_tenants_membership_through_their_own_tenants_route` (404 `membership.not_found`, B unchanged, no audit row), plus the S5a service-level test | Yes: removing the tenant condition from the membership lookup fails it |
| 18 | `TenantRoutesTests.A_customer_can_read_the_tenant_but_cannot_list_or_manage_members` (list, add, promote self, demote an administrator, remove self: all 403 `membership.permission_denied`, nothing changes) | Yes: weakening the add route's permission, and skipping the permission check, each fail it |
| 19 to 21 (HTTP) | `TenantRoutesTests.An_administrator_lists_adds_changes_and_removes_members_...` (201, 409 duplicate, 200, list, 204, audit rows with the caller as actor), `The_only_administrator_cannot_be_demoted_or_removed_over_http`, `Invalid_input_from_an_administrator_is_400_...` (3 cases) | Yes: listing the wrong tenant and a wrong audit actor each fail it |
| Boundaries | `TenantRoutesTests.Without_a_token_or_with_a_platform_token_every_tenant_route_is_401` (5 routes), `The_members_of_a_tenant_see_the_tenant_name_and_id_only` | Covered by the S2 fallback policy and scheme tests |
| 22 | `RouteTableTests`: the route table is an exact reviewed list (14 routes) with a named policy on each; only health probes are anonymous; every tenant route names a permission that exists and every permission is required by some route | Yes: removing the `/me` policy, and removing the permission metadata, each fail it |
| 23 | `AuthDenialTelemetryTests`: each denial is counted once in `tenantflow.auth.denials` with exactly the labels `scheme` and `reason` (missing token, refused token on either scheme, platform caller without the operator role, route with no policy); allowed requests and health probes are not counted; logs name the denial but never contain the token, its parts, the subject, or the `Authorization` header. Membership denials (`not_a_member`, `tenant_suspended`, `permission_denied`) are counted and checked in the isolation matrix | Yes: not counting, an extra label, a wrong reason, a missing forbidden count, a missing tenant-route count, and a reason built from the `Authorization` header each fail |
| Isolation matrix (M2 gate) | `IsolationMatrixTests`: the tenant routes are read from the host's own route table, so a route added later is covered automatically. Each is called by anonymous, platform operator, non-member, removed member, administrator of an unknown tenant and of a malformed id, member of a suspended tenant, and customer, and must answer exactly as the boundary says, change no membership or audit row when it denies, count the denial once with the right labels, and answer a real tenant, an unknown id, and a malformed id with the same shape | Yes: the access filter, the permission metadata, and the denial count each fail it |
| 26 | `TenantLifecycleTests`: a name equal ignoring case and surrounding whitespace is 409 `tenant.name_taken` and writes no tenant and no audit row (4 cases); a different name is created and the conflict body names no tenant; a suspended tenant keeps its name; a duplicate is an expected outcome and is not logged as an error (a read before the insert); `_` and `%` in a name are ordinary characters in that read; the same name created at once by two operators has exactly one winner (10 rounds); the database itself refuses a duplicate with the `ux_tenants_name_lower` index | Yes: removing the store's mapping, a case-sensitive index, an index limited to active tenants, no index, no pre-check, and unescaped wildcards each fail their tests |
