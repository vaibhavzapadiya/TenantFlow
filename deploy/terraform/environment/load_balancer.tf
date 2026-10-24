# One public load balancer per environment, HTTPS only, routing by host name: the API host to the API, the identity
# host to Keycloak. Anything else gets 404. The Keycloak admin console and master realm answer only to
# identity_admin_cidrs (PS-A17: operator access is narrowed before MFA is even asked).

resource "aws_lb" "main" {
  name               = local.name
  load_balancer_type = "application"
  internal           = false
  subnets            = aws_subnet.public[*].id
  security_groups    = [aws_security_group.load_balancer.id]

  drop_invalid_header_fields = true
  enable_deletion_protection = var.load_balancer_deletion_protection
}

resource "aws_lb_target_group" "api" {
  name                 = "${local.name}-api"
  vpc_id               = aws_vpc.main.id
  target_type          = "ip"
  protocol             = "HTTP"
  port                 = local.http_port
  deregistration_delay = 30

  # Readiness, not liveness: a task that cannot reach its database or broker gets no traffic (M1).
  health_check {
    path                = "/health/ready"
    matcher             = "200"
    interval            = 15
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }
}

resource "aws_lb_target_group" "keycloak" {
  name                 = "${local.name}-identity"
  vpc_id               = aws_vpc.main.id
  target_type          = "ip"
  protocol             = "HTTP"
  port                 = local.http_port
  deregistration_delay = 30

  health_check {
    path                = "/health/ready"
    port                = tostring(local.keycloak_management_port)
    matcher             = "200"
    interval            = 30
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }
}

resource "aws_lb_listener" "http" {
  load_balancer_arn = aws_lb.main.arn
  port              = 80
  protocol          = "HTTP"

  default_action {
    type = "redirect"

    redirect {
      protocol    = "HTTPS"
      port        = "443"
      status_code = "HTTP_301"
    }
  }
}

resource "aws_lb_listener" "https" {
  load_balancer_arn = aws_lb.main.arn
  port              = 443
  protocol          = "HTTPS"
  ssl_policy        = "ELBSecurityPolicy-TLS13-1-2-2021-06"
  certificate_arn   = aws_acm_certificate_validation.main.certificate_arn

  default_action {
    type = "fixed-response"

    fixed_response {
      content_type = "text/plain"
      message_body = "Not found"
      status_code  = "404"
    }
  }
}

resource "aws_lb_listener_rule" "api" {
  listener_arn = aws_lb_listener.https.arn
  priority     = 100

  condition {
    host_header {
      values = [local.api_host_name]
    }
  }

  action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.api.arn
  }
}

locals {
  keycloak_admin_paths = ["/admin", "/admin/*", "/realms/master", "/realms/master/*"]
}

resource "aws_lb_listener_rule" "identity_admin_allowed" {
  count = length(var.identity_admin_cidrs) > 0 ? 1 : 0

  listener_arn = aws_lb_listener.https.arn
  priority     = 10

  condition {
    host_header {
      values = [local.identity_host_name]
    }
  }

  condition {
    path_pattern {
      values = local.keycloak_admin_paths
    }
  }

  condition {
    source_ip {
      values = var.identity_admin_cidrs
    }
  }

  action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.keycloak.arn
  }
}

resource "aws_lb_listener_rule" "identity_admin_denied" {
  listener_arn = aws_lb_listener.https.arn
  priority     = 20

  condition {
    host_header {
      values = [local.identity_host_name]
    }
  }

  condition {
    path_pattern {
      values = local.keycloak_admin_paths
    }
  }

  action {
    type = "fixed-response"

    fixed_response {
      content_type = "text/plain"
      message_body = "Forbidden"
      status_code  = "403"
    }
  }
}

resource "aws_lb_listener_rule" "identity" {
  listener_arn = aws_lb_listener.https.arn
  priority     = 110

  condition {
    host_header {
      values = [local.identity_host_name]
    }
  }

  action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.keycloak.arn
  }
}
