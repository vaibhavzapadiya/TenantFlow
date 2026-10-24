# ADR-0015: API versioning with a URL segment

- Status: Accepted (2026-10-21 by the user)
- Date: 2026-10-19
- Owners: TenantFlow tech lead (options chosen by the user on 2026-10-19)
- Related decisions: ADR-0003, ADR-0005, SPEC-0002 (amended)
- Partly closes: the versioning part of the M3 "API conventions and compatibility policy" item

## Context
The API is the product's public contract. The blueprint requires that contracts are versioned
deliberately and that a compatibility policy and automated contract checks exist (section 9). Until
now the routes were unversioned (`/me`, `/platform/tenants`). More tenant routes arrive in S5 to S7, so
the cheapest moment to introduce a version is before they exist. The full compatibility policy and the
OpenAPI checks remain M3 work.

## Decision
- **The version is the first URL segment**: `/v1/me`, `/v1/platform/tenants`, `/v1/tenants/{id}/...`.
  Matching is case-insensitive like every other route.
- **Every business endpoint is mapped on a version group** (`ApiVersions.MapV1()`). The health probes
  `/health/live` and `/health/ready` are unversioned because platforms call them without knowing the
  API's version.
- **A path that is not a versioned endpoint is denied by the fallback policy** (SPEC-0002 criterion 8):
  `/me` and `/v2/me` return 401 to everyone, signed in or not, revealing nothing (the fallback policy never reads a token; see ADR-0010).
- **A breaking change ships as a new group** (`/v2`) beside the old one; non-breaking changes
  (new optional fields, new endpoints) stay in the current version. What counts as breaking is written
  in the M3 compatibility policy.
- **Resource links carry the version.** `Location` headers are generated from named routes, never
  assembled from strings, so they cannot lose the prefix.
- **No versioning library.** The mechanism is one constants class and one `MapGroup`. A test enumerates
  the route table and fails if any endpoint other than health sits outside `/v1`.

Out of scope: deprecation and sunset headers, per-version rate limits, and OpenAPI documents per
version (M3).

## Options considered
- **URL segment, hand-rolled (chosen).** Visible in logs, traces, caches, curl, and OpenAPI; trivially
  routable and rate-limitable per version. No dependency and nothing to migrate at the .NET 10 move.
  Costs: clients change the URL to adopt a new version, and we write sunset/deprecation headers
  ourselves if they are ever needed.
- **URL segment with Asp.Versioning.** Adds version sets, deprecation reporting, and OpenAPI per
  version. Rejected for now (`CLAUDE.md`, rule 8): the 10.x line targets .NET 10 only, so it would be
  a net9.0 pin (8.1.1) followed by a major upgrade at the .NET 10 move, for features one version does not need.
- **Header or query-string versioning.** Keeps URLs stable but hides the version from logs and
  browsers, and clients that omit it silently receive a default version.

## Consequences
- Easier: reading logs and traces by version; adding `/v2` without touching `/v1`.
- Harder: every route test and client uses the prefixed paths; documentation must say so.
- If deprecation signalling becomes a requirement, adopt Asp.Versioning at the .NET 10 move rather than
  re-implementing it.

## Validation
- `ApiVersioningTests`: every non-health endpoint is under `/v1`; unversioned and `/v2` paths are not
  served; `/v1/me` works.
- `TenantLifecycleTests`: the `Location` header of a created tenant is `/v1/platform/tenants/{id}`.
- `dev.ps1 smoke` calls the `/v1` routes with real Keycloak tokens.

## Revisit triggers
A second API version is needed; deprecation or sunset signalling is required; an API gateway takes
over version routing; the .NET 10 move makes the library's cost disappear.
