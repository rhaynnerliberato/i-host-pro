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

output "migrationrunner_security_group_id" {
  value = module.network.migrationrunner_security_group_id
}

output "rds_endpoint" {
  value = module.rds.endpoint
}

output "rds_port" {
  value = module.rds.port
}

output "rds_security_group_id" {
  value = module.rds.security_group_id
}

output "rds_master_user_secret_arn" {
  description = "AWS-managed master credential secret ARN (bootstrap-only)."
  value       = module.rds.master_user_secret_arn
}

output "valkey_primary_endpoint_address" {
  value = module.valkey.primary_endpoint_address
}

output "valkey_port" {
  value = module.valkey.port
}

output "valkey_security_group_id" {
  value = module.valkey.security_group_id
}
