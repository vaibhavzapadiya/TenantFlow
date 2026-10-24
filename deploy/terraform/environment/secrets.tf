# Secrets the tasks read at start-up (ADR-0033). Two kinds:
#
# - Generated here: database role and Keycloak passwords. They come from ephemeral resources and go into write-only
#   arguments, so they exist in Secrets Manager but never in plan files or state. Bump the version to rotate.
# - Set by the operator: provider keys and the audit key. Terraform creates the empty secret and its access policy;
#   the value is put with the AWS CLI (deploy/terraform/README.md) and never passes through Terraform at all.
#
# The broker operator password is the exception: Amazon MQ only takes it as a plain argument, so it is in state.
# State is encrypted with its own key and readable only by the operator (ADR-0033).

locals {
  database_name = "tenantflow"
  database_host = aws_db_instance.main.address
  # The server certificate is verified against the RDS certificate authority shipped in the images
  # (deploy/docker/Dockerfile), and only for this connection: the image's system trust store is untouched (question 8).
  connection_options = "Port=5432;Database=${local.database_name};SSL Mode=VerifyFull;Root Certificate=/app/certs/rds-ca.pem"
}

ephemeral "random_password" "database_owner" {
  length  = 40
  special = false
}

ephemeral "random_password" "database_app" {
  length  = 40
  special = false
}

ephemeral "random_password" "keycloak_database" {
  length  = 40
  special = false
}

ephemeral "random_password" "keycloak_admin" {
  length  = 40
  special = false
}

variable "secret_recovery_window_days" {
  description = "Days a deleted secret stays recoverable. 0 for on-demand environments, whose secret names must be free again at once (ADR-0034)."
  type        = number
  default     = 30
}

variable "generated_secret_version" {
  description = "Increase to generate and store new database and Keycloak passwords; the provisioning task then re-applies them."
  type        = number
  default     = 1
}

resource "aws_secretsmanager_secret" "database_owner" {
  name        = "tenantflow/${var.environment}/database-owner"
  description = "Migrator connection as tenantflow_owner (ConnectionStrings:TenantFlowOwner)."
  kms_key_id  = aws_kms_key.environment.arn

  recovery_window_in_days = var.secret_recovery_window_days
}

resource "aws_secretsmanager_secret_version" "database_owner" {
  secret_id = aws_secretsmanager_secret.database_owner.id
  secret_string_wo = jsonencode({
    password          = ephemeral.random_password.database_owner.result
    connection_string = "Host=${local.database_host};${local.connection_options};Username=tenantflow_owner;Password=${ephemeral.random_password.database_owner.result}"
  })
  secret_string_wo_version = var.generated_secret_version
}

resource "aws_secretsmanager_secret" "database_app" {
  name        = "tenantflow/${var.environment}/database-app"
  description = "API and Worker connection as tenantflow_app (ConnectionStrings:TenantFlow)."
  kms_key_id  = aws_kms_key.environment.arn

  recovery_window_in_days = var.secret_recovery_window_days
}

resource "aws_secretsmanager_secret_version" "database_app" {
  secret_id = aws_secretsmanager_secret.database_app.id
  secret_string_wo = jsonencode({
    password          = ephemeral.random_password.database_app.result
    connection_string = "Host=${local.database_host};${local.connection_options};Username=tenantflow_app;Password=${ephemeral.random_password.database_app.result}"
  })
  secret_string_wo_version = var.generated_secret_version
}

resource "aws_secretsmanager_secret" "keycloak" {
  name        = "tenantflow/${var.environment}/keycloak"
  description = "Keycloak database password and bootstrap administrator password."
  kms_key_id  = aws_kms_key.environment.arn

  recovery_window_in_days = var.secret_recovery_window_days
}

resource "aws_secretsmanager_secret_version" "keycloak" {
  secret_id = aws_secretsmanager_secret.keycloak.id
  secret_string_wo = jsonencode({
    database_password = ephemeral.random_password.keycloak_database.result
    admin_password    = ephemeral.random_password.keycloak_admin.result
  })
  secret_string_wo_version = var.generated_secret_version
}

resource "aws_secretsmanager_secret" "broker_operator" {
  name        = "tenantflow/${var.environment}/broker-operator"
  description = "RabbitMQ operator account: management console and provisioning only, never the application."
  kms_key_id  = aws_kms_key.environment.arn

  recovery_window_in_days = var.secret_recovery_window_days
}

resource "aws_secretsmanager_secret_version" "broker_operator" {
  secret_id = aws_secretsmanager_secret.broker_operator.id
  secret_string = jsonencode({
    username = "tenantflow-operator"
    password = random_password.broker_operator.result
  })
}

# Set by the operator. Keys: username, password (the least-privilege RabbitMQ user, MW-A02).
resource "aws_secretsmanager_secret" "broker_application" {
  name        = "tenantflow/${var.environment}/broker-application"
  description = "RabbitMQ application user for the API and Worker (MW-A02). Value set by the operator."
  kms_key_id  = aws_kms_key.environment.arn

  recovery_window_in_days = var.secret_recovery_window_days
}

# Set by the operator. Keys: audit_operator_reference_key (ADR-0026), stripe_secret_key, stripe_webhook_secret (PY-A17).
resource "aws_secretsmanager_secret" "application" {
  name        = "tenantflow/${var.environment}/application"
  description = "Audit key and payment provider secrets. Value set by the operator."
  kms_key_id  = aws_kms_key.environment.arn

  recovery_window_in_days = var.secret_recovery_window_days
}
