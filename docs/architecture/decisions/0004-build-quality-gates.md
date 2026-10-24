# ADR-0004: Build quality gates (analyzers, formatting, and the CI skeleton)

- Status: Accepted
- Date: 2026-10-19
- Owners: TenantFlow tech lead
- Related decisions: ADR-0003

## Context
Quality rules that run only on some machines, or that only produce warnings, decay. The
blueprint asks for nullable reference types, analyzers, important warnings treated as errors,
formatting checks, documented suppressions, and a CI gate that a clean checkout passes.

## Decision

### Compiler and analyzers (`Directory.Build.props`)
- Nullable reference types and implicit usings are enabled in every project.
- `AnalysisLevel` is pinned to `10.0-recommended`, so an SDK upgrade cannot silently add errors.
- `TreatWarningsAsErrors` is on, locally and in CI. Exceptions: `NU1901` and `NU1902` (low and
  moderate vulnerability advisories) stay warnings, and `CS1591` (missing XML docs) is off.
- `EnforceCodeStyleInBuild` makes `.editorconfig` style rules part of the build.
  `GenerateDocumentationFile` is on so that `IDE0005` (unused usings) is reported during build.
- Every severity override lives in `.editorconfig`, with a comment explaining it.

### Formatting
- `.editorconfig` is the single source of style. `.gitattributes` normalizes line endings to LF
  so Windows and Linux agree.
- CI runs `dotnet format --verify-no-changes`.

### CI skeleton (`.github/workflows/ci.yml`)
- Order: locked restore, format check, Release build, tests, then upload TRX and coverage files.
- `permissions: contents: read`, `persist-credentials: false`, and a pinned runner image
  (`ubuntu-24.04`).
- Every third-party action is pinned to a full commit SHA, with the tag kept as a comment.
- Pull-request runs cancel superseded runs. Pushes to `main` always run to completion.
- `scripts/verify.ps1` and `scripts/verify.sh` run the same steps locally.

Added in later milestones: integration tests with Testcontainers, architecture tests, OpenAPI
and contract checks, secret, dependency, container and IaC scanning, SBOM generation, and image
builds.

## Options considered
- **Warnings as errors only in CI.** Keeps developers unblocked locally, but causes surprise CI
  failures and drift. Rejected.
- **`latest-all` analysis mode.** Maximum coverage, but very noisy for a domain-heavy codebase.
  Individual rules can be raised later. Rejected for now.
- **Third-party analyzer packs** such as StyleCop, Roslynator, or SonarAnalyzer. Useful, but each
  adds configuration and noise. Add one only when a specific gap is identified.

## Consequences
New code must be clean to compile, which occasionally slows down spikes. Spikes belong on
branches, and every suppression must be justified in review.

## Validation
Milestone 0 verification confirmed that each gate fails for the right reason:
- An unused using fails the build (`IDE0005`).
- A block-scoped namespace (`IDE0161`) or whitespace drift fails the format check.
- Lock-file drift fails locked restore (`NU1004`).
- Removing either DI validation flag from `Program.cs` fails a host composition test.

## Revisit triggers
Analyzer noise leads to blanket suppressions. Build time grows enough to slow pull requests.
A new rule category becomes relevant to a real defect class.
