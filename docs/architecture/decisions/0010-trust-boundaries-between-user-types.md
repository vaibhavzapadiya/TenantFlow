# ADR-0010: Trust boundaries between user types

- Status: Accepted
- Date: 2026-10-19
- Owners: TenantFlow tech lead (decision approved by the user on 2026-10-19)
- Related decisions: ADR-0009, ADR-0011
- Closes: Open Decision 11 (platform and support detail completed by M9)

## Context
Blueprint section 2.11 requires that customer users, tenant administrators, platform administrators,
support operators, and background workers never share implicit authority. Section 8 asks for hybrid
authorization (authentication, membership, role-to-permission, resource rules), a separate path for
platform authority, and service identities that never reuse a human's permissions. The most common way
this fails is a single token type with a "user type" or "admin" claim, where one wrong policy turns a
tenant administrator into a platform administrator.

## Decision
- **Tenant boundary (customer users and tenant administrators).** One authentication scheme. Authority
  comes only from an **active membership** in an **active tenant**, loaded from TenantFlow's own data,
  never from token claims. A token may say who the user is; it never says which tenant they may act in
  or what role they hold. The tenant is selected by the route (`/tenants/{tenantId}/...`); selection
  without a membership is denied. Roles map to named permissions in business language; there is no
  broad `Admin` role. Customer and tenant administrator differ only by permissions inside a membership.
- **Platform boundary (platform administrators, later support operators).** A separate authentication
  scheme with its own audience, and a separate `/platform` route group whose policies require that
  scheme. Tenant tokens are never accepted there, and platform tokens are never accepted on tenant
  routes. Platform operations produce audit records. In M2 the group exists and denies every request
  by default; platform operations and support access (reason, time limit, audit) are designed by M9.
- **Service boundary (workers and internal callers).** Service identities use the client-credentials
  flow with explicit capability scopes. A worker establishes tenant context from trusted persisted job
  or message metadata, validates it, and never inherits tenant state from a previous job.
- **Fail closed everywhere.** An endpoint without an explicit policy is denied (fallback policy), and
  tenant-owned code with no tenant context throws rather than running unscoped.

Out of scope: support-operator workflows and platform administration features (M9), and the
service-identity implementation (with the first worker that handles tenant data, M5).

## Options considered
- **Separate schemes and route groups (chosen).** Boundaries are structural: a policy mistake on one
  side cannot accept the other side's tokens. Costs two scheme configurations and more test setup.
- **One scheme with a user-type claim and policies.** Simpler, but authority depends on every policy
  being right, and a single bug escalates privilege across boundaries.
- **A separate host for platform administration.** Strongest isolation (separate deployable and
  network exposure), but more to build and operate than the project needs now. It stays a later option,
  made cheap by the separate route group.

## Consequences
- Easier: reviewing authorization, because the boundary is visible in the route table and schemes.
- Harder: every request on a tenant route needs a membership lookup (a candidate for short-lived,
  tenant-keyed caching later, with a documented revocation delay).
- Permission changes take effect on the next request, because memberships are read from the database,
  not from tokens.

## Validation
- API tests: a tenant-scheme token is rejected on `/platform`; a platform-scheme token is rejected on
  tenant routes; a request without membership, with a removed membership, or for a suspended tenant is
  denied; an endpoint without a policy is denied by the fallback policy.
- The `slice` skill's tenant isolation checklist on each change that touches these paths.

## Implementation note (2026-10-19, M2-S4)
The user chose the stricter form of the platform boundary: the platform scheme trusts a **separate
issuer** as well as its own audience (locally the Keycloak realm `tenantflow-platform`). A tenant token
then fails issuer validation on `/platform` even if a client mapper adds the wrong audience. Operator
authority is the provider-neutral claim `tenantflow_roles` containing `platform-operator` (SPEC-0002 Q2).

## Hardening (pre-S5 review 2.6, 2026-10-19)
- **Separate issuers are enforced, not just recommended.** The host refuses to start if the tenant and platform
  schemes share an issuer or an audience (case and a trailing slash do not make them different). Copying one
  configuration block over the other would otherwise collapse this boundary silently.
- **Platform tokens may live at most one hour.** A longer-lived platform token is refused (401), and so is one
  whose lifetime cannot be worked out. The API cannot see how the identity provider is configured, and these
  are the most powerful credentials in the system. Tenant tokens are not capped.
- **No scheme authenticates by default, and the fallback and default policies name no scheme.** Tokens are read
  only where a policy names a scheme. The authorization middleware evaluates the fallback policy even for
  `AllowAnonymous` endpoints, so a fallback naming the tenant scheme made `/health/live` validate any bearer
  token it was sent and wait for the identity provider (breaking SPEC-0001: liveness never depends on an
  external system). A path with no policy now answers 401 to everyone.
- **A forgotten policy name never grants access.** `RequireAuthorization()` with no name uses a default policy
  that denies everyone, like the fallback.
- **Calls to the identity provider time out after 10 seconds**, not the library's 60.

Known limitations, accepted for now: while the identity provider is unreachable, requests that carry a token are
answered 401 (the library reports the failure as an invalid token) and the first few wait for the timeout. 503
`authentication.unavailable` would be more honest, but needs a wrapper around the library's configuration
manager; revisit with alerting in M10. Authentication failures are logged below Warning and are not counted
anywhere until SPEC-0002 criterion 23 (S7).

## Revisit triggers
Support access is designed (M9); a customer needs delegated cross-tenant access (for example a parent
organization); membership lookups become a measured performance problem.

## Implementation note (2026-10-20, M2-S6)
Tenant routes are mapped with `RequireTenantPermission(permission)`. An endpoint filter resolves the caller's active
membership in the route's tenant, then the tenant's status, then the role's permission, in that order, so a denial
reveals as little as possible: a non-member sees `tenant.not_found` whatever the tenant's state, and only members see
`membership.tenant_suspended` or `membership.permission_denied`. The resolver is the request's `ITenantContext`
(read-only, in `IdentityAccess.Contracts`) and sets it once. Handlers act on the context's tenant, never on the route's text.
