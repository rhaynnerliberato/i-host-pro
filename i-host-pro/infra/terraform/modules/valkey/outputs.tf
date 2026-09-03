output "primary_endpoint_address" {
  value = aws_elasticache_replication_group.this.primary_endpoint_address
}

output "port" {
  description = "aws_elasticache_replication_group has no computed port attribute - this is the fixed Redis/Valkey default, never overridden by this module."
  value       = 6379
}

output "security_group_id" {
  value = aws_security_group.this.id
}
