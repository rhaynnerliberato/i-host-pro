output "alb_arn" {
  value = aws_lb.this.arn
}

output "alb_dns_name" {
  value = aws_lb.this.dns_name
}

output "target_group_arn" {
  value = aws_lb_target_group.api.arn
}

output "https_listener_created" {
  description = "Whether the HTTPS listener actually exists yet (depends on certificate_arn being supplied)."
  value       = var.certificate_arn != ""
}
