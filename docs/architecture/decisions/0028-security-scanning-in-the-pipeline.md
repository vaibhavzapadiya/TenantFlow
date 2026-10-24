# ADR-0028: Secret, dependency, container, and configuration scanning in the pipeline

- Status: Proposed
- Date: 2026-10-22
- Owners: TenantFlow tech lead (the user decides)
- Related decisions: ADR-0003, ADR-0004, ADR-0008, ADR-0019 (local Docker in WSL, Open Decision 21)
- Note (2026-10-23): ADR-0037 (Proposed) replaces the PowerShell script of decision 1 with the `tests/Security` project,
  which CI's `security` job runs; the scanners, pins, register and thresholds are unchanged.

## Context
Blueprint sections 13, 15 and 18 ask CI to scan dependencies, source, secrets, containers and IaC. The M0 guide planned
secret scanning for M9.

The state today:
- NuGetAudit runs on every restore (mode `all`, level `low`), and high or critical advisories fail the build.
- Dependabot proposes updates for NuGet, Actions, Compose images and the SDK every week.
- `dev.ps1 validate` checks that Compose images are pinned by digest, that ports are bound to loopback, and that the
  realms are synthetic.
- There is no secret scan, no image scan, and no configuration scan.
- The repository has no Dockerfile and no Terraform; both arrive in M12.
- CI exists but has never run (no remote; on hold by the owner).
- Docker runs inside WSL on the workstation.

## Decision (proposed)
1. **One script, two callers.** `./scripts/security-scan.ps1` runs every scan and is also reachable as
   `./scripts/verify.ps1 -Security`. CI's new `security` job calls the same script. As with `verify.ps1`, local and CI
   results mean the same thing.
2. **Tools run as container images pinned by digest**, through the same Docker path as `dev.ps1` (`wsl docker` locally).
   This adds no NuGet packages, no tool installs, and no new runtime component.
   - **Secrets: gitleaks.** Scans the full git history and the working tree. The allow-list holds only the documented
     placeholders (`Password=placeholder`, synthetic `dev-*` users).
   - **Dependencies: NuGetAudit plus `dotnet list package --vulnerable --include-transitive`.** The scan fails on
     moderate and above (the build already fails on high). Dependabot stays.
   - **Images: Trivy image scan of every Compose image.** Fails on HIGH or CRITICAL vulnerabilities that have a fix
     (`--ignore-unfixed`). Our own images join in M12.
   - **Configuration: Trivy config scan** of `deploy/compose` and `.github/workflows`. Fails on HIGH misconfigurations.
     Terraform joins in M12.
3. **Exceptions are explicit and expire.** `.config/security-exceptions.yaml` lists each ignored finding with its id,
   reason, owner and expiry date. The script fails when an exception has expired or names an unknown tool.
4. **The CI job has least privilege.** `permissions: contents: read`, actions pinned by SHA, `persist-credentials:
   false`, no secrets, `fetch-depth: 0` for the history scan.
5. **CI status (SPEC-0009 Q1, the owner decides).** Recommended: keep CI on hold. M9's "pipeline checks pass" then means
   the local script passes with fresh evidence and the `security` job is defined. The first real CI run is recorded when
   you add a remote.
6. **Static analysis.** The .NET analyzers already run with warnings as errors. CodeQL is added to CI only when CI runs,
   because it cannot run locally in a meaningful way.

## Options considered
- **gitleaks + Trivy as pinned images (chosen).** Two widely used tools cover the four scan kinds. Pinning keeps results
  reproducible, and nothing is installed on the host. Cost: image pulls, and online vulnerability databases (a scan
  fails when a feed is unreachable).
- **Trivy alone, including its secret scanner.** One tool, but it scans files, not git history. A secret committed and
  later removed would go unseen.
- **GitHub-native scanning** (secret scanning, Dependabot alerts, CodeQL). Strong and free on public repositories, but
  it needs the remote and cannot be run locally, so local evidence would be impossible while CI is on hold.
- **Report-only scans.** Never block a change, but findings pile up unread. Rejected: the gate says "checks pass".
- **Checkov or tfsec for IaC.** Better for Terraform, which does not exist yet. Revisit in M12.

## Consequences
- A full scan takes minutes and pulls images, so it runs per milestone and in CI, not per slice.
- Third-party images can fail the scan without any change on our side. The fix is a pinned upgrade or a dated
  exception, never a silent skip.
- The vulnerability feed is an external dependency of the gate.

## Validation
Break-it proofs, each followed by a revert:
- a planted fake secret in a scratch commit;
- a package pinned to a version with a known advisory;
- an expired exception;
- a Compose service made privileged.

Each must fail the script. A clean run of the script on the branch is the gate evidence.

## Revisit triggers
CI is switched on (add CodeQL and SARIF upload); our own images and Terraform appear (M12: SBOM, provenance, Checkov);
the scans become too slow for each pull request.
