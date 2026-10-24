# Alerts for a deployed environment. The M10 Prometheus rules and Grafana dashboards (deploy/observability) run only in
# the local stack until the deployed telemetry backend is chosen (SPEC-0012 open question 7), so these CloudWatch alarms
# are what tells the operator that an environment is failing: errors at the edge, no healthy task behind a target group,
# a database running out of space or CPU, and dead-lettered messages. Each one emails the alert recipients through one
# encrypted topic. About $0.10 a month per alarm.

variable "alert_emails" {
  description = "Recipients of the environment's alarms. Personal data: pass it in the shell (TF_VAR_alert_emails='[\"...\"]'), never in a file."
  type        = list(string)
  default     = []
  sensitive   = true
}

locals {
  # The Worker's consumer queues (the Consumer constants in the modules' Application projects); each dead-letters to
  # <queue>.dlq (ADR-0021). DeployedAlarmTests keeps this list in step with the code.
  consumer_queues = ["audit.audit-events", "notifications.order-events", "orders.payment-events"]

  alarm_actions = [aws_sns_topic.alerts.arn]

  # Only while services run: with no task on purpose (services_running = false), an empty target group is expected.
  target_groups = var.services_running ? {
    api      = aws_lb_target_group.api.arn_suffix
    identity = aws_lb_target_group.keycloak.arn_suffix
  } : {}
}

resource "aws_sns_topic" "alerts" {
  name              = "${local.name}-alerts"
  kms_master_key_id = aws_kms_key.environment.arn
}

resource "aws_sns_topic_subscription" "alerts" {
  for_each = nonsensitive(toset(var.alert_emails))

  topic_arn = aws_sns_topic.alerts.arn
  protocol  = "email"
  endpoint  = each.value
}

# --- Edge -----------------------------------------------------------------------------------------------------------

resource "aws_cloudwatch_metric_alarm" "api_5xx" {
  alarm_name          = "${local.name}-api-5xx"
  alarm_description   = "The API answered more than 10 server errors in 5 minutes."
  namespace           = "AWS/ApplicationELB"
  metric_name         = "HTTPCode_Target_5XX_Count"
  dimensions          = { LoadBalancer = aws_lb.main.arn_suffix, TargetGroup = aws_lb_target_group.api.arn_suffix }
  statistic           = "Sum"
  period              = 300
  evaluation_periods  = 1
  threshold           = 10
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"
  alarm_actions       = local.alarm_actions
  ok_actions          = local.alarm_actions
}

resource "aws_cloudwatch_metric_alarm" "load_balancer_5xx" {
  alarm_name          = "${local.name}-load-balancer-5xx"
  alarm_description   = "The load balancer itself answered more than 10 server errors in 5 minutes (no target, or a target timed out)."
  namespace           = "AWS/ApplicationELB"
  metric_name         = "HTTPCode_ELB_5XX_Count"
  dimensions          = { LoadBalancer = aws_lb.main.arn_suffix }
  statistic           = "Sum"
  period              = 300
  evaluation_periods  = 1
  threshold           = 10
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"
  alarm_actions       = local.alarm_actions
  ok_actions          = local.alarm_actions
}

resource "aws_cloudwatch_metric_alarm" "no_healthy_task" {
  for_each = local.target_groups

  alarm_name          = "${local.name}-${each.key}-no-healthy-task"
  alarm_description   = "No ${each.key} task has passed its readiness check for 3 minutes."
  namespace           = "AWS/ApplicationELB"
  metric_name         = "HealthyHostCount"
  dimensions          = { LoadBalancer = aws_lb.main.arn_suffix, TargetGroup = each.value }
  statistic           = "Minimum"
  period              = 60
  evaluation_periods  = 3
  threshold           = 1
  comparison_operator = "LessThanThreshold"
  treat_missing_data  = "breaching"
  alarm_actions       = local.alarm_actions
  ok_actions          = local.alarm_actions
}

# --- Data -----------------------------------------------------------------------------------------------------------

resource "aws_cloudwatch_metric_alarm" "database_storage" {
  alarm_name          = "${local.name}-database-storage"
  alarm_description   = "The database has less than 2 GiB free; storage autoscaling may have reached max_allocated_storage."
  namespace           = "AWS/RDS"
  metric_name         = "FreeStorageSpace"
  dimensions          = { DBInstanceIdentifier = aws_db_instance.main.identifier }
  statistic           = "Minimum"
  period              = 300
  evaluation_periods  = 1
  threshold           = 2147483648
  comparison_operator = "LessThanThreshold"
  treat_missing_data  = "breaching"
  alarm_actions       = local.alarm_actions
  ok_actions          = local.alarm_actions
}

resource "aws_cloudwatch_metric_alarm" "database_cpu" {
  alarm_name          = "${local.name}-database-cpu"
  alarm_description   = "Database CPU above 80% for 15 minutes; the instance class is provisional until the M11 baseline."
  namespace           = "AWS/RDS"
  metric_name         = "CPUUtilization"
  dimensions          = { DBInstanceIdentifier = aws_db_instance.main.identifier }
  statistic           = "Average"
  period              = 300
  evaluation_periods  = 3
  threshold           = 80
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"
  alarm_actions       = local.alarm_actions
  ok_actions          = local.alarm_actions
}

resource "aws_cloudwatch_metric_alarm" "dead_letters" {
  for_each = toset(local.consumer_queues)

  alarm_name          = "${local.name}-dead-letters-${each.key}"
  alarm_description   = "Messages are waiting in ${each.key}.dlq; replay them after fixing the cause (docs/operations/dead-letter-replay.md)."
  namespace           = "AWS/AmazonMQ"
  metric_name         = "MessageCount"
  dimensions          = { Broker = aws_mq_broker.main.broker_name, VirtualHost = local.broker_vhost, Queue = "${each.key}.dlq" }
  statistic           = "Maximum"
  period              = 300
  evaluation_periods  = 1
  threshold           = 0
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"
  alarm_actions       = local.alarm_actions
  ok_actions          = local.alarm_actions
}
