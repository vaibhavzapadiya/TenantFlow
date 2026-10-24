# Threat model: tenant boundary (identity, membership, tenant context)

- Status: Reviewed at M9 start (2026-10-22)
- Date: 2026-10-22 (first draft 2026-10-19, M2)
- Owner: TenantFlow tech lead (the user)
- Scope: the API's authentication, the tenant and platform route boundaries, tenant context for requests and for the
  Worker, memberships and roles, row-level security on tenant-owned tables, and the Tenants and IdentityAccess
  persistence. Business workflows have their own threat models; this one covers the boundary they all rely on.
- Related: blueprint sections 8, 13, 15; ADR-0009 to ADR-0012, ADR-0021, ADR-0022; [SPEC-0002](../specs/0002-identity-and-tenant-boundary.md),
  [SPEC-0009](../specs/0009-security-and-privacy.md)

## Assets and data classification

| Asset | Classification | Why it matters |
|---|---|---|
| Tenant records (id, name, status) | Internal, tenant-confidential | Existence and names of customers are business-confidential |
| Memberships and roles (tenant, issuer + subject, role, status) | Confidential | They *are* the authority: changing one grants access |
| Membership and tenant audit records | Confidential, integrity-critical | Evidence of who granted what; must be append-only |
| Tenant-owned business rows (all module schemas) | Tenant-confidential | One tenant's data must never reach another tenant |
| Access tokens (tenant and platform audiences) | Secret | Bearer credentials; theft equals impersonation until expiry |
| IdP signing keys (published JWKS) | Integrity-critical | Trusting a wrong key means accepting forged identities |
| Database roles and connection strings | Secret | The owner role can change schema; the app role reaches every tenant's rows when a tenant is set |
| Telemetry (logs, traces, metrics) | Internal | Can leak tokens, subjects, or tenant data if careless |

Personal data in scope: the `sub` identifier (pseudonymous) and tenant names. TenantFlow stores no email, name, or
password (ADR-0009). A removed membership keeps its issuer and subject today (see TB-21).

## Actors and trust boundaries

Actors (blueprint section 3): anonymous caller; customer user; tenant administrator; platform operator (one IdP role,
`platform-operator`, until SPEC-0009 splits it); support operator (not built yet); Worker (service identity);
developer/operator with database access; identity-provider administrator.

| Boundary | Crossed by | Control |
|---|---|---|
| B1 Internet → API | every request | JWT validation per scheme (ADR-0009); fallback policy denies; no default scheme |
| B2 Authenticated user → tenant | `/v1/tenants/{tenantId}/…` | active membership in an active tenant, read from the database per request (ADR-0010) |
| B3 Tenant routes ↔ platform routes | `/v1/platform/…` | separate scheme, issuer and audience; startup refuses shared values |
| B4 Role → permission | tenant operations | one named permission per route; customer holds four read/place permissions |
| B5 API and Worker → PostgreSQL | every data access | non-owner app role without `BYPASSRLS`; forced RLS keyed on `app.tenant_id`, set per transaction (ADR-0011, ADR-0012) |
| B6 Migrator → PostgreSQL | deployments | owner role, used only by the Migrator |
| B7 Worker → tenant data | messages and timers | `ServiceTenantContext` entered once per message or timer from the envelope or timer row; no membership, no permission (ADR-0022) |

## Entry points and data flows

1. `GET /v1/me` and `GET /v1/me/memberships`: token → identity (issuer + subject) → memberships of that identity.
2. Tenant routes (31 at end of M8): token → identity → route `tenantId` *selects* the tenant → membership and tenant
   status lookup → permission check → tenant-scoped transaction (`set_config('app.tenant_id', …, true)`) → response.
3. Membership changes (add, change role, remove): as 2, under a tenant-level lock, plus an audit row in the same transaction.
4. Platform routes: platform token → `platform-operator` role → tenant lifecycle or first administrator → audit row.
5. Worker: broker message or due timer → new DI scope → `ServiceTenantContext.Enter(tenant)` → tenant-scoped transaction.
6. Migrator: owner role → DDL per module schema. Health endpoints: anonymous, status word only (SPEC-0001).

## Abuse cases / attack paths

