output "execution_role_arn" {
  value = aws_iam_role.execution.arn
}

output "api_task_role_arn" {
  value = aws_iam_role.api_task.arn
}

output "worker_task_role_arn" {
  value = aws_iam_role.worker_task.arn
}

output "migrationrunner_task_role_arn" {
  value = aws_iam_role.migrationrunner_task.arn
}

output "database_bootstrap_task_role_arn" {
  value = aws_iam_role.database_bootstrap_task.arn
}

output "rabbitmq_rotation_task_role_arn" {
  value = aws_iam_role.rabbitmq_rotation_task.arn
}
