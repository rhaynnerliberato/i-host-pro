output "alb_arn" {
  value = aws_lb.this.arn
}

output "alb_dns_name" {
  value = aws_lb.this.dns_name
}

# CP5.3D-B: needed by the Route53 alias record (A/AAAA alias to an ALB
# requires the ALB's own hosted zone id, distinct from the domain's zone).
output "alb_zone_id" {
  value = aws_lb.this.zone_id
}

output "target_group_arn" {
  value = aws_lb_target_group.api.arn
}

# CP5.3E (Observability Architecture) - CloudWatch metric dimensions for
# AWS/ApplicationELB use the short "arn_suffix" form, never the full ARN.
output "alb_arn_suffix" {
  value = aws_lb.this.arn_suffix
}

output "target_group_arn_suffix" {
  value = aws_lb_target_group.api.arn_suffix
}
