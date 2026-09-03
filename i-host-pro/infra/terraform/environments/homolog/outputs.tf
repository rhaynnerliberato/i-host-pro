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
