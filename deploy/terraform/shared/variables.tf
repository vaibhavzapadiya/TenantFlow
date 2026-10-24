locals {
  # One repository per deployable process (blueprint section 18: the API and worker deploy separately).
  images = ["api", "worker", "migrator", "keycloak"]
}

variable "aws_region" {
  description = "Region of the registry and every environment (SPEC-0012 open question 2)."
  type        = string
}

variable "github_repository" {
  description = "GitHub repository allowed to push images, as \"owner/name\". There is no remote yet (SPEC-0012 open question 4)."
  type        = string

  validation {
    condition     = can(regex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", var.github_repository))
    error_message = "github_repository must look like \"owner/name\"."
  }
}

variable "dns_zone_name" {
  description = "Subdomain delegated to Route 53, for example tenantflow.<your-domain> (SPEC-0012 open question 3)."
  type        = string

  validation {
    condition     = can(regex("^([a-z0-9-]+\\.)+[a-z]{2,}$", var.dns_zone_name))
    error_message = "dns_zone_name must be a lower-case domain name without a trailing dot."
  }
}

variable "monthly_budget_usd" {
  description = "Monthly cost limit for the account (Open Decision 1, set by the owner on 2026-10-22)."
  type        = number
  default     = 100
}

variable "budget_alert_emails" {
  description = "Who receives budget alarms. Personal data: pass it through TF_VAR_budget_alert_emails, never a committed file."
  type        = list(string)
  sensitive   = true

  validation {
    condition     = length(var.budget_alert_emails) > 0
    error_message = "At least one address must receive budget alarms."
  }
}

variable "images_to_keep" {
  description = "Builds kept per repository."
  type        = number
  default     = 30
}
