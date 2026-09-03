output "oidc_provider_arn" {
  value = module.github_oidc.oidc_provider_arn
}

output "deploy_role_arns" {
  value = module.github_oidc.deploy_role_arns
}

output "budget_sns_topic_arn" {
  value = module.budget.sns_topic_arn
}
