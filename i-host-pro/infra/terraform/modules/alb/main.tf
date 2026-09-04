# CP5.3D-A Decision Gate: ALB access logs bucket. Operational retention
# only (item 45 - never conflated with any LGPD personal-data retention
# policy, which this bucket has nothing to do with: ALB access logs contain
# request metadata, not application/personal data).
resource "aws_s3_bucket" "alb_access_logs" {
  count  = var.access_logs_enabled ? 1 : 0
  bucket = "ihostpro-${var.environment}-alb-access-logs-${data.aws_caller_identity.current.account_id}"

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

data "aws_caller_identity" "current" {}

resource "aws_s3_bucket_public_access_block" "alb_access_logs" {
  count  = var.access_logs_enabled ? 1 : 0
  bucket = aws_s3_bucket.alb_access_logs[0].id

  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_server_side_encryption_configuration" "alb_access_logs" {
  count  = var.access_logs_enabled ? 1 : 0
  bucket = aws_s3_bucket.alb_access_logs[0].id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

# Operational retention (pilot baseline) - never LGPD personal-data
# retention. Reasonable default for a pilot with no real traffic yet;
# revisit before ProductionReady.
resource "aws_s3_bucket_lifecycle_configuration" "alb_access_logs" {
  count  = var.access_logs_enabled ? 1 : 0
  bucket = aws_s3_bucket.alb_access_logs[0].id

  rule {
    id     = "operational-retention"
    status = "Enabled"

    filter {}

    expiration {
      days = 90
    }
  }
}

# ELB's own regional service account needs PutObject to deliver access
# logs - the exact, documented AWS mechanism (not IAM, a bucket policy
# naming ELB's account principal for this region).
data "aws_elb_service_account" "main" {}

data "aws_iam_policy_document" "alb_access_logs" {
  count = var.access_logs_enabled ? 1 : 0

  statement {
    sid       = "AllowElbLogDelivery"
    effect    = "Allow"
    actions   = ["s3:PutObject"]
    resources = ["${aws_s3_bucket.alb_access_logs[0].arn}/*"]

    principals {
      type        = "AWS"
      identifiers = [data.aws_elb_service_account.main.arn]
    }
  }
}

resource "aws_s3_bucket_policy" "alb_access_logs" {
  count  = var.access_logs_enabled ? 1 : 0
  bucket = aws_s3_bucket.alb_access_logs[0].id
  policy = data.aws_iam_policy_document.alb_access_logs[0].json
}

resource "aws_lb" "this" {
  name               = "ihostpro-${var.environment}"
  internal           = false
  load_balancer_type = "application"
  security_groups    = [var.security_group_id]
  subnets            = var.public_subnet_ids

  dynamic "access_logs" {
    for_each = var.access_logs_enabled ? [1] : []
    content {
      bucket  = aws_s3_bucket.alb_access_logs[0].id
      enabled = true
    }
  }

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

# CP5.3D-A Decision Gate item 25-27: /health/live (confirmed in both Api's
# and Worker's real Program.cs - always healthy, no dependency checks),
# never /health/ready (which can legitimately report Degraded when Redis is
# down without the Api itself being unhealthy - CP2's own
# RedisDownCorePolicyFlowWorks=true finding). Matcher is exactly 200, never
# a masking range.
resource "aws_lb_target_group" "api" {
  name        = "ihostpro-${var.environment}-api"
  port        = 8080
  protocol    = "HTTP"
  vpc_id      = var.vpc_id
  target_type = "ip" # Fargate awsvpc mode has no instance id to register

  health_check {
    enabled             = true
    path                = "/health/live"
    port                = "traffic-port"
    protocol            = "HTTP"
    matcher             = "200"
    interval            = 30
    timeout             = 5
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }

  # Reasonable pilot value (single task, desired_count=1) - long enough for
  # in-flight requests to drain, short enough not to stall a deploy
  # noticeably at this scale.
  deregistration_delay = 30

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

# HTTPS listener - never a fake/self-signed certificate. This module is
# only instantiated once BaseDomain/ACM are decided (caller-side
# enable_runtime_edge), so unconditional here - no per-listener count.
resource "aws_lb_listener" "https" {
  load_balancer_arn = aws_lb.this.arn
  port              = 443
  protocol          = "HTTPS"
  ssl_policy        = "ELBSecurityPolicy-TLS13-1-2-2021-06"
  certificate_arn   = var.certificate_arn

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.api.arn
  }
}

# HTTP listener - redirect-only, never a direct forward to the target group
# (item 56: HTTP is never the final operational endpoint) - created
# alongside HTTPS, same unconditional lifecycle.
resource "aws_lb_listener" "http_redirect" {
  load_balancer_arn = aws_lb.this.arn
  port              = 80
  protocol          = "HTTP"

  default_action {
    type = "redirect"
    redirect {
      port        = "443"
      protocol    = "HTTPS"
      status_code = "HTTP_301"
    }
  }
}
