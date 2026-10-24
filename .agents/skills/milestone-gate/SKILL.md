---
name: milestone-gate
description: Close a TenantFlow milestone. Full gate with fresh evidence, break-it proofs, reviews, specs and decisions checked, guide written, then stop for the user's approval.
---

Input: `M<n>`. Rules: `AGENTS.md`.

# Close a milestone

Fresh evidence only. Never call the gate met while a check failed or is BLOCKED.

1. List the milestone's exit criteria from `docs/roadmap.md`, one per row.
2. Run the full gate now, plus the CI coverage command. Record each command and result. A run from before
   the last edit does not count.
3. **Prove the critical guards.** For each safety criterion — cross-tenant denial, no-oversell, idempotency,
   dead-lettering — remove or invert the guard, run only the test it should break, confirm it fails at an
   assertion, then restore it by editing. Never `git checkout`, `restore` or `stash`. A test that still
   passes without its guard proves nothing: sharpen it.
4. Run a code review over the milestone's whole diff, and a security review when it touched authentication,
   tenants, payments or data.
5. Every spec for the milestone is `Implemented` with its Verification table complete; every open decision
   due by now is closed by an ADR, or deferred with the user's agreement.
6. Write `docs/milestones/M<n>-<kebab-name>.md` in the M0 guide's structure. Update the roadmap row, mark the
   criteria verified in `docs/progress/progress.json`, set `nextStep`, and run
   `dotnet run --project tools/TenantFlow.Progress -- --verify`. Data health must be clean.
7. Is every rule in `AGENTS.md` still true? Propose changes with evidence; apply none without approval.
8. Stop. Report the verdict, each criterion with its evidence, the gaps, and what the user must do. Do not
   start the next milestone.
