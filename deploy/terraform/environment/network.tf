# Two public subnets hold the load balancer and the tasks; two private subnets hold the database and the broker,
# which have no route to the internet. Tasks get public addresses instead of a NAT gateway (about $32 a month per
# environment, a third of the budget); their security group admits traffic from the load balancer only, and they
# reach only HTTPS, PostgreSQL and AMQPS (ADR-0032, SPEC-0012 open question 5).

resource "aws_vpc" "main" {
  cidr_block           = var.vpc_cidr
  enable_dns_support   = true
  enable_dns_hostnames = true

  tags = { Name = local.name }
}

# The default security group allows all traffic between its members; nothing may use it.
resource "aws_default_security_group" "main" {
  vpc_id = aws_vpc.main.id
}

resource "aws_internet_gateway" "main" {
  vpc_id = aws_vpc.main.id

  tags = { Name = local.name }
}

resource "aws_subnet" "public" {
  count = length(local.zones)

  vpc_id            = aws_vpc.main.id
  availability_zone = local.zones[count.index]
  cidr_block        = cidrsubnet(var.vpc_cidr, 4, count.index)

  tags = { Name = "${local.name}-public-${local.zones[count.index]}" }
}

resource "aws_subnet" "private" {
  count = length(local.zones)

  vpc_id            = aws_vpc.main.id
  availability_zone = local.zones[count.index]
  cidr_block        = cidrsubnet(var.vpc_cidr, 4, count.index + 8)

  tags = { Name = "${local.name}-private-${local.zones[count.index]}" }
}

resource "aws_route_table" "public" {
  vpc_id = aws_vpc.main.id

  route {
    cidr_block = "0.0.0.0/0"
    gateway_id = aws_internet_gateway.main.id
  }

  tags = { Name = "${local.name}-public" }
}

resource "aws_route_table_association" "public" {
  count = length(aws_subnet.public)

  subnet_id      = aws_subnet.public[count.index].id
  route_table_id = aws_route_table.public.id
}

# Private subnets keep the VPC's main route table, which only routes inside the VPC.

resource "aws_flow_log" "main" {
  vpc_id               = aws_vpc.main.id
  traffic_type         = "REJECT"
  log_destination_type = "cloud-watch-logs"
  log_destination      = aws_cloudwatch_log_group.flow.arn
  iam_role_arn         = aws_iam_role.flow_log.arn
}

# --- Security groups --------------------------------------------------------------------------------------------
# Rules are separate resources so each one reads as a single allowed path.

resource "aws_security_group" "load_balancer" {
  name        = "${local.name}-load-balancer"
  description = "Public HTTPS entry point"
  vpc_id      = aws_vpc.main.id
}

resource "aws_security_group" "api" {
  name        = "${local.name}-api"
  description = "API tasks"
  vpc_id      = aws_vpc.main.id
}

resource "aws_security_group" "worker" {
  name        = "${local.name}-worker"
  description = "Worker and Migrator tasks; nothing connects to them"
  vpc_id      = aws_vpc.main.id
}

# The operator's provisioning task: the only group that reaches the broker's management API.
resource "aws_security_group" "provisioning" {
  name        = "${local.name}-provisioning"
  description = "Provisioning task; nothing connects to it"
  vpc_id      = aws_vpc.main.id
}

resource "aws_security_group" "identity" {
  name        = "${local.name}-identity"
  description = "Keycloak tasks"
  vpc_id      = aws_vpc.main.id
}

resource "aws_security_group" "database" {
  name        = "${local.name}-database"
  description = "PostgreSQL"
  vpc_id      = aws_vpc.main.id
}

resource "aws_security_group" "broker" {
  name        = "${local.name}-broker"
  description = "RabbitMQ"
  vpc_id      = aws_vpc.main.id
}

resource "aws_vpc_security_group_ingress_rule" "load_balancer_https" {
  security_group_id = aws_security_group.load_balancer.id
  description       = "HTTPS from the internet"
  cidr_ipv4         = "0.0.0.0/0"
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
}

