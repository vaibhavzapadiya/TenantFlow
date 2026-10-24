# ECS on Fargate: the API, the Worker and Keycloak as services, the Migrator as a task the pipeline runs before each
# rollout (ADR-0032). Every deployed environment runs with ASPNETCORE_ENVIRONMENT=Production; what differs between
# environments is injected here from the tfvars, never from per-environment appsettings files (ADR-0034).
#
# Who changes what: Terraform creates the services and their first task definition. After that the pipeline
# registers a new revision with the promoted image and rolls the service to it, so the API and Worker services
# ignore task_definition changes. An application rollback is "roll the service to the previous revision"; ECS
# also rolls back by itself when a new revision fails its health checks (deployment circuit breaker).

locals {
  http_port                = 8080
  keycloak_management_port = 9000
  amqps_port               = 5671

  issuer_base = "https://${local.identity_host_name}/realms"
  # The application's own virtual host on the broker; its user has rights there only (the provisioning task, provisioning.tf).
  broker_vhost = "tenantflow"
  broker_host  = regex("^amqps://([^:/]+)", aws_mq_broker.main.instances[0].endpoints[0])[0]

  # Settings shared by the API and Worker; ASP.NET Core maps "__" to ":".
  application_environment = concat(
    [
      { name = "ASPNETCORE_ENVIRONMENT", value = "Production" },
      { name = "DOTNET_ENVIRONMENT", value = "Production" },
      { name = "OTEL_RESOURCE_ATTRIBUTES", value = "deployment.environment.name=${var.environment}" },
      { name = "Payments__Provider", value = var.payment_provider },
      # Tasks share no disk, so imported and exported files live in the environment's bucket (ADR-0024 amendment 1);
      # the SDK takes the task role's credentials, so no storage credential is set here.
      { name = "Storage__Provider", value = "s3" },
      { name = "Storage__S3__Bucket", value = aws_s3_bucket.files.bucket },
      { name = "Storage__S3__Region", value = var.aws_region },
    ],
    var.otlp_endpoint == null ? [] : [{ name = "OTEL_EXPORTER_OTLP_ENDPOINT", value = var.otlp_endpoint }],
    # The hosts refuse the simulator unless it is switched on explicitly; choosing it here is that choice.
    var.payment_provider != "simulator" ? [] : [{ name = "Payments__Simulator__Enabled", value = "true" }],
  )

  application_secrets = concat(
    [
      { name = "ConnectionStrings__TenantFlow", valueFrom = "${aws_secretsmanager_secret.database_app.arn}:connection_string::" },
      { name = "Audit__OperatorReferenceKey", valueFrom = "${aws_secretsmanager_secret.application.arn}:audit_operator_reference_key::" },
    ],
    var.payment_provider != "stripe" ? [] : [
      { name = "Payments__Stripe__SecretKey", valueFrom = "${aws_secretsmanager_secret.application.arn}:stripe_secret_key::" },
      { name = "Payments__Stripe__WebhookSecret", valueFrom = "${aws_secretsmanager_secret.application.arn}:stripe_webhook_secret::" },
    ],
  )

  # Only the Worker talks to the broker (the API writes outbox rows), so only it gets the broker's address and user.
  worker_broker_environment = [
    { name = "Messaging__RabbitMq__Host", value = local.broker_host },
    { name = "Messaging__RabbitMq__Port", value = tostring(local.amqps_port) },
    { name = "Messaging__RabbitMq__UseTls", value = "true" },
    { name = "Messaging__RabbitMq__VirtualHost", value = local.broker_vhost },
  ]
  worker_broker_secrets = [
    { name = "Messaging__RabbitMq__Username", valueFrom = "${aws_secretsmanager_secret.broker_application.arn}:username::" },
    { name = "Messaging__RabbitMq__Password", valueFrom = "${aws_secretsmanager_secret.broker_application.arn}:password::" },
  ]

  # The .NET containers run with a read-only root file system; /tmp is the only writable path.
  tmp_mount = [{ sourceVolume = "tmp", containerPath = "/tmp", readOnly = false }]

  log_configuration = {
    for service, group in aws_cloudwatch_log_group.service : service => {
      logDriver = "awslogs"
      options = {
        awslogs-group         = group.name
        awslogs-region        = var.aws_region
        awslogs-stream-prefix = service
      }
    }
  }
}

