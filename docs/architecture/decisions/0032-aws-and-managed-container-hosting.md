# ADR-0032: AWS with ECS Fargate, RDS for PostgreSQL and Amazon MQ for RabbitMQ

- Status: Proposed
- Date: 2026-10-22
- Owners: TenantFlow tech lead (AWS chosen by the user on 2026-10-22; acceptance pending)
- Related decisions: closes Open Decision 2 when accepted; ADR-0002, ADR-0012, ADR-0021, ADR-0024; blueprint section 18;
  SPEC-0012

## Context
The blueprint (section 18) compares AWS and GCP on a small representative deployment and keeps the first choice
provisional. TenantFlow deploys three processes: the API, the Worker (a long-running RabbitMQ consumer that also runs
clustered Quartz), and a one-off Migrator. It needs PostgreSQL 17 with row-level security, RabbitMQ (ADR-0021),
object storage (ADR-0024), secrets, and an OIDC issuer. The budget is $100 a month for the whole account (ADR-0034).
M11 has not run, so there are no measured sizes (roadmap sequencing note 5).

## Decision
1. **AWS**, one account, one region: `ap-south-1` (Mumbai), chosen by the user on 2026-10-22 (SPEC-0012 open question
   2). Public names live in a Route 53 subdomain zone, with ACM certificates validated through DNS (open question 3).
2. **ECS on Fargate** for the API, Worker and Keycloak as services, and the Migrator as a task run before a rollout.
   No Kubernetes (blueprint section 18, hosting evolution 1 and 7).
3. **RDS for PostgreSQL 17**, single-AZ outside production, `rds.force_ssl` on, encrypted with the environment's key,
   with automated backups (point-in-time recovery within the retention window).
4. **Amazon MQ for RabbitMQ** over AMQPS (5671). This decides the comparison: it is a managed RabbitMQ with TLS and
   separate users, which MW-A02 and MW-A19 need, and GCP has no managed RabbitMQ.
5. **S3** for files, **ECR** for images, **Secrets Manager** and **KMS** for secrets and keys, **CloudWatch Logs** for
   container output.
6. **Tasks in public subnets with public addresses, without a NAT gateway.** A NAT gateway costs about a third of the
   budget per environment. Tasks accept traffic only from the load balancer's security group and send only HTTPS,
   PostgreSQL and AMQPS. The database and broker sit in private subnets with no internet route.

## Options considered
- **GCP: Cloud Run, Cloud SQL, a self-managed RabbitMQ on Compute Engine.** Cheaper (the API can scale to zero; there is
  no load balancer charge for a single service). But the broker becomes ours to patch, secure with TLS and back up,
  and the Worker needs CPU always allocated, which removes most of the scale-to-zero saving. Workload Identity
  Federation for GitHub is just as good as AWS OIDC.
- **AWS with Amazon MQ replaced by a RabbitMQ task on Fargate.** Cheaper than Amazon MQ, but a broker without durable
  storage across task replacement loses messages; EFS-backed RabbitMQ is not a pattern to learn on.
- **AWS with tasks in private subnets and a NAT gateway.** This is the better posture: tasks have no public address. It
  costs about $32 a month per environment plus data. It is the recommended change for production once the budget allows
  (SPEC-0012 open question 5).
- **Amazon SQS/SNS instead of RabbitMQ.** It would rewrite ADR-0021's adapter and its tests; it is an M14 lab topic at most.

## Consequences
- Rough cost per always-on environment, prices not yet verified: load balancer $18, Amazon MQ mq.t3.micro $25,
  RDS db.t4g.micro $15, four small Fargate tasks $35, KMS, logs and secrets $5. That is about $95, so **three always-on
  environments do not fit $100** (ADR-0034 decides how they share it).
- The RabbitMQ client must support TLS (port 5671, server certificate validated). `RabbitMqOptions` has no TLS setting
  today. That is a code slice in SPEC-0012.
- Files need the S3 adapter behind the storage port (IE-A18). The filesystem adapter does not work across Fargate tasks.
- Amazon MQ creates one user. The least-privilege application user (MW-A02) is created inside RabbitMQ by a
  provisioning step.
- Amazon MQ's RabbitMQ version may trail the local 4.3. Check the version before the first apply.

## Validation
- `terraform validate` and the IaC scan (`./scripts/security-scan.ps1 -Only iac`) pass for every stack.
- After apply (with your go-ahead): the smoke checks and rollback rehearsal in SPEC-0012 run against staging.
- The first month's bill against the estimate above.

## Revisit triggers
- The bill differs from the estimate by more than half.
- The M11 baseline shows Fargate or the broker tier is the bottleneck.
- A second region or a data-residency requirement appears.
