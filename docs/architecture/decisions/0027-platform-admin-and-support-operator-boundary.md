# ADR-0027: Platform-administrator and support-operator boundary

- Status: Proposed
- Date: 2026-10-22
- Owners: TenantFlow tech lead (the user decides)
- Related decisions: ADR-0009, ADR-0010, ADR-0011, ADR-0026
- Completes: Open Decision 11 (platform and support detail due by M9)

## Context
ADR-0010 put platform operators on a separate scheme, issuer, audience and `/platform` route group. It left platform
operations and support access (reason, time limit, audit) to M9.

The facts today:
- There is one role, `platform-operator`. It can create, read, suspend and reactivate tenants, and add a first tenant
  administrator.
- No support path exists. Incidents need direct database access, which bypasses every control.
- Threat model 0001 records two residual risks: platform authority comes from an IdP claim, and support access is not
  designed.
- Blueprint section 3: support "investigates incidents and customer issues with least privilege, explicit
  reason/context, and audited access".

## Decision (proposed)
1. **Two roles, no implicit authority.** `platform-admin` replaces `platform-operator`. It may run the tenant
   lifecycle, add a first administrator, read the platform audit, and revoke support grants. `support-operator` may
   open and end its own grants and read the support views. Neither role includes the other. A person who needs both
   holds both, and each use is audited under its own actor kind. No deployed environment exists, so the rename needs no
   compatibility period.
2. **Support grants.** A new **Operations** module (blueprint section 6) owns `operations.support_grants`.
   - A support operator opens a grant for one tenant with a ticket reference (`^[A-Z]{2,10}-[0-9]{1,10}$`, never free
     text) and a duration of at most 240 minutes.
   - Opening, every use, ending, revocation and expiry are audited (ADR-0026), and the tenant sees them in its audit log.
   - There is at most one open grant per operator and tenant.
3. **Read-only support views.** They live under `/v1/platform/support/tenants/{tenantId}/...`: `members` (membership
   id, role, status and dates, with no issuer or subject) and `jobs` (import and export status, counts, failure code).
   - Each request checks the caller's own active grant for that tenant and commits the `support.tenant_accessed`
     audit intent, in one transaction, before any data is read.
   - The read runs in a tenant-scoped transaction for that tenant, with a support context that holds no tenant
     permissions. It calls read-only queries in the owning modules' `Contracts`.
   - No support route writes tenant data.
   - A missing, expired, ended or foreign grant is 404, the same answer as for an unknown tenant.
4. **No tenant consent in M9** (SPEC-0009 Q8). The reason, time limit, audit and the tenant's visibility of every
   access are the controls. Tenant-admin approval is a revisit trigger.
5. **Operator identity stays with the IdP in M9** (SPEC-0009 Q9). Authority is the `tenantflow_roles` claim on a
   platform token that lives at most one hour (ADR-0010). Audit stores a keyed reference, not the subject. A
   TenantFlow-side operator registry is deferred to M12, together with the production IdP and its MFA. The residual risk
   (an IdP administrator can mint operator authority) stays in threat model 0001.
6. **A platform route matrix.** A database test walks every `/platform` route as each kind of caller: anonymous, tenant
   token, platform token without a role, platform admin, support operator with no grant, and support operator with a
   grant. New platform routes are therefore covered automatically, as tenant routes are by the isolation matrix.

## Options considered
- **Grant with reason, time limit and audit (chosen).** It gives least privilege in time and scope, and the evidence is
  visible to the customer. It works for incidents when no tenant admin is reachable.
- **Tenant-admin approval before access.** Stronger consent, but it blocks support exactly when the tenant is locked out
  (for example a lost last administrator). It could be added later as a per-tenant setting.
- **Impersonation of a tenant user.** Familiar from consumer products, but it mixes actor identities, makes audit
  ambiguous, and gives write access. Rejected.
- **Break-glass database access with a runbook.** Needed only for disasters (M13). Not a support path.
- **A TenantFlow operator registry now.** It removes the IdP-claim risk, but needs bootstrap, management routes and its
  own audit. Better designed with the production IdP (M12).

## Consequences
- The Keycloak platform realm gains the two roles and a synthetic support user. Tests and the dev smoke check change.
- Each support view is a new read-only query in a module's `Contracts`, so support's reach is visible in code review.
- Operators lose the ability to do everything. Some incidents will need two people or two roles, by design.

## Validation
Platform route matrix, grant lifecycle tests (expiry with a fake `TimeProvider`, revocation, a foreign grant, a foreign
tenant), and "access record committed before the read" (a fault after the audit write still leaves the record). Each
guard has a break-it proof.

## Revisit triggers
A customer asks to approve support access; support needs to write data; the production IdP is chosen (operator registry
and MFA); support volume makes the 240-minute limit impractical.
