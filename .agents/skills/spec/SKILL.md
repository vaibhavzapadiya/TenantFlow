---
name: spec
description: Write a one-page TenantFlow spec for work that is large or hard to reverse, list the open questions, and stop for the user's approval. Not for bug fixes or small and medium changes.
---

Input: `<feature>`. Rules: `AGENTS.md`.

# Draft a spec

Only when `AGENTS.md` puts the work in the spec tier. Otherwise say so and write nothing. If it belongs to a
later milestone, name that milestone and stop.

The user owns the spec. Give choices with trade-offs and a recommendation; settle nothing yourself.

1. Copy `docs/specs/0000-spec-template.md` to `docs/specs/NNNN-<kebab-name>.md`, one above the highest
   number, status `Draft`.
2. **One page.** Longer usually means two features — propose the split.
3. Acceptance criteria as Given/When/Then with observable results, so each becomes a test. Every rule gets a
   denial or failure criterion; anything tenant-scoped gets a cross-tenant denial criterion.
4. Invent nothing. What the blueprint, an ADR or the user has not settled goes under Open questions with
   your recommendation. Mark the ones needing an ADR and offer to draft it `Proposed`.
5. Slices in order: each thin and ending green, the first proving the riskiest assumption.
6. Add a row to `docs/specs/README.md`.
7. Stop. Summarise, list the open questions, wait. Only the user sets `Approved`.

Link to the blueprint and ADRs instead of copying them. No code beyond contract shapes.
