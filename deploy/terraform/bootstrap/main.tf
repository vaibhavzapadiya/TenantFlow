# Remote state for every other TenantFlow stack (ADR-0033): one S3 bucket per AWS account, encrypted with its own
# KMS key, versioned, private, TLS only. Locking uses S3 conditional writes (use_lockfile), so there is no DynamoDB table.
#
# This stack is the only one that starts with local state: it creates the bucket the others store their state in.
# After the first apply, move its own state into the bucket (deploy/terraform/README.md, "Bootstrap").

terraform {
  required_version = ">= 1.11.0, < 2.0.0"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
}

provider "aws" {
  region = var.aws_region

  default_tags {
    tags = {
      project    = "tenantflow"
      stack      = "bootstrap"
      managed-by = "terraform"
    }
  }
}

variable "aws_region" {
  description = "Region of the state bucket; the other stacks use the same region (SPEC-0012 open question 2)."
  type        = string
}

data "aws_caller_identity" "current" {}

locals {
  # Bucket names are global; the account id and region make this one unique without a random suffix.
  bucket_name = "tenantflow-tfstate-${data.aws_caller_identity.current.account_id}-${var.aws_region}"
}

resource "aws_kms_key" "state" {
  description             = "TenantFlow Terraform state"
  enable_key_rotation     = true
  deletion_window_in_days = 30
}

resource "aws_kms_alias" "state" {
  name          = "alias/tenantflow-tfstate"
  target_key_id = aws_kms_key.state.key_id
}

resource "aws_s3_bucket" "state" {
  bucket = local.bucket_name

  # State is the only record of what exists; losing the bucket orphans every resource.
  lifecycle {
    prevent_destroy = true
  }
}

resource "aws_s3_bucket_ownership_controls" "state" {
  bucket = aws_s3_bucket.state.id

  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}

resource "aws_s3_bucket_public_access_block" "state" {
  bucket                  = aws_s3_bucket.state.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_versioning" "state" {
  bucket = aws_s3_bucket.state.id

  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "state" {
  bucket = aws_s3_bucket.state.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm     = "aws:kms"
      kms_master_key_id = aws_kms_key.state.arn
    }
    bucket_key_enabled = true
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "state" {
  bucket = aws_s3_bucket.state.id

  rule {
    id     = "expire-old-state-versions"
    status = "Enabled"

    filter {}

    # Old versions are the way back from a bad apply; 90 days covers a milestone with room to spare.
    noncurrent_version_expiration {
      noncurrent_days = 90
    }

    abort_incomplete_multipart_upload {
      days_after_initiation = 1
    }
  }
}

data "aws_iam_policy_document" "state" {
  statement {
    sid       = "DenyInsecureTransport"
    effect    = "Deny"
    actions   = ["s3:*"]
    resources = [aws_s3_bucket.state.arn, "${aws_s3_bucket.state.arn}/*"]

    principals {
      type        = "*"
      identifiers = ["*"]
    }

    condition {
      test     = "Bool"
      variable = "aws:SecureTransport"
      values   = ["false"]
    }
  }
}

resource "aws_s3_bucket_policy" "state" {
  bucket = aws_s3_bucket.state.id
  policy = data.aws_iam_policy_document.state.json

  depends_on = [aws_s3_bucket_public_access_block.state]
}

output "state_bucket" {
  description = "Pass as -backend-config=\"bucket=...\" when initialising the shared and environment stacks."
  value       = aws_s3_bucket.state.bucket
}

output "state_kms_key_arn" {
  description = "Pass as -backend-config=\"kms_key_id=...\" so state objects are encrypted with the state key."
  value       = aws_kms_key.state.arn
}
