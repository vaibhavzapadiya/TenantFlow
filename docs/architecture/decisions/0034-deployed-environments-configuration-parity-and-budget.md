# ADR-0034: Deployed environments, configuration parity, and the budget

- Status: Proposed
- Date: 2026-10-22
- Owners: TenantFlow tech lead (environments and budget chosen by the user on 2026-10-22; acceptance pending)
- Related decisions: closes Open Decision 1 when accepted; ADR-0008, ADR-0017, ADR-0032, ADR-0033; blueprint section 18
  (environment proposal)

## Context
The blueprint lists five lifecycle contexts: Local, CI, Development, Staging and Production. Of these, three are
persistent deployed environments. The owner chose all three, with a budget of $100 a month, and asked that every
environment's configuration be the same. ADR-0032 estimates about $95 a month for one always-on environment.

## Decision
1. **Deployed environments: dev, staging, prod**, in one AWS account (separate accounts are a revisit trigger). Local
   stays on Compose, and CI is ephemeral.
2. **Configuration parity:**
   - Every deployed environment runs `ASPNETCORE_ENVIRONMENT=Production` and the same images.
   - There is no `appsettings.Staging.json` or `appsettings.Dev.json`.
   - What differs (host names, issuer URLs, broker host, sizes, retention) is injected as environment variables and
     secrets by the one Terraform root, from `envs/<name>.tfvars`. This uses .NET's own configuration layering, so no
     wrapper script is needed.
   - Staging behaving differently from production because of a settings file becomes impossible.
3. **Promotion:** one build, one immutable image tag, promoted dev → staging → prod. Each step is a GitHub environment
   with its own deploy role; prod requires a reviewer.
4. **Budget:** a $100 monthly AWS Budget with alarms at 80% actual and 100% forecast. Recipients come from
   `TF_VAR_budget_alert_emails`, never from a committed file.
5. **Fitting three environments into $100** (chosen by the user on 2026-10-22, SPEC-0012 open question 1):
   - Staging is the always-on environment.
   - Dev and prod are applied when needed and destroyed when idle.
   - Dev has no deletion protection, so one `terraform destroy` removes it.
   - Prod keeps deletion protection on its database and load balancer. Destroying it means first applying with the
     protection off, which is a reviewed plan, then destroying. RDS keeps a final snapshot.
   - Prod is applied for the release rehearsal and the M12 gate evidence, then destroyed or kept, as you decide.

## Options considered
- **Three always-on environments:** about $285 a month by the ADR-0032 estimate, which breaks the budget.
- **Shared database and broker across environments:** cheaper, but one environment's credentials or load reach another's
  data. That conflicts with the isolation the per-environment keys and roles exist for.
- **Per-environment appsettings files:** the conventional .NET approach. It lets staging and production diverge silently,
  and it puts host names in the image.

## Consequences
- Production-only behaviour cannot hide in a settings file; environment checks in code use explicit options, not the
  environment name.
- Sizing, autoscaling and alert thresholds stay provisional until the M11 baseline.
- Destroying dev and prod when idle means their data is disposable. Prod with customer data is out of scope until M13
  (recovery drills) has run.

## Validation
- Diff `envs/dev.tfvars`, `envs/staging.tfvars` and `envs/prod.tfvars`: only sizes, retention, protection and the
  address range differ.
- The budget alarm fires as a forecast within the first week if more than one environment stays up.

## Revisit triggers
- Real customers or real personal data (prod becomes always-on, multi-AZ, and a separate account).
- The budget changes.
- A Pre-production or Performance environment gets a defined purpose (blueprint section 18).
