# Values that differ between environments live in envs/<name>.tfvars. Sizes are provisional: M11 (workload model
# and baseline) runs after M12 by the owner's decision, so nothing here is measured yet (roadmap sequencing note 5).

variable "environment" {
  description = "Environment name; also the GitHub environment whose approvals gate a deployment."
  type        = string

  validation {
    condition     = contains(["dev", "staging", "prod"], var.environment)
    error_message = "environment must be dev, staging or prod (Open Decision 1, ADR-0034)."
  }
}

variable "aws_region" {
  description = "Region of the environment (SPEC-0012 open question 2)."
  type        = string
}

variable "aws_account_id" {
  description = "Account the environment belongs to; the provider refuses any other."
  type        = string

  validation {
    condition     = can(regex("^[0-9]{12}$", var.aws_account_id))
    error_message = "aws_account_id must be 12 digits."
  }
}

variable "github_repository" {
  description = "GitHub repository whose workflow may deploy here, as \"owner/name\" (SPEC-0012 open question 4)."
  type        = string
}

variable "vpc_cidr" {
  description = "Address range of the environment's VPC; ranges never overlap, so environments could be peered later."
  type        = string
}

# --- Public endpoints ---------------------------------------------------------------------------------------------

variable "dns_zone_name" {
  description = "Route 53 zone created by the shared stack, for example tenantflow.<your-domain> (SPEC-0012 open question 3). Host names derive from it (dns.tf)."
  type        = string
}

variable "identity_admin_cidrs" {
  description = "Networks allowed to reach the Keycloak admin console. Empty means nobody, from anywhere."
  type        = list(string)
  default     = []
}

# --- Application ----------------------------------------------------------------------------------------------------

variable "image_tag" {
  description = "Immutable tag of the build to run; the pipeline sets it, and the same tag moves dev → staging → prod."
  type        = string
}

variable "services_running" {
  description = <<-EOT
    False starts no API, Worker or Keycloak task, whatever the sizes say: for the first apply, before the provisioning
    task and the Migrator have created the database users and schema (deploy/terraform/README.md, steps 5 and 9).
  EOT
  type        = bool
  default     = true
}

variable "api" {
  description = "API task size and count."
  type = object({
    cpu           = number
    memory        = number
    desired_count = number
  })
}

variable "worker" {
  description = "Worker task size and count. Quartz runs clustered (ADR-0023), so more than one is safe."
  type = object({
    cpu           = number
    memory        = number
    desired_count = number
  })
}

variable "payment_provider" {
  description = "Payments:Provider. Only a Stripe test-mode key is ever stored outside production (PY-A15)."
  type        = string
  default     = "simulator"

  validation {
    condition     = contains(["simulator", "stripe"], var.payment_provider)
    error_message = "payment_provider must be simulator or stripe (the ProviderName constants)."
  }
}

variable "otlp_endpoint" {
  description = "OTLP endpoint for traces, metrics and logs. Null until the deployed telemetry backend is chosen (SPEC-0012 open question 7)."
  type        = string
  default     = null
}

variable "log_retention_days" {
  description = "Retention of container logs; seven days matches the local telemetry retention (SPEC-0010, DP-08)."
  type        = number
  default     = 7
}

# --- Identity provider ----------------------------------------------------------------------------------------------

variable "keycloak_image_tag" {
  description = <<-EOT
    Tag (the commit) of the Keycloak image the release pipeline built (deploy/keycloak/Dockerfile). Set per apply
    by the operator, so Keycloak restarts only when its image changes.
  EOT
  type        = string
}

variable "keycloak" {
  description = "Hosted Keycloak (ADR-0035): task size and count. The image comes from keycloak_image_tag."
  type = object({
    cpu           = number
    memory        = number
    desired_count = number
  })

  validation {
    condition     = var.keycloak.desired_count <= 1
    error_message = "Keycloak runs at most one task until its cluster discovery is configured (SPEC-0012 open question 9)."
  }
}

# --- Data -----------------------------------------------------------------------------------------------------------

variable "database" {
  description = "RDS for PostgreSQL. Backups give point-in-time recovery within the retention window (M13 drills it)."
  type = object({
    engine_version        = string
    instance_class        = string
    allocated_storage     = number
    max_allocated_storage = number
    multi_az              = bool
    backup_retention_days = number
    deletion_protection   = bool
  })
}

variable "broker" {
  description = "Amazon MQ for RabbitMQ (ADR-0032). Single instance outside production keeps the budget."
  type = object({
    engine_version  = string
    instance_type   = string
    deployment_mode = string
  })

  validation {
    condition     = contains(["SINGLE_INSTANCE", "CLUSTER_MULTI_AZ"], var.broker.deployment_mode)
    error_message = "broker.deployment_mode must be SINGLE_INSTANCE or CLUSTER_MULTI_AZ."
  }
}

variable "file_retention_days" {
  description = "Imported and exported files expire after this many days (ADR-0024: seven)."
  type        = number
  default     = 7
}

variable "load_balancer_deletion_protection" {
  description = "Protects the load balancer, and with it the public endpoint, from an accidental destroy."
  type        = bool
}
