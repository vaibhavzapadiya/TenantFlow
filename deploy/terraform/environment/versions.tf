# One root module for every deployed environment (ADR-0034): dev, staging and prod run the same code, and differ
# only in envs/<name>.tfvars (sizes, retention, protection) and envs/<name>.s3.tfbackend (the state key).

terraform {
  required_version = ">= 1.11.0, < 2.0.0"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.7"
    }
  }

  # Partial configuration: terraform init -backend-config=envs/<name>.s3.tfbackend plus the bucket and key from the
  # bootstrap outputs (deploy/terraform/README.md).
  backend "s3" {}
}

provider "aws" {
  region = var.aws_region

  # Refuses to run against an account other than the one the environment was planned for.
  allowed_account_ids = [var.aws_account_id]

  default_tags {
    tags = {
      project     = "tenantflow"
      environment = var.environment
      managed-by  = "terraform"
    }
  }
}

data "aws_caller_identity" "current" {}

data "aws_availability_zones" "available" {
  state = "available"
}

locals {
  name       = "tenantflow-${var.environment}"
  account_id = data.aws_caller_identity.current.account_id
  # Two zones: the load balancer and the database subnet group both require at least two.
  zones = slice(data.aws_availability_zones.available.names, 0, 2)
}
