# PostgreSQL, RabbitMQ and object storage. All three sit in private subnets or behind IAM, encrypted with the
# environment key, and reachable only from the task security groups that need them (network.tf).

resource "aws_db_subnet_group" "main" {
  name       = local.name
  subnet_ids = aws_subnet.private[*].id
}

resource "aws_db_parameter_group" "main" {
  name   = local.name
  family = "postgres${split(".", var.database.engine_version)[0]}"

  # Every connection is encrypted (the connection strings in secrets.tf ask for SSL as well).
  parameter {
    name  = "rds.force_ssl"
    value = "1"
  }

  parameter {
    name  = "log_min_duration_statement"
    value = "1000"
  }
}

resource "aws_db_instance" "main" {
  identifier     = local.name
  engine         = "postgres"
  engine_version = var.database.engine_version
  instance_class = var.database.instance_class

  db_name  = local.database_name
  username = "tenantflow_admin"
  # RDS creates and rotates the master password in Secrets Manager; it never enters Terraform state. Only the
  # provisioning task uses it, to create the owner and application roles (ADR-0012, SPEC-0012 criterion 5).
  manage_master_user_password   = true
  master_user_secret_kms_key_id = aws_kms_key.environment.arn

  allocated_storage     = var.database.allocated_storage
  max_allocated_storage = var.database.max_allocated_storage
  storage_type          = "gp3"
  storage_encrypted     = true
  kms_key_id            = aws_kms_key.environment.arn

  db_subnet_group_name   = aws_db_subnet_group.main.name
  vpc_security_group_ids = [aws_security_group.database.id]
  parameter_group_name   = aws_db_parameter_group.main.name
  publicly_accessible    = false
  multi_az               = var.database.multi_az

  backup_retention_period    = var.database.backup_retention_days
  backup_window              = "02:00-03:00"
  maintenance_window         = "sun:03:30-sun:04:30"
  copy_tags_to_snapshot      = true
  auto_minor_version_upgrade = true
  deletion_protection        = var.database.deletion_protection
  # A protected database keeps a final snapshot; an unprotected one (dev) is disposable by design. The name carries
  # the instance's creation time, so prod can be destroyed and re-created repeatedly (ADR-0034 decision 5).
  skip_final_snapshot       = !var.database.deletion_protection
  final_snapshot_identifier = var.database.deletion_protection ? "${local.name}-final-${formatdate("YYYYMMDDhhmm", plantimestamp())}" : null

  enabled_cloudwatch_logs_exports = ["postgresql"]

  lifecycle {
    # Keep the name chosen at creation; plantimestamp() would otherwise change it on every plan.
    ignore_changes = [final_snapshot_identifier]
  }
}

# Amazon MQ allows one user at creation; it is the operator account for the management console. The application's
# least-privilege user is created inside RabbitMQ by the provisioning step and its password stored in
# aws_secretsmanager_secret.broker_application (MW-A02, SPEC-0012 open question 6).
resource "random_password" "broker_operator" {
  length  = 32
  special = false
}

resource "aws_mq_broker" "main" {
  broker_name        = local.name
  engine_type        = "RabbitMQ"
  engine_version     = var.broker.engine_version
  host_instance_type = var.broker.instance_type
  deployment_mode    = var.broker.deployment_mode

  publicly_accessible        = false
  subnet_ids                 = var.broker.deployment_mode == "SINGLE_INSTANCE" ? [aws_subnet.private[0].id] : aws_subnet.private[*].id
  security_groups            = [aws_security_group.broker.id]
  auto_minor_version_upgrade = true

  user {
    username = "tenantflow-operator"
    password = random_password.broker_operator.result
  }

  logs {
    general = true
  }

  maintenance_window_start_time {
    day_of_week = "SUNDAY"
    time_of_day = "04:30"
    time_zone   = "UTC"
  }
}

# Imported and exported files (ADR-0024). Private, encrypted, TLS only, and expired after the retention period.
resource "aws_s3_bucket" "files" {
  bucket = "${local.name}-files-${local.account_id}"
  # A dev bucket may be destroyed with its files; staging and prod refuse while files remain.
  force_destroy = var.environment == "dev"
}

resource "aws_s3_bucket_ownership_controls" "files" {
  bucket = aws_s3_bucket.files.id

  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}

resource "aws_s3_bucket_public_access_block" "files" {
  bucket                  = aws_s3_bucket.files.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_server_side_encryption_configuration" "files" {
  bucket = aws_s3_bucket.files.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm     = "aws:kms"
      kms_master_key_id = aws_kms_key.environment.arn
    }
    bucket_key_enabled = true
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "files" {
  bucket = aws_s3_bucket.files.id

  rule {
    id     = "expire-files"
    status = "Enabled"

    filter {}

    # A backstop behind the application's own expiry job: nothing outlives the retention period even if it stops.
    expiration {
      days = var.file_retention_days
    }

    abort_incomplete_multipart_upload {
      days_after_initiation = 1
    }
  }
}

data "aws_iam_policy_document" "files" {
  statement {
    sid       = "DenyInsecureTransport"
    effect    = "Deny"
    actions   = ["s3:*"]
    resources = [aws_s3_bucket.files.arn, "${aws_s3_bucket.files.arn}/*"]

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

resource "aws_s3_bucket_policy" "files" {
  bucket = aws_s3_bucket.files.id
  policy = data.aws_iam_policy_document.files.json

  depends_on = [aws_s3_bucket_public_access_block.files]
}
