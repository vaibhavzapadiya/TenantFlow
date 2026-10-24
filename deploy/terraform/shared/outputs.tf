output "repository_urls" {
  description = "Registry URL per image; the pipeline pushes to these and the environments pull from them."
  value       = { for name, repository in aws_ecr_repository.image : name => repository.repository_url }
}

output "image_push_role_arn" {
  description = "Role the GitHub workflow on main assumes to push images."
  value       = aws_iam_role.image_push.arn
}

output "name_servers" {
  description = "Add these as NS records for dns_zone_name at the parent domain's DNS provider (once)."
  value       = aws_route53_zone.main.name_servers
}

output "github_oidc_provider_arn" {
  description = "Trusted by each environment's deploy role."
  value       = aws_iam_openid_connect_provider.github.arn
}
