# Security policy

TenantFlow is a learning and portfolio project. It is **not** production software, and there is no commercial
support or service-level agreement. Still, security matters here: tenant isolation, payments and audit are central to
the design. Reports are welcome and taken seriously.

## Supported versions

Only the latest commit on `main` receives fixes. There are no released versions yet.

## Reporting a vulnerability

**Please do not open a public issue, discussion or pull request for a security problem.**

Report it privately through GitHub's private vulnerability reporting:

1. Open the repository's **Security** tab.
2. Choose **Report a vulnerability**.
3. Describe the problem, the affected area (for example tenant isolation, authentication, webhooks, imports), the
   steps to reproduce it, and the impact you expect.

Never include real personal data, real credentials or real payment details in a report. Use synthetic data, as the
project does.

## What to expect

| Step | Target |
|---|---|
| Acknowledgement of your report | within 7 days |
| First assessment (accepted, needs more information, or not a vulnerability) | within 14 days |
| Fix or mitigation for an accepted report | depends on severity; critical issues first |

Once the fix is on `main`, the issue is disclosed publicly through a GitHub security advisory. You are credited
unless you ask not to be.

## Scope

**In scope:** the code and configuration in this repository, for example:
- cross-tenant data access or tenant-context confusion;
- broken authorization or privilege escalation;
- webhook signature bypass or replay;
- injection, unsafe file handling, and CSV injection;
- secrets or personal data leaking into logs, telemetry or errors;
- insecure defaults in `deploy/`.

**Out of scope:**
- vulnerabilities in third-party packages or images with no TenantFlow-specific impact (report those upstream);
- findings that need a compromised developer machine;
- the local development stack's deliberately local-only defaults, such as the synthetic Keycloak users and the
  passwords in `.env.example`, when they are used as intended on `127.0.0.1`;
- denial of service through high request volume (rate limiting is planned in milestone M11);
- reports from automated scanners with no demonstrated impact.

## Safe harbour

If you act in good faith, only test against your own local copy of TenantFlow, do not access data that is not yours,
and give us reasonable time to fix the problem before you disclose it, we will not take action against you.

## How the project handles security

The threat models are in [docs/threat-models/](../docs/threat-models/README.md), with every mitigation tracked.
`dotnet run --project tests/Security/TenantFlow.Security.Tests -c Release` checks for secrets, vulnerable dependencies and images, and unsafe configuration. Read
the threat model for the area you are testing; it lists accepted residual risks, which are known and not new findings.
