# ADR-0035: Production identity provider: hosted Keycloak with operator MFA

- Status: Proposed
- Date: 2026-10-22
- Owners: TenantFlow tech lead (Keycloak chosen by the user on 2026-10-22; acceptance pending)
- Related decisions: ADR-0009 (OIDC boundary; the production provider was left to M12), ADR-0010, ADR-0027; threat
  rows TB-A07, TB-A23, PS-A04, PS-A16, PS-A17, MW-A23; SPEC-0012

## Context
The API trusts two OIDC issuers: the tenant realm and the platform realm. They are kept separate by
`IssuerSeparation` (ADR-0010). Locally, Keycloak 26.8 serves both. M12 must choose the production provider, enforce MFA
for platform and support operators (PS-A17), and decide revocation (TB-A07) and an operator registry (PS-A04).

## Decision
1. **Keycloak, the same pinned image as Compose, as an ECS service per environment.** Its database is `keycloak` on
   the environment's RDS instance, under its own role.
2. **Two realms, as locally:** `tenantflow` (tenant users) and `tenantflow-platform` (operators). Issuers are
   `https://<identity host>/realms/<realm>`. The API needs no code change; only its authorities change.
3. **The admin console and master realm** answer only to `identity_admin_cidrs` at the load balancer, and to nobody
   when the list is empty. The bootstrap administrator is replaced by a named operator account after the first start.
4. **MFA (PS-A17):** the production `tenantflow-platform` realm (`deploy/keycloak/tenantflow-platform-realm.production.json`)
   enforces OTP with two separate guards, both checked against Keycloak 26.8 in S3e:
   - `CONFIGURE_TOTP` is a default required action, so no token is issued before an operator enrols an OTP.
   - The browser flow requires the OTP form on every sign-in.

   The only client uses the authorization code flow with PKCE; no client allows the password grant, which would skip
   the browser flow. The realm has no users, `sslRequired: all`, brute-force protection and a password policy.
5. **One Keycloak task.** More need cluster discovery (JGroups over JDBC_PING), which is not configured yet.
6. **Delivery (decided by the user, 2026-10-23):** `deploy/keycloak/Dockerfile` builds the Compose version ahead for
   PostgreSQL, with both production realm files and the RDS certificate authority inside.
   - It starts with `start --optimized --import-realm` on a read-only root, and its JDBC connection uses `verify-full`.
   - The release pipeline builds and attests the image, but the operator rolls it out (`keycloak_image_tag`), so the
     single task restarts only when its image changes.
   - The import only creates a missing realm. Later realm changes are made in the admin console until a
     config-as-code tool is chosen.
7. **Tenant realm (decided by the user, 2026-10-23):**
   - **Accounts:** no self-registration and no password reset, because there is no email provider. An operator creates
     each user with a temporary password, which must be changed at first sign-in.
   - **OTP:** optional. Once a user has enrolled one, it is asked at every sign-in.
   - **Client:** one public PKCE client with a loopback redirect, and no password grant.

## Options considered
- **Amazon Cognito.** Managed, cheap at this size, with built-in MFA. But the two-realm separation becomes two user
  pools, role claims become groups mapped by a pre-token Lambda, and local development would differ from production.
- **Auth0 or another SaaS IdP.** Least operational work, but a free tier limits operators and MFA policy, and it adds
  an external account and data processor.
- **Keycloak (chosen).** Local and production are identical and there are no code changes. In exchange, we patch,
  back up and monitor one more stateful service (it shares the RDS backups) at about $18 a month of Fargate.

## Consequences
- Keycloak is now a production dependency. Its availability bounds sign-in, but not API calls with unexpired tokens.
- Revocation before expiry (TB-A07, PS-A16): Keycloak can revoke sessions and refresh tokens. Access tokens stay valid
  up to their lifetime unless the API introspects them. The recommendation is to keep the one-hour cap and add
  introspection for platform routes only (SPEC-0012 open question 11).
- The production realm export, with OTP required, is part of the repository and is validated like the local realms.
- Keycloak upgrades follow the image pin and its scan, like the other Compose images.

## Validation
- After the first apply: signing in to `tenantflow-platform` without an OTP credential is refused, and the API accepts
  a token from each realm on its own route group only.
- The admin console returns 403 from outside `identity_admin_cidrs`.

## Revisit triggers
- Keycloak operations (patching, outages) cost more than a managed IdP would.
- Customers need enterprise SSO federation that a managed IdP gives for free.
- More than one Keycloak task is needed.
