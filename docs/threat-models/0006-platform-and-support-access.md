# Threat model: platform and support access

- Status: Draft (M9), written with SPEC-0009 slice S1
- Date: 2026-10-22
- Owner: TenantFlow tech lead (the user)
- Scope: the platform boundary as built in M2 (platform scheme, issuer and audience, the `/v1/platform` routes, tenant
  lifecycle, first tenant administrator, the `platform-operator` role claim, the one-hour platform token cap) and the M9
  design for platform administrators and support operators (role split, support grants, read-only support views,
  platform audit read). Tenant routes are covered by threat model 0001.
- Related: blueprint sections 2.11, 3, 8, 13; ADR-0009, ADR-0010, ADR-0026 (Proposed), ADR-0027 (Proposed);
  [SPEC-0002](../specs/0002-identity-and-tenant-boundary.md), [SPEC-0009](../specs/0009-security-and-privacy.md);
  [threat model 0001](0001-tenant-boundary.md)

## Assets and data classification

| Asset | Classification | Why it matters |
|---|---|---|
| Platform access tokens | Secret | Bearer credentials for the most powerful routes; theft equals operator authority until expiry |
| The `tenantflow_roles` claim in the platform realm | Integrity-critical | It is the only source of operator authority today |
| Tenant records and lifecycle status | Internal, tenant-confidential | Suspension cuts a whole customer off; names are business-confidential |
| First tenant administrator membership | Confidential | Whoever is named controls the tenant |
| `tenants.tenant_audit` and, in M9, `audit.audit_records` | Confidential, integrity-critical | Evidence of every platform and support action |
| Support grants (M9: tenant, operator reference, ticket reference, window) | Confidential | A grant is time-limited read authority over one tenant |
| Tenant data shown in support views (M9: members, jobs) | Tenant-confidential | Read across the tenant boundary by people outside the tenant |

Personal data in scope: operator and tenant-user subjects (pseudonymous identifiers). `tenant_audit` stores the operator
issuer and subject today; the M9 audit record stores a keyed operator reference instead (SPEC-0009 Q9). The ticket
reference must match a fixed pattern, so it cannot carry free text.

## Actors and trust boundaries

Actors: anonymous caller; tenant user (customer or tenant administrator); platform operator (today); platform
administrator and support operator (M9, ADR-0027); identity-provider administrator; developer or operator with database
access.

| Boundary | Crossed by | Control |
|---|---|---|
| P1 Internet → platform routes | every `/v1/platform` request | `Platform` JWT scheme with its own issuer and audience; deny-all fallback and default policies |
| P2 Platform identity → operator authority | lifecycle and first-administrator routes | `PlatformOperator` policy requires `tenantflow_roles` = `platform-operator` |
| P3 Tenant boundary ↔ platform boundary | tokens sent to the wrong route group | issuer separation enforced at startup; each policy names exactly one scheme |
| P4 Support operator → one tenant's data (M9) | `/v1/platform/support/tenants/{tenantId}/...` | own active grant for that tenant, access record committed before the read |
| P5 IdP administration → platform realm | role assignment, token lifetime, MFA | outside TenantFlow; only the one-hour cap is enforced by the API |
| P6 Person → PostgreSQL directly | incident or disaster work | none in code; a break-glass runbook is M13 |

## Entry points and data flows

1. `POST /v1/platform/tenants`, `GET /v1/platform/tenants/{id}`, `POST .../{id}/suspend`, `POST .../{id}/reactivate`:
   platform token → `PlatformOperator` policy → `TenantLifecycleService` → tenant row and a `tenant_audit` row with the
   actor's issuer and subject.
2. `POST /v1/platform/tenants/{id}/admins`: platform token → policy → `MembershipService` adds an administrator
   membership under the tenant issuer, audited in `membership_audit`.
3. M9: `GET /v1/platform/audit` (platform-admin) reads audit records, platform-only events included.
4. M9: `POST /v1/platform/support/grants` and `DELETE /v1/platform/support/grants/{grantId}` open, end or revoke a
   grant; each is audited and visible to the tenant.
