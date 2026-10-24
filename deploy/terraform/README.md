# TenantFlow infrastructure (Terraform, AWS)

Nothing here has been applied yet. Every apply, and every resource it creates, needs the owner's explicit go-ahead
(SPEC-0012). Decisions: [ADR-0032](../../docs/architecture/decisions/0032-aws-and-managed-container-hosting.md) (AWS),
[ADR-0033](../../docs/architecture/decisions/0033-terraform-layout-remote-state-and-secrets.md) (layout, state,
secrets), [ADR-0034](../../docs/architecture/decisions/0034-deployed-environments-configuration-parity-and-budget.md)
(environments, budget), [ADR-0035](../../docs/architecture/decisions/0035-production-identity-provider-hosted-keycloak.md)
(Keycloak).

| Stack | State | Holds |
|-------|-------|-------|
| `bootstrap/` | local first, then moved into its own bucket | State bucket and its KMS key |
| `shared/` | `shared/terraform.tfstate` | ECR repositories, GitHub OIDC provider, image push role, $100 budget |
| `environment/` | `environments/<env>/terraform.tfstate` | One environment: network, load balancer, ECS services, RDS, Amazon MQ, S3, KMS, secrets, deploy role |

dev, staging and prod all use `environment/`. They differ only in `envs/<env>.tfvars` and `envs/<env>.s3.tfbackend`.

## Prerequisites

