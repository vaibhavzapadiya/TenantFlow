# SPEC-0012: Cloud deployment

- Status: Approved (by the user, 2026-10-22). Amendment 1 (S3c: storage adapter selection and deployed storage
  configuration; criteria 16 to 19, questions 15 to 20) is Approved (by the user, 2026-10-23, every recommendation accepted).
- Milestone: M12 (runs before M11 by the owner's decision, roadmap sequencing note 5)
- Owner: the user
- Related: blueprint section 18; Open Decisions 1 and 2; ADR-0032 (AWS), ADR-0033 (Terraform and state), ADR-0034
  (environments and budget), ADR-0035 (Keycloak), all Proposed; ADR-0009, ADR-0011, ADR-0012, ADR-0021, ADR-0024,
  ADR-0028; threat rows due in M12 (TB-A07, PY-A17, MW-A02, MW-A19, IE-A18, PS-A04, PS-A17, SC-A08, SC-A12, SC-A14,
  SC-A15, SC-A13, DP-A26)

## Outcome
TenantFlow runs in AWS in three environments (dev, staging and prod) from one Terraform root and one immutable image
per build, promoted through GitHub environments. A release can be checked by smoke tests and rolled back to the previous
application version in minutes. Tenant isolation, least-privilege roles and secret handling are as strict as they are
locally.

## Acceptance criteria
1. Given the three stacks, when `terraform fmt -check` and `terraform validate` run, then both pass. Given a planted
   misconfiguration (a public database), when `security-scan.ps1 -Only iac` runs, then it fails (SC-A15).
2. Given the repository, then it holds no Terraform state and no `.tfvars` with secrets or personal data. Each
   environment has its own state object and lock in the encrypted state bucket.
3. Given `envs/dev.tfvars`, `envs/staging.tfvars` and `envs/prod.tfvars`, then they declare the same keys. Every
   environment runs `ASPNETCORE_ENVIRONMENT=Production`, and no per-environment appsettings file exists (ADR-0034).
4. Given an applied environment, when its state is inspected, then no database-role or Keycloak password appears.
5. Given RDS, then the API and Worker connect as `tenantflow_app` and the Migrator as `tenantflow_owner`, over TLS.
   When `tenantflow_app` attempts DDL or `SET row_security = off` against the deployed database, it is refused
   (ADR-0011, ADR-0012).
6. Given staging with tenants A and B, when a member of A requests one of B's resources, then the response is 404 and no
   data is returned (CLAUDE.md rule 1, cross-tenant denial in the cloud).
7. Given Amazon MQ, then the API and Worker connect over AMQPS with the server certificate validated, as an application
   user without the administrator tag. A connection on 5672, or as that user to the management API, is refused (MW-A02,
   MW-A19).
8. Given exports and imports on staging, then files go to the environment's bucket through the S3 adapter. A request
   without TLS is denied, and tenant B cannot download tenant A's export (IE-A18). (Amendment 1: the adapter and the
   cross-tenant denial are proven against S3Mock in S3c, criteria 17 to 19; the TLS denial is the bucket policy, so it
   is proven on staging in S5.)
9. Given GitHub Actions, then images are pushed only from `main` through OIDC. An environment's deploy role is assumable
   only from that GitHub environment, and a token from another branch or environment is refused. No AWS access key
   exists.
10. Given a build, then its images carry an SBOM and build provenance. The digest deployed to prod equals the digest
    tested in staging (SC-A14).
11. Given a release, then the Migrator runs before the services roll. The previous application version passes the smoke
    checks on the new schema, and applied migrations are unchanged (SC-A12).
12. Given a deployed environment, when the smoke checks run, then: `/health/ready` answers 200; an authenticated call
    succeeds for each realm on its own route group only; the Keycloak admin console answers 403 from outside
    `identity_admin_cidrs`; and HTTP redirects to HTTPS.
13. Given a revision that fails its readiness check, when it is deployed, then ECS rolls back to the previous revision
    on its own. Given a healthy but wrong release, when the rollback procedure runs, then the previous revision serves
    traffic again and the time is recorded.
