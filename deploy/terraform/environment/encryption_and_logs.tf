# One customer-managed key per environment encrypts its database, files, secrets and logs, so an environment's data
# is readable only by principals that environment's IAM grants (a staging role cannot decrypt production data).

data "aws_iam_policy_document" "environment_key" {
  # Key administration and use are delegated to IAM policies in this account.
  statement {
    sid       = "AccountIam"
    actions   = ["kms:*"]
    resources = ["*"]

    principals {
      type        = "AWS"
      identifiers = ["arn:aws:iam::${local.account_id}:root"]
    }
  }

  statement {
    sid = "CloudWatchLogs"
    actions = [
      "kms:Decrypt",
      "kms:DescribeKey",
      "kms:Encrypt",
      "kms:GenerateDataKey*",
      "kms:ReEncrypt*",
    ]
    resources = ["*"]

    principals {
      type        = "Service"
      identifiers = ["logs.${var.aws_region}.amazonaws.com"]
    }

    condition {
      test     = "ArnLike"
      variable = "kms:EncryptionContext:aws:logs:arn"
      values   = ["arn:aws:logs:${var.aws_region}:${local.account_id}:log-group:/tenantflow/${var.environment}/*"]
    }
  }

  # CloudWatch alarms publish to the encrypted alerts topic (monitoring.tf), which needs a data key for each message.
  statement {
    sid       = "CloudWatchAlarms"
    actions   = ["kms:Decrypt", "kms:GenerateDataKey*"]
    resources = ["*"]

    principals {
      type        = "Service"
      identifiers = ["cloudwatch.amazonaws.com"]
    }

    condition {
      test     = "StringEquals"
      variable = "aws:SourceAccount"
      values   = [local.account_id]
    }
  }
}

resource "aws_kms_key" "environment" {
  description             = "TenantFlow ${var.environment} data"
  enable_key_rotation     = true
  deletion_window_in_days = 30
  policy                  = data.aws_iam_policy_document.environment_key.json
}

resource "aws_kms_alias" "environment" {
  name          = "alias/${local.name}"
  target_key_id = aws_kms_key.environment.key_id
}

resource "aws_cloudwatch_log_group" "service" {
  for_each = toset(["api", "worker", "migrator", "keycloak", "provisioning"])

  name              = "/tenantflow/${var.environment}/${each.key}"
  retention_in_days = var.log_retention_days
  kms_key_id        = aws_kms_key.environment.arn
}

resource "aws_cloudwatch_log_group" "flow" {
  name              = "/tenantflow/${var.environment}/vpc-flow-rejects"
  retention_in_days = var.log_retention_days
  kms_key_id        = aws_kms_key.environment.arn
}

data "aws_iam_policy_document" "flow_log_trust" {
  statement {
    actions = ["sts:AssumeRole"]

    principals {
      type        = "Service"
      identifiers = ["vpc-flow-logs.amazonaws.com"]
    }

    condition {
      test     = "StringEquals"
      variable = "aws:SourceAccount"
      values   = [local.account_id]
    }
  }
}

resource "aws_iam_role" "flow_log" {
  name               = "${local.name}-flow-log"
  assume_role_policy = data.aws_iam_policy_document.flow_log_trust.json
}

data "aws_iam_policy_document" "flow_log" {
  statement {
    actions   = ["logs:CreateLogStream", "logs:PutLogEvents", "logs:DescribeLogStreams"]
    resources = ["${aws_cloudwatch_log_group.flow.arn}:*"]
  }
}

resource "aws_iam_role_policy" "flow_log" {
  name   = "write-flow-logs"
  role   = aws_iam_role.flow_log.id
  policy = data.aws_iam_policy_document.flow_log.json
}
