resource "aws_sns_topic" "budget_alerts" {
  name = "ihostpro-budget-alerts"

  tags = {
    Project   = var.project
    ManagedBy = "Terraform"
  }
}

data "aws_iam_policy_document" "budget_alerts_publish" {
  statement {
    effect    = "Allow"
    actions   = ["SNS:Publish"]
    resources = [aws_sns_topic.budget_alerts.arn]

    principals {
      type        = "Service"
      identifiers = ["budgets.amazonaws.com"]
    }
  }
}

resource "aws_sns_topic_policy" "budget_alerts" {
  arn    = aws_sns_topic.budget_alerts.arn
  policy = data.aws_iam_policy_document.budget_alerts_publish.json
}

resource "aws_sns_topic_subscription" "email" {
  topic_arn = aws_sns_topic.budget_alerts.arn
  protocol  = "email"
  endpoint  = var.alert_email
}

# limit_amount is the CRITICAL threshold — the WARNING threshold is expressed
# as a percentage of it in the first notification block. Both are alert
# thresholds only; this resource never restricts or halts spend.
resource "aws_budgets_budget" "monthly" {
  name         = "ihostpro-monthly-cost"
  budget_type  = "COST"
  limit_amount = tostring(var.critical_threshold_usd)
  limit_unit   = "USD"
  time_unit    = "MONTHLY"

  notification {
    comparison_operator       = "GREATER_THAN"
    threshold                 = (var.warning_threshold_usd / var.critical_threshold_usd) * 100
    threshold_type            = "PERCENTAGE"
    notification_type         = "ACTUAL"
    subscriber_sns_topic_arns = [aws_sns_topic.budget_alerts.arn]
  }

  notification {
    comparison_operator       = "GREATER_THAN"
    threshold                 = 100
    threshold_type            = "PERCENTAGE"
    notification_type         = "ACTUAL"
    subscriber_sns_topic_arns = [aws_sns_topic.budget_alerts.arn]
  }
}