14. Given the production `tenantflow-platform` realm, when an operator signs in without an OTP credential, then sign-in
    is refused (PS-A17).
15. Given the account, then a $100 monthly budget alarms at 80% actual and 100% forecast.

Amendment 1 (Approved 2026-10-23; ADR-0024 amendment 1, Accepted):

16. Given the API or Worker host, when `Storage:Provider` is missing or unknown, or `s3` is chosen without
    `Storage:S3:Bucket` or `Storage:S3:Region`, or `filesystem` without `Storage:Root`, then the host refuses to start
    and the error names the key, never a value. When `filesystem` is chosen without
    `Storage:Filesystem:Enabled=true`, then the host refuses to start. Only local Development settings and test hosts
    switch it on, as the payment simulator is switched on (user, 2026-10-23, at S3c: every test host runs in
    Production with filesystem storage, so an environment-based refusal was replaced by the explicit switch).
17. Given the deployed task definitions in `services.tf`, then the API and Worker both get `Storage__Provider=s3`, the
    environment's files bucket and its region, and no storage credential; the Migrator gets no storage setting. An
    architecture test fails when either host lacks one of them.
18. Given the S3 adapter against S3Mock, when an object is put, read and deleted, then it behaves as the port promises:
    the size and SHA-256 are returned, more than `maxBytes` stores nothing and raises `ObjectTooLargeException`, a
    missing object reads as null, deleting a missing object succeeds, and a stored object whose content no longer
    matches the recorded checksum is refused on read (`StorageIntegrityException`).
19. Given tenants A and B with the S3 adapter, when a member of B requests A's export or import file, then the response
    is 404 and no content is read from storage.

## Invariants
- No remote call inside a database transaction (CLAUDE.md rule 3). Deployment steps are ordered: provision, migrate,
  roll.
- An environment's credentials, key and roles never read another environment's data.
- Infrastructure changes only by a reviewed `terraform apply`; the pipeline changes only task-definition revisions.

## Contract
- No API change. Configuration contract: environment variables and secret names in
  `deploy/terraform/environment/services.tf`.
- New options:
  - `Messaging:RabbitMq:UseTls` (or port 5671 implying TLS), with server-certificate validation.
  - Storage (amendment 1): `Storage:Provider` (`filesystem` | `s3`, required); `Storage:Root` for
    `filesystem`; `Storage:S3:Bucket` and `Storage:S3:Region` for `s3`. No access key setting: credentials come from
    the task role. Local `appsettings.Development.json` sets `filesystem` and keeps today's root.
- Deploy role: `ecs:RegisterTaskDefinition`, `UpdateService` on the env's services, `RunTask` in its cluster,
  `iam:PassRole` on its task roles.

## Data
No schema change. Database roles are provisioned in RDS by the step that `provision-roles.sql` performs locally, plus
a `keycloak` database and role. No migration is edited.

## Failure modes
- **Secret value missing:** ECS cannot start the task, and the deployment fails without touching the running revision.
- **Migrator fails:** the services are not rolled; the previous revision keeps serving.
- **New revision unhealthy:** the circuit breaker rolls back. A schema change is never rolled back (expand-contract).
- **Broker or database unavailable:** readiness fails and the load balancer stops routing (M1, M10 alerts).
- **Keycloak down:** sign-in fails, and unexpired tokens keep working up to one hour.
- **Budget exceeded:** an alarm, not a shutdown. You decide what to destroy.
- **Storage misconfigured (amendment 1):** the host refuses to start; on a new revision the circuit breaker keeps the
  previous one serving.
- **S3 unavailable or slow (amendment 1):** a submission behaves as a failed file write does today: the job stays
  `receiving` and a retry with the same `Idempotency-Key` resumes the upload (ADR-0024 decision 3, M8-R1). A running
  job's attempt fails like any other due-row failure (retried, then dead-lettered; ADR-0023, ADR-0024). A download answers 503 Problem Details with no provider detail (question 19). Readiness does not
  depend on S3 (question 18).
- **Put interrupted (amendment 1):** nothing is visible under the key; the `/tmp` copy is deleted, or vanishes with
  the task.

