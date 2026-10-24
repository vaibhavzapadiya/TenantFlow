# ADR-0009: Identity provider and the OIDC boundary

- Status: Accepted
- Date: 2026-10-19
- Owners: TenantFlow tech lead (decision approved by the user on 2026-10-19)
- Related decisions: ADR-0002, ADR-0008, ADR-0010
- Closes: Open Decision 3

## Context
TenantFlow needs authenticated callers before any tenant-scoped workflow exists (blueprint section 8).
The blueprint forbids building password storage, token signing, MFA, or account recovery, and requires
that issuer, audience, signature, expiry, and relevant claims are validated, and that an unvalidated JWT
payload is never treated as identity (sections 2.3 and 8). The production cloud is still open
(Open Decision 2, M12), and managed identity providers are tied to that choice. Tests must stay fast,
deterministic, and free of Docker (Testcontainers on this workstation is Open Decision 21).

## Decision
- **The application depends only on standard OIDC/OAuth 2.0.** The API validates bearer JWTs with
  ASP.NET Core JWT bearer authentication against a configured authority (issuer, metadata, and keys)
  and audience. Required validations: issuer, audience, signature against the authority's published
  keys, lifetime with a small bounded clock skew, and an allow-list of signing algorithms (no `none`,
  no symmetric algorithms in production). Missing or invalid authentication configuration stops the
  host at startup, like the connection string (ADR-0008).
- **No provider-specific code.** No provider SDK, admin API, or proprietary claim is used in modules.
  The user's stable identifier is the `sub` claim scoped by issuer (`iss` + `sub`); email is not identity.
- **Tests use an in-process test issuer.** A test-only signing key (generated per test run, never
  committed) mints tokens, and the test host is configured to trust that issuer. Tests can also mint
  invalid tokens (wrong issuer, audience, key, algorithm, expired) to prove denial.
- **Local development uses Keycloak in Docker Compose** as a real OIDC provider. It is an added
  stateful component: it runs with a realm imported from a committed file containing only synthetic
  users and local-only credentials created by the dev script (ADR-0008 rules apply), and the image tag
  is pinned exactly. `dev.ps1 smoke` obtains a token from it and calls the API.
- **The production provider is chosen with the cloud (M12).** Candidates are the cloud's managed
  identity service or a managed OIDC provider; the code does not change, only configuration.

Out of scope: user sign-up, MFA policy, enterprise SSO federation, and account recovery. These belong
to the provider and are revisited when a provider is chosen.

## Amendment 1: browser sign-in for the local API reference

- Status: Accepted (2026-10-23, approved by the user; SPEC-0014).
- Change: each local realm gains one browser client (`tenantflow-api-docs`, `tenantflow-platform-api-docs`): public,
  authorization code with PKCE S256 only, redirect URIs and web origin only `http://localhost:5080`, issuing exactly the
  claims of the realm's script client. The Development-only Scalar page signs in through them; in Development only, the
  OpenAPI document names the two schemes and their public client ids (Scalar pre-fills the client from the document
  alone), and the page's Content-Security-Policy allows `connect-src` to exactly the configured issuers' origins. The
  script clients stay password-grant only.
- Unchanged: token validation, policies, the production provider, and the reviewed contract (generated in Production).
- Consequences: a developer tries protected routes without copying tokens; the local realms carry one more client each,
  guarded by `RealmClientTests` and a smoke test.

## Options considered
- **Test issuer plus Keycloak locally (chosen).** Standards-only code, fast tests, and a real login
  flow locally that proves the boundary is not shaped around self-minted tokens. Cost: Keycloak uses
  roughly 0.5 to 1 GB of memory and adds a realm file and a container to maintain.
- **Test issuer only.** Lightest, but the boundary is only ever exercised with tokens we sign
  ourselves, so a mismatch with a real provider (key rotation, metadata, claim shapes) surfaces late.
- **Managed provider now (Entra ID, Auth0, Cognito).** Most realistic for production, but it needs an
  account and internet access through the corporate TLS proxy, ties the choice to a cloud before
  Open Decision 2, and tests still need a test issuer.
- **Self-hosted library inside the solution (OpenIddict, or Duende with its license).** Full control
  and portability, but the project would own token issuance and its security, which the blueprint
  discourages.

## Consequences
- Easier: swapping providers is configuration; tests cover invalid tokens deterministically.
- Harder: local startup gains a container; the realm file must stay free of real credentials and
  personal data; key rotation must be handled by fetching metadata, never by pinning a key.
- Follow-up: the trust-boundary schemes (ADR-0010) build on this validation.

## Validation
- API tests: a valid token is accepted; tokens with the wrong issuer, audience, signing key, algorithm,
  or an expired lifetime are rejected with 401; the host refuses to start without authority settings.
- `dev.ps1 smoke` obtains a Keycloak token and calls an authenticated endpoint successfully.

## Revisit triggers
The production cloud is chosen (M12); a customer needs enterprise SSO or federation; Keycloak's
local cost becomes a problem; a provider requirement cannot be met with standard OIDC.
