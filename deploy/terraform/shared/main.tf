# Account-wide resources shared by every environment (ADR-0033): the image registry, the GitHub OIDC provider with
# the role that pushes images, and the monthly budget alarm (ADR-0034). Images are built once and promoted, so the
# registry belongs to no single environment.

terraform {
  required_version = ">= 1.11.0, < 2.0.0"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }

  # Partial configuration: bucket and kms_key_id come from the bootstrap outputs (deploy/terraform/README.md).
  backend "s3" {
    key          = "shared/terraform.tfstate"
    region       = "ap-south-1"
    encrypt      = true
    use_lockfile = true
  }
}

provider "aws" {
  region = var.aws_region

  default_tags {
    tags = {
      project    = "tenantflow"
      stack      = "shared"
      managed-by = "terraform"
    }
  }
}

resource "aws_kms_key" "registry" {
  description             = "TenantFlow container images"
  enable_key_rotation     = true
  deletion_window_in_days = 30
}

# The environments find the key by this alias to let their tasks pull encrypted images.
resource "aws_kms_alias" "registry" {
  name          = "alias/tenantflow-registry"
  target_key_id = aws_kms_key.registry.key_id
}

resource "aws_ecr_repository" "image" {
  for_each = toset(local.images)

  name = "tenantflow/${each.key}"

  # A tag names exactly one build, so the image promoted to production is the image tested in staging.
  image_tag_mutability = "IMMUTABLE"

  encryption_configuration {
    encryption_type = "KMS"
    kms_key         = aws_kms_key.registry.arn
  }

  image_scanning_configuration {
    scan_on_push = true
  }
}

resource "aws_ecr_lifecycle_policy" "image" {
  for_each   = aws_ecr_repository.image
  repository = each.value.name

  policy = jsonencode({
    rules = [{
      rulePriority = 1
      description  = "Keep the newest builds; older ones cannot be rolled back to anyway once their schema is gone."
      selection = {
        tagStatus   = "any"
        countType   = "imageCountMoreThan"
        countNumber = var.images_to_keep
      }
      action = { type = "expire" }
    }]
  })
}

# GitHub Actions federation (SPEC-0012 criterion 9): workflows exchange a short-lived OIDC token for a role session,
# so no AWS access key exists anywhere. The deploy roles live in each environment stack.
resource "aws_iam_openid_connect_provider" "github" {
  url            = "https://token.actions.githubusercontent.com"
  client_id_list = ["sts.amazonaws.com"]
}

data "aws_iam_policy_document" "image_push_trust" {
  statement {
    actions = ["sts:AssumeRoleWithWebIdentity"]

    principals {
      type        = "Federated"
      identifiers = [aws_iam_openid_connect_provider.github.arn]
    }

    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:aud"
      values   = ["sts.amazonaws.com"]
    }

    # Only workflow runs on main build release images; pull requests from forks never get this role.
    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:sub"
      values   = ["repo:${var.github_repository}:ref:refs/heads/main"]
    }
  }
}

resource "aws_iam_role" "image_push" {
  name                 = "tenantflow-image-push"
  assume_role_policy   = data.aws_iam_policy_document.image_push_trust.json
  max_session_duration = 3600
}

data "aws_iam_policy_document" "image_push" {
  statement {
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"]
  }

  statement {
    actions = [
      "ecr:BatchCheckLayerAvailability",
      "ecr:BatchGetImage",
      "ecr:CompleteLayerUpload",
      "ecr:DescribeImages",
      "ecr:InitiateLayerUpload",
      "ecr:PutImage",
      "ecr:UploadLayerPart",
    ]
    resources = [for repository in aws_ecr_repository.image : repository.arn]
  }

  statement {
    actions   = ["kms:Decrypt", "kms:GenerateDataKey"]
    resources = [aws_kms_key.registry.arn]
  }
}

resource "aws_iam_role_policy" "image_push" {
  name   = "push-tenantflow-images"
  role   = aws_iam_role.image_push.id
  policy = data.aws_iam_policy_document.image_push.json
}

# The zone every environment's names live in (SPEC-0012 open question 3). The parent domain stays with its current
# DNS provider; delegate this subdomain there once, with the name_servers output as NS records.
resource "aws_route53_zone" "main" {
  name    = var.dns_zone_name
  comment = "TenantFlow environments"
}

# Only Amazon's certificate authority may issue certificates for these names.
resource "aws_route53_record" "caa" {
  zone_id = aws_route53_zone.main.zone_id
  name    = var.dns_zone_name
  type    = "CAA"
  ttl     = 3600
  records = ["0 issue \"amazon.com\"", "0 issuewild \";\""]
}

# The budget covers the whole account, so it alarms on everything the three environments spend together.
resource "aws_budgets_budget" "monthly" {
  name         = "tenantflow-monthly"
  budget_type  = "COST"
  limit_amount = tostring(var.monthly_budget_usd)
  limit_unit   = "USD"
  time_unit    = "MONTHLY"

  notification {
    comparison_operator        = "GREATER_THAN"
    threshold                  = 80
    threshold_type             = "PERCENTAGE"
    notification_type          = "ACTUAL"
    subscriber_email_addresses = var.budget_alert_emails
  }

  notification {
    comparison_operator        = "GREATER_THAN"
    threshold                  = 100
    threshold_type             = "PERCENTAGE"
    notification_type          = "FORECASTED"
    subscriber_email_addresses = var.budget_alert_emails
  }
}