- Terraform 1.11 or later (pinned in each stack's `required_version`), and the AWS CLI v2.
- Credentials you enter yourself, preferably `aws sso login` (IAM Identity Center with MFA). Never use an access key in
  a file.
- Region: `ap-south-1`, committed in `terraform.tfvars`, `envs/*.tfvars` and the backend settings.
- Values still to give: account id, GitHub repository (`owner/name`, needed for the OIDC trust even before a remote
  exists), `dns_zone_name`, and `identity_admin_cidrs` (your public IP as `x.x.x.x/32`). Pass them with `-var` or a
  git-ignored `staging.local.tfvars`; the budget emails go only in the shell (step 2).
- `identity_admin_cidrs` defaults to empty, which closes the Keycloak admin console to everyone. Without it, no first
  operator or tenant user can be created.

## Checks (no credentials needed)

```powershell
terraform fmt -recursive -check deploy/terraform
terraform -chdir=deploy/terraform/environment init -backend=false
terraform -chdir=deploy/terraform/environment validate
dotnet run --project tests/Security/TenantFlow.Security.Tests -c Release -- -method "*infrastructure_code"
```

If C: is full, set `$env:TEMP` and `$env:TMP` to a folder on D: before `init` or `providers lock`; providers download
through the temp folder.

## Order of the first apply (each step only with the owner's yes)

1. **Bootstrap**:
   - `terraform -chdir=deploy/terraform/bootstrap init`, then `apply` (the region comes from `terraform.tfvars`).
   - Then move its state into the bucket: add `backend "s3" { key = "bootstrap/terraform.tfstate" ... }` and run
     `init -migrate-state`.
   - Delete the local `terraform.tfstate*`.
2. **Shared**:
   - `init -backend-config="bucket=<state_bucket>" -backend-config="kms_key_id=<state_kms_key_arn>"`.
   - Then `apply -var dns_zone_name=<zone> -var github_repository=<owner/name>`, with
     `TF_VAR_budget_alert_emails='["..."]'` set in your shell, never in a file.
3. **Delegate the zone, and wait for it** (once):
   - Add the four `name_servers` from the shared outputs as NS records for `<zone>` at your domain's DNS provider.
   - Wait until `nslookup -type=NS <zone>` returns them. Only then go on to step 5: its certificate validation waits for
     DNS, and without the delegation the apply stalls for up to 75 minutes and then fails.
4. **Images**: `api`, `worker`, `migrator` and `keycloak`, each tagged with one commit. The repositories are immutable,
   so a tag can be pushed only once. Either:
   - **The pipeline** (SPEC-0012 S4) pushes them, once the push hold is lifted and GitHub is set up (section below).
   - **Or from this machine**, signed in with your own session, while the push hold lasts. Images pushed this way have
     no build attestation, so SPEC-0012 criteria 9 and 10 stay open until the pipeline runs.

     ```powershell
     $tag = git rev-parse HEAD
     $registry = "<account id>.dkr.ecr.ap-south-1.amazonaws.com"
     aws ecr get-login-password --region ap-south-1 | wsl -e docker login --username AWS --password-stdin $registry
     foreach ($image in 'api', 'worker', 'migrator') {
       wsl -e docker build -f deploy/docker/Dockerfile --target $image -t "$registry/tenantflow/${image}:$tag" .
       wsl -e docker push "$registry/tenantflow/${image}:$tag"
     }
     wsl -e docker build -f deploy/keycloak/Dockerfile -t "$registry/tenantflow/keycloak:$tag" .
     wsl -e docker push "$registry/tenantflow/keycloak:$tag"
     wsl -e docker logout $registry
     ```

     Build from a committed tree with no local changes (`git status` clean), so the tag names exactly what was built.
5. **Environment, with the services at zero**:
   - `init -backend-config=envs/staging.s3.tfbackend` plus the same bucket and key settings as step 2.
   - The first apply starts no task. Keycloak, the API and the Worker cannot run before their database users and
     schema exist (steps 7 and 8). On a first deployment the circuit breaker has no earlier revision to roll back to,
     so a crash-looping service would be left with a failed deployment.

     ```powershell
     terraform plan -var-file=envs/staging.tfvars -var-file=staging.local.tfvars `
       -var image_tag=<tag> -var keycloak_image_tag=<tag> -var services_running=false `
       -out staging.tfplan
     ```

     `services_running=false` sets every service's count to zero; the sizes still come from `envs/<env>.tfvars`.
   - Set `TF_VAR_alert_emails='["..."]'` in your shell, never in a file: the environment's CloudWatch alarms
     (`monitoring.tf`) email these addresses, and each one must confirm the subscription email once.
   - Review the plan, then `apply staging.tfplan`. Plan files are git-ignored: they can hold sensitive values.
   - Change `keycloak_image_tag` only when Keycloak's image should change: a new value restarts its single task.
6. **Operator secrets** (values never pass through Terraform):

   ```powershell
   aws secretsmanager put-secret-value --secret-id tenantflow/staging/application --secret-string file://application.json
   aws secretsmanager put-secret-value --secret-id tenantflow/staging/broker-application --secret-string file://broker.json
   ```

   - `application.json` keys:
     - `audit_operator_reference_key`: base64 of at least 32 random bytes (`openssl rand -base64 32`).
     - `stripe_secret_key` and `stripe_webhook_secret`, only when `payment_provider = "stripe"` (test-mode keys only
       outside prod).
   - `broker.json` keys: `username`, `password`.
   - Write both files outside the repository and delete them afterwards.
7. **Provisioning** (SPEC-0012 question 6), by the operator:

   ```powershell
   aws ecs run-task --cluster tenantflow-staging --launch-type FARGATE --task-definition tenantflow-staging-provisioning `
     --network-configuration "awsvpcConfiguration={subnets=[<subnet>],securityGroups=[<sg>],assignPublicIp=ENABLED}"
   ```

   - Use the `provisioning_task` output for the subnet and security group (its own group: the only one that reaches the
     broker's management API). The task runs `TenantFlow.Migrator provision` from the Migrator image of `image_tag`,
     the command Compose runs locally. It is done when it exits 0; its log group is `/tenantflow/staging/provisioning`.
   - As the RDS master user, verified against the RDS certificate authority, it creates or re-asserts `tenantflow_owner`,
     `tenantflow_app`, and `keycloak` with its own database.
   - Then it creates or re-asserts the RabbitMQ application user from `broker-application` (MW-A02): no tags, full rights
     only in the `tenantflow` virtual host, and it checks that the management API refuses that user.
8. **Migrator** (the pipeline runs it on every release; by hand only for the first apply or without the pipeline):

   ```powershell
   $task = aws ecs run-task --cluster tenantflow-staging --launch-type FARGATE --task-definition tenantflow-staging-migrator `
     --network-configuration "awsvpcConfiguration={subnets=[<subnet>],securityGroups=[<sg>],assignPublicIp=ENABLED}" `
     --query 'tasks[0].taskArn' --output text
   aws ecs wait tasks-stopped --cluster tenantflow-staging --tasks $task
   aws ecs describe-tasks --cluster tenantflow-staging --tasks $task --query 'tasks[0].containers[0].exitCode'
   ```

   - Use the `migrator_task` output for the subnet and security group. Go on only when the exit code is `0`; otherwise
     read the log group `/tenantflow/staging/migrator`.
9. **Start the services**: plan and apply again, without `services_running=false`, so each service gets its
   count from `envs/staging.tfvars`. Keycloak's first start creates its tables and imports both realms; allow two to
   three minutes.
10. **DNS** is automatic: `api.<env>.<zone>` and `id.<env>.<zone>` point at the load balancer, and the ACM certificate
    validates through Route 53 (`dns.tf`). The `host_names` output lists them.

## Creating users (ADR-0035)

Both realms are imported on Keycloak's first start, with no users. In the admin console (reachable only from
`identity_admin_cidrs`):

- **Operators** (`tenantflow-platform`): create the user, give `platform-admin` or `support-operator`, and set a
  temporary password. At first sign-in they choose a new password and must enrol an OTP.
- **Tenant users** (`tenantflow`): create the user and set a temporary password. Then grant the membership in
  TenantFlow as usual. Their OTP is optional.

## GitHub setup for the release pipeline (when the push hold is lifted)

`.github/workflows/release.yml` builds on main after `ci` passes, then calls `deploy.yml` for staging and prod.

- Repository variables: `AWS_REGION` (`ap-south-1`) and `AWS_IMAGE_PUSH_ROLE_ARN` (shared output `image_push_role_arn`).
- Environments `staging` and `prod` (prod with required reviewers), each with the variables `AWS_DEPLOY_ROLE_ARN`,
  `ECS_CLUSTER`, `MIGRATOR_SUBNETS` (comma-separated) and `MIGRATOR_SECURITY_GROUP` from its stack outputs, and
  `API_BASE_URL` (`https://` plus `host_names.api`).
- No secrets: every AWS call uses the run's OIDC token.

## On-demand environments (ADR-0034 decision 5)

Staging stays up. Dev and prod are applied when needed and destroyed when idle, which keeps the account within $100.
Every apply and destroy still needs the owner's yes.

- **Bring one up:** steps 5 to 10 above with `envs/dev.tfvars` or `envs/prod.tfvars`. Put the operator secrets again each
  time: dev and prod set `secret_recovery_window_days = 0`, so a destroy deletes their secrets at once and the names
  are free for the next apply.
- **Dev down:** `terraform -chdir=deploy/terraform/environment destroy -var-file=envs/dev.tfvars ...`. Dev has no
  deletion protection, and its data is disposable.
- **Prod down:** this takes two reviewed steps on purpose.
  1. Apply with `-var 'database={...deletion_protection=false}' -var load_balancer_deletion_protection=false`, using
     the same database values otherwise.
  2. Destroy. RDS takes a final snapshot (`tenantflow-prod-final`). Delete or keep it; a snapshot costs storage.
- **Check it is gone:** the budget's forecast alarm, and `aws resourcegroupstaggingapi get-resources --tag-filters Key=environment,Values=prod`.

## Rotating generated passwords

Increase `generated_secret_version` and apply. Terraform writes new database and Keycloak passwords to Secrets Manager
without storing them. Then re-run the provisioning step, which sets them on the roles, and restart the services.

## Rollback

The pipeline owns task-definition revisions. Services ignore `task_definition` in Terraform, so a rollback never fights
the state.

**Who changes a task definition.** Terraform writes the API, Worker and Migrator task definitions (settings, secrets,
sizes); the pipeline copies the latest revision and replaces only the application container's image. So a Terraform
change to one of them (a new setting, a new secret, a size) reaches the running API or Worker only at the next rollout:

- Plan with `-var image_tag=<the running commit>`, so the revision Terraform registers names the image already running.
  The running revision names its image by digest; `aws ecr describe-images --repository-name tenantflow/api --image-ids
  imageDigest=<digest> --query 'imageDetails[0].imageTags[0]'` gives its tag, the commit.
- After the apply, roll both services onto it: re-run the deploy workflow for the running commit, or
  `aws ecs update-service --cluster tenantflow-<env> --service <api|worker> --task-definition tenantflow-<env>-<api|worker>`
  (the family name alone means its latest revision).
- Keycloak is not in the pipeline: Terraform owns its service and task definition, and an apply rolls it.

- **Automatic:** a revision that fails its health checks is rolled back by the ECS deployment circuit breaker.
- **Manual:** `aws ecs update-service --cluster tenantflow-<env> --service api --task-definition tenantflow-<env>-api:<previous revision>`,
  and the same for `worker`.
- **The schema is never rolled back:** migrations are expand-contract, so the previous revision runs on the new schema
  (SC-A12).
