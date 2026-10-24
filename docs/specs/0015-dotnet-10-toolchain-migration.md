# SPEC-0015: .NET 10 toolchain migration

- Status: Approved (by the user, 2026-10-24; every recommendation in the open questions accepted as written)
- Milestone: none (cross-cutting toolchain; blocks the rest of M12)
- Owner: the user
- Related: [ADR-0003](../architecture/decisions/0003-dotnet-lts-and-dependency-policy.md) (target toolchain and
  dependency policy), [ADR-0005](../architecture/decisions/0005-temporary-dotnet-9-toolchain.md) (the interim .NET 9
  decision and its migration checklist), [ADR-0004](../architecture/decisions/0004-build-quality-gates.md) (analysis
  level and warnings as errors), ADR-0012 (database tests), ADR-0015, ADR-0036;
  [threat model 0007](../threat-models/0007-supply-chain-and-delivery.md) rows SC-12 and **SC-A13**;
  [SPEC-0012](0012-cloud-deployment.md) question 14

## Outcome
The repository builds, tests and ships on .NET 10 LTS instead of the interim .NET 9 toolchain, so the project runs on a
runtime that is supported past 2026-11-10 and receives security servicing. This closes ADR-0005, restores the pinned
toolchain ADR-0003 already decided, and closes threat row SC-A13. Nothing about the product's behaviour changes: the
same endpoints, events, schema and tests, on a newer framework.

Two dates make this urgent. .NET 9 leaves support on **2026-11-10**, which is also ADR-0005's expiry. ADR-0005 further
states that "M12 (cloud deployment) must not start while this ADR is active", and M12 is already in progress, so the
repository is currently outside the condition its own decision record sets.

## Acceptance criteria

1. Given a clean checkout, when `dotnet --version` runs at the repository root, then it prints the SDK version pinned in
   `global.json`, and that version is a 10.0.4xx release.
2. Given only an SDK older than the pin installed, when any `dotnet` command runs at the repository root, then it fails
   with an SDK resolution error rather than silently building on the older SDK.
3. Given `Directory.Build.props`, when the solution builds, then every project targets `net10.0` and `AnalysisLevel` is
   `10.0-recommended`, as ADR-0004 and the M0 guide already describe.
4. Given the migrated repository, when `dotnet build TenantFlow.sln -c Release` runs, then it succeeds with warnings —
   formatting included — treated as errors, and `dotnet format TenantFlow.sln --verify-no-changes` reports no changes.
5. Given the migrated repository, when `dotnet test TenantFlow.sln -c Release` runs, then every unit, API and
   architecture test passes.
6. Given `Program.cs` in the API and Worker hosts, when the host tests build, then they resolve the generated entry
   point with no hand-written `public partial class Program;`. Given that declaration re-added, when the solution
   builds, then the build fails with ASP0027.
7. Given `Directory.Packages.props`, when restore runs, then no `Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`,
   `Microsoft.EntityFrameworkCore.*` or `Npgsql*` package resolves to a 9.x version, and `dotnet restore` reports no
   high or critical NuGet audit advisory.
8. Given the EF Core 10 provider against the existing schema, when the model-snapshot check runs, then it reports no
   pending model changes, and no applied migration file is edited (rule 7).
9. Given the local stack, when the database tests run
   (`dotnet run --project tests/Database/TenantFlow.Database.Tests -c Release`), then every test passes, including the
   row-level-security and cross-tenant denial tests.
10. Given the local stack, when the messaging tests and the rule tests
    (`docker compose --profile checks run --rm rule-tests`) run, then every test passes.
11. Given the API host on .NET 10, when the OpenAPI document is produced, then the route-table test passes and the
    document's routes, operation ids and schema names are unchanged from the .NET 9 output; any difference is an
    intentional, recorded decision (see open question 3).
12. Given `deploy/docker/Dockerfile`, when the three images build, then each uses a `10.0` base image pinned by digest
    and the published app starts on the .NET 10 runtime. Given the previous `9.0` runtime digest, when the API image is
    built and started, then it fails because the app targets `net10.0`.
