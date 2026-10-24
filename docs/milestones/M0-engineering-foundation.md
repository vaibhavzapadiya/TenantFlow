# M0 — Engineering foundation

> **Historical guide.** The `scripts/` entry points this guide describes were removed (ADR-0037). For the current
> commands see [docs/operations/local-environment.md](../operations/local-environment.md) and `AGENTS.md`.

**Goal:** a repository where a clean checkout builds, checks itself, and tests itself the same way
on every machine and in CI, before any business code exists.

**Gate (blueprint section 20):** a clean checkout builds; formatting and analyzer checks and a
sample test pass.

## 1. What was built

```text
TenatFlow/                                 repository root ("tenantflow/" in the blueprint)
├── TenantFlow.sln
├── global.json                            SDK pin (10.0.402, ADR-0003)
├── Directory.Build.props                  build policy for every project
├── Directory.Packages.props               the only place package versions live
├── nuget.config                           nuget.org only, with package source mapping
├── .editorconfig                          style and analyzer severities, each one justified
├── .gitattributes / .gitignore            LF line endings; secrets and build output ignored
├── .config/dotnet-tools.json              local tool manifest (empty until a tool is needed)
├── .github/
│   ├── workflows/ci.yml                   pull-request quality gate
│   ├── dependabot.yml                     weekly updates with a 14-day cooldown
│   └── pull_request_template.md
├── scripts/verify.ps1, verify.sh          run the CI gate locally
├── src/Hosts/TenantFlow.Api/              empty host shell (endpoints arrive in M1+)
├── tests/Api/TenantFlow.Api.Tests/        host composition tests
└── docs/
    ├── roadmap.md                         prioritized plan, M0–M14
    ├── architecture/open-decisions.md     decision register
    ├── architecture/decisions/            ADR-0000 template, ADR-0001 to ADR-0005
    └── milestones/M0-engineering-foundation.md   this guide
```

Deliberately **not** in M0: Worker and Migrator hosts, health checks, logging, OpenTelemetry,
Docker Compose (all M1); any module or `BuildingBlocks` project (created when first needed);
architecture tests (M3, when there are boundaries to protect).

## 2. Architectural reasoning, file by file

### `global.json`: pin the SDK, allow patches
The SDK decides the compiler, analyzers, and MSBuild behaviour. Without a pin, two developers can
get different warnings from the same code. The pin is `10.0.402` with
`rollForward: latestPatch`, which fixes the feature band and still accepts security patches
(ADR-0003). An older SDK fails to resolve rather than building quietly.

### `Directory.Build.props`: policy in one place
MSBuild imports this file into every project below it, so no project can quietly opt out.
- `TreatWarningsAsErrors`: a warning nobody must fix is a warning nobody fixes.
- `AnalysisLevel` pinned to `10.0-recommended`: upgrading the SDK must not add surprise errors.
- `GenerateDocumentationFile` is on because a known Roslyn quirk only reports `IDE0005`
  (unused usings) during build when it is. `CS1591` is then disabled so XML docs stay optional.
- `RestorePackagesWithLockFile` together with CI's `--locked-mode` makes the dependency graph
  part of code review.
- `NuGetAuditMode=all` also audits **transitive** packages, which is where most vulnerable
  code hides.

### `Directory.Packages.props`: Central Package Management
One version per package across the whole monorepo. That prevents the "module A uses 8.1, module B
uses 8.3" diamond problem, and a security upgrade becomes a one-line diff.
`CentralPackageTransitivePinningEnabled` lets you override a vulnerable transitive dependency
without referencing it directly from every project.

### `nuget.config`: supply chain
`<clear/>` removes feeds inherited from the machine, and `packageSourceMapping` binds every package
id to nuget.org. If a private feed appears later, its prefixes are mapped explicitly. This closes the
dependency-confusion attack, where a public package shadows an internal one.

### `.editorconfig` and `.gitattributes`
Style is enforced by the build, not by reviewers' memory. LF line endings everywhere means the
format check gives the same answer on Windows and on the Linux CI runner. Every suppression has a
comment, which is the blueprint's "documented analyzer suppressions".

### `src/Hosts/TenantFlow.Api/Program.cs`: an empty host that is still strict
Two settings are made unconditional:
- `ValidateOnBuild`: the app refuses to start if any registration cannot be constructed. You find
  out at deploy time, not on the first request that needs the service.