resource "aws_ecs_cluster" "main" {
  name = local.name

  # Container Insights is charged per metric; the hosts already export their own metrics over OTLP (M10).
  setting {
    name  = "containerInsights"
    value = "disabled"
  }
}

# --- API ------------------------------------------------------------------------------------------------------------

resource "aws_ecs_task_definition" "api" {
  family                   = "${local.name}-api"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = var.api.cpu
  memory                   = var.api.memory
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.task["api"].arn

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }

  volume {
    name = "tmp"
  }

  container_definitions = jsonencode([{
    name                   = "api"
    image                  = "${data.aws_ecr_repository.image["api"].repository_url}:${var.image_tag}"
    essential              = true
    readonlyRootFilesystem = true
    portMappings           = [{ containerPort = local.http_port, protocol = "tcp" }]
    mountPoints            = local.tmp_mount
    environment = concat(local.application_environment, [
      { name = "ASPNETCORE_HTTP_PORTS", value = tostring(local.http_port) },
      { name = "Authentication__Tenant__Authority", value = "${local.issuer_base}/tenantflow" },
      { name = "Authentication__Tenant__Audience", value = "tenantflow-api" },
      { name = "Authentication__Platform__Authority", value = "${local.issuer_base}/tenantflow-platform" },
      { name = "Authentication__Platform__Audience", value = "tenantflow-platform-api" },
    ])
    secrets          = local.application_secrets
    logConfiguration = local.log_configuration["api"]
  }])
}

resource "aws_ecs_service" "api" {
  name            = "api"
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.api.arn
  desired_count   = var.services_running ? var.api.desired_count : 0
  launch_type     = "FARGATE"
  propagate_tags  = "SERVICE"

  deployment_minimum_healthy_percent = 100
  deployment_maximum_percent         = 200
  health_check_grace_period_seconds  = 60

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  network_configuration {
    subnets          = aws_subnet.public[*].id
    security_groups  = [aws_security_group.api.id]
    assign_public_ip = true
  }

  load_balancer {
    target_group_arn = aws_lb_target_group.api.arn
    container_name   = "api"
    container_port   = local.http_port
  }

  lifecycle {
    ignore_changes = [task_definition]
  }

  depends_on = [aws_lb_listener.https]
}

# --- Worker ---------------------------------------------------------------------------------------------------------

resource "aws_ecs_task_definition" "worker" {
  family                   = "${local.name}-worker"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = var.worker.cpu
  memory                   = var.worker.memory
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.task["worker"].arn

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }

  volume {
    name = "tmp"
  }

  container_definitions = jsonencode([{
    name                   = "worker"
    image                  = "${data.aws_ecr_repository.image["worker"].repository_url}:${var.image_tag}"
    essential              = true
    readonlyRootFilesystem = true
    mountPoints            = local.tmp_mount
    environment            = concat(local.application_environment, local.worker_broker_environment)
    secrets                = concat(local.application_secrets, local.worker_broker_secrets)
    logConfiguration       = local.log_configuration["worker"]
    # No load balancer checks the Worker, so ECS does: a Worker whose host stops answering /health/live is replaced. The
    # image has no shell, so the probe is the Worker itself (src/Hosts/TenantFlow.Worker/Health/LivenessProbe.cs).
    healthCheck = {
      command     = ["CMD", "dotnet", "TenantFlow.Worker.dll", "--probe-live"]
      interval    = 30
      timeout     = 10
      retries     = 3
      startPeriod = 60
    }
    # Long enough for an in-flight message to finish and be acknowledged (ADR-0021).
    stopTimeout = 60
  }])
}

resource "aws_ecs_service" "worker" {
  name            = "worker"
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.worker.arn
  desired_count   = var.services_running ? var.worker.desired_count : 0
  launch_type     = "FARGATE"
  propagate_tags  = "SERVICE"

  deployment_minimum_healthy_percent = 100
  deployment_maximum_percent         = 200

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  network_configuration {
    subnets          = aws_subnet.public[*].id
    security_groups  = [aws_security_group.worker.id]
    assign_public_ip = true
  }

  lifecycle {
    ignore_changes = [task_definition]
  }
}

# --- Migrator (run by the pipeline, never a service) ----------------------------------------------------------------