resource "aws_vpc_security_group_ingress_rule" "load_balancer_http" {
  security_group_id = aws_security_group.load_balancer.id
  description       = "HTTP from the internet, answered only with a redirect to HTTPS"
  cidr_ipv4         = "0.0.0.0/0"
  ip_protocol       = "tcp"
  from_port         = 80
  to_port           = 80
}

resource "aws_vpc_security_group_egress_rule" "load_balancer_to_api" {
  security_group_id            = aws_security_group.load_balancer.id
  referenced_security_group_id = aws_security_group.api.id
  ip_protocol                  = "tcp"
  from_port                    = local.http_port
  to_port                      = local.http_port
}

resource "aws_vpc_security_group_egress_rule" "load_balancer_to_identity" {
  for_each = toset([tostring(local.http_port), tostring(local.keycloak_management_port)])

  security_group_id            = aws_security_group.load_balancer.id
  referenced_security_group_id = aws_security_group.identity.id
  ip_protocol                  = "tcp"
  from_port                    = tonumber(each.key)
  to_port                      = tonumber(each.key)
}

resource "aws_vpc_security_group_ingress_rule" "api_from_load_balancer" {
  security_group_id            = aws_security_group.api.id
  referenced_security_group_id = aws_security_group.load_balancer.id
  ip_protocol                  = "tcp"
  from_port                    = local.http_port
  to_port                      = local.http_port
}

resource "aws_vpc_security_group_ingress_rule" "identity_from_load_balancer" {
  for_each = toset([tostring(local.http_port), tostring(local.keycloak_management_port)])

  security_group_id            = aws_security_group.identity.id
  referenced_security_group_id = aws_security_group.load_balancer.id
  ip_protocol                  = "tcp"
  from_port                    = tonumber(each.key)
  to_port                      = tonumber(each.key)
}

locals {
  # Which task groups reach which data store.
  database_clients = {
    api          = aws_security_group.api.id
    worker       = aws_security_group.worker.id
    identity     = aws_security_group.identity.id
    provisioning = aws_security_group.provisioning.id
  }
  # Only the Worker connects to RabbitMQ; the API publishes through the outbox (ADR-0021).
  broker_clients = {
    worker = aws_security_group.worker.id
  }
  task_groups = {
    api          = aws_security_group.api.id
    worker       = aws_security_group.worker.id
    identity     = aws_security_group.identity.id
    provisioning = aws_security_group.provisioning.id
  }
}

# HTTPS out: image pulls, Secrets Manager, CloudWatch Logs, the OTLP backend, Stripe, and the API's OIDC metadata
# fetch from Keycloak through the public endpoint.
resource "aws_vpc_security_group_egress_rule" "task_https" {
  for_each = local.task_groups

  security_group_id = each.value
  description       = "HTTPS to AWS APIs and providers"
  cidr_ipv4         = "0.0.0.0/0"
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
}

resource "aws_vpc_security_group_egress_rule" "to_database" {
  for_each = local.database_clients

  security_group_id            = each.value
  referenced_security_group_id = aws_security_group.database.id
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
}

resource "aws_vpc_security_group_ingress_rule" "database_from" {
  for_each = local.database_clients

  security_group_id            = aws_security_group.database.id
  referenced_security_group_id = each.value
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
}

resource "aws_vpc_security_group_egress_rule" "to_broker" {
  for_each = local.broker_clients

  security_group_id            = each.value
  referenced_security_group_id = aws_security_group.broker.id
  ip_protocol                  = "tcp"
  from_port                    = local.amqps_port
  to_port                      = local.amqps_port
}

resource "aws_vpc_security_group_ingress_rule" "broker_from" {
  for_each = local.broker_clients

  security_group_id            = aws_security_group.broker.id
  referenced_security_group_id = each.value
  ip_protocol                  = "tcp"
  from_port                    = local.amqps_port
  to_port                      = local.amqps_port
}

# The management API (HTTPS on 443), for creating the application user (the provisioning task, provisioning.tf).
resource "aws_vpc_security_group_ingress_rule" "broker_management_from_provisioning" {
  security_group_id            = aws_security_group.broker.id
  referenced_security_group_id = aws_security_group.provisioning.id
  ip_protocol                  = "tcp"
  from_port                    = 443
  to_port                      = 443
}
