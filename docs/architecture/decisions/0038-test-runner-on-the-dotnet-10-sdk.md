# ADR-0038: Tests keep running through VSTest on the .NET 10 SDK

- Status: Proposed
- Date: 2026-10-24
- Owners: TenantFlow tech lead
- Related decisions: [ADR-0003](0003-dotnet-lts-and-dependency-policy.md) (testing stack),
  [ADR-0004](0004-build-quality-gates.md) (the gate), [ADR-0005](0005-temporary-dotnet-9-toolchain.md),
  [SPEC-0015](../../specs/0015-dotnet-10-toolchain-migration.md)

## Context
ADR-0003 chose the xUnit v3 package line with `xunit.runner.visualstudio`, "run through `dotnet test` (VSTest)".
`xunit.v3` is also a Microsoft.Testing.Platform (MTP) application, and on the .NET 10 SDK the MSBuild targets
that ship with MTP refuse the VSTest entry point outright:

> Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later.

Every test project failed this way during the SPEC-0015 migration, so `dotnet test TenantFlow.sln -c Release` —
the gate in `AGENTS.md` and the CI Test step — did not run at all. A decision is needed before the migration can
close, because the gate is how every other rule in this repository is enforced.

Note that the repository is already split: the database, smoke, drill, security and rule test projects are started
with `dotnet run --project tests/... -- -class "*X"`, which is MTP's own command line and is unaffected either way.
Only the solution gate goes through `dotnet test`.

## Decision
Set `IsTestingPlatformApplication` to `false` in `Directory.Build.props`, so the test projects present themselves as
ordinary VSTest projects and run through `xunit.runner.visualstudio`, exactly as ADR-0003 describes.

What this deliberately keeps unchanged:
- the gate command, `dotnet test TenantFlow.sln -c Release`;
- CI's `--logger trx` and `--collect "Code Coverage;Format=cobertura"`;
- `dotnet test --filter` in the `slice` procedure and in `.agents/rules/tests.md`.

Out of scope: how the MTP-native projects are run, and any change to the test framework itself.

## Options considered

- **Opt out of MTP (chosen).** One property, no command changes anywhere, and ADR-0003's testing stack stands as
  written. Cost: it keeps the repository on an entry point Microsoft has begun removing, so this is a deferral,
  not a destination. Risk: a future SDK or xUnit release may drop the opt-out, and the same decision returns with
  less notice.
- **Adopt the new MTP-based `dotnet test`.** The supported direction, and it matches how the database and smoke
  projects already run. Cost: every test invocation changes at once — `--filter` becomes a filter expression,
  `--logger trx` becomes `--report-trx`, `--collect` becomes `--coverage` — which touches CI, `AGENTS.md`, the
  `slice` and `milestone-gate` procedures and the test conventions. It also could not be reached from this SDK:
  neither a `dotnet.config` runner section nor `TestingPlatformDotnetTestSupport` nor
  `UseMSBuildTestInfrastructure` made `dotnet test` take the MTP path during the migration, so adopting it needs
  its own spike rather than a line in a props file.
- **Drop `xunit.runner.visualstudio` and run every project with `dotnet run`.** Uniform with the MTP projects, but
  it gives up a single solution-wide test command and the coverage collector CI depends on.

## Consequences
- The gate, CI and the per-slice commands keep working unchanged through the .NET 10 migration.
- The repository carries a known deferral. The second option above is the intended destination and should be
  scheduled on its own, with its own spec, rather than bundled into a toolchain migration.
- Coverage still comes from the VSTest collector, so ADR-0004's coverage evidence is unaffected.

## Validation
`dotnet build TenantFlow.sln -c Release` and the solution's unit, API and architecture tests run on SDK 10.0.402
with this property set, and `dotnet format --verify-no-changes` is clean. Without it, every test project fails to
run with the error quoted above.

## Revisit triggers
An SDK or `xunit.v3` release that ignores or removes `IsTestingPlatformApplication`; MTP's `dotnet test` becoming
reachable with the flags the gate needs; or any other change that makes the second option cheap.
