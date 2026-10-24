# SPEC-0014: Sign-in from the local API reference

- Status: Implemented (2026-10-23; approved the same day with the recommended answer to every open question)
- Milestone: none (local developer tooling, like SPEC-0013); can run alongside M12
- Owner: the user
- Related: blueprint section 9 (API design) and "Docker Compose local environment"; ADR-0009 (identity provider and
  OIDC boundary), ADR-0010 (trust boundaries between user types), ADR-0019 (OpenAPI contract); commit `1fc4043`
  (Development-only Scalar reference); `docs/operations/developer-productivity.md`

## Outcome
A developer opens `http://localhost:5080/scalar/v1`, clicks **Authorize**, signs in at the local Keycloak as a tenant user
or a platform operator, and every "Test Request" carries that token. No token is copied by hand. Nothing changes for a
non-Development host, for the API's authentication, or (with the recommended answer to Q2) for the API contract.

## Acceptance criteria

1. Given a Development host, when the reference page and the Development OpenAPI document are requested, then they
   offer two OAuth2 schemes, **Tenant user** (realm `tenantflow`) and **Platform operator** (realm `tenantflow-platform`),
   each an authorization code flow with PKCE (S256) whose authorization and token URLs are built from
   `Authentication:Tenant:Authority` and `Authentication:Platform:Authority`, and whose client id is the matching docs
   client of criterion 4. (Amended in S2: Scalar 2.17 pre-fills the client id only from the document, so the schemes and
   client ids are in the Development document; PKCE and the preferred scheme are page settings.)
2. Given a Development host, when the reference page is requested, then its Content-Security-Policy allows
   `connect-src` to exactly the origins of the two configured authorities, in addition to `'self'`, and no other origin;
   every other response keeps its policy unchanged. (Amended in S2: the sign-in navigates to Keycloak and back, and
   only the code exchange is a fetch, so `form-action` stays `'none'`.)
3. Given a Production host with `ApiDocumentation:Enabled=true`, when `/scalar/v1` is requested, then it is not mapped
   (unchanged), and the OpenAPI document (the reviewed contract) holds no client id, authority URL or security scheme.
4. Given each local realm file, then it holds one docs client (`tenantflow-api-docs`, `tenantflow-platform-api-docs`) that
   is public, has the standard flow enabled, requires PKCE S256, has the direct-access (password), implicit and
   service-account grants disabled, allows redirect URIs only under `http://localhost:<API port>/scalar/` and web
   origins only `http://localhost:<API port>`, and issues the same audience and roles claim as the existing dev client
   of that realm. A realm file that breaks any of these fails the build (architecture test).
5. Given the existing dev clients `tenantflow-dev-cli` and `tenantflow-platform-dev-cli`, then they are unchanged: still
   password grant only, still no redirect URI or web origin.
6. Given the live local stack, when a browser token request for a docs client arrives from origin
   `http://localhost:5080`, then Keycloak processes it and allows that origin; from any other origin it answers 403
   without CORS headers (smoke test). Keycloak answers every preflight, so the token request itself is the check.
7. Given a token obtained through the **Tenant user** scheme, when it calls a `/v1/platform/*` route, then the answer is
   401 or 403 exactly as for any tenant token today; a **Platform operator** token on `/v1/me` likewise (ADR-0010; the
   existing boundary tests already cover this, so the criterion is a regression guard, not new behaviour).
8. Given the developer signs in, then the token is held in page memory only and is gone after a reload (see Q3).

## Invariants
- The API's token validation, policies and tenant resolution do not change; a docs-client token is an ordinary token
  of its realm and gets no access a dev-cli token of the same user would not get.
- No secret is committed: both docs clients are public, PKCE replaces a client secret.
- The relaxed CSP stays on the reference endpoint only, and only gains the two Keycloak origins.
- Production: no reference page, no docs client (the realm files are local-only; ADR-0009's production realm setup is
  untouched), no new OpenAPI content.

## Contract
- **OpenAPI document:** the reviewed contract (generated in Production) is unchanged. In Development only, the document
  also names the two sign-in schemes with their public client ids (S2 finding: Scalar reads the client id only there). With the alternative, `components.securitySchemes` gains two `oauth2` schemes and each operation a
  `security` requirement: an additive contract change and a snapshot update.
- **Reference page (Development only):** Scalar options `AddAuthorizationCodeFlow` per scheme with `ClientId`,
  `AuthorizationUrl` (`<authority>/protocol/openid-connect/auth`), `TokenUrl` (`<authority>/protocol/openid-connect/token`),
  `Pkce = Sha256`, `SelectedScopes = ["openid"]`, `RedirectUri = http://localhost:<port>/scalar/v1`; preferred scheme
  **Tenant user**.
- **Authorization:** unchanged for every operation.

## Data
No database change. Realm files `deploy/compose/keycloak/*-realm.json` gain one client each; Keycloak re-imports them on
`docker compose up -d --wait --force-recreate keycloak` (documented).

