# CP6 Plan A (Frontend Hosting): private S3 origin + CloudFront, the same
# "no public bucket access, CDN is the only reader" shape already proven by
# modules/alb's access-log bucket in this codebase - here the bucket holds
# the compiled Angular SPA instead of log objects. Account-id suffix on the
# bucket name for the same reason as alb_access_logs: S3 bucket names are
# unique across ALL of AWS, not just this account.
data "aws_caller_identity" "current" {}

resource "aws_s3_bucket" "frontend" {
  bucket = "ihostpro-${var.environment}-frontend-${data.aws_caller_identity.current.account_id}"

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

resource "aws_s3_bucket_public_access_block" "frontend" {
  bucket = aws_s3_bucket.frontend.id

  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_server_side_encryption_configuration" "frontend" {
  bucket = aws_s3_bucket.frontend.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

# CP6 Plan A RollbackModel: every publish also lands under releases/<git-sha>/
# in this same bucket, alongside the live root CloudFront actually serves -
# operational retention only (mirrors alb_access_logs' own 90-day rationale),
# never an LGPD personal-data policy (this bucket holds no personal data).
resource "aws_s3_bucket_lifecycle_configuration" "frontend_releases_retention" {
  bucket = aws_s3_bucket.frontend.id

  rule {
    id     = "release-history-retention"
    status = "Enabled"

    filter {
      prefix = "releases/"
    }

    expiration {
      days = 90
    }
  }
}

resource "aws_cloudfront_origin_access_control" "frontend" {
  name                              = "ihostpro-${var.environment}-frontend"
  origin_access_control_origin_type = "s3"
  signing_behavior                  = "always"
  signing_protocol                  = "sigv4"
}

# CP6 Plan A: managed cache policies looked up by name rather than a
# hardcoded UUID literal - avoids relying on a memorized policy ID.
data "aws_cloudfront_cache_policy" "caching_optimized" {
  name = "Managed-CachingOptimized"
}

data "aws_cloudfront_cache_policy" "caching_disabled" {
  name = "Managed-CachingDisabled"
}

resource "aws_cloudfront_distribution" "frontend" {
  enabled             = true
  is_ipv6_enabled     = true
  default_root_object = "index.html"
  aliases             = [var.domain_name]
  price_class         = var.price_class
  comment             = "ihostpro-${var.environment}-frontend"

  origin {
    domain_name              = aws_s3_bucket.frontend.bucket_regional_domain_name
    origin_id                = "s3-frontend"
    origin_access_control_id = aws_cloudfront_origin_access_control.frontend.id
  }

  # CP6 Plan A cache strategy: hashed JS/CSS/assets (everything not matched
  # by the two ordered behaviors below) get the long-lived managed
  # CachingOptimized policy - safe because Angular's outputHashing:"all"
  # gives every such file a new filename on every build.
  default_cache_behavior {
    allowed_methods        = ["GET", "HEAD"]
    cached_methods         = ["GET", "HEAD"]
    target_origin_id       = "s3-frontend"
    viewer_protocol_policy = "redirect-to-https"
    compress               = true
    cache_policy_id        = data.aws_cloudfront_cache_policy.caching_optimized.id
  }

  # index.html and config.json must never be stuck behind a long CloudFront
  # cache - a new deploy (index.html) or a runtime config change (config.json,
  # RuntimeConfigService's own fetch target) must be visible immediately.
  ordered_cache_behavior {
    path_pattern           = "/index.html"
    allowed_methods        = ["GET", "HEAD"]
    cached_methods         = ["GET", "HEAD"]
    target_origin_id       = "s3-frontend"
    viewer_protocol_policy = "redirect-to-https"
    compress               = true
    cache_policy_id        = data.aws_cloudfront_cache_policy.caching_disabled.id
  }

  ordered_cache_behavior {
    path_pattern           = "/config.json"
    allowed_methods        = ["GET", "HEAD"]
    cached_methods         = ["GET", "HEAD"]
    target_origin_id       = "s3-frontend"
    viewer_protocol_policy = "redirect-to-https"
    compress               = true
    cache_policy_id        = data.aws_cloudfront_cache_policy.caching_disabled.id
  }

  # SPA fallback (Angular Router / HTML5 pushState): any path with no
  # matching S3 object - e.g. a deep link or a hard refresh on
  # /reservations/123 - returns a real 403 (private bucket, no direct
  # object) or 404 (no such key) from S3; both are rewritten to index.html
  # with a 200 so the Router can take over client-side.
  custom_error_response {
    error_code         = 403
    response_code      = 200
    response_page_path = "/index.html"
  }

  custom_error_response {
    error_code         = 404
    response_code      = 200
    response_page_path = "/index.html"
  }

  restrictions {
    geo_restriction {
      restriction_type = "none"
    }
  }

  viewer_certificate {
    acm_certificate_arn      = var.certificate_arn
    ssl_support_method       = "sni-only"
    minimum_protocol_version = "TLSv1.2_2021"
  }

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

# Only this one distribution may ever read the bucket - no other principal,
# no public access (aws_s3_bucket_public_access_block above blocks that
# entirely regardless of this policy).
data "aws_iam_policy_document" "frontend_bucket" {
  statement {
    sid       = "AllowCloudFrontServicePrincipalReadOnly"
    effect    = "Allow"
    actions   = ["s3:GetObject"]
    resources = ["${aws_s3_bucket.frontend.arn}/*"]

    principals {
      type        = "Service"
      identifiers = ["cloudfront.amazonaws.com"]
    }

    condition {
      test     = "StringEquals"
      variable = "AWS:SourceArn"
      values   = [aws_cloudfront_distribution.frontend.arn]
    }
  }
}

resource "aws_s3_bucket_policy" "frontend" {
  bucket = aws_s3_bucket.frontend.id
  policy = data.aws_iam_policy_document.frontend_bucket.json
}