5. M9: `GET /v1/platform/support/tenants/{tenantId}/members` and `.../jobs`: grant check and `support.tenant_accessed`
   intent in one transaction, then a tenant-scoped read through module `Contracts` with a context holding no tenant
   permissions.
6. Local IdP: the Keycloak realm `tenantflow-platform` issues tokens with a 300-second lifetime to a synthetic user.

## Abuse cases / attack paths

| Id | Attack | Attacker goal | Path |
|---|---|---|---|
| PS-01 | Tenant token reuse on platform routes | A tenant user runs platform operations | Send a tenant token, or a tenant-issuer token with the platform audience, to `/v1/platform` |
| PS-02 | Platform identity without the role | Any platform-realm user acts as operator | Call a platform route with a valid platform token that lacks `platform-operator` |
| PS-03 | Compromised IdP administrator | Mint operator authority, or a tenant token for any subject | Assign the role in the platform realm, or issue tokens directly |
| PS-04 | Operator overreach | One person does everything: lifecycle, admins, audit, support | A single `platform-operator` role covers every platform route today |
| PS-05 | Support access without reason, time limit or audit | Browse tenant data unnoticed | Use direct database access, or a support path without a recorded reason and window |
| PS-06 | Cross-tenant read through a grant | Read tenant B with a grant for tenant A, or another operator's grant | Change `tenantId` in the support route or reuse a foreign grant id |
| PS-07 | Grant replay | Keep reading after the window | Call support views after expiry, end or revocation |
| PS-08 | Impersonation | Act as a tenant user with that user's permissions | A "log in as" feature or a token issued on a user's behalf |
| PS-09 | Support writes | Change tenant data under a read grant | Find or add a support route that writes |
| PS-10 | Unaudited or alterable platform operations | Suspend a tenant or name an administrator without evidence | A path that skips the audit write, or UPDATE/DELETE on audit rows |
| PS-11 | Platform audit read exposure | A support operator or tenant user reads cross-tenant audit | Call `GET /v1/platform/audit` with the wrong role or a tenant token |
| PS-12 | Stale platform token | Keep operator authority after the person leaves | Use a stolen or long-lived token |
| PS-13 | No MFA on operator sign-in | Take over an operator with a password | Phish or guess the platform-realm password |
| PS-14 | Break-glass database access | Read or change any tenant outside every control | Connect with the app or owner role during an incident |
| PS-15 | New platform route without a full denial check | A route is reachable by the wrong caller kind | Add a route with the wrong policy; only the route table and per-route tests catch it today |
| PS-16 | Brute force and probing | Guess tenant ids or overload platform routes | High request volume; no rate limit exists |

## Existing controls

Verified in code and tests at the start of M9:
- Separate `Tenant` and `Platform` schemes. The host refuses to start when they share an issuer or an audience, even
  differing only in case or a trailing slash (`AuthenticationExtensions`, `IssuerSeparation`).
- Strict validation: asymmetric algorithms only, issuer, audience, lifetime and signature required, 30-second clock
  skew, a required subject, no error details to callers.
- Platform tokens living longer than one hour, or whose lifetime cannot be worked out, are refused (401).
- The fallback and default policies deny everyone; the route table is an exact reviewed list with a policy per route.
- Platform routes require `tenantflow_roles` = `platform-operator`; an authenticated caller without it gets 403, counted
  as `not_operator` with bounded labels only.
- Tenant creation, suspension and reactivation write one `tenant_audit` row each; the app role cannot UPDATE, DELETE or
  TRUNCATE it. The first administrator is audited in `membership_audit`.
- The local platform realm file holds one synthetic user, no credential, and a 300-second token lifetime.

## Gaps and residual risk