| Id | Attack | Attacker goal | Path |
|---|---|---|---|
| TB-01 | Cross-tenant read by route | Read tenant B as a member of A | Change `tenantId` in the route |
| TB-02 | Object-level reference across tenants (BOLA) | Change B's membership | Use A's route with a membership id from B |
| TB-03 | Tenant-context confusion | Run a use case with no tenant or a stale one | Code path that skips context setup, or reused scoped state |
| TB-04 | Existence probing | Learn which tenant ids exist | Compare 403 versus 404, or answer shapes, on tenant routes; brute-force at volume |
| TB-05 | Privilege escalation inside a tenant | Customer becomes tenant admin | Call role-change endpoint; self-grant |
| TB-06 | Removing the last administrator | Lock a tenant out of its own management | Two admins demote each other concurrently |
| TB-07 | Stale authority | Keep access after removal or suspension | Authority cached in token claims or in memory; stolen token used until expiry |
| TB-08 | Forged or misvalidated token | Act as any user | `alg: none`, HS256 with a public key, wrong issuer or audience, expired token, unknown key |
| TB-09 | Cross-boundary token reuse | Tenant user reaches platform operations, or vice versa | Send a tenant token to `/platform` |
| TB-10 | Unprotected endpoint | Anonymous access to a new endpoint | Endpoint added without a policy |
| TB-11 | Audit tampering | Hide a role grant | Update or delete audit rows through the app role or the owner role |
| TB-12 | Database privilege abuse | Change schema or bypass RLS | App connects as owner, superuser or a `BYPASSRLS` role |
| TB-13 | Telemetry leakage | Harvest tokens or tenant data | Tokens or bodies in logs; tenant ids as unbounded metric labels |
| TB-14 | Local IdP leakage | Reuse local credentials elsewhere | Realm file or `.env` with real or shared credentials committed |
| TB-15 | Worker tenant inheritance | Process B's message with A's context | Scoped state reused across messages or timers |
| TB-16 | RLS bypass by missing or leaked tenant setting | Read or write rows of another tenant | Query outside a tenant-scoped transaction; pooled connection keeps the previous tenant |
| TB-17 | Background job outlives the requester's rights | Finish an import or export after removal or suspension | Job queued before the change keeps running (M8 deferred L10) |
| TB-18 | Service context used as a user | Worker code performs a member-only action | Permission or owner check passes under the service context |
| TB-19 | Over-broad platform authority | Read or change any tenant without a recorded reason | One operator role covers lifecycle and any future support path; support needs direct DB access |
| TB-20 | IdP administrator mints platform authority | Gain platform operations | Add the `platform-operator` role in the IdP |
| TB-21 | Identifiers kept after removal | Link a removed person to a tenant indefinitely | Removed membership keeps issuer and subject forever |

## Existing controls (end of M8, verified in code and tests)

- Strict JWT validation per scheme with an algorithm allow-list; invalid tokens get 401 that reveals nothing
  (`AuthenticationTests`). Tenant and platform schemes may not share issuer or audience, compared case- and
  slash-insensitively; no default scheme; platform tokens live at most one hour; IdP calls time out after ten seconds
  (`AuthenticationHardeningTests`, `AuthenticationExtensions.cs`).
- Tenant tokens are refused on platform routes and vice versa; a platform token without `platform-operator` is 403
  (`PlatformBoundaryTests`).
- The route-table test pins the reviewed route list, allows only health probes and the signed webhook to be anonymous,
  and requires every tenant route to name an existing permission (`RouteTableTests`).
- The isolation matrix reads every tenant route from the host's route table (31 routes) and calls each as anonymous,
  platform operator, non-member, removed member, unknown and malformed tenant, suspended-tenant member, customer and
  administrator. Denials match the boundary, change no data, look identical for real and unknown tenants, and are
  counted once with `scheme` and `reason` labels only (`IsolationMatrixTests`).
- Membership and tenant status are resolved per request; removed memberships, other issuers and missing tenants do not
  resolve, and the context is set once per request (`TenantAccessResolverTests`, `TenantRoutesTests`).
- Tenant-owned services fail closed when no context is set (`TenantMembersServiceFailClosedTests`).
- Last-administrator rule in the domain and under a tenant lock (`AdministratorRuleTests`, `MembershipServiceTests`).
- Forced RLS on every tenant-owned table; no rows visible or writable without a tenant; pooled connections do not keep
  the previous tenant (`RowLevelSecurityTests`). The app role has no elevated attributes, owns nothing and cannot run
  DDL (`RolePrivilegeTests`).
- Audit tables accept only `INSERT, SELECT` from the app role (`MembershipSchemaTests`, `TenantLifecycleTests`); a
  membership change does not survive a failed audit write (`MembershipAtomicityTests`).
- Worker: `ServiceTenantContext` fails closed unentered, can be entered once, holds no permission or membership
  (`ServiceTenantContextTests`). Consumers and timers act in their own tenant only (`OrderPaymentSagaTests`,
  `NotificationConsumerTests`, `PaymentReconciliationTests`). Renewals of a suspended tenant wait
  (`SubscriptionRenewalTests`). Imports and exports do not re-check yet (TB-17).
- Denials are counted in `tenantflow.auth.denials`; logs never carry the token or the authorization header
  (`AuthDenialTelemetryTests`); production logs hold no SQL and no tenant names (`TenantLifecycleTests`).

## Gaps and residual risk

