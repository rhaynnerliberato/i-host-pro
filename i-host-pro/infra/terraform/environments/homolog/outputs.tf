output "vpc_id" {
  value = module.network.vpc_id
}

output "public_subnet_ids" {
  value = module.network.public_subnet_ids
}

output "private_subnet_ids" {
  value = module.network.private_subnet_ids
}

output "alb_security_group_id" {
  value = module.network.alb_security_group_id
}

output "api_security_group_id" {
  value = module.network.api_security_group_id
}

output "worker_security_group_id" {
  value = module.network.worker_security_group_id
}

output "ecr_repository_urls" {
  value = module.ecr.repository_urls
}

output "secret_arns" {
  value = module.credentials.secret_arns
}

output "ecs_execution_role_arn" {
  value = module.ecs_iam.execution_role_arn
}

output "api_task_role_arn" {
  value = module.ecs_iam.api_task_role_arn
}

output "worker_task_role_arn" {
  value = module.ecs_iam.worker_task_role_arn
}

output "migrationrunner_task_role_arn" {
  value = module.ecs_iam.migrationrunner_task_role_arn
}