resource "aws_ecs_task_definition" "migrator" {
  family                   = "${local.name}-migrator"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.task["migrator"].arn

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }

  volume {
    name = "tmp"
  }

  container_definitions = jsonencode([{
    name                   = "migrator"
    image                  = "${data.aws_ecr_repository.image["migrator"].repository_url}:${var.image_tag}"
    essential              = true
    readonlyRootFilesystem = true
    mountPoints            = local.tmp_mount
    environment = [
      { name = "DOTNET_ENVIRONMENT", value = "Production" },
    ]
    secrets = [
      { name = "ConnectionStrings__TenantFlowOwner", valueFrom = "${aws_secretsmanager_secret.database_owner.arn}:connection_string::" },
    ]
    logConfiguration = local.log_configuration["migrator"]
  }])
}

# --- Keycloak (ADR-0035) --------------------------------------------------------------------------------------------

resource "aws_ecs_task_definition" "keycloak" {
  family                   = "${local.name}-keycloak"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = var.keycloak.cpu
  memory                   = var.keycloak.memory
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.task["keycloak"].arn

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }

  volume {
    name = "tmp"
  }

  volume {
    name = "keycloak-tmp"
  }

  container_definitions = jsonencode([{
    name      = "keycloak"
    image     = "${data.aws_ecr_repository.image["keycloak"].repository_url}:${var.keycloak_image_tag}"
    essential = true
    # Built ahead (deploy/keycloak/Dockerfile), so nothing is written outside the temporary folders. The import only
    # creates a realm that does not exist yet.
    command                = ["start", "--optimized", "--import-realm"]
    readonlyRootFilesystem = true
    mountPoints = [
      { sourceVolume = "tmp", containerPath = "/tmp", readOnly = false },
      { sourceVolume = "keycloak-tmp", containerPath = "/opt/keycloak/data/tmp", readOnly = false },
    ]
    portMappings = [
      { containerPort = local.http_port, protocol = "tcp" },
      { containerPort = local.keycloak_management_port, protocol = "tcp" },
    ]
    environment = [
      # The server certificate is verified against the RDS certificate authority shipped in the image.
      { name = "KC_DB_URL", value = "jdbc:postgresql://${local.database_host}:5432/keycloak?sslmode=verify-full&sslrootcert=/opt/keycloak/certs/rds-ca.pem" },
      { name = "KC_DB_USERNAME", value = "keycloak" },
      { name = "KC_HOSTNAME", value = "https://${local.identity_host_name}" },
      # TLS ends at the load balancer; Keycloak trusts only its forwarded headers.
      { name = "KC_HTTP_ENABLED", value = "true" },
      { name = "KC_HTTP_PORT", value = tostring(local.http_port) },
      { name = "KC_PROXY_HEADERS", value = "xforwarded" },
      { name = "KC_BOOTSTRAP_ADMIN_USERNAME", value = "tenantflow-bootstrap-admin" },
    ]
    secrets = [
      { name = "KC_DB_PASSWORD", valueFrom = "${aws_secretsmanager_secret.keycloak.arn}:database_password::" },
      { name = "KC_BOOTSTRAP_ADMIN_PASSWORD", valueFrom = "${aws_secretsmanager_secret.keycloak.arn}:admin_password::" },
    ]
    # No container health check: the load balancer checks /health/ready on the management port, and ECS replaces a
    # task that fails it.
    logConfiguration = local.log_configuration["keycloak"]
  }])
}

resource "aws_ecs_service" "keycloak" {
  name            = "keycloak"
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.keycloak.arn
  # One instance: several need Keycloak's cluster discovery configured, which is not built yet (open question 9).
  desired_count  = var.services_running ? var.keycloak.desired_count : 0
  launch_type    = "FARGATE"
  propagate_tags = "SERVICE"

  # The first start creates Keycloak's tables and imports both realms; it took over two minutes locally.
  health_check_grace_period_seconds = 300

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  network_configuration {
    subnets          = aws_subnet.public[*].id
    security_groups  = [aws_security_group.identity.id]
    assign_public_ip = true
  }

  load_balancer {
    target_group_arn = aws_lb_target_group.keycloak.arn
    container_name   = "keycloak"
    container_port   = local.http_port
  }

  depends_on = [aws_lb_listener.https]
}
