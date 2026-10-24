# ADR-0033: Terraform layout, remote state, and secrets kept out of state

- Status: Proposed
- Date: 2026-10-22
- Owners: TenantFlow tech lead (acceptance pending)
- Related decisions: ADR-0008 (local secrets), ADR-0028 (scanning), ADR-0032, ADR-0034; blueprint section 18
  (Terraform); SC-A15; SPEC-0012

## Context
Blueprint section 18 asks for these Terraform properties:
- remote state with access control, encryption and locking;
- pinned versions;
- secrets kept out of state where possible;
- least-privilege, short-lived deployment identities;
- separate reusable code from per-environment configuration;
- no generic abstraction before a second real use.

There are three environments (ADR-0034) and some account-wide resources.

## Decision
1. **Three stacks** under `deploy/terraform/`:
   - `bootstrap`: the state bucket and its key.
   - `shared`: the registry, GitHub OIDC, the image push role and the budget.
   - `environment`: everything one environment owns.

   Each has its own state, so an environment apply can never touch the registry or another environment.
2. **One `environment` root for dev, staging and prod.** The environments differ only in
   `envs/<name>.tfvars` and `envs/<name>.s3.tfbackend`.
3. **No child modules yet.** Each piece would have exactly one caller. The first candidate is an `ecs-service`
   module: the API, Worker and Keycloak share the same shape. It is extracted with `moved` blocks when that pays off
   (SPEC-0012 open question 10).
4. **Remote state:**
   - S3 in the same account and region, versioned, encrypted with its own KMS key, public access blocked, TLS only.
   - Locking through S3 conditional writes (`use_lockfile`), so no DynamoDB table is needed.
   - The bootstrap stack starts with local state and moves it into the bucket after its first apply.
   - State is never committed (`.gitignore`).
5. **Secrets:**
   - The RDS master password is managed by RDS (`manage_master_user_password`).
   - Database role and Keycloak passwords come from `ephemeral` resources, written through write-only arguments
     (`secret_string_wo`). They exist in Secrets Manager, never in state or plans.
   - Provider keys and the audit key are created as empty secrets; the operator puts their values with the AWS CLI.
   - The one exception is the Amazon MQ operator password, which the provider accepts only as a plain argument. It is
     therefore in state, which is encrypted and readable only by the operator.
6. **Pins:**
   - Terraform `>= 1.11, < 2`; providers `~> 6.0` (AWS) and `~> 3.7` (random).
   - `.terraform.lock.hcl` is committed with hashes for linux_amd64 and windows_amd64.
   - Provider upgrades are reviewed changes to the lock file, regenerated with `terraform providers lock`.
7. **Who changes what:**
   - Infrastructure changes are a reviewed `terraform plan` and `apply` by the operator.
   - The pipeline only registers task-definition revisions, runs the Migrator and rolls services. Services ignore
     `task_definition`, so a deploy is not drift.
8. **Checks:**
   - `terraform fmt -check` and `terraform validate` for every stack.
   - The IaC scan (`security-scan.ps1 -Only iac`, Trivy misconfiguration checks; HIGH or CRITICAL fail).
   - Exceptions go in `.config/security-exceptions.yaml`.

## Options considered
- **A directory per environment calling a shared module.** This is the common layout. It triples the wiring, and the
  copies drift. The owner asked for identical configuration across environments, which one root gives by construction.
- **Terraform workspaces.** One backend and one set of credentials for all environments. A wrong `workspace select` hits
  production. Separate backend keys plus `allowed_account_ids` make the target explicit instead.
- **Terragrunt.** It solves the per-environment wiring, but it is one more tool to learn and pin before the plain layout
  has shown a problem.
- **DynamoDB locking.** Superseded by S3 native locking in Terraform 1.10.
- **Generate every secret in Terraform.** Simple, but it puts every password in state.

## Consequences
- An apply needs `-var-file` and `-backend-config` for the environment. The README gives the exact commands.
- Rotating a generated password means bumping `generated_secret_version`, then re-running the provisioning step that
  sets the role passwords.
- The empty operator secrets must be filled before the first task starts, or ECS cannot start it.
- The C: drive on the current workstation is full. Terraform downloads providers through `%TEMP%`, so point TEMP at D:
  when you lock providers.

## Validation
- `terraform validate` passes for the three stacks; the IaC scan passes; a planted public database fails it (AWS-0180).
- After the first apply: `terraform show` on a state copy contains no database or Keycloak password.

## Revisit triggers
- A second account per environment (an AWS Organization), which moves state and roles.
- CI runs `terraform plan` (it needs a read-only plan role and state access).
- A second real caller for an `ecs-service` module.