13. Given the migrated repository, when the smoke tests and the drills run, then they pass.
14. Given the documentation, when the repository is searched for `net9.0`, `9.0.204`, `9.0-recommended` and
    "temporary .NET 9", then the only matches are the historical text of ADR-0005 and ADR-0003's record of the interim
    period; the README, the M0 guide, threat model 0007, `open-decisions.md`, the roadmap and `progress.json` all
    describe .NET 10.

## Invariants

- No product behaviour changes: the same HTTP contract, event contract, database schema and authorization rules before
  and after. A framework upgrade is never the reason a test's expectation changes; if a test must change, the cause is
  recorded as a deviation in the slice's evidence report.
- No applied migration is edited, and no new migration is added by this change (rule 7).
- No test is weakened, skipped or deleted to get a green build. A failure caused by .NET 10 is reported, not silenced.
- Tenant isolation is untouched: the cross-tenant denial tests and the isolation matrix pass unchanged.
- Every package version stays in `Directory.Packages.props` alone, and `packages.lock.json` files are **not**
  reintroduced (the user removed them on 2026-10-23, so ADR-0005's checklist step 3 is obsolete). ADR-0005's step 4
  names `scripts/verify.ps1`, which no longer exists either; the gate in `AGENTS.md` replaces it.

## Contract
No public API or event contract changes. The risk to the contract is incidental: `Microsoft.AspNetCore.OpenApi` 10
defaults to OpenAPI **3.1** where 9 emitted **3.0**, which changes the document's version field and some schema shapes
without any route changing. Criterion 11 holds the generated document to parity, and open question 3 asks the owner
whether to accept 3.1 or pin the document version to 3.0.

Authorization is unchanged by this spec: no operation gains, loses or alters a permission check.

## Data
No schema change, so expand-contract does not apply. The data risk is the provider move:
`Npgsql.EntityFrameworkCore.PostgreSQL` and `Microsoft.EntityFrameworkCore.*` go from the 9 line to the 10 line
together, as `Directory.Packages.props` already instructs. The 10.0.3 provider requires
`Microsoft.EntityFrameworkCore.Relational` in `[10.0.4, 11.0.0)` and `Npgsql >= 10.0.3`, and targets `net10.0` only, so
the move cannot be partial. EF Core 10 can generate a different model snapshot or different SQL from the same model;
criterion 8 catches a snapshot drift and criterion 9 catches a behaviour drift against a real PostgreSQL.

## Failure modes

| Risk | Expected behaviour |
|---|---|
| `AnalysisLevel 10.0-recommended` turns new analyzer rules on, and warnings are errors | The build fails loudly. Each new diagnostic is fixed in code, or suppressed in `.editorconfig` with a one-line reason. Not downgraded wholesale. |
| EF Core 10 behaviour change (snapshot, SQL, or a removed API) | Caught by criteria 8 and 9. A genuine breaking change is recorded as a deviation and raised, not worked around silently. |
| OpenAPI 3.1 default changes the generated document | Caught by criterion 11; resolved by open question 3. |
| `Microsoft.NET.Test.Sdk` 18.x coverage collector | ADR-0005 says 18.x needs the .NET 10 runtime, which now exists. CI's `--collect "Code Coverage;Format=cobertura"` must still produce a Cobertura file. |
| `dotnet format` on SDK 10 formats differently from SDK 9 | A formatting-only diff is applied in slice S1 and called out, so it is not mistaken for a behaviour change. |
| Base image digests are resolved by hand | Each new digest is recorded in the Dockerfile comment with the date, as the current pins are. |
| Third-party packages (Quartz, OpenTelemetry, RabbitMQ.Client, AWSSDK.S3, FluentValidation, Scalar) misbehave on net10.0 | They stay on their current versions. A package that genuinely cannot run on net10.0 is raised as a blocking deviation, not upgraded opportunistically. |

## Out of scope

- Migrating `TenantFlow.sln` to the `.slnx` format. ADR-0003 explicitly defers it.
- Opportunistic package upgrades that the move does not require: FluentValidation 12.2, Scalar 2.18, Quartz 4.x,
  `xunit.v3` 4.0.2, Testcontainers 4.16. They keep their current pins and their cooldown comments.
- Anything about .NET 11 or the next LTS. ADR-0003's "skip STS" rule already covers it.
- The Aspire / local-tooling question (ADR-0036, ADR-0037, SPEC-0012 question 12), even though it was blocked on the
  .NET 10 SDK. Unblocking it is a consequence, not part of this change.
- Updating the deployed infrastructure in `deploy/terraform`. This spec rebuilds the images; M12 deploys them.

## Open questions

1. **Which SDK to pin.** ADR-0003 pins `10.0.402` with `rollForward: latestPatch`, but the workstation has
   **10.0.400** — and `latestPatch` only rolls *forward*, so pinning 10.0.402 would fail to resolve on this machine.
   The installed runtime is **10.0.11**, two servicing releases behind the current 10.0.13 (2026-10-13), which is the
   same "behind on published security fixes" risk ADR-0005 raised about 9.0.204.
   *Recommendation:* install the newest 10.0.4xx SDK first, then pin that exact version with `latestPatch`. It honours
   ADR-0003 as written and brings the runtime to the current patch instead of locking in a stale one.
   *Alternative:* pin `10.0.400` now and amend ADR-0003's number. Cheaper today, and it carries a known-stale runtime
   into M12's images.
2. **Which patch for the runtime-aligned packages.** ADR-0003 says `Microsoft.AspNetCore.*` and `Microsoft.Extensions.*`
   follow the SDK's runtime patch and are exempt from the 14-day cooldown. That is **10.0.11** for the currently
   installed runtime, and **10.0.13** if question 1 is answered by installing the newest SDK.
   *Recommendation:* whatever the chosen SDK's runtime is, so the two never drift. Third-party packages keep the
   cooldown: `Npgsql` and the provider go to **10.0.3** (published 2026-05-27 and 2026-07-10, both well past cooldown).
3. **OpenAPI 3.0 or 3.1.** ASP.NET Core 10 emits OpenAPI 3.1 by default.
   *Recommendation:* keep the 3.1 default if the route-table test and Scalar are unaffected, since it is the supported
   path forward; pin the document version to 3.0 only if something downstream breaks, and record that as an ADR,
   because it is a contract decision.
4. **`Microsoft.NET.Test.Sdk` version.** 18.11.0 was published 2026-10-13, inside the 14-day cooldown; 18.10.1
   (2026-09-15) is past it. *Recommendation:* **18.10.1**, and let Dependabot propose 18.11.0 once it clears cooldown.
5. **Analyzer wave.** Moving to `10.0-recommended` in one step may surface a batch of new errors.
   *Recommendation:* take it in one step, inside slice S1, so the gate is never green on a half-pinned analysis level.
   The size of the wave is unknown until the first build; if it is large, S1 is split and you are told before any
   suppression is written.
6. **Container images in this spec or in M12.** The Dockerfile comment says the tags move "with SC-A13", which is this
   work. *Recommendation:* here (slice S3). Leaving them on `9.0` would leave `deploy/` unbuildable the moment the
   framework moves, and M12 is in progress.
7. **How the ADRs are closed.** ADR-0005 step 5 says set itself to `Superseded` and date ADR-0003's status line.
   *Recommendation:* amend ADR-0003 in place (status line plus the real SDK number from question 1) and mark ADR-0005
   `Superseded` by it — no new ADR, because no new decision is being made, only an already-accepted one carried out.
   *Alternative, if question 1 or 3 does change a decision:* a new ADR-0038 recording it. Only you can accept either
   (ADR-0001).
8. **Sequencing against M12.** ADR-0005 forbids M12 while it is active, and M12 has started.
   *Recommendation:* finish this spec before the next M12 slice, and note the overlap in the M12 guide rather than
   rewriting history.

## Slices

| Slice | Delivers | Criteria |
|-------|----------|----------|
| S1 | **Framework and packages.** `global.json` pin, `net10.0`, `AnalysisLevel 10.0-recommended`, remove the two `public partial class Program;` declarations, move the Microsoft runtime-aligned, EF Core, Npgsql and Test SDK pins to their 10.x and 18.x lines, restore, and fix every compiler, analyzer and formatting failure that follows. Ends with `dotnet test TenantFlow.sln -c Release` green. This is the riskiest assumption — that the solution builds and its tests pass unchanged on .NET 10 — so it goes first. | 1–7 |
| S2 | **Data, messaging and contract parity.** EF Core 10 model-snapshot check, database and row-level-security tests, messaging tests, rule tests, route-table and OpenAPI document parity (and the question 3 decision). | 8–11 |
| S3 | **Container images.** The `deploy/docker/Dockerfile` SDK, ASP.NET and runtime base images to `10.0` by new digests; the three images build and start; smoke tests and drills pass. | 12, 13 |
| S4 | **Decisions and documentation.** ADR-0003 amended, ADR-0005 `Superseded`, threat row SC-A13 closed, and the README, M0 guide, threat model 0007, `open-decisions.md`, roadmap and `progress.json` updated. | 14 |

## Verification
**S2 to S4 status (2026-10-24).** At the user's instruction these were implemented without the skills and without
writing or running any test, so the gate did not run. S3 (container images) and S4 (decisions and documentation) are
code- and docs-complete; criteria 12, 13 and 14 are therefore *implemented but unverified*, and criteria 8, 9 and 10
are untouched. S2 contains no code at all: it is a verification slice, and its only non-test criterion, 11, was met in
S1. Before this spec can be called Implemented, the full gate, an image build, the smoke tests and the drills must run.

Filled in during implementation. Every criterion maps to a test, or carries an explicit reason why it cannot.

| Criterion | Test | Proven to fail without the change |
|-----------|------|------------------------------------|
| 1 | `ToolchainTests.The_sdk_is_pinned_to_a_patch_of_the_ten_zero_four_hundred_band` | yes |
| 2 | same test: it asserts `rollForward: latestPatch`, which is what refuses an older SDK | yes |
| 3 | `ToolchainTests.Every_project_targets_net_ten_with_its_matching_analysis_level`, and `…The_tests_themselves_run_on_the_net_ten_runtime` for the running assembly | yes |
| 4 | the gate itself: `dotnet build TenantFlow.sln -c Release` (0 warnings) and `dotnet format --verify-no-changes` | n/a, a command rather than a test |
| 5 | `dotnet test TenantFlow.sln -c Release` | n/a, a command rather than a test |
| 6 | `ToolchainTests.No_host_hand_declares_the_entry_point_that_net_ten_generates` | yes |
| 7 | `ToolchainTests.No_runtime_aligned_package_is_left_on_the_nine_line` | yes |
| 11 | `OpenApiContractTests.The_generated_document_matches_the_committed_snapshot` (brought forward from S2: it is in the solution gate) | yes |

**S1 deviations, recorded as the Invariants section requires.**

1. *Test runner.* The .NET 10 SDK refuses to run a Microsoft.Testing.Platform application through the VSTest target,
   so no test ran at all. `IsTestingPlatformApplication=false` in `Directory.Build.props` keeps the gate command, CI's
   trx and Cobertura flags and the filter option working as ADR-0003 specifies. Drafted as
   [ADR-0038](../architecture/decisions/0038-test-runner-on-the-dotnet-10-sdk.md) (`Proposed`).
2. *OpenAPI schema-name collision.* .NET 10 names a nested type by its own name alone, so `MembersResponse.Item` and
   `MyMembershipsResponse.Item` collapsed onto one schema and `MembersResponse.items[]` was published with the wrong
   fields. Pinning the document to OpenAPI 3.0 does not help: the collision is in the generator, not the dialect. The
   two records were renamed to `Member` and `Membership`, which removes the collision at the source. The contract's
   only schema-name change is `Item`/`Item2` to `Member`/`Membership`; paths (40), operation ids (7) and
   operation/response pairs (230) are identical. Approved by the user, 2026-10-24.
3. *`BackgroundService` start semantics.* .NET 10 no longer runs the synchronous part of `ExecuteAsync` before
   `StartAsync` returns, confirmed with a standalone repro. `PlatformHeartbeatService` now creates its `PeriodicTimer`
   with the service, restoring the invariant its own comment documented, and
   `Stops_within_five_seconds_without_an_unhandled_exception` accepts a cancelled as well as a completed execute task
   while still rejecting a faulted one. Approved by the user, 2026-10-24.
4. *OpenAPI XML documentation generator.* ASP.NET Core 10 caches XML doc comments in a generated type, which tripped
   `OutboxAccessTests` because a comment names a table. The scan now skips `Microsoft.AspNetCore.OpenApi.Generated`;
   documentation text is not SQL against a table.
