# Host names, certificate and DNS records (SPEC-0012 open question 3, decided 2026-10-22). Every environment derives
# its names the same way, api.<env>.<zone> and id.<env>.<zone>, so an on-demand environment (dev, prod) comes up
# with working HTTPS and no manual DNS edits (ADR-0034 decision 5).

data "aws_route53_zone" "main" {
  name         = var.dns_zone_name
  private_zone = false
}

locals {
  api_host_name      = "api.${var.environment}.${var.dns_zone_name}"
  identity_host_name = "id.${var.environment}.${var.dns_zone_name}"
}

resource "aws_acm_certificate" "main" {
  domain_name               = local.api_host_name
  subject_alternative_names = [local.identity_host_name]
  validation_method         = "DNS"

  # A replacement certificate is issued before the old one is removed, so the listener is never without one.
  lifecycle {
    create_before_destroy = true
  }
}

resource "aws_route53_record" "certificate_validation" {
  for_each = {
    for option in aws_acm_certificate.main.domain_validation_options : option.domain_name => {
      name   = option.resource_record_name
      type   = option.resource_record_type
      record = option.resource_record_value
    }
  }

  zone_id         = data.aws_route53_zone.main.zone_id
  name            = each.value.name
  type            = each.value.type
  records         = [each.value.record]
  ttl             = 300
  allow_overwrite = true
}

resource "aws_acm_certificate_validation" "main" {
  certificate_arn         = aws_acm_certificate.main.arn
  validation_record_fqdns = [for record in aws_route53_record.certificate_validation : record.fqdn]
}

resource "aws_route53_record" "host" {
  for_each = toset([local.api_host_name, local.identity_host_name])

  zone_id = data.aws_route53_zone.main.zone_id
  name    = each.key
  type    = "A"

  alias {
    name                   = aws_lb.main.dns_name
    zone_id                = aws_lb.main.zone_id
    evaluate_target_health = true
  }
}
