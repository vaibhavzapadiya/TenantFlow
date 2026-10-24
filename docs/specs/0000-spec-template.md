# SPEC-NNNN: <Feature name>

- Status: Draft | Approved | Implemented
- Milestone: M<n>
- Owner: <who approves this spec>
- Related: <blueprint sections, ADRs, other specs>

## Outcome
One paragraph: the business result and who gets it. No implementation detail.

## Acceptance criteria
Numbered and testable. Include at least one denial or failure path for every rule.

1. Given <state>, when <action>, then <observable result>.
2. Given a user of tenant A, when they request tenant B's <resource>, then the response is 404 and no data is returned.

## Invariants
What must never happen, whatever the inputs or timing (for example: stock never goes negative,
a payment is never captured twice, no cross-tenant read).

## Contract
API or event shape, status codes, error format (Problem Details), and the authorization rule for
each operation: which permission, which tenant, which resource-level check.

## Data
Tables, columns, constraints, indexes (tenant-aware where needed), and the migration approach
(expand-contract steps in this change versus later).

## Failure modes
Timeouts, duplicates, retries, concurrency, partial failure, dependency outage: the expected
behaviour for each.

## Out of scope
What this spec deliberately does not cover.

## Open questions
Unanswered questions for the owner. A spec is not approved while any of these is blocking.

## Slices
Implementation order. Each slice is small, ends with a passing gate, and names the criteria it covers.

| Slice | Delivers | Criteria |
|-------|----------|----------|
| S1 | | |

## Verification
Filled in during implementation. Every criterion maps to a test, or carries an explicit reason why it cannot.

| Criterion | Test | Proven to fail without the change |
|-----------|------|------------------------------------|
| 1 | | |
