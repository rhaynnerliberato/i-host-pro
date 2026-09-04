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