## Failure modes
- **Keycloak down or not yet ready:** Authorize shows Keycloak's connection error; requests without a token still answer
  401 as today. No API impact.
- **Port changed in `deploy/compose/.env`:** authority URLs follow the hosts' settings; the realm's redirect URI and web
  origin are fixed to the default API port 5080 (see Q4).
- **Token expired (5 minutes):** calls answer 401; Scalar's Authorize signs in again (Keycloak's session usually makes
  it one click).
- **Wrong realm for the route:** 401/403 from the API, as criterion 7.

## Out of scope
- Any change to real (non-local) clients, the production realms, or token lifetimes.
- Swagger UI or another reference tool; Rider/VS HTTP client auth (the `.http` file already signs in).
- Refresh tokens in the reference page.

## Open questions
Answered 2026-10-23 by the user: every recommendation below is accepted (1a, 2a, 3 memory only, 4 fixed 5080, 5 ADR-0009 amendment, Proposed).

1. **Flow** *(blocking)*: (a) authorization code + PKCE with new docs clients, as written: the real-client pattern, no
   password typed into the page, two new realm clients. (b) password flow with the existing dev-cli clients, which only
   needs web origins and the CSP: smaller, but the clients' stated purpose is "smoke tests and drills only", and it
   models a grant real clients must not use. **Recommendation: (a).**
2. **Where the schemes live** *(blocking)*: (a) reference page configuration only, no contract change. (b) the OpenAPI
   document too, so generated clients and other tools see the auth requirement: arguably more correct, but an ADR-0019
   contract change touching every operation. **Recommendation: (a) now; (b) when a generated client needs it.**
3. **Token persistence:** keep the token in memory only (`WithPersistentAuthentication(false)`), or in the browser's
   localStorage across reloads. **Recommendation: memory only;** a token in localStorage outlives the tab.
4. **Non-default API port:** fix redirect URI and web origin to 5080 (simple; a changed port loses Authorize), or let
   the realm import read `${API_PORT}` like the password placeholder. **Recommendation: fixed 5080, documented.**
5. **ADR:** this narrows ADR-0009's local client setup and widens the reference page's CSP. It meets the ADR-0001 trigger
   only lightly; recommend recording it as an amendment to ADR-0009 (*Proposed*) rather than a new ADR.

## Slices

| Slice | Delivers | Criteria |
|-------|----------|----------|
| S1 | Realm docs clients + architecture test of their shape; dev-cli unchanged; smoke test of the CORS preflight. Proves the riskiest assumption: Keycloak accepts a browser token request from the reference page's origin | 4, 5, 6 |
| S2 | Scalar authorization code flows from the configured authorities, CSP widened to exactly those origins, memory-only token; API tests on the page configuration and headers; docs and ADR-0009 amendment | 1, 2, 3, 7, 8 |

## Verification
Filled in during implementation.

| Criterion | Test | Proven to fail without the change |
|-----------|------|------------------------------------|
| 1 | `ApiReferenceTests.In_development_the_reference_signs_in_tenant_users_and_platform_operators_with_pkce` | deferred to gate |
| 2 | `ApiReferenceTests.The_reference_policy_allows_connections_to_exactly_the_issuer_origins`; `In_development_only_the_reference_endpoints_carry_the_relaxed_content_security_policy` | deferred to gate |
| 3 | `ApiReferenceTests.The_published_document_carries_no_sign_in_configuration`; `OpenApiContractTests` (snapshot unchanged) | yes: without the Development check the scheme reached the published document |
| 4 | `RealmClientTests.The_api_reference_client_signs_in_with_pkce_only_on_the_local_api_address` (Architecture) | deferred to gate |
| 5 | `RealmClientTests.The_script_client_stays_password_grant_only_without_browser_access`; smoke test below (script client refused from a browser) | deferred to gate |
| 6 | `StackSmokeTests.Keycloak_accepts_the_reference_sign_in_only_from_the_api_origin` (Smoke, live stack) | yes: with `webOrigins: ["*"]` the other origin got 400 instead of 403 |
| 7 | Existing `PlatformBoundaryTests` and the smoke test `A_platform_operator_token_reaches_platform_and_not_me` (docs-client tokens carry the same claims, `RealmClientTests`) | not needed: no new behaviour |
| 8 | Persistence is not enabled in the page configuration (asserted in criterion 1's test); full sign-in confirmed by the user in a desktop browser (below) | n/a |

Browser check (2026-10-23, in-app browser): Authorize shows the client id, PKCE SHA-256 and scope `openid`; Keycloak
accepts the docs client, redirect URI and S256 challenge, and redirects back to `/scalar/v1` with a code. The in-app
browser opens Scalar's sign-in window in the same tab, so the code exchange there is not observed; it is to be confirmed
once in a desktop browser. Confirmed by the user the same day in a desktop browser: Authorize signs in and requests carry
the token.
