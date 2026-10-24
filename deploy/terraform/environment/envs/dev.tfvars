# Shared integration environment; disposable: no deletion protection, one day of backups.
# Sizes are provisional until the M11 baseline (roadmap sequencing note 5).
#
# Not here on purpose, because they are still open or are set per run (deploy/terraform/README.md):
# aws_account_id, github_repository, dns_zone_name, identity_admin_cidrs, image_tag, keycloak_image_tag.

environment = "dev"
aws_region  = "ap-south-1"
vpc_cidr    = "10.20.0.0/16"

api = {
  cpu           = 256
  memory        = 512
  desired_count = 1
}

worker = {
  cpu           = 256
  memory        = 512
  desired_count = 1
}

keycloak = {
  cpu           = 512
  memory        = 1024
  desired_count = 1
}

database = {
  engine_version        = "17"
  instance_class        = "db.t4g.micro"
  allocated_storage     = 20
  max_allocated_storage = 50
  multi_az              = false
  backup_retention_days = 1
  deletion_protection   = false
}

# Verify the newest RabbitMQ version Amazon MQ offers on mq.t3.micro before the first apply; Compose runs 4.3.
broker = {
  engine_version  = "3.13"
  instance_type   = "mq.t3.micro"
  deployment_mode = "SINGLE_INSTANCE"
}

load_balancer_deletion_protection = false

# Applied on demand and destroyed when idle (ADR-0034 decision 5): secret names must be reusable at once.
secret_recovery_window_days = 0
