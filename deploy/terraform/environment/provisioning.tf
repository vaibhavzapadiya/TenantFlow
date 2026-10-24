# The provisioning task (SPEC-0012 question 6, S3e and S3f): "TenantFlow.Migrator provision", the command Compose runs
# locally, in the Migrator image of the release. As the RDS master user it creates or re-asserts the owner and
# application roles (ADR-0012) and Keycloak's role and database (ADR-0035); then, as the broker's operator, the RabbitMQ
# application user (MW-A02). It runs as a one-off ECS task on the first deploy and after every password rotation,
# before the Migrator (deploy/terraform/README.md).
#
# It is an operator step, never a pipeline step: the task reads the RDS master password, so it has its own execution
# role, which the deploy role cannot pass.

resource "aws_iam_role" "provisioning_execution" {
  name               = "${local.name}-provisioning-execution"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_trust.json
}

data "aws_iam_policy_document" "provisioning_execution" {
  statement {
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"]
  }

  statement {
    actions   = ["ecr:BatchCheckLayerAvailability", "ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer"]
    resources = [data.aws_ecr_repository.image["migrator"].arn]
  }

  statement {
    actions   = ["logs:CreateLogStream", "logs:PutLogEvents"]
    resources = ["${aws_cloudwatch_log_group.service["provisioning"].arn}:*"]
  }

  statement {
    actions = ["secretsmanager:GetSecretValue"]
    resources = [
      aws_db_instance.main.master_user_secret[0].secret_arn,
      aws_secretsmanager_secret.database_owner.arn,
      aws_secretsmanager_secret.database_app.arn,
      aws_secretsmanager_secret.keycloak.arn,
      aws_secretsmanager_secret.broker_operator.arn,
      aws_secretsmanager_secret.broker_application.arn,
    ]
  }

  statement {
    actions   = ["kms:Decrypt"]
    resources = [aws_kms_key.environment.arn, data.aws_kms_alias.registry.target_key_arn]
  }
}

resource "aws_iam_role_policy" "provisioning_execution" {
  name   = "start-provisioning-task"
  role   = aws_iam_role.provisioning_execution.id
  policy = data.aws_iam_policy_document.provisioning_execution.json
}

resource "aws_ecs_task_definition" "provisioning" {
  family                   = "${local.name}-provisioning"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.provisioning_execution.arn
  # No task role: the provisioner calls PostgreSQL and the broker's management API, never an AWS API.

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }

  volume {
    name = "tmp"
  }

  container_definitions = jsonencode([{
    name                   = "provisioning"
    image                  = "${data.aws_ecr_repository.image["migrator"].repository_url}:${var.image_tag}"
    essential              = true
    command                = ["provision"]
    readonlyRootFilesystem = true
    mountPoints            = local.tmp_mount
    environment = [
      { name = "DOTNET_ENVIRONMENT", value = "Production" },
      { name = "Provisioning__Database__Host", value = local.database_host },
      { name = "Provisioning__Database__Database", value = local.database_name },
      # The server is verified against the RDS certificate authority the image ships (deploy/certs).
      { name = "Provisioning__Database__SslMode", value = "VerifyFull" },
      { name = "Provisioning__Database__RootCertificate", value = "/app/certs/rds-ca.pem" },
      { name = "Provisioning__Broker__ManagementUrl", value = "https://${local.broker_host}" },
      { name = "Provisioning__Broker__VirtualHost", value = local.broker_vhost },
    ]
    secrets = [
      { name = "Provisioning__Database__Username", valueFrom = "${aws_db_instance.main.master_user_secret[0].secret_arn}:username::" },
      { name = "Provisioning__Database__Password", valueFrom = "${aws_db_instance.main.master_user_secret[0].secret_arn}:password::" },
      { name = "Provisioning__Database__OwnerPassword", valueFrom = "${aws_secretsmanager_secret.database_owner.arn}:password::" },
      { name = "Provisioning__Database__AppPassword", valueFrom = "${aws_secretsmanager_secret.database_app.arn}:password::" },
      { name = "Provisioning__Database__KeycloakPassword", valueFrom = "${aws_secretsmanager_secret.keycloak.arn}:database_password::" },
      { name = "Provisioning__Broker__OperatorUsername", valueFrom = "${aws_secretsmanager_secret.broker_operator.arn}:username::" },
      { name = "Provisioning__Broker__OperatorPassword", valueFrom = "${aws_secretsmanager_secret.broker_operator.arn}:password::" },
      { name = "Provisioning__Broker__AppUsername", valueFrom = "${aws_secretsmanager_secret.broker_application.arn}:username::" },
      { name = "Provisioning__Broker__AppPassword", valueFrom = "${aws_secretsmanager_secret.broker_application.arn}:password::" },
    ]
    logConfiguration = local.log_configuration["provisioning"]
  }])
}
