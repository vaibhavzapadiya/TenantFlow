# Least-privilege roles (ADR-0033). The execution role is what ECS uses to start a task (pull the image, read its
# secrets, write logs); each task role is what the running code may do. Only the API and Worker touch the bucket.

data "aws_ecr_repository" "image" {
  for_each = toset(["api", "worker", "migrator", "keycloak"])
  name     = "tenantflow/${each.key}"
}

data "aws_kms_alias" "registry" {
  name = "alias/tenantflow-registry"
}

data "aws_iam_policy_document" "ecs_tasks_trust" {
  statement {
    actions = ["sts:AssumeRole"]

    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }

    condition {
      test     = "ArnLike"
      variable = "aws:SourceArn"
      values   = ["arn:aws:ecs:${var.aws_region}:${local.account_id}:*"]
    }
  }
}

resource "aws_iam_role" "execution" {
  name               = "${local.name}-task-execution"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_trust.json
}

data "aws_iam_policy_document" "execution" {
  statement {
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"]
  }

  statement {
    actions   = ["ecr:BatchCheckLayerAvailability", "ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer"]
    resources = [for repository in data.aws_ecr_repository.image : repository.arn]
  }

  statement {
    actions   = ["logs:CreateLogStream", "logs:PutLogEvents"]
    resources = [for group in aws_cloudwatch_log_group.service : "${group.arn}:*"]
  }

  statement {
    actions = ["secretsmanager:GetSecretValue"]
    resources = [
      aws_secretsmanager_secret.database_owner.arn,
      aws_secretsmanager_secret.database_app.arn,
      aws_secretsmanager_secret.keycloak.arn,
      aws_secretsmanager_secret.broker_application.arn,
      aws_secretsmanager_secret.application.arn,
    ]
  }

  statement {
    actions   = ["kms:Decrypt"]
    resources = [aws_kms_key.environment.arn, data.aws_kms_alias.registry.target_key_arn]
  }
}

resource "aws_iam_role_policy" "execution" {
  name   = "start-tenantflow-tasks"
  role   = aws_iam_role.execution.id
  policy = data.aws_iam_policy_document.execution.json
}

# --- Task roles -----------------------------------------------------------------------------------------------------

resource "aws_iam_role" "task" {
  for_each = toset(["api", "worker", "migrator", "keycloak"])

  name               = "${local.name}-${each.key}"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_trust.json
}

data "aws_iam_policy_document" "files_access" {
  statement {
    actions   = ["s3:ListBucket"]
    resources = [aws_s3_bucket.files.arn]
  }

  statement {
    actions   = ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"]
    resources = ["${aws_s3_bucket.files.arn}/*"]
  }

  statement {
    actions   = ["kms:Decrypt", "kms:GenerateDataKey"]
    resources = [aws_kms_key.environment.arn]
  }
}

resource "aws_iam_role_policy" "files" {
  for_each = toset(["api", "worker"])

  name   = "read-write-files"
  role   = aws_iam_role.task[each.key].id
  policy = data.aws_iam_policy_document.files_access.json
}

# --- Deployment from GitHub Actions (SPEC-0012 criterion 9) ---------------------------------------------------------
# Trusted only for workflow jobs that run in the GitHub environment of the same name, whose protection rules
# (required reviewers for prod) gate the deployment. The role runs the Migrator and rolls services to a new
# task definition; it cannot change infrastructure, which stays a reviewed terraform apply by the operator.

data "aws_iam_openid_connect_provider" "github" {
  url = "https://token.actions.githubusercontent.com"
}

data "aws_iam_policy_document" "deploy_trust" {
  statement {
    actions = ["sts:AssumeRoleWithWebIdentity"]

    principals {
      type        = "Federated"
      identifiers = [data.aws_iam_openid_connect_provider.github.arn]
    }

    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:aud"
      values   = ["sts.amazonaws.com"]
    }

    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:sub"
      values   = ["repo:${var.github_repository}:environment:${var.environment}"]
    }
  }
}

resource "aws_iam_role" "deploy" {
  name                 = "${local.name}-deploy"
  assume_role_policy   = data.aws_iam_policy_document.deploy_trust.json
  max_session_duration = 3600
}

data "aws_iam_policy_document" "deploy" {
  statement {
    actions   = ["ecs:DescribeTaskDefinition", "ecs:RegisterTaskDefinition"]
    resources = ["*"]
  }

  statement {
    actions = ["ecs:UpdateService", "ecs:DescribeServices"]
    resources = [
      aws_ecs_service.api.id,
      aws_ecs_service.worker.id,
    ]
  }

  statement {
    actions   = ["ecs:RunTask", "ecs:DescribeTasks"]
    resources = ["*"]

    condition {
      test     = "ArnEquals"
      variable = "ecs:cluster"
      values   = [aws_ecs_cluster.main.arn]
    }
  }

  statement {
    actions = ["iam:PassRole"]
    resources = [
      aws_iam_role.execution.arn,
      aws_iam_role.task["api"].arn,
      aws_iam_role.task["worker"].arn,
      aws_iam_role.task["migrator"].arn,
    ]

    condition {
      test     = "StringEquals"
      variable = "iam:PassedToService"
      values   = ["ecs-tasks.amazonaws.com"]
    }
  }
}

resource "aws_iam_role_policy" "deploy" {
  name   = "deploy-tenantflow"
  role   = aws_iam_role.deploy.id
  policy = data.aws_iam_policy_document.deploy.json
}
