output "cluster_arn" {
  value = aws_ecs_cluster.this.arn
}

output "cluster_name" {
  value = aws_ecs_cluster.this.name
}

output "database_bootstrap_task_definition_arn" {
  value = aws_ecs_task_definition.database_bootstrap.arn
}

output "migrationrunner_task_definition_arn" {
  value = aws_ecs_task_definition.migrationrunner.arn
}

output "rabbitmq_rotation_task_definition_arn" {
  value = aws_ecs_task_definition.rabbitmq_rotation.arn
}

output "tenant_provisioning_task_definition_arn" {
  value = aws_ecs_task_definition.tenant_provisioning.arn
}