| Gap | Residual risk | Handled by |
|---|---|---|
| No rate limiting on tenant or platform routes (TB-04) | Probing at volume and noisy-neighbour load | M11 workload model |
| Token revocation before expiry depends on the IdP (TB-07) | A stolen token authenticates until expiry; membership is still re-checked per request | M12 production IdP |
| Platform authority comes from an IdP role claim (TB-20) | A compromised IdP admin can mint platform authority | Accepted; M12 operator registry review |
| One operator role, no support path (TB-19) | Incidents need DB access; no least-privilege split | SPEC-0009 AC 15–18 |
| Imports and exports ignore removal and suspension (TB-17) | A removed manager's job finishes | SPEC-0009 AC 11 |
| Audit rows can be changed by the owner role (TB-11) | A DB owner can rewrite history | SPEC-0009 AC 3; hash chain accepted |
| Removed members keep their subject (TB-21) | Indefinite linkage of a person to a tenant | SPEC-0009 AC 9 |
| Security review is manual while there is no remote | Reviewer blind spots | Accepted until the owner adds a remote |

## Mitigations with owner and due date

| Id | Abuse case | Mitigation | Owner | Due | Status | Evidence |
|---|---|---|---|---|---|---|
| TB-A01 | TB-01, TB-04 | Non-members get 404 identical to an unknown or malformed tenant on every tenant route | Claude | M2 | done | `IsolationMatrixTests.Every_tenant_route_answers_every_kind_of_caller_exactly_as_the_boundary_says`, `TenantRoutesTests.A_non_member_reading_another_tenant_gets_the_same_404_as_an_unknown_or_malformed_id` |
| TB-A02 | TB-02 | Lookups by tenant and id; another tenant's membership id is 404 and changes nothing | Claude | M2 | done | `TenantRoutesTests.An_administrator_cannot_change_or_remove_another_tenants_membership_through_their_own_tenants_route`, `MembershipServiceTests.Changing_the_role_through_another_tenants_route_is_not_found_and_changes_nothing` |
| TB-A03 | TB-03 | Scoped tenant context that throws when unset, set once per request | Claude | M2 | done | `TenantMembersServiceFailClosedTests`, `TenantAccessResolverTests.The_context_can_be_set_once_per_request` |
| TB-A04 | TB-05 | One permission per route; customers lack member management | Claude | M2 | done | `RouteTableTests.Every_tenant_route_names_a_permission_that_exists_and_every_permission_is_required_somewhere`, `TenantRoutesTests.A_customer_can_read_the_tenant_but_cannot_list_or_manage_members` |
| TB-A05 | TB-06 | Last-administrator rule under a tenant-level lock | Claude | M2 | done | `AdministratorRuleTests.The_only_administrator_cannot_be_demoted_or_removed`, `MembershipServiceTests.Two_administrators_demoting_each_other_at_once_leave_exactly_one_winner_every_time` |
| TB-A06 | TB-07 | Membership and tenant status read per request, never from tokens | Claude | M2 | done | `TenantRoutesTests.A_removed_member_is_refused_with_the_same_token_on_the_next_call`, `TenantRoutesTests.A_suspended_tenant_is_403_to_its_members_and_404_to_everyone_else` |
| TB-A07 | TB-07 | Token revocation before expiry through the production IdP | user | M12 | planned | M12 production IdP |
| TB-A08 | TB-08 | Strict JWT validation and algorithm allow-list; platform token lifetime cap | Claude | M2 | done | `AuthenticationTests.Invalid_token_is_rejected_with_401_and_reveals_nothing`, `AuthenticationHardeningTests.Platform_tokens_may_live_at_most_one_hour` |
| TB-A09 | TB-09 | Separate schemes, issuers and audiences, refused at startup when shared | Claude | M2 | done | `PlatformBoundaryTests`, `AuthenticationHardeningTests.Tenant_and_platform_schemes_may_not_share_an_issuer_or_an_audience` |
| TB-A10 | TB-10 | Fallback policy denies; route-table test pins every route and its policy | Claude | M2 | done | `RouteTableTests`, `AuthenticationHardeningTests.A_policy_name_that_is_forgotten_never_grants_access` |
| TB-A11 | TB-11 | App role has only `INSERT, SELECT` on audit tables; change and audit commit together | Claude | M2 | done | `MembershipSchemaTests.Application_role_cannot_change_or_remove_membership_audit`, `TenantLifecycleTests.Application_role_cannot_change_tenant_audit_records`, `MembershipAtomicityTests.A_membership_change_does_not_survive_a_failed_audit_write` |
| TB-A12 | TB-11 | Append-only Audit module with a trigger refusing UPDATE for every role, owner included | Claude | M9 | done | `AuditLogTests.The_application_role_can_only_add_and_read_audit_records_and_no_role_can_change_one`, `AuditLogTests.Each_platform_and_member_change_writes_one_audit_event_naming_its_actor_by_reference_only` |
| TB-A13 | TB-11 | No hash-chained or WORM audit store | user | M13 | accepted | Privileges plus an update trigger are enough for a portfolio system with no external auditors (SPEC-0009 decision 4); revisit if tamper evidence is required |
| TB-A14 | TB-12 | App role is not owner, superuser or `BYPASSRLS` and cannot run DDL | Claude | M2 | done | `RolePrivilegeTests` |
| TB-A15 | TB-13 | No tokens or bodies in logs; denial metrics with bounded labels only | Claude | M2 | done | `AuthDenialTelemetryTests`, `TenantLifecycleTests.Production_logs_hold_no_sql_and_no_tenant_names` |
| TB-A16 | TB-14 | Secret scan over git history and working tree, including realm files | Claude | M9 | done | `SecurityScanTests.Every_scanner_image_is_pinned_by_digest`, `SecurityScanTests.Every_exception_names_its_tool_id_reason_and_owner_and_has_not_expired` |
| TB-A17 | TB-15, TB-18 | Fresh scope per message or timer; service context entered once, with no membership or permission | Claude | M5 | done | `ServiceTenantContextTests`, `NotificationConsumerTests.Each_message_is_recorded_in_its_own_tenant_only_never_in_the_previous_ones`, `OrderPaymentSagaTests.A_message_acts_in_its_own_tenant_only_and_an_unknown_tenant_or_order_is_dead_lettered`, `PaymentReconciliationTests.A_timer_runs_in_its_own_tenant_only` |
| TB-A18 | TB-16 | Forced RLS on every tenant-owned table; tenant set per transaction | Claude | M3 | done | `RowLevelSecurityTests.Row_level_security_is_forced_on_every_tenant_owned_table`, `RowLevelSecurityTests.Without_a_tenant_no_row_is_visible_and_no_row_can_be_written`, `RowLevelSecurityTests.A_pooled_connection_does_not_keep_the_previous_tenant` |
| TB-A19 | TB-17 | Renewals of a suspended tenant wait and charge nothing | Claude | M7 | done | `SubscriptionRenewalTests.Renewals_of_a_suspended_tenant_wait_and_charge_nothing` |
| TB-A20 | TB-17 | Imports and exports end on tenant suspension or loss of the requester's rights | Claude | M9 | done | `ImportJobTests.An_import_stops_before_its_next_batch_once_its_requester_is_demoted_and_keeps_the_batches_applied`, `ExportJobTests.An_export_ends_unwritten_when_its_requester_is_demoted_or_removed_or_its_tenant_suspended` |
| TB-A21 | TB-04 | Rate limiting on tenant and platform routes | Claude | M11 | planned | M11 workload model |
| TB-A22 | TB-19 | Split `platform-admin` and `support-operator`; time-limited, audited, read-only support grants | Claude | M9 | done | `SupportAccessTests.Every_platform_route_answers_each_kind_of_caller_as_its_role_says`, `SupportAccessTests.A_support_operator_opens_one_audited_grant_with_a_ticket_and_a_bounded_duration` |
| TB-A23 | TB-20 | Platform authority stays an IdP role claim; audit stores a keyed operator reference | user | M12 | accepted | No operator registry in M9 (SPEC-0009 decision 9); the local IdP has no production administrators; reviewed with the production IdP |
| TB-A24 | TB-21 | Pseudonymise issuer and subject of memberships removed more than 30 days ago | Claude | M9 | done | `RetentionTests.A_membership_removed_thirty_days_ago_loses_its_identity_keeps_its_row_and_the_person_can_return` |
| TB-A25 | TB-21 | On-request erasure and tenant offboarding | user | M13 | accepted | Out of scope for M9 (SPEC-0009 decision 6); time-based pseudonymisation covers removed members meanwhile |
| TB-A26 | All | Manual security review of each boundary slice while there is no remote | user | M9 | accepted | No remote exists for automated review; tenant isolation review and the matrix run on every slice; reviewed when the owner adds a remote |

## Security tests and monitoring

- Tests: the isolation matrix, route-table test, RLS tests and role-privilege tests above fail on most boundary
  violations; each denial criterion was proven to fail without its guard (slice skill). A new tenant route is picked
  up by the matrix automatically. SPEC-0009 AC 15 adds the same walk for every `/platform` route.
- Monitoring: `tenantflow.auth.denials` counts denials with `scheme` and `reason` only, never tenant or subject.
  Alerting on spikes is M10.

## Review date and change triggers

Reviewed at M2, then at M9 start (2026-10-22). Next review: the M9 gate (after the role split and support grants),
then M11 (rate limiting) and M12 (production identity provider, operator registry). Also review when a new entry
point, a new actor, a cache of authorization data, a role that bypasses RLS, or a cross-tenant feature is proposed.
