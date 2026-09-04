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

output "amazon_mq_hostname" {
  value = module.amazon_mq.amqps_hostname
}

output "amazon_mq_security_group_id" {
  value = module.amazon_mq.security_group_id
}

output "ecs_cluster_name" {
  value = module.ecs.cluster_name
}

output "database_bootstrap_task_definition_arn" {
  value = module.ecs.database_bootstrap_task_definition_arn
}

output "migrationrunner_task_definition_arn" {
  value = module.ecs.migrationrunner_task_definition_arn
}

output "database_bootstrap_security_group_id" {
  value = module.network.database_bootstrap_security_group_id
}

output "rabbitmq_rotation_task_definition_arn" {
  value = module.ecs.rabbitmq_rotation_task_definition_arn
}

output "rabbitmq_rotation_security_group_id" {
  value = module.network.rabbitmq_rotation_security_group_id
}

# CP5.3D-B item 5/6: once applied, these are the exact 4 nameservers the
# user must set at Registro.br in place of a.auto.dns.br/b.auto.dns.br -
# the only manual step left in the whole domain/ACM/ALB flow.
output "route53_name_servers" {
  value = var.enable_runtime_edge ? module.route53[0].name_servers : null
}

output "acm_certificate_arn" {
  value = var.enable_runtime_edge ? module.acm_certificate[0].certificate_arn : null
}

output "api_homolog_fqdn" {
  value = var.enable_runtime_edge ? "api.homolog.${var.base_domain}" : null
}
