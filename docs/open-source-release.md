# Open-source release checklist

This is everything needed to publish TenantFlow as an open-source project. The first two sections are already done
in the repository. The rest is for the owner, on GitHub, once the push hold (SPEC-0012 open question 4) is lifted.

## 1. Done in the repository

| Item | File |
|---|---|
| License: MIT, copyright 2026 Vaibhav Zapadiya | [LICENSE](../LICENSE) |
| Third-party material kept under its own license (Quartz.NET schema, Apache-2.0; Amazon RDS CA bundle) | [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) |
| How to contribute: setup, spec path, rules, tests, commits, pull requests, licensing of contributions | [CONTRIBUTING.md](../.github/CONTRIBUTING.md) |
| Code of conduct (Contributor Covenant 2.1, reports through GitHub) | [CODE_OF_CONDUCT.md](../.github/CODE_OF_CONDUCT.md) |
| Security policy (private vulnerability reporting, scope, response targets, safe harbour) | [SECURITY.md](../.github/SECURITY.md) |
| Issue forms (bug, feature or design) with blank issues off and a security link | [.github/ISSUE_TEMPLATE/](../.github/ISSUE_TEMPLATE/) |
| Pull request template with verification, risk and licensing checks | [.github/pull_request_template.md](../.github/pull_request_template.md) |
| Dependabot (NuGet, GitHub Actions, Docker Compose) | [.github/dependabot.yml](../.github/dependabot.yml) |
| README with overview, how to run, contributing and license | [README.md](../README.md) |

## 2. Pre-publication audit (2026-10-23)

| Check | Result |
|---|---|
| Secrets in the working tree and all history (`dotnet run --project tests/Security/TenantFlow.Security.Tests -c Release -- -method "*No_secret*"`, gitleaks) | Pass. Every finding is a reviewed synthetic placeholder in `.config/security-exceptions.yaml`: the local-only audit key in `.env.example`, and synthetic Stripe test-key placeholders in tests and in an old spec revision |
| `.env`, `settings.local.json`, Terraform state | Not tracked (`.gitignore`) |
| Terraform `*.tfvars` and `*.tfbackend` | Only region, sizes and CIDRs. The AWS account id, DNS zone, GitHub repository and admin CIDRs are deliberately not committed |
| Realm files and fixtures | Synthetic users only (`dev-*`, `@tenantflow.test`); `ComposeFileTests` enforce this |
| Vendored third-party code | Quartz.NET PostgreSQL schema only, attributed in `docs/THIRD-PARTY-NOTICES.md` |

## 3. Decide before the first public push

- [ ] **Commit author email.** Every commit so far has the author `vaibhavrappor <vaibhav@rappor.ai>`. Publishing the
  history publishes that address. Choose one:
  - accept it as it is;
  - or, **before the first push only**, rewrite the history to a GitHub `noreply` address with
    `git filter-repo --mailmap`. That changes every commit id, and the commit ids recorded in the docs and in
    `docs/progress/progress.json` would no longer match.

  Either way, set the GitHub noreply address for future commits:
  `git config user.email "<id>+<username>@users.noreply.github.com"`.
- [ ] **Project-internal notes.** `AGENTS.md`, `AGENTS.md`, `.claude/` and the milestone guides record your decisions
  with dates and some workstation details (Docker inside WSL, the .NET 9 toolchain). This is normal for a learning
  project. Read them once as a stranger would.
- [ ] **Repository URL.** Replace `<repository-url>` in the README once the repository exists.

## 4. Create and configure the GitHub repository

**General**
- [ ] Create the repository as **private** first, push, check everything below, then switch it to public.
- [ ] Add a description, for example "Multi-tenant B2B SaaS backend in .NET: modular monolith, tenant isolation,
  outbox, payments saga, observability". Add the topics `dotnet`, `aspnetcore`, `modular-monolith`, `multi-tenant`,
  `saas`, `postgresql`, `rabbitmq`, `opentelemetry` and `clean-architecture`.
- [ ] Turn on Issues. Turn on Discussions if you want questions kept out of issues; `.github/CONTRIBUTING.md` links to them.
- [ ] Create the labels `bug`, `enhancement`, `needs-triage` (used by the issue forms) and `good first issue`.

**Security (Settings → Code security)**
- [ ] **Private vulnerability reporting: on.** `.github/SECURITY.md` depends on it.
- [ ] Secret scanning and **push protection: on.** On the first push, mark the synthetic Stripe test-key placeholders in the tests as
  false positives if push protection flags them.
- [ ] Dependabot alerts and Dependabot security updates: on.
- [ ] Code scanning (CodeQL default setup): optional; it is free for public repositories.

**Branch protection (Settings → Rules, for `main`)**
- [ ] Require a pull request before merging, with at least one approval once there are other maintainers.
- [ ] Require status checks: the CI job `Restore, format, build, test` and the `security` job.
- [ ] Block force pushes and deletion.

**Actions (Settings → Actions)**
- [ ] Workflow permissions: **read repository contents** by default (the workflows already ask for what they need).
- [ ] Require approval to run workflows for first-time outside contributors.
- [ ] Environments `staging` and `prod`: add required reviewers. The deploy roles trust only these environments on
  `main` (SPEC-0012 criterion 9), so forks cannot deploy.

## 5. After the first public push

- [ ] Check that CI runs green on GitHub. It has only run locally until now.
- [ ] Add a CI status badge to the README.
- [ ] Check the community profile (Insights → Community standards); every item should be ticked.
- [ ] Optional: add `.github/CODEOWNERS` with your GitHub username, so pull requests request your review
  automatically.
- [ ] Optional: tag a first release (`v0.1.0`) with notes that summarise milestones M0 to M10.
