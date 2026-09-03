output "vpc_id" {
  value = aws_vpc.this.id
}

output "public_subnet_ids" {
  value = aws_subnet.public[*].id
}

output "private_subnet_ids" {
  value = aws_subnet.private[*].id
}

output "alb_security_group_id" {
  value = aws_security_group.alb.id
}

output "api_security_group_id" {
  value = aws_security_group.api.id
}

output "worker_security_group_id" {
  value = aws_security_group.worker.id
}

output "migrationrunner_security_group_id" {
  value = aws_security_group.migrationrunner.id
}

output "database_bootstrap_security_group_id" {
  value = aws_security_group.database_bootstrap.id
}

output "rabbitmq_rotation_security_group_id" {
  value = aws_security_group.rabbitmq_rotation.id
}
