# ADR-0003: .NET 10 LTS, pinned SDK, and dependency policy

- Status: Accepted; adopted in full on 2026-10-24 (SPEC-0015), which superseded the interim
  [ADR-0005](0005-temporary-dotnet-9-toolchain.md). Amended by [ADR-0038](0038-test-runner-on-the-dotnet-10-sdk.md) *(Proposed)*
- Date: 2026-10-19
- Owners: TenantFlow tech lead
- Related decisions: ADR-0004
- Closes: Open Decision 10 (".NET LTS is time-dependent")

## Context
The blueprint requires the currently supported .NET LTS release, a pinned SDK, locked package
versions, and a supported-version upgrade policy. Support status on the decision date, taken
from Microsoft's official release metadata:

| Channel | Type | Support status    | End of support |
|---------|------|-------------------|----------------|
| .NET 10 | LTS  | Active            | 2028-11-14     |
| .NET 9  | STS  | Maintenance       | 2026-11-10     |
| .NET 8  | LTS  | Maintenance       | 2026-11-10     |
| .NET 11 | STS  | Release candidate | not released   |

Builds must also be reproducible, and the dependency supply chain must be controlled.

## Decision

### Runtime and SDK
- Target `net10.0` everywhere through `Directory.Build.props`.
- Pin the SDK in `global.json` to `10.0.402` with `rollForward: latestPatch`. Security patches
  inside the 10.0.4xx feature band are picked up automatically. Moving to a new feature band is
  a deliberate, reviewed change.
- Skip STS releases. Move to the next LTS (.NET 12, expected November 2027) within six months of
  its release, and well before .NET 10 support ends.

### Packages
- Central Package Management (`Directory.Packages.props`) is the only place a version is set.
  Transitive pinning is enabled so a vulnerable transitive package can be overridden there.
- `packages.lock.json` files are committed. CI restores with `--locked-mode`, so an unreviewed
  dependency change fails the build. **Withdrawn 2026-10-23 by the user:** the lock files and locked
  restore were removed; versions come from `Directory.Packages.props` with transitive pinning.
- `nuget.config` clears machine-level feeds and maps every package to nuget.org, which prevents
  dependency-confusion substitution.
- NuGet audit covers direct and transitive packages. High and critical advisories fail the
  build. Low and moderate advisories stay visible as warnings.
- **Cooldown:** third-party package versions are adopted only after 14 days on the feed.
  Microsoft runtime-aligned servicing packages (`Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`)
  follow the SDK's runtime patch, so security fixes are not delayed.
- Dependabot proposes NuGet, GitHub Actions, and SDK updates weekly with the same cooldown.

### Testing stack
- xUnit v3 package line (`xunit.v3` 4.x) with `xunit.runner.visualstudio`, run through
  `dotnet test` (VSTest). xUnit 4 no longer adds `using Xunit` implicitly, so each test project
  declares it once.
- `Microsoft.AspNetCore.Mvc.Testing` for in-memory host tests. Testcontainers joins when there is
  real infrastructure to test (Milestone 1 or 3).

### Solution format
- Keep `TenantFlow.sln` as the blueprint specifies. The newer `.slnx` format is the .NET 10
  default and is a cheap later migration (`dotnet sln migrate`) once all tooling in use supports it.

## Options considered
- **.NET 9.** Already installed on the first workstation, but it is STS and leaves support in
  weeks. Rejected.
- **.NET 11.** Not released, and STS. Rejected.
- **Floating SDK (`latestMajor`).** Less maintenance, but builds change silently between
  machines and CI. Rejected.
- **Exact SDK pin (`disable`).** Fully reproducible, but every security patch needs a manual
  change. Rejected in favour of `latestPatch`.

## Consequences
- Every contributor needs a 10.0.4xx SDK at patch 402 or later.
- Adding or upgrading a package means editing one file and committing the regenerated lock files.
- The 14-day cooldown slightly delays new features in exchange for less exposure to
  compromised or broken releases.

## Validation
- CI resolves the SDK from `global.json` and fails on lock-file drift.
- Changing a version without updating the lock files fails `dotnet restore --locked-mode`. This
  was checked during Milestone 0 verification.

## Revisit triggers
.NET 12 LTS ships. A Microsoft servicing release breaks the build. The cooldown blocks an urgent
security fix, which is then handled as a documented exception in the pull request.