- `ValidateScopes`: a scoped service, such as a `DbContext` or the tenant context in M2, can never
  be resolved from the root container. Resolving it there would silently turn it into a singleton
  shared across requests. In a multi-tenant system that is a **cross-tenant data leak**, not just a bug.

ASP.NET Core enables both only in Development by default. TenantFlow enables them everywhere.
`AddServerHeader = false` stops advertising the server implementation.

### `tests/Api/TenantFlow.Api.Tests/HostCompositionTests.cs`: a sample test that earns its place
The gate asks for "a sample test". Instead of `Assert.True(true)`, the three tests protect the
two settings above:

| Test | What it proves |
|------|----------------|
| `Host_starts_and_returns_404_for_unmapped_routes` | The host composes and serves requests in the Production environment |
| `Host_refuses_to_start_when_a_registration_cannot_be_constructed` | `ValidateOnBuild` is active outside Development |
| `Scoped_services_cannot_be_resolved_from_the_root_provider` | `ValidateScopes` is active outside Development |

The tests run in **Production** on purpose. In Development these checks are on by default, so
the tests would pass even if someone deleted the lines from `Program.cs`. That was verified: deleting
either line makes exactly one test fail.

The tests use `WebApplicationFactory<Program>`. .NET 10 generates the public `Program` declaration
automatically, so no host writes it by hand; analyzer `ASP0027` flags it as redundant if one does
(removed by SPEC-0015 S1).

### `.github/workflows/ci.yml`: the CI skeleton
- **Least privilege:** `permissions: contents: read`, and `persist-credentials: false` so later steps
  cannot reuse the checkout token.
- **Immutable actions:** each action is pinned to a commit SHA. A tag can be moved to malicious
  code; a SHA cannot.
- **Fail fast, cheapest first:** restore, then format, then build, then test.
- **Evidence:** TRX results and Cobertura coverage are uploaded even when tests fail.

### `scripts/verify.ps1` / `verify.sh`
"It passed on my machine" only means something if the local command is the CI command.
Run one of these before every push.

## 3. Security considerations in M0

| Concern | Control |
|---------|---------|
| Dependency confusion | `nuget.config` with `<clear/>` and package source mapping |
| Known-vulnerable packages | NuGet audit on transitive packages; high/critical fails the build |
| Unreviewed dependency changes | Committed lock files plus `--locked-mode` in CI |
| Compromised or broken new releases | 14-day cooldown for third-party packages |
| Hijacked GitHub Actions | Actions pinned to commit SHAs; Dependabot keeps them current |
| Over-privileged CI token | `contents: read`; credentials not persisted after checkout |
| Secrets committed by accident | `.gitignore` covers `.env*`, keys, certificates, and local settings. Secret scanning in CI arrives in M9; enable GitHub push protection when the repo is created |
| Scoped state leaking between requests | `ValidateScopes` in every environment, guarded by a test |
| Server fingerprinting | Kestrel `Server` header disabled |

Residual risk: no secret scanning, SAST, or container scanning in CI yet (planned for M9), and
branch protection must be configured on GitHub by hand.

## 4. Verification

### 4.1 Toolchain right now
The repository builds on the **.NET 10 SDK (10.0.402, runtime 10.0.13)**, the LTS ADR-0003 selected.
[SPEC-0015](../specs/0015-dotnet-10-toolchain-migration.md) moved it there on 2026-10-24 and superseded
the interim [ADR-0005](../architecture/decisions/0005-temporary-dotnet-9-toolchain.md).

From the repository root, `dotnet --version` should print `10.0.402` or a newer 10.0.4xx SDK.

Lock files are committed. Regenerate them only with the SDK named in `global.json`, and never
commit a test project lock file that does not list `xunit.v3` and `Microsoft.NET.Test.Sdk`. An
older Visual Studio once wrote incomplete lock files during setup, and they were deleted.

### 4.2 Run the gate

```bash
./scripts/verify.ps1
```

Expected: the SDK version prints, restore succeeds in locked mode, the format check reports no
changes, the build reports `0 Warning(s)` and `0 Error(s)`, and the tests report `Passed: 3`.

### 4.3 Prove the gate fails when it should
A gate that never fails proves nothing. Try each change, run the verify script, then revert:

