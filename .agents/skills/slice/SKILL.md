---
name: slice
description: Implement one TenantFlow change that touches tenant isolation, authorization, money, a schema, a public contract or messaging. Test first, smallest change, build, commit. Smaller or lower-risk work does not need this skill.
---

Input: `<spec NNNN S<n> | one-line acceptance criterion>`. Rules: `AGENTS.md`.

# Implement one change

1. Open every file you will change, plus one sibling that shows the local pattern. Match it.
2. Write the test first and watch it fail for the right reason. Tenant-scoped behaviour gets a cross-tenant
   denial test.
3. Make the smallest change that passes. No drive-by refactors, no package the spec did not name.
4. Iterate with `dotnet test <project> --filter <test>`. After the last edit, once:
   `dotnet build TenantFlow.sln -c Release` plus the test projects you touched. Red stops the change.
5. Commit, with one line saying what you ran and the counts. Never push.

On the spec path, fill the spec's Verification rows. Break-it proofs, the progress tracker and the reviews
belong to `/milestone-gate`.

## Tenant isolation checklist

For anything touching tenant context, memberships, roles or tenant-owned data. Answer with file:line.

1. Tenant context comes from the authenticated identity plus an active membership — never from a route,
   header or body alone.
2. No context, no membership, or a suspended tenant denies. No fallback to a default tenant or to all.
3. Every read and write is scoped by tenant id in the query, or by row-level security. Check
   `IgnoreQueryFilters`, raw SQL, `Find` by key alone, bulk updates.
4. Lookups combine id with tenant, so another tenant's id is not-found.
5. Denials do not reveal that another tenant's row exists.
6. A named policy or permission guards it, not an inline role string. No member can raise their own role.
7. Jobs, consumers, caches and logs carry the tenant; cache keys include it; no tenant or personal data in
   logs or traces.

## Stop and ask

- The change grows past its plan → re-slice with the user.
- A test fails for a reason you did not expect → report it, never weaken or skip it.
- Docker or a service is missing → name the BLOCKED criteria. Never substitute a mock.
- Never hand-edit an applied migration.