## Out of scope
Rate limiting and capacity numbers (M11); disaster-recovery drills and RPO/RTO (M13); Kubernetes and multi-region
(M14); autoscaling policies (after the M11 baseline).

## Open questions

**Approved 2026-10-22:** questions 1 to 4 were decided by the user as noted. For 5 to 13 the recommendations below were adopted as written, including AWSSDK.S3 as the only new package (S3c). Question 14 stays the user's.

1. **Three environments within $100** (decided by the user, 2026-10-22): staging always on; dev and prod applied when
   needed and destroyed when idle (ADR-0034 decision 5). Prod keeps deletion protection, so destroying it is a
   deliberate two-step change (deploy/terraform/README.md, "On-demand environments").
2. **Region** (decided by the user, 2026-10-22): `ap-south-1` (Mumbai) for every stack and the state bucket. Before the
   first apply, check that Amazon MQ offers RabbitMQ on mq.t3.micro there, and which RabbitMQ version.
3. **Domain and certificate** (decided by the user, 2026-10-22): a subdomain of a domain you own, delegated to a Route 53
   zone in the `shared` stack (about $0.50 a month), with a CAA record allowing only Amazon.
   - Each environment gets `api.<env>.<zone>` and `id.<env>.<zone>`, with its own ACM certificate validated through
     DNS and alias records to its load balancer.
   - **Still needed from you:** the zone name (`dns_zone_name`), and adding the four NS records at your DNS provider
     once, after `shared` is applied.
4. **GitHub remote and pushing: on hold by the user (2026-10-22, "do not push anything").** There is no remote, and
   nothing is pushed or created on GitHub until you say so. S4 (criteria 9 to 11) is blocked on it; S3 is not.
   - Ready when you lift the hold: gitleaks over all 118 commits and the working tree found nothing (2026-10-22).
   - Recommendation for then: a private repository, with secret scanning and push protection on before the first push
     (SC-A08).
5. **NAT gateway:** tasks use public addresses now (ADR-0032). Recommendation: keep it until real customers arrive, then
   add NAT for prod only.
6. **Provisioning step** (database roles, Keycloak database, RabbitMQ application user): recommendation: a small
   provisioning image run as a one-off ECS task by the pipeline before the Migrator, using the RDS-managed master secret
   and the broker operator secret.
7. **Deployed telemetry** (M10 left alert routing, access control and retention to M12; DP-A11). Recommendation: an
   OpenTelemetry Collector sidecar exporting to Grafana Cloud's free tier. The M10 dashboards and promtool-tested rules
   carry over. It is an external processor of redacted telemetry, so it needs your yes. Alternative: CloudWatch and
   X-Ray, at the cost of rewriting the dashboards.
8. **Database TLS verification:** today it is `SSL Mode=Require`. Recommendation: ship the RDS CA bundle in the image
   and use `VerifyFull`.
9. **Keycloak with more than one task:** recommendation: one task until the gate. Clustering waits for M11 numbers.
10. **Terraform modules** (your question): recommendation: none yet, because each piece has one caller. Extract an
    `ecs-service` module with `moved` blocks when a fourth service or the M14 lab needs it (ADR-0033).
11. **Revocation before expiry** (TB-A07, PS-A16): recommendation: keep the one-hour access tokens and add token
    introspection for platform routes only.
12. **Replacing `scripts/dev.ps1`** (your request, 2026-10-22): this is local tooling, not M12. .NET Aspire's AppHost
    could replace `up`/`down`/`reset`. However, it needs a Docker engine reachable from Windows, and today Docker runs
    only inside WSL (Open Decision 21). It would also change the gate in CLAUDE.md (`verify.ps1 -Database`) and the
    operations docs. Recommendation: a separate spike after M12 with its own ADR; until then `dev.ps1` stays.
13. **Your AWS credentials:** recommendation: IAM Identity Center (SSO) with MFA and `aws sso login`, with no long-lived
    access keys. You enter credentials yourself; nothing is pasted into chat.
14. **Still yours, moved from M11:** SC-A13 (.NET 10, due 2026-11-10) and DP-A26 (accept ADR-0025).

