# Contributing to TenantFlow

Thank you for your interest in TenantFlow. You can help with bug reports, documentation fixes, tests, new features,
or by discussing a design decision. This guide explains how.

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md). To report a security problem, follow
[SECURITY.md](SECURITY.md) and do **not** open a public issue.

## Ways to contribute

| You want to… | Do this |
|---|---|
| Report a bug | Open a [bug report](../../../issues/new?template=bug_report.yml) with the steps to reproduce it |
| Suggest a feature or a design change | Open a [feature request](../../../issues/new?template=feature_request.yml) first, so we can agree on the approach before you write code |
| Ask a question | Use [GitHub Discussions](../../../discussions) if they are enabled, otherwise open an issue |
| Fix a typo or a doc | Open a pull request directly |
| Work on an open issue | Comment on the issue so it is not done twice, then open a pull request |

Issues labelled `good first issue` are a good place to start.

## Set up your machine

Follow [How to run](../README.md#how-to-run) in the README. In short: install the .NET 10 SDK and Docker,
then run from the repository root:

```bash
docker compose up -d
```

Then start the API and the Worker from your IDE (in Visual Studio, the **API + Worker** profile and F5). For more
detail and troubleshooting, see [docs/operations/local-environment.md](../docs/operations/local-environment.md).

## Before you write code

Read these once. They are the project's rules, and reviews check them:

- [AGENTS.md](../AGENTS.md): the workflow and the **eight non-negotiable rules**. These are tenant isolation, module
  boundaries, no remote calls inside a database transaction, money as a decimal amount plus a currency, async with
  `CancellationToken` and `TimeProvider`, no secrets or personal data, expand-contract migrations, and earned
  complexity.
- [docs/engineering-conventions.md](../docs/engineering-conventions.md): how code is organised, named and tested.
- [docs/roadmap.md](../docs/roadmap.md): what is being built now and what comes next.

### Small change or spec first?

Some changes need a short written spec in [`docs/specs/`](../docs/specs/README.md) that a maintainer approves **before**
you implement it. A spec is needed if the change touches any of these:

- tenant isolation, authorization, money or payments, or audit;
- a database schema change or migration;
- a public API or event contract;
- another module's boundary, the outbox, messaging or a saga;
- a state machine or a concurrency rule;
- anything that needs an architecture decision record (ADR), or anything bigger than one pull request.

For everything else, a pull request with a one-line description of what it achieves is enough. If you are not sure,
open an issue and ask.

Lasting architecture decisions are recorded as ADRs in [`docs/architecture/decisions/`](../docs/architecture/decisions/README.md),
using the [template](../docs/architecture/decisions/0000-adr-template.md). A new ADR starts as `Proposed`; a
maintainer accepts it.

## Making the change

1. **Fork** the repository and create a branch from `main`, for example `fix/order-cancel-404` or
   `feat/product-variants`.
2. **Write the test first.** Show that it fails without your change, then make it pass. Every tenant-scoped
   feature needs a test that another tenant is denied.
3. **Keep the change small.** One logical change per pull request. Don't refactor unrelated code in the same PR.
4. **Follow the existing patterns.** Look at a similar feature and match its structure, naming and error handling.
5. **Update the docs** that your change makes wrong, such as the README, a guide, a spec or the OpenAPI snapshot.

### Rules the build enforces

- Warnings are errors. If an analyzer rule must be suppressed, do it in `.editorconfig` with a one-line reason.
- Formatting is checked (`dotnet format`).
- Package versions live only in `Directory.Packages.props`. After changing one, run `dotnet restore TenantFlow.sln`.
- Never edit an applied migration; add a new one.
- Architecture tests fail if a module reaches into another module's internals.
- Never commit secrets, real personal data, or a `.env` file. Test data must be synthetic.

## Check your work

Run the same gate as CI before you open a pull request:

```bash
dotnet test TenantFlow.sln -c Release
```

If your change touches the database, messaging or anything in `tests/Database`, also run the database tests
(with `docker compose up -d --wait` running):

```bash
dotnet run --project tests/Database/TenantFlow.Database.Tests -c Release
```

## Commit messages

- Write a short summary line (under about 72 characters) in the imperative mood: "Add product variants", not
  "Added…".
- If it helps, add a blank line and a body that explains **why**, not only what.
- Reference the issue, spec or ADR when there is one, for example `SPEC-0004` or `Fixes #123`.

## Pull requests

- Fill in the pull request template. It asks how you verified the change and about its risks.
- Make sure CI is green. A maintainer reviews for correctness, tenant isolation, module boundaries and tests.
- Be ready for questions. Review comments are about the code, never about you.
- Maintainers squash or merge once the PR is approved.

### AI-assisted contributions

You may use AI coding tools. The project itself is built with Claude Code; see [AGENTS.md](../AGENTS.md) for the
instructions agents follow. You are responsible for every line you submit: you must understand it, have run the
tests, and be able to explain the change in review.

## Licensing of contributions

TenantFlow is licensed under the [MIT License](../LICENSE). By submitting a contribution you confirm that you have the
right to submit it, and that it is licensed under the same MIT License as the rest of the project. Do not submit
code copied from sources whose license is incompatible with MIT. If you include third-party material, add it to
[THIRD-PARTY-NOTICES.md](../docs/THIRD-PARTY-NOTICES.md).
