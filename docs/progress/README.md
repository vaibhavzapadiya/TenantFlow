# Progress tracking

TenantFlow's progress dashboard answers: where are we, what is verified, what remains, what is blocked,
and what comes next. It is one self-contained HTML file generated from two sources:

| Source | What it holds | Maintained by |
|--------|---------------|---------------|
| [`progress.json`](progress.json) | Slices, milestone exit criteria, spec criteria, tasks, risks, components, quality gates | Updated at each slice and milestone close |
| The repository | Git state, spec / ADR / open-decision registers, test definitions, live quality run | Collected by `tools/TenantFlow.Progress` |

## Open the dashboard

```bash
dotnet run --project tools/TenantFlow.Progress -- --verify --open
```

`--verify` runs the real quality gate first (locked restore, Release build with formatting enforced, tests with
coverage, the Compose rules among the architecture tests) and records the result for the current commit. Without
`--verify`, the
dashboard shows the last recorded run and marks it stale when HEAD has moved.

The output is `artifacts/progress/dashboard.html` (git-ignored). It works offline and needs no server.

## Update after a slice

Edit the slice's entry in `progress.json`:

```json
{ "id": "M2-S1", "title": "...", "status": "verified", "commit": "abc1234", "criteria": [1, 2],
  "verification": { "date": "YYYY-MM-DD", "ran": ["command -> result"], "proofs": ["break-it check that failed as expected"] } }
```

Then set `updated`, adjust `nextStep`, close or add tasks and risks, and regenerate. For a spec-path
slice, mark its criteria `"verified": true` in the `specs` section, with `how` set to `automated`,
`observed`, or `manual`.

## Status rules

- Slice status: `planned` → `in_progress` → `implemented` (code committed) → `tested` (automated tests
  pass) → `verified` (criteria proven, including break-it checks). `blocked` can occur at any point.
- Only **verified** counts as progress. Milestone status is computed, never stored:
  **complete** (every exit criterion and slice verified), **gate met** (exit criteria verified, some slice
  not), **in progress**, or **planned**.
- Progress numbers are counts, not effort estimates. Future slices come from the roadmap and are
  provisional until each milestone's kickoff refines them.
- Nothing is marked passed unless it ran and passed. Recorded results show their date; live results
  show their commit.

## Data health

The **Data health** tab flags drift between `progress.json` and the repository: a recorded commit that
does not exist, a verified slice without evidence, a spec index status that disagrees with its
criteria, an overdue open decision, a stale live run, or a milestone without slices. Fix errors before
trusting the numbers.