**Amendment 1 (Approved 2026-10-23 by the user: every recommendation in questions 15 to 20 accepted as written,
and ADR-0024 amendment 1 accepted): storage for S3c.** Found in a review of `deploy/`: the hosts require `Storage:Root`
at startup, `services.tf` passes no storage setting, and the filesystem adapter needs a folder the API and Worker share,
which separate Fargate tasks cannot have, so neither host would start on the first apply.

15. **How the adapter is chosen.** Recommendation: an explicit `Storage:Provider` (`filesystem` | `s3`), validated at
    startup with the keys each needs, as `Payments:Provider` is; Production refuses `filesystem` (implemented as an explicit `Storage:Filesystem:Enabled`
    switch, criterion 16). Alternative: `s3`
    whenever `Storage:S3:Bucket` is set, which falls back to the filesystem silently when the setting is missing.
16. **Credentials and region.** Recommendation: the task role through the SDK's default credential chain (no access
    key anywhere); `Storage:S3:Bucket` and `Storage:S3:Region` set explicitly from Terraform rather than read from the
    environment ECS provides. S3Mock tests pass a service URL and dummy credentials through a test-only override.
17. **Keeping the port's promises on S3.** Recommendation: stream the content into `/tmp` while counting bytes and
    hashing, then one `PutObject` with `ChecksumSHA256`; S3 stores an object whole or not at all, and verifies the
    checksum. `DeleteTemporaryFilesAsync` answers 0 (the bucket's lifecycle rule aborts incomplete multipart uploads).
    Alternative: multipart upload straight from the request stream: no `/tmp` copy, but more code, and the size and
    hash are known only at the end. Fargate's default 20 GiB of ephemeral storage covers the `/tmp` copies.
18. **Readiness.** Recommendation: no bucket call in `/health/ready`; startup validates the settings only. A probe per
    15 seconds costs requests, and an S3 blip would take every API task out of the load balancer.
19. **A download when S3 fails.** Today a storage exception surfaces as a 500. Recommendation: 503 Problem Details with
    no provider detail, so a client knows to retry; a missing object stays 404.
20. **Signed URLs** (blueprint: "when suitable"; ADR-0024 follow-up). Recommendation: out of scope. Downloads keep
    streaming through the authorized endpoint, which is the cross-tenant control; nothing needs direct access yet.

## Slices

| Slice | Delivers | Criteria |
|-------|----------|----------|
| S1 | AWS versus GCP comparison and recommendation (ADR-0032) | — |
| S2 | Terraform: bootstrap state, shared registry/OIDC/budget, one environment root for three environments, IaC scan. Written and validated; **not applied** (no credentials yet) | 1, 2, 3, 15 (code) |
| S3a | RabbitMQ over TLS with server-certificate validation (MW-A19) | 7 (TLS part) |
| S3b | **Moved to M11 by the user (2026-10-22):** forwarded headers have no consumer yet (no code reads the client IP or scheme; Location headers are relative). They arrive with rate limiting, their first consumer (T-25) | — |
| S3c | S3 storage adapter behind the storage port, AWSSDK.S3 (IE-A18), tested against Adobe S3Mock in the database test run only, never in the dev stack (user, 2026-10-22; ADR-0024 amendment 1). Amendment 1: `Storage:Provider` selection and startup validation, the `services.tf` storage settings for the API and Worker, and a 503 download answer when S3 fails | 8 (adapter, cross-tenant), 16, 17, 18, 19 |
| S3d | Dockerfiles for API, Worker and Migrator: non-root, read-only root, RDS CA bundle and `VerifyFull` (question 8) | 5 (TLS part) |
| S3e | Provisioning task (database roles, Keycloak database) and the production platform realm requiring OTP | 5, 14 |
| S3f | RabbitMQ application user: no tags, own virtual host, management API refused; reachable only from the provisioning task | 7 (user part) |
| S3g | Keycloak image (Compose version, built ahead, both production realms, RDS CA) and the production `tenantflow` realm: operator-created users, optional OTP, PKCE client (user, 2026-10-23) | 14 (deployed) |
| S4 | GitHub Actions: build once, SBOM and provenance, push via OIDC, migrate, deploy, promote; previous-version-on-new-schema check | 9, 10, 11 |
| S5 | Apply staging (your yes), smoke checks, cross-tenant denial in the cloud, rollback rehearsal, then dev and prod | 4, 6, 8 (TLS denial, amendment 1), 12, 13, gate |

## Verification

| Criterion | Test | Proven to fail without the change |
|-----------|------|------------------------------------|
| 1 | `terraform validate` (3 stacks), `security-scan.ps1 -Only iac` | Public RDS instance: AWS-0180 HIGH fails the scan (S2; re-proven at the gate run, 2026-10-23) |
| 3 | `EnvironmentParityTests`: dev, staging and prod declare the same top-level settings | Failed on the gate run (staging lacked `secret_recovery_window_days`); passes after the fix (2026-10-23) |
| 5 (TLS part) | `ContainerImageTests` (deny-all context, non-root chiseled stages, VerifyFull with the shipped CA) | Deny-all line removed: fails at `Assert.Equal`; digest removed: fails at `Assert.Matches` (S3d). Images built and run read-only with all capabilities dropped (2026-10-23) |
| 5 (roles) | Provisioning script run twice in a throwaway PostgreSQL 17.11: roles, Keycloak database, cross-database refusals | Observed (S3e); on RDS in S5 |
| 14 | `ProductionRealmTests`; live sign-in against a throwaway Keycloak 26.8 issues no code without OTP | Step CONDITIONAL: test fails at `Assert.Equal`; enforcement off: a code is issued (S3e) |
| 9, 10, 11 (wiring) | `ReleaseWorkflowTests`; `release.sh migrations-unchanged` on the real history and on an edited migration | prod needing only build: fails at `Assert.Contains` (S4); not yet run on GitHub |
| 7 (user part) | `provision-broker.sh` run twice against RabbitMQ 4.3.6: no tags, rights in `tenantflow` only, AMQP login works, management API 401; `BrokerProvisioningTests` | Tag added in a copy: the script fails on its own check; Worker given the 443 rule: test fails at `Assert.Contains` (S3f) |
| 14 (both realms, image) | Keycloak image built and run read-only against PostgreSQL: both realms imported; tenant user with temporary password must change it first; without OTP gets a code; after enrolling, OTP asked then code; password grant `unauthorized_client`; operator without OTP sent to OTP setup; restart keeps console changes | Tenant OTP subflow DISABLED: `ProductionRealmTests` fails at `Assert.Equal` (S3g) |
| 7 (TLS rule) | `WorkerHostTests.Host_refuses_to_start_with_plain_amqp_to_a_remote_broker_and_names_the_key`, `Host_starts_with_tls_to_a_remote_broker` | Rule disabled: fails at `Assert.NotNull` (S3a); the handshake itself is checked in S5 |
| 16 | `ConfigurationValidationTests.Host_refuses_to_start_with_incomplete_storage_settings_and_names_the_key` (4 cases), `Host_refuses_filesystem_storage_unless_it_is_switched_on`, `Host_starts_with_s3_storage_and_no_storage_credential`, `Host_refuses_to_start_without_a_storage_root_and_names_the_key` | Run against the code before S3c (temporary worktree): the switch test fails at `Assert.Contains` (S3c) |
| 17 | `ContainerImageTests.Deployed_api_and_worker_keep_files_in_the_environments_bucket_without_a_storage_credential` | Failed at `Assert.Contains` before `services.tf` set the storage settings (seen red, S3c); break-it proof deferred to gate |
| 18 | `S3StorageTests.Put_read_and_delete_keep_the_storage_ports_promises`, `Content_changed_in_the_bucket_fails_its_checksum_when_read` (S3Mock 5.2.3) | Seen red before the adapter existed (the module ignored `s3`); break-it proof deferred to gate |
| 19, 8 (adapter, cross-tenant) | `S3StorageTests.Another_tenant_can_neither_open_nor_delete_a_file_in_the_bucket`; question 19: `ExportJobTests.A_download_answers_503_without_provider_detail_while_storage_is_unavailable` | Seen red before the change (503 test: 500 `server.unexpected`); break-it proofs deferred to gate. Criterion 8's TLS denial is S5 |