| Change | Expected failure |
|--------|------------------|
| Add `using System.Text;` at the top of `Program.cs` | Build fails with `IDE0005` |
| Change the test file to a block-scoped namespace | Format check fails with `IDE0161` |
| Add trailing spaces to any line | Format check fails with `WHITESPACE` |
| Change a version in `Directory.Packages.props` without restoring | Locked restore fails with `NU1004` |
| Delete `options.ValidateScopes = true;` | `Scoped_services_cannot_be_resolved_from_the_root_provider` fails |
| Delete `options.ValidateOnBuild = true;` | `Host_refuses_to_start_when_a_registration_cannot_be_constructed` fails |

### 4.4 What was verified before handover
Verified on the real repository with SDK 9.0.204, on 2026-10-19 (the toolchain moved to 10.0.402 on 2026-10-24):

| Check | Result |
|-------|--------|
| `scripts/verify.ps1` (locked restore, format, Release build, tests) | Passed: 0 warnings, 0 errors, 3 of 3 tests |
| CI test command with `--collect "Code Coverage;Format=cobertura"` | Passed; TRX and Cobertura files produced |
| All six negative checks in 4.3 | Each failed with the expected diagnostic |
| Lock files | Test project pins 5 direct and 59 total packages; the API project has no packages yet |

Not yet verified: the GitHub Actions run itself, which needs the repository pushed to GitHub.

## 5. Trade-offs and assumptions

- **Repository root is the existing `TenatFlow` folder.** The blueprint calls the root
  `tenantflow/`. Only the folder name differs; the solution and projects use `TenantFlow`.
- **API host shell in M0.** The gate needs real code to build and test. The host has no endpoints,
  health checks, or telemetry; those are M1's deliverables.
- **xUnit 4.** It is the current major line, and adoption follows the 14-day cooldown. It dropped the
  implicit `using Xunit`, which the test project now declares.
- **VSTest runner.** xUnit 4 is built on Microsoft.Testing.Platform, but VSTest through `dotnet test`
  keeps the familiar TRX and coverage tooling. Switching runners is a small later change.
- **Warnings as errors locally.** Slightly slower spikes, no CI surprises (ADR-0004).
- **Temporary .NET 9 toolchain** (ADR-0005), chosen so work continued on the installed SDK.
  Superseded on 2026-10-24: SPEC-0015 moved the repository to the .NET 10 SDK.
- **Test SDK 17.14.1 instead of 18.x** while on .NET 9, because the 18.x coverage collector needs
  the .NET 10 runtime.
- **No commits were made.** The files are staged on a new `main` branch for your review.

## 6. Mentor exercises

1. Run every row of the table in 4.3 yourself. In one paragraph, explain why the composition
   tests must run in Production.
2. Add a fake dependency to `Directory.Packages.props` and reference it from the API project.
   Watch what the lock file and the CI restore do. Then remove it.
3. Read ADR-0003 and argue the opposite side: when would you float the SDK with `latestMajor`?
4. Create the GitHub repository, push, and configure branch protection on `main`: require the
   `Restore, format, build, test` check, require pull requests, and enable secret-scanning push protection.

## 7. Interview talking points

- "Why pin action SHAs instead of tags?" Tags are mutable. A compromised maintainer account can
  re-point `v7` to malicious code; a SHA is content-addressed.
- "Why turn on `ValidateScopes` in Production?" A scoped service captured by a singleton outlives
  its request. With per-tenant state, that turns a lifetime bug into a data-isolation incident.
- "How do you keep a monorepo's dependencies under control?" Central Package Management, committed
  lock files, locked restore in CI, transitive auditing, source mapping, and a cooldown policy.
- "What's your definition of a good sample test?" One that fails when the behaviour it names is
  removed. This was demonstrated here, not just asserted.

## 8. Exit checklist

- [x] Solution, SDK pin, central package management, lock-file policy
- [x] Analyzers, warnings as errors, `.editorconfig`, format check
- [x] ADR template plus ADR-0001 to ADR-0004; open-decisions register
- [x] CI skeleton and local verify scripts
- [x] Sample tests that guard real behaviour
- [x] Gate verified on the real repository with the installed .NET 9 SDK (ADR-0005; on .NET 10 since 2026-10-24)
- [ ] **You:** review and commit the staged files
- [ ] **You:** create the GitHub repository, push, and see the first CI run pass
- [ ] **You:** review the sequencing notes at the end of `docs/roadmap.md`
- [ ] **You, by 2026-11-10:** install the .NET 10 SDK and follow the ADR-0005 migration checklist
