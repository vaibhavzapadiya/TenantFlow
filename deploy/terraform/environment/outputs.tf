output "host_names" {
  description = "Public names of the API and Keycloak; Route 53 points both at the load balancer (dns.tf)."
  value       = { api = local.api_host_name, identity = local.identity_host_name }
}

output "cluster_name" {
  description = "ECS cluster the pipeline deploys to."
  value       = aws_ecs_cluster.main.name
}

output "deploy_role_arn" {
  description = "Role the GitHub environment of the same name assumes to deploy."
  value       = aws_iam_role.deploy.arn
}

output "migrator_task" {
  description = "What the pipeline needs to run the Migrator before a rollout."
  value = {
    task_definition = aws_ecs_task_definition.migrator.family
    subnets         = aws_subnet.public[*].id
    security_group  = aws_security_group.worker.id
  }
}

output "provisioning_task" {
  description = "Run by the operator before the first Migrator run and after every password rotation (deploy/terraform/README.md)."
  value = {
    task_definition = aws_ecs_task_definition.provisioning.family
    subnets         = aws_subnet.public[*].id
    security_group  = aws_security_group.provisioning.id
  }
}

output "files_bucket" {
  description = "Bucket for imported and exported files (the S3 storage adapter, IE-A18)."
  value       = aws_s3_bucket.files.bucket
}

output "operator_secrets" {
  description = "Secrets whose values the operator sets once, outside Terraform (deploy/terraform/README.md)."
  value = {
    application        = aws_secretsmanager_secret.application.name
    broker_application = aws_secretsmanager_secret.broker_application.name
  }
}