| Gap | Residual risk | Until |
|---|---|---|
| Operator authority is an IdP claim (PS-03) | An IdP administrator can mint platform authority or tenant tokens | M12 (production IdP, operator registry) |
| One role does everything (PS-04) | No least privilege between lifecycle, audit and support | M9 (AC 15) |
| No support path (PS-05 to PS-09) | Incidents need database access, which bypasses every control | M9 (AC 16 to 18); break-glass runbook M13 |
| Platform events only in `tenant_audit` (PS-10) | No central, tenant-visible record; operator subject stored in clear | M9 (AC 2, 3, 5) |
| Audit is not hash-chained (PS-10) | A database owner could alter history without detection | Accepted; reviewed M13 |
| No revocation before expiry, no MFA in code (PS-12, PS-13) | A stolen token works up to one hour; password-only sign-in is possible | M12 |
| No platform route matrix (PS-15) | A wrong policy on a new route relies on per-route tests | M9 (AC 15) |
| No rate limiting (PS-16) | Probing and load on platform routes | M11 |

## Mitigations with owner and due date

| Id | Abuse case | Mitigation | Owner | Due | Status | Evidence |
|---|---|---|---|---|---|---|
| PS-A01 | PS-01 | Separate platform scheme, issuer and audience; tokens never accepted across boundaries; shared issuer or audience stops startup | Claude | M2 | done | `PlatformBoundaryTests.Tenant_token_is_rejected_on_platform_routes`, `PlatformBoundaryTests.Tenant_issuer_token_with_the_platform_audience_is_still_rejected`, `PlatformBoundaryTests.Platform_token_is_rejected_on_tenant_routes`, `AuthenticationHardeningTests.Tenant_and_platform_schemes_may_not_share_an_issuer_or_an_audience` |
| PS-A02 | PS-02 | Platform routes require the `platform-operator` role claim; anonymous callers are challenged | Claude | M2 | done | `PlatformBoundaryTests.Platform_token_without_the_operator_role_is_forbidden`, `PlatformBoundaryTests.Anonymous_caller_is_challenged_on_platform_routes`, `TenantLifecycleTests.Platform_token_without_the_operator_role_creates_nothing`, `TenantAdministratorEndpointTests.Only_platform_operators_reach_the_route` |
| PS-A03 | PS-03 | Keep IdP-claim authority in M9; the one-hour cap and audit bound the window | user | M12 | accepted | Approved in SPEC-0009 Q9; no deployed environment exists and the local realm holds synthetic users only |
| PS-A04 | PS-03 | Decide a TenantFlow-side operator registry together with the production IdP | user | M12 | planned | M12 production IdP |
| PS-A05 | PS-04 | Split `platform-operator` into `platform-admin` and `support-operator`, neither implying the other | Claude | M9 | done | `SupportAccessTests.Every_platform_route_answers_each_kind_of_caller_as_its_role_says` |
| PS-A06 | PS-05 | Support grants for one tenant with a patterned ticket reference, at most 240 minutes, audited and visible to the tenant | Claude | M9 | done | `SupportAccessTests.A_support_operator_opens_one_audited_grant_with_a_ticket_and_a_bounded_duration` |
| PS-A07 | PS-06 | Each support read checks the caller's own active grant for the route tenant; anything else is 404 and nothing is read | Claude | M9 | done | `SupportAccessTests.The_views_need_the_callers_own_active_grant_for_that_tenant_and_record_each_access_before_reading` |
| PS-A08 | PS-07 | Expired, ended or revoked grants answer 404 on the next access, and the end is audited | Claude | M9 | done | `SupportAccessTests.An_ended_revoked_or_expired_grant_answers_404_from_the_next_access_and_each_end_is_audited` |
| PS-A09 | PS-08 | No impersonation route; tokens come only from the IdPs, and the exact route table makes a new route a reviewed change | Claude | M2 | done | `RouteTableTests.The_route_table_is_exactly_the_reviewed_list_with_a_policy_on_every_route` |
| PS-A10 | PS-09 | Support views are read-only queries in module Contracts; no support route writes tenant data | Claude | M9 | done | `SupportAccessTests.The_views_need_the_callers_own_active_grant_for_that_tenant_and_record_each_access_before_reading`, `RouteTableTests` |
| PS-A11 | PS-10 | Lifecycle and first-administrator changes write an audit row; the app role cannot change `tenant_audit` | Claude | M2 | done | `TenantLifecycleTests.Operator_creates_an_active_tenant_and_the_change_is_audited`, `TenantLifecycleTests.Suspend_and_reactivate_follow_the_lifecycle_and_each_change_is_audited_once`, `TenantLifecycleTests.Application_role_cannot_change_tenant_audit_records`, `TenantAdministratorEndpointTests.Operator_creates_an_administrator_the_membership_is_audited_and_the_user_can_see_it` |
| PS-A12 | PS-10 | Central append-only audit through the outbox with a keyed operator reference, including support grant events | Claude | M9 | done | `SupportAccessTests.A_support_operator_opens_one_audited_grant_with_a_ticket_and_a_bounded_duration`, `SupportAccessTests.An_ended_revoked_or_expired_grant_answers_404_from_the_next_access_and_each_end_is_audited` |
| PS-A13 | PS-10 | Hash-chained or WORM audit store not built | user | M13 | accepted | Privileges and an update trigger are the approved tamper control (SPEC-0009 Q4); a hash chain is a revisit trigger |
| PS-A14 | PS-11 | Platform audit read requires `platform-admin`; support operator 403, tenant token 401 | Claude | M9 | done | `SupportAccessTests.Every_platform_route_answers_each_kind_of_caller_as_its_role_says` |
| PS-A15 | PS-12 | Platform tokens living longer than one hour are refused | Claude | M2 | done | `AuthenticationHardeningTests.Platform_tokens_may_live_at_most_one_hour` |
| PS-A16 | PS-12 | Token revocation before expiry depends on the IdP | user | M12 | accepted | Tokens live at most one hour and authority is re-read from the claim per request; revisit with the production IdP |
| PS-A17 | PS-13 | Enforce MFA for platform and support operators in the production IdP | user | M12 | planned | M12 production IdP |
| PS-A18 | PS-14 | Break-glass database access runbook with reason, approval and after-the-fact review | user | M13 | planned | M13 disaster recovery runbook |
| PS-A19 | PS-15 | Route table requires a reviewed policy on every route; deny-all fallback | Claude | M2 | done | `RouteTableTests.Only_health_probes_and_the_signed_webhook_are_anonymous_and_no_other_route_lacks_a_named_policy`, `AuthenticationHardeningTests.A_policy_name_that_is_forgotten_never_grants_access` |
| PS-A20 | PS-15 | Platform route matrix walks every `/platform` route as each caller kind | Claude | M9 | done | `SupportAccessTests.Every_platform_route_answers_each_kind_of_caller_as_its_role_says` |
| PS-A21 | PS-16 | Rate limiting on platform routes | Claude | M11 | planned | M11 workload model |

## Security tests and monitoring

- Tests: `PlatformBoundaryTests`, `AuthenticationHardeningTests`, `TenantLifecycleTests`,
  `TenantAdministratorEndpointTests` and `RouteTableTests` today; in M9 the platform route matrix and the grant
  lifecycle tests of ADR-0027 (expiry with a fake `TimeProvider`, revocation, foreign grant, foreign tenant, access
  record committed before the read), each with a break-it proof.
- Monitoring: `tenantflow.auth.denials` counts platform denials by scheme and reason (`no_token`, `invalid_token`,
  `not_operator`) without subjects or tenant ids (`AuthDenialTelemetryTests`). Alerts on denial spikes and on support
  grant volume are M10.
- Tenants see every support access in their own audit log (M9), which is a detection control outside the platform team.

## Review date and change triggers

Review at the M9 gate (after slice S7), then at M12 (production IdP, MFA, operator registry) and M13 (break-glass
runbook, audit tamper evidence). Also review when a platform route is added, support needs to write data, a customer asks
to approve support access, a new operator role is proposed, or the 240-minute limit proves impractical.
