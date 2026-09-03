output "amqps_endpoint" {
  description = "The single AMQPS endpoint (SINGLE_INSTANCE has exactly one)."
  value       = [for e in aws_mq_broker.this.instances[0].endpoints : e if startswith(e, "amqps://")][0]
}

output "security_group_id" {
  value = aws_security_group.this.id
}
